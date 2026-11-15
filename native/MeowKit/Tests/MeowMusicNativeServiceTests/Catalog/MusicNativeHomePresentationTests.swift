import Foundation
import XCTest
import MeowMusicFeature
import MeowMusicTestSupport
import MeowRuntime
@testable import MeowMusicNativeService

final class MusicNativeHomePresentationTests: XCTestCase {
    private func load(_ sections: [RuntimeValue]) async throws -> (NativeMusicService, NativeMusicResultFixture, MusicPublicBrowsePage) {
        let peer = NativeMusicResultFixture([NativeDiscoveryFixture.page(sections)])
        let service = try await NativeMusicFixture.service(platform: peer)
        return (service, peer, try await service.homePage(cursor: nil, refresh: true))
    }
    private func section(_ id: String, channel: String? = nil, items: [RuntimeValue], title: String = "栏目") -> RuntimeValue {
        var row: RuntimeObject = ["id": .string(id), "title": .string(title), "items": .array(items)]
        if let channel { row["action"] = .object(["type": .string("channel"), "channel": .object([
            "id": .string(channel), "name": .string(title), "channelKind": .string(channel)])]) }
        return .object(row)
    }
    func testHeaderActionsUseDirectoryChannelIdentityAndPreserveCardDestinations() async throws {
        let (service, peer, page) = try await load([
            section("lists", channel: "playlist", items: [NativeDiscoveryFixture.collection("41")]),
            section("ranks", channel: "chart", items: [NativeDiscoveryFixture.collection("42")])])
        XCTAssertEqual(page.sections.compactMap { $0.channel?.kind }, [.playlists, .rankings])
        for (section, id) in zip(page.sections, ["41", "42"]) {
            guard case .collection(let value) = section.items[0].content else { return XCTFail("collection required") }
            XCTAssertEqual(value.nativeID, id); XCTAssertEqual(value.platform, .netease)
        }
        let calls = await peer.calls(); XCTAssertEqual(calls.map(\.operation), ["home"])
        await service.close()
    }
    func testHeaderAndCardTitlesNeverInventOrReplaceDeclaredNavigation() async throws {
        let (service, _, page) = try await load([
            section("lists", items: [NativeDiscoveryFixture.collection("41")], title: "排行榜"),
            section("songs", items: [try NativeDiscoveryFixture.song("42")], title: "推荐歌单"),
            section("declared", channel: "chart", items: [NativeDiscoveryFixture.collection("43")], title: "任意标题")])
        XCTAssertNil(page.sections[0].channel); XCTAssertNil(page.sections[1].channel)
        XCTAssertEqual(page.sections[2].channel?.kind, .rankings)
        XCTAssertEqual(page.sections.map { $0.items.count }, [1, 1, 1])
        await service.close()
    }
    func testCanonicalLabelsArtworkQueriesAndOptionalFieldsArePresentedLiterally() async throws {
        let cover = "http://independent-cdn.invalid/cover.jpg?size=800&quality=90"
        var item = NativeDiscoveryFixture.collection("41", title: "歌单 | 原标题", cover: cover)
        if case .object(var row) = item { row["subtitle"] = .string("原副标题"); row["coverLabel"] = .string("新专辑"); item = .object(row) }
        let (service, _, page) = try await load([section("s", items: [item, NativeDiscoveryFixture.collection("42", title: "")])])
        XCTAssertEqual(page.sections[0].items[0].title, "歌单 | 原标题")
        XCTAssertEqual(page.sections[0].items[0].subtitle, "原副标题"); XCTAssertEqual(page.sections[0].items[0].coverLabel, "新专辑")
        XCTAssertEqual(page.sections[0].items[0].artwork?.absoluteString, cover.replacingOccurrences(of: "http:", with: "https:"))
        XCTAssertNil(page.sections[0].items[1].artwork); XCTAssertEqual(page.sections[0].items[1].title, "")
        await service.close()
    }
    func testCredentialFreeArtworkPolicyDoesNotGuessHostsOrBorrowAnotherResource() async throws {
        for cover in ["https://independent-cdn.invalid/a.jpg", "https://pic.kuwo.cn/a.jpg", "https://d.musicapp.migu.cn/a.jpg"] {
            let (service, _, page) = try await load([section("s", items: [NativeDiscoveryFixture.collection("41", cover: cover)])])
            XCTAssertEqual(page.sections[0].items[0].artwork?.absoluteString, cover); await service.close()
        }
        for cover in ["javascript:alert(1)", "https://user:secret@cdn.invalid/a.jpg", "https://cdn.invalid:8443/a.jpg", "https://cdn.invalid/a.jpg#fragment"] {
            let (service, _, page) = try await load([section("s", items: [NativeDiscoveryFixture.collection("41", cover: cover)])])
            XCTAssertNil(page.sections[0].items[0].artwork)
            guard case .collection(let value) = page.sections[0].items[0].content else { return XCTFail("collection remains usable") }
            XCTAssertEqual(value.nativeID, "41"); await service.close()
        }
    }
    func testCanonicalMixedRunsAndAllSectionsKeepTheirDeclaredOrder() async throws {
        let layouts = ["banner", "list", "grid", "artist-updates", "ranked-songs"]
        let sections = try (0..<25).map { index -> RuntimeValue in
            .object(["id": .string("section-\(index)"), "title": .string("栏目\(index)"),
                "layout": .string(layouts[index % layouts.count]), "items": .array([
                    NativeDiscoveryFixture.collection("collection-\(index)"), try NativeDiscoveryFixture.song("song-\(index)")])])
        }
        let (service, peer, page) = try await load(sections)
        XCTAssertEqual(page.sections.count, 25); XCTAssertEqual(page.sections.map(\.title), (0..<25).map { "栏目\($0)" })
        XCTAssertTrue(page.sections.allSatisfy { $0.items.count == 2 })
        XCTAssertEqual(Array(page.sections.prefix(5).map(\.layout)), [.banner, .list, .grid, .artistUpdates, .rankedSongs])
        let calls = await peer.calls(); XCTAssertEqual(calls.count, 1); await service.close()
    }
    func testEmptyCanonicalPageDoesNotFabricateGuestCardsOrHeadings() async throws {
        let (service, _, page) = try await load([])
        XCTAssertTrue(page.sections.isEmpty); XCTAssertNil(page.cursor); await service.close()
    }
    func testSelectedAndUnselectedTabsRetainTheirOwnItemsAndIdentifiers() async throws {
        let value: RuntimeValue = .object(["id": .string("tabs"), "items": .array([]), "selectedTab": .string("b"),
            "tabs": .array([.object(["id": .string("a"), "name": .string("A"), "items": .array([NativeDiscoveryFixture.collection("41")])]),
                .object(["id": .string("b"), "name": .string("B"), "items": .array([NativeDiscoveryFixture.collection("42")])])])])
        let (service, peer, page) = try await load([value])
        XCTAssertEqual(page.sections[0].tabs.map(\.id), ["a", "b"]); XCTAssertEqual(page.sections[0].selectedTab, "b")
        XCTAssertEqual(page.sections[0].items.map(\.id), page.sections[0].tabs[1].items.map(\.id))
        let calls = await peer.calls(); XCTAssertEqual(calls.count, 1); await service.close()
    }
}
