import Foundation
import XCTest
import MeowRuntime
import MeowStorage
import MeowStorageTestSupport
@testable import MeowFilmFeature

final class FilmSourceImportRaceTests: XCTestCase {
    private let key = FilmSourceKey(installationId: "a", siteKey: "same")

    private func fixture(reader: (@Sendable (URL) async throws -> String)? = nil) throws
        -> (FilmSourceService, SourcesRuntimeFixture, SQLiteDatabase, URL) {
        let base = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: base, withIntermediateDirectories: true)
        let store = try storageTestDatabase(self, root: base)
        let runtime = SourcesRuntimeFixture(), settings = try sourceTestSettings(store)
        let service: FilmSourceService
        if let reader {
            service = FilmSourceService(transport: runtime.transport, database: store, settingsDocument: settings,
                runtimeSourceRoot: base.appendingPathComponent("cat"), readSourceFile: reader)
        } else {
            service = FilmSourceService(transport: runtime.transport, database: store, settingsDocument: settings,
                runtimeSourceRoot: base.appendingPathComponent("cat"))
        }
        return (service, runtime, store, base)
    }

    private func assertDeletedAfterRebuild(_ runtime: SourcesRuntimeFixture, store: SQLiteDatabase, base: URL) async throws {
        let rebuilt = FilmSourceService(transport: runtime.transport, database: store,
            settingsDocument: try sourceTestSettings(store), runtimeSourceRoot: base.appendingPathComponent("cat"))
        try await rebuilt.initialize()
        let initial = await rebuilt.snapshot
        XCTAssertFalse(initial.videos.contains { $0.key == key })
        try await rebuilt.refresh()
        let refreshed = await rebuilt.snapshot
        XCTAssertFalse(refreshed.videos.contains { $0.key == key })
        await rebuilt.close()
    }

    func testNativeLocalOrNetworkReplacementDoesNotWaitForRuntimeAndOldStatusCannotRestoreDeletion() async throws {
        for local in [false, true] {
            let (service, runtime, store, base) = try fixture()
                try await service.refresh()
            await runtime.pause("status")
            let refresh = Task { try await service.refresh() }
            await runtime.waitUntilPaused()
            do {
                if local { _ = try await service.editCat("a", name: "Local", sourceType: "local", source: "module.exports = {}") }
                else { _ = try await service.editCat("a", name: "Network", sourceURL: "https://example.invalid/new.js") }
                try await service.deleteVideo(key)
            } catch {
                await runtime.release(); _ = try? await refresh.value; await service.close(); throw error
            }
            await runtime.release()
            try await refresh.value
            let stale = await service.snapshot
            XCTAssertEqual(stale.cats.first?.inputRevision, 2)
            XCTAssertEqual(stale.cats.first?.generation, 0)
            XCTAssertEqual(stale.cats.first?.sourceType, local ? "local" : "network")
            XCTAssertFalse(stale.videos.contains { $0.key == key })
            try await runtime.acceptNativeSettings(sourceTestSettings(store))
            try await service.refresh()
            let current = await service.snapshot
            XCTAssertTrue(try XCTUnwrap(current.cats.first).canDetect)
            XCTAssertFalse(current.videos.contains { $0.key == key })
            await service.close()
            try await assertDeletedAfterRebuild(runtime, store: store, base: base)
        }
    }

    func testDeletionDuringLocalFilePreparationKeepsAllOwnAndOtherParentTombstones() async throws {
        let gate = ImportFileReadGate()
        let (service, runtime, store, base) = try fixture(reader: { _ in await gate.read() })
        var cat = try parseFilmCat(SourcesRuntimeFixture.cat("a"))
        cat.sites.append(.init(key: "old", name: "Old", api: "/old", actions: [], methods: [:]))
        await runtime.setCat(try RuntimeJSON.decode(JSONEncoder().encode(cat)))
        try await service.refresh()
        let old = FilmSourceKey(installationId: "a", siteKey: "old")
        let other = FilmSourceKey(installationId: "b", siteKey: "same")
        try await service.deleteVideo(old)
        try await service.deleteVideo(other)
        let importing = Task {
            try await service.editCat("a", name: "A", sourceType: "local", fileURL: base.appendingPathComponent("source.js"))
        }
        await gate.waitUntilReading()
        do { try await service.deleteVideo(key) }
        catch { await gate.release(); _ = try? await importing.value; await service.close(); throw error }
        await gate.release()
        _ = try await importing.value
        try await runtime.acceptNativeSettings(sourceTestSettings(store))
        try await service.refresh()
        let after = await service.snapshot
        XCTAssertFalse(after.videos.contains { $0.key == key })
        XCTAssertFalse(after.videos.contains { $0.key == old })
        XCTAssertFalse(after.videos.contains { $0.key == other })
        XCTAssertEqual(after.cats.first?.sites.count, 2, "The actual directory survives even though its sites were deleted from video sources")
        await service.close()
        try await assertDeletedAfterRebuild(runtime, store: store, base: base)
    }

    func testEarlierFileEditConflictsWithLaterSourceSaveAndCannotUndoNewDeletion() async throws {
        let gate = ImportFileReadGate()
        let (service, runtime, store, base) = try fixture(reader: { _ in await gate.read() })
        try await service.refresh()
        let earlier = Task {
            try await service.editCat("a", name: "Earlier", sourceType: "local", fileURL: base.appendingPathComponent("source.js"))
        }
        await gate.waitUntilReading()
        do {
            _ = try await service.editCat("a", name: "Later", sourceURL: "https://example.invalid/later.js")
            try await service.deleteVideo(key)
        } catch { await gate.release(); _ = try? await earlier.value; await service.close(); throw error }
        let document = try sourceTestSettings(store), before = try document.readForTest()
        await gate.release()
        do { _ = try await earlier.value; XCTFail("Old file edit must not overwrite the later intent") }
        catch let error as FilmSourceError { XCTAssertEqual(error.code, "STORAGE_CONFLICT") }
        XCTAssertEqual(try document.readForTest(), before)
        let after = await service.snapshot
        XCTAssertEqual(after.cats.first?.name, "Later")
        XCTAssertEqual(after.cats.first?.sourceUrl, "https://example.invalid/later.js")
        XCTAssertEqual(after.cats.first?.inputRevision, 2)
        XCTAssertFalse(after.videos.contains { $0.key == key })
        await service.close()
        try await assertDeletedAfterRebuild(runtime, store: store, base: base)
    }

    func testCancellationShutdownAndCloseDrainFileReadAndRejectLateBytesWithoutCommit() async throws {
        for boundary in ["cancel", "shutdown", "close"] {
            let gate = ImportFileReadGate()
            let (service, runtime, store, base) = try fixture(reader: { _ in await gate.read() })
                try await service.refresh()
            let document = try sourceTestSettings(store), before = try document.readForTest()
            let snapshot = await service.snapshot, cache = try sourceTestRows(store)
            let importing = Task {
                try await service.importLocal(name: "New", fileURL: base.appendingPathComponent("source.js"), installationId: "new")
            }
            await gate.waitUntilReading()
            var draining: Task<Void, Never>?
            if boundary == "cancel" { importing.cancel() }
            else if boundary == "shutdown" { draining = Task { await service.prepareShutdown() } }
            else { draining = Task { await service.close() } }
            await gate.waitUntilCancelled()
            let drained = expectation(description: "File read is still admitted")
            drained.isInverted = true
            let observing = Task { if let draining { await draining.value; drained.fulfill() } }
            await fulfillment(of: [drained], timeout: 0.02)
            await gate.release()
            do { _ = try await importing.value; XCTFail("Cancelled file input cannot publish") }
            catch { XCTAssertTrue(error is CancellationError, "\(error)") }
            await draining?.value
            await observing.value
            XCTAssertEqual(try document.readForTest(), before)
            XCTAssertEqual(try sourceTestRows(store), cache)
            let after = await service.snapshot, calls = await runtime.calls
            XCTAssertEqual(after, snapshot)
            XCTAssertEqual(calls, ["status"])
            XCTAssertFalse(FileManager.default.fileExists(atPath: base.appendingPathComponent("cat/sources").path))
            if boundary == "shutdown" { try await service.resume() }
            if boundary != "close" {
                _ = try await service.editCat("a", name: "After cancellation")
                let updated = await service.snapshot
                XCTAssertEqual(updated.cats.first?.name, "After cancellation")
            } else {
                do { try await service.initialize(); XCTFail("Closed owner is terminal") }
                catch let error as FilmSourceError { XCTAssertEqual(error.code, "CLOSED") }
            }
            await service.close()
        }
    }
}

private actor ImportFileReadGate {
    private var entered = false
    private var waiters: [CheckedContinuation<Void, Never>] = []
    private var pending: CheckedContinuation<String, Never>?
    private var cancelled = false
    private var cancelledWaiters: [CheckedContinuation<Void, Never>] = []

    func read() async -> String {
        entered = true
        return await withTaskCancellationHandler {
            await withCheckedContinuation { continuation in
                pending = continuation
                let ready = waiters; waiters.removeAll(); ready.forEach { $0.resume() }
            }
        } onCancel: { Task { await self.recordCancellation() } }
    }
    private func recordCancellation() {
        cancelled = true
        let ready = cancelledWaiters; cancelledWaiters.removeAll(); ready.forEach { $0.resume() }
    }
    func waitUntilCancelled() async {
        if cancelled { return }
        await withCheckedContinuation { cancelledWaiters.append($0) }
    }
    func waitUntilReading() async {
        if entered { return }
        await withCheckedContinuation { waiters.append($0) }
    }
    func release() { pending?.resume(returning: "module.exports = {}"); pending = nil }
}
