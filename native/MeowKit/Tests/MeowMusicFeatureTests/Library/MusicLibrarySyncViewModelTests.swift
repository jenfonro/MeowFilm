#if os(iOS) || os(macOS)
import Foundation
import XCTest
import Combine
@testable import MeowMusicFeature

private actor SyncPageGate {
    private var entered = false, released = false
    private var arrivals: [CheckedContinuation<Void, Never>] = []
    private var waiters: [CheckedContinuation<Void, Never>] = []
    func wait() async {
        entered = true; arrivals.forEach { $0.resume() }; arrivals.removeAll()
        if !released { await withCheckedContinuation { waiters.append($0) } }
    }
    func started() async { if !entered { await withCheckedContinuation { arrivals.append($0) } } }
    func release() { released = true; waiters.forEach { $0.resume() }; waiters.removeAll() }
}
private actor SyncPageService: MusicLibrarySyncing, MusicAccountsServing {
    private var requests: [MusicLibrarySyncRequest] = []
    private var cancelled: [UUID] = []
    private var readGate: SyncPageGate?, writeGate: SyncPageGate?
    func configure(read: SyncPageGate? = nil, write: SyncPageGate? = nil) { readGate = read; writeGate = write }
    func calls() -> [MusicLibrarySyncRequest] { requests }
    func librarySyncState() async -> MusicLibrarySyncState {
        if let readGate { await readGate.wait() }
        return .init(phase: .idle)
    }
    func synchronizeLibrary(_ request: MusicLibrarySyncRequest) async -> MusicLibrarySyncState {
        requests.append(request)
        if let writeGate { await writeGate.wait() } // Intentionally ignores cancellation.
        return .init(selections: request.selections, phase: .synced)
    }
    func cancelLibrarySync(id: UUID) { cancelled.append(id) }
    func statuses() -> [MusicAccountStatus] { [.init(platform: .qq, phase: .signedIn, nickname: "Synthetic", canLogin: true)] }
    func validate(platform: MusicAccountPlatform) throws -> MusicAccountStatus { throw MusicServiceError.unavailable }
    func beginLogin(platform: MusicAccountPlatform) throws -> MusicLoginChallenge { throw MusicServiceError.unavailable }
    func checkLogin(challengeID: UUID) throws -> MusicLoginProgress { throw MusicServiceError.unavailable }
    func cancelLogin(challengeID: UUID) {}
    func logout(platform: MusicAccountPlatform) {}
}
private actor AutomaticSyncPageService: MusicAutomaticLibrarySyncing {
    private var preferences = MusicAutomaticLibrarySyncPreferences()
    private var failure: MusicServiceError?
    private var remoteFailure: MusicServiceError?
    private var gate: SyncPageGate?
    private var beforeCommit: SyncPageGate?
    private var synchronizationGate: SyncPageGate?
    private var synchronization: Task<Result<MusicLibrarySyncState, MusicServiceError>, Never>?
    private var saves = 0
    private var rounds = 0
    private var cancelled = false
    func configure(failure: MusicServiceError? = nil, remote: MusicServiceError? = nil,
                   gate: SyncPageGate? = nil, beforeCommit: SyncPageGate? = nil, synchronization: SyncPageGate? = nil) {
        self.failure = failure; remoteFailure = remote; self.gate = gate; self.beforeCommit = beforeCommit
        synchronizationGate = synchronization
    }
    func observations() -> (saves: Int, cancelled: Bool, rounds: Int) { (saves, cancelled, rounds) }
    func finishSynchronization() async { _ = await synchronization?.value }
    func automaticLibrarySyncPreferences() -> MusicAutomaticLibrarySyncPreferences { preferences }
    func librarySyncState() -> MusicLibrarySyncState { .init(selections: preferences.selections) }
    func automaticLibrarySyncEvents() -> AsyncStream<MusicAutomaticLibrarySyncEvent> { AsyncStream { $0.finish() } }
    func synchronizeLibrary(_ request: MusicLibrarySyncRequest) throws -> MusicLibrarySyncState {
        XCTFail("Automatic settings must not use the manual-only port"); throw MusicServiceError.unsupported
    }
    func synchronizeAutomaticLibrary(_ request: MusicAutomaticLibrarySyncRequest) -> MusicLibrarySyncState { .init() }
    func cancelLibrarySync(id: UUID) {}
    func saveAutomaticLibrarySync(_ value: MusicAutomaticLibrarySyncPreferences, requestID: UUID,
                                  authorization: MusicQueueAuthorization) async throws -> MusicAutomaticLibrarySyncSaveResult {
        saves += 1
        let failure = failure, remote = remoteFailure, gate = gate, beforeCommit = beforeCommit, syncGate = synchronizationGate
        self.gate = nil; self.beforeCommit = nil; synchronizationGate = nil
        if let beforeCommit { await beforeCommit.wait() }
        if failure == nil || failure == .commitUnconfirmed {
            preferences = .init(revision: preferences.revision + 1, selections: value.selections)
        }
        let saved = preferences
        if let gate { await gate.wait() }
        cancelled = Task.isCancelled
        try authorization.check()
        if let failure { throw failure }
        rounds += 1
        let task = Task {
            defer { authorization.revoke() }
            if let syncGate { await syncGate.wait() }
            return remote.map { Result<MusicLibrarySyncState, MusicServiceError>.failure($0) } ??
                .success(.init(selections: saved.selections, phase: .synced))
        }
        synchronization = task
        return .init(preferences: saved, synchronization: task)
    }
}
@MainActor final class MusicLibrarySyncViewModelTests: XCTestCase {
    private let receipts = MusicSettingsReceiptOwner()
    func testSyncSnapshotCarriesOwnedDirectoryIDsWithoutRequiringCollectionMembers() {
        let existingCall = MusicLibrarySyncSnapshot(accountID: "account", collections: [], revision: "directory")
        XCTAssertTrue(existingCall.ownedCollectionIDs.isEmpty)
        let directory = MusicLibrarySyncSnapshot(accountID: "account", collections: [], revision: "directory",
                                                ownedCollectionIDs: ["favorites", "owned-playlist"])
        XCTAssertEqual(directory.ownedCollectionIDs, ["favorites", "owned-playlist"])
        XCTAssertTrue(directory.collections.isEmpty)
        XCTAssertEqual(directory.revision, "directory")
    }
    func testLoadNeverWritesDefaultsAreOffAndOnlyExplicitSelectedRequestWrites() async {
        let service = SyncPageService(), model = MusicLibrarySyncViewModel(service: SyncPageService(), settingsReceipts: receipts)
        model.activate(available: true, generation: UUID()); await model.load()
        XCTAssertFalse(model.canSync); XCTAssertTrue(model.selections.isEmpty)
        await model.synchronize()
        let page = MusicLibrarySyncViewModel(service: service, accounts: service, settingsReceipts: receipts)
        page.activate(available: true, generation: UUID()); await page.load()
        var calls = await service.calls(); XCTAssertTrue(calls.isEmpty)
        page.select(.qq, favorites: true); page.select(.netease, playlists: true)
        XCTAssertTrue(page.canSync); await page.synchronize()
        calls = await service.calls()
        XCTAssertEqual(calls.count, 1)
        XCTAssertEqual(calls[0].selections, [.init(platform: .qq, favorites: true), .init(platform: .netease, playlists: true)])
        XCTAssertEqual(page.state.phase, .synced); XCTAssertFalse(page.busy)
    }
    func testLateLoadsAndSyncsAfterLeaveLockGenerationOrCancelNeverRefillUI() async {
        for operation in ["load", "sync"] {
            for event in ["leave", "lock", "generation", "cancel"] {
                let service = SyncPageService(), gate = SyncPageGate()
                await service.configure(read: operation == "load" ? gate : nil, write: operation == "sync" ? gate : nil)
                let model = MusicLibrarySyncViewModel(service: service, accounts: service, settingsReceipts: receipts)
                let generation = UUID(); model.activate(available: true, generation: generation)
                model.select(.qq, favorites: true)
                let task = Task {
                    if operation == "load" { await model.load() } else { await model.synchronize() }
                }
                await gate.started()
                switch event {
                case "leave": model.deactivate()
                case "lock": model.activate(available: false, generation: generation)
                case "generation": model.activate(available: true, generation: UUID())
                default: model.cancel()
                }
                await gate.release(); await task.value
                XCTAssertEqual(model.state.phase, .idle); XCTAssertTrue(model.accounts.isEmpty)
                XCTAssertFalse(model.busy); XCTAssertNil(model.error)
            }
        }
    }
    func testInFlightLoadCannotUndoSelectionAndSyncIsSingleFlight() async {
        let service = SyncPageService(), gate = SyncPageGate(), model = MusicLibrarySyncViewModel(service: service, settingsReceipts: receipts)
        await service.configure(read: gate)
        model.activate(available: true, generation: UUID())
        let load = Task { await model.load() }; await gate.started()
        model.select(.kugou, playlists: true)
        await gate.release(); await load.value
        XCTAssertTrue(model.selection(.kugou).playlists)
        let writes = SyncPageGate(); await service.configure(write: writes)
        let task = Task { await model.synchronize() }; await writes.started()
        await model.synchronize()
        model.select(.qq, favorites: true)
        await writes.release(); await task.value
        let calls = await service.calls()
        XCTAssertEqual(calls.count, 1); XCTAssertEqual(calls[0].selections, [.init(platform: .kugou, playlists: true)])
        XCTAssertFalse(model.selection(.qq).favorites)
    }
    func testConfirmationBindsSelectionPageAccessAndForegroundWithoutRemoteWrites() async throws {
        for event in ["selection", "generation", "leave", "background", "reload", "dismiss"] {
            let service = SyncPageService(), model = MusicLibrarySyncViewModel(service: service, settingsReceipts: receipts)
            model.activate(available: true, generation: UUID())
            model.select(.qq, favorites: true); model.prepareConfirmation()
            let ticket = try XCTUnwrap(model.confirmation)
            switch event {
            case "selection": model.select(.netease, playlists: true)
            case "generation": model.activate(available: true, generation: UUID())
            case "leave": model.deactivate()
            case "background": model.setPresentationActive(false); model.setPresentationActive(true)
            case "reload": await model.load()
            default: model.dismissConfirmation(id: ticket.id)
            }
            XCTAssertNil(model.confirm(ticket), event)
            let calls = await service.calls()
            XCTAssertTrue(calls.isEmpty, event)
        }
    }
    func testConfirmedButQueuedTapCannotDispatchAfterPageOrProtectionChanges() async throws {
        for event in ["leave", "generation", "background"] {
            let service = SyncPageService(), model = MusicLibrarySyncViewModel(service: service, settingsReceipts: receipts)
            model.activate(available: true, generation: UUID())
            model.select(.qq, favorites: true); model.prepareConfirmation()
            let ticket = try XCTUnwrap(model.confirmation)
            let pending = try XCTUnwrap(model.confirm(ticket))
            // No actor yield before invalidation: exercise a delayed UI Task.
            switch event {
            case "leave": model.deactivate()
            case "generation": model.activate(available: true, generation: UUID())
            default: model.setPresentationActive(false)
            }
            await pending.value
            let calls = await service.calls()
            XCTAssertTrue(calls.isEmpty, event)
            XCTAssertFalse(model.busy)
        }
    }
    func testOldSheetCannotDismissSuccessorAndMatchingConsentDispatchesOnce() async throws {
        let service = SyncPageService(), model = MusicLibrarySyncViewModel(service: service, settingsReceipts: receipts)
        model.activate(available: true, generation: UUID())
        model.select(.qq, favorites: true); model.prepareConfirmation()
        let previous = try XCTUnwrap(model.confirmation)
        model.setPresentationActive(false); model.setPresentationActive(true)
        XCTAssertNil(model.confirmation)
        model.prepareConfirmation()
        let current = try XCTUnwrap(model.confirmation)
        model.dismissConfirmation(id: previous.id)
        XCTAssertEqual(model.confirmation?.id, current.id)
        XCTAssertNil(model.confirm(previous))
        let pending = try XCTUnwrap(model.confirm(current))
        XCTAssertNil(model.confirm(current))
        await pending.value
        let calls = await service.calls()
        XCTAssertEqual(calls.count, 1)
        XCTAssertEqual(calls.first?.selections, [.init(platform: .qq, favorites: true)])
        XCTAssertEqual(model.state.phase, .synced)
    }

    func testAutomaticSettingUnknownSurvivesDepartureAndOnlySameSettingSuccessClearsIt() async {
        let service = AutomaticSyncPageService(), gate = SyncPageGate()
        let model = MusicLibrarySyncViewModel(service: service, settingsReceipts: receipts)
        let generation = UUID()
        model.activate(available: true, generation: generation); await model.load()
        await service.configure(failure: .commitUnconfirmed, gate: gate)
        model.select(.qq, favorites: true)
        await gate.started()
        model.deactivate()
        await gate.release(); await receipts.flush()
        XCTAssertTrue(model.selections.isEmpty); XCTAssertNil(model.error); XCTAssertFalse(model.busy)
        XCTAssertEqual(receipts.unknown.map(\.setting), ["library/automatic/tx/favorites"])
        var calls = await service.observations()
        XCTAssertEqual(calls.saves, 1); XCTAssertFalse(calls.cancelled)
        model.activate(available: true, generation: generation); await model.load()
        XCTAssertTrue(model.selection(.qq).favorites, "Reopening reads the actual committed preference")
        XCTAssertEqual(receipts.unknown.count, 1)

        await service.configure()
        let changedOther = expectation(description: "other automatic preference settled")
        let other = model.$busy.dropFirst().filter { !$0 }.prefix(1).sink { _ in changedOther.fulfill() }
        model.select(.qq, playlists: true)
        await fulfillment(of: [changedOther], timeout: 2); other.cancel()
        XCTAssertEqual(receipts.unknown.count, 1)
        let changedSame = expectation(description: "same automatic preference settled")
        let same = model.$busy.dropFirst().filter { !$0 }.prefix(1).sink { _ in changedSame.fulfill() }
        model.select(.qq, favorites: false)
        await fulfillment(of: [changedSame], timeout: 2); same.cancel()
        XCTAssertTrue(receipts.unknown.isEmpty)
        calls = await service.observations(); XCTAssertEqual(calls.saves, 3)
        XCTAssertFalse(model.selection(.qq).favorites); XCTAssertTrue(model.selection(.qq).playlists)
    }

    func testConfirmedAutomaticPreferenceAndRemoteUnknownAreSeparateReceipts() async {
        let service = AutomaticSyncPageService()
        let model = MusicLibrarySyncViewModel(service: service, settingsReceipts: receipts)
        model.activate(available: true, generation: UUID()); await model.load()
        receipts.settle(receipts.begin(settings: ["library/automatic/tx/favorites"], operation: "保存同步"),
                        failure: .commitUnconfirmed)
        await service.configure(remote: .commitUnconfirmed)
        let completed = expectation(description: "local preference and remote result settled")
        let observation = model.$error.compactMap { $0 }.prefix(1).sink { _ in completed.fulfill() }
        model.select(.qq, favorites: true)
        await fulfillment(of: [completed], timeout: 2); observation.cancel()
        XCTAssertEqual(model.automaticRevision, 1); XCTAssertTrue(model.selection(.qq).favorites)
        XCTAssertTrue(receipts.unknown.isEmpty, "A remote sync unknown is not a local settings unknown")
        XCTAssertEqual(model.error, MusicServiceError.commitUnconfirmed.localizedDescription)
        let calls = await service.observations(); XCTAssertEqual(calls.saves, 1)
    }

    func testAutomaticCommitAndSettingsReceiptsDoNotWaitForImmediateSynchronization() async {
        let service = AutomaticSyncPageService(), gate = SyncPageGate()
        var refreshed = 0
        let libraryChanged = expectation(description: "completed business refreshes the library")
        let model = MusicLibrarySyncViewModel(service: service, settingsReceipts: receipts,
            onLibraryChanged: { refreshed += 1; libraryChanged.fulfill() })
        model.activate(available: true, generation: UUID()); await model.load()
        await service.configure(synchronization: gate)
        let committed = expectation(description: "local automatic preference completed")
        let local = model.$automaticRevision.filter { $0 == 1 }.prefix(1).sink { _ in committed.fulfill() }
        model.select(.qq, favorites: true)
        await gate.started()
        await fulfillment(of: [committed], timeout: 2); local.cancel()
        await receipts.flush()
        XCTAssertFalse(model.busy); XCTAssertTrue(model.selection(.qq).favorites)
        XCTAssertTrue(receipts.unknown.isEmpty); XCTAssertNil(model.error); XCTAssertEqual(refreshed, 0)
        let calls = await service.observations()
        XCTAssertEqual(calls.saves, 1); XCTAssertEqual(calls.rounds, 1)
        let completed = expectation(description: "original immediate synchronization still completes")
        let online = model.$state.filter { $0.phase == .synced }.prefix(1).sink { _ in completed.fulfill() }
        await gate.release()
        await fulfillment(of: [completed, libraryChanged], timeout: 2); online.cancel()
        XCTAssertEqual(refreshed, 1)
        await service.finishSynchronization()
        model.deactivate()
    }

    func testLeavingAutomaticSettingsDoesNotCancelAcceptedBusinessOrPublishItsLateResult() async {
        let service = AutomaticSyncPageService(), gate = SyncPageGate()
        let model = MusicLibrarySyncViewModel(service: service, settingsReceipts: receipts)
        model.activate(available: true, generation: UUID()); await model.load()
        await service.configure(remote: .upstreamFailed, synchronization: gate)
        let committed = expectation(description: "local setting observed")
        let local = model.$automaticRevision.filter { $0 == 1 }.prefix(1).sink { _ in committed.fulfill() }
        model.select(.qq, favorites: true)
        await gate.started(); await fulfillment(of: [committed], timeout: 2); local.cancel()
        model.deactivate()
        let late = expectation(description: "closed page must not receive the online error")
        late.isInverted = true
        let errors = model.$error.compactMap { $0 }.sink { _ in late.fulfill() }
        await gate.release(); await service.finishSynchronization()
        await fulfillment(of: [late], timeout: 0.1); errors.cancel()
        let saved = await service.automaticLibrarySyncPreferences(), calls = await service.observations()
        XCTAssertEqual(saved.revision, 1); XCTAssertEqual(calls.rounds, 1); XCTAssertFalse(calls.cancelled)
        XCTAssertTrue(model.selections.isEmpty); XCTAssertFalse(model.busy); XCTAssertNil(model.error)
    }

    func testReopeningBeforeSubmittedAutomaticSaveSettlesReadsActualFinalPreferencesWithoutRetry() async {
        let service = AutomaticSyncPageService(), gate = SyncPageGate()
        let model = MusicLibrarySyncViewModel(service: service, settingsReceipts: receipts)
        let generation = UUID()
        model.activate(available: true, generation: generation); await model.load()
        await service.configure(failure: .commitUnconfirmed, beforeCommit: gate)
        model.select(.qq, favorites: true); await gate.started()
        model.deactivate()
        model.activate(available: true, generation: generation); await model.load()
        XCTAssertFalse(model.selection(.qq).favorites, "The reopened page reads facts, not the old optimistic selection")
        let reconciled = expectation(description: "reopened page read final saved preferences")
        let observation = model.$automaticRevision.filter { $0 == 1 }.prefix(1).sink { _ in reconciled.fulfill() }
        await gate.release()
        await fulfillment(of: [reconciled], timeout: 2); observation.cancel()
        await receipts.flush()
        XCTAssertTrue(model.selection(.qq).favorites); XCTAssertFalse(model.busy); XCTAssertNil(model.error)
        XCTAssertEqual(receipts.unknown.map(\.setting), ["library/automatic/tx/favorites"])
        let calls = await service.observations()
        XCTAssertEqual(calls.saves, 1); XCTAssertFalse(calls.cancelled)
        model.deactivate()
    }
}
#endif
