import XCTest
import Foundation
import MeowMusicFeature
@testable import MeowMusicPlatform

private actor MusicPublicBrowseQQHTTP: MusicAccountHTTPTransport {
    private var requests: [MusicAccountHTTPRequest] = []
    private var reads = 0
    var reorder = false
    var paged = false
    func changeOnSecondRead() { reorder = true }
    func useShortPages() { paged = true }
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
                ["mid": mid, "id": "9007199254740993", "type": 1, "title": mid, "singer": [["name": "Synthetic"]],
                 "album": ["mid": "AlbumMID", "name": "Album"], "interval": 180,
                 "file": ["media_mid": "MediaMID", "size_128mp3": 100, "size_hires": 500]]
            }
            let data: [String: Any]
            switch method {
            case "GetSession": data = ["session": ["uid": "synthetic-anonymous", "sid": "synthetic-session"]]
            case "DoSearchForQQMusicMobile":
                XCTAssertEqual(params["search_type"] as? Int, 3)
                data = ["meta": ["sum": 1, "nextpage": -1], "body": ["item_songlist": [["tid": 42, "title": "Synthetic playlist", "songnum": 3]]]]
            case "CgiGetDiss":
                reads += 1
                XCTAssertEqual(params["disstid"] as? Int, 42)
                let offset = try XCTUnwrap(params["song_begin"] as? Int), limit = try XCTUnwrap(params["song_num"] as? Int)
                let all = paged ? (0..<65).map { song("MID\($0)") } :
                    reorder && reads == 2 ? [song("MID2"), song("MID1"), song("MID1")] : [song("MID1"), song("MID2"), song("MID1")]
                let songs = Array(all.dropFirst(offset).prefix(paged ? min(limit, 27) : limit))
                data = ["dirinfo": ["id": 42, "title": "Synthetic playlist", "songnum": all.count],
                        "total_song_num": all.count, "songlist_size": songs.count,
                        "hasmore": offset + songs.count < all.count ? 1 : 0, "songlist": songs]
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
    func testPublicSearchAndDetailUseExistingDeviceOwnerAndNativeRPCWithoutAccountCookie() async throws {
        let http = MusicPublicBrowseQQHTTP(), service = NativeMusicQQPublicService(transport: http)
        let search = try await service.publicBrowsePlaylists(query: "synthetic", page: 1)
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
    func testAnonymousPublicOwnerProtectionStillGatesBrowse() async throws {
        let http = MusicPublicBrowseQQHTTP(), service = NativeMusicQQPublicService(transport: http)
        service.revokeAccess(generation: 2)
        do { _ = try await service.publicBrowsePlaylists(query: "synthetic", page: 1); XCTFail("revoked") }
        catch { XCTAssertTrue([.accessUnavailable, .staleAccess].contains(error as? MusicServiceError ?? .unavailable)) }
        let calls = await http.calls(); XCTAssertTrue(calls.isEmpty)
        await service.close()
    }
}
