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
            state: state, theme: theme, openScript: { [weak self] in self?.openScript($0) }
        ))
    }

    private func showFailure() {
        // A pushed script detail reports the same shared state while it covers this page.
        guard detail == nil, let error = state.error else { return }
        reportError(error)
        state.error = nil
    }

    private func openFiles() {
        guard isPageActive, state.available, !state.importing, presentedViewController == nil else { return }
        pickerAccess = model.accountAccessGeneration
        let picker = UIDocumentPickerViewController(forOpeningContentTypes: [.javaScript], asCopy: true)
        picker.allowsMultipleSelection = true
        picker.delegate = self
        present(picker, animated: true)
    }

    func documentPicker(_ controller: UIDocumentPickerViewController, didPickDocumentsAt urls: [URL]) {
        let access = pickerAccess
        pickerAccess = nil
        controller.dismiss(animated: true) { [weak self] in
            guard let self, self.isPageActive, access == self.model.accountAccessGeneration else { return }
            self.state.importFiles(urls)
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

    var body: some View {
        List {
            Section("音源") {
                ForEach(MusicAccountPlatform.allCases) { platform in
                    Toggle(isOn: Binding(
                        get: { state.officialEnabled(platform) },
                        set: { state.setOfficial(platform, enabled: $0) }
                    )) {
                        MusicSourceRowLabel(logo: platform.settingsLogoName, title: platform.title,
                                            detail: "官方", theme: theme)
                    }
                    .disabled(!state.available || state.savingOfficial)
                    .accessibilityIdentifier("music.sources.official." + platform.rawValue)
                    .modifier(MusicSettingsRowStyle(theme: theme))
                }
                ForEach(state.scripts) { item in
                    HStack(spacing: 12) {
                        Button { openScript(item.id) } label: {
                            MusicSourceRowLabel(logo: nil, title: item.name, detail: Self.detail(item),
                                                failed: item.state == .failed, theme: theme)
                                .frame(maxWidth: .infinity, alignment: .leading)
                                .contentShape(Rectangle())
                        }
                        .buttonStyle(.plain)
                        .accessibilityIdentifier("music.sources.script." + item.id)
                        Toggle("启用", isOn: Binding(
                            get: { state.scriptEnabled(item) },
                            set: { state.setScript(item.id, enabled: $0) }
                        ))
                        .labelsHidden()
                        .disabled(!state.available || state.isPending(item.id))
                        .accessibilityLabel(item.name)
                        .accessibilityIdentifier("music.sources.script.toggle." + item.id)
                    }
                    .modifier(MusicSettingsRowStyle(theme: theme))
                }
                if state.importing {
                    ProgressView()
                        .accessibilityLabel("正在导入音源")
                        .frame(maxWidth: .infinity)
                        .modifier(MusicSettingsRowStyle(theme: theme))
                }
            }
            .headerProminence(.increased)
        }
        .modifier(MusicSettingsListStyle(theme: theme))
    }

    static func detail(_ item: MusicScriptSourceItem) -> String {
        var parts = ["脚本", item.version]
        if !item.platforms.isEmpty { parts.append(item.platforms.map(\.title).joined(separator: "、")) }
        if item.state == .failed { parts.append("加载失败") }
        return parts.joined(separator: " · ")
    }
}

private struct MusicSourceRowLabel: View {
    let logo: String?
    let title: String
    let detail: String
    var failed = false
    let theme: PageTheme

    var body: some View {
        HStack(spacing: 12) {
            MusicPlatformLogo(assetName: logo, theme: theme)
            VStack(alignment: .leading, spacing: 4) {
                Text(title)
                    .foregroundStyle(Color(uiColor: UIColor(theme.ink)))
                    .fixedSize(horizontal: false, vertical: true)
                Text(detail)
                    .font(.subheadline)
                    .foregroundColor(failed ? theme.settingsDanger : Color(uiColor: UIColor(theme.muted)))
                    .fixedSize(horizontal: false, vertical: true)
            }
        }
        .accessibilityElement(children: .combine)
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

    override func pageDidBecomeVisible() { showFailure(); closeIfRemoved() }

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
                    row("状态", Self.status(item.state))
                }
                Section {
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
