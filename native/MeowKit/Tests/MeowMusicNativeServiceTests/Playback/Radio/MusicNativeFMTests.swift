import XCTest
import MeowMusicFeature
@testable import MeowMusicNativeService

final class MusicNativeFMTests: XCTestCase {
    func testManualBoundaryAppendsFreshBatchInOrderWithoutChangingSavedSingleModeOrOldIDs() async throws {
        let store = NativeMusicMemoryDocument(), service = try await NativeMusicFixture.service(store)
        let loader = MusicAccountQueuePages(roots: [[NativeMusicFixture.track("A"), NativeMusicFixture.track("B")],
                                                   [NativeMusicFixture.track("B"), NativeMusicFixture.track("C")]])
        let source = MusicAccountQueueFixture.source(loader, kind: .fm)
        let beforeOpen = await loader.events(); XCTAssertTrue(beforeOpen.isEmpty)
        let start = try await MusicAccountQueueFixture.start(service, source, index: 1)
        let first = try await service.replaceRemoteQueue(source, start: start, expectedRevision: 0, intent: .init())
        _ = try await service.updateSettings(.init(ifRevision: first.settingsRevision, mode: .single))
        let old = try await service.queue()
        let next = try await service.advanceFM(source, direction: .next, reason: .manual, expectedRevision: old.revision, intent: .init())
        let queue = try await service.queue(), calls = await loader.events()
        XCTAssertEqual(queue.entries.count, 4); XCTAssertEqual(Array(queue.entries.prefix(2)).map(\.id), old.entries.map(\.id))
        XCTAssertEqual(next.selectedId, queue.entries[2].id); XCTAssertEqual(next.state.mode, .single)
        XCTAssertEqual(calls, ["root", "root"])
        let previous = try await service.advanceFM(source, direction: .previous, reason: .manual,
            expectedRevision: queue.revision, intent: .init())
        XCTAssertEqual(previous.selectedId, old.entries[1].id)
        let afterPrevious = await loader.events(); XCTAssertEqual(afterPrevious, calls)
        let played = try await service.playedSongs(); XCTAssertTrue(played.isEmpty)
    }

    func testEndedNeedsAcceptedExactPlaybackFactAndNeverReplaysOldEndOrWritesOnBatchFailure() async throws {
        let store = NativeMusicMemoryDocument(), scripts = NativeMusicScriptFixture()
        let service = try await NativeMusicFixture.service(store, scripts: scripts)
        let loader = MusicAccountQueuePages(roots: [[NativeMusicFixture.track("A")], [NativeMusicFixture.track("B")]])
        let source = MusicAccountQueueFixture.source(loader, kind: .fm)
        let start = try await MusicAccountQueueFixture.start(service, source)
        let first = try await service.replaceRemoteQueue(source, start: start, expectedRevision: 0, intent: .init())
        do {
            _ = try await service.advanceFM(source, direction: .next, reason: .ended, expectedRevision: first.revision, intent: .init())
            XCTFail("no synthetic ended")
        } catch { XCTAssertEqual(error as? MusicServiceError, .staleResult) }
        let queue = try await service.queue()
        let options = try await service.playbackOptions(for: queue.entries[0].track.ref)
        let audio = try await service.resolve(attemptId: "00000000-0000-4000-8000-000000000901", occurrenceId: queue.entries[0].id,
            selection: options[0].selection(quality: "128k"))
        let ended = MusicPlaybackFact(attemptId: audio.attemptId, occurrenceId: audio.occurrenceId,
            resourceId: audio.resourceId, kind: .ended, position: 180, duration: 180)
        _ = try await service.record(ended)
        let before = store.snapshot()
        await loader.fail("root")
        do {
            _ = try await service.advanceFM(source, direction: .next, reason: .ended, expectedRevision: first.revision, intent: .init())
            XCTFail("no wrap or silent retry")
        } catch { XCTAssertEqual(error as? MusicServiceError, .upstreamFailed) }
        XCTAssertEqual(store.snapshot().0, before.0); XCTAssertEqual(store.snapshot().2, before.2)
        await loader.fail(nil)
        let next = try await service.advanceFM(source, direction: .next, reason: .manual, expectedRevision: first.revision, intent: .init())
        XCTAssertNotEqual(next.selectedId, first.currentId)
        do { _ = try await service.record(ended); XCTFail("old fact cannot start another batch") }
        catch { XCTAssertEqual(error as? MusicServiceError, .staleResult) }
        let calls = await loader.events(); XCTAssertEqual(calls, ["root", "root", "root"])
    }

    func testProtectionAndRevisionChangeDuringFreshBatchDoNotAppendOrReplaceNewQueue() async throws {
        for protect in [false, true] {
            let store = NativeMusicMemoryDocument(), service = try await NativeMusicFixture.service(store)
            let loader = MusicAccountQueuePages(roots: [[NativeMusicFixture.track("A")], [NativeMusicFixture.track("B")]])
            let source = MusicAccountQueueFixture.source(loader, kind: .fm)
            let start = try await MusicAccountQueueFixture.start(service, source)
            let state = try await service.replaceRemoteQueue(source, start: start, expectedRevision: 0, intent: .init())
            let gate = NativeMusicGate(); await loader.hold("root", gate: gate)
            let task = Task { try await service.advanceFM(source, direction: .next, reason: .manual,
                expectedRevision: state.revision, intent: .init()) }
            await gate.started()
            if protect { service.revokeAccess(generation: 2) }
            else { _ = try await service.changeQueue(.clear, expectedRevision: state.revision) }
            let after = store.snapshot()
            await gate.release()
            do { _ = try await task.value; XCTFail("late batch must not commit") }
            catch { XCTAssertEqual(error as? MusicServiceError, protect ? .cancelled : .staleRevision) }
            XCTAssertEqual(store.snapshot().0, after.0); XCTAssertEqual(store.snapshot().2, after.2)
        }
    }
}
