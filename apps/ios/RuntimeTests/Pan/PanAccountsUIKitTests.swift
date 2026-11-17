#if os(iOS)
import XCTest
import UIKit
import SwiftUI
import CoreImage
import Combine
import MeowRuntime
import MeowStorage
import MeowUI
@testable import MeowFilmFeature

@MainActor
final class PanAccountsUIKitTests: XCTestCase {
    private weak var previousKeyWindow: UIWindow?

    func testPageEntryHasNoRPCValidationOrReadIndicators() async throws {
        let runtime = try PanUIKitRuntime()
        try await runtime.set(.baidu, status: .signedIn)
        let model = PanAccountsModel(service: runtime.service)
        model.start()
        let page = PanAccountsViewController(model: model, themeProvider: PanAccountsTheme())
        page.loadViewIfNeeded()
        defer { PageViewController.invalidatePages(in: page); model.close() }
        XCTAssertNil(page.table.backgroundView)
        XCTAssertEqual(model.rows.count, 7)
        XCTAssertEqual(model.rows[0].account?.status, .unverified)
        for index in 1...7 {
            let cell = page.tableView(page.table, cellForRowAt: IndexPath(row: index, section: 0))
            XCTAssertFalse(descendants(cell).contains { $0 is UIActivityIndicatorView })
        }
        let configured = page.tableView(page.table, cellForRowAt: IndexPath(row: 1, section: 0))
        XCTAssertEqual(try statusLabel(in: configured).text, "未验证")
        XCTAssertEqual(try statusLabel(in: configured).textColor, UIColor(page.theme.settings.warning))
        model.close(); model.start()
        let calls = await runtime.operations
        XCTAssertTrue(calls.isEmpty)
    }

    func testTableHasExactlyThreeColumnsAtPhoneWidthsWithThreeStateActionsAndTheme() async throws {
        let runtime = try PanUIKitRuntime()
        try await runtime.set(.baidu, status: .signedIn)
        try await runtime.set(.quark, status: .expired)
        let model = PanAccountsModel(service: runtime.service)
        model.start()
        await model.refresh(.baidu)?.value
        await model.refresh(.quark)?.value
        let theme = PanAccountsTheme()
        let page = PanAccountsViewController(model: model, themeProvider: theme)
        page.loadViewIfNeeded()
        defer { PageViewController.invalidatePages(in: page); model.close() }
        XCTAssertTrue(page.table is SettingsTableView)
        XCTAssertEqual(page.table.separatorStyle, .none)
        XCTAssertEqual(page.tableView(page.table, numberOfRowsInSection: 0), 8)
        let header = page.tableView(page.table, cellForRowAt: IndexPath(row: 0, section: 0))
        XCTAssertEqual(descendants(header).compactMap { ($0 as? UILabel)?.text }, ["平台", "状态", "操作"])
        XCTAssertTrue(descendants(header).compactMap { $0 as? UILabel }.allSatisfy(\.adjustsFontForContentSizeCategory))
        XCTAssertTrue((header.backgroundView as? SettingsRowBackgroundView)?.drawsSeparator == true)
        let headerColumns = try XCTUnwrap(descendants(header).compactMap { $0 as? PanAccountColumns }.first)
        let actionHeading = try XCTUnwrap(headerColumns.columns[2].arrangedSubviews.first as? UILabel)
        XCTAssertEqual(actionHeading.textAlignment, .center)
        for (index, status, titles) in [(1, "已登录", ["刷新", "退出登录"]), (2, "失效", ["重新登录", "退出登录"]), (3, "未登录", ["登录"])] {
            let cell = page.tableView(page.table, cellForRowAt: IndexPath(row: index, section: 0))
            let columns = try XCTUnwrap(descendants(cell).compactMap { $0 as? PanAccountColumns }.first)
            XCTAssertEqual(columns.arrangedSubviews.count, 3)
            XCTAssertEqual(descendants(columns.columns[1]).compactMap { ($0 as? UILabel)?.text }, [status])
            XCTAssertTrue(descendants(columns.columns[1]).compactMap { $0 as? UILabel }.allSatisfy(\.adjustsFontForContentSizeCategory))
            XCTAssertEqual(descendants(columns.columns[2]).compactMap { ($0 as? UIButton)?.configuration?.title }, titles)
            let badge = try statusBadge(in: cell)
            for width in [CGFloat(320), 390, 430] {
                cell.frame = CGRect(x: 0, y: 0, width: width - 32, height: 120)
                cell.setNeedsLayout(); cell.layoutIfNeeded(); cell.contentView.layoutIfNeeded()
                header.frame = cell.frame
                header.setNeedsLayout(); header.layoutIfNeeded(); header.contentView.layoutIfNeeded()
                XCTAssertFalse(columns.hasAmbiguousLayout)
                XCTAssertEqual(columns.axis, .horizontal)
                XCTAssertEqual(columns.columns[0].frame.minX, 0, accuracy: 0.5)
                XCTAssertGreaterThan(columns.columns[1].frame.minX, columns.columns[0].frame.maxX)
                XCTAssertGreaterThan(columns.columns[2].frame.minX, columns.columns[1].frame.maxX)
                XCTAssertEqual(columns.columns[2].frame.maxX, columns.bounds.width, accuracy: 0.5)
                XCTAssertGreaterThan(columns.columns[2].bounds.width, 60)
                XCTAssertLessThanOrEqual(badge.bounds.width, columns.columns[1].bounds.width + 0.5)
                if width == 430 {
                    XCTAssertLessThan(badge.bounds.width, columns.columns[1].bounds.width, "Short status must not fill its column")
                }
                XCTAssertEqual(badge.layer.cornerRadius, min(badge.bounds.width, badge.bounds.height) / 2, accuracy: 0.5)
                XCTAssertEqual(descendants(badge).compactMap { $0 as? UIImageView }.count, 1)
                let badgeIcon = try XCTUnwrap(descendants(badge).compactMap { $0 as? UIImageView }.first)
                assertContained(badgeIcon, in: badge)
                assertSymbolFits(badgeIcon, size: 12)
                try assertPlatformFits(in: cell, title: model.rows[index - 1].id.title)
                for heading in descendants(header).compactMap({ $0 as? UILabel }) {
                    assertRegular(heading, style: .subheadline)
                    try assertTextFits(heading, in: header.contentView, maximumLines: 1)
                }
                for button in descendants(columns.columns[2]).compactMap({ $0 as? UIButton }) {
                    XCTAssertGreaterThanOrEqual(button.bounds.height, 44)
                    XCTAssertEqual(button.titleLabel?.font, .preferredFont(forTextStyle: .body))
                    XCTAssertEqual(button.contentHorizontalAlignment, .center)
                    XCTAssertEqual(button.titleLabel?.textAlignment, .center)
                    let headingFrame = header.contentView.convert(actionHeading.bounds, from: actionHeading)
                    let actionFrame = cell.contentView.convert(button.bounds, from: button)
                    XCTAssertEqual(headingFrame.midX, actionFrame.midX, accuracy: 0.5)
                    let title = try XCTUnwrap(button.titleLabel)
                    XCTAssertEqual(button.convert(title.bounds, from: title).midX, button.bounds.midX, accuracy: 0.5)
                    XCTAssertEqual(title.text, button.configuration?.title)
                    try assertTextFits(title, in: button, maximumLines: 2)
                }
            }
        }
        page.overrideUserInterfaceStyle = .dark
        theme.setColorway(.sky); page.applyTheme()
        let darkCell = page.tableView(page.table, cellForRowAt: IndexPath(row: 1, section: 0))
        let darkBackground = try XCTUnwrap(darkCell.backgroundView as? SettingsRowBackgroundView)
        let fill = try XCTUnwrap(darkBackground.layer.sublayers?.compactMap { $0 as? CAShapeLayer }.first?.fillColor)
        XCTAssertTrue(UIColor(cgColor: fill).isEqual(UIColor(page.theme.settings.surface)))
        XCTAssertEqual(try statusLabel(in: darkCell).textColor, UIColor(page.theme.settings.success))
        let window = mount(page)
        defer { unmount(page, window: window) }
        await eventually { page.isPageVisible }
        for dark in [false, true] {
            page.overrideUserInterfaceStyle = dark ? .dark : .light
            page.applyTheme(); page.table.reloadData(); page.view.layoutIfNeeded()
            attach(page, name: "pan-account-statuses-\(dark ? "dark" : "light")")
        }
    }

    func testTableSemanticColorsAndBusyActionsFollowStyleWithoutFollowingPalette() async throws {
        let runtime = try PanUIKitRuntime()
        try await runtime.set(.baidu, status: .signedIn)
        try await runtime.set(.quark, status: .expired)
        try await runtime.set(.tianyi, status: .signedIn)
        let model = PanAccountsModel(service: runtime.service)
        model.start()
        await model.refresh(.baidu)?.value
        await model.refresh(.quark)?.value
        let theme = PanAccountsTheme()
        let page = PanAccountsViewController(model: model, themeProvider: theme)
        page.loadViewIfNeeded()
        defer { PageViewController.invalidatePages(in: page); model.close() }
        for style in [UIUserInterfaceStyle.light, .dark] {
            page.overrideUserInterfaceStyle = style
            let settings = SettingsTheme(dark: style == .dark)
            for colorway in [PageColorway.lilac, .sky] {
                theme.setColorway(colorway); page.applyTheme()
                let header = page.tableView(page.table, cellForRowAt: IndexPath(row: 0, section: 0))
                let headings = descendants(header).compactMap { $0 as? UILabel }
                XCTAssertEqual(headings.count, 3)
                for heading in headings { XCTAssertEqual(heading.textColor, UIColor(settings.muted)) }
                for (index, text, color, count) in [
                    (1, "已登录", settings.success, 2), (2, "失效", settings.danger, 2),
                    (3, "未登录", settings.muted, 1), (4, "未验证", settings.warning, 3),
                ] {
                    let cell = page.tableView(page.table, cellForRowAt: IndexPath(row: index, section: 0))
                    let badge = try statusBadge(in: cell)
                    let status = try statusLabel(in: cell)
                    XCTAssertEqual(status.text, text)
                    XCTAssertEqual(status.textColor, UIColor(color))
                    let icon = try XCTUnwrap(descendants(badge).compactMap { $0 as? UIImageView }.first)
                    XCTAssertNotNil(icon.image)
                    XCTAssertEqual(icon.tintColor, status.textColor)
                    XCTAssertNotNil(badge.backgroundColor)
                    XCTAssertNotEqual(badge.backgroundColor, UIColor(settings.surface))
                    XCTAssertNotEqual(badge.backgroundColor, status.textColor)
                    let actions = descendants(cell).compactMap { $0 as? UIButton }
                    XCTAssertEqual(actions.count, count)
                    for action in actions {
                        XCTAssertTrue(action.isEnabled)
                        XCTAssertEqual(action.configuration?.baseForegroundColor, UIColor(settings.accent))
                        XCTAssertEqual(try titleColor(action), UIColor(settings.accent))
                    }
                }
                XCTAssertNotNil(model.openLogin(.baidu))
                let busyCell = page.tableView(page.table, cellForRowAt: IndexPath(row: 1, section: 0))
                XCTAssertFalse(descendants(busyCell).contains { $0 is SettingsStatusBadge })
                let spinner = try XCTUnwrap(descendants(busyCell).compactMap { $0 as? UIActivityIndicatorView }.first)
                XCTAssertTrue(spinner.isAnimating)
                XCTAssertEqual(spinner.accessibilityLabel, "正在操作账号")
                let busyActions = descendants(busyCell).compactMap { $0 as? UIButton }
                XCTAssertEqual(busyActions.count, 2)
                for action in busyActions {
                    XCTAssertFalse(action.isEnabled)
                    XCTAssertEqual(action.configuration?.baseForegroundColor, UIColor(settings.muted))
                    XCTAssertEqual(try titleColor(action), UIColor(settings.muted))
                }
                model.closeLogin()
                let restoredCell = page.tableView(page.table, cellForRowAt: IndexPath(row: 1, section: 0))
                XCTAssertEqual(try statusLabel(in: restoredCell).text, "已登录")
                let restoredActions = descendants(restoredCell).compactMap { $0 as? UIButton }
                XCTAssertEqual(restoredActions.count, 2)
                for action in restoredActions {
                    XCTAssertTrue(action.isEnabled)
                    XCTAssertEqual(try titleColor(action), UIColor(settings.accent))
                }
            }
        }
    }

    func testStatusBadgeFitsPhoneWidthsAndDynamicTypeWithoutClaimingOnlineLogin() async throws {
        let runtime = try PanUIKitRuntime()
        try await runtime.set(.baidu, status: .signedIn)
        let model = PanAccountsModel(service: runtime.service)
        let page = PanAccountsViewController(model: model, themeProvider: PanAccountsTheme())
        let window = mount(page)
        defer { unmount(page, window: window); model.close() }
        await eventually { page.isPageVisible }
        var heights: [CGFloat] = []
        for category in [UIContentSizeCategory.large, .extraExtraExtraLarge, .accessibilityMedium,
                         .accessibilityExtraExtraExtraLarge, .large] {
            page.traitOverrides.preferredContentSizeCategory = category
            for width in [CGFloat(320), 390, 430] {
                window.frame = CGRect(x: 0, y: 0, width: width, height: 844)
                page.view.frame = window.bounds
                window.setNeedsLayout(); window.layoutIfNeeded(); page.view.layoutIfNeeded()
                page.table.scrollToRow(at: IndexPath(row: 0, section: 0), at: .top, animated: false)
                page.table.layoutIfNeeded()
                let header = try XCTUnwrap(page.table.cellForRow(at: IndexPath(row: 0, section: 0)))
                let headerColumns = try XCTUnwrap(descendants(header).compactMap { $0 as? PanAccountColumns }.first)
                let headings = descendants(header).compactMap { $0 as? UILabel }
                XCTAssertEqual(headings.map(\.text), ["平台", "状态", "操作"])
                for heading in headings {
                    assertRegular(heading, style: .subheadline)
                    try assertTextFits(heading, in: header.contentView, maximumLines: 1)
                }
                XCTAssertEqual(headerColumns.axis, category.isAccessibilityCategory ? .vertical : .horizontal)
                let actionHeading = try XCTUnwrap(headerColumns.columns[2].arrangedSubviews.first as? UILabel)
                let cell = try XCTUnwrap(page.table.cellForRow(at: IndexPath(row: 1, section: 0)))
                cell.layoutIfNeeded(); cell.contentView.layoutIfNeeded()
                let columns = try XCTUnwrap(descendants(cell).compactMap { $0 as? PanAccountColumns }.first)
                let badge = try statusBadge(in: cell)
                let label = try statusLabel(in: cell)
                XCTAssertEqual(label.text, "未验证")
                XCTAssertTrue(label.adjustsFontForContentSizeCategory)
                XCTAssertEqual(badge.traitCollection.preferredContentSizeCategory, category)
                XCTAssertFalse(badge.hasAmbiguousLayout, "\(width)pt / \(category.rawValue)")
                XCTAssertGreaterThan(badge.bounds.width, 0)
                XCTAssertLessThanOrEqual(badge.bounds.width, columns.columns[1].bounds.width + 0.5)
                XCTAssertEqual(badge.layer.cornerRadius, min(badge.bounds.width, badge.bounds.height) / 2, accuracy: 0.5)
                let labelFrame = badge.convert(label.bounds, from: label)
                XCTAssertGreaterThanOrEqual(labelFrame.minX, 0)
                XCTAssertLessThanOrEqual(labelFrame.maxX, badge.bounds.maxX + 0.5)
                XCTAssertGreaterThanOrEqual(labelFrame.minY, 0)
                XCTAssertLessThanOrEqual(labelFrame.maxY, badge.bounds.maxY + 0.5)
                let required = label.sizeThatFits(CGSize(width: label.bounds.width, height: .greatestFiniteMagnitude))
                XCTAssertLessThanOrEqual(required.height, label.bounds.height + 0.5)
                try assertTextFits(label, in: badge, maximumLines: category.isAccessibilityCategory ? 1 : 3)
                let badgeIcon = try XCTUnwrap(descendants(badge).compactMap { $0 as? UIImageView }.first)
                assertContained(badgeIcon, in: badge)
                assertSymbolFits(badgeIcon, size: 12)
                if category.isAccessibilityCategory {
                    XCTAssertEqual(badge.bounds.width, columns.columns[1].bounds.width, accuracy: 0.5)
                }
                if width == 320 && category != .extraExtraExtraLarge && category != .accessibilityMedium {
                    heights.append(badge.bounds.height)
                }
                attach(page, name: "pan-account-unverified-\(Int(width))-\(category.rawValue)")
                for index in 1...model.rows.count {
                    let path = IndexPath(row: index, section: 0)
                    page.table.scrollToRow(at: path, at: .top, animated: false)
                    page.table.layoutIfNeeded()
                    let rowCell = try XCTUnwrap(page.table.cellForRow(at: path))
                    rowCell.layoutIfNeeded(); rowCell.contentView.layoutIfNeeded()
                    let rowColumns = try XCTUnwrap(descendants(rowCell).compactMap { $0 as? PanAccountColumns }.first)
                    XCTAssertFalse(rowColumns.hasAmbiguousLayout)
                    XCTAssertEqual(rowColumns.axis, category.isAccessibilityCategory ? .vertical : .horizontal)
                    assertContained(rowColumns, in: rowCell.contentView)
                    if category.isAccessibilityCategory {
                        for column in rowColumns.columns {
                            XCTAssertEqual(column.bounds.width, rowColumns.bounds.width, accuracy: 0.5)
                            assertContained(column, in: rowColumns)
                        }
                        XCTAssertGreaterThan(rowColumns.columns[1].frame.minY, rowColumns.columns[0].frame.maxY)
                        XCTAssertGreaterThan(rowColumns.columns[2].frame.minY, rowColumns.columns[1].frame.maxY)
                    }
                    try assertPlatformFits(in: rowCell, title: model.rows[index - 1].id.title)
                    let rowBadge = try statusBadge(in: rowCell)
                    XCTAssertFalse(rowBadge.hasAmbiguousLayout, "\(width)pt / \(category.rawValue) / \(model.rows[index - 1].id)")
                    try assertTextFits(try statusLabel(in: rowCell), in: rowBadge,
                        maximumLines: category.isAccessibilityCategory ? 1 : 3)
                    assertContained(rowBadge, in: rowColumns.columns[1])
                    let rowBadgeIcon = try XCTUnwrap(descendants(rowBadge).compactMap { $0 as? UIImageView }.first)
                    assertContained(rowBadgeIcon, in: rowBadge)
                    assertSymbolFits(rowBadgeIcon, size: 12)
                    for button in descendants(rowColumns.columns[2]).compactMap({ $0 as? UIButton }) {
                        XCTAssertGreaterThanOrEqual(button.bounds.height, 44)
                        assertContained(button, in: rowColumns.columns[2])
                        let title = try XCTUnwrap(button.titleLabel)
                        XCTAssertEqual(title.text, button.configuration?.title)
                        assertRegular(title)
                        try assertTextFits(title, in: button, maximumLines: 2)
                        XCTAssertEqual(button.convert(title.bounds, from: title).midX, button.bounds.midX, accuracy: 0.5)
                        let headingFrame = header.contentView.convert(actionHeading.bounds, from: actionHeading)
                        let actionFrame = rowCell.contentView.convert(button.bounds, from: button)
                        XCTAssertEqual(headingFrame.midX, actionFrame.midX, accuracy: 0.5)
                    }
                }
            }
        }
        XCTAssertEqual(heights.count, 3)
        XCTAssertGreaterThan(heights[1], heights[0])
        XCTAssertEqual(heights[2], heights[0], accuracy: 0.5)
        XCTAssertEqual(model.rows[0].account?.status, .unverified)
        let calls = await runtime.operations
        XCTAssertTrue(calls.isEmpty)
    }

    func testUnavailableAccountUsesNeutralBadgeWithoutActions() async throws {
        let runtime = try PanUIKitRuntime()
        try runtime.storage.store.write(Data("synthetic-invalid-document".utf8), key: FilmSettingsDocument.key)
        let model = PanAccountsModel(service: runtime.service)
        model.start()
        let page = PanAccountsViewController(model: model, themeProvider: PanAccountsTheme())
        page.loadViewIfNeeded()
        defer { PageViewController.invalidatePages(in: page); model.close() }
        let cell = page.tableView(page.table, cellForRowAt: IndexPath(row: 1, section: 0))
        XCTAssertNil(model.rows[0].account)
        XCTAssertEqual(try statusLabel(in: cell).text, "不可用")
        XCTAssertEqual(try statusLabel(in: cell).textColor, UIColor(page.theme.settings.muted))
        XCTAssertTrue(descendants(cell).compactMap { $0 as? UIButton }.isEmpty)
        let calls = await runtime.operations
        XCTAssertTrue(calls.isEmpty)
    }

    func testTableRefreshCallsRuntimeButLogoutIsNativeAndLoginOpensSharedFloatingCard() async throws {
        let runtime = try PanUIKitRuntime()
        try await runtime.set(.baidu, status: .signedIn)
        let model = PanAccountsModel(service: runtime.service)
        model.start()
        await model.refresh(.baidu)?.value
        let page = PanAccountsViewController(model: model, themeProvider: PanAccountsTheme())
        let window = mount(page)
        defer { unmount(page, window: window); model.close() }
        await eventually { page.canPresentPageAlert && !model.rows.contains(where: \.busy) }
        let before = await runtime.count("refresh")
        let signedIn = page.tableView(page.table, cellForRowAt: IndexPath(row: 1, section: 0))
        let refresh = try button("pan.accounts.baidu.refresh", in: signedIn)
        refresh.sendActions(for: .touchUpInside)
        await eventually { await runtime.count("refresh") == before + 1 && !model.rows[0].busy }
        let fresh = page.tableView(page.table, cellForRowAt: IndexPath(row: 1, section: 0))
        try button("pan.accounts.baidu.logout", in: fresh).sendActions(for: .touchUpInside)
        await eventually { model.rows[0].account?.status == .signedOut }
        let signedOut = page.tableView(page.table, cellForRowAt: IndexPath(row: 1, section: 0))
        try button("pan.accounts.baidu.login", in: signedOut).sendActions(for: .touchUpInside)
        await eventually { page.presentedViewController is PanLoginViewController }
        let card = try XCTUnwrap(page.presentedViewController as? PanLoginViewController)
        await waitForPresentedComponent(card)
        XCTAssertTrue(card.view is AccountFloatingBackdrop)
        XCTAssertEqual(card.modalPresentationStyle, .overFullScreen)
        XCTAssertEqual(card.model.method, .qr)
        XCTAssertFalse(card.hasOwnPageHeader)
        await eventually { card.model.challenge != nil }
        NotificationCenter.default.post(name: UIApplication.willResignActiveNotification, object: nil)
        await eventually { await runtime.count("login.cancel") == 1 }
        XCTAssertNil(card.model.challenge)
        let count = await runtime.count("settings.clear")
        XCTAssertEqual(count, 0, "Even an explicit clear never calls the runtime")
        XCTAssertEqual(try runtime.storage.read().panField("accounts")?.panField("baidu")?.panField("credentials"), .object([:]))
    }

    func testManualFloatingFormBindsFieldsHidesSecretsAndNeverLoadsOldValues() async throws {
        let runtime = try PanUIKitRuntime()
        let model = PanAccountsModel(service: runtime.service)
        model.start()
        let login = try XCTUnwrap(model.openLogin(.tianyi))
        let card = PanLoginViewController(model: login, themeProvider: PanAccountsTheme(), close: { model.closeLogin() })
        let window = mount(card)
        defer { unmount(card, window: window); model.close() }
        await eventually { self.descendants(card.view).compactMap { $0 as? UITextField }.count == 3 }
        let fields = descendants(card.view).compactMap { $0 as? UITextField }
            .sorted { $0.convert(.zero, to: card.view).y < $1.convert(.zero, to: card.view).y }
        XCTAssertEqual(fields.count, 3)
        XCTAssertEqual(fields.map(\.isSecureTextEntry), [true, false, true])
        XCTAssertTrue(fields.allSatisfy { ($0.text ?? "").isEmpty })
        fields[2].text = "  synthetic password  "; fields[2].sendActions(for: .editingChanged)
        await eventually { login.draft["password"] == "  synthetic password  " }
        XCTAssertEqual(login.draft["cookie"], nil)
        let calls = await runtime.operations
        XCTAssertTrue(calls.isEmpty, "Page entry and manual login must never request runtime settings or old secrets")
        NotificationCenter.default.post(name: UIApplication.willResignActiveNotification, object: nil)
        XCTAssertFalse(login.confirmed)
    }

    func testManualNativeSaveClosesCardWithoutClaimingOnlineLogin() async throws {
        let runtime = try PanUIKitRuntime()
        let owner = PanAccountsModel(service: runtime.service)
        owner.start()
        let login = try XCTUnwrap(owner.openLogin(.tianyi))
        var closed = false
        let card = PanLoginViewController(model: login, themeProvider: PanAccountsTheme(), close: { closed = true })
        let window = mount(card)
        defer { unmount(card, window: window); owner.close() }
        await eventually { card.isPageVisible }
        login.draft = ["cookie": "synthetic-offline-cookie"]
        login.save()
        await eventually { closed }
        XCTAssertTrue(login.saved); XCTAssertFalse(login.confirmed)
        XCTAssertEqual(owner.rows[3].account?.status, .unverified)
        XCTAssertTrue(login.draft.isEmpty)
        let calls = await runtime.operations
        XCTAssertTrue(calls.isEmpty)
        owner.closeLogin(); owner.close(); owner.start()
        XCTAssertEqual(owner.rows[3].account?.status, .unverified)
        XCTAssertEqual(owner.rows[3].account?.revision, 1)
    }

    func testQRMethodPickerBindsManualCookieAndCancelsQR() async throws {
        let runtime = try PanUIKitRuntime()
        let provider = try PanProvider(panUIProvider(.quark))
        let model = PanLoginModel(provider: provider, service: runtime.service, update: { _ in })
        let card = PanLoginViewController(model: model, themeProvider: PanAccountsTheme(), close: { model.close() })
        let window = mount(card)
        defer { unmount(card, window: window); model.close() }
        await eventually { model.challenge != nil }
        XCTAssertEqual(UIApplication.shared.applicationState, .active)
        let picker = try XCTUnwrap(descendants(card.view).compactMap { $0 as? UISegmentedControl }.first)
        XCTAssertEqual(picker.numberOfSegments, 2)
        picker.selectedSegmentIndex = 1; picker.sendActions(for: .valueChanged)
        XCTAssertEqual(model.method, .manual)
        await eventually {
            card.view.setNeedsLayout(); card.view.layoutIfNeeded()
            return model.method == .manual && !self.descendants(card.view).compactMap { $0 as? UITextField }.isEmpty
        }
        let field = try XCTUnwrap(descendants(card.view).compactMap { $0 as? UITextField }.first)
        XCTAssertTrue(field.isSecureTextEntry)
        field.text = "synthetic-cookie"; field.sendActions(for: .editingChanged)
        await eventually { model.draft["cookie"] == "synthetic-cookie" }
        await eventually { await runtime.count("login.cancel") == 1 }
        XCTAssertNil(model.challenge)
    }

    func testBackgroundCancelsQRAndConfirmedReceiptClosesCardWithoutClearingAccount() async throws {
        let runtime = try PanUIKitRuntime()
        let owner = PanAccountsModel(service: runtime.service)
        owner.start()
        let page = PanAccountsViewController(model: owner, themeProvider: PanAccountsTheme())
        let window = mount(page)
        defer { unmount(page, window: window); owner.close() }
        await eventually { page.canPresentPageAlert }
        let cell = page.tableView(page.table, cellForRowAt: IndexPath(row: 1, section: 0))
        try button("pan.accounts.baidu.login", in: cell).sendActions(for: .touchUpInside)
        await eventually { page.presentedViewController is PanLoginViewController }
        let card = try XCTUnwrap(page.presentedViewController as? PanLoginViewController)
        await waitForPresentedComponent(card)
        await eventually { card.model.challenge != nil }
        NotificationCenter.default.post(name: UIApplication.willResignActiveNotification, object: nil)
        await eventually { await runtime.count("login.cancel") == 1 }
        await runtime.confirmLogin()
        NotificationCenter.default.post(name: UIApplication.didBecomeActiveNotification, object: nil)
        await eventually { card.model.confirmed && page.presentedViewController == nil }
        XCTAssertEqual(owner.rows[0].account?.status, .signedIn)
        let clears = await runtime.count("settings.clear")
        let cancels = await runtime.count("login.cancel")
        XCTAssertEqual(clears, 0); XCTAssertEqual(cancels, 1)
    }

    func testQRCodeEncodesArbitraryURLAndDecodesPNGAndJPEGByActualType() throws {
        for text in ["https://scan.example.test/login?key=synthetic", "custom-login://local/q?synthetic=1"] {
            let image = try PanQRCode.image(.url(text))
            XCTAssertGreaterThan(image.size.width, 100)
            let detector = try XCTUnwrap(CIDetector(ofType: CIDetectorTypeQRCode, context: CIContext(), options: [:]))
            let code = try XCTUnwrap(CIImage(image: image))
            XCTAssertEqual((detector.features(in: code).first as? CIQRCodeFeature)?.messageString, text)
        }
        let image = UIGraphicsImageRenderer(size: CGSize(width: 12, height: 12)).image { context in
            UIColor.black.setFill(); context.fill(CGRect(x: 0, y: 0, width: 12, height: 12))
        }
        for (bytes, type) in [(try XCTUnwrap(image.pngData()), "image/png"),
                              (try XCTUnwrap(image.jpegData(compressionQuality: 0.8)), "image/jpeg")] {
            let decoded = try PanQRCode.image(.image(bytes, mediaType: type))
            XCTAssertEqual(decoded.size.width, 12 * image.scale)
            XCTAssertNotNil(decoded.cgImage)
        }
        XCTAssertThrowsError(try PanQRCode.image(.image(Data("synthetic-invalid-image".utf8), mediaType: "image/jpeg")))
    }

    private func button(_ id: String, in view: UIView) throws -> UIButton {
        try XCTUnwrap(descendants(view).first { $0.accessibilityIdentifier == id } as? UIButton)
    }
    private func statusBadge(in view: UIView) throws -> SettingsStatusBadge {
        try XCTUnwrap(descendants(view).first { $0.accessibilityIdentifier == "pan.accounts.status" } as? SettingsStatusBadge)
    }
    private func statusLabel(in view: UIView) throws -> UILabel {
        let badge = try statusBadge(in: view)
        return try XCTUnwrap(descendants(badge).compactMap { $0 as? UILabel }.first)
    }
    private func titleColor(_ button: UIButton) throws -> UIColor {
        button.setNeedsLayout(); button.layoutIfNeeded()
        let label = try XCTUnwrap(button.titleLabel)
        if let text = label.attributedText, text.length > 0,
           let color = text.attribute(.foregroundColor, at: 0, effectiveRange: nil) as? UIColor {
            return color
        }
        return label.textColor
    }
    private func assertRegular(_ label: UILabel, style: UIFont.TextStyle = .body,
                               file: StaticString = #filePath, line: UInt = #line) {
        XCTAssertEqual(label.font, UIFont.preferredFont(forTextStyle: style, compatibleWith: label.traitCollection),
            file: file, line: line)
        XCTAssertFalse(label.font.fontDescriptor.symbolicTraits.contains(.traitBold), file: file, line: line)
        XCTAssertFalse(label.font.fontDescriptor.symbolicTraits.contains(.traitItalic), file: file, line: line)
        XCTAssertTrue(label.adjustsFontForContentSizeCategory, file: file, line: line)
        XCTAssertFalse(label.adjustsFontSizeToFitWidth, file: file, line: line)
    }
    private func assertContained(_ child: UIView, in owner: UIView, file: StaticString = #filePath, line: UInt = #line) {
        let frame = owner.convert(child.bounds, from: child)
        XCTAssertGreaterThan(child.bounds.width, 0, file: file, line: line)
        XCTAssertGreaterThan(child.bounds.height, 0, file: file, line: line)
        XCTAssertGreaterThanOrEqual(frame.minX, owner.bounds.minX - 0.5, file: file, line: line)
        XCTAssertGreaterThanOrEqual(frame.minY, owner.bounds.minY - 0.5, file: file, line: line)
        XCTAssertLessThanOrEqual(frame.maxX, owner.bounds.maxX + 0.5, file: file, line: line)
        XCTAssertLessThanOrEqual(frame.maxY, owner.bounds.maxY + 0.5, file: file, line: line)
    }
    private func assertTextFits(_ label: UILabel, in owner: UIView, maximumLines: Int,
                                file: StaticString = #filePath, line: UInt = #line) throws {
        assertContained(label, in: owner, file: file, line: line)
        let text = try XCTUnwrap(label.text, file: file, line: line)
        if let rendered = label.attributedText {
            XCTAssertEqual(rendered.string, text, file: file, line: line)
        }
        // UILabel's native text rect excludes the extra font leading used by
        // NSAttributedString.boundingRect. An unlimited wrapping probe still
        // measures every character, independently of the visible label's cap.
        let probe = UILabel()
        probe.font = label.font; probe.text = text
        if let rendered = label.attributedText { probe.attributedText = rendered }
        probe.numberOfLines = 0; probe.lineBreakMode = .byWordWrapping
        let fittingBounds = CGRect(x: 0, y: 0, width: label.bounds.width, height: .greatestFiniteMagnitude)
        let required = probe.textRect(forBounds: fittingBounds, limitedToNumberOfLines: 0)
        let limited = probe.textRect(forBounds: fittingBounds, limitedToNumberOfLines: maximumLines)
        XCTAssertLessThanOrEqual(required.width, label.bounds.width + 0.5, file: file, line: line)
        XCTAssertLessThanOrEqual(required.height, label.bounds.height + 0.5, file: file, line: line)
        XCTAssertEqual(required.height, limited.height, accuracy: 0.5, file: file, line: line)
        let visible = label.textRect(forBounds: label.bounds, limitedToNumberOfLines: label.numberOfLines)
        XCTAssertEqual(visible.height, required.height, accuracy: 0.5, file: file, line: line)
        XCTAssertGreaterThanOrEqual(visible.minX, label.bounds.minX - 0.5, file: file, line: line)
        XCTAssertGreaterThanOrEqual(visible.minY, label.bounds.minY - 0.5, file: file, line: line)
        XCTAssertLessThanOrEqual(visible.maxX, label.bounds.maxX + 0.5, file: file, line: line)
        XCTAssertLessThanOrEqual(visible.maxY, label.bounds.maxY + 0.5, file: file, line: line)
    }
    private func assertSymbolFits(_ icon: UIImageView, size: CGFloat,
                                  file: StaticString = #filePath, line: UInt = #line) {
        // SF Symbols have optical alignment insets: the constrained square
        // alignment rect need not be the image view's non-square drawing bounds.
        XCTAssertEqual(icon.alignmentRect(forFrame: icon.bounds).size, CGSize(width: size, height: size),
            file: file, line: line)
        XCTAssertEqual(max(icon.bounds.width, icon.bounds.height), size, accuracy: 0.5, file: file, line: line)
        XCTAssertGreaterThan(icon.bounds.width, 0, file: file, line: line)
        XCTAssertGreaterThan(icon.bounds.height, 0, file: file, line: line)
        XCTAssertEqual(icon.contentMode, .scaleAspectFit, file: file, line: line)
        XCTAssertNotNil(icon.image, file: file, line: line)
    }
    private func assertPlatformFits(in cell: UITableViewCell, title expectedTitle: String,
                                    file: StaticString = #filePath, line: UInt = #line) throws {
        let platform = try XCTUnwrap(descendants(cell).first { $0.accessibilityIdentifier == "pan.accounts.platform" },
            file: file, line: line)
        let title = try XCTUnwrap(descendants(platform).compactMap { $0 as? UILabel }.first, file: file, line: line)
        let icon = try XCTUnwrap(descendants(platform).compactMap { $0 as? UIImageView }.first, file: file, line: line)
        XCTAssertEqual(title.text, expectedTitle, file: file, line: line)
        assertRegular(title, file: file, line: line)
        try assertTextFits(title, in: platform, maximumLines: 2, file: file, line: line)
        assertContained(platform, in: cell.contentView, file: file, line: line)
        assertContained(icon, in: platform, file: file, line: line)
        assertContained(icon, in: cell.contentView, file: file, line: line)
        assertContained(icon, in: try XCTUnwrap(cell.backgroundView, file: file, line: line), file: file, line: line)
        let titleFrame = platform.convert(title.bounds, from: title)
        let iconFrame = platform.convert(icon.bounds, from: icon)
        XCTAssertGreaterThanOrEqual(titleFrame.minX, iconFrame.maxX + 7.5, file: file, line: line)
        assertSymbolFits(icon, size: 24, file: file, line: line)
        XCTAssertFalse(icon.isAccessibilityElement, file: file, line: line)
    }
    private func descendants(_ view: UIView) -> [UIView] { [view] + view.subviews.flatMap(descendants) }
    private func mount(_ page: UIViewController) -> UIWindow {
        let scene = UIApplication.shared.connectedScenes.compactMap { $0 as? UIWindowScene }.first
        previousKeyWindow = scene?.windows.first { $0.isKeyWindow }
        let window = scene.map { UIWindow(windowScene: $0) } ?? UIWindow(frame: .zero)
        window.frame = CGRect(x: 0, y: 0, width: 390, height: 844)
        window.rootViewController = page; window.makeKeyAndVisible()
        page.view.layoutIfNeeded()
        return window
    }
    private func waitForPresentedComponent(_ page: PageViewController) async {
        await eventually { page.isPageActive && page.viewIfLoaded?.window != nil && !page.isBeingPresented }
        await eventually { page.canPresentPageAlert }
    }
    private func unmount(_ page: UIViewController, window: UIWindow) {
        if let modal = page.presentedViewController { PageViewController.invalidatePages(in: modal) }
        PageViewController.invalidatePages(in: page)
        window.isHidden = true; window.rootViewController = nil
        previousKeyWindow?.makeKey(); previousKeyWindow = nil
    }
    private func attach(_ page: UIViewController, name: String) {
        let image = UIGraphicsImageRenderer(bounds: page.view.bounds).image { _ in
            XCTAssertTrue(page.view.drawHierarchy(in: page.view.bounds, afterScreenUpdates: true))
        }
        let attachment = XCTAttachment(image: image)
        attachment.name = name; attachment.lifetime = .keepAlways
        add(attachment)
    }
    private func eventually(_ predicate: @escaping @MainActor () async -> Bool,
                            file: StaticString = #filePath, line: UInt = #line) async {
        let deadline = Date().addingTimeInterval(3)
        while Date() < deadline {
            if await predicate() { return }
            try? await Task.sleep(for: .milliseconds(10))
        }
        XCTFail("Pan UIKit did not settle", file: file, line: line)
    }
}

@MainActor
private final class PanAccountsTheme: ObservableObject, PageThemeProviding {
    @Published private(set) var pagePalette: PagePalette = .preset(.lilac)
    let animatesPageColorway = false
    var pageThemeChanges: AnyPublisher<Void, Never> { objectWillChange.eraseToAnyPublisher() }
    func setColorway(_ value: PageColorway) { pagePalette = .preset(value) }
}

private func panUIProvider(_ id: PanProviderID) -> RuntimeValue {
    let fields: [String]
    switch id {
    case .baidu, .quark, .uc: fields = ["cookie"]
    case .tianyi: fields = ["cookie", "username", "password"]
    case .yidong: fields = ["authorization", "refreshedAt"]
    case .quarkTV, .ucTV: fields = ["refreshToken", "deviceId", "accessToken", "expiresAt"]
    }
    return .object(["provider": .string(id.rawValue), "credentialFields": .array(fields.map(RuntimeValue.string)),
                    "login": .bool([PanProviderID.baidu, .quark, .uc].contains(id)), "refresh": .bool(true)])
}
private func panUICredentials(_ id: PanProviderID) -> RuntimeValue {
    switch id {
    case .baidu, .quark, .uc, .tianyi: return .object(["cookie": .string("synthetic-ui-cookie")])
    case .yidong: return .object(["authorization": .string("synthetic-ui-authorization")])
    case .quarkTV, .ucTV: return .object(["refreshToken": .string("synthetic-ui-refresh"), "deviceId": .string("synthetic-ui-device")])
    }
}
private func panUIAccount(_ id: PanProviderID, _ status: PanAccountStatus = .signedOut, revision: Int = 0,
                          fields: [String]? = nil) -> RuntimeValue {
    let fields = fields ?? (status == .signedOut ? [] : panUICredentials(id).panObject!.keys.sorted())
    return .object(["provider": .string(id.rawValue), "revision": .number(Double(revision)),
             "configured": .bool(!fields.isEmpty), "fields": .array(fields.map(RuntimeValue.string)),
             "status": .string(status.rawValue)])
}
private final class PanUIKitKeys: EncryptedDocumentKeyProvider, @unchecked Sendable {
    private let lock = NSLock()
    private var key: Data?
    func readKey(namespace: String) throws -> Data? { lock.lock(); defer { lock.unlock() }; return key }
    func createKeyIfAbsent(namespace: String) throws -> Data {
        lock.lock(); defer { lock.unlock() }
        if key == nil { key = Data(repeating: 0x57, count: 32) }
        return key!
    }
}
private final class PanUIKitStorage: @unchecked Sendable {
    let root: URL
    let store: EncryptedDocumentStore
    let document: FilmSettingsDocument
    init() throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent("pan-ui-native-" + UUID().uuidString)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: false)
        let store = try EncryptedDocumentStore(baseURL: root, namespace: FilmSettingsBlock.panAccounts.namespace,
                                               keyProvider: PanUIKitKeys())
        self.root = root; self.store = store
        document = FilmSettingsDocument(block: .panAccounts, storage: .protected { store })
    }
    func read() throws -> RuntimeValue { try document.read() ?? .object(["version": .number(1), "accounts": .object([:])]) }
    func replace(_ id: PanProviderID, revision: Int, configured: Bool) throws -> RuntimeValue {
        var accounts = try read().panField("accounts")!.panObject!
        accounts[id.rawValue] = .object(["revision": .number(Double(revision)), "credentials":
            configured ? panUICredentials(id) : .object([:])])
        return .object(["version": .number(1), "accounts": .object(accounts)])
    }
    func snapshot(_ id: PanProviderID) throws -> RuntimeValue {
        let record = try read().panField("accounts")?.panField(id.rawValue)
        let fields = (record?.panField("credentials")?.panObject ?? RuntimeObject()).filter { _, value in
            if let text = value.panString { return !text.isEmpty }; return (value.panInteger ?? 0) > 0
        }.map(\.key).sorted()
        return panUIAccount(id, fields.isEmpty ? .signedOut : .signedIn,
                            revision: Int(record?.panField("revision")?.panInteger ?? 0), fields: fields)
    }
    deinit { try? FileManager.default.removeItem(at: root) }
}
private actor PanUIKitRuntime {
    nonisolated let storage: PanUIKitStorage
    private(set) var operations: [String] = []
    private var states: [PanProviderID: (record: RuntimeValue, status: PanAccountStatus)] = [:]
    private var sessionProvider = PanProviderID.baidu
    private var expected: RuntimeValue?
    private var confirmed = false
    init() throws { storage = try PanUIKitStorage() }
    nonisolated var service: PanAccountService {
        .init(document: storage.document, request: { try await self.call($0, $1, $2) })
    }
    func set(_ id: PanProviderID, status: PanAccountStatus) throws {
        let value = try storage.replace(id, revision: 1, configured: status != .signedOut)
        try storage.store.write(RuntimeJSON.encode(value), key: FilmSettingsDocument.key)
        states[id] = (value.panField("accounts")!.panField(id.rawValue)!, status)
    }
    func count(_ operation: String) -> Int { operations.filter { $0 == operation }.count }
    func confirmLogin() { confirmed = true }
    func call(_ operation: String, _ parameters: RuntimeValue, _ timeout: TimeInterval?) throws -> RuntimeValue {
        operations.append(operation)
        let id = parameters.panField("provider")?.panString.flatMap(PanProviderID.init(rawValue:)) ?? sessionProvider
        switch operation {
        case "refresh":
            let record = try storage.read().panField("accounts")?.panField(id.rawValue)
            if states[id]?.status == .expired, states[id]?.record == record { throw RuntimeHTTPError.remote("AUTH_FAILED") }
            return .object(["refreshed": .bool(true), "account": try storage.snapshot(id)])
        case "login.start":
            sessionProvider = id
            expected = try storage.document.snapshot().fingerprint
            return .object(["sessionId": .string("synthetic-ui-session"), "provider": .string(id.rawValue),
                "status": .string("pending"), "expiresAt": .number((Date().timeIntervalSince1970 * 1000).rounded() + 180_000),
                "challenge": .object(["type": .string("url"), "url": .string("https://scan.example.test/synthetic")])])
        case "login.poll":
            var value: RuntimeObject = ["sessionId": .string("synthetic-ui-session"),
                "status": .string(confirmed ? "confirmed" : "pending")]
            if confirmed {
                guard let expected else { throw RuntimeHTTPError.remote("LOGIN_EXPIRED") }
                let document = try storage.replace(id, revision: 2, configured: true)
                try PanAccountService.validateSettingsDocument(document)
                _ = try storage.document.compareAndSwap(expected: expected, document: document)
                self.expected = try storage.document.snapshot().fingerprint
                value["account"] = try storage.snapshot(id)
            }
            return .object(value)
        case "login.cancel": return .object(["cancelled": .bool(true)])
        default:
            XCTFail("Native account settings cannot send \(operation) to runtime")
            throw RuntimeHTTPError.invalidRequest
        }
    }
}
#endif
