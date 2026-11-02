#if os(iOS)
import XCTest
import UIKit
@testable import MeowUI

@MainActor
final class FixedSheetTransitionTests: XCTestCase {
    func testTopSheetPresentsAtTopWithExpectedGeometry() async throws {
        let (root, page, sheet, window) = await mount(edge: .top, dragHandle: false)
        defer { release(root, window: window) }
        let frame = page.view.frame
        XCTAssertEqual(frame.minY, window.bounds.minY, accuracy: 0.5)
        XCTAssertEqual(frame.height, 520, accuracy: 0.5)
        let presentation = try XCTUnwrap(page.presentationController)
        XCTAssertEqual(frame, presentation.frameOfPresentedViewInContainerView)
        XCTAssertFalse(presentation.shouldRemovePresentersView)
        XCTAssertFalse(sheet.isTransitioning)
    }

    func testOffscreenTranslationFollowsSheetEdge() {
        let frame = CGRect(x: 0, y: 120, width: 390, height: 520)
        XCTAssertEqual(FixedSheetTransition.offscreenTranslation(frame: frame, edge: .top),
                       CGSize(width: 0, height: -544))
        XCTAssertEqual(FixedSheetTransition.offscreenTranslation(frame: frame, edge: .bottom),
                       CGSize(width: 0, height: 544))
    }

    func testTopSheetCancelledInteractiveDismissalRestoresFrameAndPresenter() async throws {
        let (root, page, sheet, window) = await mount(edge: .top, dragHandle: false)
        defer { release(root, window: window) }
        let frame = page.view.frame, underlay = root.view!, marker = underlay.subviews[0]
        let markerFrame = marker.frame
        sheet.handleDrag(.began, translation: 0, velocity: -100)
        sheet.handleDrag(.changed, translation: -40, velocity: -100)
        NotificationCenter.default.post(name: UIApplication.willResignActiveNotification, object: nil)
        await wait("top interactive cancellation settled") { !sheet.isTransitioning }
        NotificationCenter.default.post(name: UIApplication.didBecomeActiveNotification, object: nil)
        XCTAssertTrue(root.presentedViewController === page)
        XCTAssertEqual(page.view.frame, frame)
        XCTAssertEqual(page.view.transform, .identity)
        XCTAssertTrue(page.isPageActive)
        XCTAssertTrue(root.view === underlay)
        XCTAssertEqual(underlay.transform, .identity)
        XCTAssertEqual(marker.frame, markerFrame)
        XCTAssertEqual(page.inactiveCount, 0)
    }

    func testTopSheetCompletedInteractiveDismissalClosesSheet() async throws {
        let (root, page, sheet, window) = await mount(edge: .top, dragHandle: false)
        defer { release(root, window: window) }
        sheet.handleDrag(.began, translation: 0, velocity: -500)
        sheet.handleDrag(.changed, translation: -160, velocity: -500)
        sheet.handleDrag(.ended, translation: -160, velocity: -500)
        await wait("top sheet dismissed") {
            root.presentedViewController == nil && !sheet.isTransitioning
        }
        XCTAssertFalse(page.isPageActive)
        XCTAssertEqual(page.inactiveCount, 1)
    }

    func testCancelledInteractiveSheetRestoresFrameAndKeepsOriginalPresenter() async throws {
        let (root, page, sheet, window) = await mount()
        defer { release(root, window: window) }
        let frame = page.view.frame, underlay = root.view!, marker = underlay.subviews[0]
        let markerFrame = marker.frame
        XCTAssertEqual(frame.height, 520, accuracy: 0.5)
        XCTAssertEqual(frame.maxY, window.bounds.maxY, accuracy: 0.5)
        let presentation = try XCTUnwrap(page.presentationController)
        XCTAssertEqual(frame, presentation.frameOfPresentedViewInContainerView)
        XCTAssertFalse(presentation.shouldRemovePresentersView)
        sheet.handleDrag(.began, translation: 0, velocity: 100)
        sheet.handleDrag(.changed, translation: 40, velocity: 100)
        NotificationCenter.default.post(name: UIApplication.willResignActiveNotification, object: nil)
        await wait("interactive cancellation settled") { !sheet.isTransitioning }
        NotificationCenter.default.post(name: UIApplication.didBecomeActiveNotification, object: nil)
        XCTAssertTrue(root.presentedViewController === page)
        XCTAssertEqual(page.view.frame, frame)
        XCTAssertEqual(page.view.transform, .identity)
        XCTAssertTrue(page.isPageActive)
        XCTAssertTrue(root.view === underlay)
        XCTAssertEqual(underlay.transform, .identity)
        XCTAssertEqual(marker.frame, markerFrame)
        XCTAssertEqual(page.inactiveCount, 0)

        sheet.handleDrag(.began, translation: 0, velocity: 100)
        sheet.handleDrag(.changed, translation: 35, velocity: 100)
        sheet.handleDrag(.ended, translation: 35, velocity: 0)
        await wait("short drag returned") { !sheet.isTransitioning }
        XCTAssertEqual(page.view.frame, frame)
        XCTAssertTrue(page.isPageActive)
    }

    func testCompletedInteractiveDismissalReleasesOnlySheetLifetime() async throws {
        let (root, page, sheet, window) = await mount()
        defer { release(root, window: window) }
        let underlay = root.view!
        sheet.handleDrag(.began, translation: 0, velocity: 500)
        sheet.handleDrag(.changed, translation: 160, velocity: 500)
        sheet.handleDrag(.ended, translation: 160, velocity: 500)
        await wait("sheet dismissed") { root.presentedViewController == nil && !sheet.isTransitioning }
        XCTAssertFalse(page.isPageActive)
        XCTAssertEqual(page.inactiveCount, 1)
        XCTAssertTrue(root.view === underlay)
        XCTAssertTrue(underlay.window === window)
        XCTAssertEqual(underlay.transform, .identity)
    }

    private func mount(edge: FixedSheetTransition.Edge = .bottom,
                       dragHandle: Bool = true)
        async -> (UIViewController, FixedSheetTestPage, FixedSheetTransition, UIWindow) {
        let root = UIViewController()
        root.view.backgroundColor = .white
        root.view.addSubview(UIView(frame: CGRect(x: 12, y: 96, width: 140, height: 80)))
        let window = UIWindow(frame: CGRect(x: 0, y: 0, width: 390, height: 844))
        window.rootViewController = root; window.makeKeyAndVisible()
        let page = FixedSheetTestPage(themeProvider: TestPageTheme())
        page.view.backgroundColor = .white
        let handle = UIView(frame: CGRect(x: 0, y: 0, width: 390, height: 24))
        page.view.addSubview(handle)
        let sheet = FixedSheetTransition(edge: edge) { .init(width: $0.width, height: 520) }
        page.modalPresentationStyle = .custom; page.transitioningDelegate = sheet
        if dragHandle { sheet.install(on: page, dragHandle: handle) }
        else { sheet.install(on: page) }
        root.present(page, animated: false)
        NotificationCenter.default.post(name: UIApplication.didBecomeActiveNotification, object: nil)
        await wait("fixed sheet appeared") { page.isPageVisible && !page.isBeingPresented && !sheet.isTransitioning }
        return (root, page, sheet, window)
    }

    private func release(_ root: UIViewController, window: UIWindow) {
        if let page = root.presentedViewController { PageViewController.invalidatePages(in: page) }
        root.dismiss(animated: false)
        window.isHidden = true; window.rootViewController = nil
    }

    private func wait(_ description: String, until predicate: @escaping @MainActor () -> Bool) async {
        let ready = expectation(for: NSPredicate { _, _ in MainActor.assumeIsolated { predicate() } },
                                evaluatedWith: self)
        ready.expectationDescription = description
        await fulfillment(of: [ready], timeout: 3)
    }
}

@MainActor
private final class FixedSheetTestPage: PageViewController {
    private(set) var inactiveCount = 0
    override func pageDidBecomeInactive() { inactiveCount += 1 }
}
#endif
