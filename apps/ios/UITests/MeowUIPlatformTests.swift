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
