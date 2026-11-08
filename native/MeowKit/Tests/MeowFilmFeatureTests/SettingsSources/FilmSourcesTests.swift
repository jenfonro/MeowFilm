import Foundation
import XCTest
import MeowRuntime
import MeowStorage
@testable import MeowFilmFeature

final class FilmSourcesTests: XCTestCase {
    private func makeOwner(_ runtime: SourcesRuntimeFixture) throws -> (FilmSourceService, URL, DocumentStore) {
        let base = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: base, withIntermediateDirectories: true)
        let store = try DocumentStore(baseURL: base, namespace: FilmSourceService.documentNamespace)
        let owner = FilmSourceService(transport: runtime.transport, storeFactory: { store })
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
            let owner = FilmSourceService(transport: unavailableCatTransport("HOST_UNAVAILABLE"),
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

    func testLocalAndNetworkImportsEnableByDefaultAndKeepCandidateRevisionSeparate() async throws {
        let runtime = SourcesRuntimeFixture()
        let (owner, base, _) = try makeOwner(runtime)
        defer { try? FileManager.default.removeItem(at: base) }
        let source = "// 中文\n" + String(repeating: "x", count: 65_537)
        let local = try await owner.importLocal(name: "Local", source: source, installationId: "local")
        XCTAssertTrue(local.enabled)
        XCTAssertEqual(local.sourceType, "local")
        XCTAssertNil(local.sourceUrl)
        let uploads = await runtime.uploads
        let upload = try XCTUnwrap(uploads.first)
        XCTAssertEqual(uploads.count, 1)
        XCTAssertEqual(upload.0, "local")
        XCTAssertEqual(upload.2, Data(source.utf8))
        let requests = await runtime.requests
        let install = try XCTUnwrap(requests.first { $0.0 == "install" }?.1)
        XCTAssertEqual(install, .object(["installationId": .string("local"), "name": .string("Local"),
            "sourceType": .string("local"), "sourceUrl": .null, "enabled": .bool(true),
            "sourceRevision": .number(Double(upload.1))]))
        let updated = try await owner.updateCat("local", source: "// replacement")
        XCTAssertEqual(updated.sourceType, "local")
        let updatedUploads = await runtime.uploads
        XCTAssertEqual(updatedUploads.count, 2)
        let updateRequests = await runtime.requests
        XCTAssertEqual(updateRequests.first { $0.0 == "update" }?.1,
            .object(["installationId": .string("local"), "sourceRevision": .number(Double(updatedUploads[1].1))]))
        let network = try await owner.importNetwork(name: "Network", sourceURL: "https://example.invalid/source.js", installationId: "network")
        XCTAssertTrue(network.enabled)
        XCTAssertEqual(network.sourceType, "network")
        _ = try await owner.editCat("network", name: "Edited", sourceURL: "https://example.invalid/edited.js")
        _ = try await owner.updateCat("network")
        let finalRequests = await runtime.requests
        XCTAssertEqual(finalRequests.first { $0.0 == "import" }?.1,
            .object(["installationId": .string("network"), "name": .string("Network"),
                "sourceUrl": .string("https://example.invalid/source.js"), "enabled": .bool(true)]))
        XCTAssertEqual(finalRequests.first { $0.0 == "edit" }?.1,
            .object(["installationId": .string("network"), "name": .string("Edited"),
                "sourceUrl": .string("https://example.invalid/edited.js")]))
        XCTAssertEqual(finalRequests.last { $0.0 == "update" }?.1, .object(["installationId": .string("network")]))
        let finalUploads = await runtime.uploads
        XCTAssertEqual(finalUploads.count, 2, "Network imports and updates download inside the runtime")
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
        let rebuilt = FilmSourceService(transport: runtime.transport,
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
        failedCat["updateError"] = .string("SCRIPT_EXCEPTION")
        failedCat["generation"] = .number(2)
        await runtime.setCat(.object(failedCat))
        await runtime.failNext("update", code: "SCRIPT_EXCEPTION")
        do { _ = try await owner.updateCat("a"); XCTFail("Failed update") }
        catch let error as FilmSourceError { XCTAssertEqual(error.code, "SCRIPT_EXCEPTION") }
        let after = await owner.snapshot
        let cat = try XCTUnwrap(after.cats.first { $0.installationId == "a" })
        XCTAssertEqual(cat.updateState, "failed")
        XCTAssertEqual(cat.updateError, "SCRIPT_EXCEPTION")
        XCTAssertEqual(cat.generation, 2, "Recovery may restart the accepted source in a fresh generation")
        XCTAssertEqual(cat.sourceHash, before.cats.first?.sourceHash)
        XCTAssertEqual(cat.modifiedAt, before.cats.first?.modifiedAt)
        XCTAssertEqual(after.videos, before.videos)
        XCTAssertTrue(cat.canDetect)
        _ = try await owner.restartCat("a")
        let restarted = await owner.snapshot
        XCTAssertEqual(restarted.cats.first?.modifiedAt, cat.modifiedAt)
        await owner.close()
    }

    func testCommittedManagementFailureReconcilesStatusWithoutRestoringTombstone() async throws {
        for operation in ["update", "import"] {
            let runtime = SourcesRuntimeFixture()
            let (owner, base, store) = try makeOwner(runtime)
            defer { try? FileManager.default.removeItem(at: base) }
            try await owner.refresh()
            let key = FilmSourceKey(installationId: "a", siteKey: "same")
            try await owner.deleteVideo(key)
            let before = try store.read(key: FilmSourceService.documentKey)
            await runtime.failAfterCommit(operation, code: "COMMIT_UNKNOWN")
            do {
                if operation == "update" { _ = try await owner.updateCat("a") }
                else { _ = try await owner.importNetwork(name: "Committed", sourceURL: "https://example.invalid/committed.js", installationId: "a") }
                XCTFail("A committed error remains an error")
            } catch let error as FilmSourceError { XCTAssertEqual(error.code, "COMMIT_UNKNOWN") }
            let snapshot = await owner.snapshot
            let cat = try XCTUnwrap(snapshot.cats.first { $0.installationId == "a" })
            if operation == "update" {
                XCTAssertEqual(cat.sourceHash, "committed-hash")
                XCTAssertEqual(cat.updateState, "updated")
            } else {
                XCTAssertEqual(cat.name, "Committed")
                XCTAssertEqual(cat.sourceUrl, "https://example.invalid/committed.js")
            }
            XCTAssertFalse(snapshot.videos.contains { $0.key == key }, "A failed import has no successful restore receipt")
            XCTAssertNotEqual(try store.read(key: FilmSourceService.documentKey), before)
            let calls = await runtime.calls
            XCTAssertEqual(Array(calls.suffix(2)), [operation, "status"])
            await owner.close()
            let rebuilt = FilmSourceService(transport: runtime.transport, storeFactory: { store })
            try await rebuilt.initialize()
            let restored = await rebuilt.snapshot
            XCTAssertEqual(restored.cats.first { $0.installationId == "a" }?.sourceHash, cat.sourceHash)
            XCTAssertEqual(restored.cats.first { $0.installationId == "a" }?.name, cat.name)
            XCTAssertFalse(restored.videos.contains { $0.key == key })
            await rebuilt.close()
        }
    }

    func testMalformedImportReceiptReconcilesCommittedStatusWithoutRestoringTombstone() async throws {
        let runtime = SourcesRuntimeFixture()
        let (owner, base, store) = try makeOwner(runtime)
        defer { try? FileManager.default.removeItem(at: base) }
        try await owner.refresh()
        let key = FilmSourceKey(installationId: "a", siteKey: "same")
        try await owner.deleteVideo(key)
        await runtime.returnNext("import", value: .object(["instances": .array([SourcesRuntimeFixture.cat("a")])]))
        do {
            _ = try await owner.importNetwork(name: "Committed", sourceURL: "https://example.invalid/committed.js", installationId: "a")
            XCTFail("Import must return its instance directly")
        } catch let error as FilmSourceError { XCTAssertEqual(error.code, "INVALID_RESPONSE") }
        let snapshot = await owner.snapshot
        XCTAssertEqual(snapshot.cats.first { $0.installationId == "a" }?.name, "Committed")
        XCTAssertFalse(snapshot.videos.contains { $0.key == key })
        let calls = await runtime.calls
        XCTAssertEqual(Array(calls.suffix(2)), ["import", "status"])
        await owner.close()
        let rebuilt = FilmSourceService(transport: unavailableCatTransport("OFFLINE"), storeFactory: { store })
        try await rebuilt.initialize()
        let restored = await rebuilt.snapshot
        XCTAssertEqual(restored.cats.first { $0.installationId == "a" }?.name, "Committed")
        XCTAssertFalse(restored.videos.contains { $0.key == key })
        await rebuilt.close()
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
        let rebuilt = FilmSourceService(transport: unavailableCatTransport("OFFLINE"), storeFactory: { store })
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

    func testPrepareShutdownDrainsAdmittedUploadAndManagementRequest() async throws {
        for operation in ["upload", "install"] {
            let runtime = SourcesRuntimeFixture()
            let (owner, base, store) = try makeOwner(runtime)
            defer { try? FileManager.default.removeItem(at: base) }
            try await owner.refresh()
            let before = await owner.snapshot
            let bytes = try store.read(key: FilmSourceService.documentKey)
            await runtime.pause(operation)
            let importing = Task { try await owner.importLocal(name: "Replacement", source: "source", installationId: "a") }
            await runtime.waitUntilPaused()
            let prepared = expectation(description: "Shutdown drained \(operation)")
            prepared.isInverted = true
            let preparing = Task { await owner.prepareShutdown(); prepared.fulfill() }
            await runtime.waitUntilCancelled()
            await fulfillment(of: [prepared], timeout: 0.05)
            let admittedCalls = await runtime.calls
            XCTAssertEqual(admittedCalls.filter { $0 == operation }.count, 1)
            await runtime.release()
            do { _ = try await importing.value; XCTFail("Cancelled import cannot publish") }
            catch { XCTAssertTrue(error is CancellationError, "\(error)") }
            await preparing.value
            let snapshot = await owner.snapshot
            XCTAssertEqual(snapshot, before)
            XCTAssertEqual(try store.read(key: FilmSourceService.documentKey), bytes)
            let calls = await runtime.calls
            XCTAssertEqual(calls.filter { $0 == "status" }.count, 1, "Cancellation must not admit reconciliation work")
            XCTAssertEqual(calls.filter { $0 == "install" }.count, operation == "install" ? 1 : 0)
            try await owner.resume()
            try await owner.refresh()
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
    private var committedFailures: [String: String] = [:]
    private var nextResults: [String: RuntimeValue] = [:]
    var requests: [(String, RuntimeValue)] = []
    var uploads: [(String, Int, Data)] = []
    private var candidates: [String: Int] = [:]

    nonisolated var transport: CatScriptTransport {
        CatScriptTransport(request: { operation, params in try await self.request(operation, params) },
            upload: { id, revision, source in try await self.upload(id, revision, source) })
    }
    func failNext(_ operation: String, code: String) { nextFailures[operation] = code }
    func failAfterCommit(_ operation: String, code: String) { committedFailures[operation] = code }
    func returnNext(_ operation: String, value: RuntimeValue) { nextResults[operation] = value }
    func setCat(_ cat: RuntimeValue) {
        let id = cat.catField("installationId")
        if let index = cats.firstIndex(where: { $0.catField("installationId") == id }) { cats[index] = cat }
        else { cats.append(cat) }
    }
    var invocations: [(String, RuntimeValue)] = []
    var invocationSiteKeys: [String] = []
    private var pausedOperation: String?
    private var paused = false
    private var entered: [CheckedContinuation<Void, Never>] = []
    private var pending: CheckedContinuation<Void, Never>?
    var replies: [String: RuntimeValue] = [:]
    private var routedReplies: [String: RuntimeValue] = [:]
    func setRoutedReply(_ action: String, id: String, page: Int, reply: RuntimeValue) {
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

    static func cat(_ id: String, state: String = "ready", enabled: Bool = true, generation: Int = 1) -> RuntimeValue {
        .object(["installationId": .string(id), "name": .string(id.uppercased()), "sourceType": .string("network"),
            "sourceUrl": .string("https://example.invalid/\(id).js"), "enabled": .bool(enabled),
            "importedAt": .number(100), "updatedAt": .null, "updateState": .string("idle"), "updateError": .null,
            "generation": .number(Double(generation)), "state": .string(state), "sourceHash": .string("hash"), "error": .null,
            "sites": state == "ready" ? .array([.object(["key": .string("same"), "name": .string("站点"),
                "api": .string("/api/\(id)"), "actions": .array(["home", "category", "detail", "play", "search"].map(RuntimeValue.string)),
                "methods": .object(["home": .string("GET"), "play": .string("POST")])])]) : .array([])])
    }
    func setState(_ state: String, id: String) { cats[cats.firstIndex { $0.catField("installationId") == .string(id) }!] = Self.cat(id, state: state) }
    func changeGeneration() { cats[0] = Self.cat("a", generation: 2) }
    func setReply(_ operation: String, _ reply: RuntimeValue) { replies[operation] = reply }
    func pause(_ operation: String) { pausedOperation = operation; paused = false }
    func waitUntilPaused() async {
        if paused { return }
        await withCheckedContinuation { entered.append($0) }
    }
    func release() { pending?.resume(); pending = nil }

    private func suspendIfNeeded(_ operation: String) async {
        if pausedOperation == operation {
            pausedOperation = nil; paused = true
            await withTaskCancellationHandler {
                await withCheckedContinuation { continuation in
                    pending = continuation
                    let waiting = entered; entered.removeAll(); waiting.forEach { $0.resume() }
                }
            } onCancel: { Task { await self.recordCancellation() } }
        }
    }

    func upload(_ id: String, _ revision: Int, _ source: Data) async throws -> RuntimeValue {
        calls.append("upload")
        uploads.append((id, revision, source))
        await suspendIfNeeded("upload")
        if let code = nextFailures.removeValue(forKey: "upload") { throw RuntimeHTTPError.remote(code) }
        candidates[id] = revision
        return .object(["installationId": .string(id), "sourceRevision": .number(Double(revision)),
            "sourceBytes": .number(Double(source.count)), "contentHash": .string("fixture-content")])
    }

    func request(_ operation: String, _ params: RuntimeValue) async throws -> RuntimeValue {
        calls.append(operation)
        requests.append((operation, params))
        await suspendIfNeeded(operation)
        if let code = nextFailures.removeValue(forKey: operation) { throw RuntimeHTTPError.remote(code) }
        let id = params.catField("installationId")?.catText ?? "a"
        let result: RuntimeValue
        switch operation {
        case "status": result = .object(["instances": .array(cats)])
        case "start": result = .object([:])
        case "install", "import", "edit", "update", "restart":
            var cat = cats.first { $0.catField("installationId") == .string(id) } ?? Self.cat(id)
            if operation == "install" || (operation == "update" && cat.catField("sourceType") == .string("local")) {
                guard params.catField("sourceRevision")?.catInteger == candidates[id], candidates[id] != nil else {
                    throw RuntimeHTTPError.remote("NOT_FOUND")
                }
                candidates[id] = nil
            }
            if operation == "update", cat.catField("sourceType") == .string("network"), params.catField("sourceRevision") != nil {
                throw RuntimeHTTPError.remote("INVALID_ARGUMENT")
            }
            if case .object(var fields) = cat {
                for field in ["name", "sourceUrl", "enabled"] {
                    if let value = params.catField(field) { fields[field] = value }
                }
                if operation == "install" { fields["sourceType"] = .string("local") }
                if operation == "import" { fields["sourceType"] = .string("network") }
                if operation == "update", committedFailures[operation] != nil {
                    fields["sourceHash"] = .string("committed-hash")
                    fields["updateState"] = .string("updated")
                    fields["updatedAt"] = .number(200)
                }
                cat = .object(fields)
            }
            setCat(cat)
            result = cat
        case "enable":
            let enabled = params.catField("enabled") == .bool(true)
            let cat = Self.cat(id, state: enabled ? "ready" : "disabled", enabled: enabled)
            cats[cats.firstIndex { $0.catField("installationId") == .string(id) }!] = cat
            result = cat
        case "uninstall": cats.removeAll { $0.catField("installationId") == .string(id) }; result = .object([:])
        case "invoke":
            invocationSiteKeys.append(params.catField("siteId")!.catText!)
            let action = params.catField("operation")!.catText!
            guard case .object? = params.catField("payload") else { throw RuntimeHTTPError.remote("INVALID_ARGUMENT") }
            let payload = params.catField("payload")!
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
        if let code = committedFailures.removeValue(forKey: operation) { throw RuntimeHTTPError.remote(code) }
        return nextResults.removeValue(forKey: operation) ?? result
    }
}

func unavailableCatTransport(_ code: String) -> CatScriptTransport {
    CatScriptTransport(request: { _, _ in throw FilmSourceError(code: code) },
        upload: { _, _, _ in throw FilmSourceError(code: code) })
}
