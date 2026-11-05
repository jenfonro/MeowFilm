import Foundation
import MeowContracts
import XCTest
@testable import MeowMusicFeature
@testable import MeowMusicNativeService

private actor MusicSourceManagementFixture: MusicScriptSourceManaging {
    var items: [MusicScriptSourceItem] = []
    var gate: NativeMusicGate?
    var failure: MusicServiceError?
    var enabledRequests: [Bool] = []
    func hold(_ gate: NativeMusicGate) { self.gate = gate }
    func fail(_ error: MusicServiceError) { failure = error }
    func scriptSources() -> [MusicScriptSourceItem] { items }
    func importScriptSource(_ data: Data, fileName: String) -> MusicScriptSourceItem {
        let value = MusicScriptSourceItem(id: UUID().uuidString, name: fileName, version: "1", origin: .file,
                                         platforms: [], enabled: false, state: .disabled)
        items.append(value); return value
    }
    func downloadScriptSource(from url: URL) -> MusicScriptSourceItem {
        let value = MusicScriptSourceItem(id: UUID().uuidString, name: url.lastPathComponent, version: "1",
                                         origin: .network(host: url.host!), platforms: [], enabled: false, state: .disabled)
        items.append(value); return value
    }
    func setScriptSourceEnabled(_ id: String, enabled: Bool) async throws -> MusicScriptSourceItem {
        enabledRequests.append(enabled)
        if let gate { self.gate = nil; await gate.wait() }
        if let failure { self.failure = nil; throw failure }
        let old = try XCTUnwrap(items.first { $0.id == id })
        let value = MusicScriptSourceItem(id: id, name: old.name, version: old.version, origin: old.origin,
            platforms: enabled ? [.qq] : old.platforms, enabled: enabled, state: enabled ? .ready : .disabled)
        items = items.map { $0.id == id ? value : $0 }
        return value
    }
    func removeScriptSource(_ id: String) { items.removeAll { $0.id == id } }
}

@MainActor private final class SourceSettingsPlayer: MusicPlaybackDriving {
    func play(_ resource: PlaybackResource) -> MusicPlayerAttempt { XCTFail("Settings started playback"); return .init(id: UUID(), resourceID: resource.id) }
    func pause(_ attempt: MusicPlayerAttempt) {}
    func resume(_ attempt: MusicPlayerAttempt) {}
    func seek(_ position: Double, attempt: MusicPlayerAttempt) -> Bool { false }
    func stop(_ attempt: MusicPlayerAttempt) {}
}

@MainActor
final class MusicSourceManagementStateTests: XCTestCase {
    func testPageUsesOneListAndConfirmedOwnerSettingsAndSupportsBothImportActionsEnableAndDelete() async throws {
        let scripts = MusicSourceManagementFixture(), store = NativeMusicMemoryDocument()
        let service = try await NativeMusicFixture.service(store)
        let model = MusicViewModel(service: service, player: SourceSettingsPlayer(), scriptSources: scripts)
        await model.load()
        let state = MusicSourceManagementState(model: model)
        state.start(); await state.flush()
        XCTAssertEqual(state.official, MusicAccountPlatform.allCases); XCTAssertTrue(state.scripts.isEmpty)
        for platform in MusicAccountPlatform.allCases { state.setOfficial(platform, enabled: false); await state.flush() }
        let persisted = try await service.state()
        XCTAssertTrue(persisted.officialSources.isEmpty); XCTAssertTrue(state.official.isEmpty)
        state.download("https://source.invalid/network.js"); await state.flush()
        let file = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString + ".js")
        try musicScriptFixture.write(to: file)
        defer { try? FileManager.default.removeItem(at: file) }
        state.importFiles([file]); await state.flush()
        XCTAssertEqual(state.scripts.count, 2); XCTAssertTrue(state.scripts.allSatisfy { !$0.enabled })
        let id = try XCTUnwrap(state.scripts.first?.id)
        state.setScript(id, enabled: true); await state.flush()
        XCTAssertEqual(state.item(id)?.state, .ready)
        state.remove(id); await state.flush()
        XCTAssertNil(state.item(id)); XCTAssertEqual(state.scripts.count, 1)
        XCTAssertNil(state.error)
        state.close(); await model.close(); await service.close()
    }

    func testFailedEnableResetsSwitchAndReportsClosedErrorWithoutPublishingLateReceiptsAfterAccessLoss() async throws {
        let scripts = MusicSourceManagementFixture()
        let item = await scripts.importScriptSource(musicScriptFixture, fileName: "fixture.js")
        let service = try await NativeMusicFixture.service()
        let model = MusicViewModel(service: service, player: SourceSettingsPlayer(), scriptSources: scripts)
        await model.load()
        let state = MusicSourceManagementState(model: model)
        state.start(); await state.flush()
        await scripts.fail(.scriptFailed)
        state.setScript(item.id, enabled: true); await state.flush()
        XCTAssertEqual(state.error, MusicServiceError.scriptFailed.localizedDescription)
        XCTAssertFalse(state.scriptEnabled(try XCTUnwrap(state.item(item.id))))
        let gate = NativeMusicGate()
        await scripts.hold(gate)
        state.setScript(item.id, enabled: true)
        await gate.started()
        model.setAvailable(false); state.reconcileAccess()
        XCTAssertTrue(state.scripts.isEmpty); XCTAssertTrue(state.pending.isEmpty)
        await gate.release()
        model.setAvailable(true); await model.load(); state.reconcileAccess(); await state.flush()
        // A committed owner operation is reconciled in the new access generation,
        // not copied from the revoked page task's late receipt.
        let actual = await scripts.scriptSources()
        state.reload(); await state.flush()
        XCTAssertEqual(state.scripts, actual)
        state.close(); await model.close(); await service.close()
    }
}
