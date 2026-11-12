import XCTest
import MeowMusicFeature
@testable import MeowMusicNativeService

final class NativeMusicImportModelTests: XCTestCase {
    // Synthetic current-library vectors specified independently of the implementation.
    private let collectionsJSON = #"""
    [{"id":"favorites","kind":"favorites","name":"Favorites","order":["2"],"revision":3,"manualOrder":false,"bindings":[],"intents":{}},{"id":"playlist:1","kind":"playlist","name":"Saved","order":["10","2"],"revision":4,"manualOrder":true,"locallyCreated":true,"bindings":[{"platform":"wy","accountId":"fixture-owner","id":"fixture-list","writable":true,"baseline":["77"],"map":{"77":"2"},"name":"Saved"}],"intents":{"2":{"present":true,"pending":true,"origin":"local","acknowledged":["[\"wy\",\"fixture-owner\",\"fixture-list\"]"]}}},{"id":"playlist:2","kind":"playlist","name":"Removed","order":[],"revision":1,"manualOrder":true,"deleted":true,"bindings":[],"intents":{}}]
    """#
    private let expectedOnline = #"""
    {"songId":"2","title":"Frozen track","artist":"Fixture","album":"","duration":180,"cover_url":"","sources":[{"sourceKey":"[\"wy\",\"77\",\"\"]","source":"wy","musicInfo":{"source":"wy","songmid":"77","name":"Frozen track","singer":"Fixture","duration":180,"types":[{"type":"320k","hash":"ABC","size":100}]}}]}
    """#
    private let expectedLocal = #"""
    {"songId":"10","title":"Frozen local","artist":"Fixture","album":"","duration":0,"cover_url":"","sources":[]}
    """#
    private func fixture() throws -> MJ {
        let songs = MJ.obj([("10", try MJ.parse(Data(expectedLocal.utf8))), ("2", try MJ.parse(Data(expectedOnline.utf8)))])
        return .obj([("version", .n(2)), ("ownerId", .string("frozen-owner")), ("revision", .n(7)), ("nextId", .n(3)),
            ("songs", songs), ("accounts", .obj([("wy", .string("fixture-owner"))])),
            ("collections", try MJ.parse(Data(collectionsJSON.utf8))),
            ("networkReferences", .array([])),
            ("status", .obj([("phase", .string("synced")), ("at", .n(42)), ("issues", .array([]))]))])
    }
    /// The fixture library as the persisted device document's library area.
    private func stored(_ library: MJ) -> Data {
        var library = library; library["ownerId"] = .string("device")
        let area = MJ.obj([("schema", .n(2)), ("scope", .string("device")), ("revision", .n(1)), ("library", library)])
        return MJ.obj([("version", .n(1)), ("scope", .string("device")), ("revision", .n(1)),
            ("records", .obj([(MusicModel.key("library"), area)]))]).bytes
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
    func testCleanSongIgnoresAdditionalSongFieldsWithoutChangingCurrentData() throws {
        let current = try fixture()
        for id in ["2", "10"] {
            var song = current["songs"][id]
            song["extension"] = .obj([("title", .string("Unrelated title")), ("sources", .strings(["unrelated"]))])
            song["annotation"] = .string("Not part of the recording")
            song["displayOrder"] = .n(42)
            XCTAssertEqual(try MusicModel.cleanSong(song).stable, current["songs"][id].stable)
        }
    }
    func testStoredCurrentLibraryLoadsWithoutRewriteOrVisibleTombstone() async throws {
        let bytes = stored(try fixture())
        let store = NativeMusicMemoryDocument(bytes), service = try await NativeMusicFixture.service(store)
        let collections = try await service.collections()
        XCTAssertEqual(collections.count, 2, "tombstone remains stored, not visible")
        let tracks = try await service.tracks(in: collections[1])
        XCTAssertEqual(tracks.map(\.songId), ["10", "2"])
        XCTAssertTrue(tracks[0].sources.isEmpty, "local metadata is not a fabricated LX source")
        XCTAssertEqual(tracks[1].sources.count, 1)
        XCTAssertEqual(store.snapshot().0, bytes); XCTAssertEqual(store.snapshot().2, 0)
        await service.close()
    }
    func testStoredSongMissingFieldsOrConflictingRecordingFailsWithoutWrite() async throws {
        let current = try fixture()
        var cases = ["songId", "title", "artist", "album", "duration", "sources"].map { field -> (String, MJ, MusicServiceError) in
            var song = current["songs"]["2"]; song.remove(field)
            return ("missing " + field, song, field == "songId" ? .songIDConflict : .corruptDocument)
        }
        var conflicting = current["songs"]["2"], sources = conflicting["sources"].array
        sources[0]["musicInfo"]["singer"] = .string("Different recording")
        conflicting["sources"] = .array(sources)
        cases.append(("conflicting recording", conflicting, .nativeIDConflict))
        for (label, song, expected) in cases {
            var library = current; library["songs"]["2"] = song
            let bytes = stored(library)
            let store = NativeMusicMemoryDocument(bytes), service = try await NativeMusicFixture.service(store)
            do { _ = try await service.collections(); XCTFail("Stored song accepted: \(label)") }
            catch { XCTAssertEqual(error as? MusicServiceError, expected, label) }
            XCTAssertEqual(store.snapshot().0, bytes, label); XCTAssertEqual(store.snapshot().2, 0, label)
            await service.close()
        }
    }
    func testStoredDocumentRejectsSecretFieldNamesInAnyAreaWithoutWrite() async throws {
        let current = try fixture()
        // An ordinary additional song field still loads, so the rejections
        // below come from the secret-name rule rather than a closed field set.
        var ordinary = current; ordinary["songs"]["2"]["annotation"] = .string("synthetic-note")
        let ordinaryBytes = stored(ordinary)
        let ordinaryStore = NativeMusicMemoryDocument(ordinaryBytes)
        let loaded = try await NativeMusicFixture.service(ordinaryStore)
        let collections = try await loaded.collections(); XCTAssertEqual(collections.count, 2)
        XCTAssertEqual(ordinaryStore.snapshot().0, ordinaryBytes); XCTAssertEqual(ordinaryStore.snapshot().2, 0)
        await loaded.close()
        var cases: [(String, Data)] = []
        var song = current; song["songs"]["2"]["cookie"] = .string("synthetic-private")
        cases.append(("song cookie", stored(song)))
        var nested = current, sources = nested["songs"]["2"]["sources"].array
        sources[0]["musicInfo"]["accessToken"] = .string("synthetic-private")
        nested["songs"]["2"]["sources"] = .array(sources)
        cases.append(("source accessToken", stored(nested)))
        var document = try MJ.parse(stored(current)), history = MusicModel.history
        history["cookie"] = .string("synthetic-private")
        document["records"][MusicModel.key("history")] = history
        cases.append(("history cookie", document.bytes))
        for (label, bytes) in cases {
            let store = NativeMusicMemoryDocument(bytes), service = try await NativeMusicFixture.service(store)
            do { _ = try await service.collections(); XCTFail("Stored secret accepted: \(label)") }
            catch { XCTAssertEqual(error as? MusicServiceError, .corruptDocument, label) }
            XCTAssertEqual(store.snapshot().0, bytes, label); XCTAssertEqual(store.snapshot().2, 0, label)
            await service.close()
        }
    }
    func testSongShapeAndSourceArrayTypesAreValidated() throws {
        for raw in [MJ.null, .array([]), .string("not a song"), .n(1)] {
            XCTAssertThrowsError(try MusicModel.cleanSong(raw)) {
                XCTAssertEqual($0 as? MusicServiceError, .corruptDocument)
            }
        }
        let current = try fixture()
        for sources in [MJ.null, .obj(), .string("not an array"), .n(0)] {
            var song = current["songs"]["2"]; song["sources"] = sources
            XCTAssertThrowsError(try MusicModel.cleanSong(song)) {
                XCTAssertEqual($0 as? MusicServiceError, .invalidImport)
            }
        }
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
