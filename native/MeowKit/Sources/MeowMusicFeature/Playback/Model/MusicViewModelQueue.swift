#if os(iOS) || os(macOS)
import Foundation
import MeowContracts
import MeowPlayback
import MeowStorage

struct MusicFMArtwork: Equatable {
    let configuration: MusicHomeAction
    let url: URL?
}

@MainActor
extension MusicViewModel {
    public func play(_ track: MusicRemoteTrack) async { await queueAction(.replace([track.ref], index: 0), startPlayback: true) }
    public func performPrimaryPlayback(presentation: MusicMiniPlayerPresentation?,
                                       operation: @escaping @MainActor () async throws -> Void) async throws {
        guard let presentation else { try await operation(); return }
        let epoch = lifecycle, intent = presentation.beginHomePlayback()
        if let old = primaryPlayback { old.presentation.cancelHomePlayback(old.intent) }
        primaryPlayback = (intent, presentation, nil)
        do {
            try await MusicPrimaryPlaybackContext.$intent.withValue(intent) { try await operation() }
            try check(epoch)
            guard primaryPlayback?.intent == intent else { return }
            if primaryPlayback?.receipt == nil {
                presentation.cancelHomePlayback(intent); primaryPlayback = nil
            } else { completePrimaryPlaybackIfPlaying() }
        } catch {
            presentation.cancelHomePlayback(intent)
            if primaryPlayback?.intent == intent { primaryPlayback = nil }
            throw error
        }
    }
    func capturePrimaryPlayback() {
        guard let intent = MusicPrimaryPlaybackContext.intent,
              primaryPlayback?.intent == intent, let current, let nativeAttempt else { return }
        primaryPlayback?.receipt = .init(occurrenceID: current.id,
            nativeAttemptID: nativeAttempt.id, accessGeneration: accountAccessGeneration)
        completePrimaryPlaybackIfPlaying()
    }
    func completePrimaryPlaybackIfPlaying() {
        guard phase == .playing, let pending = primaryPlayback, let receipt = pending.receipt,
              current?.id == receipt.occurrenceID, nativeAttempt?.id == receipt.nativeAttemptID,
              accountAccessGeneration == receipt.accessGeneration else { return }
        _ = pending.presentation.finishHomePlayback(pending.intent, playback: receipt)
        primaryPlayback = nil
    }
    /// A search section/playlist preview is one queue transaction, not a series
    /// of single-song plays that replace each other or lose occurrence order.
    public func play(_ tracks: [MusicRemoteTrack], index: Int) async {
        guard !tracks.isEmpty, tracks.indices.contains(index) else {
            error = MusicServiceError.invalidRequest.localizedDescription
            return
        }
        await queueAction(.replace(tracks.map(\.ref), index: index), startPlayback: true)
    }
    public func play(_ collection: MusicCollection, index: Int) async {
        await queueAction(.collection(id: collection.id, revision: collection.revision, index: index), startPlayback: true)
    }
    public func selectOccurrence(_ id: String) async { await queueAction(.select(id), startPlayback: true) }
    public func insertNext(_ track: MusicRemoteTrack) async { await queueAction(.append([track.ref], next: true), startPlayback: false) }
    /// Inserts a selected batch as one queue transaction, preserving its order.
    public func insertNext(_ tracks: [MusicRemoteTrack]) async {
        guard !tracks.isEmpty else { return }
        await queueAction(.append(tracks.map(\.ref), next: true), startPlayback: false)
    }
    public func append(_ track: MusicRemoteTrack) async { await queueAction(.append([track.ref], next: false), startPlayback: false) }
    public func removeOccurrence(_ id: String) async {
        await queueAction(.remove(id), startPlayback: false)
    }
    public func reorderQueue(_ ids: [String]) async { await queueAction(.reorder(ids), startPlayback: false) }
    public func clearQueue() async { await queueAction(.clear, startPlayback: false) }
    /// UI commands retain the rendered occurrence order across confirmations
    /// and native moves. A later service reread must not retarget that command.
    func changeDisplayedQueue(_ change: MusicQueueChange, intent: MusicReferenceQueueIntent,
                              startPlayback: Bool = false) async {
        guard intent.admits(access: accountAccessGeneration, revision: state.revision,
                            ids: queue.map(\.id), permitted: isAvailable && !isClosed && !Task.isCancelled)
        else { return }
        await queueAction(change, startPlayback: startPlayback, expectedQueueRevision: intent.revision)
    }

    public func openAccountQueue(platform: MusicAccountPlatform, kind: MusicAccountQueueKind,
                                 title: String) async throws -> MusicRemoteQueueSource {
        let epoch = try enter()
        guard let remoteQueueService else { throw MusicServiceError.unavailable }
        let source = try await remoteQueueService.openAccountQueue(platform: platform, kind: kind, title: title)
        do { try check(epoch); return source }
        catch { source.revoke(); await source.close(); throw error }
    }
    public func openHomeActionQueue(_ action: MusicHomeAction, title: String) async throws -> MusicRemoteQueueSource {
        let epoch = try enter()
        guard let provider = service as? any MusicHomeActionQueueServing else { throw MusicServiceError.unsupported }
        let source = try await provider.openHomeActionQueue(action, title: title)
        do { try check(epoch); return source }
        catch { source.revoke(); await source.close(); throw error }
    }
    public func playHomeAction(_ requested: MusicHomeAction, title: String, forceReload: Bool = false) async throws {
        let epoch = try enter(), account = searchAccessGeneration, selection = selectionID
        let action: MusicHomeAction
        if !forceReload, case .fm(.default, "", nil) = requested,
           let owner = service as? any MusicFMPlaybackServing {
            action = try await owner.rememberedFM()
        } else { action = requested }
        try check(epoch)
        guard searchAccessGeneration == account, selectionID == selection else { throw MusicServiceError.cancelled }
        switch action {
        case .similar(let ids, let name):
            try await performSimilarMode { try await $0.startSimilarMode(.songRoaming(sourceIDs: ids, sourceName: name)) }
            return
        case .similarArtist(let ids, let name):
            try await performSimilarMode { try await $0.startSimilarMode(.artistRoaming(sourceIDs: ids, sourceName: name)) }
            return
        case .heartbeatSong(let id):
            try await performSimilarMode { try await $0.startSimilarMode(.heartbeatSong(songID: id)) }
            return
        case .heartbeat:
            try await performSimilarMode { try await $0.startSimilarMode(.heartbeat) }
            return
        default: break
        }
        if !forceReload, action.isPersonalFM, activeHomeFM?.fmConfiguration == action.fmConfiguration,
           fmSource?.authorization.isActive == true {
            await togglePlayback(); return
        }
        let source = try await openHomeActionQueue(action, title: title)
        defer { releaseRemoteQueueSource(source) }
        try check(epoch)
        guard searchAccessGeneration == account, selectionID == selection else { throw MusicServiceError.cancelled }
        let first = try await remoteQueuePage(source, cursor: nil)
        try check(epoch)
        guard searchAccessGeneration == account, selectionID == selection else { throw MusicServiceError.cancelled }
        guard let track = first.items.first else { throw MusicServiceError.notFound }
        try await prepareRemoteQueue(source, start: .init(index: 0, track: track))
        try check(epoch)
        guard searchAccessGeneration == account else { throw MusicServiceError.cancelled }
        if fmSource?.id == source.id, source.authorization.isActive { playbackState.activeHomeFM = action }
    }
    /// Only the current occurrence of this exact live radio session can supply
    /// an entrance cover; an inserted/selected song is not implicitly an FM song.
    func currentFMArtwork(_ action: MusicHomeAction) -> URL? {
        guard let artwork = fmArtwork(configuration: activeHomeFM?.fmConfiguration, current: current,
            metadataURL: metadataArtworkURL, metadataOccurrence: metadataOccurrenceID,
            ready: isFMActive && !isPreparingRemoteQueue && phase != .failed),
              action.isPersonalFM, artwork.configuration == action.fmConfiguration else { return nil }
        return artwork.url
    }
    func fmArtwork(configuration: MusicHomeAction?, current: MusicQueueEntry?,
                   metadataURL: URL?, metadataOccurrence: String?, ready: Bool) -> MusicFMArtwork? {
        guard ready, let configuration, let current, let fmSource,
              fmSource.authorization.isActive, current.remoteSessionID == fmSource.id else { return nil }
        return .init(configuration: configuration,
            url: metadataOccurrence == current.id ? metadataURL : current.track.artwork)
    }
    public func remoteQueuePage(_ source: MusicRemoteQueueSource, cursor: String?) async throws -> MusicBrowsePage {
        let epoch = try enter()
        guard let remoteQueueService else { throw MusicServiceError.unavailable }
        let page = try await remoteQueueService.remoteQueuePage(source, cursor: cursor)
        try check(epoch); try source.authorization.check(); return page
    }
    public func filteredRemoteQueue(_ source: MusicRemoteQueueSource, query: String) async throws -> MusicRemoteQueueSource {
        let epoch = try enter()
        guard let provider = service as? any MusicRemoteCollectionServing else { throw MusicServiceError.unsupported }
        let intent = MusicQueueAuthorization()
        defer { intent.revoke() }
        let filtered = try await withTaskCancellationHandler {
            try await provider.filteredRemoteQueue(source: source, query: query, intent: intent)
        } onCancel: { intent.revoke() }
        do { try check(epoch); try intent.check(); return filtered }
        catch { filtered.revoke(); await filtered.close(); throw error }
    }
    public func saveRemoteCollection(_ source: MusicRemoteQueueSource, name: String) async throws {
        let epoch = try enter()
        guard let provider = service as? any MusicRemoteCollectionServing else { throw MusicServiceError.unsupported }
        let intent = MusicQueueAuthorization()
        defer { intent.revoke() }
        _ = try await withTaskCancellationHandler {
            try await provider.saveRemoteCollection(source: source, name: name, intent: intent)
        } onCancel: { intent.revoke() }
        try check(epoch); try intent.check()
        let next = try await requireService().collections()
        try check(epoch); publishCollections(next)
    }
    /// Search owns its selected query/platform/page order. Rehydrate that exact
    /// selection once, commit once, and carry the same page intent into playback.
    public func playSearchBatch(_ batch: MusicSearchBatchRequest) async {
        await playPreparedQueue { service, revision, intent in
            try batch.validate()
            guard let provider = service as? any MusicSearchBatchPlaying else { throw MusicServiceError.unsupported }
            return try await provider.replaceSearchBatch(batch, expectedRevision: revision, intent: intent)
        }
    }
    public func playDisplayed(_ tracks: [MusicRemoteTrack], index: Int,
                              origin: MusicDisplayedQueueOrigin) async {
        await playPreparedQueue { service, revision, intent in
            guard let provider = service as? any MusicDisplayedQueuePlaying else { throw MusicServiceError.unsupported }
            return try await provider.replaceDisplayedQueue(tracks, index: index, origin: origin,
                expectedRevision: revision, intent: intent)
        }
    }
    public func playHistorySong(_ row: MusicPlayedSong) async {
        await playDisplayed([row.track], index: 0, origin: .history(date: row.date))
    }
    func playPreparedQueue(_ prepare: @escaping @Sendable
        (any MusicServing, Int, MusicQueueAuthorization) async throws -> MusicPlaybackState) async {
        cancelRemoteQueuePreparation(); cancelFMNavigation()
        let own: UUID
        do { own = try enter() } catch { return }
        guard !isChangingQueue, !isNavigating, !isChangingSimilarMode, !isChangingQueueRecommendations,
              let service, player != nil else {
            report(MusicServiceError.busy, epoch: own); return
        }
        let request = UUID(), intent = MusicQueueAuthorization(), selection = selectionID
        remoteQueueRequest = request; remoteQueueIntent = intent
        playbackState.isPreparingRemoteQueue = true; playbackState.isChangingQueue = true
        defer {
            intent.revoke()
            if remoteQueueRequest == request {
                remoteQueueTask = nil; remoteQueueIntent = nil; remoteQueueSourceID = nil
                playbackState.isPreparingRemoteQueue = false; playbackState.isChangingQueue = false
                requestRecommendationRefresh()
            }
        }
        do {
            await (service as? any MusicSimilarModeServing)?.pauseSimilarNavigation()
            await flushPlaybackFacts(); try check(own); try intent.check()
            let before = try await service.state()
            try check(own); try intent.check()
            guard selection == selectionID, request == remoteQueueRequest else { throw MusicServiceError.staleResult }
            let task = Task {
                try await prepare(service, before.revision, intent)
            }
            remoteQueueTask = task
            let next = try await withTaskCancellationHandler { try await task.value } onCancel: {
                intent.revoke(); task.cancel()
            }
            try check(own); try intent.check()
            guard selection == selectionID, request == remoteQueueRequest else { throw MusicServiceError.staleResult }
            remoteQueueTask = nil; stopFM()
            invalidateSelection(stopMedia: true, preservingQueueIntent: intent)
            playbackState.state = next; notices.queueNotice = nil
            try await refreshQueue(service, epoch: own, selection: selectionID)
            try intent.check()
            await prepareCurrent(forceSelection: false, automatic: false,
                                 admission: QueuePlaybackAdmission(source: nil, intent: intent))
        } catch {
            if remoteQueueRequest == request { report(error, epoch: own) }
        }
    }
    /// Public playlist/album consumers use the same complete-snapshot path.
    /// Only this explicit action commits a queue; page reads are side-effect free.
    public func playRemoteQueue(_ source: MusicRemoteQueueSource, start: MusicRemoteQueueStart) async {
        let epoch = lifecycle
        do { try await prepareRemoteQueue(source, start: start) }
        catch { report(error, epoch: epoch) }
    }
    func prepareRemoteQueue(_ source: MusicRemoteQueueSource, start: MusicRemoteQueueStart) async throws {
        cancelRemoteQueuePreparation(); cancelFMNavigation()
        let epoch = try enter()
        guard !isChangingQueue, !isNavigating, !isChangingSimilarMode, !isChangingQueueRecommendations,
              let service, let remoteQueueService, player != nil else {
            throw MusicServiceError.busy
        }
        let request = UUID(), intent = MusicQueueAuthorization(), selection = selectionID
        remoteQueueRequest = request; remoteQueueIntent = intent; remoteQueueSourceID = source.id
        remoteQueueIsFM = source.context.kind == .fm
        if remoteQueueIsFM { fmShouldPlay = true }
        playbackState.isPreparingRemoteQueue = true; playbackState.isChangingQueue = true
        defer {
            intent.revoke()
            if remoteQueueRequest == request {
                remoteQueueTask = nil; remoteQueueIntent = nil; remoteQueueSourceID = nil
                remoteQueueIsFM = false; playbackState.isPreparingRemoteQueue = false; playbackState.isChangingQueue = false
                requestRecommendationRefresh()
            }
        }
        do {
            await flushPlaybackFacts(); try check(epoch); try intent.check(); try source.authorization.check()
            let before = try await service.state()
            try check(epoch); try intent.check()
            guard selection == selectionID, request == remoteQueueRequest else { throw MusicServiceError.staleResult }
            let task = Task {
                try await remoteQueueService.replaceRemoteQueue(source, start: start,
                    expectedRevision: before.revision, intent: intent)
            }
            remoteQueueTask = task
            let next = try await withTaskCancellationHandler { try await task.value } onCancel: { intent.revoke(); task.cancel() }
            try check(epoch); try intent.check(); try source.authorization.check()
            guard selection == selectionID, request == remoteQueueRequest else { throw MusicServiceError.staleResult }
            // Keep this admission through queue refresh and audio resolution.
            // A committed queue is not permission for a late page callback to
            // sound after departure/cancellation/account revocation.
            remoteQueueTask = nil
            if source.context.kind == .fm { activateFM(source) } else { stopFM() }
            invalidateSelection(stopMedia: true, preservingQueueIntent: intent)
            let committedSelection = selectionID
            let admission = QueuePlaybackAdmission(source: source, intent: intent)
            playbackState.state = next; try await refreshQueue(service, epoch: epoch, selection: committedSelection)
            notices.queueNotice = source.context.availabilityNotice
            try admission.check()
            if source.context.kind != .fm || fmShouldPlay {
                await prepareCurrent(forceSelection: false, automatic: false, admission: admission)
            } else { playbackState.phase = .paused }
        } catch {
            guard remoteQueueRequest == request else { throw MusicServiceError.cancelled }
            throw error
        }
    }
    public func cancelRemoteQueuePreparation() {
        guard isPreparingRemoteQueue || remoteQueueIntent != nil || remoteQueueTask != nil || remoteQueueSourceID != nil else { return }
        let wasPreparing = isPreparingRemoteQueue
        remoteQueueIntent?.revoke(); remoteQueueIntent = nil
        remoteQueueTask?.cancel(); remoteQueueTask = nil
        remoteQueueSourceID = nil; remoteQueueIsFM = false; remoteQueueRequest = UUID()
        playbackState.isPreparingRemoteQueue = false
        if wasPreparing { playbackState.isChangingQueue = false }
    }
    /// On page departure an admitted FM transfers to the product-lifetime model.
    /// Uncommitted actions and non-FM browsing scopes are synchronously revoked.
    public func releaseRemoteQueueSource(_ source: MusicRemoteQueueSource) {
        if fmSource?.id == source.id { return }
        if remoteQueueSourceID == source.id { cancelRemoteQueuePreparation() }
        source.revoke(); Task { await source.close() }
    }
    func activateFM(_ source: MusicRemoteQueueSource) {
        if fmSource?.id == source.id { return }
        stopFM(); fmSource = source; playbackState.isFMActive = true
        let events = source.authorization.revocations()
        fmRevocationTask = Task { [weak self] in
            for await _ in events {
                guard let self, !Task.isCancelled, self.fmSource?.id == source.id else { return }
                self.cancelFMNavigation(); self.stopFM()
                self.invalidateSelection(stopMedia: true)
                if self.isAvailable, !self.isClosed { self.error = MusicServiceError.staleAccess.localizedDescription }
            }
        }
    }
    func stopFM() {
        playbackState.activeHomeFM = nil
        fmRevocationTask?.cancel(); fmRevocationTask = nil
        fmAdvanceIntent?.revoke(); fmAdvanceIntent = nil
        let source = fmSource; fmSource = nil; playbackState.isFMActive = false; playbackState.fmPlayback = nil
        if let source { source.revoke(); Task { await source.close() } }
    }
    func cancelFMNavigation() {
        guard fmAdvanceIntent != nil else { return }
        fmAdvanceIntent?.revoke(); fmAdvanceIntent = nil
        navigationTask?.cancel(); navigationTask = nil
        navigationID = UUID(); playbackState.isNavigating = false; playbackState.isChangingQueue = false
    }
    func retirePlaybackNavigation() {
        let ownsQueue = navigationQueueID == navigationID
        navigationTask?.cancel(); navigationTask = nil
        navigationID = UUID(); playbackState.isNavigating = false
        if ownsQueue { navigationQueueID = nil; playbackState.isChangingQueue = false }
        // Keep selection/fact delivery intact: an exit still owns its history,
        // but neither an old queue acknowledgement nor an ended fact may play.
    }

    public func next() async { await advance(.next, reason: .manual) }
    public func previous() async { await advance(.previous, reason: .manual) }
    func advance(_ direction: MusicDirection, reason: MusicAdvanceReason,
                         onSelection: (@MainActor (MusicQueueEntry) -> Void)? = nil) async {
        let epoch: UUID
        do { epoch = try enter() } catch { return }
        guard !isChangingQueue, !isChangingSimilarMode, !isChangingQueueRecommendations else { return }
        let account = searchAccessGeneration
        playbackState.isChangingQueue = true
        let navigation = navigationID
        navigationQueueID = navigation
        defer {
            if navigationQueueID == navigation { navigationQueueID = nil }
            if lifecycle == epoch, navigationID == navigation {
                playbackState.isChangingQueue = false
                requestRecommendationRefresh()
            }
        }
        let token = selectionID
        let fm = fmSource
        let intent = MusicQueueAuthorization()
        if fm == nil { navigationPlaybackIntent = intent }
        if fm != nil { fmAdvanceIntent = intent; fmShouldPlay = true }
        defer {
            intent.revoke()
            if navigationPlaybackIntent === intent { navigationPlaybackIntent = nil }
            if fmAdvanceIntent === intent { fmAdvanceIntent = nil }
        }
        do {
            await flushPlaybackFacts(); try check(epoch)
            guard navigationID == navigation, searchAccessGeneration == account else { return }
            let service = try requireService(), before = try await service.state()
            try check(epoch)
            guard navigationID == navigation, token == selectionID, searchAccessGeneration == account else { return }
            let result: MusicAdvanceResult
            if let fm {
                guard let remoteQueueService else { throw MusicServiceError.unavailable }
                result = try await remoteQueueService.advanceFM(fm, direction: direction, reason: reason,
                    expectedRevision: before.revision, intent: intent)
                try intent.check(); try fm.authorization.check()
                guard fmSource?.id == fm.id else { throw MusicServiceError.staleResult }
            } else {
                result = try await service.advance(direction, reason: reason, expectedRevision: before.revision)
            }
            try check(epoch)
            guard navigationID == navigation, token == selectionID, searchAccessGeneration == account else { return }
            if result.selectedId != nil { invalidateSelection(stopMedia: true, preservingQueueIntent: intent) }
            let committedSelection = selectionID
            let admission = QueuePlaybackAdmission(source: fm, intent: intent)
            playbackState.state = result.state; try await refreshQueue(service, epoch: epoch, selection: committedSelection)
            guard navigationID == navigation, searchAccessGeneration == account else { return }
            try fm?.authorization.check()
            if let selected = result.selectedId, let entry = current, entry.id == selected {
                // A visual transition receives the actual committed occurrence,
                // before URL resolution. It never selects a preview itself.
                onSelection?(entry)
            }
            if result.selectedId != nil, intent.isActive, fm == nil || fmShouldPlay {
                await prepareCurrent(forceSelection: false, automatic: reason == .ended, admission: admission)
            }
            else if result.selectedId != nil { playbackState.phase = .paused }
        } catch {
            if navigationID == navigation, searchAccessGeneration == account { report(error, epoch: epoch) }
        }
    }
    /// Synchronous admission for the product's system-command closures.
    /// True means accepted/scheduled, never "audio already played".
    public func acceptNext(_ attempt: PlayAttempt) -> Bool { acceptNavigation(attempt, direction: .next) }
    public func acceptPrevious(_ attempt: PlayAttempt) -> Bool { acceptNavigation(attempt, direction: .previous) }
    func acceptNavigation(_ attempt: PlayAttempt, direction: MusicDirection) -> Bool {
        guard attempt.owner == "music", candidate?.resourceId == attempt.resourceID,
              nativeAttempt?.id == attempt.id else { return false }
        return requestNavigation(direction)
    }
    @discardableResult public func requestNext() -> Bool { requestNavigation(.next) }
    @discardableResult public func requestPrevious() -> Bool { requestNavigation(.previous) }
    @discardableResult
    func requestNavigation(_ direction: MusicDirection, reason: MusicAdvanceReason = .manual,
                           onSelection: (@MainActor (MusicQueueEntry) -> Void)? = nil,
                           onCompletion: (@MainActor () -> Void)? = nil) -> Bool {
        guard direction == .next ? canNext : canPrevious else { return false }
        if reason == .manual { cancelPlayPauseCommands() }
        let token = UUID(); navigationID = token; playbackState.isNavigating = true
        navigationTask = Task { [weak self] in
            guard let self, !Task.isCancelled, self.navigationID == token else { return }
            await self.advance(direction, reason: reason, onSelection: onSelection)
            if self.navigationID == token {
                self.navigationTask = nil; playbackState.isNavigating = false
                self.requestRecommendationRefresh()
            }
            onCompletion?()
        }
        return true
    }

    /// Explicit play uses business automatic candidates when supported.
    /// `forceSelection` opens the existing exact-target picker instead.
}
#endif
