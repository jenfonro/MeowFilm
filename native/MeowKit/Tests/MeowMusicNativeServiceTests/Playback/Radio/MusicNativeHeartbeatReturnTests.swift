#if os(iOS) || os(macOS)
import Foundation
import XCTest
import MeowContracts
@testable import MeowMusicFeature
@testable import MeowMusicNativeService

final class MusicNativeHeartbeatReturnTests: XCTestCase {
    @MainActor func testHeartbeatReturnKeepsPlayingMediaAndRestoresABECInOneCAS() async throws {
        try await returnThroughModel(paused: false)
    }

    @MainActor func testHeartbeatReturnKeepsPausedMediaAndRestoresABECInOneCAS() async throws {
        try await returnThroughModel(paused: true)
    }

    @MainActor func testHeartbeatReturnCASFailureKeepsCheckpointProofAndMediaUntilRetry() async throws {
        try await returnThroughModel(paused: true, failFirst: true)
    }

    @MainActor func testActiveSongRecommendationRestoresABECWithoutRestartingPausedAudio() async throws {
        try await returnThroughModel(paused: true, recommendation: true)
    }

    @MainActor func testActiveSongRecommendationRestoresABECWithoutRestartingPlayingAudio() async throws {
        try await returnThroughModel(paused: false, recommendation: true)
    }

    @MainActor func testOrdinaryPlaylistRecommendationRetryThroughModelKeepsItsFailedEntranceIdentity() async throws {
        var original = try document()
        original.queue["source"]["sourceId"] = .string("900")
        original.queue["entries"] = .array(original.queue["entries"].array.map { row in
            var row = row; row["context"] = original.queue["source"]; return row
        })
        let store = NativeMusicMemoryDocument(try original.encode(changed: ["queue", "playback", "history"]))
        let accounts = SimilarModeAccountFixture([[], ["400", "500"]])
        let service = try await NativeMusicFixture.service(store, accounts: accounts)
        let player = HeartbeatReturnPlayer(), model = MusicViewModel(service: service, player: player)
        await model.load()
        await model.startSongRecommendations(occurrenceID: "entry-2", expectedRevision: model.state.revision)
        let snapshot = try XCTUnwrap(model.similarMode)
        XCTAssertTrue(snapshot.canRetry)
        XCTAssertEqual(model.state.total, 3)
        model.error = nil
        await model.retrySimilarMode(snapshotID: snapshot.id)
        XCTAssertNil(model.error)
        XCTAssertTrue(try XCTUnwrap(model.similarMode).active)
        XCTAssertEqual(model.state.currentId, "entry-2")
        let requests = await accounts.requests
        XCTAssertEqual(requests, [.songRecommendations(seedID: "200", offset: 0),
                                  .songRecommendations(seedID: "200", offset: 0)])
        await model.close(); await service.close()
    }

    @MainActor private func returnThroughModel(paused: Bool, failFirst: Bool = false, recommendation: Bool = false) async throws {
        var original = try document()
        let originalRows = original.queue["entries"].array
        let store = NativeMusicMemoryDocument(try original.encode(changed: ["queue", "playback", "history"]))
        let accounts = SimilarModeAccountFixture([["400", "500", "600"]], metadata: true)
        let service = try await NativeMusicFixture.service(store, accounts: accounts)
        let initial = try await service.state()
        _ = try await service.startSimilarMode(recommendation ? .recommendations(occurrenceID: "entry-2", expectedRevision: initial.revision) : .currentHeartbeat(occurrenceID: "entry-2", expectedRevision: initial.revision))
        let entered = try await service.queue()
        let current = try XCTUnwrap(entered.entries.first { $0.track.sources.first?.nativeId == "500" })
        _ = try await service.changeQueue(.select(current.id), expectedRevision: entered.revision)
        let player = HeartbeatReturnPlayer(), model = MusicViewModel(service: service, player: player)
        await model.load()
        let candidate = try await resolve(service, id: current.id)
        let selection = candidate.resolvedSelection
        // Exercise the real owner and model, with synthetic account/storage
        // boundaries and a recording audio driver.
        XCTAssertTrue(try model.acceptResolvedCandidate(candidate, selection: selection,
            attemptID: candidate.attemptId, occurrenceID: current.id, epoch: model.lifecycle,
            token: model.selectionID, admission: nil))
        XCTAssertTrue(model.seek(to: 43))
        model.onPlayback(fact(candidate, .playing, position: 43))
        await model.flushPlaybackFacts()
        if paused {
            await model.togglePlayback()
            model.onPlayback(fact(candidate, .paused, position: 43))
            await model.flushPlaybackFacts()
        }
        XCTAssertNil(model.error)
        XCTAssertEqual(model.phase, paused ? .paused : .playing)
        let currentState = try await service.state()
        _ = try await service.updateSettings(.init(ifRevision: currentState.settingsRevision, mode: .shuffle))
        let stateBefore = try await service.state()
        XCTAssertEqual(stateBefore.position, 43)
        let before = try MusicDocument(store.snapshot().0, runID: "evidence")
        let retained = try XCTUnwrap(before.queue["entries"].array.first { $0["id"].string == current.id })
        let targets = try await service.similarFeedbackTargets(entry: retained, context: retained["context"])
        let proof = try XCTUnwrap(targets.first?.recommendationProof)
        XCTAssertTrue(proof.authorization.isActive)
        let sessionID = try XCTUnwrap(model.similarMode).id
        let media = try XCTUnwrap(model.nativeAttempt), phase = model.phase
        let plays = player.plays, stops = player.stops, pauses = player.pauses, resumes = player.resumes, seeks = player.seeks
        let resolutions = await accounts.resolutions
        let writes = store.snapshot().2
        XCTAssertEqual(plays, 1); XCTAssertEqual(pauses, paused ? 1 : 0)
        XCTAssertEqual(seeks, [43])

        if failFirst {
            let bytes = store.snapshot().0
            store.fail(.storageConflict)
            await model.exitSimilarMode()
            XCTAssertNotNil(model.error)
            XCTAssertEqual(store.snapshot().0, bytes)
            XCTAssertEqual(store.snapshot().2, writes)
            XCTAssertEqual(model.similarMode?.id, sessionID)
            XCTAssertEqual(model.nativeAttempt, media)
            XCTAssertEqual(model.phase, phase)
            XCTAssertEqual(model.position, 43)
            XCTAssertEqual(player.current, media)
            XCTAssertEqual(player.plays, plays); XCTAssertEqual(player.stops, stops)
            XCTAssertEqual(player.pauses, pauses); XCTAssertEqual(player.resumes, resumes); XCTAssertEqual(player.seeks, seeks)
            let unchanged = try await service.state(), attempt = await service.attempt
            XCTAssertEqual(unchanged.currentId, current.id); XCTAssertEqual(unchanged.position, 43)
            XCTAssertEqual(attempt?.id, candidate.attemptId)
            XCTAssertTrue(proof.authorization.isActive)
            store.fail(nil)
            model.error = nil
        }

        await model.exitSimilarMode()

        XCTAssertNil(model.error)
        let restored = try MusicDocument(store.snapshot().0, runID: "evidence")
        let expectedRows = [originalRows[0], originalRows[1], retained, originalRows[2]]
        XCTAssertEqual(restored.queue["entries"].array, expectedRows, "Only E returns, with its complete occurrence row")
        XCTAssertEqual(restored.queue["currentId"].string, current.id)
        XCTAssertEqual(restored.queue["source"], original.queue["source"])
        XCTAssertEqual(restored.queue["priority"], original.queue["priority"])
        XCTAssertEqual(restored.queue["similar"], .null)
        XCTAssertEqual(restored.queue["revision"].int, before.queue["revision"].int + 1)
        XCTAssertEqual(store.snapshot().2, writes + 1)
        XCTAssertEqual(model.queue.map(\.id), expectedRows.map { $0["id"].string })
        XCTAssertEqual(model.current?.id, current.id)
        XCTAssertEqual(model.state.sourceCollectionID, "original-source")
        XCTAssertEqual(model.state.queueSourceTitle, "Original")
        XCTAssertEqual(model.playbackSourceTitle, "Original", "retained E does not relabel the restored queue")
        XCTAssertEqual(model.current?.sourceTitle, current.sourceTitle, "E keeps its own recommendation provenance")
        XCTAssertEqual(model.state.position, 43); XCTAssertEqual(model.position, 43)
        XCTAssertEqual(model.state.mode, .shuffle, "return never restores the entrance loop value")
        XCTAssertEqual(model.nativeAttempt, media); XCTAssertEqual(player.current, media)
        XCTAssertEqual(model.activeResourceID, candidate.resourceId)
        XCTAssertEqual(model.phase, phase); XCTAssertEqual(player.isPlaying, !paused)
        XCTAssertEqual(player.position, 43)
        XCTAssertEqual(player.plays, plays); XCTAssertEqual(player.stops, stops)
        XCTAssertEqual(player.pauses, pauses); XCTAssertEqual(player.resumes, resumes); XCTAssertEqual(player.seeks, seeks)
        let resolutionsAfter = await accounts.resolutions, attempt = await service.attempt
        XCTAssertEqual(resolutionsAfter, resolutions)
        XCTAssertEqual(attempt?.id, candidate.attemptId)
        XCTAssertEqual(attempt?.isPlaying, !paused)
        XCTAssertNil(model.similarMode)
        XCTAssertFalse(proof.authorization.isActive)
        let live = await service.liveRecommendation(entryID: current.id)
        XCTAssertNil(live)
        let ordinaryTargets = try await service.similarFeedbackTargets(entry: retained, context: retained["context"])
        XCTAssertTrue(ordinaryTargets.allSatisfy { $0.recommendationProof == nil })
        XCTAssertFalse(ordinaryTargets.contains { $0.source == "aiMode" })
        let accepted = try await service.recordSimilarPlaybackFact(fact(candidate, .progress, position: 44))
        XCTAssertTrue(accepted, "The original attempt still owns subsequent progress")
        await model.close(); await service.close()
    }

    func testHeartbeatReturnPreservesDifferentOccurrencesOfSameRecording() async throws {
        var original = try document(["100", "200", "500"])
        let originalRows = original.queue["entries"].array
        let store = NativeMusicMemoryDocument(try original.encode(changed: ["queue", "playback", "history"]))
        let service = try await NativeMusicFixture.service(store, accounts: SimilarModeAccountFixture([["400", "500", "600"]]))
        _ = try await service.enterSimilarMode()
        let queue = try await service.queue()
        let current = try XCTUnwrap(queue.entries.first { $0.track.sources.first?.nativeId == "500" })
        XCTAssertNotEqual(current.id, "entry-3")
        _ = try await service.changeQueue(.select(current.id), expectedRevision: queue.revision)
        let before = try MusicDocument(store.snapshot().0, runID: "evidence")
        let retained = try XCTUnwrap(before.queue["entries"].array.first { $0["id"].string == current.id })
        let writes = store.snapshot().2

        let result = try await service.exitSimilarMode()

        let restored = try MusicDocument(store.snapshot().0, runID: "evidence")
        XCTAssertEqual(restored.queue["entries"].array, [originalRows[0], originalRows[1], retained, originalRows[2]])
        XCTAssertEqual(restored.queue["entries"].array.filter {
            MusicNativeSimilarData.nativeID($0["song"]) == "500"
        }.map { $0["id"].string }, [current.id, "entry-3"])
        XCTAssertEqual(result.state.currentId, current.id); XCTAssertEqual(result.action, .keepCurrent)
        XCTAssertEqual(store.snapshot().2, writes + 1)
        await service.close()
    }

    func testColdHeartbeatReturnKeepsExistingOccurrenceAndRemovesItsSavedPriority() async throws {
        var saved = try document()
        let rows = saved.queue["entries"].array, returning = MusicNativeSimilarData.queueSnapshot(saved.queue)
        saved.queue["currentId"] = rows[2]["id"]; saved.queue["priority"] = .array([])
        saved.queue["similar"] = .obj([
            ("schema", .n(2)), ("seed", rows[1]["song"]), ("seedEntryId", rows[1]["id"]),
            ("returnQueue", returning), ("sourceIds", .strings(["200"])),
            ("fromHome", .bool(false)), ("provider", .string("heartbeat")), ("providerContext", .null),
            ("continue", .bool(false)), ("offset", .n(0)), ("exhausted", .bool(false)), ("scope", .string("synthetic-scope")),
            ("status", .string("ready")), ("batches", .array([])), ("attempted", .array([]))
        ])
        let store = NativeMusicMemoryDocument(try saved.encode(changed: ["queue", "playback", "history"]))
        let service = try await NativeMusicFixture.service(store, accounts: SimilarModeAccountFixture([]))
        let restoredSession = try await service.similarModeSnapshot()
        XCTAssertTrue(try XCTUnwrap(restoredSession).active)
        let candidate = try await resolve(service, id: "entry-3")
        _ = try await service.recordSimilarPlaybackFact(fact(candidate, .playing, position: 43))
        let writes = store.snapshot().2

        let result = try await service.exitSimilarMode()

        let restored = try MusicDocument(store.snapshot().0, runID: "evidence")
        XCTAssertEqual(restored.queue["entries"].array, rows)
        XCTAssertEqual(restored.queue["source"], returning["source"])
        XCTAssertEqual(restored.queue["priority"].strings, [])
        XCTAssertEqual(result.state.currentId, "entry-3"); XCTAssertEqual(result.state.position, 43)
        XCTAssertEqual(result.action, .keepCurrent)
        let attempt = await service.attempt?.id
        XCTAssertEqual(attempt, candidate.attemptId)
        XCTAssertEqual(store.snapshot().2, writes + 1)
        await service.close()
    }

    func testHeartbeatReturnToEmptyCheckpointRetainsOnlyCurrentOccurrence() async throws {
        let store = NativeMusicMemoryDocument()
        let service = try await NativeMusicFixture.service(store, accounts: SimilarModeAccountFixture([["400", "500", "600"]]))
        _ = try await service.startSimilarMode(.heartbeat)
        let queue = try await service.queue(), current = queue.entries[1]
        _ = try await service.changeQueue(.select(current.id), expectedRevision: queue.revision)
        let before = try MusicDocument(store.snapshot().0, runID: "evidence")
        let retained = try XCTUnwrap(before.queue["entries"].array.first { $0["id"].string == current.id })
        let writes = store.snapshot().2

        let result = try await service.exitSimilarMode()

        let restored = try MusicDocument(store.snapshot().0, runID: "evidence")
        XCTAssertEqual(restored.queue["entries"].array, [retained])
        XCTAssertEqual(restored.queue["source"], .null)
        XCTAssertEqual(result.state.currentId, current.id); XCTAssertEqual(result.action, .keepCurrent)
        XCTAssertEqual(store.snapshot().2, writes + 1)
        await service.close()
    }

    func testSongRoamingHasNoReturnCheckpointAndCannotRestoreAnOlderQueue() async throws {
        var original = try document()
        let store = NativeMusicMemoryDocument(try original.encode(changed: ["queue", "playback", "history"]))
        let service = try await NativeMusicFixture.service(store, accounts: SimilarModeAccountFixture([["400", "500", "600"]]))
        _ = try await service.startSimilarMode(.songRoaming(sourceIDs: ["200"], sourceName: "SIMILAR"))
        let queue = try await service.queue(), current = queue.entries[1]
        _ = try await service.changeQueue(.select(current.id), expectedRevision: queue.revision)
        let candidate = try await resolve(service, id: current.id)
        _ = try await service.recordSimilarPlaybackFact(fact(candidate, .playing, position: 43))
        let snapshot = try await service.similarModeSnapshot()
        XCTAssertFalse(try XCTUnwrap(snapshot).canRestoreQueue)
        let bytes = store.snapshot().0, writes = store.snapshot().2
        do { _ = try await service.exitSimilarMode(); XCTFail("similar sources have no return action") }
        catch { XCTAssertEqual(error as? MusicServiceError, .unsupported) }
        XCTAssertEqual(store.snapshot().0, bytes); XCTAssertEqual(store.snapshot().2, writes)
        let attempt = await service.attempt?.id; XCTAssertEqual(attempt, candidate.attemptId)
        await service.close()
    }

    func testActiveSongRecommendationReturnRejectsLateTailBatchAfterCheckpointRestore() async throws {
        var original = try document()
        let store = NativeMusicMemoryDocument(try original.encode(changed: ["queue", "playback", "history"]))
        let accounts = SimilarModeAccountFixture([["400", "500", "600"], ["700", "800"]])
        let service = try await NativeMusicFixture.service(store, accounts: accounts)
        _ = try await service.startSimilarMode(.recommendations(occurrenceID: "entry-2", expectedRevision: 0))
        let snapshot = try await service.similarModeSnapshot()
        try await service.setSimilarContinue(true, snapshotID: XCTUnwrap(snapshot).id)
        let queue = try await service.queue(), tail = try XCTUnwrap(queue.entries.last)
        _ = try await service.changeQueue(.select(tail.id), expectedRevision: queue.revision)
        let candidate = try await resolve(service, id: tail.id), gate = NativeMusicGate()
        await accounts.suspendNext(gate)
        _ = try await service.recordSimilarPlaybackFact(fact(candidate, .playing, position: 43))
        await gate.started()
        let pending = await service.similarMode.active?.pending
        let writes = store.snapshot().2

        let result = try await service.exitSimilarMode()
        let bytes = store.snapshot().0
        await gate.release(); await pending?.value

        XCTAssertEqual(result.action, .keepCurrent)
        let restored = try await service.queue()
        XCTAssertEqual(restored.entries.map(\.id), ["entry-1", "entry-2", tail.id, "entry-3"])
        XCTAssertEqual(restored.currentId, tail.id)
        XCTAssertEqual(store.snapshot().0, bytes); XCTAssertEqual(store.snapshot().2, writes + 1)
        let active = try await service.similarModeSnapshot(), attempt = await service.attempt?.id
        XCTAssertNil(active); XCTAssertEqual(attempt, candidate.attemptId)
        await service.close()
    }

    func testHeartbeatReturnCommittedCancellationKeepsABECButInvalidatesAttemptAndRuntime() async throws {
        var original = try document()
        let rows = original.queue["entries"].array
        let store = NativeMusicMemoryDocument(try original.encode(changed: ["queue", "playback", "history"]))
        let controlled = SimilarModeControlledDocument(store)
        let service = NativeMusicService(document: controlled,
            accounts: SimilarModeAccountFixture([["400", "500", "600"]], metadata: true),
            qq: NativeMusicQQFixture(), transport: NativeMusicNoNetwork(), clock: { NativeMusicFixture.date })
        try await service.setAccess(available: true, generation: 1)
        _ = try await service.enterSimilarMode()
        let queue = try await service.queue()
        let current = try XCTUnwrap(queue.entries.first { $0.track.sources.first?.nativeId == "500" })
        _ = try await service.changeQueue(.select(current.id), expectedRevision: queue.revision)
        let candidate = try await resolve(service, id: current.id)
        _ = try await service.recordSimilarPlaybackFact(fact(candidate, .playing, position: 43))
        let before = try MusicDocument(store.snapshot().0, runID: "evidence")
        let retained = try XCTUnwrap(before.queue["entries"].array.first { $0["id"].string == current.id })
        let targets = try await service.similarFeedbackTargets(entry: retained, context: retained["context"])
        let proof = try XCTUnwrap(targets.first?.recommendationProof)
        let writes = store.snapshot().2
        controlled.cancelAfterNextCommit()

        let returning = Task { try await service.exitSimilarMode() }
        do { _ = try await returning.value; XCTFail("Committed cancellation cannot report a successful return or rollback") }
        catch { XCTAssertEqual(error as? MusicServiceError, .commitUnconfirmed) }

        let restored = try MusicDocument(store.snapshot().0, runID: "evidence")
        XCTAssertEqual(restored.queue["entries"].array, [rows[0], rows[1], retained, rows[2]])
        XCTAssertEqual(restored.queue["currentId"].string, current.id)
        XCTAssertEqual(restored.queue["similar"], .null)
        let attempt = await service.attempt, active = try await service.similarModeSnapshot()
        XCTAssertNil(attempt); XCTAssertNil(active)
        XCTAssertFalse(proof.authorization.isActive)
        XCTAssertEqual(store.snapshot().2, writes + 1, "No recovery write may undo the committed return")
        await service.close()
    }

    private func document(_ ids: [String] = ["100", "200", "300"]) throws -> MusicDocument {
        var value = try MusicDocument(nil, runID: "fixture")
        value.queue["entries"] = .array(try ids.enumerated().map { index, id in
            let song = try MusicModel.normalize(SimilarModeAccountFixture.track(id))
            return .obj([("id", .string("entry-\(index + 1)")), ("key", song["songId"]), ("song", song),
                ("context", .obj([("sourceType", .string("search-song")), ("sourceTitle", .string("Original")),
                                 ("platform", .string("wy")), ("sourceId", .string("favorites")),
                                 ("searchKeyword", .string("needle-\(index)"))]))])
        })
        value.queue["currentId"] = .string("entry-2"); value.queue["serial"] = .n(ids.count)
        value.queue["priority"] = .strings(["entry-3"])
        value.queue["source"] = .obj([("id", .string("original-source")), ("title", .string("Original")),
                                      ("sourceType", .string("playlist")), ("sourceId", .string("favorites")), ("platform", .string("wy"))])
        return value
    }

    private func resolve(_ service: NativeMusicService, id: String) async throws -> MusicPlaybackCandidate {
        let availability = try await service.sourceAvailability(for: .init(kind: .queue, id: id))
        let selection = try XCTUnwrap(availability.options.first).selection(quality: "128k")
        return try await service.resolve(attemptId: UUID().uuidString.lowercased(), occurrenceId: id, selection: selection)
    }

    private func fact(_ candidate: MusicPlaybackCandidate, _ kind: MusicPlaybackFact.Kind,
                      position: Double) -> MusicPlaybackFact {
        .init(attemptId: candidate.attemptId, occurrenceId: candidate.occurrenceId,
              resourceId: candidate.resourceId, kind: kind, position: position, duration: 180)
    }
}

@MainActor private final class HeartbeatReturnPlayer: MusicPlaybackDriving {
    var plays = 0, stops = 0, pauses = 0, resumes = 0
    var seeks: [Double] = []
    var current: MusicPlayerAttempt?
    var position: Double = 0
    var isPlaying = false
    func play(_ resource: PlaybackResource) -> MusicPlayerAttempt {
        plays += 1; position = 0; isPlaying = true
        let attempt = MusicPlayerAttempt(id: UUID(), resourceID: resource.id)
        current = attempt; return attempt
    }
    func pause(_ attempt: MusicPlayerAttempt) { pauses += 1; isPlaying = false }
    func resume(_ attempt: MusicPlayerAttempt) { resumes += 1; isPlaying = true }
    func seek(_ position: Double, attempt: MusicPlayerAttempt) -> Bool {
        seeks.append(position); self.position = position; return true
    }
    func stop(_ attempt: MusicPlayerAttempt) { stops += 1; current = nil; isPlaying = false }
}
#endif
