import XCTest
@testable import MeowMusicFeature

private actor SectionTabBrowser: MusicPublicBrowsing {
    var calls = 0
    func publicPlaylists(platform: MusicPublicBrowsePlatform, query: String, cursor: String?) throws -> MusicPublicBrowseCollectionPage {
        throw MusicServiceError.unsupported
    }
    func publicDetail(_ collection: MusicPublicBrowseCollection, cursor: String?) throws -> MusicPublicBrowseDetail {
        throw MusicServiceError.unsupported
    }
    func publicDiscovery(_ destination: MusicPublicBrowseDestination, cursor: String?) -> MusicPublicBrowsePage {
        calls += 1
        let a = MusicPublicBrowseItem(id: "a", title: "A", content: .unavailable)
        let b = MusicPublicBrowseItem(id: "b", title: "B", content: .unavailable)
        return .init(sections: [.init(id: "section", title: "", items: [a], layout: .playlistRail,
            tabs: [.init(id: "one", title: "One", items: [a]), .init(id: "two", title: "Two", items: [b])], selectedTab: "one")],
                     cursor: "cursor", filters: [.init(id: "channel|", title: "Channel")], selectedFilter: "channel|")
    }
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
        model.selectSectionTab("two", sectionID: "section", intent: try XCTUnwrap(model.makeActionIntent()))
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
                model.selectSectionTab("two", sectionID: "section", intent: intent)
                model.selectSectionTab("one", sectionID: "section", intent: intent)
                XCTAssertEqual(model.sections[0].selectedTab, "two")
            } else if transition == 1 {
                model.disappear(preservingContent: true); model.appear()
                model.selectSectionTab("two", sectionID: "section", intent: intent)
                XCTAssertEqual(model.sections[0].selectedTab, "one")
            } else {
                model.setAccess(available: false, generation: UUID())
                model.selectSectionTab("two", sectionID: "section", intent: intent)
                XCTAssertTrue(model.sections.isEmpty)
            }
            let calls = await service.calls; XCTAssertEqual(calls, 1)
        }
    }
    func testForeignSectionOrTabNeverFallsBackToAnotherChoice() async throws {
        let service = SectionTabBrowser(), model = await loaded(service)
        model.selectSectionTab("two", sectionID: "foreign", intent: try XCTUnwrap(model.makeActionIntent()))
        model.selectSectionTab("unknown", sectionID: "section", intent: try XCTUnwrap(model.makeActionIntent()))
        XCTAssertEqual(model.sections[0].selectedTab, "one"); XCTAssertEqual(model.sections[0].items.map(\.id), ["a"])
    }
}
