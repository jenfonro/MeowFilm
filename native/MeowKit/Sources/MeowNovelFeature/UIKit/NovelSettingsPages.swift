#if os(iOS)
import UIKit
import UniformTypeIdentifiers
import PhotosUI
import MeowUI
import MeowRuntime

@MainActor
public final class NovelSettingsViewController: NovelTablePage {
    public let feature: NovelFeature
    public init(feature: NovelFeature, themeProvider: any PageThemeProviding) { self.feature = feature; super.init(themeProvider: themeProvider); pageTitle = "小说设置" }
    public override func viewDidLoad() {
        super.viewDidLoad(); view.accessibilityIdentifier = "novel.settings"
        rows = ["书源管理", "替换净化", "阅读排版"].enumerated().map { index, title in
            .init(title: title, id: "novel.settings.\(index)") { [weak self] in
                guard let self else { return }
                let page: UIViewController
                switch index {
                case 0: page = NovelSourcesViewController(feature: feature, themeProvider: themeProvider)
                case 1: page = NovelRulesViewController(feature: feature, themeProvider: themeProvider)
                default: page = NovelAppearanceViewController(feature: feature, themeProvider: themeProvider)
                }
                navigationController?.pushViewController(page, animated: true)
            }
        }
    }
}

@MainActor
open class NovelTablePage: PageViewController, UITableViewDataSource, UITableViewDelegate {
    struct Row {
        var title: String
        var detail: String = ""
        var id: String = ""
        var destructive = false
        var action: () -> Void
    }
    var rows: [Row] = [] { didSet { if isViewLoaded { table.reloadData() } } }
    let table = SettingsTableView()
    open override func viewDidLoad() {
        super.viewDidLoad(); table.dataSource = self; table.delegate = self
        table.rowHeight = UITableView.automaticDimension; table.estimatedRowHeight = 54
        view.addSubview(table); table.translatesAutoresizingMaskIntoConstraints = false
        NSLayoutConstraint.activate([table.topAnchor.constraint(equalTo: contentTopAnchor), table.bottomAnchor.constraint(equalTo: view.bottomAnchor),
            table.leadingAnchor.constraint(equalTo: view.safeAreaLayoutGuide.leadingAnchor), table.trailingAnchor.constraint(equalTo: view.safeAreaLayoutGuide.trailingAnchor)])
    }
    open override func applyTheme() {
        super.applyTheme()
        if isViewLoaded {
            view.tintColor = UIColor(theme.settings.accent)
            table.separatorColor = UIColor(theme.settings.separator)
            table.reloadData()
        }
    }
    public func tableView(_ tableView: UITableView, numberOfRowsInSection section: Int) -> Int { rows.count }
    public func tableView(_ tableView: UITableView, cellForRowAt indexPath: IndexPath) -> UITableViewCell {
        let row = rows[indexPath.row]
        let isLast = indexPath.row == rows.count - 1
        let cell = SettingsTableCell(title: row.title, detail: row.detail.isEmpty ? nil : row.detail,
            theme: theme, first: indexPath.row == 0, last: isLast, destructive: row.destructive)
        cell.accessoryType = .disclosureIndicator
        cell.accessibilityIdentifier = row.id; return cell
    }
    public func tableView(_ tableView: UITableView, didSelectRowAt indexPath: IndexPath) { tableView.deselectRow(at: indexPath, animated: true); rows[indexPath.row].action() }
    func textPrompt(title: String, value: String, keyboard: UIKeyboardType = .default,
                    save: @escaping (String) throws -> Void) {
        let dialog = ManagementDialog(title: title, action: "保存", themeProvider: themeProvider)
        let input = ManagementDialog.input(placeholder: title)
        input.text = value; input.keyboardType = keyboard; input.autocapitalizationType = .none
        input.accessibilityLabel = title; input.accessibilityIdentifier = "novel.settings.input"
        dialog.body.addArrangedSubview(input)
        dialog.submit = { try save(input.text ?? ""); return true }
        present(dialog, animated: true)
    }
    func share(text: String, name: String) {
        do {
            let directory = FileManager.default.temporaryDirectory.appendingPathComponent("meowfilm-novel-export", isDirectory: true)
            try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
            let url = directory.appendingPathComponent(name); try Data(text.utf8).write(to: url, options: .atomic)
            let picker = UIDocumentPickerViewController(forExporting: [url], asCopy: true); present(picker, animated: true)
        } catch { showError(error.localizedDescription) }
    }
}

@MainActor
public final class NovelAppearanceViewController: NovelTablePage, PHPickerViewControllerDelegate {
    private let feature: NovelFeature
    private var draft: NovelReadingSettings
    public init(feature: NovelFeature, themeProvider: any PageThemeProviding) {
        self.feature = feature; draft = feature.local.settings; super.init(themeProvider: themeProvider); pageTitle = "阅读排版"
    }
    public override func viewDidLoad() { super.viewDidLoad(); view.accessibilityIdentifier = "novel.appearance"; render() }
    private func save() {
        let next = draft
        runPageTask { [weak self] in
            guard let self else { return }
            do { try await feature.saveSettings(next); draft = feature.local.settings; render() }
            catch { draft = feature.local.settings; render(); showError(error.localizedDescription) }
        }
    }
    private func numeric(_ title: String, value: Double, key: WritableKeyPath<NovelReadingSettings, Double>, valid: @escaping (Double) -> Bool) -> Row {
        .init(title: title, detail: String(format: "%g", value)) { [weak self] in
            self?.textPrompt(title: title, value: String(format: "%g", value), keyboard: .decimalPad) { [weak self] text in
                guard let self else { return }
                guard let value = Double(text), value.isFinite, valid(value) else {
                    throw NovelError.invalidRule(["数值无效"])
                }
                draft[keyPath: key] = value; save()
            }
        }
    }
    private func choices(_ title: String, items: [(String, () -> Void)]) {
        let sheet = UIAlertController(title: title, message: nil, preferredStyle: .actionSheet)
        for (name, action) in items { sheet.addAction(UIAlertAction(title: name, style: .default) { _ in action() }) }
        sheet.addAction(UIAlertAction(title: "取消", style: .cancel)); sheet.popoverPresentationController?.sourceView = view
        present(sheet, animated: true)
    }
    private func render() {
        rows = [
            .init(title: "阅读模式", detail: draft.mode.title) { [weak self] in
                guard let self else { return }; choices("阅读模式", items: NovelReadingMode.allCases.map { mode in (mode.title, { [weak self] in self?.draft.mode = mode; self?.save() }) })
            },
            numeric("字号", value: draft.fontSize, key: \.fontSize, valid: { $0 > 0 }),
            .init(title: "字重", detail: draft.fontWeight > 0 ? "粗体" : "常规") { [weak self] in
                guard let self else { return }; draft.fontWeight = draft.fontWeight > 0 ? 0 : 0.35; save()
            },
            .init(title: "字体", detail: draft.fontName.isEmpty ? "系统" : draft.fontName) { [weak self] in
                guard let self else { return }
                let names = [""] + UIFont.familyNames.sorted().compactMap { UIFont.fontNames(forFamilyName: $0).first }
                let page = NovelChoiceList(title: "字体", choices: names.map { $0.isEmpty ? "系统" : $0 }, themeProvider: themeProvider) { [weak self] index in self?.draft.fontName = names[index]; self?.save() }
                navigationController?.pushViewController(page, animated: true)
            },
            numeric("行距", value: draft.lineSpacing, key: \.lineSpacing, valid: { $0 >= 0 }),
            numeric("段距", value: draft.paragraphSpacing, key: \.paragraphSpacing, valid: { $0 >= 0 }),
            numeric("边距", value: draft.margin, key: \.margin, valid: { $0 >= 0 }),
            numeric("正文宽度", value: draft.contentWidth, key: \.contentWidth, valid: { $0 > 0 }),
            .init(title: "简繁转换", detail: draft.conversion == "none" ? "原文" : draft.conversion == "simplified" ? "简体" : "繁体") { [weak self] in
                guard let self else { return }; choices("简繁转换", items: [("原文", "none"), ("简体", "simplified"), ("繁体", "traditional")].map { name, value in
                    (name, { [weak self] in self?.draft.conversion = value; self?.save() }) })
            },
            .init(title: "主题", detail: NovelReadingSettings.themes[min(max(0, draft.theme), 7)].name) { [weak self] in
                guard let self else { return }; choices("主题", items: NovelReadingSettings.themes.enumerated().map { index, theme in
                    (theme.name, { [weak self] in self?.draft.theme = index; self?.draft.background = ""; self?.draft.foreground = ""; self?.save() }) })
            },
            color("文字颜色", key: \.foreground), color("背景颜色", key: \.background),
            .init(title: "背景图片", detail: draft.backgroundImage == nil ? "无" : "已设置") { [weak self] in
                guard let self else { return }
                choices("背景图片", items: [("选择图片", { [weak self] in
                    guard let self else { return }; var config = PHPickerConfiguration(); config.filter = .images; config.selectionLimit = 1
                    let picker = PHPickerViewController(configuration: config); picker.delegate = self; present(picker, animated: true)
                }), ("移除", { [weak self] in self?.draft.backgroundImage = nil; self?.save() })])
            },
            numeric("自动阅读速度", value: draft.automaticSpeed, key: \.automaticSpeed, valid: { $0 >= 1 && $0 <= 10 })]
    }
    private func color(_ title: String, key: WritableKeyPath<NovelReadingSettings, String>) -> Row {
        .init(title: title, detail: draft[keyPath: key]) { [weak self] in
            guard let self else { return }; textPrompt(title: title, value: draft[keyPath: key]) { [weak self] text in
                guard let self else { return }
                let value = text.trimmingCharacters(in: .whitespacesAndNewlines)
                guard value.isEmpty || value.count == 7 && value.first == "#" && UInt64(value.dropFirst(), radix: 16) != nil else {
                    throw NovelError.invalidRule(["颜色格式为 #RRGGBB"])
                }
                draft[keyPath: key] = value; save()
            }
        }
    }
    public func picker(_ picker: PHPickerViewController, didFinishPicking results: [PHPickerResult]) {
        picker.dismiss(animated: true)
        guard let item = results.first?.itemProvider else { return }
        item.loadObject(ofClass: UIImage.self) { [weak self] object, error in
            let data = (object as? UIImage)?.jpegData(compressionQuality: 0.9)
            Task { @MainActor [weak self] in
                guard let self, isPageActive else { return }
                guard let data else { if let error { showError(error.localizedDescription) }; return }
                draft.backgroundImage = data; save()
            }
        }
    }
}

@MainActor
final class NovelChoiceList: NovelTablePage {
    init(title: String, choices: [String], themeProvider: any PageThemeProviding, choose: @escaping (Int) -> Void) {
        super.init(themeProvider: themeProvider); pageTitle = title
        rows = choices.enumerated().map { index, value in .init(title: value) { [weak self] in choose(index); self?.navigationController?.popViewController(animated: true) } }
    }
}

#endif
