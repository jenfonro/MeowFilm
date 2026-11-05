import XCTest
import MeowMusicFeature
@testable import MeowMusicNativeService

// Raw synthetic HTTP bytes: no Foundation/Double pre-decoding, network, account
// or script. This exercises the production request parser, not just MJ in isolation.
private actor MusicUpstreamNumberHTTP: MusicNativeHTTPTransport {
    private var responses: [Data]
    private var requests: [MusicNativeHTTPRequest] = []
    init(_ responses: [String]) { self.responses = responses.map { Data($0.utf8) } }
    func send(_ request: MusicNativeHTTPRequest) throws -> MusicNativeHTTPResponse {
        requests.append(request)
        guard !responses.isEmpty else { throw MusicServiceError.upstreamFailed }
        return .init(status: 200, data: responses.removeFirst())
    }
    func calls() -> [MusicNativeHTTPRequest] { requests }
}

final class MusicNativeUpstreamNumberTests: XCTestCase {
    func testRepeatedMetadataUsesLastValueOnEveryNeteaseRouteWithoutRoundingIDs() async throws {
        let raw = #"{"code":200,"list":[{"id":9007199254740993,"name":"first","name":"last","updateFrequency":"daily","updateFrequency":"weekly","说明":{"score":1e400}}]}"#
        let http = MusicUpstreamNumberHTTP([raw, raw])
        let platforms = MusicNativePlatforms(transport: http, qq: NativeMusicQQFixture(), clock: { NativeMusicFixture.date })
        for route in ["/api/toplist", "/api/cloudsearch/pc"] {
            let root = try await platforms.wy(route, .obj())
            let row = try XCTUnwrap(root["list"].array.first)
            XCTAssertEqual(row["id"].id, "9007199254740993")
            XCTAssertEqual(row["name"].string, "last")
            XCTAssertEqual(row["updateFrequency"].string, "weekly")
            XCTAssertEqual(row["说明"]["score"].json, "1e400")
        }
    }
    func testPlaylistAlgorithmIsPreservedAcrossRoutesAndEscapedDuplicatesUseLastValue() async throws {
        let raw = #"{"code":200,"playlist":{"id":19723756,"tracks":[{"id":9007199254740993,"alg":null,"alg":"ops-music-1"}]}}"#
        let http = MusicUpstreamNumberHTTP([raw, raw])
        let platforms = MusicNativePlatforms(transport: http, qq: NativeMusicQQFixture(), clock: { NativeMusicFixture.date })
        for route in ["/api/v6/playlist/detail", "/api/toplist"] {
            let root = try await platforms.wy(route, .obj())
            let track = try XCTUnwrap(root["playlist"]["tracks"].array.first)
            XCTAssertEqual(track["id"].id, "9007199254740993")
            XCTAssertEqual(track["alg"].string, "ops-music-1")
        }
        let object = try MJ.parse(Data(#"{"id":1,"\u0069d":9007199254740993,"nested":{"a":0,"a":2}}"#.utf8))
        XCTAssertEqual(object["id"].id, "9007199254740993")
        XCTAssertEqual(object["nested"]["a"].int, 2)
        XCTAssertEqual(object.fields.count, 2)
    }

    private func wySong(_ id: String, title: String = "Synthetic", albumID: String? = "18446744073709551615") -> String {
        let albumField = albumID.map { #""id":\#($0),"# } ?? ""
        return #"{"id":\#(id),"name":\#(MJ.quote(title)),"ar":[{"name":"Artist"}],"al":{\#(albumField)"name":"Album","pic":18446744073709551617,"picUrl":"https://p1.music.126.net/synthetic.jpg"},"dt":180000,"unused":{"otherID":1234567890123456789012345678901234567890}}"#
    }
    private func songSearchResponse(_ platform: String, start: Int = 1, count: Int, total: Int) -> String {
        let rows = (start..<(start + count)).map { id -> String in
            if platform == "wy" { return wySong(String(id), title: "Synthetic \(id)") }
            let hex = String(id, radix: 16).uppercased(), hash = String(repeating: "0", count: 32 - hex.count) + hex
            return #"{"FileHash":"\#(hash)","OriSongName":"Synthetic \#(id)","SingerName":"Artist","Audioid":\#(id),"Duration":180}"#
        }.joined(separator: ",")
        if platform == "wy" { return #"{"code":200,"result":{"songCount":\#(total),"songs":[\#(rows)]}}"# }
        return #"{"status":1,"error_code":0,"data":{"total":\#(total),"lists":[\#(rows)]}}"#
    }
    private func playlistSearchResponse(_ platform: String, count: Int, total: Int) -> String {
        let rows = (0..<count).map { index -> String in
            if platform == "wy" {
                return #"{"id":\#(index + 1),"name":"Synthetic playlist \#(index)","creator":{"nickname":"Artist"},"trackCount":1}"#
            }
            return #"{"global_collection_id":"List\#(index)","specialname":"Synthetic playlist \#(index)","nickname":"Artist","songcount":1}"#
        }.joined(separator: ",")
        if platform == "wy" { return #"{"code":200,"result":{"playlistCount":\#(total),"playlists":[\#(rows)]}}"# }
        return #"{"status":1,"error_code":0,"data":{"total":\#(total),"lists":[\#(rows)]}}"#
    }

    @MainActor func testWYAndKGSongShortEmptyAndFullTotalPagesDoNotDispatchAnotherSearch() async throws {
        for platform in ["wy", "kg"] {
            let kind = try XCTUnwrap(MusicPublicBrowsePlatform(rawValue: platform))
            for (count, total) in [(0, 100), (2, 100), (30, 30), (30, 29)] {
                let http = MusicUpstreamNumberHTTP([songSearchResponse(platform, count: count, total: total)])
                let service = try await NativeMusicFixture.service(http: http)
                let results = MusicSearchResultsModel(readSongs: { platform, query, page, limit in
                    try await service.search(platform: platform.rawValue, query: query, page: page, limit: limit)
                }, readPlaylists: { _, _, _, _ in throw MusicServiceError.unsupported },
                    readRecording: { _ in throw MusicServiceError.unsupported })
                results.setAccess(available: true, generation: UUID()); results.appear()
                results.submit("Synthetic", preferences: .init(songPlatforms: [kind], searchPlaylists: false,
                    pages: 10, concurrency: 1))
                await results.finishPendingReads()
                results.more(.songs); await results.finishPendingReads()
                XCTAssertEqual(results.songs.count, count)
                XCTAssertTrue(results.songsLoaded); XCTAssertFalse(results.moreSongs)
                XCTAssertTrue(results.songFailures.isEmpty)
                let calls = await http.calls(); XCTAssertEqual(calls.count, 1)
                results.disappear()
                await service.close()
            }
        }
    }
    @MainActor func testWYAndKGPlaylistSearchUsesTheSameTerminalRawPageRules() async throws {
        for platform in ["wy", "kg"] {
            let kind = try XCTUnwrap(MusicPublicBrowsePlatform(rawValue: platform))
            for (count, total) in [(0, 100), (2, 100), (30, 30), (30, 29)] {
                let http = MusicUpstreamNumberHTTP([playlistSearchResponse(platform, count: count, total: total)])
                let service = try await NativeMusicFixture.service(http: http)
                let results = MusicSearchResultsModel(readSongs: { _, _, _, _ in throw MusicServiceError.unsupported },
                    readPlaylists: { platform, query, page, limit in
                        try await service.searchPublicPlaylists(platform: platform, query: query, page: page, limit: limit)
                    }, readRecording: { _ in throw MusicServiceError.unsupported })
                results.setAccess(available: true, generation: UUID()); results.appear()
                results.submit("Synthetic", preferences: .init(songPlatforms: [], playlistPlatforms: [kind],
                    pages: 10, concurrency: 1))
                await results.finishPendingReads()
                results.more(.playlists); await results.finishPendingReads()
                XCTAssertEqual(results.playlists.count, count)
                XCTAssertTrue(results.playlistsLoaded); XCTAssertFalse(results.morePlaylists)
                XCTAssertTrue(results.playlistFailures.isEmpty)
                let calls = await http.calls(); XCTAssertEqual(calls.count, 1)
                results.disappear()
                await service.close()
            }
        }
    }
    func testWYAndKGOverfullSongAndPlaylistSearchPagesRemainUpstreamInvalid() async throws {
        for platform in ["wy", "kg"] {
            let kind = try XCTUnwrap(MusicPublicBrowsePlatform(rawValue: platform))
            for playlists in [false, true] {
                let raw = playlists ? playlistSearchResponse(platform, count: 31, total: 100)
                    : songSearchResponse(platform, count: 31, total: 100)
                let http = MusicUpstreamNumberHTTP([raw]), store = NativeMusicMemoryDocument()
                let service = try await NativeMusicFixture.service(store, http: http)
                do {
                    if playlists {
                        _ = try await service.searchPublicPlaylists(platform: kind, query: "Synthetic", page: 1, limit: 30)
                    } else {
                        _ = try await service.search(platform: platform, query: "Synthetic", page: 1, limit: 30)
                    }
                    XCTFail("Overfull raw page")
                } catch { XCTAssertEqual(error as? MusicServiceError, .upstreamInvalid) }
                let calls = await http.calls(); XCTAssertEqual(calls.count, 1)
                XCTAssertEqual(store.snapshot().2, 0)
                await service.close()
            }
        }
    }
    @MainActor func testNeteaseSearchSendsOneRequestPerPageWithExactPageSizeOffsets() async throws {
        let http = MusicUpstreamNumberHTTP([
            songSearchResponse("wy", start: 1, count: 30, total: 61),
            songSearchResponse("wy", start: 31, count: 30, total: 61),
            songSearchResponse("wy", start: 61, count: 1, total: 61)
        ])
        let service = try await NativeMusicFixture.service(http: http)
        let results = MusicSearchResultsModel(readSongs: { platform, query, page, limit in
            try await service.search(platform: platform.rawValue, query: query, page: page, limit: limit)
        }, readPlaylists: { _, _, _, _ in throw MusicServiceError.unsupported },
            readRecording: { _ in throw MusicServiceError.unsupported })
        results.setAccess(available: true, generation: UUID()); results.appear()
        results.submit("Synthetic", preferences: .init(songPlatforms: [.netease], searchPlaylists: false,
            pages: 10, concurrency: 1))
        await results.finishPendingReads()
        results.more(.songs); await results.finishPendingReads()
        XCTAssertEqual(results.songs.count, 61)
        XCTAssertTrue(results.songFailures.isEmpty); XCTAssertFalse(results.moreSongs)
        let calls = await http.calls()
        XCTAssertEqual(calls.count, 3)
        let payloads = try calls.map { try MusicNativePublicBrowseFixture.decryptEAPI($0).1 }
        XCTAssertEqual(payloads.map { $0["limit"].int }, [30, 30, 30])
        XCTAssertEqual(payloads.map { $0["offset"].int }, [0, 30, 60])
        XCTAssertTrue(payloads.allSatisfy { $0["s"].string == "Synthetic" && $0["type"].int == 1 })
        results.disappear()
        await service.close()
    }

    func testWYHTTP200SearchKeepsWholePageExactIdentityAndStrictPersistence() async throws {
        let ids = ["9007199254740992", "9007199254740993"]
        let rows = ids.enumerated().map { wySong($0.element, title: "Synthetic \($0.offset)") }.joined(separator: ",")
        let http = MusicUpstreamNumberHTTP([#"{"code":200,"result":{"songCount":32,"songs":[\#(rows)]}}"#])
        let store = NativeMusicMemoryDocument(), service = try await NativeMusicFixture.service(store, http: http)
        let page = try await service.search(platform: "wy", query: "Synthetic", page: 2, limit: 30)
        XCTAssertEqual(page.total, 32); XCTAssertNil(page.nextPage)
        XCTAssertEqual(page.items.count, 2)
        XCTAssertEqual(page.items.map { $0.sources[0].nativeId }, ids)
        XCTAssertEqual(page.items.map { $0.sources[0].sourceKey }, ids.map { MJ.strings(["wy", $0, ""]).json })
        XCTAssertEqual(page.items.map(\.duration), [180, 180])
        let calls = await http.calls()
        XCTAssertEqual(calls.count, 1); XCTAssertEqual(calls[0].url.path, "/eapi/cloudsearch/pc")
        XCTAssertFalse((calls[0].headers["Cookie"] ?? "").contains("MUSIC_U"))

        let collection = try await service.createPlaylist(name: "Synthetic", refs: page.items.map(\.ref))
        let saved = try MJ.parse(XCTUnwrap(store.snapshot().0)) // Default document mode remains mandatory.
        let songs = saved["records"][MusicModel.key("library")]["library"]["songs"].fields.map(\.1)
        XCTAssertEqual(songs.count, 2)
        XCTAssertEqual(Set(songs.map { $0["sources"].array.first?["musicInfo"]["songmid"].string }), Set(ids))
        XCTAssertTrue(songs.allSatisfy {
            $0["sources"].array.first?["musicInfo"]["albumId"].string == "18446744073709551615"
        })
        let reopened = try await NativeMusicFixture.service(store)
        let tracks = try await reopened.tracks(in: collection)
        XCTAssertEqual(tracks.map { $0.sources[0].nativeId }, ids)
        XCTAssertEqual(tracks.map(\.songId), page.items.map(\.songId))
    }

    func testWYWEAPIDetailSharesLosslessParserAndRestoresRequestedOrder() async throws {
        let ids = ["9007199254740992", "9007199254740993"]
        let rows = ids.reversed().map { wySong($0) }.joined(separator: ",")
        let http = MusicUpstreamNumberHTTP([#"{"code":200,"songs":[\#(rows)]}"#])
        let platforms = MusicNativePlatforms(transport: http, qq: NativeMusicQQFixture(), clock: { NativeMusicFixture.date })
        let tracks = try await platforms.browseWYSongs(ids)
        XCTAssertEqual(tracks.count, 2)
        let data = try tracks.map { try MJ.parse($0.data) }
        XCTAssertEqual(data.map { $0["songmid"].string }, ids)
        XCTAssertEqual(data.map { $0["albumId"].string }, Array(repeating: "18446744073709551615", count: 2))
        let calls = await http.calls()
        XCTAssertEqual(calls.count, 1); XCTAssertEqual(calls[0].url.path, "/weapi/v3/song/detail")
        // WEAPI carries only the isolated public device context, never an account cookie.
        XCTAssertTrue((calls[0].headers["Cookie"] ?? "").contains("deviceId="))
        XCTAssertFalse((calls[0].headers["Cookie"] ?? "").contains("MUSIC_U"))
    }

    func testWYMissingNullAndZeroAlbumIDsKeepAllThirtySearchRowsAndPersistExactSongs() async throws {
        let ids = (1...30).map(String.init)
        let absent: [String?] = [nil, "null", "0", #""0""#]
        let rows = ids.enumerated().map { index, id in
            wySong(id, title: "Synthetic \(index)", albumID: index < 26 ? "18446744073709551615" : absent[index - 26])
        }.joined(separator: ",")
        let http = MusicUpstreamNumberHTTP([#"{"code":200,"result":{"songCount":30,"songs":[\#(rows)]}}"#])
        let store = NativeMusicMemoryDocument(), service = try await NativeMusicFixture.service(store, http: http)
        let page = try await service.search(platform: "wy", query: "Synthetic", page: 1, limit: 30)
        XCTAssertEqual(page.items.count, 30); XCTAssertEqual(page.total, 30); XCTAssertNil(page.nextPage)
        XCTAssertEqual(page.items.map { $0.sources[0].nativeId }, ids)
        let collection = try await service.createPlaylist(name: "Synthetic without albums", refs: page.items.map(\.ref))
        let saved = try MJ.parse(XCTUnwrap(store.snapshot().0))
        let songs = saved["records"][MusicModel.key("library")]["library"]["songs"].fields.map(\.1)
        XCTAssertEqual(songs.count, 30)
        for song in songs {
            let native = song["sources"].array.first?["musicInfo"] ?? .null,
                id = try XCTUnwrap(Int(native["songmid"].string))
            XCTAssertEqual(native["albumId"].string, id <= 26 ? "18446744073709551615" : "")
        }
        let reopened = try await NativeMusicFixture.service(store)
        let tracks = try await reopened.tracks(in: collection)
        XCTAssertEqual(tracks.map { $0.sources[0].nativeId }, ids)
        XCTAssertEqual(tracks.map(\.songId), page.items.map(\.songId))
        let calls = await http.calls()
        XCTAssertEqual(calls.count, 1); XCTAssertEqual(calls[0].url.path, "/eapi/cloudsearch/pc")
        XCTAssertFalse((calls[0].headers["Cookie"] ?? "").contains("MUSIC_U"))
    }

    func testWYDetailUsesSameOptionalAlbumBoundaryWithoutLosingRecordingIdentity() async throws {
        let ids = ["15", "16", "9007199254740993", "18"]
        let absent: [String?] = [nil, "null", "0", #""0""#]
        let rows = ids.enumerated().reversed().map { wySong($0.element, albumID: absent[$0.offset]) }.joined(separator: ",")
        let http = MusicUpstreamNumberHTTP([#"{"code":200,"songs":[\#(rows)]}"#])
        let platforms = MusicNativePlatforms(transport: http, qq: NativeMusicQQFixture(), clock: { NativeMusicFixture.date })
        let tracks = try await platforms.browseWYSongs(ids)
        let native = try tracks.map { try MJ.parse($0.data) }
        XCTAssertEqual(native.map { $0["songmid"].string }, ids)
        XCTAssertEqual(native.map { $0["albumId"].string }, Array(repeating: "", count: 4))
        let calls = await http.calls()
        XCTAssertEqual(calls.count, 1); XCTAssertEqual(calls[0].url.path, "/weapi/v3/song/detail")
    }

    func testWYNonSentinelMalformedAlbumIDStillRejectsWholeSearch() async throws {
        for token in ["-1", "1.5", "true", "{}", "[]", #""00""#, #""invalid""#, #""""#] {
            let rows = wySong("17") + "," + wySong("18", albumID: token)
            let http = MusicUpstreamNumberHTTP([#"{"code":200,"result":{"songCount":2,"songs":[\#(rows)]}}"#])
            let store = NativeMusicMemoryDocument(), service = try await NativeMusicFixture.service(store, http: http)
            do {
                _ = try await service.search(platform: "wy", query: "Synthetic", page: 1, limit: 30)
                XCTFail("Malformed album identity must not become missing: \(token)")
            } catch { XCTAssertEqual(error as? MusicServiceError, .upstreamInvalid) }
            XCTAssertEqual(store.snapshot().2, 0)
        }
    }

    func testWYOptionalAlbumDoesNotMakeZeroOrMissingSongIdentityValid() async throws {
        for token in ["0", "null", #""0""#] {
            let rows = wySong(token, albumID: "0")
            let http = MusicUpstreamNumberHTTP([#"{"code":200,"result":{"songCount":1,"songs":[\#(rows)]}}"#])
            let service = try await NativeMusicFixture.service(http: http)
            do {
                _ = try await service.search(platform: "wy", query: "Synthetic", page: 1, limit: 30)
                XCTFail("Recording identity is mandatory")
            } catch { XCTAssertEqual(error as? MusicServiceError, .upstreamInvalid) }
        }
    }

    func testUnconsumedNumericLexemesSurviveAtAnyPathWithoutTruncation() throws {
        let raw = #"{"id":7,"nested":[{"notPic":18446744073709551617},-18446744073709551617,1e999999999999999999999999,9007199254740993.5],"tail":"still present"}"#
        let parsed = try MJ.parse(Data(raw.utf8))
        XCTAssertEqual(parsed.json, raw)
        XCTAssertEqual(parsed["nested"].array.count, 4)
        XCTAssertEqual(parsed["nested"].array[0]["notPic"].id, "18446744073709551617")
        XCTAssertEqual(parsed["tail"].string, "still present")
        let opaque = parsed["nested"].array[2]
        XCTAssertFalse(opaque.isNumber); XCTAssertFalse(opaque.isInteger)
        XCTAssertTrue(opaque.number.isNaN); XCTAssertEqual(opaque.int, 0)
        XCTAssertThrowsError(try opaque.integer())
        XCTAssertEqual(try MJ.parse(parsed.bytes), parsed)
    }

    func testConsumedIDsAreExactIncludingIntegerExponentAndDecimalSpellings() throws {
        let cases = [
            ("9007199254740991", "9007199254740991"),
            ("9007199254740992", "9007199254740992"),
            ("9007199254740993", "9007199254740993"),
            ("18446744073709551615", "18446744073709551615"),
            ("1234567890123456789012345678901234567890", "1234567890123456789012345678901234567890"),
            ("9.007199254740993e15", "9007199254740993"),
            ("900719925474099300e-2", "9007199254740993"),
            ("9007199254740993.000", "9007199254740993"),
            ("200.0e+0", "200")
        ]
        for (token, expected) in cases {
            let value = try MJ.parse(Data(token.utf8))
            XCTAssertEqual(try MusicPlatformProtocol.numericID(value), expected, token)
            if expected.count <= 20 {
                XCTAssertEqual(try MusicNativePublicBrowseWire.playlistID(value), expected, token)
            } else {
                XCTAssertThrowsError(try MusicNativePublicBrowseWire.playlistID(value), token)
            }
        }
        for token in ["9007199254740991.1", "9007199254740993.5", "1.00000000000000001",
                      "1e-400", "-1e-400", "-9007199254740993", "1e64", "1e999999999999999999999999"] {
            let value = try MJ.parse(Data(token.utf8))
            XCTAssertThrowsError(try MusicPlatformProtocol.numericID(value), token)
            XCTAssertThrowsError(try MusicNativePublicBrowseWire.playlistID(value), token)
        }
        let zero = try MJ.parse(Data("-0.0e999999999999999999999999".utf8))
        XCTAssertEqual(try MusicPlatformProtocol.numericID(zero, zero: true), "0")
        XCTAssertThrowsError(try MusicPlatformProtocol.numericID(zero))
    }

    func testConsumedInvalidIDFailsWholeSearchInsteadOfDroppingSong() async throws {
        let rows = wySong("17") + "," + wySong("9007199254740993.5")
        let http = MusicUpstreamNumberHTTP([#"{"code":200,"result":{"songCount":2,"songs":[\#(rows)]}}"#])
        let store = NativeMusicMemoryDocument(), service = try await NativeMusicFixture.service(store, http: http)
        do {
            _ = try await service.search(platform: "wy", query: "Synthetic", page: 1, limit: 30)
            XCTFail("invalid consumed ID must not become a rounded ID or a partial page")
        } catch { XCTAssertEqual(error as? MusicServiceError, .upstreamInvalid) }
        XCTAssertEqual(store.snapshot().2, 0)
    }

    func testKugouConsumesExactAudioIDsAndDoesNotSilentlyDiscardInvalidOne() async throws {
        let platforms = MusicNativePlatforms(transport: NativeMusicNoNetwork(), qq: NativeMusicQQFixture(), clock: { NativeMusicFixture.date })
        let raw = #"{"hash":"1234567890ABCDEF1234567890ABCDEF","name":"Synthetic","Audioid":9007199254740993,"audio_id":9007199254740993,"AlbumID":18446744073709551615,"MixSongID":9007199254740995,"Duration":180,"unused":18446744073709551617}"#
        var value = try MJ.parse(Data(raw.utf8))
        let song = try await platforms.kugouSong(value)
        let native = try MJ.parse(song.data)
        XCTAssertEqual(native["songmid"].string, "1234567890ABCDEF1234567890ABCDEF")
        XCTAssertEqual(native["albumId"].string, "18446744073709551615")
        XCTAssertEqual(native["albumAudioId"].string, "9007199254740995")
        for token in ["9007199254740992", "9007199254740993.5", #""09007199254740993""#] {
            value["audio_id"] = try MJ.parse(Data(token.utf8))
            do {
                _ = try await platforms.kugouSong(value)
                XCTFail("conflicting/fractional/leading-zero Audioid must not be ignored")
            } catch { XCTAssertEqual(error as? MusicServiceError, .upstreamInvalid) }
        }
    }

    func testKugouEmptyOptionalAlbumIDsDoNotRejectTheRecording() async throws {
        let platforms = MusicNativePlatforms(transport: NativeMusicNoNetwork(), qq: NativeMusicQQFixture(), clock: { NativeMusicFixture.date })
        let raw = #"{"FileHash":"1234567890ABCDEF1234567890ABCDEF","OriSongName":"Synthetic","SingerName":"Artist","Audioid":42,"AlbumID":"","MixSongID":"","Duration":180}"#
        let original = try MJ.parse(Data(raw.utf8))
        let result = try await platforms.kugouSong(original)
        let native = try MJ.parse(result.data)
        XCTAssertEqual(native["songmid"].string, "1234567890ABCDEF1234567890ABCDEF")
        XCTAssertEqual(native["albumId"].string, "0")
        XCTAssertEqual(native["albumAudioId"].string, "0")
        XCTAssertEqual(native["hash"].string, "1234567890ABCDEF1234567890ABCDEF")
        for field in ["AlbumID", "MixSongID"] {
            var malformed = original; malformed[field] = .string("invalid")
            do {
                _ = try await platforms.kugouSong(malformed)
                XCTFail("Nonempty invalid identity must not become zero")
            } catch { XCTAssertEqual(error as? MusicServiceError, .upstreamInvalid) }
        }
    }

    func testUnusedLargeNumbersRoundTripAndExistingStableEncodingIsUnchanged() throws {
        for token in ["9007199254740992", "9007199254740993", "18446744073709551615", "1e400"] {
            let text = #"{"unused":\#(token)}"#
            XCTAssertEqual(try MJ.parse(Data(text.utf8)).json, text, token)
        }
        let raw = #"{"10":"ten","2":"two","01":"string","4294967295":"string","a":1e-7,"b":-1e-7,"c":1e-6,"id":"9007199254740993"}"#
        let expected = #"{"2":"two","10":"ten","01":"string","4294967295":"string","a":1e-7,"b":-1e-7,"c":0.000001,"id":"9007199254740993"}"#
        let document = try MJ.parse(Data(raw.utf8))
        XCTAssertEqual(document.stable.json, expected)
        XCTAssertEqual(document.stable.digest, "c48099f7385e58a189620dbb6839341e93405a5445884135c9ed0985f7b2e380")
    }

    func testStandardJSONSyntaxAndResourceLimitsRemainEnforced() throws {
        for raw in ["01", "+1", "1.", "1e", "NaN", "Infinity", "[1,]", "1 2"] {
            XCTAssertThrowsError(try MJ.parse(Data(raw.utf8)), raw)
        }
        let raw = Data(#"{"unused":18446744073709551617}"#.utf8)
        XCTAssertThrowsError(try MJ.parse(raw, maximum: raw.count - 1))
        let deep = String(repeating: "[", count: 41) + "1" + String(repeating: "]", count: 41)
        XCTAssertThrowsError(try MJ.parse(Data(deep.utf8)))
    }
}
