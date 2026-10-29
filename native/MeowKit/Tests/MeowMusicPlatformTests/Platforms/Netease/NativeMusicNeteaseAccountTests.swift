import Foundation
import XCTest
import ImageIO
import MeowAccounts
import MeowMusicFeature
@testable import MeowMusicPlatform

final class NativeMusicNeteaseAccountTests: XCTestCase {
    func testQRAnonymousBootstrapLocalPNGVerifiedCommitAndLocalLogout() async throws {
        let f = try MusicNeteaseNativeFixture()
        try await f.enable()
        let challenge = try await f.service.beginLogin(platform: .netease)
        XCTAssertEqual(challenge.platform, .netease); XCTAssertNil(f.store.peek(f.identity))
        XCTAssertEqual(challenge.expiresAt, f.clock.now().addingTimeInterval(180))
        let imageSource = try XCTUnwrap(CGImageSourceCreateWithData(challenge.image as CFData, nil))
        XCTAssertNotNil(CGImageSourceCreateImageAtIndex(imageSource, 0, nil))
        let progress = try await f.service.checkLogin(challengeID: challenge.id)
        XCTAssertEqual(progress, .confirmed)
        let record = try XCTUnwrap(f.store.peek(f.identity))
        let data = try MusicQQWire.decode(record.credential.dataRepresentation())
        XCTAssertEqual(data["platform"] as? String, "wy"); XCTAssertEqual(data["accountID"] as? String, "42")
        XCTAssertEqual(Set(data.keys), ["version", "platform", "accountID", "cookies", "device"])
        let cookies = try MusicQQWire.object(data["cookies"])
        XCTAssertNil(cookies["MUSIC_A"]); XCTAssertNil(data["key"]); XCTAssertNil(data["url"])
        let before = await f.http.recorded()
        XCTAssertEqual(before.map(\.request.url.path), ["/api/bsr/sk/get", "/xeapi/register/anonimous",
            "/eapi/login/qrcode/unikey", "/eapi/login/qrcode/client/login", "/weapi/w/nuser/account/get"])
        for row in before.prefix(4) {
            XCTAssertFalse((row.request.headers["Cookie"] ?? "").contains("MUSIC_U"))
            XCTAssertNil(row.request.headers["x-music-u"])
            XCTAssertNotEqual(row.request.url.path, "/login", "Never upload a QR URL to an image service")
        }
        let signedIn = try await awaitStatus(f)
        XCTAssertEqual(signedIn.nickname, "合成网易昵称")
        try await f.service.logout(platform: .netease)
        let after = await f.http.recorded()
        XCTAssertEqual(after.count, before.count); XCTAssertNil(f.store.peek(f.identity))
        let status = try await f.service.statuses()
        XCTAssertEqual(status.first?.phase, .signedOut)
        _ = await f.service.close()
    }
    private func awaitStatus(_ f: MusicNeteaseNativeFixture) async throws -> MusicAccountStatus {
        let status = try await f.service.statuses(); return try XCTUnwrap(status.first)
    }
    func testRestoreVerifiesAndConditionallyConfirmsTheSameCredential() async throws {
        let f = try MusicNeteaseNativeFixture(), before = f.store.peek(f.identity)
        try await f.enable()
        let value = try await f.service.validate(platform: .netease)
        XCTAssertEqual(value.phase, .signedIn)
        XCTAssertEqual(f.store.peek(f.identity)?.credential, before?.credential)
        XCTAssertEqual(f.store.peek(f.identity)?.revision, before?.revision)
        let requests = await f.http.recorded()
        XCTAssertEqual(requests.map(\.request.url.path), ["/weapi/w/nuser/account/get"])
        XCTAssertEqual(f.store.calls().filter { $0 == "write" }.count, 1, "Unchanged verified credentials retain their cache scope without rewriting")
        _ = await f.service.close()
    }
    func testRestoreProfileMissingNullAndWrongIdentityFailDifferently() async throws {
        let cases: [([String: Any], MusicServiceError)] = [
            (["code": 200], .upstreamInvalid),
            (["code": 200, "profile": NSNull()], .sessionExpired),
            (["code": 200, "profile": ["userId": 99, "nickname": "合成"]], .targetChanged),
            (["code": 200, "profile": ["userId": 42], "account": ["id": 99]], .targetChanged)
        ]
        for (body, expected) in cases {
            let f = try MusicNeteaseNativeFixture(); try await f.enable()
            await f.http.replies("/weapi/w/nuser/account/get", [try MusicNeteaseFixture.response(body)])
            do { _ = try await f.service.validate(platform: .netease); XCTFail("Unverified restore") }
            catch { XCTAssertEqual(error as? MusicServiceError, expected) }
            let rows = try await f.service.statuses()
            XCTAssertNotEqual(rows.first?.phase, .signedIn); XCTAssertEqual(rows.first?.nickname, "")
            if expected == .sessionExpired { XCTAssertNil(f.store.peek(f.identity)) }
            else { XCTAssertNotNil(f.store.peek(f.identity)) }
            _ = await f.service.close()
        }
    }
    func testRestoredCookieRotationRevalidatesReplacementThenCommitsWithCAS() async throws {
        let f = try MusicNeteaseNativeFixture(), original = f.store.peek(f.identity); try await f.enable()
        await f.http.replies("/weapi/w/nuser/account/get", [try MusicNeteaseFixture.response(
            ["code": 200, "profile": ["userId": 42]], headers: ["set-cookie": "MUSIC_U=synthetic-renewed; Path=/"])])
        let status = try await f.service.validate(platform: .netease)
        XCTAssertEqual(status.phase, .signedIn)
        let stored = try XCTUnwrap(f.store.peek(f.identity))
        XCTAssertNotEqual(stored.revision, original?.revision)
        let cookies = try MusicQQWire.object(MusicQQWire.decode(stored.credential.dataRepresentation())["cookies"])
        XCTAssertEqual(cookies["MUSIC_U"] as? String, "synthetic-renewed")
        let requests = await f.http.recorded()
        XCTAssertEqual(requests.count, 2)
        XCTAssertTrue(requests[1].request.headers["Cookie"]?.contains("MUSIC_U=synthetic-renewed") == true)
        XCTAssertEqual(f.store.calls().filter { $0 == "write" }.count, 2)
        _ = await f.service.close()
    }
    func testQRProgressThrottleExpiryAndNoUnverifiedCredential() async throws {
        let f = try MusicNeteaseNativeFixture(restored: false); try await f.enable()
        let challenge = try await f.service.beginLogin(platform: .netease)
        for (code, expected) in [(801, MusicLoginProgress.waiting), (802, .scanned)] {
            await f.http.replies("/eapi/login/qrcode/client/login", [try MusicNeteaseFixture.response(["code": code])])
            let result = try await f.service.checkLogin(challengeID: challenge.id); XCTAssertEqual(result, expected)
            do { _ = try await f.service.checkLogin(challengeID: challenge.id); XCTFail("Unthrottled polling") }
            catch { XCTAssertEqual(error as? MusicServiceError, .rateLimited) }
            f.clock.advance(3)
        }
        f.clock.advance(181)
        let expired = try await f.service.checkLogin(challengeID: challenge.id)
        XCTAssertEqual(expired, .expired); XCTAssertNil(f.store.peek(f.identity))
        _ = await f.service.close()
    }
    func testCancelAndProtectionRejectNonCooperativeLateProfile() async throws {
        for lock in [false, true] {
            let f = try MusicNeteaseNativeFixture(restored: false); try await f.enable()
            let challenge = try await f.service.beginLogin(platform: .netease), gate = MusicAccountTestGate()
            await f.http.hold("/weapi/w/nuser/account/get", gate)
            let pending = Task { try await f.service.checkLogin(challengeID: challenge.id) }
            await gate.started()
            if lock { _ = try await f.service.setAccess(available: false, generation: 2) }
            else { await f.service.cancelLogin(challengeID: challenge.id) }
            await gate.release()
            do { _ = try await pending.value; XCTFail("Late login committed") } catch {}
            XCTAssertNil(f.store.peek(f.identity))
            let statuses = try await f.service.statuses()
            XCTAssertTrue(statuses.allSatisfy { $0.nickname.isEmpty && $0.phase != .signedIn })
            if lock { XCTAssertTrue(statuses.allSatisfy { !$0.canLogin }) }
            _ = await f.service.close()
        }
    }
    func testQRDeadlineExpiresDuringProfileAndStorageFailureIsNotSuccess() async throws {
        let f = try MusicNeteaseNativeFixture(restored: false); try await f.enable()
        let challenge = try await f.service.beginLogin(platform: .netease), gate = MusicAccountTestGate()
        await f.http.hold("/weapi/w/nuser/account/get", gate)
        let pending = Task { try await f.service.checkLogin(challengeID: challenge.id) }
        await gate.started(); f.clock.advance(181); await gate.release()
        do { _ = try await pending.value; XCTFail("Expired during verification") }
        catch { XCTAssertEqual(error as? MusicServiceError, .staleResult) }
        XCTAssertNil(f.store.peek(f.identity)); _ = await f.service.close()
        let failing = try MusicNeteaseNativeFixture(restored: false); try await failing.enable()
        let next = try await failing.service.beginLogin(platform: .netease)
        failing.store.fail("write")
        do { _ = try await failing.service.checkLogin(challengeID: next.id); XCTFail("Failed store signed in") }
        catch { XCTAssertEqual(error as? MusicServiceError, .storageFailed) }
        XCTAssertNil(failing.store.peek(failing.identity))
        _ = await failing.service.close()
    }
    func testHandshakeFailureAndStorageReadFailureNeverCreateEmptySession() async throws {
        let f = try MusicNeteaseNativeFixture(restored: false); try await f.enable()
        await f.http.replies("/api/bsr/sk/get", [try MusicNeteaseFixture.response(["code": 200,
            "data": ["timestamp": "1800000000000", "signature": "not-a-signature", "encryptedData": "bad"]])])
        do { _ = try await f.service.beginLogin(platform: .netease); XCTFail("Invalid handshake") } catch {}
        let requests = await f.http.recorded(); XCTAssertEqual(requests.count, 1)
        XCTAssertNil(f.store.peek(f.identity)); _ = await f.service.close()
        let restored = try MusicNeteaseNativeFixture(); try await restored.enable(); restored.store.fail("read")
        do { _ = try await restored.service.validate(platform: .netease); XCTFail("Storage failure hidden") }
        catch { XCTAssertEqual(error as? MusicServiceError, .storageFailed) }
        let network = await restored.http.recorded(); XCTAssertTrue(network.isEmpty)
        _ = await restored.service.close()
    }
}
