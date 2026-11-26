#if os(iOS)
import UIKit
import Combine
import UniformTypeIdentifiers
import MeowUI

/// Pure UIKit music source management directly hosting `ManagementListView` (eliminates 4-layer SwiftUI nesting).
@MainActor
final class MusicSourceManagementViewController: ManagementPageViewController, UIDocumentPickerDelegate {
    private let model: MusicViewModel
    private let appearance: MusicAppearancePreferencesStore
    let state: MusicSourceManagementState
    private let listView = ManagementListView()
    private let importingIndicator = UIActivityIndicatorView(style: .medium)
    private var searchQuery = ""
    private var allRows: [ManagementListView.Row] = []
    private var updatingID: String?
    private weak var detail: MusicScriptSourceDetailViewController?

    init(model: MusicViewModel, appearance: MusicAppearancePreferencesStore) {
        self.model = model
        self.appearance = appearance
        state = MusicSourceManagementState(model: model)
        super.init(themeProvider: appearance)
        pageTitle = "音源管理"
    }

    override func viewDidLoad() {
        super.viewDidLoad()
        view.accessibilityIdentifier = "music.sources.page"
        importingIndicator.hidesWhenStopped = true
        listView.search.placeholder = "搜索音源"
        listView.footerMoreNeedsSelection = true
        listView.onSearch = { [weak self] query in
            self?.searchQuery = query
            self?.applyFilteredRows()
        }
        listView.onSelection = { [weak self] in
            guard let self else { return }
            self.state.selected = self.listView.selected
            self.updateFooterState()
        }
        listView.onDelete = { [weak self] in self?.confirmRemoveSelected() }
        listView.translatesAutoresizingMaskIntoConstraints = false
        view.addSubview(listView)
        NSLayoutConstraint.activate([
            listView.topAnchor.constraint(equalTo: contentTopAnchor),
            listView.leadingAnchor.constraint(equalTo: view.safeAreaLayoutGuide.leadingAnchor),
            listView.trailingAnchor.constraint(equalTo: view.safeAreaLayoutGuide.trailingAnchor),
            listView.bottomAnchor.constraint(equalTo: view.safeAreaLayoutGuide.bottomAnchor),
        ])

        if state.managesScripts {
            let add = PageHeaderButton(image: PageIcon.image(.plus), accessibilityLabel: "添加音源", menu: UIMenu(children: [
                UIAction(title: "本地导入", image: UIImage(systemName: "doc")) { [weak self] _ in self?.openFiles() },
                UIAction(title: "网络导入", image: UIImage(systemName: "link")) { [weak self] _ in self?.openURL() },
            ]))
            add.accessibilityIdentifier = "music.sources.add"
            setManagementActions([add])
        }

        observe(model.settingsChanges) { [weak state] in state?.reconcileSettings() }
        let listChanges = Publishers.MergeMany([
            state.$official.removeDuplicates().dropFirst().map { _ -> Void in }.eraseToAnyPublisher(),
            state.$scripts.dropFirst().map { _ -> Void in }.eraseToAnyPublisher(),
            state.$savingOfficial.removeDuplicates().dropFirst().map { _ -> Void in }.eraseToAnyPublisher(),
            state.$importing.removeDuplicates().dropFirst().map { _ -> Void in }.eraseToAnyPublisher(),
            state.$pending.removeDuplicates().dropFirst().map { _ -> Void in }.eraseToAnyPublisher(),
            state.$selected.removeDuplicates().dropFirst().map { _ -> Void in }.eraseToAnyPublisher(),
            state.$checking.removeDuplicates().dropFirst().map { _ -> Void in }.eraseToAnyPublisher(),
            state.$officialChecks.removeDuplicates().dropFirst().map { _ -> Void in }.eraseToAnyPublisher(),
        ]).eraseToAnyPublisher()
        observe(listChanges, whenVisible: true) { [weak self] in self?.render() }
        observe(state.$error.removeDuplicates().dropFirst().map { _ -> Void in }.eraseToAnyPublisher(), whenVisible: true) { [weak self] in
            self?.showFailure()
        }
        applyTheme()
        render()
    }

    override func pageDidBecomeActive() { state.start() }
    override func pageDidBecomeInactive() {
        updatingID = nil
        cancelPageTask(key: "edit")
        state.close()
    }
    override func pageDidBecomeVisible() {
        super.pageDidBecomeVisible()
        state.show(self)
        showFailure()
    }
    override func pageDidBecomeHidden() {
        cancelPageTask(key: "edit")
        state.hide(self)
    }

    override func applyTheme() {
        super.applyTheme()
        guard isViewLoaded else { return }
        view.tintColor = UIColor(theme.settings.accent)
        listView.applyTheme(theme)
        importingIndicator.color = UIColor(theme.settings.accent)
    }

    private func render() {
        guard isViewLoaded else { return }
        allRows = MusicAccountPlatform.allCases.map { platform in
            let id = "official:" + platform.rawValue
            let checking = state.checking.contains(id)
            let status = checking ? "检测中" : Self.healthName(
                state.officialChecks[platform].map { $0 ? "ok" : "failed" })
            return ManagementListView.Row(
                id: id,
                title: platform.title,
                detail: "内置 · \(status)",
                enabled: state.officialEnabled(platform),
                failed: state.officialChecks[platform] == false,
                controlsEnabled: state.isActive,
                toggleEnabled: !state.savingOfficial,
                toggle: { [weak state] in state?.setOfficial(platform, enabled: $0) },
                menu: UIMenu(children: [
                    UIAction(title: "检测", attributes: checking ? .disabled : []) { [weak state] _ in
                        state?.checkOfficial(platform)
                    }
                ])
            )
        } + state.scripts.map { item in
            let checking = state.checking.contains(item.id)
            let status = state.isPending(item.id) && !checking
                ? "处理中" : MusicScriptSourceDetailViewController.status(item.state)
            let detail = "\(status) · \(checking ? "检测中" : Self.healthName(item.check?.summary))"
            return ManagementListView.Row(
                id: item.id,
                title: item.name,
                detail: detail,
                enabled: state.scriptEnabled(item),
                failed: item.state == .failed ||
                    item.check?.summary == "failed" || item.check?.summary == "abnormal",
                controlsEnabled: state.isActive && !state.isPending(item.id),
                edit: { [weak self] in self?.edit(item.id) },
                open: { [weak self] in self?.openScript(item.id) },
                toggle: { [weak state] in state?.setScript(item.id, enabled: $0) },
                menu: UIMenu(children: [
                    UIAction(title: "检测", attributes: state.isPending(item.id) ? .disabled : []) { [weak state] _ in
                        state?.checkScript(item.id)
                    },
                    UIAction(title: "更新", attributes: state.isPending(item.id) ? .disabled : []) { [weak self] _ in
                        self?.update(item.id)
                    },
                ])
            )
        }
        applyFilteredRows()
        listView.configureToolbar(showsSearch: true, buttons: [],
                                  accessories: state.importing ? [importingIndicator] : [])
        if state.importing { importingIndicator.startAnimating() } else { importingIndicator.stopAnimating() }
        updateFooterState()
    }

    private func applyFilteredRows() {
        let term = searchQuery.trimmingCharacters(in: .whitespacesAndNewlines).lowercased()
        let visible = term.isEmpty ? allRows : allRows.filter {
            $0.title.lowercased().contains(term) || $0.detail.lowercased().contains(term)
        }
        listView.setRows(
            visible,
            total: allRows.count,
            validIDs: Set(allRows.map(\.id)),
            emptyMessage: state.importing ? "正在导入音源" : (allRows.isEmpty ? "暂无音源" : "没有匹配的音源")
        )
        listView.setSelection(state.selected.intersection(Set(allRows.map(\.id))))
        updateFooterState()
    }

    private func updateFooterState() {
        let selectedScripts = state.scripts.filter { listView.selected.contains($0.id) }
        listView.deleteButton.isEnabled = state.isActive && !selectedScripts.isEmpty &&
            !selectedScripts.contains { state.isPending($0.id) }
        listView.footerMore.isEnabled = state.isActive && !listView.selected.isEmpty
        listView.footerMore.menu = UIMenu(children: [
            UIAction(title: "检测", attributes: listView.selected.isEmpty ? .disabled : []) { [weak state] _ in
                state?.checkSelected()
            }
        ])
    }

    private func showFailure() {
        guard detail == nil, let error = state.error else { return }
        enqueueError(error)
        state.error = nil
    }

    private func openFiles(updating id: String? = nil) {
        guard isPageActive, state.isActive, !state.importing else { return }
        updatingID = id
        let picker = UIDocumentPickerViewController(forOpeningContentTypes: [.javaScript], asCopy: true)
        picker.allowsMultipleSelection = id == nil
        picker.delegate = self
        if !presentPageModal(picker) {
            updatingID = nil
        }
    }

    func documentPicker(_ controller: UIDocumentPickerViewController, didPickDocumentsAt urls: [URL]) {
        let updating = updatingID
        updatingID = nil
        dismissPageModal(controller) { [weak self] in
            guard let self, self.isPageActive, !self.model.isClosed else { return }
            if let updating, let file = urls.first { self.state.update(updating, file: file) }
            else { self.state.importFiles(urls) }
        }
    }

    func documentPickerWasCancelled(_ controller: UIDocumentPickerViewController) {
        updatingID = nil
    }

    func openURL() {
        guard isPageActive, state.isActive, !state.importing else { return }
        let dialog = ManagementDialog(title: "网络导入", action: "导入", themeProvider: appearance)
        let input = ManagementDialog.input(placeholder: "音源网络地址")
        input.keyboardType = .URL; input.textContentType = .URL
        input.autocapitalizationType = .none; input.autocorrectionType = .no
        input.accessibilityLabel = "网络地址"; input.accessibilityIdentifier = "music.sources.url"
        dialog.addInput(input, title: "网络地址")
        dialog.accepted = { [weak self] in
            guard let self, self.isPageActive, let text = input.text else { return }
            self.state.download(text)
        }
        presentPageModal(dialog)
    }

    private func update(_ id: String) {
        guard let item = state.item(id) else { return }
        if case .file = item.origin { openFiles(updating: id) } else { state.update(id) }
    }

    private func edit(_ id: String) {
        guard isPageActive, !hasActivePageTask(key: "edit"), let service = model.scriptSources else { return }
        runPageTask(
            key: "edit",
            mapError: { MusicServiceError.sanitized($0).localizedDescription }
        ) { [weak self] in
            let detail = try await service.scriptSourceDetail(id)
            guard let self, !Task.isCancelled, self.isPageActive else { return }
            let dialog = ManagementDialog(title: "编辑音源", action: "保存", themeProvider: self.appearance)
            let name = ManagementDialog.input(placeholder: "名称")
            name.text = detail.item.name; name.accessibilityLabel = "名称"
            name.accessibilityIdentifier = "music.sources.edit.name"
            dialog.addInput(name, title: "名称")
            let address = detail.networkURL.map { url in
                let input = ManagementDialog.input(placeholder: "网络地址")
                input.text = url; input.keyboardType = .URL; input.textContentType = .URL
                input.autocapitalizationType = .none; input.autocorrectionType = .no
                input.accessibilityLabel = "网络地址"; input.accessibilityIdentifier = "music.sources.edit.url"
                dialog.addInput(input, title: "网络地址")
                return input
            }
            dialog.accepted = { [weak self] in
                guard let self, self.isPageActive, let text = name.text else { return }
                self.state.edit(id, name: text, url: address?.text)
            }
            self.presentPageModal(dialog)
        }
    }

    func confirmRemoveSelected() {
        guard isPageActive, state.isActive else { return }
        let ids = state.scripts.filter { state.selected.contains($0.id) }.map(\.id)
        guard !ids.isEmpty, !ids.contains(where: state.isPending) else { return }
        let dialog = ManagementDialog(title: "删除所选 \(ids.count) 个音源？", action: "删除", themeProvider: appearance)
        dialog.accepted = { [weak self] in
            guard let self, self.isPageActive else { return }
            for id in ids { self.state.remove(id) }
        }
        presentPageModal(dialog)
    }

    private func openScript(_ id: String) {
        guard isPageActive, presentedViewController == nil, detail == nil, state.item(id) != nil else { return }
        let controller = MusicScriptSourceDetailViewController(state: state, id: id, appearance: appearance)
        detail = controller
        navigationController?.pushViewController(controller, animated: true)
    }

    static func healthName(_ summary: String?) -> String {
        switch summary {
        case "ok": return "正常"
        case "abnormal": return "异常"
        case "failed": return "失败"
        default: return "未检测"
        }
    }
}

@MainActor
final class MusicScriptSourceDetailViewController: SettingsListViewController {
    private let state: MusicSourceManagementState
    let id: String

    init(state: MusicSourceManagementState, id: String, appearance: MusicAppearancePreferencesStore) {
        self.state = state
        self.id = id
        super.init(themeProvider: appearance)
        pageTitle = state.item(id)?.name
    }

    override func viewDidLoad() {
        super.viewDidLoad()
        let detailChanges = Publishers.MergeMany([
            state.$scripts.dropFirst().map { _ -> Void in }.eraseToAnyPublisher(),
            state.$pending.removeDuplicates().dropFirst().map { _ -> Void in }.eraseToAnyPublisher(),
            state.$details.dropFirst().map { _ -> Void in }.eraseToAnyPublisher(),
        ]).eraseToAnyPublisher()
        observe(detailChanges, whenVisible: true) { [weak self] in
            self?.render()
            self?.closeIfRemoved()
        }
        observe(state.$error.removeDuplicates().dropFirst().map { _ -> Void in }.eraseToAnyPublisher(), whenVisible: true) { [weak self] in
            self?.showFailure()
        }
        render()
    }

    override func pageDidBecomeVisible() {
        super.pageDidBecomeVisible()
        state.show(self)
        state.readDetail(id)
        showFailure()
    }

    override func pageDidBecomeHidden() {
        state.hide(self)
        super.pageDidBecomeHidden()
    }

    private func render() {
        guard isViewLoaded else { return }
        guard let item = state.item(id) else {
            applySections([])
            return
        }
        pageTitle = item.name
        var infoRows: [SettingsListRow] = [
            infoRow(id: "version", title: "版本", value: item.version)
        ]
        if !item.author.isEmpty { infoRows.append(infoRow(id: "author", title: "作者", value: item.author)) }
        if !item.summary.isEmpty { infoRows.append(infoRow(id: "summary", title: "简介", value: item.summary)) }
        if !item.homepage.isEmpty { infoRows.append(infoRow(id: "homepage", title: "主页", value: item.homepage)) }
        let originText: String = {
            if case .network(let host) = item.origin { return host }
            return "本地文件"
        }()
        infoRows.append(infoRow(id: "origin", title: "来源", value: originText))
        if !item.platforms.isEmpty {
            infoRows.append(infoRow(id: "platforms", title: "支持平台", value: item.platforms.map(\.title).joined(separator: "、")))
        }
        infoRows.append(infoRow(id: "health", title: "状态", value: MusicSourceManagementViewController.healthName(item.check?.summary)))
        infoRows.append(infoRow(id: "state", title: "运行状态", value: Self.status(item.state)))
        if let detail = state.details[id], let url = detail.networkURL {
            infoRows.append(infoRow(id: "url", title: "网络地址", value: url))
        }
        if let check = item.check {
            infoRows.append(infoRow(id: "checkedAt", title: "检测时间", value: check.checkedAt.formatted()))
        }

        let platformRows = MusicPublicBrowsePlatform.allCases.map { platform in
            infoRow(
                id: "check.\(platform.rawValue)",
                title: platform.title,
                value: MusicSourceManagementViewController.healthName(item.check?.platforms[platform.rawValue])
            )
        }

        var sections: [SettingsListSection] = [
            SettingsListSection(id: "info", rows: infoRows),
            SettingsListSection(id: "checks", title: "平台检测", rows: platformRows),
        ]
        if state.details[id]?.rollbackAvailable == true {
            sections.append(SettingsListSection(id: "rollback", rows: [
                SettingsListRow(
                    id: "music.sources.script.rollback",
                    title: "回退更新",
                    enabled: state.isActive && !state.isPending(id),
                    accessibilityLabel: "回退更新",
                    action: { [weak state, id] in state?.rollback(id) }
                )
            ]))
        }
        applySections(sections)
    }

    private func infoRow(id: String, title: String, value: String) -> SettingsListRow {
        SettingsListRow(id: "music.sources.detail.\(id)", title: title, detail: value)
    }

    private func showFailure() {
        guard let error = state.error else { return }
        enqueueError(error)
        state.error = nil
    }

    private func closeIfRemoved() {
        pageTitle = state.item(id)?.name
        guard state.item(id) == nil || !state.isActive, navigationController?.topViewController === self else { return }
        navigationController?.popViewController(animated: true)
    }

    static func status(_ value: MusicScriptSourceItem.State) -> String {
        switch value {
        case .disabled: return "已停用"
        case .idle: return "已启用"
        case .starting: return "启动中"
        case .ready: return "运行中"
        case .failed: return "加载失败"
        }
    }
}
#endif
