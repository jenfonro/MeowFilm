import XCTest
@testable import MeowMusicFeature

private actor MusicChannelReadGate {
    private var started = false, released = false
    private var starters: [CheckedContinuation<Void, Never>] = []
    private var waiters: [CheckedContinuation<Void, Never>] = []
    func wait() async {
        started = true; starters.forEach { $0.resume() }; starters.removeAll()
        if !released { await withCheckedContinuation { waiters.append($0) } }
    }
    func didStart() async {
        if !started { await withCheckedContinuation { starters.append($0) } }
    }
    func release() {
        released = true; waiters.forEach { $0.resume() }; waiters.removeAll()
    }
}

private actor MusicReferenceHomeService: MusicPublicBrowsing {
    var reads = 0
    var firstChannelGate: MusicChannelReadGate?
    func holdFirstChannelRead(_ gate: MusicChannelReadGate) { firstChannelGate = gate }
    func publicPlaylists(platform: MusicPublicBrowsePlatform, query: String,
                         cursor: String?) throws -> MusicPublicBrowseCollectionPage {
        throw MusicServiceError.unsupported
    }
    func publicDetail(_ collection: MusicPublicBrowseCollection,
                      cursor: String?) throws -> MusicPublicBrowseDetail {
        throw MusicServiceError.unsupported
    }
    func publicDiscovery(_ destination: MusicPublicBrowseDestination,
                         cursor: String?) async -> MusicPublicBrowsePage {
        reads += 1
        var id = cursor == nil ? "first" : "second"
        if case .channel(let channel, _) = destination {
            id = channel.id
            if let gate = firstChannelGate {
                firstChannelGate = nil
                await gate.wait()
            }
        }
        return .init(sections: [.init(id: id, title: id, items: [
            .init(id: id, title: id, content: .unavailable)
        ], layout: .playlistRail)], cursor: cursor == nil ? "next" : nil)
    }
}

@MainActor final class MusicReferenceHomeStateTests: XCTestCase {
    private let chart = MusicPublicBrowseChannel(id: "chart", title: "排行榜", kind: .rankings)

    func testFirstVisitStartsOnDirectoryFirstItemAndLaterVisitsRestoreHistory() throws {
        let suite = "MusicReferenceHomeStateTests." + UUID().uuidString
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        defer { defaults.removePersistentDomain(forName: suite) }
        let playlists = MusicPublicBrowseChannel(id: "playlist", title: "歌单", kind: .playlists)
        let catalogue = [playlists, chart]
        let first = MusicChannelSelection(defaults: defaults)
        first.restoreChannel(from: catalogue, catalogueLoaded: true)
        XCTAssertEqual(first.channel, playlists)
        first.selectChannel(chart)
        let restored = MusicChannelSelection(defaults: defaults)
        restored.restoreChannel(from: [chart, playlists], catalogueLoaded: true)
        XCTAssertEqual(restored.channel, chart)
    }

    func testFirstVisitWaitsForLoadedCatalogueBeforeSelectingFirstItem() throws {
        let suite = "MusicReferenceHomeStateTests." + UUID().uuidString
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        defer { defaults.removePersistentDomain(forName: suite) }
        let playlists = MusicPublicBrowseChannel(id: "playlist", title: "歌单", kind: .playlists)
        let selection = MusicChannelSelection(defaults: defaults)
        selection.restoreChannel(from: [playlists, chart], catalogueLoaded: false)
        XCTAssertNil(selection.channel)
        selection.restoreChannel(from: [playlists, chart], catalogueLoaded: true)
        XCTAssertEqual(selection.channel, playlists)
    }

    func testFirstVisitUsesTheFirstDynamicChannelWithoutRanking() throws {
        let suite = "MusicReferenceHomeStateTests." + UUID().uuidString
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        defer { defaults.removePersistentDomain(forName: suite) }
        let playlists = MusicPublicBrowseChannel(id: "playlist", title: "歌单", kind: .playlists)
        let cube = MusicPublicBrowseChannel(id: "cube", title: "欧美", kind: .cube)
        let selection = MusicChannelSelection(defaults: defaults)
        selection.restoreChannel(from: [playlists, cube], catalogueLoaded: true)
        XCTAssertEqual(selection.channel, playlists)
    }

    func testUnknownHistoryFallsBackToFirstItemOnlyAfterCatalogueLoads() throws {
        let suite = "MusicReferenceHomeStateTests." + UUID().uuidString
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        defer { defaults.removePersistentDomain(forName: suite) }
        let missing = MusicPublicBrowseChannel(id: "missing", title: "已下架", kind: .playlists)
        MusicChannelSelection(defaults: defaults).selectChannel(missing)
        let playlists = MusicPublicBrowseChannel(id: "playlist", title: "歌单", kind: .playlists)
        let restored = MusicChannelSelection(defaults: defaults)
        restored.restoreChannel(from: [playlists, chart], catalogueLoaded: false)
        XCTAssertNil(restored.channel)
        restored.restoreChannel(from: [playlists, chart], catalogueLoaded: true)
        XCTAssertEqual(restored.channel, playlists)
        XCTAssertEqual(defaults.string(forKey: "music.ui.lastChannel"), missing.identity)
        let reloaded = MusicChannelSelection(defaults: defaults)
        reloaded.restoreChannel(from: [chart, playlists], catalogueLoaded: true)
        XCTAssertEqual(reloaded.channel, chart)
        reloaded.selectChannel(MusicPublicBrowseChannel(id: "playlist", title: "歌单", kind: .playlists))
        reloaded.restoreChannel(from: [chart], catalogueLoaded: true)
        XCTAssertEqual(reloaded.channel?.id, "playlist")
    }

    func testSelectedChannelUpdatesPersistedIdentityWhenStablePageCodeGetsANewID() throws {
        let suite = "MusicReferenceHomeStateTests." + UUID().uuidString
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        defer { defaults.removePersistentDomain(forName: suite) }
        let previous = MusicPublicBrowseChannel(id: "old", title: "电音", kind: .cube, pageCode: "electronic")
        let current = MusicPublicBrowseChannel(id: "new", title: "电音", kind: .cube, pageCode: "electronic")
        let selection = MusicChannelSelection(defaults: defaults)
        selection.selectChannel(previous)
        selection.restoreChannel(from: [current], catalogueLoaded: true)
        XCTAssertEqual(selection.channel, current)
        let restored = MusicChannelSelection(defaults: defaults)
        restored.restoreChannel(from: [current], catalogueLoaded: true)
        XCTAssertEqual(restored.channel, current)
    }

    func testChannelRestorationRequiresFullIdentityAndDoesNotRewriteUnmatchedValues() throws {
        let suite = "MusicReferenceHomeStateTests." + UUID().uuidString
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        defer { defaults.removePersistentDomain(forName: suite) }
        let channel = MusicPublicBrowseChannel(id: "electronic", title: "电音", kind: .cube,
            pageCode: "electronic_page", platform: .netease)
        for stored in [channel.id, channel.pageCode] {
            defaults.set(stored, forKey: "music.ui.lastChannel")
            let selection = MusicChannelSelection(defaults: defaults)
            selection.restoreChannel(from: [chart, channel], catalogueLoaded: false)
            XCTAssertNil(selection.channel)
            selection.restoreChannel(from: [chart, channel], catalogueLoaded: true)
            XCTAssertEqual(selection.channel, chart)
            XCTAssertEqual(defaults.string(forKey: "music.ui.lastChannel"), stored)
        }
        defaults.set(channel.identity, forKey: "music.ui.lastChannel")
        let selection = MusicChannelSelection(defaults: defaults)
        selection.restoreChannel(from: [chart, channel], catalogueLoaded: false)
        XCTAssertEqual(selection.channel, channel)
        XCTAssertEqual(defaults.string(forKey: "music.ui.lastChannel"), channel.identity)
    }

    func testRememberedChannelGetsItsOwnFirstPageAfterCatalogueRestoration() async throws {
        let suite = "MusicReferenceHomeStateTests." + UUID().uuidString
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        defer { defaults.removePersistentDomain(forName: suite) }
        MusicChannelSelection(defaults: defaults).selectChannel(chart)
        let navigation = MusicChannelSelection(defaults: defaults)
        let service = MusicReferenceHomeService()
        let access = UUID()
        navigation.restoreChannel(from: [chart], catalogueLoaded: true)
        XCTAssertEqual(navigation.channel, chart)
        let selected = try XCTUnwrap(navigation.channel)
        let page = MusicPublicBrowseViewModel(service: service, request: .discovery(.channel(selected, tag: "")))
        page.setAccess(available: true, generation: access); page.appear()
        await page.loadIfNeeded()
        XCTAssertTrue(page.loaded)
        XCTAssertEqual(page.sections.map(\.id), ["chart"])
        let reads = await service.reads
        XCTAssertEqual(reads, 1)
    }

    func testChannelRestorationSeparatesEqualProviderCodesAndPageCodes() throws {
        let suite = "MusicReferenceHomeStateTests." + UUID().uuidString
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        defer { defaults.removePersistentDomain(forName: suite) }
        let original = MusicPublicBrowseChannel(id: "shared", title: "当前来源", kind: .cube,
            pageCode: "shared_page", platform: .netease)
        let foreign = MusicPublicBrowseChannel(id: "shared", title: "其他来源", kind: .cube,
            pageCode: "shared_page", platform: .qq)
        let selection = MusicChannelSelection(defaults: defaults)
        selection.selectChannel(original)
        selection.restoreChannel(from: [foreign], catalogueLoaded: true)
        XCTAssertEqual(selection.channel, foreign)
        XCTAssertEqual(defaults.string(forKey: "music.ui.lastChannel"), original.identity)
        let restored = MusicChannelSelection(defaults: defaults)
        restored.restoreChannel(from: [foreign], catalogueLoaded: false); XCTAssertNil(restored.channel)
        restored.restoreChannel(from: [foreign, original], catalogueLoaded: false)
        XCTAssertEqual(restored.channel, original)
        XCTAssertNotEqual(original.identity, foreign.identity)
    }

    func testSourceSwitchRetiresMemorySelectionWithoutRewritingRememberedChannel() throws {
        let suite = "MusicReferenceHomeStateTests." + UUID().uuidString
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        defer { defaults.removePersistentDomain(forName: suite) }
        let selection = MusicChannelSelection(defaults: defaults)
        let feature = MusicPublicBrowseChannel(id: "feature", title: "精选", kind: .featured, platform: .kugou)
        let playlists = MusicPublicBrowseChannel(id: "playlist", title: "歌单广场", kind: .playlists, platform: .kugou)
        selection.selectChannel(chart)
        selection.restoreChannel(from: [feature, playlists], catalogueLoaded: false)
        XCTAssertNil(selection.channel)
        selection.restoreChannel(from: [feature, playlists], catalogueLoaded: true)
        XCTAssertEqual(selection.channel, feature)
        XCTAssertEqual(defaults.string(forKey: "music.ui.lastChannel"), chart.identity)
        selection.restoreChannel(from: [.init(id: "playlist", title: "歌单", kind: .playlists), chart], catalogueLoaded: true)
        XCTAssertEqual(selection.channel, chart)
    }

    func testPublicHomeErrorContextIsSourceNeutralButConcreteRequestsKeepTheirPlatform() {
        let service = MusicReferenceHomeService()
        for destination in [MusicPublicBrowseDestination.home, .channels, .featured] {
            let model = MusicPublicBrowseViewModel(service: service, request: .discovery(destination))
            XCTAssertEqual(model.errorContext, "音乐内容读取")
        }
        let channel = MusicPublicBrowseChannel(id: "playlist", title: "", kind: .playlists, platform: .kugou)
        let kugou = MusicPublicBrowseViewModel(service: service, request: .discovery(.channel(channel, tag: "")))
        XCTAssertEqual(kugou.errorContext, "酷狗音乐 · 音乐内容读取")
        let action = MusicPublicBrowseViewModel(service: service, request: .homeAction(.heartbeat, title: "心动模式"))
        XCTAssertEqual(action.errorContext, "网易云 · 音乐内容读取")
    }

    func testExplicitRefreshReplacesCancelledUnfinishedChannelReadBeforeItUnwinds() async {
        let service = MusicReferenceHomeService(), gate = MusicChannelReadGate(), access = UUID()
        await service.holdFirstChannelRead(gate)
        let page = MusicPublicBrowseViewModel(service: service, request: .discovery(.channel(chart, tag: "")))
        page.setAccess(available: true, generation: access); page.appear()
        let previous = Task { await page.loadIfNeeded() }
        await gate.didStart()
        previous.cancel()
        XCTAssertTrue(page.isLoading)
        await page.refresh()
        XCTAssertTrue(page.loaded); XCTAssertFalse(page.isLoading)
        XCTAssertEqual(page.sections.map(\.id), ["chart"])
        await gate.release(); await previous.value
        XCTAssertTrue(page.loaded); XCTAssertNil(page.error)
        XCTAssertEqual(page.sections.map(\.id), ["chart"])
        let reads = await service.reads
        XCTAssertEqual(reads, 2)
    }

    func testIndependentChannelsKeepInFlightAndLoadedContentAcrossCoverage() async throws {
        let service = MusicReferenceHomeService(), gate = MusicChannelReadGate(), access = UUID()
        await service.holdFirstChannelRead(gate)
        let page = MusicPublicBrowseViewModel(service: service, request: .discovery(.channel(chart, tag: "")))
        page.setAccess(available: true, generation: access); page.appear()
        let previous = Task { await page.loadIfNeeded() }
        await gate.didStart()
        let next = MusicPublicBrowseChannel(id: "new", title: "新歌", kind: .newSongs)
        let nextPage = MusicPublicBrowseViewModel(service: service, request: .discovery(.channel(next, tag: "")))
        nextPage.setAccess(available: true, generation: access); nextPage.appear()
        await nextPage.loadIfNeeded()
        XCTAssertTrue(page.isLoading)
        XCTAssertEqual(nextPage.sections.map(\.id), ["new"])
        await gate.release(); await previous.value
        XCTAssertEqual(page.sections.map(\.id), ["chart"])
        let intent = try XCTUnwrap(page.makeActionIntent())
        page.appear(); await page.loadIfNeeded()
        var acted = false
        await page.perform(intent: intent) { acted = true }
        XCTAssertTrue(acted)
        XCTAssertTrue(page.loaded); XCTAssertEqual(page.sections.map(\.id), ["chart"])
        XCTAssertEqual(nextPage.sections.map(\.id), ["new"])
        let reads = await service.reads
        XCTAssertEqual(reads, 2, "Returning to a loaded channel must not refetch")
        page.setAccess(available: false, generation: UUID())
        XCTAssertFalse(page.loaded); XCTAssertTrue(page.sections.isEmpty)
        XCTAssertTrue(nextPage.loaded)
    }


    func testRepeatedAppearanceKeepsLoadedHomeAndActionWithoutRefetch() async throws {
        let service = MusicReferenceHomeService()
        let page = MusicPublicBrowseViewModel(service: service, request: .discovery(.home))
        let generation = UUID()
        page.setAccess(available: true, generation: generation); page.appear()
        await page.refresh()
        let intent = try XCTUnwrap(page.makeActionIntent())
        page.appear(); await page.loadIfNeeded()
        XCTAssertTrue(page.loaded)
        XCTAssertEqual(page.sections.map(\.id), ["first"])
        var acted = false
        await page.perform(intent: intent) { acted = true }
        XCTAssertTrue(acted)
        XCTAssertNotNil(page.makeActionIntent())
        let count = await service.reads
        XCTAssertEqual(count, 1)
    }

    func testHomePaginationAppendsWithoutLosingLayoutOrFirstPage() async {
        let page = MusicPublicBrowseViewModel(service: MusicReferenceHomeService(), request: .discovery(.home))
        page.setAccess(available: true, generation: UUID()); page.appear()
        await page.refresh(); await page.appendNext()
        XCTAssertEqual(page.sections.map(\.id), ["first", "second"])
        XCTAssertEqual(page.sections.map(\.layout), [.playlistRail, .playlistRail])
        XCTAssertNil(page.nextCursor); XCTAssertNil(page.error)
    }

    func testProtectionChangeClearsEvenResidentPublicSnapshot() async {
        let page = MusicPublicBrowseViewModel(service: MusicReferenceHomeService(), request: .discovery(.home))
        page.setAccess(available: true, generation: UUID()); page.appear()
        await page.refresh()
        page.setAccess(available: false, generation: UUID())
        XCTAssertFalse(page.loaded); XCTAssertTrue(page.sections.isEmpty)
        XCTAssertNil(page.nextCursor); XCTAssertNil(page.makeActionIntent())
    }
}
