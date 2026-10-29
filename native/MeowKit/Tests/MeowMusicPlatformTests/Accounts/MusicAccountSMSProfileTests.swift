import Foundation
import XCTest
import MeowAccounts
import MeowMusicFeature
@testable import MeowMusicPlatform

/// Synthetic injected HTTP only. These tests must never use URLSession,
/// Keychain, live accounts or an actual SMS destination.
final class MusicAccountSMSProfileTests: XCTestCase {
    private let phone = "10000000000" // Deliberately synthetic, never sent.

    private func smsReplies(_ f: MusicNeteaseNativeFixture) async throws {
        await f.http.replies("/weapi/sms/captcha/sent", [try MusicNeteaseFixture.response(["code": 200])])
        await f.http.replies("/weapi/w/login/cellphone", [
            try MusicNeteaseFixture.response(["code": 200, "profile": ["userId": 42], "account": ["id": 42]],
                headers: ["set-cookie": "MUSIC_U=synthetic-user-42; Path=/; HttpOnly"])
        ])
    }

    func testNeteaseSMSExplicitSendCooldownSubmitVerifiedCredentialAndProfile() async throws {
        let f = try MusicNeteaseNativeFixture(restored: false), id = UUID()
        try await smsReplies(f); try await f.enable()
        _ = try await f.service.statuses()
        var records = await f.http.recorded()
        XCTAssertTrue(records.isEmpty, "Reading status must not send SMS or login")
        let sent = try await f.service.sendSMS(platform: .netease, countryCode: "86", phone: phone, attemptID: id)
        XCTAssertEqual(sent.retryAt, f.clock.now().addingTimeInterval(60))
        XCTAssertNil(f.store.peek(f.identity), "Sending is not authentication")
        do {
            _ = try await f.service.sendSMS(platform: .netease, countryCode: "86", phone: phone, attemptID: id)
            XCTFail("Cooldown bypass")
        } catch { XCTAssertEqual((error as? MusicSMSFailure)?.reason, .rateLimited) }
        let result = try await f.service.submitSMS(platform: .netease, countryCode: "86", phone: phone, code: "000000", attemptID: id)
        XCTAssertEqual(result.phase, .signedIn)
        let record = try XCTUnwrap(f.store.peek(f.identity))
        let serialized = String(decoding: record.credential.dataRepresentation(), as: UTF8.self)
        XCTAssertFalse(serialized.contains(phone))
        XCTAssertFalse(serialized.contains("captcha"))
        records = await f.http.recorded()
        XCTAssertEqual(records.map(\.request.url.path), ["/weapi/sms/captcha/sent", "/weapi/w/login/cellphone", "/weapi/w/nuser/account/get"])
        XCTAssertTrue(records.allSatisfy { $0.request.url.scheme == "https" })
        XCTAssertTrue(records.prefix(2).allSatisfy { !($0.request.headers["Cookie"] ?? "").contains("MUSIC_U") })
        let displayed = try await f.service.refreshProfile(platform: .netease)
        XCTAssertEqual(displayed.profile?.membership.vip, true)
        XCTAssertEqual(displayed.profile?.membership.tier, "svip")
        XCTAssertEqual(displayed.profile?.membership.expiresAt, f.clock.now().addingTimeInterval(300))
        _ = await f.service.close()
    }

    func testInvalidSMSFieldsQQAndBackgroundReadsNeverReachHTTP() async throws {
        let f = try MusicNeteaseNativeFixture(restored: false)
        try await f.enable()
        for (country, number) in [("0", phone), ("86", "00000"), ("12345", phone), ("1", "1234"), ("86", phone + "\n")] {
            do {
                _ = try await f.service.sendSMS(platform: .netease, countryCode: country, phone: number, attemptID: UUID())
                XCTFail("Invalid fields accepted")
            } catch { XCTAssertEqual(error as? MusicServiceError, .invalidRequest) }
        }
        XCTAssertThrowsError(try MusicAccountSMSInput.validate(platform: .kugou, countryCode: "1", phone: "5550100"))
        XCTAssertThrowsError(try MusicAccountSMSInput.validate(platform: .qq, countryCode: "86", phone: phone))
        let records = await f.http.recorded()
        XCTAssertTrue(records.isEmpty); XCTAssertTrue(f.store.calls().isEmpty)
        _ = await f.service.close()
    }

    func testCancelledOrLockedSMSCannotCommitLateCredential() async throws {
        for lock in [false, true] {
            let f = try MusicNeteaseNativeFixture(restored: false), gate = MusicAccountTestGate(), id = UUID()
            try await smsReplies(f); try await f.enable()
            await f.http.hold("/weapi/w/login/cellphone", gate)
            let task = Task { try await f.service.submitSMS(platform: .netease, countryCode: "86", phone: phone, code: "000000", attemptID: id) }
            await gate.started()
            if lock { _ = try await f.service.setAccess(available: false, generation: 2) }
            else { await f.service.cancelSMSLogin(attemptID: id) }
            await gate.release()
            do { _ = try await task.value; XCTFail("Late login accepted") } catch {}
            XCTAssertNil(f.store.peek(f.identity))
            let records = await f.http.recorded()
            XCTAssertFalse(records.contains { $0.request.url.path == "/weapi/w/nuser/account/get" })
            let statuses = try await f.service.statuses()
            XCTAssertNotEqual(statuses.first?.phase, .signedIn)
            _ = await f.service.close()
        }
    }

    func testSMSFailureDoesNotResendAndCooldownSurvivesCancel() async throws {
        let f = try MusicNeteaseNativeFixture(restored: false), id = UUID()
        try await f.enable()
        await f.http.replies("/weapi/sms/captcha/sent", [try MusicNeteaseFixture.response(["code": 509])])
        do {
            _ = try await f.service.sendSMS(platform: .netease, countryCode: "86", phone: phone, attemptID: id)
            XCTFail("Failed send accepted")
        } catch { XCTAssertEqual((error as? MusicSMSFailure)?.reason, .rateLimited) }
        await f.service.cancelSMSLogin(attemptID: id)
        do {
            _ = try await f.service.sendSMS(platform: .netease, countryCode: "86", phone: phone, attemptID: UUID())
            XCTFail("Cancellation reset cooldown")
        } catch { XCTAssertEqual((error as? MusicSMSFailure)?.reason, .rateLimited) }
        let records = await f.http.recorded(); XCTAssertEqual(records.count, 1)
        XCTAssertNil(f.store.peek(f.identity))
        _ = await f.service.close()
    }

    func testKugouSMSUsesPinnedPayloadRoutesSameDeviceAndNoMembershipFromLogin() async throws {
        let s = MusicKugouTestSystem(), id = UUID()
        await s.http.respond("/v7/send_mobile_code", [try MusicKugouFixture.response(["status": 1, "data": "synthetic-sent"])])
        await s.http.respond("/v7/login_by_verifycode", [
            try MusicKugouFixture.response(["status": 1, "data": ["userid": 42, "token": MusicKugouFixture.token, "vip_type": 99,
                                                              "t1": "synthetic-device-token"]])
        ])
        try await s.provider.sendSMS(countryCode: "86", phone: phone, attemptID: id)
        let result = try await s.provider.loginSMS(countryCode: "86", phone: phone, code: "000000", attemptID: id)
        XCTAssertEqual(result.nativeID, "42")
        let stored = try MusicQQWire.decode(result.credential.dataRepresentation())
        XCTAssertEqual((stored["auth"] as? [String: Any])?["t1"] as? String, "synthetic-device-token")
        let requests = await s.http.recorded()
        XCTAssertEqual(requests.map(\.url.path), ["/risk/v2/r_register_dev", "/v7/send_mobile_code", "/v7/login_by_verifycode", "/v3/get_my_info"])
        let send = requests[1], login = requests[2]
        XCTAssertEqual(send.headers["x-router"], "login.user.kugou.com")
        XCTAssertEqual(login.url.host, "loginserviceretry.kugou.com")
        XCTAssertEqual(login.headers["support-calm"], "1")
        XCTAssertEqual(try MusicKugouFixture.fields(send)["mid"], try MusicKugouFixture.fields(login)["mid"])
        let sendBody = try MusicQQWire.decode(send.body!)
        XCTAssertEqual(sendBody["mobile"] as? String, phone)
        XCTAssertEqual(sendBody["businessid"] as? Int, 5)
        let loginBody = try MusicQQWire.decode(login.body!)
        XCTAssertEqual(loginBody["mobile"] as? String, "10*****0")
        XCTAssertEqual((loginBody["pk"] as? String)?.count, 256)
        XCTAssertEqual(loginBody["pk"] as? String, (loginBody["pk"] as? String)?.uppercased())
        XCTAssertFalse(String(decoding: login.body!, as: UTF8.self).contains(phone))
        XCTAssertNil(loginBody["code"]); XCTAssertNil(loginBody["token"])
        let profile = try await s.provider.accountProfile(result.credential).profile
        XCTAssertEqual(profile.membership.tier, "vip", "Display uses get_union_vip, not login vip_type")
        await s.provider.cancelSMS(id)
    }

    func testKugouOwnerSMSCommitProfileWithoutEchoedUIDAndSizedAvatar() async throws {
        let s = MusicKugouTestSystem(), store = MusicAccountTestStore(), identity = try MusicAccountFixture.identity("kg")
        let service = NativeMusicAccountService(accounts: .init(store: store), scopes: [.kugou: identity],
                                                transport: s.http, clock: { [clock = s.clock] in clock.now() })
        await s.http.respond("/v7/login_by_verifycode", [
            try MusicKugouFixture.response(["status": 1, "data": ["userid": 42, "token": MusicKugouFixture.token]])
        ])
        await s.http.respond("/v3/get_my_info", [try MusicKugouFixture.response(["status": 1, "data": [
            "nickname": "合成酷狗昵称", "pic": "http://imge.kugou.com/kugouicon/{size}/synthetic.jpg"
        ]])])
        _ = try await service.setAccess(available: true, generation: 1)
        let status = try await service.submitSMS(platform: .kugou, countryCode: "86", phone: phone,
                                                 code: "000000", attemptID: UUID())
        XCTAssertEqual(status.phase, .signedIn); XCTAssertNotNil(store.peek(identity))
        let display = try await service.refreshProfile(platform: .kugou)
        XCTAssertEqual(display.profile?.nickname, "合成酷狗昵称")
        XCTAssertEqual(display.profile?.avatarURL?.absoluteString, "https://imge.kugou.com/kugouicon/480/synthetic.jpg")
        XCTAssertEqual(display.profile?.membership.vip, true)
        let requests = await s.http.recorded()
        XCTAssertFalse(requests.contains { $0.url.path == "/v7/send_mobile_code" }, "Submit never sends")
        _ = await service.close()
    }

    func testTypedInvalidCodeAndMultiAccountErrorsSurviveOwnerWithoutCredentialWrite() async throws {
        let f = try MusicNeteaseNativeFixture(restored: false); try await f.enable()
        await f.http.replies("/weapi/w/login/cellphone", [try MusicNeteaseFixture.response(["code": 503])])
        do {
            _ = try await f.service.submitSMS(platform: .netease, countryCode: "86", phone: phone, code: "000000", attemptID: UUID())
            XCTFail("Invalid code authenticated")
        } catch {
            XCTAssertEqual((error as? MusicSMSFailure)?.detail, .invalidCode)
            XCTAssertEqual(error.localizedDescription, "验证码错误或已过期")
        }
        XCTAssertNil(f.store.peek(f.identity))
        let requests = await f.http.recorded(); XCTAssertEqual(requests.count, 1)
        _ = await f.service.close()

        let s = MusicKugouTestSystem(), store = MusicAccountTestStore(), identity = try MusicAccountFixture.identity("kg")
        let service = NativeMusicAccountService(accounts: .init(store: store), scopes: [.kugou: identity],
                                                transport: s.http, clock: { [clock = s.clock] in clock.now() })
        await s.http.respond("/v7/login_by_verifycode", [
            try MusicKugouFixture.response(["status": 1, "data": ["info_list": [["userid": 42], ["userid": 99]]]])
        ])
        _ = try await service.setAccess(available: true, generation: 1)
        do {
            _ = try await service.submitSMS(platform: .kugou, countryCode: "86", phone: phone, code: "000000", attemptID: UUID())
            XCTFail("Implicitly selected one of multiple accounts")
        } catch { XCTAssertEqual((error as? MusicSMSFailure)?.detail, .accountSelectionRequired) }
        XCTAssertNil(store.peek(identity))
        _ = await service.close()
    }

    func testQQProfileUsesHomepageIdentityAvatarAndMembershipNotPlaybackCapabilities() async throws {
        let f = try MusicNativeFixture(); try await f.enable()
        await f.http.replies("GetHomepageHeader", try MusicAccountFixture.rpc(["Info": ["BaseInfo": [
            "EncryptedUin": "synthetic-euin-101", "Name": "合成QQ昵称", "Avatar": "https://y.gtimg.cn/synthetic.jpg"
        ]]]))
        let value = try await f.service.refreshProfile(platform: .qq)
        XCTAssertEqual(value.profile?.nickname, "合成QQ昵称")
        XCTAssertEqual(value.profile?.avatarURL?.host, "y.gtimg.cn")
        XCTAssertEqual(value.profile?.membership.tier, "svip")
        XCTAssertEqual(value.profile?.membership.expiresAt, f.clock.now().addingTimeInterval(300))
        let requests = await f.http.recorded()
        let methods = try requests.map(MusicAccountTestHTTP.method)
        XCTAssertEqual(methods.filter { $0 == "vip_login_base" }.count, 1)
        XCTAssertFalse(methods.contains("CgiGetTrackInfo")); XCTAssertFalse(methods.contains("UrlGetVkey"))
        await f.http.replies("GetHomepageHeader", try MusicAccountFixture.rpc(["Info": ["BaseInfo": [
            "EncryptedUin": "synthetic-euin-other", "Name": "不应显示"
        ]]]))
        do { _ = try await f.service.refreshProfile(platform: .qq); XCTFail("Foreign profile accepted") }
        catch { XCTAssertEqual(error as? MusicServiceError, .targetChanged) }
        let rows = try await f.service.statuses()
        XCTAssertNotEqual(rows.first?.nickname, "不应显示")
        _ = await f.service.close()
    }

    func testNeteaseProfileUnknownFailurePreservesProfileButNotPrivilegeAndLogoutClears() async throws {
        let f = try MusicNeteaseNativeFixture(); try await f.enable()
        await f.http.replies("/weapi/w/nuser/account/get", [try MusicNeteaseFixture.response([
            "code": 200, "profile": ["userId": 42, "nickname": "合成昵称", "avatarUrl": "https://p1.music.126.net/synthetic.jpg"]
        ])])
        await f.http.replies("/weapi/music-vip-membership/client/vip/info", [try MusicNeteaseFixture.response(["code": 502])])
        let status = try await f.service.refreshProfile(platform: .netease)
        XCTAssertEqual(status.profile?.nickname, "合成昵称")
        XCTAssertEqual(status.profile?.avatarURL?.host, "p1.music.126.net")
        XCTAssertNil(status.profile?.membership.vip, "Failure must not claim non-membership")
        try await f.service.logout(platform: .netease)
        let rows = try await f.service.statuses()
        XCTAssertNil(rows.first?.profile); XCTAssertEqual(rows.first?.nickname, "")
        _ = await f.service.close()
    }

    func testOldProfileCannotRefillAfterLogout() async throws {
        let f = try MusicNeteaseNativeFixture(), gate = MusicAccountTestGate()
        try await f.enable(); _ = try await f.service.validate(platform: .netease)
        await f.http.hold("/weapi/music-vip-membership/client/vip/info", gate)
        let task = Task { try await f.service.refreshProfile(platform: .netease) }
        await gate.started(); try await f.service.logout(platform: .netease); await gate.release()
        do { _ = try await task.value; XCTFail("Late profile accepted") } catch {}
        let rows = try await f.service.statuses()
        XCTAssertEqual(rows.first?.phase, .signedOut); XCTAssertNil(rows.first?.profile)
        _ = await f.service.close()
    }

    func testMembershipAbsoluteTimeUnknownExpiredFutureStartAndTierPriority() throws {
        let now = Date(timeIntervalSince1970: 1_800_000_000), ms = 1_800_000_000_000
        let wy = MusicAccountProfileWire.neteaseMembership([
            "now": ms + 100_000, "associator": ["expireTime": ms + 90_000], "redplus": ["expireTime": ms + 200_000]
        ], at: now)
        XCTAssertEqual(wy.tier, "svip"); XCTAssertEqual(wy.expiresAt, now.addingTimeInterval(200), "Do not replace absolute expiry with receipt time")
        XCTAssertNil(MusicAccountProfileWire.neteaseMembership([:], at: now).vip)
        let kg = MusicAccountProfileWire.kugouMembership([
            "busi_vip": [["busi_type": "concept", "product_type": "svip", "is_vip": 1,
                          "vip_begin_time": 1_800_000_100, "vip_end_time": 1_800_000_200]],
            "is_vip": 0, "vip_end_time": 1_799_999_000
        ], at: now)
        XCTAssertEqual(kg.vip, false); XCTAssertEqual(kg.expiresAt, now.addingTimeInterval(-1000))
        let qq = MusicAccountProfileWire.qqMembership(["identity": ["vip": 1], "userinfo": ["expire": "2020-01-02"]], at: now)
        XCTAssertEqual(qq.vip, false)
        XCTAssertEqual(qq.expiresAt, Date(timeIntervalSince1970: 1_577_894_400))
        XCTAssertNil(MusicAccountProfileWire.qqMembership(["identity": ["vip": "1"]], at: now).vip)
        let futureProduct: [String: Any] = ["svip": 1, "userinfo": ["expire": 1_800_001_000],
            "identity": ["huge_vip": 1, "huge_vip_start": 1_800_000_100, "huge_vip_end": 1_800_002_000]]
        let qqActive = MusicAccountProfileWire.qqMembership(futureProduct, at: now)
        XCTAssertEqual(MusicAccountProfileWire.productDeadline(futureProduct, platform: .qq, value: qqActive, at: now),
                       now.addingTimeInterval(100), "A future product start invalidates the old display early")
        XCTAssertEqual(MusicAccountProfileWire.neteaseDeadline(["now": ms + 100_000], value: wy, at: now),
                       now.addingTimeInterval(100), "Display expiry and upstream-clock cache deadline are distinct")
        XCTAssertNil(MusicAccountProfileWire.kugouMembership(["is_vip": 0,
            "busi_vip": [["product_type": "svip"]]], at: now).vip)
    }

    func testDisplayDTOOldFieldsAndAvatarSecretBoundaries() throws {
        let old = try JSONDecoder().decode(MusicAccountProfile.self, from: Data("{\"nickname\":\"合成旧昵称\"}".utf8))
        XCTAssertNil(old.avatarURL); XCTAssertNil(old.membership.vip); XCTAssertNil(old.membership.expiresAt)
        let membership = try JSONDecoder().decode(MusicAccountMembership.self, from: Data("{}".utf8))
        XCTAssertNil(membership.vip); XCTAssertEqual(membership.tier, "")
        XCTAssertThrowsError(try MusicAccountProfileWire.avatar("https://p1.music.126.net/%73ynthetic-secret", platform: .netease, secrets: ["synthetic-secret"]))
        XCTAssertNil(try MusicAccountProfileWire.avatar("https://not-a-cdn.invalid/avatar.png", platform: .qq, secrets: []))
    }

    func testSMSWhitelistRejectsHostMethodQueryAndRedirectTargets() {
        for path in ["/weapi/sms/captcha/sent", "/weapi/w/login/cellphone"] {
            XCTAssertTrue(MusicAccountSMSHTTP.permits(.init(url: URL(string: "https://music.163.com" + path)!, method: "POST")))
            for url in ["http://music.163.com" + path, "https://music.163.com" + path + "?next=1", "https://music.163.com.evil.invalid" + path] {
                XCTAssertFalse(MusicAccountSMSHTTP.permits(.init(url: URL(string: url)!, method: "POST")))
            }
            XCTAssertFalse(MusicAccountSMSHTTP.permits(.init(url: URL(string: "https://music.163.com" + path)!)))
        }
    }

    func testSMSLimitReservationsAreBoundedHashedAndEightSubmitsNeverImplicitlySend() throws {
        let now = Date(timeIntervalSince1970: 1_800_000_000)
        var limits = MusicAccountSMSLimits()
        let key = try limits.reserve(platform: .netease, country: "86", phone: phone, send: false, at: now)
        XCTAssertFalse(key.contains(phone)); XCTAssertEqual(key.count, 64)
        XCTAssertThrowsError(try limits.reserve(platform: .netease, country: "86", phone: phone, send: true, at: now))
        limits.release(key)
        for _ in 1..<8 {
            let next = try limits.reserve(platform: .netease, country: "86", phone: phone, send: false, at: now)
            limits.release(next)
        }
        XCTAssertThrowsError(try limits.reserve(platform: .netease, country: "86", phone: phone, send: false, at: now))
        XCTAssertEqual(limits.sends, 0)
        XCTAssertThrowsError(try MusicAccountSMSInput.validate(platform: .netease, countryCode: "86\n", phone: phone))
        XCTAssertThrowsError(try MusicAccountSMSInput.validate(platform: .netease, countryCode: "86", phone: phone, code: "000000\n"))
    }
}
