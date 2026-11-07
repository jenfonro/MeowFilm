#if os(iOS)
import UIKit
import MeowUI

@MainActor
final class FilmRuleEditorController: FilmSettingsListController, UITextViewDelegate {
    override var avoidsKeyboard: Bool { true }
    let owner: FilmRulesDraft
    let group: FilmRuleGroup
    private(set) var ruleIndex: Int?
    let pattern = filmTextView("正则表达式", id: "film.rule.pattern")
    let replacement = filmTextField("替换", id: "film.rule.replace")
    let flags = filmTextField("flags", id: "film.rule.flags")
    private var writing = false
    private var removedFromDraft = false
    private var finished = false

    init(owner: FilmRulesDraft, group: FilmRuleGroup, index: Int?, themeProvider: any PageThemeProviding) {
        self.owner = owner; self.group = group; ruleIndex = index
        super.init(themeProvider: themeProvider)
        pageTitle = index == nil ? "添加规则" : "编辑规则"
        if let index {
            let rule = owner.rules(group)[index]
            pattern.text = rule.pattern; replacement.text = rule.replace; flags.text = rule.flags
        }
    }
    override func viewDidLoad() {
        super.viewDidLoad()
        pattern.delegate = self
        for field in [replacement, flags] {
            field.addAction(UIAction { [weak self] _ in self?.updateDraft() }, for: .editingChanged)
        }
        render()
    }
    func textViewDidChange(_ textView: UITextView) { updateDraft() }
    func updateDraft() {
        guard !finished, let ruleIndex else { return }
        owner.edit(group, index: ruleIndex, rule: currentRule)
    }
    private var currentRule: FilmMatchRule {
        .init(pattern: pattern.text, replace: replacement.text ?? "", flags: flags.text ?? "")
    }
    override func render() {
        if finished {
            sections = []
            super.render()
            return
        }
        if removedFromDraft {
            sections = [.init(title: "", rows: [
                .init(title: writing ? "保存中" : "保存删除", id: "film.rule.save", enabled: !writing,
                      action: { [weak self] in self?.retryDeletedDraft() }),
            ])]
            super.render()
            return
        }
        pattern.isEditable = !writing; replacement.isEnabled = !writing; flags.isEnabled = !writing
        pattern.textColor = UIColor(theme.ink); replacement.textColor = UIColor(theme.ink); flags.textColor = UIColor(theme.ink)
        sections = [.init(title: "正则表达式", rows: [.init(title: "", id: "", content: pattern)])]
        if !group.isClean {
            sections.append(.init(title: "替换", rows: [.init(title: "", id: "", content: replacement)]))
            sections.append(.init(title: "flags", rows: [.init(title: "", id: "", content: flags)]))
        }
        var actions: [FilmSettingsRow] = [
            .init(title: writing ? "保存中" : "保存", id: "film.rule.save", enabled: !writing && !owner.busy,
                  action: { [weak self] in self?.save() }),
        ]
        if ruleIndex != nil {
            actions.append(.init(title: "删除", id: "film.rule.delete", enabled: !writing && !owner.busy,
                action: { [weak self] in self?.confirm("删除规则？") { [weak self] in self?.deleteRule() } }))
        }
        sections.append(.init(title: "", rows: actions)); super.render()
    }
    func save() {
        guard !finished, !writing, !owner.busy else { return }
        updateDraft(); view.endEditing(true)
        writing = true; render()
        let value = currentRule
        Task { [weak self] in
            guard let self else { return }
            let count = owner.rules(group).count
            do {
                if ruleIndex == nil {
                    try await owner.add(group, rule: value)
                } else { try await owner.save() }
                finishEditing()
            } catch {
                // Add has already joined the shared draft even when persistence fails.
                if ruleIndex == nil, owner.rules(group).count > count { ruleIndex = count }
                writing = false; render()
                failure(error, retry: { [weak self] in self?.save() })
            }
        }
    }
    func deleteRule() {
        guard !finished, let index = ruleIndex, !writing, !owner.busy else { return }
        writing = true; render()
        Task { [weak self] in
            guard let self else { return }
            do {
                try await owner.delete(group, index: index)
                finishEditing()
            } catch {
                // Retry commits the complete draft; it must not delete the next row.
                removedFromDraft = true; writing = false; render()
                failure(error, retry: { [weak self] in self?.retryDeletedDraft() })
            }
        }
    }
    private func retryDeletedDraft() {
        guard !writing else { return }
        writing = true; render()
        Task { [weak self] in
            guard let self else { return }
            do { try await owner.save(); finishEditing() }
            catch {
                writing = false; render()
                failure(error, retry: { [weak self] in self?.retryDeletedDraft() })
            }
        }
    }
    private func finishEditing() {
        // Saving normalizes all groups and can remove this row or earlier rows.
        // The submitted editor is over; a new editor must use the new snapshot.
        finished = true; writing = false; ruleIndex = nil
        pattern.delegate = nil
        pattern.isEditable = false; replacement.isEnabled = false; flags.isEnabled = false
        view.endEditing(true); render()
        if navigationController?.topViewController === self {
            navigationController?.popViewController(animated: true)
        }
    }
}
#endif
