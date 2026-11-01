#if os(iOS)
import XCTest
import UIKit
import MeowUI

@MainActor
final class SearchPageViewControllerTests: XCTestCase {
    func testCapsuleContainsOrderedControlsInTheAppOwnedHeader() async throws {
        let theme = PageLifecycleTestTheme()
        let root = PageLifecycleTestPage(themeProvider: theme)
        root.pageTitle = "上一页"
        let navigation = PageNavigationController(themeProvider: theme)
        navigation.setViewControllers([root], animated: false)
        let scene = UIApplication.shared.connectedScenes.compactMap { $0 as? UIWindowScene }.first
        let window = scene.map { UIWindow(windowScene: $0) }
            ?? UIWindow(frame: CGRect(x: 0, y: 0, width: 390, height: 844))
        window.rootViewController = navigation
        window.makeKeyAndVisible()
        defer {
            PageViewController.invalidatePages(in: navigation)
            window.isHidden = true
            window.rootViewController = nil
        }
        window.layoutIfNeeded()
        navigation.view.layoutIfNeeded()
        let ready = expectation(for: NSPredicate { _, _ in
            MainActor.assumeIsolated { root.appearanceEvents.contains("didAppear") }
        }, evaluatedWith: root)
        await fulfillment(of: [ready], timeout: 2)
        XCTAssertTrue(navigation.navigationBar.isHidden)
        let page = SearchPageViewController(themeProvider: theme, query: "歌曲")
        navigation.pushViewController(page, animated: false)
        navigation.view.layoutIfNeeded()
        page.view.layoutIfNeeded()
        let capsule = page.searchCapsule
        capsule.layoutIfNeeded()
        let submit = try submitButton(in: page)
        let icon = try XCTUnwrap(descendants(capsule).compactMap { $0 as? UIImageView }.first)
        XCTAssertTrue(page.searchField.isDescendant(of: capsule))
        XCTAssertTrue(submit.isDescendant(of: capsule))
        let inputFrame = page.searchField.convert(page.searchField.bounds, to: capsule)
        let iconFrame = icon.convert(icon.bounds, to: capsule)
        let buttonFrame = submit.convert(submit.bounds, to: capsule)
        XCTAssertGreaterThan(inputFrame.width, 80)
        XCTAssertGreaterThan(inputFrame.minX, iconFrame.maxX)
        XCTAssertGreaterThan(buttonFrame.minX, inputFrame.maxX)
        for control in [inputFrame, iconFrame, buttonFrame] {
            XCTAssertEqual(control.midY, capsule.bounds.midY, accuracy: 0.5)
            XCTAssertGreaterThanOrEqual(control.minY, -0.5)
            XCTAssertLessThanOrEqual(control.maxY, capsule.bounds.maxY + 0.5)
        }
        let frame = capsule.convert(capsule.bounds, to: page.pageHeader)
        XCTAssertEqual(frame.height, PageLayoutMetrics.searchControl, accuracy: 0.5)
        XCTAssertGreaterThan(frame.width, page.pageHeader.bounds.width * 0.6)
        XCTAssertGreaterThanOrEqual(frame.minX, 0)
        XCTAssertLessThanOrEqual(frame.maxX, page.pageHeader.bounds.width)
        XCTAssertEqual(frame.midY, page.pageHeader.contentFrame.midY, accuracy: 0.5)
        XCTAssertEqual(page.additionalSafeAreaInsets.top, 0)
        XCTAssertNil(page.navigationItem.titleView)
        XCTAssertNil(page.navigationItem.leftBarButtonItem)
        XCTAssertNil(page.navigationItem.rightBarButtonItem)
    }

    func testEditingAndBothSubmissionControlsUseTheSameTrimmedQuery() async throws {
        let page = SearchPageViewController(themeProvider: PageLifecycleTestTheme(), query: " \n ")
        var submitted: [String] = []
        page.onSubmit = { submitted.append($0) }
        let navigation = PageNavigationController(themeProvider: page.themeProvider)
        navigation.setViewControllers([page], animated: false)
        let scene = UIApplication.shared.connectedScenes.compactMap { $0 as? UIWindowScene }.first
        let window = scene.map { UIWindow(windowScene: $0) }
            ?? UIWindow(frame: CGRect(x: 0, y: 0, width: 390, height: 844))
        window.rootViewController = navigation
        window.makeKeyAndVisible()
        defer {
            PageViewController.invalidatePages(in: navigation)
            window.isHidden = true
            window.rootViewController = nil
        }
        window.layoutIfNeeded()
        navigation.view.layoutIfNeeded()
        let ready = expectation(for: NSPredicate { _, _ in
            MainActor.assumeIsolated { page.searchField.window != nil }
        }, evaluatedWith: page)
        await fulfillment(of: [ready], timeout: 2)
        let submit = try submitButton(in: page)
        XCTAssertFalse(submit.isEnabled)
        page.searchField.sendActions(for: .editingDidEndOnExit)
        XCTAssertTrue(submitted.isEmpty)
        page.query = "  歌曲  "
        XCTAssertTrue(submit.isEnabled)
        page.searchField.sendActions(for: .editingDidEndOnExit)
        XCTAssertEqual(submitted, ["歌曲"])
        page.searchField.text = "  歌手  "
        page.searchField.sendActions(for: .editingChanged)
        submit.sendActions(for: .touchUpInside)
        XCTAssertEqual(submitted, ["歌曲", "歌手"])
        page.searchField.text = ""
        page.searchField.sendActions(for: .editingChanged)
        XCTAssertFalse(submit.isEnabled)
        page.searchField.sendActions(for: .editingDidEndOnExit)
        XCTAssertEqual(submitted, ["歌曲", "歌手"])
    }

    private func submitButton(in page: SearchPageViewController) throws -> UIButton {
        let capsule = page.searchCapsule
        return try XCTUnwrap(descendants(capsule).compactMap { $0 as? UIButton }.first {
            $0.accessibilityIdentifier == "search.submit"
        })
    }

    private func descendants(_ view: UIView) -> [UIView] {
        view.subviews.flatMap { [$0] + descendants($0) }
    }

}
#endif
