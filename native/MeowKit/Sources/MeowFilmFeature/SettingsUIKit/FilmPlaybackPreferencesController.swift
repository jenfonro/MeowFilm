#if os(iOS)
import UIKit
import MeowUI

@MainActor
final class FilmPlaybackPreferencesController: FilmSettingsListController {
    enum Area { case preferences, siteClean, autoPrepare }
    private let owner: FilmPlaybackSettingsModel
    private let area: Area
    private var loading: Task<Void, Never>?
    private var saving: Task<Void, Never>?
    private let keywords = filmTextField("关键字匹配规则", id: "film.preferences.keywords")
    override var avoidsKeyboard: Bool { true }

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
                .map { $0.trimmingCharacters(in: .whitespacesAndNewlines) }.filter { !$0.isEmpty }
            owner.edit { if self.area == .siteClean { $0.siteCleanKeywords = values } else { $0.keywords = values } }
        }, for: .editingChanged)
        keywords.addAction(UIAction { [weak self] _ in self?.update() }, for: .editingDidEnd)
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
        guard let draft = owner.draft else {
            sections = [.init(title: "", rows: [.init(title: owner.busy ? "加载中" : "暂无设置",
                                                      id: "film.preferences.state")])]
            render(); return
        }
        switch area {
        case .preferences:
            sections = [
                .init(title: "优先片源规则", rows: draft.ruleOrder.enumerated().map { index, dimension in
                    .init(title: dimension.title, detail: "\(index + 1)", id: "film.preferences.dimension.\(dimension.rawValue)",
                          enabled: !owner.busy, disclosure: true, action: { [weak self] in
                              self?.orderMenu(group: .ruleOrder, index: index, title: dimension.title)
                          })
                } + [restoreRow("恢复默认顺序", group: .ruleOrder)]),
                .init(title: "网盘匹配规则", rows: draft.panOrder.enumerated().map { index, provider in
                    .init(title: provider.title, detail: "\(index + 1)", id: "film.preferences.pan.\(provider.rawValue)",
                          enabled: !owner.busy, disclosure: true, action: { [weak self] in
                              self?.orderMenu(group: .panOrder, index: index, title: provider.title)
                          })
                } + [restoreRow("恢复默认网盘", group: .panOrder)]),
                .init(title: "关键字匹配规则", rows: keywordRows(draft.keywords, group: .keywords)),
            ]
        case .siteClean:
            sections = [.init(title: "关键字匹配规则", rows: keywordRows(draft.siteCleanKeywords, group: .siteClean))]
        case .autoPrepare:
            sections = [.init(title: "", rows: [
                filmSwitchRow("进入播放页自动准备", id: "film.preferences.auto-prepare",
                              isOn: draft.iosAutoPrepare, enabled: !owner.busy, ink: UIColor(theme.ink)) { [weak owner] value in
                    owner?.edit { $0.iosAutoPrepare = value }
                },
                restoreRow("恢复默认", group: .autoPrepare),
            ])]
        }
        render()
    }

    private func restoreRow(_ title: String, group: FilmPlaybackSettingsModel.Group) -> FilmSettingsRow {
        .init(title: title, id: "film.preferences.restore.\(group)", enabled: !owner.busy,
              action: { [weak self] in
                  self?.view.endEditing(true)
                  self?.owner.restoreDraft(group)
              })
    }

    private func keywordRows(_ values: [String], group: FilmPlaybackSettingsModel.Group) -> [FilmSettingsRow] {
        keywords.text = values.joined(separator: ",")
        keywords.isEnabled = !owner.busy; keywords.textColor = UIColor(theme.ink)
        return [.init(title: "关键字匹配规则", id: "film.preferences.keywords.row", content: keywords),
                restoreRow("恢复默认关键词", group: group)]
    }

    private func orderMenu(group: FilmPlaybackSettingsModel.Group, index: Int, title: String) {
        view.endEditing(true)
        let count: Int
        switch group {
        case .ruleOrder: count = owner.draft?.ruleOrder.count ?? 0
        case .panOrder: count = owner.draft?.panOrder.count ?? 0
        case .keywords: count = owner.draft?.keywords.count ?? 0
        case .siteClean: count = owner.draft?.siteCleanKeywords.count ?? 0
        case .autoPrepare: return
        }
        let alert = UIAlertController(title: title, message: nil, preferredStyle: .actionSheet)
        if index > 0 { alert.addAction(UIAlertAction(title: "上移", style: .default) { [weak self] _ in
            self?.move(group, from: index, to: index - 1)
        }) }
        if index + 1 < count { alert.addAction(UIAlertAction(title: "下移", style: .default) { [weak self] _ in
            self?.move(group, from: index, to: index + 1)
        }) }
        alert.addAction(UIAlertAction(title: "取消", style: .cancel))
        presentSheet(alert)
    }

    private func move(_ group: FilmPlaybackSettingsModel.Group, from: Int, to: Int) {
        owner.edit {
            switch group {
            case .ruleOrder: $0.ruleOrder.swapAt(from, to)
            case .panOrder: $0.panOrder.swapAt(from, to)
            case .keywords: $0.keywords.swapAt(from, to)
            case .siteClean: $0.siteCleanKeywords.swapAt(from, to)
            case .autoPrepare: break
            }
        }
    }

    private func presentSheet(_ alert: UIAlertController) {
        guard canPresentPageAlert else { return }
        alert.popoverPresentationController?.sourceView = table
        alert.popoverPresentationController?.sourceRect = CGRect(x: table.bounds.midX, y: table.bounds.midY, width: 1, height: 1)
        present(alert, animated: true)
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
#endif
