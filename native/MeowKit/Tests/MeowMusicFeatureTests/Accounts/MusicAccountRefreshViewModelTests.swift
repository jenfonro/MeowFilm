#if os(iOS) || os(macOS)
import Foundation
import XCTest
@testable import MeowMusicFeature

/// Deliberately ignores cancellation so a late provider reply exercises the
/// page epoch, not just cooperative cancellation inside a test double.
private actor AccountRefreshPageGate {
    private var entered = false
    private var released = false
    private var arrival: CheckedContinuation<Void, Never>?
    private var waiting: CheckedContinuation<Void, Never>?
    func wait() async {
        entered = true
        arrival?.resume(); arrival = nil
        if !released { await withCheckedContinuation { waiting = $0 } }
    }
    func started() async {
        if !entered { await withCheckedContinuation { arrival = $0 } }
    }
    func release() {
        released = true
        waiting?.resume(); waiting = nil
    }
}

private struct AccountRefreshPageCounts: Sendable {
    var statuses = 0
    var refreshed: [MusicAccountPlatform] = []
    var validated: [MusicAccountPlatform] = []
    var began = 0
    var loggedOut: [MusicAccountPlatform] = []
}

private struct AccountRefreshUntrustedError: Error, LocalizedError {
    var errorDescription: String? { "合成上游内部错误，不应显示" }
}

/// The default fixture intentionally DOES NOT conform to the new optional
/// capability. A separate adapter below must explicitly opt in.
private actor AccountRefreshPageService: MusicAccountsServing {
    private var rows: [MusicAccountStatus]
    private var counts = AccountRefreshPageCounts()
    private var gate: AccountRefreshPageGate?
    private var reply: MusicAccountStatus?
    private var afterRefresh: [MusicAccountStatus]?
    private var failure: MusicServiceError?
    private var statusFailure: MusicServiceError?
    private var statusFailureAfterRefresh: MusicServiceError?
    private var untrustedFailure = false

    init(rows: [MusicAccountStatus] = [
        .init(platform: .qq, phase: .signedIn, nickname: "合成账号", canLogin: true)
    ]) { self.rows = rows }

    func configureRefresh(
        reply: MusicAccountStatus? = nil, rows: [MusicAccountStatus]? = nil,
        failure: MusicServiceError? = nil, untrustedFailure: Bool = false,
        gate: AccountRefreshPageGate? = nil, statusFailureAfterRefresh: MusicServiceError? = nil
    ) {
        self.reply = reply; afterRefresh = rows; self.failure = failure
        self.untrustedFailure = untrustedFailure; self.gate = gate
        self.statusFailureAfterRefresh = statusFailureAfterRefresh
    }
    func replaceRows(_ value: [MusicAccountStatus]) { rows = value }
    func snapshotCounts() -> AccountRefreshPageCounts { counts }
    func statuses() async throws -> [MusicAccountStatus] {
        counts.statuses += 1
        if let statusFailure { throw statusFailure }
        return rows
    }
    func validate(platform: MusicAccountPlatform) async throws -> MusicAccountStatus {
        counts.validated.append(platform)
        return .init(platform: platform, phase: .signedIn, canLogin: true)
    }
    func beginLogin(platform: MusicAccountPlatform) async throws -> MusicLoginChallenge {
        counts.began += 1
        return .init(id: UUID(), platform: platform, image: Data([1]), expiresAt: .distantFuture)
    }
    func checkLogin(challengeID: UUID) async throws -> MusicLoginProgress { .waiting }
    func cancelLogin(challengeID: UUID) async {}
    func logout(platform: MusicAccountPlatform) async throws {
        counts.loggedOut.append(platform)
        rows = rows.map { $0.platform == platform ? .init(platform: platform, phase: .signedOut, canLogin: true) : $0 }
    }
    func explicitNativeRefresh(platform: MusicAccountPlatform) async throws -> MusicAccountStatus {
        counts.refreshed.append(platform)
        let result = reply ?? MusicAccountStatus(platform: platform, phase: .signedIn, nickname: "合成返回值", canLogin: true)
        let capturedFailure = failure
        let capturedUntrustedFailure = untrustedFailure
        if let afterRefresh { rows = afterRefresh }
        statusFailure = statusFailureAfterRefresh
        if let gate { await gate.wait() }
        if capturedUntrustedFailure { throw AccountRefreshUntrustedError() }
        if let capturedFailure { throw capturedFailure }
        return result // no late writes after logout/revocation
    }
}

private struct RefreshCapablePageService: MusicAccountsServing, MusicAccountsRefreshing {
    let base: AccountRefreshPageService
    func statuses() async throws -> [MusicAccountStatus] { try await base.statuses() }
    func validate(platform: MusicAccountPlatform) async throws -> MusicAccountStatus { try await base.validate(platform: platform) }
    func beginLogin(platform: MusicAccountPlatform) async throws -> MusicLoginChallenge { try await base.beginLogin(platform: platform) }
    func checkLogin(challengeID: UUID) async throws -> MusicLoginProgress { try await base.checkLogin(challengeID: challengeID) }
    func cancelLogin(challengeID: UUID) async { await base.cancelLogin(challengeID: challengeID) }
    func logout(platform: MusicAccountPlatform) async throws { try await base.logout(platform: platform) }
    func refresh(platform: MusicAccountPlatform) async throws -> MusicAccountStatus { try await base.explicitNativeRefresh(platform: platform) }
}

final class MusicAccountRefreshViewModelTests: XCTestCase {
    @MainActor func testServingWithoutOptionalCapabilityNeverOffersOrCallsRefresh() async {
        let service = AccountRefreshPageService(rows: [
            .init(platform: .qq, phase: .signedIn, canLogin: true),
            .init(platform: .netease, phase: .requiresValidation, canLogin: true)
        ])
        let model = MusicAccountsViewModel(service: service)
        model.activate(); await model.flush()
        XCTAssertFalse(model.supportsSessionRefresh)
        for platform in [MusicAccountPlatform.qq, .netease] {
            XCTAssertFalse(model.showsSessionRefresh(platform))
            XCTAssertFalse(model.canRefreshSession(platform))
            model.refreshSession(platform)
        }
        await model.flush()
        let counts = await service.snapshotCounts()
        XCTAssertTrue(counts.refreshed.isEmpty)
        XCTAssertTrue(counts.validated.isEmpty)
        XCTAssertEqual(counts.began, 0)
        XCTAssertNil(model.refreshingPlatform); XCTAssertNil(model.error)
        model.deactivate()
    }

    @MainActor func testRefreshIsExplicitTargetsOnePlatformAndUsesAuthoritativeStatuses() async {
        let base = AccountRefreshPageService(rows: [
            .init(platform: .qq, phase: .signedIn, nickname: "合成QQ", canLogin: true),
            .init(platform: .kugou, phase: .requiresValidation, canLogin: true)
        ])
        let model = MusicAccountsViewModel(service: RefreshCapablePageService(base: base))
        model.activate(); await model.flush()
        model.reload(); await model.flush()
        let before = await base.snapshotCounts()
        XCTAssertTrue(before.refreshed.isEmpty, "Loading/status reads must not renew credentials.")
        XCTAssertTrue(model.supportsSessionRefresh)
        XCTAssertTrue(model.showsSessionRefresh(.qq))
        XCTAssertTrue(model.showsSessionRefresh(.kugou))
        XCTAssertFalse(model.showsSessionRefresh(.netease))
        await base.configureRefresh(
            reply: .init(platform: .kugou, phase: .signedIn, nickname: "合成返回值", canLogin: true),
            rows: [.init(platform: .qq, phase: .signedIn, nickname: "合成QQ", canLogin: true),
                   .init(platform: .kugou, phase: .signedIn, nickname: "合成权威状态", canLogin: true)]
        )
        model.refreshSession(.kugou)
        XCTAssertTrue(model.isBusy); XCTAssertEqual(model.refreshingPlatform, .kugou)
        await model.flush()
        let counts = await base.snapshotCounts()
        XCTAssertEqual(counts.refreshed, [.kugou])
        XCTAssertTrue(counts.validated.isEmpty); XCTAssertEqual(counts.began, 0)
        XCTAssertEqual(model.rows.first(where: { $0.platform == .kugou })?.nickname, "合成权威状态")
        XCTAssertEqual(model.rows.first(where: { $0.platform == .qq })?.nickname, "合成QQ")
        XCTAssertFalse(model.isBusy); XCTAssertNil(model.refreshingPlatform); XCTAssertNil(model.error)
        model.deactivate()
    }

    @MainActor func testIneligiblePhasesInactiveAndLockedPagesDoNotOfferRefresh() async {
        let base = AccountRefreshPageService()
        let model = MusicAccountsViewModel(service: RefreshCapablePageService(base: base))
        model.refreshSession(.qq)
        XCTAssertFalse(model.showsSessionRefresh(.qq))
        model.activate(); await model.flush()
        let ineligible: [MusicAccountStatus.Phase] = [.signedOut, .signingIn, .unavailable, .removalPending]
        for phase in ineligible {
            await base.replaceRows([.init(platform: .qq, phase: phase, canLogin: true)])
            model.reload(); await model.flush()
            XCTAssertFalse(model.showsSessionRefresh(.qq))
            model.refreshSession(.qq); await model.flush()
        }
        model.setAccess(isAvailable: false, accessGeneration: UUID())
        model.refreshSession(.qq); await model.flush()
        XCTAssertFalse(model.canRefreshSession(.qq))
        let counts = await base.snapshotCounts()
        XCTAssertTrue(counts.refreshed.isEmpty)
        model.deactivate()
    }

    @MainActor func testBusyStateShowsProgressAndRejectsRepeatedRefresh() async {
        let base = AccountRefreshPageService(), gate = AccountRefreshPageGate()
        let model = MusicAccountsViewModel(service: RefreshCapablePageService(base: base))
        model.activate(); await model.flush()
        await base.configureRefresh(gate: gate)
        model.refreshSession(.qq)
        let pending = model.pendingOperation
        await gate.started()
        XCTAssertEqual(model.refreshingPlatform, .qq)
        XCTAssertTrue(model.showsSessionRefresh(.qq), "Keep the disabled action visible while it is busy.")
        XCTAssertFalse(model.canRefreshSession(.qq))
        model.refreshSession(.qq)
        model.validate(.qq)
        let busy = await base.snapshotCounts()
        XCTAssertEqual(busy.refreshed, [.qq]); XCTAssertTrue(busy.validated.isEmpty)
        await gate.release(); await pending?.value
        XCTAssertFalse(model.isBusy); XCTAssertNil(model.refreshingPlatform)
        model.deactivate()
    }

    @MainActor func testCommitUnconfirmedReadsRevokedStatusButNeverAutoValidatesOrLogsIn() async {
        let base = AccountRefreshPageService()
        let model = MusicAccountsViewModel(service: RefreshCapablePageService(base: base))
        model.activate(); await model.flush()
        await base.configureRefresh(
            rows: [.init(platform: .qq, phase: .requiresValidation, canLogin: true)],
            failure: .commitUnconfirmed
        )
        model.refreshSession(.qq); await model.flush()
        XCTAssertMusicDiagnosticMessage(model.error, "QQ 音乐 · 刷新登录会话：保存结果未确认")
        XCTAssertEqual(model.rows.first?.phase, .requiresValidation)
        XCTAssertEqual(model.rows.first?.nickname, "")
        XCTAssertEqual(model.rows.first?.canLogin, true)
        XCTAssertFalse(model.isBusy); XCTAssertNil(model.refreshingPlatform)
        let counts = await base.snapshotCounts()
        XCTAssertEqual(counts.refreshed, [.qq])
        XCTAssertEqual(counts.statuses, 2)
        XCTAssertTrue(counts.validated.isEmpty); XCTAssertEqual(counts.began, 0)
        // Recovery remains a separate explicit user action.
        model.validate(.qq); await model.flush()
        let afterValidation = await base.snapshotCounts()
        XCTAssertEqual(afterValidation.validated, [.qq])
        XCTAssertEqual(afterValidation.refreshed, [.qq])
        model.deactivate()
    }

    @MainActor func testUntrustedFailureIsSanitizedAndDoesNotLeakReturnedDiagnostic() async {
        let base = AccountRefreshPageService()
        let model = MusicAccountsViewModel(service: RefreshCapablePageService(base: base))
        model.activate(); await model.flush()
        await base.configureRefresh(untrustedFailure: true)
        model.refreshSession(.qq); await model.flush()
        XCTAssertMusicDiagnosticMessage(model.error, "QQ 音乐 · 刷新登录会话：服务未能完成请求，原因尚未明确")
        XCTAssertNotEqual(model.error, AccountRefreshUntrustedError().localizedDescription)
        XCTAssertFalse(model.error?.contains(AccountRefreshUntrustedError().localizedDescription) ?? true)
        XCTAssertFalse(model.isBusy); XCTAssertNil(model.refreshingPlatform)
        model.deactivate()
    }

    @MainActor func testFailedFollowupStatusReadDoesNotRetryOrApplyReturnedAccountRow() async {
        let base = AccountRefreshPageService()
        let model = MusicAccountsViewModel(service: RefreshCapablePageService(base: base))
        model.activate(); await model.flush()
        await base.configureRefresh(statusFailureAfterRefresh: .timeout)
        model.refreshSession(.qq); await model.flush()
        let counts = await base.snapshotCounts()
        XCTAssertEqual(counts.statuses, 2)
        XCTAssertEqual(counts.refreshed, [.qq])
        XCTAssertEqual(model.rows.first?.nickname, "合成账号")
        XCTAssertMusicDiagnosticMessage(model.error, "QQ 音乐 · 刷新登录会话：请求超时")
        XCTAssertFalse(model.isBusy); XCTAssertNil(model.refreshingPlatform)
        model.deactivate()
    }

    @MainActor func testLateSuccessAndFailureAfterAccessRevocationCannotRefillNewEpoch() async {
        let failures: [MusicServiceError?] = [nil, .timeout]
        for failure in failures {
            let base = AccountRefreshPageService(), gate = AccountRefreshPageGate()
            let model = MusicAccountsViewModel(service: RefreshCapablePageService(base: base))
            model.activate(accessGeneration: UUID()); await model.flush()
            await base.configureRefresh(failure: failure, gate: gate)
            model.refreshSession(.qq); let pending = model.pendingOperation
            await gate.started()
            model.setAccess(isAvailable: false, accessGeneration: UUID())
            XCTAssertTrue(model.rows.isEmpty); XCTAssertNil(model.refreshingPlatform); XCTAssertFalse(model.isBusy)
            await base.replaceRows([.init(platform: .qq, phase: .signedOut, canLogin: true)])
            model.setAccess(isAvailable: true, accessGeneration: UUID()); await model.flush()
            await gate.release(); await pending?.value
            XCTAssertEqual(model.rows.first?.phase, .signedOut)
            XCTAssertEqual(model.rows.first?.nickname, "")
            XCTAssertNil(model.error); XCTAssertNil(model.refreshingPlatform)
            let counts = await base.snapshotCounts()
            XCTAssertEqual(counts.statuses, 2, "The old result must not trigger an extra status reload.")
            model.deactivate()
        }
    }

    @MainActor func testLogoutDuringRefreshCannotBeRevivedByLateSuccessfulReply() async {
        let base = AccountRefreshPageService(), gate = AccountRefreshPageGate()
        let model = MusicAccountsViewModel(service: RefreshCapablePageService(base: base))
        model.activate(); await model.flush()
        await base.configureRefresh(gate: gate)
        model.refreshSession(.qq); let pending = model.pendingOperation
        await gate.started()
        model.logout(.qq); await model.flush()
        XCTAssertEqual(model.rows.first?.phase, .signedOut)
        await gate.release(); await pending?.value
        XCTAssertEqual(model.rows.first?.phase, .signedOut); XCTAssertEqual(model.rows.first?.nickname, "")
        XCTAssertNil(model.error); XCTAssertNil(model.refreshingPlatform); XCTAssertFalse(model.isBusy)
        let counts = await base.snapshotCounts()
        XCTAssertEqual(counts.loggedOut, [.qq])
        XCTAssertEqual(counts.statuses, 2)
        model.deactivate()
    }

    @MainActor func testExplicitCancelRejectsLateErrorAndDoesNotLogoutAccount() async {
        let base = AccountRefreshPageService(), gate = AccountRefreshPageGate()
        let model = MusicAccountsViewModel(service: RefreshCapablePageService(base: base))
        model.activate(); await model.flush()
        await base.configureRefresh(failure: .timeout, gate: gate)
        model.refreshSession(.qq); let pending = model.pendingOperation
        await gate.started()
        model.cancelSessionRefresh()
        XCTAssertNil(model.refreshingPlatform)
        await model.flush()
        await gate.release(); await pending?.value
        XCTAssertNil(model.error); XCTAssertFalse(model.isBusy)
        XCTAssertEqual(model.rows.first?.phase, .signedIn)
        let counts = await base.snapshotCounts()
        XCTAssertTrue(counts.loggedOut.isEmpty); XCTAssertTrue(counts.validated.isEmpty)
        XCTAssertEqual(counts.refreshed, [.qq])
        model.deactivate()
    }

    @MainActor func testPageDeactivationBeforeScheduledRefreshMakesNoNativeCall() async {
        let base = AccountRefreshPageService()
        let model = MusicAccountsViewModel(service: RefreshCapablePageService(base: base))
        model.activate(); await model.flush()
        model.refreshSession(.qq); let pending = model.pendingOperation
        model.deactivate(); await pending?.value
        let counts = await base.snapshotCounts()
        XCTAssertTrue(counts.refreshed.isEmpty)
        XCTAssertTrue(model.rows.isEmpty); XCTAssertNil(model.error); XCTAssertNil(model.refreshingPlatform)
    }

    @MainActor func testRefreshResponseForDifferentPlatformIsRejected() async {
        let base = AccountRefreshPageService()
        let model = MusicAccountsViewModel(service: RefreshCapablePageService(base: base))
        model.activate(); await model.flush()
        await base.configureRefresh(reply: .init(platform: .netease, phase: .signedIn, nickname: "合成其他平台", canLogin: true))
        model.refreshSession(.qq); await model.flush()
        XCTAssertMusicDiagnosticMessage(model.error, "QQ 音乐 · 刷新登录会话：平台响应格式异常")
        XCTAssertEqual(model.rows.first?.platform, .qq)
        XCTAssertEqual(model.rows.first?.nickname, "合成账号")
        model.deactivate()
    }
}
#endif
