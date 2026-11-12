import XCTest
import MeowMusicFeature
@testable import MeowMusicNativeService

final class MusicNativeFeedbackTests: XCTestCase {
    func testOptionalSettingsDefaultOffPreserveLibraryUnknownPlaybackFieldsAndColdReadTrue() async throws {
        let store = NativeMusicMemoryDocument(), service = try await NativeMusicFixture.service(store)
        let page = try await service.search(platform: "tx", query: "Synthetic", page: 1, limit: 30)
        let collection = try await service.createPlaylist(name: "Retained", refs: page.items.map(\.ref))
        let old = try await service.playbackFeedbackSettings()
        XCTAssertEqual(old.platforms.count, 3)
        XCTAssertTrue(old.platforms.values.allSatisfy { !$0.enabled && $0.generation == 0 })
        let originalBytes = try XCTUnwrap(store.snapshot().0), original = try MJ.parse(originalBytes)
        var draft = try MusicDocument(originalBytes, runID: "synthetic")
        draft.playback["syntheticFutureField"] = .obj([("retained", .string("original"))])
        try store.compareAndSwap(expected: originalBytes, replacement: draft.encode(changed: ["playback"]))
        let enabled = try await service.setPlaybackFeedback(platform: .kugou, enabled: true, expectedRevision: old.revision)
        XCTAssertEqual(enabled.platforms[.kugou], MusicPlaybackFeedbackSetting(enabled: true, generation: 1))
        let saved = try MJ.parse(XCTUnwrap(store.snapshot().0)), records = saved["records"]
        XCTAssertEqual(saved["version"], original["version"])
        XCTAssertEqual(records[MusicModel.key("library")], original["records"][MusicModel.key("library")])
        XCTAssertEqual(records[MusicModel.key("playback")]["syntheticFutureField"], draft.playback["syntheticFutureField"])
        XCTAssertEqual(records[MusicModel.key("playback")]["mode"], draft.playback["mode"])
        await service.close()
        let cold = try await NativeMusicFixture.service(store)
        let coldSettings = try await cold.playbackFeedbackSettings(), tracks = try await cold.tracks(in: collection)
        XCTAssertEqual(coldSettings, enabled); XCTAssertEqual(tracks.count, 1)
        await cold.close()
    }

    func testSettingsCASAndProtectionFailWithoutResetOrReplay() async throws {
        let store = NativeMusicMemoryDocument(), service = try await NativeMusicFixture.service(store)
        let first = try await service.setPlaybackFeedback(platform: .qq, enabled: true, expectedRevision: 0)
        let before = store.snapshot()
        do { _ = try await service.setPlaybackFeedback(platform: .qq, enabled: false, expectedRevision: 0); XCTFail("stale CAS") }
        catch { XCTAssertEqual(error as? MusicServiceError, .staleRevision) }
        XCTAssertEqual(store.snapshot().0, before.0); XCTAssertEqual(store.snapshot().2, before.2)
        store.fail(.storageConflict)
        do { _ = try await service.setPlaybackFeedback(platform: .qq, enabled: false, expectedRevision: first.revision); XCTFail("storage CAS") }
        catch { XCTAssertEqual(error as? MusicServiceError, .storageConflict) }
        store.fail(nil)
        let current = try await service.playbackFeedbackSettings()
        XCTAssertEqual(current, first); XCTAssertEqual(store.snapshot().2, before.2)
        service.revokeAccess(generation: 2)
        let reads = store.snapshot().1
        do { _ = try await service.playbackFeedbackSettings(); XCTFail("locked") }
        catch { XCTAssertEqual(error as? MusicServiceError, .accessUnavailable) }
        XCTAssertEqual(store.snapshot().1, reads); XCTAssertEqual(store.snapshot().0, before.0)
        await service.close()
    }

    func testMalformedFeedbackFailsClosedWithoutMakingOtherLibraryDataEmpty() async throws {
        let store = NativeMusicMemoryDocument(), service = try await NativeMusicFixture.service(store)
        let page = try await service.search(platform: "tx", query: "Synthetic", page: 1, limit: 30)
        let collection = try await service.createPlaylist(name: "Retained", refs: page.items.map(\.ref))
        let before = store.snapshot().0
        var draft = try MusicDocument(before, runID: "synthetic")
        draft.playback["feedback"] = .obj([("schema", .n(99)), ("platforms", .obj())])
        try store.compareAndSwap(expected: before, replacement: draft.encode(changed: ["playback"]))
        let malformed = store.snapshot()
        do { _ = try await service.playbackFeedbackSettings(); XCTFail("unknown schema") }
        catch { XCTAssertEqual(error as? MusicServiceError, .corruptDocument) }
        let tracks = try await service.tracks(in: collection)
        XCTAssertEqual(tracks.count, 1); XCTAssertEqual(store.snapshot().0, malformed.0)
        XCTAssertEqual(store.snapshot().2, malformed.2)
        await service.close()
    }

    private func ready(_ service: NativeMusicService) async throws -> MusicPlaybackCandidate {
        let page = try await service.search(platform: "tx", query: "SyntheticMID", page: 1, limit: 30)
        let state = try await service.changeQueue(.replace(page.items.map(\.ref), index: 0), expectedRevision: 0)
        let options = (try await service.sourceAvailability(for: page.items[0].ref)).options
        let option = try XCTUnwrap(options.first { $0.installationId == NativeMusicFixture.installation })
        return try await service.resolve(attemptId: UUID().uuidString.lowercased(),
            occurrenceId: XCTUnwrap(state.currentId), selection: option.selection(quality: "128k"))
    }

    func testResolvedURLAndDurableHistoryDoNotSendOnlyMatchingObservedAttemptDoes() async throws {
        let accounts = NativeFeedbackAccounts(), scripts = NativeMusicScriptFixture()
        let service = try await NativeMusicFixture.service(scripts: scripts, accounts: accounts)
        _ = try await service.setPlaybackFeedback(platform: .qq, enabled: true, expectedRevision: 0)
        let candidate = try await ready(service), controller = await service.playbackFeedback
        _ = try await service.record(.init(attemptId: candidate.attemptId, occurrenceId: candidate.occurrenceId,
            resourceId: candidate.resourceId, kind: .playing, position: 0))
        await controller.flush()
        var reports = await accounts.recorded(); XCTAssertTrue(reports.isEmpty)
        await service.consumePlaybackFeedback(NativeFeedbackFixture.sample(1, at: 0, position: 0, kind: .playing,
            attempt: candidate.attemptId, resource: "foreign"))
        await service.consumePlaybackFeedback(NativeFeedbackFixture.sample(1, at: 0, position: 0, kind: .playing,
            attempt: candidate.attemptId, occurrence: "entry-999"))
        await service.consumePlaybackFeedback(NativeFeedbackFixture.sample(1, at: 0, position: 0, kind: .playing))
        await controller.flush()
        reports = await accounts.recorded(); XCTAssertTrue(reports.isEmpty)
        await service.consumePlaybackFeedback(NativeFeedbackFixture.sample(1, at: 0, position: 0, kind: .playing,
            attempt: candidate.attemptId))
        await controller.flush()
        await service.consumePlaybackFeedback(NativeFeedbackFixture.sample(2, at: 1, position: 1, attempt: candidate.attemptId))
        await service.consumePlaybackFeedback(NativeFeedbackFixture.sample(3, at: 4, position: 4, attempt: candidate.attemptId))
        // The persisted terminal flag can win the actor race; its native
        // terminal observation must still reach the feedback controller.
        _ = try await service.record(.init(attemptId: candidate.attemptId, occurrenceId: candidate.occurrenceId,
            resourceId: candidate.resourceId, kind: .stopped, position: 5))
        await service.consumePlaybackFeedback(NativeFeedbackFixture.sample(4, at: 5, position: 5, kind: .stopped,
            running: false, attempt: candidate.attemptId))
        await controller.flush()
        reports = await accounts.recorded()
        XCTAssertEqual(reports.map(\.event), [.start, .end])
        XCTAssertEqual(reports.last?.listened, 5)
        let history = try await service.playedSongs(), state = try await service.state()
        XCTAssertEqual(history.count, 1); XCTAssertEqual(state.position, 5)
        await service.close()
    }

    func testSynchronousLockRevokesPendingFeedbackBeforeActorACKAndDoesNotBlockHistory() async throws {
        let accounts = NativeFeedbackAccounts(), gate = NativeMusicGate()
        let service = try await NativeMusicFixture.service(scripts: NativeMusicScriptFixture(), accounts: accounts)
        _ = try await service.setPlaybackFeedback(platform: .qq, enabled: true, expectedRevision: 0)
        let candidate = try await ready(service), controller = await service.playbackFeedback
        await accounts.configure(result: .confirmed, gate: gate)
        await service.consumePlaybackFeedback(NativeFeedbackFixture.sample(1, at: 0, position: 0, kind: .playing,
            attempt: candidate.attemptId))
        await gate.started()
        // Record completes while the feedback peer is deliberately stuck.
        let accepted = try await service.record(.init(attemptId: candidate.attemptId, occurrenceId: candidate.occurrenceId,
            resourceId: candidate.resourceId, kind: .playing, position: 1))
        XCTAssertTrue(accepted)
        service.revokeAccess(generation: 2)
        let active = await accounts.activeCount(); XCTAssertEqual(active, 0)
        await gate.release(); await controller.flush()
        await service.consumePlaybackFeedback(NativeFeedbackFixture.sample(2, at: 1, position: 1, kind: .stopped,
            running: false, attempt: candidate.attemptId))
        await controller.flush()
        let reports = await accounts.recorded(); XCTAssertEqual(reports.map(\.event), [.start])
        try await service.setAccess(available: false, generation: 2)
        try await service.setAccess(available: true, generation: 3)
        let history = try await service.playedSongs(); XCTAssertEqual(history.count, 1)
        await service.close()
    }

    func testSelectionCancellationSettlesOldObservedInstanceOnlyOnce() async throws {
        let accounts = NativeFeedbackAccounts()
        let service = try await NativeMusicFixture.service(scripts: NativeMusicScriptFixture(), accounts: accounts)
        _ = try await service.setPlaybackFeedback(platform: .qq, enabled: true, expectedRevision: 0)
        let candidate = try await ready(service), controller = await service.playbackFeedback
        await service.consumePlaybackFeedback(NativeFeedbackFixture.sample(1, at: 0, position: 0, kind: .playing,
            attempt: candidate.attemptId))
        await controller.flush()
        await service.consumePlaybackFeedback(NativeFeedbackFixture.sample(2, at: 1, position: 1, attempt: candidate.attemptId))
        await service.consumePlaybackFeedback(NativeFeedbackFixture.sample(3, at: 4, position: 4, attempt: candidate.attemptId))
        await service.finishPlaybackFeedback(attemptID: candidate.attemptId, reason: .interrupt)
        try await service.cancel(attemptId: candidate.attemptId)
        await controller.flush()
        let reports = await accounts.recorded()
        XCTAssertEqual(reports.map(\.event), [.start, .end]); XCTAssertEqual(reports.last?.listened, 4)
        await service.close()
    }

    func testPrepareForCloseFreezesInterruptBeforeRevocationWithoutWaitingForStartReceipt() async throws {
        let accounts = NativeFeedbackAccounts(), gate = NativeMusicGate()
        let service = try await NativeMusicFixture.service(scripts: NativeMusicScriptFixture(), accounts: accounts)
        _ = try await service.setPlaybackFeedback(platform: .qq, enabled: true, expectedRevision: 0)
        let candidate = try await ready(service), controller = await service.playbackFeedback
        await accounts.configure(result: .confirmed, gate: gate)
        await service.consumePlaybackFeedback(NativeFeedbackFixture.sample(1, at: 0, position: 0, kind: .playing,
            attempt: candidate.attemptId))
        await gate.started()
        await service.consumePlaybackFeedback(NativeFeedbackFixture.sample(2, at: 4, position: 4,
            attempt: candidate.attemptId))
        await service.prepareForClose()
        await service.prepareForClose()
        _ = try await service.state()
        let before = await accounts.recorded()
        XCTAssertEqual(before.map(\.event), [.start], "The local prepare cannot wait for this held receipt")
        await gate.release(); await controller.flush()
        let rows = await accounts.recorded()
        XCTAssertEqual(rows.map(\.event), [.start, .end])
        XCTAssertEqual(rows.first?.instanceID, rows.last?.instanceID)
        XCTAssertEqual(rows.last?.listened, 4); XCTAssertEqual(rows.last?.position, 4)
        XCTAssertEqual(rows.last?.occurredAt, 1004); XCTAssertEqual(rows.last?.reason, .interrupt)
        await service.close(); await service.close()
        XCTAssertEqual(accounts.releases.count(), 1)
    }

    func testCloseDoesNotWaitForNetworkAndRevocationStillRejectsLateReceipt() async throws {
        let accounts = NativeFeedbackAccounts(), gate = NativeMusicGate()
        let service = try await NativeMusicFixture.service(scripts: NativeMusicScriptFixture(), accounts: accounts)
        _ = try await service.setPlaybackFeedback(platform: .qq, enabled: true, expectedRevision: 0)
        let candidate = try await ready(service), controller = await service.playbackFeedback
        await accounts.configure(result: .confirmed, gate: gate)
        await service.consumePlaybackFeedback(NativeFeedbackFixture.sample(1, at: 0, position: 0, kind: .playing,
            attempt: candidate.attemptId))
        await gate.started()
        let closed = expectation(description: "Service close does not drain a remote receipt")
        let closing = Task { await service.close(); closed.fulfill() }
        await fulfillment(of: [closed], timeout: 3)
        await gate.release(); await closing.value; await controller.flush()
        let rows = await accounts.recorded(), active = await accounts.activeCount()
        XCTAssertEqual(rows.map(\.event), [.start]); XCTAssertEqual(active, 0)
        do { _ = try await service.state(); XCTFail("The product owner is closed") }
        catch { XCTAssertEqual(error as? MusicServiceError, .closed) }
        XCTAssertEqual(accounts.releases.count(), 1)
    }

    func testMappingRespectsAuthoritativeEmptySourcesAndExactKGIdentity() throws {
        var song = try NativeMusicFixture.song()
        XCTAssertEqual(try MusicNativeFeedbackMapping.targets(song: song, context: .obj()), NativeFeedbackFixture.targets)
        let automatic = try MusicNativeFeedbackMapping.targets(song: song, context: .obj([("automatic", .bool(true))]))
        XCTAssertEqual(automatic.first?.automatic, true)
        song["sources"] = .array([])
        XCTAssertTrue(try MusicNativeFeedbackMapping.targets(song: song, context: .obj()).isEmpty)
        let hash = String(repeating: "A", count: 32)
        let info = MJ.obj([("source", .string("kg")), ("songmid", .string(hash)),
            ("hash", .string(hash)), ("albumAudioId", .string("22"))])
        var kg = MJ.obj([("sourceKey", .string(try MusicModel.sourceKey(info))),
            ("source", .string("kg")), ("musicInfo", info)])
        XCTAssertEqual(MusicNativeFeedbackMapping.recording(kg, platform: .kugou)?.mixsongID, "22")
        kg["musicInfo"]["mixsongid"] = .string("23")
        XCTAssertNil(MusicNativeFeedbackMapping.recording(kg, platform: .kugou))
        var changed = kg["musicInfo"]
        changed.remove("mixsongid")
        kg["musicInfo"] = changed
        kg["musicInfo"]["hash"] = .string(String(repeating: "B", count: 32))
        kg["sourceKey"] = .string(try MusicModel.sourceKey(kg["musicInfo"]))
        XCTAssertNil(MusicNativeFeedbackMapping.recording(kg, platform: .kugou))
        kg["musicInfo"]["hash"] = .string(hash)
        kg["sourceKey"] = .string(try MusicModel.sourceKey(kg["musicInfo"]))
        kg["musicInfo"]["albumAudioId"] = .string("0")
        kg["musicInfo"]["mixsongid"] = .string("22")
        XCTAssertEqual(MusicNativeFeedbackMapping.recording(kg, platform: .kugou)?.mixsongID, "22")
    }

    func testMergedSearchKeepsKeywordButExternalCollectionsNeverBecomeNeteaseIDs() throws {
        var song = try MusicModel.normalize(SimilarModeAccountFixture.track("101"))
        let qq = try MusicModel.normalize(MusicNativeCandidateDiscoveryFixture.track(
            "tx", id: "SyntheticMID", title: "Song 101", artist: "Synthetic"))
        song["sources"] = .array(song["sources"].array + qq["sources"].array)
        for kind in ["playlist", "album"] {
            let context = MJ.obj([("sourceType", .string(kind)), ("platform", .string("tx")), ("sourceId", .string("501"))])
            let target = try XCTUnwrap(MusicNativeFeedbackMapping.targets(song: song, context: context).first { $0.platform == .netease })
            XCTAssertEqual(target.source, ""); XCTAssertEqual(target.sourceID, "")
            XCTAssertEqual(target.origin?.platform, .qq); XCTAssertEqual(target.origin?.sourceID, "501")
        }
        let native = try MusicNativeFeedbackMapping.targets(song: song,
            context: .obj([("sourceType", .string("playlist")), ("platform", .string("wy")), ("sourceId", .string("501"))]))
        XCTAssertEqual(native.first { $0.platform == .netease }?.source, "list")
        XCTAssertEqual(native.first { $0.platform == .netease }?.sourceID, "501")
        let search = try MusicNativeFeedbackMapping.targets(song: song,
            context: .obj([("sourceType", .string("search")), ("platform", .string("tx")),
                          ("searchKeyword", .string("真实搜索词"))]))
        XCTAssertEqual(Set(search.map(\.platform)), [.netease, .qq])
        XCTAssertEqual(search.first { $0.platform == .netease }?.sourceID, "真实搜索词")
        XCTAssertTrue(search.allSatisfy { $0.origin?.searchKeyword == "真实搜索词" })
    }

    func testCurrentPlaybackSettingCASAdmitsNewPlatformAndRevokesOnlySelectedPlatform() async throws {
        let accounts = NativeFeedbackAccounts(), clock = NativeFeedbackClock()
        await accounts.configure(platform: .netease, result: .confirmed)
        var song = try NativeMusicFixture.song()
        let native = try MusicModel.normalize(MusicNativeCandidateDiscoveryFixture.track(
            "wy", id: "101", title: song["title"].string, artist: song["artist"].string))
        song["sources"] = .array(song["sources"].array + native["sources"].array)
        let service = try await NativeMusicFixture.service(MusicNativeCandidateDiscoveryFixture.store(song: song),
            scripts: NativeMusicScriptFixture(), accounts: accounts, monotonicClock: { clock.now() })
        _ = try await service.setPlaybackFeedback(platform: .qq, enabled: true, expectedRevision: 0)
        let options = try await service.sourceAvailability(for: .init(kind: .queue, id: "entry-1"))
        let option = try XCTUnwrap(options.options.first { $0.installationId == NativeMusicFixture.installation })
        let candidate = try await service.resolve(attemptId: UUID().uuidString.lowercased(), occurrenceId: "entry-1",
            selection: option.selection(quality: "128k"))
        let controller = await service.playbackFeedback
        await service.consumePlaybackFeedback(NativeFeedbackFixture.sample(1, at: 0, position: 0, kind: .playing,
            attempt: candidate.attemptId))
        await controller.flush()
        clock.set(2)
        _ = try await service.setPlaybackFeedback(platform: .netease, enabled: true, expectedRevision: 1)
        await service.consumePlaybackFeedback(NativeFeedbackFixture.sample(2, at: 2, position: 2, attempt: candidate.attemptId))
        await controller.flush()
        clock.set(5)
        await service.consumePlaybackFeedback(NativeFeedbackFixture.sample(3, at: 5, position: 5, attempt: candidate.attemptId))
        _ = try await service.setPlaybackFeedback(platform: .qq, enabled: false, expectedRevision: 2)
        clock.set(6)
        await service.consumePlaybackFeedback(NativeFeedbackFixture.sample(4, at: 6, position: 6, kind: .stopped,
            running: false, attempt: candidate.attemptId))
        await controller.flush()
        let rows = await accounts.recorded()
        XCTAssertEqual(rows.filter { $0.target.platform == .qq }.map(\.event), [.start])
        let netease = rows.filter { $0.target.platform == .netease }
        XCTAssertEqual(netease.map(\.event), [.start, .end]); XCTAssertEqual(netease.last?.listened, 4)
        XCTAssertEqual(netease.first?.startedAt, 1000); XCTAssertEqual(netease.first?.occurredAt, 1002)
        XCTAssertEqual(accounts.releases.count(), 2)
        await service.close()
    }

    func testLegacyResolvePreparationFailureKeepsOldObservedListen() async throws {
        let accounts = NativeFeedbackAccounts(), scripts = NativeMusicScriptFixture(), gate = NativeMusicGate()
        let service = try await NativeMusicFixture.service(scripts: scripts, accounts: accounts)
        _ = try await service.setPlaybackFeedback(platform: .qq, enabled: true, expectedRevision: 0)
        let candidate = try await ready(service), controller = await service.playbackFeedback
        await service.consumePlaybackFeedback(NativeFeedbackFixture.sample(1, at: 0, position: 0, kind: .playing,
            attempt: candidate.attemptId))
        await controller.flush()
        await scripts.configure(gate: gate, error: .upstreamFailed)
        let selection = try XCTUnwrap(candidate.resolvedSelection)
        let preparing = Task {
            try await service.resolve(attemptId: UUID().uuidString.lowercased(), occurrenceId: candidate.occurrenceId,
                                      selection: selection)
        }
        await gate.started()
        await service.consumePlaybackFeedback(NativeFeedbackFixture.sample(2, at: 2, position: 2, attempt: candidate.attemptId))
        await gate.release()
        do { _ = try await preparing.value; XCTFail("synthetic preparation must fail") }
        catch { XCTAssertEqual(error as? MusicServiceError, .upstreamFailed) }
        let before = await accounts.recorded(); XCTAssertEqual(before.map(\.event), [.start])
        await service.consumePlaybackFeedback(NativeFeedbackFixture.sample(3, at: 5, position: 5, kind: .stopped,
            running: false, attempt: candidate.attemptId))
        await controller.flush()
        let rows = await accounts.recorded()
        XCTAssertEqual(rows.map(\.event), [.start, .end]); XCTAssertEqual(rows.last?.listened, 5)
        XCTAssertEqual(rows.first?.instanceID, rows.last?.instanceID)
        await service.close()
    }

    func testRecommendationKeepsCurrentListenAcrossLaterSuccessfulResourceHandoff() async throws {
        let accounts = NativeFeedbackAccounts()
        await accounts.configure(platform: .netease, result: .confirmed)
        let owner = SimilarModeAccountFixture([["301"]], metadata: true, feedback: accounts)
        let song = try MusicModel.normalize(SimilarModeAccountFixture.track("100"))
        let service = try await NativeMusicFixture.service(MusicNativeCandidateDiscoveryFixture.store(song: song),
                                                          accounts: owner)
        _ = try await service.setPlaybackFeedback(platform: .netease, enabled: true, expectedRevision: 0)
        let options = try await service.sourceAvailability(for: .init(kind: .queue, id: "entry-1"))
        let selection = try XCTUnwrap(options.options.first).selection(quality: "128k")
        let first = try await service.resolve(attemptId: UUID().uuidString.lowercased(), occurrenceId: "entry-1",
                                              selection: selection)
        let controller = await service.playbackFeedback
        await service.consumePlaybackFeedback(NativeFeedbackFixture.sample(1, at: 0, position: 0, kind: .playing,
            attempt: first.attemptId))
        await controller.flush()
        await service.consumePlaybackFeedback(NativeFeedbackFixture.sample(2, at: 1, position: 1, attempt: first.attemptId))
        let state = try await service.state()
        let mode = try await service.startSimilarMode(.recommendations(occurrenceID: "entry-1", expectedRevision: state.revision))
        XCTAssertEqual(mode.action, .keepCurrent); XCTAssertEqual(mode.state.currentId, "entry-1")
        let next = try await service.resolve(attemptId: UUID().uuidString.lowercased(), occurrenceId: "entry-1",
                                             selection: selection)
        let native = UUID()
        await service.consumePlaybackFeedback(NativeFeedbackFixture.sample(1, at: 1, position: 1, kind: .playing,
            attempt: next.attemptId, native: native))
        await controller.flush()
        await service.consumePlaybackFeedback(NativeFeedbackFixture.sample(2, at: 3, position: 3,
            attempt: next.attemptId, native: native))
        await service.consumePlaybackFeedback(NativeFeedbackFixture.sample(3, at: 4, position: 4, kind: .stopped,
            running: false, attempt: next.attemptId, native: native))
        await controller.flush()
        let rows = await accounts.recorded()
        XCTAssertEqual(rows.map(\.event), [.start, .end])
        XCTAssertEqual(rows.first?.instanceID, rows.last?.instanceID)
        XCTAssertEqual(rows.last?.listened, 4)
        await service.close()
    }

    func testLegacyRecordedTerminalDoesNotReportButNextActualReplayStartsNewInstance() async throws {
        let accounts = NativeFeedbackAccounts()
        let service = try await NativeMusicFixture.service(scripts: NativeMusicScriptFixture(), accounts: accounts)
        _ = try await service.setPlaybackFeedback(platform: .qq, enabled: true, expectedRevision: 0)
        let candidate = try await ready(service), controller = await service.playbackFeedback
        await service.consumePlaybackFeedback(NativeFeedbackFixture.sample(1, at: 0, position: 0, kind: .playing,
            attempt: candidate.attemptId))
        await controller.flush()
        _ = try await service.record(.init(attemptId: candidate.attemptId, occurrenceId: candidate.occurrenceId,
            resourceId: candidate.resourceId, kind: .ended, position: 2))
        let replay = try await service.resolve(attemptId: UUID().uuidString.lowercased(), occurrenceId: candidate.occurrenceId,
            selection: XCTUnwrap(candidate.resolvedSelection))
        await controller.flush()
        let prepared = await accounts.recorded(); XCTAssertEqual(prepared.map(\.event), [.start])
        await service.consumePlaybackFeedback(NativeFeedbackFixture.sample(1, at: 3, position: 0, kind: .playing,
            attempt: replay.attemptId, native: UUID()))
        await controller.flush()
        let starts = await accounts.recorded().filter { $0.event == .start }
        XCTAssertEqual(starts.count, 2)
        XCTAssertNotEqual(starts.first?.instanceID, starts.last?.instanceID)
        await service.close()
    }
}
