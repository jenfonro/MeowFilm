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

    func testRememberedChannelGetsItsOwnFirstPageAfterCatalogueRestoration() async throws {
        let suite = "MusicReferenceHomeStateTests." + UUID().uuidString
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        defer { defaults.removePersistentDomain(forName: suite) }
        MusicChannelSelection(defaults: defaults).selectChannel(chart)
        let navigation = MusicChannelSelection(defaults: defaults)
        let service = MusicReferenceHomeService()
        let access = UUID()
        navigation.restoreChannel(from: [chart])
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
