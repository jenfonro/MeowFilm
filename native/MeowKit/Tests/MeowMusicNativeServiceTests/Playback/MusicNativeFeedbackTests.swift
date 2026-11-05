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
        XCTAssertEqual(reports.last?.listened, 4)
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
        await service.cancelPlaybackFeedback(attemptID: candidate.attemptId)
        try await service.cancel(attemptId: candidate.attemptId)
        await controller.flush()
        let reports = await accounts.recorded()
        XCTAssertEqual(reports.map(\.event), [.start, .end]); XCTAssertEqual(reports.last?.listened, 3)
        await service.close()
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
}
