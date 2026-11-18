import Foundation
import XCTest
import Combine
@testable import MeowMusicFeature

private actor BrowseArtworkGate {
    private var started = false, released = false
    private var starts: [CheckedContinuation<Void, Never>] = []
    private var finishes: [CheckedContinuation<Void, Never>] = []
    func wait() async {
        started = true; starts.forEach { $0.resume() }; starts = []
        if !released { await withCheckedContinuation { finishes.append($0) } }
    }
    func didStart() async { if !started { await withCheckedContinuation { starts.append($0) } } }
    func release() { released = true; finishes.forEach { $0.resume() }; finishes = [] }
}

private actor BrowseArtworkService: MusicPublicBrowsing, MusicPublicPlaylistSummaryReading {
    var pages: [String: MusicPublicBrowsePage]
    var summaries: [MusicPublicBrowseCollection] = []
    var failure: MusicServiceError?
    var gate: BrowseArtworkGate?
    var requests: [[String]] = []
    init(_ page: MusicPublicBrowsePage) { pages = ["": page] }
    func configure(summaries: [MusicPublicBrowseCollection] = [], failure: MusicServiceError? = nil,
                   gate: BrowseArtworkGate? = nil) {
        self.summaries = summaries; self.failure = failure; self.gate = gate
    }
    func replace(_ page: MusicPublicBrowsePage, cursor: String = "") { pages[cursor] = page }
    func publicDiscovery(_ destination: MusicPublicBrowseDestination, cursor: String?) -> MusicPublicBrowsePage {
        pages[cursor ?? ""] ?? .init(sections: [])
    }
    func publicPlaylistSummaries(platform: MusicPublicBrowsePlatform, ids: [String]) async throws -> [MusicPublicBrowseCollection] {
        requests.append(ids)
        let result = summaries, error = failure
        if let gate { await gate.wait() }
        if let error { throw error }
        return result
    }
    func publicDetail(_ collection: MusicPublicBrowseCollection, cursor: String?) throws -> MusicPublicBrowseDetail {
        throw MusicServiceError.unsupported
    }
    func publicPlaylists(platform: MusicPublicBrowsePlatform, query: String, cursor: String?) throws -> MusicPublicBrowseCollectionPage {
        throw MusicServiceError.unsupported
    }
}

@MainActor final class MusicPublicBrowseArtworkTests: XCTestCase {
    private let cover = URL(string: "https://images.invalid/summary.jpg")!
    private func item(_ id: String, key: String? = nil, artwork: URL? = nil, root: URL? = nil) -> MusicPublicBrowseItem {
        .init(id: key ?? id, title: "Card " + id, subtitle: "Subtitle", artwork: artwork,
            content: .collection(.init(platform: .netease, nativeID: id, title: "Root " + id, artwork: root)),
            kind: .playlist, presentation: "preview", coverRef: .init(platform: .netease, id: id))
    }
    private func page(_ items: [MusicPublicBrowseItem], cursor: String? = nil,
                      tabs: [MusicPublicBrowseSectionTab] = []) -> MusicPublicBrowsePage {
        .init(sections: [.init(id: "s", title: "Section", items: items, layout: .featureRail,
            tabs: tabs, selectedTab: tabs.first?.id ?? "")], cursor: cursor)
    }
    private func model(_ service: BrowseArtworkService) async -> MusicPublicBrowseViewModel {
        let model = MusicPublicBrowseViewModel(service: service,
            request: .discovery(.channel(.init(id: "channel", title: "", kind: .cube), tag: "")))
        model.setAccess(available: true, generation: UUID()); model.appear()
        await model.loadIfNeeded()
        return model
    }
    private func settled(_ model: MusicPublicBrowseViewModel, id: String) async {
        let done = expectation(for: NSPredicate { _, _ in
            MainActor.assumeIsolated {
                switch model.artworkReads[.init(platform: .netease, id: id)] {
                case .settled?, .failed?: return true
                default: return false
                }
            }
        }, evaluatedWith: model)
        await fulfillment(of: [done], timeout: 2)
    }

    func testVisibleDemandDeduplicatesAndFillsOnlyEmptyCoversAcrossTabs() async throws {
        let existing = URL(string: "https://images.invalid/current.jpg")!
        let a = item("a"), duplicate = item("a", key: "duplicate"), filled = item("a", key: "filled", artwork: existing)
        let b = item("b")
        let service = BrowseArtworkService(page([a, duplicate, filled, b], tabs: [
            .init(id: "one", title: "One", items: [a, duplicate, filled, b]),
            .init(id: "two", title: "Two", items: [item("a", key: "tab")])
        ]))
        await service.configure(summaries: [
            .init(platform: .netease, nativeID: "b", title: "Ignored title", artwork: cover),
            .init(platform: .netease, nativeID: "extra", title: "", artwork: cover),
            .init(platform: .netease, nativeID: "a", title: "Ignored title", artwork: cover)
        ])
        let model = await model(service)
        let initial = await service.requests; XCTAssertTrue(initial.isEmpty)
        model.setVisibleArtworkItems([a, duplicate, filled, b])
        model.setVisibleArtworkItems([b, a, duplicate])
        await settled(model, id: "a")
        XCTAssertEqual(model.sections[0].items.map(\.artwork), [cover, cover, existing, cover])
        XCTAssertEqual(model.sections[0].items.map(\.title), ["Card a", "Card a", "Card a", "Card b"])
        XCTAssertEqual(model.sections[0].tabs[1].items[0].artwork, cover)
        let requests = await service.requests; XCTAssertEqual(requests, [["a", "b"]])
        model.disappear()
    }

    func testHiddenPageSettlesAdmittedBatchWithoutPublishingOrDispatchingRemainingDemand() async throws {
        let items = (0..<31).map { item(String(format: "%02d", $0)) }
        let service = BrowseArtworkService(page(items)), gate = BrowseArtworkGate()
        await service.configure(summaries: items.map {
            .init(platform: .netease, nativeID: $0.id, title: "", artwork: cover)
        }, gate: gate)
        let model = await model(service)
        model.setVisibleArtworkItems(items)
        await gate.didStart()
        model.setDisplayVisible(false)
        await gate.release()
        await settled(model, id: "00")
        XCTAssertTrue(model.sections[0].items.allSatisfy { $0.artwork == nil })
        let hidden = await service.requests; XCTAssertEqual(hidden.count, 1); XCTAssertEqual(hidden[0].count, 30)
        model.setDisplayVisible(true)
        XCTAssertEqual(model.sections[0].items[0].artwork, cover)
        model.setVisibleArtworkItems(model.sections[0].items)
        await settled(model, id: "30")
        let resumed = await service.requests; XCTAssertEqual(resumed.count, 2); XCTAssertEqual(resumed[1], ["30"])
        model.disappear()
    }

    func testEmptyAndFailedSummariesAreFiniteTerminalStatesOnReentry() async throws {
        for failure in [nil, MusicServiceError.connectionFailed] {
            let a = item("a"), service = BrowseArtworkService(page([item("a")]))
            await service.configure(failure: failure)
            let model = await model(service)
            model.setVisibleArtworkItems([a]); await settled(model, id: "a")
            for _ in 0..<3 {
                model.setVisibleArtworkItems([]); model.setDisplayVisible(false)
                model.setDisplayVisible(true); model.setVisibleArtworkItems([a])
            }
            let requests = await service.requests; XCTAssertEqual(requests, [["a"]])
            XCTAssertNil(model.sections[0].items[0].artwork); XCTAssertNil(model.error)
            if failure == nil {
                guard case .settled(nil)? = model.artworkReads[a.coverRef!] else { return XCTFail("successful no-cover") }
            } else {
                guard case .failed(.connectionFailed)? = model.artworkReads[a.coverRef!] else { return XCTFail("failure terminal") }
            }
            model.disappear()
        }
    }

    func testContentReplacementAndAccountChangeRetireLateResultsEvenWithSameCardID() async throws {
        for changeAccount in [false, true] {
            let a = item("a"), service = BrowseArtworkService(page([item("a")])), gate = BrowseArtworkGate()
            await service.configure(summaries: [.init(platform: .netease, nativeID: "a", title: "", artwork: cover)], gate: gate)
            let model = await model(service)
            model.setVisibleArtworkItems([a]); await gate.didStart()
            if changeAccount { _ = model.invalidateAccount(.netease) }
            else { await model.refresh() }
            await service.configure()
            model.setVisibleArtworkItems(model.sections[0].items)
            await settled(model, id: "a")
            let stalePublication = expectation(description: "retired cover cannot publish")
            stalePublication.isInverted = true
            let subscription = model.$sections.dropFirst().sink { sections in
                if sections.first?.items.first?.artwork != nil { stalePublication.fulfill() }
            }
            await gate.release()
            await fulfillment(of: [stalePublication], timeout: 0.1)
            subscription.cancel()
            // The retired task can complete after the replacement has reached its own terminal state.
            let requests = await service.requests; XCTAssertEqual(requests, [["a"], ["a"]])
            XCTAssertNil(model.sections[0].items[0].artwork)
            model.disappear()
        }
    }

    func testKnownCollectionRootCoverAndCachedPagesReuseTheSameSnapshot() async throws {
        let a = item("a", root: cover), b = item("b")
        let service = BrowseArtworkService(page([a, b], cursor: "next"))
        await service.replace(page([item("b", key: "next-b")]), cursor: "next")
        await service.configure(summaries: [.init(platform: .netease, nativeID: "b", title: "", artwork: cover)])
        let model = await model(service)
        model.setVisibleArtworkItems([a, b]); await settled(model, id: "b")
        XCTAssertEqual(model.sections[0].items[0].artwork, cover)
        await model.next()
        XCTAssertEqual(model.sections[0].items[0].artwork, cover)
        await model.previous()
        XCTAssertEqual(model.sections[0].items.map(\.artwork), [cover, cover])
        model.setVisibleArtworkItems(model.sections[0].items)
        let requests = await service.requests; XCTAssertEqual(requests, [["b"]])
        model.disappear()
    }

    func testRepositoryUpdatesSongFactsWithoutOverwritingCanonicalCardPresentation() {
        let track = MusicRemoteTrack(ref: .init(kind: .library, id: "song"), songId: "song", title: "Song")
        let card = MusicPublicBrowseItem(id: "banner", title: "Banner", artwork: cover, content: .track(track), kind: .song)
        let replacement = MusicRemoteTrack(ref: track.ref, songId: "song", title: "Changed song",
            artwork: URL(string: "https://images.invalid/song.jpg"))
        let projected = card.projected(["song": replacement], layout: .banner)
        XCTAssertEqual(projected.title, "Banner"); XCTAssertEqual(projected.artwork, cover)
        guard case .track(let current) = projected.content else { return XCTFail("track") }
        XCTAssertEqual(current.title, replacement.title); XCTAssertEqual(current.artwork, replacement.artwork)
    }
}
