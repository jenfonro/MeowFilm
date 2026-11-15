import XCTest
import MeowMusicFeature
import MeowMusicTestSupport
import MeowRuntime
@testable import MeowMusicNativeService

enum NativeDiscoveryFixture {
    static func collection(_ id: String, title: String = "Collection", cover: String = "", album: Bool = false) -> RuntimeValue {
        .object(["id": .string(id), "name": .string(title), "cover": .string(cover),
            "action": .object(["type": .string(album ? "album" : "playlist")])])
    }
    static func section(_ id: String, items: [RuntimeValue], type: String = "playlists", title: String = "", layout: String = "") -> RuntimeValue {
        .object(["id": .string(id), "title": .string(title), "type": .string(type), "layout": .string(layout), "items": .array(items)])
    }
    static func page(_ sections: [RuntimeValue], next: RuntimeValue = .null) -> RuntimeValue {
        .object(["sections": .array(sections), "continuation": next])
    }
    static func song(_ id: String, cover: String = "", title: String = "Synthetic song") throws -> RuntimeValue {
        try MusicPlatformFixture.song(MusicNativePublicBrowseFixture.track(id, title: title, artwork: cover))
    }
}
private typealias D = NativeDiscoveryFixture
final class MusicNativePublicBrowseDiscoveryTests: XCTestCase {
    private let channel = MusicPublicBrowseChannel(id: "synthetic", title: "Synthetic", kind: .cube, pageCode: "synthetic_page")
    func testHomeConsumesAlreadyCompletedSummariesWithoutReadingMembers() async throws {
        let cover = "https://p1.music.126.net/radar.jpg?param=800y800&quality=90"
        let peer = NativeMusicResultFixture([D.page([D.section("entries", items: [D.collection("41", title: "私人雷达", cover: cover), D.collection("42", title: "补齐歌单名")], type: "entries")])])
        let document = NativeMusicMemoryDocument(), service = try await NativeMusicFixture.service(document, platform: peer), page = try await service.publicDiscovery(.home, cursor: nil)
        XCTAssertEqual(page.sections[0].items.map(\.title), ["私人雷达", "补齐歌单名"]); XCTAssertEqual(page.sections[0].items[0].artwork?.absoluteString, cover)
        guard case .collection(let collection) = page.sections[0].items[0].content else { return XCTFail("collection required") }
        XCTAssertEqual(collection.artwork, page.sections[0].items[0].artwork); XCTAssertEqual(document.snapshot().2, 0)
        let calls = await peer.calls(); XCTAssertEqual(calls.map(\.operation), ["home"]); await service.close()
    }
    func testHomeConsumesRuntimeDisplayAndAvailabilityWithoutInferringActionAccountPolicy() async throws {
        let hidden: RuntimeValue = .object(["display": .bool(false), "action": .object([
            "type": .string("similar"), "sourceIds": .string("unused")])])
        let visible: RuntimeValue = .object(["name": .string("运行器入口"), "display": .bool(true),
            "requiresAccount": .bool(true), "available": .bool(true),
            "action": .object(["type": .string("daily"), "branch": .string("style")])])
        let unavailable: RuntimeValue = .object(["name": .string("不可用入口"), "display": .bool(true),
            "available": .bool(false), "action": .object(["type": .string("similar")])])
        let peer = NativeMusicResultFixture([.object(["entries": .array([hidden, visible, unavailable]), "sections": .array([])])])
        let service = try await NativeMusicFixture.service(platform: peer), page = try await service.homePage(cursor: nil, refresh: true)
        let section = try XCTUnwrap(page.sections.first)
        XCTAssertEqual(section.layout, .featureRail)
        XCTAssertEqual(section.items.map(\.id), ["entry:1", "entry:2"])
        XCTAssertEqual(section.items.map(\.title), ["运行器入口", "不可用入口"])
        XCTAssertTrue(section.items[0].requiresAccount)
        guard case .action(.daily(branch: .style)) = section.items[0].content,
              case .unavailable = section.items[1].content else { return XCTFail("Runtime display and availability must be consumed") }
        await assertMusicFailure(.authRequired) {
            _ = try await service.openHomeActionQueue(.daily(branch: .style), title: "运行器入口")
        }
        let calls = await peer.calls(); XCTAssertEqual(calls.map(\.operation), ["home"]); await service.close()
    }
    func testEntryTabsFollowRuntimeDisplayAndSelectTheRemainingVisibleTab() async throws {
        let hidden: RuntimeValue = .object(["display": .bool(false)])
        let visible = D.collection("42", title: "仍可显示")
        let section: RuntimeValue = .object(["id": .string("tabbed"), "type": .string("entries"),
            "items": .array([hidden]), "tabs": .array([
                .object(["id": .string("hidden"), "items": .array([hidden])]),
                .object(["id": .string("visible"), "items": .array([visible])])
            ]), "selectedTab": .string("hidden")])
        let peer = NativeMusicResultFixture([D.page([
            D.section("hidden", items: [hidden], type: "entries"), section])])
        let service = try await NativeMusicFixture.service(platform: peer)
        let page = try await service.homePage(cursor: nil, refresh: true), shown = try XCTUnwrap(page.sections.first)
        XCTAssertEqual(page.sections.count, 1)
        XCTAssertEqual(shown.tabs.map(\.id), ["visible"]); XCTAssertEqual(shown.selectedTab, "visible")
        XCTAssertEqual(shown.items.map(\.title), ["仍可显示"])
        await service.close()
    }
    func testHiddenEntriesKeepTheRealContinuationWithoutAnEmptyFeatureRail() async throws {
        let hidden: RuntimeValue = .object(["display": .bool(false)])
        let peer = NativeMusicResultFixture([
            .object(["entries": .array([hidden]), "sections": .array([]),
                "continuation": .object(["cursor": .string("next")])]),
            D.page([D.section("public", items: [D.collection("42")])])
        ])
        let service = try await NativeMusicFixture.service(platform: peer)
        let first = try await service.homePage(cursor: nil, refresh: true)
        XCTAssertTrue(first.sections.isEmpty)
        let second = try await service.homePage(cursor: XCTUnwrap(first.cursor), refresh: false)
        XCTAssertEqual(second.sections.first?.items.count, 1); XCTAssertNil(second.cursor)
        let calls = await peer.calls(); XCTAssertEqual(calls.count, 2); await service.close()
    }
    func testRepeatedCollectionsAndPreviewSongsReuseCurrentRepositoryWithoutCoverSearch() async throws {
        var item = D.collection("41")
        if case .object(var row) = item { row["previewSongs"] = .array([try D.song("71", cover: "https://artwork.invalid/song.jpg")]); item = .object(row) }
        let peer = NativeMusicResultFixture([D.page([D.section("one", items: [item]), D.section("two", items: [item])])]), service = try await NativeMusicFixture.service(platform: peer)
        let page = try await service.homePage(cursor: nil, refresh: true)
        XCTAssertNil(page.sections[0].items[0].artwork); XCTAssertEqual(page.sections[0].items[0].previewSongs.first?.songId, page.sections[1].items[0].previewSongs.first?.songId)
        let calls = await peer.calls(); XCTAssertEqual(calls.count, 1); await service.close()
    }
    func testAbsentOptionalCoverKeepsContentWhileRealAuthenticationFailurePropagates() async throws {
        let peer = NativeMusicResultFixture([D.page([D.section("s", items: [D.collection("41")])])]), service = try await NativeMusicFixture.service(platform: peer)
        let page = try await service.homePage(cursor: nil, refresh: true); XCTAssertNil(page.sections[0].items[0].artwork)
        await service.close()
        let rejected = try await NativeMusicFixture.service(platform: NativeMusicResultFixture(failure: .authRequired))
        await assertMusicFailure(.authRequired) { _ = try await rejected.homePage(cursor: nil, refresh: true) }; await rejected.close()
    }
    func testCanonicalSongsAndTabsDoNotTriggerNativeMetadataEnrichment() async throws {
        let songs = try (1...30).map { try D.song(String($0), title: $0 == 2 ? "" : "Song \($0)") }
        let section: RuntimeValue = .object(["id": .string("s"), "type": .string("songs"), "items": .array(songs),
            "tabs": .array([.object(["id": .string("shown"), "name": .string("Shown"), "items": .array(songs)]),
                .object(["id": .string("hidden"), "name": .string("Hidden"), "items": .array([try D.song("31")])])]), "selectedTab": .string("shown")])
        let peer = NativeMusicResultFixture([D.page([section])]), service = try await NativeMusicFixture.service(platform: peer), page = try await service.homePage(cursor: nil, refresh: true)
        XCTAssertEqual(page.sections[0].items.count, 30); XCTAssertEqual(page.sections[0].tabs[1].items.count, 1)
        let calls = await peer.calls(); XCTAssertEqual(calls.map(\.operation), ["home"]); await service.close()
    }
    func testHomeAuthorizationFailureIsNotGuestPlaceholderContent() async throws {
        let peer = NativeMusicResultFixture(failure: .authRequired), service = try await NativeMusicFixture.service(platform: peer)
        await assertMusicFailure(.authRequired) { _ = try await service.publicDiscovery(.home, cursor: nil) }
        let calls = await peer.calls(); XCTAssertEqual(calls.count, 1); await service.close()
    }
    func testUnconsumedDeepBranchDoesNotImposeNativeTraversalBudget() async throws {
        var unused: RuntimeValue = .object([:]); for _ in 0..<30 { unused = .object(["children": .array([unused])]) }
        let response: RuntimeValue = .object(["sections": .array([D.section("s", items: [D.collection("41")])]), "unused": unused])
        let service = try await NativeMusicFixture.service(platform: NativeMusicResultFixture([response]))
        let page = try await service.homePage(cursor: nil, refresh: true); XCTAssertEqual(page.sections[0].items.count, 1); await service.close()
    }
    func testHomeRetainsHeadingsAndIgnoresLargeUnusedMetadata() async throws {
        let response: RuntimeValue = .object(["sections": .array([D.section("one", items: [D.collection("41")], title: "原版栏目一"), D.section("two", items: [try D.song("42")], type: "songs", title: "")]),
            "metadata": .string("18446744073709551617")])
        let service = try await NativeMusicFixture.service(platform: NativeMusicResultFixture([response])), page = try await service.homePage(cursor: nil, refresh: true)
        XCTAssertEqual(page.sections.map(\.title), ["原版栏目一", ""]); XCTAssertEqual(page.sections.map(\.layout), [.playlistRail, .songColumns]); await service.close()
    }
    func testMixedPlaylistAlbumAndSongPreserveCanonicalContinuation() async throws {
        let peer = NativeMusicResultFixture([D.page([D.section("collections", items: [D.collection("4"), D.collection("5", album: true)]), D.section("songs", items: [try D.song("6")], type: "songs")], next: .object(["cursor": .string("opaque")]))])
        let service = try await NativeMusicFixture.service(platform: peer), page = try await service.homePage(cursor: nil, refresh: true)
        XCTAssertNotNil(page.cursor); XCTAssertEqual(page.sections.flatMap(\.items).count, 3)
        guard case .collection(let album) = page.sections[0].items[1].content, case .track(let song) = page.sections[1].items[0].content else { return XCTFail("typed contents required") }
        XCTAssertEqual(album.kind, .album); XCTAssertEqual(song.sources[0].nativeId, "6")
        let calls = await peer.calls(); XCTAssertEqual(calls.count, 1); await service.close()
    }
    func testDirectoryUsesTypedActionsNotExecutableURLs() async throws {
        let response: RuntimeValue = .object(["items": .array([.object(["id": .string("classical"), "name": .string("Classical"), "kind": .string("channel"), "channelKind": .string("cube"), "pageCode": .string("classical_1")]),
            .object(["name": .string("Unknown"), "url": .string("javascript:alert(1)")])])])
        let service = try await NativeMusicFixture.service(platform: NativeMusicResultFixture([response])), page = try await service.publicDiscovery(.channels, cursor: nil)
        guard case .channel(let channel) = page.sections[0].items[0].content, case .unavailable = page.sections[0].items[1].content else { return XCTFail("unknown URL cannot become an action") }
        XCTAssertEqual(channel.kind, .cube); XCTAssertEqual(channel.pageCode, "classical_1"); await service.close()
    }
    func testSelectedTabsRetainAllCanonicalItemsWithoutWalkingUpstreamDSL() async throws {
        let section: RuntimeValue = .object(["id": .string("tabs"), "items": .array([D.collection("2", title: "B")]), "tabs": .array([
            .object(["id": .string("a"), "name": .string("A"), "items": .array([D.collection("1", title: "A")])]),
            .object(["id": .string("b"), "name": .string("B"), "items": .array([D.collection("2", title: "B")])])]), "selectedTab": .string("b")])
        let service = try await NativeMusicFixture.service(platform: NativeMusicResultFixture([D.page([section])])), page = try await service.publicDiscovery(.channel(channel, tag: "b"), cursor: nil)
        XCTAssertEqual(page.sections[0].selectedTab, "b"); XCTAssertEqual(page.sections[0].tabs.map(\.title), ["A", "B"]); XCTAssertEqual(page.sections[0].items[0].title, "B"); await service.close()
    }
    func testDailyRequiresExistingAccountPortWithoutAnonymousFallback() async throws {
        let peer = NativeMusicResultFixture(), service = try await NativeMusicFixture.service(platform: peer)
        await assertMusicFailure(.authRequired) { _ = try await service.publicDiscovery(.daily(.netease), cursor: nil) }
        let calls = await peer.calls(); XCTAssertTrue(calls.isEmpty); await service.close()
    }
    func testEmptyVisibleMorePageRemainsUsableAndCanAdvance() async throws {
        let peer = NativeMusicResultFixture([D.page([], next: .object(["cursor": .string("next")])), D.page([D.section("s", items: [D.collection("41")])])])
        let service = try await NativeMusicFixture.service(platform: peer), first = try await service.homePage(cursor: nil, refresh: true)
        XCTAssertTrue(first.sections.isEmpty)
        let second = try await service.homePage(cursor: XCTUnwrap(first.cursor), refresh: false); XCTAssertEqual(second.sections[0].items.count, 1); XCTAssertNil(second.cursor); await service.close()
    }
    func testCubeConsumerKeepsTypedRulePointAndAlgorithmSelectors() async throws {
        let action: RuntimeValue = .object(["type": .string("fm"), "provider": .string("rule"), "ruleId": .string("3"), "tabTagId": .string("4"), "sourceName": .string("Feed")])
        let peer = NativeMusicResultFixture([D.page([D.section("feed", items: [.object(["name": .string("Feed"), "action": action])])])]), service = try await NativeMusicFixture.service(platform: peer)
        let page = try await service.publicDiscovery(.channel(channel, tag: "|4"), cursor: nil)
        guard case .action(let value) = page.sections[0].items[0].content else { return XCTFail("action required") }
        XCTAssertEqual(value, .ruleFM(ruleID: "3", tabTagID: "4", sourceName: "Feed"))
        let calls = await peer.calls(); XCTAssertEqual(calls[0].parameters[music: "tag"].musicText, "|4"); await service.close()
    }
    func testRecommendationContinuationBeyondFormerOffsetLimitIsForwardedWhole() async throws {
        let tokens: [RuntimeValue] = [5970, 6000, 6030].map { .object(["offset": .number(Double($0)), "ruleId": .string("3")]) }
        let peer = NativeMusicResultFixture(try tokens.enumerated().map { D.page([D.section("feed", items: [try D.song(String($0.offset + 6))], type: "songs")], next: $0.offset + 1 < tokens.count ? tokens[$0.offset + 1] : .null) })
        let port = MusicNativePlatforms(client: try peer.makeClient()); var remote = try MJ.parse(RuntimeJSON.encode(tokens[0]))
        for index in tokens.indices {
            let page = try await port.browseDiscovery(.channel(channel, tag: ""), source: .netease, remote: remote)
            XCTAssertEqual(page.sections[0].items.count, 1); if let next = page.next { remote = next } else { XCTAssertEqual(index, 2) }
        }
        let calls = await peer.calls(); XCTAssertEqual(calls.map { $0.parameters[music: "continuation"] }, tokens)
    }
    func testRealProtocolOffsetFailurePropagatesWithoutNativeReinterpretation() async throws {
        let peer = NativeMusicResultFixture(failure: .limit), service = try await NativeMusicFixture.service(platform: peer)
        await assertMusicFailure(.limit) { _ = try await service.publicDiscovery(.channel(channel, tag: ""), cursor: nil) }
        let calls = await peer.calls(); XCTAssertEqual(calls.count, 1); await service.close()
    }
    func testAbsentOptionalAlgorithmTagsStillPresentActualSongs() async throws {
        let peer = NativeMusicResultFixture([D.page([D.section("feed", items: [try D.song("6")], type: "songs")])]), service = try await NativeMusicFixture.service(platform: peer)
        let page = try await service.publicDiscovery(.channel(channel, tag: ""), cursor: nil), item = page.sections[0].items[0]
        guard case .track(let track) = item.content else { return XCTFail("song required") }
        XCTAssertEqual(item.artwork, track.artwork); XCTAssertTrue(page.filters.isEmpty)
        let calls = await peer.calls(); XCTAssertEqual(calls.count, 1); await service.close()
    }
    func testAlgorithmFiltersStaySeparateFromTabsAndCursorBindsExactSelection() async throws {
        var section = D.section("feed", items: [try D.song("6")], type: "songs")
        if case .object(var row) = section { row["tags"] = .array([.object(["id": .string("|4"), "name": .string("Synthetic"), "group": .string("风格")])]); row["selectedTag"] = .string("|4"); section = .object(row) }
        let peer = NativeMusicResultFixture([D.page([section], next: .object(["offset": .number(30)])), D.page([section])]), service = try await NativeMusicFixture.service(platform: peer)
        let target = MusicPublicBrowseDestination.channel(channel, tag: "|4"), first = try await service.publicDiscovery(target, cursor: nil)
        let second = try await service.publicDiscovery(target, cursor: XCTUnwrap(first.cursor))
        XCTAssertTrue(first.filters.isEmpty); XCTAssertEqual(first.sections[0].filters.map(\.group), ["风格"]); XCTAssertEqual(first.sections[0].selectedFilter, "|4")
        XCTAssertEqual(first.sections[0].id, second.sections[0].id)
        await assertMusicFailure(.staleResult) { _ = try await service.publicDiscovery(.channel(channel, tag: ""), cursor: first.cursor) }; await service.close()
    }
    func testFeedFailureDoesNotTurnIntoEmptySuccessAndDoesNotRetry() async throws {
        let peer = NativeMusicResultFixture(failure: .upstreamFailed), service = try await NativeMusicFixture.service(platform: peer)
        await assertMusicFailure(.upstreamFailed) { _ = try await service.publicDiscovery(.channel(channel, tag: ""), cursor: nil) }
        let calls = await peer.calls(); XCTAssertEqual(calls.count, 1); await service.close()
    }
    func testInitialFeedFailureStaysOnItsSectionWithoutDroppingOtherContentOrRetrying() async throws {
        let feed: RuntimeValue = .object(["id": .string("feed"), "title": .string("Songs"),
            "type": .string("songs"), "items": .array([]), "errorCode": .string("NETWORK_ERROR"),
            "tags": .array([.object(["id": .string("all"), "name": .string("全部歌曲")])]),
            "selectedTag": .string("all")])
        let peer = NativeMusicResultFixture([D.page([D.section("cards", items: [D.collection("41")]), feed])])
        let service = try await NativeMusicFixture.service(platform: peer)
        let page = try await service.publicDiscovery(.channel(channel, tag: ""), cursor: nil)
        XCTAssertEqual(page.sections.count, 2)
        XCTAssertEqual(page.sections[0].items.count, 1)
        XCTAssertNil(page.sections[0].error)
        XCTAssertEqual(page.sections[1].error, .connectionFailed)
        XCTAssertTrue(page.sections[1].items.isEmpty)
        XCTAssertEqual(page.sections[1].filters.map(\.id), ["all"])
        XCTAssertEqual(page.sections[1].selectedFilter, "all")
        XCTAssertNil(page.cursor)
        let calls = await peer.calls(); XCTAssertEqual(calls.count, 1)
        await service.close()
    }
    func testOptionalTagAuthenticationFailureRemainsAuthenticationNotGuestFeed() async throws {
        let peer = NativeMusicResultFixture(failure: .authRequired), service = try await NativeMusicFixture.service(platform: peer)
        await assertMusicFailure(.authRequired) { _ = try await service.publicDiscovery(.channel(channel, tag: ""), cursor: nil) }
        let calls = await peer.calls(); XCTAssertEqual(calls.count, 1); await service.close()
    }
}
