import Foundation
import XCTest
import MeowMusicFeature
@testable import MeowMusicPlatform

@MainActor final class MusicNativeQueueRecommendationProtocolTests: XCTestCase {
    private let path = "/eapi/homepage/more/rcmd/song"
    private let metadataPath = "/weapi/v3/song/detail"
    private let request = MusicNativeSimilarModeRequest.queueRecommendations(songIDs: ["101", "102"], currentPlaySongID: "102")

    func testMapsAcceptObjectsOrJSONStringsAndHasMorePreservesTrueFalseAndUnknown() async throws {
        let f = try MusicNeteaseNativeFixture(); try await f.enable()
        let algorithms: [String: Any] = ["101": "alg-101", "102": NSNull()]
        let sources: [String: Any] = ["101": "synthetic-source-101", "102": NSNull(), "103": ""]
        let replies: [[String: Any]] = [
            ["songList": (101...104).map(MusicNeteaseFixture.song), "algMap": algorithms, "sourceMap": sources, "hasMore": true],
            ["songList": (101...104).map(MusicNeteaseFixture.song),
             "algMap": String(decoding: try MusicQQWire.json(algorithms), as: UTF8.self),
             "sourceMap": String(decoding: try MusicQQWire.json(sources), as: UTF8.self), "hasMore": false],
            ["songList": [MusicNeteaseFixture.song()], "algMap": NSNull(), "sourceMap": NSNull()],
            ["songList": [MusicNeteaseFixture.song()], "hasMore": NSNull()]
        ]
        await f.http.replies(path, try replies.map { try MusicNeteaseFixture.response(["code": 200, "data": $0]) })
        let lease = try await f.service.beginAccountQueue(platform: .netease)
        let expectedMore: [Bool?] = [true, false, nil, nil]
        for index in replies.indices {
            let batch = try await f.service.similarModeBatch(lease, request: request)
            XCTAssertEqual(batch.hasMore, expectedMore[index]); XCTAssertNil(batch.heartbeatContext)
            let songs = try batch.items.map { try MusicQQWire.decode($0.data) }
            XCTAssertEqual(songs.count, index < 2 ? 4 : 1)
            var feedbackSources: [String: String] = [:]
            for song in songs {
                let attribution = try MusicQQWire.object(song["_recommendation"])
                let id = try MusicQQWire.string(song["songmid"]), mapped = index < 2 && id == "101"
                feedbackSources[id] = try MusicQQWire.string(attribution["feedbackSource"])
                XCTAssertEqual(attribution["source"] as? String, "moreRcmd")
                XCTAssertEqual(attribution["sourceId"] as? String, "")
                XCTAssertEqual(attribution["feedbackSource"] as? String, mapped ? "synthetic-source-101" : "moreRcmd")
                XCTAssertEqual(attribution["alg"] as? String, mapped ? "alg-101" : "")
            }
            if index < 2 {
                XCTAssertEqual(feedbackSources["102"], "moreRcmd", "null map entry")
                XCTAssertEqual(feedbackSources["103"], "moreRcmd", "empty map entry")
                XCTAssertEqual(feedbackSources["104"], "moreRcmd", "missing map entry")
            }
        }
        let records = await f.http.recorded()
        XCTAssertEqual(records.filter { $0.request.url.path == path }.count, 4)
        XCTAssertFalse(records.contains { $0.request.url.path.contains("radio/get") || $0.request.url.path.contains("intelligence") })
        _ = await f.service.close()
    }

    func testLargeQueueBatchPreservesCompleteRepeatedInputAndHasNoOffset() async throws {
        let f = try MusicNeteaseNativeFixture(); try await f.enable()
        let ids = Array(3001..<4002)
        let songs = ids.map { ["id": $0, "name": "合成", "ar": [["name": "歌手"]]] as [String: Any] }
        await f.http.replies(path, [try MusicNeteaseFixture.response([
            "code": 200, "data": ["songList": songs, "hasMore": false]
        ])])
        let lease = try await f.service.beginAccountQueue(platform: .netease)
        let before = f.store.peek(f.identity), input = ["101"] + (102..<2203).map(String.init) + ["101"]
        let batch = try await f.service.similarModeBatch(lease,
            request: .queueRecommendations(songIDs: input, currentPlaySongID: "2202"))
        let actual = try batch.items.map { try MusicQQWire.string(MusicQQWire.decode($0.data)["songmid"]) }
        XCTAssertEqual(actual, ids.map(String.init))
        XCTAssertEqual(batch.hasMore, false)
        let records = await f.http.recorded(), call = try XCTUnwrap(records.first { $0.request.url.path == path })
        let payload = try MusicQQWire.decode(XCTUnwrap(call.payload))
        XCTAssertEqual(payload["songIds"] as? String, "[" + input.joined(separator: ",") + "]")
        XCTAssertEqual(payload["currentPlaySongId"] as? String, "2202")
        XCTAssertEqual(payload["source"] as? String, "curlist_more_rcmd")
        XCTAssertNil(payload["offset"]); XCTAssertNil(payload["cursor"]); XCTAssertNil(payload["mode"])
        XCTAssertEqual(f.store.peek(f.identity), before)
        _ = await f.service.close()
    }

    func testSourceMapPreservesRawNonemptyValuesWithinWireBoundary() async throws {
        let f = try MusicNeteaseNativeFixture(); try await f.enable()
        let sources = ["aiMode", "moreRcmd", " synthetic source ", String(repeating: "x", count: 2000)]
        let map = Dictionary(uniqueKeysWithValues: sources.enumerated().map { (String(101 + $0.offset), $0.element) })
        await f.http.replies(path, [try MusicNeteaseFixture.response(["code": 200, "data": [
            "songList": (101...104).map(MusicNeteaseFixture.song), "sourceMap": map
        ]])])
        let lease = try await f.service.beginAccountQueue(platform: .netease)
        let batch = try await f.service.similarModeBatch(lease, request: request)
        let actual = try batch.items.map { item in
            let song = try MusicQQWire.decode(item.data), attribution = try MusicQQWire.object(song["_recommendation"])
            return try MusicQQWire.string(attribution["feedbackSource"], maximum: 2000)
        }
        XCTAssertEqual(actual, sources)
        _ = await f.service.close()
    }

    func testRecommendationMetadataHydratesMissingArtistsInProtocolPagesWithoutLosingAttribution() async throws {
        let f = try MusicNeteaseNativeFixture(); try await f.enable()
        let ids = Array(1001..<1104)
        let raw = ids.map { ["songId": $0, "name": "待补详情", "ar": []] as [String: Any] }
        await f.http.replies(path, [try MusicNeteaseFixture.response(["code": 200, "data": [
            "songList": raw, "algMap": ["1001": "first-alg", "1103": "last-alg"],
            "sourceMap": ["1103": "synthetic-source"], "hasMore": true
        ]])])
        await f.http.replies(metadataPath, [
            try MusicNeteaseFixture.response(["code": 200, "songs": (1001..<1101).map(MusicNeteaseFixture.song)]),
            try MusicNeteaseFixture.response(["code": 200, "songs": (1101..<1104).map(MusicNeteaseFixture.song)])
        ])
        let lease = try await f.service.beginAccountQueue(platform: .netease)
        let batch = try await f.service.similarModeBatch(lease, request: request)
        let songs = try batch.items.map { try MusicQQWire.decode($0.data) }
        XCTAssertEqual(songs.compactMap { $0["songmid"] as? String }, ids.map(String.init))
        XCTAssertEqual(songs.compactMap { $0["name"] as? String }, ids.map { "合成歌曲\($0)" })
        XCTAssertTrue(songs.allSatisfy { $0["singer"] as? String == "合成歌手" })
        let first = try MusicQQWire.object(XCTUnwrap(songs.first)["_recommendation"])
        let last = try MusicQQWire.object(XCTUnwrap(songs.last)["_recommendation"])
        XCTAssertEqual(first["alg"] as? String, "first-alg"); XCTAssertEqual(first["feedbackSource"] as? String, "moreRcmd")
        XCTAssertEqual(last["alg"] as? String, "last-alg"); XCTAssertEqual(last["feedbackSource"] as? String, "synthetic-source")
        XCTAssertEqual(batch.hasMore, true)
        let records = await f.http.recorded()
        XCTAssertEqual(records.filter { $0.request.url.path == metadataPath }.count, 2)
        _ = await f.service.close()
    }

    func testMissingForeignDuplicateOrIncompleteMetadataFailsWholeBatch() async throws {
        var noArtist = MusicNeteaseFixture.song(102); noArtist["ar"] = []
        var noName = MusicNeteaseFixture.song(102); noName["name"] = " "
        let cases: [(String, [[String: Any]], MusicServiceError)] = [
            ("missing", [], .targetChanged),
            ("foreign", [MusicNeteaseFixture.song(999)], .upstreamInvalid),
            ("duplicate", [MusicNeteaseFixture.song(102), MusicNeteaseFixture.song(102)], .upstreamInvalid),
            ("missing artist", [noArtist], .upstreamInvalid),
            ("missing name", [noName], .upstreamInvalid)
        ]
        for (label, metadata, expected) in cases {
            let f = try MusicNeteaseNativeFixture(); try await f.enable()
            await f.http.replies(path, [try MusicNeteaseFixture.response(["code": 200, "data": [
                "songList": [MusicNeteaseFixture.song(101), ["id": 102]]
            ]])])
            await f.http.replies(metadataPath, [try MusicNeteaseFixture.response(["code": 200, "songs": metadata])])
            let lease = try await f.service.beginAccountQueue(platform: .netease)
            do {
                _ = try await f.service.similarModeBatch(lease, request: request)
                XCTFail("Partial batch accepted: " + label)
            } catch { XCTAssertEqual(error as? MusicServiceError, expected, label) }
            _ = await f.service.close()
        }
    }

    func testMalformedMapsAndNonBooleanHasMoreAreNotSuccessfulBatches() async throws {
        let cases: [[String: Any]] = [
            ["algMap": "[]"], ["algMap": "invalid-json"], ["algMap": 1],
            ["sourceMap": "[]"], ["sourceMap": ["101": 1]],
            ["sourceMap": ["101": "invalid\nsource"]], ["sourceMap": ["101": String(repeating: "x", count: 2001)]],
            ["hasMore": 1], ["hasMore": "false"]
        ]
        for fields in cases {
            let f = try MusicNeteaseNativeFixture(); try await f.enable()
            let body = fields.merging(["songList": [MusicNeteaseFixture.song()]]) { _, value in value }
            await f.http.replies(path, [try MusicNeteaseFixture.response(["code": 200, "data": body])])
            let lease = try await f.service.beginAccountQueue(platform: .netease)
            do { _ = try await f.service.similarModeBatch(lease, request: request); XCTFail("Malformed continuation accepted") }
            catch { XCTAssertEqual(error as? MusicServiceError, .upstreamInvalid) }
            _ = await f.service.close()
        }
    }

    func testEmptyTerminalBatchAndExplicitFreshRetryDoNotInventPagination() async throws {
        let f = try MusicNeteaseNativeFixture(); try await f.enable()
        await f.http.replies(path, [
            try MusicNeteaseFixture.response(["code": 200, "data": ["songList": [], "hasMore": false]]),
            try MusicNeteaseFixture.response(["code": 200, "data": ["songList": [MusicNeteaseFixture.song(103)], "hasMore": true]])
        ])
        let lease = try await f.service.beginAccountQueue(platform: .netease)
        let terminal = try await f.service.similarModeBatch(lease, request: request)
        XCTAssertTrue(terminal.items.isEmpty); XCTAssertEqual(terminal.hasMore, false)
        let retry = try await f.service.similarModeBatch(lease, request: request)
        XCTAssertEqual(retry.items.count, 1); XCTAssertEqual(retry.hasMore, true)
        let records = await f.http.recorded(), calls = records.filter { $0.request.url.path == path }
        XCTAssertEqual(calls.count, 2)
        for call in calls {
            let payload = try MusicQQWire.decode(XCTUnwrap(call.payload))
            XCTAssertEqual(payload["songIds"] as? String, "[101,102]")
            XCTAssertEqual(payload["currentPlaySongId"] as? String, "102")
            XCTAssertNil(payload["offset"]); XCTAssertNil(payload["cursor"])
        }
        _ = await f.service.close()
    }
}
