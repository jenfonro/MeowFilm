import Foundation
import XCTest
import MeowAccounts
import MeowMusicFeature
import MeowJSON
@testable import MeowMusicPlatform

final class MusicNeteaseBusinessTests: XCTestCase {
    func testAccountCollectionRetainsAlbumArtworkAndItsImageParameters() async throws {
        let f = try MusicNeteaseNativeFixture(); try await f.enable()
        let image = "http://p1.music.126.net/album.jpg?param=800y800&quality=90"
        var song = MusicNeteaseFixture.song()
        song["al"] = ["id": 301, "name": "专辑", "picUrl": image]
        var detail = MusicNeteaseFixture.detail()
        var playlist = try XCTUnwrap(detail["playlist"] as? [String: Any])
        playlist["tracks"] = [song, MusicNeteaseFixture.song(102)]
        detail["playlist"] = playlist
        await f.http.replies("/eapi/v6/playlist/detail", [try MusicNeteaseFixture.response(detail)])
        let page = try await f.service.nativeTracks(platform: .netease, collectionID: "501", cursor: nil)
        let row = try MusicQQWire.decode(XCTUnwrap(page.items.first).data)
        XCTAssertEqual(row["img"] as? String, image.replacingOccurrences(of: "http:", with: "https:"))
        let calls = await f.http.recorded()
        XCTAssertFalse(calls.contains { $0.request.url.path == "/weapi/v3/song/detail" })
        _ = await f.service.close()
    }
    func testAccountCollectionReadsMoreThanTwoThousandSongsWithoutHydratingEveryPage() async throws {
        let f = try MusicNeteaseNativeFixture(); try await f.enable()
        let ids = Array(101..<2102)
        var entry = MusicNeteaseFixture.collection(); entry["trackCount"] = ids.count
        var detail = MusicNeteaseFixture.detail([])
        var playlist = try XCTUnwrap(detail["playlist"] as? [String: Any])
        playlist["trackCount"] = ids.count
        playlist["trackIds"] = ids.map { ["id": $0] }
        // Keep display metadata for every member without exceeding the
        // production 256 KiB response bound. IDs alone would force hydration.
        playlist["tracks"] = ids.map { ["id": $0, "name": "x", "ar": [["name": "x"]]] }
        detail["playlist"] = playlist
        await f.http.replies("/weapi/user/playlist", [
            try MusicNeteaseFixture.response(["code": 200, "more": false, "playlist": [entry]])
        ])
        await f.http.replies("/eapi/v6/playlist/detail", [
            try MusicNeteaseFixture.response(detail)
        ])
        let lease = try await f.service.beginAccountQueue(platform: .netease)
        var cursor: String?, actual: [String] = []
        repeat {
            let page = try await f.service.accountQueuePage(lease, kind: .collection(id: "501"), cursor: cursor)
            XCTAssertEqual(page.total, ids.count)
            actual += try page.items.map { try MusicQQWire.string(MusicQQWire.decode($0.data)["songmid"]) }
            cursor = page.cursor
        } while cursor != nil
        XCTAssertEqual(actual, ids.map(String.init))
        let requests = await f.http.recorded()
        XCTAssertEqual(requests.filter { $0.request.url.path == "/eapi/v6/playlist/detail" }.count, 2)
        XCTAssertEqual(requests.filter { $0.request.url.path == "/weapi/v3/song/detail" }.count, 0)
        await f.service.endAccountQueue(lease); _ = await f.service.close()
    }

    private func displayFixture() async throws -> MusicNeteaseNativeFixture {
        let f = try MusicNeteaseNativeFixture(); try await f.enable()
        let ids = Array(101..<136)
        var entry = MusicNeteaseFixture.collection(); entry["trackCount"] = ids.count
        var detail = entry
        detail["trackIds"] = ids.map { ["id": $0] }
        detail["tracks"] = ids.map { id -> [String: Any] in
            var song = MusicNeteaseFixture.song(id)
            if id == 133 { song["ar"] = [["id": 900]] }
            else if id >= 131 { song.removeValue(forKey: "name") }
            return song
        }
        await f.http.replies("/weapi/user/playlist", [
            try MusicNeteaseFixture.response(["code": 200, "more": false, "playlist": [entry]])
        ])
        await f.http.replies("/eapi/v6/playlist/detail", [
            try MusicNeteaseFixture.response(["code": 200, "playlist": detail])
        ])
        await f.http.replies("/weapi/v3/song/detail", [
            try MusicNeteaseFixture.response(["code": 200, "songs": (131..<136).map(MusicNeteaseFixture.song)])
        ])
        return f
    }
    func testCollectionDisplayKeepsCompleteMembersButOnlyHydratesExplicitlyOpenedMissingPage() async throws {
        let f = try await displayFixture()
        let first = try await f.service.nativeTracks(platform: .netease, collectionID: "501", cursor: nil)
        XCTAssertEqual(first.items.count, 30); XCTAssertEqual(first.total, 35)
        var requests = await f.http.recorded()
        XCTAssertEqual(requests.filter { $0.request.url.path == "/weapi/v3/song/detail" }.count, 0)
        XCTAssertEqual(requests.filter { $0.request.url.path == "/eapi/v6/playlist/detail" }.count, 2)
        let cursor = try XCTUnwrap(first.cursor)
        let second = try await f.service.nativeTracks(platform: .netease, collectionID: "501", cursor: cursor)
        let replay = try await f.service.nativeTracks(platform: .netease, collectionID: "501", cursor: cursor)
        XCTAssertEqual(second.items.map(\.data), replay.items.map(\.data))
        XCTAssertEqual(second.total, 35); XCTAssertNil(second.cursor)
        let rows = try second.items.map { try MusicQQWire.decode($0.data) }
        XCTAssertEqual(rows.compactMap { $0["songmid"] as? String }, (131..<136).map(String.init))
        XCTAssertEqual(rows.compactMap { $0["name"] as? String }, (131..<136).map { "合成歌曲\($0)" })
        requests = await f.http.recorded()
        XCTAssertEqual(requests.filter { $0.request.url.path == "/weapi/v3/song/detail" }.count, 1)
        XCTAssertEqual(requests.filter { $0.request.url.path == "/eapi/v6/playlist/detail" }.count, 2)
        XCTAssertFalse(requests.contains { $0.request.url.path.contains("player/url") })
        _ = await f.service.close()
    }
    func testLateCollectionDisplayHydrationCannotRestorePageAfterLogout() async throws {
        let f = try await displayFixture(), gate = MusicAccountTestGate()
        let first = try await f.service.nativeTracks(platform: .netease, collectionID: "501", cursor: nil)
        let cursor = try XCTUnwrap(first.cursor)
        await f.http.hold("/weapi/v3/song/detail", gate)
        let task = Task { try await f.service.nativeTracks(platform: .netease, collectionID: "501", cursor: cursor) }
        await gate.started()
        try await f.service.logout(platform: .netease)
        await gate.release()
        do { _ = try await task.value; XCTFail("Logged-out page was published") }
        catch { XCTAssertNotNil(error as? MusicServiceError) }
        XCTAssertNil(f.store.peek(f.identity))
        do { _ = try await f.service.nativeTracks(platform: .netease, collectionID: "501", cursor: cursor); XCTFail("Retired cursor was reused") }
        catch { XCTAssertNotNil(error as? MusicServiceError) }
        _ = await f.service.close()
    }
    func testDailyPagingChinaSixAMCacheBoundaryAndFMNeverFabricateContinuation() async throws {
        let f = try MusicNeteaseNativeFixture(); try await f.enable()
        let first = try await f.service.nativeRecommendations(platform: .netease, kind: "daily", cursor: nil)
        XCTAssertEqual(first.items.count, 30)
        let second = try await f.service.nativeRecommendations(platform: .netease, kind: "daily", cursor: XCTUnwrap(first.cursor))
        XCTAssertEqual(second.items.count, 5); XCTAssertNil(second.cursor)
        _ = try await f.service.nativeRecommendations(platform: .netease, kind: "daily", cursor: nil)
        let cached = await f.http.recorded()
        XCTAssertEqual(cached.filter { $0.request.url.path == "/xeapi/v3/discovery/recommend/songs" }.count, 1)
        let fm = try await f.service.nativeRecommendations(platform: .netease, kind: "fm", cursor: nil)
        XCTAssertEqual(fm.items.count, 1); XCTAssertNil(fm.cursor)
        let next = NeteaseMusicAccountAdapter.nextDailyBoundary(f.clock.now())
        f.clock.advance(next.timeIntervalSince(f.clock.now()) - 1)
        let nearBoundary = try await f.service.nativeRecommendations(platform: .netease, kind: "daily", cursor: nil)
        f.clock.advance(2)
        do {
            _ = try await f.service.nativeRecommendations(platform: .netease, kind: "daily", cursor: XCTUnwrap(nearBoundary.cursor))
            XCTFail("Yesterday's daily cursor")
        } catch { XCTAssertEqual(error as? MusicServiceError, .staleResult) }
        _ = try await f.service.nativeRecommendations(platform: .netease, kind: "daily", cursor: nil)
        let requests = await f.http.recorded()
        XCTAssertEqual(requests.filter { $0.request.url.path == "/xeapi/v3/discovery/recommend/songs" }.count, 2)
        XCTAssertFalse(requests.contains { $0.request.url.path.contains("player/url") })
        _ = await f.service.close()
    }
    func testDirectoryAndMemberDoubleReadsKeepOrderAndMissingMetadata() async throws {
        let f = try MusicNeteaseNativeFixture(); try await f.enable()
        let catalog = try await f.service.nativeCollections(platform: .netease, cursor: nil)
        XCTAssertEqual(catalog.items.map(\.owned), [true, false])
        XCTAssertEqual(catalog.items.map(\.id), ["501", "502"])
        await f.http.replies("/eapi/v6/playlist/detail", [try MusicNeteaseFixture.response(MusicNeteaseFixture.detail([102, 101], includeTracks: false))])
        await f.http.replies("/weapi/v3/song/detail", [.init(status: 503, data: Data())])
        let tracks = try await f.service.nativeTracks(platform: .netease, collectionID: "501", cursor: nil)
        let rows = try tracks.items.map { try MusicQQWire.decode($0.data) }
        XCTAssertEqual(rows.compactMap { $0["songmid"] as? String }, ["102", "101"])
        XCTAssertTrue(rows.allSatisfy { $0["name"] as? String == "未知歌曲" })
        let requests = await f.http.recorded(), paths = requests.map(\.request.url.path)
        XCTAssertEqual(paths.filter { $0 == "/eapi/v6/playlist/detail" }.count, 2)
        XCTAssertEqual(paths.filter { $0 == "/weapi/user/playlist" }.count, 4)
        XCTAssertFalse(paths.contains { $0.contains("/manipulate") || $0.contains("/create") || $0.contains("/remove") })
        _ = await f.service.close()
    }
    func testDirectoryChangedMissingFavoritesAndNonBooleanMoreAreNeverEmptySuccess() async throws {
        let good = try MusicNeteaseFixture.response(["code": 200, "more": false, "playlist": [MusicNeteaseFixture.collection()]])
        var changed = MusicNeteaseFixture.collection(); changed["name"] = "并发修改"
        let cases: [([MusicAccountHTTPResponse], MusicServiceError)] = [
            ([good, try MusicNeteaseFixture.response(["code": 200, "more": false, "playlist": [changed]])], .staleResult),
            ([try MusicNeteaseFixture.response(["code": 200, "more": false, "playlist": []])], .upstreamInvalid),
            ([try MusicNeteaseFixture.response(["code": 200, "more": 1, "playlist": [MusicNeteaseFixture.collection()]])], .upstreamInvalid)
        ]
        for (responses, expected) in cases {
            let f = try MusicNeteaseNativeFixture(); try await f.enable()
            await f.http.replies("/weapi/user/playlist", responses)
            do { _ = try await f.service.nativeCollections(platform: .netease, cursor: nil); XCTFail("Incomplete catalog") }
            catch { XCTAssertEqual(error as? MusicServiceError, expected) }
            _ = await f.service.close()
        }
    }
    func testChangedOrDuplicateMembersAndAuthenticationDuringMetadataFailClosed() async throws {
        for mode in ["changed", "duplicate", "authentication"] {
            let f = try MusicNeteaseNativeFixture(); try await f.enable()
            let a = try MusicNeteaseFixture.response(MusicNeteaseFixture.detail())
            if mode == "changed" {
                await f.http.replies("/eapi/v6/playlist/detail",
                                     [a, try MusicNeteaseFixture.response(MusicNeteaseFixture.detail([102, 101]))])
            } else if mode == "duplicate" {
                await f.http.replies("/eapi/v6/playlist/detail", [try MusicNeteaseFixture.response(MusicNeteaseFixture.detail([101, 101]))])
            } else {
                await f.http.replies("/eapi/v6/playlist/detail", [try MusicNeteaseFixture.response(MusicNeteaseFixture.detail(includeTracks: false))])
                await f.http.replies("/weapi/v3/song/detail", [.init(status: 401, data: Data())])
            }
            do { _ = try await f.service.nativeTracks(platform: .netease, collectionID: "501", cursor: nil); XCTFail("Incomplete members") }
            catch {
                let expected: MusicServiceError = mode == "changed" ? .staleResult : mode == "duplicate" ? .upstreamInvalid : .sessionExpired
                XCTAssertEqual(error as? MusicServiceError, expected)
            }
            if mode == "authentication" { XCTAssertNil(f.store.peek(f.identity)) }
            _ = await f.service.close()
        }
    }
    func testCapabilitiesAreMetadataOnlyAndExactOfficialSelectionHasBoundedTTL() async throws {
        let f = try MusicNeteaseNativeFixture(); try await f.enable()
        let options = try await f.service.nativeOptions(platform: .netease, nativeID: "101", sourceKey: "wy:101")
        let option = try XCTUnwrap(options.first)
        XCTAssertEqual(Set(option.qualities), ["128k", "192k", "320k", "flac", "flac24bit", "jyeffect", "dolby", "sky", "master", "vivid"])
        let before = await f.http.recorded()
        XCTAssertFalse(before.contains { $0.request.url.path.contains("player/url") })
        let result = try await f.service.nativeResolve(platform: .netease, nativeID: "101", selection: option.selection(quality: "sky"))
        XCTAssertEqual(result.url.host, "m801.music.126.net"); XCTAssertTrue(result.headers.isEmpty)
        XCTAssertEqual(result.expiresAt, f.clock.now().addingTimeInterval(1800))
        let requests = await f.http.recorded()
        let player = try XCTUnwrap(requests.first { $0.request.url.path.contains("player/url") })
        let data = try MusicQQWire.decode(XCTUnwrap(player.payload))
        XCTAssertEqual(data["level"] as? String, "sky"); XCTAssertEqual(data["immerseType"] as? String, "c51")
        XCTAssertEqual(data["ids"] as? String, "[101]")
        XCTAssertEqual(requests.filter { $0.request.url.path.contains("player/url") }.count, 1)
        _ = await f.service.close()
    }
    func testUnknownOfficialQualityStillPreservesResponseDuration() async throws {
        let durations: [Double?] = [191, 190, 0, nil]
        for duration in durations {
            let f = try MusicNeteaseNativeFixture(); try await f.enable()
            let options = try await f.service.nativeOptions(platform: .netease, nativeID: "101", sourceKey: "wy:101")
            var response = MusicNeteaseFixture.audio(level: "unrecognized")
            var rows = try XCTUnwrap(response["data"] as? [[String: Any]])
            if let duration { rows[0]["time"] = duration * 1000 }
            response["data"] = rows
            await f.http.replies("/xeapi/song/enhance/player/url/v1", [try MusicNeteaseFixture.response(response)])
            let audio = try await f.service.nativeResolve(platform: .netease, nativeID: "101",
                selection: XCTUnwrap(options.first).selection(quality: "flac"))
            XCTAssertNil(audio.delivery, "Unknown quality must not become the requested quality")
            XCTAssertEqual(audio.duration, duration.flatMap { $0 > 0 ? $0 : nil })
            _ = await f.service.close()
        }
    }
    func testOfficialWrongIdentityActualTrialAndUnsafeURLNeverRetryOrReturnURL() async throws {
        var responses: [[String: Any]] = [
            MusicNeteaseFixture.audio(id: 999),
            MusicNeteaseFixture.audio(url: "https://127.0.0.1/private")
        ]
        for marker in ["freeTrialInfo", "trial", "preview"] {
            var response = MusicNeteaseFixture.audio()
            var rows = try XCTUnwrap(response["data"] as? [[String: Any]])
            rows[0][marker] = ["start": 0, "end": 30]
            response["data"] = rows; responses.append(response)
        }
        for response in responses {
            let f = try MusicNeteaseNativeFixture(); try await f.enable()
            let options = try await f.service.nativeOptions(platform: .netease, nativeID: "101", sourceKey: "wy:101")
            await f.http.replies("/xeapi/song/enhance/player/url/v1", [try MusicNeteaseFixture.response(response)])
            do {
                _ = try await f.service.nativeResolve(platform: .netease, nativeID: "101", selection: options[0].selection(quality: "flac"))
                XCTFail("Wrong or unproven resource")
            } catch { XCTAssertNotNil(error as? MusicServiceError) }
            let requests = await f.http.recorded()
            XCTAssertEqual(requests.filter { $0.request.url.path.contains("player/url") }.count, 1)
            _ = await f.service.close()
        }
    }
    func testMembershipFactsDoNotReplaceResourceCapabilities() async throws {
        for mode in ["unknown", "expired", "wrong"] {
            let f = try MusicNeteaseNativeFixture(); try await f.enable()
            let at = Int(f.clock.now().timeIntervalSince1970 * 1000)
            var rights: [String: Any] = ["userId": mode == "wrong" ? 99 : 42, "now": at]
            if mode != "unknown" { rights["associator"] = ["expireTime": mode == "expired" ? at - 1000 : at + 300_000] }
            await f.http.replies("/weapi/music-vip-membership/client/vip/info",
                                 [try MusicNeteaseFixture.response(["code": 200, "data": rights])])
            let options = try await f.service.nativeOptions(platform: .netease, nativeID: "101", sourceKey: "wy:101")
            XCTAssertTrue(options.first?.qualities.contains("flac") == true)
            let requests = await f.http.recorded()
            XCTAssertFalse(requests.contains { $0.request.url.path.contains("player/url") || $0.request.url.path.contains("vip/info") })
            _ = await f.service.close()
        }
    }
    func testSameSongIDAcrossPlatformsDoesNotReplaceGrantOrCancelOtherAccount() async throws {
        struct Router: MusicAccountHTTPTransport {
            let wy: MusicNeteaseTestHTTP
            let qq: MusicAccountTestHTTP
            func send(_ request: MusicAccountHTTPRequest) async throws -> MusicAccountHTTPResponse {
                if request.url.host?.hasSuffix("163.com") == true { return try await wy.send(request) }
                return try await qq.send(request)
            }
        }
        let clock = MusicAccountTestClock(), store = MusicAccountTestStore()
        let wy = MusicNeteaseTestHTTP(clock: clock), qq = MusicAccountTestHTTP(clock: clock)
        let wyID = try MusicAccountFixture.identity("wy"), qqID = try MusicAccountFixture.identity()
        try store.write(.init(credential: MusicNeteaseFixture.credential()), for: wyID)
        try store.write(.init(credential: MusicAccountFixture.credential()), for: qqID)
        await qq.replies("CgiGetTrackInfo", try MusicAccountFixture.rpc(["tracks": [MusicAccountFixture.song("101")]]))
        let service = NativeMusicAccountService(accounts: AccountSessionCoordinator(store: store),
            scopes: [.netease: wyID, .qq: qqID], transport: Router(wy: wy, qq: qq), clock: { clock.now() })
        _ = try await service.setAccess(available: true, generation: 1)
        let wyOptions = try await service.nativeOptions(platform: .netease, nativeID: "101", sourceKey: "wy:101")
        _ = try await service.nativeOptions(platform: .qq, nativeID: "101", sourceKey: "tx:101")
        _ = try await service.nativeResolve(platform: .netease, nativeID: "101", selection: wyOptions[0].selection(quality: "flac"))
        try await service.logout(platform: .qq)
        let result = try await service.nativeResolve(platform: .netease, nativeID: "101", selection: wyOptions[0].selection(quality: "flac"))
        XCTAssertEqual(result.url.host, "m801.music.126.net")
        XCTAssertNotNil(store.peek(wyID)); XCTAssertNil(store.peek(qqID))
        _ = await service.close()
    }
}
