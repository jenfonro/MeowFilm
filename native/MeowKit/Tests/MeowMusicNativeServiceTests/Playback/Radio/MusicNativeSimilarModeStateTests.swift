import XCTest
import MeowMusicFeature
@testable import MeowMusicNativeService

final class MusicNativeSimilarModeStateTests: XCTestCase {
    func testSimilarSeedAndProviderChangesReplaceQueueWithoutReturnOrGlobalModeChanges() async throws {
        let owner = SimilarModeAccountFixture([["301", "302"], ["401", "402"], ["501", "502"], ["601", "602"]])
        let service = try await NativeMusicFixture.service(NativeMusicMemoryDocument(try fixtureDocument()), accounts: owner)
        _ = try await service.updateSettings(.init(ifRevision: 0, mode: .single))
        let starts: [MusicSimilarModeStart] = [
            .artistRoaming(sourceIDs: ["11"], sourceName: "艺人一"),
            .artistRoaming(sourceIDs: ["12"], sourceName: "艺人二"),
            .songRoaming(sourceIDs: ["21"], sourceName: "歌曲一"),
            .songRoaming(sourceIDs: ["22"], sourceName: "歌曲二")
        ]
        for (index, start) in starts.enumerated() {
            let result = try await service.startSimilarMode(start)
            let snapshot = try await service.similarModeSnapshot(), queue = try await service.queue()
            XCTAssertEqual(result.state.mode, .single)
            XCTAssertTrue(try XCTUnwrap(snapshot).continueEnabled)
            XCTAssertFalse(try XCTUnwrap(snapshot).canRestoreQueue)
            XCTAssertEqual(queue.entries.compactMap { $0.track.sources.first?.nativeId },
                           index < 2 ? [String(301 + index * 100), String(302 + index * 100)] :
                               [String(501 + (index - 2) * 100), String(502 + (index - 2) * 100)])
            XCTAssertEqual(queue.currentId, queue.entries.first?.id)
            XCTAssertEqual(snapshot?.provider, index < 2 ? .artistRoaming : .songRoaming)
        }
        let requests = await owner.requests
        XCTAssertEqual(requests, [.artistRoaming(sourceIDs: ["11"]), .artistRoaming(sourceIDs: ["12"]),
                                  .songRoaming(sourceIDs: ["21"]), .songRoaming(sourceIDs: ["22"])])
        await service.close()
    }

    func testSharedNeteaseIdentityEnablesRecommendationWithoutSearchButOrdinaryPlaylistCannotHeartbeat() async throws {
        let foreign = try MusicModel.normalize(MusicNativeCandidateDiscoveryFixture.track(
            "tx", id: "ForeignMID", title: "Song 100", artist: "Synthetic"))
        var document = try MusicDocument(fixtureDocument(count: 1), runID: "fixture")
        var rows = document.queue["entries"].array
        document.queue["source"] = .obj([("sourceType", .string("playlist")), ("platform", .string("wy")), ("sourceId", .string("900"))])
        rows[0]["song"] = foreign; rows[0]["context"] = document.queue["source"]
        document.queue["entries"] = .array(rows)
        let owner = SimilarModeAccountFixture([["301"]])
        let qq = MusicNativeCandidateDiscoveryQQ(), http = MusicNativeCandidateDiscoveryHTTP()
        let service = try await NativeMusicFixture.service(
            NativeMusicMemoryDocument(try document.encode(changed: ["queue", "playback"])), accounts: owner, qq: qq, http: http)
        let missing = try await service.canEnterSongRecommendations()
        XCTAssertFalse(missing)
        try await service.retainSongMetadata([MusicModel.normalize(SimilarModeAccountFixture.track("100"))])
        let eligible = try await service.canEnterSongRecommendations(), heartbeat = try await service.canEnterSimilarMode()
        XCTAssertTrue(eligible); XCTAssertFalse(heartbeat)
        do { _ = try await service.enterSimilarMode(); XCTFail("ordinary playlist is not the official heartbeat context") }
        catch { XCTAssertEqual(error as? MusicServiceError, .sourceUnavailable) }
        let before = try await service.state()
        let result = try await service.startSimilarMode(.recommendations(occurrenceID: "entry-1", expectedRevision: before.revision))
        XCTAssertEqual(result.action, .keepCurrent)
        let queue = try await service.queue()
        XCTAssertEqual(queue.currentId, "entry-1")
        XCTAssertEqual(queue.entries.first?.track.sources.first?.platform, "tx")
        XCTAssertEqual(Set(queue.entries.first?.track.sources.map(\.platform) ?? []), ["tx", "wy"])
        let requests = await owner.requests, qqCalls = await qq.calls(), httpCalls = await http.calls()
        XCTAssertEqual(requests, [.songRecommendations(seedID: "100", offset: 0)])
        XCTAssertTrue(qqCalls.isEmpty); XCTAssertTrue(httpCalls.isEmpty)
        await service.close()
    }

    func testFavoritesQueueDoesNotGiveForeignCurrentOccurrenceHeartbeatEligibility() async throws {
        var document = try MusicDocument(fixtureDocument(), runID: "fixture")
        var rows = document.queue["entries"].array
        rows[1]["context"] = .obj([("sourceType", .string("song"))])
        document.queue["entries"] = .array(rows)
        let owner = SimilarModeAccountFixture([["301"]])
        let service = try await NativeMusicFixture.service(
            NativeMusicMemoryDocument(try document.encode(changed: ["queue", "playback"])), accounts: owner)
        let original = try await service.canEnterSimilarMode()
        XCTAssertTrue(original)
        _ = try await service.changeQueue(.select("entry-2"), expectedRevision: 0)
        let foreign = try await service.canEnterSimilarMode()
        XCTAssertFalse(foreign)
        let requests = await owner.count()
        XCTAssertEqual(requests, 0, "eligibility uses existing occurrence provenance, not a membership request")
        await service.close()
    }

    func testSimilarRestoreAndTailAppendKeepLargeQueueReturnQueueBatchesAndAttempts() async throws {
        var document = try MusicDocument(fixtureDocument(count: 2001), runID: "fixture")
        let original = document.queue["entries"].array
        let seed = original[0]["song"], returning = MusicNativeSimilarData.queueSnapshot(document.queue)
        document.queue["currentId"] = original.last?["id"] ?? .null
        document.queue["serial"] = .n(10_000)
        document.queue["similar"] = .obj([
            ("schema", .n(2)), ("seed", seed), ("seedEntryId", original[0]["id"]), ("returnQueue", returning),
            ("sourceIds", .strings(["100"])), ("fromHome", .bool(false)), ("provider", .string("queue-recommendations")),
            ("providerContext", .null), ("offset", .n(15)), ("exhausted", .bool(false)), ("continue", .bool(true)), ("scope", .string("synthetic-scope")),
            ("status", .string("ready")),
            ("batches", .array(original.enumerated().map {
                MusicNativeSimilarData.batch("batch-\($0.offset)", basedOn: seed, provider: .queueRecommendations, rows: [$0.element])
            })),
            ("attempted", .strings((3000...5000).map { "entry-\($0)" }))
        ])
        let store = NativeMusicMemoryDocument(try document.encode(changed: ["queue", "history", "playback"]))
        let owner = SimilarModeAccountFixture([["301", "302", "303", "304"]])
        let service = try await NativeMusicFixture.service(store, accounts: owner)
        let initialValue = try await service.similarModeSnapshot(), initial = try XCTUnwrap(initialValue)
        XCTAssertTrue(initial.active); XCTAssertEqual(initial.batches.count, 2001)
        let attemptedBefore = await service.similarMode.active?.attempted.count
        XCTAssertEqual(attemptedBefore, 2001)
        let candidate = try await resolve(service, id: "entry-2001")
        _ = try await service.recordSimilarPlaybackFact(fact(candidate, .playing))
        let pending = await service.similarMode.active?.pending
        await pending?.value
        let queue = try await service.queue(), saved = try MusicDocument(store.snapshot().0, runID: "saved")
        XCTAssertEqual(queue.entries.count, 2005)
        XCTAssertEqual(queue.entries.prefix(2001).map(\.id), original.map { $0["id"].string })
        XCTAssertEqual(queue.entries.suffix(4).compactMap { $0.track.sources.first?.nativeId }, ["301", "302", "303", "304"])
        XCTAssertEqual(saved.queue["similar"]["batches"].array.count, 2002)
        XCTAssertEqual(saved.queue["similar"]["attempted"].array.count, 2002)
        let requests = await owner.requests
        XCTAssertEqual(requests, [.songRecommendations(seedID: "100", offset: 15)])
        await service.close()
        let cold = try await NativeMusicFixture.service(store, accounts: owner)
        let restoredValue = try await cold.similarModeSnapshot(), restored = try XCTUnwrap(restoredValue)
        XCTAssertTrue(restored.active); XCTAssertEqual(restored.batches.count, 2002)
        let exited = try await cold.exitSimilarMode(), returningQueue = try await cold.queue()
        XCTAssertEqual(exited.state.total, 2001)
        XCTAssertEqual(returningQueue.entries.map(\.id), original.map { $0["id"].string })
        XCTAssertEqual(returningQueue.currentId, "entry-2001")
        XCTAssertEqual(exited.action, .keepCurrent)
        let after = await owner.count()
        XCTAssertEqual(after, 1, "cold restore and exit do not fetch another recommendation")
        await cold.close()
    }

    func testSimilarSingleResponseOverTwoThousandIsAcceptedAndCommitted() async throws {
        let owner = SimilarModeAccountFixture([(1000..<3001).map(String.init)])
        let store = NativeMusicMemoryDocument(try fixtureDocument())
        let service = try await NativeMusicFixture.service(store, accounts: owner), before = store.snapshot()
        let entered = try await service.enterSimilarMode()
        XCTAssertEqual(entered.state.total, 2002)
        XCTAssertEqual(store.snapshot().2, before.2 + 1)
        let queue = try await service.queue()
        XCTAssertEqual(queue.entries.count, 2002)
        await service.close()
    }

    func testCurrentHeartbeatUsesActualOccurrenceDespiteMetadataCollisionAndHomeSessionShortcut() async throws {
        let owner = SimilarModeAccountFixture([["201", "202"], ["301", "202", "302", "303", "304"]],
                                              metadata: true)
        let service = try await NativeMusicFixture.service(NativeMusicMemoryDocument(try fixtureDocument()), accounts: owner)
        let home = try await service.startSimilarMode(.heartbeat)
        let homeQueue = try await service.queue(), current = try XCTUnwrap(homeQueue.entries.last)
        let selected = try await service.changeQueue(.select(current.id), expectedRevision: home.state.revision)
        let playing = try await resolve(service, id: current.id)
        _ = try await service.recordSimilarPlaybackFact(fact(playing, .playing))
        await owner.rename("202", to: "Changed response title")
        let result = try await service.startSimilarMode(.currentHeartbeat(occurrenceID: current.id,
                                                                         expectedRevision: selected.revision))
        let queue = try await service.queue()
        XCTAssertEqual(result.action, .keepCurrent)
        XCTAssertEqual(queue.currentId, current.id)
        XCTAssertEqual(queue.entries.count, 5)
        XCTAssertEqual(queue.entries[0].id, current.id)
        XCTAssertEqual(queue.entries[0].track.title, current.track.title)
        let retained = await service.attempt?.id
        XCTAssertEqual(retained, playing.attemptId)
        let snapshot = try await service.similarModeSnapshot()
        XCTAssertFalse(try XCTUnwrap(snapshot).continueEnabled)
        let requests = await owner.requests
        XCTAssertEqual(requests, [.heartbeat(lastID: nil, fromHome: true, context: nil),
                                  .heartbeat(lastID: "202", fromHome: false, context: nil)])
        let live = await service.liveRecommendation(entryID: current.id)
        XCTAssertEqual(live?.value["feedbackSource"].string, "aiMode")
        let returned = try await service.exitSimilarMode()
        XCTAssertEqual(returned.action, .keepCurrent)
        let original = try await service.queue()
        XCTAssertEqual(original.entries.map(\.id), ["entry-1", current.id, "entry-2"])
        XCTAssertEqual(original.currentId, current.id)
        let returnedAttempt = await service.attempt?.id
        XCTAssertEqual(returnedAttempt, playing.attemptId)
        await service.close()
    }

    func testCurrentHeartbeatRejectsWrongOccurrenceAndRevisionAndDoesNotGrantUnreturnedSeedProof() async throws {
        let owner = SimilarModeAccountFixture([["301", "302", "303", "304"]], metadata: true)
        let service = try await NativeMusicFixture.service(NativeMusicMemoryDocument(try fixtureDocument()), accounts: owner)
        let before = try await service.state()
        for start in [MusicSimilarModeStart.currentHeartbeat(occurrenceID: "entry-2", expectedRevision: before.revision),
                      .currentHeartbeat(occurrenceID: "entry-1", expectedRevision: before.revision + 1)] {
            do { _ = try await service.startSimilarMode(start); XCTFail("stale entrance") }
            catch { XCTAssertTrue([MusicServiceError.targetChanged, .staleRevision].contains(MusicServiceError.sanitized(error))) }
        }
        let unopened = await owner.openings
        XCTAssertEqual(unopened, 0)
        let result = try await service.startSimilarMode(.currentHeartbeat(occurrenceID: "entry-1",
                                                                         expectedRevision: before.revision))
        XCTAssertEqual(result.state.total, 5)
        let seedProof = await service.liveRecommendation(entryID: "entry-1")
        XCTAssertNil(seedProof, "retaining current audio does not claim it was in the response")
        let queue = try await service.queue()
        for entry in queue.entries.dropFirst() {
            let proof = await service.liveRecommendation(entryID: entry.id)
            XCTAssertEqual(proof?.value["songId"].id, entry.track.sources.first?.nativeId)
            XCTAssertEqual(proof?.value["feedbackSource"].string, "aiMode")
        }
        await service.close()
    }

    func testOldOrIncompleteSimilarSessionIsRejectedWithoutAccountLeaseOrDocumentWrites() async throws {
        var document = try MusicDocument(fixtureDocument(), runID: "fixture")
        let entries = document.queue["entries"].array
        let current = MJ.obj([
            ("schema", .n(2)), ("seed", entries[0]["song"]), ("seedEntryId", entries[0]["id"]),
            ("returnQueue", MusicNativeSimilarData.queueSnapshot(document.queue)),
            ("sourceIds", .strings(["100"])), ("fromHome", .bool(false)), ("provider", .string("heartbeat")),
            ("providerContext", .null), ("offset", .n(15)), ("exhausted", .bool(false)), ("continue", .bool(true)), ("scope", .string("synthetic-scope")),
            ("status", .string("ready")), ("batches", .array([
                MusicNativeSimilarData.batch("batch", basedOn: entries[0]["song"], provider: .heartbeat, rows: entries)
            ])), ("attempted", .array([]))
        ])
        for invalid in ["schema1", "provider", "sourceIds", "batchProvider", "returnSerial", "returnCurrent"] {
            var saved = current
            switch invalid {
            case "schema1": saved["schema"] = .n(1)
            case "provider", "sourceIds": saved.remove(invalid)
            case "batchProvider":
                var batches = saved["batches"].array; batches[0].remove("provider")
                saved["batches"] = .array(batches)
            case "returnSerial":
                var returning = saved["returnQueue"]["entries"].array
                returning[1]["id"] = .string("entry-100")
                saved["returnQueue"]["entries"] = .array(returning)
            default: saved["returnQueue"]["currentId"] = .string("entry-999")
            }
            document.queue["similar"] = saved
            let bytes = try document.encode(changed: ["queue", "history", "playback"])
            let store = NativeMusicMemoryDocument(bytes), owner = SimilarModeAccountFixture([])
            let service = try await NativeMusicFixture.service(store, accounts: owner)
            do { _ = try await service.similarModeSnapshot(); XCTFail("Must reject \(invalid)") }
            catch { XCTAssertEqual(error as? MusicServiceError, .corruptDocument) }
            do { _ = try await service.queue(); XCTFail("Invalid state must not be hidden") }
            catch { XCTAssertEqual(error as? MusicServiceError, .corruptDocument) }
            XCTAssertEqual(store.snapshot().0, bytes); XCTAssertEqual(store.snapshot().2, 0)
            let openings = await owner.openings, requests = await owner.count()
            XCTAssertEqual(openings, 0); XCTAssertEqual(requests, 0)
            await service.close()
        }
    }

    func testPreviewEnterAndSeedExitPreserveMediaOwnerAndReturnQueue() async throws {
        let owner = SimilarModeAccountFixture([["100", "201", "201", "202"]])
        let store = NativeMusicMemoryDocument(try fixtureDocument())
        let service = try await NativeMusicFixture.service(store, accounts: owner)
        let candidate = try await resolve(service, id: "entry-1")
        _ = try await service.recordSimilarPlaybackFact(fact(candidate, .playing))
        let before = try await service.queue()
        let previewValue = try await service.previewSimilarMode()
        let preview = try XCTUnwrap(previewValue)
        XCTAssertFalse(preview.active); XCTAssertFalse(preview.continueEnabled)
        XCTAssertEqual(preview.candidates.count, 3, "the first batch includes the actual current recording")
        let entered = try await service.enterSimilarMode()
        XCTAssertEqual(entered.action, .keepCurrent); XCTAssertEqual(entered.state.currentId, "entry-1")
        let attempt = await service.attempt?.id
        XCTAssertEqual(attempt, candidate.attemptId)
        let liveValue = try await service.similarModeSnapshot()
        let live = try XCTUnwrap(liveValue)
        XCTAssertTrue(live.active); XCTAssertEqual(live.batches.count, 1)
        XCTAssertFalse(live.continueEnabled)
        let exited = try await service.exitSimilarMode()
        XCTAssertEqual(exited.action, .keepCurrent)
        let restored = try await service.queue(), retained = await service.attempt?.id
        XCTAssertEqual(restored.entries.map(\.id), before.entries.map(\.id))
        XCTAssertEqual(retained, candidate.attemptId)
        let requests = await owner.requests
        XCTAssertEqual(requests, [.heartbeat(lastID: "100", fromHome: false, context: nil)])
    }

    func testTailPlayingFetchesOnceAndEndedNavigatesExistingQueueBeforeResponse() async throws {
        let owner = SimilarModeAccountFixture([["201", "202"], ["301", "302", "303", "304"]])
        let service = try await NativeMusicFixture.service(NativeMusicMemoryDocument(try fixtureDocument()), accounts: owner)
        _ = try await service.startSimilarMode(.songRoaming(sourceIDs: ["100"], sourceName: "Seed"))
        let queue = try await service.queue(), tail = try XCTUnwrap(queue.entries.last)
        _ = try await service.changeQueue(.select(tail.id), expectedRevision: queue.revision)
        let candidate = try await resolve(service, id: tail.id), gate = NativeMusicGate()
        await owner.suspendNext(gate)
        _ = try await service.recordSimilarPlaybackFact(fact(candidate, .playing))
        await gate.started()
        let pending = await service.similarMode.active?.pending
        _ = try await service.recordSimilarPlaybackFact(fact(candidate, .progress))
        _ = try await service.recordSimilarPlaybackFact(fact(candidate, .ended))
        let state = try await service.state()
        let result = try await service.advance(.next, reason: .ended, expectedRevision: state.revision)
        XCTAssertEqual(result.selectedId, queue.entries[0].id, "network does not gate list wrapping")
        await gate.release(); await pending?.value
        let after = try await service.queue()
        XCTAssertEqual(after.entries.count, 6)
        XCTAssertEqual(after.currentId, result.selectedId, "late append does not navigate")
        let requests = await owner.requests
        XCTAssertEqual(requests, [.songRoaming(sourceIDs: ["100"]), .songRoaming(sourceIDs: ["100"])])
        await service.close()
    }

    func testExplicitContinueCancellationAndAccountRevocationRejectLateBatches() async throws {
        let owner = SimilarModeAccountFixture([["201"], ["301"], ["401"]])
        let service = try await NativeMusicFixture.service(NativeMusicMemoryDocument(try fixtureDocument()), accounts: owner)
        _ = try await service.startSimilarMode(.songRoaming(sourceIDs: ["100"], sourceName: "Seed"))
        let enabledValue = try await service.similarModeSnapshot()
        try await service.setSimilarContinue(true, snapshotID: XCTUnwrap(enabledValue).id)
        let queue = try await service.queue(), candidate = try await resolve(service, id: XCTUnwrap(queue.currentId))
        let gate = NativeMusicGate(); await owner.suspendNext(gate)
        _ = try await service.recordSimilarPlaybackFact(fact(candidate, .playing))
        await gate.started()
        let activeValue = try await service.similarModeSnapshot()
        let active = try XCTUnwrap(activeValue)
        let cancelledBatch = await service.similarMode.active?.pending
        try await service.setSimilarContinue(false, snapshotID: active.id)
        await gate.release()
        await cancelledBatch?.value
        let count = try await service.queue().entries.count
        XCTAssertEqual(count, 1)
        let disabledValue = try await service.similarModeSnapshot()
        let disabled = try XCTUnwrap(disabledValue)
        XCTAssertFalse(disabled.continueEnabled)
        try await service.setSimilarContinue(true, snapshotID: active.id)
        let buffered = await service.similarMode.active?.pending
        await buffered?.value
        let appended = try await service.queue()
        XCTAssertEqual(appended.entries.count, 2, "reenabling consumes the retained response")
        let calls = await owner.count(); XCTAssertEqual(calls, 2)
        let revokedGate = NativeMusicGate(); await owner.suspendNext(revokedGate)
        _ = try await service.changeQueue(.select(appended.entries[1].id), expectedRevision: appended.revision)
        let next = try await resolve(service, id: appended.entries[1].id)
        _ = try await service.recordSimilarPlaybackFact(fact(next, .playing))
        await revokedGate.started()
        let revokedPending = await service.similarMode.active?.pending
        await owner.revoke(); await revokedGate.release(); await revokedPending?.value
        let snapshot = try await service.similarModeSnapshot()
        XCTAssertNil(snapshot)
        let final = try await service.queue()
        XCTAssertEqual(final.entries.count, 2)
        await service.close()
    }

    func testCandidateIsSnapshotBoundExitIsSilentAndGenericReplaceDoesNotAutoEnter() async throws {
        let owner = SimilarModeAccountFixture([["201", "202"]])
        let service = try await NativeMusicFixture.service(NativeMusicMemoryDocument(try fixtureDocument()), accounts: owner)
        let previewValue = try await service.previewSimilarMode()
        let preview = try XCTUnwrap(previewValue)
        do {
            _ = try await service.playSimilarCandidate(snapshotID: UUID(), candidateID: preview.candidates[0].id)
            XCTFail("foreign snapshot")
        } catch { XCTAssertEqual(error as? MusicServiceError, .targetChanged) }
        let played = try await service.playSimilarCandidate(snapshotID: preview.id, candidateID: preview.candidates[1].id)
        XCTAssertEqual(played.action, .playCurrent)
        let exited = try await service.exitSimilarMode()
        XCTAssertEqual(exited.action, .keepCurrent)
        XCTAssertEqual(exited.state.currentId, played.state.currentId)
        let state = try await service.changeQueue(.replace([.init(kind: .queue, id: "entry-1")], index: 0),
                                                   expectedRevision: exited.state.revision)
        XCTAssertEqual(state.total, 1)
        let generic = try await service.similarModeSnapshot()
        XCTAssertNil(generic, "non-search .replace must not acquire automatic roaming")
        await service.close()
    }

    func testFailedEntryRetryColdRestoreAndCASFailurePreserveQueue() async throws {
        let owner = SimilarModeAccountFixture([[], ["201", "202"]])
        let store = NativeMusicMemoryDocument(try fixtureDocument())
        let cancellingStore = SimilarModeControlledDocument(store)
        let service = NativeMusicService(document: cancellingStore, accounts: owner,
            qq: NativeMusicQQFixture(), transport: NativeMusicNoNetwork(), clock: { NativeMusicFixture.date })
        try await service.setAccess(available: true, generation: 1)
        let before = store.snapshot().0
        do { _ = try await service.enterSimilarMode(); XCTFail("empty preview") }
        catch { XCTAssertEqual(error as? MusicServiceError, .notFound) }
        XCTAssertEqual(store.snapshot().0, before)
        let emptyValue = try await service.similarModeSnapshot()
        let empty = try XCTUnwrap(emptyValue)
        XCTAssertTrue(empty.canRetry)
        _ = try await service.retrySimilarMode(snapshotID: empty.id)
        let activeValue = try await service.similarModeSnapshot()
        let active = try XCTUnwrap(activeValue)
        XCTAssertTrue(active.active)
        let saved = store.snapshot().0
        store.fail(.storageConflict)
        do { _ = try await service.exitSimilarMode(); XCTFail("CAS conflict") }
        catch { XCTAssertEqual(error as? MusicServiceError, .storageConflict) }
        XCTAssertEqual(store.snapshot().0, saved)
        store.fail(nil)
        let reopened = try await NativeMusicFixture.service(store, accounts: owner)
        let restoredValue = try await reopened.similarModeSnapshot()
        let restored = try XCTUnwrap(restoredValue)
        XCTAssertTrue(restored.active); XCTAssertEqual(restored.batches.count, 1)
        let requestCount = await owner.count()
        XCTAssertEqual(requestCount, 2, "restoring a session does not fetch a fresh recommendation")
        await reopened.close()

        let current = try await service.queue()
        _ = try await resolve(service, id: XCTUnwrap(current.currentId))
        cancellingStore.cancelAfterNextCommit()
        let cancellation = Task { try await service.exitSimilarMode() }
        do { _ = try await cancellation.value; XCTFail("a committed exit cannot be reported as rollback") }
        catch { XCTAssertEqual(error as? MusicServiceError, .commitUnconfirmed) }
        let savedAfterExit = try MusicDocument(store.snapshot().0, runID: "reopened")
        XCTAssertEqual(savedAfterExit.queue["similar"], .null)
        XCTAssertEqual(savedAfterExit.queue["entries"].array.map { $0["id"].string }, ["entry-1", "entry-2"])
        let retainedAttempt = await service.attempt?.id
        XCTAssertNil(retainedAttempt)
        let afterUnconfirmed = try await service.similarModeSnapshot()
        XCTAssertNil(afterUnconfirmed)
        await service.close()
    }

    func testFailedHomeArtistEntranceCanBeRetriedWithTheSameArtistIdentity() async throws {
        let owner = SimilarModeAccountFixture([[], ["301", "302"]])
        let service = try await NativeMusicFixture.service(NativeMusicMemoryDocument(try fixtureDocument()), accounts: owner)
        let entrance = MusicSimilarModeStart.artistRoaming(sourceIDs: ["11"], sourceName: "艺人")
        let before = try await service.queue()
        do { _ = try await service.startSimilarMode(entrance); XCTFail("empty entrance") }
        catch { XCTAssertEqual(error as? MusicServiceError, .notFound) }
        let unchanged = try await service.queue()
        XCTAssertEqual(unchanged.entries.map(\.id), before.entries.map(\.id))
        _ = try await service.startSimilarMode(entrance)
        let queue = try await service.queue()
        XCTAssertEqual(queue.entries.compactMap { $0.track.sources.first?.nativeId }, ["301", "302"])
        let requests = await owner.requests
        XCTAssertEqual(requests, [.artistRoaming(sourceIDs: ["11"]), .artistRoaming(sourceIDs: ["11"])])
        await service.close()
    }

    private func fixtureDocument(count: Int = 2) throws -> Data {
        var d = try MusicDocument(nil, runID: "fixture")
        d.queue["entries"] = .array(try (0..<count).map { index in
            let id = index == 0 ? "100" : "200"
            let song = try MusicModel.normalize(SimilarModeAccountFixture.track(id))
            return .obj([("id", .string("entry-\(index + 1)")), ("key", song["songId"]),
                         ("song", song), ("context", .obj([("platform", .string("wy")), ("sourceId", .string("favorites"))]))])
        })
        d.queue["currentId"] = .string("entry-1"); d.queue["serial"] = .n(count)
        d.queue["source"] = .obj([("sourceType", .string("playlist")), ("platform", .string("wy")), ("sourceId", .string("favorites"))])
        return try d.encode(changed: ["queue", "history", "playback"])
    }
    private func resolve(_ service: NativeMusicService, id: String) async throws -> MusicPlaybackCandidate {
        let options = (try await service.sourceAvailability(for: .init(kind: .queue, id: id))).options
        return try await service.resolve(attemptId: UUID().uuidString.lowercased(), occurrenceId: id,
            selection: XCTUnwrap(options.first).selection(quality: "128k"))
    }
    private func fact(_ candidate: MusicPlaybackCandidate, _ kind: MusicPlaybackFact.Kind) -> MusicPlaybackFact {
        .init(attemptId: candidate.attemptId, occurrenceId: candidate.occurrenceId,
              resourceId: candidate.resourceId, kind: kind, position: kind == .ended ? 180 : 1)
    }
}

actor SimilarModeAccountFixture: MusicNativeAccountProviding, MusicNativeSimilarModeProviding {
    var requests: [MusicNativeSimilarModeRequest] = []
    var batches: [[String]]
    var nextGate: NativeMusicGate?
    var leases: [UUID: MusicAccountQueueLease] = [:]
    var openings = 0
    var endings = 0
    var activeRequests = 0
    var resolutions = 0
    var hasMore: [Bool?]
    let metadata: Bool
    var names: [String: String]
    var nextFailure: MusicServiceError?
    init(_ batches: [[String]], hasMore: [Bool?] = [], metadata: Bool = false, names: [String: String] = [:]) {
        self.batches = batches; self.hasMore = hasMore; self.metadata = metadata; self.names = names
    }
    nonisolated static func track(_ id: String, name: String? = nil, recommendation: MJ = .null) -> MusicNativeTrackData {
        var song = MJ.obj([("songmid", .string(id)),
            ("name", .string(name ?? "Song " + id)), ("singer", .string("Synthetic")),
            ("albumName", .string("Album")), ("duration", .n(180)),
            ("types", .array([.obj([("type", .string("128k"))])]))])
        if recommendation != .null { song["_recommendation"] = recommendation }
        return .init(platform: "wy", data: song.bytes)
    }
    func count() -> Int { requests.count }
    func suspendNext(_ gate: NativeMusicGate) { nextGate = gate }
    func failNext(_ error: MusicServiceError) { nextFailure = error }
    func rename(_ id: String, to name: String) { names[id] = name }
    func revoke() { for lease in leases.values { lease.revoke() } }
    func beginAccountQueue(platform: MusicAccountPlatform) -> MusicAccountQueueLease {
        openings += 1
        let lease = MusicAccountQueueLease(platform: platform); leases[lease.id] = lease; return lease
    }
    func checkAccountQueue(_ lease: MusicAccountQueueLease) throws {
        guard leases[lease.id] === lease else { throw MusicServiceError.staleAccess }
        try lease.authorization.check()
    }
    func endAccountQueue(_ lease: MusicAccountQueueLease) {
        endings += 1; leases.removeValue(forKey: lease.id)?.revoke()
    }
    func similarModeScope(_ lease: MusicAccountQueueLease) throws -> String {
        try checkAccountQueue(lease); return "synthetic-scope"
    }
    func similarModeBatch(_ lease: MusicAccountQueueLease, request: MusicNativeSimilarModeRequest) async throws -> MusicNativeSimilarModeBatch {
        try checkAccountQueue(lease); requests.append(request)
        let ids = batches.isEmpty ? [] : batches.removeFirst(), gate = nextGate; nextGate = nil
        let more = hasMore.isEmpty ? nil : hasMore.removeFirst()
        let failure = nextFailure; nextFailure = nil
        activeRequests += 1
        defer { activeRequests -= 1 }
        if let gate { await gate.wait() }
        if let failure { throw failure }
        // Deliberately noncooperative response: NativeMusicService's lease and
        // request identity must still reject this if it was revoked/cancelled.
        let source: String, feedbackSource: String, sourceID: String
        let context: MusicSimilarHeartbeatContext?
        let offset: Int?
        switch request {
        case .heartbeat(let last, _, _):
            offset = nil
            source = "heartbeat"; feedbackSource = "aiMode"; sourceID = "900"
            context = .init(playlistID: "900", seedID: last ?? "100")
        case .songRoaming, .artistRoaming:
            source = "similar"; feedbackSource = ""; sourceID = ""; context = nil; offset = nil
        case .songRecommendations(_, let current):
            offset = current + 15
            source = "moreRcmd"; feedbackSource = "synthetic_source_map"; sourceID = ""; context = nil
        }
        return .init(items: ids.map { id in
            Self.track(id, name: names[id], recommendation: metadata ? .obj([
                ("songId", .string(id)), ("alg", .string("alg-" + id)), ("source", .string(source)),
                ("sourceId", .string(sourceID)), ("feedbackSource", .string(feedbackSource))
            ]) : .null)
        }, heartbeatContext: context, hasMore: offset == nil ? more : more ?? true, offset: offset)
    }
    func accountQueuePage(_ lease: MusicAccountQueueLease, kind: MusicAccountQueueKind,
                          cursor: String?) throws -> MusicNativeTrackPage { throw MusicServiceError.unsupported }
    func nativeRecommendations(platform: MusicAccountPlatform, kind: String, cursor: String?) throws -> MusicNativeTrackPage { throw MusicServiceError.unsupported }
    func nativeCollections(platform: MusicAccountPlatform, cursor: String?) throws -> MusicAccountCollectionPage { throw MusicServiceError.unsupported }
    func nativeTracks(platform: MusicAccountPlatform, collectionID: String, cursor: String?) throws -> MusicNativeTrackPage { throw MusicServiceError.unsupported }
    func nativeOptions(platform: MusicAccountPlatform, nativeID: String, sourceKey: String) -> [MusicPlaybackOption] {
        [.init(installationId: "official:synthetic", generation: 1, siteId: "wy", sourceKey: sourceKey,
               qualities: ["128k"], title: "Synthetic", version: "1", contentHash: String(repeating: "a", count: 64))]
    }
    func nativeResolve(platform: MusicAccountPlatform, nativeID: String, selection: MusicPlaybackSelection) -> MusicNativeAudio {
        resolutions += 1
        return .init(url: URL(string: "https://m701.music.126.net/synthetic.mp3")!)
    }
}

/// Deterministic read/commit boundaries, without timers or scheduling guesses.
final class SimilarModeControlledDocument: MusicNativeDocumentStoring, @unchecked Sendable {
    private let backing: NativeMusicMemoryDocument
    private let lock = NSLock()
    private var cancelNext = false
    private var nextRead: (@Sendable () -> Void)?
    init(_ backing: NativeMusicMemoryDocument) { self.backing = backing }
    func read() throws -> Data? {
        let value = try backing.read()
        lock.lock(); let callback = nextRead; nextRead = nil; lock.unlock()
        callback?(); return value
    }
    func afterNextRead(_ callback: @escaping @Sendable () -> Void) {
        lock.lock(); nextRead = callback; lock.unlock()
    }
    func cancelAfterNextCommit() { lock.lock(); cancelNext = true; lock.unlock() }
    func compareAndSwap(expected: Data?, replacement: Data) throws {
        try backing.compareAndSwap(expected: expected, replacement: replacement)
        lock.lock(); let cancel = cancelNext; cancelNext = false; lock.unlock()
        if cancel { withUnsafeCurrentTask { $0?.cancel() } }
    }
}
