#if os(iOS)
import XCTest
import UIKit
@testable import MeowUI

@MainActor
final class RetainedPagesControllerTests: XCTestCase {
    func testFactoriesAreLazyAndAccessorNeverCreatesAPage() async {
        let theme = PageLifecycleTestTheme()
        var creations = [0, 0, 0]
        let retained = RetainedPagesController(pageFactories: (0..<3).map { index in
            {
                creations[index] += 1
                return PageLifecycleTestPage(themeProvider: theme)
            }
        }, selectedIndex: 1)
        XCTAssertEqual(creations, [0, 0, 0])
        XCTAssertNil(retained.page(at: 0))
        XCTAssertNil(retained.page(at: 1))
        XCTAssertNil(retained.page(at: -1))
        XCTAssertNil(retained.page(at: 3))
        XCTAssertFalse(retained.isViewLoaded)
        retained.loadViewIfNeeded()
        XCTAssertEqual(creations, [0, 1, 0])
        XCTAssertEqual(retained.children.count, 1)
        XCTAssertTrue(retained.page(at: 1)?.parent === retained)
        retained.select(-1, animated: false)
        retained.select(3, animated: false)
        XCTAssertEqual(retained.selectedIndex, 1)
        XCTAssertEqual(creations, [0, 1, 0])
    }

    func testSelectingBeforeViewLoadDoesNotCreateTheOriginalIndex() async {
        let theme = PageLifecycleTestTheme()
        var creations = [0, 0]
        let retained = RetainedPagesController(pageFactories: (0..<2).map { index in
            {
                creations[index] += 1
                return PageLifecycleTestPage(themeProvider: theme)
            }
        })
        var selections: [Int] = []
        retained.didSelect = { selections.append($0) }
        retained.select(1, animated: false)
        XCTAssertEqual(retained.selectedIndex, 1)
        XCTAssertEqual(creations, [0, 1])
        XCTAssertEqual(selections, [1])
        XCTAssertNil(retained.page(at: 0))
    }

    func testWillSelectStartsWithTheHorizontalTransitionAndPrecedesDidSelect() async throws {
        let theme = PageLifecycleTestTheme()
        let retained = RetainedPagesController(pageFactories: [
            { PageLifecycleTestPage(themeProvider: theme) },
            { PageLifecycleTestPage(themeProvider: theme) },
            { PageLifecycleTestPage(themeProvider: theme) }
        ])
        var events: [String] = []
        var horizontalTransitionWill: (index: Int, duration: TimeInterval)?
        retained.willSelect = { index, duration in
            events.append("will \(index)")
            horizontalTransitionWill = (index, duration)
        }
        retained.didSelect = { events.append("did \($0)") }
        retained.select(1, animated: true)
        XCTAssertEqual(horizontalTransitionWill?.index, 1)
        XCTAssertEqual(horizontalTransitionWill?.duration, 0)
        XCTAssertEqual(events, ["will 1", "did 1"])

        let scene = try XCTUnwrap(UIApplication.shared.connectedScenes.compactMap { $0 as? UIWindowScene }.first)
        let previousKeyWindow = scene.windows.first { $0.isKeyWindow }
        let window = UIWindow(windowScene: scene)
        window.rootViewController = retained
        window.makeKeyAndVisible()
        defer {
            window.isHidden = true
            window.rootViewController = nil
            previousKeyWindow?.makeKey()
        }
        let displayed = expectation(for: NSPredicate { _, _ in
            MainActor.assumeIsolated {
                (retained.page(at: 1) as? PageLifecycleTestPage)?.appearanceEvents.last == "didAppear"
            }
        }, evaluatedWith: retained)
        await fulfillment(of: [displayed], timeout: 2)

        events.removeAll()
        let selected = expectation(description: "horizontal transition finished")
        retained.didSelect = {
            events.append("did \($0)")
            if $0 == 2 { selected.fulfill() }
        }
        retained.select(2, animated: true)
        XCTAssertEqual(horizontalTransitionWill?.index, 2)
        XCTAssertGreaterThan(horizontalTransitionWill?.duration ?? 0, 0)
        XCTAssertEqual(events, ["will 2"])
        await fulfillment(of: [selected], timeout: 2)
        XCTAssertEqual(events, ["will 2", "did 2"])
    }

    func testForwardHorizontalTransitionMovesPagesLeftWithoutFadingAndRestoresTransforms() async throws {
        try await assertHorizontalTransition(from: 0, to: 2)
    }

    func testBackwardHorizontalTransitionMovesPagesRightWithoutFadingAndRestoresTransforms() async throws {
        try await assertHorizontalTransition(from: 2, to: 0)
    }

    func testSwitchingKeepsContainmentStateAndBalancedAppearance() async throws {
        let theme = PageLifecycleTestTheme()
        let retained = RetainedPagesController(pageFactories: [
            { PageLifecycleTestPage(themeProvider: theme) },
            { PageLifecycleTestPage(themeProvider: theme) }
        ])
        retained.beginAppearanceTransition(true, animated: false)
        retained.endAppearanceTransition()
        NotificationCenter.default.post(name: UIApplication.didBecomeActiveNotification, object: nil)
        let first = try XCTUnwrap(retained.page(at: 0) as? PageLifecycleTestPage)
        first.view.tag = 42
        XCTAssertEqual(first.appearanceEvents, ["willAppear", "didAppear"])
        retained.select(1, animated: false)
        let second = try XCTUnwrap(retained.page(at: 1) as? PageLifecycleTestPage)
        NotificationCenter.default.post(name: UIApplication.didBecomeActiveNotification, object: nil)
        XCTAssertTrue(first.parent === retained)
        XCTAssertTrue(second.parent === retained)
        XCTAssertEqual(retained.children.count, 2)
        XCTAssertTrue(first.isPageActive)
        XCTAssertFalse(first.isPageVisible)
        XCTAssertTrue(first.view.isHidden)
        XCTAssertTrue(second.isPageActive)
        XCTAssertTrue(second.isPageVisible)
        XCTAssertEqual(first.inactiveCount, 0)
        XCTAssertEqual(first.appearanceEvents, ["willAppear", "didAppear", "willDisappear", "didDisappear"])
        XCTAssertEqual(second.appearanceEvents, ["willAppear", "didAppear"])
        retained.select(0, animated: false)
        XCTAssertTrue(retained.page(at: 0) === first)
        XCTAssertEqual(first.view.tag, 42)
        XCTAssertEqual(first.activeCount, 1)
        XCTAssertEqual(second.activeCount, 1)
        XCTAssertFalse(second.isPageVisible)
        XCTAssertEqual(second.inactiveCount, 0)

        retained.beginAppearanceTransition(false, animated: false)
        retained.endAppearanceTransition()
        let hiddenSecondEvents = second.appearanceEvents
        retained.beginAppearanceTransition(true, animated: false)
        retained.endAppearanceTransition()
        XCTAssertEqual(second.appearanceEvents, hiddenSecondEvents)
        XCTAssertEqual(first.activeCount, 1)
        XCTAssertEqual(first.inactiveCount, 0)
        XCTAssertEqual(first.visibleCount, first.hiddenCount + 1)
    }

    func testHiddenSelectionDoesNotInventAppearanceAndOnlySelectedPageAppears() async throws {
        let theme = PageLifecycleTestTheme()
        let retained = RetainedPagesController(pageFactories: [
            { PageLifecycleTestPage(themeProvider: theme) },
            { PageLifecycleTestPage(themeProvider: theme) }
        ])
        retained.loadViewIfNeeded()
        retained.select(1, animated: false)
        let first = try XCTUnwrap(retained.page(at: 0) as? PageLifecycleTestPage)
        let second = try XCTUnwrap(retained.page(at: 1) as? PageLifecycleTestPage)
        XCTAssertTrue(first.appearanceEvents.isEmpty)
        XCTAssertTrue(second.appearanceEvents.isEmpty)
        XCTAssertFalse(first.isPageActive)
        XCTAssertFalse(second.isPageActive)
        retained.beginAppearanceTransition(true, animated: false)
        retained.endAppearanceTransition()
        XCTAssertTrue(first.appearanceEvents.isEmpty)
        XCTAssertEqual(second.appearanceEvents, ["willAppear", "didAppear"])
        XCTAssertTrue(second.isPageActive)
    }

    func testSelectionsDuringContainerAppearanceAreAppliedAfterThePairEnds() async throws {
        let theme = PageLifecycleTestTheme()
        let retained = RetainedPagesController(pageFactories: [
            { PageLifecycleTestPage(themeProvider: theme) },
            { PageLifecycleTestPage(themeProvider: theme) },
            { PageLifecycleTestPage(themeProvider: theme) }
        ])
        var selections: [Int] = []
        retained.didSelect = { selections.append($0) }
        retained.beginAppearanceTransition(true, animated: false)
        retained.select(1, animated: false)
        retained.select(2, animated: false)
        XCTAssertNil(retained.page(at: 1))
        XCTAssertNil(retained.page(at: 2))
        retained.endAppearanceTransition()
        let first = try XCTUnwrap(retained.page(at: 0) as? PageLifecycleTestPage)
        let third = try XCTUnwrap(retained.page(at: 2) as? PageLifecycleTestPage)
        XCTAssertEqual(retained.selectedIndex, 2)
        XCTAssertEqual(selections, [2])
        XCTAssertEqual(first.appearanceEvents, ["willAppear", "didAppear", "willDisappear", "didDisappear"])
        XCTAssertEqual(third.appearanceEvents, ["willAppear", "didAppear"])
        XCTAssertNil(retained.page(at: 1))

        retained.beginAppearanceTransition(false, animated: false)
        retained.select(0, animated: false)
        retained.endAppearanceTransition()
        XCTAssertEqual(retained.selectedIndex, 0)
        XCTAssertEqual(first.appearanceEvents, ["willAppear", "didAppear", "willDisappear", "didDisappear"])
        retained.beginAppearanceTransition(true, animated: false)
        retained.endAppearanceTransition()
        XCTAssertEqual(Array(first.appearanceEvents.suffix(2)), ["willAppear", "didAppear"])
        XCTAssertEqual(first.activeCount, 1)
    }

    func testContainerDisappearanceDuringHorizontalTransitionKeepsAppearancePairsBalanced() async throws {
        let theme = PageLifecycleTestTheme()
        let retained = RetainedPagesController(pageFactories: [
            { PageLifecycleTestPage(themeProvider: theme) },
            { PageLifecycleTestPage(themeProvider: theme) }
        ])
        let scene = try XCTUnwrap(UIApplication.shared.connectedScenes.compactMap { $0 as? UIWindowScene }.first)
        let previousKeyWindow = scene.windows.first { $0.isKeyWindow }
        let window = UIWindow(windowScene: scene)
        window.rootViewController = retained
        window.makeKeyAndVisible()
        defer {
            window.isHidden = true
            window.rootViewController = nil
            previousKeyWindow?.makeKey()
        }
        let displayed = expectation(for: NSPredicate { _, _ in
            MainActor.assumeIsolated {
                (retained.page(at: 0) as? PageLifecycleTestPage)?.appearanceEvents.last == "didAppear"
            }
        }, evaluatedWith: retained)
        await fulfillment(of: [displayed], timeout: 2)
        let selected = expectation(description: "horizontal transition finished")
        retained.didSelect = { if $0 == 1 { selected.fulfill() } }
        retained.select(1, animated: true)
        retained.beginAppearanceTransition(false, animated: true)
        retained.endAppearanceTransition()
        await fulfillment(of: [selected], timeout: 2)
        let first = try XCTUnwrap(retained.page(at: 0) as? PageLifecycleTestPage)
        let second = try XCTUnwrap(retained.page(at: 1) as? PageLifecycleTestPage)
        XCTAssertEqual(first.appearanceEvents, ["willAppear", "didAppear", "willDisappear", "didDisappear"])
        XCTAssertEqual(second.appearanceEvents, ["willAppear", "didAppear", "willDisappear", "didDisappear"])
        retained.beginAppearanceTransition(true, animated: false)
        retained.endAppearanceTransition()
        XCTAssertEqual(Array(second.appearanceEvents.suffix(2)), ["willAppear", "didAppear"])
        XCTAssertEqual(first.activeCount, 1)
        XCTAssertEqual(second.activeCount, 1)
        XCTAssertEqual(second.inactiveCount, 0)
    }

    func testRemovingContainerClosesVisibleAndHiddenPages() async throws {
        let owner = UIViewController()
        let theme = PageLifecycleTestTheme()
        let retained = RetainedPagesController(pageFactories: [
            { PageLifecycleTestPage(themeProvider: theme) },
            { PageLifecycleTestPage(themeProvider: theme) }
        ])
        owner.addChild(retained)
        owner.view.addSubview(retained.view)
        retained.didMove(toParent: owner)
        retained.beginAppearanceTransition(true, animated: false)
        retained.endAppearanceTransition()
        retained.select(1, animated: false)
        let first = try XCTUnwrap(retained.page(at: 0) as? PageLifecycleTestPage)
        let second = try XCTUnwrap(retained.page(at: 1) as? PageLifecycleTestPage)
        retained.willMove(toParent: nil)
        retained.view.removeFromSuperview()
        retained.removeFromParent()
        XCTAssertFalse(first.isPageActive)
        XCTAssertFalse(second.isPageActive)
        XCTAssertEqual(first.inactiveCount, 1)
        XCTAssertEqual(second.inactiveCount, 1)
    }

    func testDestroyingContainerCancelsResourcesOfExternallyRetainedPage() async throws {
        let theme = PageLifecycleTestTheme()
        var retained: RetainedPagesController? = RetainedPagesController(pageFactories: [
            { PageLifecycleTestPage(themeProvider: theme) }
        ])
        retained?.beginAppearanceTransition(true, animated: false)
        retained?.endAppearanceTransition()
        let page = try XCTUnwrap(retained?.page(at: 0) as? PageLifecycleTestPage)
        let started = expectation(description: "container task started")
        let cancelled = expectation(description: "container task cancelled")
        var continuation: CheckedContinuation<Void, Never>?
        page.runPageTask {
            await withCheckedContinuation { continuation = $0; started.fulfill() }
            XCTAssertTrue(Task.isCancelled)
            cancelled.fulfill()
        }
        await fulfillment(of: [started], timeout: 2)
        retained = nil
        XCTAssertFalse(page.isPageActive)
        XCTAssertEqual(page.inactiveCount, 1)
        continuation?.resume()
        await fulfillment(of: [cancelled], timeout: 2)
    }

    private func assertHorizontalTransition(from previousIndex: Int, to nextIndex: Int) async throws {
        if UIApplication.shared.applicationState != .active {
            let runnerIdentifier = try XCTUnwrap(Bundle.main.bundleIdentifier)
            // Presentation layers update only while the runner is in the foreground.
            XCUIApplication(bundleIdentifier: runnerIdentifier).activate()
        }
        XCTAssertEqual(UIApplication.shared.applicationState, .active)
        let theme = PageLifecycleTestTheme()
        let retained = RetainedPagesController(pageFactories: (0..<3).map { _ in
            { PageLifecycleTestPage(themeProvider: theme) }
        }, selectedIndex: previousIndex)
        let scene = try XCTUnwrap(UIApplication.shared.connectedScenes.compactMap { $0 as? UIWindowScene }.first)
        XCTAssertEqual(scene.activationState, .foregroundActive)
        let previousKeyWindow = scene.windows.first { $0.isKeyWindow }
        let window = UIWindow(windowScene: scene)
        window.rootViewController = retained
        window.makeKeyAndVisible()
        defer {
            window.isHidden = true
            window.rootViewController = nil
            previousKeyWindow?.makeKey()
        }
        let displayed = expectation(for: NSPredicate { _, _ in
            MainActor.assumeIsolated {
                (retained.page(at: previousIndex) as? PageLifecycleTestPage)?.appearanceEvents.last == "didAppear"
            }
        }, evaluatedWith: retained)
        await fulfillment(of: [displayed], timeout: 2)
        window.layoutIfNeeded()
        let previous = try XCTUnwrap(retained.page(at: previousIndex))
        let width = retained.view.bounds.width
        XCTAssertGreaterThan(width, 0)
        XCTAssertTrue(retained.view.clipsToBounds)
        let previousPage = try XCTUnwrap(previous as? PageViewController)
        XCTAssertTrue(previousPage.hasOwnPageHeader)
        let safeArea = previous.view.safeAreaInsets
        let headerRow = previousPage.pageHeader.contentFrame
        XCTAssertGreaterThan(safeArea.top, 0)
        XCTAssertGreaterThan(headerRow.width, 0)
        var events: [String] = []
        retained.willSelect = { index, duration in
            events.append("will \(index)")
            XCTAssertEqual(index, nextIndex)
            XCTAssertEqual(duration, 0.22)
        }
        let selected = expectation(description: "horizontal transition finished")
        retained.didSelect = { index in
            events.append("did \(index)")
            XCTAssertEqual(index, nextIndex)
            XCTAssertTrue(previous.view.isHidden)
            XCTAssertEqual(previous.view.transform, .identity)
            selected.fulfill()
        }
        retained.select(nextIndex, animated: true)
        let next = try XCTUnwrap(retained.page(at: nextIndex))
        XCTAssertEqual(events, ["will \(nextIndex)"])
        XCTAssertFalse(previous.view.isHidden)
        XCTAssertFalse(next.view.isHidden)
        XCTAssertEqual(previous.view.alpha, 1)
        XCTAssertEqual(next.view.alpha, 1)
        // Each page's own header moves with it: the transition must not lay out
        // either header or page safe area again.
        window.layoutIfNeeded()
        for page in [previous, next] {
            let page = try XCTUnwrap(page as? PageViewController)
            XCTAssertEqual(page.view.safeAreaInsets, safeArea)
            XCTAssertEqual(page.pageHeader.contentFrame, headerRow)
        }
        let observer = HorizontalTransitionObserver(previous: previous.view, next: next.view)
        let displayLink = CADisplayLink(target: observer, selector: #selector(HorizontalTransitionObserver.sample(_:)))
        displayLink.add(to: .main, forMode: .common)
        defer { displayLink.invalidate() }
        await fulfillment(of: [selected], timeout: 2)
        displayLink.invalidate()

        XCTAssertFalse(observer.samples.isEmpty, "Observe rendered frames during the horizontal transition")
        for sample in observer.samples {
            XCTAssertEqual(sample.previousOpacity, 1)
            XCTAssertEqual(sample.nextOpacity, 1)
            XCTAssertEqual(sample.previous.ty, 0, accuracy: 0.01)
            XCTAssertEqual(sample.next.ty, 0, accuracy: 0.01)
        }
        let movingSamples = observer.samples.filter {
            abs($0.previous.tx) > 0.1 && abs($0.previous.tx) < width - 0.1 &&
                abs($0.next.tx) > 0.1 && abs($0.next.tx) < width - 0.1
        }
        XCTAssertFalse(movingSamples.isEmpty, "Both pages must visibly move between their starting and ending positions")
        for sample in movingSamples {
            if nextIndex > previousIndex {
                XCTAssertLessThan(sample.previous.tx, 0)
                XCTAssertGreaterThan(sample.next.tx, 0)
            } else {
                XCTAssertGreaterThan(sample.previous.tx, 0)
                XCTAssertLessThan(sample.next.tx, 0)
            }
            XCTAssertEqual(abs(sample.previous.tx) + abs(sample.next.tx), width, accuracy: 1)
        }
        XCTAssertEqual(events, ["will \(nextIndex)", "did \(nextIndex)"])
        XCTAssertEqual(retained.selectedIndex, nextIndex)
        XCTAssertTrue(previous.view.isHidden)
        XCTAssertFalse(next.view.isHidden)
        XCTAssertEqual(previous.view.transform, .identity)
        XCTAssertEqual(next.view.transform, .identity)
        XCTAssertEqual(previous.view.alpha, 1)
        XCTAssertEqual(next.view.alpha, 1)
        XCTAssertNil(retained.page(at: 1))
    }
}

@MainActor
private final class HorizontalTransitionObserver: NSObject {
    private let previous: UIView
    private let next: UIView
    private(set) var samples: [(previous: CGAffineTransform, next: CGAffineTransform,
                               previousOpacity: Float, nextOpacity: Float)] = []

    init(previous: UIView, next: UIView) {
        self.previous = previous
        self.next = next
        super.init()
    }

    @objc func sample(_ displayLink: CADisplayLink) {
        guard let previous = previous.layer.presentation(), let next = next.layer.presentation() else { return }
        samples.append((previous.affineTransform(), next.affineTransform(), previous.opacity, next.opacity))
    }
}
#endif
