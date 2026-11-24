#if os(iOS)
import UIKit
import PhotosUI
import MeowUI

@MainActor
public final class NovelSettingsViewController: SettingsListViewController {
    public let feature: NovelFeature
    public init(feature: NovelFeature, themeProvider: any PageThemeProviding) { self.feature = feature; super.init(themeProvider: themeProvider); pageTitle = "小说设置" }
    public override func viewDidLoad() {
        super.viewDidLoad(); view.accessibilityIdentifier = "novel.settings"
        let rows = ["书源管理", "替换净化", "阅读排版"].enumerated().map { index, title in
            SettingsListRow(id: "novel.settings.\(index)", title: title, accessory: .disclosure) { [weak self] in
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
        applySections([SettingsListSection(id: "main", rows: rows)])
    }
}

@MainActor
public final class NovelAppearanceViewController: SettingsListViewController, PHPickerViewControllerDelegate {
    private let feature: NovelFeature
    private var draft: NovelReadingSettings
    public init(feature: NovelFeature, themeProvider: any PageThemeProviding) {
        self.feature = feature; draft = feature.local.settings; super.init(themeProvider: themeProvider); pageTitle = "阅读排版"
    }
    public override func viewDidLoad() { super.viewDidLoad(); view.accessibilityIdentifier = "novel.appearance"; render() }
    private func save() {
        let next = draft
        runPageTask(key: "save", onFinally: { [weak self] in
            guard let self, !Task.isCancelled else { return }
            self.draft = self.feature.local.settings
            self.render()
        }) { [weak self] in
            guard let self else { return }
            try await feature.saveSettings(next)
        }
    }
    private func textPrompt(title: String, value: String, keyboard: UIKeyboardType = .default,
                            save: @escaping (String) throws -> Void) {
        let dialog = ManagementDialog(title: title, action: "保存", themeProvider: themeProvider)
        let input = ManagementDialog.input(placeholder: title)
        input.text = value; input.keyboardType = keyboard; input.autocapitalizationType = .none
        input.accessibilityLabel = title; input.accessibilityIdentifier = "novel.settings.input"
        dialog.addInput(input, title: title)
        dialog.submit = { try save(input.text ?? ""); return true }
        presentPageModal(dialog)
    }
    private func numeric(_ id: String, _ title: String, value: Double, key: WritableKeyPath<NovelReadingSettings, Double>, valid: @escaping (Double) -> Bool) -> SettingsListRow {
        SettingsListRow(id: id, title: title, detail: String(format: "%g", value), accessory: .disclosure) { [weak self] in
            self?.textPrompt(title: title, value: String(format: "%g", value), keyboard: .decimalPad) { [weak self] text in
                guard let self else { return }
                guard let value = Double(text.trimmingCharacters(in: .whitespacesAndNewlines)), value.isFinite, valid(value) else {
                    throw NovelError.invalidRule(["数值无效"])
                }
                draft[keyPath: key] = value; save()
            }
        }
    }
    private func choices(_ title: String, items: [(String, () -> Void)]) {
        presentPageActionSheet(
            title: title,
            sourceView: view,
            actions: items.map { name, action in PageAlertAction(title: name, style: .default, handler: action) }
        )
    }
    private func render() {
        applySections([SettingsListSection(id: "main", rows: [
            SettingsListRow(id: "novel.appearance.mode", title: "阅读模式", detail: draft.mode.title, accessory: .disclosure) { [weak self] in
                guard let self else { return }; choices("阅读模式", items: NovelReadingMode.allCases.map { mode in (mode.title, { [weak self] in self?.draft.mode = mode; self?.save() }) })
            },
            numeric("novel.appearance.fontSize", "字号", value: draft.fontSize, key: \.fontSize, valid: { $0 > 0 }),
            SettingsListRow(id: "novel.appearance.fontWeight", title: "字重", detail: draft.fontWeight > 0 ? "粗体" : "常规", accessory: .disclosure) { [weak self] in
                guard let self else { return }; draft.fontWeight = draft.fontWeight > 0 ? 0 : 0.35; save()
            },
            SettingsListRow(id: "novel.appearance.fontName", title: "字体", detail: draft.fontName.isEmpty ? "系统" : draft.fontName, accessory: .disclosure) { [weak self] in
                guard let self else { return }
                let names = [""] + UIFont.familyNames.sorted().compactMap { UIFont.fontNames(forFamilyName: $0).first }
                let page = NovelChoiceList(title: "字体", choices: names.map { $0.isEmpty ? "系统" : $0 }, themeProvider: themeProvider) { [weak self] index in self?.draft.fontName = names[index]; self?.save() }
                navigationController?.pushViewController(page, animated: true)
            },
            numeric("novel.appearance.lineSpacing", "行距", value: draft.lineSpacing, key: \.lineSpacing, valid: { $0 >= 0 }),
            numeric("novel.appearance.paragraphSpacing", "段距", value: draft.paragraphSpacing, key: \.paragraphSpacing, valid: { $0 >= 0 }),
            numeric("novel.appearance.margin", "边距", value: draft.margin, key: \.margin, valid: { $0 >= 0 }),
            numeric("novel.appearance.contentWidth", "正文宽度", value: draft.contentWidth, key: \.contentWidth, valid: { $0 > 0 }),
            SettingsListRow(id: "novel.appearance.conversion", title: "简繁转换", detail: draft.conversion == "none" ? "原文" : draft.conversion == "simplified" ? "简体" : "繁体", accessory: .disclosure) { [weak self] in
                guard let self else { return }; choices("简繁转换", items: [("原文", "none"), ("简体", "simplified"), ("繁体", "traditional")].map { name, value in
                    (name, { [weak self] in self?.draft.conversion = value; self?.save() }) })
            },
            SettingsListRow(id: "novel.appearance.theme", title: "主题", detail: NovelReadingSettings.themes[min(max(0, draft.theme), 7)].name, accessory: .disclosure) { [weak self] in
                guard let self else { return }; choices("主题", items: NovelReadingSettings.themes.enumerated().map { index, theme in
                    (theme.name, { [weak self] in self?.draft.theme = index; self?.draft.background = ""; self?.draft.foreground = ""; self?.save() }) })
            },
            color("novel.appearance.foreground", "文字颜色", key: \.foreground),
            color("novel.appearance.background", "背景颜色", key: \.background),
            SettingsListRow(id: "novel.appearance.backgroundImage", title: "背景图片", detail: draft.backgroundImage == nil ? "无" : "已设置", accessory: .disclosure) { [weak self] in
                guard let self else { return }
                choices("背景图片", items: [("选择图片", { [weak self] in
                    guard let self else { return }; var config = PHPickerConfiguration(); config.filter = .images; config.selectionLimit = 1
                    let picker = PHPickerViewController(configuration: config); picker.delegate = self; presentPageModal(picker)
                }), ("移除", { [weak self] in self?.draft.backgroundImage = nil; self?.save() })])
            },
            numeric("novel.appearance.automaticSpeed", "自动阅读速度", value: draft.automaticSpeed, key: \.automaticSpeed, valid: { $0 >= 1 && $0 <= 10 })
        ])])
    }
    private func color(_ id: String, _ title: String, key: WritableKeyPath<NovelReadingSettings, String>) -> SettingsListRow {
        let current = draft[keyPath: key]
        return SettingsListRow(id: id, title: title, detail: current.isEmpty ? nil : current, accessory: .disclosure) { [weak self] in
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
        dismissPageModal(picker)
        guard let item = results.first?.itemProvider else { return }
        item.loadObject(ofClass: UIImage.self) { [weak self] object, error in
            let data = (object as? UIImage)?.jpegData(compressionQuality: 0.9)
            Task { @MainActor [weak self] in
                guard let self, isPageActive else { return }
                guard let data else { if let error { enqueueError(error.localizedDescription) }; return }
                draft.backgroundImage = data; save()
            }
        }
    }
}

@MainActor
final class NovelChoiceList: SettingsListViewController {
    init(title: String, choices: [String], themeProvider: any PageThemeProviding, choose: @escaping (Int) -> Void) {
        super.init(themeProvider: themeProvider); pageTitle = title
        let rows = choices.enumerated().map { index, value in
            SettingsListRow(id: "novel.choice.\(index)", title: value, accessory: .disclosure) { [weak self] in
                choose(index); self?.navigationController?.popViewController(animated: true)
            }
        }
        applySections([SettingsListSection(id: "main", rows: rows)])
    }
}

#endif
