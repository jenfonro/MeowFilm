#if os(iOS)
import UIKit
import MeowUI

/// One rule's fields stay together inside the page, including at accessibility sizes.
@MainActor
final class FilmRuleEditorView: UIStackView, PageThemeable {
    private(set) var pattern: SettingsTextView!
    private(set) var replacement: SettingsTextField?
    private(set) var flags: SettingsTextField?
    private(set) var deleteButton: PageHeaderTextButton!
    let resultLabel = UILabel()
    private let title = UILabel()
    private let titleRow = UIStackView()
    private let matchingFields = UIStackView()
    private let background = SettingsRowBackgroundView(first: true, last: true)
    private var inputSections: [SettingsInputSection] = []
    private var flagsWidth: NSLayoutConstraint?
    private var deleteHeight: NSLayoutConstraint?
    private var cardResult: FilmRuleCardResult?
    var change: ((FilmMatchRule) -> Void)?
    var delete: (() -> Void)?

    init(rule: FilmMatchRule, group: FilmRuleGroup, index: Int, theme: PageTheme) {
        let id = "film.rules.\(group.rawValue).\(index)"
        super.init(frame: .zero)
        accessibilityIdentifier = id
        axis = .vertical; spacing = 12
        isLayoutMarginsRelativeArrangement = true
        layoutMargins = .init(top: 12, left: 20, bottom: 16, right: 20)
        insertSubview(background, at: 0)
        background.translatesAutoresizingMaskIntoConstraints = false
        NSLayoutConstraint.activate([
            background.topAnchor.constraint(equalTo: topAnchor),
            background.bottomAnchor.constraint(equalTo: bottomAnchor),
            background.leadingAnchor.constraint(equalTo: leadingAnchor),
            background.trailingAnchor.constraint(equalTo: trailingAnchor),
        ])
        title.text = "规则 \(index + 1)"
        for label in [title, resultLabel] {
            label.font = .preferredFont(forTextStyle: .subheadline)
            label.adjustsFontForContentSizeCategory = true
            label.numberOfLines = 0
            label.setContentCompressionResistancePriority(.required, for: .vertical)
        }
        title.setContentCompressionResistancePriority(.required, for: .horizontal)
        resultLabel.lineBreakMode = .byCharWrapping
        resultLabel.accessibilityIdentifier = id + ".result"
        resultLabel.isHidden = true
        titleRow.axis = .horizontal; titleRow.alignment = .firstBaseline; titleRow.spacing = 12
        titleRow.addArrangedSubview(title); titleRow.addArrangedSubview(resultLabel)
        let remove = PageHeaderTextButton(title: "删除") { }
        deleteButton = remove
        remove.titleLabel?.font = .preferredFont(forTextStyle: .subheadline)
        remove.titleLabel?.adjustsFontForContentSizeCategory = true
        remove.accessibilityLabel = "删除规则 \(index + 1)"
        remove.accessibilityIdentifier = id + ".delete"
        deleteHeight = remove.heightAnchor.constraint(greaterThanOrEqualToConstant: 44)
        deleteHeight?.isActive = true
        remove.addAction(UIAction { [weak self] _ in self?.delete?() }, for: .touchUpInside)
        let heading = UIView()
        heading.addSubview(titleRow); titleRow.translatesAutoresizingMaskIntoConstraints = false
        heading.addSubview(remove); remove.translatesAutoresizingMaskIntoConstraints = false
        // Pin the delete hit region; only the title's first line follows its baseline.
        let contentBottom = heading.bottomAnchor.constraint(equalTo: titleRow.bottomAnchor)
        contentBottom.priority = .defaultLow
        NSLayoutConstraint.activate([
            remove.topAnchor.constraint(equalTo: heading.topAnchor),
            remove.trailingAnchor.constraint(equalTo: heading.trailingAnchor),
            titleRow.leadingAnchor.constraint(equalTo: heading.leadingAnchor),
            titleRow.trailingAnchor.constraint(equalTo: remove.leadingAnchor, constant: -12),
            title.firstBaselineAnchor.constraint(equalTo: remove.firstBaselineAnchor),
            heading.bottomAnchor.constraint(greaterThanOrEqualTo: titleRow.bottomAnchor),
            heading.bottomAnchor.constraint(greaterThanOrEqualTo: remove.bottomAnchor),
            contentBottom,
        ])
        addArrangedSubview(heading)

        let patternView = FilmRulePatternTextView(rows: 4)
        patternView.text = rule.pattern
        patternView.inputAppearance = .underline
        patternView.autocorrectionType = .no
        patternView.autocapitalizationType = .none
        patternView.smartQuotesType = .no
        patternView.smartDashesType = .no
        patternView.adjustsFontForContentSizeCategory = true
        patternView.backgroundColor = .clear
        patternView.textContainer.lineFragmentPadding = 0
        patternView.accessibilityIdentifier = id + ".pattern"
        patternView.accessibilityLabel = "pattern"
        patternView.onTextChanged = { [weak self] in self?.changed() }
        pattern = patternView
        let patternSection = makeInputSection(title: "pattern", input: patternView)
        addArrangedSubview(patternSection)

        if !group.isClean {
            let replaceField = makeTextField(value: rule.replace, title: "replace", id: id + ".replace")
            let flagsField = makeTextField(value: rule.flags, title: "flags", id: id + ".flags")
            replacement = replaceField
            flags = flagsField
            let replaceSection = makeInputSection(title: "replace", input: replaceField)
            let flagsSection = makeInputSection(title: "flags", input: flagsField)
            matchingFields.axis = .horizontal; matchingFields.alignment = .fill; matchingFields.spacing = 12
            matchingFields.addArrangedSubview(replaceSection); matchingFields.addArrangedSubview(flagsSection)
            flagsWidth = flagsSection.widthAnchor.constraint(equalToConstant: 76)
            addArrangedSubview(matchingFields)
        }
        registerForTraitChanges([UITraitPreferredContentSizeCategory.self]) {
            (editor: FilmRuleEditorView, _: UITraitCollection) in editor.updateFieldLayout()
        }
        updateFieldLayout(); applyTheme(theme)
    }
    required init(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }
    override func didMoveToWindow() { super.didMoveToWindow(); updateFieldLayout() }

    private func makeTextField(value: String, title: String, id: String) -> SettingsTextField {
        let field = SettingsTextField()
        field.text = value
        field.inputAppearance = .underline
        field.autocorrectionType = .no
        field.autocapitalizationType = .none
        field.smartQuotesType = .no
        field.smartDashesType = .no
        field.adjustsFontForContentSizeCategory = true
        field.heightAnchor.constraint(greaterThanOrEqualToConstant: 36).isActive = true
        field.setContentCompressionResistancePriority(.required, for: .vertical)
        field.setContentCompressionResistancePriority(.defaultLow, for: .horizontal)
        field.accessibilityIdentifier = id
        field.accessibilityLabel = title
        field.addAction(UIAction { [weak self] _ in self?.changed() }, for: .editingChanged)
        return field
    }

    private func makeInputSection(title: String, input: UIView) -> SettingsInputSection {
        let section = SettingsInputSection(title: title, input: input)
        section.spacing = 8
        section.isLayoutMarginsRelativeArrangement = true
        section.layoutMargins = .zero
        inputSections.append(section)
        return section
    }

    private func updateFieldLayout() {
        let vertical = traitCollection.preferredContentSizeCategory.isAccessibilityCategory
        titleRow.axis = vertical ? .vertical : .horizontal
        titleRow.alignment = vertical ? .fill : .firstBaseline
        matchingFields.axis = vertical ? .vertical : .horizontal
        flagsWidth?.isActive = !vertical
        for label in [title, resultLabel, deleteButton.titleLabel].compactMap({ $0 }) {
            label.font = .preferredFont(forTextStyle: .subheadline, compatibleWith: traitCollection)
        }
        deleteHeight?.constant = max(44, (deleteButton.titleLabel?.font.lineHeight ?? 20) + 12)
        let captionFont = UIFontMetrics.default.scaledFont(for: .systemFont(ofSize: 13), compatibleWith: traitCollection)
        for section in inputSections {
            section.titleLabel.font = captionFont
        }
        pattern.font = .preferredFont(forTextStyle: .body, compatibleWith: traitCollection)
        replacement?.font = .preferredFont(forTextStyle: .body, compatibleWith: traitCollection)
        flags?.font = .preferredFont(forTextStyle: .body, compatibleWith: traitCollection)
    }
    func applyTheme(_ theme: PageTheme) {
        background.applyTheme(theme)
        title.textColor = UIColor(theme.settings.ink)
        deleteButton.setTitleColor(UIColor(theme.settings.danger), for: .normal)
        deleteButton.setTitleColor(UIColor(theme.settings.muted), for: .disabled)
        for section in inputSections {
            section.applyTheme(theme)
        }
        resultLabel.textColor = UIColor(cardResult?.failed == true ? theme.settings.danger : theme.settings.ink)
    }
    func showResult(_ value: FilmRuleCardResult?, theme: PageTheme) {
        cardResult = value
        resultLabel.text = value.map { "测试结果：\($0.text)" }
        resultLabel.accessibilityLabel = resultLabel.text
        resultLabel.isHidden = value == nil
        resultLabel.textColor = UIColor(value?.failed == true ? theme.settings.danger : theme.settings.ink)
    }
    private func changed() {
        change?(.init(pattern: pattern.text, replace: replacement?.text ?? "", flags: flags?.text ?? ""))
    }
    func setEnabled(_ enabled: Bool) {
        if pattern.isEditable != enabled { pattern.isEditable = enabled }
        if replacement?.isEnabled != enabled { replacement?.isEnabled = enabled }
        if flags?.isEnabled != enabled { flags?.isEnabled = enabled }
        deleteButton.isEnabled = enabled
    }
}

@MainActor
private final class FilmRulePatternTextView: SettingsTextView, UITextViewDelegate {
    var onTextChanged: (() -> Void)?
    private let rows: Int
    private let minimumHeight: CGFloat
    private lazy var minimumHeightConstraint = heightAnchor.constraint(equalToConstant: minimumHeight)

    init(rows: Int) {
        self.rows = rows
        self.minimumHeight = max(92, CGFloat(rows) * 19.5 + 18)
        super.init(frame: .zero, textContainer: nil)
        delegate = self
        minimumHeightConstraint.isActive = true
        updateHeight()
        registerForTraitChanges([UITraitPreferredContentSizeCategory.self]) { (text: FilmRulePatternTextView, _: UITraitCollection) in
            text.updateHeight()
        }
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    override func didMoveToWindow() {
        super.didMoveToWindow()
        updateHeight()
    }

    override func layoutSubviews() {
        super.layoutSubviews()
        let height = max(
            UIFontMetrics.default.scaledValue(for: minimumHeight, compatibleWith: traitCollection),
            ceil((font?.lineHeight ?? 20) * CGFloat(rows) + textContainerInset.top + textContainerInset.bottom)
        )
        if minimumHeightConstraint.constant != height {
            minimumHeightConstraint.constant = height
        }
    }

    private func updateHeight() {
        minimumHeightConstraint.constant = UIFontMetrics.default.scaledValue(for: minimumHeight, compatibleWith: traitCollection)
    }

    func textViewDidChange(_ textView: UITextView) {
        onTextChanged?()
    }
}
#endif
