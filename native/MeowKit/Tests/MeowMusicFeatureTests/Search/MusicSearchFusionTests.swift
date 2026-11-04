import XCTest
@testable import MeowMusicFeature

final class MusicSearchFusionTests: XCTestCase {
    private func track(_ platform: String, _ native: String, songID: String = "same", title: String = "Song",
                       artist: String = "Artist", duration: Double = 180) -> MusicRemoteTrack {
        .init(ref: .init(kind: .handle, id: platform + native), songId: songID, title: title,
              artist: artist, album: "Album", duration: duration,
              sources: [.init(sourceKey: platform + ":" + native, platform: platform, nativeId: native)])
    }
    func testReciprocalRankFusionIsArrivalIndependentAndKeepsRealActionReferences() throws {
        let wy = track("wy", "a"), qq = track("tx", "b", title: "Ｓｏｎｇ"), other = track("kg", "c", songID: "other", title: "Other")
        let batches: [MusicSearchFusion.SongBatch] = [
            .init(platform: .netease, items: [wy]), .init(platform: .qq, items: [qq]),
            .init(platform: .kugou, items: [other])
        ]
        let forward = MusicSearchFusion.songs(batches), reverse = MusicSearchFusion.songs(Array(batches.reversed()))
        XCTAssertEqual(forward.map(\.id), reverse.map(\.id))
        let winner = try XCTUnwrap(forward.first)
        XCTAssertEqual(winner.id, "same")
        XCTAssertEqual(winner.score, 1.0 / 3.0, accuracy: 0.000001)
        XCTAssertEqual(Set(winner.variants.map(\.ref)), Set([wy.ref, qq.ref]))
        XCTAssertEqual(winner.track.ref, reverse.first?.track.ref)
        XCTAssertEqual(winner.matchedPlatforms, ["tx", "wy"])
        XCTAssertTrue(winner.variants.contains(winner.track))
    }
    func testDifferentRecordingAndMissingDurationNeverFuseJustBecauseSongIDMatches() {
        let original = track("wy", "a")
        let live = track("tx", "b", title: "Song (Live)")
        let longer = track("kg", "c", duration: 220)
        let unknown = track("tx", "d", duration: 0)
        let rows = MusicSearchFusion.songs([
            .init(platform: .netease, items: [original]), .init(platform: .qq, items: [live, unknown]),
            .init(platform: .kugou, items: [longer])
        ])
        XCTAssertEqual(rows.count, 4)
        XCTAssertEqual(Set(rows.map(\.id)).count, 4)
        XCTAssertTrue(rows.allSatisfy { $0.variants.count == 1 })
        XCTAssertFalse(MusicSearchFusion.sameRecording(original, live))
        XCTAssertFalse(MusicSearchFusion.sameRecording(original, unknown))
    }
    func testDuplicateEditionDoesNotIncreasePlatformVoteAndExplicitRanksArePreserved() throws {
        let first = track("wy", "a"), edition = track("wy", "b")
        let qq = track("tx", "c", songID: "other", title: "Other")
        let rows = MusicSearchFusion.songs([
            .init(platform: .netease, items: [first, edition], ranks: ["wy:a": 31, "wy:b": 45]),
            .init(platform: .qq, items: [qq])
        ])
        XCTAssertEqual(rows.first?.id, "other")
        let merged = try XCTUnwrap(rows.first { $0.id == "same" })
        XCTAssertEqual(merged.matchedPlatforms, ["wy"])
        XCTAssertEqual(merged.score, 1.0 / 36, accuracy: 0.000001)
    }
    func testPlaylistsKeepPlatformIdentityAndFavoritesUseStableRecordingIdentity() throws {
        let a = MusicPublicBrowseCollection(platform: .netease, nativeID: "1", title: "Same title")
        let b = MusicPublicBrowseCollection(platform: .qq, nativeID: "1", title: "Same title")
        let playlists = MusicSearchFusion.playlists([.init(platform: .netease, items: [a, a]),
                                                    .init(platform: .qq, items: [b])])
        XCTAssertEqual(Set(playlists.map(\.id)), Set([a.id, b.id]))
        let song = track("wy", "a"), other = track("qq", "other", songID: "different", title: "Other")
        let row = try XCTUnwrap(MusicSearchFusion.songs([.init(platform: .netease, items: [song])]).first)
        let saved = MusicRemoteTrack(ref: .init(kind: .library, id: song.songId), songId: song.songId,
                                    title: song.title, artist: song.artist, duration: song.duration)
        XCTAssertTrue(MusicSearchFusion.isFavorite(row, members: [saved]))
        XCTAssertFalse(MusicSearchFusion.isFavorite(row, members: [other]))
    }
    func testLaterRoundAppendsNewRecordingsWithoutMovingDisplayedOrder() throws {
        let first = track("wy", "first", songID: "first", title: "First")
        let second = track("wy", "second", songID: "second", title: "Second")
        let displayed = MusicSearchFusion.songs([
            .init(platform: .netease, items: [first, second])
        ])
        let late = track("tx", "late", songID: "late", title: "Late")
        let appended = MusicSearchFusion.appendSongs(displayed: displayed, adding: [
            .init(platform: .qq, items: [late])
        ])
        XCTAssertEqual(Array(appended.prefix(2)).map(\.id), displayed.map(\.id))
        XCTAssertEqual(appended.last?.id, "late")
    }
    func testLaterRoundMergesNewSourceIntoShownRecordingWithoutChangingItsPosition() throws {
        let wy = track("wy", "a"), tx = track("tx", "b", songID: "same")
        let displayed = MusicSearchFusion.songs([
            .init(platform: .netease, items: [wy]),
            .init(platform: .qq, items: [track("tx", "other", songID: "other", title: "Other")])
        ])
        let position = try XCTUnwrap(displayed.firstIndex { $0.id == "same" })
        let appended = MusicSearchFusion.appendSongs(displayed: displayed, adding: [
            .init(platform: .qq, items: [tx])
        ])
        XCTAssertEqual(Array(appended.prefix(displayed.count)).map(\.id), displayed.map(\.id))
        XCTAssertEqual(appended[position].matchedPlatforms, ["tx", "wy"])
        XCTAssertTrue(appended[position].variants.contains(tx))
        XCTAssertEqual(appended[position].track, displayed[position].track)
        XCTAssertNotEqual(appended[position], displayed[position], "来源变化也是歌曲行的内容变化")
        XCTAssertEqual(MusicSearchFusion.appendSongs(displayed: appended, adding: []), appended)
    }
    func testLaterRoundDoesNotFoldIncompatibleRecordingIntoShownSongID() {
        let shown = track("wy", "a", title: "Song")
        let live = track("tx", "b", title: "Song (Live)")
        let displayed = MusicSearchFusion.songs([.init(platform: .netease, items: [shown])])
        let appended = MusicSearchFusion.appendSongs(displayed: displayed, adding: [
            .init(platform: .qq, items: [live])
        ])
        XCTAssertEqual(appended.count, 2)
        XCTAssertEqual(appended.first?.variants.count, 1)
        XCTAssertTrue(appended.contains { $0.variants.contains(live) })
        XCTAssertEqual(Set(appended.map(\.id)).count, 2)
    }
    func testCrossRoundAllPairsRuleRejectsNonTransitiveTimingMatch() throws {
        let middle = track("wy", "middle", duration: 180)
        let lower = track("tx", "lower", duration: 177)
        let upper = track("kg", "upper", duration: 183)
        let shown = MusicSearchFusion.songs([.init(platform: .netease, items: [middle]),
                                             .init(platform: .qq, items: [lower])])
        XCTAssertEqual(shown.count, 1)
        let later = MusicSearchFusion.appendSongs(displayed: shown, adding: [.init(platform: .kugou, items: [upper])])
        XCTAssertEqual(later.count, 2)
        XCTAssertEqual(later.first?.variants.count, 2)
        XCTAssertEqual(later.first?.id, shown.first?.id)
        XCTAssertTrue(later.last?.variants.contains(upper) == true)
    }
    func testExplicitPlatformOrderControlsDisplayAndEqualScoreButNotRecordingEvidence() throws {
        let wy = track("wy", "wy"), qq = track("tx", "qq")
        let order: [MusicPublicBrowsePlatform] = [.qq, .netease]
        let fused = MusicSearchFusion.songs([.init(platform: .netease, items: [wy]),
                                            .init(platform: .qq, items: [qq])], platformOrder: order)
        XCTAssertEqual(fused.first?.track.ref, qq.ref)
        let a = track("wy", "a", songID: "a", title: "A"), b = track("tx", "b", songID: "b", title: "B")
        let separate = MusicSearchFusion.songs([.init(platform: .netease, items: [a]),
                                                .init(platform: .qq, items: [b])], platformOrder: order)
        XCTAssertEqual(separate.map(\.id), ["b", "a"])
    }
    func testNativeMetadataEnrichmentWithDifferentDerivedSongIDStillUsesTheOneRecordingRule() {
        let original = track("wy", "same-native", songID: "old-id", title: "Song", artist: "未知歌手")
        let enriched = track("wy", "same-native", songID: "new-id", title: "Song", artist: "Artist")
        let shown = MusicSearchFusion.songs([.init(platform: .netease, items: [original])])
        let result = MusicSearchFusion.appendSongs(displayed: shown, adding: [.init(platform: .netease, items: [enriched])])
        XCTAssertEqual(result.count, 1)
        XCTAssertEqual(result.first?.id, "old-id")
        XCTAssertTrue(result.first?.variants.contains(enriched) == true)
        XCTAssertTrue(MusicSearchFusion.sameSearchTarget(original, enriched, platform: .netease))
        let conflict = track("wy", "same-native", songID: "conflicting-id", artist: "Different artist")
        let later = MusicSearchFusion.appendSongs(displayed: result, adding: [.init(platform: .netease, items: [conflict])])
        XCTAssertEqual(later.count, 2)
    }
    func testSameNativeIDComparesVersionArtistAndTimingButNotTitle() {
        let original = track("wy", "same-native")
        let retitled = track("wy", "same-native", title: "Different song")
        XCTAssertTrue(MusicSearchFusion.sameRecording(original, retitled))
        XCTAssertTrue(MusicSearchFusion.sameSearchTarget(original, retitled, platform: .netease))
        let live = track("wy", "same-native", title: "Song (Live)")
        XCTAssertFalse(MusicSearchFusion.sameRecording(original, live))
        let otherArtist = track("wy", "same-native", artist: "Different artist")
        XCTAssertFalse(MusicSearchFusion.sameRecording(original, otherArtist))
        let longer = track("wy", "same-native", duration: 240)
        XCTAssertFalse(MusicSearchFusion.sameRecording(original, longer))
    }
    func testLiveVersionTagsMatchWhenAdjacentToChineseText() {
        XCTAssertEqual(MusicRecordingIdentity(title: "现场live", artist: "A", duration: 180).version, "live")
        XCTAssertEqual(MusicRecordingIdentity(title: "Live版", artist: "A", duration: 180).version, "live")
        XCTAssertEqual(MusicRecordingIdentity(title: "现场", artist: "A", duration: 180).version, "live")
        XCTAssertEqual(MusicRecordingIdentity(title: "delivery", artist: "A", duration: 180).version, "")
        XCTAssertEqual(MusicRecordingIdentity(title: "lived", artist: "A", duration: 180).version, "")
        XCTAssertEqual(MusicRecordingIdentity(title: "Song (DJ版)", artist: "A", duration: 180).version, "remix")
        XCTAssertEqual(MusicRecordingIdentity(title: "Song (Cover版)", artist: "A", duration: 180).version, "cover")
        XCTAssertEqual(MusicRecordingIdentity(title: "discover", artist: "A", duration: 180).version, "")
    }
    func testFavoriteRequiresRecordingEvidenceEvenWhenSongIDMatches() throws {
        let original = track("wy", "a"), live = track("tx", "b", title: "Song (Live)")
        let row = try XCTUnwrap(MusicSearchFusion.songs([.init(platform: .netease, items: [original])]).first)
        XCTAssertFalse(MusicSearchFusion.isFavorite(row, members: [live]))
    }
}
