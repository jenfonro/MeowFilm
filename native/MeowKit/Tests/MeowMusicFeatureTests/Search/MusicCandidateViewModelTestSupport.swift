#if os(iOS) || os(macOS)
import Foundation
import MeowContracts
import MeowMusicFeature

/// Finite test-controlled, deliberately non-cooperative dependency.
actor MusicCandidateViewModelGate {
    let began: @Sendable () -> Void
    private var released = false
    private var waiting: [CheckedContinuation<Void, Never>] = []
    init(_ began: @escaping @Sendable () -> Void) { self.began = began }
    func wait() async {
        began()
        if !released { await withCheckedContinuation { waiting.append($0) } }
    }
    func release() { released = true; waiting.forEach { $0.resume() }; waiting = [] }
}

actor MusicCandidateViewModelService: MusicServing, MusicCandidateDiscoveryServing {
    struct Counts: Sendable {
        var begins = 0, commits = 0, automatic = 0, options = 0, manual = 0
        var retained: [String] = [], cancelled: [String] = [], facts: [MusicPlaybackFact] = []
    }
    static func track(_ id: String) -> MusicRemoteTrack {
        .init(ref: .init(kind: .queue, id: id), songId: "recording", title: "Synthetic",
            artist: "Fixture", duration: 180,
            sources: [.init(sourceKey: "source-a", platform: "tx", nativeId: "A")])
    }
    static func option(_ key: String = "source-a") -> MusicCandidateDiscoveryOption {
        MusicCandidateDiscoveryFixture.option(source: key, qualities: ["128k"])
    }
    var entries = ["entry-1", "entry-2"].map {
        MusicQueueEntry(id: $0, track: MusicCandidateViewModelService.track($0))
    }
    var currentID = "entry-1", revision = 1, settingsRevision = 0
    var discoveryRequired = false
    var automaticFailure: MusicServiceError?, manualFailure: MusicServiceError?
    var resolveGate: MusicCandidateViewModelGate?, refreshGate: MusicCandidateViewModelGate?
    var ready: String?
    var owned: [UUID: String] = [:]
    var closed: Set<UUID> = []
    var counts = Counts()
    func configure(discovery: Bool = false, automaticFailure: MusicServiceError? = nil,
                   manualFailure: MusicServiceError? = nil, resolve: MusicCandidateViewModelGate? = nil,
                   refresh: MusicCandidateViewModelGate? = nil) {
        discoveryRequired = discovery; self.automaticFailure = automaticFailure; self.manualFailure = manualFailure
        resolveGate = resolve; refreshGate = refresh
    }
    func snapshot() -> MusicPlaybackState {
        .init(revision: revision, settingsRevision: settingsRevision, currentId: currentID,
            total: entries.count, position: 23, canNext: true, canPrevious: true)
    }
    func state() async -> MusicPlaybackState {
        let value = snapshot()
        if ready != nil, let gate = refreshGate { refreshGate = nil; await gate.wait() }
        return value
    }
    func queue() -> MusicQueueSnapshot { .init(revision: revision, currentId: currentID, entries: entries) }
    func beginCandidateDiscovery(occurrenceID: String, expectedQueueRevision: Int) throws -> any MusicCandidateDiscoverySession {
        guard currentID == occurrenceID, revision == expectedQueueRevision else { throw MusicServiceError.staleRevision }
        counts.begins += 1
        return Session(owner: self, id: UUID(), occurrenceID: occurrenceID)
    }
    func discoverySnapshot(_ id: UUID, discover: Bool) throws -> MusicCandidateDiscoverySnapshot {
        guard !closed.contains(id) else { throw MusicServiceError.cancelled }
        var track = entries.first { $0.id == currentID }!.track
        if discover, discoveryRequired {
            track = .init(ref: track.ref, songId: track.songId, title: track.title,
                artist: track.artist, album: track.album, duration: track.duration,
                sources: track.sources + [.init(sourceKey: "source-b", platform: "tx", nativeId: "B")])
        }
        return .init(track: track, state: snapshot(),
            options: discoveryRequired ? (discover ? [Self.option("source-b")] : []) : [Self.option()])
    }
    func commit(_ id: UUID) throws {
        guard !closed.contains(id) else { throw MusicServiceError.cancelled }
        guard discoveryRequired, let index = entries.firstIndex(where: { $0.id == currentID }) else { return }
        let t = entries[index].track
        entries[index] = .init(id: currentID, track: .init(ref: t.ref, songId: t.songId,
            title: t.title, artist: t.artist, album: t.album, duration: t.duration,
            sources: t.sources + [.init(sourceKey: "source-b", platform: "tx", nativeId: "B")]))
        counts.commits += 1; revision += 1
    }
    func resolveCandidate(_ id: UUID, occurrence: String, target: MusicCandidateDiscoveryTarget,
                          attempt: String) async throws -> MusicPlaybackCandidate {
        counts.automatic += 1; owned[id] = attempt
        if let failure = automaticFailure { throw failure }
        ready = attempt
        if let gate = resolveGate { resolveGate = nil; await gate.wait() }
        return value(attempt, occurrence: occurrence, quality: target.quality)
    }
    func value(_ attempt: String, occurrence: String, quality: String) -> MusicPlaybackCandidate {
        .init(attemptId: attempt, occurrenceId: occurrence, resourceId: "music:" + attempt,
            url: URL(string: "https://media.invalid/candidate.mp3")!, title: "Synthetic",
            startPosition: 23, requestedQuality: quality)
    }
    func closeSession(_ id: UUID, retaining: String?) {
        closed.insert(id)
        if let retaining { counts.retained.append(retaining) }
        if let old = owned.removeValue(forKey: id), old != retaining { cancelExact(old) }
    }
    func cancelExact(_ id: String) { counts.cancelled.append(id); if ready == id { ready = nil } }
    func cancel(attemptId: String?) throws {
        try Task.checkCancellation()
        guard let attemptId else { throw MusicServiceError.invalidRequest }
        cancelExact(attemptId)
    }
    func playbackOptions(for ref: MusicReference) -> [MusicPlaybackOption] {
        counts.options += 1; return [Self.option().playback]
    }
    func resolve(attemptId: String, occurrenceId: String, selection: MusicPlaybackSelection) throws -> MusicPlaybackCandidate {
        counts.manual += 1
        if let manualFailure { throw manualFailure }
        ready = attemptId
        return value(attemptId, occurrence: occurrenceId, quality: selection.quality)
    }
    func changeQueue(_ change: MusicQueueChange, expectedRevision: Int) throws -> MusicPlaybackState {
        guard revision == expectedRevision else { throw MusicServiceError.staleRevision }
        switch change {
        case .select(let id): currentID = id
        case .replace:
            currentID = "entry-3"; entries = [.init(id: currentID, track: Self.track(currentID))]
        default: throw MusicServiceError.unsupported
        }
        revision += 1; return snapshot()
    }
    func advance(_ direction: MusicDirection, reason: MusicAdvanceReason, expectedRevision: Int) throws -> MusicAdvanceResult {
        .init(state: try changeQueue(.select("entry-2"), expectedRevision: expectedRevision), selectedId: currentID)
    }
    func updateSettings(_ change: MusicSettingsChange) throws -> MusicPlaybackState {
        guard settingsRevision == change.ifRevision else { throw MusicServiceError.staleRevision }
        settingsRevision += 1; return snapshot()
    }
    func mutateQueueDuringRefresh() { revision += 1; entries.reverse() }
    func metrics() -> Counts { counts }
    func activeAttempt() -> String? { ready }
    func record(_ fact: MusicPlaybackFact) throws -> Bool {
        guard ready == fact.attemptId else { throw MusicServiceError.staleResult }
        counts.facts.append(fact); return true
    }
    func lyrics(occurrenceId: String, sourceKey: String) -> MusicLyricsSnapshot {
        .init(occurrenceId: occurrenceId, state: .none, lines: [])
    }
    func platforms() -> [MusicPlatform] { [] }
    func collections() -> [MusicCollection] { [] }
    func search(platform: String, query: String, page: Int, limit: Int) throws -> MusicSearchPage { throw MusicServiceError.unsupported }
    func tracks(in collection: MusicCollection) -> [MusicRemoteTrack] { [] }
    func createPlaylist(name: String, refs: [MusicReference]) throws -> MusicCollection { throw MusicServiceError.unsupported }
    func editPlaylist(_ collection: MusicCollection, name: String?, refs: [MusicReference]?, order: [String]?) throws -> MusicCollection { throw MusicServiceError.unsupported }
    func removePlaylist(_ collection: MusicCollection) throws { throw MusicServiceError.unsupported }
    func playedSongs() -> [MusicPlayedSong] { [] }
    func playedPlaylists() -> [MusicPlayedPlaylist] { [] }
    func restoreHistory(id: String, expectedRevision: Int) throws -> MusicPlaybackState { throw MusicServiceError.unsupported }
    func prepareImport(data: Data, sourceId: String, ownerId: String) throws -> MusicImportPreview { throw MusicServiceError.unsupported }
    func commitImport(_ preview: MusicImportPreview) throws -> MusicImportReport { throw MusicServiceError.unsupported }
    func releaseImport(_ preview: MusicImportPreview) {}
    func rollbackImport(batchId: String) throws { throw MusicServiceError.unsupported }

    private struct Session: MusicCandidateDiscoverySession {
        let owner: MusicCandidateViewModelService, id: UUID, occurrenceID: String
        func snapshot(discover: Bool, force: Bool) async throws -> MusicCandidateDiscoverySnapshot {
            try await owner.discoverySnapshot(id, discover: discover)
        }
        func commitDiscovery() async throws { try await owner.commit(id) }
        func resolve(_ target: MusicCandidateDiscoveryTarget, attemptID: String) async throws -> MusicPlaybackCandidate {
            try await owner.resolveCandidate(id, occurrence: occurrenceID, target: target, attempt: attemptID)
        }
        func cancel(attemptID: String) async { await owner.cancelExact(attemptID) }
        func close(retainingAttemptID: String?) async { await owner.closeSession(id, retaining: retainingAttemptID) }
    }
}

@MainActor final class MusicCandidateViewModelPlayer: MusicPlaybackDriving {
    var resources: [PlaybackResource] = []
    var stopped: [MusicPlayerAttempt] = []
    var onPlay: ((MusicPlayerAttempt) -> Void)?
    func play(_ resource: PlaybackResource) -> MusicPlayerAttempt {
        let attempt = MusicPlayerAttempt(id: UUID(), resourceID: resource.id)
        resources.append(resource); onPlay?(attempt); return attempt
    }
    func pause(_ attempt: MusicPlayerAttempt) {}
    func resume(_ attempt: MusicPlayerAttempt) {}
    func seek(_ position: Double, attempt: MusicPlayerAttempt) -> Bool { false }
    func stop(_ attempt: MusicPlayerAttempt) { stopped.append(attempt) }
}
#endif
