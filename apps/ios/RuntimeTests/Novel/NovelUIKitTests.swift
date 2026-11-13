#if os(iOS)
import XCTest
import UIKit
import WebKit
import Combine
import CoreImage
import MeowUI
import MeowRuntime
import MeowStorage
@testable import MeowNovelFeature
@testable import Meowfilm

@MainActor
final class NovelUIKitTests: XCTestCase {
    private var windows: [(window: UIWindow, previous: UIWindow?)] = []
    private var roots: [URL] = []
    private var features: [NovelFeature] = []
    private var runtimes: [NovelUIKitRuntime] = []
    override func tearDown() async throws {
        for runtime in runtimes { await runtime.acceptImage() }
        for (window, previous) in windows.reversed() {
            if let root = window.rootViewController { PageViewController.invalidatePages(in: root) }
            window.isHidden = true; window.rootViewController = nil
            previous?.makeKey()
        }
        for feature in features { await feature.close() }
        for root in roots { try? FileManager.default.removeItem(at: root) }
        windows = []; features = []; roots = []; runtimes = []
        try await super.tearDown()
    }
    private func fixture() async throws -> (NovelFeature, NovelUIKitRuntime) {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true); roots.append(root)
        let runtime = NovelUIKitRuntime(); runtimes.append(runtime)
        let resources = try await hostResources()
        let rules = NovelRuleProcessor(runtime: resources.novelRuntime)
        let feature = try NovelFeature(library: NovelLibrary(store: DocumentStore(baseURL: root, namespace: NovelLibrary.namespace)), rules: rules, runtime: runtime)
        try await feature.reloadLocal(); try await feature.refreshSources(); features.append(feature)
        return (feature, runtime)
    }
    private func mount(_ page: UIViewController) throws -> UIWindow {
        let scene = try XCTUnwrap(UIApplication.shared.connectedScenes.compactMap { $0 as? UIWindowScene }.first { $0.activationState == .foregroundActive })
        let previous = scene.windows.first { $0.isKeyWindow }
        let window = UIWindow(windowScene: scene); window.frame = scene.coordinateSpace.bounds; window.windowLevel = .normal + 1
        window.rootViewController = page; window.makeKeyAndVisible(); window.layoutIfNeeded(); page.view.layoutIfNeeded()
        windows.append((window, previous)); return window
    }
    private func wait(file: StaticString = #filePath, line: UInt = #line, _ condition: @escaping @MainActor () -> Bool) async {
        let done = expectation(for: NSPredicate { _, _ in MainActor.assumeIsolated { condition() } }, evaluatedWith: nil)
        await fulfillment(of: [done], timeout: 5); XCTAssertTrue(condition(), file: file, line: line)
    }
    private func descendants(_ view: UIView) -> [UIView] { [view] + view.subviews.flatMap(descendants) }
    private func button(_ id: String, in view: UIView) throws -> UIButton { try XCTUnwrap(descendants(view).first { $0.accessibilityIdentifier == id } as? UIButton) }
    private func assertLabelFits(_ label: UILabel, in container: UIView, file: StaticString = #filePath, line: UInt = #line) {
        let size = label.sizeThatFits(CGSize(width: label.bounds.width, height: .greatestFiniteMagnitude))
        let frame = label.convert(label.bounds, to: container)
        XCTAssertGreaterThan(label.bounds.width, 0, file: file, line: line)
        XCTAssertGreaterThanOrEqual(label.bounds.height + 1, size.height, file: file, line: line)
        XCTAssertGreaterThanOrEqual(frame.minX, -1, file: file, line: line)
        XCTAssertLessThanOrEqual(frame.maxX, container.bounds.width + 1, file: file, line: line)
    }
    private func assertReachableEdges(_ content: UIView, in scroll: UIScrollView, file: StaticString = #filePath, line: UInt = #line) {
        for y in [content.bounds.minY, content.bounds.maxY - 1] {
            let edge = CGRect(x: content.bounds.minX, y: y, width: content.bounds.width, height: 1)
            scroll.scrollRectToVisible(content.convert(edge, to: scroll), animated: false); scroll.layoutIfNeeded()
            XCTAssertTrue(scroll.bounds.insetBy(dx: -1, dy: -1).contains(content.convert(edge, to: scroll)), file: file, line: line)
        }
    }
    private func attach(_ window: UIWindow, _ name: String) {
        window.layoutIfNeeded()
        XCTAssertEqual(window.windowScene?.activationState, .foregroundActive)
        var drawn = false
        let image = UIGraphicsImageRenderer(bounds: window.bounds).image { _ in drawn = window.drawHierarchy(in: window.bounds, afterScreenUpdates: true) }
        XCTAssertTrue(drawn, "The native window hierarchy must actually render for its screenshot")
        let attachment = XCTAttachment(image: image); attachment.name = name; attachment.lifetime = .keepAlways; add(attachment)
    }
    private func attachManagement(_ window: UIWindow, _ name: String) async throws {
        window.layoutIfNeeded(); XCTAssertEqual(window.windowScene?.activationState, .foregroundActive)
        // App-hosted XCTest is not an XCUITest runner, so XCUIScreen is intentionally
        // unavailable here.  Render the visible UIWindow hierarchy itself instead of using
        // a detached view or a second-process screenshot bridge.  drawHierarchy preserves
        // safe-area layout and UIVisualEffectView composition for the native page and sheets.
        try await Task.sleep(nanoseconds: 150_000_000)
        let image = UIGraphicsImageRenderer(bounds: window.bounds).image { _ in
            XCTAssertTrue(window.drawHierarchy(in: window.bounds, afterScreenUpdates: true))
        }
        let attachment = XCTAttachment(image: image)
        attachment.name = "screen-" + name; attachment.lifetime = .keepAlways; add(attachment)
    }
    func testManagementPagesUsePublicHeadersAndLiveApplicationTheme() async throws {
        let (feature, _) = try await fixture(), provider = NovelManagementPageTheme()
        let reference = PageViewController(themeProvider: provider)
        let navigation = PageNavigationController(themeProvider: provider); navigation.setViewControllers([reference], animated: false)
        let window = try mount(navigation)
        let pages: [(PageViewController, Int)] = [
            (NovelSourcesViewController(feature: feature, themeProvider: provider), 0),
            (try NovelSourceEditor(source: nil, protocol: .legado, definition: nil, feature: feature, themeProvider: provider), 2),
            (NovelRulesViewController(feature: feature, themeProvider: provider), 0),
            (NovelRuleEditor(rule: NovelRule(), feature: feature, themeProvider: provider), 2)
        ]
        for (index, entry) in pages.enumerated() {
            let (page, actions) = entry
            navigation.pushViewController(page, animated: false); await wait { page.isPageVisible }
            let canvas = try XCTUnwrap(page.view as? WindowGradientView)
            window.overrideUserInterfaceStyle = .dark
            await wait { page.traitCollection.userInterfaceStyle == .dark && canvas.backgroundColor == UIColor(page.theme.bottom) }
            provider.setPalette(index.isMultiple(of: 2) ? .preset(.mint) : .preset(.lilac))
            let darkTheme = PageTheme(colors: provider.pagePalette, dark: true)
            await wait { canvas.backgroundColor == UIColor(darkTheme.bottom) }
            XCTAssertEqual(canvas.theme, darkTheme)
            XCTAssertEqual(page.pageHeader.background.theme, darkTheme)
            for style in [UIUserInterfaceStyle.light, .dark] {
                window.overrideUserInterfaceStyle = style
                await wait { page.traitCollection.userInterfaceStyle == style && canvas.backgroundColor == UIColor(page.theme.bottom) }
                window.layoutIfNeeded(); page.view.layoutIfNeeded()
                XCTAssertEqual(page.headerContentHeight, reference.headerContentHeight)
                XCTAssertEqual(page.pageHeader.horizontalInset, reference.pageHeader.horizontalInset)
                XCTAssertEqual(page.pageHeader.contentFrame.height, reference.headerContentHeight, accuracy: 0.5)
                XCTAssertEqual(page.pageHeader.trailingCount, actions)
                XCTAssertFalse(page.pageHeader.background.isHidden)
                let back = try button("page.header.back", in: page.view)
                XCTAssertTrue(back is PageHeaderButton)
                XCTAssertEqual(back.bounds.size, back.intrinsicContentSize)
                XCTAssertEqual(back.tintColor, UIColor(page.theme.ink))
                let settings = page.theme.settings
                if let manager = page as? NovelManagementPage {
                    let table = try XCTUnwrap(descendants(manager.manager).first { $0.accessibilityIdentifier == "management.list" } as? UITableView)
                    XCTAssertEqual(table.backgroundColor, UIColor(settings.surface))
                    XCTAssertEqual(table.separatorStyle, .singleLine)
                    XCTAssertEqual(table.separatorColor, UIColor(settings.separator))
                    XCTAssertEqual(table.frame.minX, 24, accuracy: 0.5)
                    XCTAssertEqual(table.layer.cornerRadius, 22)
                    XCTAssertTrue(table.clipsToBounds)
                    XCTAssertEqual(manager.manager.search.searchTextField.backgroundColor, UIColor(settings.input))
                    XCTAssertEqual(manager.manager.search.searchTextField.textColor, UIColor(settings.ink))
                    let remove = try button("management.delete", in: manager.manager)
                    XCTAssertEqual(remove.backgroundColor, UIColor(settings.input))
                    XCTAssertEqual(remove.titleColor(for: .normal), UIColor(settings.danger))
                    XCTAssertEqual(remove.titleColor(for: .disabled), UIColor(settings.muted))
                }
                for form in descendants(page.view).compactMap({ $0 as? ManagementFormView }) {
                    XCTAssertEqual(form.fields.backgroundColor, UIColor(settings.surface))
                    for input in descendants(form) {
                        if let field = input as? UITextField {
                            XCTAssertEqual(field.textColor, UIColor(settings.ink))
                            if form.boxedInputs { XCTAssertEqual(field.backgroundColor, UIColor(settings.input)); XCTAssertEqual(field.layer.borderColor, UIColor(settings.inputBorder).cgColor) }
                        } else if let field = input as? UITextView {
                            XCTAssertEqual(field.textColor, UIColor(settings.ink))
                            if form.boxedInputs { XCTAssertEqual(field.backgroundColor, UIColor(settings.input)); XCTAssertEqual(field.layer.borderColor, UIColor(settings.inputBorder).cgColor) }
                        }
                    }
                }
                if page is NovelSourceEditor {
                    let panel = try XCTUnwrap(descendants(page.view).first { $0.accessibilityIdentifier == "novel.source.panel" })
                    XCTAssertEqual(panel.backgroundColor, UIColor(settings.surface))
                    let type = try XCTUnwrap(descendants(panel).compactMap { $0 as? UIButton }.first { $0.accessibilityLabel == "内容类型" })
                    XCTAssertEqual(type.configuration?.baseForegroundColor, UIColor(settings.ink))
                    XCTAssertEqual(type.configuration?.background.backgroundColor, UIColor(settings.input))
                }
            }
        }
    }
    func testNativeSourceAndRuleManagersKeepSelectionTabsFieldsAndImportLayout() async throws {
        let (feature, _) = try await fixture(), theme = NovelTestPageTheme()
        let page = NovelSourcesViewController(feature: feature, themeProvider: theme)
        let navigation = PageNavigationController(themeProvider: theme); navigation.setViewControllers([page], animated: false)
        let window = try mount(navigation); await wait { page.isPageVisible }
        page.render(); window.layoutIfNeeded()
        XCTAssertEqual(descendants(page.manager).compactMap { $0 as? UISearchBar }.count, 1)
        let table = try XCTUnwrap(descendants(page.manager).first { $0.accessibilityIdentifier == "management.list" } as? UITableView)
        XCTAssertEqual(table.numberOfRows(inSection: 0), 1); try await attachManagement(window, "source-manager")
        page.manager.setRows([], total: 0, validIDs: [], emptyMessage: "暂无书源，请先导入")
        try await attachManagement(window, "source-manager-empty")
        let selectAll = try button("management.selectAll", in: page.manager)
        XCTAssertEqual(selectAll.title(for: .normal), "  全选 (0/0)"); XCTAssertGreaterThan(selectAll.titleLabel?.bounds.width ?? 0, 0)
        page.render()
        try button("management.selectAll", in: page.manager).sendActions(for: .touchUpInside); XCTAssertEqual(page.manager.selected, ["fixture"])
        page.manager.setRows([], total: 1, validIDs: ["fixture"], emptyMessage: "没有匹配的书源")
        try button("management.invert", in: page.manager).sendActions(for: .touchUpInside); XCTAssertEqual(page.manager.selected, ["fixture"])
        page.render(); try button("management.invert", in: page.manager).sendActions(for: .touchUpInside); XCTAssertTrue(page.manager.selected.isEmpty)
        let editor = try NovelSourceEditor(source: nil, protocol: .legado, definition: #"{"bookSourceName":"全本同人小说网","bookSourceUrl":"https://www.qbtr.org","bookSourceGroup":"同人","bookSourceType":0,"enabled":true,"enabledExplore":true,"enabledCookieJar":false,"ruleSearch":{}}"#, feature: feature, themeProvider: theme)
        navigation.pushViewController(editor, animated: false); await wait { editor.isPageVisible }; try await attachManagement(window, "source-basic")
        let basic = try XCTUnwrap(descendants(editor.view).first { $0.accessibilityIdentifier == "novel.source.field.bookSourceName" } as? UITextField)
        basic.text = "书源名称"; basic.sendActions(for: .editingChanged)
        try button("novel.source.tab.1", in: editor.view).sendActions(for: .touchUpInside)
        XCTAssertNotNil(descendants(editor.view).first { $0.accessibilityIdentifier == "novel.source.field.ruleSearch.bookList" } as? UITextView)
        try await attachManagement(window, "source-search")
        for index in 2..<6 {
            try button("novel.source.tab.\(index)", in: editor.view).sendActions(for: .touchUpInside)
            XCTAssertFalse(descendants(editor.view).filter { $0.accessibilityIdentifier?.hasPrefix("novel.source.field.") == true }.isEmpty)
            try await attachManagement(window, "source-tab-\(index)")
        }
        try button("novel.source.tab.0", in: editor.view).sendActions(for: .touchUpInside)
        XCTAssertEqual((descendants(editor.view).first { $0.accessibilityIdentifier == "novel.source.field.bookSourceName" } as? UITextField)?.text, "书源名称")
        navigation.popViewController(animated: false)
        let rules = NovelRulesViewController(feature: feature, themeProvider: theme); navigation.pushViewController(rules, animated: false)
        await wait { rules.isPageVisible }; try await attachManagement(window, "rule-manager")
        var rule = NovelRule(); rule.name = "净化广告"; rule.pattern = "广告"; rule.isRegex = true
        let ruleEditor = NovelRuleEditor(rule: rule, feature: feature, themeProvider: theme); navigation.pushViewController(ruleEditor, animated: false)
        await wait { ruleEditor.isPageVisible }; try await attachManagement(window, "rule-editor")
        XCTAssertEqual(descendants(ruleEditor.view).filter { $0.accessibilityIdentifier?.hasPrefix("novel.rule.field.") == true }.count, 7)
        let scopeChecks = descendants(ruleEditor.view).compactMap { $0 as? UIButton }.filter { ["作用于标题", "作用于正文"].contains($0.accessibilityLabel ?? "") }
        XCTAssertEqual(scopeChecks.count, 2); XCTAssertTrue(scopeChecks.allSatisfy { $0.buttonType == .custom && $0.bounds.width > 0 })
        let form = try XCTUnwrap(descendants(ruleEditor.view).first { $0 is ManagementFormView } as? ManagementFormView)
        let timeout = try XCTUnwrap(descendants(form).first { $0.accessibilityIdentifier == "novel.rule.field.timeoutMillisecond" })
        form.scrollRectToVisible(timeout.convert(timeout.bounds, to: form), animated: false)
        try await attachManagement(window, "rule-editor-bottom")
        try button("novel.rule.save", in: ruleEditor.view).sendActions(for: .touchUpInside)
        await wait { feature.local.rules.contains { $0.id == rule.id } && navigation.topViewController === rules }
        let dialog = ManagementDialog(title: "导入书源", action: "确定导入", themeProvider: theme)
        let names = ["全本同人小说网", "南极小说（优）", "🍊淘小说吧🍊", "小原文学网", "错层小说", "思兔阅读", "猫眼看书", "未来天王", "梦想家文学", "📚 梦书中文", "搬山人小说网", "书包书库", "阅读源 13", "阅读源 14", "阅读源 15", "异次元", "阅读源 17", "阅读源 18"]
        let selection = ManagementImportView(items: names.map { .init(title: $0, detail: "阅读源 · 常规", status: "已存在") })
        dialog.body.addArrangedSubview(selection); rules.present(dialog, animated: false)
        await wait { dialog.view.window === window && !dialog.isBeingPresented }
        window.layoutIfNeeded(); dialog.view.layoutIfNeeded()
        XCTAssertEqual(selection.selected, Set(names.indices)); XCTAssertGreaterThan(selection.bounds.height, 0); try await attachManagement(window, "source-import-confirm")
        let panel = try XCTUnwrap(descendants(dialog.view).first { $0.accessibilityIdentifier == "management.dialog.panel" })
        let importTable = try XCTUnwrap(descendants(selection).compactMap { $0 as? UITableView }.first)
        for style in [UIUserInterfaceStyle.light, .dark] {
            window.overrideUserInterfaceStyle = style
            let settings = SettingsTheme(dark: style == .dark)
            await wait { panel.backgroundColor == UIColor(settings.surface) && importTable.separatorColor == UIColor(settings.separator) }
            XCTAssertEqual(panel.layer.borderColor, UIColor(settings.border).cgColor)
            XCTAssertEqual(try button("management.dialog.confirm", in: dialog.view).backgroundColor, UIColor(settings.accent))
            XCTAssertEqual(try button("management.dialog.confirm", in: dialog.view).titleColor(for: .normal), UIColor(settings.onAccent))
            XCTAssertEqual(selection.selected, Set(names.indices))
            let cell = try XCTUnwrap(importTable.dataSource?.tableView(importTable, cellForRowAt: .init(row: 0, section: 0)))
            let labels = descendants(cell).compactMap { $0 as? UILabel }
            XCTAssertEqual(labels.first { $0.text == names[0] }?.textColor, UIColor(settings.ink))
            XCTAssertEqual(labels.first { $0.text == "已存在" }?.textColor, UIColor(settings.warning))
        }
        try button("management.dialog.cancel", in: dialog.view).sendActions(for: .touchUpInside); await wait { rules.presentedViewController == nil }
    }
    func testManagementModalCancellationWaitsForActualOperationDrain() async throws {
        let (feature, _) = try await fixture(), theme = NovelTestPageTheme()
        let page = NovelSourcesViewController(feature: feature, themeProvider: theme)
        let navigation = PageNavigationController(themeProvider: theme); navigation.setViewControllers([page], animated: false)
        let window = try mount(navigation); await wait { page.isPageVisible }
        let table = try XCTUnwrap(descendants(page.manager).first { $0.accessibilityIdentifier == "management.list" } as? UITableView)
        let rows = table.numberOfRows(inSection: 0)
        let entered = expectation(description: "preview entered"), cancelled = expectation(description: "preview cancelled")
        var drain: CheckedContinuation<Void, Never>?, accepted = false
        page.addressDialog(label: "书源地址", placeholder: "请输入网络书源地址", load: { _ in
            entered.fulfill()
            do { try await Task.sleep(nanoseconds: 60_000_000_000) }
            catch { cancelled.fulfill(); await withCheckedContinuation { drain = $0 }; throw error }
        }, completed: { accepted = true })
        let dialog = try XCTUnwrap(page.presentedViewController as? ManagementDialog)
        let input = try XCTUnwrap(descendants(dialog.view).first { $0.accessibilityIdentifier == "management.import.url" } as? UITextField)
        await wait { dialog.view.window === window && !dialog.isBeingPresented }
        window.layoutIfNeeded(); try await attachManagement(window, "source-network-import")
        XCTAssertEqual(dialog.modalPresentationStyle, .overFullScreen)
        XCTAssertEqual(dialog.view.backgroundColor, .clear)
        XCTAssertTrue(page.view.window === window && page.manager.window === window)
        XCTAssertFalse(page.view.isHidden); XCTAssertFalse(page.manager.isHidden); XCTAssertEqual(page.view.alpha, 1)
        XCTAssertEqual(table.numberOfRows(inSection: 0), rows); XCTAssertGreaterThan(rows, 0)
        let panel = try XCTUnwrap(descendants(dialog.view).first { $0.accessibilityIdentifier == "management.dialog.panel" })
        let backdrop = dialog.view.subviews.filter { $0 !== panel }.flatMap(descendants)
        XCTAssertFalse(backdrop.contains { ($0 as? UIVisualEffectView)?.effect is UIBlurEffect }, "Network import must keep the underlying source list clear")
        let scrim = try XCTUnwrap(descendants(dialog.view).first { $0.accessibilityIdentifier == "management.dialog.scrim" })
        XCTAssertGreaterThan(try XCTUnwrap(scrim.backgroundColor).cgColor.alpha, 0)
        XCTAssertLessThan(try XCTUnwrap(scrim.backgroundColor).cgColor.alpha, 1)
        let settings = page.theme.settings
        XCTAssertEqual(input.backgroundColor, UIColor(settings.input)); XCTAssertEqual(input.textColor, UIColor(settings.ink))
        XCTAssertEqual(input.layer.borderColor, UIColor(settings.inputBorder).cgColor)
        input.text = "https://source.invalid/book-sources.json"
        try button("management.dialog.confirm", in: dialog.view).sendActions(for: .touchUpInside); await fulfillment(of: [entered], timeout: 5)
        try button("management.dialog.cancel", in: dialog.view).sendActions(for: .touchUpInside); await fulfillment(of: [cancelled], timeout: 5)
        XCTAssertTrue(page.presentedViewController === dialog); XCTAssertFalse(accepted)
        let receipt = try XCTUnwrap(drain); receipt.resume(); await wait { page.presentedViewController == nil && page.isPageVisible }; XCTAssertFalse(accepted)
        XCTAssertTrue(page.isPageActive && page.isPageVisible && page.manager.window === window)
        XCTAssertEqual(table.numberOfRows(inSection: 0), rows)
    }

    func testManagementDialogLargeTypeKeepsLongContentAndSubmitResultsReachableAt320() async throws {
        let page = PageViewController(themeProvider: NovelTestPageTheme())
        let window = try mount(page); window.frame = CGRect(x: 0, y: 0, width: 320, height: 568)
        window.layoutIfNeeded(); await wait { page.canPresentPageAlert }
        let heading = "网络导入：" + String(repeating: "包含很长名称的书源分组", count: 3)
        let message = String(repeating: "无法导入，请检查网络地址和书源内容后重新提交。", count: 8)
        let dialog = ManagementDialog(title: heading, action: "确定导入", themeProvider: NovelTestPageTheme())
        dialog.traitOverrides.preferredContentSizeCategory = .large
        let label = UILabel(); label.text = String(repeating: "书源网络地址", count: 4); label.font = .systemFont(ofSize: 14)
        let input = ManagementDialog.input(placeholder: "网络地址")
        let address = "https://source.invalid/" + String(repeating: "long-path/", count: 20)
        input.keyboardType = .URL; input.text = address
        dialog.body.addArrangedSubview(label); dialog.body.addArrangedSubview(input)
        var submissions: [String] = [], accepted = 0
        dialog.submit = {
            submissions.append(input.text ?? "")
            if submissions.count == 1 { throw NSError(domain: "ManagementDialogDynamicType", code: 1, userInfo: [NSLocalizedDescriptionKey: message]) }
            return submissions.count > 2
        }
        dialog.accepted = { accepted += 1 }
        page.present(dialog, animated: false)
        await wait { dialog.view.window === window && !dialog.isBeingPresented }
        let headingLabel = try XCTUnwrap(descendants(dialog.view).compactMap { $0 as? UILabel }.first { $0.text == heading })
        let error = try XCTUnwrap(descendants(dialog.view).first { $0.accessibilityIdentifier == "management.dialog.error" } as? UILabel)
        let scroll = try XCTUnwrap(descendants(dialog.view).first { $0.accessibilityIdentifier == "management.dialog.scroll" } as? UIScrollView)
        let panel = try XCTUnwrap(descendants(dialog.view).first { $0.accessibilityIdentifier == "management.dialog.panel" })
        let confirm = try button("management.dialog.confirm", in: dialog.view)
        let cancel = try button("management.dialog.cancel", in: dialog.view)
        XCTAssertEqual(headingLabel.font.pointSize, 17, accuracy: 0.1)
        XCTAssertEqual(error.font.pointSize, 13, accuracy: 0.1)
        XCTAssertEqual(input.font?.pointSize ?? 0, 14, accuracy: 0.1)
        XCTAssertEqual(confirm.titleLabel?.font.pointSize ?? 0, 14, accuracy: 0.1)
        dialog.traitOverrides.preferredContentSizeCategory = .accessibilityExtraExtraExtraLarge
        await wait {
            headingLabel.font.pointSize > 17 && error.font.pointSize > 13 &&
                (input.font?.pointSize ?? 0) > 14 && (confirm.titleLabel?.font.pointSize ?? 0) > 14 && label.font.pointSize > 14
        }
        dialog.view.setNeedsLayout(); dialog.view.layoutIfNeeded()
        confirm.sendActions(for: .touchUpInside)
        await wait { error.text == message && !error.isHidden && confirm.isEnabled }
        dialog.view.layoutIfNeeded()
        XCTAssertEqual(dialog.view.bounds.width, 320, accuracy: 0.5)
        XCTAssertEqual(dialog.modalPresentationStyle, .overFullScreen)
        XCTAssertTrue(page.view.window === window && !page.view.isHidden)
        XCTAssertGreaterThanOrEqual(panel.frame.minY, dialog.view.safeAreaLayoutGuide.layoutFrame.minY)
        XCTAssertLessThanOrEqual(panel.frame.maxY, dialog.view.keyboardLayoutGuide.layoutFrame.minY)
        XCTAssertLessThanOrEqual(scroll.contentSize.width, scroll.bounds.width + 1)
        XCTAssertGreaterThan(scroll.contentSize.height, scroll.bounds.height)
        for text in [headingLabel, label, error] {
            XCTAssertTrue(text.adjustsFontForContentSizeCategory); XCTAssertEqual(text.numberOfLines, 0)
            assertLabelFits(text, in: panel); assertReachableEdges(text, in: scroll)
        }
        XCTAssertTrue(input.adjustsFontForContentSizeCategory)
        XCTAssertGreaterThanOrEqual(input.bounds.height, 44)
        XCTAssertGreaterThanOrEqual(input.bounds.height, input.intrinsicContentSize.height)
        XCTAssertEqual(input.keyboardType, .URL); XCTAssertEqual(input.text, address)
        assertReachableEdges(input, in: scroll)
        for action in [cancel, confirm] {
            XCTAssertGreaterThanOrEqual(action.bounds.width, 44); XCTAssertGreaterThanOrEqual(action.bounds.height, 44)
            XCTAssertTrue(action.titleLabel?.adjustsFontForContentSizeCategory == true)
            assertLabelFits(try XCTUnwrap(action.titleLabel), in: action); assertReachableEdges(action, in: scroll)
        }
        XCTAssertLessThan(cancel.convert(cancel.bounds, to: panel).maxY, confirm.convert(confirm.bounds, to: panel).minY)
        XCTAssertEqual(submissions, [address]); XCTAssertEqual(accepted, 0)
        confirm.sendActions(for: .touchUpInside)
        await wait { submissions.count == 2 && confirm.isEnabled }
        XCTAssertTrue(page.presentedViewController === dialog); XCTAssertTrue(error.isHidden); XCTAssertEqual(accepted, 0)
        confirm.sendActions(for: .touchUpInside)
        await wait { page.presentedViewController == nil && accepted == 1 }
        XCTAssertEqual(submissions, [address, address, address])
    }

    func testManagementImportLargeTypeWrapsLongRowsAndReachesLastSelectionAt320() async throws {
        let page = PageViewController(themeProvider: NovelTestPageTheme())
        let window = try mount(page); window.frame = CGRect(x: 0, y: 0, width: 320, height: 568)
        window.layoutIfNeeded(); await wait { page.canPresentPageAlert }
        let items = (0..<14).map { index in
            ManagementImportView.Item(title: "书源 \(index) " + String(repeating: "包含完整名称的阅读来源", count: 3),
                detail: "https://source.invalid/" + String(repeating: "long-description/", count: 5),
                status: "新增：" + String(repeating: "需要完整显示的导入状态", count: 3))
        }
        let preview = ManagementImportView(items: items, selected: [0])
        let dialog = ManagementDialog(title: "导入书源", action: "确定导入", themeProvider: NovelTestPageTheme())
        dialog.body.addArrangedSubview(preview); page.present(dialog, animated: false)
        await wait { dialog.view.window === window && !dialog.isBeingPresented }
        let table = try XCTUnwrap(descendants(preview).compactMap { $0 as? UITableView }.first)
        let all = try XCTUnwrap(descendants(preview).compactMap { $0 as? UIButton }.first { $0.title(for: .normal) == "   全选" })
        let count = try XCTUnwrap(descendants(preview).compactMap { $0 as? UILabel }.first { $0.text == "1/14" })
        let scroll = try XCTUnwrap(descendants(dialog.view).first { $0.accessibilityIdentifier == "management.dialog.scroll" } as? UIScrollView)
        var defaultRowHeight: CGFloat = 0
        for category in [UIContentSizeCategory.large, .accessibilityExtraExtraExtraLarge] {
            dialog.traitOverrides.preferredContentSizeCategory = category
            await wait { preview.traitCollection.preferredContentSizeCategory == category }
            dialog.view.setNeedsLayout(); dialog.view.layoutIfNeeded(); preview.layoutIfNeeded(); table.layoutIfNeeded()
            XCTAssertEqual(dialog.view.bounds.width, 320, accuracy: 0.5)
            XCTAssertEqual(table.numberOfRows(inSection: 0), items.count)
            XCTAssertEqual(preview.selected, [0])
            XCTAssertTrue(all.titleLabel?.adjustsFontForContentSizeCategory == true); XCTAssertTrue(count.adjustsFontForContentSizeCategory)
            XCTAssertGreaterThanOrEqual(all.bounds.width, 44); XCTAssertGreaterThanOrEqual(all.bounds.height, 44)
            assertLabelFits(count, in: preview)
            for index in items.indices {
                table.scrollToRow(at: IndexPath(row: index, section: 0), at: .top, animated: false); table.layoutIfNeeded()
            }
            let first = IndexPath(row: 0, section: 0)
            table.scrollToRow(at: first, at: .top, animated: false); table.layoutIfNeeded()
            let cell = try XCTUnwrap(table.cellForRow(at: first))
            let labels = descendants(cell.contentView).compactMap { $0 as? UILabel }
            let title = try XCTUnwrap(labels.first { $0.text == items[0].title })
            let detail = try XCTUnwrap(labels.first { $0.text == items[0].detail })
            let status = try XCTUnwrap(labels.first { $0.text == items[0].status })
            for label in [title, detail, status] {
                XCTAssertTrue(label.adjustsFontForContentSizeCategory); XCTAssertEqual(label.numberOfLines, 0)
                assertLabelFits(label, in: cell.contentView); assertReachableEdges(label, in: table)
                let frame = label.convert(label.bounds, to: cell.contentView)
                XCTAssertGreaterThanOrEqual(frame.minY, -1)
                XCTAssertLessThanOrEqual(frame.maxY, cell.contentView.bounds.height + 1)
            }
            XCTAssertGreaterThanOrEqual(status.convert(status.bounds, to: cell.contentView).minY, detail.convert(detail.bounds, to: cell.contentView).maxY)
            XCTAssertGreaterThan(cell.bounds.height, 58)
            if category == .large { defaultRowHeight = cell.bounds.height }
            else {
                XCTAssertGreaterThan(cell.bounds.height, defaultRowHeight)
                XCTAssertGreaterThan(title.font.pointSize, 14); XCTAssertGreaterThan(detail.font.pointSize, 12)
                XCTAssertGreaterThan(status.font.pointSize, 12); XCTAssertGreaterThan(count.font.pointSize, 13)
                XCTAssertGreaterThan(all.titleLabel?.font.pointSize ?? 0, 14)
            }
        }
        let last = IndexPath(row: items.count - 1, section: 0)
        table.scrollToRow(at: last, at: .top, animated: false); table.layoutIfNeeded()
        let lastCell = try XCTUnwrap(table.cellForRow(at: last))
        let check = try XCTUnwrap(descendants(lastCell).compactMap { $0 as? UIButton }.first { $0.accessibilityLabel == "选择 " + items[last.row].title })
        XCTAssertGreaterThanOrEqual(check.bounds.width, 44); XCTAssertGreaterThanOrEqual(check.bounds.height, 44)
        assertReachableEdges(check, in: table)
        check.sendActions(for: .touchUpInside)
        XCTAssertEqual(preview.selected, [0, last.row]); XCTAssertEqual(count.text, "2/14")
        all.sendActions(for: .touchUpInside); XCTAssertEqual(preview.selected, Set(items.indices))
        all.sendActions(for: .touchUpInside); XCTAssertTrue(preview.selected.isEmpty); XCTAssertEqual(count.text, "0/14")
        let cancel = try button("management.dialog.cancel", in: dialog.view)
        let confirm = try button("management.dialog.confirm", in: dialog.view)
        assertReachableEdges(confirm, in: scroll); assertReachableEdges(cancel, in: scroll)
        cancel.sendActions(for: .touchUpInside); await wait { page.presentedViewController == nil && page.isPageVisible }
    }

    func testNovelAppearanceInputUsesThemedDialogAndKeepsInvalidValueOpen() async throws {
        let (feature, _) = try await fixture(), theme = NovelTestPageTheme()
        let initialFontSize = feature.local.settings.fontSize
        let page = NovelAppearanceViewController(feature: feature, themeProvider: theme)
        let navigation = PageNavigationController(themeProvider: theme)
        navigation.setViewControllers([page], animated: false)
        let window = try mount(navigation)
        await wait { page.isPageVisible && page.rows.count > 1 }
        page.tableView(page.table, didSelectRowAt: .init(row: 1, section: 0))

        let dialog = try XCTUnwrap(page.presentedViewController as? ManagementDialog)
        let input = try XCTUnwrap(descendants(dialog.view).first {
            $0.accessibilityIdentifier == "novel.settings.input"
        } as? UITextField)
        await wait { dialog.view.window === window && !dialog.isBeingPresented }
        let settings = SettingsTheme(dark: input.traitCollection.userInterfaceStyle == .dark)
        XCTAssertEqual(input.backgroundColor, UIColor(settings.input))
        XCTAssertEqual(input.layer.borderColor, UIColor(settings.inputBorder).cgColor)
        input.text = "-1"
        try button("management.dialog.confirm", in: dialog.view).sendActions(for: .touchUpInside)
        await wait {
            (self.descendants(dialog.view).first {
                $0.accessibilityIdentifier == "management.dialog.error"
            } as? UILabel)?.text == "数值无效"
        }
        XCTAssertTrue(page.presentedViewController === dialog)
        XCTAssertEqual(feature.local.settings.fontSize, initialFontSize)

        input.text = "25"
        try button("management.dialog.confirm", in: dialog.view).sendActions(for: .touchUpInside)
        await wait { page.presentedViewController == nil && feature.local.settings.fontSize == 25 }
        XCTAssertEqual(feature.local.settings.fontSize, 25)
    }

    func testReplacementQrImageDecodesTheOriginalRuleTextAndRejectsNonImages() throws {
        let text = #"{"id":42,"pattern":"advertisement","replacement":""}"#
        let filter = try XCTUnwrap(CIFilter(name: "CIQRCodeGenerator"))
        filter.setValue(Data(text.utf8), forKey: "inputMessage")
        let output = try XCTUnwrap(filter.outputImage).transformed(by: CGAffineTransform(scaleX: 8, y: 8))
        let pixels = try XCTUnwrap(CIContext().createCGImage(output, from: output.extent))
        let data = try XCTUnwrap(UIImage(cgImage: pixels).pngData())
        XCTAssertEqual(try NovelRulesViewController.readRuleQr(data), text)
        XCTAssertThrowsError(try NovelRulesViewController.readRuleQr(Data("not an image".utf8)))
    }
    func testEmptyAndPopulatedShelfKeepNativeSelectorsAndReturnToSameBook() async throws {
        let (feature, _) = try await fixture(), theme = NovelTestPageTheme()
        let page = NovelHomeViewController(feature: feature, themeProvider: theme)
        let navigation = PageNavigationController(themeProvider: theme); navigation.setViewControllers([page], animated: false)
        let window = try mount(navigation); page.showShelf()
        await wait { page.isPageVisible }
        let table = try XCTUnwrap(descendants(page.view).first { $0.accessibilityIdentifier == "novel.books" } as? UITableView)
        XCTAssertEqual(table.numberOfRows(inSection: 0), 0)
        let segments = try XCTUnwrap(descendants(page.view).first { $0.accessibilityIdentifier == "novel.sections" } as? UISegmentedControl)
        XCTAssertFalse(segments.isHidden)
        XCTAssertGreaterThan(segments.bounds.width, 0)
        XCTAssertNotNil(descendants(page.view).first { $0.accessibilityIdentifier == "novel.books.empty" })
        attach(window, "novel-shelf-empty")
        let book = NovelBook(installationId: "fixture", bookUrl: "https://fixture.invalid/book", name: "雨夜来信", author: "林舟", intro: "一封迟来的信，抵达雨中的旧城。")
        try await feature.setShelf(book, present: true)
        await wait { table.numberOfRows(inSection: 0) == 1 }
        XCTAssertEqual(table.cellForRow(at: .init(row: 0, section: 0))?.textLabel?.text, book.name)
        attach(window, "novel-shelf-book")
        page.onSelect = { book in navigation.pushViewController(NovelDetailViewController(book: book, feature: feature, themeProvider: theme), animated: false) }
        page.tableView(table, didSelectRowAt: .init(row: 0, section: 0))
        XCTAssertTrue(navigation.topViewController is NovelDetailViewController)
        XCTAssertTrue(feature.local.books(history: true).isEmpty)
        navigation.popViewController(animated: false)
        XCTAssertTrue(navigation.topViewController === page)
        XCTAssertEqual(table.numberOfRows(inSection: 0), 1)
    }
    func testReaderFourModesNativeLayoutProgressPurchaseAndBackgroundPause() async throws {
        let (feature, runtime) = try await fixture(), theme = NovelTestPageTheme()
        let session = feature.makeReader(book: .init(installationId: "fixture", bookUrl: "https://fixture.invalid/book", name: "雨夜来信", author: "林舟"))
        let reader = NovelReaderViewController(session: session, themeProvider: theme)
        let navigation = PageNavigationController(themeProvider: theme); navigation.setViewControllers([reader], animated: false)
        let window = try mount(navigation)
        await wait { !session.loading && session.content != nil && reader.isPageVisible }
        XCTAssertGreaterThan(try button("novel.reader.catalog", in: reader.view).bounds.height, 43)
        for mode in NovelReadingMode.allCases {
            var settings = feature.local.settings; settings.mode = mode
            try await feature.saveSettings(settings)
            await wait { !session.loading }
            await Task.yield(); reader.view.layoutIfNeeded()
            let scroll = try XCTUnwrap(descendants(reader.view).compactMap { $0 as? UIScrollView }.first)
            XCTAssertEqual(scroll.isScrollEnabled, mode == .scroll)
            XCTAssertGreaterThan(scroll.bounds.height, 0)
            XCTAssertLessThanOrEqual(scroll.frame.maxY, reader.view.safeAreaLayoutGuide.layoutFrame.maxY)
            let progress = try XCTUnwrap(descendants(reader.view).first { $0.accessibilityIdentifier == "novel.reader.progress" } as? UISlider)
            progress.value = 0.45; progress.sendActions(for: .valueChanged)
            XCTAssertGreaterThan(session.anchor?.ratio ?? 0, 0)
            attach(window, "novel-reader-\(mode.rawValue)")
        }
        let purchase = try button("novel.reader.purchase", in: reader.view)
        purchase.sendActions(for: .touchUpInside)
        await wait { reader.presentedViewController is NovelPurchaseConfirmationViewController }
        let confirmation = try XCTUnwrap(reader.presentedViewController as? NovelPurchaseConfirmationViewController)
        XCTAssertEqual(confirmation.pageTitle, "购买本章")
        let names = descendants(confirmation.view).compactMap { ($0 as? UILabel)?.text }
        XCTAssertTrue(names.contains("雨夜来信")); XCTAssertTrue(names.contains("第一章 雨声")); XCTAssertTrue(names.contains("合成书源"))
        attach(window, "novel-purchase-confirmation")
        let before = await runtime.purchases; XCTAssertEqual(before, 0)
        try button("novel.purchase.cancel", in: confirmation.view).sendActions(for: .touchUpInside)
        await wait { reader.presentedViewController == nil }
        let afterCancel = await runtime.purchases; XCTAssertEqual(afterCancel, 0)
        purchase.sendActions(for: .touchUpInside)
        await wait { reader.presentedViewController is NovelPurchaseConfirmationViewController }
        let accepted = try XCTUnwrap(reader.presentedViewController as? NovelPurchaseConfirmationViewController)
        try button("novel.purchase.confirm", in: accepted.view).sendActions(for: .touchUpInside)
        await wait { !session.loading && session.purchasableChapter == nil }
        let afterConsent = await runtime.purchases; XCTAssertEqual(afterConsent, 1)
        try button("novel.reader.automatic", in: reader.view).sendActions(for: .touchUpInside)
        await wait { session.automatic }
        NotificationCenter.default.post(name: UIApplication.willResignActiveNotification, object: nil)
        await wait { !session.automatic }
        await session.flush()
        XCTAssertFalse(feature.local.books(history: true).isEmpty)
    }
    func testVisibleReaderWithOpenMenuDoesNotRepublishAutomaticWhileIdle() async throws {
        let (feature, _) = try await fixture(), theme = NovelTestPageTheme()
        let session = feature.makeReader(book: .init(installationId: "fixture", bookUrl: "https://fixture.invalid/book", name: "雨夜来信", author: "林舟"))
        let reader = NovelReaderViewController(session: session, themeProvider: theme)
        let navigation = PageNavigationController(themeProvider: theme); navigation.setViewControllers([reader], animated: false)
        let window = try mount(navigation)
        await wait { !session.loading && session.content != nil && reader.isPageVisible }
        await session.flush()
        XCTAssertFalse(session.automatic)
        XCTAssertFalse(try button("novel.reader.catalog", in: reader.view).isHidden)
        let anchor = session.anchor
        let quiet = expectation(description: "A stable reader with its menu open must not publish automatic again")
        quiet.isInverted = true; quiet.assertForOverFulfill = false
        var automaticPublications = 0
        let observation = session.$automatic.dropFirst().sink { _ in
            automaticPublications += 1; quiet.fulfill()
        }
        defer { observation.cancel() }
        // A legitimate equal local snapshot also renders the reader; rendering
        // must not write the already-false automatic state back into its model.
        try await feature.reloadLocal()
        window.layoutIfNeeded()
        await fulfillment(of: [quiet], timeout: 0.35)
        XCTAssertEqual(automaticPublications, 0)
        XCTAssertEqual(session.anchor, anchor)
    }
    func testTextImageAnchorsSurviveFontSafeAreaAndImageArrivalWithoutBreakingGraphemes() throws {
        let text = String(repeating: "雨声落在窗边。👨‍👩‍👧‍👦\n", count: 80)
        let content = NovelContent(title: "第一章", blocks: [.init(text: text), .init(src: "https://fixture.invalid/image"), .init(text: "尾声")])
        var settings = NovelReadingSettings(); settings.mode = .slide
        let chapter = NovelChapter(name: "第一章", chapterUrl: "chapter", index: 0)
        let before = NovelReaderLayout.pages(content: content, settings: settings, size: .init(width: 390, height: 600))
        XCTAssertGreaterThan(before.count, 1)
        let anchor = NovelReaderLayout.anchor(at: 0, page: before[2], chapter: chapter)
        XCTAssertGreaterThan(anchor.ratio, 0)
        settings.fontSize = 29; settings.margin = 30; settings.lineSpacing = 12
        let after = NovelReaderLayout.pages(content: content, settings: settings, size: .init(width: 320, height: 420), imageSizes: [1: .init(width: 1400, height: 6000)])
        let index = NovelReaderLayout.page(for: anchor, pages: after)
        XCTAssertTrue(after[index].elements.contains { $0.blockIndex == anchor.blockIndex && $0.range.map { NSLocationInRange(anchor.utf16Offset, $0) } == true })
        let ns = text as NSString
        XCTAssertEqual(ns.rangeOfComposedCharacterSequence(at: anchor.utf16Offset).location, anchor.utf16Offset)
        let imageAnchor = NovelAnchor(chapterUrl: "chapter", chapterTitle: chapter.name, chapterIndex: 0, blockIndex: 1,
            ratio: (Double(text.utf16.count) + 0.6) / Double(text.utf16.count + 1 + 2))
        let imagePage = NovelReaderLayout.page(for: imageAnchor, pages: after)
        XCTAssertTrue(after[imagePage].elements.contains { $0.blockIndex == 1 })
        settings.mode = .scroll
        let continuous = NovelReaderLayout.pages(content: content, settings: settings, size: .init(width: 320, height: 420), imageSizes: [1: .init(width: 1400, height: 6000)])
        let restored = NovelReaderLayout.anchor(at: NovelReaderLayout.y(for: imageAnchor, page: continuous[0]), page: continuous[0], chapter: chapter)
        XCTAssertEqual(restored.blockIndex, imageAnchor.blockIndex)
        XCTAssertEqual(restored.ratio, imageAnchor.ratio, accuracy: 0.00001)
        let empty = NovelReaderLayout.pages(content: .init(title: "", blocks: []), settings: settings, size: .init(width: 320, height: 420))
        XCTAssertEqual(empty.count, 1); XCTAssertGreaterThan(empty[0].height, 0)
    }
    func testSelectedFontWeightChangesRealGlyphAttributesAndPagination() throws {
        var settings = NovelReadingSettings(); settings.fontName = "HelveticaNeue"; settings.fontSize = 23
        let content = NovelContent(title: "字重", blocks: [.init(text: String(repeating: "Native reading typography 雨夜来信。", count: 80))])
        let regular = NovelReaderLayout.pages(content: content, settings: settings, size: .init(width: 320, height: 440))
        let first = try XCTUnwrap(regular.first?.elements.first?.text?.attribute(.font, at: 0, effectiveRange: nil) as? UIFont)
        settings.fontWeight = 0.35
        let bold = NovelReaderLayout.pages(content: content, settings: settings, size: .init(width: 320, height: 440))
        let second = try XCTUnwrap(bold.first?.elements.first?.text?.attribute(.font, at: 0, effectiveRange: nil) as? UIFont)
        XCTAssertEqual(first.familyName, second.familyName)
        XCTAssertNotEqual(first.fontName, second.fontName)
        XCTAssertTrue(second.fontDescriptor.symbolicTraits.contains(.traitBold))
        let traits = try XCTUnwrap(second.fontDescriptor.object(forKey: .traits) as? [UIFontDescriptor.TraitKey: Any])
        XCTAssertGreaterThan((traits[.weight] as? NSNumber)?.doubleValue ?? 0, 0)
        XCTAssertGreaterThan(bold.count, 1); XCTAssertGreaterThan(regular.count, 1)
    }
    func testActualReaderKeepsTextAnchorAcrossEveryModeFontAndSafeAreaReflow() async throws {
        let (feature, _) = try await fixture()
        var settings = feature.local.settings; settings.mode = .scroll; try await feature.saveSettings(settings)
        let session = feature.makeReader(book: .init(installationId: "fixture", bookUrl: "https://fixture.invalid/book", name: "雨夜来信", author: "林舟"))
        let reader = NovelReaderViewController(session: session, themeProvider: NovelTestPageTheme())
        let navigation = PageNavigationController(themeProvider: NovelTestPageTheme()); navigation.setViewControllers([reader], animated: false)
        let window = try mount(navigation)
        await wait { session.content != nil && !session.loading && session.anchor != nil && reader.isPageVisible }
        let scroll = try XCTUnwrap(descendants(reader.view).compactMap { $0 as? UIScrollView }.first)
        let canvas = try XCTUnwrap(descendants(reader.view).compactMap { $0 as? NovelReaderCanvas }.first)
        scroll.setContentOffset(.init(x: 0, y: 250), animated: false)
        await wait { (session.anchor?.utf16Offset ?? 0) > 0 }
        let anchor = try XCTUnwrap(session.anchor)
        let text = try XCTUnwrap(session.content?.blocks[anchor.blockIndex].text) as NSString
        XCTAssertEqual(text.rangeOfComposedCharacterSequence(at: anchor.utf16Offset).location, anchor.utf16Offset)
        for mode in NovelReadingMode.allCases {
            settings.mode = mode; settings.fontSize = 29; settings.fontName = "HelveticaNeue"; settings.fontWeight = 0.35
            settings.margin = 29; settings.lineSpacing = 11
            try await feature.saveSettings(settings)
            await wait { !session.loading }
            reader.additionalSafeAreaInsets = .init(top: 18, left: 10, bottom: 54, right: 10)
            reader.view.setNeedsLayout(); reader.view.layoutIfNeeded(); await Task.yield()
            XCTAssertEqual(session.anchor, anchor, "Reflow is not a user movement to a new line start")
            XCTAssertTrue(canvas.page.elements.contains {
                $0.blockIndex == anchor.blockIndex && $0.range.map { NSLocationInRange(anchor.utf16Offset, $0) } == true
            })
            if mode == .scroll {
                XCTAssertEqual(scroll.contentOffset.y, NovelReaderLayout.y(for: anchor, page: canvas.page), accuracy: 1)
            }
        }
        attach(window, "novel-reader-text-anchor")
        navigation.pushViewController(PageViewController(themeProvider: NovelTestPageTheme()), animated: false)
        navigation.popViewController(animated: false)
        await wait { reader.isPageVisible }
        XCTAssertEqual(session.anchor, anchor)
    }
    func testActualReaderKeepsImageAnchorWhenHighResolutionArrivesFontAndSafeAreaChange() async throws {
        let (feature, runtime) = try await fixture()
        let format = UIGraphicsImageRendererFormat(); format.scale = 1
        let bytes = UIGraphicsImageRenderer(size: .init(width: 600, height: 3600), format: format).pngData { context in
            UIColor.systemIndigo.setFill(); context.fill(CGRect(x: 0, y: 0, width: 600, height: 3600))
        }
        let entered = expectation(description: "actual reader requested the visible image")
        await runtime.configureImage(bytes, entered: { entered.fulfill() })
        var settings = feature.local.settings; settings.mode = .scroll; try await feature.saveSettings(settings)
        let session = feature.makeReader(book: .init(installationId: "fixture", bookUrl: "https://fixture.invalid/book", name: "雨夜来信", author: "林舟"))
        let reader = NovelReaderViewController(session: session, themeProvider: NovelTestPageTheme())
        let navigation = PageNavigationController(themeProvider: NovelTestPageTheme()); navigation.setViewControllers([reader], animated: false)
        let window = try mount(navigation)
        await fulfillment(of: [entered], timeout: 5)
        await wait { session.content != nil && !session.loading && session.anchor != nil }
        let scroll = try XCTUnwrap(descendants(reader.view).compactMap { $0 as? UIScrollView }.first)
        let canvas = try XCTUnwrap(descendants(reader.view).compactMap { $0 as? NovelReaderCanvas }.first)
        scroll.setContentOffset(.init(x: 0, y: 60), animated: false)
        await wait { session.anchor?.blockIndex == 0 && (session.anchor?.ratio ?? 0) > 0 }
        let anchor = try XCTUnwrap(session.anchor)
        XCTAssertEqual(anchor.utf16Offset, 0)
        await runtime.acceptImage()
        await wait { canvas.images[0]?.size.height == 3600 }
        XCTAssertEqual(session.anchor?.blockIndex, 0)
        XCTAssertEqual(try XCTUnwrap(session.anchor?.ratio), anchor.ratio, accuracy: 0.000001)
        settings.fontSize = 28; settings.margin = 26; try await feature.saveSettings(settings)
        await wait { !session.loading }
        reader.additionalSafeAreaInsets = .init(top: 20, left: 0, bottom: 60, right: 0)
        reader.view.setNeedsLayout(); reader.view.layoutIfNeeded()
        await Task.yield()
        XCTAssertEqual(session.anchor?.blockIndex, 0)
        XCTAssertEqual(try XCTUnwrap(session.anchor?.ratio), anchor.ratio, accuracy: 0.000001)
        XCTAssertGreaterThan(canvas.page.elements.first?.imageHeight ?? 0, scroll.bounds.height)
        XCTAssertTrue(canvas.images.values.allSatisfy { $0.size.height == 3600 }, "The reader uses the full decoded image rather than artwork thumbnails")
        attach(window, "novel-reader-late-image-anchor")
        let ordinary = PageViewController(themeProvider: NovelTestPageTheme())
        navigation.pushViewController(ordinary, animated: false)
        await wait { !reader.isPageVisible && canvas.images.isEmpty }
        navigation.popViewController(animated: false)
        await wait { reader.isPageVisible && !canvas.images.isEmpty }
        XCTAssertEqual(session.anchor?.blockIndex, 0)
        XCTAssertEqual(try XCTUnwrap(session.anchor?.ratio), anchor.ratio, accuracy: 0.000001)
        let releases = await runtime.imageReleases; XCTAssertGreaterThanOrEqual(releases, 1)
    }
    func testRetainedReaderAppliesChangedRulesAndConversionWhenItBecomesVisibleAgain() async throws {
        let (feature, runtime) = try await fixture(), theme = NovelTestPageTheme()
        let session = feature.makeReader(book: .init(installationId: "fixture", bookUrl: "https://fixture.invalid/book", name: "雨夜来信"))
        let reader = NovelReaderViewController(session: session, themeProvider: theme)
        let navigation = PageNavigationController(themeProvider: theme)
        navigation.setViewControllers([reader], animated: false); _ = try mount(navigation)
        await wait { reader.isPageVisible && !session.loading && session.content != nil }
        let before = try XCTUnwrap(session.content), anchor = try XCTUnwrap(session.anchor)
        navigation.pushViewController(PageViewController(themeProvider: theme), animated: false)
        await wait { !reader.isPageVisible }
        var rule = NovelRule(); rule.pattern = "雨"; rule.replacement = "书"
        try await feature.saveRules([rule])
        var settings = feature.local.settings; settings.conversion = "traditional"
        try await feature.saveSettings(settings)
        XCTAssertFalse(session.loading); XCTAssertFalse(session.closed)
        XCTAssertEqual(session.content, before); XCTAssertEqual(session.anchor, anchor)
        navigation.popViewController(animated: false)
        await wait { reader.isPageVisible && !session.loading && session.content?.blocks.first?.text?.hasPrefix("書落") == true }
        XCTAssertEqual(session.anchor, anchor)
        let bodyCalls = await runtime.contentCalls; XCTAssertEqual(bodyCalls, 1)
        navigation.pushViewController(PageViewController(themeProvider: theme), animated: false)
        await wait { !reader.isPageVisible }
        navigation.popViewController(animated: false)
        await wait { reader.isPageVisible }
        XCTAssertFalse(session.loading)
        let after = await runtime.contentCalls; XCTAssertEqual(after, bodyCalls)
    }
    func testCancelledPurchaseConfirmationResumesPendingDisplaySettingsWithoutPurchasing() async throws {
        let (feature, runtime) = try await fixture(), theme = NovelTestPageTheme()
        let session = feature.makeReader(book: .init(installationId: "fixture", bookUrl: "https://fixture.invalid/book", name: "雨夜来信"))
        let reader = NovelReaderViewController(session: session, themeProvider: theme)
        let navigation = PageNavigationController(themeProvider: theme)
        navigation.setViewControllers([reader], animated: false); _ = try mount(navigation)
        await wait { reader.isPageVisible && !session.loading && session.content != nil }
        let before = session.content, anchor = session.anchor
        try button("novel.reader.purchase", in: reader.view).sendActions(for: .touchUpInside)
        await wait { reader.presentedViewController is NovelPurchaseConfirmationViewController }
        let confirmation = try XCTUnwrap(reader.presentedViewController as? NovelPurchaseConfirmationViewController)
        var rule = NovelRule(); rule.pattern = "雨"; rule.replacement = "书"
        try await feature.saveRules([rule])
        var settings = feature.local.settings; settings.conversion = "traditional"
        try await feature.saveSettings(settings)
        XCTAssertFalse(session.loading); XCTAssertEqual(session.content, before); XCTAssertEqual(session.anchor, anchor)
        try button("novel.purchase.cancel", in: confirmation.view).sendActions(for: .touchUpInside)
        await wait { reader.presentedViewController == nil && !session.loading && session.content?.blocks.first?.text?.hasPrefix("書落") == true }
        XCTAssertEqual(session.anchor, anchor)
        let purchases = await runtime.purchases, bodies = await runtime.contentCalls
        XCTAssertEqual(purchases, 0); XCTAssertEqual(bodies, 1)
    }
    func testHomeShelfAndHiddenRefreshCancelDiscoveryAndOnlyUseTheCurrentRevision() async throws {
        let (feature, runtime) = try await fixture()
        let entered = expectation(description: "discovery entered"), cancelled = expectation(description: "discovery physically cancelled")
        await runtime.configureExplore(block: true, entered: { entered.fulfill() }, cancelled: { cancelled.fulfill() })
        try await feature.refreshSources()
        let home = NovelHomeViewController(feature: feature, themeProvider: NovelTestPageTheme())
        let navigation = PageNavigationController(themeProvider: NovelTestPageTheme()); navigation.setViewControllers([home], animated: false)
        _ = try mount(navigation)
        await fulfillment(of: [entered], timeout: 5)
        home.showShelf()
        await fulfillment(of: [cancelled], timeout: 5)
        let before = await runtime.exploreCalls
        let table = try XCTUnwrap(descendants(home.view).first { $0.accessibilityIdentifier == "novel.books" } as? UITableView)
        table.refreshControl?.beginRefreshing(); table.refreshControl?.sendActions(for: .valueChanged)
        await wait { table.refreshControl?.isRefreshing == false }
        let whileShelf = await runtime.exploreCalls; XCTAssertEqual(whileShelf, before)
        await runtime.configureExplore(block: false, revision: 2)
        try await feature.refreshSources()
        await Task.yield()
        let afterSourceChange = await runtime.exploreCalls; XCTAssertEqual(afterSourceChange, before)
        home.showHome()
        await wait { table.numberOfRows(inSection: 0) == 1 }
        let refreshed = await runtime.exploreCalls; XCTAssertTrue(refreshed.contains("explore:2"))
        navigation.pushViewController(PageViewController(themeProvider: NovelTestPageTheme()), animated: false)
        await wait { !home.isPageVisible }
        let hidden = await runtime.exploreCalls
        table.refreshControl?.beginRefreshing(); table.refreshControl?.sendActions(for: .valueChanged)
        await wait { table.refreshControl?.isRefreshing == false }
        let afterHiddenRefresh = await runtime.exploreCalls; XCTAssertEqual(afterHiddenRefresh, hidden)
    }
}

@MainActor
private final class NovelManagementPageTheme: PageThemeProviding {
    private(set) var pagePalette: PagePalette = .preset(.lilac)
    let animatesPageColorway = false
    private let changes = PassthroughSubject<Void, Never>()
    var pageThemeChanges: AnyPublisher<Void, Never> { changes.eraseToAnyPublisher() }
    func setPalette(_ palette: PagePalette) { pagePalette = palette; changes.send(()) }
}

private actor NovelUIKitRuntime: NovelRuntimeServing {
    var purchases = 0
    var contentCalls = 0
    var imageReleases = 0
    private var imageBytes: Data?
    private var imageEntered: (@Sendable () -> Void)?
    private var imageContinuation: CheckedContinuation<Data, Never>?
    private var imageAccepted = false
    private var exploreEnabled = false, blockExplore = false
    private var revision = 1
    private var exploreEntered: (@Sendable () -> Void)?, exploreCancelled: (@Sendable () -> Void)?
    var exploreCalls: [String] = []
    func configureImage(_ bytes: Data, entered: @escaping @Sendable () -> Void) { imageBytes = bytes; imageEntered = entered }
    func acceptImage() { imageAccepted = true; imageContinuation?.resume(returning: imageBytes ?? Data()); imageContinuation = nil }
    func configureExplore(block: Bool, revision: Int = 1, entered: (@Sendable () -> Void)? = nil, cancelled: (@Sendable () -> Void)? = nil) {
        exploreEnabled = true; blockExplore = block; self.revision = revision; exploreEntered = entered; exploreCancelled = cancelled
    }
    func hasActiveCalls() -> Bool { false }
    func setActivityHandler(_ handler: (@Sendable (Bool) -> Void)?) {}
    func close() {}
    func image(_ reference: String) async throws -> Data {
        guard let imageBytes else { throw NovelError.invalidResponse }
        if imageAccepted { return imageBytes }
        return await withCheckedContinuation { imageContinuation = $0; imageEntered?(); imageEntered = nil }
    }
    func call(_ operation: String, _ parameters: RuntimeValue) async throws -> RuntimeValue {
        if operation == "library.image" { return .object(["reference": .string("/v1/source/images/fixture")]) }
        if operation == "library.releaseImage" { imageReleases += 1; return .object(["released": .bool(true)]) }
        if operation == "library.list" {
            return .object(["sources": .array([.object(["installationId": .string("fixture"), "revision": .number(Double(revision)), "name": .string("合成书源"),
                "sourceUrl": .string("https://fixture.invalid/"), "group": .string(""), "order": .number(0), "enabled": .bool(true), "enabledExplore": .bool(exploreEnabled),
                "protocol": .string("qysg"), "actions": .array(["search", "bookInfo", "toc", "content", "payAction", "exploreKinds", "explore"].map { .string($0) })])])])
        }
        let payload = parameters["payload"]
        switch parameters["action"].string {
        case "bookInfo": return .object(["book": payload["book"]])
        case "toc": return .object(["chapters": .array([NovelChapter(name: "第一章 雨声", chapterUrl: "https://fixture.invalid/1", index: 0, isPay: purchases > 0, isVip: true).wire])])
        case "content":
            contentCalls += 1
            var blocks: [RuntimeValue] = [.object(["type": .string("text"),
                "text": .string(String(repeating: purchases > 0 ? "已购正文。雨夜来信。\n" : "雨落在青石街道上。她推开旧书店的门，信笺仍放在窗前。\n", count: 90))])]
            if imageBytes != nil { blocks.insert(.object(["type": .string("image"), "src": .string("https://fixture.invalid/image")]), at: 0) }
            return .object(["title": .string("第一章 雨声"), "chapter": payload["chapter"], "blocks": .array(blocks)])
        case "exploreKinds":
            exploreCalls.append("kinds:\(revision)")
            return .object(["kinds": .array([.object(["title": .string("全部"), "url": .string("https://fixture.invalid/explore")])])])
        case "explore":
            exploreCalls.append("explore:\(revision)")
            let entered = exploreEntered; exploreEntered = nil; entered?()
            if blockExplore {
                do { try await Task.sleep(nanoseconds: 60_000_000_000) }
                catch { let cancelled = exploreCancelled; exploreCancelled = nil; cancelled?(); throw error }
            }
            return .object(["books": .array([.object(["bookUrl": .string("https://fixture.invalid/book"), "name": .string("雨夜来信"), "author": .string("林舟")])])])
        case "payAction": purchases += 1; return .bool(true)
        default: throw NovelError.invalidResponse
        }
    }
}
#endif
