import XCTest
import MeowMusicFeature
@testable import MeowMusicNativeService

final class MusicNativeCandidateDiscoveryMergeTests: XCTestCase {
    func testStrictRecordingIdentityRejectsSingerVersionMissingDurationAndTimingMismatch() throws {
        let seed = try MusicNativeCandidateDiscoveryFixture.song()
        let values = [
            MusicNativeCandidateDiscoveryFixture.track("wy", id: "1"),
            MusicNativeCandidateDiscoveryFixture.track("wy", id: "2", artist: "Another"),
            MusicNativeCandidateDiscoveryFixture.track("wy", id: "3", title: "Synthetic recording (Live)"),
            MusicNativeCandidateDiscoveryFixture.track("wy", id: "4", duration: 0),
            MusicNativeCandidateDiscoveryFixture.track("wy", id: "5", duration: 190),
            MusicNativeCandidateDiscoveryFixture.track("wy", id: "6", title: "Synthetic recording (Remix)")
        ]
        let result = try MusicNativeCandidateDiscoveryMerge.merge(seed,
            observations: [.init(platform: "wy", page: .init(items: values), failure: nil)])
        let sources = try MusicModel.sources(result.song)
        XCTAssertEqual(sources.filter { $0.platform == "wy" }.map(\.nativeId), ["1"])
        XCTAssertEqual(result.song["songId"], seed["songId"])
        XCTAssertTrue(result.issues.isEmpty)
    }
    func testAllPairsAvoidsTransitiveDurationMergeAndPlatformEditionsAreBoundedFairly() throws {
        let seed = try MusicNativeCandidateDiscoveryFixture.song()
        let a = MusicNativeCandidateDiscoveryFixture.track("wy", id: "1", duration: 177)
        let b = MusicNativeCandidateDiscoveryFixture.track("kg", id: String(repeating: "B", count: 32), duration: 183)
        let result = try MusicNativeCandidateDiscoveryMerge.merge(seed, observations: [
            .init(platform: "wy", page: .init(items: [a]), failure: nil),
            .init(platform: "kg", page: .init(items: [b]), failure: nil)
        ])
        XCTAssertEqual(try MusicModel.sources(result.song).count, 2)
        let editions = (1...30).map { MusicNativeCandidateDiscoveryFixture.track("wy", id: String($0)) }
        let fair = try MusicNativeCandidateDiscoveryMerge.merge(seed, observations: [
            .init(platform: "wy", page: .init(items: editions), failure: nil),
            .init(platform: "kg", page: .init(items: [MusicNativeCandidateDiscoveryFixture.track("kg", id: String(repeating: "C", count: 32))]), failure: nil)
        ])
        let sources = try MusicModel.sources(fair.song)
        XCTAssertEqual(sources.count, 16)
        XCTAssertTrue(sources.contains { $0.platform == "kg" })
        XCTAssertTrue(sources.contains { $0.platform == "tx" })
    }
    func testContradictoryKnownNativeIDIsRejectedWithoutDurableQuarantineTable() throws {
        let seed = try MusicNativeCandidateDiscoveryFixture.song()
        let reused = MusicNativeCandidateDiscoveryFixture.track(artist: "Different recording")
        let result = try MusicNativeCandidateDiscoveryMerge.merge(seed,
            observations: [.init(platform: "tx", page: .init(items: [reused]), failure: nil)])
        XCTAssertTrue(result.song["sources"].array.isEmpty)
        XCTAssertTrue(try MusicModel.sources(MusicModel.cleanSong(result.song)).isEmpty)
        XCTAssertEqual(result.song["songId"], seed["songId"])
        XCTAssertTrue(result.issues.contains { $0.failure == .nativeIDConflict })
    }
    func testMetadataCleaningNeverCopiesSignedURLAccountFieldsOrPlaybackFacts() throws {
        var raw = try MJ.parse(MusicNativeCandidateDiscoveryFixture.track("wy", id: "123").data)
        raw["url"] = .string("https://media.invalid/a?signature=synthetic")
        raw["token"] = .string("synthetic-not-a-credential")
        raw["headers"] = .obj([("Cookie", .string("synthetic-not-a-cookie"))])
        let seed = try MusicNativeCandidateDiscoveryFixture.song()
        let result = try MusicNativeCandidateDiscoveryMerge.merge(seed, observations: [
            .init(platform: "wy", page: .init(items: [.init(platform: "wy", data: raw.bytes)]), failure: nil)
        ])
        XCTAssertFalse(result.song.json.contains("signature"))
        XCTAssertFalse(result.song.json.contains("synthetic-not"))
        XCTAssertFalse(result.song.json.contains("qualityVerified"))
        try MusicModel.rejectSecrets(result.song)
    }
    func testMissingPlatformsKeepDiscoveryEligibleAndForceNeverMarksUnobservedPlatforms() throws {
        let original = try MusicNativeCandidateDiscoveryFixture.song()
        XCTAssertTrue(MusicNativeCandidateDiscoveryMerge.needsDiscovery(original, force: false))
        let partial = try MusicNativeCandidateDiscoveryMerge.merge(original,
            observations: [.init(platform: "wy", page: nil, failure: .timeout)]).song
        XCTAssertTrue(MusicNativeCandidateDiscoveryMerge.needsDiscovery(partial, force: false))
        XCTAssertTrue(MusicNativeCandidateDiscoveryMerge.needsDiscovery(partial, force: true))
        XCTAssertEqual(partial["sources"], original["sources"])
    }
    func testCompleteSourceSkipsOnlyItsPlatformAndForceAllowsRefresh() throws {
        let result = try MusicNativeCandidateDiscoveryMerge.merge(MusicNativeCandidateDiscoveryFixture.song(), observations: [
            .init(platform: "wy", page: .init(items: [MusicNativeCandidateDiscoveryFixture.track("wy", id: "123")]), failure: nil)
        ]).song
        XCTAssertFalse(MusicNativeCandidateDiscoveryMerge.needsPlatform("wy", song: result, force: false))
        XCTAssertFalse(MusicNativeCandidateDiscoveryMerge.needsPlatform("tx", song: result, force: false))
        XCTAssertTrue(MusicNativeCandidateDiscoveryMerge.needsPlatform("mg", song: result, force: false))
        XCTAssertTrue(MusicNativeCandidateDiscoveryMerge.needsPlatform("wy", song: result, force: true))
        XCTAssertTrue(MusicNativeCandidateDiscoveryMerge.needsPlatform("tx", song: result, force: true))
        let cached = MusicSourceDiscoveryEntry(checkedAt: NativeMusicFixture.date.timeIntervalSince1970 * 1000, sources: [])
        XCTAssertFalse(MusicNativeCandidateDiscoveryMerge.needsPlatform("mg", song: result, force: false, cache: cached))
    }

    func testIncompleteMetadataNeedsSearchOnlyWithoutASuccessfulCacheCheck() throws {
        let original = try MusicNativeCandidateDiscoveryFixture.song()
        let cached = MusicSourceDiscoveryEntry(checkedAt: NativeMusicFixture.date.timeIntervalSince1970 * 1000, sources: [])
        for (field, value) in [
            ("name", MJ.string("")), ("name", .string("未知歌曲")), ("name", .string(" UNKNOWN ")),
            ("singer", .string("")), ("singer", .string("未知歌手")), ("singer", .string("Unknown artist")),
            ("duration", .n(0))
        ] {
            var song = original, rows = original["sources"].array
            rows[0]["musicInfo"][field] = value
            song["sources"] = .array(rows)
            XCTAssertTrue(MusicNativeCandidateDiscoveryMerge.needsPlatform("tx", song: song, force: false))
            XCTAssertFalse(MusicNativeCandidateDiscoveryMerge.needsPlatform("tx", song: song, force: false, cache: cached))
            XCTAssertTrue(MusicNativeCandidateDiscoveryMerge.needsPlatform("tx", song: song, force: true, cache: cached))
        }
    }

    func testEmptySuccessNeverInventsAnIDAndOnlyCacheCanSuppressMissingPlatformDiscovery() throws {
        let observations: [MusicNativeCandidateDiscoveryObservation] = MusicNativeSearchProtocol.platforms.map { platform in
            .init(platform: platform, page: .init(items: platform == "tx" ? [MusicNativeCandidateDiscoveryFixture.track()] : []), failure: nil)
        }
        let first = try MusicNativeCandidateDiscoveryMerge.merge(MusicNativeCandidateDiscoveryFixture.song(), observations: observations)
        XCTAssertTrue(first.issues.isEmpty)
        for platform in MusicNativeSearchProtocol.platforms {
            XCTAssertEqual(MusicNativeCandidateDiscoveryMerge.needsPlatform(platform, song: first.song, force: false),
                platform != "tx")
            XCTAssertTrue(MusicNativeCandidateDiscoveryMerge.needsPlatform(platform, song: first.song, force: true))
        }
        let cache = Dictionary(uniqueKeysWithValues: MusicNativeSearchProtocol.platforms.map {
            ($0, MusicSourceDiscoveryEntry(checkedAt: NativeMusicFixture.date.timeIntervalSince1970 * 1000, sources: []))
        })
        XCTAssertFalse(MusicNativeCandidateDiscoveryMerge.needsDiscovery(first.song, force: false, cache: cache))
        let second = try MusicNativeCandidateDiscoveryMerge.merge(first.song, observations: observations)
        XCTAssertEqual(second.song["songId"], first.song["songId"])
    }

    func testFailureMissingObservationAndEmptyPagePreserveKnownIDsAndMissingPlatformEligibility() throws {
        let complete: [MusicNativeCandidateDiscoveryObservation] = MusicNativeSearchProtocol.platforms.map {
            .init(platform: $0, page: .init(items: $0 == "tx" ? [MusicNativeCandidateDiscoveryFixture.track()] : []), failure: nil)
        }
        let healthy = try MusicNativeCandidateDiscoveryMerge.merge(MusicNativeCandidateDiscoveryFixture.song(), observations: complete).song
        let failed = complete.map { row -> MusicNativeCandidateDiscoveryObservation in
            row.platform == "mg" ? .init(platform: "mg", page: nil, failure: .rateLimited) : row
        }
        let failure = try MusicNativeCandidateDiscoveryMerge.merge(healthy, observations: failed)
        XCTAssertTrue(failure.issues.contains { $0.platform == "mg" && $0.failure == .rateLimited })
        XCTAssertTrue(MusicNativeCandidateDiscoveryMerge.needsDiscovery(failure.song, force: false))
        let missing = try MusicNativeCandidateDiscoveryMerge.merge(healthy, observations: complete.filter { $0.platform != "kw" })
        XCTAssertTrue(missing.issues.isEmpty)
        let empty = complete.map { MusicNativeCandidateDiscoveryObservation(platform: $0.platform, page: .init(items: []), failure: nil) }
        let absent = try MusicNativeCandidateDiscoveryMerge.merge(healthy, observations: empty)
        XCTAssertEqual(try MusicModel.sources(absent.song).map(\.nativeId), ["SeedMID"], "old mapping is not silently deleted")
        XCTAssertThrowsError(try MusicNativeCandidateDiscoveryMerge.merge(healthy, observations: complete + [complete[0]]))
        XCTAssertThrowsError(try MusicNativeCandidateDiscoveryMerge.merge(healthy,
            observations: [.init(platform: "unknown", page: .init(items: []), failure: nil)]))
    }

    func testFivePlatformsKeepFairSourceBudgetAndMiguGroupExpansionDoesNotHideMatchesAfterThirty() throws {
        let seed = try MusicNativeCandidateDiscoveryFixture.song()
        var mg: [MusicNativeTrackData] = (0..<40).map {
            MusicNativeCandidateDiscoveryFixture.track("mg", id: String(1000 + $0), title: "Unrelated \($0)")
        }
        mg.append(MusicNativeCandidateDiscoveryFixture.track("mg", id: "9999"))
        let observations: [MusicNativeCandidateDiscoveryObservation] = [
            .init(platform: "tx", page: .init(items: [MusicNativeCandidateDiscoveryFixture.track()]), failure: nil),
            .init(platform: "wy", page: .init(items: (1...30).map { MusicNativeCandidateDiscoveryFixture.track("wy", id: String($0)) }), failure: nil),
            .init(platform: "kg", page: .init(items: [MusicNativeCandidateDiscoveryFixture.track("kg", id: String(repeating: "A", count: 32))]), failure: nil),
            .init(platform: "kw", page: .init(items: [MusicNativeCandidateDiscoveryFixture.track("kw", id: "123")]), failure: nil),
            .init(platform: "mg", page: .init(items: mg), failure: nil)
        ]
        let result = try MusicNativeCandidateDiscoveryMerge.merge(seed, observations: observations)
        let sources = try MusicModel.sources(result.song)
        XCTAssertEqual(sources.count, 16)
        XCTAssertEqual(Set(sources.map(\.platform)), ["wy", "tx", "kg", "kw", "mg"])
        XCTAssertTrue(sources.contains { $0.platform == "mg" && $0.nativeId == "9999" })
        XCTAssertTrue(result.issues.contains { $0.failure == .limit })
    }
    func testManyEditionsOfOnePlatformDoNotCountAsOtherPlatforms() throws {
        var seed = try MusicModel.normalize(MusicNativeCandidateDiscoveryFixture.track("wy", id: "1"))
        for id in 2...16 {
            let variant = try MusicModel.normalize(MusicNativeCandidateDiscoveryFixture.track("wy", id: String(id)))
            seed = try MusicModel.mergeSources(seed, adding: variant["sources"].array)
        }
        XCTAssertEqual(try MusicModel.sources(seed).count, 16)
        XCTAssertTrue(MusicNativeCandidateDiscoveryMerge.needsDiscovery(seed, force: false))
        XCTAssertFalse(MusicNativeCandidateDiscoveryMerge.needsPlatform("wy", song: seed, force: false))
        XCTAssertTrue(MusicNativeCandidateDiscoveryMerge.needsPlatform("tx", song: seed, force: false))
    }
    func testPrimarySourceStaysFirstAndRemainingSourcesUseOrdinalKeyOrder() throws {
        let seed = try MusicModel.normalize(MusicNativeCandidateDiscoveryFixture.track("tx", id: "primary"))
        let wy = try MusicModel.normalize(MusicNativeCandidateDiscoveryFixture.track("wy", id: "9"))
        let mg = try MusicModel.normalize(MusicNativeCandidateDiscoveryFixture.track("mg", id: "1"))
        let kg = try MusicModel.normalize(MusicNativeCandidateDiscoveryFixture.track("kg", id: String(repeating: "B", count: 32)))
        let merged = try MusicModel.mergeSources(seed, adding: wy["sources"].array + mg["sources"].array + kg["sources"].array)
        XCTAssertEqual(merged["sources"].array.first?["source"].string, "tx")
        let keys = merged["sources"].array.dropFirst().map { $0["sourceKey"].string }
        XCTAssertEqual(keys, keys.sorted { $0.utf16.lexicographicallyPrecedes($1.utf16) })
    }
    func testIncompleteSameIDRefreshKeepsSingerAndDurationEvidenceForFutureConflicts() throws {
        let seed = try MusicNativeCandidateDiscoveryFixture.song()
        let incomplete = MusicNativeCandidateDiscoveryFixture.track(artist: "未知歌手", duration: 0)
        let updated = try MusicNativeCandidateDiscoveryMerge.merge(seed, observations: [
            .init(platform: "tx", page: .init(items: [incomplete]), failure: nil)
        ]).song
        let data = try XCTUnwrap(updated["sources"].array.first)["musicInfo"]
        XCTAssertEqual(data["singer"], seed["sources"].array.first?["musicInfo"]["singer"])
        XCTAssertEqual(data["duration"].number, 180)
        let conflict = try MusicNativeCandidateDiscoveryMerge.merge(updated, observations: [
            .init(platform: "tx", page: .init(items: [MusicNativeCandidateDiscoveryFixture.track(artist: "Other artist")]), failure: nil)
        ])
        XCTAssertTrue(conflict.song["sources"].array.isEmpty)
        XCTAssertTrue(conflict.issues.contains { $0.failure == .nativeIDConflict })
    }
    func testDiscoveryMergeKeepsTheSeedPrimaryRecordingFirst() throws {
        let seed = try MusicNativeCandidateDiscoveryFixture.song()
        let result = try MusicNativeCandidateDiscoveryMerge.merge(seed, observations: [
            .init(platform: "kg", page: .init(items: [MusicNativeCandidateDiscoveryFixture.track("kg", id: String(repeating: "B", count: 32))]), failure: nil),
            .init(platform: "wy", page: .init(items: [MusicNativeCandidateDiscoveryFixture.track("wy", id: "9")]), failure: nil)
        ])
        let rows = result.song["sources"].array
        XCTAssertEqual(rows.count, 3)
        XCTAssertEqual(rows.first?["sourceKey"], seed["sources"].array.first?["sourceKey"], "sources[0] defines the song")
        let rest = rows.dropFirst().map { $0["sourceKey"].string }
        XCTAssertEqual(rest, rest.sorted { $0.utf16.lexicographicallyPrecedes($1.utf16) })
    }
    func testExcludedSourceIsNeverProposedAgain() throws {
        let seed = try MusicNativeCandidateDiscoveryFixture.song()
        let rejected = try MusicModel.normalize(MusicNativeCandidateDiscoveryFixture.track("wy", id: "9"))
        let key = try XCTUnwrap(rejected["sources"].array.first)["sourceKey"].string
        let result = try MusicNativeCandidateDiscoveryMerge.merge(seed, observations: [
            .init(platform: "wy", page: .init(items: [MusicNativeCandidateDiscoveryFixture.track("wy", id: "9"),
                                                     MusicNativeCandidateDiscoveryFixture.track("wy", id: "10")]), failure: nil)
        ], excluded: [key])
        let keys = result.song["sources"].array.map { $0["sourceKey"].string }
        XCTAssertFalse(keys.contains(key))
        XCTAssertEqual(try MusicModel.sources(result.song).filter { $0.platform == "wy" }.map(\.nativeId), ["10"],
                       "only the confirmed mismatch is excluded, not the platform")
    }
}
