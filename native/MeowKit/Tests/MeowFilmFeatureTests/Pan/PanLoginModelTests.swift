import XCTest
import Combine
import MeowRuntime
@testable import MeowFilmFeature

@MainActor
final class PanLoginModelTests: XCTestCase {
    private func login(_ runtime: PanTestRuntime, provider: PanProviderID = .baidu,
                       pause: @escaping @Sendable () async throws -> Void = { await Task.yield() },
                       update: @escaping (PanAccount) -> Void = { _ in }) throws -> PanLoginModel {
        PanLoginModel(provider: try PanProvider(panProviderValue(provider)), service: runtime.service,
                      pause: pause, update: update)
    }

    func testManualSaveReplacesWholeDraftThenRefreshesBeforeFinishingWithoutSecretReadback() async throws {
        let runtime = PanTestRuntime()
        var accounts: [PanAccount] = []
        let model = try login(runtime, provider: .quarkTV) { accounts.append($0) }
        XCTAssertEqual(model.method, .manual); XCTAssertTrue(model.draft.isEmpty)
        model.setVisible(true)
        model.draft = ["refreshToken": "synthetic-refresh", "deviceId": "synthetic-device"]
        await model.save()?.value
        XCTAssertTrue(model.confirmed); XCTAssertTrue(model.draft.isEmpty)
        XCTAssertEqual(accounts.count, 2)
        XCTAssertEqual(accounts.last?.status, .signedIn)
        let calls = await runtime.calls
        XCTAssertEqual(calls.map(\.operation), ["settings.set", "refresh"])
        XCTAssertEqual(calls[0].parameters.panField("credentials"), .object([
            "refreshToken": .string("synthetic-refresh"), "deviceId": .string("synthetic-device")]))
        XCTAssertFalse(calls.contains { $0.operation == "settings.get" })
        model.close()
        let cancels = await runtime.count("login.cancel")
        XCTAssertEqual(cancels, 0)
    }

    func testSaveIsSingleFlightAndFailedWriteKeepsDraftAndFormOpen() async throws {
        let runtime = PanTestRuntime()
        await runtime.failSet(.remote("STORAGE_ERROR"))
        let model = try login(runtime, provider: .tianyi)
        model.setVisible(true); model.draft = ["cookie": "synthetic-cookie"]
        let operation = model.save()
        XCTAssertNil(model.save())
        await operation?.value
        XCTAssertFalse(model.confirmed); XCTAssertFalse(model.busy)
        XCTAssertEqual(model.draft["cookie"], "synthetic-cookie")
        XCTAssertEqual(model.notice?.message, "网盘账号保存失败")
        await runtime.failSet(nil)
        await model.save()?.value
        XCTAssertTrue(model.confirmed)
        model.close()
    }

    func testRefreshNetworkFailureDoesNotMakeSavedAccountExpiredOrFinishLogin() async throws {
        let runtime = PanTestRuntime()
        await runtime.setRefresh(.tianyi, error: .remote("NETWORK_ERROR"))
        var snapshots: [PanAccount] = []
        let model = try login(runtime, provider: .tianyi) { snapshots.append($0) }
        model.setVisible(true); model.draft = ["username": "synthetic-user", "password": " synthetic-password "]
        await model.save()?.value
        XCTAssertFalse(model.confirmed); XCTAssertEqual(snapshots.count, 1)
        XCTAssertTrue(snapshots[0].configured); XCTAssertNotEqual(snapshots[0].status, .expired)
        XCTAssertEqual(model.draft["password"], " synthetic-password ")
        XCTAssertEqual(model.notice?.message, "网盘网络请求失败")
        model.close(); XCTAssertTrue(model.draft.isEmpty)
    }

    func testInvalidNumberIsRejectedBeforeWritingAndTimestampIsOptional() async throws {
        let runtime = PanTestRuntime()
        let form = try login(runtime, provider: .yidong)
        form.setVisible(true); form.draft = ["authorization": "synthetic-authorization", "refreshedAt": "-1"]
        XCTAssertNil(form.save()); XCTAssertNotNil(form.notice)
        let count = await runtime.count("settings.set")
        XCTAssertEqual(count, 0)
        form.draft["refreshedAt"] = ""
        await form.save()?.value
        XCTAssertTrue(form.confirmed)
        form.close()
    }

    func testManualAuthenticationFailureKeepsCredentialsAndPublishesRuntimeExpiredSnapshot() async throws {
        let runtime = PanTestRuntime()
        await runtime.setRefresh(.ucTV, error: .remote("AUTH_FAILED"))
        var snapshots: [PanAccount] = []
        let model = try login(runtime, provider: .ucTV) { snapshots.append($0) }
        model.setVisible(true); model.draft = ["refreshToken": "synthetic-refresh", "deviceId": "synthetic-device"]
        await model.save()?.value
        XCTAssertFalse(model.confirmed); XCTAssertFalse(model.busy)
        XCTAssertEqual(snapshots.last?.status, .expired)
        XCTAssertEqual(snapshots.last?.fields, ["deviceId", "refreshToken"])
        XCTAssertTrue(snapshots.last?.configured == true)
        XCTAssertEqual(model.draft["refreshToken"], "synthetic-refresh")
        model.close()
    }

    func testQRProgressWaitsForConfirmedPersistenceReceiptAndCloseNeverClearsAccount() async throws {
        let runtime = PanTestRuntime()
        await runtime.polls([panReceipt(.pending), panReceipt(.scanned),
            panReceipt(.confirmed, account: panAccountValue(.baidu, .signedIn, revision: 4))])
        var snapshots: [PanAccount] = []
        let model = try login(runtime) { snapshots.append($0) }
        var progress: [PanLoginProgress] = []
        let subscription = model.$progress.compactMap { $0 }.sink { progress.append($0) }
        defer { subscription.cancel() }
        model.setVisible(true)
        await panEventually(self) { model.confirmed }
        XCTAssertEqual(model.progress, .confirmed)
        XCTAssertTrue(progress.contains(.pending)); XCTAssertTrue(progress.contains(.scanned))
        XCTAssertEqual(snapshots.map(\.revision), [4])
        XCTAssertNil(model.challenge)
        model.close()
        let calls = await runtime.calls.map(\.operation)
        XCTAssertFalse(calls.contains("settings.clear")); XCTAssertFalse(calls.contains("login.cancel"))
    }

    func testExpiredQRCanRetryAndManualSwitchCancelsAndStopsPolling() async throws {
        let runtime = PanTestRuntime()
        await runtime.polls([panReceipt(.expired)])
        let model = try login(runtime)
        model.setVisible(true)
        await panEventually(self) { model.progress == .expired }
        XCTAssertTrue(model.canRetry); XCTAssertFalse(model.confirmed)
        await runtime.polls([panReceipt(.pending)])
        model.retry()
        await panEventually(self) { model.challenge != nil }
        model.selectMethod(.manual)
        await panEventually(self) { await runtime.count("login.cancel") == 2 }
        XCTAssertEqual(model.method, .manual); XCTAssertNil(model.challenge)
        let count = await runtime.count("login.poll")
        await Task.yield()
        let after = await runtime.count("login.poll")
        XCTAssertEqual(after, count)
        model.close()
    }

    func testCancelledReceiptOffersRetryAndInvalidImageCancelsBeforePolling() async throws {
        let cancelled = PanTestRuntime()
        await cancelled.polls([panReceipt(.cancelled)])
        let cancelledForm = try login(cancelled)
        cancelledForm.setVisible(true)
        await panEventually(self) { cancelledForm.progress == .cancelled }
        XCTAssertTrue(cancelledForm.canRetry); XCTAssertFalse(cancelledForm.confirmed)
        cancelledForm.close()

        let invalidImage = PanTestRuntime()
        let form = try self.login(invalidImage)
        form.validateChallenge = { _ in throw PanAccountError.invalidQRCode }
        form.setVisible(true)
        await panEventually(self) { form.notice != nil }
        XCTAssertTrue(form.canRetry); XCTAssertNil(form.challenge)
        let polls = await invalidImage.count("login.poll"), cancels = await invalidImage.count("login.cancel")
        XCTAssertEqual(polls, 0); XCTAssertEqual(cancels, 1)
        form.close()
    }

    func testCloseBackgroundAndMethodSwitchCleanUpLateStart() async throws {
        for boundary in ["close", "background", "manual"] {
            let runtime = PanTestRuntime()
            let owner = PanAccountsModel(service: runtime.service)
            await owner.start()?.value
            await runtime.challenge(panChallengeValue("synthetic-old-session"))
            let entered = expectation(description: "start admitted for \(boundary)")
            let gate = PanTestGate { entered.fulfill() }
            await runtime.gate("login.start", gate)
            let model = try XCTUnwrap(owner.openLogin(.baidu))
            model.setVisible(true)
            await fulfillment(of: [entered], timeout: 2)
            let starting = model.start()
            switch boundary {
            case "close": owner.closeLogin()
            case "background": model.setVisible(false)
            default: model.selectMethod(.manual)
            }
            XCTAssertNil(model.challenge); XCTAssertFalse(model.confirmed)
            XCTAssertNotNil(model.closing)
            let oldPolls = await runtime.count("login.poll")
            XCTAssertEqual(oldPolls, 0)
            let cancelling = expectation(description: "exact old cancel before side effect")
            let cancelGate = PanTestGate { cancelling.fulfill() }
            await runtime.gate("login.cancel", cancelGate)
            await runtime.challenge(panChallengeValue("synthetic-new-session"))
            await runtime.polls([panReceipt(.pending, id: "synthetic-new-session")])
            let next = boundary == "close" ? try XCTUnwrap(owner.openLogin(.baidu)) : model
            if boundary == "manual" { next.selectMethod(.qr) }
            next.setVisible(true)
            let newStarting = next.start()
            let startsBeforeRelease = await runtime.count("login.start")
            XCTAssertEqual(startsBeforeRelease, 1)
            await gate.release()
            await fulfillment(of: [cancelling], timeout: 2)
            let startsBeforeCancel = await runtime.count("login.start")
            let priorEffects = await runtime.effects
            XCTAssertEqual(startsBeforeCancel, 1)
            XCTAssertEqual(priorEffects, ["start:synthetic-old-session"])
            await cancelGate.release()
            await starting?.value; await newStarting?.value
            await panEventually(self) { next.challenge?.sessionID == "synthetic-new-session" }
            let effects = await runtime.effects, activeSession = await runtime.session(.baidu)
            XCTAssertEqual(effects, ["start:synthetic-old-session", "cancel:synthetic-old-session", "start:synthetic-new-session"])
            XCTAssertEqual(activeSession, "synthetic-new-session")
            let polls = await runtime.calls.filter { $0.operation == "login.poll" }
            XCTAssertTrue(polls.allSatisfy { $0.parameters.panField("sessionId") == .string("synthetic-new-session") })
            XCTAssertFalse(next.confirmed)
            owner.close(); await owner.closing?.value
        }
    }

    func testLateConfirmedPollAfterCloseDoesNotPublishOrCallSettingsClear() async throws {
        let runtime = PanTestRuntime()
        await runtime.polls([panReceipt(.confirmed, account: panAccountValue(.baidu, .signedIn, revision: 9))])
        let entered = expectation(description: "poll admitted")
        let gate = PanTestGate { entered.fulfill() }
        await runtime.gate("login.poll", gate)
        var snapshots: [PanAccount] = []
        let model = try login(runtime) { snapshots.append($0) }
        model.setVisible(true)
        await fulfillment(of: [entered], timeout: 2)
        let polling = model.polling
        model.close()
        await gate.release()
        await polling?.value
        await panEventually(self) { await runtime.count("login.cancel") == 1 }
        XCTAssertFalse(model.confirmed); XCTAssertTrue(snapshots.isEmpty)
        let clears = await runtime.count("settings.clear")
        XCTAssertEqual(clears, 0)
    }

    func testBackgroundCancelsCurrentQRAndForegroundStartsFreshChallenge() async throws {
        let runtime = PanTestRuntime()
        await runtime.challenge(panChallengeValue("synthetic-old-session"))
        await runtime.polls([panReceipt(.pending, id: "synthetic-old-session")])
        let form = try login(runtime, pause: { try await Task.sleep(for: .seconds(30)) })
        form.setVisible(true)
        await panEventually(self) { form.challenge != nil }
        let entered = expectation(description: "background cancel held before side effect")
        let gate = PanTestGate { entered.fulfill() }
        await runtime.gate("login.cancel", gate)
        form.setVisible(false)
        XCTAssertNil(form.challenge)
        await runtime.challenge(panChallengeValue("synthetic-new-session"))
        await runtime.polls([panReceipt(.pending, id: "synthetic-new-session")])
        form.setVisible(true)
        let starting = form.start()
        await fulfillment(of: [entered], timeout: 2)
        let startsBeforeCancel = await runtime.count("login.start")
        XCTAssertEqual(startsBeforeCancel, 1)
        await gate.release(); await starting?.value
        await panEventually(self) { await runtime.count("login.start") == 2 }
        let effects = await runtime.effects, activeSession = await runtime.session(.baidu)
        XCTAssertEqual(effects, ["start:synthetic-old-session", "cancel:synthetic-old-session", "start:synthetic-new-session"])
        XCTAssertEqual(activeSession, "synthetic-new-session")
        form.close(); await form.closing?.value
    }
}
