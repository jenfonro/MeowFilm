import Foundation
import MeowMusicFeature

struct MusicNativeFMTrack {
    let song: MJ
    let recommendation: MJ?
}

final class MusicNativeFMSession {
    let source: MusicRemoteQueueSource
    let action: MusicHomeAction
    var continueEnabled: Bool
    var attempted = Set<String>()
    var pending: Task<Void, Never>?
    var buffered: [MusicNativeFMTrack] = []
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
        let saved = try read().playback["fmSelections"][scope]
        guard saved != .null else { return .fm(mode: .default, submode: "", entry: nil) }
        let action = try JSONDecoder().decode(MusicHomeAction.self, from: saved.bytes)
        try musicRequire(action.isPersonalFM, .corruptDocument)
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
                try similarQueueTransaction(["playback"]) { selectContinuationMode(&$0) }
            }
        }
        session.continueEnabled = enabled
        emitSimilar()
        if enabled { try fmBoundary() }
    }
    func fmPreferenceScope(_ binding: MusicNativeHomeBinding) async throws -> String {
        guard let owner = accounts as? any MusicNativeFMProviding else { throw MusicServiceError.unsupported }
        let scope = try await owner.nativeFMPreferenceScope(binding)
        try musicRequire(!scope.isEmpty && scope.utf8.count <= 512, .invalidResponse)
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
        let event = d.queue["entries"].array.last?["id"].string ?? ""
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
            let batch = try await work(label: "fmBatch", draining: true) { try await source.nextFMBatch(intent: source.authorization) }
            try musicRequire(batch.allSatisfy { $0.platform == source.context.platform }, .invalidResponse)
            let songs = try batch.map(MusicModel.normalize)
            let recommendations = try batch.map(MusicNativeSimilarData.recommendation)
            let tracks = songs.indices.map { MusicNativeFMTrack(song: songs[$0], recommendation: recommendations[$0]) }
            try await source.validate()
            try check(own); try source.authorization.check()
            guard fmPlayback === session, !Task.isCancelled else { return }
            try await retainSongMetadata(songs)
            try check(own); try source.authorization.check()
            guard fmPlayback === session, !Task.isCancelled else { return }
            session.exhausted = songs.isEmpty
            if session.continueEnabled { try appendFMBatch(session, tracks: tracks) }
            else { session.buffered = tracks }
        } catch {
            guard fmPlayback === session, epoch == own, source.authorization.isActive else { return }
            let failure = MusicServiceError.sanitized(error)
            if failure != .cancelled { session.error = failure; session.errorID = UUID() }
        }
    }
    func appendFMBatch(_ session: MusicNativeFMSession, tracks: [MusicNativeFMTrack]) throws {
        guard !tracks.isEmpty else { return }
        let context = try remoteContext(session.source)
        var live: [String: MJ] = [:]
        try session.source.authorization.perform {
            try similarQueueTransaction(["queue"]) { d in
                try musicRequire(d.queue["source"]["remoteSession"].string == session.source.id.uuidString.lowercased(), .targetChanged)
                let rows = try makeEntries(tracks.map(\.song), queue: &d.queue, context: context)
                for i in rows.indices {
                    if let value = tracks[i].recommendation { live[rows[i]["id"].string] = value }
                }
                d.queue["entries"] = .array(d.queue["entries"].array + rows)
                d.queue["revision"] = .n(d.queue["revision"].int + 1)
            }
        }
        retainQueueFeedbackOrigins(live, authorization: session.source.authorization, action: session.action)
    }
    public func advanceFM(_ source: MusicRemoteQueueSource, direction: MusicDirection, reason: MusicAdvanceReason,
                          expectedRevision: Int, intent: MusicQueueAuthorization) async throws -> MusicAdvanceResult {
        try intent.check(); try source.authorization.check(); try check()
        try musicRequire(try read().queue["source"]["remoteSession"].string == source.id.uuidString.lowercased(), .targetChanged)
        return try await advance(direction, reason: reason, expectedRevision: expectedRevision)
    }
}
