import XCTest
import MeowRuntime
@testable import MeowFilmFeature

@MainActor
final class PanAccountsModelTests: XCTestCase {
    func testActualProviderProjectionAndThreeStatesValidateConfiguredRowsOnly() async throws {
        let runtime = PanTestRuntime()
        await runtime.setAccount(.baidu, status: .signedIn)
        await runtime.setAccount(.quark, status: .expired)
        await runtime.setRefresh(.quark, error: .remote("AUTH_FAILED"))
        let model = PanAccountsModel(service: runtime.service)
        await model.start()?.value
        defer { model.close() }
        XCTAssertEqual(model.rows.map(\.id), PanProviderID.allCases)
        XCTAssertEqual(model.rows.map(\.account?.status), [.signedIn, .expired, .signedOut, .signedOut, .signedOut, .signedOut, .signedOut])
        XCTAssertEqual(model.rows[0].actions, [.refresh, .logout])
        XCTAssertEqual(model.rows[1].actions, [.login, .logout])
        XCTAssertEqual(model.rows[2].actions, [.login])
        XCTAssertTrue(model.rows[1].account?.configured == true)
        XCTAssertEqual(model.rows[1].account?.fields, ["cookie"])
        let refreshed = await runtime.calls.filter { $0.operation == "refresh" }.map { $0.parameters.panField("provider")?.panString }
        XCTAssertEqual(Set(refreshed.compactMap { $0 }), ["baidu", "quark"])
        XCTAssertNotNil(model.notice)
    }

    func testNetworkFailurePreservesSignedInAndExpiredSnapshotsAndNotifies() async {
        for status in [PanAccountStatus.signedIn, .expired] {
            let runtime = PanTestRuntime()
            await runtime.setAccount(.baidu, status: status, revision: 7)
            await runtime.setRefresh(.baidu, error: .remote("NETWORK_ERROR"))
            let model = PanAccountsModel(service: runtime.service)
            await model.start()?.value
            XCTAssertEqual(model.rows.first?.account?.status, status)
            XCTAssertEqual(model.rows.first?.account?.revision, 7)
            XCTAssertEqual(model.rows.first?.account?.fields, ["cookie"])
            XCTAssertEqual(model.notice?.message, "网盘网络请求失败")
            model.close()
        }
    }

    func testConfiguredValidationUsesRefreshForAllSevenProviders() async {
        let runtime = PanTestRuntime()
        for provider in PanProviderID.allCases { await runtime.setAccount(provider, status: .signedIn) }
        let model = PanAccountsModel(service: runtime.service)
        await model.start()?.value
        XCTAssertEqual(model.rows.count, 7)
        XCTAssertTrue(model.rows.allSatisfy { $0.account?.status == .signedIn && !$0.busy })
        let calls = await runtime.calls.filter { $0.operation == "refresh" }
        XCTAssertEqual(Set(calls.compactMap { $0.parameters.panField("provider")?.panString }),
                       Set(PanProviderID.allCases.map(\.rawValue)))
        model.close()
    }

    func testLateRefreshCannotOverwriteExplicitLogoutAndOperationsAreSingleFlight() async {
        let runtime = PanTestRuntime()
        let model = PanAccountsModel(service: runtime.service)
        await model.start()?.value
        await runtime.setAccount(.baidu, status: .signedIn)
        await model.start()?.value
        let entered = expectation(description: "refresh entered")
        let gate = PanTestGate { entered.fulfill() }
        await runtime.gate("refresh.baidu", gate)
        let old = model.refresh(.baidu)
        await fulfillment(of: [entered], timeout: 2)
        XCTAssertNil(model.refresh(.baidu))
        await model.logout(.baidu)?.value
        XCTAssertEqual(model.rows[0].account?.status, .signedOut)
        let revision = model.rows[0].account?.revision
        await gate.release(); await old?.value
        XCTAssertEqual(model.rows[0].account?.status, .signedOut)
        XCTAssertEqual(model.rows[0].account?.revision, revision)
        model.close()
    }

    func testLateListReadCannotReplaceACompletedValidationAtTheSameRevision() async {
        let runtime = PanTestRuntime()
        await runtime.setAccount(.baidu, status: .expired)
        await runtime.setRefresh(.baidu, error: .remote("AUTH_FAILED"))
        let model = PanAccountsModel(service: runtime.service)
        await model.start()?.value
        let entered = expectation(description: "list snapshot captured")
        let gate = PanTestGate { entered.fulfill() }
        await runtime.gate("settings.get", gate)
        let reading = model.start()
        await fulfillment(of: [entered], timeout: 2)
        await runtime.setRefresh(.baidu, value: panAccountValue(.baidu, .signedIn, revision: 1))
        await model.refresh(.baidu)?.value
        XCTAssertEqual(model.rows[0].account?.status, .signedIn)
        await gate.release(); await reading?.value
        XCTAssertEqual(model.rows[0].account?.status, .signedIn)
        XCTAssertEqual(model.rows[0].account?.revision, 1)
        model.close()
    }

    func testRefreshAndLoginAreMutuallyExclusiveAndCloseDropsLateReceipts() async throws {
        let runtime = PanTestRuntime()
        let owner = PanAccountsModel(service: runtime.service)
        await owner.start()?.value
        let login = try XCTUnwrap(owner.openLogin(.baidu))
        XCTAssertNil(owner.openLogin(.quark))
        XCTAssertNil(owner.refresh(.baidu))
        login.setVisible(true)
        await panEventually(self) { await runtime.count("login.poll") > 0 }
        owner.close()
        await panEventually(self) { await runtime.count("login.cancel") == 1 }
        XCTAssertFalse(login.confirmed)
        XCTAssertFalse(owner.rows.contains { $0.busy })
    }

    func testStaleManualReceiptCannotOverwriteLogoutOrNewLogin() async throws {
        let runtime = PanTestRuntime()
        let model = PanAccountsModel(service: runtime.service)
        await model.start()?.value
        let login = try XCTUnwrap(model.openLogin(.tianyi))
        login.setVisible(true); login.draft = ["cookie": "synthetic-new-cookie"]
        let entered = expectation(description: "manual request before runtime write")
        let gate = PanTestGate { entered.fulfill() }
        await runtime.gate("settings.set", gate)
        let saving = login.save()
        await fulfillment(of: [entered], timeout: 2)
        let clearing = model.logout(.tianyi)
        XCTAssertNotNil(login.closing)
        XCTAssertNil(model.openLogin(.tianyi))
        let prematureClears = await runtime.count("settings.clear")
        XCTAssertEqual(prematureClears, 0)
        await gate.release(); await saving?.value; await clearing?.value
        let revision = model.rows[3].account?.revision
        let next = try XCTUnwrap(model.openLogin(.tianyi))
        XCTAssertEqual(model.rows[3].account?.status, .signedOut)
        XCTAssertEqual(model.rows[3].account?.revision, revision)
        XCTAssertEqual(revision, 2)
        XCTAssertEqual(model.rows[3].account?.configured, false)
        XCTAssertEqual(model.rows[3].account?.fields, [])
        let stored = await runtime.credentials(.tianyi), state = await runtime.account(.tianyi)
        let effects = await runtime.effects, refreshes = await runtime.count("refresh")
        XCTAssertNil(stored)
        XCTAssertEqual(state, panAccountValue(.tianyi, revision: 2))
        XCTAssertEqual(effects, ["set:tianyi", "clear:tianyi"])
        XCTAssertEqual(refreshes, 0, "A retired save cannot refresh or publish")
        XCTAssertFalse(login.confirmed)
        XCTAssertFalse(next.confirmed)
        model.close()
    }

    func testClosedManualFormCanDeallocateWhileNextSaveWaitsForItsRealWrite() async throws {
        let runtime = PanTestRuntime()
        let owner = PanAccountsModel(service: runtime.service)
        await owner.start()?.value
        var old: PanLoginModel? = try XCTUnwrap(owner.openLogin(.tianyi))
        weak var retired = old
        old?.setVisible(true); old?.draft = ["cookie": "synthetic-old-cookie"]
        let entered = expectation(description: "old write held before side effect")
        let gate = PanTestGate { entered.fulfill() }
        await runtime.gate("settings.set", gate)
        let saving = old?.save()
        await fulfillment(of: [entered], timeout: 2)
        owner.closeLogin(); old = nil
        XCTAssertNil(retired)
        XCTAssertNotNil(owner.closing)
        let next = try XCTUnwrap(owner.openLogin(.tianyi))
        next.setVisible(true); next.draft = ["cookie": "synthetic-next-cookie"]
        let replacement = next.save()
        let submitted = await runtime.count("settings.set"), storedBeforeRelease = await runtime.credentials(.tianyi)
        XCTAssertEqual(submitted, 1); XCTAssertNil(storedBeforeRelease)
        await gate.release(); await saving?.value; await replacement?.value
        let stored = await runtime.credentials(.tianyi), effects = await runtime.effects
        XCTAssertEqual(stored, .object(["cookie": .string("synthetic-next-cookie")]))
        XCTAssertEqual(effects, ["set:tianyi", "set:tianyi"])
        XCTAssertTrue(next.confirmed)
        XCTAssertEqual(owner.rows[3].account?.revision, 2)
        XCTAssertEqual(owner.rows[3].account?.status, .signedIn)
        let refreshes = await runtime.count("refresh")
        XCTAssertEqual(refreshes, 1)
        owner.close()
    }

    func testPageCloseAndReentryKeepWriteAndClearDrainBeforeReading() async throws {
        let runtime = PanTestRuntime()
        let owner = PanAccountsModel(service: runtime.service)
        await owner.start()?.value
        let form = try XCTUnwrap(owner.openLogin(.tianyi))
        form.setVisible(true); form.draft = ["cookie": "synthetic-cookie"]
        let setEntered = expectation(description: "set before storage")
        let setGate = PanTestGate { setEntered.fulfill() }
        let clearEntered = expectation(description: "clear before storage")
        let clearGate = PanTestGate { clearEntered.fulfill() }
        await runtime.gate("settings.set", setGate)
        await runtime.gate("settings.clear", clearGate)
        let saving = form.save()
        await fulfillment(of: [setEntered], timeout: 2)
        let clearing = owner.logout(.tianyi)
        owner.close()
        let reading = owner.start()
        let readsBeforeSet = await runtime.count("settings.get")
        XCTAssertEqual(readsBeforeSet, 1)
        await setGate.release()
        await fulfillment(of: [clearEntered], timeout: 2)
        let readsBeforeClear = await runtime.count("settings.get")
        XCTAssertEqual(readsBeforeClear, 1)
        await clearGate.release()
        await saving?.value; await clearing?.value; await reading?.value
        let stored = await runtime.credentials(.tianyi), state = await runtime.account(.tianyi)
        XCTAssertNil(stored); XCTAssertEqual(state, panAccountValue(.tianyi, revision: 2))
        XCTAssertEqual(owner.rows[3].account?.status, .signedOut)
        XCTAssertEqual(owner.rows[3].account?.configured, false)
        XCTAssertFalse(form.confirmed); XCTAssertTrue(form.draft.isEmpty)
        owner.close()
    }

    func testRefreshAndNewQRStartWaitForRetiredManualWrite() async throws {
        let runtime = PanTestRuntime()
        await runtime.setAccount(.baidu, status: .signedIn)
        let owner = PanAccountsModel(service: runtime.service)
        await owner.start()?.value
        let form = try XCTUnwrap(owner.openLogin(.baidu))
        form.selectMethod(.manual); form.setVisible(true); form.draft = ["cookie": "synthetic-cookie"]
        let entered = expectation(description: "manual write before storage")
        let gate = PanTestGate { entered.fulfill() }
        await runtime.gate("settings.set", gate)
        let saving = form.save()
        await fulfillment(of: [entered], timeout: 2)
        owner.closeLogin()
        let refreshing = owner.refresh(.baidu)
        let refreshesBeforeRelease = await runtime.count("refresh")
        XCTAssertEqual(refreshesBeforeRelease, 1)
        await gate.release(); await saving?.value; await refreshing?.value
        XCTAssertFalse(form.confirmed)
        XCTAssertEqual(owner.rows[0].account?.status, .signedIn)

        let second = try XCTUnwrap(owner.openLogin(.baidu))
        second.selectMethod(.manual); second.setVisible(true); second.draft = ["cookie": "synthetic-next-cookie"]
        let secondEntered = expectation(description: "next write before storage")
        let secondGate = PanTestGate { secondEntered.fulfill() }
        await runtime.gate("settings.set", secondGate)
        let replacement = second.save()
        await fulfillment(of: [secondEntered], timeout: 2)
        owner.closeLogin()
        let qr = try XCTUnwrap(owner.openLogin(.baidu))
        qr.setVisible(true)
        let starting = qr.start()
        let startsBeforeRelease = await runtime.count("login.start")
        XCTAssertEqual(startsBeforeRelease, 0)
        await secondGate.release(); await replacement?.value; await starting?.value
        XCTAssertNotNil(qr.challenge); XCTAssertFalse(second.confirmed)
        let effects = await runtime.effects
        XCTAssertEqual(effects, ["set:baidu", "set:baidu", "start:synthetic-session"])
        owner.close(); await owner.closing?.value
    }
}
