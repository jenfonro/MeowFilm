import XCTest
import MeowMusicFeature
import MeowMusicTestSupport
@testable import MeowMusicNativeService

private actor AccessTransitionAudioCache: MusicAudioFileCaching {
    let gate: NativeMusicGate
    init(_ gate: NativeMusicGate) { self.gate = gate }
    func lookup(scope: String, songID: String, authorization: MusicQueueAuthorization,
                accepts: @escaping @Sendable (MusicAudioFileCacheIdentity) -> Bool) -> MusicAudioFileCacheHit? { nil }
    func preparePlayback(_ request: MusicAudioFilePlaybackRequest) async throws -> any MusicAudioFilePlayback { MusicTestByteSource() }
    func storeAfterPlayback(_ request: MusicAudioFileCacheRequest) -> MusicAudioFileCacheVersion? { nil }
    func touch(_ identity: MusicAudioFileCacheIdentity, version: MusicAudioFileCacheVersion, confirmedAt: Date, authorization: MusicQueueAuthorization) {}
    func remove(_ identity: MusicAudioFileCacheIdentity, version: MusicAudioFileCacheVersion) {}
    func cancelPending() async { await gate.wait() }
    func clearOfficial() { clear() }
    func clear() {}
    func close() {}
}

final class MusicNativeAccessTransitionTests: XCTestCase {
    func testNewScopeCannotReadOrWriteBeforeOldCachesFinishRetiring() async throws {
        let store = NativeMusicMemoryDocument(), gate = NativeMusicGate(), platform = NativeMusicPlatformFixture()
        let service = NativeMusicService(document: store, platformClient: try platform.makeClient(),
            audioFileCache: AccessTransitionAudioCache(gate))
        let enabling = Task { try await service.setAccess(available: true, generation: 1) }
        await gate.started()
        do { _ = try await service.state(); XCTFail("Partially retired scope admitted a read") }
        catch { XCTAssertEqual(error as? MusicServiceError, .accessUnavailable) }
        do { _ = try await service.createPlaylist(name: "Synthetic", refs: []); XCTFail("Early write") }
        catch { XCTAssertEqual(error as? MusicServiceError, .accessUnavailable) }
        do { _ = try await service.publicPlaylists(platform: .netease, query: "Synthetic", cursor: nil); XCTFail("Early HTTP") }
        catch { XCTAssertEqual(error as? MusicServiceError, .accessUnavailable) }
        XCTAssertEqual(store.snapshot().1, 0); XCTAssertEqual(store.snapshot().2, 0)
        let requests = await platform.calls(); XCTAssertTrue(requests.isEmpty)
        await gate.release(); try await enabling.value
        _ = try await service.state()
        _ = try await service.createPlaylist(name: "Synthetic", refs: [])
        XCTAssertEqual(store.snapshot().2, 1)
        await service.close()
    }
}
