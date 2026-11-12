import Foundation
import MeowMusicFeature
@testable import MeowMusicNativeService

// Synthetic public locators and media only. This fixture never sends a request,
// creates a credential, or modifies the older shared fixture assertions.
final class MusicRecoveryTestClock: @unchecked Sendable {
    private let lock = NSLock()
    private var value = NativeMusicFixture.date
    func now() -> Date { lock.lock(); defer { lock.unlock() }; return value }
    func advance(_ seconds: TimeInterval) {
        lock.lock(); value = value.addingTimeInterval(seconds); lock.unlock()
    }
}

actor MusicRecoveryTestAccounts: MusicNativeAccountProviding, MusicNativePlaybackRecoveryAuthorizing, MusicNativeCacheScopeProviding {
    struct Request: Sendable {
        let platform: MusicAccountPlatform
        let nativeID: String
        let selection: MusicPlaybackSelection
        let rejectedURL: URL
    }
    static let originalURL = URL(string: "https://media.invalid/synthetic.flac?revision=original")!
    static let renewedURL = URL(string: "https://media.invalid/synthetic.flac?revision=renewed")!
    let platform: MusicAccountPlatform
    let nativeID: String
    let clock: MusicRecoveryTestClock
    private var generation = 1, installation = "official:recovery-synthetic"
    private var qualities = ["flac", "128k"]
    private var grantValid = false
    private var optionsCount = 0, resolves = 0
    private var scopeReads = 0
    private var refreshes: [Request] = []
    private var refreshGate: NativeMusicGate?
    private var resolveGate: NativeMusicGate?
    private var scope = MusicNativeCacheScope(scope: String(repeating: "c", count: 64))
    private var playbackAuthorization: MusicQueueAuthorization?
    private var optionsGates: [Int: NativeMusicGate] = [:]
    private var optionsError: MusicServiceError?, refreshError: MusicServiceError?
    private var replacementURL = MusicRecoveryTestAccounts.renewedURL
    private var qualityURLs: [String: URL] = [:]
    private var rotationOnReturn = false, withdrawOnReturn = false
    private var expiration: Date?
    init(platform: MusicAccountPlatform, nativeID: String, clock: MusicRecoveryTestClock) {
        self.platform = platform; self.nativeID = nativeID; self.clock = clock
    }
    func holdRefresh(_ gate: NativeMusicGate) { refreshGate = gate }
    func holdResolve(_ gate: NativeMusicGate) { resolveGate = gate }
    func enablePlaybackAuthorization() -> MusicQueueAuthorization {
        let authorization = MusicQueueAuthorization()
        playbackAuthorization = authorization
        return authorization
    }
    func nativeCacheScope() throws -> MusicNativeCacheScope { scopeReads += 1; try scope.authorization.check(); return scope }
    func scopeReadCount() -> Int { scopeReads }
    func checkNativeCacheScope(_ value: MusicNativeCacheScope) throws {
        try value.authorization.check()
        guard value == scope else { throw MusicServiceError.staleAccess }
    }
    func holdOptions(call: Int, gate: NativeMusicGate) { optionsGates[call] = gate }
    func expireGrant() { grantValid = false }
    func rotate() {
        generation += 1
        scope.authorization.revoke()
        scope = .init(scope: String(repeating: "d", count: 64))
        playbackAuthorization?.revoke()
    }
    func replaceInstallation() { installation = "official:different-synthetic" }
    func allow(_ qualities: [String]) { self.qualities = qualities }
    func failOptions(_ error: MusicServiceError) { optionsError = error }
    func failRefresh(_ error: MusicServiceError) { refreshError = error }
    func setResolveURL(_ url: URL, quality: String) { qualityURLs[quality] = url }
    func setResult(_ url: URL, expiresAt: Date? = nil) { replacementURL = url; expiration = expiresAt }
    func changeAfterRefresh(account: Bool) { rotationOnReturn = account; withdrawOnReturn = !account }
    func counts() -> (options: Int, resolves: Int, refreshes: Int) { (optionsCount, resolves, refreshes.count) }
    func requests() -> [Request] { refreshes }
    private func check(_ platform: MusicAccountPlatform, _ nativeID: String) throws {
        guard platform == self.platform, nativeID == self.nativeID else { throw MusicServiceError.targetChanged }
    }
    func nativeOptions(platform: MusicAccountPlatform, nativeID: String, sourceKey: String) async throws -> [MusicPlaybackOption] {
        try check(platform, nativeID)
        optionsCount += 1
        if let gate = optionsGates.removeValue(forKey: optionsCount) { await gate.wait() }
        if let optionsError { throw optionsError }
        grantValid = true
        return [.init(installationId: installation, generation: generation, siteId: platform.rawValue,
            sourceKey: sourceKey, qualities: qualities, title: "Synthetic", version: "1",
            contentHash: String(repeating: "a", count: 64))]
    }
    func nativeRecoveryOptions(platform: MusicAccountPlatform, nativeID: String,
                               selection: MusicPlaybackSelection) async throws -> [MusicPlaybackOption] {
        try check(platform, nativeID)
        guard selection.siteId == platform.rawValue, selection.installationId == installation,
              selection.generation == generation else { throw MusicServiceError.targetChanged }
        let values = try await nativeOptions(platform: platform, nativeID: nativeID, sourceKey: selection.sourceKey)
        guard selection.installationId == installation, selection.generation == generation else {
            throw MusicServiceError.targetChanged
        }
        return values
    }
    func nativeResolve(platform: MusicAccountPlatform, nativeID: String,
                       selection: MusicPlaybackSelection) async throws -> MusicNativeAudio {
        try check(platform, nativeID)
        guard grantValid, selection.installationId == installation, selection.generation == generation,
              selection.siteId == platform.rawValue, qualities.contains(selection.quality) else {
            throw MusicServiceError.staleResult
        }
        resolves += 1
        if let gate = resolveGate { resolveGate = nil; await gate.wait() }
        return .init(url: qualityURLs[selection.quality] ?? Self.originalURL, expiresAt: clock.now().addingTimeInterval(60),
                     playbackAuthorization: playbackAuthorization)
    }
    func nativeRefreshAudio(platform: MusicAccountPlatform, nativeID: String,
                            selection: MusicPlaybackSelection, rejectedURL: URL) async throws -> MusicNativeAudio {
        try check(platform, nativeID)
        refreshes.append(.init(platform: platform, nativeID: nativeID, selection: selection, rejectedURL: rejectedURL))
        guard grantValid, selection.installationId == installation, selection.generation == generation,
              selection.siteId == platform.rawValue, qualities.contains(selection.quality) else {
            throw MusicServiceError.staleResult
        }
        let own = generation, value = replacementURL
        if let gate = refreshGate { refreshGate = nil; await gate.wait() }
        guard own == generation else { throw MusicServiceError.staleResult }
        if let refreshError { throw refreshError }
        // Deliberately stale peer output to exercise the business owner's final
        // grant check too, not only the real adapter's independent checks.
        if rotationOnReturn { generation += 1 }
        if withdrawOnReturn { qualities = ["128k"] }
        return .init(url: value, expiresAt: expiration ?? clock.now().addingTimeInterval(60),
                     playbackAuthorization: playbackAuthorization)
    }
    func nativeRecommendations(platform: MusicAccountPlatform, kind: String, cursor: String?) throws -> MusicNativeTrackPage {
        throw MusicServiceError.unsupported
    }
    func nativeCollections(platform: MusicAccountPlatform, cursor: String?) throws -> MusicAccountCollectionPage {
        throw MusicServiceError.unsupported
    }
    func nativeTracks(platform: MusicAccountPlatform, collectionID: String, cursor: String?) throws -> MusicNativeTrackPage {
        throw MusicServiceError.unsupported
    }
}

struct MusicRecoveryTestFixture {
    let store: NativeMusicMemoryDocument
    let accounts: MusicRecoveryTestAccounts
    let clock: MusicRecoveryTestClock
    let service: NativeMusicService
    let selection: MusicPlaybackSelection
    let original: MusicPlaybackCandidate

    static func make(platform: MusicAccountPlatform = .qq, discovery: Bool = false, cached: Bool = false,
                     authorized: Bool = false) async throws -> Self {
        let nativeID = platform == .kugou ? String(repeating: "A", count: 32) : "101"
        let song = try MusicModel.normalize(MusicNativeCandidateDiscoveryFixture.track(platform.rawValue, id: nativeID))
        let store = try MusicNativeCandidateDiscoveryFixture.store(song: song)
        let clock = MusicRecoveryTestClock()
        let accounts = MusicRecoveryTestAccounts(platform: platform, nativeID: nativeID, clock: clock)
        if authorized { _ = await accounts.enablePlaybackAuthorization() }
        let d = try MusicDocument(store.read(), runID: "cache-fixture")
        let source = try MusicModel.sources(song)[0]
        let file = MusicRecoveryTestFileCache(hit: .init(identity: .init(scope: String(repeating: "c", count: 64),
            songID: song["songId"].string, sourceKey: source.sourceKey, platform: platform.rawValue,
            nativeID: nativeID, requestedQuality: "flac",
            playbackSignature: NativeMusicService.audioCacheSignature(song: song, playback: d.playback), provider: "official:recovery-synthetic"),
            fileURL: URL(fileURLWithPath: "/synthetic-cache/recording.flac"), duration: 180,
            measuredQuality: "flac", expiresAt: clock.now().addingTimeInterval(3600)))
        let service = NativeMusicService(document: store, accounts: accounts, qq: NativeMusicQQFixture(),
            audioFileCache: cached ? file : nil,
            transport: NativeMusicNoNetwork(), clock: { clock.now() })
        try await service.setAccess(available: true, generation: 1)
        let selection: MusicPlaybackSelection, original: MusicPlaybackCandidate
        if discovery {
            let session = try await service.beginCandidateDiscovery(occurrenceID: "entry-1", expectedQueueRevision: 1)
            let snapshot = try await session.snapshot(discover: false, force: false)
            guard let option = snapshot.options.first else { throw MusicServiceError.sourceUnavailable }
            let target = option.target(quality: "flac")
            selection = target.selection
            original = try await session.resolve(target, attemptID: UUID().uuidString.lowercased())
            await session.close(retainingAttemptID: original.attemptId)
        } else {
            let options = (try await service.sourceAvailability(for: .init(kind: .queue, id: "entry-1"))).options
            guard let option = options.first else { throw MusicServiceError.sourceUnavailable }
            selection = option.selection(quality: "flac")
            if cached {
                guard let value = try await service.cachedPlayback(attemptID: UUID().uuidString.lowercased(),
                    occurrenceID: "entry-1", selection: selection) else { throw MusicServiceError.notFound }
                original = value.candidate
            } else {
                original = try await service.resolve(attemptId: UUID().uuidString.lowercased(),
                    occurrenceId: "entry-1", selection: selection)
            }
        }
        return .init(store: store, accounts: accounts, clock: clock, service: service,
                     selection: selection, original: original)
    }
    func recover(attemptID: String = UUID().uuidString.lowercased(), position: Double = 37.5) async throws -> MusicPlaybackRecovery {
        try await service.recoverPlayback(previousAttemptID: original.attemptId, attemptID: attemptID,
            rejectedURL: original.url, position: position)
    }
    func fact(_ candidate: MusicPlaybackCandidate, kind: MusicPlaybackFact.Kind, position: Double = 37.5) -> MusicPlaybackFact {
        .init(attemptId: candidate.attemptId, occurrenceId: candidate.occurrenceId,
            resourceId: candidate.resourceId, kind: kind, position: position, duration: 180)
    }
    func historyDocument() throws -> MJ {
        let bytes = try store.read()
        return try MusicDocument(bytes, runID: "inspect").history
    }
}

actor MusicRecoveryTestFileCache: MusicAudioFileCaching {
    var hit: MusicAudioFileCacheHit?
    private var removalFails = false
    func failRemoval() { removalFails = true }
    init(hit: MusicAudioFileCacheHit) { self.hit = hit }
    func lookup(scope: String, songID: String, authorization: MusicQueueAuthorization,
                accepts: @escaping @Sendable (MusicAudioFileCacheIdentity) -> Bool) -> MusicAudioFileCacheHit? {
        guard authorization.isActive, let hit, hit.identity.scope == scope,
              hit.identity.songID == songID, accepts(hit.identity) else { return nil }
        return hit
    }
    func preparePlayback(_ request: MusicAudioFilePlaybackRequest) async throws -> any MusicAudioFilePlayback { MusicTestByteSource() }
    func storeAfterPlayback(_ request: MusicAudioFileCacheRequest) -> MusicAudioFileCacheVersion? { nil }
    func touch(_ identity: MusicAudioFileCacheIdentity, version: MusicAudioFileCacheVersion, confirmedAt: Date, authorization: MusicQueueAuthorization) {}
    func remove(_ identity: MusicAudioFileCacheIdentity, version: MusicAudioFileCacheVersion) {
        if !removalFails, hit?.identity == identity, hit?.version == version { hit = nil }
    }
    func cancelPending() {}
    func clear() { hit = nil }
    func clearOfficial() { if hit?.identity.isOfficial == true { hit = nil } }
    func close() {}
}
