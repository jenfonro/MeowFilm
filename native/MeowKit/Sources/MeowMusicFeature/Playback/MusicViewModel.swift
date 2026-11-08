#if os(iOS) || os(macOS)
import Foundation
import Combine
import MeowContracts
import MeowPlayback
import MeowStorage

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

/// Deterministic injection point for tests/other platform UI; the production
/// adapter below delegates exclusively to the product's PlaybackController.
@MainActor public protocol MusicPlaybackDriving: AnyObject {
    func play(_ resource: PlaybackResource) -> MusicPlayerAttempt
    func pause(_ attempt: MusicPlayerAttempt)
    func resume(_ attempt: MusicPlayerAttempt)
    /// true means accepted by the current driver, not that media has reached
    /// the target. A rejected request must not resume playback or confirm UI.
    @discardableResult func seek(_ position: Double, attempt: MusicPlayerAttempt) -> Bool
    func stop(_ attempt: MusicPlayerAttempt)
}
@MainActor public protocol MusicPlaybackHandoffDriving: MusicPlaybackDriving {
    func prepareReplacement(_ resource: PlaybackResource, replacing: MusicPlayerAttempt) async throws -> MusicPreparedPlayback
    func preparePlayback(_ resource: PlaybackResource, autoplay: Bool) async throws -> MusicPreparedPlayback
}
@MainActor private final class SharedMusicPlaybackDriver: MusicPlaybackHandoffDriving {
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
    func seek(_ position: Double, attempt: MusicPlayerAttempt) -> Bool {
        guard let current = target(attempt) else { return false }
        return playback.seek(to: position, attempt: current)
    }
    func stop(_ attempt: MusicPlayerAttempt) { if let current = target(attempt) { playback.stop(attempt: current) } }
}

/// Product lifetime, not page lifetime. Products bind PlaybackEvent and system
/// navigation once and retain this model while another domain is visible.
@MainActor public final class MusicViewModel {
    public let accessState = MusicAccessState()
    public let libraryState = MusicLibraryState()
    public let playbackState = MusicPlaybackFeatureState()
    public let metadataState = MusicMetadataState()
    public var query = ""
    public let notices = MusicNoticeState()
    private var favoritesStorage: MusicMiniPlayerFavorites?
    private var favoritesObservation: AnyCancellable?
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
    var accountObservation: Task<Void, Never>?
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
        guard !(accounts is any MusicAccountSessionsObserving), isAvailable, !isClosed else { return }
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
                if self.isAvailable { self.publishAccountSessionChanges([platform]) }
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
        guard isAvailable, !isClosed, let candidate, nativeAttempt != nil,
              let provider = service as? any MusicChorusProviding else { return nil }
        let epoch = lifecycle, selection = selectionID
        let point = try await provider.chorus(attemptID: candidate.attemptId, actualDuration: actualDuration)
        try Task.checkCancellation()
        guard lifecycle == epoch, selectionID == selection, self.candidate?.attemptId == candidate.attemptId,
              isAvailable, !isClosed else { throw MusicServiceError.staleResult }
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
    var feedbackAutomatic = false
    var player: (any MusicPlaybackDriving)?
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
        didSet { searchResultsCache = MusicSearchResultsCache.make() }
    }
    var searchResultsCache = MusicSearchResultsCache.make()
    var sourceIntent = MusicSourceIntent.automatic
    var sourceCommittedSettingsRevision: Int?
    var pendingSourceSelection: (id: UUID, intent: MusicSourceIntent, lifetime: MusicQueueAuthorization)?
    var pendingSourceFailure: (id: UUID, fact: MusicPlaybackFact, invalidDuration: Bool)?
    // Synchronous native commit can publish a failure before returning. Defer
    // its recovery until the successful commit publishes the new intent.
    var sourceCommit: (attemptID: String, failure: MusicPlaybackFact?, invalidDuration: Bool)?
    var sourceRenewed = Set<String>()
    var sourceRecoveryID = UUID()
    var sourceRecoveryTask: Task<Void, Never>?
    var sourceRecoveryWantsPlayback: Bool?
    var sourceSwitchTask: Task<MusicPreparedSource, Error>?
    var sourceSwitchID = UUID()
    var playbackControlRevision = UUID()
    var playPauseCommandTask: (id: UUID, attempt: MusicPlayerAttempt, task: Task<Void, Never>)?
    var sourceMediaTask: Task<MusicPreparedPlayback, Error>?
    var resolutionTask: Task<MusicPlaybackCandidate, Error>?
    var candidateDiscoveryTask: Task<MusicCandidateDiscoveryResolution, Error>?
    var lyricTask: Task<MusicLyricsSnapshot, Error>?
    let playbackMetadata = MusicPlaybackMetadata()
    var metadataPreferencesRefreshTask: Task<Void, Never>?
    var metadataPreferencesRefreshRequested = false
    var cachedAudioLookup: Task<MusicCachedPlayback?, Error>?
    var cachedAudioObservation: Task<Void, Never>?
    var cachedAudioRecoveryTask: Task<Void, Never>?
    var remoteAudioRecoveryTask: Task<Void, Never>?
    // Keep cancelled continuations until their exact attempt cleanup finishes.
    // Clearing the active pointer is not evidence that a late result drained.
    var recoveryDrains: [UUID: Task<Void, Never>] = [:]
    var remoteAudioRecoveryTried = false
    struct CachedAudioRecovery {
        let attemptID: String
        let selection: MusicPlaybackSelection?
        let automatic: Bool
        let admission: QueuePlaybackAdmission?
    }
    var cachedAudioRecovery: CachedAudioRecovery?
    var eventTail: Task<Void, Never>?
    var navigationTask: Task<Void, Never>?
    var navigationID = UUID()
    var navigationQueueID: UUID?
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
    var similarObservation: Task<Void, Never>?
    var similarObservationID = UUID()
    var recommendationReadID = UUID()
    var recommendationPreviewReadID: UUID?
    var queuePreviewCancellation: Task<Void, Never>?
    var recommendationQueueRevision: Int?
    var recommendationAccount: UUID?
    var recommendationCommandID = UUID()
    var recommendationReturnID: UUID?
    var recommendationTask: Task<MusicSimilarModeResult, Error>?
    var recommendationIntent: MusicQueueAuthorization?
    var recommendationPlaybackIntent: MusicQueueAuthorization?
    var navigationPlaybackIntent: MusicQueueAuthorization?
    var remoteQueueTask: Task<MusicPlaybackState, Error>?
    var remoteQueueIntent: MusicQueueAuthorization?
    var remoteQueueRequest = UUID()
    var remoteQueueSourceID: UUID?
    var remoteQueueIsFM = false
    var fmSource: MusicRemoteQueueSource?
    var fmRevocationTask: Task<Void, Never>?
    var fmAdvanceIntent: MusicQueueAuthorization?
    var fmShouldPlay = true
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

    public init(service: any MusicServing, playback: PlaybackController,
                accounts: (any MusicAccountsServing)? = nil,
                scriptSources: (any MusicScriptSourceManaging)? = nil) {
        self.service = service; self.player = SharedMusicPlaybackDriver(playback); self.accounts = accounts
        self.scriptSources = scriptSources
        feedbackConsumer = MusicPlaybackFeedbackConsumer(service: service as? any MusicPlaybackFeedbackConsuming)
        configureMetadata()
        observeAccountSessions()
    }
    public init(service: any MusicServing, player: any MusicPlaybackDriving,
                accounts: (any MusicAccountsServing)? = nil,
                scriptSources: (any MusicScriptSourceManaging)? = nil) {
        self.service = service; self.player = player; self.accounts = accounts
        self.scriptSources = scriptSources
        feedbackConsumer = MusicPlaybackFeedbackConsumer(service: service as? any MusicPlaybackFeedbackConsuming)
        configureMetadata()
        observeAccountSessions()
    }
    public func attachPlayback(_ playback: PlaybackController) {
        if player == nil { player = SharedMusicPlaybackDriver(playback) }
    }
    public var current: MusicQueueEntry? { queue.first { $0.id == state.currentId } }
    public var displayTrack: MusicRemoteTrack? {
        guard let track = current?.track else { return nil }
        guard metadataOccurrenceID == current?.id else { return track }
        return .init(ref: track.ref, songId: track.songId, title: track.title, artist: track.artist,
            album: track.album, duration: track.duration, sources: track.sources, artwork: metadataArtworkURL)
    }
    public var canNavigate: Bool {
        isAvailable && !isClosed && service != nil && player != nil && current != nil &&
        !isResolving && !isNavigating && !isChangingQueue && !isChangingSimilarMode && !isChangingQueueRecommendations
    }
    public var canNext: Bool {
        guard canNavigate else { return false }
        if let fmSource { return fmSource.authorization.isActive }
        return state.canNext
    }
    public var canPrevious: Bool {
        guard canNavigate else { return false }
        if let fmSource { return fmSource.authorization.isActive && queue.first?.id != state.currentId }
        return state.canPrevious
    }
    func recordPreview(_ direction: MusicDirection) -> MusicQueueEntry? {
        let id: String?
        if isFMActive { id = direction == .next ? state.fmNextPreviewID : state.fmPreviousPreviewID }
        else { id = direction == .next ? state.nextPreviewID : state.previousPreviewID }
        return id.flatMap { selected in queue.first { $0.id == selected } }
    }
    public var activeResourceID: String? { candidate?.resourceId }
    public var activeOccurrenceID: String? { candidate?.occurrenceId }
    public var activeNativeAttemptID: UUID? { nativeAttempt?.id }
    public var requestedQuality: String? { candidate?.requestedQuality }
    public var currentLyricIndex: Int? { MusicLyricTimeline.activeIndex(lines: lyricLines, at: position) }

    func enter() throws -> UUID {
        guard !isClosed else { throw MusicServiceError.closed }
        guard isAvailable else { throw MusicServiceError.accessUnavailable }
        return lifecycle
    }
    func check(_ epoch: UUID) throws {
        try Task.checkCancellation()
        guard isAvailable, !isClosed, lifecycle == epoch else { throw MusicServiceError.cancelled }
    }
    func report(_ error: Error, epoch: UUID, operation: String? = nil,
                        kind: MusicRequestTrace.Operation = .playback, trace explicitTrace: MusicRequestTrace? = nil) {
        guard lifecycle == epoch, !isClosed, isAvailable else { return }
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
        cachedAudioObservation?.cancel(); cachedAudioObservation = nil
        if let primaryPlayback, MusicPrimaryPlaybackContext.intent != primaryPlayback.intent {
            primaryPlayback.presentation.cancelHomePlayback(primaryPlayback.intent)
            self.primaryPlayback = nil
        }
        let old = nativeAttempt
        nativeAttempt = nil; pendingNativeResourceID = nil; candidate = nil
        sourceCommittedSettingsRevision = nil
        resolvingAttempt = nil; terminalAttempt = nil; lastProgressWrite = nil
        playbackState.phase = .idle
        // Stop can synchronously publish the old exit; it no longer owns the
        // model when that callback re-enters.
        if let old { player?.stop(old) }
    }
    func invalidateSelection(stopMedia: Bool, preservingQueueIntent intent: MusicQueueAuthorization? = nil) {
        let retainsPlayback = !stopMedia && isAvailable && !isClosed && nativeAttempt != nil &&
            candidate?.occurrenceId == current?.id && terminalAttempt == nil
        if remoteQueueIntent !== intent { cancelRemoteQueuePreparation() }
        if recommendationIntent !== intent && recommendationPlaybackIntent !== intent { recommendationPlaybackIntent?.revoke() }
        if navigationPlaybackIntent !== intent { navigationPlaybackIntent?.revoke() }
        if fmAdvanceIntent !== intent { fmAdvanceIntent?.revoke(); fmAdvanceIntent = nil }
        feedbackConsumer.invalidate()
        if !retainsPlayback { feedbackAutomatic = false }
        sourceSwitchID = UUID(); sourceSwitchTask?.cancel(); sourceSwitchTask = nil; sourceMediaTask?.cancel(); sourceMediaTask = nil
        sourceRecoveryTask?.cancel(); sourceRecoveryTask = nil
        sourceRecoveryWantsPlayback = nil; sourceRecoveryID = UUID()
        if !retainsPlayback { sourceIntent = .automatic; sourceRenewed = [] }
        pendingSourceSelection?.lifetime.revoke(); pendingSourceSelection = nil
        pendingSourceFailure = nil
        selectionID = UUID()
        resolutionTask?.cancel(); resolutionTask = nil
        candidateDiscoveryTask?.cancel(); candidateDiscoveryTask = nil
        lyricTask?.cancel(); lyricTask = nil
        if metadataOccurrenceID != current?.id { playbackMetadata.clearSelection() }
        cachedAudioLookup?.cancel(); cachedAudioLookup = nil
        cachedAudioRecoveryTask?.cancel(); cachedAudioRecoveryTask = nil
        remoteAudioRecoveryTask?.cancel(); remoteAudioRecoveryTask = nil
        if !retainsPlayback { cachedAudioRecovery = nil; remoteAudioRecoveryTried = false }
        playbackState.isResolving = false; playbackState.options = []; playbackState.scriptSourceError = nil
        if metadataOccurrenceID != current?.id { metadataState.lyricLines = []; metadataState.lyricState = "idle" }
        if stopMedia { stopOwned() }
    }

    /// Gate/UI revocation is synchronous and never attempts a domain read.
    /// An already-held remote stream continues. Local-file admission is
    /// stopped here; new business operations also stop.
    public func setAvailable(_ available: Bool) {
        guard isAvailable != available, !isClosed else { return }
        cancelPlayPauseCommands()
        notifyCollectionPages(.revoking)
        favoritesStorage?.suspend()
        // Unlike an already-held remote stream, a local cached file has an
        // explicitly revocable file admission. Never retain it on revocation.
        cancelRemoteQueuePreparation(); stopFM()
        stopSimilarObservation()
        if candidate?.isCachedAudio == true { stopOwned() }
        accessState.isAvailable = available; lifecycle = UUID(); loadID = UUID()
        playbackMetadata.setAccess(available: available, generation: lifecycle)
        metadataPreferencesRefreshRequested = false
        metadataPreferencesRefreshTask?.cancel()
        navigationTask?.cancel(); navigationTask = nil
        navigationID = UUID(); playbackState.isNavigating = false; playbackState.isChangingQueue = false; pendingNativeResourceID = nil
        invalidateSelection(stopMedia: false); eventTail?.cancel()
        candidate = nil; resolvingAttempt = nil; terminalAttempt = nil
        playbackState.state = MusicPlaybackState(); playbackState.queue = []; notices.queueNotice = nil; libraryState.collections = []; libraryState.platforms = []
        libraryState.playedSongs = []; libraryState.playedPlaylists = []
        libraryState.isLoaded = false; libraryState.isLoading = false
        position = 0; duration = nil; error = nil
        notifyCollectionPages(.access)
    }

    public func load() async {
        let epoch: UUID
        do { epoch = try enter() } catch { return }
        let token = UUID(); loadID = token; libraryState.isLoading = true
        defer { if loadID == token { libraryState.isLoading = false } }
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
                if collectionCatalogueID == catalogueID { publishCollections(nextCollections) }
                libraryState.platforms = nextPlatforms
                libraryState.isLoaded = true
                // Persistence never means playback. No prepare, resolve, play,
                // resume, audio-session activation or synthetic played event.
                if candidate == nil { position = nextState.position; playbackState.phase = .paused }
                beginPlaybackMetadata(restoreOnly: candidate == nil)
                observeSimilarMode()
                await loadRecommendationState(refreshQueue: false)
            }
        } catch { report(error, epoch: epoch, operation: "读取曲库与播放状态", kind: .library) }
    }

    public func updateSettings(_ change: MusicSettingsChange) async {
        let epoch: UUID
        do { epoch = try enter() } catch { return }
        // A settings action supersedes an in-flight selection, not an already
        // playing resource. Do this before the first await, even if the write fails.
        if isResolving { invalidateSelection(stopMedia: candidate == nil) }
        do {
            _ = try await requireService().updateSettings(change)
            try await reconcilePlaybackFeedbackSettings(accessGeneration: epoch)
            try check(epoch)
            if !state.lyricsEnabled {
                lyricTask?.cancel()
                beginPlaybackMetadata(); metadataState.lyricLines = []; metadataState.lyricState = "disabled"
            }
            else if let current, candidate?.occurrenceId == current.id {
                await loadLyrics(occurrenceId: current.id)
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
            if !Task.isCancelled { report(error, epoch: epoch, operation: "保存播放设置", kind: .settings) }
        }
    }
    public func setMode(_ mode: MusicPlaybackMode) async {
        await updateSettings(MusicSettingsChange(ifRevision: state.settingsRevision, mode: mode))
    }
    public func close() async {
        guard !isClosed else { return }
        cancelPlayPauseCommands()
        favoritesObservation?.cancel(); favoritesObservation = nil
        favoritesStorage?.suspend()
        accountObservation?.cancel(); accountObservation = nil
        notifyCollectionPages(.revoking)
        cancelRemoteQueuePreparation(); cancelFMNavigation(); stopFM()
        stopSimilarObservation()
        let attempt = resolvingAttempt ?? candidate?.attemptId
        accessState.isClosed = true; lifecycle = UUID(); navigationTask?.cancel(); eventTail?.cancel()
        metadataPreferencesRefreshRequested = false
        metadataPreferencesRefreshTask?.cancel()
        playbackMetadata.close()
        notifyCollectionPages(.access)
        navigationID = UUID(); playbackState.isNavigating = false; playbackState.isChangingQueue = false
        invalidateSelection(stopMedia: true)
        playbackState.queue = []; notices.queueNotice = nil; metadataState.lyricLines = []
        libraryState.isLoaded = false
        await favoritesStorage?.flush()
        if let service {
            if let attempt { do { try await service.cancel(attemptId: attempt) } catch { self.error = MusicServiceError.sanitized(error).localizedDescription } }
        }
    }
    // The model is owned by MainActor UI. Tear down synchronously instead of
    // entering the nested isolated-deinit runtime path on older runtimes.
    nonisolated deinit {
        MainActor.assumeIsolated {
            favoritesObservation?.cancel()
            favoritesStorage?.suspend()
            accountObservation?.cancel()
            remoteQueueIntent?.revoke(); remoteQueueTask?.cancel()
            fmAdvanceIntent?.revoke(); fmRevocationTask?.cancel(); fmSource?.revoke()
            sourceSwitchTask?.cancel(); sourceMediaTask?.cancel()
            playPauseCommandTask?.task.cancel()
            resolutionTask?.cancel(); candidateDiscoveryTask?.cancel()
            lyricTask?.cancel(); eventTail?.cancel(); navigationTask?.cancel()
            metadataPreferencesRefreshTask?.cancel()
            cachedAudioLookup?.cancel(); cachedAudioObservation?.cancel(); cachedAudioRecoveryTask?.cancel()
            remoteAudioRecoveryTask?.cancel()
            for task in recoveryDrains.values { task.cancel() }
            similarObservation?.cancel()
        }
    }
}
#endif
