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
        let retained = RetainedPagesController(pages: [first, second])
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
        let cancelled = expectation(description: "physical page task observed cancellation")
        var continuation: CheckedContinuation<Void, Never>?
        firstPage.runPageTask {
            await withCheckedContinuation { continuation = $0; started.fulfill() }
            XCTAssertTrue(Task.isCancelled)
            cancelled.fulfill()
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
        XCTAssertFalse(firstPage.isPageActive)
        XCTAssertTrue(secondPage.isPageActive)
        XCTAssertEqual(firstPage.inactiveCount, 1)
        XCTAssertTrue(retained.children.first === second)
        XCTAssertNil(first.parent)
        continuation?.resume()
        await fulfillment(of: [cancelled], timeout: 3)

        let detail = PageViewController(themeProvider: theme)
        navigation.pushViewController(detail, animated: false)
        XCTAssertTrue(navigation.topViewController === detail)
        XCTAssertTrue(navigation.interactivePopGestureRecognizer === system)
        XCTAssertTrue(system.delegate === delegate)
        XCTAssertTrue(content?.delegate === contentDelegate)
        XCTAssertEqual(navigation.view.gestureRecognizers?.filter { $0 is UIScreenEdgePanGestureRecognizer }.count, edges)
        XCTAssertFalse(detail.view.gestureRecognizers?.contains { $0 is UIScreenEdgePanGestureRecognizer } ?? false)
        XCTAssertEqual(navigation.viewControllers.count, 2)

        XCTAssertTrue(navigation.popViewController(animated: false) === detail)
        XCTAssertTrue(navigation.topViewController === retained)
        XCTAssertEqual(navigation.viewControllers.count, 1)
        XCTAssertTrue(secondPage.isPageActive)
        XCTAssertEqual(retained.selectedIndex, 1)
        XCTAssertTrue(retained.children.first === second)
        NotificationCenter.default.post(name: UIApplication.willResignActiveNotification, object: nil)
        XCTAssertFalse(secondPage.isPageActive)
        NotificationCenter.default.post(name: UIApplication.didBecomeActiveNotification, object: nil)
        XCTAssertTrue(secondPage.isPageActive)
        XCTAssertFalse(firstPage.isPageActive)
    }

    func testNativeSurfaceTransitionCancelsAndCommitsWithoutReplacingControls() async throws {
        let theme = TestPageTheme()
        let root = PageViewController(themeProvider: theme)
        let window = try display(root)
        root.view.layoutIfNeeded()
        let dock = UIView(frame: CGRect(x: 18, y: window.bounds.height - 180,
                                       width: window.bounds.width - 36, height: 100))
        root.view.addSubview(dock)
        let surface = WindowGradientView(themeProvider: theme)
        let play = UIButton(type: .system), queue = UIButton(type: .system)
        surface.addSubview(play); surface.addSubview(queue)
        dock.addSubview(surface); surface.frame = dock.bounds
        surface.layer.cornerRadius = 24
        surface.layoutIfNeeded()
        let full = PageViewController(themeProvider: theme)
        full.loadViewIfNeeded()
        full.view = UIView()
        let transition = NativeSurfaceTransition(surface: surface, dock: dock,
            presentLayout: {
                play.frame = CGRect(x: 60, y: surface.bounds.height - 80, width: 44, height: 44)
                queue.frame = CGRect(x: surface.bounds.width - 104, y: surface.bounds.height - 80,
                                     width: 44, height: 44)
            }, dockLayout: {
                play.frame = CGRect(x: surface.bounds.width - 96, y: 28, width: 44, height: 44)
                queue.frame = CGRect(x: surface.bounds.width - 48, y: 28, width: 44, height: 44)
            })
        let reference = WindowGradientView(themeProvider: theme)
        window.addSubview(reference)
        reference.frame = window.bounds
        reference.layoutIfNeeded()

        for (presenting, cancelled) in [(true, true), (true, false), (false, true), (false, false)] {
            if presenting {
                _ = transition.animationController(forPresented: full, presenting: root, source: root)
            } else { _ = transition.animationController(forDismissed: full) }
            let context = SurfaceContext(container: root.view, from: presenting ? root : full,
                                         to: presenting ? full : root, frame: root.view.bounds)
            let complete = expectation(description: "native transition completion")
            context.completed = { complete.fulfill() }
            let animator = try XCTUnwrap(transition.interruptibleAnimator(using: context) as? UIViewPropertyAnimator)
            XCTAssertTrue(transition.interruptibleAnimator(using: context) === animator)
            XCTAssertTrue(transition.isTransitioning)
            XCTAssertTrue(surface.subviews.contains { $0 === play })
            XCTAssertTrue(surface.subviews.contains { $0 === queue })
            XCTAssertEqual(surface.subviews.filter { $0 is UIButton }.count, 2)
            animator.startAnimation()
            animator.pauseAnimation()
            animator.fractionComplete = 0.5
            context.transitionWasCancelled = cancelled
            animator.stopAnimation(false)
            animator.finishAnimation(at: cancelled ? .start : .end)
            await fulfillment(of: [complete], timeout: 3)
            XCTAssertEqual(context.result, !cancelled)
            XCTAssertFalse(transition.isTransitioning)
            if presenting == cancelled {
                XCTAssertTrue(surface.superview === dock)
                XCTAssertEqual(surface.frame, dock.bounds)
            } else {
                XCTAssertTrue(surface.superview === full.view)
                XCTAssertEqual(surface.frame, full.view.bounds)
            }
            XCTAssertTrue(play.superview === surface)
            XCTAssertTrue(queue.superview === surface)
            try assertCanvas(surface, reference: reference, window: window)
        }

        full.view.addSubview(surface)
        surface.frame = full.view.bounds
        transition.restoreDock()
        transition.restoreDock()
        XCTAssertTrue(surface.superview === dock)
        XCTAssertEqual(surface.frame, dock.bounds)
        XCTAssertEqual(surface.layer.cornerRadius, 24)
        XCTAssertEqual(dock.subviews.filter { $0 === surface }.count, 1)
        XCTAssertTrue(play.superview === surface)
        XCTAssertTrue(queue.superview === surface)
        try assertCanvas(surface, reference: reference, window: window)
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

    private func assertCanvas(_ surface: WindowGradientView, reference: WindowGradientView, window: UIWindow) throws {
        surface.setNeedsLayout(); surface.layoutIfNeeded()
        XCTAssertTrue(surface.isOpaque)
        XCTAssertEqual(surface.backgroundColor?.cgColor.alpha, 1)
        XCTAssertTrue(surface.window === window)
        let projected = surface.convert(window.bounds, from: window)
        XCTAssertEqual(surface.canvasFrame, CGRect(x: 0, y: projected.minY,
                                                   width: surface.bounds.width, height: projected.height))
        let point = CGPoint(x: surface.bounds.midX, y: surface.bounds.midY)
        let actual = try pixel(surface, at: point)
        let expected = try pixel(reference, at: reference.convert(point, from: surface))
        XCTAssertEqual(actual[3], 255)
        for index in 0..<3 {
            XCTAssertLessThanOrEqual(abs(Int(actual[index]) - Int(expected[index])), 2,
                                     "component \(index): surface \(actual) reference \(expected)")
        }
    }

    private func pixel(_ view: UIView, at point: CGPoint) throws -> [UInt8] {
        let width = max(1, Int(view.bounds.width.rounded(.up))), height = max(1, Int(view.bounds.height.rounded(.up)))
        var bytes = [UInt8](repeating: 0, count: width * height * 4)
        let context = try XCTUnwrap(CGContext(data: &bytes, width: width, height: height,
            bitsPerComponent: 8, bytesPerRow: width * 4, space: CGColorSpaceCreateDeviceRGB(),
            bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue))
        context.translateBy(x: 0, y: CGFloat(height))
        context.scaleBy(x: 1, y: -1)
        view.layer.render(in: context)
        let x = min(width - 1, max(0, Int(point.x))), y = min(height - 1, max(0, Int(point.y)))
        let offset = (y * width + x) * 4
        return Array(bytes[offset..<offset + 4])
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
