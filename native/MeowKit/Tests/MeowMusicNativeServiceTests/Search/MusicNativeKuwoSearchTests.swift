import Foundation
import XCTest
import MeowMusicFeature
@testable import MeowMusicNativeService

final class MusicNativeKuwoSearchTests: XCTestCase {
    func testFixedAnonymousWireAndMetadataKeepLongIDWithoutNodeOrAccount() async throws {
        let raw = MusicNativeKuwoSearchFixture.song(id: "9007199254740993", title: "A &amp; B", artist: "One&amp;Two")
        let http = NativeMusicHTTPFixture([MusicNativeKuwoSearchFixture.page([raw], total: 61)])
        let service = try await NativeMusicFixture.service(http: http)
        let platforms = try await service.platforms()
        XCTAssertEqual(platforms.map(\.id), ["wy", "tx", "kg", "kw", "mg"])
        XCTAssertTrue(platforms.allSatisfy(\.search))
        XCTAssertEqual(platforms.suffix(2).map(\.lyrics), [true, true])
        XCTAssertEqual(platforms.suffix(2).map(\.accountState), ["unsupported", "unsupported"])
        let before = await http.calls(); XCTAssertTrue(before.isEmpty, "platform selection is passive")
        let page = try await service.search(platform: "kw", query: "A + B & 中文", page: 2, limit: 30)
        XCTAssertEqual(page.total, 61); XCTAssertNil(page.nextPage)
        XCTAssertEqual(page.items[0].sources[0].nativeId, "9007199254740993")
        XCTAssertEqual(page.items[0].title, "A & B"); XCTAssertEqual(page.items[0].artist, "One、Two")
        let calls = await http.calls(), wire = try XCTUnwrap(calls.first)
        let query = Dictionary(uniqueKeysWithValues: try XCTUnwrap(URLComponents(url: wire.url, resolvingAgainstBaseURL: false)?.queryItems).map { ($0.name, $0.value ?? "") })
        XCTAssertEqual(calls.count, 1); XCTAssertEqual(wire.method, "GET"); XCTAssertNil(wire.body)
        XCTAssertEqual(wire.url.scheme, "https"); XCTAssertEqual(wire.url.host, "search.kuwo.cn"); XCTAssertEqual(wire.url.path, "/r.s")
        XCTAssertEqual(query["all"], "A + B & 中文"); XCTAssertTrue(wire.url.absoluteString.contains("%2B"))
        XCTAssertEqual(query["pn"], "1"); XCTAssertEqual(query["rn"], "30")
        XCTAssertEqual(query["ft"], "music"); XCTAssertEqual(query["show_copyright_off"], "1")
        XCTAssertEqual(query["uid"], "794762570"); XCTAssertEqual(query["ver"], "kwplayer_ar_9.2.2.1")
        XCTAssertEqual(wire.headers["Referer"], "https://www.kuwo.cn/")
        XCTAssertNil(wire.headers["Cookie"]); XCTAssertNil(wire.headers["Authorization"])
        let native = try MJ.parse(MusicNativeKuwoSearch.song(raw).data)
        XCTAssertEqual(native["types"].array.map { $0["type"].string }, ["128k", "320k", "flac"])
        XCTAssertEqual(native["_types"]["flac"]["size"].string, "25MB")
        XCTAssertEqual(native["duration"].int, 180)
        let clean = try MusicModel.normalize(.init(platform: "kw", data: native.bytes))
        let locator = try MusicModel.locator(MusicModel.sourceData(clean["sources"].array[0]))
        XCTAssertEqual(locator["songmid"].string, "9007199254740993"); XCTAssertEqual(locator["_types"], native["_types"])
        await service.close()
    }

    func testSearchArtworkSelectsFirstValidCandidateAndSurvivesNormalization() async throws {
        let short = "120/s4s12/76/3598052169.jpg"
        let cover = "https://img1.kuwo.cn/star/albumcover/120/s4s12/76/3598052169.jpg"
        let pic = "https://artwork.example/album.jpg?size=large"
        let img = "https://other.example/album.jpg"
        let samples: [(name: String, fields: MJ, cover: String?)] = [
            ("short only", .obj([("web_albumpic_short", .string(short))]), cover),
            ("second observed path", .obj([("web_albumpic_short", .string("120/s4s43/64/2138370659.jpg"))]),
                "https://img1.kuwo.cn/star/albumcover/120/s4s43/64/2138370659.jpg"),
            ("other size and format", .obj([("web_albumpic_short", .string("512/cover_name-2.webp"))]),
                "https://img1.kuwo.cn/star/albumcover/512/cover_name-2.webp"),
            ("pic wins", .obj([("pic", .string(pic)), ("img", .string(img)), ("web_albumpic_short", .string(short))]), pic),
            ("HTTP pic and public query", .obj([("pic", .string("http://artwork.example/album.jpg?size=large"))]), pic),
            ("img wins", .obj([("img", .string(img)), ("web_albumpic_short", .string(short))]), img),
            ("HTTP img", .obj([("img", .string("http://other.example/album.jpg"))]), img),
            ("empty pic", .obj([("pic", .string("")), ("img", .string(img))]), img),
            ("null pic", .obj([("pic", .null), ("img", .string(img))]), img),
            ("invalid pic", .obj([("pic", .string("not a URL")), ("img", .string(img))]), img),
            ("non-string pic", .obj([("pic", .n(42)), ("img", .string(img))]), img),
            ("credential query pic", .obj([("pic", .string(pic + "&token=synthetic")), ("img", .string(img))]), img),
            ("fragment pic", .obj([("pic", .string(pic + "#cover")), ("img", .string(img))]), img),
            ("userinfo pic", .obj([("pic", .string("https://user@artwork.example/cover.jpg")), ("img", .string(img))]), img),
            ("non-HTTP pic", .obj([("pic", .string("file:///cover.jpg")), ("img", .string(img))]), img),
            ("unsupported port pic", .obj([("pic", .string("https://artwork.example:8443/a.jpg")), ("img", .string(img))]), img),
            ("oversized pic", .obj([("pic", .string("https://artwork.example/" + String(repeating: "a", count: 4096))),
                ("img", .string(img))]), img),
            ("empty candidates", .obj([("pic", .string("")), ("img", .string("")), ("web_albumpic_short", .string(short))]), cover),
            ("invalid candidates", .obj([("pic", .string("invalid")), ("img", .string(img + "?token=synthetic")),
                ("web_albumpic_short", .string(short))]), cover),
            ("non-string candidates", .obj([("pic", .obj()), ("img", .n(12)), ("web_albumpic_short", .string(short))]), cover),
            ("whitespace candidates", .obj([("pic", .string(" ")), ("img", .string("\t")), ("web_albumpic_short", .string(short))]), cover),
            ("valid pic ignores invalid short", .obj([("pic", .string(pic)), ("web_albumpic_short", .string("//other.example/a.jpg"))]), pic),
            ("absent artwork", .obj(), nil),
            ("null artwork", .obj([("pic", .null), ("img", .null), ("web_albumpic_short", .null)]), nil),
            ("invalid artwork", .obj([("pic", .string("invalid")), ("img", .string("file:///a.jpg"))]), nil)
        ]
        try await assertSearchArtwork(samples)
    }

    func testSearchRejectsUnsafeShortArtworkWithoutDroppingSongsOrRequestingDetails() async throws {
        let paths = [
            "", " ", "/120/cover.jpg", "//other.example/cover.jpg", "https://other.example/cover.jpg",
            "http:cover.jpg", ".", "..", "../cover.jpg", "120/../../cover.jpg", "120/./cover.jpg",
            "%2e%2e/cover.jpg", "120/.%2E/cover.jpg", "120/%2f..%2fcover.jpg", "120/%5Ccover.jpg",
            "120\\..\\cover.jpg", "120/cover.jpg?size=large", "120/cover.jpg#cover",
            "120/cover.jpg%3fsize=large", "120/cover.jpg%23cover",
            "120/\ncover.jpg", "120/\rcover.jpg", "120/\tcover.jpg", "120/\0cover.jpg",
            "120/\u{007F}cover.jpg", "120/\u{0085}cover.jpg", "120/%00cover.jpg", "120/%0Acover.jpg",
            "120/%7Fcover.jpg", "120/%C2%85cover.jpg", "120/%ZZcover.jpg", "120/cover%.jpg",
            "120/" + String(repeating: "a", count: 4096) + ".jpg"
        ]
        let samples: [(name: String, fields: MJ, cover: String?)] = paths.enumerated().map { index, path in
            ("unsafe short \(index)", .obj([("web_albumpic_short", .string(path))]), nil)
        }
        try await assertSearchArtwork(samples)
    }

    private func assertSearchArtwork(_ samples: [(name: String, fields: MJ, cover: String?)],
                                     file: StaticString = #filePath, line: UInt = #line) async throws {
        let rows = samples.enumerated().map { index, sample in
            var raw = MusicNativeKuwoSearchFixture.song(id: String(1000 + index), title: "Artwork sample \(index)")
            for (key, value) in sample.fields.fields { raw[key] = value }
            return raw
        }
        let store = NativeMusicMemoryDocument()
        let http = NativeMusicHTTPFixture([MusicNativeKuwoSearchFixture.page(rows, total: rows.count * 3)])
        let service = try await NativeMusicFixture.service(store, http: http)
        let page = try await service.search(platform: "kw", query: "m3", page: 2, limit: rows.count)
        XCTAssertEqual(page.platform, "kw", file: file, line: line)
        XCTAssertEqual(page.query, "m3", file: file, line: line)
        XCTAssertEqual(page.page, 2, file: file, line: line)
        XCTAssertEqual(page.limit, rows.count, file: file, line: line)
        XCTAssertEqual(page.total, rows.count * 3, file: file, line: line)
        XCTAssertEqual(page.nextPage, 3, file: file, line: line)
        XCTAssertEqual(page.items.count, rows.count, file: file, line: line)
        XCTAssertEqual(page.items.map(\.title), rows.indices.map { "Artwork sample \($0)" }, file: file, line: line)
        XCTAssertEqual(page.items.map { $0.artwork?.absoluteString }, samples.map { $0.cover }, file: file, line: line)
        XCTAssertEqual(store.snapshot().2, 0, "search remains read-only", file: file, line: line)
        let playlist = try await service.createPlaylist(name: "Artwork samples", refs: page.items.map(\.ref))
        let restored = try await service.tracks(in: playlist)
        XCTAssertEqual(restored.map(\.songId), page.items.map(\.songId), file: file, line: line)
        XCTAssertEqual(restored.map { $0.artwork?.absoluteString }, samples.map { $0.cover }, file: file, line: line)
        let document = try MusicDocument(store.snapshot().0, runID: "fixture")
        for (index, track) in page.items.enumerated() {
            let sample = samples[index], id = String(1000 + index)
            XCTAssertEqual(track.sources.map(\.platform), ["kw"], sample.name, file: file, line: line)
            XCTAssertEqual(track.sources.map(\.nativeId), [id], sample.name, file: file, line: line)
            XCTAssertEqual(track.sources.map(\.sourceKey), ["[\"kw\",\"\(id)\",\"\"]"], sample.name, file: file, line: line)
            XCTAssertEqual(track.artist, "Fixture", sample.name, file: file, line: line)
            XCTAssertEqual(track.album, "Synthetic album", sample.name, file: file, line: line)
            XCTAssertEqual(track.duration, 180, sample.name, file: file, line: line)
            let song = document.library["library"]["songs"][track.songId]
            XCTAssertEqual(song["cover_url"].string, sample.cover ?? "", sample.name, file: file, line: line)
            let info = try XCTUnwrap(song["sources"].array.first, sample.name, file: file, line: line)["musicInfo"]
            if let cover = sample.cover {
                XCTAssertEqual(info["img"].string, cover, sample.name, file: file, line: line)
            } else {
                XCTAssertFalse(info.contains("img"), sample.name, file: file, line: line)
            }
            XCTAssertEqual(info["songmid"].string, id, sample.name, file: file, line: line)
            XCTAssertEqual(info["types"].array.map { $0["type"].string }, ["128k", "320k", "flac"], sample.name, file: file, line: line)
            XCTAssertEqual(info["_types"]["flac"]["size"].string, "25MB", sample.name, file: file, line: line)
        }
        let calls = await http.calls()
        XCTAssertEqual(calls.count, 1, "only the original search request", file: file, line: line)
        let wire = try XCTUnwrap(calls.first, file: file, line: line)
        XCTAssertEqual(wire.method, "GET", file: file, line: line)
        XCTAssertEqual(wire.url.host, "search.kuwo.cn", file: file, line: line)
        XCTAssertEqual(wire.url.path, "/r.s", file: file, line: line)
        let query = try XCTUnwrap(URLComponents(url: wire.url, resolvingAgainstBaseURL: false)?.queryItems, file: file, line: line)
        XCTAssertEqual(query.first { $0.name == "all" }?.value, "m3", file: file, line: line)
        XCTAssertEqual(query.first { $0.name == "pn" }?.value, "1", file: file, line: line)
        XCTAssertEqual(query.first { $0.name == "rn" }?.value, String(rows.count), file: file, line: line)
        await service.close()
    }

    func testSingleQuotedDataIsNotJavaScriptAndEntitiesAreDecodedOnlyOnce() throws {
        let body = #" {'TOTAL':'1','SHOW':'1','abslist':[{'MUSICRID':'MUSIC_123','SONGNAME':'Don\'t "stop" &amp;lt; &#x4e2d;','ARTIST':'One&amp;Two','ALBUM':'Album','DURATION':'180','N_MINFO':'level:h,bitrate:128,format:mp3,size:2Mb'}]} "#
        let page = try MusicNativeKuwoSearch.decode(Data(body.utf8), page: 1)
        let row = try MJ.parse(page.items[0].data)
        XCTAssertEqual(row["name"].string, "Don't \"stop\" &lt; 中")
        XCTAssertEqual(row["singer"].string, "One、Two")
        for bad in ["{'TOTAL':0};alert(1)", "{'TOTAL':undefined}", "{'TOTAL':0,'abslist':()=>[]}",
                    "{'x':'unterminated}", "{\"TOTAL\":0}{\"abslist\":[]}"] {
            XCTAssertThrowsError(try MusicNativeKuwoSearch.object(Data(bad.utf8)), bad)
        }
        let repeated = try MusicNativeKuwoSearch.object(Data("{'TOTAL':0,'TOTAL':1,'abslist':[]}".utf8))
        XCTAssertEqual(repeated["TOTAL"], .n(1))
        XCTAssertThrowsError(try MusicNativeKuwoSearch.text(.string("&#13;bad"), maximum: 1024))
    }

    func testPaginationKeepsDuplicateOccurrencesAndAcceptsEmptyEndWithoutInvalidQuality() throws {
        let raw = MusicNativeKuwoSearchFixture.song()
        let page = try MusicNativeKuwoSearch.decode(MusicNativeKuwoSearchFixture.page([raw, raw], total: 32).bytes, page: 2)
        XCTAssertEqual(page.items.count, 2); XCTAssertEqual(page.total, 32); XCTAssertNil(page.nextPage)
        XCTAssertEqual(page.items[0].data, page.items[1].data)
        XCTAssertTrue(try MusicNativeKuwoSearch.decode(MusicNativeKuwoSearchFixture.page([], total: 0).bytes, page: 1).items.isEmpty)
        let empty = try MusicNativeKuwoSearch.decode(MusicNativeKuwoSearchFixture.page([], total: 1).bytes, page: 1)
        XCTAssertTrue(empty.items.isEmpty); XCTAssertNil(empty.nextPage)
        var emptyEnd = MusicNativeKuwoSearchFixture.page([], total: 61); emptyEnd["SHOW"] = .string("0")
        let shownEmpty = try MusicNativeKuwoSearch.decode(emptyEnd.bytes, page: 1)
        XCTAssertTrue(shownEmpty.items.isEmpty); XCTAssertNil(shownEmpty.nextPage)
        XCTAssertThrowsError(try MusicNativeKuwoSearch.decode(MusicNativeKuwoSearchFixture.page(Array(repeating: raw, count: 31), total: 31).bytes, page: 1))
        var hidden = MusicNativeKuwoSearchFixture.page([raw], total: 1); hidden["SHOW"] = .string("0")
        XCTAssertThrowsError(try MusicNativeKuwoSearch.decode(hidden.bytes, page: 1))
        var bad = raw; bad.remove("N_MINFO")
        XCTAssertThrowsError(try MusicNativeKuwoSearch.song(bad))
        bad = raw; bad["MUSICRID"] = .string("MUSIC_../123")
        XCTAssertThrowsError(try MusicNativeKuwoSearch.song(bad))
        XCTAssertThrowsError(try MusicNativeKuwoSearch.request(query: String(repeating: "a", count: 201), page: 1))
        let later = try MusicNativeKuwoSearch.request(query: "query", page: 201)
        let parameters = URLComponents(url: later.url, resolvingAgainstBaseURL: false)?.queryItems
        XCTAssertEqual(parameters?.first { $0.name == "pn" }?.value, "200")
        XCTAssertTrue(MusicNativeKuwoSearch.permits(later))
    }

    @MainActor func testShortPageEndsWithoutAnotherRequestAndFullPageAtTotalAlsoEnds() async throws {
        for (rows, total) in [(0, 61), (2, 61), (30, 30), (30, 29)] {
            let raw = (0..<rows).map {
                MusicNativeKuwoSearchFixture.song(id: String(100 + $0), title: "Synthetic \($0)")
            }
            let http = NativeMusicHTTPFixture([MusicNativeKuwoSearchFixture.page(raw, total: total)])
            let service = try await NativeMusicFixture.service(http: http)
            let results = MusicSearchResultsModel(readSongs: { platform, query, page, limit in
                try await service.search(platform: platform.rawValue, query: query, page: page, limit: limit)
            }, readPlaylists: { _, _, _, _ in throw MusicServiceError.unsupported },
                readRecording: { _ in throw MusicServiceError.unsupported })
            results.setAccess(available: true, generation: UUID()); results.appear()
            results.submit("Synthetic", preferences: .init(songPlatforms: [.kuwo], searchPlaylists: false,
                pages: 10, concurrency: 1))
            await results.finishPendingReads()
            results.more(.songs); await results.finishPendingReads()
            XCTAssertEqual(results.songs.count, rows)
            XCTAssertFalse(results.moreSongs); XCTAssertTrue(results.songFailures.isEmpty)
            let calls = await http.calls(); XCTAssertEqual(calls.count, 1)
            results.disappear()
            await service.close()
        }
    }
    func testFullPageBeforeTotalAdvancesExactlyOnePage() throws {
        let row = MusicNativeKuwoSearchFixture.song()
        let page = try MusicNativeKuwoSearch.decode(
            MusicNativeKuwoSearchFixture.page(Array(repeating: row, count: 30), total: 61).bytes, page: 2)
        XCTAssertEqual(page.items.count, 30)
        XCTAssertEqual(page.nextPage, 3)
    }

    func testLimitAboveOneHundredIsRejectedBeforeAnyRequest() async throws {
        let http = NativeMusicHTTPFixture([])
        let service = try await NativeMusicFixture.service(http: http)
        do { _ = try await service.search(platform: "kw", query: "Synthetic", page: 1, limit: 101); XCTFail("limit") }
        catch { XCTAssertEqual(error as? MusicServiceError, .invalidRequest) }
        let calls = await http.calls(); XCTAssertTrue(calls.isEmpty)
        await service.close()
    }

    func testHTTPSRouteAdmissionRejectsHeadersMethodOriginAndQueryWithoutNetworking() async throws {
        let good = try MusicNativeKuwoSearch.request(query: "Synthetic", page: 1)
        XCTAssertTrue(MusicNativeKuwoSearch.permits(good))
        let transport = URLSessionMusicNativeHTTPTransport(); transport.close()
        do { _ = try await transport.send(good); XCTFail("closed") }
        catch { XCTAssertEqual(error as? MusicServiceError, .closed, "valid route reached closed gate") }
        var bad: [MusicNativeHTTPRequest] = [
            .init(url: good.url, method: "POST", headers: good.headers),
            .init(url: good.url, headers: good.headers, body: Data()),
            .init(url: URL(string: good.url.absoluteString.replacingOccurrences(of: "https:", with: "http:"))!, headers: good.headers),
            .init(url: URL(string: good.url.absoluteString.replacingOccurrences(of: "search.kuwo.cn", with: "search.kuwo.cn.evil.invalid"))!, headers: good.headers),
            .init(url: URL(string: good.url.absoluteString + "&all=other")!, headers: good.headers),
            .init(url: URL(string: good.url.absoluteString.replacingOccurrences(of: "/r.s?", with: "/elsewhere?"))!, headers: good.headers)
        ]
        var overflow = URLComponents(url: good.url, resolvingAgainstBaseURL: false)!
        overflow.queryItems = overflow.queryItems?.map {
            $0.name == "pn" ? URLQueryItem(name: "pn", value: String(Int.max)) : $0
        }
        bad.append(.init(url: overflow.url!, headers: good.headers))
        for field in ["Cookie", "Authorization", "X-Unbounded"] {
            var headers = good.headers; headers[field] = "synthetic"
            bad.append(.init(url: good.url, headers: headers))
        }
        for text in ["one\r\ntwo", "one\rtwo", "one\ntwo", "one\0two"] {
            var headers = good.headers; headers["Referer"] = text
            bad.append(.init(url: good.url, headers: headers))
        }
        for request in bad {
            XCTAssertFalse(MusicNativeKuwoSearch.permits(request))
            do { _ = try await transport.send(request); XCTFail("invalid request") }
            catch { XCTAssertEqual(error as? MusicServiceError, .invalidRequest) }
        }
    }

    func testFailuresAreNotRetriedOrReplacedWithAnEmptyPage() async throws {
        // This public endpoint's bare 403 contains no evidence of a login requirement.
        for (status, expected) in [(429, MusicServiceError.rateLimited), (503, .upstreamFailed), (403, .requestRejected)] {
            let http = MusicNativeKuwoSearchTransport(.init(status: status, data: Data()))
            let service = try await NativeMusicFixture.service(http: http)
            do { _ = try await service.search(platform: "kw", query: "Synthetic", page: 1, limit: 30); XCTFail("failure") }
            catch { XCTAssertEqual(error as? MusicServiceError, expected) }
            let calls = await http.calls(); XCTAssertEqual(calls.count, 1)
            XCTAssertEqual(calls[0].url.host, "search.kuwo.cn")
            await service.close()
        }
        let http = MusicNativeKuwoSearchTransport(.init(status: 200, data: Data(repeating: 32, count: 2 * 1024 * 1024 + 1)))
        let service = try await NativeMusicFixture.service(http: http)
        do { _ = try await service.search(platform: "kw", query: "Synthetic", page: 1, limit: 30); XCTFail("budget") }
        catch { XCTAssertEqual(error as? MusicServiceError, .limit) }
        await service.close()
    }

    func testCancellationAndRevocationRejectLatePublicResultsWithoutWriting() async throws {
        for locked in [false, true] {
            let gate = NativeMusicGate(), store = NativeMusicMemoryDocument()
            let body = MusicNativeKuwoSearchFixture.page([MusicNativeKuwoSearchFixture.song()], total: 1)
            let http = MusicNativeKuwoSearchTransport(.init(status: 200, data: body.bytes), gate: gate)
            let service = try await NativeMusicFixture.service(store, http: http)
            let task = Task { try await service.search(platform: "kw", query: "Synthetic", page: 1, limit: 30) }
            await gate.started()
            if locked { service.revokeAccess(generation: 2) } else { task.cancel() }
            await gate.release()
            do { _ = try await task.value; XCTFail("late result accepted") } catch {}
            XCTAssertEqual(store.snapshot().2, 0)
            let calls = await http.calls(); XCTAssertEqual(calls.count, 1)
            if locked {
                try await service.setAccess(available: false, generation: 2)
                try await service.setAccess(available: true, generation: 3)
                let state = try await service.state(); XCTAssertEqual(state.total, 0)
            }
            await service.close()
        }
    }

    func testSearchLibraryQueueAndInjectedResolverKeepSameStableResourceForBothPlatforms() async throws {
        for platform in ["kw", "mg"] {
            // Exercise the current four-record document. The injected resolver
            // is a contract double.
            let initial = MJ.obj([("version", .n(1)), ("scope", .string("device")), ("revision", .n(0)),
                ("records", .obj())])
            let store = NativeMusicMemoryDocument(initial.bytes), scripts = MusicNativeCandidateDiscoveryScripts()
            await scripts.supported(["kw", "mg"])
            let body = platform == "kw"
                ? MusicNativeKuwoSearchFixture.page([MusicNativeKuwoSearchFixture.song()], total: 1)
                : MusicNativeMiguSearchFixture.page([[MusicNativeMiguSearchFixture.song()]], total: 1)
            let accounts = NativeMusicAccountFixture(), http = NativeMusicHTTPFixture([body])
            let service = try await NativeMusicFixture.service(store, scripts: scripts, accounts: accounts, http: http)
            let search = try await service.search(platform: platform, query: "Synthetic", page: 1, limit: 30)
            XCTAssertEqual(store.snapshot().2, 0, "search is read-only")
            let first = try XCTUnwrap(search.items.first)
            let playlist = try await service.createPlaylist(name: "Synthetic", refs: [first.ref])
            let tracks = try await service.tracks(in: playlist)
            XCTAssertEqual(tracks[0].sources, first.sources)
            let state = try await service.changeQueue(.replace([tracks[0].ref, tracks[0].ref], index: 1), expectedRevision: 0)
            let queue = try await service.queue()
            XCTAssertNotEqual(queue.entries[0].id, queue.entries[1].id)
            XCTAssertEqual(state.currentId, queue.entries[1].id)
            let options = (try await service.sourceAvailability(for: tracks[0].ref)).options
            let selected = try XCTUnwrap(options.first)
            XCTAssertEqual(options.count, 1); XCTAssertEqual(selected.siteId, platform)
            XCTAssertEqual(selected.sourceKey, first.sources[0].sourceKey)
            let facts = try await service.playedSongs(); XCTAssertTrue(facts.isEmpty)
            let beforeResolve = store.snapshot()
            _ = try await service.resolve(attemptId: UUID().uuidString.lowercased(), occurrenceId: queue.entries[1].id,
                selection: selected.selection(quality: "128k"))
            let requests = await scripts.calls(); XCTAssertEqual(requests.count, 1)
            let publicRequests = await http.calls(); XCTAssertEqual(publicRequests.count, 1, "only the native search, no implicit lyric request")
            XCTAssertEqual(store.snapshot().0, beforeResolve.0, "URL resolution does not rewrite the library envelope")
            XCTAssertEqual(store.snapshot().2, beforeResolve.2)
            XCTAssertEqual(requests[0].siteID, platform); XCTAssertEqual(requests[0].sourceKey, first.sources[0].sourceKey)
            let locator = try MJ.parse(requests[0].musicInfo)
            XCTAssertEqual(locator["songmid"].string, "123")
            if platform == "mg" {
                XCTAssertEqual(locator["copyrightId"].string, "600123")
                XCTAssertEqual(locator["contentId"].string, "60090001")
                XCTAssertEqual(locator["audioFormats"].array.first?["aformat"].string, "020010")
            }
            let accountCalls = await accounts.events(); XCTAssertTrue(accountCalls.isEmpty)
            let played = try await service.playedSongs(); XCTAssertTrue(played.isEmpty, "resolution is not a played fact")
            let saved = try MJ.parse(XCTUnwrap(store.snapshot().0))
            XCTAssertEqual(saved["version"].int, 1); XCTAssertFalse(saved.json.contains("media.invalid"))
            XCTAssertTrue(Set(saved["records"].fields.map(\.0)).isSubset(
                of: Set(["library", "queue", "history", "playback"].map(MusicModel.key))))
            XCTAssertFalse(saved.json.contains("Cookie")); XCTAssertFalse(saved.json.contains("private.invalid"))
            await service.close()
        }
    }
}

enum MusicNativeKuwoSearchFixture {
    static func song(id: String = "123", title: String = "Synthetic recording", artist: String = "Fixture") -> MJ {
        .obj([("MUSICRID", .string("MUSIC_" + id)), ("SONGNAME", .string(title)), ("ARTIST", .string(artist)),
            ("ALBUM", .string("Synthetic album")), ("ALBUMID", .string("19")), ("DURATION", .string("180")),
            ("N_MINFO", .string("level:ff,bitrate:2000,format:flac,size:25Mb;level:p,bitrate:320,format:mp3,size:6Mb;level:h,bitrate:128,format:mp3,size:2Mb"))])
    }
    static func page(_ rows: [MJ], total: Int) -> MJ {
        .obj([("TOTAL", .string(String(total))), ("SHOW", .string("1")), ("abslist", .array(rows))])
    }
}

actor MusicNativeKuwoSearchTransport: MusicNativeHTTPTransport {
    let response: MusicNativeHTTPResponse
    let gate: NativeMusicGate?
    private var requests: [MusicNativeHTTPRequest] = []
    init(_ response: MusicNativeHTTPResponse, gate: NativeMusicGate? = nil) { self.response = response; self.gate = gate }
    func send(_ request: MusicNativeHTTPRequest) async throws -> MusicNativeHTTPResponse {
        requests.append(request)
        if let gate { await gate.wait() }
        return response
    }
    func calls() -> [MusicNativeHTTPRequest] { requests }
}
