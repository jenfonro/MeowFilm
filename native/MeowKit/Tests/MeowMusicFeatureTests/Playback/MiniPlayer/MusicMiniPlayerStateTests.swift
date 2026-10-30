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

    func testMetadataProgressAndControlsStaySeparatedWithLargerText() {
        let normal = MusicMiniPlayerLayout(titleLineHeight: 22, artistLineHeight: 15, lyricLineHeight: 20)
        let large = MusicMiniPlayerLayout(titleLineHeight: 44, artistLineHeight: 30, lyricLineHeight: 40)
        for layout in [normal, large] {
            XCTAssertGreaterThanOrEqual(layout.seekTop,
                16 + layout.titleLineHeight + 6 + layout.artistLineHeight + 8 + layout.lyricLineHeight + 16)
            XCTAssertGreaterThan(layout.controlsTop, layout.seekTop + 46)
            XCTAssertGreaterThanOrEqual(layout.expandedHeight, layout.controlsTop + 56 + 8)
            XCTAssertEqual(layout.compactArtworkTop + 21, layout.compactHeight / 2)
            let controls = layout.compactControlFrame(width: 292)
            XCTAssertGreaterThanOrEqual(controls.minX, 0)
            XCTAssertLessThanOrEqual(controls.maxX, 292)
            XCTAssertLessThanOrEqual(controls.maxY, CGFloat(layout.compactHeight))
        }
        XCTAssertGreaterThan(large.expandedHeight, normal.expandedHeight)
        XCTAssertGreaterThan(large.compactHeight, normal.compactHeight)
    }

    func testHomePreviewExpiresAfterFiveSecondsAndNewActivationRestartsIt() {
        var state = MusicMiniPlayerLifecycle()
        state.activate("first", at: 10)
        XCTAssertEqual(state.autoCollapseAt, 15)
        state.expire(at: 14.999)
        XCTAssertTrue(state.expanded)
        state.expire(at: 15)
        XCTAssertFalse(state.expanded)
        XCTAssertNil(state.autoCollapseAt)
        state.hold()
        XCTAssertFalse(state.expanded)
        state.activate("second", at: 20)
        state.activate("third", at: 23)
        state.expire(at: 25)
        XCTAssertTrue(state.expanded)
        XCTAssertEqual(state.autoCollapseAt, 28)
        state.expire(at: 28)
        XCTAssertFalse(state.expanded)
    }

    func testPlaybackControlHoldsAcrossTracksUntilOutsideInteraction() {
        var state = MusicMiniPlayerLifecycle()
        state.activate("first", at: 0)
        state.interactOutside()
        XCTAssertTrue(state.expanded, "Only a held card follows outside-touch collapse")
        state.hold()
        XCTAssertTrue(state.held)
        XCTAssertNil(state.autoCollapseAt)
        state.expire(at: 100)
        state.select("next")
        XCTAssertTrue(state.expanded)
        XCTAssertTrue(state.held)
        state.interactOutside()
        XCTAssertFalse(state.expanded)
        XCTAssertFalse(state.held)
        state.select("ordinary-next")
        XCTAssertFalse(state.expanded)
    }

    func testSelectionClearAndInvalidationEndPreviewButNewHomeActionStartsFresh() {
        var state = MusicMiniPlayerLifecycle()
        state.select("ordinary")
        XCTAssertFalse(state.expanded)
        state.activate("first", at: 0)
        state.select("automatic-next")
        XCTAssertFalse(state.expanded)
        state.activate("second", at: 1)
        state.hold()
        state.activate("third", at: 2)
        XCTAssertFalse(state.held)
        XCTAssertEqual(state.autoCollapseAt, 7)
        state.hold()
        state.select(nil)
        XCTAssertFalse(state.expanded)
        state.activate("fourth", at: 3)
        state.invalidate()
        state.expire(at: 100)
        XCTAssertFalse(state.expanded)
        XCTAssertNil(state.autoCollapseAt)
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
