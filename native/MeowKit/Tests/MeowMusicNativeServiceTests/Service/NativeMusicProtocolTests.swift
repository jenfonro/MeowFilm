import XCTest
import MeowMusicFeature
import MeowMusicTestSupport
import MeowRuntime
@testable import MeowMusicNativeService

final class NativeMusicProtocolTests: XCTestCase {
    // Upstream EAPI/KG signatures and encrypted QRC/KRC vectors belong to the
    // common Node provider tests. These tests exercise the native result owner.
    func testCanonicalSearchKeepsLongIDAndPageWithoutScriptOrAccount() async throws {
        let peer = NativeMusicResultFixture([try MusicPlatformFixture.page([
            MusicNativePublicBrowseFixture.track(MusicNativePublicBrowseFixture.id)
        ], platform: "wy", total: 31, page: 2)])
        let service = try await NativeMusicFixture.service(platform: peer)
        let page = try await service.search(platform: "wy", query: "Synthetic", page: 2, limit: 30)
        XCTAssertEqual(page.items[0].sources[0].nativeId, "9007199254740993"); XCTAssertNil(page.nextPage)
        let calls = await peer.calls(); XCTAssertEqual(calls.map(\.operation), ["search"])
        XCTAssertEqual(calls[0].parameters[music: "page"], .number(2))
        XCTAssertEqual(calls[0].parameters[music: "limit"], .number(30))
        await service.close()
    }
    func testCanonicalLyricFailureIsNotEmptySuccessOrAnAdditionalNativeRequest() async throws {
        let peer = NativeMusicResultFixture(failure: .invalidResponse)
        let platforms = MusicNativePlatforms(client: try peer.makeClient())
        await assertMusicFailure(.upstreamInvalid) {
            _ = try await platforms.lyrics(platform: "kg", info: .obj([("songmid", .string("locator"))]))
        }
        let calls = await peer.calls(); XCTAssertEqual(calls.map(\.operation), ["lyrics"])
    }
    func testCanonicalWordsKeepExactMillisecondTranslationAndMissingMatchStaysEmpty() throws {
        let lines = try MusicNativeLyricsParser.parse(.init(text: "[1000,1000](1000,300,0)喵(1300,700,0)音乐",
            translation: "[00:01.000]Synthetic translation"))
        XCTAssertEqual(lines.count, 1); XCTAssertEqual(lines[0].text, "喵音乐")
        XCTAssertEqual(lines[0].translation, "Synthetic translation")
        XCTAssertEqual(lines[0].words.map(\.time), [1, 1.3])
        let mismatch = try MusicNativeLyricsParser.parse(.init(text: "[00:01.000]Original", translation: "[00:01.001]Wrong"))
        XCTAssertEqual(mismatch[0].translation, "")
    }
    func testCanonicalZeroDurationAndAlreadyDecodedEntitiesRemainLiteral() throws {
        let lines = try MusicNativeLyricsParser.parse(.init(text: "[1000,0](1000,0,0)Zero\n[1000,20](1000,20,0)&lt;"))
        XCTAssertEqual(lines[0].end, 1); XCTAssertEqual(lines[0].words[0].duration, 0)
        XCTAssertEqual(lines[1].text, "&lt;"); XCTAssertEqual(lines[1].end, 1.02, accuracy: 0.000001)
    }
    func testCanonicalRelativeWordsConvertedByNodeKeepOffsetsInNativePresentation() throws {
        let lines = try MusicNativeLyricsParser.parse(.init(text: "[offset:100]\n[1000,1000](1000,400,0)A(1400,600,0)B"))
        XCTAssertEqual(lines[0].text, "AB"); XCTAssertEqual(lines[0].words.map(\.time), [1.1, 1.5])
        XCTAssertEqual(lines[0].words.map(\.duration), [0.4, 0.6])
    }
}
