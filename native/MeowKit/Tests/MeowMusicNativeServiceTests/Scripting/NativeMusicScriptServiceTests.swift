import Foundation
import MeowJSON
import MeowMusicFeature
import XCTest
@testable import MeowMusicNativeService

final class NativeMusicScriptServiceTests: XCTestCase {
    func testImportInspectsOnlyLeadingMetadataAndRemainsDisabledAcrossReopenWithoutStartingRuntime() async throws {
        let documents = try MusicScriptTestDocuments(); defer { documents.remove() }
        let runtime = MusicScriptTestRuntime(), service = documents.owner(runtime)
        let item = try await service.importScriptSource(musicScriptFixture, fileName: "source.JS")
        XCTAssertEqual(item.name, "合成 LX 音源"); XCTAssertEqual(item.version, "2")
        XCTAssertEqual(item.author, "Fixture"); XCTAssertEqual(item.homepage, "https://source.invalid")
        XCTAssertEqual(item.summary, "Synthetic test source"); XCTAssertEqual(item.origin, .file)
        XCTAssertFalse(item.enabled); XCTAssertEqual(item.state, .disabled); XCTAssertTrue(item.platforms.isEmpty)
        let catalog = try MusicScriptCatalog.decode(Data(contentsOf: documents.file("catalog")))
        XCTAssertEqual(catalog.installations.map(\.id), [item.id])
        XCTAssertEqual(try Data(contentsOf: documents.file("source." + musicScriptSHA256(musicScriptFixture))), musicScriptFixture)
        let reopened = try await documents.owner(runtime).scriptSources()
        XCTAssertEqual(reopened, [item])
        let metrics = await runtime.metrics(); XCTAssertEqual(metrics.connects, 0)
        let other = try await service.importScriptSource(Data("throw Error('never run'); // @name not metadata".utf8), fileName: "fallback.js")
        XCTAssertEqual(other.name, "fallback")
        await service.close()
    }

    func testInvalidImportsLeaveNoCatalogOrSourceAndDoNotLaunch() async throws {
        let documents = try MusicScriptTestDocuments(); defer { documents.remove() }
        let runtime = MusicScriptTestRuntime(), service = documents.owner(runtime)
        for (data, name, expected) in [
            (Data(), "a.js", MusicServiceError.invalidScript),
            (Data(" \n\t".utf8), "a.js", .invalidScript),
            (Data([0xff, 0xfe, 0x61]), "a.js", .invalidScript),
            (Data([0xef, 0xbb, 0xbf]) + musicScriptFixture, "a.js", .invalidScript),
            (musicScriptFixture, "a.txt", .invalidScript),
            (Data(repeating: 65, count: MusicScriptSourceItem.maximumSourceBytes + 1), "a.js", .limit),
        ] {
            await scriptExpectFailure(expected) { try await service.importScriptSource(data, fileName: name) }
        }
        XCTAssertNil(try documents.store.read(key: "catalog"))
        let items = try await service.scriptSources(), metrics = await runtime.metrics()
        XCTAssertTrue(items.isEmpty); XCTAssertEqual(metrics.connects, 0)
        await service.close()
    }

    func testEnableDisableReloadFenceInvokeAndStorageAndDeletionRemovesPrivateDataAndLastSourceReference() async throws {
        let documents = try MusicScriptTestDocuments(); defer { documents.remove() }
        let runtime = MusicScriptTestRuntime(), service = documents.owner(runtime)
        let a = try await service.importScriptSource(musicScriptFixture, fileName: "a.js")
        let b = try await service.importScriptSource(musicScriptFixture, fileName: "b.js")
        let enabled = try await service.setScriptSourceEnabled(a.id, enabled: true)
        XCTAssertTrue(enabled.enabled); XCTAssertEqual(enabled.state, .ready); XCTAssertEqual(enabled.platforms, [.qq])
        let sources = try await service.sources(), first = try XCTUnwrap(sources.first)
        let binding = try await runtime.binding(a.id)
        let stored = try await runtime.reverse("set", binding: binding)
        XCTAssertEqual(stored["ok"], .bool(true))
        XCTAssertNotNil(try documents.store.read(key: "storage." + a.id))
        let storage = try MusicScriptStorage.decode(Data(contentsOf: documents.file("storage." + a.id)))
        XCTAssertEqual(storage.value(site: "tx", key: "count"), .number(7))
        let wrongGeneration = try await runtime.reverse("get", binding: binding, generation: first.generation + 1)
        XCTAssertEqual(wrongGeneration["code"], .string("STALE_SOURCE"))
        let request = MusicScriptRequest(installationID: a.id, generation: first.generation, siteID: "tx",
                                        sourceKey: "tx:test", quality: "128k", musicInfo: Data("{}".utf8))
        let audio = try await service.resolve(request: request)
        XCTAssertEqual(audio.url.absoluteString, "https://media.invalid/track.mp3")
        _ = try await service.setScriptSourceEnabled(a.id, enabled: false)
        let stale = try await runtime.reverse("get", binding: binding)
        XCTAssertEqual(stale["code"], .string("STALE_SOURCE"))
        await scriptExpectFailure(.sourceUnavailable) { try await service.resolve(request: request) }
        _ = try await service.setScriptSourceEnabled(a.id, enabled: true)
        let next = try await service.source(installationID: a.id)
        XCTAssertGreaterThan(next.generation, first.generation)
        let nextBinding = try await runtime.binding(a.id)
        let retained = try await runtime.reverse("get", binding: nextBinding)
        XCTAssertEqual(retained["value"], .number(7))
        let stillStale = try await runtime.reverse("get", binding: binding)
        XCTAssertEqual(stillStale["code"], .string("STALE_SOURCE"))
        try await service.removeScriptSource(a.id)
        XCTAssertNil(try documents.store.read(key: "storage." + a.id))
        XCTAssertFalse(FileManager.default.fileExists(atPath: documents.file("storage." + a.id).path))
        XCTAssertNotNil(try documents.store.read(key: "source." + musicScriptSHA256(musicScriptFixture)))
        try await service.removeScriptSource(b.id)
        XCTAssertNil(try documents.store.read(key: "source." + musicScriptSHA256(musicScriptFixture)))
        let reopened = try await documents.owner(runtime).scriptSources()
        XCTAssertTrue(reopened.isEmpty)
        let calls = await runtime.metrics().calls
        XCTAssertEqual(calls.filter { $0 == "unload" }.count, 2)
        await service.close(); await runtime.close()
    }

    func testFailedLoadAndLostReceiptNeverPersistEnabledAndReconcilePublishedBinding() async throws {
        for mode in [MusicScriptTestRuntime.Load.failed, .lostReceipt] {
            let documents = try MusicScriptTestDocuments(); defer { documents.remove() }
            let runtime = MusicScriptTestRuntime(), service = documents.owner(runtime)
            await runtime.configure(mode)
            let item = try await service.importScriptSource(musicScriptFixture, fileName: "source.js")
            await scriptExpectFailure(mode == .failed ? .scriptFailed : .timeout) {
                try await service.setScriptSourceEnabled(item.id, enabled: true)
            }
            let items = try await documents.owner(runtime).scriptSources()
            XCTAssertEqual(items, [item])
            let calls = await runtime.metrics().calls
            XCTAssertTrue(calls.contains("status"))
            if mode == .lostReceipt { XCTAssertTrue(calls.contains("unload")) }
            let instances = await runtime.instances
            XCTAssertTrue(instances.isEmpty)
            await runtime.configure(.success)
            let enabled = try await service.setScriptSourceEnabled(item.id, enabled: true)
            XCTAssertTrue(enabled.enabled, "An explicit retry can load after a script failure")
            await service.close(); await runtime.close()
        }
    }

    func testDisablingRejectsLateInvokeAndRuntimeExitIsFinal() async throws {
        let documents = try MusicScriptTestDocuments(); defer { documents.remove() }
        let runtime = MusicScriptTestRuntime(), service = documents.owner(runtime)
        let item = try await service.importScriptSource(musicScriptFixture, fileName: "source.js")
        _ = try await service.setScriptSourceEnabled(item.id, enabled: true)
        let source = try await service.source(installationID: item.id), gate = NativeMusicGate()
        await runtime.holdInvoke(gate)
        let task = Task {
            try await service.resolve(request: .init(installationID: item.id, generation: source.generation,
                siteID: "tx", sourceKey: "test", quality: "128k", musicInfo: Data("{}".utf8)))
        }
        await gate.started()
        _ = try await service.setScriptSourceEnabled(item.id, enabled: false)
        await gate.release()
        await scriptExpectFailure(.sourceUnavailable) { try await task.value }
        _ = try await service.setScriptSourceEnabled(item.id, enabled: true)
        await runtime.close()
        await scriptExpectFailure(.sourceUnavailable) { try await service.sources() }
        let items = try await service.scriptSources()
        XCTAssertEqual(items.first?.state, .failed)
        await scriptExpectFailure(.scriptRuntimeUnavailable) { try await service.setScriptSourceEnabled(item.id, enabled: true) }
        let count = await runtime.metrics().connects
        XCTAssertEqual(count, 1)
        await service.close()
    }
}
