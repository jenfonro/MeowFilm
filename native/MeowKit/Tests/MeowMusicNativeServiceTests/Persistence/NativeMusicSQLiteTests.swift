import XCTest
import MeowMusicFeature
import MeowRuntime
import MeowStorage
import MeowStorageTestSupport
@testable import MeowMusicNativeService

final class NativeMusicSQLiteTests: XCTestCase {
    private func location() throws -> URL {
        FileManager.default.temporaryDirectory.resolvingSymlinksInPath()
            .appendingPathComponent("music-sqlite-test-" + UUID().uuidString, isDirectory: true)
    }
    private func song(_ index: Int, platforms: [String] = MusicModel.sites) throws -> MJ {
        let tracks = try platforms.map { platform in
            try MusicModel.normalize(.init(platform: platform, data: MJ.obj([
                ("songmid", .string("synthetic-\(index)")), ("name", .string("Track \(index)")),
                ("singer", .string("Fixture")), ("duration", .n(180)),
                ("hash", .string(platform == "kg" ? "hash-\(index)" : ""))]).bytes))
        }
        return try MusicModel.mergeSources(tracks[0], adding: tracks.dropFirst().flatMap { $0["sources"].array })
    }
    func testFreshFixtureReadsNativeStateWithoutWriting() async throws {
        let store = NativeMusicTestLibrary()
        let service = try await NativeMusicFixture.service(store)
        let state = try await service.state()
        XCTAssertNil(state.currentId)
        XCTAssertEqual(try store.readSnapshot().revision, 0)
        XCTAssertEqual(try store.allSongIDs(), [])
        XCTAssertEqual(store.snapshot().2, 0)
        await service.close()
    }
    private func database(_ root: URL) throws -> NativeMusicSQLiteFixture {
        NativeMusicSQLiteFixture(try storageTestDatabase(self, root: root))
    }
    func testCurrentAppDatabaseReopensCommittedStateAndBorrowedCloseDoesNotCloseIt() async throws {
        let root = try location(), database = try database(root)
        XCTAssertEqual(try database.readSnapshot().revision, 0)
        XCTAssertTrue(FileManager.default.fileExists(atPath: database.storage.url.path))
        try database.withTransaction { try $0.saveArea("playback", value: try MusicModel.playback.storageValue) }
        let committed = try database.readSnapshot()
        XCTAssertEqual(committed.revision, 1)
        try database.close()
        XCTAssertThrowsError(try database.readSnapshot()) { XCTAssertEqual($0 as? MusicServiceError, .closed) }
        let shared = try await database.storage.read { try $0.query("SELECT revision FROM music_owner_state").first!.integer("revision") }
        XCTAssertEqual(shared, 1)
        try await database.storage.close()
        let connection = try storageTestOpen(root)
        addTeardownBlock { try await connection.close() }
        let reopened = NativeMusicSQLiteFixture(connection)
        XCTAssertEqual(try reopened.readSnapshot().identity, committed.identity)
        XCTAssertEqual(try MJ.parse(XCTUnwrap(reopened.readSnapshot().areas["playback"])).stable, MusicModel.playback.stable)
    }
    func testTenThousandMultiPlatformSongsUseIndexedHotReadsAndOnlyChangedRows() async throws {
        let root = try location(), database = try database(root)
        let measured = MusicSQLiteMeasuredStore(database)
        let service = NativeMusicService(database: measured, platformClient: try NativeMusicFixture.client())
        try await service.setAccess(available: true, generation: 1)
        let clock = ContinuousClock()
        var mark = clock.now
        func elapsed(_ phase: String) {
            let now = clock.now
            print("TenThousandSongs \(phase) \(mark.duration(to: now))")
            mark = now
        }
        let input = try (0..<10_000).map { try song($0) }
        elapsed("prepare-five-source-input")
        let adopted = try await service.adoptSongs(input, authoritative: true)
        elapsed("adopt-and-project")
        XCTAssertEqual(adopted.count, 10_000)
        XCTAssertEqual(try database.allSongIDs().count, 10_000)
        var counts = measured.counts()
        XCTAssertEqual(counts.candidateBatches, 1)
        XCTAssertEqual(counts.sourceOwnerBatches, 1)
        let revision = try database.readSnapshot().revision
        let wal = URL(fileURLWithPath: database.storage.url.path + "-wal")
        let walBytes = try FileManager.default.attributesOfItem(atPath: wal.path)[.size] as? UInt64
        measured.reset()
        mark = clock.now
        let repeated = try await service.adoptSongs(Array(input.prefix(10)), authoritative: true)
        elapsed("repeat-ten")
        XCTAssertEqual(repeated.map { $0["songId"] }, Array(adopted.prefix(10)).map { $0["songId"] })
        XCTAssertEqual(try database.readSnapshot().revision, revision)
        XCTAssertEqual(try FileManager.default.attributesOfItem(atPath: wal.path)[.size] as? UInt64, walBytes)
        counts = measured.counts()
        XCTAssertEqual(counts.enumerations, 0)
        XCTAssertEqual(counts.candidates, 0)
        XCTAssertEqual(counts.saved, 0)
        measured.reset()
        mark = clock.now
        _ = try await service.repositoryTracks(songIDs: Set(adopted.prefix(10).map { $0["songId"].string }))
        elapsed("read-ten")
        counts = measured.counts()
        XCTAssertEqual(counts.songReads, 10)
        XCTAssertEqual(counts.enumerations, 0)
        XCTAssertEqual(counts.candidates, 0)
        measured.reset()
        let updated = Array(input.prefix(3)).map { raw -> MJ in
            var value = raw; value["album"] = .string("New album")
            return value
        }
        _ = try await service.adoptSongs(updated, authoritative: true)
        counts = measured.counts()
        XCTAssertEqual(counts.saved, 3)
        XCTAssertEqual(counts.enumerations, 0)
        XCTAssertEqual(counts.candidates, 0)
        XCTAssertLessThan(counts.songReads, 30)
        let collections = try await service.collections()
        let favorites = try XCTUnwrap(collections.first { $0.kind == "favorites" })
        let references = adopted.map { MusicReference(kind: .library, id: $0["songId"].string) }
        mark = clock.now
        let saved = try await service.editPlaylist(favorites, name: nil, refs: Array(references.dropLast()), order: nil)
        elapsed("add-9999-members")
        measured.reset()
        let databaseConnection = database.storage
        let beforeRows = try await databaseConnection.read { db in try db.query(
            "SELECT count(*) AS count FROM music_collection_members WHERE scope_key=? AND collections_key=?",
            [.text("device"), .text(saved.id)]).first!.integer("count") }
        _ = try await service.changeCollectionMembership(saved, adding: [references.last!], removing: [])
        elapsed("add-last-member")
        counts = measured.counts()
        XCTAssertEqual(counts.songReads, 0)
        XCTAssertLessThan(counts.referenceChecks, 6)
        XCTAssertEqual(counts.saved, 0)
        let afterRows = try await databaseConnection.read { db in try db.query(
            "SELECT count(*) AS count FROM music_collection_members WHERE scope_key=? AND collections_key=?",
            [.text("device"), .text(saved.id)]).first!.integer("count") }
        XCTAssertEqual(afterRows, beforeRows + 1)
        measured.reset()
        try await service.sqliteSyncCheckpoint()
        counts = measured.counts()
        XCTAssertEqual(counts.songReads, 0)
        XCTAssertEqual(counts.referenceChecks, 0)
        XCTAssertEqual(counts.enumerations, 0)
        XCTAssertEqual(counts.saved, 0)
        await service.close()
        try await database.storage.close()
        let connection = try storageTestOpen(root)
        addTeardownBlock { try await connection.close() }
        let reopened = NativeMusicSQLiteFixture(connection)
        defer { try? reopened.close() }
        XCTAssertEqual(try reopened.allSongIDs().count, 10_000)
        XCTAssertEqual(try MJ.parse(XCTUnwrap(reopened.songData(id: adopted[0]["songId"].string)))["album"].string, "New album")
        XCTAssertEqual(try MJ.parse(XCTUnwrap(reopened.songData(id: adopted[0]["songId"].string)))["sources"].array.count, 5)
    }
    func testControlSnapshotInvalidatesAfterAnotherOwnerCommits() async throws {
        let root = try location(), first = try database(root)
        try first.withTransaction { try $0.saveArea("playback", value: try MusicModel.playback.storageValue) }
        _ = try first.readSnapshot()
        let second = NativeMusicSQLiteFixture(first.storage)
        defer { try? second.close() }
        var source = MusicModel.catalogSource
        source["platform"] = .string("tx")
        try second.withTransaction { try $0.saveArea("catalogSource", value: try source.storageValue) }
        let refreshed = try first.readSnapshot()
        XCTAssertEqual(try MJ.parse(XCTUnwrap(refreshed.areas["catalogSource"]))["platform"], .string("tx"))
        var playback = MusicModel.playback
        playback["mode"] = .n(2)
        try first.withTransaction { try $0.saveArea("playback", value: try playback.storageValue) }
        let saved = try second.readSnapshot()
        XCTAssertEqual(try MJ.parse(XCTUnwrap(saved.areas["playback"]))["mode"], .n(2))
        XCTAssertEqual(try MJ.parse(XCTUnwrap(saved.areas["catalogSource"]))["platform"], .string("tx"))
    }
    func testSongAndQueueCommitAtomicallyAndRollbackOnFailureOrCancellation() async throws {
        let root = try location(), database = try database(root)
        let service = NativeMusicService(database: database, platformClient: try NativeMusicFixture.client())
        try await service.setAccess(available: true, generation: 1)
        let incoming = try song(1)
        for failure in [MusicServiceError.storageFailed, .cancelled] {
            do {
                try await service.sqliteAtomicSongAndQueue(incoming, failure: failure)
                XCTFail("The candidate must roll back")
            } catch { XCTAssertEqual(error as? MusicServiceError, failure) }
            XCTAssertEqual(try database.allSongIDs().count, 0)
            XCTAssertEqual(try database.readSnapshot().revision, 0)
        }
        try await service.sqliteAtomicSongAndQueue(incoming, failure: nil)
        XCTAssertEqual(try database.allSongIDs().count, 1)
        XCTAssertEqual(try database.readSnapshot().revision, 1)
        await service.close()
        try await database.storage.close()
        let connection = try storageTestOpen(root)
        addTeardownBlock { try await connection.close() }
        let reopened = NativeMusicSQLiteFixture(connection)
        defer { try? reopened.close() }
        let restored = NativeMusicService(database: reopened, platformClient: try NativeMusicFixture.client())
        try await restored.setAccess(available: true, generation: 1)
        let queue = try await restored.queue()
        XCTAssertEqual(queue.entries.count, 1)
        XCTAssertEqual(queue.currentId, "entry-1")
        await restored.close()
    }
    func testActualTaskCancellationBeforeCommitRollsBackAndMissingQueueReferenceIsRejected() async throws {
        let root = try location(), database = try database(root)
        let service = NativeMusicService(database: database, platformClient: try NativeMusicFixture.client())
        try await service.setAccess(available: true, generation: 1)
        let incoming = try song(2)
        let cancelled = Task { try await service.sqliteCancelAtomicSongAndQueue(incoming) }
        do { try await cancelled.value; XCTFail("Task cancellation must not commit a candidate") }
        catch { XCTAssertEqual(error as? MusicServiceError, .cancelled) }
        XCTAssertEqual(try database.readSnapshot().revision, 0)
        XCTAssertEqual(try database.allSongIDs(), [])
        do { try await service.sqliteAtomicSongAndQueue(incoming, failure: nil, missingReference: true); XCTFail("Unknown permanent reference must be rejected") }
        catch { XCTAssertEqual(error as? MusicServiceError, .corruptDocument) }
        XCTAssertEqual(try database.allSongIDs(), [])
        await service.close()
    }
    func testRetiredWriterCannotMutateAndUnrelatedUserFilesRemainUntouched() throws {
        let root = try location(), database = try database(root)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        let old = root.appendingPathComponent("unrelated-user-file"), oldBytes = Data("synthetic user data".utf8)
        try oldBytes.write(to: old)
        var retained: (any MusicNativeLibraryWriting)?
        try database.withTransaction { retained = $0 }
        XCTAssertEqual(try database.readSnapshot().revision, 0)
        XCTAssertThrowsError(try XCTUnwrap(retained).saveArea("playback", value: try MusicModel.playback.storageValue)) {
            XCTAssertEqual($0 as? MusicServiceError, .storageConflict)
        }
        XCTAssertEqual(try Data(contentsOf: old), oldBytes)
    }
    func testCommitReceiptLossIsReportedWithoutReplayingAndSourceOwnershipCannotRebind() throws {
        let root = try location(), database = try database(root)
        var first = try song(1); first["songId"] = .string(UUID().uuidString.lowercased())
        XCTAssertThrowsError(try database.withTransaction(commit: { commit in
            try commit(); throw MusicServiceError.storageFailed
        }) { try $0.saveSong(first.bytes) }) {
            guard case MusicNativeLibraryCommitError.receiptLost = $0 else { return XCTFail("Expected a verified lost receipt") }
        }
        XCTAssertEqual(try database.allSongIDs().count, 1)
        var second = first; second["songId"] = .string(UUID().uuidString.lowercased())
        XCTAssertThrowsError(try database.withTransaction { try $0.saveSong(second.bytes) }) { XCTAssertEqual($0 as? MusicServiceError, .nativeIDConflict) }
        XCTAssertEqual(try database.allSongIDs(), [first["songId"].string])
        XCTAssertEqual(try database.sourceOwner(key: first["sources"].array[0]["sourceKey"].string), first["songId"].string)
    }
    func testUnknownCurrentDatabaseIsNotRepaired() throws {
        let root = try location(), directory = root.appendingPathComponent("storage-v1", isDirectory: true)
        let file = directory.appendingPathComponent("meowfilm.sqlite3")
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: root) }
        let invalid = Data("not a sqlite database".utf8)
        try invalid.write(to: file)
        XCTAssertThrowsError(try storageTestOpen(root)) { XCTAssertEqual($0 as? SQLiteStorageError, .corrupt) }
        XCTAssertEqual(try Data(contentsOf: file), invalid)
    }

    func testRegisteredSourcesAndRecordingBaselineCannotBeRemovedOrChanged() throws {
        let root = try location(), database = try database(root)
        var original = try song(1)
        original["songId"] = .string(UUID().uuidString.lowercased())
        original["baselineDuration"] = .n(180)
        try database.withTransaction { try $0.saveSong(original.bytes) }
        let revision = try database.readSnapshot().revision
        var missing = original
        missing["sources"] = .array(Array(original["sources"].array.dropLast()))
        XCTAssertThrowsError(try database.withTransaction { try $0.saveSong(missing.bytes) }) {
            XCTAssertEqual($0 as? MusicServiceError, .nativeIDConflict)
        }
        var rebased = original
        rebased["baselineDuration"] = .n(183)
        XCTAssertThrowsError(try database.withTransaction { try $0.saveSong(rebased.bytes) }) {
            XCTAssertEqual($0 as? MusicServiceError, .storageConflict)
        }
        XCTAssertEqual(try database.readSnapshot().revision, revision)
        let saved = try MJ.parse(XCTUnwrap(database.songData(id: original["songId"].string)))
        XCTAssertEqual(saved.stable, original.stable)
    }

    func testReceiptLossSettlesTheSameOccurrencePositionAndChangedSongWithoutReplay() async throws {
        let root = try location(), database = try database(root)
        let measured = MusicSQLiteMeasuredStore(database)
        let service = NativeMusicService(database: measured, platformClient: try NativeMusicFixture.client())
        try await service.setAccess(available: true, generation: 1)
        let input = try song(1)
        let adopted = try await service.adoptSongs([input], authoritative: true)
        let id = adopted[0]["songId"].string
        let selected = try await service.changeQueue(.replace([.init(kind: .library, id: id)], index: 0), expectedRevision: 0)
        try await service.sqliteRecordPosition(37)
        let currentID = try XCTUnwrap(selected.currentId)
        measured.loseNextReceipt()
        do {
            _ = try await service.changeQueue(.select(currentID), expectedRevision: selected.revision)
            XCTFail("The callback receipt remains unconfirmed")
        } catch { XCTAssertEqual(error as? MusicServiceError, .commitUnconfirmed) }
        let state = try await service.state()
        XCTAssertEqual(state.currentId, currentID)
        XCTAssertEqual(state.position, 0)
        let persisted = try MJ.parse(XCTUnwrap(database.readSnapshot().areas["playback"]))
        XCTAssertEqual(persisted["position"]["seconds"], .n(0))

        let changes = await service.songChanges()
        var updated = input; updated["album"] = .string("Receipt update")
        let revision = try database.readSnapshot().revision
        measured.loseNextReceipt()
        do { _ = try await service.adoptSongs([updated], authoritative: true); XCTFail("Expected lost callback receipt") }
        catch { XCTAssertEqual(error as? MusicServiceError, .commitUnconfirmed) }
        var iterator = changes.makeAsyncIterator()
        let notification = await iterator.next()
        XCTAssertEqual(notification, [id])
        XCTAssertEqual(try database.readSnapshot().revision, revision + 1)
        XCTAssertEqual(try MJ.parse(XCTUnwrap(database.songData(id: id)))["album"], .string("Receipt update"))
        _ = try await service.state()
        XCTAssertEqual(try database.readSnapshot().revision, revision + 1)
        await service.close()
    }

    func testUnknownReceiptReadbackUsesExactIdentityAndNeverReplaysOrAcceptsAnotherCommit() async throws {
        for interveningWrite in [false, true] {
            let root = try location(), database = try database(root)
                let measured = MusicSQLiteMeasuredStore(database)
            let service = NativeMusicService(database: measured, platformClient: try NativeMusicFixture.client())
            try await service.setAccess(available: true, generation: 1)
            measured.loseNextReceipt(unknown: true)
            do { _ = try await service.adoptSongs([song(1)], authoritative: true); XCTFail("Expected unknown receipt") }
            catch { XCTAssertEqual(error as? MusicServiceError, .commitUnconfirmed) }
            let committed = try database.readSnapshot().identity
            if interveningWrite {
                try database.withTransaction { try $0.saveArea("catalogSource", value: try MusicModel.catalogSource.storageValue) }
                do { _ = try await service.state(); XCTFail("A third identity must remain fenced") }
                catch { XCTAssertEqual(error as? MusicServiceError, .storageConflict) }
            } else {
                _ = try await service.state()
                XCTAssertEqual(try database.readSnapshot().identity, committed)
            }
            XCTAssertEqual(try database.allSongIDs().count, 1)
            await service.close()
        }
    }

    func testSearchAdoptionMatchesAnUnknownSourceOnceAndRevokedSourceReadsFail() async throws {
        let root = try location(), database = try database(root)
        let measured = MusicSQLiteMeasuredStore(database)
        let service = NativeMusicService(database: measured, platformClient: try NativeMusicFixture.client())
        try await service.setAccess(available: true, generation: 1)
        let first = try await service.adoptSongs([song(1, platforms: ["wy"])], authoritative: true)
        measured.reset()
        let request = await service.beginSourceDiscoveryObservation(platform: "tx", query: "Track 1 Fixture")
        let adopted = try await service.adoptSearchSongs([song(1, platforms: ["tx"])], observation: request, checks: [:])
        XCTAssertEqual(adopted[0]["songId"], first[0]["songId"])
        XCTAssertEqual(measured.counts().candidateBatches, 1)
        XCTAssertEqual(measured.counts().candidates, 0)
        service.revokeAccess(generation: 1)
        let track = MusicNativeTrackData(platform: "tx", data: adopted[0]["sources"].array.last!["musicInfo"].bytes)
        do { _ = try await service.registeredSongID(track); XCTFail("Source reads must use the access fence") }
        catch { XCTAssertEqual(error as? MusicServiceError, .accessUnavailable) }
        await service.close()
    }

    func testSlowSongObserverReceivesTheUnionOfUnconsumedChanges() async throws {
        let root = try location(), database = try database(root)
        let service = NativeMusicService(database: database, platformClient: try NativeMusicFixture.client())
        try await service.setAccess(available: true, generation: 1)
        let changes = await service.songChanges()
        let first = try await service.adoptSongs([song(1)], authoritative: true)
        let second = try await service.adoptSongs([song(2)], authoritative: true)
        var iterator = changes.makeAsyncIterator()
        let ids = await iterator.next()
        XCTAssertEqual(ids, Set([first[0]["songId"].string, second[0]["songId"].string]))
        await service.close()
    }
}

private extension NativeMusicService {
    func sqliteSyncCheckpoint() throws {
        let initial = try read()
        var round = MusicNativeSyncRound(request: NativeSyncFixture.request(), epoch: epoch, port: NativeSyncPeer(),
            library: initial.library["library"], ledger: try MusicNativeSyncLedger.load(initial.library),
            revision: initial.library["revision"].int)
        round.ledger.phase = .pulling
        try syncCheckpoint(&round)
    }

    func sqliteAtomicSongAndQueue(_ incoming: MJ, failure: MusicServiceError?, missingReference: Bool = false) throws {
        try transaction(["queue", "library"]) { draft in
            var library = draft.library["library"]
            let ids = try registerSongs([incoming], library: &library)
            draft.queue["entries"] = .array([.obj([("id", .string("entry-1")), ("songId", .string(missingReference ? UUID().uuidString.lowercased() : ids[0]))])])
            draft.queue["currentId"] = .string("entry-1"); draft.queue["serial"] = .n(1)
            if let failure { throw failure }
        }
    }
    func sqliteCancelAtomicSongAndQueue(_ incoming: MJ) throws {
        try withUnsafeCurrentTask { task in
            try transaction(["queue", "library"]) { draft in
                var library = draft.library["library"]
                _ = try registerSongs([incoming], library: &library)
                task?.cancel()
            }
        }
    }

    func sqliteRecordPosition(_ seconds: Double) throws {
        try transaction(["playback"]) { draft in
            draft.playback["position"] = .obj([("occurrenceId", draft.queue["currentId"]), ("seconds", .number(seconds))])
        }
    }

}

private final class MusicSQLiteMeasuredStore: MusicNativeLibraryStoring, @unchecked Sendable {
    struct Counts { var songReads = 0, referenceChecks = 0, candidates = 0, candidateBatches = 0, sourceOwnerBatches = 0, enumerations = 0, saved = 0 }
    private let lock = NSLock()
    private var value = Counts()
    private var receiptLoss: Bool?
    let database: NativeMusicSQLiteFixture
    init(_ database: NativeMusicSQLiteFixture) { self.database = database }
    func reset() { lock.lock(); value = .init(); lock.unlock() }
    func counts() -> Counts { lock.lock(); defer { lock.unlock() }; return value }
    func loseNextReceipt(unknown: Bool = false) { lock.lock(); receiptLoss = unknown; lock.unlock() }
    private func count(_ body: (inout Counts) -> Void) { lock.lock(); body(&value); lock.unlock() }
    func readRevision() throws -> MusicNativeLibraryRevision { try database.readRevision() }
    func readSnapshot() throws -> MusicNativeLibrarySnapshot { try database.readSnapshot() }
    func songData(id: String) throws -> Data? { count { $0.songReads += 1 }; return try database.songData(id: id) }
    func containsSong(id: String) throws -> Bool { count { $0.referenceChecks += 1 }; return try database.containsSong(id: id) }
    func sourceOwner(key: String) throws -> String? { try database.sourceOwner(key: key) }
    func sourceOwners(keys: Set<String>) throws -> [String: String] {
        count { $0.sourceOwnerBatches += 1 }; return try database.sourceOwners(keys: keys)
    }
    func candidateIDs(lookupKey: String) throws -> [String] { count { $0.candidates += 1 }; return try database.candidateIDs(lookupKey: lookupKey) }
    func candidateIDs(lookupKeys: Set<String>) throws -> [String: [String]] {
        count { $0.candidateBatches += 1 }; return try database.candidateIDs(lookupKeys: lookupKeys)
    }
    func querySongIDs(query: String) throws -> [String] { try database.querySongIDs(query: query) }
    func allSongIDs() throws -> [String] { count { $0.enumerations += 1 }; return try database.allSongIDs() }
    func withTransaction<T>(commit: (_ action: () throws -> Void) throws -> Void, _ body: (any MusicNativeLibraryWriting) throws -> T) throws -> T {
        lock.lock(); let loss = receiptLoss; receiptLoss = nil; lock.unlock()
        let before = try database.readRevision()
        do {
            return try database.withTransaction(commit: { action in
                try withoutActuallyEscaping(action) { sqlCommit in
                    try commit {
                        try sqlCommit()
                        if loss != nil { throw MusicServiceError.storageFailed }
                    }
                }
            }) { try body(Writer(store: self, base: $0)) }
        } catch MusicNativeLibraryCommitError.receiptLost where loss == true {
            throw MusicNativeLibraryCommitError.outcomeUnknown(before: before, after: try database.readRevision())
        }
    }
    func close() throws { try database.close() }
    private struct Writer: MusicNativeLibraryWriting {
        let store: MusicSQLiteMeasuredStore
        let base: any MusicNativeLibraryWriting
        var changedSongIDs: Set<String> { base.changedSongIDs }
        func readRevision() throws -> MusicNativeLibraryRevision { try base.readRevision() }
        func readSnapshot() throws -> MusicNativeLibrarySnapshot { try base.readSnapshot() }
        func songData(id: String) throws -> Data? { store.count { $0.songReads += 1 }; return try base.songData(id: id) }
        func containsSong(id: String) throws -> Bool { store.count { $0.referenceChecks += 1 }; return try base.containsSong(id: id) }
        func sourceOwner(key: String) throws -> String? { try base.sourceOwner(key: key) }
        func sourceOwners(keys: Set<String>) throws -> [String: String] { try store.sourceOwners(keys: keys) }
        func candidateIDs(lookupKey: String) throws -> [String] { store.count { $0.candidates += 1 }; return try base.candidateIDs(lookupKey: lookupKey) }
        func candidateIDs(lookupKeys: Set<String>) throws -> [String: [String]] { try store.candidateIDs(lookupKeys: lookupKeys) }
        func querySongIDs(query: String) throws -> [String] { try base.querySongIDs(query: query) }
        func allSongIDs() throws -> [String] { store.count { $0.enumerations += 1 }; return try base.allSongIDs() }
        func saveSong(_ data: Data) throws { try base.saveSong(data); store.count { $0.saved += 1 } }
        func saveArea(_ area: String, value: RuntimeValue) throws { try base.saveArea(area, value: value) }
    }
}

private final class NativeMusicSQLiteFixture: MusicNativeLibraryStoring, @unchecked Sendable {
    let storage: SQLiteDatabase
    private let owner: MusicNativeLibraryDatabase
    init(_ storage: SQLiteDatabase) { self.storage = storage; owner = .init(database: storage) }
    func readRevision() throws -> MusicNativeLibraryRevision { try storageTestIO { try self.owner.readRevision() } }
    func readSnapshot() throws -> MusicNativeLibrarySnapshot { try storageTestIO { try self.owner.readSnapshot() } }
    func songData(id: String) throws -> Data? { try storageTestIO { try self.owner.songData(id: id) } }
    func containsSong(id: String) throws -> Bool { try storageTestIO { try self.owner.containsSong(id: id) } }
    func sourceOwner(key: String) throws -> String? { try storageTestIO { try self.owner.sourceOwner(key: key) } }
    func sourceOwners(keys: Set<String>) throws -> [String: String] { try storageTestIO { try self.owner.sourceOwners(keys: keys) } }
    func candidateIDs(lookupKey: String) throws -> [String] { try storageTestIO { try self.owner.candidateIDs(lookupKey: lookupKey) } }
    func candidateIDs(lookupKeys: Set<String>) throws -> [String: [String]] { try storageTestIO { try self.owner.candidateIDs(lookupKeys: lookupKeys) } }
    func querySongIDs(query: String) throws -> [String] { try storageTestIO { try self.owner.querySongIDs(query: query) } }
    func allSongIDs() throws -> [String] { try storageTestIO { try self.owner.allSongIDs() } }
    func withTransaction<T>(commit: (_ action: () throws -> Void) throws -> Void,
                            _ body: (any MusicNativeLibraryWriting) throws -> T) throws -> T {
        // The product normally calls this from its non-main music owner.
        // A test assertion can borrow that lane synchronously without a fake store.
        try withoutActuallyEscaping(commit) { committed in
            try withoutActuallyEscaping(body) { action in
                try storageTestIO { try self.owner.withTransaction(commit: committed, action) }
            }
        }
    }
    func close() throws { try storageTestIO { try self.owner.close() } }
}
