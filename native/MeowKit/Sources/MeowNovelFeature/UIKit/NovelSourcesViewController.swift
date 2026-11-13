#if os(iOS)
import UIKit
import UniformTypeIdentifiers
import MeowUI
import MeowRuntime

@MainActor
final class NovelSourcesViewController: NovelManagementPage, UIDocumentPickerDelegate {
    private var query = "", sort = 0, reverse = false, group: String?, family: String?, status = 0
    private let sortButton = ManagementListView.icon("management.sort", label: "排序")
    private let groupButton = ManagementListView.icon("management.group", label: "分组与协议")
    private let moreButton = ManagementListView.icon("management.more", label: "更多")
    private var groups: [String] { Array(Set(feature.sources.flatMap { NovelGroups.split($0.group) })).sorted { $0.localizedStandardCompare($1) == .orderedAscending } }
    private var visible: [NovelSource] {
        let query = query.trimmingCharacters(in: .whitespacesAndNewlines)
        var result = feature.sources.filter { source in
            (family == nil || family == source.protocol) && (group == nil || NovelGroups.split(source.group).contains(group!)) &&
            (status == 0 || status == 1 && source.enabled || status == 2 && !source.enabled || status == 3 && source.enabledExplore || status == 4 && !source.enabledExplore) &&
            (query.isEmpty || [source.name, source.sourceUrl, source.group].contains { $0.localizedCaseInsensitiveContains(query) })
        }
        result.sort { a, b in
            switch sort {
            case 1: if (a.weight ?? 0) != (b.weight ?? 0) { return (a.weight ?? 0) > (b.weight ?? 0) }
            case 3: if a.sourceUrl != b.sourceUrl { return a.sourceUrl.localizedStandardCompare(b.sourceUrl) == .orderedAscending }
            case 4: if (a.lastUpdateTime ?? 0) != (b.lastUpdateTime ?? 0) { return (a.lastUpdateTime ?? 0) > (b.lastUpdateTime ?? 0) }
            case 5: if (a.respondTime ?? 180000) != (b.respondTime ?? 180000) { return (a.respondTime ?? 180000) < (b.respondTime ?? 180000) }
            case 6: if a.enabled != b.enabled { return a.enabled }
            default: break
            }
            if sort == 0, a.order != b.order { return a.order < b.order }
            let name = a.name.localizedStandardCompare(b.name)
            return name == .orderedSame ? a.id < b.id : name == .orderedAscending
        }
        return reverse ? result.reversed() : result
    }
    override init(feature: NovelFeature, themeProvider: any PageThemeProviding) { super.init(feature: feature, themeProvider: themeProvider); pageTitle = "书源管理" }
    override func viewDidLoad() {
        super.viewDidLoad(); view.accessibilityIdentifier = "novel.sources"
        manager.search.placeholder = "搜索书源"; manager.onSearch = { [weak self] in self?.query = $0; self?.render() }
        for button in [sortButton, groupButton, moreButton] { button.showsMenuAsPrimaryAction = true; manager.tools.addArrangedSubview(button) }
        manager.onSelection = { [weak self] in self?.menus() }
        manager.onDelete = { [weak self] in guard let self else { return }; remove(feature.sources.filter { self.manager.selected.contains($0.id) }) }
        render()
    }
    override func pageDidBecomeVisible() { perform { [feature] in try await feature.refreshSources() } }
    override func render() {
        manager.setRows(visible.map { source in .init(id: source.id, title: source.name,
            detail: ([NovelSourceProtocol(rawValue: source.protocol)?.title ?? source.protocol, source.group, source.errorCode ?? ""].filter { !$0.isEmpty }).joined(separator: " · "), enabled: source.enabled, failed: source.errorCode != nil,
            edit: { [weak self] in self?.edit(source) }, toggle: { [weak self] value in self?.update(source, ["enabled": .bool(value)]) }, menu: rowMenu(source)) },
            total: feature.sources.count, validIDs: Set(feature.sources.map(\.id)), emptyMessage: feature.sources.isEmpty ? "暂无书源，请先导入" : "没有匹配的书源")
        menus()
    }
    private func menus() {
        let names = ["手动", "智能", "名称", "地址", "更新时间", "响应时间", "启用"]
        sortButton.menu = UIMenu(children: [action("反序", selected: reverse) { [weak self] in self?.reverse.toggle(); self?.render() }] + names.enumerated().map { index, title in action(title, selected: sort == index) { [weak self] in self?.sort = index; self?.render() } })
        let protocols = UIMenu(title: "书源协议", children: [action("全部协议", selected: family == nil) { [weak self] in self?.family = nil; self?.render() }] + NovelSourceProtocol.allCases.map { value in action(value.title, selected: family == value.rawValue) { [weak self] in self?.family = value.rawValue; self?.render() } })
        groupButton.menu = UIMenu(children: [action("分组管理") { [weak self] in self?.manageGroups() }, protocols,
            action("全部分组", selected: group == nil && status == 0) { [weak self] in self?.group = nil; self?.status = 0; self?.render() }] + ["已启用", "已禁用", "已启用发现", "已禁用发现"].enumerated().map { index, title in action(title, selected: status == index + 1) { [weak self] in self?.status = index + 1; self?.group = nil; self?.render() } } + groups.map { name in action(name, selected: group == name) { [weak self] in self?.group = name; self?.status = 0; self?.render() } })
        moreButton.menu = UIMenu(children: [UIMenu(title: "新建书源", children: NovelSourceProtocol.allCases.map { value in action(value.title) { [weak self] in self?.newSource(value) } }), action("本地导入") { [weak self] in self?.fileImport() }, action("网络导入") { [weak self] in self?.networkImport() }, action("粘贴导入") { [weak self] in guard let self else { return }; perform { [weak self] in guard let self else { return }; showPreview(try await feature.sourcesClient.previewImport(text: UIPasteboard.general.string ?? "")) } }, action("导出书源") { [weak self] in self?.export(self?.feature.sources ?? []) }])
        manager.footerMore.menu = UIMenu(children: [action("导出选中书源") { [weak self] in guard let self else { return }; export(feature.sources.filter { self.manager.selected.contains($0.id) }) }, action("启用所选") { [weak self] in self?.batchEnabled(true) }, action("禁用所选") { [weak self] in self?.batchEnabled(false) }])
    }
    private func rowMenu(_ source: NovelSource) -> UIMenu {
        var actions = [action("置顶") { [weak self] in self?.move(source, top: true) }, action("置底") { [weak self] in self?.move(source, top: false) }]
        if source.hasExplore == true { actions.append(action(source.enabledExplore ? "禁用发现" : "启用发现") { [weak self] in self?.update(source, ["enabledExplore": .bool(!source.enabledExplore)]) }) }
        if source.enabled && source.actions.contains("login") { actions.append(action("登录") { [weak self] in self?.perform { [weak self] in _ = try await self?.feature.sourcesClient.invoke(source, action: "login") } }) }
        actions += [action("导出") { [weak self] in self?.export([source]) }, action("删除", destructive: true) { [weak self] in self?.remove([source]) }]
        return UIMenu(children: actions)
    }
    private func mutate(_ operation: @escaping @MainActor () async throws -> Void) async throws {
        do { try await operation() }
        catch { try? await feature.refreshSources(); throw error }
        try await feature.refreshSources()
    }
    private func update(_ source: NovelSource, _ patch: RuntimeObject) { perform { [weak self, feature] in try await self?.mutate { try await feature.sourcesClient.update(source, patch: patch) } } }
    private func move(_ source: NovelSource, top: Bool) {
        let ordered = feature.sources.sorted { $0.order < $1.order }.filter { $0.id != source.id }
        let next = top ? [source] + ordered : ordered + [source]
        guard let edge = top ? feature.sources.map(\.order).min() : feature.sources.map(\.order).max() else { return }
        perform { [weak self, feature] in
            try await self?.mutate {
                // JSON safe integers are the runtime's ordering contract.
                if edge > -9007199254740991 && edge < 9007199254740991 {
                    try await feature.sourcesClient.update(source, patch: ["order": .number(Double(edge + (top ? -1 : 1)))])
                } else {
                    for (index, item) in next.enumerated() { try await feature.sourcesClient.update(item, patch: ["order": .number(Double(index))]) }
                }
            }
        }
    }
    private func batchEnabled(_ enabled: Bool) {
        let selected = feature.sources.filter { self.manager.selected.contains($0.id) }
        perform { [weak self, feature] in try await self?.mutate { for item in selected { try await feature.sourcesClient.update(item, patch: ["enabled": .bool(enabled)]) } } }
    }
    private func remove(_ sources: [NovelSource]) {
        confirmDelete(sources.count) { [feature, weak self] in
            defer { self?.render() }
            do { for source in sources { try await feature.sourcesClient.remove(source) } }
            catch { try? await feature.refreshSources(); throw error }
            try await feature.refreshSources()
        }
    }
    private func export(_ sources: [NovelSource]) { perform { [weak self] in guard let self else { return }; try share(try await feature.sourcesClient.export(sources), name: "book-sources.json") } }
    private func newSource(_ protocol: NovelSourceProtocol) { do { let page = try NovelSourceEditor(source: nil, protocol: `protocol`, definition: nil, feature: feature, themeProvider: themeProvider); navigationController?.pushViewController(page, animated: true) } catch { showError(error.localizedDescription) } }
    private func edit(_ source: NovelSource) {
        perform { [weak self] in
            guard let self, let family = NovelSourceProtocol(rawValue: source.protocol) else { return }
            let current = try await feature.sourcesClient.definition(source); try Task.checkCancellation()
            let page = try NovelSourceEditor(source: current.source, protocol: family, definition: current.definition, feature: feature, themeProvider: themeProvider)
            navigationController?.pushViewController(page, animated: true)
        }
    }
    private func fileImport() { let picker = UIDocumentPickerViewController(forOpeningContentTypes: [.json, .plainText, .html], asCopy: true); picker.delegate = self; present(picker, animated: true) }
    private func networkImport() {
        var preview: NovelSourceImportPreview?
        addressDialog(label: "书源地址", placeholder: "请输入网络书源地址", load: { [feature] url in preview = try await feature.sourcesClient.previewImport(url: url) }, completed: { [weak self] in if let preview { self?.showPreview(preview) } })
    }
    func documentPicker(_ controller: UIDocumentPickerViewController, didPickDocumentsAt urls: [URL]) {
        guard let url = urls.first else { return }
        dismissPageModal(controller) { [weak self] in self?.perform { [weak self] in
            guard let self else { return }; let access = url.startAccessingSecurityScopedResource(); defer { if access { url.stopAccessingSecurityScopedResource() } }
            let preview = try await feature.sourcesClient.previewImport(text: String(contentsOf: url, encoding: .utf8), fileName: url.lastPathComponent)
            showPreview(preview)
        } }
    }
    private func showPreview(_ preview: NovelSourceImportPreview) {
        let dialog = ManagementDialog(title: "导入书源", action: "确定导入", themeProvider: themeProvider)
        let selection = ManagementImportView(items: preview.items.map { .init(title: $0.name, detail: [NovelSourceProtocol(rawValue: $0.protocol)?.title ?? $0.protocol, $0.group].filter { !$0.isEmpty }.joined(separator: " · "), status: $0.exists ? "已存在" : "新增") })
        dialog.body.addArrangedSubview(selection)
        if !preview.failures.isEmpty { let label = UILabel(); label.text = preview.failures.map { "条目 \($0.index + 1) · \($0.code)" }.joined(separator: "\n"); label.numberOfLines = 0; label.font = UIFontMetrics.default.scaledFont(for: .systemFont(ofSize: 12)); label.adjustsFontForContentSizeCategory = true; label.textColor = UIColor { UIColor(SettingsTheme(dark: $0.userInterfaceStyle == .dark).danger) }; dialog.body.addArrangedSubview(label) }
        var failures: [String] = []
        dialog.submit = { [feature] in
            guard !selection.selected.isEmpty else { throw NovelError.invalidRule(["请选择要导入的书源"]) }
            let definitions = try preview.items.enumerated().filter { selection.selected.contains($0.offset) }.map { try RuntimeJSON.decode(Data($0.element.definition.utf8)) }
            let receipt = try await feature.sourcesClient.importSources(text: String(decoding: RuntimeJSON.encode(.array(definitions)), as: UTF8.self))
            try await feature.refreshSources(); failures = receipt.failures.map { "\($0.name) · \($0.code)" }; return true
        }
        dialog.accepted = { [weak self] in if !failures.isEmpty { self?.showError(failures.joined(separator: "\n")) } }
        present(dialog, animated: true)
    }
    private func manageGroups() {
        let selected = manager.selected
        groupsPage(groups: { [self] in groups }, create: { [self, feature] name in
            try await mutate { for source in feature.sources.filter({ selected.contains($0.id) }) { try await feature.sourcesClient.update(source, patch: ["group": .string(NovelGroups.replacing(source.group, old: "", new: name))]) } }
        }, change: { [self, feature] old, next in
            try await mutate { for source in feature.sources.filter({ NovelGroups.split($0.group).contains(old) }) { try await feature.sourcesClient.update(source, patch: ["group": .string(NovelGroups.replacing(source.group, old: old, new: next))]) } }
        })
    }
}
#endif
