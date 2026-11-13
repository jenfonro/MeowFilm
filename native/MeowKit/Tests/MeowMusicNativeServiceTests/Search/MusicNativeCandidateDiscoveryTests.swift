import XCTest
import MeowMusicFeature
@testable import MeowMusicNativeService

final class MusicNativeCandidateDiscoveryTests: XCTestCase {
    func testColdIdleCandidatesLoadOnlyWhenSelectedAndKeepTheirRealExecutionIdentity() async throws {
        for state in [MusicScriptSourceItem.State.idle, .starting] {
            let scripts = MusicNativeCandidateDiscoveryScripts()
            await scripts.directory(state)
            let service = try await NativeMusicFixture.service(try MusicNativeCandidateDiscoveryFixture.store(), scripts: scripts)
            let session = try await service.beginCandidateDiscovery(occurrenceID: "entry-1", expectedQueueRevision: 1)
            let snapshot = try await session.snapshot(discover: false, force: false)
            let target = try XCTUnwrap(MusicNativeCandidateDiscoveryFixture.target(snapshot, platform: "tx"))
            XCTAssertEqual(target.selection.generation, 0)
            XCTAssertNotNil(target.selection.scriptIdentity)
            let before = await scripts.calls(); XCTAssertTrue(before.isEmpty)
            let candidate = try await session.resolve(target, attemptID: UUID().uuidString.lowercased())
            XCTAssertEqual(candidate.url.host, "media.invalid")
            let calls = await scripts.calls()
            XCTAssertEqual(calls.count, 1); XCTAssertEqual(calls.first?.generation, 0)
            let live = try await scripts.source(installationID: target.selection.installationId)
            XCTAssertEqual(live.state, .ready); XCTAssertEqual(live.generation, 7)
            let resolved = MusicPlaybackSelection(installationId: live.installationID, generation: 7,
                siteId: target.selection.siteId, sourceKey: target.selection.sourceKey, quality: target.quality,
                scriptIdentity: live.identity)
            XCTAssertEqual(candidate.resolvedSelection, resolved)
            let attempt = await service.attempt
            XCTAssertEqual(attempt?.selection, resolved)
            XCTAssertEqual(attempt?.scriptSource, live)
            let ready = try await session.snapshot(discover: false, force: false)
            let old = try XCTUnwrap(MusicNativeCandidateDiscoveryFixture.target(ready, platform: "tx"))
            XCTAssertEqual(old.selection, resolved)
            await scripts.configure(generation: 8)
            await scriptExpectFailure(.targetChanged) {
                try await session.resolve(old, attemptID: UUID().uuidString.lowercased())
            }
            let afterReload = await scripts.calls(); XCTAssertEqual(afterReload.count, 1)
            await session.close(retainingAttemptID: candidate.attemptId)
            await service.close()
        }
    }

    func testBothSourcePickerAndAutomaticCandidatesKeepEveryOneOfThirtyThreeIdleInstallations() async throws {
        let scripts = MusicNativeCandidateDiscoveryScripts()
        let ids = (1...33).map { String(format: "00000000-0000-4000-8000-%012d", $0) }
        await scripts.installed(ids); await scripts.directory(.idle)
        let service = try await NativeMusicFixture.service(try MusicNativeCandidateDiscoveryFixture.store(), scripts: scripts)
        let availability = try await service.sourceAvailability(for: .init(kind: .queue, id: "entry-1"))
        XCTAssertEqual(availability.options.count, 33)
        XCTAssertEqual(Set(availability.options.map(\.installationId)), Set(ids))
        XCTAssertTrue(availability.options.allSatisfy { $0.generation == 0 && $0.selection(quality: "128k").scriptIdentity != nil })
        let session = try await service.beginCandidateDiscovery(occurrenceID: "entry-1", expectedQueueRevision: 1)
        let snapshot = try await session.snapshot(discover: false, force: false)
        XCTAssertEqual(snapshot.options.count, 33)
        XCTAssertEqual(Set(snapshot.options.map { $0.playback.installationId }), Set(ids))
        let before = await scripts.calls(); XCTAssertTrue(before.isEmpty)
        let last = try XCTUnwrap(snapshot.options.first { $0.playback.installationId == ids.last })
        let candidate = try await session.resolve(last.target(quality: "128k"), attemptID: UUID().uuidString.lowercased())
        let calls = await scripts.calls()
        XCTAssertEqual(calls.map(\.installationID), [ids[32]])
        await session.close(retainingAttemptID: candidate.attemptId); await service.close()
    }

    func testFailedInstallationsStayOutOfBothCandidateListsAndDoNotExecute() async throws {
        let scripts = MusicNativeCandidateDiscoveryScripts()
        await scripts.directory(.failed)
        let service = try await NativeMusicFixture.service(try MusicNativeCandidateDiscoveryFixture.store(), scripts: scripts)
        let availability = try await service.sourceAvailability(for: .init(kind: .queue, id: "entry-1"))
        XCTAssertTrue(availability.options.isEmpty)
        let session = try await service.beginCandidateDiscovery(occurrenceID: "entry-1", expectedQueueRevision: 1)
        let snapshot = try await session.snapshot(discover: false, force: false)
        XCTAssertTrue(snapshot.options.isEmpty)
        let calls = await scripts.calls(); XCTAssertTrue(calls.isEmpty)
        let live = await scripts.sources()
        XCTAssertEqual(live.first?.state, .failed)
        await session.close(retainingAttemptID: nil); await service.close()
    }

    func testColdSelectionRejectsChangedContentBeforeResolveAndAfterLazyLoading() async throws {
        for afterCall in [false, true] {
            let scripts = MusicNativeCandidateDiscoveryScripts()
            await scripts.directory(.idle)
            let service = try await NativeMusicFixture.service(try MusicNativeCandidateDiscoveryFixture.store(), scripts: scripts)
            let session = try await service.beginCandidateDiscovery(occurrenceID: "entry-1", expectedQueueRevision: 1)
            let snapshot = try await session.snapshot(discover: false, force: false)
            let target = try XCTUnwrap(MusicNativeCandidateDiscoveryFixture.target(snapshot, platform: "tx"))
            await scripts.configure(generation: 0, version: afterCall ? "1" : "2", mutateAfterResolve: afterCall)
            await scriptExpectFailure(.targetChanged) {
                try await session.resolve(target, attemptID: UUID().uuidString.lowercased())
            }
            let calls = await scripts.calls(); XCTAssertEqual(calls.count, afterCall ? 1 : 0)
            let played = try await service.playedSongs(); XCTAssertTrue(played.isEmpty)
            await session.close(retainingAttemptID: nil); await service.close()
        }
    }

    func testParallelNativeDiscoveryAndExactPlaybackUseExistingFactWriter() async throws {
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
        let queue = try await service.queue()
        XCTAssertEqual(queue.revision, 1); XCTAssertEqual(queue.currentId, "entry-1")
        XCTAssertEqual(queue.entries[0].track.songId, snapshot.track.songId)
        let cached = try await service.beginCandidateDiscovery(occurrenceID: "entry-1", expectedQueueRevision: 1)
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
    func testDiscoveryDoesNotNeedQueueWriteAuthority() async throws {
        let store = try MusicNativeCandidateDiscoveryFixture.store()
        let cache = MusicNativeMemorySourceDiscoveryCache(clock: { NativeMusicFixture.date })
        let service = try await NativeMusicFixture.service(store, scripts: MusicNativeCandidateDiscoveryScripts(),
            qq: MusicNativeCandidateDiscoveryQQ(), http: MusicNativeCandidateDiscoveryHTTP(), sourceDiscovery: cache)
        let before = store.snapshot()
        store.fail(.storageConflict)
        let session = try await service.beginCandidateDiscovery(occurrenceID: "entry-1", expectedQueueRevision: 1)
        let snapshot = try await session.snapshot(discover: true, force: false)
        XCTAssertEqual(snapshot.track.sources.count, 5)
        XCTAssertEqual(store.snapshot().0, before.0)
        XCTAssertEqual(store.snapshot().2, before.2)
        let next = try await service.beginCandidateDiscovery(occurrenceID: "entry-1", expectedQueueRevision: 1)
        let cached = try await next.snapshot(discover: false, force: false)
        XCTAssertEqual(cached.track.sources.count, 5)
        await next.close(retainingAttemptID: nil)
        store.fail(nil)
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
    func testProductionConsumerFindsMissingSourceAndResolvesWithoutQueueWrite() async throws {
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
        // Only the platform the injected script can play is searched.
        XCTAssertEqual(searchedHTTP, ["wy"])
        let queue = try await service.queue()
        XCTAssertEqual(queue.revision, 1); XCTAssertEqual(queue.entries[0].track.sources.count, 2)
        XCTAssertEqual(queue.entries[0].track.sources.first?.platform, "tx", "the seed stays the primary recording")
        XCTAssertEqual(store.snapshot().2, 0, "source discovery and resolution do not write the queue")
        let again = try await service.beginCandidateDiscovery(occurrenceID: "entry-1", expectedQueueRevision: 1)
        let known = try await again.snapshot(discover: true, force: false)
        XCTAssertEqual(known.track.sources.count, 2)
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
    func testPlayableKnownSourceNeverSearchesAnyPlatform() async throws {
        let store = try MusicNativeCandidateDiscoveryFixture.store(), scripts = MusicNativeCandidateDiscoveryScripts()
        let qq = MusicNativeCandidateDiscoveryQQ(), http = MusicNativeCandidateDiscoveryHTTP()
        let service = try await NativeMusicFixture.service(store, scripts: scripts, qq: qq, http: http)
        let consumer = MusicCandidateDiscoveryConsumer(provider: service)
        let result = try await consumer.resolve(occurrenceID: "entry-1", expectedQueueRevision: 1, mode: .automatic())
        XCTAssertEqual(result.target.selection.siteId, "tx"); XCTAssertEqual(result.attempts, 1)
        let httpCalls = await http.calls(), qqCalls = await qq.calls()
        XCTAssertTrue(httpCalls.isEmpty); XCTAssertTrue(qqCalls.isEmpty)
        XCTAssertEqual(store.snapshot().2, 0, "source discovery never writes the queue")
        await service.close()
    }
    func testAutomaticScopeSkipsSignedOutAccountsButRefreshSearchesEveryEnabledPlatform() async throws {
        let seed = try MusicModel.normalize(MusicNativeCandidateDiscoveryFixture.track("kg", id: String(repeating: "A", count: 32)))
        for signedIn in [true, false] {
            let store = try MusicNativeCandidateDiscoveryFixture.store(song: seed)
            let accounts = MusicNativeScopedAccounts(signedIn ? [.netease, .qq] : [.netease])
            let qq = MusicNativeCandidateDiscoveryQQ(), http = MusicNativeCandidateDiscoveryHTTP()
            let service = NativeMusicService(document: store, accounts: accounts, qq: qq,
                enabledPlatforms: [.qq, .kugou], transport: http, clock: { NativeMusicFixture.date })
            try await service.setAccess(available: true, generation: 1)
            // Netease is playable but disabled; Kugou already has complete data.
            let automatic = try await service.beginCandidateDiscovery(occurrenceID: "entry-1", expectedQueueRevision: 1)
            _ = try await automatic.snapshot(discover: true, force: false)
            await automatic.close(retainingAttemptID: nil)
            let automaticHTTP = await http.calls(), automaticQQ = await qq.calls()
            XCTAssertTrue(automaticHTTP.isEmpty)
            XCTAssertEqual(automaticQQ.count, signedIn ? 1 : 0, "a signed-out QQ account cannot play a QQ match")
            let refresh = try await service.beginCandidateDiscovery(occurrenceID: "entry-1", expectedQueueRevision: 1)
            _ = try await refresh.snapshot(discover: true, force: true)
            await refresh.close(retainingAttemptID: nil)
            let refreshedHTTP = await http.calls(), refreshedQQ = await qq.calls()
            XCTAssertEqual(refreshedHTTP, ["kg"])
            XCTAssertEqual(refreshedQQ.count, automaticQQ.count + 1)
            await service.close()
        }
    }
    func testSevenDayRecheckFollowsOnlyContentFailures() async throws {
        let day: Double = 24 * 60 * 60 * 1000, now = NativeMusicFixture.date.timeIntervalSince1970 * 1000
        let cases: [(age: Double?, error: MusicServiceError, searches: Int, failure: MusicServiceError)] = [
            (nil, .notFound, 1, .notFound), (3 * day, .notFound, 0, .notFound),
            (8 * day, .previewOnly, 1, .previewOnly), (nil, .timeout, 0, .sourceUnavailable)
        ]
        for value in cases {
            let seed = try MusicNativeCandidateDiscoveryFixture.song(), row = try XCTUnwrap(seed["sources"].array.first)
            let store = try MusicNativeCandidateDiscoveryFixture.store(song: seed)
            let cache = MusicNativeMemorySourceDiscoveryCache(clock: { NativeMusicFixture.date })
            if let age = value.age {
                await cache.record(fingerprint: MusicModel.fingerprint(seed), duration: 180, platform: "tx", checkedAt: now - age,
                    sources: [.init(sourceKey: row["sourceKey"].string, source: "tx", musicInfo: row["musicInfo"].json)])
            }
            let scripts = NativeMusicScriptFixture(), qq = MusicNativeCandidateDiscoveryQQ()
            await scripts.configure(error: value.error)
            let service = try await NativeMusicFixture.service(store, scripts: scripts, qq: qq, sourceDiscovery: cache)
            let consumer = MusicCandidateDiscoveryConsumer(provider: service)
            do { _ = try await consumer.resolve(occurrenceID: "entry-1", expectedQueueRevision: 1, mode: .automatic()); XCTFail("exhausted") }
            catch { XCTAssertEqual((error as? MusicCandidateDiscoveryFailure)?.failure, value.failure) }
            let searches = await qq.calls()
            XCTAssertEqual(searches.count, value.searches, "age \(String(describing: value.age)) error \(value.error)")
            if value.searches > 0 {
                let entries = await cache.entries(fingerprint: MusicModel.fingerprint(seed), duration: 180)
                XCTAssertEqual(entries["tx"]?.checkedAt, now, "only an actual search refreshes the check time")
            }
            await service.close()
        }
    }
}

/// Account adapter whose local scope is fixed by the test; it never answers options.
private actor MusicNativeScopedAccounts: MusicNativeAccountProviding, MusicNativeCandidateScopeProviding {
    let platforms: Set<MusicAccountPlatform>
    init(_ platforms: Set<MusicAccountPlatform>) { self.platforms = platforms }
    func candidatePlatforms() -> Set<MusicAccountPlatform> { platforms }
    func nativeOptions(platform: MusicAccountPlatform, nativeID: String, sourceKey: String) -> [MusicPlaybackOption] { [] }
    func nativeResolve(platform: MusicAccountPlatform, nativeID: String,
                       selection: MusicPlaybackSelection) throws -> MusicNativeAudio { throw MusicServiceError.unsupported }
    func nativeRecommendations(platform: MusicAccountPlatform, kind: String, cursor: String?) throws -> MusicNativeTrackPage {
        throw MusicServiceError.unsupported
    }
    func nativeCollections(platform: MusicAccountPlatform, cursor: String?) throws -> MusicAccountCollectionPage {
        throw MusicServiceError.unsupported
    }
    func nativeTracks(platform: MusicAccountPlatform, collectionID: String, cursor: String?) throws -> MusicNativeTrackPage {
        throw MusicServiceError.unsupported
    }
}
