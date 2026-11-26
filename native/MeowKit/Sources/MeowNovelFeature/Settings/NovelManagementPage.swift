#if os(iOS)
import UIKit
import MeowStorage
import MeowUI

@MainActor
class NovelManagementPage: ManagementPageViewController {
    let feature: NovelFeature
    init(feature: NovelFeature, themeProvider: any PageThemeProviding) { self.feature = feature; super.init(themeProvider: themeProvider) }
    override func viewDidLoad() {
        super.viewDidLoad()
        observe(feature, whenVisible: true) { [weak self] in self?.render() }
    }
    func render() {}
    func perform(_ operation: @escaping @MainActor () async throws -> Void) {
        guard !hasActivePageTask(key: "action"), isPageActive else { return }
        runPageTask(key: "action", onFinally: { [weak self] in
            self?.render()
        }) {
            try await operation()
        }
    }
    func action(_ title: String, destructive: Bool = false, selected: Bool = false, body: @escaping () -> Void) -> UIAction {
        UIAction(title: title, attributes: destructive ? .destructive : [], state: selected ? .on : .off) { _ in body() }
    }
    func confirmDelete(_ count: Int, operation: @escaping @MainActor () async throws -> Void) {
        guard count > 0 else { return }
        let dialog = ManagementDialog(title: "删除所选 \(count) 项？", action: "删除", themeProvider: themeProvider)
        dialog.submit = { try await operation(); return true }; presentPageModal(dialog)
    }
    func addressDialog(label: String, placeholder: String, load: @escaping (String) async throws -> Void, completed: @escaping () -> Void) {
        let dialog = ManagementDialog(title: "网络导入", themeProvider: themeProvider)
        let input = ManagementDialog.input(placeholder: placeholder); input.keyboardType = .URL
        input.autocorrectionType = .no; input.autocapitalizationType = .none; input.accessibilityIdentifier = "management.import.url"
        dialog.addInput(input, title: label)
        dialog.submit = { let url = (input.text ?? "").trimmingCharacters(in: .whitespacesAndNewlines); guard !url.isEmpty else { throw NovelError.invalidRule(["请输入\(placeholder)"]) }; try await load(url); return true }
        dialog.accepted = completed; presentPageModal(dialog)
    }
    func groupsPage(groups: @escaping () -> [String], create: @escaping (String) async throws -> Void, change: @escaping (String, String?) async throws -> Void) {
        let page = NovelGroupsPage(groups: groups, canCreate: !manager.selected.isEmpty, themeProvider: themeProvider, create: create, change: change)
        navigationController?.pushViewController(page, animated: true)
    }
}

@MainActor
private final class NovelGroupsPage: SettingsListViewController {
    private let groups: () -> [String]
    private let canCreate: Bool
    private let create: (String) async throws -> Void
    private let change: (String, String?) async throws -> Void
    init(groups: @escaping () -> [String], canCreate: Bool, themeProvider: any PageThemeProviding, create: @escaping (String) async throws -> Void, change: @escaping (String, String?) async throws -> Void) {
        self.groups = groups; self.canCreate = canCreate; self.create = create; self.change = change
        super.init(themeProvider: themeProvider); pageTitle = "分组管理"
    }
    override func viewDidLoad() {
        super.viewDidLoad()
        let add = PageHeaderTextButton(title: "新增") { [weak self] in self?.edit(nil) }; add.isEnabled = canCreate; add.accessibilityLabel = "为所选项目新增分组"
        pageHeader.setTrailing([add]); render()
    }
    private func render() {
        let rows = groups().map { group in
            SettingsListRow(id: "novel.group.\(group)", title: group, accessory: .disclosure) { [weak self] in
                guard let self else { return }
                presentPageActionSheet(
                    title: group,
                    sourceView: view,
                    actions: [
                        PageAlertAction(title: "编辑", style: .default) { [weak self] in self?.edit(group) },
                        PageAlertAction(title: "删除分组", style: .destructive) { [weak self] in
                            guard let self else { return }
                            let dialog = ManagementDialog(title: "删除分组“\(group)”？", action: "删除", themeProvider: themeProvider)
                            dialog.submit = { [weak self] in guard let self else { return false }; defer { render() }; try await change(group, nil); return true }
                            presentPageModal(dialog)
                        },
                    ]
                )
            }
        }
        applySections([SettingsListSection(id: "main", rows: rows)])
    }
    private func edit(_ old: String?) {
        let dialog = ManagementDialog(title: old == nil ? "新增分组" : "编辑分组", action: "保存", themeProvider: themeProvider)
        let input = ManagementDialog.input(placeholder: "分组名称"); input.text = old
        dialog.addInput(input, title: "分组名称")
        dialog.submit = { [weak self] in
            guard let self else { return false }; let name = (input.text ?? "").trimmingCharacters(in: .whitespacesAndNewlines)
            guard NovelGroups.split(name).count == 1 else { throw NovelError.invalidRule(["请填写单个分组名称"]) }
            defer { render() }; try await (old == nil ? create(name) : change(old!, name)); return true
        }; presentPageModal(dialog)
    }
}

@MainActor
extension PageViewController {
    func presentNovelExport(_ text: String, name: String) throws {
        guard canPresentPageAlert else { return }
        let root = AppTempDirectory.rootURL().appendingPathComponent("novel-export", isDirectory: true)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        let safeName = AppTempDirectory.sanitizedFilename(name, fallback: "novel-export.json")
        let url = root.appendingPathComponent(safeName, isDirectory: false)
        try Data(text.utf8).write(to: url, options: .atomic)
        presentPageModal(UIDocumentPickerViewController(forExporting: [url], asCopy: true))
    }
}
#endif
