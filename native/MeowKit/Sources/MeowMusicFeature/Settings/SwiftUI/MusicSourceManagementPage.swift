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
    private let state: MusicSourceManagementState
    private var pickerAccess: UUID?
    private var updatingID: String?
    private weak var detail: MusicScriptSourceDetailViewController?
    private var editWork: (id: UUID, task: Task<Void, Never>)?

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
        pickerAccess = nil; updatingID = nil
        editWork?.task.cancel(); editWork = nil
        state.close()
    }
    override func pageDidBecomeVisible() { state.show(self); showFailure() }
    override func pageDidBecomeHidden() {
        editWork?.task.cancel(); editWork = nil
        state.hide(self)
    }

    override func applyTheme() {
        super.applyTheme()
        guard contentThemeChanged() else { return }
        setContent(MusicSourceManagementContent(
            state: state, theme: theme, openScript: { [weak self] in self?.openScript($0) },
            edit: { [weak self] in self?.edit($0) }, update: { [weak self] in self?.update($0) },
            remove: { [weak self] in self?.confirmRemove($0) }, removeSelected: { [weak self] in self?.confirmRemove(nil) }
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

    private func openURL() {
        guard isPageActive, state.available, !state.importing, presentedViewController == nil else { return }
        let access = model.accountAccessGeneration
        let alert = UIAlertController(title: "网络导入", message: nil, preferredStyle: .alert)
        alert.addTextField { field in
            field.keyboardType = .URL
            field.textContentType = .URL
            field.autocapitalizationType = .none
            field.autocorrectionType = .no
            field.accessibilityIdentifier = "music.sources.url"
        }
        alert.addAction(UIAlertAction(title: "取消", style: .cancel))
        alert.addAction(UIAlertAction(title: "导入", style: .default) { [weak self, weak alert] _ in
            guard let self, self.isPageActive, let text = alert?.textFields?.first?.text,
                  access == self.model.accountAccessGeneration else { return }
            self.state.download(text)
        })
        present(alert, animated: true)
    }

    private func update(_ id: String) {
        guard let item = state.item(id) else { return }
        if case .file = item.origin { openFiles(updating: id) } else { state.update(id) }
    }
    private func edit(_ id: String) {
        guard isPageActive, editWork == nil, presentedViewController == nil, let service = model.scriptSources else { return }
        let access = model.accountAccessGeneration
        let workID = UUID()
        let task = Task { [weak self] in
            defer { if self?.editWork?.id == workID { self?.editWork = nil } }
            guard !Task.isCancelled else { return }
            do {
                let detail = try await service.scriptSourceDetail(id)
                guard let self, !Task.isCancelled, self.isPageActive, self.canPresentPageAlert,
                      access == self.model.accountAccessGeneration else { return }
                let alert = UIAlertController(title: "编辑音源", message: nil, preferredStyle: .alert)
                alert.addTextField { $0.text = detail.item.name; $0.accessibilityLabel = "名称" }
                if let url = detail.networkURL {
                    alert.addTextField {
                        $0.text = url; $0.keyboardType = .URL; $0.autocapitalizationType = .none
                        $0.autocorrectionType = .no; $0.accessibilityLabel = "网络地址"
                    }
                }
                alert.addAction(UIAlertAction(title: "取消", style: .cancel))
                alert.addAction(UIAlertAction(title: "保存", style: .default) { [weak self, weak alert] _ in
                    guard let self, self.isPageActive, access == self.model.accountAccessGeneration,
                          let name = alert?.textFields?.first?.text else { return }
                    let url = detail.networkURL == nil ? nil : alert?.textFields?.last?.text
                    self.state.edit(id, name: name, url: url)
                })
                self.present(alert, animated: true)
            } catch {
                guard let self, !Task.isCancelled, self.isPageActive,
                      access == self.model.accountAccessGeneration else { return }
                self.reportError(MusicServiceError.sanitized(error).localizedDescription)
            }
        }
        editWork = (workID, task)
    }
    private func confirmRemove(_ id: String?) {
        guard isPageActive, presentedViewController == nil else { return }
        let access = model.accountAccessGeneration
        let ids = id.map { [$0] } ?? state.scripts.filter { state.selected.contains($0.id) }.map(\.id)
        let alert = UIAlertController(title: id == nil ? "删除所选音源？" : "删除音源？",
                                      message: id.flatMap { state.item($0)?.name }, preferredStyle: .alert)
        alert.addAction(UIAlertAction(title: "取消", style: .cancel))
        alert.addAction(UIAlertAction(title: "删除", style: .destructive) { [weak self] _ in
            guard let self, self.isPageActive, access == self.model.accountAccessGeneration else { return }
            for id in ids { self.state.remove(id) }
        })
        present(alert, animated: true)
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
    var remove: (String) -> Void = { _ in }
    var removeSelected: () -> Void = {}
    @State private var selecting = false
    @Environment(\.dynamicTypeSize) private var typeSize

    var body: some View {
        List {
            Section {
                sourceLayout {
                    Button { selecting.toggle() } label: {
                        Text(selecting ? "完成选择" : "选择")
                            .frame(maxWidth: .infinity, minHeight: 44, alignment: .leading)
                            .contentShape(Rectangle())
                    }
                    .accessibilityIdentifier("music.sources.select")
                    if selecting {
                        Menu {
                            Button("全选") {
                                state.selected = Set(state.scripts.map(\.id) +
                                    MusicAccountPlatform.allCases.map { "official:" + $0.rawValue })
                            }
                            Button("取消选择") { state.selected = [] }
                            Button("检测", action: state.checkSelected).disabled(state.selected.isEmpty)
                            Button("删除", role: .destructive, action: removeSelected)
                                .disabled(!state.scripts.contains { state.selected.contains($0.id) })
                        } label: {
                            Text("已选 \(state.selected.count)").frame(minWidth: 44, minHeight: 44)
                        }
                        .accessibilityLabel("批量操作，已选 \(state.selected.count) 个音源")
                        .accessibilityIdentifier("music.sources.batch")
                    }
                }
                .modifier(MusicSettingsRowStyle(theme: theme))
            }
            Section {
                ForEach(MusicAccountPlatform.allCases) { platform in
                    let id = "official:" + platform.rawValue
                    sourceLayout {
                        HStack(spacing: 8) {
                            if selecting { selection(id) }
                            VStack(alignment: .leading, spacing: 4) {
                                Text(platform.title).foregroundStyle(Color(uiColor: UIColor(theme.ink)))
                                    .fixedSize(horizontal: false, vertical: true)
                                health(state.officialChecks[platform].map { $0 ? "ok" : "failed" },
                                       checking: state.checking.contains(id), prefix: "内置")
                            }
                            .frame(maxWidth: .infinity, alignment: .leading)
                        }
                        if !selecting {
                            HStack(spacing: 8) {
                                Toggle("启用", isOn: Binding(get: { state.officialEnabled(platform) },
                                    set: { state.setOfficial(platform, enabled: $0) })).labelsHidden()
                                    .disabled(state.savingOfficial)
                                    .accessibilityLabel(platform.title + " 启用")
                                    .frame(minHeight: 44)
                                Menu {
                                    Button("检测") { state.checkOfficial(platform) }.disabled(state.checking.contains(id))
                                } label: { more(platform.title) }
                            }
                        }
                    }
                    .modifier(MusicSettingsRowStyle(theme: theme))
                    .accessibilityIdentifier("music.sources.official." + platform.rawValue)
                }
                ForEach(state.scripts) { item in
                    sourceLayout {
                        HStack(spacing: 8) {
                            if selecting { selection(item.id) }
                            VStack(alignment: .leading, spacing: 4) {
                                Button {
                                    if selecting { state.select(item.id) } else { openScript(item.id) }
                                } label: {
                                    Text(item.name).foregroundStyle(Color(uiColor: UIColor(theme.ink)))
                                        .frame(maxWidth: .infinity, minHeight: 44, alignment: .leading)
                                        .fixedSize(horizontal: false, vertical: true)
                                }.buttonStyle(.plain)
                                health(item.check?.summary, checking: state.checking.contains(item.id),
                                       prefix: state.isPending(item.id) && !state.checking.contains(item.id)
                                        ? "处理中" : MusicScriptSourceDetailContent.status(item.state))
                            }
                            .frame(maxWidth: .infinity, alignment: .leading)
                        }
                        if !selecting {
                            HStack(spacing: 8) {
                                Toggle("启用", isOn: Binding(get: { state.scriptEnabled(item) },
                                    set: { state.setScript(item.id, enabled: $0) })).labelsHidden()
                                    .disabled(state.isPending(item.id))
                                    .accessibilityLabel(item.name + " 启用")
                                    .frame(minHeight: 44)
                                Menu {
                                    Button("检测") { state.checkScript(item.id) }
                                    Button("编辑") { edit(item.id) }
                                    Button("更新") { update(item.id) }
                                    Button("删除", role: .destructive) { remove(item.id) }
                                } label: { more(item.name) }
                                .disabled(state.isPending(item.id))
                            }
                        }
                    }
                    .modifier(MusicSettingsRowStyle(theme: theme))
                    .accessibilityIdentifier("music.sources.script." + item.id)
                }
                if state.importing { ProgressView().accessibilityLabel("正在导入音源") }
            }
        }
        .buttonStyle(.borderless)
        .disabled(!state.available)
        .modifier(MusicSettingsListStyle(theme: theme))
    }
    private func sourceLayout<Content: View>(@ViewBuilder content: () -> Content) -> some View {
        let layout = typeSize.isAccessibilitySize
            ? AnyLayout(VStackLayout(alignment: .trailing, spacing: 8))
            : AnyLayout(HStackLayout(alignment: .center, spacing: 12))
        return layout { content() }
    }
    private func more(_ name: String) -> some View {
        Image(systemName: "ellipsis").frame(minWidth: 44, minHeight: 44)
            .contentShape(Rectangle()).accessibilityLabel(name + " 更多操作")
    }
    private func selection(_ id: String) -> some View {
        Button { state.select(id) } label: {
            Image(systemName: state.selected.contains(id) ? "checkmark.circle.fill" : "circle")
                .frame(minWidth: 44, minHeight: 44)
        }.buttonStyle(.plain).accessibilityLabel((state.selected.contains(id) ? "取消选择 " : "选择 ") +
            (state.item(id)?.name ?? MusicAccountPlatform.allCases.first { id == "official:" + $0.rawValue }?.title ?? "音源"))
    }
    private func health(_ value: String?, checking: Bool, prefix: String) -> some View {
        Label(prefix + " · " + (checking ? "检测中" : Self.healthName(value)), systemImage: checking ? "clock" : "circle.fill")
            .font(.caption).foregroundStyle(checking ? Color.secondary : Self.healthColor(value))
            .fixedSize(horizontal: false, vertical: true)
    }
    static func origin(_ item: MusicScriptSourceItem) -> String { if case .file = item.origin { return "本地" }; return "网络" }
    static func detail(_ item: MusicScriptSourceItem) -> String { origin(item) + " · " + item.version }
    static func healthName(_ value: String?) -> String {
        switch value { case "ok", "normal": return "正常"; case "partial": return "部分可用"; case "failed", "abnormal": return "异常"
        case "unsupported": return "不支持"; default: return "未检测" }
    }
    static func healthColor(_ value: String?) -> Color {
        switch value { case "ok", "normal": return .green; case "partial": return .orange; case "failed", "abnormal": return .red; default: return .secondary }
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
            state: state, id: id, theme: theme, remove: { [weak self] in self?.confirmRemove() }
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

    private func confirmRemove() {
        guard isPageActive, presentedViewController == nil, let item = state.item(id) else { return }
        let access = state.model.accountAccessGeneration
        let alert = UIAlertController(title: "删除音源？", message: item.name, preferredStyle: .alert)
        alert.addAction(UIAlertAction(title: "取消", style: .cancel))
        alert.addAction(UIAlertAction(title: "删除", style: .destructive) { [weak self] _ in
            guard let self, self.isPageActive, access == self.state.model.accountAccessGeneration else { return }
            self.state.remove(self.id)
        })
        present(alert, animated: true)
    }
}

@MainActor
struct MusicScriptSourceDetailContent: View {
    @ObservedObject var state: MusicSourceManagementState
    let id: String
    let theme: PageTheme
    var remove: () -> Void = {}
    @Environment(\.dynamicTypeSize) private var typeSize

    var body: some View {
        List {
            if let item = state.item(id) {
                Section {
                    row("版本", item.version)
                    if !item.author.isEmpty { row("作者", item.author) }
                    if !item.summary.isEmpty { row("简介", item.summary) }
                    if !item.homepage.isEmpty { row("主页", item.homepage) }
                    row("来源", { if case .network(let host) = item.origin { return host }; return "本地文件" }())
                    if !item.platforms.isEmpty { row("支持平台", item.platforms.map(\.title).joined(separator: "、")) }
                    row("状态", MusicSourceManagementContent.healthName(item.check?.summary))
                    row("运行状态", Self.status(item.state))
                    if let detail = state.details[id], let url = detail.networkURL { row("网络地址", url) }
                    if let check = item.check { row("检测时间", check.checkedAt.formatted()) }
                }
                Section("平台检测") {
                    ForEach(MusicPublicBrowsePlatform.allCases) { platform in
                        row(platform.title, MusicSourceManagementContent.healthName(item.check?.platforms[platform.rawValue]))
                    }
                }
                Section {
                    if state.details[id]?.rollbackAvailable == true {
                        Button { state.rollback(id) } label: {
                            Text("回退更新")
                                .frame(maxWidth: .infinity, minHeight: 44, alignment: .leading)
                                .contentShape(Rectangle())
                        }
                        .disabled(!state.available || state.isPending(id))
                        .modifier(MusicSettingsRowStyle(theme: theme))
                    }
                    Button(role: .destructive, action: remove) {
                        Text("删除音源")
                            .frame(maxWidth: .infinity, minHeight: 44, alignment: .leading)
                            .contentShape(Rectangle())
                    }
                    .disabled(!state.available || state.isPending(id))
                    .accessibilityIdentifier("music.sources.script.remove")
                    .modifier(MusicSettingsRowStyle(theme: theme))
                }
            }
        }
        .modifier(MusicSettingsListStyle(theme: theme))
    }

    private func row(_ title: String, _ value: String) -> some View {
        let layout = typeSize.isAccessibilitySize
            ? AnyLayout(VStackLayout(alignment: .leading, spacing: 8))
            : AnyLayout(HStackLayout(alignment: .firstTextBaseline, spacing: 12))
        return layout {
            Text(title).foregroundStyle(Color(uiColor: UIColor(theme.muted)))
            if !typeSize.isAccessibilitySize { Spacer(minLength: 12) }
            Text(value).multilineTextAlignment(typeSize.isAccessibilitySize ? .leading : .trailing)
                .textSelection(.enabled)
        }
        .accessibilityElement(children: .combine)
        .modifier(MusicSettingsRowStyle(theme: theme))
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
