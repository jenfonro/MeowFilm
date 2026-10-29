#if os(iOS) || os(macOS)
import Foundation
import XCTest
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
@MainActor final class MusicLibrarySyncViewModelTests: XCTestCase {
    func testLoadNeverWritesDefaultsAreOffAndOnlyExplicitSelectedRequestWrites() async {
        let service = SyncPageService(), model = MusicLibrarySyncViewModel(service: SyncPageService())
        model.activate(available: true, generation: UUID()); await model.load()
        XCTAssertFalse(model.canSync); XCTAssertTrue(model.selections.isEmpty)
        await model.synchronize()
        let page = MusicLibrarySyncViewModel(service: service, accounts: service)
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
                let model = MusicLibrarySyncViewModel(service: service, accounts: service)
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
        let service = SyncPageService(), gate = SyncPageGate(), model = MusicLibrarySyncViewModel(service: service)
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
            let service = SyncPageService(), model = MusicLibrarySyncViewModel(service: service)
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
            let service = SyncPageService(), model = MusicLibrarySyncViewModel(service: service)
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
        let service = SyncPageService(), model = MusicLibrarySyncViewModel(service: service)
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
}
#endif
