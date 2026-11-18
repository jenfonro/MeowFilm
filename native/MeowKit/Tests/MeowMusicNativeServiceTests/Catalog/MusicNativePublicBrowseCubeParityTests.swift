import XCTest
import MeowMusicFeature
import MeowMusicTestSupport
import MeowRuntime
@testable import MeowMusicNativeService

private typealias D = NativeDiscoveryFixture
final class MusicNativePublicBrowseCubeParityTests: XCTestCase {
    private let channel = MusicPublicBrowseChannel(id: "synthetic", title: "Synthetic", kind: .cube, pageCode: "synthetic_page")
    private func tabs(_ groups: [(String, [RuntimeValue])], selected: String) -> RuntimeValue {
        .object(["id": .string("recommend"), "title": .string("官方栏目"),
            "items": .array(groups.first { $0.0 == selected }?.1 ?? []), "selectedTab": .string(selected),
            "tabs": .array(groups.map { .object(["id": .string($0.0), "name": .string($0.0), "items": .array($0.1)]) })])
    }
    func testCanonicalTabsKeepNumericFMSelectorAndDeclaredCoverLabelsWithoutNativeHydration() async throws {
        let fm: RuntimeValue = .object(["title": .string("规则封面"), "kind": .string("fm"), "coverLabel": .string("规则封面"),
            "action": .object(["type": .string("fm"), "provider": .string("rule"), "ruleId": .string(""), "tabTagId": .string("17001"), "sourceName": .string("规则封面")])])
        let inherited: RuntimeValue = .object(["title": .string("继承封面"), "kind": .string("fm"), "coverLabel": .string("继承封面"),
            "action": .object(["type": .string("fm"), "provider": .string("rule"), "tabTagId": .string("electronic"), "sourceName": .string("继承封面")])])
        let response = D.page([tabs([("一", [try D.song("11"), D.collection("41", title: "水合歌单")]), ("二", [try D.song("12"), fm, inherited])], selected: "二")])
        let peer = NativeMusicResultFixture([response]), service = try await NativeMusicFixture.service(platform: peer), page = try await service.publicDiscovery(.channel(channel, tag: ""), cursor: nil)
        let section = try XCTUnwrap(page.sections.first); XCTAssertEqual(section.selectedTab, "二"); XCTAssertEqual(section.tabs.map(\.title), ["一", "二"])
        XCTAssertEqual(section.items.map(\.coverLabel), ["", "规则封面", "继承封面"])
        guard case .action(let action) = section.items[1].content, case .action(let second) = section.items[2].content else { return XCTFail("FM actions required") }
        XCTAssertEqual(action, .ruleFM(ruleID: "", tabTagID: "17001", sourceName: "规则封面")); XCTAssertEqual(second, .ruleFM(ruleID: "", tabTagID: "electronic", sourceName: "继承封面"))
        XCTAssertEqual(section.tabs[0].items.map(\.title), ["Synthetic song", "水合歌单"])
        let calls = await peer.calls(); XCTAssertEqual(calls.map(\.operation), ["channel"]); await service.close()
    }
    func testMissingOptionalCoverInUnselectedTabDoesNotTriggerSummaryRequests() async throws {
        let response = D.page([tabs([("一", [D.collection("41", title: "已有名称")]), ("二", [D.collection("42", title: "无封面")])], selected: "二")])
        let peer = NativeMusicResultFixture([response]), service = try await NativeMusicFixture.service(platform: peer), page = try await service.publicDiscovery(.channel(channel, tag: ""), cursor: nil)
        XCTAssertEqual(page.sections[0].items.map(\.title), ["无封面"]); XCTAssertNil(page.sections[0].items[0].artwork)
        XCTAssertEqual(page.sections[0].tabs[0].items.map(\.title), ["已有名称"]); XCTAssertNil(page.sections[0].tabs[0].items[0].artwork)
        let calls = await peer.calls(); XCTAssertEqual(calls.count, 1); await service.close()
    }
    func testCanonicalGridAndScrollKeepSongCollectionAndAlbumSectionsSeparate() async throws {
        let response = D.page([D.section("songs", items: [try D.song("11")], title: "混排", layout: "song-columns"),
            D.section("grid", items: [D.collection("41"), D.collection("42", album: true)], title: "混排", layout: "playlist-grid"),
            D.section("rail", items: [D.collection("43")], title: "混排")])
        let service = try await NativeMusicFixture.service(platform: NativeMusicResultFixture([response])), page = try await service.publicDiscovery(.channel(channel, tag: ""), cursor: nil)
        XCTAssertEqual(page.sections.map(\.layout), [.songColumns, .grid, .playlistRail]); XCTAssertEqual(page.sections.map(\.title), ["混排", "混排", "混排"])
        XCTAssertEqual(page.sections.map { $0.items.count }, [1, 2, 1]); await service.close()
    }
    func testFiniteFeedKeepsHeaderSeparateAndDoesNotInventContinuationOrAlgorithmRead() async throws {
        var header = D.collection("43", title: "专辑", album: true)
        if case .object(var row) = header { row["subtitle"] = .string("副标题"); header = .object(row) }
        let response = D.page([D.section("header", items: [header]), D.section("feed", items: [try D.song("11"), try D.song("12")], layout: "song-list")])
        let peer = NativeMusicResultFixture([response]), service = try await NativeMusicFixture.service(platform: peer), page = try await service.publicDiscovery(.channel(channel, tag: ""), cursor: nil)
        XCTAssertNil(page.cursor); XCTAssertNil(page.notice); XCTAssertEqual(page.sections.count, 2); XCTAssertEqual(page.sections[0].items[0].subtitle, "副标题")
        XCTAssertEqual(page.sections[1].layout, .list); XCTAssertEqual(page.sections[1].items.count, 2)
        let calls = await peer.calls(); XCTAssertEqual(calls.count, 1); await service.close()
    }
    func testCurrentProviderSelectionIsConsumedWithoutGuessingCarouselParents() async throws {
        let response: RuntimeValue = .object(["sections": .array([]), "tags": .array(["a", "b", "c", "d", "e", "f"].map { .object(["id": .string($0), "name": .string($0)]) }), "selectedTag": .string("b|")])
        let service = try await NativeMusicFixture.service(platform: NativeMusicResultFixture([response])), page = try await service.publicDiscovery(.channel(channel, tag: ""), cursor: nil)
        XCTAssertEqual(page.selectedFilter, "b|"); XCTAssertEqual(page.filters.count, 6); await service.close()
    }
    func testAllTabItemsBeyondFormerBudgetAndRepeatedIDsRemainConsumable() async throws {
        let rows = (1...301).map { D.collection(String($0)) }
        let response = D.page([tabs([("same", rows), ("same", rows)], selected: "same")])
        let peer = NativeMusicResultFixture([response]), service = try await NativeMusicFixture.service(platform: peer), page = try await service.publicDiscovery(.channel(channel, tag: ""), cursor: nil)
        XCTAssertEqual(page.sections[0].tabs.map { $0.items.count }, [301, 301]); XCTAssertEqual(page.sections[0].items.count, 301)
        let calls = await peer.calls(); XCTAssertEqual(calls.count, 1); await service.close()
    }
    func testCurrentHomeSnapshotKeepsCoverLabelWithoutFMPreviewAuthority() throws {
        let item = MusicNativePublicBrowseItem(id: "fm", title: "FM", content: .action(.fm(mode: .default, submode: "", entry: nil)), coverLabel: "FM")
        let page = MusicNativePublicBrowseDiscovery(sections: [.init(id: "s", title: "", items: [item])]), data = try MusicNativeHomeSnapshot.encode(page, source: .netease)
        let restored = try XCTUnwrap(MusicNativeHomeSnapshot.decode(data, source: .netease)); XCTAssertEqual(restored.sections[0].items[0].coverLabel, "FM")
        XCTAssertTrue(restored.sections[0].items[0].previewSongs.isEmpty); XCTAssertFalse(String(decoding: data, as: UTF8.self).contains("authorization"))
    }
}
