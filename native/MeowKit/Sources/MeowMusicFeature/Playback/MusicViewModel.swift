#if os(iOS) || os(macOS)
import Foundation
import Combine
import CryptoKit
import MeowContracts
import MeowPlayback
import MeowStorage

/// Only time-dependent presentation observes this clock. Progress does not
/// invalidate navigation, browse catalogues, artwork owners or queue lists.
@MainActor public final class MusicPlaybackClock: ObservableObject {
    @Published public fileprivate(set) var position: Double = 0
    @Published public fileprivate(set) var duration: Double?
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
@MainActor private final class SharedMusicPlaybackDriver: MusicPlaybackDriving {
    let playback: PlaybackController
    init(_ playback: PlaybackController) { self.playback = playback }
    func play(_ resource: PlaybackResource) -> MusicPlayerAttempt {
        let attempt = playback.playAttempt(resource: resource, owner: "music")
        return MusicPlayerAttempt(id: attempt.id, resourceID: attempt.resourceID)
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
@MainActor public final class MusicViewModel: ObservableObject {
    @Published public private(set) var state = MusicPlaybackState()
    @Published public private(set) var platforms: [MusicPlatform] = []
    @Published public private(set) var scriptSourceError: String?
    @Published public private(set) var collections: [MusicCollection] = []
    @Published public private(set) var queue: [MusicQueueEntry] = []
    @Published public private(set) var searchResults: [MusicRemoteTrack] = []
    @Published public private(set) var nextSearchPage: Int?
    @Published public private(set) var hasSearched = false
    @Published public var query = ""
    @Published public var selectedPlatform = "wy"
    @Published public private(set) var isLoading = false
    @Published public private(set) var isSearching = false
    @Published public private(set) var isResolving = false
    @Published public private(set) var isNavigating = false
    @Published public private(set) var isChangingQueue = false
    @Published public private(set) var isAvailable = true
    @Published public private(set) var isClosed = false
    @Published public private(set) var options: [MusicPlaybackOption] = []
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
    @Published public var showingSources = false
    @Published public var showingPlayer = false
    @Published public private(set) var lyricState = "idle"
    @Published public private(set) var lyricLines: [MusicLyricLine] = []
    @Published public private(set) var metadataArtworkURL: URL?
    @Published public private(set) var metadataArtworkData: Data?
    private var metadataOccurrenceID: String?
    public let clock = MusicPlaybackClock()
    public private(set) var position: Double {
        get { clock.position }
        set { if clock.position != newValue { clock.position = newValue } }
    }
    public private(set) var duration: Double? {
        get { clock.duration }
        set { if clock.duration != newValue { clock.duration = newValue } }
    }
    @Published public private(set) var phase: PlaybackPhase = .idle
    @Published public private(set) var playedSongs: [MusicPlayedSong] = []
    @Published public private(set) var playedPlaylists: [MusicPlayedPlaylist] = []
    @Published public private(set) var importPreview: MusicImportPreview?
    @Published public private(set) var isImporting = false
    @Published public var importOwner = ""
    @Published public var showingImport = false
    @Published public var error: String?
    @Published public private(set) var queueNotice: String?

    private let service: (any MusicServing)?
    public let accounts: (any MusicAccountsServing)?
    public var accountBrowser: (any MusicAccountBrowsing)? { service as? any MusicAccountBrowsing }
    public var librarySync: (any MusicLibrarySyncing)? { service as? any MusicLibrarySyncing }
    public var importManagement: (any MusicImportHistoryProviding)? { service as? any MusicImportHistoryProviding }
    public var remoteQueueService: (any MusicRemoteQueueServing)? { service as? any MusicRemoteQueueServing }
    public var playbackFeedbackSettings: (any MusicPlaybackFeedbackSettingsServing)? { service as? any MusicPlaybackFeedbackSettingsServing }
    public var metadataPreferencesService: (any MusicMetadataPreferencesServing)? { service as? any MusicMetadataPreferencesServing }
    private let feedbackConsumer: MusicPlaybackFeedbackConsumer
    private var feedbackAutomatic = false
    private var player: (any MusicPlaybackDriving)?
    private var primaryPlayback: (intent: UUID, presentation: MusicMiniPlayerPresentation,
                                  receipt: MusicMiniPlayerPlaybackIdentity?)?
    private var lifecycle = UUID(), searchID = UUID(), selectionID = UUID(), loadID = UUID()
    private var importID = UUID()
    private var collectionConsumers: [UUID: WeakMusicCollectionConsumer] = [:]
    private var collectionCatalogueID = UUID()
    public var accountAccessGeneration: UUID { lifecycle }
    private var searchTask: Task<MusicSearchPage, Error>?
    private var resolutionTask: Task<MusicPlaybackCandidate, Error>?
    private var candidateDiscoveryTask: Task<MusicCandidateDiscoveryResolution, Error>?
    private var lyricTask: Task<MusicLyricsSnapshot, Error>?
    private let playbackMetadata = MusicPlaybackMetadata()
    private var metadataPreferencesRefreshTask: Task<Void, Never>?
    private var metadataPreferencesRefreshRequested = false
    private var cachedAudioLookup: Task<MusicCachedPlayback?, Error>?
    private var cachedAudioObservation: Task<Void, Never>?
    private var cachedAudioRecoveryTask: Task<Void, Never>?
    private var remoteAudioRecoveryTask: Task<Void, Never>?
    // Keep cancelled continuations until their exact attempt cleanup finishes.
    // Clearing the active pointer is not evidence that a late result drained.
    private var recoveryDrains: [UUID: Task<Void, Never>] = [:]
    private var remoteAudioRecoveryTried = false
    private struct CachedAudioRecovery {
        let attemptID: String
        let selection: MusicPlaybackSelection?
        let automatic: Bool
        let admission: QueuePlaybackAdmission?
    }
    private var cachedAudioRecovery: CachedAudioRecovery?
    private var eventTail: Task<Void, Never>?
    private var navigationTask: Task<Void, Never>?
    private var navigationID = UUID()
    private var navigationQueueID: UUID?
    private var pendingNativeResourceID: String?
    private var pending = 0, pendingFacts = 0
    private var candidate: MusicPlaybackCandidate?
    private var nativeAttempt: MusicPlayerAttempt?
    private var resolvingAttempt: String?
    private var terminalAttempt: String?
    private var lastProgressWrite: Double?
    private var lastSelection: MusicPlaybackSelection?
    private struct SourceReplacementIntent {
        let occurrenceID: String
        let position: Double
        let remainsPaused: Bool
    }
    private var importData: Data?
    private var importSourceID: String?
    private var searchContext: (platform: String, query: String)?
    @Published public private(set) var isPreparingRemoteQueue = false
    @Published public private(set) var isFMActive = false
    @Published public private(set) var similarMode: MusicSimilarModeSnapshot?
    @Published public private(set) var canEnterSimilarMode = false
    @Published public private(set) var isChangingSimilarMode = false
    private var similarObservation: Task<Void, Never>?
    private var similarObservationID = UUID()
    private var remoteQueueTask: Task<MusicPlaybackState, Error>?
    private var remoteQueueIntent: MusicQueueAuthorization?
    private var remoteQueueRequest = UUID()
    private var remoteQueueSourceID: UUID?
    private var remoteQueueIsFM = false
    private var fmSource: MusicRemoteQueueSource?
    @Published public private(set) var activeHomeFM: MusicHomeAction?
    private var fmRevocationTask: Task<Void, Never>?
    private var fmAdvanceIntent: MusicQueueAuthorization?
    private var fmShouldPlay = true
    private struct QueuePlaybackAdmission {
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
                accounts: (any MusicAccountsServing)? = nil) {
        self.service = service; self.player = SharedMusicPlaybackDriver(playback); self.accounts = accounts
        feedbackConsumer = MusicPlaybackFeedbackConsumer(service: service as? any MusicPlaybackFeedbackConsuming)
        configureMetadata()
    }
    public init(service: any MusicServing, player: any MusicPlaybackDriving,
                accounts: (any MusicAccountsServing)? = nil) {
        self.service = service; self.player = player; self.accounts = accounts
        feedbackConsumer = MusicPlaybackFeedbackConsumer(service: service as? any MusicPlaybackFeedbackConsuming)
        configureMetadata()
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
    private func configureMetadata() {
        playbackMetadata.configure(service: service, accounts: accounts) { [weak self] value in
            guard let self else { return }
            self.metadataOccurrenceID = value.occurrenceID
            self.lyricLines = value.lines; self.lyricState = value.lyricState
            self.metadataArtworkURL = value.artwork; self.metadataArtworkData = value.cachedArtwork
        }
        playbackMetadata.setAccess(available: isAvailable, generation: lifecycle)
    }
    public func setPlaybackDisplayCache(_ cache: (any MusicPlaybackDisplayCaching)?) {
        playbackMetadata.setDisplayCache(cache)
    }
    public func artworkFailed(_ url: URL?) { playbackMetadata.artworkFailed(url) }
    public func capturedArtwork(_ data: Data, url: URL?) { playbackMetadata.capturedArtwork(data, url: url) }
    private func beginPlaybackMetadata(restoreOnly: Bool = false, fallbackSourceKey: String? = nil, force: Bool = false) {
        guard let current else { playbackMetadata.clearSelection(); return }
        playbackMetadata.select(current, lyricsEnabled: state.lyricsEnabled, restoreOnly: restoreOnly,
                                fallbackSourceKey: fallbackSourceKey, force: force)
    }
    public func metadataPreferencesChanged() async {
        let own = lifecycle
        do {
            let value = try await requireService().state(); try check(own); state = value
            beginPlaybackMetadata(force: true)
        } catch { report(error, epoch: own, operation: "读取歌词与封面设置", kind: .settings) }
    }
    func scheduleMetadataPreferencesRefresh() {
        guard isAvailable, !isClosed else { return }
        metadataPreferencesRefreshRequested = true
        guard metadataPreferencesRefreshTask == nil else { return }
        metadataPreferencesRefreshRequested = false
        metadataPreferencesRefreshTask = Task { [weak self] in
            guard let self else { return }
            await self.metadataPreferencesChanged()
            self.metadataPreferencesRefreshTask = nil
            if self.metadataPreferencesRefreshRequested {
                self.scheduleMetadataPreferencesRefresh()
            }
        }
    }
    func flushMetadataPreferencesRefresh() async {
        while let pending = metadataPreferencesRefreshTask { await pending.value }
    }
    public var canNavigate: Bool {
        isAvailable && !isClosed && service != nil && player != nil && current != nil &&
        !isResolving && !isNavigating && !isChangingQueue && pending < 10
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

    private func enter() throws -> UUID {
        guard !isClosed else { throw MusicServiceError.closed }
        guard isAvailable else { throw MusicServiceError.accessUnavailable }
        guard pending < 10 else { throw MusicServiceError.busy }
        pending += 1; return lifecycle
    }
    private func check(_ epoch: UUID) throws {
        try Task.checkCancellation()
        guard isAvailable, !isClosed, lifecycle == epoch else { throw MusicServiceError.cancelled }
    }
    private func report(_ error: Error, epoch: UUID, operation: String? = nil,
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
    private func requireService() throws -> any MusicServing {
        guard let service else { throw MusicServiceError.unavailable }; return service
    }
    private func stopOwned() {
        cachedAudioObservation?.cancel(); cachedAudioObservation = nil
        if let primaryPlayback, MusicPrimaryPlaybackContext.intent != primaryPlayback.intent {
            primaryPlayback.presentation.cancelHomePlayback(primaryPlayback.intent)
            self.primaryPlayback = nil
        }
        let old = nativeAttempt
        nativeAttempt = nil; pendingNativeResourceID = nil; candidate = nil
        resolvingAttempt = nil; terminalAttempt = nil; lastProgressWrite = nil
        phase = .idle
        // Stop can synchronously publish the old exit; it no longer owns the
        // model when that callback re-enters.
        if let old { player?.stop(old) }
    }
    private func invalidateSelection(stopMedia: Bool, preservingQueueIntent intent: MusicQueueAuthorization? = nil,
                                     keepSources: Bool = false) {
        if remoteQueueIntent !== intent { cancelRemoteQueuePreparation() }
        if fmAdvanceIntent !== intent { fmAdvanceIntent?.revoke(); fmAdvanceIntent = nil }
        feedbackConsumer.invalidate()
        feedbackAutomatic = false
        selectionID = UUID()
        resolutionTask?.cancel(); resolutionTask = nil
        candidateDiscoveryTask?.cancel(); candidateDiscoveryTask = nil
        lyricTask?.cancel(); lyricTask = nil
        if metadataOccurrenceID != current?.id { playbackMetadata.clearSelection() }
        cachedAudioLookup?.cancel(); cachedAudioLookup = nil
        cachedAudioRecoveryTask?.cancel(); cachedAudioRecoveryTask = nil; cachedAudioRecovery = nil
        remoteAudioRecoveryTask?.cancel(); remoteAudioRecoveryTask = nil; remoteAudioRecoveryTried = false
        isResolving = false; options = []; scriptSourceError = nil
        if !keepSources { showingSources = false }
        if metadataOccurrenceID != current?.id { lyricLines = []; lyricState = "idle" }
        if stopMedia { stopOwned() }
    }

    /// Gate/UI revocation is synchronous and never attempts a domain read.
    /// Remote stream lock-screen policy belongs to product assembly. Protected
    /// local-file admission is stopped here; new business operations also stop.
    public func setAvailable(_ available: Bool) {
        guard isAvailable != available, !isClosed else { return }
        notifyCollectionPages(.revoking)
        // Unlike an already-held remote stream, a protected local file has
        // an explicitly revocable file admission. Never retain it on lock.
        cancelRemoteQueuePreparation(); stopFM()
        stopSimilarObservation()
        if candidate?.isCachedAudio == true { stopOwned() }
        isAvailable = available; lifecycle = UUID(); loadID = UUID(); searchID = UUID()
        playbackMetadata.setAccess(available: available, generation: lifecycle)
        metadataPreferencesRefreshRequested = false
        metadataPreferencesRefreshTask?.cancel()
        searchTask?.cancel(); searchTask = nil; navigationTask?.cancel(); navigationTask = nil
        navigationID = UUID(); isNavigating = false; isChangingQueue = false; pendingNativeResourceID = nil
        invalidateSelection(stopMedia: false); eventTail?.cancel()
        candidate = nil; resolvingAttempt = nil; terminalAttempt = nil
        state = MusicPlaybackState(); queue = []; queueNotice = nil; collections = []; platforms = []
        searchResults = []; hasSearched = false; nextSearchPage = nil; searchContext = nil
        playedSongs = []; playedPlaylists = []; importData = nil; importPreview = nil; importSourceID = nil
        showingImport = false; isLoading = false; isSearching = false; isImporting = false
        position = 0; duration = nil; error = nil
        notifyCollectionPages(.access)
    }

    public func load() async {
        let epoch: UUID
        do { epoch = try enter() } catch { return }
        defer { pending -= 1 }
        let token = UUID(); loadID = token; isLoading = true
        defer { if loadID == token { isLoading = false } }
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
                if nextQueue.entries.map(\.id) != queue.map(\.id) { queueNotice = nil }
                state = nextState; queue = nextQueue.entries
                if collectionCatalogueID == catalogueID { publishCollections(nextCollections) }
                platforms = nextPlatforms
                // Persistence never means playback. No prepare, resolve, play,
                // resume, audio-session activation or synthetic played event.
                if candidate == nil { position = nextState.position; phase = .paused }
                beginPlaybackMetadata(restoreOnly: candidate == nil)
                observeSimilarMode()
            }
        } catch { report(error, epoch: epoch, operation: "读取曲库与播放状态", kind: .library) }
    }

    private func stopSimilarObservation() {
        similarObservationID = UUID()
        similarObservation?.cancel(); similarObservation = nil
        similarMode = nil; canEnterSimilarMode = false; isChangingSimilarMode = false
    }
    private func observeSimilarMode() {
        guard similarObservation == nil, isAvailable, !isClosed,
              let provider = service as? any MusicSimilarModeServing else { return }
        let own = lifecycle, id = UUID(); similarObservationID = id
        similarObservation = Task { [weak self] in
            let events = await provider.similarModeChanges()
            for await _ in events {
                guard let self, !Task.isCancelled, self.lifecycle == own,
                      self.similarObservationID == id else { return }
                await self.loadSimilarMode()
            }
        }
    }
    public func loadSimilarMode(preview: Bool = false) async {
        guard isAvailable, !isClosed, let provider = service as? any MusicSimilarModeServing,
              let service else { return }
        observeSimilarMode()
        let own = lifecycle
        do {
            let value = try await (preview ? provider.previewSimilarMode() : provider.similarModeSnapshot())
            let canEnter = try await provider.canEnterSimilarMode()
            try check(own)
            similarMode = value; canEnterSimilarMode = canEnter
            // A recommendation batch may append without a UI queue action.
            // Refresh data only; notifications can never restart the player.
            if !isChangingQueue, !isNavigating, !isChangingSimilarMode {
                let next = try await service.state(), snapshot = try await service.queue()
                try check(own)
                if !isChangingQueue, !isNavigating, !isChangingSimilarMode,
                   next.revision == snapshot.revision, next.currentId == snapshot.currentId,
                   next.revision >= state.revision, next.settingsRevision >= state.settingsRevision {
                    if next.sourceCollectionID != state.sourceCollectionID || snapshot.entries.isEmpty { queueNotice = nil }
                    state = next; queue = snapshot.entries
                    if candidate == nil { position = next.position }
                }
            }
        } catch {
            guard lifecycle == own, isAvailable, !isClosed else { return }
            let failure = MusicServiceError.sanitized(error)
            if failure.isAuthenticationFailure || [.accessUnavailable, .staleAccess, .unsupported].contains(failure) {
                similarMode = nil; canEnterSimilarMode = false
            }
        }
    }
    private func performSimilarMode(
        _ operation: (any MusicSimilarModeServing) async throws -> MusicSimilarModeResult
    ) async throws {
        let own = try enter(); defer { pending -= 1 }
        guard !isChangingSimilarMode, !isChangingQueue, !isNavigating,
              let provider = service as? any MusicSimilarModeServing else { throw MusicServiceError.busy }
        isChangingSimilarMode = true
        defer { if lifecycle == own { isChangingSimilarMode = false } }
        cancelRemoteQueuePreparation(); cancelFMNavigation()
        await provider.pauseSimilarNavigation()
        await flushPlaybackFacts(); try check(own)
        let result = try await operation(provider)
        try check(own)
        stopFM()
        switch result.action {
        case .playCurrent, .restoreOnly: invalidateSelection(stopMedia: true)
        case .keepCurrent, .togglePlayback: break
        }
        state = result.state; queueNotice = nil
        try await refreshQueue(try requireService(), epoch: own)
        similarMode = try await provider.similarModeSnapshot()
        canEnterSimilarMode = try await provider.canEnterSimilarMode()
        try check(own)
        switch result.action {
        case .playCurrent: await prepareCurrent()
        case .togglePlayback: await togglePlayback()
        case .keepCurrent, .restoreOnly: break
        }
    }
    public func enterSimilarMode() async {
        let own = lifecycle
        do { try await performSimilarMode { try await $0.enterSimilarMode() } }
        catch { report(error, epoch: own) }
    }
    public func exitSimilarMode() async {
        let own = lifecycle
        do { try await performSimilarMode { try await $0.exitSimilarMode() } }
        catch { report(error, epoch: own) }
    }
    public func playSimilarCandidate(snapshotID: UUID, candidateID: String) async {
        let own = lifecycle
        do {
            try await performSimilarMode {
                try await $0.playSimilarCandidate(snapshotID: snapshotID, candidateID: candidateID)
            }
        } catch { report(error, epoch: own, operation: "播放相似歌曲") }
    }
    public func setSimilarContinue(_ enabled: Bool, snapshotID: UUID) async {
        let own = lifecycle
        guard isAvailable, !isClosed, let provider = service as? any MusicSimilarModeServing else { return }
        do {
            try await provider.setSimilarContinue(enabled, snapshotID: snapshotID)
            try check(own); await loadSimilarMode()
        } catch { report(error, epoch: own, operation: "保存推荐续播设置", kind: .settings) }
    }
    public func retrySimilarMode(snapshotID: UUID) async {
        let own = lifecycle
        do { try await performSimilarMode { try await $0.retrySimilarMode(snapshotID: snapshotID) } }
        catch { report(error, epoch: own) }
    }
    public func search(loadMore: Bool = false) async {
        let epoch: UUID
        do { epoch = try enter() } catch { return }
        defer { pending -= 1 }
        let platform = selectedPlatform, keyword = query
        let page = loadMore ? nextSearchPage : 1
        guard let page, !keyword.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else { return }
        if loadMore {
            guard !isSearching, searchContext?.platform == platform, searchContext?.query == keyword else { return }
        }
        let token = UUID(); searchID = token; searchTask?.cancel(); isSearching = true
        let trace = MusicRequestTrace(.search, platform: MusicPublicBrowsePlatform(rawValue: platform))
        trace.record(.started)
        defer { if searchID == token { isSearching = false; searchTask = nil } }
        do {
            let service = try requireService()
            let task = Task {
                try await MusicRequestTrace.$current.withValue(trace) {
                    try await service.search(platform: platform, query: keyword, page: page)
                }
            }
            searchTask = task
            let result = try await withTaskCancellationHandler { try await task.value } onCancel: { task.cancel() }
            try check(epoch)
            guard searchID == token, selectedPlatform == platform, query == keyword else { return }
            guard result.platform == platform, result.query == keyword, result.page == page else { throw MusicServiceError.invalidResponse }
            if loadMore {
                guard searchResults.count + result.items.count <= 600 else { throw MusicServiceError.limit }
                searchResults += result.items
            } else { searchResults = result.items }
            hasSearched = true; nextSearchPage = result.nextPage; searchContext = (platform, keyword)
            trace.record(.completed)
        } catch {
            guard searchID == token, selectedPlatform == platform, query == keyword else { trace.failed(error); return }
            report(error, epoch: epoch, operation: "搜索歌曲", kind: .search, trace: trace)
        }
    }
    public func collectionTracks(_ collection: MusicCollection) async throws -> [MusicRemoteTrack] {
        let epoch = try enter(); defer { pending -= 1 }
        let values = try await requireService().tracks(in: collection); try check(epoch); return values
    }

    // Page admissions are captured synchronously by MusicCollectionViewModel.
    // Keep the original public collection APIs for non-page callers.
    func observeCollectionPage(_ page: MusicCollectionViewModel, id: UUID) {
        collectionConsumers[id] = WeakMusicCollectionConsumer(page)
    }
    func removeCollectionPage(id: UUID) { collectionConsumers.removeValue(forKey: id) }
    private func notifyCollectionPages(_ change: MusicCollectionOwnerChange, origin: UUID? = nil) {
        collectionConsumers = collectionConsumers.filter { $0.value.value != nil }
        for observer in Array(collectionConsumers.values) { observer.value?.ownerChanged(change, origin: origin) }
    }
    private func publishCollections(_ values: [MusicCollection], origin: UUID? = nil) {
        collectionCatalogueID = UUID()
        collections = values; notifyCollectionPages(.catalogue(values), origin: origin)
    }
    func readCollectionPage(id: String, binding: MusicCollectionPageBinding) async throws -> MusicCollectionPageSnapshot? {
        try binding.check(self)
        let epoch = try enter(); defer { pending -= 1 }
        let service = try requireService()
        if let current = collections.first(where: { $0.id.utf8.elementsEqual(id.utf8) }) {
            do {
                let tracks = try await service.tracks(in: current)
                try check(epoch); try binding.check(self)
                guard tracks.count == current.count, tracks.count <= 20_000,
                      Set(tracks.map { Data($0.songId.utf8) }).count == tracks.count,
                      tracks.allSatisfy({ $0.ref.kind == .library && $0.ref.id.utf8.elementsEqual($0.songId.utf8) }) else {
                    throw MusicServiceError.invalidResponse
                }
                return .init(collection: current, tracks: tracks)
            } catch {
                try check(epoch); try binding.check(self)
                guard [.notFound, .staleRevision].contains(MusicServiceError.sanitized(error)) else { throw error }
            }
        }
        let catalogueID = collectionCatalogueID
        let values = try await service.collections()
        try check(epoch); try binding.check(self)
        guard values.count <= 2000, Set(values.map { Data($0.id.utf8) }).count == values.count else {
            throw MusicServiceError.invalidResponse
        }
        guard let collection = values.first(where: { $0.id.utf8.elementsEqual(id.utf8) }) else {
            if collectionCatalogueID == catalogueID { publishCollections(values, origin: binding.id) }
            return nil
        }
        let tracks = try await service.tracks(in: collection)
        try check(epoch); try binding.check(self)
        guard tracks.count == collection.count, tracks.count <= 20_000,
              Set(tracks.map { Data($0.songId.utf8) }).count == tracks.count,
              tracks.allSatisfy({ $0.ref.kind == .library && $0.ref.id.utf8.elementsEqual($0.songId.utf8) }) else {
            throw MusicServiceError.invalidResponse
        }
        if collectionCatalogueID == catalogueID { publishCollections(values, origin: binding.id) }
        return .init(collection: collection, tracks: tracks)
    }
    func editCollectionPage(_ ticket: MusicCollectionEditTicket, name: String?,
                            refs: [MusicReference]?, order: [String]?) async throws {
        try ticket.check(self)
        let epoch = try enter(); defer { pending -= 1 }
        // Do not let an earlier root load overwrite the post-edit catalogue.
        loadID = UUID(); isLoading = false
        let service = try requireService()
        let changed = try await service.editPlaylist(ticket.collection, name: name, refs: refs, order: order)
        try check(epoch); try ticket.check(self)
        guard changed.id.utf8.elementsEqual(ticket.collection.id.utf8),
              changed.revision > ticket.collection.revision else { throw MusicServiceError.invalidResponse }
        let values = try await service.collections()
        try check(epoch); try ticket.check(self); publishCollections(values)
    }
    func addCollectionPage(_ ticket: MusicCollectionEditTicket, ref: MusicReference, to target: MusicCollection) async throws {
        try ticket.check(self)
        let epoch = try enter(); defer { pending -= 1 }
        let service = try requireService(), tracks = try await service.tracks(in: target)
        try check(epoch); try ticket.check(self)
        loadID = UUID(); isLoading = false
        _ = try await service.editPlaylist(target, name: nil, refs: tracks.map(\.ref) + [ref], order: nil)
        try check(epoch); try ticket.check(self)
        let values = try await service.collections()
        try check(epoch); try ticket.check(self); publishCollections(values)
    }
    func queueCollectionPage(_ ticket: MusicCollectionEditTicket, change: MusicQueueChange, startPlayback: Bool) async throws {
        try ticket.check(self)
        await queueAction(change, startPlayback: startPlayback, collectionTicket: ticket)
        try ticket.check(self)
    }

    private func refreshQueue(_ service: any MusicServing, epoch: UUID, selection: UUID? = nil) async throws {
        let snapshot = try await service.queue(); try check(epoch)
        if let selection, selection != selectionID { throw MusicServiceError.staleResult }
        guard snapshot.revision == state.revision, snapshot.currentId == state.currentId else { throw MusicServiceError.staleRevision }
        queue = snapshot.entries; position = state.position
        if queue.isEmpty { queueNotice = nil }
    }
    private func queueAction(_ change: MusicQueueChange, startPlayback: Bool,
                             collectionTicket: MusicCollectionEditTicket? = nil,
                             expectedQueueRevision: Int? = nil) async {
        do { try collectionTicket?.check(self) } catch { return }
        cancelRemoteQueuePreparation()
        cancelFMNavigation()
        let epoch: UUID
        do { epoch = try enter() } catch { return }
        defer { pending -= 1 }
        guard !isChangingQueue, !isNavigating else {
            report(MusicServiceError.busy, epoch: epoch, operation: "更新播放队列", kind: .queue); return
        }
        isChangingQueue = true
        defer { if lifecycle == epoch { isChangingQueue = false } }
        let token = selectionID
        do {
            await (service as? any MusicSimilarModeServing)?.pauseSimilarNavigation()
            await flushPlaybackFacts(); try check(epoch); try collectionTicket?.check(self)
            let service = try requireService(), before = try await service.state()
            try check(epoch); try collectionTicket?.check(self); guard token == selectionID else { return }
            if let expectedQueueRevision, before.revision != expectedQueueRevision {
                let latest = try await service.queue()
                try check(epoch)
                guard token == selectionID else { return }
                if latest.revision == before.revision, latest.currentId == before.currentId {
                    if before.currentId != state.currentId { invalidateSelection(stopMedia: true) }
                    state = before; queue = latest.entries
                }
                throw MusicServiceError.revisionConflict
            }
            let next: MusicPlaybackState
            if let ticket = collectionTicket, case .replace(let refs, let index) = change,
               let provider = service as? any MusicCollectionQueueServing {
                next = try await provider.changeCollectionQueue(collection: ticket.collection, refs: refs,
                    index: index, expectedRevision: before.revision)
            } else { next = try await service.changeQueue(change, expectedRevision: before.revision) }
            try check(epoch); try collectionTicket?.check(self); guard token == selectionID else { return }
            switch change {
            case .replace, .collection, .clear: stopFM(); queueNotice = nil
            default: if next.total == 0 { stopFM() }
            }
            if startPlayback || next.currentId != before.currentId { invalidateSelection(stopMedia: true) }
            state = next; try await refreshQueue(service, epoch: epoch)
            try collectionTicket?.check(self)
            if startPlayback { await prepareCurrent() }
        } catch {
            if let collectionTicket, !collectionTicket.binding.matches(self) { return }
            report(error, epoch: epoch, operation: "更新播放队列", kind: startPlayback ? .playback : .queue)
        }
    }
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
    private func capturePrimaryPlayback() {
        guard let intent = MusicPrimaryPlaybackContext.intent,
              primaryPlayback?.intent == intent, let current, let nativeAttempt else { return }
        primaryPlayback?.receipt = .init(occurrenceID: current.id,
            nativeAttemptID: nativeAttempt.id, accessGeneration: accountAccessGeneration)
        completePrimaryPlaybackIfPlaying()
    }
    private func completePrimaryPlaybackIfPlaying() {
        guard phase == .playing, let pending = primaryPlayback, let receipt = pending.receipt,
              current?.id == receipt.occurrenceID, nativeAttempt?.id == receipt.nativeAttemptID,
              accountAccessGeneration == receipt.accessGeneration else { return }
        _ = pending.presentation.finishHomePlayback(pending.intent, playback: receipt)
        primaryPlayback = nil
    }
    /// A search section/playlist preview is one queue transaction, not a series
    /// of single-song plays that replace each other or lose occurrence order.
    public func play(_ tracks: [MusicRemoteTrack], index: Int) async {
        guard (1...2000).contains(tracks.count), tracks.indices.contains(index) else {
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
        let epoch = try enter(); defer { pending -= 1 }
        guard let remoteQueueService else { throw MusicServiceError.unavailable }
        let source = try await remoteQueueService.openAccountQueue(platform: platform, kind: kind, title: title)
        do { try check(epoch); return source }
        catch { source.revoke(); await source.close(); throw error }
    }
    public func openHomeActionQueue(_ action: MusicHomeAction, title: String) async throws -> MusicRemoteQueueSource {
        let epoch = try enter(); defer { pending -= 1 }
        guard let provider = service as? any MusicHomeActionQueueServing else { throw MusicServiceError.unsupported }
        let source = try await provider.openHomeActionQueue(action, title: title)
        do { try check(epoch); return source }
        catch { source.revoke(); await source.close(); throw error }
    }
    public func playHomeAction(_ action: MusicHomeAction, title: String, forceReload: Bool = false) async throws {
        switch action {
        case .similar(let ids, let name):
            try await performSimilarMode { try await $0.startSimilarMode(.songRoaming(sourceIDs: ids, sourceName: name)) }
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
        let first = try await remoteQueuePage(source, cursor: nil)
        try Task.checkCancellation()
        guard let track = first.items.first else { throw MusicServiceError.notFound }
        await playRemoteQueue(source, start: .init(index: 0, track: track))
        if fmSource?.id == source.id { activeHomeFM = action }
    }
    /// Only the current occurrence of this exact live radio session can supply
    /// an entrance cover; an inserted/selected song is not implicitly an FM song.
    func currentFMArtwork(_ action: MusicHomeAction) -> URL? {
        guard action.isPersonalFM, activeHomeFM?.fmConfiguration == action.fmConfiguration,
              let fmSource, fmSource.authorization.isActive, current?.remoteSessionID == fmSource.id,
              !isPreparingRemoteQueue, phase != .failed else { return nil }
        return displayTrack?.artwork
    }
    public func remoteQueuePage(_ source: MusicRemoteQueueSource, cursor: String?) async throws -> MusicBrowsePage {
        let epoch = try enter(); defer { pending -= 1 }
        guard let remoteQueueService else { throw MusicServiceError.unavailable }
        let page = try await remoteQueueService.remoteQueuePage(source, cursor: cursor)
        try check(epoch); try source.authorization.check(); return page
    }
    public func filteredRemoteQueue(_ source: MusicRemoteQueueSource, query: String) async throws -> MusicRemoteQueueSource {
        let epoch = try enter(); defer { pending -= 1 }
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
        let epoch = try enter(); defer { pending -= 1 }
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
    private func playPreparedQueue(_ prepare: @escaping @Sendable
        (any MusicServing, Int, MusicQueueAuthorization) async throws -> MusicPlaybackState) async {
        cancelRemoteQueuePreparation(); cancelFMNavigation()
        let own: UUID
        do { own = try enter() } catch { return }
        defer { pending -= 1 }
        guard !isChangingQueue, !isNavigating, let service, player != nil else {
            report(MusicServiceError.busy, epoch: own); return
        }
        let request = UUID(), intent = MusicQueueAuthorization(), selection = selectionID
        remoteQueueRequest = request; remoteQueueIntent = intent
        isPreparingRemoteQueue = true; isChangingQueue = true
        defer {
            intent.revoke()
            if remoteQueueRequest == request {
                remoteQueueTask = nil; remoteQueueIntent = nil; remoteQueueSourceID = nil
                isPreparingRemoteQueue = false; isChangingQueue = false
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
            state = next; queueNotice = nil
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
        cancelRemoteQueuePreparation(); cancelFMNavigation()
        let epoch: UUID
        do { epoch = try enter() } catch { return }
        defer { pending -= 1 }
        guard !isChangingQueue, !isNavigating, let service, let remoteQueueService, player != nil else {
            report(MusicServiceError.busy, epoch: epoch); return
        }
        let request = UUID(), intent = MusicQueueAuthorization(), selection = selectionID
        remoteQueueRequest = request; remoteQueueIntent = intent; remoteQueueSourceID = source.id
        remoteQueueIsFM = source.context.kind == .fm
        if remoteQueueIsFM { fmShouldPlay = true }
        isPreparingRemoteQueue = true; isChangingQueue = true
        defer {
            intent.revoke()
            if remoteQueueRequest == request {
                remoteQueueTask = nil; remoteQueueIntent = nil; remoteQueueSourceID = nil
                remoteQueueIsFM = false; isPreparingRemoteQueue = false; isChangingQueue = false
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
            state = next; try await refreshQueue(service, epoch: epoch, selection: committedSelection)
            queueNotice = source.context.availabilityNotice
            try admission.check()
            if source.context.kind != .fm || fmShouldPlay {
                await prepareCurrent(forceSelection: false, automatic: false, admission: admission)
            } else { phase = .paused }
        } catch {
            if remoteQueueRequest == request { report(error, epoch: epoch) }
        }
    }
    public func cancelRemoteQueuePreparation() {
        guard isPreparingRemoteQueue || remoteQueueIntent != nil || remoteQueueTask != nil || remoteQueueSourceID != nil else { return }
        let wasPreparing = isPreparingRemoteQueue
        remoteQueueIntent?.revoke(); remoteQueueIntent = nil
        remoteQueueTask?.cancel(); remoteQueueTask = nil
        remoteQueueSourceID = nil; remoteQueueIsFM = false; remoteQueueRequest = UUID()
        isPreparingRemoteQueue = false
        if wasPreparing { isChangingQueue = false }
    }
    /// On page departure an admitted FM transfers to the product-lifetime model.
    /// Uncommitted actions and non-FM browsing scopes are synchronously revoked.
    public func releaseRemoteQueueSource(_ source: MusicRemoteQueueSource) {
        if fmSource?.id == source.id { return }
        if remoteQueueSourceID == source.id { cancelRemoteQueuePreparation() }
        source.revoke(); Task { await source.close() }
    }
    private func activateFM(_ source: MusicRemoteQueueSource) {
        if fmSource?.id == source.id { return }
        stopFM(); fmSource = source; isFMActive = true
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
    private func stopFM() {
        activeHomeFM = nil
        fmRevocationTask?.cancel(); fmRevocationTask = nil
        fmAdvanceIntent?.revoke(); fmAdvanceIntent = nil
        let source = fmSource; fmSource = nil; isFMActive = false
        if let source { source.revoke(); Task { await source.close() } }
    }
    private func cancelFMNavigation() {
        guard fmAdvanceIntent != nil else { return }
        fmAdvanceIntent?.revoke(); fmAdvanceIntent = nil
        navigationTask?.cancel(); navigationTask = nil
        navigationID = UUID(); isNavigating = false; isChangingQueue = false
    }
    private func retirePlaybackNavigation() {
        let ownsQueue = navigationQueueID == navigationID
        navigationTask?.cancel(); navigationTask = nil
        navigationID = UUID(); isNavigating = false
        if ownsQueue { navigationQueueID = nil; isChangingQueue = false }
        // Keep selection/fact delivery intact: an exit still owns its history,
        // but neither an old queue acknowledgement nor an ended fact may play.
    }

    public func next() async { await advance(.next, reason: .manual) }
    public func previous() async { await advance(.previous, reason: .manual) }
    private func advance(_ direction: MusicDirection, reason: MusicAdvanceReason,
                         onSelection: (@MainActor (MusicQueueEntry) -> Void)? = nil) async {
        let epoch: UUID
        do { epoch = try enter() } catch { return }
        defer { pending -= 1 }
        guard !isChangingQueue else { return }
        isChangingQueue = true
        let navigation = navigationID
        navigationQueueID = navigation
        defer {
            if navigationQueueID == navigation { navigationQueueID = nil }
            if lifecycle == epoch, navigationID == navigation { isChangingQueue = false }
        }
        let token = selectionID
        let fm = fmSource
        let intent = MusicQueueAuthorization()
        if fm != nil { fmAdvanceIntent = intent; fmShouldPlay = true }
        defer { intent.revoke(); if fmAdvanceIntent === intent { fmAdvanceIntent = nil } }
        do {
            await flushPlaybackFacts(); try check(epoch)
            guard navigationID == navigation else { return }
            let service = try requireService(), before = try await service.state()
            try check(epoch); guard navigationID == navigation, token == selectionID else { return }
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
            try check(epoch); guard navigationID == navigation, token == selectionID else { return }
            if result.selectedId != nil { invalidateSelection(stopMedia: true, preservingQueueIntent: intent) }
            let committedSelection = selectionID
            let admission = fm.map { QueuePlaybackAdmission(source: $0, intent: intent) }
            state = result.state; try await refreshQueue(service, epoch: epoch, selection: committedSelection)
            try admission?.check()
            guard navigationID == navigation else { return }
            if let selected = result.selectedId, let entry = current, entry.id == selected {
                // A visual transition receives the actual committed occurrence,
                // before URL resolution. It never selects a preview itself.
                onSelection?(entry)
            }
            if result.selectedId != nil, fm == nil || fmShouldPlay {
                await prepareCurrent(forceSelection: false, automatic: reason == .ended, admission: admission)
            }
            else if result.selectedId != nil { phase = .paused }
        } catch { if navigationID == navigation { report(error, epoch: epoch) } }
    }
    /// Synchronous admission for the product's system-command closures.
    /// True means accepted/scheduled, never "audio already played".
    public func acceptNext(_ attempt: PlayAttempt) -> Bool { acceptNavigation(attempt, direction: .next) }
    public func acceptPrevious(_ attempt: PlayAttempt) -> Bool { acceptNavigation(attempt, direction: .previous) }
    private func acceptNavigation(_ attempt: PlayAttempt, direction: MusicDirection) -> Bool {
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
        let token = UUID(); navigationID = token; isNavigating = true
        navigationTask = Task { [weak self] in
            guard let self, !Task.isCancelled, self.navigationID == token else { return }
            await self.advance(direction, reason: reason, onSelection: onSelection)
            if self.navigationID == token { self.navigationTask = nil; self.isNavigating = false }
            onCompletion?()
        }
        return true
    }

    /// Explicit play uses business automatic candidates when supported.
    /// `forceSelection` opens the existing exact-target picker instead.
    public func prepareCurrent(forceSelection: Bool = false) async {
        await prepareCurrent(forceSelection: forceSelection, automatic: false)
    }
    private func prepareCurrent(forceSelection: Bool, automatic: Bool, admission: QueuePlaybackAdmission? = nil) async {
        let trace = MusicRequestTrace(.playback,
            platform: current?.track.sources.first.flatMap { MusicPublicBrowsePlatform(rawValue: $0.platform) })
        trace.record(.started)
        await MusicRequestTrace.$current.withValue(trace) {
            await prepareCurrentRequest(forceSelection: forceSelection, automatic: automatic, admission: admission)
        }
        trace.record(Task.isCancelled ? .cancelled : .completed)
    }
    private func prepareCurrentRequest(forceSelection: Bool, automatic: Bool, admission: QueuePlaybackAdmission?) async {
        let admission = admission ?? fmSource.map { QueuePlaybackAdmission(source: $0, intent: nil) }
        do { try admission?.check() } catch { report(error, epoch: lifecycle); return }
        let epoch: UUID
        do { epoch = try enter() } catch { return }
        defer { pending -= 1 }
        guard let current else { return }
        if forceSelection, service is any MusicResolvedSourcesServing {
            // Inspection never cancels the current resolver/player and never
            // counts declared qualities as successfully resolved resources.
            showingSources = true
            return
        }
        // Ordinary play/resume/navigation explicitly opts into the music
        // business policy, independently of the feedback `automatic` flag.
        // A picker/manual request never enters this path, even after failure.
        if !forceSelection, let provider = service as? any MusicCandidateDiscoveryServing {
            await prepareAutomaticCandidate(provider, epoch: epoch, automatic: automatic, admission: admission)
            return
        }
        if isResolving {
            invalidateSelection(stopMedia: true, preservingQueueIntent: admission?.intent)
        }
        let token = selectionID
        do {
            let service = try requireService()
            let result: [MusicPlaybackOption]
            var scriptError: String?
            if let provider = service as? any MusicSourceAvailabilityProviding {
                let availability = try await provider.sourceAvailability(for: current.track.ref)
                result = availability.options
                if case .unavailable(let failure) = availability.scriptState {
                    scriptError = failure.localizedDescription
                }
            } else {
                result = try await service.playbackOptions(for: current.track.ref)
            }
            try check(epoch); try admission?.check()
            guard token == selectionID, self.current?.id == current.id else { return }
            if admission?.source?.context.kind == .fm, admission?.intent != nil, !fmShouldPlay {
                phase = .paused; return
            }
            options = result; scriptSourceError = scriptError
            if !forceSelection, let selected = lastSelection {
                let matches = result.filter {
                    $0.installationId == selected.installationId && $0.generation == selected.generation &&
                    $0.siteId == selected.siteId && $0.sourceKey == selected.sourceKey &&
                    $0.qualities.contains(selected.quality)
                }
                if matches.count == 1 {
                    await choose(matches[0].selection(quality: selected.quality), automatic: automatic, admission: admission); return
                }
            }
            // No global current script, fallback or "first enabled" selection.
            showingSources = true
            if result.isEmpty { report(MusicServiceError.sourceUnavailable, epoch: epoch) }
        } catch { report(error, epoch: epoch) }
    }

    private func checkSelection(_ epoch: UUID, token: UUID, occurrenceID: String,
                                admission: QueuePlaybackAdmission?) throws {
        try check(epoch); try admission?.check()
        guard token == selectionID, current?.id == occurrenceID else { throw MusicServiceError.staleResult }
    }

    /// A discovery CAS may only enrich the current recording, once. Read both
    /// snapshots before publishing either, also on exhaustion after a commit.
    /// Unrelated queue/settings mutations are not permission to play the result.
    private func refreshCandidateQueue(_ service: any MusicServing, epoch: UUID, token: UUID,
        before: MusicPlaybackState, entries: [MusicQueueEntry], admission: QueuePlaybackAdmission?) async throws {
        guard let occurrenceID = before.currentId else { throw MusicServiceError.notFound }
        let next = try await service.state()
        try checkSelection(epoch, token: token, occurrenceID: occurrenceID, admission: admission)
        let snapshot = try await service.queue()
        try checkSelection(epoch, token: token, occurrenceID: occurrenceID, admission: admission)
        guard next.revision == snapshot.revision, next.currentId == snapshot.currentId,
              next.currentId == occurrenceID, next.settingsRevision == before.settingsRevision,
              next.settingsRevision >= state.settingsRevision,
              next.revision >= before.revision, next.revision - before.revision <= 1,
              next.revision >= state.revision, snapshot.entries.map(\.id) == entries.map(\.id),
              next.total == snapshot.entries.count else { throw MusicServiceError.staleRevision }
        for (old, new) in zip(entries, snapshot.entries) {
            if old.id != occurrenceID {
                guard old == new else { throw MusicServiceError.targetChanged }
            } else {
                let a = old.track, b = new.track
                guard a.ref == b.ref, a.songId == b.songId, a.title == b.title, a.artist == b.artist,
                      a.album == b.album, a.duration == b.duration else { throw MusicServiceError.targetChanged }
            }
        }
        state = next; queue = snapshot.entries; position = next.position
    }

    private func prepareAutomaticCandidate(_ provider: any MusicCandidateDiscoveryServing, epoch: UUID,
        automatic: Bool, admission: QueuePlaybackAdmission?, useAudioCache: Bool = true) async {
        guard let current, let service, player != nil else { return }
        do { try admission?.check() } catch { report(error, epoch: epoch); return }
        let before = state, entries = queue, oldAttempt = resolvingAttempt ?? candidate?.attemptId
        invalidateSelection(stopMedia: true, preservingQueueIntent: admission?.intent)
        feedbackAutomatic = automatic
        let token = selectionID
        isResolving = true; error = nil
        beginPlaybackMetadata()
        var returnedAttempt: String?
        var accepted = false
        defer {
            if selectionID == token {
                isResolving = false; candidateDiscoveryTask = nil; resolvingAttempt = nil
            }
        }
        do {
            if let oldAttempt { try await service.cancel(attemptId: oldAttempt) }
            try checkSelection(epoch, token: token, occurrenceID: current.id, admission: admission)
            if useAudioCache, try await startCachedAudio(selection: nil, automatic: automatic,
                epoch: epoch, token: token, occurrenceID: current.id, admission: admission) { return }
            // One scoped consumer per request. Its synchronous Task cancellation
            // cannot become a delayed actor cancel of a newer selection.
            let consumer = MusicCandidateDiscoveryConsumer(provider: provider)
            let task = Task {
                try await consumer.resolve(occurrenceID: current.id, expectedQueueRevision: before.revision,
                    mode: .automatic())
            }
            candidateDiscoveryTask = task
            let outcome: Result<MusicCandidateDiscoveryResolution, Error>
            do {
                let result = try await withTaskCancellationHandler { try await task.value } onCancel: { task.cancel() }
                returnedAttempt = result.candidate.attemptId
                outcome = .success(result)
            } catch { outcome = .failure(error) }
            try checkSelection(epoch, token: token, occurrenceID: current.id, admission: admission)
            resolvingAttempt = returnedAttempt
            try await refreshCandidateQueue(service, epoch: epoch, token: token, before: before,
                entries: entries, admission: admission)
            let result = try outcome.get()
            guard self.current?.track.sources.contains(where: {
                $0.sourceKey == result.target.selection.sourceKey && $0.platform == result.target.selection.siteId
            }) == true else { throw MusicServiceError.targetChanged }
            accepted = try acceptResolvedCandidate(result.candidate, selection: result.target.selection,
                attemptID: result.candidate.attemptId, occurrenceID: current.id, epoch: epoch, token: token, admission: admission)
            if accepted { beginPlaybackMetadata(fallbackSourceKey: result.target.selection.sourceKey) }
        } catch {
            if selectionID == token {
                if !accepted { clearUnacceptedCandidate(returnedAttempt) }
                if admission?.intent?.isActive != false {
                    if let failure = error as? MusicCandidateDiscoveryFailure { report(failure.failure, epoch: epoch) }
                    else { report(error, epoch: epoch) }
                }
            }
        }
        // The consumer retains a successful attempt for the existing fact owner.
        // If UI admission/refresh rejects it, release exactly that attempt, never
        // cancel(nil) or a potentially newer player.
        if !accepted, let returnedAttempt { await releaseUnacceptedAttempt(returnedAttempt, service: service) }
    }

    /// Shared by manual resolution and business automatic discovery. The input
    /// is already resolved: this function must never invoke another resolver.
    private func acceptResolvedCandidate(_ value: MusicPlaybackCandidate, selection: MusicPlaybackSelection,
        attemptID: String, occurrenceID: String, epoch: UUID, token: UUID,
        admission: QueuePlaybackAdmission?, replacement: SourceReplacementIntent? = nil) throws -> Bool {
        try checkSelection(epoch, token: token, occurrenceID: occurrenceID, admission: admission)
        guard let player else { throw MusicServiceError.unavailable }
        guard value.attemptId == attemptID, value.occurrenceId == occurrenceID,
              value.resourceId == "music:" + attemptID, value.requestedQuality == selection.quality else {
            throw MusicServiceError.invalidResponse
        }
        if let expiry = value.expiresAt, expiry <= Date().timeIntervalSince1970 * 1000 {
            throw MusicServiceError.invalidMediaCandidate
        }
        let resource = try value.resource()
        if admission?.source?.context.kind == .fm, admission?.intent != nil, !fmShouldPlay {
            phase = .paused; return false
        }
        candidate = value; lastSelection = selection; terminalAttempt = nil; lastProgressWrite = nil
        phase = .preparing
        // Native preparation/failure callbacks may synchronously re-enter.
        pendingNativeResourceID = value.resourceId
        defer { if selectionID == token { pendingNativeResourceID = nil } }
        let started: MusicPlayerAttempt
        let start = {
            let play = { player.play(resource) }
            if let admission { return try admission.perform(play) }
            return play()
        }
        if let authorization = value.playbackAuthorization ?? value.fileAuthorization { started = try authorization.perform(start) }
        else { started = try start() }
        do {
            try checkSelection(epoch, token: token, occurrenceID: occurrenceID, admission: admission)
            try value.fileAuthorization?.check()
            try value.playbackAuthorization?.check()
            guard candidate?.attemptId == attemptID, started.resourceID == value.resourceId,
                  nativeAttempt == nil || nativeAttempt == started else { throw MusicServiceError.invalidResponse }
        } catch { player.stop(started); throw error }
        nativeAttempt = started
        if let replacement, replacement.occurrenceID == occurrenceID {
            if replacement.position.isFinite, replacement.position > 0,
               abs(replacement.position - value.startPosition) > 0.25 {
                _ = player.seek(replacement.position, attempt: started)
            }
            if replacement.remainsPaused { player.pause(started) }
        }
        observeAudioAuthorization(value, native: started)
        capturePrimaryPlayback()
        isResolving = false; resolvingAttempt = nil
        return true
    }

    private func clearUnacceptedCandidate(_ attemptID: String?) {
        pendingNativeResourceID = nil
        if let attemptID, candidate?.attemptId == attemptID {
            stopOwned()
        }
        if candidate == nil { phase = .paused }
    }

    private func releaseUnacceptedAttempt(_ id: String, service: any MusicServing) async {
        // Cleanup must not inherit a cancelled caller: NativeMusicService checks
        // cancellation on its public port. A revoked service clears its attempts
        // at its own protection fence. This finite exact-ID cleanup is awaited.
        await Task { try? await service.cancel(attemptId: id) }.value
    }

    public func choose(_ selection: MusicPlaybackSelection) async {
        await choose(selection, automatic: false)
    }
    func inspectSources(for ref: MusicReference) async throws -> any MusicSourceInspectionSession {
        try check(lifecycle)
        guard let provider = service as? any MusicResolvedSourcesServing else { throw MusicServiceError.unsupported }
        let own = lifecycle
        let session = try await provider.inspectSources(for: ref)
        do { try check(own); return session }
        catch { await session.close(); throw error }
    }
    func chooseInspectedSource(_ source: MusicResolvedSource, session: any MusicSourceInspectionSession) async {
        guard let current, let service else { return }
        let own = lifecycle, token = selectionID, before = state, entries = queue
        let admission = fmSource.map { QueuePlaybackAdmission(source: $0, intent: nil) }
        do {
            try checkSelection(own, token: token, occurrenceID: current.id, admission: admission)
            let selection = try await session.adopt(sourceID: source.id, occurrenceID: current.id,
                                                     expectedQueueRevision: before.revision)
            try checkSelection(own, token: token, occurrenceID: current.id, admission: admission)
            try await refreshCandidateQueue(service, epoch: own, token: token, before: before,
                                            entries: entries, admission: admission)
            guard selection == source.selection else { throw MusicServiceError.targetChanged }
            // Adoption verifies/enriches the recording; it is not permission to
            // bypass complete local bytes for this explicitly selected quality.
            await choose(selection, automatic: false, admission: admission, useAudioCache: true, keepSources: true)
        } catch { report(error, epoch: own) }
    }
    private func choose(_ selection: MusicPlaybackSelection, automatic: Bool, admission: QueuePlaybackAdmission? = nil,
                        useAudioCache: Bool = true, keepSources: Bool = false) async {
        let trace = MusicRequestTrace(.audio, platform: MusicPublicBrowsePlatform(rawValue: selection.siteId))
        trace.record(.started)
        await MusicRequestTrace.$current.withValue(trace) {
            await chooseRequest(selection, automatic: automatic, admission: admission,
                                useAudioCache: useAudioCache, keepSources: keepSources)
        }
        trace.record(Task.isCancelled ? .cancelled : .completed)
    }
    private func chooseRequest(_ selection: MusicPlaybackSelection, automatic: Bool, admission: QueuePlaybackAdmission?,
                               useAudioCache: Bool, keepSources: Bool) async {
        let admission = admission ?? fmSource.map { QueuePlaybackAdmission(source: $0, intent: nil) }
        let epoch: UUID
        do { epoch = try enter() } catch { return }
        defer { pending -= 1 }
        guard let current, player != nil else { return }
        let oldAttempt = resolvingAttempt ?? candidate?.attemptId
        let replacement = nativeAttempt == nil ? nil : SourceReplacementIntent(
            occurrenceID: current.id, position: position,
            remainsPaused: phase == .paused)
        do { try admission?.check() } catch { report(error, epoch: epoch); return }
        invalidateSelection(stopMedia: true, preservingQueueIntent: admission?.intent, keepSources: keepSources)
        feedbackAutomatic = automatic
        let token = selectionID, attemptId = UUID().uuidString.lowercased()
        let settingsRevision = state.settingsRevision
        beginPlaybackMetadata(fallbackSourceKey: selection.sourceKey)
        var accepted = false
        isResolving = true; resolvingAttempt = attemptId
        if !keepSources { showingSources = false }
        defer { if selectionID == token { isResolving = false; resolutionTask = nil; resolvingAttempt = nil } }
        do {
            let service = try requireService()
            if let oldAttempt { try await service.cancel(attemptId: oldAttempt) }
            try check(epoch); try admission?.check(); guard selectionID == token else { return }
            if useAudioCache, try await startCachedAudio(selection: selection, automatic: automatic,
                epoch: epoch, token: token, occurrenceID: current.id, admission: admission,
                replacement: replacement) { return }
            let task = Task { try await service.resolve(attemptId: attemptId, occurrenceId: current.id, selection: selection) }
            resolutionTask = task
            let value = try await withTaskCancellationHandler { try await task.value } onCancel: { task.cancel() }
            guard settingsRevision == state.settingsRevision else { throw MusicServiceError.staleRevision }
            accepted = try acceptResolvedCandidate(value, selection: selection, attemptID: attemptId,
                occurrenceID: current.id, epoch: epoch, token: token, admission: admission,
                replacement: replacement)
            if accepted { beginPlaybackMetadata(fallbackSourceKey: selection.sourceKey) }
        } catch {
            if selectionID == token {
                if !accepted { clearUnacceptedCandidate(attemptId) }
                if admission?.intent?.isActive != false { report(error, epoch: epoch) }
            }
        }
        if !accepted, let service { await releaseUnacceptedAttempt(attemptId, service: service) }
    }

    /// Both automatic and pinned requests check complete bytes before obtaining
    /// any platform grant. A cache hit is still admitted by the same player and
    /// playback-fact owner; it never becomes an offline-library occurrence.
    private func startCachedAudio(selection: MusicPlaybackSelection?, automatic: Bool, epoch: UUID,
        token: UUID, occurrenceID: String, admission: QueuePlaybackAdmission?,
        replacement: SourceReplacementIntent? = nil) async throws -> Bool {
        guard let service, let cache = service as? any MusicCachedPlaybackServing else { return false }
        let attemptID = UUID().uuidString.lowercased(), settingsRevision = state.settingsRevision
        let task = Task { try await cache.cachedPlayback(attemptID: attemptID,
            occurrenceID: occurrenceID, selection: selection) }
        cachedAudioLookup = task
        var hit: MusicCachedPlayback?, accepted = false
        do {
            hit = try await withTaskCancellationHandler { try await task.value } onCancel: { task.cancel() }
            try checkSelection(epoch, token: token, occurrenceID: occurrenceID, admission: admission)
            guard state.settingsRevision == settingsRevision else { throw MusicServiceError.staleRevision }
            guard let hit else {
                MusicRequestTrace(.media).record(.cacheMiss)
                cachedAudioLookup = nil; return false
            }
            MusicRequestTrace(.media).record(.cacheHit)
            guard hit.candidate.isCachedAudio, hit.candidate.url.isFileURL,
                  selection == nil || selection == hit.selection else { throw MusicServiceError.invalidResponse }
            cachedAudioRecovery = CachedAudioRecovery(attemptID: attemptID, selection: selection,
                automatic: automatic, admission: admission)
            accepted = try acceptResolvedCandidate(hit.candidate, selection: hit.selection,
                attemptID: attemptID, occurrenceID: occurrenceID, epoch: epoch, token: token,
                admission: admission, replacement: replacement)
            if accepted { beginPlaybackMetadata(fallbackSourceKey: hit.selection.sourceKey) }
            else { await releaseUnacceptedAttempt(attemptID, service: service) }
            if selectionID == token { cachedAudioLookup = nil }
            return true
        } catch {
            if selectionID == token {
                cachedAudioLookup = nil
                if !accepted { cachedAudioRecovery = nil; clearUnacceptedCandidate(attemptID) }
            }
            // A suspended lookup can return an accepted service attempt after UI
            // selection changed. Release that exact attempt, never the new one.
            if hit != nil, !accepted { await releaseUnacceptedAttempt(attemptID, service: service) }
            throw error
        }
    }

    private func observeAudioAuthorization(_ value: MusicPlaybackCandidate, native: MusicPlayerAttempt) {
        cachedAudioObservation?.cancel(); cachedAudioObservation = nil
        guard let authorization = value.playbackAuthorization ?? value.fileAuthorization else { return }
        let events = authorization.revocations()
        cachedAudioObservation = Task { [weak self] in
            for await _ in events {
                // Protection can hide UI state and clear the candidate while
                // the already admitted remote stream keeps playing. Its
                // account fence still owns THIS native attempt, not the page
                // lifecycle. Never stop a newer admitted attempt.
                guard !Task.isCancelled, let self, self.nativeAttempt == native else { return }
                self.invalidateSelection(stopMedia: true)
                if self.isAvailable { self.error = MusicServiceError.staleAccess.localizedDescription }
                if let service = self.service {
                    await self.releaseUnacceptedAttempt(value.attemptId, service: service)
                }
                return
            }
        }
    }

    /// A failed cache file is not proof the song is unavailable. Preserve the
    /// occurrence and position, evict only this file, then retry the original
    /// automatic/pinned route once with file lookup disabled.
    private func recoverCachedAudio(_ fact: MusicPlaybackFact) -> Bool {
        guard fact.kind == .failed, let recovery = cachedAudioRecovery,
              recovery.attemptID == fact.attemptId, candidate?.isCachedAudio == true,
              recoveryDrains.count < 8,
              let service, let cache = service as? any MusicCachedPlaybackRecoveryServing else { return false }
        cachedAudioRecovery = nil; terminalAttempt = fact.attemptId; isResolving = true
        cachedAudioObservation?.cancel(); cachedAudioObservation = nil
        let epoch = lifecycle, token = selectionID, previous = eventTail, before = state, entries = queue
        let drainID = UUID()
        let task = Task { [weak self] in
            await previous?.value
            guard let self else { return }
            var result: MusicPlaybackRecovery?, accepted = false
            defer {
                self.recoveryDrains.removeValue(forKey: drainID)
                if self.lifecycle == epoch, self.selectionID == token {
                    self.cachedAudioRecoveryTask = nil; self.isResolving = false; self.resolvingAttempt = nil
                }
            }
            do {
                try self.checkSelection(epoch, token: token, occurrenceID: fact.occurrenceId, admission: recovery.admission)
                result = try await cache.recoverCachedPlayback(previousAttemptID: fact.attemptId,
                    selection: recovery.selection, position: fact.position)
                try self.checkSelection(epoch, token: token, occurrenceID: fact.occurrenceId, admission: recovery.admission)
                guard let result else { throw MusicServiceError.invalidResponse }
                self.resolvingAttempt = result.candidate.attemptId
                try await self.refreshCandidateQueue(service, epoch: epoch, token: token, before: before,
                    entries: entries, admission: recovery.admission)
                self.stopOwned()
                self.feedbackAutomatic = recovery.automatic
                accepted = try self.acceptResolvedCandidate(result.candidate, selection: result.selection,
                    attemptID: result.candidate.attemptId, occurrenceID: fact.occurrenceId,
                    epoch: epoch, token: token, admission: recovery.admission)
                if accepted { self.beginPlaybackMetadata(fallbackSourceKey: result.selection.sourceKey) }
            } catch {
                if self.lifecycle == epoch, self.selectionID == token, !Task.isCancelled {
                    if !accepted { self.stopOwned(); self.phase = .failed }
                    self.report(error, epoch: epoch)
                }
            }
            if !accepted, let result {
                await self.releaseUnacceptedAttempt(result.candidate.attemptId, service: service)
            }
        }
        cachedAudioRecoveryTask = task; recoveryDrains[drainID] = task
        return true
    }

    /// Refresh an expiring official media URL without changing recording,
    /// account, platform or quality. The service still owns the old attempt:
    /// do not mark the whole queue item failed before this bounded retry.
    private func recoverRemoteAudio(_ fact: MusicPlaybackFact) -> Bool {
        guard fact.kind == .failed, !remoteAudioRecoveryTried,
              recoveryDrains.count < 8,
              let previous = candidate, !previous.isCachedAudio, !previous.url.isFileURL,
              let service, let recovery = service as? any MusicPlaybackRecoveryServing else { return false }
        remoteAudioRecoveryTried = true; terminalAttempt = fact.attemptId; isResolving = true
        let epoch = lifecycle, token = selectionID, prior = eventTail
        let admission = fmSource.map { QueuePlaybackAdmission(source: $0, intent: nil) }
        let drainID = UUID()
        let task = Task { [weak self] in
            await prior?.value
            guard let self else { return }
            let attemptID = UUID().uuidString.lowercased()
            var result: MusicPlaybackRecovery?, accepted = false
            defer {
                self.recoveryDrains.removeValue(forKey: drainID)
                if self.lifecycle == epoch, self.selectionID == token {
                    self.isResolving = false; self.remoteAudioRecoveryTask = nil
                    if self.resolvingAttempt == attemptID { self.resolvingAttempt = nil }
                }
            }
            do {
                try self.checkSelection(epoch, token: token, occurrenceID: fact.occurrenceId, admission: admission)
                self.resolvingAttempt = attemptID
                result = try await recovery.recoverPlayback(previousAttemptID: fact.attemptId,
                    attemptID: attemptID, rejectedURL: previous.url, position: fact.position)
                try self.checkSelection(epoch, token: token, occurrenceID: fact.occurrenceId, admission: admission)
                guard let result else { throw MusicServiceError.invalidResponse }
                self.stopOwned()
                accepted = try self.acceptResolvedCandidate(result.candidate, selection: result.selection,
                    attemptID: attemptID, occurrenceID: fact.occurrenceId, epoch: epoch, token: token, admission: admission)
                if accepted {
                    self.beginPlaybackMetadata(fallbackSourceKey: result.selection.sourceKey)
                } else { await self.releaseUnacceptedAttempt(attemptID, service: service) }
            } catch {
                if result != nil, !accepted { await self.releaseUnacceptedAttempt(attemptID, service: service) }
                guard self.lifecycle == epoch, self.selectionID == token, self.isAvailable, !Task.isCancelled else { return }
                if !accepted, result != nil { self.clearUnacceptedCandidate(attemptID) }
                if result == nil, self.candidate?.attemptId == fact.attemptId {
                    // A failed refresh leaves the old owner intact. Only now
                    // publish its terminal failure through the original writer.
                    self.terminalAttempt = nil
                    self.onPlayback(fact)
                } else { self.report(error, epoch: epoch) }
            }
        }
        remoteAudioRecoveryTask = task; recoveryDrains[drainID] = task
        return true
    }
    @discardableResult private func pausePlaybackRecovery() -> Bool {
        // A user's pause wins over an automatic recovery, before any actor
        // suspension. Do not interpret a failed native item as "start again"
        // while its replacement is still being resolved.
        guard cachedAudioRecoveryTask != nil || remoteAudioRecoveryTask != nil else { return false }
        let attempts = Set([candidate?.attemptId, resolvingAttempt].compactMap { $0 })
        fmShouldPlay = false
        invalidateSelection(stopMedia: true)
        phase = .paused
        if let service {
            let id = UUID()
            recoveryDrains[id] = Task { [weak self] in
                guard let self else { return }
                defer { self.recoveryDrains.removeValue(forKey: id) }
                for attempt in attempts { await self.releaseUnacceptedAttempt(attempt, service: service) }
            }
        }
        return true
    }
    public func togglePlayback() async {
        guard isAvailable, !isClosed else { return }
        if pausePlaybackRecovery() { return }
        let own = lifecycle, token = selectionID
        await (service as? any MusicSimilarModeServing)?.pauseSimilarNavigation()
        guard isAvailable, !isClosed, lifecycle == own, selectionID == token else { return }
        if let fmSource, !fmSource.authorization.isActive {
            cancelFMNavigation(); stopFM(); invalidateSelection(stopMedia: true)
            error = MusicServiceError.staleAccess.localizedDescription; return
        }
        if fmAdvanceIntent != nil || (isPreparingRemoteQueue && remoteQueueIsFM) {
            fmShouldPlay.toggle()
            if !fmShouldPlay, let nativeAttempt { player?.pause(nativeAttempt) }
            return
        }
        if let nativeAttempt, candidate != nil, terminalAttempt == nil {
            guard candidate?.fileAuthorization?.isActive != false, candidate?.playbackAuthorization?.isActive != false else {
                invalidateSelection(stopMedia: true); error = MusicServiceError.staleAccess.localizedDescription; return
            }
            if phase == .playing || phase == .buffering { player?.pause(nativeAttempt) }
            else {
                player?.resume(nativeAttempt)
                capturePrimaryPlayback()
            }
        } else { await prepareCurrent() }
    }
    @discardableResult
    public func seek(to seconds: Double) -> Bool {
        guard isAvailable, !isClosed, terminalAttempt == nil, seconds.isFinite, seconds >= 0,
              let nativeAttempt else { return false }
        guard candidate?.fileAuthorization?.isActive != false, candidate?.playbackAuthorization?.isActive != false else {
            invalidateSelection(stopMedia: true); error = MusicServiceError.staleAccess.localizedDescription; return false
        }
        guard player?.seek(seconds, attempt: nativeAttempt) == true else { return false }
        feedbackConsumer.discontinuity()
        return true
    }

    public func loadLyrics(occurrenceId: String, sourceKey: String) async {
        guard isAvailable, !isClosed, current?.id == occurrenceId else { return }
        beginPlaybackMetadata(fallbackSourceKey: sourceKey)
        await playbackMetadata.flush()
    }

    /// Preferred product binding. No View.onChange and no polling.
    public func onPlaybackEvent(_ event: PlaybackEvent) {
        guard isAvailable, !isClosed, event.attempt.owner == "music",
              event.snapshot.owner == "music", event.snapshot.resourceID == event.attempt.resourceID,
              let candidate, candidate.resourceId == event.attempt.resourceID else { return }
        if let nativeAttempt, nativeAttempt.id != event.attempt.id { return }
        if nativeAttempt == nil {
            // Only adopt an attempt while this exact resource is entering the
            // injected native play call, including synchronous preflight failure.
            guard pendingNativeResourceID == event.attempt.resourceID else { return }
            nativeAttempt = MusicPlayerAttempt(id: event.attempt.id, resourceID: event.attempt.resourceID)
        }
        if terminalAttempt == candidate.attemptId {
            // Ended persistence may still be awaiting acknowledgement. A
            // replacement must retire its navigation even after the last fact.
            if [.replaced, .stopped].contains(event.kind) {
                retirePlaybackNavigation()
                cancelRemoteQueuePreparation(); cancelFMNavigation()
                nativeAttempt = nil; pendingNativeResourceID = nil
            }
            if [.paused, .stopped, .replaced].contains(event.kind) { pausePlaybackRecovery() }
            return
        }
        switch event.kind {
        case .paused: fmShouldPlay = false
        case .replaced, .stopped:
            retirePlaybackNavigation()
            cancelRemoteQueuePreparation(); cancelFMNavigation()
            nativeAttempt = nil; pendingNativeResourceID = nil
        default: break
        }
        guard candidate.fileAuthorization?.isActive != false,
              candidate.playbackAuthorization?.isActive != false else {
            invalidateSelection(stopMedia: true); return
        }
        // The unique native event has passed resource/native generation and
        // protected-file admission. Never sample the compatibility snapshot path.
        feedbackConsumer.consume(event, candidate: candidate, automatic: feedbackAutomatic)
        let kind: MusicPlaybackFact.Kind?
        switch event.kind {
        case .played: kind = .playing
        case .progress: kind = .progress
        case .paused: kind = .paused
        case .ended: kind = .ended
        case .failed: kind = .failed
        case .replaced, .stopped: kind = .stopped
        case .preparing, .prepared: kind = nil
        }
        if phase != event.snapshot.phase { phase = event.snapshot.phase }
        if event.kind == .played { completePrimaryPlaybackIfPlaying() }
        if let kind {
            onPlayback(MusicPlaybackFact(attemptId: candidate.attemptId, occurrenceId: candidate.occurrenceId,
                resourceId: candidate.resourceId, kind: kind, position: event.snapshot.position, duration: event.snapshot.duration))
        }
    }
    public func onPlayback(_ event: PlaybackEvent) { onPlaybackEvent(event) }
    /// Compatibility for product snapshot observers. Resource IDs are
    /// per-resolution UUIDs, so repeated same-track attempts cannot alias.
    public func onPlayback(_ snapshot: PlaybackSnapshot) {
        guard isAvailable, !isClosed, snapshot.owner == "music", let candidate,
              snapshot.resourceID == candidate.resourceId else { return }
        guard candidate.fileAuthorization?.isActive != false,
              candidate.playbackAuthorization?.isActive != false else {
            invalidateSelection(stopMedia: true); return
        }
        let kind: MusicPlaybackFact.Kind
        switch snapshot.phase {
        case .playing: kind = phase == .playing ? .progress : .playing
        case .paused: kind = .paused
        case .ended: kind = .ended
        case .failed: kind = .failed
        case .idle: kind = .stopped
        case .buffering, .preparing: phase = snapshot.phase; return
        }
        if phase != snapshot.phase { phase = snapshot.phase }
        onPlayback(MusicPlaybackFact(attemptId: candidate.attemptId, occurrenceId: candidate.occurrenceId,
            resourceId: candidate.resourceId, kind: kind, position: snapshot.position, duration: snapshot.duration))
    }
    public func onPlayback(_ fact: MusicPlaybackFact) {
        guard isAvailable, !isClosed, let candidate, fact.attemptId == candidate.attemptId,
              fact.occurrenceId == candidate.occurrenceId, fact.resourceId == candidate.resourceId,
              fact.position.isFinite, fact.position >= 0, terminalAttempt != fact.attemptId else { return }
        guard candidate.fileAuthorization?.isActive != false,
              candidate.playbackAuthorization?.isActive != false else {
            invalidateSelection(stopMedia: true); return
        }
        position = fact.position
        if let duration = fact.duration, duration.isFinite, duration >= 0 { self.duration = duration }
        switch fact.kind {
        case .playing: phase = .playing
        case .paused: phase = .paused; fmShouldPlay = false
        case .ended: phase = .ended
        case .failed: phase = .failed
        case .stopped: phase = .idle
        case .progress: break
        }
        if recoverCachedAudio(fact) { return }
        if recoverRemoteAudio(fact) { return }
        // UI gets each clock sample. Persistent progress is coalesced at five
        // seconds plus pause/end/exit; request targets aren't playback facts.
        if fact.kind == .progress, let lastProgressWrite, abs(fact.position - lastProgressWrite) < 5 { return }
        if pendingFacts >= 8 {
            if fact.kind != .progress { error = MusicServiceError.busy.localizedDescription }
            return
        }
        if [.ended, .failed, .stopped].contains(fact.kind) { terminalAttempt = fact.attemptId }
        let epoch = lifecycle, token = selectionID, navigation = navigationID, prior = eventTail
        guard let service else { return }
        pendingFacts += 1
        eventTail = Task { [weak self] in
            await prior?.value
            guard let self else { return }
            defer { self.pendingFacts -= 1 }
            do {
                try self.check(epoch)
                guard token == self.selectionID, self.candidate?.attemptId == fact.attemptId else { return }
                try candidate.fileAuthorization?.check()
                try candidate.playbackAuthorization?.check()
                let accepted: Bool
                if let similar = service as? any MusicSimilarModeServing {
                    if fact.kind == .paused || fact.kind == .stopped { await similar.pauseSimilarNavigation() }
                    accepted = try await similar.recordSimilarPlaybackFact(fact)
                } else { accepted = try await service.record(fact) }
                try self.check(epoch)
                guard token == self.selectionID, self.candidate?.attemptId == fact.attemptId else { return }
                if accepted { self.lastProgressWrite = fact.position }
                if accepted && fact.kind == .ended && self.navigationID == navigation {
                    // Schedule after this fact task, rather than awaiting a
                    // navigation that must itself drain earlier facts.
                    _ = self.requestNavigation(.next, reason: .ended)
                } else if fact.kind == .failed {
                    self.report(MusicServiceError.invalidMediaCandidate, epoch: epoch, operation: "播放音频", kind: .media)
                    let updated = try await service.state()
                    try self.check(epoch)
                    guard token == self.selectionID, self.candidate?.attemptId == fact.attemptId else { return }
                    self.state = updated
                }
            } catch {
                if self.lifecycle == epoch, self.selectionID == token, self.terminalAttempt == fact.attemptId {
                    self.terminalAttempt = nil
                }
                self.report(error, epoch: epoch)
            }
        }
    }
    /// Finite logical event drain for lifecycle/tests; no native frame polling.
    public func flushPlaybackFacts() async {
        await feedbackConsumer.flush() // Admission only, never awaits network feedback.
        await eventTail?.value
        let recoveries = Array(recoveryDrains.values)
        for task in recoveries { await task.value }
        await eventTail?.value
    }
    public func flushNavigation() async { await navigationTask?.value }

    public func updateSettings(_ change: MusicSettingsChange) async {
        let epoch: UUID
        do { epoch = try enter() } catch { return }
        defer { pending -= 1 }
        // A settings action supersedes an in-flight selection, not an already
        // playing resource. Do this before the first await, even if the write fails.
        if isResolving { invalidateSelection(stopMedia: true) }
        do {
            let next = try await requireService().updateSettings(change); try check(epoch); state = next
            if !next.lyricsEnabled {
                lyricTask?.cancel()
                beginPlaybackMetadata(); lyricLines = []; lyricState = "disabled"
            }
            else if let current, candidate?.occurrenceId == current.id, let lastSelection {
                await loadLyrics(occurrenceId: current.id, sourceKey: lastSelection.sourceKey)
            }
        } catch { report(error, epoch: epoch, operation: "保存播放设置", kind: .settings) }
    }
    public func setMode(_ mode: MusicPlaybackMode) async {
        await updateSettings(MusicSettingsChange(ifRevision: state.settingsRevision, mode: mode))
    }
    public func add(_ track: MusicRemoteTrack, to collection: MusicCollection) async {
        let epoch: UUID
        do { epoch = try enter() } catch { return }
        defer { pending -= 1 }
        do {
            let service = try requireService(), existing = try await service.tracks(in: collection)
            try check(epoch)
            _ = try await service.editPlaylist(collection, name: nil, refs: existing.map(\.ref) + [track.ref], order: nil)
            try check(epoch); let next = try await service.collections(); try check(epoch); publishCollections(next)
        } catch { report(error, epoch: epoch, operation: "添加到歌单", kind: .library) }
    }
    public func createPlaylist(_ title: String, tracks: [MusicRemoteTrack] = []) async {
        let epoch: UUID
        do { epoch = try enter() } catch { return }
        defer { pending -= 1 }
        do {
            let service = try requireService()
            let created = try await service.createPlaylist(name: title, refs: tracks.map(\.ref)); try check(epoch)
            if let existing = collections.first(where: { $0.id == created.id }) {
                guard existing == created else { throw MusicServiceError.invalidResponse }
                return
            }
            publishCollections(collections + [created])
        } catch { report(error, epoch: epoch, operation: "创建歌单", kind: .library) }
    }
    public func editPlaylist(_ collection: MusicCollection, name: String? = nil, tracks: [MusicRemoteTrack]? = nil,
                             order: [String]? = nil) async {
        let epoch: UUID
        do { epoch = try enter() } catch { return }
        defer { pending -= 1 }
        do {
            let service = try requireService()
            _ = try await service.editPlaylist(collection, name: name, refs: tracks?.map(\.ref), order: order)
            try check(epoch); let next = try await service.collections(); try check(epoch); publishCollections(next)
        } catch { report(error, epoch: epoch, operation: "编辑歌单", kind: .library) }
    }
    public func removePlaylist(_ collection: MusicCollection) async {
        let epoch: UUID
        do { epoch = try enter() } catch { return }
        defer { pending -= 1 }
        do {
            let service = try requireService(); try await service.removePlaylist(collection); try check(epoch)
            let next = try await service.collections(); try check(epoch); publishCollections(next)
        } catch { report(error, epoch: epoch, operation: "删除歌单", kind: .library) }
    }
    public func loadHistory() async {
        let epoch: UUID
        do { epoch = try enter() } catch { return }
        defer { pending -= 1 }
        do {
            let service = try requireService(), songs = try await service.playedSongs(), playlists = try await service.playedPlaylists()
            try check(epoch); playedSongs = songs; playedPlaylists = playlists
        } catch { report(error, epoch: epoch, operation: "读取播放历史", kind: .library) }
    }
    public func restoreHistory(_ playlist: MusicPlayedPlaylist) async {
        cancelRemoteQueuePreparation(); cancelFMNavigation()
        let epoch: UUID
        do { epoch = try enter() } catch { return }
        defer { pending -= 1 }
        guard !isChangingQueue, !isNavigating else { return }
        isChangingQueue = true
        defer { if lifecycle == epoch { isChangingQueue = false } }
        do {
            await flushPlaybackFacts(); try check(epoch)
            let service = try requireService(), before = try await service.state()
            try check(epoch)
            let restored = try await service.restoreHistory(id: playlist.id, expectedRevision: before.revision)
            try check(epoch); stopFM(); invalidateSelection(stopMedia: true); state = restored; queueNotice = nil
            try await refreshQueue(service, epoch: epoch)
            // History restore, like app restore, does not auto-sound.
        } catch { report(error, epoch: epoch, operation: "恢复历史队列", kind: .queue) }
    }
    public func historyDetail(id: String) async throws -> MusicHistoryDetail {
        let epoch = try enter(); defer { pending -= 1 }
        guard let provider = service as? any MusicHistoryManaging else { throw MusicServiceError.unsupported }
        let detail = try await provider.historyDetail(id: id)
        try check(epoch); return detail
    }
    public func removeHistory(detail: MusicHistoryDetail) async {
        let epoch: UUID
        do { epoch = try enter() } catch { return }
        defer { pending -= 1 }
        do {
            guard let provider = service as? any MusicHistoryManaging else { throw MusicServiceError.unsupported }
            try await provider.removeHistory(detail: detail); try check(epoch)
            let next = try await requireService().playedPlaylists()
            try check(epoch); playedPlaylists = next
        } catch { report(error, epoch: epoch, operation: "删除播放历史", kind: .library) }
    }
    /// Restores the selected history detail and explicitly continues playback.
    public func restoreHistory(detail: MusicHistoryDetail, startIndex: Int? = nil) async {
        cancelRemoteQueuePreparation(); cancelFMNavigation()
        let epoch: UUID
        do { epoch = try enter() } catch { return }
        defer { pending -= 1 }
        guard !isChangingQueue, !isNavigating else { return }
        isChangingQueue = true
        defer { if lifecycle == epoch { isChangingQueue = false } }
        do {
            guard let provider = service as? any MusicHistoryManaging else { throw MusicServiceError.unsupported }
            await flushPlaybackFacts(); try check(epoch)
            let service = try requireService(), before = try await service.state()
            try check(epoch)
            let restored = try await provider.restoreHistory(detail: detail, startIndex: startIndex,
                                                            expectedRevision: before.revision)
            try check(epoch)
            try await (service as? any MusicSimilarModeServing)?.cancelSimilarMode()
            try check(epoch); stopFM(); invalidateSelection(stopMedia: true); state = restored; queueNotice = nil
            try await refreshQueue(service, epoch: epoch)
            await prepareCurrent()
        } catch { report(error, epoch: epoch, operation: "继续播放历史歌单") }
    }

    public func importFile(_ url: URL) async {
        let epoch = lifecycle
        guard isAvailable, !isClosed else { return }
        let cleanup = cancelImportPresentation(), own = importID
        do {
            await cleanup?.value
            try check(epoch)
            guard importID == own else { return }
            let bytes = try await ImportedFileReader.read(url, maximumBytes: 7 * 1024 * 1024)
            try check(epoch)
            guard importID == own else { return }
            guard service != nil else { throw MusicServiceError.unavailable }
            // Explicit library import through its owner and preview/commit protocol.
            importData = bytes
            importSourceID = "archive-sha256:" + SHA256.hash(data: bytes).map { String(format: "%02x", $0) }.joined()
            importOwner = ""; importPreview = nil; showingImport = true
        } catch let failure as ImportedFileError {
            if epoch == lifecycle, isAvailable, importID == own { error = failure.localizedDescription }
        } catch {
            if importID == own { report(error, epoch: epoch, operation: "读取曲库文件", kind: .library) }
        }
    }
    public func previewImport() async {
        let epoch: UUID
        do { epoch = try enter() } catch { return }
        defer { pending -= 1 }
        guard let data = importData, let source = importSourceID, !importOwner.isEmpty, !isImporting else { return }
        let own = importID
        isImporting = true; defer { if lifecycle == epoch { isImporting = false } }
        do {
            let service = try requireService()
            let preview = try await service.prepareImport(data: data, sourceId: source, ownerId: importOwner)
            do {
                try check(epoch)
                guard importID == own else { throw MusicServiceError.cancelled }
                importPreview = preview
            } catch {
                await Task { try? await service.releaseImport(preview) }.value
                throw error
            }
        } catch {
            if importID == own { report(error, epoch: epoch, operation: "预览曲库导入", kind: .library) }
        }
    }
    public func confirmImport() async {
        let epoch = lifecycle
        guard let importPreview, let service, !isImporting else { return }
        isImporting = true; defer { if lifecycle == epoch { isImporting = false } }
        let own = importID
        do {
            _ = try await service.commitImport(importPreview); try check(epoch)
            guard importID == own else { return }
            self.importPreview = nil; importData = nil; importSourceID = nil; showingImport = false
            await load()
        } catch {
            if importID == own { report(error, epoch: epoch, operation: "导入曲库", kind: .library) }
        }
    }
    public func cancelImport() async {
        await cancelImportPresentation()?.value
    }

    /// UIKit dismissal clears only this presentation synchronously. Cleanup
    /// captures its exact preview/service; it cannot cancel a reopened import.
    @discardableResult
    func cancelImportPresentation() -> Task<Void, Never>? {
        let epoch = lifecycle, own = UUID()
        importID = own
        let preview = importPreview
        importPreview = nil; importData = nil; importSourceID = nil; showingImport = false
        if let preview, let service {
            return Task { [weak self] in
                do { try await service.releaseImport(preview) }
                catch {
                    if self?.importID == own {
                        self?.report(error, epoch: epoch, operation: "取消曲库导入", kind: .library)
                    }
                }
            }
        }
        return nil
    }
    public func close() async {
        guard !isClosed else { return }
        notifyCollectionPages(.revoking)
        cancelRemoteQueuePreparation(); cancelFMNavigation(); stopFM()
        stopSimilarObservation()
        let attempt = resolvingAttempt ?? candidate?.attemptId
        let preview = importPreview
        isClosed = true; lifecycle = UUID(); searchTask?.cancel(); navigationTask?.cancel(); eventTail?.cancel()
        metadataPreferencesRefreshRequested = false
        metadataPreferencesRefreshTask?.cancel()
        playbackMetadata.close()
        notifyCollectionPages(.access)
        navigationID = UUID(); isNavigating = false; isChangingQueue = false
        invalidateSelection(stopMedia: true)
        importData = nil; importPreview = nil; showingImport = false; searchResults = []; queue = []; queueNotice = nil; lyricLines = []
        if let service {
            if let attempt { do { try await service.cancel(attemptId: attempt) } catch { self.error = MusicServiceError.sanitized(error).localizedDescription } }
            if let preview { do { try await service.releaseImport(preview) } catch { self.error = MusicServiceError.sanitized(error).localizedDescription } }
        }
    }
    deinit {
        remoteQueueIntent?.revoke(); remoteQueueTask?.cancel()
        fmAdvanceIntent?.revoke(); fmRevocationTask?.cancel(); fmSource?.revoke()
        searchTask?.cancel(); resolutionTask?.cancel(); candidateDiscoveryTask?.cancel()
        lyricTask?.cancel(); eventTail?.cancel(); navigationTask?.cancel()
        metadataPreferencesRefreshTask?.cancel()
        cachedAudioLookup?.cancel(); cachedAudioObservation?.cancel(); cachedAudioRecoveryTask?.cancel()
        remoteAudioRecoveryTask?.cancel()
        for task in recoveryDrains.values { task.cancel() }
        similarObservation?.cancel()
    }
}
#endif
