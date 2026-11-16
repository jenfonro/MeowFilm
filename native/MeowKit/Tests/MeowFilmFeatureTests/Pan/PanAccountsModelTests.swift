import XCTest
import MeowRuntime
@testable import MeowFilmFeature

@MainActor
final class PanAccountsModelTests: XCTestCase {
    func testEntryReadsAllProvidersLocallyWithoutLoadingOrAutomaticValidation() async throws {
        let runtime = try PanTestRuntime()
        try await runtime.setAccount(.baidu, status: .signedIn)
        try await runtime.setAccount(.quark, status: .expired)
        let model = PanAccountsModel(service: runtime.service)
        model.start()
        defer { model.close() }
        XCTAssertEqual(model.rows.map(\.id), PanProviderID.allCases)
        XCTAssertEqual(model.rows.map(\.account?.status), [.unverified, .unverified, .signedOut, .signedOut, .signedOut, .signedOut, .signedOut])
        XCTAssertEqual(model.rows[0].actions, [.login, .refresh, .logout])
        XCTAssertEqual(model.rows[2].actions, [.login])
        XCTAssertTrue(model.rows[1].account?.configured == true)
        XCTAssertEqual(model.rows[1].account?.fields, ["cookie"])
        XCTAssertFalse(model.rows.contains(where: \.busy)); XCTAssertNil(model.notice)
        model.close(); model.start()
        await Task.yield()
        let calls = await runtime.calls
        XCTAssertTrue(calls.isEmpty)
    }

    func testOnlyExplicitValidationPublishesOnlineSignedInAndExpiredStates() async throws {
        let runtime = try PanTestRuntime()
        try await runtime.setAccount(.baidu, status: .signedIn)
        try await runtime.setAccount(.quark, status: .expired)
        await runtime.setRefresh(.quark, error: .remote("AUTH_FAILED"))
        let model = PanAccountsModel(service: runtime.service)
        model.start()
        await model.refresh(.baidu)?.value
        await model.refresh(.quark)?.value
        XCTAssertEqual(model.rows[0].account?.status, .signedIn)
        XCTAssertEqual(model.rows[1].account?.status, .expired)
        XCTAssertEqual(model.rows[0].actions, [.refresh, .logout])
        XCTAssertEqual(model.rows[1].actions, [.login, .logout])
        XCTAssertTrue(model.rows[1].account?.configured == true)
        XCTAssertEqual(model.notice?.message, "网盘凭据已失效")
        let refreshed = await runtime.calls.filter { $0.operation == "refresh" }.map { $0.parameters.panField("provider")?.panString }
        XCTAssertEqual(refreshed, ["baidu", "quark"])
        model.close()
    }

    func testNetworkFailurePreservesVerifiedAndExpiredSnapshotsAndNotifies() async throws {
        for status in [PanAccountStatus.signedIn, .expired] {
            let runtime = try PanTestRuntime()
            try await runtime.setAccount(.baidu, status: .signedIn, revision: 7)
            let model = PanAccountsModel(service: runtime.service)
            model.start()
            if status == .expired { await runtime.setRefresh(.baidu, error: .remote("AUTH_FAILED")) }
            await model.refresh(.baidu)?.value
            await runtime.setRefresh(.baidu, error: .remote("NETWORK_ERROR"))
            await model.refresh(.baidu)?.value
            XCTAssertEqual(model.rows.first?.account?.status, status)
            XCTAssertEqual(model.rows.first?.account?.revision, 7)
            XCTAssertEqual(model.rows.first?.account?.fields, ["cookie"])
            XCTAssertEqual(model.notice?.message, "网盘网络请求失败")
            model.start()
            XCTAssertEqual(model.rows.first?.account?.status, status)
            model.close()
        }
    }

    func testExplicitRefreshRetainsAllSevenProviders() async throws {
        let runtime = try PanTestRuntime()
        for provider in PanProviderID.allCases { try await runtime.setAccount(provider, status: .signedIn) }
        let model = PanAccountsModel(service: runtime.service)
        model.start()
        for provider in PanProviderID.allCases { await model.refresh(provider)?.value }
        XCTAssertEqual(model.rows.count, 7)
        XCTAssertTrue(model.rows.allSatisfy { $0.account?.status == .signedIn && !$0.busy })
        let calls = await runtime.calls.filter { $0.operation == "refresh" }
        XCTAssertEqual(Set(calls.compactMap { $0.parameters.panField("provider")?.panString }),
                       Set(PanProviderID.allCases.map(\.rawValue)))
        model.close()
    }

    func testLateRefreshCannotOverwriteExplicitLogoutAndOperationsAreSingleFlight() async throws {
        let runtime = try PanTestRuntime()
        try await runtime.setAccount(.baidu, status: .signedIn)
        let model = PanAccountsModel(service: runtime.service)
        model.start()
        let entered = expectation(description: "refresh entered")
        let gate = PanTestGate { entered.fulfill() }
        await runtime.gate("refresh.baidu", gate)
        let old = model.refresh(.baidu)
        await fulfillment(of: [entered], timeout: 2)
        XCTAssertNil(model.refresh(.baidu))
        model.logout(.baidu)
        XCTAssertEqual(model.rows[0].account?.status, .signedOut)
        let revision = model.rows[0].account?.revision
        XCTAssertEqual(revision, 2)
        await gate.release(); await old?.value
        XCTAssertEqual(model.rows[0].account?.status, .signedOut)
        XCTAssertEqual(model.rows[0].account?.revision, revision)
        XCTAssertEqual(try runtime.storage.credentials(.baidu), .object([:]))
        model.close()
    }

    func testEntryNeverWaitsForRetiredOnlineDrainAndObservesRuntimeCredentialWriteback() async throws {
        let runtime = try PanTestRuntime()
        try await runtime.setAccount(.baidu, status: .signedIn)
        let model = PanAccountsModel(service: runtime.service)
        model.start()
        let entered = expectation(description: "refresh held")
        let gate = PanTestGate { entered.fulfill() }
        await runtime.gate("refresh.baidu", gate)
        let old = model.refresh(.baidu)
        await fulfillment(of: [entered], timeout: 2)
        model.close()
        try runtime.storage.seed(.baidu, credentials: .object(["cookie": .string("synthetic-runtime-rotation")]), revision: 2)
        model.start()
        XCTAssertEqual(model.rows[0].account?.revision, 2)
        XCTAssertEqual(model.rows[0].account?.status, .unverified)
        XCTAssertFalse(model.rows[0].busy)
        await gate.release(); await old?.value
        XCTAssertEqual(model.rows[0].account?.revision, 2)
        XCTAssertEqual(model.rows[0].account?.status, .unverified)
        model.close()
    }

    func testRefreshAndLoginAreMutuallyExclusiveAndCloseDropsLateReceipts() async throws {
        let runtime = try PanTestRuntime()
        let owner = PanAccountsModel(service: runtime.service)
        owner.start()
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

    func testManualSaveAndLogoutAreCommittedLocallyAndNeverSubmitRuntimeWork() async throws {
        let runtime = try PanTestRuntime()
        let owner = PanAccountsModel(service: runtime.service)
        owner.start()
        let login = try XCTUnwrap(owner.openLogin(.tianyi))
        login.setVisible(true); login.draft = ["cookie": "synthetic-cookie"]
        login.save()
        XCTAssertTrue(login.saved); XCTAssertFalse(login.confirmed)
        XCTAssertEqual(owner.rows[3].account?.revision, 1)
        XCTAssertEqual(owner.rows[3].account?.status, .unverified)
        owner.logout(.tianyi)
        XCTAssertEqual(owner.rows[3].account?.status, .signedOut)
        XCTAssertEqual(owner.rows[3].account?.revision, 2)
        XCTAssertFalse(owner.rows[3].account!.configured)
        XCTAssertEqual(owner.rows[3].account?.fields, [])
        XCTAssertEqual(try runtime.storage.credentials(.tianyi), .object([:]))
        let next = try XCTUnwrap(owner.openLogin(.tianyi))
        XCTAssertFalse(next.completed)
        XCTAssertTrue(next.draft.isEmpty)
        let calls = await runtime.calls
        XCTAssertTrue(calls.isEmpty)
        owner.close()
    }

    func testClosedManualFormDeallocatesAndNextSaveReplacesCredentialsWithoutValidation() async throws {
        let runtime = try PanTestRuntime()
        let owner = PanAccountsModel(service: runtime.service)
        owner.start()
        var old: PanLoginModel? = try XCTUnwrap(owner.openLogin(.tianyi))
        weak var retired = old
        old?.setVisible(true); old?.draft = ["cookie": "synthetic-old-cookie"]
        old?.save()
        owner.closeLogin(); old = nil
        XCTAssertNil(retired)
        let next = try XCTUnwrap(owner.openLogin(.tianyi))
        next.setVisible(true); next.draft = ["username": "synthetic-user", "password": " synthetic-next-password "]
        next.save()
        XCTAssertEqual(try runtime.storage.credentials(.tianyi), .object([
            "username": .string("synthetic-user"), "password": .string(" synthetic-next-password ")]))
        XCTAssertTrue(next.saved); XCTAssertFalse(next.confirmed)
        XCTAssertEqual(owner.rows[3].account?.revision, 2)
        XCTAssertEqual(owner.rows[3].account?.status, .unverified)
        let calls = await runtime.calls
        XCTAssertTrue(calls.isEmpty)
        owner.close()
    }

    func testNativeClearFailurePreservesSnapshotAndPageReentryHasNoRPC() throws {
        let runtime = try PanTestRuntime()
        try runtime.storage.seed(.tianyi, credentials: panSyntheticCredentials(.tianyi), revision: 5)
        let owner = PanAccountsModel(service: runtime.service)
        owner.start()
        runtime.storage.committer.fail(true)
        owner.logout(.tianyi)
        XCTAssertEqual(owner.rows[3].account?.revision, 5)
        XCTAssertEqual(owner.rows[3].account?.status, .unverified)
        XCTAssertTrue(owner.rows[3].account!.configured)
        XCTAssertEqual(owner.notice?.message, "网盘账号保存失败")
        XCTAssertFalse(owner.rows[3].busy)
        runtime.storage.committer.fail(false)
        owner.logout(.tianyi)
        owner.close(); owner.start()
        XCTAssertEqual(owner.rows[3].account?.status, .signedOut)
        XCTAssertEqual(owner.rows[3].account?.revision, 6)
        owner.close()
    }

    func testLogoutAndNewManualSaveOvertakeRetiredQRStartAndCancel() async throws {
        let runtime = try PanTestRuntime()
        let owner = PanAccountsModel(service: runtime.service)
        owner.start()
        let login = try XCTUnwrap(owner.openLogin(.baidu))
        let entered = expectation(description: "QR start held before receipt")
        let gate = PanTestGate { entered.fulfill() }
        await runtime.gate("login.start", gate)
        login.setVisible(true)
        await fulfillment(of: [entered], timeout: 2)
        let starting = login.start()
        owner.logout(.baidu)
        XCTAssertEqual(owner.rows[0].account?.status, .signedOut)
        XCTAssertEqual(owner.rows[0].account?.revision, 1)
        let next = try XCTUnwrap(owner.openLogin(.baidu))
        next.selectMethod(.manual); next.setVisible(true); next.draft = ["cookie": "synthetic-latest"]
        next.save()
        XCTAssertTrue(next.saved)
        XCTAssertEqual(owner.rows[0].account?.revision, 2)
        await gate.release(); await starting?.value; await owner.closing?.value
        XCTAssertFalse(login.confirmed)
        XCTAssertEqual(try runtime.storage.credentials(.baidu), .object(["cookie": .string("synthetic-latest")]))
        XCTAssertEqual(owner.rows[0].account?.status, .unverified)
        let effects = await runtime.effects
        XCTAssertEqual(effects, ["start:synthetic-session", "cancel:synthetic-session"])
        owner.close(); await owner.closing?.value
    }
}
