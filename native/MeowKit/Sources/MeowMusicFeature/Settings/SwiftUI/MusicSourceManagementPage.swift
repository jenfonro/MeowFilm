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

    init(model: MusicViewModel, appearance: MusicAppearancePreferencesStore) {
        self.model = model
        state = MusicSourceManagementState(model: model)
        super.init(appearance: appearance, content: MusicSourceManagementContent(
            state: state, theme: PageTheme(palette: appearance.pageColorway)
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
    override func pageDidBecomeInactive() { state.close() }
    override func pageDidBecomeVisible() { showFailure() }

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
        pickerAccess = nil
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
            guard let self, let text = alert?.textFields?.first?.text,
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
        guard isPageActive, presentedViewController == nil, let service = model.scriptSources else { return }
        let access = model.accountAccessGeneration
        runPageTask { [weak self] in
            do {
                let detail = try await service.scriptSourceDetail(id)
                guard let self, !Task.isCancelled, self.isPageActive,
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
                    guard let self, access == self.model.accountAccessGeneration,
                          let name = alert?.textFields?.first?.text else { return }
                    let url = detail.networkURL == nil ? nil : alert?.textFields?.last?.text
                    self.state.edit(id, name: name, url: url)
                })
                self.present(alert, animated: true)
            } catch { self?.reportError(MusicServiceError.sanitized(error).localizedDescription) }
        }
    }
    private func confirmRemove(_ id: String?) {
        guard isPageActive, presentedViewController == nil else { return }
        let alert = UIAlertController(title: id == nil ? "删除所选音源？" : "删除音源？",
                                      message: id.flatMap { state.item($0)?.name }, preferredStyle: .alert)
        alert.addAction(UIAlertAction(title: "取消", style: .cancel))
        alert.addAction(UIAlertAction(title: "删除", style: .destructive) { [weak self] _ in
            if let id { self?.state.remove(id) } else { self?.state.removeSelected() }
        })
        present(alert, animated: true)
    }
    private func openScript(_ id: String) {
        guard isPageActive, presentedViewController == nil, detail == nil, state.item(id) != nil else { return }
        let controller = MusicScriptSourceDetailViewController(state: state, id: id, appearance: appearance)
        detail = controller
        navigationController?.pushViewController(controller, animated: true)
    }
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

    var body: some View {
        List {
            Section {
                HStack {
                    Button(state.selected.isEmpty ? "全选" : "取消选择") {
                        state.selected = state.selected.isEmpty ? Set(state.scripts.map(\.id) +
                            MusicAccountPlatform.allCases.map { "official:" + $0.rawValue }) : []
                    }
                    Spacer()
                    Button("检测", action: state.checkSelected).disabled(state.selected.isEmpty)
                    Button("删除", role: .destructive, action: removeSelected)
                        .disabled(!state.scripts.contains { state.selected.contains($0.id) })
                }
                .modifier(MusicSettingsRowStyle(theme: theme))
            }
            Section("音源") {
                ForEach(MusicAccountPlatform.allCases) { platform in
                    let id = "official:" + platform.rawValue
                    VStack(alignment: .leading, spacing: 10) {
                        HStack(spacing: 12) {
                            selection(id)
                            MusicPlatformLogo(assetName: platform.settingsLogoName, theme: theme)
                            Text(platform.title)
                            Text("内置").font(.caption).foregroundStyle(.secondary)
                            Spacer()
                            Toggle("启用", isOn: Binding(get: { state.officialEnabled(platform) },
                                set: { state.setOfficial(platform, enabled: $0) })).labelsHidden()
                                .disabled(state.savingOfficial)
                        }
                        HStack {
                            health(state.officialChecks[platform].map { $0 ? "ok" : "failed" }, checking: state.checking.contains(id))
                            Spacer()
                            Button("检测") { state.checkOfficial(platform) }.disabled(state.checking.contains(id))
                        }
                    }
                    .modifier(MusicSettingsRowStyle(theme: theme))
                    .accessibilityIdentifier("music.sources.official." + platform.rawValue)
                }
                ForEach(state.scripts) { item in
                    VStack(alignment: .leading, spacing: 10) {
                        HStack(spacing: 12) {
                            selection(item.id)
                            Button { openScript(item.id) } label: {
                                VStack(alignment: .leading, spacing: 4) {
                                    Text(item.name).foregroundStyle(Color(uiColor: UIColor(theme.ink)))
                                    Text(item.version).font(.caption).foregroundStyle(.secondary)
                                }
                            }.buttonStyle(.plain)
                            Text(Self.origin(item)).font(.caption).foregroundStyle(.secondary)
                            Spacer()
                            Toggle("启用", isOn: Binding(get: { state.scriptEnabled(item) },
                                set: { state.setScript(item.id, enabled: $0) })).labelsHidden()
                                .disabled(state.isPending(item.id))
                                .accessibilityLabel(item.name + " 启用")
                        }
                        health(item.check?.summary, checking: state.checking.contains(item.id))
                        ViewThatFits(in: .horizontal) {
                            platforms(item, vertical: false)
                            platforms(item, vertical: true)
                        }
                        HStack(spacing: 22) {
                            Button("检测") { state.checkScript(item.id) }
                            Button("编辑") { edit(item.id) }
                            Button("更新") { update(item.id) }
                            Spacer(minLength: 0)
                            Button("删除", role: .destructive) { remove(item.id) }
                        }
                        .font(.subheadline)
                        .disabled(state.isPending(item.id))
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
    private func selection(_ id: String) -> some View {
        Button { state.select(id) } label: {
            Image(systemName: state.selected.contains(id) ? "checkmark.circle.fill" : "circle")
                .frame(minWidth: 32, minHeight: 44)
        }.buttonStyle(.plain).accessibilityLabel(state.selected.contains(id) ? "取消选择" : "选择")
    }
    private func health(_ value: String?, checking: Bool) -> some View {
        Label(checking ? "检测中" : Self.healthName(value), systemImage: checking ? "clock" : "circle.fill")
            .font(.caption).foregroundStyle(checking ? Color.secondary : Self.healthColor(value))
    }
    private func platforms(_ item: MusicScriptSourceItem, vertical: Bool) -> some View {
        let layout = vertical ? AnyLayout(VStackLayout(alignment: .leading, spacing: 6)) : AnyLayout(HStackLayout(spacing: 12))
        return layout {
            ForEach(MusicPublicBrowsePlatform.allCases) { platform in
                let value = item.check?.platforms[platform.rawValue]
                Label(platform.title, systemImage: value == "ok" ? "checkmark.circle.fill" : "circle")
                    .font(.caption).foregroundStyle(Self.healthColor(value))
                    .accessibilityValue(Self.healthName(value))
            }
        }
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
            state: state, id: id, theme: PageTheme(palette: appearance.pageColorway)
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

    override func pageDidBecomeVisible() { state.readDetail(id); showFailure(); closeIfRemoved() }

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
        guard state.item(id) == nil || !state.available, navigationController?.topViewController === self else { return }
        navigationController?.popViewController(animated: true)
    }

    private func confirmRemove() {
        guard isPageActive, presentedViewController == nil, let item = state.item(id) else { return }
        let alert = UIAlertController(title: "删除音源？", message: item.name, preferredStyle: .alert)
        alert.addAction(UIAlertAction(title: "取消", style: .cancel))
        alert.addAction(UIAlertAction(title: "删除", style: .destructive) { [weak self] _ in
            guard let self else { return }
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

    var body: some View {
        List {
            if let item = state.item(id) {
                Section {
                    row("名称", item.name)
                    row("版本", item.version)
                    if !item.author.isEmpty { row("作者", item.author) }
                    if !item.summary.isEmpty { row("简介", item.summary) }
                    if !item.homepage.isEmpty { row("主页", item.homepage) }
                    row("来源", { if case .network(let host) = item.origin { return host }; return "本地文件" }())
                    if !item.platforms.isEmpty { row("支持平台", item.platforms.map(\.title).joined(separator: "、")) }
                    row("状态", MusicSourceManagementContent.healthName(item.check?.summary))
                    if let detail = state.details[id], let url = detail.networkURL { row("网络地址", url) }
                    if let check = item.check { row("检测时间", check.checkedAt.formatted()) }
                }
                Section {
                    if state.details[id]?.rollbackAvailable == true {
                        Button("回退更新") { state.rollback(id) }
                            .disabled(state.isPending(id))
                            .modifier(MusicSettingsRowStyle(theme: theme))
                    }
                    Button(role: .destructive, action: remove) {
                        Text("删除音源").frame(maxWidth: .infinity, alignment: .leading)
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
        HStack(alignment: .firstTextBaseline, spacing: 12) {
            Text(title).foregroundStyle(Color(uiColor: UIColor(theme.muted)))
            Spacer(minLength: 12)
            Text(value).multilineTextAlignment(.trailing).textSelection(.enabled)
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
