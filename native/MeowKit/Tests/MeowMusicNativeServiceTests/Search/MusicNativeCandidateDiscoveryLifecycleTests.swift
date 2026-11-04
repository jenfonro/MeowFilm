import XCTest
import MeowMusicFeature
@testable import MeowMusicNativeService

final class MusicNativeCandidateDiscoveryLifecycleTests: XCTestCase {
    func testPlaylistEnqueueDoesNotDiscoverEveryTrackAndOnlyCurrentOccurrenceCanSearch() async throws {
        let tracks = (0..<40).map {
            MusicNativeCandidateDiscoveryFixture.track("tx", id: "Queued\($0)",
                title: $0 == 7 ? "Synthetic recording" : "Unplayed recording \($0)")
        }
        let pages = MusicAccountQueuePages(roots: [tracks]), source = MusicAccountQueueFixture.source(pages)
        let store = NativeMusicMemoryDocument(), http = MusicNativeCandidateDiscoveryHTTP(), qq = MusicNativeCandidateDiscoveryQQ()
        let scripts = MusicNativeCandidateDiscoveryScripts()
        await scripts.supported(["wy"])
        let cache = MusicNativeMemorySourceDiscoveryCache(clock: { NativeMusicFixture.date })
        let service = try await NativeMusicFixture.service(store, scripts: scripts, qq: qq, http: http, sourceDiscovery: cache)
        let page = try await service.remoteQueuePage(source, cursor: nil)
        let state = try await service.replaceRemoteQueue(source, start: .init(index: 7, track: page.items[7]),
            expectedRevision: 0, intent: .init())
        let queued = try await service.queue()
        XCTAssertEqual(queued.entries.count, 40)
        XCTAssertEqual(state.currentId, queued.entries[7].id)
        let initialHTTP = await http.calls(), initialQQ = await qq.calls(), sourceCalls = await pages.events()
        XCTAssertTrue(initialHTTP.isEmpty); XCTAssertTrue(initialQQ.isEmpty)
        XCTAssertEqual(sourceCalls, ["root"])
        do {
            _ = try await service.beginCandidateDiscovery(occurrenceID: queued.entries[0].id, expectedQueueRevision: state.revision)
            XCTFail("Noncurrent queue entry started discovery")
        } catch { XCTAssertEqual(error as? MusicServiceError, .targetChanged) }
        let consumer = MusicCandidateDiscoveryConsumer(provider: service)
        let result = try await consumer.resolve(occurrenceID: queued.entries[7].id,
            expectedQueueRevision: state.revision, mode: .automatic())
        XCTAssertEqual(result.target.selection.siteId, "wy")
        let calls = await http.calls(), qqCalls = await qq.calls()
        XCTAssertEqual(Set(calls), ["wy", "kg", "kw", "mg"]); XCTAssertEqual(calls.count, 4)
        XCTAssertTrue(qqCalls.isEmpty)
        let discovered = try await service.queue()
        XCTAssertEqual(discovered.entries.count, 40)
        XCTAssertEqual(discovered.revision, state.revision + 1)
        XCTAssertEqual(discovered.entries[7].track.sources.count, 5)
        for index in discovered.entries.indices where index != 7 {
            XCTAssertEqual(discovered.entries[index].track.sources, queued.entries[index].track.sources)
            let song = try MusicModel.normalize(tracks[index])
            let entries = await cache.entries(fingerprint: MusicModel.fingerprint(song), duration: 180)
            XCTAssertTrue(entries.isEmpty, "An unplayed recording acquired search evidence")
        }
        let history = try await service.playedSongs()
        XCTAssertTrue(history.isEmpty)
        await source.close(); await service.close()
    }

    func testQueueAndSettingsRevisionChangesRejectStagedCommit() async throws {
        for settings in [true, false] {
            let store = try MusicNativeCandidateDiscoveryFixture.store()
            let service = try await NativeMusicFixture.service(store, qq: MusicNativeCandidateDiscoveryQQ(), http: MusicNativeCandidateDiscoveryHTTP())
            let session = try await service.beginCandidateDiscovery(occurrenceID: "entry-1", expectedQueueRevision: 1)
            _ = try await session.snapshot(discover: true, force: false)
            if settings { _ = try await service.updateSettings(.init(ifRevision: 0, mode: .shuffle)) }
            else { _ = try await service.changeQueue(.select("entry-1"), expectedRevision: 1) }
            let before = store.snapshot().0
            do { try await session.commitDiscovery(); XCTFail("stale snapshot") }
            catch { XCTAssertEqual(error as? MusicServiceError, .staleRevision) }
            XCTAssertEqual(store.snapshot().0, before)
            await session.close(retainingAttemptID: nil); await service.close()
        }
    }
    func testLockCancelsConcurrentNoncooperativeSearchWithoutWritingLateMatches() async throws {
        let barrier = MusicNativeCandidateDiscoveryBarrier(), store = try MusicNativeCandidateDiscoveryFixture.store()
        let service = try await NativeMusicFixture.service(store, qq: MusicNativeCandidateDiscoveryQQ(barrier: barrier),
            http: MusicNativeCandidateDiscoveryHTTP(barrier: barrier))
        let before = store.snapshot().0
        let session = try await service.beginCandidateDiscovery(occurrenceID: "entry-1", expectedQueueRevision: 1)
        let task = Task { try await session.snapshot(discover: true, force: false) }
        await barrier.started(4)
        service.revokeAccess(generation: 2)
        do { _ = try await task.value; XCTFail("revoked") } catch {}
        await barrier.release()
        try await service.setAccess(available: false, generation: 2)
        try await service.setAccess(available: true, generation: 3)
        do { try await session.commitDiscovery(); XCTFail("old access epoch") } catch {}
        XCTAssertEqual(store.snapshot().0, before)
        await session.close(retainingAttemptID: nil); await service.close()
    }
    func testClosedSessionCannotCommitOrResolveEvenIfItsCallbacksArriveLater() async throws {
        let barrier = MusicNativeCandidateDiscoveryBarrier(), store = try MusicNativeCandidateDiscoveryFixture.store()
        let service = try await NativeMusicFixture.service(store, qq: MusicNativeCandidateDiscoveryQQ(barrier: barrier),
            http: MusicNativeCandidateDiscoveryHTTP(barrier: barrier))
        let session = try await service.beginCandidateDiscovery(occurrenceID: "entry-1", expectedQueueRevision: 1)
        let before = store.snapshot().0
        let task = Task { try await session.snapshot(discover: true, force: false) }
        await barrier.started(4); await session.close(retainingAttemptID: nil)
        do { _ = try await task.value; XCTFail("closed") } catch {}
        await barrier.release()
        do { try await session.commitDiscovery(); XCTFail("closed commit") }
        catch { XCTAssertEqual(error as? MusicServiceError, .closed) }
        XCTAssertEqual(store.snapshot().0, before); await service.close()
    }
    func testCancelledOldAttemptCannotCancelNewSessionOrAdmitItsURL() async throws {
        let barrier = MusicNativeCandidateDiscoveryBarrier(), scripts = MusicNativeCandidateDiscoveryScripts()
        let store = try MusicNativeCandidateDiscoveryFixture.store()
        let service = try await NativeMusicFixture.service(store, scripts: scripts)
        let old = try await service.beginCandidateDiscovery(occurrenceID: "entry-1", expectedQueueRevision: 1)
        let snapshot = try await old.snapshot(discover: false, force: false)
        let target = try XCTUnwrap(MusicNativeCandidateDiscoveryFixture.target(snapshot, platform: "tx"))
        await scripts.configure(barrier: barrier)
        let attemptID = UUID().uuidString.lowercased()
        let task = Task { try await old.resolve(target, attemptID: attemptID) }
        await barrier.started(1); await old.cancel(attemptID: attemptID)
        do { _ = try await task.value; XCTFail("cancelled attempt") } catch {}
        await scripts.configure()
        let new = try await service.beginCandidateDiscovery(occurrenceID: "entry-1", expectedQueueRevision: 1)
        let candidate = try await new.resolve(target, attemptID: UUID().uuidString.lowercased())
        await old.close(retainingAttemptID: nil)
        await barrier.release()
        await new.close(retainingAttemptID: candidate.attemptId)
        let accepted = try await service.record(.init(attemptId: candidate.attemptId, occurrenceId: "entry-1",
            resourceId: candidate.resourceId, kind: .playing, position: 1))
        XCTAssertTrue(accepted); await service.close()
    }
}
