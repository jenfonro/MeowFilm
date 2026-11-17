import XCTest
import Foundation
import MeowMusicFeature
import MeowMusicPlatform
import MeowMusicTestSupport
@testable import MeowMusicNativeService

final class NativeMusicQQDirectTests: XCTestCase {
    func testAnonymousCanonicalQQSearchAndLyricsDoNotNeedNativeAccountAuthority() async throws {
        let peer = NativeMusicResultFixture([
            try MusicPlatformFixture.page([NativeMusicFixture.track()], platform: "tx"),
            .object(["text": .string("[00:01.000]QQ")])
        ])
        let store = NativeMusicTestLibrary(), service = try await NativeMusicFixture.service(store, platform: peer)
        let page = try await service.search(platform: "tx", query: "synthetic", page: 1, limit: 30)
        let track = try XCTUnwrap(page.items.first)
        XCTAssertEqual(track.sources.first?.nativeId, "SyntheticMID"); XCTAssertNotNil(UUID(uuidString: track.songId))
        let document = try MusicDocument(store.snapshot().0, runID: "inspect")
        let info = try XCTUnwrap((try document.fixtureSong(track.songId))["sources"].array.first)["musicInfo"]
        XCTAssertEqual(info["qqSongId"].string, "9007199254740993")
        let state = try await service.changeQueue(.replace([track.ref], index: 0), expectedRevision: 0)
        let lyrics = try await service.lyrics(occurrenceId: XCTUnwrap(state.currentId), sourceKey: track.sources[0].sourceKey)
        XCTAssertEqual(lyrics.lines.map(\.text), ["QQ"])
        let calls = await peer.calls(); XCTAssertEqual(calls.map(\.operation), ["search", "lyrics"])
        await service.close()
    }
}
