#if os(iOS)
import UIKit
import MeowUI

/// Pure UIKit film playback preferences (`preferences`, `siteClean`, `autoPrepare`) built on
/// `SettingsListViewController` with native table reordering and zero SwiftUI hosting.
@MainActor
final class FilmPlaybackPreferencesController: FilmSettingsListController {
    enum Area { case preferences, siteClean, autoPrepare }
    private let owner: FilmPlaybackSettingsModel
    let area: Area
    private var loading: Task<Void, Never>?
    private var saving: Task<Void, Never>?
    private let keywords: SettingsTextField = {
        let field = filmTextField("关键字匹配规则", id: "film.preferences.keywords") as! SettingsTextField
        field.inputAppearance = .underline
        return field
    }()
    private lazy var keywordSection: SettingsInputSection = {
        let section = SettingsInputSection(title: "关键字匹配规则", input: keywords)
        section.titleLabel.setContentHuggingPriority(.defaultHigh, for: .vertical)
        section.isLayoutMarginsRelativeArrangement = true
        section.layoutMargins = .zero
        return section
    }()

    override var avoidsKeyboard: Bool { true }
    override var allowsRowReordering: Bool { area == .preferences }

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
        table.isEditing = (area == .preferences)
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
        observe(owner, whenVisible: true) { [weak self] in
            guard let self else { return }
            if !self.keywords.isFirstResponder { self.update() }
        }
        load()
    }

    override func pageDidBecomeInactive() {
        loading?.cancel(); loading = nil
        super.pageDidBecomeInactive()
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
            sections = [.init(id: "state", title: "", rows: [
                .init(title: owner.preferencesBusy ? "加载中" : "暂无设置", id: "film.preferences.state")
            ])]
            render()
            return
        }
        let keywordValue = (area == .siteClean ? draft.siteCleanKeywords : draft.keywords).joined(separator: ",")
        if !keywords.isFirstResponder, keywords.text != keywordValue {
            keywords.text = keywordValue
        }
        keywords.isEnabled = !owner.preferencesBusy
        keywordSection.applyTheme(theme)

        switch area {
        case .preferences:
            sections = [
                FilmSettingsSection(
                    id: "dimensions",
                    title: "优先片源规则",
                    rows: draft.ruleOrder.map { item in
                        FilmSettingsRow(
                            title: item.title,
                            id: "film.preferences.dimension.\(item.rawValue)",
                            enabled: !owner.preferencesBusy,
                            canMove: !owner.preferencesBusy
                        )
                    }
                ),
                FilmSettingsSection(
                    id: "pans",
                    title: "网盘匹配规则",
                    rows: draft.panOrder.map { item in
                        FilmSettingsRow(
                            title: item.title,
                            id: "film.preferences.pan.\(item.rawValue)",
                            enabled: !owner.preferencesBusy,
                            canMove: !owner.preferencesBusy
                        )
                    }
                ),
                FilmSettingsSection(
                    id: "keywords",
                    title: "关键字匹配规则",
                    rows: [
                        FilmSettingsRow(
                            title: "",
                            id: "film.preferences.keywords.row",
                            enabled: !owner.preferencesBusy,
                            content: keywordSection
                        )
                    ]
                ),
            ]
        case .siteClean:
            sections = [
                FilmSettingsSection(
                    id: "keywords",
                    title: "关键字匹配规则",
                    rows: [
                        FilmSettingsRow(
                            title: "",
                            id: "film.preferences.keywords.row",
                            enabled: !owner.preferencesBusy,
                            content: keywordSection
                        ),
                        restoreRow("恢复默认关键词", group: .siteClean),
                    ]
                )
            ]
        case .autoPrepare:
            sections = [
                FilmSettingsSection(
                    id: "autoPrepare",
                    title: "",
                    rows: [
                        filmSwitchRow("进入播放页自动准备", id: "film.preferences.auto-prepare",
                                      isOn: draft.iosAutoPrepare, enabled: !owner.preferencesBusy) { [weak owner] value in
                            owner?.edit { $0.iosAutoPrepare = value }
                        },
                        restoreRow("恢复默认", group: .autoPrepare),
                    ]
                )
            ]
        }
        render()
    }

    override func didMoveRow(from sourceIndexPath: IndexPath, to destinationIndexPath: IndexPath) {
        guard area == .preferences, sourceIndexPath.section == destinationIndexPath.section,
              let draft = owner.draft else { return }
        if sourceIndexPath.section == 0 {
            var order = draft.ruleOrder
            guard order.indices.contains(sourceIndexPath.row), order.indices.contains(destinationIndexPath.row) else { return }
            let moved = order.remove(at: sourceIndexPath.row)
            order.insert(moved, at: destinationIndexPath.row)
            owner.edit { $0.ruleOrder = order }
        } else if sourceIndexPath.section == 1 {
            var order = draft.panOrder
            guard order.indices.contains(sourceIndexPath.row), order.indices.contains(destinationIndexPath.row) else { return }
            let moved = order.remove(at: sourceIndexPath.row)
            order.insert(moved, at: destinationIndexPath.row)
            owner.edit { $0.panOrder = order }
        }
    }

    private func restoreRow(_ title: String, group: FilmPlaybackSettingsModel.Group) -> FilmSettingsRow {
        .init(title: title, id: "film.preferences.restore.\(group)", enabled: !owner.preferencesBusy,
              action: { [weak self] in
                  self?.view.endEditing(true)
                  self?.owner.restoreDraft(group)
              })
    }

    private func save() {
        guard saving == nil else { return }
        view.endEditing(true)
        let owner = self.owner
        saving = Task { [weak self] in
            defer { self?.saving = nil }
            do { try await owner.savePreferences(); self?.notice("已保存") }
            catch { self?.failure(error, retry: { [weak self] in self?.save() }) }
        }
    }
}
#endif
