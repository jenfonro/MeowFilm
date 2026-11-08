import Foundation
import Combine
import MeowContracts
import XCTest
@testable import MeowMusicFeature
@testable import MeowMusicNativeService

private actor ManagementReadbackOwner: MusicScriptSourceManaging {
    static func item(_ id: String, name: String? = nil, version: String = "1", enabled: Bool = false,
                     check: MusicScriptSourceCheck? = nil,
                     origin: MusicScriptSourceItem.Origin = .file) -> MusicScriptSourceItem {
        .init(id: id, name: name ?? id, version: version, origin: origin, platforms: [.qq],
              enabled: enabled, state: enabled ? .ready : .disabled, check: check)
    }
    private var items = [ManagementReadbackOwner.item("a"), ManagementReadbackOwner.item("b")]
    private var writeGate: NativeMusicGate?
    private var listGate: NativeMusicGate?
    private var detailGates: [String: NativeMusicGate] = [:]
    private var networkURLs: [String: String] = [:]
    private var uncertain = false
    private var uncertainImport = false
    private var uncertainChange = false
    private(set) var detailCalls: [String: Int] = [:]
    private(set) var enabledRequests: [Bool] = []
    private(set) var listCalls = 0

    func holdWrite(_ gate: NativeMusicGate) { writeGate = gate }
    func holdList(_ gate: NativeMusicGate) { listGate = gate }
    func holdDetail(_ id: String, gate: NativeMusicGate) { detailGates[id] = gate }
    func prepareNetworkSource() {
        networkURLs["a"] = "https://source.invalid/old.js"
        _ = put(.init(id: "a", name: "a", version: "1", origin: .network(host: "source.invalid"),
                      platforms: [.qq], enabled: false, state: .disabled))
    }
    func setUncertain(_ value: Bool) { uncertain = value }
    func failImportAfterCommit() { uncertainImport = true }
    func failChangeAfterCommit() { uncertainChange = true }
    func snapshot() -> [MusicScriptSourceItem] { items }
    func scriptSources() async -> [MusicScriptSourceItem] {
        listCalls += 1
        let snapshot = items
        if let gate = listGate { listGate = nil; await gate.wait() }
        return snapshot
    }
    private func waitForWrite() async {
        if let gate = writeGate { writeGate = nil; await gate.wait() }
    }
    private func put(_ value: MusicScriptSourceItem) -> MusicScriptSourceItem {
        if let index = items.firstIndex(where: { $0.id == value.id }) { items[index] = value }
        else { items.append(value) }
        return value
    }
    func setScriptSourceEnabled(_ id: String, enabled: Bool) async throws -> MusicScriptSourceItem {
        enabledRequests.append(enabled)
        await waitForWrite()
        return put(Self.item(id, enabled: enabled))
    }
    func removeScriptSource(_ id: String) async throws {
        await waitForWrite()
        items.removeAll { $0.id == id }
        if uncertainChange { throw MusicServiceError.commitUnconfirmed }
    }
    func editScriptSource(_ id: String, name: String, networkURL: String?) async throws -> MusicScriptSourceItem {
        if id == "b" {
            if uncertain { _ = put(Self.item(id, name: name)) }
            throw uncertain ? MusicServiceError.commitUnconfirmed : MusicServiceError.scriptFailed
        }
        await waitForWrite()
        if let networkURL { networkURLs[id] = networkURL }
        let value = put(Self.item(id, name: name, origin: networkURLs[id] == nil ? .file : .network(host: "source.invalid")))
        if uncertainChange { throw MusicServiceError.commitUnconfirmed }
        return value
    }
    func updateScriptSource(_ id: String) async throws -> MusicScriptSourceItem {
        await waitForWrite()
        let value = put(Self.item(id, version: "2", origin: networkURLs[id] == nil ? .file : .network(host: "source.invalid")))
        if uncertainChange { throw MusicServiceError.commitUnconfirmed }
        return value
    }
    func updateScriptSource(_ id: String, data: Data, fileName: String) async throws -> MusicScriptSourceItem {
        try await updateScriptSource(id)
    }
    func rollbackScriptSource(_ id: String) async throws -> MusicScriptSourceItem {
        await waitForWrite()
        return put(Self.item(id, version: "previous"))
    }
    func checkScriptSource(_ id: String) async throws -> MusicScriptSourceItem {
        await waitForWrite()
        return put(Self.item(id, check: .init(summary: "normal", checkedAt: Date(timeIntervalSince1970: 100),
                                            platforms: ["tx": "ok"])))
    }
    func importScriptSource(_ data: Data, fileName: String) async throws -> MusicScriptSourceItem {
        await waitForWrite()
        let item = put(Self.item("imported"))
        if uncertainImport { throw MusicServiceError.commitUnconfirmed }
        return item
    }
    func downloadScriptSource(from url: URL) async throws -> MusicScriptSourceItem {
        try await importScriptSource(Data(), fileName: url.lastPathComponent)
    }
    func scriptSourceDetail(_ id: String) async throws -> MusicScriptSourceDetail {
        detailCalls[id, default: 0] += 1
        guard let item = items.first(where: { $0.id == id }) else { throw MusicServiceError.notFound }
        let detail = MusicScriptSourceDetail(item: item, networkURL: networkURLs[id],
                                            rollbackAvailable: item.version != "1")
        if let gate = detailGates.removeValue(forKey: id) { await gate.wait() }
        return detail
    }
}

@MainActor private final class ManagementReadbackPlayer: MusicPlaybackDriving {
    func play(_ resource: PlaybackResource) -> MusicPlayerAttempt {
        XCTFail("Management must not play media"); return .init(id: UUID(), resourceID: resource.id)
    }
    func pause(_ attempt: MusicPlayerAttempt) { XCTFail("Management must not pause media") }
    func resume(_ attempt: MusicPlayerAttempt) { XCTFail("Management must not resume media") }
    func seek(_ position: Double, attempt: MusicPlayerAttempt) -> Bool { XCTFail("Management must not seek"); return false }
    func stop(_ attempt: MusicPlayerAttempt) { XCTFail("Management must not stop media") }
}

@MainActor final class MusicSourceManagementReadbackTests: XCTestCase {
    private enum Action: Equatable {
        case enable, remove, edit, update, rollback, check, download, importFile

        @MainActor func submit(_ state: MusicSourceManagementState, file: URL) {
            switch self {
            case .enable: state.setScript("a", enabled: true)
            case .remove: state.remove("a")
            case .edit: state.edit("a", name: "renamed", url: nil)
            case .update: state.update("a")
            case .rollback: state.rollback("a")
            case .check: state.checkScript("a")
            case .download: state.download("https://source.invalid/new.js")
            case .importFile: state.importFiles([file])
            }
        }
        func accepted(_ items: [MusicScriptSourceItem]) -> Bool {
            let a = items.first { $0.id == "a" }
            switch self {
            case .enable: return a?.enabled == true
            case .remove: return a == nil
            case .edit: return a?.name == "renamed"
            case .update: return a?.version == "2"
            case .rollback: return a?.version == "previous"
            case .check: return a?.check?.summary == "normal"
            case .download, .importFile: return items.contains { $0.id == "imported" }
            }
        }
    }

    func testOldFailureReadbackCannotUndoConfirmedEnableAndNextDisableReachesOwner() async throws {
        try await interleave(.enable, uncertain: false, readFirst: false)
    }
    func testOldFailureReadbackCannotResurrectConfirmedDeletion() async throws {
        try await interleave(.remove, uncertain: false, readFirst: false)
    }
    func testInvalidatedReadbackStillReconcilesUncertainCommit() async throws {
        try await interleave(.enable, uncertain: true, readFirst: false)
    }
    func testReadbackBeforeSuccessfulReceiptKeepsBothOwnerFacts() async throws {
        for action in [Action.enable, .remove] {
            try await interleave(action, uncertain: true, readFirst: true)
        }
    }
    func testOperationStartedDuringReadbackPreservesUncertainReconciliation() async throws {
        try await interleave(.enable, uncertain: true, readFirst: true, readBeforeAction: true)
    }
    func testOldReadbackCannotUndoUpdateEditRollbackImportOrCheck() async throws {
        for action in [Action.edit, .update, .rollback, .check, .download, .importFile] {
            try await interleave(action, uncertain: true, readFirst: false)
        }
    }

    private func interleave(_ action: Action, uncertain: Bool, readFirst: Bool,
                            readBeforeAction: Bool = false) async throws {
        let scripts = ManagementReadbackOwner(), service = try await NativeMusicFixture.service()
        let model = MusicViewModel(service: service, player: ManagementReadbackPlayer(), scriptSources: scripts)
        await model.load()
        let state = MusicSourceManagementState(model: model)
        state.start(); await state.flush()
        let file = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString + ".js")
        try musicScriptFixture.write(to: file)
        defer { try? FileManager.default.removeItem(at: file) }
        let writeGate = NativeMusicGate(), listGate = NativeMusicGate()
        await scripts.holdWrite(writeGate)
        if !readBeforeAction {
            action.submit(state, file: file)
            await writeGate.started()
        }
        await scripts.setUncertain(uncertain)
        await scripts.holdList(listGate)
        state.edit("b", name: "committed-b", url: nil)
        await listGate.started() // Captured A's old value, after B's failed/uncertain receipt.
        if readBeforeAction {
            action.submit(state, file: file)
            await writeGate.started()
        }

        let published = expectation(description: "A successful receipt published")
        let observation = state.$scripts.filter { action.accepted($0) }.prefix(1).sink { _ in published.fulfill() }
        if readFirst {
            let reconciled = expectation(description: "B uncertain commit reconciled before A completes")
            let readObservation = state.$scripts.filter { $0.first { $0.id == "b" }?.name == "committed-b" }
                .prefix(1).sink { _ in reconciled.fulfill() }
            await listGate.release()
            await fulfillment(of: [reconciled], timeout: 3)
            readObservation.cancel()
        }
        await writeGate.release()
        await fulfillment(of: [published], timeout: 3)
        observation.cancel()
        let retained = state.$scripts.sink { items in
            XCTAssertTrue(action.accepted(items), "A confirmed fact must never be replaced by the old list")
        }
        await listGate.release()
        await state.flush()
        let actual = await scripts.snapshot()
        XCTAssertTrue(action.accepted(state.scripts))
        XCTAssertEqual(state.scripts, actual)
        XCTAssertEqual(state.item("b")?.name, uncertain ? "committed-b" : "b")
        XCTAssertTrue(state.pending.isEmpty)
        XCTAssertTrue(state.checking.isEmpty)
        XCTAssertFalse(state.importing)
        retained.cancel()
        if action == .enable {
            state.setScript("a", enabled: false); await state.flush()
            let requests = await scripts.enabledRequests
            XCTAssertEqual(requests, [true, false], "The visible switch must not suppress the next real disable")
            let disabled = await scripts.snapshot()
            XCTAssertEqual(state.scripts, disabled)
            XCTAssertFalse(try XCTUnwrap(state.item("a")).enabled)
        }
        state.close(); await model.close(); await service.close()
    }

    func testUncertainFileAndNetworkImportsReadBackTheCommittedInstallation() async throws {
        for local in [false, true] {
            let scripts = ManagementReadbackOwner(), service = try await NativeMusicFixture.service()
            let model = MusicViewModel(service: service, player: ManagementReadbackPlayer(), scriptSources: scripts)
            await model.load()
            let state = MusicSourceManagementState(model: model)
            state.start(); await state.flush()
            let file = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString + ".js")
            try musicScriptFixture.write(to: file)
            defer { try? FileManager.default.removeItem(at: file) }
            await scripts.failImportAfterCommit()
            if local { state.importFiles([file]) }
            else { state.download("https://source.invalid/new.js") }
            await state.flush()
            let actual = await scripts.snapshot()
            XCTAssertEqual(state.scripts, actual)
            XCTAssertNotNil(state.item("imported"))
            XCTAssertFalse(state.importing)
            XCTAssertNotNil(state.error, "Reconciliation must not turn an uncertain receipt into success")
            state.close(); await model.close(); await service.close()
        }
    }

    func testCloseAndAccessRevocationRejectLateReadbackAndReopenReconcilesOwner() async throws {
        for revoke in [false, true] {
            let scripts = ManagementReadbackOwner(), service = try await NativeMusicFixture.service()
            let model = MusicViewModel(service: service, player: ManagementReadbackPlayer(), scriptSources: scripts)
            await model.load()
            let state = MusicSourceManagementState(model: model)
            state.start(); await state.flush()
            let writeGate = NativeMusicGate()
            await scripts.holdWrite(writeGate)
            state.setScript("a", enabled: true)
            await writeGate.started()
            await scripts.setUncertain(true)
            let gate = NativeMusicGate()
            await scripts.holdList(gate)
            state.edit("b", name: "committed-b", url: nil)
            await gate.started()
            // Retain the waiter before close clears the state's task table.
            let entered = expectation(description: "drain scheduled")
            let draining = Task { entered.fulfill(); await state.flush() }
            await fulfillment(of: [entered], timeout: 3)
            if revoke { model.setAvailable(false); state.reconcileAccess() }
            else { state.close() }
            let inactive = state.scripts
            let reads = await scripts.listCalls
            await writeGate.release(); await gate.release(); await draining.value
            XCTAssertEqual(state.scripts, inactive)
            XCTAssertNil(state.error)
            let after = await scripts.listCalls
            XCTAssertEqual(after, reads, "An inactive page must not retry its stale read")
            if revoke { model.setAvailable(true); await model.load(); state.reconcileAccess() }
            else { state.start() }
            await state.flush()
            let actual = await scripts.snapshot()
            XCTAssertEqual(state.scripts, actual)
            XCTAssertEqual(state.item("b")?.name, "committed-b")
            XCTAssertTrue(try XCTUnwrap(state.item("a")).enabled)
            state.close(); await model.close(); await service.close()
        }
    }
}


extension MusicSourceManagementReadbackTests {
    func testLateDetailCannotReplaceEditedAddressOrUpdatedRollbackAndOtherScriptStillCompletes() async throws {
        for edit in [true, false] {
            let scripts = ManagementReadbackOwner(), service = try await NativeMusicFixture.service()
            await scripts.prepareNetworkSource()
            let model = MusicViewModel(service: service, player: ManagementReadbackPlayer(), scriptSources: scripts)
            await model.load()
            let state = MusicSourceManagementState(model: model)
            state.start(); await state.flush()
            let old = NativeMusicGate(), other = NativeMusicGate()
            await scripts.holdDetail("a", gate: old)
            await scripts.holdDetail("b", gate: other)
            state.readDetail("a"); state.readDetail("b")
            await old.started(); await other.started()
            let fresh = expectation(description: "New A detail published before old receipt")
            let observation = state.$details.filter { values in
                guard let a = values["a"] else { return false }
                return edit ? a.networkURL == "https://source.invalid/new.js" : a.rollbackAvailable
            }.prefix(1).sink { _ in fresh.fulfill() }
            if edit { state.edit("a", name: "renamed", url: "https://source.invalid/new.js") }
            else { state.update("a") }
            await fulfillment(of: [fresh], timeout: 3)
            observation.cancel()
            let retained = state.$details.sink { values in
                if edit { XCTAssertEqual(values["a"]?.networkURL, "https://source.invalid/new.js") }
                else { XCTAssertEqual(values["a"]?.rollbackAvailable, true) }
            }
            await old.release(); await other.release(); await state.flush()
            retained.cancel()
            let actual = try await scripts.scriptSourceDetail("a")
            XCTAssertEqual(state.details["a"]?.item, actual.item)
            XCTAssertEqual(state.details["a"]?.networkURL, actual.networkURL)
            XCTAssertEqual(state.details["a"]?.rollbackAvailable, actual.rollbackAvailable)
            XCTAssertEqual(state.details["b"]?.item.id, "b", "A's replacement must not invalidate B's detail")
            XCTAssertNil(state.error)
            state.close(); await model.close(); await service.close()
        }
    }

    func testLateDetailCannotReappearAfterDeleteAndDoesNotCancelOtherScript() async throws {
        let scripts = ManagementReadbackOwner(), service = try await NativeMusicFixture.service()
        let model = MusicViewModel(service: service, player: ManagementReadbackPlayer(), scriptSources: scripts)
        await model.load()
        let state = MusicSourceManagementState(model: model)
        state.start(); await state.flush()
        state.readDetail("a"); await state.flush()
        XCTAssertNotNil(state.details["a"])
        let old = NativeMusicGate(), other = NativeMusicGate()
        await scripts.holdDetail("a", gate: old)
        await scripts.holdDetail("b", gate: other)
        state.readDetail("a"); state.readDetail("b")
        await old.started(); await other.started()
        let deleted = expectation(description: "A deleted before detail returns")
        let observation = state.$scripts.filter { !$0.contains { $0.id == "a" } }
            .prefix(1).sink { _ in deleted.fulfill() }
        state.remove("a")
        await fulfillment(of: [deleted], timeout: 3)
        observation.cancel()
        XCTAssertNil(state.details["a"])
        let retained = state.$details.sink { XCTAssertNil($0["a"], "A deleted detail must not be resurrected") }
        await old.release(); await other.release(); await state.flush()
        retained.cancel()
        XCTAssertNil(state.item("a")); XCTAssertNil(state.details["a"])
        XCTAssertEqual(state.details["b"]?.item.id, "b")
        XCTAssertNil(state.error)
        state.close(); await model.close(); await service.close()
    }
}


extension MusicSourceManagementReadbackTests {
    func testUncertainChangeListReconciliationInvalidatesOnlyAffectedDetails() async throws {
        for operation in ["update", "edit", "remove"] {
            let scripts = ManagementReadbackOwner(), service = try await NativeMusicFixture.service()
            await scripts.prepareNetworkSource()
            let model = MusicViewModel(service: service, player: ManagementReadbackPlayer(), scriptSources: scripts)
            await model.load()
            let state = MusicSourceManagementState(model: model)
            state.start(); await state.flush()
            let old = NativeMusicGate(), other = NativeMusicGate(), list = NativeMusicGate()
            await scripts.holdDetail("a", gate: old); await scripts.holdDetail("b", gate: other)
            state.readDetail("a"); state.readDetail("b")
            await old.started(); await other.started()
            await scripts.failChangeAfterCommit()
            await scripts.holdList(list)
            let accepted = expectation(description: "Uncertain write reconciled before old detail returns")
            var observation: AnyCancellable
            if operation == "remove" {
                observation = state.$scripts.filter { !$0.contains { $0.id == "a" } }
                    .prefix(1).sink { _ in accepted.fulfill() }
                state.remove("a")
            } else {
                observation = state.$details.filter { values in
                    guard let a = values["a"] else { return false }
                    return operation == "edit" ? a.networkURL == "https://source.invalid/new.js" : a.rollbackAvailable
                }.prefix(1).sink { _ in accepted.fulfill() }
                if operation == "edit" {
                    // URL is detail-only: the accepted list item is unchanged.
                    state.edit("a", name: "a", url: "https://source.invalid/new.js")
                } else { state.update("a") }
            }
            await list.started(); await list.release()
            await fulfillment(of: [accepted], timeout: 3)
            observation.cancel()
            let retained = state.$details.sink { values in
                switch operation {
                case "remove": XCTAssertNil(values["a"])
                case "edit": XCTAssertEqual(values["a"]?.networkURL, "https://source.invalid/new.js")
                default: XCTAssertEqual(values["a"]?.rollbackAvailable, true)
                }
            }
            await old.release(); await other.release(); await state.flush()
            retained.cancel()
            let actual = await scripts.snapshot(), calls = await scripts.detailCalls
            XCTAssertEqual(state.scripts, actual)
            XCTAssertEqual(calls["a"], operation == "remove" ? 1 : 2)
            XCTAssertEqual(calls["b"], 1, "Unrelated details must neither be invalidated nor fetched again")
            XCTAssertEqual(state.details["b"]?.item.id, "b")
            XCTAssertNotNil(state.error, "An uncertain receipt must not be reported as success")
            state.close(); await model.close(); await service.close()
        }
    }
}
