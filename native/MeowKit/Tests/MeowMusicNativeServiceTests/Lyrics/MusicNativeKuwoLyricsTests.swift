import Foundation
import XCTest
import MeowMusicFeature
import MeowMusicTestSupport
@testable import MeowMusicNativeService

final class MusicNativeKuwoLyricsTests: XCTestCase {
    func testCanonicalWordsOffsetAndTranslationReachTheGenericNativePresenter() async throws {
        let peer = NativeMusicResultFixture([MusicNativeLyricsFixture.lyrics("kw")])
        let port = MusicNativePlatforms(client: try peer.makeClient())
        let value = try await port.lyrics(platform: "kw", info: .obj([("songmid", .string("9007199254740993"))]))
        let rows = try MusicNativeLyricsParser.parse(value)
        XCTAssertEqual(rows.map(\.text), ["AB", "C"]); XCTAssertEqual(value.romanization, "")
        XCTAssertEqual(rows[0].time, 1.1, accuracy: 0.000001)
        XCTAssertEqual(rows[0].translation, "Translation")
        XCTAssertEqual(rows[0].words[0].time, 1.3, accuracy: 0.000001)
        XCTAssertEqual(rows[0].words[0].duration, 0.2, accuracy: 0.000001)
        XCTAssertEqual(rows[0].words[1].time, 1.9, accuracy: 0.000001)
        XCTAssertEqual(rows[1].words[0].time, 2.2, accuracy: 0.000001)
        let calls = await peer.calls(); XCTAssertEqual(calls.count, 1)
        XCTAssertEqual(calls[0].parameters[music: "musicInfo"][music: "songmid"].musicText, "9007199254740993")
    }
    func testMainLyricsDoNotRequireOptionalTranslationOrRomanization() async throws {
        let peer = NativeMusicResultFixture([.object(["text": .string("[00:01.000]A & B\n[00:02.000]next")])])
        let port = MusicNativePlatforms(client: try peer.makeClient())
        let value = try await port.lyrics(platform: "kw", info: .obj([("songmid", .string("123"))]))
        XCTAssertEqual(try MusicNativeLyricsParser.parse(value).map(\.text), ["A & B", "next"])
        XCTAssertEqual(value.translation, ""); XCTAssertEqual(value.romanization, "")
    }
    func testNativeTextBudgetStillRejectsAnOversizedMainLyric() throws {
        XCTAssertThrowsError(try MusicNativeLyricsParser.parse(.init(text: String(repeating: "x", count: 512 * 1024 + 1)))) {
            XCTAssertEqual($0 as? MusicServiceError, .limit)
        }
    }
}
