import Foundation
import MeowMusicFeature

enum MusicCandidateDiscoveryFixture {
    static func option(_ installation: String = "script-a", platform: String = "tx", source: String = "source-a",
                       qualities: [String] = ["flac", "128k"], version: String = "1", revision: String = "r1",
                       generation: Int = 1) -> MusicCandidateDiscoveryOption {
        .init(playback: .init(installationId: installation, generation: generation, siteId: platform, sourceKey: source,
            qualities: qualities, title: "Synthetic", version: version, contentHash: String(repeating: "a", count: 64),
            revisionHash: installation.hasPrefix("official:") ? nil : revision),
            revisionHash: installation.hasPrefix("official:") ? nil : revision)
    }
    static func snapshot(_ options: [MusicCandidateDiscoveryOption]) -> MusicCandidateDiscoverySnapshot {
        var sources: [MusicNativeSource] = []
        for option in options where !sources.contains(where: { $0.sourceKey == option.playback.sourceKey }) {
            sources.append(.init(sourceKey: option.playback.sourceKey, platform: option.playback.siteId, nativeId: option.playback.sourceKey))
        }
        return .init(track: .init(ref: .init(kind: .queue, id: "entry-1"), songId: "song:synthetic", title: "Synthetic",
            artist: "Fixture", duration: 180, sources: sources),
            state: .init(revision: 1, qualityEnabled: ["master", "spatial", "enhanced", "hires", "lossless", "high", "standard"],
                qualityOrder: ["master", "spatial", "enhanced", "hires", "lossless", "high", "standard"],
                spatialEnabled: ["sky", "dolby"], spatialOrder: ["sky", "dolby"], currentId: "entry-1"), options: options)
    }
}

actor MusicCandidateDiscoveryTestGate {
    private var open = false, entered = false
    private var waiters: [CheckedContinuation<Void, Never>] = []
    private var starters: [CheckedContinuation<Void, Never>] = []
    func wait() async {
        entered = true; starters.forEach { $0.resume() }; starters = []
        if !open { await withCheckedContinuation { waiters.append($0) } }
    }
    func started() async { if !entered { await withCheckedContinuation { starters.append($0) } } }
    func release() { open = true; waiters.forEach { $0.resume() }; waiters = [] }
}

actor MusicCandidateDiscoveryTestSession: MusicCandidateDiscoverySession {
    let known: MusicCandidateDiscoverySnapshot
    let discovered: MusicCandidateDiscoverySnapshot
    let discoveryGate: MusicCandidateDiscoveryTestGate?
    let resolutionGate: MusicCandidateDiscoveryTestGate?
    let failures: [MusicServiceError]
    let resultSelection: @Sendable (MusicPlaybackSelection) -> MusicPlaybackSelection
    private var targets: [MusicCandidateDiscoveryTarget] = []
    private var attempts: [String] = []
    private var snapshots = 0, discoveries = 0
    private var closed = false
    private var retained: String?
    private var cancellations: [String] = []
    init(known: MusicCandidateDiscoverySnapshot, discovered: MusicCandidateDiscoverySnapshot? = nil,
         failures: [MusicServiceError] = [], discoveryGate: MusicCandidateDiscoveryTestGate? = nil,
         resolutionGate: MusicCandidateDiscoveryTestGate? = nil,
         resultSelection: @escaping @Sendable (MusicPlaybackSelection) -> MusicPlaybackSelection = { $0 }) {
        self.known = known; self.discovered = discovered ?? known; self.failures = failures
        self.discoveryGate = discoveryGate; self.resolutionGate = resolutionGate
        self.resultSelection = resultSelection
    }
    func snapshot(discover: Bool, force: Bool) async throws -> MusicCandidateDiscoverySnapshot {
        snapshots += 1
        if discover { discoveries += 1 }
        if discover, let discoveryGate { await discoveryGate.wait() }
        if closed || Task.isCancelled { throw MusicServiceError.cancelled }
        return discover ? discovered : known
    }
    func resolve(_ target: MusicCandidateDiscoveryTarget, attemptID: String) async throws -> MusicPlaybackCandidate {
        targets.append(target)
        attempts.append(attemptID)
        if let resolutionGate { await resolutionGate.wait() }
        if closed || Task.isCancelled { throw MusicServiceError.cancelled }
        if targets.count <= failures.count { throw failures[targets.count - 1] }
        return .init(attemptId: attemptID, occurrenceId: "entry-1", resourceId: "music:" + attemptID,
            url: URL(string: "https://media.invalid/fixture.flac")!, title: "Synthetic", requestedQuality: target.quality,
            resolvedSelection: resultSelection(target.selection))
    }
    func cancel(attemptID: String) { cancellations.append(attemptID) }
    func close(retainingAttemptID: String?) async {
        closed = true; retained = retainingAttemptID
        await discoveryGate?.release()
        await resolutionGate?.release()
    }
    func facts() -> (targets: [MusicCandidateDiscoveryTarget], snapshots: Int, discoveries: Int,
                     retained: String?, cancellations: [String], attempts: [String]) {
        (targets, snapshots, discoveries, retained, cancellations, attempts)
    }
}

struct MusicCandidateDiscoveryTestProvider: MusicCandidateDiscoveryServing {
    let session: MusicCandidateDiscoveryTestSession
    func beginCandidateDiscovery(occurrenceID: String, expectedQueueRevision: Int) throws -> any MusicCandidateDiscoverySession {
        guard occurrenceID == "entry-1", expectedQueueRevision == 1 else { throw MusicServiceError.staleRevision }
        return session
    }
}
