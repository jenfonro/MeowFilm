#if os(iOS) || os(macOS)
import Foundation
import XCTest
@testable import MeowMusicFeature

private final class LoginParityClock: @unchecked Sendable {
    private let lock = NSLock()
    private var value = Date(timeIntervalSince1970: 1_800_000_000)
    func now() -> Date { lock.lock(); defer { lock.unlock() }; return value }
    func advance(_ seconds: TimeInterval) { lock.lock(); value.addTimeInterval(seconds); lock.unlock() }
}
private actor LoginParityGate {
    private var entered = false, released = false
    private var starts: [CheckedContinuation<Void, Never>] = []
    private var waits: [CheckedContinuation<Void, Never>] = []
    func wait() async {
        entered = true; starts.forEach { $0.resume() }; starts = []
        if !released { await withCheckedContinuation { waits.append($0) } }
    }
    func started() async { if !entered { await withCheckedContinuation { starts.append($0) } } }
    func release() { released = true; waits.forEach { $0.resume() }; waits = [] }
}
private actor LoginParityService: MusicAccountsServing, MusicAccountsSMSServing, MusicAccountsProfileServing {
    let clock: LoginParityClock
    var rows = MusicAccountPlatform.allCases.map { MusicAccountStatus(platform: $0, phase: .signedOut, canLogin: true) }
    var calls: [String] = [], cancelled: [UUID] = []
    var challenges: [UUID: MusicAccountPlatform] = [:]
    var hold: (String, LoginParityGate)?
    init(_ clock: LoginParityClock) { self.clock = clock }
    func block(_ name: String, _ gate: LoginParityGate) { hold = (name, gate) }
    func captured() -> [String] { calls }
    private func row(_ platform: MusicAccountPlatform, _ phase: MusicAccountStatus.Phase, profile: MusicAccountProfile? = nil) -> MusicAccountStatus {
        let value = MusicAccountStatus(platform: platform, phase: phase, nickname: profile?.nickname ?? "", canLogin: true, profile: profile)
        rows = rows.map { $0.platform == platform ? value : $0 }; return value
    }
    func statuses() async throws -> [MusicAccountStatus] { rows }
    func validate(platform: MusicAccountPlatform) async throws -> MusicAccountStatus { row(platform, .signedIn) }
    func beginLogin(platform: MusicAccountPlatform) async throws -> MusicLoginChallenge {
        calls.append("qr"); _ = row(platform, .signingIn)
        let id = UUID(); challenges[id] = platform
        return .init(id: id, platform: platform, image: Data([1, 2]), expiresAt: clock.now().addingTimeInterval(180))
    }
    func checkLogin(challengeID: UUID) async throws -> MusicLoginProgress { calls.append("poll"); return .waiting }
    func cancelLogin(challengeID: UUID) async {
        cancelled.append(challengeID)
        if let platform = challenges.removeValue(forKey: challengeID),
           rows.first(where: { $0.platform == platform })?.phase == .signingIn {
            _ = row(platform, .signedOut)
        }
    }
    func logout(platform: MusicAccountPlatform) async throws { calls.append("logout"); _ = row(platform, .signedOut) }
    func sendSMS(platform: MusicAccountPlatform, countryCode: String, phone: String, attemptID: UUID) async throws -> MusicSMSSendReceipt {
        calls.append("send"); _ = row(platform, .signingIn)
        if let hold, hold.0 == "send" { await hold.1.wait() }
        return .init(retryAt: clock.now().addingTimeInterval(60))
    }
    func submitSMS(platform: MusicAccountPlatform, countryCode: String, phone: String, code: String, attemptID: UUID) async throws -> MusicAccountStatus {
        calls.append("submit")
        if let hold, hold.0 == "submit" {
            await hold.1.wait()
            // Deliberately return a late result without mutating account state.
            // The owner, not this page, guards whether authentication committed.
            return .init(platform: platform, phase: .signedIn)
        }
        return row(platform, .signedIn)
    }
    func cancelSMSLogin(attemptID: UUID) async { cancelled.append(attemptID) }
    func refreshProfile(platform: MusicAccountPlatform) async throws -> MusicAccountStatus {
        calls.append("profile")
        let value = row(platform, .signedIn, profile: .init(nickname: "合成资料",
            avatarURL: URL(string: "https://p1.music.126.net/synthetic.jpg"),
            membership: .init(vip: true, tier: "vip", expiresAt: clock.now().addingTimeInterval(300))))
        if let hold, hold.0 == "profile" { await hold.1.wait() }
        return value
    }
}

final class MusicAccountLoginParityTests: XCTestCase {
    @MainActor func testNoLoginRequestOnOpenAndEveryExplicitPlatformLoginDefaultsToQR() async {
        let clock = LoginParityClock(), service = LoginParityService(clock)
        let model = MusicAccountsViewModel(service: service, clock: { clock.now() })
        model.activate(); await model.flush()
        XCTAssertNil(model.loginPlatform); XCTAssertNil(model.challenge)
        let before = await service.captured(); XCTAssertTrue(before.isEmpty)
        for platform in MusicAccountPlatform.allCases {
            model.selectLogin(platform); await model.flush()
            XCTAssertEqual(model.loginMethod, .qr); XCTAssertEqual(model.countryCode, "86")
            XCTAssertEqual(model.challenge?.platform, platform)
            XCTAssertFalse(model.canSendSMS)
            model.setForeground(false); model.setForeground(true)
        }
        XCTAssertFalse(model.supportsSMS(.qq))
        let calls = await service.captured()
        XCTAssertEqual(calls.filter { $0 == "qr" }.count, 3)
        XCTAssertFalse(calls.contains("send")); XCTAssertFalse(calls.contains("submit"))
        model.deactivate()
    }
    @MainActor func testSendIsExplicitSingleAndCooldownSurvivesMethodSwitchAndBackground() async {
        let clock = LoginParityClock(), service = LoginParityService(clock)
        let model = MusicAccountsViewModel(service: service, clock: { clock.now() })
        model.activate(); await model.flush(); model.selectLogin(.netease); await model.flush()
        model.selectMethod(.sms)
        model.phone = "10000000000"; model.code = "000000"
        model.sendSMS(); model.sendSMS(); await model.flush()
        XCTAssertEqual(model.resendSeconds, 60); XCTAssertFalse(model.canSendSMS)
        model.selectMethod(.qr); await model.flush()
        XCTAssertEqual(model.code, ""); XCTAssertEqual(model.phone, "10000000000")
        model.selectMethod(.sms)
        XCTAssertEqual(model.resendSeconds, 60)
        model.setForeground(false); clock.advance(61)
        XCTAssertFalse(model.canSendSMS)
        model.setForeground(true)
        XCTAssertEqual(model.resendSeconds, 0); XCTAssertTrue(model.canSendSMS)
        let calls = await service.captured()
        XCTAssertEqual(calls.filter { $0 == "send" }.count, 1)
        XCTAssertFalse(calls.contains("submit"))
        model.deactivate()
    }
    @MainActor func testSMSSubmitEnrichesSameAccountAndClearsSensitiveInputs() async {
        let clock = LoginParityClock(), service = LoginParityService(clock)
        let model = MusicAccountsViewModel(service: service, clock: { clock.now() })
        model.activate(); await model.flush(); model.selectLogin(.kugou); await model.flush()
        model.selectMethod(.sms)
        model.phone = "10000000000"; model.code = "000000"
        XCTAssertTrue(model.canSubmitSMS)
        model.submitSMS(); await model.flush()
        XCTAssertNil(model.loginPlatform); XCTAssertEqual(model.code, ""); XCTAssertEqual(model.phone, "")
        let row = model.rows.first { $0.platform == .kugou }
        XCTAssertEqual(row?.profile?.nickname, "合成资料"); XCTAssertEqual(row?.profile?.membership.vip, true)
        let calls = await service.captured()
        XCTAssertEqual(calls, ["qr", "submit", "profile"], "Submit may use an already received code; it must not implicitly send")
        model.deactivate()
    }
    @MainActor func testLateSMSCannotRefillAfterProtectionOrSwitchToAnotherPlatform() async {
        for lock in [false, true] {
            let clock = LoginParityClock(), service = LoginParityService(clock), gate = LoginParityGate()
            let model = MusicAccountsViewModel(service: service, clock: { clock.now() })
            model.activate(); await model.flush(); model.selectLogin(.netease); await model.flush()
            model.selectMethod(.sms)
            model.phone = "10000000000"; model.code = "000000"
            await service.block("submit", gate)
            model.submitSMS(); let pending = model.pendingOperation
            await gate.started()
            if lock { model.setAccess(isAvailable: false, accessGeneration: UUID()) }
            else { model.selectLogin(.kugou); await model.flush() }
            await gate.release(); await pending?.value
            XCTAssertEqual(model.code, ""); XCTAssertEqual(model.phone, "")
            if lock { XCTAssertTrue(model.rows.isEmpty); XCTAssertNil(model.loginPlatform) }
            else { XCTAssertEqual(model.loginPlatform, .kugou); XCTAssertEqual(model.loginMethod, .qr) }
            let calls = await service.captured(); XCTAssertFalse(calls.contains("profile"))
            model.deactivate()
        }
    }
    @MainActor func testQRExpiresWhileBackgroundWithoutPollAndRequiresExplicitRegeneration() async {
        let clock = LoginParityClock(), service = LoginParityService(clock)
        let model = MusicAccountsViewModel(service: service, clock: { clock.now() })
        model.activate(); await model.flush(); model.beginLogin(.qq); await model.flush()
        let old = model.challenge?.id
        model.setForeground(false); clock.advance(181)
        var calls = await service.captured(); XCTAssertFalse(calls.contains("poll"))
        model.setForeground(true); await model.pendingPoll?.value
        XCTAssertEqual(model.progress, .expired); XCTAssertEqual(model.challenge?.id, old)
        calls = await service.captured(); XCTAssertEqual(calls, ["qr"])
        model.beginLogin(.qq); await model.flush()
        XCTAssertNotEqual(model.challenge?.id, old); XCTAssertEqual(model.progress, .waiting)
        model.deactivate()
    }
    @MainActor func testLateProfileCannotRefillAfterPageLeaves() async {
        let clock = LoginParityClock(), service = LoginParityService(clock), gate = LoginParityGate()
        let model = MusicAccountsViewModel(service: service, clock: { clock.now() })
        model.activate(); await model.flush()
        await service.block("profile", gate)
        model.validate(.netease); let pending = model.pendingOperation
        await gate.started(); model.deactivate()
        await gate.release(); await pending?.value
        XCTAssertTrue(model.rows.isEmpty); XCTAssertNil(model.error); XCTAssertFalse(model.isBusy)
    }

    @MainActor func testBackgroundCancelsInFlightSMSWithoutResendingOrPublishingLateResult() async {
        let clock = LoginParityClock(), service = LoginParityService(clock), gate = LoginParityGate()
        let model = MusicAccountsViewModel(service: service, clock: { clock.now() })
        model.activate(); await model.flush(); model.selectLogin(.netease); await model.flush()
        model.selectMethod(.sms); model.phone = "10000000000"; model.code = "000000"
        await service.block("submit", gate)
        model.submitSMS(); let pending = model.pendingOperation
        await gate.started()
        model.setForeground(false)
        XCTAssertFalse(model.isBusy); XCTAssertNil(model.loginPlatform); XCTAssertEqual(model.code, "")
        await gate.release(); await pending?.value
        XCTAssertNil(model.error)
        XCTAssertFalse(model.rows.contains { $0.phase == .signedIn })
        model.setForeground(true); await model.flush()
        let calls = await service.captured()
        XCTAssertEqual(calls.filter { $0 == "submit" }.count, 1)
        XCTAssertEqual(calls.filter { $0 == "qr" }.count, 1)
        XCTAssertFalse(calls.contains("send")); XCTAssertFalse(calls.contains("profile"))
        model.deactivate()
    }
}
#endif
