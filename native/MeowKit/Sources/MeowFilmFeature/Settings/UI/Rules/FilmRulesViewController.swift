#if os(iOS)
import UIKit
import Combine
import MeowUI

@MainActor
final class FilmRuleTestView: NSObject, UITextViewDelegate, PageThemeable {
    let input = SettingsTextView()
    let query = SettingsTextField()
    let inputSection: SettingsInputSection
    let querySection: SettingsInputSection
    var change: (() -> Void)?

    init(group: FilmRuleGroup) {
        inputSection = SettingsInputSection(title: "测试文本", input: input)
        querySection = SettingsInputSection(title: "查询词", input: query)
        super.init()
        input.accessibilityLabel = "测试文本"; input.accessibilityIdentifier = "film.rule.test.input"
        query.accessibilityLabel = "查询词"; query.accessibilityIdentifier = "film.rule.test.query"
        input.inputAppearance = .underline; query.inputAppearance = .underline
        input.font = .preferredFont(forTextStyle: .body)
        query.font = .preferredFont(forTextStyle: .body)
        input.adjustsFontForContentSizeCategory = true; query.adjustsFontForContentSizeCategory = true
        input.autocorrectionType = .no; query.autocorrectionType = .no
        input.autocapitalizationType = .none; query.autocapitalizationType = .none
        input.smartQuotesType = .no; query.smartQuotesType = .no
        input.smartDashesType = .no; query.smartDashesType = .no
        input.heightAnchor.constraint(greaterThanOrEqualToConstant: 160).isActive = true
        query.heightAnchor.constraint(greaterThanOrEqualToConstant: 44).isActive = true
        query.isHidden = group != .search; querySection.isHidden = query.isHidden
        input.delegate = self
        query.addAction(UIAction { [weak self] _ in self?.change?() }, for: .editingChanged)
    }
    func textViewDidChange(_ textView: UITextView) { change?() }
    func applyTheme(_ theme: PageTheme) {
        inputSection.applyTheme(theme); querySection.applyTheme(theme)
    }
}

@MainActor
final class FilmRulePageViewController: SettingsFormViewController {
    let owner: FilmRulesDraft
    let group: FilmRuleGroup
    let testFields: FilmRuleTestView
    var scrollView: UIScrollView { form }
    override var formFooter: UIView? { actions }
    override var usesFormCardSurface: Bool { false }
    private let cards = UIStackView()
    private let actions = UIStackView()
    private let actionLine = SettingsDividerView()
    private let loading = UILabel()
    private var actionHeights: [NSLayoutConstraint] = []
    private(set) var editors: [FilmRuleEditorView] = []
    private(set) var writing = false
    private(set) var testing = false
    private(set) var result: FilmRuleResult?
    private var renderedRules: [FilmMatchRule] = []
    private var editGeneration = UUID()
    private var testGeneration = UUID()
    private var pendingTest: (generation: UUID, request: FilmRuleRequest, rules: [FilmMatchRule])?
    private lazy var saveButton = PageHeaderTextButton(title: "保存") { [weak self] in self?.save() }
    private lazy var addButton = PageHeaderTextButton(title: "＋ 添加规则") { [weak self] in
        guard let self, !owner.busy, !writing else { return }
        cancelTest(); owner.add(group); render()
    }
    private lazy var restoreButton = PageHeaderTextButton(title: "恢复默认") { [weak self] in
        self?.presentPageConfirmation(title: "恢复默认规则？", confirmTitle: "确认", destructive: true) { [weak self] in
            self?.restore()
        }
    }
    private lazy var testButton = PageHeaderTextButton(title: "测试") { [weak self] in
        guard let self else { return }
        if testing { cancelTest() } else { runTest() }
    }

    init(owner: FilmRulesDraft, group: FilmRuleGroup, themeProvider: any PageThemeProviding) {
        self.owner = owner; self.group = group
        testFields = FilmRuleTestView(group: group)
        super.init(themeProvider: themeProvider); pageTitle = group.title
    }
    override func viewDidLoad() {
        super.viewDidLoad()
        view.accessibilityIdentifier = "film.rules.\(group.rawValue)"
        saveButton.accessibilityIdentifier = "film.rules.save"
        addButton.accessibilityIdentifier = "film.rules.\(group.rawValue).add"
        restoreButton.accessibilityIdentifier = "film.rules.restore"
        testButton.accessibilityIdentifier = "film.rules.test"
        actions.accessibilityIdentifier = "film.rules.actions"
        actions.isLayoutMarginsRelativeArrangement = true
        actions.layoutMargins = .init(top: 8, left: SettingsVisualMetrics.cardHorizontalInset,
                                      bottom: 8, right: SettingsVisualMetrics.cardHorizontalInset)
        form.fields.spacing = SettingsVisualMetrics.sectionSpacing
        cards.axis = .vertical; cards.spacing = SettingsVisualMetrics.sectionSpacing
        form.fields.addArrangedSubview(cards)
        loading.text = "加载中"; loading.font = .preferredFont(forTextStyle: .body)
        loading.adjustsFontForContentSizeCategory = true
        registerMutedLabel(loading)
        form.fields.addArrangedSubview(loading)
        registerThemeable(testFields)
        registerThemeable(actionLine)
        testFields.change = { [weak self] in self?.cancelTest() }
        actions.axis = .horizontal; actions.spacing = 12; actions.distribution = .fillEqually
        for button in [addButton, restoreButton, testButton] {
            button.titleLabel?.numberOfLines = 0
            button.titleLabel?.lineBreakMode = .byCharWrapping
            button.setContentCompressionResistancePriority(.defaultLow, for: .horizontal)
            actions.addArrangedSubview(button)
        }
        actions.addSubview(actionLine); actionLine.translatesAutoresizingMaskIntoConstraints = false
        NSLayoutConstraint.activate([
            actionLine.topAnchor.constraint(equalTo: actions.topAnchor),
            actionLine.leadingAnchor.constraint(equalTo: actions.leadingAnchor),
            actionLine.trailingAnchor.constraint(equalTo: actions.trailingAnchor),
        ])
        actionHeights = [addButton, restoreButton, testButton].map {
            let height = $0.heightAnchor.constraint(greaterThanOrEqualToConstant: 44)
            height.isActive = true
            return height
        }
        registerForTraitChanges([UITraitPreferredContentSizeCategory.self]) {
            (page: FilmRulePageViewController, _: UITraitCollection) in page.updateActionLayout()
        }
        observeDistinct(owner.$busy.eraseToAnyPublisher(), immediateOnMain: false, whenVisible: true) { [weak self] _ in self?.render() }
        observeDistinct(owner.$draft.eraseToAnyPublisher(), immediateOnMain: false, whenVisible: true) { [weak self] _ in self?.render() }
        let editing = NotificationCenter.default.publisher(for: UITextField.textDidBeginEditingNotification)
            .merge(with: NotificationCenter.default.publisher(for: UITextView.textDidBeginEditingNotification))
            .merge(with: NotificationCenter.default.publisher(for: UIResponder.keyboardDidChangeFrameNotification))
            .eraseToAnyPublisher()
        observe(editing, whenVisible: true) { [weak self] _ in self?.scrollFocusedField() }
        pageHeader.setTrailing([saveButton])
        updateActionLayout(); render(); applyTheme()
    }
    override func pageDidBecomeActive() {
        runPageTask(key: "load", retry: { [weak self] in self?.pageDidBecomeActive() }) { [weak self, owner] in
            try await owner.load()
            self?.render()
        }
    }
    override func pageDidBecomeVisible() { render(); startPendingTest() }
    override func pageDidFinishAppearing() { super.pageDidFinishAppearing(); startPendingTest() }
    override func applyTheme() {
        super.applyTheme()
        guard isViewLoaded else { return }
        for editor in editors { editor.applyTheme(theme) }
        actions.backgroundColor = UIColor(theme.settings.surface)
        updateControls()
    }
    func render() {
        guard isViewLoaded else { return }
        let rules = owner.rules(group)
        if renderedRules != rules {
            cancelTest()
            editGeneration = UUID()
            let generation = editGeneration
            for editor in editors { editor.setEnabled(false); editor.removeFromSuperview() }
            renderedRules = rules
            editors = rules.enumerated().map { index, rule in
                let editor = FilmRuleEditorView(rule: rule, group: group, index: index, theme: theme)
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
        actions.axis = largeType ? .vertical : .horizontal
        actions.distribution = largeType ? .fillEqually : .fillProportionally
        for (button, height) in zip([addButton, restoreButton, testButton], actionHeights) {
            button.titleLabel?.font = .preferredFont(forTextStyle: .body, compatibleWith: traitCollection)
            button.titleLabel?.adjustsFontForContentSizeCategory = true
            height.constant = max(44, (button.titleLabel?.font.lineHeight ?? 20) + 16)
        }
    }
    private func scrollFocusedField() {
        let fields: [UIView] = editors.flatMap { [$0.pattern as UIView] + [$0.replacement, $0.flags].compactMap { $0 } }
        guard let field = fields.first(where: \.isFirstResponder) else { return }
        revealFocusedField(field, inset: 12)
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
                if isPageVisible { enqueueNotice(title: "已保存") }
            } catch {
                guard let self else { return }
                writing = false; render()
                if isPageActive { enqueueError(error.localizedDescription, retry: { [weak self] in self?.save() }) }
            }
        }
    }

    private var testRequest: FilmRuleRequest {
        .init(section: group.section, text: testFields.input.text ?? "",
              query: group == .search ? testFields.query.text : nil)
    }
    func runTest() {
        guard canPresentPageAlert, owner.draft != nil, !owner.busy, !writing else { return }
        cancelTest()
        let dialog = ManagementDialog(title: "测试", action: "测试", themeProvider: themeProvider)
        dialog.addBodyView(testFields.inputSection)
        if group == .search { dialog.addBodyView(testFields.querySection) }
        var submitted: (generation: UUID, request: FilmRuleRequest, rules: [FilmMatchRule])?
        dialog.submit = { [weak self] in
            guard let self, isPageActive, owner.draft != nil, !owner.busy, !writing else { return false }
            submitted = (testGeneration, testRequest, owner.rules(group))
            return true
        }
        dialog.accepted = { [weak self] in
            guard let self, let value = submitted else { return }
            submitted = nil
            guard self.isPageActive, self.testGeneration == value.generation else { return }
            self.pendingTest = value
            self.startPendingTest()
        }
        presentPageModal(dialog)
    }
    private func startPendingTest() {
        guard let value = pendingTest, isPageActive, isPageVisible, canPresentPageAlert else { return }
        pendingTest = nil
        guard owner.draft != nil, !owner.busy, !writing, testGeneration == value.generation,
              testRequest == value.request, owner.rules(group) == value.rules else { cancelTest(); return }
        let generation = value.generation, request = value.request, rules = value.rules
        testing = true
        for editor in editors { editor.showResult(.init(text: "测试中"), theme: theme) }
        updateControls()
        runPageTask(
            key: "test",
            retry: { [weak self] in self?.runTest() },
            onFinally: { [weak self] in
                guard let self, self.testGeneration == generation, self.testing else { return }
                self.cancelTest()
            }
        ) { [weak self, service = owner.service, group] in
            let value = try await service.process(request, group: group, rules: rules)
            guard let self, !Task.isCancelled, self.isPageActive, self.isPageVisible,
                  self.testGeneration == generation else { return }
            guard self.testRequest == request, self.owner.rules(group) == rules else { self.cancelTest(); return }
            let results = try FilmRuleCardResult.values(value, group: group, count: rules.count)
            self.result = value; self.testing = false
            for (editor, row) in zip(self.editors, results) { editor.showResult(row, theme: self.theme) }
            self.updateControls()
            self.scrollFocusedField()
        }
    }
    func cancelTest() {
        testGeneration = UUID()
        pendingTest = nil
        cancelPageTask(key: "test")
        testing = false; result = nil
        for editor in editors { editor.showResult(nil, theme: theme) }
        if isViewLoaded { updateControls() }
    }

    private func restore() {
        guard isPageActive, !owner.busy, !writing else { return }
        cancelTest()
        view.endEditing(true); writing = true; render()
        runPageTask(
            key: "restore",
            retry: { [weak self] in self?.restore() },
            onFinally: { [weak self] in
                guard let self else { return }
                self.writing = false
                self.render()
            }
        ) { [owner, group] in
            try await owner.restore(group)
        }
    }
    override func pageDidBecomeHidden() { cancelTest() }
    override func pageDidBecomeInactive() { cancelTest(); writing = false }
}
#endif
