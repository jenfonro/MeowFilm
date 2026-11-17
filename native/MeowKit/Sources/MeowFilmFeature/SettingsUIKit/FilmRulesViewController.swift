#if os(iOS)
import UIKit
import Combine
import MeowUI

@MainActor
final class FilmRuleTestView {
    let input = filmTextField("测试文本", id: "film.rule.test.input")
    let query = filmTextField("查询词", id: "film.rule.test.query")

    init(group: FilmRuleGroup) { query.isHidden = group != .search }
}

@MainActor
final class FilmRulePageViewController: FilmSettingsListController {
    override var avoidsKeyboard: Bool { true }
    override var usesSettingsTable: Bool { false }
    let owner: FilmRulesDraft
    let group: FilmRuleGroup
    let testFields: FilmRuleTestView
    let scrollView = UIScrollView()
    private let editorForm = ManagementFormView()
    private let content = UIStackView()
    private let cards = UIStackView()
    private let actions = UIStackView()
    private let loading = UILabel()
    private var actionHeights: [NSLayoutConstraint] = []
    private(set) var editors: [FilmRuleEditorView] = []
    private(set) var writing = false
    private(set) var testing = false
    private(set) var result: FilmRuleResult?
    private var renderedRules: [FilmMatchRule] = []
    private var editGeneration = UUID()
    private var testGeneration = UUID()
    private var testTask: Task<Void, Never>?
    private lazy var saveButton = PageHeaderTextButton(title: "保存") { [weak self] in self?.save() }
    private lazy var addButton = PageHeaderTextButton(title: "＋ 添加规则") { [weak self] in
        guard let self, !owner.busy, !writing else { return }
        cancelTest(); owner.add(group); render()
    }
    private lazy var restoreButton = PageHeaderTextButton(title: "恢复默认") { [weak self] in
        self?.confirm("恢复默认规则？") { [weak self] in self?.restore() }
    }
    private lazy var testButton = PageHeaderTextButton(title: "测试") { [weak self] in
        guard let self else { return }
        if testing { cancelTest() } else { runTest() }
    }

    init(owner: FilmRulesDraft, group: FilmRuleGroup, themeProvider: any PageThemeProviding) {
        self.owner = owner; self.group = group
        testFields = FilmRuleTestView(group: group)
        super.init(themeProvider: themeProvider); pageTitle = group.title
        editorForm.boxedInputs = true
    }
    override func viewDidLoad() {
        super.viewDidLoad()
        view.accessibilityIdentifier = "film.rules.\(group.rawValue)"
        saveButton.accessibilityIdentifier = "film.rules.save"
        addButton.accessibilityIdentifier = "film.rules.\(group.rawValue).add"
        restoreButton.accessibilityIdentifier = "film.rules.restore"
        testButton.accessibilityIdentifier = "film.rules.test"
        scrollView.keyboardDismissMode = .interactive
        scrollView.alwaysBounceVertical = true
        scrollView.contentInsetAdjustmentBehavior = .never
        view.addSubview(scrollView); scrollView.translatesAutoresizingMaskIntoConstraints = false
        content.axis = .vertical; content.spacing = SettingsVisualMetrics.sectionSpacing
        cards.axis = .vertical; cards.spacing = SettingsVisualMetrics.sectionSpacing
        scrollView.addSubview(content); content.translatesAutoresizingMaskIntoConstraints = false
        NSLayoutConstraint.activate([
            scrollView.topAnchor.constraint(equalTo: contentTopAnchor),
            scrollView.leadingAnchor.constraint(equalTo: view.safeAreaLayoutGuide.leadingAnchor),
            scrollView.trailingAnchor.constraint(equalTo: view.safeAreaLayoutGuide.trailingAnchor),
            scrollView.bottomAnchor.constraint(equalTo: view.keyboardLayoutGuide.topAnchor),
            content.topAnchor.constraint(equalTo: scrollView.contentLayoutGuide.topAnchor,
                constant: SettingsVisualMetrics.sectionSpacing),
            content.bottomAnchor.constraint(equalTo: scrollView.contentLayoutGuide.bottomAnchor,
                constant: -SettingsVisualMetrics.sectionSpacing),
            content.leadingAnchor.constraint(equalTo: scrollView.contentLayoutGuide.leadingAnchor,
                constant: SettingsVisualMetrics.cardHorizontalInset),
            content.trailingAnchor.constraint(equalTo: scrollView.contentLayoutGuide.trailingAnchor,
                constant: -SettingsVisualMetrics.cardHorizontalInset),
            content.widthAnchor.constraint(equalTo: scrollView.frameLayoutGuide.widthAnchor,
                constant: -2 * SettingsVisualMetrics.cardHorizontalInset),
        ])
        content.addArrangedSubview(cards)
        loading.text = "加载中"; loading.font = .preferredFont(forTextStyle: .body)
        loading.adjustsFontForContentSizeCategory = true
        content.addArrangedSubview(loading)
        content.addArrangedSubview(addButton)
        addButton.layer.cornerRadius = SettingsVisualMetrics.cardCornerRadius
        addButton.layer.borderWidth = 1
        let testInputs = UIStackView(); testInputs.axis = .vertical; testInputs.spacing = 12
        for field in [testFields.input, testFields.query] where !field.isHidden {
            let label = UILabel(); label.text = field.placeholder
            label.font = .preferredFont(forTextStyle: .subheadline)
            label.adjustsFontForContentSizeCategory = true; label.numberOfLines = 0
            let column = UIStackView(arrangedSubviews: [label, field])
            column.axis = .vertical; column.spacing = 8
            testInputs.addArrangedSubview(column)
            field.smartQuotesType = .no; field.smartDashesType = .no
            field.addAction(UIAction { [weak self] _ in self?.cancelTest() }, for: .editingChanged)
        }
        content.addArrangedSubview(testInputs)
        actions.axis = .horizontal; actions.spacing = 12; actions.distribution = .fillEqually
        for button in [restoreButton, testButton] {
            button.titleLabel?.numberOfLines = 0
            button.titleLabel?.lineBreakMode = .byCharWrapping
            button.setContentCompressionResistancePriority(.defaultLow, for: .horizontal)
        }
        actions.addArrangedSubview(restoreButton); actions.addArrangedSubview(testButton)
        content.addArrangedSubview(actions)
        actionHeights = [addButton, restoreButton, testButton].map {
            let height = $0.heightAnchor.constraint(greaterThanOrEqualToConstant: 44)
            height.isActive = true
            return height
        }
        registerForTraitChanges([UITraitPreferredContentSizeCategory.self]) {
            (page: FilmRulePageViewController, _: UITraitCollection) in page.updateActionLayout()
        }
        observe(owner.$busy.removeDuplicates().eraseToAnyPublisher()) { [weak self] _ in self?.render() }
        observe(owner.$draft.removeDuplicates().eraseToAnyPublisher()) { [weak self] _ in self?.render() }
        let editing = NotificationCenter.default.publisher(for: UITextField.textDidBeginEditingNotification)
            .merge(with: NotificationCenter.default.publisher(for: UITextView.textDidBeginEditingNotification))
            .merge(with: NotificationCenter.default.publisher(for: UIResponder.keyboardDidChangeFrameNotification))
            .eraseToAnyPublisher()
        observe(editing) { [weak self] _ in self?.scrollFocusedField() }
        pageHeader.setTrailing([saveButton])
        updateActionLayout(); render(); applyTheme()
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
        guard isViewLoaded else { return }
        for editor in editors { editor.applyTheme(theme) }
        for field in [testFields.input, testFields.query] {
            theme.settings.apply(to: field)
            for case let label as UILabel in (field.superview as? UIStackView)?.arrangedSubviews ?? [] {
                label.textColor = UIColor(theme.settings.muted)
            }
        }
        loading.textColor = UIColor(theme.settings.muted)
        addButton.layer.borderColor = UIColor(theme.settings.border).cgColor
        updateControls()
    }
    override func render() {
        guard isViewLoaded else { return }
        let rules = owner.rules(group)
        if renderedRules != rules {
            cancelTest()
            editGeneration = UUID()
            let generation = editGeneration
            for editor in editors { editor.setEnabled(false); editor.removeFromSuperview() }
            renderedRules = rules
            editors = rules.enumerated().map { index, rule in
                let editor = FilmRuleEditorView(rule: rule, group: group, index: index,
                                                theme: theme, form: editorForm)
                editor.change = { [weak self] rule in
                    guard let self, editGeneration == generation, !owner.busy, !writing else { return }
                    cancelTest()
                    owner.edit(group, index: index, rule: rule)
                    renderedRules = owner.rules(group)
                }
                editor.delete = { [weak self] in
                    guard let self, editGeneration == generation, !owner.busy, !writing else { return }
                    cancelTest(); owner.delete(group, index: index); render()
                }
                cards.addArrangedSubview(editor)
                return editor
            }
        }
        cards.isHidden = editors.isEmpty
        loading.isHidden = owner.draft != nil
        updateControls()
    }
    private func updateControls() {
        let enabled = owner.draft != nil && !owner.busy && !writing
        for editor in editors { editor.setEnabled(enabled) }
        saveButton.isEnabled = owner.draft != nil && !owner.busy && !writing
        saveButton.applyTheme(theme)
        addButton.isEnabled = enabled; restoreButton.isEnabled = enabled
        testButton.isEnabled = testing || enabled
        testButton.setTitle(testing ? "取消" : "测试", for: .normal)
        testButton.accessibilityLabel = testing ? "取消测试" : "测试"
        for button in [addButton, restoreButton, testButton] {
            button.setTitleColor(UIColor(theme.settings.accent), for: .normal)
            button.setTitleColor(UIColor(theme.settings.muted), for: .disabled)
        }
    }
    private func updateActionLayout() {
        let largeType = traitCollection.preferredContentSizeCategory.isAccessibilityCategory
        for (button, height) in zip([addButton, restoreButton, testButton], actionHeights) {
            button.titleLabel?.font = .preferredFont(forTextStyle: .body, compatibleWith: traitCollection)
            button.titleLabel?.adjustsFontForContentSizeCategory = true
            let lines: CGFloat = largeType && button !== addButton ? 2 : 1
            height.constant = max(44, (button.titleLabel?.font.lineHeight ?? 20) * lines + 16)
        }
    }
    private func scrollFocusedField() {
        let fields: [UIView] = editors.flatMap { [$0.pattern as UIView] + [$0.replacement, $0.flags].compactMap { $0 } }
            + [testFields.input, testFields.query]
        guard let field = fields.first(where: \.isFirstResponder) else { return }
        view.layoutIfNeeded()
        scrollView.scrollRectToVisible(field.convert(field.bounds, to: scrollView).insetBy(dx: 0, dy: -12), animated: false)
    }
    func save() {
        guard isPageActive, !owner.busy, !writing else { return }
        cancelTest()
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

    private var testRequest: FilmRuleRequest {
        .init(section: group.section, text: testFields.input.text ?? "",
              query: group == .search ? testFields.query.text : nil)
    }
    func runTest() {
        guard isPageActive, owner.draft != nil, !owner.busy, !writing else { return }
        cancelTest()
        let generation = UUID()
        testGeneration = generation
        let request = testRequest
        let rules = owner.rules(group)
        testing = true
        for editor in editors { editor.showResult(.init(text: "测试中"), theme: theme) }
        updateControls()
        testTask = Task { [weak self, service = owner.service, group] in
            do {
                let value = try await service.process(request, group: group, rules: rules)
                guard let self, !Task.isCancelled, isPageActive, isPageVisible,
                      testGeneration == generation else { return }
                guard testRequest == request, owner.rules(group) == rules else { cancelTest(); return }
                let results = try FilmRuleCardResult.values(value, group: group, count: rules.count)
                result = value; testing = false; testTask = nil
                for (editor, row) in zip(editors, results) { editor.showResult(row, theme: theme) }
                updateControls()
                scrollFocusedField()
            } catch {
                guard let self, !Task.isCancelled, isPageActive, isPageVisible,
                      testGeneration == generation else { return }
                guard testRequest == request, owner.rules(group) == rules else { cancelTest(); return }
                cancelTest()
                failure(error, retry: { [weak self] in self?.runTest() })
            }
        }
    }
    func cancelTest() {
        testGeneration = UUID()
        testTask?.cancel(); testTask = nil
        testing = false; result = nil
        for editor in editors { editor.showResult(nil, theme: theme) }
        if isViewLoaded { updateControls() }
    }

    private func restore() {
        guard isPageActive, !owner.busy, !writing else { return }
        cancelTest()
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
    override func pageDidBecomeHidden() { cancelTest() }
    override func pageDidBecomeInactive() { cancelTest(); writing = false }
}
#endif
