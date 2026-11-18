import Foundation
import XCTest
import MeowMusicFeature
import MeowMusicTestSupport
import MeowRuntime
@testable import MeowMusicNativeService

final class MusicNativeUpstreamNumberTests: XCTestCase {
    func testCanonicalLongPlatformLocatorsRemainExactThroughRepositoryAndColdRead() async throws {
        for platform in ["wy", "kg"] {
            let nativeID = "9007199254740993" + String(repeating: "1", count: 80)
            var info = try MJ.parse(MusicNativeCandidateDiscoveryFixture.track(platform, id: nativeID).data)
            info["albumId"] = .string("18446744073709551615")
            if platform == "kg" { info["hash"] = .string("1234567890ABCDEF1234567890ABCDEF"); info["albumAudioId"] = .string("9007199254740995") }
            let peer = NativeMusicResultFixture([try MusicPlatformFixture.page([.init(platform: platform, data: info.bytes)], platform: platform)])
            let store = NativeMusicTestLibrary(), service = try await NativeMusicFixture.service(store, platform: peer)
            let page = try await service.search(platform: platform, query: "Synthetic", page: 1, limit: 30)
            let track = try XCTUnwrap(page.items.first)
            let collection = try await service.createPlaylist(name: "Long locators", refs: [track.ref])
            await service.close()
            let reopened = try await NativeMusicFixture.service(store)
            let restored = try await reopened.tracks(in: collection)
            XCTAssertEqual(restored[0].songId, track.songId); XCTAssertEqual(restored[0].sources[0].nativeId, nativeID)
            let document = try MusicDocument(store.snapshot().0, runID: "inspect")
            let saved = try XCTUnwrap((try document.fixtureSong(track.songId))["sources"].array.first)["musicInfo"]
            XCTAssertEqual(saved["albumId"].string, "18446744073709551615")
            if platform == "kg" { XCTAssertEqual(saved["albumAudioId"].string, "9007199254740995") }
            await reopened.close()
        }
    }

    func testCanonicalOptionalAlbumAndArtworkAbsenceDoesNotLoseSongs() async throws {
        let rows = (1...31).map { MusicNativeCandidateDiscoveryFixture.track("wy", id: String($0), title: "Song \($0)") }
        let peer = NativeMusicResultFixture([try MusicPlatformFixture.page(rows, platform: "wy", total: 20)])
        let service = try await NativeMusicFixture.service(platform: peer)
        let page = try await service.search(platform: "wy", query: "Synthetic", page: 1, limit: 30)
        XCTAssertEqual(page.items.count, 31); XCTAssertEqual(page.items.map { $0.sources[0].nativeId }, (1...31).map(String.init))
        XCTAssertTrue(page.items.allSatisfy { $0.artwork == nil })
        await service.close()
    }

    func testMissingCanonicalLocatorKeepsDisplayAndOnlyAdoptsIdentifiedRows() async throws {
        let valid = try MusicPlatformFixture.song(NativeMusicFixture.track())
        let peer = NativeMusicResultFixture([.object(["items": .array([valid, .object(["sources": .array([])])]), "total": .number(2)])])
        let store = NativeMusicTestLibrary(), service = try await NativeMusicFixture.service(store, platform: peer)
        let page = try await service.search(platform: "tx", query: "Synthetic", page: 1, limit: 30)
        XCTAssertEqual(page.items.count, 2)
        XCTAssertFalse(page.items[0].songId.isEmpty); XCTAssertTrue(page.items[1].songId.isEmpty)
        XCTAssertTrue(page.items[1].sources.isEmpty)
        let state = try await service.read()
        XCTAssertEqual(try state.songRows.allSongIDs().count, 1)
        await service.close()
    }

    func testUnconsumedNestedCanonicalMetadataDoesNotRejectRequiredSearchFields() async throws {
        var extra = RuntimeValue.string("not consumed")
        // Unconsumed metadata stays within the runtime's recursive I/O bound.
        for _ in 0..<60 { extra = .object(["extra": extra]) }
        var song = try MusicPlatformFixture.song(NativeMusicFixture.track())
        let ordinary = RuntimeValue.object(["items": .array([song]), "total": .number(1)])
        if case .object(var fields) = song { fields["unused"] = extra; song = .object(fields) }
        let peer = NativeMusicResultFixture([ordinary, .object(["items": .array([song]), "total": .number(1)])])
        let store = NativeMusicTestLibrary(), service = try await NativeMusicFixture.service(store, platform: peer)
        let first = try await service.search(platform: "tx", query: "Synthetic", page: 1, limit: 30)
        let baseline = try await service.read(), writes = store.snapshot().2
        let page = try await service.search(platform: "tx", query: "Synthetic", page: 1, limit: 30)
        XCTAssertEqual(page.items.first?.sources.first?.nativeId, "SyntheticMID")
        XCTAssertEqual(page.items.first?.songId, first.items.first?.songId)
        let current = try await service.read()
        XCTAssertEqual(current.library, baseline.library); XCTAssertEqual(current.queue, baseline.queue)
        XCTAssertEqual(current.playback, baseline.playback); XCTAssertEqual(current.history, baseline.history)
        XCTAssertEqual(store.snapshot().2, writes)
        let calls = await peer.calls(); XCTAssertEqual(calls.map(\.operation), ["search", "search"])
        await service.close()
    }

    func testNativeJSONPersistencePreservesLargeNumbersDuplicateLastValueAndSyntaxBoundaries() throws {
        let raw = #"{"id":9007199254740993,"unused":1e400,"precise":1.234567890123456789,"name":"first","name":"last"}"#
        let value = try MJ.parse(Data(raw.utf8))
        XCTAssertEqual(value["id"].id, "9007199254740993"); XCTAssertEqual(value["name"].string, "last")
        XCTAssertEqual(value["unused"].json, "1e400"); XCTAssertEqual(try MJ.parse(value.bytes), value)
        XCTAssertEqual(value["precise"].json, "1.234567890123456789")
        XCTAssertEqual(value.fields.map(\.0), ["id", "unused", "precise", "name"])
        let nested = String(repeating: "[", count: 80) + "0" + String(repeating: "]", count: 80)
        XCTAssertEqual(try MJ.parse(Data(nested.utf8)).json, nested)
        for bad in ["01", "+1", "1.", "1e", "NaN", "Infinity", "[1,]", "1 2"] { XCTAssertThrowsError(try MJ.parse(Data(bad.utf8))) }
        let large = MJ.obj([("text", .string(String(repeating: "x", count: 8 * 1024 * 1024 + 1)))])
        XCTAssertEqual(try MJ.parse(large.bytes), large)
    }

    func testNativeJSONRetainsUnconsumedNumericLexemesAndExactIntegerConversions() throws {
        let raw = #"{"id":7,"nested":[{"notPic":18446744073709551617},-18446744073709551617,1e999999999999999999999999,9007199254740993.5],"tail":"still present"}"#
        let parsed = try MJ.parse(Data(raw.utf8))
        XCTAssertEqual(parsed.json, raw)
        XCTAssertEqual(parsed["nested"].array.count, 4)
        XCTAssertEqual(parsed["tail"].string, "still present")
        let opaque = parsed["nested"].array[2]
        XCTAssertFalse(opaque.isNumber); XCTAssertFalse(opaque.isInteger)
        XCTAssertTrue(opaque.number.isNaN); XCTAssertEqual(opaque.int, 0)
        XCTAssertThrowsError(try opaque.integer())
        XCTAssertEqual(try MJ.parse(parsed.bytes), parsed)
        for (token, expected) in [
            ("9007199254740991", "9007199254740991"),
            ("9007199254740992", "9007199254740992"),
            ("9007199254740993", "9007199254740993"),
            ("18446744073709551615", "18446744073709551615"),
            ("1234567890123456789012345678901234567890", "1234567890123456789012345678901234567890"),
            ("9.007199254740993e15", "9007199254740993"),
            ("900719925474099300e-2", "9007199254740993"),
            ("9007199254740993.000", "9007199254740993"),
            ("200.0e+0", "200"),
            ("1e64", "1" + String(repeating: "0", count: 64))
        ] {
            XCTAssertEqual(try MJ.parse(Data(token.utf8)).id, expected, token)
        }
        for token in ["9007199254740991.1", "9007199254740993.5", "1.00000000000000001",
                      "1e-400", "-1e-400", "-9007199254740993", "1e999999999999999999999999"] {
            XCTAssertEqual(try MJ.parse(Data(token.utf8)).id, "", token)
        }
        XCTAssertEqual(try MJ.parse(Data("-0.0e999999999999999999999999".utf8)).id, "0")
    }

    func testNativeStableEncodingKeepsNumericKeyEnumerationAndTransactionDigest() throws {
        let raw = #"{"10":"ten","2":"two","01":"string","4294967295":"string","a":1e-7,"b":-1e-7,"c":1e-6,"id":"9007199254740993"}"#
        let expected = #"{"2":"two","10":"ten","01":"string","4294967295":"string","a":1e-7,"b":-1e-7,"c":0.000001,"id":"9007199254740993"}"#
        let document = try MJ.parse(Data(raw.utf8))
        XCTAssertEqual(document.stable.json, expected)
        XCTAssertEqual(document.stable.digest, "c48099f7385e58a189620dbb6839341e93405a5445884135c9ed0985f7b2e380")
    }
}
