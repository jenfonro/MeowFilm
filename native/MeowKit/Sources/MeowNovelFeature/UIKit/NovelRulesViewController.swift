#if os(iOS)
import UIKit
import UniformTypeIdentifiers
import MeowUI

@MainActor
final class NovelRulesViewController: NovelTablePage, UIDocumentPickerDelegate {
    private let feature: NovelFeature
    private var group: String?
    init(feature: NovelFeature, themeProvider: any PageThemeProviding) { self.feature = feature; super.init(themeProvider: themeProvider); pageTitle = "替换净化" }
    override func viewDidLoad() {
        super.viewDidLoad(); view.accessibilityIdentifier = "novel.rules"
        pageHeader.setTrailing([PageHeaderTextButton(title: "操作") { [weak self] in self?.actions() }])
        observe(feature) { [weak self] in self?.render() }; render()
    }
    private func render() {
        rows = feature.local.rules.filter { group == nil || $0.group == group }.enumerated().sorted {
            $0.element.order == $1.element.order ? $0.offset < $1.offset : $0.element.order < $1.element.order
        }.map { _, rule in
            .init(title: rule.name.isEmpty ? rule.pattern : rule.name, detail: (rule.isEnabled ? "启用" : "停用") + (rule.group.isEmpty ? "" : " · " + rule.group)) { [weak self] in self?.edit(rule) }
        }
    }
    private func actions() {
        let alert = UIAlertController(title: "替换净化", message: nil, preferredStyle: .actionSheet)
        alert.addAction(UIAlertAction(title: "新增", style: .default) { [weak self] _ in self?.edit(NovelRule()) })
        alert.addAction(UIAlertAction(title: "分组", style: .default) { [weak self] _ in
            guard let self else { return }; let names = ["全部"] + Set(feature.local.rules.map(\.group)).sorted()
            let page = NovelChoiceList(title: "分组", choices: names, themeProvider: themeProvider) { [weak self] index in self?.group = index == 0 ? nil : names[index]; self?.render() }
            navigationController?.pushViewController(page, animated: true)
        })
        alert.addAction(UIAlertAction(title: "导入", style: .default) { [weak self] _ in
            guard let self else { return }; let picker = UIDocumentPickerViewController(forOpeningContentTypes: [.json, .plainText], asCopy: true)
            picker.allowsMultipleSelection = true; picker.delegate = self; present(picker, animated: true)
        })
        alert.addAction(UIAlertAction(title: "导出", style: .default) { [weak self] _ in
            guard let self else { return }
            do { let encoder = JSONEncoder(); encoder.outputFormatting = [.prettyPrinted, .sortedKeys]; share(text: String(decoding: try encoder.encode(feature.local.rules), as: UTF8.self), name: "purification-rules.json") }
            catch { showError(error.localizedDescription) }
        })
        alert.addAction(UIAlertAction(title: "取消", style: .cancel)); alert.popoverPresentationController?.sourceView = view; present(alert, animated: true)
    }
    private func edit(_ rule: NovelRule) {
        let page = NovelRuleEditor(rule: rule, feature: feature, themeProvider: themeProvider)
        navigationController?.pushViewController(page, animated: true)
    }
    func documentPicker(_ controller: UIDocumentPickerViewController, didPickDocumentsAt urls: [URL]) {
        runPageTask { [weak self] in
            guard let self else { return }; var errors: [String] = []
            for url in urls {
                do {
                    let access = url.startAccessingSecurityScopedResource(); defer { if access { url.stopAccessingSecurityScopedResource() } }
                    let data = try Data(contentsOf: url), imported = try JSONDecoder().decode([NovelRule].self, from: data)
                    var next = feature.local.rules
                    for rule in imported {
                        try await feature.rules.validate(rule)
                        if let index = next.firstIndex(where: { $0.id == rule.id }) { next[index] = rule } else { next.append(rule) }
                    }
                    try await feature.saveRules(next)
                } catch { errors.append(url.lastPathComponent + " · " + error.localizedDescription) }
            }
            if !errors.isEmpty { showError(errors.joined(separator: "\n")) }
        }
    }
}

@MainActor
final class NovelRuleEditor: NovelTablePage {
    private var rule: NovelRule
    private let feature: NovelFeature
    init(rule: NovelRule, feature: NovelFeature, themeProvider: any PageThemeProviding) { self.rule = rule; self.feature = feature; super.init(themeProvider: themeProvider); pageTitle = "净化规则" }
    override func viewDidLoad() {
        super.viewDidLoad(); pageHeader.setTrailing([PageHeaderTextButton(title: "保存") { [weak self] in self?.save() }]); render()
    }
    private func text(_ title: String, key: WritableKeyPath<NovelRule, String>) -> Row {
        .init(title: title, detail: rule[keyPath: key]) { [weak self] in
            guard let self else { return }; textPrompt(title: title, value: rule[keyPath: key]) { [weak self] in self?.rule[keyPath: key] = $0; self?.render() }
        }
    }
    private func toggle(_ title: String, key: WritableKeyPath<NovelRule, Bool>) -> Row {
        .init(title: title, detail: rule[keyPath: key] ? "开启" : "关闭") { [weak self] in guard let self else { return }; rule[keyPath: key].toggle(); render() }
    }
    private func render() {
        rows = [text("名称", key: \.name), text("分组", key: \.group), toggle("启用", key: \.isEnabled),
            text("查找", key: \.pattern), text("替换为", key: \.replacement), toggle("正则表达式", key: \.isRegex),
            toggle("处理标题", key: \.scopeTitle), toggle("处理正文", key: \.scopeContent),
            text("适用书名或源 URL", key: \.scope), text("排除书名或源 URL", key: \.excludeScope),
            .init(title: "顺序", detail: "\(rule.order)") { [weak self] in
                guard let self else { return }; textPrompt(title: "顺序", value: "\(rule.order)", keyboard: .numbersAndPunctuation) { [weak self] value in
                    guard let self else { return }; guard let number = Int(value) else { showError("顺序无效"); return }; rule.order = number; render()
                }
            },
            .init(title: "执行超时（毫秒）", detail: "\(rule.timeoutMillisecond)") { [weak self] in
                guard let self else { return }; textPrompt(title: "执行超时（毫秒）", value: "\(rule.timeoutMillisecond)", keyboard: .numberPad) { [weak self] value in
                    guard let self else { return }; guard let number = Int(value), number > 0 else { showError("超时无效"); return }; rule.timeoutMillisecond = number; render()
                }
            },
            .init(title: "删除") { [weak self] in
                guard let self else { return }
                let alert = UIAlertController(title: "删除净化规则", message: rule.name, preferredStyle: .alert)
                alert.addAction(UIAlertAction(title: "取消", style: .cancel)); alert.addAction(UIAlertAction(title: "删除", style: .destructive) { [weak self] _ in
                    guard let self else { return }; runPageTask { [weak self] in
                        guard let self else { return }; do { try await feature.saveRules(feature.local.rules.filter { $0.id != self.rule.id }); navigationController?.popViewController(animated: true) }
                        catch { showError(error.localizedDescription) }
                    }
                }); present(alert, animated: true)
            }]
    }
    private func save() {
        runPageTask { [weak self] in
            guard let self else { return }
            do {
                var next = feature.local.rules
                if let index = next.firstIndex(where: { $0.id == self.rule.id }) { next[index] = rule } else { next.append(rule) }
                try await feature.saveRules(next); navigationController?.popViewController(animated: true)
            } catch { showError(error.localizedDescription) }
        }
    }
}
#endif
