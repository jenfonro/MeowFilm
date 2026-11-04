import Foundation
import XCTest
import MeowAccounts
import MeowMusicFeature
@testable import MeowMusicPlatform

@MainActor final class MusicAccountSyncProtocolTests: XCTestCase {
    func testFixedSyncRouteAdmissionRejectsMethodHostPathRouterAndHeaderInjection() async throws {
        for route in MusicAccountSyncRoute.Kugou.allCases {
            let url = URL(string: "https://gateway.kugou.com" + route.rawValue)!
            let headers = route.router.map { ["x-router": $0] } ?? [:]
            XCTAssertTrue(MusicAccountSyncRoute.permits(.init(url: url, method: "POST", headers: headers)))
            XCTAssertFalse(MusicAccountSyncRoute.permits(.init(url: url, method: "GET", headers: headers)))
            XCTAssertFalse(MusicAccountSyncRoute.permits(.init(url: url, method: "POST", headers: ["x-router": "wrong.invalid"])))
            if let router = route.router {
                XCTAssertFalse(MusicAccountSyncRoute.permits(.init(url: url, method: "POST", headers: ["x-router": router, "X-Router": router])))
            }
        }
        let wy = ["https://music.163.com/weapi/playlist/create", "https://music.163.com/weapi/playlist/remove",
                  "https://music.163.com/weapi/radio/like", "https://interfacepc.music.163.com/eapi/batch",
                  "https://interfacepc.music.163.com/eapi/playlist/manipulate/tracks"]
        for raw in wy {
            let url = URL(string: raw)!
            XCTAssertTrue(MusicAccountSyncRoute.permits(.init(url: url, method: "POST")))
            XCTAssertFalse(MusicAccountSyncRoute.permits(.init(url: url, method: "GET")))
            XCTAssertFalse(MusicAccountSyncRoute.permits(.init(url: url, method: "POST", headers: ["x-router": "x"])))
        }
        for raw in ["http://music.163.com/weapi/playlist/create", "https://music.163.com.evil.invalid/weapi/playlist/create",
                    "https://music.163.com:444/weapi/playlist/create", "https://user@music.163.com/weapi/playlist/create",
                    "https://music.163.com/weapi/playlist/create#ignored", "https://gateway.kugou.com/v7/send_mobile_code",
                    "https://interface3.music.163.com/eapi/batch", "https://127.0.0.1/weapi/radio/like"] {
            XCTAssertFalse(MusicAccountSyncRoute.permits(.init(url: URL(string: raw)!, method: "POST")))
        }
        let transport = URLSessionMusicAccountTransport()
        defer { transport.close() }
        // All requests fail before URLSession is constructed; no live network.
        for bad in ["x\r\ny", "x\ry", "x\ny", "x\0y"] {
            do {
                _ = try await transport.send(.init(url: URL(string: wy[0])!, method: "POST", headers: ["Cookie": bad]))
                XCTFail("header injection")
            } catch { XCTAssertEqual(error as? MusicServiceError, .invalidRequest) }
        }
    }

    func testNeteaseFixedCreateNameMemberOrderAndFavoriteRoutes() async throws {
        let clock = MusicAccountTestClock(), http = MusicNeteaseTestHTTP(clock: clock)
        let provider = NeteaseMusicAccountAdapter(transport: http, now: { clock.now() })
        let credential = try MusicNeteaseFixture.credential(), authorization = MusicAccountSyncAuthorization {}
        let favorite = MusicNeteaseFixture.collection(500)
        let playlist = MusicNeteaseFixture.collection(501, special: 0)
        await http.replies("/weapi/user/playlist", [try MusicNeteaseFixture.response(["code": 200, "more": false, "playlist": [favorite, playlist]])])
        var detail = playlist; detail["trackIds"] = [["id": 101], ["id": 102]]; detail["tracks"] = [MusicNeteaseFixture.song(), MusicNeteaseFixture.song(102)]
        await http.replies("/eapi/v6/playlist/detail", [try MusicNeteaseFixture.response(["code": 200, "playlist": detail])])
        for path in ["/eapi/batch", "/eapi/playlist/manipulate/tracks", "/weapi/playlist/remove"] {
            await http.replies(path, [try MusicNeteaseFixture.response(["code": 200])])
        }
        await http.replies("/weapi/playlist/create", [try MusicNeteaseFixture.response(["code": 200, "id": 601])])
        let snapshot = try await provider.syncRead(kinds: [.playlist], credential: credential, guard: authorization)
        let before = try XCTUnwrap(snapshot.collections.first)
        let created = try await provider.syncWrite(.init(action: .create, name: "Native", directoryRevision: snapshot.revision),
                                                  credential: credential, guard: authorization)
        if case .acknowledged(let id, _) = created { XCTAssertEqual(id, "601") } else { XCTFail("create") }
        _ = try await provider.syncWrite(.init(action: .rename, collectionID: before.id, name: "New", before: before), credential: credential, guard: authorization)
        _ = try await provider.syncWrite(.init(action: .order, collectionID: before.id, before: before, members: Array(before.members.reversed())), credential: credential, guard: authorization)
        _ = try await provider.syncWrite(.init(action: .remove, collectionID: before.id, before: before, members: [before.members[0]]), credential: credential, guard: authorization)
        let addition = try MusicAccountSyncWire.member(NeteaseMusicAccountAdapter.song(MusicNeteaseFixture.song(103), id: "103"), platform: .netease)
        _ = try await provider.syncWrite(.init(action: .add, collectionID: before.id, before: before, members: [addition]), credential: credential, guard: authorization)
        _ = try await provider.syncWrite(.init(action: .delete, collectionID: before.id, before: before), credential: credential, guard: authorization)
        var records = await http.recorded()
        let batch = try XCTUnwrap(records.first { $0.request.url.path == "/eapi/batch" })
        let body = try MusicQQWire.decode(XCTUnwrap(batch.payload))
        let nested = try MusicQQWire.decode(Data(try MusicQQWire.string(body["/api/playlist/update/name"]).utf8))
        XCTAssertEqual(Set(nested.keys), ["id", "name"]); XCTAssertEqual(nested["id"] as? String, "501")
        let changes = try records.filter { $0.request.url.path == "/eapi/playlist/manipulate/tracks" }.map { try MusicQQWire.decode(XCTUnwrap($0.payload)) }
        XCTAssertEqual(changes.compactMap { $0["op"] as? String }, ["update", "del", "add"])
        XCTAssertEqual(changes[0]["trackIds"] as? String, "[\"102\",\"101\"]")
        XCTAssertEqual(changes[2]["trackIds"] as? String, "[\"103\"]")
        XCTAssertTrue(records.filter { MusicAccountSyncRoute.permits($0.request) }.allSatisfy { $0.request.headers["Cookie"]?.contains("synthetic-user-42") == true })
        // System favorites use radio/like, never playlist deletion or batch add.
        await http.replies("/weapi/user/playlist", [try MusicNeteaseFixture.response(["code": 200, "more": false, "playlist": [MusicNeteaseFixture.collection()]])])
        await http.replies("/eapi/v6/playlist/detail", [try MusicNeteaseFixture.response(MusicNeteaseFixture.detail())])
        await http.replies("/weapi/radio/like", [try MusicNeteaseFixture.response(["code": 200])])
        let favorites = try await provider.syncRead(kinds: [.favorites], credential: credential, guard: authorization)
        let f = try XCTUnwrap(favorites.collections.first)
        _ = try await provider.syncWrite(.init(action: .remove, collectionID: f.id, before: f, members: [f.members[0]]), credential: credential, guard: authorization)
        let rejected = try await provider.syncWrite(.init(action: .delete, collectionID: f.id, before: f), credential: credential, guard: authorization)
        if case .notDispatched(.favoritesProtected) = rejected {} else { XCTFail("protected favorites") }
        records = await http.recorded()
        XCTAssertEqual(records.filter { $0.request.url.path == "/weapi/radio/like" }.count, 1)
        XCTAssertEqual(records.filter { $0.request.url.path == "/weapi/playlist/remove" }.count, 1)
    }

    func testQQUsesAccountRPCExactRecordingAndUnsupportedOrderDoesNotWrite() async throws {
        let clock = MusicAccountTestClock(), http = MusicAccountTestHTTP(clock: clock)
        let provider = QQMusicAccountAdapter(transport: http, now: { clock.now() })
        let credential = try MusicAccountFixture.credential(), authorization = MusicAccountSyncAuthorization {}
        for method in ["EditPlaylist", "DelPlaylist", "DelSonglist", "AddSonglist"] {
            await http.replies(method, try MusicAccountFixture.rpc(["retCode": 0]))
        }
        await http.replies("AddPlaylist", try MusicAccountFixture.rpc(["retCode": 0, "result": ["tid": 601, "dirName": "Native"]]))
        let snapshot = try await provider.syncRead(kinds: [.playlist], credential: credential, guard: authorization)
        let c = try XCTUnwrap(snapshot.collections.first { $0.id == "501" })
        let order = try await provider.syncWrite(.init(action: .order, collectionID: c.id, before: c, members: c.members), credential: credential, guard: authorization)
        if case .notDispatched(.unsupported) = order {} else { XCTFail("no order emulation") }
        _ = try await provider.syncWrite(.init(action: .create, name: "Native", directoryRevision: snapshot.revision), credential: credential, guard: authorization)
        _ = try await provider.syncWrite(.init(action: .rename, collectionID: c.id, name: "New", before: c), credential: credential, guard: authorization)
        _ = try await provider.syncWrite(.init(action: .remove, collectionID: c.id, before: c, members: c.members), credential: credential, guard: authorization)
        _ = try await provider.syncWrite(.init(action: .delete, collectionID: c.id, before: c), credential: credential, guard: authorization)
        await http.replies("CgiGetTrackInfo", try MusicAccountFixture.rpc(["tracks": [MusicAccountFixture.song("NewMID", id: 202)]]))
        let addition = MusicLibrarySyncMember(nativeID: "NewMID", occurrenceID: "NewMID",
            track: try MusicQQWire.json(["songmid": "NewMID", "qqSongId": "999"])) // Untrusted numeric hint is not used.
        _ = try await provider.syncWrite(.init(action: .add, collectionID: c.id, before: c, members: [addition]), credential: credential, guard: authorization)
        let requests = await http.recorded()
        let writes = try requests.filter { try ["AddPlaylist", "EditPlaylist", "DelSonglist", "DelPlaylist", "AddSonglist"].contains(MusicAccountTestHTTP.method($0)) }
        XCTAssertEqual(try writes.map(MusicAccountTestHTTP.method), ["AddPlaylist", "EditPlaylist", "DelSonglist", "DelPlaylist", "AddSonglist"])
        XCTAssertTrue(writes.allSatisfy { $0.headers["Cookie"]?.contains("synthetic-account-key") == true })
        let removal = try MusicQQWire.object(MusicQQWire.object(MusicAccountTestHTTP.fields(writes[2])["req_0"])["param"])
        XCTAssertEqual(removal["bFmtUtf8"] as? Int, 1)
        XCTAssertEqual((removal["v_songInfo"] as? [[String: Int]])?.first, ["songId": 101, "songType": 0])
        let added = try MusicQQWire.object(MusicQQWire.object(MusicAccountTestHTTP.fields(writes[4])["req_0"])["param"])
        XCTAssertEqual(added["bFmtUtf8"] as? Bool, true)
        XCTAssertEqual((added["v_songInfo"] as? [[String: Int]])?.first, ["songId": 202, "songType": 0])
        for request in requests where (try? MusicAccountTestHTTP.method(request)) == "GetSession" {
            XCTAssertNil(request.headers["Cookie"])
        }
    }

    func testPreflightRequiresExactRevisionAndNeverTreatsReadonlyOrDuplicateMutationAsWritable() throws {
        let member = MusicLibrarySyncMember(nativeID: "101", occurrenceID: "101", track: Data())
        let c = MusicLibrarySyncCollection(id: "501", name: "Native", kind: .playlist, writable: true,
            canRename: true, canDelete: true, canOrder: true, members: [member], revision: "current")
        let snapshot = MusicLibrarySyncSnapshot(accountID: "42", collections: [c], revision: "directory")
        XCTAssertThrowsError(try MusicAccountSyncWire.preflight(.init(action: .create, name: "X", directoryRevision: "stale"), snapshot: snapshot))
        XCTAssertThrowsError(try MusicAccountSyncWire.preflight(.init(action: .add, collectionID: c.id, before: c, members: [member]), snapshot: snapshot))
        XCTAssertThrowsError(try MusicAccountSyncWire.preflight(.init(action: .remove, collectionID: c.id, before: c, members: [member, member]), snapshot: snapshot))
        XCTAssertThrowsError(try MusicAccountSyncWire.name("bad\r\nname"))
        XCTAssertNoThrow(try MusicAccountSyncWire.name(String(repeating: "字", count: 200), maximum: 200))
    }
}
