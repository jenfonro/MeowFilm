import Foundation
import MeowMusicFeature
@testable import MeowRuntime
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
            ("SOURCE_REDIRECT", .scriptRedirect), ("TARGET_CHANGED", .targetChanged), ("RECORDING_MISMATCH", .invalidMediaCandidate),
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

extension NativeMusicScriptServiceTests {
    func testResolveProjectsReceiptDurationWithoutInventingMeasuredQuality() async throws {
        let runtime = ScriptMediaReceiptRuntime()
        let session = RuntimeSession(launcher: MusicScriptHTTPTestRuntime { try await runtime.respond($0, $1) })
        let service = NativeMusicScriptService(connection: { try await session.connection() })
        await service.start()
        let source = try await service.source(installationID: ScriptMediaReceiptRuntime.id)
        let request = MusicScriptRequest(source: source, siteID: "tx", sourceKey: "recording", quality: "flac",
            musicInfo: Data(#"{"duration":180,"songmid":"123"}"#.utf8))
        let durations: [RuntimeValue?] = [.number(185), .number(190), .number(180.125), .number(0), .null, nil]
        for duration in durations {
            await runtime.reply(duration: duration)
            let execution = try await service.resolve(request: request), audio = execution.value
            let expected: Double?
            if case .number(let value)? = duration, value > 0 { expected = value } else { expected = nil }
            XCTAssertEqual(audio.duration, expected)
            XCTAssertNil(audio.delivery, "The requested flac level is not measured quality.")
            XCTAssertEqual(audio.url.absoluteString, "https://media.invalid/receipt.mp3")
            XCTAssertEqual(audio.headers, ["X-Test": "receipt"])
            XCTAssertEqual(audio.expiresAt, Date(timeIntervalSince1970: 1900000000))
            XCTAssertTrue(audio.fileCachePolicy.allowed)
            XCTAssertEqual(execution.source.state, .ready)
            XCTAssertGreaterThan(execution.source.generation, 0)
            XCTAssertEqual(execution.source.identity, source.identity)
        }
        let requests = await runtime.requests
        XCTAssertEqual(requests.count, 6)
        for parameters in requests {
            XCTAssertEqual(parameters.scriptField("musicInfo")?.scriptField("duration"), .number(180))
            XCTAssertEqual(parameters.scriptField("quality"), .string("flac"))
        }
        try await service.close(); await session.close()
    }

    func testResolveRejectsMalformedReceiptDurationAndOwnerRecordingMismatchAsMediaFailures() async throws {
        let runtime = ScriptMediaReceiptRuntime()
        let session = RuntimeSession(launcher: MusicScriptHTTPTestRuntime { try await runtime.respond($0, $1) })
        let service = NativeMusicScriptService(connection: { try await session.connection() })
        await service.start()
        let source = try await service.source(installationID: ScriptMediaReceiptRuntime.id)
        let request = MusicScriptRequest(source: source, siteID: "tx", sourceKey: "recording", quality: "flac",
            musicInfo: Data(#"{"duration":180}"#.utf8))
        let durations: [RuntimeValue] = [.number(-1), .string("185"), .bool(true), .object([:])]
        for duration in durations {
            await runtime.reply(duration: duration)
            await scriptExpectFailure(.invalidMediaCandidate) { try await service.resolve(request: request) }
        }
        await runtime.reply(code: "RECORDING_MISMATCH")
        await scriptExpectFailure(.invalidMediaCandidate) { try await service.resolve(request: request) }
        try await service.close(); await session.close()
    }

    func testResolveRejectsWrongReceiptIdentityAfterFirstLazyLoad() async throws {
        let mismatches: [(String, RuntimeValue)] = [
            ("installationId", .string("22222222-2222-4222-8222-222222222222")),
            ("generation", .number(2)), ("contentHash", .string(String(repeating: "c", count: 64))),
        ]
        for (field, value) in mismatches {
            let runtime = ScriptMediaReceiptRuntime()
            await runtime.receiptIdentity(field, value: value)
            let session = RuntimeSession(launcher: MusicScriptHTTPTestRuntime { try await runtime.respond($0, $1) })
            let service = NativeMusicScriptService(connection: { try await session.connection() })
            await service.start()
            let cold = try await service.source(installationID: ScriptMediaReceiptRuntime.id)
            XCTAssertEqual(cold.state, .idle); XCTAssertEqual(cold.generation, 0)
            await scriptExpectFailure(.sourceUnavailable) {
                try await service.resolve(request: .init(source: cold, siteID: "tx", sourceKey: "recording",
                    quality: "flac", musicInfo: Data("{}".utf8)))
            }
            let loaded = try await service.source(installationID: cold.installationID)
            XCTAssertEqual(loaded.state, .ready); XCTAssertEqual(loaded.generation, 1)
            let requests = await runtime.requests
            XCTAssertEqual(requests.count, 1)
            try await service.close(); await session.close()
        }
    }

    func testResolveRejectsConcurrentContentGenerationOrDisableAfterFirstLazyLoad() async throws {
        for change in ["content", "generation", "disable"] {
            let gate = NativeMusicGate(), runtime = ScriptMediaReceiptRuntime()
            await runtime.hold(gate)
            let session = RuntimeSession(launcher: MusicScriptHTTPTestRuntime { try await runtime.respond($0, $1) })
            let service = NativeMusicScriptService(connection: { try await session.connection() })
            await service.start()
            let cold = try await service.source(installationID: ScriptMediaReceiptRuntime.id)
            let request = MusicScriptRequest(source: cold, siteID: "tx", sourceKey: "recording",
                quality: "flac", musicInfo: Data("{}".utf8))
            let pending = Task { try await service.resolve(request: request) }
            await gate.started()
            await runtime.change(change)
            await gate.release()
            await scriptExpectFailure(.sourceUnavailable) { try await pending.value }
            let requests = await runtime.requests
            XCTAssertEqual(requests.count, 1)
            try await service.close(); await session.close()
        }
    }

    func testEnabledSourcesAbortGlobalFailuresWithoutReturningPartialResults() async throws {
        let failures: [(String, MusicServiceError)] = [
            ("CANCELLED", .cancelled), ("PERMISSION_DENIED", .permissionDenied),
            ("CORRUPT_STORAGE", .corruptDocument), ("STORAGE_ERROR", .storageFailed),
            ("SCRIPT_PROTOCOL_ERROR", .invalidScript), ("INVALID_REQUEST", .invalidRequest),
            ("TARGET_CHANGED", .targetChanged), ("CLOSED", .scriptRuntimeUnavailable),
        ]
        for operation in ["sources.list", "sources.detail"] {
            for (code, expected) in failures {
                let runtime = EnabledSourcesRuntime(operation: operation, fault: .code(code))
                let session = RuntimeSession(launcher: MusicScriptHTTPTestRuntime { try await runtime.respond($0, $1) })
                let service = NativeMusicScriptService(connection: { try await session.connection() })
                await service.start()
                await scriptExpectDeclarationFailure(expected, wrapped: operation == "sources.detail" && code == "PERMISSION_DENIED") {
                    if operation == "sources.list" { _ = try await service.sources() }
                    else { _ = try await service.source(installationID: EnabledSourcesRuntime.bad) }
                }
                let requested = await runtime.details, methods = await runtime.methods
                XCTAssertEqual(requested, operation == "sources.detail" ? [EnabledSourcesRuntime.bad] : [])
                XCTAssertFalse(methods.contains("sources.enable"))
                XCTAssertFalse(methods.contains("resolve"))
                try await service.close(); await session.close()
            }
        }
    }

    func testEnabledSourcesAbortMalformedResponsesAtListAndDetail() async throws {
        for operation in ["sources.list", "sources.detail"] {
            for fault in [EnabledSourcesRuntime.Fault.envelope, .item, .snapshot, .capabilities] {
                let runtime = EnabledSourcesRuntime(operation: operation, fault: fault)
                let session = RuntimeSession(launcher: MusicScriptHTTPTestRuntime { try await runtime.respond($0, $1) })
                let service = NativeMusicScriptService(connection: { try await session.connection() })
                await service.start()
                await scriptExpectFailure(.invalidResponse) {
                    if operation == "sources.list" { _ = try await service.sources() }
                    else { _ = try await service.source(installationID: EnabledSourcesRuntime.bad) }
                }
                let requested = await runtime.details, methods = await runtime.methods
                XCTAssertEqual(requested, operation == "sources.detail" ? [EnabledSourcesRuntime.bad] : [])
                XCTAssertFalse(methods.contains("sources.enable"))
                try await service.close(); await session.close()
            }
        }
    }

    func testEnabledSourcesIsolateBrokenScriptsAndKeepHealthySources() async throws {
        let failures: [(String, MusicServiceError)] = [
            ("SCRIPT_FAILURE", .scriptFailed), ("SCRIPT_ENCODING", .invalidScript), ("SCRIPT_INTEGRITY", .invalidScript),
            ("SCRIPT_PROTOCOL_ERROR", .invalidScript), ("LIMIT", .limit), ("SCRIPT_CLEANUP", .scriptCleanupFailed),
            ("NETWORK_ERROR", .upstreamFailed), ("UPSTREAM_HTTP", .upstreamFailed), ("UPSTREAM_FORMAT", .upstreamInvalid),
            ("TIMEOUT", .timeout), ("STALE_SOURCE", .sourceUnavailable), ("SOURCE_NOT_FOUND", .sourceUnavailable),
            ("SOURCE_DISABLED", .sourceUnavailable), ("CAPABILITY_UNAVAILABLE", .sourceUnavailable),
            ("INVALID_MEDIA_CANDIDATE", .invalidMediaCandidate), ("MEDIA_UNAVAILABLE", .invalidMediaCandidate),
        ]
        for failure in [Optional<(String, MusicServiceError)>.none] + failures.map({ Optional($0) }) {
            let runtime = EnabledSourcesRuntime(operation: "resolve", fault: failure.map { .code($0.0) })
            let session = RuntimeSession(launcher: MusicScriptHTTPTestRuntime { try await runtime.respond($0, $1) })
            let service = NativeMusicScriptService(connection: { try await session.connection() })
            await service.start()
            let initial = try await service.sources()
            XCTAssertEqual(initial.filter(\.isCandidate).map(\.installationID), EnabledSourcesRuntime.ids)
            let source = try XCTUnwrap(initial.first { $0.installationID == EnabledSourcesRuntime.bad })
            let request = MusicScriptRequest(source: source, siteID: "tx", sourceKey: "recording",
                quality: "128k", musicInfo: Data("{}".utf8))
            let expectedCandidates: [String], expectedState: MusicScriptSourceItem.State
            if let failure {
                await scriptExpectFailure(failure.1) { try await service.resolve(request: request) }
                expectedCandidates = [EnabledSourcesRuntime.before, EnabledSourcesRuntime.after]
                expectedState = .failed
            } else {
                _ = try await service.resolve(request: request)
                expectedCandidates = EnabledSourcesRuntime.ids
                expectedState = .ready
            }
            let sources = try await service.sources()
            XCTAssertEqual(sources.map(\.installationID), EnabledSourcesRuntime.ids)
            XCTAssertEqual(sources.filter(\.isCandidate).map(\.installationID), expectedCandidates)
            XCTAssertEqual(sources.first { $0.installationID == EnabledSourcesRuntime.bad }?.state, expectedState)
            let methods = await runtime.methods
            XCTAssertEqual(methods.filter { $0 == "resolve" }.count, 1)
            XCTAssertFalse(methods.contains("sources.enable"))
            try await service.close(); await session.close()
        }
    }

    func testEnabledSourcesTaskCancellationAbortsPendingListAndDetail() async throws {
        for operation in ["sources.list", "sources.detail"] {
            let gate = NativeMusicGate()
            let runtime = EnabledSourcesRuntime(operation: operation, gate: gate)
            let session = RuntimeSession(launcher: MusicScriptHTTPTestRuntime { try await runtime.respond($0, $1) })
            let service = NativeMusicScriptService(connection: { try await session.connection() })
            await service.start()
            let task = Task {
                if operation == "sources.list" { _ = try await service.sources() }
                else { _ = try await service.source(installationID: EnabledSourcesRuntime.bad) }
            }
            await gate.started()
            task.cancel()
            await scriptExpectFailure(.cancelled) { try await task.value }
            await gate.release()
            let requested = await runtime.details, methods = await runtime.methods
            XCTAssertEqual(requested, operation == "sources.detail" ? [EnabledSourcesRuntime.bad] : [])
            XCTAssertFalse(methods.contains("sources.enable"))
            try await service.close(); await session.close()
        }
    }

    func testDiscoveryScopePropagatesTransientDeclarationFailuresBeforeSearchOrResolve() async throws {
        let failures: [(EnabledSourcesRuntime.Fault, MusicServiceError)] = [
            (.snapshot, .invalidResponse), (.code("SCRIPT_PROTOCOL_ERROR"), .invalidScript),
            (.code("INVALID_REQUEST"), .invalidRequest), (.code("PERMISSION_DENIED"), .permissionDenied),
        ]
        for entry in ScopeEntry.allCases {
            for (fault, expected) in failures {
                try await discoveryScope(entry, fault: fault, expectedFailure: expected)
            }
        }
    }

    func testDiscoveryScopeKeepsHealthyOfficialAfterScriptOrHostFailure() async throws {
        for entry in ScopeEntry.allCases {
            for code in ["SCRIPT_FAILURE", "CLOSED"] {
                try await discoveryScope(entry, fault: .code(code), expectedFailure: nil)
            }
        }
    }

    private enum ScopeEntry: CaseIterable { case legacy, automatic, explicit }
    private func discoveryScope(_ entry: ScopeEntry, fault: EnabledSourcesRuntime.Fault,
                                expectedFailure: MusicServiceError?) async throws {
        // Scope reads the complete directory now; it never implicitly enables
        // or probes each installation through sources.detail.
        let scopeListing = entry == .automatic ? 2 : 1
        let runtime = EnabledSourcesRuntime(operation: "sources.list", fault: fault, faultOnListing: scopeListing)
        let runtimeSession = RuntimeSession(launcher: MusicScriptHTTPTestRuntime { try await runtime.respond($0, $1) })
        let scripts = NativeMusicScriptService(connection: { try await runtimeSession.connection() })
        await scripts.start()
        let clock = MusicRecoveryTestClock()
        let accounts = MusicRecoveryTestAccounts(platform: .qq, nativeID: "101", clock: clock)
        let qq = MusicNativeCandidateDiscoveryQQ(items: [MusicNativeCandidateDiscoveryFixture.track("tx", id: "101")])
        let http = MusicNativeCandidateDiscoveryHTTP()
        let song = try MusicModel.normalize(MusicNativeCandidateDiscoveryFixture.track("kw", id: "123"))
        let service = NativeMusicService(document: try MusicNativeCandidateDiscoveryFixture.store(song: song),
            scripts: scripts, accounts: accounts, qq: qq, enabledPlatforms: [.qq, .kuwo],
            transport: http, clock: { clock.now() })
        try await service.setAccess(available: true, generation: 1)
        let context = "\(entry) \(String(describing: fault))"
        var selection: MusicPlaybackSelection?
        do {
            switch entry {
            case .legacy:
                let session = try await service.beginCandidateDiscovery(occurrenceID: "entry-1", expectedQueueRevision: 1)
                do {
                    let snapshot = try await session.snapshot(discover: true, force: false)
                    let option = try XCTUnwrap(snapshot.options.first { $0.playback.installationId.hasPrefix("official:") })
                    try await session.commitDiscovery()
                    let target = option.target(quality: "flac")
                    _ = try await session.resolve(target, attemptID: UUID().uuidString.lowercased())
                    selection = target.selection
                    await session.close(retainingAttemptID: nil)
                } catch {
                    await session.close(retainingAttemptID: nil)
                    throw error
                }
            case .automatic, .explicit:
                let session = try await service.sourceSession(occurrenceID: "entry-1", expectedQueueRevision: 1)
                if entry == .explicit {
                    let updates = await session.updates()
                    _ = try await session.loadMore(refresh: false)
                    try await MusicCandidateDiscoveryDeadline.run(seconds: 3, timing: .init()) {
                        for try await value in updates where !value.isLoading && !value.sources.isEmpty { return }
                        throw MusicServiceError.closed
                    }
                }
                let result = try await session.match(intent: .automatic)
                selection = result.selection
                await service.discardSource(attemptID: result.candidate.attemptId)
            }
            XCTAssertNil(expectedFailure, "Declaration failure was swallowed: \(context)")
            XCTAssertEqual(selection?.siteId, "tx", context)
            XCTAssertEqual(selection?.quality, "flac", context)
            XCTAssertTrue(selection?.installationId.hasPrefix("official:") == true, context)
        } catch {
            XCTAssertEqual(MusicServiceError.sanitized(error), expectedFailure, context)
        }
        let searches = await qq.calls(), otherSearches = await http.calls()
        let accountsCalls = await accounts.counts(), scriptResolves = await runtime.resolutions
        let injected = await runtime.faultsApplied, listings = await runtime.listings
        XCTAssertEqual(injected, 1, "The fault must reach the first scope enumeration: \(context)")
        XCTAssertTrue(otherSearches.isEmpty, context)
        XCTAssertEqual(scriptResolves, 0, context)
        if expectedFailure != nil {
            XCTAssertTrue(searches.isEmpty, "A declaration contract/permission failure cannot authorize search: \(context)")
            XCTAssertEqual(accountsCalls.resolves, 0, context)
            XCTAssertEqual(listings, scopeListing, "Do not hide the failure behind a second enumeration: \(context)")
            XCTAssertNil(selection, context)
        } else {
            XCTAssertEqual(searches.count, 1, context)
            XCTAssertEqual(accountsCalls.resolves, 1, context)
            XCTAssertNotNil(selection, context)
        }
        let history = try await service.playedSongs(), exclusions = try await service.sourceExclusions()
        XCTAssertTrue(history.isEmpty, context)
        XCTAssertTrue(exclusions.isEmpty, context)
        let recovered = try await scripts.sources()
        XCTAssertEqual(recovered.map(\.installationID), EnabledSourcesRuntime.ids, context)
        let methods = await runtime.methods
        XCTAssertFalse(methods.contains("sources.enable"), context)
        await service.close(); try await scripts.close(); await runtimeSession.close()
    }
}

private func scriptExpectDeclarationFailure<T>(_ expected: MusicServiceError, wrapped: Bool,
    file: StaticString = #filePath, line: UInt = #line, _ body: () async throws -> T) async {
    do { _ = try await body(); XCTFail("Expected \(expected)", file: file, line: line) }
    catch {
        XCTAssertEqual(MusicServiceError.sanitized(error), expected, file: file, line: line)
        XCTAssertEqual(error is MusicSourceDeclarationFailure, wrapped, file: file, line: line)
    }
}

private actor ScriptMediaReceiptRuntime {
    static let id = "11111111-1111-4111-8111-111111111111"
    private var duration: RuntimeValue?
    private var code: String?
    private var identity: RuntimeObject = [:]
    private var enabled = true, generation = 0
    private var content = String(repeating: "a", count: 64)
    private var gate: NativeMusicGate?
    var requests: [RuntimeValue] = []
    func reply(duration: RuntimeValue? = nil, code: String? = nil) { self.duration = duration; self.code = code }
    func receiptIdentity(_ field: String, value: RuntimeValue) { identity[field] = value }
    func hold(_ gate: NativeMusicGate) { self.gate = gate }
    func change(_ change: String) {
        if change == "content" { content = String(repeating: "c", count: 64) }
        else if change == "generation" { generation += 1 }
        else { enabled = false; generation = 0 }
    }
    func respond(_ method: String, _ value: RuntimeValue) async throws -> RuntimeValue {
        if method == "sources.detail" {
            return MusicScriptHTTPTestRuntime.success(.object(["item": .object([
                "installationId": .string(Self.id), "version": .string("1"), "enabled": .bool(enabled),
                "state": .string(!enabled ? "disabled" : generation == 0 ? "idle" : "ready"),
                "generation": .number(Double(generation)), "contentHash": .string(content),
                "executionHash": .string(String(repeating: "b", count: 64)),
                "sites": .object(["tx": .object(["actions": .array([.string("musicUrl")]),
                    "qualitys": .array([.string("flac")])])]),
            ])]))
        }
        guard method == "resolve" else { throw RuntimeHTTPError.invalidRequest }
        requests.append(value)
        if generation == 0 { generation = 1 }
        if let code { return MusicScriptHTTPTestRuntime.failure(code) }
        var fields: RuntimeObject = [
            "installationId": .string(Self.id), "generation": .number(Double(generation)),
            "contentHash": .string(content), "platform": value.scriptField("platform")!,
            "quality": value.scriptField("quality")!, "url": .string("https://media.invalid/receipt.mp3"),
            "headers": .object(["X-Test": .string("receipt")]), "expiresAt": .number(1900000000000),
        ]
        fields["duration"] = duration
        for (key, value) in identity { fields[key] = value }
        let result = MusicScriptHTTPTestRuntime.success(.object(fields))
        if let gate { await gate.wait(); try Task.checkCancellation() }
        return result
    }
}

private actor EnabledSourcesRuntime {
    enum Fault: Sendable { case code(String), envelope, item, snapshot, capabilities }
    static let before = "11111111-1111-4111-8111-111111111111"
    static let bad = "22222222-2222-4222-8222-222222222222"
    static let after = "33333333-3333-4333-8333-333333333333"
    static let ids = [before, bad, after]
    private let operation: String
    private let fault: Fault?
    private let gate: NativeMusicGate?
    private let faultOnListing: Int?
    private var failed = false
    var details: [String] = [], methods: [String] = []
    var listings = 0, resolutions = 0, faultsApplied = 0

    init(operation: String, fault: Fault? = nil, gate: NativeMusicGate? = nil, faultOnListing: Int? = nil) {
        self.operation = operation; self.fault = fault; self.gate = gate; self.faultOnListing = faultOnListing
    }
    private func item(_ id: String) -> RuntimeObject {
        let bad = id == Self.bad && failed
        return [
            "installationId": .string(id), "name": .string("Synthetic source"), "version": .string("1"),
            "origin": .object(["kind": .string("file")]), "enabled": .bool(true),
            "state": .string(bad ? "failed" : "ready"), "generation": .number(bad ? 0 : 1),
            "contentHash": .string(String(repeating: "a", count: 64)),
            "executionHash": .string(String(repeating: "b", count: 64)),
            "sites": .object(["tx": .object([
                "actions": .array([.string("musicUrl")]), "qualitys": .array([.string("128k")]),
            ])]),
        ]
    }
    func respond(_ method: String, _ parameters: RuntimeValue) async throws -> RuntimeValue {
        methods.append(method)
        if method == "sources.list" { listings += 1 }
        if method == "resolve" { resolutions += 1 }
        let id = parameters.scriptField("installationId")?.scriptText ?? Self.bad
        if method == "sources.detail" { details.append(id) }
        var value = item(id)
        if method == operation && (method == "sources.list" || id == Self.bad) &&
            (faultOnListing == nil || listings == faultOnListing) {
            faultsApplied += 1
            if let gate { await gate.wait(); try Task.checkCancellation() }
            switch fault {
            case .code(let code)?:
                if method == "resolve" { failed = true }
                return MusicScriptHTTPTestRuntime.failure(code)
            case .envelope?: return .object(["ok": .bool(true), "value": .object([:])])
            case .item?:
                if method == "sources.list" {
                    return MusicScriptHTTPTestRuntime.success(.object(["items": .array([.object(item(Self.before)), .null,
                        .object(item(Self.after))])]))
                }
                return MusicScriptHTTPTestRuntime.success(.object(["item": .null]))
            case .snapshot?: value["generation"] = nil
            case .capabilities?: value["sites"] = .object(["tx": .object(["actions": .array([]), "qualitys": .number(1)])])
            case nil: break
            }
        }
        if method == "sources.list" {
            return MusicScriptHTTPTestRuntime.success(.object(["items": .array(Self.ids.map {
                $0 == Self.bad ? .object(value) : .object(item($0))
            })]))
        }
        if method == "resolve" {
            return MusicScriptHTTPTestRuntime.success(.object([
                "installationId": .string(id), "generation": .number(1),
                "contentHash": .string(String(repeating: "a", count: 64)),
                "platform": parameters.scriptField("platform")!, "quality": parameters.scriptField("quality")!,
                "url": .string("https://media.invalid/healthy.mp3"), "headers": .object([:]),
            ]))
        }
        return MusicScriptHTTPTestRuntime.success(.object(["item": .object(value)]))
    }
}

extension NativeMusicScriptServiceTests {
    func testColdDeclarationAndExplicitDisableKeepUserOrderWithoutBlockingOtherInstallations() async throws {
        for heldMethod in ["sources.detail", "resolve"] {
            let gate = NativeMusicGate(), runtime = DeclarationOrderRuntime(heldMethod: heldMethod, gate: gate)
            let session = RuntimeSession(launcher: MusicScriptHTTPTestRuntime { try await runtime.respond($0, $1) })
            let service = NativeMusicScriptService(connection: { try await session.connection() })
            await service.start()
            let initial: MusicScriptSource?
            if heldMethod == "resolve" { initial = try await service.source(installationID: DeclarationOrderRuntime.first) }
            else { initial = nil }
            let loading = Task {
                if let initial {
                    let execution = try await service.resolve(request: .init(source: initial, siteID: "tx", sourceKey: "recording",
                        quality: "flac", musicInfo: Data("{}".utf8)))
                    return execution.source
                }
                return try await service.source(installationID: DeclarationOrderRuntime.first)
            }
            await gate.started()
            let disabled = try await service.setScriptSourceEnabled(DeclarationOrderRuntime.first, enabled: false)
            XCTAssertFalse(disabled.enabled)
            let other = try await service.setScriptSourceEnabled(DeclarationOrderRuntime.second, enabled: true)
            XCTAssertTrue(other.enabled)
            await gate.release()
            if heldMethod == "resolve" {
                await scriptExpectFailure(.sourceUnavailable) { try await loading.value }
            } else {
                let staleRead = try await loading.value
                XCTAssertEqual(staleRead.state, .idle); XCTAssertEqual(staleRead.generation, 0)
                await scriptExpectFailure(.sourceUnavailable) {
                    try await service.resolve(request: .init(source: staleRead, siteID: "tx", sourceKey: "recording",
                        quality: "flac", musicInfo: Data("{}".utf8)))
                }
            }
            let detail = try await service.scriptSourceDetail(DeclarationOrderRuntime.first)
            XCTAssertFalse(detail.item.enabled); XCTAssertEqual(detail.item.state, .disabled)
            let snapshot = try await service.source(installationID: DeclarationOrderRuntime.first)
            XCTAssertFalse(snapshot.isCandidate); XCTAssertEqual(snapshot.generation, 0)
            let calls = await runtime.firstCalls
            XCTAssertEqual(calls, heldMethod == "resolve" ?
                ["sources.detail", "sources.detail", "resolve", "sources.disable", "sources.detail", "sources.detail", "sources.detail"] :
                ["sources.detail", "sources.disable", "sources.detail", "sources.detail", "sources.detail"])
            XCTAssertFalse(calls.contains("sources.enable"))
            try await service.close(); await session.close()
        }
    }

    func testDeclarationSubmittedAfterDisableCannotReloadTheInstallation() async throws {
        let gate = NativeMusicGate(), runtime = DeclarationOrderRuntime(heldMethod: "sources.disable", gate: gate)
        let session = RuntimeSession(launcher: MusicScriptHTTPTestRuntime { try await runtime.respond($0, $1) })
        let service = NativeMusicScriptService(connection: { try await session.connection() })
        await service.start()
        let disabling = Task { try await service.setScriptSourceEnabled(DeclarationOrderRuntime.first, enabled: false) }
        await gate.started()
        let reading = try await service.source(installationID: DeclarationOrderRuntime.first)
        XCTAssertEqual(reading.state, .idle); XCTAssertEqual(reading.generation, 0)
        let other = try await service.setScriptSourceEnabled(DeclarationOrderRuntime.second, enabled: true)
        XCTAssertTrue(other.enabled)
        await gate.release()
        let disabled = try await disabling.value
        XCTAssertFalse(disabled.enabled)
        let after = try await service.source(installationID: DeclarationOrderRuntime.first)
        XCTAssertFalse(after.isCandidate); XCTAssertEqual(after.state, .disabled)
        await scriptExpectFailure(.sourceUnavailable) {
            try await service.resolve(request: .init(source: reading, siteID: "tx", sourceKey: "recording",
                quality: "flac", musicInfo: Data("{}".utf8)))
        }
        let calls = await runtime.firstCalls
        XCTAssertEqual(calls, ["sources.disable", "sources.detail", "sources.detail", "sources.detail"])
        try await service.close(); await session.close()
    }

    func testCancelledConcurrentDeclarationDoesNotLoadAndDoesNotBlockLaterDisable() async throws {
        let firstGate = NativeMusicGate(), waitingGate = NativeMusicGate()
        let runtime = DeclarationOrderRuntime(heldMethod: "sources.detail", gate: firstGate)
        let session = RuntimeSession(launcher: MusicScriptHTTPTestRuntime { try await runtime.respond($0, $1) })
        let service = NativeMusicScriptService(connection: { try await session.connection() })
        await service.start()
        let loading = Task { try await service.source(installationID: DeclarationOrderRuntime.first) }
        await firstGate.started()
        await runtime.hold("sources.detail", waitingGate)
        let waiting = Task { try await service.source(installationID: DeclarationOrderRuntime.first) }
        await waitingGate.started()
        _ = try await service.scriptSourceDetail(DeclarationOrderRuntime.second)
        waiting.cancel()
        await scriptExpectFailure(.cancelled) { try await waiting.value }
        let disabled = try await service.setScriptSourceEnabled(DeclarationOrderRuntime.first, enabled: false)
        XCTAssertFalse(disabled.enabled)
        await firstGate.release(); await waitingGate.release()
        let staleRead = try await loading.value
        XCTAssertEqual(staleRead.state, .idle)
        let live = try await service.source(installationID: DeclarationOrderRuntime.first)
        XCTAssertFalse(live.isCandidate); XCTAssertEqual(live.state, .disabled)
        let calls = await runtime.firstCalls
        XCTAssertEqual(calls, ["sources.detail", "sources.detail", "sources.disable", "sources.detail"])
        try await service.close(); await session.close()
    }

    func testCancellationDuringColdDetailDoesNotImplicitlyEnable() async throws {
        let gate = NativeMusicGate(), runtime = DeclarationOrderRuntime(heldMethod: "sources.detail", gate: gate)
        let session = RuntimeSession(launcher: MusicScriptHTTPTestRuntime { try await runtime.respond($0, $1) })
        let service = NativeMusicScriptService(connection: { try await session.connection() })
        await service.start()
        let loading = Task { try await service.source(installationID: DeclarationOrderRuntime.first) }
        await gate.started(); loading.cancel()
        await scriptExpectFailure(.cancelled) { try await loading.value }
        await gate.release()
        _ = try await service.setScriptSourceEnabled(DeclarationOrderRuntime.first, enabled: false)
        let calls = await runtime.firstCalls
        XCTAssertEqual(calls, ["sources.detail", "sources.disable"])
        try await service.close(); await session.close()
    }
}

private actor DeclarationOrderRuntime {
    static let first = "11111111-1111-4111-8111-111111111111"
    static let second = "22222222-2222-4222-8222-222222222222"
    private var gates: [String: [NativeMusicGate]]
    private var enabled = [DeclarationOrderRuntime.first: true, DeclarationOrderRuntime.second: false]
    private var generations = [DeclarationOrderRuntime.first: 0, DeclarationOrderRuntime.second: 0]
    var firstCalls: [String] = []
    init(heldMethod: String, gate: NativeMusicGate) { gates = [heldMethod: [gate]] }
    func hold(_ method: String, _ gate: NativeMusicGate) { gates[method, default: []].append(gate) }
    func respond(_ method: String, _ value: RuntimeValue) async throws -> RuntimeValue {
        let id = try XCTUnwrap(value.scriptField("installationId")?.scriptText)
        if id == Self.first { firstCalls.append(method) }
        if method == "resolve" && generations[id] == 0 { generations[id] = 1 }
        let detail = item(id)
        let result = MusicScriptHTTPTestRuntime.success(.object([
            "installationId": .string(id), "generation": .number(Double(generations[id] ?? 0)),
            "contentHash": .string(String(repeating: "a", count: 64)),
            "platform": .string("tx"), "quality": .string("flac"),
            "url": .string("https://media.invalid/order.flac"), "headers": .object([:]),
        ]))
        if id == Self.first, var waiting = gates[method], !waiting.isEmpty {
            let gate = waiting.removeFirst()
            gates[method] = waiting
            await gate.wait()
            try Task.checkCancellation()
        }
        if method == "sources.enable" { enabled[id] = true; generations[id, default: 0] += 1 }
        if method == "sources.disable" { enabled[id] = false; generations[id] = 0 }
        if method == "resolve" { return result }
        return MusicScriptHTTPTestRuntime.success(.object(["item": method == "sources.detail" ? detail : item(id)]))
    }
    private func item(_ id: String) -> RuntimeValue {
        let generation = generations[id] ?? 0
        return .object([
            "installationId": .string(id), "name": .string("Fixture"), "version": .string("1"),
            "enabled": .bool(enabled[id] == true), "origin": .object(["kind": .string("file")]),
            "state": .string(enabled[id] == true ? (generation > 0 ? "ready" : "idle") : "disabled"),
            "generation": .number(Double(generation)),
            "contentHash": .string(String(repeating: "a", count: 64)),
            "executionHash": .string(String(repeating: "b", count: 64)),
            "sites": .object(["tx": .object(["actions": .array([.string("musicUrl")]),
                "qualitys": .array([.string("flac")])])]),
        ])
    }
}

/// Uses the same complete HTTP envelopes, URLSession cancellation and shared
/// RuntimeSession as MusicScriptTestRuntime, with per-scenario business replies.
actor MusicScriptHTTPTestRuntime: RuntimeLaunching {
    typealias Response = @Sendable (String, RuntimeValue) async throws -> RuntimeValue
    private let response: Response
    private let endpointHost = UUID().uuidString.lowercased() + ".invalid"
    private var client: RuntimeHTTPClient?
    init(_ response: @escaping Response) { self.response = response }
    func connect() throws -> RuntimeHTTPClient {
        MusicScriptRegressionURLProtocol.registry.set(self, host: endpointHost)
        let configuration = URLSessionConfiguration.ephemeral
        configuration.protocolClasses = [MusicScriptRegressionURLProtocol.self]
        let client = try RuntimeHTTPClient(baseURL: URL(string: "http://\(endpointHost):43210")!,
            configuration: configuration)
        self.client = client
        return client
    }
    func close() async {
        await client?.close()
        MusicScriptRegressionURLProtocol.registry.set(nil, host: endpointHost)
    }
    func waitForExit() {}
    static func success(_ value: RuntimeValue, contract: String = "music.script.v3") -> RuntimeValue {
        .object(["ok": .bool(true), "contract": .string(contract), "value": value])
    }
    static func failure(_ code: String) -> RuntimeValue {
        .object(["ok": .bool(false), "contract": .string("music.script.v3"), "code": .string(code)])
    }
    func respond(_ request: URLRequest) async throws -> RuntimeValue {
        if request.url!.path == "/v1/describe" {
            return Self.success(.object(["protocol": .string("runtime.http.v1"), "modules": .array([
                .object(["id": .string("music"), "version": .string("0.8.0"), "contract": .string("music.script.v3")]),
            ])]), contract: "runtime.http.v1")
        }
        if request.url!.path == "/v1/music/close" {
            return Self.success(.object(["closed": .bool(true), "cleanupComplete": .bool(true)]))
        }
        let bytes: Data
        if let body = request.httpBody { bytes = body }
        else if let stream = request.httpBodyStream {
            stream.open(); defer { stream.close() }
            var data = Data(), buffer = [UInt8](repeating: 0, count: 4096)
            while stream.hasBytesAvailable {
                let count = stream.read(&buffer, maxLength: buffer.count)
                if count <= 0 { break }
                data.append(contentsOf: buffer.prefix(count))
            }
            bytes = data
        } else { bytes = Data() }
        return try await response(request.url!.lastPathComponent, RuntimeJSON.decode(bytes))
    }
}

private final class MusicScriptRegressionHTTPRegistry: @unchecked Sendable {
    private let lock = NSLock()
    private var runtimes: [String: MusicScriptHTTPTestRuntime] = [:]
    func set(_ runtime: MusicScriptHTTPTestRuntime?, host: String) {
        lock.lock(); defer { lock.unlock() }; runtimes[host] = runtime
    }
    func runtime(_ host: String) -> MusicScriptHTTPTestRuntime? {
        lock.lock(); defer { lock.unlock() }; return runtimes[host]
    }
}

private final class MusicScriptRegressionURLProtocol: URLProtocol, @unchecked Sendable {
    static let registry = MusicScriptRegressionHTTPRegistry()
    private let lock = NSLock()
    private var loading: Task<Void, Never>?
    override class func canInit(with request: URLRequest) -> Bool { true }
    override class func canonicalRequest(for request: URLRequest) -> URLRequest { request }
    override func startLoading() {
        lock.lock(); defer { lock.unlock() }
        loading = Task {
            do {
                let id = request.url?.host ?? ""
                guard let runtime = Self.registry.runtime(id) else { throw RuntimeHTTPError.closed }
                let response = try await runtime.respond(request)
                try Task.checkCancellation()
                let data = try RuntimeJSON.encode(response)
                let http = HTTPURLResponse(url: request.url!, statusCode: 200, httpVersion: "HTTP/1.1",
                    headerFields: ["Content-Type": "application/json",
                        "X-Meow-Request-Id": request.value(forHTTPHeaderField: "X-Meow-Request-Id")!])!
                client?.urlProtocol(self, didReceive: http, cacheStoragePolicy: .notAllowed)
                client?.urlProtocol(self, didLoad: data)
                client?.urlProtocolDidFinishLoading(self)
            } catch {
                if !Task.isCancelled { client?.urlProtocol(self, didFailWithError: error) }
            }
        }
    }
    override func stopLoading() {
        lock.lock(); let task = loading; loading = nil; lock.unlock()
        task?.cancel()
    }
}
