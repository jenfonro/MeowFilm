import Foundation
import XCTest
import MeowMusicFeature
import MeowMusicTestSupport
import MeowRuntime
@testable import MeowMusicNativeService

final class MusicNativeMiguLyricsTests: XCTestCase {
    func testCanonicalAbsoluteWordTimesAndOptionalTextReachNativePresentation() async throws {
        let result: RuntimeValue = .object([
            "text": .string("[1000,800](1000,300,0)你(1300,500,0)好\n[2000,500](2000,500,0)世界"),
            "translation": .string("[00:01.000]Hello\n[00:02.000]World")
        ])
        let peer = NativeMusicResultFixture([result])
        let port = MusicNativePlatforms(client: try peer.makeClient())
        let value = try await port.lyrics(platform: "mg", info: .obj([("songmid", .string("123")), ("copyrightId", .string("600123"))]))
        let rows = try MusicNativeLyricsParser.parse(value)
        XCTAssertEqual(rows.map(\.text), ["你好", "世界"]); XCTAssertEqual(rows.map(\.translation), ["Hello", "World"])
        XCTAssertEqual(rows[0].words[0].time, 1); XCTAssertEqual(rows[0].words[0].duration, 0.3, accuracy: 0.000001)
        XCTAssertEqual(rows[0].words[1].time, 1.3, accuracy: 0.000001)
        XCTAssertEqual(rows[1].words[0].time, 2); XCTAssertEqual(rows[1].words[0].duration, 0.5)
    }
    func testCanonicalLyricFailureIsNotRetriedOrReplacedWithAnotherFormat() async throws {
        for (failure, expected) in [(MusicServiceError.invalidResponse, MusicServiceError.upstreamInvalid),
                                   (.upstreamInvalid, .upstreamInvalid), (.upstreamFailed, .upstreamFailed), (.limit, .limit)] {
            let peer = NativeMusicResultFixture(failure: failure), client = try peer.makeClient()
            let port = MusicNativePlatforms(client: client)
            await assertMusicFailure(expected) { _ = try await port.lyrics(platform: "mg", info: .obj([("songmid", .string("123"))])) }
            let calls = await peer.calls(); XCTAssertEqual(calls.map(\.operation), ["lyrics"])
        }
    }
}
