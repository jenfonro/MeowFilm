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
    func testImportHistoryFailureDuringPushIsShownAndReturningDoesNotReadAgain() async throws {
        let theme = PageLifecycleTestTheme(), root = PageLifecycleTestPage(themeProvider: PageLifecycleTestTheme())
        let navigation = PageNavigationController(themeProvider: theme)
        navigation.setViewControllers([root], animated: false)
        let window = mount(navigation)
        defer { release(navigation, window: window) }
        let ready = expectation(for: NSPredicate { _, _ in
            MainActor.assumeIsolated { root.appearanceEvents.contains("didAppear") }
        }, evaluatedWith: root)
        await fulfillment(of: [ready], timeout: 2)
        let owner = MusicViewModel(service: MusicUIKitRegressionService(), player: MusicUIKitRegressionPlayer())
        let service = MusicUIKitImportHistoryFixture()
        let page = MusicImportHistoryViewController(owner: owner, service: service,
                                                    appearance: MusicAppearancePreferencesStore())
        page.loadViewIfNeeded()
        let initialReads = await service.reads
        XCTAssertEqual(initialReads, 0)
        NotificationCenter.default.post(name: UIApplication.didBecomeActiveNotification, object: nil)
        navigation.pushViewController(page, animated: true)
        navigation.view.layoutIfNeeded()
        let presented = expectation(for: NSPredicate { _, _ in
            MainActor.assumeIsolated {
                guard let alert = page.presentedViewController as? UIAlertController else { return false }
                return alert.viewIfLoaded?.window != nil && !alert.isBeingPresented &&
                    alert.transitionCoordinator == nil
            }
        }, evaluatedWith: page)
        await fulfillment(of: [presented], timeout: 2)
        let alert = try XCTUnwrap(page.presentedViewController as? UIAlertController,
            "active=\(page.isPageActive), visible=\(page.isPageVisible), canPresent=\(page.canPresentPageAlert)")
        XCTAssertEqual(alert.message, MusicServiceError.storageFailed.localizedDescription)
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
        let calls = await service.reads
        XCTAssertEqual(calls, 1)
        XCTAssertTrue(page.isPageActive)
        await owner.close()
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
            title: "榜单"), tracks: [])
        let until = Date().addingTimeInterval(3600)
        let service = MusicUIKitRegressionService(discovery: [
            .init(hot: [.init(keyword: "保留热词")], charts: [chart],
                  failures: [.init(component: .charts, chartID: "offline", error: .networkOffline),
                             .init(component: .charts, chartID: "failed", error: .timeout)], validUntil: until),
            .init(hot: [.init(keyword: "恢复热词")], charts: [chart], validUntil: until),
        ])
        let model = MusicViewModel(service: service, player: MusicUIKitRegressionPlayer())
        let controller = MusicSearchViewController(model: model, appearance: MusicAppearancePreferencesStore(),
                                                   router: MusicUIKitRegressionRouter())
        let window = mount(controller)
        defer { release(controller, window: window) }
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
        XCTAssertTrue(alert.message?.contains("音乐榜单读取") == true)
        XCTAssertTrue(alert.actions.contains { $0.title == "重试" })
        XCTAssertTrue(table.visibleCells.contains {
            ($0.contentConfiguration as? UIListContentConfiguration)?.text?.contains("保留热词") == true
        })
        XCTAssertTrue((0..<table.numberOfSections).contains {
            controller.tableView(table, titleForHeaderInSection: $0) == "音乐榜单"
        })
        await withCheckedContinuation { continuation in
            controller.dismiss(animated: false) { continuation.resume() }
        }
        controller.retryDiscovery()
        let recovered = expectation(for: NSPredicate { _, _ in
            MainActor.assumeIsolated {
                table.visibleCells.contains {
                    ($0.contentConfiguration as? UIListContentConfiguration)?.text?.contains("恢复热词") == true
                }
            }
        }, evaluatedWith: table)
        await fulfillment(of: [recovered], timeout: 2)
        controller.beginAppearanceTransition(false, animated: false)
        controller.endAppearanceTransition()
        controller.beginAppearanceTransition(true, animated: false)
        controller.endAppearanceTransition()
        let counts = await service.counts()
        XCTAssertEqual(counts.discovery, 2)
        XCTAssertNil(table.refreshControl)
        await model.close()
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

    func testLeadingCardsUseSharedGapAndUntitledPlaybackHeadingRemainsTouchable() async throws {
        let page = MusicPublicBrowsePage(sections: [
            .init(id: "cards", title: "", items: [card("cover", previews: [])], layout: .featureRail),
            .init(id: "songs", title: "", items: [
                .init(id: "song", title: "歌曲", content: .track(track("song"))),
            ], layout: .list),
        ])
        let service = MusicUIKitRegressionService(browse: page)
        let model = MusicViewModel(service: service, player: MusicUIKitRegressionPlayer())
        let controller = MusicBrowseViewController(title: "推荐", model: model, browser: service,
            request: .discovery(.home), appearance: MusicAppearancePreferencesStore(),
            router: MusicUIKitRegressionRouter(), embedded: true)
        let window = mount(controller)
        defer { release(controller, window: window) }
        let collection = try XCTUnwrap(controller.view.subviews.compactMap { $0 as? UICollectionView }.first)
        let kind = UICollectionView.elementKindSectionHeader
        let loaded = expectation(for: NSPredicate { _, _ in
            MainActor.assumeIsolated {
                collection.layoutIfNeeded()
                return collection.cellForItem(at: IndexPath(item: 0, section: 0)) != nil &&
                    collection.supplementaryView(forElementKind: kind, at: IndexPath(item: 0, section: 1)) != nil
            }
        }, evaluatedWith: collection)
        await fulfillment(of: [loaded], timeout: 2)
        let first = try XCTUnwrap(collection.cellForItem(at: IndexPath(item: 0, section: 0)))
        XCTAssertEqual(first.convert(first.bounds, to: collection).minY,
                       PageLayoutMetrics.contentGap, accuracy: 0.5)
        let header = try XCTUnwrap(collection.supplementaryView(
            forElementKind: kind, at: IndexPath(item: 0, section: 1)))
        let play = try XCTUnwrap(descendants(header).compactMap { $0 as? UIButton }.first { !$0.isHidden })
        XCTAssertGreaterThan(play.bounds.height, 0)
        XCTAssertTrue(header.hitTest(play.center, with: nil) === play)
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
        let window = mount(controller)
        defer { release(controller, window: window) }
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

    func testEditorDragUpdatesVisibleNumbersWithoutReplacingCells() async throws {
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
        let table = try XCTUnwrap(editor.view.subviews.compactMap { $0 as? UITableView }.first)
        table.layoutIfNeeded()
        let cells = try (0..<4).map { try XCTUnwrap(table.cellForRow(at: IndexPath(row: $0, section: 0))) }
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
        XCTAssertEqual(draft.selectedTracks.map(\.songId), ["a"])
        for (index, original) in [1, 2, 0, 3].enumerated() {
            let cell = try XCTUnwrap(table.cellForRow(at: IndexPath(row: index, section: 0)))
            XCTAssertTrue(cell === cells[original])
            XCTAssertEqual(cell.accessoryType, draft.tracks[index].songId == "a" ? .checkmark : .none)
            XCTAssertTrue(descendants(cell).compactMap { $0 as? UILabel }.contains {
                $0.text == "\(index + 1). \(draft.tracks[index].title)"
            })
        }
        page.disappear()
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
    private func card(_ id: String, previews: [MusicRemoteTrack]) -> MusicPublicBrowseItem {
        .init(id: id, title: id, content: .unavailable, previewSongs: previews)
    }
    private func preview(_ title: String, in view: UIView) throws -> UIButton {
        try XCTUnwrap(descendants(view).compactMap { $0 as? UIButton }.first { $0.accessibilityLabel == "播放" + title })
    }
    private func descendants(_ view: UIView) -> [UIView] {
        view.subviews.flatMap { [$0] + descendants($0) }
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
        XCTAssertFalse(fullPlay === order[2])
        XCTAssertEqual(fullPlay.layer.sublayers?.first { $0.name == "play.progress.track" }?.opacity, 0)
        let fullControls = try XCTUnwrap(dockViews(full.view).compactMap { $0 as? MusicPlaybackControlsView }.first)
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
        model.candidate = .init(attemptId: id, occurrenceId: id, resourceId: resource.id,
                                url: url, title: id, requestedQuality: "128k")
        model.lastSelection = .init(installationId: "official:fixture", generation: 1,
                                    siteId: "wy", sourceKey: "wy:" + id, quality: "128k")
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
    func showSearch() { XCTFail("Unexpected navigation") }
    func showSettings() { XCTFail("Unexpected navigation") }
    func showPublicBrowse(_ request: MusicPublicBrowseRequest, title: String) { XCTFail("Unexpected navigation") }
    func showPublicCollection(_ collection: MusicPublicBrowseCollection) { XCTFail("Unexpected navigation") }
    func showLocalCollection(_ collection: MusicCollection) { XCTFail("Unexpected navigation") }
    func showPlayer(_ destination: MusicPlaybackDestination) { XCTFail("Unexpected navigation") }
    func selectChannel(_ channel: MusicPublicBrowseChannel) { XCTFail("Unexpected navigation") }
}

private actor MusicUIKitRegressionService: MusicServing, MusicAccountBrowsing, MusicPublicBrowsing,
    MusicSearchDiscoveryProviding, MusicChorusProviding {
    private let discovery: [MusicSearchDiscoverySnapshot]
    private let browse: MusicPublicBrowsePage
    private let localTracks: [MusicRemoteTrack]
    private let collectionKind: String
    private var discoveryCalls = 0, browseCalls = 0, trackCalls = 0
    private var chorusCalls: [String] = [], chorusActive = 0, chorusPeak = 0
    private var chorusGate: MusicUIKitChorusGate?
    private var chorusFailure: MusicServiceError?
    init(discovery: [MusicSearchDiscoverySnapshot] = [], browse: MusicPublicBrowsePage = .init(sections: []),
         localTracks: [MusicRemoteTrack] = [], collectionKind: String = "playlist") {
        self.discovery = discovery; self.browse = browse; self.localTracks = localTracks
        self.collectionKind = collectionKind
    }
    func counts() -> (discovery: Int, browse: Int, tracks: Int) { (discoveryCalls, browseCalls, trackCalls) }
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
    func searchDiscovery() throws -> MusicSearchDiscoverySnapshot {
        guard discoveryCalls < discovery.count else { throw MusicServiceError.unsupported }
        defer { discoveryCalls += 1 }
        return discovery[discoveryCalls]
    }
    func publicDiscovery(_ destination: MusicPublicBrowseDestination, cursor: String?) -> MusicPublicBrowsePage {
        browseCalls += 1; return browse
    }
    func publicPlaylists(platform: MusicPublicBrowsePlatform, query: String, cursor: String?) throws -> MusicPublicBrowseCollectionPage { throw MusicServiceError.unsupported }
    func publicDetail(_ collection: MusicPublicBrowseCollection, cursor: String?) throws -> MusicPublicBrowseDetail { throw MusicServiceError.unsupported }
    func recommendations(platform: MusicAccountPlatform, kind: String, cursor: String?) throws -> MusicBrowsePage { throw MusicServiceError.unsupported }
    func accountCollections(platform: MusicAccountPlatform, cursor: String?) throws -> MusicAccountCollectionPage { throw MusicServiceError.unsupported }
    func accountTracks(platform: MusicAccountPlatform, collectionID: String, cursor: String?) throws -> MusicBrowsePage { throw MusicServiceError.unsupported }
    func state() -> MusicPlaybackState { .init() }
    func platforms() -> [MusicPlatform] { [] }
    func search(platform: String, query: String, page: Int) throws -> MusicSearchPage { throw MusicServiceError.unsupported }
    func collections() -> [MusicCollection] {
        localTracks.isEmpty ? [] : [.init(id: collectionKind == "favorites" ? "favorites" : "playlist:1",
            title: "歌单", kind: collectionKind, count: localTracks.count, revision: 1)]
    }
    func tracks(in collection: MusicCollection) -> [MusicRemoteTrack] { trackCalls += 1; return localTracks }
    func createPlaylist(name: String, refs: [MusicReference]) throws -> MusicCollection { throw MusicServiceError.unsupported }
    func editPlaylist(_ collection: MusicCollection, name: String?, refs: [MusicReference]?, order: [String]?) throws -> MusicCollection { throw MusicServiceError.unsupported }
    func removePlaylist(_ collection: MusicCollection) throws { throw MusicServiceError.unsupported }
    func queue() -> MusicQueueSnapshot { .init(revision: 0, currentId: nil, entries: []) }
    func changeQueue(_ change: MusicQueueChange, expectedRevision: Int) throws -> MusicPlaybackState { throw MusicServiceError.unsupported }
    func advance(_ direction: MusicDirection, reason: MusicAdvanceReason, expectedRevision: Int) throws -> MusicAdvanceResult { throw MusicServiceError.unsupported }
    func updateSettings(_ change: MusicSettingsChange) throws -> MusicPlaybackState { throw MusicServiceError.unsupported }
    func playbackOptions(for ref: MusicReference) -> [MusicPlaybackOption] { [] }
    func resolve(attemptId: String, occurrenceId: String, selection: MusicPlaybackSelection) throws -> MusicPlaybackCandidate { throw MusicServiceError.unsupported }
    func cancel(attemptId: String?) {}
    func record(_ fact: MusicPlaybackFact) -> Bool { false }
    func lyrics(occurrenceId: String, sourceKey: String) throws -> MusicLyricsSnapshot { throw MusicServiceError.unsupported }
    func playedSongs() -> [MusicPlayedSong] { [] }
    func playedPlaylists() -> [MusicPlayedPlaylist] { [] }
    func restoreHistory(id: String, expectedRevision: Int) throws -> MusicPlaybackState { throw MusicServiceError.unsupported }
    func prepareImport(data: Data, sourceId: String, ownerId: String) throws -> MusicImportPreview { throw MusicServiceError.unsupported }
    func commitImport(_ preview: MusicImportPreview) throws -> MusicImportReport { throw MusicServiceError.unsupported }
    func releaseImport(_ preview: MusicImportPreview) {}
    func rollbackImport(batchId: String) throws { throw MusicServiceError.unsupported }
}

private actor MusicUIKitImportHistoryFixture: MusicImportHistoryProviding {
    private(set) var reads = 0
    func importHistory() throws -> MusicImportHistorySnapshot {
        reads += 1
        throw MusicServiceError.storageFailed
    }
    func rollbackImportedLibrary(_ authorization: MusicImportRollbackAuthorization) {
        XCTFail("Opening history must not roll back the library")
    }
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
#endif
