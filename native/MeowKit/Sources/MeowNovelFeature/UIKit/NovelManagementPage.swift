#if os(iOS)
import UIKit
import MeowUI

@MainActor
class NovelManagementPage: ManagementPageViewController {
    let feature: NovelFeature
    let manager = ManagementListView()
    private var busy = false
    init(feature: NovelFeature, themeProvider: any PageThemeProviding) { self.feature = feature; super.init(themeProvider: themeProvider) }
    override func viewDidLoad() {
        super.viewDidLoad(); view.addSubview(manager); manager.translatesAutoresizingMaskIntoConstraints = false
        NSLayoutConstraint.activate([manager.topAnchor.constraint(equalTo: contentTopAnchor), manager.leadingAnchor.constraint(equalTo: view.safeAreaLayoutGuide.leadingAnchor), manager.trailingAnchor.constraint(equalTo: view.safeAreaLayoutGuide.trailingAnchor), manager.bottomAnchor.constraint(equalTo: view.bottomAnchor)])
        observe(feature) { [weak self] in self?.render() }
    }
    override func applyTheme() { super.applyTheme(); if isViewLoaded { manager.applyTheme(theme) } }
    func render() {}
    func perform(_ operation: @escaping @MainActor () async throws -> Void) {
        guard !busy, isPageActive else { return }; busy = true
        runPageTask { [weak self] in
            guard let self else { return }; defer { busy = false; render() }
            do { try await operation() } catch is CancellationError { } catch { if !Task.isCancelled { showError(error.localizedDescription) } }
        }
    }
    func action(_ title: String, destructive: Bool = false, selected: Bool = false, body: @escaping () -> Void) -> UIAction {
        UIAction(title: title, attributes: destructive ? .destructive : [], state: selected ? .on : .off) { _ in body() }
    }
    func share(_ text: String, name: String) throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent("meowfilm-novel-export", isDirectory: true)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        let url = root.appendingPathComponent(name); try Data(text.utf8).write(to: url, options: .atomic)
        present(UIDocumentPickerViewController(forExporting: [url], asCopy: true), animated: true)
    }
    func confirmDelete(_ count: Int, operation: @escaping @MainActor () async throws -> Void) {
        guard count > 0 else { return }
        let dialog = ManagementDialog(title: "删除所选 \(count) 项？", action: "删除", themeProvider: themeProvider)
        dialog.submit = { try await operation(); return true }; present(dialog, animated: true)
    }
    func addressDialog(label: String, placeholder: String, load: @escaping (String) async throws -> Void, completed: @escaping () -> Void) {
        let dialog = ManagementDialog(title: "网络导入", themeProvider: themeProvider)
        let title = UILabel(); title.text = label; title.font = .systemFont(ofSize: 14)
        let input = ManagementDialog.input(placeholder: placeholder); input.keyboardType = .URL
        input.autocorrectionType = .no; input.autocapitalizationType = .none; input.accessibilityIdentifier = "management.import.url"
        dialog.body.addArrangedSubview(title); dialog.body.addArrangedSubview(input)
        dialog.submit = { let url = (input.text ?? "").trimmingCharacters(in: .whitespacesAndNewlines); guard !url.isEmpty else { throw NovelError.invalidRule([placeholder]) }; try await load(url); return true }
        dialog.accepted = completed; present(dialog, animated: true)
    }
    func groupsPage(groups: @escaping () -> [String], create: @escaping (String) async throws -> Void, change: @escaping (String, String?) async throws -> Void) {
        let page = NovelGroupsPage(groups: groups, canCreate: !manager.selected.isEmpty, themeProvider: themeProvider, create: create, change: change)
        navigationController?.pushViewController(page, animated: true)
    }
}

@MainActor
private final class NovelGroupsPage: NovelTablePage {
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
        rows = groups().map { group in .init(title: group) { [weak self] in
            guard let self else { return }
            let sheet = UIAlertController(title: group, message: nil, preferredStyle: .actionSheet)
            sheet.addAction(UIAlertAction(title: "编辑", style: .default) { [weak self] _ in self?.edit(group) })
            sheet.addAction(UIAlertAction(title: "删除分组", style: .destructive) { [weak self] _ in
                guard let self else { return }
                let dialog = ManagementDialog(title: "删除分组“\(group)”？", action: "删除", themeProvider: themeProvider)
                dialog.submit = { [weak self] in guard let self else { return false }; defer { render() }; try await change(group, nil); return true }
                present(dialog, animated: true)
            })
            sheet.addAction(UIAlertAction(title: "取消", style: .cancel)); sheet.popoverPresentationController?.sourceView = view; present(sheet, animated: true)
        } }
    }
    private func edit(_ old: String?) {
        let dialog = ManagementDialog(title: old == nil ? "新增分组" : "编辑分组", action: "保存", themeProvider: themeProvider)
        let input = UITextField(); input.borderStyle = .roundedRect; input.text = old; input.placeholder = "分组名称"; input.heightAnchor.constraint(equalToConstant: 44).isActive = true
        dialog.body.addArrangedSubview(input)
        dialog.submit = { [weak self] in
            guard let self else { return false }; let name = (input.text ?? "").trimmingCharacters(in: .whitespacesAndNewlines)
            guard NovelGroups.split(name).count == 1 else { throw NovelError.invalidRule(["请填写单个分组名称"]) }
            defer { render() }; try await (old == nil ? create(name) : change(old!, name)); return true
        }; present(dialog, animated: true)
    }
}
#endif
