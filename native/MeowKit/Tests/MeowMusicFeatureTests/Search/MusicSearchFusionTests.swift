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
        let saved = MusicRemoteTrack(ref: .init(kind: .library, id: song.songId), songId: song.songId, title: song.title)
        XCTAssertTrue(MusicSearchFusion.isFavorite(row, members: [saved]))
        XCTAssertFalse(MusicSearchFusion.isFavorite(row, members: [other]))
    }
}
