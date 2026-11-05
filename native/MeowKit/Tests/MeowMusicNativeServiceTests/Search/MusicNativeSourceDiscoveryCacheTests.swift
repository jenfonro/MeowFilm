import Foundation
import XCTest
import MeowMusicFeature
import MeowStorage
@testable import MeowMusicNativeService

private actor MusicNativeMalformedDiscoveryQQ: MusicPublicQQProviding {
    private var requests = 0
    func search(query: String, page: Int, limit: Int) -> MusicNativeTrackPage {
        requests += 1
        return .init(items: [.init(platform: "tx", data: Data("{}".utf8))], total: 1)
    }
    func lyrics(mid: String) -> MusicNativeLyrics { .init(text: "") }
    func calls() -> Int { requests }
}

final class MusicNativeSourceDiscoveryCacheTests: XCTestCase {
    private func row(_ platform: String = "wy", id: String = "1", title: String = "Synthetic recording",
                     artist: String = "Fixture", duration: Int = 180) throws -> (song: MJ, source: MJ) {
        let song = try MusicModel.normalize(MusicNativeCandidateDiscoveryFixture.track(
            platform, id: id, title: title, artist: artist, duration: duration))
        return (song, try XCTUnwrap(song["sources"].array.first))
    }
    private func stored(_ source: MJ) -> MusicSourceDiscoverySource {
        .init(sourceKey: source["sourceKey"].string, source: source["source"].string,
              musicInfo: source["musicInfo"].json)
    }
    private func record(_ cache: any MusicSourceDiscoveryCaching, song: MJ, platform: String = "wy",
                        at: Double = NativeMusicFixture.date.timeIntervalSince1970 * 1000,
                        sources: [MusicSourceDiscoverySource]) async {
        await cache.record(fingerprint: MusicModel.fingerprint(song), duration: song["duration"].number,
            platform: platform, checkedAt: at, sources: sources)
    }
    private func entry(_ cache: any MusicSourceDiscoveryCaching, _ song: MJ) async -> [String: MusicSourceDiscoveryEntry] {
        await cache.entries(fingerprint: MusicModel.fingerprint(song), duration: song["duration"].number)
    }
    private func recording(_ song: MJ) -> MusicSourceDiscoveryRecording {
        .init(fingerprint: MusicModel.fingerprint(song), duration: song["duration"].number)
    }
    private func recordsFile(_ root: URL) -> URL {
        root.appendingPathComponent(MusicNativePersistentSourceDiscoveryCache.namespace, isDirectory: true)
            .appendingPathComponent("records.json")
    }
    private func temporaryRoot() throws -> URL {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString, isDirectory: true)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        return root
    }
    /// The discovery queue fixture with the same recording saved in favorites.
    private func favoriteStore(_ song: MJ) throws -> NativeMusicMemoryDocument {
        var document = try MusicDocument(nil, runID: "favorite")
        document.queue["entries"] = .array([.obj([("id", .string("entry-1")), ("song", song)])])
        document.queue["currentId"] = .string("entry-1"); document.queue["serial"] = .n(1); document.queue["revision"] = .n(1)
        document.library["library"]["songs"] = .obj([(song["songId"].string, song)])
        var favorites = document.library["library"]["collections"].array[0]
        favorites["order"] = .strings([song["songId"].string])
        document.library["library"]["collections"] = .array([favorites])
        try document.validate()
        return NativeMusicMemoryDocument(try document.encode(changed: ["queue", "playback", "library"]))
    }

    func testMemoryCacheKeepsCompatibleSourcesWithOriginalCheckTimeRegardlessOfAge() async throws {
        let clock = NativeMusicFixtureClock(), cache = MusicNativeMemorySourceDiscoveryCache(clock: { clock.now() })
        let value = try row(), source = stored(value.source), checked = clock.now().timeIntervalSince1970 * 1000
        await record(cache, song: value.song, sources: [source])
        let hit = await entry(cache, value.song)
        XCTAssertEqual(hit["wy"]?.sources, [source])
        clock.advance(31 * 24 * 60 * 60)
        let old = await entry(cache, value.song)
        XCTAssertEqual(old["wy"]?.sources, [source], "a found match is not hidden by age")
        XCTAssertEqual(old["wy"]?.checkedAt, checked, "reading does not renew the check")
    }

    func testIncompatibleDurationIsAMissAndEmptySuccessIsRecorded() async throws {
        let cache = MusicNativeMemorySourceDiscoveryCache(clock: { NativeMusicFixture.date })
        let value = try row()
        await record(cache, song: value.song, sources: [])
        let hit = await entry(cache, value.song)
        XCTAssertEqual(hit["wy"]?.sources, [])
        var changed = value.song
        changed["duration"] = .n(300)
        let miss = await entry(cache, changed)
        XCTAssertTrue(miss.isEmpty)
    }

    func testAnotherPlatformWriteDoesNotSlideAnEarlierPlatformsCheckTime() async throws {
        let clock = NativeMusicFixtureClock(), cache = MusicNativeMemorySourceDiscoveryCache(clock: { clock.now() })
        let song = try row().song, first = clock.now().timeIntervalSince1970 * 1000
        await record(cache, song: song, sources: [])
        clock.advance(10 * 24 * 60 * 60)
        let second = clock.now().timeIntervalSince1970 * 1000
        await record(cache, song: song, platform: "tx", at: second, sources: [])
        clock.advance(20 * 24 * 60 * 60)
        let hit = await entry(cache, song)
        XCTAssertEqual(hit["wy"]?.checkedAt, first)
        XCTAssertEqual(hit["tx"]?.checkedAt, second)
    }

    func testIncompatibleDurationWriteDiscardsEarlierPlatformChecks() async throws {
        let cache = MusicNativeMemorySourceDiscoveryCache(clock: { NativeMusicFixture.date })
        let song = try row().song
        await record(cache, song: song, sources: [])
        var changed = song
        changed["duration"] = .n(300)
        await record(cache, song: changed, platform: "tx", sources: [])
        let hit = await entry(cache, changed), old = await entry(cache, song)
        XCTAssertEqual(Set(hit.keys), ["tx"])
        XCTAssertTrue(old.isEmpty)
    }

    func testIncompleteDurationWriteKeepsEarlierRecordingTimingEvidence() async throws {
        let cache = MusicNativeMemorySourceDiscoveryCache(clock: { NativeMusicFixture.date })
        let song = try row().song
        await record(cache, song: song, sources: [])
        var incomplete = song
        incomplete["duration"] = .n(0)
        await record(cache, song: incomplete, platform: "tx", sources: [])
        let compatible = await entry(cache, song)
        XCTAssertEqual(Set(compatible.keys), ["wy", "tx"])
        var other = song
        other["duration"] = .n(300)
        let miss = await entry(cache, other)
        XCTAssertTrue(miss.isEmpty)
    }

    func testSensitiveOrMalformedSourcesCannotReplaceAnEarlierSuccessfulCheck() async throws {
        let cache = MusicNativeMemorySourceDiscoveryCache(clock: { NativeMusicFixture.date })
        let value = try row(), source = stored(value.source)
        await record(cache, song: value.song, sources: [source])
        for field in ["token", "headers", "url", "audioUrl", "authorization"] {
            var row = value.source
            row["musicInfo"][field] = .string("synthetic-rejected-field")
            await record(cache, song: value.song, sources: [stored(row)])
            let hit = await entry(cache, value.song)
            XCTAssertEqual(hit["wy"]?.sources, [source])
            XCTAssertFalse(hit["wy"]?.sources.contains { $0.musicInfo.contains("synthetic-rejected-field") } ?? true)
        }
        await record(cache, song: value.song, sources: [
            .init(sourceKey: source.sourceKey, source: source.source, musicInfo: "{")
        ])
        let hit = await entry(cache, value.song)
        XCTAssertEqual(hit["wy"]?.sources, [source])
    }

    func testInvalidTimesAndDurationsCannotCreateSuccessfulChecks() async throws {
        let cache = MusicNativeMemorySourceDiscoveryCache(clock: { NativeMusicFixture.date })
        let song = try row().song, fingerprint = MusicModel.fingerprint(song)
        let at = NativeMusicFixture.date.timeIntervalSince1970 * 1000
        let invalidDurations: [Double] = [-1, .infinity, .nan, 604_801]
        for duration in invalidDurations {
            await cache.record(fingerprint: fingerprint, duration: duration, platform: "wy", checkedAt: at, sources: [])
        }
        let invalidTimes: [Double] = [0, -1, .infinity, .nan, at + 1]
        for checkedAt in invalidTimes {
            await cache.record(fingerprint: fingerprint, duration: 180, platform: "wy", checkedAt: checkedAt, sources: [])
        }
        let hit = await entry(cache, song)
        XCTAssertTrue(hit.isEmpty)
    }

    func testByteBudgetEvictsTheOldestRecording() async throws {
        let clock = NativeMusicFixtureClock()
        let cache = MusicNativeMemorySourceDiscoveryCache(clock: { clock.now() }, maximumBytes: 20_000)
        var songs: [MJ] = []
        for index in 0..<100 {
            let value = try row(id: String(index), title: "Synthetic recording \(index)")
            songs.append(value.song)
            await record(cache, song: value.song, at: clock.now().timeIntervalSince1970 * 1000,
                         sources: [stored(value.source)])
            clock.advance(1)
        }
        let evicted = await entry(cache, songs[0])
        let retained = await entry(cache, songs[99])
        XCTAssertTrue(evicted.isEmpty)
        XCTAssertEqual(retained["wy"]?.sources.count, 1)
    }

    func testPersistentCacheRoundTripsOnlyRetainedRecordingsThroughItsPlainNamespace() async throws {
        let root = try temporaryRoot()
        defer { try? FileManager.default.removeItem(at: root) }
        func make() -> MusicNativePersistentSourceDiscoveryCache {
            MusicNativePersistentSourceDiscoveryCache(storeFactory: {
                try DocumentStore(baseURL: root, namespace: MusicNativePersistentSourceDiscoveryCache.namespace)
            }, clock: { NativeMusicFixture.date })
        }
        let kept = try row(), temporary = try row(id: "2", title: "Temporary recording"), first = make()
        await record(first, song: kept.song, sources: [stored(kept.source)])
        await record(first, song: temporary.song, sources: [stored(temporary.source)])
        let unpersisted = await entry(make(), kept.song)
        XCTAssertTrue(unpersisted.isEmpty, "records live in memory until persisted")
        await first.persist(retaining: [recording(kept.song)])
        let reopened = make()
        let hit = await entry(reopened, kept.song), dropped = await entry(reopened, temporary.song)
        XCTAssertEqual(hit["wy"]?.sources, [stored(kept.source)])
        XCTAssertTrue(dropped.isEmpty, "a recording outside the retained set stays in memory only")
        XCTAssertEqual(MusicNativePersistentSourceDiscoveryCache.namespace, "music.source-discovery.v1")
        let saved = try XCTUnwrap(JSONSerialization.jsonObject(with: Data(contentsOf: recordsFile(root))) as? [String: Any])
        XCTAssertEqual(saved["version"] as? Int, 1)
        XCTAssertEqual((saved["records"] as? [String: Any])?.count, 1)
        let record = try XCTUnwrap((saved["records"] as? [String: [String: Any]])?.values.first)
        XCTAssertEqual(record["excluded"] as? [String], [])
    }

    func testMissingExcludedIsACacheMissWithoutReadTimeRewrite() async throws {
        let root = try temporaryRoot()
        defer { try? FileManager.default.removeItem(at: root) }
        let document = try DocumentStore(baseURL: root, namespace: MusicNativePersistentSourceDiscoveryCache.namespace)
        func make() -> MusicNativePersistentSourceDiscoveryCache {
            .init(storeFactory: { document }, clock: { NativeMusicFixture.date })
        }
        let value = try row(), cache = make()
        await record(cache, song: value.song, sources: [stored(value.source)])
        await cache.persist(retaining: [recording(value.song)])
        var envelope = try MJ.parse(XCTUnwrap(document.read(key: "records")))
        let key = MusicRecordingIdentity.hash(MusicModel.fingerprint(value.song))
        var old = envelope["records"][key]; old.remove("excluded")
        envelope["records"][key] = old
        let bytes = envelope.bytes
        try document.write(bytes, key: "records")
        let reopened = make(), miss = await entry(reopened, value.song)
        let excluded = await reopened.excluded(fingerprint: MusicModel.fingerprint(value.song), duration: 180)
        XCTAssertTrue(miss.isEmpty); XCTAssertTrue(excluded.isEmpty)
        XCTAssertEqual(try document.read(key: "records"), bytes)
        // The existing invalid-cache policy permits a later ordinary persist.
        await record(reopened, song: value.song, sources: [stored(value.source)])
        await reopened.persist(retaining: [recording(value.song)])
        let saved = try MJ.parse(XCTUnwrap(document.read(key: "records")))
        XCTAssertEqual(saved["records"][key]["excluded"], .array([]))
        let hit = await entry(make(), value.song)
        XCTAssertEqual(hit["wy"]?.sources, [stored(value.source)])
    }

    func testPersistentLoadedEnvelopeIsReusedAndOnlyChangedRetainedBytesAreWritten() async throws {
        let root = try temporaryRoot()
        defer { try? FileManager.default.removeItem(at: root) }
        let clock = NativeMusicFixtureClock()
        let document = try DocumentStore(baseURL: root, namespace: MusicNativePersistentSourceDiscoveryCache.namespace)
        let initial = MusicNativePersistentSourceDiscoveryCache(storeFactory: { document }, clock: { clock.now() })
        let value = try row(), source = stored(value.source)
        await record(initial, song: value.song, sources: [source])
        await initial.persist(retaining: [recording(value.song)])
        let cache = MusicNativePersistentSourceDiscoveryCache(storeFactory: { document }, clock: { clock.now() })
        let loaded = await entry(cache, value.song)
        XCTAssertEqual(loaded["wy"]?.sources, [source])
        // A reload would now read an absent document and lose the loaded record.
        try document.remove(key: "records")
        for platform in ["wy", "tx", "kg", "kw", "mg"] {
            await record(cache, song: value.song, platform: platform, sources: platform == "wy" ? [source] : [])
            let saved = await entry(cache, value.song)
            XCTAssertNotNil(saved[platform]); XCTAssertEqual(saved["wy"]?.sources, [source])
        }
        XCTAssertNil(try document.read(key: "records"), "records stay in memory until persisted")
        await cache.persist(retaining: [recording(value.song)])
        let saved = try XCTUnwrap(JSONSerialization.jsonObject(with: Data(contentsOf: recordsFile(root))) as? [String: Any])
        let records = try XCTUnwrap(saved["records"] as? [String: [String: Any]])
        XCTAssertEqual(Set((records.values.first?["platforms"] as? [String: Any] ?? [:]).keys),
                       ["wy", "tx", "kg", "kw", "mg"])
        let fileID = try XCTUnwrap(FileManager.default.attributesOfItem(atPath: recordsFile(root).path)[.systemFileNumber] as? NSNumber)
        await cache.persist(retaining: [recording(value.song)])
        XCTAssertEqual(try FileManager.default.attributesOfItem(atPath: recordsFile(root).path)[.systemFileNumber] as? NSNumber,
                       fileID, "unchanged bytes are not replaced")
        clock.advance(30 * 24 * 60 * 60)
        let old = await entry(cache, value.song)
        XCTAssertEqual(old["wy"]?.sources, [source]); XCTAssertEqual(Set(old.keys), ["wy", "tx", "kg", "kw", "mg"])
        XCTAssertEqual(try FileManager.default.attributesOfItem(atPath: recordsFile(root).path)[.systemFileNumber] as? NSNumber,
                       fileID, "reading old positive records does not write")
    }

    func testPersistentWriteFailureKeepsMemoryAndFailedLoadsCanRetry() async throws {
        let root = try temporaryRoot()
        defer { try? FileManager.default.removeItem(at: root) }
        let document = try DocumentStore(baseURL: root, namespace: MusicNativePersistentSourceDiscoveryCache.namespace)
        let cache = MusicNativePersistentSourceDiscoveryCache(storeFactory: { document }, clock: { NativeMusicFixture.date })
        let value = try row(), source = stored(value.source), retained = [recording(value.song)]
        await record(cache, song: value.song, sources: [source])
        await cache.persist(retaining: retained)
        let committed = try Data(contentsOf: recordsFile(root))
        await record(cache, song: value.song, platform: "tx", sources: [])
        // A directory in place of the document makes reads and replacement fail.
        try FileManager.default.removeItem(at: recordsFile(root))
        try FileManager.default.createDirectory(at: recordsFile(root), withIntermediateDirectories: false)
        await cache.persist(retaining: retained)
        let memory = await entry(cache, value.song)
        XCTAssertEqual(Set(memory.keys), ["wy", "tx"], "a failed write keeps serving memory")
        try FileManager.default.removeItem(at: recordsFile(root))
        try committed.write(to: recordsFile(root))
        await cache.persist(retaining: retained)
        let reopened = MusicNativePersistentSourceDiscoveryCache(storeFactory: { document }, clock: { NativeMusicFixture.date })
        let durable = await entry(reopened, value.song)
        XCTAssertEqual(Set(durable.keys), ["wy", "tx"], "the next persist retries the failed write")
        let bytes = try Data(contentsOf: recordsFile(root))
        try FileManager.default.removeItem(at: recordsFile(root))
        try FileManager.default.createDirectory(at: recordsFile(root), withIntermediateDirectories: false)
        let fresh = MusicNativePersistentSourceDiscoveryCache(storeFactory: { document }, clock: { NativeMusicFixture.date })
        let miss = await entry(fresh, value.song)
        XCTAssertTrue(miss.isEmpty)
        try FileManager.default.removeItem(at: recordsFile(root))
        try bytes.write(to: recordsFile(root))
        let recovered = await entry(fresh, value.song)
        XCTAssertEqual(recovered, durable, "a failed load must remain retryable")
    }

    func testPersistentByteBudgetEvictionSurvivesReopening() async throws {
        let root = try temporaryRoot()
        defer { try? FileManager.default.removeItem(at: root) }
        let clock = NativeMusicFixtureClock(), maximum = 20_000
        let document = try DocumentStore(baseURL: root, namespace: MusicNativePersistentSourceDiscoveryCache.namespace)
        let cache = MusicNativePersistentSourceDiscoveryCache(storeFactory: { document },
            clock: { clock.now() }, maximumBytes: maximum)
        var songs: [MJ] = []
        for index in 0..<60 {
            let value = try row(id: String(index), title: "Persistent recording \(index)")
            songs.append(value.song)
            await record(cache, song: value.song, at: clock.now().timeIntervalSince1970 * 1000,
                         sources: [stored(value.source)])
            clock.advance(1)
        }
        let cachedOld = await entry(cache, songs[0]), cachedLatest = await entry(cache, songs[59])
        XCTAssertTrue(cachedOld.isEmpty)
        XCTAssertEqual(cachedLatest["wy"]?.sources.count, 1)
        await cache.persist(retaining: songs.map(recording))
        let data = try XCTUnwrap(document.read(key: "records"))
        XCTAssertLessThanOrEqual(data.count, maximum)
        let reopened = MusicNativePersistentSourceDiscoveryCache(storeFactory: { document },
            clock: { clock.now() }, maximumBytes: maximum)
        let old = await entry(reopened, songs[0]), latest = await entry(reopened, songs[59])
        XCTAssertTrue(old.isEmpty)
        XCTAssertEqual(latest["wy"]?.sources.count, 1)
    }

    func testEmptySuccessStopsTheSecondPlaybackSearchForThatPlatform() async throws {
        let seed = try MusicNativeCandidateDiscoveryFixture.song()
        let store = try MusicNativeCandidateDiscoveryFixture.store(song: seed)
        let http = MusicNativeCandidateDiscoveryHTTP(empty: ["kw"]), cache = MusicNativeMemorySourceDiscoveryCache(clock: { NativeMusicFixture.date })
        let service = try await NativeMusicFixture.service(store, scripts: MusicNativeCandidateDiscoveryScripts(),
            qq: MusicNativeCandidateDiscoveryQQ(), http: http, sourceDiscovery: cache)
        let first = try await service.beginCandidateDiscovery(occurrenceID: "entry-1", expectedQueueRevision: 1)
        _ = try await first.snapshot(discover: true, force: true)
        await first.close(retainingAttemptID: nil)
        let firstCalls = await http.calls()
        XCTAssertEqual(Set(firstCalls), ["wy", "kg", "kw", "mg"])
        let second = try await service.beginCandidateDiscovery(occurrenceID: "entry-1", expectedQueueRevision: 1)
        _ = try await second.snapshot(discover: true, force: false)
        await second.close(retainingAttemptID: nil)
        let secondCalls = await http.calls()
        XCTAssertEqual(secondCalls, firstCalls)
        await service.close()
    }

    func testFailedPlatformIsRetriedWhileSuccessfulPlatformsStayCached() async throws {
        let seed = try MusicNativeCandidateDiscoveryFixture.song()
        let store = try MusicNativeCandidateDiscoveryFixture.store(song: seed)
        let http = MusicNativeCandidateDiscoveryHTTP(failures: ["wy": .rateLimited]), cache = MusicNativeMemorySourceDiscoveryCache(clock: { NativeMusicFixture.date })
        let service = try await NativeMusicFixture.service(store, scripts: MusicNativeCandidateDiscoveryScripts(),
            qq: MusicNativeCandidateDiscoveryQQ(), http: http, sourceDiscovery: cache)
        let first = try await service.beginCandidateDiscovery(occurrenceID: "entry-1", expectedQueueRevision: 1)
        let initial = try await first.snapshot(discover: true, force: true)
        XCTAssertTrue(initial.issues.contains { $0.platform == "wy" && $0.failure == .rateLimited })
        await first.close(retainingAttemptID: nil)
        let firstCalls = await http.calls()
        let second = try await service.beginCandidateDiscovery(occurrenceID: "entry-1", expectedQueueRevision: 1)
        _ = try await second.snapshot(discover: true, force: false)
        await second.close(retainingAttemptID: nil)
        let secondCalls = await http.calls()
        XCTAssertEqual(Array(secondCalls.dropFirst(firstCalls.count)), ["wy"])
        await service.close()
    }

    func testMalformedSearchRowsDoNotBecomeSuccessfulEmptyCacheChecks() async throws {
        var song = try MusicNativeCandidateDiscoveryFixture.song(), rows = song["sources"].array
        rows[0]["musicInfo"]["duration"] = .n(0)
        song["sources"] = .array(rows)
        let store = try MusicNativeCandidateDiscoveryFixture.store(song: song), qq = MusicNativeMalformedDiscoveryQQ()
        let cache = MusicNativeMemorySourceDiscoveryCache(clock: { NativeMusicFixture.date })
        let service = NativeMusicService(document: store, scripts: MusicNativeCandidateDiscoveryScripts(), qq: qq,
            sourceDiscovery: cache, enabledPlatforms: [.qq], transport: NativeMusicNoNetwork(), clock: { NativeMusicFixture.date })
        try await service.setAccess(available: true, generation: 1)
        for _ in 0..<2 {
            let session = try await service.beginCandidateDiscovery(occurrenceID: "entry-1", expectedQueueRevision: 1)
            let snapshot = try await session.snapshot(discover: true, force: false)
            XCTAssertTrue(snapshot.issues.contains { $0.stage == .identity && $0.failure == .upstreamInvalid })
            await session.close(retainingAttemptID: nil)
            let entries = await entry(cache, song)
            XCTAssertNil(entries["tx"])
        }
        let calls = await qq.calls()
        XCTAssertEqual(calls, 2)
        await service.close()
    }

    func testIncompleteExistingSourceIsRequeried() async throws {
        var song = try MusicModel.normalize(MusicNativeCandidateDiscoveryFixture.track("tx", id: "SeedMID"))
        var rows = song["sources"].array
        rows[0]["musicInfo"]["singer"] = .string("未知歌手")
        rows[0]["musicInfo"]["duration"] = .n(0)
        song["sources"] = .array(rows)
        let store = try MusicNativeCandidateDiscoveryFixture.store(song: song)
        let qq = MusicNativeCandidateDiscoveryQQ(fail: .rateLimited)
        let service = try await NativeMusicFixture.service(store, scripts: MusicNativeCandidateDiscoveryScripts(), qq: qq)
        let session = try await service.beginCandidateDiscovery(occurrenceID: "entry-1", expectedQueueRevision: 1)
        let snapshot = try await session.snapshot(discover: true, force: false)
        let calls = await qq.calls()
        XCTAssertEqual(calls.count, 1)
        XCTAssertTrue(snapshot.issues.contains { $0.platform == "tx" && $0.failure == .rateLimited })
        await session.close(retainingAttemptID: nil); await service.close()
    }

    func testSuccessfulEmptyCheckSuppressesRetryOfIncompleteExistingMetadata() async throws {
        var song = try MusicNativeCandidateDiscoveryFixture.song(), rows = song["sources"].array
        rows[0]["musicInfo"]["singer"] = .string("未知歌手")
        rows[0]["musicInfo"]["duration"] = .n(0)
        song["sources"] = .array(rows)
        let store = try MusicNativeCandidateDiscoveryFixture.store(song: song)
        let cache = MusicNativeMemorySourceDiscoveryCache(clock: { NativeMusicFixture.date })
        await record(cache, song: song, platform: "tx", sources: [])
        let qq = MusicNativeCandidateDiscoveryQQ(fail: .rateLimited)
        let service = NativeMusicService(document: store, scripts: MusicNativeCandidateDiscoveryScripts(), qq: qq,
            sourceDiscovery: cache, enabledPlatforms: [.qq], transport: NativeMusicNoNetwork(), clock: { NativeMusicFixture.date })
        try await service.setAccess(available: true, generation: 1)
        let session = try await service.beginCandidateDiscovery(occurrenceID: "entry-1", expectedQueueRevision: 1)
        let snapshot = try await session.snapshot(discover: true, force: false)
        XCTAssertFalse(snapshot.searched)
        XCTAssertTrue(snapshot.issues.isEmpty)
        let calls = await qq.calls()
        XCTAssertTrue(calls.isEmpty)
        await session.close(retainingAttemptID: nil); await service.close()
    }

    func testSuccessfulRefreshEnrichesIncompleteExistingSourceMetadata() async throws {
        var song = try MusicNativeCandidateDiscoveryFixture.song(), rows = song["sources"].array
        rows[0]["musicInfo"]["singer"] = .string("unknown")
        rows[0]["musicInfo"]["duration"] = .n(0)
        song["sources"] = .array(rows)
        let store = try MusicNativeCandidateDiscoveryFixture.store(song: song), qq = MusicNativeCandidateDiscoveryQQ()
        let service = NativeMusicService(document: store, scripts: MusicNativeCandidateDiscoveryScripts(), qq: qq,
            enabledPlatforms: [.qq], transport: NativeMusicNoNetwork(), clock: { NativeMusicFixture.date })
        try await service.setAccess(available: true, generation: 1)
        let session = try await service.beginCandidateDiscovery(occurrenceID: "entry-1", expectedQueueRevision: 1)
        let snapshot = try await session.snapshot(discover: true, force: false)
        XCTAssertTrue(snapshot.searched)
        XCTAssertEqual(snapshot.track.sources.map(\.nativeId), ["SeedMID"])
        try await session.commitDiscovery()
        let saved = try await service.read()
        let info = saved.queue["entries"].array[0]["song"]["sources"].array[0]["musicInfo"]
        XCTAssertEqual(info["singer"].string, "Fixture")
        XCTAssertEqual(info["duration"].number, 180)
        let calls = await qq.calls()
        XCTAssertEqual(calls.count, 1)
        await session.close(retainingAttemptID: nil); await service.close()
    }

    func testDuplicateSourceObservationsCacheTheirEnrichedMetadata() async throws {
        var song = try MusicNativeCandidateDiscoveryFixture.song(), rows = song["sources"].array
        rows[0]["musicInfo"]["singer"] = .string("unknown")
        rows[0]["musicInfo"]["duration"] = .n(0)
        song["sources"] = .array(rows)
        let store = try MusicNativeCandidateDiscoveryFixture.store(song: song)
        let qq = MusicNativeCandidateDiscoveryQQ(items: [
            MusicNativeCandidateDiscoveryFixture.track(artist: "unknown", duration: 0),
            MusicNativeCandidateDiscoveryFixture.track()
        ])
        let cache = MusicNativeMemorySourceDiscoveryCache(clock: { NativeMusicFixture.date })
        let service = NativeMusicService(document: store, scripts: MusicNativeCandidateDiscoveryScripts(), qq: qq,
            sourceDiscovery: cache, enabledPlatforms: [.qq], transport: NativeMusicNoNetwork(), clock: { NativeMusicFixture.date })
        try await service.setAccess(available: true, generation: 1)
        let first = try await service.beginCandidateDiscovery(occurrenceID: "entry-1", expectedQueueRevision: 1)
        _ = try await first.snapshot(discover: true, force: false)
        await first.close(retainingAttemptID: nil)
        let entries = await entry(cache, song)
        let sources = try XCTUnwrap(entries["tx"]).sources
        XCTAssertEqual(sources.count, 1)
        let info = try MJ.parse(Data(sources[0].musicInfo.utf8))
        XCTAssertEqual(info["singer"].string, "Fixture")
        XCTAssertEqual(info["duration"].number, 180)
        let second = try await service.beginCandidateDiscovery(occurrenceID: "entry-1", expectedQueueRevision: 1)
        let snapshot = try await second.snapshot(discover: true, force: false)
        XCTAssertFalse(snapshot.searched)
        XCTAssertEqual(snapshot.track.sources.map(\.nativeId), ["SeedMID"])
        let calls = await qq.calls()
        XCTAssertEqual(calls.count, 1)
        await second.close(retainingAttemptID: nil); await service.close()
    }

    func testForceBypassesEveryCachedPlatform() async throws {
        let seed = try MusicNativeCandidateDiscoveryFixture.song()
        let store = try MusicNativeCandidateDiscoveryFixture.store(song: seed)
        let http = MusicNativeCandidateDiscoveryHTTP(), cache = MusicNativeMemorySourceDiscoveryCache(clock: { NativeMusicFixture.date })
        let service = try await NativeMusicFixture.service(store, qq: MusicNativeCandidateDiscoveryQQ(),
            http: http, sourceDiscovery: cache)
        let first = try await service.beginCandidateDiscovery(occurrenceID: "entry-1", expectedQueueRevision: 1)
        _ = try await first.snapshot(discover: true, force: true)
        await first.close(retainingAttemptID: nil)
        let firstCalls = await http.calls()
        let second = try await service.beginCandidateDiscovery(occurrenceID: "entry-1", expectedQueueRevision: 1)
        _ = try await second.snapshot(discover: true, force: true)
        let secondCalls = await http.calls()
        XCTAssertEqual(secondCalls.count, firstCalls.count * 2)
        _ = try await second.snapshot(discover: true, force: true)
        let sameSessionCalls = await http.calls()
        XCTAssertEqual(sameSessionCalls.count, firstCalls.count * 3)
        await second.close(retainingAttemptID: nil); await service.close()
    }

    func testCachedSourcesMergeWithoutAnyNetworkRequest() async throws {
        let seed = try MusicNativeCandidateDiscoveryFixture.song()
        let store = try MusicNativeCandidateDiscoveryFixture.store(song: seed)
        let cache = MusicNativeMemorySourceDiscoveryCache(clock: { NativeMusicFixture.date })
        let cached = try MusicModel.normalize(MusicNativeCandidateDiscoveryFixture.track("wy", id: "cached"))
        await record(cache, song: seed, platform: "wy", sources: [stored(cached["sources"].array[0])])
        for platform in MusicNativeSearchProtocol.platforms where platform != "wy" {
            await record(cache, song: seed, platform: platform, sources: [])
        }
        let http = MusicNativeCandidateDiscoveryHTTP(), qq = MusicNativeCandidateDiscoveryQQ()
        let service = try await NativeMusicFixture.service(store, scripts: MusicNativeCandidateDiscoveryScripts(),
            qq: qq, http: http, sourceDiscovery: cache)
        let session = try await service.beginCandidateDiscovery(occurrenceID: "entry-1", expectedQueueRevision: 1)
        let before = store.snapshot().0
        let snapshot = try await session.snapshot(discover: true, force: false)
        XCTAssertTrue(snapshot.track.sources.contains { $0.platform == "wy" && $0.nativeId == "cached" })
        XCTAssertFalse(snapshot.searched)
        XCTAssertEqual(store.snapshot().0, before)
        let httpCalls = await http.calls(), qqCalls = await qq.calls()
        XCTAssertTrue(httpCalls.isEmpty); XCTAssertTrue(qqCalls.isEmpty)
        try await session.commitDiscovery()
        let queue = try await service.queue()
        XCTAssertEqual(queue.revision, 2)
        XCTAssertTrue(queue.entries[0].track.sources.contains { $0.platform == "wy" && $0.nativeId == "cached" })
        await session.close(retainingAttemptID: nil); await service.close()
    }

    func testCachedEditionsKeepSlotsForEveryPreviouslyDiscoveredPlatform() async throws {
        let seed = try MusicNativeCandidateDiscoveryFixture.song()
        let cache = MusicNativeMemorySourceDiscoveryCache(clock: { NativeMusicFixture.date })
        let editions = try (1...16).map { try row(id: String($0)).source }
        await record(cache, song: seed, sources: editions.map(stored))
        for platform in ["kg", "kw", "mg"] {
            let value = try row(platform, id: platform == "kg" ? String(repeating: "A", count: 32) : "123")
            await record(cache, song: seed, platform: platform, sources: [stored(value.source)])
        }
        let http = MusicNativeCandidateDiscoveryHTTP(), qq = MusicNativeCandidateDiscoveryQQ()
        let service = try await NativeMusicFixture.service(MusicNativeCandidateDiscoveryFixture.store(song: seed),
            scripts: MusicNativeCandidateDiscoveryScripts(), qq: qq, http: http, sourceDiscovery: cache)
        let session = try await service.beginCandidateDiscovery(occurrenceID: "entry-1", expectedQueueRevision: 1)
        let snapshot = try await session.snapshot(discover: true, force: false)
        XCTAssertEqual(snapshot.track.sources.count, 16)
        XCTAssertEqual(Set(snapshot.track.sources.map(\.platform)), ["wy", "tx", "kg", "kw", "mg"])
        let httpCalls = await http.calls(), qqCalls = await qq.calls()
        XCTAssertTrue(httpCalls.isEmpty); XCTAssertTrue(qqCalls.isEmpty)
        await session.close(retainingAttemptID: nil); await service.close()
    }

    func testEnablingANewPlatformQueriesOnlyItsMissingCacheEntry() async throws {
        let seed = try MusicNativeCandidateDiscoveryFixture.song()
        let store = try MusicNativeCandidateDiscoveryFixture.store(song: seed)
        let cache = MusicNativeMemorySourceDiscoveryCache(clock: { NativeMusicFixture.date })
        let http = MusicNativeCandidateDiscoveryHTTP(), qq = MusicNativeCandidateDiscoveryQQ()
        let first = NativeMusicService(document: store, qq: qq, sourceDiscovery: cache,
            enabledPlatforms: [.netease, .qq, .kugou], transport: http, clock: { NativeMusicFixture.date })
        try await first.setAccess(available: true, generation: 1)
        let initial = try await first.beginCandidateDiscovery(occurrenceID: "entry-1", expectedQueueRevision: 1)
        _ = try await initial.snapshot(discover: true, force: true)
        await initial.close(retainingAttemptID: nil); await first.close()
        let initialEntries = await entry(cache, seed), initialCalls = await http.calls()
        XCTAssertEqual(Set(initialEntries.keys), ["wy", "tx", "kg"])
        XCTAssertEqual(Set(initialCalls), ["wy", "kg"])
        let second = NativeMusicService(document: store, scripts: MusicNativeCandidateDiscoveryScripts(), qq: qq,
            sourceDiscovery: cache, enabledPlatforms: [.netease, .qq, .kugou, .kuwo], transport: http,
            clock: { NativeMusicFixture.date })
        try await second.setAccess(available: true, generation: 1)
        let session = try await second.beginCandidateDiscovery(occurrenceID: "entry-1", expectedQueueRevision: 1)
        _ = try await session.snapshot(discover: true, force: false)
        let calls = await http.calls(), qqCalls = await qq.calls()
        XCTAssertEqual(Array(calls.dropFirst(initialCalls.count)), ["kw"])
        XCTAssertEqual(qqCalls.count, 1)
        let updated = await entry(cache, seed)
        XCTAssertNil(updated["mg"])
        XCTAssertNotNil(updated["kw"])
        await session.close(retainingAttemptID: nil); await second.close()
    }

    func testEmptyCheckIsSearchedAgainOnlyAfterTwentyFourHours() async throws {
        let clock = NativeMusicFixtureClock(), seed = try MusicNativeCandidateDiscoveryFixture.song()
        let store = try MusicNativeCandidateDiscoveryFixture.store(song: seed)
        let cache = MusicNativeMemorySourceDiscoveryCache(clock: { clock.now() })
        let http = MusicNativeCandidateDiscoveryHTTP(empty: ["kw"])
        let service = try await NativeMusicFixture.service(store, scripts: MusicNativeCandidateDiscoveryScripts(),
            qq: MusicNativeCandidateDiscoveryQQ(), http: http, sourceDiscovery: cache, clock: { clock.now() })
        let first = try await service.beginCandidateDiscovery(occurrenceID: "entry-1", expectedQueueRevision: 1)
        _ = try await first.snapshot(discover: true, force: false)
        try await first.commitDiscovery()
        await first.close(retainingAttemptID: nil)
        let firstCalls = await http.calls()
        XCTAssertEqual(Set(firstCalls), ["wy", "kg", "kw", "mg"])
        clock.advance(24 * 60 * 60 - 1)
        let early = try await service.beginCandidateDiscovery(occurrenceID: "entry-1", expectedQueueRevision: 2)
        _ = try await early.snapshot(discover: true, force: false)
        await early.close(retainingAttemptID: nil)
        let earlyCalls = await http.calls()
        XCTAssertEqual(earlyCalls, firstCalls, "an empty check is reused for 24 hours")
        clock.advance(2)
        let second = try await service.beginCandidateDiscovery(occurrenceID: "entry-1", expectedQueueRevision: 2)
        _ = try await second.snapshot(discover: true, force: false)
        await second.close(retainingAttemptID: nil)
        let secondCalls = await http.calls()
        XCTAssertEqual(Array(secondCalls.dropFirst(firstCalls.count)), ["kw"],
                       "Empty results retry after 24 hours; found sources do not expire")
        await service.close()
    }

    func testOldFoundMatchIsMergedWithoutSearching() async throws {
        let seed = try MusicNativeCandidateDiscoveryFixture.song()
        let store = try MusicNativeCandidateDiscoveryFixture.store(song: seed)
        let now = NativeMusicFixture.date.timeIntervalSince1970 * 1000
        let cache = MusicNativeMemorySourceDiscoveryCache(clock: { NativeMusicFixture.date })
        let cached = try MusicModel.normalize(MusicNativeCandidateDiscoveryFixture.track("wy", id: "cached"))
        await record(cache, song: seed, platform: "wy", at: now - 31 * 24 * 60 * 60 * 1000,
                     sources: [stored(cached["sources"].array[0])])
        for platform in ["kg", "kw", "mg"] { await record(cache, song: seed, platform: platform, sources: []) }
        let http = MusicNativeCandidateDiscoveryHTTP(), qq = MusicNativeCandidateDiscoveryQQ()
        let service = try await NativeMusicFixture.service(store, scripts: MusicNativeCandidateDiscoveryScripts(),
            qq: qq, http: http, sourceDiscovery: cache)
        let session = try await service.beginCandidateDiscovery(occurrenceID: "entry-1", expectedQueueRevision: 1)
        let snapshot = try await session.snapshot(discover: true, force: false)
        XCTAssertTrue(snapshot.track.sources.contains { $0.platform == "wy" && $0.nativeId == "cached" })
        let httpCalls = await http.calls(), qqCalls = await qq.calls()
        XCTAssertTrue(httpCalls.isEmpty, "age alone never triggers a search"); XCTAssertTrue(qqCalls.isEmpty)
        await session.close(retainingAttemptID: nil); await service.close()
    }

    func testOnlyLibraryRecordingsReachDiskAndSavingPersistsEarlierMemory() async throws {
        let root = try temporaryRoot()
        defer { try? FileManager.default.removeItem(at: root) }
        let document = try DocumentStore(baseURL: root, namespace: MusicNativePersistentSourceDiscoveryCache.namespace)
        func reopened() -> MusicNativePersistentSourceDiscoveryCache {
            MusicNativePersistentSourceDiscoveryCache(storeFactory: { document }, clock: { NativeMusicFixture.date })
        }
        let seed = try MusicNativeCandidateDiscoveryFixture.song()
        let store = try MusicNativeCandidateDiscoveryFixture.store(song: seed), cache = reopened()
        let service = try await NativeMusicFixture.service(store, scripts: MusicNativeCandidateDiscoveryScripts(),
            qq: MusicNativeCandidateDiscoveryQQ(), http: MusicNativeCandidateDiscoveryHTTP(), sourceDiscovery: cache)
        let session = try await service.beginCandidateDiscovery(occurrenceID: "entry-1", expectedQueueRevision: 1)
        _ = try await session.snapshot(discover: true, force: false)
        await session.close(retainingAttemptID: nil)
        await service.flushSourceDiscoveryPersist()
        let temporary = await entry(reopened(), seed)
        XCTAssertTrue(temporary.isEmpty, "a temporary queue song is not written to disk")
        XCTAssertNil(try document.read(key: "records"))
        let collections = try await service.collections()
        var favorites = try XCTUnwrap(collections.first { $0.kind == "favorites" })
        favorites = try await service.editPlaylist(favorites, name: nil, refs: [.init(kind: .queue, id: "entry-1")], order: nil)
        await service.flushSourceDiscoveryPersist()
        let saved = await entry(reopened(), seed)
        XCTAssertEqual(Set(saved.keys), ["wy", "kg", "kw", "mg"], "saving persists the checks made before it")
        _ = try await service.editPlaylist(favorites, name: nil, refs: [], order: nil)
        await service.flushSourceDiscoveryPersist()
        let removed = await entry(reopened(), seed)
        XCTAssertTrue(removed.isEmpty, "a recording removed from the library is dropped at the next persist")
        await service.close()
    }

    func testExclusionsBelongToOneRecordingAndArePersistedWithIt() async throws {
        let root = try temporaryRoot()
        defer { try? FileManager.default.removeItem(at: root) }
        let document = try DocumentStore(baseURL: root, namespace: MusicNativePersistentSourceDiscoveryCache.namespace)
        let cache = MusicNativePersistentSourceDiscoveryCache(storeFactory: { document }, clock: { NativeMusicFixture.date })
        let a = try row(), b = try row(id: "2", title: "Another recording"), key = a.source["sourceKey"].string
        await cache.exclude(fingerprint: MusicModel.fingerprint(a.song), duration: 180, sourceKey: key)
        await record(cache, song: a.song, sources: [])
        let excluded = await cache.excluded(fingerprint: MusicModel.fingerprint(a.song), duration: 180)
        let other = await cache.excluded(fingerprint: MusicModel.fingerprint(b.song), duration: 180)
        XCTAssertEqual(excluded, [key]); XCTAssertTrue(other.isEmpty, "the target recording is not globally blocked")
        await cache.persist(retaining: [recording(a.song)])
        let reopened = MusicNativePersistentSourceDiscoveryCache(storeFactory: { document }, clock: { NativeMusicFixture.date })
        let durable = await reopened.excluded(fingerprint: MusicModel.fingerprint(a.song), duration: 180)
        XCTAssertEqual(durable, [key])
    }

    func testPersistentReadFailureServesMemoryAndNeverOverwritesTheUnreadDocument() async throws {
        let seed = try MusicNativeCandidateDiscoveryFixture.song()
        let store = try favoriteStore(seed)
        let root = try temporaryRoot()
        defer { try? FileManager.default.removeItem(at: root) }
        let document = try DocumentStore(baseURL: root, namespace: MusicNativePersistentSourceDiscoveryCache.namespace)
        let initial = MusicNativePersistentSourceDiscoveryCache(storeFactory: { document }, clock: { NativeMusicFixture.date })
        await record(initial, song: seed, at: NativeMusicFixture.date.timeIntervalSince1970 * 1000 - 3_600_000, sources: [])
        await initial.persist(retaining: [recording(seed)])
        let bytes = try Data(contentsOf: recordsFile(root))
        // A second hard link makes the store refuse to read the document, while a
        // replacement write would still succeed.
        let alias = root.appendingPathComponent("records-alias.json")
        try FileManager.default.linkItem(at: recordsFile(root), to: alias)
        let cache = MusicNativePersistentSourceDiscoveryCache(storeFactory: { document }, clock: { NativeMusicFixture.date })
        let scripts = MusicNativeCandidateDiscoveryScripts()
        let service = try await NativeMusicFixture.service(store, scripts: scripts, qq: MusicNativeCandidateDiscoveryQQ(),
            http: MusicNativeCandidateDiscoveryHTTP(), sourceDiscovery: cache)
        let session = try await service.beginCandidateDiscovery(occurrenceID: "entry-1", expectedQueueRevision: 1)
        let snapshot = try await session.snapshot(discover: true, force: true)
        XCTAssertEqual(Set(snapshot.track.sources.map(\.platform)), ["wy", "tx", "kg", "kw", "mg"])
        try await session.commitDiscovery()
        let queue = try await service.queue(), memory = await entry(cache, seed)
        XCTAssertEqual(queue.revision, 2)
        XCTAssertEqual(Set(memory.keys), ["wy", "tx", "kg", "kw", "mg"], "an unreadable document does not hide this run's checks")
        let target = try XCTUnwrap(MusicNativeCandidateDiscoveryFixture.target(snapshot))
        let candidate = try await session.resolve(target, attemptID: UUID().uuidString.lowercased())
        XCTAssertEqual(candidate.occurrenceId, "entry-1")
        await session.close(retainingAttemptID: nil)
        await service.flushSourceDiscoveryPersist()
        XCTAssertEqual(try Data(contentsOf: recordsFile(root)), bytes, "a document that was never merged is not overwritten")
        try FileManager.default.removeItem(at: alias)
        let merged = await entry(cache, seed)
        XCTAssertEqual(merged["wy"]?.sources.isEmpty, false, "newer memory evidence wins over the older stored check")
        await service.close()
    }

    func testPersistentWriteFailureDoesNotRejectDiscoveryOrItsQueueCommit() async throws {
        let seed = try MusicNativeCandidateDiscoveryFixture.song()
        let store = try favoriteStore(seed)
        // An absent base reads as no document but cannot accept a replacement.
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString, isDirectory: true)
        defer { try? FileManager.default.removeItem(at: root) }
        let document = try DocumentStore(baseURL: root, namespace: MusicNativePersistentSourceDiscoveryCache.namespace)
        let cache = MusicNativePersistentSourceDiscoveryCache(storeFactory: { document }, clock: { NativeMusicFixture.date })
        let scripts = MusicNativeCandidateDiscoveryScripts()
        let service = try await NativeMusicFixture.service(store, scripts: scripts, qq: MusicNativeCandidateDiscoveryQQ(),
            http: MusicNativeCandidateDiscoveryHTTP(), sourceDiscovery: cache)
        let session = try await service.beginCandidateDiscovery(occurrenceID: "entry-1", expectedQueueRevision: 1)
        let snapshot = try await session.snapshot(discover: true, force: true)
        XCTAssertEqual(Set(snapshot.track.sources.map(\.platform)), ["wy", "tx", "kg", "kw", "mg"])
        try await session.commitDiscovery()
        await service.flushSourceDiscoveryPersist()
        let queue = try await service.queue(), memory = await entry(cache, seed)
        XCTAssertEqual(queue.revision, 2)
        XCTAssertEqual(Set(memory.keys), ["wy", "tx", "kg", "kw", "mg"], "a failed write keeps serving memory")
        XCTAssertFalse(FileManager.default.fileExists(atPath: root.path))
        let target = try XCTUnwrap(MusicNativeCandidateDiscoveryFixture.target(snapshot))
        let candidate = try await session.resolve(target, attemptID: UUID().uuidString.lowercased())
        XCTAssertEqual(candidate.occurrenceId, "entry-1")
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        await cache.persist(retaining: [recording(seed)])
        let reopened = MusicNativePersistentSourceDiscoveryCache(storeFactory: { document }, clock: { NativeMusicFixture.date })
        let durable = await entry(reopened, seed)
        XCTAssertEqual(Set(durable.keys), ["wy", "tx", "kg", "kw", "mg"], "the library recording is written once storage works")
        await session.close(retainingAttemptID: nil); await service.close()
    }

    func testPersistentStoreFailureIsAMissAndDoesNotRejectPlaybackDiscovery() async throws {
        let seed = try MusicNativeCandidateDiscoveryFixture.song()
        let store = try MusicNativeCandidateDiscoveryFixture.store(song: seed)
        let cache = MusicNativePersistentSourceDiscoveryCache(storeFactory: {
            throw DocumentStoreError.storageFailure
        })
        let service = try await NativeMusicFixture.service(store, qq: MusicNativeCandidateDiscoveryQQ(),
            http: MusicNativeCandidateDiscoveryHTTP(), sourceDiscovery: cache)
        let session = try await service.beginCandidateDiscovery(occurrenceID: "entry-1", expectedQueueRevision: 1)
        let snapshot = try await session.snapshot(discover: true, force: true)
        XCTAssertEqual(Set(snapshot.track.sources.map(\.platform)), ["wy", "tx", "kg", "kw", "mg"])
        try await session.commitDiscovery()
        let queue = try await service.queue()
        XCTAssertEqual(queue.revision, 2)
        await session.close(retainingAttemptID: nil); await service.close()
    }
}
