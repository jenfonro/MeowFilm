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
        let before = store.snapshot().0, writes = store.snapshot().2
        let otherEntrance = try MusicModel.normalize(MusicNativeCandidateDiscoveryFixture.track(
            "tx", id: "OtherMID", title: "Song 301", artist: "Synthetic"))
        let unknown = await service.cachedSourceDiscoveryState(otherEntrance)
        XCTAssertTrue(unknown.entries.isEmpty)

        let previewValue = try await service.previewQueueRecommendations(), preview = try XCTUnwrap(previewValue)
        XCTAssertEqual(preview.candidates.map(\.song.title), ["Song 301"])
        let acquired = await service.cachedSourceDiscoveryState(otherEntrance)
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
        let unchangedEvidence = await service.cachedSourceDiscoveryState(otherEntrance)
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

    func testPreviewIsReadOnlyAndClickAppendsCompleteNativeUniqueBatchWithoutChangingSource() async throws {
        let ids = ["100"] + Array(repeating: "200", count: 23)
        let owner = SimilarModeAccountFixture([["200", "301", "301", "302", "303", "304", "305"]],
            metadata: true, names: ["200": "Changed old metadata", "301": "Same metadata", "302": "Same metadata"])
        let store = NativeMusicMemoryDocument(try document(ids))
        let service = try await NativeMusicFixture.service(store, accounts: owner)
        let before = try await service.queue(), bytes = store.snapshot().0
        let snapshotValue = try await service.previewQueueRecommendations()
        let snapshot = try XCTUnwrap(snapshotValue)
        XCTAssertEqual(snapshot.candidates.count, 5)
        XCTAssertFalse(snapshot.continueEnabled)
        XCTAssertEqual(snapshot.currentEntryID, "entry-1")
        XCTAssertEqual(store.snapshot().0, bytes)
        XCTAssertEqual(store.snapshot().2, 0)
        let unchanged = try await service.queue(), mode = try await service.similarModeSnapshot()
        XCTAssertEqual(unchanged.entries.map(\.id), before.entries.map(\.id))
        XCTAssertEqual(unchanged.currentId, before.currentId)
        XCTAssertNil(mode)
        let requests = await owner.requests
        XCTAssertEqual(requests, [.queueRecommendations(songIDs: ids, currentPlaySongID: "100")])
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
        XCTAssertEqual(saved.queue["source"], original.queue["source"])
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

    func testMixedOrMissingNativeIDRejectsTheEntireQueueWithoutOpeningAnAccount() async throws {
        for missing in [false, true] {
            var d = try MusicDocument(document(["100", "200"]), runID: "fixture")
            var rows = d.queue["entries"].array
            if missing { rows[1]["song"]["sources"] = .array([]) }
            else { rows[1]["song"] = try NativeMusicFixture.song() }
            d.queue["entries"] = .array(rows)
            let bytes = try d.encode(changed: ["queue", "playback", "history"])
            let store = NativeMusicMemoryDocument(bytes), owner = SimilarModeAccountFixture([["301"]])
            let service = try await NativeMusicFixture.service(store, accounts: owner)
            let snapshot = try await service.queueRecommendationSnapshot()
            let preview = try await service.previewQueueRecommendations()
            XCTAssertNil(snapshot); XCTAssertNil(preview)
            let openings = await owner.openings, requests = await owner.count()
            XCTAssertEqual(openings, 0); XCTAssertEqual(requests, 0)
            XCTAssertEqual(store.snapshot().0, bytes); XCTAssertEqual(store.snapshot().2, 0)
            await service.close()
        }
    }

    func testContinueRequiresRealTailFactAndFetchesOneCompleteBatchAtEachNewTail() async throws {
        let owner = SimilarModeAccountFixture([["901"], ["301", "302", "303", "304"], ["401", "402"]],
                                              hasMore: [nil, true, false])
        let service = try await NativeMusicFixture.service(NativeMusicMemoryDocument(try document(["100", "200"])),
                                                           accounts: owner)
        let previewValue = try await service.previewQueueRecommendations()
        let preview = try XCTUnwrap(previewValue)
        let first = try await resolve(service, "entry-1")
        _ = try await service.recordSimilarPlaybackFact(fact(first, .playing))
        try await service.setQueueRecommendationContinue(true, snapshotID: preview.id)
        let noAppend = try await service.queue()
        XCTAssertEqual(noAppend.entries.count, 2, "an enabled preview still cannot append away from the actual tail")
        _ = try await service.changeQueue(.select("entry-2"), expectedRevision: noAppend.revision)
        let tail = try await resolve(service, "entry-2"), before = await owner.count()
        XCTAssertEqual(before, 1, "selection and resolution never trigger a batch")
        let gate = NativeMusicGate(); await owner.suspendNext(gate)
        _ = try await service.recordSimilarPlaybackFact(fact(tail, .playing))
        await gate.started()
        let pending = await service.similarMode.queue?.pending
        _ = try await service.recordSimilarPlaybackFact(fact(tail, .progress))
        let single = await owner.count()
        XCTAssertEqual(single, 2)
        await gate.release(); await pending?.value
        let appended = try await service.queue()
        XCTAssertEqual(appended.entries.count, 6)
        XCTAssertEqual(appended.currentId, "entry-2", "background append never chooses a new occurrence")
        let activeAttempt = await service.attempt?.id
        XCTAssertEqual(activeAttempt, tail.attemptId)
        let newTail = try XCTUnwrap(appended.entries.last)
        _ = try await service.changeQueue(.select(newTail.id), expectedRevision: appended.revision)
        let second = try await resolve(service, newTail.id)
        _ = try await service.recordSimilarPlaybackFact(fact(second, .playing))
        let secondPending = await service.similarMode.queue?.pending
        await secondPending?.value
        let complete = try await service.queue()
        XCTAssertEqual(complete.entries.count, 8)
        XCTAssertEqual(complete.currentId, newTail.id)
        let finalTail = try XCTUnwrap(complete.entries.last)
        _ = try await service.changeQueue(.select(finalTail.id), expectedRevision: complete.revision)
        let final = try await resolve(service, finalTail.id)
        _ = try await service.recordSimilarPlaybackFact(fact(final, .playing))
        _ = try await service.recordSimilarPlaybackFact(fact(final, .progress))
        let terminalValue = try await service.queueRecommendationSnapshot(), terminal = try XCTUnwrap(terminalValue)
        XCTAssertTrue(terminal.exhausted)
        let requests = await owner.requests
        XCTAssertEqual(requests, [
            .queueRecommendations(songIDs: ["100", "200"], currentPlaySongID: "100"),
            .queueRecommendations(songIDs: ["100", "200"], currentPlaySongID: "200"),
            .queueRecommendations(songIDs: ["100", "200", "301", "302", "303", "304"], currentPlaySongID: "304")
        ])
        await service.close()
    }

    func testNextAndEndedAwaitPendingBatchAndPauseFencesLateNavigation() async throws {
        for ended in [false, true] {
            for paused in [false, true] {
                let owner = SimilarModeAccountFixture([["301", "302", "303", "304"]])
                let backing = NativeMusicMemoryDocument(try document(["100"]))
                let controlled = SimilarModeControlledDocument(backing)
                let service = NativeMusicService(document: controlled, accounts: owner,
                    qq: NativeMusicQQFixture(), transport: NativeMusicNoNetwork(), clock: { NativeMusicFixture.date })
                try await service.setAccess(available: true, generation: 1)
                let snapshotValue = try await service.queueRecommendationSnapshot()
                try await service.setQueueRecommendationContinue(true, snapshotID: XCTUnwrap(snapshotValue).id)
                let tail = try await resolve(service, "entry-1")
                let gate = NativeMusicGate(); await owner.suspendNext(gate)
                _ = try await service.recordSimilarPlaybackFact(fact(tail, .playing))
                await gate.started()
                if ended { _ = try await service.recordSimilarPlaybackFact(fact(tail, .ended)) }
                let state = try await service.state()
                XCTAssertTrue(state.canNext, "Next must remain reachable while it waits for real pending work")
                XCTAssertNil(state.nextPreviewID, "an uncommitted candidate is not a next occurrence")
                controlled.afterNextRead {
                    Task {
                        if paused { await service.pauseSimilarNavigation() }
                        await gate.release()
                    }
                }
                do {
                    let advanced = try await service.advance(.next, reason: ended ? .ended : .manual,
                                                              expectedRevision: state.revision)
                    XCTAssertFalse(paused, "pause must reject delayed start after the pending wait")
                    XCTAssertEqual(advanced.selectedId, "entry-2")
                } catch {
                    XCTAssertTrue(paused)
                    XCTAssertEqual(error as? MusicServiceError, .cancelled)
                }
                let queue = try await service.queue()
                XCTAssertEqual(queue.entries.count, 5)
                XCTAssertEqual(queue.currentId, paused ? "entry-1" : "entry-2")
                await service.close()
            }
        }
    }

    func testDisableCancelsUncommittedWorkAndRetainsItUntilPhysicalDrain() async throws {
        let owner = SimilarModeAccountFixture([["301"], ["401"]])
        let store = NativeMusicMemoryDocument(try document(["100"]))
        let service = try await NativeMusicFixture.service(store, accounts: owner)
        let snapshotValue = try await service.queueRecommendationSnapshot(), snapshot = try XCTUnwrap(snapshotValue)
        try await service.setQueueRecommendationContinue(true, snapshotID: snapshot.id)
        let tail = try await resolve(service, "entry-1"), gate = NativeMusicGate()
        await owner.suspendNext(gate)
        _ = try await service.recordSimilarPlaybackFact(fact(tail, .playing))
        await gate.started()
        let writesBeforeCancel = store.snapshot().2
        let pending = await service.similarMode.queue?.pending
        try await service.setQueueRecommendationContinue(false, snapshotID: snapshot.id)
        let retained = await service.similarMode.queue?.pending
        XCTAssertNotNil(retained, "logical cancellation must keep the physical owner until the adapter returns")
        let active = await owner.activeRequests
        XCTAssertEqual(active, 1)
        let disabledValue = try await service.queueRecommendationSnapshot(), disabled = try XCTUnwrap(disabledValue)
        XCTAssertFalse(disabled.continueEnabled)
        try await service.setQueueRecommendationContinue(true, snapshotID: disabled.id)
        let stillSingle = await owner.count()
        XCTAssertEqual(stillSingle, 1, "reenabling cannot overlap a retiring request")
        await gate.release(); await pending?.value
        let unchanged = try await service.queue()
        XCTAssertEqual(unchanged.entries.count, 1); XCTAssertEqual(store.snapshot().2, writesBeforeCancel)
        _ = try await service.recordSimilarPlaybackFact(fact(tail, .progress))
        let second = await service.similarMode.queue?.pending
        await second?.value
        let appended = try await service.queue()
        XCTAssertEqual(appended.entries.count, 2)
        XCTAssertEqual(appended.entries.last?.track.sources.first?.nativeId, "401")
        let enabledValue = try await service.queueRecommendationSnapshot()
        try await service.setQueueRecommendationContinue(false, snapshotID: XCTUnwrap(enabledValue).id)
        let kept = try await service.queue()
        XCTAssertEqual(kept.entries.map(\.id), appended.entries.map(\.id), "disable preserves committed rows")
        await service.close()
    }

    func testEmptyDuplicateAndHasMoreFalseStopAutomaticFetchingButCanExplicitlyRetry() async throws {
        for ids in [[], ["100", "100"], ["301", "302", "303", "304"]] {
            let owner = SimilarModeAccountFixture([ids, ["401"]],
                                                  hasMore: [ids.first == "301" ? false : nil, true])
            let service = try await NativeMusicFixture.service(NativeMusicMemoryDocument(try document(["100"])),
                                                               accounts: owner)
            let initialValue = try await service.queueRecommendationSnapshot(), initial = try XCTUnwrap(initialValue)
            try await service.setQueueRecommendationContinue(true, snapshotID: initial.id)
            let tail = try await resolve(service, "entry-1")
            _ = try await service.recordSimilarPlaybackFact(fact(tail, .playing))
            let pending = await service.similarMode.queue?.pending
            await pending?.value
            let firstQueue = try await service.queue()
            XCTAssertEqual(firstQueue.entries.count, ids.first == "301" ? 5 : 1)
            _ = try await service.recordSimilarPlaybackFact(fact(tail, .progress))
            let requestCount = await owner.count()
            XCTAssertEqual(requestCount, 1)
            let terminalValue = try await service.queueRecommendationSnapshot(), terminal = try XCTUnwrap(terminalValue)
            XCTAssertTrue(terminal.exhausted); XCTAssertTrue(terminal.canRetry)
            _ = try await service.retryQueueRecommendations(snapshotID: terminal.id)
            let retried = try await service.queue(), retryValue = try await service.queueRecommendationSnapshot()
            if firstQueue.entries.count == 1 {
                XCTAssertEqual(retried.entries.count, 2)
                XCTAssertEqual(retried.currentId, "entry-1")
            } else {
                XCTAssertEqual(retried.entries.count, 5, "retry away from the real tail remains a preview")
                XCTAssertEqual(try XCTUnwrap(retryValue).candidates.count, 1)
            }
            await service.close()
        }
    }

    func testFailedPreviewRetainsQueueAndExplicitRetryReturnsCandidates() async throws {
        let owner = SimilarModeAccountFixture([[], ["301", "302", "303", "304"]])
        await owner.failNext(.upstreamFailed)
        let store = NativeMusicMemoryDocument(try document(["100", "200"]))
        let service = try await NativeMusicFixture.service(store, accounts: owner)
        let bytes = store.snapshot().0
        let failedValue = try await service.previewQueueRecommendations(), failed = try XCTUnwrap(failedValue)
        XCTAssertEqual(failed.status, .error); XCTAssertEqual(failed.error, .upstreamFailed)
        XCTAssertTrue(failed.canRetry)
        _ = try await service.retryQueueRecommendations(snapshotID: failed.id)
        let retryValue = try await service.queueRecommendationSnapshot(), retry = try XCTUnwrap(retryValue)
        XCTAssertEqual(retry.candidates.count, 4)
        XCTAssertEqual(store.snapshot().0, bytes); XCTAssertEqual(store.snapshot().2, 0)
        XCTAssertFalse(retry.continueEnabled)
        await service.close()
    }

    func testCancelReadOnlyPreviewDrainsLeaseAndRejectsLateCandidatesWithoutQueueWrites() async throws {
        let owner = SimilarModeAccountFixture([["301", "302"]])
        let store = NativeMusicMemoryDocument(try document(["100"]))
        let service = try await NativeMusicFixture.service(store, accounts: owner)
        let bytes = store.snapshot().0, gate = NativeMusicGate()
        await owner.suspendNext(gate)
        let loading = Task { try await service.previewQueueRecommendations() }
        await gate.started()
        let leaseValue = await service.similarMode.queue?.lease, lease = try XCTUnwrap(leaseValue)
        await service.cancelQueueRecommendationPreview()
        XCTAssertFalse(lease.authorization.isActive)
        let active = await owner.activeRequests, retiring = await service.similarMode.retiring.count
        XCTAssertEqual(active, 1); XCTAssertGreaterThan(retiring, 0)
        await gate.release()
        do { _ = try await loading.value; XCTFail("cancelled candidate token") }
        catch { XCTAssertEqual(error as? MusicServiceError, .targetChanged) }
        await service.drainSimilarWork()
        let ended = await owner.endings, pending = await owner.activeRequests
        XCTAssertEqual(ended, 1); XCTAssertEqual(pending, 0)
        XCTAssertEqual(store.snapshot().0, bytes); XCTAssertEqual(store.snapshot().2, 0)
        await service.close()
    }

    func testSelectionReorderReplacementAndAccountRevocationRejectSuspendedPreview() async throws {
        for change in ["select", "reorder", "replace", "account"] {
            let owner = SimilarModeAccountFixture([["301", "302"]], metadata: true)
            let store = NativeMusicMemoryDocument(try document(["100", "200"]))
            let service = try await NativeMusicFixture.service(store, accounts: owner)
            let gate = NativeMusicGate(); await owner.suspendNext(gate)
            let loading = Task { try await service.previewQueueRecommendations() }
            await gate.started()
            let oldValue = try await service.queueRecommendationSnapshot(), old = try XCTUnwrap(oldValue)
            let queue = try await service.queue()
            switch change {
            case "select": _ = try await service.changeQueue(.select("entry-2"), expectedRevision: queue.revision)
            case "reorder":
                _ = try await service.changeQueue(.reorder(["entry-2", "entry-1"]), expectedRevision: queue.revision)
            case "replace":
                _ = try await service.changeQueue(.replace([.init(kind: .queue, id: "entry-1")], index: 0),
                                                   expectedRevision: queue.revision)
            default: await owner.revoke()
            }
            let writes = store.snapshot().2
            await gate.release()
            do { _ = try await loading.value; XCTFail("superseded preview") }
            catch { XCTAssertEqual(error as? MusicServiceError, .targetChanged) }
            let currentValue = try await service.queueRecommendationSnapshot()
            if let current = currentValue {
                XCTAssertTrue(current.candidates.isEmpty)
                XCTAssertNotEqual(current.id, old.id)
            }
            XCTAssertEqual(store.snapshot().2, writes)
            let noProof = await service.liveRecommendation(entryID: "entry-1")
            XCTAssertNil(noProof)
            await service.close()
        }
    }

    func testCandidateCASConflictAndUnknownCommitNeverRollBackOrPublishProof() async throws {
        let owner = SimilarModeAccountFixture([["301", "302", "303", "304"]], metadata: true)
        let store = NativeMusicMemoryDocument(try document(["100", "200"]))
        let controlled = SimilarModeControlledDocument(store)
        let service = NativeMusicService(document: controlled, accounts: owner,
            qq: NativeMusicQQFixture(), transport: NativeMusicNoNetwork(), clock: { NativeMusicFixture.date })
        try await service.setAccess(available: true, generation: 1)
        let previewValue = try await service.previewQueueRecommendations(), preview = try XCTUnwrap(previewValue)
        let before = store.snapshot().0
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
        XCTAssertEqual(saved.queue["entries"].array.count, 6)
        XCTAssertEqual(saved.queue["currentId"].string, "entry-5")
        let proof = await service.liveRecommendation(entryID: "entry-5")
        XCTAssertNil(proof)
        let state = try await service.state()
        XCTAssertEqual(state.total, 6)
        XCTAssertEqual(store.snapshot().2, 1, "no recovery write can overwrite the uncertain commit")
        await service.close()
    }

    func testCloseRevokesProofAndWaitsForRecommendationWorkerToPhysicallyFinish() async throws {
        let owner = SimilarModeAccountFixture([["301"]])
        let service = try await NativeMusicFixture.service(NativeMusicMemoryDocument(try document(["100"])), accounts: owner)
        let gate = NativeMusicGate(); await owner.suspendNext(gate)
        let loading = Task { try await service.previewQueueRecommendations() }
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

    private func document(_ ids: [String]) throws -> Data {
        var d = try MusicDocument(nil, runID: "fixture")
        d.queue["entries"] = .array(try ids.enumerated().map { index, id in
            let song = try MusicModel.normalize(SimilarModeAccountFixture.track(id))
            return .obj([("id", .string("entry-\(index + 1)")), ("key", song["songId"]), ("song", song),
                         ("context", .obj([("sourceType", .string("daily")), ("sourceTitle", .string("原始推荐"))]))])
        })
        d.queue["currentId"] = .string("entry-1"); d.queue["serial"] = .n(ids.count)
        d.queue["source"] = .obj([("id", .string("original-list")), ("title", .string("原始推荐"))])
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
