import Foundation
import XCTest
@testable import MeowMusicFeature

final class MusicMiniPlayerStateTests: XCTestCase {
    func testMiniMorphKeepsReferenceBezierRatherThanASpringApproximation() {
        let expansion = MusicMiniPlayerMorph(expanded: true)
        XCTAssertEqual(expansion.duration, 0.74)
        XCTAssertEqual(expansion.x1, 0.415)
        XCTAssertEqual(expansion.y1, 1.005)
        XCTAssertEqual(expansion.x2, 0.228)
        XCTAssertEqual(expansion.y2, 1.126)
        let collapse = MusicMiniPlayerMorph(expanded: false)
        XCTAssertEqual(collapse.duration, 0.37)
        XCTAssertEqual(collapse.x1, 0.35)
        XCTAssertEqual(collapse.y1, 0.3)
        XCTAssertEqual(collapse.x2, 0.5)
        XCTAssertEqual(collapse.y2, 0.95)
    }

    func testMiniCoverAndDetailLinesUseIndependentReferenceAnchors() {
        let normal = MusicMiniPlayerLayout(lineHeight: 22)
        XCTAssertEqual(normal.compactArtworkTop, 8)
        XCTAssertEqual(normal.compactControlsTop, 8)
        XCTAssertEqual(normal.detailLineHeight, 21)
        XCTAssertEqual(normal.expandedHeight - normal.seekTop - 44, 52)
        XCTAssertEqual(normal.expandedHeight - normal.controlsTop - 44, 6)
        let large = MusicMiniPlayerLayout(lineHeight: 44)
        XCTAssertEqual(large.detailLineHeight, 42)
        XCTAssertGreaterThan(large.compactArtworkTop, normal.compactArtworkTop)
        XCTAssertGreaterThanOrEqual(large.expandedInfoHeight, large.detailLineHeight * 3 + 37)
    }

    private let attempt = UUID()
    private func sample(_ at: Double, _ position: Double, key: String? = "occurrence",
                        session: UUID? = nil, rate: Double = 1,
                        playing: Bool = true, seeking: Bool = false) -> MusicMiniPlayerSample {
        .init(occurrenceID: key, attemptID: session ?? attempt, at: at, position: position,
              rate: rate, playing: playing, seeking: seeking)
    }
    private func identity(occurrence: String = "occurrence", attempt: UUID = UUID(),
                          access: UUID = UUID()) -> MusicMiniPlayerPlaybackIdentity {
        .init(occurrenceID: occurrence, nativeAttemptID: attempt, accessGeneration: access)
    }

    func testOrdinaryPlayingAndAutomaticTrackChangesNeverExpand() {
        var state = MusicMiniPlayerLifecycle()
        state.sample(sample(0, 0))
        state.sample(sample(20, 20))
        state.sample(sample(21, 0, key: "automatic-next"))
        XCTAssertFalse(state.expanded)
        XCTAssertEqual(state.elapsed, 0)
    }

    func testExplicitActivationCollapsesOnlyAfterFiveConfirmedPlayingSeconds() {
        var state = MusicMiniPlayerLifecycle()
        state.activate("occurrence")
        state.sample(sample(10, 40))
        state.sample(sample(14.9, 44.9))
        XCTAssertTrue(state.expanded)
        XCTAssertEqual(state.elapsed, 4.9, accuracy: 0.0001)
        state.sample(sample(15, 45))
        XCTAssertFalse(state.expanded)
        XCTAssertFalse(state.held)
        XCTAssertEqual(state.elapsed, 0)
    }

    func testWaitingStallPauseAndSuspensionDoNotSpendTheRemainingTime() {
        var state = MusicMiniPlayerLifecycle()
        state.activate("occurrence")
        state.sample(sample(0, 0, playing: false))
        state.sample(sample(30, 0, playing: false))
        state.sample(sample(31, 0))
        state.sample(sample(33, 2))
        state.sample(sample(34, 2)) // Playing flag alone cannot invent media movement.
        state.sample(sample(50, 2))
        XCTAssertEqual(state.elapsed, 2)
        state.sample(sample(51, 2, playing: false))
        state.sample(sample(80, 2))
        state.sample(sample(81, 3))
        XCTAssertEqual(state.elapsed, 3)
        state.suspend()
        state.sample(sample(100, 22))
        XCTAssertEqual(state.elapsed, 3)
        state.sample(sample(101.9, 23.9))
        XCTAssertTrue(state.expanded)
        state.sample(sample(102, 24))
        XCTAssertFalse(state.expanded)
    }

    func testSeekJumpsReverseMovementAndExplicitSeekingNeverCount() {
        var state = MusicMiniPlayerLifecycle()
        state.activate("occurrence")
        state.sample(sample(0, 0))
        state.sample(sample(1, 1))
        state.sample(sample(2, 100))
        XCTAssertEqual(state.elapsed, 1)
        state.sample(sample(3, 10))
        XCTAssertEqual(state.elapsed, 1)
        state.sample(sample(4, 11))
        XCTAssertEqual(state.elapsed, 2)
        state.sample(sample(5, 100, seeking: true))
        state.sample(sample(6, 100))
        XCTAssertEqual(state.elapsed, 2)
        state.sample(sample(7, 101))
        XCTAssertEqual(state.elapsed, 3)
    }

    func testRateAndAttemptBoundariesRebaseWithoutChargingOldIntervals() {
        var state = MusicMiniPlayerLifecycle()
        state.activate("occurrence")
        state.sample(sample(0, 0, rate: 2))
        state.sample(sample(2, 4, rate: 2))
        XCTAssertEqual(state.elapsed, 2)
        state.sample(sample(3, 5, rate: 1))
        XCTAssertEqual(state.elapsed, 2)
        let replacement = UUID()
        state.sample(sample(4, 0, session: replacement))
        XCTAssertEqual(state.elapsed, 2)
        state.sample(sample(6.9, 2.9, session: replacement))
        XCTAssertTrue(state.expanded)
        state.sample(sample(7, 3, session: replacement))
        XCTAssertFalse(state.expanded)
    }

    func testSparseDuplicateAndInvalidSamplesNeverInventPlayback() {
        var state = MusicMiniPlayerLifecycle()
        state.activate("occurrence")
        state.sample(sample(0, 0))
        state.sample(sample(0, 5))
        XCTAssertEqual(state.elapsed, 0)
        state.sample(sample(1, 1))
        XCTAssertEqual(state.elapsed, 1)
        state.sample(sample(2, .nan))
        state.sample(sample(3, 3))
        XCTAssertEqual(state.elapsed, 1)
        state.sample(sample(4, 4, rate: 0))
        state.sample(sample(5, 5))
        XCTAssertEqual(state.elapsed, 1)
        state.sample(sample(4, 6)) // Reversed monotonic timestamp.
        XCTAssertEqual(state.elapsed, 1)
        state.sample(.init(occurrenceID: "occurrence", attemptID: nil, at: 10,
                           position: 10, rate: 1, playing: true, seeking: false))
        XCTAssertTrue(state.expanded)
        XCTAssertEqual(state.elapsed, 1)
    }

    func testUserInteractionHoldsAcrossTrackChangesButClearAlwaysCollapses() {
        var state = MusicMiniPlayerLifecycle()
        state.activate("occurrence")
        state.sample(sample(0, 0))
        state.sample(sample(2, 2))
        state.hold()
        state.sample(sample(100, 100))
        state.select("next")
        XCTAssertTrue(state.expanded)
        XCTAssertTrue(state.held)
        XCTAssertEqual(state.elapsed, 2)
        state.select(nil)
        XCTAssertFalse(state.expanded)
        XCTAssertFalse(state.held)
    }

    func testUnheldSelectionAndNewExplicitPlaybackResetTheLifecycle() {
        var state = MusicMiniPlayerLifecycle()
        state.activate("occurrence")
        state.select("next")
        XCTAssertFalse(state.expanded)
        state.activate("next")
        state.hold()
        state.activate("third")
        XCTAssertTrue(state.expanded)
        XCTAssertFalse(state.held)
        XCTAssertEqual(state.elapsed, 0)
        state.invalidate()
        state.sample(sample(20, 20, key: "third"))
        XCTAssertFalse(state.expanded)
    }

    func testCompactTextAndUnknownProgressMatchTheReferenceDisplayRules() {
        let track = MusicRemoteTrack(ref: .init(kind: .library, id: "song"), songId: "song",
                                     title: "歌手 - 歌曲", artist: "歌手")
        XCTAssertEqual(MusicMiniPlayerText.compactTitle(track), "歌曲 - 歌手")
        XCTAssertEqual(MusicMiniPlayerText.compactTitle(nil), "暂无播放")
        XCTAssertEqual(MusicMiniPlayerText.time(nil), "--:--")
        XCTAssertEqual(MusicMiniPlayerText.time(.nan), "--:--")
        XCTAssertEqual(MusicMiniPlayerText.time(65.9), "01:05")
        XCTAssertEqual(MusicMiniPlayerText.progress(position: 40, duration: 100), 0.4)
        XCTAssertEqual(MusicMiniPlayerText.progress(position: .nan, duration: 100), 0)
        XCTAssertEqual(MusicMiniPlayerText.progress(position: 80, duration: nil), 0)
        XCTAssertEqual(MusicMiniPlayerText.progress(position: 120, duration: 100), 1)
    }
}
