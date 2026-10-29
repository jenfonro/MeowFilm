#if os(iOS) || os(macOS)
import Foundation
import XCTest
@testable import MeowMusicFeature

private actor MusicAccountPageGate {
    private var entered = false, released = false
    private var start: CheckedContinuation<Void, Never>?
    private var waiting: CheckedContinuation<Void, Never>?
    func wait() async {
        entered = true; start?.resume(); start = nil
        if !released { await withCheckedContinuation { waiting = $0 } }
    }
    func started() async { if !entered { await withCheckedContinuation { start = $0 } } }
    func release() { released = true; waiting?.resume(); waiting = nil }
}
private actor MusicAccountPageService: MusicAccountsServing {
    var rows: [MusicAccountStatus] = [.init(platform: .qq, phase: .signedIn, nickname: "合成旧账号", canLogin: true)]
    var gate: (String, MusicAccountPageGate)?
    var cancelled: [UUID] = []
    var logoutCount = 0, beginCount = 0
    let id = UUID()
    func hold(_ name: String, _ value: MusicAccountPageGate) { gate = (name, value) }
    func clearGate() { gate = nil }
    func replace(_ value: [MusicAccountStatus]) { rows = value }
    func statuses() async throws -> [MusicAccountStatus] {
        let captured = rows
        if let gate, gate.0 == "statuses" { await gate.1.wait() }
        return captured // non-cooperative late old result is intentional
    }
    func validate(platform: MusicAccountPlatform) async throws -> MusicAccountStatus {
        if let gate, gate.0 == "validate" { await gate.1.wait() }
        return .init(platform: platform, phase: .signedIn, nickname: "合成晚到账号", canLogin: true)
    }
    func beginLogin(platform: MusicAccountPlatform) async throws -> MusicLoginChallenge {
        beginCount += 1
        rows = [.init(platform: platform, phase: .signingIn, canLogin: true)]
        if let gate, gate.0 == "begin" { await gate.1.wait() }
        return .init(id: id, platform: platform, image: Data([1, 2, 3]), expiresAt: Date().addingTimeInterval(180))
    }
    func checkLogin(challengeID: UUID) async throws -> MusicLoginProgress { .waiting }
    func cancelLogin(challengeID: UUID) async { cancelled.append(challengeID) }
    func logout(platform: MusicAccountPlatform) async throws {
        logoutCount += 1; rows = [.init(platform: platform, phase: .signedOut, canLogin: true)]
    }
    func cancellations() -> [UUID] { cancelled }
    func counts() -> (Int, Int) { (logoutCount, beginCount) }
}

final class MusicAccountsViewModelTests: XCTestCase {
    @MainActor func testDeactivationBeforeScheduledLoginStartsDoesNotCallService() async {
        let service = MusicAccountPageService()
        let page = MusicAccountsViewModel(service: service)
        page.activate(); await page.flush()
        page.beginLogin(.qq); let pending = page.pendingOperation
        page.deactivate(); await pending?.value
        let counts = await service.counts(); XCTAssertEqual(counts.1, 0)
        XCTAssertNil(page.challenge); XCTAssertTrue(page.rows.isEmpty)
    }

    @MainActor func testLateReloadCannotRefillAfterLockAndUnlock() async throws {
        let service = MusicAccountPageService(), gate = MusicAccountPageGate()
        await service.hold("statuses", gate)
        let model = MusicAccountsViewModel(service: service)
        model.activate(accessGeneration: UUID()); let pending = model.pendingOperation
        await gate.started()
        model.setAccess(isAvailable: false, accessGeneration: UUID())
        XCTAssertTrue(model.rows.isEmpty); XCTAssertNil(model.challenge)
        await service.clearGate()
        await service.replace([.init(platform: .qq, phase: .signedOut, canLogin: true)])
        model.setAccess(isAvailable: true, accessGeneration: UUID()); await model.flush()
        await gate.release(); await pending?.value
        XCTAssertEqual(model.rows.first?.phase, .signedOut); XCTAssertEqual(model.rows.first?.nickname, "")
        model.deactivate()
    }

    @MainActor func testLateReloadCannotRefillAfterPageLeaves() async throws {
        let service = MusicAccountPageService(), gate = MusicAccountPageGate()
        await service.hold("statuses", gate)
        let model = MusicAccountsViewModel(service: service)
        model.activate(); let pending = model.pendingOperation
        await gate.started(); model.deactivate()
        await gate.release(); await pending?.value
        XCTAssertTrue(model.rows.isEmpty); XCTAssertNil(model.error); XCTAssertFalse(model.isBusy)
        let counts = await service.counts(); XCTAssertEqual(counts.0, 0, "Leaving is not logout")
    }

    @MainActor func testOldValidationCannotRefillAfterExplicitLogout() async throws {
        let service = MusicAccountPageService(), gate = MusicAccountPageGate()
        let model = MusicAccountsViewModel(service: service)
        model.activate(); await model.flush()
        await service.hold("validate", gate)
        model.validate(.qq); let pending = model.pendingOperation
        await gate.started(); model.logout(.qq); await model.flush()
        await gate.release(); await pending?.value
        XCTAssertEqual(model.rows.first?.phase, .signedOut); XCTAssertEqual(model.rows.first?.nickname, "")
        let counts = await service.counts(); XCTAssertEqual(counts.0, 1)
        model.deactivate()
    }

    @MainActor func testLateQRCreateIsCancelledInsteadOfDisplayedOnDepartedPage() async throws {
        let service = MusicAccountPageService(), gate = MusicAccountPageGate()
        let model = MusicAccountsViewModel(service: service)
        model.activate(); await model.flush(); await service.hold("begin", gate)
        model.beginLogin(.qq); let pending = model.pendingOperation
        await gate.started(); model.deactivate()
        await gate.release(); await pending?.value
        XCTAssertNil(model.challenge); XCTAssertTrue(model.rows.isEmpty)
        let cancelled = await service.cancellations(); XCTAssertEqual(cancelled.count, 1)
    }

    @MainActor func testBindingEpochChangesWhileAvailableRejectOldQR() async throws {
        let service = MusicAccountPageService(), gate = MusicAccountPageGate()
        let model = MusicAccountsViewModel(service: service)
        model.activate(accessGeneration: UUID()); await model.flush()
        await service.hold("begin", gate); model.beginLogin(.qq); let pending = model.pendingOperation
        await gate.started(); model.setAccess(isAvailable: true, accessGeneration: UUID())
        await model.flush(); await gate.release(); await pending?.value
        XCTAssertNil(model.challenge); XCTAssertFalse(model.isBusy)
        let cancelled = await service.cancellations(); XCTAssertEqual(cancelled.count, 1)
        model.deactivate()
    }

    @MainActor func testUnavailableRowsNeverExposeNicknameOrLoginAndLockedPageDoesNoWork() async throws {
        let service = MusicAccountPageService()
        await service.replace([.init(platform: .qq, phase: .unavailable, nickname: "合成不应显示", canLogin: true)])
        let model = MusicAccountsViewModel(service: service)
        model.activate(); await model.flush()
        XCTAssertEqual(model.rows.first?.nickname, ""); XCTAssertEqual(model.rows.first?.canLogin, false)
        model.setAccess(isAvailable: false); model.beginLogin(.qq); model.validate(.qq); await model.flush()
        let counts = await service.counts(); XCTAssertEqual(counts.1, 0)
        XCTAssertTrue(model.rows.isEmpty); model.deactivate()
    }

    @MainActor func testRepeatedBeginWhileBusyOnlyStartsOneNativeAttempt() async throws {
        let service = MusicAccountPageService(), gate = MusicAccountPageGate()
        let model = MusicAccountsViewModel(service: service)
        model.activate(); await model.flush(); await service.hold("begin", gate)
        model.beginLogin(.qq); let pending = model.pendingOperation
        await gate.started(); model.beginLogin(.qq)
        let counts = await service.counts(); XCTAssertEqual(counts.1, 1)
        await gate.release(); await pending?.value
        XCTAssertNotNil(model.challenge); model.deactivate(); XCTAssertNil(model.challenge)
    }

    @MainActor func testUnavailableNativeSnapshotClearsQRWithoutAnAccessStream() async throws {
        let service = MusicAccountPageService()
        let page = MusicAccountsViewModel(service: service)
        page.activate(); await page.flush(); page.beginLogin(.qq); await page.flush()
        XCTAssertNotNil(page.challenge)
        await service.replace([.init(platform: .qq, phase: .unavailable)])
        page.reload(); await page.flush()
        XCTAssertNil(page.challenge); XCTAssertEqual(page.rows.first?.nickname, "")
        XCTAssertEqual(page.rows.first?.canLogin, false)
        page.deactivate()
    }
}
#endif
