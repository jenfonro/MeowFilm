import Foundation
import XCTest
import MeowMusicFeature

final class MusicLyricContractTests: XCTestCase {
    func testLineAndWordPayloadsRoundTripWithoutAdditionalTimingMetadata() throws {
        let json = Data("""
            [{"index":0,"time":0,"end":1,"text":"AB","translation":"译文","words":[
            {"time":-0.1,"duration":0.01,"text":"A"},{"time":-0.09,"duration":0,"text":"B"}]},
            {"index":1,"time":1,"end":5,"text":"整句","translation":"","words":[]}]
            """.utf8)
        let lines = try JSONDecoder().decode([MusicLyricLine].self, from: json)
        let encoded = try JSONEncoder().encode(lines)
        XCTAssertEqual(try JSONDecoder().decode([MusicLyricLine].self, from: encoded), lines)
        let payload = try XCTUnwrap(JSONSerialization.jsonObject(with: encoded) as? [[String: Any]])
        XCTAssertEqual(Set(payload[0].keys), ["index", "time", "end", "text", "translation", "words"])
        XCTAssertTrue(lines[1].words.isEmpty)
        XCTAssertEqual(lines[0].time, 0)
        XCTAssertEqual(lines[0].words[0].time, -0.1)
        XCTAssertEqual(lines[0].words[0].duration, 0.01)
        XCTAssertEqual(lines[0].words[1].duration, 0)
    }

    func testZeroDurationWordHighlightsAtItsStartAndHolds() {
        let word = MusicLyricWord(time: 1, duration: 0, text: "末词")
        XCTAssertEqual(MusicLyricTimeline.wordProgress(word, at: 0.999), 0)
        XCTAssertEqual(MusicLyricTimeline.wordProgress(word, at: 1), 1)
        XCTAssertEqual(MusicLyricTimeline.wordProgress(word, at: 300), 1)
    }
}
