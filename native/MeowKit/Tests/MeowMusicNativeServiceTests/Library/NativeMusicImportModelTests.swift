import XCTest
import MeowMusicFeature
@testable import MeowMusicNativeService

final class NativeMusicImportModelTests: XCTestCase {
    // Synthetic current-library vectors specified independently of the implementation.
    // SHA-256 was frozen with Python hashlib, not by running the reference JS.
    private let batch = "e17cfc9a1761babe8423d1054deb3e11edfa471db165957ce2ab42225464d06f"
    private let after = "a95fe60872976c86cae17c660e594dd47bc890a1b6c4d632112922ab19829b53"
    private let collectionsJSON = #"""
    [{"id":"favorites","kind":"favorites","name":"Favorites","order":["2"],"revision":3,"manualOrder":false,"bindings":[],"intents":{}},{"id":"playlist:1","kind":"playlist","name":"Saved","order":["10","2"],"revision":4,"manualOrder":true,"locallyCreated":true,"bindings":[{"platform":"wy","accountId":"fixture-owner","id":"fixture-list","writable":false,"baseline":["77"],"map":{"77":"2"},"name":"Saved"}],"intents":{"2":{"present":true,"pending":true,"origin":"local","acknowledged":["[\"wy\",\"fixture-owner\",\"fixture-list\"]"]}}},{"id":"playlist:2","kind":"playlist","name":"Removed","order":[],"revision":1,"manualOrder":true,"deleted":true,"bindings":[],"intents":{}}]
    """#
    private let expectedOnline = #"""
    {"songId":"2","title":"Frozen track","artist":"Fixture","album":"","duration":180,"cover_url":"","sources":[{"sourceKey":"[\"wy\",\"77\",\"\"]","source":"wy","musicInfo":{"source":"wy","songmid":"77","name":"Frozen track","singer":"Fixture","duration":180,"types":[{"type":"320k","hash":"ABC","size":100}]}}]}
    """#
    private let expectedLocal = #"""
    {"songId":"10","title":"Frozen local","artist":"Fixture","album":"","duration":0,"cover_url":"","sources":[]}
    """#
    private func fixture(prepared: Bool = false) throws -> MJ {
        let songs = MJ.obj([("10", try MJ.parse(Data(expectedLocal.utf8))), ("2", try MJ.parse(Data(expectedOnline.utf8)))])
        return .obj([("version", .n(1)), ("ownerId", .string("frozen-owner")), ("revision", .n(7)), ("nextId", .n(3)),
            ("songs", songs), ("accounts", .obj([("wy", .string("fixture-owner"))])),
            ("collections", try MJ.parse(Data(collectionsJSON.utf8))),
            ("status", prepared ? .obj([("phase", .string("blocked")), ("at", .n(0)),
                ("issues", .array([.obj([("code", .string("import_requires_rebinding"))])]))]) :
                .obj([("phase", .string("synced")), ("at", .n(42)), ("issues", .array([]))]))])
    }
    func testCompactSongCleaningKeepsIDsAndFiltersUnknownNativeQualityFields() throws {
        let expected = try fixture()
        for id in ["2", "10"] {
            let clean = try MusicModel.cleanSong(expected["songs"][id])
            XCTAssertEqual(clean.stable, expected["songs"][id].stable)
            XCTAssertEqual(try MusicModel.cleanSong(clean).stable, clean.stable)
        }
        var online = expected["songs"]["2"], sources = online["sources"].array
        var qualities = sources[0]["musicInfo"]["types"].array
        qualities[0]["discarded"] = .string("not-durable")
        qualities.append(.obj([("type", .string("obsolete"))]))
        sources[0]["musicInfo"]["types"] = .array(qualities)
        online["sources"] = .array(sources)
        XCTAssertEqual(try MusicModel.cleanSong(online).stable, expected["songs"]["2"].stable)
        XCTAssertEqual(MusicModel.publicURL(.string("https://localhost./cover.jpg")),
                       .string("https://localhost./cover.jpg"))
    }
    func testImportIgnoresAdditionalSongFieldsWithoutChangingCurrentData() async throws {
        let current = try fixture()
        var input = current
        for id in ["2", "10"] {
            var song = input["songs"][id]
            song["extension"] = .obj([("title", .string("Unrelated title")), ("sources", .strings(["unrelated"]))])
            song["annotation"] = .string("Not part of the recording")
            song["displayOrder"] = .n(42)
            input["songs"][id] = song
        }
        let bytes = input.bytes, store = NativeMusicMemoryDocument()
        let service = try await NativeMusicFixture.service(store)
        let preview = try await service.prepareImport(data: bytes, sourceId: "fixed-contract", ownerId: "frozen-owner")
        XCTAssertEqual(preview.report.batchId, batch)
        XCTAssertEqual(preview.report.songs, 2)
        XCTAssertNil(store.snapshot().0); XCTAssertEqual(store.snapshot().2, 0)
        _ = try await service.commitImport(preview)
        let saved = try MJ.parse(XCTUnwrap(store.snapshot().0))["records"][MusicModel.key("library")]
        XCTAssertEqual(saved["library"]["songs"].stable, current["songs"].stable)
        XCTAssertEqual(saved["receipt"]["afterHash"].string, after)
        XCTAssertEqual(input.bytes, bytes)
        await service.close()

        let before = store.snapshot(), reopened = try await NativeMusicFixture.service(store)
        let again = try await reopened.prepareImport(data: current.bytes, sourceId: "fixed-contract", ownerId: "frozen-owner")
        XCTAssertTrue(again.report.alreadyApplied)
        XCTAssertEqual(again.report.batchId, batch)
        let collections = try await reopened.collections()
        let tracks = try await reopened.tracks(in: collections[1])
        XCTAssertEqual(tracks.map(\.songId), ["10", "2"])
        XCTAssertTrue(tracks[0].sources.isEmpty)
        XCTAssertEqual(tracks[1].sources.count, 1)
        XCTAssertEqual(store.snapshot().0, before.0); XCTAssertEqual(store.snapshot().2, before.2)
        await reopened.close()
    }
    func testImportRequiresSongFieldsWithoutTargetWrites() async throws {
        let current = try fixture(), compact = current["songs"]["2"]
        let store = NativeMusicMemoryDocument(), service = try await NativeMusicFixture.service(store)
        for field in ["songId", "title", "artist", "album", "duration", "sources"] {
            var song = compact; song.remove(field)
            var input = current; input["songs"]["2"] = song
            do {
                _ = try await service.prepareImport(data: input.bytes, sourceId: "fixed-contract", ownerId: "frozen-owner")
                XCTFail("Song missing \(field) was accepted")
            } catch {
                XCTAssertEqual(error as? MusicServiceError, field == "songId" ? .songIDConflict : .corruptDocument)
            }
            XCTAssertNil(store.snapshot().0); XCTAssertEqual(store.snapshot().2, 0)
        }
        await service.close()
    }
    func testSongShapeAndSourceArrayTypesAreValidated() async throws {
        for raw in [MJ.null, .array([]), .string("not a song"), .n(1)] {
            XCTAssertThrowsError(try MusicModel.cleanSong(raw)) {
                XCTAssertEqual($0 as? MusicServiceError, .corruptDocument)
            }
        }
        let current = try fixture(), store = NativeMusicMemoryDocument()
        let service = try await NativeMusicFixture.service(store)
        for sources in [MJ.null, .obj(), .string("not an array"), .n(0)] {
            var input = current; input["songs"]["2"]["sources"] = sources
            do {
                _ = try await service.prepareImport(data: input.bytes, sourceId: "fixed-contract", ownerId: "frozen-owner")
                XCTFail("Invalid source array was accepted")
            } catch { XCTAssertEqual(error as? MusicServiceError, .invalidImport) }
            XCTAssertNil(store.snapshot().0); XCTAssertEqual(store.snapshot().2, 0)
        }
        await service.close()
    }
    func testImportGoldenBatchReceiptMetadataAndRollback() async throws {
        let store = NativeMusicMemoryDocument(), service = try await NativeMusicFixture.service(store)
        let raw = try fixture()
        let preview = try await service.prepareImport(data: raw.bytes, sourceId: "fixed-contract", ownerId: "frozen-owner")
        XCTAssertEqual(preview.report.batchId, batch)
        _ = try await service.commitImport(preview)
        let saved = try MJ.parse(XCTUnwrap(store.snapshot().0))
        let library = saved["records"][MusicModel.key("library")]
        XCTAssertEqual(library["receipt"]["afterHash"].string, after)
        XCTAssertEqual(library["receipt"]["sourceOwnerId"].string, "frozen-owner")
        XCTAssertEqual(library["library"]["collections"], raw["collections"])
        XCTAssertEqual(library["library"]["accounts"], raw["accounts"])
        XCTAssertEqual(library["library"]["songs"]["2"]["songId"].string, "2")
        let again = try await service.prepareImport(data: raw.stable.bytes, sourceId: "fixed-contract", ownerId: "frozen-owner")
        XCTAssertTrue(again.report.alreadyApplied)
        try await service.rollbackImport(batchId: batch)
        let empty = try await service.collections()
        XCTAssertEqual(empty.count, 1); XCTAssertEqual(empty[0].count, 0)
    }
    func testCurrentImportCASFailureKeepsTargetAndPreviewAvailableForExplicitRetry() async throws {
        let store = NativeMusicMemoryDocument(), service = try await NativeMusicFixture.service(store)
        let source = try fixture().bytes
        let preview = try await service.prepareImport(data: source, sourceId: "fixed-contract", ownerId: "frozen-owner")
        store.fail(.storageConflict)
        do { _ = try await service.commitImport(preview); XCTFail("Failed CAS cannot apply an import") }
        catch { XCTAssertEqual(error as? MusicServiceError, .storageConflict) }
        XCTAssertNil(store.snapshot().0); XCTAssertEqual(store.snapshot().2, 0)
        store.fail(nil)
        let report = try await service.commitImport(preview)
        XCTAssertFalse(report.alreadyApplied); XCTAssertEqual(report.batchId, batch)
        XCTAssertEqual(store.snapshot().2, 1)
        XCTAssertEqual(source, try fixture().bytes)
        await service.close()
    }
    func testCurrentCompactReceiptIsNotRewrittenOrReimportedUnderNewIdentity() async throws {
        var library = try fixture(prepared: true); library["ownerId"] = .string("device")
        XCTAssertEqual(library.stable.digest, after)
        let area = MJ.obj([("schema", .n(1)), ("scope", .string("device")), ("revision", .n(1)), ("library", library),
            ("receipt", .obj([("batchId", .string(batch)), ("sourceId", .string("fixed-contract")), ("sourceOwnerId", .string("frozen-owner")),
                ("appliedAt", .n(1234)), ("appliedRevision", .n(1)), ("afterHash", .string(after))]))])
        let bytes = MJ.obj([("version", .n(1)), ("scope", .string("device")), ("revision", .n(1)),
            ("records", .obj([(MusicModel.key("library"), area)]))]).bytes
        let store = NativeMusicMemoryDocument(bytes), service = try await NativeMusicFixture.service(store)
        let preview = try await service.prepareImport(data: fixture().bytes, sourceId: "fixed-contract", ownerId: "frozen-owner")
        XCTAssertTrue(preview.report.alreadyApplied)
        XCTAssertEqual(store.snapshot().0, bytes); XCTAssertEqual(store.snapshot().2, 0)
        let collections = try await service.collections()
        XCTAssertEqual(collections.count, 2, "tombstone remains stored, not visible")
        let tracks = try await service.tracks(in: collections[1])
        XCTAssertEqual(tracks.map(\.songId), ["10", "2"])
        XCTAssertTrue(tracks[0].sources.isEmpty, "local metadata is not a fabricated LX source")
    }
    func testStableJSONEnumeratesIndexKeysAndUsesECMAScriptSmallNumbers() throws {
        let value = try MJ.parse(Data(#"{"10":"ten","2":"two","01":"string","4294967295":"string","a":1e-7,"b":-1e-7,"c":1e-6}"#.utf8))
        XCTAssertEqual(value.stable.json, #"{"2":"two","10":"ten","01":"string","4294967295":"string","a":1e-7,"b":-1e-7,"c":0.000001}"#)
    }
    func testDurableFiltersSecretAndTransientFieldsWithoutLosingOrdinaryFields() {
        let value = MJ.obj([
            ("x-api-key", .string("secret")), ("apiKeyValue", .string("secret")),
            ("Headers", .string("secret")), ("accessToken", .string("secret")),
            ("AudioUrl", .string("https://example.invalid/audio")),
            ("headersCount", .string("kept")), ("normal", .string("kept"))
        ])
        let clean = MusicModel.durable(value)
        XCTAssertEqual(Set(clean.fields.map(\.0)), ["headersCount", "normal"])
        XCTAssertEqual(MusicModel.durable(.string("HTTPS://host/a?Token=1")), .string(""))
        XCTAssertEqual(MusicModel.durable(.string("blob:https://host/a")), .string(""))
        XCTAssertEqual(MusicModel.durable(.string("DATA:text/plain,secret")), .string(""))
    }
    func testConflictingNativeRecordingIsRejectedWithoutDurableQuarantineTable() async throws {
        var library = try fixture(prepared: true)
        var accepted = library["songs"]["2"]["sources"].array
        accepted[0]["musicInfo"]["singer"] = .string("Different recording")
        library["songs"]["2"]["sources"] = .array(accepted)
        let store = NativeMusicMemoryDocument(), service = try await NativeMusicFixture.service(store)
        do { _ = try await service.prepareImport(data: library.bytes, sourceId: "fixed", ownerId: "frozen-owner"); XCTFail("conflicting source") }
        catch { XCTAssertEqual(error as? MusicServiceError, .nativeIDConflict) }
        XCTAssertNil(store.snapshot().0)
    }
    func testDuplicateSourceKeysRemainRejectedAfterOrdering() throws {
        let song = try NativeMusicFixture.song()
        let source = try XCTUnwrap(song["sources"].array.first)
        var duplicate = song
        duplicate["sources"] = .array([source, source])
        XCTAssertThrowsError(try MusicModel.cleanSong(duplicate)) {
            XCTAssertEqual($0 as? MusicServiceError, .nativeIDConflict)
        }
    }
    func testCurrentCompactSourceWithoutSongmidFailsAsInvalidSource() throws {
        var song = try NativeMusicFixture.song()
        _ = try MusicModel.cleanSong(song)
        var source = try XCTUnwrap(song["sources"].array.first)
        var info = source["musicInfo"]
        info.remove("songmid")
        source["musicInfo"] = info
        song["sources"] = .array([source])
        XCTAssertThrowsError(try MusicModel.cleanSong(song)) {
            XCTAssertEqual($0 as? MusicServiceError, .sourceUnavailable)
        }
        XCTAssertThrowsError(try MusicModel.cleanSource(source)) {
            XCTAssertEqual($0 as? MusicServiceError, .sourceUnavailable)
        }
    }
}
