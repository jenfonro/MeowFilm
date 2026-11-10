#if os(iOS)
import XCTest
import UIKit
import Combine
@testable import MeowUI

@MainActor
final class PageViewControllerTests: XCTestCase {
    func testCoverageAndForegroundOnlyChangeVisibility() async {
        let page = PageLifecycleTestPage(themeProvider: PageLifecycleTestTheme())
        page.beginAppearanceTransition(true, animated: false)
        page.endAppearanceTransition()
        NotificationCenter.default.post(name: UIApplication.didBecomeActiveNotification, object: nil)
        XCTAssertTrue(page.isPageActive)
        XCTAssertTrue(page.isPageVisible)
        XCTAssertEqual(page.activeCount, 1)
        XCTAssertEqual(page.visibleCount, 1)

        page.beginAppearanceTransition(false, animated: false)
        page.endAppearanceTransition()
        NotificationCenter.default.post(name: UIApplication.willResignActiveNotification, object: nil)
        NotificationCenter.default.post(name: UIApplication.didBecomeActiveNotification, object: nil)
        XCTAssertTrue(page.isPageActive)
        XCTAssertFalse(page.isPageVisible)
        XCTAssertEqual(page.inactiveCount, 0)
        XCTAssertEqual(page.hiddenCount, 1)

        page.beginAppearanceTransition(true, animated: false)
        page.endAppearanceTransition()
        NotificationCenter.default.post(name: UIApplication.willResignActiveNotification, object: nil)
        NotificationCenter.default.post(name: UIApplication.willResignActiveNotification, object: nil)
        XCTAssertTrue(page.isPageActive)
        XCTAssertFalse(page.isPageVisible)
        NotificationCenter.default.post(name: UIApplication.didBecomeActiveNotification, object: nil)
        NotificationCenter.default.post(name: UIApplication.didBecomeActiveNotification, object: nil)
        XCTAssertTrue(page.isPageVisible)
        XCTAssertEqual(page.activeCount, 1)
        XCTAssertEqual(page.visibleCount, 3)
        XCTAssertEqual(page.hiddenCount, 2)
        XCTAssertEqual(page.inactiveCount, 0)
    }

    func testPublisherRemainsSubscribedUntilRemoval() async {
        let container = UIViewController()
        let page = PageLifecycleTestPage(themeProvider: PageLifecycleTestTheme())
        container.addChild(page)
        container.view.addSubview(page.view)
        page.didMove(toParent: container)
        let changes = PassthroughSubject<Void, Never>()
        var subscriptions = 0, cancellations = 0, updates = 0
        let updated = expectation(description: "updates while retained")
        updated.expectedFulfillmentCount = 3
        page.observe(changes.handleEvents(
            receiveSubscription: { _ in subscriptions += 1 },
            receiveCancel: { cancellations += 1 }
        ).eraseToAnyPublisher()) {
            updates += 1
            updated.fulfill()
        }
        XCTAssertEqual(subscriptions, 0)
        page.beginAppearanceTransition(true, animated: false)
        page.endAppearanceTransition()
        page.beginAppearanceTransition(false, animated: false)
        page.endAppearanceTransition()
        changes.send()
        NotificationCenter.default.post(name: UIApplication.willResignActiveNotification, object: nil)
        changes.send()
        NotificationCenter.default.post(name: UIApplication.didBecomeActiveNotification, object: nil)
        page.beginAppearanceTransition(true, animated: false)
        page.endAppearanceTransition()
        changes.send()
        await fulfillment(of: [updated], timeout: 2)
        XCTAssertEqual(subscriptions, 1)
        XCTAssertEqual(cancellations, 0)
        XCTAssertEqual(updates, 3)

        changes.send()
        page.willMove(toParent: nil)
        page.view.removeFromSuperview()
        page.removeFromParent()
        changes.send()
        let drained = expectation(description: "main run loop drained")
        RunLoop.main.perform { drained.fulfill() }
        await fulfillment(of: [drained], timeout: 2)
        XCTAssertFalse(page.isPageActive)
        XCTAssertFalse(page.isPageVisible)
        XCTAssertEqual(page.inactiveCount, 1)
        XCTAssertEqual(cancellations, 1)
        XCTAssertEqual(updates, 3)
        page.beginAppearanceTransition(true, animated: false)
        page.endAppearanceTransition()
        XCTAssertFalse(page.isPageActive)
        XCTAssertEqual(page.activeCount, 1)
        XCTAssertEqual(subscriptions, 1)
    }

    func testFocusedObjectObservationContinuesWhileHidden() async {
        let page = PageLifecycleTestPage(themeProvider: PageLifecycleTestTheme())
        let object = PageLifecycleTestObject()
        let updated = expectation(description: "focused object update")
        var observedValue: Int?
        page.observe(object) {
            observedValue = object.value
            updated.fulfill()
        }
        page.beginAppearanceTransition(true, animated: false)
        page.endAppearanceTransition()
        page.beginAppearanceTransition(false, animated: false)
        page.endAppearanceTransition()
        object.value = 7
        await fulfillment(of: [updated], timeout: 2)
        XCTAssertEqual(observedValue, 7)
        XCTAssertTrue(page.isPageActive)
    }

    func testValuePublisherPreservesPayloadThroughRetainedDetachAndCancelsOnExplicitRelease() async {
        let owner = PageLifecycleTestPage(themeProvider: PageLifecycleTestTheme())
        owner.beginAppearanceTransition(true, animated: false)
        owner.endAppearanceTransition()
        let wrapper = PageLifecycleTestModalWrapper()
        let page = PageLifecycleTestPage(themeProvider: PageLifecycleTestTheme())
        page.lifetimeOwner = owner
        wrapper.addChild(page)
        wrapper.view.addSubview(page.view)
        page.didMove(toParent: wrapper)
        let changes = PassthroughSubject<Set<String>, Never>()
        var subscriptions = 0, cancellations = 0
        var values: [Set<String>] = []
        let updated = expectation(description: "account platform changes delivered")
        updated.expectedFulfillmentCount = 3
        page.observe(changes.handleEvents(
            receiveSubscription: { _ in subscriptions += 1 },
            receiveCancel: { cancellations += 1 }
        ).eraseToAnyPublisher()) { platforms in
            values.append(platforms)
            updated.fulfill()
        }
        XCTAssertEqual(subscriptions, 0)
        page.beginAppearanceTransition(true, animated: false)
        page.endAppearanceTransition()
        changes.send(["tx"])
        wrapper.moving = true
        page.beginAppearanceTransition(false, animated: false)
        page.endAppearanceTransition()
        page.willMove(toParent: nil)
        page.view.removeFromSuperview()
        page.removeFromParent()
        NotificationCenter.default.post(name: UIApplication.willResignActiveNotification, object: nil)
        changes.send(["wy"])
        XCTAssertTrue(page.isPageActive)
        XCTAssertFalse(page.isPageVisible)
        XCTAssertEqual(page.inactiveCount, 0)
        XCTAssertEqual(cancellations, 0)
        wrapper.moving = false
        wrapper.addChild(page)
        wrapper.view.addSubview(page.view)
        page.didMove(toParent: wrapper)
        NotificationCenter.default.post(name: UIApplication.didBecomeActiveNotification, object: nil)
        page.beginAppearanceTransition(true, animated: false)
        page.endAppearanceTransition()
        changes.send(["kg", "wy"])
        await fulfillment(of: [updated], timeout: 2)
        XCTAssertEqual(values, [Set(["tx"]), Set(["wy"]), Set(["kg", "wy"])])
        XCTAssertEqual(subscriptions, 1)
        XCTAssertEqual(page.activeCount, 1)
        PageViewController.invalidatePages(in: page)
        page.willMove(toParent: nil)
        page.view.removeFromSuperview()
        page.removeFromParent()
        changes.send(["mg"])
        let drained = expectation(description: "released publisher drained")
        RunLoop.main.perform { drained.fulfill() }
        await fulfillment(of: [drained], timeout: 2)
        XCTAssertTrue(owner.isPageActive)
        XCTAssertFalse(page.isPageActive)
        XCTAssertEqual(page.inactiveCount, 1)
        XCTAssertEqual(cancellations, 1)
        XCTAssertEqual(values.count, 3)
    }

    func testRetainedDetachHidesVisualsButKeepsTaskUntilExplicitRelease() async {
        let owner = PageLifecycleTestPage(themeProvider: PageLifecycleTestTheme())
        let wrapper = UIViewController()
        let page = PageLifecycleTestPage(themeProvider: PageLifecycleTestTheme())
        page.lifetimeOwner = owner
        wrapper.addChild(page)
        wrapper.view.addSubview(page.view)
        page.didMove(toParent: wrapper)
        page.beginAppearanceTransition(true, animated: false)
        page.endAppearanceTransition()
        NotificationCenter.default.post(name: UIApplication.didBecomeActiveNotification, object: nil)
        let started = expectation(description: "cached task started")
        let finished = expectation(description: "cached task survived detach")
        var continuation: CheckedContinuation<Void, Never>?
        page.runPageTask {
            await withCheckedContinuation { continuation = $0; started.fulfill() }
            XCTAssertFalse(Task.isCancelled)
            finished.fulfill()
        }
        await fulfillment(of: [started], timeout: 2)
        page.willMove(toParent: nil)
        page.view.removeFromSuperview()
        page.removeFromParent()
        XCTAssertFalse(owner.isPageActive, "An owner awaiting first appearance is not closed")
        XCTAssertTrue(page.isPageActive)
        XCTAssertFalse(page.isPageVisible)
        XCTAssertEqual(page.hiddenCount, 1)
        continuation?.resume()
        await fulfillment(of: [finished], timeout: 2)
        let closingStarted = expectation(description: "cached closing task started")
        let cancelled = expectation(description: "cached closing task cancelled")
        page.runPageTask {
            await withCheckedContinuation { continuation = $0; closingStarted.fulfill() }
            XCTAssertTrue(Task.isCancelled)
            cancelled.fulfill()
        }
        await fulfillment(of: [closingStarted], timeout: 2)
        PageViewController.invalidatePages(in: page)
        continuation?.resume()
        await fulfillment(of: [cancelled], timeout: 2)
        XCTAssertFalse(page.isPageActive)
        XCTAssertEqual(page.inactiveCount, 1)
    }

    func testRetainedContentIgnoresTemporaryWrapperDismissalButClosedOwnerEndsContent() async {
        let owner = PageLifecycleTestPage(themeProvider: PageLifecycleTestTheme())
        owner.beginAppearanceTransition(true, animated: false)
        owner.endAppearanceTransition()
        let wrapper = PageLifecycleTestModalWrapper()
        let page = PageLifecycleTestPage(themeProvider: PageLifecycleTestTheme())
        page.lifetimeOwner = owner
        wrapper.addChild(page)
        wrapper.view.addSubview(page.view)
        page.didMove(toParent: wrapper)
        page.beginAppearanceTransition(true, animated: false)
        page.endAppearanceTransition()
        wrapper.dismissing = true
        page.beginAppearanceTransition(false, animated: false)
        page.endAppearanceTransition()
        XCTAssertTrue(page.isPageActive)
        XCTAssertEqual(page.inactiveCount, 0)
        PageViewController.invalidatePages(in: owner)
        page.beginAppearanceTransition(true, animated: false)
        page.endAppearanceTransition()
        page.beginAppearanceTransition(false, animated: false)
        page.endAppearanceTransition()
        XCTAssertFalse(page.isPageActive)
        XCTAssertEqual(page.activeCount, 1)
        XCTAssertEqual(page.inactiveCount, 1)
    }

    func testRootRemovalClosesAttachedRetainedContentDespiteExplicitOwner() async {
        let outer = UIViewController()
        let owner = PageLifecycleTestPage(themeProvider: PageLifecycleTestTheme())
        let wrapper = UIViewController()
        let page = PageLifecycleTestPage(themeProvider: PageLifecycleTestTheme())
        page.lifetimeOwner = owner
        outer.addChild(owner)
        outer.view.addSubview(owner.view)
        owner.didMove(toParent: outer)
        owner.addChild(wrapper)
        owner.view.addSubview(wrapper.view)
        wrapper.didMove(toParent: owner)
        wrapper.addChild(page)
        wrapper.view.addSubview(page.view)
        page.didMove(toParent: wrapper)
        owner.beginAppearanceTransition(true, animated: false)
        owner.endAppearanceTransition()
        XCTAssertTrue(page.isPageActive)
        owner.willMove(toParent: nil)
        owner.view.removeFromSuperview()
        owner.removeFromParent()
        XCTAssertFalse(owner.isPageActive)
        XCTAssertFalse(page.isPageActive)
        XCTAssertEqual(owner.inactiveCount, 1)
        XCTAssertEqual(page.inactiveCount, 1)
    }

    func testLifetimeOwnerIsWeakAndReleasedOwnerDoesNotSuppressRemoval() async {
        let wrapper = UIViewController()
        let page = PageLifecycleTestPage(themeProvider: PageLifecycleTestTheme())
        autoreleasepool {
            let owner = PageLifecycleTestPage(themeProvider: PageLifecycleTestTheme())
            page.lifetimeOwner = owner
            XCTAssertTrue(page.lifetimeOwner === owner)
        }
        wrapper.addChild(page)
        wrapper.view.addSubview(page.view)
        page.didMove(toParent: wrapper)
        page.beginAppearanceTransition(true, animated: false)
        page.endAppearanceTransition()
        XCTAssertNil(page.lifetimeOwner)
        page.willMove(toParent: nil)
        page.view.removeFromSuperview()
        page.removeFromParent()
        XCTAssertFalse(page.isPageActive)
        XCTAssertEqual(page.inactiveCount, 1)
    }

    func testTasksSurviveCoverageAndBackgroundButCancelOnRemoval() async {
        let container = UIViewController()
        let page = PageLifecycleTestPage(themeProvider: PageLifecycleTestTheme())
        container.addChild(page)
        container.view.addSubview(page.view)
        page.didMove(toParent: container)
        page.beginAppearanceTransition(true, animated: false)
        page.endAppearanceTransition()
        let started = expectation(description: "retained task started")
        let finished = expectation(description: "retained task finished")
        var continuation: CheckedContinuation<Void, Never>?
        page.runPageTask {
            await withCheckedContinuation { continuation = $0; started.fulfill() }
            XCTAssertFalse(Task.isCancelled)
            finished.fulfill()
        }
        await fulfillment(of: [started], timeout: 2)
        page.beginAppearanceTransition(false, animated: false)
        page.endAppearanceTransition()
        NotificationCenter.default.post(name: UIApplication.willResignActiveNotification, object: nil)
        XCTAssertTrue(page.isPageActive)
        continuation?.resume()
        await fulfillment(of: [finished], timeout: 2)

        let closingStarted = expectation(description: "closing task started")
        let cancelled = expectation(description: "closing task cancelled")
        page.runPageTask {
            await withCheckedContinuation { continuation = $0; closingStarted.fulfill() }
            XCTAssertTrue(Task.isCancelled)
            cancelled.fulfill()
        }
        await fulfillment(of: [closingStarted], timeout: 2)
        page.willMove(toParent: nil)
        page.view.removeFromSuperview()
        page.removeFromParent()
        continuation?.resume()
        await fulfillment(of: [cancelled], timeout: 2)
        XCTAssertFalse(page.isPageActive)
        XCTAssertEqual(page.activeCount, 1)
        XCTAssertEqual(page.inactiveCount, 1)
    }

    func testAllValidPageTasksStartAndInvalidateTogether() async {
        let page = PageLifecycleTestPage(themeProvider: PageLifecycleTestTheme())
        page.beginAppearanceTransition(true, animated: false)
        page.endAppearanceTransition()
        let started = expectation(description: "page tasks started")
        let finished = expectation(description: "page tasks cancelled")
        started.expectedFulfillmentCount = 10
        finished.expectedFulfillmentCount = 10
        var continuations: [Int: CheckedContinuation<Void, Never>] = [:]
        for index in 0..<10 {
            page.runPageTask {
                await withCheckedContinuation { continuations[index] = $0; started.fulfill() }
                XCTAssertTrue(Task.isCancelled)
                finished.fulfill()
            }
        }
        await fulfillment(of: [started], timeout: 2)
        XCTAssertEqual(continuations.count, 10)
        PageViewController.invalidatePages(in: page)
        continuations.values.forEach { $0.resume() }
        continuations.removeAll()
        await fulfillment(of: [finished], timeout: 2)
        XCTAssertFalse(page.isPageActive)
        var restarted = false
        page.runPageTask { restarted = true }
        let drained = expectation(description: "invalidated tasks drained")
        RunLoop.main.perform { drained.fulfill() }
        await fulfillment(of: [drained], timeout: 2)
        XCTAssertFalse(restarted)
    }

    func testModalWrapperDismissalWaitsForCommitAndIgnoresCancellation() async {
        let wrapper = PageLifecycleTestModalWrapper()
        let page = PageLifecycleTestPage(themeProvider: PageLifecycleTestTheme())
        wrapper.addChild(page)
        wrapper.view.addSubview(page.view)
        page.didMove(toParent: wrapper)
        page.beginAppearanceTransition(true, animated: false)
        page.endAppearanceTransition()
        NotificationCenter.default.post(name: UIApplication.didBecomeActiveNotification, object: nil)
        let coordinator = PageLifecycleTestCoordinator()
        wrapper.coordinator = coordinator
        wrapper.dismissing = true
        page.beginAppearanceTransition(false, animated: true)
        page.endAppearanceTransition()
        XCTAssertTrue(page.isPageActive)
        coordinator.complete(cancelled: true)
        wrapper.dismissing = false
        page.beginAppearanceTransition(true, animated: false)
        page.endAppearanceTransition()
        XCTAssertTrue(page.isPageActive)
        XCTAssertEqual(page.activeCount, 1)
        XCTAssertEqual(page.inactiveCount, 0)

        wrapper.dismissing = true
        page.beginAppearanceTransition(false, animated: true)
        page.endAppearanceTransition()
        XCTAssertTrue(page.isPageActive)
        coordinator.complete(cancelled: false)
        XCTAssertFalse(page.isPageActive)
        XCTAssertFalse(page.isPageVisible)
        XCTAssertEqual(page.inactiveCount, 1)
        XCTAssertEqual(page.visibleCount, page.hiddenCount)
    }

    func testNonanimatedModalWrapperDismissalClosesChildWithoutRemovingContainment() async {
        let wrapper = PageLifecycleTestModalWrapper()
        let page = PageLifecycleTestPage(themeProvider: PageLifecycleTestTheme())
        wrapper.addChild(page)
        wrapper.view.addSubview(page.view)
        page.didMove(toParent: wrapper)
        page.beginAppearanceTransition(true, animated: false)
        page.endAppearanceTransition()
        wrapper.dismissing = true
        page.beginAppearanceTransition(false, animated: false)
        page.endAppearanceTransition()
        XCTAssertTrue(page.parent === wrapper)
        XCTAssertFalse(page.isPageActive)
        XCTAssertEqual(page.inactiveCount, 1)
    }

    func testActualModalNavigationCoverageAndDismissalHaveDifferentLifetimes() async throws {
        let theme = PageLifecycleTestTheme()
        let presenter = PageLifecycleTestPresenter()
        let ready = expectation(description: "presenter appeared")
        presenter.onDidAppear = { ready.fulfill() }
        let page = PageLifecycleTestPage(themeProvider: theme)
        let wrapper = PageNavigationController(themeProvider: theme)
        wrapper.setViewControllers([page], animated: false)
        wrapper.modalPresentationStyle = .fullScreen
        let scene = try XCTUnwrap(UIApplication.shared.connectedScenes.compactMap { $0 as? UIWindowScene }.first)
        let previousKeyWindow = scene.windows.first { $0.isKeyWindow }
        let window = UIWindow(windowScene: scene)
        window.rootViewController = presenter
        window.makeKeyAndVisible()
        defer {
            window.isHidden = true
            window.rootViewController = nil
            previousKeyWindow?.makeKey()
        }
        await fulfillment(of: [ready], timeout: 2)
        let presented = expectation(description: "modal wrapper presented")
        presenter.present(wrapper, animated: false) { presented.fulfill() }
        await fulfillment(of: [presented], timeout: 2)
        window.layoutIfNeeded()
        wrapper.view.layoutIfNeeded()
        NotificationCenter.default.post(name: UIApplication.didBecomeActiveNotification, object: nil)
        XCTAssertTrue(page.view.window === window)
        XCTAssertTrue(page.isPageActive)
        XCTAssertTrue(page.isPageVisible)
        wrapper.pushViewController(UIViewController(), animated: false)
        wrapper.view.layoutIfNeeded()
        let covered = expectation(for: NSPredicate { _, _ in
            MainActor.assumeIsolated { !page.isPageVisible }
        }, evaluatedWith: page)
        await fulfillment(of: [covered], timeout: 2)
        XCTAssertTrue(page.isPageActive)
        XCTAssertFalse(page.isPageVisible)
        XCTAssertEqual(page.inactiveCount, 0)
        wrapper.popViewController(animated: false)
        wrapper.view.layoutIfNeeded()
        let revealed = expectation(for: NSPredicate { _, _ in
            MainActor.assumeIsolated { page.isPageVisible }
        }, evaluatedWith: page)
        await fulfillment(of: [revealed], timeout: 2)
        XCTAssertTrue(page.isPageVisible)
        XCTAssertEqual(page.activeCount, 1)
        let dismissed = expectation(description: "modal wrapper dismissed")
        wrapper.dismiss(animated: false) { dismissed.fulfill() }
        await fulfillment(of: [dismissed], timeout: 2)
        XCTAssertTrue(page.parent === wrapper)
        XCTAssertFalse(page.isPageActive)
        XCTAssertFalse(page.isPageVisible)
        XCTAssertEqual(page.inactiveCount, 1)
    }

    func testAlertAdmissionMatchesShowErrorWithoutChangingPageLifetime() async throws {
        let page = PageAlertTestPage(themeProvider: PageLifecycleTestTheme())
        XCTAssertFalse(page.canPresentPageAlert)
        XCTAssertFalse(page.isViewLoaded)
        page.beginAppearanceTransition(true, animated: false)
        page.endAppearanceTransition()
        NotificationCenter.default.post(name: UIApplication.didBecomeActiveNotification, object: nil)
        XCTAssertTrue(page.isPageVisible)
        XCTAssertFalse(page.canPresentPageAlert, "A visible page still needs a window")
        let root = PageLifecycleTestModalWrapper()
        let parent = PageLifecycleTestModalWrapper()
        root.addChild(parent)
        root.view.addSubview(parent.view)
        parent.didMove(toParent: root)
        parent.addChild(page)
        parent.view.addSubview(page.view)
        page.didMove(toParent: parent)
        let scene = try XCTUnwrap(UIApplication.shared.connectedScenes.compactMap { $0 as? UIWindowScene }.first)
        let previousKeyWindow = scene.windows.first { $0.isKeyWindow }
        let window = UIWindow(windowScene: scene)
        window.rootViewController = root
        window.makeKeyAndVisible()
        defer {
            window.isHidden = true
            window.rootViewController = nil
            previousKeyWindow?.makeKey()
        }
        let displayed = expectation(description: "alert page displayed")
        RunLoop.main.perform { displayed.fulfill() }
        await fulfillment(of: [displayed], timeout: 2)
        XCTAssertTrue(page.canPresentPageAlert)
        XCTAssertFalse(page.showError(nil))
        XCTAssertFalse(page.showError(""))
        let assertBlocked = {
            XCTAssertFalse(page.canPresentPageAlert)
            XCTAssertFalse(page.showError("failure"))
            XCTAssertTrue(page.isPageActive)
            XCTAssertTrue(page.alerts.isEmpty)
        }
        page.covering = UIViewController()
        assertBlocked()
        page.covering = nil
        let transition = PageLifecycleTestCoordinator()
        page.coordinator = transition
        page.viewWillTransition(to: page.view.bounds.size, with: transition)
        assertBlocked()
        transition.complete(cancelled: false)
        XCTAssertTrue(page.canPresentPageAlert, "A retained coordinator is no longer active after its completion")
        page.coordinator = PageLifecycleTestCoordinator()
        assertBlocked()
        page.coordinator = nil
        for ancestor in [parent, root] {
            ancestor.covering = UIViewController()
            assertBlocked()
            ancestor.covering = nil
            ancestor.presenting = true
            assertBlocked()
            ancestor.presenting = false
            ancestor.dismissing = true
            assertBlocked()
            ancestor.dismissing = false
            ancestor.movingTo = true
            assertBlocked()
            ancestor.movingTo = false
            ancestor.moving = true
            assertBlocked()
            ancestor.moving = false
            ancestor.coordinator = PageLifecycleTestCoordinator()
            assertBlocked()
            ancestor.coordinator = nil
        }
        NotificationCenter.default.post(name: UIApplication.willResignActiveNotification, object: nil)
        assertBlocked()
        NotificationCenter.default.post(name: UIApplication.didBecomeActiveNotification, object: nil)
        page.beginAppearanceTransition(false, animated: false)
        page.endAppearanceTransition()
        assertBlocked()
        page.beginAppearanceTransition(true, animated: false)
        page.endAppearanceTransition()
        XCTAssertTrue(page.canPresentPageAlert)
        XCTAssertTrue(page.showError("failure"))
        let alert = try XCTUnwrap(page.alerts.first as? UIAlertController)
        XCTAssertEqual(alert.message, "failure")
        XCTAssertTrue(page.isPageActive)
    }

    func testShowErrorKeepsDefaultConfirmationAndAcknowledgedCallback() async throws {
        try await performRunnerAction { $0.activate() }
        let page = PageLifecycleTestPage(themeProvider: PageLifecycleTestTheme())
        let scene = try XCTUnwrap(UIApplication.shared.connectedScenes.compactMap { $0 as? UIWindowScene }.first)
        let previousKeyWindow = scene.windows.first { $0.isKeyWindow }
        let window = UIWindow(windowScene: scene)
        window.rootViewController = page
        window.makeKeyAndVisible()
        defer {
            page.dismiss(animated: false)
            PageViewController.invalidatePages(in: page)
            window.isHidden = true
            window.rootViewController = nil
            previousKeyWindow?.makeKey()
        }
        await waitForErrorAlertState("page ready") { page.canPresentPageAlert }
        XCTAssertTrue(page.showError("default failure"))
        await waitForErrorAlertState("default error presented") {
            guard let alert = page.presentedViewController as? UIAlertController else { return false }
            return alert.view.window != nil && !alert.isBeingPresented
        }
        let first = try XCTUnwrap(page.presentedViewController as? UIAlertController)
        XCTAssertEqual(first.message, "default failure")
        XCTAssertEqual(first.actions.map(\.title), ["确定"])
        try await performRunnerAction { $0.alerts["错误"].buttons["确定"].tap() }
        await waitForErrorAlertState("default error dismissed") { page.presentedViewController == nil && page.canPresentPageAlert }
        var acknowledgements = 0
        XCTAssertTrue(page.showError("acknowledged failure") { acknowledgements += 1 })
        await waitForErrorAlertState("acknowledged error presented") {
            guard let alert = page.presentedViewController as? UIAlertController else { return false }
            return alert.view.window != nil && !alert.isBeingPresented
        }
        let second = try XCTUnwrap(page.presentedViewController as? UIAlertController)
        XCTAssertEqual(second.message, "acknowledged failure")
        XCTAssertEqual(second.actions.map(\.title), ["确定"])
        XCTAssertEqual(acknowledgements, 0)
        try await performRunnerAction { $0.alerts["错误"].buttons["确定"].tap() }
        await waitForErrorAlertState("acknowledged callback completed") {
            page.presentedViewController == nil && acknowledgements == 1
        }
        await drainErrorAlertRunLoop()
        XCTAssertEqual(acknowledgements, 1)
        XCTAssertTrue(page.isPageActive)
    }

    func testRetryableErrorCallsRetryOnceAfterActualDismissal() async throws {
        try await performRunnerAction { $0.activate() }
        let page = PageLifecycleTestPage(themeProvider: PageLifecycleTestTheme())
        let scene = try XCTUnwrap(UIApplication.shared.connectedScenes.compactMap { $0 as? UIWindowScene }.first)
        let previousKeyWindow = scene.windows.first { $0.isKeyWindow }
        let window = UIWindow(windowScene: scene)
        window.rootViewController = page
        window.makeKeyAndVisible()
        defer {
            page.dismiss(animated: false)
            PageViewController.invalidatePages(in: page)
            window.isHidden = true
            window.rootViewController = nil
            previousKeyWindow?.makeKey()
        }
        await waitForErrorAlertState("page ready") { page.canPresentPageAlert }
        var retries = 0
        weak var presentedAlert: UIAlertController?
        let retried = expectation(description: "retry after dismissal")
        retried.assertForOverFulfill = true
        XCTAssertTrue(page.showRetryableError("retry failure") {
            retries += 1
            XCTAssertNil(page.presentedViewController)
            XCTAssertNil(presentedAlert?.presentingViewController)
            XCTAssertNil(presentedAlert?.viewIfLoaded?.window)
            XCTAssertFalse(presentedAlert?.isBeingDismissed ?? false)
            XCTAssertTrue(page.isPageActive)
            XCTAssertTrue(page.canPresentPageAlert, "The actual dismissal receipt also settles alert admission")
            retried.fulfill()
        })
        await waitForErrorAlertState("retryable error presented") {
            guard let alert = page.presentedViewController as? UIAlertController else { return false }
            return alert.view.window != nil && !alert.isBeingPresented
        }
        let alert = try XCTUnwrap(page.presentedViewController as? UIAlertController)
        presentedAlert = alert
        XCTAssertEqual(alert.message, "retry failure")
        XCTAssertEqual(alert.actions.map(\.title), ["确定", "重试"])
        XCTAssertEqual(alert.actions.map(\.style), [.cancel, .default])
        XCTAssertEqual(retries, 0)
        try await performRunnerAction { $0.alerts["错误"].buttons["重试"].tap() }
        await fulfillment(of: [retried], timeout: 4)
        await drainErrorAlertRunLoop()
        XCTAssertEqual(retries, 1)
        XCTAssertNil(page.presentedViewController)

        await waitForErrorAlertState("page ready for cancellation") { page.canPresentPageAlert }
        XCTAssertTrue(page.showRetryableError("cancel retry") { retries += 1 })
        await waitForErrorAlertState("cancelable retry error presented") {
            guard let alert = page.presentedViewController as? UIAlertController else { return false }
            return alert.view.window != nil && !alert.isBeingPresented
        }
        try await performRunnerAction { $0.alerts["错误"].buttons["确定"].tap() }
        await waitForErrorAlertState("retry cancelled") { page.presentedViewController == nil }
        await drainErrorAlertRunLoop()
        XCTAssertEqual(retries, 1, "Confirmation must not invoke retry")
    }

    func testRetryableErrorDoesNotCallRetryAfterPageInvalidation() async throws {
        let runner = XCUIApplication(bundleIdentifier: try XCTUnwrap(Bundle.main.bundleIdentifier))
        try await performRunnerAction { $0.activate() }
        let page = PageLifecycleTestPage(themeProvider: PageLifecycleTestTheme())
        let scene = try XCTUnwrap(UIApplication.shared.connectedScenes.compactMap { $0 as? UIWindowScene }.first)
        let previousKeyWindow = scene.windows.first { $0.isKeyWindow }
        let window = UIWindow(windowScene: scene)
        window.rootViewController = page
        window.makeKeyAndVisible()
        defer {
            page.dismiss(animated: false)
            PageViewController.invalidatePages(in: page)
            window.isHidden = true
            window.rootViewController = nil
            previousKeyWindow?.makeKey()
        }
        await waitForErrorAlertState("page ready") { page.canPresentPageAlert }
        var retries = 0
        XCTAssertTrue(page.showRetryableError("invalidated failure") { retries += 1 })
        await waitForErrorAlertState("error presented before invalidation") {
            guard let alert = page.presentedViewController as? UIAlertController else { return false }
            return alert.view.window != nil && !alert.isBeingPresented
        }
        let alert = try XCTUnwrap(page.presentedViewController as? UIAlertController)
        PageViewController.invalidatePages(in: page)
        XCTAssertFalse(page.isPageActive)
        XCTAssertTrue(runner.alerts["错误"].buttons["重试"].isHittable)
        try await performRunnerAction { $0.alerts["错误"].buttons["重试"].tap() }
        await waitForErrorAlertState("invalidated page alert dismissed") {
            page.presentedViewController == nil && alert.viewIfLoaded?.window == nil
        }
        await drainErrorAlertRunLoop()
        XCTAssertEqual(retries, 0)
    }

    func testModalDismissalReceiptSettlesOnlyItsOwnTransitionBeforeFollowUp() async throws {
        try await performRunnerAction { $0.activate() }
        for replaced in [false, true] {
            let page = PageAlertTestPage(themeProvider: PageLifecycleTestTheme())
            try mountDismissalPage(page)
            await waitForErrorAlertState("dismissal page ready") { page.canPresentPageAlert }
            let modal = PageDismissalTestModal()
            page.covering = modal
            let unrelated = PageDismissalTestModal()
            page.dismissPageModal(unrelated) { XCTFail("An unrelated modal has no dismissal receipt for this page") }
            XCTAssertFalse(unrelated.dismissalStarted)
            var completions = 0
            page.dismissPageModal(modal) {
                completions += 1
                XCTAssertNil(page.presentedViewController)
                XCTAssertEqual(page.canPresentPageAlert, !replaced)
            }
            XCTAssertTrue(modal.dismissalStarted)
            XCTAssertEqual(completions, 0)
            page.coordinator = modal.coordinator
            let replacement = PageLifecycleTestCoordinator()
            if replaced {
                page.coordinator = replacement
                page.viewWillTransition(to: page.view.bounds.size, with: replacement)
            }
            page.covering = nil
            XCTAssertFalse(page.canPresentPageAlert, "Removing the modal alone is not a transition receipt")
            modal.completeDismissal()
            XCTAssertEqual(completions, 1)
            XCTAssertEqual(page.canPresentPageAlert, !replaced)
            if replaced {
                replacement.complete(cancelled: false)
                XCTAssertTrue(page.canPresentPageAlert, "The new transition still needs its own completion")
            }
        }
    }

    func testModalDismissalReceiptDoesNotExecuteFollowUpAfterInvalidation() async throws {
        try await performRunnerAction { $0.activate() }
        let page = PageAlertTestPage(themeProvider: PageLifecycleTestTheme())
        try mountDismissalPage(page)
        await waitForErrorAlertState("dismissal page ready before invalidation") { page.canPresentPageAlert }
        let modal = PageDismissalTestModal()
        page.covering = modal
        var completions = 0
        page.dismissPageModal(modal) { completions += 1 }
        XCTAssertTrue(modal.dismissalStarted)
        page.coordinator = modal.coordinator
        page.covering = nil
        PageViewController.invalidatePages(in: page)
        modal.completeDismissal()
        XCTAssertEqual(completions, 0)
        XCTAssertFalse(page.isPageActive)
        XCTAssertFalse(page.canPresentPageAlert)
    }

    func testAutomaticallyDismissedModalRunsFollowUpOnlyForItsRecordedPageOnce() async throws {
        try await performRunnerAction { $0.activate() }
        let page = PageLifecycleTestPage(themeProvider: PageLifecycleTestTheme())
        try mountDismissalPage(page)
        await waitForErrorAlertState("page ready before automatic dismissal") { page.canPresentPageAlert }
        let alert = UIAlertController(title: "自动退出", message: nil, preferredStyle: .alert)
        alert.addAction(UIAlertAction(title: "确定", style: .default))
        page.present(alert, animated: true)
        await waitForErrorAlertState("automatic dismissal alert presented") {
            alert.viewIfLoaded?.window != nil && !alert.isBeingPresented
        }
        try await performRunnerAction { $0.alerts["自动退出"].buttons["确定"].tap() }
        await waitForErrorAlertState("automatic dismissal has removed presentation links") {
            page.presentedViewController == nil && alert.presentingViewController == nil &&
                alert.viewIfLoaded?.window == nil && !alert.isBeingDismissed
        }
        let otherPage = PageLifecycleTestPage(themeProvider: PageLifecycleTestTheme())
        otherPage.dismissPageModal(alert) { XCTFail("Another page cannot consume the former modal") }
        var completions = 0
        page.dismissPageModal(alert) {
            completions += 1
            XCTAssertTrue(page.canPresentPageAlert)
        }
        XCTAssertEqual(completions, 1)
        page.dismissPageModal(alert) { completions += 1 }
        XCTAssertEqual(completions, 1, "The recorded automatic dismissal can only be consumed once")
    }

    private func mountDismissalPage(_ page: PageViewController) throws {
        let scene = try XCTUnwrap(UIApplication.shared.connectedScenes.compactMap { $0 as? UIWindowScene }.first {
            $0.activationState == .foregroundActive
        })
        let previous = scene.windows.first { $0.isKeyWindow }
        let window = UIWindow(windowScene: scene)
        window.frame = scene.coordinateSpace.bounds
        addTeardownBlock {
            await MainActor.run {
                PageViewController.invalidatePages(in: page)
                window.isHidden = true; window.rootViewController = nil
                previous?.makeKey()
            }
        }
        window.rootViewController = page; window.makeKeyAndVisible(); window.layoutIfNeeded()
        page.view.layoutIfNeeded()
    }

    private func waitForErrorAlertState(_ description: String,
                                       file: StaticString = #filePath, line: UInt = #line,
                                       _ predicate: @escaping @MainActor () -> Bool) async {
        let ready = expectation(for: NSPredicate { _, _ in MainActor.assumeIsolated { predicate() } },
                                evaluatedWith: nil)
        let result = await XCTWaiter.fulfillment(of: [ready], timeout: 4)
        XCTAssertEqual(result, .completed, description, file: file, line: line)
    }

    private func drainErrorAlertRunLoop() async {
        await withCheckedContinuation { continuation in
            RunLoop.main.perform { continuation.resume() }
        }
    }

    // XCTest requires the main thread, but its nested run loop must not hold
    // this async test's actor job while delivering touches to the runner itself.
    private func performRunnerAction(_ action: @escaping @MainActor (XCUIApplication) -> Void) async throws {
        let identifier = try XCTUnwrap(Bundle.main.bundleIdentifier)
        await withCheckedContinuation { continuation in
            RunLoop.main.perform {
                MainActor.assumeIsolated { action(XCUIApplication(bundleIdentifier: identifier)) }
                continuation.resume()
            }
        }
    }

    func testDeinitCancelsTasksAndSubscriptions() async {
        var page: PageLifecycleTestPage? = PageLifecycleTestPage(themeProvider: PageLifecycleTestTheme())
        let changes = PassthroughSubject<Void, Never>()
        var cancellations = 0
        page?.observe(changes.handleEvents(
            receiveCancel: { cancellations += 1 }
        ).eraseToAnyPublisher()) {}
        page?.beginAppearanceTransition(true, animated: false)
        page?.endAppearanceTransition()
        let started = expectation(description: "deinit task started")
        let cancelled = expectation(description: "deinit task cancelled")
        var continuation: CheckedContinuation<Void, Never>?
        page?.runPageTask {
            await withCheckedContinuation { continuation = $0; started.fulfill() }
            XCTAssertTrue(Task.isCancelled)
            cancelled.fulfill()
        }
        await fulfillment(of: [started], timeout: 2)
        weak var released = page
        let synchronousRelease = expectation(description: "synchronous page release in task-local scope")
        let releaseOnMain = { @MainActor in
            PageLifecycleTestLocal.$marker.withValue(1) {
                page = nil
                XCTAssertNil(released)
                XCTAssertEqual(cancellations, 1)
            }
            XCTAssertEqual(PageLifecycleTestLocal.marker, 0)
            synchronousRelease.fulfill()
        }
        RunLoop.main.perform { MainActor.assumeIsolated { releaseOnMain() } }
        await fulfillment(of: [synchronousRelease], timeout: 2)
        continuation?.resume()
        await fulfillment(of: [cancelled], timeout: 2)
    }
}

private enum PageLifecycleTestLocal {
    @TaskLocal static var marker = 0
}

@MainActor
final class PageLifecycleTestTheme: PageThemeProviding {
    let pageColorway = PageColorway.allCases[0]
    let animatesPageColorway = false
    var pageThemeChanges: AnyPublisher<Void, Never> {
        Empty(completeImmediately: false).eraseToAnyPublisher()
    }
}

@MainActor
final class PageLifecycleTestPage: PageViewController {
    var activeCount = 0, inactiveCount = 0, visibleCount = 0, hiddenCount = 0
    var appearanceEvents: [String] = []
    override func pageDidBecomeActive() { activeCount += 1 }
    override func pageDidBecomeInactive() { inactiveCount += 1 }
    override func pageDidBecomeVisible() { visibleCount += 1 }
    override func pageDidBecomeHidden() { hiddenCount += 1 }
    override func viewWillAppear(_ animated: Bool) {
        super.viewWillAppear(animated)
        appearanceEvents.append("willAppear")
    }
    override func viewDidAppear(_ animated: Bool) {
        super.viewDidAppear(animated)
        appearanceEvents.append("didAppear")
    }
    override func viewWillDisappear(_ animated: Bool) {
        super.viewWillDisappear(animated)
        appearanceEvents.append("willDisappear")
    }
    override func viewDidDisappear(_ animated: Bool) {
        super.viewDidDisappear(animated)
        appearanceEvents.append("didDisappear")
    }
}

@MainActor
private final class PageLifecycleTestObject: ObservableObject {
    @Published var value = 0
}

@MainActor
private final class PageLifecycleTestPresenter: UIViewController {
    var onDidAppear: (() -> Void)?
    override func viewDidAppear(_ animated: Bool) {
        super.viewDidAppear(animated)
        let callback = onDidAppear
        onDidAppear = nil
        callback?()
    }
}

@MainActor
private final class PageLifecycleTestModalWrapper: UIViewController {
    var covering: UIViewController?
    var presenting = false
    var dismissing = false
    var movingTo = false
    var moving = false
    var coordinator: PageLifecycleTestCoordinator?
    override var presentedViewController: UIViewController? { covering ?? super.presentedViewController }
    override var isBeingPresented: Bool { presenting }
    override var isBeingDismissed: Bool { dismissing }
    override var isMovingToParent: Bool { movingTo }
    override var isMovingFromParent: Bool { moving }
    override var transitionCoordinator: UIViewControllerTransitionCoordinator? { coordinator }
}

@MainActor
private final class PageAlertTestPage: PageViewController {
    var covering: UIViewController?
    var coordinator: UIViewControllerTransitionCoordinator?
    var alerts: [UIViewController] = []
    override var presentedViewController: UIViewController? { covering ?? super.presentedViewController }
    override var transitionCoordinator: UIViewControllerTransitionCoordinator? { coordinator ?? super.transitionCoordinator }
    override func present(_ viewControllerToPresent: UIViewController, animated flag: Bool,
                          completion: (() -> Void)? = nil) {
        alerts.append(viewControllerToPresent)
        completion?()
    }
}

@MainActor
private final class PageDismissalTestModal: UIViewController {
    let coordinator = PageLifecycleTestCoordinator()
    private(set) var dismissalStarted = false
    private var completion: (() -> Void)?
    override var transitionCoordinator: UIViewControllerTransitionCoordinator? { dismissalStarted ? coordinator : nil }
    override func dismiss(animated flag: Bool, completion: (() -> Void)? = nil) {
        dismissalStarted = true
        self.completion = completion
    }
    func completeDismissal() {
        coordinator.complete(cancelled: false)
        let callback = completion
        completion = nil
        callback?()
    }
}

@MainActor
private final class PageLifecycleTestCoordinator: NSObject, UIViewControllerTransitionCoordinator {
    let isAnimated = true
    let presentationStyle = UIModalPresentationStyle.fullScreen
    let initiallyInteractive = true
    let isInterruptible = true
    let isInteractive = true
    private(set) var isCancelled = false
    let transitionDuration: TimeInterval = 0.22
    let percentComplete: CGFloat = 0
    let completionVelocity: CGFloat = 0
    let completionCurve = UIView.AnimationCurve.easeInOut
    let containerView = UIView()
    let targetTransform = CGAffineTransform.identity
    private var completions: [(UIViewControllerTransitionCoordinatorContext) -> Void] = []

    func complete(cancelled: Bool) {
        isCancelled = cancelled
        let callbacks = completions
        completions.removeAll()
        for completion in callbacks { completion(self) }
    }

    func viewController(forKey key: UITransitionContextViewControllerKey) -> UIViewController? { nil }
    func view(forKey key: UITransitionContextViewKey) -> UIView? { nil }

    func animate(alongsideTransition animation: ((UIViewControllerTransitionCoordinatorContext) -> Void)?,
                 completion: ((UIViewControllerTransitionCoordinatorContext) -> Void)?) -> Bool {
        animation?(self)
        if let completion { completions.append(completion) }
        return true
    }

    func animateAlongsideTransition(in view: UIView?,
                                   animation: ((UIViewControllerTransitionCoordinatorContext) -> Void)?,
                                   completion: ((UIViewControllerTransitionCoordinatorContext) -> Void)?) -> Bool {
        animate(alongsideTransition: animation, completion: completion)
    }

    func notifyWhenInteractionEnds(_ handler: @escaping (UIViewControllerTransitionCoordinatorContext) -> Void) {}
    func notifyWhenInteractionChanges(_ handler: @escaping (UIViewControllerTransitionCoordinatorContext) -> Void) {}
}
#endif
