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
            observations: [.init(platform: "wy", page: .init(items: values), failure: nil)], now: MusicNativeCandidateDiscoveryFixture.date)
        let sources = try MusicModel.sources(result.song)
        XCTAssertEqual(sources.filter { $0.platform == "wy" }.map(\.nativeId), ["1"])
        XCTAssertEqual(result.song["songId"], seed["songId"])
        XCTAssertFalse(result.issues.contains { ["kw", "mg"].contains($0.platform) && $0.failure == .unsupported })
        XCTAssertTrue(result.song["partial"].bool)
    }
    func testAllPairsAvoidsTransitiveDurationMergeAndPlatformEditionsAreBoundedFairly() throws {
        let seed = try MusicNativeCandidateDiscoveryFixture.song()
        let a = MusicNativeCandidateDiscoveryFixture.track("wy", id: "1", duration: 177)
        let b = MusicNativeCandidateDiscoveryFixture.track("kg", id: String(repeating: "B", count: 32), duration: 183)
        let result = try MusicNativeCandidateDiscoveryMerge.merge(seed, observations: [
            .init(platform: "wy", page: .init(items: [a]), failure: nil),
            .init(platform: "kg", page: .init(items: [b]), failure: nil)
        ], now: MusicNativeCandidateDiscoveryFixture.date)
        XCTAssertEqual(try MusicModel.sources(result.song).count, 2)
        let editions = (1...30).map { MusicNativeCandidateDiscoveryFixture.track("wy", id: String($0)) }
        let fair = try MusicNativeCandidateDiscoveryMerge.merge(seed, observations: [
            .init(platform: "wy", page: .init(items: editions), failure: nil),
            .init(platform: "kg", page: .init(items: [MusicNativeCandidateDiscoveryFixture.track("kg", id: String(repeating: "C", count: 32))]), failure: nil)
        ], now: MusicNativeCandidateDiscoveryFixture.date)
        let sources = try MusicModel.sources(fair.song)
        XCTAssertEqual(sources.count, 16)
        XCTAssertTrue(sources.contains { $0.platform == "kg" })
        XCTAssertTrue(sources.contains { $0.platform == "tx" })
    }
    func testContradictoryKnownNativeIDIsQuarantinedAndCannotBeRenormalizedBack() throws {
        let seed = try MusicNativeCandidateDiscoveryFixture.song()
        let reused = MusicNativeCandidateDiscoveryFixture.track(artist: "Different recording")
        let result = try MusicNativeCandidateDiscoveryMerge.merge(seed,
            observations: [.init(platform: "tx", page: .init(items: [reused]), failure: nil)], now: MusicNativeCandidateDiscoveryFixture.date)
        XCTAssertTrue(result.song["sources"].array.isEmpty)
        XCTAssertEqual(result.song["quarantinedSources"].array.count, 1)
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
        ], now: MusicNativeCandidateDiscoveryFixture.date)
        XCTAssertFalse(result.song.json.contains("signature"))
        XCTAssertFalse(result.song.json.contains("synthetic-not"))
        XCTAssertFalse(result.song.json.contains("qualityVerified"))
        try MusicModel.rejectSecrets(result.song)
    }
    func testPartialBackoffForceAndDueTimeAreExplicit() throws {
        let now = MusicNativeCandidateDiscoveryFixture.date
        let original = try MusicNativeCandidateDiscoveryFixture.song()
        XCTAssertTrue(MusicNativeCandidateDiscoveryMerge.needsDiscovery(original, now: now, force: false))
        let partial = try MusicNativeCandidateDiscoveryMerge.merge(original,
            observations: [.init(platform: "wy", page: nil, failure: .timeout)], now: now).song
        XCTAssertFalse(MusicNativeCandidateDiscoveryMerge.needsDiscovery(partial, now: now.addingTimeInterval(1799), force: false))
        XCTAssertTrue(MusicNativeCandidateDiscoveryMerge.needsDiscovery(partial, now: now.addingTimeInterval(1800), force: false))
        XCTAssertTrue(MusicNativeCandidateDiscoveryMerge.needsDiscovery(partial, now: now, force: true))
        XCTAssertEqual(partial["sources"], original["sources"])
    }
    func testFreshNativeEvidenceSkipsOnlyItsPlatformAndForceBypassesFreshness() throws {
        let now = MusicNativeCandidateDiscoveryFixture.date
        let result = try MusicNativeCandidateDiscoveryMerge.merge(MusicNativeCandidateDiscoveryFixture.song(), observations: [
            .init(platform: "wy", page: .init(items: [MusicNativeCandidateDiscoveryFixture.track("wy", id: "123")]), failure: nil)
        ], now: now).song
        XCTAssertFalse(MusicNativeCandidateDiscoveryMerge.needsPlatform("wy", song: result, now: now.addingTimeInterval(1800), force: false))
        XCTAssertTrue(MusicNativeCandidateDiscoveryMerge.needsPlatform("tx", song: result, now: now, force: false))
        XCTAssertTrue(MusicNativeCandidateDiscoveryMerge.needsPlatform("wy", song: result, now: now, force: true))
        XCTAssertTrue(MusicNativeCandidateDiscoveryMerge.needsPlatform("wy", song: result, now: now.addingTimeInterval(7 * 86400), force: false))
    }

    func testFivePlatformSuccessIncludingEmptyResultsClosesFreshnessAndPromotesSevenFifteenThirtyDays() throws {
        let now = MusicNativeCandidateDiscoveryFixture.date
        let observations: [MusicNativeCandidateDiscoveryObservation] = MusicNativeSearchProtocol.platforms.map { platform in
            .init(platform: platform, page: .init(items: platform == "tx" ? [MusicNativeCandidateDiscoveryFixture.track()] : []), failure: nil)
        }
        let first = try MusicNativeCandidateDiscoveryMerge.merge(MusicNativeCandidateDiscoveryFixture.song(), observations: observations, now: now)
        XCTAssertFalse(first.song["partial"].bool); XCTAssertTrue(first.issues.isEmpty)
        XCTAssertEqual(first.song["stableCount"].int, 0)
        XCTAssertEqual(first.song["nextCheckAt"].number, now.addingTimeInterval(7 * 86400).timeIntervalSince1970 * 1000)
        for platform in MusicNativeSearchProtocol.platforms {
            XCTAssertFalse(MusicNativeCandidateDiscoveryMerge.needsPlatform(platform, song: first.song, now: now.addingTimeInterval(1), force: false))
            XCTAssertTrue(MusicNativeCandidateDiscoveryMerge.needsPlatform(platform, song: first.song, now: now, force: true))
        }
        let next = now.addingTimeInterval(7 * 86400)
        XCTAssertTrue(MusicNativeCandidateDiscoveryMerge.needsDiscovery(first.song, now: next, force: false))
        let second = try MusicNativeCandidateDiscoveryMerge.merge(first.song, observations: observations, now: next)
        XCTAssertEqual(second.song["stableCount"].int, 1)
        XCTAssertEqual(second.song["nextCheckAt"].number, next.addingTimeInterval(15 * 86400).timeIntervalSince1970 * 1000)
        let thirdAt = next.addingTimeInterval(15 * 86400)
        let third = try MusicNativeCandidateDiscoveryMerge.merge(second.song, observations: observations, now: thirdAt)
        XCTAssertEqual(third.song["stableCount"].int, 2)
        XCTAssertEqual(third.song["nextCheckAt"].number, thirdAt.addingTimeInterval(30 * 86400).timeIntervalSince1970 * 1000)
        XCTAssertEqual(third.song["songId"], first.song["songId"])
        XCTAssertEqual(third.song["sourcesRetryAfter"].number, 0)
    }

    func testFivePlatformFailureMissingObservationAndKnownIDAbsenceNeverPromoteFullFreshness() throws {
        let now = MusicNativeCandidateDiscoveryFixture.date
        let complete: [MusicNativeCandidateDiscoveryObservation] = MusicNativeSearchProtocol.platforms.map {
            .init(platform: $0, page: .init(items: $0 == "tx" ? [MusicNativeCandidateDiscoveryFixture.track()] : []), failure: nil)
        }
        let healthy = try MusicNativeCandidateDiscoveryMerge.merge(MusicNativeCandidateDiscoveryFixture.song(), observations: complete, now: now).song
        let due = now.addingTimeInterval(7 * 86400)
        let failed = complete.map { row -> MusicNativeCandidateDiscoveryObservation in
            row.platform == "mg" ? .init(platform: "mg", page: nil, failure: .rateLimited) : row
        }
        let failure = try MusicNativeCandidateDiscoveryMerge.merge(healthy, observations: failed, now: due)
        XCTAssertTrue(failure.song["partial"].bool); XCTAssertEqual(failure.song["stableCount"], healthy["stableCount"])
        XCTAssertEqual(failure.song["nextCheckAt"], healthy["nextCheckAt"])
        XCTAssertTrue(failure.issues.contains { $0.platform == "mg" && $0.failure == .rateLimited })
        XCTAssertFalse(MusicNativeCandidateDiscoveryMerge.needsDiscovery(failure.song, now: due.addingTimeInterval(1799), force: false))
        XCTAssertTrue(MusicNativeCandidateDiscoveryMerge.needsDiscovery(failure.song, now: due.addingTimeInterval(1800), force: false))
        let missing = try MusicNativeCandidateDiscoveryMerge.merge(healthy, observations: complete.filter { $0.platform != "kw" }, now: due)
        XCTAssertTrue(missing.song["partial"].bool); XCTAssertEqual(missing.song["nextCheckAt"], healthy["nextCheckAt"])
        let empty = complete.map { MusicNativeCandidateDiscoveryObservation(platform: $0.platform, page: .init(items: []), failure: nil) }
        let absent = try MusicNativeCandidateDiscoveryMerge.merge(healthy, observations: empty, now: due)
        XCTAssertTrue(absent.song["partial"].bool)
        XCTAssertEqual(try MusicModel.sources(absent.song).map(\.nativeId), ["SeedMID"], "old mapping is not silently deleted")
        XCTAssertEqual(absent.song["stableCount"], healthy["stableCount"])
        XCTAssertThrowsError(try MusicNativeCandidateDiscoveryMerge.merge(healthy, observations: complete + [complete[0]], now: due))
        XCTAssertThrowsError(try MusicNativeCandidateDiscoveryMerge.merge(healthy,
            observations: [.init(platform: "unknown", page: .init(items: []), failure: nil)], now: due))
    }

    func testFivePlatformsKeepFairSourceBudgetAndMiguGroupExpansionDoesNotHideMatchesAfterThirty() throws {
        let seed = try MusicNativeCandidateDiscoveryFixture.song(), now = MusicNativeCandidateDiscoveryFixture.date
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
        let result = try MusicNativeCandidateDiscoveryMerge.merge(seed, observations: observations, now: now)
        let sources = try MusicModel.sources(result.song)
        XCTAssertEqual(sources.count, 16)
        XCTAssertEqual(Set(sources.map(\.platform)), ["wy", "tx", "kg", "kw", "mg"])
        XCTAssertTrue(sources.contains { $0.platform == "mg" && $0.nativeId == "9999" })
        XCTAssertTrue(result.song["partial"].bool, "bounded source overflow cannot claim complete validation")
        XCTAssertTrue(result.issues.contains { $0.failure == .limit })
    }
}
