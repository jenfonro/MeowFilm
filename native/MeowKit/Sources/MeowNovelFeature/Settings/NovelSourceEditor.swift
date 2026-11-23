#if os(iOS)
import UIKit
import MeowUI
import MeowRuntime

@MainActor
final class NovelSourceEditor: ManagementPageViewController {
    private let feature: NovelFeature
    private var source: NovelSource?
    private var draft: NovelSourceDraft
    private let form = ManagementFormView(), tabs = UIStackView(), controls = UIStackView()
    private let options = UIScrollView(), tabScroll = UIScrollView()
    private lazy var optionsHeight = options.heightAnchor.constraint(equalToConstant: 64)
    private lazy var tabsHeight = tabScroll.heightAnchor.constraint(equalToConstant: 46)
    private let panel = UIView()
    private let typeLabel = UILabel(), typeButton = UIButton(type: .system)
    private var optionChecks: [UIButton] = []
    private var tabButtons: [UIButton] = [], indicators: [UIView] = [], separators: [UIView] = []
    private let saveButton = PageHeaderButton(image: ManagementIcon.save, accessibilityLabel: "保存") {}
    private var activeTab = 0
    init(source: NovelSource?, protocol: NovelSourceProtocol, definition: String?, feature: NovelFeature, themeProvider: any PageThemeProviding) throws {
        self.source = source; self.feature = feature; draft = try NovelSourceDraft(protocol: `protocol`, definition: definition)
        super.init(themeProvider: themeProvider); pageTitle = "编辑\(`protocol`.title)"
    }
    override func viewDidLoad() {
        super.viewDidLoad(); view.accessibilityIdentifier = "novel.source.editor"
        saveButton.addAction(UIAction { [weak self] _ in self?.save() }, for: .touchUpInside); saveButton.accessibilityIdentifier = "novel.source.save"
        let more = PageHeaderButton(image: ManagementIcon.more, accessibilityLabel: "更多", menu: UIMenu(children: [
            UIAction(title: "复制书源") { [weak self] _ in self?.copy() }, UIAction(title: "导出书源") { [weak self] _ in self?.export() }]))
        setManagementActions([saveButton, more])
        controls.alignment = .center; controls.spacing = 10
        typeLabel.text = "类型"; typeLabel.adjustsFontForContentSizeCategory = true; controls.addArrangedSubview(typeLabel)
        let names = ["文本", "音频", "图片", "文件"]
        let current = Int(draft.text(.init(path: "bookSourceType", label: ""))) ?? 0
        var style = UIButton.Configuration.plain(); style.title = names.indices.contains(current) ? names[current] : "文本"; style.image = UIImage(systemName: "chevron.up.chevron.down", withConfiguration: UIImage.SymbolConfiguration(pointSize: 9)); style.imagePlacement = .trailing; style.imagePadding = 8; style.background.cornerRadius = 10; style.contentInsets = .init(top: 5, leading: 10, bottom: 5, trailing: 10)
        typeButton.configuration = style; typeButton.titleLabel?.adjustsFontForContentSizeCategory = true; typeButton.accessibilityLabel = "内容类型"; typeButton.showsMenuAsPrimaryAction = true
        for item in [typeLabel, typeButton] {
            item.setContentCompressionResistancePriority(.required, for: .horizontal)
            item.setContentCompressionResistancePriority(.required, for: .vertical)
        }
        typeButton.menu = UIMenu(children: names.enumerated().map { index, title in UIAction(title: title) { [weak self] _ in
            self?.draft.set(.init(path: "bookSourceType", label: "内容类型", kind: .number), String(index))
            self?.typeButton.configuration?.title = title
        } })
        controls.addArrangedSubview(typeButton)
        for (key, label) in [("enabled", "启用"), ("enabledExplore", "发现"), ("enabledCookieJar", "CookieJar")] {
            let check = ManagementFormView.check(label, value: draft.bool(key)) { [weak self] value in self?.draft.setBool(key, value) }
            optionChecks.append(check)
            controls.addArrangedSubview(check)
        }
        options.showsHorizontalScrollIndicator = false; options.addSubview(controls); controls.translatesAutoresizingMaskIntoConstraints = false
        NSLayoutConstraint.activate([controls.leadingAnchor.constraint(equalTo: options.contentLayoutGuide.leadingAnchor, constant: 16), controls.trailingAnchor.constraint(equalTo: options.contentLayoutGuide.trailingAnchor, constant: -16), controls.topAnchor.constraint(equalTo: options.contentLayoutGuide.topAnchor, constant: 12), controls.bottomAnchor.constraint(equalTo: options.contentLayoutGuide.bottomAnchor, constant: -12), controls.heightAnchor.constraint(equalTo: options.frameLayoutGuide.heightAnchor, constant: -24)])
        tabScroll.showsHorizontalScrollIndicator = false; tabScroll.addSubview(tabs); tabs.translatesAutoresizingMaskIntoConstraints = false; tabs.spacing = 6
        NSLayoutConstraint.activate([tabs.leadingAnchor.constraint(equalTo: tabScroll.contentLayoutGuide.leadingAnchor, constant: 12), tabs.trailingAnchor.constraint(equalTo: tabScroll.contentLayoutGuide.trailingAnchor, constant: -12), tabs.topAnchor.constraint(equalTo: tabScroll.contentLayoutGuide.topAnchor), tabs.bottomAnchor.constraint(equalTo: tabScroll.contentLayoutGuide.bottomAnchor), tabs.heightAnchor.constraint(equalTo: tabScroll.frameLayoutGuide.heightAnchor)])
        let titles = draft.tabs.map(\.title) + (draft.extensionFields.isEmpty ? [] : ["扩展字段"])
        for (index, title) in titles.enumerated() {
            let button = UIButton(type: .system)
            var tabConfig = UIButton.Configuration.plain()
            tabConfig.title = title
            tabConfig.contentInsets = .init(top: 12, leading: 14, bottom: 12, trailing: 14)
            button.configuration = tabConfig
            button.titleLabel?.adjustsFontForContentSizeCategory = true
            button.setContentCompressionResistancePriority(.required, for: .horizontal); button.setContentCompressionResistancePriority(.required, for: .vertical)
            button.widthAnchor.constraint(greaterThanOrEqualToConstant: 64).isActive = true; button.accessibilityIdentifier = "novel.source.tab.\(index)"
            let indicator = UIView(); indicator.layer.cornerRadius = 1.5; button.addSubview(indicator); indicator.translatesAutoresizingMaskIntoConstraints = false
            NSLayoutConstraint.activate([indicator.leadingAnchor.constraint(equalTo: button.leadingAnchor, constant: 14), indicator.trailingAnchor.constraint(equalTo: button.trailingAnchor, constant: -14), indicator.bottomAnchor.constraint(equalTo: button.bottomAnchor), indicator.heightAnchor.constraint(equalToConstant: 3)]); indicators.append(indicator)
            button.addAction(UIAction { [weak self] _ in self?.view.endEditing(true); self?.activeTab = index; self?.render() }, for: .touchUpInside)
            tabButtons.append(button); tabs.addArrangedSubview(button)
        }
        view.addSubview(panel); panel.translatesAutoresizingMaskIntoConstraints = false; panel.accessibilityIdentifier = "novel.source.panel"; panel.layer.cornerRadius = 22; panel.clipsToBounds = true
        for child in [options, tabScroll, form] { panel.addSubview(child); child.translatesAutoresizingMaskIntoConstraints = false }
        for anchor in [options.bottomAnchor, tabScroll.bottomAnchor] {
            let line = UIView(); panel.addSubview(line); line.translatesAutoresizingMaskIntoConstraints = false; separators.append(line)
            NSLayoutConstraint.activate([line.leadingAnchor.constraint(equalTo: panel.leadingAnchor), line.trailingAnchor.constraint(equalTo: panel.trailingAnchor), line.bottomAnchor.constraint(equalTo: anchor), line.heightAnchor.constraint(equalToConstant: 0.5)])
        }
        NSLayoutConstraint.activate([panel.topAnchor.constraint(equalTo: contentTopAnchor, constant: SettingsVisualMetrics.contentTopSpacing), panel.leadingAnchor.constraint(equalTo: view.safeAreaLayoutGuide.leadingAnchor, constant: 24), panel.trailingAnchor.constraint(equalTo: view.safeAreaLayoutGuide.trailingAnchor, constant: -24), panel.bottomAnchor.constraint(equalTo: view.keyboardLayoutGuide.topAnchor, constant: -8),
            options.topAnchor.constraint(equalTo: panel.topAnchor), options.leadingAnchor.constraint(equalTo: panel.leadingAnchor), options.trailingAnchor.constraint(equalTo: panel.trailingAnchor), optionsHeight,
            tabScroll.topAnchor.constraint(equalTo: options.bottomAnchor), tabScroll.leadingAnchor.constraint(equalTo: options.leadingAnchor), tabScroll.trailingAnchor.constraint(equalTo: options.trailingAnchor), tabsHeight,
            form.topAnchor.constraint(equalTo: tabScroll.bottomAnchor), form.leadingAnchor.constraint(equalTo: panel.leadingAnchor), form.trailingAnchor.constraint(equalTo: panel.trailingAnchor), form.bottomAnchor.constraint(equalTo: panel.bottomAnchor)])
        registerForTraitChanges([UITraitPreferredContentSizeCategory.self]) { (editor: NovelSourceEditor, _: UITraitCollection) in editor.updateFonts() }
        updateFonts(); render(); applyTheme()
    }
    override func viewIsAppearing(_ animated: Bool) {
        super.viewIsAppearing(animated); updateFonts()
    }
    private func updateFonts() {
        let font = UIFontMetrics.default.scaledFont(for: .systemFont(ofSize: 13), compatibleWith: traitCollection)
        typeLabel.font = font
        typeButton.configuration?.titleTextAttributesTransformer = UIConfigurationTextAttributesTransformer { attributes in
            var attributes = attributes; attributes.font = font; return attributes
        }
        let tabFont = UIFontMetrics.default.scaledFont(for: .systemFont(ofSize: 14), compatibleWith: traitCollection)
        for button in tabButtons {
            button.configuration?.titleTextAttributesTransformer = UIConfigurationTextAttributesTransformer { attributes in
                var attributes = attributes; attributes.font = tabFont; return attributes
            }
        }
        optionsHeight.constant = max(64, ceil(font.lineHeight) + 10 + 24)
        tabsHeight.constant = max(46, ceil(tabFont.lineHeight) + 24)
    }
    private func updateTabAppearance() {
        let settings = theme.settings
        for (index, button) in tabButtons.enumerated() {
            button.configuration?.baseForegroundColor = UIColor(index == activeTab ? settings.ink : settings.muted)
            indicators[index].backgroundColor = UIColor(settings.accent)
            indicators[index].isHidden = index != activeTab
        }
    }
    override func applyTheme() {
        super.applyTheme()
        guard isViewLoaded else { return }
        let settings = theme.settings
        panel.backgroundColor = UIColor(settings.surface); typeLabel.textColor = UIColor(settings.muted)
        typeButton.configuration?.baseForegroundColor = UIColor(settings.ink)
        typeButton.configuration?.background.backgroundColor = UIColor(settings.input)
        for line in separators { line.backgroundColor = UIColor(settings.separator) }
        for check in optionChecks { (check as? any PageThemeable)?.applyTheme(theme) }
        updateTabAppearance()
        form.applyTheme(theme)
    }
    private func render() {
        form.clear()
        updateTabAppearance()
        let fields = activeTab < draft.tabs.count ? draft.tabs[activeTab].fields : draft.extensionFields
        for field in fields {
            if field.kind == .bool {
                let button = ManagementFormView.check(field.label, value: draft.text(field) == "true") { [weak self] value in self?.draft.set(field, value ? "true" : "false") }
                form.addSection(button)
            } else { form.text(field.label, value: draft.text(field), rows: field.rows, id: "novel.source.field.\(field.path)") { [weak self] value in self?.draft.set(field, value) } }
        }
        form.setContentOffset(.zero, animated: false)
        form.applyTheme(theme)
    }
    private func save() {
        guard !hasActivePageTask(key: "save") else { return }; view.endEditing(true)
        do {
            let definition = try draft.definition(); saveButton.isEnabled = false
            runPageTask(key: "save", onFinally: { [weak self] in
                self?.saveButton.isEnabled = true
            }) { [weak self] in
                guard let self else { return }
                if let source { self.source = try await feature.sourcesClient.save(source, definition: definition) }
                else {
                    let receipt = try await feature.sourcesClient.importSources(text: definition)
                    guard receipt.failures.isEmpty, let item = receipt.sources.first else { throw NovelError.importFailures(receipt.failures.map { $0.code }) }
                    source = item
                }
                try await feature.refreshSources(); source = feature.sources.first { $0.id == self.source?.id }
                enqueueNotice(title: "书源已保存")
            }
        } catch { enqueueError(error.localizedDescription) }
    }
    private func copy() { do { UIPasteboard.general.string = try draft.definition() } catch { enqueueError(error.localizedDescription) } }
    private func export() {
        do {
            try presentNovelExport(draft.definition(), name: "book-source.json")
        } catch { enqueueError(error.localizedDescription) }
    }
}
#endif
