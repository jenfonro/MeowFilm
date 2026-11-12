import XCTest
import MeowMusicFeature
import MeowStorage
@testable import MeowMusicNativeService

final class MusicNativeQueueRecommendationTests: XCTestCase {
    func testPreviewSharesAcquiredMetadataWithoutQueryEvidenceOrPersistingNonmembers() async throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: root) }
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        let metadataDocument = try DocumentStore(baseURL: root, namespace: MusicNativePersistentSourceDiscoveryCache.namespace)
        let cache = MusicNativePersistentSourceDiscoveryCache(storeFactory: { metadataDocument }, clock: { NativeMusicFixture.date })
        let owner = SimilarModeAccountFixture([["301"]])
        let qq = MusicNativeCandidateDiscoveryQQ(), http = MusicNativeCandidateDiscoveryHTTP()
        let store = NativeMusicMemoryDocument(try document(["100"]))
        let service = try await NativeMusicFixture.service(store, accounts: owner, qq: qq, http: http, sourceDiscovery: cache)
        _ = try await service.createPlaylist(name: "Saved", refs: [.init(kind: .queue, id: "entry-1")])
        await service.flushSourceDiscoveryPersist()
        let persisted = try XCTUnwrap(metadataDocument.read(key: "records"))
        let file = root.appendingPathComponent(MusicNativePersistentSourceDiscoveryCache.namespace)
            .appendingPathComponent("records.json")
        XCTAssertTrue(FileManager.default.fileExists(atPath: file.path))
        let otherEntrance = try MusicModel.normalize(MusicNativeCandidateDiscoveryFixture.track(
            "tx", id: "OtherMID", title: "Song 301", artist: "Synthetic"))
        let unknown = try await service.cachedSourceDiscoveryState(otherEntrance)
        XCTAssertTrue(unknown.entries.isEmpty)
        _ = try await startPlaying(service)
        let before = store.snapshot().0, writes = store.snapshot().2

        let previewValue = try await preview(service), preview = try XCTUnwrap(previewValue)
        XCTAssertEqual(preview.candidates.map(\.song.title), ["Song 301"])
        let acquired = try await service.cachedSourceDiscoveryState(otherEntrance)
        XCTAssertEqual(Set(acquired.entries.keys), ["wy"])
        let known = try XCTUnwrap(acquired.entries["wy"])
        let recommended = try MusicModel.normalize(SimilarModeAccountFixture.track("301"))
        XCTAssertEqual(known.sources.map(\.sourceKey), [recommended["sources"].array[0]["sourceKey"].string])
        XCTAssertEqual(known.checkedAt, 0)
        XCTAssertTrue(known.observations.isEmpty)
        XCTAssertTrue(acquired.checks.isEmpty)

        let reused = try await service.sharedSongMetadata(otherEntrance)
        XCTAssertEqual(Set(reused["sources"].array.map { $0["source"].string }), ["tx", "wy"])
        XCTAssertEqual(reused["sources"].array.first?["sourceKey"], otherEntrance["sources"].array.first?["sourceKey"])
        let mapped = try XCTUnwrap(reused["sources"].array.first { $0["source"].string == "wy" })
        XCTAssertEqual(mapped["sourceKey"], recommended["sources"].array[0]["sourceKey"])
        let unchangedEvidence = try await service.cachedSourceDiscoveryState(otherEntrance)
        XCTAssertTrue(unchangedEvidence.entries.values.allSatisfy { $0.checkedAt == 0 && $0.observations.isEmpty })
        XCTAssertTrue(unchangedEvidence.checks.isEmpty)
        let cachedPreview = try await service.queueRecommendationSnapshot()
        XCTAssertEqual(cachedPreview?.id, preview.id)
        let requests = await owner.count(), qqCalls = await qq.calls(), httpCalls = await http.calls()
        XCTAssertEqual(requests, 1)
        XCTAssertTrue(qqCalls.isEmpty)
        XCTAssertTrue(httpCalls.isEmpty)
        XCTAssertEqual(store.snapshot().0, before)
        XCTAssertEqual(store.snapshot().2, writes)

        await service.flushSourceDiscoveryPersist()
        XCTAssertEqual(try metadataDocument.read(key: "records"), persisted)
        let reopened = MusicNativePersistentSourceDiscoveryCache(storeFactory: { metadataDocument }, clock: { NativeMusicFixture.date })
        let unsaved = await reopened.entries(fingerprint: MusicModel.fingerprint(otherEntrance), duration: 180)
        XCTAssertTrue(unsaved.isEmpty)
        let local = try MusicModel.normalize(SimilarModeAccountFixture.track("100"))
        let saved = await reopened.entries(fingerprint: MusicModel.fingerprint(local), duration: 180)
        XCTAssertFalse(try XCTUnwrap(saved["wy"]).sources.isEmpty, "local membership is the durable positive control")
        await service.close()
    }

    func testPreviewClickAppendsWholeBatchAndMarksRecommendationWithoutCheckpoint() async throws {
        let ids = ["100"] + Array(repeating: "200", count: 23)
        let owner = SimilarModeAccountFixture([["200", "301", "301", "302", "303", "304", "305"]],
            metadata: true, names: ["200": "Changed old metadata", "301": "Same metadata", "302": "Same metadata"])
        let store = NativeMusicMemoryDocument(try document(ids))
        let service = try await NativeMusicFixture.service(store, accounts: owner)
        let initialQueue = try await service.queue()
        _ = try await startPlaying(service)
        _ = try await service.changeQueue(.select(initialQueue.entries.last!.id), expectedRevision: initialQueue.revision)
        _ = try await startPlaying(service)
        let before = try await service.queue(), bytes = store.snapshot().0, writes = store.snapshot().2
        let snapshotValue = try await preview(service)
        let snapshot = try XCTUnwrap(snapshotValue)
        XCTAssertEqual(snapshot.candidates.count, 5)
        XCTAssertFalse(snapshot.continueEnabled)
        XCTAssertEqual(snapshot.currentEntryID, before.currentId)
        XCTAssertEqual(store.snapshot().0, bytes)
        XCTAssertEqual(store.snapshot().2, writes)
        let unchanged = try await service.queue(), mode = try await service.similarModeSnapshot()
        XCTAssertEqual(unchanged.entries.map(\.id), before.entries.map(\.id))
        XCTAssertEqual(unchanged.currentId, before.currentId)
        XCTAssertNil(mode)
        let requests = await owner.requests
        XCTAssertEqual(requests, [.songRecommendations(seedID: "100", offset: 0)])
        let chosen = try await service.playQueueRecommendation(snapshotID: snapshot.id,
                                                               candidateID: snapshot.candidates[3].id)
        let queue = try await service.queue()
        XCTAssertEqual(chosen.action, .playCurrent)
        XCTAssertEqual(queue.entries.count, ids.count + 5)
        XCTAssertEqual(queue.entries.prefix(ids.count).map(\.id), before.entries.map(\.id))
        XCTAssertEqual(queue.entries.suffix(5).compactMap { $0.track.sources.first?.nativeId },
                       ["301", "302", "303", "304", "305"])
        XCTAssertEqual(queue.currentId, queue.entries[ids.count + 3].id)
        let saved = try MusicDocument(store.snapshot().0, runID: "evidence")
        let original = try MusicDocument(bytes, runID: "evidence")
        XCTAssertEqual(saved.queue["source"]["sourceType"].string, "queue-recommendations")
        XCTAssertEqual(saved.queue["similar"], .null)
        XCTAssertEqual(saved.queue["entries"].array[0]["context"], original.queue["entries"].array[0]["context"])
        for entry in queue.entries.suffix(5) {
            let live = await service.liveRecommendation(entryID: entry.id)
            XCTAssertEqual(live?.value["songId"].id, entry.track.sources.first?.nativeId)
            XCTAssertEqual(live?.value["feedbackSource"].string, "synthetic_source_map")
            XCTAssertEqual(live?.value["alg"].string, "alg-" + (entry.track.sources.first?.nativeId ?? ""))
        }
        let originalProof = await service.liveRecommendation(entryID: "entry-1")
        XCTAssertNil(originalProof)
        do {
            _ = try await service.playQueueRecommendation(snapshotID: snapshot.id, candidateID: snapshot.candidates[0].id)
            XCTFail("a committed batch cannot be replayed with its old candidate token")
        } catch { XCTAssertEqual(error as? MusicServiceError, .targetChanged) }
        await service.close()
        let cold = try await NativeMusicFixture.service(store, accounts: owner)
        let coldProof = await cold.liveRecommendation(entryID: queue.entries.last!.id)
        XCTAssertNil(coldProof, "persisted attribution cannot restore live proof")
        let coldQueue = try await cold.queue()
        XCTAssertEqual(coldQueue.entries.count, queue.entries.count)
        let requestCount = await owner.count()
        XCTAssertEqual(requestCount, 1)
        await cold.close()
    }

    func testOnlyOriginalSearchSeedNeedsNativeIDAndOrdinaryQueuesDoNotAutoRecommend() async throws {
        for eligible in [false, true] {
            var d = try MusicDocument(document(["100", "200"]), runID: "fixture")
            var rows = d.queue["entries"].array
            rows[eligible ? 1 : 0]["song"] = try NativeMusicFixture.song()
            d.queue["entries"] = .array(rows)
            let store = NativeMusicMemoryDocument(try d.encode(changed: ["queue", "playback", "history"]))
            let owner = SimilarModeAccountFixture([["301"]])
            let service = try await NativeMusicFixture.service(store, accounts: owner)
            let snapshot = try await service.queueRecommendationSnapshot()
            XCTAssertEqual(snapshot != nil, eligible)
            let openings = await owner.openings; XCTAssertEqual(openings, 0, "projection never opens an account")
            await service.close()
        }
        var d = try MusicDocument(document(["100"]), runID: "fixture")
        d.queue["source"] = .obj([("sourceType", .string("playlist"))])
        let owner = SimilarModeAccountFixture([["301"]])
        let service = try await NativeMusicFixture.service(NativeMusicMemoryDocument(try d.encode(changed: ["queue", "playback", "history"])), accounts: owner)
        let snapshot = try await service.previewQueueRecommendations()
        XCTAssertNil(snapshot)
        let allowed = try await service.canEnterSongRecommendations(); XCTAssertTrue(allowed)
        await service.close()
    }

    func testContinuationUsesFixedSeedAndServerOffsetAndOnlyRealTailPlayback() async throws {
        let owner = SimilarModeAccountFixture([["301", "302"], ["401", "402"]], hasMore: [true, false])
        let service = try await NativeMusicFixture.service(NativeMusicMemoryDocument(try document(["100"])), accounts: owner)
        let value = try await preview(service), first = try XCTUnwrap(value)
        let chosen = try await service.playQueueRecommendation(snapshotID: first.id, candidateID: first.candidates[0].id)
        let queue = try await service.queue()
        try await service.setQueueRecommendationContinue(true, snapshotID: first.id)
        _ = try await startPlaying(service)
        let beforeTail = await owner.count(); XCTAssertEqual(beforeTail, 1)
        _ = try await service.changeQueue(.select(queue.entries.last!.id), expectedRevision: chosen.state.revision)
        let gate = NativeMusicGate(); await owner.suspendNext(gate)
        let tail = try await startPlaying(service)
        await gate.started()
        let pending = await service.similarMode.queue?.pending
        _ = try await service.recordSimilarPlaybackFact(fact(tail, .progress))
        let single = await owner.count(); XCTAssertEqual(single, 2)
        await gate.release(); await pending?.value
        let appended = try await service.queue()
        XCTAssertEqual(appended.entries.count, 5); XCTAssertEqual(appended.currentId, tail.occurrenceId)
        let requests = await owner.requests
        XCTAssertEqual(requests, [.songRecommendations(seedID: "100", offset: 0), .songRecommendations(seedID: "100", offset: 15)])
        let terminal = try await service.queueRecommendationSnapshot()
        XCTAssertTrue(try XCTUnwrap(terminal).exhausted)
        await service.close()
    }

    func testNextAndEndedDoNotWaitForPendingCandidatesOrNavigateOnLateResponse() async throws {
        for ended in [false, true] {
            let owner = SimilarModeAccountFixture([["301", "302"]])
            let service = try await NativeMusicFixture.service(NativeMusicMemoryDocument(try document(["100"])), accounts: owner)
            let gate = NativeMusicGate(); await owner.suspendNext(gate)
            let tail = try await startPlaying(service)
            await gate.started()
            let pending = await service.similarMode.queue?.pending
            if ended { _ = try await service.recordSimilarPlaybackFact(fact(tail, .ended)) }
            let state = try await service.state()
            let advanced = try await service.advance(.next, reason: ended ? .ended : .manual, expectedRevision: state.revision)
            XCTAssertEqual(advanced.selectedId, "entry-1", "existing list wraps immediately")
            await service.pauseSimilarNavigation()
            await gate.release(); await pending?.value
            let queue = try await service.queue(), candidates = try await service.queueRecommendationSnapshot()
            XCTAssertEqual(queue.entries.count, 1); XCTAssertEqual(queue.currentId, "entry-1")
            XCTAssertEqual(candidates?.candidates.count, 2)
            await service.close()
        }
    }

    func testOrdinaryModePreservesInFlightCandidatesForExplicitSelectionOnly() async throws {
        let owner = SimilarModeAccountFixture([["301"]])
        let service = try await NativeMusicFixture.service(NativeMusicMemoryDocument(try document(["100"])), accounts: owner)
        let value = try await service.queueRecommendationSnapshot(), snapshot = try XCTUnwrap(value)
        let gate = NativeMusicGate(); await owner.suspendNext(gate)
        _ = try await startPlaying(service); await gate.started()
        let pending = await service.similarMode.queue?.pending
        try await service.setQueueRecommendationContinue(false, snapshotID: snapshot.id)
        await service.cancelQueueRecommendationPreview()
        await gate.release(); await pending?.value
        let disabled = try await service.queueRecommendationSnapshot()
        XCTAssertFalse(try XCTUnwrap(disabled).continueEnabled); XCTAssertEqual(disabled?.candidates.count, 1)
        let before = try await service.state()
        let next = try await service.advance(.next, reason: .manual, expectedRevision: before.revision)
        XCTAssertEqual(next.state.total, 1); XCTAssertEqual(next.selectedId, "entry-1")
        let choice = try XCTUnwrap(disabled?.candidates.first)
        _ = try await service.playQueueRecommendation(snapshotID: snapshot.id, candidateID: choice.id)
        let selected = try await service.queueRecommendationSnapshot()
        XCTAssertFalse(try XCTUnwrap(selected).continueEnabled)
        _ = try await startPlaying(service)
        let count = await owner.count(); XCTAssertEqual(count, 1)
        await service.close()
    }

    func testEveryOrdinaryModeStopsInFlightRecommendationAppendAndManualNextKeepsExistingQueue() async throws {
        for mode in [MusicPlaybackMode.loop, .single, .shuffle] {
            let owner = SimilarModeAccountFixture([["301", "302"], ["401", "402"]])
            let service = try await NativeMusicFixture.service(NativeMusicMemoryDocument(try document(["100"])), accounts: owner)
            let initial = try await preview(service), snapshot = try XCTUnwrap(initial)
            let selected = try await service.playQueueRecommendation(snapshotID: snapshot.id,
                candidateID: snapshot.candidates[0].id)
            try await service.setQueueRecommendationContinue(true, snapshotID: snapshot.id)
            let queue = try await service.queue(), tail = try XCTUnwrap(queue.entries.last)
            _ = try await service.changeQueue(.select(tail.id), expectedRevision: selected.state.revision)
            let gate = NativeMusicGate(); await owner.suspendNext(gate)
            _ = try await startPlaying(service); await gate.started()
            let pending = await service.similarMode.queue?.pending, before = try await service.state()
            _ = try await service.updateSettings(.init(ifRevision: before.settingsRevision, mode: mode))
            await gate.release(); await pending?.value
            let disabled = try await service.queueRecommendationSnapshot(), current = try await service.queue()
            XCTAssertEqual(disabled?.id, snapshot.id); XCTAssertFalse(try XCTUnwrap(disabled).continueEnabled)
            XCTAssertEqual(disabled?.candidates.count, 2)
            XCTAssertEqual(current.entries.count, queue.entries.count); XCTAssertEqual(current.currentId, tail.id)
            let next = try await service.advance(.next, reason: .manual, expectedRevision: current.revision)
            XCTAssertTrue(current.entries.contains { $0.id == next.selectedId })
            XCTAssertEqual(next.state.total, current.entries.count)
            let choice = try XCTUnwrap(disabled?.candidates.last)
            let explicit = try await service.playQueueRecommendation(snapshotID: snapshot.id, candidateID: choice.id)
            XCTAssertEqual(explicit.state.mode, mode)
            XCTAssertEqual(explicit.state.total, current.entries.count + 2)
            let retained = try await service.queueRecommendationSnapshot()
            XCTAssertFalse(try XCTUnwrap(retained).continueEnabled)
            let requests = await owner.requests
            XCTAssertEqual(requests, [.songRecommendations(seedID: "100", offset: 0),
                .songRecommendations(seedID: "100", offset: 15)])
            await service.close()
        }
    }

    func testEmptyDuplicateAndTerminalResponseStopAutomaticRequestsAndExplicitRetryKeepsCandidates() async throws {
        for ids in [[], ["100", "100"], ["301", "302"]] {
            let owner = SimilarModeAccountFixture([ids, ["401"]], hasMore: [false, true])
            let service = try await NativeMusicFixture.service(NativeMusicMemoryDocument(try document(["100"])), accounts: owner)
            let first = try await preview(service), terminal = try XCTUnwrap(first)
            XCTAssertTrue(terminal.exhausted); XCTAssertNil(terminal.error)
            let queue = try await service.queue(); XCTAssertEqual(queue.entries.count, 1)
            if ids.first == "301" {
                _ = try await service.playQueueRecommendation(snapshotID: terminal.id, candidateID: terminal.candidates[0].id)
            }
            let tail = try await service.queue()
            _ = try await service.changeQueue(.select(tail.entries.last!.id), expectedRevision: tail.revision)
            _ = try await startPlaying(service)
            let count = await owner.count(); XCTAssertEqual(count, 1)
            let retryValue = try await service.queueRecommendationSnapshot(), retry = try XCTUnwrap(retryValue)
            XCTAssertTrue(retry.canRetry)
            _ = try await service.retryQueueRecommendations(snapshotID: retry.id)
            let after = try await service.queue(), candidates = try await service.queueRecommendationSnapshot()
            XCTAssertEqual(after.entries.count, ids.first == "301" ? 3 : 1)
            XCTAssertEqual(candidates?.candidates.count, 1)
            await service.close()
        }
    }

    func testFailedPreviewRetainsQueueAndExplicitRetryReturnsCandidates() async throws {
        let owner = SimilarModeAccountFixture([[], ["301", "302", "303", "304"]])
        await owner.failNext(.upstreamFailed)
        let store = NativeMusicMemoryDocument(try document(["100"]))
        let service = try await NativeMusicFixture.service(store, accounts: owner)
        _ = try await startPlaying(service)
        let bytes = store.snapshot().0, writes = store.snapshot().2
        let failedValue = try await preview(service), failed = try XCTUnwrap(failedValue)
        XCTAssertEqual(failed.status, .error); XCTAssertEqual(failed.error, .upstreamFailed)
        XCTAssertTrue(failed.canRetry)
        _ = try await service.retryQueueRecommendations(snapshotID: failed.id)
        let retryValue = try await service.queueRecommendationSnapshot(), retry = try XCTUnwrap(retryValue)
        XCTAssertEqual(retry.candidates.count, 4)
        XCTAssertEqual(store.snapshot().0, bytes); XCTAssertEqual(store.snapshot().2, writes)
        XCTAssertFalse(retry.continueEnabled)
        await service.close()
    }

    func testSheetCancellationPreservesLeaseCandidatesAndDoesNotChangeQueue() async throws {
        let owner = SimilarModeAccountFixture([["301", "302"]])
        let store = NativeMusicMemoryDocument(try document(["100"]))
        let service = try await NativeMusicFixture.service(store, accounts: owner), gate = NativeMusicGate()
        await owner.suspendNext(gate)
        _ = try await startPlaying(service); await gate.started()
        let bytes = store.snapshot().0, writes = store.snapshot().2
        let pending = await service.similarMode.queue?.pending
        let value = await service.similarMode.queue?.lease, lease = try XCTUnwrap(value)
        await service.cancelQueueRecommendationPreview()
        XCTAssertTrue(lease.authorization.isActive)
        await gate.release(); await pending?.value
        let snapshot = try await service.queueRecommendationSnapshot()
        XCTAssertEqual(snapshot?.candidates.count, 2)
        XCTAssertEqual(store.snapshot().0, bytes); XCTAssertEqual(store.snapshot().2, writes)
        await service.close()
        let ended = await owner.endings; XCTAssertEqual(ended, 1)
    }

    func testSelectionAndReorderRetainSeedWhileReplacementAndAccountRevocationRejectOldResponse() async throws {
        for change in ["select", "reorder", "replace", "account"] {
            let owner = SimilarModeAccountFixture([["301", "302"]], metadata: true)
            let store = NativeMusicMemoryDocument(try document(["100", "200"]))
            let service = try await NativeMusicFixture.service(store, accounts: owner)
            let gate = NativeMusicGate(); await owner.suspendNext(gate)
            _ = try await startPlaying(service); await gate.started()
            _ = try await service.changeQueue(.select("entry-2"), expectedRevision: 0)
            let pending = await service.similarMode.queue?.pending
            let old = try await service.queueRecommendationSnapshot(), queue = try await service.queue()
            switch change {
            case "select": _ = try await service.changeQueue(.select("entry-1"), expectedRevision: queue.revision)
            case "reorder": _ = try await service.changeQueue(.reorder(["entry-2", "entry-1"]), expectedRevision: queue.revision)
            case "replace": _ = try await service.changeQueue(.replace([.init(kind: .queue, id: "entry-1")], index: 0), expectedRevision: queue.revision)
            default: await owner.revoke()
            }
            let writes = store.snapshot().2
            await gate.release(); await pending?.value
            let current = try await service.queueRecommendationSnapshot()
            if change == "select" || change == "reorder" {
                XCTAssertEqual(current?.id, old?.id); XCTAssertEqual(current?.candidates.count, 2)
            } else { XCTAssertTrue(current?.candidates.isEmpty != false) }
            XCTAssertEqual(store.snapshot().2, writes)
            let proof = await service.liveRecommendation(entryID: "entry-1"); XCTAssertNil(proof)
            let requests = await owner.requests; XCTAssertEqual(requests, [.songRecommendations(seedID: "100", offset: 0)])
            await service.close()
        }
    }

    func testCandidateCASConflictAndUnknownCommitNeverRollBackOrPublishProof() async throws {
        let owner = SimilarModeAccountFixture([["301", "302", "303", "304"]], metadata: true)
        let store = NativeMusicMemoryDocument(try document(["100"]))
        let controlled = SimilarModeControlledDocument(store)
        let service = NativeMusicService(document: controlled, accounts: owner,
            qq: NativeMusicQQFixture(), transport: NativeMusicNoNetwork(), clock: { NativeMusicFixture.date })
        try await service.setAccess(available: true, generation: 1)
        let previewValue = try await preview(service), preview = try XCTUnwrap(previewValue)
        let before = store.snapshot().0, writes = store.snapshot().2
        store.fail(.storageConflict)
        do {
            _ = try await service.playQueueRecommendation(snapshotID: preview.id, candidateID: preview.candidates[2].id)
            XCTFail("CAS conflict")
        } catch { XCTAssertEqual(error as? MusicServiceError, .storageConflict) }
        XCTAssertEqual(store.snapshot().0, before)
        store.fail(nil); controlled.cancelAfterNextCommit()
        let committing = Task {
            try await service.playQueueRecommendation(snapshotID: preview.id, candidateID: preview.candidates[2].id)
        }
        do { _ = try await committing.value; XCTFail("unknown commit must not be reported as rollback") }
        catch { XCTAssertEqual(error as? MusicServiceError, .commitUnconfirmed) }
        let saved = try MusicDocument(store.snapshot().0, runID: "evidence")
        XCTAssertEqual(saved.queue["entries"].array.count, 5)
        XCTAssertEqual(saved.queue["currentId"].string, "entry-4")
        let proof = await service.liveRecommendation(entryID: "entry-4")
        XCTAssertNil(proof)
        let state = try await service.state()
        XCTAssertEqual(state.total, 5)
        XCTAssertEqual(store.snapshot().2, writes + 1, "no recovery write can overwrite the uncertain commit")
        await service.close()
    }

    func testCloseRevokesProofAndWaitsForRecommendationWorkerToPhysicallyFinish() async throws {
        let owner = SimilarModeAccountFixture([["301"]])
        let service = try await NativeMusicFixture.service(NativeMusicMemoryDocument(try document(["100"])), accounts: owner)
        let gate = NativeMusicGate(); await owner.suspendNext(gate)
        let loading = Task { try await preview(service) }
        await gate.started()
        let leaseValue = await service.similarMode.queue?.lease, lease = try XCTUnwrap(leaseValue)
        var revocations = lease.authorization.revocations().makeAsyncIterator()
        let closing = Task { await service.close() }
        _ = await revocations.next()
        let draining = await service.similarMode.retiring.count, physicallyActive = await owner.activeRequests
        XCTAssertGreaterThan(draining, 0); XCTAssertEqual(physicallyActive, 1)
        await gate.release()
        await closing.value
        do { _ = try await loading.value; XCTFail("closed preview") }
        catch { XCTAssertEqual(error as? MusicServiceError, .closed) }
        let active = await owner.activeRequests, endings = await owner.endings
        let remaining = await service.similarMode.workers.count
        XCTAssertEqual(active, 0); XCTAssertEqual(endings, 1); XCTAssertEqual(remaining, 0)
    }

    func testPlayNextKeepsReadyOrInFlightCandidatesSeedAndPriorityAcrossLoopChanges() async throws {
        for inFlight in [false, true] {
            let owner = SimilarModeAccountFixture([["301", "302"], ["401"]])
            let service = try await NativeMusicFixture.service(NativeMusicMemoryDocument(try document(["100"])), accounts: owner)
            let gate = NativeMusicGate()
            if inFlight { await owner.suspendNext(gate) }
            _ = try await startPlaying(service)
            if inFlight { await gate.started() }
            else { _ = try await service.previewQueueRecommendations() }
            let value = try await service.queueRecommendationSnapshot(), original = try XCTUnwrap(value)
            let pending = await service.similarMode.queue?.pending
            var state = try await service.state()
            _ = try await service.changeQueue(.append([.init(kind: .queue, id: "entry-1")], next: true), expectedRevision: state.revision)
            for mode in [MusicPlaybackMode.single, .shuffle, .loop] {
                state = try await service.state()
                _ = try await service.updateSettings(.init(ifRevision: state.settingsRevision, mode: mode))
            }
            if inFlight { await gate.release(); await pending?.value }
            let retained = try await service.queueRecommendationSnapshot()
            XCTAssertEqual(retained?.id, original.id); XCTAssertEqual(retained?.candidates.count, 2)
            XCTAssertFalse(try XCTUnwrap(retained).continueEnabled)
            let queue = try await service.queue()
            XCTAssertEqual(queue.currentId, "entry-1"); XCTAssertEqual(queue.entries.count, 2)
            let next = try await service.advance(.next, reason: .manual, expectedRevision: queue.revision)
            XCTAssertEqual(next.selectedId, "entry-2", "explicit insertion precedes recommendations")
            let candidates = try await service.queueRecommendationSnapshot()
            XCTAssertEqual(candidates?.candidates.count, 2)
            let ordinary = try await service.advance(.next, reason: .manual, expectedRevision: next.state.revision)
            XCTAssertEqual(ordinary.selectedId, "entry-1")
            let retainedCandidates = try await service.queueRecommendationSnapshot()
            let choice = try XCTUnwrap(retainedCandidates?.candidates.first)
            let recommended = try await service.playQueueRecommendation(snapshotID: original.id, candidateID: choice.id)
            XCTAssertEqual(recommended.state.currentId, "entry-3")
            let requests = await owner.requests
            XCTAssertEqual(requests, [.songRecommendations(seedID: "100", offset: 0)])
            let active = try await service.queueRecommendationSnapshot(); XCTAssertTrue(try XCTUnwrap(active).active)
            let appended = try await service.changeQueue(.append([.init(kind: .queue, id: "entry-1")], next: false), expectedRevision: recommended.state.revision)
            _ = try await service.changeQueue(.append([.init(kind: .queue, id: "entry-1")], next: true), expectedRevision: appended.revision)
            let continued = try await service.queueRecommendationSnapshot()
            XCTAssertEqual(continued?.id, original.id); XCTAssertTrue(try XCTUnwrap(continued).active)
            await service.close()
        }
    }

    func testOrdinaryAppendBeforeActivationRevokesReadyOrInFlightCandidateOwnership() async throws {
        for inFlight in [false, true] {
            let owner = SimilarModeAccountFixture([["301", "302"]])
            let service = try await NativeMusicFixture.service(NativeMusicMemoryDocument(try document(["100"])), accounts: owner)
            let gate = NativeMusicGate()
            if inFlight { await owner.suspendNext(gate) }
            _ = try await startPlaying(service)
            if inFlight { await gate.started() } else { _ = try await service.previewQueueRecommendations() }
            let pending = await service.similarMode.queue?.pending
            let state = try await service.state()
            _ = try await service.changeQueue(.append([.init(kind: .queue, id: "entry-1")], next: false), expectedRevision: state.revision)
            if inFlight { await gate.release(); await pending?.value }
            let snapshot = try await service.queueRecommendationSnapshot()
            XCTAssertNil(snapshot)
            let queue = try await service.queue(); XCTAssertEqual(queue.entries.count, 2); XCTAssertEqual(queue.currentId, "entry-1")
            await service.close()
        }
    }

    func testSingleTailFetchOnlyPreparesCandidatesAndNaturalEndRepeatsSeed() async throws {
        let owner = SimilarModeAccountFixture([["301", "302"]])
        let service = try await NativeMusicFixture.service(NativeMusicMemoryDocument(try document(["100"])), accounts: owner)
        _ = try await service.updateSettings(.init(ifRevision: 0, mode: .single))
        let audio = try await startPlaying(service)
        let value = try await service.previewQueueRecommendations(), snapshot = try XCTUnwrap(value)
        XCTAssertEqual(snapshot.candidates.count, 2)
        let before = try await service.queue(); XCTAssertEqual(before.entries.count, 1)
        _ = try await service.recordSimilarPlaybackFact(fact(audio, .ended))
        let repeated = try await service.advance(.next, reason: .ended, expectedRevision: before.revision)
        XCTAssertEqual(repeated.selectedId, "entry-1")
        let retained = try await service.queueRecommendationSnapshot(); XCTAssertEqual(retained?.candidates.count, 2)
        await service.close()
    }

    func testSearchFirstCandidatesWaitForStartedInEveryOrdinaryMode() async throws {
        for mode in [MusicPlaybackMode.loop, .single, .shuffle] {
            let owner = SimilarModeAccountFixture([["301", "302"]])
            let service = NativeMusicService(document: NativeMusicMemoryDocument(try document(["100"])),
                accounts: owner, qq: NativeMusicQQFixture(), transport: NativeMusicNoNetwork(),
                clock: { NativeMusicFixture.date })
            try await service.setAccess(available: true, generation: 1)
            _ = try await service.updateSettings(.init(ifRevision: 0, mode: mode))
            let snapshot = try await service.queueRecommendationSnapshot()
            _ = try await service.previewQueueRecommendations()
            let before = await owner.count()
            XCTAssertEqual(before, 0, "opening a menu or resolving the queue is not Started")
            let audio = try await startPlaying(service)
            let candidates = try await service.previewQueueRecommendations()
            XCTAssertEqual(candidates?.candidates.count, 2)
            XCTAssertEqual(candidates?.id, snapshot?.id)
            let requests = await owner.requests
            XCTAssertEqual(requests, [.songRecommendations(seedID: "100", offset: 0)])
            _ = try await service.recordSimilarPlaybackFact(fact(audio, .paused))
            _ = try await service.recordSimilarPlaybackFact(fact(audio, .playing))
            _ = try await service.previewQueueRecommendations()
            let after = await owner.count()
            XCTAssertEqual(after, 1)
            let queue = try await service.queue()
            XCTAssertEqual(queue.entries.count, 1); XCTAssertEqual(queue.currentId, "entry-1")
            await service.close()
        }
    }

    func testOrdinarySearchCandidatesDoNotEnableContinuationAndSpecialSelectionUsesLoop() async throws {
        for mode in [MusicPlaybackMode.loop, .single, .shuffle] {
            let owner = SimilarModeAccountFixture([["301"]])
            let service = try await NativeMusicFixture.service(NativeMusicMemoryDocument(try document(["100"])), accounts: owner)
            _ = try await service.updateSettings(.init(ifRevision: 0, mode: mode))
            let initial = try await service.queueRecommendationSnapshot(), id = try XCTUnwrap(initial).id
            _ = try await startPlaying(service)
            let ready = try await service.previewQueueRecommendations()
            XCTAssertEqual(ready?.candidates.count, 1)
            XCTAssertFalse(try XCTUnwrap(ready).continueEnabled)
            let ordinary = try await service.queue()
            XCTAssertEqual(ordinary.entries.count, 1)
            try await service.setQueueRecommendationContinue(true, snapshotID: id)
            let selected = try await service.state(), special = try await service.queueRecommendationSnapshot()
            XCTAssertEqual(selected.mode, .loop)
            XCTAssertTrue(try XCTUnwrap(special).continueEnabled)
            XCTAssertEqual(selected.currentId, ordinary.currentId)
            XCTAssertEqual(selected.total, 2, "selecting the special mode appends ready candidates without navigating")
            let requests = await owner.count(); XCTAssertEqual(requests, 1)
            await service.close()
        }
    }

    func testShuffleOnlyDrawsForQueueNavigationAndNeverForPlaybackFacts() async throws {
        let owner = SimilarModeAccountFixture([["301", "302"]]), random = RecommendationRandom(0.75)
        let service = NativeMusicService(document: NativeMusicMemoryDocument(try document(["100", "200"])),
            accounts: owner, qq: NativeMusicQQFixture(), transport: NativeMusicNoNetwork(),
            clock: { NativeMusicFixture.date }, random: { random.draw() })
        try await service.setAccess(available: true, generation: 1)
        _ = try await service.startSimilarMode(.songRoaming(sourceIDs: ["100"], sourceName: "Seed"))
        let state = try await service.state()
        _ = try await service.updateSettings(.init(ifRevision: state.settingsRevision, mode: .shuffle))
        let first = try await startPlaying(service)
        let handoff = try await resolve(service, first.occurrenceId)
        for kind in [MusicPlaybackFact.Kind.playing, .paused, .playing, .progress, .ended] {
            _ = try await service.recordSimilarPlaybackFact(fact(handoff, kind))
        }
        _ = try await startPlaying(service)
        _ = try await service.state(); _ = try await service.queueRecommendationSnapshot()
        XCTAssertEqual(random.calls, 0, "playback facts and projections never draw random values")
        let beforeNext = try await service.state()
        let next = try await service.advance(.next, reason: .manual, expectedRevision: beforeNext.revision)
        XCTAssertNotEqual(next.selectedId, first.occurrenceId)
        XCTAssertEqual(random.calls, 1, "the random source belongs only to queue navigation")
        let requests = await owner.count(); XCTAssertEqual(requests, 1)
        await service.close()
    }

    @discardableResult private func startPlaying(_ service: NativeMusicService) async throws -> MusicPlaybackCandidate {
        let state = try await service.state()
        let candidate = try await resolve(service, XCTUnwrap(state.currentId))
        _ = try await service.recordSimilarPlaybackFact(fact(candidate, .playing))
        return candidate
    }
    private func preview(_ service: NativeMusicService) async throws -> MusicQueueRecommendationSnapshot? {
        let current = await service.attempt
        if current?.started != true { _ = try await startPlaying(service) }
        return try await service.previewQueueRecommendations()
    }

    private func document(_ ids: [String]) throws -> Data {
        var d = try MusicDocument(nil, runID: "fixture")
        d.queue["entries"] = .array(try ids.enumerated().map { index, id in
            let song = try MusicModel.normalize(SimilarModeAccountFixture.track(id))
            return .obj([("id", .string("entry-\(index + 1)")), ("key", song["songId"]), ("song", song),
                         ("context", .obj([("sourceType", .string("daily")), ("sourceTitle", .string("原始推荐"))]))])
        })
        d.queue["currentId"] = .string("entry-1"); d.queue["serial"] = .n(ids.count)
        d.queue["source"] = .obj([("sourceType", .string("search-song")), ("sourceTitle", .string("搜索")), ("seedEntryId", .string("entry-1"))])
        return try d.encode(changed: ["queue", "playback", "history"])
    }
    private func resolve(_ service: NativeMusicService, _ id: String) async throws -> MusicPlaybackCandidate {
        let options = (try await service.sourceAvailability(for: .init(kind: .queue, id: id))).options
        return try await service.resolve(attemptId: UUID().uuidString.lowercased(), occurrenceId: id,
            selection: XCTUnwrap(options.first).selection(quality: "128k"))
    }
    private func fact(_ candidate: MusicPlaybackCandidate, _ kind: MusicPlaybackFact.Kind) -> MusicPlaybackFact {
        .init(attemptId: candidate.attemptId, occurrenceId: candidate.occurrenceId,
              resourceId: candidate.resourceId, kind: kind, position: kind == .ended ? 180 : 1)
    }
}

private final class RecommendationRandom: @unchecked Sendable {
    private let lock = NSLock()
    private let value: Double
    private var count = 0
    init(_ value: Double) { self.value = value }
    func draw() -> Double { lock.lock(); defer { lock.unlock() }; count += 1; return value }
    var calls: Int { lock.lock(); defer { lock.unlock() }; return count }
}
