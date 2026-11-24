#if os(iOS) || os(macOS)
import Foundation
import Combine
import MeowContracts
import MeowDebug
import MeowPlayback
import MeowStorage
import MeowUI

/// Only time-dependent presentation observes this clock. Progress does not
/// invalidate navigation, browse catalogues, artwork owners or queue lists.
@MainActor public final class MusicPlaybackClock: ObservableObject {
    @Published public internal(set) var position: Double = 0
    @Published public internal(set) var duration: Double?
}

public struct MusicPlayerAttempt: Equatable, Sendable {
    public let id: UUID
    public let resourceID: String
    public init(id: UUID, resourceID: String) { self.id = id; self.resourceID = resourceID }
}

@MainActor final class MusicPlaybackDriver {
    let playback: PlaybackController
    init(_ playback: PlaybackController) { self.playback = playback }
    func play(_ resource: PlaybackResource) -> MusicPlayerAttempt {
        let attempt = playback.playAttempt(resource: resource, owner: "music")
        return MusicPlayerAttempt(id: attempt.id, resourceID: attempt.resourceID)
    }
    func prepareReplacement(_ resource: PlaybackResource, replacing: MusicPlayerAttempt) async throws -> MusicPreparedPlayback {
        guard let current = target(replacing) else { throw MusicServiceError.staleResult }
        let prepared = try await playback.prepareReplacement(resource: resource, replacing: current)
        return MusicPreparedPlayback(duration: prepared.duration, synchronize: { [playback] in
            try await playback.synchronizeReplacement(prepared)
        }, commit: { [playback] in
            let attempt = try playback.commitReplacement(prepared)
            return .init(id: attempt.id, resourceID: attempt.resourceID)
        }, cancel: { prepared.cancel() })
    }
    func preparePlayback(_ resource: PlaybackResource, autoplay: Bool) async throws -> MusicPreparedPlayback {
        let prepared = try await playback.preparePlayback(resource: resource, owner: "music", autoplay: autoplay)
        return MusicPreparedPlayback(duration: prepared.duration, synchronize: { [playback] in
            try await playback.synchronizeReplacement(prepared)
        }, commit: { [playback] in
            let attempt = try playback.commitReplacement(prepared)
            return .init(id: attempt.id, resourceID: attempt.resourceID)
        }, cancel: { prepared.cancel() })
    }
    func target(_ attempt: MusicPlayerAttempt) -> PlayAttempt? {
        guard let current = playback.currentAttempt, current.owner == "music",
              current.id == attempt.id, current.resourceID == attempt.resourceID else { return nil }
        return current
    }
    func pause(_ attempt: MusicPlayerAttempt) { if let current = target(attempt) { playback.pause(attempt: current) } }
    func resume(_ attempt: MusicPlayerAttempt) { if let current = target(attempt) { playback.resume(attempt: current) } }
    @discardableResult func seek(_ position: Double, attempt: MusicPlayerAttempt) -> Bool {
        guard let current = target(attempt) else { return false }
        return playback.seek(to: position, attempt: current)
    }
    func stop(_ attempt: MusicPlayerAttempt) { if let current = target(attempt) { playback.stop(attempt: current) } }
}

/// Product lifetime, not page lifetime. Products bind PlaybackEvent and system
/// navigation once and retain this model while another domain is visible.
@MainActor public final class MusicViewModel {
    #if os(iOS)
    public let artworkImages: MusicArtworkImages
    #endif
    public let searchPreferences = MusicSearchPreferencesStore()
    public let searchHistory = SearchHistoryStore()
    public let channelSelection = MusicChannelSelection()
    public func bindPreferences(database: SQLiteDatabase) async throws {
        await searchPreferences.bind(database: database)
        await searchHistory.bind(database: database)
        try await channelSelection.bind(database: database)
        guard searchPreferences.persistenceError == nil, searchHistory.persistenceError == nil else {
            throw MusicServiceError.storageFailed
        }
    }
    public let accessState = MusicAccessState()
    public let libraryState = MusicLibraryState()
    public let playbackState = MusicPlaybackFeatureState()
    public let metadataState = MusicMetadataState()
    public let settingsReceipts = MusicSettingsReceiptOwner()
    public var query = ""
    public let notices = MusicNoticeState()
    nonisolated(unsafe) private var favoritesStorage: MusicMiniPlayerFavorites?
    nonisolated(unsafe) private var favoritesObservation: AnyCancellable?
    public var favorites: MusicMiniPlayerFavorites {
        if let favoritesStorage { return favoritesStorage }
        let favorites = MusicMiniPlayerFavorites(model: self)
        favoritesStorage = favorites
        favoritesObservation = libraryState.$collections
            .map { $0.first { $0.kind == "favorites" } }
            .removeDuplicates { $0?.id == $1?.id && $0?.revision == $1?.revision }
            .receive(on: RunLoop.main)
            .sink { [weak favorites] _ in favorites?.reconcile() }
        favorites.reconcile()
        return favorites
    }
    let accountEvents = PassthroughSubject<MusicAccountPlatform, Never>()
    nonisolated(unsafe) var accountObservation: Task<Void, Never>?
    private var activeAccountLoginTransitions: Set<UUID> = []
    var isAccountLoginInProgress: Bool { !activeAccountLoginTransitions.isEmpty }
    func beginAccountLoginTransition() -> UUID {
        let token = UUID()
        activeAccountLoginTransitions.insert(token)
        return token
    }
    func endAccountLoginTransition(_ token: UUID) {
        activeAccountLoginTransitions.remove(token)
    }
    public var accountChanges: AnyPublisher<MusicAccountPlatform, Never> {
        accountEvents.eraseToAnyPublisher()
    }
    public var onRequestPresentation: (@MainActor (MusicPlaybackDestination) -> Void)?
    public var hasLoadedLibrary: Bool { libraryState.isLoaded }
    public func ensureLibraryLoaded() async {
        if !libraryState.isLoaded, !isLoading { await load() }
    }

    /// Adapters with an authoritative stream do not need an extra UI echo.
    /// Other account adapters publish after their actual mutation completes.
    public func accountSessionsChanged(_ platform: MusicAccountPlatform? = nil) {
        guard !(accounts is any MusicAccountSessionsObserving), !isClosed else { return }
        publishAccountSessionChanges(platform.map({ [$0] }) ?? MusicAccountPlatform.allCases)
    }

    private func publishAccountSessionChanges(_ platforms: [MusicAccountPlatform]) {
        searchAccessGeneration = UUID()
        if platforms.contains(.netease) {
            cancelRecommendationOperation()
            navigationPlaybackIntent?.revoke()
            cancelQueueRecommendationPreview()
            clearRecommendationSnapshots()
            stopFM()
        }
        for platform in platforms { accountEvents.send(platform) }
    }

    func observeAccountSessions() {
        guard accountObservation == nil, let source = accounts as? any MusicAccountSessionsObserving else { return }
        accountObservation = Task { [weak self] in
            let changes = await source.accountSessionChanges()
            for await platform in changes {
                guard let self, !Task.isCancelled, !self.isClosed else { return }
                self.publishAccountSessionChanges([platform])
            }
        }
    }

    public var activePlaybackSelection: MusicPlaybackSelection? {
        candidate != nil && activeNativeAttemptID != nil ? lastSelection : nil
    }
    public var activeAudioDelivery: MusicAudioDelivery? {
        activeNativeAttemptID == nil ? nil : candidate?.delivery
    }
    /// Presentation reads the admitted source kind, never the private URL,
    /// credentials, file lease or candidate itself.
    var activeAudioIsCached: Bool? {
        activeNativeAttemptID == nil ? nil : candidate?.isCachedAudio
    }
    func chorus(actualDuration: Double) async throws -> MusicChorusPoint? {
        guard !isClosed, let candidate, nativeAttempt != nil,
              let provider = service as? any MusicChorusProviding else { return nil }
        let epoch = lifecycle, selection = selectionID
        let point = try await provider.chorus(attemptID: candidate.attemptId, actualDuration: actualDuration)
        try Task.checkCancellation()
        guard lifecycle == epoch, selectionID == selection, self.candidate?.attemptId == candidate.attemptId,
              !isClosed else { throw MusicServiceError.staleResult }
        return point
    }
    var metadataOccurrenceID: String? {
        get { metadataState.occurrenceID }
        set { metadataState.occurrenceID = newValue }
    }
    public let clock = MusicPlaybackClock()
    public internal(set) var position: Double {
        get { clock.position }
        set { if clock.position != newValue { clock.position = newValue } }
    }
    public internal(set) var duration: Double? {
        get { clock.duration }
        set { if clock.duration != newValue { clock.duration = newValue } }
    }

    let service: (any MusicServing)?
    lazy var libraryActions = MusicLibraryActions(service: service, access: accessState, state: libraryState)
    public let accounts: (any MusicAccountsServing)?
    /// Installed script sources; built-in sources are part of the playback settings.
    public let scriptSources: (any MusicScriptSourceManaging)?
    public var accountBrowser: (any MusicAccountBrowsing)? { service as? any MusicAccountBrowsing }
    public var librarySync: (any MusicLibrarySyncing)? { service as? any MusicLibrarySyncing }
    public var remoteQueueService: (any MusicRemoteQueueServing)? { service as? any MusicRemoteQueueServing }
    public var playbackFeedbackSettings: (any MusicPlaybackFeedbackSettingsServing)? { service as? any MusicPlaybackFeedbackSettingsServing }
    public var metadataPreferencesService: (any MusicMetadataPreferencesServing)? { service }
    let feedbackConsumer: MusicPlaybackFeedbackConsumer
    let sourcePlayback = MusicSourcePlaybackCoordinator()
    let recommendations = MusicRecommendationCoordinator()
    var isClosing = false
    nonisolated(unsafe) private var closingTask: Task<Void, Never>?
    let player: MusicPlaybackDriver
    var primaryPlayback: (intent: UUID, presentation: MusicMiniPlayerPresentation,
                                  receipt: MusicMiniPlayerPlaybackIdentity?)?
    var lifecycle: UUID {
        get { accessState.generation }
        set {
            searchAccessGeneration = UUID()
            accessState.generation = newValue
        }
    }
    var selectionID = UUID(), loadID = UUID()
    var collectionConsumers: [UUID: WeakMusicCollectionConsumer] = [:]
    var collectionCatalogueID = UUID()
    public var accountAccessGeneration: UUID { lifecycle }
    public private(set) var searchAccessGeneration = UUID() {
        didSet {
            let previous = searchResultsCache
            searchResultsCache = MusicSearchResultsCache.make()
            Task { await previous.invalidate() }
        }
    }
    var searchResultsCache = MusicSearchResultsCache.make()
    let playbackMetadata = MusicPlaybackMetadata()
    nonisolated(unsafe) var metadataPreferencesRefreshTask: Task<Void, Never>?
    nonisolated(unsafe) var songRepositoryObservation: Task<Void, Never>?
    var songRepositoryProjectionID = UUID()
    var metadataPreferencesRefreshRequested = false

    var feedbackAutomatic = false
    var playbackControlRevision = UUID()
    nonisolated(unsafe) var playPauseCommandTask: (id: UUID, attempt: MusicPlayerAttempt, task: Task<Void, Never>)?
    nonisolated(unsafe) var eventTail: Task<Void, Never>?
    nonisolated(unsafe) var navigationTask: Task<Void, Never>?
    var navigationID = UUID()
    var navigationQueueID: UUID?
    nonisolated(unsafe) var navigationPlaybackIntent: MusicQueueAuthorization?
    var pendingNativeResourceID: String?
    var candidate: MusicPlaybackCandidate?
    var nativeAttempt: MusicPlayerAttempt?
    var resolvingAttempt: String?
    var terminalAttempt: String?
    var lastProgressWrite: Double?
    var lastSelection: MusicPlaybackSelection?

    struct SourceReplacementIntent {
        let occurrenceID: String
        let position: Double
        let remainsPaused: Bool
    }
    struct QueuePlaybackAdmission {
        let source: MusicRemoteQueueSource?
        let intent: MusicQueueAuthorization?
        func check() throws { try source?.authorization.check(); try intent?.check() }
        func perform<T>(_ action: () throws -> T) throws -> T {
            if let source {
                return try source.authorization.perform {
                    if let intent { return try intent.perform(action) }
                    return try action()
                }
            }
            if let intent { return try intent.perform(action) }
            return try action()
        }
    }

    #if os(iOS)
    public init(service: any MusicServing, playback: PlaybackController,
                accounts: (any MusicAccountsServing)? = nil,
                scriptSources: (any MusicScriptSourceManaging)? = nil,
                artworkImages: MusicArtworkImages? = nil) {
        self.artworkImages = artworkImages ?? MusicArtworkImages()
        self.service = service; self.player = MusicPlaybackDriver(playback); self.accounts = accounts
        self.scriptSources = scriptSources
        feedbackConsumer = MusicPlaybackFeedbackConsumer(service: service as? any MusicPlaybackFeedbackConsuming)
        configureMetadata()
        observeAccountSessions()
        observeSongRepository()
    }
    #else
    public init(service: any MusicServing, playback: PlaybackController,
                accounts: (any MusicAccountsServing)? = nil,
                scriptSources: (any MusicScriptSourceManaging)? = nil) {
        self.service = service; self.player = MusicPlaybackDriver(playback); self.accounts = accounts
        self.scriptSources = scriptSources
        feedbackConsumer = MusicPlaybackFeedbackConsumer(service: service as? any MusicPlaybackFeedbackConsuming)
        configureMetadata()
        observeAccountSessions()
        observeSongRepository()
    }
    #endif
    public var current: MusicQueueEntry? { queue.first { $0.id == state.currentId } }
    public var displayTrack: MusicRemoteTrack? {
        guard let track = current?.track else { return nil }
        guard metadataOccurrenceID == current?.id else { return track }
        let current = metadataState.track ?? track
        return .init(ref: track.ref, songId: current.songId, title: current.title, artist: current.artist,
            album: current.album, duration: current.duration, sources: current.sources, artwork: metadataArtworkURL,
            artistNames: current.artistNames, sourcePosition: current.sourcePosition)
    }
    public var canNavigate: Bool {
        !isClosed && !isClosing && service != nil && current != nil &&
        recommendations.fmSource?.authorization.isActive != false &&
        !isResolving && !isNavigating && !isChangingQueue && !isChangingSimilarMode && !isChangingQueueRecommendations
    }
    public var canNext: Bool {
        guard canNavigate else { return false }
        return state.canNext
    }
    public var canPrevious: Bool {
        guard canNavigate else { return false }
        return state.canPrevious
    }
    func recordPreview(_ direction: MusicDirection) -> MusicQueueEntry? {
        let id: String?
        id = direction == .next ? state.nextPreviewID : state.previousPreviewID
        return id.flatMap { selected in queue.first { $0.id == selected } }
    }
    public var activeResourceID: String? { candidate?.resourceId }
    public var activeOccurrenceID: String? { candidate?.occurrenceId }
    public var activeNativeAttemptID: UUID? { nativeAttempt?.id }
    public var requestedQuality: String? { candidate?.requestedQuality }
    public var currentLyricIndex: Int? { MusicLyricTimeline.activeIndex(lines: lyricLines, at: position) }

    func enter() throws -> UUID {
        guard !isClosed, !isClosing else { throw MusicServiceError.closed }
        return lifecycle
    }
    func check(_ epoch: UUID) throws {
        try Task.checkCancellation()
        guard !isClosed, lifecycle == epoch else { throw MusicServiceError.cancelled }
    }
    func report(_ error: Error, epoch: UUID, operation: String? = nil,
                        kind: MusicRequestTrace.Operation = .playback, trace explicitTrace: MusicRequestTrace? = nil) {
        guard lifecycle == epoch, !isClosed else { return }
        let failure = MusicServiceError.sanitized(error)
        let trace = explicitTrace ?? MusicRequestTrace.current ?? MusicRequestTrace(kind,
            platform: kind == .playback || kind == .audio
                ? lastSelection.flatMap { MusicPublicBrowsePlatform(rawValue: $0.siteId) } : nil)
        trace.failed(error)
        if failure != .cancelled {
            self.error = failure.message(operation: operation ?? trace.operation.title, platform: trace.platform?.title,
                                         diagnosticID: trace.logicalID)
        }
    }
    func requireService() throws -> any MusicServing {
        guard let service else { throw MusicServiceError.unavailable }; return service
    }
    func stopOwned() {
        sourcePlayback.cachedAudioObservation?.cancel(); sourcePlayback.cachedAudioObservation = nil
        if let primaryPlayback, MusicPrimaryPlaybackContext.intent != primaryPlayback.intent {
            primaryPlayback.presentation.cancelHomePlayback(primaryPlayback.intent)
            self.primaryPlayback = nil
        }
        let old = nativeAttempt
        nativeAttempt = nil; pendingNativeResourceID = nil; candidate = nil
        sourcePlayback.sourceCommittedSettingsRevision = nil
        resolvingAttempt = nil; terminalAttempt = nil; lastProgressWrite = nil
        playbackState.phase = .idle
        // Stop can synchronously publish the old exit; it no longer owns the
        // model when that callback re-enters.
        if let old { player.stop(old) }
    }
    func invalidateSelection(stopMedia: Bool, reason: MusicPlaybackFeedbackReport.Reason = .interrupt,
                             preservingQueueIntent intent: MusicQueueAuthorization? = nil) {
        let retainsPlayback = !stopMedia && !isClosed && nativeAttempt != nil &&
            candidate?.occurrenceId == current?.id && terminalAttempt == nil
        if recommendations.remoteQueueIntent !== intent { cancelRemoteQueuePreparation() }
        if recommendations.recommendationIntent !== intent && recommendations.recommendationPlaybackIntent !== intent {
            recommendations.recommendationPlaybackIntent?.revoke()
        }
        if navigationPlaybackIntent !== intent { navigationPlaybackIntent?.revoke() }
        if recommendations.fmAdvanceIntent !== intent {
            recommendations.fmAdvanceIntent?.revoke(); recommendations.fmAdvanceIntent = nil
        }
        if !retainsPlayback {
            if reason == .ui || reason == .playend { feedbackConsumer.retire(reason: reason) }
            else { feedbackConsumer.invalidate(reason: reason) }
        }
        if !retainsPlayback { feedbackAutomatic = false }
        sourcePlayback.cancel(retainsPlayback ? [.resetSelection] : [.resetSelection, .clearRecoveryState, .clearIntent])
        selectionID = UUID()
        if metadataOccurrenceID != current?.id { playbackMetadata.clearSelection() }
        playbackState.isResolving = false; playbackState.options = []; playbackState.scriptSourceError = nil
        if metadataOccurrenceID != current?.id { metadataState.lyricLines = []; metadataState.lyricState = "idle" }
        if stopMedia { stopOwned() }
    }

    public func load() async {
        let epoch: UUID
        do { epoch = try enter() } catch { return }
        let token = UUID(); loadID = token; libraryState.isLoading = true
        defer { if loadID == token { libraryState.isLoading = false } }
        let started = DispatchTime.now().uptimeNanoseconds
        DebugRuntime.log(.musicLibrary, sub: "model", event: "load.start", level: .debug)
        do {
            if let service {
                let nextState = try await service.state()
                let nextQueue = try await service.queue()
                let catalogueID = collectionCatalogueID
                let nextCollections = try await service.collections()
                let nextPlatforms = try await service.platforms()
                try check(epoch); guard loadID == token else { return }
                guard nextState.revision == nextQueue.revision, nextState.currentId == nextQueue.currentId,
                      nextState.revision >= state.revision, nextState.settingsRevision >= state.settingsRevision else { throw MusicServiceError.staleRevision }
                if nextQueue.entries.map(\.id) != queue.map(\.id) { notices.queueNotice = nil }
                playbackState.state = nextState; playbackState.queue = nextQueue.entries
                pruneRetainedFMSources()
                if collectionCatalogueID == catalogueID { publishCollections(nextCollections) }
                libraryState.platforms = nextPlatforms
                libraryState.isLoaded = true
                if let repository = service as? any MusicSongRepositoryReading {
                    await refreshRepositoryProjections(repository, access: epoch)
                }
                try check(epoch); guard loadID == token else { return }
                // Persistence never means playback. No prepare, resolve, play,
                // resume, audio-session activation or synthetic played event.
                if candidate == nil { position = nextState.position; playbackState.phase = .paused }
                beginPlaybackMetadata(restoreOnly: candidate == nil)
                observeSimilarMode()
                await loadRecommendationState(refreshQueue: false)
                let elapsedMs = Double(DispatchTime.now().uptimeNanoseconds - started) / 1_000_000.0
                DebugRuntime.log(.musicLibrary, sub: "model", event: "load.end", level: .debug, durationMs: elapsedMs, data: .object([
                    "collections": .int(libraryState.collections.count),
                    "queue": .int(playbackState.queue.count),
                    "platforms": .int(libraryState.platforms.count)
                ]))
            }
        } catch {
            DebugRuntime.logError(.musicLibrary, sub: "model", event: "load.error", error: error)
            report(error, epoch: epoch, operation: "读取曲库与播放状态", kind: .library)
        }
    }

    public func updateSettings(_ change: MusicSettingsChange) async {
        guard !Task.isCancelled else { return }
        let epoch: UUID
        do { epoch = try enter() } catch { return }
        // A settings action supersedes an in-flight selection, not an already
        // playing resource. Do this before the first await, even if the write fails.
        if isResolving { invalidateSelection(stopMedia: candidate == nil) }
        let service: any MusicServing
        do { service = try requireService() } catch { return }
        let submitted = settingsReceipts.submit(settings: change.receiptSettings, operation: "保存播放设置") {
            try await service.updateSettings(change)
        }
        do {
            _ = try await submitted.value
            try await reconcilePlaybackFeedbackSettings(accessGeneration: epoch)
            try check(epoch)
            if change.mode != nil { await loadRecommendationState() }
            if !state.lyricsEnabled {
                beginPlaybackMetadata(); metadataState.lyricLines = []; metadataState.lyricState = "disabled"
            }
            else if let current, candidate?.occurrenceId == current.id {
                beginPlaybackMetadata(refreshSources: true)
            }
        } catch {
            let failure = error as? MusicServiceError ?? MusicServiceError.sanitized(error)
            if [.commitUnconfirmed, .staleRevision, .storageConflict].contains(failure) {
                do { try await reconcilePlaybackFeedbackSettings(accessGeneration: epoch) }
                catch {
                    if !Task.isCancelled { report(error, epoch: epoch, operation: "读取播放设置", kind: .settings) }
                    return
                }
            }
            if failure != .commitUnconfirmed, !Task.isCancelled {
                report(error, epoch: epoch, operation: "保存播放设置", kind: .settings)
            }
        }
    }
    public func setMode(_ mode: MusicPlaybackMode) async {
        await updateSettings(MusicSettingsChange(ifRevision: state.settingsRevision, mode: mode))
    }
    /// Stops output synchronously while exit observations still own their
    /// candidate. `close()` drains those local facts, never the network ACK.
    public func prepareForClose() {
        guard !isClosed, !isClosing else { return }
        isClosing = true
        (service as? any MusicPlaybackFeedbackClosePreparing)?.stopPlaybackFeedbackDispatchForClose()
        cancelPlayPauseCommands()
        navigationTask?.cancel()
        sourcePlayback.cancel()
        let attempt = resolvingAttempt ?? candidate?.attemptId
        // Stop while the immutable native exit still has its owner/candidate.
        // Freeze that actual final observation before retiring FM/source grants.
        if let nativeAttempt { player.stop(nativeAttempt) }
        nativeAttempt = nil; pendingNativeResourceID = nil
        closingTask = Task { await completeClose(attemptID: attempt) }
    }
    public func close() async {
        prepareForClose()
        await closingTask?.value
    }
    public func debugCacheStats() async -> DebugJSONValue {
        let searchStats = await searchResultsCache.stats()
        return .object([
            "lyricMemoryCache": playbackMetadata.debugLyricCacheStats(),
            "searchResultsCache": .object([
                "entryCount": .int(searchStats.entryCount),
                "inFlightCount": .int(searchStats.inFlightCount),
                "totalCost": .int(searchStats.totalCost),
                "capacity": searchStats.capacity.map(DebugJSONValue.int) ?? .null,
                "maximumCost": .int(searchStats.maximumCost)
            ])
        ])
    }

    public func debugClearMemoryCaches() async {
        playbackMetadata.clearMemoryLyricCache()
        await searchResultsCache.invalidate()
    }

    private func completeClose(attemptID: String?) async {
        await searchPreferences.close()
        do { try await searchHistory.close() } catch { self.error = "搜索历史关闭失败" }
        await channelSelection.close()
        await feedbackConsumer.flush()
        await eventTail?.value
        await feedbackConsumer.finish(reason: .interrupt)
        await settingsReceipts.flush()
        favoritesObservation?.cancel(); favoritesObservation = nil
        favoritesStorage?.suspend()
        accountObservation?.cancel(); accountObservation = nil
        songRepositoryObservation?.cancel(); songRepositoryObservation = nil
        notifyCollectionPages(.revoking)
        cancelRemoteQueuePreparation(); cancelFMNavigation(); await stopFMAndDrain()
        stopSimilarObservation()
        accessState.isClosed = true; lifecycle = UUID(); navigationTask?.cancel(); eventTail?.cancel()
        metadataPreferencesRefreshRequested = false
        metadataPreferencesRefreshTask?.cancel()
        await playbackMetadata.close()
        await searchResultsCache.invalidate()
        notifyCollectionPages(.access)
        navigationID = UUID(); playbackState.isNavigating = false; playbackState.isChangingQueue = false
        invalidateSelection(stopMedia: true)
        playbackState.queue = []; notices.queueNotice = nil; metadataState.lyricLines = []
        libraryState.isLoaded = false
        await favoritesStorage?.flush()
        if let service {
            if let attemptID { do { try await service.cancel(attemptId: attemptID) } catch { self.error = MusicServiceError.sanitized(error).localizedDescription } }
        }
    }
    nonisolated deinit {
        favoritesObservation?.cancel()
        if let favorites = favoritesStorage {
            Task { @MainActor in favorites.suspend() }
        }
        accountObservation?.cancel()
        songRepositoryObservation?.cancel()
        recommendations.cancelAllInDeinit()
        sourcePlayback.cancelAllInDeinit()
        playPauseCommandTask?.task.cancel()
        eventTail?.cancel()
        navigationTask?.cancel()
        navigationPlaybackIntent?.revoke()
        metadataPreferencesRefreshTask?.cancel()
        closingTask?.cancel()
    }
}
#endif
