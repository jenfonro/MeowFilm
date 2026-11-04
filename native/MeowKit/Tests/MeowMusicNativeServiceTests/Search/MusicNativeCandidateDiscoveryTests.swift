import XCTest
import MeowMusicFeature
@testable import MeowMusicNativeService

final class MusicNativeCandidateDiscoveryTests: XCTestCase {
    func testParallelNativeDiscoveryCASAndExactPlaybackUseExistingFactWriter() async throws {
        let barrier = MusicNativeCandidateDiscoveryBarrier(), store = try MusicNativeCandidateDiscoveryFixture.store()
        let scripts = MusicNativeCandidateDiscoveryScripts()
        let qq = MusicNativeCandidateDiscoveryQQ(barrier: barrier), http = MusicNativeCandidateDiscoveryHTTP(barrier: barrier)
        let service = try await NativeMusicFixture.service(store, scripts: scripts, qq: qq, http: http)
        let session = try await service.beginCandidateDiscovery(occurrenceID: "entry-1", expectedQueueRevision: 1)
        let before = store.snapshot().0
        let task = Task { try await session.snapshot(discover: true, force: true) }
        await barrier.started(5) // All five must enter before any is released.
        await barrier.release()
        let snapshot = try await task.value
        XCTAssertEqual(Set(snapshot.track.sources.map(\.platform)), ["tx", "wy", "kg", "kw", "mg"])
        let searchedHTTP = await http.calls(), searchedQQ = await qq.calls()
        XCTAssertEqual(Set(searchedHTTP), ["wy", "kg", "kw", "mg"]); XCTAssertEqual(searchedHTTP.count, 4)
        XCTAssertEqual(searchedQQ.count, 1)
        XCTAssertFalse(snapshot.issues.contains { $0.stage == .search })
        XCTAssertEqual(store.snapshot().0, before, "discovery is staged, not an asynchronous writer")
        try await session.commitDiscovery()
        let queue = try await service.queue()
        XCTAssertEqual(queue.revision, 2); XCTAssertEqual(queue.currentId, "entry-1")
        XCTAssertEqual(queue.entries[0].track.songId, snapshot.track.songId)
        let cached = try await service.beginCandidateDiscovery(occurrenceID: "entry-1", expectedQueueRevision: 2)
        let cachedSnapshot = try await cached.snapshot(discover: true, force: false)
        XCTAssertEqual(cachedSnapshot.track.sources.count, 5)
        await cached.close(retainingAttemptID: nil)
        let target = try XCTUnwrap(MusicNativeCandidateDiscoveryFixture.target(snapshot))
        let candidate = try await session.resolve(target, attemptID: UUID().uuidString.lowercased())
        XCTAssertEqual(candidate.startPosition, 0, "Cold restoration does not restore playback time")
        let calls = await scripts.calls()
        XCTAssertEqual(calls.count, 1)
        XCTAssertEqual(calls[0].installationID, target.selection.installationId)
        XCTAssertEqual(calls[0].generation, target.selection.generation)
        XCTAssertEqual(calls[0].siteID, "wy"); XCTAssertEqual(calls[0].sourceKey, target.selection.sourceKey)
        let beforeFact = try await service.playedSongs(); XCTAssertTrue(beforeFact.isEmpty)
        await session.close(retainingAttemptID: candidate.attemptId)
        let accepted = try await service.record(.init(attemptId: candidate.attemptId, occurrenceId: "entry-1",
            resourceId: candidate.resourceId, kind: .playing, position: 24))
        XCTAssertTrue(accepted)
        let played = try await service.playedSongs(); XCTAssertEqual(played.count, 1)
        let persisted = String(decoding: try XCTUnwrap(store.snapshot().0), as: UTF8.self)
        XCTAssertFalse(persisted.contains("signature=")); XCTAssertFalse(persisted.contains("media.invalid"))
        await service.close()
    }
    func testPartialFailureIsNotEmptySuccessAndOtherNativePlatformStillProducesCandidates() async throws {
        let store = try MusicNativeCandidateDiscoveryFixture.store()
        let cache = MusicNativeMemorySourceDiscoveryCache(clock: { NativeMusicFixture.date })
        let service = try await NativeMusicFixture.service(store, scripts: MusicNativeCandidateDiscoveryScripts(),
            qq: MusicNativeCandidateDiscoveryQQ(), http: MusicNativeCandidateDiscoveryHTTP(wyStatus: 503),
            sourceDiscovery: cache)
        let session = try await service.beginCandidateDiscovery(occurrenceID: "entry-1", expectedQueueRevision: 1)
        let snapshot = try await session.snapshot(discover: true, force: true)
        XCTAssertTrue(snapshot.issues.contains { $0.stage == .search && $0.platform == "wy" && $0.failure == .upstreamFailed })
        XCTAssertNotNil(MusicNativeCandidateDiscoveryFixture.target(snapshot, platform: "kg"))
        await session.close(retainingAttemptID: nil)
        let second = try await service.beginCandidateDiscovery(occurrenceID: "entry-1", expectedQueueRevision: 1)
        let retried = try await second.snapshot(discover: true, force: false)
        XCTAssertTrue(retried.issues.contains { $0.stage == .search && $0.platform == "wy" })
        await second.close(retainingAttemptID: nil)
        await service.close()
    }
    func testFailedCASLeavesQueueAndAllCandidateMetadataUntouched() async throws {
        let store = try MusicNativeCandidateDiscoveryFixture.store()
        let cache = MusicNativeMemorySourceDiscoveryCache(clock: { NativeMusicFixture.date })
        let service = try await NativeMusicFixture.service(store, qq: MusicNativeCandidateDiscoveryQQ(),
            http: MusicNativeCandidateDiscoveryHTTP(), sourceDiscovery: cache)
        let session = try await service.beginCandidateDiscovery(occurrenceID: "entry-1", expectedQueueRevision: 1)
        _ = try await session.snapshot(discover: true, force: false)
        let before = store.snapshot().0
        store.fail(.storageConflict)
        do { try await session.commitDiscovery(); XCTFail("CAS must fail") }
        catch { XCTAssertEqual(error as? MusicServiceError, .storageConflict) }
        XCTAssertEqual(store.snapshot().0, before)
        store.fail(nil)
        try await session.commitDiscovery()
        let updated = try await service.queue(); XCTAssertEqual(updated.revision, 2)
        let refreshed = try await service.beginCandidateDiscovery(occurrenceID: "entry-1", expectedQueueRevision: 2)
        let cached = try await refreshed.snapshot(discover: true, force: false)
        XCTAssertEqual(cached.track.sources.count, 5)
        await refreshed.close(retainingAttemptID: nil)
        await session.close(retainingAttemptID: nil); await service.close()
    }
    func testChangedScriptVersionAndConfigurationAreRejectedBeforeAnyURLRequest() async throws {
        for change in ["version", "revision", "generation"] {
            let store = try MusicNativeCandidateDiscoveryFixture.store(), scripts = MusicNativeCandidateDiscoveryScripts()
            let service = try await NativeMusicFixture.service(store, scripts: scripts)
            let session = try await service.beginCandidateDiscovery(occurrenceID: "entry-1", expectedQueueRevision: 1)
            let snapshot = try await session.snapshot(discover: false, force: false)
            let target = try XCTUnwrap(MusicNativeCandidateDiscoveryFixture.target(snapshot, platform: "tx"))
            await scripts.configure(generation: change == "generation" ? 2 : 1, version: change == "version" ? "2" : "1",
                revision: String(repeating: change == "revision" ? "b" : "a", count: 64))
            do { _ = try await session.resolve(target, attemptID: UUID().uuidString.lowercased()); XCTFail("pin changed") }
            catch { XCTAssertEqual(error as? MusicServiceError, .targetChanged) }
            let calls = await scripts.calls(); XCTAssertTrue(calls.isEmpty)
            await session.close(retainingAttemptID: nil); await service.close()
        }
    }
    func testVersionChangeAfterURLResponseCannotPublishCandidateOrPlayedHistory() async throws {
        let store = try MusicNativeCandidateDiscoveryFixture.store(), scripts = MusicNativeCandidateDiscoveryScripts()
        let service = try await NativeMusicFixture.service(store, scripts: scripts)
        let session = try await service.beginCandidateDiscovery(occurrenceID: "entry-1", expectedQueueRevision: 1)
        let snapshot = try await session.snapshot(discover: false, force: false)
        let target = try XCTUnwrap(MusicNativeCandidateDiscoveryFixture.target(snapshot, platform: "tx"))
        await scripts.configure(mutateAfterResolve: true)
        do { _ = try await session.resolve(target, attemptID: UUID().uuidString.lowercased()); XCTFail("changed response") }
        catch { XCTAssertEqual(error as? MusicServiceError, .targetChanged) }
        let played = try await service.playedSongs(); XCTAssertTrue(played.isEmpty)
        await session.close(retainingAttemptID: nil); await service.close()
    }
    func testProductionConsumerFindsMissingSourceCommitsAndResolvesItWithoutAnotherWriter() async throws {
        let store = try MusicNativeCandidateDiscoveryFixture.store(), scripts = MusicNativeCandidateDiscoveryScripts()
        await scripts.supported(["wy"])
        let qq = MusicNativeCandidateDiscoveryQQ(), http = MusicNativeCandidateDiscoveryHTTP()
        let cache = MusicNativeMemorySourceDiscoveryCache(clock: { NativeMusicFixture.date })
        let service = try await NativeMusicFixture.service(store, scripts: scripts,
            qq: qq, http: http, sourceDiscovery: cache)
        let consumer = MusicCandidateDiscoveryConsumer(provider: service)
        let result = try await consumer.resolve(occurrenceID: "entry-1", expectedQueueRevision: 1, mode: .automatic())
        XCTAssertEqual(result.target.selection.siteId, "wy")
        XCTAssertEqual(result.attempts, 1)
        let calls = await scripts.calls()
        XCTAssertEqual(calls.count, 1); XCTAssertEqual(calls[0].siteID, "wy")
        let searchedQQ = await qq.calls(), searchedHTTP = await http.calls()
        XCTAssertTrue(searchedQQ.isEmpty)
        XCTAssertEqual(Set(searchedHTTP), ["wy", "kg", "kw", "mg"]); XCTAssertEqual(searchedHTTP.count, 4)
        let queue = try await service.queue()
        XCTAssertEqual(queue.revision, 2); XCTAssertEqual(queue.entries[0].track.sources.count, 5)
        XCTAssertEqual(store.snapshot().2, 1, "one CAS; resolution itself does not write history")
        let again = try await service.beginCandidateDiscovery(occurrenceID: "entry-1", expectedQueueRevision: 2)
        let known = try await again.snapshot(discover: true, force: false)
        XCTAssertEqual(known.track.sources.count, 5)
        await again.close(retainingAttemptID: nil)
        let accepted = try await service.record(.init(attemptId: result.candidate.attemptId, occurrenceId: "entry-1",
            resourceId: result.candidate.resourceId, kind: .playing, position: 23))
        XCTAssertTrue(accepted)
        await service.close()
    }
    func testManualExactInstallationLeavesSameNamedPeerUnrequested() async throws {
        let store = try MusicNativeCandidateDiscoveryFixture.store(), scripts = MusicNativeCandidateDiscoveryScripts()
        let second = "33333333-3333-4333-8333-333333333333"
        await scripts.installed([MusicNativeCandidateDiscoveryFixture.installation, second])
        let service = try await NativeMusicFixture.service(store, scripts: scripts)
        let list = try await service.beginCandidateDiscovery(occurrenceID: "entry-1", expectedQueueRevision: 1)
        let snapshot = try await list.snapshot(discover: false, force: false)
        let option = try XCTUnwrap(snapshot.options.first { $0.playback.installationId == second })
        await list.close(retainingAttemptID: nil)
        let consumer = MusicCandidateDiscoveryConsumer(provider: service)
        let result = try await consumer.resolve(occurrenceID: "entry-1", expectedQueueRevision: 1,
            mode: .manual(option.target(quality: "128k")))
        let calls = await scripts.calls()
        XCTAssertEqual(calls.map(\.installationID), [second])
        XCTAssertEqual(result.target.selection.sourceKey, option.playback.sourceKey)
        XCTAssertEqual(store.snapshot().2, 0)
        await service.close()
    }
}
