#if os(iOS)
import UIKit
import SwiftUI
import MeowUI

@MainActor
final class FilmPlaybackPreferencesController: FilmSettingsListController {
    enum Area { case preferences, siteClean, autoPrepare }
    private let owner: FilmPlaybackSettingsModel
    private let area: Area
    private var loading: Task<Void, Never>?
    private var saving: Task<Void, Never>?
    private let keywords = filmTextField("关键字匹配规则", id: "film.preferences.keywords")
    private var preferencesContent: UIHostingController<FilmPreferencesContent>?
    override var avoidsKeyboard: Bool { true }
    override var usesSettingsTable: Bool { area != .preferences }

    init(owner: FilmPlaybackSettingsModel, area: Area, themeProvider: any PageThemeProviding) {
        self.owner = owner; self.area = area
        super.init(themeProvider: themeProvider)
        switch area {
        case .preferences: pageTitle = "偏好设置"
        case .siteClean: pageTitle = "站点净化设置"
        case .autoPrepare: pageTitle = "自动准备"
        }
    }

    override func viewDidLoad() {
        super.viewDidLoad()
        table.accessibilityIdentifier = "film.preferences.list"
        pageHeader.setTrailing([PageHeaderTextButton(title: "保存") { [weak self] in self?.save() }])
        keywords.placeholder = nil
        keywords.smartDashesType = .no; keywords.smartQuotesType = .no
        keywords.addAction(UIAction { [weak self] _ in
            guard let self else { return }
            let values = (keywords.text ?? "").split(whereSeparator: { $0 == "," || $0 == "，" })
                .map { FilmPlaybackSettingsModel.trimInput(String($0)) }.filter { !$0.isEmpty }
            owner.edit { if self.area == .siteClean { $0.siteCleanKeywords = values } else { $0.keywords = values } }
        }, for: .editingChanged)
        keywords.addAction(UIAction { [weak self] _ in self?.update() }, for: .editingDidEnd)
        if area == .preferences {
            let content = UIHostingController(rootView: FilmPreferencesContent(owner: owner, keywords: keywords, theme: theme))
            preferencesContent = content
            addChild(content); view.addSubview(content.view)
            content.view.backgroundColor = .clear
            content.view.translatesAutoresizingMaskIntoConstraints = false
            NSLayoutConstraint.activate([
                content.view.topAnchor.constraint(equalTo: contentTopAnchor),
                content.view.leadingAnchor.constraint(equalTo: view.safeAreaLayoutGuide.leadingAnchor),
                content.view.trailingAnchor.constraint(equalTo: view.safeAreaLayoutGuide.trailingAnchor),
                content.view.bottomAnchor.constraint(equalTo: view.keyboardLayoutGuide.topAnchor),
            ])
            content.didMove(toParent: self)
        }
        observe(owner) { [weak self] in if self?.keywords.isFirstResponder != true { self?.update() } }
        load()
    }

    private func load() {
        loading = Task { [weak self] in
            guard let self else { return }
            do { try await owner.loadPreferences(); update() }
            catch { failure(error, retry: { [weak self] in self?.load() }) }
        }
    }

    private func update() {
        if area == .preferences {
            if !keywords.isFirstResponder { keywords.text = owner.draft?.keywords.joined(separator: ",") }
            return
        }
        guard let draft = owner.draft else {
            sections = [.init(title: "", rows: [.init(title: owner.preferencesBusy ? "加载中" : "暂无设置",
                                                      id: "film.preferences.state")])]
            render(); return
        }
        switch area {
        case .preferences:
            return
        case .siteClean:
            sections = [.init(title: "关键字匹配规则", rows: keywordRows(draft.siteCleanKeywords, group: .siteClean))]
        case .autoPrepare:
            sections = [.init(title: "", rows: [
                filmSwitchRow("进入播放页自动准备", id: "film.preferences.auto-prepare",
                              isOn: draft.iosAutoPrepare, enabled: !owner.preferencesBusy) { [weak owner] value in
                    owner?.edit { $0.iosAutoPrepare = value }
                },
                restoreRow("恢复默认", group: .autoPrepare),
            ])]
        }
        render()
    }

    private func restoreRow(_ title: String, group: FilmPlaybackSettingsModel.Group) -> FilmSettingsRow {
        .init(title: title, id: "film.preferences.restore.\(group)", enabled: !owner.preferencesBusy,
              action: { [weak self] in
                  self?.view.endEditing(true)
                  self?.owner.restoreDraft(group)
              })
    }

    private func keywordRows(_ values: [String], group: FilmPlaybackSettingsModel.Group) -> [FilmSettingsRow] {
        keywords.text = values.joined(separator: ",")
        keywords.isEnabled = !owner.preferencesBusy; keywords.textColor = UIColor(theme.settings.ink)
        return [.init(title: "关键字匹配规则", id: "film.preferences.keywords.row", content: keywords),
                restoreRow("恢复默认关键词", group: group)]
    }

    override func applyTheme() {
        super.applyTheme()
        preferencesContent?.rootView = FilmPreferencesContent(owner: owner, keywords: keywords, theme: theme)
    }

    private func save() {
        guard saving == nil else { return }
        view.endEditing(true)
        saving = Task { [weak self] in
            guard let self else { return }
            defer { saving = nil }
            do { try await owner.savePreferences(); notice("已保存") }
            catch { failure(error, retry: { [weak self] in self?.save() }) }
        }
    }
}

@MainActor
private struct FilmPreferencesContent: View {
    @ObservedObject var owner: FilmPlaybackSettingsModel
    let keywords: UITextField
    let theme: PageTheme
    @ScaledMetric(relativeTo: .body) private var inputHeight = UIFont.preferredFont(
        forTextStyle: .body, compatibleWith: UITraitCollection(preferredContentSizeCategory: .large)
    ).lineHeight + 16
    private struct Item: Identifiable { let id, title: String }

    var body: some View {
        List {
            if let draft = owner.draft {
                SettingsOrderingSection(title: "优先片源规则",
                    items: draft.ruleOrder.map { Item(id: $0.rawValue, title: $0.title) },
                    theme: theme, enabled: !owner.preferencesBusy) { item in
                    Text(item.title).accessibilityIdentifier("film.preferences.dimension.\(item.id)")
                } move: { offsets, destination in
                    var order = draft.ruleOrder
                    order.move(fromOffsets: offsets, toOffset: destination)
                    owner.edit { $0.ruleOrder = order }
                }
                SettingsOrderingSection(title: "网盘匹配规则",
                    items: draft.panOrder.map { Item(id: $0.rawValue, title: $0.title) },
                    theme: theme, enabled: !owner.preferencesBusy) { item in
                    Text(item.title).accessibilityIdentifier("film.preferences.pan.\(item.id)")
                } move: { offsets, destination in
                    var order = draft.panOrder
                    order.move(fromOffsets: offsets, toOffset: destination)
                    owner.edit { $0.panOrder = order }
                }
                Section {
                    KeywordsInput(field: keywords, value: draft.keywords.joined(separator: ","),
                                  enabled: !owner.preferencesBusy, theme: theme)
                        .frame(height: max(36, inputHeight))
                        .modifier(SettingsListRowStyle(theme: theme, first: true, last: true))
                } header: {
                    SettingsListSectionHeader(title: "关键字匹配规则", theme: theme)
                }
            } else {
                Text(owner.preferencesBusy ? "加载中" : "暂无设置")
            }
        }
        .environment(\.editMode, .constant(.active))
        .modifier(SettingsListStyle(theme: theme))
        .accessibilityIdentifier("film.preferences.list")
    }

    private struct KeywordsInput: UIViewRepresentable {
        let field: UITextField
        let value: String
        let enabled: Bool
        let theme: PageTheme
        func makeUIView(context: Context) -> UITextField { field }
        func updateUIView(_ view: UITextField, context: Context) {
            if !view.isFirstResponder { view.text = value }
            view.isEnabled = enabled; theme.settings.apply(to: view)
        }
    }
}
#endif
