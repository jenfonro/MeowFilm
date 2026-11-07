import Foundation
import XCTest
import MeowJSON
import MeowStorage
@testable import MeowFilmFeature

final class FilmSourceImportRaceTests: XCTestCase {
    private let key = FilmSourceKey(installationId: "a", siteKey: "same")

    private func fixture(reader: (@Sendable (URL) async throws -> String)? = nil) throws
        -> (FilmSourceService, SourcesRuntimeFixture, DocumentStore, URL) {
        let base = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: base, withIntermediateDirectories: true)
        let store = try DocumentStore(baseURL: base, namespace: FilmSourceService.documentNamespace)
        let runtime = SourcesRuntimeFixture()
        let transport = CatScriptTransport(rpc: { method, params, _ in try await runtime.rpc(method, params) })
        let service: FilmSourceService
        if let reader { service = FilmSourceService(transport: transport, storeFactory: { store }, readSourceFile: reader) }
        else { service = FilmSourceService(transport: transport, storeFactory: { store }) }
        return (service, runtime, store, base)
    }

    private func waitForQueuedImport(_ service: FilmSourceService) async throws {
        let deadline = ContinuousClock.now.advanced(by: .seconds(5))
        while await service.queuedManagementCount == 0 {
            if ContinuousClock.now >= deadline { throw FilmSourceError(code: "TEST_IMPORT_DID_NOT_QUEUE") }
            try await Task.sleep(nanoseconds: 1_000_000)
        }
    }

    private func assertDeletedAfterRebuild(_ runtime: SourcesRuntimeFixture, store: DocumentStore) async throws {
        let rebuilt = FilmSourceService(transport: .init(rpc: { method, params, _ in try await runtime.rpc(method, params) }),
            storeFactory: { store })
        try await rebuilt.initialize()
        let initial = await rebuilt.snapshot
        XCTAssertFalse(initial.videos.contains { $0.key == key })
        try await rebuilt.refresh()
        let refreshed = await rebuilt.snapshot
        XCTAssertFalse(refreshed.videos.contains { $0.key == key })
        await rebuilt.close()
    }

    func testNewDeletionWhileNetworkOrStringImportWaitsForManagementSurvivesRebuild() async throws {
        for local in [false, true] {
            let (service, runtime, store, base) = try fixture()
            defer { try? FileManager.default.removeItem(at: base) }
            try await service.refresh()
            await runtime.pause("status")
            let refresh = Task { try await service.refresh() }
            await runtime.waitUntilPaused()
            let importing = Task {
                if local { return try await service.importLocal(name: "A", source: "module.exports = {}", installationId: "a") }
                return try await service.importNetwork(name: "A", sourceURL: "https://example.invalid/a.js", installationId: "a")
            }
            do {
                try await waitForQueuedImport(service)
                try await service.deleteVideo(key)
            } catch {
                importing.cancel(); await runtime.release(); _ = try? await refresh.value; _ = try? await importing.value
                await service.close(); throw error
            }
            await runtime.release()
            try await refresh.value
            _ = try await importing.value
            let after = await service.snapshot
            XCTAssertFalse(after.videos.contains { $0.key == key })
            await service.close()
            try await assertDeletedAfterRebuild(runtime, store: store)
        }
    }

    func testDeletionDuringLocalFileReadWinsButPreviouslyDeletedSiteCanRestore() async throws {
        let gate = ImportFileReadGate()
        let (service, runtime, store, base) = try fixture(reader: { _ in await gate.read() })
        defer { try? FileManager.default.removeItem(at: base) }
        try await service.refresh()
        let oldDeletion = FilmSourceKey(installationId: "b", siteKey: "same")
        // Same parent: one deletion precedes admission, the other happens while reading.
        var cat = try JSONDecoder().decode(FilmCatSource.self, from: JSONEncoder().encode(SourcesRuntimeFixture.cat("a")))
        cat.sites.append(.init(key: "old", name: "Old", api: "/old", actions: [], methods: [:]))
        await runtime.setCat(try JSONDecoder().decode(JSONValue.self, from: JSONEncoder().encode(cat)))
        try await service.refresh()
        let old = FilmSourceKey(installationId: "a", siteKey: "old")
        try await service.deleteVideo(old)
        try await service.deleteVideo(oldDeletion)
        let importing = Task { try await service.importLocal(name: "A", fileURL: base.appendingPathComponent("source.js"), installationId: "a") }
        await gate.waitUntilReading()
        do { try await service.deleteVideo(key) }
        catch { await gate.release(); _ = try? await importing.value; await service.close(); throw error }
        await gate.release()
        _ = try await importing.value
        let after = await service.snapshot
        XCTAssertFalse(after.videos.contains { $0.key == key })
        XCTAssertTrue(after.videos.contains { $0.key == old })
        XCTAssertFalse(after.videos.contains { $0.key == oldDeletion }, "Other parent's tombstone must remain")
        await service.close()
        try await assertDeletedAfterRebuild(runtime, store: store)
    }

    func testEarlierFileImportCannotRestoreSameKeyAfterAnotherImportRestoresAndUserDeletesAgain() async throws {
        let gate = ImportFileReadGate()
        let (service, runtime, store, base) = try fixture(reader: { _ in await gate.read() })
        defer { try? FileManager.default.removeItem(at: base) }
        try await service.refresh()
        try await service.deleteVideo(key)
        let earlier = Task { try await service.importLocal(name: "Earlier", fileURL: base.appendingPathComponent("source.js"), installationId: "a") }
        await gate.waitUntilReading()
        do {
            _ = try await service.importNetwork(name: "Later", sourceURL: "https://example.invalid/a.js", installationId: "a")
            let restored = await service.snapshot
            XCTAssertTrue(restored.videos.contains { $0.key == key })
            try await service.deleteVideo(key)
        } catch { await gate.release(); _ = try? await earlier.value; await service.close(); throw error }
        await gate.release()
        _ = try await earlier.value
        let after = await service.snapshot
        XCTAssertFalse(after.videos.contains { $0.key == key }, "Old receipt cannot match a new deletion of the same key")
        await service.close()
        try await assertDeletedAfterRebuild(runtime, store: store)
    }
}

private actor ImportFileReadGate {
    private var entered = false
    private var waiters: [CheckedContinuation<Void, Never>] = []
    private var pending: CheckedContinuation<String, Never>?

    func read() async -> String {
        entered = true
        return await withCheckedContinuation { continuation in
            pending = continuation
            let ready = waiters; waiters.removeAll(); ready.forEach { $0.resume() }
        }
    }
    func waitUntilReading() async {
        if entered { return }
        await withCheckedContinuation { waiters.append($0) }
    }
    func release() { pending?.resume(returning: "module.exports = {}"); pending = nil }
}
