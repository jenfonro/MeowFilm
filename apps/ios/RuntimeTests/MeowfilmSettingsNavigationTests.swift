import XCTest
import UIKit
import MeowUI
@testable import MeowFilmFeature
@testable import MeowMusicFeature
@testable import MeowNovelFeature
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

    func testSettingsPagesShareCardRowsAndUseTheirOwnVisibleHeaderTitle() throws {
        let store = MusicAppearancePreferencesStore()
        let root = MeowfilmSettingsViewController(themeProvider: store, resetUserData: {})
        let novel = NovelTablePage(themeProvider: store)
        novel.pageTitle = "小说设置"
        novel.rows = [.init(title: "书源管理", detail: "启用") {}, .init(title: "阅读排版") {}]
        _ = try host(root); _ = try host(novel)

        let rootTable = try XCTUnwrap(descendants(root.view).compactMap { $0 as? SettingsTableView }.first)
        let novelTable = try XCTUnwrap(descendants(novel.view).compactMap { $0 as? SettingsTableView }.first)
        XCTAssertEqual(root.pageHeader.title, "设置")
        XCTAssertEqual(novel.pageHeader.title, "小说设置")
        XCTAssertNil(root.navigationItem.title)
        XCTAssertNil(novel.navigationItem.title)
        XCTAssertEqual(rootTable.separatorStyle, .none)
        XCTAssertEqual(novelTable.separatorStyle, .none)

        let rootFirst = root.tableView(rootTable, cellForRowAt: IndexPath(row: 0, section: 0))
        let rootLast = root.tableView(rootTable, cellForRowAt: IndexPath(row: 2, section: 0))
        let novelFirst = novel.tableView(novelTable, cellForRowAt: IndexPath(row: 0, section: 0))
        let novelLast = novel.tableView(novelTable, cellForRowAt: IndexPath(row: 1, section: 0))
        XCTAssertTrue(rootFirst is SettingsTableCell)
        XCTAssertTrue(novelFirst is SettingsTableCell)
        XCTAssertEqual((rootFirst.contentConfiguration as? UIListContentConfiguration)?.text, "音乐设置")
        XCTAssertEqual((novelFirst.contentConfiguration as? UIListContentConfiguration)?.text, "书源管理")
        XCTAssertEqual((novelFirst.contentConfiguration as? UIListContentConfiguration)?.secondaryText, "启用")
        XCTAssertTrue((rootFirst.backgroundView as? SettingsRowBackgroundView)?.drawsSeparator == true)
        XCTAssertFalse((rootLast.backgroundView as? SettingsRowBackgroundView)?.drawsSeparator == true)
        XCTAssertTrue((novelFirst.backgroundView as? SettingsRowBackgroundView)?.drawsSeparator == true)
        XCTAssertFalse((novelLast.backgroundView as? SettingsRowBackgroundView)?.drawsSeparator == true)
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
            page.traitOverrides.userInterfaceStyle = dark ? .dark : .light
            waitUntil { page.theme.dark == dark }
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
            colors.traitOverrides.userInterfaceStyle = dark ? .dark : .light
            waitUntil { colors.theme.dark == dark }
            colors.traitOverrides.preferredContentSizeCategory = category
            colors.view.layoutIfNeeded()
            XCTAssertEqual(colors.pageTitle, "配色设置")
            waitUntil { !self.descendants(colors.view).compactMap { $0 as? UIScrollView }.isEmpty }
            XCTAssertEqual(colors.view.bounds.width, width, accuracy: 1)
            attach(colors, "settings-colors-\(Int(width))-\(dark ? "dark" : "light")-large")
        }
    }

    func testRootAndNovelSettingsUseStableColorsWhileTheirBackgroundStillFollowsThePalette() throws {
        let domain = "SettingsSemanticColors." + UUID().uuidString
        let defaults = try XCTUnwrap(UserDefaults(suiteName: domain))
        defer { defaults.removePersistentDomain(forName: domain) }
        let store = MusicAppearancePreferencesStore(defaults: defaults)
        let root = MeowfilmSettingsViewController(themeProvider: store, resetUserData: {})
        let novel = NovelTablePage(themeProvider: store)
        novel.pageTitle = "小说设置"
        novel.rows = [.init(title: "书源管理", detail: "启用") {}, .init(title: "删除", destructive: true) {}]
        _ = try host(root); _ = try host(novel)
        let table = try XCTUnwrap(descendants(root.view).compactMap { $0 as? UITableView }.first)
        let novelTable = try XCTUnwrap(descendants(novel.view).compactMap { $0 as? UITableView }.first)
        for dark in [false, true] {
            root.traitOverrides.userInterfaceStyle = dark ? .dark : .light
            novel.traitOverrides.userInterfaceStyle = dark ? .dark : .light
            waitUntil { root.theme.dark == dark && novel.theme.dark == dark }
            for palette in [PagePalette.preset(.coral), .custom(0xffff00)] {
                store.selectFixedPalette(palette)
                root.applyTheme(); novel.applyTheme()
                let colors = SettingsTheme(dark: dark)
                XCTAssertEqual(root.theme.palette, palette)
                XCTAssertEqual(root.pageHeader.background.theme.palette, palette)
                XCTAssertEqual(root.view.tintColor, UIColor(colors.accent))
                XCTAssertEqual(table.separatorColor, UIColor(colors.separator))
                let row = root.tableView(table, cellForRowAt: IndexPath(row: 0, section: 0))
                let content = try XCTUnwrap(row.contentConfiguration as? UIListContentConfiguration)
                XCTAssertEqual(content.textProperties.color, UIColor(colors.ink))
                let danger = root.tableView(table, cellForRowAt: IndexPath(row: 0, section: 2))
                XCTAssertEqual((danger.contentConfiguration as? UIListContentConfiguration)?.textProperties.color,
                    UIColor(colors.danger))
                let novelRow = novel.tableView(novelTable, cellForRowAt: IndexPath(row: 0, section: 0))
                let novelContent = try XCTUnwrap(novelRow.contentConfiguration as? UIListContentConfiguration)
                XCTAssertEqual(novelContent.textProperties.color, UIColor(colors.ink))
                XCTAssertEqual(novelContent.secondaryTextProperties.color, UIColor(colors.muted))
                XCTAssertEqual(novel.view.tintColor, UIColor(colors.accent))
                let novelDanger = novel.tableView(novelTable, cellForRowAt: IndexPath(row: 1, section: 0))
                XCTAssertEqual((novelDanger.contentConfiguration as? UIListContentConfiguration)?.textProperties.color,
                    UIColor(colors.danger))
            }
            attach(root, "settings-semantic-root-\(dark ? "dark" : "light")")
            attach(novel, "settings-semantic-novel-\(dark ? "dark" : "light")")
        }
    }

    func testFilmControlsKeepTheirDraftAndReadableSemanticColorsAcrossAppearanceChanges() throws {
        let store = MusicAppearancePreferencesStore()
        let page = FilmSettingsListController(themeProvider: store)
        page.pageTitle = "规则设置"
        let field = filmTextField("查询词", id: "semantic.query", value: "保留草稿")
        let pattern = filmTextView("pattern", id: "semantic.pattern", value: "(第\\d+集)")
        let toggle = UISwitch(); toggle.isOn = true
        let stack = UIStackView(arrangedSubviews: [field, pattern, toggle])
        stack.axis = .vertical; stack.spacing = 12
        page.sections = [.init(title: "测试", rows: [
            .init(title: "", id: "semantic.fields", content: stack),
            .init(title: "选中", id: "semantic.selected", selected: true),
            .init(title: "删除", id: "semantic.delete", destructive: true),
        ])]
        _ = try host(page)
        for dark in [false, true] {
            page.traitOverrides.userInterfaceStyle = dark ? .dark : .light
            waitUntil { page.theme.dark == dark }
            page.applyTheme(); page.view.layoutIfNeeded()
            _ = page.tableView(page.table, cellForRowAt: IndexPath(row: 0, section: 0))
            let colors = SettingsTheme(dark: dark)
            XCTAssertEqual(field.text, "保留草稿")
            XCTAssertEqual(pattern.text, "(第\\d+集)")
            XCTAssertEqual(field.textColor, UIColor(colors.ink))
            XCTAssertEqual(field.backgroundColor, UIColor(colors.input))
            XCTAssertEqual(field.tintColor, UIColor(colors.accent))
            XCTAssertEqual(field.attributedPlaceholder?.attribute(.foregroundColor, at: 0, effectiveRange: nil) as? UIColor,
                UIColor(colors.muted))
            XCTAssertEqual(pattern.textColor, UIColor(colors.ink))
            XCTAssertEqual(pattern.backgroundColor, UIColor(colors.input))
            XCTAssertEqual(toggle.onTintColor, UIColor(colors.accent))
            XCTAssertEqual(page.view.tintColor, UIColor(colors.accent))
            let selected = page.tableView(page.table, cellForRowAt: IndexPath(row: 1, section: 0))
            XCTAssertEqual(selected.accessoryType, .checkmark)
            let danger = page.tableView(page.table, cellForRowAt: IndexPath(row: 2, section: 0))
            XCTAssertEqual((danger.contentConfiguration as? UIListContentConfiguration)?.textProperties.color,
                UIColor(colors.danger))
            page.table.reloadData(); page.view.layoutIfNeeded()
            attach(page, "settings-semantic-film-fields-\(dark ? "dark" : "light")")
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
