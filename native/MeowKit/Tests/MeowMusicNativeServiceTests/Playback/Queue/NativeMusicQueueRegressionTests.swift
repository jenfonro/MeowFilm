import XCTest
import MeowMusicFeature
@testable import MeowMusicNativeService

final class NativeMusicQueueRegressionTests: XCTestCase {
    func testSessionPausePreservesTimeButColdQueueRestoresAtZeroWithoutResolving() async throws {
        let store = NativeMusicMemoryDocument(), scripts = NativeMusicScriptFixture()
        let service = try await NativeMusicFixture.service(store, scripts: scripts)
        let page = try await service.search(platform: "tx", query: "Synthetic", page: 1)
        let queued = try await service.changeQueue(.replace(page.items.map(\.ref), index: 0), expectedRevision: 0)
        let occurrence = try XCTUnwrap(queued.currentId), option = try await service.playbackOptions(for: page.items[0].ref)[0]
        let candidate = try await service.resolve(attemptId: UUID().uuidString.lowercased(),
            occurrenceId: occurrence, selection: option.selection(quality: "128k"))
        for kind in [MusicPlaybackFact.Kind.playing, .paused] {
            _ = try await service.record(.init(attemptId: candidate.attemptId, occurrenceId: occurrence,
                resourceId: candidate.resourceId, kind: kind, position: 37))
        }
        let paused = try await service.state()
        XCTAssertEqual(paused.position, 37)
        let bytes = try XCTUnwrap(store.snapshot().0), durable = try MJ.parse(bytes)
        XCTAssertEqual(durable["records"][MusicModel.key("playback")]["position"]["seconds"], .n(0))
        let callsBefore = await scripts.calls().count
        await service.close()
        let cold = try await NativeMusicFixture.service(store, scripts: scripts)
        let restored = try await cold.state(), restoredQueue = try await cold.queue()
        XCTAssertEqual(restored.position, 0); XCTAssertEqual(restored.currentId, occurrence)
        XCTAssertEqual(restoredQueue.entries.map(\.id), [occurrence])
        let callsAfter = await scripts.calls().count
        XCTAssertEqual(callsAfter, callsBefore, "Restoring a queue must not resolve or play")
        XCTAssertEqual(store.snapshot().0, bytes, "Reading cold state is not a data migration")
        await cold.close()
    }

    func testReselectSameOccurrenceResetsPositionAndRejectsOldAttemptOnlyAfterCommit() async throws {
        let store = NativeMusicMemoryDocument(), service = try await NativeMusicFixture.service(store, scripts: NativeMusicScriptFixture())
        let page = try await service.search(platform: "tx", query: "Synthetic", page: 1)
        let state = try await service.changeQueue(.replace(page.items.map(\.ref), index: 0), expectedRevision: 0)
        let id = try XCTUnwrap(state.currentId), options = try await service.playbackOptions(for: page.items[0].ref)
        let candidate = try await service.resolve(attemptId: UUID().uuidString.lowercased(), occurrenceId: id,
                                                 selection: options[0].selection(quality: "128k"))
        let fact = MusicPlaybackFact(attemptId: candidate.attemptId, occurrenceId: id, resourceId: candidate.resourceId, kind: .playing, position: 17)
        _ = try await service.record(fact)
        store.fail(.storageConflict)
        do { _ = try await service.changeQueue(.select(id), expectedRevision: state.revision); XCTFail("CAS conflict") }
        catch { XCTAssertEqual(error as? MusicServiceError, .storageConflict) }
        store.fail(nil)
        let accepted = try await service.record(fact); XCTAssertTrue(accepted)
        let selected = try await service.changeQueue(.select(id), expectedRevision: state.revision)
        XCTAssertEqual(selected.currentId, id); XCTAssertEqual(selected.position, 0)
        do { _ = try await service.record(fact); XCTFail("old same-occurrence attempt") }
        catch { XCTAssertEqual(error as? MusicServiceError, .staleResult) }
        let history = try await service.playedSongs(); XCTAssertEqual(history.count, 1)
    }
    func testReselectCancelsNoncooperativePendingResolveWithoutWaitingForUpstream() async throws {
        let scripts = NativeMusicScriptFixture(), gate = NativeMusicGate()
        let service = try await NativeMusicFixture.service(scripts: scripts)
        let page = try await service.search(platform: "tx", query: "Synthetic", page: 1)
        let state = try await service.changeQueue(.replace(page.items.map(\.ref), index: 0), expectedRevision: 0)
        let id = try XCTUnwrap(state.currentId), options = try await service.playbackOptions(for: page.items[0].ref)
        await scripts.configure(gate: gate)
        let request = Task { try await service.resolve(attemptId: UUID().uuidString.lowercased(), occurrenceId: id,
                                                       selection: options[0].selection(quality: "128k")) }
        await gate.started()
        _ = try await service.changeQueue(.select(id), expectedRevision: state.revision)
        do { _ = try await request.value; XCTFail("logical cancellation must not await server") }
        catch { XCTAssertEqual(error as? MusicServiceError, .cancelled) }
        let physical = await service.operations.count
        XCTAssertGreaterThan(physical, 0, "cancelled but unsettled work still owns its budget")
        await gate.release()
        let history = try await service.playedSongs(); XCTAssertTrue(history.isEmpty)
    }
    func testOrderBoundaryDoesNotRewriteQueueOrResetPosition() async throws {
        let store = NativeMusicMemoryDocument(), service = try await NativeMusicFixture.service(store, scripts: NativeMusicScriptFixture())
        let page = try await service.search(platform: "tx", query: "Synthetic", page: 1)
        let state = try await service.changeQueue(.replace(page.items.map(\.ref), index: 0), expectedRevision: 0)
        _ = try await service.updateSettings(.init(ifRevision: 0, mode: .order))
        let id = try XCTUnwrap(state.currentId), options = try await service.playbackOptions(for: page.items[0].ref)
        let candidate = try await service.resolve(attemptId: UUID().uuidString.lowercased(), occurrenceId: id,
                                                 selection: options[0].selection(quality: "128k"))
        _ = try await service.record(.init(attemptId: candidate.attemptId, occurrenceId: id, resourceId: candidate.resourceId, kind: .playing, position: 11))
        let before = store.snapshot().0
        let result = try await service.advance(.previous, reason: .manual, expectedRevision: state.revision)
        XCTAssertNil(result.selectedId); XCTAssertEqual(result.state.position, 11)
        XCTAssertEqual(result.state.revision, state.revision); XCTAssertEqual(store.snapshot().0, before)
    }
}
