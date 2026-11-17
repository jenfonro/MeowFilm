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

    func testManualSaveCommitsWithoutOnlineValidationOrSecretReadback() async throws {
        let runtime = try PanTestRuntime()
        var accounts: [PanAccount] = []
        let model = try login(runtime, provider: .tianyi) { accounts.append($0) }
        XCTAssertEqual(model.method, .manual); XCTAssertTrue(model.draft.isEmpty)
        model.setVisible(true)
        model.draft = ["username": "synthetic-user", "password": "synthetic-password"]
        model.save()
        XCTAssertTrue(model.saved); XCTAssertTrue(model.completed); XCTAssertFalse(model.confirmed)
        XCTAssertTrue(model.draft.isEmpty); XCTAssertNil(model.progress)
        XCTAssertEqual(accounts.count, 1)
        XCTAssertEqual(accounts.last?.status, .unverified)
        XCTAssertEqual(try runtime.storage.credentials(.tianyi), .object([
            "username": .string("synthetic-user"), "password": .string("synthetic-password")]))
        model.save()
        XCTAssertEqual(try runtime.storage.read().panField("accounts")?.panField("tianyi")?.panField("revision"), .number(1))
        let calls = await runtime.calls
        XCTAssertTrue(calls.isEmpty)
        model.close()
        let cancels = await runtime.count("login.cancel")
        XCTAssertEqual(cancels, 0)
    }

    func testFailedNativeWriteKeepsDraftAndFormOpenThenCanSave() async throws {
        let runtime = try PanTestRuntime()
        runtime.storage.committer.fail(true)
        let model = try login(runtime, provider: .tianyi)
        model.setVisible(true); model.draft = ["cookie": "synthetic-cookie"]
        model.save()
        XCTAssertFalse(model.completed); XCTAssertFalse(model.confirmed); XCTAssertFalse(model.busy)
        XCTAssertEqual(model.draft["cookie"], "synthetic-cookie")
        XCTAssertEqual(model.notice?.message, "网盘账号保存失败")
        XCTAssertNil(try runtime.storage.document.read())
        XCTAssertEqual(runtime.storage.changes.values, [])
        runtime.storage.committer.fail(false)
        model.save()
        XCTAssertTrue(model.saved); XCTAssertFalse(model.confirmed)
        model.close()
    }

    func testRuntimeNetworkFailureCannotPreventLocalManualSave() async throws {
        let runtime = try PanTestRuntime()
        await runtime.setRefresh(.tianyi, error: .remote("NETWORK_ERROR"))
        var snapshots: [PanAccount] = []
        let model = try login(runtime, provider: .tianyi) { snapshots.append($0) }
        model.setVisible(true); model.draft = ["username": "synthetic-user", "password": " synthetic-password "]
        model.save()
        XCTAssertTrue(model.saved); XCTAssertFalse(model.confirmed); XCTAssertEqual(snapshots.count, 1)
        XCTAssertEqual(snapshots[0].configured, true); XCTAssertEqual(snapshots[0].status, .unverified)
        XCTAssertEqual(try runtime.storage.credentials(.tianyi)?.panField("password"), .string(" synthetic-password "))
        XCTAssertNil(model.notice)
        let count = await runtime.count("refresh")
        XCTAssertEqual(count, 0)
        model.close(); XCTAssertTrue(model.draft.isEmpty)
    }

    func testInvalidNumberIsRejectedBeforeWritingAndTimestampIsOptional() async throws {
        let runtime = try PanTestRuntime()
        let form = try login(runtime, provider: .yidong)
        form.setVisible(true); form.draft = ["authorization": "synthetic-authorization", "refreshedAt": "-1"]
        form.save(); XCTAssertNotNil(form.notice); XCTAssertFalse(form.completed)
        XCTAssertNil(try runtime.storage.document.read())
        form.draft["refreshedAt"] = ""
        form.save()
        XCTAssertTrue(form.saved); XCTAssertFalse(form.confirmed)
        form.close()
    }

    func testInvalidHeaderNeverCommitsOrFinishesAndPreservesDraft() async throws {
        let runtime = try PanTestRuntime()
        let model = try login(runtime, provider: .uc)
        model.selectMethod(.manual)
        model.setVisible(true); model.draft = ["cookie": "synthetic\r\ninvalid"]
        model.save()
        XCTAssertFalse(model.completed); XCTAssertFalse(model.busy)
        XCTAssertNil(try runtime.storage.document.read())
        XCTAssertEqual(model.draft["cookie"], "synthetic\r\ninvalid")
        XCTAssertEqual(model.notice?.message, "网盘凭据无效")
        model.close()
    }

    func testQRProgressWaitsForConfirmedPersistenceReceiptAndCloseNeverClearsAccount() async throws {
        let runtime = try PanTestRuntime()
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
        let runtime = try PanTestRuntime()
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
        let cancelled = try PanTestRuntime()
        await cancelled.polls([panReceipt(.cancelled)])
        let cancelledForm = try login(cancelled)
        cancelledForm.setVisible(true)
        await panEventually(self) { cancelledForm.progress == .cancelled }
        XCTAssertTrue(cancelledForm.canRetry); XCTAssertFalse(cancelledForm.confirmed)
        cancelledForm.close()

        let invalidImage = try PanTestRuntime()
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
            let runtime = try PanTestRuntime()
            let owner = PanAccountsModel(service: runtime.service)
            owner.start()
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
        let runtime = try PanTestRuntime()
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

    func testLateConfirmedPollCannotReplaceAYoungerManualAccount() async throws {
        let runtime = try PanTestRuntime()
        await runtime.polls([panReceipt(.confirmed, account: panAccountValue(.baidu, .signedIn, revision: 1))])
        let entered = expectation(description: "poll before native credential CAS")
        let gate = PanTestGate { entered.fulfill() }
        await runtime.gate("login.poll", gate)
        let owner = PanAccountsModel(service: runtime.service)
        owner.start()
        let old = try XCTUnwrap(owner.openLogin(.baidu))
        old.setVisible(true)
        await fulfillment(of: [entered], timeout: 2)
        let polling = old.polling
        owner.closeLogin()
        let next = try XCTUnwrap(owner.openLogin(.baidu))
        next.selectMethod(.manual); next.setVisible(true)
        next.draft = ["cookie": "synthetic-newer-account"]; next.save()
        XCTAssertTrue(next.saved)
        let revision = owner.rows[0].account?.revision
        await gate.release(); await polling?.value
        XCTAssertFalse(old.confirmed)
        XCTAssertEqual(owner.rows[0].account?.revision, revision)
        XCTAssertEqual(owner.rows[0].account?.status, .unverified)
        XCTAssertEqual(try runtime.storage.credentials(.baidu), .object(["cookie": .string("synthetic-newer-account")]))
        owner.close(); await owner.closing?.value
    }

    func testQRStorageFailureNeverPublishesConfirmedAndCanRetry() async throws {
        let runtime = try PanTestRuntime()
        await runtime.polls([panReceipt(.confirmed, account: panAccountValue(.baidu, .signedIn, revision: 1))])
        runtime.storage.committer.fail(true)
        var snapshots: [PanAccount] = []
        let model = try login(runtime) { snapshots.append($0) }
        model.setVisible(true)
        await panEventually(self) { model.notice != nil }
        XCTAssertFalse(model.confirmed); XCTAssertFalse(model.completed)
        XCTAssertNotEqual(model.progress, .confirmed)
        XCTAssertEqual(model.notice?.message, "网盘账号保存失败")
        XCTAssertTrue(model.canRetry); XCTAssertTrue(snapshots.isEmpty)
        XCTAssertNil(try runtime.storage.document.read())
        runtime.storage.committer.fail(false)
        model.retry()
        await panEventually(self) { model.confirmed }
        XCTAssertEqual(snapshots.count, 1)
        XCTAssertEqual(snapshots.first?.status, .signedIn)
        XCTAssertEqual(try runtime.storage.credentials(.baidu), panSyntheticCredentials(.baidu))
        model.close(); await model.closing?.value
    }

    func testBackgroundCancelsCurrentQRAndForegroundStartsFreshChallenge() async throws {
        let runtime = try PanTestRuntime()
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
