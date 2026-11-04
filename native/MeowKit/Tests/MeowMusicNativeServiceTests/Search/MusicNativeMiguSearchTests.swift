import Foundation
import XCTest
import MeowMusicFeature
@testable import MeowMusicNativeService

final class MusicNativeMiguSearchTests: XCTestCase {
    func testIndependentFixedSignatureAndAnonymousWirePreserveKeyword() throws {
        // Independent vector from fixed meowmusic migu/client_test.go (Python
        // generated there), not computed by another call to the Swift signer.
        let request = try MusicNativeMiguSearch.request(query: "Fixture & Test", page: 2,
            now: Date(timeIntervalSince1970: 1_700_000_000))
        XCTAssertEqual(request.headers["sign"], "706491f168cf2cc1a56ae7a4bf33c5b2")
        XCTAssertEqual(request.headers["timestamp"], "1700000000000")
        XCTAssertEqual(request.headers["deviceId"], "963B7AA0D21511ED807EE5846EC87D20")
        XCTAssertEqual(request.headers["uiVersion"], "A_music_3.6.1")
        XCTAssertEqual(request.headers["channel"], "0146921")
        XCTAssertEqual(request.url.scheme, "https"); XCTAssertEqual(request.url.host, "jadeite.migu.cn")
        XCTAssertEqual(request.url.path, "/music_search/v3/search/searchAll")
        XCTAssertEqual(request.method, "GET"); XCTAssertNil(request.body)
        XCTAssertNil(request.headers["Cookie"]); XCTAssertNil(request.headers["Authorization"])
        let query = Dictionary(uniqueKeysWithValues: try XCTUnwrap(URLComponents(url: request.url, resolvingAgainstBaseURL: false)?.queryItems).map { ($0.name, $0.value ?? "") })
        XCTAssertEqual(query["text"], "Fixture & Test"); XCTAssertEqual(query["pageNo"], "2")
        XCTAssertEqual(query["pageSize"], "30"); XCTAssertEqual(query["sid"], "USS")
        XCTAssertEqual(query["isCorrect"], "0"); XCTAssertEqual(query["isCopyright"], "1")
        XCTAssertEqual(query["searchSwitch"], #"{"song":1,"album":0,"singer":0,"tagSong":1,"mvSong":0,"bestShow":1,"songlist":0,"lyricSong":0}"#)
        let special = try MusicNativeMiguSearch.request(query: "A + 中文 & %", page: 1, timestamp: "1700000000000")
        XCTAssertTrue(special.url.absoluteString.contains("%2B")); XCTAssertTrue(MusicNativeMiguSearch.permits(special))
    }

    func testGroupsAreNotTruncatedAndPaginationCountsGroupsRatherThanEditions() async throws {
        var groups: [[MJ]] = []
        for index in 0..<30 {
            let firstID: Int = 1000 + index * 2
            let firstCopyrightID: Int = 600000 + index * 2
            let first: MJ = MusicNativeMiguSearchFixture.song(
                id: String(firstID), copyrightID: String(firstCopyrightID))
            let second: MJ = MusicNativeMiguSearchFixture.song(
                id: String(firstID + 1), copyrightID: String(firstCopyrightID + 1))
            groups.append([first, second])
        }
        let tail = MusicNativeMiguSearchFixture.song(id: "2000", copyrightID: "700000")
        let http = NativeMusicHTTPFixture([
            MusicNativeMiguSearchFixture.page(groups, total: 31),
            MusicNativeMiguSearchFixture.page([[tail, tail]], total: 31)
        ])
        let service = try await NativeMusicFixture.service(http: http)
        let first = try await service.search(platform: "mg", query: "Synthetic", page: 1, limit: 30)
        XCTAssertEqual(first.items.count, 60); XCTAssertEqual(first.total, 31); XCTAssertEqual(first.nextPage, 2)
        XCTAssertEqual(Set(first.items.flatMap(\.sources).map(\.nativeId)).count, 60)
        let second = try await service.search(platform: "mg", query: "Synthetic", page: 2, limit: 30)
        XCTAssertEqual(second.items.count, 1); XCTAssertEqual(second.total, 31); XCTAssertNil(second.nextPage)
        XCTAssertEqual(second.items[0].sources[0].nativeId, "2000")
        let calls = await http.calls(); XCTAssertEqual(calls.count, 2)
        await service.close()
    }

    func testShortRawGroupPageEndsAndFullPageAtTotalEnds() throws {
        let first = MusicNativeMiguSearchFixture.song(id: "100", copyrightID: "600100")
        let editions = (0..<40).map {
            MusicNativeMiguSearchFixture.song(id: String(1000 + $0), copyrightID: String(600000 + $0))
        }
        let short = try MusicNativeMiguSearch.decode(
            MusicNativeMiguSearchFixture.page([Array(editions.prefix(20)), Array(editions.suffix(20))], total: 100), page: 1)
        XCTAssertEqual(short.items.count, 40)
        XCTAssertNil(short.nextPage)
        let full = try MusicNativeMiguSearch.decode(
            MusicNativeMiguSearchFixture.page(Array(repeating: [first], count: 30), total: 30), page: 1)
        XCTAssertEqual(full.items.count, 1)
        XCTAssertNil(full.nextPage)
        let more = try MusicNativeMiguSearch.decode(
            MusicNativeMiguSearchFixture.page(Array(repeating: [first], count: 30), total: 31), page: 1)
        XCTAssertEqual(more.items.count, 1); XCTAssertEqual(more.nextPage, 2)
        let exceeded = try MusicNativeMiguSearch.decode(
            MusicNativeMiguSearchFixture.page(Array(repeating: [first], count: 30), total: 29), page: 1)
        XCTAssertNil(exceeded.nextPage)
        let empty = try MusicNativeMiguSearch.decode(MusicNativeMiguSearchFixture.page([], total: 100), page: 1)
        XCTAssertTrue(empty.items.isEmpty); XCTAssertNil(empty.nextPage)
        XCTAssertThrowsError(try MusicNativeMiguSearch.decode(
            MusicNativeMiguSearchFixture.page(Array(repeating: [first], count: 31), total: 31), page: 1)) {
            XCTAssertEqual($0 as? MusicServiceError, .upstreamInvalid)
        }
    }
    @MainActor func testShortExpandedEmptyAndFullTotalGroupPagesDoNotIssueAnotherSearch() async throws {
        let rows = (0..<40).map {
            MusicNativeMiguSearchFixture.song(id: String(1000 + $0), copyrightID: String(600000 + $0),
                title: "Synthetic \($0)")
        }
        for (groups, total) in [
            ([[MJ]](), 100),
            ([Array(rows.prefix(20)), Array(rows.suffix(20))], 100),
            (rows.prefix(30).map { [$0] }, 30),
            (rows.prefix(30).map { [$0] }, 29)
        ] {
            let http = NativeMusicHTTPFixture([MusicNativeMiguSearchFixture.page(groups, total: total)])
            let service = try await NativeMusicFixture.service(http: http)
            let results = MusicSearchResultsModel(readSongs: { platform, query, page, limit in
                try await service.search(platform: platform.rawValue, query: query, page: page, limit: limit)
            }, readPlaylists: { _, _, _, _ in throw MusicServiceError.unsupported },
                readRecording: { _ in throw MusicServiceError.unsupported })
            results.setAccess(available: true, generation: UUID()); results.appear()
            results.submit("Synthetic", preferences: .init(songPlatforms: [.migu], searchPlaylists: false,
                pages: 10, concurrency: 1))
            await results.finishPendingReads()
            results.more(.songs); await results.finishPendingReads()
            XCTAssertEqual(results.songs.count, groups.reduce(0) { $0 + $1.count })
            XCTAssertFalse(results.moreSongs); XCTAssertTrue(results.songFailures.isEmpty)
            let calls = await http.calls(); XCTAssertEqual(calls.count, 1)
            results.disappear()
            await service.close()
        }
    }

    func testExpandedPageReservesAllHandlesBeforeEqualTimestampEviction() async throws {
        let store = NativeMusicMemoryDocument(), service = try await NativeMusicFixture.service(store)
        let native = try MusicNativeMiguSearch.song(MusicNativeMiguSearchFixture.song())
        let page = MusicNativeTrackPage(items: Array(repeating: native, count: 200))
        for _ in 0..<3 { _ = try await service.page(page, maximumItems: 200) }
        let latest = try await service.page(page, maximumItems: 200)
        XCTAssertEqual(latest.items.count, 200); XCTAssertEqual(store.snapshot().2, 0)
        // The fixed clock makes every old/new handle timestamp identical.
        // Every handle in the returned page must still be usable as a source.
        let state = try await service.changeQueue(.replace(latest.items.map(\.ref), index: 199), expectedRevision: 0)
        XCTAssertEqual(state.total, 200)
        let queue = try await service.queue()
        XCTAssertEqual(queue.currentId, queue.entries.last?.id)
        XCTAssertEqual(Set(queue.entries.map(\.id)).count, 200)
        XCTAssertEqual(store.snapshot().2, 1)
        await service.close()
    }

    func testNativeLocatorKeepsCopyrightContentFormatsButNeverPlayableURLsOrCredentials() throws {
        var raw = MusicNativeMiguSearchFixture.song()
        raw["Cookie"] = .string("synthetic-not-a-cookie"); raw["token"] = .string("synthetic-not-a-token")
        raw["url"] = .string("https://private.invalid/audio?signature=synthetic")
        raw["lrcUrl"] = .string("https://private.invalid/lyrics?signature=synthetic")
        let track = try MusicNativeMiguSearch.song(raw), native = try MJ.parse(track.data)
        let clean = try MusicModel.normalize(track),
            locator = try MusicModel.locator(MusicModel.sourceData(clean["sources"].array[0]))
        XCTAssertEqual(locator["songmid"].string, "123"); XCTAssertEqual(locator["songId"].string, "123")
        XCTAssertEqual(locator["copyrightId"].string, "600123"); XCTAssertEqual(locator["contentId"].string, "60090001")
        XCTAssertEqual(locator["audioFormats"].array[0]["aformat"].string, "020010")
        XCTAssertEqual(locator["audioFormats"].array[0]["hash"].string, "synthetic-checksum")
        XCTAssertEqual(native["types"].array.map { $0["type"].string }, ["128k", "320k", "flac24bit"])
        XCTAssertEqual(native["img"].string, "https://d.musicapp.migu.cn/cover.jpg")
        XCTAssertFalse(clean.json.contains("private.invalid")); XCTAssertFalse(clean.json.contains("synthetic-not"))
        try MusicModel.rejectSecrets(clean)
        var alternate = raw
        alternate.remove("audioFormats"); alternate.remove("duration")
        alternate["length"] = .string("00:03:00")
        alternate["newRateFormats"] = .array([.obj([("formatType", .string("SQ")), ("size", .string("12345"))])])
        let data = try MJ.parse(MusicNativeMiguSearch.song(alternate).data)
        XCTAssertEqual(data["duration"].int, 180); XCTAssertEqual(data["types"].array[0]["type"].string, "flac")
        XCTAssertEqual(data["newRateFormats"].array.count, 1)
    }

    func testMalformedGroupsAndAmbiguousNativeAliasesFailWithoutFabricatingEmpty() throws {
        let row = MusicNativeMiguSearchFixture.song()
        var nested = MusicNativeMiguSearchFixture.page([[row]], total: 1)
        nested = .obj([("code", nested["code"]), ("data", .obj([("songResultData", nested["songResultData"])]))])
        XCTAssertEqual(try MusicNativeMiguSearch.decode(nested, page: 1).items.count, 1)
        XCTAssertTrue(try MusicNativeMiguSearch.decode(MusicNativeMiguSearchFixture.page([], total: 0), page: 1).items.isEmpty)
        for bad in [MJ.obj([("code", .string("000000"))]), MJ.obj([("code", .string("error"))]),
                    MusicNativeMiguSearchFixture.page([[]], total: 1),
                    MusicNativeMiguSearchFixture.page(Array(repeating: [row], count: 31), total: 31),
                    MusicNativeMiguSearchFixture.page([[row, MusicNativeMiguSearchFixture.song(id: "124")]], total: 1),
                    MusicNativeMiguSearchFixture.page([[row, MusicNativeMiguSearchFixture.song(copyrightID: "600124")]], total: 1)] {
            XCTAssertThrowsError(try MusicNativeMiguSearch.decode(bad, page: 1))
        }
        // A group may contain many editions; no candidate-total cap truncates
        // distinct recordings in one page.
        let distinct = (0..<201).map {
            MusicNativeMiguSearchFixture.song(id: String(1000 + $0), copyrightID: String(600000 + $0))
        }
        let editions = try MusicNativeMiguSearch.decode(
            MusicNativeMiguSearchFixture.page([distinct], total: 1), page: 1)
        XCTAssertEqual(editions.items.count, 201)
        XCTAssertEqual(Set(editions.items.map { $0.data }).count, 201)
        XCTAssertEqual(editions.total, 1); XCTAssertNil(editions.nextPage)
        // Repeating one copyright edition in a group still collapses to one
        // recording, independently of the no-total-cap behavior above.
        let repeated = try MusicNativeMiguSearch.decode(
            MusicNativeMiguSearchFixture.page([Array(repeating: row, count: 201)], total: 1), page: 1)
        XCTAssertEqual(repeated.items.count, 1)
        XCTAssertEqual(repeated.total, 1); XCTAssertNil(repeated.nextPage)
        var bad = row; bad["songId"] = .string("https://evil.invalid")
        XCTAssertThrowsError(try MusicNativeMiguSearch.song(bad))
        bad = row; bad["copyrightId"] = .null
        XCTAssertThrowsError(try MusicNativeMiguSearch.song(bad))
        XCTAssertThrowsError(try MusicNativeMiguSearch.request(query: " ", page: 1, timestamp: "1"))
        XCTAssertThrowsError(try MusicNativeMiguSearch.request(query: "q", page: 0, timestamp: "1"))
        XCTAssertThrowsError(try MusicNativeMiguSearch.request(query: "q", page: 1, timestamp: "one\r\ntwo"))
    }

    func testExactRouteRejectsHeaderInjectionCredentialsWrongSignatureAndArbitraryHosts() async throws {
        let request = try MusicNativeMiguSearch.request(query: "Synthetic", page: 1, timestamp: "1700000000000")
        let transport = URLSessionMusicNativeHTTPTransport(); transport.close()
        XCTAssertTrue(MusicNativeMiguSearch.permits(request))
        do { _ = try await transport.send(request); XCTFail("closed") }
        catch { XCTAssertEqual(error as? MusicServiceError, .closed) }
        var failures: [MusicNativeHTTPRequest] = [
            .init(url: request.url, method: "POST", headers: request.headers),
            .init(url: URL(string: request.url.absoluteString + "&text=changed")!, headers: request.headers),
            .init(url: URL(string: request.url.absoluteString.replacingOccurrences(of: "jadeite.migu.cn", with: "jadeite.migu.cn.evil.invalid"))!, headers: request.headers),
            .init(url: URL(string: request.url.absoluteString.replacingOccurrences(of: "https:", with: "http:"))!, headers: request.headers)
        ]
        for (key, value) in [("Cookie", "synthetic"), ("Authorization", "synthetic"), ("sign", "not-the-signature"),
                             ("timestamp", "old\r\nnew"), ("deviceId", "wrong\0device"), ("channel", "wrong\nchannel")] {
            var headers = request.headers; headers[key] = value
            failures.append(.init(url: request.url, headers: headers))
        }
        for bad in failures {
            XCTAssertFalse(MusicNativeMiguSearch.permits(bad))
            do { _ = try await transport.send(bad); XCTFail("invalid") }
            catch { XCTAssertEqual(error as? MusicServiceError, .invalidRequest) }
        }
    }

    func testEpochCancellationAndLimitFailureNeverRetrySearchOrCreateBusinessWrites() async throws {
        for cancel in [false, true] {
            let gate = NativeMusicGate()
            let body = MusicNativeMiguSearchFixture.page([[MusicNativeMiguSearchFixture.song()]], total: 1)
            let http = MusicNativeKuwoSearchTransport(.init(status: 200, data: body.bytes), gate: gate)
            let platforms = MusicNativePlatforms(transport: http, qq: NativeMusicQQFixture(), clock: { NativeMusicFixture.date })
            let task = Task { try await platforms.search(platform: "mg", query: "Synthetic", page: 1, limit: 30) }
            await gate.started()
            if cancel { task.cancel() } else { await platforms.revoke() }
            await gate.release()
            do { _ = try await task.value; XCTFail("old result") }
            catch {
                if cancel { XCTAssertTrue(error is CancellationError) }
                else { XCTAssertEqual(error as? MusicServiceError, .staleResult) }
            }
            let calls = await http.calls(); XCTAssertEqual(calls.count, 1)
        }
        let store = NativeMusicMemoryDocument()
        let http = MusicNativeKuwoSearchTransport(.init(status: 429, data: Data()))
        let service = try await NativeMusicFixture.service(store, http: http)
        do { _ = try await service.search(platform: "mg", query: "Synthetic", page: 1, limit: 30); XCTFail("limited") }
        catch { XCTAssertEqual(error as? MusicServiceError, .rateLimited) }
        let calls = await http.calls(); XCTAssertEqual(calls.count, 1); XCTAssertEqual(store.snapshot().2, 0)
        await service.close()
    }

    func testAutomaticConsumerDiscoversKWOrMGAndResolvesOnlyTheSelectedLXPlatform() async throws {
        for platform in ["kw", "mg"] {
            let store = try MusicNativeCandidateDiscoveryFixture.store(), scripts = MusicNativeCandidateDiscoveryScripts()
            await scripts.supported([platform])
            let http = MusicNativeCandidateDiscoveryHTTP(), qq = MusicNativeCandidateDiscoveryQQ()
            let service = try await NativeMusicFixture.service(store, scripts: scripts, qq: qq, http: http)
            let consumer = MusicCandidateDiscoveryConsumer(provider: service)
            let result = try await consumer.resolve(occurrenceID: "entry-1", expectedQueueRevision: 1, mode: .automatic())
            XCTAssertEqual(result.target.selection.siteId, platform); XCTAssertEqual(result.attempts, 1)
            let searches = await http.calls(); XCTAssertEqual(Set(searches), ["wy", "kg", "kw", "mg"])
            let requests = await scripts.calls(); XCTAssertEqual(requests.count, 1)
            XCTAssertEqual(requests[0].siteID, platform)
            XCTAssertEqual(requests[0].installationID, MusicNativeCandidateDiscoveryFixture.installation)
            let native = try MJ.parse(requests[0].musicInfo)
            XCTAssertEqual(native["songmid"].string, "123")
            if platform == "mg" { XCTAssertEqual(native["copyrightId"].string, "600123") }
            let queue = try await service.queue()
            XCTAssertEqual(queue.currentId, "entry-1"); XCTAssertEqual(queue.revision, 2)
            XCTAssertEqual(Set(queue.entries[0].track.sources.map(\.platform)), ["wy", "tx", "kg", "kw", "mg"])
            XCTAssertEqual(store.snapshot().2, 1, "same queue CAS writer; no search-side library/played write")
            let played = try await service.playedSongs(); XCTAssertTrue(played.isEmpty)
            await service.close()
        }
    }
}

enum MusicNativeMiguSearchFixture {
    static func song(id: String = "123", copyrightID: String = "600123", title: String = "Synthetic recording") -> MJ {
        .obj([("songId", .string(id)), ("copyrightId", .string(copyrightID)), ("contentId", .string("60090001")),
            ("name", .string(title)), ("singerList", .array([.obj([("name", .string("Fixture"))])])),
            ("albumName", .string("Synthetic album")), ("albumId", .string("45")), ("duration", .n(180)), ("img3", .string("/cover.jpg")),
            ("audioFormats", .array([
                .obj([("formatType", .string("PQ")), ("asize", .string("1234567")), ("aformat", .string("020010")),
                    ("hash", .string("synthetic-checksum")), ("url", .string("https://private.invalid/signed"))]),
                .obj([("formatType", .string("HQ")), ("asize", .string("3456789"))]),
                .obj([("formatType", .string("ZQ24")), ("isize", .string("8000000"))])
            ]))])
    }
    static func page(_ groups: [[MJ]], total: Int) -> MJ {
        .obj([("code", .string("000000")), ("songResultData", .obj([
            ("totalCount", .string(String(total))), ("resultList", .array(groups.map(MJ.array)))
        ]))])
    }
}
