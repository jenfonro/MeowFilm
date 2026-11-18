import Foundation
import XCTest
import MeowMusicFeature
import MeowMusicTestSupport
import MeowRuntime
@testable import MeowMusicNativeService

final class MusicNativeContentProtocolTests: XCTestCase {
    private func fixture(_ name: String) throws -> RuntimeValue {
        let path = "tests/fixtures/music-content/\(name).json"
        var root = URL(fileURLWithPath: #filePath).deletingLastPathComponent()
        while !FileManager.default.fileExists(atPath: root.appendingPathComponent(path).path) {
            let parent = root.deletingLastPathComponent()
            guard parent != root else { throw MusicServiceError.notFound }
            root = parent
        }
        return try RuntimeJSON.decode(Data(contentsOf: root.appendingPathComponent(path)))
    }

    func testSharedBrowseFixtureKeepsFinalLayoutsPartialCardsAndIndependentBannerArtwork() async throws {
        let peer = NativeMusicResultFixture([try fixture("browse-page")])
        let service = try await NativeMusicFixture.service(platform: peer)
        let channel = MusicPublicBrowseChannel(id: "electronic", title: "电音", kind: .cube, pageCode: "SYNTHETIC_ELECTRONIC")
        let page = try await service.publicDiscovery(.channel(channel, tag: ""), cursor: nil)
        XCTAssertEqual(page.sections.map(\.layout), [.banner, .featureRail, .list, .grid, .rankingStack, .list])
        XCTAssertEqual(page.sections.map { $0.items.count }, [2, 1, 2, 2, 1, 0])
        XCTAssertTrue(page.sections.allSatisfy { $0.error == nil })
        let banner = page.sections[0].items
        XCTAssertEqual(banner.map(\.title), ["活动横幅", "无横幅图"])
        XCTAssertEqual(banner[0].artwork?.absoluteString, "https://images.invalid/banner.jpg")
        XCTAssertNil(banner[1].artwork); XCTAssertTrue(banner.allSatisfy { $0.coverRef == nil })
        guard case .track(let song) = banner[0].content else { return XCTFail("Song action") }
        XCTAssertEqual(song.artwork?.absoluteString, "https://images.invalid/song.jpg")
        let partial = page.sections[2].items[1]
        XCTAssertEqual(partial.title, "仅展示歌曲")
        guard case .unavailable = partial.content else { return XCTFail("No repository identity") }
        XCTAssertEqual(page.sections[3].items.map(\.title), ["缺少定位的歌单", "未知动作内容"])
        XCTAssertEqual(page.sections[3].items[1].artwork?.absoluteString, "https://images.invalid/unknown.jpg")
        let reference = page.sections[1].items[0].coverRef
        XCTAssertEqual(reference, .init(platform: .netease, id: "synthetic-list"))
        XCTAssertEqual(page.sections[1].items[0].presentation, "preview")
        XCTAssertEqual(page.sections[1].tabs.count, 2)
        guard case .channel(let chart) = page.sections[4].items[0].content else { return XCTFail("Chart navigation") }
        XCTAssertEqual(chart.kind, .chart); XCTAssertEqual(chart.id, "albums")
        let document = try await service.read()
        XCTAssertEqual(try document.songRows.allSongIDs().count, 1)
        let calls = await peer.calls(); XCTAssertEqual(calls.map(\.operation), ["channel"])
        await service.close()
    }

    func testSharedHomeFixtureRetainsLabelAndCardFieldsThroughSnapshot() async throws {
        let service = try await NativeMusicFixture.service(platform: NativeMusicResultFixture([try fixture("home-page")]))
        let page = try await service.homePage(cursor: nil, refresh: true)
        let item = try XCTUnwrap(page.sections.first?.items.first)
        XCTAssertEqual(item.label, "私人雷达")
        XCTAssertEqual(item.title, "私人雷达"); XCTAssertEqual(item.subtitle, "今天的歌单内容")
        XCTAssertEqual(item.contextText, "今日动态歌单"); XCTAssertEqual(item.playCount, 3031000)
        let display = await service.homeDisplay
        let native = try XCTUnwrap(display?.page)
        let encoded = try MusicNativeHomeSnapshot.encode(native, source: .netease)
        let restored = try XCTUnwrap(MusicNativeHomeSnapshot.decode(encoded, source: .netease))
        let saved = try XCTUnwrap(restored.sections.first?.items.first)
        XCTAssertEqual(saved.title, item.title); XCTAssertEqual(saved.label, item.label)
        XCTAssertEqual(saved.kind, item.kind); XCTAssertEqual(saved.coverRef, item.coverRef)
        XCTAssertEqual(saved.presentation, item.presentation)
        XCTAssertEqual(saved.contextText, item.contextText); XCTAssertEqual(saved.playCount, item.playCount)
        guard case .collection(let collection) = saved.content else { return XCTFail("Expected collection") }
        XCTAssertEqual(collection.playCount, 3031000)
        await service.close()
    }

    func testSharedChannelDirectoryAndChartNavigationUseDeclaredSelectors() async throws {
        let peer = NativeMusicResultFixture([try fixture("channels"), .object(["sections": .array([])])])
        let service = try await NativeMusicFixture.service(platform: peer)
        let directory = try await service.publicDiscovery(.channels, cursor: nil)
        let channels = directory.sections.flatMap(\.items).compactMap { item -> MusicPublicBrowseChannel? in
            if case .channel(let channel) = item.content { return channel }; return nil
        }
        XCTAssertEqual(channels.map(\.kind), [.cube, .newSongs])
        XCTAssertEqual(channels[0].pageCode, "SYNTHETIC_ELECTRONIC")
        _ = try await service.publicDiscovery(.channel(.init(id: "albums", title: "专辑榜", kind: .chart), tag: ""), cursor: nil)
        let calls = await peer.calls()
        XCTAssertEqual(calls.map(\.operation), ["channels", "charts"])
        XCTAssertEqual(calls[1].parameters[music: "kind"].musicText, "albums")
        await service.close()
    }

    func testSharedListFixtureDisplaysSongsWithoutAdoptingMissingSources() async throws {
        let peer = NativeMusicResultFixture([try fixture("list-page"), try fixture("list-page")])
        let service = try await NativeMusicFixture.service(platform: peer)
        let search = try await service.search(platform: "mg", query: "Synthetic", page: 1, limit: 30)
        XCTAssertEqual(search.items.map(\.title), ["合成歌曲"])
        XCTAssertTrue(search.items[0].songId.isEmpty); XCTAssertTrue(search.items[0].sources.isEmpty)
        let detail = try await service.publicDetail(.init(platform: .migu, nativeID: "list", title: ""), cursor: nil)
        XCTAssertEqual(detail.tracks.map(\.title), ["合成歌曲"])
        XCTAssertEqual(detail.tracks[0].artwork?.absoluteString, "https://images.invalid/song.jpg")
        let document = try await service.read()
        XCTAssertTrue(try document.songRows.allSongIDs().isEmpty)
        await service.close()
    }

    func testPlaylistSummaryReusesDetailRootCoverAndNeverMemberCover() async throws {
        var detail = try MusicNativePublicBrowseFixture.page(["song"], collectionID: "list")
        if case .object(var fields) = detail {
            fields["cover"] = .string("https://images.invalid/root.jpg"); detail = .object(fields)
        }
        let peer = NativeMusicResultFixture([detail, .object(["items": .array([])])])
        let service = try await NativeMusicFixture.service(platform: peer)
        _ = try await service.publicDetail(.init(platform: .netease, nativeID: "list", title: ""), cursor: nil)
        let summaries = try await service.publicPlaylistSummaries(platform: .netease, ids: ["list", "missing"])
        XCTAssertEqual(summaries.map(\.nativeID), ["list"])
        XCTAssertEqual(summaries[0].artwork?.absoluteString, "https://images.invalid/root.jpg")
        let calls = await peer.calls()
        XCTAssertEqual(calls.map(\.operation), ["playlist", "playlists"])
        XCTAssertEqual(calls[1].parameters[music: "ids"].musicArray.map(\.musicText), ["missing"])
        await service.close()
    }
}
