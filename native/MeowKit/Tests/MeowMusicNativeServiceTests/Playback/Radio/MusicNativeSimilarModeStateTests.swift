import XCTest
import MeowMusicFeature
@testable import MeowMusicNativeService

final class MusicNativeSimilarModeStateTests: XCTestCase {
    func testSimilarRestoreAndTailAppendKeepLargeQueueReturnQueueBatchesAndAttempts() async throws {
        var document = try MusicDocument(fixtureDocument(count: 2001), runID: "fixture")
        let original = document.queue["entries"].array
        let seed = original[0]["song"], returning = MusicNativeSimilarData.queueSnapshot(document.queue)
        document.queue["currentId"] = original.last?["id"] ?? .null
        document.queue["serial"] = .n(10_000)
        document.queue["similar"] = .obj([
            ("schema", .n(2)), ("seed", seed), ("seedEntryId", original[0]["id"]), ("returnQueue", returning),
            ("sourceIds", .strings(["100"])), ("fromHome", .bool(false)), ("provider", .string("heartbeat")),
            ("providerContext", .null), ("continue", .bool(true)), ("scope", .string("synthetic-scope")),
            ("status", .string("ready")),
            ("batches", .array(original.enumerated().map {
                MusicNativeSimilarData.batch("batch-\($0.offset)", basedOn: seed, provider: .heartbeat, rows: [$0.element])
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
        XCTAssertEqual(queue.entries.count, 2004)
        XCTAssertEqual(queue.entries.prefix(2001).map(\.id), original.map { $0["id"].string })
        XCTAssertEqual(queue.entries.suffix(3).compactMap { $0.track.sources.first?.nativeId }, ["301", "302", "303"])
        XCTAssertEqual(saved.queue["similar"]["batches"].array.count, 2002)
        XCTAssertEqual(saved.queue["similar"]["attempted"].array.count, 2002)
        let requests = await owner.requests
        XCTAssertEqual(requests, [.queueRecommendations(songIDs: ["100"] + Array(repeating: "200", count: 2000),
                                                       currentPlaySongID: "200")])
        await service.close()
        let cold = try await NativeMusicFixture.service(store, accounts: owner)
        let restoredValue = try await cold.similarModeSnapshot(), restored = try XCTUnwrap(restoredValue)
        XCTAssertTrue(restored.active); XCTAssertEqual(restored.batches.count, 2002)
        let exited = try await cold.exitSimilarMode(), returningQueue = try await cold.queue()
        XCTAssertEqual(exited.state.total, 2001)
        XCTAssertEqual(returningQueue.entries.map(\.id), original.map { $0["id"].string })
        XCTAssertEqual(returningQueue.currentId, returning["currentId"].string)
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

    func testPreviewEnterAndSeedExitPreserveMediaOwnerAndReturnQueue() async throws {
        let owner = SimilarModeAccountFixture([["100", "201", "201", "202"]])
        let store = NativeMusicMemoryDocument(try fixtureDocument())
        let service = try await NativeMusicFixture.service(store, accounts: owner)
        let candidate = try await resolve(service, id: "entry-1")
        _ = try await service.recordSimilarPlaybackFact(fact(candidate, .playing))
        let before = try await service.queue()
        let previewValue = try await service.previewSimilarMode()
        let preview = try XCTUnwrap(previewValue)
        XCTAssertFalse(preview.active); XCTAssertEqual(preview.candidates.count, 2)
        let entered = try await service.enterSimilarMode()
        XCTAssertEqual(entered.action, .keepCurrent); XCTAssertEqual(entered.state.currentId, "entry-1")
        let attempt = await service.attempt?.id
        XCTAssertEqual(attempt, candidate.attemptId)
        let liveValue = try await service.similarModeSnapshot()
        let live = try XCTUnwrap(liveValue)
        XCTAssertTrue(live.active); XCTAssertEqual(live.batches.count, 1)
        let exited = try await service.exitSimilarMode()
        XCTAssertEqual(exited.action, .keepCurrent)
        let restored = try await service.queue(), retained = await service.attempt?.id
        XCTAssertEqual(restored.entries.map(\.id), before.entries.map(\.id))
        XCTAssertEqual(retained, candidate.attemptId)
        let requests = await owner.requests
        XCTAssertEqual(requests, [.heartbeat(lastID: "100", fromHome: false, context: nil)])
    }

    func testActualTailPlayingStartsOnlyOneBatchAndEndedWaitsForNewOccurrences() async throws {
        let owner = SimilarModeAccountFixture([["201", "202"], ["202", "301", "302", "303", "304"]])
        let store = NativeMusicMemoryDocument(try fixtureDocument())
        let controlledStore = SimilarModeControlledDocument(store)
        let service = NativeMusicService(document: controlledStore, accounts: owner,
            qq: NativeMusicQQFixture(), transport: NativeMusicNoNetwork(), clock: { NativeMusicFixture.date })
        try await service.setAccess(available: true, generation: 1)
        _ = try await service.startSimilarMode(.heartbeat)
        let queue = try await service.queue(), tail = try XCTUnwrap(queue.entries.last)
        _ = try await service.changeQueue(.select(tail.id), expectedRevision: queue.revision)
        let candidate = try await resolve(service, id: tail.id)
        let beforePlaying = await owner.count()
        XCTAssertEqual(beforePlaying, 1, "resolve/select aren't playback facts")
        let gate = NativeMusicGate()
        await owner.suspendNext(gate)
        _ = try await service.recordSimilarPlaybackFact(fact(candidate, .playing))
        await gate.started()
        _ = try await service.recordSimilarPlaybackFact(fact(candidate, .progress))
        let afterPlaying = await owner.count()
        XCTAssertEqual(afterPlaying, 2)
        _ = try await service.recordSimilarPlaybackFact(fact(candidate, .ended))
        let state = try await service.state()
        // Release only once advance has entered its queue read. Releasing from
        // an unrelated task could append before advance starts and test a stale
        // revision instead of the pending-tail boundary.
        controlledStore.afterNextRead { Task { await gate.release() } }
        let result = try await service.advance(.next, reason: .ended, expectedRevision: state.revision)
        let after = try await service.queue()
        XCTAssertEqual(after.entries.count, 5, "tail adds at most three fresh songs")
        XCTAssertEqual(result.selectedId, after.entries[2].id)
        let requests = await owner.requests
        XCTAssertEqual(requests.last, .queueRecommendations(songIDs: ["201", "202"], currentPlaySongID: "202"))
        let snapshotValue = try await service.similarModeSnapshot()
        let snapshot = try XCTUnwrap(snapshotValue)
        XCTAssertEqual(snapshot.batches.last?.provider, .queueRecommendations)
        XCTAssertFalse(snapshot.canRetry)
    }

    func testExplicitContinueCancellationAndAccountRevocationRejectLateBatches() async throws {
        let owner = SimilarModeAccountFixture([["201"], ["301"], ["401"]])
        let service = try await NativeMusicFixture.service(NativeMusicMemoryDocument(try fixtureDocument()), accounts: owner)
        _ = try await service.startSimilarMode(.songRoaming(sourceIDs: ["100"], sourceName: "Seed"))
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
        let revokedGate = NativeMusicGate(); await owner.suspendNext(revokedGate)
        try await service.setSimilarContinue(true, snapshotID: active.id)
        await revokedGate.started()
        await owner.revoke()
        await revokedGate.release()
        let snapshot = try await service.similarModeSnapshot()
        XCTAssertNil(snapshot)
        let final = try await service.queue()
        XCTAssertEqual(final.entries.count, 1)
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
        XCTAssertEqual(exited.action, .restoreOnly)
        let state = try await service.changeQueue(.replace([.init(kind: .queue, id: "entry-1")], index: 0),
                                                   expectedRevision: exited.state.revision)
        XCTAssertEqual(state.total, 1)
        let generic = try await service.similarModeSnapshot()
        XCTAssertNil(generic, "non-search .replace must not acquire automatic roaming")
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
    }

    private func fixtureDocument(count: Int = 2) throws -> Data {
        var d = try MusicDocument(nil, runID: "fixture")
        d.queue["entries"] = .array(try (0..<count).map { index in
            let id = index == 0 ? "100" : "200"
            let song = try MusicModel.normalize(SimilarModeAccountFixture.track(id))
            return .obj([("id", .string("entry-\(index + 1)")), ("key", song["songId"]),
                         ("song", song), ("context", .obj())])
        })
        d.queue["currentId"] = .string("entry-1"); d.queue["serial"] = .n(count)
        return try d.encode(changed: ["queue", "history", "playback"])
    }
    private func resolve(_ service: NativeMusicService, id: String) async throws -> MusicPlaybackCandidate {
        let options = try await service.playbackOptions(for: .init(kind: .queue, id: id))
        return try await service.resolve(attemptId: UUID().uuidString.lowercased(), occurrenceId: id,
            selection: XCTUnwrap(options.first).selection(quality: "128k"))
    }
    private func fact(_ candidate: MusicPlaybackCandidate, _ kind: MusicPlaybackFact.Kind) -> MusicPlaybackFact {
        .init(attemptId: candidate.attemptId, occurrenceId: candidate.occurrenceId,
              resourceId: candidate.resourceId, kind: kind, position: kind == .ended ? 180 : 1)
    }
}

private actor SimilarModeAccountFixture: MusicNativeAccountProviding, MusicNativeSimilarModeProviding {
    var requests: [MusicNativeSimilarModeRequest] = []
    var batches: [[String]]
    var nextGate: NativeMusicGate?
    var leases: [UUID: MusicAccountQueueLease] = [:]
    init(_ batches: [[String]]) { self.batches = batches }
    nonisolated static func track(_ id: String) -> MusicNativeTrackData {
        .init(platform: "wy", data: MJ.obj([("songmid", .string(id)),
            ("name", .string("Song " + id)), ("singer", .string("Synthetic")),
            ("albumName", .string("Album")), ("duration", .n(180)),
            ("types", .array([.obj([("type", .string("128k"))])]))]).bytes)
    }
    func count() -> Int { requests.count }
    func suspendNext(_ gate: NativeMusicGate) { nextGate = gate }
    func revoke() { for lease in leases.values { lease.revoke() } }
    func beginAccountQueue(platform: MusicAccountPlatform) -> MusicAccountQueueLease {
        let lease = MusicAccountQueueLease(platform: platform); leases[lease.id] = lease; return lease
    }
    func checkAccountQueue(_ lease: MusicAccountQueueLease) throws {
        guard leases[lease.id] === lease else { throw MusicServiceError.staleAccess }
        try lease.authorization.check()
    }
    func endAccountQueue(_ lease: MusicAccountQueueLease) { leases.removeValue(forKey: lease.id)?.revoke() }
    func similarModeScope(_ lease: MusicAccountQueueLease) throws -> String {
        try checkAccountQueue(lease); return "synthetic-scope"
    }
    func similarModeBatch(_ lease: MusicAccountQueueLease, request: MusicNativeSimilarModeRequest) async throws -> MusicNativeSimilarModeBatch {
        try checkAccountQueue(lease); requests.append(request)
        let ids = batches.isEmpty ? [] : batches.removeFirst(), gate = nextGate; nextGate = nil
        if let gate { await gate.wait() }
        // Deliberately noncooperative response: NativeMusicService's lease and
        // request identity must still reject this if it was revoked/cancelled.
        return .init(items: ids.map(Self.track), heartbeatContext: .init(playlistID: "900", seedID: "100"))
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
        .init(url: URL(string: "https://m701.music.126.net/synthetic.mp3")!)
    }
}

/// Deterministic read/commit boundaries, without timers or scheduling guesses.
private final class SimilarModeControlledDocument: MusicNativeDocumentStoring, @unchecked Sendable {
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
