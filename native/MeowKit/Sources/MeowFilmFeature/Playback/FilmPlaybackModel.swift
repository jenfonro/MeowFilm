import Foundation
import Combine
import MeowContracts
import MeowDebug
import MeowRuntime

public enum FilmPlaybackStatus: Equatable {
    case idle, loading, searching, preparing, prepared, buffering, playing, paused, ended, unavailable, failed
    public var title: String? {
        switch self {
        case .idle: nil
        case .loading: "加载中"
        case .searching: "匹配片源"
        case .preparing: "准备中"
        case .prepared: "地址已准备"
        case .buffering: "缓冲中"
        case .playing: "播放中"
        case .paused: "已暂停"
        case .ended: "播放结束"
        case .unavailable: "暂无可用视频源"
        case .failed: "播放失败"
        }
    }
    public var busy: Bool { self == .loading || self == .searching || self == .preparing || self == .buffering }
}

/// Node owns keyword sessions, smart list, matching, freshness, and persistent playback facts.
/// This owner holds one observation and one admitted native preparation at a time.
@MainActor
public final class FilmPlaybackModel {
    /// High-frequency playback telemetry store (only observed by `FilmPlayerViewController`).
    public let mediaStore = FilmMediaStateStore()
    /// Low-frequency detail/browsing/error store (observed by detail & directory pages).
    public let presentationStore = FilmPresentationStateStore()

    public private(set) var snapshot: FilmPlaybackSnapshot? {
        didSet {
            let changed: Bool
            switch (oldValue, snapshot) {
            case (nil, nil): changed = false
            case (let lhs?, let rhs?): changed = !lhs.hasSamePresentation(as: rhs)
            default: changed = true
            }
            if changed {
                presentationStore.snapshot = snapshot
            }
        }
    }
    public internal(set) var presentation: FilmPlaybackSnapshot? {
        didSet {
            let changed: Bool
            switch (oldValue, presentation) {
            case (nil, nil): changed = false
            case (let lhs?, let rhs?): changed = !lhs.hasSamePresentation(as: rhs)
            default: changed = true
            }
            if changed {
                presentationStore.presentation = presentation
            }
        }
    }
    public private(set) var opening: Bool {
        get { presentationStore.opening }
        set { guard presentationStore.opening != newValue else { return }; presentationStore.opening = newValue }
    }
    public private(set) var observationError: FilmPlaybackError? {
        get { presentationStore.observationError }
        set { guard presentationStore.observationError != newValue else { return }; presentationStore.observationError = newValue }
    }
    public internal(set) var actionError: FilmPlaybackError? {
        get { presentationStore.actionError }
        set { guard presentationStore.actionError != newValue else { return }; presentationStore.actionError = newValue }
    }
    public private(set) var mediaStopError: FilmPlaybackError? {
        get { presentationStore.mediaStopError }
        set { guard presentationStore.mediaStopError != newValue else { return }; presentationStore.mediaStopError = newValue }
    }
    public private(set) var sessionReleaseError: FilmPlaybackError? {
        get { presentationStore.sessionReleaseError }
        set { guard presentationStore.sessionReleaseError != newValue else { return }; presentationStore.sessionReleaseError = newValue }
    }
    public internal(set) var mediaState: FilmMediaState {
        get { mediaStore.mediaState }
        set {
            let oldValue = mediaStore.mediaState
            mediaStore.mediaState = newValue
            if let heldRate, heldRate.identity != controlIdentity || oldValue.engine != newValue.engine {
                cancelTemporaryRate()
            }
            updateChapters()
        }
    }
    var nativeStatus: FilmPlaybackStatus? {
        get { mediaStore.nativeStatus }
        set {
            setNativeStatus(newValue, coarsePresentation: newValue)
        }
    }
    func setNativeStatus(_ status: FilmPlaybackStatus?, coarsePresentation: FilmPlaybackStatus?) {
        if mediaStore.nativeStatus != status {
            mediaStore.nativeStatus = status
        }
        if presentationStore.nativeStatus != coarsePresentation {
            presentationStore.nativeStatus = coarsePresentation
        }
    }

    public var playbackStatus: FilmPlaybackStatus {
        if let nativeStatus { return nativeStatus }
        if snapshot?.noSources == true { return .unavailable }
        switch executionStage {
        case "loading", "cached", "history", "manual": return .loading
        case "searching": return .searching
        case "resolving", "pending": return .preparing
        case "prepared": return .prepared
        case "ended": return .ended
        case "unavailable": return .unavailable
        case "error": return .failed
        default: return .idle
        }
    }

    public var canControlPlayback: Bool {
        guard !runtimeClosed, let committed, committed.started, !committed.terminal, !committed.retired else { return false }
        return media.isCurrent(sessionID: committed.sessionID, attemptID: committed.attempt.id)
    }

    public var canStartPlayback: Bool {
        guard executionStage != nil, !playbackStatus.busy, !restoringRead, let snapshot,
              !snapshot.noSources, snapshot.stage != "error" else { return false }
        if snapshot.current != nil || snapshot.attempt != nil { return true }
        if !snapshot.directoryItems.isEmpty {
            return snapshot.directoryItems.contains(where: \.isPlayable)
        }
        return snapshot.work.kind == .movie || snapshot.sourceCanLoadMore || !snapshot.sourceOptions.isEmpty
    }

    public var canSelectFromPresentation: Bool {
        guard executionStage != nil, !restoringRead, let presentation, !presentation.noSources else { return false }
        return !presentation.directoryLoading && presentation.directoryError == nil
    }

    public var canRetryMedia: Bool {
        executionStage != nil && snapshot?.retryMediaAttemptID != nil
    }

    public var directoryHasNoVideoSource: Bool {
        presentation?.noSources == true
    }

    public var playerPreferences: FilmPlayerPreferences { preferencesStore.preferences }
    public var actualEngine: PlaybackVideoEngine { mediaState.engine }
    public var availableRates: [Double] {
        [0.5, 0.75] + FilmPlayerPreferences.defaultRates(for: actualEngine)
    }
    public var skipSegment: FilmPlaybackSegment? {
        guard controlIdentity != nil else { return nil }
        return chapterModel.segments.first { mediaState.position >= $0.start && mediaState.position < $0.end }
    }
    public var sessionID: UUID? { allocatedSessionID }
    var restoringRead: Bool { restorePresentation != nil }
    public var executionStage: String? {
        guard !closed, !runtimeClosed, observationError == nil else { return nil }
        if restorePresentation != nil { return snapshot?.directoryLoading == true ? "loading" : nil }
        return snapshot?.stage
    }
    public var isReleased: Bool { released && mediaStopped }
    public var canLeave: Bool {
        isReleased || runtimeClosed && closeDrained &&
            (allocatedSessionID.map { media.isLocallyDrained(sessionID: $0) } ?? true)
    }

    let transport: FilmPlaybackTransport
    let makeRequest: @Sendable () async throws -> FilmPlaybackRequest
    let searchContext: FilmSearchContext?
    var restorePresentation: FilmPlaybackSnapshot?
    let media: any FilmPlaybackMediaControlling
    let preferencesStore: FilmPlayerPreferencesStore
    let playbackPreferences: FilmPlaybackSettingsModel?
    let chapterModel: FilmPlaybackChapterModel
    private var preferencesObservation: AnyCancellable?
    private var chaptersObservation: AnyCancellable?
    private var chapterErrorObservation: AnyCancellable?
    var heldRate: (token: UUID, identity: FilmPlayerControlIdentity)?
    var allocatedSessionID: UUID?
    var openingTask: Task<Void, Never>?
    nonisolated(unsafe) var observationTask: Task<Void, Never>?
    var observationAdmission = UUID()
    nonisolated(unsafe) var runtimeClosureTask: Task<Void, Never>?
    nonisolated(unsafe) var runtimeAvailabilityTask: Task<Void, Never>?
    var runtimeClosed = false
    nonisolated(unsafe) var recoveryTask: Task<Void, Never>?
    nonisolated(unsafe) var ownerCloseTask: Task<Void, Never>?
    var presentationOwners = Set<UUID>()
    var closingTask: Task<Void, Never>?
    var closingAdmission = UUID()
    var reopeningTask: Task<Void, Never>?
    var reopeningAdmission = UUID()
    var generation = UUID()
    var closed = false
    var retained = false
    var released = false
    var commandAdmission = UUID()
    var awaitingIntent: UUID? {
        didSet { if awaitingIntent == nil { resumeCommitWaiter() } }
    }
    var commitWaiter: (id: UUID, continuation: CheckedContinuation<Void, Error>)?
    var blockedAttemptID: String?
    var continuingFrom: String?
    var mediaAdmission = UUID()
    var preparing: Preparation?
    var committed: Preparation?
    var preparingID: String? { preparing?.attempt.id }
    var committedID: String? { committed?.attempt.id }
    var requestedPreparation: Preparation?
    var preparationWorker: Task<Void, Never>?
    nonisolated(unsafe) var activePreparation: Task<Void, Never>?
    var cancellingPreparation: Task<Void, Never>?
    var stoppingMedia: Task<Void, Never>?
    var commandWorker: Task<Void, Never>?
    var queuedCommands: [Command] = []
    var mediaStopped = true
    var closeDrained = false
    var lastProgressSentAt: ContinuousClock.Instant?
    var lastProgressPaused: Bool?
    var lastProgressAttemptID: String?

    @MainActor
    final class Preparation {
        let attempt: FilmAttemptInfo
        let targetID: String
        let contentKind: FilmPlaybackKind
        let sessionID: UUID
        let admission: UUID
        let generation: UUID
        var didCommit = false
        var started = false
        var terminal = false
        var retired = false
        var resourceID: String?
        var state: FilmMediaState?
        private var resourceReleased = false
        private var releaseTask: Task<Void, Error>?
        init(attempt: FilmAttemptInfo, targetID: String, contentKind: FilmPlaybackKind,
             sessionID: UUID, admission: UUID, generation: UUID) {
            self.attempt = attempt
            self.targetID = targetID
            self.contentKind = contentKind
            self.sessionID = sessionID
            self.admission = admission
            self.generation = generation
        }
        func releaseResource(transport: FilmPlaybackTransport) async throws {
            guard !resourceReleased, let resourceID else { return }
            if let releaseTask { try await releaseTask.value; return }
            let task = Task { [self] in
                try await FilmPlaybackModel.releaseMedia(transport: transport, sessionID: sessionID,
                                                         attemptID: attempt.id, resourceID: resourceID)
            }
            releaseTask = task
            do {
                try await task.value
                resourceReleased = true; releaseTask = nil
            } catch {
                releaseTask = nil
                throw error
            }
        }
    }

    struct Command {
        let action: String
        let parameters: RuntimeObject
        let key: String
        let intent: UUID?
        let ui: Bool
    }

    public init(transport: FilmPlaybackTransport, media: any FilmPlaybackMediaControlling,
                searchContext: FilmSearchContext? = nil,
                playerPreferences: FilmPlayerPreferencesStore? = nil,
                playbackPreferences: FilmPlaybackSettingsModel? = nil,
                request: @escaping @Sendable () async throws -> FilmPlaybackRequest) {
        self.transport = transport; self.media = media; makeRequest = request
        self.searchContext = searchContext
        preferencesStore = playerPreferences ?? FilmPlayerPreferencesStore()
        self.playbackPreferences = playbackPreferences
        chapterModel = FilmPlaybackChapterModel(transport: transport)
        var previousPreferences = preferencesStore.preferences
        preferencesObservation = preferencesStore.$preferences.removeDuplicates().dropFirst().sink { [weak self] value in
            guard let self else { return }
            if !previousPreferences.hasSameGestureSettings(as: value) {
                cancelTemporaryRate()
            }
            if previousPreferences.recognizesChapters != value.recognizesChapters {
                updateChapters(enabled: value.recognizesChapters)
            }
            previousPreferences = value
            mediaStore.notifyChange()
        }
        chaptersObservation = chapterModel.$segments.dropFirst().sink { [weak self] _ in
            self?.mediaStore.notifyChange()
        }
        chapterErrorObservation = chapterModel.$error.compactMap { $0 }.sink { [weak self] error in
            self?.actionError = error
        }
    }

    @discardableResult
    public func open(restoreOnly: Bool = false) -> Task<Void, Never>? {
        if runtimeClosed { return recoverRuntime() }
        if closingTask != nil { return reopenAfterDrain() }
        if let openingTask { return openingTask }
        guard !closed else { return nil }
        if snapshot != nil {
            let generation = generation
            openingTask = Task { [weak self] in
                guard let self else { return }
                defer { openingTask = nil }
                do { try await playbackPreferences?.reloadPreferences() }
                catch {
                    if !closed, self.generation == generation, !Self.cancelled(error) {
                        actionError = Self.failure(error)
                        DebugRuntime.logError(.filmPlayback, sub: "session", event: "preferences.reload.error", error: error)
                    }
                }
                guard !closed, self.generation == generation else { return }
                observeRuntimeClosure(); observe()
            }
            return openingTask
        }
        if allocatedSessionID != nil { return reopenAfterDrain() }
        opening = true; actionError = nil
        let generation = generation
        openingTask = Task { [weak self] in
            guard let self else { return }
            defer { self.opening = false; self.openingTask = nil }
            do {
                try await self.playbackPreferences?.reloadPreferences()
                let previous = self.presentation
                var request = try await self.makeRequest()
                request.restoreOnly = restoreOnly || previous != nil
                self.restorePresentation = request.restoreOnly ? previous : nil
                guard !self.closed, self.generation == generation else {
                    self.restorePresentation = nil
                    return
                }
                let value = try await self.transport.call("session.open", parameters: request.value)
                if let id = value.filmObject?["sessionId"]?.filmText.flatMap(UUID.init(uuidString:)) {
                    self.allocatedSessionID = id
                }
                let snapshot = try FilmPlaybackSnapshot(value: value)
                self.allocatedSessionID = snapshot.sessionID
                DebugRuntime.log(.filmPlayback, sub: "session", event: "open.ok", level: .debug, data: .object([
                    "sessionId": .string(snapshot.sessionID.uuidString.lowercased()),
                    "targetId": .string(snapshot.targetID),
                    "restoreOnly": .bool(request.restoreOnly),
                ]))
                self.observeRuntimeClosure()
                guard !self.closed, self.generation == generation else {
                    self.restorePresentation = nil
                    return
                }
                self.accept(snapshot)
                self.observe()
            } catch {
                self.restorePresentation = nil
                if !self.closed, !Self.cancelled(error) {
                    self.actionError = Self.failure(error)
                    DebugRuntime.logError(.filmPlayback, sub: "session", event: "open.error", error: error)
                    self.observeRuntimeClosure()
                }
            }
        }
        return openingTask
    }

    private func reopenAfterDrain() -> Task<Void, Never>? {
        if let reopeningTask { return reopeningTask }
        let admission = UUID(); reopeningAdmission = admission
        reopeningTask = Task { [weak self] in
            guard let self else { return }
            defer {
                if self.reopeningAdmission == admission { self.reopeningTask = nil }
            }
            while let closingTask = self.closingTask {
                let closingAdmission = self.closingAdmission
                await closingTask.value
                guard !Task.isCancelled, self.reopeningAdmission == admission else { return }
                if self.closingAdmission != closingAdmission { continue }
                self.closingTask = nil
                break
            }
            guard !Task.isCancelled, self.reopeningAdmission == admission else { return }
            if self.retained {
                self.closed = false; self.retained = false
                self.restorePresentation = nil
                self.observe()
                return
            }
            if !self.isReleased { await self.closeSession() }
            guard !Task.isCancelled, self.reopeningAdmission == admission, self.isReleased else { return }
            self.closingTask = nil
            self.resetSession()
            await self.open(restoreOnly: true)?.value
        }
        return reopeningTask
    }

    private func resetSession() {
        generation = UUID(); closed = false; retained = false; released = false
        closeDrained = false
        allocatedSessionID = nil; snapshot = nil
        restorePresentation = nil
        observationError = nil; actionError = nil
        mediaStopError = nil; sessionReleaseError = nil
        committed = nil; preparing = nil; mediaStopped = true
        mediaState = FilmMediaState(); nativeStatus = nil; blockedAttemptID = nil; continuingFrom = nil
    }

    public func acknowledgeError() { actionError = nil }
    public func reportMediaControlError(_ error: FilmPlaybackError) { actionError = error }

    public func reconnect() {
        if runtimeClosed { _ = recoverRuntime(); return }
        guard !closed, snapshot != nil else { return }
        let previous = observationTask
        previous?.cancel()
        let admission = UUID(); observationAdmission = admission
        observationTask = Task { [weak self] in
            await previous?.value
            guard let self, self.observationAdmission == admission else { return }
            self.observationTask = nil
            if !self.closed { self.observe() }
        }
    }

    public func setPresentationVisible(_ visible: Bool, owner: UUID) {
        if visible { presentationOwners.insert(owner) }
        else { presentationOwners.remove(owner) }
        if presentationOwners.isEmpty { cancelTemporaryRate() }
    }

    public func runtimeDidBecomeAvailable() {
        guard !presentationOwners.isEmpty else { return }
        runtimeAvailabilityTask?.cancel()
        let generation = generation
        runtimeAvailabilityTask = Task { [weak self] in
            guard let self else { return }
            defer { if self.generation == generation { self.runtimeAvailabilityTask = nil } }
            let borrowed = await self.transport.borrowedProducer()
            guard !Task.isCancelled, !self.presentationOwners.isEmpty, self.generation == generation else { return }
            if borrowed?.isClosed == true || self.runtimeClosed {
                if !self.runtimeClosed {
                    self.observeRuntimeClosure()
                    await self.runtimeClosureTask?.value
                }
                guard !Task.isCancelled, !self.presentationOwners.isEmpty, self.generation == generation else { return }
                if self.runtimeClosed {
                    _ = self.recoverRuntime()
                } else if self.allocatedSessionID == nil, self.openingTask == nil {
                    await self.transport.renew()
                    guard !Task.isCancelled, !self.presentationOwners.isEmpty, self.generation == generation else { return }
                    self.closed = false
                    _ = self.open(restoreOnly: self.presentation != nil)
                }
                return
            }
            if self.allocatedSessionID == nil, self.openingTask == nil,
               self.actionError != nil || self.observationError != nil {
                DebugRuntime.log(.filmPlayback, sub: "session", event: "runtime.recover.initial", level: .info)
                await self.transport.renew()
                guard !Task.isCancelled, !self.presentationOwners.isEmpty, self.generation == generation else { return }
                self.closed = false
                self.observationError = nil
                _ = self.open(restoreOnly: self.presentation != nil)
            }
        }
    }

    private func recoverRuntime() -> Task<Void, Never>? {
        if let recoveryTask { return recoveryTask }
        let generation = generation
        recoveryTask = Task { [weak self] in
            guard let self else { return }
            defer { self.recoveryTask = nil }
            await self.closingTask?.value
            guard !Task.isCancelled, self.generation == generation, self.canLeave else { return }
            do {
                DebugRuntime.log(.filmPlayback, sub: "session", event: "runtime.recover.start", level: .info)
                await self.transport.renew()
                guard let client = try await self.transport.producer(), !client.isClosed else {
                    throw FilmPlaybackError.closed
                }
                guard !Task.isCancelled, self.generation == generation else { return }
                self.closingTask = nil
                self.runtimeClosed = false
                self.resetSession()
                await self.open(restoreOnly: true)?.value
            } catch {
                if !Self.cancelled(error) {
                    self.observationError = Self.failure(error)
                    DebugRuntime.logError(.filmPlayback, sub: "session", event: "runtime.recover.error", error: error)
                }
            }
        }
        return recoveryTask
    }

    private func observeRuntimeClosure() {
        guard runtimeClosureTask == nil, !runtimeClosed,
              let wait = transport.runtimeClosure else { return }
        let generation = generation
        runtimeClosureTask = Task { [weak self] in
            do {
                try await wait()
                try Task.checkCancellation()
            } catch {
                if let self, self.generation == generation {
                    self.runtimeClosureTask = nil
                    if !Self.cancelled(error) { self.observationError = Self.failure(error) }
                }
                return
            }
            guard let self, self.generation == generation else { return }
            DebugRuntime.log(.filmPlayback, sub: "session", event: "runtime.closed", level: .warn)
            self.runtimeClosureTask = nil
            self.runtimeClosed = true
            self.closed = true; self.retained = false
            self.commandAdmission = UUID(); self.awaitingIntent = nil
            self.cancelPreparation()
            self.observationError = .closed
            await self.close(allowSystemOwner: false)
        }
    }

    private func observe() {
        guard observationTask == nil, let current = snapshot, !closed || retained else { return }
        observationError = nil
        let admission = UUID(); observationAdmission = admission
        let generation = generation
        observationTask = Task { [weak self, transport] in
            do {
                try await transport.snapshots(sessionID: current.sessionID, after: current.sequence) { [weak self] value in
                    try await self?.receive(value, generation: generation)
                }
            } catch {
                if let self, !self.closed || self.retained, self.generation == generation, !Self.cancelled(error) {
                    self.observationError = Self.failure(error)
                    DebugRuntime.logError(.filmPlayback, sub: "session", event: "observe.error", error: error)
                }
            }
            if let self, self.generation == generation, self.observationAdmission == admission {
                self.observationTask = nil
            }
        }
    }

    private func receive(_ value: FilmPlaybackSnapshot, generation: UUID) throws {
        guard !closed || retained, self.generation == generation else { throw CancellationError() }
        guard value.sessionID == allocatedSessionID else { throw FilmPlaybackError.invalidResponse }
        accept(value)
    }

    func accept(_ value: FilmPlaybackSnapshot, reconcileMedia: Bool = true) {
        guard value.sessionID == allocatedSessionID else { return }
        if let current = snapshot, current.sessionID == value.sessionID, value.sequence <= current.sequence { return }
        snapshot = value
        if let previous = restorePresentation {
            if !value.directoryLoading {
                restorePresentation = nil
                if let error = value.directoryError ?? (value.noSources ? nil : value.error) {
                    actionError = FilmPlaybackError(code: error)
                }
                presentation = value.retainingLoadedPresentation(previous)
            }
        } else {
            presentation = value.retainingLoadedPresentation(presentation)
        }
        if reconcileMedia { reconcilePreparation() }
        updateChapters()
    }

    public func close(allowSystemOwner: Bool = false) async {
        cancelTemporaryRate()
        chapterModel.update(sessionID: nil, attemptID: nil, resourceID: nil, chapters: [], enabled: false)
        runtimeAvailabilityTask?.cancel()
        runtimeAvailabilityTask = nil
        ownerCloseTask?.cancel()
        ownerCloseTask = nil
        recoveryTask?.cancel()
        reopeningTask?.cancel()
        reopeningTask = nil; reopeningAdmission = UUID()
        commandAdmission = UUID(); awaitingIntent = nil
        await closeSession(allowSystemOwner: allowSystemOwner)
    }

    private func closeSession(allowSystemOwner: Bool = false) async {
        let generation = generation
        let reopeningAdmission = reopeningAdmission
        while let closingTask {
            let admission = closingAdmission
            await closingTask.value
            guard self.generation == generation, self.reopeningAdmission == reopeningAdmission else { return }
            if closingAdmission != admission { continue }
            let keep = allowSystemOwner && !runtimeClosed && hasSystemOwner && committed?.started == true
            if retained && keep || isReleased { return }
            self.closingTask = nil
        }
        let needsNativeStop = !closeDrained || !mediaStopped ||
            (sessionID.map { !media.isLocallyDrained(sessionID: $0) } ?? false)
        closed = true
        closeDrained = false
        retained = allowSystemOwner && !runtimeClosed && hasSystemOwner && committed?.started == true
        cancelPreparation(); observationAdmission = UUID(); observationTask?.cancel()
        let keepNative = retained
        closingAdmission = UUID()
        closingTask = Task { [self] in
            await observationTask?.value; observationTask = nil
            await preparationWorker?.value
            await cancellingPreparation?.value
            await stoppingMedia?.value; stoppingMedia = nil
            await openingTask?.value
            queuedCommands.removeAll { $0.ui || !keepNative && $0.intent != nil }
            await commandWorker?.value
            guard let sessionID else { released = true; closeDrained = true; return }
            var failure: FilmPlaybackError?
            if !keepNative, needsNativeStop {
                do {
                    try await media.stop(sessionID: sessionID)
                    mediaStopped = true; mediaStopError = nil
                } catch {
                    mediaStopped = false; mediaStopError = Self.failure(error)
                    failure = mediaStopError
                    DebugRuntime.logError(.filmPlayback, sub: "session", event: "close.mediaStop.error", error: error)
                }
            }
            await commandWorker?.value
            do { try await flushProgress() }
            catch { failure = failure ?? Self.failure(error) }
            if !keepNative, media.isLocallyDrained(sessionID: sessionID) {
                committed = nil; mediaState = FilmMediaState(); nativeStatus = nil
            }
            if !released {
                do {
                    let receipt = try await transport.call("session.release", parameters: .object([
                        "sessionId": .string(sessionID.uuidString.lowercased()), "keepMedia": .bool(keepNative),
                    ]))
                    guard receipt.filmObject?[keepNative ? "retained" : "released"] == .bool(true) else {
                        throw FilmPlaybackError.invalidResponse
                    }
                    released = !keepNative
                    sessionReleaseError = nil
                } catch {
                    sessionReleaseError = Self.failure(error)
                    failure = failure ?? sessionReleaseError
                    DebugRuntime.logError(.filmPlayback, sub: "session", event: "close.release.error", error: error)
                }
            }
            if let failure { actionError = failure }
            if keepNative { observe() }
            if !keepNative, isReleased {
                let wait = runtimeClosureTask
                runtimeClosureTask = nil
                wait?.cancel()
                await wait?.value
            }
            if !keepNative { closeDrained = true }
        }
        await closingTask?.value
    }

    static let mediaActions: Set<String> = [
        "prepare", "play", "select.item", "pan", "quality", "switch", "block", "next", "previous", "stop",
        "media.retry",
    ]
    static func cancelled(_ error: Error) -> Bool {
        error is CancellationError || (error as? FilmPlaybackError)?.code == "CANCELLED" ||
            (error as? RuntimeHTTPError) == .cancelled
    }
    static func failure(_ error: Error) -> FilmPlaybackError {
        if let error = error as? FilmPlaybackError { return error }
        if let error = error as? RuntimeHTTPError {
            switch error {
            case .closed: return .closed
            case .cancelled: return .init(code: "CANCELLED")
            case .timeout: return .init(code: "TIMEOUT")
            case .invalidRequest: return .init(code: "INVALID_REQUEST")
            case .invalidResponse: return .invalidResponse
            case .remote(let code): return .init(code: code)
            }
        }
        if let error = error as? FilmSearchError {
            switch error {
            case .closed: return .closed
            case .invalidResponse: return .invalidResponse
            case .unavailable: return .init(code: "HOST_UNAVAILABLE")
            }
        }
        if let error = error as? FilmSourceError { return .init(code: error.code) }
        if error is CancellationError { return .init(code: "CANCELLED") }
        return .init(code: "FAILED")
    }
    nonisolated deinit {
        runtimeAvailabilityTask?.cancel()
        ownerCloseTask?.cancel()
        recoveryTask?.cancel()
        runtimeClosureTask?.cancel()
        observationTask?.cancel(); activePreparation?.cancel()
    }
}
