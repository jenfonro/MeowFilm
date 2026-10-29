import XCTest
import MeowMusicFeature
@testable import MeowMusicNativeService

final class NativeMusicStorageTests: XCTestCase {
    func testV1ImportReceiptIdempotenceRollbackAndNoSecretPersistence() async throws {
        let store = NativeMusicMemoryDocument(), service = try await NativeMusicFixture.service(store)
        var legacy = try NativeMusicFixture.legacy()
        let id = try XCTUnwrap(legacy["songs"].fields.first?.0)
        var song = legacy["songs"][id]
        song["cookie"] = .string("synthetic-private")
        song["url"] = .string("https://media.invalid/temporary")
        song["cover_url"] = .string("https://cover.invalid/a?signature=synthetic-private")
        song["source_data"]["credential"] = .string("synthetic-private")
        legacy["songs"][id] = song
        let preview = try await service.prepareImport(data: legacy.bytes, sourceId: "fixed-synthetic", ownerId: "legacy-owner")
        XCTAssertNil(store.snapshot().0)
        let report = try await service.commitImport(preview); XCTAssertFalse(report.alreadyApplied)
        let saved = try MJ.parse(XCTUnwrap(store.snapshot().0))
        XCTAssertEqual(saved["version"].int, 1)
        XCTAssertFalse(saved.json.contains("synthetic-private"))
        XCTAssertFalse(saved.json.contains("media.invalid"))
        let repeatPreview = try await service.prepareImport(data: legacy.stable.bytes, sourceId: "fixed-synthetic", ownerId: "legacy-owner")
        XCTAssertTrue(repeatPreview.report.alreadyApplied)
        XCTAssertEqual(preview.report.batchId, repeatPreview.report.batchId)
        let again = try await service.commitImport(repeatPreview); XCTAssertTrue(again.alreadyApplied)
        try await service.rollbackImport(batchId: report.batchId)
        try await service.rollbackImport(batchId: report.batchId)
        let collections = try await service.collections(); XCTAssertEqual(collections.count, 1); XCTAssertEqual(collections[0].count, 0)
    }
    func testImportedTargetChangePreventsRollbackAndKeepsTombstone() async throws {
        let service = try await NativeMusicFixture.service()
        let preview = try await service.prepareImport(data: NativeMusicFixture.legacy().bytes, sourceId: "fixed", ownerId: "legacy-owner")
        _ = try await service.commitImport(preview)
        let created = try await service.createPlaylist(name: "New", refs: [])
        try await service.removePlaylist(created)
        do { try await service.rollbackImport(batchId: preview.report.batchId); XCTFail("changed import") }
        catch { XCTAssertEqual(error as? MusicServiceError, .importedTargetChanged) }
        let stored = try await service.read()
        XCTAssertTrue(stored.library["library"]["collections"].array.contains { $0["id"].string == created.id && $0["deleted"].bool })
    }
    func testCorruptUnknownEnvelopeNeverCreatesNewLibrary() async throws {
        for input in [
            #"{"version":3,"scope":"device","revision":0,"records":{}}"#,
            #"{"version":2,"scope":"device","revision":0,"records":{}}"#,
            #"{"version":1,"scope":"device","revision":0,"records":{"[\"meowfilm\",\"music\",1,\"device\",\"downloads\"]":{"schema":1,"items":[]}}}"#,
            #"{"version":1,"scope":"device","revision":0,"records":{"unknown":{}}}"#,
            #"{"version":1,"version":2,"scope":"device","revision":0,"records":{}}"#,
            #"{"version":1,"scope":"device","revision":9007199254740993,"records":{}}"#
        ] {
            let bytes = Data(input.utf8), store = NativeMusicMemoryDocument(bytes)
            let service = try await NativeMusicFixture.service(store)
            do { _ = try await service.createPlaylist(name: "Must not replace", refs: []); XCTFail(input) }
            catch { XCTAssertNotNil(error as? MusicServiceError) }
            XCTAssertEqual(store.snapshot().0, bytes); XCTAssertEqual(store.snapshot().2, 0)
        }
    }
    func testJSONPreservesUnicodeByteIdentitiesAndNativeNumericStrings() throws {
        let value = try MJ.parse(Data(#"{"é":1,"e\u0301":2,"id":"9007199254740993"}"#.utf8))
        XCTAssertEqual(value.fields.count, 3)
        XCTAssertEqual(value["é"].int, 1); XCTAssertEqual(value["e\u{301}"].int, 2)
        XCTAssertEqual(try MusicPlatformProtocol.numericID(value["id"]), "9007199254740993")
        XCTAssertThrowsError(try MJ.parse(Data("9007199254740993".utf8)))
        XCTAssertThrowsError(try MJ.parse(Data(#"{"a":1,"a":2}"#.utf8)))
    }
    func testCASConflictAndStaleCollectionRevisionKeepProtectedBytesUnchanged() async throws {
        let store = NativeMusicMemoryDocument(), service = try await NativeMusicFixture.service(store)
        let c = try await service.createPlaylist(name: "One", refs: [])
        _ = try await service.editPlaylist(c, name: "Two", refs: nil, order: nil)
        let before = store.snapshot().0
        do { try await service.removePlaylist(c); XCTFail("stale revision") }
        catch { XCTAssertEqual(error as? MusicServiceError, .revisionConflict) }
        XCTAssertEqual(before, store.snapshot().0)
        store.fail(.storageConflict)
        do { _ = try await service.createPlaylist(name: "Conflict", refs: []); XCTFail("CAS conflict") }
        catch { XCTAssertEqual(error as? MusicServiceError, .storageConflict) }
        XCTAssertEqual(before, store.snapshot().0)
    }
    func testAllOffReadRepairDoesNotAcceptAllOffUserWrite() async throws {
        let service = try await NativeMusicFixture.service()
        do { _ = try await service.updateSettings(.init(ifRevision: 0, qualityEnabled: [], spatialEnabled: [])); XCTFail("all off") }
        catch { XCTAssertEqual(error as? MusicServiceError, .qualityDisabled) }
        var prefs = MusicModel.playback; prefs["qualitySettings"] = .obj([("qualityEnabled", .array([])), ("spatialEnabled", .array([]))])
        let policy = MusicModel.policy(prefs); XCTAssertEqual(policy.enabled, MusicModel.qualityOrder)
    }
    func testOnlyWholeQueueReplaceArchivesAndEndedAdvanceIsSingleUse() async throws {
        let scripts = NativeMusicScriptFixture(), service = try await NativeMusicFixture.service(scripts: scripts)
        let page = try await service.search(platform: "tx", query: "A", page: 1), ref = page.items[0].ref
        var state = try await service.changeQueue(.replace([ref, ref], index: 0), expectedRevision: 0)
        state = try await service.changeQueue(.append([ref], next: true), expectedRevision: state.revision)
        let empty = try await service.playedPlaylists(); XCTAssertTrue(empty.isEmpty)
        state = try await service.changeQueue(.replace([ref], index: 0), expectedRevision: state.revision)
        let archives = try await service.playedPlaylists(); XCTAssertEqual(archives.count, 1); XCTAssertEqual(archives[0].count, 3)
        let options = try await service.playbackOptions(for: ref)
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
