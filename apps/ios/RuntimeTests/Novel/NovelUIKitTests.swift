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
    private func novelShell(_ home: NovelHomeViewController, theme: any PageThemeProviding) -> MeowfilmPrimaryShellViewController {
        let factories: [@MainActor () -> UIViewController] = MeowfilmPrimaryPage.allCases.map { page in
            { page == .novel ? home : UIViewController() }
        }
        let shell = MeowfilmPrimaryShellViewController(pageFactories: factories, themeProvider: theme, openMenu: {}, openSearch: {})
        shell.loadViewIfNeeded(); shell.select(.novel)
        return shell
    }
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
                    XCTAssertEqual(table.convert(table.bounds, to: page.view).maxY,
                        page.view.safeAreaLayoutGuide.layoutFrame.maxY - 4, accuracy: 0.5)
                    XCTAssertEqual(table.layer.cornerRadius, 22)
                    XCTAssertTrue(table.clipsToBounds)
                    XCTAssertEqual(manager.manager.search.searchTextField.backgroundColor, UIColor(settings.input))
                    XCTAssertEqual(manager.manager.search.searchTextField.textColor, UIColor(settings.ink))
                    XCTAssertEqual(manager.manager.search.searchTextField.convert(manager.manager.search.searchTextField.bounds, to: page.view).minY -
                        page.pageHeader.convert(page.pageHeader.bounds, to: page.view).maxY,
                        SettingsVisualMetrics.contentTopSpacing, accuracy: 0.5)
                    XCTAssertFalse(manager.manager.tools.isHidden)
                    XCTAssertEqual(manager.manager.tools.arrangedSubviews.count, page is NovelSourcesViewController ? 3 : 2)
                    XCTAssertEqual(manager.manager.tools.convert(manager.manager.tools.bounds, to: page.view).minY,
                        manager.manager.search.searchTextField.convert(manager.manager.search.searchTextField.bounds, to: page.view).minY,
                        accuracy: 0.5)
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
                            XCTAssertEqual(field.backgroundColor, .clear)
                            XCTAssertEqual(field.layer.borderWidth, 0)
                            XCTAssertEqual(field.layer.cornerRadius, 0)
                            XCTAssertEqual((field as? SettingsTextField)?.inputAppearance, .plain)
                        } else if let field = input as? UITextView {
                            XCTAssertEqual(field.textColor, UIColor(settings.ink))
                            XCTAssertEqual(field.backgroundColor, .clear)
                            XCTAssertEqual(field.layer.borderWidth, 0)
                            XCTAssertEqual(field.layer.cornerRadius, 0)
                            XCTAssertEqual((field as? SettingsTextView)?.inputAppearance, .plain)
                        }
                    }
                    let dividers = form.fields.arrangedSubviews.filter { $0.subviews.isEmpty }
                    XCTAssertFalse(dividers.isEmpty)
                    for divider in dividers {
                        XCTAssertFalse(divider.isHidden)
                        XCTAssertGreaterThan(divider.bounds.height, 0)
                        XCTAssertEqual(divider.bounds.height, SettingsVisualMetrics.separatorHeight,
                            accuracy: 0.5 / form.traitCollection.displayScale + 0.001,
                            "The separator may round to either adjacent physical pixel height")
                        XCTAssertEqual(divider.backgroundColor?.resolvedColor(with: form.traitCollection), UIColor(settings.separator))
                        XCTAssertEqual(divider.bounds.width, form.fields.bounds.width - (form.boxedInputs ? 32 : 0),
                            accuracy: 0.5)
                    }
                    if page is NovelRuleEditor {
                        XCTAssertEqual(form.fields.convert(form.fields.bounds, to: page.view).minY -
                            page.pageHeader.convert(page.pageHeader.bounds, to: page.view).maxY,
                            SettingsVisualMetrics.contentTopSpacing, accuracy: 0.5)
                    }
                }
                if page is NovelSourceEditor {
                    let panel = try XCTUnwrap(descendants(page.view).first { $0.accessibilityIdentifier == "novel.source.panel" })
                    XCTAssertEqual(panel.backgroundColor, UIColor(settings.surface))
                    XCTAssertEqual(panel.convert(panel.bounds, to: page.view).minY -
                        page.pageHeader.convert(page.pageHeader.bounds, to: page.view).maxY,
                        SettingsVisualMetrics.contentTopSpacing, accuracy: 0.5)
                    let type = try XCTUnwrap(descendants(panel).compactMap { $0 as? UIButton }.first { $0.accessibilityLabel == "内容类型" })
                    XCTAssertEqual(type.configuration?.baseForegroundColor, UIColor(settings.ink))
                    XCTAssertEqual(type.configuration?.background.backgroundColor, UIColor(settings.input))
                }
                attach(window, "settings-novel-\(index)-\(style == .dark ? "dark" : "light")")
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
        let more = try XCTUnwrap(descendants(page.manager.tools).compactMap { $0 as? UIButton }.first { $0.accessibilityLabel == "更多" })
        let create = try XCTUnwrap(more.menu?.children.compactMap { $0 as? UIMenu }.first { $0.title == "新建书源" })
        XCTAssertEqual(create.children.map(\.title), ["阅读源", "轻悦时光"])
        let groups = try XCTUnwrap(descendants(page.manager.tools).compactMap { $0 as? UIButton }.first { $0.accessibilityLabel == "分组与协议" })
        let protocols = try XCTUnwrap(groups.menu?.children.compactMap { $0 as? UIMenu }.first { $0.title == "书源协议" })
        XCTAssertEqual(protocols.children.map(\.title), ["全部协议", "阅读源", "轻悦时光"])
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
        await feature.rules.close()
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
        XCTAssertEqual(input.backgroundColor, .clear); XCTAssertEqual(input.textColor, UIColor(settings.ink))
        XCTAssertEqual(input.layer.borderWidth, 0)
        XCTAssertEqual((input as? SettingsTextField)?.inputAppearance, .underline)
        XCTAssertEqual((input.superview as? SettingsInputSection)?.titleLabel.text, "书源地址")
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
        let fieldTitle = String(repeating: "书源网络地址", count: 4)
        let input = ManagementDialog.input(placeholder: "网络地址")
        let address = "https://source.invalid/" + String(repeating: "long-path/", count: 20)
        input.keyboardType = .URL; input.text = address
        dialog.addInput(input, title: fieldTitle)
        let label = try XCTUnwrap(dialog.body.arrangedSubviews.first as? SettingsInputSection).titleLabel
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
        XCTAssertEqual(label.font.pointSize, 15, accuracy: 0.1)
        XCTAssertEqual(confirm.titleLabel?.font.pointSize ?? 0, 14, accuracy: 0.1)
        dialog.traitOverrides.preferredContentSizeCategory = .accessibilityExtraExtraExtraLarge
        await wait {
            headingLabel.font.pointSize > 17 && error.font.pointSize > 13 &&
                (input.font?.pointSize ?? 0) > 14 && (confirm.titleLabel?.font.pointSize ?? 0) > 14 && label.font.pointSize > 15
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
        XCTAssertEqual((input as? SettingsTextField)?.inputAppearance, .underline)
        XCTAssertEqual(input.backgroundColor, .clear)
        XCTAssertEqual(input.layer.borderWidth, 0)
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
        XCTAssertEqual(input.backgroundColor, .clear)
        XCTAssertEqual(input.layer.borderWidth, 0)
        XCTAssertEqual((input as? SettingsTextField)?.inputAppearance, .underline)
        XCTAssertEqual((input.superview as? SettingsInputSection)?.titleLabel.text, input.accessibilityLabel)
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
    func testDiscoveryEmptyAndLoadedListReturnsFromDetailsWithoutReloading() async throws {
        let (feature, runtime) = try await fixture(), theme = NovelTestPageTheme()
        let page = NovelHomeViewController(feature: feature, themeProvider: theme)
        let navigation = PageNavigationController(themeProvider: theme); navigation.setViewControllers([novelShell(page, theme: theme)], animated: false)
        let window = try mount(navigation)
        await wait { page.isPageVisible && !page.discovery.loading }
        let table = try XCTUnwrap(descendants(page.view).first { $0.accessibilityIdentifier == "novel.books" } as? UITableView)
        XCTAssertEqual(table.numberOfRows(inSection: 0), 0)
        XCTAssertEqual((table.backgroundView as? UILabel)?.text, "没有可发现书源")
        attach(window, "novel-home-empty")
        await runtime.configureExplore(block: false)
        try await feature.refreshSources()
        await wait { !page.discovery.loading && table.numberOfRows(inSection: 0) == 1 }
        table.layoutIfNeeded()
        let book = try XCTUnwrap(page.discovery.books.first)
        XCTAssertEqual(book.name, "雨夜来信")
        let cell = try XCTUnwrap(table.cellForRow(at: .init(row: 0, section: 0)) as? NovelBookCell)
        XCTAssertEqual((descendants(cell).first { $0.accessibilityIdentifier == "novel.book.title" } as? UILabel)?.text, book.name)
        attach(window, "novel-home-book")
        let calls = await runtime.exploreCalls
        page.onSelect = { book in navigation.pushViewController(NovelDetailViewController(book: book, feature: feature, themeProvider: theme), animated: false) }
        page.tableView(table, didSelectRowAt: .init(row: 0, section: 0))
        XCTAssertTrue(navigation.topViewController is NovelDetailViewController)
        XCTAssertTrue(feature.local.books(history: true).isEmpty)
        navigation.popViewController(animated: false)
        await wait { page.isPageVisible }
        XCTAssertEqual(page.discovery.books, [book])
        XCTAssertEqual(table.numberOfRows(inSection: 0), 1)
        let returnedCalls = await runtime.exploreCalls
        XCTAssertEqual(returnedCalls, calls)
    }
    func testDismissedDiscoveryFailureCanReopenRetryAndKeepTheFailedPage() async throws {
        let (feature, runtime) = try await fixture(), theme = NovelTestPageTheme()
        await runtime.configureExplore(block: false)
        try await feature.refreshSources()
        let page = NovelHomeViewController(feature: feature, themeProvider: theme)
        let shell = novelShell(page, theme: theme)
        let navigation = PageNavigationController(themeProvider: theme)
        navigation.setViewControllers([shell], animated: false)
        _ = try mount(navigation)
        await wait { page.isPageVisible && !page.discovery.loading && page.discovery.canLoadMore }
        let books = page.discovery.books
        await runtime.failNextExplore()
        try button("novel.explore.more", in: page.view).sendActions(for: .touchUpInside)
        await wait { page.presentedViewController is UIAlertController && !page.discovery.loading }
        let alert = try XCTUnwrap(page.presentedViewController as? UIAlertController)
        await wait { !alert.isBeingPresented }
        page.dismissPageModal(alert)
        await wait { page.presentedViewController == nil && page.canPresentPageAlert }
        navigation.pushViewController(PageViewController(themeProvider: theme), animated: false)
        navigation.popViewController(animated: false)
        await wait { page.canPresentPageAlert }
        let more = try button("novel.explore.more", in: page.view)
        XCTAssertTrue(more.isEnabled); XCTAssertEqual(more.title(for: .normal), "重试")
        XCTAssertEqual(page.discovery.books, books)
        more.sendActions(for: .touchUpInside)
        await wait { page.presentedViewController is UIAlertController }
        let retry = try XCTUnwrap(page.presentedViewController as? UIAlertController)
        XCTAssertEqual(retry.actions.map(\.title), ["确定", "重试"])
        await wait { !retry.isBeingPresented }
        page.dismissPageModal(retry) { page.discovery.retry() }
        await wait { page.canPresentPageAlert && !page.discovery.loading && page.discovery.error == nil }
        let pages = await runtime.explorePages
        XCTAssertEqual(pages, [1, 2, 2])
        XCTAssertEqual(page.discovery.books, books)
    }
    func testDiscoveryChannelAndCurrentKindsExpandOverBooksAndCloseOnSelectionOrOutside() async throws {
        let (feature, runtime) = try await fixture(), theme = NovelTestPageTheme()
        await runtime.configureExplore(block: false); await runtime.configureGroupedExplore()
        try await feature.refreshSources()
        let page = NovelHomeViewController(feature: feature, themeProvider: theme)
        let navigation = PageNavigationController(themeProvider: theme); navigation.setViewControllers([novelShell(page, theme: theme)], animated: false)
        let window = try mount(navigation)
        await wait { !page.discovery.loading && page.discovery.channels.count == 9 }
        window.layoutIfNeeded(); page.view.layoutIfNeeded()
        let table = try XCTUnwrap(descendants(page.view).first { $0.accessibilityIdentifier == "novel.books" } as? UITableView)
        let channels = try XCTUnwrap(descendants(page.view).first { $0.accessibilityIdentifier == "novel.channel.tags" } as? FilterTagsView)
        let kinds = try XCTUnwrap(descendants(page.view).first { $0.accessibilityIdentifier == "novel.kind.tags" } as? FilterTagsView)
        let originalTop = table.frame.minY
        XCTAssertTrue(channels.hasOverflow); XCTAssertTrue(kinds.hasOverflow)
        XCTAssertEqual(page.discovery.selectedChannel?.title, "榜单")
        XCTAssertEqual(descendants(kinds).compactMap { ($0 as? UIButton)?.title(for: .normal) },
                       ["月票榜", "畅销榜", "新书榜", "推荐榜", "完结榜", "收藏榜", "热搜榜", "更新榜"])
        try button("novel.channel.tags.toggle", in: page.view).sendActions(for: .touchUpInside)
        page.view.layoutIfNeeded()
        XCTAssertTrue(channels.isExpanded); XCTAssertFalse(kinds.isExpanded)
        XCTAssertTrue(table.accessibilityElementsHidden); XCTAssertTrue(kinds.accessibilityElementsHidden)
        XCTAssertEqual(table.frame.minY, originalTop, accuracy: 0.5)
        attach(window, "novel-home-channels-expanded")
        try button("novel.tags.dismiss", in: page.view).sendActions(for: .touchUpInside)
        page.view.layoutIfNeeded()
        XCTAssertFalse(channels.isExpanded); XCTAssertFalse(table.accessibilityElementsHidden)
        try button("novel.kind.tags.toggle", in: page.view).sendActions(for: .touchUpInside)
        page.view.layoutIfNeeded()
        XCTAssertTrue(kinds.isExpanded); XCTAssertTrue(channels.accessibilityElementsHidden)
        XCTAssertEqual(table.frame.minY, originalTop, accuracy: 0.5)
        attach(window, "novel-home-kinds-expanded")
        let selectedKind = try XCTUnwrap(page.discovery.selectedChannel?.items.last)
        try button("novel.kind.tag." + selectedKind.id, in: page.view).sendActions(for: .touchUpInside)
        await wait { !page.discovery.loading && page.discovery.selectedKindID == selectedKind.id }
        XCTAssertFalse(kinds.isExpanded); XCTAssertFalse(table.accessibilityElementsHidden)
        let category = page.discovery.channels[1]
        try button("novel.channel.tags.toggle", in: page.view).sendActions(for: .touchUpInside)
        try button("novel.channel.tag." + category.id, in: page.view).sendActions(for: .touchUpInside)
        await wait { !page.discovery.loading && page.discovery.selectedChannelID == category.id }
        page.view.layoutIfNeeded()
        XCTAssertFalse(channels.isExpanded)
        XCTAssertEqual(descendants(kinds).compactMap { ($0 as? UIButton)?.title(for: .normal) }, ["东方玄幻", "异世大陆"])
        attach(window, "novel-home-current-category")
    }
    func testHomeAndSearchUseSharedMetadataCellsAcrossThemesWidthAndDynamicType() async throws {
        let (feature, runtime) = try await fixture(), theme = NovelTestPageTheme()
        await runtime.configureExplore(block: false); await runtime.configureGroupedExplore()
        await runtime.configureBookPresentation()
        let coverFormat = UIGraphicsImageRendererFormat(); coverFormat.scale = 1
        let coverBytes = UIGraphicsImageRenderer(size: CGSize(width: 148, height: 208), format: coverFormat).pngData { context in
            UIColor(red: 0.19, green: 0.25, blue: 0.35, alpha: 1).setFill()
            context.fill(CGRect(x: 0, y: 0, width: 148, height: 208))
            ("雨夜\n来信" as NSString).draw(in: CGRect(x: 24, y: 48, width: 100, height: 120),
                withAttributes: [.font: UIFont.systemFont(ofSize: 29), .foregroundColor: UIColor.white])
        }
        await runtime.configureImage(coverBytes, entered: {}); await runtime.acceptImage()
        try await feature.refreshSources()
        let page = NovelHomeViewController(feature: feature, themeProvider: theme)
        let navigation = PageNavigationController(themeProvider: theme); navigation.setViewControllers([novelShell(page, theme: theme)], animated: false)
        let host = UIViewController()
        host.addChild(navigation); host.view.addSubview(navigation.view); navigation.didMove(toParent: host)
        navigation.view.translatesAutoresizingMaskIntoConstraints = false
        let width = navigation.view.widthAnchor.constraint(equalToConstant: 320)
        NSLayoutConstraint.activate([width, navigation.view.centerXAnchor.constraint(equalTo: host.view.centerXAnchor),
            navigation.view.topAnchor.constraint(equalTo: host.view.topAnchor), navigation.view.bottomAnchor.constraint(equalTo: host.view.bottomAnchor)])
        let window = try mount(host)
        await wait { !page.discovery.loading && page.discovery.books.count == 4 }
        let table = try XCTUnwrap(descendants(page.view).first { $0.accessibilityIdentifier == "novel.books" } as? UITableView)
        var ordinaryHeight: CGFloat = 0
        for (name, size, style, category) in [
            ("compact-light", CGFloat(320), UIUserInterfaceStyle.light, UIContentSizeCategory.large),
            ("wide-dark", window.bounds.width, .dark, .large),
            ("compact-large-type", CGFloat(320), .light, .accessibilityExtraExtraExtraLarge),
        ] {
            width.constant = size; window.overrideUserInterfaceStyle = style
            navigation.traitOverrides.preferredContentSizeCategory = category
            window.layoutIfNeeded(); table.reloadData(); table.layoutIfNeeded()
            await wait { page.traitCollection.userInterfaceStyle == style }
            let cell = try XCTUnwrap(table.cellForRow(at: .init(row: 0, section: 0)) as? NovelBookCell)
            cell.layoutIfNeeded()
            let texts = Dictionary(uniqueKeysWithValues: descendants(cell).compactMap { view -> (String, UILabel)? in
                guard let label = view as? UILabel, let id = label.accessibilityIdentifier else { return nil }
                return (id, label)
            })
            XCTAssertEqual(texts["novel.book.title"]?.text, "雨夜来信")
            XCTAssertEqual(texts["novel.book.author"]?.text, "作者：林舟")
            XCTAssertEqual(texts["novel.book.intro"]?.numberOfLines, 1)
            XCTAssertEqual(texts["novel.book.wordCount"]?.text, "128.6万字")
            XCTAssertEqual(texts["novel.book.source"]?.text, "合成书源")
            XCTAssertEqual(texts["novel.book.chapter"]?.text, "第一章 雨声")
            for label in texts.values {
                XCTAssertFalse(label.font.fontDescriptor.symbolicTraits.contains(.traitBold))
                XCTAssertFalse(label.font.fontDescriptor.symbolicTraits.contains(.traitItalic))
                assertLabelFits(label, in: cell)
            }
            let cover = try XCTUnwrap(descendants(cell).first { $0.accessibilityIdentifier == "novel.book.cover" })
            XCTAssertEqual(cover.bounds.size, CGSize(width: 74, height: 104))
            XCTAssertEqual(cell.bounds.width, size, accuracy: 1)
            if category == .large { ordinaryHeight = cell.bounds.height }
            else { XCTAssertGreaterThan(cell.bounds.height, ordinaryHeight) }
            attach(window, "novel-home-" + name)
        }
        width.constant = window.bounds.width; window.overrideUserInterfaceStyle = .light
        navigation.traitOverrides.preferredContentSizeCategory = .large
        let search = NovelSearchResultsContent(feature: feature, theme: page.theme)
        let searchPage = SearchPageViewController(themeProvider: theme, query: "雨夜")
        searchPage.searchTargets = [.init(id: "novel", title: "小说", placeholder: "搜索小说")]
        searchPage.searchTargetID = "novel"
        searchPage.addChild(search); searchPage.view.addSubview(search.view); search.didMove(toParent: searchPage)
        search.view.translatesAutoresizingMaskIntoConstraints = false
        NSLayoutConstraint.activate([
            search.view.topAnchor.constraint(equalTo: searchPage.contentTopAnchor),
            search.view.leadingAnchor.constraint(equalTo: searchPage.view.safeAreaLayoutGuide.leadingAnchor),
            search.view.trailingAnchor.constraint(equalTo: searchPage.view.safeAreaLayoutGuide.trailingAnchor),
            search.view.bottomAnchor.constraint(equalTo: searchPage.view.safeAreaLayoutGuide.bottomAnchor),
        ])
        searchPage.onSubmit = { search.submit($0) }
        navigation.pushViewController(searchPage, animated: false)
        searchPage.searchField.sendActions(for: .editingDidEndOnExit)
        await wait { !search.session.loading && search.session.books.count == 4 }
        let results = try XCTUnwrap(descendants(search.view).first { $0.accessibilityIdentifier == "novel.search.books" } as? UITableView)
        window.layoutIfNeeded(); results.layoutIfNeeded()
        let cell = try XCTUnwrap(results.cellForRow(at: .init(row: 0, section: 0)) as? NovelBookCell)
        XCTAssertEqual((descendants(cell).first { $0.accessibilityIdentifier == "novel.book.wordCount" } as? UILabel)?.text, "128.6万字")
        XCTAssertEqual((descendants(cell).first { $0.accessibilityIdentifier == "novel.book.source" } as? UILabel)?.text, "合成书源")
        attach(window, "novel-search-shared-book-list")
        search.close()
    }
    func testSharedBookCellRejectsLateCoverAndReleasesEveryTrustedReference() async throws {
        let (feature, runtime) = try await fixture(), provider = NovelTestPageTheme()
        let format = UIGraphicsImageRendererFormat(); format.scale = 1
        let bytes = UIGraphicsImageRenderer(size: CGSize(width: 148, height: 208), format: format).pngData { context in
            UIColor.systemIndigo.setFill(); context.fill(CGRect(x: 0, y: 0, width: 148, height: 208))
        }
        let requested = expectation(description: "book cell requested a trusted cover")
        await runtime.configureImage(bytes, entered: { requested.fulfill() })
        let page = PageViewController(themeProvider: provider)
        let cell = NovelBookCell(style: .default, reuseIdentifier: NovelBookCell.reuseIdentifier)
        page.view.addSubview(cell)
        cell.frame = CGRect(x: 0, y: 0, width: 320, height: 128)
        _ = try mount(page)
        let book = NovelBook(installationId: "fixture", bookUrl: "https://fixture.invalid/book",
            name: "雨夜来信", coverUrl: "https://fixture.invalid/cover")
        cell.configure(book, feature: feature, theme: page.theme, loadCover: true)
        await fulfillment(of: [requested], timeout: 5)
        cell.cancelImage(); await runtime.acceptImage()
        let cover = try XCTUnwrap(descendants(cell).first { $0.accessibilityIdentifier == "novel.book.cover" } as? UIImageView)
        let deadline = ContinuousClock.now.advanced(by: .seconds(5))
        while await runtime.imageReleases < 1, ContinuousClock.now < deadline { await Task.yield() }
        let released = await runtime.imageReleases
        XCTAssertEqual(released, 1); XCTAssertNil(cover.image)
        cell.configure(book, feature: feature, theme: page.theme, loadCover: true)
        await wait { cover.image != nil }
        let allReleased = await runtime.imageReleases
        XCTAssertEqual(allReleased, 2)
        cell.prepareForReuse(); XCTAssertNil(cover.image)
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
    func testHiddenDiscoveryCancelsAndResumesOnlyTheCurrentRevision() async throws {
        let (feature, runtime) = try await fixture()
        let entered = expectation(description: "discovery entered"), cancelled = expectation(description: "discovery physically cancelled")
        await runtime.configureExplore(block: true, entered: { entered.fulfill() }, cancelled: { cancelled.fulfill() })
        try await feature.refreshSources()
        let home = NovelHomeViewController(feature: feature, themeProvider: NovelTestPageTheme())
        let navigation = PageNavigationController(themeProvider: NovelTestPageTheme()); navigation.setViewControllers([home], animated: false)
        _ = try mount(navigation)
        await fulfillment(of: [entered], timeout: 5)
        navigation.pushViewController(PageViewController(themeProvider: NovelTestPageTheme()), animated: false)
        await wait { !home.isPageVisible }
        await fulfillment(of: [cancelled], timeout: 5)
        let before = await runtime.exploreCalls
        let table = try XCTUnwrap(descendants(home.view).first { $0.accessibilityIdentifier == "novel.books" } as? UITableView)
        table.refreshControl?.beginRefreshing(); table.refreshControl?.sendActions(for: .valueChanged)
        await wait { table.refreshControl?.isRefreshing == false }
        let whileHidden = await runtime.exploreCalls; XCTAssertEqual(whileHidden, before)
        await runtime.configureExplore(block: false, revision: 2)
        try await feature.refreshSources()
        await Task.yield()
        let afterSourceChange = await runtime.exploreCalls; XCTAssertEqual(afterSourceChange, before)
        navigation.popViewController(animated: false)
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
    private var failingExplore = false
    private var groupedExplore = false
    private var presentationBooks = false
    private var revision = 1
    private var exploreEntered: (@Sendable () -> Void)?, exploreCancelled: (@Sendable () -> Void)?
    var exploreCalls: [String] = []
    var explorePages: [Int] = []
    func configureImage(_ bytes: Data, entered: @escaping @Sendable () -> Void) { imageBytes = bytes; imageEntered = entered }
    func acceptImage() { imageAccepted = true; imageContinuation?.resume(returning: imageBytes ?? Data()); imageContinuation = nil }
    func configureExplore(block: Bool, revision: Int = 1, entered: (@Sendable () -> Void)? = nil, cancelled: (@Sendable () -> Void)? = nil) {
        exploreEnabled = true; blockExplore = block; self.revision = revision; exploreEntered = entered; exploreCancelled = cancelled
    }
    func configureGroupedExplore() { groupedExplore = true }
    func failNextExplore() { failingExplore = true }
    func configureBookPresentation() { presentationBooks = true }
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
                "text": .string(String(repeating: purchases > 0 ? "已购正文。雨夜来信。\n" : "雨落在青石街道上。她推开旧书店的门，信笺仍放在窗前。\n", count: 90)),
                "src": .object(["unused": .bool(true)]), "alt": .array([.null])])]
            if imageBytes != nil { blocks.insert(.object(["type": .string("image"), "src": .string("https://fixture.invalid/image"), "text": .object(["unused": .bool(true)])]), at: 0) }
            return .object(["title": .string("第一章 雨声"), "chapter": payload["chapter"], "blocks": .array(blocks)])
        case "exploreKinds":
            exploreCalls.append("kinds:\(revision)")
            if groupedExplore {
                var kinds: [RuntimeValue] = [.object(["title": .string("榜单")])]
                for title in ["月票榜", "畅销榜", "新书榜", "推荐榜", "完结榜", "收藏榜", "热搜榜", "更新榜"] {
                    kinds.append(.object(["title": .string(title), "url": .string("https://fixture.invalid/" + title)]))
                }
                for title in ["玄幻", "奇幻", "武侠", "仙侠", "都市", "历史", "科幻", "悬疑"] {
                    kinds.append(.object(["title": .string(title)]))
                    for item in title == "玄幻" ? ["东方玄幻", "异世大陆"] : ["热门", "最新"] {
                        kinds.append(.object(["title": .string(item), "url": .string("https://fixture.invalid/" + title + "/" + item)]))
                    }
                }
                return .object(["kinds": .array(kinds)])
            }
            return .object(["kinds": .array([.object(["title": .string("全部"), "url": .string("https://fixture.invalid/explore")])])])
        case "explore":
            exploreCalls.append("explore:\(revision)")
            explorePages.append(Int(payload["page"].number ?? 0))
            if failingExplore { failingExplore = false; throw NovelError.invalidResponse }
            let entered = exploreEntered; exploreEntered = nil; entered?()
            if blockExplore {
                do { try await Task.sleep(nanoseconds: 60_000_000_000) }
                catch { let cancelled = exploreCancelled; exploreCancelled = nil; cancelled?(); throw error }
            }
            return .object(["books": .array(books + [.null, .object(["name": .string("缺少地址")])])])
        case "search": return .object(["books": .array(books)])
        case "payAction": purchases += 1; return .bool(true)
        default: throw NovelError.invalidResponse
        }
    }
    private var book: RuntimeValue {
        .object(["bookUrl": .string("https://fixture.invalid/book"), "name": .string("雨夜来信"), "author": .string("林舟"),
                 "intro": .string("一封迟来的信，抵达雨中的旧城。她推开旧书店的门，信笺仍放在窗前。"),
                 "coverUrl": .string(presentationBooks ? "https://fixture.invalid/cover" : ""),
                 "wordCount": .string("128.6万字"), "latestChapterTitle": .string("第一章 雨声")])
    }
    private var books: [RuntimeValue] {
        guard presentationBooks else { return [book] }
        return [book,
            .object(["bookUrl": .string("https://fixture.invalid/long-title"),
                "name": .string("沿着星光走到世界尽头：一位旅人的漫长归途"), "author": .string("远山与海"),
                "intro": .string("星图上的最后一条航线，通向无人知晓的故乡。"),
                "wordCount": .string("76.8万字"), "latestChapterTitle": .string("第六百二十一章 归途")]),
            .object(["bookUrl": .string("https://fixture.invalid/missing"), "name": .string("无声的港湾")]),
            .object(["bookUrl": .string("https://fixture.invalid/city"), "name": .string("旧城拾光"),
                "author": .string("青石"), "intro": .string("旧书店里的故事，从一张泛黄的照片开始。"), "wordCount": .string("32.5万字")])]
    }
}
#endif
