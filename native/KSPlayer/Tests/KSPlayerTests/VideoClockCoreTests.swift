import XCTest
import CoreMedia
import QuartzCore
@testable import KSPlayer

final class VideoClockCoreTests: XCTestCase {
    func testAudioTimestampExtrapolationUsesTheTransportRate() {
        for rate in [1.0, 2, 4, 6, 8] {
            var clock = KSClock()
            clock.setRate(rate)
            clock.time = CMTime(seconds: 10, preferredTimescale: 600)
            XCTAssertEqual(clock.getTime(at: clock.lastMediaTime + 1), 10 + rate, accuracy: 0.0001)
        }
    }

    func testVideoPresentationDoesNotDiscardFractionalRunningTime() {
        var clock = KSClock(continuous: true)
        clock.time = CMTime(seconds: 10, preferredTimescale: 600)
        clock.setRate(8)
        let before = clock.getTime()
        // Displaying another frame does not restart the running master clock
        // from this PTS. The displayed time itself still reflects that frame.
        clock.time = CMTime(seconds: 10.1, preferredTimescale: 600)
        XCTAssertEqual(clock.time.seconds, 10.1, accuracy: 0.0001)
        XCTAssertEqual(clock.getTime(), before, accuracy: 0.02)
        XCTAssertEqual(clock.getTime(at: CACurrentMediaTime() + 1) - clock.getTime(), 8, accuracy: 0.02)
    }

    func testPauseResumeSeekAndRateChangeDoNotAccumulatePausedWallTime() {
        var clock = KSClock(continuous: true)
        clock.time = CMTime(seconds: 2, preferredTimescale: 600)
        XCTAssertEqual(clock.getTime(at: clock.lastMediaTime + 60), 2, accuracy: 0.0001)
        clock.setRate(8)
        let beforeChange = clock.getTime()
        clock.setRate(2)
        XCTAssertEqual(clock.getTime(), beforeChange, accuracy: 0.02)
        clock.setRate(0)
        let paused = clock.getTime()
        XCTAssertEqual(clock.getTime(at: CACurrentMediaTime() + 60), paused, accuracy: 0.0001)
        clock.time = CMTime(seconds: 15, preferredTimescale: 600)
        XCTAssertEqual(clock.getTime(at: clock.lastMediaTime + 60), 15, accuracy: 0.0001)
        clock.setRate(6)
        XCTAssertEqual(clock.getTime(), 15, accuracy: 0.02)
        XCTAssertEqual(clock.getTime(at: CACurrentMediaTime() + 1), 21, accuracy: 0.02)
    }

    func testVideoFallbackCanContinueFromTheRealAudioAxisWithoutInventingPresentedTime() {
        var clock = KSClock(continuous: true)
        clock.time = CMTime(seconds: 2, preferredTimescale: 600)
        clock.setRate(8)
        clock.synchronize(to: 10)
        XCTAssertEqual(clock.time.seconds, 2)
        XCTAssertEqual(clock.getTime(), 10, accuracy: 0.02)
        XCTAssertEqual(clock.getTime(at: CACurrentMediaTime() + 1), 18, accuracy: 0.02)
    }

    func testChapterPositionsUseTheSameZeroAsPlaybackAndSeek() throws {
        let chapter = try XCTUnwrap(Chapter.playbackTimeline(
            start: 12, end: 20, title: "片头", origin: 10, duration: 60))
        XCTAssertEqual(chapter.start, 2)
        XCTAssertEqual(chapter.end, 10)
        XCTAssertEqual(chapter.title, "片头")
        let clipped = try XCTUnwrap(Chapter.playbackTimeline(
            start: 5, end: 80, title: "Original title", origin: 10, duration: 60))
        XCTAssertEqual(clipped.start, 0)
        XCTAssertEqual(clipped.end, 60)
        XCTAssertEqual(clipped.title, "Original title")
    }

    func testChapterInvalidOrEmptyRangesAreNotInvented() {
        for (start, end) in [(Double.nan, 20.0), (2.0, Double.infinity), (2.0, 2.0),
                             (20.0, 10.0), (0.0, 5.0), (75.0, 80.0)] {
            XCTAssertNil(Chapter.playbackTimeline(
                start: start, end: end, title: "片尾", origin: 10, duration: 60))
        }
    }
}
