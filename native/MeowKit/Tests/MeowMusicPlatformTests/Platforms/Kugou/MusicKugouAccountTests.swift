import XCTest
import Foundation
import MeowAccounts
import MeowMusicFeature
@testable import MeowMusicPlatform

final class MusicKugouAccountTests: XCTestCase {
    func testQRWaitingScannedConfirmedThenRestoresWithSameDeviceAndProfile() async throws {
        let s = MusicKugouTestSystem(), id = UUID()
        let challenge = try await s.provider.createQR(id: id)
        XCTAssertEqual(challenge.platform, .kugou); XCTAssertEqual(challenge.id, id)
        XCTAssertTrue(challenge.image.starts(with: [137, 80, 78, 71, 13, 10, 26, 10]))
        XCTAssertEqual(challenge.expiresAt, s.clock.now().addingTimeInterval(180))
        let responses = try [1, 2, 4].map {
            try MusicKugouFixture.response(["status": 1, "data": ["status": $0, "userid": 42, "token": MusicKugouFixture.token,
                "vip_token": "synthetic-private-vip", "unwanted": "must-not-persist"]])
        }
        await s.http.respond("/v2/get_userinfo_qrcode", responses)
        let waiting = try await s.provider.pollQR(id: id)
        XCTAssertEqual(waiting.progress, .waiting); XCTAssertNil(waiting.verified)
        s.clock.advance(3)
        let scanned = try await s.provider.pollQR(id: id)
        XCTAssertEqual(scanned.progress, .scanned); XCTAssertNil(scanned.verified)
        s.clock.advance(3)
        let result = try await s.provider.pollQR(id: id), verified = try XCTUnwrap(result.verified)
        XCTAssertEqual(result.progress, .confirmed); XCTAssertEqual(verified.nativeID, "42")
        let stored = try MusicQQWire.decode(verified.credential.dataRepresentation())
        XCTAssertEqual(Set(stored.keys), ["version", "platform", "accountID", "auth", "device"])
        let auth = try MusicQQWire.object(stored["auth"])
        XCTAssertEqual(Set(auth.keys), ["userid", "token", "vip_token"])
        XCTAssertNil(auth["unwanted"]); XCTAssertEqual(stored["platform"] as? String, "kg")
        let restored = try await s.provider.verify(verified.credential)
        XCTAssertEqual(restored.credential.dataRepresentation(), verified.credential.dataRepresentation())
        let requests = await s.http.recorded()
        let profileRequests = requests.filter { $0.url.path == "/v3/get_my_info" }
        XCTAssertEqual(profileRequests.count, 2)
        let mid1 = try MusicKugouFixture.fields(profileRequests[0])["mid"], mid2 = try MusicKugouFixture.fields(profileRequests[1])["mid"]
        XCTAssertEqual(mid1, mid2)
        do { _ = try await s.provider.pollQR(id: id); XCTFail("Consumed QR accepted") }
        catch { XCTAssertEqual(error as? MusicServiceError, .staleResult) }
    }
    func testCreateCooldownAndPollThrottleDoNotSendRequests() async throws {
        let s = MusicKugouTestSystem(), id = UUID()
        _ = try await s.provider.createQR(id: id)
        do { _ = try await s.provider.createQR(id: UUID()); XCTFail("Create cooldown bypass") }
        catch { XCTAssertEqual(error as? MusicServiceError, .rateLimited) }
        await s.http.respond("/v2/get_userinfo_qrcode", [try MusicKugouFixture.response(["status": 1, "data": ["status": 1]])])
        _ = try await s.provider.pollQR(id: id)
        do { _ = try await s.provider.pollQR(id: id); XCTFail("Poll throttle bypass") }
        catch { XCTAssertEqual(error as? MusicServiceError, .rateLimited) }
        let requests = await s.http.recorded()
        XCTAssertEqual(requests.filter { $0.url.path == "/v2/qrcode" }.count, 1)
        XCTAssertEqual(requests.filter { $0.url.path == "/v2/get_userinfo_qrcode" }.count, 1)
    }
    func testExpiryBeforePollAndUpstreamExpiredNeverVerifyProfile() async throws {
        for localExpiry in [true, false] {
            let s = MusicKugouTestSystem(), id = UUID()
            _ = try await s.provider.createQR(id: id)
            if localExpiry { s.clock.advance(181) }
            else { await s.http.respond("/v2/get_userinfo_qrcode", [try MusicKugouFixture.response(["status": 1, "data": ["status": 0]])]) }
            let result = try await s.provider.pollQR(id: id)
            XCTAssertEqual(result.progress, .expired); XCTAssertNil(result.verified)
            let requests = await s.http.recorded()
            XCTAssertFalse(requests.contains { $0.url.path == "/v3/get_my_info" })
            if localExpiry { XCTAssertFalse(requests.contains { $0.url.path == "/v2/get_userinfo_qrcode" }) }
        }
    }
    func testCancelRegistrationPreventsLateCreateAndCancelsTransport() async throws {
        let s = MusicKugouTestSystem(), id = UUID(), gate = MusicAccountTestGate()
        await s.http.hold("/risk/v2/r_register_dev", gate)
        let task = Task { try await s.provider.createQR(id: id) }
        await gate.started()
        await s.provider.cancelQR(id)
        await gate.release()
        do { _ = try await task.value; XCTFail("Cancelled registration produced QR") }
        catch { XCTAssertEqual(error as? MusicServiceError, .staleResult) }
        let requests = await s.http.recorded(), cancellations = await s.http.cancellationCount()
        XCTAssertEqual(requests.count, 1); XCTAssertEqual(cancellations, 1)
    }
    func testProfileCompletionAfterCancelExpiryOrRevokeCannotIssueCredential() async throws {
        for action in ["cancel", "expire", "revoke"] {
            let s = MusicKugouTestSystem(), id = UUID(), gate = MusicAccountTestGate()
            _ = try await s.provider.createQR(id: id)
            await s.http.hold("/v3/get_my_info", gate)
            let task = Task { try await s.provider.pollQR(id: id) }
            await gate.started()
            if action == "cancel" { await s.provider.cancelQR(id) }
            else if action == "expire" { s.clock.advance(181) }
            else { await s.provider.revoke() }
            await gate.release()
            do { _ = try await task.value; XCTFail("Late \(action) authentication accepted") }
            catch { XCTAssertEqual(error as? MusicServiceError, .staleResult) }
        }
    }
    func testDuplicatePollIsBusyAndOldCreateCannotDeleteNewSameID() async throws {
        let s = MusicKugouTestSystem(), id = UUID(), gate = MusicAccountTestGate()
        await s.http.hold("/v2/qrcode", gate)
        let old = Task { try await s.provider.createQR(id: id) }
        await gate.started()
        await s.provider.cancelQR(id); s.clock.advance(5)
        // Releasing a non-cooperative old transfer must not erase the new QR's nonce.
        let new = Task { try await s.provider.createQR(id: id) }
        await gate.release()
        do { _ = try await old.value; XCTFail("Old QR create accepted") } catch {}
        _ = try await new.value
        let pollGate = MusicAccountTestGate()
        await s.http.hold("/v2/get_userinfo_qrcode", pollGate)
        let poll = Task { try await s.provider.pollQR(id: id) }
        await pollGate.started()
        do { _ = try await s.provider.pollQR(id: id); XCTFail("Duplicate poll sent") }
        catch { XCTAssertEqual(error as? MusicServiceError, .busy) }
        await pollGate.release()
        let result = try await poll.value
        XCTAssertEqual(result.progress, .confirmed)
    }
    func testProfileRejectsConflictingIdentityMalformedProfileAndForeignStoredCredentials() async throws {
        let s = MusicKugouTestSystem()
        let cases: [[String: Any]] = [["userid": 99, "nickname": "wrong"], ["userid": 42, "nickname": 1]]
        for data in cases {
            await s.http.respond("/v3/get_my_info", [try MusicKugouFixture.response(["status": 1, "data": data])])
            do { _ = try await s.provider.verify(MusicKugouFixture.credential()); XCTFail("Unbound identity accepted") } catch {}
        }
        let before = await s.http.recorded().count
        let bytes = try MusicKugouFixture.credential().dataRepresentation()
        var object = try MusicQQWire.decode(bytes)
        object["platform"] = "tx"
        do { _ = try await s.provider.verify(AccountCredential(data: MusicQQWire.json(object))); XCTFail("Foreign credential accepted") }
        catch { XCTAssertEqual(error as? MusicServiceError, .invalidResponse) }
        object["platform"] = "kg"; object["accountID"] = "99"
        do { _ = try await s.provider.verify(AccountCredential(data: MusicQQWire.json(object))); XCTFail("Changed UID accepted") }
        catch { XCTAssertEqual(error as? MusicServiceError, .targetChanged) }
        let after = await s.http.recorded().count
        XCTAssertEqual(before, after)
    }
    func testEmptyRegistrationResponseIsValidAnonymousDeviceNotLogin() async throws {
        let s = MusicKugouTestSystem(), id = UUID()
        await s.http.respond("/risk/v2/r_register_dev", [try MusicKugouFixture.response(["status": 1, "data": []])])
        _ = try await s.provider.createQR(id: id)
        let requests = await s.http.recorded()
        let create = try XCTUnwrap(requests.first { $0.url.path == "/v2/qrcode" })
        let fields = try MusicKugouFixture.fields(create)
        XCTAssertEqual(fields["dfid"], "-"); XCTAssertNil(fields["userid"]); XCTAssertNil(fields["token"])
    }
    func testAuthErrorsSecurityHeaderAndUnsafeTransportErrorsAreSanitized() async throws {
        for (response, expected) in [
            (try MusicKugouFixture.response(["status": 0, "error_code": 20018]), MusicServiceError.authRequired),
            (try MusicKugouFixture.response(["status": 1], headers: ["SSA-Code": "synthetic-verification"]), .verificationRequired),
            (try MusicKugouFixture.response(["status": 1], status: 429), .rateLimited),
            (try MusicKugouFixture.response(["status": 1], status: 302, headers: ["location": "https://evil.invalid"]), .upstreamFailed)
        ] {
            let s = MusicKugouTestSystem()
            await s.http.respond("/v3/get_my_info", [response])
            do { _ = try await s.provider.verify(MusicKugouFixture.credential()); XCTFail("Error accepted") }
            catch { XCTAssertEqual(error as? MusicServiceError, expected); XCTAssertFalse(String(describing: error).contains(MusicKugouFixture.token)) }
        }
        let s = MusicKugouTestSystem()
        await s.http.fail("/v3/get_my_info")
        do { _ = try await s.provider.verify(MusicKugouFixture.credential()); XCTFail("Network failure accepted") }
        catch { XCTAssertEqual(error as? MusicServiceError, .unavailable); XCTAssertFalse(String(describing: error).contains("token=")) }
    }
    func testRestoreAfterRevokeIgnoresOldProfileButCanVerifyNewEpoch() async throws {
        let s = MusicKugouTestSystem(), gate = MusicAccountTestGate()
        await s.http.hold("/v3/get_my_info", gate)
        let task = Task { try await s.provider.verify(MusicKugouFixture.credential()) }
        await gate.started(); await s.provider.revoke(); await gate.release()
        do { _ = try await task.value; XCTFail("Old restore accepted") }
        catch { XCTAssertEqual(error as? MusicServiceError, .staleResult) }
        let next = try await s.provider.verify(MusicKugouFixture.credential(uid: "99", token: "synthetic-other-token"))
        XCTAssertEqual(next.nativeID, "99")
    }
}
