#if os(iOS)
import XCTest
import UIKit
import SwiftUI
import Combine
import MeowUI
import MeowContracts
import MeowPlayback
@testable import MeowMusicFeature

@MainActor
final class MusicUIKitRegressionTests: XCTestCase {
    func testHiddenHomeRevokesRowsAndDefersRefillAndFMPreviewUntilReturn() async throws {
        let action = MusicHomeAction.fm(mode: .default, submode: "", entry: nil)
        func home(_ id: String) -> MusicPublicBrowsePage {
            .init(sections: [.init(id: "home", title: "", items: [
                .init(id: id, title: "私人 FM", content: .action(action)),
            ], layout: .featureRail)])
        }
        let service = MusicUIKitRegressionService(browse: home("initial"))
        let model = MusicViewModel(service: service, player: MusicUIKitRegressionPlayer())
        let appearance = MusicAppearancePreferencesStore(), router = MusicUIKitRegressionRouter()
        let controller = MusicBrowseViewController(title: "推荐", model: model, browser: service,
            request: .discovery(.home), appearance: appearance, router: router, embedded: true)
        let navigation = PageNavigationController(themeProvider: appearance)
        navigation.setViewControllers([controller], animated: false)
        let window = mount(navigation)
        defer { release(navigation, window: window) }
        let collection = try XCTUnwrap(controller.view.subviews.compactMap { $0 as? UICollectionView }.first)
        let preview = expectation(description: "initial visible FM preview")
        await service.observeFMPreviews(after: 0) { preview.fulfill() }
        await fulfillment(of: [preview], timeout: 3)
        await waitForHomeSubscription(service)
        await waitForResult { collection.layoutIfNeeded(); return collection.visibleCells.count == 1 }
        await waitForResult { controller.canPresentPageAlert && navigation.transitionCoordinator == nil }
        let cover = PageViewController(themeProvider: appearance)
        navigation.pushViewController(cover, animated: false)
        navigation.view.layoutIfNeeded()
        await waitForResult { !controller.isPageVisible }
        XCTAssertTrue(navigation.topViewController === cover)
        XCTAssertFalse(controller.isPageVisible,
            "active=\(controller.isPageActive), coverWindow=\(cover.viewIfLoaded?.window != nil), transition=\(navigation.transitionCoordinator != nil)")
        XCTAssertTrue(controller.isPageActive)
        model.accountSessionsChanged(.netease)
        // Home scope invalidation is delivered by the native home stream;
        // the account UI echo only invalidates account-bound detail requests.
        await service.updateBrowse(home("replacement"))
        await waitForResult { collection.numberOfSections == 0 }
        let afterAccount = await service.counts(), previewCalls = await service.fmPreviewCount
        XCTAssertEqual(afterAccount.browse, 1); XCTAssertEqual(previewCalls, 1)
        await deliverMainRunLoop()
        let whileHidden = await service.counts()
        XCTAssertEqual(whileHidden.browse, 1)
        let returnedPreview = expectation(description: "FM preview only after returning")
        await service.observeFMPreviews(after: 1) { returnedPreview.fulfill() }
        XCTAssertTrue(navigation.popViewController(animated: false) === cover)
        navigation.view.layoutIfNeeded()
        await fulfillment(of: [returnedPreview], timeout: 3)
        await waitForResult {
            collection.layoutIfNeeded()
            return collection.visibleCells.contains { $0.accessibilityIdentifier == "music.browse.item.replacement" }
        }
        let returned = await service.counts(), returnedPreviews = await service.fmPreviewCount
        XCTAssertEqual(returned.browse, 2); XCTAssertEqual(returnedPreviews, 2)
        await model.close()
    }

    func testCoveredHomeFinishesAdmittedReadWithoutRenderingOrReadingAgainOnReturn() async throws {
        let entered = expectation(description: "home read entered")
        let gate = MusicUIKitSearchGate { entered.fulfill() }
        let service = MusicUIKitRegressionService(browse: .init(sections: [
            .init(id: "cards", title: "", items: [card("retained", previews: [])], layout: .playlistRail),
        ]))
        await service.holdHome(gate)
        let model = MusicViewModel(service: service, player: MusicUIKitRegressionPlayer())
        let appearance = MusicAppearancePreferencesStore(), router = MusicUIKitRegressionRouter()
        let page = MusicBrowseViewController(title: "推荐", model: model, browser: service,
            request: .discovery(.home), appearance: appearance, router: router, embedded: true)
        let navigation = PageNavigationController(themeProvider: appearance)
        navigation.setViewControllers([page], animated: false)
        let window = mount(navigation)
        defer { release(navigation, window: window) }
        await fulfillment(of: [entered], timeout: 3)
        let collection = try XCTUnwrap(page.view.subviews.compactMap { $0 as? UICollectionView }.first)
        await waitForResult { collection.backgroundView is MusicLoadingSkeletonView }
        let skeleton = try XCTUnwrap(collection.backgroundView as? MusicLoadingSkeletonView)
        XCTAssertFalse(skeleton.isHidden)
        await waitForResult { page.canPresentPageAlert && navigation.transitionCoordinator == nil }
        navigation.pushViewController(PageViewController(themeProvider: appearance), animated: false)
        navigation.view.layoutIfNeeded()
        await waitForResult { !page.isPageVisible }
        XCTAssertFalse(hasSkeletonAnimation(skeleton.layer))
        await gate.release()
        // Subscription follows the actual read's settlement, not an arbitrary delay.
        await waitForHomeSubscription(service)
        await deliverMainRunLoop()
        XCTAssertEqual(collection.numberOfSections, 0)
        navigation.popViewController(animated: false)
        navigation.view.layoutIfNeeded()
        await waitForResult { collection.layoutIfNeeded(); return collection.visibleCells.count == 1 }
        XCTAssertFalse(collection.backgroundView is MusicLoadingSkeletonView)
        let calls = await service.counts()
        XCTAssertEqual(calls.browse, 1)
        let refreshing = expectation(description: "refresh with existing content entered")
        let refreshGate = MusicUIKitSearchGate { refreshing.fulfill() }
        await service.holdHome(refreshGate)
        collection.refreshControl?.sendActions(for: .valueChanged)
        await fulfillment(of: [refreshing], timeout: 3)
        XCTAssertEqual(collection.visibleCells.count, 1)
        XCTAssertFalse(collection.backgroundView is MusicLoadingSkeletonView)
        await refreshGate.release()
        await model.close()
    }

    func testChannelCatalogueAndFirstChannelUseSkeletonWithoutObscuringLoadedContent() async throws {
        let key = "music.ui.lastChannel", saved = UserDefaults.standard.object(forKey: key)
        UserDefaults.standard.removeObject(forKey: key)
        defer { UserDefaults.standard.set(saved, forKey: key) }
        let first = MusicPublicBrowseChannel(id: "playlist", title: "歌单", kind: .playlists)
        let chart = MusicPublicBrowseChannel(id: "chart", title: "排行榜", kind: .rankings)
        let service = MusicUIKitRegressionService(browse: .init(sections: [
            .init(id: "directory", title: "", items: [first, chart].map {
                .init(id: $0.identity, title: $0.title, content: .channel($0))
            }, layout: .playlistRail),
        ]), channelBrowse: .init(sections: [
            .init(id: "content", title: "", items: [card("loaded", previews: [])], layout: .playlistRail),
        ]))
        let directoryEntered = expectation(description: "directory entered")
        let channelEntered = expectation(description: "first channel entered")
        let directoryGate = MusicUIKitSearchGate { directoryEntered.fulfill() }
        let channelGate = MusicUIKitSearchGate { channelEntered.fulfill() }
        await service.holdCatalogue(directoryGate); await service.holdChannel(channelGate)
        let model = MusicViewModel(service: service, player: MusicUIKitRegressionPlayer())
        let router = MusicUIKitRegressionRouter()
        let controller = MusicChannelsViewController(model: model, browser: service,
            appearance: MusicAppearancePreferencesStore(), router: router)
        let window = mount(controller)
        defer { release(controller, window: window) }
        await fulfillment(of: [directoryEntered], timeout: 3)
        await waitForResult {
            controller.view.subviews.contains { $0 is MusicLoadingSkeletonView && !$0.isHidden }
        }
        await directoryGate.release()
        let pager = try XCTUnwrap(controller.children.compactMap { $0 as? UIPageViewController }.first)
        await waitForResult {
            guard let page = pager.viewControllers?.first as? MusicBrowseViewController,
                  page.viewIfLoaded?.window != nil else { return false }
            return true
        }
        NotificationCenter.default.post(name: UIApplication.didBecomeActiveNotification, object: nil)
        await fulfillment(of: [channelEntered], timeout: 3)
        let child = try XCTUnwrap(pager.viewControllers?.first as? MusicBrowseViewController)
        let collection = try XCTUnwrap(child.view.subviews.compactMap { $0 as? UICollectionView }.first)
        await waitForResult { collection.backgroundView is MusicLoadingSkeletonView }
        XCTAssertTrue(controller.view.subviews.compactMap { $0 as? MusicLoadingSkeletonView }.allSatisfy(\.isHidden))
        let requests = await service.discoveryRequests
        XCTAssertTrue(requests.contains(.channel(first, tag: "")))
        XCTAssertFalse(requests.contains(.channel(chart, tag: "")))
        await channelGate.release()
        await waitForResult { collection.layoutIfNeeded(); return collection.visibleCells.count == 1 }
        XCTAssertFalse(collection.backgroundView is MusicLoadingSkeletonView)
        await model.close()
    }

    func testLocalCollectionFirstReadUsesSkeletonButContentPreservingReloadDoesNot() async throws {
        let service = MusicUIKitRegressionService(localTracks: [
            .init(ref: .init(kind: .library, id: "one"), songId: "one", title: "one"),
        ])
        let initial = expectation(description: "local initial tracks entered")
        let gate = MusicUIKitSearchGate { initial.fulfill() }
        await service.holdLocalTracks(gate)
        let model = MusicViewModel(service: service, player: MusicUIKitRegressionPlayer())
        let page = MusicCollectionViewModel(model: model, collectionID: "playlist:1")
        let controller = MusicCollectionViewController(model: model, page: page,
            appearance: MusicAppearancePreferencesStore(), router: MusicUIKitRegressionRouter())
        let window = mount(controller)
        defer { release(controller, window: window) }
        await fulfillment(of: [initial], timeout: 3)
        let table = try XCTUnwrap(controller.view.subviews.compactMap { $0 as? UITableView }.first)
        await waitForResult { self.visibleSkeletons(in: table).count == 1 }
        await gate.release()
        await waitForResult { table.layoutIfNeeded(); return table.numberOfSections == 1 &&
            table.numberOfRows(inSection: 0) == 1 &&
            self.visibleSkeletons(in: table).isEmpty }
        let refreshing = expectation(description: "local content preserving refresh entered")
        let refreshGate = MusicUIKitSearchGate { refreshing.fulfill() }
        await service.holdLocalTracks(refreshGate)
        let read = try XCTUnwrap(page.reload(preservingContent: true))
        await fulfillment(of: [refreshing], timeout: 3)
        XCTAssertEqual(table.numberOfRows(inSection: 0), 1)
        XCTAssertTrue(visibleSkeletons(in: table).isEmpty)
        await refreshGate.release(); await read.value
        await model.close()
    }

    func testRemoteCollectionSkeletonLeavesMetadataVisibleAndDoesNotReturnForPagination() async throws {
        let collection = MusicPublicBrowseCollection(platform: .netease, nativeID: "skeleton", title: "保留歌单信息")
        let tracks = [resultTrack("wy", "one"), resultTrack("wy", "two")]
        let service = MusicUIKitRegressionService(detail: .init(collection: collection,
            members: [.init(nativeID: "one", sourcePosition: 0, track: tracks[0])],
            total: 2, cursor: "next", snapshot: "skeleton", consumed: 1),
            nextDetail: .init(collection: collection,
                members: [.init(nativeID: "two", sourcePosition: 1, track: tracks[1])],
                total: 2, cursor: nil, snapshot: "skeleton", consumed: 1))
        let entered = expectation(description: "remote initial read entered")
        let next = expectation(description: "remote next page entered")
        let gate = MusicUIKitSearchGate { entered.fulfill() }, nextGate = MusicUIKitSearchGate { next.fulfill() }
        await service.holdDetail(cursor: nil, gate: gate)
        await service.holdDetail(cursor: "next", gate: nextGate)
        let model = MusicViewModel(service: service, player: MusicUIKitRegressionPlayer())
        let controller = MusicCollectionViewController(model: model, collection: collection, browser: service,
            appearance: MusicAppearancePreferencesStore(), router: MusicUIKitRegressionRouter())
        let window = mount(controller)
        defer { release(controller, window: window) }
        await fulfillment(of: [entered], timeout: 3)
        let table = try XCTUnwrap(controller.view.subviews.compactMap { $0 as? UITableView }.first)
        await waitForResult { self.visibleSkeletons(in: table).count == 1 }
        let title = try XCTUnwrap(descendants(try XCTUnwrap(table.tableHeaderView))
            .compactMap { $0 as? UILabel }.first { $0.text == collection.title })
        XCTAssertFalse(title.isHidden)
        XCTAssertFalse(try XCTUnwrap(table.tableHeaderView).isHidden)
        let footer = try XCTUnwrap(table.tableFooterView)
        XCTAssertTrue(visibleSkeletons(in: table).allSatisfy { $0.isDescendant(of: footer) })
        await gate.release()
        await waitForResult { table.layoutIfNeeded(); return table.numberOfSections == 1 &&
            table.numberOfRows(inSection: 0) == 1 }
        if let cell = table.cellForRow(at: IndexPath(row: 0, section: 0)) {
            controller.tableView(table, willDisplay: cell, forRowAt: IndexPath(row: 0, section: 0))
        }
        await fulfillment(of: [next], timeout: 3)
        XCTAssertEqual(table.numberOfRows(inSection: 0), 1)
        XCTAssertTrue(visibleSkeletons(in: table).isEmpty)
        await nextGate.release()
        await waitForResult { table.numberOfRows(inSection: 0) == 2 }
        await model.close()
    }

    func testSearchSkeletonsRemainIndependentStopWhileHiddenAndNewPageReusesResults() async throws {
        let restore = try useSearchResultPreferences()
        defer { restore() }
        let suite = "MusicUIKitRegressionTests.skeleton-search." + UUID().uuidString
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        defer { defaults.removePersistentDomain(forName: suite) }
        let service = MusicUIKitRegressionService(discovery: [
            .init(hot: [], charts: [], validUntil: Date().addingTimeInterval(600)),
        ])
        await service.configureResultPages(songs: ["wy": [[resultTrack("wy", "one")]]],
            playlists: ["wy": [[.init(platform: .netease, nativeID: "one", title: "歌单")]]])
        await service.allowResultSearch {}
        let songsEntered = expectation(description: "initial songs entered")
        let listsEntered = expectation(description: "initial playlists entered")
        let songsGate = MusicUIKitSearchGate { songsEntered.fulfill() }
        let listsGate = MusicUIKitSearchGate { listsEntered.fulfill() }
        await service.blockResultPage(.songs, page: 1, gate: songsGate)
        await service.blockResultPage(.playlists, page: 1, gate: listsGate)
        let model = MusicViewModel(service: service, player: MusicUIKitRegressionPlayer())
        let appearance = MusicAppearancePreferencesStore()
        let root = MusicSearchViewController(model: model, appearance: appearance,
            router: MusicUIKitRegressionRouter(), history: SearchHistoryStore(defaults: defaults))
        let navigation = PageNavigationController(themeProvider: appearance)
        navigation.setViewControllers([root], animated: false)
        let window = mount(navigation)
        defer { release(navigation, window: window) }
        await waitForResult { root.canPresentPageAlert }
        let shown = expectation(description: "initial result navigation completed")
        navigation.didShowPage = { page in
            if page !== root, page is SearchPageViewController { shown.fulfill() }
        }
        root.query = "骨架测试"; root.onSubmit?("骨架测试")
        navigation.view.layoutIfNeeded()
        await fulfillment(of: [shown], timeout: 3)
        navigation.didShowPage = nil
        let results = try XCTUnwrap(navigation.topViewController as? SearchPageViewController)
        XCTAssertNotNil(results.viewIfLoaded?.window, "The result page must actually finish mounting before foreground admission")
        NotificationCenter.default.post(name: UIApplication.didBecomeActiveNotification, object: nil)
        await fulfillment(of: [songsEntered, listsEntered], timeout: 3)
        let table = try resultTable(0, in: results)
        func skeleton(in section: Int) -> MusicLoadingSkeletonView? {
            table.visibleCells.filter { table.indexPath(for: $0)?.section == section }
                .flatMap(self.descendants).compactMap { $0 as? MusicLoadingSkeletonView }.first { !$0.isHidden }
        }
        await waitForResult { table.layoutIfNeeded(); return skeleton(in: 0) != nil }
        let songSkeleton = try XCTUnwrap(skeleton(in: 0))
        XCTAssertEqual(try resultButton(id: "music.search.results.action.0.0", in: results.view).title(for: .normal), "取消")
        // A compact runner viewport need not materialize both status rows at
        // once. Bring each section on screen before checking its placeholder.
        let lastPlaylistRow = IndexPath(row: table.numberOfRows(inSection: 1) - 1, section: 1)
        table.scrollToRow(at: lastPlaylistRow, at: .bottom, animated: false)
        await waitForResult { table.layoutIfNeeded(); return skeleton(in: 1) != nil }
        let playlistSkeleton = try XCTUnwrap(skeleton(in: 1))
        XCTAssertFalse(songSkeleton === playlistSkeleton)
        XCTAssertEqual(try resultButton(id: "music.search.results.action.0.1", in: results.view).title(for: .normal), "取消")
        let skeletons = [songSkeleton, playlistSkeleton]
        table.setContentOffset(CGPoint(x: 0, y: -table.adjustedContentInset.top), animated: false)
        await waitForResult { results.canPresentPageAlert }
        navigation.pushViewController(PageViewController(themeProvider: appearance), animated: false)
        navigation.view.layoutIfNeeded()
        await waitForResult { !results.isPageVisible }
        XCTAssertTrue(skeletons.allSatisfy { !self.hasSkeletonAnimation($0.layer) })
        navigation.popViewController(animated: false); navigation.view.layoutIfNeeded()
        await waitForResult { results.isPageVisible }
        await songsGate.release()
        await waitForResult { table.layoutIfNeeded(); return table.visibleCells.contains { $0 is MusicSearchSongCell } &&
            self.visibleSkeletons(in: table).count == 1 }
        await listsGate.release()
        await waitForResult { table.layoutIfNeeded(); return table.visibleCells.contains { $0 is MusicSearchPlaylistCell } &&
            self.visibleSkeletons(in: table).isEmpty }
        navigation.popViewController(animated: false); navigation.view.layoutIfNeeded()
        await waitForResult { root.canPresentPageAlert }
        let reopened = expectation(description: "new result navigation completed")
        navigation.didShowPage = { page in
            if page !== root, page is SearchPageViewController { reopened.fulfill() }
        }
        root.onSubmit?("骨架测试")
        navigation.view.layoutIfNeeded()
        await fulfillment(of: [reopened], timeout: 3)
        navigation.didShowPage = nil
        NotificationCenter.default.post(name: UIApplication.didBecomeActiveNotification, object: nil)
        await waitForResult {
            guard let newPage = navigation.topViewController as? SearchPageViewController, newPage !== results,
                  let newTable = self.identified("music.search.results.page.0", in: newPage.view) as? UITableView else {
                return false
            }
            newTable.layoutIfNeeded()
            return newTable.visibleCells.contains { $0 is MusicSearchSongCell } &&
                newTable.visibleCells.contains { $0 is MusicSearchPlaylistCell } &&
                self.visibleSkeletons(in: newTable).isEmpty
        }
        let reads = await service.resultReads()
        XCTAssertEqual(reads.count, 2)
        await model.close()
    }

    func testSearchDiscoveryFirstEmptyLoadUsesSkeletonAndHiddenCompletionDoesNotReload() async throws {
        let suite = "MusicUIKitRegressionTests.skeleton-discovery." + UUID().uuidString
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        defer { defaults.removePersistentDomain(forName: suite) }
        let entered = expectation(description: "discovery initial read entered")
        let gate = MusicUIKitSearchGate { entered.fulfill() }
        let service = MusicUIKitRegressionService(discovery: [
            .init(hot: [.init(keyword: "热词", rank: 1)], charts: [], validUntil: Date().addingTimeInterval(600)),
        ], discoveryGate: gate)
        let model = MusicViewModel(service: service, player: MusicUIKitRegressionPlayer())
        let appearance = MusicAppearancePreferencesStore()
        let controller = MusicSearchViewController(model: model, appearance: appearance,
            router: MusicUIKitRegressionRouter(), history: SearchHistoryStore(defaults: defaults))
        let navigation = PageNavigationController(themeProvider: appearance)
        navigation.setViewControllers([controller], animated: false)
        let window = mount(navigation)
        defer { release(navigation, window: window) }
        await fulfillment(of: [entered], timeout: 3)
        let table = try XCTUnwrap(controller.view.subviews.compactMap { $0 as? UITableView }.first)
        await waitForResult { self.visibleSkeletons(in: table).count == 1 && controller.canPresentPageAlert }
        let skeleton = try XCTUnwrap(table.backgroundView as? MusicLoadingSkeletonView)
        navigation.pushViewController(PageViewController(themeProvider: appearance), animated: false)
        navigation.view.layoutIfNeeded()
        await waitForResult { !controller.isPageVisible }
        XCTAssertFalse(hasSkeletonAnimation(skeleton.layer))
        await gate.release()
        navigation.popViewController(animated: false); navigation.view.layoutIfNeeded()
        await waitForResult { table.layoutIfNeeded(); return table.numberOfRows(inSection: 0) > 0 &&
            self.visibleSkeletons(in: table).isEmpty }
        let calls = await service.searchCalls(); XCTAssertEqual(calls.count, 1)
        await model.close()
    }

    private func visibleSkeletons(in view: UIView) -> [MusicLoadingSkeletonView] {
        descendants(view).compactMap { $0 as? MusicLoadingSkeletonView }.filter { skeleton in
            guard skeleton.window != nil else { return false }
            var ancestor: UIView? = skeleton
            while let view = ancestor {
                if view.isHidden { return false }
                ancestor = view.superview
            }
            return true
        }
    }

    private func hasSkeletonAnimation(_ layer: CALayer) -> Bool {
        layer.animationKeys()?.contains("music.loading.shimmer") == true ||
            (layer.sublayers ?? []).contains(where: hasSkeletonAnimation)
    }

    func testHiddenChannelCatalogueDefersReplacementAndRevokesTheOldChild() async throws {
        let remembered = UserDefaults.standard.object(forKey: "music.ui.lastChannel")
        defer { UserDefaults.standard.set(remembered, forKey: "music.ui.lastChannel") }
        let channel = MusicPublicBrowseChannel(id: "chart", title: "排行榜", kind: .rankings)
        let directory = MusicPublicBrowsePage(sections: [
            .init(id: "directory", title: "", items: [
                .init(id: channel.identity, title: channel.title, content: .channel(channel)),
            ], layout: .playlistRail),
        ])
        let service = MusicUIKitRegressionService(browse: directory, channelBrowse: .init(sections: [
            .init(id: "cards", title: "", items: [card("kept", previews: [])], layout: .playlistRail),
        ]))
        let model = MusicViewModel(service: service, player: MusicUIKitRegressionPlayer())
        let appearance = MusicAppearancePreferencesStore(), router = MusicUIKitRegressionRouter()
        let controller = MusicChannelsViewController(model: model, browser: service,
            appearance: appearance, router: router)
        controller.select(channel)
        let navigation = PageNavigationController(themeProvider: appearance)
        navigation.setViewControllers([controller], animated: false)
        let window = mount(navigation)
        defer { release(navigation, window: window) }
        let pager = try XCTUnwrap(controller.children.compactMap { $0 as? UIPageViewController }.first)
        var activated: UIViewController?
        await waitForResult {
            guard let page = pager.viewControllers?.first as? MusicBrowseViewController,
                  let collection = page.view.subviews.compactMap({ $0 as? UICollectionView }).first else { return false }
            if page.view.window != nil, activated !== page {
                activated = page
                NotificationCenter.default.post(name: UIApplication.didBecomeActiveNotification, object: nil)
            }
            collection.layoutIfNeeded()
            return !collection.visibleCells.isEmpty
        }
        let old = try XCTUnwrap(pager.viewControllers?.first as? MusicBrowseViewController)
        await waitForHomeSubscription(service)
        let before = await service.counts()
        await waitForResult { controller.canPresentPageAlert && navigation.transitionCoordinator == nil }
        navigation.pushViewController(PageViewController(themeProvider: appearance), animated: false)
        navigation.view.layoutIfNeeded()
        await waitForResult { !controller.isPageVisible }
        await service.updateBrowse(directory)
        await waitForResult { !old.isPageActive }
        let hidden = await service.counts()
        XCTAssertEqual(hidden.browse, before.browse)
        navigation.popViewController(animated: false)
        navigation.view.layoutIfNeeded()
        await waitForResult {
            guard let page = pager.viewControllers?.first as? MusicBrowseViewController,
                  page !== old,
                  let collection = page.view.subviews.compactMap({ $0 as? UICollectionView }).first else { return false }
            if page.view.window != nil, activated !== page {
                activated = page
                NotificationCenter.default.post(name: UIApplication.didBecomeActiveNotification, object: nil)
            }
            collection.layoutIfNeeded()
            return !collection.visibleCells.isEmpty
        }
        let returned = await service.discoveryRequests
        XCTAssertEqual(returned.filter { $0 == .channels }.count, 2)
        XCTAssertEqual(returned.filter { $0 == .channel(channel, tag: "") }.count, 2)
        await model.close()
    }

    func testBrowseReusesHeaderAndStatusViewsButReplacesChangedMenusAndRejectsOldActions() async throws {
        let service = MusicUIKitRegressionService()
        let model = MusicViewModel(service: service, player: MusicUIKitRegressionPlayer())
        let appearance = MusicAppearancePreferencesStore(), router = MusicUIKitRegressionRouter()
        let page = MusicBrowseViewController(title: "推荐", model: model, browser: service,
            request: .discovery(.home), appearance: appearance, router: router)
        let window = mount(page)
        defer { release(page, window: window) }
        let collection = try XCTUnwrap(page.view.subviews.compactMap { $0 as? UICollectionView }.first)
        await waitForHomeSubscription(service)
        await waitForResult { (collection.backgroundView as? UILabel)?.text == "暂无内容" }
        let status = try XCTUnwrap(collection.backgroundView)
        let button = try XCTUnwrap(descendants(page.pageHeader).compactMap { $0 as? PageHeaderButton }
            .first { $0.accessibilityLabel == "更多" })
        let menu = try XCTUnwrap(button.menu)
        let refresh = try XCTUnwrap(menu.children.compactMap { $0 as? UIAction }.first { $0.title == "刷新" })
        page.applyTheme(); page.applyTheme()
        XCTAssertTrue(button.menu === menu); XCTAssertTrue(collection.backgroundView === status)
        let entered = expectation(description: "explicit refresh started")
        let gate = MusicUIKitSearchGate { entered.fulfill() }
        await service.holdHome(gate)
        invoke(refresh)
        await fulfillment(of: [entered], timeout: 3)
        await waitForResult { (collection.backgroundView as? UILabel)?.text == "加载中" }
        XCTAssertTrue(collection.backgroundView === status)
        await gate.release()
        await waitForResult { (collection.backgroundView as? UILabel)?.text == "暂无内容" }
        XCTAssertTrue(descendants(page.pageHeader).contains { $0 === button })
        let currentMenu = try XCTUnwrap(button.menu)
        XCTAssertFalse(currentMenu === menu)
        let changed = expectation(description: "current refresh starts")
        let nextGate = MusicUIKitSearchGate { changed.fulfill() }
        await service.holdHome(nextGate)
        invoke(refresh)
        await deliverMainRunLoop()
        let staleCalls = await service.counts()
        XCTAssertEqual(staleCalls.browse, 2, "An old captured menu cannot refresh a successor epoch")
        invoke(try XCTUnwrap(currentMenu.children.compactMap { $0 as? UIAction }.first { $0.title == "刷新" }))
        await fulfillment(of: [changed], timeout: 3)
        await nextGate.release()
        await waitForResult { (collection.backgroundView as? UILabel)?.text == "暂无内容" }
        await model.close()
    }

    func testCollectionCellKeepsMenuForStateOnlyChangesAndRefreshesRealActionInputs() throws {
        let track = MusicRemoteTrack(ref: .init(kind: .library, id: "same"), songId: "same", title: "歌曲")
        var displayedTrack = track
        let cell = MusicCollectionTrackCell(style: .default, reuseIdentifier: nil)
        var builds = 0, plays = 0
        var revision = UUID()
        func menu() -> UIMenu {
            builds += 1
            return UIMenu(children: [UIAction(title: "菜单 \(builds)") { _ in }])
        }
        func configure(active: Bool = false, favorite: Bool = false, enabled: Bool = true,
                       filtered: Int = 0, original: Int = 0, theme: PageTheme = .init()) {
            let callback = builds + 1
            cell.configure(track: displayedTrack, active: active, isFavorite: favorite, favoriteEnabled: enabled,
                actionsEnabled: enabled, theme: theme, actionRevision: revision, menu: menu(),
                filteredIndex: filtered, originalIndex: original,
                onPlay: { plays = callback }, onFavorite: {}, onAppend: {})
        }
        configure()
        let more = try resultButton(id: "music.collection.more.same", in: cell)
        let favorite = try resultButton(id: "music.collection.favorite.same", in: cell)
        let play = try resultButton(id: "music.collection.play.same", in: cell)
        let firstMenu = try XCTUnwrap(more.menu)
        for _ in 0..<20 { configure() }
        configure(active: true, favorite: true, theme: .init(palette: .mint))
        XCTAssertEqual(builds, 1); XCTAssertTrue(more.menu === firstMenu)
        XCTAssertTrue(play.accessibilityTraits.contains(.selected))
        XCTAssertTrue(favorite.accessibilityTraits.contains(.selected))
        play.sendActions(for: .touchUpInside); XCTAssertEqual(plays, 2, "Callbacks must use current inputs")
        configure(enabled: false)
        XCTAssertFalse(play.isEnabled); XCTAssertFalse(favorite.isEnabled); XCTAssertFalse(more.isEnabled)
        XCTAssertEqual(builds, 1)
        configure(filtered: 1, original: 4)
        XCTAssertEqual(builds, 2); XCTAssertFalse(more.menu === firstMenu)
        revision = UUID(); configure(filtered: 1, original: 4)
        XCTAssertEqual(builds, 3)
        displayedTrack = .init(ref: track.ref, songId: track.songId, title: "更新的歌曲", artist: "更新的歌手")
        configure(filtered: 1, original: 4)
        XCTAssertEqual(builds, 4)
        XCTAssertTrue(descendants(cell).compactMap { $0 as? UILabel }.contains { $0.text == "更新的歌曲" })
        cell.prepareForReuse(); configure()
        XCTAssertEqual(builds, 5); XCTAssertEqual(cell.representedReference, track.ref)
    }

    func testLocalCollectionUnrelatedStateKeepsRowsAndMenusAndFilteringUsesOriginalPosition() async throws {
        let tracks = (0..<2000).map { index in
            MusicRemoteTrack(ref: .init(kind: .library, id: "song-\(index)"), songId: "song-\(index)",
                             title: index == 1999 ? "needle" : "song \(index)")
        }
        let service = MusicUIKitRegressionService(localTracks: tracks)
        let model = MusicViewModel(service: service, player: MusicUIKitRegressionPlayer())
        let page = MusicCollectionViewModel(model: model, collectionID: "playlist:1")
        await page.appear()?.value
        let router = MusicUIKitRegressionRouter()
        let controller = MusicCollectionViewController(model: model, page: page,
            appearance: MusicAppearancePreferencesStore(), router: router)
        let window = mount(controller)
        defer { release(controller, window: window) }
        let table = try XCTUnwrap(controller.view.subviews.compactMap { $0 as? UITableView }.first)
        await waitForResult { table.layoutIfNeeded(); return table.numberOfRows(inSection: 0) == 2000 &&
            table.visibleCells.contains { $0.accessibilityIdentifier == "music.collection.track.song-0" } }
        let first = try XCTUnwrap(table.visibleCells.first { $0.accessibilityIdentifier == "music.collection.track.song-0" })
        let more = try resultButton(id: "music.collection.more.song-0", in: first)
        let initialMenu = try XCTUnwrap(more.menu)
        page.objectWillChange.send(); page.objectWillChange.send()
        await deliverMainRunLoop()
        XCTAssertTrue(table.visibleCells.contains { $0 === first }); XCTAssertTrue(more.menu === initialMenu)
        let target = MusicCollection(id: "target", title: "目标歌单", kind: "playlist", count: 0, revision: 1)
        model.publishCollections(model.collections + [target])
        await waitForResult {
            more.menu?.children.compactMap { $0 as? UIMenu }.first { $0.title == "添加到歌单" }?
                .children.contains { $0.title == "目标歌单" } == true
        }
        XCTAssertFalse(more.menu === initialMenu)
        page.searchQuery = "needle"
        await waitForResult { table.layoutIfNeeded(); return table.numberOfRows(inSection: 0) == 1 &&
            table.visibleCells.contains { $0.accessibilityIdentifier == "music.collection.track.song-1999" } }
        let filtered = try XCTUnwrap(table.visibleCells.first { $0.accessibilityIdentifier == "music.collection.track.song-1999" })
        let append = try resultButton(id: "music.collection.append.song-1999", in: filtered)
        let enqueued = expectation(description: "filtered row appended")
        await service.observeLocalQueue { enqueued.fulfill() }
        append.sendActions(for: .touchUpInside)
        await fulfillment(of: [enqueued], timeout: 3)
        let refs = await service.localQueueReferences
        XCTAssertEqual(refs, [tracks[1999].ref], "Filtered row zero must retain original row 1999")
        page.searchQuery = ""
        await waitForResult { table.numberOfRows(inSection: 0) == 2000 }
        await model.close()
    }

    func testModalSettingsOwnTheirCloseAndDoneControls() async throws {
        let service = MusicUIKitRegressionService()
        let owner = MusicViewModel(service: service, player: MusicUIKitRegressionPlayer())
        let appearance = MusicAppearancePreferencesStore()
        let browse = MusicPublicBrowseViewModel(service: service, request: .discovery(.home))
        let source = MusicRemoteQueueSource(
            context: .init(kind: .recommendation, id: "daily", title: "每日推荐", platform: "wy"),
            loadPage: { _ in .init(items: []) }
        )
        let daily = MusicDailyStyleViewController(page: browse, source: source, appearance: appearance)
        let fm = MusicFMSettingsViewController(model: owner, appearance: appearance)
        let sources = MusicSourcesViewController(model: owner, tracks: [], appearance: appearance)
        for page in [daily, fm, sources] as [PageViewController] {
            page.loadViewIfNeeded()
            page.view.frame = CGRect(x: 0, y: 0, width: 390, height: 600)
            page.view.layoutIfNeeded()
            XCTAssertTrue(page.hasOwnPageHeader)
            XCTAssertEqual(page.pageHeader.title, page.pageTitle)
            XCTAssertNil(page.title)
            XCTAssertNil(page.navigationItem.title)
            let close = try XCTUnwrap(descendants(page.pageHeader).compactMap { $0 as? PageHeaderButton }
                .first { $0.accessibilityLabel == "关闭" })
            XCTAssertNil(close.configuration)
            XCTAssertEqual(close.layer.borderWidth, 0)
            close.isHighlighted = true
            XCTAssertFalse(close.isHighlighted)
            XCTAssertGreaterThan(close.convert(close.bounds, to: page.view).width, 0)
            PageViewController.invalidatePages(in: page)
        }
        XCTAssertTrue(descendants(daily.pageHeader).compactMap { $0 as? PageHeaderTextButton }
            .contains { $0.title(for: .normal) == "完成" })
        browse.disappear()
        await source.close()
        await owner.close()
    }

    func testFMArtworkOnlyUpdatesForEffectiveCoverWithoutReconfiguringCard() async throws {
        let action = MusicHomeAction.fm(mode: .default, submode: "", entry: nil)
        let appearance = MusicAppearancePreferencesStore()
        let service = MusicUIKitRegressionService(browse: .init(sections: [
            .init(id: "fm", title: "", items: [
                .init(id: "fm", title: "私人 FM", content: .action(action)),
            ], layout: .featureRail),
        ]))
        let model = MusicViewModel(service: service, player: MusicUIKitRegressionPlayer())
        let controller = MusicBrowseViewController(title: "推荐", model: model, browser: service,
            request: .discovery(.home), appearance: appearance, router: MusicUIKitRegressionRouter(), embedded: true)
        let window = mount(controller)
        defer { release(controller, window: window) }
        let collection = try XCTUnwrap(controller.view.subviews.compactMap { $0 as? UICollectionView }.first)
        let loaded = expectation(for: NSPredicate { _, _ in
            MainActor.assumeIsolated { collection.layoutIfNeeded(); return collection.visibleCells.count == 1 }
        }, evaluatedWith: collection)
        await fulfillment(of: [loaded], timeout: 2)
        let cell = try XCTUnwrap(collection.visibleCells.first as? MusicBrowseCell)
        let title = try XCTUnwrap(descendants(cell).compactMap { $0 as? UILabel }.first { $0.text == "私人 FM" })
        let image = try XCTUnwrap(descendants(cell).compactMap { $0 as? MusicArtworkView }.first)
        title.text = "保留已配置内容"
        var events = 0, covers: [URL] = []
        let subscription = model.fmArtworkChanges.sink { events += 1 }
        image.onFailure = { covers.append($0) }
        let source = MusicRemoteQueueSource(context: .init(kind: .fm, id: "fm", title: "FM", platform: "wy"),
            loadPage: { _ in XCTFail("Artwork observation must not load a queue"); return .init(items: []) })
        model.activateFM(source)
        let first = try XCTUnwrap(URL(string: "synthetic-artwork://first"))
        model.playbackState.queue = [.init(id: "fm-song", track: .init(ref: .init(kind: .queue, id: "fm-song"),
            songId: "41", title: "当前歌曲", artwork: first), remoteSessionID: source.id)]
        model.playbackState.state = .init(currentId: "fm-song", total: 1)
        model.playbackState.activeHomeFM = action
        let firstCover = expectation(for: NSPredicate { _, _ in
            MainActor.assumeIsolated { covers == [first] }
        }, evaluatedWith: image)
        await fulfillment(of: [firstCover], timeout: 2)
        XCTAssertEqual(events, 1)
        XCTAssertTrue(collection.visibleCells.first === cell); XCTAssertEqual(title.text, "保留已配置内容")
        let unchanged = expectation(description: "unrelated state does not change the cover")
        unchanged.isInverted = true
        image.onFailure = { covers.append($0); unchanged.fulfill() }
        model.playbackState.state = .init(settingsRevision: 1, currentId: "fm-song", total: 1, position: 12)
        model.playbackState.phase = .playing
        model.playbackState.canEnterSimilarMode = true
        model.metadataState.lyricState = "loaded"
        model.metadataState.artworkData = Data([1, 2, 3])
        model.metadataState.artworkURL = first
        model.metadataState.occurrenceID = "fm-song"
        model.playbackState.queue = model.queue
        await fulfillment(of: [unchanged], timeout: 0.1)
        XCTAssertEqual(events, 1); XCTAssertEqual(covers, [first])
        XCTAssertEqual(title.text, "保留已配置内容")
        let second = try XCTUnwrap(URL(string: "synthetic-artwork://second"))
        let nextCover = expectation(description: "new effective metadata cover")
        image.onFailure = { covers.append($0); nextCover.fulfill() }
        model.metadataState.artworkURL = second
        await fulfillment(of: [nextCover], timeout: 2)
        XCTAssertEqual(events, 2); XCTAssertEqual(covers, [first, second])
        XCTAssertEqual(model.currentFMArtwork(action), second)
        XCTAssertTrue(collection.visibleCells.first === cell); XCTAssertEqual(title.text, "保留已配置内容")
        model.playbackState.phase = .failed
        XCTAssertEqual(events, 3); XCTAssertNil(model.currentFMArtwork(action))
        let counts = await service.counts(); XCTAssertEqual(counts.browse, 1)
        subscription.cancel(); image.onFailure = nil; await model.close()
    }

    func testSettingsErrorWaitsForAppearanceAndIsNotPresentedAgainOnReturn() async throws {
        let appearance = MusicAppearancePreferencesStore()
        let page = MusicSettingsPageViewController(appearance: appearance, content: Text("设置"))
        page.reportError("读取失败", retry: {})
        XCTAssertFalse(page.isViewLoaded)
        XCTAssertNil(page.presentedViewController)
        let navigation = PageNavigationController(themeProvider: appearance)
        navigation.setViewControllers([page], animated: false)
        let window = mount(navigation)
        defer { release(navigation, window: window) }
        let presented = expectation(for: NSPredicate { _, _ in
            MainActor.assumeIsolated {
                guard let alert = page.presentedViewController as? UIAlertController else { return false }
                return alert.viewIfLoaded?.window != nil && !alert.isBeingPresented &&
                    alert.transitionCoordinator == nil
            }
        }, evaluatedWith: page)
        await fulfillment(of: [presented], timeout: 2)
        let alert = try XCTUnwrap(page.presentedViewController as? UIAlertController)
        XCTAssertEqual(alert.message, "读取失败")
        XCTAssertTrue(alert.actions.contains { $0.title == "重试" })
        await withCheckedContinuation { continuation in
            page.dismiss(animated: false) { continuation.resume() }
        }
        navigation.pushViewController(UIViewController(), animated: false)
        navigation.view.layoutIfNeeded()
        navigation.popViewController(animated: false)
        navigation.view.layoutIfNeeded()
        let returned = expectation(for: NSPredicate { _, _ in
            MainActor.assumeIsolated { page.isPageVisible }
        }, evaluatedWith: page)
        await fulfillment(of: [returned], timeout: 2)
        page.reportError("读取失败", retry: {})
        XCTAssertNil(page.presentedViewController)
        XCTAssertTrue(page.isPageActive)
    }

    func testDiscoveryPartialFailuresKeepSuccessAndExplicitRetryBypassesExpiry() async throws {
        let chart = MusicSearchDiscoveryChart(collection: .init(platform: .netease, nativeID: "chart",
            title: "榜单"))
        let hot = (1...8).map { MusicSearchHotKeyword(keyword: "保留热词\($0)", rank: $0) }
        let until = Date().addingTimeInterval(3600)
        let suite = "MusicUIKitRegressionTests.search." + UUID().uuidString
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        defer { defaults.removePersistentDomain(forName: suite) }
        let history = SearchHistoryStore(defaults: defaults)
        for index in (1...8).reversed() {
            history.record("较长的搜索历史关键词 \(index)")
        }
        let detail = searchDetail(chart.collection, positions: [0, 2])
        let service = MusicUIKitRegressionService(discovery: [
            .init(hot: hot, hotCanPlay: true, charts: [],
                  failures: [.init(component: .charts, error: .networkOffline)], validUntil: until),
            .init(hot: [], charts: [chart], validUntil: until),
        ], details: [chart.id: [detail]])
        let model = MusicViewModel(service: service, player: MusicUIKitRegressionPlayer())
        let controller = MusicSearchViewController(model: model, appearance: MusicAppearancePreferencesStore(),
                                                   router: MusicUIKitRegressionRouter(), history: history)
        let navigation = PageNavigationController(themeProvider: MusicAppearancePreferencesStore())
        navigation.setViewControllers([controller], animated: false)
        let window = mount(navigation)
        defer { release(navigation, window: window) }
        let table = try XCTUnwrap(controller.view.subviews.compactMap { $0 as? UITableView }.first)
        let failure = expectation(for: NSPredicate { _, _ in
            MainActor.assumeIsolated {
                guard let alert = controller.presentedViewController as? UIAlertController else { return false }
                return !alert.isBeingPresented && alert.viewIfLoaded?.window != nil &&
                    alert.transitionCoordinator == nil
            }
        }, evaluatedWith: controller)
        await fulfillment(of: [failure], timeout: 2)
        let alert = try XCTUnwrap(controller.presentedViewController as? UIAlertController)
        XCTAssertTrue(alert.message?.contains(MusicServiceError.networkOffline.message(
            operation: "音乐榜单读取", platform: MusicPublicBrowsePlatform.netease.title)) == true)
        XCTAssertTrue(alert.actions.contains { $0.title == "重试" })
        let firstHot = hot[0]
        XCTAssertNotNil(identified("music.search.suggestions.keyword.\(firstHot.id)", in: controller.view))
        XCTAssertNotNil(identified("music.search.chart.row.search.hot.\(firstHot.id)", in: controller.view))
        let hotPlay = try XCTUnwrap(identified("music.search.chart.play.search.hot", in: controller.view) as? UIButton)
        XCTAssertFalse(hotPlay.isHidden); XCTAssertTrue(hotPlay.isEnabled)
        let hotRow = try XCTUnwrap(identified("music.search.chart.row.search.hot.\(firstHot.id)",
                                             in: controller.view) as? UIButton)
        XCTAssertTrue(try XCTUnwrap(hotRow.accessibilityLabel).hasPrefix("搜索"))
        XCTAssertNotNil(identified("music.search.history", in: controller.view))
        let localBefore = await service.counts()
        try XCTUnwrap(identified("music.search.suggestions.rotate", in: controller.view) as? UIButton)
            .sendActions(for: .touchUpInside)
        let toggle = try XCTUnwrap(identified("music.search.history.toggle", in: controller.view) as? UIButton)
        XCTAssertFalse(toggle.isHidden)
        toggle.sendActions(for: .touchUpInside)
        try XCTUnwrap(identified("music.search.history.clear", in: controller.view) as? UIButton)
            .sendActions(for: .touchUpInside)
        await deliverMainRunLoop()
        let localAfter = await service.counts()
        XCTAssertEqual(localAfter.discovery, localBefore.discovery)
        XCTAssertEqual(localAfter.details, localBefore.details)
        await withCheckedContinuation { continuation in
            controller.dismiss(animated: false) { continuation.resume() }
        }
        controller.retryDiscovery()
        let chartRow = expectation(for: NSPredicate { _, _ in
            MainActor.assumeIsolated { table.numberOfRows(inSection: 0) == 2 }
        }, evaluatedWith: table)
        await fulfillment(of: [chartRow], timeout: 2)
        table.scrollToRow(at: IndexPath(row: 1, section: 0), at: .top, animated: false)
        table.layoutIfNeeded()
        controller.scrollViewDidScroll(table)
        let recovered = expectation(for: NSPredicate { _, _ in
            MainActor.assumeIsolated {
                controller.view.layoutIfNeeded()
                return self.identified("music.search.chart.row.\(chart.id).id-0", in: controller.view) != nil
            }
        }, evaluatedWith: controller)
        await fulfillment(of: [recovered], timeout: 2)
        XCTAssertNotNil(identified("music.search.suggestions.keyword.\(firstHot.id)", in: controller.view),
                        "Retrying charts must retain successful hot words")
        XCTAssertNotNil(identified("music.search.chart.card.\(chart.id)", in: controller.view))
        XCTAssertNotNil(identified("music.search.chart.play.\(chart.id)", in: controller.view))
        XCTAssertEqual(try XCTUnwrap(identified("music.search.chart.rank.\(chart.id).id-2",
                                               in: controller.view) as? UILabel).text, "3")
        controller.beginAppearanceTransition(false, animated: false)
        controller.endAppearanceTransition()
        controller.beginAppearanceTransition(true, animated: false)
        controller.endAppearanceTransition()
        controller.view.layoutIfNeeded()
        await deliverMainRunLoop()
        let counts = await service.counts()
        XCTAssertEqual(counts.discovery, 2)
        XCTAssertEqual(counts.details, 1)
        let searchCalls = await service.searchCalls()
        XCTAssertEqual(searchCalls, [Set([.hot, .charts]), Set([.charts])])
        XCTAssertNil(table.refreshControl)
        await model.close()
    }

    func testSearchChartDemandRequiresMemberViewportAndTracksOuterAndHorizontalScrolling() async throws {
        let charts = [
            MusicSearchDiscoveryChart(collection: .init(platform: .netease, nativeID: "one", title: "榜单一")),
            MusicSearchDiscoveryChart(collection: .init(platform: .netease, nativeID: "two", title: "榜单二")),
        ]
        let hot = (1...20).map { MusicSearchHotKeyword(keyword: "热词\($0)", rank: $0) }
        let suite = "MusicUIKitRegressionTests.search-demand." + UUID().uuidString
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        defer { defaults.removePersistentDomain(forName: suite) }
        let history = SearchHistoryStore(defaults: defaults)
        let entered = expectation(description: "search discovery entered")
        let gate = MusicUIKitSearchGate { entered.fulfill() }
        let service = MusicUIKitRegressionService(
            discovery: [.init(hot: hot, charts: charts, validUntil: Date().addingTimeInterval(600))],
            details: [
                charts[0].id: [searchDetail(charts[0].collection, positions: [0])],
                charts[1].id: [searchDetail(charts[1].collection, positions: [0, 3])],
            ],
            discoveryGate: gate
        )
        let model = MusicViewModel(service: service, player: MusicUIKitRegressionPlayer())
        let controller = MusicSearchViewController(model: model, appearance: MusicAppearancePreferencesStore(),
                                                   router: MusicUIKitRegressionRouter(), history: history)
        controller.loadViewIfNeeded()
        let table = try XCTUnwrap(controller.view.subviews.compactMap { $0 as? UITableView }.first)
        let window = mount(controller)
        defer { release(controller, window: window) }
        await fulfillment(of: [entered], timeout: 2)
        controller.additionalSafeAreaInsets.bottom = 24
        controller.view.layoutIfNeeded()
        let fixedInsets = table.adjustedContentInset
        table.contentInset.bottom += max(0, table.bounds.height - fixedInsets.top - fixedInsets.bottom - 60)
        table.layoutIfNeeded()
        await gate.release()

        let rows = expectation(for: NSPredicate { _, _ in
            MainActor.assumeIsolated { table.numberOfRows(inSection: 0) == 2 }
        }, evaluatedWith: table)
        await fulfillment(of: [rows], timeout: 2)
        let chartPath = IndexPath(row: 1, section: 0)
        let chartRect = table.rectForRow(at: chartPath)
        table.setContentOffset(CGPoint(x: 0, y: chartRect.minY - table.adjustedContentInset.top),
                               animated: false)
        table.layoutIfNeeded()
        controller.scrollViewDidScroll(table)
        let cardReady = expectation(for: NSPredicate { _, _ in
            MainActor.assumeIsolated {
                controller.view.layoutIfNeeded()
                return self.identified("music.search.chart.members.\(charts[0].id)",
                                       in: controller.view) != nil
            }
        }, evaluatedWith: controller)
        await fulfillment(of: [cardReady], timeout: 2)
        var counts = await service.counts()
        XCTAssertEqual(counts.details, 0, "A visible title without members must not read a chart page")

        let firstMembers = try XCTUnwrap(identified("music.search.chart.members.\(charts[0].id)",
                                                   in: controller.view))
        let firstMembersInTable = firstMembers.convert(firstMembers.bounds, to: table)
        table.setContentOffset(CGPoint(
            x: 0,
            y: firstMembersInTable.maxY - 44 - table.adjustedContentInset.top
        ), animated: false)
        table.layoutIfNeeded()
        controller.scrollViewDidScroll(table)
        let firstLoaded = expectation(for: NSPredicate { _, _ in
            MainActor.assumeIsolated {
                controller.view.layoutIfNeeded()
                return self.identified("music.search.chart.row.\(charts[0].id).id-0",
                                       in: controller.view) != nil
            }
        }, evaluatedWith: controller)
        await fulfillment(of: [firstLoaded], timeout: 2)
        counts = await service.counts()
        XCTAssertEqual(counts.details, 1,
                       "The lower member region of a short card remains demand-bearing in the shared tall rail")

        let rail = try XCTUnwrap(identified("music.search.charts", in: controller.view)
                                 as? MusicSearchChartRailCell)
        let horizontal = try XCTUnwrap(identified("music.search.charts.scroll", in: rail) as? UIScrollView)
        let secondCard = try XCTUnwrap(identified("music.search.chart.card.\(charts[1].id)", in: rail))
        horizontal.setContentOffset(CGPoint(x: secondCard.frame.minX, y: 0), animated: false)
        rail.scrollViewDidScroll(horizontal)
        let secondLoaded = expectation(for: NSPredicate { _, _ in
            MainActor.assumeIsolated {
                controller.view.layoutIfNeeded()
                return self.identified("music.search.chart.row.\(charts[1].id).id-3",
                                       in: controller.view) != nil
            }
        }, evaluatedWith: controller)
        await fulfillment(of: [secondLoaded], timeout: 2)
        counts = await service.counts()
        XCTAssertEqual(counts.details, 2)
        let detailCalls = await service.detailCallIDs()
        XCTAssertEqual(detailCalls, charts.map(\.id))
        await model.close()
    }

    func testSearchResultsPagePushesOnceAndKeepsSingleHistoryEntry() async throws {
        let suite = "MusicUIKitRegressionTests.search-access." + UUID().uuidString
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        defer { defaults.removePersistentDomain(forName: suite) }
        let history = SearchHistoryStore(defaults: defaults)
        history.record("原关键词")
        let service = MusicUIKitRegressionService()
        let model = MusicViewModel(service: service, player: MusicUIKitRegressionPlayer())
        let controller = MusicSearchViewController(model: model, appearance: MusicAppearancePreferencesStore(),
                                                   router: MusicUIKitRegressionRouter(), history: history)
        let navigation = PageNavigationController(themeProvider: MusicAppearancePreferencesStore())
        navigation.setViewControllers([controller], animated: false)
        let window = mount(navigation)
        defer { release(navigation, window: window) }
        let searchResultsShown = expectation(description: "search results navigation completed")
        navigation.didShowPage = { page in
            if page !== controller, page is SearchPageViewController {
                searchResultsShown.fulfill()
            }
        }
        defer { navigation.didShowPage = nil }
        controller.query = "原关键词"
        controller.onSubmit?("原关键词")
        await fulfillment(of: [searchResultsShown], timeout: 2)
        let results = try XCTUnwrap(navigation.topViewController as? SearchPageViewController)
        XCTAssertFalse(results === controller)
        XCTAssertEqual(navigation.viewControllers.count, 2)
        XCTAssertEqual(history.entries.map(\.text), ["原关键词"])
        XCTAssertTrue(results.isPageActive)
        XCTAssertEqual(results.query, "原关键词")
        XCTAssertEqual(history.entries.map(\.text), ["原关键词"])
        XCTAssertEqual(results.searchField.text, "原关键词")
        await model.close()
    }

    func testSearchResultsResubmitsRetainedKeywordWithoutAddingHistoryAfterAccountChange() async throws {
        let suite = "MusicUIKitRegressionTests.search-resubmit." + UUID().uuidString
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        defer { defaults.removePersistentDomain(forName: suite) }
        let restorePreferences = try useSearchResultPreferences()
        defer { restorePreferences() }
        let history = SearchHistoryStore(defaults: defaults)
        let service = MusicUIKitRegressionService()
        let firstReads = expectation(description: "initial song and playlist searches")
        firstReads.expectedFulfillmentCount = 2
        await service.allowResultSearch { firstReads.fulfill() }
        let model = MusicViewModel(service: service, player: MusicUIKitRegressionPlayer())
        let controller = MusicSearchViewController(model: model, appearance: MusicAppearancePreferencesStore(),
                                                   router: MusicUIKitRegressionRouter(), history: history)
        let navigation = PageNavigationController(themeProvider: MusicAppearancePreferencesStore())
        navigation.setViewControllers([controller], animated: false)
        let window = mount(navigation)
        defer { release(navigation, window: window) }
        controller.query = "原关键词"
        controller.onSubmit?("原关键词")
        await fulfillment(of: [firstReads], timeout: 2)
        await deliverMainRunLoop()
        let results = try XCTUnwrap(navigation.topViewController as? SearchPageViewController)
        XCTAssertEqual(history.entries.map(\.text), ["原关键词"])
        XCTAssertEqual(results.query, "原关键词")
        results.query = "尚未提交的新关键词"
        history.record("较新的关键词")
        let entries = history.entries, storedHistory = defaults.data(forKey: SearchHistoryStore.storageKey)
        var historyChanges = 0
        let historyObservation = history.$entries.dropFirst().sink { _ in historyChanges += 1 }
        defer { historyObservation.cancel() }
        let repeatedReads = expectation(description: "new generation replays both searches once")
        repeatedReads.expectedFulfillmentCount = 2
        await service.allowResultSearch { repeatedReads.fulfill() }
        model.accountSessionsChanged()
        await fulfillment(of: [repeatedReads], timeout: 2)
        await deliverMainRunLoop()
        let reads = await service.resultReads()
        XCTAssertEqual(reads.count, 4)
        XCTAssertEqual(reads.filter { $0.section == .songs }.count, 2)
        XCTAssertEqual(reads.filter { $0.section == .playlists }.count, 2)
        XCTAssertTrue(reads.allSatisfy { $0.query == "原关键词" && $0.page == 1 && $0.limit == 30 })
        XCTAssertTrue(results.isPageActive)
        XCTAssertEqual(results.query, "尚未提交的新关键词")
        XCTAssertEqual(history.entries, entries)
        XCTAssertEqual(defaults.data(forKey: SearchHistoryStore.storageKey), storedHistory)
        XCTAssertEqual(historyChanges, 0)
        await model.close()
    }
    func testUnavailableActiveSearchResultsClearAndOnlyResubmitAfterAccessReturns() async throws {
        let suite = "MusicUIKitRegressionTests.search-unavailable." + UUID().uuidString
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        defer { defaults.removePersistentDomain(forName: suite) }
        let restorePreferences = try useSearchResultPreferences()
        defer { restorePreferences() }
        let history = SearchHistoryStore(defaults: defaults), service = MusicUIKitRegressionService()
        let firstReads = expectation(description: "initial search reads")
        firstReads.expectedFulfillmentCount = 2
        await service.allowResultSearch { firstReads.fulfill() }
        let model = MusicViewModel(service: service, player: MusicUIKitRegressionPlayer())
        let controller = MusicSearchViewController(model: model, appearance: MusicAppearancePreferencesStore(),
                                                   router: MusicUIKitRegressionRouter(), history: history)
        let navigation = PageNavigationController(themeProvider: MusicAppearancePreferencesStore())
        navigation.setViewControllers([controller], animated: false)
        let window = mount(navigation)
        defer { release(navigation, window: window) }
        controller.query = "原关键词"; controller.onSubmit?("原关键词")
        await fulfillment(of: [firstReads], timeout: 2)
        let results = try XCTUnwrap(navigation.topViewController as? SearchPageViewController)
        let table = try XCTUnwrap(identified("music.search.results.page.0", in: results.view) as? UITableView)
        let loaded = expectation(for: NSPredicate { _, _ in
            MainActor.assumeIsolated {
                results.view.layoutIfNeeded()
                return table.visibleCells.contains { $0 is MusicSearchSongCell }
            }
        }, evaluatedWith: table)
        await fulfillment(of: [loaded], timeout: 2)
        history.record("较新的关键词")
        let reads = await service.resultReads(), entries = history.entries
        results.query = "尚未提交的新关键词"
        model.setAvailable(false)
        let cleared = expectation(for: NSPredicate { _, _ in
            MainActor.assumeIsolated {
                results.view.layoutIfNeeded()
                return table.numberOfSections == 2 && table.numberOfRows(inSection: 0) == 1 &&
                    table.numberOfRows(inSection: 1) == 1 &&
                    !table.visibleCells.contains { $0 is MusicSearchSongCell }
            }
        }, evaluatedWith: table)
        await fulfillment(of: [cleared], timeout: 2)
        model.accountSessionsChanged()
        await deliverMainRunLoop()
        let unavailableReads = await service.resultReads()
        XCTAssertEqual(unavailableReads, reads)
        XCTAssertTrue(results.isPageActive); XCTAssertEqual(results.query, "尚未提交的新关键词")
        XCTAssertEqual(history.entries, entries)
        let restoredReads = expectation(description: "search resumes only when access returns")
        restoredReads.expectedFulfillmentCount = 2
        await service.allowResultSearch { restoredReads.fulfill() }
        model.setAvailable(true)
        await fulfillment(of: [restoredReads], timeout: 2)
        let restored = expectation(for: NSPredicate { _, _ in
            MainActor.assumeIsolated {
                results.view.layoutIfNeeded()
                return table.visibleCells.contains { $0 is MusicSearchSongCell }
            }
        }, evaluatedWith: table)
        await fulfillment(of: [restored], timeout: 2)
        await deliverMainRunLoop()
        let availableReads = await service.resultReads()
        XCTAssertEqual(availableReads.count, reads.count + 2)
        XCTAssertTrue(availableReads.suffix(2).allSatisfy { $0.query == "原关键词" })
        XCTAssertEqual(results.query, "尚未提交的新关键词")
        XCTAssertEqual(history.entries, entries)
        await model.close()
    }
    func testInactiveSearchResultsDoNotResubmitAfterAccountGenerationChanges() async throws {
        let suite = "MusicUIKitRegressionTests.search-inactive." + UUID().uuidString
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        defer { defaults.removePersistentDomain(forName: suite) }
        let restorePreferences = try useSearchResultPreferences()
        defer { restorePreferences() }
        let history = SearchHistoryStore(defaults: defaults), service = MusicUIKitRegressionService()
        let firstReads = expectation(description: "initial search reads")
        firstReads.expectedFulfillmentCount = 2
        await service.allowResultSearch { firstReads.fulfill() }
        let model = MusicViewModel(service: service, player: MusicUIKitRegressionPlayer())
        let controller = MusicSearchViewController(model: model, appearance: MusicAppearancePreferencesStore(),
                                                   router: MusicUIKitRegressionRouter(), history: history)
        let navigation = PageNavigationController(themeProvider: MusicAppearancePreferencesStore())
        navigation.setViewControllers([controller], animated: false)
        let window = mount(navigation)
        defer { release(navigation, window: window) }
        controller.query = "原关键词"; controller.onSubmit?("原关键词")
        await fulfillment(of: [firstReads], timeout: 2)
        await deliverMainRunLoop()
        let results = try XCTUnwrap(navigation.topViewController as? SearchPageViewController)
        let reads = await service.resultReads(), entries = history.entries
        navigation.popViewController(animated: false)
        navigation.view.layoutIfNeeded()
        await deliverMainRunLoop()
        XCTAssertFalse(results.isPageActive)
        model.accountSessionsChanged()
        await deliverMainRunLoop()
        let inactiveReads = await service.resultReads()
        XCTAssertEqual(inactiveReads, reads); XCTAssertEqual(history.entries, entries)
        await model.close()
    }
    func testPlaylistStaleResultFooterUsesTheUnifiedRetryLabel() async throws {
        let suite = "MusicUIKitRegressionTests.search-retry." + UUID().uuidString
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        defer { defaults.removePersistentDomain(forName: suite) }
        let restorePreferences = try useSearchResultPreferences()
        defer { restorePreferences() }
        let service = MusicUIKitRegressionService()
        let firstReads = expectation(description: "song result and stale playlist failure")
        firstReads.expectedFulfillmentCount = 2
        await service.allowResultSearch(playlistFailure: .staleResult) { firstReads.fulfill() }
        let model = MusicViewModel(service: service, player: MusicUIKitRegressionPlayer())
        let controller = MusicSearchViewController(model: model, appearance: MusicAppearancePreferencesStore(),
            router: MusicUIKitRegressionRouter(), history: SearchHistoryStore(defaults: defaults))
        let navigation = PageNavigationController(themeProvider: MusicAppearancePreferencesStore())
        navigation.setViewControllers([controller], animated: false)
        let window = mount(navigation)
        defer { release(navigation, window: window) }
        controller.query = "原关键词"; controller.onSubmit?("原关键词")
        await fulfillment(of: [firstReads], timeout: 2)
        let results = try XCTUnwrap(navigation.topViewController as? SearchPageViewController)
        let footer = expectation(for: NSPredicate { _, _ in
            MainActor.assumeIsolated {
                results.view.layoutIfNeeded()
                return self.identified("music.search.results.action.0.1", in: results.view) != nil
            }
        }, evaluatedWith: results)
        await fulfillment(of: [footer], timeout: 2)
        let button = try XCTUnwrap(identified("music.search.results.action.0.1", in: results.view) as? UIButton)
        XCTAssertEqual(button.title(for: .normal), "重试")
        await model.close()
    }

    func testSearchSourceMergeRefreshesVisibleRowMenuAndActions() async throws {
        let service = MusicUIKitRegressionService()
        let primary = resultTrack("wy", "primary", title: "同一录音")
        let added = resultTrack("tx", "added", title: "同一录音")
        await service.configureResultPages(songs: [
            "wy": [[primary]], "tx": [[resultTrack("tx", "other")], [added]],
        ])
        try await withSearchResults(service, preferences: .init(songPlatforms: [.netease, .qq],
            playlistPlatforms: [])) { results, _, _ in
            let table = try self.resultTable(0, in: results)
            let cell = try XCTUnwrap(table.visibleCells.compactMap { $0 as? MusicSearchSongCell }
                .first { self.descendants($0).compactMap { $0 as? UILabel }.contains { $0.text == primary.title } })
            let more = try self.resultButton(prefix: "music.search.song.more.", in: cell)
            let oldMenu = try XCTUnwrap(more.menu)
            try self.resultButton(id: "music.search.results.action.0.0", in: results.view)
                .sendActions(for: .touchUpInside)
            await self.waitForResult { more.menu !== oldMenu }
            XCTAssertTrue(table.visibleCells.contains { $0 === cell }, "来源补入不能靠滚出复用才生效")

            let sourceAction = try XCTUnwrap(more.menu?.children.compactMap { $0 as? UIAction }
                .first { $0.title == "音源" })
            let sourceRead = self.expectation(description: "source inspection consumes both origins")
            await service.observeRecordingReads { sourceRead.fulfill() }
            self.invoke(sourceAction)
            await self.waitForResult {
                (results.presentedViewController as? UINavigationController)?
                    .topViewController is MusicSourcesViewController
            }
            NotificationCenter.default.post(name: UIApplication.didBecomeActiveNotification, object: nil)
            await self.fulfillment(of: [sourceRead], timeout: 3)
            await self.waitForResult { results.presentedViewController?.isBeingPresented == false }
            await service.observeRecordingReads(nil)
            let inspected = await service.recordingReads()
            XCTAssertEqual(inspected.first?.targets.first?.origins.map(\.platform), [.netease, .qq])
            await withCheckedContinuation { continuation in
                results.dismiss(animated: false) { continuation.resume() }
            }
            let closes = await service.inspectionCloseCount()
            XCTAssertGreaterThan(closes, 0, "关闭音源页必须释放检查会话")
            await self.waitForResult { results.canPresentPageAlert }

            let appended = self.expectation(description: "append resolves updated sources")
            await service.observeRecordingReads { appended.fulfill() }
            try self.resultButton(prefix: "music.search.song.append.", in: cell)
                .sendActions(for: .touchUpInside)
            await self.fulfillment(of: [appended], timeout: 3)
            await service.observeRecordingReads(nil)
            await self.deliverMainRunLoop(); await self.deliverMainRunLoop()
            await self.waitForResult {
                self.matchingResultButton(prefix: "music.search.song.append.", in: cell)?.isEnabled == true
            }
            let favorited = self.expectation(description: "favorite resolves updated sources")
            await service.observeRecordingReads { favorited.fulfill() }
            try self.resultButton(prefix: "music.search.song.favorite.", in: cell)
                .sendActions(for: .touchUpInside)
            await self.fulfillment(of: [favorited], timeout: 3)
            await service.observeRecordingReads(nil)
            await self.deliverMainRunLoop(); await self.deliverMainRunLoop()
            await self.waitForResult {
                self.matchingResultButton(prefix: "music.search.song.info.", in: cell)?.isEnabled == true
            }
            let played = self.expectation(description: "single play consumes updated sources")
            await service.observeBatchPlays { played.fulfill() }
            try self.resultButton(prefix: "music.search.song.info.", in: cell)
                .sendActions(for: .touchUpInside)
            await self.fulfillment(of: [played], timeout: 3)
            let recordings = await service.recordingReads(), batches = await service.batchPlays()
            XCTAssertEqual(recordings.count, 3)
            XCTAssertTrue(recordings.allSatisfy { $0.targets.first?.origins.map(\.platform) == [.netease, .qq] })
            XCTAssertEqual(batches.first?.targets.first?.origins.map(\.platform), [.netease, .qq])
        }
    }

    func testUnchangedSearchStateAndPlaylistLoadingDoNotReconfigureSongHeadingOrRows() async throws {
        let service = MusicUIKitRegressionService()
        let entered = expectation(description: "playlist next page pending")
        let gate = MusicUIKitSearchGate { entered.fulfill() }
        await service.configureResultPages(songs: ["tx": [[resultTrack("tx", "kept")]]],
            playlists: ["tx": [[resultPlaylist("one")], [resultPlaylist("two")]]])
        await service.blockResultPage(.playlists, page: 2, gate: gate)
        try await withSearchResults(service) { results, _, _ in
            let table = try self.resultTable(0, in: results)
            let heading = try XCTUnwrap(self.identified("music.search.results.section.title", in: table) as? UILabel)
            let song = try XCTUnwrap(table.visibleCells.first { $0 is MusicSearchSongCell })
            let menu = try XCTUnwrap(self.resultButton(prefix: "music.search.song.more.", in: song).menu)
            var headingWrites = 0
            let observation = heading.observe(\.text) { _, _ in
                MainActor.assumeIsolated { headingWrites += 1 }
            }
            defer { observation.invalidate() }
            table.delegate?.scrollViewWillBeginDragging?(table)
            table.delegate?.scrollViewWillBeginDragging?(table)
            await self.deliverMainRunLoop(); await self.deliverMainRunLoop()
            XCTAssertEqual(headingWrites, 0, "同等状态不能重复配置标题")
            try self.resultButton(id: "music.search.results.action.0.1", in: results.view)
                .sendActions(for: .touchUpInside)
            await self.fulfillment(of: [entered], timeout: 3)
            await self.waitForResult {
                (self.identified("music.search.results.action.0.1",
                                 in: results.view) as? UIButton)?.title(for: .normal) == "取消"
            }
            XCTAssertEqual(headingWrites, 0, "歌单加载状态不能重配单曲标题")
            XCTAssertTrue(try self.resultButton(prefix: "music.search.song.more.", in: song).menu === menu)
            await gate.release()
            await self.waitForResult { table.numberOfRows(inSection: 1) == 3 }
            XCTAssertEqual(headingWrites, 0)
            let reads = await service.resultReads()
            XCTAssertEqual(reads.filter { $0.section == .songs }.count, 1)
            XCTAssertEqual(reads.filter { $0.section == .playlists }.count, 2)
        }
    }

    func testSearchTabSwitchingDoesNotPageShortListsUntilNewVerticalDrag() async throws {
        let service = MusicUIKitRegressionService()
        await service.configureResultPages(songs: ["tx": [
            [resultTrack("tx", "one")],
            [resultTrack("tx", "two")] + (2..<30).map { resultTrack("tx", "row-\($0)") },
            [resultTrack("tx", "three")],
        ]], playlists: ["tx": [[resultPlaylist("one")], [resultPlaylist("two")]]])
        try await withSearchResults(service) { results, _, _ in
            for index in [1, 2, 0, 1] {
                let table = try await self.selectResultTab(index, in: results)
                self.displayLastResult(in: table, section: index == 2 ? 1 : 0)
                await self.deliverMainRunLoop(); await self.deliverMainRunLoop()
            }
            let before = await service.resultReads()
            XCTAssertEqual(before.count, 2, "切标签或显示末行不产生下一轮请求")
            let table = try self.resultTable(1, in: results)
            let next = self.expectation(description: "vertical drag permits song pagination")
            await service.allowResultSearch { next.fulfill() }
            table.delegate?.scrollViewWillBeginDragging?(table)
            self.displayLastResult(in: table, section: 0)
            await self.fulfillment(of: [next], timeout: 3)
            await service.allowResultSearch {}
            await self.waitForResult {
                self.descendants(table).compactMap { $0 as? UILabel }.contains { $0.text == "two" }
            }
            _ = try await self.selectResultTab(2, in: results)
            _ = try await self.selectResultTab(1, in: results)
            self.displayLastResult(in: table, section: 0)
            await self.deliverMainRunLoop(); await self.deliverMainRunLoop()
            let after = await service.resultReads()
            XCTAssertEqual(after.count, 3)
            XCTAssertEqual(after.last?.section, .songs)
            XCTAssertEqual(after.last?.page, 2)
        }
    }

    func testSearchAppendPreservesVisibleRowsOffsetAndTabReturn() async throws {
        let service = MusicUIKitRegressionService()
        let entered = expectation(description: "song next page pending")
        let gate = MusicUIKitSearchGate { entered.fulfill() }
        await service.configureResultPages(songs: ["tx": [
            (0..<30).map { resultTrack("tx", "row-\($0)") },
            (30..<60).map { resultTrack("tx", "row-\($0)") },
        ]])
        await service.blockResultPage(.songs, page: 2, gate: gate)
        try await withSearchResults(service) { results, _, _ in
            let table = try await self.selectResultTab(1, in: results)
            table.scrollToRow(at: IndexPath(row: table.numberOfRows(inSection: 0) - 1, section: 0),
                              at: .bottom, animated: false)
            table.layoutIfNeeded()
            await self.deliverMainRunLoop()
            let offset = table.contentOffset
            let visible = table.indexPathsForVisibleRows ?? []
            let cells = visible.compactMap { table.cellForRow(at: $0) as? MusicSearchSongCell }
            let songPaths = visible.filter { table.cellForRow(at: $0) is MusicSearchSongCell }
            let frames = songPaths.map { table.rectForRow(at: $0) }
            let titles = cells.map { self.descendants($0).compactMap { $0 as? UILabel }.compactMap(\.text) }
            let next = try self.resultButton(id: "music.search.results.action.1.0", in: table)
            next.sendActions(for: .touchUpInside)
            await self.fulfillment(of: [entered], timeout: 3)
            await self.deliverMainRunLoop(); table.layoutIfNeeded()
            XCTAssertEqual(table.contentOffset.y, offset.y, accuracy: 0.5)
            XCTAssertEqual(songPaths.map { table.rectForRow(at: $0) }, frames)
            await gate.release()
            await self.waitForResult { table.numberOfRows(inSection: 0) == 61 }
            table.layoutIfNeeded()
            XCTAssertEqual(table.contentOffset.y, offset.y, accuracy: 0.5)
            XCTAssertEqual(songPaths.map { table.rectForRow(at: $0) }, frames)
            XCTAssertEqual(cells.map { self.descendants($0).compactMap { $0 as? UILabel }.compactMap(\.text) }, titles)
            XCTAssertTrue(cells.allSatisfy { cell in table.visibleCells.contains { $0 === cell } })
            _ = try await self.selectResultTab(2, in: results)
            _ = try await self.selectResultTab(1, in: results)
            XCTAssertEqual(table.contentOffset.y, offset.y, accuracy: 0.5)
            let reads = await service.resultReads()
            XCTAssertEqual(reads.filter { $0.section == .songs }.map(\.page), [1, 2])
        }
    }

    func testSearchHorizontalTabTransitionDoesNotReadAndOldMenuCannotCrossNewQuery() async throws {
        let service = MusicUIKitRegressionService()
        await service.configureResultPages(songs: ["tx": [[resultTrack("tx", "one")], [resultTrack("tx", "two")]]],
            playlists: ["tx": [[resultPlaylist("one")], [resultPlaylist("two")]]])
        try await withSearchResults(service) { results, _, _ in
            let songs = try await self.selectResultTab(1, in: results)
            let oldMenu = try XCTUnwrap(self.resultButton(prefix: "music.search.song.more.", in: songs).menu)
            songs.delegate?.scrollViewWillBeginDragging?(songs)
            let pager = try XCTUnwrap(results.children.compactMap { $0 as? UIPageViewController }.first)
            let previous = try XCTUnwrap(pager.viewControllers?.first)
            let target = try XCTUnwrap(pager.dataSource?.pageViewController(pager, viewControllerAfter: previous))
            pager.delegate?.pageViewController?(pager, willTransitionTo: [target])
            pager.setViewControllers([target], direction: .forward, animated: false)
            pager.delegate?.pageViewController?(pager, didFinishAnimating: true,
                previousViewControllers: [previous], transitionCompleted: true)
            results.view.layoutIfNeeded()
            let playlists = try self.resultTable(2, in: results)
            self.displayLastResult(in: playlists, section: 1)
            await self.deliverMainRunLoop(); await self.deliverMainRunLoop()
            let before = await service.resultReads()
            XCTAssertEqual(before.count, 2, "横向分页不能继承上一分类的纵向加载意图")

            let newReads = self.expectation(description: "new query reads both types once")
            newReads.expectedFulfillmentCount = 2
            await service.allowResultSearch { newReads.fulfill() }
            results.query = "新关键词"; results.onSubmit?("新关键词")
            await self.fulfillment(of: [newReads], timeout: 3)
            await service.allowResultSearch {}
            _ = try await self.selectResultTab(1, in: results)
            await self.waitForResult {
                guard let menu = self.matchingResultButton(prefix: "music.search.song.more.", in: songs)?.menu else {
                    return false
                }
                return menu !== oldMenu
            }
            let oldSource = try XCTUnwrap(oldMenu.children.compactMap { $0 as? UIAction }
                .first { $0.title == "音源" })
            self.invoke(oldSource)
            await self.deliverMainRunLoop()
            XCTAssertNil(results.presentedViewController)
            let recordings = await service.recordingReads()
            XCTAssertTrue(recordings.isEmpty)
            let reads = await service.resultReads()
            XCTAssertEqual(reads.map(\.query), ["测试录音", "测试录音", "新关键词", "新关键词"])
        }
    }

    func testSearchPlaylistLayoutAndAccessClearUpdateAllRetainedPages() async throws {
        let service = MusicUIKitRegressionService()
        await service.configureResultPages(songs: ["tx": [[resultTrack("tx", "same")]]],
            playlists: ["tx": [(0..<8).map { resultPlaylist("list-\($0)") }]])
        try await withSearchResults(service) { results, model, appearance in
            let all = try self.resultTable(0, in: results)
            let oldMenu = try XCTUnwrap(self.resultButton(prefix: "music.search.song.more.", in: all).menu)
            let songs = try await self.selectResultTab(1, in: results)
            let playlists = try await self.selectResultTab(2, in: results)
            XCTAssertLessThan(playlists.numberOfRows(inSection: 1), 8)
            appearance.set(\.playlistView, .list)
            await self.waitForResult { playlists.numberOfRows(inSection: 1) == 8 }
            appearance.set(\.playlistView, .grid)
            await self.waitForResult { playlists.numberOfRows(inSection: 1) < 8 }

            model.setAvailable(false)
            await self.waitForResult {
                [all, songs].allSatisfy { $0.numberOfRows(inSection: 0) == 1 } &&
                    playlists.numberOfRows(inSection: 1) == 0
            }
            let oldSource = try XCTUnwrap(oldMenu.children.compactMap { $0 as? UIAction }
                .first { $0.title == "音源" })
            self.invoke(oldSource)
            await self.deliverMainRunLoop()
            XCTAssertNil(results.presentedViewController)
            let reads = await service.recordingReads()
            XCTAssertTrue(reads.isEmpty)
            _ = try await self.selectResultTab(0, in: results)
            let play = try self.resultButton(id: "music.search.results.section.play", in: all)
            XCTAssertTrue(play.isHidden)
            model.setAvailable(true)
            await self.waitForResult {
                all.visibleCells.contains {
                    (self.identified("music.search.results.section.play", in: $0) as? UIButton)?.isHidden == false
                }
            }
            XCTAssertTrue(all.visibleCells.contains {
                (self.identified("music.search.results.section.play", in: $0) as? UIButton)?.isHidden == false
            })
        }
    }

    func testChannelTabsKeepDirectoryOrderAndCenterTheCurrentChannel() throws {
        let channels: [MusicPublicBrowseChannel] = [
            .init(id: "chart", title: "排行榜", kind: .rankings),
            .init(id: "vip", title: "VIP", kind: .vip),
            .init(id: "playlist", title: "歌单", kind: .playlists),
            .init(id: "western", title: "欧美", kind: .cube),
            .init(id: "electronic", title: "电音", kind: .cube),
        ]
        let tabs = MusicChannelTabsView(frame: CGRect(x: 0, y: 0, width: 220, height: 52))
        var selected: MusicPublicBrowseChannel?
        tabs.configure(channels: channels, selectedID: channels.last?.identity, theme: PageTheme()) {
            selected = $0
        }
        tabs.layoutIfNeeded()
        let buttons = descendants(tabs).compactMap { $0 as? UIButton }
            .filter { $0.accessibilityIdentifier?.hasPrefix("music.channel.tab.") == true }
            .sorted { $0.frame.minX < $1.frame.minX }
        XCTAssertEqual(buttons.compactMap { $0.title(for: .normal) }, channels.map(\.title))
        XCTAssertTrue(try XCTUnwrap(buttons.last).accessibilityTraits.contains(.selected))
        let scroll = try XCTUnwrap(descendants(tabs).compactMap { $0 as? UIScrollView }.first)
        XCTAssertGreaterThan(scroll.contentOffset.x, 0)
        try XCTUnwrap(buttons.first { $0.title(for: .normal) == "歌单" }).sendActions(for: .touchUpInside)
        XCTAssertEqual(selected, channels[2])
        let renamed = channels.enumerated().map { index, channel in
            index == 2 ? MusicPublicBrowseChannel(
                id: channel.id, title: "精选歌单", kind: channel.kind,
                pageCode: channel.pageCode, platform: channel.platform
            ) : channel
        }
        tabs.configure(channels: renamed, selectedID: renamed[2].identity, theme: PageTheme()) { selected = $0 }
        tabs.layoutIfNeeded()
        let updated = descendants(tabs).compactMap { $0 as? UIButton }.filter {
            $0.accessibilityIdentifier?.hasPrefix("music.channel.tab.") == true
        }
        XCTAssertNotNil(updated.first { $0.title(for: .normal) == "精选歌单" })
        XCTAssertNil(updated.first { $0.title(for: .normal) == "歌单" })
    }

    func testChannelSwitcherLivesInChromeWhilePagerFillsContent() throws {
        let channels: [MusicPublicBrowseChannel] = [
            .init(id: "chart", title: "排行榜", kind: .rankings),
            .init(id: "playlist", title: "歌单", kind: .playlists),
        ]
        let service = MusicUIKitRegressionService(browse: .init(sections: [
            .init(id: "directory", title: "频道", items: channels.map {
                .init(id: $0.identity, title: $0.title, content: .channel($0))
            }, layout: .playlistRail),
        ]))
        let model = MusicViewModel(service: service, player: MusicUIKitRegressionPlayer())
        let router = MusicUIKitRegressionRouter()
        let appearance = MusicAppearancePreferencesStore()
        let host = PageViewController(themeProvider: appearance)
        let controller = MusicChannelsViewController(
            model: model, browser: service, appearance: appearance,
            router: router
        )
        host.loadViewIfNeeded()
        host.addChild(controller)
        host.view.addSubview(controller.view)
        controller.view.translatesAutoresizingMaskIntoConstraints = false
        NSLayoutConstraint.activate([
            controller.view.leadingAnchor.constraint(equalTo: host.view.leadingAnchor),
            controller.view.trailingAnchor.constraint(equalTo: host.view.trailingAnchor),
            controller.view.topAnchor.constraint(equalTo: host.contentTopAnchor),
            controller.view.bottomAnchor.constraint(equalTo: host.view.bottomAnchor),
        ])
        controller.didMove(toParent: host)
        let window = mount(host)
        defer { release(host, window: window) }
        controller.view.layoutIfNeeded()
        let switcher = controller.channelSwitcherView
        XCTAssertEqual(switcher.accessibilityIdentifier, "music.channel.switcher")
        XCTAssertFalse(switcher.isDescendant(of: controller.view))
        let pager = try XCTUnwrap(controller.children.compactMap { $0 as? UIPageViewController }.first)
        XCTAssertEqual(pager.view.frame, controller.view.bounds)
    }

    func testChannelPickerPresentsFromTopAsCustomSheet() async throws {
        let channels: [MusicPublicBrowseChannel] = [
            .init(id: "chart", title: "排行榜", kind: .rankings),
            .init(id: "electronic", title: "电音", kind: .cube),
        ]
        let service = MusicUIKitRegressionService(browse: .init(sections: [
            .init(id: "directory", title: "频道", items: channels.map {
                .init(id: $0.identity, title: $0.title, content: .channel($0))
            }, layout: .playlistRail),
        ]))
        let model = MusicViewModel(service: service, player: MusicUIKitRegressionPlayer())
        let controller = MusicChannelsViewController(
            model: model, browser: service, appearance: MusicAppearancePreferencesStore(),
            router: MusicUIKitRegressionRouter()
        )
        controller.select(channels[0])
        let window = mount(controller)
        defer { release(controller, window: window) }
        let ready = expectation(for: NSPredicate { _, _ in
            MainActor.assumeIsolated { controller.isPageVisible && controller.canPresentPageAlert }
        }, evaluatedWith: controller)
        await fulfillment(of: [ready], timeout: 3)

        controller.showPicker()
        let presented = expectation(for: NSPredicate { _, _ in
            MainActor.assumeIsolated {
                guard let picker = controller.presentedViewController as? MusicChannelsPickerViewController else {
                    return false
                }
                return !picker.isBeingPresented && picker.viewIfLoaded?.window != nil &&
                    picker.transitionCoordinator == nil
            }
        }, evaluatedWith: controller)
        await fulfillment(of: [presented], timeout: 3)
        let picker = try XCTUnwrap(controller.presentedViewController as? MusicChannelsPickerViewController)

        let safeTop = window.safeAreaInsets.top
        XCTAssertEqual(picker.modalPresentationStyle, .custom)
        XCTAssertEqual(picker.view.frame.minY, window.bounds.minY, accuracy: 0.5)
        XCTAssertEqual(picker.view.frame.height, picker.preferredHeight + safeTop, accuracy: 1)
        XCTAssertLessThan(picker.view.frame.height, window.bounds.height * 0.72 + 1)
        XCTAssertEqual(picker.view.layer.cornerRadius, 24)
        XCTAssertEqual(picker.view.accessibilityIdentifier, "music.channels.picker")
        XCTAssertFalse(picker.hasOwnPageHeader)
        if let close = descendants(picker.view).first(where: {
            $0.accessibilityIdentifier == "music.channels.picker.close"
        }) {
            let frame = close.convert(close.bounds, to: window)
            XCTAssertGreaterThanOrEqual(frame.minY, safeTop - 0.5)
            XCTAssertLessThanOrEqual(frame.minY, safeTop + 20.5)
        } else {
            XCTAssertFalse(descendants(picker.view).contains { $0 is PageHeaderView })
        }
        XCTAssertFalse(try XCTUnwrap(picker.presentationController).shouldRemovePresentersView)

        await withCheckedContinuation { continuation in
            controller.dismiss(animated: false) { continuation.resume() }
        }
    }

    func testChannelPagerLoadsOnlyShownPagesAndReusesContentOnReturn() async throws {
        let remembered = UserDefaults.standard.object(forKey: "music.ui.lastChannel")
        defer {
            if let remembered { UserDefaults.standard.set(remembered, forKey: "music.ui.lastChannel") }
            else { UserDefaults.standard.removeObject(forKey: "music.ui.lastChannel") }
        }
        let channels: [MusicPublicBrowseChannel] = [
            .init(id: "chart", title: "排行榜", kind: .rankings),
            .init(id: "electronic", title: "电音", kind: .cube),
        ]
        let service = MusicUIKitRegressionService(browse: .init(sections: [
            .init(id: "directory", title: "频道", items: channels.map {
                .init(id: $0.identity, title: $0.title, content: .channel($0))
            }, layout: .playlistRail),
        ]), channelBrowse: .init(sections: [
            .init(id: "cards", title: "动态栏目", items: [card("content", previews: [])], layout: .playlistRail),
        ]))
        let model = MusicViewModel(service: service, player: MusicUIKitRegressionPlayer())
        let router = MusicUIKitRegressionRouter()
        let controller = MusicChannelsViewController(
            model: model, browser: service, appearance: MusicAppearancePreferencesStore(), router: router
        )
        controller.select(channels[0])
        let window = mount(controller)
        defer { release(controller, window: window) }
        let pager = try XCTUnwrap(controller.children.compactMap { $0 as? UIPageViewController }.first)
        var activated: UIViewController?
        let loadedPage: @MainActor () -> MusicBrowseViewController? = {
            guard let page = pager.viewControllers?.first as? MusicBrowseViewController,
                  let collection = page.view.subviews.compactMap({ $0 as? UICollectionView }).first else { return nil }
            page.view.layoutIfNeeded()
            collection.layoutIfNeeded()
            if page.view.window != nil, activated !== page {
                activated = page
                NotificationCenter.default.post(name: UIApplication.didBecomeActiveNotification, object: nil)
            }
            return collection.visibleCells.isEmpty ? nil : page
        }
        let firstLoaded = expectation(for: NSPredicate { _, _ in
            MainActor.assumeIsolated { loadedPage() != nil }
        }, evaluatedWith: pager)
        await fulfillment(of: [firstLoaded], timeout: 3)
        let first = try XCTUnwrap(loadedPage())
        let neighbor = try XCTUnwrap(controller.pageViewController(pager, viewControllerAfter: first))
        XCTAssertFalse(try XCTUnwrap(neighbor as? PageViewController).isPageActive)
        let initialReads = await service.discoveryRequests
        XCTAssertEqual(initialReads, [.channels, .channel(channels[0], tag: "")])

        controller.select(channels[1])
        let secondLoaded = expectation(for: NSPredicate { _, _ in
            MainActor.assumeIsolated { loadedPage() === neighbor }
        }, evaluatedWith: pager)
        await fulfillment(of: [secondLoaded], timeout: 3)
        XCTAssertTrue(pager.viewControllers?.first === neighbor)
        let selectedReads = await service.discoveryRequests
        XCTAssertEqual(selectedReads, initialReads + [.channel(channels[1], tag: "")])

        controller.select(channels[0])
        let returned = expectation(for: NSPredicate { _, _ in
            MainActor.assumeIsolated { loadedPage() === first }
        }, evaluatedWith: pager)
        await fulfillment(of: [returned], timeout: 3)
        XCTAssertTrue(pager.viewControllers?.first === first)
        let returnedReads = await service.discoveryRequests
        XCTAssertEqual(returnedReads, selectedReads)
        await model.close()
    }

    func testRankingCardKeepsOneCoverAndOnePlayButtonWithoutExtraMasks() throws {
        let collection = MusicPublicBrowseCollection(platform: .netease, nativeID: "chart", title: "热歌榜")
        let urls = try (1...3).map {
            try XCTUnwrap(URL(string: "synthetic-artwork://ranking-mask-\($0)"))
        }
        let ranking = MusicPublicBrowseRanking(preview: [
            .init(id: "one", title: "第一首", artist: "歌手甲", artwork: urls[0], rank: 1),
            .init(id: "two", title: "第二首", artist: "歌手乙", artwork: urls[1], rank: 2),
            .init(id: "three", title: "第三首", artist: "歌手丙", artwork: urls[2], rank: 3),
        ])
        let item = MusicPublicBrowseItem(id: collection.id, title: "热歌榜",
                                         content: .collection(collection), ranking: ranking)
        let cell = MusicBrowseCell(frame: CGRect(x: 0, y: 0, width: 358, height: 166))
        cell.configure(item: item, layout: .rankingStack, theme: PageTheme(), onPlay: {})
        cell.layoutIfNeeded()
        let covers = descendants(cell).compactMap { $0 as? MusicArtworkView }.filter { !$0.isHidden }
        XCTAssertEqual(covers.count, 1)
        XCTAssertTrue(covers.allSatisfy { $0.alpha == 1 })
        let buttons = descendants(cell).compactMap { $0 as? UIButton }.filter { !$0.isHidden }
        XCTAssertEqual(buttons.filter { $0.accessibilityIdentifier == "music.browse.play.\(collection.id)" }.count, 1)
    }

    func testChannelSwitcherRemainsIndependentFromExpandedTags() throws {
        let filters = (0..<12).map {
            MusicPublicBrowseFilter(id: "tag-\($0)", title: "标签\($0)")
        }
        let tabs = MusicChannelTabsView(frame: CGRect(x: 0, y: 0, width: 280, height: 44))
        let channel = MusicPublicBrowseChannel(id: "chart", title: "排行榜", kind: .rankings)
        tabs.configure(channels: [channel], selectedID: channel.identity, theme: PageTheme()) { _ in }
        tabs.layoutIfNeeded()
        let initialHeight = tabs.bounds.height
        let tags = MusicChannelTagsView(themeProvider: MusicAppearancePreferencesStore())
        tags.frame = CGRect(x: 0, y: 0, width: 280, height: 48)
        tags.configure(filters: filters, selectedID: filters[0].id, theme: PageTheme()) { _ in }
        let collapsed = tags.preferredHeight(for: tags.bounds.width)
        tags.frame.size.height = collapsed
        tags.layoutIfNeeded()
        let toggle = try XCTUnwrap(descendants(tags).compactMap { $0 as? UIButton }.first {
            $0.accessibilityIdentifier == "music.channel.tags.toggle"
        })
        toggle.sendActions(for: .touchUpInside)
        let expanded = tags.preferredHeight(for: tags.bounds.width)
        tags.frame.size.height = expanded
        tags.layoutIfNeeded()
        XCTAssertGreaterThan(expanded, collapsed)
        XCTAssertEqual(tabs.bounds.height, initialHeight)
        XCTAssertEqual(tabs.accessibilityIdentifier, "music.channel.switcher")
    }

    func testChannelTagsExpandIntoTheirOwnRowsWithoutCompressingButtons() throws {
        let filters = [
            "全部", "Phonk", "House", "Bass", "Techno", "Trance", "百大 DJ", "厂牌精选", "场景歌单",
        ].enumerated().map { MusicPublicBrowseFilter(id: String($0.offset), title: $0.element) }
        let tags = MusicChannelTagsView(themeProvider: MusicAppearancePreferencesStore())
        tags.frame = CGRect(x: 0, y: 0, width: 390, height: 48)
        var selected = ""
        tags.configure(filters: filters, selectedID: "0", theme: PageTheme()) { selected = $0 }
        let collapsed = tags.preferredHeight(for: tags.bounds.width)
        XCTAssertEqual(tags.preferredHeight(for: tags.bounds.width), collapsed)
        XCTAssertTrue(tags.hasOverflow)
        XCTAssertFalse(tags.isExpanded)
        tags.frame.size.height = collapsed
        tags.layoutIfNeeded()
        XCTAssertEqual(collapsed, 56)
        XCTAssertTrue(tags.hasOverflow)
        let toggle = try XCTUnwrap(descendants(tags).compactMap { $0 as? UIButton }.first {
            $0.accessibilityIdentifier == "music.channel.tags.toggle"
        })
        let all = try XCTUnwrap(descendants(tags).compactMap { $0 as? UIButton }.first {
            $0.title(for: .normal) == "全部"
        })
        XCTAssertEqual(all.convert(all.bounds, to: tags).minX, 16, accuracy: 0.5)
        XCTAssertEqual(all.convert(all.bounds, to: tags).midY, toggle.frame.midY, accuracy: 0.5)
        toggle.layoutIfNeeded()
        let icon = try XCTUnwrap(toggle.imageView)
        let iconFrame = icon.convert(icon.bounds, to: tags)
        XCTAssertEqual(tags.bounds.maxX - iconFrame.maxX, 16, accuracy: 0.5)
        toggle.sendActions(for: .touchUpInside)
        let expanded = tags.preferredHeight(for: tags.bounds.width)
        XCTAssertEqual(tags.preferredHeight(for: tags.bounds.width), expanded)
        XCTAssertTrue(tags.isExpanded)
        tags.frame.size.height = expanded
        tags.layoutIfNeeded()
        XCTAssertTrue(tags.isExpanded)
        XCTAssertGreaterThan(expanded, collapsed)
        let buttons = descendants(tags).compactMap { $0 as? UIButton }.filter {
            $0.accessibilityIdentifier?.hasPrefix("music.channel.tag.") == true
        }
        let rows = Set(buttons.map { $0.convert($0.bounds, to: tags).minY.rounded() })
        XCTAssertGreaterThan(rows.count, 1)
        XCTAssertTrue(buttons.allSatisfy { $0.bounds.height == 30 })
        XCTAssertTrue(buttons.allSatisfy {
            $0.convert($0.bounds, to: tags).maxX <= toggle.frame.minX + 0.5
        })
        let minimumY = buttons.map { $0.convert($0.bounds, to: tags).minY }.min() ?? 0
        let firstRow = buttons.filter {
            $0.convert($0.bounds, to: tags).minY.rounded() == minimumY.rounded()
        }
        XCTAssertTrue(firstRow.allSatisfy {
            abs($0.convert($0.bounds, to: tags).midY - toggle.frame.midY) < 0.5
        })
        try XCTUnwrap(buttons.first { $0.title(for: .normal) == "House" }).sendActions(for: .touchUpInside)
        XCTAssertEqual(selected, "2")
        XCTAssertFalse(tags.isExpanded)
        let renamed = filters.map {
            $0.id == "2" ? MusicPublicBrowseFilter(id: $0.id, title: "Deep House", group: $0.group) : $0
        }
        tags.configure(filters: renamed, selectedID: "2", theme: PageTheme()) { selected = $0 }
        tags.frame.size.height = tags.preferredHeight(for: tags.bounds.width)
        tags.layoutIfNeeded()
        let updated = descendants(tags).compactMap { $0 as? UIButton }.filter {
            $0.accessibilityIdentifier?.hasPrefix("music.channel.tag.") == true
        }
        XCTAssertNotNil(updated.first { $0.title(for: .normal) == "Deep House" })
        XCTAssertNil(updated.first { $0.title(for: .normal) == "House" })
        let many = (0..<40).map {
            MusicPublicBrowseFilter(id: "many-\($0)", title: "动态标签 \($0)")
        }
        tags.configure(filters: many, selectedID: many[0].id, theme: PageTheme()) { selected = $0 }
        tags.frame.size.height = tags.preferredHeight(for: tags.bounds.width)
        tags.layoutIfNeeded()
        toggle.sendActions(for: .touchUpInside)
        let capped = tags.preferredHeight(for: tags.bounds.width)
        tags.frame.size.height = capped
        tags.layoutIfNeeded()
        XCTAssertLessThanOrEqual(capped, 180)
        let scroll = try XCTUnwrap(descendants(tags).compactMap { $0 as? UIScrollView }.first)
        XCTAssertTrue(scroll.isScrollEnabled)
        XCTAssertGreaterThan(scroll.contentSize.height, scroll.bounds.height)
    }

    func testRankingStackUsesDedicatedCardWithThreeRealRankRows() throws {
        let collection = MusicPublicBrowseCollection(platform: .netease, nativeID: "chart", title: "热歌榜")
        let urls = try (1...3).map { index in
            try XCTUnwrap(URL(string: "synthetic-artwork://rank-\(index)"))
        }
        let ranking = MusicPublicBrowseRanking(preview: [
            .init(id: "one", title: "第一首", artist: "歌手甲", artwork: urls[0], rank: 1, lastRank: 1),
            .init(id: "two", title: "第二首", artist: "歌手乙", artwork: urls[1], rank: 2, lastRank: 4),
            .init(id: "three", title: "第三首", artist: "歌手丙", artwork: urls[2], rank: 3, lastRank: 0),
        ], updateFrequency: "每天更新")
        let item = MusicPublicBrowseItem(id: collection.id, title: "热歌榜", content: .collection(collection),
                                         ranking: ranking)
        let theme = PageTheme()
        let cell = MusicBrowseCell(frame: CGRect(x: 0, y: 0, width: 358, height: 166))
        cell.configure(item: item, layout: .rankingStack, theme: theme, onPlay: {})
        cell.layoutIfNeeded()
        XCTAssertEqual(cell.contentView.backgroundColor, UIColor(theme.surface))
        XCTAssertEqual(cell.contentView.layer.cornerRadius, 18)
        XCTAssertNotNil(descendants(cell).compactMap { $0 as? UILabel }.first { $0.text == "每天更新" })
        for id in ["one", "two", "three"] {
            XCTAssertNotNil(descendants(cell).first { $0.accessibilityIdentifier == "music.browse.ranking.\(id)" })
        }
        let movements = Set(descendants(cell).compactMap { ($0 as? UILabel)?.text })
        XCTAssertTrue(movements.isSuperset(of: Set(["−", "▴", "新"])))
        let covers = descendants(cell).compactMap { $0 as? MusicArtworkView }.filter { !$0.isHidden }
        XCTAssertEqual(covers.count, 1)
        let front = try XCTUnwrap(covers.first)
        let play = try XCTUnwrap(descendants(cell).compactMap { $0 as? UIButton }.first {
            $0.accessibilityIdentifier == "music.browse.play.\(collection.id)"
        })
        XCTAssertTrue(front.frame.contains(play.center))
        XCTAssertEqual(play.backgroundColor, .clear)
    }

    func testUnchangedPreviewsKeepTheirViewsAndUseUpdatedActions() throws {
        let cell = MusicBrowseCell(frame: CGRect(x: 0, y: 0, width: 320, height: 210))
        let a = track("a"), b = track("b"), c = track("c"), d = track("d")
        var oldCalls = 0, played: [String] = []
        cell.configure(item: card("card", previews: [a, b, c]), layout: .rankedSongs,
                       theme: PageTheme(), onPreview: { _ in oldCalls += 1 }, onPlay: {})
        let first = try preview("a", in: cell), third = try preview("c", in: cell)
        let cover = try XCTUnwrap(descendants(first).compactMap { $0 as? UIImageView }.first)
        let image = UIImage()
        cover.image = image
        cell.configure(item: card("card", previews: [a, d, c]), layout: .rankedSongs,
                       theme: PageTheme(palette: .mint), onPreview: { played.append($0.songId) }, onPlay: {})
        XCTAssertTrue(try preview("a", in: cell) === first)
        XCTAssertTrue(try preview("c", in: cell) === third)
        XCTAssertTrue(cover.image === image)
        XCTAssertFalse(descendants(cell).contains { $0.accessibilityLabel == "播放b" })
        first.sendActions(for: .touchUpInside)
        XCTAssertEqual(played, ["a"]); XCTAssertEqual(oldCalls, 0)
    }

    func testBrowseCardColorGradientAppearsOnlyWhenTextOverlaysArtwork() throws {
        let cell = MusicBrowseCell(frame: CGRect(x: 0, y: 0, width: 180, height: 232))
        let gradient = try XCTUnwrap(cell.contentView.layer.sublayers?.compactMap { $0 as? CAGradientLayer }
            .first { $0.name == "music.browse.text-gradient" })
        let artwork = try XCTUnwrap(cell.contentView.subviews.compactMap { $0 as? MusicArtworkView }
            .first { $0.onImage != nil })
        let collection = MusicPublicBrowseCollection(platform: .netease, nativeID: "1",
            title: "标题")
        let overlay = MusicPublicBrowseItem(id: "overlay", title: "标题", subtitle: "副标题",
                                            content: .collection(collection))

        cell.configure(item: overlay, layout: .featureRail, theme: PageTheme(), onPlay: {})
        let provider = try XCTUnwrap(CGDataProvider(data: Data([250, 200, 100, 255]) as CFData))
        let colorSpace = try XCTUnwrap(CGColorSpace(name: CGColorSpace.sRGB))
        let source = try XCTUnwrap(CGImage(width: 1, height: 1, bitsPerComponent: 8, bitsPerPixel: 32,
                                           bytesPerRow: 4, space: colorSpace,
                                           bitmapInfo: CGBitmapInfo(rawValue: CGImageAlphaInfo.last.rawValue),
                                           provider: provider, decode: nil, shouldInterpolate: true,
                                           intent: .defaultIntent))
        artwork.onImage?(UIImage(cgImage: source), nil)
        cell.layoutIfNeeded()
        XCTAssertFalse(gradient.isHidden)
        XCTAssertEqual(artwork.frame.height, cell.bounds.width)
        XCTAssertEqual(gradient.frame, artwork.frame)
        XCTAssertEqual(gradient.locations?.map(\.doubleValue), [0.62, 0.82, 1])
        let colors = try XCTUnwrap(gradient.colors as? [CGColor])
        let bottom = UIColor(cgColor: try XCTUnwrap(colors.last))
        XCTAssertEqual(cell.contentView.backgroundColor, bottom)
        let title = try XCTUnwrap(cell.contentView.subviews.compactMap { $0 as? UILabel }
            .first { $0.text == "标题" })
        XCTAssertGreaterThanOrEqual(title.frame.minY, artwork.frame.maxY)
        var red: CGFloat = 0, green: CGFloat = 0, blue: CGFloat = 0, alpha: CGFloat = 0
        XCTAssertTrue(bottom.getRed(&red, green: &green, blue: &blue, alpha: &alpha))
        XCTAssertEqual(0.2126 * red + 0.7152 * green + 0.0722 * blue, 170.0 / 255.0, accuracy: 0.01)
        XCTAssertEqual(colors[1].alpha, 112.0 / 255.0, accuracy: 0.001)

        let standard = MusicPublicBrowseItem(id: "standard", title: "普通入口",
                                             content: .unavailable)
        cell.configure(item: standard, layout: .featureRail, theme: PageTheme(), onPlay: {})
        artwork.onImage?(UIImage(cgImage: source), nil)
        let standardBottom = UIColor(cgColor: try XCTUnwrap((gradient.colors as? [CGColor])?.last))
        XCTAssertTrue(standardBottom.getRed(&red, green: &green, blue: &blue, alpha: &alpha))
        XCTAssertEqual(0.2126 * red + 0.7152 * green + 0.0722 * blue, 96.0 / 255.0, accuracy: 0.01)
        XCTAssertEqual(cell.contentView.backgroundColor, standardBottom)

        cell.configure(item: overlay, layout: .playlistRail, theme: PageTheme(), onPlay: {})
        cell.layoutIfNeeded()
        XCTAssertTrue(gradient.isHidden)
        XCTAssertEqual(gradient.frame, .zero)
        XCTAssertEqual(cell.contentView.backgroundColor, .clear)
        let subtitle = try XCTUnwrap(cell.contentView.subviews.compactMap { $0 as? UILabel }
            .first { $0.text == "副标题" })
        let play = try XCTUnwrap(cell.contentView.subviews.compactMap { $0 as? UIButton }
            .first { $0.accessibilityIdentifier == "music.browse.play.overlay" })
        XCTAssertTrue(subtitle.isHidden)
        XCTAssertEqual(subtitle.frame, .zero)
        XCTAssertTrue(artwork.frame.contains(play.frame))
        XCTAssertEqual(artwork.frame.maxX - play.frame.maxX, 6, accuracy: 0.5)
        XCTAssertEqual(artwork.frame.maxY - play.frame.maxY, 6, accuracy: 0.5)

        cell.configure(item: overlay, layout: .banner, theme: PageTheme(), onPlay: {})
        cell.layoutIfNeeded()
        XCTAssertFalse(gradient.isHidden)
        XCTAssertEqual(gradient.frame, artwork.frame)
        XCTAssertEqual(gradient.locations?.map(\.doubleValue), [0, 0.52, 1])
        XCTAssertEqual(try XCTUnwrap((gradient.colors as? [CGColor])?.last).alpha,
                       160.0 / 255.0, accuracy: 0.001)

        let empty = MusicPublicBrowseItem(id: "empty", title: "", content: .unavailable)
        cell.configure(item: empty, layout: .banner, theme: PageTheme(), onPlay: {})
        cell.layoutIfNeeded()
        XCTAssertTrue(gradient.isHidden)
        XCTAssertEqual(gradient.frame, .zero)

        let ranking = MusicPublicBrowseItem(id: collection.id, title: "榜单",
            content: .collection(collection), ranking: .init(preview: [
                .init(id: "rank", title: "榜单歌曲"),
            ]))
        cell.configure(item: ranking, layout: .rankingStack, theme: PageTheme(), onPlay: {})
        cell.layoutIfNeeded()
        cell.configure(item: overlay, layout: .featureRail, theme: PageTheme(), onPlay: {})
        cell.layoutIfNeeded()
        let layers = try XCTUnwrap(cell.contentView.layer.sublayers)
        let artworkLayerIndex = try XCTUnwrap(layers.firstIndex { $0 === artwork.layer })
        let gradientLayerIndex = try XCTUnwrap(layers.firstIndex { $0 === gradient })
        XCTAssertGreaterThan(gradientLayerIndex, artworkLayerIndex)
    }

    func testLeadingCardsAndUntitledHomeSongsKeepContentGapAndRowPlaybackTouchable() async throws {
        let page = MusicPublicBrowsePage(sections: [
            .init(id: "cards", title: "", items: [card("cover", previews: [])], layout: .featureRail),
            .init(id: "songs", title: "", items: [
                .init(id: "song", title: "歌曲", content: .track(track("song"))),
            ], layout: .list),
        ])
        let service = MusicUIKitRegressionService(browse: .init(sections: [], cursor: "public", consumed: 1), nextBrowse: page)
        let model = MusicViewModel(service: service, player: MusicUIKitRegressionPlayer())
        let controller = MusicBrowseViewController(title: "推荐", model: model, browser: service,
            request: .discovery(.home), appearance: MusicAppearancePreferencesStore(),
            router: MusicUIKitRegressionRouter(), embedded: true)
        let window = mount(controller)
        defer { release(controller, window: window) }
        let collection = try XCTUnwrap(controller.view.subviews.compactMap { $0 as? UICollectionView }.first)
        let loaded = expectation(for: NSPredicate { _, _ in
            MainActor.assumeIsolated {
                collection.layoutIfNeeded()
                return collection.cellForItem(at: IndexPath(item: 0, section: 0)) != nil &&
                    collection.cellForItem(at: IndexPath(item: 0, section: 1)) != nil
            }
        }, evaluatedWith: collection)
        await fulfillment(of: [loaded], timeout: 2)
        let first = try XCTUnwrap(collection.cellForItem(at: IndexPath(item: 0, section: 0)))
        XCTAssertEqual(first.convert(first.bounds, to: collection).minY,
                       PageLayoutMetrics.contentGap, accuracy: 0.5)
        let row = try XCTUnwrap(collection.cellForItem(at: IndexPath(item: 0, section: 1)))
        let play = try XCTUnwrap(descendants(row).compactMap { $0 as? UIButton }.first { !$0.isHidden })
        XCTAssertGreaterThan(play.bounds.height, 0)
        XCTAssertTrue(row.hitTest(play.convert(CGPoint(x: play.bounds.midX, y: play.bounds.midY), to: row), with: nil) === play)
        XCTAssertEqual(MusicBrowseHeader.height(for: page.sections[1], showsSectionPlayback: false), 0)
        let counts = await service.counts(); XCTAssertEqual(counts.browse, 2)
        await model.close()
    }

    func testHomeHeaderUsesTitleWidthAndKeepsFiltersAndChannelActionsSeparate() throws {
        let song = MusicPublicBrowseItem(id: "song", title: "歌曲", content: .track(track("song")))
        let header = MusicBrowseHeader(frame: CGRect(x: 0, y: 0, width: 360, height: 44))
        var plays = 0, channelCalls = 0
        header.configure(section: .init(id: "songs", title: "歌曲栏目", items: [song]),
            theme: PageTheme(), showsSectionPlayback: false, onPlay: { plays += 1 }, onChannel: {},
            onChoice: { _ in }, onRetry: {}, error: nil)
        header.layoutIfNeeded()
        let title = try XCTUnwrap(descendants(header).compactMap { $0 as? UILabel }.first { $0.text == "歌曲栏目" })
        XCTAssertEqual(title.frame.width, header.bounds.width, accuracy: 0.5)

        let channel = MusicPublicBrowseChannel(id: "playlist", title: "歌单", kind: .playlists)
        let filters = [MusicPublicBrowseFilter(id: "all", title: "全部"), .init(id: "other", title: "其他")]
        header.configure(section: .init(id: "lists", title: "推荐歌单", items: [], channel: channel,
            filters: filters, selectedFilter: "all"), theme: PageTheme(),
            showsSectionPlayback: false, onPlay: { plays += 1 }, onChannel: { channelCalls += 1 },
            onChoice: { _ in }, onRetry: {}, error: nil)
        header.layoutIfNeeded()
        let entry = try XCTUnwrap(descendants(header).compactMap { $0 as? UIButton }.first {
            $0.accessibilityIdentifier == "music.browse.channel.lists"
        })
        XCTAssertTrue(header.hitTest(CGPoint(x: 280, y: 22), with: nil) === entry)
        entry.sendActions(for: .touchUpInside)
        XCTAssertEqual(channelCalls, 1); XCTAssertEqual(plays, 0)
        let choices = try XCTUnwrap(descendants(header).compactMap { $0 as? UIButton }.first { $0.menu != nil && !$0.isHidden })
        XCTAssertEqual(choices.menu?.children.compactMap { ($0 as? UIAction)?.title }, filters.map(\.title))
        let label = try XCTUnwrap(descendants(header).compactMap { $0 as? UILabel }.first { $0.text == "推荐歌单" })
        let arrow = try XCTUnwrap(descendants(header).compactMap { $0 as? UIImageView }.first { !$0.isHidden && $0.image != nil })
        XCTAssertEqual(arrow.frame.minX - label.frame.maxX, 6, accuracy: 0.5)
    }

    func testOrdinaryChannelSectionDoesNotAddAnOrphanPlaybackButton() throws {
        let card = MusicPublicBrowseItem(
            id: "card", title: "歌单",
            content: .collection(.init(platform: .netease, nativeID: "1", title: "歌单"))
        )
        let song = MusicPublicBrowseItem(id: "song", title: "歌曲", content: .track(track("song")))
        let header = MusicBrowseHeader(frame: CGRect(x: 0, y: 0, width: 360, height: 44))
        header.configure(
            section: .init(id: "cards", title: "推荐歌单", items: [card, song], layout: .playlistRail),
            theme: PageTheme(), showsSectionPlayback: false,
            onPlay: {}, onChannel: {}, onChoice: { _ in }, onRetry: {}, error: nil
        )
        header.layoutIfNeeded()
        XCTAssertFalse(descendants(header).compactMap { $0 as? UIButton }.contains {
            $0.accessibilityLabel == "播放全部" && !$0.isHidden
        })
        XCTAssertEqual(MusicBrowseHeader.height(for: .init(
            id: "cards", title: "推荐歌单", items: [card, song], layout: .playlistRail
        )), 44)
    }

    func testDisabledSectionPlaybackLeavesUntitledContentWithoutHeading() throws {
        let song = MusicPublicBrowseItem(id: "song", title: "歌曲", content: .track(track("song")))
        let section = MusicPublicBrowseSection(id: "songs", title: "", items: [song], layout: .list)
        let header = MusicBrowseHeader(frame: CGRect(x: 0, y: 0, width: 360, height: 44))
        header.configure(
            section: section,
            theme: PageTheme(), showsSectionPlayback: false,
            onPlay: {}, onChannel: {}, onChoice: { _ in }, onRetry: {}, error: nil
        )
        header.layoutIfNeeded()
        XCTAssertFalse(descendants(header).compactMap { $0 as? UIButton }.contains {
            $0.accessibilityLabel == "播放全部" && !$0.isHidden
        })
        XCTAssertEqual(MusicBrowseHeader.height(for: section, showsSectionPlayback: false), 0)
    }

    func testSharedSongSectionHeadingKeepsItsPlaybackControl() throws {
        let song = MusicPublicBrowseItem(id: "song", title: "歌曲", content: .track(track("song")))
        let header = MusicBrowseHeader(frame: CGRect(x: 0, y: 0, width: 360, height: 44))
        var plays = 0
        header.configure(
            section: .init(id: "songs", title: "歌曲", items: [song], layout: .list),
            theme: PageTheme(), showsSectionPlayback: true,
            onPlay: { plays += 1 }, onChannel: {}, onChoice: { _ in }, onRetry: {}, error: nil
        )
        header.layoutIfNeeded()
        let play = try XCTUnwrap(descendants(header).compactMap { $0 as? UIButton }.first {
            $0.accessibilityLabel == "播放全部" && !$0.isHidden
        })
        play.sendActions(for: .touchUpInside)
        XCTAssertEqual(plays, 1)
    }

    func testOrdinaryChannelRailKeepsTheSharedCardWidth() async throws {
        let card = MusicPublicBrowseItem(
            id: "card", title: "歌单",
            content: .collection(.init(platform: .netease, nativeID: "1", title: "歌单"))
        )
        let channel = MusicPublicBrowseChannel(id: "western", title: "欧美", kind: .cube)
        for style in [MusicPublicBrowseSection.Layout.playlistRail, .featureRail, .vipSongs, .vipRankedSongs] {
            var sizes: [CGSize] = []
            for request in [MusicPublicBrowseRequest.discovery(.home), .discovery(.channel(channel, tag: ""))] {
                let service = MusicUIKitRegressionService(browse: .init(sections: [
                    .init(id: "cards", title: "推荐歌单", items: [card], layout: style),
                ]))
                let model = MusicViewModel(service: service, player: MusicUIKitRegressionPlayer())
                let controller = MusicBrowseViewController(
                    title: "欧美", model: model, browser: service, request: request,
                    appearance: MusicAppearancePreferencesStore(), router: MusicUIKitRegressionRouter(), embedded: true
                )
                let window = mount(controller)
                defer { release(controller, window: window) }
                let collection = try XCTUnwrap(controller.view.subviews.compactMap { $0 as? UICollectionView }.first)
                let loaded = expectation(for: NSPredicate { _, _ in
                    MainActor.assumeIsolated {
                        collection.layoutIfNeeded()
                        return collection.numberOfSections > 0 && collection.numberOfItems(inSection: 0) > 0
                    }
                }, evaluatedWith: collection)
                await fulfillment(of: [loaded], timeout: 2)
                controller.view.layoutIfNeeded()
                collection.layoutIfNeeded()
                let attributes = try XCTUnwrap(collection.collectionViewLayout.layoutAttributesForItem(
                    at: IndexPath(item: 0, section: 0)))
                sizes.append(attributes.size)
                XCTAssertTrue(collection.visibleCells.first is MusicBrowseCell)
                await model.close()
            }
            XCTAssertEqual(sizes[0], sizes[1], "\(style) must use the same layout on home and channels")
        }
    }

    func testVIPRankedSongsStackVerticallyAtFullWidthWithoutHorizontalScrolling() async throws {
        let cards = (0..<3).map { index in
            MusicPublicBrowseItem(
                id: "vip-rank-\(index)", title: "黑胶VIP 排行榜 \(index)",
                content: .collection(.init(platform: .netease, nativeID: "\(index)", title: "榜单 \(index)"))
            )
        }
        let service = MusicUIKitRegressionService(browse: .init(sections: [
            .init(id: "vip:rankings", title: "黑胶VIP 排行榜", items: cards, layout: .vipRankedSongs),
        ]))
        let model = MusicViewModel(service: service, player: MusicUIKitRegressionPlayer())
        let controller = MusicBrowseViewController(
            title: "VIP", model: model, browser: service,
            request: .discovery(.channel(.init(id: "vip", title: "VIP", kind: .cube), tag: "")),
            appearance: MusicAppearancePreferencesStore(), router: MusicUIKitRegressionRouter(), embedded: true
        )
        let window = mount(controller)
        defer { release(controller, window: window) }
        let collection = try XCTUnwrap(controller.view.subviews.compactMap { $0 as? UICollectionView }.first)
        let loaded = expectation(for: NSPredicate { _, _ in
            MainActor.assumeIsolated {
                collection.layoutIfNeeded()
                return collection.numberOfSections > 0 && collection.numberOfItems(inSection: 0) == 3
            }
        }, evaluatedWith: collection)
        await fulfillment(of: [loaded], timeout: 2)
        controller.view.layoutIfNeeded()
        collection.layoutIfNeeded()

        let frames = try (0..<3).map { index in
            try XCTUnwrap(collection.collectionViewLayout.layoutAttributesForItem(
                at: IndexPath(item: index, section: 0))).frame
        }
        let inset = collection.adjustedContentInset
        let fullWidth = collection.bounds.width - inset.left - inset.right - 32
        XCTAssertEqual(Set(frames.map { $0.minX }).count, 1)
        XCTAssertEqual(frames[0].minX, inset.left + 16, accuracy: 0.5)
        XCTAssertTrue(zip(frames, frames.dropFirst()).allSatisfy { $0.minY < $1.minY })
        for frame in frames {
            XCTAssertEqual(frame.width, fullWidth, accuracy: 0.5)
        }
        XCTAssertLessThanOrEqual(collection.contentSize.width, collection.bounds.width + 0.5)
        await model.close()
    }

    func testOrdinaryChannelPageRendersServerFiltersAboveContent() async throws {
        let filters = [
            MusicPublicBrowseFilter(id: "all", title: "全部"),
            MusicPublicBrowseFilter(id: "house", title: "House"),
            MusicPublicBrowseFilter(id: "bass", title: "Bass"),
        ]
        let card = MusicPublicBrowseItem(
            id: "card", title: "歌单",
            content: .collection(.init(platform: .netease, nativeID: "1", title: "歌单"))
        )
        let service = MusicUIKitRegressionService(browse: .init(
            sections: [.init(id: "cards", title: "推荐歌单", items: [card], layout: .playlistRail)],
            filters: filters, selectedFilter: "all"
        ))
        let model = MusicViewModel(service: service, player: MusicUIKitRegressionPlayer())
        let controller = MusicBrowseViewController(
            title: "欧美", model: model, browser: service,
            request: .discovery(.channel(.init(id: "western", title: "欧美", kind: .cube), tag: "")),
            appearance: MusicAppearancePreferencesStore(), router: MusicUIKitRegressionRouter(),
            embedded: true
        )
        let window = mount(controller)
        defer { release(controller, window: window) }
        let loaded = expectation(for: NSPredicate { _, _ in
            MainActor.assumeIsolated {
                controller.view.layoutIfNeeded()
                return controller.view.subviews.compactMap { $0 as? MusicChannelTagsView }.contains {
                    !$0.isHidden && $0.bounds.height > 0 &&
                        self.descendants($0).compactMap { $0 as? UIButton }.filter {
                            $0.accessibilityIdentifier?.hasPrefix("music.channel.tag.") == true
                        }.count == filters.count
                }
            }
        }, evaluatedWith: controller)
        await fulfillment(of: [loaded], timeout: 2)
        controller.view.layoutIfNeeded()
        let tags = try XCTUnwrap(controller.view.subviews.compactMap { $0 as? MusicChannelTagsView }.first)
        XCTAssertGreaterThan(tags.bounds.height, 0)
        XCTAssertFalse(tags.isHidden)
        XCTAssertEqual(tags.frame.minY, controller.pageHeader.frame.maxY, accuracy: 0.5)
        let buttons = descendants(tags).compactMap { $0 as? UIButton }.filter {
            $0.accessibilityIdentifier?.hasPrefix("music.channel.tag.") == true
        }
        XCTAssertEqual(buttons.compactMap { $0.title(for: .normal) }, ["全部", "House", "Bass"])
    }

    func testOrdinaryChannelBannerUsesReferenceGeometryWithAndWithoutTags() async throws {
        let filters = ["全部", "Phonk🔥", "House", "Bass", "Techno", "Trance", "百大 DJ", "厂牌精选", "场景歌单"]
            .enumerated().map { MusicPublicBrowseFilter(id: String($0.offset), title: $0.element) }
        for values in [[], filters] {
            let service = MusicUIKitRegressionService(browse: .init(sections: [
                .init(id: "banner", title: "", items: [
                    .init(id: "banner", title: "电音", content: .collection(
                        .init(platform: .netease, nativeID: "1", title: "电音")
                    )),
                ], layout: .banner),
            ], filters: values, selectedFilter: values.first?.id ?? ""))
            let model = MusicViewModel(service: service, player: MusicUIKitRegressionPlayer())
            let controller = MusicBrowseViewController(
                title: "电音", model: model, browser: service,
                request: .discovery(.channel(.init(id: "electronic", title: "电音", kind: .cube), tag: "")),
                appearance: MusicAppearancePreferencesStore(), router: MusicUIKitRegressionRouter(), embedded: true
            )
            let window = mount(controller)
            defer { release(controller, window: window) }
            let collection = try XCTUnwrap(controller.view.subviews.compactMap { $0 as? UICollectionView }.first)
            let loaded = expectation(for: NSPredicate { _, _ in
                MainActor.assumeIsolated {
                    controller.view.layoutIfNeeded()
                    collection.layoutIfNeeded()
                    return collection.numberOfSections > 0 && collection.numberOfItems(inSection: 0) > 0
                }
            }, evaluatedWith: collection)
            await fulfillment(of: [loaded], timeout: 2)
            controller.view.layoutIfNeeded()
            collection.layoutIfNeeded()
            let attributes = try XCTUnwrap(collection.collectionViewLayout.layoutAttributesForItem(
                at: IndexPath(item: 0, section: 0)))
            let inset = collection.adjustedContentInset
            let width = collection.bounds.width - inset.left - inset.right - 32
            XCTAssertEqual(attributes.frame.width, width, accuracy: 0.5)
            XCTAssertEqual(attributes.frame.height, width / 2.56, accuracy: 0.5)
            let cell = try XCTUnwrap(collection.visibleCells.first as? MusicBannerCarouselCell)
            let frame = cell.convert(cell.bounds, to: collection)
            XCTAssertEqual(frame.minX, inset.left + 16, accuracy: 0.5)
            XCTAssertEqual(frame.minY, 4, accuracy: 0.5)
            let tags = try XCTUnwrap(controller.view.subviews.compactMap { $0 as? MusicChannelTagsView }.first)
            let contentTop = controller.pageHeader.frame.maxY
            XCTAssertEqual(tags.frame.minY, contentTop, accuracy: 0.5)
            XCTAssertEqual(collection.frame.minY, contentTop + (values.isEmpty ? 0 : 56), accuracy: 0.5)
            XCTAssertEqual(cell.contentView.layer.cornerRadius, 14)
            XCTAssertFalse(descendants(cell).compactMap { $0 as? UIButton }.contains {
                $0.accessibilityIdentifier?.hasPrefix("music.browse.play.") == true && !$0.isHidden
            })
            await model.close()
        }
    }

    func testExpandedChannelTagsOverlayContentAndDismissWithoutReading() async throws {
        let filters = (0..<12).map { MusicPublicBrowseFilter(id: "tag-\($0)", title: "动态标签\($0)") }
        let service = MusicUIKitRegressionService(browse: .init(sections: [
            .init(id: "banner", title: "", items: [
                .init(id: "banner", title: "电音", content: .collection(
                    .init(platform: .netease, nativeID: "1", title: "电音")
                )),
            ], layout: .banner),
        ], filters: filters, selectedFilter: filters[0].id))
        let model = MusicViewModel(service: service, player: MusicUIKitRegressionPlayer())
        let controller = MusicBrowseViewController(
            title: "电音", model: model, browser: service,
            request: .discovery(.channel(.init(id: "electronic", title: "电音", kind: .cube), tag: "")),
            appearance: MusicAppearancePreferencesStore(), router: MusicUIKitRegressionRouter(), embedded: true
        )
        let window = mount(controller)
        defer { release(controller, window: window) }
        let tags = try XCTUnwrap(controller.view.subviews.compactMap { $0 as? MusicChannelTagsView }.first)
        let collection = try XCTUnwrap(controller.view.subviews.compactMap { $0 as? UICollectionView }.first)
        let loaded = expectation(for: NSPredicate { _, _ in
            MainActor.assumeIsolated {
                controller.view.layoutIfNeeded()
                return tags.bounds.height > 0 && !collection.visibleCells.isEmpty
            }
        }, evaluatedWith: controller)
        await fulfillment(of: [loaded], timeout: 2)
        let frame = collection.frame
        let banner = try XCTUnwrap(collection.visibleCells.first)
        let bannerFrame = banner.convert(banner.bounds, to: controller.view)
        let toggle = try XCTUnwrap(descendants(tags).compactMap { $0 as? UIButton }.first {
            $0.accessibilityIdentifier == "music.channel.tags.toggle"
        })
        toggle.sendActions(for: .touchUpInside)
        controller.view.layoutIfNeeded()
        XCTAssertTrue(tags.isExpanded)
        XCTAssertGreaterThan(tags.bounds.height, tags.collapsedHeight)
        XCTAssertEqual(tags.backgroundColor, .clear)
        let gradient = try XCTUnwrap(descendants(tags).compactMap { $0 as? WindowGradientView }.first)
        XCTAssertFalse(gradient.isHidden)
        let expectedCanvas = window.convert(window.bounds, to: tags)
        XCTAssertEqual(gradient.canvasFrame, expectedCanvas)
        XCTAssertEqual(gradient.convert(gradient.canvasFrame, to: window), window.bounds)
        XCTAssertEqual(collection.frame, frame)
        XCTAssertEqual(banner.convert(banner.bounds, to: controller.view), bannerFrame)
        XCTAssertTrue(collection.accessibilityElementsHidden)
        let dismiss = try XCTUnwrap(controller.view.subviews.compactMap { $0 as? UIButton }.first {
            $0.accessibilityIdentifier == "music.channel.tags.dismiss"
        })
        XCTAssertFalse(dismiss.isHidden)
        let point = CGPoint(x: controller.view.bounds.midX, y: tags.frame.maxY + 20)
        XCTAssertTrue(controller.view.hitTest(point, with: nil) === dismiss)
        dismiss.sendActions(for: .touchUpInside)
        controller.view.layoutIfNeeded()
        XCTAssertFalse(tags.isExpanded)
        XCTAssertTrue(dismiss.isHidden)
        XCTAssertEqual(collection.frame, frame)
        XCTAssertFalse(collection.accessibilityElementsHidden)
        XCTAssertEqual(dismiss.backgroundColor, UIColor(PageTheme.scrim))
        let layers = try XCTUnwrap(controller.view.subviews.firstIndex(of: dismiss))
        let tagLayer = try XCTUnwrap(controller.view.subviews.firstIndex(of: tags))
        XCTAssertLessThan(layers, tagLayer)
        let counts = await service.counts()
        XCTAssertEqual(counts.browse, 1, "Opening and closing tags must not request channel data")
        toggle.sendActions(for: .touchUpInside)
        controller.view.layoutIfNeeded()
        controller.pageDidBecomeHidden()
        controller.view.layoutIfNeeded()
        XCTAssertFalse(tags.isExpanded)
        XCTAssertTrue(dismiss.isHidden)
        await model.close()
    }

    func testBannerReconfigurationUpdatesItsOpenActionWithoutAddingGestures() throws {
        let collection = MusicPublicBrowseCollection(platform: .netease, nativeID: "1", title: "推荐")
        let first = MusicPublicBrowseItem(id: "banner", title: "旧推荐", content: .collection(collection))
        let updated = MusicPublicBrowseItem(id: "banner", title: "新推荐", content: .collection(collection))
        let carousel = MusicBannerCarouselCell(frame: CGRect(x: 0, y: 0, width: 408, height: 159))
        var opened = ""
        carousel.configure([first], theme: PageTheme()) { opened = $0.title }
        carousel.layoutIfNeeded()
        let tile = try XCTUnwrap(descendants(carousel).compactMap { $0 as? MusicBrowseCell }.first)
        carousel.configure([updated], theme: PageTheme()) { opened = $0.title }
        carousel.layoutIfNeeded()
        let taps = try XCTUnwrap(tile.contentView.gestureRecognizers?.compactMap { $0 as? UITapGestureRecognizer })
        XCTAssertEqual(taps.count, 1)
        _ = tile.perform(NSSelectorFromString("open"))
        XCTAssertEqual(opened, "新推荐")
        let artwork = try XCTUnwrap(tile.contentView.subviews.compactMap { $0 as? MusicArtworkView }.first)
        XCTAssertEqual(artwork.contentMode, .scaleAspectFit)
        tile.configure(item: updated, layout: .playlistRail, theme: PageTheme(), onPlay: {})
        XCTAssertEqual(artwork.contentMode, .scaleAspectFill)
    }

    func testBannerTitleAvoidsPageDotsWithMultiplePages() throws {
        let items = ["标题一", "标题二"].enumerated().map { index, title in
            MusicPublicBrowseItem(
                id: "banner-\(index)", title: title,
                content: .collection(.init(platform: .netease, nativeID: "\(index + 1)", title: title))
            )
        }
        let carousel = MusicBannerCarouselCell(frame: CGRect(x: 0, y: 0, width: 408, height: 159))
        carousel.configure(items, theme: PageTheme()) { _ in }
        carousel.layoutIfNeeded()
        let tiles = descendants(carousel).compactMap { $0 as? MusicBrowseCell }
        XCTAssertEqual(tiles.count, 2)
        tiles[0].layoutIfNeeded()
        let title = try XCTUnwrap(descendants(tiles[0]).compactMap { $0 as? UILabel }
            .first { $0.text == "标题一" })
        let dots = try XCTUnwrap(descendants(carousel).compactMap { $0 as? UIPageControl }.first)
        let titleFrame = title.convert(title.bounds, to: carousel)
        let dotsFrame = dots.convert(dots.bounds, to: carousel)
        XCTAssertEqual(dots.numberOfPages, 2)
        XCTAssertLessThanOrEqual(titleFrame.maxX, dotsFrame.minX)
    }

    func testEmptyCatalogueDeactivatesOldChannelPageAndShowsPlaceholder() async throws {
        let channels: [MusicPublicBrowseChannel] = [
            .init(id: "chart", title: "排行榜", kind: .rankings),
            .init(id: "electronic", title: "电音", kind: .cube),
        ]
        let directory = MusicPublicBrowsePage(sections: [
            .init(id: "directory", title: "频道", items: channels.map {
                .init(id: $0.identity, title: $0.title, content: .channel($0))
            }, layout: .playlistRail),
        ])
        let service = MusicUIKitRegressionService(browse: directory, channelBrowse: .init(sections: [
            .init(id: "cards", title: "动态栏目", items: [card("content", previews: [])], layout: .playlistRail),
        ]))
        let model = MusicViewModel(service: service, player: MusicUIKitRegressionPlayer())
        let router = MusicUIKitRegressionRouter()
        let controller = MusicChannelsViewController(
            model: model, browser: service, appearance: MusicAppearancePreferencesStore(),
            router: router
        )
        controller.select(channels[0])
        let window = mount(controller)
        defer { release(controller, window: window) }
        let pager = try XCTUnwrap(controller.children.compactMap { $0 as? UIPageViewController }.first)
        var activated: UIViewController?
        let loaded = expectation(for: NSPredicate { _, _ in
            MainActor.assumeIsolated {
                guard let page = pager.viewControllers?.first as? MusicBrowseViewController,
                      let collection = page.view.subviews.compactMap({ $0 as? UICollectionView }).first else {
                    return false
                }
                page.view.layoutIfNeeded()
                collection.layoutIfNeeded()
                if page.view.window != nil, activated !== page {
                    activated = page
                    NotificationCenter.default.post(name: UIApplication.didBecomeActiveNotification, object: nil)
                }
                return !collection.visibleCells.isEmpty
            }
        }, evaluatedWith: pager)
        await fulfillment(of: [loaded], timeout: 3)
        let oldPage = try XCTUnwrap(pager.viewControllers?.first as? MusicBrowseViewController)
        XCTAssertTrue(oldPage.isPageActive)

        var observing = false
        for _ in 0..<100 {
            observing = await service.isObservingHome
            if observing { break }
            try await Task.sleep(for: .milliseconds(10))
        }
        XCTAssertTrue(observing)
        await service.updateBrowse(.init(sections: []))
        let emptied = expectation(for: NSPredicate { _, _ in
            MainActor.assumeIsolated {
                guard let label = controller.view.subviews.compactMap({ $0 as? UILabel })
                    .first(where: { $0.text == "暂无频道" }) else { return false }
                return !oldPage.isPageActive && !label.isHidden &&
                    pager.viewControllers?.first !== oldPage
            }
        }, evaluatedWith: controller)
        await fulfillment(of: [emptied], timeout: 3)
        XCTAssertFalse(oldPage.isPageActive)
        XCTAssertFalse(pager.viewControllers?.first is MusicBrowseViewController)
        let placeholder = try XCTUnwrap(controller.view.subviews.compactMap { $0 as? UILabel }
            .first { $0.text == "暂无频道" })
        XCTAssertFalse(placeholder.isHidden)
        await model.close()
    }

    func testHomeChannelTitleAndCollectionCardUseIndependentNavigationActions() async throws {
        let channel = MusicPublicBrowseChannel(id: "chart", title: "排行榜", kind: .rankings)
        let destination = MusicPublicBrowseCollection(platform: .netease, nativeID: "41", title: "榜单")
        let service = MusicUIKitRegressionService(browse: .init(sections: [
            .init(id: "rankings", title: "排行榜", items: [
                .init(id: destination.id, title: destination.title, content: .collection(destination)),
            ], layout: .playlistRail, channel: channel),
        ]))
        let model = MusicViewModel(service: service, player: MusicUIKitRegressionPlayer())
        let router = MusicUIKitRegressionRouter()
        var selected: [MusicPublicBrowseChannel] = [], opened: [MusicPublicBrowseCollection] = []
        router.onChannel = { selected.append($0) }; router.onCollection = { opened.append($0) }
        let controller = MusicBrowseViewController(title: "推荐", model: model, browser: service,
            request: .discovery(.home), appearance: MusicAppearancePreferencesStore(), router: router, embedded: true)
        let window = mount(controller)
        defer { release(controller, window: window) }
        let collection = try XCTUnwrap(controller.view.subviews.compactMap { $0 as? UICollectionView }.first)
        let kind = UICollectionView.elementKindSectionHeader
        let ready = expectation(for: NSPredicate { _, _ in
            MainActor.assumeIsolated {
                collection.layoutIfNeeded()
                return collection.supplementaryView(forElementKind: kind, at: IndexPath(item: 0, section: 0)) != nil &&
                    collection.cellForItem(at: IndexPath(item: 0, section: 0)) != nil
            }
        }, evaluatedWith: collection)
        await fulfillment(of: [ready], timeout: 2)
        let header = try XCTUnwrap(collection.supplementaryView(forElementKind: kind, at: IndexPath(item: 0, section: 0)))
        let cell = try XCTUnwrap(collection.cellForItem(at: IndexPath(item: 0, section: 0)))
        XCTAssertEqual(cell.bounds.height - cell.bounds.width, 42, accuracy: 0.5)
        let entry = try XCTUnwrap(header.hitTest(CGPoint(x: header.bounds.width - 2, y: 22), with: nil) as? UIButton)
        entry.sendActions(for: .touchUpInside)
        XCTAssertEqual(selected, [channel]); XCTAssertTrue(opened.isEmpty)
        controller.collectionView(collection, didSelectItemAt: IndexPath(item: 0, section: 0))
        XCTAssertEqual(opened, [destination]); XCTAssertEqual(selected, [channel])
        let counts = await service.counts(); XCTAssertEqual(counts.browse, 1)
        await model.close()
    }

    func testSectionTabLeavesUnchangedCellHeaderAndPreviewViewsIntact() async throws {
        let a = card("a", previews: [track("a")]), b = card("b", previews: [track("b")])
        let untouched = card("untouched", previews: [track("keep")])
        let page = MusicPublicBrowsePage(sections: [
            .init(id: "changing", title: "切换", items: [a], layout: .rankedSongs,
                  tabs: [.init(id: "one", title: "一", items: [a]), .init(id: "two", title: "二", items: [b])],
                  selectedTab: "one"),
            .init(id: "unchanged", title: "保留", items: [untouched], layout: .rankedSongs),
        ])
        let service = MusicUIKitRegressionService(browse: page)
        let model = MusicViewModel(service: service, player: MusicUIKitRegressionPlayer())
        let router = MusicUIKitRegressionRouter()
        let controller = MusicBrowseViewController(title: "频道", model: model, browser: service,
            request: .discovery(.channel(.init(id: "channel", title: "频道", kind: .cube), tag: "")),
            appearance: MusicAppearancePreferencesStore(), router: router, embedded: true)
        let navigation = PageNavigationController(themeProvider: controller.themeProvider)
        navigation.setViewControllers([controller], animated: false)
        let window = mount(navigation)
        defer { release(controller, window: window) }
        window.frame = CGRect(x: 0, y: 0, width: 390, height: 844)
        window.layoutIfNeeded()
        controller.view.layoutIfNeeded()
        let collection = try XCTUnwrap(controller.view.subviews.compactMap { $0 as? UICollectionView }.first)
        let loaded = expectation(for: NSPredicate { _, _ in
            MainActor.assumeIsolated {
                collection.layoutIfNeeded()
                return collection.cellForItem(at: IndexPath(item: 0, section: 1)) != nil
            }
        }, evaluatedWith: collection)
        await fulfillment(of: [loaded], timeout: 2)
        let keptCell = try XCTUnwrap(collection.cellForItem(at: IndexPath(item: 0, section: 1)))
        let keptPreview = try preview("keep", in: keptCell)
        let kind = UICollectionView.elementKindSectionHeader
        let keptHeader = try XCTUnwrap(collection.supplementaryView(forElementKind: kind,
                                                                  at: IndexPath(item: 0, section: 1)))
        let headerTitle = try XCTUnwrap(descendants(keptHeader).compactMap { $0 as? UILabel }.first { $0.text == "保留" })
        headerTitle.text = "未重新配置"
        let changingHeader = try XCTUnwrap(collection.supplementaryView(forElementKind: kind,
                                                                      at: IndexPath(item: 0, section: 0)))
        let tab = try XCTUnwrap(descendants(changingHeader).compactMap { $0 as? UIButton }.first {
            $0.title(for: .normal) == "二"
        })
        tab.sendActions(for: .touchUpInside)
        let changed = expectation(for: NSPredicate { _, _ in
            MainActor.assumeIsolated {
                collection.layoutIfNeeded()
                return collection.cellForItem(at: IndexPath(item: 0, section: 0))?.accessibilityIdentifier == "music.browse.item.b"
            }
        }, evaluatedWith: collection)
        await fulfillment(of: [changed], timeout: 2)
        XCTAssertTrue(collection.cellForItem(at: IndexPath(item: 0, section: 1)) === keptCell)
        XCTAssertTrue(try preview("keep", in: keptCell) === keptPreview)
        XCTAssertEqual(headerTitle.text, "未重新配置")
        let counts = await service.counts()
        XCTAssertEqual(counts.browse, 1)
        await model.close()
    }

    func testCollectionDetailHeaderAndTrackRowsMatchReferenceGeometry() throws {
        let theme = PageTheme()
        let tracks = [
            MusicRemoteTrack(ref: .init(kind: .library, id: "rain"), songId: "rain",
                             title: "Rain", artist: "秦凡淇", album: "羽毛剑"),
            MusicRemoteTrack(ref: .init(kind: .library, id: "slow"), songId: "slow",
                             title: "慢慢听", artist: "房东的猫", album: "世界"),
        ]
        let header = MusicCollectionDetailHeaderView(themeProvider: MusicAppearancePreferencesStore())
        header.configure(
            content: .init(title: "慢慢听", owner: "我的歌单", count: tracks.count,
                           artwork: tracks.first?.artwork),
            filteredCount: tracks.count,
            searchQuery: "",
            playTitle: "播放全部",
            isPlaying: false,
            canPlay: true,
            showsSave: false,
            canSave: false,
            showsEdit: true,
            canEdit: true,
            showsRename: true,
            canRename: true,
            theme: theme
        )
        header.frame = CGRect(x: 0, y: 0, width: 390, height: header.preferredHeight(for: 390))
        header.layoutIfNeeded()

        let cover = try XCTUnwrap(descendants(header).first {
            $0.accessibilityIdentifier == "music.collection.cover"
        })
        let card = try XCTUnwrap(descendants(header).first {
            $0.accessibilityIdentifier == "music.collection.infoCard"
        })
        let coverFrame = cover.convert(cover.bounds, to: header)
        XCTAssertEqual(header.heroHeight(for: 390), 149, accuracy: 0.5)
        XCTAssertEqual(header.bounds.height, 211, accuracy: 0.5)
        XCTAssertEqual(card.frame.minX, 12, accuracy: 0.5)
        XCTAssertEqual(card.frame.minY, 10, accuracy: 0.5)
        XCTAssertEqual(card.frame.width, 366, accuracy: 0.5)
        XCTAssertEqual(card.layer.cornerRadius, 18, accuracy: 0.5)
        XCTAssertEqual(card.layer.borderWidth, 1, accuracy: 0.1)
        XCTAssertEqual(coverFrame.minX, 24, accuracy: 0.5)
        XCTAssertEqual(coverFrame.minY, 22, accuracy: 0.5)
        XCTAssertEqual(coverFrame.width, 102.6, accuracy: 0.5)
        XCTAssertEqual(coverFrame.height, 102.6, accuracy: 0.5)
        XCTAssertTrue(descendants(header).compactMap { $0 as? UILabel }.contains { $0.text == "慢慢听" })
        XCTAssertTrue(descendants(header).compactMap { $0 as? UILabel }.contains { $0.text == "我的歌单" })
        XCTAssertEqual(descendants(header).compactMap { $0 as? UILabel }.filter { $0.text == "2首" }.count, 1)

        let bar = header.actionBar
        bar.frame = CGRect(x: 0, y: 0, width: 390, height: bar.preferredHeight)
        bar.layoutIfNeeded()
        XCTAssertEqual(descendants(bar).compactMap { $0 as? UILabel }.filter { $0.text == "2首" }.count, 1)
        let play = try XCTUnwrap(descendants(bar).compactMap { $0 as? UIButton }.first {
            $0.accessibilityIdentifier == "music.collection.playAll"
        })
        let edit = try XCTUnwrap(descendants(bar).compactMap { $0 as? UIButton }.first {
            $0.accessibilityIdentifier == "music.collection.edit"
        })
        XCTAssertEqual(bar.bounds.height, 62, accuracy: 0.5)
        XCTAssertEqual(play.frame.minY, 0, accuracy: 0.5)
        XCTAssertEqual(play.frame.height, 62, accuracy: 0.5)
        XCTAssertEqual(edit.frame.minX, 330, accuracy: 0.6)
        XCTAssertEqual(edit.frame.minY, 9, accuracy: 0.6)
        XCTAssertEqual(edit.frame.width, 44, accuracy: 0.5)
        XCTAssertEqual(edit.frame.height, 44, accuracy: 0.5)
        XCTAssertEqual(edit.accessibilityLabel, "多选")
        XCTAssertEqual(edit.accessibilityCustomActions?.map(\.name) ?? [], ["重命名"])

        header.setCollapseProgress(1)
        XCTAssertEqual(card.alpha, 0, accuracy: 0.01)
        header.setCollapseProgress(0)
        XCTAssertEqual(card.alpha, 1, accuracy: 0.01)

        header.setSearchVisible(true)
        bar.frame.size.height = bar.preferredHeight
        bar.layoutIfNeeded()
        XCTAssertEqual(header.preferredHeight(for: 390), 263, accuracy: 0.5)
        XCTAssertEqual(bar.bounds.height, 114, accuracy: 0.5)
        XCTAssertFalse(header.searchField.isHidden)
        XCTAssertEqual(header.searchField.frame.minX, 16, accuracy: 0.5)
        XCTAssertEqual(header.searchField.frame.width, 358, accuracy: 0.5)
        XCTAssertEqual(header.searchField.frame.height, 40, accuracy: 0.5)

        let cell = MusicCollectionTrackCell(style: .default,
                                            reuseIdentifier: MusicCollectionTrackCell.reuseIdentifier)
        cell.frame = CGRect(x: 0, y: 0, width: 390, height: 72)
        cell.configure(
            track: tracks[0],
            active: false,
            isFavorite: true,
            favoriteEnabled: true,
            actionsEnabled: true,
            theme: theme,
            actionRevision: UUID(),
            menu: UIMenu(children: [UIAction(title: "下一首播放") { _ in }]),
            filteredIndex: 0,
            originalIndex: 0,
            onPlay: {},
            onFavorite: {},
            onAppend: {}
        )
        cell.layoutIfNeeded()
        let artwork = try XCTUnwrap(descendants(cell).compactMap { $0 as? MusicArtworkView }.first)
        let artworkFrame = artwork.convert(artwork.bounds, to: cell.contentView)
        XCTAssertEqual(artworkFrame.minX, 16, accuracy: 0.5)
        XCTAssertEqual(artworkFrame.minY, 15, accuracy: 0.5)
        XCTAssertEqual(artworkFrame.width, 42, accuracy: 0.5)
        XCTAssertEqual(artworkFrame.height, 42, accuracy: 0.5)
        let buttons = descendants(cell).compactMap { $0 as? UIButton }
        let favorite = try XCTUnwrap(buttons.first {
            $0.accessibilityIdentifier == "music.collection.favorite.rain"
        })
        let append = try XCTUnwrap(buttons.first {
            $0.accessibilityIdentifier == "music.collection.append.rain"
        })
        let more = try XCTUnwrap(buttons.first {
            $0.accessibilityIdentifier == "music.collection.more.rain"
        })
        XCTAssertEqual(favorite.frame.minX, 260, accuracy: 0.5)
        XCTAssertEqual(append.frame.minX, 299, accuracy: 0.5)
        XCTAssertEqual(more.frame.minX, 338, accuracy: 0.5)
        for button in [favorite, append, more] {
            XCTAssertEqual(button.frame.minY, 14, accuracy: 0.5)
            XCTAssertEqual(button.frame.width, 36, accuracy: 0.5)
            XCTAssertEqual(button.frame.height, 44, accuracy: 0.5)
        }
        XCTAssertTrue(favorite.accessibilityTraits.contains(.selected))
        XCTAssertEqual(favorite.accessibilityLabel, "取消收藏 Rain")
        XCTAssertEqual(append.accessibilityLabel, "添加 Rain 到播放队列")
        XCTAssertEqual(more.accessibilityLabel, "Rain 更多操作")

        let dark = PageTheme(dark: true)
        header.applyTheme(dark)
        let title = try XCTUnwrap(descendants(header).compactMap { $0 as? UILabel }.first {
            $0.text == "慢慢听"
        })
        XCTAssertEqual(title.textColor, UIColor(dark.ink))
        XCTAssertEqual(header.searchField.backgroundColor, UIColor(dark.control))
    }

    func testPublicCollectionDetailUsesReferenceRowsAndPaginatesWithoutExtraPlayButtons() async throws {
        let collection = MusicPublicBrowseCollection(
            platform: .netease,
            nativeID: "reference",
            title: "未来复古合成波",
            creator: "用户7883263058",
            count: 3
        )
        let tracks = [
            MusicRemoteTrack(ref: .init(kind: .queue, id: "first"), songId: "first",
                             title: "Time to Pretend", artist: "Lazer Boomerang",
                             album: "Time to Pretend",
                             sources: [.init(sourceKey: "wy:first", platform: "wy", nativeId: "first")]),
            MusicRemoteTrack(ref: .init(kind: .queue, id: "second"), songId: "second",
                             title: "The Best Of Me", artist: "Dion Timmer",
                             album: "Very Extra",
                             sources: [.init(sourceKey: "wy:second", platform: "wy", nativeId: "second")]),
            MusicRemoteTrack(ref: .init(kind: .queue, id: "third"), songId: "third",
                             title: "Midnight City", artist: "M83",
                             album: "Hurry Up, We're Dreaming",
                             sources: [.init(sourceKey: "wy:third", platform: "wy", nativeId: "third")]),
        ]
        let members = tracks.enumerated().map {
            MusicPublicBrowseMember(nativeID: $0.element.songId, sourcePosition: $0.offset, track: $0.element)
        }
        let firstPage = MusicPublicBrowseDetail(
            collection: collection,
            members: Array(members.prefix(2)),
            total: tracks.count,
            cursor: "next",
            snapshot: "reference-snapshot",
            consumed: 2
        )
        let nextPage = MusicPublicBrowseDetail(
            collection: collection,
            members: [members[2]],
            total: tracks.count,
            cursor: nil,
            snapshot: "reference-snapshot",
            consumed: 1
        )
        let service = MusicUIKitRegressionService(detail: firstPage, nextDetail: nextPage)
        let model = MusicViewModel(service: service, player: MusicUIKitRegressionPlayer())
        let appearance = MusicAppearancePreferencesStore()
        let controller = MusicCollectionViewController(
            model: model,
            collection: collection,
            browser: service,
            appearance: appearance,
            router: MusicUIKitRegressionRouter()
        )
        let navigation = PageNavigationController(themeProvider: appearance)
        navigation.setViewControllers([controller], animated: false)
        let window = mount(navigation)
        defer { release(navigation, window: window) }
        let table = try XCTUnwrap(controller.view.subviews.compactMap { $0 as? UITableView }.first)
        let initial = expectation(for: NSPredicate { _, _ in
            MainActor.assumeIsolated {
                table.layoutIfNeeded()
                return table.numberOfRows(inSection: 0) >= 2
            }
        }, evaluatedWith: table)
        await fulfillment(of: [initial], timeout: 2)
        if table.numberOfRows(inSection: 0) == 2,
           let last = table.cellForRow(at: IndexPath(row: 1, section: 0)) {
            controller.tableView(table, willDisplay: last, forRowAt: IndexPath(row: 1, section: 0))
        }
        let appended = expectation(for: NSPredicate { _, _ in
            MainActor.assumeIsolated {
                table.layoutIfNeeded()
                return table.numberOfRows(inSection: 0) == 3
            }
        }, evaluatedWith: table)
        await fulfillment(of: [appended], timeout: 2)

        XCTAssertNil(controller.title)
        XCTAssertEqual(
            descendants(controller.pageHeader).compactMap { $0 as? UIButton }
                .compactMap(\.accessibilityIdentifier)
                .filter { $0.hasPrefix("music.collection.") },
            ["music.collection.search", "music.collection.refresh"]
        )
        let header = try XCTUnwrap(table.tableHeaderView as? MusicCollectionDetailHeaderView)
        let labels = descendants(header).compactMap { $0 as? UILabel }
        XCTAssertTrue(labels.contains { $0.text == collection.title })
        XCTAssertTrue(labels.contains { $0.text == collection.creator })
        XCTAssertEqual(labels.filter { $0.text == "3首" }.count, 1)
        XCTAssertEqual(descendants(header.actionBar).compactMap { $0 as? UILabel }.filter { $0.text == "3首" }.count, 1)
        let save = try XCTUnwrap(descendants(header).compactMap { $0 as? UIButton }.first {
            $0.accessibilityIdentifier == "music.collection.save"
        })
        XCTAssertFalse(save.isHidden)
        XCTAssertNil(save.title(for: .normal))
        XCTAssertEqual(save.accessibilityLabel, "收藏歌单")
        let owner = try XCTUnwrap(labels.first { $0.text == collection.creator })
        let count = try XCTUnwrap(labels.first { $0.text == "3首" && $0.frame.minY > owner.frame.minY })
        XCTAssertEqual(save.convert(save.bounds, to: header).maxX, header.bounds.width - 18, accuracy: 0.5)
        XCTAssertEqual(save.frame.midY, (owner.frame.minY + count.frame.maxY) / 2, accuracy: 0.5)
        XCTAssertLessThanOrEqual(owner.frame.maxX, save.frame.minX - 8)
        save.layoutIfNeeded()
        let heart = try XCTUnwrap(save.imageView)
        XCTAssertNotNil(save.currentImage)
        XCTAssertEqual(heart.frame.width, 22, accuracy: 0.5)
        XCTAssertEqual(heart.frame.height, 22, accuracy: 0.5)
        XCTAssertEqual(heart.frame.midX, save.bounds.midX, accuracy: 0.5)
        XCTAssertEqual(heart.frame.midY, save.bounds.midY, accuracy: 0.5)
        let edit = try XCTUnwrap(descendants(header.actionBar).compactMap { $0 as? UIButton }.first {
            $0.accessibilityIdentifier == "music.collection.edit"
        })
        XCTAssertFalse(edit.isHidden)
        XCTAssertTrue(edit.isEnabled)
        XCTAssertEqual(edit.accessibilityLabel, "多选")

        let first = try XCTUnwrap(table.cellForRow(at: IndexPath(row: 0, section: 0))
                                  as? MusicCollectionTrackCell)
        let buttons = descendants(first).compactMap { $0 as? UIButton }
        XCTAssertEqual(buttons.count, 4)
        XCTAssertEqual(
            Set(buttons.compactMap(\.accessibilityIdentifier)),
            Set([
                "music.collection.play.first",
                "music.collection.favorite.first",
                "music.collection.append.first",
                "music.collection.more.first",
            ])
        )
        let more = try XCTUnwrap(buttons.first {
            $0.accessibilityIdentifier == "music.collection.more.first"
        })
        XCTAssertEqual(
            more.menu?.children.compactMap { ($0 as? UIAction)?.title },
            ["下一首播放", "音源", "添加到播放队列"]
        )

        edit.sendActions(for: .touchUpInside)
        let pushed = expectation(for: NSPredicate { _, _ in
            MainActor.assumeIsolated {
                navigation.view.layoutIfNeeded()
                NotificationCenter.default.post(name: UIApplication.didBecomeActiveNotification, object: nil)
                return (navigation.topViewController as? MusicCollectionEditorViewController)?.isPageVisible == true
            }
        }, evaluatedWith: navigation)
        await fulfillment(of: [pushed], timeout: 2)
        try await Task.sleep(nanoseconds: 600_000_000)
        let editor = try XCTUnwrap(navigation.topViewController as? MusicCollectionEditorViewController)
        XCTAssertEqual(editor.pageTitle, collection.title)
        XCTAssertEqual(editor.pageHeader.title, collection.title)
        XCTAssertNil(editor.title)
        XCTAssertNil(editor.navigationItem.title)
        let editorTable = try XCTUnwrap(editor.view.subviews.compactMap { $0 as? UITableView }.first)
        let actions = try XCTUnwrap(editor.view.subviews.compactMap {
            $0 as? MusicCollectionEditorActionsView
        }.first)
        editorTable.layoutIfNeeded()
        XCTAssertFalse(editorTable.isEditing)
        XCTAssertEqual(editorTable.numberOfRows(inSection: 0), 3)
        XCTAssertFalse(editor.tableView(editorTable, canMoveRowAt: IndexPath(row: 0, section: 0)))
        let editorCell = try XCTUnwrap(editorTable.cellForRow(at: IndexPath(row: 0, section: 0))
                                       as? MusicCollectionEditorTrackCell)
        XCTAssertFalse(editorCell.showsReorderControl)
        XCTAssertEqual(
            descendants(actions).compactMap { $0 as? UIButton }.filter { !$0.isHidden }
                .compactMap(\.accessibilityLabel),
            ["下一首播放", "收藏到歌单"]
        )
        let select = try XCTUnwrap(descendants(editorCell).compactMap { $0 as? UIButton }.first {
            $0.accessibilityIdentifier == "music.collection.editor.select.first"
        })
        select.sendActions(for: .touchUpInside)
        XCTAssertTrue(editorCell.isTrackSelected)
        navigation.popViewController(animated: false)
        navigation.view.layoutIfNeeded()
        XCTAssertTrue(navigation.topViewController === controller)

        header.setSearchVisible(true)
        header.searchField.text = "Midnight"
        header.searchField.sendActions(for: .editingChanged)
        let filtered = expectation(for: NSPredicate { _, _ in
            MainActor.assumeIsolated { table.numberOfRows(inSection: 0) == 1 }
        }, evaluatedWith: table)
        await fulfillment(of: [filtered], timeout: 2)
        let detailReads = await service.detailReadCount()
        XCTAssertEqual(detailReads, 2)
        await model.close()
    }

    func testCollectionDetailSearchFiltersRowsAndKeepsInlineActions() async throws {
        let tracks = [
            MusicRemoteTrack(ref: .init(kind: .library, id: "rain"), songId: "rain",
                             title: "Rain", artist: "秦凡淇", album: "羽毛剑"),
            MusicRemoteTrack(ref: .init(kind: .library, id: "slow"), songId: "slow",
                             title: "慢慢听", artist: "房东的猫", album: "世界"),
        ]
        let service = MusicUIKitRegressionService(localTracks: tracks)
        let model = MusicViewModel(service: service, player: MusicUIKitRegressionPlayer())
        let page = MusicCollectionViewModel(
            model: model,
            collectionID: "playlist:1",
            currentQueueCollectionID: { model.state.sourceCollectionID }
        )
        await page.appear()?.value
        let appearance = MusicAppearancePreferencesStore()
        let controller = MusicCollectionViewController(
            model: model,
            page: page,
            appearance: appearance,
            router: MusicUIKitRegressionRouter()
        )
        let navigation = PageNavigationController(themeProvider: appearance)
        navigation.setViewControllers([controller], animated: false)
        let window = mount(navigation)
        defer { release(navigation, window: window) }
        let table = try XCTUnwrap(controller.view.subviews.compactMap { $0 as? UITableView }.first)
        let loaded = expectation(for: NSPredicate { _, _ in
            MainActor.assumeIsolated {
                table.layoutIfNeeded()
                guard let header = table.tableHeaderView as? MusicCollectionDetailHeaderView,
                      table.numberOfRows(inSection: 0) == 2,
                      abs(header.bounds.height - header.preferredHeight(for: table.bounds.width)) < 0.5,
                      table.cellForRow(at: IndexPath(row: 0, section: 0)) != nil else { return false }
                return true
            }
        }, evaluatedWith: table)
        await fulfillment(of: [loaded], timeout: 2)

        XCTAssertEqual(descendants(controller.pageHeader).compactMap { $0 as? UIButton }
                        .compactMap(\.accessibilityIdentifier)
                        .filter { $0.hasPrefix("music.collection.") },
                       ["music.collection.search", "music.collection.delete", "music.collection.refresh"])
        let header = try XCTUnwrap(table.tableHeaderView as? MusicCollectionDetailHeaderView)
        let first = try XCTUnwrap(table.cellForRow(at: IndexPath(row: 0, section: 0))
                                  as? MusicCollectionTrackCell)
        let actionButtons = descendants(first).compactMap { $0 as? UIButton }
        XCTAssertNotNil(actionButtons.first {
            $0.accessibilityIdentifier == "music.collection.favorite.rain"
        })
        XCTAssertNotNil(actionButtons.first {
            $0.accessibilityIdentifier == "music.collection.append.rain"
        })
        let more = try XCTUnwrap(actionButtons.first {
            $0.accessibilityIdentifier == "music.collection.more.rain"
        })
        XCTAssertEqual(more.menu?.children.compactMap { $0 as? UIAction }.map(\.title) ?? [],
                       ["下一首播放", "添加到播放队列", "从歌单移除"])

        let bar = header.actionBar
        let hero = header.heroHeight(for: header.bounds.width)
        XCTAssertTrue(bar.superview === controller.view)
        XCTAssertEqual(bar.frame.height, 62, accuracy: 0.5)
        XCTAssertEqual(bar.frame.minY, table.frame.minY + hero, accuracy: 0.5)
        let top = -table.adjustedContentInset.top
        table.contentInset.bottom = 1000
        table.setContentOffset(CGPoint(x: 0, y: top + hero + 20), animated: false)
        table.layoutIfNeeded()
        XCTAssertEqual(header.collapseProgress, 1, accuracy: 0.01)
        XCTAssertEqual(controller.pageHeader.title, "歌单")
        XCTAssertEqual(bar.frame.minY, table.frame.minY, accuracy: 0.5)
        table.setContentOffset(CGPoint(x: 0, y: top), animated: false)
        table.layoutIfNeeded()
        XCTAssertEqual(header.collapseProgress, 0, accuracy: 0.01)
        XCTAssertNil(controller.pageHeader.title)
        XCTAssertEqual(bar.frame.minY, table.frame.minY + hero, accuracy: 0.5)
        table.contentInset.bottom = 0

        header.setSearchVisible(true)
        XCTAssertEqual(bar.frame.height, 114, accuracy: 0.5)
        header.searchField.text = "Rain"
        header.searchField.sendActions(for: .editingChanged)
        let filtered = expectation(for: NSPredicate { _, _ in
            MainActor.assumeIsolated {
                table.layoutIfNeeded()
                return table.numberOfRows(inSection: 0) == 1 &&
                    self.descendants(header.actionBar).compactMap { $0 as? UILabel }.contains { $0.text == "1首" }
            }
        }, evaluatedWith: table)
        await fulfillment(of: [filtered], timeout: 2)

        header.searchField.text = "不存在"
        header.searchField.sendActions(for: .editingChanged)
        let empty = expectation(for: NSPredicate { _, _ in
            MainActor.assumeIsolated {
                table.layoutIfNeeded()
                return table.numberOfRows(inSection: 0) == 0 &&
                    self.descendants(table.tableFooterView ?? UIView()).compactMap { $0 as? UILabel }
                        .contains { $0.text == "无匹配歌曲" }
            }
        }, evaluatedWith: table)
        await fulfillment(of: [empty], timeout: 2)

        header.setSearchVisible(false)
        let restored = expectation(for: NSPredicate { _, _ in
            MainActor.assumeIsolated { table.numberOfRows(inSection: 0) == 2 }
        }, evaluatedWith: table)
        await fulfillment(of: [restored], timeout: 2)
        await model.close()
    }

    func testEditorSelectionAndDragPreserveTrackIdentityWithoutReplacingCells() async throws {
        let service = MusicUIKitRegressionService(localTracks: ["a", "b", "c", "d"].map {
            .init(ref: .init(kind: .library, id: $0), songId: $0, title: $0)
        })
        let model = MusicViewModel(service: service, player: MusicUIKitRegressionPlayer())
        let page = MusicCollectionViewModel(model: model, collectionID: "playlist:1")
        await page.appear()?.value
        let draft = try XCTUnwrap(page.beginEditing(presentationID: page.presentationID))
        draft.toggle(draft.tracks[0].ref)
        let router = MusicUIKitRegressionRouter(), appearance = MusicAppearancePreferencesStore()
        let origin = MusicCollectionViewController(model: model, collectionID: "playlist:1",
                                                  appearance: appearance, router: router)
        let editor = MusicCollectionEditorViewController(page: page, draft: draft, owner: model,
                                                         origin: origin, appearance: appearance)
        let window = mount(editor)
        defer { release(editor, window: window) }
        let appeared = expectation(for: NSPredicate { _, _ in
            MainActor.assumeIsolated { editor.isPageVisible }
        }, evaluatedWith: editor)
        await fulfillment(of: [appeared], timeout: 2)
        let toolbar = try XCTUnwrap(editor.view.subviews.compactMap {
            $0 as? MusicCollectionEditorToolbarView
        }.first)
        let actions = try XCTUnwrap(editor.view.subviews.compactMap {
            $0 as? MusicCollectionEditorActionsView
        }.first)
        let table = try XCTUnwrap(editor.view.subviews.compactMap { $0 as? UITableView }.first)
        editor.view.layoutIfNeeded()
        table.layoutIfNeeded()
        XCTAssertEqual(toolbar.frame.height, 58, accuracy: 0.5)
        XCTAssertEqual(toolbar.frame.minY, editor.pageHeader.frame.maxY, accuracy: 0.5)
        XCTAssertEqual(actions.frame.height, 72, accuracy: 0.5)
        XCTAssertEqual(table.rowHeight, 72, accuracy: 0.5)
        XCTAssertEqual(editor.pageHeader.trailingCount, 0)
        let selectAll = try XCTUnwrap(descendants(toolbar).compactMap { $0 as? UIButton }.first {
            $0.accessibilityIdentifier == "music.collection.editor.selectAll"
        })
        let done = try XCTUnwrap(descendants(toolbar).compactMap { $0 as? UIButton }.first {
            $0.accessibilityIdentifier == "music.collection.editor.done"
        })
        XCTAssertEqual(selectAll.accessibilityLabel, "全选")
        XCTAssertEqual(done.accessibilityLabel, "完成")
        XCTAssertEqual(descendants(actions).compactMap { $0 as? UIButton }.compactMap(\.accessibilityLabel),
                       ["下一首播放", "收藏到歌单", "删除"])

        let cells = try (0..<3).map {
            try XCTUnwrap(table.cellForRow(at: IndexPath(row: $0, section: 0))
                          as? MusicCollectionEditorTrackCell)
        }
        XCTAssertTrue(editor.tableView(table, canEditRowAt: IndexPath(row: 0, section: 0)))
        XCTAssertTrue(editor.tableView(table, canMoveRowAt: IndexPath(row: 0, section: 0)))
        XCTAssertEqual(editor.tableView(table, editingStyleForRowAt: IndexPath(row: 0, section: 0)),
                       .none)
        XCTAssertTrue(cells.allSatisfy { $0.showsReorderControl })
        XCTAssertTrue(cells[0].isTrackSelected)
        XCTAssertFalse(cells[1].isTrackSelected)
        let mark = try XCTUnwrap(descendants(cells[0]).first {
            $0.accessibilityIdentifier == "music.collection.editor.selectionMark.a"
        })
        let markFrame = mark.convert(mark.bounds, to: cells[0].contentView)
        XCTAssertEqual(markFrame.minX, 20.5, accuracy: 0.5)
        XCTAssertEqual(markFrame.minY, 24.5, accuracy: 0.5)
        XCTAssertEqual(markFrame.width, 23, accuracy: 0.5)
        XCTAssertEqual(markFrame.height, 23, accuracy: 0.5)
        let artwork = try XCTUnwrap(descendants(cells[0]).compactMap { $0 as? MusicArtworkView }.first)
        let artworkFrame = artwork.convert(artwork.bounds, to: cells[0].contentView)
        XCTAssertEqual(artworkFrame.minX, 56, accuracy: 0.5)
        XCTAssertEqual(artworkFrame.minY, 15, accuracy: 0.5)
        XCTAssertEqual(artworkFrame.width, 42, accuracy: 0.5)
        XCTAssertEqual(artworkFrame.height, 42, accuracy: 0.5)
        selectAll.sendActions(for: .touchUpInside)
        XCTAssertEqual(draft.selectedTracks.map(\.songId), ["a", "b", "c", "d"])
        XCTAssertEqual(selectAll.accessibilityLabel, "取消全选")
        XCTAssertTrue(cells.allSatisfy { $0.isTrackSelected })
        selectAll.sendActions(for: .touchUpInside)
        XCTAssertTrue(draft.selectedTracks.isEmpty)
        XCTAssertEqual(selectAll.accessibilityLabel, "全选")
        XCTAssertTrue(cells.allSatisfy { !$0.isTrackSelected })
        let firstSelection = try XCTUnwrap(descendants(cells[0]).compactMap { $0 as? UIButton }.first {
            $0.accessibilityIdentifier == "music.collection.editor.select.a"
        })
        let secondSelection = try XCTUnwrap(descendants(cells[1]).compactMap { $0 as? UIButton }.first {
            $0.accessibilityIdentifier == "music.collection.editor.select.b"
        })
        firstSelection.sendActions(for: .touchUpInside)
        secondSelection.sendActions(for: .touchUpInside)
        XCTAssertEqual(draft.selectedTracks.map(\.songId), ["a", "b"])
        XCTAssertTrue(cells[0].isTrackSelected)
        XCTAssertTrue(cells[1].isTrackSelected)

        let from = IndexPath(row: 0, section: 0), to = IndexPath(row: 2, section: 0)
        let moved = expectation(description: "row movement completed")
        UIView.performWithoutAnimation {
            table.performBatchUpdates {
                table.moveRow(at: from, to: to)
                editor.tableView(table, moveRowAt: from, to: to)
            } completion: { _ in moved.fulfill() }
        }
        await fulfillment(of: [moved], timeout: 2)
        XCTAssertEqual(draft.tracks.map(\.songId), ["b", "c", "a", "d"])
        XCTAssertEqual(draft.selectedTracks.map(\.songId), ["b", "a"])
        for (index, original) in [1, 2, 0].enumerated() {
            let cell = try XCTUnwrap(table.cellForRow(at: IndexPath(row: index, section: 0))
                                     as? MusicCollectionEditorTrackCell)
            XCTAssertTrue(cell === cells[original])
            XCTAssertEqual(cell.isTrackSelected, ["a", "b"].contains(draft.tracks[index].songId))
            XCTAssertTrue(cell.showsReorderControl)
            XCTAssertTrue(descendants(cell).compactMap { $0 as? UILabel }.contains {
                $0.text == draft.tracks[index].title
            })
        }
        table.scrollToRow(at: IndexPath(row: 3, section: 0), at: .bottom, animated: false)
        table.layoutIfNeeded()
        let last = try XCTUnwrap(table.cellForRow(at: IndexPath(row: 3, section: 0))
                                as? MusicCollectionEditorTrackCell)
        XCTAssertEqual(last.representedReference, draft.tracks[3].ref)
        XCTAssertFalse(last.isTrackSelected)
        XCTAssertTrue(last.showsReorderControl)
        page.disappear()
        await model.close()
    }

    func testChangedLocalEditorHeaderBackPresentsConfirmationWithoutRemovingDraft() async throws {
        let service = MusicUIKitRegressionService(localTracks: ["a", "b"].map {
            .init(ref: .init(kind: .library, id: $0), songId: $0, title: $0)
        })
        let model = MusicViewModel(service: service, player: MusicUIKitRegressionPlayer())
        let page = MusicCollectionViewModel(model: model, collectionID: "playlist:1")
        await page.appear()?.value
        let draft = try XCTUnwrap(page.beginEditing(presentationID: page.presentationID))
        draft.toggle(draft.tracks[0].ref)
        draft.deleteSelected()
        let appearance = MusicAppearancePreferencesStore()
        let origin = MusicCollectionViewController(model: model, collectionID: "playlist:1",
                                                   appearance: appearance, router: MusicUIKitRegressionRouter())
        let editor = MusicCollectionEditorViewController(page: page, draft: draft, owner: model,
                                                         origin: origin, appearance: appearance)
        let navigation = PageNavigationController(themeProvider: appearance)
        navigation.setViewControllers([origin, editor], animated: false)
        let window = mount(navigation)
        defer { release(navigation, window: window) }
        let visible = expectation(for: NSPredicate { _, _ in
            MainActor.assumeIsolated { editor.isPageVisible }
        }, evaluatedWith: editor)
        await fulfillment(of: [visible], timeout: 2)
        XCTAssertTrue(editor.requestsBackConfirmationForNavigationPop())
        let back = try XCTUnwrap(descendants(editor.pageHeader).compactMap { $0 as? UIButton }.first {
            $0.accessibilityIdentifier == "page.header.back"
        })
        back.sendActions(for: .touchUpInside)
        let alert = try XCTUnwrap(editor.presentedViewController as? UIAlertController)
        XCTAssertEqual(alert.title, "放弃未保存的修改？")
        XCTAssertEqual(alert.actions.map(\.title), ["继续编辑", "放弃"])
        XCTAssertTrue(navigation.topViewController === editor)
        XCTAssertTrue(page.editor === draft)
        XCTAssertTrue(draft.hasChanges)
        editor.dismiss(animated: false)
        await model.close()
    }

    func testUnchangedLocalEditorBackReturnsToDetailOnce() async throws {
        let service = MusicUIKitRegressionService(localTracks: ["a", "b"].map {
            .init(ref: .init(kind: .library, id: $0), songId: $0, title: $0)
        })
        let model = MusicViewModel(service: service, player: MusicUIKitRegressionPlayer())
        let appearance = MusicAppearancePreferencesStore()
        let root = MusicCollectionViewController(model: model, collectionID: "playlist:1",
                                                 appearance: appearance, router: MusicUIKitRegressionRouter())
        let navigation = PageNavigationController(themeProvider: appearance)
        navigation.setViewControllers([UIViewController(), root], animated: false)
        let window = mount(navigation)
        defer { release(navigation, window: window) }
        let table = try XCTUnwrap(root.view.subviews.compactMap { $0 as? UITableView }.first)
        let loaded = expectation(for: NSPredicate { _, _ in
            MainActor.assumeIsolated {
                table.layoutIfNeeded()
                guard let header = table.tableHeaderView as? MusicCollectionDetailHeaderView,
                      let edit = self.descendants(header.actionBar).compactMap({ $0 as? UIButton }).first(where: {
                          $0.accessibilityIdentifier == "music.collection.edit"
                      }) else { return false }
                return table.numberOfRows(inSection: 0) == 2 && edit.isEnabled
            }
        }, evaluatedWith: table)
        await fulfillment(of: [loaded], timeout: 2)
        let header = try XCTUnwrap(table.tableHeaderView as? MusicCollectionDetailHeaderView)
        let edit = try XCTUnwrap(descendants(header.actionBar).compactMap { $0 as? UIButton }.first {
            $0.accessibilityIdentifier == "music.collection.edit"
        })
        edit.sendActions(for: .touchUpInside)
        let visible = expectation(for: NSPredicate { _, _ in
            MainActor.assumeIsolated {
                navigation.view.layoutIfNeeded()
                NotificationCenter.default.post(name: UIApplication.didBecomeActiveNotification, object: nil)
                return (navigation.topViewController as? MusicCollectionEditorViewController)?.isPageVisible == true
            }
        }, evaluatedWith: navigation)
        await fulfillment(of: [visible], timeout: 2)
        try await Task.sleep(nanoseconds: 600_000_000)
        let editor = try XCTUnwrap(navigation.topViewController as? MusicCollectionEditorViewController)
        XCTAssertTrue(editor.pageHeader.hasExplicitLeading)
        navigation.popViewController(animated: true)
        let returned = expectation(for: NSPredicate { _, _ in
            MainActor.assumeIsolated {
                navigation.view.layoutIfNeeded()
                return navigation.viewControllers.count == 2 && navigation.topViewController === root &&
                    root.isPageVisible
            }
        }, evaluatedWith: navigation)
        await fulfillment(of: [returned], timeout: 2)
        let stable = expectation(description: "No second pop")
        stable.isInverted = true
        await fulfillment(of: [stable], timeout: 0.4)
        XCTAssertEqual(navigation.viewControllers.count, 2)
        XCTAssertTrue(navigation.topViewController === root)
        XCTAssertEqual(table.numberOfRows(inSection: 0), 2)
        XCTAssertTrue(edit.isEnabled)
        await model.close()
    }

    func testQueueConsumesSharedFavoritesWithoutSuspendingThemOnRemoval() async throws {
        let saved = MusicRemoteTrack(ref: .init(kind: .library, id: "favorite"), songId: "favorite", title: "favorite")
        let service = MusicUIKitRegressionService(localTracks: [saved], collectionKind: "favorites")
        let model = MusicViewModel(service: service, player: MusicUIKitRegressionPlayer())
        await model.load()
        let favorites = model.favorites
        await favorites.flush()
        XCTAssertTrue(favorites.loaded, "The fixture must load its authoritative favorites")
        XCTAssertNil(favorites.error)
        let before = await service.counts()
        let queue = MusicQueueViewController(model: model, appearance: MusicAppearancePreferencesStore())
        let window = mount(queue)
        let appeared = expectation(for: NSPredicate { _, _ in
            MainActor.assumeIsolated { queue.isPageActive }
        }, evaluatedWith: queue)
        await fulfillment(of: [appeared], timeout: 2)
        release(queue, window: window)
        await favorites.flush()
        let after = await service.counts()
        XCTAssertTrue(model.favorites === favorites)
        XCTAssertTrue(favorites.loaded)
        XCTAssertTrue(favorites.isFavorite(track("favorite")))
        XCTAssertEqual(after.tracks, before.tracks)
        await model.close()
    }

    private func track(_ id: String) -> MusicRemoteTrack {
        .init(ref: .init(kind: .handle, id: id), songId: id, title: id)
    }
    private func searchDetail(_ collection: MusicPublicBrowseCollection,
                              positions: [Int]) -> MusicPublicBrowseDetail {
        let members = positions.map { position -> MusicPublicBrowseMember in
            let nativeID = "id-\(position)"
            let track = MusicRemoteTrack(
                ref: .init(kind: .handle, id: "handle-\(collection.nativeID)-\(position)"),
                songId: "wy:" + nativeID,
                title: "歌曲 \(position + 1)",
                sources: [.init(sourceKey: "wy:" + nativeID, platform: "wy", nativeId: nativeID)]
            )
            return .init(nativeID: nativeID, sourcePosition: position, track: track)
        }
        return .init(collection: collection, members: members, total: 30, cursor: nil,
                     snapshot: String(repeating: "a", count: 64), consumed: 30)
    }
    private func card(_ id: String, previews: [MusicRemoteTrack]) -> MusicPublicBrowseItem {
        .init(id: id, title: id, content: .unavailable, previewSongs: previews)
    }
    private func preview(_ title: String, in view: UIView) throws -> UIButton {
        try XCTUnwrap(descendants(view).compactMap { $0 as? UIButton }.first { $0.accessibilityLabel == "播放" + title })
    }
    private func descendants(_ view: UIView) -> [UIView] {
        view.subviews.flatMap { [$0] + descendants($0) }
    }
    private func identified(_ id: String, in view: UIView) -> UIView? {
        ([view] + descendants(view)).first { $0.accessibilityIdentifier == id }
    }
    private func waitForHomeSubscription(_ service: MusicUIKitRegressionService) async {
        let subscribed = expectation(description: "home scope observer installed")
        await service.observeHomeSubscription { subscribed.fulfill() }
        await fulfillment(of: [subscribed], timeout: 3)
    }
    private func deliverMainRunLoop() async {
        await withCheckedContinuation { continuation in
            RunLoop.main.perform { continuation.resume() }
        }
    }
    private func resultTrack(_ platform: String, _ id: String, title: String? = nil) -> MusicRemoteTrack {
        .init(ref: .init(kind: .handle, id: platform + ":" + id), songId: id,
            title: title ?? id, artist: "Fixture", album: "Album", duration: 180,
            sources: [.init(sourceKey: platform + ":" + id, platform: platform, nativeId: id)])
    }
    private func resultPlaylist(_ id: String) -> MusicPublicBrowseCollection {
        .init(platform: .qq, nativeID: id, title: id)
    }
    private func resultTable(_ index: Int, in results: UIViewController) throws -> UITableView {
        try XCTUnwrap(identified("music.search.results.page.\(index)", in: results.view) as? UITableView)
    }
    private func resultButton(id: String, in view: UIView) throws -> UIButton {
        try XCTUnwrap(identified(id, in: view) as? UIButton, id)
    }
    private func resultButton(prefix: String, in view: UIView) throws -> UIButton {
        try XCTUnwrap(matchingResultButton(prefix: prefix, in: view), prefix)
    }
    private func matchingResultButton(prefix: String, in view: UIView) -> UIButton? {
        descendants(view).compactMap { $0 as? UIButton }
            .first { $0.accessibilityIdentifier?.hasPrefix(prefix) == true }
    }
    private func invoke(_ action: UIAction) {
        let sender = UIButton(type: .system)
        sender.addAction(action, for: .touchUpInside)
        sender.sendActions(for: .touchUpInside)
    }
    private func waitForResult(file: StaticString = #filePath, line: UInt = #line,
                               _ predicate: @escaping @MainActor () -> Bool) async {
        let ready = expectation(for: NSPredicate { _, _ in MainActor.assumeIsolated { predicate() } },
                                evaluatedWith: nil)
        let outcome = await XCTWaiter.fulfillment(of: [ready], timeout: 4)
        XCTAssertEqual(outcome, .completed, file: file, line: line)
    }
    private func selectResultTab(_ index: Int, in results: SearchPageViewController) async throws -> UITableView {
        let button = try resultButton(id: "music.search.results.tab.\(index)", in: results.view)
        button.sendActions(for: .touchUpInside)
        await waitForResult {
            button.accessibilityTraits.contains(.selected) &&
                results.shouldBeginInteractiveBack(at: .zero, in: results.view) == (index == 0)
        }
        results.view.layoutIfNeeded()
        await deliverMainRunLoop()
        return try resultTable(index, in: results)
    }
    private func displayLastResult(in table: UITableView, section: Int) {
        // UIKit can redisplay a retained row after a tab transition or relayout,
        // without a new vertical drag. Exercise that delegate event explicitly.
        let rows = table.indexPathsForVisibleRows?.filter { $0.section == section } ?? []
        let last = rows.reversed().first {
            guard let cell = table.cellForRow(at: $0) else { return false }
            return cell is MusicSearchSongCell || cell is MusicSearchPlaylistCell
        }
        guard let last, let cell = table.cellForRow(at: last) else { XCTFail("Expected a visible result"); return }
        table.delegate?.tableView?(table, willDisplay: cell, forRowAt: last)
    }
    private func withSearchResults(_ service: MusicUIKitRegressionService,
        preferences: MusicSearchPreferences = .init(songPlatforms: [.qq], playlistPlatforms: [.qq]),
        run: (SearchPageViewController, MusicViewModel, MusicAppearancePreferencesStore) async throws -> Void) async throws {
        let suite = "MusicUIKitRegressionTests.result-update." + UUID().uuidString
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        let key = MusicSearchPreferencesStore.preferencesKey, saved = UserDefaults.standard.object(forKey: key)
        UserDefaults.standard.set(try JSONEncoder().encode(preferences), forKey: key)
        defer {
            UserDefaults.standard.set(saved, forKey: key)
            defaults.removePersistentDomain(forName: suite)
        }
        let appearance = MusicAppearancePreferencesStore(defaults: defaults)
        let model = MusicViewModel(service: service, player: MusicUIKitRegressionPlayer())
        await model.load()
        await service.allowResultSearch {}
        let root = MusicSearchViewController(model: model, appearance: appearance,
            router: MusicUIKitRegressionRouter(), history: SearchHistoryStore(defaults: defaults))
        let navigation = PageNavigationController(themeProvider: appearance)
        navigation.setViewControllers([root], animated: false)
        let window = mount(navigation)
        defer { release(navigation, window: window) }
        let shown = expectation(description: "result navigation completed")
        navigation.didShowPage = { page in
            if page !== root, page is SearchPageViewController { shown.fulfill() }
        }
        root.query = "测试录音"; root.onSubmit?("测试录音")
        await fulfillment(of: [shown], timeout: 3)
        navigation.didShowPage = nil
        // The component runner may itself be inactive. Deliver the foreground
        // event after the pushed page has installed its lifecycle observers.
        NotificationCenter.default.post(name: UIApplication.didBecomeActiveNotification, object: nil)
        await waitForResult {
            guard let results = navigation.topViewController as? SearchPageViewController, results !== root else { return false }
            results.view.layoutIfNeeded()
            guard let table = self.identified("music.search.results.page.0", in: results.view) as? UITableView else {
                return false
            }
            let cells = table.visibleCells
            return results.canPresentPageAlert && cells.contains { $0 is MusicSearchSongCell } &&
                !cells.flatMap(self.descendants).compactMap { $0 as? UIButton }
                    .contains { $0.title(for: .normal) == "取消" }
        }
        let results = try XCTUnwrap(navigation.topViewController as? SearchPageViewController)
        XCTAssertTrue(results.canPresentPageAlert,
            "active=\(results.isPageActive) visible=\(results.isPageVisible) " +
            "alert=\((results.presentedViewController as? UIAlertController)?.message ?? "none") " +
            "navigationPresented=\(String(describing: navigation.presentedViewController))")
        await deliverMainRunLoop(); await deliverMainRunLoop()
        do { try await run(results, model, appearance) }
        catch { await model.close(); throw error }
        await model.close()
    }
    private func useSearchResultPreferences() throws -> @MainActor () -> Void {
        let defaults = UserDefaults.standard, key = MusicSearchPreferencesStore.preferencesKey
        let saved = defaults.object(forKey: key)
        defaults.set(try JSONEncoder().encode(MusicSearchPreferences(songPlatforms: [.netease],
            playlistPlatforms: [.netease])), forKey: key)
        return { defaults.set(saved, forKey: key) }
    }
    private func mount(_ controller: UIViewController) -> UIWindow {
        let scene = UIApplication.shared.connectedScenes.compactMap { $0 as? UIWindowScene }.first
        let window = scene.map { UIWindow(windowScene: $0) }
            ?? UIWindow(frame: CGRect(x: 0, y: 0, width: 390, height: 844))
        controller.loadViewIfNeeded()
        window.rootViewController = controller
        window.makeKeyAndVisible()
        window.layoutIfNeeded()
        controller.view.layoutIfNeeded()
        NotificationCenter.default.post(name: UIApplication.didBecomeActiveNotification, object: nil)
        return window
    }
    private func release(_ controller: UIViewController, window: UIWindow) {
        PageViewController.invalidatePages(in: controller)
        window.isHidden = true
        window.rootViewController = nil
    }
}

@MainActor
final class MusicPlayerContentModelTests: XCTestCase {
    func testChorusFailureDoesNotHotRetryAndNextExplicitOpenCanReadAgain() async throws {
        let service = MusicUIKitRegressionService(), playback = PlaybackController()
        let model = MusicViewModel(service: service, playback: playback)
        try select("retry", model: model, playback: playback)
        await service.failNextChorus(.timeout)
        let presentation = MusicMiniPlayerPresentation()
        let content = MusicPlayerContentModel(model: model, playback: playback,
            appearance: MusicAppearancePreferencesStore(), presentation: presentation)
        presentation.setFullPlayerPresented(true)
        await deliverUpdates(); await content.flushChorus()
        model.clock.position = 20
        await deliverUpdates(); await content.flushChorus()
        let failed = await service.chorusStatistics()
        XCTAssertEqual(failed.calls, ["retry"])
        presentation.setFullPlayerPresented(false)
        await deliverUpdates()
        presentation.setFullPlayerPresented(true)
        await deliverUpdates(); await content.flushChorus()
        let retried = await service.chorusStatistics()
        XCTAssertEqual(retried.calls, ["retry", "retry"])
        XCTAssertNil(content.clock.chorus)
        await model.close()
    }

    func testSettledEmptyChorusSurvivesCloseReopenAndUnrelatedRevisions() async throws {
        let service = MusicUIKitRegressionService(), playback = PlaybackController()
        let model = MusicViewModel(service: service, playback: playback)
        try select("first", model: model, playback: playback)
        let presentation = MusicMiniPlayerPresentation()
        let content = MusicPlayerContentModel(model: model, playback: playback,
            appearance: MusicAppearancePreferencesStore(), presentation: presentation)
        presentation.setFullPlayerPresented(true)
        await deliverUpdates(); await content.flushChorus()
        let first = await service.chorusStatistics()
        XCTAssertEqual(first.calls, ["first"])
        presentation.setFullPlayerPresented(false)
        await deliverUpdates()
        model.playbackState.state = .init(revision: 8, settingsRevision: 5, currentId: "first", total: 1)
        presentation.setFullPlayerPresented(true)
        await deliverUpdates(); await content.flushChorus()
        let reopened = await service.chorusStatistics()
        XCTAssertEqual(reopened.calls, ["first"])
        XCTAssertNil(content.clock.chorus)
        await model.close()
    }

    func testChangedChorusIdentityDrainsOldRequestBeforeReadingItsReplacement() async throws {
        let service = MusicUIKitRegressionService(), playback = PlaybackController()
        let model = MusicViewModel(service: service, playback: playback)
        try select("old", model: model, playback: playback)
        let entered = expectation(description: "old chorus entered")
        let gate = MusicUIKitChorusGate { entered.fulfill() }
        await service.holdChorus(gate)
        let presentation = MusicMiniPlayerPresentation()
        presentation.setFullPlayerPresented(true)
        let content = MusicPlayerContentModel(model: model, playback: playback,
            appearance: MusicAppearancePreferencesStore(), presentation: presentation)
        await fulfillment(of: [entered], timeout: 2)
        try select("new", model: model, playback: playback)
        await deliverUpdates()
        let blocked = await service.chorusStatistics()
        XCTAssertEqual(blocked.calls, ["old"])
        await gate.release(); await content.flushChorus()
        let completed = await service.chorusStatistics()
        XCTAssertEqual(completed.calls, ["old", "new"])
        XCTAssertEqual(completed.peak, 1)
        XCTAssertNil(content.clock.chorus, "A late old marker must not replace the new empty result")
        await model.close()
    }

    func testCapsuleBodyAndControlsHaveDistinctActionsAndRingUsesSharedProgress() async throws {
        let (content, surface) = try makeDock()
        let root = PageViewController(themeProvider: content.appearance)
        let window = mountDock(surface, in: root)
        defer { window.isHidden = true; window.rootViewController = nil }
        await deliverUpdates()
        let play = try dockButton("play", in: surface), queue = try dockButton("queue", in: surface)
        let cover = try dockButton("open", in: surface)
        let ring = try XCTUnwrap(play.layer.sublayers?.first { $0.name == "play.progress.played" } as? CAShapeLayer)
        let track = try XCTUnwrap(play.layer.sublayers?.first { $0.name == "play.progress.track" } as? CAShapeLayer)
        XCTAssertEqual(surface.backgroundColor, .white)
        XCTAssertTrue(surface.isOpaque)
        XCTAssertEqual(track.opacity, 1)
        XCTAssertEqual(ring.strokeEnd, 1 / 6, accuracy: 0.001)
        XCTAssertLessThan(try XCTUnwrap(track.strokeColor).alpha, try XCTUnwrap(ring.strokeColor).alpha)
        content.model.clock.position = 60
        await deliverUpdates()
        XCTAssertEqual(ring.strokeEnd, 1 / 3, accuracy: 0.001)
        XCTAssertTrue(surface.isPlayerBody(surface))
        XCTAssertFalse(surface.isPlayerBody(play.imageView))
        XCTAssertFalse(surface.isPlayerBody(queue))
        XCTAssertFalse(surface.isPlayerBody(cover))
        for width in [CGFloat(292), CGFloat(408)] {
            surface.frame.size.width = width
            surface.setNeedsLayout(); surface.layoutIfNeeded()
            XCTAssertTrue(surface.bounds.contains(play.frame))
            XCTAssertTrue(surface.bounds.contains(queue.frame))
            XCTAssertFalse(play.frame.intersects(cover.frame))
            XCTAssertTrue(surface.hitTest(cover.center, with: nil) === cover)
        }
        var destinations: [MusicPlaybackDestination] = []
        surface.onPresent = { destinations.append($0) }
        queue.sendActions(for: .touchUpInside)
        cover.sendActions(for: .touchUpInside)
        surface.presentPlayer()
        XCTAssertEqual(destinations, [.queue, .player, .player])
        XCTAssertFalse(content.dockExpanded)
        attachDock(surface, name: "capsule-player")
        await content.model.close()
    }

    func testCardControlHoldEndsOnOutsideTouchAndFullPageLeavesDockInPlace() async throws {
        let (content, surface) = try makeDock()
        let root = PageViewController(themeProvider: content.appearance)
        let window = mountDock(surface, in: root)
        defer { window.isHidden = true; window.rootViewController = nil }
        let bottom = surface.frame.maxY
        await expandDock(content)
        surface.layoutIfNeeded()
        let order = try ["favorite", "previous", "play", "next", "queue"].map { try dockButton($0, in: surface) }
        XCTAssertEqual(order.map(\.frame.midX), order.map(\.frame.midX).sorted())
        let slider = try XCTUnwrap(dockViews(surface).compactMap { $0 as? MusicProgressSlider }.first)
        let close = try dockButton("collapse", in: surface)
        let cover = try dockButton("open", in: surface)
        XCTAssertFalse(cover.frame.intersects(slider.frame))
        XCTAssertFalse(close.frame.intersects(cover.frame))
        XCTAssertTrue(order.allSatisfy { $0.frame.minY > slider.frame.maxY && $0.frame.height >= 44 })
        XCTAssertFalse(dockViews(surface).contains {
            $0.accessibilityIdentifier == "music.player.lyricTranslation" ||
                $0.accessibilityIdentifier == "music.player.lyricBackground"
        })
        for width in [CGFloat(292), CGFloat(408)] {
            surface.frame.size.width = width
            surface.setNeedsLayout(); surface.layoutIfNeeded()
            XCTAssertTrue(order.allSatisfy { surface.bounds.contains($0.frame) })
            XCTAssertFalse(cover.frame.intersects(slider.frame))
        }
        let normalHeight = surface.dockHeight
        surface.traitOverrides.preferredContentSizeCategory = .accessibilityExtraExtraExtraLarge
        let enlarged = expectation(for: NSPredicate { _, _ in
            MainActor.assumeIsolated {
                surface.layoutIfNeeded()
                return surface.dockHeight > normalHeight &&
                    abs(surface.bounds.height - surface.dockHeight) < 0.5
            }
        }, evaluatedWith: surface)
        await fulfillment(of: [enlarged], timeout: 2)
        XCTAssertGreaterThan(surface.dockHeight, normalHeight)
        XCTAssertEqual(surface.bounds.height, surface.dockHeight, accuracy: 0.5)
        XCTAssertTrue(order.allSatisfy { surface.bounds.contains($0.frame) })
        surface.traitOverrides.preferredContentSizeCategory = .large
        let restored = expectation(for: NSPredicate { _, _ in
            MainActor.assumeIsolated {
                surface.layoutIfNeeded()
                return abs(surface.dockHeight - normalHeight) < 0.5 &&
                    abs(surface.bounds.height - surface.dockHeight) < 0.5
            }
        }, evaluatedWith: surface)
        await fulfillment(of: [restored], timeout: 2)
        XCTAssertEqual(try XCTUnwrap(slider.minimumTrackTintColor), slider.thumbColor)
        let observer = try XCTUnwrap(window.gestureRecognizers?.compactMap { $0 as? MusicDockInteractionObserver }.first)
        XCTAssertFalse(observer.cancelsTouchesInView)
        XCTAssertFalse(observer.delaysTouchesBegan)
        XCTAssertFalse(observer.delaysTouchesEnded)
        slider.sendActions(for: .touchDown)
        slider.sendActions(for: .touchCancel)
        observer.observeTouch(in: slider)
        XCTAssertTrue(content.dockExpanded)
        attachDock(surface, name: "card-player")
        observer.observeTouch(in: root.view)
        XCTAssertFalse(content.dockExpanded)
        await expandDock(content)
        close.sendActions(for: .touchUpInside)
        XCTAssertFalse(content.dockExpanded)
        await expandDock(content)
        let full = MusicPlayerViewController(content: content, appearance: content.appearance)
        let opened = expectation(description: "independent player opened")
        surface.onPresent = { destination in
            XCTAssertEqual(destination, .player)
            content.presentation.setFullPlayerPresented(true)
            root.present(full, animated: true) { opened.fulfill() }
        }
        surface.presentPlayer()
        XCTAssertFalse(content.dockExpanded)
        XCTAssertFalse(surface.isHidden)
        XCTAssertEqual(surface.alpha, 1)
        XCTAssertEqual(surface.transform, .identity)
        XCTAssertEqual(root.view.transform, .identity)
        await fulfillment(of: [opened], timeout: 3)
        root.view.layoutIfNeeded()
        XCTAssertEqual(surface.frame.maxY, bottom, accuracy: 0.5)
        XCTAssertTrue(surface.window === window)
        XCTAssertEqual(root.view.transform, .identity)
        let fullPlay = try XCTUnwrap(dockViews(full.view).compactMap { $0 as? UIButton }.first {
            $0.accessibilityIdentifier == "music.player.play"
        })
        let fullButtons = dockViews(full.view).compactMap { $0 as? UIButton }
        func fullButton(_ identifier: String) throws -> UIButton {
            return try XCTUnwrap(fullButtons.first { $0.accessibilityIdentifier == identifier })
        }
        let transport = try ["mode", "previous", "play", "next", "queue"].map {
            try fullButton("music.player.\($0)")
        }
        for pair in zip(transport, transport.dropFirst()) {
            XCTAssertLessThan(pair.0.frame.midX, pair.1.frame.midX)
            XCTAssertEqual(pair.0.frame.midY, pair.1.frame.midY, accuracy: 0.5)
        }
        let favorite = try fullButton("music.player.favorite")
        let more = try fullButton("music.player.more")
        XCTAssertLessThan(favorite.frame.midX, more.frame.midX)
        XCTAssertEqual(favorite.frame.midY, more.frame.midY, accuracy: 0.5)
        let lyricToggle = try fullButton("music.player.lyricToggle")
        let translation = try fullButton("music.player.lyricTranslation")
        let background = try fullButton("music.player.lyricBackground")
        let fullControls = try XCTUnwrap(dockViews(full.view).compactMap { $0 as? MusicPlaybackControlsView }.first)
        XCTAssertTrue(translation.isHidden)
        XCTAssertTrue(background.isHidden)
        lyricToggle.sendActions(for: .touchUpInside)
        full.view.layoutIfNeeded()
        XCTAssertFalse(translation.isHidden)
        XCTAssertFalse(background.isHidden)
        for button in [translation, background] {
            XCTAssertTrue(button.superview === full.view)
            XCTAssertTrue(full.view.hitTest(button.center, with: nil) === button)
        }
        XCTAssertFalse(fullPlay === order[2])
        XCTAssertEqual(fullPlay.layer.sublayers?.first { $0.name == "play.progress.track" }?.opacity, 0)
        do {
            let responsiveControls = MusicPlaybackControlsView(content: content, layout: .full)
            responsiveControls.setFullViewport(viewportHeight: 844, safeHeight: 763)
            let regularControlsHeight = responsiveControls.fullHeight
            responsiveControls.setFullViewport(viewportHeight: 650, safeHeight: 620)
            let shortControlsHeight = responsiveControls.fullHeight
            responsiveControls.setFullViewport(viewportHeight: 480, safeHeight: 460)
            let veryShortControlsHeight = responsiveControls.fullHeight
            XCTAssertLessThan(shortControlsHeight, regularControlsHeight)
            XCTAssertLessThan(veryShortControlsHeight, shortControlsHeight)
        }
        do {
            let responsivePlayer = MusicFullPlayerView(content: content)
            responsivePlayer.frame = CGRect(x: 0, y: 0, width: 480, height: 640)
            responsivePlayer.layoutIfNeeded()
            let records = dockViews(responsivePlayer).compactMap { $0 as? MusicRecordDiscView }
            XCTAssertFalse(records.isEmpty)
            XCTAssertTrue(records.allSatisfy { $0.bounds.width > 0 })
            responsivePlayer.frame = CGRect(x: 0, y: 0, width: 844, height: 390)
            responsivePlayer.layoutIfNeeded()
            XCTAssertTrue(records.allSatisfy { $0.bounds.width == 0 })
        }
        do {
            full.additionalSafeAreaInsets = UIEdgeInsets(top: 0, left: 59, bottom: 0, right: 59)
            full.view.layoutIfNeeded()
            let close = try fullButton("music.player.close")
            let route = try XCTUnwrap(dockViews(full.view).first {
                $0.accessibilityIdentifier == "music.player.audioOutput"
            })
            XCTAssertGreaterThanOrEqual(close.frame.minX, full.view.safeAreaInsets.left + 8)
            XCTAssertLessThanOrEqual(route.frame.maxX,
                                    full.view.bounds.width - full.view.safeAreaInsets.right - 8)
            full.additionalSafeAreaInsets = .zero
            full.view.layoutIfNeeded()
        }
        let fullHeight = fullControls.fullHeight
        full.view.traitOverrides.preferredContentSizeCategory = .accessibilityExtraExtraExtraLarge
        let fullEnlarged = expectation(for: NSPredicate { _, _ in
            MainActor.assumeIsolated {
                full.view.layoutIfNeeded()
                return fullControls.fullHeight > fullHeight &&
                    abs(fullControls.bounds.height - fullControls.fullHeight) < 0.5
            }
        }, evaluatedWith: full.view)
        await fulfillment(of: [fullEnlarged], timeout: 2)
        XCTAssertTrue(full.view.bounds.contains(fullControls.frame))
        let dismissed = expectation(description: "independent player dismissed")
        full.onDismiss = { dismissed.fulfill() }
        let back = try XCTUnwrap(dockViews(full.view).compactMap { $0 as? UIButton }.first {
            $0.accessibilityIdentifier == "music.player.close"
        })
        back.sendActions(for: .touchUpInside)
        await fulfillment(of: [dismissed], timeout: 3)
        XCTAssertFalse(content.presentation.isFullPlayerPresented)
        XCTAssertFalse(content.dockExpanded)
        XCTAssertEqual(surface.frame.maxY, bottom, accuracy: 0.5)
        await content.model.close()
    }

    func testHomeCardReturnsAfterFiveSecondsWithoutMediaProgressOrControlHold() async throws {
        let (content, surface) = try makeDock()
        let collapsed = expectation(description: "five-second preview expired")
        let started = ProcessInfo.processInfo.systemUptime
        await expandDock(content)
        let observation = content.dockChanges.sink {
            if !content.dockExpanded { collapsed.fulfill() }
        }
        defer { observation.cancel() }
        var queueOpened = false
        surface.onPresent = { queueOpened = $0 == .queue }
        try dockButton("queue", in: surface).sendActions(for: .touchUpInside)
        XCTAssertTrue(queueOpened)
        XCTAssertNotEqual(content.model.phase, .playing)
        await fulfillment(of: [collapsed], timeout: 7)
        XCTAssertGreaterThanOrEqual(ProcessInfo.processInfo.systemUptime - started, 4.9)
        XCTAssertEqual(content.clock.position, 30)
        XCTAssertFalse(content.dockExpanded)
        await content.model.close()
    }

    func testRecordPagingHitAreaFollowsOnlyVisibleCircularDiscs() async throws {
        let (content, _) = try makeDock()
        defer { content.model.player = nil }
        let player = MusicFullPlayerView(content: content)
        for size in [CGSize(width: 390, height: 844), CGSize(width: 480, height: 640)] {
            player.frame = CGRect(origin: .zero, size: size)
            player.layoutIfNeeded()
            let scroll = try XCTUnwrap(dockViews(player).compactMap { $0 as? MusicRecordScrollView }.first)
            let disc = try XCTUnwrap(scroll.subviews.compactMap { $0 as? MusicRecordDiscView }.first {
                $0.frame.contains(CGPoint(x: scroll.bounds.midX, y: scroll.bounds.midY))
            })
            XCTAssertGreaterThan(disc.bounds.width, 0)
            for point in [CGPoint(x: 0.5, y: 0.5), CGPoint(x: 0.1, y: 0.5), CGPoint(x: 0.5, y: 0.9)] {
                let local = CGPoint(x: point.x * disc.bounds.width, y: point.y * disc.bounds.height)
                let inScroll = disc.convert(local, to: scroll)
                XCTAssertTrue(scroll.point(inside: inScroll, with: nil))
                XCTAssertTrue(player.hitTest(disc.convert(local, to: player), with: nil) === scroll)
            }
            for point in [CGPoint(x: 0.02, y: 0.02), CGPoint(x: 0.98, y: 0.02),
                          CGPoint(x: 0.02, y: 0.98), CGPoint(x: 0.98, y: 0.98),
                          CGPoint(x: -0.04, y: 0.5), CGPoint(x: 1.04, y: 0.5)] {
                let local = CGPoint(x: point.x * disc.bounds.width, y: point.y * disc.bounds.height)
                XCTAssertFalse(scroll.point(inside: disc.convert(local, to: scroll), with: nil))
                XCTAssertFalse(player.hitTest(disc.convert(local, to: player), with: nil) === scroll)
            }
            // Hit testing is in scrolled content coordinates, not a fixed center rectangle.
            scroll.contentOffset.x += 24
            XCTAssertTrue(scroll.point(inside: disc.center, with: nil))
            disc.isHidden = true
            XCTAssertFalse(scroll.point(inside: disc.center, with: nil))
            disc.isHidden = false
            scroll.contentOffset.x -= 24
        }
        player.frame.size = CGSize(width: 844, height: 390)
        player.layoutIfNeeded()
        let scroll = try XCTUnwrap(dockViews(player).compactMap { $0 as? MusicRecordScrollView }.first)
        XCTAssertFalse(scroll.point(inside: CGPoint(x: scroll.bounds.midX, y: scroll.bounds.midY), with: nil))
        await content.model.close()
    }

    func testProgressTrackTapsSeekForwardBackwardAndEndpointsInFullAndCardControls() async throws {
        for layout in [MusicPlaybackControlsView.Layout.full, .card] {
            let (content, _) = try makeDock()
            let driver = MusicSeekRecordingPlayer()
            content.model.player = driver
            let controls = MusicPlaybackControlsView(content: content, layout: layout)
            controls.frame = CGRect(x: 0, y: 0, width: 390, height: 300)
            controls.setRendering(true)
            controls.layoutIfNeeded()
            let slider = try XCTUnwrap(dockViews(controls).compactMap { $0 as? MusicProgressSlider }.first)
            XCTAssertTrue(slider.isEnabled)
            let originalAttempt = try XCTUnwrap(content.model.nativeAttempt)
            // The 12pt tracking thumb travels from x=6 to width-6.
            func point(_ fraction: CGFloat) -> CGPoint {
                CGPoint(x: 6 + fraction * (slider.bounds.width - 12), y: slider.bounds.midY)
            }
            // Clock is at 30s of 180s: unplayed, earlier, and both ends.
            for fraction: CGFloat in [0.75, 0.05, 0, 1] {
                let previousCount = driver.seeks.count
                slider.beginTrackTouch(at: point(fraction))
                XCTAssertEqual(slider.value, Float(fraction) * 180, accuracy: 0.5)
                XCTAssertEqual(driver.seeks.count, previousCount, "Touch-down only previews")
                slider.endTrackTouch(at: point(fraction))
                XCTAssertEqual(driver.seeks.count, previousCount + 1)
                let seek = try XCTUnwrap(driver.seeks.last)
                XCTAssertEqual(seek.0, Double(fraction) * 180, accuracy: 0.5)
                XCTAssertEqual(seek.1.id, originalAttempt.id)
                XCTAssertEqual(content.model.phase, .paused, "Seeking must not resume playback")
                XCTAssertEqual(content.model.clock.position, 30, "A seek request is not a playback receipt")
            }
            // A press on the track can drag before release; only the release commits.
            let previousCount = driver.seeks.count
            slider.beginTrackTouch(at: point(0.75))
            slider.moveTrackTouch(to: point(0.6))
            slider.moveTrackTouch(to: point(0.4))
            XCTAssertEqual(slider.value, 72, accuracy: 0.5)
            let preview = MusicMiniPlayerText.time(Double(slider.value))
            XCTAssertTrue(dockViews(controls).contains { ($0 as? UILabel)?.text == preview })
            XCTAssertEqual(driver.seeks.count, previousCount)
            slider.endTrackTouch(at: point(0.4))
            XCTAssertEqual(driver.seeks.count, previousCount + 1)
            XCTAssertEqual(try XCTUnwrap(driver.seeks.last).0, 72, accuracy: 0.5)
            content.model.player = nil
            await content.model.close()
        }
    }

    func testProgressTapCancellationAndInvalidatedPlaybackCannotCommit() async throws {
        for invalidation in ["cancel", "attempt", "access", "hidden"] {
            let (content, _) = try makeDock()
            let driver = MusicSeekRecordingPlayer()
            content.model.player = driver
            let controls = MusicPlaybackControlsView(content: content, layout: .full)
            controls.frame = CGRect(x: 0, y: 0, width: 390, height: 300)
            controls.setRendering(true)
            controls.layoutIfNeeded()
            let slider = try XCTUnwrap(dockViews(controls).compactMap { $0 as? MusicProgressSlider }.first)
            let point = CGPoint(x: slider.bounds.width * 0.8, y: slider.bounds.midY)
            slider.beginTrackTouch(at: point)
            XCTAssertGreaterThan(slider.value, 100)
            switch invalidation {
            case "cancel":
                slider.cancelTrackTouch()
                XCTAssertEqual(slider.value, 30, accuracy: 0.001)
            case "attempt":
                content.model.nativeAttempt = .init(id: UUID(), resourceID: "replacement")
            case "access":
                content.model.accessState.isAvailable = false
            default:
                controls.setRendering(false)
                XCTAssertEqual(slider.value, 30, accuracy: 0.001)
            }
            slider.endTrackTouch(at: point)
            XCTAssertTrue(driver.seeks.isEmpty, invalidation)
            content.model.player = nil
            await content.model.close()
        }
    }

    func testProgressSliderFollowsDeclinedTrackTouchAsOneControlSequence() {
        let slider = MusicProgressSlider(frame: CGRect(x: 0, y: 0, width: 320, height: 44))
        slider.maximumValue = 180
        slider.value = 30
        var events: [String] = []
        let observed: [(UIControl.Event, String)] = [(.touchDown, "down"), (.valueChanged, "change"),
            (.touchUpInside, "upInside"), (.touchUpOutside, "upOutside"), (.touchCancel, "cancel")]
        for (event, name) in observed {
            slider.addAction(UIAction { _ in events.append(name) }, for: event)
        }
        // While a touch is followed, the slider's own recognizers cannot adopt it;
        // one its owner disabled stays disabled afterwards.
        let active = UIPanGestureRecognizer(), inactive = UIPanGestureRecognizer()
        inactive.isEnabled = false
        slider.addGestureRecognizer(active)
        slider.addGestureRecognizer(inactive)
        func x(_ fraction: CGFloat) -> CGFloat { 6 + fraction * 308 }

        slider.beginTrackTouch(at: CGPoint(x: x(0.75), y: 22))
        XCTAssertEqual(slider.value, 135, accuracy: 0.5)
        XCTAssertFalse(active.isEnabled)
        slider.beginTrackTouch(at: CGPoint(x: x(0.1), y: 22))
        XCTAssertEqual(slider.value, 135, accuracy: 0.5, "A second begin while following is ignored")
        slider.moveTrackTouch(to: CGPoint(x: x(0.4), y: 22))
        XCTAssertEqual(slider.value, 72, accuracy: 0.5)
        slider.moveTrackTouch(to: CGPoint(x: x(0.4), y: 22))
        slider.endTrackTouch(at: CGPoint(x: x(0.4), y: 22))
        XCTAssertEqual(events, ["down", "change", "change", "upInside"])
        XCTAssertTrue(active.isEnabled)
        XCTAssertFalse(inactive.isEnabled)
        slider.endTrackTouch(at: CGPoint(x: x(0.9), y: 22))
        XCTAssertEqual(slider.value, 72, accuracy: 0.5, "Only a followed touch can end")

        events = []
        slider.beginTrackTouch(at: CGPoint(x: -40, y: 22))
        XCTAssertEqual(slider.value, 0)
        slider.endTrackTouch(at: CGPoint(x: 400, y: 90))
        XCTAssertEqual(slider.value, 180)
        XCTAssertEqual(events, ["down", "change", "change", "upOutside"])

        events = []
        slider.beginTrackTouch(at: CGPoint(x: x(0.5), y: 22))
        slider.cancelTrackTouch()
        slider.cancelTrackTouch()
        XCTAssertEqual(events, ["down", "change", "cancel"])
        XCTAssertTrue(active.isEnabled)

        events = []
        slider.isEnabled = false
        let held = slider.value
        slider.beginTrackTouch(at: CGPoint(x: x(0.1), y: 22))
        slider.moveTrackTouch(to: CGPoint(x: x(0.2), y: 22))
        slider.endTrackTouch(at: CGPoint(x: x(0.2), y: 22))
        XCTAssertEqual(events, [])
        XCTAssertEqual(slider.value, held)
    }

    private func makeDock() throws -> (MusicPlayerContentModel, MusicPlayerSurface) {
        let playback = PlaybackController()
        let model = MusicViewModel(service: MusicUIKitRegressionService(), playback: playback)
        try select("dock", model: model, playback: playback)
        model.playbackState.queue = [.init(id: "dock", track: .init(ref: .init(kind: .queue, id: "dock"),
            songId: "dock", title: "Goodbye", artist: "Ramsey"))]
        model.playbackState.phase = .paused
        model.clock.position = 30
        let content = MusicPlayerContentModel(model: model, playback: playback,
            appearance: MusicAppearancePreferencesStore(defaults: UserDefaults(suiteName: UUID().uuidString)!),
            presentation: MusicMiniPlayerPresentation())
        let surface = MusicPlayerSurface(content: content)
        surface.frame = CGRect(x: 0, y: 0, width: 408, height: surface.dockHeight)
        let image = UIGraphicsImageRenderer(size: CGSize(width: 84, height: 84)).image { context in
            UIColor(red: 0.15, green: 0.12, blue: 0.10, alpha: 1).setFill()
            context.fill(CGRect(x: 0, y: 0, width: 84, height: 84))
            UIColor(red: 0.7, green: 0.55, blue: 0.3, alpha: 1).setStroke()
            UIBezierPath(roundedRect: CGRect(x: 8, y: 8, width: 68, height: 68), cornerRadius: 8).stroke()
        }
        try dockButton("open", in: surface).setImage(image, for: .normal)
        return (content, surface)
    }

    private func expandDock(_ content: MusicPlayerContentModel) async {
        let intent = content.presentation.beginHomePlayback()
        XCTAssertTrue(content.presentation.finishHomePlayback(intent, playback: .init(
            occurrenceID: content.model.current!.id, nativeAttemptID: content.model.activeNativeAttemptID!,
            accessGeneration: content.model.accountAccessGeneration)))
        await deliverUpdates()
        XCTAssertTrue(content.dockExpanded)
    }

    private func mountDock(_ surface: MusicPlayerSurface, in root: UIViewController) -> UIWindow {
        let scene = UIApplication.shared.connectedScenes.compactMap { $0 as? UIWindowScene }.first
        let window = scene.map { UIWindow(windowScene: $0) }
            ?? UIWindow(frame: CGRect(x: 0, y: 0, width: 430, height: 932))
        window.rootViewController = root
        window.makeKeyAndVisible()
        root.view.addSubview(surface)
        surface.frame = CGRect(x: 14, y: root.view.bounds.height - 144, width: root.view.bounds.width - 28,
                               height: surface.dockHeight)
        let bottom = surface.frame.maxY
        surface.onSizeChanged = { [weak surface] in
            guard let surface else { return }
            surface.frame = CGRect(x: surface.frame.minX, y: bottom - surface.dockHeight,
                                   width: surface.frame.width, height: surface.dockHeight)
        }
        surface.observeOutsideInteractions(in: window)
        root.view.layoutIfNeeded()
        NotificationCenter.default.post(name: UIApplication.didBecomeActiveNotification, object: nil)
        return window
    }

    private func dockViews(_ view: UIView) -> [UIView] { view.subviews.flatMap { [$0] + dockViews($0) } }
    private func dockButton(_ name: String, in surface: UIView) throws -> UIButton {
        try XCTUnwrap(dockViews(surface).compactMap { $0 as? UIButton }.first {
            $0.accessibilityIdentifier == "music.miniPlayer." + name
        })
    }
    private func attachDock(_ surface: MusicPlayerSurface, name: String) {
        surface.layoutIfNeeded()
        let size = CGSize(width: surface.bounds.width + 32, height: surface.bounds.height + 32)
        let image = UIGraphicsImageRenderer(size: size).image { context in
            UIColor(white: 0.95, alpha: 1).setFill()
            context.fill(CGRect(origin: .zero, size: size))
            context.cgContext.translateBy(x: 16, y: 16)
            surface.layer.render(in: context.cgContext)
        }
        let attachment = XCTAttachment(image: image)
        attachment.name = name
        attachment.lifetime = .keepAlways
        add(attachment)
    }

    private func select(_ id: String, model: MusicViewModel, playback: PlaybackController) throws {
        let url = URL(string: "https://media.invalid/fixture.mp3")!
        let resource = try PlaybackResource(id: "music:" + id, url: url, title: id,
                                            headers: ["X-Fixture": "bad\nvalue"])
        // Exercise the real attempt identity without creating an item or fetching media.
        let attempt = playback.playAttempt(resource: resource, owner: "music", autoplay: false)
        XCTAssertNil(playback.player.currentItem)
        model.nativeAttempt = .init(id: attempt.id, resourceID: resource.id)
        let selection = MusicPlaybackSelection(installationId: "official:fixture", generation: 1,
            siteId: "wy", sourceKey: "wy:" + id, quality: "128k")
        model.candidate = .init(attemptId: id, occurrenceId: id, resourceId: resource.id,
                                url: url, title: id, requestedQuality: "128k", resolvedSelection: selection)
        model.lastSelection = selection
        model.playbackState.queue = [.init(id: id, track: .init(ref: .init(kind: .queue, id: id),
                                                             songId: id, title: id))]
        model.playbackState.state = .init(currentId: id, total: 1)
        model.duration = 180
    }

    private func deliverUpdates() async {
        await withCheckedContinuation { continuation in
            RunLoop.main.perform { continuation.resume() }
        }
    }
}

@MainActor
private final class MusicUIKitRegressionRouter: MusicUIRouting {
    let primaryPlaybackPresentation = MusicMiniPlayerPresentation()
    var onChannel: ((MusicPublicBrowseChannel) -> Void)?
    var onCollection: ((MusicPublicBrowseCollection) -> Void)?
    func showSearch() { XCTFail("Unexpected navigation") }
    func showSettings() { XCTFail("Unexpected navigation") }
    func showPublicBrowse(_ request: MusicPublicBrowseRequest, title: String) { XCTFail("Unexpected navigation") }
    func showPublicCollection(_ collection: MusicPublicBrowseCollection) {
        guard let onCollection else { XCTFail("Unexpected navigation"); return }
        onCollection(collection)
    }
    func showLocalCollection(_ collection: MusicCollection) { XCTFail("Unexpected navigation") }
    func showPlayer(_ destination: MusicPlaybackDestination) { XCTFail("Unexpected navigation") }
    func selectChannel(_ channel: MusicPublicBrowseChannel) {
        guard let onChannel else { XCTFail("Unexpected navigation"); return }
        onChannel(channel)
    }
}

private actor MusicUIKitRegressionService: MusicServing, MusicAccountBrowsing, MusicPublicBrowsing, MusicPublicPlaylistSearching,
    MusicSearchDiscoveryProviding, MusicSearchHotPlaybackProviding, MusicChorusProviding, MusicHomeBrowsing,
    MusicSearchBatchPlaying, MusicResolvedSourcesServing, MusicFMPreviewServing {
    struct ResultRead: Equatable, Sendable {
        let section: MusicSearchSection
        let platform: String
        let query: String
        let page: Int
        let limit: Int
    }
    private let discovery: [MusicSearchDiscoverySnapshot]
    private var browse: MusicPublicBrowsePage
    private let nextBrowse: MusicPublicBrowsePage?
    private let detail: MusicPublicBrowseDetail?
    private let nextDetail: MusicPublicBrowseDetail?
    private let channelBrowse: MusicPublicBrowsePage?
    private let localTracks: [MusicRemoteTrack]
    private let collectionKind: String
    private var details: [String: [MusicPublicBrowseDetail]]
    private var discoveryGate: MusicUIKitSearchGate?
    private var discoveryCalls = 0, browseCalls = 0, trackCalls = 0
    private var requestedDiscovery: [Set<MusicSearchDiscoveryFailure.Component>] = []
    private var requestedDetails: [String] = []
    private(set) var discoveryRequests: [MusicPublicBrowseDestination] = []
    private var chorusCalls: [String] = [], chorusActive = 0, chorusPeak = 0
    private var chorusGate: MusicUIKitChorusGate?
    private var chorusFailure: MusicServiceError?
    private var resultSearchObserver: (@Sendable () -> Void)?
    private var requestedResultReads: [ResultRead] = []
    private var playlistSearchFailure: MusicServiceError?
    private var songResultPages: [String: [[MusicRemoteTrack]]]?
    private var playlistResultPages: [String: [[MusicPublicBrowseCollection]]] = [:]
    private var resultPageGates: [MusicSearchSection: [Int: MusicUIKitSearchGate]] = [:]
    private var requestedRecordings: [MusicSearchBatchRequest] = [], playedBatches: [MusicSearchBatchRequest] = []
    private var recordingObserver: (@Sendable () -> Void)?, batchObserver: (@Sendable () -> Void)?
    private var resolvedResultTracks: [MusicReference: MusicRemoteTrack] = [:]
    private var resultFavorites: [MusicRemoteTrack] = []
    private let resultInspection = MusicUIKitResultInspectionSession()
    private var homeContinuations: [UUID: AsyncStream<Void>.Continuation] = [:]
    private var homeSubscriptionObserver: (@Sendable () -> Void)?
    private var homeGate: MusicUIKitSearchGate?
    private var catalogueGate: MusicUIKitSearchGate?
    private var channelGate: MusicUIKitSearchGate?
    private var detailGates: [String: MusicUIKitSearchGate] = [:]
    private var localTracksGate: MusicUIKitSearchGate?
    private var previewObserver: (after: Int, body: @Sendable () -> Void)?
    private(set) var fmPreviewCount = 0
    private var localQueueObserver: (@Sendable () -> Void)?
    private(set) var localQueueReferences: [MusicReference] = []
    var isObservingHome: Bool { !homeContinuations.isEmpty }
    init(discovery: [MusicSearchDiscoverySnapshot] = [], browse: MusicPublicBrowsePage = .init(sections: []),
         nextBrowse: MusicPublicBrowsePage? = nil, detail: MusicPublicBrowseDetail? = nil,
         nextDetail: MusicPublicBrowseDetail? = nil,
         channelBrowse: MusicPublicBrowsePage? = nil,
         localTracks: [MusicRemoteTrack] = [], collectionKind: String = "playlist",
         details: [String: [MusicPublicBrowseDetail]] = [:],
         discoveryGate: MusicUIKitSearchGate? = nil) {
        self.discovery = discovery; self.browse = browse; self.nextBrowse = nextBrowse
        self.detail = detail; self.nextDetail = nextDetail
        self.channelBrowse = channelBrowse
        self.localTracks = localTracks; self.collectionKind = collectionKind
        self.details = details; self.discoveryGate = discoveryGate
    }
    func counts() -> (discovery: Int, browse: Int, tracks: Int, details: Int) {
        (discoveryCalls, browseCalls, trackCalls, requestedDetails.count)
    }
    func searchCalls() -> [Set<MusicSearchDiscoveryFailure.Component>] { requestedDiscovery }
    func allowResultSearch(playlistFailure: MusicServiceError? = nil, onRead: @escaping @Sendable () -> Void) {
        playlistSearchFailure = playlistFailure; resultSearchObserver = onRead
    }
    func resultReads() -> [ResultRead] { requestedResultReads }
    func configureResultPages(songs: [String: [[MusicRemoteTrack]]],
                              playlists: [String: [[MusicPublicBrowseCollection]]] = [:]) {
        songResultPages = songs; playlistResultPages = playlists
    }
    func blockResultPage(_ section: MusicSearchSection, page: Int, gate: MusicUIKitSearchGate) {
        resultPageGates[section, default: [:]][page] = gate
    }
    func observeRecordingReads(_ observer: (@Sendable () -> Void)?) { recordingObserver = observer }
    func observeBatchPlays(_ observer: (@Sendable () -> Void)?) { batchObserver = observer }
    func recordingReads() -> [MusicSearchBatchRequest] { requestedRecordings }
    func batchPlays() -> [MusicSearchBatchRequest] { playedBatches }
    func searchRecording(_ request: MusicSearchBatchRequest) throws -> MusicRemoteTrack {
        guard songResultPages != nil, let origins = request.targets.first?.origins,
              let primary = origins.first?.track else { throw MusicServiceError.unsupported }
        requestedRecordings.append(request)
        recordingObserver?()
        let track = MusicRemoteTrack(ref: primary.ref, songId: primary.songId, title: primary.title,
            artist: primary.artist, album: primary.album, duration: primary.duration,
            sources: origins.flatMap { $0.track.sources })
        resolvedResultTracks[track.ref] = track
        return track
    }
    func replaceSearchBatch(_ request: MusicSearchBatchRequest, expectedRevision: Int,
                            intent: MusicQueueAuthorization) throws -> MusicPlaybackState {
        guard songResultPages != nil else { throw MusicServiceError.unsupported }
        try intent.check()
        playedBatches.append(request); batchObserver?()
        return .init()
    }
    func inspectSources(for reference: MusicReference) throws -> any MusicSourceInspectionSession {
        guard songResultPages != nil else { throw MusicServiceError.unsupported }
        return resultInspection
    }
    func inspectionCloseCount() async -> Int { await resultInspection.closeCount }
    func detailCallIDs() -> [String] { requestedDetails }
    func detailReadCount() -> Int { requestedDetails.count }
    func updateBrowse(_ page: MusicPublicBrowsePage) {
        browse = page
        for continuation in homeContinuations.values { continuation.yield(()) }
    }
    func holdHome(_ gate: MusicUIKitSearchGate) { homeGate = gate }
    func holdCatalogue(_ gate: MusicUIKitSearchGate) { catalogueGate = gate }
    func holdChannel(_ gate: MusicUIKitSearchGate) { channelGate = gate }
    func holdDetail(cursor: String?, gate: MusicUIKitSearchGate) { detailGates[cursor ?? ""] = gate }
    func holdLocalTracks(_ gate: MusicUIKitSearchGate) { localTracksGate = gate }
    func observeHomeSubscription(_ observer: @escaping @Sendable () -> Void) {
        if homeContinuations.isEmpty { homeSubscriptionObserver = observer }
        else { observer() }
    }
    func observeFMPreviews(after count: Int, _ observer: @escaping @Sendable () -> Void) {
        previewObserver = (count, observer)
        notifyFMPreview()
    }
    func fmPreview(_ action: MusicHomeAction) -> MusicFMPreview? {
        fmPreviewCount += 1; notifyFMPreview(); return nil
    }
    private func notifyFMPreview() {
        guard let observer = previewObserver, fmPreviewCount > observer.after else { return }
        previewObserver = nil; observer.body()
    }
    func observeLocalQueue(_ observer: @escaping @Sendable () -> Void) { localQueueObserver = observer }
    func holdChorus(_ gate: MusicUIKitChorusGate) { chorusGate = gate }
    func failNextChorus(_ error: MusicServiceError) { chorusFailure = error }
    func chorusStatistics() -> (calls: [String], peak: Int) { (chorusCalls, chorusPeak) }
    func chorus(attemptID: String, actualDuration: Double) async throws -> MusicChorusPoint? {
        chorusCalls.append(attemptID); chorusActive += 1; chorusPeak = max(chorusPeak, chorusActive)
        defer { chorusActive -= 1 }
        if let failure = chorusFailure { chorusFailure = nil; throw failure }
        let gate = chorusGate; chorusGate = nil
        if let gate { await gate.wait(); return .init(start: 10, end: 20) }
        return nil
    }
    func searchDiscovery(components: Set<MusicSearchDiscoveryFailure.Component>) async throws
        -> MusicSearchDiscoverySnapshot {
        requestedDiscovery.append(components)
        guard discoveryCalls < discovery.count else { throw MusicServiceError.unsupported }
        let gate = discoveryGate
        discoveryGate = nil
        if let gate { await gate.wait() }
        defer { discoveryCalls += 1 }
        return discovery[discoveryCalls]
    }
    func searchHotPlayback(keywords: [String]) async throws -> [MusicRemoteTrack] {
        throw MusicServiceError.unsupported
    }
    func homeSourceSelection() async -> MusicHomeSourceSelection {
        .init(selected: .netease, revision: 0, available: [.netease])
    }
    func selectHomeSource(_ source: MusicPublicBrowsePlatform, ifRevision: Int) async throws -> MusicHomeSourceSelection {
        .init(selected: .netease, revision: 0, available: [.netease])
    }
    func restoredHome() async throws -> MusicPublicBrowsePage? { nil }
    func homePage(cursor: String?, refresh: Bool) async throws -> MusicPublicBrowsePage {
        browseCalls += 1
        let gate = homeGate; homeGate = nil
        if let gate { await gate.wait() }
        return cursor == nil ? browse : nextBrowse ?? browse
    }
    func homeChanges() async -> AsyncStream<Void> {
        let pair = AsyncStream<Void>.makeStream(bufferingPolicy: .bufferingNewest(1))
        let id = UUID()
        homeContinuations[id] = pair.continuation
        let observer = homeSubscriptionObserver; homeSubscriptionObserver = nil; observer?()
        pair.continuation.onTermination = { [weak self] _ in
            Task { await self?.removeHomeContinuation(id) }
        }
        return pair.stream
    }
    private func removeHomeContinuation(_ id: UUID) { homeContinuations[id] = nil }
    func publicDiscovery(_ destination: MusicPublicBrowseDestination, cursor: String?) async -> MusicPublicBrowsePage {
        browseCalls += 1; discoveryRequests.append(destination)
        if case .channels = destination, let gate = catalogueGate {
            catalogueGate = nil; await gate.wait()
        } else if case .channel = destination, let gate = channelGate {
            channelGate = nil; await gate.wait()
        }
        if case .channel = destination, let channelBrowse { return channelBrowse }
        return cursor == nil ? browse : nextBrowse ?? browse
    }
    func publicPlaylists(platform: MusicPublicBrowsePlatform, query: String, cursor: String?) throws -> MusicPublicBrowseCollectionPage { throw MusicServiceError.unsupported }
    func searchPublicPlaylists(platform: MusicPublicBrowsePlatform, query: String,
                               page: Int, limit: Int) async throws -> MusicPlaylistSearchPage {
        guard let observe = resultSearchObserver else { throw MusicServiceError.unsupported }
        requestedResultReads.append(.init(section: .playlists, platform: platform.rawValue,
            query: query, page: page, limit: limit))
        observe()
        if let gate = resultPageGates[.playlists]?[page] { await gate.wait() }
        if let failure = playlistSearchFailure { throw failure }
        if songResultPages != nil {
            let pages = playlistResultPages[platform.rawValue] ?? []
            return .init(platform: platform, query: query, page: page, limit: limit,
                total: pages.reduce(0) { $0 + $1.count }, nextPage: page < pages.count ? page + 1 : nil,
                items: pages.indices.contains(page - 1) ? pages[page - 1] : [])
        }
        return .init(platform: platform, query: query, page: page, limit: limit,
                     total: 0, nextPage: nil, items: [])
    }
    func publicDetail(_ collection: MusicPublicBrowseCollection, cursor: String?) async throws
        -> MusicPublicBrowseDetail {
        if let gate = detailGates.removeValue(forKey: cursor ?? "") { await gate.wait() }
        if let detail, detail.collection.id == collection.id {
            requestedDetails.append(collection.id)
            if cursor == nil { return detail }
            guard cursor == detail.cursor, let nextDetail else { throw MusicServiceError.notFound }
            return nextDetail
        }
        guard cursor == nil, var pages = details[collection.id], !pages.isEmpty else {
            throw MusicServiceError.unsupported
        }
        requestedDetails.append(collection.id)
        let result = pages.removeFirst()
        details[collection.id] = pages
        return result
    }
    func recommendations(platform: MusicAccountPlatform, kind: String, cursor: String?) throws -> MusicBrowsePage { throw MusicServiceError.unsupported }
    func accountCollections(platform: MusicAccountPlatform, cursor: String?) throws -> MusicAccountCollectionPage { throw MusicServiceError.unsupported }
    func accountTracks(platform: MusicAccountPlatform, collectionID: String, cursor: String?) throws -> MusicBrowsePage { throw MusicServiceError.unsupported }
    func state() -> MusicPlaybackState { .init() }
    func platforms() -> [MusicPlatform] { [] }
    func search(platform: String, query: String, page: Int, limit: Int) async throws -> MusicSearchPage {
        guard let observe = resultSearchObserver else { throw MusicServiceError.unsupported }
        requestedResultReads.append(.init(section: .songs, platform: platform, query: query, page: page, limit: limit))
        if let pages = songResultPages {
            observe()
            if let gate = resultPageGates[.songs]?[page] { await gate.wait() }
            let values = pages[platform] ?? []
            return .init(platform: platform, query: query, page: page, limit: limit,
                total: values.reduce(0) { $0 + $1.count }, nextPage: page < values.count ? page + 1 : nil,
                items: values.indices.contains(page - 1) ? values[page - 1] : [])
        }
        let id = "result-\(platform)-\(requestedResultReads.count)"
        let track = MusicRemoteTrack(ref: .init(kind: .handle, id: id), songId: id,
            title: "Synthetic \(id)", artist: "Fixture", duration: 180,
            sources: [.init(sourceKey: id, platform: platform, nativeId: id)])
        observe()
        return .init(platform: platform, query: query, page: page, limit: limit,
                     total: 1, nextPage: nil, items: [track])
    }
    func collections() -> [MusicCollection] {
        if songResultPages != nil {
            return [.init(id: "favorites", title: "收藏", kind: "favorites", count: resultFavorites.count, revision: 1)]
        }
        return localTracks.isEmpty ? [] : [.init(id: collectionKind == "favorites" ? "favorites" : "playlist:1",
            title: "歌单", kind: collectionKind, count: localTracks.count, revision: 1)]
    }
    func tracks(in collection: MusicCollection) async -> [MusicRemoteTrack] {
        trackCalls += 1
        if let gate = localTracksGate { localTracksGate = nil; await gate.wait() }
        return songResultPages != nil ? resultFavorites : localTracks
    }
    func createPlaylist(name: String, refs: [MusicReference]) throws -> MusicCollection { throw MusicServiceError.unsupported }
    func editPlaylist(_ collection: MusicCollection, name: String?, refs: [MusicReference]?, order: [String]?) throws -> MusicCollection {
        guard songResultPages != nil else { throw MusicServiceError.unsupported }
        if let refs { resultFavorites = refs.compactMap { resolvedResultTracks[$0] } }
        return collection
    }
    func removePlaylist(_ collection: MusicCollection) throws { throw MusicServiceError.unsupported }
    func queue() -> MusicQueueSnapshot { .init(revision: 0, currentId: nil, entries: []) }
    func changeQueue(_ change: MusicQueueChange, expectedRevision: Int) throws -> MusicPlaybackState {
        if let localQueueObserver, case .append(let refs, _) = change {
            localQueueReferences = refs; localQueueObserver(); return .init()
        }
        guard songResultPages != nil else { throw MusicServiceError.unsupported }
        return .init()
    }
    func advance(_ direction: MusicDirection, reason: MusicAdvanceReason, expectedRevision: Int) throws -> MusicAdvanceResult { throw MusicServiceError.unsupported }
    func updateSettings(_ change: MusicSettingsChange) throws -> MusicPlaybackState { throw MusicServiceError.unsupported }
    func sourceAvailability(for ref: MusicReference) -> MusicSourceAvailability { .init(options: [], scriptState: .notConfigured) }
    func metadataPreferences() throws -> MusicMetadataPreferences { throw MusicServiceError.unsupported }
    func updateMetadataPreferences(_ change: MusicMetadataPreferencesChange) throws -> MusicMetadataPreferences { throw MusicServiceError.unsupported }
    func metadataCandidates(occurrenceID: String) throws -> MusicMetadataCandidates { throw MusicServiceError.unsupported }
    func resolve(attemptId: String, occurrenceId: String, selection: MusicPlaybackSelection) throws -> MusicPlaybackCandidate { throw MusicServiceError.unsupported }
    func cancel(attemptId: String?) {}
    func record(_ fact: MusicPlaybackFact) -> Bool { false }
    func lyrics(occurrenceId: String, sourceKey: String) throws -> MusicLyricsSnapshot { throw MusicServiceError.unsupported }
    func playedSongs() -> [MusicPlayedSong] { [] }
    func playedPlaylists() -> [MusicPlayedPlaylist] { [] }
    func restoreHistory(id: String, expectedRevision: Int) throws -> MusicPlaybackState { throw MusicServiceError.unsupported }
}

private actor MusicUIKitResultInspectionSession: MusicSourceInspectionSession {
    private let pair = AsyncThrowingStream<MusicResolvedSourcesSnapshot, Error>.makeStream()
    private(set) var closeCount = 0
    func updates() -> AsyncThrowingStream<MusicResolvedSourcesSnapshot, Error> { pair.stream }
    func loadMore(refresh: Bool) -> MusicResolvedSourcesSnapshot { .init(sources: [], hasMore: false) }
    func adopt(sourceID: String, occurrenceID: String, expectedQueueRevision: Int) throws -> MusicPlaybackSelection {
        throw MusicServiceError.unsupported
    }
    func reject(sourceKey: String, occurrenceID: String, expectedQueueRevision: Int) throws {
        throw MusicServiceError.unsupported
    }
    func close() { closeCount += 1; pair.continuation.finish() }
}

@MainActor
private final class MusicSeekRecordingPlayer: MusicPlaybackDriving {
    var seeks: [(Double, MusicPlayerAttempt)] = []
    func play(_ resource: PlaybackResource) -> MusicPlayerAttempt {
        XCTFail("Unexpected playback"); return .init(id: UUID(), resourceID: resource.id)
    }
    func pause(_ attempt: MusicPlayerAttempt) { XCTFail("Unexpected pause") }
    func resume(_ attempt: MusicPlayerAttempt) { XCTFail("Unexpected resume") }
    func seek(_ position: Double, attempt: MusicPlayerAttempt) -> Bool {
        seeks.append((position, attempt)); return true
    }
    func stop(_ attempt: MusicPlayerAttempt) { XCTFail("Unexpected stop") }
}

@MainActor
private final class MusicUIKitRegressionPlayer: MusicPlaybackDriving {
    func play(_ resource: PlaybackResource) -> MusicPlayerAttempt {
        XCTFail("Unexpected playback"); return .init(id: UUID(), resourceID: resource.id)
    }
    func pause(_ attempt: MusicPlayerAttempt) { XCTFail("Unexpected pause") }
    func resume(_ attempt: MusicPlayerAttempt) { XCTFail("Unexpected resume") }
    func seek(_ position: Double, attempt: MusicPlayerAttempt) -> Bool { XCTFail("Unexpected seek"); return false }
    func stop(_ attempt: MusicPlayerAttempt) { XCTFail("Unexpected stop") }
}

private actor MusicUIKitChorusGate {
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

private actor MusicUIKitSearchGate {
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
#endif
