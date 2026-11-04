import XCTest
import MeowMusicFeature
@testable import MeowMusicNativeService

private actor BrowseMetadataReads {
    var songIDs: [[String]] = []
    func metadata(_ request: MusicNativeBrowseMetadataRequest) throws -> Data {
        guard case .songIDs(let ids) = request else { throw MusicServiceError.invalidRequest }
        songIDs.append(ids)
        return MJ.obj([("code", .n(200)), ("songs", .array(ids.map { MusicNativePublicBrowseFixture.song($0) }))]).bytes
    }
}

final class MusicNativePublicBrowseDiscoveryTests: XCTestCase {
    func testHomeSummaryOnlyFillsMissingTitleWithoutLoadingOptionalCoversOrMembers() async throws {
        let resources = try MJ.parse(Data(#"""
        [
          {"resourceType":"playlist","id":41,"title":"歌曲预览","subTitle":"私人雷达","singleLineTitle":"官方副标题"},
          {"resourceType":"playlist","id":42,"coverImg":"https://p1.music.126.net/original.jpg"},
          {"resourceType":"playlist","id":43,"title":"封面完整","coverImg":"https://p1.music.126.net/complete.jpg"},
          {"resourceType":"playlist","id":44,"title":"保留标题"}
        ]
        """#.utf8), mode: .upstream)
        let metadata = try MJ.parse(Data(#"""
        [
          {"id":42,"name":"补齐歌单名","coverImgUrl":"https://p1.music.126.net/replacement.jpg"}
        ]
        """#.utf8), mode: .upstream)
        let block = MJ.obj([("dslData", .obj([("dslShowTitle", .bool(false)),
            ("blockResource", .obj([("resources", resources)]))]))])
        let http = NativeMusicHTTPFixture([
            .obj([("code", .n(200)), ("data", .obj([("blocks", .array([block]))]))]),
            .obj([("code", .n(200)), ("data", metadata)])
        ])
        let service = try await NativeMusicFixture.service(http: http)
        let page = try await service.publicDiscovery(.home, cursor: nil)
        XCTAssertEqual(page.sections.map(\.layout), [.featureRail])
        let items = try XCTUnwrap(page.sections.first).items
        XCTAssertEqual(items.map(\.title), ["私人雷达", "补齐歌单名", "封面完整", "保留标题"])
        XCTAssertEqual(items.map { $0.artwork?.absoluteString },
            [nil, "https://p1.music.126.net/original.jpg", "https://p1.music.126.net/complete.jpg", nil])
        for item in items {
            if case .collection(let value) = item.content {
                XCTAssertEqual(value.artwork, item.artwork)
                XCTAssertEqual(value.title, item.title)
            } else { XCTFail("metadata must not change the native collection action") }
        }
        let calls = await http.calls()
        XCTAssertEqual(calls.count, 2)
        let request = try MusicNativePublicBrowseFixture.decryptEAPI(calls[1])
        XCTAssertEqual(request.0, "/api/tag/tab/playlists")
        XCTAssertEqual(request.1["ids"].string, "42")
    }
    func testReturnedSongDataOnlyReadsMissingVisibleIDsAndLeavesHiddenTabUntouched() async throws {
        let ids = (1...30).map(String.init)
        let items = try ids.map { id -> MusicNativePublicBrowseItem in
            var raw = MusicNativePublicBrowseFixture.song(id)
            if id == "2" { raw.remove("name") }
            if id == "9" { raw["ar"] = .array([.obj([("id", .n(900))])]) }
            return try XCTUnwrap(MusicNativePublicBrowseParser.resource(
                .obj([("resourceType", .string("song")), ("songData", raw)]), index: id))
        }
        let hidden = MusicNativePublicBrowseItem(id: "hidden", title: "", content: .songID("31"))
        let input = MusicNativePublicBrowseDiscovery(sections: [.init(id: "songs", title: "", items: items,
            tabs: [.init(id: "shown", title: "", items: items), .init(id: "hidden", title: "", items: [hidden])],
            selectedTab: "shown")])
        let reads = BrowseMetadataReads()
        let port = MusicNativePlatforms(transport: NativeMusicNoNetwork(), qq: NativeMusicQQFixture(), clock: { NativeMusicFixture.date })
        let enriched = try await port.browseEnrich(input, metadata: { try await reads.metadata($0) })
        let requested = await reads.songIDs; XCTAssertEqual(requested, [["2", "9"]])
        XCTAssertEqual(enriched.sections[0].items.count, 30)
        let retained = try enriched.sections[0].items.map { item -> String in
            guard case .song(let value) = item.content else { throw MusicServiceError.invalidResponse }
            return try MJ.parse(value.data)["songmid"].string
        }
        XCTAssertEqual(retained, ids)
        guard case .songID("31") = enriched.sections[0].tabs[1].items[0].content else {
            return XCTFail("Hidden tab must retain its original identity without hydration")
        }
    }

    func testHomeAuthorizationFailureDoesNotBecomeGuestPlaceholderContent() async throws {
        let http = NativeMusicHTTPFixture([.obj([("code", .n(301))])])
        let service = try await NativeMusicFixture.service(http: http)
        do { _ = try await service.publicDiscovery(.home, cursor: nil); XCTFail("authentication required") }
        catch { XCTAssertEqual(error as? MusicServiceError, .authRequired) }
        let calls = await http.calls()
        XCTAssertEqual(calls.count, 1)
    }

    func testHomeParsingRetainsStrictDSLandTraversalBudgets() throws {
        let malformed = MJ.obj([("blocks", .array([.obj([("dslData", .string("{"))])]))])
        XCTAssertThrowsError(try MusicNativePublicBrowseParser.blocks(malformed, previous: .obj()))
        var nested = MJ.obj()
        for _ in 0..<30 { nested = .obj([("children", .array([nested]))]) }
        var budget = 6000
        XCTAssertThrowsError(try MusicNativePublicBrowseParser.leaves(nested, budget: &budget)) {
            XCTAssertEqual($0 as? MusicServiceError, .limit)
        }
        budget = 1
        XCTAssertThrowsError(try MusicNativePublicBrowseParser.leaves(.array([.obj()]), budget: &budget)) {
            XCTAssertEqual($0 as? MusicServiceError, .limit)
        }
    }

    func testHomeKeepsSeparateDSLRootsHeadingsHiddenTitleAndLargeMetadata() throws {
        let dsl = try MJ.parse(Data(#"{"first":{"header":{"title":"原版栏目一"},"items":[{"resourceType":"playlist","id":41,"name":"歌单","coverUrl":"https://p1.music.126.net/a.jpg","metadata":18446744073709551617}]},"second":{"header":{"title":"原版栏目二"},"items":[{"resourceType":"song","id":42,"name":"歌曲"}]}}"#.utf8), mode: .upstream)
        let decoded = try MusicNativePublicBrowseParser.object(.string(dsl.json))
        XCTAssertEqual(decoded, dsl)
        let block = MJ.obj([("positionCode", .string("root")), ("dslData", decoded)])
        let page = try MusicNativePublicBrowseParser.blocks(.obj([("blocks", .array([block]))]), previous: .obj())
        XCTAssertEqual(page.sections.map(\.title), ["原版栏目一", "原版栏目二"])
        XCTAssertEqual(page.sections.map(\.layout), [.playlistRail, .songColumns])
        XCTAssertEqual(page.sections.flatMap(\.items).map(\.title), ["歌单", "歌曲"])
        var hidden = block; hidden["hideTitle"] = .bool(true)
        let withoutHeading = try MusicNativePublicBrowseParser.blocks(.obj([("blocks", .array([hidden]))]), previous: .obj())
        XCTAssertEqual(withoutHeading.sections.map(\.title), ["", ""])
    }

    func testNativeHomeConsumesMixedPlaylistAlbumAndSongAndPreservesContinuation() async throws {
        let list = MJ.obj([("resourceType", .string("playlist")), ("id", .n(4)), ("name", .string("Playlist")),
                          ("coverUrl", .string("https://p1.music.126.net/synthetic.jpg"))])
        let album = MJ.obj([("resourceType", .string("album")), ("id", .n(5)), ("name", .string("Album"))])
        let song = MJ.obj([("resourceType", .string("song")), ("id", .n(6))])
        let block = MJ.obj([("positionCode", .string("position-1")), ("dslData", .obj([("items", .array([list, album, song]))]))])
        let http = NativeMusicHTTPFixture([.obj([("code", .n(200)), ("data", .obj([
            ("blocks", .array([block])), ("hasMore", .bool(true)), ("cursor", .n(1)), ("blockCodeOrderList", .string("[]"))]))]),
            .obj([("code", .n(200)), ("songs", .array([MusicNativePublicBrowseFixture.song("6")]))])])
        let service = try await NativeMusicFixture.service(http: http)
        let page = try await service.publicDiscovery(.home, cursor: nil)
        XCTAssertNotNil(page.cursor); XCTAssertEqual(page.sections.flatMap(\.items).count, 3)
        if case .collection(let value) = page.sections[0].items[1].content { XCTAssertEqual(value.kind, .album) }
        else { XCTFail("album must not become a playlist") }
        XCTAssertEqual(page.sections.map(\.layout), [.playlistRail, .songColumns])
        if case .track(let value) = page.sections[1].items[0].content { XCTAssertEqual(value.sources[0].nativeId, "6") }
        else { XCTFail("song must be hydrated") }
        let calls = await http.calls(), decoded = try MusicNativePublicBrowseFixture.decryptEAPI(calls[0])
        XCTAssertEqual(decoded.0, "/api/link/page/rcmd/resource/show")
        XCTAssertEqual(decoded.1["pageCode"].string, "HOME_RECOMMEND_PAGE")
    }
    func testChannelDirectoryParsesOnlyRecognizedNativeActions() throws {
        let rows = [
            MJ.obj([("code", .string("classical")), ("name", .string("Classical")),
                    ("url", .string("orpheus://rnpage?component=cube-renderer-rn&page=classical_1"))]),
            .obj([("code", .string("unknown")), ("name", .string("Unknown")), ("url", .string("https://untrusted.invalid/action"))])
        ]
        let channels = try MusicNativePublicBrowseParser.channels(.obj([("data", .obj([("channels", .array(rows))]))]))
        XCTAssertEqual(channels[0].kind, .cube); XCTAssertEqual(channels[0].pageCode, "classical_1")
        XCTAssertEqual(channels[1].kind, .unsupported)
        XCTAssertNil(try MusicNativePublicBrowseParser.action(.string("javascript:alert(1)")))
        XCTAssertNil(try MusicNativePublicBrowseParser.action(.string("https://music.163.com/#/playlist?id=41")))
        XCTAssertNil(try MusicNativePublicBrowseParser.resource(.obj([("resourceType", .string("podcast")),
            ("resourceExtInfo", .obj([("songData", MusicNativePublicBrowseFixture.song())]))]), index: "1"))
    }
    func testCubeOnlyWalksSelectedTabAndRejectsDepthBomb() throws {
        func group(_ id: String) -> MJ {
            .obj([("id", .string(id)), ("componentName", .string("TabItem")), ("props", .obj([("title", .string(id))])),
                ("children", .array([.obj([("componentName", .string("Grid")), ("children", .array([
                    .obj([("componentName", .string("Playlist")),
                        ("props", .obj([("id", .n(id == "a" ? 1 : 2)), ("name", .string(id))]))])
                ]))])]))])
        }
        let root = MJ.obj([("componentName", .string("Root")), ("children", .array([group("a"), group("b")]))])
        var parser = MusicNativePublicBrowseCube()
        try parser.collect(root); parser.selected = "b"; try parser.walk(root)
        XCTAssertEqual(parser.page.sections.flatMap(\.items).count, 1)
        XCTAssertEqual(parser.page.sections[0].items[0].id, "wy:playlist:2")
        var nested = MJ.obj()
        for _ in 0..<30 { nested = .obj([("children", .array([nested]))]) }
        var bounded = MusicNativePublicBrowseCube()
        XCTAssertThrowsError(try bounded.collect(nested))
    }
    func testDailyRequiresExistingAccountPortAndNeverFallsBackToAnonymous() async throws {
        let http = NativeMusicHTTPFixture([])
        let service = try await NativeMusicFixture.service(http: http)
        do { _ = try await service.publicDiscovery(.daily(.netease), cursor: nil); XCTFail("missing account") }
        catch { XCTAssertEqual(error as? MusicServiceError, .authRequired) }
        let calls = await http.calls(); XCTAssertTrue(calls.isEmpty)
    }
    func testEmptyMorePageIsRejectedByNativeConsumer() async throws {
        let root = MJ.obj([("data", .obj([("blocks", .array([])), ("hasMore", .bool(true)), ("cursor", .n(0))]))])
        XCTAssertThrowsError(try MusicNativePublicBrowseParser.blocks(root, previous: .obj([("cursor", .string("0"))])))
        let cases: [(blocks: [MJ], error: MusicServiceError)] = [([], .limit), ([.null], .upstreamInvalid)]
        for item in cases {
            let response = MJ.obj([("code", .n(200)), ("data", .obj([
                ("blocks", .array(item.blocks)), ("hasMore", .bool(true)), ("cursor", .string("1")),
            ]))])
            let service = try await NativeMusicFixture.service(http: NativeMusicHTTPFixture([response]))
            do { _ = try await service.homePage(cursor: nil, refresh: true); XCTFail("Nonterminal page without real progress accepted") }
            catch { XCTAssertEqual(error as? MusicServiceError, item.error) }
            await service.close()
        }
    }
    func testCubeProductionConsumerKeepsNativeRulePointAndAlgorithmIDs() async throws {
        let songList = MJ.obj([("ruleId", .n(3)), ("pointId", .array([.obj([("id", .n(2))])])), ("title", .string("Feed"))])
        let component = MJ.obj([("componentName", .string("Root")), ("props", .obj([("songList", songList)]))])
        let protocolData = MJ.obj([("pages", .array([component]))])
        let cube = MJ.obj([("code", .n(200)), ("data", .obj([("pageProtocol", protocolData)]))])
        let tags = MJ.obj([("code", .n(200)), ("data", .obj([("algTagInfos", .array([
            .obj([("algTagId", .n(4)), ("algTagName", .string("Synthetic tag"))])
        ]))]))])
        let feed = MJ.obj([("code", .n(200)), ("songs", .array([MusicNativePublicBrowseFixture.song("6")])), ("hasMore", .bool(true))])
        let http = NativeMusicHTTPFixture([cube, tags, feed]), service = try await NativeMusicFixture.service(http: http)
        let channel = MusicPublicBrowseChannel(id: "classical", title: "Classical", kind: .cube, pageCode: "classical_1")
        let result = try await service.publicDiscovery(.channel(channel, tag: "|4"), cursor: nil)
        XCTAssertEqual(result.selectedFilter, "|"); XCTAssertTrue(result.filters.isEmpty)
        XCTAssertEqual(result.sections[0].selectedFilter, "|4")
        XCTAssertEqual(result.sections[0].filters.map(\.id), ["|", "|4"])
        XCTAssertNotNil(result.cursor)
        XCTAssertEqual(result.sections[0].items.count, 1)
        let calls = await http.calls(), input = try MusicNativePublicBrowseFixture.decryptEAPI(calls[2])
        XCTAssertEqual(calls[0].url.path, "/weapi/cube/render/page/protocol")
        XCTAssertEqual(input.0, "/api/tag/tab/alg/recommends")
        XCTAssertEqual(input.1["ruleId"].string, "3"); XCTAssertEqual(input.1["algTagIds"].string, "4")
        XCTAssertEqual(input.1["extMap"].string, #"{"pointIds":"2"}"#)
    }
    private var feedChannel: MusicPublicBrowseChannel {
        .init(id: "synthetic", title: "Synthetic", kind: .cube, pageCode: "synthetic_page")
    }
    private var feedProtocol: MJ {
        .obj([("code", .n(200)), ("data", .obj([("pageProtocol", .obj([("pages", .array([
            .obj([("componentName", .string("Root")), ("props", .obj([("songList", .obj([
                ("ruleId", .n(3)), ("title", .string("Feed"))
            ]))]))])
        ]))]))]))])
    }
    private func feedResponse(_ id: String, more: Bool = false) -> MJ {
        .obj([("code", .n(200)), ("songs", .array([MusicNativePublicBrowseFixture.song(id)])), ("hasMore", .bool(more))])
    }
    func testRecommendationFeedContinuesBeyondFormerOffsetLimitAndStopsOnEmptyPage() async throws {
        let tags = MJ.obj([("code", .n(200)), ("data", .obj([("algTagInfos", .null)]))])
        let pages = [feedResponse("6", more: true), feedResponse("7", more: true), feedResponse("8", more: true),
            .obj([("code", .n(200)), ("songs", .array([])), ("hasMore", .bool(true))])]
        let http = NativeMusicHTTPFixture(pages.flatMap { [feedProtocol, tags, $0] })
        let port = MusicNativePlatforms(transport: http, qq: NativeMusicQQFixture(), clock: { NativeMusicFixture.date })
        let offsets = [5970, 6000, 6030, 6060]
        var remote = MJ.obj([("offset", .n(offsets[0]))])
        for (index, offset) in offsets.enumerated() {
            let page = try await port.browseCube(feedChannel, tag: "", remote: remote)
            let section = try XCTUnwrap(page.sections.first)
            XCTAssertNil(section.error)
            XCTAssertEqual(section.items.map(\.id), index < pages.count - 1 ? [String(index + 6)] : [])
            if index < pages.count - 1 {
                remote = try XCTUnwrap(page.next)
                XCTAssertEqual(try remote["offset"].integer(), offset + 30)
                XCTAssertFalse(remote["snapshot"].string.isEmpty)
            } else { XCTAssertNil(page.next) }
        }
        let calls = await http.calls()
        XCTAssertEqual(calls.count, offsets.count * 3)
        let requests = calls.filter { $0.url.path == "/eapi/tag/tab/alg/recommends" }
        let requestedOffsets = try requests.map { try MusicNativePublicBrowseFixture.decryptEAPI($0).1["offset"].integer() }
        XCTAssertEqual(requestedOffsets, offsets)
    }
    func testRecommendationFeedStillRejectsUnrepresentableContinuationOffset() async throws {
        let tags = MJ.obj([("code", .n(200)), ("data", .obj([("algTagInfos", .null)]))])
        let http = NativeMusicHTTPFixture([feedProtocol, tags, feedResponse("6", more: true),
                                          feedProtocol, tags, feedResponse("7", more: true)])
        let port = MusicNativePlatforms(transport: http, qq: NativeMusicQQFixture(), clock: { NativeMusicFixture.date })
        let lastOffset = 9_007_199_254_740_990
        let first = try await port.browseCube(feedChannel, tag: "", remote: .obj([("offset", .n(lastOffset - 30))]))
        let remote = try XCTUnwrap(first.next)
        XCTAssertEqual(try remote["offset"].integer(), lastOffset)
        do {
            _ = try await port.browseCube(feedChannel, tag: "", remote: remote)
            XCTFail("Unrepresentable continuation offset")
        } catch { XCTAssertEqual(error as? MusicServiceError, .limit) }
        let calls = await http.calls()
        XCTAssertEqual(calls.count, 6)
        let requests = calls.filter { $0.url.path == "/eapi/tag/tab/alg/recommends" }
        let requestedOffsets = try requests.map { try MusicNativePublicBrowseFixture.decryptEAPI($0).1["offset"].integer() }
        XCTAssertEqual(requestedOffsets, [lastOffset - 30, lastOffset])
    }
    func testNullOrFailedOptionalAlgorithmTagsStillFetchActualSongs() async throws {
        for response in [MJ.obj([("code", .n(200)), ("data", .obj([("algTagInfos", .null)]))]),
                         .obj([("code", .n(500))])] {
            let http = NativeMusicHTTPFixture([feedProtocol, response, feedResponse("6")])
            let service = try await NativeMusicFixture.service(http: http)
            let page = try await service.publicDiscovery(.channel(feedChannel, tag: ""), cursor: nil)
            XCTAssertNil(page.sections[0].error); XCTAssertEqual(page.sections[0].items.count, 1)
            let item = page.sections[0].items[0]
            if case .track(let track) = item.content {
                XCTAssertFalse(item.subtitle.isEmpty); XCTAssertEqual(item.artwork, track.artwork)
                XCTAssertEqual(item.subtitle, [track.artist, track.album].filter { !$0.isEmpty }.joined(separator: " · "))
            } else { XCTFail("feed song") }
            XCTAssertTrue(page.filters.isEmpty)
            XCTAssertEqual(page.sections[0].filters.map(\.title), ["全部歌曲"])
            let calls = await http.calls()
            XCTAssertEqual(calls.count, 3)
            XCTAssertEqual(try MusicNativePublicBrowseFixture.decryptEAPI(calls[2]).0, "/api/tag/tab/alg/recommends")
            await service.close()
        }
    }
    func testAlgorithmFilterKeepsGroupsSeparateFromChannelTabsAndPaginationIdentityStable() async throws {
        let tags = MJ.obj([("code", .n(200)), ("data", .obj([("algTagInfos", .array([
            .obj([("algTagName", .string("风格")), ("children", .array([
                .obj([("algTagId", .n(4)), ("algTagName", .string("Synthetic"))])
            ]))])
        ]))]))])
        let http = NativeMusicHTTPFixture([feedProtocol, tags, feedResponse("6", more: true),
                                          feedProtocol, tags, feedResponse("7")])
        let service = try await NativeMusicFixture.service(http: http)
        let first = try await service.publicDiscovery(.channel(feedChannel, tag: "|4"), cursor: nil)
        let next = try await service.publicDiscovery(.channel(feedChannel, tag: "|4"), cursor: XCTUnwrap(first.cursor))
        XCTAssertTrue(first.filters.isEmpty); XCTAssertEqual(first.selectedFilter, "|")
        XCTAssertEqual(first.sections[0].filters.map(\.group), ["", "风格"])
        XCTAssertEqual(first.sections[0].selectedFilter, "|4")
        XCTAssertEqual(first.sections[0].id, next.sections[0].id)
        XCTAssertNotEqual(first.sections[0].items[0].id, next.sections[0].items[0].id)
        XCTAssertNil(next.cursor)
        let calls = await http.calls(), params = try MusicNativePublicBrowseFixture.decryptEAPI(calls[5]).1
        XCTAssertEqual(try params["offset"].integer(6000), 30)
        XCTAssertEqual(params["algTagIds"].string, "4")
        let count = calls.count
        do {
            _ = try await service.publicDiscovery(.channel(feedChannel, tag: ""), cursor: first.cursor)
            XCTFail("cursor must remain bound to the exact algorithm")
        } catch { XCTAssertEqual(error as? MusicServiceError, .staleResult) }
        let after = await http.calls(); XCTAssertEqual(after.count, count)
        await service.close()
    }
    func testInitialFeedFailureIsSectionLocalAndDoesNotTurnIntoEmptySuccess() async throws {
        let http = NativeMusicHTTPFixture([feedProtocol,
            .obj([("code", .n(200)), ("data", .obj([("algTagInfos", .null)]))]), .obj([("code", .n(500))])])
        let service = try await NativeMusicFixture.service(http: http)
        let page = try await service.publicDiscovery(.channel(feedChannel, tag: ""), cursor: nil)
        XCTAssertEqual(page.sections.count, 1); XCTAssertTrue(page.sections[0].items.isEmpty)
        XCTAssertEqual(page.sections[0].error, .upstreamFailed); XCTAssertNil(page.cursor)
        XCTAssertEqual(page.sections[0].filters.map(\.id), ["|"])
        await service.close()
    }
    func testOptionalTagAuthenticationFailureIsNotSwallowedOrReplacedByGuestFeed() async throws {
        let http = NativeMusicHTTPFixture([feedProtocol, .obj([("code", .n(301))]), feedResponse("6")])
        let service = try await NativeMusicFixture.service(http: http)
        do { _ = try await service.publicDiscovery(.channel(feedChannel, tag: ""), cursor: nil); XCTFail("auth") }
        catch { XCTAssertEqual(error as? MusicServiceError, .authRequired) }
        let calls = await http.calls(); XCTAssertEqual(calls.count, 2)
        await service.close()
    }
}
