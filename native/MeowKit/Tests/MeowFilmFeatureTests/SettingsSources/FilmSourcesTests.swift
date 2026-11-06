import Foundation
import XCTest
import MeowJSON
import MeowStorage
@testable import MeowFilmFeature

final class FilmSourcesTests: XCTestCase {
    private func makeOwner(_ runtime: SourcesRuntimeFixture) throws -> (FilmSourceService, URL, DocumentStore) {
        let base = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: base, withIntermediateDirectories: true)
        let store = try DocumentStore(baseURL: base, namespace: FilmSourceService.documentNamespace)
        let owner = FilmSourceService(transport: .init(rpc: { method, params, _ in
            try await runtime.rpc(method, params)
        }), storeFactory: { store })
        return (owner, base, store)
    }

    func testLocalInitializationDoesNotStartNodeAndStartupRestoresWithoutProbing() async throws {
        let runtime = SourcesRuntimeFixture()
        let (owner, base, _) = try makeOwner(runtime)
        defer { try? FileManager.default.removeItem(at: base) }
        try await owner.initialize()
        let before = await runtime.calls
        XCTAssertTrue(before.isEmpty)
        try await owner.start()
        let calls = await runtime.calls
        XCTAssertEqual(calls, ["start", "status"])
        let snapshot = await owner.snapshot
        XCTAssertEqual(snapshot.cats.count, 2)
        XCTAssertEqual(snapshot.videos.count, 2)
        XCTAssertTrue(snapshot.videos.allSatisfy { $0.detection == nil })
        await owner.close()
    }

    func testRepeatedInitializePreservesLiveSnapshotAndStartCanRepeat() async throws {
        let runtime = SourcesRuntimeFixture()
        let (owner, base, _) = try makeOwner(runtime)
        defer { try? FileManager.default.removeItem(at: base) }
        try await owner.start()
        let before = await owner.snapshot
        try await owner.initialize()
        let initialized = await owner.snapshot
        XCTAssertEqual(initialized, before)
        try await owner.start()
        let restarted = await owner.snapshot
        XCTAssertEqual(restarted, before)
        await owner.close()
    }

    func testRuntimeFailuresPublishObservableErrorForStartAndRefresh() async throws {
        for starting in [true, false] {
            let base = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
            try FileManager.default.createDirectory(at: base, withIntermediateDirectories: true)
            defer { try? FileManager.default.removeItem(at: base) }
            let store = try DocumentStore(baseURL: base, namespace: FilmSourceService.documentNamespace)
            let owner = FilmSourceService(transport: .init(rpc: { _, _, _ in throw FilmSourceError(code: "HOST_UNAVAILABLE") }),
                storeFactory: { store })
            let stream = await owner.snapshots()
            var iterator = stream.makeAsyncIterator()
            let initial = await iterator.next()
            XCTAssertNil(initial?.runtimeError)
            do {
                if starting { try await owner.start() } else { try await owner.refresh() }
                XCTFail("Runtime failure must be visible to caller")
            } catch let error as FilmSourceError { XCTAssertEqual(error.code, "HOST_UNAVAILABLE") }
            let failed = await iterator.next()
            XCTAssertEqual(failed?.runtimeError, "HOST_UNAVAILABLE")
            await owner.close()
        }
    }

    func testInvalidLocalFileNeverReachesRuntime() async throws {
        let runtime = SourcesRuntimeFixture()
        let (owner, base, _) = try makeOwner(runtime)
        defer { try? FileManager.default.removeItem(at: base) }
        let file = base.appendingPathComponent("source.js")
        try Data([0xff, 0xfe, 0x80]).write(to: file)
        do { _ = try await owner.importLocal(name: "fixture", fileURL: file); XCTFail("Invalid UTF-8") }
        catch let error as FilmSourceError { XCTAssertEqual(error.code, "INVALID_SOURCE") }
        let calls = await runtime.calls
        XCTAssertTrue(calls.isEmpty)
        await owner.close()
    }

    func testOptionsOrderingTombstonesAndExplicitReimportAreLocalAndDurable() async throws {
        let runtime = SourcesRuntimeFixture()
        let (owner, base, store) = try makeOwner(runtime)
        defer { try? FileManager.default.removeItem(at: base) }
        try await owner.refresh()
        let a = FilmSourceKey(installationId: "a", siteKey: "same")
        let b = FilmSourceKey(installationId: "b", siteKey: "same")
        try await owner.saveVideoOptions(a, options: .init(aggregateImages: true), order: 8)
        try await owner.saveVideoOptions(b, options: .init(enabled: false, homeVisible: false, searchEnabled: false, aggregateImages: true), order: -1)
        var snapshot = await owner.snapshot
        XCTAssertEqual(snapshot.videos.first?.key, b)
        XCTAssertFalse(try XCTUnwrap(snapshot.videos.first { $0.key == a }).options.aggregateImages)
        try await owner.deleteVideo(a)
        try await owner.refresh()
        _ = try await owner.restartCat("a")
        snapshot = await owner.snapshot
        XCTAssertEqual(snapshot.videos.map(\.key), [b])
        let rebuilt = FilmSourceService(transport: .init(rpc: { method, params, _ in try await runtime.rpc(method, params) }),
            storeFactory: { store })
        try await rebuilt.initialize()
        let restored = await rebuilt.snapshot
        XCTAssertEqual(restored.videos, snapshot.videos)
        XCTAssertEqual(restored.cats.map(\.generation), [0, 0])
        try await rebuilt.refresh()
        _ = try await rebuilt.importNetwork(name: "A", sourceURL: "https://example.invalid/a.js", installationId: "a")
        let imported = await rebuilt.snapshot
        XCTAssertEqual(Set(imported.videos.map(\.key)), Set([a, b]))
        XCTAssertEqual(imported.videos.first { $0.key == b }?.options,
            .init(enabled: false, homeVisible: false, searchEnabled: false, aggregateImages: true))
        await owner.close(); await rebuilt.close()
    }

    func testFailedAndStartingReimportsDoNotRestoreDeletedVideos() async throws {
        let runtime = SourcesRuntimeFixture()
        let (owner, base, _) = try makeOwner(runtime)
        defer { try? FileManager.default.removeItem(at: base) }
        try await owner.refresh()
        let key = FilmSourceKey(installationId: "a", siteKey: "same")
        try await owner.deleteVideo(key)
        for state in ["failed", "starting"] {
            await runtime.setState(state, id: "a")
            do {
                _ = try await owner.importNetwork(name: "A", sourceURL: "https://example.invalid/a.js", installationId: "a")
                XCTFail("A non-ready import cannot restore tombstones")
            } catch let error as FilmSourceError { XCTAssertEqual(error.code, "IMPORT_FAILED") }
            let snapshot = await owner.snapshot
            XCTAssertFalse(snapshot.videos.contains { $0.key == key })
            XCTAssertEqual(snapshot.cats.first { $0.installationId == "a" }?.sites.count, 1)
        }
        await owner.close()
    }

    func testReimportRestoresOnlyReadyReceiptSitesForItsOwnParent() async throws {
        let runtime = SourcesRuntimeFixture()
        let (owner, base, _) = try makeOwner(runtime)
        defer { try? FileManager.default.removeItem(at: base) }
        let a = FilmSourceKey(installationId: "a", siteKey: "same")
        let b = FilmSourceKey(installationId: "b", siteKey: "same")
        let other = FilmSourceKey(installationId: "a", siteKey: "other")
        guard case .object(var full) = SourcesRuntimeFixture.cat("a"),
              case .object(var extra) = full["sites"]!.catArray![0] else { return XCTFail("Fixture") }
        extra["key"] = .string("other")
        full["sites"] = .array(full["sites"]!.catArray! + [.object(extra)])
        await runtime.setCat(.object(full))
        try await owner.refresh()
        try await owner.deleteVideo(a)
        try await owner.deleteVideo(other)
        try await owner.deleteVideo(b)
        await runtime.setCat(SourcesRuntimeFixture.cat("a"))
        _ = try await owner.importNetwork(name: "A", sourceURL: "https://example.invalid/a.js", installationId: "a")
        var snapshot = await owner.snapshot
        XCTAssertEqual(snapshot.videos.map(\.key), [a])
        await runtime.setCat(.object(full))
        try await owner.refresh()
        snapshot = await owner.snapshot
        XCTAssertEqual(snapshot.videos.map(\.key), [a], "Absent imported site remains deleted on later ordinary sync")
        await owner.close()
    }

    func testFailedUpdateKeepsOldVersionAndReportedModificationTime() async throws {
        let runtime = SourcesRuntimeFixture()
        let (owner, base, _) = try makeOwner(runtime)
        defer { try? FileManager.default.removeItem(at: base) }
        try await owner.refresh()
        let before = await owner.snapshot
        guard case .object(var failedCat) = SourcesRuntimeFixture.cat("a") else { return XCTFail("Fixture") }
        failedCat["updateState"] = .string("failed")
        failedCat["updateError"] = .string("UPDATE_FAILED")
        await runtime.setCat(.object(failedCat))
        await runtime.failNext("update", code: "UPDATE_FAILED")
        do { _ = try await owner.updateCat("a"); XCTFail("Failed update") }
        catch let error as FilmSourceError { XCTAssertEqual(error.code, "UPDATE_FAILED") }
        let after = await owner.snapshot
        let cat = try XCTUnwrap(after.cats.first { $0.installationId == "a" })
        XCTAssertEqual(cat.updateState, "failed")
        XCTAssertEqual(cat.updateError, "UPDATE_FAILED")
        XCTAssertEqual(cat.sourceHash, before.cats.first?.sourceHash)
        XCTAssertEqual(cat.modifiedAt, before.cats.first?.modifiedAt)
        XCTAssertEqual(after.videos, before.videos)
        XCTAssertTrue(cat.canDetect)
        _ = try await owner.restartCat("a")
        let restarted = await owner.snapshot
        XCTAssertEqual(restarted.cats.first?.modifiedAt, cat.modifiedAt)
        await owner.close()
    }

    func testNewDeletionWinsAgainstInFlightReimport() async throws {
        let runtime = SourcesRuntimeFixture()
        let (owner, base, _) = try makeOwner(runtime)
        defer { try? FileManager.default.removeItem(at: base) }
        try await owner.refresh()
        let key = FilmSourceKey(installationId: "a", siteKey: "same")
        await runtime.pause("import")
        let importing = Task { try await owner.importNetwork(name: "A", sourceURL: "https://example.invalid/a.js", installationId: "a") }
        await runtime.waitUntilPaused()
        try await owner.deleteVideo(key)
        await runtime.release()
        _ = try await importing.value
        let snapshot = await owner.snapshot
        XCTAssertFalse(snapshot.videos.contains { $0.key == key })
        await owner.close()
    }

    func testStorageConflictDoesNotPublishOptionsAndReloadsActualDocument() async throws {
        let runtime = SourcesRuntimeFixture()
        let (owner, base, store) = try makeOwner(runtime)
        defer { try? FileManager.default.removeItem(at: base) }
        try await owner.refresh()
        let before = await owner.snapshot
        let saved = try XCTUnwrap(store.read(key: FilmSourceService.documentKey))
        var external = saved; external.append(0x20)
        try store.write(external, key: FilmSourceService.documentKey)
        do {
            try await owner.saveVideoOptions(.init(installationId: "a", siteKey: "same"), options: .init(enabled: false))
            XCTFail("Conflicting save must fail")
        } catch { XCTAssertEqual(error as? DocumentStoreError, .revisionConflict) }
        let failed = await owner.snapshot
        XCTAssertEqual(failed, before)
        try await owner.initialize()
        let reloaded = await owner.snapshot
        XCTAssertEqual(reloaded.videos, before.videos)
        await owner.close()
    }

    func testFiveStageProbeUsesReferencePayloadAndPersistsOnlyDetection() async throws {
        let runtime = SourcesRuntimeFixture()
        let (owner, base, store) = try makeOwner(runtime)
        defer { try? FileManager.default.removeItem(at: base) }
        try await owner.refresh()
        let key = FilmSourceKey(installationId: "a", siteKey: "same")
        let options = FilmVideoOptions(enabled: false, homeVisible: false, searchEnabled: false, aggregateImages: true)
        try await owner.saveVideoOptions(key, options: options)
        let result = try await owner.detect(key)
        XCTAssertEqual(result.result, "valid")
        XCTAssertTrue(result.playbackResolved)
        XCTAssertFalse(result.mediaPlaybackVerified)
        XCTAssertEqual(result.steps.map(\.operation), ["home", "category", "detail", "play", "search"])
        let invocations = await runtime.invocations
        XCTAssertEqual(invocations.map(\.0), ["home", "category", "detail", "play", "search"])
        XCTAssertEqual(invocations[1].1, .object(["id": .string("tv"), "page": .number(1), "filter": .bool(true), "filters": .object([:])]))
        XCTAssertEqual(invocations[2].1, .object(["id": .string("v1"), "vod_id": .string("v1")]))
        XCTAssertEqual(invocations[3].1, .object(["flag": .string("线路"), "id": .string("episode"), "siteApi": .string("/api/a")]))
        XCTAssertEqual(invocations[4].1, .object(["wd": .string("斗破"), "page": .number(1)]))
        let rebuilt = FilmSourceService(transport: .init(rpc: { _, _, _ in throw FilmSourceError(code: "OFFLINE") }), storeFactory: { store })
        try await rebuilt.initialize()
        let snapshot = await rebuilt.snapshot
        XCTAssertEqual(snapshot.videos.first { $0.key == key }?.options, options)
        XCTAssertEqual(snapshot.videos.first { $0.key == key }?.detection, result)
        await owner.close(); await rebuilt.close()
    }

    func testLateProbeCannotPublishAfterDeleteDisableGenerationOrClose() async throws {
        for invalidation in ["delete", "disable", "generation", "cancel", "close"] {
            let runtime = SourcesRuntimeFixture()
            let (owner, base, _) = try makeOwner(runtime)
            defer { try? FileManager.default.removeItem(at: base) }
            try await owner.refresh()
            let key = FilmSourceKey(installationId: "a", siteKey: "same")
            await runtime.pause("invoke")
            let detection = Task { try await owner.detect(key) }
            await runtime.waitUntilPaused()
            var closing: Task<Void, Never>?
            switch invalidation {
            case "delete": try await owner.deleteVideo(key)
            case "disable": _ = try await owner.enableCat("a", enabled: false)
            case "generation": await runtime.changeGeneration(); try await owner.refresh()
            case "cancel": await owner.cancelDetection(key)
            default:
                closing = Task { await owner.close() }
                await runtime.waitUntilCancelled()
            }
            await runtime.release()
            do { _ = try await detection.value; XCTFail("Late detection must be cancelled: \(invalidation)") }
            catch { XCTAssertTrue(error is CancellationError, "\(error)") }
            await closing?.value
            let snapshot = await owner.snapshot
            XCTAssertTrue(snapshot.videos.allSatisfy { $0.detection == nil })
            await owner.close()
        }
    }

    func testDisabledCatKeepsSitesRejectsRestartAndDetectionAndDeleteCleansParent() async throws {
        let runtime = SourcesRuntimeFixture()
        let (owner, base, _) = try makeOwner(runtime)
        defer { try? FileManager.default.removeItem(at: base) }
        try await owner.refresh()
        _ = try await owner.enableCat("a", enabled: false)
        let snapshot = await owner.snapshot
        XCTAssertEqual(snapshot.cats.first { $0.installationId == "a" }?.sites.count, 1)
        XCTAssertEqual(snapshot.videos.count, 2)
        do { _ = try await owner.restartCat("a"); XCTFail("Disabled restart") }
        catch let error as FilmSourceError { XCTAssertEqual(error.code, "DISABLED") }
        do { _ = try await owner.detect(.init(installationId: "a", siteKey: "same")); XCTFail("Disabled detection") }
        catch let error as FilmSourceError { XCTAssertEqual(error.code, "DISABLED") }
        try await owner.deleteCat("a")
        let deleted = await owner.snapshot
        XCTAssertEqual(deleted.cats.map(\.installationId), ["b"])
        XCTAssertEqual(deleted.videos.map(\.key.installationId), ["b"])
        await owner.close()
    }

    func testClearDrainsThenRemovesDocumentAndResumeStartsEmpty() async throws {
        let runtime = SourcesRuntimeFixture()
        let (owner, base, store) = try makeOwner(runtime)
        defer { try? FileManager.default.removeItem(at: base) }
        try await owner.refresh()
        await owner.prepareShutdown()
        do { try await owner.refresh(); XCTFail("Prepared owner must reject new work") }
        catch let error as FilmSourceError { XCTAssertEqual(error.code, "SHUTTING_DOWN") }
        try await owner.clear()
        XCTAssertNil(try store.read(key: FilmSourceService.documentKey))
        try await owner.resume()
        try await owner.initialize()
        let snapshot = await owner.snapshot
        XCTAssertTrue(snapshot.cats.isEmpty && snapshot.videos.isEmpty)
        await owner.close()
    }
}

actor SourcesRuntimeFixture {
    var cats = [SourcesRuntimeFixture.cat("a"), SourcesRuntimeFixture.cat("b")]
    var calls: [String] = []
    private var nextFailures: [String: String] = [:]
    func failNext(_ operation: String, code: String) { nextFailures[operation] = code }
    func setCat(_ cat: JSONValue) {
        let id = cat.catField("installationId")
        if let index = cats.firstIndex(where: { $0.catField("installationId") == id }) { cats[index] = cat }
        else { cats.append(cat) }
    }
    var invocations: [(String, JSONValue)] = []
    private var pausedOperation: String?
    private var paused = false
    private var entered: [CheckedContinuation<Void, Never>] = []
    private var pending: CheckedContinuation<Void, Never>?
    var replies: [String: JSONValue] = [:]
    private var routedReplies: [String: JSONValue] = [:]
    func setRoutedReply(_ action: String, id: String, page: Int, reply: JSONValue) {
        routedReplies["\(action):\(id):\(page)"] = reply
    }
    private var cancelled = false
    private var cancelledWaiters: [CheckedContinuation<Void, Never>] = []
    func waitUntilCancelled() async {
        if cancelled { return }
        await withCheckedContinuation { cancelledWaiters.append($0) }
    }
    private func recordCancellation() {
        cancelled = true
        let waiting = cancelledWaiters; cancelledWaiters.removeAll(); waiting.forEach { $0.resume() }
    }

    static func cat(_ id: String, state: String = "ready", enabled: Bool = true, generation: Int = 1) -> JSONValue {
        .object(["installationId": .string(id), "name": .string(id.uppercased()), "sourceType": .string("network"),
            "sourceUrl": .string("https://example.invalid/\(id).js"), "enabled": .bool(enabled),
            "importedAt": .number(100), "updatedAt": .null, "updateState": .string("idle"), "updateError": .null,
            "generation": .number(Double(generation)), "state": .string(state), "sourceHash": .string("hash"), "error": .null,
            "sites": state == "ready" ? .array([.object(["key": .string("same"), "name": .string("站点"),
                "api": .string("/api/\(id)"), "actions": .array(["home", "category", "detail", "play", "search"].map(JSONValue.string)),
                "methods": .object(["home": .string("GET"), "play": .string("POST")])])]) : .array([])])
    }
    func setState(_ state: String, id: String) { cats[cats.firstIndex { $0.catField("installationId") == .string(id) }!] = Self.cat(id, state: state) }
    func changeGeneration() { cats[0] = Self.cat("a", generation: 2) }
    func setReply(_ operation: String, _ reply: JSONValue) { replies[operation] = reply }
    func pause(_ operation: String) { pausedOperation = operation; paused = false }
    func waitUntilPaused() async {
        if paused { return }
        await withCheckedContinuation { entered.append($0) }
    }
    func release() { pending?.resume(); pending = nil }

    func rpc(_ method: String, _ params: JSONValue) async throws -> JSONValue {
        let operation = String(method.dropFirst("cat.script.".count))
        calls.append(operation)
        if pausedOperation == operation {
            pausedOperation = nil; paused = true
            await withTaskCancellationHandler {
                await withCheckedContinuation { continuation in
                    pending = continuation
                    let waiting = entered; entered.removeAll(); waiting.forEach { $0.resume() }
                }
            } onCancel: { Task { await self.recordCancellation() } }
        }
        if let code = nextFailures.removeValue(forKey: operation) {
            return .object(["ok": .bool(false), "contract": .string("cat.script.v1"), "code": .string(code)])
        }
        let id = params.catField("installationId")?.catText ?? "a"
        let result: JSONValue
        switch operation {
        case "status": result = .object(["instances": .array(cats)])
        case "start": result = .object([:])
        case "import", "install", "restart", "edit", "update": result = cats.first { $0.catField("installationId") == .string(id) }!
        case "enable":
            let enabled = params.catField("enabled") == .bool(true)
            let cat = Self.cat(id, state: enabled ? "ready" : "disabled", enabled: enabled)
            cats[cats.firstIndex { $0.catField("installationId") == .string(id) }!] = cat
            result = cat
        case "uninstall": cats.removeAll { $0.catField("installationId") == .string(id) }; result = .object([:])
        case "invoke":
            let action = params.catField("operation")!.catText!
            let payload = try CatScriptTransport.parse(Data(params.catField("payload")!.catText!.utf8))
            invocations.append((action, payload))
            let routeID = payload.catField("id")?.catText ?? ""
            let page = payload.catField("pg")?.catInteger ?? payload.catField("page")?.catInteger ?? 1
            if let reply = routedReplies["\(action):\(routeID):\(page)"] ?? replies[action] { result = reply }
            else {
                switch action {
                case "home": result = .object(["class": .array([.object(["type_id": .string("tv")])])])
                case "category", "search": result = .object(["list": .array([.object(["vod_id": .string("v1")])])])
                case "detail": result = .object(["list": .array([.object(["vod_play_from": .string("线路"), "vod_play_url": .string("第一集$episode#第二集$ignored")])])])
                case "play": result = .object(["url": .string("https://example.invalid/video.m3u8")])
                default: throw FilmSourceError(code: "UNEXPECTED_CALL")
                }
            }
        default: throw FilmSourceError(code: "UNEXPECTED_CALL")
        }
        return try Self.envelope(result)
    }

    static func envelope(_ value: JSONValue) throws -> JSONValue {
        .object(["ok": .bool(true), "contract": .string("cat.script.v1"),
            "data": .string(String(decoding: try JSONEncoder().encode(value), as: UTF8.self))])
    }
}
