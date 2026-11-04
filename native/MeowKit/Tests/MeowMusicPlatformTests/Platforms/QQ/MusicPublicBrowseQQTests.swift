import XCTest
import Foundation
import MeowMusicFeature
@testable import MeowMusicPlatform

private actor MusicPublicBrowseQQHTTP: MusicAccountHTTPTransport {
    private var requests: [MusicAccountHTTPRequest] = []
    private var reads = 0
    private var largePlaylist = false
    var reorder = false
    var paged = false
    var searchTotal = 1
    var searchRows = 1
    var searchNextPage = -1
    var searchSongs = false
    func changeOnSecondRead() { reorder = true }
    func useShortPages() { paged = true }
    func configureSearch(total: Int, rows: Int, nextPage: Int) {
        searchTotal = total; searchRows = rows; searchNextPage = nextPage
    }
    func configureSongSearch(total: Int, rows: Int, nextPage: Int) {
        searchSongs = true; configureSearch(total: total, rows: rows, nextPage: nextPage)
    }
    func useLargePlaylist() { largePlaylist = true }
    func calls() -> [MusicAccountHTTPRequest] { requests }
    func send(_ request: MusicAccountHTTPRequest) throws -> MusicAccountHTTPResponse {
        requests.append(request)
        let root: [String: Any]
        if request.url.host == "api.tencentmusic.com" {
            let value: [String: Any] = ["code": 0, "data": ["q16": "synthetic-q16", "q36": "synthetic-q36"]]
            root = ["code": 0, "data": String(decoding: try JSONSerialization.data(withJSONObject: value), as: UTF8.self)]
        } else {
            let body = try XCTUnwrap(JSONSerialization.jsonObject(with: request.body ?? Data()) as? [String: Any])
            let rpc = try XCTUnwrap(body["req_0"] as? [String: Any])
            let method = rpc["method"] as? String, params = rpc["param"] as? [String: Any] ?? [:]
            func song(_ mid: String) -> [String: Any] {
                var value: [String: Any] = ["mid": mid, "id": "9007199254740993", "type": 1, "title": mid, "singer": [["name": "Synthetic"]],
                 "album": ["mid": "AlbumMID", "name": "Album"], "interval": 180,
                 "file": ["media_mid": "MediaMID", "size_128mp3": 100, "size_hires": 500]]
                if largePlaylist {
                    value["title"] = String(repeating: "T", count: 1000)
                    value["singer"] = (1...4).map { _ in ["name": String(repeating: "S", count: 250)] }
                    value["album"] = ["mid": "AlbumMID", "name": String(repeating: "A", count: 1000)]
                }
                return value
            }
            let data: [String: Any]
            switch method {
            case "GetSession": data = ["session": ["uid": "synthetic-anonymous", "sid": "synthetic-session"]]
            case "DoSearchForQQMusicMobile":
                XCTAssertEqual(params["search_type"] as? Int, searchSongs ? 0 : 3)
                let body: [String: Any]
                if searchSongs {
                    body = ["item_song": (0..<searchRows).map { song("MID\($0)") }]
                } else {
                    body = ["item_songlist": (0..<searchRows).map {
                        ["tid": 42 + $0, "title": "Synthetic playlist \($0)", "songnum": 3]
                    }]
                }
                data = ["meta": ["sum": searchTotal, "nextpage": searchNextPage], "body": body]
            case "CgiGetDiss":
                reads += 1
                XCTAssertEqual(params["disstid"] as? Int, 42)
                let offset = try XCTUnwrap(params["song_begin"] as? Int), limit = try XCTUnwrap(params["song_num"] as? Int)
                let total = largePlaylist ? 4096 : paged ? 65 : 3
                let rows: [[String: Any]]
                if largePlaylist {
                    rows = (offset..<min(offset + min(limit, 27), total))
                        .map { song(String(format: "MID%04d", $0)) }
                } else if paged {
                    rows = (0..<total).map { song("MID\($0)") }
                } else if reorder && reads == 2 {
                    rows = [song("MID2"), song("MID1"), song("MID1")]
                } else {
                    rows = [song("MID1"), song("MID2"), song("MID1")]
                }
                let songlist = largePlaylist ? rows : Array(rows.dropFirst(offset).prefix(min(limit, 27)))
                let size = songlist.count
                data = ["dirinfo": ["id": 42, "title": "Synthetic playlist", "songnum": total],
                        "total_song_num": total, "songlist_size": size,
                        "hasmore": offset + size < total ? 1 : 0, "songlist": songlist]
            default: throw MusicServiceError.upstreamFailed
            }
            root = ["code": 0, "req_0": ["code": 0, "data": data]]
        }
        return .init(status: 200, data: try JSONSerialization.data(withJSONObject: root))
    }
}
final class MusicPublicBrowseQQTests: XCTestCase {
    func testCurrentMemberPagesUseThirtyLimitAndContinueAtActualOffsetWithoutCompleteRead() async throws {
        let http = MusicPublicBrowseQQHTTP(); await http.useShortPages()
        let service = NativeMusicQQPublicService(transport: http)
        let first = try await service.publicBrowsePlaylistPage(id: "42", offset: 0)
        let second = try await service.publicBrowsePlaylistPage(id: "42", offset: 27)
        XCTAssertEqual(first.tracks.count, 27); XCTAssertEqual(second.tracks.count, 27); XCTAssertTrue(second.more)
        let calls = await http.calls()
        let params = try calls.filter { $0.url.host == "u.y.qq.com" }.compactMap { request -> [String: Any]? in
            let root = try MusicQQWire.decode(XCTUnwrap(request.body)), rpc = try MusicQQWire.object(root["req_0"])
            return rpc["method"] as? String == "CgiGetDiss" ? try MusicQQWire.object(rpc["param"]) : nil
        }
        XCTAssertEqual(params.compactMap { $0["song_begin"] as? Int }, [0, 27])
        XCTAssertEqual(params.compactMap { $0["song_num"] as? Int }, [30, 30])
        let all = try await service.publicBrowsePlaylist(id: "42")
        XCTAssertEqual(all.tracks.count, 65); XCTAssertEqual(all.headSignature, first.headSignature)
        await service.close()
    }
    func testWholePlaylistReadStopsAtNativeByteBudgetBeforeNextPage() async throws {
        let http = MusicPublicBrowseQQHTTP()
        await http.useLargePlaylist()
        let service = NativeMusicQQPublicService(transport: http)
        let page = try await service.publicBrowsePlaylistPage(id: "42", offset: 0)
        XCTAssertEqual(page.tracks.count, 27)
        XCTAssertEqual(page.total, 4096)
        XCTAssertEqual(page.collection.count, 4096)
        XCTAssertTrue(page.more)
        let costs = page.tracks.map(\.data.count)
        let rowCost = try XCTUnwrap(costs.first)
        XCTAssertTrue(costs.allSatisfy { $0 == rowCost })
        let before = await http.calls()
        let checkpoint = before.count
        let budgetPages = MusicRemoteQueueSource.maximumBytes / (27 * rowCost) + 1
        let expectedOffsets = (0..<budgetPages).map { 27 * $0 }
        XCTAssertGreaterThanOrEqual(4096, budgetPages * 27)
        do { _ = try await service.publicBrowsePlaylist(id: "42"); XCTFail("Native byte budget exceeded") }
        catch { XCTAssertEqual(error as? MusicServiceError, .limit) }
        let calls = await http.calls()
        let requests = try calls.dropFirst(checkpoint).compactMap { request -> [String: Any]? in
            let root = try MusicQQWire.decode(XCTUnwrap(request.body)), rpc = try MusicQQWire.object(root["req_0"])
            return rpc["method"] as? String == "CgiGetDiss" ? try MusicQQWire.object(rpc["param"]) : nil
        }
        XCTAssertEqual(requests.compactMap { $0["song_begin"] as? Int }, expectedOffsets)
        XCTAssertEqual(requests.count, budgetPages)
        XCTAssertEqual(requests.compactMap { $0["song_num"] as? Int }, Array(repeating: 100, count: budgetPages))
        await service.close()
    }
    func testPublicSearchAndDetailUseExistingDeviceOwnerAndNativeRPCWithoutAccountCookie() async throws {
        let http = MusicPublicBrowseQQHTTP(), service = NativeMusicQQPublicService(transport: http)
        let search = try await service.publicBrowsePlaylists(query: "synthetic", page: 1, limit: 30)
        XCTAssertEqual(search.items[0].nativeID, "42")
        let result = try await service.publicBrowsePlaylist(id: "42")
        XCTAssertEqual(result.tracks.count, 3); XCTAssertEqual(result.tracks[0].data, result.tracks[2].data)
        let first = try XCTUnwrap(JSONSerialization.jsonObject(with: result.tracks[0].data) as? [String: Any])
        XCTAssertEqual(first["qqSongId"] as? String, "9007199254740993")
        XCTAssertEqual(first["songmid"] as? String, "MID1")
        XCTAssertEqual(first["strMediaMid"] as? String, "MediaMID")
        let calls = await http.calls()
        XCTAssertEqual(calls.filter { $0.url.host == "api.tencentmusic.com" }.count, 1)
        XCTAssertTrue(calls.allSatisfy { $0.headers["Cookie"] == nil })
        XCTAssertFalse(calls.contains { String(decoding: $0.body ?? Data(), as: UTF8.self).contains("music.native.accounts.invoke") })
        await service.close()
    }
    func testSameCountReorderFailsWholeSnapshotCheck() async throws {
        let http = MusicPublicBrowseQQHTTP(); await http.changeOnSecondRead()
        let service = NativeMusicQQPublicService(transport: http)
        do { _ = try await service.publicBrowsePlaylist(id: "42"); XCTFail("same-count reorder") }
        catch { XCTAssertEqual(error as? MusicServiceError, .staleResult) }
        await service.close()
    }
    func testPlaylistSearchPaginationFollowsNextPageWithoutTrustingShortPageOrSum() async throws {
        let http = MusicPublicBrowseQQHTTP()
        let service = NativeMusicQQPublicService(transport: http)
        await http.configureSearch(total: 100, rows: 2, nextPage: 2)
        let short = try await service.publicBrowsePlaylists(query: "synthetic", page: 1, limit: 30)
        XCTAssertEqual(short.items.count, 2); XCTAssertEqual(short.total, 100); XCTAssertEqual(short.nextPage, 2)
        await http.configureSearch(total: 30, rows: 30, nextPage: 2)
        let full = try await service.publicBrowsePlaylists(query: "synthetic", page: 1, limit: 30)
        XCTAssertEqual(full.items.count, 30); XCTAssertEqual(full.nextPage, 2)
        await http.configureSearch(total: 100, rows: 30, nextPage: 2)
        let more = try await service.publicBrowsePlaylists(query: "synthetic", page: 1, limit: 30)
        XCTAssertEqual(more.nextPage, 2); XCTAssertEqual(more.total, 100)
        await http.configureSearch(total: 100, rows: 30, nextPage: -1)
        let upstreamEnd = try await service.publicBrowsePlaylists(query: "synthetic", page: 1, limit: 30)
        XCTAssertNil(upstreamEnd.nextPage)
        await http.configureSearch(total: 100, rows: 0, nextPage: 9)
        let empty = try await service.publicBrowsePlaylists(query: "synthetic", page: 1, limit: 30)
        XCTAssertTrue(empty.items.isEmpty); XCTAssertNil(empty.nextPage)
        for nextPage in [1, 0] {
            await http.configureSearch(total: 100, rows: 30, nextPage: nextPage)
            do {
                _ = try await service.publicBrowsePlaylists(query: "synthetic", page: 2, limit: 30)
                XCTFail("Non-progressing nextpage")
            } catch { XCTAssertEqual(error as? MusicServiceError, .upstreamInvalid) }
        }
        await http.configureSearch(total: 100, rows: 31, nextPage: 9)
        do {
            _ = try await service.publicBrowsePlaylists(query: "synthetic", page: 1, limit: 30)
            XCTFail("Overfull raw page")
        } catch { XCTAssertEqual(error as? MusicServiceError, .upstreamInvalid) }
        await service.close()
    }
    func testSongSearchPaginationFollowsNextPageWithoutTrustingShortPageOrSum() async throws {
        let http = MusicPublicBrowseQQHTTP(), service = NativeMusicQQPublicService(transport: http)
        await http.configureSongSearch(total: 100, rows: 2, nextPage: 2)
        let short = try await service.search(query: "synthetic", page: 1, limit: 30)
        XCTAssertEqual(short.items.count, 2); XCTAssertEqual(short.total, 100); XCTAssertEqual(short.nextPage, 2)
        await http.configureSongSearch(total: 30, rows: 30, nextPage: 2)
        let full = try await service.search(query: "synthetic", page: 1, limit: 30)
        XCTAssertEqual(full.items.count, 30); XCTAssertEqual(full.nextPage, 2)
        await http.configureSongSearch(total: 100, rows: 30, nextPage: -1)
        let upstreamEnd = try await service.search(query: "synthetic", page: 1, limit: 30)
        XCTAssertNil(upstreamEnd.nextPage)
        await http.configureSongSearch(total: 100, rows: 0, nextPage: 9)
        let empty = try await service.search(query: "synthetic", page: 1, limit: 30)
        XCTAssertTrue(empty.items.isEmpty); XCTAssertNil(empty.nextPage)
        for nextPage in [2, 1] {
            await http.configureSongSearch(total: 100, rows: 30, nextPage: nextPage)
            do {
                _ = try await service.search(query: "synthetic", page: 2, limit: 30)
                XCTFail("Non-progressing nextpage")
            } catch { XCTAssertEqual(error as? MusicServiceError, .upstreamInvalid) }
        }
        await http.configureSongSearch(total: 100, rows: 31, nextPage: 9)
        do {
            _ = try await service.search(query: "synthetic", page: 1, limit: 30)
            XCTFail("Overfull raw song page")
        } catch { XCTAssertEqual(error as? MusicServiceError, .upstreamInvalid) }
        await service.close()
    }
    func testInvalidPlaylistSearchRangeIsRejectedBeforeDeviceOrSearchRequests() async throws {
        let http = MusicPublicBrowseQQHTTP(), service = NativeMusicQQPublicService(transport: http)
        for (page, limit) in [(0, 30), (Int.max, 30), (1, 0), (1, 101)] {
            do {
                _ = try await service.publicBrowsePlaylists(query: "synthetic", page: page, limit: limit)
                XCTFail("Invalid search range")
            } catch { XCTAssertEqual(error as? MusicServiceError, .invalidRequest) }
        }
        let calls = await http.calls(); XCTAssertTrue(calls.isEmpty)
        await service.close()
    }
    func testAnonymousPublicOwnerProtectionStillGatesBrowse() async throws {
        let http = MusicPublicBrowseQQHTTP(), service = NativeMusicQQPublicService(transport: http)
        service.revokeAccess(generation: 2)
        do { _ = try await service.publicBrowsePlaylists(query: "synthetic", page: 1, limit: 30); XCTFail("revoked") }
        catch { XCTAssertTrue([.accessUnavailable, .staleAccess].contains(error as? MusicServiceError ?? .unavailable)) }
        let calls = await http.calls(); XCTAssertTrue(calls.isEmpty)
        await service.close()
    }
}
