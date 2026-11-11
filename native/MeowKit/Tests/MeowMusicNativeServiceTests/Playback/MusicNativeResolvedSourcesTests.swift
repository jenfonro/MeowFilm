import Foundation
import XCTest
import MeowMusicFeature
@testable import MeowMusicNativeService

final class MusicNativeResolvedSourcesTests: XCTestCase {
    private func settle(_ inspection: any MusicSourceInspectionSession) async throws -> MusicResolvedSourcesSnapshot {
        _ = try await inspection.loadMore(refresh: false)
        return try await MusicCandidateDiscoveryDeadline.run(seconds: 3, timing: .init()) {
            for try await snapshot in await inspection.updates() where !snapshot.isLoading { return snapshot }
            throw MusicServiceError.closed
        }
    }

    func testInspectionPublishesRealLazyLoadGenerationAndRejectsReloadBeforeAdoption() async throws {
        for reload in [false, true] {
            let song = try MusicNativeCandidateDiscoveryFixture.song()
            let cache = MusicNativeMemorySourceDiscoveryCache(clock: { NativeMusicFixture.date })
            for platform in MusicNativeSearchProtocol.platforms {
                await cache.record(fingerprint: MusicModel.fingerprint(song), duration: 180, platform: platform,
                    checkedAt: NativeMusicFixture.date.timeIntervalSince1970 * 1000, sources: [])
            }
            let scripts = MusicNativeCandidateDiscoveryScripts()
            await scripts.supported(["tx"]); await scripts.directory(.idle)
            let store = try MusicNativeCandidateDiscoveryFixture.store(song: song)
            let service = try await NativeMusicFixture.service(store, scripts: scripts, sourceDiscovery: cache)
            let inspection = try await service.inspectSources(for: .init(kind: .queue, id: "entry-1"))
            let snapshot = try await settle(inspection)
            let choice = try XCTUnwrap(snapshot.sources.first)
            XCTAssertGreaterThan(choice.selection.generation, 0)
            XCTAssertNotNil(choice.selection.scriptIdentity)
            let requests = await scripts.calls()
            XCTAssertEqual(requests.first?.source.state, .idle)
            XCTAssertEqual(requests.first?.generation, 0)
            if reload {
                await scripts.configure(generation: choice.selection.generation + 1)
                await scriptExpectFailure(.targetChanged) {
                    try await inspection.adopt(sourceID: choice.id, occurrenceID: "entry-1", expectedQueueRevision: 1)
                }
            } else {
                let selected = try await inspection.adopt(sourceID: choice.id, occurrenceID: "entry-1", expectedQueueRevision: 1)
                XCTAssertEqual(selected, choice.selection)
            }
            let played = try await service.playedSongs(); XCTAssertTrue(played.isEmpty)
            await inspection.close(); await service.close()
        }
    }

    func testInspectionMergesCachedSourcesWithoutSearchAndAdoptsThroughCurrentQueueCAS() async throws {
        let song = try MusicNativeCandidateDiscoveryFixture.song()
        let cached = try MusicModel.normalize(MusicNativeCandidateDiscoveryFixture.track("wy", id: "cached"))
        let row = try XCTUnwrap(cached["sources"].array.first)
        let cache = MusicNativeMemorySourceDiscoveryCache(clock: { NativeMusicFixture.date })
        let fingerprint = MusicModel.fingerprint(song), at = NativeMusicFixture.date.timeIntervalSince1970 * 1000
        for platform in MusicNativeSearchProtocol.platforms {
            await cache.record(fingerprint: fingerprint, duration: 180, platform: platform, checkedAt: at,
                sources: platform == "wy" ? [.init(sourceKey: row["sourceKey"].string, source: platform,
                    musicInfo: row["musicInfo"].json)] : [])
        }
        let store = try MusicNativeCandidateDiscoveryFixture.store(song: song), slow = NativeMusicGate()
        await slow.release()
        let accounts = MusicInspectionConcurrencyAccounts(slow: slow, onLower: {})
        let http = MusicNativeCandidateDiscoveryHTTP(), qq = MusicNativeCandidateDiscoveryQQ()
        let service = try await NativeMusicFixture.service(store, accounts: accounts, qq: qq, http: http, sourceDiscovery: cache)
        let before = store.snapshot().0
        let inspection = try await service.inspectSources(for: .init(kind: .queue, id: "entry-1"))
        let snapshot = try await settle(inspection)
        let choice = try XCTUnwrap(snapshot.sources.first { $0.selection.siteId == "wy" })
        let calls = await http.calls(), qqCalls = await qq.calls()
        XCTAssertTrue(calls.isEmpty); XCTAssertTrue(qqCalls.isEmpty)
        XCTAssertEqual(store.snapshot().0, before)
        let selected = try await inspection.adopt(sourceID: choice.id, occurrenceID: "entry-1", expectedQueueRevision: 1)
        XCTAssertEqual(selected, choice.selection)
        let queue = try await service.queue()
        XCTAssertEqual(queue.revision, 2); XCTAssertEqual(queue.currentId, "entry-1")
        XCTAssertTrue(queue.entries[0].track.sources.contains { $0.platform == "wy" && $0.nativeId == "cached" })
        let history = try await service.playedSongs()
        XCTAssertTrue(history.isEmpty)
        await inspection.close(); await service.close()
    }

    func testRejectedPeerIsRemovedThroughQueueCASAndNeverSelectedAgain() async throws {
        let primary = try MusicNativeCandidateDiscoveryFixture.song()
        let peer = try MusicModel.normalize(MusicNativeCandidateDiscoveryFixture.track("wy", id: "cached"))
        let row = try XCTUnwrap(peer["sources"].array.first), key = row["sourceKey"].string
        let song = try MusicModel.mergeSources(primary, adding: [row])
        let cache = MusicNativeMemorySourceDiscoveryCache(clock: { NativeMusicFixture.date })
        let fingerprint = MusicModel.fingerprint(song), at = NativeMusicFixture.date.timeIntervalSince1970 * 1000
        for platform in MusicNativeSearchProtocol.platforms {
            await cache.record(fingerprint: fingerprint, duration: 180, platform: platform, checkedAt: at,
                sources: platform == "wy" ? [.init(sourceKey: key, source: platform, musicInfo: row["musicInfo"].json)] : [])
        }
        let store = try MusicNativeCandidateDiscoveryFixture.store(song: song), slow = NativeMusicGate()
        await slow.release()
        let accounts = MusicInspectionConcurrencyAccounts(slow: slow, onLower: {})
        let http = MusicNativeCandidateDiscoveryHTTP(), qq = MusicNativeCandidateDiscoveryQQ()
        let service = try await NativeMusicFixture.service(store, accounts: accounts, qq: qq, http: http, sourceDiscovery: cache)
        let inspection = try await service.inspectSources(for: .init(kind: .queue, id: "entry-1"))
        let before = try await settle(inspection)
        XCTAssertTrue(before.sources.contains { $0.selection.sourceKey == key })
        let bytes = store.snapshot().0, primaryKey = try XCTUnwrap(primary["sources"].array.first)["sourceKey"].string
        do { try await inspection.reject(sourceKey: primaryKey, occurrenceID: "entry-1", expectedQueueRevision: 1); XCTFail("primary") }
        catch { XCTAssertEqual(error as? MusicServiceError, .invalidRequest) }
        do { try await inspection.reject(sourceKey: key, occurrenceID: "entry-1", expectedQueueRevision: 0); XCTFail("stale") }
        catch { XCTAssertEqual(error as? MusicServiceError, .targetChanged) }
        XCTAssertEqual(store.snapshot().0, bytes, "a refused rejection writes nothing")
        try await inspection.reject(sourceKey: key, occurrenceID: "entry-1", expectedQueueRevision: 1)
        let queue = try await service.queue()
        XCTAssertEqual(queue.revision, 2); XCTAssertEqual(queue.entries[0].track.sources.map(\.platform), ["tx"])
        let after = try await settle(inspection)
        XCTAssertFalse(after.sources.contains { $0.selection.sourceKey == key }, "the cached match is not merged back")
        XCTAssertTrue(after.sources.contains { $0.selection.siteId == "tx" })
        await inspection.close()
        let session = try await service.beginCandidateDiscovery(occurrenceID: "entry-1", expectedQueueRevision: 2)
        let snapshot = try await session.snapshot(discover: true, force: false)
        XCTAssertFalse(snapshot.track.sources.contains { $0.sourceKey == key })
        XCTAssertFalse(snapshot.options.contains { $0.playback.sourceKey == key })
        await session.close(retainingAttemptID: nil)
        let calls = await http.calls(), qqCalls = await qq.calls()
        XCTAssertTrue(calls.isEmpty); XCTAssertTrue(qqCalls.isEmpty)
        await service.close()
    }

    func testExcludedSourceStillInTheQueueIsNotOfferedOrPlayedFromAudioCache() async throws {
        let clock = MusicRecoveryTestClock()
        // A Kuwo primary has no account options; the QQ peer is the cached, account-backed source.
        let primary = try MusicModel.normalize(MusicNativeCandidateDiscoveryFixture.track("kw", id: "123"))
        let peer = try MusicModel.normalize(MusicNativeCandidateDiscoveryFixture.track("tx", id: "101"))
        let row = try XCTUnwrap(peer["sources"].array.first), key = row["sourceKey"].string
        let song = try MusicModel.mergeSources(primary, adding: [row])
        let store = try MusicNativeCandidateDiscoveryFixture.store(song: song)
        let d = try MusicDocument(store.read(), runID: "exclusion")
        let accounts = MusicRecoveryTestAccounts(platform: .qq, nativeID: "101", clock: clock)
        let file = MusicRecoveryTestFileCache(hit: .init(identity: .init(scope: String(repeating: "c", count: 64),
            songID: song["songId"].string, sourceKey: key, platform: "tx", nativeID: "101", requestedQuality: "flac",
            playbackSignature: NativeMusicService.audioCacheSignature(song: song, playback: d.playback), provider: "official:recovery-synthetic"),
            fileURL: URL(fileURLWithPath: "/synthetic-cache/recording.flac"), duration: 180,
            measuredQuality: "flac", expiresAt: clock.now().addingTimeInterval(3600)))
        let cache = MusicNativeMemorySourceDiscoveryCache(clock: { clock.now() })
        let service = NativeMusicService(document: store, accounts: accounts, qq: NativeMusicQQFixture(),
            sourceDiscovery: cache, audioFileCache: file, transport: NativeMusicNoNetwork(), clock: { clock.now() })
        try await service.setAccess(available: true, generation: 1)
        let hit = try await service.cachedPlayback(attemptID: UUID().uuidString.lowercased(), occurrenceID: "entry-1", selection: nil)
        XCTAssertNotNil(hit)
        let offered = (try await service.sourceAvailability(for: .init(kind: .queue, id: "entry-1"))).options
        XCTAssertTrue(offered.contains { $0.sourceKey == key })
        try await service.excludeRecordingSource(song, sourceKey: key)
        let miss = try await service.cachedPlayback(attemptID: UUID().uuidString.lowercased(), occurrenceID: "entry-1", selection: nil)
        XCTAssertNil(miss, "a cached file of another recording is not this song")
        let options = (try await service.sourceAvailability(for: .init(kind: .queue, id: "entry-1"))).options
        XCTAssertFalse(options.contains { $0.sourceKey == key })
        let session = try await service.beginCandidateDiscovery(occurrenceID: "entry-1", expectedQueueRevision: 1)
        let snapshot = try await session.snapshot(discover: false, force: false)
        XCTAssertFalse(snapshot.options.contains { $0.playback.sourceKey == key })
        await session.close(retainingAttemptID: nil); await service.close()
    }

    func testClosedInspectorCannotReject() async throws {
        let f = try await MusicRecoveryTestFixture.make()
        let inspection = try await f.service.inspectSources(for: .init(kind: .queue, id: "entry-1"))
        await inspection.close()
        let before = f.store.snapshot().0
        do { try await inspection.reject(sourceKey: "any", occurrenceID: "entry-1", expectedQueueRevision: 1); XCTFail("closed") }
        catch { XCTAssertEqual(error as? MusicServiceError, .closed) }
        XCTAssertEqual(f.store.snapshot().0, before)
        await f.service.close()
    }

    func testInspectionManualRefreshBypassesSuccessfulEmptyCacheChecks() async throws {
        let song = try MusicNativeCandidateDiscoveryFixture.song()
        let cache = MusicNativeMemorySourceDiscoveryCache(clock: { NativeMusicFixture.date })
        for platform in MusicNativeSearchProtocol.platforms {
            await cache.record(fingerprint: MusicModel.fingerprint(song), duration: 180, platform: platform,
                checkedAt: NativeMusicFixture.date.timeIntervalSince1970 * 1000, sources: [])
        }
        let store = try MusicNativeCandidateDiscoveryFixture.store(song: song), slow = NativeMusicGate()
        await slow.release()
        let accounts = MusicInspectionConcurrencyAccounts(slow: slow, onLower: {})
        let http = MusicNativeCandidateDiscoveryHTTP(), qq = MusicNativeCandidateDiscoveryQQ()
        let service = try await NativeMusicFixture.service(store, accounts: accounts, qq: qq, http: http, sourceDiscovery: cache)
        let inspection = try await service.inspectSources(for: .init(kind: .queue, id: "entry-1"))
        _ = try await settle(inspection)
        let initial = await http.calls(), initialQQ = await qq.calls()
        XCTAssertTrue(initial.isEmpty); XCTAssertTrue(initialQQ.isEmpty)
        let before = store.snapshot().0
        _ = try await inspection.loadMore(refresh: true)
        _ = try await settle(inspection)
        let refreshed = await http.calls(), refreshedQQ = await qq.calls()
        XCTAssertEqual(Set(refreshed), ["wy", "kg", "kw", "mg"]); XCTAssertEqual(refreshed.count, 4)
        XCTAssertEqual(refreshedQQ.count, 1)
        XCTAssertEqual(store.snapshot().0, before)
        await inspection.close(); await service.close()
    }

    func testInspectionAndCloseKeepCurrentAttemptDocumentAndListeningFactsUntouched() async throws {
        let f = try await MusicRecoveryTestFixture.make()
        let before = f.store.snapshot().0
        let inspection = try await f.service.inspectSources(for: .init(kind: .queue, id: "entry-1"))
        let snapshot = try await settle(inspection)
        XCTAssertFalse(snapshot.sources.isEmpty)
        XCTAssertTrue(snapshot.sources.allSatisfy { $0.delivery == nil },
                      "Requested capability cannot prove actual quality")
        XCTAssertEqual(f.store.snapshot().0, before)
        let history = try await f.service.playedSongs(); XCTAssertTrue(history.isEmpty)
        await inspection.close()
        let accepted = try await f.service.record(f.fact(f.original, kind: .playing))
        XCTAssertTrue(accepted, "Inspection must not replace/cancel the existing media attempt")
        await f.service.close()
    }

    func testSelectionOnlyAdoptsIntoTheAlreadyCurrentOccurrenceAndThenUsesExistingResolver() async throws {
        let f = try await MusicRecoveryTestFixture.make()
        let inspection = try await f.service.inspectSources(for: .init(kind: .queue, id: "entry-1"))
        let snapshot = try await settle(inspection)
        let source = try XCTUnwrap(snapshot.sources.first)
        do {
            _ = try await inspection.adopt(sourceID: source.id, occurrenceID: "entry-2", expectedQueueRevision: 1)
            XCTFail("Inspection selected another occurrence")
        } catch { XCTAssertEqual(error as? MusicServiceError, .targetChanged) }
        let selected = try await inspection.adopt(sourceID: source.id, occurrenceID: "entry-1", expectedQueueRevision: 1)
        XCTAssertEqual(selected, source.selection)
        let history = try await f.service.playedSongs(); XCTAssertTrue(history.isEmpty)
        let candidate = try await f.service.resolve(attemptId: UUID().uuidString.lowercased(),
                                                   occurrenceId: "entry-1", selection: selected)
        XCTAssertEqual(candidate.occurrenceId, "entry-1"); XCTAssertEqual(candidate.startPosition, 0)
        await inspection.close()
        let accepted = try await f.service.record(f.fact(candidate, kind: .playing))
        XCTAssertTrue(accepted)
        await f.service.close()
    }

    func testAccountChangeCannotAdoptPreviouslyResolvedChoice() async throws {
        let f = try await MusicRecoveryTestFixture.make()
        let inspection = try await f.service.inspectSources(for: .init(kind: .queue, id: "entry-1"))
        let snapshot = try await settle(inspection)
        let source = try XCTUnwrap(snapshot.sources.first), before = f.store.snapshot().0
        await f.accounts.rotate()
        do {
            _ = try await inspection.adopt(sourceID: source.id, occurrenceID: "entry-1", expectedQueueRevision: 1)
            XCTFail("Stale inspected account adopted")
        } catch { XCTAssertEqual(error as? MusicServiceError, .targetChanged) }
        XCTAssertEqual(f.store.snapshot().0, before)
        await inspection.close(); await f.service.close()
    }

    func testClosedInspectorCannotIssueRequestsOrAdoptRows() async throws {
        let f = try await MusicRecoveryTestFixture.make()
        let inspection = try await f.service.inspectSources(for: .init(kind: .queue, id: "entry-1"))
        await inspection.close()
        let before = await f.accounts.counts()
        do { _ = try await inspection.loadMore(refresh: true); XCTFail("Closed inspector loaded") }
        catch { XCTAssertEqual(error as? MusicServiceError, .closed) }
        let after = await f.accounts.counts()
        XCTAssertEqual(before.options, after.options); XCTAssertEqual(before.resolves, after.resolves)
        await f.service.close()
    }

    func testIdleCompletedListWithdrawsOnAccountRevocationWithoutRefreshOrAdopt() async throws {
        let f = try await MusicRecoveryTestFixture.make(authorized: true)
        let inspection = try await f.service.inspectSources(for: .init(kind: .queue, id: "entry-1"))
        let snapshot = try await settle(inspection)
        XCTAssertFalse(snapshot.sources.isEmpty)
        let updates = await inspection.updates()
        await f.accounts.rotate()
        do {
            try await MusicCandidateDiscoveryDeadline.run(seconds: 3, timing: .init()) {
                for try await _ in updates {}
            }
            XCTFail("Revocation left an idle source list alive")
        } catch { XCTAssertEqual(error as? MusicServiceError, .staleAccess) }
        await inspection.close(); await f.service.close()
    }

    func testSlowProviderDoesNotBlockExplicitExpansionOfReadyPeer() async throws {
        var song = try MusicModel.normalize(MusicNativeCandidateDiscoveryFixture.track("wy", id: "101"))
        let peer = try MusicModel.normalize(MusicNativeCandidateDiscoveryFixture.track("tx", id: "102"))
        song["sources"] = .array(song["sources"].array + peer["sources"].array)
        let store = try MusicNativeCandidateDiscoveryFixture.store(song: song)
        let slow = NativeMusicGate(), lower = expectation(description: "Ready peer reaches next tier")
        let accounts = MusicInspectionConcurrencyAccounts(slow: slow, onLower: { lower.fulfill() })
        let service = NativeMusicService(document: store, accounts: accounts, qq: NativeMusicQQFixture(),
            enabledPlatforms: [.netease, .qq], transport: NativeMusicNoNetwork(), clock: { NativeMusicFixture.date })
        try await service.setAccess(available: true, generation: 1)
        _ = try await service.updateSettings(.init(ifRevision: 0, qualityEnabled: ["lossless", "standard"],
                                                   qualityOrder: ["lossless", "standard"]))
        let inspection = try await service.inspectSources(for: .init(kind: .queue, id: "entry-1"))
        _ = try await inspection.loadMore(refresh: false)
        await slow.started()
        let fast = try await MusicCandidateDiscoveryDeadline.run(seconds: 3, timing: .init()) {
            for try await value in await inspection.updates()
                where value.sources.contains(where: { $0.selection.siteId == "tx" && $0.delivery?.quality == "flac" }) {
                return value
            }
            throw MusicServiceError.closed
        }
        XCTAssertTrue(fast.isLoading, "Other provider is still physically in flight")
        _ = try await inspection.loadMore(refresh: false)
        await fulfillment(of: [lower], timeout: 2)
        await inspection.close()
        await slow.release()
        let counts = await accounts.maximums()
        XCTAssertLessThanOrEqual(counts.total, 3)
        XCTAssertLessThanOrEqual(counts.perProvider, 1)
        let history = try await service.playedSongs(); XCTAssertTrue(history.isEmpty)
        await service.close()
    }
}

private actor MusicInspectionConcurrencyAccounts: MusicNativeAccountProviding {
    let slow: NativeMusicGate
    let onLower: @Sendable () -> Void
    var held = false
    var active: [String: Int] = [:], total = 0, maximumTotal = 0, maximumProvider = 0
    init(slow: NativeMusicGate, onLower: @escaping @Sendable () -> Void) {
        self.slow = slow; self.onLower = onLower
    }
    func maximums() -> (total: Int, perProvider: Int) { (maximumTotal, maximumProvider) }
    func nativeOptions(platform: MusicAccountPlatform, nativeID: String, sourceKey: String) -> [MusicPlaybackOption] {
        [.init(installationId: "official:inspection-" + platform.rawValue, generation: 1,
            siteId: platform.rawValue, sourceKey: sourceKey, qualities: ["flac", "128k"],
            title: platform.title, version: "1", contentHash: String(repeating: "a", count: 64))]
    }
    func nativeResolve(platform: MusicAccountPlatform, nativeID: String,
                       selection: MusicPlaybackSelection) async throws -> MusicNativeAudio {
        let key = selection.sourceKey
        active[key, default: 0] += 1; total += 1
        maximumTotal = max(total, maximumTotal); maximumProvider = max(active[key]!, maximumProvider)
        defer { active[key, default: 1] -= 1; total -= 1 }
        if platform == .netease, !held { held = true; await slow.wait() }
        if platform == .qq, selection.quality == "128k" { onLower() }
        return .init(url: URL(string: "https://media.invalid/" + platform.rawValue + "." + selection.quality)!,
                     delivery: .init(quality: selection.quality, evidence: .officialResponse))
    }
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
