import XCTest
import UIKit
import Combine
@testable import MeowUI

@MainActor
final class MeowUIPlatformTests: XCTestCase {
    private var window: UIWindow?
    private weak var previousKeyWindow: UIWindow?

    func testOrdinaryNavigationLeavesSystemPopOwnershipUntouched() async throws {
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

        let system = try XCTUnwrap(navigation.interactivePopGestureRecognizer)
        let delegate = system.delegate
        let content: UIGestureRecognizer?
        if #available(iOS 26.0, *) { content = navigation.interactiveContentPopGestureRecognizer }
        else { content = nil }
        let contentDelegate = content?.delegate
        let edges = navigation.view.gestureRecognizers?.filter { $0 is UIScreenEdgePanGestureRecognizer }.count
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
        XCTAssertTrue(navigation.interactivePopGestureRecognizer === system)
        XCTAssertTrue(system.delegate === delegate)
        XCTAssertTrue(content?.delegate === contentDelegate)
        XCTAssertEqual(navigation.view.gestureRecognizers?.filter { $0 is UIScreenEdgePanGestureRecognizer }.count, edges)
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

    func testPlainBackButtonTracksTheSourceAcrossPushPopAndStackReplacement() throws {
        let theme = TestPageTheme()
        let root = PageViewController(themeProvider: theme)
        let first = PlainBackPage(themeProvider: theme), second = PlainBackPage(themeProvider: theme)
        let ordinary = PageViewController(themeProvider: theme)
        let pages: [UIViewController] = [root, first, second, ordinary]
        let navigation = PageNavigationController(themeProvider: theme)
        navigation.setViewControllers([root], animated: false)
        try display(navigation)
        XCTAssertFalse(root.prefersPlainBackButton)
        XCTAssertTrue(first.prefersPlainBackButton)
        try assertPlainBackSource(nil, pages: pages)

        push(first, on: navigation)
        XCTAssertTrue(navigation.topViewController === first)
        try assertPlainBackSource(root, pages: pages)
        push(second, on: navigation)
        XCTAssertTrue(navigation.topViewController === second)
        try assertPlainBackSource(first, pages: pages)
        push(ordinary, on: navigation)
        XCTAssertTrue(navigation.topViewController === ordinary)
        try assertPlainBackSource(nil, pages: pages)

        XCTAssertTrue(pop(navigation) === ordinary)
        XCTAssertTrue(navigation.topViewController === second)
        try assertPlainBackSource(first, pages: pages)
        XCTAssertTrue(pop(navigation) === second)
        XCTAssertTrue(navigation.topViewController === first)
        try assertPlainBackSource(root, pages: pages)
        XCTAssertTrue(pop(navigation) === first)
        XCTAssertTrue(navigation.topViewController === root)
        try assertPlainBackSource(nil, pages: pages)

        let replacement = PageViewController(themeProvider: theme)
        let detail = PlainBackPage(themeProvider: theme)
        push(detail, on: navigation)
        try assertPlainBackSource(root, pages: [root, detail])
        navigation.setViewControllers([replacement], animated: false)
        settle(navigation)
        XCTAssertTrue(navigation.topViewController === replacement)
        try assertPlainBackSource(nil, pages: pages + [detail, replacement])
    }

    func testPlainBackButtonPreservesCustomItemsAndConsumerReplacements() throws {
        let theme = TestPageTheme()
        let root = PageViewController(themeProvider: theme)
        let target = BackItemTarget()
        let action = #selector(BackItemTarget.selected)
        let original = UIBarButtonItem(title: "Custom", style: .done, target: target, action: action)
        original.tintColor = .systemPurple
        root.navigationItem.backBarButtonItem = original
        let navigation = PageNavigationController(themeProvider: theme)
        navigation.setViewControllers([root], animated: false)
        try display(navigation)

        let first = PlainBackPage(themeProvider: theme)
        push(first, on: navigation)
        XCTAssertTrue(root.navigationItem.backBarButtonItem === original)
        XCTAssertEqual(original.title, "Custom")
        XCTAssertEqual(original.style, .done)
        XCTAssertTrue(original.target === target)
        XCTAssertEqual(original.action, action)
        XCTAssertEqual(original.tintColor, .systemPurple)
        if #available(iOS 26.0, *) { XCTAssertTrue(original.hidesSharedBackground) }
        XCTAssertTrue(pop(navigation) === first)
        XCTAssertTrue(root.navigationItem.backBarButtonItem === original)
        if #available(iOS 26.0, *) {
            XCTAssertFalse(original.hidesSharedBackground)
            original.hidesSharedBackground = true
        }

        let second = PlainBackPage(themeProvider: theme)
        push(second, on: navigation)
        XCTAssertTrue(pop(navigation) === second)
        XCTAssertTrue(root.navigationItem.backBarButtonItem === original)
        if #available(iOS 26.0, *) {
            XCTAssertTrue(original.hidesSharedBackground)
            original.hidesSharedBackground = false
        }

        let third = PlainBackPage(themeProvider: theme)
        let replacement = UIBarButtonItem(title: "Replacement", style: .plain, target: target, action: action)
        navigation.willShowPage = { page, _ in
            if page === third { root.navigationItem.backBarButtonItem = replacement }
        }
        push(third, on: navigation)
        navigation.willShowPage = nil
        XCTAssertTrue(root.navigationItem.backBarButtonItem === replacement)
        if #available(iOS 26.0, *) {
            XCTAssertFalse(original.hidesSharedBackground)
            XCTAssertTrue(replacement.hidesSharedBackground)
        }
        // A later replacement must not be overwritten while the temporary
        // adaptation is being restored.
        let final = UIBarButtonItem(title: "Final", style: .done, target: target, action: action)
        root.navigationItem.backBarButtonItem = final
        XCTAssertTrue(pop(navigation) === third)
        XCTAssertTrue(root.navigationItem.backBarButtonItem === final)
        XCTAssertEqual(final.title, "Final")
        XCTAssertTrue(final.target === target)
        XCTAssertEqual(final.action, action)
        if #available(iOS 26.0, *) {
            XCTAssertFalse(replacement.hidesSharedBackground)
            XCTAssertFalse(final.hidesSharedBackground)
        }

        root.navigationItem.backBarButtonItem = nil
        let fourth = PlainBackPage(themeProvider: theme)
        push(fourth, on: navigation)
        let generated = root.navigationItem.backBarButtonItem
        if #available(iOS 26.0, *) { XCTAssertEqual(generated?.hidesSharedBackground, true) }
        else { XCTAssertNil(generated) }
        root.navigationItem.backBarButtonItem = final
        XCTAssertTrue(pop(navigation) === fourth)
        XCTAssertTrue(root.navigationItem.backBarButtonItem === final)
        if #available(iOS 26.0, *) { XCTAssertEqual(generated?.hidesSharedBackground, false) }
    }

    func testPlainBackButtonUsesTheFinalShownPageAfterCancelledTransitions() throws {
        let theme = TestPageTheme()
        let root = PageViewController(themeProvider: theme)
        let first = PlainBackPage(themeProvider: theme), second = PlainBackPage(themeProvider: theme)
        let ordinary = PageViewController(themeProvider: theme)
        let pages: [UIViewController] = [root, first, second, ordinary]
        let navigation = PageNavigationController(themeProvider: theme)
        navigation.setViewControllers([root], animated: false)
        try display(navigation)
        push(first, on: navigation)
        push(second, on: navigation)
        try assertPlainBackSource(first, pages: pages)

        // Drive only the delegate receipts of a cancelled pop; the real
        // navigation stack still identifies the page UIKit finally displayed.
        navigation.navigationController(navigation, willShow: first, animated: true)
        try assertPlainBackSource(root, pages: pages)
        navigation.navigationController(navigation, didShow: second, animated: true)
        XCTAssertTrue(navigation.topViewController === second)
        try assertPlainBackSource(first, pages: pages)
        navigation.navigationController(navigation, willShow: root, animated: true)
        try assertPlainBackSource(nil, pages: pages)
        navigation.navigationController(navigation, didShow: second, animated: true)
        try assertPlainBackSource(first, pages: pages)

        push(ordinary, on: navigation)
        try assertPlainBackSource(nil, pages: pages)
        var willCalls = 0, didCalls = 0
        navigation.willShowPage = { page, animated in
            willCalls += 1
            XCTAssertTrue(page === second)
            XCTAssertTrue(animated)
            XCTAssertEqual(page.navigationItem.backButtonDisplayMode, .minimal)
        }
        navigation.didShowPage = { page in
            didCalls += 1
            XCTAssertTrue(page === ordinary)
        }
        navigation.navigationController(navigation, willShow: second, animated: true)
        try assertPlainBackSource(first, pages: pages)
        navigation.navigationController(navigation, didShow: ordinary, animated: true)
        XCTAssertTrue(navigation.topViewController === ordinary)
        try assertPlainBackSource(nil, pages: pages)
        XCTAssertEqual(willCalls, 1)
        XCTAssertEqual(didCalls, 1)
        navigation.willShowPage = nil
        navigation.didShowPage = nil
    }

    func testPlainBackButtonLeavesSystemEdgeAndContentPopOwnershipUntouched() throws {
        let theme = TestPageTheme()
        let root = PageViewController(themeProvider: theme), ordinary = PageViewController(themeProvider: theme)
        let plain = PlainBackPage(themeProvider: theme)
        let navigation = PageNavigationController(themeProvider: theme)
        navigation.setViewControllers([root], animated: false)
        try display(navigation)
        push(ordinary, on: navigation)
        let system = try XCTUnwrap(navigation.interactivePopGestureRecognizer)
        let delegate = system.delegate
        let enabled = system.isEnabled
        let content: UIGestureRecognizer?
        if #available(iOS 26.0, *) { content = navigation.interactiveContentPopGestureRecognizer }
        else { content = nil }
        let contentDelegate = content?.delegate
        let contentEnabled = content?.isEnabled
        let recognizers = navigation.view.gestureRecognizers ?? []

        push(plain, on: navigation)
        XCTAssertTrue(navigation.navigationBar.topItem === plain.navigationItem)
        XCTAssertTrue(navigation.navigationBar.backItem === ordinary.navigationItem)
        XCTAssertTrue(navigation.delegate === navigation)
        XCTAssertNil(plain.navigationItem.leftBarButtonItems)
        XCTAssertFalse(plain.navigationItem.hidesBackButton)
        try assertPlainBackSource(ordinary, pages: [root, ordinary, plain])
        XCTAssertFalse(plain.view.gestureRecognizers?.contains { $0 is UIScreenEdgePanGestureRecognizer } ?? false)
        for popped in [false, true] {
            if popped { XCTAssertTrue(pop(navigation) === plain) }
            XCTAssertTrue(navigation.interactivePopGestureRecognizer === system)
            XCTAssertTrue(system.delegate === delegate)
            XCTAssertEqual(system.isEnabled, enabled)
            if #available(iOS 26.0, *) {
                XCTAssertTrue(navigation.interactiveContentPopGestureRecognizer === content)
            }
            XCTAssertTrue(content?.delegate === contentDelegate)
            XCTAssertEqual(content?.isEnabled, contentEnabled)
            let current = navigation.view.gestureRecognizers ?? []
            XCTAssertEqual(current.count, recognizers.count)
            XCTAssertTrue(recognizers.allSatisfy { original in current.contains { $0 === original } })
        }
        XCTAssertTrue(navigation.topViewController === ordinary)
        try assertPlainBackSource(nil, pages: [root, ordinary, plain])
    }

    func testPlainBackButtonRestoresCustomStateWhenNavigationIsReleased() {
        let theme = TestPageTheme()
        let root = PageViewController(themeProvider: theme), plain = PlainBackPage(themeProvider: theme)
        let original = UIBarButtonItem(title: "Retained", style: .plain, target: nil, action: nil)
        root.navigationItem.backBarButtonItem = original
        weak var released: PageNavigationController?
        autoreleasepool {
            let navigation = PageNavigationController(themeProvider: theme)
            navigation.loadViewIfNeeded()
            navigation.setViewControllers([root, plain], animated: false)
            navigation.navigationController(navigation, willShow: plain, animated: false)
            navigation.navigationController(navigation, didShow: plain, animated: false)
            if #available(iOS 26.0, *) { XCTAssertTrue(original.hidesSharedBackground) }
            released = navigation
        }
        XCTAssertNil(released)
        XCTAssertTrue(root.navigationItem.backBarButtonItem === original)
        if #available(iOS 26.0, *) { XCTAssertFalse(original.hidesSharedBackground) }
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
            animator.startAnimation()
            animator.pauseAnimation()
            animator.fractionComplete = 0.5
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

    func testEditorEdgeRequestsConfirmationWithoutPoppingThePage() throws {
        let theme = TestPageTheme()
        let root = PageViewController(themeProvider: theme)
        let editor = PageViewController(themeProvider: theme)
        let navigation = PageNavigationController(themeProvider: theme)
        navigation.setViewControllers([root, editor], animated: false)
        try display(navigation)
        navigation.view.layoutIfNeeded(); editor.view.layoutIfNeeded()
        let system = try XCTUnwrap(navigation.interactivePopGestureRecognizer)
        let delegate = system.delegate
        let enabled = system.isEnabled
        let content: UIGestureRecognizer?
        if #available(iOS 26.0, *) { content = navigation.interactiveContentPopGestureRecognizer }
        else { content = nil }
        let contentEnabled = content?.isEnabled
        let contentDelegate = content?.delegate
        var confirmations = 0
        let lease = UIKitEditorBackGestureLease(navigationController: navigation, hostView: editor.view) {
            confirmations += 1
        }
        XCTAssertGreaterThan(editor.view.bounds.width, 0)
        lease.activate(); lease.activate()
        XCTAssertFalse(system.isEnabled)
        if let content { XCTAssertFalse(content.isEnabled) }
        XCTAssertFalse(lease.release(translationX: editor.view.bounds.width * 0.1, velocityX: 100))
        XCTAssertEqual(confirmations, 0)
        XCTAssertTrue(lease.release(translationX: editor.view.bounds.width * 0.6, velocityX: 100))
        XCTAssertEqual(confirmations, 1)
        XCTAssertTrue(navigation.topViewController === editor)
        XCTAssertEqual(navigation.viewControllers.count, 2)
        lease.invalidate(); lease.invalidate()
        XCTAssertEqual(system.isEnabled, enabled)
        XCTAssertEqual(content?.isEnabled, contentEnabled)
        XCTAssertTrue(system.delegate === delegate)
        XCTAssertTrue(content?.delegate === contentDelegate)
        XCTAssertFalse(editor.view.gestureRecognizers?.contains { $0 is UIScreenEdgePanGestureRecognizer } ?? false)
        system.isEnabled = false
        lease.activate(); lease.invalidate()
        XCTAssertFalse(system.isEnabled)
        system.isEnabled = enabled
        XCTAssertTrue(navigation.popViewController(animated: false) === editor)
        XCTAssertTrue(navigation.topViewController === root)
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

    private func assertPlainBackSource(
        _ source: UIViewController?, pages: [UIViewController],
        file: StaticString = #filePath, line: UInt = #line
    ) throws {
        for page in pages {
            if #available(iOS 26.0, *), page === source {
                let item = try XCTUnwrap(page.navigationItem.backBarButtonItem, file: file, line: line)
                XCTAssertTrue(item.hidesSharedBackground, file: file, line: line)
                XCTAssertNil(item.target, file: file, line: line)
                XCTAssertNil(item.action, file: file, line: line)
            } else {
                XCTAssertNil(page.navigationItem.backBarButtonItem, file: file, line: line)
            }
        }
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
private final class PlainBackPage: PageViewController {
    override var prefersPlainBackButton: Bool { true }
}

@MainActor
private final class BackItemTarget: NSObject {
    @objc func selected() {}
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
