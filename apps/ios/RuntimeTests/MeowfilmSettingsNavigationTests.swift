import XCTest
import UIKit
import MeowUI
@testable import MeowMusicFeature
@testable import Meowfilm

@MainActor
final class MeowfilmSettingsNavigationTests: XCTestCase {
    private var windows: [(UIWindow, UIWindow?)] = []

    override func tearDown() {
        for (window, previous) in windows.reversed() {
            if let root = window.rootViewController { PageViewController.invalidatePages(in: root) }
            window.isHidden = true; window.rootViewController = nil
            previous?.makeKey()
        }
        windows.removeAll()
        super.tearDown()
    }

    func testRootCategoriesPreferencesCacheAndResetAreIndependentOfMusicAvailability() throws {
        let page = MeowfilmSettingsViewController(themeProvider: MusicAppearancePreferencesStore(), resetUserData: {})
        _ = try host(page)
        let table = try XCTUnwrap(descendants(page.view).compactMap { $0 as? UITableView }.first)
        XCTAssertEqual(page.pageTitle, "设置")
        XCTAssertEqual(table.numberOfSections, 3)
        XCTAssertEqual((0..<3).map { table.numberOfRows(inSection: $0) }, [3, 2, 1])
        let paths = [IndexPath(row: 0, section: 0), IndexPath(row: 1, section: 0), IndexPath(row: 2, section: 0),
                     IndexPath(row: 0, section: 1), IndexPath(row: 1, section: 1), IndexPath(row: 0, section: 2)]
        XCTAssertEqual(paths.map { page.tableView(table, cellForRowAt: $0).accessibilityLabel },
                       ["音乐设置", "影视设置", "小说设置", "偏好设置", "缓存管理", "清空本机数据"])
        var selected: [String] = []
        page.openMusic = { selected.append("music") }; page.openFilm = { selected.append("film") }
        page.openNovel = { selected.append("novel") }
        page.openPreferences = { selected.append("preferences") }; page.openCache = { selected.append("cache") }
        for path in paths.dropLast() { page.tableView(table, didSelectRowAt: path) }
        XCTAssertEqual(selected, ["music", "film", "novel", "preferences", "cache"])
        attach(page, "settings-root-phone")
    }

    func testUserDataResetRequiresDestructiveConfirmationAndCancelDoesNotRequestIt() throws {
        var requests = 0
        let page = MeowfilmSettingsViewController(themeProvider: MusicAppearancePreferencesStore(),
                                                  resetUserData: { requests += 1 })
        _ = try host(page)
        waitUntil { page.canPresentPageAlert }
        page.confirmUserDataReset()
        let alert = try XCTUnwrap(page.presentedViewController as? UIAlertController)
        XCTAssertEqual(alert.title, "清空本机数据？")
        XCTAssertTrue(alert.message?.contains("云端数据和 App 外的原始文件不受影响") == true)
        XCTAssertTrue(alert.message?.contains("重新打开") == true)
        XCTAssertEqual(alert.actions.map(\.style), [.cancel, .destructive])
        XCTAssertEqual(alert.actions.map(\.title), ["取消", "清除并等待重启"])
        XCTAssertEqual(requests, 0, "Opening the confirmation is not consent")
        let dismissed = expectation(description: "cancelled")
        page.dismiss(animated: false) { dismissed.fulfill() }
        wait(for: [dismissed], timeout: 3)
        XCTAssertEqual(requests, 0)
    }

    func testConfirmedUserDataResetIsSingleFlightAndFailureAllowsRetry() throws {
        var requests = 0
        let requested = expectation(description: "confirmed request")
        let retried = expectation(description: "retry request")
        let page = MeowfilmSettingsViewController(themeProvider: MusicAppearancePreferencesStore(), resetUserData: {
            requests += 1
            if requests == 1 { requested.fulfill() } else { retried.fulfill() }
            throw MusicServiceError.storageFailed
        })
        page.loadViewIfNeeded()
        page.requestUserDataReset(); page.requestUserDataReset()
        wait(for: [requested], timeout: 3)
        XCTAssertEqual(requests, 1)
        page.requestUserDataReset()
        wait(for: [retried], timeout: 3)
        XCTAssertEqual(requests, 2)
    }

    func testPreferencesColorPageAndNativePickerCancelAndCommit() throws {
        let domain = "SettingsColorUI." + UUID().uuidString
        let defaults = try XCTUnwrap(UserDefaults(suiteName: domain))
        defer { defaults.removePersistentDomain(forName: domain) }
        let store = MusicAppearancePreferencesStore(defaults: defaults)
        store.selectFixedPalette(.preset(.mint))
        let preference = MusicAppearanceSettingsViewController(store: store)
        let navigation = PageNavigationController(themeProvider: store)
        navigation.setViewControllers([preference], animated: false)
        _ = try host(navigation)
        waitUntil { preference.isPageActive }
        XCTAssertEqual(preference.pageTitle, "偏好设置")
        preference.showColors()
        waitUntil { navigation.topViewController is MusicColorSettingsViewController }
        let page = try XCTUnwrap(navigation.topViewController as? MusicColorSettingsViewController)
        waitUntil { page.canPresentPageAlert }
        XCTAssertEqual(page.pageTitle, "配色设置")
        page.showCustomColor()
        let sheet = try XCTUnwrap(page.presentedViewController as? UINavigationController)
        let picker = try XCTUnwrap(sheet.topViewController as? MusicCustomColorPickerController)
        waitUntil { picker.view.window != nil && !sheet.isBeingPresented }
        picker.selectedColor = .red
        picker.colorPickerViewController(picker, didSelect: .red, continuously: true)
        XCTAssertEqual(store.pagePalette, .custom(0xff0000))
        XCTAssertEqual(MusicAppearancePreferencesStore(defaults: defaults).pagePalette, .preset(.mint))
        picker.cancel()
        waitUntil { page.presentedViewController == nil }
        XCTAssertEqual(store.pagePalette, .preset(.mint))
        page.showCustomColor()
        let secondSheet = try XCTUnwrap(page.presentedViewController as? UINavigationController)
        let second = try XCTUnwrap(secondSheet.topViewController as? MusicCustomColorPickerController)
        waitUntil { second.view.window != nil && !secondSheet.isBeingPresented }
        second.selectedColor = .blue
        second.colorPickerViewController(second, didSelect: .blue, continuously: false)
        second.colorPickerViewControllerDidFinish(second)
        waitUntil { page.presentedViewController == nil }
        XCTAssertEqual(store.pagePalette, .preset(.mint), "Closing the system picker is not explicit confirmation")
        XCTAssertEqual(MusicAppearancePreferencesStore(defaults: defaults).pagePalette, .preset(.mint))
        page.showCustomColor()
        let finalSheet = try XCTUnwrap(page.presentedViewController as? UINavigationController)
        let confirmed = try XCTUnwrap(finalSheet.topViewController as? MusicCustomColorPickerController)
        waitUntil { confirmed.view.window != nil && !finalSheet.isBeingPresented }
        confirmed.selectedColor = .blue
        confirmed.colorPickerViewController(confirmed, didSelect: .blue, continuously: false)
        confirmed.commit()
        waitUntil { page.presentedViewController == nil }
        XCTAssertEqual(store.pagePalette, .custom(0x0000ff))
        XCTAssertEqual(MusicAppearancePreferencesStore(defaults: defaults).pagePalette, .custom(0x0000ff))
        attach(page, "settings-custom-color-saved")
    }

    func testSettingsRootAndColorOptionsFitNarrowLargeTypeAndDarkAppearance() throws {
        let domain = "SettingsColorGeometry." + UUID().uuidString
        let defaults = try XCTUnwrap(UserDefaults(suiteName: domain))
        defer { defaults.removePersistentDomain(forName: domain) }
        let store = MusicAppearancePreferencesStore(defaults: defaults)
        store.set(\.colorMode, .fixed)
        for (width, dark, category) in [(CGFloat(320), false, UIContentSizeCategory.extraExtraExtraLarge),
                                        (CGFloat(430), true, UIContentSizeCategory.accessibilityExtraExtraExtraLarge)] {
            let page = MeowfilmSettingsViewController(themeProvider: store, resetUserData: {})
            let window = try host(page, width: width)
            window.overrideUserInterfaceStyle = dark ? .dark : .light
            page.traitOverrides.preferredContentSizeCategory = category
            page.view.layoutIfNeeded()
            let table = try XCTUnwrap(descendants(page.view).compactMap { $0 as? UITableView }.first)
            for section in 0..<table.numberOfSections {
                for row in 0..<table.numberOfRows(inSection: section) {
                    let path = IndexPath(row: row, section: section)
                    XCTAssertGreaterThanOrEqual(table.rectForRow(at: path).height, 44)
                }
            }
            attach(page, "settings-root-\(Int(width))-\(dark ? "dark" : "light")-large")
            let colors = MusicColorSettingsViewController(state: MusicAppearanceSettingsState(store: store))
            let colorWindow = try host(colors, width: width)
            colorWindow.overrideUserInterfaceStyle = dark ? .dark : .light
            colors.traitOverrides.preferredContentSizeCategory = category
            colors.view.layoutIfNeeded()
            XCTAssertEqual(colors.pageTitle, "配色设置")
            waitUntil { !self.descendants(colors.view).compactMap { $0 as? UIScrollView }.isEmpty }
            XCTAssertEqual(colors.view.bounds.width, width, accuracy: 1)
            attach(colors, "settings-colors-\(Int(width))-\(dark ? "dark" : "light")-large")
        }
    }

    private func host(_ page: UIViewController, width: CGFloat = 390) throws -> UIWindow {
        let scene = try XCTUnwrap(UIApplication.shared.connectedScenes.compactMap { $0 as? UIWindowScene }
            .first { $0.activationState == .foregroundActive })
        let previous = scene.windows.first { $0.isKeyWindow }
        let window = UIWindow(windowScene: scene)
        window.frame = scene.coordinateSpace.bounds
        window.windowLevel = .normal + 1
        let container = UIViewController()
        container.addChild(page); container.view.addSubview(page.view)
        page.view.translatesAutoresizingMaskIntoConstraints = false
        NSLayoutConstraint.activate([
            page.view.topAnchor.constraint(equalTo: container.view.topAnchor),
            page.view.leadingAnchor.constraint(equalTo: container.view.leadingAnchor),
            page.view.widthAnchor.constraint(equalToConstant: width),
            page.view.heightAnchor.constraint(equalTo: container.view.heightAnchor),
        ])
        page.didMove(toParent: container)
        window.rootViewController = container; window.makeKeyAndVisible()
        window.layoutIfNeeded()
        windows.append((window, previous))
        return window
    }
    private func waitUntil(_ condition: @escaping @MainActor () -> Bool) {
        let ready = expectation(for: NSPredicate { _, _ in MainActor.assumeIsolated { condition() } }, evaluatedWith: nil)
        wait(for: [ready], timeout: 4)
    }
    private func descendants(_ view: UIView) -> [UIView] { [view] + view.subviews.flatMap(descendants) }
    private func attach(_ page: UIViewController, _ name: String) {
        let image = UIGraphicsImageRenderer(bounds: page.view.bounds).image { _ in
            page.view.drawHierarchy(in: page.view.bounds, afterScreenUpdates: true)
        }
        let attachment = XCTAttachment(image: image)
        attachment.name = name; attachment.lifetime = .keepAlways; add(attachment)
    }
}
