import XCTest
import UIKit
import Combine
import SwiftUI
@testable import MeowUI

@MainActor
final class MeowUIPlatformTests: XCTestCase {
    private var window: UIWindow?
    private weak var previousKeyWindow: UIWindow?

    func testSettingsCardsRoundOnlyOuterCornersAndKeepDividersInsideTheGroup() throws {
        for dark in [false, true] {
            let theme = PageTheme(palette: .coral, dark: dark)
            for width: CGFloat in [288, 358] {
                for selected in [false, true] {
                    let card = UIView(frame: CGRect(x: 0, y: 0, width: width, height: 168))
                    card.isOpaque = false
                    for index in 0..<3 {
                        let row = SettingsRowBackgroundView(theme: theme, first: index == 0,
                            last: index == 2, selected: selected, showsSeparator: index < 2)
                        row.frame = CGRect(x: 0, y: CGFloat(index) * 56, width: width, height: 56)
                        card.addSubview(row)
                        row.layoutIfNeeded()
                    }
                    let image = renderSettingsCard(card)
                    let surface = selected ? theme.settings.selected : theme.settings.surface
                    let surfacePixel = [surface.red, surface.green, surface.blue, surface.alpha]
                    let divider = theme.settings.separator
                    let dividerPixel = [divider.red, divider.green, divider.blue, divider.alpha]
                    for x in [CGFloat(1), width - 1] {
                        XCTAssertEqual(try settingsPixel(image, x: x, y: 1)[3], 0)
                        XCTAssertEqual(try settingsPixel(image, x: x, y: 167)[3], 0)
                    }
                    XCTAssertEqual(try settingsPixel(image, x: 2.5, y: 8.5)[3], 255)
                    XCTAssertEqual(try settingsPixel(image, x: 2.5, y: 159.5)[3], 255)
                    for y: CGFloat in [55.75, 111.75] {
                        XCTAssertEqual(try settingsPixel(image, x: 8, y: y), surfacePixel,
                            "An internal row must not add a full-width border")
                        XCTAssertEqual(try settingsPixel(image, x: width / 2, y: y), dividerPixel)
                        XCTAssertEqual(try settingsPixel(image, x: 8, y: y + 0.5), surfacePixel,
                            "The next row must join the card without another rounded corner")
                    }
                }
            }
        }
    }

    func testSwiftUISettingsBackgroundMatchesUIKitAfterThemeAndGroupPositionChanges() async throws {
        let light = PageTheme(palette: .sky)
        let host = UIHostingController(rootView:
            SettingsRowBackground(theme: light, first: true, last: false, showsSeparator: true)
                .frame(width: 288, height: 56))
        host.view.backgroundColor = .clear
        try display(host)
        for (theme, first, last, separator) in [
            (light, true, false, true),
            (PageTheme(palette: .coral, dark: true), false, true, false),
            (light, true, true, false),
        ] {
            let reference = SettingsRowBackgroundView(theme: theme, first: first, last: last,
                showsSeparator: separator)
            reference.frame = CGRect(x: 0, y: 0, width: 288, height: 56)
            reference.layoutIfNeeded()
            let expected = try XCTUnwrap(renderSettingsCard(reference).pngData())
            host.rootView = SettingsRowBackground(theme: theme, first: first, last: last,
                showsSeparator: separator).frame(width: 288, height: 56)
            let rendered = expectation(for: NSPredicate { _, _ in
                MainActor.assumeIsolated {
                    host.view.layoutIfNeeded()
                    guard let background = self.descendants(host.view)
                        .compactMap({ $0 as? SettingsRowBackgroundView }).first,
                          background.bounds.size == reference.bounds.size else { return false }
                    return self.renderSettingsCard(background).pngData() == expected
                }
            }, evaluatedWith: host)
            await fulfillment(of: [rendered], timeout: 2)
        }
    }

    func testManagementRowsPreserveReaderLayoutAndActionsAcrossThemesAndDynamicTypeAt320Points() async throws {
        let host = UIViewController(), manager = ManagementListView()
        host.loadViewIfNeeded(); host.view.addSubview(manager)
        manager.translatesAutoresizingMaskIntoConstraints = false
        NSLayoutConstraint.activate([
            manager.centerXAnchor.constraint(equalTo: host.view.centerXAnchor),
            manager.topAnchor.constraint(equalTo: host.view.safeAreaLayoutGuide.topAnchor),
            manager.widthAnchor.constraint(equalToConstant: 320),
            manager.heightAnchor.constraint(equalToConstant: 620),
        ])
        let window = try display(host)
        var edits = 0, changes: [Bool] = [], deletions = 0
        manager.onDelete = { deletions += 1 }
        let rows: [ManagementListView.Row] = [
            .init(id: "a", title: "书源", detail: "正常", enabled: true,
                  edit: { edits += 1 }, toggle: { changes.append($0) },
                  menu: UIMenu(children: [UIAction(title: "检测") { _ in }])),
            .init(id: "b", title: "来源二", detail: "失败", enabled: false, failed: true,
                  edit: {}, toggle: { _ in }, menu: UIMenu(children: [UIAction(title: "检测") { _ in }])),
        ]
        let table = try XCTUnwrap(descendants(manager).compactMap { $0 as? UITableView }.first)
        for category in [UIContentSizeCategory.large, .accessibilityExtraExtraExtraLarge] {
            host.traitOverrides.preferredContentSizeCategory = category
            await Task.yield()
            for dark in [false, true] {
                window.overrideUserInterfaceStyle = dark ? .dark : .light
                let theme = PageTheme(palette: .coral, dark: dark)
                manager.applyTheme(theme)
                manager.setRows(rows, total: 2, validIDs: ["a", "b"], emptyMessage: "无来源")
                host.view.layoutIfNeeded(); table.layoutIfNeeded()
                XCTAssertEqual(table.frame.minX, 24, accuracy: 0.5)
                XCTAssertEqual(table.backgroundColor, UIColor(theme.settings.surface))
                XCTAssertEqual(table.separatorStyle, .singleLine)
                XCTAssertEqual(table.separatorColor, UIColor(theme.settings.separator))
                XCTAssertEqual(table.layer.cornerRadius, 22)
                XCTAssertTrue(table.clipsToBounds)
                for index in 0..<2 {
                    let cell = try XCTUnwrap(table.cellForRow(at: IndexPath(row: index, section: 0)))
                    let labels = descendants(cell.contentView).compactMap { $0 as? UILabel }
                    let title = try XCTUnwrap(labels.first { $0.text == rows[index].title })
                    let detail = try XCTUnwrap(labels.first { $0.text == rows[index].detail })
                    XCTAssertTrue(title.adjustsFontForContentSizeCategory)
                    XCTAssertTrue(detail.adjustsFontForContentSizeCategory)
                    XCTAssertEqual(title.font.pointSize,
                        UIFontMetrics.default.scaledFont(for: .systemFont(ofSize: 14), compatibleWith: cell.traitCollection).pointSize)
                    XCTAssertEqual(detail.font.pointSize,
                        UIFontMetrics.default.scaledFont(for: .systemFont(ofSize: 11), compatibleWith: cell.traitCollection).pointSize)
                    XCTAssertEqual(title.textColor, UIColor(theme.settings.ink))
                    XCTAssertEqual(detail.textColor, UIColor(index == 1 ? theme.settings.danger : theme.settings.muted))
                    for label in [title, detail] {
                        let needed = label.sizeThatFits(.init(width: label.bounds.width, height: .greatestFiniteMagnitude))
                        XCTAssertGreaterThanOrEqual(label.bounds.height + 1, needed.height)
                    }
                    let controls = descendants(cell.contentView).compactMap { $0 as? UIControl }
                    let toggle = try XCTUnwrap(controls.compactMap { $0 as? UISwitch }.first)
                    XCTAssertEqual(toggle.transform, CGAffineTransform(scaleX: 0.7, y: 0.7))
                    XCTAssertEqual(toggle.isOn, index == 0)
                    XCTAssertEqual(toggle.onTintColor, UIColor(theme.settings.accent))
                    let edit = try XCTUnwrap(controls.compactMap { $0 as? UIButton }
                        .first { $0.accessibilityLabel == "编辑 \(rows[index].title)" })
                    let more = try XCTUnwrap(controls.compactMap { $0 as? UIButton }.first { $0.menu != nil })
                    let select = try XCTUnwrap(controls.compactMap { $0 as? UIButton }
                        .first { $0.accessibilityLabel == "选择 \(rows[index].title)" })
                    for button in [edit, more] {
                        XCTAssertEqual(button.bounds.size, CGSize(width: 38, height: 38))
                        XCTAssertEqual(button.tintColor, UIColor(theme.settings.accent))
                    }
                    XCTAssertEqual(select.bounds.size, CGSize(width: 32, height: 44))
                    XCTAssertEqual(more.convert(more.bounds, to: cell.contentView).minX -
                        edit.convert(edit.bounds, to: cell.contentView).maxX, 4, accuracy: 0.5)
                    for control in [select as UIControl, toggle, edit, more] {
                        let frame = control.convert(control.bounds, to: cell.contentView)
                        XCTAssertGreaterThanOrEqual(frame.minX, 10 - 0.5)
                        XCTAssertLessThanOrEqual(frame.maxX, cell.contentView.bounds.width - 10 + 0.5)
                        XCTAssertFalse(control.isHidden); XCTAssertTrue(control.isEnabled)
                    }
                }
                XCTAssertEqual(manager.search.searchTextField.textColor, UIColor(theme.settings.ink))
                XCTAssertEqual(manager.search.searchTextField.backgroundColor, UIColor(theme.settings.input))
                XCTAssertTrue(manager.search.searchTextField.adjustsFontForContentSizeCategory)
                for button in descendants(manager).compactMap({ $0 as? UIButton }).filter({
                    ["management.selectAll", "management.invert", "management.delete"].contains($0.accessibilityIdentifier ?? "")
                }) {
                    XCTAssertTrue(button.titleLabel?.adjustsFontForContentSizeCategory == true)
                    let parent = try XCTUnwrap(button.superview)
                    let frame = button.convert(button.bounds, to: parent)
                    XCTAssertGreaterThanOrEqual(frame.minX, -0.5)
                    XCTAssertLessThanOrEqual(frame.maxX, parent.bounds.width + 0.5)
                    XCTAssertGreaterThanOrEqual(frame.minY, -0.5)
                    XCTAssertLessThanOrEqual(frame.maxY, parent.bounds.height + 0.5)
                }
            }
        }
        let cell = try XCTUnwrap(table.cellForRow(at: IndexPath(row: 0, section: 0)))
        let edit = try XCTUnwrap(descendants(cell).compactMap { $0 as? UIButton }
            .first { $0.accessibilityLabel == "编辑 书源" })
        edit.sendActions(for: .touchUpInside); XCTAssertEqual(edits, 1)
        let toggle = try XCTUnwrap(descendants(cell).compactMap { $0 as? UISwitch }.first)
        toggle.setOn(false, animated: false); toggle.sendActions(for: .valueChanged)
        XCTAssertEqual(changes, [false])
        let select = try XCTUnwrap(descendants(cell).compactMap { $0 as? UIButton }
            .first { $0.accessibilityLabel == "选择 书源" })
        select.sendActions(for: .touchUpInside); XCTAssertEqual(manager.selected, ["a"])
        XCTAssertTrue(manager.deleteButton.isEnabled)
        manager.deleteButton.sendActions(for: .touchUpInside); XCTAssertEqual(deletions, 1)
        let selectAll = try XCTUnwrap(descendants(manager).compactMap { $0 as? UIButton }
            .first { $0.accessibilityIdentifier == "management.selectAll" })
        selectAll.sendActions(for: .touchUpInside); XCTAssertEqual(manager.selected, ["a", "b"])
        let invert = try XCTUnwrap(descendants(manager).compactMap { $0 as? UIButton }
            .first { $0.accessibilityIdentifier == "management.invert" })
        invert.sendActions(for: .touchUpInside); XCTAssertTrue(manager.selected.isEmpty)
        XCTAssertFalse(manager.deleteButton.isEnabled)
        manager.setRows([rows[0], .init(id: "status", title: "运行器不可用", enabled: false,
            showsSelection: false, showsToggle: false, toggle: { _ in }, menu: UIMenu(children: []))],
            total: 1, validIDs: ["a"], emptyMessage: "无来源")
        selectAll.sendActions(for: .touchUpInside)
        XCTAssertEqual(manager.selected, ["a"], "Status rows must never join batch selection")
    }

    func testManagementFormsKeepFieldsAndChecksReadableAt320PointsAcrossDynamicTypeAndThemes() async throws {
        let host = UIViewController(), window = try display(host)
        let longLabel = "排除范围，选填书名或者书源 URL（excludeScope），较长的字段标签需要完整显示"
        let longValue = String(repeating: "https://example.invalid/很长的规则内容/没有空格的文本\n", count: 60)
        for boxed in [false, true] {
            host.traitOverrides.preferredContentSizeCategory = .large
            let form = ManagementFormView(); form.boxedInputs = boxed
            let name = try XCTUnwrap(form.text(longLabel, value: "书源名称", id: "form.name", changed: { _ in }) as? UITextField)
            let pattern = try XCTUnwrap(form.text("替换规则（pattern）", value: longValue, rows: 3,
                id: "form.pattern", changed: { _ in }) as? UITextView)
            let result = try XCTUnwrap(form.text("替换为（replacement）", value: longValue, rows: 6,
                id: "form.replacement", changed: { _ in }) as? UITextView)
            let enabled = ManagementFormView.check("使用正则表达式，较长的复选标签仍需完整显示并允许勾选",
                value: true, round: boxed, changed: { _ in })
            form.addSection(enabled)
            let titleScope = ManagementFormView.check("作用于标题", value: true, round: true, changed: { _ in })
            let contentScope = ManagementFormView.check("作用于正文", value: false, round: true, changed: { _ in })
            let checks = UIStackView(arrangedSubviews: [titleScope, contentScope, UIView()])
            checks.spacing = 14; form.addSection(checks)
            let tail = form.text("超时毫秒数（timeoutMillisecond）", value: "3000", id: "form.timeout", changed: { _ in })
            installManagementForm(form, in: host)
            for category in [UIContentSizeCategory.large, .accessibilityExtraExtraExtraLarge] {
                host.traitOverrides.preferredContentSizeCategory = category
                await Task.yield()
                for dark in [false, true] {
                    window.overrideUserInterfaceStyle = dark ? .dark : .light
                    let theme = PageTheme(palette: .coral, dark: dark), settings = theme.settings
                    form.applyTheme(theme)
                    window.layoutIfNeeded(); host.view.layoutIfNeeded(); form.layoutIfNeeded()
                    XCTAssertEqual(form.bounds.width, 320, accuracy: 0.5)
                    XCTAssertEqual(form.traitCollection.preferredContentSizeCategory, category)
                    XCTAssertFalse(form.fields.hasAmbiguousLayout)
                    XCTAssertLessThanOrEqual(form.contentSize.width, form.bounds.width + 0.5)
                    XCTAssertGreaterThan(form.contentSize.height, form.bounds.height)
                    XCTAssertEqual(form.keyboardDismissMode, .interactive)
                    XCTAssertEqual(form.fields.backgroundColor, UIColor(settings.surface))
                    let fieldLabels = [longLabel, "替换规则（pattern）", "替换为（replacement）", "超时毫秒数（timeoutMillisecond）"]
                    let labels = descendants(form.fields).compactMap { $0 as? UILabel }
                        .filter { fieldLabels.contains($0.text ?? "") }
                        + [enabled, titleScope, contentScope].compactMap(\.titleLabel)
                    XCTAssertEqual(labels.count, 7)
                    for label in labels where !(label.text ?? "").isEmpty {
                        XCTAssertTrue(label.adjustsFontForContentSizeCategory)
                        XCTAssertGreaterThan(label.bounds.width, 0)
                        let needed = label.sizeThatFits(CGSize(width: label.bounds.width, height: .greatestFiniteMagnitude))
                        XCTAssertGreaterThanOrEqual(label.bounds.height + 1, needed.height,
                            "Labels and check titles must fit their full text")
                        if let parent = label.superview as? UIButton {
                            let frame = label.convert(label.bounds, to: parent)
                            XCTAssertGreaterThanOrEqual(frame.minX, -0.5)
                            XCTAssertLessThanOrEqual(frame.maxX, parent.bounds.width + 0.5)
                            XCTAssertGreaterThanOrEqual(frame.minY, -0.5)
                            XCTAssertLessThanOrEqual(frame.maxY, parent.bounds.height + 0.5)
                        }
                    }
                    let label = try XCTUnwrap(labels.first { $0.text == longLabel })
                    let baseInputSize: CGFloat = boxed ? 13 : 17, baseLabelSize: CGFloat = boxed ? 13 : 12
                    if category == .large {
                        XCTAssertEqual(name.font?.pointSize, baseInputSize)
                        XCTAssertEqual(pattern.font?.pointSize, baseInputSize)
                        XCTAssertEqual(label.font.pointSize, baseLabelSize)
                        XCTAssertEqual(enabled.titleLabel?.font.pointSize, boxed ? 14 : 13)
                    } else {
                        XCTAssertGreaterThan(try XCTUnwrap(name.font).pointSize, baseInputSize)
                        XCTAssertGreaterThan(try XCTUnwrap(pattern.font).pointSize, baseInputSize)
                        XCTAssertGreaterThan(label.font.pointSize, baseLabelSize)
                        XCTAssertGreaterThan(try XCTUnwrap(enabled.titleLabel?.font).pointSize, boxed ? 14 : 13)
                    }
                    XCTAssertTrue(name.adjustsFontForContentSizeCategory); XCTAssertTrue(name.isEnabled)
                    XCTAssertGreaterThanOrEqual(name.bounds.height + 1, try XCTUnwrap(name.font).lineHeight)
                    XCTAssertEqual(name.textColor, UIColor(settings.ink))
                    XCTAssertEqual(label.textColor, UIColor(settings.muted))
                    for (text, rows) in [(pattern, 3), (result, 6)] {
                        XCTAssertTrue(text.adjustsFontForContentSizeCategory)
                        XCTAssertTrue(text.isEditable); XCTAssertTrue(text.isScrollEnabled)
                        XCTAssertGreaterThanOrEqual(text.bounds.height + 1,
                            try XCTUnwrap(text.font).lineHeight * CGFloat(rows) + text.textContainerInset.top + text.textContainerInset.bottom)
                        XCTAssertGreaterThan(text.contentSize.height, text.bounds.height)
                        XCTAssertLessThanOrEqual(text.contentSize.width, text.bounds.width + 1)
                        XCTAssertEqual(text.textColor, UIColor(settings.ink))
                    }
                    XCTAssertGreaterThan(result.bounds.height, pattern.bounds.height)
                    for input in [name as UIView, pattern, result, enabled, titleScope, contentScope, tail] {
                        let frame = input.convert(input.bounds, to: form.fields)
                        XCTAssertGreaterThanOrEqual(frame.minX, -0.5)
                        XCTAssertLessThanOrEqual(frame.maxX, form.fields.bounds.width + 0.5)
                        XCTAssertGreaterThan(input.bounds.height, 0)
                    }
                    let titleFrame = titleScope.convert(titleScope.bounds, to: checks)
                    let contentFrame = contentScope.convert(contentScope.bounds, to: checks)
                    XCTAssertEqual(checks.axis, category.isAccessibilityCategory ? .vertical : .horizontal)
                    XCTAssertEqual(checks.arrangedSubviews.last?.isHidden, category.isAccessibilityCategory)
                    XCTAssertTrue(titleFrame.intersection(contentFrame).isNull)
                    for button in [enabled, titleScope, contentScope] {
                        XCTAssertTrue(button.isEnabled)
                        XCTAssertEqual(button.titleColor(for: .normal), UIColor(settings.ink))
                    }
                    if boxed {
                        for input in [name as UIView, pattern, result] {
                            XCTAssertEqual(input.backgroundColor, UIColor(settings.input))
                            XCTAssertEqual(input.layer.borderColor, UIColor(settings.inputBorder).cgColor)
                        }
                    } else {
                        let separators = form.fields.arrangedSubviews.filter { !($0 is UIStackView) && !($0 is UIButton) }
                        XCTAssertFalse(separators.isEmpty)
                        for separator in separators {
                            XCTAssertEqual(separator.backgroundColor?.resolvedColor(with: form.traitCollection), UIColor(settings.separator))
                            XCTAssertGreaterThan(separator.bounds.height, 0)
                            XCTAssertEqual(separator.bounds.width, form.fields.bounds.width, accuracy: 0.5)
                        }
                    }
                    form.scrollRectToVisible(tail.convert(tail.bounds, to: form), animated: false)
                    form.layoutIfNeeded()
                    let tailFrame = tail.convert(tail.bounds, to: form)
                    XCTAssertGreaterThanOrEqual(tailFrame.minY, form.bounds.minY - 0.5)
                    XCTAssertLessThanOrEqual(tailFrame.maxY, form.bounds.maxY + 0.5)
                }
            }
            form.removeFromSuperview()
        }
    }

    func testManagementFormEditingCallbacksKeepValuesForSaveAcrossTypeAndThemeChanges() async throws {
        let host = UIViewController(), window = try display(host)
        for boxed in [false, true] {
            host.traitOverrides.preferredContentSizeCategory = .large
            let form = ManagementFormView(); form.boxedInputs = boxed
            var values: [String: String] = [:], changes: [String] = [], selections: [Bool] = []
            let name = try XCTUnwrap(form.text("名称", value: "原名称", id: "form.name") {
                values["name"] = $0; changes.append("name")
            } as? UITextField)
            let pattern = try XCTUnwrap(form.text("替换规则", value: "原规则", rows: 3, id: "form.pattern") {
                values["pattern"] = $0; changes.append("pattern")
            } as? UITextView)
            let check = ManagementFormView.check("使用正则表达式", value: true, round: boxed) { selections.append($0) }
            form.addSection(check); installManagementForm(form, in: host)
            window.layoutIfNeeded(); host.view.layoutIfNeeded()
            name.text = "已编辑名称"; name.sendActions(for: .editingChanged)
            pattern.text = "第一行\n第二行"; pattern.delegate?.textViewDidChange?(pattern)
            check.sendActions(for: .touchUpInside)
            XCTAssertEqual(values, ["name": "已编辑名称", "pattern": "第一行\n第二行"])
            XCTAssertEqual(changes, ["name", "pattern"]); XCTAssertEqual(selections, [false])
            host.traitOverrides.preferredContentSizeCategory = .accessibilityExtraExtraExtraLarge
            window.overrideUserInterfaceStyle = .dark
            await Task.yield()
            form.applyTheme(PageTheme(dark: true))
            window.layoutIfNeeded(); host.view.layoutIfNeeded(); form.layoutIfNeeded()
            XCTAssertEqual(name.text, "已编辑名称"); XCTAssertEqual(pattern.text, "第一行\n第二行")
            XCTAssertFalse(check.isSelected); XCTAssertEqual(check.accessibilityValue, "未勾选")
            XCTAssertEqual(changes, ["name", "pattern"]); XCTAssertEqual(selections, [false])
            name.text = "最终保存名称"; name.sendActions(for: .editingChanged)
            pattern.text = #"广告\s+"#; pattern.delegate?.textViewDidChange?(pattern)
            check.sendActions(for: .touchUpInside)
            XCTAssertEqual(values, ["name": "最终保存名称", "pattern": #"广告\s+"#],
                "The consumer must receive every edited value used to save its draft")
            host.traitOverrides.preferredContentSizeCategory = .large
            window.overrideUserInterfaceStyle = .light
            await Task.yield()
            form.applyTheme(PageTheme())
            window.layoutIfNeeded(); host.view.layoutIfNeeded(); form.layoutIfNeeded()
            XCTAssertTrue(descendants(form).first { $0.accessibilityIdentifier == "form.name" } === name)
            XCTAssertTrue(descendants(form).first { $0.accessibilityIdentifier == "form.pattern" } === pattern)
            XCTAssertEqual(name.text, "最终保存名称"); XCTAssertEqual(pattern.text, #"广告\s+"#)
            XCTAssertEqual(values, ["name": "最终保存名称", "pattern": #"广告\s+"#])
            XCTAssertEqual(name.font?.pointSize, boxed ? 13 : 17)
            XCTAssertEqual(pattern.font?.pointSize, boxed ? 13 : 17)
            XCTAssertEqual(check.titleLabel?.font.pointSize, boxed ? 14 : 13)
            XCTAssertEqual(changes, ["name", "pattern", "name", "pattern"])
            XCTAssertEqual(selections, [false, true]); XCTAssertTrue(check.isSelected)
            XCTAssertEqual(check.accessibilityValue, "已勾选")
            form.removeFromSuperview()
        }
    }

    func testNavigationOwnsPageLifetimeAndDisablesSystemPopGestures() async throws {
        let theme = TestPageTheme()
        let firstPage = LifecyclePage(themeProvider: theme), secondPage = LifecyclePage(themeProvider: theme)
        let first = host(firstPage), second = host(secondPage)
        let retained = RetainedPagesController(pageFactories: [{ first }, { second }])
        let navigation = PageNavigationController(themeProvider: theme)
        navigation.setViewControllers([retained], animated: false)
        firstPage.activation = expectation(description: "first page appeared")
        let appeared = firstPage.activation!
        try display(navigation)
        activateForeground()
        await fulfillment(of: [appeared], timeout: 3)
        XCTAssertTrue(firstPage.isPageActive)
        XCTAssertFalse(secondPage.isPageActive)
        XCTAssertEqual(retained.children.count, 1)
        XCTAssertTrue(retained.children.first === first)

        XCTAssertTrue(navigation.interactivePopGestureRecognizer?.isEnabled == false)
        if #available(iOS 26.0, *) {
            XCTAssertTrue(navigation.interactiveContentPopGestureRecognizer?.isEnabled == false)
        }
        let started = expectation(description: "page task started")
        let finished = expectation(description: "removed page task observed cancellation")
        var continuation: CheckedContinuation<Void, Never>?
        firstPage.runPageTask {
            await withCheckedContinuation { continuation = $0; started.fulfill() }
            XCTAssertTrue(Task.isCancelled)
            finished.fulfill()
        }
        await fulfillment(of: [started], timeout: 3)
        secondPage.activation = expectation(description: "retained selection appeared")
        let selected = secondPage.activation!
        let selectionCompleted = expectation(description: "retained selection completed")
        retained.didSelect = { if $0 == 1 { selectionCompleted.fulfill() } }
        retained.select(1, animated: false)
        activateForeground()
        await fulfillment(of: [selected, selectionCompleted], timeout: 3)
        retained.didSelect = nil
        XCTAssertTrue(firstPage.isPageActive)
        XCTAssertFalse(firstPage.isPageVisible)
        XCTAssertTrue(secondPage.isPageActive)
        XCTAssertEqual(firstPage.inactiveCount, 0)
        XCTAssertEqual(retained.children.count, 2)
        XCTAssertTrue(first.parent === retained)

        let detail = PageViewController(themeProvider: theme)
        navigation.pushViewController(detail, animated: false)
        XCTAssertTrue(navigation.topViewController === detail)
        XCTAssertFalse(detail.view.gestureRecognizers?.contains { $0 is UIScreenEdgePanGestureRecognizer } ?? false)
        XCTAssertTrue(secondPage.isPageActive)
        XCTAssertEqual(navigation.viewControllers.count, 2)
        XCTAssertTrue(navigation.popViewController(animated: false) === detail)
        XCTAssertFalse(detail.isPageActive)
        XCTAssertTrue(navigation.topViewController === retained)
        XCTAssertTrue(secondPage.isPageActive)
        XCTAssertEqual(retained.selectedIndex, 1)
        NotificationCenter.default.post(name: UIApplication.willResignActiveNotification, object: nil)
        XCTAssertTrue(secondPage.isPageActive)
        NotificationCenter.default.post(name: UIApplication.didBecomeActiveNotification, object: nil)
        XCTAssertTrue(secondPage.isPageActive)
        XCTAssertTrue(firstPage.isPageActive)
        XCTAssertEqual(firstPage.inactiveCount, 0)
        PageViewController.invalidatePages(in: navigation)
        XCTAssertFalse(firstPage.isPageActive)
        XCTAssertFalse(secondPage.isPageActive)
        continuation?.resume()
        await fulfillment(of: [finished], timeout: 3)
    }

    func testAppOwnedHeaderReplacesEverySystemNavigationItem() async throws {
        let theme = TestPageTheme()
        let root = PageViewController(themeProvider: theme)
        let rootItem = root.navigationItem
        root.pageTitle = "首页"
        let detail = PageViewController(themeProvider: theme)
        detail.pageTitle = "详情"
        let navigation = PageNavigationController(themeProvider: theme)
        navigation.setViewControllers([root], animated: false)
        let window = try display(navigation)
        XCTAssertTrue(navigation.navigationBar.isHidden)
        XCTAssertNil(root.title)
        XCTAssertNil(navigation.navigationBar.topItem?.title)
        XCTAssertNil(root.navigationItem.title)
        XCTAssertNil(root.navigationItem.titleView)
        XCTAssertNil(root.navigationItem.leftBarButtonItem)
        XCTAssertNil(root.navigationItem.rightBarButtonItem)
        XCTAssertNil(root.navigationItem.searchController)
        XCTAssertTrue(root.navigationItem === rootItem)
        XCTAssertEqual(root.pageHeader.title, "首页")

        let detailShown = expectation(description: "app-owned detail appeared")
        navigation.didShowPage = { if $0 === detail { detailShown.fulfill() } }
        push(detail, on: navigation)
        await fulfillment(of: [detailShown], timeout: 3)
        navigation.didShowPage = nil
        XCTAssertTrue(navigation.navigationBar.isHidden)
        XCTAssertTrue(navigation.topViewController === detail)
        XCTAssertNil(detail.title)
        XCTAssertNil(navigation.navigationBar.topItem?.title)
        XCTAssertNil(navigation.navigationBar.backItem?.title)
        let itemPositions: (UINavigationController) -> [Int] = { owner in
            (owner.navigationBar.items ?? []).map { item in
                owner.viewControllers.firstIndex { $0.navigationItem === item } ?? -1
            }
        }
        let appItemPositions = itemPositions(navigation)
        XCTAssertFalse(appItemPositions.contains(-1), "Every system item must be the page's native item")

        // A hidden bar can retain its previously displayed items. Compare the
        // native behavior instead of using that invisible UI cursor as the stack.
        let nativeRoot = UIViewController(), nativeDetail = UIViewController()
        let native = UINavigationController(rootViewController: nativeRoot)
        native.setNavigationBarHidden(true, animated: false)
        let receipt = NativeNavigationDelegate()
        let nativeShown = expectation(description: "native detail appeared")
        receipt.didShow = { if $0 === nativeDetail { nativeShown.fulfill() } }
        native.delegate = receipt
        window.rootViewController = native
        window.layoutIfNeeded()
        push(nativeDetail, on: native)
        await fulfillment(of: [nativeShown], timeout: 3)
        receipt.didShow = nil
        XCTAssertEqual(appItemPositions, itemPositions(native))
        let comparison = XCTAttachment(string: "App-owned: \(appItemPositions); native UIKit: \(itemPositions(native))")
        comparison.name = "hidden-navigation-items"
        comparison.lifetime = .keepAlways
        add(comparison)
        window.rootViewController = navigation
        window.layoutIfNeeded()
        XCTAssertNil(detail.navigationItem.title)
        XCTAssertNil(detail.navigationItem.titleView)
        XCTAssertNil(detail.navigationItem.leftBarButtonItem)
        XCTAssertNil(detail.navigationItem.rightBarButtonItem)
        XCTAssertNil(detail.navigationItem.searchController)
        XCTAssertEqual(detail.pageHeader.title, "详情")
        detail.pageTitle = "新标题"
        XCTAssertEqual(detail.pageHeader.title, "新标题")
        XCTAssertNil(detail.title)
        XCTAssertNil(detail.navigationItem.title)
        XCTAssertTrue(detail.pageHeader.hasExplicitLeading)
        let returnButton = try XCTUnwrap(descendants(detail.pageHeader).compactMap { $0 as? PageHeaderButton }.first)
        XCTAssertEqual(returnButton.accessibilityLabel, "返回")
        XCTAssertFalse(returnButton.isHighlighted)
        returnButton.isHighlighted = true
        XCTAssertFalse(returnButton.isHighlighted)

        let returned = expectation(description: "header return completed")
        navigation.didShowPage = { if $0 === root { returned.fulfill() } }
        returnButton.sendActions(for: .touchUpInside)
        settle(navigation)
        await fulfillment(of: [returned], timeout: 3)
        navigation.didShowPage = nil
        XCTAssertTrue(navigation.topViewController === root)
        XCTAssertEqual(root.pageHeader.title, "首页")
        XCTAssertFalse(root.pageHeader.hasExplicitLeading)
    }

    func testInteractiveBackGestureAndHeaderButtonUseTheSamePopPath() throws {
        let theme = TestPageTheme()
        let root = PageViewController(themeProvider: theme)
        let detail = PageViewController(themeProvider: theme)
        let navigation = PageNavigationController(themeProvider: theme)
        navigation.setViewControllers([root], animated: false)
        try display(navigation)
        push(detail, on: navigation)
        XCTAssertTrue(navigation.interactivePopGestureRecognizer?.isEnabled == false)
        if #available(iOS 26.0, *) {
            XCTAssertTrue(navigation.interactiveContentPopGestureRecognizer?.isEnabled == false)
        }
        XCTAssertTrue(navigation.view.gestureRecognizers?.contains(navigation.interactiveBackGesture) == true)
        navigation.requestBack()
        settle(navigation)
        XCTAssertTrue(navigation.topViewController === root)
    }

    func testIndependentPageTransitionCancelsAndRevealsActualPresenter() async throws {
        let theme = TestPageTheme()
        let root = PageViewController(themeProvider: theme)
        try display(root)
        root.view.layoutIfNeeded()
        let dock = UIView(frame: CGRect(x: 18, y: 400, width: 300, height: 100))
        let dockPlay = UIButton(type: .system)
        dock.addSubview(dockPlay)
        root.view.addSubview(dock)
        let full = PageViewController(themeProvider: theme)
        full.loadViewIfNeeded()
        let fullPlay = UIButton(type: .system)
        full.view.addSubview(fullPlay)
        for edge in [VerticalPageTransition.Edge.bottom, .top] {
            let transition = VerticalPageTransition(edge: edge)
            transition.installEdgeDismissal(on: full)
            XCTAssertEqual(transition.edgeGesture.edges, .left)
            XCTAssertTrue(full.view.isOpaque)

            for (presenting, cancelled) in [(true, true), (true, false), (false, true), (false, false)] {
                let delegate = try XCTUnwrap(presenting
                    ? transition.animationController(forPresented: full, presenting: root, source: root)
                    : transition.animationController(forDismissed: full))
                let context = SurfaceContext(container: root.view, from: presenting ? root : full,
                                             to: presenting ? full : root, frame: root.view.bounds)
                let completed = expectation(description: "independent page transition completion")
                context.completed = { completed.fulfill() }
                let animator = try XCTUnwrap(delegate.interruptibleAnimator?(using: context) as? UIViewPropertyAnimator)
                XCTAssertTrue(delegate.interruptibleAnimator?(using: context) === animator)
                XCTAssertTrue(transition.isTransitioning)
                let offset = root.view.bounds.height * (edge == .top ? -1 : 1)
                XCTAssertEqual(full.view.transform.ty, presenting ? offset : 0, accuracy: 0.5)
                animator.startAnimation(afterDelay: 1)
                animator.pauseAnimation()
                XCTAssertEqual(full.view.transform.tx, 0, accuracy: 0.5)
                XCTAssertEqual(full.view.transform.ty, presenting ? 0 : offset, accuracy: 0.5)
                context.transitionWasCancelled = cancelled
                animator.stopAnimation(false)
                animator.finishAnimation(at: cancelled ? .start : .end)
                await fulfillment(of: [completed], timeout: 3)
                XCTAssertEqual(context.result, !cancelled)
                XCTAssertFalse(transition.isTransitioning)
                XCTAssertTrue(dock.superview === root.view)
                XCTAssertTrue(dockPlay.superview === dock)
                XCTAssertTrue(fullPlay.superview === full.view)
                XCTAssertFalse(dockPlay === fullPlay)
                XCTAssertEqual(root.view.transform, .identity)
                XCTAssertEqual(full.view.transform, .identity)
                if presenting == cancelled { XCTAssertNil(full.view.superview) }
                else { XCTAssertTrue(full.view.superview === root.view) }
            }
        }
    }

    func testEditorConfirmationBlocksTheAppOwnedPopUntilReleased() throws {
        let theme = TestPageTheme()
        let root = PageViewController(themeProvider: theme)
        let editor = ConfirmingPage(themeProvider: theme)
        let navigation = PageNavigationController(themeProvider: theme)
        navigation.setViewControllers([root, editor], animated: false)
        try display(navigation)
        navigation.view.layoutIfNeeded(); editor.view.layoutIfNeeded()
        XCTAssertTrue(editor.requestsBackConfirmationForNavigationPop())
        navigation.requestBack()
        settle(navigation)
        XCTAssertTrue(navigation.topViewController === editor)
        XCTAssertEqual(editor.confirmations, 1)
        editor.confirm = false
        navigation.requestBack()
        settle(navigation)
        XCTAssertTrue(navigation.topViewController === root)
    }

    func testHeaderControlsKeepTheirGeometryAcrossContentReplacementAndResize() throws {
        let header = PageHeaderView(themeProvider: TestPageTheme())
        let back = PageHeaderButton(image: PageIcon.image(.back), accessibilityLabel: "返回") {}
        let done = PageHeaderTextButton(title: "完成") {}
        header.setLeading(back)
        header.setTrailing([done])
        let custom = PageHeaderTextButton(title: "频道") {}
        for (width, top) in [(CGFloat(375), CGFloat(0)), (440, 62), (390, 20)] {
            header.statusBarHeight = top
            header.frame = CGRect(x: 0, y: 0, width: width, height: top + PageLayoutMetrics.navigation)
            header.setTitleView(custom)
            header.layoutIfNeeded()
            let titleFrame = custom.convert(custom.bounds, to: header)
            let backFrame = back.convert(back.bounds, to: header)
            let doneFrame = done.convert(done.bounds, to: header)
            XCTAssertEqual(titleFrame.height, PageLayoutMetrics.searchControl, accuracy: 0.5)
            XCTAssertEqual(titleFrame.midY, top + PageLayoutMetrics.navigation / 2, accuracy: 0.5)
            XCTAssertEqual(backFrame.width, 44, accuracy: 0.5)
            XCTAssertEqual(doneFrame.width, done.intrinsicContentSize.width, accuracy: 0.5)
            XCTAssertGreaterThan(titleFrame.width, width * 0.6)
            XCTAssertGreaterThanOrEqual(titleFrame.minX, backFrame.maxX)
            XCTAssertLessThanOrEqual(titleFrame.maxX, doneFrame.minX)
            header.setTitleView(nil)
            header.title = "页面"
            header.layoutIfNeeded()
            let label = try XCTUnwrap(descendants(header).compactMap { $0 as? UILabel }
                .first { $0.text == "页面" })
            let frame = label.convert(label.bounds, to: header)
            XCTAssertEqual(frame.midX, width / 2, accuracy: 0.5)
            XCTAssertEqual(frame.midY, titleFrame.midY, accuracy: 0.5)
            XCTAssertGreaterThan(frame.width, 0)
        }
        header.setTrailing([])
        header.layoutIfNeeded()
        XCTAssertEqual(header.trailingCount, 0)
        done.isHighlighted = true
        XCTAssertFalse(done.isHighlighted)
    }

    func testHeaderlessWindowContainerDoesNotObscureItsPageHeader() throws {
        let theme = TestPageTheme()
        let root = HeaderlessContainer(themeProvider: theme)
        let navigation = PageNavigationController(themeProvider: theme)
        let page = PageViewController(themeProvider: theme)
        page.pageTitle = "首页"
        navigation.setViewControllers([page], animated: false)
        root.loadViewIfNeeded()
        root.addChild(navigation)
        root.view.addSubview(navigation.view)
        navigation.view.pinEdges(to: root.view)
        navigation.didMove(toParent: root)
        try display(root)
        root.view.layoutIfNeeded()
        page.view.layoutIfNeeded()
        XCTAssertFalse(root.hasOwnPageHeader)
        XCTAssertTrue(page.hasOwnPageHeader)
        XCTAssertFalse(root.view.subviews.contains { $0 is PageHeaderView })
        XCTAssertEqual(page.pageHeader.title, "首页")
        XCTAssertEqual(page.additionalSafeAreaInsets.top, 0)
        XCTAssertEqual(page.pageHeader.frame.height,
                       page.view.safeAreaInsets.top + PageLayoutMetrics.navigation, accuracy: 0.5)
    }

    func testHeaderControlsRespectHorizontalSafeAreaWithoutShrinkingTheirBackground() throws {
        let page = PageViewController(themeProvider: TestPageTheme())
        page.loadViewIfNeeded()
        let back = PageHeaderButton(image: PageIcon.image(.back), accessibilityLabel: "返回") {}
        let done = PageHeaderTextButton(title: "完成") {}
        let content = UIView()
        page.pageHeader.setLeading(back)
        page.pageHeader.setTrailing([done])
        page.pageHeader.setTitleView(content)
        try display(page)
        for (left, right) in [(CGFloat(0), CGFloat(0)), (59, 59), (36, 72)] {
            page.additionalSafeAreaInsets = UIEdgeInsets(top: 0, left: left, bottom: 0, right: right)
            page.view.layoutIfNeeded()
            page.pageHeader.layoutIfNeeded()
            let header = page.pageHeader
            let safe = header.safeAreaLayoutGuide.layoutFrame
            let backFrame = back.convert(back.bounds, to: header)
            let doneFrame = done.convert(done.bounds, to: header)
            let titleFrame = content.convert(content.bounds, to: header)
            XCTAssertEqual(header.frame.width, page.view.bounds.width, accuracy: 0.5)
            XCTAssertEqual(header.background.frame, header.bounds)
            XCTAssertEqual(header.contentFrame.minX, safe.minX, accuracy: 0.5)
            XCTAssertEqual(header.contentFrame.maxX, safe.maxX, accuracy: 0.5)
            XCTAssertEqual(backFrame.minX, safe.minX + 8, accuracy: 0.5)
            XCTAssertEqual(doneFrame.maxX, safe.maxX - 8, accuracy: 0.5)
            XCTAssertGreaterThan(titleFrame.width, 0)
            XCTAssertGreaterThanOrEqual(titleFrame.minX, backFrame.maxX)
            XCTAssertLessThanOrEqual(titleFrame.maxX, doneFrame.minX)
        }
    }

    func testSystemAlertsKeepTheirLayoutWithAnAppOwnedPageHeader() async throws {
        let plain = UIViewController()
        plain.view.backgroundColor = .white
        let page = PageViewController(themeProvider: TestPageTheme())
        let navigation = PageNavigationController(themeProvider: page.themeProvider)
        navigation.setViewControllers([page], animated: false)
        let window = try display(plain)
        var frames: [CGRect] = []
        for (index, presenter) in [plain, navigation].enumerated() {
            window.rootViewController = presenter
            window.layoutIfNeeded()
            let alert = UIAlertController(title: "错误", message: "验收错误", preferredStyle: .alert)
            alert.addAction(UIAlertAction(title: "取消", style: .cancel))
            let shown = expectation(description: "native alert appeared")
            presenter.present(alert, animated: false) { shown.fulfill() }
            await fulfillment(of: [shown], timeout: 3)
            window.layoutIfNeeded()
            alert.view.layoutIfNeeded()
            let label = try XCTUnwrap(descendants(alert.view).compactMap { $0 as? UILabel }
                .first { $0.text == "错误" })
            let frame = label.convert(label.bounds, to: window)
            XCTAssertGreaterThan(frame.width, 0)
            XCTAssertTrue(window.bounds.contains(frame))
            XCTAssertFalse(descendants(alert.view).contains { $0 is PageHeaderView })
            frames.append(frame)
            // Component fixtures run in the test runner, not the foreground
            // App. Render their actual layers rather than a blank window snapshot.
            let image = UIGraphicsImageRenderer(bounds: alert.view.bounds).image { context in
                alert.view.layer.render(in: context.cgContext)
            }
            let attachment = XCTAttachment(image: image)
            attachment.name = index == 0 ? "native-alert" : "page-header-alert"
            attachment.lifetime = .keepAlways
            add(attachment)
            let closed = expectation(description: "native alert dismissed")
            alert.dismiss(animated: false) { closed.fulfill() }
            await fulfillment(of: [closed], timeout: 3)
        }
        XCTAssertEqual(frames[0].midX, frames[1].midX, accuracy: 1)
        XCTAssertEqual(frames[0].midY, frames[1].midY, accuracy: 1)
        XCTAssertEqual(frames[0].size, frames[1].size)
    }

    func testPagesRethemeTheirOwnHeadersInsideTheNavigationStack() async throws {
        let theme = TestPageTheme()
        let navigation = PageNavigationController(themeProvider: theme)
        let covered = PageViewController(themeProvider: theme)
        let top = PageViewController(themeProvider: theme)
        navigation.setViewControllers([covered, top], animated: false)
        try display(navigation)
        settle(navigation)
        covered.loadViewIfNeeded()
        theme.setColorway(.mint)
        let expected = UIColor(PageTheme(palette: .mint).accent)
        let rethemed = expectation(for: NSPredicate { _, _ in
            MainActor.assumeIsolated {
                covered.view.tintColor == expected && top.view.tintColor == expected
            }
        }, evaluatedWith: navigation)
        await fulfillment(of: [rethemed], timeout: 2)
    }

    func testPushSettlesIncomingHeaderBeforeTheSlideStarts() throws {
        let theme = TestPageTheme()
        let navigation = PageNavigationController(themeProvider: theme)
        let from = PageViewController(themeProvider: theme)
        let to = PageViewController(themeProvider: theme)
        let host = UIViewController()
        host.loadViewIfNeeded()
        let container = UIView()
        host.view.addSubview(container)
        container.pinEdges(to: host.view)
        try display(host)
        host.view.layoutIfNeeded()
        let insets = container.safeAreaInsets
        XCTAssertGreaterThan(insets.top, 0, "The host must provide a real top safe area")
        from.loadViewIfNeeded()
        to.loadViewIfNeeded()
        container.addSubview(from.view)
        from.view.frame = container.bounds
        let delegate = try XCTUnwrap(navigation.navigationController(
            navigation, animationControllerFor: .push, from: from, to: to
        ))
        let context = SurfaceContext(container: container, from: from, to: to, frame: container.bounds)
        _ = try XCTUnwrap(delegate.interruptibleAnimator?(using: context))
        // The animator must settle the incoming page's geometry before the
        // horizontal animation starts; no further layout is performed here.
        XCTAssertEqual(to.pageHeader.statusBarHeight, insets.top, accuracy: 0.5)
        XCTAssertEqual(to.pageHeader.frame.height,
                       insets.top + PageLayoutMetrics.navigation, accuracy: 0.5)
    }

    func testContentLayoutDoesNotResetScrollPositionOrOverwriteDynamicHeaderTitle() throws {
        let page = PageViewController(themeProvider: TestPageTheme())
        page.pageTitle = "页面"
        page.loadViewIfNeeded()
        let scroll = UIScrollView()
        page.view.addSubview(scroll)
        scroll.translatesAutoresizingMaskIntoConstraints = false
        NSLayoutConstraint.activate([
            scroll.leadingAnchor.constraint(equalTo: page.view.leadingAnchor),
            scroll.trailingAnchor.constraint(equalTo: page.view.trailingAnchor),
            scroll.topAnchor.constraint(equalTo: page.contentTopAnchor),
            scroll.bottomAnchor.constraint(equalTo: page.view.safeAreaLayoutGuide.bottomAnchor),
        ])
        try display(page)
        page.view.layoutIfNeeded()
        scroll.contentSize = CGSize(width: scroll.bounds.width, height: 3000)
        scroll.contentOffset.y = 300
        page.pageHeader.title = "动态标题"
        for _ in 0..<3 {
            page.view.setNeedsLayout()
            page.view.layoutIfNeeded()
            XCTAssertEqual(scroll.contentOffset.y, 300, accuracy: 0.5)
            XCTAssertEqual(scroll.frame.minY, page.pageHeader.frame.maxY, accuracy: 0.5)
            XCTAssertEqual(page.pageHeader.title, "动态标题")
        }
        scroll.contentSize.width = scroll.bounds.width * 2
        let point = CGPoint(x: scroll.frame.midX, y: scroll.frame.midY)
        scroll.contentOffset.x = 100
        XCTAssertFalse(page.shouldBeginInteractiveBack(at: point, in: page.view))
        scroll.contentOffset.x = 0
        XCTAssertTrue(page.shouldBeginInteractiveBack(at: point, in: page.view))
    }

    func testHorizontalStackTransitionsKeepPreviousPageStationaryAndCleanUpCancellation() async throws {
        let theme = TestPageTheme()
        let navigation = PageNavigationController(themeProvider: theme)
        let first = PageViewController(themeProvider: theme)
        let second = PageViewController(themeProvider: theme)
        let container = UIView(frame: CGRect(x: 0, y: 0, width: 390, height: 844))
        first.view.frame = container.bounds
        second.view.frame = container.bounds
        for operation in [UINavigationController.Operation.push, .pop] {
            for cancelled in [false, true] {
                container.addSubview(first.view)
                let from = operation == .push ? first : second
                let to = operation == .push ? second : first
                container.addSubview(from.view)
                let delegate = try XCTUnwrap(navigation.navigationController(
                    navigation, animationControllerFor: operation, from: from, to: to
                ))
                let context = SurfaceContext(container: container, from: from, to: to, frame: container.bounds)
                let completed = expectation(description: "stack transition completed")
                context.completed = { completed.fulfill() }
                let animator = try XCTUnwrap(delegate.interruptibleAnimator?(using: context) as? UIViewPropertyAnimator)
                XCTAssertTrue(delegate.interruptibleAnimator?(using: context) === animator)
                animator.startAnimation(afterDelay: 1)
                animator.pauseAnimation()
                XCTAssertEqual(first.view.transform, .identity)
                XCTAssertEqual(first.view.alpha, 1)
                XCTAssertEqual(first.view.layer.shadowOpacity, 0)
                context.transitionWasCancelled = cancelled
                animator.stopAnimation(false)
                animator.finishAnimation(at: cancelled ? .start : .end)
                await fulfillment(of: [completed], timeout: 3)
                XCTAssertEqual(context.result, !cancelled)
                XCTAssertEqual(first.view.transform, .identity)
                XCTAssertEqual(second.view.transform, .identity)
                first.view.removeFromSuperview()
                second.view.removeFromSuperview()
            }
        }
    }

    /// Non-animated push and pop start their deferred transition, and the
    /// navigation delegate receipts, on the navigation view's next layout.
    private func settle(_ navigation: UINavigationController) { navigation.view.layoutIfNeeded() }

    private func push(_ page: UIViewController, on navigation: UINavigationController) {
        navigation.pushViewController(page, animated: false)
        settle(navigation)
    }

    @discardableResult
    private func pop(_ navigation: UINavigationController) -> UIViewController? {
        defer { settle(navigation) }
        return navigation.popViewController(animated: false)
    }

    private func installManagementForm(_ form: ManagementFormView, in host: UIViewController) {
        host.view.addSubview(form); form.translatesAutoresizingMaskIntoConstraints = false
        NSLayoutConstraint.activate([
            form.centerXAnchor.constraint(equalTo: host.view.centerXAnchor),
            form.topAnchor.constraint(equalTo: host.view.safeAreaLayoutGuide.topAnchor),
            form.widthAnchor.constraint(equalToConstant: 320),
            form.heightAnchor.constraint(equalToConstant: 420),
        ])
    }

    @discardableResult
    private func display(_ controller: UIViewController) throws -> UIWindow {
        let scene = try XCTUnwrap(UIApplication.shared.connectedScenes.compactMap { $0 as? UIWindowScene }
            .first, "The UI test runner has no scene")
        previousKeyWindow = scene.windows.first { $0.isKeyWindow }
        let value = UIWindow(windowScene: scene)
        value.frame = scene.coordinateSpace.bounds
        value.overrideUserInterfaceStyle = .light
        value.windowLevel = .normal + 1
        value.rootViewController = controller
        window = value
        value.makeKeyAndVisible()
        value.layoutIfNeeded()
        return value
    }

    private func host(_ page: UIViewController) -> UIViewController {
        let host = UIViewController()
        host.loadViewIfNeeded()
        host.addChild(page)
        host.view.addSubview(page.view)
        page.view.pinEdges(to: host.view)
        page.didMove(toParent: host)
        return host
    }

    private func activateForeground() {
        NotificationCenter.default.post(name: UIApplication.didBecomeActiveNotification, object: nil)
    }

    private func descendants(_ view: UIView) -> [UIView] {
        view.subviews.flatMap { [$0] + descendants($0) }
    }

    private func renderSettingsCard(_ view: UIView) -> UIImage {
        let format = UIGraphicsImageRendererFormat()
        format.scale = 2
        format.preferredRange = .standard
        return UIGraphicsImageRenderer(bounds: view.bounds, format: format).image {
            view.layer.render(in: $0.cgContext)
        }
    }

    private func settingsPixel(_ image: UIImage, x: CGFloat, y: CGFloat) throws -> [UInt8] {
        let imageData = try XCTUnwrap(image.cgImage)
        let pixel = try XCTUnwrap(imageData.cropping(to: CGRect(
            x: (x * image.scale).rounded(.down), y: (y * image.scale).rounded(.down),
            width: 1, height: 1)))
        var bytes = [UInt8](repeating: 0, count: 4)
        try bytes.withUnsafeMutableBytes { buffer in
            let context = try XCTUnwrap(CGContext(data: buffer.baseAddress, width: 1, height: 1,
                bitsPerComponent: 8, bytesPerRow: 4, space: CGColorSpaceCreateDeviceRGB(),
                bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue))
            context.draw(pixel, in: CGRect(x: 0, y: 0, width: 1, height: 1))
        }
        return bytes
    }

    override func tearDown() {
        window?.isHidden = true
        window?.rootViewController = nil
        window = nil
        previousKeyWindow?.makeKey()
        previousKeyWindow = nil
        super.tearDown()
    }
}

@MainActor
private final class NativeNavigationDelegate: NSObject, UINavigationControllerDelegate {
    var didShow: ((UIViewController) -> Void)?

    func navigationController(_ navigationController: UINavigationController,
                              didShow viewController: UIViewController, animated: Bool) {
        didShow?(viewController)
    }
}

@MainActor
private final class ConfirmingPage: PageViewController {
    var confirm = true
    private(set) var confirmations = 0
    override func requestsBackConfirmationForNavigationPop() -> Bool { confirm }
    override func requestBackConfirmationForNavigationPop() { confirmations += 1 }
}

@MainActor
private final class HeaderlessContainer: PageViewController {
    override var usesPageHeader: Bool { false }
}

@MainActor
private final class LifecyclePage: PageViewController {
    var activation: XCTestExpectation?
    private(set) var inactiveCount = 0
    override func pageDidBecomeActive() { activation?.fulfill(); activation = nil }
    override func pageDidBecomeInactive() { inactiveCount += 1 }
}

/// UIKit normally supplies this context. The fixture drives production native
/// animators deterministically; it is not a real touch or system-pop driver.
@MainActor
private final class SurfaceContext: NSObject, UIViewControllerContextTransitioning {
    let containerView: UIView
    let isAnimated = true, isInteractive = false
    let presentationStyle = UIModalPresentationStyle.custom
    let targetTransform = CGAffineTransform.identity
    var transitionWasCancelled = false
    var completed: (() -> Void)?
    private(set) var result: Bool?
    private let from: UIViewController, to: UIViewController, frame: CGRect
    init(container: UIView, from: UIViewController, to: UIViewController, frame: CGRect) {
        containerView = container; self.from = from; self.to = to; self.frame = frame
    }
    func viewController(forKey key: UITransitionContextViewControllerKey) -> UIViewController? { key == .from ? from : to }
    func view(forKey key: UITransitionContextViewKey) -> UIView? { key == .from ? from.view : to.view }
    func initialFrame(for vc: UIViewController) -> CGRect { frame }
    func finalFrame(for vc: UIViewController) -> CGRect { frame }
    func updateInteractiveTransition(_ percentComplete: CGFloat) { }
    func finishInteractiveTransition() { }
    func cancelInteractiveTransition() { transitionWasCancelled = true }
    func pauseInteractiveTransition() { }
    func completeTransition(_ didComplete: Bool) { result = didComplete; completed?() }
}
