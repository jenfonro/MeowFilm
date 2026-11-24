#if os(iOS) || os(macOS)
import Foundation
import MeowContracts
import MeowPlayback
import MeowStorage

@MainActor
extension MusicViewModel {
    func clearRecommendationSnapshots() {
        recommendations.recommendationReadID = UUID()
        recommendations.recommendationPreviewReadID = nil
        recommendations.recommendationQueueRevision = nil; recommendations.recommendationAccount = nil
        playbackState.similarMode = nil; playbackState.queueRecommendations = nil
        playbackState.canEnterSimilarMode = false; playbackState.canEnterSongRecommendations = false
        playbackState.fmPlayback = nil
    }
    func cancelRecommendationOperation() {
        recommendations.recommendationCommandID = UUID()
        recommendations.recommendationReturnID = nil
        recommendations.recommendationIntent?.revoke(); recommendations.recommendationIntent = nil
        recommendations.recommendationTask?.cancel(); recommendations.recommendationTask = nil
        recommendations.recommendationPlaybackIntent?.revoke(); recommendations.recommendationPlaybackIntent = nil
        playbackState.isChangingSimilarMode = false
        playbackState.isChangingQueueRecommendations = false
    }
    func stopSimilarObservation() {
        recommendations.similarObservationID = UUID()
        recommendations.similarObservation?.cancel(); recommendations.similarObservation = nil
        cancelRecommendationOperation()
        cancelQueueRecommendationPreview()
        clearRecommendationSnapshots()
    }
    func observeSimilarMode() {
        guard recommendations.similarObservation == nil, !isClosed,
              let provider = service as? any MusicSimilarModeServing else { return }
        let own = lifecycle, id = UUID(); recommendations.similarObservationID = id
        recommendations.similarObservation = Task { [weak self] in
            let events = await provider.similarModeChanges()
            for await _ in events {
                guard let self, !Task.isCancelled, self.lifecycle == own,
                      self.recommendations.similarObservationID == id else { return }
                await self.loadRecommendationState()
            }
        }
    }
    func requestRecommendationRefresh() {
        guard service is any MusicSimilarModeServing || service is any MusicQueueRecommendationServing else { return }
        let own = lifecycle, account = searchAccessGeneration, read = recommendations.recommendationReadID
        Task { [weak self] in
            guard let self, self.lifecycle == own, self.searchAccessGeneration == account,
                  self.recommendations.recommendationReadID == read else { return }
            await self.loadRecommendationState()
        }
    }
    public func loadSimilarMode(preview: Bool = false) async {
        await loadRecommendationState(previewSimilar: preview)
    }
    public func previewQueueRecommendations() async {
        await loadRecommendationState(previewQueue: true)
    }
    public func cancelQueueRecommendationPreview() {
        recommendations.recommendationReadID = UUID()
        recommendations.recommendationPreviewReadID = nil
        guard let provider = service as? any MusicQueueRecommendationServing else { return }
        let previous = recommendations.queuePreviewCancellation
        recommendations.queuePreviewCancellation = Task {
            await previous?.value
            await provider.cancelQueueRecommendationPreview()
        }
    }
    /// Read the queue and both recommendation projections as one fenced view.
    /// Events refresh rows; they never select, resolve or restart playback.
    func loadRecommendationState(previewSimilar: Bool = false, previewQueue: Bool = false,
                                 refreshQueue: Bool = true) async {
        guard !isClosed, let service,
              !isChangingQueue, !isNavigating, !isChangingSimilarMode, !isChangingQueueRecommendations else { return }
        let similar = service as? any MusicSimilarModeServing
        let ordinary = service as? any MusicQueueRecommendationServing
        guard similar != nil || ordinary != nil else { return }
        let preview = previewSimilar || previewQueue
        // Stream refreshes cannot invalidate an explicit preview before it has
        // even submitted its candidate request.
        guard preview || recommendations.recommendationPreviewReadID != recommendations.recommendationReadID else { return }
        observeSimilarMode()
        let own = lifecycle, account = searchAccessGeneration, read = UUID()
        let revision = state.revision, selection = selectionID
        recommendations.recommendationReadID = read
        if preview { recommendations.recommendationPreviewReadID = read }
        defer {
            if recommendations.recommendationPreviewReadID == read { recommendations.recommendationPreviewReadID = nil }
        }
        func admitted() throws {
            try check(own)
            guard recommendations.recommendationReadID == read, searchAccessGeneration == account,
                  state.revision == revision, selectionID == selection,
                  !isChangingQueue, !isNavigating, !isChangingSimilarMode, !isChangingQueueRecommendations
            else { throw MusicServiceError.cancelled }
        }
        do {
            let before = try await service.state()
            try admitted()
            if previewSimilar { recommendations.recommendationPreviewReadID = nil }
            let mode = try await (previewSimilar ? similar?.previewSimilarMode() : similar?.similarModeSnapshot())
            try admitted()
            let canEnter = try await similar?.canEnterSimilarMode() ?? false
            try admitted()
            let canRecommend = try await similar?.canEnterSongRecommendations() ?? false
            try admitted()
            let radio = try await (service as? any MusicFMPlaybackServing)?.fmPlaybackSnapshot()
            try admitted()
            let batch: MusicQueueRecommendationSnapshot?
            if previewQueue, let ordinary {
                // Closing a previous sheet owns its cancel/drain. A new sheet
                // cannot start a preview that the old global cancel could hit.
                await recommendations.queuePreviewCancellation?.value
                try admitted()
                recommendations.recommendationPreviewReadID = nil
                batch = try await ordinary.previewQueueRecommendations()
            } else { batch = try await ordinary?.queueRecommendationSnapshot() }
            try admitted()
            let next = try await service.state(), rows = try await service.queue()
            try admitted()
            guard before.revision == next.revision, before.settingsRevision == next.settingsRevision,
                  before.currentId == next.currentId,
                  next.revision == rows.revision, next.currentId == rows.currentId,
                  next.revision >= state.revision, next.settingsRevision >= state.settingsRevision else { return }
            if refreshQueue {
                if next.sourceCollectionID != state.sourceCollectionID || rows.entries.isEmpty { notices.queueNotice = nil }
                playbackState.state = next; playbackState.queue = rows.entries
                pruneRetainedFMSources()
                if candidate == nil { position = next.position }
            } else if next.revision != state.revision || next.currentId != state.currentId {
                return
            } else if rows.entries != playbackState.queue {
                playbackState.queue = rows.entries
            }
            playbackState.similarMode = mode
            playbackState.canEnterSimilarMode = canEnter
            playbackState.canEnterSongRecommendations = canRecommend
            playbackState.fmPlayback = radio
            if let radio {
                playbackState.isFMActive = true
                if recommendations.fmSource == nil || playbackState.activeHomeFM == nil {
                    playbackState.activeHomeFM = radio.action
                }
            } else if recommendations.fmSource == nil {
                playbackState.isFMActive = false
                playbackState.activeHomeFM = nil
            }
            recommendations.recommendationQueueRevision = next.revision; recommendations.recommendationAccount = account
            playbackState.queueRecommendations = mode?.active != true && !isFMActive &&
                batch?.currentEntryID == next.currentId ? batch : nil
            let failureID = mode?.errorID ?? batch?.errorID ?? radio?.errorID
            if let failureID, failureID != recommendations.lastRecommendationFailure,
               let failure = mode?.error ?? batch?.error ?? radio?.error {
                recommendations.lastRecommendationFailure = failureID
                report(failure, epoch: own, operation: "读取后续歌曲")
            }
        } catch {
            do { try admitted() } catch { return }
            let failure = MusicServiceError.sanitized(error)
            if failure.isAuthenticationFailure || [.accessUnavailable, .staleAccess, .unsupported].contains(failure) {
                clearRecommendationSnapshots()
            }
            if previewQueue || previewSimilar { report(error, epoch: own, operation: "读取推荐歌曲") }
        }
    }

    /// Service cancellation and audio admission have separate lifetimes: pause
    /// keeps a committed batch, while revoking every late route to the player.
    func performRecommendation(ordinary: Bool = false,
        _ operation: @escaping @MainActor () async throws -> MusicSimilarModeResult
    ) async throws {
        let own = try enter()
        guard !isChangingSimilarMode, !isChangingQueueRecommendations, !isChangingQueue, !isNavigating else {
            throw MusicServiceError.busy
        }
        let account = searchAccessGeneration, selection = selectionID, command = UUID()
        let intent = MusicQueueAuthorization(), audio = MusicQueueAuthorization()
        let control = playbackControlRevision
        recommendations.recommendationPlaybackIntent?.revoke()
        recommendations.recommendationCommandID = command
        recommendations.recommendationIntent = intent
        recommendations.recommendationPlaybackIntent = audio
        recommendations.recommendationReadID = UUID()
        if ordinary { playbackState.isChangingQueueRecommendations = true }
        else { playbackState.isChangingSimilarMode = true }
        defer {
            intent.revoke(); audio.revoke()
            if recommendations.recommendationCommandID == command {
                recommendations.recommendationTask = nil
                recommendations.recommendationIntent = nil
                recommendations.recommendationPlaybackIntent = nil
                playbackState.isChangingSimilarMode = false; playbackState.isChangingQueueRecommendations = false
            }
        }
        func admitted() throws {
            try check(own)
            guard intent.isActive, searchAccessGeneration == account,
                  recommendations.recommendationCommandID == command else { throw MusicServiceError.cancelled }
        }
        cancelRemoteQueuePreparation(); cancelFMNavigation()
        await flushPlaybackFacts()
        try admitted()
        guard selectionID == selection else { throw MusicServiceError.cancelled }
        let task = Task {
            try Task.checkCancellation()
            return try await operation()
        }
        recommendations.recommendationTask = task
        do {
            let result = try await withTaskCancellationHandler {
                try await task.value
            } onCancel: { intent.revoke(); audio.revoke(); task.cancel() }
            try admitted()
            guard selectionID == selection,
                  result.state.revision >= state.revision,
                  result.state.settingsRevision >= state.settingsRevision else { throw MusicServiceError.cancelled }
            stopFM()
            switch result.action {
            case .playCurrent, .restoreOnly: invalidateSelection(stopMedia: true, reason: .ui, preservingQueueIntent: intent)
            case .keepCurrent, .togglePlayback: break
            }
            playbackState.state = result.state; notices.queueNotice = nil
            try await refreshQueue(try requireService(), epoch: own, selection: selectionID)
            try admitted()
            switch result.action {
            case .playCurrent:
                if audio.isActive, playbackControlRevision == control {
                    await prepareCurrent(forceSelection: false, automatic: false,
                                         admission: QueuePlaybackAdmission(source: nil, intent: audio))
                } else { playbackState.phase = .paused }
            case .togglePlayback:
                if audio.isActive, playbackControlRevision == control {
                    playbackState.isChangingSimilarMode = false; playbackState.isChangingQueueRecommendations = false
                    await togglePlayback()
                }
            case .keepCurrent, .restoreOnly: break
            }
            try admitted()
        } catch {
            try admitted()
            throw error
        }
    }
    func performSimilarMode(
        _ operation: @escaping @MainActor (any MusicSimilarModeServing) async throws -> MusicSimilarModeResult
    ) async throws {
        guard let provider = service as? any MusicSimilarModeServing else { throw MusicServiceError.unsupported }
        let own = lifecycle, account = searchAccessGeneration
        do { try await performRecommendation { try await operation(provider) } }
        catch {
            if lifecycle == own, searchAccessGeneration == account, !Task.isCancelled { await loadRecommendationState() }
            throw error
        }
        if lifecycle == own, searchAccessGeneration == account, !Task.isCancelled { await loadRecommendationState() }
    }
    public func startHeartbeat() async {
        let own = lifecycle, account = searchAccessGeneration
        do { try await playHomeAction(.heartbeat, title: "心动模式") }
        catch {
            if searchAccessGeneration == account, !Task.isCancelled { report(error, epoch: own, operation: "开启心动模式") }
        }
    }
    public func startSongRecommendations(occurrenceID: String, expectedRevision: Int) async {
        let own = lifecycle, account = searchAccessGeneration
        guard current?.id == occurrenceID, state.revision == expectedRevision else { return }
        do {
            try await performSimilarMode {
                try await $0.startSimilarMode(.recommendations(occurrenceID: occurrenceID, expectedRevision: expectedRevision))
            }
        } catch {
            if searchAccessGeneration == account, !Task.isCancelled { report(error, epoch: own, operation: "开启单曲推荐") }
        }
    }
    public func setFMContinue(_ enabled: Bool, sessionID: UUID) async {
        let own = lifecycle, account = searchAccessGeneration
        guard !isClosed, fmPlayback?.id == sessionID,
              !Task.isCancelled, let provider = service as? any MusicFMPlaybackServing else { return }
        let submitted = settingsReceipts.submit(settings: enabled ? ["playback/mode"] : [],
                                                operation: "保存漫游续播设置") {
            try await provider.setFMContinue(enabled, sessionID: sessionID)
        }
        do {
            try await submitted.value
            try check(own)
            guard searchAccessGeneration == account else { return }
            await loadRecommendationState()
        } catch {
            if (error as? MusicServiceError) != .commitUnconfirmed,
               searchAccessGeneration == account, !Task.isCancelled {
                report(error, epoch: own, operation: "保存漫游续播设置", kind: .settings)
            }
        }
    }
    public func startCurrentHeartbeat(occurrenceID: String, expectedRevision: Int) async {
        let own = lifecycle, account = searchAccessGeneration
        guard current?.id == occurrenceID, state.revision == expectedRevision else { return }
        do {
            try await performSimilarMode {
                try await $0.startSimilarMode(.currentHeartbeat(occurrenceID: occurrenceID, expectedRevision: expectedRevision))
            }
        } catch {
            if searchAccessGeneration == account, !Task.isCancelled { report(error, epoch: own, operation: "开启心动模式") }
        }
    }
    public func enterSimilarMode() async {
        let own = lifecycle, account = searchAccessGeneration
        do { try await performSimilarMode { try await $0.enterSimilarMode() } }
        catch { if searchAccessGeneration == account, !Task.isCancelled { report(error, epoch: own) } }
    }
    public func exitSimilarMode() async {
        // Return drains an entry still waiting for its first batch before exit
        // restores the checkpoint. It must not erase that checkpoint via cancel.
        guard recommendations.recommendationReturnID == nil else { return }
        let pending = recommendations.recommendationTask, own = lifecycle, account = searchAccessGeneration
        cancelRecommendationOperation()
        let command = recommendations.recommendationCommandID
        recommendations.recommendationReturnID = command
        defer { if recommendations.recommendationReturnID == command { recommendations.recommendationReturnID = nil } }
        playbackState.isChangingSimilarMode = true
        _ = await pending?.result
        guard lifecycle == own, searchAccessGeneration == account,
              recommendations.recommendationCommandID == command else { return }
        playbackState.isChangingSimilarMode = false
        do { try await performSimilarMode { try await $0.exitSimilarMode() } }
        catch { if searchAccessGeneration == account, !Task.isCancelled { report(error, epoch: own) } }
    }
    public func playSimilarCandidate(snapshotID: UUID, candidateID: String) async {
        let own = lifecycle, account = searchAccessGeneration
        guard similarMode?.id == snapshotID else { return }
        do { try await performSimilarMode { try await $0.playSimilarCandidate(snapshotID: snapshotID, candidateID: candidateID) } }
        catch {
            if searchAccessGeneration == account, !Task.isCancelled { report(error, epoch: own, operation: "播放相似歌曲") }
        }
    }
    public func setSimilarContinue(_ enabled: Bool, snapshotID: UUID) async {
        let own = lifecycle, account = searchAccessGeneration
        guard !isClosed, similarMode?.id == snapshotID,
              !Task.isCancelled, let provider = service as? any MusicSimilarModeServing else { return }
        let submitted = settingsReceipts.submit(
            settings: ["queue/similar/" + snapshotID.uuidString + "/continue"] + (enabled ? ["playback/mode"] : []),
            operation: "保存推荐续播设置") {
                try await provider.setSimilarContinue(enabled, snapshotID: snapshotID)
            }
        do {
            try await submitted.value
            try check(own)
            guard searchAccessGeneration == account else { return }
            await loadRecommendationState()
        } catch {
            if (error as? MusicServiceError) != .commitUnconfirmed,
               searchAccessGeneration == account, !Task.isCancelled {
                report(error, epoch: own, operation: "保存推荐续播设置", kind: .settings)
            }
        }
    }
    public func retrySimilarMode(snapshotID: UUID) async {
        let own = lifecycle, account = searchAccessGeneration
        guard similarMode?.id == snapshotID else { return }
        do { try await performSimilarMode { try await $0.retrySimilarMode(snapshotID: snapshotID) } }
        catch { if searchAccessGeneration == account, !Task.isCancelled { report(error, epoch: own) } }
    }
    public func playQueueRecommendation(snapshotID: UUID, candidateID: String) async {
        let own = lifecycle, account = searchAccessGeneration
        guard queueRecommendations?.id == snapshotID,
              let provider = service as? any MusicQueueRecommendationServing else { return }
        do {
            try await performRecommendation(ordinary: true) {
                try await provider.playQueueRecommendation(snapshotID: snapshotID, candidateID: candidateID)
            }
            await loadRecommendationState()
        } catch {
            guard searchAccessGeneration == account, !Task.isCancelled else { return }
            await loadRecommendationState()
            if searchAccessGeneration == account { report(error, epoch: own, operation: "播放推荐歌曲") }
        }
    }
    public func setQueueRecommendationContinue(_ enabled: Bool, snapshotID: UUID) async {
        let own = lifecycle, account = searchAccessGeneration
        guard !isClosed, queueRecommendations?.id == snapshotID,
              !Task.isCancelled, let provider = service as? any MusicQueueRecommendationServing else { return }
        let submitted = settingsReceipts.submit(settings: enabled ? ["playback/mode"] : [],
                                                operation: "保存推荐续播设置") {
            try await provider.setQueueRecommendationContinue(enabled, snapshotID: snapshotID)
        }
        do {
            try await submitted.value
            try check(own)
            guard searchAccessGeneration == account else { return }
            await loadRecommendationState()
        } catch {
            if (error as? MusicServiceError) != .commitUnconfirmed,
               searchAccessGeneration == account, !Task.isCancelled {
                report(error, epoch: own, operation: "保存推荐续播设置", kind: .settings)
            }
        }
    }
    public func retryQueueRecommendations(snapshotID: UUID) async {
        let own = lifecycle, account = searchAccessGeneration
        guard queueRecommendations?.id == snapshotID,
              let provider = service as? any MusicQueueRecommendationServing else { return }
        do {
            try await performRecommendation(ordinary: true) { try await provider.retryQueueRecommendations(snapshotID: snapshotID) }
            await loadRecommendationState()
        } catch {
            guard searchAccessGeneration == account, !Task.isCancelled else { return }
            await loadRecommendationState()
            if searchAccessGeneration == account { report(error, epoch: own, operation: "重试推荐歌曲") }
        }
    }
}
#endif
