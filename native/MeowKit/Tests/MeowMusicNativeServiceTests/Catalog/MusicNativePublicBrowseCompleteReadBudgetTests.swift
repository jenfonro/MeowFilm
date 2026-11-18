import Foundation
import XCTest
import MeowMusicFeature
import MeowMusicTestSupport
@testable import MeowMusicNativeService

final class MusicNativePublicBrowseCompleteReadBudgetTests: XCTestCase {
    func testWYCompleteReadAdoptsAllTracksBeyondFormerByteBudget() async throws {
        try await checkCompleteRead(platform: "wy")
    }
    func testKGCompleteReadAdoptsAllTracksBeyondFormerByteBudget() async throws {
        try await checkCompleteRead(platform: "kg")
    }
    private func checkCompleteRead(platform: String) async throws {
        let track = MusicNativePublicBrowseFixture.track("budget", platform: platform,
            artwork: "https://artwork.invalid/" + String(repeating: "a", count: 3500) + ".jpg")
        let count = (6 * 1024 * 1024) / track.data.count + 1
        XCTAssertGreaterThan(count * track.data.count, (6 * 1024 * 1024))
        let response = try MusicPlatformFixture.page(Array(repeating: track, count: count), platform: platform)
        guard case .object(var fields) = response else { return XCTFail("fixture page") }
        fields["id"] = .string("budget"); fields["name"] = .string("Budget")
        let peer = NativeMusicResultFixture([.object(fields)]), store = NativeMusicTestLibrary()
        let service = try await NativeMusicFixture.service(store, platform: peer)
        let collection = MusicPublicBrowseCollection(platform: platform == "wy" ? .netease : .kugou, nativeID: "budget", title: "Budget")
        let tracks = try await service.publicQueueTracks(.init(collection: collection))
        XCTAssertEqual(tracks.count, count)
        XCTAssertNotNil(store.snapshot().0)
        XCTAssertGreaterThan(store.snapshot().2, 0)
        let calls = await peer.calls(); XCTAssertEqual(calls.map(\.operation), ["playlist.complete"])
        await service.close()
    }
}
