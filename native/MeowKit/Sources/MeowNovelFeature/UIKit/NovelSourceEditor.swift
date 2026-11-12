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
    private let panel = UIView()
    private var indicators: [UIView] = []
    private let saveButton = PageHeaderButton(image: ManagementIcon.save, accessibilityLabel: "保存") {}
    private var activeTab = 0, saving = false
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
        let label = UILabel(); label.text = "类型"; label.textColor = .secondaryLabel; label.font = .systemFont(ofSize: 13); controls.addArrangedSubview(label)
        let type = UIButton(type: .system); let names = ["文本", "音频", "图片", "文件"]
        let current = Int(draft.text(.init(path: "bookSourceType", label: ""))) ?? 0
        var style = UIButton.Configuration.gray(); style.title = names.indices.contains(current) ? names[current] : "文本"; style.image = UIImage(systemName: "chevron.up.chevron.down", withConfiguration: UIImage.SymbolConfiguration(pointSize: 9)); style.imagePlacement = .trailing; style.imagePadding = 8; style.baseForegroundColor = .label; style.background.cornerRadius = 10; style.contentInsets = .init(top: 5, leading: 10, bottom: 5, trailing: 10)
        type.configuration = style; type.titleLabel?.font = .systemFont(ofSize: 13); type.accessibilityLabel = "内容类型"; type.showsMenuAsPrimaryAction = true
        type.menu = UIMenu(children: names.enumerated().map { index, title in UIAction(title: title) { [weak self, weak type] _ in
            self?.draft.set(.init(path: "bookSourceType", label: "内容类型", kind: .number), String(index))
            type?.configuration?.title = title
        } })
        controls.addArrangedSubview(type)
        for (key, label) in [(draft.enabledKey, "启用"), ("enabledExplore", "发现"), ("enabledCookieJar", "CookieJar")] {
            controls.addArrangedSubview(ManagementFormView.check(label, value: draft.bool(key)) { [weak self] value in self?.draft.setBool(key, value) })
        }
        let options = UIScrollView(); options.showsHorizontalScrollIndicator = false; options.addSubview(controls); controls.translatesAutoresizingMaskIntoConstraints = false
        NSLayoutConstraint.activate([controls.leadingAnchor.constraint(equalTo: options.contentLayoutGuide.leadingAnchor, constant: 16), controls.trailingAnchor.constraint(equalTo: options.contentLayoutGuide.trailingAnchor, constant: -16), controls.topAnchor.constraint(equalTo: options.contentLayoutGuide.topAnchor), controls.bottomAnchor.constraint(equalTo: options.contentLayoutGuide.bottomAnchor), controls.heightAnchor.constraint(equalTo: options.frameLayoutGuide.heightAnchor)])
        let tabScroll = UIScrollView(); tabScroll.showsHorizontalScrollIndicator = false; tabScroll.addSubview(tabs); tabs.translatesAutoresizingMaskIntoConstraints = false; tabs.spacing = 6
        NSLayoutConstraint.activate([tabs.leadingAnchor.constraint(equalTo: tabScroll.contentLayoutGuide.leadingAnchor, constant: 12), tabs.trailingAnchor.constraint(equalTo: tabScroll.contentLayoutGuide.trailingAnchor, constant: -12), tabs.topAnchor.constraint(equalTo: tabScroll.contentLayoutGuide.topAnchor), tabs.bottomAnchor.constraint(equalTo: tabScroll.contentLayoutGuide.bottomAnchor), tabs.heightAnchor.constraint(equalTo: tabScroll.frameLayoutGuide.heightAnchor)])
        let titles = draft.tabs.map(\.title) + (draft.extensionFields.isEmpty ? [] : ["扩展字段"])
        for (index, title) in titles.enumerated() {
            let button = UIButton(type: .system); button.setTitle(title, for: .normal); button.titleLabel?.font = .systemFont(ofSize: 14)
            button.widthAnchor.constraint(greaterThanOrEqualToConstant: 64).isActive = true; button.accessibilityIdentifier = "novel.source.tab.\(index)"
            let indicator = UIView(); indicator.backgroundColor = .systemBlue; indicator.layer.cornerRadius = 1.5; button.addSubview(indicator); indicator.translatesAutoresizingMaskIntoConstraints = false
            NSLayoutConstraint.activate([indicator.leadingAnchor.constraint(equalTo: button.leadingAnchor, constant: 14), indicator.trailingAnchor.constraint(equalTo: button.trailingAnchor, constant: -14), indicator.bottomAnchor.constraint(equalTo: button.bottomAnchor), indicator.heightAnchor.constraint(equalToConstant: 3)]); indicators.append(indicator)
            button.addAction(UIAction { [weak self] _ in self?.view.endEditing(true); self?.activeTab = index; self?.render() }, for: .touchUpInside); tabs.addArrangedSubview(button)
        }
        view.addSubview(panel); panel.translatesAutoresizingMaskIntoConstraints = false; panel.backgroundColor = .secondarySystemGroupedBackground; panel.layer.cornerRadius = 22; panel.clipsToBounds = true
        for child in [options, tabScroll, form] { panel.addSubview(child); child.translatesAutoresizingMaskIntoConstraints = false }
        for anchor in [options.bottomAnchor, tabScroll.bottomAnchor] {
            let line = UIView(); line.backgroundColor = .separator; panel.addSubview(line); line.translatesAutoresizingMaskIntoConstraints = false
            NSLayoutConstraint.activate([line.leadingAnchor.constraint(equalTo: panel.leadingAnchor), line.trailingAnchor.constraint(equalTo: panel.trailingAnchor), line.bottomAnchor.constraint(equalTo: anchor), line.heightAnchor.constraint(equalToConstant: 0.5)])
        }
        NSLayoutConstraint.activate([panel.topAnchor.constraint(equalTo: contentTopAnchor, constant: 12), panel.leadingAnchor.constraint(equalTo: view.safeAreaLayoutGuide.leadingAnchor, constant: 24), panel.trailingAnchor.constraint(equalTo: view.safeAreaLayoutGuide.trailingAnchor, constant: -24), panel.bottomAnchor.constraint(equalTo: view.keyboardLayoutGuide.topAnchor, constant: -8),
            options.topAnchor.constraint(equalTo: panel.topAnchor), options.leadingAnchor.constraint(equalTo: panel.leadingAnchor), options.trailingAnchor.constraint(equalTo: panel.trailingAnchor), options.heightAnchor.constraint(equalToConstant: 64),
            tabScroll.topAnchor.constraint(equalTo: options.bottomAnchor), tabScroll.leadingAnchor.constraint(equalTo: options.leadingAnchor), tabScroll.trailingAnchor.constraint(equalTo: options.trailingAnchor), tabScroll.heightAnchor.constraint(equalToConstant: 46),
            form.topAnchor.constraint(equalTo: tabScroll.bottomAnchor), form.leadingAnchor.constraint(equalTo: panel.leadingAnchor), form.trailingAnchor.constraint(equalTo: panel.trailingAnchor), form.bottomAnchor.constraint(equalTo: panel.bottomAnchor)])
        render()
    }
    override func applyTheme() { super.applyTheme(); if isViewLoaded { form.applyTheme(theme) } }
    private func render() {
        form.clear()
        for (index, view) in tabs.arrangedSubviews.enumerated() { (view as? UIButton)?.setTitleColor(index == activeTab ? .label : .secondaryLabel, for: .normal); indicators[index].isHidden = index != activeTab }
        let fields = activeTab < draft.tabs.count ? draft.tabs[activeTab].fields : draft.extensionFields
        for field in fields {
            if field.kind == .bool {
                let button = ManagementFormView.check(field.label, value: draft.text(field) == "true") { [weak self] value in self?.draft.set(field, value ? "true" : "false") }
                form.addSection(button)
            } else { form.text(field.label, value: draft.text(field), rows: field.rows, id: "novel.source.field.\(field.path)") { [weak self] value in self?.draft.set(field, value) } }
        }
        form.setContentOffset(.zero, animated: false); form.applyTheme(theme)
    }
    private func save() {
        guard !saving else { return }; view.endEditing(true)
        do {
            let definition = try draft.definition(); saving = true; saveButton.isEnabled = false
            runPageTask { [weak self] in
                guard let self else { return }; defer { saving = false; saveButton.isEnabled = true }
                do {
                    if let source { self.source = try await feature.sourcesClient.save(source, definition: definition) }
                    else {
                        let receipt = try await feature.sourcesClient.importSources(text: definition)
                        guard receipt.failures.isEmpty, let item = receipt.sources.first else { throw NovelError.importFailures(receipt.failures.map { $0.code }) }
                        source = item
                    }
                    try await feature.refreshSources(); source = feature.sources.first { $0.id == self.source?.id }
                    if canPresentPageAlert {
                        let notice = UIAlertController(title: "书源已保存", message: nil, preferredStyle: .alert)
                        notice.addAction(UIAlertAction(title: "确定", style: .default))
                        present(notice, animated: true)
                    }
                } catch is CancellationError { } catch { if !Task.isCancelled { showError(error.localizedDescription) } }
            }
        } catch { showError(error.localizedDescription) }
    }
    private func copy() { do { UIPasteboard.general.string = try draft.definition() } catch { showError(error.localizedDescription) } }
    private func export() {
        do {
            let directory = FileManager.default.temporaryDirectory.appendingPathComponent("meowfilm-novel-export", isDirectory: true)
            try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
            let url = directory.appendingPathComponent("book-source.json"); try Data(draft.definition().utf8).write(to: url, options: .atomic)
            present(UIDocumentPickerViewController(forExporting: [url], asCopy: true), animated: true)
        } catch { showError(error.localizedDescription) }
    }
}
#endif
