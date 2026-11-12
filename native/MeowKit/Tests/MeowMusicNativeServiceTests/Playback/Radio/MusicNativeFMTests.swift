import XCTest
import MeowMusicFeature
@testable import MeowMusicNativeService

final class MusicNativeFMTests: XCTestCase {
    func testFMUsesGlobalLoopPriorityFailuresAndWrapsExistingQueue() async throws {
        let service = try await NativeMusicFixture.service()
        var d = try MusicDocument(nil, runID: "fixture")
        d.queue["entries"] = .array(try (1...4).map { n in
            .obj([("id", .string("entry-\(n)")), ("song", try NativeMusicFixture.song("ID-\(n)"))])
        })
        d.queue["currentId"] = .string("entry-2"); d.queue["serial"] = .n(4)
        d.queue["failed"] = .strings(["entry-3"])
        d.queue["priority"] = .strings(["entry-4"])
        let priority = await service.nextID(d, direction: .next); XCTAssertEqual(priority, "entry-4")
        d.queue["priority"] = .array([]); d.queue["currentId"] = .string("entry-4")
        let wrapped = await service.nextID(d, direction: .next); XCTAssertEqual(wrapped, "entry-1")
        d.playback["mode"] = .n(MusicPlaybackMode.single.rawValue)
        let repeated = await service.nextID(d, direction: .next, reason: .ended)
        XCTAssertEqual(repeated, "entry-4")
        let manual = await service.nextID(d, direction: .next); XCTAssertEqual(manual, "entry-1")
        await service.close()
    }

    func testPlayingTailAppendsBeyondFormerTwoThousandLimitWithoutChoosingNewBatch() async throws {
        let service = try await NativeMusicFixture.service(scripts: NativeMusicScriptFixture())
        let loader = MusicAccountQueuePages(roots: [[NativeMusicFixture.track("A")], [NativeMusicFixture.track("B")]])
        let source = MusicAccountQueueFixture.source(loader, kind: .fm)
        let start = try await MusicAccountQueueFixture.start(service, source)
        _ = try await service.replaceRemoteQueue(source, start: start, expectedRevision: 0, intent: .init())
        try await service.transaction(["queue"]) { d in
            let first = d.queue["entries"].array[0]
            d.queue["entries"] = .array((1...2000).map { n in var row = first; row["id"] = .string("entry-\(n)"); return row })
            d.queue["currentId"] = .string("entry-2000"); d.queue["serial"] = .n(2000)
        }
        let before = try await service.queue()
        let audio = try await playing(service)
        let pending = await service.fmPlayback?.pending; await pending?.value
        let after = try await service.queue()
        XCTAssertEqual(after.entries.count, 2001); XCTAssertEqual(Array(after.entries.prefix(2000)), before.entries)
        XCTAssertEqual(after.currentId, "entry-2000"); XCTAssertEqual(after.entries.last?.track.sources.first?.nativeId, "B")
        let attempt = await service.attempt?.id; XCTAssertEqual(attempt, audio.attemptId)
        let calls = await loader.events(); XCTAssertEqual(calls, ["root", "root"])
        await source.close(); await service.close()
    }

    func testSpecialTailFetchAppendsWithoutTakingOverAfterNaturalEnd() async throws {
        let service = try await NativeMusicFixture.service(scripts: NativeMusicScriptFixture())
        let loader = MusicAccountQueuePages(roots: [[NativeMusicFixture.track("A"), NativeMusicFixture.track("B")], [NativeMusicFixture.track("C")]])
        let source = MusicAccountQueueFixture.source(loader, kind: .fm)
        let start = try await MusicAccountQueueFixture.start(service, source)
        _ = try await service.replaceRemoteQueue(source, start: start, expectedRevision: 0, intent: .init())
        _ = try await playing(service)
        let noFetch = await loader.events(); XCTAssertEqual(noFetch, ["root"])
        let queue = try await service.queue()
        _ = try await service.changeQueue(.select(queue.entries[1].id), expectedRevision: queue.revision)
        let gate = NativeMusicGate(); await loader.hold("root", gate: gate)
        let audio = try await playing(service); await gate.started()
        let pending = await service.fmPlayback?.pending
        _ = try await service.recordSimilarPlaybackFact(fact(audio, .ended))
        let state = try await service.state()
        let next = try await service.advanceFM(source, direction: .next, reason: .ended, expectedRevision: state.revision, intent: .init())
        XCTAssertEqual(next.selectedId, queue.entries[0].id)
        await gate.release(); await pending?.value
        let appended = try await service.queue()
        XCTAssertEqual(appended.entries.count, 3); XCTAssertEqual(appended.currentId, next.selectedId)
        XCTAssertEqual(next.state.mode, .loop)
        await source.close(); await service.close()
    }

    func testEveryOrdinaryModeIncludingSameLoopStopsFMUntilSpecialReselection() async throws {
        for mode in [MusicPlaybackMode.loop, .single, .shuffle] {
            let service = try await NativeMusicFixture.service(scripts: NativeMusicScriptFixture())
            let loader = MusicAccountQueuePages(roots: [[NativeMusicFixture.track("A")], [NativeMusicFixture.track("B")]])
            let source = MusicAccountQueueFixture.source(loader, kind: .fm)
            let start = try await MusicAccountQueueFixture.start(service, source)
            let initial = try await service.replaceRemoteQueue(source, start: start, expectedRevision: 0, intent: .init())
            _ = try await service.updateSettings(.init(ifRevision: initial.settingsRevision, mode: mode))
            let audio = try await playing(service)
            let calls = await loader.events(); XCTAssertEqual(calls, ["root"])
            let ordinary = try await service.fmPlaybackSnapshot()
            XCTAssertFalse(try XCTUnwrap(ordinary).continueEnabled)
            XCTAssertEqual(ordinary?.id, source.id)
            try await service.setFMContinue(true, sessionID: source.id)
            let pending = await service.fmPlayback?.pending; await pending?.value
            let special = try await service.fmPlaybackSnapshot(), state = try await service.state()
            XCTAssertTrue(try XCTUnwrap(special).continueEnabled)
            XCTAssertEqual(state.mode, .loop); XCTAssertEqual(state.total, 2)
            XCTAssertEqual(state.currentId, audio.occurrenceId)
            let attempt = await service.attempt?.id; XCTAssertEqual(attempt, audio.attemptId)
            await source.close(); await service.close()
        }
    }

    func testOrdinaryChoiceBuffersInFlightBatchAndChannelSwitchPreservesPlaybackMode() async throws {
        let service = try await NativeMusicFixture.service(scripts: NativeMusicScriptFixture())
        let loader = MusicAccountQueuePages(roots: [[NativeMusicFixture.track("A")], [NativeMusicFixture.track("B")]])
        let source = MusicAccountQueueFixture.source(loader, kind: .fm)
        let start = try await MusicAccountQueueFixture.start(service, source)
        let initial = try await service.replaceRemoteQueue(source, start: start, expectedRevision: 0, intent: .init())
        let gate = NativeMusicGate(); await loader.hold("root", gate: gate)
        _ = try await playing(service); await gate.started()
        let pending = await service.fmPlayback?.pending
        _ = try await service.updateSettings(.init(ifRevision: initial.settingsRevision, mode: .single))
        await gate.release(); await pending?.value
        let disabled = try await service.queue(); XCTAssertEqual(disabled.entries.count, 1)
        try await service.setFMContinue(true, sessionID: source.id)
        let retained = try await service.queue(); XCTAssertEqual(retained.entries.count, 2)
        let calls = await loader.events(); XCTAssertEqual(calls, ["root", "root"])
        let beforeSwitch = try await service.state()
        _ = try await service.updateSettings(.init(ifRevision: beforeSwitch.settingsRevision, mode: .single))
        let nextLoader = MusicAccountQueuePages(roots: [[NativeMusicFixture.track("C"), NativeMusicFixture.track("D")]])
        let nextSource = MusicAccountQueueFixture.source(nextLoader, kind: .fm)
        let nextStart = try await MusicAccountQueueFixture.start(service, nextSource)
        let switched = try await service.replaceRemoteQueue(nextSource, start: nextStart, expectedRevision: retained.revision, intent: .init())
        let queue = try await service.queue(), snapshot = try await service.fmPlaybackSnapshot()
        XCTAssertEqual(queue.entries.count, 4); XCTAssertEqual(Array(queue.entries.prefix(2)), retained.entries)
        XCTAssertEqual(queue.currentId, queue.entries[2].id); XCTAssertEqual(switched.mode, .single)
        XCTAssertFalse(try XCTUnwrap(snapshot).continueEnabled)
        await source.close(); await nextSource.close(); await service.close()
    }

    func testFailureAndExhaustionDoNotGateExistingLoopOrRetrySamePlayEvent() async throws {
        for failing in [false, true] {
            let service = try await NativeMusicFixture.service(scripts: NativeMusicScriptFixture())
            let loader = MusicAccountQueuePages(roots: [[NativeMusicFixture.track("A")], []])
            let source = MusicAccountQueueFixture.source(loader, kind: .fm)
            let start = try await MusicAccountQueueFixture.start(service, source)
            _ = try await service.replaceRemoteQueue(source, start: start, expectedRevision: 0, intent: .init())
            if failing { await loader.fail("root") }
            let audio = try await playing(service), pending = await service.fmPlayback?.pending
            await pending?.value
            _ = try await service.recordSimilarPlaybackFact(fact(audio, .paused))
            _ = try await service.recordSimilarPlaybackFact(fact(audio, .playing))
            _ = try await service.recordSimilarPlaybackFact(fact(audio, .progress))
            let snapshot = try await service.fmPlaybackSnapshot()
            XCTAssertEqual(snapshot?.error, failing ? .upstreamFailed : nil)
            let state = try await service.state()
            let result = try await service.advanceFM(source, direction: .next, reason: .manual, expectedRevision: state.revision, intent: .init())
            XCTAssertEqual(result.selectedId, state.currentId)
            let calls = await loader.events(); XCTAssertEqual(calls, ["root", "root"])
            await source.close(); await service.close()
        }
    }

    func testReplacementProtectionAndAccountRevocationRejectLateBatch() async throws {
        for change in ["clear", "protection", "account"] {
            let store = NativeMusicMemoryDocument(), service = try await NativeMusicFixture.service(store, scripts: NativeMusicScriptFixture())
            let loader = MusicAccountQueuePages(roots: [[NativeMusicFixture.track("A")], [NativeMusicFixture.track("B")]])
            let source = MusicAccountQueueFixture.source(loader, kind: .fm)
            let start = try await MusicAccountQueueFixture.start(service, source)
            _ = try await service.replaceRemoteQueue(source, start: start, expectedRevision: 0, intent: .init())
            let gate = NativeMusicGate(); await loader.hold("root", gate: gate)
            _ = try await playing(service); await gate.started()
            let pending = await service.fmPlayback?.pending
            if change == "protection" { service.revokeAccess(generation: 2) }
            else if change == "account" { source.revoke() }
            else { let state = try await service.state(); _ = try await service.changeQueue(.clear, expectedRevision: state.revision) }
            let before = store.snapshot()
            await gate.release(); await pending?.value
            XCTAssertEqual(store.snapshot().0, before.0); XCTAssertEqual(store.snapshot().2, before.2)
            await source.close(); await service.close()
        }
    }

    private func playing(_ service: NativeMusicService) async throws -> MusicPlaybackCandidate {
        let state = try await service.state(), id = try XCTUnwrap(state.currentId)
        let options = (try await service.sourceAvailability(for: .init(kind: .queue, id: id))).options
        let audio = try await service.resolve(attemptId: UUID().uuidString.lowercased(), occurrenceId: id,
            selection: XCTUnwrap(options.first).selection(quality: "128k"))
        _ = try await service.recordSimilarPlaybackFact(fact(audio, .playing))
        return audio
    }
    private func fact(_ audio: MusicPlaybackCandidate, _ kind: MusicPlaybackFact.Kind) -> MusicPlaybackFact {
        .init(attemptId: audio.attemptId, occurrenceId: audio.occurrenceId, resourceId: audio.resourceId,
              kind: kind, position: kind == .ended ? 180 : 1, duration: 180)
    }
}
