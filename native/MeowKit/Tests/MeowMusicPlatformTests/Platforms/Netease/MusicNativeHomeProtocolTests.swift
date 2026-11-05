import XCTest
import Foundation
import MeowMusicFeature
import MeowJSON
@testable import MeowMusicPlatform

/// Synthetic transport only. These declarations have not been executed on
/// Apple; they exercise the real adapter/encryption and its typed request port.
final class MusicNativeHomeProtocolTests: XCTestCase {
    private let homePath = "/eapi/link/page/rcmd/resource/show"
    private let artistPath = "/eapi/sub/artist/new/works/song-mv/list/v2"

    func testAuthenticatedHomeForwardsFinalArtworkAndOpaqueNumericMetadataWithoutSecrets() async throws {
        let f = try MusicNeteaseNativeFixture(); try await f.enable()
        let binding = try await f.service.nativeHomeBinding()
        let raw = #"{"code":200,"cookie":"synthetic-user-42","data":{"blocks":[{"picUrl":null,"picUrl":"https://p1.music.126.net/synthetic.jpg","unused":1e400,"largeID":18446744073709551617}],"authorization":"synthetic-csrf"}}"#
        await f.http.replies(homePath, [.init(status: 200, data: Data(raw.utf8))])
        let bytes = try await f.service.nativeHomePayload(.home(), binding: binding)
        let root = try MusicQQWire.decode(bytes)
        let data = try MusicQQWire.object(root["data"])
        let row = try XCTUnwrap((data["blocks"] as? [[String: Any]])?.first)
        XCTAssertEqual(row["picUrl"] as? String, "https://p1.music.126.net/synthetic.jpg")
        XCTAssertEqual((row["unused"] as? JSONNumber)?.literal, "1e400")
        XCTAssertEqual((row["largeID"] as? JSONNumber)?.literal, "18446744073709551617")
        XCTAssertFalse(String(decoding: bytes, as: UTF8.self).contains("synthetic-user-42"))
        XCTAssertFalse(String(decoding: bytes, as: UTF8.self).contains("synthetic-csrf"))
        XCTAssertNil(root["cookie"]); XCTAssertNil(data["authorization"])
        _ = await f.service.close()
    }

    func testHomeFeaturedAndContinuationUseExactAuthenticatedEAPI() async throws {
        let f = try MusicNeteaseNativeFixture(); try await f.enable()
        await f.http.replies(homePath, [try MusicNeteaseFixture.response([
            "code": 200, "data": ["blocks": [], "hasMore": false]
        ])])
        let binding = try await f.service.nativeHomeBinding()
        XCTAssertTrue(binding.loggedIn)
        _ = try await f.service.nativeHomePayload(.home(), binding: binding)
        let continuation = try MusicNativeHomeContinuation(cursor: "9007199254740991",
            blockCodeOrderList: #"["block-a","block-b"]"#, loadedPositionCodes: ["position-a", "position-b"])
        _ = try await f.service.nativeHomePayload(.featured(continuation: continuation), binding: binding)
        let records = await f.http.recorded()
        let pages = records.filter { $0.request.url.path == homePath }
        XCTAssertEqual(pages.count, 2)
        let first = try MusicQQWire.decode(XCTUnwrap(pages.first?.payload))
        let second = try MusicQQWire.decode(XCTUnwrap(pages.last?.payload))
        XCTAssertEqual(first["pageCode"] as? String, "HOME_RECOMMEND_PAGE")
        XCTAssertEqual((first["cursor"] as? NSNumber)?.int64Value, 0)
        XCTAssertEqual(first["isFirstScreen"] as? Bool, true)
        XCTAssertEqual(first["pageStyleType"] as? String, "noCutBlock")
        XCTAssertEqual(first["refresh"] as? Bool, false)
        XCTAssertNil(first["blockCodeOrderList"]); XCTAssertNil(first["loadedPositionCodes"])
        XCTAssertEqual(second["pageCode"] as? String, "HOME_DISCOVERY_PAGE")
        XCTAssertEqual((second["cursor"] as? NSNumber)?.int64Value, 9_007_199_254_740_991)
        XCTAssertEqual(second["isFirstScreen"] as? Bool, false)
        XCTAssertEqual(second["blockCodeOrderList"] as? String, continuation.blockCodeOrderList)
        XCTAssertEqual(second["loadedPositionCodes"] as? String, #"["position-a","position-b"]"#)
        for record in pages {
            XCTAssertTrue(MusicNeteaseHomeRoute.permits(record.request))
            XCTAssertEqual(record.request.headers["Cache-Control"], "no-store")
            XCTAssertTrue(record.request.headers["Cookie"]?.contains("MUSIC_U=synthetic-user-42") == true)
            XCTAssertNil(record.request.url.query)
        }
        XCTAssertFalse(binding.scope.contains("synthetic")); XCTAssertFalse(binding.scope.contains("MUSIC_U"))
        try await f.service.checkNativeHomeBinding(binding)
        _ = await f.service.close()
    }

    func testArtistActivityHasReferenceDefaultsAndExactTimestampContinuation() async throws {
        let f = try MusicNeteaseNativeFixture(); try await f.enable()
        await f.http.replies(artistPath, [try MusicNeteaseFixture.response([
            "code": 200, "data": ["newWorks": [], "hasMore": false]
        ])])
        let binding = try await f.service.nativeHomeBinding()
        _ = try await f.service.nativeHomePayload(.artistActivity(), binding: binding)
        _ = try await f.service.nativeHomePayload(.artistActivity(before: "999999999999999999"), binding: binding)
        let records = await f.http.recorded()
        let pages = records.filter { $0.request.url.path == artistPath }
        XCTAssertEqual(pages.count, 2)
        let first = try MusicQQWire.decode(XCTUnwrap(pages.first?.payload))
        let second = try MusicQQWire.decode(XCTUnwrap(pages.last?.payload))
        XCTAssertEqual((first["startTimestamp"] as? NSNumber)?.int64Value, 1_800_000_000_000)
        XCTAssertEqual(first["firstRequest"] as? Bool, true)
        XCTAssertEqual(first["limit"] as? Int, 10); XCTAssertEqual(first["sourceType"] as? Int, 1)
        XCTAssertEqual(second["startTimestamp"] as? String, "999999999999999999")
        XCTAssertEqual(second["firstRequest"] as? Bool, false)
        XCTAssertEqual(second["limit"] as? Int, 10); XCTAssertEqual(second["sourceType"] as? Int, 1)
        XCTAssertTrue(pages.allSatisfy { MusicNeteaseHomeRoute.permits($0.request) })
        _ = await f.service.close()
    }

    func testHydrationUsesSameBindingAndRetainsLargeIDsWithoutRounding() async throws {
        let f = try MusicNeteaseNativeFixture(); try await f.enable()
        let path = "/eapi/tag/tab/playlists"
        await f.http.replies(path, [try MusicNeteaseFixture.response([
            "code": 200, "data": ["playlists": [["id": "999999999999999999", "name": "合成歌单"]]]
        ])])
        let vipPath = "/eapi/vipnewcenter/app/viptab/recommend/song/list"
        await f.http.replies(vipPath, [try MusicNeteaseFixture.response([
            "code": 200, "data": ["songInfoList": [], "reasonList": []]
        ])])
        let binding = try await f.service.nativeHomeBinding(), ids = ["101", "999999999999999999"]
        _ = try await f.service.nativeHomePayload(.songMetadata(ids: ids), binding: binding)
        _ = try await f.service.nativeHomePayload(.playlistMetadata(ids: ids), binding: binding)
        _ = try await f.service.nativeHomePayload(.vipSongs, binding: binding)
        let spec = try MusicNeteaseHomeRequest.specification(.songMetadata(ids: ids), now: f.clock.now())
        XCTAssertEqual(spec.path, "/api/v3/song/detail"); XCTAssertEqual(spec.crypto, "weapi")
        XCTAssertEqual(spec.data["c"] as? String, #"[{"id":101},{"id":999999999999999999}]"#)
        let vipSpec = try MusicNeteaseHomeRequest.specification(.vipSongs, now: f.clock.now())
        XCTAssertEqual(vipSpec.path, "/api/vipnewcenter/app/viptab/recommend/song/list")
        XCTAssertEqual(vipSpec.crypto, "eapi"); XCTAssertTrue(vipSpec.data.isEmpty)
        let records = await f.http.recorded()
        let song = try XCTUnwrap(records.first { $0.request.url.path == "/weapi/v3/song/detail" })
        let playlist = try XCTUnwrap(records.first { $0.request.url.path == path })
        let vip = try XCTUnwrap(records.first { $0.request.url.path == vipPath })
        XCTAssertTrue(MusicNeteaseHomeRoute.permits(song.request))
        XCTAssertTrue(MusicNeteaseHomeRoute.permits(playlist.request))
        XCTAssertTrue(MusicNeteaseHomeRoute.permits(vip.request))
        XCTAssertTrue(records.contains { $0.request.url.path == "/weapi/music-vip-membership/client/vip/info" })
        XCTAssertTrue(song.request.headers["Cookie"]?.contains("MUSIC_U=synthetic-user-42") == true)
        XCTAssertTrue(playlist.request.headers["Cookie"]?.contains("MUSIC_U=synthetic-user-42") == true)
        XCTAssertTrue(vip.request.headers["Cookie"]?.contains("MUSIC_U=synthetic-user-42") == true)
        let payload = try MusicQQWire.decode(XCTUnwrap(playlist.payload))
        XCTAssertEqual(payload["ids"] as? String, ids.joined(separator: ","))
        let unchanged = try await f.service.nativeHomeBinding()
        XCTAssertEqual(unchanged, binding)
        _ = await f.service.close()
    }

    func testContinuationAndMetadataBoundsRejectBeforeAnyHTTP() async throws {
        for cursor in ["", "-1", "1.0", "1e3", "١", "9007199254740992", String(repeating: "0", count: 21)] {
            XCTAssertThrowsError(try MusicNativeHomeContinuation(cursor: cursor))
        }
        XCTAssertThrowsError(try MusicNativeHomeContinuation(cursor: "1", blockCodeOrderList: String(repeating: "a", count: 16_001)))
        XCTAssertThrowsError(try MusicNativeHomeContinuation(cursor: "1", blockCodeOrderList: "a\nb"))
        XCTAssertThrowsError(try MusicNativeHomeContinuation(cursor: "1", loadedPositionCodes: Array(repeating: "x", count: 301)))
        XCTAssertThrowsError(try MusicNativeHomeContinuation(cursor: "1", loadedPositionCodes: [String(repeating: "x", count: 201)]))
        XCTAssertThrowsError(try MusicNativeHomeContinuation(cursor: "1", loadedPositionCodes: Array(repeating: String(repeating: "x", count: 200), count: 100)))
        let f = try MusicNeteaseNativeFixture(restored: false); try await f.enable()
        let binding = try await f.service.nativeHomeBinding()
        let requests: [MusicNativeHomeRequest] = [
            .artistActivity(before: ""), .artistActivity(before: "-1"),
            .artistActivity(before: "1234567890123456789"), .artistActivity(before: "1&limit=100"),
            .songMetadata(ids: []), .playlistMetadata(ids: []),
            .songMetadata(ids: Array(repeating: "1", count: 101)),
            .playlistMetadata(ids: Array(repeating: "1", count: 101)),
            .songMetadata(ids: ["1,\"header\":{}"]), .playlistMetadata(ids: ["0"]),
            .songMetadata(ids: ["01"]), .playlistMetadata(ids: [String(repeating: "1", count: 19)])
        ]
        for request in requests {
            do { _ = try await f.service.nativeHomePayload(request, binding: binding); XCTFail("Invalid typed request accepted") }
            catch { XCTAssertEqual(error as? MusicServiceError, .invalidRequest) }
        }
        let records = await f.http.recorded()
        XCTAssertTrue(records.isEmpty)
        _ = await f.service.close()
    }

    func testRouteAllowlistRejectsHostMethodQueryAndRouterChanges() throws {
        for (host, path) in [
            ("interfacepc.music.163.com", homePath),
            ("interfacepc.music.163.com", artistPath),
            ("interfacepc.music.163.com", "/eapi/tag/tab/playlists"),
            ("interfacepc.music.163.com", "/eapi/vipnewcenter/app/viptab/recommend/song/list"),
            ("music.163.com", "/weapi/v3/song/detail")
        ] {
            let base = "https://" + host + path
            XCTAssertTrue(MusicNeteaseHomeRoute.permits(.init(url: try XCTUnwrap(URL(string: base)), method: "POST")))
            for invalid in [
                base.replacingOccurrences(of: "https:", with: "http:"),
                "https://invalid.example" + path, base + "?cookie=value", base + "#x",
                "https://name@" + host + path, "https://" + host + ":444" + path,
                "https://" + host + path + "/arbitrary"
            ] {
                XCTAssertFalse(MusicNeteaseHomeRoute.permits(.init(url: try XCTUnwrap(URL(string: invalid)), method: "POST")))
            }
            let url = try XCTUnwrap(URL(string: base))
            XCTAssertFalse(MusicNeteaseHomeRoute.permits(.init(url: url, method: "GET")))
            XCTAssertFalse(MusicNeteaseHomeRoute.permits(.init(url: url, method: "POST", headers: ["X-Router": host])))
        }
    }

    func testPayloadDoesNotReturnCookieEnvelopesOrReflectedCredentialValues() async throws {
        let f = try MusicNeteaseNativeFixture(); try await f.enable()
        let binding = try await f.service.nativeHomeBinding()
        await f.http.replies(homePath, [try MusicNeteaseFixture.response([
            "code": 200, "cookie": "synthetic-user-42", "data": [
                "blocks": [], "MUSIC_U": "synthetic-user-42", "nested": ["authorization": "synthetic-csrf"]
            ]
        ])])
        let bytes = try await f.service.nativeHomePayload(.home(), binding: binding)
        let text = String(decoding: bytes, as: UTF8.self)
        XCTAssertFalse(text.contains("synthetic")); XCTAssertFalse(text.contains("MUSIC_U"))
        XCTAssertTrue(text.contains("blocks"))
        for leaked in ["synthetic-user-42", "%73ynthetic-user-42", "https://invalid.example/?x=synthetic-csrf"] {
            await f.http.replies(homePath, [try MusicNeteaseFixture.response([
                "code": 200, "data": ["blocks": [["title": leaked]]]
            ])])
            do { _ = try await f.service.nativeHomePayload(.home(), binding: binding); XCTFail("Reflected credential returned") }
            catch { XCTAssertEqual(error as? MusicServiceError, .upstreamInvalid) }
        }
        try await f.service.checkNativeHomeBinding(binding)
        _ = await f.service.close()
    }

    func testSetCookieCannotSilentlyReplaceCredentialFromHomeRead() async throws {
        let f = try MusicNeteaseNativeFixture(); try await f.enable()
        let binding = try await f.service.nativeHomeBinding(), saved = f.store.peek(f.identity)
        await f.http.replies(homePath, [try MusicNeteaseFixture.response([
            "code": 200, "data": ["blocks": []]
        ], headers: ["set-cookie": "MUSIC_U=synthetic-replacement; Path=/"])])
        do { _ = try await f.service.nativeHomePayload(.home(), binding: binding); XCTFail("Unverified credential rotation") }
        catch { XCTAssertEqual(error as? MusicServiceError, .verificationRequired) }
        XCTAssertEqual(f.store.peek(f.identity), saved)
        XCTAssertTrue(binding.authorization.isActive)
        _ = await f.service.close()
    }
}
