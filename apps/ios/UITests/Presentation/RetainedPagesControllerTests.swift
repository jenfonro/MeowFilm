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

    func testWillSelectStartsWithTheCrossfadeAndPrecedesDidSelect() async throws {
        let theme = PageLifecycleTestTheme()
        let retained = RetainedPagesController(pageFactories: [
            { PageLifecycleTestPage(themeProvider: theme) },
            { PageLifecycleTestPage(themeProvider: theme) },
            { PageLifecycleTestPage(themeProvider: theme) }
        ])
        var events: [String] = []
        var crossfadeWill: (index: Int, duration: TimeInterval)?
        retained.willSelect = { index, duration in
            events.append("will \(index)")
            crossfadeWill = (index, duration)
        }
        retained.didSelect = { events.append("did \($0)") }
        retained.select(1, animated: true)
        XCTAssertEqual(crossfadeWill?.index, 1)
        XCTAssertEqual(crossfadeWill?.duration, 0)
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
        let selected = expectation(description: "crossfade finished")
        retained.didSelect = {
            events.append("did \($0)")
            if $0 == 2 { selected.fulfill() }
        }
        retained.select(2, animated: true)
        XCTAssertEqual(crossfadeWill?.index, 2)
        XCTAssertGreaterThan(crossfadeWill?.duration ?? 0, 0)
        XCTAssertEqual(events, ["will 2"])
        await fulfillment(of: [selected], timeout: 2)
        XCTAssertEqual(events, ["will 2", "did 2"])
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

    func testContainerDisappearanceDuringCrossfadeKeepsAppearancePairsBalanced() async throws {
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
        let selected = expectation(description: "crossfade finished")
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
}
#endif
