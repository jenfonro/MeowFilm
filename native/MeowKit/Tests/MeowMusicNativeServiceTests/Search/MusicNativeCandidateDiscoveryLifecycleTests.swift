import XCTest
import MeowMusicFeature
@testable import MeowMusicNativeService

final class MusicNativeCandidateDiscoveryLifecycleTests: XCTestCase {
    func testClosedCapabilityCallbacksDoNotDispatchAndRegisteredScriptsPhysicallyDrain() async throws {
        let barrier = MusicNativeCandidateDiscoveryBarrier(), completed = MusicNativeCandidateDiscoveryBarrier()
        await completed.release()
        let scripts = MusicNativeCandidateDiscoveryScripts()
        await scripts.holdSources(barrier)
        let store = try MusicNativeCandidateDiscoveryFixture.store()
        let service = try await NativeMusicFixture.service(store, scripts: scripts)
        let session = try await service.candidateDiscoverySession(occurrenceID: "entry-1", expectedQueueRevision: 1)
        let before = store.snapshot().0
        let snapshot = Task { try await session.snapshot(discover: false, force: false) }
        await barrier.started(1)
        let closing = Task { await session.close(retainingAttemptID: nil); await completed.enter() }
        do {
            try await MusicCandidateDiscoveryDeadline.run(seconds: 3) {
                while true {
                    do { try await session.check() }
                    catch MusicServiceError.closed { return }
                    await Task.yield()
                }
            }
        } catch {
            XCTFail("The closed capability owner remained readable")
            await barrier.release()
            await closing.value
            _ = try? await snapshot.value
            await service.close()
            return
        }
        do { _ = try await session.snapshot(discover: false, force: false); XCTFail("Closed capability read started") }
        catch { XCTAssertEqual(error as? MusicServiceError, .closed) }
        let pendingClose = await completed.count(), calls = await scripts.sourcesCalls()
        XCTAssertEqual(pendingClose, 0, "Close must await the registered noncooperative script callback")
        XCTAssertEqual(calls, 1, "A callback requested after close cannot start another sources read")
        await barrier.release()
        await closing.value
        do { _ = try await snapshot.value; XCTFail("A closed capability result was published") }
        catch { XCTAssertTrue([MusicServiceError.closed, .cancelled].contains(MusicServiceError.sanitized(error))) }
        let finalCalls = await scripts.sourcesCalls()
        XCTAssertEqual(finalCalls, 1)
        XCTAssertEqual(store.snapshot().0, before)

        let current = try await service.beginCandidateDiscovery(occurrenceID: "entry-1", expectedQueueRevision: 1)
        let value = try await current.snapshot(discover: false, force: false)
        XCTAssertFalse(value.options.isEmpty, "Closing one session cannot revoke a different candidate owner")
        let currentCalls = await scripts.sourcesCalls()
        XCTAssertEqual(currentCalls, 2)
        await current.close(retainingAttemptID: nil)
        await service.close()
    }

    func testPlaylistEnqueueDoesNotDiscoverEveryTrackAndOnlyCurrentOccurrenceCanSearch() async throws {
        let tracks = (0..<40).map {
            MusicNativeCandidateDiscoveryFixture.track("tx", id: "Queued\($0)",
                title: $0 == 7 ? "Synthetic recording" : "Unplayed recording \($0)")
        }
        let pages = MusicAccountQueuePages(roots: [tracks]), source = MusicAccountQueueFixture.source(pages)
        let store = NativeMusicTestLibrary(), http = MusicNativeDiscoveryPlatforms(), qq = MusicNativeCandidateDiscoveryQQ()
        let scripts = MusicNativeCandidateDiscoveryScripts()
        await scripts.supported(["wy"])
        let cache = MusicNativeMemorySourceDiscoveryCache()
        let service = try await NativeMusicFixture.service(store, scripts: scripts, platform: NativeMusicFixture.routes(primary: qq, others: http), sourceDiscovery: cache)
        _ = try await service.remoteQueuePage(source, cursor: nil)
        let state = try await service.replaceRemoteQueue(source, start: .init(index: 7),
            expectedRevision: 0, intent: .init())
        let queued = try await service.queue()
        let original = try await service.read(), writes = store.snapshot().2
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
        // Only the platform the injected script can play is searched, once.
        XCTAssertEqual(calls, ["wy"])
        XCTAssertTrue(qqCalls.isEmpty)
        let discovered = try await service.queue()
        XCTAssertEqual(discovered.entries.count, 40)
        XCTAssertEqual(discovered.revision, state.revision)
        XCTAssertEqual(discovered.entries[7].track.sources.count, 2)
        let prepared = try await service.read()
        XCTAssertEqual(prepared.queue, original.queue)
        XCTAssertEqual(prepared.playback, original.playback)
        XCTAssertGreaterThan(store.snapshot().2, writes, "new source facts are saved in the repository, not the queue")
        for index in discovered.entries.indices where index != 7 {
            XCTAssertEqual(discovered.entries[index].track.sources, queued.entries[index].track.sources)
            let entries = await cache.entries(songID: discovered.entries[index].track.songId)
            XCTAssertTrue(entries.isEmpty, "an unplayed recording has repository facts, not synthetic search evidence")
        }
        let history = try await service.playedSongs()
        XCTAssertTrue(history.isEmpty)
        await source.close(); await service.close()
    }

    func testQueueAndSettingsRevisionChangesRejectStagedDiscovery() async throws {
        for settings in [true, false] {
            let store = try MusicNativeCandidateDiscoveryFixture.store()
            let service = try await NativeMusicFixture.service(store,
            scripts: MusicNativeCandidateDiscoveryScripts(),
            platform: NativeMusicFixture.routes(primary: MusicNativeCandidateDiscoveryQQ(), others: MusicNativeDiscoveryPlatforms()))
            let session = try await service.beginCandidateDiscovery(occurrenceID: "entry-1", expectedQueueRevision: 1)
            _ = try await session.snapshot(discover: true, force: false)
            if settings { _ = try await service.updateSettings(.init(ifRevision: 0, mode: .shuffle)) }
            else { _ = try await service.changeQueue(.select("entry-1"), expectedRevision: 1) }
            let before = store.snapshot().0
            do { _ = try await session.snapshot(discover: false, force: false); XCTFail("stale snapshot") }
            catch { XCTAssertEqual(error as? MusicServiceError, .staleRevision) }
            XCTAssertEqual(store.snapshot().0, before)
            await session.close(retainingAttemptID: nil); await service.close()
        }
    }
    func testLockCancelsConcurrentNoncooperativeSearchWithoutWritingLateMatches() async throws {
        let barrier = MusicNativeCandidateDiscoveryBarrier(), store = try MusicNativeCandidateDiscoveryFixture.store()
        let service = try await NativeMusicFixture.service(store,
            scripts: MusicNativeCandidateDiscoveryScripts(),
            platform: NativeMusicFixture.routes(primary: MusicNativeCandidateDiscoveryQQ(barrier: barrier), others: MusicNativeDiscoveryPlatforms(barrier: barrier)))
        let before = store.snapshot().0
        let session = try await service.beginCandidateDiscovery(occurrenceID: "entry-1", expectedQueueRevision: 1)
        let task = Task { try await session.snapshot(discover: true, force: false) }
        await barrier.started(4)
        service.revokeAccess(generation: 2)
        do { _ = try await task.value; XCTFail("revoked") } catch {}
        await barrier.release()
        try await service.setAccess(available: false, generation: 2)
        try await service.setAccess(available: true, generation: 3)
        do { _ = try await session.snapshot(discover: false, force: false); XCTFail("old access epoch") } catch {}
        XCTAssertEqual(store.snapshot().0, before)
        await session.close(retainingAttemptID: nil); await service.close()
    }
    func testClosedSessionCannotReadOrResolveEvenIfItsCallbacksArriveLater() async throws {
        let barrier = MusicNativeCandidateDiscoveryBarrier(), store = try MusicNativeCandidateDiscoveryFixture.store()
        let service = try await NativeMusicFixture.service(store,
            scripts: MusicNativeCandidateDiscoveryScripts(),
            platform: NativeMusicFixture.routes(primary: MusicNativeCandidateDiscoveryQQ(barrier: barrier), others: MusicNativeDiscoveryPlatforms(barrier: barrier)))
        let session = try await service.beginCandidateDiscovery(occurrenceID: "entry-1", expectedQueueRevision: 1)
        let before = store.snapshot().0
        let task = Task { try await session.snapshot(discover: true, force: false) }
        await barrier.started(4)
        let closing = Task { await session.close(retainingAttemptID: nil) }
        do { _ = try await task.value; XCTFail("closed") } catch {}
        await barrier.release()
        await closing.value
        do { _ = try await session.snapshot(discover: false, force: false); XCTFail("closed session") }
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
