import XCTest
@testable import MeowMusicFeature

private actor FeedGate {
    private var started = false, released = false
    private var starts: [CheckedContinuation<Void, Never>] = []
    private var ends: [CheckedContinuation<Void, Never>] = []
    func wait() async {
        started = true; starts.forEach { $0.resume() }; starts = []
        if !released { await withCheckedContinuation { ends.append($0) } }
    }
    func didStart() async { if !started { await withCheckedContinuation { starts.append($0) } } }
    func release() { released = true; ends.forEach { $0.resume() }; ends = [] }
}

private actor ChannelFeedService: MusicPublicBrowsing {
    static let channel = MusicPublicBrowseChannel(id: "synthetic", title: "Synthetic", kind: .cube, pageCode: "synthetic_page")
    static let tabs: [MusicPublicBrowseFilter] = [.init(id: "a|", title: "A"), .init(id: "b|", title: "B")]
    static let choices: [MusicPublicBrowseFilter] = [
        .init(id: "a|", title: "全部歌曲"), .init(id: "a|4", title: "Four", group: "Style")
    ]
    var failure: MusicServiceError?
    var sectionFailure: MusicServiceError?
    var gate: FeedGate?
    var duplicate = false
    var calls: [(String, String?)] = []
    func configure(failure: MusicServiceError? = nil, sectionFailure: MusicServiceError? = nil,
                   gate: FeedGate? = nil, duplicate: Bool = false) {
        self.failure = failure; self.sectionFailure = sectionFailure; self.gate = gate; self.duplicate = duplicate
    }
    func publicPlaylists(platform: MusicPublicBrowsePlatform, query: String, cursor: String?) throws -> MusicPublicBrowseCollectionPage {
        throw MusicServiceError.unsupported
    }
    func publicDetail(_ collection: MusicPublicBrowseCollection, cursor: String?) throws -> MusicPublicBrowseDetail {
        throw MusicServiceError.unsupported
    }
    func publicDiscovery(_ destination: MusicPublicBrowseDestination, cursor: String?) async throws -> MusicPublicBrowsePage {
        guard case .channel(_, let tag) = destination else { throw MusicServiceError.unsupported }
        calls.append((tag, cursor))
        if let gate { await gate.wait() }
        if let failure { throw failure }
        let index = cursor == nil || duplicate ? "1" : cursor!.hasSuffix(":1") ? "2" : "3"
        let id = tag + index
        let track = MusicRemoteTrack(ref: .init(kind: .handle, id: id), songId: id, title: id,
                                    sources: [.init(sourceKey: id, platform: "wy", nativeId: index)])
        let items: [MusicPublicBrowseItem] = sectionFailure == nil ?
            [.init(id: id, title: id, content: .track(track))] : []
        let feed = MusicPublicBrowseSection(id: "feed", title: "Feed", items: items,
            filters: Self.choices, selectedFilter: tag, error: sectionFailure)
        let other = MusicPublicBrowseSection(id: "cards", title: "Cards", items: [
            .init(id: "card", title: "Card", content: .unavailable)
        ], layout: .playlistRail)
        return .init(sections: cursor == nil ? [other, feed] : [feed],
                     cursor: sectionFailure == nil ? tag + ":" + (cursor == nil ? "1" : cursor!.hasSuffix(":1") ? "2" : "3") : nil,
                     filters: Self.tabs, selectedFilter: tag.hasPrefix("b") ? "b|" : "a|")
    }
}

@MainActor final class MusicChannelFeedStateTests: XCTestCase {
    private func loaded(_ service: ChannelFeedService) async -> MusicPublicBrowseViewModel {
        let model = MusicPublicBrowseViewModel(service: service, request: .discovery(.channel(ChannelFeedService.channel, tag: "a|")))
        model.setAccess(available: true, generation: UUID()); model.appear(); await model.refresh()
        return model
    }
    private func feed(_ model: MusicPublicBrowseViewModel) -> MusicPublicBrowseSection? {
        model.sections.first { $0.id == "feed" }
    }
    func testSectionSelectionCommitsTargetAndResetsCursorOnlyAfterSuccessfulRead() async throws {
        let service = ChannelFeedService(), model = await loaded(service), gate = FeedGate()
        let oldRequest = model.request, oldCursor = model.nextCursor
        let intent = try XCTUnwrap(model.makeActionIntent())
        await service.configure(gate: gate)
        let task = Task { await model.selectSectionFilter("a|4", sectionID: "feed", intent: intent) }
        await gate.didStart()
        XCTAssertEqual(model.request, oldRequest); XCTAssertEqual(model.nextCursor, oldCursor)
        XCTAssertEqual(feed(model)?.selectedFilter, "a|"); XCTAssertFalse(model.sections.isEmpty)
        await gate.release(); await task.value
        XCTAssertEqual(model.request, .discovery(.channel(ChannelFeedService.channel, tag: "a|4")))
        XCTAssertEqual(feed(model)?.selectedFilter, "a|4"); XCTAssertEqual(model.nextCursor, "a|4:1")
        XCTAssertEqual(model.selectedFilter, "a|"); XCTAssertEqual(model.filters, ChannelFeedService.tabs)
        XCTAssertEqual(model.sections.map(\.id), ["cards", "feed"]); XCTAssertEqual(model.pageNumber, 1)
    }
    func testFailedCandidateRetainsSongsCommittedSelectionAndCursorThenRetriesExactTarget() async throws {
        let service = ChannelFeedService(), model = await loaded(service)
        let oldRequest = model.request, oldCursor = model.nextCursor, oldRows = model.tracks
        let intent = try XCTUnwrap(model.makeActionIntent())
        await service.configure(sectionFailure: .upstreamFailed)
        await model.selectSectionFilter("a|4", sectionID: "feed", intent: intent)
        XCTAssertEqual(model.request, oldRequest); XCTAssertEqual(model.nextCursor, oldCursor)
        XCTAssertEqual(model.tracks, oldRows); XCTAssertEqual(feed(model)?.selectedFilter, "a|")
        XCTAssertEqual(feed(model)?.error, .upstreamFailed); XCTAssertNil(model.error)
        await model.appendNext()
        let held = await service.calls; XCTAssertEqual(held.count, 2)
        await service.configure(); await model.retry()
        XCTAssertEqual(feed(model)?.selectedFilter, "a|4"); XCTAssertNil(feed(model)?.error)
        let calls = await service.calls
        XCTAssertEqual(calls.map(\.0), ["a|", "a|4", "a|4"]); XCTAssertTrue(calls.allSatisfy { $0.1 == nil })
    }
    func testTopLevelReadFailureAlsoRetainsCandidateForExplicitRetry() async throws {
        let service = ChannelFeedService(), model = await loaded(service)
        await service.configure(failure: .rateLimited)
        let intent = try XCTUnwrap(model.makeActionIntent())
        await model.selectSectionFilter("a|4", sectionID: "feed", intent: intent)
        XCTAssertEqual(model.error, .rateLimited); XCTAssertEqual(feed(model)?.selectedFilter, "a|")
        XCTAssertEqual(model.nextCursor, "a|:1")
        await service.configure(); await model.retry()
        XCTAssertNil(model.error); XCTAssertEqual(feed(model)?.selectedFilter, "a|4")
        XCTAssertEqual(model.nextCursor, "a|4:1")
    }
    func testOldSheetSelectionCannotActAfterRefreshDepartureOrAccessReplacement() async throws {
        for action in 0..<3 {
            let service = ChannelFeedService(), model = await loaded(service)
            let intent = try XCTUnwrap(model.makeActionIntent())
            if action == 0 { await model.refresh() }
            else if action == 1 { model.disappear(preservingContent: true); model.appear() }
            else { model.setAccess(available: true, generation: UUID()) }
            let before = await service.calls.count
            await model.selectSectionFilter("a|4", sectionID: "feed", intent: intent)
            let after = await service.calls.count; XCTAssertEqual(before, after)
            XCTAssertEqual(model.request, .discovery(.channel(ChannelFeedService.channel, tag: "a|")))
        }
    }
    func testCancelledFilterReadCannotPublishNewSelectionOrConsumeOldCursor() async throws {
        let service = ChannelFeedService(), model = await loaded(service), gate = FeedGate()
        let intent = try XCTUnwrap(model.makeActionIntent())
        await service.configure(gate: gate)
        let task = Task { await model.selectSectionFilter("a|4", sectionID: "feed", intent: intent) }
        await gate.didStart(); task.cancel(); await gate.release(); await task.value
        XCTAssertEqual(feed(model)?.selectedFilter, "a|"); XCTAssertEqual(model.nextCursor, "a|:1")
        XCTAssertEqual(model.request, .discovery(.channel(ChannelFeedService.channel, tag: "a|")))
        XCTAssertFalse(model.isLoading); XCTAssertNil(model.error)
    }
    func testRefreshAndChangingTopGroupRetainPixelsButFenceOldContentActions() async throws {
        let service = ChannelFeedService(), model = await loaded(service), refreshGate = FeedGate()
        await service.configure(gate: refreshGate)
        let refresh = Task { await model.refresh() }; await refreshGate.didStart()
        XCTAssertEqual(model.sections.map(\.id), ["cards", "feed"])
        await refreshGate.release(); await refresh.value
        let groupGate = FeedGate(); await service.configure(gate: groupGate)
        let group = Task { await model.selectFilter("b|") }; await groupGate.didStart()
        XCTAssertEqual(model.sections.map(\.id), ["cards", "feed"])
        XCTAssertFalse(model.filters.isEmpty)
        XCTAssertEqual(model.selectedFilter, "b|")
        XCTAssertFalse(model.contentIsCurrent); XCTAssertNil(model.makeActionIntent())
        XCTAssertNil(model.nextCursor)
        await groupGate.release(); await group.value
        XCTAssertEqual(model.selectedFilter, "b|")
        XCTAssertTrue(model.contentIsCurrent); XCTAssertNotNil(model.makeActionIntent())
    }
    func testAppendKeepsOneFeedAndRejectsDuplicateNonterminalPageWithoutLosingCursor() async throws {
        let service = ChannelFeedService(), model = await loaded(service)
        await model.appendNext()
        XCTAssertEqual(model.sections.map(\.id), ["cards", "feed"]); XCTAssertEqual(model.tracks.count, 2)
        XCTAssertEqual(feed(model)?.filters, ChannelFeedService.choices)
        let cursor = model.nextCursor
        await service.configure(duplicate: true); await model.appendNext()
        XCTAssertEqual(model.error, .invalidResponse); XCTAssertEqual(model.nextCursor, cursor)
        XCTAssertEqual(model.tracks.count, 2)
        await service.configure(); await model.retry()
        XCTAssertNil(model.error); XCTAssertEqual(model.tracks.count, 3)
        XCTAssertEqual(model.sections.filter { $0.id == "feed" }.count, 1)
    }
    func testRevokedFilterRequestDoesNotRepopulateRetainedRows() async throws {
        let service = ChannelFeedService(), model = await loaded(service), gate = FeedGate()
        let intent = try XCTUnwrap(model.makeActionIntent())
        await service.configure(gate: gate)
        let task = Task { await model.selectSectionFilter("a|4", sectionID: "feed", intent: intent) }
        await gate.didStart(); model.setAccess(available: false, generation: UUID())
        await gate.release(); await task.value
        XCTAssertTrue(model.sections.isEmpty); XCTAssertNil(model.nextCursor)
        XCTAssertFalse(model.loaded); XCTAssertFalse(model.isLoading); XCTAssertNil(model.error)
    }
}
