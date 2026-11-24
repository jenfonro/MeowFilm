import Dispatch
import Foundation
import MeowContracts
import MeowDebug
import MeowMusicFeature

final class MusicNativeFMSession {
    let source: MusicRemoteQueueSource
    let action: MusicHomeAction
    let ownsSource: Bool
    var continueEnabled: Bool
    var attempted = Set<String>()
    var pending: Task<Void, Never>?
    var revocationWatcher: Task<Void, Never>?
    var buffered: [MusicQueueTrackReference] = []
    var exhausted = false
    var error: MusicServiceError?
    var errorID: UUID?
    init(source: MusicRemoteQueueSource, action: MusicHomeAction, continueEnabled: Bool, ownsSource: Bool = false) {
        self.source = source; self.action = action; self.continueEnabled = continueEnabled; self.ownsSource = ownsSource
    }
}

extension NativeMusicService: MusicFMPlaybackServing {
    public func rememberedFM() async throws -> MusicHomeAction {
        let own = epoch
        let binding = try await currentHomeBinding()
        let scope = try await fmPreferenceScope(binding)
        try await checkHomeBinding(binding, epoch: own)
        guard let saved = try read().playback.fmSelections.first(where: { $0.fmScope == scope }) else {
            return .fm(mode: .default, submode: "", entry: nil)
        }
        let action = try saved.toAction()
        try musicRequire(action.isPersonalFM, .corruptStorage)
        return action
    }
    public func fmPlaybackSnapshot() async throws -> MusicFMPlaybackSnapshot? {
        try await restoreFMSessionIfNeeded()
        try check()
        guard let session = fmPlayback, session.source.authorization.isActive else { return nil }
        return .init(id: session.source.id, action: session.action, continueEnabled: session.continueEnabled,
                     loading: session.pending != nil, error: session.error, errorID: session.errorID)
    }
    public func setFMContinue(_ enabled: Bool, sessionID: UUID) async throws {
        try check()
        try await restoreFMSessionIfNeeded(force: true)
        guard let session = fmPlayback, session.source.id == sessionID else { throw MusicServiceError.targetChanged }
        try session.source.authorization.check()
        let savedFM = try MusicQueueFMState.fromAction(session.action, continueEnabled: enabled)
        try session.source.authorization.perform {
            try similarQueueTransaction(enabled ? [.queue, .playback] : [.queue]) { d in
                d.queue.fm = savedFM
                if enabled { selectContinuationMode(&d) }
            }
        }
        session.continueEnabled = enabled
        emitSimilar()
        if enabled { try fmBoundary() }
    }
    func installFMPlayback(source: MusicRemoteQueueSource, action: MusicHomeAction,
                           continueEnabled: Bool, ownsSource: Bool) {
        let session = MusicNativeFMSession(source: source, action: action,
                                           continueEnabled: continueEnabled, ownsSource: ownsSource)
        let sourceID = source.id
        let events = source.authorization.revocations()
        session.revocationWatcher = Task { [weak self] in
            for await _ in events {
                if Task.isCancelled { return }
                await self?.fmSessionAuthorizationRevoked(sourceID)
                return
            }
        }
        fmPlayback = session
        fmRestored = true
        fmRestoreRetryAfter = 0
    }
    private func waitForFMRestoration(_ task: Task<Void, Error>, own: UUID) async throws {
        let waiter = SingleShotAsyncGate<Void>()
        let bridge = Task {
            do {
                try await task.value
                waiter.finish(.success(()))
            } catch {
                waiter.finish(.failure(error))
            }
        }
        defer { bridge.cancel() }
        try await withTaskCancellationHandler {
            try await waiter.value()
        } onCancel: {
            bridge.cancel()
            waiter.finish(.failure(MusicServiceError.cancelled))
        }
        try check(own)
    }
    func restoreFMSessionIfNeeded(force: Bool = false) async throws {
        try check()
        if let existing = fmPlayback, existing.source.authorization.isActive {
            fmRestored = true
            fmRestoreRetryAfter = 0
            return
        }
        guard !fmRestored else { return }
        if let active = fmRestoration {
            try await waitForFMRestoration(active.task, own: epoch)
            return
        }
        let nowNs = DispatchTime.now().uptimeNanoseconds
        if !force && fmRestoreRetryAfter > nowNs { return }
        let d = try read()
        guard let saved = d.queue.fm else {
            fmRestored = true
            return
        }
        guard d.queue.source?.sourceType == "fm", !d.queue.entries.isEmpty else {
            try similarQueueTransaction([.queue]) { draft in
                if draft.queue.fm == saved { draft.queue.fm = nil }
            }
            fmRestored = true
            return
        }
        let restoration = UUID(), own = epoch, sourceMeta = d.queue.source
        let task = Task<Void, Error> { [self] in
            try await self.runFMRestoration(id: restoration, saved: saved, sourceMeta: sourceMeta, own: own)
        }
        fmRestoration = (restoration, task)
        try await waitForFMRestoration(task, own: own)
    }
    private func runFMRestoration(id restoration: UUID, saved: MusicQueueFMState,
                                  sourceMeta: MusicSourceRecord?, own: UUID) async throws {
        defer {
            if fmRestoration?.id == restoration { fmRestoration = nil }
        }
        var openedSource: MusicRemoteQueueSource?
        do {
            let action = try saved.toAction()
            try musicRequire(action.isPersonalFM, .corruptStorage)
            let title = MusicModel.firstText(sourceMeta?.title, sourceMeta?.sourceTitle)
            let resolvedTitle = title.isEmpty ? "私人漫游" : title
            let source = try await work(label: "fmRestore", draining: true) { [self] in
                try await self.openHomeActionQueue(action, title: resolvedTitle)
            }
            openedSource = source
            try check(own); try source.authorization.check()
            let current = try read()
            try musicRequire(fmRestoration?.id == restoration && current.queue.fm == saved &&
                current.queue.source?.sourceType == "fm" && !current.queue.entries.isEmpty, .targetChanged)
            let continueEnabled = saved.continueEnabled &&
                current.playback.mode == MusicPlaybackMode.loop.rawValue
            let newSessionID = source.id.uuidString.lowercased()
            let updatedFM = try MusicQueueFMState.fromAction(action, continueEnabled: continueEnabled)
            try source.authorization.perform {
                try similarQueueTransaction([.queue]) { draft in
                    try musicRequire(draft.queue.fm == saved && draft.queue.source?.sourceType == "fm", .targetChanged)
                    let previousSessionID = draft.queue.source?.remoteSession
                    draft.queue.source?.remoteSession = newSessionID
                    for index in draft.queue.entries.indices {
                        if draft.queue.entries[index].context.sourceType == "fm",
                           previousSessionID == nil || draft.queue.entries[index].context.remoteSession == previousSessionID {
                            draft.queue.entries[index].context.remoteSession = newSessionID
                        }
                    }
                    draft.queue.fm = updatedFM
                }
            }
            installFMPlayback(source: source, action: action, continueEnabled: continueEnabled, ownsSource: true)
            openedSource = nil
            emitSimilar()
        } catch {
            if let openedSource { openedSource.revoke(); await openedSource.close() }
            let failure = MusicServiceError.sanitized(error)
            if failure == .commitUnconfirmed { throw failure }
            try check(own)
            guard fmRestoration?.id == restoration else { throw MusicServiceError.cancelled }
            if failure.isAuthenticationFailure || [.staleAccess, .notFound, .unsupported].contains(failure) {
                try similarQueueTransaction([.queue]) { draft in
                    if draft.queue.fm == saved { draft.queue.fm = nil }
                }
                fmRestored = true
                fmRestoreRetryAfter = 0
                emitSimilar()
            } else {
                if failure != .cancelled {
                    fmRestoreRetryAfter = DispatchTime.now().uptimeNanoseconds + 10_000_000_000
                    DebugRuntime.logError(.musicPlayback, sub: "recommendations", event: "fmRestore.error", error: failure)
                }
                throw failure
            }
        }
    }
    private func fmSessionAuthorizationRevoked(_ sourceID: UUID) {
        guard fmPlayback?.source.id == sourceID else { return }
        clearFMPlayback()
        fmRestored = false
        emitSimilar()
    }
    func fmPreferenceScope(_ binding: MusicNativeHomeBinding) async throws -> String {
        guard let owner = accounts as? any MusicNativeFMProviding else {
            return binding.loggedIn ? "wy-fm:" + binding.scope : "wy-fm:guest"
        }
        let scope = try await owner.nativeFMPreferenceScope(binding)
        try musicRequire(!scope.isEmpty, .invalidResponse)
        return scope
    }
    func clearFMPlayback() {
        if let restoring = fmRestoration {
            fmRestoration = nil
            restoring.task.cancel()
            cancelWork("fmRestore")
            retainSimilarDrain(Task { _ = await restoring.task.result })
        }
        fmRestoreRetryAfter = 0
        guard let session = fmPlayback else { return }
        fmPlayback = nil
        session.revocationWatcher?.cancel()
        session.revocationWatcher = nil
        session.pending?.cancel()
        if let pending = session.pending { retainSimilarDrain(pending) }
        if session.ownsSource {
            session.source.revoke()
            retainSimilarDrain(Task { await session.source.close() })
        }
    }
    func fmBoundary() throws {
        guard let session = fmPlayback, session.continueEnabled, session.pending == nil, !session.exhausted,
              continuationIsDue(try read()) else { return }
        let d = try read()
        let event = d.queue.entries.last?.id ?? ""
        guard !session.buffered.isEmpty || !session.attempted.contains(event) else { return }
        try session.source.authorization.check()
        session.attempted.insert(event); session.error = nil; session.errorID = nil
        if !session.buffered.isEmpty {
            try appendFMBatch(session, tracks: session.buffered)
            session.buffered = []
            emitSimilar()
            return
        }
        session.pending = Task { await loadFMBatch(session) }
        emitSimilar()
    }
    func loadFMBatch(_ session: MusicNativeFMSession) async {
        defer { session.pending = nil; if fmPlayback === session { emitSimilar() } }
        let own = epoch, source = session.source
        do {
            let batch = try await work(label: "fmBatch", draining: true) { try await source.nextFMReferences(intent: source.authorization) }
            try await source.validate()
            try check(own); try source.authorization.check()
            guard fmPlayback === session, !Task.isCancelled else { return }
            session.exhausted = batch.isEmpty
            if session.continueEnabled { try appendFMBatch(session, tracks: batch) }
            else { session.buffered = batch }
        } catch {
            guard fmPlayback === session, epoch == own, source.authorization.isActive else { return }
            let failure = MusicServiceError.sanitized(error)
            if failure != .cancelled { session.error = failure; session.errorID = UUID() }
        }
    }
    func appendFMBatch(_ session: MusicNativeFMSession, tracks: [MusicQueueTrackReference]) throws {
        guard !tracks.isEmpty else { return }
        let context = try remoteContext(session.source)
        var live: [String: MusicRecommendationMetadata] = [:]
        try session.source.authorization.perform {
            try similarQueueTransaction([.queue]) { d in
                try musicRequire(d.queue.source?.remoteSession == session.source.id.uuidString.lowercased(), .targetChanged)
                let songs = try repositorySongs(tracks.map(\.songID), d)
                let rows = try makeEntries(songs, state: &d, context: context)
                for i in rows.indices {
                    if let value = tracks[i].recommendation {
                        live[rows[i].id] = try MusicWireCodec.parseRecommendation(from: value)
                    }
                }
                d.queue.entries += rows
                d.queue.revision += 1
            }
        }
        retainQueueFeedbackOrigins(live, authorization: session.source.authorization, action: session.action)
    }
    public func advanceFM(_ source: MusicRemoteQueueSource, direction: MusicDirection, reason: MusicAdvanceReason,
                          expectedRevision: Int, intent: MusicQueueAuthorization) async throws -> MusicAdvanceResult {
        try intent.check(); try source.authorization.check(); try check()
        try requirePlatform(source.context.platform)
        try musicRequire(try read().queue.source?.remoteSession == source.id.uuidString.lowercased(), .targetChanged)
        return try await advance(direction, reason: reason, expectedRevision: expectedRevision)
    }
}
