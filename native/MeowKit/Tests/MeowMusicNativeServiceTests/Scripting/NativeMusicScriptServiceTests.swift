import Foundation
import MeowMusicFeature
import MeowRuntime
import XCTest
@testable import MeowMusicNativeService

final class NativeMusicScriptServiceTests: XCTestCase {
    func testExplicitStartupIsSharedAndDoesNotExecuteScripts() async throws {
        let runtime = MusicScriptTestRuntime(), session = RuntimeSession(launcher: runtime)
        let service = NativeMusicScriptService(connection: { try await session.connection() })
        await scriptExpectFailure(.scriptRuntimeUnavailable) { try await service.scriptSources() }
        await service.start(); await service.start()
        let items = try await service.scriptSources()
        XCTAssertTrue(items.isEmpty)
        let metrics = await runtime.metrics()
        XCTAssertEqual(metrics.connects, 1); XCTAssertEqual(metrics.calls, ["runtime.describe", "sources.list"])
        let connection = try await session.connection()
        try await scriptAssertCatAvailable(connection)
        try await service.close()
        let shared = try await session.connection()
        XCTAssertTrue(connection === shared)
        try await scriptAssertCatAvailable(shared)
        let after = await runtime.metrics()
        XCTAssertEqual(after.connects, 1)
        XCTAssertEqual(after.calls.filter { $0 == "close" }.count, 1)
        await session.close(); await runtime.close()
    }
    func testImportPutsOneCompleteSourceThenSelectsItsRevisionWithoutNativeStorage() async throws {
        let runtime = MusicScriptTestRuntime(), session = RuntimeSession(launcher: runtime)
        let service = NativeMusicScriptService(connection: { try await session.connection() })
        await service.start()
        let bytes = Data(repeating: 65, count: 1024 * 1024 + 1)
        let item = try await service.importScriptSource(bytes, fileName: "selected.js")
        XCTAssertEqual(item.name, "Synthetic source"); XCTAssertFalse(item.enabled)
        let calls = await runtime.parameters
        let uploads = await runtime.uploads
        XCTAssertEqual(uploads.count, 1)
        let uploaded = try XCTUnwrap(uploads.first)
        XCTAssertEqual(uploaded.source, bytes)
        XCTAssertEqual(uploaded.basename, "selected.js")
        XCTAssertEqual(uploaded.installationID, item.id)
        XCTAssertGreaterThan(uploaded.revision, 0)
        let added = try XCTUnwrap(calls.first { $0.0 == "sources.add" }?.1)
        XCTAssertEqual(added.scriptField("installationId"), .string(uploaded.installationID))
        XCTAssertEqual(added.scriptField("sourceRevision"), .number(Double(uploaded.revision)))
        XCTAssertFalse(calls.contains { $0.0 == "sources.enable" })
        let listed = try await service.scriptSources(); XCTAssertEqual(listed, [item])
        try await service.close(); await session.close(); await runtime.close()
    }
    func testSingleQualityResolvePreservesHeadersExpiryAndFencesDisabledOrOldGeneration() async throws {
        let runtime = MusicScriptTestRuntime(), session = RuntimeSession(launcher: runtime)
        let service = NativeMusicScriptService(connection: { try await session.connection() })
        await service.start()
        let item = try await service.importScriptSource(musicScriptFixture, fileName: "source.js")
        _ = try await service.setScriptSourceEnabled(item.id, enabled: true)
        let source = try await service.source(installationID: item.id)
        XCTAssertEqual(source.capabilities.map(\.siteID), ["tx"])
        let request = MusicScriptRequest(source: source,
            siteID: "tx", sourceKey: "test", quality: "128k", musicInfo: Data("{}".utf8))
        let audio = try await service.resolve(request: request)
        XCTAssertEqual(audio.value.url.absoluteString, "https://media.invalid/track.mp3")
        XCTAssertEqual(audio.value.headers, ["X-Test": "public"])
        XCTAssertEqual(audio.value.expiresAt, Date(timeIntervalSince1970: 1900000000))
        let lyric = try await service.lyrics(request: request); XCTAssertEqual(lyric.value.text, "[00:00]line")
        _ = try await service.setScriptSourceEnabled(item.id, enabled: false)
        await scriptExpectFailure(.sourceUnavailable) { try await service.resolve(request: request) }
        _ = try await service.setScriptSourceEnabled(item.id, enabled: true)
        await scriptExpectFailure(.sourceUnavailable) { try await service.resolve(request: request) }
        let metrics = await runtime.metrics(); XCTAssertEqual(metrics.calls.filter { $0 == "resolve" }.count, 1)
        try await service.close(); await session.close(); await runtime.close()
    }
    func testAllManagementOperationsForwardToTheSharedOwner() async throws {
        let runtime = MusicScriptTestRuntime(), session = RuntimeSession(launcher: runtime)
        let service = NativeMusicScriptService(connection: { try await session.connection() })
        await service.start()
        let item = try await service.downloadScriptSource(from: URL(string: "https://source.invalid/source.js")!)
        _ = try await service.editScriptSource(item.id, name: "Renamed", networkURL: "https://source.invalid/new.js")
        _ = try await service.updateScriptSource(item.id)
        _ = try await service.rollbackScriptSource(item.id)
        _ = try await service.checkScriptSource(item.id)
        _ = try await service.scriptSourceDetail(item.id)
        try await service.removeScriptSource(item.id)
        let local = try await service.importScriptSource(musicScriptFixture, fileName: "local.js")
        _ = try await service.updateScriptSource(local.id, data: musicScriptFixture, fileName: "new.js")
        try await service.removeScriptSource(local.id)
        let uploads = await runtime.uploads
        XCTAssertEqual(uploads.count, 2)
        XCTAssertEqual(uploads.map(\.installationID), [local.id, local.id])
        let forwarded = await runtime.parameters
        let updates = forwarded.filter { $0.0 == "sources.update" }.map(\.1)
        XCTAssertEqual(updates.count, 2)
        XCTAssertNil(updates.first?.scriptField("sourceRevision"))
        XCTAssertEqual(updates.last?.scriptField("sourceRevision"), uploads.last.map { .number(Double($0.revision)) })
        let methods = await runtime.metrics().calls
        for name in ["download", "edit", "update", "rollback", "check", "detail", "remove"] {
            XCTAssertTrue(methods.contains("sources." + name))
        }
        try await service.close(); await session.close(); await runtime.close()
    }
    func testStorageFailuresAndHostFailureAreNotEmptySourceLists() async throws {
        let runtime = MusicScriptTestRuntime(), session = RuntimeSession(launcher: runtime)
        let service = NativeMusicScriptService(connection: { try await session.connection() })
        await service.start()
        await runtime.fail("CORRUPT_STORAGE")
        await scriptExpectFailure(.corruptDocument) { try await service.scriptSources() }
        await runtime.fail("STORAGE_ERROR")
        await scriptExpectFailure(.storageFailed) { try await service.scriptSources() }
        await runtime.close()
        await scriptExpectFailure(.scriptRuntimeUnavailable) { try await service.sources() }
        await service.start()
        let connects = await runtime.metrics().connects; XCTAssertEqual(connects, 1)
        await scriptExpectFailure(.scriptCleanupFailed) { try await service.close() }
        await session.close()
    }
    func testDirectoryReadsKeepIdleStartingAndFailedWithoutRunningOrRetryingScripts() async throws {
        for state in [MusicScriptSourceItem.State.idle, .starting, .failed] {
            let runtime = MusicScriptTestRuntime(), session = RuntimeSession(launcher: runtime)
            await runtime.directory(state)
            let service = NativeMusicScriptService(connection: { try await session.connection() })
            await service.start()
            let items = try await service.scriptSources()
            let sources = try await service.sources()
            let source = try await service.source(installationID: XCTUnwrap(items.first).id)
            XCTAssertEqual(items.first?.state, state)
            XCTAssertEqual(sources, [source])
            XCTAssertEqual(source.state, state); XCTAssertEqual(source.generation, 0)
            XCTAssertEqual(source.isCandidate, state != .failed)
            if state == .failed {
                let request = MusicScriptRequest(source: source, siteID: "tx", sourceKey: "test", quality: "128k", musicInfo: Data("{}".utf8))
                await scriptExpectFailure(.sourceUnavailable) { try await service.resolve(request: request) }
                await scriptExpectFailure(.sourceUnavailable) { try await service.lyrics(request: request) }
            }
            let calls = await runtime.metrics().calls
            XCTAssertFalse(calls.contains("sources.enable")); XCTAssertFalse(calls.contains("sources.check"))
            XCTAssertFalse(calls.contains("resolve")); XCTAssertFalse(calls.contains("invoke"))
            try await service.close(); await session.close()
        }
    }

    func testFirstResolveAndRawLyricInvocationLoadIdleAndReturnRealExecutionGeneration() async throws {
        for lyric in [false, true] {
            let runtime = MusicScriptTestRuntime(), session = RuntimeSession(launcher: runtime)
            await runtime.directory(.idle)
            let service = NativeMusicScriptService(connection: { try await session.connection() })
            await service.start()
            let sources = try await service.sources()
            let cold = try XCTUnwrap(sources.first)
            let request = MusicScriptRequest(source: cold, siteID: "tx", sourceKey: "test", quality: "128k", musicInfo: Data("{}".utf8))
            let executed: MusicScriptSource
            if lyric {
                let result = try await service.lyrics(request: request)
                XCTAssertEqual(result.value.text, "[00:00]line")
                executed = result.source
            } else {
                let result = try await service.resolve(request: request)
                XCTAssertEqual(result.value.url.absoluteString, "https://media.invalid/track.mp3")
                executed = result.source
            }
            XCTAssertEqual(cold.state, .idle); XCTAssertEqual(cold.generation, 0)
            XCTAssertEqual(executed.state, .ready); XCTAssertGreaterThan(executed.generation, 0)
            XCTAssertEqual(executed.identity, cold.identity)
            let live = try await service.source(installationID: cold.installationID)
            XCTAssertEqual(live, executed)
            let calls = await runtime.metrics().calls
            XCTAssertFalse(calls.contains("sources.enable"))
            XCTAssertEqual(calls.filter { $0 == (lyric ? "invoke" : "resolve") }.count, 1)
            try await service.close(); await session.close()
        }
    }

    func testFirstCheckLoadsIdleWithoutAnImplicitEnableAndPublishesTheRuntimeCheck() async throws {
        let runtime = MusicScriptTestRuntime(), session = RuntimeSession(launcher: runtime)
        await runtime.directory(.idle)
        let service = NativeMusicScriptService(connection: { try await session.connection() })
        await service.start()
        let sources = try await service.sources()
        let cold = try XCTUnwrap(sources.first)
        XCTAssertEqual(cold.state, .idle); XCTAssertEqual(cold.generation, 0)
        let checked = try await service.checkScriptSource(cold.installationID)
        XCTAssertTrue(checked.enabled); XCTAssertEqual(checked.state, .ready)
        XCTAssertEqual(checked.check?.summary, "normal")
        XCTAssertEqual(checked.check?.checkedAt, Date(timeIntervalSince1970: 1900000000))
        XCTAssertEqual(checked.check?.platforms, ["tx": "ok"])
        let loaded = try await service.source(installationID: cold.installationID)
        XCTAssertGreaterThan(loaded.generation, 0); XCTAssertEqual(loaded.identity, cold.identity)
        let calls = await runtime.metrics().calls
        XCTAssertEqual(calls.filter { $0 == "sources.check" }.count, 1)
        XCTAssertFalse(calls.contains("sources.enable"))
        XCTAssertFalse(calls.contains("resolve")); XCTAssertFalse(calls.contains("invoke"))
        try await service.close(); await session.close()
    }

    func testRawInvokeRejectsConcurrentContentOrGenerationChangeWithoutIdentityInTheResult() async throws {
        for change in ["content", "generation", "cold-enable"] {
            let runtime = MusicScriptTestRuntime(), session = RuntimeSession(launcher: runtime)
            await runtime.directory(change == "cold-enable" ? .idle : .ready)
            let service = NativeMusicScriptService(connection: { try await session.connection() })
            await service.start()
            let sources = try await service.sources()
            let before = try XCTUnwrap(sources.first)
            let gate = NativeMusicGate()
            await runtime.hold("invoke", gate)
            let request = MusicScriptRequest(source: before, siteID: "tx", sourceKey: "test", quality: "", musicInfo: Data("{}".utf8))
            let pending = Task { try await service.lyrics(request: request) }
            await gate.started()
            if change == "content" { await runtime.replaceContent() }
            else if change == "generation" { await runtime.reload() }
            else { _ = try await service.setScriptSourceEnabled(before.installationID, enabled: true) }
            await gate.release()
            await scriptExpectFailure(.sourceUnavailable) { try await pending.value }
            let calls = await runtime.metrics().calls
            XCTAssertEqual(calls.filter { $0 == "invoke" }.count, 1)
            try await service.close(); await session.close()
        }
    }

    func testCompleteValuesKeepUnicodeKeysAndBusinessFieldsBeyondOldResultAndParameterSizes() async throws {
        let runtime = MusicScriptTestRuntime(), session = RuntimeSession(launcher: runtime)
        await runtime.directory(.ready)
        let client = try await session.connection()
        let transport = MusicScriptTransport(connection: client)
        let text = String(repeating: "音", count: 3 * 1024 * 1024)
        let result = try RuntimeJSON.decode(Data(#"{"\u00e9":1,"e\u0301":2,"blob":"业务","token":null}"#.utf8))
        for value in [result, .array([.string(text), .null]), .string("中文原始值"), .null] {
            await runtime.setLyric(value)
            let reply = try await transport.call("invoke", ["installationId": .string(await runtime.id),
                "siteId": .string("tx"), "operation": .string("lyric"),
                "payload": .object(["musicInfo": result, "text": .string(text)])])
            XCTAssertEqual(reply, value)
        }
        let parameters = await runtime.parameters
        XCTAssertEqual(parameters.count, 4)
        for (_, value) in parameters {
            XCTAssertEqual(value.scriptField("payload")?.scriptField("musicInfo"), result)
            XCTAssertEqual(value.scriptField("payload")?.scriptField("text"), .string(text))
        }
        if case .object(let fields) = result { XCTAssertEqual(fields.count, 4) }
        else { XCTFail("object required") }
        await session.close()
    }

    func testMusicInfoUsesStrictRuntimeJSONWithoutCanonicalKeyMerging() throws {
        let bytes = Data(#"{"\u00e9":"composed","e\u0301":"decomposed","中文":null}"#.utf8)
        let value = try NativeMusicScriptService.musicInfo(bytes)
        guard case .object(let fields) = value else { return XCTFail("object required") }
        XCTAssertEqual(fields.count, 3)
        XCTAssertEqual(fields["\u{e9}"], .string("composed"))
        XCTAssertEqual(fields["e\u{301}"], .string("decomposed"))
        XCTAssertEqual(try RuntimeJSON.decode(RuntimeJSON.encode(value)), value)
        for invalid in [Data([0xff]), Data(#"{"key":1,"key":2}"#.utf8), Data(#"{"n":9007199254740992}"#.utf8), Data("null".utf8)] {
            XCTAssertThrowsError(try NativeMusicScriptService.musicInfo(invalid)) {
                XCTAssertEqual($0 as? MusicServiceError, .invalidRequest)
            }
        }
    }

    func testCurrentSourceAndMediaErrorsKeepTheirBusinessMeaning() async throws {
        let runtime = MusicScriptTestRuntime(), session = RuntimeSession(launcher: runtime)
        let service = NativeMusicScriptService(connection: { try await session.connection() })
        await service.start()
        let errors: [(String, MusicServiceError)] = [
            ("INVALID_SOURCE_URL", .insecureScriptURL), ("SOURCE_DOWNLOAD_FAILED", .scriptDownloadFailed),
            ("SOURCE_REDIRECT", .scriptRedirect), ("TARGET_CHANGED", .targetChanged), ("RECORDING_MISMATCH", .targetChanged),
            ("EXPIRED_MEDIA_CANDIDATE", .invalidMediaCandidate), ("MEDIA_HEADERS_UNSUPPORTED", .invalidMediaCandidate),
            ("PERMISSION_DENIED", .permissionDenied), ("SCRIPT_PROTOCOL_ERROR", .invalidScript),
        ]
        for (code, expected) in errors {
            await runtime.fail(code)
            await scriptExpectFailure(expected) { try await service.scriptSources() }
        }
        await runtime.fail(nil)
        try await service.close(); await session.close()
    }

    func testLyricsValidateTheRawBusinessObjectWithoutLegacyIdentityUnwrapping() async throws {
        let runtime = MusicScriptTestRuntime(), session = RuntimeSession(launcher: runtime)
        await runtime.directory(.ready)
        let service = NativeMusicScriptService(connection: { try await session.connection() })
        await service.start()
        let sources = try await service.sources(), source = try XCTUnwrap(sources.first)
        let request = MusicScriptRequest(source: source, siteID: "tx", sourceKey: "test", quality: "", musicInfo: Data("{}".utf8))
        let invalidValues: [RuntimeValue] = [.null, .array([]), .string("lyric"),
            .object(["value": .object(["lyric": .string("wrapped")])]),
            .object(["lyric": .string("line"), "tlyric": .number(1)])]
        for invalid in invalidValues {
            await runtime.setLyric(invalid)
            await scriptExpectFailure(.invalidLyricResponse) { try await service.lyrics(request: request) }
        }
        await runtime.setLyric(.object(["lyric": .string("[00:00]正文"), "tlyric": .string("[00:00]译文"), "rlyric": .null]))
        let result = try await service.lyrics(request: request)
        XCTAssertEqual(result.value.text, "[00:00]正文")
        XCTAssertEqual(result.value.translation, "[00:00]译文")
        XCTAssertEqual(result.value.romanization, "")
        XCTAssertEqual(result.source, source)
        try await service.close(); await session.close()
    }

}
