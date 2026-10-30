import XCTest
import MeowMusicFeature
@testable import MeowMusicNativeService

final class MusicNativePublicBrowseCubeParityTests: XCTestCase {
    private let channel = MusicPublicBrowseChannel(id: "synthetic", title: "Synthetic", kind: .cube, pageCode: "synthetic_page")
    private func node(_ text: String) throws -> MJ { try MJ.parse(Data(text.utf8), mode: .upstream) }
    private func response(_ page: MJ) -> MJ {
        .obj([("code", .n(200)), ("data", .obj([("pageProtocol", .obj([("pages", .array([page]))]))]))])
    }
    func testRecommendTabsHydrateOnlySelectedChoiceAndKeepFMNumericSelectorAndCoverLabel() async throws {
        let root = try node(#"""
        {"componentName":"Root","children":[{"id":"recommend","props":{"tab":"electronic","title":"官方栏目","recommendModule":[
          {"title":"一","modules":[{"type":"song","id":11},{"type":"playlist","id":41}]},
          {"title":"二","isDefaultTab":true,"modules":[{"type":"song","id":12},
            {"type":"fm","tabTagId":17001,"fmLogoTextContent":"规则封面"},
            {"type":"fm","tabTagId":"","fmLogoTextContent":"继承封面"}]}
        ]}}]}
        """#)
        let http = NativeMusicHTTPFixture([response(root),
            .obj([("code", .n(200)), ("songs", .array([MusicNativePublicBrowseFixture.song("12")]))]),
            .obj([("code", .n(200)), ("songs", .array([MusicNativePublicBrowseFixture.song("11")]))]),
            .obj([("code", .n(200)), ("data", .array([.obj([("id", .n(41)), ("name", .string("水合歌单")),
                ("coverImgUrl", .string("https://p1.music.126.net/fixture.jpg"))])]))])
        ])
        let service = try await NativeMusicFixture.service(http: http)
        let page = try await service.publicDiscovery(.channel(channel, tag: ""), cursor: nil)
        let section = try XCTUnwrap(page.sections.first)
        XCTAssertEqual(page.sections.count, 1); XCTAssertTrue(page.filters.isEmpty)
        XCTAssertEqual(section.title, "官方栏目"); XCTAssertEqual(section.tabs.map(\.title), ["一", "二"])
        XCTAssertEqual(section.selectedTab, "recommend:group:1")
        XCTAssertEqual(section.items.map(\.id), section.tabs[1].items.map(\.id))
        XCTAssertTrue(section.tabs[0].items.isEmpty)
        XCTAssertEqual(section.tabs[1].items.map(\.coverLabel), ["", "规则封面", "继承封面"])
        if case .action(let action) = section.items[1].content {
            XCTAssertEqual(action, .ruleFM(ruleID: "", tabTagID: "17001", sourceName: "规则封面"))
        } else { XCTFail("numeric FM tab must survive component inheritance") }
        if case .action(let action) = section.items[2].content {
            XCTAssertEqual(action, .ruleFM(ruleID: "", tabTagID: "electronic", sourceName: "继承封面"))
        } else { XCTFail("empty FM tab must inherit") }
        if case .track(let track) = section.tabs[1].items[0].content { XCTAssertEqual(track.sources[0].nativeId, "12") }
        else { XCTFail("selected song must be available") }
        let initial = await http.calls(); XCTAssertEqual(initial.count, 2)
        let load = try XCTUnwrap(section.tabs[0].loadItems)
        let selected = try await load()
        XCTAssertEqual(selected.map(\.title), ["Synthetic song", "水合歌单"])
        if case .track(let track) = selected[0].content { XCTAssertEqual(track.sources[0].nativeId, "11") }
        else { XCTFail("explicit choice must complete its missing display fields") }
        let calls = await http.calls(); XCTAssertEqual(calls.count, 4)
        await service.close()
    }
    func testGridAndScrollSplitDirectSongsFromCollectionsWithoutFlatteningLeaves() throws {
        let root = try node(#"""
        {"children":[{"componentName":"Title","props":{"mainTitle":"混排"}},
          {"componentName":"Grid","children":[
            {"componentName":"Playlist","props":{"id":41,"name":"歌单"}},
            {"componentName":"Song","props":{"id":11}},
            {"componentName":"Album","props":{"id":42,"name":"专辑"}}
          ]},
          {"componentName":"Scroll","children":[{"componentName":"Playlist","props":{"id":43,"name":"横排"}}]}
        ]}
        """#)
        var parser = MusicNativePublicBrowseCube(); try parser.collect(root); try parser.walk(root)
        XCTAssertEqual(parser.page.sections.map(\.layout), [.songColumns, .grid, .playlistRail])
        XCTAssertEqual(parser.page.sections.map(\.title), ["混排", "混排", "混排"])
        XCTAssertEqual(parser.page.sections.map { $0.items.count }, [1, 2, 1])
    }
    func testFiniteFeedKeepsHeaderSeparateAndNeverInventsNextPageOrAlgorithmRequest() async throws {
        let root = try node(#"""
        {"componentName":"InfiniteScrollList","props":{"listTitleStr":""},"children":[
          {"componentName":"Grid","children":[{"componentName":"Song","props":{"id":11}},
                                             {"componentName":"Song","props":{"id":12}}]},
          {"props":{"albumData":{"id":43,"name":"专辑","subTitle":"副标题"}}}
        ]}
        """#)
        let http = NativeMusicHTTPFixture([response(root),
            .obj([("code", .n(200)), ("songs", .array([MusicNativePublicBrowseFixture.song("11"), MusicNativePublicBrowseFixture.song("12")]))])
        ])
        let service = try await NativeMusicFixture.service(http: http)
        let page = try await service.publicDiscovery(.channel(channel, tag: ""), cursor: nil)
        XCTAssertNil(page.cursor); XCTAssertNil(page.notice)
        XCTAssertEqual(page.sections.count, 2); XCTAssertEqual(page.sections[0].items[0].subtitle, "副标题")
        let feed = page.sections[1]
        XCTAssertEqual(feed.layout, .list); XCTAssertEqual(feed.title, "")
        XCTAssertEqual(feed.items.count, 2); XCTAssertEqual(feed.filters.map(\.title), ["全部歌曲"])
        let calls = await http.calls(); XCTAssertEqual(calls.count, 2)
        XCTAssertFalse(calls.contains { $0.url.path.contains("/tag/tab/") })
        await service.close()
    }
    func testOldCarouselDefaultUsesOnlyFirstParentsSiblings() async throws {
        let root = try node(#"""
        {"children":[
          {"componentName":"MusicGenreSongList","children":[
            {"componentName":"MusicGenreSongListItem","id":"a","props":{"title":"A"}},
            {"componentName":"MusicGenreSongListItem","id":"b","props":{"title":"B"}},
            {"componentName":"MusicGenreSongListItem","id":"c","props":{"title":"C"}}]},
          {"componentName":"Tabs","children":[
            {"componentName":"TabItem","id":"d","props":{"title":"D"}},
            {"componentName":"TabItem","id":"e","props":{"title":"E"}},
            {"componentName":"TabItem","id":"f","props":{"title":"F"}}]}
        ]}
        """#)
        let http = NativeMusicHTTPFixture([response(root)]), service = try await NativeMusicFixture.service(http: http)
        let page = try await service.publicDiscovery(.channel(channel, tag: ""), cursor: nil)
        XCTAssertEqual(page.selectedFilter, "b|"); XCTAssertEqual(page.filters.count, 6)
        await service.close()
    }
    func testAllTabItemsShareGlobalBudgetAndDuplicateGroupIDsAreRejected() async throws {
        let rows = (1...301).map { MusicNativePublicBrowseItem(id: "\($0)", title: "", content: .unavailable) }
        let section = MusicNativePublicBrowseSection(id: "s", title: "", items: [],
            tabs: [.init(id: "a", title: "", items: rows), .init(id: "b", title: "", items: rows)], selectedTab: "a")
        let port = MusicNativePlatforms(transport: NativeMusicNoNetwork(), qq: NativeMusicQQFixture(), clock: { NativeMusicFixture.date })
        do { _ = try await port.browseEnrich(.init(sections: [section])); XCTFail("all tab budget") }
        catch { XCTAssertEqual(error as? MusicServiceError, .limit) }
        let duplicate = try node(#"{"children":[{"componentName":"TabItem","id":"same"},{"componentName":"TabItem","id":"same"}]}"#)
        var parser = MusicNativePublicBrowseCube()
        XCTAssertThrowsError(try parser.collect(duplicate))
    }
    func testHomeSnapshotRetainsDeclaredCoverLabelButNeverAnFMPreviewBatch() throws {
        let item = MusicNativePublicBrowseItem(id: "fm", title: "FM", content: .action(.fm(mode: .default, submode: "", entry: nil)),
                                              coverLabel: "FM")
        let page = MusicNativePublicBrowseDiscovery(sections: [.init(id: "s", title: "", items: [item])])
        let data = try MusicNativeHomeSnapshot.encode(page, scope: "synthetic")
        let restored = try XCTUnwrap(MusicNativeHomeSnapshot.decode(data, scope: "synthetic"))
        XCTAssertEqual(restored.sections[0].items[0].coverLabel, "FM")
        XCTAssertTrue(restored.sections[0].items[0].previewSongs.isEmpty)
        XCTAssertFalse(String(decoding: data, as: UTF8.self).contains("authorization"))
    }
}
