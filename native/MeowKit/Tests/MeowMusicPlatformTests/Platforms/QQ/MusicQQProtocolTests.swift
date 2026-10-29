import Foundation
import XCTest
import MeowMusicFeature
import MeowAccounts
import MeowJSON
@testable import MeowMusicPlatform

final class MusicQQProtocolTests: XCTestCase {
    func testCBCVectorAndPrivateCallbackParsingDoNotExecuteJavaScript() throws {
        // Independent Node crypto vector, generated locally, not reference execution.
        XCTAssertEqual(try MusicQQWire.aes("synthetic-device", key: "lvcwmSYVr2Axv1gn", iv: "Zs0ntDqG2jyhKN0c"),
                       "+JCibeQBRLhj6aGmvwBMr1u5TILvIXRE1yp9gRJPAX0=")
        XCTAssertEqual(MusicQQWire.md5("test"), "098f6bcd4621d373cade4e832627b4f6")
        XCTAssertEqual(MusicQQWire.hash33("abc", seed: 0), 108966)
        XCTAssertEqual(try MusicQQWire.callback(Data("ptuiCB('66','0','');".utf8)), ["66", "0", ""])
        for value in ["ptuiCB('0','0','');evil()", "eval('66','0','')", "ptuiCB('0'+run(),'0','')"] {
            XCTAssertThrowsError(try MusicQQWire.callback(Data(value.utf8)))
        }
    }

    func testCookieBoundariesHandleExpiresAndLastValueWithoutSharedCookieJar() throws {
        let response = MusicAccountHTTPResponse(status: 302, data: Data(), headers: ["Set-Cookie":
            "qrsig=synthetic-old; Expires=Wed, 21 Oct 2030 07:28:00 GMT, p_skey=synthetic-key; HttpOnly, qrsig=synthetic-new; Path=/"])
        let cookies = try MusicQQWire.cookies(response)
        XCTAssertEqual(cookies, ["qrsig": "synthetic-new", "p_skey": "synthetic-key"])
        XCTAssertThrowsError(try MusicQQWire.cookies(.init(status: 200, data: Data(),
            headers: ["set-cookie": "qrsig=bad\r\nvalue"])))
    }

    func testQimeiEnvelopeIsFixedProtocolAndGetSessionRemainsAnonymous() async throws {
        let clock = MusicAccountTestClock(), http = MusicAccountTestHTTP(clock: MusicAccountTestClock())
        let adapter = QQMusicAccountAdapter(transport: http, now: { clock.now() })
        _ = try await adapter.verify(MusicAccountFixture.credential())
        let requests = await http.recorded()
        let qimei = try XCTUnwrap(requests.first)
        XCTAssertEqual(qimei.url.absoluteString, "https://api.tencentmusic.com/tme/trpc/proxy")
        XCTAssertEqual(qimei.headers["method"], "GetQimei")
        XCTAssertEqual(qimei.headers["service"], "trpc.tme_datasvr.qimeiproxy.QimeiProxy")
        XCTAssertNil(qimei.headers["Cookie"])
        let fields = try MusicQQWire.object(MusicAccountTestHTTP.fields(qimei)["qimeiParams"])
        let key = try MusicQQWire.string(fields["key"])
        // The encrypted full device envelope is larger than the helper's
        // default 1024-byte UI/string bound; this is not a malformed response.
        // Keep an explicit finite envelope bound, without changing production
        // field validation or dropping either signature/anonymous assertion.
        let params = try MusicQQWire.string(fields["params"], maximum: 16_384)
        XCTAssertGreaterThan(params.utf8.count, 1024)
        XCTAssertNotNil(Data(base64Encoded: params))
        let time = try MusicQQWire.string(fields["time"]), nonce = try MusicQQWire.string(fields["nonce"])
        let extra = try MusicQQWire.string(fields["extra"])
        XCTAssertEqual(Data(base64Encoded: key)?.count, 128)
        XCTAssertEqual(fields["sign"] as? String, MusicQQWire.md5(key + params + time + "000" + nonce + "ZdJqM15EeO2zWc08" + extra))
        XCTAssertEqual(qimei.headers["sign"], MusicQQWire.md5("qimei_qq_androidpzAuCmaFAaFaHrdakPjLIEqKrGnSOOvH" + time))
        let sessions = try requests.filter { try MusicAccountTestHTTP.method($0) == "GetSession" }
        let session = try XCTUnwrap(sessions.first)
        XCTAssertEqual(sessions.count, 1); XCTAssertNil(session.headers["Cookie"])
        let comm = try MusicQQWire.object(MusicAccountTestHTTP.fields(session)["comm"])
        XCTAssertNil(comm["qq"]); XCTAssertNil(comm["authst"])
        _ = try await adapter.verify(MusicAccountFixture.credential())
        let repeated = await http.recorded()
        XCTAssertEqual(repeated.filter { $0.url.host == "api.tencentmusic.com" }.count, 1)
        await adapter.revoke()
    }

    func testRestoreProfileMismatchCannotBecomeAcceptedCredential() async throws {
        let f = try MusicNativeFixture(); try await f.enable()
        await f.http.replies("GetLoginUserInfo", try MusicAccountFixture.rpc(["musicid": 202]))
        let before = f.store.peek(f.identity)
        do { _ = try await f.service.validate(platform: .qq); XCTFail("Wrong account") }
        catch { XCTAssertEqual(error as? MusicServiceError, .targetChanged) }
        XCTAssertEqual(f.store.peek(f.identity), before)
        let status = try await f.service.statuses()
        XCTAssertFalse(status.contains { $0.phase == .signedIn }); _ = await f.service.close()
    }

    func testHTTPAuthAndRateLimitsAreClosedCodesAndAuthFailureInvalidatesOriginalScope() async throws {
        // The fixture contains a saved credential; a rejected validation is an
        // expired session, unlike the independently covered anonymous caller.
        for (code, expected) in [(401, MusicServiceError.sessionExpired), (429, .rateLimited), (500, .upstreamFailed)] {
            let f = try MusicNativeFixture(); try await f.enable()
            await f.http.replies("GetLoginUserInfo", .init(status: code, data: Data("synthetic-private-body".utf8)))
            do { _ = try await f.service.validate(platform: .qq); XCTFail("HTTP error accepted") }
            catch { XCTAssertEqual(error as? MusicServiceError, expected) }
            if code == 401 { XCTAssertNil(f.store.peek(f.identity)) }
            else { XCTAssertNotNil(f.store.peek(f.identity)) }
            _ = await f.service.close()
        }
    }

    func testMissingOrUnknownMembershipDoesNotGrantOfficialPlayback() async throws {
        let memberships: [[String: Any]] = [[:], ["svip": 2], ["svip": true]]
        for membership in memberships {
            let f = try MusicNativeFixture(); try await f.enable()
            await f.http.replies("vip_login_base", try MusicAccountFixture.rpc(membership))
            let reply = try await f.invoke("capabilities", ["nativeId": .string(MusicAccountFixture.mid)])
            XCTAssertEqual(reply["ok"], .bool(false)); XCTAssertEqual(reply["code"], .string("VERIFICATION_REQUIRED"))
            let requests = await f.http.recorded(), methods = try requests.map(MusicAccountTestHTTP.method)
            XCTAssertFalse(methods.contains("UrlGetVkey")); _ = await f.service.close()
        }
    }

    func testExpiredEntitlementNoOptionsAndFutureBoundaryClampsMediaTTL() async throws {
        let f = try MusicNativeFixture(); try await f.enable()
        await f.http.replies("vip_login_base", try MusicAccountFixture.rpc([
            "svip": 1, "userinfo": ["expire": f.clock.now().timeIntervalSince1970 - 10]
        ]))
        let empty = try await f.invoke("capabilities", ["nativeId": .string(MusicAccountFixture.mid)])
        XCTAssertEqual(empty["qualities"], .array([]))
        await f.http.replies("vip_login_base", try MusicAccountFixture.rpc([
            "svip": 1, "userinfo": ["expire": f.clock.now().timeIntervalSince1970 + 10]
        ]))
        let cap = try await f.invoke("capabilities", ["nativeId": .string(MusicAccountFixture.mid)])
        let reply = try await f.invoke("resolve", ["nativeId": .string(MusicAccountFixture.mid), "quality": .string("master"),
            "installationId": try XCTUnwrap(cap["installationId"]), "generation": try XCTUnwrap(cap["generation"])])
        XCTAssertEqual(reply["expiresAt"], .number(f.clock.now().addingTimeInterval(10).timeIntervalSince1970 * 1000))
        _ = await f.service.close()
    }

    func testOfficialRejectsTrialWrongIdentityAndHostWithoutFallback() async throws {
        let variants: [[String: Any]] = [
            ["songmid": "WrongMID"], ["filename": "F000Wrong.flac"], ["result": 104003], ["ekey": "synthetic"],
            ["purl": "RS02Trial.mp3"], ["purl": "https://stream.qqmusic.qq.com.attacker.invalid/M500File.mp3"],
            ["purl": "https://user@isure.stream.qqmusic.qq.com/M500File.mp3"],
            ["purl": "https://isure.stream.qqmusic.qq.com:9999/M500File.mp3"], ["trial": false]
        ]
        for variant in variants {
            let f = try MusicNativeFixture(); try await f.enable()
            let filename = "M500SyntheticMIDSyntheticMID.mp3"
            let row: [String: Any] = ["songmid": MusicAccountFixture.mid, "filename": filename, "result": 0, "purl": filename, "ekey": ""]
            await f.http.replies("UrlGetVkey", try MusicAccountFixture.rpc([
                "expiration": 120, "midurlinfo": [row.merging(variant) { _, newer in newer }]
            ]))
            let cap = try await f.invoke("capabilities", ["nativeId": .string(MusicAccountFixture.mid)])
            let reply = try await f.invoke("resolve", ["nativeId": .string(MusicAccountFixture.mid), "quality": .string("128k"),
                "installationId": try XCTUnwrap(cap["installationId"]), "generation": try XCTUnwrap(cap["generation"])])
            XCTAssertEqual(reply["ok"], .bool(false)); XCTAssertNil(reply["url"])
            let requests = await f.http.recorded(), names = try requests.map(MusicAccountTestHTTP.method)
            XCTAssertEqual(names.filter { $0 == "UrlGetVkey" }.count, 1)
            _ = await f.service.close()
        }
    }

    func testIncompleteOrHiddenCatalogCannotBeReportedAsEmptyOrComplete() async throws {
        let variants: [[String: Any]] = [
            ["v_list": [], "v_failTids": [], "hide": true, "total": 0, "hasmore": 0],
            ["v_list": [], "v_failTids": [], "hide": false, "total": 1, "hasmore": 0],
            ["v_list": [], "v_failTids": [501], "hide": false, "total": 0, "hasmore": 0]
        ]
        for variant in variants {
            let f = try MusicNativeFixture(); try await f.enable()
            await f.http.replies("CgiGetPlaylistFavInfo", try MusicAccountFixture.rpc(variant))
            let reply = try await f.invoke("collections")
            XCTAssertEqual(reply["ok"], .bool(false)); XCTAssertNil(reply["items"])
            _ = await f.service.close()
        }
    }

    func testUnknownResolveGrantDoesNotProbeUrlOrDifferentSong() async throws {
        let f = try MusicNativeFixture(); try await f.enable()
        let cap = try await f.invoke("capabilities", ["nativeId": .string(MusicAccountFixture.mid)])
        let reply = try await f.invoke("resolve", ["nativeId": .string("DifferentMID"), "quality": .string("128k"),
            "installationId": try XCTUnwrap(cap["installationId"]), "generation": try XCTUnwrap(cap["generation"])])
        XCTAssertEqual(reply["code"], .string("STALE_RESULT"))
        let requests = await f.http.recorded(), names = try requests.map(MusicAccountTestHTTP.method)
        XCTAssertFalse(names.contains("UrlGetVkey")); _ = await f.service.close()
    }

    func testTransportRejectsUnregisteredOriginsBeforeCreatingNetworkTasks() async throws {
        let transport = URLSessionMusicAccountTransport()
        for url in ["http://u.y.qq.com/cgi-bin/musicu.fcg", "https://attacker.invalid/cgi-bin/musicu.fcg",
                    "https://u.y.qq.com/arbitrary", "https://user@u.y.qq.com/cgi-bin/musicu.fcg"] {
            do { _ = try await transport.send(.init(url: URL(string: url)!)); XCTFail("Unregistered request") }
            catch { XCTAssertEqual(error as? MusicServiceError, .invalidRequest) }
        }
        transport.close()
    }
}
