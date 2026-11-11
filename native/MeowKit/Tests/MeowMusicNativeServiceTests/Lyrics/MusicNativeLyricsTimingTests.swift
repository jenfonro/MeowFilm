import XCTest
import MeowMusicFeature
@testable import MeowMusicNativeService

final class MusicNativeLyricsTimingTests: XCTestCase {
    func testPlainLRCUsesLineBoundariesWithoutGeneratingWords() throws {
        let text = "[00:01.000]你 好，A!\n[00:01.010]短句\n[00:02.000]最后一句"
        let rows = try MusicNativeLyricsParser.parse(.init(text: text), duration: 180)
        XCTAssertEqual(rows.map(\.text), ["你 好，A!", "短句", "最后一句"])
        XCTAssertTrue(rows.allSatisfy { $0.words.isEmpty })
        XCTAssertEqual(rows.map(\.end), [1.01, 2, 180])

        let open = try MusicNativeLyricsParser.parse(.init(text: text))
        XCTAssertTrue(open.allSatisfy { $0.words.isEmpty })
        XCTAssertEqual(open.last?.end, 2)
    }

    func testEnhancedLRCUsesRealAdjacentMarkersAndLeavesLastWordOpen() throws {
        let text = "[00:01.000]<00:01.000>A<00:01.010>B"
        for duration in [0.0, 180.0] {
            let row = try XCTUnwrap(MusicNativeLyricsParser.parse(.init(text: text), duration: duration).first)
            XCTAssertEqual(row.words.map(\.text), ["A", "B"])
            XCTAssertEqual(row.words.map(\.time), [1, 1.01])
            XCTAssertEqual(row.words[0].duration, 0.01, accuracy: 0.000001)
            XCTAssertEqual(row.words[1].duration, 0)
            XCTAssertEqual(row.end, duration > 0 ? duration : 1)
            XCTAssertEqual(MusicLyricTimeline.wordProgress(row.words[1], at: 1.009), 0)
            XCTAssertEqual(MusicLyricTimeline.wordProgress(row.words[1], at: 1.01), 1)
            XCTAssertEqual(MusicLyricTimeline.wordProgress(row.words[1], at: 300), 1)
        }
    }

    func testEnhancedLRCTrailingMarkerAndNextRealLineProvideNaturalEnds() throws {
        let rows = try MusicNativeLyricsParser.parse(.init(text:
            "[00:01.000]<00:01.000>A<00:01.020>\n[00:02.000]<00:02.000>B\n[00:02.030]C"), duration: 180)
        XCTAssertEqual(rows[0].end, 1.02)
        XCTAssertEqual(rows[0].words[0].duration, 0.02, accuracy: 0.000001)
        XCTAssertEqual(rows[1].words[0].duration, 0.03, accuracy: 0.000001)
        XCTAssertTrue(rows[2].words.isEmpty)
    }

    func testEnhancedLRCZeroIntervalsAndNegativeOffsetArePreserved() throws {
        let rows = try MusicNativeLyricsParser.parse(.init(
            text: "[offset:-1050]\n[00:01.000]<00:01.000>A<00:01.000>B<00:01.020>C",
            translation: "[offset:-1050]\n[00:01.000]译文"), duration: 180)
        let row = try XCTUnwrap(rows.first)
        XCTAssertEqual(row.time, 0)
        XCTAssertEqual(row.translation, "译文")
        XCTAssertEqual(row.words[0].time, -0.05, accuracy: 0.000001)
        XCTAssertEqual(row.words[1].time, -0.05, accuracy: 0.000001)
        XCTAssertEqual(row.words[2].time, -0.03, accuracy: 0.000001)
        XCTAssertEqual(row.words[0].duration, 0)
        XCTAssertEqual(row.words[1].duration, 0.02, accuracy: 0.000001)
        XCTAssertEqual(row.words[2].duration, 0)
    }

    func testRealZeroStartAndZeroDurationAreNotReplaced() throws {
        for text in ["[0,10](0,0,0)A(0,10,0)B", "[00:00.000]<00:00.000>A<00:00.000>B<00:00.010>"] {
            let row = try XCTUnwrap(MusicNativeLyricsParser.parse(.init(text: text)).first)
            XCTAssertEqual(row.time, 0)
            XCTAssertEqual(row.words.map(\.time), [0, 0])
            XCTAssertEqual(row.words.map(\.duration), [0, 0.01])
        }
    }

    func testRelativeEnhancedLRCMarkersKeepRealShortIntervals() throws {
        let row = try XCTUnwrap(MusicNativeLyricsParser.parse(.init(
            text: "[00:10.000][[00:00.010]]A[[00:00.020]]B[[00:00.025]]")).first)
        XCTAssertEqual(row.words.map(\.time), [10.01, 10.02])
        XCTAssertEqual(row.words[0].duration, 0.01, accuracy: 0.000001)
        XCTAssertEqual(row.words[1].duration, 0.005, accuracy: 0.000001)
    }

    func testEmptyRealMarkerEndsPrecedingWordWithoutExtendingAcrossTheGap() throws {
        let row = try XCTUnwrap(MusicNativeLyricsParser.parse(.init(
            text: "[00:01.000]<00:01.000>A<00:01.010><00:01.030>B")).first)
        XCTAssertEqual(row.words.map(\.text), ["A", "B"])
        XCTAssertEqual(row.words.map(\.time), [1, 1.03])
        XCTAssertEqual(row.words[0].duration, 0.01, accuracy: 0.000001)
        XCTAssertEqual(row.words[1].duration, 0)
    }

    func testQRCAndYRCExplicitDurationsSurviveShortZeroAndNegativeTimes() throws {
        for text in [
            "[offset:-1100]\n[1000,20]A(1000,10)B(1010,0)",
            "[offset:-1100]\n[1000,20](1000,10,0)A(1010,0,0)B"
        ] {
            let row = try XCTUnwrap(MusicNativeLyricsParser.parse(.init(text: text), duration: 180).first)
            XCTAssertEqual(row.time, 0)
            XCTAssertEqual(row.end, 0)
            XCTAssertEqual(row.words.map(\.text), ["A", "B"])
            XCTAssertEqual(row.words[0].time, -0.1, accuracy: 0.000001)
            XCTAssertEqual(row.words[1].time, -0.09, accuracy: 0.000001)
            XCTAssertEqual(row.words.map(\.duration), [0.01, 0])
        }
    }

    func testKRCExplicitDurationsAndNegativeOffsetSurviveConversion() throws {
        let raw = try MusicNativeLyricsParser.kugou("[offset:-1100]\n[1000,20]<0,10,0>A<10,0,0>B")
        let row = try XCTUnwrap(MusicNativeLyricsParser.parse(raw, duration: 180).first)
        XCTAssertEqual(row.time, 0)
        XCTAssertEqual(row.end, 0)
        XCTAssertEqual(row.words[0].time, -0.1, accuracy: 0.000001)
        XCTAssertEqual(row.words[1].time, -0.09, accuracy: 0.000001)
        XCTAssertEqual(row.words.map(\.duration), [0.01, 0])
    }

    func testNegativeOffsetClampsSeekLinesAndTranslationKeysWithoutClampingWordBoundaries() throws {
        let rows = try MusicNativeLyricsParser.parse(.init(
            text: "[offset:-1100]\n[00:01.000]<00:01.000>A\n[00:01.050]<00:01.050>B\n[00:02.000]C",
            translation: "[offset:-1100]\n[00:01.000]译一\n[00:01.050]译二\n[00:02.000]译三"))
        XCTAssertEqual(rows[0].time, 0)
        XCTAssertEqual(rows[1].time, 0)
        XCTAssertEqual(rows[2].time, 0.9, accuracy: 0.000001)
        XCTAssertEqual(rows[0].end, 0)
        XCTAssertEqual(rows[1].end, 0.9, accuracy: 0.000001)
        XCTAssertEqual(rows[2].end, 0.9, accuracy: 0.000001)
        XCTAssertEqual(rows.map(\.translation), ["译一\n译二", "译一\n译二", "译三"])
        XCTAssertEqual(rows[0].words[0].time, -0.1, accuracy: 0.000001)
        XCTAssertEqual(rows[0].words[0].duration, 0.05, accuracy: 0.000001)
        XCTAssertEqual(rows[1].words[0].time, -0.05, accuracy: 0.000001)
        XCTAssertEqual(rows[1].words[0].duration, 0.95, accuracy: 0.000001)
        XCTAssertTrue(rows[2].words.isEmpty)
    }
}
