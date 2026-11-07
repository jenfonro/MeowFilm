import Foundation
import MeowJSON
import MeowMusicFeature
import MeowRuntime
import XCTest
@testable import MeowMusicNativeService

private actor MusicCleanupFailedLauncher: RuntimeBridgeLaunching {
    private(set) var calls = 0
    func connect(handlers: [String: RuntimeBridgeConnection.Handler]) throws -> RuntimeBridgeConnection {
        calls += 1
        throw RuntimeLaunchError.payloadUnavailable
    }
    func waitForExit() {}
}

final class MusicScriptCleanupTests: XCTestCase {
    private func started(_ runtime: MusicScriptTestRuntime) async throws -> (RuntimeBridgeSession, NativeMusicScriptService) {
        let session = RuntimeBridgeSession(launcher: runtime)
        try await session.configure(handlers: [:])
        let service = NativeMusicScriptService(connection: { try await session.connection() })
        await service.start()
        return (session, service)
    }

    func testFailedLaunchCloseDoesNotRetryOrInventMusicCleanupFailure() async throws {
        let launcher = MusicCleanupFailedLauncher(), session = RuntimeBridgeSession(launcher: launcher)
        try await session.configure(handlers: [:])
        let service = NativeMusicScriptService(connection: { try await session.connection() })
        await scriptExpectFailure(.scriptRuntimeUnavailable) { try await service.scriptSources() }
        let before = await launcher.calls; XCTAssertEqual(before, 0)
        await service.start()
        await scriptExpectFailure(.scriptRuntimeUnavailable) { try await service.scriptSources() }
        await service.start()
        await scriptExpectFailure(.scriptRuntimeUnavailable) { try await service.scriptSources() }
        try await service.close()
        try await service.close()
        let calls = await launcher.calls; XCTAssertEqual(calls, 1)
        await session.close()
    }

    func testPassiveCloseDoesNotBorrowOrStartTheSession() async throws {
        let runtime = MusicScriptTestRuntime(), session = RuntimeBridgeSession(launcher: runtime)
        try await session.configure(handlers: [:])
        let service = NativeMusicScriptService(connection: { try await session.connection() })
        try await service.close(); try await service.close()
        await service.start()
        await scriptExpectFailure(.closed) { try await service.scriptSources() }
        let metrics = await runtime.metrics()
        XCTAssertEqual(metrics.connects, 0); XCTAssertTrue(metrics.calls.isEmpty)
        await session.close()
    }

    func testFailedDisableOrDeleteWithNoLiveSourcesStillRequiresModuleCloseReceipt() async throws {
        for deleting in [false, true] {
            let runtime = MusicScriptTestRuntime()
            let (session, service) = try await started(runtime)
            let item = try await service.importScriptSource(musicScriptFixture, fileName: "source.js")
            _ = try await service.setScriptSourceEnabled(item.id, enabled: true)
            await runtime.setCleanupComplete(false)
            await runtime.setModuleClose(.failed)
            if deleting {
                await scriptExpectFailure(.scriptCleanupFailed) { try await service.removeScriptSource(item.id) }
            } else {
                await scriptExpectFailure(.scriptCleanupFailed) {
                    try await service.setScriptSourceEnabled(item.id, enabled: false)
                }
            }
            let items = try await service.scriptSources()
            if deleting { XCTAssertTrue(items.isEmpty) }
            else { XCTAssertEqual(items.first?.enabled, false); XCTAssertEqual(items.first?.state, .disabled) }
            let live = try await service.sources(); XCTAssertTrue(live.isEmpty)
            await scriptExpectFailure(.scriptCleanupFailed) { try await service.close() }
            // Even a changed remote fixture cannot replay this terminal close.
            await runtime.setModuleClose(.success)
            await scriptExpectFailure(.scriptCleanupFailed) { try await service.close() }
            try await scriptAssertCatAvailable(try await session.connection())
            let calls = await runtime.metrics().calls
            XCTAssertEqual(calls.filter { $0 == (deleting ? "sources.remove" : "sources.disable") }.count, 1)
            XCTAssertEqual(calls.filter { $0 == "close" }.count, 1)
            await session.close(); await runtime.close()
        }
    }

    func testModuleCloseReceiptSettlesEarlierCleanupFailureAndConcurrentCancelledWaitersShareIt() async throws {
        for confirmed in [false, true] {
            let runtime = MusicScriptTestRuntime()
            let (session, service) = try await started(runtime)
            let item = try await service.importScriptSource(musicScriptFixture, fileName: "source.js")
            _ = try await service.setScriptSourceEnabled(item.id, enabled: true)
            await runtime.setCleanupComplete(false)
            await scriptExpectFailure(.scriptCleanupFailed) {
                try await service.setScriptSourceEnabled(item.id, enabled: false)
            }
            let live = try await service.sources(); XCTAssertTrue(live.isEmpty)
            await runtime.setModuleClose(confirmed ? .success : .failed)
            let gate = NativeMusicGate()
            await runtime.hold("close", gate)
            let firstPremature = expectation(description: "first close waits for the module receipt")
            let secondPremature = expectation(description: "second close waits for the same receipt")
            firstPremature.isInverted = true; secondPremature.isInverted = true
            let first = Task { defer { firstPremature.fulfill() }; try await service.close() }
            await gate.started()
            let second = Task { defer { secondPremature.fulfill() }; try await service.close() }
            first.cancel()
            await fulfillment(of: [firstPremature, secondPremature], timeout: 0.05)
            await scriptExpectFailure(.closed) { try await service.scriptSources() }
            try await scriptAssertCatAvailable(try await session.connection())
            await gate.release()
            if confirmed {
                try await first.value; try await second.value; try await service.close()
            } else {
                await scriptExpectFailure(.scriptCleanupFailed) { try await first.value }
                await scriptExpectFailure(.scriptCleanupFailed) { try await second.value }
                await scriptExpectFailure(.scriptCleanupFailed) { try await service.close() }
            }
            let calls = await runtime.metrics().calls
            XCTAssertEqual(calls.filter { $0 == "close" }.count, 1)
            try await scriptAssertCatAvailable(try await session.connection())
            await session.close(); await runtime.close()
        }
    }

    func testLoadRollbackFailureWithNoLiveSourceIsSettledOnlyByModuleClose() async throws {
        for confirmed in [false, true] {
            let runtime = MusicScriptTestRuntime()
            let (session, service) = try await started(runtime)
            // v3 owns load and rollback; the caller sees the original SCRIPT_FAILURE
            // while the terminal receipt independently confirms (or denies) cleanup.
            await runtime.setEnableFailure("SCRIPT_FAILURE")
            await runtime.setModuleClose(confirmed ? .success : .failed)
            let item = try await service.importScriptSource(musicScriptFixture, fileName: "source.js")
            await scriptExpectFailure(.scriptFailed) {
                try await service.setScriptSourceEnabled(item.id, enabled: true)
            }
            let items = try await service.scriptSources(); XCTAssertEqual(items, [item])
            let live = try await service.sources(); XCTAssertTrue(live.isEmpty)
            let connection = try await session.connection()
            let detail = try MusicScriptTransport.success(await connection.call("music.script.sources.detail",
                .object(["installationId": .string(item.id)])))
            XCTAssertEqual(detail["item"]?["generation"], .number(0))
            if confirmed { try await service.close() }
            else { await scriptExpectFailure(.scriptCleanupFailed) { try await service.close() } }
            let calls = await runtime.metrics().calls
            XCTAssertEqual(calls.filter { $0 == "sources.enable" }.count, 1)
            XCTAssertEqual(calls.filter { $0 == "close" }.count, 1)
            try await scriptAssertCatAvailable(connection)
            await session.close(); await runtime.close()
        }
    }

    func testLostEnableReceiptPreservesNodeAuthorityWithoutReplayAndStillRequiresModuleClose() async throws {
        for confirmed in [false, true] {
            let runtime = MusicScriptTestRuntime()
            let (session, service) = try await started(runtime)
            let item = try await service.importScriptSource(musicScriptFixture, fileName: "source.js")
            XCTAssertFalse(item.enabled)
            await runtime.setLoseEnableReceipt(true)
            await scriptExpectFailure(.timeout) {
                try await service.setScriptSourceEnabled(item.id, enabled: true)
            }
            let listed = try await service.scriptSources()
            XCTAssertEqual(listed.map(\.id), [item.id])
            XCTAssertEqual(listed.first?.enabled, true)
            XCTAssertEqual(listed.first?.state, .ready)
            let detail = try await service.scriptSourceDetail(item.id)
            XCTAssertEqual(detail.item, listed.first)
            let source = try await service.source(installationID: item.id)
            XCTAssertTrue(source.enabled)
            XCTAssertGreaterThan(source.generation, 0)
            let beforeClose = await runtime.metrics().calls
            XCTAssertEqual(beforeClose.filter {
                ["sources.enable", "sources.disable", "sources.remove"].contains($0)
            }, ["sources.enable"])

            await runtime.setModuleClose(confirmed ? .success : .failed)
            if confirmed { try await service.close() }
            else { await scriptExpectFailure(.scriptCleanupFailed) { try await service.close() } }
            // A different peer response must not change the cached terminal result.
            await runtime.setModuleClose(confirmed ? .failed : .success)
            if confirmed { try await service.close() }
            else { await scriptExpectFailure(.scriptCleanupFailed) { try await service.close() } }
            try await scriptAssertCatAvailable(try await session.connection())
            let calls = await runtime.metrics().calls
            XCTAssertEqual(calls.filter { $0 == "close" }.count, 1)
            XCTAssertEqual(calls.filter {
                ["sources.enable", "sources.disable", "sources.remove"].contains($0)
            }, ["sources.enable"])
            await session.close(); await runtime.close()
        }
    }

    func testNormalLoadFailureAndNoLiveSourceCanCloseNormally() async throws {
        let runtime = MusicScriptTestRuntime()
        let (session, service) = try await started(runtime)
        await runtime.setEnableFailure("SCRIPT_FAILURE")
        let item = try await service.importScriptSource(musicScriptFixture, fileName: "source.js")
        await scriptExpectFailure(.scriptFailed) { try await service.setScriptSourceEnabled(item.id, enabled: true) }
        let items = try await service.scriptSources(); XCTAssertEqual(items, [item])
        let live = try await service.sources(); XCTAssertTrue(live.isEmpty)
        try await service.close()
        let calls = await runtime.metrics().calls
        XCTAssertEqual(calls.filter { $0 == "close" }.count, 1)
        try await scriptAssertCatAvailable(try await session.connection())
        await session.close(); await runtime.close()
    }

    func testIncompleteOrLostModuleCloseReceiptFailsAndIsNotRetried() async throws {
        for response in [MusicScriptTestRuntime.ModuleClose.missingClosed, .missingCleanup, .incomplete, .notClosed, .disconnected] {
            let runtime = MusicScriptTestRuntime()
            let (session, service) = try await started(runtime)
            let item = try await service.importScriptSource(musicScriptFixture, fileName: "source.js")
            _ = try await service.setScriptSourceEnabled(item.id, enabled: true)
            let connection = try await session.connection()
            await runtime.setModuleClose(response)
            await scriptExpectFailure(.scriptCleanupFailed) { try await service.close() }
            await scriptExpectFailure(.scriptCleanupFailed) { try await service.close() }
            let calls = await runtime.metrics().calls
            XCTAssertEqual(calls.filter { $0 == "close" }.count, 1)
            if response != .disconnected { try await scriptAssertCatAvailable(connection) }
            await session.close(); await runtime.close()
        }
    }

    func testNewGenerationCannotHideEarlierModuleCleanupFailure() async throws {
        let runtime = MusicScriptTestRuntime()
        let (session, service) = try await started(runtime)
        let item = try await service.importScriptSource(musicScriptFixture, fileName: "source.js")
        _ = try await service.setScriptSourceEnabled(item.id, enabled: true)
        let before = try await service.source(installationID: item.id)
        await runtime.setCleanupComplete(false)
        await scriptExpectFailure(.scriptCleanupFailed) {
            try await service.setScriptSourceEnabled(item.id, enabled: false)
        }
        await runtime.setCleanupComplete(true)
        _ = try await service.setScriptSourceEnabled(item.id, enabled: true)
        let after = try await service.source(installationID: item.id)
        XCTAssertGreaterThan(after.generation, before.generation)
        await runtime.setModuleClose(.failed)
        await scriptExpectFailure(.scriptCleanupFailed) { try await service.close() }
        try await scriptAssertCatAvailable(try await session.connection())
        await session.close(); await runtime.close()
    }

    func testHandshakeFailureKeepsSharedCatConnectionAndStillClosesMusicWithoutRetry() async throws {
        for confirmed in [false, true] {
            let runtime = MusicScriptTestRuntime()
            await runtime.setHandshakeValid(false)
            let (session, service) = try await started(runtime)
            await scriptExpectFailure(.scriptRuntimeUnavailable) { try await service.scriptSources() }
            await runtime.setHandshakeValid(true)
            await service.start()
            await scriptExpectFailure(.scriptRuntimeUnavailable) { try await service.scriptSources() }
            let connection = try await session.connection()
            try await scriptAssertCatAvailable(connection)
            await runtime.setModuleClose(confirmed ? .success : .failed)
            if confirmed { try await service.close() }
            else { await scriptExpectFailure(.scriptCleanupFailed) { try await service.close() } }
            try await scriptAssertCatAvailable(connection)
            let metrics = await runtime.metrics()
            XCTAssertEqual(metrics.connects, 1)
            XCTAssertEqual(metrics.calls.filter { $0 == "runtime.describe" }.count, 1)
            XCTAssertEqual(metrics.calls.filter { $0 == "close" }.count, 1)
            XCTAssertFalse(metrics.calls.contains("sources.list"))
            await session.close(); await runtime.close()
        }
    }

    func testCloseCancelsHandshakeButRetainsItsConnectionForModuleCleanup() async throws {
        let runtime = MusicScriptTestRuntime(), gate = NativeMusicGate()
        let cancelled = expectation(description: "handshake cancelled")
        await runtime.hold("runtime.describe", gate, onCancel: { cancelled.fulfill() })
        let (session, service) = try await started(runtime)
        await gate.started()
        let closing = Task { try await service.close() }
        await fulfillment(of: [cancelled], timeout: 1)
        await gate.release()
        try await closing.value
        try await scriptAssertCatAvailable(try await session.connection())
        let calls = await runtime.metrics().calls
        XCTAssertEqual(calls.filter { $0 == "close" }.count, 1)
        await session.close(); await runtime.close()
    }

    func testCloseCancelsPendingLoadResolveAndLyricWithoutClosingSharedHost() async throws {
        for method in ["sources.enable", "resolve", "invoke"] {
            let runtime = MusicScriptTestRuntime()
            let (session, service) = try await started(runtime)
            let item = try await service.importScriptSource(musicScriptFixture, fileName: "source.js")
            _ = try await service.setScriptSourceEnabled(item.id, enabled: true)
            let source = try await service.source(installationID: item.id)
            let request = MusicScriptRequest(installationID: item.id, generation: source.generation,
                siteID: "tx", sourceKey: "test", quality: "128k", musicInfo: Data("{}".utf8))
            let workGate = NativeMusicGate(), closeGate = NativeMusicGate()
            let cancelled = expectation(description: "in-flight \(method) cancelled")
            await runtime.hold(method, workGate, onCancel: { cancelled.fulfill() })
            await runtime.hold("close", closeGate)
            let work = Task {
                if method == "sources.enable" { _ = try await service.setScriptSourceEnabled(item.id, enabled: true) }
                else if method == "resolve" { _ = try await service.resolve(request: request) }
                else { _ = try await service.lyrics(request: request) }
            }
            await workGate.started()
            let closing = Task { try await service.close() }
            await fulfillment(of: [cancelled], timeout: 1)
            await scriptExpectFailure(.cancelled) { try await work.value }
            await closeGate.started()
            await scriptExpectFailure(.closed) { try await service.scriptSources() }
            try await scriptAssertCatAvailable(try await session.connection())
            await workGate.release(); await closeGate.release()
            try await closing.value
            let calls = await runtime.metrics().calls
            XCTAssertEqual(calls.filter { $0 == "close" }.count, 1)
            try await scriptAssertCatAvailable(try await session.connection())
            await session.close(); await runtime.close()
        }
    }

    func testCloseDrainsCancelledUploadIncludingUncancelledStageRelease() async throws {
        for method in ["stage.chunk", "sources.add"] {
            let runtime = MusicScriptTestRuntime()
            let (session, service) = try await started(runtime)
            let workGate = NativeMusicGate(), releaseGate = NativeMusicGate()
            let cancelled = expectation(description: "upload \(method) cancelled")
            await runtime.hold(method, workGate, onCancel: { cancelled.fulfill() })
            let releaseCancelled = expectation(description: "stage release must not inherit cancellation")
            releaseCancelled.isInverted = true
            await runtime.hold("stage.abort", releaseGate, onCancel: { releaseCancelled.fulfill() })
            let finished = expectation(description: "upload must wait for its stage release")
            finished.isInverted = true
            let upload = Task {
                defer { finished.fulfill() }
                return try await service.importScriptSource(Data(repeating: 65, count: 128 * 1024 + 1), fileName: "source.js")
            }
            await workGate.started()
            let closing = Task { try await service.close() }
            await fulfillment(of: [cancelled], timeout: 1)
            await releaseGate.started()
            await fulfillment(of: [releaseCancelled, finished], timeout: 0.05)
            let beforeRelease = await runtime.metrics().calls
            XCTAssertFalse(beforeRelease.contains("close"))
            await scriptExpectFailure(.closed) { try await service.scriptSources() }
            try await scriptAssertCatAvailable(try await session.connection())
            await workGate.release(); await releaseGate.release()
            await scriptExpectFailure(.cancelled) { try await upload.value }
            try await closing.value
            let stages = await runtime.stages; XCTAssertTrue(stages.isEmpty)
            let calls = await runtime.metrics().calls
            XCTAssertEqual(calls.filter { $0 == "stage.abort" }.count, 1)
            XCTAssertEqual(calls.filter { $0 == "close" }.count, 1)
            XCTAssertLessThan(try XCTUnwrap(calls.firstIndex(of: "stage.abort")), try XCTUnwrap(calls.firstIndex(of: "close")))
            if method == "stage.chunk" {
                XCTAssertEqual(calls.filter { $0 == "stage.chunk" }.count, 1)
                XCTAssertFalse(calls.contains("sources.add"))
            }
            await session.close(); await runtime.close()
        }
    }

    func testCallerCancellationDuringStageReleaseStillLeavesWorkForCloseToDrain() async throws {
        let runtime = MusicScriptTestRuntime()
        let (session, service) = try await started(runtime)
        let gate = NativeMusicGate()
        let releaseCancelled = expectation(description: "release survives both caller and owner cancellation")
        releaseCancelled.isInverted = true
        await runtime.hold("stage.abort", gate, onCancel: { releaseCancelled.fulfill() })
        let finished = expectation(description: "cancelled upload still waits for release")
        finished.isInverted = true
        let upload = Task {
            defer { finished.fulfill() }
            return try await service.importScriptSource(musicScriptFixture, fileName: "source.js")
        }
        await gate.started()
        upload.cancel()
        let closing = Task { try await service.close() }
        await fulfillment(of: [releaseCancelled, finished], timeout: 0.05)
        let beforeRelease = await runtime.metrics().calls
        XCTAssertTrue(beforeRelease.contains("sources.add"))
        XCTAssertFalse(beforeRelease.contains("close"))
        await gate.release()
        await scriptExpectFailure(.cancelled) { try await upload.value }
        try await closing.value
        let stages = await runtime.stages; XCTAssertTrue(stages.isEmpty)
        let calls = await runtime.metrics().calls
        XCTAssertEqual(calls.filter { $0 == "stage.abort" }.count, 1)
        XCTAssertEqual(calls.filter { $0 == "close" }.count, 1)
        try await scriptAssertCatAvailable(try await session.connection())
        await session.close(); await runtime.close()
    }
}
