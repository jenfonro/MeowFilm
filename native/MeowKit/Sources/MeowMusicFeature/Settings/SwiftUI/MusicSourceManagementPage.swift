#if os(iOS)
import SwiftUI
import UIKit
import Combine
import UniformTypeIdentifiers
import MeowUI

@MainActor
final class MusicSourceManagementViewController: MusicSettingsPageViewController<MusicSourceManagementContent>,
    UIDocumentPickerDelegate
{
    private let model: MusicViewModel
    let state: MusicSourceManagementState
    private var pickerAccess: UUID?
    private var updatingID: String?
    private weak var detail: MusicScriptSourceDetailViewController?
    private var editWork: (id: UUID, task: Task<Void, Never>)?
    private var presentationGeneration = UUID()

    init(model: MusicViewModel, appearance: MusicAppearancePreferencesStore) {
        self.model = model
        state = MusicSourceManagementState(model: model)
        super.init(appearance: appearance, content: MusicSourceManagementContent(
            state: state, theme: PageTheme(colors: appearance.pagePalette)
        ))
        pageTitle = "音源管理"
    }

    override func viewDidLoad() {
        super.viewDidLoad()
        observe(model.settingsChanges) { [weak state] in state?.reconcileSettings() }
        observe(model.accessChanges) { [weak self] in
            guard let self else { return }
            invalidateErrorPresentation()
            presentationGeneration = UUID()
            editWork?.task.cancel(); editWork = nil
            state.reconcileAccess()
        }
        observe(state.$error.removeDuplicates().map { _ -> Void in }.eraseToAnyPublisher()) { [weak self] in
            self?.showFailure()
        }
        if state.managesScripts {
            let add = PageHeaderButton(image: PageIcon.image(.plus), accessibilityLabel: "添加音源", menu: UIMenu(children: [
                UIAction(title: "本地导入", image: UIImage(systemName: "doc")) { [weak self] _ in self?.openFiles() },
                UIAction(title: "网络导入", image: UIImage(systemName: "link")) { [weak self] _ in self?.openURL() },
            ]))
            add.accessibilityIdentifier = "music.sources.add"
            pageHeader.setTrailing([add])
        }
    }

    override func pageDidBecomeActive() { state.start() }
    override func pageDidBecomeInactive() {
        presentationGeneration = UUID()
        pickerAccess = nil; updatingID = nil
        editWork?.task.cancel(); editWork = nil
        state.close()
    }
    override func pageDidBecomeVisible() { state.show(self); showFailure() }
    override func pageDidBecomeHidden() {
        presentationGeneration = UUID()
        editWork?.task.cancel(); editWork = nil
        state.hide(self)
    }

    override func applyTheme() {
        super.applyTheme()
        guard contentThemeChanged() else { return }
        setContent(MusicSourceManagementContent(
            state: state, theme: theme, openScript: { [weak self] in self?.openScript($0) },
            edit: { [weak self] in self?.edit($0) }, update: { [weak self] in self?.update($0) },
            removeSelected: { [weak self] in self?.confirmRemoveSelected() }
        ))
    }

    private func showFailure() {
        // A pushed script detail reports the same shared state while it covers this page.
        guard detail == nil, let error = state.error else { return }
        reportError(error)
        state.error = nil
    }

    private func openFiles(updating id: String? = nil) {
        guard isPageActive, state.available, !state.importing, presentedViewController == nil else { return }
        updatingID = id
        pickerAccess = model.accountAccessGeneration
        let picker = UIDocumentPickerViewController(forOpeningContentTypes: [.javaScript], asCopy: true)
        picker.allowsMultipleSelection = id == nil
        picker.delegate = self
        present(picker, animated: true)
    }

    func documentPicker(_ controller: UIDocumentPickerViewController, didPickDocumentsAt urls: [URL]) {
        let access = pickerAccess, updating = updatingID
        updatingID = nil
        pickerAccess = nil
        controller.dismiss(animated: true) { [weak self] in
            guard let self, self.isPageActive, access == self.model.accountAccessGeneration else { return }
            if let updating, let file = urls.first { self.state.update(updating, file: file) }
            else { self.state.importFiles(urls) }
        }
    }

    func documentPickerWasCancelled(_ controller: UIDocumentPickerViewController) {
        pickerAccess = nil; updatingID = nil
    }

    func openURL() {
        guard isPageActive, state.available, !state.importing, presentedViewController == nil else { return }
        let access = model.accountAccessGeneration, presentation = presentationGeneration
        let dialog = ManagementDialog(title: "网络导入", action: "导入", themeProvider: appearance)
        let input = ManagementDialog.input(placeholder: "音源网络地址")
        input.keyboardType = .URL; input.textContentType = .URL
        input.autocapitalizationType = .none; input.autocorrectionType = .no
        input.accessibilityLabel = "网络地址"; input.accessibilityIdentifier = "music.sources.url"
        dialog.body.addArrangedSubview(input)
        dialog.accepted = { [weak self] in
            guard let self, self.isPageActive, self.isPageVisible, let text = input.text,
                  presentation == self.presentationGeneration,
                  access == self.model.accountAccessGeneration else { return }
            self.state.download(text)
        }
        present(dialog, animated: true)
    }

    private func update(_ id: String) {
        guard let item = state.item(id) else { return }
        if case .file = item.origin { openFiles(updating: id) } else { state.update(id) }
    }
    private func edit(_ id: String) {
        guard isPageActive, editWork == nil, presentedViewController == nil, let service = model.scriptSources else { return }
        let access = model.accountAccessGeneration, presentation = presentationGeneration
        let workID = UUID()
        let task = Task { [weak self] in
            defer { if self?.editWork?.id == workID { self?.editWork = nil } }
            guard !Task.isCancelled else { return }
            do {
                let detail = try await service.scriptSourceDetail(id)
                guard let self, !Task.isCancelled, self.isPageActive, self.canPresentPageAlert,
                      presentation == self.presentationGeneration,
                      access == self.model.accountAccessGeneration else { return }
                let dialog = ManagementDialog(title: "编辑音源", action: "保存", themeProvider: self.appearance)
                let name = ManagementDialog.input(placeholder: "名称")
                name.text = detail.item.name; name.accessibilityLabel = "名称"
                name.accessibilityIdentifier = "music.sources.edit.name"
                let nameTitle = UILabel(); nameTitle.text = "名称"
                nameTitle.textColor = UIColor { UIColor(SettingsTheme(dark: $0.userInterfaceStyle == .dark).ink) }
                nameTitle.font = .preferredFont(forTextStyle: .subheadline)
                nameTitle.adjustsFontForContentSizeCategory = true
                dialog.body.addArrangedSubview(nameTitle); dialog.body.addArrangedSubview(name)
                let address = detail.networkURL.map { url in
                    let input = ManagementDialog.input(placeholder: "网络地址")
                    input.text = url; input.keyboardType = .URL; input.textContentType = .URL
                    input.autocapitalizationType = .none; input.autocorrectionType = .no
                    input.accessibilityLabel = "网络地址"; input.accessibilityIdentifier = "music.sources.edit.url"
                    let title = UILabel(); title.text = "网络地址"
                    title.textColor = UIColor { UIColor(SettingsTheme(dark: $0.userInterfaceStyle == .dark).ink) }
                    title.font = .preferredFont(forTextStyle: .subheadline)
                    title.adjustsFontForContentSizeCategory = true
                    dialog.body.addArrangedSubview(title); dialog.body.addArrangedSubview(input)
                    return input
                }
                dialog.accepted = { [weak self] in
                    guard let self, self.isPageActive, self.isPageVisible,
                          presentation == self.presentationGeneration,
                          access == self.model.accountAccessGeneration, let text = name.text else { return }
                    self.state.edit(id, name: text, url: address?.text)
                }
                self.present(dialog, animated: true)
            } catch {
                guard let self, !Task.isCancelled, self.isPageActive,
                      access == self.model.accountAccessGeneration else { return }
                self.reportError(MusicServiceError.sanitized(error).localizedDescription)
            }
        }
        editWork = (workID, task)
    }
    func confirmRemoveSelected() {
        guard isPageActive, state.available, presentedViewController == nil else { return }
        let access = model.accountAccessGeneration, presentation = presentationGeneration
        let ids = state.scripts.filter { state.selected.contains($0.id) }.map(\.id)
        guard !ids.isEmpty, !ids.contains(where: state.isPending) else { return }
        let dialog = ManagementDialog(title: "删除所选 \(ids.count) 个音源？", action: "删除", themeProvider: appearance)
        dialog.accepted = { [weak self] in
            guard let self, self.isPageActive, self.isPageVisible,
                  presentation == self.presentationGeneration,
                  access == self.model.accountAccessGeneration else { return }
            for id in ids { self.state.remove(id) }
        }
        present(dialog, animated: true)
    }
    private func openScript(_ id: String) {
        guard isPageActive, presentedViewController == nil, detail == nil, state.item(id) != nil else { return }
        let controller = MusicScriptSourceDetailViewController(state: state, id: id, appearance: appearance)
        detail = controller
        navigationController?.pushViewController(controller, animated: true)
    }

    deinit { editWork?.task.cancel() }
}

@MainActor
struct MusicSourceManagementContent: View {
    @ObservedObject var state: MusicSourceManagementState
    let theme: PageTheme
    var openScript: (String) -> Void = { _ in }
    var edit: (String) -> Void = { _ in }
    var update: (String) -> Void = { _ in }
    var removeSelected: () -> Void = {}

    var body: some View {
        MusicSourceManagementList(state: state, theme: theme, openScript: openScript,
                                  edit: edit, update: update, removeSelected: removeSelected)
    }
    static func origin(_ item: MusicScriptSourceItem) -> String { if case .file = item.origin { return "本地" }; return "网络" }
    static func detail(_ item: MusicScriptSourceItem) -> String { origin(item) + " · " + item.version }
    static func healthName(_ value: String?) -> String {
        switch value { case "ok", "normal": return "正常"; case "partial": return "部分可用"; case "failed", "abnormal": return "异常"
        case "unsupported": return "不支持"; default: return "未检测" }
    }
}

@MainActor
private struct MusicSourceManagementList: UIViewRepresentable {
    @ObservedObject var state: MusicSourceManagementState
    let theme: PageTheme
    let openScript: (String) -> Void
    let edit: (String) -> Void
    let update: (String) -> Void
    let removeSelected: () -> Void
    @MainActor
    final class Coordinator {
        let importingIndicator = UIActivityIndicatorView(style: .medium)
        var rows: [ManagementListView.Row] = []
        var query = ""

        func renderRows(in view: ManagementListView, state: MusicSourceManagementState) {
            let needle = query.trimmingCharacters(in: .whitespacesAndNewlines)
            let visible = needle.isEmpty ? rows : rows.filter {
                [$0.title, $0.detail, $0.id].contains { $0.localizedCaseInsensitiveContains(needle) }
            }
            view.setRows(visible, total: rows.count, validIDs: Set(rows.map(\.id)),
                         emptyMessage: state.importing ? "正在导入音源" :
                            (rows.isEmpty ? "暂无音源" : "没有匹配的音源"))
            view.setSelection(state.selected)
            updateDeleteButton(view, state: state)
        }

        func updateDeleteButton(_ view: ManagementListView, state: MusicSourceManagementState) {
            let selectedScripts = state.scripts.filter { view.selected.contains($0.id) }
            view.deleteButton.isEnabled = state.available && !selectedScripts.isEmpty &&
                !selectedScripts.contains { state.isPending($0.id) }
        }
    }

    func makeCoordinator() -> Coordinator { Coordinator() }

    func makeUIView(context: Context) -> ManagementListView {
        let view = ManagementListView()
        view.search.placeholder = "搜索音源"
        view.configureToolbar(showsSearch: true, buttons: [], accessories:
            state.importing ? [context.coordinator.importingIndicator] : [])
        view.footerMoreNeedsSelection = true
        let coordinator = context.coordinator
        let state = state
        view.onSearch = { [weak coordinator, weak view] query in
            guard let coordinator, let view else { return }
            coordinator.query = query
            coordinator.renderRows(in: view, state: state)
        }
        view.onSelection = { [weak coordinator, weak view] in
            guard let coordinator, let view else { return }
            state.selected = view.selected
            coordinator.updateDeleteButton(view, state: state)
        }
        view.onDelete = removeSelected
        return view
    }

    func updateUIView(_ view: ManagementListView, context: Context) {
        view.onDelete = removeSelected
        view.applyTheme(theme)
        let rows = MusicAccountPlatform.allCases.map { platform in
            let id = "official:" + platform.rawValue
            let checking = state.checking.contains(id)
            let status = checking ? "检测中" : MusicSourceManagementContent.healthName(
                state.officialChecks[platform].map { $0 ? "ok" : "failed" })
            return ManagementListView.Row(id: id, title: platform.title,
                detail: "内置 · \(status)", enabled: state.officialEnabled(platform),
                failed: state.officialChecks[platform] == false, controlsEnabled: state.available,
                toggleEnabled: !state.savingOfficial,
                toggle: { state.setOfficial(platform, enabled: $0) }, menu: UIMenu(children: [
                    UIAction(title: "检测", attributes: checking ? .disabled : []) { _ in state.checkOfficial(platform) },
                ]))
        } + state.scripts.map { item in
            let checking = state.checking.contains(item.id)
            let status = state.isPending(item.id) && !checking
                ? "处理中" : MusicScriptSourceDetailContent.status(item.state)
            let detail = "\(status) · \(checking ? "检测中" : MusicSourceManagementContent.healthName(item.check?.summary))"
            return ManagementListView.Row(id: item.id, title: item.name, detail: detail,
                enabled: state.scriptEnabled(item), failed: item.state == .failed ||
                    item.check?.summary == "failed" || item.check?.summary == "abnormal",
                controlsEnabled: state.available && !state.isPending(item.id),
                edit: { edit(item.id) }, open: { openScript(item.id) },
                toggle: { state.setScript(item.id, enabled: $0) },
                menu: UIMenu(children: [
                    UIAction(title: "检测", attributes: state.isPending(item.id) ? .disabled : []) { _ in state.checkScript(item.id) },
                    UIAction(title: "更新", attributes: state.isPending(item.id) ? .disabled : []) { _ in update(item.id) },
                ]))
        }
        context.coordinator.rows = rows
        context.coordinator.renderRows(in: view, state: state)
        let indicator = context.coordinator.importingIndicator
        view.configureToolbar(showsSearch: true, buttons: [],
                              accessories: state.importing ? [indicator] : [])
        if state.importing { indicator.startAnimating() } else { indicator.stopAnimating() }
        indicator.color = UIColor(theme.settings.accent)
        context.coordinator.updateDeleteButton(view, state: state)
        view.footerMore.isEnabled = state.available && !view.selected.isEmpty
        view.footerMore.menu = UIMenu(children: [
            UIAction(title: "检测", attributes: view.selected.isEmpty ? .disabled : []) {
                _ in state.checkSelected()
            },
        ])
    }

}

@MainActor
final class MusicScriptSourceDetailViewController: MusicSettingsPageViewController<MusicScriptSourceDetailContent> {
    private let state: MusicSourceManagementState
    let id: String

    init(state: MusicSourceManagementState, id: String, appearance: MusicAppearancePreferencesStore) {
        self.state = state; self.id = id
        super.init(appearance: appearance, content: MusicScriptSourceDetailContent(
            state: state, id: id, theme: PageTheme(colors: appearance.pagePalette)
        ))
        pageTitle = state.item(id)?.name
    }

    override func viewDidLoad() {
        super.viewDidLoad()
        observe(state.$scripts.map { _ -> Void in }.eraseToAnyPublisher()) { [weak self] in self?.closeIfRemoved() }
        observe(state.$error.removeDuplicates().map { _ -> Void in }.eraseToAnyPublisher()) { [weak self] in
            self?.showFailure()
        }
    }

    override func pageDidBecomeVisible() { state.show(self); state.readDetail(id); showFailure(); closeIfRemoved() }
    override func pageDidBecomeHidden() { state.hide(self) }

    override func applyTheme() {
        super.applyTheme()
        guard contentThemeChanged() else { return }
        setContent(MusicScriptSourceDetailContent(
            state: state, id: id, theme: theme
        ))
    }

    private func showFailure() {
        guard let error = state.error else { return }
        reportError(error)
        state.error = nil
    }

    private func closeIfRemoved() {
        pageTitle = state.item(id)?.name
        guard state.item(id) == nil || !state.available, navigationController?.topViewController === self else { return }
        navigationController?.popViewController(animated: true)
    }
}

@MainActor
struct MusicScriptSourceDetailContent: View {
    @ObservedObject var state: MusicSourceManagementState
    let id: String
    let theme: PageTheme
    @Environment(\.dynamicTypeSize) private var typeSize

    var body: some View {
        List {
            if let item = state.item(id) {
                Section {
                    row("版本", item.version, first: true)
                    if !item.author.isEmpty { row("作者", item.author) }
                    if !item.summary.isEmpty { row("简介", item.summary) }
                    if !item.homepage.isEmpty { row("主页", item.homepage) }
                    row("来源", { if case .network(let host) = item.origin { return host }; return "本地文件" }())
                    if !item.platforms.isEmpty { row("支持平台", item.platforms.map(\.title).joined(separator: "、")) }
                    row("状态", MusicSourceManagementContent.healthName(item.check?.summary))
                    row("运行状态", Self.status(item.state),
                        last: state.details[id]?.networkURL == nil && item.check == nil)
                    if let detail = state.details[id], let url = detail.networkURL {
                        row("网络地址", url, last: item.check == nil)
                    }
                    if let check = item.check { row("检测时间", check.checkedAt.formatted(), last: true) }
                }
                .listSectionSeparator(.hidden)
                Section("平台检测") {
                    ForEach(MusicPublicBrowsePlatform.allCases) { platform in
                        row(platform.title, MusicSourceManagementContent.healthName(item.check?.platforms[platform.rawValue]),
                            first: platform == MusicPublicBrowsePlatform.allCases.first,
                            last: platform == MusicPublicBrowsePlatform.allCases.last)
                    }
                }
                .listSectionSeparator(.hidden)
                if state.details[id]?.rollbackAvailable == true {
                    Section {
                        Button { state.rollback(id) } label: {
                            Text("回退更新")
                                .foregroundStyle(Color(uiColor: UIColor(theme.settings.accent)))
                                .frame(maxWidth: .infinity, minHeight: 44, alignment: .leading)
                                .contentShape(Rectangle())
                        }
                        .disabled(!state.available || state.isPending(id))
                        .accessibilityLabel("回退更新")
                        .accessibilityIdentifier("music.sources.script.rollback")
                        .modifier(MusicSettingsRowStyle(theme: theme, first: true, last: true))
                    }
                    .listSectionSeparator(.hidden)
                }
            }
        }
        .modifier(MusicSettingsListStyle(theme: theme))
    }

    private func row(_ title: String, _ value: String, first: Bool = false, last: Bool = false) -> some View {
        let layout = typeSize.isAccessibilitySize
            ? AnyLayout(VStackLayout(alignment: .leading, spacing: 8))
            : AnyLayout(HStackLayout(alignment: .firstTextBaseline, spacing: 12))
        return layout {
            Text(title).foregroundStyle(Color(uiColor: UIColor(theme.settings.muted)))
            if !typeSize.isAccessibilitySize { Spacer(minLength: 12) }
            Text(value).multilineTextAlignment(typeSize.isAccessibilitySize ? .leading : .trailing)
                .textSelection(.enabled)
        }
        .accessibilityElement(children: .combine)
        .modifier(MusicSettingsRowStyle(theme: theme, first: first, last: last))
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
