#if os(iOS)
import UIKit
import Combine
import MeowUI

@MainActor
final class FilmRulePageViewController: FilmSettingsListController {
    override var avoidsKeyboard: Bool { true }
    let owner: FilmRulesDraft
    let group: FilmRuleGroup
    private let editorForm = ManagementFormView()
    private(set) var editors: [FilmRuleEditorView] = []
    private(set) var writing = false
    private var editGeneration = UUID()
    private lazy var saveButton = PageHeaderTextButton(title: "保存") { [weak self] in self?.save() }

    init(owner: FilmRulesDraft, group: FilmRuleGroup, themeProvider: any PageThemeProviding) {
        self.owner = owner; self.group = group
        super.init(themeProvider: themeProvider); pageTitle = group.title
    }
    override func viewDidLoad() {
        super.viewDidLoad()
        view.accessibilityIdentifier = "film.rules.\(group.rawValue)"
        saveButton.accessibilityIdentifier = "film.rules.save"
        observe(owner.$busy.removeDuplicates().eraseToAnyPublisher()) { [weak self] _ in self?.render() }
        pageHeader.setTrailing([saveButton]); render()
    }
    override func pageDidBecomeActive() {
        runPageTask { [weak self] in
            guard let self else { return }
            do { try await owner.load(); render() }
            catch { failure(error, retry: { [weak self] in self?.pageDidBecomeActive() }) }
        }
    }
    override func pageDidBecomeVisible() { render() }
    override func applyTheme() {
        super.applyTheme()
        if isViewLoaded { render() }
    }
    override func render() {
        guard isViewLoaded else { return }
        editGeneration = UUID()
        let generation = editGeneration
        for editor in editors { editor.setEnabled(false) }
        editors = owner.rules(group).enumerated().map { index, rule in
            let editor = FilmRuleEditorView(rule: rule, group: group, index: index,
                                            theme: theme, form: editorForm)
            editor.setEnabled(!owner.busy && !writing)
            editor.change = { [weak self] rule in
                guard let self, editGeneration == generation else { return }
                owner.edit(group, index: index, rule: rule)
            }
            editor.delete = { [weak self] in
                guard let self, editGeneration == generation, !owner.busy, !writing else { return }
                owner.delete(group, index: index)
                render()
            }
            return editor
        }
        saveButton.isEnabled = owner.draft != nil && !owner.busy && !writing
        saveButton.applyTheme(theme)
        sections = editors.enumerated().map { index, editor in
            .init(title: "", rows: [.init(title: "", id: "film.rules.\(group.rawValue).\(index)",
                                        content: editor, stylesContent: false)])
        }
        sections.append(.init(title: "", rows: [
            .init(title: "添加规则", id: "film.rules.\(group.rawValue).add", enabled: owner.draft != nil && !owner.busy && !writing,
                  action: { [weak self] in guard let self else { return }; owner.add(group); render() }),
            .init(title: "恢复默认", id: "film.rules.restore", enabled: owner.draft != nil && !owner.busy && !writing,
                  action: { [weak self] in self?.confirm("恢复默认规则？") { [weak self] in self?.restore() } }),
            .init(title: "测试", id: "film.rules.test", enabled: owner.draft != nil,
                  action: { [weak self] in self?.openTest() }),
        ]))
        if owner.draft == nil { sections.append(.init(title: "", rows: [.init(title: "加载中", id: "film.rules.busy")])) }
        super.render()
    }
    func save() {
        guard isPageActive, !owner.busy, !writing else { return }
        view.endEditing(true); writing = true; render()
        Task { [weak self, owner, group] in
            do {
                try await owner.save(group)
                guard let self else { return }
                writing = false; render()
                if isPageVisible { notice("已保存") }
            } catch {
                guard let self else { return }
                writing = false; render()
                if isPageActive { failure(error, retry: { [weak self] in self?.save() }) }
            }
        }
    }

    private func openTest() {
        guard owner.draft != nil, let navigationController,
              navigationController.topViewController === self else { return }
        navigationController.pushViewController(
            FilmRuleTestController(owner: owner, group: group, themeProvider: themeProvider),
            animated: true
        )
    }

    private func restore() {
        guard isPageActive, !owner.busy, !writing else { return }
        view.endEditing(true); writing = true; render()
        runPageTask { [weak self] in
            guard let self else { return }
            defer { writing = false; render() }
            do {
                try await owner.restore(group)
                try Task.checkCancellation()
            } catch { failure(error, retry: { [weak self] in self?.restore() }) }
        }
    }
    override func pageDidBecomeInactive() { writing = false }
}
#endif
