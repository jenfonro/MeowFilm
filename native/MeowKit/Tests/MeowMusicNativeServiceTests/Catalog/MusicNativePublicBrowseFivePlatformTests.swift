import Foundation
import XCTest
import MeowMusicFeature
@testable import MeowMusicNativeService

final class MusicNativePublicBrowseFivePlatformTests: XCTestCase {
    func testBothSearchesUseIndependentPlaylistWireAndKeepCursorPlatformQueryAndNativeID() async throws {
        for platform in MusicNativePublicBrowseFiveFixture.platforms {
            let http = MusicNativePublicBrowseFiveHTTP(platform, searchTotal: 31), store = NativeMusicMemoryDocument()
            let accounts = NativeMusicAccountFixture(), service = try await NativeMusicFixture.service(store, accounts: accounts, http: http)
            let query = "A + B & 中文"
            let first = try await service.publicPlaylists(platform: platform, query: query, cursor: nil)
            XCTAssertEqual(first.items.count, 30); XCTAssertEqual(first.total, 31)
            XCTAssertNotNil(first.cursor)
            XCTAssertEqual(first.items[0].nativeID, MusicNativePublicBrowseFiveFixture.playlistID)
            XCTAssertEqual(first.items[0].id, platform.rawValue + ":playlist:" + MusicNativePublicBrowseFiveFixture.playlistID)
            XCTAssertEqual(first.items[0].artwork?.scheme, "https")
            let cursor = try XCTUnwrap(first.cursor)
            let second = try await service.publicPlaylists(platform: platform, query: query, cursor: cursor)
            XCTAssertEqual(second.items.count, 1); XCTAssertNil(second.cursor)
            for (target, text) in [(platform, "other"), (platform == .kuwo ? .migu : .kuwo, query)] {
                do { _ = try await service.publicPlaylists(platform: target, query: text, cursor: cursor); XCTFail("unbound cursor") }
                catch { XCTAssertEqual(error as? MusicServiceError, .staleResult) }
            }
            let calls = await http.calls()
            XCTAssertEqual(calls.count, 2)
            let values = Dictionary(uniqueKeysWithValues: try XCTUnwrap(URLComponents(url: calls[0].url, resolvingAgainstBaseURL: false)?.queryItems)
                .map { ($0.name, $0.value ?? "") })
            XCTAssertNil(calls[0].headers["Cookie"]); XCTAssertNil(calls[0].headers["Authorization"])
            if platform == .kuwo {
                XCTAssertEqual(values["ft"], "playlist"); XCTAssertEqual(values["all"], query); XCTAssertEqual(values["pn"], "0")
                XCTAssertEqual(first.items[0].title, "Synthetic & mix")
                XCTAssertFalse(MusicNativeKuwoSearch.permits(calls[0]))
            } else {
                XCTAssertEqual(values["searchSwitch"], MusicNativePublicBrowseMigu.switches)
                XCTAssertEqual(values["isCorrect"], "1"); XCTAssertEqual(values["text"], query)
                XCTAssertEqual(calls[0].headers["sign"],
                    MusicNativeMiguSearch.signature(query: query, timestamp: try XCTUnwrap(calls[0].headers["timestamp"])))
                XCTAssertFalse(MusicNativeMiguSearch.permits(calls[0]))
            }
            let accountCalls = await accounts.events(); XCTAssertTrue(accountCalls.isEmpty)
            XCTAssertEqual(store.snapshot().1, 0); XCTAssertEqual(store.snapshot().2, 0)
            await service.close()
        }
    }

    func testNewRouteAllowlistIsActuallyAdmittedByClosedNativeTransportAndRejectsEscapes() async throws {
        let requests = [
            try MusicNativePublicBrowseKuwo.search(query: "A + B", page: 2, limit: 30),
            try MusicNativePublicBrowseKuwo.detail(id: "42", page: 1),
            try MusicNativePublicBrowseMigu.search(query: "A + B", page: 2, now: NativeMusicFixture.date),
            try MusicNativePublicBrowseMigu.detail(id: "42", page: nil),
            try MusicNativePublicBrowseMigu.detail(id: "42", page: 2)
        ]
        let transport = URLSessionMusicNativeHTTPTransport(); transport.close()
        for good in requests {
            XCTAssertTrue(MusicNativePublicBrowseRoutes.allows(good))
            do { _ = try await transport.send(good); XCTFail("closed") }
            catch { XCTAssertEqual(error as? MusicServiceError, .closed) }
            var headers = good.headers; headers["Cookie"] = "synthetic"
            var failures: [MusicNativeHTTPRequest] = [
                .init(url: good.url, method: "POST", headers: good.headers),
                .init(url: good.url, headers: headers),
                .init(url: good.url, headers: good.headers, body: Data()),
                .init(url: URL(string: good.url.absoluteString.replacingOccurrences(of: "https:", with: "http:"))!, headers: good.headers),
                .init(url: URL(string: good.url.absoluteString + "&playlistId=other")!, headers: good.headers),
                .init(url: URL(string: good.url.absoluteString + "&url=https://other.invalid")!, headers: good.headers)
            ]
            var url = URLComponents(url: good.url, resolvingAgainstBaseURL: false)!
            url.host = (url.host ?? "") + ".evil.invalid"
            failures.append(.init(url: url.url!, headers: good.headers))
            for bad in failures {
                XCTAssertFalse(MusicNativePublicBrowseRoutes.allows(bad))
                do { _ = try await transport.send(bad); XCTFail("invalid route") }
                catch { XCTAssertEqual(error as? MusicServiceError, .invalidRequest) }
            }
        }
    }

    func testKuwoPlaylistSearchRejectsUntrustedPageAndSongOffsetOverflow() throws {
        let good = try MusicNativePublicBrowseKuwo.search(query: "Synthetic", page: 1)
        var url = try XCTUnwrap(URLComponents(url: good.url, resolvingAgainstBaseURL: false))
        url.queryItems = url.queryItems?.map { $0.name == "pn" ? .init(name: "pn", value: String(Int.max)) : $0 }
        let malformed = MusicNativeHTTPRequest(url: try XCTUnwrap(url.url), headers: good.headers)
        XCTAssertFalse(MusicNativePublicBrowseKuwo.permits(malformed))
        XCTAssertFalse(MusicNativePublicBrowseRoutes.allows(malformed))
        XCTAssertThrowsError(try MusicNativePublicBrowseKuwo.detail(id: "42", page: Int.max / 1000))
        XCTAssertThrowsError(try MusicNativePublicBrowseMigu.detail(id: "42", page: Int.max / 50))
    }

    func testFullDoubleReadIncludesTailAndDetectsMembershipOwnerCountAndMissingMetadata() async throws {
        for platform in MusicNativePublicBrowseFiveFixture.platforms {
            let total = platform == .kuwo ? 1001 : 51
            for fault in ["tail", "reorder", "owner", "count", "short", "wrongID", "metadata"] + (platform == .migu ? ["copyright", "published"] : []) {
                let store = NativeMusicMemoryDocument(), http = MusicNativePublicBrowseFiveHTTP(platform, total: total, fault: fault)
                let service = try await NativeMusicFixture.service(store, http: http)
                do { _ = try await service.publicDetail(MusicNativePublicBrowseFiveFixture.collection(platform), cursor: nil); XCTFail(fault) }
                catch {
                    let failure = try XCTUnwrap(error as? MusicServiceError)
                    XCTAssertTrue([MusicServiceError.staleResult, .targetChanged, .upstreamInvalid].contains(failure), fault)
                }
                XCTAssertEqual(store.snapshot().2, 0, fault)
                await service.close()
            }
        }
    }

    func testDetailCursorPreservesOrderMetadataAndRepeatedOccurrencesAcrossPages() async throws {
        for platform in MusicNativePublicBrowseFiveFixture.platforms {
            let http = MusicNativePublicBrowseFiveHTTP(platform, repeats: true)
            let service = try await NativeMusicFixture.service(http: http)
            let collection = MusicNativePublicBrowseFiveFixture.collection(platform)
            let first = try await service.publicDetail(collection, cursor: nil)
            let second = try await service.publicDetail(collection, cursor: XCTUnwrap(first.cursor))
            let last = try await service.publicDetail(collection, cursor: XCTUnwrap(second.cursor))
            XCTAssertEqual([first.tracks.count, second.tracks.count, last.tracks.count], [30, 30, 5])
            XCTAssertEqual(first.total, 65); XCTAssertNil(last.cursor)
            XCTAssertEqual(first.snapshot, second.snapshot); XCTAssertEqual(second.snapshot, last.snapshot)
            XCTAssertEqual(first.tracks[0].songId, first.tracks[2].songId)
            XCTAssertEqual(first.tracks[0].sources.first?.platform, platform.rawValue)
            XCTAssertEqual(first.tracks[0].duration, 180)
            let rows = try await service.publicQueueTracks(.init(collection: collection, snapshot: first.snapshot))
            XCTAssertEqual(rows.count, 65); XCTAssertEqual(rows[0].data, rows[2].data)
            let native = try MJ.parse(rows[0].data)
            XCTAssertEqual(native["types"].array.first?["type"].string, "128k")
            if platform == .migu {
                XCTAssertEqual(native["copyrightId"].string, "6001")
                XCTAssertEqual(native["contentId"].string, "9001")
                XCTAssertEqual(native["audioFormats"].array.first?["aformat"].string, "020010")
                XCTAssertFalse(native.json.contains("private.invalid"))
            } else { XCTAssertEqual(native["singer"].string, "A、B") }
            let calls = await http.calls()
            XCTAssertEqual(calls.count, platform == .kuwo ? 2 : 6, "all pages and the queue reuse one complete double-verified snapshot")
            await service.close()
        }
    }

    func testEmptyListIsVerifiedAndLargePlaylistIsAcceptedWhileUnsupportedAlbumFails() async throws {
        for platform in MusicNativePublicBrowseFiveFixture.platforms {
            let empty = MusicNativePublicBrowseFiveHTTP(platform, total: 0, searchTotal: 0)
            let service = try await NativeMusicFixture.service(http: empty)
            let search = try await service.publicPlaylists(platform: platform, query: "empty", cursor: nil)
            XCTAssertTrue(search.items.isEmpty); XCTAssertNil(search.cursor)
            let page = try await service.publicDetail(MusicNativePublicBrowseFiveFixture.collection(platform), cursor: nil)
            XCTAssertTrue(page.tracks.isEmpty); XCTAssertEqual(page.total, 0); XCTAssertNil(page.cursor); XCTAssertEqual(page.snapshot.count, 64)
            let calls = await empty.calls(); XCTAssertEqual(calls.count, platform == .kuwo ? 3 : 5)
            do {
                _ = try await service.publicDetail(.init(platform: platform, nativeID: "41", kind: .album, title: "Unsupported"), cursor: nil)
                XCTFail("no evidence for albums")
            } catch { XCTAssertEqual(error as? MusicServiceError, .unsupported) }
            let unchanged = await empty.calls(); XCTAssertEqual(unchanged.count, calls.count)
            await service.close()
            let large = MusicNativePublicBrowseFiveHTTP(platform, total: 2001)
            let complete = try await NativeMusicFixture.service(http: large)
            let largePage = try await complete.publicDetail(MusicNativePublicBrowseFiveFixture.collection(platform), cursor: nil)
            XCTAssertEqual(largePage.total, 2001); XCTAssertEqual(largePage.tracks.count, 30)
            await complete.close()
        }
    }

    func testShortAndEmptySearchPagesEndWhileFullPagesKeepAccurateCursors() throws {
        for platform in MusicNativePublicBrowseFiveFixture.platforms {
            func decode(_ root: MJ, page: Int) throws -> MusicPublicBrowseQQSearch {
                if platform == .kuwo { return try MusicNativePublicBrowseKuwo.searchPage(root.bytes, page: page) }
                return try MusicNativePublicBrowseMigu.searchPage(root, page: page)
            }
            let short = try decode(MusicNativePublicBrowseFiveFixture.search(platform, page: 1, total: 31, rows: 2), page: 1)
            XCTAssertEqual(short.items.count, 2); XCTAssertNil(short.nextPage)
            let empty = try decode(MusicNativePublicBrowseFiveFixture.search(platform, page: 1, total: 31, rows: 0), page: 1)
            XCTAssertTrue(empty.items.isEmpty); XCTAssertNil(empty.nextPage)
            XCTAssertThrowsError(try decode(MusicNativePublicBrowseFiveFixture.search(platform, page: 1, total: 31, rows: 31), page: 1)) {
                XCTAssertEqual($0 as? MusicServiceError, .upstreamInvalid)
            }
            let fullEnd = try decode(MusicNativePublicBrowseFiveFixture.search(platform, page: 200, total: 6000), page: 200)
            XCTAssertEqual(fullEnd.items.count, 30); XCTAssertNil(fullEnd.nextPage)
            let exceeded = try decode(MusicNativePublicBrowseFiveFixture.search(platform, page: 200, total: 5999, rows: 30), page: 200)
            XCTAssertEqual(exceeded.items.count, 30); XCTAssertNil(exceeded.nextPage)
            let later = try decode(MusicNativePublicBrowseFiveFixture.search(platform, page: 200, total: 6001), page: 200)
            XCTAssertEqual(later.items.count, 30); XCTAssertEqual(later.nextPage, 201)
            let last = try decode(MusicNativePublicBrowseFiveFixture.search(platform, page: 201, total: 6001), page: 201)
            XCTAssertEqual(last.items.count, 1); XCTAssertEqual(last.total, 6001); XCTAssertNil(last.nextPage)
        }
    }
    func testKWPlaylistLexicalCompatibilityIsDataOnlyAndMGFailureNeverBecomesAnEmptySuccess() throws {
        let text = #"{'TOTAL':'1','abslist':[{'playlistid':'41','name':'L'amour &amp; mix','nickname':'A&amp;B','songnum':'1'}]}"#
        let page = try MusicNativePublicBrowseKuwo.searchPage(Data(text.utf8), page: 1)
        XCTAssertEqual(page.items.first?.title, "L'amour & mix")
        XCTAssertEqual(page.items.first?.creator, "A&B")
        XCTAssertThrowsError(try MusicNativePublicBrowseKuwo.searchPage(Data((text + ";process.exit()").utf8), page: 1))
        var failed = MusicNativePublicBrowseFiveFixture.search(.migu, page: 1, total: 0)
        failed["code"] = .string("100001")
        XCTAssertThrowsError(try MusicNativePublicBrowseMigu.searchPage(failed, page: 1))
        XCTAssertThrowsError(try MusicNativePublicBrowseWire.playlistID(.string("../41")))
        XCTAssertThrowsError(try MusicNativePublicBrowseWire.count(.n(-1)))
    }
    func testResponseByteBudgetStillRejectsBothNewPlatformsBeforePublishingOrWriting() async throws {
        for platform in MusicNativePublicBrowseFiveFixture.platforms {
            let http = NativeMusicHTTPFixture([.obj([("padding", .string(String(repeating: "x", count: 2 * 1024 * 1024)))])])
            let store = NativeMusicMemoryDocument(), service = try await NativeMusicFixture.service(store, http: http)
            do { _ = try await service.publicPlaylists(platform: platform, query: "Synthetic", cursor: nil); XCTFail("response limit") }
            catch { XCTAssertEqual(error as? MusicServiceError, .limit) }
            let calls = await http.calls(); XCTAssertEqual(calls.count, 1)
            XCTAssertEqual(store.snapshot().2, 0)
            await service.close()
        }
    }
}
