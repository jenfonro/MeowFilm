import Foundation
import XCTest
import MeowMusicFeature
@testable import MeowMusicPlatform

@MainActor final class MusicNativeHeartbeatProtocolTests: XCTestCase {
    private let directoryPath = "/weapi/user/playlist"
    private let detailPath = "/eapi/v6/playlist/detail"
    private let metadataPath = "/weapi/v3/song/detail"
    private let heartbeatPath = "/eapi/playmode/intelligence/list"

    func testLikedEntryFindsOwnSecondPageAndChoosesFromEveryHydratedMember() async throws {
        let f = try MusicNeteaseNativeFixture()
        let adapter = NeteaseMusicAccountAdapter(transport: f.http, now: { [clock = f.clock] in clock.now() })
        let ids = Array(101..<206), expected = ids.map(String.init)
        let firstPage = [MusicNeteaseFixture.collection(900, owner: 99)] +
            (901..<1000).map { MusicNeteaseFixture.collection($0, special: 0) }
        await f.http.replies(directoryPath, [
            try MusicNeteaseFixture.response(["code": 200, "more": true, "playlist": firstPage]),
            try MusicNeteaseFixture.response(["code": 200, "more": false, "playlist": [MusicNeteaseFixture.collection()]])
        ])
        var row = try MusicQQWire.object(MusicNeteaseFixture.detail(ids)["playlist"])
        row["trackCount"] = 102; row["cloudTrackCount"] = 3
        row["tracks"] = [MusicNeteaseFixture.song(101), MusicNeteaseFixture.song(102)]
        await f.http.replies(detailPath, [try MusicNeteaseFixture.response(["code": 200, "playlist": row])])
        await f.http.replies(metadataPath, [
            try MusicNeteaseFixture.response(["code": 200, "songs": (103..<203).map(MusicNeteaseFixture.song)]),
            try MusicNeteaseFixture.response(["code": 200, "songs": (203..<206).map(MusicNeteaseFixture.song)])
        ])
        await f.http.replies(heartbeatPath, [try MusicNeteaseFixture.response([
            "code": 200, "data": (501..<506).map { ["songInfo": MusicNeteaseFixture.song($0)] }
        ])])
        let batch = try await adapter.similarMode(.heartbeat(lastID: nil, fromHome: true, context: nil),
            credential: MusicNeteaseFixture.credential(), authorize: {}, chooseLikedSong: { candidates in
                XCTAssertEqual(candidates, expected)
                return "205"
            })
        XCTAssertEqual(batch.heartbeatContext, .init(playlistID: "501", seedID: "205"))
        XCTAssertEqual(batch.items.count, 6)
        let records = await f.http.recorded(), request = try XCTUnwrap(records.first { $0.request.url.path == heartbeatPath })
        let payload = try MusicQQWire.decode(XCTUnwrap(request.payload))
        XCTAssertEqual(payload["playlistId"] as? String, "501")
        XCTAssertEqual(payload["startMusicId"] as? String, "205")
        XCTAssertEqual(payload["songId"] as? String, "205")
        XCTAssertEqual(payload["count"] as? Int, 105); XCTAssertEqual(payload["type"] as? String, "fromPlayAll")
        XCTAssertEqual(records.filter { $0.request.url.path == directoryPath }.count, 2)
        XCTAssertEqual(records.filter { $0.request.url.path == metadataPath }.count, 2)
    }

    func testCurrentUsesActualMemberAndHomeUsesChosenMemberAsFirstSong() async throws {
        let f = try MusicNeteaseNativeFixture()
        let adapter = NeteaseMusicAccountAdapter(transport: f.http, now: { [clock = f.clock] in clock.now() })
        var row = try MusicQQWire.object(MusicNeteaseFixture.detail([101, 102, 103])["playlist"])
        var invalid = MusicNeteaseFixture.song(101); invalid["ar"] = []
        row["tracks"] = [invalid, MusicNeteaseFixture.song(102), MusicNeteaseFixture.song(103)]
        await f.http.replies(detailPath, [try MusicNeteaseFixture.response(["code": 200, "playlist": row])])
        await f.http.replies(metadataPath, [try MusicNeteaseFixture.response(["code": 200, "songs": [invalid]])])
        await f.http.replies(heartbeatPath, [try MusicNeteaseFixture.response([
            "code": 200, "data": [["songInfo": MusicNeteaseFixture.song(104)]]
        ])])
        let current = try await adapter.similarMode(.heartbeat(lastID: "102", fromHome: false, context: nil),
            credential: MusicNeteaseFixture.credential(), authorize: {}, chooseLikedSong: { _ in
                XCTFail("Current entry must use its actual last ID")
                return "103"
            })
        let context = try XCTUnwrap(current.heartbeatContext)
        XCTAssertEqual(context, .init(playlistID: "501", seedID: "102"))
        _ = try await adapter.similarMode(.heartbeat(lastID: nil, fromHome: true, context: nil),
            credential: MusicNeteaseFixture.credential(), authorize: {}, chooseLikedSong: { candidates in
                XCTAssertEqual(candidates, ["102", "103"])
                return "103"
            })
        let records = await f.http.recorded(), requests = records.filter { $0.request.url.path == heartbeatPath }
        XCTAssertEqual(requests.count, 2)
        let one = try MusicQQWire.decode(XCTUnwrap(requests.first?.payload))
        let all = try MusicQQWire.decode(XCTUnwrap(requests.last?.payload))
        XCTAssertEqual(one["startMusicId"] as? String, "102"); XCTAssertEqual(one["songId"] as? String, "102")
        XCTAssertEqual(all["startMusicId"] as? String, "103"); XCTAssertEqual(all["songId"] as? String, "103")
        XCTAssertEqual(one["type"] as? String, "fromPlayOne"); XCTAssertEqual(all["type"] as? String, "fromPlayAll")
        XCTAssertEqual(one["count"] as? Int, 3); XCTAssertEqual(all["count"] as? Int, 3)
        XCTAssertEqual(records.filter { $0.request.url.path == directoryPath }.count, 2)
        XCTAssertEqual(records.filter { $0.request.url.path == detailPath }.count, 2)
    }

    func testSuppliedContextCannotBypassOwnListOrFirstValidSeed() async throws {
        for context in [
            MusicSimilarHeartbeatContext(playlistID: "999", seedID: "101"),
            MusicSimilarHeartbeatContext(playlistID: "501", seedID: "999"),
            MusicSimilarHeartbeatContext(playlistID: "501", seedID: "102")
        ] {
            let f = try MusicNeteaseNativeFixture(); try await f.enable()
            let lease = try await f.service.beginAccountQueue(platform: .netease)
            do {
                _ = try await f.service.similarModeBatch(lease, request: .heartbeat(lastID: "101", fromHome: false, context: context))
                XCTFail("Unverified context accepted")
            } catch { XCTAssertEqual(error as? MusicServiceError, .targetChanged) }
            let records = await f.http.recorded()
            XCTAssertEqual(records.filter { $0.request.url.path == directoryPath }.count, 1)
            XCTAssertEqual(records.filter { $0.request.url.path == detailPath }.count, 1)
            XCTAssertFalse(records.contains { $0.request.url.path == heartbeatPath })
            _ = await f.service.close()
        }
    }

    func testCurrentEntryWithoutActualLastIDCannotFallBackToLikedSeed() async throws {
        let f = try MusicNeteaseNativeFixture(); try await f.enable()
        let lease = try await f.service.beginAccountQueue(platform: .netease)
        do {
            _ = try await f.service.similarModeBatch(lease, request: .heartbeat(lastID: nil, fromHome: false, context: nil))
            XCTFail("Current entry silently became a liked-list entry")
        } catch { XCTAssertEqual(error as? MusicServiceError, .invalidRequest) }
        let records = await f.http.recorded()
        XCTAssertFalse(records.contains { $0.request.url.path == heartbeatPath })
        _ = await f.service.close()
    }

    func testSameContextRechecksChangedCompleteCountOnEveryHeartbeat() async throws {
        let f = try MusicNeteaseNativeFixture(); try await f.enable()
        await f.http.replies(detailPath, try [[101, 102], [101, 102, 103, 104]].map {
            try MusicNeteaseFixture.response(MusicNeteaseFixture.detail($0))
        })
        await f.http.replies(heartbeatPath, [try MusicNeteaseFixture.response([
            "code": 200, "data": [["songInfo": MusicNeteaseFixture.song(105)]]
        ])])
        let lease = try await f.service.beginAccountQueue(platform: .netease)
        let first = try await f.service.similarModeBatch(lease, request: .heartbeat(lastID: "101", fromHome: false, context: nil))
        _ = try await f.service.similarModeBatch(lease, request: .heartbeat(lastID: "101", fromHome: false, context: first.heartbeatContext))
        let records = await f.http.recorded(), requests = records.filter { $0.request.url.path == heartbeatPath }
        XCTAssertEqual(requests.count, 2)
        let payloads = try requests.map { try MusicQQWire.decode(XCTUnwrap($0.payload)) }
        XCTAssertEqual(payloads.compactMap { $0["count"] as? Int }, [2, 4])
        XCTAssertEqual(payloads.compactMap { $0["startMusicId"] as? String }, ["101", "101"])
        XCTAssertEqual(records.filter { $0.request.url.path == directoryPath }.count, 2)
        XCTAssertEqual(records.filter { $0.request.url.path == detailPath }.count, 2)
        _ = await f.service.close()
    }

    func testInvalidOrUnseedableCompleteLikesFailBeforeIntelligence() async throws {
        let base = try MusicQQWire.object(MusicNeteaseFixture.detail()["playlist"])
        let noDisplayMetadata = [101, 102].map { id -> [String: Any] in
            var row = MusicNeteaseFixture.song(id); row["ar"] = []
            return row
        }
        let cases: [(String, [String: Any], MusicServiceError)] = [
            ("empty", ["trackCount": 0, "trackIds": [], "tracks": []], .notFound),
            ("partial", ["trackCount": 3], .upstreamInvalid),
            ("duplicate", ["trackIds": [["id": 101], ["id": 101]]], .upstreamInvalid),
            ("foreign owner", ["creator": ["userId": 99]], .targetChanged),
            ("conflicting owners", ["userId": 99], .targetChanged),
            ("ordinary playlist", ["specialType": 0], .targetChanged),
            ("wrong playlist", ["id": 999], .targetChanged),
            ("no valid metadata", ["tracks": []], .notFound),
            ("duplicate metadata", ["tracks": [MusicNeteaseFixture.song(101), MusicNeteaseFixture.song(101)]], .upstreamInvalid),
            ("foreign metadata", ["tracks": [MusicNeteaseFixture.song(999)]], .upstreamInvalid)
        ]
        for (label, changes, expected) in cases {
            let f = try MusicNeteaseNativeFixture(); try await f.enable()
            let row = base.merging(changes) { _, replacement in replacement }
            await f.http.replies(detailPath, [try MusicNeteaseFixture.response(["code": 200, "playlist": row])])
            await f.http.replies(metadataPath, [try MusicNeteaseFixture.response(["code": 200, "songs": noDisplayMetadata])])
            let lease = try await f.service.beginAccountQueue(platform: .netease)
            do {
                _ = try await f.service.similarModeBatch(lease, request: .heartbeat(lastID: nil, fromHome: true, context: nil))
                XCTFail(label)
            } catch { XCTAssertEqual(error as? MusicServiceError, expected, label) }
            let records = await f.http.recorded()
            XCTAssertFalse(records.contains { $0.request.url.path == heartbeatPath }, label)
            _ = await f.service.close()
        }
    }

    func testPartialMetadataFailsBeforeIntelligenceDespiteExistingValidSeed() async throws {
        let f = try MusicNeteaseNativeFixture(); try await f.enable()
        var row = try MusicQQWire.object(MusicNeteaseFixture.detail([101, 102, 103])["playlist"])
        row["tracks"] = [MusicNeteaseFixture.song(101)]
        await f.http.replies(detailPath, [try MusicNeteaseFixture.response(["code": 200, "playlist": row])])
        await f.http.replies(metadataPath, [try MusicNeteaseFixture.response([
            "code": 200, "songs": [MusicNeteaseFixture.song(102)]
        ])])
        let lease = try await f.service.beginAccountQueue(platform: .netease)
        do {
            _ = try await f.service.similarModeBatch(lease, request: .heartbeat(lastID: nil, fromHome: true, context: nil))
            XCTFail("Missing member 103 was ignored because other valid seeds remained")
        } catch { XCTAssertEqual(error as? MusicServiceError, .upstreamInvalid) }
        let records = await f.http.recorded()
        XCTAssertEqual(records.filter { $0.request.url.path == metadataPath }.count, 1)
        XCTAssertFalse(records.contains { $0.request.url.path == heartbeatPath })
        _ = await f.service.close()
    }

    func testMissingEntireMetadataBatchFailsBeforeIntelligence() async throws {
        let f = try MusicNeteaseNativeFixture(); try await f.enable()
        await f.http.replies(detailPath, [try MusicNeteaseFixture.response(
            MusicNeteaseFixture.detail(includeTracks: false)
        )])
        await f.http.replies(metadataPath, [try MusicNeteaseFixture.response(["code": 200, "songs": []])])
        let lease = try await f.service.beginAccountQueue(platform: .netease)
        do {
            _ = try await f.service.similarModeBatch(lease, request: .heartbeat(lastID: nil, fromHome: true, context: nil))
            XCTFail("An empty metadata response was treated as an unseedable complete list")
        } catch { XCTAssertEqual(error as? MusicServiceError, .upstreamInvalid) }
        let records = await f.http.recorded()
        XCTAssertEqual(records.filter { $0.request.url.path == metadataPath }.count, 1)
        XCTAssertFalse(records.contains { $0.request.url.path == heartbeatPath })
        _ = await f.service.close()
    }

    func testDirectoryCannotClaimIncompleteDuplicateOrAmbiguousFavorites() async throws {
        let cases: [(String, [String: Any], MusicServiceError)] = [
            ("no favorite", ["more": false, "playlist": []], .targetChanged),
            ("two own favorites", ["more": false, "playlist": [MusicNeteaseFixture.collection(), MusicNeteaseFixture.collection(502)]], .targetChanged),
            ("duplicate ID", ["more": false, "playlist": [MusicNeteaseFixture.collection(), MusicNeteaseFixture.collection()]], .staleResult),
            ("empty continuation", ["more": true, "playlist": []], .upstreamInvalid),
            ("nonboolean continuation", ["more": 1, "playlist": [MusicNeteaseFixture.collection()]], .upstreamInvalid),
            ("missing owner", ["more": false, "playlist": [["id": 501, "specialType": 5]]], .upstreamInvalid)
        ]
        for (label, fields, expected) in cases {
            let f = try MusicNeteaseNativeFixture(); try await f.enable()
            await f.http.replies(directoryPath, [try MusicNeteaseFixture.response(fields.merging(["code": 200]) { _, value in value })])
            let lease = try await f.service.beginAccountQueue(platform: .netease)
            do {
                _ = try await f.service.similarModeBatch(lease, request: .heartbeat(lastID: nil, fromHome: true, context: nil))
                XCTFail(label)
            } catch { XCTAssertEqual(error as? MusicServiceError, expected, label) }
            let records = await f.http.recorded()
            XCTAssertFalse(records.contains { $0.request.url.path == detailPath || $0.request.url.path == heartbeatPath }, label)
            _ = await f.service.close()
        }
    }

    func testDirectoryResourceBoundaryFailsInsteadOfReturningPartialFavorites() async throws {
        let f = try MusicNeteaseNativeFixture(); try await f.enable()
        let pages = try (0..<20).map { page in
            try MusicNeteaseFixture.response(["code": 200, "more": true, "playlist": (0..<100).map {
                MusicNeteaseFixture.collection(1000 + page * 100 + $0, special: page == 0 && $0 == 0 ? 5 : 0)
            }])
        }
        await f.http.replies(directoryPath, pages)
        let lease = try await f.service.beginAccountQueue(platform: .netease)
        do {
            _ = try await f.service.similarModeBatch(lease, request: .heartbeat(lastID: nil, fromHome: true, context: nil))
            XCTFail("Partial directory accepted as complete favorites")
        } catch { XCTAssertEqual(error as? MusicServiceError, .limit) }
        let records = await f.http.recorded()
        XCTAssertEqual(records.filter { $0.request.url.path == directoryPath }.count, 20)
        XCTAssertFalse(records.contains { $0.request.url.path == detailPath || $0.request.url.path == heartbeatPath })
        _ = await f.service.close()
    }

    func testLiveAndLegacyHomeHeartbeatKeepWholeLargeBatchAndAttribution() async throws {
        let f = try MusicNeteaseNativeFixture(); try await f.enable()
        let ids = Array(1001..<2002)
        let raw = ids.map { ["songInfo": ["id": $0, "name": "合成", "ar": [["name": "歌手"]]], "alg": "alg-\($0)"] as [String: Any] }
        await f.http.replies(heartbeatPath, [try MusicNeteaseFixture.response(["code": 200, "data": raw])])
        let lease = try await f.service.beginAccountQueue(platform: .netease)
        let live = try await f.service.similarModeBatch(lease, request: .heartbeat(lastID: "102", fromHome: false, context: nil))
        XCTAssertEqual(live.items.count, ids.count + 1); XCTAssertNil(live.hasMore)
        var legacy: [MusicNativeTrackData] = [], cursor: String?
        repeat {
            let page = try await f.service.homeActionPage(lease, action: .heartbeat, cursor: cursor)
            legacy += page.items; cursor = page.cursor
            XCTAssertEqual(page.total, ids.count + 1)
        } while cursor != nil
        for items in [live.items, legacy] {
            let songs = try items.dropFirst().map { try MusicQQWire.decode($0.data) }
            XCTAssertEqual(songs.compactMap { $0["songmid"] as? String }, ids.map(String.init))
            for (song, id) in zip(songs, ids) {
                let attribution = try MusicQQWire.object(song["_recommendation"])
                XCTAssertEqual(attribution["source"] as? String, "heartbeat")
                XCTAssertEqual(attribution["feedbackSource"] as? String, "aiMode")
                XCTAssertEqual(attribution["sourceId"] as? String, "501")
                XCTAssertEqual(attribution["alg"] as? String, "alg-\(id)")
            }
        }
        let records = await f.http.recorded()
        XCTAssertEqual(records.filter { $0.request.url.path == heartbeatPath }.count, 2)
        XCTAssertEqual(records.filter { $0.request.url.path == directoryPath }.count, 2)
        _ = await f.service.close()
    }

    func testCompleteTracksOnlyLikesPreserveCountStartAndEveryRandomCandidate() async throws {
        let ids = [103, 101, 102], expected = ids.map(String.init)
        for nullTrackIDs in [false, true] {
            for count in [2, 3] {
                let f = try MusicNeteaseNativeFixture()
                let adapter = NeteaseMusicAccountAdapter(transport: f.http, now: { [clock = f.clock] in clock.now() })
                var row = try MusicQQWire.object(MusicNeteaseFixture.detail(ids)["playlist"])
                row.removeValue(forKey: "trackIds")
                if nullTrackIDs { row["trackIds"] = NSNull() }
                row["trackCount"] = count; row["cloudTrackCount"] = 1
                await f.http.replies(detailPath, [try MusicNeteaseFixture.response(["code": 200, "playlist": row])])
                await f.http.replies(heartbeatPath, [try MusicNeteaseFixture.response([
                    "code": 200, "data": [["songInfo": MusicNeteaseFixture.song(104)]]
                ])])
                let batch = try await adapter.similarMode(.heartbeat(lastID: nil, fromHome: true, context: nil),
                    credential: MusicNeteaseFixture.credential(), authorize: {}, chooseLikedSong: { candidates in
                        XCTAssertEqual(candidates, expected)
                        return "102"
                    })
                XCTAssertEqual(batch.heartbeatContext, .init(playlistID: "501", seedID: "102"))
                XCTAssertEqual(batch.items.count, 2)
                let records = await f.http.recorded()
                let requests = records.filter { $0.request.url.path == heartbeatPath }
                XCTAssertEqual(requests.count, 1)
                let payload = try MusicQQWire.decode(XCTUnwrap(requests.first?.payload))
                XCTAssertEqual(payload["count"] as? Int, 3)
                XCTAssertEqual(payload["startMusicId"] as? String, "102")
                XCTAssertEqual(payload["songId"] as? String, "102")
                XCTAssertFalse(records.contains { $0.request.url.path == metadataPath })
            }
        }
    }

    func testIncompleteDuplicateOrExplicitIncompleteTracksOnlyLikesFailBeforeIntelligence() async throws {
        var base = try MusicQQWire.object(MusicNeteaseFixture.detail()["playlist"])
        base.removeValue(forKey: "trackIds")
        let cases: [(String, [String: Any])] = [
            ("incomplete tracks", ["trackCount": 3]),
            ("duplicate tracks", ["tracks": [MusicNeteaseFixture.song(101), MusicNeteaseFixture.song(101)]]),
            ("empty tracks with members", ["tracks": []]),
            ("empty tracks with cloud members", ["trackCount": 0, "cloudTrackCount": 1, "tracks": []]),
            ("explicit incomplete trackIds", ["trackIds": [["id": 101]]]),
            ("explicit empty trackIds", ["trackIds": []])
        ]
        for nullTrackIDs in [false, true] {
            for (label, changes) in cases {
                let f = try MusicNeteaseNativeFixture(); try await f.enable()
                var row = base
                if nullTrackIDs { row["trackIds"] = NSNull() }
                row.merge(changes) { _, replacement in replacement }
                await f.http.replies(detailPath, [try MusicNeteaseFixture.response(["code": 200, "playlist": row])])
                let lease = try await f.service.beginAccountQueue(platform: .netease)
                do {
                    _ = try await f.service.similarModeBatch(lease, request: .heartbeat(lastID: nil, fromHome: true, context: nil))
                    XCTFail(label)
                } catch { XCTAssertEqual(error as? MusicServiceError, .upstreamInvalid, label) }
                let records = await f.http.recorded()
                XCTAssertFalse(records.contains { $0.request.url.path == metadataPath || $0.request.url.path == heartbeatPath }, label)
                _ = await f.service.close()
            }
        }
    }

    func testEmptyTracksOnlyLikesRemainNotFound() async throws {
        for nullTrackIDs in [false, true] {
            let f = try MusicNeteaseNativeFixture(); try await f.enable()
            var row = try MusicQQWire.object(MusicNeteaseFixture.detail([])["playlist"])
            row.removeValue(forKey: "trackIds")
            if nullTrackIDs { row["trackIds"] = NSNull() }
            row["cloudTrackCount"] = 0
            await f.http.replies(detailPath, [try MusicNeteaseFixture.response(["code": 200, "playlist": row])])
            let lease = try await f.service.beginAccountQueue(platform: .netease)
            do {
                _ = try await f.service.similarModeBatch(lease, request: .heartbeat(lastID: nil, fromHome: true, context: nil))
                XCTFail("Complete empty favorites were not reported as notFound")
            } catch { XCTAssertEqual(error as? MusicServiceError, .notFound) }
            let records = await f.http.recorded()
            XCTAssertFalse(records.contains { $0.request.url.path == metadataPath || $0.request.url.path == heartbeatPath })
            _ = await f.service.close()
        }
    }
    func testDisplayedHomeSongIsFirstAndNullSongInfoHydratesByTopLevelID() async throws {
        let f = try MusicNeteaseNativeFixture(); try await f.enable()
        await f.http.replies(heartbeatPath, [try MusicNeteaseFixture.response(["code": 200, "data": [
            ["id": 103, "songInfo": NSNull(), "alg": "null-info"],
            ["songInfo": MusicNeteaseFixture.song(102), "alg": "shown"]
        ]])])
        await f.http.replies(metadataPath, [try MusicNeteaseFixture.response(["code": 200, "songs": [MusicNeteaseFixture.song(103)]])])
        let lease = try await f.service.beginAccountQueue(platform: .netease)
        let batch = try await f.service.similarModeBatch(lease, request: .heartbeat(lastID: "102", fromHome: true, context: nil))
        let songs = try batch.items.map { try MusicQQWire.decode($0.data) }
        XCTAssertEqual(songs.compactMap { $0["songmid"] as? String }, ["102", "103"])
        let records = await f.http.recorded(), call = try XCTUnwrap(records.first { $0.request.url.path == heartbeatPath })
        let data = try MusicQQWire.decode(XCTUnwrap(call.payload))
        XCTAssertEqual(data["songId"] as? String, "102"); XCTAssertEqual(data["startMusicId"] as? String, "102")
        do {
            _ = try await f.service.similarModeBatch(lease, request: .heartbeat(lastID: "999", fromHome: false, context: nil))
            XCTFail("unverified member accepted")
        } catch { XCTAssertEqual(error as? MusicServiceError, .sourceUnavailable) }
        _ = await f.service.close()
    }

    func testDisplayedHomeSongOutsideLikesIsRetainedWithoutReplacingItWithAnotherSeed() async throws {
        let f = try MusicNeteaseNativeFixture(); try await f.enable()
        await f.http.replies(heartbeatPath, [try MusicNeteaseFixture.response(["code": 200, "data": [
            ["songInfo": MusicNeteaseFixture.song(103)]
        ]])])
        await f.http.replies(metadataPath, [try MusicNeteaseFixture.response([
            "code": 200, "songs": [MusicNeteaseFixture.song(999)]
        ])])
        let lease = try await f.service.beginAccountQueue(platform: .netease)
        let batch = try await f.service.similarModeBatch(lease, request: .heartbeat(lastID: "999", fromHome: true, context: nil))
        let songs = try batch.items.map { try MusicQQWire.decode($0.data) }
        XCTAssertEqual(songs.compactMap { $0["songmid"] as? String }, ["999", "103"])
        let calls = await f.http.recorded()
        let request = try XCTUnwrap(calls.first { $0.request.url.path == heartbeatPath })
        let body = try MusicQQWire.decode(XCTUnwrap(request.payload))
        XCTAssertEqual(body["songId"] as? String, "999")
        XCTAssertEqual(body["startMusicId"] as? String, "999")
        XCTAssertEqual(body["playlistId"] as? String, "501")
        _ = await f.service.close()
    }

}
