import Foundation
import XCTest
import MeowJSON
import MeowStorage
@testable import MeowFilmFeature

final class FilmSourceDetectionFenceTests: XCTestCase {
    private let key = FilmSourceKey(installationId: "a", siteKey: "same")
    private let options = FilmVideoOptions(enabled: false, homeVisible: false, searchEnabled: false, aggregateImages: true)

    private func owner(_ fixture: DetectionBlobFixture) throws -> (FilmSourceService, DocumentStore, URL) {
        let base = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: base, withIntermediateDirectories: true)
        let store = try DocumentStore(baseURL: base, namespace: FilmSourceService.documentNamespace)
        return (FilmSourceService(transport: .init(rpc: { method, params, _ in
            try await fixture.rpc(method, params)
        }), storeFactory: { store }), store, base)
    }

    func testHostGenerationChangeDuringFinalBlobTransferCannotPersistOldDetection() async throws {
        for boundary in [DetectionBlobFixture.Boundary.lastChunk, .release] {
            let paused = expectation(description: "Final result \(boundary)")
            let fixture = try DetectionBlobFixture(boundary: boundary, paused: { paused.fulfill() })
            let (service, store, base) = try owner(fixture)
            defer { try? FileManager.default.removeItem(at: base) }
            try await service.refresh()
            try await service.saveVideoOptions(key, options: options)
            let bytes = try store.read(key: FilmSourceService.documentKey)
            let detection = Task { try await service.detect(key) }
            await fulfillment(of: [paused], timeout: 5)
            // The host changes independently. Do not refresh the source owner:
            // doing so would invalidate the probe before the completion fence.
            await fixture.runtime.changeGeneration()
            let raw = try await fixture.runtime.rpc("cat.script.status", .object([:]))
            let status = try CatScriptTransport.parse(Data(raw.catField("data")!.catText!.utf8))
            XCTAssertEqual(status.catField("instances")?.catArray?.first?.catField("generation"), .number(2))
            let cached = await service.snapshot
            XCTAssertEqual(cached.cats.first?.generation, 1)
            await fixture.release()
            do { _ = try await detection.value; XCTFail("Old-generation detection must not complete") }
            catch { XCTAssertTrue(error is CancellationError, "\(error)") }
            let snapshot = await service.snapshot
            XCTAssertEqual(snapshot.cats.first?.generation, 2)
            XCTAssertNil(snapshot.videos.first { $0.key == key }?.detection)
            XCTAssertEqual(snapshot.videos.first { $0.key == key }?.options, options)
            XCTAssertEqual(try store.read(key: FilmSourceService.documentKey), bytes)
            let releases = await fixture.releases
            XCTAssertEqual(releases, 1)
            await service.close()
        }
    }

    func testSameGenerationBlobPersistsOnlyAfterHostConfirmation() async throws {
        let fixture = try DetectionBlobFixture()
        let (service, store, base) = try owner(fixture)
        defer { try? FileManager.default.removeItem(at: base) }
        try await service.refresh()
        try await service.saveVideoOptions(key, options: options)
        let result = try await service.detect(key)
        XCTAssertEqual(result.result, "valid")
        let calls = await fixture.calls
        XCTAssertEqual(Array(calls.suffix(2)), ["blob.release", "status"])
        let rebuilt = FilmSourceService(transport: .init(rpc: { _, _, _ in throw FilmSourceError(code: "OFFLINE") }),
            storeFactory: { store })
        try await rebuilt.initialize()
        let snapshot = await rebuilt.snapshot
        XCTAssertEqual(snapshot.videos.first { $0.key == key }?.detection, result)
        XCTAssertEqual(snapshot.videos.first { $0.key == key }?.options, options)
        await rebuilt.close(); await service.close()
    }

    func testFailedCompletionConfirmationDoesNotPublishOrPersistDetection() async throws {
        let paused = expectation(description: "Result release")
        let fixture = try DetectionBlobFixture(boundary: .release, paused: { paused.fulfill() })
        let (service, store, base) = try owner(fixture)
        defer { try? FileManager.default.removeItem(at: base) }
        try await service.refresh()
        try await service.saveVideoOptions(key, options: options)
        let bytes = try store.read(key: FilmSourceService.documentKey)
        let detection = Task { try await service.detect(key) }
        await fulfillment(of: [paused], timeout: 5)
        await fixture.runtime.failNext("status", code: "HOST_UNAVAILABLE")
        await fixture.release()
        do { _ = try await detection.value; XCTFail("No authoritative confirmation") }
        catch let error as FilmSourceError { XCTAssertEqual(error.code, "HOST_UNAVAILABLE") }
        let snapshot = await service.snapshot
        XCTAssertEqual(snapshot.runtimeError, "HOST_UNAVAILABLE")
        XCTAssertNil(snapshot.videos.first { $0.key == key }?.detection)
        XCTAssertEqual(snapshot.videos.first { $0.key == key }?.options, options)
        XCTAssertEqual(try store.read(key: FilmSourceService.documentKey), bytes)
        let releases = await fixture.releases
        XCTAssertEqual(releases, 1)
        await service.close()
    }

    func testCancellationDeletionAndCloseDuringConfirmationCannotPublish() async throws {
        for invalidation in ["cancel", "delete", "close"] {
            let paused = expectation(description: "Completion status")
            let cancelled = invalidation == "close" ? expectation(description: "Close cancelled the admitted confirmation") : nil
            let fixture = try DetectionBlobFixture(boundary: .confirmation, paused: { paused.fulfill() },
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

/// Models the existing Cat protocol: result tokens are module-owned and remain
/// readable across an installation's generation change.
private actor DetectionBlobFixture {
    enum Boundary { case lastChunk, release, confirmation }
    let runtime = SourcesRuntimeFixture()
    private let bytes: Data
    private let boundary: Boundary?
    private let paused: @Sendable () -> Void
    private let cancelled: @Sendable () -> Void
    private var pending: CheckedContinuation<Void, Never>?
    private var open = false
    private var didPause = false
    private var resultCreated = false
    private var released = false
    var releases = 0
    var calls: [String] = []

    init(boundary: Boundary? = nil, paused: @escaping @Sendable () -> Void = {},
         cancelled: @escaping @Sendable () -> Void = {}) throws {
        self.boundary = boundary; self.paused = paused; self.cancelled = cancelled
        bytes = try JSONEncoder().encode(JSONValue.object([
            "list": .array([.object(["vod_id": .string("v1")])]),
            "padding": .string(String(repeating: "x", count: 70_000)),
        ]))
    }

    func release() { open = true; pending?.resume(); pending = nil }

    private func pause(_ point: Boundary) async {
        guard boundary == point, !didPause, !open else { return }
        didPause = true
        await withTaskCancellationHandler {
            await withCheckedContinuation { pending = $0; paused() }
        } onCancel: { [cancelled] in cancelled() }
    }

    func rpc(_ method: String, _ params: JSONValue) async throws -> JSONValue {
        let operation = String(method.dropFirst("cat.script.".count))
        calls.append(operation)
        var fields: [String: JSONValue] = ["ok": .bool(true), "contract": .string("cat.script.v1")]
        switch operation {
        case "invoke" where params.catField("operation") == .string("search"):
            _ = try await runtime.rpc(method, params)
            resultCreated = true
            fields["blob"] = .object(["token": .string("result"), "length": .number(Double(bytes.count)),
                "sha256": .string(CatScriptTransport.hash(bytes)), "chunkBytes": .number(65_536)])
        case "blob.read":
            guard resultCreated, !released, params.catField("token") == .string("result"),
                  let offset = params.catField("offset")?.catInteger else { throw FilmSourceError(code: "BAD_TOKEN") }
            let end = min(offset + 65_536, bytes.count)
            if end == bytes.count { await pause(.lastChunk) }
            fields["data"] = .string(bytes.subdata(in: offset..<end).base64EncodedString())
            fields["next"] = .number(Double(end))
        case "blob.release":
            guard resultCreated, !released, params.catField("token") == .string("result") else {
                throw FilmSourceError(code: "BAD_TOKEN")
            }
            await pause(.release)
            released = true; releases += 1
            fields["released"] = .bool(true)
        default:
            if operation == "status", resultCreated { await pause(.confirmation) }
            return try await runtime.rpc(method, params)
        }
        return .object(fields)
    }
}
