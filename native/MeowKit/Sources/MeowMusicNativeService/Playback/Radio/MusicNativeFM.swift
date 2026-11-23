import Foundation
import MeowMusicFeature

final class MusicNativeFMSession {
    let source: MusicRemoteQueueSource
    let action: MusicHomeAction
    var continueEnabled: Bool
    var attempted = Set<String>()
    var pending: Task<Void, Never>?
    var buffered: [MusicQueueTrackReference] = []
    var exhausted = false
    var error: MusicServiceError?
    var errorID: UUID?
    init(source: MusicRemoteQueueSource, action: MusicHomeAction, continueEnabled: Bool) {
        self.source = source; self.action = action; self.continueEnabled = continueEnabled
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
        try check()
        guard let session = fmPlayback, session.source.authorization.isActive else { return nil }
        return .init(id: session.source.id, action: session.action, continueEnabled: session.continueEnabled,
                     loading: session.pending != nil, error: session.error, errorID: session.errorID)
    }
    public func setFMContinue(_ enabled: Bool, sessionID: UUID) async throws {
        try check()
        guard let session = fmPlayback, session.source.id == sessionID else { throw MusicServiceError.targetChanged }
        try session.source.authorization.check()
        if enabled {
            try session.source.authorization.perform {
                try similarQueueTransaction([.playback]) { selectContinuationMode(&$0) }
            }
        }
        session.continueEnabled = enabled
        emitSimilar()
        if enabled { try fmBoundary() }
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
        guard let session = fmPlayback else { return }
        fmPlayback = nil
        session.pending?.cancel()
        if let pending = session.pending { retainSimilarDrain(pending) }
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
                let songs = try tracks.map { try repositorySong($0.songID, d) }
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
