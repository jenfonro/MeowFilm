#if os(iOS)
import UIKit
import MeowUI

/// One rule's fields stay together inside the page, including at accessibility sizes.
@MainActor
final class FilmRuleEditorView: UIStackView {
    private(set) var pattern: UITextView!
    private(set) var replacement: UITextField?
    private(set) var flags: UITextField?
    private(set) var deleteButton: PageHeaderTextButton!
    let resultLabel = UILabel()
    private let title = UILabel()
    private let titleRow = UIStackView()
    private let matchingFields = UIStackView()
    private var flagsWidth: NSLayoutConstraint?
    private var deleteHeight: NSLayoutConstraint?
    private var background: UIView?
    private var cardResult: FilmRuleCardResult?
    var change: ((FilmMatchRule) -> Void)?
    var delete: (() -> Void)?

    init(rule: FilmMatchRule, group: FilmRuleGroup, index: Int,
         theme: PageTheme, form: ManagementFormView) {
        let id = "film.rules.\(group.rawValue).\(index)"
        super.init(frame: .zero)
        accessibilityIdentifier = id
        axis = .vertical; spacing = 12
        isLayoutMarginsRelativeArrangement = true
        layoutMargins = .init(top: 12, left: 20, bottom: 16, right: 20)
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
        titleRow.axis = .horizontal; titleRow.alignment = .top; titleRow.spacing = 12
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
        let heading = UIStackView(arrangedSubviews: [titleRow, remove])
        heading.axis = .horizontal
        heading.spacing = 12; heading.alignment = .top
        heading.heightAnchor.constraint(greaterThanOrEqualToConstant: 44).isActive = true
        addArrangedSubview(heading)

        pattern = form.text("pattern", value: rule.pattern, rows: 4,
            id: id + ".pattern", theme: theme, in: self) { [weak self] _ in
                self?.changed()
            } as! UITextView
        if !group.isClean {
            let replaceColumn = UIStackView(); replaceColumn.axis = .vertical
            let flagsColumn = UIStackView(); flagsColumn.axis = .vertical
            replacement = form.text("replace", value: rule.replace, id: id + ".replace",
                theme: theme, in: replaceColumn) { [weak self] _ in self?.changed() } as! UITextField
            flags = form.text("flags", value: rule.flags, id: id + ".flags",
                theme: theme, in: flagsColumn) { [weak self] _ in self?.changed() } as! UITextField
            matchingFields.axis = .horizontal; matchingFields.alignment = .fill; matchingFields.spacing = 12
            matchingFields.addArrangedSubview(replaceColumn); matchingFields.addArrangedSubview(flagsColumn)
            flagsWidth = flagsColumn.widthAnchor.constraint(equalToConstant: 76)
            addArrangedSubview(matchingFields)
        }
        registerForTraitChanges([UITraitPreferredContentSizeCategory.self]) {
            (editor: FilmRuleEditorView, _: UITraitCollection) in editor.updateFieldLayout()
        }
        updateFieldLayout(); applyTheme(theme)
    }
    required init(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }
    override func didMoveToWindow() { super.didMoveToWindow(); updateFieldLayout() }
    private func updateFieldLayout() {
        let vertical = traitCollection.preferredContentSizeCategory.isAccessibilityCategory
        titleRow.axis = vertical ? .vertical : .horizontal
        matchingFields.axis = vertical ? .vertical : .horizontal
        flagsWidth?.isActive = !vertical
        for label in [title, resultLabel, deleteButton.titleLabel].compactMap({ $0 }) {
            label.font = .preferredFont(forTextStyle: .subheadline, compatibleWith: traitCollection)
        }
        deleteHeight?.constant = max(44, (deleteButton.titleLabel?.font.lineHeight ?? 20) + 12)
        pattern.font = .preferredFont(forTextStyle: .body, compatibleWith: traitCollection)
        replacement?.font = .preferredFont(forTextStyle: .body, compatibleWith: traitCollection)
        flags?.font = .preferredFont(forTextStyle: .body, compatibleWith: traitCollection)
    }
    func applyTheme(_ theme: PageTheme) {
        background?.removeFromSuperview()
        let surface = SettingsRowBackgroundView(theme: theme, first: true, last: true)
        background = surface
        insertSubview(surface, at: 0); surface.translatesAutoresizingMaskIntoConstraints = false
        NSLayoutConstraint.activate([
            surface.topAnchor.constraint(equalTo: topAnchor), surface.bottomAnchor.constraint(equalTo: bottomAnchor),
            surface.leadingAnchor.constraint(equalTo: leadingAnchor), surface.trailingAnchor.constraint(equalTo: trailingAnchor),
        ])
        title.textColor = UIColor(theme.settings.ink)
        deleteButton.setTitleColor(UIColor(theme.settings.danger), for: .normal)
        deleteButton.setTitleColor(UIColor(theme.settings.muted), for: .disabled)
        theme.settings.apply(to: pattern)
        for field in [replacement, flags].compactMap({ $0 }) { theme.settings.apply(to: field) }
        let inputs: [UIView?] = [pattern, replacement, flags]
        for input in inputs.compactMap({ $0 }) {
            for case let label as UILabel in (input.superview as? UIStackView)?.arrangedSubviews ?? [] {
                label.textColor = UIColor(theme.settings.muted)
            }
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
#endif
