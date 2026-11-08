import Foundation
import XCTest
@testable import MeowMusicFeature

/// Expected branches come from iWebPlus b900c259 player-layout.js,
/// queue-sheet.js, mini-player.js and browse-settings.js/audio-quality.js.
/// These policy tests do not claim SwiftUI lifetime, gesture or device validation.
final class MusicReferenceUIRegressionTests: XCTestCase {
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

    func testFMHeaderUsesReferenceModeNamesAndKeepsSceneDetailsOutOfTheShortTitle() {
        XCTAssertEqual(MusicFMModeChoice.main.map { MusicReferencePlayerLabels.fmSource($0.action) },
                       ["FM·默认", "FM·熟悉", "FM·探索", "FM·拼图"])
        for scene in MusicFMModeChoice.scenes {
            XCTAssertEqual(MusicReferencePlayerLabels.fmSource(scene.action), "FM·场景")
        }
        XCTAssertEqual(MusicReferencePlayerLabels.fmSource(.ruleFM(ruleID: "1", tabTagID: "", sourceName: "深夜")),
                       "FM·深夜")
        XCTAssertEqual(MusicReferencePlayerLabels.fmSource(nil), "FM·私人漫游")
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
