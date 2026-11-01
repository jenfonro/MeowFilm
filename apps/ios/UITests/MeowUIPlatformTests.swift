import XCTest
import UIKit
import Combine
@testable import MeowUI

@MainActor
final class MeowUIPlatformTests: XCTestCase {
    private var window: UIWindow?
    private weak var previousKeyWindow: UIWindow?

    func testNavigationOwnsPageLifetimeAndDisablesSystemPopGestures() async throws {
        let theme = TestPageTheme()
        let firstPage = LifecyclePage(themeProvider: theme), secondPage = LifecyclePage(themeProvider: theme)
        let first = host(firstPage), second = host(secondPage)
        let retained = RetainedPagesController(pageFactories: [{ first }, { second }])
        let navigation = PageNavigationController(themeProvider: theme)
        navigation.setViewControllers([retained], animated: false)
        firstPage.activation = expectation(description: "first page appeared")
        let appeared = firstPage.activation!
        try display(navigation)
        activateForeground()
        await fulfillment(of: [appeared], timeout: 3)
        XCTAssertTrue(firstPage.isPageActive)
        XCTAssertFalse(secondPage.isPageActive)
        XCTAssertEqual(retained.children.count, 1)
        XCTAssertTrue(retained.children.first === first)

        XCTAssertTrue(navigation.interactivePopGestureRecognizer?.isEnabled == false)
        if #available(iOS 26.0, *) {
            XCTAssertTrue(navigation.interactiveContentPopGestureRecognizer?.isEnabled == false)
        }
        let started = expectation(description: "page task started")
        let finished = expectation(description: "removed page task observed cancellation")
        var continuation: CheckedContinuation<Void, Never>?
        firstPage.runPageTask {
            await withCheckedContinuation { continuation = $0; started.fulfill() }
            XCTAssertTrue(Task.isCancelled)
            finished.fulfill()
        }
        await fulfillment(of: [started], timeout: 3)
        secondPage.activation = expectation(description: "retained selection appeared")
        let selected = secondPage.activation!
        let selectionCompleted = expectation(description: "retained selection completed")
        retained.didSelect = { if $0 == 1 { selectionCompleted.fulfill() } }
        retained.select(1, animated: false)
        activateForeground()
        await fulfillment(of: [selected, selectionCompleted], timeout: 3)
        retained.didSelect = nil
        XCTAssertTrue(firstPage.isPageActive)
        XCTAssertFalse(firstPage.isPageVisible)
        XCTAssertTrue(secondPage.isPageActive)
        XCTAssertEqual(firstPage.inactiveCount, 0)
        XCTAssertEqual(retained.children.count, 2)
        XCTAssertTrue(first.parent === retained)

        let detail = PageViewController(themeProvider: theme)
        navigation.pushViewController(detail, animated: false)
        XCTAssertTrue(navigation.topViewController === detail)
        XCTAssertFalse(detail.view.gestureRecognizers?.contains { $0 is UIScreenEdgePanGestureRecognizer } ?? false)
        XCTAssertTrue(secondPage.isPageActive)
        XCTAssertEqual(navigation.viewControllers.count, 2)
        XCTAssertTrue(navigation.popViewController(animated: false) === detail)
        XCTAssertFalse(detail.isPageActive)
        XCTAssertTrue(navigation.topViewController === retained)
        XCTAssertTrue(secondPage.isPageActive)
        XCTAssertEqual(retained.selectedIndex, 1)
        NotificationCenter.default.post(name: UIApplication.willResignActiveNotification, object: nil)
        XCTAssertTrue(secondPage.isPageActive)
        NotificationCenter.default.post(name: UIApplication.didBecomeActiveNotification, object: nil)
        XCTAssertTrue(secondPage.isPageActive)
        XCTAssertTrue(firstPage.isPageActive)
        XCTAssertEqual(firstPage.inactiveCount, 0)
        PageViewController.invalidatePages(in: navigation)
        XCTAssertFalse(firstPage.isPageActive)
        XCTAssertFalse(secondPage.isPageActive)
        continuation?.resume()
        await fulfillment(of: [finished], timeout: 3)
    }

    func testAppOwnedHeaderReplacesEverySystemNavigationItem() async throws {
        let theme = TestPageTheme()
        let root = PageViewController(themeProvider: theme)
        let rootItem = root.navigationItem
        root.pageTitle = "首页"
        let detail = PageViewController(themeProvider: theme)
        detail.pageTitle = "详情"
        let navigation = PageNavigationController(themeProvider: theme)
        navigation.setViewControllers([root], animated: false)
        let window = try display(navigation)
        XCTAssertTrue(navigation.navigationBar.isHidden)
        XCTAssertNil(root.title)
        XCTAssertNil(navigation.navigationBar.topItem?.title)
        XCTAssertNil(root.navigationItem.title)
        XCTAssertNil(root.navigationItem.titleView)
        XCTAssertNil(root.navigationItem.leftBarButtonItem)
        XCTAssertNil(root.navigationItem.rightBarButtonItem)
        XCTAssertNil(root.navigationItem.searchController)
        XCTAssertTrue(root.navigationItem === rootItem)
        XCTAssertEqual(root.pageHeader.title, "首页")

        let detailShown = expectation(description: "app-owned detail appeared")
        navigation.didShowPage = { if $0 === detail { detailShown.fulfill() } }
        push(detail, on: navigation)
        await fulfillment(of: [detailShown], timeout: 3)
        navigation.didShowPage = nil
        XCTAssertTrue(navigation.navigationBar.isHidden)
        XCTAssertTrue(navigation.topViewController === detail)
        XCTAssertNil(detail.title)
        XCTAssertNil(navigation.navigationBar.topItem?.title)
        XCTAssertNil(navigation.navigationBar.backItem?.title)
        let itemPositions: (UINavigationController) -> [Int] = { owner in
            (owner.navigationBar.items ?? []).map { item in
                owner.viewControllers.firstIndex { $0.navigationItem === item } ?? -1
            }
        }
        let appItemPositions = itemPositions(navigation)
        XCTAssertFalse(appItemPositions.contains(-1), "Every system item must be the page's native item")

        // A hidden bar can retain its previously displayed items. Compare the
        // native behavior instead of using that invisible UI cursor as the stack.
        let nativeRoot = UIViewController(), nativeDetail = UIViewController()
        let native = UINavigationController(rootViewController: nativeRoot)
        native.setNavigationBarHidden(true, animated: false)
        let receipt = NativeNavigationDelegate()
        let nativeShown = expectation(description: "native detail appeared")
        receipt.didShow = { if $0 === nativeDetail { nativeShown.fulfill() } }
        native.delegate = receipt
        window.rootViewController = native
        window.layoutIfNeeded()
        push(nativeDetail, on: native)
        await fulfillment(of: [nativeShown], timeout: 3)
        receipt.didShow = nil
        XCTAssertEqual(appItemPositions, itemPositions(native))
        let comparison = XCTAttachment(string: "App-owned: \(appItemPositions); native UIKit: \(itemPositions(native))")
        comparison.name = "hidden-navigation-items"
        comparison.lifetime = .keepAlways
        add(comparison)
        window.rootViewController = navigation
        window.layoutIfNeeded()
        XCTAssertNil(detail.navigationItem.title)
        XCTAssertNil(detail.navigationItem.titleView)
        XCTAssertNil(detail.navigationItem.leftBarButtonItem)
        XCTAssertNil(detail.navigationItem.rightBarButtonItem)
        XCTAssertNil(detail.navigationItem.searchController)
        XCTAssertEqual(detail.pageHeader.title, "详情")
        detail.pageTitle = "新标题"
        XCTAssertEqual(detail.pageHeader.title, "新标题")
        XCTAssertNil(detail.title)
        XCTAssertNil(detail.navigationItem.title)
        XCTAssertTrue(detail.pageHeader.hasExplicitLeading)
        let returnButton = try XCTUnwrap(descendants(detail.pageHeader).compactMap { $0 as? PageHeaderButton }.first)
        XCTAssertEqual(returnButton.accessibilityLabel, "返回")
        XCTAssertFalse(returnButton.isHighlighted)
        returnButton.isHighlighted = true
        XCTAssertFalse(returnButton.isHighlighted)

        let returned = expectation(description: "header return completed")
        navigation.didShowPage = { if $0 === root { returned.fulfill() } }
        returnButton.sendActions(for: .touchUpInside)
        settle(navigation)
        await fulfillment(of: [returned], timeout: 3)
        navigation.didShowPage = nil
        XCTAssertTrue(navigation.topViewController === root)
        XCTAssertEqual(root.pageHeader.title, "首页")
        XCTAssertFalse(root.pageHeader.hasExplicitLeading)
    }

    func testInteractiveBackGestureAndHeaderButtonUseTheSamePopPath() throws {
        let theme = TestPageTheme()
        let root = PageViewController(themeProvider: theme)
        let detail = PageViewController(themeProvider: theme)
        let navigation = PageNavigationController(themeProvider: theme)
        navigation.setViewControllers([root], animated: false)
        try display(navigation)
        push(detail, on: navigation)
        XCTAssertTrue(navigation.interactivePopGestureRecognizer?.isEnabled == false)
        if #available(iOS 26.0, *) {
            XCTAssertTrue(navigation.interactiveContentPopGestureRecognizer?.isEnabled == false)
        }
        XCTAssertTrue(navigation.view.gestureRecognizers?.contains(navigation.interactiveBackGesture) == true)
        navigation.requestBack()
        settle(navigation)
        XCTAssertTrue(navigation.topViewController === root)
    }

    func testIndependentPageTransitionCancelsAndRevealsActualPresenter() async throws {
        let theme = TestPageTheme()
        let root = PageViewController(themeProvider: theme)
        try display(root)
        root.view.layoutIfNeeded()
        let dock = UIView(frame: CGRect(x: 18, y: 400, width: 300, height: 100))
        let dockPlay = UIButton(type: .system)
        dock.addSubview(dockPlay)
        root.view.addSubview(dock)
        let full = PageViewController(themeProvider: theme)
        full.loadViewIfNeeded()
        let fullPlay = UIButton(type: .system)
        full.view.addSubview(fullPlay)
        let transition = BottomPageTransition()
        transition.installEdgeDismissal(on: full)
        XCTAssertEqual(transition.edgeGesture.edges, .left)
        XCTAssertTrue(full.view.isOpaque)

        for (presenting, cancelled) in [(true, true), (true, false), (false, true), (false, false)] {
            let delegate = try XCTUnwrap(presenting
                ? transition.animationController(forPresented: full, presenting: root, source: root)
                : transition.animationController(forDismissed: full))
            let context = SurfaceContext(container: root.view, from: presenting ? root : full,
                                         to: presenting ? full : root, frame: root.view.bounds)
            let completed = expectation(description: "independent page transition completion")
            context.completed = { completed.fulfill() }
            let animator = try XCTUnwrap(delegate.interruptibleAnimator?(using: context) as? UIViewPropertyAnimator)
            XCTAssertTrue(delegate.interruptibleAnimator?(using: context) === animator)
            XCTAssertTrue(transition.isTransitioning)
            animator.startAnimation(afterDelay: 1)
            animator.pauseAnimation()
            XCTAssertEqual(full.view.transform.tx, 0, accuracy: 0.5)
            XCTAssertEqual(full.view.transform.ty, presenting ? 0 : root.view.bounds.height, accuracy: 0.5)
            context.transitionWasCancelled = cancelled
            animator.stopAnimation(false)
            animator.finishAnimation(at: cancelled ? .start : .end)
            await fulfillment(of: [completed], timeout: 3)
            XCTAssertEqual(context.result, !cancelled)
            XCTAssertFalse(transition.isTransitioning)
            XCTAssertTrue(dock.superview === root.view)
            XCTAssertTrue(dockPlay.superview === dock)
            XCTAssertTrue(fullPlay.superview === full.view)
            XCTAssertFalse(dockPlay === fullPlay)
            XCTAssertEqual(root.view.transform, .identity)
            XCTAssertEqual(full.view.transform, .identity)
            if presenting == cancelled { XCTAssertNil(full.view.superview) }
            else { XCTAssertTrue(full.view.superview === root.view) }
        }
    }

    func testEditorConfirmationBlocksTheAppOwnedPopUntilReleased() throws {
        let theme = TestPageTheme()
        let root = PageViewController(themeProvider: theme)
        let editor = ConfirmingPage(themeProvider: theme)
        let navigation = PageNavigationController(themeProvider: theme)
        navigation.setViewControllers([root, editor], animated: false)
        try display(navigation)
        navigation.view.layoutIfNeeded(); editor.view.layoutIfNeeded()
        XCTAssertTrue(editor.requestsBackConfirmationForNavigationPop())
        navigation.requestBack()
        settle(navigation)
        XCTAssertTrue(navigation.topViewController === editor)
        XCTAssertEqual(editor.confirmations, 1)
        editor.confirm = false
        navigation.requestBack()
        settle(navigation)
        XCTAssertTrue(navigation.topViewController === root)
    }

    func testHeaderControlsKeepTheirGeometryAcrossContentReplacementAndResize() throws {
        let header = PageHeaderView(themeProvider: TestPageTheme())
        let back = PageHeaderButton(image: PageIcon.image(.back), accessibilityLabel: "返回") {}
        let done = PageHeaderTextButton(title: "完成") {}
        header.setLeading(back)
        header.setTrailing([done])
        let custom = PageHeaderTextButton(title: "频道") {}
        for (width, top) in [(CGFloat(375), CGFloat(0)), (440, 62), (390, 20)] {
            header.statusBarHeight = top
            header.frame = CGRect(x: 0, y: 0, width: width, height: top + PageLayoutMetrics.navigation)
            header.setTitleView(custom)
            header.layoutIfNeeded()
            let titleFrame = custom.convert(custom.bounds, to: header)
            let backFrame = back.convert(back.bounds, to: header)
            let doneFrame = done.convert(done.bounds, to: header)
            XCTAssertEqual(titleFrame.height, PageLayoutMetrics.searchControl, accuracy: 0.5)
            XCTAssertEqual(titleFrame.midY, top + PageLayoutMetrics.navigation / 2, accuracy: 0.5)
            XCTAssertEqual(backFrame.width, 44, accuracy: 0.5)
            XCTAssertEqual(doneFrame.width, done.intrinsicContentSize.width, accuracy: 0.5)
            XCTAssertGreaterThan(titleFrame.width, width * 0.6)
            XCTAssertGreaterThanOrEqual(titleFrame.minX, backFrame.maxX)
            XCTAssertLessThanOrEqual(titleFrame.maxX, doneFrame.minX)
            header.setTitleView(nil)
            header.title = "页面"
            header.layoutIfNeeded()
            let label = try XCTUnwrap(descendants(header).compactMap { $0 as? UILabel }
                .first { $0.text == "页面" })
            let frame = label.convert(label.bounds, to: header)
            XCTAssertEqual(frame.midX, width / 2, accuracy: 0.5)
            XCTAssertEqual(frame.midY, titleFrame.midY, accuracy: 0.5)
            XCTAssertGreaterThan(frame.width, 0)
        }
        header.setTrailing([])
        header.layoutIfNeeded()
        XCTAssertEqual(header.trailingCount, 0)
        done.isHighlighted = true
        XCTAssertFalse(done.isHighlighted)
    }

    func testHeaderlessWindowContainerDoesNotObscureItsPageHeader() throws {
        let theme = TestPageTheme()
        let root = HeaderlessContainer(themeProvider: theme)
        let navigation = PageNavigationController(themeProvider: theme)
        let page = PageViewController(themeProvider: theme)
        page.pageTitle = "首页"
        navigation.setViewControllers([page], animated: false)
        root.loadViewIfNeeded()
        root.addChild(navigation)
        root.view.addSubview(navigation.view)
        navigation.view.pinEdges(to: root.view)
        navigation.didMove(toParent: root)
        try display(root)
        root.view.layoutIfNeeded()
        page.view.layoutIfNeeded()
        XCTAssertFalse(root.hasOwnPageHeader)
        XCTAssertTrue(page.hasOwnPageHeader)
        XCTAssertFalse(root.view.subviews.contains { $0 is PageHeaderView })
        XCTAssertEqual(page.pageHeader.title, "首页")
        XCTAssertEqual(page.additionalSafeAreaInsets.top, 0)
        XCTAssertEqual(page.pageHeader.frame.height,
                       page.view.safeAreaInsets.top + PageLayoutMetrics.navigation, accuracy: 0.5)
    }

    func testHeaderControlsRespectHorizontalSafeAreaWithoutShrinkingTheirBackground() throws {
        let page = PageViewController(themeProvider: TestPageTheme())
        page.loadViewIfNeeded()
        let back = PageHeaderButton(image: PageIcon.image(.back), accessibilityLabel: "返回") {}
        let done = PageHeaderTextButton(title: "完成") {}
        let content = UIView()
        page.pageHeader.setLeading(back)
        page.pageHeader.setTrailing([done])
        page.pageHeader.setTitleView(content)
        try display(page)
        for (left, right) in [(CGFloat(0), CGFloat(0)), (59, 59), (36, 72)] {
            page.additionalSafeAreaInsets = UIEdgeInsets(top: 0, left: left, bottom: 0, right: right)
            page.view.layoutIfNeeded()
            page.pageHeader.layoutIfNeeded()
            let header = page.pageHeader
            let safe = header.safeAreaLayoutGuide.layoutFrame
            let backFrame = back.convert(back.bounds, to: header)
            let doneFrame = done.convert(done.bounds, to: header)
            let titleFrame = content.convert(content.bounds, to: header)
            XCTAssertEqual(header.frame.width, page.view.bounds.width, accuracy: 0.5)
            XCTAssertEqual(header.background.frame, header.bounds)
            XCTAssertEqual(header.contentFrame.minX, safe.minX, accuracy: 0.5)
            XCTAssertEqual(header.contentFrame.maxX, safe.maxX, accuracy: 0.5)
            XCTAssertEqual(backFrame.minX, safe.minX + 8, accuracy: 0.5)
            XCTAssertEqual(doneFrame.maxX, safe.maxX - 8, accuracy: 0.5)
            XCTAssertGreaterThan(titleFrame.width, 0)
            XCTAssertGreaterThanOrEqual(titleFrame.minX, backFrame.maxX)
            XCTAssertLessThanOrEqual(titleFrame.maxX, doneFrame.minX)
        }
    }

    func testSystemAlertsKeepTheirLayoutWithAnAppOwnedPageHeader() async throws {
        let plain = UIViewController()
        plain.view.backgroundColor = .white
        let page = PageViewController(themeProvider: TestPageTheme())
        let navigation = PageNavigationController(themeProvider: page.themeProvider)
        navigation.setViewControllers([page], animated: false)
        let window = try display(plain)
        var frames: [CGRect] = []
        for (index, presenter) in [plain, navigation].enumerated() {
            window.rootViewController = presenter
            window.layoutIfNeeded()
            let alert = UIAlertController(title: "错误", message: "验收错误", preferredStyle: .alert)
            alert.addAction(UIAlertAction(title: "取消", style: .cancel))
            let shown = expectation(description: "native alert appeared")
            presenter.present(alert, animated: false) { shown.fulfill() }
            await fulfillment(of: [shown], timeout: 3)
            window.layoutIfNeeded()
            alert.view.layoutIfNeeded()
            let label = try XCTUnwrap(descendants(alert.view).compactMap { $0 as? UILabel }
                .first { $0.text == "错误" })
            let frame = label.convert(label.bounds, to: window)
            XCTAssertGreaterThan(frame.width, 0)
            XCTAssertTrue(window.bounds.contains(frame))
            XCTAssertFalse(descendants(alert.view).contains { $0 is PageHeaderView })
            frames.append(frame)
            // Component fixtures run in the test runner, not the foreground
            // App. Render their actual layers rather than a blank window snapshot.
            let image = UIGraphicsImageRenderer(bounds: alert.view.bounds).image { context in
                alert.view.layer.render(in: context.cgContext)
            }
            let attachment = XCTAttachment(image: image)
            attachment.name = index == 0 ? "native-alert" : "page-header-alert"
            attachment.lifetime = .keepAlways
            add(attachment)
            let closed = expectation(description: "native alert dismissed")
            alert.dismiss(animated: false) { closed.fulfill() }
            await fulfillment(of: [closed], timeout: 3)
        }
        XCTAssertEqual(frames[0].midX, frames[1].midX, accuracy: 1)
        XCTAssertEqual(frames[0].midY, frames[1].midY, accuracy: 1)
        XCTAssertEqual(frames[0].size, frames[1].size)
    }

    func testPagesRethemeTheirOwnHeadersInsideTheNavigationStack() async throws {
        let theme = TestPageTheme()
        let navigation = PageNavigationController(themeProvider: theme)
        let covered = PageViewController(themeProvider: theme)
        let top = PageViewController(themeProvider: theme)
        navigation.setViewControllers([covered, top], animated: false)
        try display(navigation)
        settle(navigation)
        covered.loadViewIfNeeded()
        theme.setColorway(.mint)
        let expected = UIColor(PageTheme(palette: .mint).accent)
        let rethemed = expectation(for: NSPredicate { _, _ in
            MainActor.assumeIsolated {
                covered.view.tintColor == expected && top.view.tintColor == expected
            }
        }, evaluatedWith: navigation)
        await fulfillment(of: [rethemed], timeout: 2)
    }

    func testPushSettlesIncomingHeaderBeforeTheSlideStarts() throws {
        let theme = TestPageTheme()
        let navigation = PageNavigationController(themeProvider: theme)
        let from = PageViewController(themeProvider: theme)
        let to = PageViewController(themeProvider: theme)
        let host = UIViewController()
        host.loadViewIfNeeded()
        let container = UIView()
        host.view.addSubview(container)
        container.pinEdges(to: host.view)
        try display(host)
        host.view.layoutIfNeeded()
        let insets = container.safeAreaInsets
        XCTAssertGreaterThan(insets.top, 0, "The host must provide a real top safe area")
        from.loadViewIfNeeded()
        to.loadViewIfNeeded()
        container.addSubview(from.view)
        from.view.frame = container.bounds
        let delegate = try XCTUnwrap(navigation.navigationController(
            navigation, animationControllerFor: .push, from: from, to: to
        ))
        let context = SurfaceContext(container: container, from: from, to: to, frame: container.bounds)
        _ = try XCTUnwrap(delegate.interruptibleAnimator?(using: context))
        // The animator must settle the incoming page's geometry before the
        // horizontal animation starts; no further layout is performed here.
        XCTAssertEqual(to.pageHeader.statusBarHeight, insets.top, accuracy: 0.5)
        XCTAssertEqual(to.pageHeader.frame.height,
                       insets.top + PageLayoutMetrics.navigation, accuracy: 0.5)
    }

    func testContentLayoutDoesNotResetScrollPositionOrOverwriteDynamicHeaderTitle() throws {
        let page = PageViewController(themeProvider: TestPageTheme())
        page.pageTitle = "页面"
        page.loadViewIfNeeded()
        let scroll = UIScrollView()
        page.view.addSubview(scroll)
        scroll.translatesAutoresizingMaskIntoConstraints = false
        NSLayoutConstraint.activate([
            scroll.leadingAnchor.constraint(equalTo: page.view.leadingAnchor),
            scroll.trailingAnchor.constraint(equalTo: page.view.trailingAnchor),
            scroll.topAnchor.constraint(equalTo: page.contentTopAnchor),
            scroll.bottomAnchor.constraint(equalTo: page.view.safeAreaLayoutGuide.bottomAnchor),
        ])
        try display(page)
        page.view.layoutIfNeeded()
        scroll.contentSize = CGSize(width: scroll.bounds.width, height: 3000)
        scroll.contentOffset.y = 300
        page.pageHeader.title = "动态标题"
        for _ in 0..<3 {
            page.view.setNeedsLayout()
            page.view.layoutIfNeeded()
            XCTAssertEqual(scroll.contentOffset.y, 300, accuracy: 0.5)
            XCTAssertEqual(scroll.frame.minY, page.pageHeader.frame.maxY, accuracy: 0.5)
            XCTAssertEqual(page.pageHeader.title, "动态标题")
        }
        scroll.contentSize.width = scroll.bounds.width * 2
        let point = CGPoint(x: scroll.frame.midX, y: scroll.frame.midY)
        scroll.contentOffset.x = 100
        XCTAssertFalse(page.shouldBeginInteractiveBack(at: point, in: page.view))
        scroll.contentOffset.x = 0
        XCTAssertTrue(page.shouldBeginInteractiveBack(at: point, in: page.view))
    }

    func testHorizontalStackTransitionsKeepPreviousPageStationaryAndCleanUpCancellation() async throws {
        let theme = TestPageTheme()
        let navigation = PageNavigationController(themeProvider: theme)
        let first = PageViewController(themeProvider: theme)
        let second = PageViewController(themeProvider: theme)
        let container = UIView(frame: CGRect(x: 0, y: 0, width: 390, height: 844))
        first.view.frame = container.bounds
        second.view.frame = container.bounds
        for operation in [UINavigationController.Operation.push, .pop] {
            for cancelled in [false, true] {
                container.addSubview(first.view)
                let from = operation == .push ? first : second
                let to = operation == .push ? second : first
                container.addSubview(from.view)
                let delegate = try XCTUnwrap(navigation.navigationController(
                    navigation, animationControllerFor: operation, from: from, to: to
                ))
                let context = SurfaceContext(container: container, from: from, to: to, frame: container.bounds)
                let completed = expectation(description: "stack transition completed")
                context.completed = { completed.fulfill() }
                let animator = try XCTUnwrap(delegate.interruptibleAnimator?(using: context) as? UIViewPropertyAnimator)
                XCTAssertTrue(delegate.interruptibleAnimator?(using: context) === animator)
                animator.startAnimation(afterDelay: 1)
                animator.pauseAnimation()
                XCTAssertEqual(first.view.transform, .identity)
                XCTAssertEqual(first.view.alpha, 1)
                XCTAssertEqual(first.view.layer.shadowOpacity, 0)
                context.transitionWasCancelled = cancelled
                animator.stopAnimation(false)
                animator.finishAnimation(at: cancelled ? .start : .end)
                await fulfillment(of: [completed], timeout: 3)
                XCTAssertEqual(context.result, !cancelled)
                XCTAssertEqual(first.view.transform, .identity)
                XCTAssertEqual(second.view.transform, .identity)
                first.view.removeFromSuperview()
                second.view.removeFromSuperview()
            }
        }
    }

    /// Non-animated push and pop start their deferred transition, and the
    /// navigation delegate receipts, on the navigation view's next layout.
    private func settle(_ navigation: UINavigationController) { navigation.view.layoutIfNeeded() }

    private func push(_ page: UIViewController, on navigation: UINavigationController) {
        navigation.pushViewController(page, animated: false)
        settle(navigation)
    }

    @discardableResult
    private func pop(_ navigation: UINavigationController) -> UIViewController? {
        defer { settle(navigation) }
        return navigation.popViewController(animated: false)
    }

    @discardableResult
    private func display(_ controller: UIViewController) throws -> UIWindow {
        let scene = try XCTUnwrap(UIApplication.shared.connectedScenes.compactMap { $0 as? UIWindowScene }
            .first, "The UI test runner has no scene")
        previousKeyWindow = scene.windows.first { $0.isKeyWindow }
        let value = UIWindow(windowScene: scene)
        value.frame = scene.coordinateSpace.bounds
        value.overrideUserInterfaceStyle = .light
        value.windowLevel = .normal + 1
        value.rootViewController = controller
        window = value
        value.makeKeyAndVisible()
        value.layoutIfNeeded()
        return value
    }

    private func host(_ page: UIViewController) -> UIViewController {
        let host = UIViewController()
        host.loadViewIfNeeded()
        host.addChild(page)
        host.view.addSubview(page.view)
        page.view.pinEdges(to: host.view)
        page.didMove(toParent: host)
        return host
    }

    private func activateForeground() {
        NotificationCenter.default.post(name: UIApplication.didBecomeActiveNotification, object: nil)
    }

    private func descendants(_ view: UIView) -> [UIView] {
        view.subviews.flatMap { [$0] + descendants($0) }
    }

    override func tearDown() {
        window?.isHidden = true
        window?.rootViewController = nil
        window = nil
        previousKeyWindow?.makeKey()
        previousKeyWindow = nil
        super.tearDown()
    }
}

@MainActor
private final class NativeNavigationDelegate: NSObject, UINavigationControllerDelegate {
    var didShow: ((UIViewController) -> Void)?

    func navigationController(_ navigationController: UINavigationController,
                              didShow viewController: UIViewController, animated: Bool) {
        didShow?(viewController)
    }
}

@MainActor
private final class ConfirmingPage: PageViewController {
    var confirm = true
    private(set) var confirmations = 0
    override func requestsBackConfirmationForNavigationPop() -> Bool { confirm }
    override func requestBackConfirmationForNavigationPop() { confirmations += 1 }
}

@MainActor
private final class HeaderlessContainer: PageViewController {
    override var usesPageHeader: Bool { false }
}

@MainActor
private final class LifecyclePage: PageViewController {
    var activation: XCTestExpectation?
    private(set) var inactiveCount = 0
    override func pageDidBecomeActive() { activation?.fulfill(); activation = nil }
    override func pageDidBecomeInactive() { inactiveCount += 1 }
}

/// UIKit normally supplies this context. The fixture drives production native
/// animators deterministically; it is not a real touch or system-pop driver.
@MainActor
private final class SurfaceContext: NSObject, UIViewControllerContextTransitioning {
    let containerView: UIView
    let isAnimated = true, isInteractive = false
    let presentationStyle = UIModalPresentationStyle.custom
    let targetTransform = CGAffineTransform.identity
    var transitionWasCancelled = false
    var completed: (() -> Void)?
    private(set) var result: Bool?
    private let from: UIViewController, to: UIViewController, frame: CGRect
    init(container: UIView, from: UIViewController, to: UIViewController, frame: CGRect) {
        containerView = container; self.from = from; self.to = to; self.frame = frame
    }
    func viewController(forKey key: UITransitionContextViewControllerKey) -> UIViewController? { key == .from ? from : to }
    func view(forKey key: UITransitionContextViewKey) -> UIView? { key == .from ? from.view : to.view }
    func initialFrame(for vc: UIViewController) -> CGRect { frame }
    func finalFrame(for vc: UIViewController) -> CGRect { frame }
    func updateInteractiveTransition(_ percentComplete: CGFloat) { }
    func finishInteractiveTransition() { }
    func cancelInteractiveTransition() { transitionWasCancelled = true }
    func pauseInteractiveTransition() { }
    func completeTransition(_ didComplete: Bool) { result = didComplete; completed?() }
}
