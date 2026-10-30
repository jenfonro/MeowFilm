#if os(iOS)
import XCTest
import UIKit
import MeowUI

@MainActor
final class NavigationTitleViewTests: XCTestCase {
    func testSearchCapsuleKeepsItsHeightBetweenActiveSideButtons() async throws {
        let page = PageViewController(themeProvider: TestPageTheme())
        let menu = UIButton(type: .system), feature = UIButton(type: .system)
        menu.setImage(PageIcon.image(.menu), for: .normal)
        feature.setImage(PageIcon.image(.recognize), for: .normal)
        for button in [menu, feature] {
            button.translatesAutoresizingMaskIntoConstraints = false
            NSLayoutConstraint.activate([
                button.widthAnchor.constraint(equalToConstant: 44),
                button.heightAnchor.constraint(equalToConstant: 44),
            ])
        }
        let search = UIButton(type: .system)
        var configuration = UIButton.Configuration.plain()
        configuration.title = "搜索歌曲、歌手、歌单"
        configuration.image = PageIcon.image(.search, size: 18)
        configuration.imagePadding = 8
        configuration.contentInsets = NSDirectionalEdgeInsets(top: 0, leading: 14, bottom: 0, trailing: 14)
        search.configuration = configuration
        search.layer.cornerRadius = PageLayoutMetrics.searchControl / 2
        search.setContentCompressionResistancePriority(.defaultLow, for: .horizontal)
        let capsule = NavigationTitleView(content: search)
        page.navigationItem.titleView = capsule
        page.navigationItem.leftBarButtonItem = UIBarButtonItem(customView: menu)
        page.navigationItem.rightBarButtonItem = UIBarButtonItem(customView: feature)
        if #available(iOS 26.0, *) {
            page.navigationItem.leftBarButtonItem?.hidesSharedBackground = true
            page.navigationItem.rightBarButtonItem?.hidesSharedBackground = true
        }
        var menuTaps = 0, searchTaps = 0
        menu.addAction(UIAction { _ in menuTaps += 1 }, for: .touchUpInside)
        search.addAction(UIAction { _ in searchTaps += 1 }, for: .touchUpInside)
        let navigation = PageNavigationController(themeProvider: page.themeProvider)
        navigation.setViewControllers([page], animated: false)
        let scene = UIApplication.shared.connectedScenes.compactMap { $0 as? UIWindowScene }.first
        let window = scene.map { UIWindow(windowScene: $0) }
            ?? UIWindow(frame: CGRect(x: 0, y: 0, width: 430, height: 932))
        window.rootViewController = navigation
        window.makeKeyAndVisible()
        defer {
            PageViewController.invalidatePages(in: navigation)
            window.isHidden = true
            window.rootViewController = nil
        }
        let ready = expectation(for: NSPredicate { _, _ in
            MainActor.assumeIsolated { search.window != nil }
        }, evaluatedWith: page)
        await fulfillment(of: [ready], timeout: 2)
        for width in [CGFloat(320), 430] {
            window.frame.size.width = width
            window.layoutIfNeeded()
            navigation.view.layoutIfNeeded()
            capsule.layoutIfNeeded()
            let frame = capsule.convert(capsule.bounds, to: navigation.navigationBar)
            let menuFrame = menu.convert(menu.bounds, to: navigation.navigationBar)
            let featureFrame = feature.convert(feature.bounds, to: navigation.navigationBar)
            XCTAssertEqual(frame.height, PageLayoutMetrics.searchControl, accuracy: 0.5)
            XCTAssertEqual(search.bounds.height, frame.height, accuracy: 0.5)
            XCTAssertGreaterThan(frame.width, 150)
            XCTAssertGreaterThanOrEqual(frame.minX, menuFrame.maxX)
            XCTAssertLessThanOrEqual(frame.maxX, featureFrame.minX)
            XCTAssertEqual(feature.bounds.size, CGSize(width: 44, height: 44))
            XCTAssertTrue(menu.isEnabled)
            XCTAssertTrue(search.isEnabled)
            XCTAssertTrue(feature.isEnabled)
            XCTAssertEqual(feature.alpha, 1)
        }
        menu.sendActions(for: .touchUpInside)
        search.sendActions(for: .touchUpInside)
        feature.sendActions(for: .touchUpInside)
        XCTAssertEqual(menuTaps, 1)
        XCTAssertEqual(searchTaps, 1)
    }
}
#endif
