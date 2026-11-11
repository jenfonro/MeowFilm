#if os(iOS)
import XCTest
import UIKit
import MeowUI
import MeowPlayback
@testable import MeowMusicFeature

@MainActor final class MusicNetworkPlaylistUIKitTests: XCTestCase {
    func testLibraryGroupsNetworkReferencesAndRoutesOnlyTheirExplicitRemoteIdentity() async throws {
        let service = NetworkPlaylistUIKitService(), router = NetworkPlaylistUIKitRouter()
        let model = MusicViewModel(service: service, playback: PlaybackController())
        await model.load()
        let page = MusicLibraryViewController(model: model, appearance: MusicAppearancePreferencesStore(), router: router)
        let scene = UIApplication.shared.connectedScenes.compactMap { $0 as? UIWindowScene }.first
        let window = scene.map { UIWindow(windowScene: $0) } ?? UIWindow(frame: CGRect(x: 0, y: 0, width: 390, height: 844))
        page.loadViewIfNeeded(); window.rootViewController = page; window.makeKeyAndVisible()
        window.layoutIfNeeded(); page.view.layoutIfNeeded()
        NotificationCenter.default.post(name: UIApplication.didBecomeActiveNotification, object: nil)
        defer { PageViewController.invalidatePages(in: page); window.isHidden = true; window.rootViewController = nil }
        let list = try XCTUnwrap(page.view.subviews.compactMap { $0 as? UICollectionView }.first)
        let ready = expectation(for: NSPredicate { _, _ in MainActor.assumeIsolated {
            page.isPageActive && list.numberOfSections == 2
        } }, evaluatedWith: nil)
        await fulfillment(of: [ready], timeout: 4)
        XCTAssertEqual(list.numberOfItems(inSection: 0), 2); XCTAssertEqual(list.numberOfItems(inSection: 1), 1)
        list.layoutIfNeeded()
        let managedHeader = list.supplementaryView(forElementKind: UICollectionView.elementKindSectionHeader, at: IndexPath(item: 0, section: 0))
        let networkHeader = list.supplementaryView(forElementKind: UICollectionView.elementKindSectionHeader, at: IndexPath(item: 0, section: 1))
        XCTAssertEqual(managedHeader?.accessibilityIdentifier, "music.library.managed")
        XCTAssertEqual(networkHeader?.accessibilityIdentifier, "music.library.network")
        page.collectionView(list, didSelectItemAt: IndexPath(item: 1, section: 0))
        page.collectionView(list, didSelectItemAt: IndexPath(item: 0, section: 1))
        XCTAssertEqual(router.local?.id, "playlist:1")
        XCTAssertEqual(router.remote, .init(platform: .qq, nativeID: "501", title: "Same", count: 9))
        XCTAssertNil(page.collectionView(list, contextMenuConfigurationForItemAt: IndexPath(item: 1, section: 0), point: .zero))
        XCTAssertNotNil(page.collectionView(list, contextMenuConfigurationForItemAt: IndexPath(item: 0, section: 1), point: .zero))
        let reads = await service.memberReads
        XCTAssertEqual(reads, 0)
        await model.close()
    }

    func testSearchSongMenuKeepsOnlyManagedPlaylistsWhenCollectionsChangeDuringFavoritesRead() async throws {
        let suite = "MusicNetworkPlaylistUIKitTests.search." + UUID().uuidString
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        let key = MusicSearchPreferencesStore.preferencesKey, saved = UserDefaults.standard.object(forKey: key)
        UserDefaults.standard.set(try JSONEncoder().encode(MusicSearchPreferences(
            songPlatforms: [.qq], searchPlaylists: false)), forKey: key)
        defer {
            UserDefaults.standard.set(saved, forKey: key)
            defaults.removePersistentDomain(forName: suite)
        }
        let service = NetworkPlaylistUIKitService(), router = NetworkPlaylistUIKitRouter()
        let model = MusicViewModel(service: service, playback: PlaybackController())
        await model.load()
        let appearance = MusicAppearancePreferencesStore(defaults: defaults)
        let root = MusicSearchViewController(model: model, appearance: appearance, router: router,
                                            history: SearchHistoryStore(defaults: defaults))
        let navigation = PageNavigationController(themeProvider: appearance)
        navigation.setViewControllers([root], animated: false)
        let scene = UIApplication.shared.connectedScenes.compactMap { $0 as? UIWindowScene }.first
        let window = scene.map { UIWindow(windowScene: $0) } ?? UIWindow(frame: CGRect(x: 0, y: 0, width: 390, height: 844))
        navigation.loadViewIfNeeded(); window.rootViewController = navigation; window.makeKeyAndVisible()
        window.layoutIfNeeded(); navigation.view.layoutIfNeeded()
        NotificationCenter.default.post(name: UIApplication.didBecomeActiveNotification, object: nil)
        defer { PageViewController.invalidatePages(in: navigation); window.isHidden = true; window.rootViewController = nil }
        do {
            let shown = expectation(description: "search results presented")
            navigation.didShowPage = { page in
                if page is MusicSearchResultsViewController { shown.fulfill() }
            }
            root.query = "测试录音"; root.onSubmit?("测试录音")
            await fulfillment(of: [shown], timeout: 3)
            navigation.didShowPage = nil
            let page = try XCTUnwrap(navigation.topViewController as? MusicSearchResultsViewController)
            NotificationCenter.default.post(name: UIApplication.didBecomeActiveNotification, object: nil)
            let ready = expectation(for: NSPredicate { _, _ in MainActor.assumeIsolated {
                page.view.layoutIfNeeded()
                return page.canPresentPageAlert && self.descendants(page.view).compactMap { $0 as? UIButton }
                    .contains { $0.accessibilityIdentifier?.hasPrefix("music.search.song.more.") == true && $0.menu != nil }
            } }, evaluatedWith: nil)
            await fulfillment(of: [ready], timeout: 4)
            let more = try XCTUnwrap(descendants(page.view).compactMap { $0 as? UIButton }
                .first { $0.accessibilityIdentifier?.hasPrefix("music.search.song.more.") == true })
            let menu = try XCTUnwrap(more.menu)
            XCTAssertEqual(menu.children.map(\.title), ["下一首播放", "添加到播放队列", "音源", "添加到歌单"])
            let targets = try XCTUnwrap(menu.children.compactMap { $0 as? UIMenu }.first { $0.title == "添加到歌单" })
            XCTAssertEqual(targets.children.map(\.title), ["Same"], "同名网络引用和 favorites 均不能成为添加目标")
            XCTAssertTrue(targets.children.allSatisfy { $0 is UIAction })
            let table = try XCTUnwrap(descendants(page.view).compactMap { $0 as? UITableView }
                .first { $0.accessibilityIdentifier == "music.search.results.page.1" })
            let row = try XCTUnwrap(table.visibleCells.first { $0 is MusicSearchSongCell })
            for prefix in ["music.search.song.info.", "music.search.song.favorite.", "music.search.song.append."] {
                XCTAssertTrue(descendants(row).compactMap { $0 as? UIButton }
                    .contains { $0.accessibilityIdentifier?.hasPrefix(prefix) == true && $0.isEnabled })
            }

            let reading = expectation(description: "favorites refresh held")
            await service.holdFavoritesRead { reading.fulfill() }
            await model.createPlaylist("First managed")
            await fulfillment(of: [reading], timeout: 3)
            let firstUpdate = expectation(for: NSPredicate { _, _ in MainActor.assumeIsolated {
                more.menu?.children.compactMap { $0 as? UIMenu }.first { $0.title == "添加到歌单" }?
                    .children.map(\.title) == ["Same", "First managed"]
            } }, evaluatedWith: nil)
            await fulfillment(of: [firstUpdate], timeout: 4)
            for _ in 0..<2 {
                await withCheckedContinuation { continuation in RunLoop.main.perform { continuation.resume() } }
            }
            await model.createPlaylist("Latest managed")
            XCTAssertNil(model.error)
            let updated = expectation(for: NSPredicate { _, _ in MainActor.assumeIsolated {
                more.menu?.children.compactMap { $0 as? UIMenu }.first { $0.title == "添加到歌单" }?
                    .children.map(\.title) == ["Same", "First managed", "Latest managed"]
            } }, evaluatedWith: nil)
            await fulfillment(of: [updated], timeout: 4)
            let beforeSave = try XCTUnwrap(more.menu)
            try await model.saveNetworkPlaylist(.init(platform: .qq, nativeID: "502", title: "New network"))
            XCTAssertTrue(model.collections.contains { $0.title == "New network" && $0.kind == "network" })
            let savedNetwork = expectation(for: NSPredicate { _, _ in MainActor.assumeIsolated {
                more.menu !== beforeSave &&
                    more.menu?.children.compactMap { $0 as? UIMenu }.first { $0.title == "添加到歌单" }?
                        .children.map(\.title) == ["Same", "First managed", "Latest managed"]
            } }, evaluatedWith: nil)
            await fulfillment(of: [savedNetwork], timeout: 4)
            let refreshed = try XCTUnwrap(more.menu)
            XCTAssertEqual(refreshed.children.map(\.title), ["下一首播放", "添加到播放队列", "音源", "添加到歌单"])
            XCTAssertTrue(table.visibleCells.contains { $0 === row }, "集合刷新不能依赖歌曲行滚出复用")
            XCTAssertTrue(descendants(row).contains { $0 === more })
            let held = await service.isFavoritesReadHeld
            XCTAssertTrue(held, "歌单菜单刷新不能等待收藏曲目读取完成")
        } catch {
            await service.resumeFavoritesRead()
            await model.close()
            throw error
        }
        await service.resumeFavoritesRead()
        await model.close()
    }

    func testDailyRecommendationCannotBeSavedButSelectedTrackPlaysCompleteSource() async throws {
        try await activateRecommendationRunner()
        let service = NetworkPlaylistUIKitService(), router = NetworkPlaylistUIKitRouter()
        let model = MusicViewModel(service: service, playback: PlaybackController())
        await model.load()
        addTeardownBlock { await model.close() }
        let appearance = MusicAppearancePreferencesStore()
        let page = MusicCollectionViewController(model: model,
            recommendation: .discovery(.daily(.netease)), browser: service,
            appearance: appearance, router: router)
        page.loadViewIfNeeded()
        let list = try XCTUnwrap(page.view.subviews.compactMap { $0 as? UITableView }.first)
        // This case isolates complete-source playback from browsing pagination.
        // Do not let UITableView's initial willDisplay consume the tail;
        // the row selection below still goes through the real controller method.
        list.delegate = nil
        let navigation = PageNavigationController(themeProvider: appearance)
        navigation.setViewControllers([page], animated: false)
        let scene = try XCTUnwrap(UIApplication.shared.connectedScenes.compactMap { $0 as? UIWindowScene }
            .first { $0.activationState == .foregroundActive })
        let previousKeyWindow = scene.windows.first { $0.isKeyWindow }
        let window = UIWindow(windowScene: scene)
        window.frame = scene.coordinateSpace.bounds
        navigation.loadViewIfNeeded(); window.rootViewController = navigation; window.makeKeyAndVisible()
        window.layoutIfNeeded(); navigation.view.layoutIfNeeded()
        XCTAssertEqual(UIApplication.shared.applicationState, .active)
        defer {
            PageViewController.invalidatePages(in: navigation)
            window.isHidden = true; window.rootViewController = nil
            previousKeyWindow?.makeKey()
        }
        let ready = expectation(for: NSPredicate { _, _ in MainActor.assumeIsolated {
            list.layoutIfNeeded()
            return page.canPresentPageAlert && list.numberOfSections == 1 && list.numberOfRows(inSection: 0) == 30 &&
                list.cellForRow(at: IndexPath(row: 1, section: 0)) != nil
        } }, evaluatedWith: nil)
        await fulfillment(of: [ready], timeout: 4)
        XCTAssertFalse(descendants(page.view).contains {
            $0.accessibilityIdentifier == "music.collection.save" && !$0.isHidden
        })
        let branches = try XCTUnwrap(descendants(page.pageHeader).compactMap { $0 as? UISegmentedControl }.first)
        XCTAssertEqual(branches.selectedSegmentIndex, 0)
        XCTAssertGreaterThan(branches.bounds.width, 160)
        let bar = try XCTUnwrap(page.view.subviews.compactMap { $0 as? MusicCollectionDetailActionBar }.first)
        XCTAssertEqual(bar.frame.minY, list.frame.minY, accuracy: 0.5)
        attachRecommendationView(navigation.view, name: "daily-default-light")
        XCTAssertTrue(model.queue.isEmpty, "浏览推荐页不能提前提交队列")
        XCTAssertEqual(list.numberOfRows(inSection: 0), 30)
        page.tableView(list, didSelectRowAt: IndexPath(row: 1, section: 0))
        let played = expectation(for: NSPredicate { _, _ in MainActor.assumeIsolated {
            model.queue.count == 31 && model.current?.track.songId == "daily-1" && !model.isPreparingRemoteQueue
        } }, evaluatedWith: nil)
        await fulfillment(of: [played], timeout: 4)
        XCTAssertEqual(model.queue.map(\.track.songId), (0..<31).map { "daily-\($0)" })
        XCTAssertEqual(list.numberOfRows(inSection: 0), 30, "未展示的尾页仍必须加入完整播放队列")
        let opened = await service.openedAccountSources, committed = await service.committedSource
        let selected = await service.committedIndex, displayedWrites = await service.displayedQueueWrites
        XCTAssertEqual(opened.count, 1); XCTAssertEqual(committed, opened.first)
        XCTAssertEqual(selected, 1); XCTAssertEqual(displayedWrites, 0)
        XCTAssertTrue(model.queue.allSatisfy { $0.remoteSessionID == committed && $0.sourceTitle == "每日推荐" })
    }

    func testDailyBranchesKeepQueueAndStyleSheetCancelKeepsConfirmedSelection() async throws {
        try await activateRecommendationRunner()
        let service = NetworkPlaylistUIKitService(), router = NetworkPlaylistUIKitRouter()
        let model = MusicViewModel(service: service, playback: PlaybackController())
        await model.load()
        addTeardownBlock { await model.close() }
        let appearance = MusicAppearancePreferencesStore()
        let page = MusicCollectionViewController(model: model,
            recommendation: .accountRecommendation(.netease, kind: "daily", title: "每日推荐"),
            browser: service, appearance: appearance, router: router)
        let navigation = PageNavigationController(themeProvider: appearance)
        navigation.setViewControllers([page], animated: false)
        let scene = try XCTUnwrap(UIApplication.shared.connectedScenes.compactMap { $0 as? UIWindowScene }
            .first { $0.activationState == .foregroundActive })
        let previousKeyWindow = scene.windows.first { $0.isKeyWindow }
        let window = UIWindow(windowScene: scene)
        window.frame = scene.coordinateSpace.bounds
        navigation.loadViewIfNeeded(); window.rootViewController = navigation; window.makeKeyAndVisible()
        window.layoutIfNeeded(); navigation.view.layoutIfNeeded()
        XCTAssertEqual(UIApplication.shared.applicationState, .active)
        defer {
            PageViewController.invalidatePages(in: navigation)
            window.isHidden = true; window.rootViewController = nil
            previousKeyWindow?.makeKey()
        }
        let table = try XCTUnwrap(page.view.subviews.compactMap { $0 as? UITableView }.first)
        let branches = try XCTUnwrap(descendants(page.pageHeader).compactMap { $0 as? UISegmentedControl }.first)
        let ready = expectation(for: NSPredicate { _, _ in MainActor.assumeIsolated {
            page.canPresentPageAlert && table.numberOfRows(inSection: 0) >= 30
        } }, evaluatedWith: nil)
        await fulfillment(of: [ready], timeout: 4)
        let queueBefore = model.queue.map(\.id)
        table.setContentOffset(CGPoint(x: 0, y: 144), animated: false)
        branches.selectedSegmentIndex = 1; branches.sendActions(for: .valueChanged)
        let styleReady = expectation(for: NSPredicate { _, _ in MainActor.assumeIsolated {
            table.layoutIfNeeded()
            return self.descendants(page.view).compactMap { $0 as? UIButton }.contains {
                $0.accessibilityIdentifier == "music.dailyStyle.current" && $0.isEnabled &&
                $0.title(for: .normal)?.contains("服务标签甲") == true
            }
        } }, evaluatedWith: nil)
        await fulfillment(of: [styleReady], timeout: 4)
        XCTAssertEqual(table.contentOffset.y, 0, accuracy: 0.5)
        XCTAssertEqual(model.queue.map(\.id), queueBefore)
        let capsule = try XCTUnwrap(descendants(page.view).compactMap { $0 as? UIButton }.first {
            $0.accessibilityIdentifier == "music.dailyStyle.current"
        })
        let bar = try XCTUnwrap(page.view.subviews.compactMap { $0 as? MusicCollectionDetailActionBar }.first)
        let capsuleFrame = capsule.convert(capsule.bounds, to: page.view)
        let firstRow = table.convert(table.rectForRow(at: IndexPath(row: 0, section: 0)), to: page.view)
        XCTAssertGreaterThanOrEqual(capsuleFrame.minY, bar.frame.maxY)
        XCTAssertLessThanOrEqual(capsuleFrame.maxY, firstRow.minY)
        XCTAssertEqual(bar.frame.minY, table.frame.minY, accuracy: 0.5)
        attachRecommendationView(navigation.view, name: "daily-style-light")
        window.overrideUserInterfaceStyle = .dark
        let darkReady = expectation(for: NSPredicate { _, _ in MainActor.assumeIsolated {
            navigation.view.layoutIfNeeded()
            return page.theme.dark && capsule.traitCollection.userInterfaceStyle == .dark &&
                capsule.backgroundColor == UIColor(page.theme.chip)
        } }, evaluatedWith: nil)
        await fulfillment(of: [darkReady], timeout: 4)
        XCTAssertTrue(page.theme.dark)
        guard page.theme.dark, capsule.traitCollection.userInterfaceStyle == .dark,
              capsule.backgroundColor == UIColor(page.theme.chip) else { return }
        table.setContentOffset(CGPoint(x: 0, y: 120), animated: false)
        table.layoutIfNeeded()
        XCTAssertEqual(bar.frame.minY, table.frame.minY, accuracy: 0.5)
        XCTAssertLessThan(capsule.convert(capsule.bounds, to: page.view).maxY, bar.frame.maxY)
        attachRecommendationView(navigation.view, name: "daily-style-dark-pinned")
        table.setContentOffset(.zero, animated: false)
        table.layoutIfNeeded()
        capsule.sendActions(for: .touchUpInside)
        let presented = expectation(for: NSPredicate { _, _ in MainActor.assumeIsolated {
            guard let wrapper = page.presentedViewController as? PageNavigationController,
                  let editor = wrapper.topViewController as? MusicDailyStyleViewController else { return false }
            return UIApplication.shared.applicationState == .active && editor.canPresentPageAlert &&
                editor.theme.dark && self.descendants(editor.pageHeader)
                .compactMap { $0 as? PageHeaderTextButton }.contains { $0.isEnabled }
        } }, evaluatedWith: nil)
        await fulfillment(of: [presented], timeout: 4)
        let wrapper = try XCTUnwrap(page.presentedViewController as? PageNavigationController)
        let editor = try XCTUnwrap(wrapper.topViewController as? MusicDailyStyleViewController)
        XCTAssertTrue(editor.theme.dark)
        attachRecommendationView(wrapper.view, name: "daily-style-editor-dark")
        let close = try XCTUnwrap(descendants(editor.pageHeader).compactMap { $0 as? PageHeaderButton }
            .first { $0.accessibilityLabel == "关闭" })
        close.sendActions(for: .touchUpInside)
        let dismissed = expectation(for: NSPredicate { _, _ in MainActor.assumeIsolated {
            page.presentedViewController == nil && page.canPresentPageAlert
        } }, evaluatedWith: nil)
        await fulfillment(of: [dismissed], timeout: 4)
        XCTAssertTrue(capsule.title(for: .normal)?.contains("服务标签甲") == true)
        let writes = await service.styleWrites
        XCTAssertEqual(writes, 0)
        XCTAssertEqual(model.queue.map(\.id), queueBefore)
        branches.selectedSegmentIndex = 0; branches.sendActions(for: .valueChanged)
        let returned = expectation(for: NSPredicate { _, _ in MainActor.assumeIsolated {
            capsule.isHidden && branches.selectedSegmentIndex == 0 && table.numberOfRows(inSection: 0) >= 30
        } }, evaluatedWith: nil)
        await fulfillment(of: [returned], timeout: 4)
        XCTAssertEqual(table.contentOffset.y, 0, accuracy: 0.5)
        XCTAssertEqual(model.queue.map(\.id), queueBefore)
    }

    // Activate the actual XCTest runner; synthetic lifecycle notifications do not
    // foreground a newly presented sheet or complete UIKit's presentation transitions.
    private func activateRecommendationRunner() async throws {
        let identifier = try XCTUnwrap(Bundle.main.bundleIdentifier)
        await withCheckedContinuation { continuation in
            RunLoop.main.perform {
                MainActor.assumeIsolated { XCUIApplication(bundleIdentifier: identifier).activate() }
                continuation.resume()
            }
        }
        let active = expectation(for: NSPredicate { _, _ in MainActor.assumeIsolated {
            UIApplication.shared.applicationState == .active
        } }, evaluatedWith: nil)
        await fulfillment(of: [active], timeout: 4)
    }

    private func attachRecommendationView(_ view: UIView, name: String) {
        view.layoutIfNeeded()
        let image = UIGraphicsImageRenderer(size: view.bounds.size).image { renderer in
            if view.window != nil {
                XCTAssertTrue(view.drawHierarchy(in: view.bounds, afterScreenUpdates: true))
            } else {
                view.layer.render(in: renderer.cgContext)
            }
        }
        let attachment = XCTAttachment(image: image)
        attachment.name = name
        attachment.lifetime = .keepAlways
        add(attachment)
    }

    private func descendants(_ view: UIView) -> [UIView] {
        view.subviews.flatMap { [$0] + descendants($0) }
    }
}

@MainActor private final class NetworkPlaylistUIKitRouter: MusicUIRouting {
    let primaryPlaybackPresentation = MusicMiniPlayerPresentation()
    var local: MusicCollection?
    var remote: MusicPublicBrowseCollection?
    func showSearch() { XCTFail("unexpected navigation") }
    func showSettings() { XCTFail("unexpected navigation") }
    func showPublicBrowse(_ request: MusicPublicBrowseRequest, title: String) { XCTFail("unexpected navigation") }
    func showLocalCollection(_ collection: MusicCollection) { local = collection }
    func showPublicCollection(_ collection: MusicPublicBrowseCollection) { remote = collection }
    func showPlayer(_ destination: MusicPlaybackDestination) { XCTFail("unexpected playback") }
    func selectChannel(_ channel: MusicPublicBrowseChannel) { XCTFail("unexpected navigation") }
}

private actor NetworkPlaylistUIKitService: MusicServing, MusicAccountBrowsing, MusicNetworkPlaylistServing,
    MusicPublicBrowsing, MusicRemoteQueueServing, MusicDisplayedQueuePlaying, MusicHomeActionQueueServing {
    private(set) var memberReads = 0
    private(set) var openedAccountSources: [UUID] = []
    private(set) var committedSource: UUID?
    private(set) var committedIndex: Int?
    private(set) var displayedQueueWrites = 0
    private var playbackQueue = MusicQueueSnapshot(revision: 0, currentId: nil, entries: [])
    private var catalogue: [MusicCollection] =
        [.init(id: "explicit-network-reference", title: "Same", kind: "network", count: 9, revision: 1,
               networkReference: .init(platform: .qq, remoteID: "501")),
         .init(id: "favorites", title: "Favorites", kind: "favorites"),
         .init(id: "playlist:1", title: "Same")]
    private var favoritesReadObserver: (@Sendable () -> Void)?
    private var favoritesRead: CheckedContinuation<[MusicRemoteTrack], Never>?
    var isFavoritesReadHeld: Bool { favoritesRead != nil }
    func holdFavoritesRead(_ observer: @escaping @Sendable () -> Void) { favoritesReadObserver = observer }
    func resumeFavoritesRead() {
        favoritesReadObserver = nil
        let read = favoritesRead; favoritesRead = nil
        read?.resume(returning: [])
    }
    func state() -> MusicPlaybackState {
        .init(revision: playbackQueue.revision, currentId: playbackQueue.currentId, total: playbackQueue.entries.count)
    }
    func platforms() -> [MusicPlatform] { [] }
    func collections() -> [MusicCollection] { catalogue }
    func tracks(in collection: MusicCollection) async throws -> [MusicRemoteTrack] {
        memberReads += 1
        guard collection.kind == "favorites" else { throw MusicServiceError.unsupported }
        if let observer = favoritesReadObserver {
            return await withCheckedContinuation { continuation in
                favoritesRead = continuation; observer()
            }
        }
        return []
    }
    func queue() -> MusicQueueSnapshot { playbackQueue }
    func search(platform: String, query: String, page: Int, limit: Int) throws -> MusicSearchPage {
        guard platform == "tx", page == 1 else { throw MusicServiceError.unsupported }
        let track = MusicRemoteTrack(ref: .init(kind: .handle, id: "search-song"), songId: "search-song",
            title: "测试录音", artist: "Fixture", duration: 180,
            sources: [.init(sourceKey: "tx:search-song", platform: "tx", nativeId: "search-song")])
        return .init(platform: platform, query: query, page: page, limit: limit, total: 1, nextPage: nil, items: [track])
    }
    func recommendations(platform: MusicAccountPlatform, kind: String, cursor: String?) throws -> MusicBrowsePage { throw MusicServiceError.unsupported }
    func accountCollections(platform: MusicAccountPlatform, cursor: String?) throws -> MusicAccountCollectionPage { throw MusicServiceError.unsupported }
    func accountTracks(platform: MusicAccountPlatform, collectionID: String, cursor: String?) throws -> MusicBrowsePage { throw MusicServiceError.unsupported }
    func publicPlaylists(platform: MusicPublicBrowsePlatform, query: String, cursor: String?) throws -> MusicPublicBrowseCollectionPage { throw MusicServiceError.unsupported }
    func publicDetail(_ collection: MusicPublicBrowseCollection, cursor: String?) throws -> MusicPublicBrowseDetail { throw MusicServiceError.unsupported }
    func publicDiscovery(_ destination: MusicPublicBrowseDestination, cursor: String?) throws -> MusicPublicBrowsePage { throw MusicServiceError.unsupported }
    func openAccountQueue(platform: MusicAccountPlatform, kind: MusicAccountQueueKind, title: String) throws -> MusicRemoteQueueSource {
        guard platform == .netease, kind == .recommendation(kind: "daily") else { throw MusicServiceError.unsupported }
        let source = MusicRemoteQueueSource(
            context: .init(kind: .recommendation, id: "daily", title: title, platform: "wy"),
            loadPage: { cursor in
                let start = cursor.flatMap(Int.init) ?? 0, end = min(start + 30, 31)
                let tracks = (start..<end).map { index in
                    MusicRemoteTrack(ref: .init(kind: .handle, id: "daily-\(index)"), songId: "daily-\(index)",
                        title: "Daily \(index + 1)", artist: "Fixture",
                        sources: [.init(sourceKey: "wy:daily-\(index)", platform: "wy", nativeId: "daily-\(index)")])
                }
                return .init(items: try tracks.map { .init(platform: "wy", data: try JSONEncoder().encode($0)) },
                             total: 31, cursor: end < 31 ? String(end) : nil)
            })
        openedAccountSources.append(source.id)
        return source
    }
    private(set) var styleWrites = 0
    static let styleSelection = MusicDailyStyleSelection(category: "1000", tags: [.init(id: "7", name: "服务标签甲")])
    func openHomeActionQueue(_ action: MusicHomeAction, title: String) throws -> MusicRemoteQueueSource {
        guard case .daily(let branch) = action else { throw MusicServiceError.unsupported }
        if branch == .default {
            return try openAccountQueue(platform: .netease, kind: .recommendation(kind: "daily"), title: title)
        }
        return MusicRemoteQueueSource(
            context: .init(kind: .recommendation, id: "daily-style", title: title, platform: "wy"),
            loadPage: { _ in try Self.stylePage(Self.styleSelection) },
            dailyStyle: .init(categories: {
                [.init(id: "1000", name: "服务分类甲", tags: [
                    .init(id: "7", name: "服务标签甲"), .init(id: "8", name: "服务标签乙"),
                    .init(id: "9", name: "服务返回的长标签")]),
                 .init(id: "2000", name: "服务分类乙", tags: [.init(id: "10", name: "服务标签丙")])]
            }, save: { selection in try await self.saveStyle(selection) }), homeAction: action)
    }
    private static func stylePage(_ selection: MusicDailyStyleSelection) throws -> MusicNativeTrackPage {
        let tracks = (0..<12).map { index in
            MusicRemoteTrack(ref: .init(kind: .handle, id: "style-\(index)"), songId: "style-\(index)",
                title: "风格歌曲 \(index + 1)", artist: "Fixture",
                sources: [.init(sourceKey: "wy:style-\(index)", platform: "wy", nativeId: "style-\(index)")])
        }
        return .init(items: try tracks.map { .init(platform: "wy", data: try JSONEncoder().encode($0)) },
                     total: tracks.count, dailyStyle: selection)
    }
    private func saveStyle(_ selection: MusicDailyStyleSelection) throws -> MusicDailyStyleSave {
        styleWrites += 1
        return .init(page: try Self.stylePage(selection), saveConfirmed: true, selectionChanged: false)
    }
    func remoteQueuePage(_ source: MusicRemoteQueueSource, cursor: String?) async throws -> MusicBrowsePage {
        let page = try await source.page(cursor: cursor)
        return .init(items: try page.items.map { try JSONDecoder().decode(MusicRemoteTrack.self, from: $0.data) },
                     cursor: page.cursor, dailyStyle: page.dailyStyle)
    }
    func replaceRemoteQueue(_ source: MusicRemoteQueueSource, start: MusicRemoteQueueStart,
                            expectedRevision: Int, intent: MusicQueueAuthorization) async throws -> MusicPlaybackState {
        let values = try await source.completeTracks(intent: intent)
        let tracks = try values.map { try JSONDecoder().decode(MusicRemoteTrack.self, from: $0.data) }
        try intent.check(); try source.authorization.check()
        guard expectedRevision == playbackQueue.revision else { throw MusicServiceError.staleRevision }
        guard tracks.indices.contains(start.index), tracks[start.index].songId == start.songID,
              tracks[start.index].sources.map(\.sourceKey) == start.sourceKeys else { throw MusicServiceError.targetChanged }
        playbackQueue = .init(revision: expectedRevision + 1, currentId: "entry-\(start.index)",
            entries: tracks.enumerated().map {
                .init(id: "entry-\($0.offset)", track: $0.element, sourceTitle: source.context.title, remoteSessionID: source.id)
            })
        committedSource = source.id; committedIndex = start.index
        return state()
    }
    func replaceDisplayedQueue(_ tracks: [MusicRemoteTrack], index: Int, origin: MusicDisplayedQueueOrigin,
                               expectedRevision: Int, intent: MusicQueueAuthorization) throws -> MusicPlaybackState {
        displayedQueueWrites += 1
        throw MusicServiceError.unsupported
    }
    func advanceFM(_ source: MusicRemoteQueueSource, direction: MusicDirection, reason: MusicAdvanceReason,
                   expectedRevision: Int, intent: MusicQueueAuthorization) throws -> MusicAdvanceResult { throw MusicServiceError.unsupported }
    func createPlaylist(name: String, refs: [MusicReference]) -> MusicCollection {
        let collection = MusicCollection(id: "playlist:\(catalogue.count)", title: name, count: refs.count, revision: 1)
        catalogue.append(collection)
        return collection
    }
    func saveNetworkPlaylist(_ collection: MusicPublicBrowseCollection, intent: MusicQueueAuthorization) throws -> MusicCollection {
        try intent.check()
        let reference = MusicCollection(id: "network:\(collection.id)", title: collection.title, kind: "network",
            networkReference: .init(platform: collection.platform, remoteID: collection.nativeID))
        catalogue.append(reference)
        return reference
    }
    func removeNetworkPlaylist(_ collection: MusicCollection, intent: MusicQueueAuthorization) throws { throw MusicServiceError.unsupported }
    func editPlaylist(_ collection: MusicCollection, name: String?, refs: [MusicReference]?, order: [String]?) throws -> MusicCollection { throw MusicServiceError.unsupported }
    func removePlaylist(_ collection: MusicCollection) throws { throw MusicServiceError.unsupported }
    func changeQueue(_ change: MusicQueueChange, expectedRevision: Int) throws -> MusicPlaybackState { throw MusicServiceError.unsupported }
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
#endif
