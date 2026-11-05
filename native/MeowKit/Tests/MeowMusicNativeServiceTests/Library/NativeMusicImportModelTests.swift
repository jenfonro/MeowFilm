import XCTest
import MeowMusicFeature
@testable import MeowMusicNativeService

final class NativeMusicImportModelTests: XCTestCase {
    // Synthetic current-library vectors specified independently of the implementation.
    // SHA-256 was frozen with Python hashlib, not by running the reference JS.
    private let collectionsJSON = #"""
    [{"id":"favorites","kind":"favorites","name":"Favorites","order":["2"],"revision":3,"manualOrder":false,"bindings":[],"intents":{}},{"id":"playlist:1","kind":"playlist","name":"Saved","order":["10","2"],"revision":4,"manualOrder":true,"locallyCreated":true,"bindings":[{"platform":"wy","accountId":"fixture-owner","id":"fixture-list","writable":false,"baseline":["77"],"map":{"77":"2"},"name":"Saved"}],"intents":{"2":{"present":true,"pending":true,"origin":"local","acknowledged":["[\"wy\",\"fixture-owner\",\"fixture-list\"]"]}}},{"id":"playlist:2","kind":"playlist","name":"Removed","order":[],"revision":1,"manualOrder":true,"deleted":true,"bindings":[],"intents":{}}]
    """#
    private let expectedOnline = #"""
    {"songId":"2","title":"Frozen track","artist":"Fixture","album":"","duration":180,"cover_url":"","sources":[{"sourceKey":"[\"wy\",\"77\",\"\"]","source":"wy","musicInfo":{"source":"wy","songmid":"77","name":"Frozen track","singer":"Fixture","duration":180,"types":[{"type":"320k","hash":"ABC","size":100}]}}]}
    """#
    private let expectedLocal = #"""
    {"songId":"10","title":"Frozen local","artist":"Fixture","album":"","duration":0,"cover_url":"","sources":[]}
    """#
    private func fixture() throws -> MJ {
        let songs = MJ.obj([("10", try MJ.parse(Data(expectedLocal.utf8))), ("2", try MJ.parse(Data(expectedOnline.utf8)))])
        return .obj([("version", .n(1)), ("ownerId", .string("frozen-owner")), ("revision", .n(7)), ("nextId", .n(3)),
            ("songs", songs), ("accounts", .obj([("wy", .string("fixture-owner"))])),
            ("collections", try MJ.parse(Data(collectionsJSON.utf8))),
            ("status", .obj([("phase", .string("synced")), ("at", .n(42)), ("issues", .array([]))]))])
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
