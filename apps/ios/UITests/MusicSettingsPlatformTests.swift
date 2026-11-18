#if os(iOS)
import XCTest
import UIKit
import SwiftUI
import Combine
import MeowContracts
import MeowPlayback
import MeowStorage
@testable import MeowFilmFeature
@testable import MeowMusicFeature
@testable import MeowUI

@MainActor
final class MusicSettingsPlatformTests: XCTestCase {
    func testSettingsRowsShareUIKitTypeGeometryAndMatchSearchPurificationTopSpacing() throws {
        let domain = "MusicSettingsRowGeometry." + UUID().uuidString
        let defaults = try XCTUnwrap(UserDefaults(suiteName: domain))
        defer { defaults.removePersistentDomain(forName: domain) }
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent(domain, isDirectory: true)
        let rules = FilmRuleService(settingsDocument: FilmSettingsDocument(
            block: .rules, storage: .plain { try DocumentStore(baseURL: directory, namespace: FilmSettingsBlock.rules.namespace) }
        ), request: { _, _ in
            XCTFail("Measuring settings geometry must not execute rules")
            throw FilmRuleServiceError.closed
        })
        let owner = FilmRulesDraft(service: rules)
        let loaded = expectation(description: "native reference rules loaded")
        Task {
            do { try await owner.load() }
            catch { XCTFail("Loading synthetic reference rules failed: \(error)") }
            loaded.fulfill()
        }
        wait(for: [loaded], timeout: 3)
        defer {
            let closed = expectation(description: "reference rules closed")
            Task { await rules.close(); closed.fulfill() }
            wait(for: [closed], timeout: 3)
            try? FileManager.default.removeItem(at: directory)
        }
        let appearance = MusicAppearancePreferencesStore(defaults: defaults)
        let page = HostingPageViewController(themeProvider: appearance,
            content: MusicSettingsContent(theme: PageTheme(palette: .sky)))
        page.pageTitle = "音乐设置"
        try withSettingsComponent(page) {
            let window = try XCTUnwrap(page.view.window)
            // Keep the system's horizontal List margins as an independent reference.
            let nativeReference = UIHostingController(rootView: List { Text("平台账号") }.listStyle(.insetGrouped))
            let container = try XCTUnwrap(page.parent)
            container.addChild(nativeReference)
            container.view.insertSubview(nativeReference.view, at: 0)
            nativeReference.view.alpha = 0
            nativeReference.view.translatesAutoresizingMaskIntoConstraints = false
            NSLayoutConstraint.activate([
                nativeReference.view.topAnchor.constraint(equalTo: page.contentTopAnchor),
                nativeReference.view.leadingAnchor.constraint(equalTo: page.view.leadingAnchor),
                nativeReference.view.trailingAnchor.constraint(equalTo: page.view.trailingAnchor),
                nativeReference.view.bottomAnchor.constraint(equalTo: page.view.bottomAnchor)
            ])
            nativeReference.didMove(toParent: container)
            @MainActor func nativeList() -> UICollectionView? {
                settingsDescendants(nativeReference.view).compactMap { $0 as? UICollectionView }.first
            }
            let reference = FilmRulePageViewController(owner: owner, group: .search, themeProvider: appearance)
            page.addChild(reference)
            page.view.insertSubview(reference.view, at: 0)
            reference.view.alpha = 0
            reference.view.translatesAutoresizingMaskIntoConstraints = false
            NSLayoutConstraint.activate([
                reference.view.topAnchor.constraint(equalTo: page.contentTopAnchor),
                reference.view.leadingAnchor.constraint(equalTo: page.view.safeAreaLayoutGuide.leadingAnchor),
                reference.view.trailingAnchor.constraint(equalTo: page.view.safeAreaLayoutGuide.trailingAnchor),
                reference.view.bottomAnchor.constraint(equalTo: page.view.bottomAnchor)
            ])
            reference.didMove(toParent: page)
            let width = try XCTUnwrap(page.view.constraints.first {
                $0.firstAttribute == .width && $0.secondItem == nil
            })
            @MainActor func visibleRows() -> [SettingsRowBackgroundView] {
                settingsDescendants(page.view).compactMap { $0 as? UICollectionView }
                    .flatMap(\.visibleCells).flatMap { settingsDescendants($0) }
                    .compactMap { $0 as? SettingsRowBackgroundView }
                    .filter { $0.bounds.width > 0 }
            }
            let environments: [(CGFloat, UIContentSizeCategory)] = [CGFloat(320), 390, 430].flatMap { viewport in
                [UIContentSizeCategory.large, .accessibilityExtraExtraExtraLarge].map { (viewport, $0) }
            }
            for (viewport, category) in environments {
                page.traitOverrides.preferredContentSizeCategory = category
                nativeReference.traitOverrides.preferredContentSizeCategory = category
                width.constant = viewport
                for dark in [false, true] {
                    page.traitOverrides.userInterfaceStyle = dark ? .dark : .light
                    nativeReference.traitOverrides.userInterfaceStyle = dark ? .dark : .light
                    page.setContent(MusicSettingsContent(theme: PageTheme(palette: .sky, dark: dark)))
                    reference.applyTheme()
                    waitForSettingsComponent("settings rows laid out for current type and theme") {
                        window.layoutIfNeeded(); page.view.layoutIfNeeded(); nativeReference.view.layoutIfNeeded()
                        return !visibleRows().isEmpty &&
                            nativeList()?.layoutAttributesForItem(at: IndexPath(item: 0, section: 0)) != nil &&
                            page.traitCollection.preferredContentSizeCategory == category
                    }
                    let rows = visibleRows()
                        .sorted { $0.convert($0.bounds, to: page.view).minY < $1.convert($1.bounds, to: page.view).minY }
                    let first = try XCTUnwrap(rows.first)
                    let frame = first.convert(first.bounds, to: page.view)
                    let systemList = try XCTUnwrap(nativeList())
                    let systemRow = try XCTUnwrap(systemList.layoutAttributesForItem(at: IndexPath(item: 0, section: 0)))
                    let systemFrame = systemList.convert(systemRow.frame, to: page.view)
                    XCTAssertEqual(frame.minX, systemFrame.minX, accuracy: 0.5)
                    XCTAssertEqual(frame.maxX, systemFrame.maxX, accuracy: 0.5)
                    let headerBottom = page.pageHeader.convert(page.pageHeader.bounds, to: page.view).maxY
                    XCTAssertGreaterThanOrEqual(frame.minY, headerBottom)
                    let referenceCard = try XCTUnwrap(reference.editors.first)
                    let referenceFrame = referenceCard.convert(referenceCard.bounds, to: page.view)
                    XCTAssertEqual(SettingsVisualMetrics.contentTopSpacing, 20)
                    XCTAssertEqual(referenceFrame.minY - headerBottom, SettingsVisualMetrics.contentTopSpacing, accuracy: 1,
                        "Measure the search-purification card's outer boundary, not its inner title padding")
                    XCTAssertEqual(frame.minY - headerBottom, referenceFrame.minY - headerBottom, accuracy: 1,
                        "The SwiftUI list must use the actual search-purification first-card gap")
                    XCTAssertEqual(reference.scrollView.contentInset.top, 0, accuracy: 0.5)
                    XCTAssertEqual(reference.scrollView.adjustedContentInset.top, 0, accuracy: 0.5)
                    XCTAssertEqual(reference.scrollView.contentOffset.y, 0, accuracy: 0.5)
                    let list = try XCTUnwrap(settingsDescendants(page.view).compactMap { $0 as? UICollectionView }.first)
                    XCTAssertEqual(list.contentOffset.y, -list.adjustedContentInset.top, accuracy: 1)
                    let metrics = XCTAttachment(string: """
                        width=\(viewport) category=\(category.rawValue) dark=\(dark)
                        headerBottom=\(headerBottom) cardTop=\(frame.minY) referenceCardTop=\(referenceFrame.minY)
                        card=\(frame) nativeGroupedRow=\(systemFrame)
                        listOffset=\(list.contentOffset) inset=\(list.contentInset) adjustedInset=\(list.adjustedContentInset)
                        """)
                    metrics.name = "settings-top-metrics-\(Int(viewport))-\(category.rawValue)-\(dark)"
                    metrics.lifetime = .keepAlways; add(metrics)
                    let title = UILabel()
                    title.font = .preferredFont(forTextStyle: .body, compatibleWith: page.traitCollection)
                    title.numberOfLines = 0; title.text = "平台账号"
                    let labelWidth = frame.width - SettingsVisualMetrics.rowHorizontalPadding * 2 - 24 - 12 - 16 - 12
                    let bodyHeight = title.sizeThatFits(CGSize(width: labelWidth, height: .greatestFiniteMagnitude)).height
                    XCTAssertEqual(frame.height, max(SettingsVisualMetrics.rowHeight,
                        ceil(bodyHeight) + SettingsVisualMetrics.rowVerticalPadding * 2), accuracy: 1)
                    for row in rows {
                        let bounds = row.convert(row.bounds, to: page.view)
                        XCTAssertGreaterThanOrEqual(bounds.minX, 0)
                        XCTAssertLessThanOrEqual(bounds.maxX, page.view.bounds.width)
                    }
                    let screenshot = XCTAttachment(image: UIGraphicsImageRenderer(bounds: page.view.bounds).image { _ in
                        XCTAssertTrue(page.view.drawHierarchy(in: page.view.bounds, afterScreenUpdates: true))
                    })
                    screenshot.name = "settings-shared-type-\(Int(viewport))-\(category.rawValue)-\(dark ? "dark" : "light")"
                    screenshot.lifetime = .keepAlways; add(screenshot)
                }
            }
        }
    }

    func testSettingsListFirstSectionTitleUsesSharedTopMargin() throws {
        let appearance = MusicAppearancePreferencesStore()
        let page = HostingPageViewController(themeProvider: appearance,
            content: SettingsTitledListGeometryFixture(theme: PageTheme(palette: .sky)))
        page.pageTitle = "歌词／封面设置"
        try withSettingsComponent(page) {
            let window = try XCTUnwrap(page.view.window)
            let width = try XCTUnwrap(page.view.constraints.first {
                $0.firstAttribute == .width && $0.secondItem == nil
            })
            let environments: [(CGFloat, UIContentSizeCategory)] = [CGFloat(320), 390, 430].flatMap { viewport in
                [UIContentSizeCategory.large, .accessibilityExtraExtraExtraLarge].map { (viewport, $0) }
            }
            for (viewport, category) in environments {
                width.constant = viewport
                page.traitOverrides.preferredContentSizeCategory = category
                for dark in [false, true] {
                    page.traitOverrides.userInterfaceStyle = dark ? .dark : .light
                    page.setContent(SettingsTitledListGeometryFixture(theme: PageTheme(palette: .sky, dark: dark)))
                    waitForSettingsComponent("titled list laid out") {
                        window.layoutIfNeeded(); page.view.layoutIfNeeded()
                        return self.settingsDescendants(page.view).compactMap { $0 as? UICollectionView }
                            .contains { $0.numberOfSections == 2 && $0.numberOfItems(inSection: 0) == 1 } &&
                            self.settingsDescendants(page.view).contains {
                                $0.accessibilityIdentifier == "settings.geometry.titleBounds" && !$0.bounds.isEmpty
                            }
                    }
                    let list = try XCTUnwrap(settingsDescendants(page.view).compactMap { $0 as? UICollectionView }.first)
                    let title = try XCTUnwrap(settingsDescendants(page.view).first {
                        $0.accessibilityIdentifier == "settings.geometry.titleBounds"
                    })
                    // Measure the title itself in the same UIKit space as the rows and page header.
                    let titleFrame = title.convert(title.bounds, to: page.view)
                    let sectionHeader = try XCTUnwrap(list.layoutAttributesForSupplementaryElement(
                        ofKind: UICollectionView.elementKindSectionHeader, at: IndexPath(item: 0, section: 0)))
                    let sectionHeaderFrame = list.convert(sectionHeader.frame, to: page.view)
                    let headerBottom = page.pageHeader.convert(page.pageHeader.bounds, to: page.view).maxY
                    XCTAssertEqual(SettingsVisualMetrics.contentTopSpacing, 20)
                    XCTAssertEqual(titleFrame.minY - headerBottom, SettingsVisualMetrics.contentTopSpacing, accuracy: 1,
                        "The actual first section title, not the scroll container, starts at the shared margin")
                    XCTAssertEqual(titleFrame.minY, sectionHeaderFrame.minY, accuracy: 1,
                        "The section header must not add another inset above the visible title")
                    XCTAssertGreaterThan(titleFrame.height, 0)
                    XCTAssertGreaterThanOrEqual(titleFrame.minX, sectionHeaderFrame.minX)
                    XCTAssertLessThanOrEqual(titleFrame.maxX, sectionHeaderFrame.maxX + 0.5)
                    let first = try XCTUnwrap(list.layoutAttributesForItem(at: IndexPath(item: 0, section: 0)))
                    let second = try XCTUnwrap(list.layoutAttributesForItem(at: IndexPath(item: 0, section: 1)))
                    let rowFrame = list.convert(first.frame, to: page.view)
                    XCTAssertGreaterThanOrEqual(rowFrame.minY, titleFrame.maxY)
                    XCTAssertGreaterThan(second.frame.minY, first.frame.maxY)
                    XCTAssertEqual(list.contentOffset.y, -list.adjustedContentInset.top, accuracy: 1)
                    let metrics = XCTAttachment(string: """
                        width=\(viewport) category=\(category.rawValue) dark=\(dark)
                        headerBottom=\(headerBottom) title=\(titleFrame) firstRow=\(rowFrame)
                        firstHeader=\(sectionHeaderFrame) titleBounds=\(title.bounds)
                        offset=\(list.contentOffset) inset=\(list.contentInset) adjustedInset=\(list.adjustedContentInset)
                        """)
                    metrics.name = "settings-titled-top-\(Int(viewport))-\(category.rawValue)-\(dark)"
                    metrics.lifetime = .keepAlways; add(metrics)
                    let screenshot = XCTAttachment(image: UIGraphicsImageRenderer(bounds: page.view.bounds).image { _ in
                        XCTAssertTrue(page.view.drawHierarchy(in: page.view.bounds, afterScreenUpdates: true))
                    })
                    screenshot.name = metrics.name
                    screenshot.lifetime = .keepAlways; add(screenshot)
                }
            }
        }
    }

    func testCatalogSourceAndLockLyricAccessoriesDoNotEnlargeOrdinaryRows() throws {
        let appearance = MusicAppearancePreferencesStore()
        let page = HostingPageViewController(themeProvider: appearance, content: MusicSettingsContent(
            theme: PageTheme(palette: .sky), catalogSource: MusicHomeSourceSelection(
                selected: .qq, revision: 0, available: [.qq, .netease]
            )
        ))
        try withSettingsComponent(page) {
            waitForSettingsComponent("catalogue source row laid out") {
                page.view.layoutIfNeeded()
                return self.settingsDescendants(page.view).compactMap { $0 as? UICollectionView }
                    .contains { $0.numberOfSections == 2 && $0.numberOfItems(inSection: 0) == 7 }
            }
            let list = try XCTUnwrap(settingsDescendants(page.view).compactMap { $0 as? UICollectionView }.first)
            let baseline = try XCTUnwrap(list.layoutAttributesForItem(at: IndexPath(item: 0, section: 0))).size.height
            for index in [IndexPath(item: 6, section: 0), IndexPath(item: 0, section: 1)] {
                list.scrollToItem(at: index, at: .centeredVertically, animated: false)
                waitForSettingsComponent("compact accessory row visible") {
                    list.layoutIfNeeded()
                    return list.cellForItem(at: index) != nil
                }
                let cell = try XCTUnwrap(list.cellForItem(at: index))
                XCTAssertEqual(cell.bounds.height, baseline, accuracy: 1)
                for control in settingsDescendants(cell).compactMap({ $0 as? SettingsSwitch }) {
                    XCTAssertEqual(control.bounds.height, UIFont.preferredFont(forTextStyle: .body,
                        compatibleWith: control.traitCollection).lineHeight, accuracy: 1)
                    XCTAssertLessThanOrEqual(control.control.frame.height, control.bounds.height + 1)
                }
            }
        }
    }

    func testCompactToggleTapsOnlyOnceAndDisabledLabelKeepsValue() throws {
        let runner = XCUIApplication(bundleIdentifier: try XCTUnwrap(Bundle.main.bundleIdentifier))
        performAccountPageAction { runner.activate() }
        waitForSettingsComponent("compact toggle runner active") { UIApplication.shared.applicationState == .active }
        var changes: [Bool] = []
        let appearance = MusicAppearancePreferencesStore()
        let page = HostingPageViewController(themeProvider: appearance,
            content: CompactToggleFixture(enabled: true, changed: { changes.append($0) }))
        try withSettingsComponent(page) {
            waitForSettingsComponent("compact native switch visible") {
                !self.settingsDescendants(page.view).compactMap { $0 as? SettingsSwitch }.isEmpty
            }
            let wrapper = try XCTUnwrap(settingsDescendants(page.view).compactMap { $0 as? SettingsSwitch }.first)
            let control = runner.switches["settings.compact-toggle"]
            XCTAssertTrue(control.waitForExistence(timeout: 3))
            XCTAssertTrue(control.isHittable)
            XCTAssertLessThanOrEqual(wrapper.convert(wrapper.bounds, to: page.view).maxX, page.view.bounds.maxX)
            performAccountPageAction { control.tap() }
            waitForSettingsComponent("native switch sends exactly one change") { changes == [true] }
            XCTAssertEqual(changes, [true])
            performAccountPageAction {
                control.coordinate(withNormalizedOffset: CGVector(dx: 0.5, dy: 0.5))
                    .withOffset(CGVector(dx: 0, dy: 20)).tap()
            }
            waitForSettingsComponent("compact switch accepts the lower row touch once") { changes == [true, false] }
            performAccountPageAction {
                control.coordinate(withNormalizedOffset: CGVector(dx: 0.5, dy: 0.5))
                    .withOffset(CGVector(dx: 0, dy: -20)).tap()
            }
            waitForSettingsComponent("compact switch accepts the upper row touch once") { changes == [true, false, true] }
            page.setContent(CompactToggleFixture(enabled: false, changed: { changes.append($0) }))
            waitForSettingsComponent("compact switch disabled") { !wrapper.control.isEnabled }
            let label = runner.staticTexts["紧凑开关"]
            XCTAssertTrue(label.waitForExistence(timeout: 3))
            performAccountPageAction { label.tap() }
            XCTAssertEqual(changes, [true, false, true])
            XCTAssertTrue(wrapper.control.isOn)
        }
    }

    func testCompactCatalogMenuRetainsSelectionBindingAndDisabledState() throws {
        let runner = XCUIApplication(bundleIdentifier: try XCTUnwrap(Bundle.main.bundleIdentifier))
        performAccountPageAction { runner.activate() }
        waitForSettingsComponent("settings menu test host active") { UIApplication.shared.applicationState == .active }
        var selections: [MusicPublicBrowsePlatform] = []
        let appearance = MusicAppearancePreferencesStore()
        let page = HostingPageViewController(themeProvider: appearance, content: MusicSettingsContent(
            theme: PageTheme(palette: .sky), catalogSource: MusicHomeSourceSelection(
                selected: .qq, revision: 0, available: [.qq, .netease]
            ), selectCatalogSource: { selections.append($0) }
        ))
        try withSettingsComponent(page) {
            waitForSettingsComponent("catalogue source menu list ready") {
                self.settingsDescendants(page.view).compactMap { $0 as? UICollectionView }
                    .contains { $0.numberOfSections == 2 && $0.numberOfItems(inSection: 0) == 7 }
            }
            let list = try XCTUnwrap(settingsDescendants(page.view).compactMap { $0 as? UICollectionView }.first)
            list.scrollToItem(at: IndexPath(item: 6, section: 0), at: .centeredVertically, animated: false)
            list.layoutIfNeeded()
            let menu = runner.buttons["music.settings.catalogSource"]
            XCTAssertTrue(menu.waitForExistence(timeout: 3))
            XCTAssertTrue(menu.isEnabled)
            XCTAssertTrue(menu.label.contains(MusicPublicBrowsePlatform.qq.title))
            performAccountPageAction { menu.tap() }
            let option = runner.buttons[MusicPublicBrowsePlatform.netease.title]
            XCTAssertTrue(option.waitForExistence(timeout: 3))
            performAccountPageAction { option.tap() }
            waitForSettingsComponent("compact menu invokes the original selection binding") { selections == [.netease] }
            page.setContent(MusicSettingsContent(
                theme: PageTheme(palette: .sky), catalogSource: MusicHomeSourceSelection(
                    selected: .netease, revision: 1, available: [.qq, .netease]
                ), savingCatalogSource: true, selectCatalogSource: { selections.append($0) }
            ))
            waitForSettingsComponent("in-flight catalogue save disables the compact menu") {
                page.view.layoutIfNeeded()
                return menu.exists && !menu.isEnabled && menu.label.contains(MusicPublicBrowsePlatform.netease.title)
            }
            XCTAssertEqual(selections, [.netease])
        }
    }

    func testAccountLoginFormKeepsDraftAndReadableControlsAcrossAppearances() throws {
        let appearance = MusicAppearancePreferencesStore()
        let service = makeService()
        let accounts = MusicSettingsSMSAccountsFixture(base: service)
        let owner = makeOwner(service: service, accounts: accounts)
        let state = MusicAccountLoginState(owner: owner, service: accounts, platform: .netease)
        defer {
            state.close()
            let closed = expectation(description: "account color owner closed")
            Task { await owner.close(); closed.fulfill() }
            wait(for: [closed], timeout: 3)
        }
        state.start(); state.selectMethod(.sms)
        state.fields.setPhone("10000000000")
        let page = MusicSettingsPageViewController(appearance: appearance,
            content: MusicAccountLoginContent(state: state, theme: PageTheme(palette: .coral)))
        try withSettingsComponent(page) {
            for dark in [false, true] {
                page.traitOverrides.userInterfaceStyle = dark ? .dark : .light
                waitForSettingsComponent("account form appearance propagated") { page.theme.dark == dark }
                page.setContent(MusicAccountLoginContent(state: state, theme: PageTheme(palette: .coral, dark: dark)))
                for enabled in [false, true] {
                    state.fields.setCode(enabled ? "000000" : "")
                    waitForSettingsComponent("account form retains its draft and button availability") {
                        page.view.layoutIfNeeded()
                        let fields = self.settingsDescendants(page.view).compactMap { $0 as? UITextField }
                        return state.actions.value.canSubmit == enabled &&
                            fields.contains { $0.text == "10000000000" } &&
                            fields.contains { $0.text == (enabled ? "000000" : "") }
                    }
                    let screenshot = XCTAttachment(image: UIGraphicsImageRenderer(bounds: page.view.bounds).image { _ in
                        XCTAssertTrue(page.view.drawHierarchy(in: page.view.bounds, afterScreenUpdates: true))
                    })
                    screenshot.name = "settings-semantic-account-\(dark ? "dark" : "light")-\(enabled ? "enabled" : "disabled")"
                    screenshot.lifetime = .keepAlways; add(screenshot)
                }
            }
        }
    }

    func testSettingsControlsKeepStableBlueAcrossPaletteAndAppearanceChanges() throws {
        let domain = "MusicSettingsSemanticColors." + UUID().uuidString
        let defaults = try XCTUnwrap(UserDefaults(suiteName: domain))
        defer { defaults.removePersistentDomain(forName: domain) }
        let appearance = MusicAppearancePreferencesStore(defaults: defaults)
        let service = makeService()
        let owner = makeOwner(service: service, accounts: service)
        defer {
            let closed = expectation(description: "semantic color owner closed")
            Task { await owner.close(); closed.fulfill() }
            wait(for: [closed], timeout: 3)
        }
        let router = MusicSettingsRouterFixture()
        let page = MusicSettingsViewController(model: owner, appearance: appearance, router: router)
        try withSettingsComponent(page) {
            for dark in [false, true] {
                page.traitOverrides.userInterfaceStyle = dark ? .dark : .light
                waitForSettingsComponent("settings appearance propagated") { page.theme.dark == dark }
                for palette in [PagePalette.preset(.coral), .custom(0xffff00)] {
                    appearance.selectFixedPalette(palette)
                    try showLockLyricRow(in: page)
                    let accent = SettingsTheme(dark: dark).accent
                    let expected = UIColor(accent)
                    waitForSettingsComponent("settings switch rendered after palette change") {
                        page.view.layoutIfNeeded()
                        return page.view.tintColor == expected &&
                            !self.settingsDescendants(page.view).compactMap { $0 as? UISwitch }.isEmpty
                    }
                    let toggle = try XCTUnwrap(settingsDescendants(page.view).compactMap { $0 as? UISwitch }.first)
                    let tint = try XCTUnwrap(toggle.onTintColor)
                    var red: CGFloat = 0, green: CGFloat = 0, blue: CGFloat = 0, alpha: CGFloat = 0
                    XCTAssertTrue(tint.resolvedColor(with: toggle.traitCollection)
                        .getRed(&red, green: &green, blue: &blue, alpha: &alpha), "\(tint)")
                    XCTAssertEqual([red, green, blue, alpha].map { Int(($0 * 255).rounded()) },
                        [accent.red, accent.green, accent.blue, accent.alpha].map(Int.init), "\(tint)")
                    XCTAssertEqual(page.theme.palette, palette)
                    XCTAssertEqual(page.pageHeader.background.theme.palette, palette)
                    XCTAssertEqual(page.view.tintColor, expected)
                }
                let screenshot = XCTAttachment(image: UIGraphicsImageRenderer(bounds: page.view.bounds).image { _ in
                    XCTAssertTrue(page.view.drawHierarchy(in: page.view.bounds, afterScreenUpdates: true))
                })
                screenshot.name = "settings-semantic-music-\(dark ? "dark" : "light")"
                screenshot.lifetime = .keepAlways; add(screenshot)
            }
        }
    }

    func testMusicSettingsLockLyricControlPersistsAndReportsFailedWrites() throws {
        let domain = "MusicLockLyricSettings." + UUID().uuidString
        let defaults = try XCTUnwrap(UserDefaults(suiteName: domain))
        defer { defaults.removePersistentDomain(forName: domain) }
        let appearance = MusicAppearancePreferencesStore(defaults: defaults)
        let service = makeService()
        let owner = makeOwner(service: service, accounts: service)
        defer {
            let closed = expectation(description: "lock lyric owner closed")
            Task { await owner.close(); closed.fulfill() }
            wait(for: [closed], timeout: 3)
        }
        let router = MusicSettingsRouterFixture()
        let page = MusicSettingsViewController(model: owner, appearance: appearance, router: router)
        try withSettingsComponent(page) {
            XCTAssertEqual(page.pageTitle, "音乐设置")
            try showLockLyricRow(in: page)
            waitForSettingsComponent("direct lock lyric switch visible") {
                self.settingsDescendants(page.view).compactMap { $0 as? UISwitch }.count == 1
            }
            let control = try XCTUnwrap(settingsDescendants(page.view).compactMap { $0 as? UISwitch }.first)
            XCTAssertTrue(control.isOn)
            control.setOn(false, animated: false); control.sendActions(for: .valueChanged)
            waitForSettingsComponent("lock lyric preference persisted") { !appearance.preferences.lockLyric }
            XCTAssertFalse(MusicAppearancePreferencesStore(defaults: defaults).preferences.lockLyric)

            let corrupt = Data("invalid-preferences".utf8)
            defaults.set(corrupt, forKey: MusicAppearancePreferencesStore.preferencesKey)
            waitForSettingsComponent("music page ready for error feedback") { page.canPresentPageAlert }
            try showLockLyricRow(in: page)
            let current = try XCTUnwrap(settingsDescendants(page.view).compactMap { $0 as? UISwitch }.first)
            current.setOn(true, animated: false); current.sendActions(for: .valueChanged)
            waitForSettingsComponent("failed lock lyric write reported") { page.presentedViewController is UIAlertController }
            XCTAssertEqual((page.presentedViewController as? UIAlertController)?.message, "音乐偏好读取失败")
            XCTAssertFalse(appearance.preferences.lockLyric)
            XCTAssertEqual(defaults.data(forKey: MusicAppearancePreferencesStore.preferencesKey), corrupt)
        }
    }

    /// A UIKit component host checks geometry and persistence, not App routing.
    func testLibraryPlaylistToggleChangesCellGeometryPreservesScrollAnchorAndPersistsAcrossStores() throws {
        let domain = "MusicLibraryLayout." + UUID().uuidString
        let defaults = try XCTUnwrap(UserDefaults(suiteName: domain))
        defer { defaults.removePersistentDomain(forName: domain) }
        let appearance = MusicAppearancePreferencesStore(defaults: defaults)
        let service = makeService()
        let owner = makeOwner(service: service, accounts: service)
        defer {
            let closed = expectation(description: "library layout owner closed")
            Task { await owner.close(); closed.fulfill() }
            wait(for: [closed], timeout: 3)
        }
        let items = (0..<72).map {
            MusicCollection(id: "layout-\($0)", title: "合成歌单 \($0)", count: $0 + 1)
        }
        let loaded = expectation(description: "synthetic library loaded")
        Task {
            await service.setCollections(items)
            await owner.load()
            loaded.fulfill()
        }
        wait(for: [loaded], timeout: 3)
        XCTAssertEqual(owner.collections.map(\.id), items.map(\.id))
        XCTAssertEqual(appearance.preferences.playlistView, .grid)
        let router = MusicSettingsRouterFixture()
        let page = MusicLibraryViewController(model: owner, appearance: appearance, router: router)
        try withSettingsComponent(page) {
            let collection = try XCTUnwrap(settingsDescendants(page.view).compactMap { $0 as? UICollectionView }.first)
            waitForSettingsComponent("all playlists rendered") {
                collection.layoutIfNeeded()
                return collection.numberOfSections == 1 && collection.numberOfItems(inSection: 0) == items.count &&
                    !collection.visibleCells.isEmpty
            }
            XCTAssertGreaterThan(collection.contentSize.height, collection.bounds.height * 2)
            collection.scrollToItem(at: IndexPath(item: 36, section: 0), at: .top, animated: false)
            collection.layoutIfNeeded()
            // Leave the top row partly visible, as during an ordinary scroll.
            collection.setContentOffset(CGPoint(x: 0, y: collection.contentOffset.y + 17), animated: false)
            collection.layoutIfNeeded()
            let visible = collection.visibleCells.filter { $0.frame.intersects(collection.bounds) }.sorted {
                $0.frame.minY == $1.frame.minY ? $0.frame.minX < $1.frame.minX : $0.frame.minY < $1.frame.minY
            }
            let anchor = try XCTUnwrap(visible.first)
            let anchorID = try XCTUnwrap(anchor.accessibilityIdentifier)
            let expectedIDs = Set(items.map { "music.library.collection." + $0.id })
            XCTAssertEqual(anchorID, "music.library.collection." + items[36].id)
            XCTAssertTrue(visible.allSatisfy { expectedIDs.contains($0.accessibilityIdentifier ?? "") })
            let originalSize = anchor.bounds.size
            let originalY = anchor.convert(anchor.bounds, to: page.view).minY
            XCTAssertLessThan(originalSize.width, collection.bounds.width / 2)
            XCTAssertTrue(visible.dropFirst().contains {
                abs($0.frame.minY - anchor.frame.minY) < 1 && $0.frame.minX > anchor.frame.minX
            }, "Grid layout places multiple real cells on the same row")
            XCTAssertGreaterThan(collection.contentOffset.y, -collection.adjustedContentInset.top + originalSize.height)
            XCTAssertLessThan(collection.contentOffset.y,
                collection.contentSize.height - collection.bounds.height + collection.adjustedContentInset.bottom - originalSize.height)

            page.togglePlaylistAppearance()
            waitForSettingsComponent("list layout rendered with the same playlist visible") {
                collection.layoutIfNeeded()
                return collection.visibleCells.contains {
                    $0.accessibilityIdentifier == anchorID && $0.bounds.width > collection.bounds.width * 0.8
                }
            }
            let listAnchor = try XCTUnwrap(collection.visibleCells.first { $0.accessibilityIdentifier == anchorID })
            XCTAssertEqual(appearance.preferences.playlistView, .list)
            XCTAssertGreaterThan(listAnchor.bounds.width, originalSize.width * 2)
            XCTAssertLessThan(listAnchor.bounds.height, originalSize.height)
            XCTAssertTrue(listAnchor.frame.intersects(collection.bounds))
            XCTAssertEqual(listAnchor.convert(listAnchor.bounds, to: page.view).minY, originalY, accuracy: 1)
            let listIDs = collection.visibleCells.compactMap(\.accessibilityIdentifier)
            XCTAssertEqual(listIDs.count, collection.visibleCells.count)
            XCTAssertTrue(listIDs.contains(anchorID))
            XCTAssertTrue(Set(listIDs).isSubset(of: expectedIDs))
            XCTAssertGreaterThan(collection.contentOffset.y, -collection.adjustedContentInset.top + listAnchor.bounds.height)
            XCTAssertLessThan(collection.contentOffset.y,
                collection.contentSize.height - collection.bounds.height + collection.adjustedContentInset.bottom - listAnchor.bounds.height)
            let reopenedList = MusicAppearancePreferencesStore(defaults: try XCTUnwrap(UserDefaults(suiteName: domain)))
            XCTAssertEqual(reopenedList.preferences.playlistView, .list)

            page.togglePlaylistAppearance()
            waitForSettingsComponent("grid layout restored with the same playlist visible") {
                collection.layoutIfNeeded()
                return collection.visibleCells.contains {
                    $0.accessibilityIdentifier == anchorID && abs($0.bounds.width - originalSize.width) < 1 &&
                        abs($0.bounds.height - originalSize.height) < 1
                }
            }
            let gridAnchor = try XCTUnwrap(collection.visibleCells.first { $0.accessibilityIdentifier == anchorID })
            XCTAssertEqual(appearance.preferences.playlistView, .grid)
            XCTAssertEqual(gridAnchor.bounds.width, originalSize.width, accuracy: 1)
            XCTAssertEqual(gridAnchor.bounds.height, originalSize.height, accuracy: 1)
            XCTAssertTrue(gridAnchor.frame.intersects(collection.bounds))
            XCTAssertEqual(gridAnchor.convert(gridAnchor.bounds, to: page.view).minY, originalY, accuracy: 1)
            let gridIDs = collection.visibleCells.compactMap(\.accessibilityIdentifier)
            XCTAssertEqual(gridIDs.count, collection.visibleCells.count)
            XCTAssertTrue(gridIDs.contains(anchorID))
            XCTAssertTrue(Set(gridIDs).isSubset(of: expectedIDs))
            let reopenedGrid = MusicAppearancePreferencesStore(defaults: try XCTUnwrap(UserDefaults(suiteName: domain)))
            XCTAssertEqual(reopenedGrid.preferences.playlistView, .grid)
        }
    }

    func testSourceManagementRendersBuiltInAndScriptSwitchesTogetherAndWritesThroughBindings() throws {
        let runner = XCUIApplication(bundleIdentifier: try XCTUnwrap(Bundle.main.bundleIdentifier))
        performAccountPageAction { runner.activate() }
        waitForSettingsComponent("source search host foreground") { UIApplication.shared.applicationState == .active }
        let domain = "MusicSourceSettings." + UUID().uuidString
        let defaults = try XCTUnwrap(UserDefaults(suiteName: domain))
        defer { defaults.removePersistentDomain(forName: domain) }
        let appearance = MusicAppearancePreferencesStore(defaults: defaults)
        let service = makeService(), scripts = MusicSettingsScriptFixture()
        let model = MusicViewModel(service: service, player: MusicSettingsPlayerFixture(), scriptSources: scripts)
        let state = MusicSourceManagementState(model: model)
        let loaded = expectation(description: "source list loaded")
        Task { await model.load(); state.start(); await state.flush(); loaded.fulfill() }
        wait(for: [loaded], timeout: 3)
        XCTAssertEqual(MusicSettingsDestination.sources.title, "音源管理")
        XCTAssertEqual(MusicSettingsDestination.sources.accessibilityIdentifier, "music.settings.sources")
        let host = UIHostingController(rootView: MusicSourceManagementContent(state: state, theme: PageTheme(colors: appearance.pagePalette)))
        let scene = UIApplication.shared.connectedScenes.compactMap { $0 as? UIWindowScene }.first
        let container = UIViewController()
        container.addChild(host)
        container.view.addSubview(host.view)
        host.view.translatesAutoresizingMaskIntoConstraints = false
        let width = host.view.widthAnchor.constraint(equalToConstant: 390)
        NSLayoutConstraint.activate([
            host.view.leadingAnchor.constraint(equalTo: container.view.leadingAnchor),
            host.view.topAnchor.constraint(equalTo: container.view.topAnchor),
            host.view.bottomAnchor.constraint(equalTo: container.view.bottomAnchor), width
        ])
        host.didMove(toParent: container)
        let window = scene.map { UIWindow(windowScene: $0) } ?? UIWindow(frame: CGRect(x: 0, y: 0, width: 390, height: 844))
        window.frame = scene?.screen.bounds ?? window.frame
        window.rootViewController = container; window.makeKeyAndVisible()
        defer { window.isHidden = true; window.rootViewController = nil }
        func views(_ view: UIView) -> [UIView] { [view] + view.subviews.flatMap(views) }
        let switchesReady = XCTNSPredicateExpectation(predicate: NSPredicate { _, _ in
            host.view.layoutIfNeeded()
            return views(host.view).compactMap { $0 as? UISwitch }.count == 4
        }, object: nil)
        wait(for: [switchesReady], timeout: 3)
        let switches = views(host.view).compactMap { $0 as? UISwitch }.sorted { $0.convert(.zero, to: host.view).y < $1.convert(.zero, to: host.view).y }
        XCTAssertEqual(switches.count, 4)
        guard switches.count == 4 else { return }
        XCTAssertTrue(switches[0].isOn); XCTAssertFalse(switches[3].isOn)
        switches[0].setOn(false, animated: false); switches[0].sendActions(for: .valueChanged)
        switches[3].setOn(true, animated: false); switches[3].sendActions(for: .valueChanged)
        let saved = expectation(description: "switches persisted")
        Task { await state.flush(); saved.fulfill() }
        wait(for: [saved], timeout: 3)
        XCTAssertFalse(state.officialEnabled(.netease))
        XCTAssertEqual(state.scripts.first?.state, .ready)
        let manager = try XCTUnwrap(views(host.view).compactMap { $0 as? ManagementListView }.first)
        let table = try XCTUnwrap(views(manager).compactMap { $0 as? UITableView }.first)
        XCTAssertEqual(manager.convert(manager.bounds, to: host.view).maxY, host.view.bounds.maxY, accuracy: 0.5)
        XCTAssertEqual(table.frame.maxY, manager.safeAreaLayoutGuide.layoutFrame.maxY - 4, accuracy: 0.5)
        XCTAssertFalse(manager.search.isHidden)
        XCTAssertGreaterThan(manager.search.searchTextField.bounds.width, manager.bounds.width / 2)
        manager.setSelection(["fixture"])
        manager.onSelection?()
        for query in ["fixture", "official:" + MusicAccountPlatform.netease.rawValue, ""] {
            manager.search.text = query
            manager.search.delegate?.searchBar?(manager.search, textDidChange: query)
            XCTAssertEqual(table.numberOfRows(inSection: 0), query.isEmpty ? 4 : 1)
            if !query.isEmpty {
                table.layoutIfNeeded()
                let row = try XCTUnwrap(table.cellForRow(at: IndexPath(row: 0, section: 0)))
                XCTAssertEqual(row.accessibilityIdentifier, "management.row.\(query)")
            }
            XCTAssertEqual(state.selected, ["fixture"], "Search must preserve selected source identities")
        }
        let searchInput = runner.searchFields["搜索音源"]
        XCTAssertTrue(searchInput.waitForExistence(timeout: 3))
        performAccountPageAction { searchInput.tap(); searchInput.typeText("fixture") }
        waitForSettingsComponent("typed source search and keyboard layout applied") {
            host.view.layoutIfNeeded(); manager.layoutIfNeeded()
            return table.numberOfRows(inSection: 0) == 1 &&
                manager.keyboardLayoutGuide.layoutFrame.height > manager.safeAreaInsets.bottom + 1
        }
        let footer = try XCTUnwrap(views(manager).first {
            $0.accessibilityIdentifier == "management.selectAll"
        }?.superview)
        XCTAssertLessThanOrEqual(footer.frame.maxY, manager.keyboardLayoutGuide.layoutFrame.minY - 8 + 0.5)
        XCTAssertEqual(table.frame.maxY, manager.keyboardLayoutGuide.layoutFrame.minY - 4, accuracy: 0.5)
        XCTAssertEqual(footer.frame.maxY, table.frame.maxY - 4, accuracy: 0.5)
        XCTAssertEqual(state.selected, ["fixture"])
        performAccountPageAction { window.endEditing(true) }
        manager.search.text = ""
        manager.search.delegate?.searchBar?(manager.search, textDidChange: "")
        waitForSettingsComponent("source search keyboard dismissed") {
            window.layoutIfNeeded(); host.view.layoutIfNeeded(); manager.layoutIfNeeded()
            return manager.keyboardLayoutGuide.layoutFrame.height <= manager.safeAreaInsets.bottom + 1
        }
        let screenshot = XCTAttachment(image: UIGraphicsImageRenderer(bounds: host.view.bounds).image { context in
            host.view.drawHierarchy(in: host.view.bounds, afterScreenUpdates: true)
        })
        screenshot.name = "Unified music sources"; screenshot.lifetime = .keepAlways
        add(screenshot)
        host.traitOverrides.preferredContentSizeCategory = .accessibilityExtraExtraExtraLarge
        width.constant = 320
        waitForSettingsComponent("narrow large type sources laid out") {
            window.layoutIfNeeded(); host.view.layoutIfNeeded()
            return host.view.window === window && host.view.bounds.width == 320 &&
                host.traitCollection.preferredContentSizeCategory == .accessibilityExtraExtraExtraLarge &&
                !views(host.view).compactMap { $0 as? UITableView }.flatMap(\.visibleCells).isEmpty &&
                !views(host.view).compactMap { $0 as? UISwitch }.isEmpty
        }
        for toggle in views(host.view).compactMap({ $0 as? UISwitch }) {
            let frame = toggle.convert(toggle.bounds, to: host.view)
            XCTAssertGreaterThanOrEqual(frame.minX, 0)
            XCTAssertLessThanOrEqual(frame.maxX, host.view.bounds.width)
        }
        let largeType = XCTAttachment(image: UIGraphicsImageRenderer(bounds: host.view.bounds).image { _ in
            XCTAssertTrue(host.view.drawHierarchy(in: host.view.bounds, afterScreenUpdates: true))
        })
        largeType.name = "Music sources 320 AXXXL"; largeType.lifetime = .keepAlways; add(largeType)
        state.close()
        let closed = expectation(description: "source settings closed")
        Task { await model.close(); closed.fulfill() }
        wait(for: [closed], timeout: 3)
    }

    /// Synchronous on purpose: pages released inside a Swift task enter the
    /// back-deployed isolated-deinit path, which UIKit owners never use.
    func testSettingsConsumersUseTheAppOwnedHeaderAndKeepFloatingCards() throws {
        let domain = "MusicSettingsPlatformTests." + UUID().uuidString
        let defaults = try XCTUnwrap(UserDefaults(suiteName: domain))
        defer { defaults.removePersistentDomain(forName: domain) }
        let appearance = MusicAppearancePreferencesStore(defaults: defaults)
        let service = makeService()
        let owner = makeOwner(service: service, accounts: service)
        let router = MusicSettingsRouterFixture()
        let root = MusicSettingsViewController(model: owner, appearance: appearance, router: router)
        let carrier = MusicSettingsPageViewController(appearance: appearance, content: EmptyView())
        let accounts = MusicAccountsViewController(model: owner, appearance: appearance, router: router)
        let login = MusicAccountLoginViewController(
            owner: owner, service: service, platform: .qq, appearance: appearance
        )
        let detail = MusicAccountDetailViewController(
            state: MusicAccountsSettingsState(owner: owner), platform: .qq, appearance: appearance, actions: .init()
        )
        let pages: [PageViewController] = [root, carrier, accounts, login, detail]
        for page in pages {
            XCTAssertFalse(page.isViewLoaded)
            XCTAssertNil(page.title)
            XCTAssertNil(page.navigationItem.title)
        }
        XCTAssertEqual(root.pageTitle, "音乐设置")
        XCTAssertEqual(accounts.pageTitle, "平台账号")
        XCTAssertEqual(login.pageTitle, MusicAccountPlatform.qq.title)
        XCTAssertEqual(detail.pageTitle, MusicAccountPlatform.qq.title)
        for card in [login, detail] as [UIViewController] {
            XCTAssertEqual(card.modalPresentationStyle, .overFullScreen, "Account login and details float over the list")
            XCTAssertTrue(card.view is AccountFloatingBackdrop)
        }
        XCTAssertNil(PageViewController(themeProvider: appearance).navigationItem.titleView)
        XCTAssertNil(HostingPageViewController(
            themeProvider: appearance, content: EmptyView()
        ).navigationItem.titleView)
        let closed = expectation(description: "owner closed")
        Task { await owner.close(); closed.fulfill() }
        wait(for: [closed], timeout: 2)
    }

    func testQRCompletionRequiresAuthoritativeSignedInStatus() async {
        let service = makeService()
        let owner = makeOwner(service: service, accounts: service)
        let state = MusicAccountLoginState(owner: owner, service: service, platform: .qq)
        defer { state.close() }
        await prepareQR(state)
        XCTAssertEqual(state.status, MusicLoginProgress.waiting.title)

        await service.completeQR(as: .requiresValidation)
        await finishQR(state)
        XCTAssertEqual(state.model.progress, .confirmed)
        XCTAssertEqual(state.model.rows.first { $0.platform == .qq }?.phase, .requiresValidation)
        XCTAssertFalse(state.signedIn)
        XCTAssertNotEqual(state.status, MusicLoginProgress.confirmed.title)
        XCTAssertTrue(state.canRetry)

        let accepted = expectation(description: "authoritative QR login accepted")
        let observation = state.$signedIn.filter { $0 }.prefix(1).sink { _ in accepted.fulfill() }
        defer { observation.cancel() }
        state.retry()
        await prepareQR(state)
        await service.completeQR(as: .signedIn)
        await finishQR(state)
        await fulfillment(of: [accepted], timeout: 2)
        XCTAssertEqual(state.model.rows.first { $0.platform == .qq }?.phase, .signedIn)
        XCTAssertTrue(state.signedIn)
        XCTAssertEqual(state.status, MusicLoginProgress.confirmed.title)
        XCTAssertFalse(state.canRetry)
        XCTAssertNil(state.model.challenge)
        await owner.close()
    }

    func testSignedInPresentationIsClearedWhenAccountAccessIsRevoked() async {
        let service = makeService()
        let owner = makeOwner(service: service, accounts: service)
        let state = MusicAccountLoginState(owner: owner, service: service, platform: .qq)
        defer { state.close() }
        await prepareQR(state)
        await service.completeQR(as: .signedIn)
        await finishQR(state)
        XCTAssertTrue(state.signedIn)

        let accessObservation = owner.accessChanges.receive(on: RunLoop.main)
            .sink { [weak state] in state?.reconcileAccess() }
        let revoked = expectation(description: "completion removed after access revocation")
        let completionObservation = state.$signedIn.filter { !$0 }.prefix(1)
            .sink { _ in revoked.fulfill() }
        defer { accessObservation.cancel(); completionObservation.cancel() }
        let generation = owner.accountAccessGeneration
        owner.setAvailable(false)
        await fulfillment(of: [revoked], timeout: 2)
        await flushPresentation()
        XCTAssertNotEqual(owner.accountAccessGeneration, generation)
        XCTAssertFalse(state.available)
        XCTAssertFalse(state.signedIn)
        XCTAssertFalse(state.canRetry)
        XCTAssertEqual(state.status, MusicAccountStatus.Phase.unavailable.title)
        XCTAssertTrue(state.model.rows.isEmpty)
        XCTAssertNil(state.image)
        let retainedStatus = await service.statuses()
        XCTAssertEqual(retainedStatus.first { $0.platform == .qq }?.phase, .signedIn,
                       "Revoking page access does not log out the native account")

        await service.setStatus(.signedOut, platform: .qq)
        let reopened = expectation(description: "new access generation reconciled")
        let availabilityObservation = state.$available.filter { $0 }.prefix(1)
            .sink { _ in reopened.fulfill() }
        defer { availabilityObservation.cancel() }
        owner.setAvailable(true)
        await fulfillment(of: [reopened], timeout: 2)
        await state.model.flush()
        await flushPresentation()
        XCTAssertFalse(state.signedIn)
        XCTAssertEqual(state.model.rows.first { $0.platform == .qq }?.phase, .signedOut)
        XCTAssertNotEqual(state.status, MusicLoginProgress.confirmed.title)
        await owner.close()
    }

    func testSamePlatformSessionChangeRechecksCompletion() async {
        let service = makeService()
        let owner = makeOwner(service: service, accounts: service)
        let state = MusicAccountLoginState(owner: owner, service: service, platform: .qq)
        defer { state.close() }
        await prepareQR(state)
        await service.completeQR(as: .signedIn)
        await finishQR(state)
        XCTAssertTrue(state.canFinish)

        state.accountSessionChanged(.netease)
        XCTAssertFalse(state.checkingAccount, "Another platform's session does not gate completion")
        XCTAssertTrue(state.canFinish)

        await service.setStatus(.signedOut, platform: .qq)
        state.accountSessionChanged(.qq)
        XCTAssertTrue(state.checkingAccount)
        XCTAssertFalse(state.canFinish, "Cached signed-in rows cannot finish after a session change")
        await state.model.flush()
        await flushPresentation()
        XCTAssertFalse(state.checkingAccount)
        XCTAssertFalse(state.signedIn)
        XCTAssertFalse(state.canFinish)
        XCTAssertEqual(state.model.rows.first { $0.platform == .qq }?.phase, .signedOut)
        await owner.close()
    }

    func testLateQRConfirmationCannotRestoreCompletionAfterAccessRevocation() async {
        let service = makeService()
        let owner = makeOwner(service: service, accounts: service)
        let state = MusicAccountLoginState(owner: owner, service: service, platform: .qq)
        defer { state.close() }
        await prepareQR(state)
        await service.completeQR(as: .signedIn)
        let entered = expectation(description: "QR confirmation request entered")
        let gate = MusicSettingsQRCheckGate { entered.fulfill() }
        await service.holdQRCheck(gate)
        state.model.setForeground(true)
        let pending = state.model.pendingPoll
        XCTAssertNotNil(pending)
        await fulfillment(of: [entered], timeout: 2)

        owner.setAvailable(false)
        state.reconcileAccess()
        XCTAssertFalse(state.available)
        await gate.release()
        await pending?.value
        await state.model.flush()
        await flushPresentation()
        let lateStatus = await service.statuses()
        XCTAssertEqual(lateStatus.first { $0.platform == .qq }?.phase, .signedIn)
        XCTAssertFalse(state.signedIn)
        XCTAssertFalse(state.canRetry)
        XCTAssertTrue(state.model.rows.isEmpty)
        XCTAssertEqual(state.status, MusicAccountStatus.Phase.unavailable.title)
        await owner.close()
    }

    func testLoginMethodsRespectQQAndOptionalSMSCapabilities() async {
        for platform in MusicAccountPlatform.allCases {
            let service = makeService()
            let accounts = MusicSettingsSMSAccountsFixture(base: service)
            let owner = makeOwner(service: service, accounts: accounts)
            let state = MusicAccountLoginState(owner: owner, service: accounts, platform: platform)
            defer { state.close() }
            await prepareQR(state)
            let supportsSMS = platform != .qq
            XCTAssertEqual(state.supportsSMS, supportsSMS)
            state.model.setForeground(true)
            state.selectMethod(.sms)
            state.fields.setPhone("10000000000")
            state.fields.setCode("000000")
            await flushPresentation()
            XCTAssertEqual(state.method, supportsSMS ? .sms : .qr)
            XCTAssertEqual(state.actions.value.canSend, supportsSMS)
            XCTAssertEqual(state.actions.value.canSubmit, supportsSMS)
            XCTAssertFalse(state.actions.value.sent, "No code was requested, so the action is not a resend")
            XCTAssertFalse(state.signedIn)
            let before = await service.smsCalls()
            XCTAssertTrue(before.sends.isEmpty)
            XCTAssertTrue(before.submits.isEmpty)

            if supportsSMS {
                let accepted = expectation(description: platform.title + " explicit SMS accepted")
                let observation = state.$signedIn.filter { $0 }.prefix(1).sink { _ in accepted.fulfill() }
                defer { observation.cancel() }
                state.actions.submit()
                await state.model.flush()
                await fulfillment(of: [accepted], timeout: 2)
                XCTAssertTrue(state.signedIn)
                XCTAssertEqual(state.model.rows.first { $0.platform == platform }?.phase, .signedIn)
                XCTAssertEqual(state.status, MusicLoginProgress.confirmed.title)
            } else {
                state.actions.send()
                state.actions.submit()
                await state.model.flush()
                await flushPresentation()
                XCTAssertFalse(state.signedIn)
            }
            let after = await service.smsCalls()
            XCTAssertTrue(after.sends.isEmpty, "Selecting a method or submitting a code must not send SMS")
            XCTAssertEqual(after.submits, supportsSMS ? [platform] : [])
            await owner.close()
        }

        for platform in [MusicAccountPlatform.netease, .kugou] {
            let service = makeService()
            let owner = makeOwner(service: service, accounts: service)
            let state = MusicAccountLoginState(owner: owner, service: service, platform: platform)
            defer { state.close() }
            await prepareQR(state)
            XCTAssertFalse(state.supportsSMS)
            state.model.setForeground(true)
            state.selectMethod(.sms)
            state.fields.setPhone("10000000000")
            state.fields.setCode("000000")
            await flushPresentation()
            XCTAssertEqual(state.method, .qr)
            XCTAssertFalse(state.actions.value.canSend)
            XCTAssertFalse(state.actions.value.canSubmit)
            state.actions.send()
            state.actions.submit()
            await state.model.flush()
            let calls = await service.smsCalls()
            XCTAssertTrue(calls.sends.isEmpty)
            XCTAssertTrue(calls.submits.isEmpty)
            XCTAssertFalse(state.signedIn)
            await owner.close()
        }
    }

    func testAccountPageKeepsNavigationAndBottomFileActionsWithoutRepeatedSectionTitle() throws {
        let service = makeService()
        let owner = makeOwner(service: service, accounts: service)
        let appearance = MusicAppearancePreferencesStore(defaults: UserDefaults(suiteName: UUID().uuidString)!)
        let loaded = expectation(description: "account page service available")
        Task { await owner.load(); loaded.fulfill() }
        wait(for: [loaded], timeout: 3)
        let page = MusicAccountsViewController(model: owner, appearance: appearance, router: MusicSettingsRouterFixture())
        try withMountedAccountPage(page) { runner in
            let importing = runner.buttons["music.accounts.import"]
            let exporting = runner.buttons["music.accounts.export"]
            let lastAccount = runner.buttons["music.accounts.kg"]
            XCTAssertTrue(importing.waitForExistence(timeout: 3))
            XCTAssertTrue(exporting.exists); XCTAssertTrue(lastAccount.exists)
            XCTAssertEqual(importing.label, "导入账号")
            XCTAssertEqual(exporting.label, "导出账号")
            XCTAssertTrue(importing.isHittable); XCTAssertTrue(exporting.isHittable)
            XCTAssertGreaterThan(importing.frame.minY, lastAccount.frame.minY)
            XCTAssertGreaterThan(exporting.frame.minY, importing.frame.minY)
            XCTAssertEqual(page.pageTitle, "平台账号")
            XCTAssertTrue(runner.staticTexts["平台账号"].exists)
            XCTAssertFalse(runner.staticTexts["音乐平台"].exists)
            let rows = settingsDescendants(page.view).compactMap { $0 as? SettingsRowBackgroundView }
                .filter { $0.bounds.width > 0 }
            XCTAssertGreaterThanOrEqual(rows.count, 5)
            for row in rows {
                XCTAssertGreaterThanOrEqual(row.bounds.height, SettingsVisualMetrics.rowHeight)
                XCTAssertEqual(row.bounds.width, page.view.bounds.width - SettingsVisualMetrics.cardHorizontalInset * 2,
                    accuracy: 0.5)
            }
            let screenshot = XCTAttachment(image: UIGraphicsImageRenderer(bounds: page.view.bounds).image { _ in
                XCTAssertTrue(page.view.drawHierarchy(in: page.view.bounds, afterScreenUpdates: true))
            })
            screenshot.name = "settings-platform-accounts-shared-type"
            screenshot.lifetime = .keepAlways; add(screenshot)
        }
        let closed = expectation(description: "account page closed")
        Task { await owner.close(); closed.fulfill() }
        wait(for: [closed], timeout: 3)
    }

    func testAccountImportUsesSingleSystemOpenPickerAndCancelReleasesBusyState() throws {
        let service = makeService(), appearance = MusicAppearancePreferencesStore(defaults: .standard)
        let owner = makeOwner(service: service, accounts: service)
        let loaded = expectation(description: "account service available")
        Task { await owner.load(); loaded.fulfill() }
        wait(for: [loaded], timeout: 3)
        let page = MusicAccountsViewController(model: owner, appearance: appearance, router: MusicSettingsRouterFixture())
        try withMountedAccountPage(page) { runner in
            let button = runner.buttons["music.accounts.import"]
            XCTAssertTrue(button.waitForExistence(timeout: 3))
            XCTAssertTrue(button.isHittable)
            performAccountPageAction { button.tap() }
            let shown = expectation(for: NSPredicate { _, _ in
                MainActor.assumeIsolated {
                    guard let picker = page.presentedViewController as? UIDocumentPickerViewController else { return false }
                    return picker.viewIfLoaded?.window != nil && !picker.isBeingPresented
                }
            }, evaluatedWith: nil)
            wait(for: [shown], timeout: 10)
            let picker = try XCTUnwrap(page.presentedViewController as? UIDocumentPickerViewController)
            XCTAssertFalse(picker.allowsMultipleSelection)
            XCTAssertEqual(picker.documentPickerMode, .open)
            XCTAssertTrue(page.transfer.busy)
            page.documentPickerWasCancelled(picker)
            XCTAssertFalse(page.transfer.busy)
        }
        let closed = expectation(description: "file picker owner closed")
        Task { await owner.close(); closed.fulfill() }
        wait(for: [closed], timeout: 3)
    }

    func testAccountImportAlreadyDismissedSystemPickerStartsImportAndReleasesBusyState() throws {
        let service = makeService(importConflicts: [])
        try withMountedAccountImportPicker(service) { page, picker, input, runner in
            let dismissed = expectation(description: "system picker automatic dismissal completed")
            performAccountPageAction {
                picker.dismiss(animated: true) { dismissed.fulfill() }
            }
            wait(for: [dismissed], timeout: 5)
            waitForSettingsComponent("system picker presentation links removed before delegate delivery") {
                page.presentedViewController == nil && picker.presentingViewController == nil &&
                    picker.viewIfLoaded?.window == nil && !picker.isBeingDismissed && page.canPresentPageAlert
            }
            XCTAssertTrue(page.transfer.busy)
            performAccountPageAction {
                page.documentPicker(picker, didPickDocumentsAt: [input])
                XCTAssertNotNil(page.transfer.pendingOperation, "A settled picker must hand its file to the import owner")
            }
            waitForSettingsComponent("already dismissed picker started and finished account import") {
                page.transfer.summary != nil && !page.transfer.busy
            }
            let summary = try XCTUnwrap(page.transfer.summary)
            XCTAssertEqual(summary.imported, [.netease, .qq])
            XCTAssertTrue(summary.skipped.isEmpty)
            try acknowledgeAccountImportSummary(page, runner: runner, summary: summary)
            assertAccountTransferCounts(service, prepares: 1, commits: 1)
        }
    }

    func testAccountImportDismissingSystemPickerStartsImportAndPresentsConflictAfterDismissal() throws {
        let service = makeService()
        try withMountedAccountImportPicker(service) { page, picker, input, runner in
            let dismissed = expectation(description: "in-flight system picker automatic dismissal completed")
            var dismissalFinished = false
            performAccountPageAction {
                picker.dismiss(animated: true) { dismissalFinished = true; dismissed.fulfill() }
                XCTAssertTrue(picker.isBeingDismissed)
                XCTAssertNotNil(picker.transitionCoordinator)
                XCTAssertFalse(dismissalFinished, "Deliver didPick during the real UIKit dismissal, not after it")
                page.documentPicker(picker, didPickDocumentsAt: [input])
            }
            wait(for: [dismissed], timeout: 5)
            waitForSettingsComponent("account conflict presented after system picker dismissal") {
                guard let alert = page.presentedViewController as? UIAlertController else { return false }
                return picker.viewIfLoaded?.window == nil && !picker.isBeingDismissed &&
                    alert.title == "已有平台账号" && alert.viewIfLoaded?.window != nil && !alert.isBeingPresented
            }
            let plan = try XCTUnwrap(page.transfer.plan)
            XCTAssertEqual(plan.conflicts, [.netease, .qq])
            XCTAssertTrue(page.transfer.busy); XCTAssertNil(page.transfer.summary)
            let skip = runner.alerts["已有平台账号"].buttons["跳过"]
            XCTAssertTrue(skip.waitForExistence(timeout: 3)); XCTAssertTrue(skip.isHittable)
            performAccountPageAction { skip.tap() }
            waitForSettingsComponent("conflict decision finished account import and released busy state") {
                page.transfer.summary != nil && !page.transfer.busy
            }
            let summary = try XCTUnwrap(page.transfer.summary)
            XCTAssertEqual(summary.skipped, [.netease, .qq])
            XCTAssertTrue(summary.imported.isEmpty)
            try acknowledgeAccountImportSummary(page, runner: runner, summary: summary)
            assertAccountTransferCounts(service, prepares: 1, commits: 1)
        }
    }

    func testAccountImportDismissingSystemPickerDoesNotConsumeReplacementSelection() throws {
        let service = makeService(importConflicts: [])
        try withMountedAccountImportPicker(service) { page, picker, input, _ in
            let dismissed = expectation(description: "obsolete picker dismissal completed")
            let originalSelection = page.transfer.selectionID
            performAccountPageAction {
                picker.dismiss(animated: true) { dismissed.fulfill() }
                XCTAssertTrue(picker.isBeingDismissed); XCTAssertNotNil(picker.transitionCoordinator)
                page.documentPicker(picker, didPickDocumentsAt: [input])
                // Replace the transfer selection before the original UIKit receipt, not via another Files pick.
                page.transfer.selectionCancelled()
                XCTAssertTrue(page.transfer.beginSelection())
            }
            let replacementSelection = page.transfer.selectionID
            XCTAssertNotEqual(replacementSelection, originalSelection)
            wait(for: [dismissed], timeout: 5)
            waitForSettingsComponent("obsolete picker dismissal settled") {
                picker.viewIfLoaded?.window == nil && !picker.isBeingDismissed && page.canPresentPageAlert
            }
            performAccountPageAction {
                XCTAssertEqual(page.transfer.selectionID, replacementSelection)
                XCTAssertTrue(page.transfer.busy, "The replacement selection is still waiting for its own file")
                XCTAssertNil(page.transfer.pendingOperation); XCTAssertNil(page.transfer.plan)
                XCTAssertNil(page.transfer.summary); XCTAssertNil(page.presentedViewController)
            }
            assertAccountTransferCounts(service, prepares: 0, commits: 0)
            page.transfer.selectionCancelled()
            XCTAssertFalse(page.transfer.busy)
        }
    }

    func testAccountFileStatePresentsConflictOncePerBatchAndPublishesSingleFinalSummary() async throws {
        let service = makeService()
        // Explicit transfer-capable account fixture; no platform network or Keychain.
        let accountService = makeService()
        let model = makeOwner(service: service, accounts: accountService)
        await model.load()
        let state = MusicAccountTransferState(owner: model); state.start()
        var confirmations = 0, reports: [String] = []
        state.confirm = { _ in confirmations += 1 }
        state.result = { reports.append($0) }
        let input = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString + ".json")
        try Data("synthetic file boundary".utf8).write(to: input)
        defer { try? FileManager.default.removeItem(at: input) }
        XCTAssertTrue(state.beginSelection()); state.importFile(input)
        await state.pendingOperation?.value
        XCTAssertEqual(confirmations, 1); XCTAssertTrue(reports.isEmpty)
        state.commit(.skip); await state.pendingOperation?.value
        XCTAssertEqual(reports.count, 1)
        XCTAssertTrue(state.beginSelection()); state.importFile(input)
        await state.pendingOperation?.value
        XCTAssertEqual(confirmations, 2)
        state.selectionCancelled()
        let counts = await accountService.transferCounts()
        XCTAssertEqual(counts.prepares, 2); XCTAssertEqual(counts.commits, 1)
        XCTAssertEqual(reports.count, 1)
        state.close(); await model.close()
    }

    func testAccountExportUsesSystemDestinationPickerAndCancelRemovesStaging() throws {
        let service = makeService(), appearance = MusicAppearancePreferencesStore(defaults: .standard)
        let owner = makeOwner(service: service, accounts: service)
        let loaded = expectation(description: "export service available")
        Task { await owner.load(); loaded.fulfill() }
        wait(for: [loaded], timeout: 3)
        let page = MusicAccountsViewController(model: owner, appearance: appearance, router: MusicSettingsRouterFixture())
        try withMountedAccountPage(page) { runner in
            let button = runner.buttons["music.accounts.export"]
            XCTAssertTrue(button.waitForExistence(timeout: 3))
            XCTAssertTrue(button.isHittable)
            performAccountPageAction { button.tap() }
            let ready = expectation(for: NSPredicate { _, _ in
                MainActor.assumeIsolated {
                    guard let picker = page.presentedViewController as? UIDocumentPickerViewController else { return false }
                    return picker.viewIfLoaded?.window != nil && !picker.isBeingPresented
                }
            }, evaluatedWith: nil)
            wait(for: [ready], timeout: 10)
            let picker = try XCTUnwrap(page.presentedViewController as? UIDocumentPickerViewController)
            XCTAssertEqual(picker.documentPickerMode, .exportToService)
            let staging = try XCTUnwrap(page.transfer.temporaryDirectory)
            XCTAssertTrue(FileManager.default.fileExists(atPath: staging.path))
            page.documentPickerWasCancelled(picker)
            XCTAssertFalse(FileManager.default.fileExists(atPath: staging.path))
            XCTAssertFalse(page.transfer.busy)
        }
        let closed = expectation(description: "export owner closed")
        Task { await owner.close(); closed.fulfill() }
        wait(for: [closed], timeout: 3)
    }

    func testAccountExportCleansOnlyOwnedStagingForCompletionCancellationAndPageClose() async throws {
        let accountService = makeService()
        let model = makeOwner(service: makeService(), accounts: accountService)
        await model.load()
        let state = MusicAccountTransferState(owner: model); state.start()
        var source: URL?
        state.exported = { source = $0 }
        state.exportFile(); await state.pendingOperation?.value
        let staged = try XCTUnwrap(source), directory = try XCTUnwrap(state.temporaryDirectory)
        XCTAssertTrue(FileManager.default.fileExists(atPath: staged.path))
        let destination = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString + ".json")
        try FileManager.default.copyItem(at: staged, to: destination)
        defer { try? FileManager.default.removeItem(at: destination) }
        state.exportFinished()
        XCTAssertFalse(FileManager.default.fileExists(atPath: directory.path))
        XCTAssertTrue(FileManager.default.fileExists(atPath: destination.path))
        source = nil; state.exportFile(); await state.pendingOperation?.value
        let cancelled = try XCTUnwrap(state.temporaryDirectory)
        state.selectionCancelled()
        XCTAssertFalse(FileManager.default.fileExists(atPath: cancelled.path))
        source = nil; state.exportFile(); await state.pendingOperation?.value
        let leaving = try XCTUnwrap(state.temporaryDirectory)
        state.close()
        XCTAssertFalse(FileManager.default.fileExists(atPath: leaving.path))
        XCTAssertTrue(FileManager.default.fileExists(atPath: destination.path))
        await model.close()
    }

    func testAccountFileReadFailureAndPageCancellationCannotPublishLateSummary() async throws {
        let service = makeService()
        let model = makeOwner(service: service, accounts: service); await model.load()
        let state = MusicAccountTransferState(owner: model); state.start()
        var reports: [String] = []
        state.result = { reports.append($0) }
        XCTAssertTrue(state.beginSelection())
        state.importFile(FileManager.default.temporaryDirectory.appendingPathComponent("private-missing-" + UUID().uuidString))
        await state.pendingOperation?.value
        XCTAssertEqual(reports.count, 1)
        XCTAssertFalse(reports[0].contains("private-missing"))
        let input = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try Data([1]).write(to: input); defer { try? FileManager.default.removeItem(at: input) }
        let gate = MusicSettingsTransferGate(); await service.holdTransfer(gate)
        state.confirm = { [weak state] _ in state?.commit(.overwrite) }
        XCTAssertTrue(state.beginSelection()); state.importFile(input)
        await state.pendingOperation?.value
        await gate.started()
        let pending = state.pendingOperation
        state.close(); await gate.release(); await pending?.value
        XCTAssertEqual(reports.count, 1); XCTAssertNil(state.summary)
        await model.close()
    }

    // A real foreground system picker dismissal stands in for Files' automatic dismissal.
    // Direct delegate delivery models only the UIKit-to-import handoff, not a Files selection.
    private func withMountedAccountImportPicker(
        _ service: MusicSettingsServiceFixture,
        body: (MusicAccountsViewController, UIDocumentPickerViewController, URL, XCUIApplication) throws -> Void
    ) throws {
        let owner = makeOwner(service: service, accounts: service)
        defer {
            let closed = expectation(description: "picker handoff owner closed")
            Task { await owner.close(); closed.fulfill() }
            wait(for: [closed], timeout: 3)
        }
        let input = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString + ".json")
        try Data("synthetic file boundary".utf8).write(to: input)
        defer { try? FileManager.default.removeItem(at: input) }
        let loaded = expectation(description: "picker handoff account service available")
        Task { await owner.load(); loaded.fulfill() }
        wait(for: [loaded], timeout: 3)
        let page = MusicAccountsViewController(model: owner,
            appearance: MusicAppearancePreferencesStore(defaults: .standard), router: MusicSettingsRouterFixture())
        try withMountedAccountPage(page) { runner in
            let button = runner.buttons["music.accounts.import"]
            XCTAssertTrue(button.waitForExistence(timeout: 3)); XCTAssertTrue(button.isHittable)
            performAccountPageAction { button.tap() }
            let shown = expectation(for: NSPredicate { _, _ in
                MainActor.assumeIsolated {
                    guard let picker = page.presentedViewController as? UIDocumentPickerViewController else { return false }
                    return picker.viewIfLoaded?.window != nil && !picker.isBeingPresented
                }
            }, evaluatedWith: nil)
            wait(for: [shown], timeout: 10)
            let picker = try XCTUnwrap(page.presentedViewController as? UIDocumentPickerViewController)
            XCTAssertTrue(picker.delegate === page); XCTAssertEqual(picker.documentPickerMode, .open)
            XCTAssertTrue(page.transfer.busy); XCTAssertNil(page.transfer.pendingOperation)
            try body(page, picker, input, runner)
        }
    }

    private func acknowledgeAccountImportSummary(
        _ page: MusicAccountsViewController, runner: XCUIApplication, summary: MusicAccountImportSummary
    ) throws {
        waitForSettingsComponent("final account import summary presented in the foreground window") {
            guard let alert = page.presentedViewController as? UIAlertController else { return false }
            return alert.title == "账号导入导出" && alert.message == summary.message &&
                alert.viewIfLoaded?.window != nil && !alert.isBeingPresented
        }
        let alert = try XCTUnwrap(page.presentedViewController as? UIAlertController)
        XCTAssertEqual(alert.actions.map(\.title), ["确定"])
        XCTAssertFalse(page.transfer.busy); XCTAssertNil(page.transfer.plan)
        XCTAssertTrue(summary.failed.isEmpty); XCTAssertTrue(summary.cancelled.isEmpty)
        let confirm = runner.alerts["账号导入导出"].buttons["确定"]
        XCTAssertTrue(confirm.waitForExistence(timeout: 3)); XCTAssertTrue(confirm.isHittable)
        performAccountPageAction { confirm.tap() }
        waitForSettingsComponent("summary acknowledged without leaving a busy page or another alert") {
            page.presentedViewController == nil && page.canPresentPageAlert && !page.transfer.busy
        }
        XCTAssertEqual(page.transfer.summary, summary)
    }

    private func assertAccountTransferCounts(_ service: MusicSettingsServiceFixture, prepares: Int, commits: Int) {
        let counted = expectation(description: "account import transfer calls counted")
        Task {
            let counts = await service.transferCounts()
            XCTAssertEqual(counts.prepares, prepares); XCTAssertEqual(counts.commits, commits)
            counted.fulfill()
        }
        wait(for: [counted], timeout: 3)
    }

    private func withMountedAccountPage(
        _ page: MusicAccountsViewController, body: (XCUIApplication) throws -> Void
    ) throws {
        let runner = XCUIApplication(bundleIdentifier: try XCTUnwrap(Bundle.main.bundleIdentifier))
        performAccountPageAction { runner.activate() }
        let foreground = expectation(for: NSPredicate { _, _ in
            MainActor.assumeIsolated {
                UIApplication.shared.applicationState == .active &&
                    UIApplication.shared.connectedScenes.contains { $0.activationState == .foregroundActive }
            }
        }, evaluatedWith: nil)
        wait(for: [foreground], timeout: 3)
        let scene = try XCTUnwrap(UIApplication.shared.connectedScenes.compactMap { $0 as? UIWindowScene }
            .first { $0.activationState == .foregroundActive })
        let previousKeyWindow = scene.windows.first { $0.isKeyWindow }
        let navigation = PageNavigationController(themeProvider: page.themeProvider)
        navigation.setViewControllers([page], animated: false)
        let window = UIWindow(windowScene: scene)
        window.frame = scene.coordinateSpace.bounds
        window.windowLevel = .normal + 1
        window.rootViewController = navigation
        window.makeKeyAndVisible(); window.layoutIfNeeded()
        defer {
            if navigation.presentedViewController != nil || page.presentedViewController != nil {
                let dismissed = expectation(description: "account page modal dismissed")
                navigation.dismiss(animated: false) { dismissed.fulfill() }
                wait(for: [dismissed], timeout: 3)
            }
            PageViewController.invalidatePages(in: navigation)
            window.isHidden = true; window.rootViewController = nil
            previousKeyWindow?.makeKey()
        }
        let visible = expectation(for: NSPredicate { _, _ in
            MainActor.assumeIsolated {
                page.view.layoutIfNeeded()
                return page.isPageActive && page.canPresentPageAlert && page.transfer.available
            }
        }, evaluatedWith: nil)
        wait(for: [visible], timeout: 3)
        XCTAssertTrue(page.isPageActive); XCTAssertTrue(page.isPageVisible)
        XCTAssertTrue(page.transfer.available); XCTAssertTrue(page.canPresentPageAlert)
        try body(runner)
    }

    private func performAccountPageAction(_ action: @escaping @MainActor () -> Void) {
        let performed = expectation(description: "runner action delivered")
        RunLoop.main.perform {
            MainActor.assumeIsolated { action() }
            performed.fulfill()
        }
        wait(for: [performed], timeout: 5)
    }

    private func makeService(importConflicts: [MusicAccountPlatform] = [.netease, .qq]) -> MusicSettingsServiceFixture {
        let image = UIGraphicsImageRenderer(size: CGSize(width: 8, height: 8)).pngData { context in
            UIColor.black.setFill()
            context.fill(CGRect(x: 0, y: 0, width: 8, height: 8))
        }
        return MusicSettingsServiceFixture(image: image, importConflicts: importConflicts)
    }

    private func makeOwner(
        service: MusicSettingsServiceFixture, accounts: any MusicAccountsServing
    ) -> MusicViewModel {
        MusicViewModel(service: service, player: MusicSettingsPlayerFixture(), accounts: accounts)
    }

    private func settingsDescendants(_ view: UIView) -> [UIView] {
        [view] + view.subviews.flatMap(settingsDescendants)
    }

    private func showLockLyricRow(in page: UIViewController) throws {
        waitForSettingsComponent("music settings sections laid out") {
            page.view.layoutIfNeeded()
            return self.settingsDescendants(page.view).compactMap { $0 as? UICollectionView }.contains {
                $0.numberOfSections > 1 && $0.numberOfItems(inSection: 1) == 1
            }
        }
        let list = try XCTUnwrap(settingsDescendants(page.view).compactMap { $0 as? UICollectionView }.first)
        let row = IndexPath(item: 0, section: 1)
        list.scrollToItem(at: row, at: .centeredVertically, animated: false)
        waitForSettingsComponent("lock lyric row scrolled into view") {
            list.layoutIfNeeded()
            guard list.indexPathsForVisibleItems.contains(row),
                  let cell = list.cellForItem(at: row) else { return false }
            return self.settingsDescendants(cell).compactMap { $0 as? UISwitch }.count == 1
        }
    }

    private func waitForSettingsComponent(_ description: String, until predicate: @escaping @MainActor () -> Bool) {
        let ready = XCTNSPredicateExpectation(predicate: NSPredicate { _, _ in
            MainActor.assumeIsolated { predicate() }
        }, object: nil)
        wait(for: [ready], timeout: 3)
        XCTAssertTrue(predicate(), description)
    }

    private func withSettingsComponent(_ page: PageViewController, body: () throws -> Void) rethrows {
        let scene = UIApplication.shared.connectedScenes.compactMap { $0 as? UIWindowScene }.first
        let previousWindow = scene?.windows.first { $0.isKeyWindow }
        let container = UIViewController()
        container.addChild(page)
        container.view.addSubview(page.view)
        page.view.translatesAutoresizingMaskIntoConstraints = false
        NSLayoutConstraint.activate([
            page.view.leadingAnchor.constraint(equalTo: container.view.leadingAnchor),
            page.view.topAnchor.constraint(equalTo: container.view.topAnchor),
            page.view.bottomAnchor.constraint(equalTo: container.view.bottomAnchor),
            page.view.widthAnchor.constraint(equalToConstant: 390)
        ])
        page.didMove(toParent: container)
        let window = scene.map { UIWindow(windowScene: $0) } ?? UIWindow(frame: CGRect(x: 0, y: 0, width: 390, height: 844))
        window.frame = scene?.screen.bounds ?? window.frame
        window.rootViewController = container; window.makeKeyAndVisible()
        defer {
            PageViewController.invalidatePages(in: page)
            window.isHidden = true; window.rootViewController = nil
            previousWindow?.makeKey()
        }
        NotificationCenter.default.post(name: UIApplication.didBecomeActiveNotification, object: nil)
        waitForSettingsComponent("component page visible") {
            page.view.layoutIfNeeded()
            return page.isPageVisible
        }
        try body()
    }

    private func prepareQR(_ state: MusicAccountLoginState) async {
        state.start()
        await state.model.flush()
        state.model.setForeground(false)
        await flushPresentation()
        XCTAssertNotNil(state.model.challenge)
        XCTAssertNotNil(state.image)
        XCTAssertEqual(state.method, .qr)
        XCTAssertFalse(state.signedIn)
    }

    private func finishQR(_ state: MusicAccountLoginState) async {
        // The existing foreground-resume path checks immediately, without
        // replacing the production clock or waiting for its polling interval.
        state.model.setForeground(true)
        let pending = state.model.pendingPoll
        XCTAssertNotNil(pending)
        let completed = expectation(description: "immediate QR poll completed")
        var drained = false
        let drain = Task {
            await pending?.value
            drained = true
            completed.fulfill()
        }
        await fulfillment(of: [completed], timeout: 2)
        if !drained { state.model.setForeground(false) }
        await drain.value
        await state.model.flush()
        await flushPresentation()
    }

    private func flushPresentation() async {
        let flushed = expectation(description: "main-run-loop presentation delivered")
        RunLoop.main.perform { flushed.fulfill() }
        await fulfillment(of: [flushed], timeout: 2)
    }
}

@MainActor
private struct SettingsTitledListGeometryFixture: View {
    let theme: PageTheme
    var body: some View {
        List {
            Section {
                Text("QQ 音乐").modifier(SettingsListRowStyle(theme: theme, first: true, last: true))
            } header: {
                SettingsListSectionHeader(title: "歌词", theme: theme)
                    .accessibilityIdentifier("settings.geometry.firstHeader")
                    .background(SettingsTitleFrameProbe())
            }
            Section {
                Text("网易云音乐").modifier(SettingsListRowStyle(theme: theme, first: true, last: true))
            } header: {
                SettingsListSectionHeader(title: "封面", theme: theme)
            }
        }
        .modifier(SettingsListStyle(theme: theme))
    }
}

@MainActor
private struct SettingsTitleFrameProbe: UIViewRepresentable {
    func makeUIView(context: Context) -> UIView {
        let view = UIView()
        view.accessibilityIdentifier = "settings.geometry.titleBounds"
        view.isUserInteractionEnabled = false
        view.accessibilityElementsHidden = true
        return view
    }
    func updateUIView(_ uiView: UIView, context: Context) {}
}

@MainActor
private struct CompactToggleFixture: View {
    let enabled: Bool
    let changed: (Bool) -> Void
    @State private var value = false

    var body: some View {
        List {
            Toggle("紧凑开关", isOn: Binding(get: { value }, set: { value = $0; changed($0) }))
                .accessibilityIdentifier("settings.compact-toggle")
                .disabled(!enabled)
                .modifier(SettingsListRowStyle(theme: PageTheme(), first: true, last: true))
        }.modifier(SettingsListStyle(theme: PageTheme()))
    }
}

private actor MusicSettingsServiceFixture: MusicServing, MusicAccountsServing, MusicAccountTransferring {
    private var transferPrepares = 0, transferCommits = 0
    private let importConflicts: [MusicAccountPlatform]
    private var transferGate: MusicSettingsTransferGate?
    func holdTransfer(_ gate: MusicSettingsTransferGate) { transferGate = gate }
    func transferCounts() -> (prepares: Int, commits: Int) { (transferPrepares, transferCommits) }
    func exportAccountFile() throws -> MusicAccountFilePayload { try .init(fileData: Data("synthetic-export".utf8)) }
    func prepareAccountImport(_ payload: MusicAccountFilePayload) -> MusicAccountImportPlan {
        transferPrepares += 1; return MusicAccountImportPlan(conflicts: importConflicts, cancel: { _ in })
    }
    func importAccounts(_ plan: MusicAccountImportPlan, policy: MusicAccountImportPolicy) async -> MusicAccountImportSummary {
        transferCommits += 1
        if let transferGate { await transferGate.wait() }
        var report = MusicAccountImportSummary()
        if policy == .skip { report.skipped = plan.conflicts } else { report.imported = [.netease, .qq] }
        return report
    }
    func discardAccountImport(_ plan: MusicAccountImportPlan) { plan.cancel() }
    private let image: Data
    private var rows = MusicAccountPlatform.allCases.map {
        MusicAccountStatus(platform: $0, phase: .signedOut, canLogin: true)
    }
    private var challenge: (id: UUID, platform: MusicAccountPlatform)?
    private var qrCompletion: MusicAccountStatus.Phase?
    private var qrGate: MusicSettingsQRCheckGate?
    private var sends: [MusicAccountPlatform] = [], submits: [MusicAccountPlatform] = []
    private var settingsRevision = 0
    private var official = MusicAccountPlatform.allCases
    private var collectionItems: [MusicCollection] = []

    init(image: Data, importConflicts: [MusicAccountPlatform]) {
        self.image = image; self.importConflicts = importConflicts
    }
    func completeQR(as phase: MusicAccountStatus.Phase) { qrCompletion = phase }
    func holdQRCheck(_ gate: MusicSettingsQRCheckGate) { qrGate = gate }
    func setCollections(_ value: [MusicCollection]) { collectionItems = value }
    func smsCalls() -> (sends: [MusicAccountPlatform], submits: [MusicAccountPlatform]) { (sends, submits) }
    func setStatus(_ phase: MusicAccountStatus.Phase, platform: MusicAccountPlatform) {
        let value = MusicAccountStatus(platform: platform, phase: phase, canLogin: true)
        rows = rows.map { $0.platform == platform ? value : $0 }
    }
    func statuses() -> [MusicAccountStatus] { rows }
    func validate(platform: MusicAccountPlatform) throws -> MusicAccountStatus { throw MusicServiceError.unsupported }
    func beginLogin(platform: MusicAccountPlatform) -> MusicLoginChallenge {
        let id = UUID()
        challenge = (id, platform)
        qrCompletion = nil
        setStatus(.signingIn, platform: platform)
        return .init(id: id, platform: platform, image: image, expiresAt: .distantFuture)
    }
    func checkLogin(challengeID: UUID) async throws -> MusicLoginProgress {
        guard let challenge, challenge.id == challengeID else { throw MusicServiceError.staleResult }
        guard let phase = qrCompletion else { return .waiting }
        let gate = qrGate; qrGate = nil
        if let gate { await gate.wait() }
        // Deliberately non-cooperative: a late native receipt must not revive
        // the revoked page's presentation, even if native status is signed in.
        setStatus(phase, platform: challenge.platform)
        if self.challenge?.id == challengeID { self.challenge = nil }
        return .confirmed
    }
    func cancelLogin(challengeID: UUID) {
        guard let challenge, challenge.id == challengeID else { return }
        self.challenge = nil
        if rows.first(where: { $0.platform == challenge.platform })?.phase == .signingIn {
            setStatus(.signedOut, platform: challenge.platform)
        }
    }
    func logout(platform: MusicAccountPlatform) { setStatus(.signedOut, platform: platform) }
    func sendSMS(platform: MusicAccountPlatform, countryCode: String, phone: String,
                 attemptID: UUID) throws -> MusicSMSSendReceipt {
        sends.append(platform)
        guard platform != .qq else { throw MusicServiceError.unsupported }
        return .init(retryAt: .distantPast)
    }
    func submitSMS(platform: MusicAccountPlatform, countryCode: String, phone: String,
                   code: String, attemptID: UUID) throws -> MusicAccountStatus {
        submits.append(platform)
        guard platform != .qq else { throw MusicServiceError.unsupported }
        setStatus(.signedIn, platform: platform)
        return .init(platform: platform, phase: .signedIn, canLogin: true)
    }
    func cancelSMSLogin(attemptID: UUID) {}

    func state() -> MusicPlaybackState { .init(settingsRevision: settingsRevision, officialSources: official) }
    func platforms() -> [MusicPlatform] { [] }
    func search(platform: String, query: String, page: Int, limit: Int) throws -> MusicSearchPage { throw MusicServiceError.unsupported }
    func collections() -> [MusicCollection] { collectionItems }
    func tracks(in collection: MusicCollection) -> [MusicRemoteTrack] { [] }
    func createPlaylist(name: String, refs: [MusicReference]) throws -> MusicCollection { throw MusicServiceError.unsupported }
    func editPlaylist(_ collection: MusicCollection, name: String?, refs: [MusicReference]?, order: [String]?) throws -> MusicCollection { throw MusicServiceError.unsupported }
    func removePlaylist(_ collection: MusicCollection) throws { throw MusicServiceError.unsupported }
    func queue() -> MusicQueueSnapshot { .init(revision: 0, currentId: nil, entries: []) }
    func changeQueue(_ change: MusicQueueChange, expectedRevision: Int) throws -> MusicPlaybackState { throw MusicServiceError.unsupported }
    func advance(_ direction: MusicDirection, reason: MusicAdvanceReason, expectedRevision: Int) throws -> MusicAdvanceResult { throw MusicServiceError.unsupported }
    func updateSettings(_ change: MusicSettingsChange) throws -> MusicPlaybackState {
        guard change.ifRevision == settingsRevision else { throw MusicServiceError.staleRevision }
        official = change.officialSources ?? official
        settingsRevision += 1
        return state()
    }
    func sourceAvailability(for ref: MusicReference) -> MusicSourceAvailability { .init(options: [], scriptState: .notConfigured) }
    func metadataPreferences() throws -> MusicMetadataPreferences { throw MusicServiceError.unsupported }
    func updateMetadataPreferences(_ change: MusicMetadataPreferencesChange) throws -> MusicMetadataPreferences { throw MusicServiceError.unsupported }
    func metadataCandidates(occurrenceID: String) throws -> MusicMetadataCandidates { throw MusicServiceError.unsupported }
    func resolve(attemptId: String, occurrenceId: String, selection: MusicPlaybackSelection) throws -> MusicPlaybackCandidate { throw MusicServiceError.unsupported }
    func cancel(attemptId: String?) {}
    func record(_ fact: MusicPlaybackFact) -> Bool { false }
    func lyrics(occurrenceId: String, sourceKey: String) throws -> MusicLyricsSnapshot { throw MusicServiceError.unsupported }
    func playedSongs() -> [MusicPlayedSong] { [] }
    func playedPlaylists() -> [MusicPlayedPlaylist] { [] }
    func restoreHistory(id: String, expectedRevision: Int) throws -> MusicPlaybackState { throw MusicServiceError.unsupported }
}

private actor MusicSettingsScriptFixture: MusicScriptSourceManaging {
    var enabled = false
    func item() -> MusicScriptSourceItem {
        .init(id: "fixture", name: "合成 LX 音源", version: "2", origin: .file,
              platforms: [.qq], enabled: enabled, state: enabled ? .ready : .disabled)
    }
    func scriptSources() -> [MusicScriptSourceItem] { [item()] }
    func importScriptSource(_ data: Data, fileName: String) throws -> MusicScriptSourceItem { throw MusicServiceError.unsupported }
    func downloadScriptSource(from url: URL) throws -> MusicScriptSourceItem { throw MusicServiceError.unsupported }
    func setScriptSourceEnabled(_ id: String, enabled: Bool) -> MusicScriptSourceItem { self.enabled = enabled; return item() }
    func scriptSourceDetail(_ id: String) -> MusicScriptSourceDetail { .init(item: item(), networkURL: nil, rollbackAvailable: false) }
    func editScriptSource(_ id: String, name: String, networkURL: String?) -> MusicScriptSourceItem { item() }
    func updateScriptSource(_ id: String) -> MusicScriptSourceItem { item() }
    func updateScriptSource(_ id: String, data: Data, fileName: String) -> MusicScriptSourceItem { item() }
    func rollbackScriptSource(_ id: String) throws -> MusicScriptSourceItem { throw MusicServiceError.unsupported }
    func checkScriptSource(_ id: String) -> MusicScriptSourceItem { item() }
    func removeScriptSource(_ id: String) throws { throw MusicServiceError.unsupported }
}

/// The base fixture is QR-only; the optional SMS port exists only on this wrapper.
private struct MusicSettingsSMSAccountsFixture: MusicAccountsServing, MusicAccountsSMSServing {
    let base: MusicSettingsServiceFixture
    func statuses() async throws -> [MusicAccountStatus] { await base.statuses() }
    func validate(platform: MusicAccountPlatform) async throws -> MusicAccountStatus { try await base.validate(platform: platform) }
    func beginLogin(platform: MusicAccountPlatform) async throws -> MusicLoginChallenge { await base.beginLogin(platform: platform) }
    func checkLogin(challengeID: UUID) async throws -> MusicLoginProgress { try await base.checkLogin(challengeID: challengeID) }
    func cancelLogin(challengeID: UUID) async { await base.cancelLogin(challengeID: challengeID) }
    func logout(platform: MusicAccountPlatform) async throws { await base.logout(platform: platform) }
    func sendSMS(platform: MusicAccountPlatform, countryCode: String, phone: String, attemptID: UUID) async throws -> MusicSMSSendReceipt {
        try await base.sendSMS(platform: platform, countryCode: countryCode, phone: phone, attemptID: attemptID)
    }
    func submitSMS(platform: MusicAccountPlatform, countryCode: String, phone: String, code: String, attemptID: UUID) async throws -> MusicAccountStatus {
        try await base.submitSMS(platform: platform, countryCode: countryCode, phone: phone, code: code, attemptID: attemptID)
    }
    func cancelSMSLogin(attemptID: UUID) async { await base.cancelSMSLogin(attemptID: attemptID) }
}

private actor MusicSettingsQRCheckGate {
    private let entered: @Sendable () -> Void
    private var released = false
    private var waiter: CheckedContinuation<Void, Never>?
    init(_ entered: @escaping @Sendable () -> Void) { self.entered = entered }
    func wait() async {
        entered()
        if !released { await withCheckedContinuation { waiter = $0 } }
    }
    func release() { released = true; waiter?.resume(); waiter = nil }
}

@MainActor
private final class MusicSettingsPlayerFixture: MusicPlaybackDriving {
    func play(_ resource: PlaybackResource) -> MusicPlayerAttempt {
        XCTFail("Unexpected playback"); return .init(id: UUID(), resourceID: resource.id)
    }
    func pause(_ attempt: MusicPlayerAttempt) { XCTFail("Unexpected pause") }
    func resume(_ attempt: MusicPlayerAttempt) { XCTFail("Unexpected resume") }
    func seek(_ position: Double, attempt: MusicPlayerAttempt) -> Bool { XCTFail("Unexpected seek"); return false }
    func stop(_ attempt: MusicPlayerAttempt) { XCTFail("Unexpected stop") }
}

@MainActor
private final class MusicSettingsRouterFixture: MusicUIRouting {
    let primaryPlaybackPresentation = MusicMiniPlayerPresentation()
    func showSearch() { XCTFail("Unexpected navigation") }
    func showSettings() { XCTFail("Unexpected navigation") }
    func showPublicBrowse(_ request: MusicPublicBrowseRequest, title: String) { XCTFail("Unexpected navigation") }
    func showPublicCollection(_ collection: MusicPublicBrowseCollection) { XCTFail("Unexpected navigation") }
    func showLocalCollection(_ collection: MusicCollection) { XCTFail("Unexpected navigation") }
    func showPlayer(_ destination: MusicPlaybackDestination) { XCTFail("Unexpected navigation") }
    func selectChannel(_ channel: MusicPublicBrowseChannel) { XCTFail("Unexpected navigation") }
}
#endif

#if os(iOS)
private actor MusicSettingsTransferGate {
    private var entered = false, released = false
    private var waiting: CheckedContinuation<Void, Never>?
    private var starting: CheckedContinuation<Void, Never>?
    func wait() async {
        entered = true; starting?.resume(); starting = nil
        if !released { await withCheckedContinuation { waiting = $0 } }
    }
    func started() async { if !entered { await withCheckedContinuation { starting = $0 } } }
    func release() { released = true; waiting?.resume(); waiting = nil }
}
#endif
