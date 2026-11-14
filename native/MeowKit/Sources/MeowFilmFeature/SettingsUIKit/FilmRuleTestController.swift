#if os(iOS)
import UIKit
import Combine
import MeowUI

/// The test input is kept in one settings row so it follows the same
/// field/menu geometry as the source editor instead of becoming a free-form
/// page-level text area.
@MainActor
final class FilmRuleTestView {
    let input = filmTextField("测试文本", id: "film.rule.test.input")
    let query = filmTextField("查询词", id: "film.rule.test.query")

    init(group: FilmRuleGroup) {
        query.isHidden = group != .search
        for field in [input, query] { field.backgroundColor = .clear }
    }
}

@MainActor
final class FilmRuleTestController: FilmSettingsListController {
    override var avoidsKeyboard: Bool { true }

    let owner: FilmRulesDraft
    let group: FilmRuleGroup
    let testFields: FilmRuleTestView
    private(set) var result: FilmRuleResult?
    private(set) var testing = false
    private var testGeneration = UUID()
    private var testTask: Task<Void, Never>?
    private lazy var testButton = PageHeaderTextButton(title: "测试", accessibilityLabel: "测试") { [weak self] in
        guard let self else { return }
        if testing { cancelTest(); render() } else { runTest() }
    }

    init(owner: FilmRulesDraft, group: FilmRuleGroup, themeProvider: any PageThemeProviding) {
        self.owner = owner
        self.group = group
        testFields = FilmRuleTestView(group: group)
        super.init(themeProvider: themeProvider)
        pageTitle = "\(group.title)测试"
    }

    override func viewDidLoad() {
        super.viewDidLoad()
        view.accessibilityIdentifier = "film.rules.\(group.rawValue).test"
        testButton.accessibilityIdentifier = "film.rule.test.run"
        observe(owner.$busy.removeDuplicates().eraseToAnyPublisher()) { [weak self] _ in self?.render() }
        render()
    }

    override func pageDidBecomeActive() {
        runPageTask { [weak self] in
            guard let self else { return }
            do {
                try await owner.load()
                render()
            } catch {
                failure(error, retry: { [weak self] in self?.pageDidBecomeActive() })
            }
        }
    }

    override func pageDidBecomeVisible() { render() }

    override func applyTheme() {
        super.applyTheme()
        if isViewLoaded { render() }
    }

    override func render() {
        guard isViewLoaded else { return }
        for field in [testFields.input, testFields.query] {
            field.textColor = UIColor(theme.settings.ink)
            field.tintColor = UIColor(theme.settings.accent)
            field.attributedPlaceholder = field.placeholder.map {
                NSAttributedString(string: $0, attributes: [.foregroundColor: UIColor(theme.settings.muted)])
            }
        }
        testButton.setTitle(testing ? "取消" : "测试", for: .normal)
        testButton.accessibilityLabel = testing ? "取消测试" : "测试"
        testButton.applyTheme(theme)
        testButton.isEnabled = testing || (owner.draft != nil && !owner.busy)

        let inputRow = UIStackView(arrangedSubviews: [testFields.input, testButton])
        inputRow.axis = .horizontal
        inputRow.alignment = .center
        inputRow.spacing = 8
        testFields.input.setContentCompressionResistancePriority(.defaultLow, for: .horizontal)
        testButton.setContentCompressionResistancePriority(.required, for: .horizontal)
        var inputRows: [FilmSettingsRow] = [
            .init(title: "测试文本", id: "film.rule.test.fields", content: inputRow, stylesContent: false),
        ]
        if group == .search {
            inputRows.append(.init(title: "查询词", id: "film.rule.test.query", content: testFields.query, stylesContent: false))
        }
        if owner.draft == nil {
            inputRows.append(.init(title: "加载中", id: "film.rule.test.loading"))
        }
        sections = [.init(title: "输入", rows: inputRows)]

        if let result {
            sections.append(.init(title: "输出", rows: [
                .init(title: result.outputText.isEmpty ? "空结果" : "最终结果",
                      detail: result.outputText.displayString, id: "film.rule.test.output.row"),
                .init(title: result.matched ? "已匹配" : "未匹配", id: "film.rule.test.match"),
            ] + result.previews.enumerated().map {
                .init(title: $0.element.displayString, id: "film.rule.test.preview.\($0.offset)")
            } + result.errors.enumerated().map {
                .init(title: $0.element.text.displayString, id: "film.rule.test.error.\($0.offset)")
            }))
        } else {
            sections.append(.init(title: "输出", rows: [
                .init(title: "最终结果", detail: testing ? "测试中" : "尚未测试", id: "film.rule.test.output.row")
            ]))
        }
        super.render()
    }

    func runTest() {
        guard let draft = owner.draft, !owner.busy else { return }
        if testing { cancelTest(); render(); return }
        view.endEditing(true)
        cancelTest()
        let generation = UUID()
        testGeneration = generation
        let request = FilmRuleRequest(
            section: group.section,
            text: testFields.input.text ?? "",
            query: group == .search ? testFields.query.text : nil
        )
        testing = true
        result = nil
        render()
        testTask = Task { [weak self, service = owner.service, group] in
            do {
                let value = try await service.process(request, group: group, rules: draft.rules(in: group))
                guard let self, !Task.isCancelled, testGeneration == generation else { return }
                result = value
                testing = false
                testTask = nil
                render()
            } catch {
                guard let self, !Task.isCancelled, testGeneration == generation else { return }
                testing = false
                testTask = nil
                render()
                failure(error, retry: { [weak self] in self?.runTest() })
            }
        }
    }

    func cancelTest() {
        testGeneration = UUID()
        testTask?.cancel()
        testTask = nil
        testing = false
    }

    override func pageDidBecomeHidden() { cancelTest() }
    override func pageDidBecomeInactive() { cancelTest() }
}
#endif
