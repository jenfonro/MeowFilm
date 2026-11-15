import Foundation
import XCTest
import MeowMusicFeature
import MeowMusicTestSupport
@testable import MeowMusicNativeService

final class MusicNativePublicBrowseCompleteReadBudgetTests: XCTestCase {
    func testWYCompleteReadFailsAtRetainedNativeTrackByteBudgetWithoutPartialAdoption() async throws {
        try await checkRetainedBudget(platform: "wy")
    }
    func testKGCompleteReadFailsAtRetainedNativeTrackByteBudgetWithoutPartialAdoption() async throws {
        try await checkRetainedBudget(platform: "kg")
    }
    private func checkRetainedBudget(platform: String) async throws {
        let track = MusicNativePublicBrowseFixture.track("budget", platform: platform,
            artwork: "https://artwork.invalid/" + String(repeating: "a", count: 3500) + ".jpg")
        let count = MusicRemoteQueueSource.maximumBytes / track.data.count + 1
        XCTAssertGreaterThan(count * track.data.count, MusicRemoteQueueSource.maximumBytes)
        let response = try MusicPlatformFixture.page(Array(repeating: track, count: count), platform: platform)
        guard case .object(var fields) = response else { return XCTFail("fixture page") }
        fields["id"] = .string("budget"); fields["name"] = .string("Budget")
        let peer = NativeMusicResultFixture([.object(fields)]), store = NativeMusicMemoryDocument()
        let service = try await NativeMusicFixture.service(store, platform: peer)
        let collection = MusicPublicBrowseCollection(platform: platform == "wy" ? .netease : .kugou, nativeID: "budget", title: "Budget")
        await assertMusicFailure(.limit) { _ = try await service.publicQueueTracks(.init(collection: collection)) }
        XCTAssertNil(store.snapshot().0); XCTAssertEqual(store.snapshot().2, 0)
        let calls = await peer.calls(); XCTAssertEqual(calls.map(\.operation), ["playlist.complete"])
        await service.close()
    }
}
