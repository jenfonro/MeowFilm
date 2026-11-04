import XCTest
import MeowMusicFeature
@testable import MeowMusicNativeService

final class NativeMusicLegacyTests: XCTestCase {
    // Synthetic import-to-compact-schema vectors specified independently of the implementation.
    // SHA-256 was frozen with Python hashlib, not by running the reference JS.
    private let batch = "1e5cb64f577b86519ad0399631d6ed068aab2cb37e492ffed07522af86a49eac"
    private let after = "f55d381355ebc62cac9cacfcc8c5774aff523e9b8b4aca5cb4018a58a5eaf740"
    private let collectionsJSON = #"""
    [{"id":"favorites","kind":"favorites","name":"Favorites","order":["2"],"revision":3,"manualOrder":false,"bindings":[],"intents":{},"creating":{}},{"id":"playlist:1","kind":"playlist","name":"Saved","order":["10","2"],"revision":4,"manualOrder":true,"locallyCreated":true,"bindings":[{"platform":"wy","accountId":"fixture-owner","id":"fixture-list","writable":false,"baseline":["77"],"map":{"2":"77"},"name":"Saved"}],"intents":{"2":{"present":true,"pending":true,"origin":"local","acknowledged":["[\"wy\",\"fixture-owner\",\"fixture-list\"]"]}},"creating":{"[\"wy\",\"fixture-owner\"]":{"before":["fixture-list"],"name":"Uncertain"}}},{"id":"playlist:2","kind":"playlist","name":"Removed","order":[],"revision":1,"manualOrder":true,"deleted":true,"bindings":[],"intents":{},"creating":{}}]
    """#
    private let expectedOnline = #"""
    {"songId":"2","title":"Frozen track","artist":"Fixture","album":"","duration":180,"cover_url":"","sources":[{"sourceKey":"[\"wy\",\"77\",\"\"]","source":"wy","musicInfo":{"source":"wy","songmid":"77","name":"Frozen track","singer":"Fixture","duration":180,"types":[{"type":"320k","hash":"ABC","size":100}]}}]}
    """#
    private let expectedLocal = #"""
    {"songId":"10","title":"Frozen local","artist":"Fixture","album":"","duration":0,"cover_url":"","sources":[]}
    """#
    private func fixture(normalized: Bool) throws -> MJ {
        let songs: MJ
        if normalized {
            songs = .obj([("10", try MJ.parse(Data(expectedLocal.utf8))), ("2", try MJ.parse(Data(expectedOnline.utf8)))])
        } else {
            let local = try MJ.parse(Data(#"{"songId":"10","id":10,"title":"Frozen local","artist":"Fixture","duration":0,"file_path":"Music/synthetic.wav","plugin_entry_path":"local"}"#.utf8))
            let native = #"{"source":"wy","songmid":"77","name":"Frozen track","singer":"Fixture","duration":180,"types":[{"type":"320k","hash":"ABC","size":100,"discarded":"not-durable"},{"type":"obsolete"}]}"#
            let online = MJ.obj([("songId", .string("2")), ("id", .n(23)), ("plugin_entry_path", .string("legacy-catalog")),
                                 ("musicInfo", try MJ.parse(Data(native.utf8)))])
            songs = .obj([("10", local), ("2", online)])
        }
        return .obj([("version", .n(1)), ("ownerId", .string("frozen-owner")), ("revision", .n(7)), ("nextId", .n(3)),
            ("songs", songs), ("accounts", .obj([("wy", .string("fixture-owner"))])),
            ("collections", try MJ.parse(Data(collectionsJSON.utf8))),
            ("status", normalized ? .obj([("phase", .string("blocked")), ("at", .n(0)),
                ("issues", .array([.obj([("code", .string("legacy_import_requires_rebinding"))])]))]) :
                .obj([("phase", .string("synced")), ("at", .n(42)), ("issues", .array([]))]))])
    }
    func testCompactSongNormalizationKeepsIDsAndFiltersUnknownQualityFields() throws {
        let raw = try fixture(normalized: false), expected = try fixture(normalized: true)
        for id in ["2", "10"] {
            let clean = try MusicModel.cleanSong(raw["songs"][id])
            XCTAssertEqual(clean.stable, expected["songs"][id].stable)
            XCTAssertEqual(try MusicModel.cleanSong(clean).stable, clean.stable)
        }
        let data = #"{"source":"wy","songmid":"77","name":"Frozen track","singer":"Fixture","duration":180}"#
        let fromFile = try MusicModel.cleanSong(.obj([("songId", .string("fixed-id")), ("musicInfo", try MJ.parse(Data(data.utf8)))]))
        XCTAssertEqual(fromFile["songId"].string, "fixed-id")
        XCTAssertEqual(fromFile["sources"].array.first?["musicInfo"]["songmid"].string, "77")
        XCTAssertEqual(fromFile["file_path"].string, "")
        XCTAssertEqual(MusicModel.publicURL(.string("https://localhost./cover.jpg")), .string(""))
    }
    func testImportGoldenBatchReceiptMetadataAndRollback() async throws {
        let store = NativeMusicMemoryDocument(), service = try await NativeMusicFixture.service(store)
        let raw = try fixture(normalized: false)
        let preview = try await service.prepareImport(data: raw.bytes, sourceId: "fixed-contract", ownerId: "frozen-owner")
        XCTAssertEqual(preview.report.batchId, batch)
        _ = try await service.commitImport(preview)
        let saved = try MJ.parse(XCTUnwrap(store.snapshot().0))
        let library = saved["records"][MusicModel.key("library")]
        XCTAssertEqual(library["receipt"]["afterHash"].string, after)
        XCTAssertEqual(library["library"]["collections"], raw["collections"])
        XCTAssertEqual(library["library"]["accounts"], raw["accounts"])
        XCTAssertEqual(library["library"]["songs"]["2"]["songId"].string, "2")
        XCTAssertFalse(saved.json.contains("not-durable"))
        let again = try await service.prepareImport(data: raw.stable.bytes, sourceId: "fixed-contract", ownerId: "frozen-owner")
        XCTAssertTrue(again.report.alreadyApplied)
        try await service.rollbackImport(batchId: batch)
        let empty = try await service.collections()
        XCTAssertEqual(empty.count, 1); XCTAssertEqual(empty[0].count, 0)
    }
    func testCurrentCompactReceiptIsNotRewrittenOrReimportedUnderNewIdentity() async throws {
        var legacy = try fixture(normalized: true); legacy["ownerId"] = .string("device")
        XCTAssertEqual(legacy.stable.digest, after)
        let area = MJ.obj([("schema", .n(1)), ("scope", .string("device")), ("revision", .n(1)), ("library", legacy),
            ("receipt", .obj([("batchId", .string(batch)), ("sourceId", .string("fixed-contract")), ("legacyOwnerId", .string("frozen-owner")),
                ("appliedAt", .n(1234)), ("appliedRevision", .n(1)), ("afterHash", .string(after))]))])
        let bytes = MJ.obj([("version", .n(1)), ("scope", .string("device")), ("revision", .n(1)),
            ("records", .obj([(MusicModel.key("library"), area)]))]).bytes
        let store = NativeMusicMemoryDocument(bytes), service = try await NativeMusicFixture.service(store)
        let preview = try await service.prepareImport(data: fixture(normalized: false).bytes, sourceId: "fixed-contract", ownerId: "frozen-owner")
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
        var legacy = try fixture(normalized: true)
        var accepted = legacy["songs"]["2"]["sources"].array
        accepted[0]["musicInfo"]["singer"] = .string("Different recording")
        legacy["songs"]["2"]["sources"] = .array(accepted)
        let store = NativeMusicMemoryDocument(), service = try await NativeMusicFixture.service(store)
        do { _ = try await service.prepareImport(data: legacy.bytes, sourceId: "fixed", ownerId: "frozen-owner"); XCTFail("conflicting source") }
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
