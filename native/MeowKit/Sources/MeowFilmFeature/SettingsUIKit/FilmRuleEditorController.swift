#if os(iOS)
import UIKit
import MeowUI

/// One rule's fields stay together inside the page, including at accessibility sizes.
@MainActor
final class FilmRuleEditorView: UIStackView, UITextViewDelegate {
    let pattern = UITextView()
    let replacement: UITextField
    let flags: UITextField
    let delete = UIButton(type: .system)
    private let options = UIStackView()
    private var flagsWidth: NSLayoutConstraint?
    var change: ((FilmMatchRule) -> Void)?
    var resize: (() -> Void)?

    init(rule: FilmMatchRule, group: FilmRuleGroup, index: Int, theme: PageTheme) {
        let id = "film.rules.\(group.rawValue).\(index)"
        replacement = filmTextField("replace", id: id + ".replace", value: rule.replace)
        flags = filmTextField("flags", id: id + ".flags", value: rule.flags)
        super.init(frame: .zero)
        axis = .vertical; spacing = 8
        let patternLabel = UILabel()
        patternLabel.text = "pattern"; patternLabel.font = .preferredFont(forTextStyle: .caption1)
        patternLabel.adjustsFontForContentSizeCategory = true; patternLabel.textColor = UIColor(theme.settings.muted)
        addArrangedSubview(patternLabel)
        pattern.text = rule.pattern
        pattern.accessibilityLabel = "pattern"; pattern.accessibilityIdentifier = id + ".pattern"
        pattern.font = .preferredFont(forTextStyle: .body)
        pattern.adjustsFontForContentSizeCategory = true
        pattern.isScrollEnabled = false; pattern.backgroundColor = .clear
        pattern.autocorrectionType = .no; pattern.autocapitalizationType = .none
        pattern.smartQuotesType = .no; pattern.smartDashesType = .no
        pattern.heightAnchor.constraint(greaterThanOrEqualToConstant: 96).isActive = true
        pattern.delegate = self
        pattern.textColor = UIColor(theme.settings.ink)
        addArrangedSubview(pattern)
        if !group.isClean {
            options.spacing = 12; options.distribution = .fill
            for (name, field) in [("replace", replacement), ("flags", flags)] {
                field.textColor = UIColor(theme.settings.ink)
                field.addAction(UIAction { [weak self] _ in self?.changed() }, for: .editingChanged)
                let label = UILabel()
                label.text = name; label.font = .preferredFont(forTextStyle: .caption1)
                label.adjustsFontForContentSizeCategory = true; label.textColor = UIColor(theme.settings.muted)
                let stack = UIStackView(arrangedSubviews: [label, field])
                stack.axis = .vertical; stack.spacing = 4
                if name == "flags" {
                    flagsWidth = stack.widthAnchor.constraint(equalToConstant: 72)
                    flagsWidth?.isActive = true
                } else {
                    stack.setContentHuggingPriority(.defaultLow, for: .horizontal)
                    field.setContentHuggingPriority(.defaultLow, for: .horizontal)
                }
                options.addArrangedSubview(stack)
            }
            addArrangedSubview(options)
        }
        delete.setTitle("删除", for: .normal)
        delete.tintColor = UIColor(theme.settings.danger)
        delete.accessibilityIdentifier = id + ".delete"
        delete.titleLabel?.font = .preferredFont(forTextStyle: .body)
        delete.titleLabel?.adjustsFontForContentSizeCategory = true
        delete.heightAnchor.constraint(greaterThanOrEqualToConstant: 44).isActive = true
        addArrangedSubview(delete)
    }
    required init(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }
    override func layoutSubviews() {
        let axis: NSLayoutConstraint.Axis = bounds.width < 300 || traitCollection.preferredContentSizeCategory.isAccessibilityCategory
            ? .vertical : .horizontal
        if options.axis != axis {
            flagsWidth?.isActive = axis == .horizontal
            options.axis = axis
        }
        super.layoutSubviews()
    }
    func textViewDidChange(_ textView: UITextView) { changed(); resize?() }
    private func changed() {
        change?(.init(pattern: pattern.text, replace: replacement.text ?? "", flags: flags.text ?? ""))
    }
    func setEnabled(_ enabled: Bool) {
        pattern.isEditable = enabled; replacement.isEnabled = enabled; flags.isEnabled = enabled; delete.isEnabled = enabled
    }
}
#endif
