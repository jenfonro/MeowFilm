import XCTest
import UIKit
import MeowUI
import MeowStorage
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
        try assertSettingsFirstElement(root, table: rootTable)
        try assertSettingsFirstElement(novel, table: novelTable)
    }

    func testSettingsFirstContentMatchesSearchPurificationForTitledUntitledAndNestedPages() async throws {
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent(
            "SettingsTopSpacing." + UUID().uuidString, isDirectory: true)
        let rules = FilmRuleService(settingsDocument: FilmSettingsDocument(
            block: .rules, storage: .plain { try DocumentStore(baseURL: directory, namespace: FilmSettingsBlock.rules.namespace) }
        ), request: { _, _ in
            XCTFail("Measuring native settings must not execute rules")
            throw FilmRuleServiceError.closed
        })
        let owner = FilmRulesDraft(service: rules)
        try await owner.load()
        let panModel = PanAccountsModel(service: PanAccountService(document: FilmSettingsDocument(
            block: .panAccounts, storage: .plain { try DocumentStore(baseURL: directory, namespace: FilmSettingsBlock.panAccounts.namespace) }
        ), request: { _, _, _ in
            XCTFail("Measuring the account list must not validate accounts")
            throw FilmRuleServiceError.closed
        }))
        addTeardownBlock { @MainActor in
            panModel.close(); await panModel.closing?.value
            await rules.close()
            try? FileManager.default.removeItem(at: directory)
        }
        let domain = "SettingsTopTheme." + UUID().uuidString
        let defaults = try XCTUnwrap(UserDefaults(suiteName: domain))
        defer { defaults.removePersistentDomain(forName: domain) }
        let store = MusicAppearancePreferencesStore(defaults: defaults)
        let reference = FilmRulePageViewController(owner: owner, group: .search, themeProvider: store)
        let root = MeowfilmSettingsViewController(themeProvider: store)
        let novel = NovelTablePage(themeProvider: store)
        novel.pageTitle = "小说设置"; novel.rows = [.init(title: "书源管理") {}]
        let film = FilmSettingsListController(themeProvider: store)
        film.pageTitle = "影视设置"
        film.sections = [.init(title: "", rows: [.init(title: "播放器设置", id: "spacing.film")])]
        let titled = FilmSettingsListController(themeProvider: store)
        titled.pageTitle = "播放控制"
        titled.sections = [.init(title: "播放选项", rows: [.init(title: "倍速", id: "spacing.player")]),
                          .init(title: "画面", rows: [.init(title: "比例", id: "spacing.video")])]
        let pan = PanAccountsViewController(model: panModel, themeProvider: store)
        let parent = PageViewController(themeProvider: store)
        parent.pageTitle = "智能播放设置"
        let nested = SettingsNestedFilmPage(themeProvider: store)
        nested.sections = [.init(title: "", rows: [.init(title: "偏好设置", id: "spacing.nested")])]
        parent.loadViewIfNeeded(); parent.addChild(nested)
        parent.view.addSubview(nested.view); nested.view.translatesAutoresizingMaskIntoConstraints = false
        NSLayoutConstraint.activate([
            nested.view.topAnchor.constraint(equalTo: parent.contentTopAnchor),
            nested.view.leadingAnchor.constraint(equalTo: parent.view.safeAreaLayoutGuide.leadingAnchor),
            nested.view.trailingAnchor.constraint(equalTo: parent.view.safeAreaLayoutGuide.trailingAnchor),
            nested.view.bottomAnchor.constraint(equalTo: parent.view.safeAreaLayoutGuide.bottomAnchor),
        ])
        nested.didMove(toParent: parent)
        let pages: [PageViewController] = [reference, root, novel, film, titled, pan, parent]
        for page in pages { _ = try host(page) }
        let environments: [(CGFloat, UIContentSizeCategory)] = [CGFloat(320), 390, 430].flatMap { viewport in
            [UIContentSizeCategory.large, .accessibilityExtraExtraExtraLarge].map { (viewport, $0) }
        }
        for (viewport, category) in environments {
            for dark in [false, true] {
                for page in pages {
                    let width = try XCTUnwrap(page.view.constraints.first {
                        $0.firstAttribute == .width && $0.secondItem == nil
                    })
                    width.constant = viewport
                    page.traitOverrides.preferredContentSizeCategory = category
                    page.traitOverrides.userInterfaceStyle = dark ? .dark : .light
                    page.applyTheme(); page.view.window?.layoutIfNeeded(); page.view.layoutIfNeeded()
                }
                let card = try XCTUnwrap(reference.editors.first)
                let gap = card.convert(card.bounds, to: reference.view).minY -
                    reference.pageHeader.convert(reference.pageHeader.bounds, to: reference.view).maxY
                XCTAssertEqual(SettingsVisualMetrics.contentTopSpacing, 20)
                XCTAssertEqual(gap, SettingsVisualMetrics.contentTopSpacing, accuracy: 1)
                XCTAssertEqual(reference.scrollView.contentInset.top, 0, accuracy: 0.5)
                XCTAssertEqual(reference.scrollView.adjustedContentInset.top, 0, accuracy: 0.5)
                XCTAssertEqual(reference.scrollView.contentOffset.y, 0, accuracy: 0.5)
                for page in pages.dropFirst() {
                    let table = try XCTUnwrap(descendants(page.view).compactMap { $0 as? SettingsTableView }.first)
                    table.reloadData(); page.view.layoutIfNeeded()
                    try assertSettingsFirstElement(page, table: table, title: page === titled ? "播放选项" : nil,
                        referenceGap: gap)
                }
                XCTAssertEqual(titled.table.heightForHeader(in: 1, title: "画面"), UITableView.automaticDimension)
                XCTAssertNil(titled.table.viewForHeader(in: 1, title: "画面", theme: titled.theme))
                XCTAssertEqual(nested.pageHeader.bounds.height, 0, "The embedded menu does not add another page header")
                attach(titled, "settings-top-titled-\(Int(viewport))-\(category.rawValue)-\(dark)")
                attach(pan, "settings-top-pan-\(Int(viewport))-\(category.rawValue)-\(dark)")
            }
        }
        film.sections = [.init(title: "", rows: (0..<60).map {
            .init(title: "设置 \($0)", id: "spacing.row.\($0)")
        })]
        film.render(); film.view.layoutIfNeeded()
        film.table.setContentOffset(CGPoint(x: 0, y: 240), animated: false)
        film.table.layoutIfNeeded()
        let offset = film.table.contentOffset
        let firstVisible = try XCTUnwrap(film.table.indexPathsForVisibleRows?.first)
        let visibleTop = film.table.rectForRow(at: firstVisible).minY - offset.y
        film.render(); film.view.layoutIfNeeded()
        XCTAssertEqual(film.table.rectForRow(at: firstVisible).minY - film.table.contentOffset.y,
            visibleTop, accuracy: 1,
            "Reloading estimated row heights must preserve the visible content anchor")
        let renderedOffset = film.table.contentOffset
        let renderedCell = try XCTUnwrap(film.table.cellForRow(at: firstVisible))
        film.traitOverrides.userInterfaceStyle = .light
        film.applyTheme(); film.view.layoutIfNeeded()
        XCTAssertTrue(film.table.cellForRow(at: firstVisible) === renderedCell)
        XCTAssertEqual((renderedCell.contentConfiguration as? UIListContentConfiguration)?.textProperties.color,
            UIColor(film.theme.settings.ink))
        XCTAssertEqual(film.table.contentOffset.y, renderedOffset.y, accuracy: 1,
            "Theme updates must not change the user's scroll position")
        XCTAssertEqual(film.table.rectForRow(at: firstVisible).minY - film.table.contentOffset.y,
            visibleTop, accuracy: 1, "Header policy must preserve the visible content through render and theme changes")
    }

    func testNonSettingsBorrowerKeepsNativeGroupedFirstHeaderAndInsets() throws {
        let page = SettingsBorrowedFilmPage(themeProvider: MusicAppearancePreferencesStore())
        page.pageTitle = "原始目录"
        page.sections = [.init(title: "", rows: [.init(title: "目录", id: "spacing.directory")])]
        _ = try host(page)
        let system = UITableView(frame: .zero, style: .insetGrouped)
        system.dataSource = page
        system.rowHeight = UITableView.automaticDimension; system.estimatedRowHeight = 60
        page.view.insertSubview(system, at: 0); system.alpha = 0
        system.translatesAutoresizingMaskIntoConstraints = false
        NSLayoutConstraint.activate([
            system.topAnchor.constraint(equalTo: page.contentTopAnchor),
            system.leadingAnchor.constraint(equalTo: page.view.safeAreaLayoutGuide.leadingAnchor),
            system.trailingAnchor.constraint(equalTo: page.view.safeAreaLayoutGuide.trailingAnchor),
            system.bottomAnchor.constraint(equalTo: page.view.safeAreaLayoutGuide.bottomAnchor),
        ])
        page.view.layoutIfNeeded(); system.layoutIfNeeded()
        XCTAssertEqual(page.table.contentInsetAdjustmentBehavior, system.contentInsetAdjustmentBehavior)
        XCTAssertEqual(page.table.contentInset.top, system.contentInset.top, accuracy: 0.5)
        XCTAssertEqual(page.table.adjustedContentInset.top, system.adjustedContentInset.top, accuracy: 0.5)
        XCTAssertEqual(page.table.contentOffset.y, system.contentOffset.y, accuracy: 0.5)
        XCTAssertEqual(page.table.rectForHeader(inSection: 0).height, system.rectForHeader(inSection: 0).height, accuracy: 1)
        XCTAssertEqual(page.table.rectForRow(at: IndexPath(row: 0, section: 0)).minY,
            system.rectForRow(at: IndexPath(row: 0, section: 0)).minY, accuracy: 1)
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
            try assertSettingsFirstElement(page, table: try XCTUnwrap(table as? SettingsTableView))
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
    private func assertSettingsFirstElement(
        _ page: PageViewController, table: SettingsTableView, title: String? = nil,
        referenceGap: CGFloat = SettingsVisualMetrics.contentTopSpacing
    ) throws {
        let first: UIView
        if let title {
            let labels = descendants(table).compactMap { $0 as? UILabel }.filter {
                $0.text == title && !$0.isHidden && $0.alpha > 0
            }
            XCTAssertEqual(labels.count, 1, "The custom first header must not duplicate UIKit's title")
            let label = try XCTUnwrap(labels.first)
            XCTAssertEqual(label.font, UIFont.preferredFont(forTextStyle: .subheadline, compatibleWith: table.traitCollection))
            XCTAssertEqual(label.textColor, UIColor(page.theme.settings.muted))
            first = label
        } else {
            let cell = try XCTUnwrap(table.cellForRow(at: IndexPath(row: 0, section: 0)))
            first = try XCTUnwrap(cell.backgroundView as? SettingsRowBackgroundView)
            XCTAssertEqual(table.rectForHeader(inSection: 0).height, SettingsVisualMetrics.contentTopSpacing, accuracy: 0.5)
        }
        let headerBottom = page.pageHeader.convert(page.pageHeader.bounds, to: page.view).maxY
        let frame = first.convert(first.bounds, to: page.view)
        XCTAssertEqual(frame.minY - headerBottom, referenceGap, accuracy: 1,
            "Measure the first visible card or section title in the page-header coordinate space")
        XCTAssertEqual(table.contentInset.top, 0, accuracy: 0.5)
        XCTAssertEqual(table.adjustedContentInset.top, 0, accuracy: 0.5)
        XCTAssertEqual(table.contentOffset.y, 0, accuracy: 0.5)
        let metrics = XCTAttachment(string: """
            page=\(page.pageTitle ?? "") width=\(page.view.bounds.width) category=\(page.traitCollection.preferredContentSizeCategory.rawValue)
            headerBottom=\(headerBottom) firstFrame=\(frame) firstHeader=\(table.rectForHeader(inSection: 0))
            offset=\(table.contentOffset) inset=\(table.contentInset) adjustedInset=\(table.adjustedContentInset)
            """)
        metrics.name = "settings-native-top-\(page.pageTitle ?? "")-\(Int(page.view.bounds.width))"
        metrics.lifetime = .keepAlways; add(metrics)
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

@MainActor
private final class SettingsNestedFilmPage: FilmSettingsListController {
    override var usesPageHeader: Bool { false }
}

@MainActor
private final class SettingsBorrowedFilmPage: FilmSettingsListController {
    override var usesSettingsColors: Bool { false }
}
