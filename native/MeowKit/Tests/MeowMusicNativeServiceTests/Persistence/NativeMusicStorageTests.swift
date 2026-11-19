import XCTest
import MeowMusicFeature
import MeowStorage
import MeowStorageTestSupport
@testable import MeowMusicNativeService

final class NativeMusicStorageTests: XCTestCase {
    func testQueueOnlyTransactionKeepsHistoryAndOtherAreasAtomic() async throws {
        var document = try MusicDocument(nil, runID: "storage")
        document.history["playlists"] = .array(["newest", "oldest"].map {
            .obj([("id", .string($0)), ("title", .string("Archive")),
                  ("at", .number(NativeMusicFixture.date.timeIntervalSince1970 * 1000)),
                  ("entries", .array([])), ("currentId", .null), ("priority", .array([])),
                  ("history", .array([])), ("source", .null)])
        })
        let song = try NativeMusicFixture.song()
        document.library["library"]["songs"][song["songId"].string] = song
        document.history["days"] = .array([.obj([
            ("date", .string("2026-10-23")),
            ("songs", .array([.obj([("songId", song["songId"]), ("context", .obj()), ("at", .n(1))])]))
        ])])
        let original = try document.encode(changed: ["library", "queue", "history", "playback"])
        let before = try MJ.parse(original)
        let store = NativeMusicTestLibrary(original), service = try await NativeMusicFixture.service(store)
        let context = MJ.obj([("sourceTitle", .string(String(repeating: "q", count: 1024)))])
        let entries = (1...2001).map {
            MJ.obj([("id", .string("entry-\($0)")), ("songId", song["songId"]), ("context", context)])
        }
        for failing in [true, false] {
            store.fail(failing ? .storageConflict : nil)
            do {
                try await service.transaction(["queue"]) { draft in
                    draft.queue["entries"] = .array(entries)
                    draft.queue["currentId"] = entries.last?["id"] ?? .null
                    draft.queue["serial"] = .n(entries.count)
                    draft.queue["revision"] = .n(1)
                }
                XCTAssertFalse(failing)
            } catch {
                XCTAssertTrue(failing); XCTAssertEqual(error as? MusicServiceError, .storageConflict)
            }
            if failing {
                XCTAssertEqual(store.snapshot().0, original); XCTAssertEqual(store.snapshot().2, 0)
            }
        }
        let saved = try MJ.parse(XCTUnwrap(store.snapshot().0)), records = saved["areas"]
        XCTAssertEqual(records["queue"]["entries"].array.count, 2001)
        XCTAssertEqual(records["history"]["playlists"].array.map { $0["id"].string }, ["newest", "oldest"])
        XCTAssertEqual(records["history"]["days"].stable, before["areas"]["history"]["days"].stable)
        for area in ["library", "playback"] { XCTAssertEqual(records[area].stable, before["areas"][area].stable) }
        XCTAssertEqual(saved["revision"].int, before["revision"].int + 1)
        XCTAssertEqual(store.snapshot().2, 1)
        await service.close()
    }

    func testMissingOrInvalidMatchingDurationDoesNotRejectUsableSourceLocators() throws {
        let original = try XCTUnwrap(NativeMusicFixture.song()["sources"].array.first)
        let invalid: [MJ] = [.n(-1), .string("-1"), .string("NaN"), .string("Infinity"),
                             .string("1e999"), .bool(true), .obj(), .array([])]
        for value in invalid {
            var source = original
            source["musicInfo"]["duration"] = value
            let song = try MusicModel.normalize(.init(platform: "tx", data: source["musicInfo"].bytes))
            XCTAssertNil(MusicModel.descriptor(song).duration)
            XCTAssertEqual(try MusicModel.sources(song).first?.nativeId, original["musicInfo"]["songmid"].id)
        }
        let valid: [MJ] = [.n(0), .n(604_800), .n(604_801), .string("180")]
        for value in valid {
            var source = original
            source["musicInfo"]["duration"] = value
            XCTAssertNoThrow(try MusicModel.cleanSource(source))
        }
    }

    func testLocalCollectionEditPreservesSongExtensionsAndUnconsumedBrokenAreas() async throws {
        var document = try MusicDocument(nil, runID: "fixture")
        let extra = try MJ.parse(Data(#"{"precise":1.234567890123456789,"large":9007199254740993,"huge":1e999}"#.utf8))
        var unrelated = try NativeMusicFixture.song("unrelated")
        unrelated["extra"] = extra
        document.library["library"]["songs"][unrelated["songId"].string] = unrelated
        document.library["sync"] = try MusicNativeSyncLedger().json()
        document.library["sync"]["fences"] = .null
        let row = MusicModel.freshCollection("playlist:1", "Before")
        document.library["library"]["collections"] = .array(document.library["library"]["collections"].array + [row])
        document.library["library"]["nextId"] = .n(2)
        document.queue["entries"] = .null
        document.playback["sourceExclusions"] = .null
        document.history["playlists"] = .null
        let bytes = try document.encode(changed: ["library", "queue", "playback", "history"])
        let store = NativeMusicTestLibrary(bytes), service = try await NativeMusicFixture.service(store)
        let songBefore = try XCTUnwrap(store.songData(id: unrelated["songId"].string))
        let listed = try await service.collections()
        let selected = try XCTUnwrap(listed.first { $0.id == "playlist:1" })
        let changed = try await service.editPlaylist(selected, name: "After", refs: nil, order: nil)
        XCTAssertEqual(changed.title, "After")
        let saved = try MJ.parse(XCTUnwrap(store.snapshot().0)), before = try MJ.parse(bytes)
        XCTAssertEqual(saved["areas"]["queue"].stable, before["areas"]["queue"].stable)
        XCTAssertEqual(saved["areas"]["playback"].stable, before["areas"]["playback"].stable)
        XCTAssertEqual(saved["areas"]["history"].stable, before["areas"]["history"].stable)
        let library = saved["areas"]["library"]
        XCTAssertEqual(try store.songData(id: unrelated["songId"].string), songBefore)
        XCTAssertEqual(library["sync"]["fences"], document.library["sync"]["fences"])
        XCTAssertEqual(extra["precise"].json, "1.234567890123456789")
        await assertMusicFailure(.corruptDocument) { _ = try await service.queue() }
        await assertMusicFailure(.corruptDocument) { _ = try await service.sourceExclusions() }
        await assertMusicFailure(.corruptDocument) { _ = try await service.playedPlaylists() }
        await service.close()
    }
    func testSelectedCollectionMissingSongIsRejectedBeforeCommit() async throws {
        var document = try MusicDocument(nil, runID: "fixture")
        var selected = MusicModel.freshCollection("playlist:1", "Selected")
        selected["order"] = .strings([UUID().uuidString.lowercased()])
        document.library["library"]["collections"] = .array([selected])
        document.library["library"].remove("songs")
        let database = try storageTestDatabase(self)
        let store = MusicNativeLibraryDatabase(database: database)
        let library = try document.library.storageValue
        // A dangling member cannot enter the current database: the typed
        // relation owns this rejection before a collection can be opened.
        XCTAssertThrowsError(try storageTestIO {
            try store.withTransaction { try $0.saveArea("library", value: library) }
        }) {
            XCTAssertEqual($0 as? SQLiteStorageError, .constraint)
        }
        let snapshot = try storageTestIO { try store.readSnapshot() }
        XCTAssertEqual(snapshot.revision, 0)
        XCTAssertTrue(snapshot.areas.isEmpty)
        XCTAssertEqual(try storageTestIO { try store.allSongIDs() }, [])
        let collections = try await database.read { try $0.query("SELECT * FROM music_collections") }
        XCTAssertTrue(collections.isEmpty)
        try storageTestIO { try store.close() }
    }
    func testSongSourceAndNativeMetadataExtensionsSurviveSQLiteAdoptionAndColdRead() async throws {
        let extra = try MJ.parse(Data(#"{"precise":1.234567890123456789,"large":9007199254740993,"huge":1e999}"#.utf8))
        var song = try NativeMusicFixture.song("extended")
        song["extra"] = extra
        var source = try XCTUnwrap(song["sources"].array.first)
        source["extra"] = extra; source["musicInfo"]["extra"] = extra
        song["sources"] = .array([source])
        let store = NativeMusicTestLibrary(), service = try await NativeMusicFixture.service(store)
        let adopted = try await service.adoptSongs([song], authoritative: true)
        let id = try XCTUnwrap(adopted.first?["songId"].string)
        XCTAssertEqual(adopted[0]["extra"], extra)
        XCTAssertEqual(adopted[0]["sources"].array[0]["extra"], extra)
        XCTAssertEqual(adopted[0]["sources"].array[0]["musicInfo"]["extra"], extra)
        let key = source["sourceKey"].string, before = store.snapshot()
        XCTAssertEqual(try store.sourceOwner(key: key), id)
        _ = try await service.adoptSongs([adopted[0]], authoritative: true)
        XCTAssertEqual(store.snapshot().0, before.0); XCTAssertEqual(store.snapshot().2, before.2)
        var updated = adopted[0]
        var next = extra; next["increment"] = .n(1)
        updated["extra"] = next
        var updatedSource = updated["sources"].array[0]
        updatedSource["extra"] = next; updatedSource["musicInfo"]["extra"] = next
        updated["sources"] = .array([updatedSource])
        _ = try await service.adoptSongs([updated], authoritative: true)
        let after = store.snapshot()
        XCTAssertEqual(after.2, before.2 + 1)
        await service.close()
        let reopened = try await NativeMusicFixture.service(store)
        let state = try await reopened.read()
        let saved = try await reopened.repositorySong(id, state)
        XCTAssertEqual(saved["extra"].stable, next.stable)
        XCTAssertEqual(saved["sources"].array[0]["extra"].stable, next.stable)
        XCTAssertEqual(saved["sources"].array[0]["musicInfo"]["extra"].stable, next.stable)
        XCTAssertEqual(try store.sourceOwner(key: key), id)
        XCTAssertEqual(store.snapshot().0, after.0); XCTAssertEqual(store.snapshot().2, after.2)
        await reopened.close()
    }
    func testUnreadableLibraryListCannotBecomeSuccessfulEmptyCatalogue() async throws {
        for damaged in [MJ.null] {
            var document = try MusicDocument(nil, runID: "fixture")
            document.library["library"]["collections"] = .array([])
            document.library["library"]["networkReferences"] = damaged
            let bytes = try document.encode(changed: ["library"]), store = NativeMusicTestLibrary(bytes)
            let service = try await NativeMusicFixture.service(store)
            await assertMusicFailure(.corruptDocument) { _ = try await service.collections() }
            XCTAssertEqual(store.snapshot().0, bytes); XCTAssertEqual(store.snapshot().2, 0)
            await service.close()
        }
    }
    func testSelectedFavoritesIdentityCannotBeReinterpretedAsRemovablePlaylist() async throws {
        var document = try MusicDocument(nil, runID: "fixture")
        var favorites = document.library["library"]["collections"].array[0]
        favorites["kind"] = .string("playlist")
        document.library["library"]["collections"] = .array([favorites])
        let bytes = try document.encode(changed: ["library"]), store = NativeMusicTestLibrary(bytes)
        let service = try await NativeMusicFixture.service(store)
        await assertMusicFailure(.favoritesProtected) {
            try await service.removePlaylist(.init(id: "favorites", title: "", revision: 0))
        }
        XCTAssertEqual(store.snapshot().0, bytes); XCTAssertEqual(store.snapshot().2, 0)
        await service.close()
    }
    func testCorruptCurrentAreasNeverCreateNewLibrary() async throws {
        for input in [
            #"{"revision":9007199254740993,"areas":{},"songs":{}}"#
        ] {
            let bytes = Data(input.utf8), store = NativeMusicTestLibrary(bytes)
            let service = try await NativeMusicFixture.service(store)
            do { _ = try await service.createPlaylist(name: "Must not replace", refs: []); XCTFail(input) }
            catch { XCTAssertNotNil(error as? MusicServiceError) }
            XCTAssertEqual(store.snapshot().0, bytes); XCTAssertEqual(store.snapshot().2, 0)
            await service.close()
        }
    }
    func testCachedControlStateNeverMasksChangedCorruptAreas() async throws {
        let store = NativeMusicTestLibrary(), service = try await NativeMusicFixture.service(store)
        var document = try await service.read()
        let valid = try document.encode(changed: [])
        _ = try await service.state()
        _ = try await service.queue()
        var corrupt = try MJ.parse(valid)
        corrupt["areas"]["queue"]["revision"] = .n(-1)
        store.replace(corrupt.bytes)
        do { _ = try await service.queue(); XCTFail("Changed corrupt bytes must be revalidated") }
        catch { XCTAssertEqual(error as? MusicServiceError, .corruptDocument) }
        store.replace(valid)
        do { _ = try await service.queue() }
        catch { XCTFail("Restored valid bytes must be readable: \(error)") }
        await service.close()
    }
    func testSameRevisionWithDifferentReceiptReloadsCachedControlsWithoutWriting() async throws {
        var document = try MusicDocument(nil, runID: "fixture")
        let original = try document.encode(changed: [])
        let store = NativeMusicTestLibrary(original), service = try await NativeMusicFixture.service(store)
        let before = try store.readSnapshot().identity
        let initial = try await service.read()
        XCTAssertEqual(initial.catalogSource["platform"].string, "wy")
        var replacement = try MJ.parse(original)
        replacement["areas"]["catalogSource"]["platform"] = .string("kg")
        store.replace(replacement.bytes)
        let after = try store.readSnapshot().identity
        XCTAssertEqual(after.revision, before.revision)
        XCTAssertNotEqual(after.receipt, before.receipt)
        let current = try await service.read()
        XCTAssertEqual(current.catalogSource["platform"].string, "kg")
        XCTAssertEqual(store.snapshot().2, 0)
        XCTAssertEqual(store.snapshot().0, replacement.bytes)
        await service.close()
    }
    func testTransactionRejectsChangedReceiptAtTheSameRevisionWithoutPublishingDraft() async throws {
        var document = try MusicDocument(nil, runID: "fixture")
        let original = try document.encode(changed: [])
        let backing = NativeMusicTestLibrary(original), store = MusicReplacementBeforeTransactionLibrary(backing)
        let service = NativeMusicService(database: store, platformClient: try NativeMusicFixture.client())
        try await service.setAccess(available: true, generation: 1)
        _ = try await service.read()
        let before = try backing.readSnapshot().identity
        var replacement = try MJ.parse(original)
        replacement["areas"]["catalogSource"]["platform"] = .string("kg")
        store.replaceBeforeNextTransaction(replacement.bytes)
        do { _ = try await service.createPlaylist(name: "Must not publish", refs: []); XCTFail("A different receipt must conflict") }
        catch { XCTAssertEqual(error as? MusicServiceError, .storageConflict) }
        let after = try backing.readSnapshot().identity
        XCTAssertEqual(after.revision, before.revision)
        XCTAssertNotEqual(after.receipt, before.receipt)
        XCTAssertEqual(backing.snapshot().0, replacement.bytes)
        XCTAssertEqual(backing.snapshot().2, 0)
        let current = try await service.read()
        XCTAssertEqual(current.catalogSource["platform"].string, "kg")
        await service.close()
    }
    func testInvalidQueueSerialAndPointersAreRejectedWithoutReadRepair() async throws {
        let song = try NativeMusicFixture.song()
        let invalid: [(String, MJ)] = [
            ("serial", .n(0)), ("currentId", .string("entry-99")),
            ("priority", .strings(["entry-1"]))
        ]
        for (field, value) in invalid {
            var document = try MusicDocument(nil, runID: "fixture")
            document.library["library"]["songs"][song["songId"].string] = song
            document.queue["entries"] = .array([.obj([("id", .string("entry-1")), ("songId", song["songId"])])])
            document.queue["currentId"] = .string("entry-1"); document.queue["serial"] = .n(1)
            document.queue[field] = value
            let bytes = try document.encode(changed: ["library", "queue"]), store = NativeMusicTestLibrary(bytes)
            let service = try await NativeMusicFixture.service(store)
            do { _ = try await service.queue(); XCTFail("Invalid \(field) must not be repaired") }
            catch { XCTAssertEqual(error as? MusicServiceError, .corruptDocument) }
            do { _ = try await service.changeQueue(.clear, expectedRevision: 0); XCTFail("Do not reset a bad document") }
            catch { XCTAssertEqual(error as? MusicServiceError, .corruptDocument) }
            XCTAssertEqual(store.snapshot().0, bytes); XCTAssertEqual(store.snapshot().2, 0)
            await service.close()
        }
    }
    func testJSONPreservesUnicodeByteIdentitiesAndNativeNumericStrings() throws {
        let value = try MJ.parse(Data(#"{"é":1,"e\u0301":2,"id":"9007199254740993"}"#.utf8))
        XCTAssertEqual(value.fields.count, 3)
        XCTAssertEqual(value["é"].int, 1); XCTAssertEqual(value["e\u{301}"].int, 2)
        XCTAssertEqual(value["id"].id, "9007199254740993")
        XCTAssertEqual(try MJ.parse(Data("9007199254740993".utf8)).id, "9007199254740993")
        XCTAssertEqual(try MJ.parse(Data(#"{"a":1,"a":2}"#.utf8))["a"].int, 2)
    }
    func testTransactionConflictAndStaleCollectionRevisionKeepRecordsUnchanged() async throws {
        let store = NativeMusicTestLibrary(), service = try await NativeMusicFixture.service(store)
        let c = try await service.createPlaylist(name: "One", refs: [])
        _ = try await service.editPlaylist(c, name: "Two", refs: nil, order: nil)
        let before = store.snapshot().0
        do { try await service.removePlaylist(c); XCTFail("stale revision") }
        catch { XCTAssertEqual(error as? MusicServiceError, .revisionConflict) }
        XCTAssertEqual(before, store.snapshot().0)
        store.fail(.storageConflict)
        do { _ = try await service.createPlaylist(name: "Conflict", refs: []); XCTFail("transaction conflict") }
        catch { XCTAssertEqual(error as? MusicServiceError, .storageConflict) }
        XCTAssertEqual(before, store.snapshot().0)
    }
    func testAllOffReadStaysOffAndUserWriteStillRequiresAvailableQuality() async throws {
        let service = try await NativeMusicFixture.service()
        do { _ = try await service.updateSettings(.init(ifRevision: 0, qualityEnabled: [], spatialEnabled: [])); XCTFail("all off") }
        catch { XCTAssertEqual(error as? MusicServiceError, .qualityDisabled) }
        var prefs = MusicModel.playback; prefs["qualitySettings"] = .obj([("qualityEnabled", .array([])), ("spatialEnabled", .array([]))])
        let policy = MusicModel.policy(prefs)
        XCTAssertTrue(policy.enabled.isEmpty); XCTAssertTrue(policy.spatial.isEmpty)
    }
    func testOnlyWholeQueueReplaceArchivesAndEndedAdvanceIsSingleUse() async throws {
        let scripts = NativeMusicScriptFixture(), service = try await NativeMusicFixture.service(scripts: scripts)
        let page = try await service.search(platform: "tx", query: "A", page: 1, limit: 30), ref = page.items[0].ref
        var state = try await service.changeQueue(.replace([ref, ref], index: 0), expectedRevision: 0)
        state = try await service.changeQueue(.append([ref], next: true), expectedRevision: state.revision)
        let empty = try await service.playedPlaylists(); XCTAssertTrue(empty.isEmpty)
        state = try await service.changeQueue(.replace([ref], index: 0), expectedRevision: state.revision)
        let archives = try await service.playedPlaylists(); XCTAssertEqual(archives.count, 1); XCTAssertEqual(archives[0].count, 3)
        let options = (try await service.sourceAvailability(for: ref)).options
        let candidate = try await service.resolve(attemptId: UUID().uuidString.lowercased(), occurrenceId: XCTUnwrap(state.currentId),
                                                   selection: options[0].selection(quality: "128k"))
        let ended = MusicPlaybackFact(attemptId: candidate.attemptId, occurrenceId: candidate.occurrenceId, resourceId: candidate.resourceId, kind: .ended, position: 180)
        let accepted = try await service.record(ended), duplicate = try await service.record(ended)
        XCTAssertTrue(accepted); XCTAssertFalse(duplicate)
        let advance = try await service.advance(.next, reason: .ended, expectedRevision: state.revision)
        do { _ = try await service.advance(.next, reason: .ended, expectedRevision: advance.state.revision); XCTFail("second ended") }
        catch { XCTAssertEqual(error as? MusicServiceError, .staleResult) }
        let history = try await service.playedSongs(); XCTAssertTrue(history.isEmpty, "ended without actual playing is not played")
    }
}

private final class MusicReplacementBeforeTransactionLibrary: NativeMusicTestLibraryWrapping, @unchecked Sendable {
    let backingLibrary: NativeMusicTestLibrary
    private let lock = NSLock()
    private var replacement: Data?
    init(_ backing: NativeMusicTestLibrary) { backingLibrary = backing }
    func readSnapshot() throws -> MusicNativeLibrarySnapshot { try backingLibrary.readSnapshot() }
    func replaceBeforeNextTransaction(_ data: Data) { lock.lock(); replacement = data; lock.unlock() }
    func withTransaction<T>(commit: (_ action: () throws -> Void) throws -> Void, _ body: (any MusicNativeLibraryWriting) throws -> T) throws -> T {
        lock.lock(); let data = replacement; replacement = nil; lock.unlock()
        if let data { backingLibrary.replace(data) }
        return try backingLibrary.withTransaction(commit: commit, body)
    }
}
