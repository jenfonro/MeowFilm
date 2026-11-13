#if os(iOS)
import UIKit
import MeowUI

/// One rule's fields stay together inside the page, including at accessibility sizes.
@MainActor
final class FilmRuleEditorView: UIStackView {
    private(set) var pattern: UITextView!
    private(set) var replacement: UITextField?
    private let preservedFlags: String
    private(set) var deleteButton: PageHeaderTextButton!
    var change: ((FilmMatchRule) -> Void)?
    var delete: (() -> Void)?

    init(rule: FilmMatchRule, group: FilmRuleGroup, index: Int,
         theme: PageTheme, form: ManagementFormView) {
        let id = "film.rules.\(group.rawValue).\(index)"
        // `flags` is intentionally not a presentation field. An empty value
        // remains valid in the wire contract and the runtime resolves it to
        // its existing default (`i`); preserving the received value also
        // prevents an unrelated edit from rewriting a stored rule.
        preservedFlags = rule.flags
        super.init(frame: .zero)
        axis = .vertical; spacing = 0
        let title = UILabel()
        title.text = "规则 \(index + 1)"
        title.font = .preferredFont(forTextStyle: .subheadline)
        title.adjustsFontForContentSizeCategory = true
        title.textColor = UIColor(theme.settings.ink)
        let remove = PageHeaderTextButton(title: "删除") { }
        deleteButton = remove
        remove.setTitleColor(UIColor(theme.settings.danger), for: .normal)
        remove.setTitleColor(UIColor(theme.settings.muted), for: .disabled)
        remove.titleLabel?.font = .preferredFont(forTextStyle: .subheadline)
        remove.titleLabel?.adjustsFontForContentSizeCategory = true
        remove.accessibilityLabel = "删除规则 \(index + 1)"
        remove.accessibilityIdentifier = id + ".delete"
        remove.heightAnchor.constraint(greaterThanOrEqualToConstant: 44).isActive = true
        remove.addAction(UIAction { [weak self] _ in self?.delete?() }, for: .touchUpInside)
        let heading = UIStackView(arrangedSubviews: [title, UIView(), remove])
        heading.axis = .horizontal
        heading.alignment = .center
        heading.heightAnchor.constraint(greaterThanOrEqualToConstant: 44).isActive = true
        addArrangedSubview(heading)

        pattern = form.text("匹配规则", value: rule.pattern, rows: 4,
            id: id + ".pattern", theme: theme, in: self) { [weak self] _ in
                self?.changed()
            } as! UITextView
        if !group.isClean {
            replacement = form.text("替换内容", value: rule.replace,
                id: id + ".replace", theme: theme, in: self) { [weak self] _ in self?.changed() } as! UITextField
        }
    }
    required init(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }
    private func changed() {
        change?(.init(pattern: pattern.text, replace: replacement?.text ?? "", flags: preservedFlags))
    }
    func setEnabled(_ enabled: Bool) {
        pattern.isEditable = enabled; replacement?.isEnabled = enabled; deleteButton.isEnabled = enabled
    }
}
#endif
