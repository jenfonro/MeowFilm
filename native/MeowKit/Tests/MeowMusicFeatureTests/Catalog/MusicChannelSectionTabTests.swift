import XCTest
@testable import MeowMusicFeature

private actor SectionTabBrowser: MusicPublicBrowsing {
    var calls = 0
    var tabCalls = 0
    var lazy = false
    var gate: SectionTabGate?
    func deferTab(_ gate: SectionTabGate? = nil) { lazy = true; self.gate = gate }
    func loadSecond() async -> [MusicPublicBrowseItem] {
        tabCalls += 1
        if let gate { await gate.wait() }
        return [.init(id: "b", title: "B", content: .unavailable)]
    }
    func publicPlaylists(platform: MusicPublicBrowsePlatform, query: String, cursor: String?) throws -> MusicPublicBrowseCollectionPage {
        throw MusicServiceError.unsupported
    }
    func publicDetail(_ collection: MusicPublicBrowseCollection, cursor: String?) throws -> MusicPublicBrowseDetail {
        throw MusicServiceError.unsupported
    }
    func publicDiscovery(_ destination: MusicPublicBrowseDestination, cursor: String?) -> MusicPublicBrowsePage {
        calls += 1
        if cursor != nil {
            return .init(sections: [.init(id: "next", title: "", items: [
                .init(id: "next-item", title: "Next", content: .unavailable)
            ])])
        }
        let a = MusicPublicBrowseItem(id: "a", title: "A", content: .unavailable)
        let b = MusicPublicBrowseItem(id: "b", title: "B", content: .unavailable)
        let second = lazy ? MusicPublicBrowseSectionTab(id: "two", title: "Two", items: [],
            loadItems: { await self.loadSecond() }) : .init(id: "two", title: "Two", items: [b])
        return .init(sections: [.init(id: "section", title: "", items: [a], layout: .playlistRail,
            tabs: [.init(id: "one", title: "One", items: [a]), second], selectedTab: "one")],
                     cursor: "cursor", filters: [.init(id: "channel|", title: "Channel")], selectedFilter: "channel|")
    }
}
private actor SectionTabGate {
    let entered = XCTestExpectation(description: "tab display read entered")
    var continuation: CheckedContinuation<Void, Never>?
    func wait() async {
        entered.fulfill()
        await withCheckedContinuation { continuation = $0 }
    }
    func release() { continuation?.resume(); continuation = nil }
}
@MainActor final class MusicChannelSectionTabTests: XCTestCase {
    private func loaded(_ service: SectionTabBrowser) async -> MusicPublicBrowseViewModel {
        let model = MusicPublicBrowseViewModel(service: service, request: .discovery(.channel(
            .init(id: "c", title: "C", kind: .cube, pageCode: "synthetic_page"), tag: "")))
        model.setAccess(available: true, generation: UUID()); model.appear(); await model.refresh()
        return model
    }
    func testSectionTabOnlySelectsReturnedContentWithoutNetworkCursorOrChannelMutation() async throws {
        let service = SectionTabBrowser(), model = await loaded(service)
        let request = model.request, cursor = model.nextCursor, epoch = model.actionPageID
        await model.selectSectionTab("two", sectionID: "section", intent: try XCTUnwrap(model.makeActionIntent()))
        XCTAssertEqual(model.sections[0].items.map(\.id), ["b"]); XCTAssertEqual(model.sections[0].selectedTab, "two")
        XCTAssertEqual(model.request, request); XCTAssertEqual(model.selectedFilter, "channel|")
        XCTAssertEqual(model.nextCursor, cursor); XCTAssertNotEqual(model.actionPageID, epoch)
        let calls = await service.calls; XCTAssertEqual(calls, 1)
    }
    func testOldTabIntentCannotBeReplayedAfterSelectionDepartureOrRevocation() async throws {
        for transition in 0..<3 {
            let service = SectionTabBrowser(), model = await loaded(service)
            let intent = try XCTUnwrap(model.makeActionIntent())
            if transition == 0 {
                await model.selectSectionTab("two", sectionID: "section", intent: intent)
                await model.selectSectionTab("one", sectionID: "section", intent: intent)
                XCTAssertEqual(model.sections[0].selectedTab, "two")
            } else if transition == 1 {
                model.disappear(); model.appear()
                await model.selectSectionTab("two", sectionID: "section", intent: intent)
                XCTAssertTrue(model.sections.isEmpty)
                XCTAssertFalse(model.loaded)
            } else {
                model.setAccess(available: false, generation: UUID())
                await model.selectSectionTab("two", sectionID: "section", intent: intent)
                XCTAssertTrue(model.sections.isEmpty)
            }
            let calls = await service.calls; XCTAssertEqual(calls, 1)
        }
    }
    func testForeignSectionOrTabNeverFallsBackToAnotherChoice() async throws {
        let service = SectionTabBrowser(), model = await loaded(service)
        await model.selectSectionTab("two", sectionID: "foreign", intent: try XCTUnwrap(model.makeActionIntent()))
        await model.selectSectionTab("unknown", sectionID: "section", intent: try XCTUnwrap(model.makeActionIntent()))
        XCTAssertEqual(model.sections[0].selectedTab, "one"); XCTAssertEqual(model.sections[0].items.map(\.id), ["a"])
    }
    func testMissingTabDisplayLoadsOnlyOnSelectionAndReusesItsResult() async throws {
        let service = SectionTabBrowser(); await service.deferTab()
        let model = await loaded(service)
        let before = await service.tabCalls; XCTAssertEqual(before, 0)
        await model.selectSectionTab("two", sectionID: "section", intent: try XCTUnwrap(model.makeActionIntent()))
        XCTAssertEqual(model.sections[0].items.map(\.id), ["b"])
        await model.selectSectionTab("one", sectionID: "section", intent: try XCTUnwrap(model.makeActionIntent()))
        await model.selectSectionTab("two", sectionID: "section", intent: try XCTUnwrap(model.makeActionIntent()))
        let after = await service.tabCalls; XCTAssertEqual(after, 1)
        let calls = await service.calls; XCTAssertEqual(calls, 1)
    }
    func testReturningToLoadedPageRetainsTabSelectionAndCompletedDisplayRead() async throws {
        let service = SectionTabBrowser(); await service.deferTab()
        let model = await loaded(service)
        await model.selectSectionTab("two", sectionID: "section", intent: try XCTUnwrap(model.makeActionIntent()))
        await model.next(); await model.previous()
        XCTAssertEqual(model.sections[0].selectedTab, "two"); XCTAssertEqual(model.sections[0].items.map(\.id), ["b"])
        let calls = await service.calls, tabCalls = await service.tabCalls
        XCTAssertEqual(calls, 2); XCTAssertEqual(tabCalls, 1)
    }
    func testLateTabReadCannotRestoreDepartedPage() async throws {
        let service = SectionTabBrowser(), gate = SectionTabGate()
        await service.deferTab(gate)
        let model = await loaded(service), intent = try XCTUnwrap(model.makeActionIntent())
        let task = Task { await model.selectSectionTab("two", sectionID: "section", intent: intent) }
        await fulfillment(of: [gate.entered], timeout: 3)
        model.disappear()
        await gate.release(); await task.value
        XCTAssertTrue(model.sections.isEmpty); XCTAssertFalse(model.loaded)
    }
}
