import XCTest
import MeowMusicFeature
@testable import MeowMusicNativeService

final class NativeMusicStorageTests: XCTestCase {
    func testOversizedDocumentDropsOldestHistoryArchivesBeforeReportingLimit() throws {
        var document = try MusicDocument(nil, runID: "storage")
        let padding = String(repeating: "x", count: 5 * 1024 * 1024)
        let archives = ["newest", "oldest"].map {
            MJ.obj([("id", .string($0)), ("title", .string("Archive")),
                    ("at", .number(NativeMusicFixture.date.timeIntervalSince1970 * 1000)),
                    ("entries", .array([])), ("currentId", .null), ("priority", .array([])),
                    ("history", .array([])), ("source", .null), ("padding", .string(padding))])
        }
        document.history["playlists"] = .array(archives)
        let data = try document.encode(changed: ["history"])
        XCTAssertLessThanOrEqual(data.count, 8 * 1024 * 1024)
        let saved = try MJ.parse(data)
        let retained = saved["records"][MusicModel.key("history")]["playlists"].array
        XCTAssertEqual(retained.map { $0["id"].string }, ["newest"])
        XCTAssertEqual(document.history["playlists"].array.map { $0["id"].string }, ["newest"])
        XCTAssertEqual(saved["records"][MusicModel.key("history")]["days"].array.count, 0)
    }

    func testDocumentStillOverLimitAfterDiscardingHistoryReportsLimit() throws {
        var document = try MusicDocument(nil, runID: "storage")
        document.history["playlists"] = .array([
            MJ.obj([("id", .string("archive")), ("title", .string("Archive")),
                    ("at", .number(NativeMusicFixture.date.timeIntervalSince1970 * 1000)),
                    ("entries", .array([])), ("currentId", .null), ("priority", .array([])),
                    ("history", .array([])), ("source", .null),
                    ("padding", .string(String(repeating: "x", count: 3 * 1024 * 1024)))])
        ])
        document.history["days"] = .array([
            MJ.obj([("date", .string("2026-10-23")), ("songs", .array([])),
                    ("padding", .string(String(repeating: "x", count: 9 * 1024 * 1024)))])
        ])
        XCTAssertThrowsError(try document.encode(changed: ["history"])) {
            XCTAssertEqual($0 as? MusicServiceError, .limit)
        }
        XCTAssertTrue(document.history["playlists"].array.isEmpty)
    }

    func testQueueOnlyTransactionEvictsOldestArchiveAndKeepsOtherAreasAtomic() async throws {
        var document = try MusicDocument(nil, runID: "storage")
        let padding = String(repeating: "x", count: 3 * 1024 * 1024)
        document.history["playlists"] = .array(["newest", "oldest"].map {
            .obj([("id", .string($0)), ("title", .string("Archive")),
                  ("at", .number(NativeMusicFixture.date.timeIntervalSince1970 * 1000)),
                  ("entries", .array([])), ("currentId", .null), ("priority", .array([])),
                  ("history", .array([])), ("source", .null), ("padding", .string(padding))])
        })
        let song = try NativeMusicFixture.song()
        document.history["days"] = .array([.obj([
            ("date", .string("2026-10-23")),
            ("songs", .array([.obj([("song", song), ("context", .obj()), ("at", .n(1))])]))
        ])])
        let original = try document.encode(changed: ["library", "queue", "history", "playback"])
        let before = try MJ.parse(original)
        let store = NativeMusicMemoryDocument(original), service = try await NativeMusicFixture.service(store)
        let context = MJ.obj([("sourceTitle", .string(String(repeating: "q", count: 1024)))])
        let entries = (1...2001).map {
            MJ.obj([("id", .string("entry-\($0)")), ("key", song["songId"]),
                    ("song", song), ("context", context)])
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
        let saved = try MJ.parse(XCTUnwrap(store.snapshot().0)), records = saved["records"]
        XCTAssertEqual(records[MusicModel.key("queue")]["entries"].array.count, 2001)
        XCTAssertEqual(records[MusicModel.key("history")]["playlists"].array.map { $0["id"].string }, ["newest"])
        XCTAssertEqual(records[MusicModel.key("history")]["days"], before["records"][MusicModel.key("history")]["days"])
        for area in ["library", "playback"] { XCTAssertEqual(records[MusicModel.key(area)], before["records"][MusicModel.key(area)]) }
        XCTAssertEqual(saved["revision"].int, before["revision"].int + 1)
        XCTAssertEqual(store.snapshot().2, 1)
        XCTAssertLessThanOrEqual(try XCTUnwrap(store.snapshot().0).count, 8 * 1024 * 1024)
        await service.close()
    }

    func testCompactSourcesRejectInvalidDurationBeforeRecordingComparison() throws {
        let original = try XCTUnwrap(NativeMusicFixture.song()["sources"].array.first)
        let invalid: [MJ] = [.n(-1), .n(604_801), .string("-1"), .string("NaN"), .string("Infinity"),
                             .string("1e999"), .bool(true), .obj(), .array([])]
        for value in invalid {
            var source = original
            source["musicInfo"]["duration"] = value
            XCTAssertThrowsError(try MusicModel.cleanSource(source)) {
                XCTAssertEqual($0 as? MusicServiceError, .invalidImport)
            }
        }
        let valid: [MJ] = [.n(0), .n(604_800), .string("180")]
        for value in valid {
            var source = original
            source["musicInfo"]["duration"] = value
            XCTAssertNoThrow(try MusicModel.cleanSource(source))
        }
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
    func testParsedDocumentMemoryNeverMasksChangedCorruptBytes() async throws {
        let store = NativeMusicMemoryDocument(), service = try await NativeMusicFixture.service(store)
        var document = try await service.read()
        let valid = try document.encode(changed: [])
        _ = try await service.state()
        _ = try await service.queue()
        let corrupt = Data(#"{"version":1,"scope":"device","revision":0,"records":{"unknown":{}}}"#.utf8)
        store.replace(corrupt)
        do { _ = try await service.queue(); XCTFail("Changed corrupt bytes must be revalidated") }
        catch { XCTAssertEqual(error as? MusicServiceError, .corruptDocument) }
        store.replace(valid)
        do { _ = try await service.queue() }
        catch { XCTFail("Restored valid bytes must be readable: \(error)") }
        await service.close()
    }
    func testInvalidQueueSerialAndPointersAreRejectedWithoutReadRepair() async throws {
        let song = try NativeMusicFixture.song()
        let invalid: [(String, MJ)] = [
            ("serial", .n(0)), ("currentId", .string("entry-99")),
            ("priority", .strings(["entry-1"])), ("history", .strings(["entry-99"])),
            ("failed", .strings(["entry-99"]))
        ]
        for (field, value) in invalid {
            var document = try MusicDocument(nil, runID: "fixture")
            document.queue["entries"] = .array([.obj([("id", .string("entry-1")), ("song", song)])])
            document.queue["currentId"] = .string("entry-1"); document.queue["serial"] = .n(1)
            document.queue[field] = value
            let bytes = try document.encode(changed: ["queue"]), store = NativeMusicMemoryDocument(bytes)
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
        XCTAssertEqual(try MusicPlatformProtocol.numericID(value["id"]), "9007199254740993")
        XCTAssertEqual(try MJ.parse(Data("9007199254740993".utf8)).id, "9007199254740993")
        XCTAssertEqual(try MJ.parse(Data(#"{"a":1,"a":2}"#.utf8))["a"].int, 2)
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
