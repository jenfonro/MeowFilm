import Foundation
import Combine
import MeowContracts
import MeowRuntime
#if os(iOS)
import UIKit
#endif

/// Node owns matching, freshness, episode mapping and persistent playback facts.
/// This owner holds one observation and one admitted native preparation at a time.
@MainActor
public final class FilmPlaybackModel: ObservableObject {
    @Published public private(set) var snapshot: FilmPlaybackSnapshot?
    @Published public private(set) var presentation: FilmPlaybackSnapshot?
    @Published public private(set) var opening = false
    @Published public private(set) var observationError: FilmPlaybackError?
    @Published public private(set) var actionError: FilmPlaybackError?
    /// Independent close failures; media.stop includes its media-receipt drain.
    @Published public private(set) var mediaStopError: FilmPlaybackError?
    @Published public private(set) var sessionReleaseError: FilmPlaybackError?
    @Published public private(set) var mediaState = FilmMediaState() {
        didSet {
            if let heldRate, heldRate.identity != controlIdentity || oldValue.engine != mediaState.engine {
                cancelTemporaryRate()
            }
            updateChapters()
        }
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
        if restorePresentation != nil { return snapshot?.browsing.loading == true ? "loading" : nil }
        return snapshot?.stage
    }
    public var isReleased: Bool { released && mediaStopped }
    /// A dead runtime cannot acknowledge release. Keep that failure visible,
    /// but do not trap navigation after the precise local resources have drained.
    public var canLeave: Bool {
        isReleased || runtimeClosed && closeDrained &&
            (allocatedSessionID.map { media.isLocallyDrained(sessionID: $0) } ?? true)
    }

    private let transport: FilmPlaybackTransport
    private let makeRequest: @Sendable () async throws -> FilmPlaybackRequest
    private let searchContext: FilmSearchContext?
    private var searchObservation: AnyCancellable?
    private var searchUpdateTask: Task<Void, Never>?
    private var restorePresentation: FilmPlaybackSnapshot?
    private var searchRevision = -1
    private var reentryTarget: FilmPlaybackTarget?
    private var reentryError: String?
    private let media: any FilmPlaybackMediaControlling
    private let preferencesStore: FilmPlayerPreferencesStore
    private let chapterModel: FilmPlaybackChapterModel
    private var preferencesObservation: AnyCancellable?
    private var chaptersObservation: AnyCancellable?
    private var chapterErrorObservation: AnyCancellable?
    private var heldRate: (token: UUID, identity: FilmPlayerControlIdentity)?
    private var allocatedSessionID: UUID?
    private var openingTask: Task<Void, Never>?
    private var observationTask: Task<Void, Never>?
    private var observationAdmission = UUID()
    private var runtimeClosureTask: Task<Void, Never>?
    private var runtimeClosed = false
    private var recoveryTask: Task<Void, Never>?
    private var presentationOwners = Set<UUID>()
    private var closingTask: Task<Void, Never>?
    private var closingAdmission = UUID()
    private var reopeningTask: Task<Void, Never>?
    private var reopeningAdmission = UUID()
    private var generation = UUID()
    private var closed = false
    private var retained = false
    private var released = false
    private var commandAdmission = UUID()
    private var awaitingIntent: UUID? {
        didSet { if awaitingIntent == nil { resumeCommitWaiter() } }
    }
    private var commitWaiter: (id: UUID, continuation: CheckedContinuation<Void, Error>)?
    private var blockedAttemptID: String?
    private var mediaAdmission = UUID()
    private var preparing: Preparation?
    private var committed: Preparation?
    private var preparingID: String? { preparing?.attempt.id }
    private var committedID: String? { committed?.attempt.id }
    private var requestedPreparation: Preparation?
    private var preparationWorker: Task<Void, Never>?
    private var activePreparation: Task<Void, Never>?
    private var cancellingPreparation: Task<Void, Never>?
    private var stoppingMedia: Task<Void, Never>?
    private var commandWorker: Task<Void, Never>?
    private var queuedCommands: [Command] = []
    private var mediaStopped = true
    private var closeDrained = false

    @MainActor
    private final class Preparation {
        let attempt: FilmPlaybackAttempt
        let target: FilmPlaybackTarget
        let contentKind: FilmPlaybackKind
        let sessionID: UUID
        let admission: UUID
        let generation: UUID
        var didCommit = false
        var started = false
        var terminal = false
        // The native release callback starts only after physical disposal. Its
        // RPC may publish a hidden receipt before returning, so retire output
        // synchronously without discarding the captured terminal identity.
        var retired = false
        var resourceID: String?
        var state: FilmMediaState?
        private var resourceReleased = false
        private var releaseTask: Task<Void, Error>?
        init(attempt: FilmPlaybackAttempt, target: FilmPlaybackTarget, contentKind: FilmPlaybackKind,
             sessionID: UUID, admission: UUID, generation: UUID) {
            self.attempt = attempt; self.target = target; self.contentKind = contentKind; self.sessionID = sessionID
            self.admission = admission; self.generation = generation
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
    private struct Command {
        let action: String
        let parameters: RuntimeObject
        let key: String
        let intent: UUID?
        let ui: Bool
    }
    public init(transport: FilmPlaybackTransport, media: any FilmPlaybackMediaControlling,
                searchContext: FilmSearchContext? = nil,
                playerPreferences: FilmPlayerPreferencesStore? = nil,
                request: @escaping @Sendable () async throws -> FilmPlaybackRequest) {
        self.transport = transport; self.media = media; makeRequest = request
        self.searchContext = searchContext
        preferencesStore = playerPreferences ?? FilmPlayerPreferencesStore()
        chapterModel = FilmPlaybackChapterModel(transport: transport)
        preferencesObservation = preferencesStore.$preferences.dropFirst().sink { [weak self] value in
            guard let self else { return }
            cancelTemporaryRate()
            updateChapters(enabled: value.recognizesChapters)
            objectWillChange.send()
        }
        chaptersObservation = chapterModel.$segments.dropFirst().sink { [weak self] _ in
            self?.objectWillChange.send()
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
            observeRuntimeClosure(); observe(); observeSearchContext()
            return nil
        }
        if allocatedSessionID != nil { return reopenAfterDrain() }
        opening = true; actionError = nil
        let generation = generation
        openingTask = Task { [weak self] in
            guard let self else { return }
            defer { self.opening = false; self.openingTask = nil }
            do {
                let previous = self.presentation
                let base = try await self.makeRequest()
                var request = self.reentryTarget.map { base.retargeted($0, errorCode: self.reentryError) } ?? base
                request.restoreOnly = restoreOnly || previous != nil
                self.restorePresentation = request.restoreOnly ? previous : nil
                if request.restoreOnly, let previous { request.restoreBrowsing(previous) }
                if let context = self.searchContext {
                    self.searchRevision = Int(context.snapshot.sequence)
                }
                guard !self.closed, self.generation == generation else { return }
                // Once admitted, an open is drained rather than cancelled: its
                // returned session must still be released if the page leaves.
                let value = try await self.transport.call("session.open", parameters: request.value)
                if let id = value.filmObject?["sessionId"]?.filmText.flatMap(UUID.init(uuidString:)) {
                    self.allocatedSessionID = id
                }
                let snapshot = try FilmPlaybackSnapshot(value: value)
                self.allocatedSessionID = snapshot.sessionID
                self.observeRuntimeClosure()
                guard !self.closed, self.generation == generation else { return }
                self.accept(snapshot); self.observe()
                if self.restorePresentation == nil { self.observeSearchContext() }
            } catch {
                if !self.closed, !Self.cancelled(error) {
                    self.actionError = Self.failure(error)
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
                self.observe(); self.observeSearchContext()
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
        allocatedSessionID = nil; snapshot = nil; observationError = nil; actionError = nil
        mediaStopError = nil; sessionReleaseError = nil
        committed = nil; preparing = nil; mediaStopped = true
        // An explicit selection can own the drain/reopen. Its publication fence
        // survives until that exact command, not the opening snapshot, is accepted.
        mediaState = FilmMediaState(); blockedAttemptID = nil
    }

    public func acknowledgeError() { actionError = nil }
    public func reportMediaControlError(_ error: FilmPlaybackError) { actionError = error }

    /// Reconnect only the observer. Disconnection never ends the business or media owner.
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

    /// App runtime availability, not a replay of a playback intent. Hidden
    /// pages retain display data and rebind when they are next activated.
    public func runtimeDidBecomeAvailable() {
        guard !presentationOwners.isEmpty else { return }
        let generation = generation
        Task { [weak self] in
            guard let self, (try? await self.transport.producer())?.isClosed == true,
                  self.generation == generation else { return }
            self.observeRuntimeClosure()
            await self.runtimeClosureTask?.value
            guard !self.presentationOwners.isEmpty, self.runtimeClosed, self.generation == generation else { return }
            _ = self.recoverRuntime()
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
                if !Self.cancelled(error) { self.observationError = Self.failure(error) }
            }
        }
        return recoveryTask
    }

    public func suspendObservation() async {
        let task = observationTask
        let admission = UUID(); observationAdmission = admission
        task?.cancel()
        await task?.value
        if observationAdmission == admission { observationTask = nil }
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
            // Detach the completed wait before close: its drain must never await
            // this task. Revoke retained admission before any existing close wait.
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

    private func accept(_ value: FilmPlaybackSnapshot, reconcileMedia: Bool = true) {
        guard value.sessionID == allocatedSessionID else { return }
        if let current = snapshot, current.sessionID == value.sessionID, value.sequence <= current.sequence { return }
        snapshot = value
        if let previous = restorePresentation {
            if !value.browsing.loading {
                if let error = value.browsing.error ?? value.error {
                    actionError = FilmPlaybackError(code: error)
                } else {
                    restorePresentation = nil
                    presentation = value.retainingLoadedPresentation(previous)
                    observeSearchContext()
                }
            }
        } else { presentation = value.retainingLoadedPresentation(presentation) }
        reentryTarget = value.target
        reentryError = value.error
        if reconcileMedia { reconcilePreparation() }
        updateChapters()
    }

    private func observeSearchContext() {
        guard !closed, let context = searchContext else { return }
        if searchObservation == nil {
            searchObservation = context.session.$snapshot.sink { [weak self] _ in
                // @Published sends before assignment; build from the settled
                // context on the next actor turn, with one coalescing worker.
                self?.scheduleSearchUpdate()
            }
        }
        scheduleSearchUpdate()
    }

    private func scheduleSearchUpdate() {
        guard !closed, !runtimeClosed, restorePresentation == nil, allocatedSessionID != nil,
              searchUpdateTask == nil, let context = searchContext else { return }
        let generation = generation
        searchUpdateTask = Task { [weak self] in
            guard let self else { return }
            defer { self.searchUpdateTask = nil }
            while !Task.isCancelled, !self.closed, !self.runtimeClosed, self.generation == generation,
                  Int(context.snapshot.sequence) > self.searchRevision {
                guard let update = context.playbackUpdate() else { return }
                guard !Task.isCancelled, !self.closed, !self.runtimeClosed, self.generation == generation else { return }
                self.searchRevision = update.revision
                await self.enqueueAction("search.update", parameters: update.fields, ui: true)?.value
            }
        }
    }

    private func reconcilePreparation() {
        guard (!closed || retained), restorePresentation == nil, awaitingIntent == nil, let value = snapshot else { return }
        if let attempt = value.attempt, !attempt.prepareOnly, attempt.id != blockedAttemptID,
           attempt.id != committedID {
            prepare(attempt, sessionID: value.sessionID)
        } else if (preparing != nil && preparing?.didCommit != true && preparing?.retired != true) ||
            requestedPreparation != nil {
            cancelPreparation()
        }
        // Revocation is a business authorization change, not UI cancellation.
        // A replacement can coexist with the old current until real output starts.
        if let committed, value.current?.attemptID != committed.attempt.id,
           value.attempt?.id != committed.attempt.id {
            if committed.retired {
                // The Node snapshot hides a disposed receipt but retains its
                // existing feedback identity. The captured preparation still
                // owns an accurate terminal event; no tombstone map is needed.
                mediaState = FilmMediaState()
            } else if stoppingMedia == nil {
                stoppingMedia = Task { [weak self, media] in
                    do {
                        try await media.stop(sessionID: value.sessionID)
                        if let self, self.committed === committed {
                            self.committed = nil; self.mediaState = FilmMediaState()
                            self.mediaStopped = true
                        }
                    } catch { self?.actionError = Self.failure(error) }
                    self?.stoppingMedia = nil
                }
            }
        }
    }

    /// One serial RPC worker. UI intents coalesce by operation group; progress
    /// keeps only its latest queued value. Actual start/terminal facts stay ordered.
    @discardableResult
    public func send(_ action: String, parameters: RuntimeObject = [:]) -> Task<Void, Never>? {
        if Self.mediaActions.contains(action) { cancelTemporaryRate() }
        if action == "stop", closed || closingTask != nil || openingTask != nil || reopeningTask != nil {
            let admission = UUID(); commandAdmission = admission
            let generation = generation
            return Task { [weak self] in
                guard let self, self.generation == generation, self.commandAdmission == admission else { return }
                await self.close()
            }
        }
        if openingTask == nil {
            restorePresentation = nil
        }
        let explicitPlayback = action == "play" || action == "select.episode" || action == "select.candidate"
        if (explicitPlayback || runtimeClosed || openingTask != nil),
           closed || openingTask != nil || reopeningTask != nil {
            // Admit the explicit intent before drain/reopen can publish an older attempt.
            commandAdmission = UUID(); awaitingIntent = commandAdmission
            let opening = open()
            let reopeningAdmission = reopeningAdmission, commandAdmission = commandAdmission
            return Task { [weak self] in
                await opening?.value
                guard let self else { return }
                defer {
                    if self.commandAdmission == commandAdmission { self.awaitingIntent = nil }
                }
                guard !Task.isCancelled, !self.closed,
                      self.reopeningAdmission == reopeningAdmission, self.commandAdmission == commandAdmission else { return }
                guard self.allocatedSessionID != nil else { return }
                await self.enqueueAction(action, parameters: parameters, ui: true)?.value
            }
        }
        guard allocatedSessionID != nil, !closed else { return nil }
        return enqueueAction(action, parameters: parameters, ui: true)
    }

    /// System navigation belongs to the actual native attempt, including a
    /// legitimately retained PiP owner. It does not resurrect a released session.
    public func navigateFromSystem(forward: Bool) -> Bool {
        guard (!closed || retained), !released, allocatedSessionID != nil,
              let committed, committed.started, !committed.terminal, !committed.retired else { return false }
        enqueueAction(forward ? "next" : "previous", parameters: [:], ui: false)
        return true
    }

    @discardableResult
    private func enqueueAction(_ action: String, parameters: RuntimeObject, ui: Bool) -> Task<Void, Never>? {
        var intent: UUID?
        if Self.mediaActions.contains(action) || action == "browse.metadata" {
            commandAdmission = UUID(); awaitingIntent = commandAdmission
            intent = commandAdmission
            // Blocking or retry may reject without replacing anything.
            // Hold publication, not the exact pending media/receipt it can retain.
            if action != "block" && action != "media.retry" {
                blockedAttemptID = snapshot?.attempt?.id
                cancelPreparation()
            }
        }
        let key = intent == nil || action == "browse.metadata" ? action : "media.intent"
        enqueue(.init(action: action, parameters: parameters, key: key, intent: intent, ui: ui))
        return commandWorker
    }

    private func enqueue(_ command: Command) {
        if let index = queuedCommands.firstIndex(where: { $0.key == command.key }) {
            queuedCommands[index] = command
        } else if !command.ui, let index = queuedCommands.firstIndex(where: { $0.ui }) {
            queuedCommands.insert(command, at: index)
        } else { queuedCommands.append(command) }
        if commandWorker == nil {
            commandWorker = Task { [weak self] in
                guard let self else { return }
                await self.runCommands()
            }
        }
    }

    private func runCommands() async {
        while !queuedCommands.isEmpty, let sessionID = allocatedSessionID, !released {
            let command = queuedCommands.removeFirst()
            if closed && (command.ui || !retained && command.intent != nil) { continue }
            if let intent = command.intent, command.key == "media.intent", intent != commandAdmission { continue }
            do {
                if command.action == "stop" {
                    await preparationWorker?.value
                    await cancellingPreparation?.value
                    await stoppingMedia?.value
                    try await media.stop(sessionID: sessionID)
                    mediaStopped = true
                    // Native disposal may precede its terminal callback. Those
                    // captured facts must reach Node before stop replaces them.
                    try await drainFeedback(sessionID: sessionID)
                    try await flushProgress()
                    committed = nil; mediaState = FilmMediaState()
                }
                let previousCurrent = committed
                let value = try await callCommand(command.action, fields: command.parameters, sessionID: sessionID)
                // Close drains admitted RPCs without returning publication to a
                // departed UI. Native/system feedback still belongs to its owner.
                if command.ui, closed { continue }
                if let intent = command.intent, intent == awaitingIntent {
                    // The RPC snapshot is the latest command's admission barrier.
                    // Older SSE attempts cannot reacquire publication while it waits.
                    awaitingIntent = nil; blockedAttemptID = nil
                }
                accept(value, reconcileMedia: false)
                if command.action != "browse.refresh" { reconcilePreparation() }
                if command.action == "play", command.intent == commandAdmission,
                   let previousCurrent, committed === previousCurrent,
                   previousCurrent.started, !previousCurrent.retired, !previousCurrent.terminal,
                   snapshot?.attempt == nil, snapshot?.current?.attemptID == previousCurrent.attempt.id {
                    media.resume(sessionID: sessionID, attemptID: previousCurrent.attempt.id, userInitiated: true)
                }
            } catch {
                if command.ui, closed { continue }
                if command.intent == awaitingIntent { awaitingIntent = nil }
                if !Self.cancelled(error), command.intent == nil || command.intent == commandAdmission,
                   !queuedCommands.contains(where: { $0.key == command.key }),
                   command.ui || (error as? FilmPlaybackError)?.code != "STALE_SESSION" {
                    actionError = Self.failure(error)
                }
                if command.action == "block" || command.action == "media.retry" { reconcilePreparation() }
            }
        }
        commandWorker = nil
    }

    private func drainFeedback(sessionID: UUID) async throws {
        while let index = queuedCommands.firstIndex(where: { !$0.ui && $0.intent == nil }) {
            let command = queuedCommands.remove(at: index)
            do { accept(try await callCommand(command.action, fields: command.parameters, sessionID: sessionID)) }
            catch let error as FilmPlaybackError where error.code == "STALE_SESSION" {
                // A later operation can replace one of Node's two identities.
                // Never retarget the retired fact to the new attempt.
            }
        }
    }

    private func callCommand(_ action: String, fields: RuntimeObject, sessionID: UUID) async throws -> FilmPlaybackSnapshot {
        var fields = fields
        fields["sessionId"] = .string(sessionID.uuidString.lowercased()); fields["action"] = .string(action)
        let value = try FilmPlaybackSnapshot(value: await transport.call("session.command", parameters: .object(fields)))
        guard value.sessionID == sessionID else { throw FilmPlaybackError.invalidResponse }
        return value
    }

    public func selectEpisode(_ position: FilmEpisodePosition) {
        send("select.episode", parameters: ["episode": .object(position.value)])
    }
    public func selectCandidate(_ id: String) { send("select.candidate", parameters: ["candidateId": .string(id)]) }
    public func browse(sourceID: String?, lineID: FilmPlaybackIdentifier? = nil) {
        var fields: RuntimeObject = ["sourceId": sourceID.map(RuntimeValue.string) ?? .null]
        fields["lineId"] = lineID?.value
        send("browse", parameters: fields)
    }
    public func browseSeason(_ number: Int) { send("browse.season", parameters: ["season": .number(Double(number))]) }
    @discardableResult
    public func refreshBrowsing() -> Task<Void, Never>? {
        actionError = nil
        return send("browse.refresh")
    }
    @discardableResult
    public func retryMedia(attemptID: String? = nil) -> Task<Void, Never>? {
        let id = attemptID ?? snapshot?.attempt?.id ?? snapshot?.current?.attemptID
        actionError = nil
        return send("media.retry", parameters: id.map { ["attemptId": .string($0)] } ?? [:])
    }
    public func browseMetadata(_ id: String, keepSource: Bool = false) {
        var fields: RuntimeObject = ["metadataSourceId": .string(id)]
        if keepSource { fields["keepSource"] = .bool(true) }
        send("browse.metadata", parameters: fields)
    }
    public func selectPan(_ provider: FilmPanProvider) { send("pan", parameters: ["provider": .string(provider.rawValue)]) }
    public func selectQuality(_ quality: FilmResourceQuality) { send("quality", parameters: ["quality": .string(quality.rawValue)]) }
    public func setPreview(_ enabled: Bool) { send("preview", parameters: ["enabled": .bool(enabled)]) }
    public func setShortVideo(_ enabled: Bool) { send("shortvideo", parameters: ["enabled": .bool(enabled)]) }

    private func cancelPreparation() {
        mediaAdmission = UUID(); requestedPreparation = nil
        activePreparation?.cancel()
        resumeCommitWaiter()
        if activePreparation != nil, cancellingPreparation == nil, let sessionID {
            cancellingPreparation = Task { [weak self, media] in
                do { try await media.cancelPending(sessionID: sessionID) }
                catch { self?.actionError = Self.failure(error) }
                self?.cancellingPreparation = nil
            }
        }
        // Retain the worker until the cancelled HTTP/decoder/lease physically drains.
    }

    private func prepare(_ attempt: FilmPlaybackAttempt, sessionID: UUID) {
        guard preparingID != attempt.id, requestedPreparation?.attempt.id != attempt.id else { return }
        cancelPreparation()
        guard let snapshot else { return }
        let contentKind = snapshot.metadataSources.first { $0.id == snapshot.metadataSourceID }?.kind ?? snapshot.target.kind
        requestedPreparation = .init(attempt: attempt, target: snapshot.target, contentKind: contentKind, sessionID: sessionID,
                                     admission: mediaAdmission, generation: generation)
        if preparationWorker == nil {
            preparationWorker = Task { [weak self] in
                guard let self else { return }
                await self.runPreparations()
            }
        }
    }

    private func runPreparations() async {
        while let request = requestedPreparation {
            requestedPreparation = nil; preparing = request
            await cancellingPreparation?.value
            await stoppingMedia?.value; stoppingMedia = nil
            activePreparation = Task { [weak self] in
                guard let self else { return }
                await self.prepareMedia(request)
            }
            await activePreparation?.value
            activePreparation = nil; preparing = nil
        }
        preparationWorker = nil
    }

    private func prepareMedia(_ request: Preparation) async {
        var handedToMedia = false
        do {
            try Task.checkCancellation()
            guard ownsPending(request) else { throw CancellationError() }
            // Do not cancel an admitted receipt RPC; obtain its lease ID to release it.
            let value = try await Task { [transport] in
                try await transport.call("session.attempt", parameters: .object([
                    "sessionId": .string(request.sessionID.uuidString.lowercased()),
                    "attemptId": .string(request.attempt.id),
                ]))
            }.value
            request.resourceID = value.filmObject?["id"]?.filmText
            let input = try FilmPlaybackMediaInput(value: value, sessionID: request.sessionID, attemptID: request.attempt.id)
            try Task.checkCancellation()
            guard ownsPending(request) else { throw CancellationError() }
            let transport = transport
            request.resourceID = input.resourceID
            handedToMedia = true
            mediaStopped = false
            try await media.play(input, preservePosition: preservesPosition(request), waitForCommit: { [weak self] in
                guard let self else { throw CancellationError() }
                try await self.waitForCommit(request)
            }, canCommit: { [weak self] in
                self?.admits(request) == true
            }, release: { [weak self] in
                request.retired = true
                try await request.releaseResource(transport: transport)
                if let self, self.committed === request { self.mediaState = FilmMediaState() }
            }) { [weak self] event in
                self?.receiveMedia(event, request: request)
            }
        } catch {
            let preparationError = error
            if eligible(request), !Self.cancelled(preparationError), !request.terminal {
                actionError = Self.failure(preparationError)
                if handedToMedia {
                    request.terminal = true
                    feedback("media.failed", attemptID: request.attempt.id, code: Self.failure(preparationError).code)
                } else {
                    // An RPC/receipt failure is not a decoder failure or a watch
                    // fact. A fresh explicit intent may request this receipt
                    // again; unrelated SSE updates must not retry it in a loop.
                    blockedAttemptID = request.attempt.id
                }
            }
            do {
                try await request.releaseResource(transport: transport)
            } catch { actionError = Self.failure(error) }
        }
    }

    private static func releaseMedia(transport: FilmPlaybackTransport, sessionID: UUID,
                                     attemptID: String, resourceID: String) async throws {
        let receipt = try await Task {
            try await transport.call("session.media.release", parameters: .object([
                "sessionId": .string(sessionID.uuidString.lowercased()),
                "attemptId": .string(attemptID), "resourceId": .string(resourceID),
            ]))
        }.value
        guard receipt.filmObject?["released"] == .bool(true) else { throw FilmPlaybackError.invalidResponse }
    }

    private func admits(_ request: Preparation) -> Bool {
        awaitingIntent == nil && ownsPending(request)
    }

    private func ownsPending(_ request: Preparation) -> Bool {
        eligible(request) && !request.retired && snapshot?.attempt?.id == request.attempt.id &&
            snapshot?.attempt?.prepareOnly == false
    }

    private func eligible(_ request: Preparation) -> Bool {
        (!closed || retained) && generation == request.generation && mediaAdmission == request.admission
    }

    private func waitForCommit(_ request: Preparation) async throws {
        while true {
            try Task.checkCancellation()
            guard ownsPending(request) else { throw CancellationError() }
            guard awaitingIntent != nil else { return }
            let id = UUID()
            try await withTaskCancellationHandler {
                try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<Void, Error>) in
                    if Task.isCancelled { continuation.resume(throwing: CancellationError()) }
                    else { commitWaiter = (id, continuation) }
                }
            } onCancel: {
                Task { @MainActor [weak self] in
                    guard self?.commitWaiter?.id == id else { return }
                    self?.resumeCommitWaiter()
                }
            }
        }
    }

    private func resumeCommitWaiter() {
        let waiter = commitWaiter
        commitWaiter = nil
        waiter?.continuation.resume()
    }

    private func preservesPosition(_ request: Preparation) -> Bool {
        guard let current = committed, current.started, !current.retired,
              current.target.id.utf8.elementsEqual(request.target.id.utf8) else { return false }
        if request.contentKind == .movie && current.contentKind == .movie { return true }
        let a = current.attempt.candidate.position, b = request.attempt.candidate.position
        if let first = a.globalEpisode, let second = b.globalEpisode { return first == second }
        if let firstSeason = a.season, let secondSeason = b.season,
           let firstEpisode = a.episode, let secondEpisode = b.episode {
            return firstSeason == secondSeason && firstEpisode == secondEpisode
        }
        return current.attempt.candidate.id.utf8.elementsEqual(request.attempt.candidate.id.utf8)
    }

    private func receiveMedia(_ event: FilmMediaEvent, request: Preparation) {
        let sessionID = request.sessionID
        guard allocatedSessionID == sessionID, generation == request.generation, !released,
              preparing === request || committed === request else { return }
        switch event {
        case .state(let state):
            guard state.sessionID == sessionID, state.attemptID == request.attempt.id else { return }
            var state = state
            state.resourceID = request.resourceID
            request.state = state
            guard request.didCommit else { return }
            if committed === request, !request.retired { mediaState = state }
            if request.started && !request.terminal { queueProgress(state) }
        case .started(let id):
            guard id == request.attempt.id, admits(request), !request.started else { return }
            cancelTemporaryRate()
            // The shared owner's started callback is synchronous with its final
            // cancelled-task fence and real output commit. No second commit shim.
            committed = request; request.didCommit = true; request.started = true
            if let state = request.state { mediaState = state }
            feedback("media.started", attemptID: id)
            if let state = request.state { queueProgress(state) }
            closeRetainedWithoutSystemOwner(request)
        case .failed(let id, let error):
            guard id == request.attempt.id, request.didCommit || eligible(request), !request.terminal else { return }
            actionError = error
            if request.started, let state = request.state { queueProgress(state) }
            request.terminal = true
            cancelTemporaryRate(); updateChapters()
            feedback("media.failed", attemptID: id, code: error.code)
        case .ended(let id):
            guard id == request.attempt.id, request.didCommit, !request.terminal else { return }
            if request.started, let state = request.state { queueProgress(state) }
            request.terminal = true
            cancelTemporaryRate(); updateChapters()
            feedback("media.ended", attemptID: id)
        case .ownerEnded(let id):
            guard id == request.attempt.id, committed === request else { return }
            Task { [weak self] in
                guard let self, self.committed === request else { return }
                await self.close()
            }
        case .systemOwnerEnded(let id):
            guard id == request.attempt.id, committed === request, retained else { return }
            closeRetainedWithoutSystemOwner(request)
        }
    }

    private func closeRetainedWithoutSystemOwner(_ request: Preparation) {
        guard retained, committed === request, !hasSystemOwner else { return }
        Task { [weak self] in
            guard let self, self.retained, self.committed === request, !self.hasSystemOwner else { return }
            await self.close()
        }
    }

    private func feedback(_ action: String, attemptID: String, code: String? = nil) {
        var parameters: RuntimeObject = ["attemptId": .string(attemptID)]
        if let code { parameters["code"] = .string(code) }
        enqueue(.init(action: action, parameters: parameters,
                      key: "\(action).\(attemptID)", intent: nil, ui: false))
    }
    private func progressFields(_ state: FilmMediaState) -> RuntimeObject {
        ["attemptId": .string(state.attemptID!), "position": .number(state.position),
         "duration": .number(state.duration), "paused": .bool(state.paused)]
    }
    private func queueProgress(_ state: FilmMediaState) {
        enqueue(.init(action: "media.progress", parameters: progressFields(state),
                      key: "media.progress.\(state.attemptID!)", intent: nil, ui: false))
    }
    private func flushProgress() async throws {
        guard !released, let committed, committed.started, !committed.terminal,
              let state = committed.state, let sessionID else { return }
        accept(try await callCommand("media.progress", fields: progressFields(state), sessionID: sessionID))
    }

    public func pauseOrResume() {
        guard !runtimeClosed else { return }
        guard let current = committed, current.started, !current.retired, !current.terminal,
              media.isCurrent(sessionID: current.sessionID, attemptID: current.attempt.id) else {
            send("play"); return
        }
        if mediaState.paused { media.resume(sessionID: current.sessionID, attemptID: current.attempt.id, userInitiated: true) }
        else { _ = media.pause(sessionID: current.sessionID, attemptID: current.attempt.id) }
    }
    var controlIdentity: FilmPlayerControlIdentity? {
        guard !closed, !runtimeClosed, let committed, committed.started, !committed.retired, !committed.terminal,
              media.isCurrent(sessionID: committed.sessionID, attemptID: committed.attempt.id),
              mediaState.sessionID == committed.sessionID, mediaState.attemptID == committed.attempt.id else { return nil }
        return .init(sessionID: committed.sessionID, attemptID: committed.attempt.id)
    }

    public func seek(to value: Double) {
        guard value.isFinite, let identity = controlIdentity, mediaState.duration > 0 else { return }
        media.seek(sessionID: identity.sessionID, to: min(mediaState.duration, max(0, value)))
    }
    public func seekByStep(forward: Bool) {
        seek(to: mediaState.position + (forward ? 1 : -1) * playerPreferences.seekStep)
    }
    public func setRate(_ value: Double) {
        guard value.isFinite, (0.5...3).contains(value), let identity = controlIdentity else { return }
        cancelTemporaryRate()
        guard identity == controlIdentity else { return }
        media.setRate(sessionID: identity.sessionID, rate: min(value, actualEngine.maximumRate))
    }
    public func setVolume(_ value: Double) {
        guard value.isFinite, let identity = controlIdentity else { return }
        media.setVolume(sessionID: identity.sessionID, volume: min(1, max(0, value)))
    }

    @discardableResult
    public func beginTemporaryRate() -> UUID? {
        cancelTemporaryRate()
        guard let identity = controlIdentity else { return nil }
        let token = UUID()
        heldRate = (token, identity)
        media.setTemporaryRate(sessionID: identity.sessionID, attemptID: identity.attemptID,
                               rate: min(playerPreferences.holdRate, actualEngine.maximumRate))
        return token
    }
    public func endTemporaryRate(_ token: UUID) {
        guard heldRate?.token == token else { return }
        cancelTemporaryRate()
    }
    public func cancelTemporaryRate() {
        guard let heldRate else { return }
        self.heldRate = nil
        guard media.isCurrent(sessionID: heldRate.identity.sessionID, attemptID: heldRate.identity.attemptID) else { return }
        media.setTemporaryRate(sessionID: heldRate.identity.sessionID, attemptID: heldRate.identity.attemptID, rate: nil)
    }

    public func skipChapter() {
        if let skipSegment { seek(to: skipSegment.end) }
    }
    private func updateChapters(enabled: Bool? = nil) {
        let current = controlIdentity
        chapterModel.update(sessionID: current?.sessionID, attemptID: current?.attemptID,
                            resourceID: current == nil ? nil : mediaState.resourceID,
                            chapters: current == nil ? [] : mediaState.chapters,
                            enabled: enabled ?? playerPreferences.recognizesChapters)
    }
    public func startPictureInPicture() { if !runtimeClosed, let sessionID { media.startPictureInPicture(sessionID: sessionID) } }
    public func stopPictureInPicture() { if let sessionID { media.stopPictureInPicture(sessionID: sessionID) } }
    public var hasSystemOwner: Bool {
        guard let committed, committed.started, !committed.terminal, !committed.retired, !released,
              let sessionID else { return false }
        return media.hasSystemOwner(sessionID: sessionID)
    }

    public func applicationDidEnterBackground() async {
        await close(allowSystemOwner: true)
    }
    #if os(iOS)
    public func makeVideoView() -> UIView? { sessionID.map { media.makeVideoView(sessionID: $0) } }
    public func setVideoScale(_ scale: FilmVideoScale) {
        if let sessionID { media.setVideoScale(sessionID: sessionID, scale: scale) }
    }
    #endif

    public func close(allowSystemOwner: Bool = false) async {
        cancelTemporaryRate()
        chapterModel.update(sessionID: nil, attemptID: nil, resourceID: nil, chapters: [], enabled: false)
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
        searchObservation?.cancel(); searchObservation = nil
        searchUpdateTask?.cancel()
        retained = allowSystemOwner && !runtimeClosed && hasSystemOwner && committed?.started == true
        cancelPreparation(); observationAdmission = UUID(); observationTask?.cancel()
        let keepNative = retained
        closingAdmission = UUID()
        closingTask = Task { [self] in
            await searchUpdateTask?.value
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
                }
            }
            await commandWorker?.value
            do { try await flushProgress() }
            catch { failure = failure ?? Self.failure(error) }
            if !keepNative, media.isLocallyDrained(sessionID: sessionID) {
                committed = nil; mediaState = FilmMediaState()
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

    /// Test/host synchronization waits for admitted work, not timer polling.
    public func waitForPendingWork() async {
        await recoveryTask?.value
        await reopeningTask?.value
        await closingTask?.value
        while openingTask != nil || commandWorker != nil || searchUpdateTask != nil || preparationWorker != nil ||
            cancellingPreparation != nil || stoppingMedia != nil {
            await openingTask?.value; await searchUpdateTask?.value; await commandWorker?.value; await preparationWorker?.value
            await cancellingPreparation?.value; await stoppingMedia?.value
        }
    }

    private static let mediaActions: Set<String> = [
        "prepare", "play", "select.episode", "select.candidate", "pan", "quality", "switch", "block", "next", "previous", "stop",
        "media.retry",
    ]
    private static func cancelled(_ error: Error) -> Bool {
        error is CancellationError || (error as? FilmPlaybackError)?.code == "CANCELLED"
    }
    static func failure(_ error: Error) -> FilmPlaybackError {
        (error as? FilmPlaybackError) ?? FilmPlaybackError.invalidResponse
    }
    deinit {
        recoveryTask?.cancel()
        runtimeClosureTask?.cancel(); searchUpdateTask?.cancel()
        observationTask?.cancel(); activePreparation?.cancel()
    }
}
