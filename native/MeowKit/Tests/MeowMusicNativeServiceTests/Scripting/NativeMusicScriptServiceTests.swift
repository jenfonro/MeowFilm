import Foundation
import MeowJSON
import MeowMusicFeature
import XCTest
@testable import MeowMusicNativeService

final class NativeMusicScriptServiceTests: XCTestCase {
    func testExplicitStartupIsSharedAndDoesNotExecuteScripts() async throws {
        let runtime = MusicScriptTestRuntime(), service = NativeMusicScriptService(launcher: runtime)
        await scriptExpectFailure(.scriptRuntimeUnavailable) { try await service.scriptSources() }
        await service.start(); await service.start()
        let items = try await service.scriptSources()
        XCTAssertTrue(items.isEmpty)
        let metrics = await runtime.metrics()
        XCTAssertEqual(metrics.connects, 1); XCTAssertEqual(metrics.calls, ["runtime.describe", "sources.list"])
        let handlers = await runtime.reverseHandlers; XCTAssertTrue(handlers.isEmpty)
        await service.close(); await runtime.close()
    }
    func testImportStagesThenUsesSharedManagementWithoutNativeStorage() async throws {
        let runtime = MusicScriptTestRuntime(), service = NativeMusicScriptService(launcher: runtime)
        await service.start()
        let bytes = Data(repeating: 65, count: 128 * 1024 + 1)
        let item = try await service.importScriptSource(bytes, fileName: "selected.js")
        XCTAssertEqual(item.name, "Synthetic source"); XCTAssertFalse(item.enabled)
        let calls = await runtime.parameters
        XCTAssertEqual(calls.filter { $0.0 == "stage.chunk" }.count, 3)
        XCTAssertEqual(calls.first { $0.0 == "stage.begin" }?.1["basename"], .string("selected.js"))
        XCTAssertEqual(calls.first { $0.0 == "stage.begin" }?.1["sha256"], .string(musicScriptSHA256(bytes)))
        XCTAssertTrue(calls.contains { $0.0 == "sources.add" }); XCTAssertFalse(calls.contains { $0.0 == "sources.enable" })
        let stages = await runtime.stages; XCTAssertTrue(stages.isEmpty)
        let listed = try await service.scriptSources(); XCTAssertEqual(listed, [item])
        await service.close(); await runtime.close()
    }
    func testSingleQualityResolvePreservesHeadersExpiryAndFencesDisabledOrOldGeneration() async throws {
        let runtime = MusicScriptTestRuntime(), service = NativeMusicScriptService(launcher: runtime)
        await service.start()
        let item = try await service.importScriptSource(musicScriptFixture, fileName: "source.js")
        _ = try await service.setScriptSourceEnabled(item.id, enabled: true)
        let source = try await service.source(installationID: item.id)
        XCTAssertEqual(source.capabilities.map(\.siteID), ["tx"])
        let request = MusicScriptRequest(installationID: item.id, generation: source.generation,
            siteID: "tx", sourceKey: "test", quality: "128k", musicInfo: Data("{}".utf8))
        let audio = try await service.resolve(request: request)
        XCTAssertEqual(audio.url.absoluteString, "https://media.invalid/track.mp3")
        XCTAssertEqual(audio.headers, ["X-Test": "public"])
        XCTAssertEqual(audio.expiresAt, Date(timeIntervalSince1970: 1900000000))
        let lyric = try await service.lyrics(request: request); XCTAssertEqual(lyric.text, "[00:00]line")
        _ = try await service.setScriptSourceEnabled(item.id, enabled: false)
        await scriptExpectFailure(.sourceUnavailable) { try await service.resolve(request: request) }
        _ = try await service.setScriptSourceEnabled(item.id, enabled: true)
        await scriptExpectFailure(.sourceUnavailable) { try await service.resolve(request: request) }
        let metrics = await runtime.metrics(); XCTAssertEqual(metrics.calls.filter { $0 == "resolve" }.count, 1)
        await service.close(); await runtime.close()
    }
    func testAllManagementOperationsForwardToTheSharedOwner() async throws {
        let runtime = MusicScriptTestRuntime(), service = NativeMusicScriptService(launcher: runtime)
        await service.start()
        let item = try await service.downloadScriptSource(from: URL(string: "https://source.invalid/source.js")!)
        _ = try await service.editScriptSource(item.id, name: "Renamed", networkURL: "https://source.invalid/new.js")
        _ = try await service.updateScriptSource(item.id)
        _ = try await service.updateScriptSource(item.id, data: musicScriptFixture, fileName: "new.js")
        _ = try await service.rollbackScriptSource(item.id)
        _ = try await service.checkScriptSource(item.id)
        _ = try await service.scriptSourceDetail(item.id)
        try await service.removeScriptSource(item.id)
        let methods = await runtime.metrics().calls
        for name in ["download", "edit", "update", "rollback", "check", "detail", "remove"] {
            XCTAssertTrue(methods.contains("sources." + name))
        }
        await service.close(); await runtime.close()
    }
    func testStorageFailuresAndHostFailureAreNotEmptySourceLists() async throws {
        let runtime = MusicScriptTestRuntime(), service = NativeMusicScriptService(launcher: runtime)
        await service.start()
        await runtime.fail("CORRUPT_STORAGE")
        await scriptExpectFailure(.corruptDocument) { try await service.scriptSources() }
        await runtime.fail("STORAGE_ERROR")
        await scriptExpectFailure(.storageFailed) { try await service.scriptSources() }
        await runtime.close()
        await scriptExpectFailure(.scriptRuntimeUnavailable) { try await service.sources() }
        await service.start()
        let connects = await runtime.metrics().connects; XCTAssertEqual(connects, 1)
        await service.close()
    }
}
