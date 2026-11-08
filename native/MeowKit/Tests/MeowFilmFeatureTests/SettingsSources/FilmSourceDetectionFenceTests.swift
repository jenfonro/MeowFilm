import Foundation
import XCTest
import MeowRuntime
import MeowStorage
@testable import MeowFilmFeature

final class FilmSourceDetectionFenceTests: XCTestCase {
    private let key = FilmSourceKey(installationId: "a", siteKey: "same")
    private let options = FilmVideoOptions(enabled: false, homeVisible: false, searchEnabled: false, aggregateImages: true)

    private func owner(_ fixture: DetectionValueFixture) throws -> (FilmSourceService, DocumentStore, URL) {
        let base = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: base, withIntermediateDirectories: true)
        let store = try DocumentStore(baseURL: base, namespace: FilmSourceService.documentNamespace)
        return (FilmSourceService(transport: fixture.transport, storeFactory: { store }), store, base)
    }

    func testHostGenerationChangeAfterFinalValueIsCapturedCannotPersistOldDetection() async throws {
        let paused = expectation(description: "Final search value")
        let fixture = DetectionValueFixture(boundary: .result, paused: { paused.fulfill() })
        let (service, store, base) = try owner(fixture)
        defer { try? FileManager.default.removeItem(at: base) }
        try await service.refresh()
        try await service.saveVideoOptions(key, options: options)
        let bytes = try store.read(key: FilmSourceService.documentKey)
        let detection = Task { try await service.detect(key) }
        await fulfillment(of: [paused], timeout: 5)
        // The runtime changes independently after capturing the complete value.
        // No source-owner refresh may invalidate the probe before its final fence.
        await fixture.runtime.changeGeneration()
        let status = try await fixture.runtime.request("status", .object([:]))
        XCTAssertEqual(status.catField("instances")?.catArray?.first?.catField("generation"), .number(2))
        let cached = await service.snapshot
        XCTAssertEqual(cached.cats.first?.generation, 1)
        XCTAssertEqual(try store.read(key: FilmSourceService.documentKey), bytes)
        await fixture.release()
        do { _ = try await detection.value; XCTFail("Old-generation detection must not complete") }
        catch { XCTAssertTrue(error is CancellationError, "\(error)") }
        let snapshot = await service.snapshot
        XCTAssertEqual(snapshot.cats.first?.generation, 2)
        XCTAssertNil(snapshot.videos.first { $0.key == key }?.detection)
        XCTAssertEqual(snapshot.videos.first { $0.key == key }?.options, options)
        XCTAssertEqual(try store.read(key: FilmSourceService.documentKey), bytes)
        let completed = await fixture.completedResults
        XCTAssertEqual(completed, 1)
        await service.close()
    }

    func testSameGenerationValuePersistsOnlyAfterHostConfirmation() async throws {
        let fixture = DetectionValueFixture()
        let (service, store, base) = try owner(fixture)
        defer { try? FileManager.default.removeItem(at: base) }
        try await service.refresh()
        try await service.saveVideoOptions(key, options: options)
        let result = try await service.detect(key)
        XCTAssertEqual(result.result, "valid")
        let calls = await fixture.calls
        XCTAssertEqual(Array(calls.suffix(2)), ["invoke", "status"])
        let completed = await fixture.completedResults
        XCTAssertEqual(completed, 1)
        let rebuilt = FilmSourceService(transport: unavailableCatTransport("OFFLINE"), storeFactory: { store })
        try await rebuilt.initialize()
        let snapshot = await rebuilt.snapshot
        XCTAssertEqual(snapshot.videos.first { $0.key == key }?.detection, result)
        XCTAssertEqual(snapshot.videos.first { $0.key == key }?.options, options)
        await rebuilt.close(); await service.close()
    }

    func testFailedCompletionConfirmationDoesNotPublishOrPersistDetection() async throws {
        let paused = expectation(description: "Complete search value")
        let fixture = DetectionValueFixture(boundary: .result, paused: { paused.fulfill() })
        let (service, store, base) = try owner(fixture)
        defer { try? FileManager.default.removeItem(at: base) }
        try await service.refresh()
        try await service.saveVideoOptions(key, options: options)
        let bytes = try store.read(key: FilmSourceService.documentKey)
        let detection = Task { try await service.detect(key) }
        await fulfillment(of: [paused], timeout: 5)
        await fixture.runtime.failNext("status", code: "HOST_CLOSED")
        await fixture.release()
        do { _ = try await detection.value; XCTFail("No authoritative confirmation") }
        catch let error as FilmSourceError { XCTAssertEqual(error.code, "HOST_CLOSED") }
        let snapshot = await service.snapshot
        XCTAssertEqual(snapshot.runtimeError, "HOST_CLOSED")
        XCTAssertNil(snapshot.videos.first { $0.key == key }?.detection)
        XCTAssertEqual(snapshot.videos.first { $0.key == key }?.options, options)
        XCTAssertEqual(try store.read(key: FilmSourceService.documentKey), bytes)
        let completed = await fixture.completedResults
        XCTAssertEqual(completed, 1)
        await service.close()
    }

    func testCancellationDeletionAndCloseDuringConfirmationCannotPublish() async throws {
        for invalidation in ["cancel", "delete", "close"] {
            let paused = expectation(description: "Completion status")
            let cancelled = invalidation == "close" ? expectation(description: "Close cancelled the admitted confirmation") : nil
            let fixture = DetectionValueFixture(boundary: .confirmation, paused: { paused.fulfill() },
                cancelled: { cancelled?.fulfill() })
            let (service, store, base) = try owner(fixture)
            defer { try? FileManager.default.removeItem(at: base) }
            try await service.refresh()
            try await service.saveVideoOptions(key, options: options)
            let detection = Task { try await service.detect(key) }
            await fulfillment(of: [paused], timeout: 5)
            var closing: Task<Void, Never>?
            switch invalidation {
            case "cancel": await service.cancelDetection(key)
            case "delete": try await service.deleteVideo(key)
            default:
                closing = Task { await service.close() }
                await fulfillment(of: [try XCTUnwrap(cancelled)], timeout: 5)
            }
            let bytes = try store.read(key: FilmSourceService.documentKey)
            await fixture.release()
            do { _ = try await detection.value; XCTFail("Invalidated completion: \(invalidation)") }
            catch { XCTAssertTrue(error is CancellationError, "\(error)") }
            await closing?.value
            let snapshot = await service.snapshot
            XCTAssertTrue(snapshot.videos.allSatisfy { $0.detection == nil })
            XCTAssertEqual(try store.read(key: FilmSourceService.documentKey), bytes)
            if invalidation == "delete" { XCTAssertFalse(snapshot.videos.contains { $0.key == key }) }
            else { XCTAssertEqual(snapshot.videos.first { $0.key == key }?.options, options) }
            await service.close()
        }
    }
}

/// Pauses after capturing a complete business value or the final status response.
private actor DetectionValueFixture {
    enum Boundary { case result, confirmation }
    let runtime = SourcesRuntimeFixture()
    private let result: RuntimeValue = .object([
        "list": .array([.object(["vod_id": .string("v1")])]),
        "padding": .string(String(repeating: "x", count: 70_000))
    ])
    private let boundary: Boundary?
    private let paused: @Sendable () -> Void
    private let cancelled: @Sendable () -> Void
    private var pending: CheckedContinuation<Void, Never>?
    private var open = false
    private var didPause = false
    private var resultCreated = false
    var completedResults = 0
    var calls: [String] = []

    init(boundary: Boundary? = nil, paused: @escaping @Sendable () -> Void = {},
         cancelled: @escaping @Sendable () -> Void = {}) {
        self.boundary = boundary; self.paused = paused; self.cancelled = cancelled
    }

    nonisolated var transport: CatScriptTransport {
        CatScriptTransport(request: { operation, params in try await self.request(operation, params) },
            upload: { _, _, _ in throw FilmSourceError(code: "UNEXPECTED_UPLOAD") })
    }

    func release() { open = true; pending?.resume(); pending = nil }

    private func pause(_ point: Boundary) async {
        guard boundary == point, !didPause, !open else { return }
        didPause = true
        await withTaskCancellationHandler {
            await withCheckedContinuation { pending = $0; paused() }
        } onCancel: { [cancelled] in cancelled() }
    }

    private func request(_ operation: String, _ params: RuntimeValue) async throws -> RuntimeValue {
        calls.append(operation)
        let value = try await runtime.request(operation, params)
        if operation == "invoke", params.catField("operation") == .string("search") {
            let captured = result
            resultCreated = true
            await pause(.result)
            completedResults += 1
            return captured
        }
        if operation == "status", resultCreated { await pause(.confirmation) }
        return value
    }
}
