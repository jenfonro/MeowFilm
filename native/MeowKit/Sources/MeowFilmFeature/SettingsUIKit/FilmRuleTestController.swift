#if os(iOS)
import UIKit
import MeowUI

@MainActor
final class FilmRuleTestController: FilmSettingsListController {
    override var avoidsKeyboard: Bool { true }
    let owner: FilmRulesDraft
    let section: FilmRuleSection
    let input = filmTextView("测试文本", id: "film.rule.test.input")
    let query = filmTextField("查询词", id: "film.rule.test.query")
    private(set) var result: FilmRuleResult?
    private(set) var testing = false
    private var testTask: Task<Void, Never>?
    private var testGeneration = UUID()

    init(owner: FilmRulesDraft, section: FilmRuleSection, themeProvider: any PageThemeProviding) {
        self.owner = owner; self.section = section
        super.init(themeProvider: themeProvider); pageTitle = "测试\(section.settingsTitle)"
    }
    override func viewDidLoad() { super.viewDidLoad(); render() }
    override func render() {
        input.textColor = UIColor(theme.ink); query.textColor = UIColor(theme.ink)
        sections = [.init(title: "测试文本", rows: [.init(title: "", id: "", content: input)])]
        if section == .search {
            sections.append(.init(title: "查询词", rows: [.init(title: "", id: "", content: query)]))
        }
        sections.append(.init(title: "", rows: [
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
        super.render()
    }
    func runTest() {
        guard let draft = owner.draft else { return }
        view.endEditing(true); cancelTest()
        let generation = UUID(); testGeneration = generation
        testing = true; result = nil; render()
        let request = FilmRuleRequest(section: section, text: input.text, query: section == .search ? query.text : nil)
        testTask = Task { [weak self, service = owner.service] in
            do {
                let value = try await service.process(request, draft: draft)
                guard let self, !Task.isCancelled, testGeneration == generation else { return }
                result = value; testing = false; testTask = nil; render()
            } catch {
                guard let self, !Task.isCancelled, testGeneration == generation else { return }
                testing = false; testTask = nil; render()
                failure(error, retry: { [weak self] in self?.runTest() })
            }
        }
    }
    func cancelTest() {
        testGeneration = UUID(); testTask?.cancel(); testTask = nil; testing = false
    }
    override func pageDidBecomeHidden() { cancelTest() }
    override func pageDidBecomeInactive() { cancelTest() }
    override func viewWillAppear(_ animated: Bool) { super.viewWillAppear(animated); render() }
}
#endif
