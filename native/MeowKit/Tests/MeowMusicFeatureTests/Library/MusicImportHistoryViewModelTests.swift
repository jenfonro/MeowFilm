import Foundation
import XCTest
@testable import MeowMusicFeature

private actor ImportHistoryService: MusicImportHistoryProviding {
    let batch = String(repeating: "a", count: 64)
    var writes = 0
    var hold = false, entered = false
    var entryWaiters: [CheckedContinuation<Void, Never>] = []
    var releaseWaiter: CheckedContinuation<Void, Never>?
    func importHistory() -> MusicImportHistorySnapshot {
        writes == 0 ? .imported(batchID: batch, appliedAt: Date(timeIntervalSince1970: 1_800_000_000), canRollback: true) :
            .rolledBack(batchID: batch)
    }
    func suspend() { hold = true }
    func started() async {
        if !entered { await withCheckedContinuation { entryWaiters.append($0) } }
    }
    func release() { releaseWaiter?.resume(); releaseWaiter = nil }
    func count() -> Int { writes }
    func rollbackImportedLibrary(_ authorization: MusicImportRollbackAuthorization) async throws {
        if hold {
            entered = true; entryWaiters.forEach { $0.resume() }; entryWaiters.removeAll()
            await withCheckedContinuation { releaseWaiter = $0 }
        }
        try authorization.perform { writes += 1 }
    }
}

@MainActor final class MusicImportHistoryViewModelTests: XCTestCase {
    func testExplicitConfirmationRefreshesReceiptAndNotifiesExistingLibraryOnce() async throws {
        let service = ImportHistoryService()
        var refreshes = 0
        let model = MusicImportHistoryViewModel(service: service, onChanged: { refreshes += 1 })
        model.access(available: true, generation: UUID()); model.setForeground(true)
        await model.load()
        let id = try XCTUnwrap(model.requestRollback())
        await model.confirmRollback(UUID())
        let unchanged = await service.count(); XCTAssertEqual(unchanged, 0)
        await model.confirmRollback(id); await model.confirmRollback(id)
        let writes = await service.count(); XCTAssertEqual(writes, 1); XCTAssertEqual(refreshes, 1)
        XCTAssertEqual(model.snapshot, .rolledBack(batchID: String(repeating: "a", count: 64)))
    }

    func testForegroundLossRevokesQueuedActionAndReopeningNeverReplaysIt() async throws {
        let service = ImportHistoryService(), model = MusicImportHistoryViewModel(service: service)
        model.access(available: true, generation: UUID()); model.setForeground(true); await model.load()
        let id = try XCTUnwrap(model.requestRollback())
        await service.suspend()
        let task = Task { await model.confirmRollback(id) }
        await service.started()
        model.setForeground(false); model.setForeground(true)
        await service.release(); await task.value
        let count = await service.count(); XCTAssertEqual(count, 0)
        XCTAssertNil(model.confirmationID); XCTAssertNil(model.error)
        await model.load(); await model.confirmRollback(id)
        let after = await service.count(); XCTAssertEqual(after, 0)
    }

    func testProtectionChangeAndCloseRejectOldConfirmationWithoutRebinding() async throws {
        let service = ImportHistoryService(), model = MusicImportHistoryViewModel(service: service)
        model.access(available: true, generation: UUID()); model.setForeground(true); await model.load()
        let first = try XCTUnwrap(model.requestRollback())
        model.access(available: false, generation: UUID()); model.access(available: true, generation: UUID())
        await model.load(); await model.confirmRollback(first)
        let second = try XCTUnwrap(model.requestRollback())
        model.close(); await model.confirmRollback(second)
        let count = await service.count(); XCTAssertEqual(count, 0); XCTAssertEqual(model.snapshot, .empty)
    }

    func testSamePresentationCanReopenAndReadWithoutReplayingRetiredConfirmation() async throws {
        let service = ImportHistoryService(), model = MusicImportHistoryViewModel(service: service)
        let generation = UUID()
        model.access(available: true, generation: generation); model.setForeground(true); await model.load()
        let old = try XCTUnwrap(model.requestRollback())
        model.deactivate()
        model.access(available: true, generation: generation); model.setForeground(true); await model.load()
        await model.confirmRollback(old)
        let before = await service.count(); XCTAssertEqual(before, 0)
        let current = try XCTUnwrap(model.requestRollback())
        XCTAssertNotEqual(current, old)
        await model.confirmRollback(current)
        let after = await service.count(); XCTAssertEqual(after, 1)
    }
}
