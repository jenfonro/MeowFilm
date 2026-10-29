import Foundation
import XCTest
@testable import MeowMusicFeature

/// Expected branches come from iWebPlus b900c259 player-layout.js,
/// queue-sheet.js, mini-player.js and browse-settings.js/audio-quality.js.
/// These policy tests do not claim SwiftUI lifetime, gesture or device validation.
final class MusicReferenceUIRegressionTests: XCTestCase {
    func testCompactControlSurfaceEndsAtTheTitleBoundaryWithoutMovingItsButtons() {
        for width in [292.0, 320, 362, 402] {
            for line in [22.0, 44, 64] {
                let layout = MusicMiniPlayerLayout(lineHeight: line)
                let frame = layout.compactControlFrame(width: width)
                XCTAssertEqual(Double(frame.minX), width - 100)
                XCTAssertEqual(Double(frame.maxX), width)
                XCTAssertEqual(Double(frame.minX + frame.width - 74), width - 74)
                XCTAssertEqual(Double(frame.minX + frame.width - 30), width - 30)
                XCTAssertEqual(Double(frame.maxY) + 6, layout.compactHeight)
            }
        }
        for width in [0, -1, Double.nan, .infinity] {
            XCTAssertEqual(MusicMiniPlayerLayout(lineHeight: 22).compactControlFrame(width: width).width, 0)
        }
    }

    func testMiniPlayerDynamicTypeMovesControlsBelowItsExpandedContent() {
        let normal = MusicMiniPlayerLayout(lineHeight: 22)
        XCTAssertEqual(normal.compactHeight, 58)
        XCTAssertEqual(normal.detailLineHeight, 21)
        XCTAssertEqual(normal.compactArtworkTop, 8)
        XCTAssertEqual(normal.expandedInfoHeight, 100)
        XCTAssertEqual(normal.seekTop, 100)
        XCTAssertEqual(normal.controlsTop, 146)
        XCTAssertEqual(normal.expandedHeight, 196)
        for lineHeight in [22.0, 32, 44, 64] {
            let layout = MusicMiniPlayerLayout(lineHeight: lineHeight)
            XCTAssertGreaterThanOrEqual(layout.compactHeight, lineHeight + 24)
            XCTAssertGreaterThanOrEqual(layout.expandedInfoHeight, layout.detailLineHeight * 3 + 37)
            XCTAssertEqual(layout.seekTop, layout.expandedInfoHeight)
            XCTAssertGreaterThanOrEqual(layout.controlsTop - layout.seekTop, 44)
            XCTAssertGreaterThanOrEqual(layout.expandedHeight - layout.controlsTop, 44)
            XCTAssertEqual(layout.compactControlsTop + 44 + 6, layout.compactHeight)
        }
        for invalid in [Double.nan, .infinity, -1, 0] {
            XCTAssertEqual(MusicMiniPlayerLayout(lineHeight: invalid), normal)
        }
    }
    func testSourceLabelUsesPublishedContextWithFMOnlyTakingPrecedenceWhenActive() {
        XCTAssertEqual(MusicReferencePlayerLabels.source(fm: nil, context: "搜索：歌曲",
            collection: "收藏", track: "歌名"), "搜索：歌曲")
        XCTAssertEqual(MusicReferencePlayerLabels.source(fm: "场景 FM", context: "远端歌单",
            collection: "收藏", track: "歌名"), "场景 FM")
        XCTAssertEqual(MusicReferencePlayerLabels.source(fm: nil, context: nil,
            collection: "收藏", track: "歌名"), "收藏")
        XCTAssertEqual(MusicReferencePlayerLabels.source(fm: nil, context: "",
            collection: nil, track: "歌名"), "歌名")
    }

    func testRequestedQualityIsExplicitlyARequestNotAnActualQualityClaim() {
        XCTAssertEqual(MusicReferencePlayerLabels.quality(requestedLabel: "无损"), "请求：无损")
        XCTAssertEqual(MusicReferencePlayerLabels.quality(requestedLabel: nil), "音源与音质")
    }

    func testQueueHasThreeCategoriesAndHistorySelectionIsSecondLevel() {
        XCTAssertEqual(MusicReferenceQueuePage.allCases, [.current, .songs, .playlists])
        XCTAssertEqual(MusicReferenceQueuePage.allCases.map(\.title), ["当前播放", "已播歌曲", "已播歌单"])
        XCTAssertEqual(MusicReferenceQueuePage.historySelection(nil, ids: ["a", "b"]), "a")
        XCTAssertEqual(MusicReferenceQueuePage.historySelection("b", ids: ["a", "b"]), "b")
    }

    func testEmptyHistoryDoesNotRemoveItsCategoryOrCreateSentinelCategories() {
        XCTAssertNil(MusicReferenceQueuePage.historySelection(nil, ids: []))
        XCTAssertEqual(MusicReferenceQueuePage(rawValue: 2), .playlists)
        XCTAssertNil(MusicReferenceQueuePage(rawValue: -1))
        XCTAssertNil(MusicReferenceQueuePage(rawValue: 3))
    }

    func testHistoryDeletionReconcilesStableSelectionWithoutReinterpretingAnIndex() {
        XCTAssertEqual(MusicReferenceQueuePage.historySelection("c", ids: ["b", "c"]), "c")
        XCTAssertEqual(MusicReferenceQueuePage.historySelection("a", ids: ["b", "c"]), "b")
        XCTAssertNil(MusicReferenceQueuePage.historySelection("b", ids: []))
    }

    func testLastEffectiveQualityIsLockedBeforeAnyWrite() {
        XCTAssertTrue(MusicQualityTogglePolicy.locksQuality("standard", qualities: ["standard"], spatial: []))
        XCTAssertFalse(MusicQualityTogglePolicy.locksQuality("high", qualities: ["standard"], spatial: []))
        XCTAssertFalse(MusicQualityTogglePolicy.locksQuality("standard", qualities: ["standard", "high"], spatial: []))
        // Empty spatial does not let the user turn off the only usable category.
        XCTAssertTrue(MusicQualityTogglePolicy.locksQuality("standard",
            qualities: ["standard", "spatial"], spatial: []))
        XCTAssertFalse(MusicQualityTogglePolicy.locksQuality("spatial",
            qualities: ["standard", "spatial"], spatial: []))
    }

    func testSpatialOnlyRequiresBothCategoryAndOneRealSubtype() {
        XCTAssertTrue(MusicQualityTogglePolicy.locksQuality("spatial", qualities: ["spatial"], spatial: ["dolby"]))
        XCTAssertTrue(MusicQualityTogglePolicy.locksSpatial("dolby", qualities: ["spatial"], spatial: ["dolby"]))
        XCTAssertFalse(MusicQualityTogglePolicy.locksSpatial("sky", qualities: ["spatial"], spatial: ["dolby"]))
        XCTAssertFalse(MusicQualityTogglePolicy.locksSpatial("dolby",
            qualities: ["spatial"], spatial: ["dolby", "sky"]))
        XCTAssertFalse(MusicQualityTogglePolicy.locksSpatial("dolby",
            qualities: ["spatial", "standard"], spatial: ["dolby"]))
        XCTAssertFalse(MusicQualityTogglePolicy.locksSpatial("dolby",
            qualities: ["standard"], spatial: ["dolby"]))
    }

    func testUnknownQualityAndSpatialNamesNeverCountAsEffectiveAlternatives() {
        XCTAssertFalse(MusicQualityTogglePolicy.hasEnabledQuality(qualities: ["unknown"], spatial: ["dolby"]))
        XCTAssertFalse(MusicQualityTogglePolicy.hasEnabledQuality(qualities: ["spatial"], spatial: ["unknown"]))
        XCTAssertTrue(MusicQualityTogglePolicy.locksQuality("high", qualities: ["high", "unknown"], spatial: []))
        XCTAssertTrue(MusicQualityTogglePolicy.locksSpatial("dolby",
            qualities: ["spatial"], spatial: ["dolby", "unknown"]))
    }

}
