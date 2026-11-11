import Foundation
import Combine
import MeowContracts
import XCTest
@testable import MeowMusicFeature
@testable import MeowMusicNativeService

private actor MusicSourceManagementFixture: MusicScriptSourceManaging {
    var items: [MusicScriptSourceItem] = []
    var gate: NativeMusicGate?
    var failure: MusicServiceError?
    var enabledRequests: [Bool] = []
    var importRequests: [String] = []
    var writeWasCancelled = false
    var cancelledReads = Set<String>()
    private var importGate: NativeMusicGate?
    private var listGate: NativeMusicGate?
    private var detailGate: NativeMusicGate?
    private var checkGate: NativeMusicGate?
    private var checkCancellation: (@Sendable () -> Void)?
    func hold(_ gate: NativeMusicGate) { self.gate = gate }
    func holdImport(_ gate: NativeMusicGate) { importGate = gate }
    func holdList(_ gate: NativeMusicGate) { listGate = gate }
    func holdDetail(_ gate: NativeMusicGate) { detailGate = gate }
    func holdCheck(_ gate: NativeMusicGate, cancelled: @escaping @Sendable () -> Void) {
        checkGate = gate; checkCancellation = cancelled
    }
    func fail(_ error: MusicServiceError) { failure = error }
    func scriptSources() async -> [MusicScriptSourceItem] {
        let snapshot = items
        if let gate = listGate { listGate = nil; await gate.wait() }
        if Task.isCancelled { cancelledReads.insert("list") }
        return snapshot
    }
    func importScriptSource(_ data: Data, fileName: String) async throws -> MusicScriptSourceItem {
        importRequests.append(fileName)
        if let gate = importGate { importGate = nil; await gate.wait() }
        writeWasCancelled = writeWasCancelled || Task.isCancelled
        try Task.checkCancellation()
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
        writeWasCancelled = writeWasCancelled || Task.isCancelled
        try Task.checkCancellation()
        let failure = failure; self.failure = nil
        if let failure, failure != .commitUnconfirmed { throw failure }
        let old = try XCTUnwrap(items.first { $0.id == id })
        let value = MusicScriptSourceItem(id: id, name: old.name, version: old.version, origin: old.origin,
            platforms: enabled ? [.qq] : old.platforms, enabled: enabled, state: enabled ? .ready : .disabled)
        items = items.map { $0.id == id ? value : $0 }
        if let failure { throw failure }
        return value
    }
    func scriptSourceDetail(_ id: String) async throws -> MusicScriptSourceDetail {
        let snapshot = MusicScriptSourceDetail(item: try XCTUnwrap(items.first { $0.id == id }),
                                              networkURL: nil, rollbackAvailable: true)
        if let gate = detailGate { detailGate = nil; await gate.wait() }
        if Task.isCancelled { cancelledReads.insert("detail") }
        return snapshot
    }
    func editScriptSource(_ id: String, name: String, networkURL: String?) throws -> MusicScriptSourceItem {
        let old = try XCTUnwrap(items.first { $0.id == id })
        let value = MusicScriptSourceItem(id: id, name: name, version: old.version, origin: old.origin,
            platforms: old.platforms, enabled: old.enabled, state: old.state, check: old.check)
        items = items.map { $0.id == id ? value : $0 }; return value
    }
    func updateScriptSource(_ id: String) throws -> MusicScriptSourceItem { try XCTUnwrap(items.first { $0.id == id }) }
    func updateScriptSource(_ id: String, data: Data, fileName: String) throws -> MusicScriptSourceItem { try updateScriptSource(id) }
    func rollbackScriptSource(_ id: String) throws -> MusicScriptSourceItem { try updateScriptSource(id) }
    func checkScriptSource(_ id: String) async throws -> MusicScriptSourceItem {
        if let gate = checkGate {
            checkGate = nil
            let cancelled = checkCancellation; checkCancellation = nil
            await withTaskCancellationHandler { await gate.wait() } onCancel: { cancelled?() }
        }
        // Deliberately return a late success even if cancellation was ignored.
        let old = try XCTUnwrap(items.first { $0.id == id })
        let value = MusicScriptSourceItem(id: id, name: old.name, version: old.version, origin: old.origin,
            platforms: old.platforms, enabled: old.enabled, state: old.state,
            check: .init(summary: "normal", checkedAt: Date(), platforms: ["tx": "ok"]))
        if !Task.isCancelled { items = items.map { $0.id == id ? value : $0 } }
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
    func testBatchCheckDisabledScriptsDoesNotEnableAndEditKeepsInstallationIdentity() async throws {
        let scripts = MusicSourceManagementFixture()
        let item = try await scripts.importScriptSource(musicScriptFixture, fileName: "fixture.js")
        let service = try await NativeMusicFixture.service()
        let model = MusicViewModel(service: service, player: SourceSettingsPlayer(), scriptSources: scripts)
        await model.load()
        let state = MusicSourceManagementState(model: model)
        state.start(); await state.flush()
        state.select(item.id); state.checkSelected(); await state.flush()
        XCTAssertFalse(try XCTUnwrap(state.item(item.id)).enabled)
        XCTAssertEqual(state.item(item.id)?.check?.platforms["tx"], "ok")
        state.edit(item.id, name: "Renamed", url: nil); await state.flush()
        XCTAssertEqual(state.item(item.id)?.name, "Renamed")
        XCTAssertEqual(state.scripts.map(\.id), [item.id])
        state.removeSelected(); await state.flush(); XCTAssertTrue(state.scripts.isEmpty)
        state.close(); await model.close(); await service.close()
    }

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
        let item = try await scripts.importScriptSource(musicScriptFixture, fileName: "fixture.js")
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

    func testHidingCancelsScriptAndOfficialChecksAndQuickReturnRejectsLateResults() async throws {
        let scripts = MusicSourceManagementFixture(), native = try await NativeMusicFixture.service()
        let item = try await scripts.importScriptSource(musicScriptFixture, fileName: "fixture.js")
        let service = SourceManagementService(native)
        let model = MusicViewModel(service: service, player: SourceSettingsPlayer(), scriptSources: scripts)
        await model.load()
        let state = MusicSourceManagementState(model: model), page = NSObject()
        state.start(); state.show(page); await state.flush()
        let script = NativeMusicGate(), official = NativeMusicGate()
        let scriptCancelled = expectation(description: "script check cancelled on hide")
        let officialCancelled = expectation(description: "official check cancelled on hide")
        await scripts.holdCheck(script) { scriptCancelled.fulfill() }
        await service.holdCheck(official) { officialCancelled.fulfill() }
        state.checkScript(item.id); state.checkOfficial(.qq)
        await script.started(); await official.started()
        state.hide(page); state.show(page)
        await fulfillment(of: [scriptCancelled, officialCancelled], timeout: 3)
        await script.release(); await official.release(); await state.flush()
        XCTAssertTrue(state.checking.isEmpty); XCTAssertTrue(state.pending.isEmpty)
        XCTAssertNil(state.item(item.id)?.check)
        XCTAssertNil(state.officialChecks[.qq]); XCTAssertNil(state.error)
        let notifications = await service.configurationNotifications
        XCTAssertEqual(notifications, 0, "Cancelled checks are not configuration writes")
        state.checkScript(item.id); state.checkOfficial(.qq); await state.flush()
        XCTAssertEqual(state.item(item.id)?.check?.summary, "normal")
        XCTAssertEqual(state.officialChecks[.qq], true)
        state.close(); await model.close(); await native.close()
    }

    func testHiddenListAndDetailReadsAreCancelledAndCannotReplaceNewFactsAfterReturn() async throws {
        let scripts = MusicSourceManagementFixture(), native = try await NativeMusicFixture.service()
        let item = try await scripts.importScriptSource(musicScriptFixture, fileName: "fixture.js")
        let model = MusicViewModel(service: native, player: SourceSettingsPlayer(), scriptSources: scripts)
        await model.load()
        let state = MusicSourceManagementState(model: model), page = NSObject()
        state.start(); state.show(page); await state.flush()
        let list = NativeMusicGate(), detail = NativeMusicGate()
        await scripts.holdList(list); await scripts.holdDetail(detail)
        state.reload(); state.readDetail(item.id)
        await list.started(); await detail.started()
        state.hide(page)
        _ = try await scripts.editScriptSource(item.id, name: "New owner fact", networkURL: nil)
        let reconciled = expectation(description: "new list and detail read back")
        let observation = state.$details.filter { $0[item.id]?.item.name == "New owner fact" }.prefix(1)
            .sink { _ in reconciled.fulfill() }
        state.show(page); state.readDetail(item.id)
        await fulfillment(of: [reconciled], timeout: 3)
        observation.cancel()
        await list.release(); await detail.release(); await state.flush()
        XCTAssertEqual(state.item(item.id)?.name, "New owner fact")
        XCTAssertEqual(state.details[item.id]?.item.name, "New owner fact")
        let cancelled = await scripts.cancelledReads
        XCTAssertEqual(cancelled, ["list", "detail"])
        XCTAssertNil(state.error)
        state.close(); await model.close(); await native.close()
    }

    func testVisibleDetailHandoffCancelsListWorkWithoutLettingOldListHideDetail() async throws {
        let scripts = MusicSourceManagementFixture(), native = try await NativeMusicFixture.service()
        let item = try await scripts.importScriptSource(musicScriptFixture, fileName: "fixture.js")
        let model = MusicViewModel(service: native, player: SourceSettingsPlayer(), scriptSources: scripts)
        await model.load()
        let state = MusicSourceManagementState(model: model), list = NSObject(), detail = NSObject()
        state.start(); state.show(list); await state.flush()
        let gate = NativeMusicGate(), cancelled = expectation(description: "covered list check cancelled")
        await scripts.holdCheck(gate) { cancelled.fulfill() }
        state.checkScript(item.id); await gate.started()
        state.show(detail); state.hide(list)
        state.readDetail(item.id)
        await fulfillment(of: [cancelled], timeout: 3)
        await gate.release(); await state.flush()
        XCTAssertEqual(state.details[item.id]?.item.id, item.id, "The newly visible detail can still read")
        XCTAssertNil(state.item(item.id)?.check, "The covered list's late check cannot publish")
        state.hide(detail)
        state.setScript(item.id, enabled: true); await state.flush()
        let requests = await scripts.enabledRequests
        XCTAssertTrue(requests.isEmpty, "A hidden detail does not admit new work")
        state.close(); await model.close(); await native.close()
    }

    func testAcceptedScriptWriteSurvivesHideAndQuickReturnAndNotifiesPlaybackOnSuccessOrFailure() async throws {
        let failures: [MusicServiceError?] = [nil, .scriptFailed, .commitUnconfirmed]
        for failure in failures {
            let scripts = MusicSourceManagementFixture(), native = try await NativeMusicFixture.service()
            let item = try await scripts.importScriptSource(musicScriptFixture, fileName: "fixture.js")
            let service = SourceManagementService(native)
            let model = MusicViewModel(service: service, player: SourceSettingsPlayer(), scriptSources: scripts)
            await model.load()
            let state = MusicSourceManagementState(model: model), page = NSObject()
            state.start(); state.show(page); await state.flush()
            if let failure { await scripts.fail(failure) }
            let gate = NativeMusicGate()
            await scripts.hold(gate)
            state.setScript(item.id, enabled: true); await gate.started()
            state.hide(page)
            XCTAssertTrue(state.isPending(item.id), "A handed-off write remains in flight while hidden")
            state.show(page)
            state.setScript(item.id, enabled: true)
            await gate.release(); await state.flush()
            let requests = await scripts.enabledRequests, cancelled = await scripts.writeWasCancelled
            let notifications = await service.configurationNotifications, actual = await scripts.scriptSources()
            XCTAssertEqual(requests, [true], "Returning cannot resubmit the pending write")
            XCTAssertFalse(cancelled)
            XCTAssertEqual(notifications, 1)
            XCTAssertEqual(state.scripts, actual)
            XCTAssertEqual(state.item(item.id)?.enabled, failure != .scriptFailed)
            XCTAssertTrue(state.pending.isEmpty)
            XCTAssertEqual(state.error, failure?.localizedDescription)
            state.close(); await model.close(); await native.close()
        }
    }

    func testCloseAndReopenBeforeAcceptedWriteReceiptStillDrainsAndReconcilesOwner() async throws {
        let scripts = MusicSourceManagementFixture(), native = try await NativeMusicFixture.service()
        let item = try await scripts.importScriptSource(musicScriptFixture, fileName: "fixture.js")
        let service = SourceManagementService(native)
        let model = MusicViewModel(service: service, player: SourceSettingsPlayer(), scriptSources: scripts)
        await model.load()
        let state = MusicSourceManagementState(model: model)
        state.start(); await state.flush()
        let gate = NativeMusicGate()
        await scripts.hold(gate)
        state.setScript(item.id, enabled: true); await gate.started()
        state.close(); state.start()
        await gate.release(); await state.flush()
        let actual = await scripts.scriptSources(), cancelled = await scripts.writeWasCancelled
        let notifications = await service.configurationNotifications
        XCTAssertFalse(cancelled); XCTAssertEqual(notifications, 1)
        XCTAssertEqual(state.scripts, actual)
        XCTAssertTrue(try XCTUnwrap(state.item(item.id)).enabled)
        XCTAssertTrue(state.pending.isEmpty); XCTAssertNil(state.error)
        state.close(); await model.close(); await native.close()
    }

    func testHiddenBatchImportSettlesAcceptedFileButDoesNotSubmitNextAfterQuickReturn() async throws {
        let scripts = MusicSourceManagementFixture(), native = try await NativeMusicFixture.service()
        let service = SourceManagementService(native)
        let model = MusicViewModel(service: service, player: SourceSettingsPlayer(), scriptSources: scripts)
        await model.load()
        let state = MusicSourceManagementState(model: model), page = NSObject()
        state.start(); state.show(page); await state.flush()
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: directory) }
        let files = ["a.js", "b.js"].map { directory.appendingPathComponent($0) }
        for file in files { try musicScriptFixture.write(to: file) }
        let gate = NativeMusicGate()
        await scripts.holdImport(gate)
        state.importFiles(files); await gate.started()
        state.hide(page); state.show(page)
        await gate.release(); await state.flush()
        let imports = await scripts.importRequests, cancelled = await scripts.writeWasCancelled
        let notifications = await service.configurationNotifications
        XCTAssertEqual(imports, ["a.js"]); XCTAssertFalse(cancelled); XCTAssertEqual(notifications, 1)
        XCTAssertEqual(state.scripts.map(\.name), ["a.js"])
        XCTAssertFalse(state.importing); XCTAssertNil(state.error)
        state.importFiles([files[1]]); await state.flush()
        let resumed = await scripts.importRequests
        XCTAssertEqual(resumed, ["a.js", "b.js"], "A fresh batch may import the remaining file")
        state.close(); await model.close(); await native.close()
    }

    func testHidingBeforeDispatchStopsQueuedWritesWithoutNotifyingPlayback() async throws {
        let scripts = MusicSourceManagementFixture(), native = try await NativeMusicFixture.service()
        let item = try await scripts.importScriptSource(musicScriptFixture, fileName: "fixture.js")
        let service = SourceManagementService(native)
        let model = MusicViewModel(service: service, player: SourceSettingsPlayer(), scriptSources: scripts)
        await model.load()
        let state = MusicSourceManagementState(model: model), page = NSObject()
        state.start(); state.show(page); await state.flush()
        state.setScript(item.id, enabled: true)
        state.setOfficial(.qq, enabled: false)
        state.download("https://source.invalid/queued.js")
        state.hide(page); state.show(page)
        await state.flush()
        let requests = await scripts.enabledRequests, notifications = await service.configurationNotifications
        let settings = await service.settingsRequests, actual = await scripts.scriptSources()
        XCTAssertTrue(requests.isEmpty); XCTAssertEqual(notifications, 0); XCTAssertEqual(settings, 0)
        XCTAssertEqual(actual.map(\.id), [item.id])
        XCTAssertTrue(state.pending.isEmpty); XCTAssertFalse(state.importing); XCTAssertFalse(state.savingOfficial)
        XCTAssertTrue(state.officialEnabled(.qq)); XCTAssertNil(state.error)
        state.close(); await model.close(); await native.close()
    }

    func testAcceptedOfficialWriteSettlesWithoutCancellationAfterHideOrCloseAndQuickReturn() async throws {
        for close in [false, true] {
            let native = try await NativeMusicFixture.service(), service = SourceManagementService(native)
            let model = MusicViewModel(service: service, player: SourceSettingsPlayer())
            await model.load()
            let state = MusicSourceManagementState(model: model), page = NSObject(), gate = NativeMusicGate()
            state.start(); state.show(page)
            await service.holdSettings(gate)
            state.setOfficial(.qq, enabled: false); await gate.started()
            if close { state.close(); state.start() }
            else { state.hide(page); state.show(page) }
            await gate.release(); await state.flush()
            let saved = try await native.state(), cancelled = await service.settingsWasCancelled
            XCTAssertFalse(cancelled)
            XCTAssertFalse(saved.officialSources.contains(.qq))
            XCTAssertEqual(model.state.officialSources, saved.officialSources)
            XCTAssertEqual(state.official, saved.officialSources)
            XCTAssertFalse(state.savingOfficial); XCTAssertNil(model.error)
            state.close(); await model.close(); await native.close()
        }
    }
}

private actor SourceManagementService: MusicServing, MusicSourcePlaybackServing, MusicOfficialSourceChecking {
    let base: NativeMusicService
    private var checkGate: NativeMusicGate?
    private var checkCancellation: (@Sendable () -> Void)?
    private var settingsGate: NativeMusicGate?
    private(set) var settingsRequests = 0
    private(set) var settingsWasCancelled = false
    private(set) var configurationNotifications = 0
    init(_ base: NativeMusicService) { self.base = base }
    func holdCheck(_ gate: NativeMusicGate, cancelled: @escaping @Sendable () -> Void) {
        checkGate = gate; checkCancellation = cancelled
    }
    func holdSettings(_ gate: NativeMusicGate) { settingsGate = gate }
    func checkOfficialSource(_ platform: MusicAccountPlatform) async throws {
        if let gate = checkGate {
            checkGate = nil
            let cancelled = checkCancellation; checkCancellation = nil
            await withTaskCancellationHandler { await gate.wait() } onCancel: { cancelled?() }
        }
        try Task.checkCancellation()
    }
    func updateSettings(_ change: MusicSettingsChange) async throws -> MusicPlaybackState {
        settingsRequests += 1
        if let gate = settingsGate { settingsGate = nil; await gate.wait() }
        settingsWasCancelled = settingsWasCancelled || Task.isCancelled
        try Task.checkCancellation()
        return try await base.updateSettings(change)
    }
    func sourceConfigurationChanged() async { await base.sourceConfigurationChanged(); configurationNotifications += 1 }
    func state() async throws -> MusicPlaybackState { try await base.state() }
    func platforms() async throws -> [MusicPlatform] { try await base.platforms() }
    func collections() async throws -> [MusicCollection] { try await base.collections() }
    func queue() async throws -> MusicQueueSnapshot { try await base.queue() }
    func sourceAvailability(for ref: MusicReference) async throws -> MusicSourceAvailability { try await base.sourceAvailability(for: ref) }
    func search(platform: String, query: String, page: Int, limit: Int) async throws -> MusicSearchPage {
        try await base.search(platform: platform, query: query, page: page, limit: limit)
    }
    func tracks(in collection: MusicCollection) async throws -> [MusicRemoteTrack] { try await base.tracks(in: collection) }
    func createPlaylist(name: String, refs: [MusicReference]) async throws -> MusicCollection { try await base.createPlaylist(name: name, refs: refs) }
    func editPlaylist(_ collection: MusicCollection, name: String?, refs: [MusicReference]?, order: [String]?) async throws -> MusicCollection {
        try await base.editPlaylist(collection, name: name, refs: refs, order: order)
    }
    func removePlaylist(_ collection: MusicCollection) async throws { try await base.removePlaylist(collection) }
    func changeQueue(_ change: MusicQueueChange, expectedRevision: Int) async throws -> MusicPlaybackState {
        try await base.changeQueue(change, expectedRevision: expectedRevision)
    }
    func advance(_ direction: MusicDirection, reason: MusicAdvanceReason, expectedRevision: Int) async throws -> MusicAdvanceResult {
        try await base.advance(direction, reason: reason, expectedRevision: expectedRevision)
    }
    func resolve(attemptId: String, occurrenceId: String, selection: MusicPlaybackSelection) async throws -> MusicPlaybackCandidate {
        try await base.resolve(attemptId: attemptId, occurrenceId: occurrenceId, selection: selection)
    }
    func cancel(attemptId: String?) async throws { try await base.cancel(attemptId: attemptId) }
    func record(_ fact: MusicPlaybackFact) async throws -> Bool { try await base.record(fact) }
    func lyrics(occurrenceId: String, sourceKey: String) async throws -> MusicLyricsSnapshot { try await base.lyrics(occurrenceId: occurrenceId, sourceKey: sourceKey) }
    func playedSongs() async throws -> [MusicPlayedSong] { try await base.playedSongs() }
    func playedPlaylists() async throws -> [MusicPlayedPlaylist] { try await base.playedPlaylists() }
    func restoreHistory(id: String, expectedRevision: Int) async throws -> MusicPlaybackState { try await base.restoreHistory(id: id, expectedRevision: expectedRevision) }
    func metadataPreferences() async throws -> MusicMetadataPreferences { try await base.metadataPreferences() }
    func updateMetadataPreferences(_ change: MusicMetadataPreferencesChange) async throws -> MusicMetadataPreferences { try await base.updateMetadataPreferences(change) }
    func metadataCandidates(occurrenceID: String) async throws -> MusicMetadataCandidates { try await base.metadataCandidates(occurrenceID: occurrenceID) }
    func inspectSources(for reference: MusicReference) async throws -> any MusicSourceInspectionSession { try await base.inspectSources(for: reference) }
    func sourceSession(occurrenceID: String, expectedQueueRevision: Int) async throws -> any MusicSourcePlaybackSession {
        try await base.sourceSession(occurrenceID: occurrenceID, expectedQueueRevision: expectedQueueRevision)
    }
    func activateSource(attemptID: String) async throws { try await base.activateSource(attemptID: attemptID) }
    func discardSource(attemptID: String) async { await base.discardSource(attemptID: attemptID) }
}
