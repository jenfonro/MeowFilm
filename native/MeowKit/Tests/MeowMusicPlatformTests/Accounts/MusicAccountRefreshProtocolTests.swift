import Foundation
import XCTest
import MeowAccounts
import MeowMusicFeature
@testable import MeowMusicPlatform

private struct MusicRefreshKugouFixture {
    let identity = try! MusicAccountFixture.identity("kg")
    let store = MusicAccountTestStore()
    let clock = MusicAccountTestClock()
    let http: MusicKugouTestHTTP
    let accounts: AccountSessionCoordinator
    let service: NativeMusicAccountService
    init(credential: AccountCredential? = nil) throws {
        http = .init(clock: clock); accounts = .init(store: store)
        service = .init(accounts: accounts, scopes: [.kugou: identity], transport: http, clock: { [clock] in clock.now() })
        try store.write(.init(credential: credential ?? MusicKugouFixture.credential()), for: identity)
    }
    func enable() async throws { _ = try await service.setAccess(available: true, generation: 1) }
}

final class MusicAccountRefreshProtocolTests: XCTestCase {
    func testUpdatedSecretsCannotBeReturnedAsPublicProfileText() throws {
        for text in ["synthetic-new-secret", "%73ynthetic-new-secret", "%2573ynthetic-new-secret"] {
            XCTAssertThrowsError(try MusicAccountPublicText.profile(text, secrets: ["", "synthetic-new-secret"])) {
                XCTAssertEqual($0 as? MusicServiceError, .upstreamInvalid)
            }
        }
        XCTAssertEqual(try MusicAccountPublicText.profile("合成昵称", secrets: ["synthetic-new-secret"]), "合成昵称")
    }
    func testNeteaseRefreshUsesEAPIThenVerifiesNewCookiesAndCommitsDeviceOnSameToken() async throws {
        let f = try MusicNeteaseNativeFixture(); try await f.enable()
        _ = try await f.service.validate(platform: .netease)
        let before = await f.accounts.snapshot(for: f.identity)
        await f.http.replies("/eapi/login/token/refresh", [try MusicNeteaseFixture.response(["code": 200], headers: [
            "set-cookie": "MUSIC_U=synthetic-updated; Path=/, __csrf=synthetic-csrf-next; Path=/, deviceId=synthetic-device-next; Path=/"
        ])])
        let result = try await f.service.refresh(platform: .netease)
        let after = await f.accounts.snapshot(for: f.identity)
        XCTAssertEqual(result.phase, .signedIn); XCTAssertEqual(before.generation, after.generation)
        let stored = try MusicQQWire.decode(XCTUnwrap(f.store.peek(f.identity)).credential.dataRepresentation())
        XCTAssertEqual(stored["accountID"] as? String, "42")
        XCTAssertEqual(try MusicQQWire.object(stored["cookies"])["MUSIC_U"] as? String, "synthetic-updated")
        XCTAssertEqual(try MusicQQWire.object(stored["device"])["deviceId"] as? String, "synthetic-device-next")
        let requests = await f.http.recorded()
        XCTAssertEqual(requests.map(\.request.url.path), ["/weapi/w/nuser/account/get", "/eapi/login/token/refresh", "/weapi/w/nuser/account/get"])
        let refresh = requests[1], profile = requests[2]
        XCTAssertEqual(refresh.request.url.host, "interfacepc.music.163.com")
        let payload = try MusicQQWire.decode(XCTUnwrap(refresh.payload))
        XCTAssertEqual(try MusicQQWire.object(payload["header"])["MUSIC_U"] as? String, "synthetic-user-42")
        XCTAssertTrue(profile.request.headers["Cookie"]?.contains("MUSIC_U=synthetic-updated") == true)
        XCTAssertTrue(profile.request.headers["Cookie"]?.contains("deviceId=synthetic-device-next") == true)
        XCTAssertFalse(f.store.calls().contains("remove"))
        _ = await f.service.close()
    }
    func testNeteaseRotatingProfileCookieIsBoundedAndCannotCommitUnverifiedTail() async throws {
        let f = try MusicNeteaseNativeFixture(); try await f.enable()
        let original = f.store.peek(f.identity)
        let body: [String: Any] = ["code": 200, "profile": ["userId": 42]]
        await f.http.replies("/weapi/w/nuser/account/get", [
            try MusicNeteaseFixture.response(body, headers: ["set-cookie": "MUSIC_U=synthetic-one; Path=/"]),
            try MusicNeteaseFixture.response(body, headers: ["set-cookie": "MUSIC_U=synthetic-two; Path=/"])
        ])
        do { _ = try await f.service.validate(platform: .netease); XCTFail("Unbounded/unstable update accepted") }
        catch { XCTAssertEqual(error as? MusicServiceError, .verificationRequired) }
        let requests = await f.http.recorded()
        XCTAssertEqual(requests.count, 2); XCTAssertEqual(f.store.peek(f.identity), original)
        _ = await f.service.close()
    }
    func testNeteaseDeviceOnlyUpdateUsesCASAndKeychainFailureIsNotCachedSuccess() async throws {
        let f = try MusicNeteaseNativeFixture(); try await f.enable()
        let before = f.store.peek(f.identity)
        await f.http.replies("/weapi/w/nuser/account/get", [try MusicNeteaseFixture.response(
            ["code": 200, "profile": ["userId": 42]], headers: ["set-cookie": "deviceId=synthetic-device-next; Path=/"])])
        f.store.fail("write")
        do { _ = try await f.service.validate(platform: .netease); XCTFail("Device stored only in RAM") }
        catch { XCTAssertEqual(error as? MusicServiceError, .commitUnconfirmed) }
        XCTAssertEqual(f.store.peek(f.identity), before)
        let rows = try await f.service.statuses()
        XCTAssertEqual(rows.first?.phase, .requiresValidation); XCTAssertEqual(rows.first?.nickname, "")
        let requests = await f.http.recorded(); XCTAssertEqual(requests.count, 1)
        _ = await f.service.close()
    }
    func testNeteaseRefreshMismatchedUIDLeavesOldRecordAndDoesNotReplay() async throws {
        let f = try MusicNeteaseNativeFixture(); try await f.enable()
        let original = f.store.peek(f.identity)
        await f.http.replies("/eapi/login/token/refresh", [try MusicNeteaseFixture.response(["code": 200],
            headers: ["set-cookie": "MUSIC_U=synthetic-updated; Path=/"])])
        await f.http.replies("/weapi/w/nuser/account/get", [try MusicNeteaseFixture.response(
            ["code": 200, "profile": ["userId": 99]])])
        do { _ = try await f.service.refresh(platform: .netease); XCTFail("Refreshed to different UID") }
        catch { XCTAssertEqual(error as? MusicServiceError, .targetChanged) }
        XCTAssertEqual(f.store.peek(f.identity), original)
        let requests = await f.http.recorded()
        XCTAssertEqual(requests.count, 2)
        _ = await f.service.close()
    }
    func testKugouRefreshKeepsDeviceAndVerifiesNewTokenAfterExactRouteAndEnvelope() async throws {
        let f = try MusicRefreshKugouFixture(); try await f.enable()
        _ = try await f.service.validate(platform: .kugou)
        let before = await f.accounts.snapshot(for: f.identity), old = f.store.peek(f.identity)
        await f.http.respond("/v5/login_by_token", [try MusicKugouFixture.response(["status": 1,
            "data": ["userid": 42, "token": "synthetic-refreshed", "t1": 8, "vip_token": "synthetic-vip-next"]])])
        let result = try await f.service.refresh(platform: .kugou)
        let after = await f.accounts.snapshot(for: f.identity)
        XCTAssertEqual(result.phase, .signedIn); XCTAssertEqual(before.generation, after.generation)
        let requests = await f.http.recorded(), refresh = try XCTUnwrap(requests.first { $0.url.path == "/v5/login_by_token" })
        XCTAssertEqual(refresh.method, "POST"); XCTAssertEqual(refresh.url.host, "gateway.kugou.com")
        XCTAssertEqual(refresh.headers["x-router"], "login.user.kugou.com")
        let query = try MusicKugouFixture.fields(refresh)
        XCTAssertEqual(query["token"], MusicKugouFixture.token); XCTAssertEqual(query["userid"], "42")
        let body = try MusicQQWire.decode(XCTUnwrap(refresh.body))
        XCTAssertEqual(body["t3"] as? String, "MCwwLDAsMCwwLDAsMCwwLDA=")
        XCTAssertEqual((body["pk"] as? String)?.count, 256)
        XCTAssertEqual((body["params"] as? String)?.count, 32)
        let p3 = try MusicNeteaseFixture.aes(MusicNeteaseFixture.hex(MusicQQWire.string(body["p3"])),
            key: Data("90b8382a1bb4ccdcf063102053fd75b8".utf8), iv: Data("f063102053fd75b8".utf8))
        let inner = try MusicQQWire.decode(p3)
        XCTAssertEqual(inner["token"] as? String, MusicKugouFixture.token)
        XCTAssertEqual(inner["clienttime"] as? Int, Int(f.clock.now().timeIntervalSince1970))
        let profile = try XCTUnwrap(requests.last)
        XCTAssertEqual(profile.url.path, "/v3/get_my_info")
        XCTAssertEqual(try MusicKugouFixture.fields(profile)["token"], "synthetic-refreshed")
        let stored = try MusicQQWire.decode(XCTUnwrap(f.store.peek(f.identity)).credential.dataRepresentation())
        let device = try MusicQQWire.object(stored["device"])
        let oldDevice = try MusicQQWire.object(MusicQQWire.decode(XCTUnwrap(old).credential.dataRepresentation())["device"])
        XCTAssertEqual(try MusicQQWire.json(device), try MusicQQWire.json(oldDevice))
        XCTAssertEqual(try MusicQQWire.object(stored["auth"])["token"] as? String, "synthetic-refreshed")
        _ = await f.service.close()
    }
    func testKugouMissingDFIDRegistersAnonymouslyAndConditionallySavesOnOriginalDevice() async throws {
        var raw = try MusicQQWire.decode(MusicKugouFixture.credential().dataRepresentation())
        var device = try MusicQQWire.object(raw["device"]); device.removeValue(forKey: "dfid"); raw["device"] = device
        let f = try MusicRefreshKugouFixture(credential: AccountCredential(data: MusicQQWire.json(raw))); try await f.enable()
        _ = try await f.service.validate(platform: .kugou)
        let requests = await f.http.recorded()
        XCTAssertEqual(requests.map(\.url.path), ["/risk/v2/r_register_dev", "/v3/get_my_info"])
        let query = try MusicKugouFixture.fields(requests[0])
        XCTAssertNil(query["token"]); XCTAssertNil(query["userid"])
        let stored = try MusicQQWire.object(MusicQQWire.decode(XCTUnwrap(f.store.peek(f.identity)).credential.dataRepresentation())["device"])
        XCTAssertEqual(stored["seed"] as? String, device["seed"] as? String)
        XCTAssertEqual(stored["KUGOU_API_MID"] as? String, device["KUGOU_API_MID"] as? String)
        XCTAssertEqual(stored["dfid"] as? String, "synthetic-dfid")
        _ = await f.service.close()
    }
    func testKugouIndependentEncryptedLoginVectorsAndStrictPadding() throws {
        // .NET AES-CBC independent synthetic vectors; ASCII MD5(seed) is the
        // 32-byte key, its last 16 characters the IV. Never a real credential.
        let seed = "0123456789abcdef"
        let json = "81066de0314b25333e4b74434619ac00abe2190c2ab38d48644afb049cb61971893bc5e0f7e0bb9f1f3fafc4a4e9af12"
        let token = "c8f7826444ab9d0da5f9dafe1c9d3a04ccfc7543fa83494a7e486850bca49702"
        for cipher in [json, token] {
            let result = try MusicKugouCrypto.loginData(["userid": 42, "secu_params": cipher], seed: seed)
            XCTAssertEqual(try MusicKugouWire.id(result["userid"]), "42")
            XCTAssertEqual(result["token"] as? String, "synthetic-refreshed")
        }
        for invalid in ["not-hex", "0", "00", "35bbb8f3e30f868545114cf0ac17fd5f"] {
            XCTAssertThrowsError(try MusicKugouCrypto.loginData(["userid": 42, "secu_params": invalid], seed: seed))
        }
    }
    func testKugouEncryptedIdentityCannotOverrideOriginalBinding() async throws {
        let s = MusicKugouTestSystem()
        let context = try await s.provider.context(MusicKugouFixture.credential(uid: "99"))
        let cipher = "81066de0314b25333e4b74434619ac00abe2190c2ab38d48644afb049cb61971893bc5e0f7e0bb9f1f3fafc4a4e9af12"
        do {
            _ = try await s.provider.verifiedRefresh(["userid": 99, "secu_params": cipher],
                seed: "0123456789abcdef", context: context)
            XCTFail("Encrypted UID changed account")
        } catch { XCTAssertEqual(error as? MusicServiceError, .targetChanged) }
        let requests = await s.http.recorded(); XCTAssertTrue(requests.isEmpty)
    }
    func testQQRefreshUsesOriginalCookiesAndVerifiesSameUIDWithoutNewLogin() async throws {
        let f = try MusicNativeFixture(restored: false)
        var auth = MusicAccountFixture.auth(); auth["refresh_token"] = "synthetic-refresh"
        try f.store.write(.init(credential: AccountCredential(data: MusicQQWire.json(["version": 1, "platform": "tx", "auth": auth]))), for: f.identity)
        try await f.enable(); _ = try await f.service.validate(platform: .qq)
        let before = await f.accounts.snapshot(for: f.identity)
        var next = MusicAccountFixture.auth(); next["musickey"] = "synthetic-next-key"; next.removeValue(forKey: "encrypt_uin")
        await f.http.replies("Login", try MusicAccountFixture.rpc(next))
        _ = try await f.service.refresh(platform: .qq)
        let after = await f.accounts.snapshot(for: f.identity)
        XCTAssertEqual(before.generation, after.generation)
        let requests = await f.http.recorded(), login = try XCTUnwrap(requests.first { (try? MusicAccountTestHTTP.method($0)) == "Login" })
        let root = try MusicAccountTestHTTP.fields(login), param = try MusicQQWire.object(MusicQQWire.object(root["req_0"])["param"])
        XCTAssertEqual(param["loginMode"] as? Int, 2); XCTAssertEqual(param["refresh_token"] as? String, "synthetic-refresh")
        XCTAssertTrue(login.headers["Cookie"]?.contains("synthetic-account-key") == true)
        for request in requests where (try? MusicAccountTestHTTP.method(request)) == "GetSession" {
            XCTAssertNil(request.headers["Cookie"])
            XCTAssertNil(try MusicQQWire.object(MusicAccountTestHTTP.fields(request)["comm"])["authst"])
        }
        let saved = try MusicQQWire.object(MusicQQWire.decode(XCTUnwrap(f.store.peek(f.identity)).credential.dataRepresentation())["auth"])
        XCTAssertEqual(saved["musickey"] as? String, "synthetic-next-key")
        XCTAssertEqual(saved["encrypt_uin"] as? String, "synthetic-euin-101")
        XCTAssertFalse(f.store.calls().contains("remove"))
        _ = await f.service.close()
    }
    func testQQProfileEnrichmentIsPersistedAndMissingRefreshIsNotAQRLogin() async throws {
        let f = try MusicNativeFixture(restored: false)
        var auth = MusicAccountFixture.auth(); auth.removeValue(forKey: "encrypt_uin")
        try f.store.write(.init(credential: AccountCredential(data: MusicQQWire.json(["version": 1, "platform": "tx", "auth": auth]))), for: f.identity)
        await f.http.replies("/rsc/fcgi-bin/fcg_get_profile_homepage.fcg", try MusicAccountFixture.response(
            ["code": 0, "data": ["creator": ["uin": 101, "encrypt_uin": "synthetic-euin-101", "nick": "合成昵称"]]]))
        try await f.enable(); _ = try await f.service.validate(platform: .qq)
        let stored = try XCTUnwrap(f.store.peek(f.identity))
        let saved = try MusicQQWire.object(MusicQQWire.decode(stored.credential.dataRepresentation())["auth"])
        XCTAssertEqual(saved["encrypt_uin"] as? String, "synthetic-euin-101")
        let before = await f.http.recorded()
        do { _ = try await f.service.refresh(platform: .qq); XCTFail("Missing refresh material accepted") }
        catch { XCTAssertEqual(error as? MusicServiceError, .verificationRequired) }
        let after = await f.http.recorded()
        XCTAssertEqual(before.count, after.count); XCTAssertEqual(f.store.peek(f.identity), stored)
        XCTAssertFalse(f.store.calls().contains("remove"))
        _ = await f.service.close()
    }
}
