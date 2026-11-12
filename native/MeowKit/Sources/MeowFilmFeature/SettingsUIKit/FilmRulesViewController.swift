#if os(iOS)
import UIKit
import Combine
import MeowUI

@MainActor
final class FilmRulePageViewController: FilmSettingsListController {
    override var avoidsKeyboard: Bool { true }
    let owner: FilmRulesDraft
    let group: FilmRuleGroup
    let testFields: FilmRuleTestView
    private(set) var editors: [FilmRuleEditorView] = []
    private(set) var result: FilmRuleResult?
    private(set) var testing = false
    private(set) var writing = false
    private(set) var testExpanded = false
    private var editGeneration = UUID()
    private var testGeneration = UUID()
    private var testTask: Task<Void, Never>?
    private lazy var saveButton = PageHeaderTextButton(title: "保存") { [weak self] in self?.save() }

    init(owner: FilmRulesDraft, group: FilmRuleGroup, themeProvider: any PageThemeProviding) {
        self.owner = owner; self.group = group; testFields = FilmRuleTestView(group: group)
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
            let editor = FilmRuleEditorView(rule: rule, group: group, index: index, theme: theme)
            editor.setEnabled(!owner.busy && !writing)
            editor.change = { [weak self] rule in
                guard let self, editGeneration == generation else { return }
                owner.edit(group, index: index, rule: rule)
            }
            editor.resize = { [weak self] in self?.table.beginUpdates(); self?.table.endUpdates() }
            editor.delete.addAction(UIAction { [weak self] _ in
                guard let self, editGeneration == generation else { return }
                owner.delete(group, index: index); render()
            }, for: .touchUpInside)
            return editor
        }
        saveButton.isEnabled = owner.draft != nil && !owner.busy && !writing
        saveButton.applyTheme(theme)
        sections = editors.enumerated().map { index, editor in
            .init(title: "", rows: [.init(title: "", id: "film.rules.\(group.rawValue).\(index)", content: editor)])
        }
        sections.append(.init(title: "", rows: [
            .init(title: "添加规则", id: "film.rules.\(group.rawValue).add", enabled: owner.draft != nil && !owner.busy && !writing,
                  action: { [weak self] in guard let self else { return }; owner.add(group); render() }),
            .init(title: "恢复默认", id: "film.rules.restore", enabled: owner.draft != nil && !owner.busy && !writing,
                  action: { [weak self] in self?.confirm("恢复默认规则？") { [weak self] in self?.restore() } }),
            .init(title: testExpanded ? "收起测试" : "测试", id: "film.rules.test", enabled: owner.draft != nil,
                  action: { [weak self] in self?.toggleTest() }),
        ]))
        if owner.draft == nil { sections.append(.init(title: "", rows: [.init(title: "加载中", id: "film.rules.busy")])) }
        if testExpanded {
            testFields.input.textColor = UIColor(theme.settings.ink); testFields.query.textColor = UIColor(theme.settings.ink)
            sections.append(.init(title: "测试文本", rows: [
                .init(title: "", id: "film.rule.test.fields", content: testFields),
                .init(title: testing ? "取消测试" : "测试", id: "film.rule.test.run", action: { [weak self] in
                    guard let self else { return }
                    if testing { cancelTest(); render() } else { runTest() }
                }),
            ]))
            if let result {
                var rows: [FilmSettingsRow] = [
                    .init(title: result.outputText.isEmpty ? "空结果" : result.outputText.displayString, id: "film.rule.test.output"),
                    .init(title: result.matched ? "已匹配" : "未匹配", id: "film.rule.test.match"),
                ]
                rows += result.previews.enumerated().map { .init(title: $0.element.displayString, id: "film.rule.test.preview.\($0.offset)") }
                rows += result.errors.enumerated().map { .init(title: $0.element.text.displayString, id: "film.rule.test.error.\($0.offset)") }
                sections.append(.init(title: "结果", rows: rows))
            }
        }
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
    func toggleTest() {
        testExpanded.toggle()
        if !testExpanded { cancelTest() }
        render()
    }
    func runTest() {
        guard owner.draft != nil else { return }
        view.endEditing(true); cancelTest()
        let generation = UUID(); testGeneration = generation
        let rows = owner.rules(group)
        let request = FilmRuleRequest(section: group.section, text: testFields.input.text,
                                      query: group == .search ? testFields.query.text : nil)
        testing = true; result = nil; render()
        testTask = Task { [weak self, service = owner.service, group] in
            do {
                let value = try await service.process(request, group: group, rules: rows)
                guard let self, !Task.isCancelled, testGeneration == generation else { return }
                result = value; testing = false; testTask = nil; render()
            } catch {
                guard let self, !Task.isCancelled, testGeneration == generation else { return }
                testing = false; testTask = nil; render()
                failure(error, retry: { [weak self] in self?.runTest() })
            }
        }
    }
    func cancelTest() { testGeneration = UUID(); testTask?.cancel(); testTask = nil; testing = false }
    override func pageDidBecomeHidden() { cancelTest() }
    override func pageDidBecomeInactive() { cancelTest(); writing = false }
}
#endif
