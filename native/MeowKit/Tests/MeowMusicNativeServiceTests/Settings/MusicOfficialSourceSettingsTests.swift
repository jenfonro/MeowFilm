import Foundation
import MeowMusicFeature
import XCTest
@testable import MeowMusicNativeService

final class MusicOfficialSourceSettingsTests: XCTestCase {
    func testDefaultIsReadOnlyAndThreeIndependentSwitchesPersistIncludingAllOff() async throws {
        let store = NativeMusicMemoryDocument(), service = try await NativeMusicFixture.service(store)
        var state = try await service.state()
        XCTAssertEqual(state.officialSources, MusicAccountPlatform.allCases)
        XCTAssertNil(store.snapshot().0); XCTAssertEqual(store.snapshot().2, 0)
        for platform in MusicAccountPlatform.allCases {
            let remaining = state.officialSources.filter { $0 != platform }
            state = try await service.updateSettings(.init(ifRevision: state.settingsRevision, officialSources: remaining))
            XCTAssertEqual(state.officialSources, remaining)
            let reopened = try await NativeMusicFixture.service(store)
            let read = try await reopened.state()
            XCTAssertEqual(read.officialSources, remaining)
            await reopened.close()
        }
        XCTAssertTrue(state.officialSources.isEmpty)
        let before = store.snapshot().0
        await scriptExpectFailure(.staleRevision) {
            try await service.updateSettings(.init(ifRevision: 0, officialSources: [.qq]))
        }
        XCTAssertEqual(store.snapshot().0, before)
        let enabled = try await service.updateSettings(.init(ifRevision: state.settingsRevision, officialSources: [.qq]))
        XCTAssertEqual(enabled.officialSources, [.qq])
        await service.close()
    }

    func testMalformedStoredOfficialSourcesAreRejectedNotDefaultedOrSilentlyDisabled() async throws {
        for value in [MJ.null, .string("tx"), .strings(["wy", "wy"]), .strings(["kw"]), .array([.n(1)])] {
            var document = try MusicDocument(nil, runID: "test")
            document.playback["officialSources"] = value
            let store = NativeMusicMemoryDocument(try document.encode(changed: ["playback"]))
            let service = try await NativeMusicFixture.service(store)
            await scriptExpectFailure(.corruptDocument) { try await service.state() }
            await service.close()
        }
    }

    func testDisabledOfficialPlatformIsRejectedByOptionsAutomaticManualDirectAndRecoveryWhileScriptsStillWork() async throws {
        let store = try MusicNativeCandidateDiscoveryFixture.store()
        let accounts = NativeMusicAccountFixture(), scripts = NativeMusicScriptFixture()
        let service = try await NativeMusicFixture.service(store, scripts: scripts, accounts: accounts)
        let ref = MusicReference(kind: .queue, id: "entry-1")
        let before = try await service.playbackOptions(for: ref)
        let official = try XCTUnwrap(before.first { $0.installationId.hasPrefix("official:") })
        _ = try await service.updateSettings(.init(ifRevision: 0, officialSources: [.netease, .kugou]))
        let options = try await service.playbackOptions(for: ref)
        XCTAssertEqual(options.map(\.installationId), [NativeMusicFixture.installation])
        let count = await accounts.events().count
        let session = try await service.beginCandidateDiscovery(occurrenceID: "entry-1", expectedQueueRevision: 1)
        let snapshot = try await session.snapshot(discover: false, force: false)
        XCTAssertEqual(snapshot.options.map(\.playback.installationId), [NativeMusicFixture.installation])
        let candidate = try await session.resolve(try XCTUnwrap(snapshot.options.first).target(quality: "128k"),
                                                 attemptID: UUID().uuidString.lowercased())
        XCTAssertEqual(candidate.url.host, "media.invalid")
        await session.close(retainingAttemptID: candidate.attemptId)
        let inspection = try await service.inspectSources(for: ref)
        _ = try await inspection.loadMore(refresh: false)
        let inspected = try await MusicCandidateDiscoveryDeadline.run(seconds: 3, timing: .init()) {
            for try await value in await inspection.updates() where !value.isLoading { return value }
            throw MusicServiceError.closed
        }
        XCTAssertFalse(inspected.sources.isEmpty)
        XCTAssertTrue(inspected.sources.allSatisfy { $0.selection.installationId == NativeMusicFixture.installation })
        _ = try await inspection.adopt(sourceID: XCTUnwrap(inspected.sources.first).id,
                                       occurrenceID: "entry-1", expectedQueueRevision: 1)
        await scriptExpectFailure(.sourceUnavailable) {
            try await service.resolve(attemptId: UUID().uuidString.lowercased(), occurrenceId: "entry-1",
                                      selection: official.selection(quality: "128k"))
        }
        let after = await accounts.events().count
        XCTAssertEqual(after, count, "Disabled official source must not reach its account audio provider")
        await inspection.close(); await service.close()
        for platform in MusicAccountPlatform.allCases {
            let fixture = try await MusicRecoveryTestFixture.make(platform: platform)
            _ = try await fixture.service.updateSettings(.init(ifRevision: 0, officialSources: []))
            // Recovery pins the complete settings revision before the provider call.
            await scriptExpectFailure(.staleRevision) { try await fixture.recover() }
            let calls = await fixture.accounts.counts()
            XCTAssertEqual(calls.refreshes, 0)
            await fixture.service.close()
        }
    }

    func testCompleteOfficialCacheCannotBeReusedAfterSwitchOffAndOtherCapabilitiesRemainAvailable() async throws {
        for platform in MusicAccountPlatform.allCases {
            let fixture = try await MusicRecoveryTestFixture.make(platform: platform, cached: true)
            XCTAssertTrue(fixture.original.url.isFileURL)
            let platforms = try await fixture.service.platforms()
            let metadata = try await fixture.service.metadataCandidates(occurrenceID: "entry-1")
            _ = try await fixture.service.updateSettings(.init(ifRevision: 0, officialSources: []))
            let cached = try await fixture.service.cachedPlayback(attemptID: UUID().uuidString.lowercased(),
                                                                  occurrenceID: "entry-1", selection: nil)
            XCTAssertNil(cached)
            let nextPlatforms = try await fixture.service.platforms()
            let nextMetadata = try await fixture.service.metadataCandidates(occurrenceID: "entry-1")
            XCTAssertEqual(nextPlatforms.map(\.id), platforms.map(\.id))
            XCTAssertEqual(nextMetadata.candidates, metadata.candidates, "Audio admission does not change lyric/artwork candidates")
            XCTAssertEqual(nextMetadata.track, metadata.track)
            XCTAssertEqual(nextMetadata.preferences.platforms, metadata.preferences.platforms)
            XCTAssertEqual(nextMetadata.preferences.enabledPlatforms, metadata.preferences.enabledPlatforms)
            XCTAssertEqual(nextMetadata.preferences.revision, metadata.preferences.revision + 1, "The shared settings CAS advances")
            await fixture.service.close()
        }
        let service = try await NativeMusicFixture.service(accounts: NativeMusicAccountFixture())
        _ = try await service.updateSettings(.init(ifRevision: 0, officialSources: []))
        let search = try await service.search(platform: "tx", query: "Synthetic", page: 1, limit: 30)
        XCTAssertEqual(search.items.count, 1)
        let queued = try await service.changeQueue(.replace(search.items.map(\.ref), index: 0), expectedRevision: 0)
        let lyrics = try await service.lyrics(occurrenceId: XCTUnwrap(queued.currentId),
                                             sourceKey: search.items[0].sources[0].sourceKey)
        XCTAssertEqual(lyrics.state, .ready)
        let accountCollections = try await service.accountCollections(platform: .qq, cursor: nil)
        XCTAssertEqual(accountCollections.items.count, 1)
        await service.close()
    }

    func testSwitchOffWhileOfficialResolveIsInFlightRejectsItsLateResult() async throws {
        let fixture = try await MusicRecoveryTestFixture.make(), gate = NativeMusicGate()
        await fixture.accounts.holdResolve(gate)
        let task = Task {
            try await fixture.service.resolve(attemptId: UUID().uuidString.lowercased(),
                occurrenceId: "entry-1", selection: fixture.selection)
        }
        await gate.started()
        _ = try await fixture.service.updateSettings(.init(ifRevision: 0, officialSources: []))
        await gate.release()
        await scriptExpectFailure(.sourceUnavailable) { try await task.value }
        await fixture.service.close()
    }

    func testAutomaticDiscoverySkipsDisabledOfficialPlatformsButKeepsEnabledScriptSites() async throws {
        let seed = try MusicModel.normalize(MusicNativeCandidateDiscoveryFixture.track("kg", id: String(repeating: "A", count: 32)))
        for withScript in [false, true] {
            let store = try MusicNativeCandidateDiscoveryFixture.store(song: seed)
            let scripts = MusicNativeCandidateDiscoveryScripts()
            await scripts.supported(["tx"])
            let qq = MusicNativeCandidateDiscoveryQQ(), http = MusicNativeCandidateDiscoveryHTTP()
            let service = try await NativeMusicFixture.service(store, scripts: withScript ? scripts : nil,
                accounts: NativeMusicAccountFixture(), qq: qq, http: http)
            _ = try await service.updateSettings(.init(ifRevision: 0, officialSources: []))
            let session = try await service.beginCandidateDiscovery(occurrenceID: "entry-1", expectedQueueRevision: 1)
            let snapshot = try await session.snapshot(discover: true, force: false)
            let searches = await qq.calls(), otherSearches = await http.calls()
            XCTAssertEqual(searches.count, withScript ? 1 : 0)
            XCTAssertTrue(otherSearches.isEmpty)
            if withScript {
                XCTAssertFalse(snapshot.options.isEmpty)
                XCTAssertTrue(snapshot.options.allSatisfy {
                    $0.playback.siteId == "tx" && $0.playback.installationId == MusicNativeCandidateDiscoveryFixture.installation
                })
            } else { XCTAssertTrue(snapshot.options.isEmpty) }
            await session.close(retainingAttemptID: nil); await service.close()
        }
    }

    func testExcludedRecordingNeverReappearsThroughScriptOptionsOrManualInspection() async throws {
        let primary = try MusicModel.normalize(MusicNativeCandidateDiscoveryFixture.track("kw", id: "123"))
        let peer = try MusicModel.normalize(MusicNativeCandidateDiscoveryFixture.track("tx", id: "101"))
        let row = try XCTUnwrap(peer["sources"].array.first), key = row["sourceKey"].string
        let song = try MusicModel.mergeSources(primary, adding: [row])
        let store = try MusicNativeCandidateDiscoveryFixture.store(song: song)
        let cache = MusicNativeMemorySourceDiscoveryCache(clock: { NativeMusicFixture.date })
        let at = NativeMusicFixture.date.timeIntervalSince1970 * 1000
        for platform in MusicNativeSearchProtocol.platforms {
            await cache.record(fingerprint: MusicModel.fingerprint(song), duration: 180, platform: platform,
                               checkedAt: at, sources: [])
        }
        let scripts = MusicNativeCandidateDiscoveryScripts()
        await scripts.supported(["kw", "tx"])
        let service = try await NativeMusicFixture.service(store, scripts: scripts, sourceDiscovery: cache)
        let before = try await service.playbackOptions(for: .init(kind: .queue, id: "entry-1"))
        XCTAssertTrue(before.contains { $0.sourceKey == key })
        await cache.exclude(fingerprint: MusicModel.fingerprint(song), duration: 180, sourceKey: key)
        let after = try await service.playbackOptions(for: .init(kind: .queue, id: "entry-1"))
        XCTAssertFalse(after.contains { $0.sourceKey == key })
        let session = try await service.beginCandidateDiscovery(occurrenceID: "entry-1", expectedQueueRevision: 1)
        let options = try await session.snapshot(discover: false, force: false)
        XCTAssertFalse(options.options.contains { $0.playback.sourceKey == key })
        await session.close(retainingAttemptID: nil)
        let inspection = try await service.inspectSources(for: .init(kind: .queue, id: "entry-1"))
        _ = try await inspection.loadMore(refresh: false)
        let inspected = try await MusicCandidateDiscoveryDeadline.run(seconds: 3, timing: .init()) {
            for try await value in await inspection.updates() where !value.isLoading { return value }
            throw MusicServiceError.closed
        }
        XCTAssertFalse(inspected.sources.isEmpty)
        XCTAssertTrue(inspected.sources.allSatisfy { $0.selection.siteId == "kw" })
        let requests = await scripts.calls()
        XCTAssertTrue(requests.allSatisfy { $0.siteID == "kw" })
        await inspection.close(); await service.close()
    }
}
