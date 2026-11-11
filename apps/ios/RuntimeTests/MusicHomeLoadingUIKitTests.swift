#if os(iOS)
import XCTest
import UIKit
import MeowUI
import MeowPlayback
@testable import MeowMusicFeature

// App-hosted so visibility, covering pages and native attachments use a foreground scene.
@MainActor
final class MusicHomeLoadingUIKitTests: XCTestCase {
    func testTallHomeLoadsAllSectionsWithoutScrollingOrSwipingAndAttachesTail() async throws {
        let first = sections(0..<12) + [rail("artists", count: 12, layout: .artistUpdates)]
        let service = HomeLoadingService(pages: [
            "": .init(sections: first, cursor: "tall", consumed: 13),
            "tall": .init(sections: sections(13..<26), cursor: "tail", consumed: 13),
            "tail": .init(sections: [rail("original-tail")], consumed: 1),
        ])
        try await withHome(service) { page, collection, _ in
            let initialOffset = collection.contentOffset
            await self.wait("all original sections displayed without scrolling") {
                collection.numberOfSections == 27
            }
            collection.layoutIfNeeded()
            XCTAssertEqual(collection.contentOffset.y, initialOffset.y, accuracy: 0.5)
            XCTAssertGreaterThan(collection.contentSize.height, collection.bounds.height * 2)
            XCTAssertEqual(collection.numberOfItems(inSection: 12), 12)
            let reads = await service.reads
            XCTAssertEqual(reads, [nil, "tall", "tail"])
            let unexpectedIO = await service.unexpectedIO
            XCTAssertTrue(unexpectedIO.isEmpty, "完整栏目读取不能展开频道、歌单成员或播放")
            // Only scroll after complete loading, to attach the native tail for visual review.
            collection.scrollToItem(at: IndexPath(item: 0, section: 26), at: .bottom, animated: false)
            collection.layoutIfNeeded()
            await self.wait("original tail card visible") {
                collection.visibleCells.contains { $0.accessibilityIdentifier == "music.browse.item.original-tail-0" }
            }
            let image = UIGraphicsImageRenderer(bounds: page.view.bounds).image { _ in
                page.view.drawHierarchy(in: page.view.bounds, afterScreenUpdates: true)
            }
            let attachment = XCTAttachment(image: image)
            attachment.name = "首页完整加载后的原栏目尾部"
            attachment.lifetime = .keepAlways
            self.add(attachment)
        }
    }

    func testFilteredAndDuplicateBatchesFinishWithoutScrollingAndStopAtRealEnd() async throws {
        let first = sections(0..<12)
        let service = HomeLoadingService(pages: [
            "": .init(sections: first, cursor: "filtered", consumed: 12),
            "filtered": .init(sections: [], cursor: "duplicate", consumed: 2),
            "duplicate": .init(sections: [first[11]], cursor: "tail", consumed: 1),
            "tail": .init(sections: [rail("original-tail")], consumed: 1),
        ])
        try await withHome(service) { page, collection, _ in
            let initialOffset = collection.contentOffset
            await self.wait("filtered and duplicate batches finish without new cells or scrolling") {
                collection.numberOfSections == 13
            }
            XCTAssertEqual(collection.contentOffset.y, initialOffset.y, accuracy: 0.5)
            XCTAssertEqual((0..<12).map { collection.numberOfItems(inSection: $0) }, Array(repeating: 12, count: 12))
            let reads = await service.reads
            XCTAssertEqual(reads, [nil, "filtered", "duplicate", "tail"])
            let unexpected = self.expectation(description: "real terminal response stops the complete read")
            unexpected.isInverted = true
            await service.observeContinuation { unexpected.fulfill() }
            page.applyTheme(); page.view.setNeedsLayout(); page.view.layoutIfNeeded()
            await self.fulfillment(of: [unexpected], timeout: 0.2)
            let finalReads = await service.reads
            XCTAssertEqual(finalReads, reads)
        }
    }

    func testCoveredHomeCompletesAllBatchesAndReturnsWithFullContentWithoutDragging() async throws {
        let entered = expectation(description: "continuation admitted without scrolling")
        let settled = expectation(description: "complete home read settles and subscribes while covered")
        let gate = HomeLoadingGate { entered.fulfill() }
        let service = HomeLoadingService(pages: [
            "": .init(sections: sections(0..<12), cursor: "in-flight", consumed: 12),
            "in-flight": .init(sections: [rail("arrived-while-hidden")], cursor: "tail", consumed: 1),
            "tail": .init(sections: [rail("original-tail")], consumed: 1),
        ], heldCursor: "in-flight", gate: gate)
        await service.observeSubscription { settled.fulfill() }
        addTeardownBlock { await gate.release() }
        try await withHome(service) { page, collection, navigation in
            await self.wait("first batch displayed") { collection.numberOfSections == 12 }
            await self.fulfillment(of: [entered], timeout: 3)
            navigation.pushViewController(PageViewController(themeProvider: page.themeProvider), animated: false)
            await self.wait("home actually covered") { !page.isPageVisible }
            await gate.release()
            await self.fulfillment(of: [settled], timeout: 3)
            let hiddenReads = await service.reads
            XCTAssertEqual(hiddenReads, [nil, "in-flight", "tail"])
            XCTAssertEqual(collection.numberOfSections, 12, "普通遮挡允许完整任务结算，但不更新隐藏页面像素")
            navigation.popViewController(animated: false)
            await self.wait("complete home immediately rendered on return without dragging") {
                page.isPageVisible && collection.numberOfSections == 14
            }
            let reads = await service.reads
            XCTAssertEqual(reads, hiddenReads, "返回不能重新读取已经完整结算的首页")
        }
    }

    func testContinuationErrorRetainsSuccessfulSectionsAndStopsWithoutRetry() async throws {
        let failed = expectation(description: "continuation failed without scrolling")
        let service = HomeLoadingService(pages: [
            "": .init(sections: sections(0..<12), cursor: "failed", consumed: 12),
        ], failedCursor: "failed")
        await service.observeContinuation { failed.fulfill() }
        try await withHome(service) { page, collection, _ in
            await self.fulfillment(of: [failed], timeout: 3)
            await self.wait("ordinary error presented") { page.presentedViewController is UIAlertController }
            await self.wait("successful first batch retained") { collection.numberOfSections == 12 }
            page.dismiss(animated: false)
            await self.wait("home visible after dismissing error") {
                page.isPageVisible && page.presentedViewController == nil
            }
            let unexpected = self.expectation(description: "ordinary error cannot silently retry or become an empty page")
            unexpected.isInverted = true
            await service.observeContinuation { unexpected.fulfill() }
            page.view.setNeedsLayout(); page.view.layoutIfNeeded()
            await self.fulfillment(of: [unexpected], timeout: 0.2)
            let reads = await service.reads
            XCTAssertEqual(reads, [nil, "failed"])
            XCTAssertEqual(collection.numberOfSections, 12)
        }
    }

    private func rail(_ id: String, count: Int = 12,
                      layout: MusicPublicBrowseSection.Layout = .playlistRail) -> MusicPublicBrowseSection {
        .init(id: id, title: id, items: (0..<count).map { index in
            let key = "\(id)-\(index)"
            return .init(id: key, title: key, content: .collection(
                .init(platform: .netease, nativeID: key, title: key)))
        }, layout: layout)
    }

    private func sections(_ range: Range<Int>) -> [MusicPublicBrowseSection] {
        range.map { rail("section-\($0)") }
    }

    private func withHome(_ service: HomeLoadingService,
                          body: (MusicBrowseViewController, UICollectionView, PageNavigationController) async throws -> Void) async throws {
        await wait("real foreground scene available") {
            UIApplication.shared.applicationState == .active &&
                UIApplication.shared.connectedScenes.contains { $0.activationState == .foregroundActive }
        }
        let scene = try XCTUnwrap(UIApplication.shared.connectedScenes.compactMap { $0 as? UIWindowScene }
            .first { $0.activationState == .foregroundActive })
        let previousKeyWindow = scene.windows.first { $0.isKeyWindow }
        let model = MusicViewModel(service: service, playback: PlaybackController())
        let router = HomeLoadingRouter(), appearance = MusicAppearancePreferencesStore()
        defer { withExtendedLifetime(router) {} }
        let page = MusicBrowseViewController(title: "推荐", model: model, browser: service,
            request: .discovery(.home), appearance: appearance, router: router, embedded: true)
        let navigation = PageNavigationController(themeProvider: appearance)
        navigation.setViewControllers([page], animated: false)
        let window = UIWindow(windowScene: scene)
        window.frame = scene.coordinateSpace.bounds
        window.rootViewController = navigation; window.makeKeyAndVisible()
        window.layoutIfNeeded(); navigation.view.layoutIfNeeded()
        defer {
            PageViewController.invalidatePages(in: navigation)
            window.isHidden = true; window.rootViewController = nil
            previousKeyWindow?.makeKey()
        }
        addTeardownBlock { await model.close() }
        let collection = try XCTUnwrap(page.view.subviews.compactMap { $0 as? UICollectionView }.first)
        await wait("home visible through UIKit lifecycle") { page.isPageVisible }
        try await body(page, collection, navigation)
    }

    private func wait(_ description: String, until predicate: @escaping @MainActor () -> Bool) async {
        let ready = expectation(for: NSPredicate { _, _ in MainActor.assumeIsolated { predicate() } },
                                evaluatedWith: nil)
        ready.expectationDescription = description
        await fulfillment(of: [ready], timeout: 3)
    }
}

private actor HomeLoadingGate {
    private let entered: @Sendable () -> Void
    private var released = false
    private var waiter: CheckedContinuation<Void, Never>?
    init(_ entered: @escaping @Sendable () -> Void) { self.entered = entered }
    func wait() async {
        entered()
        if !released { await withCheckedContinuation { waiter = $0 } }
    }
    func release() { released = true; waiter?.resume(); waiter = nil }
}

private actor HomeLoadingService: MusicServing, MusicHomeBrowsing {
    private let pages: [String: MusicPublicBrowsePage]
    private let heldCursor: String?
    private let gate: HomeLoadingGate?
    private let failedCursor: String?
    private var continuationObserver: (@Sendable () -> Void)?
    private var subscriptionObserver: (@Sendable () -> Void)?
    private(set) var unexpectedIO: [String] = []
    private(set) var reads: [String?] = []
    init(pages: [String: MusicPublicBrowsePage], heldCursor: String? = nil,
         gate: HomeLoadingGate? = nil, failedCursor: String? = nil) {
        self.pages = pages; self.heldCursor = heldCursor; self.gate = gate; self.failedCursor = failedCursor
    }
    func observeContinuation(_ observer: (@Sendable () -> Void)?) { continuationObserver = observer }
    func homeSourceSelection() -> MusicHomeSourceSelection {
        .init(selected: .netease, revision: 0, available: [.netease])
    }
    func selectHomeSource(_ source: MusicPublicBrowsePlatform, ifRevision: Int) throws -> MusicHomeSourceSelection {
        throw MusicServiceError.unsupported
    }
    func restoredHome() -> MusicPublicBrowsePage? { nil }
    func observeSubscription(_ observer: @escaping @Sendable () -> Void) { subscriptionObserver = observer }
    func homeChanges() -> AsyncStream<Void> {
        subscriptionObserver?(); subscriptionObserver = nil
        return AsyncStream { _ in }
    }
    func homePage(cursor: String?, refresh: Bool) async throws -> MusicPublicBrowsePage {
        reads.append(cursor)
        if cursor != nil { continuationObserver?() }
        if cursor == heldCursor, let gate { await gate.wait() }
        if let failedCursor, cursor == failedCursor { throw MusicServiceError.unavailable }
        guard let page = pages[cursor ?? ""] else {
            XCTFail("Unexpected continuation \(cursor ?? "first")")
            throw MusicServiceError.invalidRequest
        }
        return page
    }
    func publicDiscovery(_ destination: MusicPublicBrowseDestination, cursor: String?) throws -> MusicPublicBrowsePage {
        unexpectedIO.append("discovery"); XCTFail("Home must use its existing lifecycle port"); throw MusicServiceError.unsupported
    }
    func publicPlaylists(platform: MusicPublicBrowsePlatform, query: String, cursor: String?) throws -> MusicPublicBrowseCollectionPage {
        unexpectedIO.append("playlists"); XCTFail("Home loading cannot expand playlists"); throw MusicServiceError.unsupported
    }
    func publicDetail(_ collection: MusicPublicBrowseCollection, cursor: String?) throws -> MusicPublicBrowseDetail {
        unexpectedIO.append("detail"); XCTFail("Home loading cannot read collection members"); throw MusicServiceError.unsupported
    }
    func state() -> MusicPlaybackState { .init() }
    func platforms() -> [MusicPlatform] { [] }
    func search(platform: String, query: String, page: Int, limit: Int) throws -> MusicSearchPage {
        unexpectedIO.append("search"); XCTFail("Home loading cannot search playback sources"); throw MusicServiceError.unsupported
    }
    func collections() -> [MusicCollection] { [] }
    func tracks(in collection: MusicCollection) throws -> [MusicRemoteTrack] { throw MusicServiceError.unsupported }
    func createPlaylist(name: String, refs: [MusicReference]) throws -> MusicCollection { throw MusicServiceError.unsupported }
    func editPlaylist(_ collection: MusicCollection, name: String?, refs: [MusicReference]?, order: [String]?) throws -> MusicCollection {
        throw MusicServiceError.unsupported
    }
    func removePlaylist(_ collection: MusicCollection) throws { throw MusicServiceError.unsupported }
    func queue() -> MusicQueueSnapshot { .init(revision: 0, currentId: nil, entries: []) }
    func changeQueue(_ change: MusicQueueChange, expectedRevision: Int) throws -> MusicPlaybackState { throw MusicServiceError.unsupported }
    func advance(_ direction: MusicDirection, reason: MusicAdvanceReason, expectedRevision: Int) throws -> MusicAdvanceResult {
        throw MusicServiceError.unsupported
    }
    func updateSettings(_ change: MusicSettingsChange) throws -> MusicPlaybackState { throw MusicServiceError.unsupported }
    func sourceAvailability(for ref: MusicReference) throws -> MusicSourceAvailability { throw MusicServiceError.unsupported }
    func metadataPreferences() throws -> MusicMetadataPreferences { throw MusicServiceError.unsupported }
    func updateMetadataPreferences(_ change: MusicMetadataPreferencesChange) throws -> MusicMetadataPreferences {
        throw MusicServiceError.unsupported
    }
    func metadataCandidates(occurrenceID: String) throws -> MusicMetadataCandidates { throw MusicServiceError.unsupported }
    func resolve(attemptId: String, occurrenceId: String, selection: MusicPlaybackSelection) throws -> MusicPlaybackCandidate {
        unexpectedIO.append("playback"); XCTFail("Home loading cannot resolve playback"); throw MusicServiceError.unsupported
    }
    func cancel(attemptId: String?) {}
    func record(_ fact: MusicPlaybackFact) -> Bool { false }
    func lyrics(occurrenceId: String, sourceKey: String) throws -> MusicLyricsSnapshot { throw MusicServiceError.unsupported }
    func playedSongs() -> [MusicPlayedSong] { [] }
    func playedPlaylists() -> [MusicPlayedPlaylist] { [] }
    func restoreHistory(id: String, expectedRevision: Int) throws -> MusicPlaybackState { throw MusicServiceError.unsupported }
}

@MainActor
private final class HomeLoadingRouter: MusicUIRouting {
    let primaryPlaybackPresentation = MusicMiniPlayerPresentation()
    func showSearch() { XCTFail("Home loading cannot navigate") }
    func showSettings() { XCTFail("Home loading cannot navigate") }
    func showPublicBrowse(_ request: MusicPublicBrowseRequest, title: String) { XCTFail("Home loading cannot navigate") }
    func showPublicCollection(_ collection: MusicPublicBrowseCollection) { XCTFail("Home loading cannot navigate") }
    func showLocalCollection(_ collection: MusicCollection) { XCTFail("Home loading cannot navigate") }
    func showPlayer(_ destination: MusicPlaybackDestination) { XCTFail("Home loading cannot navigate") }
    func selectChannel(_ channel: MusicPublicBrowseChannel) { XCTFail("Home loading cannot expand channels") }
}
#endif
