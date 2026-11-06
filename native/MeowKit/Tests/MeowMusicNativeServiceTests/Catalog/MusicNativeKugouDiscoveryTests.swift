import XCTest
import CryptoKit
import MeowMusicFeature
@testable import MeowMusicNativeService

private typealias F = KugouDiscoveryFixture

final class MusicNativeKugouDiscoveryTests: XCTestCase {
    private func song(_ item: MusicPublicBrowseItem, id: Int, title: String, duration: Double,
                      file: StaticString = #filePath, line: UInt = #line) {
        XCTAssertEqual(item.id, F.hash(id), file: file, line: line)
        guard case .track(let track) = item.content else { return XCTFail("Expected track", file: file, line: line) }
        XCTAssertEqual(track.title, title, file: file, line: line)
        XCTAssertEqual(track.artist, "Synthetic singer", file: file, line: line)
        XCTAssertEqual(track.duration, duration, file: file, line: line)
        XCTAssertEqual(track.sources.count, 1, file: file, line: line)
        XCTAssertEqual(track.sources.first?.platform, "kg", file: file, line: line)
        XCTAssertEqual(track.sources.first?.nativeId, F.hash(id), file: file, line: line)
        XCTAssertTrue(track.songId.hasPrefix("song:"), file: file, line: line)
    }
    private func rank(_ item: MusicPublicBrowseItem, file: StaticString = #filePath,
                      line: UInt = #line) throws -> MusicPublicBrowseChannel {
        XCTAssertEqual(item.id, "rank-8888", file: file, line: line)
        guard case .channel(let channel) = item.content else {
            XCTFail("A rank must open as a channel, not a playlist", file: file, line: line)
            throw MusicServiceError.invalidResponse
        }
        XCTAssertEqual(channel.id, "rank-8888", file: file, line: line)
        XCTAssertEqual(channel.pageCode, "156133", file: file, line: line)
        XCTAssertEqual(channel.kind, .rankings, file: file, line: line)
        XCTAssertEqual(channel.platform, .kugou, file: file, line: line)
        let preview = try XCTUnwrap(item.ranking?.preview.first, file: file, line: line)
        XCTAssertEqual(preview.title, "Synthetic preview", file: file, line: line)
        XCTAssertEqual(preview.artist, "Synthetic singer", file: file, line: line)
        XCTAssertEqual(preview.rank, 1, file: file, line: line)
        XCTAssertNil(preview.lastRank, "No invented movement/new badge", file: file, line: line)
        XCTAssertEqual(item.ranking?.updateFrequency, "每天", file: file, line: line)
        return channel
    }
    func testHomeReadsOnlySummariesAndPreservesNativeIdentities() async throws {
        let gateway = KugouGateway { call, _ in
            switch call.path {
            case F.recommend:
                XCTAssertEqual(call.method, "POST")
                XCTAssertEqual(call.header("x-router"), "specialrec.service.kugou.com")
                XCTAssertEqual(call.body["page"], .n(1))
                XCTAssertEqual(call.body["pagesize"], .n(20))
                XCTAssertEqual(call.body["special_recommend"]["categoryid"], .n(0))
            case F.newSongs:
                XCTAssertEqual(call.method, "POST")
                XCTAssertEqual(call.body["page"], .n(1))
                XCTAssertEqual(call.body["rank_id"], .n(21608))
                XCTAssertEqual(call.body["pagesize"], .n(12))
            case F.rankList:
                XCTAssertEqual(call.method, "GET")
                XCTAssertEqual(call.query["plat"], "2")
                XCTAssertEqual(call.query["withsong"], "1")
                XCTAssertEqual(call.query["parentid"], "0")
            default: return nil
            }
            return F.homeResponse(call)
        }
        let value = try await kugouService(gateway)
        let page = try await value.homePage(cursor: nil, refresh: true)
        XCTAssertEqual(page.sections.map(\.layout), [.playlistRail, .songColumns, .rankingStack])
        XCTAssertEqual(page.sections.compactMap(\.channel?.id), ["playlist", "newsong", "chart"])
        let playlists = try XCTUnwrap(page.kugouSection("playlists"))
        XCTAssertEqual(playlists.items.count, 20)
        guard case .collection(let playlist) = playlists.items[0].content else { return XCTFail("Expected collection") }
        XCTAssertEqual(playlist.nativeID, "collection_3_123456_1_0")
        XCTAssertEqual(playlist.platform, .kugou)
        XCTAssertEqual(playlist.count, 196)
        XCTAssertEqual(playlist.artwork?.absoluteString, "https://imge.kugou.com/synthetic/480/playlist.jpg")
        song(try XCTUnwrap(page.kugouSection("new")?.items.first), id: 1, title: "Synthetic new song 1", duration: 279)
        _ = try rank(XCTUnwrap(page.kugouSection("ranks")?.items.first))
        XCTAssertNotNil(page.cursor)
        await finishKugou(value, gateway, paths: [F.recommend, F.newSongs, F.rankList])
    }
    func testHomeOverflowIsRetainedAndContinuationReadsOnlyRecommendations() async throws {
        let gateway = KugouGateway { call, _ in
            switch call.path {
            case F.recommend:
                return call.body["page"].int == 1 ? F.playlists(25, more: true) : F.playlists(1, start: 100)
            case F.newSongs: return F.newSongPage(0, total: 0)
            case F.rankList: return F.ranks()
            default: return nil
            }
        }
        let value = try await kugouService(gateway)
        let first = try await value.homePage(cursor: nil, refresh: true)
        XCTAssertEqual(first.kugouSection("playlists")?.items.count, 25)
        XCTAssertNil(first.kugouSection("new"))
        let cursor = try XCTUnwrap(first.cursor)
        let second = try await value.homePage(cursor: cursor, refresh: false)
        XCTAssertEqual(second.sections.count, 1)
        XCTAssertEqual(second.sections.first?.id, first.kugouSection("playlists")?.id)
        XCTAssertEqual(second.kugouCollectionIDs, ["collection_3_123456_100_0"])
        XCTAssertNil(second.cursor)
        await assertMusicFailure(.staleResult) {
            _ = try await value.publicDiscovery(.channel(.kugou("feature", .featured), tag: ""), cursor: cursor)
        }
        await finishKugou(value, gateway, paths: [F.recommend, F.newSongs, F.rankList, F.recommend])
    }
    func testDirectoryPublishesFourPrimaryChannelsAndSixNonemptyGroupsInProviderOrder() async throws {
        let gateway = KugouGateway { call, _ in
            guard call.path == F.tags else { return nil }
            XCTAssertEqual(call.method, "POST")
            XCTAssertEqual(call.body, .obj([("tag_type", .string("collection")), ("tag_id", .n(0)), ("source", .n(3))]))
            return F.tagGroups(extraEmpty: true)
        }
        let value = try await kugouService(gateway)
        let page = try await value.publicDiscovery(.channels, cursor: nil)
        let channels = page.kugouItems.compactMap { item -> MusicPublicBrowseChannel? in
            if case .channel(let row) = item.content { return row }; return nil
        }
        XCTAssertEqual(channels.map(\.id), ["feature", "playlist", "chart", "newsong",
            "category-5", "category-1", "category-2", "category-8", "category-4", "category-7"])
        XCTAssertEqual(channels.dropFirst(4).map(\.title), ["场景", "主题", "语种", "风格", "心情", "年代"])
        XCTAssertEqual(channels.map(\.kind), [.featured, .playlists, .rankings, .newSongs] + Array(repeating: .playlists, count: 6))
        XCTAssertTrue(channels.allSatisfy { $0.platform == .kugou && $0.pageCode.isEmpty })
        XCTAssertNil(page.cursor)
        await finishKugou(value, gateway, paths: [F.tags])
    }
    func testPlaylistTagsRetainGroupsSelectionAndDefaultRecommendation() async throws {
        let gateway = KugouGateway { call, _ in
            switch call.path {
            case F.tags: return F.tagGroups()
            case F.recommend: return F.playlists(1)
            default: return nil
            }
        }
        let value = try await kugouService(gateway), channel = MusicPublicBrowseChannel.kugou("playlist", .playlists)
        let defaults = try await value.publicDiscovery(.channel(channel, tag: ""), cursor: nil)
        XCTAssertEqual(defaults.selectedFilter, "0")
        let page = try await value.publicDiscovery(.channel(channel, tag: "587"), cursor: nil)
        XCTAssertEqual(page.filters.map(\.id), ["0", "587", "660", "1084", "12", "84", "20", "9", "574", "1107", "578", "13", "14"])
        XCTAssertEqual(page.filters.map(\.group), ["", "场景", "场景", "主题", "主题", "语种", "语种",
            "风格", "风格", "心情", "心情", "年代", "年代"])
        XCTAssertEqual(page.filters.first?.title, "推荐")
        XCTAssertEqual(page.filters.first { $0.id == "587" }?.title, "学习")
        XCTAssertEqual(page.selectedFilter, "587")
        XCTAssertEqual(page.sections.map(\.layout), [.grid])
        XCTAssertEqual(page.kugouCollectionIDs, ["collection_3_123456_1_0"])
        if case .collection(let playlist)? = page.kugouItems.first?.content { XCTAssertEqual(playlist.count, 196) }
        else { XCTFail("Expected collection") }
        let calls = await gateway.calls()
        XCTAssertEqual(calls.filter { $0.path == F.recommend }.map { $0.body["special_recommend"]["categoryid"].int }, [0, 587])
        await finishKugou(value, gateway, paths: [F.tags, F.recommend, F.tags, F.recommend])
    }
    func testTagContinuationAdvancesAndChangedTagStartsAtPageOne() async throws {
        let gateway = KugouGateway { call, _ in
            if call.path == F.tags { return F.tagGroups() }
            guard call.path == F.recommend else { return nil }
            let page = call.body["page"].int, tag = call.body["special_recommend"]["categoryid"].int
            XCTAssertTrue([587, 660].contains(tag))
            return F.playlists(page == 1 ? 30 : 1, start: tag * 1000 + page * 100, more: page == 1)
        }
        let value = try await kugouService(gateway), channel = MusicPublicBrowseChannel.kugou("category-5", .playlists)
        let first = try await value.publicDiscovery(.channel(channel, tag: "587"), cursor: nil)
        XCTAssertEqual(first.sections.first?.title, "场景")
        XCTAssertEqual(first.filters.map(\.id), ["587", "660"])
        let second = try await value.publicDiscovery(.channel(channel, tag: "587"), cursor: XCTUnwrap(first.cursor))
        XCTAssertEqual(second.sections.first?.id, first.sections.first?.id)
        XCTAssertEqual(second.kugouCollectionIDs, ["collection_3_123456_587200_0"])
        XCTAssertNil(second.cursor)
        let changed = try await value.publicDiscovery(.channel(channel, tag: "660"), cursor: nil)
        XCTAssertEqual(changed.selectedFilter, "660")
        XCTAssertEqual(changed.kugouCollectionIDs.first, "collection_3_123456_660100_0")
        let calls = await gateway.calls()
        XCTAssertEqual(calls.filter { $0.path == F.recommend }.map { $0.body["page"].int }, [1, 2, 1])
        let defaults = try await value.publicDiscovery(.channel(channel, tag: ""), cursor: nil)
        XCTAssertEqual(defaults.selectedFilter, "587")
        await finishKugou(value, gateway, paths: Array(repeating: [F.tags, F.recommend], count: 4).flatMap { $0 })
    }
    func testContinuationsRejectAnotherTagTargetAndPlatformWithoutMixedReads() async throws {
        let gateway = KugouGateway { call, _ in F.homeResponse(call) }
        let value = try await kugouService(gateway)
        let first = try await value.publicDiscovery(.channel(.kugou("playlist", .playlists), tag: "587"), cursor: nil)
        let cursor = try XCTUnwrap(first.cursor)
        let mismatches: [MusicPublicBrowseDestination] = [
            .channel(.kugou("playlist", .playlists), tag: "660"),
            .channel(.kugou("category-5", .playlists), tag: "587"), .home,
            .channel(.init(id: "playlist", title: "", kind: .playlists, platform: .netease), tag: "")]
        for destination in mismatches {
            await assertMusicFailure(.staleResult) { _ = try await value.publicDiscovery(destination, cursor: cursor) }
        }
        await assertMusicFailure(.invalidRequest) {
            _ = try await value.publicDiscovery(.channel(.kugou("category-5", .playlists), tag: "84"), cursor: nil)
        }
        await assertMusicFailure(.notFound) {
            _ = try await value.publicDiscovery(.channel(.kugou("category-999", .playlists), tag: ""), cursor: nil)
        }
        await finishKugou(value, gateway, paths: [F.tags, F.recommend, F.tags, F.tags])
    }
    func testFeaturedContinuationRemainsDistinctFromHome() async throws {
        let gateway = KugouGateway { call, _ in
            switch call.path {
            case F.recommend:
                return call.body["page"].int == 1 ? F.playlists(20, start: 100, more: true) : F.playlists(1)
            case F.newSongs: return F.newSongPage(0, total: 0)
            case F.rankList: return F.ranks()
            default: return nil
            }
        }
        let value = try await kugouService(gateway)
        let destination = MusicPublicBrowseDestination.channel(.kugou("feature", .featured), tag: "")
        let first = try await value.publicDiscovery(destination, cursor: nil), cursor = try XCTUnwrap(first.cursor)
        XCTAssertEqual(first.sections.count, 2, "Empty new-song section is omitted on iOS")
        await assertMusicFailure(.staleResult) { _ = try await value.homePage(cursor: cursor, refresh: false) }
        let second = try await value.publicDiscovery(destination, cursor: cursor)
        XCTAssertEqual(second.sections.count, 1)
        XCTAssertEqual(second.kugouCollectionIDs, ["collection_3_123456_1_0"])
        XCTAssertNil(second.cursor)
        await finishKugou(value, gateway, paths: [F.recommend, F.newSongs, F.rankList, F.recommend])
    }
    func testRankCardOpensDedicatedAudioWithRevalidatedIDAndCID() async throws {
        let gateway = KugouGateway { call, _ in
            if call.path == F.rankList { return F.ranks() }
            guard call.path == F.rankAudio else { return nil }
            XCTAssertEqual(call.method, "POST")
            XCTAssertEqual(call.header("kg-tid"), "369")
            XCTAssertEqual(call.body["rank_id"], .n(8888))
            XCTAssertEqual(call.body["rank_cid"], .n(156133))
            XCTAssertEqual(call.body["page"], .n(1))
            XCTAssertEqual(call.body["pagesize"], .n(30))
            var response = F.rankPage(2, total: 2), rows = response["data"]["songlist"].array
            rows[1]["audio_info"]["duration_128"] = .n(185500)
            response["data"]["songlist"] = .array(rows)
            return response
        }
        let value = try await kugouService(gateway)
        let chart = try await value.publicDiscovery(.channel(.kugou("chart", .rankings), tag: ""), cursor: nil)
        let channel = try rank(XCTUnwrap(chart.kugouItems.first))
        let page = try await value.publicDiscovery(.channel(channel, tag: ""), cursor: nil)
        XCTAssertEqual(page.kugouItems.count, 2)
        song(page.kugouItems[0], id: 1, title: "Synthetic rank song 1", duration: 216)
        song(page.kugouItems[1], id: 2, title: "Synthetic rank song 2", duration: 185.5)
        XCTAssertNil(page.cursor)
        await finishKugou(value, gateway, paths: [F.rankList, F.rankList, F.rankAudio])
    }
    func testRankAndNewSongPagingReadOnlyTheNextMemberPage() async throws {
        for endpoint in [F.rankAudio, F.newSongs] {
            let gateway = KugouGateway { call, _ in
                if call.path == F.rankList { return F.ranks() }
                guard call.path == endpoint else { return nil }
                XCTAssertEqual(call.body["pagesize"], .n(30))
                let page = call.body["page"].int
                return endpoint == F.rankAudio
                    ? F.rankPage(page == 1 ? 30 : 1, total: 31, start: page == 1 ? 1 : 31)
                    : F.newSongPage(page == 1 ? 30 : 1, total: 31, start: page == 1 ? 1 : 31)
            }
            let value = try await kugouService(gateway), destination = F.destination(endpoint)
            let first = try await value.publicDiscovery(destination, cursor: nil)
            XCTAssertEqual(first.kugouItems.count, 30)
            let kind = endpoint == F.rankAudio ? "rank" : "new", duration = endpoint == F.rankAudio ? 216.0 : 279.0
            song(first.kugouItems[0], id: 1, title: "Synthetic \(kind) song 1", duration: duration)
            let second = try await value.publicDiscovery(destination, cursor: XCTUnwrap(first.cursor))
            XCTAssertEqual(second.kugouItems.count, 1)
            song(try XCTUnwrap(second.kugouItems.first), id: 31, title: "Synthetic \(kind) song 31", duration: duration)
            XCTAssertEqual(second.sections.first?.id, first.sections.first?.id)
            XCTAssertNil(second.cursor)
            let calls = await gateway.calls()
            XCTAssertEqual(calls.filter { $0.path == endpoint }.map { $0.body["page"].int }, [1, 2])
            await finishKugou(value, gateway, paths: F.pathsFor(endpoint) + F.pathsFor(endpoint))
        }
    }
    func testRankIdentityAndRepublishingRejectMismatches() async throws {
        let gateway = KugouGateway { call, _ in call.path == F.rankList ? F.ranks() : nil }
        let value = try await kugouService(gateway)
        await assertMusicFailure(.targetChanged) {
            _ = try await value.publicDiscovery(.channel(.kugou("rank-8888", .rankings, pageCode: "147778"), tag: ""), cursor: nil)
        }
        await assertMusicFailure(.notFound) {
            _ = try await value.publicDiscovery(.channel(.kugou("rank-9999", .rankings), tag: ""), cursor: nil)
        }
        await finishKugou(value, gateway, paths: [F.rankList, F.rankList])
        let faults: [(String, MusicServiceError, Int)] = [
            ("changed-cid", .staleResult, 1), ("member-cid", .targetChanged, 2), ("conflicting-total", .targetChanged, 2)]
        for (fault, expected, count) in faults {
            let gateway = KugouGateway { call, number in
                if call.path == F.rankList { return F.ranks(cid: fault == "changed-cid" && number > 1 ? 156134 : 156133) }
                guard call.path == F.rankAudio else { return nil }
                let page = call.body["page"].int
                var response = F.rankPage(page == 1 ? 30 : 1, total: 31, start: page == 1 ? 1 : 31)
                if page == 2 {
                    if fault == "conflicting-total" { response["total"] = .n(32) }
                    if fault == "member-cid" {
                        var rows = response["data"]["songlist"].array
                        rows[0]["rank_cid"] = .n(147778); response["data"]["songlist"] = .array(rows)
                    }
                }
                return response
            }
            let value = try await kugouService(gateway)
            let destination = MusicPublicBrowseDestination.channel(.kugou("rank-8888", .rankings), tag: "")
            let first = try await value.publicDiscovery(destination, cursor: nil), cursor = try XCTUnwrap(first.cursor)
            await assertMusicFailure(expected) { _ = try await value.publicDiscovery(destination, cursor: cursor) }
            let calls = await gateway.calls()
            XCTAssertEqual(calls.filter { $0.path == F.rankAudio }.count, count)
            await finishKugou(value, gateway)
        }
    }
    func testExplicitEmptyTerminalPagesStaySuccessful() async throws {
        let gateway = KugouGateway { call, _ in
            switch call.path {
            case F.recommend: return F.playlists(0)
            case F.newSongs: return F.newSongPage(0, total: 0)
            case F.rankList: return F.ranks()
            case F.rankAudio: return F.rankPage(0, total: 0)
            case F.tags: return F.tagGroups()
            default: return nil
            }
        }
        let value = try await kugouService(gateway)
        let home = try await value.homePage(cursor: nil, refresh: true)
        XCTAssertEqual(home.sections.map(\.id), ["kg:home:ranks"])
        XCTAssertEqual(home.kugouItems.count, 1); XCTAssertNil(home.cursor)
        for endpoint in [F.recommend, F.newSongs, F.rankAudio] {
            let page = try await value.publicDiscovery(F.destination(endpoint), cursor: nil)
            XCTAssertTrue(page.kugouItems.isEmpty); XCTAssertNil(page.cursor)
        }
        await finishKugou(value, gateway, paths: [F.recommend, F.newSongs, F.rankList,
            F.tags, F.recommend, F.newSongs, F.rankList, F.rankAudio])
        for endpoint in [F.rankList, F.tags] {
            let gateway = KugouGateway { call, _ in
                guard call.path == endpoint else { return nil }
                return F.json(endpoint == F.tags ? #"{"status":1,"error_code":0,"data":[]}"#
                    : #"{"status":1,"errcode":0,"error":"","data":{"info":[],"total":0}}"#)
            }
            let value = try await kugouService(gateway)
            let page = try await value.publicDiscovery(F.destination(endpoint), cursor: nil)
            XCTAssertEqual(page.kugouItems.map(\.id), endpoint == F.tags ? ["feature", "playlist", "chart", "newsong"] : [])
            XCTAssertNil(page.cursor)
            await finishKugou(value, gateway, paths: [endpoint])
        }
    }
    func testIncompleteMembersAndNonadvancingRecommendationsFailExplicitly() async throws {
        for endpoint in [F.newSongs, F.rankAudio] {
            for fault in ["short-first", "empty-first", "missing-second", "overflow-first"] {
                let gateway = KugouGateway { call, _ in
                    if call.path == F.rankList { return F.ranks() }
                    guard call.path == endpoint else { return nil }
                    let page = call.body["page"].int
                    let count: Int
                    switch fault {
                    case "short-first": count = 29
                    case "empty-first": count = 0
                    case "overflow-first": count = 31
                    default: count = page == 2 ? 0 : 30
                    }
                    return endpoint == F.newSongs ? F.newSongPage(count, total: 32) : F.rankPage(count, total: 32)
                }
                let value = try await kugouService(gateway), destination = F.destination(endpoint)
                var cursor: String?
                if fault == "missing-second" {
                    let first = try await value.publicDiscovery(destination, cursor: nil)
                    cursor = try XCTUnwrap(first.cursor)
                }
                await assertMusicFailure(.upstreamInvalid) { _ = try await value.publicDiscovery(destination, cursor: cursor) }
                let calls = await gateway.calls()
                XCTAssertEqual(calls.filter { $0.path == endpoint }.count, fault == "missing-second" ? 2 : 1)
                await finishKugou(value, gateway)
            }
        }
        let gateway = KugouGateway { call, _ in
            call.path == F.tags ? F.tagGroups() : call.path == F.recommend ? F.playlists(0, more: true) : nil
        }
        let value = try await kugouService(gateway)
        await assertMusicFailure(.limit) {
            _ = try await value.publicDiscovery(.channel(.kugou("playlist", .playlists), tag: "587"), cursor: nil)
        }
        await finishKugou(value, gateway, paths: [F.tags, F.recommend])
    }
    func testMalformedResponsesNeverBecomeEmptySuccess() async throws {
        let cases: [(String, String)] = [
            (F.tags, #"{"status":1,"error_code":0}"#),
            (F.tags, #"{"status":1,"error_code":0,"data":{}}"#),
            (F.recommend, #"{"status":1,"error_code":0,"data":{"has_next":0}}"#),
            (F.recommend, #"{"status":1,"error_code":0,"data":{"has_next":0,"special_list":{}}}"#),
            (F.rankList, #"{"status":1,"errcode":0,"error":"","data":{}}"#),
            (F.rankList, #"{"status":1,"errcode":0,"error":"","data":{"info":null}}"#),
            (F.newSongs, #"{"status":1,"error_code":0,"total":1}"#),
            (F.newSongs, #"{"status":1,"error_code":0,"total":1,"data":null}"#),
            (F.rankAudio, #"{"status":1,"error_code":0,"total":1,"data":{"total":1}}"#),
            (F.rankAudio, #"{"status":1,"error_code":0,"total":1,"data":{"total":1,"songlist":{}}}"#)]
        for (endpoint, body) in cases {
            let gateway = KugouGateway { call, _ in call.path == endpoint ? F.json(body) : F.homeResponse(call) }
            let value = try await kugouService(gateway)
            await assertMusicFailure(.upstreamInvalid) { _ = try await value.publicDiscovery(F.destination(endpoint), cursor: nil) }
            await finishKugou(value, gateway, paths: F.pathsFor(endpoint))
        }
        let gateway = KugouGateway { call, _ in
            guard call.path == F.tags else { return nil }
            var value = F.tagGroups(), groups = value["data"].array, tags = groups[0]["son"].array
            tags[0]["parent_id"] = .string("8"); groups[0]["son"] = .array(tags); value["data"] = .array(groups)
            return value
        }
        let value = try await kugouService(gateway)
        await assertMusicFailure(.upstreamInvalid) { _ = try await value.publicDiscovery(.channels, cursor: nil) }
        await finishKugou(value, gateway, paths: [F.tags])
    }
    func testUpstreamErrorsPreserveMeaningWithoutRetryOrFallback() async throws {
        let errors: [(Int, MusicServiceError)] = [(7000, .upstreamFailed), (20018, .authRequired), (20028, .verificationRequired)]
        for endpoint in [F.tags, F.recommend, F.newSongs, F.rankList, F.rankAudio] {
            for (code, expected) in errors {
                let gateway = KugouGateway { call, _ in call.path == endpoint ? F.failure(code) : F.homeResponse(call) }
                let value = try await kugouService(gateway)
                await assertMusicFailure(expected) { _ = try await value.publicDiscovery(F.destination(endpoint), cursor: nil) }
                await finishKugou(value, gateway, paths: F.pathsFor(endpoint))
            }
        }
        let gateway = KugouGateway { call, _ in
            call.path == F.tags ? F.json(#"{"status":1,"error_code":0,"ssa-code":"x","data":[]}"#) : nil
        }
        let value = try await kugouService(gateway)
        await assertMusicFailure(.verificationRequired) { _ = try await value.publicDiscovery(.channels, cursor: nil) }
        await finishKugou(value, gateway, paths: [F.tags])
    }
    func testAnonymousRequestShapesAndExactBodySignature() async throws {
        let gateway = KugouGateway { call, _ in
            if call.path == F.rankAudio { return F.rankPage(1, total: 1) }
            return F.homeResponse(call)
        }
        let value = try await kugouService(gateway)
        _ = try await value.homePage(cursor: nil, refresh: true)
        _ = try await value.publicDiscovery(.channels, cursor: nil)
        _ = try await value.publicDiscovery(F.destination(F.rankAudio), cursor: nil)
        func md5(_ text: String) -> String { Insecure.MD5.hash(data: Data(text.utf8)).map { String(format: "%02x", $0) }.joined() }
        let calls = await gateway.calls()
        for call in calls {
            XCTAssertNil(call.header("Cookie")); XCTAssertNil(call.header("Authorization"))
            XCTAssertNil(call.query["token"]); XCTAssertNil(call.query["userid"])
            XCTAssertNotNil(call.query["mid"]?.range(of: "^[a-f0-9]{32}$", options: .regularExpression))
            XCTAssertEqual(call.query["dfid"], "-"); XCTAssertEqual(call.query["uuid"], "-")
            XCTAssertEqual(call.query["appid"], "1005"); XCTAssertEqual(call.query["clientver"], "20489")
            XCTAssertEqual(call.query["clienttime"], "1800000000")
            let salt = "OIlwieks28dk2k092lksi2UIkp"
            let params = call.query.keys.filter { $0 != "signature" }.sorted().map { $0 + "=" + call.query[$0]! }.joined()
            let body = call.rawBody.map { String(decoding: $0, as: UTF8.self) } ?? ""
            XCTAssertEqual(call.query["signature"], md5(salt + params + body + salt))
            if call.path == F.rankList {
                XCTAssertEqual(call.method, "GET"); XCTAssertNil(call.rawBody); XCTAssertNil(call.header("Content-Type"))
            } else { XCTAssertEqual(call.method, "POST"); XCTAssertEqual(call.header("Content-Type"), "application/json") }
            if [F.recommend, F.newSongs].contains(call.path) { XCTAssertEqual(call.body["userid"], .n(0)) }
            if call.path == F.recommend {
                XCTAssertEqual(call.body["key"].string, md5("1005" + salt + "20489" + "1800000000"))
                XCTAssertEqual(call.header("x-router"), "specialrec.service.kugou.com")
            }
        }
        await finishKugou(value, gateway, paths: [F.recommend, F.newSongs, F.rankList, F.tags, F.rankList, F.rankAudio])
    }
    func testDiscoveryRoutesAreExactHTTPSMethodQueryAndBodyRecipes() {
        for path in F.paths {
            let method = path == F.rankList ? "GET" : "POST", body: Data? = path == F.rankList ? nil : Data("{}".utf8)
            func request(_ url: String, method: String, body: Data?) -> MusicNativeHTTPRequest {
                .init(url: URL(string: url)!, method: method, body: body)
            }
            let base = "https://gateway.kugou.com" + path
            XCTAssertTrue(MusicNativePublicBrowseRoutes.allows(request(base + "?signature=x", method: method, body: body)))
            let invalid = [
                request(base + "?signature=x", method: method == "GET" ? "POST" : "GET", body: body),
                request(base + "/other?signature=x", method: method, body: body),
                request(base, method: method, body: body),
                request(base + "?signature=x", method: method, body: body == nil ? Data("{}".utf8) : nil),
                request("http://gateway.kugou.com" + path + "?signature=x", method: method, body: body),
                request("https://other.invalid" + path + "?signature=x", method: method, body: body),
                request("https://gateway.kugou.com:8443" + path + "?signature=x", method: method, body: body)]
            for wire in invalid { XCTAssertFalse(MusicNativePublicBrowseRoutes.allows(wire), wire.url.absoluteString) }
        }
    }
}
