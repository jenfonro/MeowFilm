#if os(iOS)
import UIKit
import UniformTypeIdentifiers
import CoreImage
import MeowUI

@MainActor
final class NovelRulesViewController: NovelManagementPage, UIDocumentPickerDelegate {
    private var query = "", group: String?, noGroup = false, qrImport = false
    private let groupButton = ManagementListView.icon("management.group", label: "分组")
    private let moreButton = ManagementListView.icon("management.more", label: "更多")
    private var groups: [String] { Array(Set(feature.local.rules.flatMap { NovelGroups.split($0.group) })).sorted() }
    private var visible: [NovelRule] {
        let keyword = query.trimmingCharacters(in: .whitespacesAndNewlines)
        return feature.local.rules.enumerated().filter { _, rule in
            (!noGroup || rule.group.isEmpty) && (group == nil || NovelGroups.split(rule.group).contains(group!)) &&
            (keyword.isEmpty || [rule.name, rule.group, rule.pattern, rule.replacement, rule.scope].contains { $0.localizedCaseInsensitiveContains(keyword) })
        }.sorted { $0.element.order == $1.element.order ? $0.element.name.localizedStandardCompare($1.element.name) == .orderedAscending : $0.element.order < $1.element.order }.map(\.element)
    }
    override init(feature: NovelFeature, themeProvider: any PageThemeProviding) { super.init(feature: feature, themeProvider: themeProvider); pageTitle = "替换净化" }
    override func viewDidLoad() {
        super.viewDidLoad(); view.accessibilityIdentifier = "novel.rules"; manager.search.placeholder = "搜索替换规则"
        manager.onSearch = { [weak self] in self?.query = $0; self?.render() }
        for button in [groupButton, moreButton] { button.showsMenuAsPrimaryAction = true; manager.tools.addArrangedSubview(button) }
        manager.onSelection = { [weak self] in self?.menus() }
        manager.onDelete = { [weak self] in guard let self else { return }; remove(feature.local.rules.filter { self.manager.selected.contains($0.id) }) }
        render()
    }
    override func render() {
        manager.setRows(visible.map { rule in .init(id: rule.id, title: rule.name.isEmpty ? rule.pattern : rule.name, detail: rule.group,
            enabled: rule.isEnabled, edit: { [weak self] in self?.edit(rule) }, toggle: { [weak self] enabled in
                guard let self else { return }; var next = rule; next.isEnabled = enabled; save(next)
            }, menu: UIMenu(children: [action("置顶") { [weak self] in self?.move(rule, top: true) }, action("置底") { [weak self] in self?.move(rule, top: false) }, action("删除", destructive: true) { [weak self] in self?.remove([rule]) }])) }, total: feature.local.rules.count, validIDs: Set(feature.local.rules.map(\.id)), emptyMessage: feature.local.rules.isEmpty ? "暂无替换规则，请先新建" : "没有匹配的替换规则")
        menus()
    }
    private func menus() {
        groupButton.menu = UIMenu(children: [action("分组管理") { [weak self] in self?.manageGroups() }, action("全部分组", selected: group == nil && !noGroup) { [weak self] in self?.group = nil; self?.noGroup = false; self?.render() }, action("无分组", selected: noGroup) { [weak self] in self?.group = nil; self?.noGroup = true; self?.render() }] + groups.map { name in action(name, selected: group == name) { [weak self] in self?.group = name; self?.noGroup = false; self?.render() } })
        moreButton.menu = UIMenu(children: [action("新建替换") { [weak self] in var rule = NovelRule(); rule.isRegex = true; self?.edit(rule) }, action("本地导入") { [weak self] in self?.fileImport(qr: false) }, action("网络导入") { [weak self] in self?.networkImport() }, action("二维码导入") { [weak self] in self?.fileImport(qr: true) }, action("导出替换规则") { [weak self] in guard let self else { return }; perform { [weak self] in guard let self else { return }; try share(NovelRuleImport.encode(feature.local.rules), name: "replace-rules.json") } }])
        manager.footerMore.menu = UIMenu(children: [action("导出选中规则") { [weak self] in guard let self else { return }; perform { [weak self] in guard let self else { return }; try share(NovelRuleImport.encode(feature.local.rules.filter { self.manager.selected.contains($0.id) }), name: "replace-rules.json") } }])
    }
    private func save(_ rule: NovelRule) {
        perform { [feature] in var next = feature.local.rules; if let index = next.firstIndex(where: { $0.id == rule.id }) { next[index] = rule } else { next.append(rule) }; try await feature.saveRules(next) }
    }
    private func edit(_ rule: NovelRule) { navigationController?.pushViewController(NovelRuleEditor(rule: rule, feature: feature, themeProvider: themeProvider), animated: true) }
    private func move(_ rule: NovelRule, top: Bool) {
        var rules = feature.local.rules.enumerated().sorted { $0.element.order == $1.element.order ? $0.offset < $1.offset : $0.element.order < $1.element.order }.map(\.element).filter { $0.id != rule.id }
        if top { rules.insert(rule, at: 0) } else { rules.append(rule) }
        for index in rules.indices { rules[index].order = index }
        let next = rules; perform { [feature] in try await feature.saveRules(next) }
    }
    private func remove(_ rules: [NovelRule]) {
        let ids = Set(rules.map(\.id))
        confirmDelete(ids.count) { [feature] in try await feature.saveRules(feature.local.rules.filter { !ids.contains($0.id) }) }
    }
    private func fileImport(qr: Bool) { qrImport = qr; let picker = UIDocumentPickerViewController(forOpeningContentTypes: qr ? [.image] : [.json, .plainText], asCopy: true); picker.delegate = self; present(picker, animated: true) }
    func documentPicker(_ controller: UIDocumentPickerViewController, didPickDocumentsAt urls: [URL]) {
        guard let url = urls.first else { return }; let qr = qrImport
        dismissPageModal(controller) { [weak self] in self?.perform { [weak self] in
            guard let self else { return }; let access = url.startAccessingSecurityScopedResource(); defer { if access { url.stopAccessingSecurityScopedResource() } }
            let data = try Data(contentsOf: url), text: String
            if qr {
                text = try Self.readRuleQr(data)
            } else { guard let value = String(data: data, encoding: .utf8) else { throw NovelError.invalidRule(["替换规则文件不是 UTF-8"]) }; text = value }
            try await preview(text)
        } }
    }
    static func readRuleQr(_ data: Data) throws -> String {
        guard let image = CIImage(data: data), let detector = CIDetector(ofType: CIDetectorTypeQRCode, context: nil, options: [CIDetectorAccuracy: CIDetectorAccuracyHigh]), let value = (detector.features(in: image).first as? CIQRCodeFeature)?.messageString else { throw NovelError.invalidRule(["未识别到二维码内容"]) }
        return value
    }
    private func networkImport() {
        var preview: (rules: [NovelRule], failures: [String]) = ([], [])
        addressDialog(label: "替换规则地址", placeholder: "请输入网络替换规则地址", load: { [self] text in
            guard let url = URL(string: text), ["http", "https"].contains(url.scheme?.lowercased() ?? ""), url.user == nil, url.password == nil else { throw NovelError.invalidRule(["网络地址无效"]) }
            let config = URLSessionConfiguration.ephemeral; config.httpCookieStorage = nil
            let session = URLSession(configuration: config); defer { session.finishTasksAndInvalidate() }
            let (data, response) = try await session.data(from: url)
            guard let response = response as? HTTPURLResponse, (200..<300).contains(response.statusCode), let text = String(data: data, encoding: .utf8) else { throw NovelError.invalidResponse }
            preview = try await self.parse(text)
        }, completed: { [weak self] in self?.showPreview(preview.rules, failures: preview.failures) })
    }
    private func preview(_ text: String) async throws {
        let preview = try await parse(text)
        try Task.checkCancellation(); showPreview(preview.rules, failures: preview.failures)
    }
    private func parse(_ text: String) async throws -> (rules: [NovelRule], failures: [String]) {
        var rules: [NovelRule] = [], failures: [String] = []
        for (index, entry) in try NovelRuleImport.entries(text).enumerated() {
            do { let rule = try NovelRuleImport.decode(entry); try await feature.rules.validate(rule); rules.append(rule) }
            catch is CancellationError { throw CancellationError() }
            catch { try Task.checkCancellation(); failures.append("规则 \(index + 1) 无效") }
        }
        return (rules, failures)
    }
    private func showPreview(_ rules: [NovelRule], failures: [String] = []) {
        let current = Dictionary(feature.local.rules.map { ($0.id, $0) }, uniquingKeysWith: { _, last in last })
        let statuses = rules.map { NovelRuleImport.status($0, existing: current[$0.id]) }
        let selection = ManagementImportView(items: rules.enumerated().map { index, rule in .init(title: rule.name.isEmpty ? rule.pattern : rule.name, detail: rule.group, status: statuses[index]) }, selected: Set(rules.indices.filter { statuses[$0] != "已有" }))
        let dialog = ManagementDialog(title: "导入替换规则", action: "确定导入", themeProvider: themeProvider); dialog.body.addArrangedSubview(selection)
        if !failures.isEmpty { let label = UILabel(); label.text = failures.joined(separator: "\n"); label.numberOfLines = 0; label.textColor = UIColor { UIColor(SettingsTheme(dark: $0.userInterfaceStyle == .dark).danger) }; label.font = UIFontMetrics.default.scaledFont(for: .systemFont(ofSize: 12)); label.adjustsFontForContentSizeCategory = true; dialog.body.addArrangedSubview(label) }
        dialog.submit = { [feature] in
            guard !selection.selected.isEmpty else { throw NovelError.invalidRule(["请选择要导入的替换规则"]) }
            var next = feature.local.rules
            for (index, rule) in rules.enumerated() where selection.selected.contains(index) { if let at = next.firstIndex(where: { $0.id == rule.id }) { next[at] = rule } else { next.append(rule) } }
            try await feature.saveRules(next); return true
        }; present(dialog, animated: true)
    }
    private func manageGroups() {
        let ids = manager.selected
        groupsPage(groups: { [self] in groups }, create: { [feature] name in
            var next = feature.local.rules; for index in next.indices where ids.contains(next[index].id) { next[index].group = NovelGroups.replacing(next[index].group, old: "", new: name) }
            try await feature.saveRules(next)
        }, change: { [feature] old, name in
            var next = feature.local.rules; for index in next.indices where NovelGroups.split(next[index].group).contains(old) { next[index].group = NovelGroups.replacing(next[index].group, old: old, new: name) }
            try await feature.saveRules(next)
        })
    }
}

@MainActor
final class NovelRuleEditor: ManagementPageViewController {
    private var rule: NovelRule
    private let feature: NovelFeature
    private let form = ManagementFormView()
    private var timeout: String, saving = false
    init(rule: NovelRule, feature: NovelFeature, themeProvider: any PageThemeProviding) { self.rule = rule; self.feature = feature; timeout = String(rule.timeoutMillisecond); super.init(themeProvider: themeProvider); pageTitle = "替换规则编辑" }
    override func viewDidLoad() {
        super.viewDidLoad(); view.accessibilityIdentifier = "novel.rule.editor"; form.boxedInputs = true
        let save = PageHeaderButton(image: ManagementIcon.save, accessibilityLabel: "保存") { [weak self] in self?.save() }; save.accessibilityIdentifier = "novel.rule.save"
        setManagementActions([save, PageHeaderButton(image: ManagementIcon.more, accessibilityLabel: "更多", menu: UIMenu(children: [UIAction(title: "复制规则") { [weak self] _ in self?.copy() }, UIAction(title: "粘贴规则") { [weak self] _ in self?.paste() }]))])
        view.addSubview(form); form.translatesAutoresizingMaskIntoConstraints = false
        NSLayoutConstraint.activate([form.topAnchor.constraint(equalTo: contentTopAnchor), form.leadingAnchor.constraint(equalTo: view.safeAreaLayoutGuide.leadingAnchor, constant: 12), form.trailingAnchor.constraint(equalTo: view.safeAreaLayoutGuide.trailingAnchor, constant: -12), form.bottomAnchor.constraint(equalTo: view.keyboardLayoutGuide.topAnchor)])
        render()
    }
    override func applyTheme() { super.applyTheme(); if isViewLoaded { form.applyTheme(theme) } }
    private func text(_ label: String, _ key: WritableKeyPath<NovelRule, String>, rows: Int = 1, id: String) { form.text(label, value: rule[keyPath: key], rows: rows, id: "novel.rule.field." + id) { [weak self] in self?.rule[keyPath: key] = $0 } }
    private func render() {
        form.clear()
        text("替换规则名称（name）", \.name, id: "name"); text("分组（group）", \.group, id: "group")
        text("替换规则（pattern）", \.pattern, rows: 5, id: "pattern")
        form.addSection(ManagementFormView.check("使用正则表达式", value: rule.isRegex, round: true) { [weak self] in self?.rule.isRegex = $0 })
        text("替换为（replacement）", \.replacement, rows: 4, id: "replacement")
        let checks = UIStackView(arrangedSubviews: [ManagementFormView.check("作用于标题", value: rule.scopeTitle, round: true) { [weak self] in self?.rule.scopeTitle = $0 }, ManagementFormView.check("作用于正文", value: rule.scopeContent, round: true) { [weak self] in self?.rule.scopeContent = $0 }, UIView()]); checks.spacing = 14; form.addSection(checks)
        text("替换范围，选填书名或者书源 URL（scope）", \.scope, id: "scope"); text("排除范围，选填书名或者书源 URL（excludeScope）", \.excludeScope, id: "excludeScope")
        form.text("超时毫秒数（timeoutMillisecond）", value: timeout, id: "novel.rule.field.timeoutMillisecond") { [weak self] in self?.timeout = $0 }
        form.applyTheme(theme)
    }
    private func payload() throws -> NovelRule {
        guard let timeout = Int(timeout), timeout > 0 else { throw NovelError.invalidRule(["超时毫秒数无效"]) }
        var next = rule; next.timeoutMillisecond = timeout
        next.name = next.name.trimmingCharacters(in: .whitespacesAndNewlines); next.group = next.group.trimmingCharacters(in: .whitespacesAndNewlines)
        next.scope = next.scope.trimmingCharacters(in: .whitespacesAndNewlines); next.excludeScope = next.excludeScope.trimmingCharacters(in: .whitespacesAndNewlines)
        return next
    }
    private func save() {
        guard !saving else { return }; view.endEditing(true)
        do {
            let rule = try payload(); saving = true
            runPageTask { [weak self] in
                guard let self else { return }; defer { saving = false }
                do { var next = feature.local.rules; if let index = next.firstIndex(where: { $0.id == rule.id }) { next[index] = rule } else { next.append(rule) }; try await feature.saveRules(next); navigationController?.popViewController(animated: true) }
                catch is CancellationError { } catch { if !Task.isCancelled { showError(error.localizedDescription) } }
            }
        } catch { showError(error.localizedDescription) }
    }
    private func copy() { do { UIPasteboard.general.string = try String(decoding: JSONEncoder().encode(payload()), as: UTF8.self) } catch { showError(error.localizedDescription) } }
    private func paste() {
        do {
            guard var next = try NovelRuleImport.parse(UIPasteboard.general.string ?? "").first else { throw NovelError.invalidResponse }
            next.id = rule.id; next.order = rule.order; next.isEnabled = rule.isEnabled; rule = next; timeout = String(next.timeoutMillisecond); render()
        }
        catch { showError("剪贴板内容格式不对") }
    }
}
#endif
