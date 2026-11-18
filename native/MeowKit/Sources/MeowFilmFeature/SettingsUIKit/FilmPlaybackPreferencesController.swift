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
    private var preferencesContent: UIHostingController<FilmPreferencesContent>?
    private var keywordSize = CGSize.zero, keywordViewportSize = CGSize.zero
    private var needsKeywordReveal = false
    override var avoidsKeyboard: Bool { true }
    override var usesSettingsTable: Bool { area == .autoPrepare }

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
        keywords.addAction(UIAction { [weak self] _ in
            self?.needsKeywordReveal = true; self?.view.setNeedsLayout()
        }, for: .editingDidBegin)
        if area != .autoPrepare {
            let content = UIHostingController(rootView: FilmPreferencesContent(owner: owner, keywordSection: keywordSection,
                siteClean: area == .siteClean, theme: theme, contentSizeCategory: traitCollection.preferredContentSizeCategory))
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
            registerForTraitChanges([UITraitPreferredContentSizeCategory.self]) {
                (page: FilmPlaybackPreferencesController, _: UITraitCollection) in page.applyTheme()
            }
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
        if area != .autoPrepare {
            if !keywords.isFirstResponder {
                keywords.text = (area == .siteClean ? owner.draft?.siteCleanKeywords : owner.draft?.keywords)?.joined(separator: ",")
            }
            return
        }
        guard let draft = owner.draft else {
            sections = [.init(title: "", rows: [.init(title: owner.preferencesBusy ? "加载中" : "暂无设置",
                                                      id: "film.preferences.state")])]
            render(); return
        }
        switch area {
        case .preferences, .siteClean:
            return
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

    override func applyTheme() {
        super.applyTheme()
        keywordSection.applyTheme(theme)
        preferencesContent?.rootView = FilmPreferencesContent(owner: owner, keywordSection: keywordSection,
            siteClean: area == .siteClean, theme: theme, contentSizeCategory: traitCollection.preferredContentSizeCategory)
        needsKeywordReveal = true; viewIfLoaded?.setNeedsLayout()
    }

    override func viewDidLayoutSubviews() {
        super.viewDidLayoutSubviews()
        preferencesContent?.view.layoutIfNeeded()
        guard let scroll = sequence(first: keywordSection as UIView, next: { $0.superview })
            .compactMap({ $0 as? UIScrollView }).first else { return }
        let changed = keywordSize != keywordSection.bounds.size || keywordViewportSize != scroll.bounds.size
        keywordSize = keywordSection.bounds.size; keywordViewportSize = scroll.bounds.size
        guard needsKeywordReveal || changed else { return }
        needsKeywordReveal = false
        if keywords.isFirstResponder {
            scroll.scrollRectToVisible(keywords.convert(keywords.bounds, to: scroll).insetBy(dx: 0, dy: -8), animated: false)
        }
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
    let keywordSection: SettingsInputSection
    let siteClean: Bool
    let theme: PageTheme
    let contentSizeCategory: UIContentSizeCategory
    private struct Item: Identifiable { let id, title: String }

    var body: some View {
        List {
            if let draft = owner.draft {
                if !siteClean {
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
                }
                Section {
                    KeywordsInput(section: keywordSection, value: (siteClean ? draft.siteCleanKeywords : draft.keywords).joined(separator: ","),
                                  enabled: !owner.preferencesBusy, theme: theme, contentSizeCategory: contentSizeCategory)
                        .modifier(SettingsListRowStyle(theme: theme, first: true, last: !siteClean))
                    if siteClean {
                        Button("恢复默认关键词") {
                            keywordSection.endEditing(true)
                            owner.restoreDraft(.siteClean)
                        }
                        .disabled(owner.preferencesBusy)
                        .accessibilityIdentifier("film.preferences.restore.siteClean")
                        .modifier(SettingsListRowStyle(theme: theme, first: false, last: true))
                    }
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
        let section: SettingsInputSection
        let value: String
        let enabled: Bool
        let theme: PageTheme
        let contentSizeCategory: UIContentSizeCategory
        func makeUIView(context: Context) -> SettingsInputSection { section }
        func updateUIView(_ view: SettingsInputSection, context: Context) {
            let field = view.input as! UITextField
            if !field.isFirstResponder { field.text = value }
            field.isEnabled = enabled; view.applyTheme(theme)
            applyFonts(to: view)
        }
        func sizeThatFits(_ proposal: ProposedViewSize, uiView: SettingsInputSection, context: Context) -> CGSize? {
            guard let width = proposal.width, width.isFinite else { return nil }
            applyFonts(to: uiView)
            let title = uiView.titleLabel
            let titleSize = title.sizeThatFits(CGSize(width: width, height: .greatestFiniteMagnitude))
            return CGSize(width: width,
                height: titleSize.height + uiView.spacing + uiView.input.intrinsicContentSize.height + uiView.layoutMargins.bottom)
        }
        private func applyFonts(to view: SettingsInputSection) {
            let traits = UITraitCollection(preferredContentSizeCategory: contentSizeCategory)
            let titleFont = UIFont.preferredFont(forTextStyle: .subheadline, compatibleWith: traits)
            if view.titleLabel.font != titleFont { view.titleLabel.font = titleFont }
            let field = view.input as! UITextField
            let inputFont = UIFont.preferredFont(forTextStyle: .body, compatibleWith: traits)
            if field.font != inputFont { field.font = inputFont }
            let hitInset = max(0, (44 - field.intrinsicContentSize.height) / 2)
            if view.layoutMargins.bottom != hitInset {
                view.layoutMargins = .init(top: 0, left: 0, bottom: hitInset, right: 0)
            }
        }
    }
}
#endif
