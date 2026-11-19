import Foundation
import XCTest
import MeowRuntime
import MeowStorage
import MeowStorageTestSupport
@testable import MeowFilmFeature

final class FilmSourcesTests: XCTestCase {
    private func makeOwner(_ runtime: SourcesRuntimeFixture) throws -> (FilmSourceService, URL, SQLiteDatabase) {
        let base = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: base, withIntermediateDirectories: true)
        let store = try storageTestDatabase(self, root: base)
        let owner = FilmSourceService(transport: runtime.transport, database: store, settingsDocument: try sourceTestSettings(store),
            runtimeSourceRoot: base.appendingPathComponent("cat"))
        return (owner, base, store)
    }

    func testLocalInitializationDoesNotStartNodeAndStartupRestoresWithoutProbing() async throws {
        let runtime = SourcesRuntimeFixture()
        let (owner, base, _) = try makeOwner(runtime)
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
                let store = try storageTestDatabase(self, root: base)
            let owner = FilmSourceService(transport: unavailableCatTransport("HOST_UNAVAILABLE"),
                database: store, settingsDocument: try sourceTestSettings(store),
            runtimeSourceRoot: base.appendingPathComponent("cat"))
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
        let file = base.appendingPathComponent("source.js")
        try Data([0xff, 0xfe, 0x80]).write(to: file)
        do { _ = try await owner.importLocal(name: "fixture", fileURL: file); XCTFail("Invalid UTF-8") }
        catch let error as FilmSourceError { XCTAssertEqual(error.code, "INVALID_SOURCE") }
        let calls = await runtime.calls
        XCTAssertTrue(calls.isEmpty)
        await owner.close()
    }

    func testNewLocalAndNetworkIntentSaveWithoutHTTPInstallOrDownload() async throws {
        let runtime = SourcesRuntimeFixture()
        let (owner, base, _) = try makeOwner(runtime)
        let local = try await owner.importLocal(name: "Local", source: "// 中文\nmodule.exports = {}", installationId: "local")
        let network = try await owner.importNetwork(name: "Network", sourceURL: "https://example.invalid/source.js", installationId: "network")
        XCTAssertTrue(local.enabled && network.enabled)
        XCTAssertEqual(local.sourceType, "local")
        XCTAssertNil(local.sourceUrl)
        XCTAssertEqual(network.sourceType, "network")
        XCTAssertEqual(local.inputRevision, 1)
        XCTAssertEqual(network.inputRevision, 1)
        XCTAssertEqual(local.generation, 0)
        XCTAssertEqual(network.generation, 0)
        let calls = await runtime.calls
        XCTAssertTrue(calls.isEmpty, "Native source intent saves cannot install, upload, or download")
        await owner.close()
    }

    func testOptionsOrderingAndTombstonesSurviveNativeSourceEditsAndReopen() async throws {
        let runtime = SourcesRuntimeFixture()
        let (owner, base, store) = try makeOwner(runtime)
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
            database: store, settingsDocument: try sourceTestSettings(store),
            runtimeSourceRoot: base.appendingPathComponent("cat"))
        try await rebuilt.initialize()
        let restored = await rebuilt.snapshot
        XCTAssertEqual(restored.videos, snapshot.videos)
        XCTAssertEqual(restored.cats.map(\.generation), [0, 0])
        try await rebuilt.refresh()
        _ = try await rebuilt.editCat("a", name: "A", sourceURL: "https://example.invalid/a.js")
        let imported = await rebuilt.snapshot
        XCTAssertEqual(imported.videos.map(\.key), [b], "Source intent edits cannot restore deleted video sources")
        XCTAssertEqual(imported.videos.first { $0.key == b }?.options,
            .init(enabled: false, homeVisible: false, searchEnabled: false, aggregateImages: true))
        await owner.close(); await rebuilt.close()
    }

    func testFailedAndStartingActualLoadsDoNotUndoSavedIntentOrRestoreDeletedVideos() async throws {
        let runtime = SourcesRuntimeFixture()
        let (owner, base, _) = try makeOwner(runtime)
        try await owner.refresh()
        let key = FilmSourceKey(installationId: "a", siteKey: "same")
        try await owner.deleteVideo(key)
        for state in ["failed", "starting"] {
            await runtime.setState(state, id: "a")
            try await owner.refresh()
            let saved = try await owner.editCat("a", name: "Saved \(state)")
            XCTAssertEqual(saved.name, "Saved \(state)")
            XCTAssertEqual(saved.state, state)
            let snapshot = await owner.snapshot
            XCTAssertFalse(snapshot.videos.contains { $0.key == key })
            XCTAssertEqual(snapshot.cats.first { $0.installationId == "a" }?.sites.count, 1)
        }
        await owner.close()
    }

    func testIntentReplacementAndLaterReadyStatusCannotRestoreOwnOrOtherParentTombstones() async throws {
        let runtime = SourcesRuntimeFixture()
        let (owner, base, store) = try makeOwner(runtime)
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
        _ = try await owner.editCat("a", name: "Replacement", sourceURL: "https://example.invalid/replacement.js")
        let document = try sourceTestSettings(store)
        try await runtime.acceptNativeSettings(document)
        try await owner.refresh()
        let snapshot = await owner.snapshot
        XCTAssertTrue(snapshot.videos.isEmpty)
        XCTAssertEqual(snapshot.cats.first { $0.installationId == "a" }?.inputRevision, 2)
        XCTAssertTrue(try XCTUnwrap(snapshot.cats.first { $0.installationId == "a" }).canDetect)
        await owner.close()
    }

    func testNewUpdateDisplayStateDoesNotFailTheRuntimeSourceBatch() async throws {
        let runtime = SourcesRuntimeFixture()
        let (owner, base, _) = try makeOwner(runtime)
        guard case .object(var cat) = SourcesRuntimeFixture.cat("a") else { return XCTFail("Fixture") }
        cat["updateState"] = .string("scheduled")
        cat["extra"] = .object(["state": .array([.number(1)])])
        await runtime.setCat(.object(cat))
        try await owner.refresh()
        let snapshot = await owner.snapshot
        XCTAssertNil(snapshot.runtimeError)
        XCTAssertEqual(snapshot.cats.count, 2)
        XCTAssertEqual(snapshot.cats.first { $0.installationId == "a" }?.updateState, "scheduled")
        XCTAssertTrue(snapshot.cats.allSatisfy(\.canDetect))
        XCTAssertEqual(snapshot.videos.map(\.key.installationId), ["a", "b"])
        await owner.close()
    }

    func testFailedUpdateKeepsOldVersionAndReportedModificationTime() async throws {
        let runtime = SourcesRuntimeFixture()
        let (owner, base, _) = try makeOwner(runtime)
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

    func testCommittedNetworkUpdateFailureReconcilesFactsWithoutRestoringTombstone() async throws {
        let runtime = SourcesRuntimeFixture()
        let (owner, base, store) = try makeOwner(runtime)
        try await owner.refresh()
        let key = FilmSourceKey(installationId: "a", siteKey: "same")
        try await owner.deleteVideo(key)
        let before = try sourceTestRows(store)
        await runtime.failAfterCommit("update", code: "COMMIT_UNKNOWN")
        do { _ = try await owner.updateCat("a"); XCTFail("A committed error remains an error") }
        catch let error as FilmSourceError { XCTAssertEqual(error.code, "COMMIT_UNKNOWN") }
        let snapshot = await owner.snapshot
        let cat = try XCTUnwrap(snapshot.cats.first { $0.installationId == "a" })
        XCTAssertEqual(cat.sourceHash, "committed-hash")
        XCTAssertEqual(cat.updateState, "updated")
        XCTAssertEqual(cat.inputRevision, 1)
        XCTAssertFalse(snapshot.videos.contains { $0.key == key })
        XCTAssertNotEqual(try sourceTestRows(store), before)
        let calls = await runtime.calls
        XCTAssertEqual(Array(calls.suffix(2)), ["update", "status"])
        await owner.close()
        let rebuilt = FilmSourceService(transport: unavailableCatTransport("OFFLINE"), database: store,
            settingsDocument: try sourceTestSettings(store), runtimeSourceRoot: base.appendingPathComponent("cat"))
        try await rebuilt.initialize()
        let restored = await rebuilt.snapshot
        XCTAssertEqual(restored.cats.first { $0.installationId == "a" }?.sourceHash, cat.sourceHash)
        XCTAssertEqual(restored.cats.first { $0.installationId == "a" }?.name, cat.name)
        XCTAssertFalse(restored.videos.contains { $0.key == key })
        await rebuilt.close()
    }

    func testMalformedStatusCannotOverwriteSavedIntentOrRestoreTombstone() async throws {
        let runtime = SourcesRuntimeFixture()
        let (owner, base, store) = try makeOwner(runtime)
        try await owner.refresh()
        let key = FilmSourceKey(installationId: "a", siteKey: "same")
        try await owner.deleteVideo(key)
        _ = try await owner.editCat("a", name: "Committed", sourceURL: "https://example.invalid/committed.js")
        await runtime.returnNext("status", value: .object(["instances": .array([.object(["installationId": .string("a")])])]))
        do { try await owner.refresh(); XCTFail("Malformed status") }
        catch let error as FilmSourceError { XCTAssertEqual(error.code, "INVALID_RESPONSE") }
        let snapshot = await owner.snapshot
        XCTAssertEqual(snapshot.cats.first { $0.installationId == "a" }?.name, "Committed")
        XCTAssertEqual(snapshot.cats.first { $0.installationId == "a" }?.inputRevision, 2)
        XCTAssertFalse(snapshot.videos.contains { $0.key == key })
        await owner.close()
        let rebuilt = FilmSourceService(transport: unavailableCatTransport("OFFLINE"), database: store,
            settingsDocument: try sourceTestSettings(store), runtimeSourceRoot: base.appendingPathComponent("cat"))
        try await rebuilt.initialize()
        let restored = await rebuilt.snapshot
        XCTAssertEqual(restored.cats.first { $0.installationId == "a" }?.name, "Committed")
        XCTAssertEqual(restored.cats.first { $0.installationId == "a" }?.sourceUrl, "https://example.invalid/committed.js")
        XCTAssertFalse(restored.videos.contains { $0.key == key })
        await rebuilt.close()
    }

    func testNativeSourceEditAndNewDeletionDoNotWaitForInFlightRuntimeStatus() async throws {
        let runtime = SourcesRuntimeFixture()
        let (owner, base, _) = try makeOwner(runtime)
        try await owner.refresh()
        let key = FilmSourceKey(installationId: "a", siteKey: "same")
        await runtime.pause("status")
        let refreshing = Task { try await owner.refresh() }
        await runtime.waitUntilPaused()
        _ = try await owner.editCat("a", name: "Saved", sourceURL: "https://example.invalid/new.js")
        try await owner.deleteVideo(key)
        await runtime.release()
        try await refreshing.value
        let snapshot = await owner.snapshot
        XCTAssertFalse(snapshot.videos.contains { $0.key == key })
        XCTAssertEqual(snapshot.cats.first { $0.installationId == "a" }?.inputRevision, 2)
        XCTAssertEqual(snapshot.cats.first { $0.installationId == "a" }?.name, "Saved")
        XCTAssertEqual(snapshot.cats.first { $0.installationId == "a" }?.generation, 0)
        await owner.close()
    }

    func testStorageConflictDoesNotPublishOptionsAndReloadsActualDocument() async throws {
        let runtime = SourcesRuntimeFixture()
        let (owner, base, store) = try makeOwner(runtime)
        try await owner.refresh()
        let before = await owner.snapshot
        _ = try await store.transaction { try $0.execute("UPDATE ios_film_source_state SET revision=revision+1 WHERE singleton=1") }
        do {
            try await owner.saveVideoOptions(.init(installationId: "a", siteKey: "same"), options: .init(enabled: false))
            XCTFail("Conflicting save must fail")
        } catch let error as FilmSourceError { XCTAssertEqual(error.code, "STORAGE_CONFLICT") }
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
        let rebuilt = FilmSourceService(transport: unavailableCatTransport("OFFLINE"), database: store, settingsDocument: try sourceTestSettings(store),
            runtimeSourceRoot: base.appendingPathComponent("cat"))
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

    func testPrepareShutdownDrainsAdmittedNetworkUpdateAndStatusRequest() async throws {
        for operation in ["update", "status"] {
            let runtime = SourcesRuntimeFixture()
            let (owner, base, store) = try makeOwner(runtime)
                try await owner.refresh()
            let before = await owner.snapshot
            let bytes = try sourceTestRows(store)
            await runtime.pause(operation)
            let importing = Task { try await owner.updateCat("a") }
            await runtime.waitUntilPaused()
            let prepared = expectation(description: "Shutdown drained \(operation)")
            prepared.isInverted = true
            let preparing = Task { await owner.prepareShutdown(); prepared.fulfill() }
            await runtime.waitUntilCancelled()
            await fulfillment(of: [prepared], timeout: 0.05)
            let admittedCalls = await runtime.calls
            XCTAssertEqual(admittedCalls.filter { $0 == operation }.count, operation == "status" ? 2 : 1)
            await runtime.release()
            do { _ = try await importing.value; XCTFail("Cancelled import cannot publish") }
            catch { XCTAssertTrue(error is CancellationError, "\(error)") }
            await preparing.value
            let snapshot = await owner.snapshot
            XCTAssertEqual(snapshot, before)
            XCTAssertEqual(try sourceTestRows(store), bytes)
            let calls = await runtime.calls
            XCTAssertEqual(calls.filter { $0 == "status" }.count, operation == "status" ? 2 : 1, "Cancellation must not admit reconciliation work")
            XCTAssertEqual(calls.filter { $0 == "update" }.count, 1)
            try await owner.resume()
            try await owner.refresh()
            await owner.close()
        }
    }

    func testDisabledCatKeepsSitesRejectsRestartAndDetectionAndDeleteCleansParent() async throws {
        let runtime = SourcesRuntimeFixture()
        let (owner, base, _) = try makeOwner(runtime)
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
        try await owner.refresh()
        await owner.prepareShutdown()
        do { try await owner.refresh(); XCTFail("Prepared owner must reject new work") }
        catch let error as FilmSourceError { XCTAssertEqual(error.code, "SHUTTING_DOWN") }
        try await owner.clear()
        XCTAssertNil(try sourceTestRows(store))
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

    nonisolated var transport: CatScriptTransport {
        CatScriptTransport(request: { operation, params in try await self.request(operation, params) })
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
            "inputRevision": .number(1), "generation": .number(Double(generation)), "state": .string(state), "sourceHash": .string("hash"), "error": .null,
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

    func acceptNativeSettings(_ document: FilmSettingsDocument) async throws {
        cats = try await CatSourceSettings(document: document).read().map { input in
            let old = cats.first { $0.catField("installationId") == .string(input.installationId) }
            guard case .object(var fields) = old ?? Self.cat(input.installationId) else { preconditionFailure("Fixture") }
            for (key, value) in ["name": RuntimeValue.string(input.name), "sourceType": .string(input.sourceType),
                "sourceUrl": input.sourceUrl.map(RuntimeValue.string) ?? .null, "enabled": .bool(input.enabled),
                "inputRevision": .number(Double(input.inputRevision)), "state": .string(input.enabled ? "ready" : "disabled")] {
                fields[key] = value
            }
            if input.sourceType == "local" { fields["sourceHash"] = input.localHash.map(RuntimeValue.string) ?? .null }
            return .object(fields)
        }
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
        case "update", "restart":
            var cat = cats.first { $0.catField("installationId") == .string(id) } ?? Self.cat(id)
            if case .object(var fields) = cat {
                for field in ["name", "sourceUrl", "enabled"] {
                    if let value = params.catField(field) { fields[field] = value }
                }
                if operation == "update", committedFailures[operation] != nil {
                    fields["sourceHash"] = .string("committed-hash")
                    fields["updateState"] = .string("updated")
                    fields["updatedAt"] = .number(200)
                }
                cat = .object(fields)
            }
            setCat(cat)
            result = cat
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
    CatScriptTransport(request: { _, _ in throw FilmSourceError(code: code) })
}
