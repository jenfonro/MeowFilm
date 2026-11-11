import Foundation
import XCTest
import MeowMusicFeature
import MeowStorage
@testable import MeowMusicNativeService

private actor SongMetadataQQ: MusicPublicQQProviding {
    struct Step: Sendable {
        var items: [MusicNativeTrackData] = []
        var failure: MusicServiceError?
        var gate: NativeMusicGate?
    }
    private var steps: [Step]
    private var count = 0
    init(_ steps: [Step]) { self.steps = steps }
    func search(query: String, page: Int, limit: Int) async throws -> MusicNativeTrackPage {
        count += 1
        let step = steps.count > 1 ? steps.removeFirst() : steps[0]
        if let gate = step.gate { await gate.wait() }
        if let failure = step.failure { throw failure }
        return .init(items: step.items, total: step.items.count)
    }
    func lyrics(mid: String) -> MusicNativeLyrics { .init(text: "") }
    func calls() -> Int { count }
}

final class MusicNativeSongMetadataTests: XCTestCase {
    private func native(_ platform: String = "tx", id: String = "mapped",
                        title: String = "Synthetic recording", artist: String = "Fixture",
                        duration: Int = 180, album: String = "Album") -> MusicNativeTrackData {
        let original = MusicNativeCandidateDiscoveryFixture.track(
            platform, id: id, title: title, artist: artist, duration: duration)
        var data = try! MJ.parse(original.data)
        data["albumName"] = .string(album)
        return .init(platform: platform, data: data.bytes)
    }
    private func song(_ platform: String = "wy", id: String = "1",
                      title: String = "Synthetic recording", duration: Int = 180) throws -> MJ {
        try MusicModel.normalize(native(platform, id: id, title: title, duration: duration))
    }
    private func source(_ song: MJ) -> MusicSourceDiscoverySource {
        let row = song["sources"].array[0]
        return .init(sourceKey: row["sourceKey"].string, source: row["source"].string, musicInfo: row["musicInfo"].json)
    }
    private func setup(_ qq: SongMetadataQQ, seed: MJ, clock: NativeMusicFixtureClock = .init(),
                       cache: (any MusicSourceDiscoveryCaching)? = nil,
                       scripts: (any MusicScriptResolving)? = nil,
                       enabledPlatforms: Set<MusicPublicBrowsePlatform> = [.netease, .qq]) async throws -> NativeMusicService {
        let service = NativeMusicService(document: try MusicNativeCandidateDiscoveryFixture.store(song: seed),
            scripts: scripts, qq: qq, sourceDiscovery: cache, enabledPlatforms: enabledPlatforms,
            transport: NativeMusicNoNetwork(), clock: { clock.now() })
        try await service.setAccess(available: true, generation: 1)
        return service
    }
    private func info(_ song: MJ, platform: String) throws -> MJ {
        try XCTUnwrap(song["sources"].array.first { $0["source"].string == platform })["musicInfo"]
    }

    func testSearchAndAcquiredPlaylistMetadataShareUpdatesInBothDirectionsWithoutRequests() async throws {
        let seed = try song(), qq = SongMetadataQQ([.init(items: [native()])])
        let service = try await setup(qq, seed: seed)
        let first = try await service.search(platform: "tx", query: "Synthetic", page: 1, limit: 30)
        let fromPlaylist = try await service.sharedSongMetadata(seed)
        XCTAssertEqual(Set(fromPlaylist["sources"].array.map { $0["source"].string }), ["wy", "tx"])
        let document = try await service.read()
        let qqSong = try await service.song(first.items[0].ref, document)
        let fromSearch = try await service.sharedSongMetadata(qqSong)
        XCTAssertEqual(Set(fromSearch["sources"].array.map { $0["source"].string }), ["wy", "tx"])
        let updated = try MusicModel.normalize(native(id: "new-edition"))
        try await service.retainSongMetadata([updated])
        let reread = try await service.sharedSongMetadata(seed)
        XCTAssertTrue(reread["sources"].array.contains { MusicModel.sourceData($0)["songmid"].string == "new-edition" })
        let calls = await qq.calls()
        XCTAssertEqual(calls, 1, "metadata reads and playlist adoption must never search")
        await service.close()
    }

    func testNormalizedNameAndArtistShareFactsButLiveCoverAndDurationRemainIsolated() async throws {
        let seed = try song(), qq = SongMetadataQQ([.init()])
        let service = try await setup(qq, seed: seed)
        let normalized = try MusicModel.normalize(native(title: "  Ｓｙｎｔｈｅｔｉｃ   recording ",
                                                         artist: " FIXTURE "))
        try await service.retainSongMetadata([normalized])
        let hit = try await service.sharedSongMetadata(seed)
        XCTAssertTrue(hit["sources"].array.contains { $0["source"].string == "tx" })
        for isolated in [
            try song(title: "Synthetic recording (Live)"),
            try song(title: "Synthetic recording (Cover)"),
            try song(duration: 300)
        ] {
            let result = try await service.sharedSongMetadata(isolated)
            XCTAssertFalse(result["sources"].array.contains { $0["source"].string == "tx" })
        }
        let calls = await qq.calls()
        XCTAssertEqual(calls, 0)
        await service.close()
    }

    func testAcquiredFactsHaveNoSyntheticCheckAndAreAvailableToTheOtherNativeEntrance() async throws {
        let seed = try song(), qq = SongMetadataQQ([.init()])
        let service = try await setup(qq, seed: seed)
        try await service.retainSongMetadata([MusicModel.normalize(native())])
        let state = try await service.sourceDiscoveryState(seed)
        XCTAssertEqual(state.entries["tx"]?.checkedAt, 0)
        XCTAssertEqual(state.entries["tx"]?.observations, [])
        XCTAssertFalse(MusicNativeCandidateDiscoveryMerge.needsPlatform("tx", song: seed,
            force: false, cache: state.current(window: MusicSourceDiscoveryWindow.playback)["tx"]))
        let calls = await qq.calls()
        XCTAssertEqual(calls, 0)
        await service.close()
    }

    func testBroadPageAbsenceDoesNotBecomeRecordingNotFoundButExactQueryDoes() async throws {
        let seed = try song(), qq = SongMetadataQQ([.init()])
        let clock = NativeMusicFixtureClock(), service = try await setup(qq, seed: seed, clock: clock)
        _ = try await service.search(platform: "tx", query: "Synthetic", page: 1, limit: 30)
        let broad = try await service.sourceDiscoveryState(seed)
        XCTAssertNil(broad.current(window: MusicSourceDiscoveryWindow.playback)["tx"])
        clock.advance(1)
        let exact = try await service.search(platform: "tx", query: "Synthetic recording Fixture", page: 1, limit: 30)
        let state = try await service.sourceDiscoveryState(seed)
        let empty = try XCTUnwrap(state.current(window: MusicSourceDiscoveryWindow.playback)["tx"])
        XCTAssertTrue(empty.sources.isEmpty)
        XCTAssertEqual(empty.observations.first?.outcome, .notFound)
        XCTAssertEqual(empty.observations.first?.query, exact.query)
        XCTAssertEqual(empty.observations.first?.context, "public")
        XCTAssertEqual(empty.checkedAt, exact.observation?.checkedAt)
        XCTAssertFalse(MusicNativeCandidateDiscoveryMerge.needsPlatform("tx", song: seed, force: false, cache: empty))
        let otherQuery = try await service.sourceDiscoveryState(seed, query: "different query")
        let otherAccount = try await service.sourceDiscoveryState(seed, context: "account:other")
        XCTAssertNil(otherQuery.current(window: MusicSourceDiscoveryWindow.playback)["tx"])
        XCTAssertNil(otherAccount.current(window: MusicSourceDiscoveryWindow.playback)["tx"])
        await service.close()
    }

    func testFailureDoesNotCreateNegativeOrRenewEarlierSuccessfulCheck() async throws {
        let seed = try song(), clock = NativeMusicFixtureClock()
        let qq = SongMetadataQQ([.init(), .init(failure: .timeout)])
        let service = try await setup(qq, seed: seed, clock: clock)
        let page = try await service.search(platform: "tx", query: "Synthetic recording Fixture", page: 1, limit: 30)
        clock.advance(60)
        do {
            _ = try await service.search(platform: "tx", query: page.query, page: 1, limit: 30)
            XCTFail("Timeout is a failed request")
        } catch { XCTAssertEqual(error as? MusicServiceError, .timeout) }
        let state = try await service.sourceDiscoveryState(seed)
        let entry = try XCTUnwrap(state.entries["tx"])
        XCTAssertEqual(entry.checkedAt, page.observation?.checkedAt)
        XCTAssertEqual(entry.observations.filter { $0.outcome == .notFound }.count, 1)
        XCTAssertTrue(entry.observations.contains { $0.outcome == .failed && $0.failureCode == MusicServiceError.timeout.rawValue })
        clock.advance(24 * 60 * 60)
        let expired = try await service.sourceDiscoveryState(seed)
        XCTAssertNil(expired.current(window: MusicSourceDiscoveryWindow.playback)["tx"])
        await service.close()
    }

    func testFailureWithoutEarlierCheckRemainsRetryable() async throws {
        let seed = try song(), qq = SongMetadataQQ([.init(failure: .rateLimited)])
        let service = try await setup(qq, seed: seed)
        do {
            _ = try await service.search(platform: "tx", query: "Synthetic recording Fixture", page: 1, limit: 30)
            XCTFail("Rate limiting is not empty success")
        } catch { XCTAssertEqual(error as? MusicServiceError, .rateLimited) }
        let state = try await service.sourceDiscoveryState(seed)
        XCTAssertNil(state.current(window: MusicSourceDiscoveryWindow.playback)["tx"])
        XCTAssertEqual(state.entries["tx"]?.observations.first?.outcome, .failed)
        XCTAssertEqual(state.entries["tx"]?.checkedAt, 0)
        await service.close()
    }

    func testCancelledEmptyResponseDoesNotWriteNegative() async throws {
        let seed = try song(), gate = NativeMusicGate(), qq = SongMetadataQQ([.init(gate: gate)])
        let service = try await setup(qq, seed: seed)
        let search = Task { try await service.search(platform: "tx",
            query: "Synthetic recording Fixture", page: 1, limit: 30) }
        await gate.started()
        search.cancel(); await gate.release()
        do { _ = try await search.value; XCTFail("Cancelled search must not publish") }
        catch { XCTAssertEqual(error as? MusicServiceError, .cancelled) }
        let state = try await service.sourceDiscoveryState(seed)
        XCTAssertNil(state.entries["tx"])
        await service.close()
    }

    func testLateOldEmptyResponseCannotOverwriteNewFoundEvidenceEvenAtSameClockTime() async throws {
        let seed = try song(), gate = NativeMusicGate()
        let qq = SongMetadataQQ([.init(gate: gate), .init(items: [native(album: "Newest")])])
        let service = try await setup(qq, seed: seed)
        let old = Task { try await service.search(platform: "tx", query: "Synthetic recording Fixture", page: 1, limit: 30) }
        await gate.started()
        let newest = try await service.search(platform: "tx", query: "Synthetic recording Fixture", page: 1, limit: 30)
        await gate.release()
        _ = try await old.value
        let state = try await service.sourceDiscoveryState(seed)
        let evidence = try XCTUnwrap(state.entries["tx"])
        XCTAssertTrue(evidence.observations.contains {
            $0.outcome == .found && $0.requestID == newest.observation?.requestID
        })
        XCTAssertFalse(evidence.observations.contains { $0.outcome == .notFound })
        let merged = try await service.sharedSongMetadata(seed)
        XCTAssertEqual(try info(merged, platform: "tx")["albumName"].string, "Newest")
        await service.close()
    }

    func testLateOldFoundResponseCannotRollBackNativeMetadata() async throws {
        let seed = try song(), gate = NativeMusicGate()
        var oldData = try MJ.parse(native(album: "Old").data)
        oldData["qqSongId"] = .n(123)
        let oldTrack = MusicNativeTrackData(platform: "tx", data: oldData.bytes)
        let qq = SongMetadataQQ([.init(items: [oldTrack], gate: gate),
                                 .init(items: [native(album: "New")])])
        let service = try await setup(qq, seed: seed)
        let old = Task { try await service.search(platform: "tx", query: "Synthetic recording Fixture", page: 1, limit: 30) }
        await gate.started()
        _ = try await service.search(platform: "tx", query: "Synthetic recording Fixture", page: 1, limit: 30)
        await gate.release()
        let oldPage = try await old.value
        let document = try await service.read()
        let displayed = try await service.song(oldPage.items[0].ref, document)
        XCTAssertEqual(try info(displayed, platform: "tx")["albumName"].string, "New",
                       "search publishes the owner projection, not a second raw import")
        XCTAssertEqual(try info(displayed, platform: "tx")["qqSongId"], .null)
        let reread = try await service.sharedSongMetadata(displayed)
        XCTAssertEqual(try info(reread, platform: "tx")["qqSongId"], .null)
        let result = try await service.sharedSongMetadata(seed)
        XCTAssertEqual(try info(result, platform: "tx")["albumName"].string, "New")
        XCTAssertEqual(try info(result, platform: "tx")["qqSongId"], .null,
                       "rejected old raw rows cannot sneak back through metadata import")
        await service.close()
    }

    func testNewQueryRemovesOptionalFieldsAndOldHandleCannotReimportThem() async throws {
        let seed = try song()
        var oldData = try MJ.parse(native(album: "Old").data)
        oldData["qqSongId"] = .n(123)
        let oldTrack = MusicNativeTrackData(platform: "tx", data: oldData.bytes)
        let qq = SongMetadataQQ([.init(items: [oldTrack]), .init(items: [native(album: "New")])])
        let service = try await setup(qq, seed: seed)
        let old = try await service.search(platform: "tx", query: "Synthetic recording Fixture", page: 1, limit: 30)
        let oldDocument = try await service.read()
        let oldSnapshot = try await service.song(old.items[0].ref, oldDocument)
        XCTAssertEqual(try info(oldSnapshot, platform: "tx")["qqSongId"], .n(123))
        let fresh = try await service.search(platform: "tx", query: old.query, page: 1, limit: 30)
        let state = try await service.sourceDiscoveryState(seed)
        let current = try XCTUnwrap(state.entries["tx"])
        let currentInfo = try MJ.parse(Data(XCTUnwrap(current.sources.first).musicInfo.utf8))
        XCTAssertEqual(currentInfo["albumName"].string, "New")
        XCTAssertEqual(currentInfo["qqSongId"], .null, "real query updates do not fill from earlier query rows")
        XCTAssertEqual(current.checkedAt, fresh.observation?.checkedAt)

        // The live handle still owns its old display snapshot, not the current facts.
        let request = MusicSearchBatchRequest(query: old.query, targets: [
            .init(platform: .qq, page: 1, track: old.items[0])
        ])
        let reread = try await service.searchRecording(request)
        let document = try await service.read()
        let projected = try await service.song(reread.ref, document)
        XCTAssertEqual(projected["songId"], oldSnapshot["songId"])
        XCTAssertEqual(projected["sources"].array.first?["sourceKey"], oldSnapshot["sources"].array.first?["sourceKey"])
        XCTAssertEqual(try info(projected, platform: "tx")["albumName"].string, "New")
        XCTAssertEqual(try info(projected, platform: "tx")["qqSongId"], .null)

        let imported = try await service.sharedSongMetadata(oldSnapshot)
        XCTAssertEqual(try info(imported, platform: "tx")["qqSongId"], .null,
                       "snapshot imports must not enrich a row already refreshed by a query")
        try await service.retainSongMetadata([MusicModel.normalize(native(id: "new-edition"))])
        let withEdition = try await service.sharedSongMetadata(oldSnapshot)
        XCTAssertEqual(withEdition["sources"].array.first?["sourceKey"], oldSnapshot["sources"].array.first?["sourceKey"])
        XCTAssertTrue(withEdition["sources"].array.contains {
            $0["source"].string == "tx" && MusicModel.sourceData($0)["songmid"].string == "new-edition"
        })
        let owner = try await service.sourceDiscoveryState(seed)
        let originalKey = try XCTUnwrap(oldSnapshot["sources"].array.first)["sourceKey"].string
        let refreshed = try XCTUnwrap(owner.entries["tx"]?.sources.first { $0.sourceKey == originalKey })
        XCTAssertEqual(try MJ.parse(Data(refreshed.musicInfo.utf8))["qqSongId"], .null)
        XCTAssertEqual(owner.entries["tx"]?.checkedAt, fresh.observation?.checkedAt)
        let calls = await qq.calls()
        XCTAssertEqual(calls, 2, "old handles and metadata reads do not issue another search")
        await service.close()
    }

    func testAcquiredRowsCanSupplementUntilThatSourceIsObservedByAQuery() async throws {
        let seed = try song(), qq = SongMetadataQQ([.init(items: [native()])])
        let service = try await setup(qq, seed: seed)
        let acquired = native(id: "imported")
        try await service.retainSongMetadata([MusicModel.normalize(acquired)])
        _ = try await service.search(platform: "tx", query: "Synthetic recording Fixture", page: 1, limit: 30)
        var enriched = try MJ.parse(acquired.data)
        enriched["qqSongId"] = .n(456)
        let imported = try MusicModel.normalize(.init(platform: "tx", data: enriched.bytes))
        try await service.retainSongMetadata([imported])
        let shared = try await service.sharedSongMetadata(seed)
        let row = try XCTUnwrap(shared["sources"].array.first {
            MusicModel.sourceData($0)["songmid"].string == "imported"
        })
        XCTAssertEqual(row["musicInfo"]["qqSongId"], .n(456),
                       "a query for another source key must not block acquired metadata supplementation")
        XCTAssertEqual(shared["sources"].array.first?["sourceKey"], seed["sources"].array.first?["sourceKey"])
        let calls = await qq.calls()
        XCTAssertEqual(calls, 1)
        await service.close()
    }

    func testRetryingDiskLoadCannotPreferAnOlderRequestsLateCompletionTime() async throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: root) }
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        let document = try DocumentStore(baseURL: root, namespace: MusicNativePersistentSourceDiscoveryCache.namespace)
        let seed = try song(), fingerprint = MusicModel.fingerprint(seed)
        let at = NativeMusicFixture.date.timeIntervalSince1970 * 1000
        let old = MusicSearchObservation(startedAt: at - 200, order: 1, checkedAt: at - 10,
            platform: "tx", query: "Synthetic recording Fixture", scope: .recording, outcome: .found)
        var oldData = try MJ.parse(native(album: "Old").data)
        oldData["qqSongId"] = .n(123)
        let oldSong = try MusicModel.normalize(.init(platform: "tx", data: oldData.bytes))
        let initial = MusicNativePersistentSourceDiscoveryCache(storeFactory: { document }, clock: { NativeMusicFixture.date })
        await initial.observe(fingerprint: fingerprint, duration: 180, observation: old,
                              sources: [source(oldSong)])
        await initial.persist(retaining: [.init(fingerprint: fingerprint, duration: 180)])
        let file = root.appendingPathComponent(MusicNativePersistentSourceDiscoveryCache.namespace)
            .appendingPathComponent("records.json")
        XCTAssertTrue(FileManager.default.fileExists(atPath: file.path))
        XCTAssertNotNil(try document.read(key: "records"))
        let alias = root.appendingPathComponent("unreadable-records.json")
        try FileManager.default.linkItem(at: file, to: alias)
        let cache = MusicNativePersistentSourceDiscoveryCache(storeFactory: { document }, clock: { NativeMusicFixture.date })
        let newer = MusicSearchObservation(startedAt: at - 100, order: 2, checkedAt: at - 50,
            platform: "tx", query: old.query, scope: .recording, outcome: .found)
        await cache.observe(fingerprint: fingerprint, duration: 180, observation: newer,
                            sources: [source(try MusicModel.normalize(native(album: "New")))])
        try FileManager.default.removeItem(at: alias)
        let entries = await cache.entries(fingerprint: fingerprint, duration: 180)
        let entry = try XCTUnwrap(entries["tx"])
        XCTAssertEqual(entry.checkedAt, newer.checkedAt)
        XCTAssertEqual(entry.observations.map(\.requestID), [newer.requestID])
        let info = try MJ.parse(Data(XCTUnwrap(entry.sources.first).musicInfo.utf8))
        XCTAssertEqual(info["albumName"].string, "New")
        XCTAssertEqual(info["qqSongId"], .null, "late disk rows cannot fill fields removed by the newer query")
        await cache.persist(retaining: [.init(fingerprint: fingerprint, duration: 180)])
        let reopened = MusicNativePersistentSourceDiscoveryCache(storeFactory: { document }, clock: { NativeMusicFixture.date })
        await reopened.include([.init(recording: .init(fingerprint: fingerprint, duration: 180),
                                      platform: "tx", sources: [source(oldSong)])])
        let restored = await reopened.entries(fingerprint: fingerprint, duration: 180)
        let restoredEntry = try XCTUnwrap(restored["tx"])
        let restoredInfo = try MJ.parse(Data(XCTUnwrap(restoredEntry.sources.first).musicInfo.utf8))
        XCTAssertEqual(restoredInfo["albumName"].string, "New")
        XCTAssertEqual(restoredInfo["qqSongId"], .null, "reopening must retain the query-versus-import distinction")
        XCTAssertEqual(restoredEntry.checkedAt, newer.checkedAt)
    }

    func testNewSuccessRefreshesNegativeAndCachedEmptyDoesNotEraseIt() async throws {
        let seed = try song(), clock = NativeMusicFixtureClock()
        let qq = SongMetadataQQ([.init(), .init(items: [native(album: "Current")])])
        let service = try await setup(qq, seed: seed, clock: clock)
        let empty = try await service.search(platform: "tx", query: "Synthetic recording Fixture", page: 1, limit: 30)
        clock.advance(60)
        let found = try await service.search(platform: "tx", query: empty.query, page: 1, limit: 30)
        try await service.observeSearchPages([empty], references: [.init(kind: .queue, id: "entry-1")])
        let state = try await service.sourceDiscoveryState(seed)
        let entry = try XCTUnwrap(state.entries["tx"])
        XCTAssertEqual(entry.checkedAt, found.observation?.checkedAt)
        XCTAssertTrue(entry.observations.contains { $0.outcome == .found })
        XCTAssertFalse(entry.observations.contains { $0.outcome == .notFound })
        let shared = try await service.sharedSongMetadata(seed)
        XCTAssertEqual(try info(shared, platform: "tx")["albumName"].string, "Current")
        let calls = await qq.calls()
        XCTAssertEqual(calls, 2)
        await service.close()
    }

    func testCachedObservationUsesOriginalTimeAndCannotReplaceNewerSuccess() async throws {
        let seed = try song(), clock = NativeMusicFixtureClock()
        let qq = SongMetadataQQ([.init(), .init(items: [native()])])
        let service = try await setup(qq, seed: seed, clock: clock)
        let old = try await service.search(platform: "tx", query: "Synthetic recording Fixture", page: 1, limit: 30)
        clock.advance(60)
        try await service.observeSearchPages([old], references: [.init(kind: .queue, id: "entry-1")])
        let reused = try await service.sourceDiscoveryState(seed)
        XCTAssertEqual(reused.entries["tx"]?.checkedAt, old.observation?.checkedAt)
        let fresh = try await service.search(platform: "tx", query: old.query, page: 1, limit: 30)
        clock.advance(60)
        try await service.observeSearchPages([old], references: [.init(kind: .queue, id: "entry-1")])
        let current = try await service.sourceDiscoveryState(seed)
        XCTAssertEqual(current.entries["tx"]?.checkedAt, fresh.observation?.checkedAt)
        XCTAssertFalse(current.entries["tx"]?.sources.isEmpty ?? true)
        let calls = await qq.calls()
        XCTAssertEqual(calls, 2)
        await service.close()
    }

    func testQueryPageCanBindLaterNativeRecordingWithoutInventingFactsFromDTO() async throws {
        let seed = try song(title: "Unrelated"), target = try song()
        let clock = NativeMusicFixtureClock(), qq = SongMetadataQQ([.init()])
        let service = try await setup(qq, seed: seed, clock: clock)
        let page = try await service.search(platform: "tx", query: "Synthetic recording Fixture", page: 1, limit: 30)
        clock.advance(60)
        let ref = try await service.remember(target).ref
        try await service.observeSearchPages([page], references: [ref])
        let state = try await service.sourceDiscoveryState(target)
        XCTAssertEqual(state.entries["tx"]?.observations.first?.outcome, .notFound)
        XCTAssertEqual(state.entries["tx"]?.checkedAt, page.observation?.checkedAt)
        let calls = await qq.calls()
        XCTAssertEqual(calls, 1)
        await service.close()
    }

    func testLibraryReadImportsItsKnownSourcesAndProjectsNewSearchMapping() async throws {
        let seed = try song()
        var document = try MusicDocument(nil, runID: "metadata")
        document.library["library"]["songs"] = .obj([(seed["songId"].string, seed)])
        var favorites = document.library["library"]["collections"].array[0]
        favorites["order"] = .strings([seed["songId"].string])
        document.library["library"]["collections"] = .array([favorites])
        let store = NativeMusicMemoryDocument(try document.encode(changed: ["library"]))
        let qq = SongMetadataQQ([.init(items: [native()])])
        let service = try await NativeMusicFixture.service(store, qq: qq)
        _ = try await service.search(platform: "tx", query: "Synthetic", page: 1, limit: 30)
        let collections = try await service.collections()
        let collection = try XCTUnwrap(collections.first)
        let tracks = try await service.tracks(in: collection)
        XCTAssertEqual(Set(tracks[0].sources.map(\.platform)), ["wy", "tx"])
        XCTAssertEqual(store.snapshot().2, 0, "metadata reads do not rewrite membership or the protected document")
        await service.close()
    }

    func testSearchBatchReadsFreshSharedMappingsWithoutRenewingLiveOrigins() async throws {
        let seed = try song(), qq = SongMetadataQQ([.init(items: [native()])])
        let service = try await setup(qq, seed: seed)
        let page = try await service.search(platform: "tx", query: "Synthetic", page: 1, limit: 30)
        let request = MusicSearchBatchRequest(query: page.query, targets: [
            .init(platform: .qq, page: 1, track: page.items[0])
        ])
        try await service.retainSongMetadata([song(id: "later")])
        let recording = try await service.searchRecording(request)
        XCTAssertTrue(recording.sources.contains { $0.platform == "wy" && $0.nativeId == "later" })
        _ = try await service.replaceSearchBatch(request, expectedRevision: 1, intent: .init())
        let document = try await service.read()
        let saved = document.queue["entries"].array[0]["song"]
        XCTAssertTrue(saved["sources"].array.contains { MusicModel.sourceData($0)["songmid"].string == "later" })
        let calls = await qq.calls()
        XCTAssertEqual(calls, 1, "batch adoption never discovers unrelated platforms")
        await service.close()
    }

    func testPersistenceRetainsOnlyLocalMembershipAndOriginalQueryEvidence() async throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: root) }
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        let document = try DocumentStore(baseURL: root, namespace: MusicNativePersistentSourceDiscoveryCache.namespace)
        let cache = MusicNativePersistentSourceDiscoveryCache(storeFactory: { document }, clock: { NativeMusicFixture.date })
        let seed = try song(), qq = SongMetadataQQ([.init(items: [native()])])
        let service = try await setup(qq, seed: seed, cache: cache)
        let page = try await service.search(platform: "tx", query: "Synthetic recording Fixture", page: 1, limit: 30)
        await service.flushSourceDiscoveryPersist()
        XCTAssertNil(try document.read(key: "records"), "queue and search alone remain memory-only")
        let playlist = try await service.createPlaylist(name: "Saved", refs: [.init(kind: .queue, id: "entry-1")])
        await service.flushSourceDiscoveryPersist()
        let file = root.appendingPathComponent(MusicNativePersistentSourceDiscoveryCache.namespace)
            .appendingPathComponent("records.json")
        XCTAssertTrue(FileManager.default.fileExists(atPath: file.path))
        XCTAssertNotNil(try document.read(key: "records"))
        let reopened = MusicNativePersistentSourceDiscoveryCache(storeFactory: { document }, clock: { NativeMusicFixture.date })
        let saved = await reopened.entries(fingerprint: MusicModel.fingerprint(seed), duration: 180)
        XCTAssertEqual(saved["tx"]?.observations.first?.checkedAt, page.observation?.checkedAt)
        XCTAssertEqual(saved["wy"]?.checkedAt, 0)
        try await service.removePlaylist(playlist)
        await service.flushSourceDiscoveryPersist()
        let empty = MusicNativePersistentSourceDiscoveryCache(storeFactory: { document }, clock: { NativeMusicFixture.date })
        let removed = await empty.entries(fingerprint: MusicModel.fingerprint(seed), duration: 180)
        XCTAssertTrue(removed.isEmpty)
        await service.close()
    }

    func testExcludedRealFoundKeepsEvidenceAndOnlyForceOrExpiredChecksSearchAgain() async throws {
        for force in [false, true] {
            let seed = try song(), clock = NativeMusicFixtureClock()
            let cache = MusicNativeMemorySourceDiscoveryCache(clock: { clock.now() })
            let qq = SongMetadataQQ([.init(items: [native()])])
            let scripts = MusicNativeCandidateDiscoveryScripts()
            await scripts.supported(["tx"])
            let service = try await setup(qq, seed: seed, clock: clock, cache: cache,
                                          scripts: scripts, enabledPlatforms: [.qq])
            let page = try await service.search(platform: "tx", query: "Synthetic recording Fixture", page: 1, limit: 30)
            let recorded = await cache.entries(fingerprint: MusicModel.fingerprint(seed), duration: 180)
            let original = try XCTUnwrap(recorded["tx"])
            let key = try XCTUnwrap(original.sources.first).sourceKey
            XCTAssertFalse(original.observations.isEmpty)
            XCTAssertTrue(original.observations.allSatisfy {
                $0.outcome == .found && $0.query == page.query && $0.context == "public"
            })
            try await service.excludeRecordingSource(seed, sourceKey: key)
            let state = try await service.sourceDiscoveryState(seed)
            XCTAssertNil(state.current(window: MusicSourceDiscoveryWindow.playback)["tx"])
            XCTAssertEqual(state.checks["tx"]?.checkedAt, original.checkedAt)
            XCTAssertEqual(state.checks["tx"]?.observations, original.observations)
            let projected = try await service.sharedSongMetadata(seed)
            XCTAssertFalse(projected["sources"].array.contains { $0["sourceKey"].string == key })

            let session = try await service.beginCandidateDiscovery(occurrenceID: "entry-1", expectedQueueRevision: 1)
            let ordinary = try await session.snapshot(discover: true, force: false)
            XCTAssertFalse(ordinary.track.sources.contains { $0.sourceKey == key })
            let calls = await qq.calls()
            XCTAssertEqual(calls, 1, "Filtering the only found row must not trigger an ordinary repeat query")
            let unchanged = await cache.entries(fingerprint: MusicModel.fingerprint(seed), duration: 180)
            XCTAssertEqual(unchanged["tx"], original, "Read projection cannot rewrite query time, outcome or native rows")

            if !force { clock.advance(24 * 60 * 60) }
            let retry = try await session.snapshot(discover: true, force: force)
            XCTAssertFalse(retry.track.sources.contains { $0.sourceKey == key })
            let retriedCalls = await qq.calls()
            XCTAssertEqual(retriedCalls, 2, "Force and the existing 24-hour empty-source window still permit search")
            await session.close(retainingAttemptID: nil)
            await service.close()
        }
    }

    func testFilteredFoundChecksKeepOriginalContextAndThirtyDaySyncWindow() async throws {
        let seed = try song(), clock = NativeMusicFixtureClock()
        let cache = MusicNativeMemorySourceDiscoveryCache(clock: { clock.now() })
        let at = clock.now().timeIntervalSince1970 * 1000
        let found = MusicSearchObservation(startedAt: at, checkedAt: at, platform: "tx",
            query: "Synthetic recording Fixture", context: "public", scope: .recording, outcome: .found)
        let nativeSource = source(try MusicModel.normalize(native()))
        await cache.observe(fingerprint: MusicModel.fingerprint(seed), duration: 180,
                            observation: found, sources: [nativeSource])
        let qq = SongMetadataQQ([.init()])
        let service = try await setup(qq, seed: seed, clock: clock, cache: cache)
        try await service.excludeRecordingSource(seed, sourceKey: nativeSource.sourceKey)
        clock.advance(25 * 24 * 60 * 60)
        let state = try await service.sourceDiscoveryState(seed)
        XCTAssertNil(state.current(window: MusicSourceDiscoveryWindow.librarySync)["tx"])
        XCTAssertFalse(state.hasCurrentCheck("tx", window: MusicSourceDiscoveryWindow.playback))
        XCTAssertTrue(state.hasCurrentCheck("tx", window: MusicSourceDiscoveryWindow.librarySync))
        XCTAssertEqual(state.checks["tx"]?.observations, [found])
        XCTAssertEqual(state.checks["tx"]?.checkedAt, at)
        let otherContext = try await service.sourceDiscoveryState(seed, context: "another-account")
        XCTAssertFalse(otherContext.hasCurrentCheck("tx", window: MusicSourceDiscoveryWindow.librarySync))
        clock.advance(6 * 24 * 60 * 60)
        let expired = try await service.sourceDiscoveryState(seed)
        XCTAssertFalse(expired.hasCurrentCheck("tx", window: MusicSourceDiscoveryWindow.librarySync))
        XCTAssertEqual(expired.checks["tx"]?.observations, [found])
        XCTAssertEqual(expired.checks["tx"]?.checkedAt, at)
        let calls = await qq.calls()
        XCTAssertEqual(calls, 0, "Checking the original evidence must not refresh it")
        await service.close()
    }

    func testUnqueriedFailedCancelledAndBroadMissingEvidenceDoNotSuppressDedicatedDiscovery() async throws {
        for kind in ["acquired", "failed", "cancelled", "broad"] {
            let seed = try song(), clock = NativeMusicFixtureClock()
            let cache = MusicNativeMemorySourceDiscoveryCache(clock: { clock.now() })
            var steps: [SongMetadataQQ.Step] = [.init(items: [native()])]
            switch kind {
            case "failed": steps.insert(.init(failure: .timeout), at: 0)
            case "cancelled": steps.insert(.init(failure: .cancelled), at: 0)
            case "broad": steps.insert(.init(), at: 0)
            default: break
            }
            let qq = SongMetadataQQ(steps), scripts = MusicNativeCandidateDiscoveryScripts()
            await scripts.supported(["tx"])
            let service = try await setup(qq, seed: seed, clock: clock, cache: cache,
                                          scripts: scripts, enabledPlatforms: [.qq])
            if kind == "acquired" {
                let acquired = try MusicModel.normalize(native())
                try await service.retainSongMetadata([acquired])
                try await service.excludeRecordingSource(seed, sourceKey: source(acquired).sourceKey)
                let stored = await cache.entries(fingerprint: MusicModel.fingerprint(seed), duration: 180)
                XCTAssertEqual(stored["tx"]?.checkedAt, 0)
                XCTAssertEqual(stored["tx"]?.observations, [])
            } else {
                do {
                    _ = try await service.search(platform: "tx",
                        query: kind == "broad" ? "Synthetic" : "Synthetic recording Fixture", page: 1, limit: 30)
                    XCTAssertEqual(kind, "broad", "The injected failed or cancelled query must fail")
                } catch {
                    XCTAssertNotEqual(kind, "broad")
                    XCTAssertEqual(error as? MusicServiceError, kind == "failed" ? .timeout : .cancelled)
                }
            }
            let state = try await service.sourceDiscoveryState(seed)
            XCTAssertFalse(state.hasCurrentCheck("tx", window: MusicSourceDiscoveryWindow.playback), kind)
            XCTAssertNil(state.current(window: MusicSourceDiscoveryWindow.playback)["tx"], kind)
            let before = await cache.entries(fingerprint: MusicModel.fingerprint(seed), duration: 180)
            XCTAssertFalse(before.values.flatMap(\.observations).contains { $0.outcome == .notFound }, kind)
            let callsBefore = await qq.calls()
            let session = try await service.beginCandidateDiscovery(occurrenceID: "entry-1", expectedQueueRevision: 1)
            _ = try await session.snapshot(discover: true, force: false)
            let callsAfter = await qq.calls()
            XCTAssertEqual(callsAfter, callsBefore + 1, kind)
            await session.close(retainingAttemptID: nil)
            await service.close()
        }
    }

    func testConflictingNativeRecordingDoesNotTurnRejectedPositiveIntoNotFound() async throws {
        let seed = try song(), cache = MusicNativeMemorySourceDiscoveryCache(clock: { NativeMusicFixture.date })
        let good = try song("tx", id: "same"), conflicting = try song("tx", id: "same", duration: 300)
        await cache.record(fingerprint: MusicModel.fingerprint(seed), duration: 180, platform: "tx",
                           checkedAt: NativeMusicFixture.date.timeIntervalSince1970 * 1000, sources: [source(conflicting)])
        let qq = SongMetadataQQ([.init()])
        let service = try await setup(qq, seed: seed, cache: cache)
        let rejected = try await service.sourceDiscoveryState(seed)
        XCTAssertNil(rejected.current(window: MusicSourceDiscoveryWindow.playback)["tx"])
        try await service.retainSongMetadata([good])
        let preserved = try await service.sourceDiscoveryState(seed)
        XCTAssertNil(preserved.current(window: MusicSourceDiscoveryWindow.playback)["tx"],
                     "an ID conflict cannot be silently relabeled as this recording")
        await service.close()
    }
}
