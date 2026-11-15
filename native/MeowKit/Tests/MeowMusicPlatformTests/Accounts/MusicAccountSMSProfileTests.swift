import Foundation
import XCTest
import MeowAccounts
import MeowMusicFeature
import MeowRuntime
@testable import MeowMusicPlatform

/// Canonical runtime results only. SMS protocol/cooldown and membership wire
/// conversion run once in Node; native tests cover consent, commit and scope.
final class MusicAccountSMSProfileTests: XCTestCase {
    private let phone = "10000000000"
    @MainActor func testNodeSMSSendSurvivesCancellationAndCloseUntilItsActualReceiptSettles() async throws {
        let phone = phone
        for outcome in ["confirmed", "rejected", "unknown"] {
            let f = try MusicNativeFixture(platform: .netease, restored: false), id = UUID()
            try await f.enable()
            let entered = expectation(description: "SMS dispatched " + outcome)
            let gate = MusicAccountTestGate { entered.fulfill() }
            await f.platform.hold("sms.send", gate)
            if outcome != "confirmed" {
                await f.platform.fail("sms.send", code: outcome == "unknown" ? "SMS_SEND_UNCONFIRMED" : "RATE_LIMITED")
            }
            let pending = MusicAccountQueueOwnerCall("SMS settled " + outcome) {
                try await f.service.sendSMS(platform: .netease, countryCode: "86", phone: phone, attemptID: id)
            }
            defer { pending.cancel(); Task { await gate.release(); _ = await f.service.close(); await f.runtime.runtime.close() } }
            await fulfillment(of: [entered], timeout: 3)
            pending.cancel()
            let cancel = MusicAccountQueueOwnerCall("SMS page cancelled " + outcome) { await f.service.cancelSMSLogin(attemptID: id) }
            await fulfillment(of: [cancel.finished], timeout: 3)
            try cancel.value()
            let closing = MusicAccountQueueOwnerCall("SMS pending close " + outcome) { await f.service.close() }
            await fulfillment(of: [closing.finished], timeout: 3)
            let report = try closing.value()
            XCTAssertFalse(report.cleanupComplete)
            XCTAssertEqual(report.inFlight, 1)
            XCTAssertEqual(report.pendingCredentialWrites, 0)
            let repeated = await f.service.close()
            XCTAssertEqual(repeated, report)
            XCTAssertNil(pending.result)
            await gate.release()
            await fulfillment(of: [pending.finished], timeout: 3)
            if outcome == "confirmed" {
                XCTAssertEqual(try pending.value().retryAt, f.clock.now().addingTimeInterval(60))
            } else {
                XCTAssertThrowsError(try pending.value()) {
                    let failure = $0 as? MusicSMSFailure
                    XCTAssertEqual(failure?.reason, outcome == "unknown" ? .commitUnconfirmed : .rateLimited)
                    XCTAssertEqual(failure?.retryAt, outcome == "unknown" ? f.clock.now().addingTimeInterval(60) : nil)
                }
            }
            let requests = await f.platform.recorded()
            XCTAssertEqual(requests.filter { $0.operation == "sms.send" }.count, 1)
            XCTAssertNil(f.store.peek(f.identity))
            let drained = await f.service.close()
            XCTAssertTrue(drained.cleanupComplete)
            XCTAssertEqual(drained.inFlight, 0)
            await f.runtime.runtime.close()
        }
    }

    @MainActor func testBridgeFailureAfterSMSDispatchReturnsUnknownWithTheExistingClockAndNeverResends() async throws {
        let phone = phone
        let f = try MusicNativeFixture(platform: .netease, restored: false)
        try await f.enable()
        let entered = expectation(description: "SMS crossed the runtime dispatch boundary")
        let gate = MusicAccountTestGate { entered.fulfill() }
        await f.platform.hold("sms.send", gate)
        let pending = MusicAccountQueueOwnerCall("SMS bridge failure settled") {
            try await f.service.sendSMS(platform: .netease, countryCode: "86", phone: phone, attemptID: UUID())
        }
        defer { pending.cancel(); Task { await gate.release(); _ = await f.service.close() } }
        await fulfillment(of: [entered], timeout: 3)
        let bridge = MusicAccountQueueOwnerCall("runtime HTTP client closed") { await f.runtime.runtime.close() }
        await fulfillment(of: [bridge.finished, pending.finished], timeout: 3)
        try bridge.value()
        XCTAssertThrowsError(try pending.value()) {
            let failure = $0 as? MusicSMSFailure
            XCTAssertEqual(failure?.reason, .commitUnconfirmed)
            XCTAssertEqual(failure?.retryAt, f.clock.now().addingTimeInterval(60))
        }
        await gate.release()
        let requests = await f.platform.recorded()
        XCTAssertEqual(requests.filter { $0.operation == "sms.send" }.count, 1)
        XCTAssertNil(f.store.peek(f.identity))
        _ = await f.service.close()
    }

    func testUnconfirmedSMSSendReceiptHasTheSameUnknownCooldownWithoutAnAutomaticRetry() async throws {
        let f = try MusicNativeFixture(platform: .netease, restored: false)
        try await f.enable()
        await f.platform.replies("sms.send", .object(["confirmed": .bool(false)]))
        do {
            _ = try await f.service.sendSMS(platform: .netease, countryCode: "86", phone: phone, attemptID: UUID())
            XCTFail("An unconfirmed send cannot become a confirmed receipt")
        } catch {
            let failure = error as? MusicSMSFailure
            XCTAssertEqual(failure?.reason, .commitUnconfirmed)
            XCTAssertEqual(failure?.retryAt, f.clock.now().addingTimeInterval(60))
        }
        let requests = await f.platform.recorded()
        XCTAssertEqual(requests.filter { $0.operation == "sms.send" }.count, 1)
        XCTAssertNil(f.store.peek(f.identity))
        _ = await f.service.close()
        await f.runtime.runtime.close()
    }

    func testExplicitSMSSendDoesNotAuthenticateAndSubmitSavesBeforePublication() async throws {
        for platform in [MusicAccountPlatform.netease, .kugou] {
            let f = try MusicNativeFixture(platform: platform, restored: false), id = UUID()
            try await f.enable(); _ = try await f.service.statuses()
            let before = await f.platform.recorded(); XCTAssertTrue(before.isEmpty)
            let sent = try await f.service.sendSMS(platform: platform, countryCode: "86", phone: phone, attemptID: id)
            XCTAssertEqual(sent.retryAt, f.clock.now().addingTimeInterval(60)); XCTAssertNil(f.store.peek(f.identity))
            let status = try await f.service.submitSMS(platform: platform, countryCode: "86", phone: phone,
                code: "000000", attemptID: id)
            XCTAssertEqual(status.phase, .signedIn)
            let credential = try XCTUnwrap(f.store.peek(f.identity)).credential
            let text = String(decoding: credential.dataRepresentation(), as: UTF8.self)
            XCTAssertFalse(text.contains(phone)); XCTAssertFalse(text.contains("captcha"))
            let calls = await f.platform.recorded()
            XCTAssertEqual(calls.map(\.operation), ["sms.send", "sms.login"])
            XCTAssertEqual(calls.first?.parameters[music: "phone"], .string(phone))
            XCTAssertEqual(calls.last?.parameters[music: "captcha"], .string("000000"))
            let publication = f.runtime.requests().last { $0.transportOperation == "session.update" && $0.fields[music: "session"] != .null }
            XCTAssertEqual(publication?.fields[music: "session"][music: "userId"], .string("101"))
            XCTAssertEqual(f.store.calls().filter { $0 == "write" }.count, 1)
            _ = await f.service.close()
        }
    }
    func testQQSMSRemainsUnsupportedWithoutStorageOrRuntimeIO() async throws {
        let f = try MusicNativeFixture(restored: false); try await f.enable()
        for submit in [false, true] {
            do {
                if submit { _ = try await f.service.submitSMS(platform: .qq, countryCode: "86", phone: phone, code: "000000", attemptID: UUID()) }
                else { _ = try await f.service.sendSMS(platform: .qq, countryCode: "86", phone: phone, attemptID: UUID()) }
                XCTFail("No new QQ SMS capability")
            } catch { XCTAssertEqual(error as? MusicServiceError, .unsupported) }
        }
        let calls = await f.platform.recorded(); XCTAssertTrue(calls.isEmpty)
        XCTAssertTrue(f.store.calls().isEmpty); _ = await f.service.close()
    }
    func testRequiredSMSMaterialFailureIsAttributedToNodeAndNeverCommitsCredentials() async throws {
        let f = try MusicNativeFixture(platform: .netease, restored: false); try await f.enable()
        await f.platform.fail("sms.send", code: "INVALID_REQUEST")
        do { _ = try await f.service.sendSMS(platform: .netease, countryCode: "86", phone: "", attemptID: UUID()); XCTFail("Required material") }
        catch { XCTAssertEqual((error as? MusicSMSFailure)?.reason, .invalidRequest) }
        let calls = await f.platform.recorded()
        XCTAssertEqual(calls.map(\.operation), ["sms.send"]); XCTAssertEqual(calls.first?.parameters[music: "phone"], .string(""))
        XCTAssertNil(f.store.peek(f.identity)); _ = await f.service.close()
    }
    func testCancelledOrLockedSMSCannotCommitLateCredential() async throws {
        for lock in [false, true] {
            let f = try MusicNativeFixture(platform: .netease, restored: false), gate = MusicAccountTestGate(), id = UUID()
            try await f.enable(); await f.platform.hold("sms.login", gate)
            let task = Task { try await f.service.submitSMS(platform: .netease, countryCode: "86", phone: phone, code: "000000", attemptID: id) }
            await gate.started()
            if lock { _ = try await f.service.setAccess(available: false, generation: 2) }
            else { await f.service.cancelSMSLogin(attemptID: id) }
            await gate.release()
            do { _ = try await task.value; XCTFail("Late login") }
            catch { XCTAssertTrue([.cancelled, .staleResult, .accessUnavailable].contains(error as? MusicServiceError ?? .unavailable)) }
            XCTAssertNil(f.store.peek(f.identity))
            XCTAssertFalse(f.runtime.requests().contains { $0.transportOperation == "session.update" && $0.fields[music: "session"] != .null })
            let statuses = try await f.service.statuses(); XCTAssertNotEqual(statuses.first?.phase, .signedIn)
            _ = await f.service.close()
        }
    }
    func testSMSFailuresAndUnknownReceiptsNeverAutomaticallyResend() async throws {
        for (code, expected) in [("RATE_LIMITED", MusicServiceError.rateLimited), ("SMS_SEND_UNCONFIRMED", .commitUnconfirmed)] {
            let f = try MusicNativeFixture(platform: .netease, restored: false); try await f.enable()
            await f.platform.fail("sms.send", code: code)
            do { _ = try await f.service.sendSMS(platform: .netease, countryCode: "86", phone: phone, attemptID: UUID()); XCTFail("Failed send") }
            catch {
                let failure = error as? MusicSMSFailure
                XCTAssertEqual(failure?.reason, expected)
                if code == "SMS_SEND_UNCONFIRMED" {
                    XCTAssertEqual(failure?.retryAt, f.clock.now().addingTimeInterval(60))
                }
            }
            let calls = await f.platform.recorded(); XCTAssertEqual(calls.map(\.operation), ["sms.send"])
            XCTAssertNil(f.store.peek(f.identity)); _ = await f.service.close()
        }
    }
    func testCloseReportsUncooperativeSMSSendUntilItsActualReceiptSettles() async throws {
        let identity = try MusicAccountFixture.identity("wy"), store = MusicAccountTestStore()
        let provider = MusicAccountTestProvider(), gate = MusicAccountTestGate()
        let owner = NativeMusicAccountService(accounts: .init(store: store), scopes: [.netease: identity],
            providers: [.netease: provider])
        _ = try await owner.setAccess(available: true, generation: 1)
        await provider.holdSMS(gate)
        let pending = Task { try await owner.sendSMS(platform: .netease, countryCode: "86",
            phone: self.phone, attemptID: UUID()) }
        await gate.started()
        let closing = await owner.close()
        XCTAssertFalse(closing.cleanupComplete)
        XCTAssertEqual(closing.inFlight, 1)
        XCTAssertEqual(closing.pendingCredentialWrites, 0)
        await gate.release()
        let receipt = try await pending.value
        XCTAssertEqual(receipt.retryAt, .distantFuture, "Closing cannot erase a send which actually settled")
        let drained = await owner.close()
        XCTAssertTrue(drained.cleanupComplete)
        XCTAssertEqual(drained.inFlight, 0)
        XCTAssertNil(store.peek(identity))
    }
    func testTypedInvalidCodeAndAccountSelectionSurviveTheNativeOwner() async throws {
        for (platform, code, detail) in [(MusicAccountPlatform.netease, "INVALID_CODE", MusicSMSFailure.Detail.invalidCode),
                                        (.kugou, "ACCOUNT_SELECTION_REQUIRED", .accountSelectionRequired)] {
            let f = try MusicNativeFixture(platform: platform, restored: false); try await f.enable()
            await f.platform.fail("sms.login", code: code)
            do { _ = try await f.service.submitSMS(platform: platform, countryCode: "86", phone: phone, code: "000000", attemptID: UUID()); XCTFail("Authentication failed") }
            catch { XCTAssertEqual((error as? MusicSMSFailure)?.detail, detail) }
            XCTAssertNil(f.store.peek(f.identity)); _ = await f.service.close()
        }
    }
    func testMembershipAndProfileAreConsumedWithoutSecondIdentityOrRightsValidation() async throws {
        for platform in MusicAccountPlatform.allCases {
            let f = try MusicNativeFixture(platform: platform); try await f.enable()
            await f.platform.replies("account.profile", .object([
                "profile": .object(["displayName": .string("合成昵称"), "avatar": .string("https://not-a-cdn.invalid/avatar.png")]),
                "entitlements": .object(["membership": .object(["vip": .bool(true), "tier": .string("svip"),
                    "expiresAt": .number(f.clock.now().addingTimeInterval(300).timeIntervalSince1970 * 1000)]),
                    "recheckAt": .number(f.clock.now().addingTimeInterval(100).timeIntervalSince1970 * 1000)]),
                "unused": .array([.object(["cookie": .string("must-not-enter-public-profile")])])]))
            let status = try await f.service.refreshProfile(platform: platform)
            XCTAssertEqual(status.nickname, "合成昵称"); XCTAssertEqual(status.profile?.avatarURL?.host, "not-a-cdn.invalid")
            XCTAssertEqual(status.profile?.membership.vip, true); XCTAssertEqual(status.profile?.membership.tier, "svip")
            XCTAssertEqual(status.profile?.membership.expiresAt, f.clock.now().addingTimeInterval(300))
            let profile = try JSONEncoder().encode(XCTUnwrap(status.profile))
            XCTAssertFalse(String(decoding: profile, as: UTF8.self).contains("must-not-enter-public-profile"))
            let calls = await f.platform.recorded(); XCTAssertEqual(calls.map(\.operation), ["account.verify", "account.profile"])
            XCTAssertFalse(calls.contains { $0.operation == "audio.options" || $0.operation.hasPrefix("sms.") })
            _ = await f.service.close()
        }
    }
    func testUnknownMembershipIsNotFalseAndLogoutClearsTheProfile() async throws {
        let f = try MusicNativeFixture(platform: .netease); try await f.enable()
        await f.platform.replies("account.profile", .object(["profile": .object(["displayName": .string("合成昵称")]),
            "entitlements": .object(["membership": .object([:])])]))
        let status = try await f.service.refreshProfile(platform: .netease)
        XCTAssertEqual(status.profile?.nickname, "合成昵称"); XCTAssertNil(status.profile?.membership.vip)
        try await f.service.logout(platform: .netease)
        let statuses = try await f.service.statuses(); XCTAssertNil(statuses.first?.profile); XCTAssertEqual(statuses.first?.nickname, "")
        _ = await f.service.close()
    }
    func testOldProfileCannotRefillAfterLogout() async throws {
        let f = try MusicNativeFixture(platform: .netease), gate = MusicAccountTestGate()
        try await f.enable(); _ = try await f.service.validate(platform: .netease)
        await f.platform.hold("account.profile", gate)
        let task = Task { try await f.service.refreshProfile(platform: .netease) }
        await gate.started(); try await f.service.logout(platform: .netease); await gate.release()
        do { _ = try await task.value; XCTFail("Late profile") }
        catch { XCTAssertEqual(error as? MusicServiceError, .cancelled) }
        let statuses = try await f.service.statuses(); XCTAssertEqual(statuses.first?.phase, .signedOut); XCTAssertNil(statuses.first?.profile)
        _ = await f.service.close()
    }
    func testCurrentDisplayDTOEncodesAndReloadsItsActualFields() throws {
        let profile = MusicAccountProfile(nickname: "合成昵称", avatarURL: URL(string: "https://images.invalid/a.png"),
            membership: .init(vip: true, tier: "svip", expiresAt: Date(timeIntervalSince1970: 1_800_000_300)))
        let restored = try JSONDecoder().decode(MusicAccountProfile.self, from: JSONEncoder().encode(profile))
        XCTAssertEqual(restored, profile)
    }
}
