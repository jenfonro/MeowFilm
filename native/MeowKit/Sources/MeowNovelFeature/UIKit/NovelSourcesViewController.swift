#if os(iOS)
import UIKit
import UniformTypeIdentifiers
import MeowUI
import MeowRuntime

@MainActor
final class NovelSourcesViewController: NovelTablePage, UIDocumentPickerDelegate, UISearchBarDelegate {
    private let feature: NovelFeature
    private let filter = UISearchBar()
    private var selected: Set<String> = []
    private var visible: [NovelSource] { feature.sources.filter { filter.text?.isEmpty != false || [$0.name, $0.group, $0.sourceUrl].contains { $0.localizedCaseInsensitiveContains(filter.text ?? "") } } }
    init(feature: NovelFeature, themeProvider: any PageThemeProviding) { self.feature = feature; super.init(themeProvider: themeProvider); pageTitle = "书源管理" }
    override func viewDidLoad() {
        super.viewDidLoad(); view.accessibilityIdentifier = "novel.sources"
        filter.delegate = self; filter.placeholder = "搜索书源、分组"; filter.sizeToFit(); table.tableHeaderView = filter
        pageHeader.setTrailing([PageHeaderTextButton(title: "操作") { [weak self] in self?.actions() }])
        observe(feature) { [weak self] in self?.render() }; render()
    }
    override func pageDidBecomeVisible() { refresh() }
    func searchBar(_ searchBar: UISearchBar, textDidChange searchText: String) { render() }
    private func refresh() { perform { [feature] in try await feature.refreshSources() } }
    private func perform(_ operation: @escaping @MainActor () async throws -> Void) {
        runPageTask { [weak self] in do { try await operation(); self?.render() } catch { self?.showError(error.localizedDescription) } }
    }
    private func render() {
        selected.formIntersection(Set(feature.sources.map(\.id)))
        rows = visible.map { source in
            .init(title: (selected.contains(source.id) ? "✓ " : "") + source.name,
                detail: [source.enabled ? "启用" : "停用", source.enabledExplore ? "发现" : "", source.group].filter { !$0.isEmpty }.joined(separator: " · ")) { [weak self] in self?.sourceActions(source) }
        }
    }
    private func sheet(_ title: String, actions: [UIAlertAction]) {
        let sheet = UIAlertController(title: title, message: nil, preferredStyle: .actionSheet)
        for action in actions { sheet.addAction(action) }; sheet.addAction(UIAlertAction(title: "取消", style: .cancel))
        sheet.popoverPresentationController?.sourceView = view; present(sheet, animated: true)
    }
    private func actions() {
        sheet("书源管理", actions: [
            UIAlertAction(title: "从文件导入", style: .default) { [weak self] _ in
                guard let self else { return }; let picker = UIDocumentPickerViewController(forOpeningContentTypes: [.json, .plainText], asCopy: true)
                picker.allowsMultipleSelection = true; picker.delegate = self; present(picker, animated: true)
            },
            UIAlertAction(title: "从 URL 导入", style: .default) { [weak self] _ in self?.importURL() },
            UIAlertAction(title: "粘贴导入", style: .default) { [weak self] _ in
                guard let self else { return }
                let page = NovelJSONEditor(title: "导入书源", text: "", themeProvider: themeProvider) { [weak self] text in
                    guard let self else { return }; let receipt = try await feature.sourcesClient.importSources(text: text)
                    try await feature.refreshSources()
                    if !receipt.failures.isEmpty { throw NovelError.importFailures(receipt.failures.map { "\($0.index + 1) · \($0.name) · \($0.code)" }) }
                }; navigationController?.pushViewController(page, animated: true)
            },
            UIAlertAction(title: "全选 / 取消全选", style: .default) { [weak self] _ in
                guard let self else { return }; let ids = Set(visible.map(\.id)); selected = ids.isSubset(of: selected) ? selected.subtracting(ids) : selected.union(ids); render()
            },
            UIAlertAction(title: "导出", style: .default) { [weak self] _ in self?.export() },
            UIAlertAction(title: "启用所选", style: .default) { [weak self] _ in self?.batch(enabled: true) },
            UIAlertAction(title: "停用所选", style: .default) { [weak self] _ in self?.batch(enabled: false) },
            UIAlertAction(title: "删除所选", style: .destructive) { [weak self] _ in self?.confirmRemove(self?.feature.sources.filter { self?.selected.contains($0.id) == true } ?? []) }])
    }
    private func sourceActions(_ source: NovelSource) {
        sheet(source.name, actions: [
            UIAlertAction(title: selected.contains(source.id) ? "取消选择" : "选择", style: .default) { [weak self] _ in
                guard let self else { return }; if selected.contains(source.id) { selected.remove(source.id) } else { selected.insert(source.id) }; render()
            },
            UIAlertAction(title: "编辑", style: .default) { [weak self] _ in self?.edit(source) },
            UIAlertAction(title: "名称", style: .default) { [weak self] _ in self?.metadata(source, field: "name", title: "名称", value: source.name) },
            UIAlertAction(title: "分组", style: .default) { [weak self] _ in self?.metadata(source, field: "group", title: "分组", value: source.group) },
            UIAlertAction(title: "排序", style: .default) { [weak self] _ in
                self?.textPrompt(title: "排序", value: String(format: "%g", source.order), keyboard: .numbersAndPunctuation) { [weak self] value in
                    guard let self, let order = Double(value), order.isFinite else { self?.showError("排序数值无效"); return }
                    perform { [feature] in try await feature.sourcesClient.update(source, patch: ["order": .number(order)]); try await feature.refreshSources() }
                }
            },
            UIAlertAction(title: source.enabled ? "停用" : "启用", style: .default) { [weak self] _ in
                self?.perform { [weak self] in guard let self else { return }; try await feature.sourcesClient.update(source, patch: ["enabled": .bool(!source.enabled)]); try await feature.refreshSources() }
            },
            UIAlertAction(title: source.enabledExplore ? "关闭发现" : "启用发现", style: .default) { [weak self] _ in
                self?.perform { [weak self] in guard let self else { return }; try await feature.sourcesClient.update(source, patch: ["enabledExplore": .bool(!source.enabledExplore)]); try await feature.refreshSources() }
            },
            loginAction(source),
            UIAlertAction(title: "导出", style: .default) { [weak self] _ in self?.export([source]) },
            UIAlertAction(title: "删除", style: .destructive) { [weak self] _ in self?.confirmRemove([source]) }])
    }
    private func loginAction(_ source: NovelSource) -> UIAlertAction {
        let action = UIAlertAction(title: "登录", style: .default) { [weak self] _ in
                self?.perform { [weak self] in guard let self else { return }; _ = try await feature.sourcesClient.invoke(source, action: "login") }
        }
        action.isEnabled = source.enabled && source.actions.contains("login")
        return action
    }
    private func metadata(_ source: NovelSource, field: String, title: String, value: String) {
        textPrompt(title: title, value: value) { [weak self] text in
            self?.perform { [weak self] in guard let self else { return }; try await feature.sourcesClient.update(source, patch: RuntimeObject([field: .string(text)])); try await feature.refreshSources() }
        }
    }
    private func edit(_ source: NovelSource) {
        perform { [weak self] in
            guard let self else { return }; let definition = try await feature.sourcesClient.definition(source)
            guard !Task.isCancelled else { return }
            let editor = NovelJSONEditor(title: source.name, text: definition, themeProvider: themeProvider) { [feature] text in
                try await feature.sourcesClient.save(source, definition: text); try await feature.refreshSources()
            }; navigationController?.pushViewController(editor, animated: true)
        }
    }
    private func importURL() {
        textPrompt(title: "书源 URL（多项用空格分隔）", value: "", keyboard: .URL) { [weak self] text in
            self?.perform { [weak self] in
                guard let self else { return }; var failures: [String] = []
                for url in text.split(whereSeparator: \.isWhitespace).map(String.init) {
                    do { let receipt = try await feature.sourcesClient.importSources(url: url); failures += receipt.failures.map { "\($0.index + 1) · \($0.name) · \($0.code)" } }
                    catch { failures.append(error.localizedDescription) }
                }
                try await feature.refreshSources(); if !failures.isEmpty { showError(failures.joined(separator: "\n")) }
            }
        }
    }
    func documentPicker(_ controller: UIDocumentPickerViewController, didPickDocumentsAt urls: [URL]) {
        perform { [weak self] in
            guard let self else { return }; var failures: [String] = []
            for url in urls {
                do {
                    let access = url.startAccessingSecurityScopedResource(); defer { if access { url.stopAccessingSecurityScopedResource() } }
                    let text = try String(contentsOf: url, encoding: .utf8)
                    let receipt = try await feature.sourcesClient.importSources(text: text, fileName: url.lastPathComponent)
                    failures += receipt.failures.map { "\(url.lastPathComponent) · \($0.index + 1) · \($0.name) · \($0.code)" }
                } catch { failures.append(url.lastPathComponent + " · " + error.localizedDescription) }
            }
            try await feature.refreshSources(); if !failures.isEmpty { showError(failures.joined(separator: "\n")) }
        }
    }
    private func export(_ selection: [NovelSource]? = nil) {
        let sources = selection ?? (selected.isEmpty ? feature.sources : feature.sources.filter { selected.contains($0.id) })
        perform { [weak self] in guard let self else { return }; share(text: try await feature.sourcesClient.export(sources), name: "sources.json") }
    }
    private func batch(enabled: Bool) {
        let sources = feature.sources.filter { selected.contains($0.id) }
        perform { [weak self] in
            guard let self else { return }; var failures: [String] = []
            for source in sources { do { try await feature.sourcesClient.update(source, patch: ["enabled": .bool(enabled)]) } catch { failures.append(source.name + " · " + error.localizedDescription) } }
            try await feature.refreshSources(); if !failures.isEmpty { showError(failures.joined(separator: "\n")) }
        }
    }
    private func confirmRemove(_ sources: [NovelSource]) {
        guard !sources.isEmpty else { return }
        let alert = UIAlertController(title: "删除书源", message: sources.map(\.name).joined(separator: "\n"), preferredStyle: .alert)
        alert.addAction(UIAlertAction(title: "取消", style: .cancel))
        alert.addAction(UIAlertAction(title: "删除", style: .destructive) { [weak self] _ in
            self?.perform { [weak self] in
                guard let self else { return }; var failures: [String] = []
                for source in sources { do { try await feature.sourcesClient.remove(source) } catch { failures.append(source.name + " · " + error.localizedDescription) } }
                try await feature.refreshSources(); if !failures.isEmpty { showError(failures.joined(separator: "\n")) }
            }
        }); present(alert, animated: true)
    }
}
#endif
