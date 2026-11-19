#if os(iOS)
import UIKit
import MeowUI

@MainActor
class FilmSettingsFormController: FilmSettingsListController {
    let form = ManagementFormView()
    private let panel = UIView()
    private var formSize = CGSize.zero, contentSize = CGSize.zero
    private var needsFocusedFieldReveal = false
    var formHeader: UIView? { nil }
    override var usesSettingsTable: Bool { false }

    override func viewDidLoad() {
        super.viewDidLoad()
        var top = contentTopAnchor
        if let header = formHeader {
            view.addSubview(header); header.translatesAutoresizingMaskIntoConstraints = false
            NSLayoutConstraint.activate([
                header.topAnchor.constraint(equalTo: contentTopAnchor, constant: SettingsVisualMetrics.contentTopSpacing),
                header.leadingAnchor.constraint(equalTo: view.safeAreaLayoutGuide.leadingAnchor, constant: 24),
                header.trailingAnchor.constraint(equalTo: view.safeAreaLayoutGuide.trailingAnchor, constant: -24),
            ])
            top = header.bottomAnchor
        }
        view.addSubview(panel); panel.translatesAutoresizingMaskIntoConstraints = false
        panel.layer.cornerRadius = 22; panel.clipsToBounds = true
        panel.layer.borderWidth = 0.5
        panel.addSubview(form); form.translatesAutoresizingMaskIntoConstraints = false
        NSLayoutConstraint.activate([
            panel.topAnchor.constraint(equalTo: top, constant: formHeader == nil ? SettingsVisualMetrics.contentTopSpacing : 12),
            panel.leadingAnchor.constraint(equalTo: view.safeAreaLayoutGuide.leadingAnchor, constant: 24),
            panel.trailingAnchor.constraint(equalTo: view.safeAreaLayoutGuide.trailingAnchor, constant: -24),
            panel.bottomAnchor.constraint(equalTo: view.keyboardLayoutGuide.topAnchor, constant: -8),
            form.topAnchor.constraint(equalTo: panel.topAnchor), form.bottomAnchor.constraint(equalTo: panel.bottomAnchor),
            form.leadingAnchor.constraint(equalTo: panel.leadingAnchor), form.trailingAnchor.constraint(equalTo: panel.trailingAnchor),
        ])
        registerForTraitChanges([UITraitPreferredContentSizeCategory.self]) {
            (page: FilmSettingsFormController, _: UITraitCollection) in page.applyTheme()
        }
        applyTheme()
    }

    override func applyTheme() {
        super.applyTheme()
        guard isViewLoaded else { return }
        panel.backgroundColor = UIColor(theme.settings.surface)
        panel.layer.borderColor = UIColor(theme.settings.border).cgColor
        form.applyTheme(theme)
        applyFormTheme(in: form.fields)
        needsFocusedFieldReveal = true; view.setNeedsLayout()
    }

    override func viewDidLayoutSubviews() {
        super.viewDidLayoutSubviews()
        form.layoutIfNeeded()
        let changed = formSize != form.bounds.size || contentSize != form.contentSize
        formSize = form.bounds.size; contentSize = form.contentSize
        guard needsFocusedFieldReveal || changed else { return }
        needsFocusedFieldReveal = false
        if let field = focusedField(in: form.fields) {
            form.scrollRectToVisible(field.convert(field.bounds, to: form).insetBy(dx: 0, dy: -8), animated: false)
        }
    }

    private func focusedField(in view: UIView) -> UIView? {
        if view.isFirstResponder { return view }
        for child in view.subviews {
            if let field = focusedField(in: child) { return field }
        }
        return nil
    }

    private func applyFormTheme(in view: UIView) {
        let settings = theme.settings
        if let field = view as? UITextField { settings.apply(to: field) }
        else if let field = view as? UITextView { settings.apply(to: field) }
        else if let label = view as? UILabel { label.textColor = UIColor(settings.muted) }
        else if let button = view as? UIButton {
            if button.configuration != nil {
                button.configuration?.baseForegroundColor = UIColor(
                    button.tag == 1 ? settings.danger : settings.accent)
            }
        } else {
            if view.subviews.isEmpty { view.backgroundColor = UIColor(settings.separator) }
            for child in view.subviews { applyFormTheme(in: child) }
        }
    }

    func text(_ title: String, id: String, value: String = "", in container: UIStackView? = nil,
              changed: @escaping (String) -> Void = { _ in }) -> UITextField {
        let field = form.text(title, value: value, id: id, theme: theme, in: container, changed: changed) as! UITextField
        field.addAction(UIAction { [weak self] _ in
            self?.needsFocusedFieldReveal = true; self?.view.setNeedsLayout()
        }, for: .editingDidBegin)
        return field
    }

    func option(_ title: String, id: String, disclosure: Bool = false, destructive: Bool = false,
                action: @escaping () -> Void) -> UIButton {
        let button = FilmSettingsOptionButton(frame: .zero)
        var configuration = UIButton.Configuration.plain()
        configuration.title = title
        configuration.titleLineBreakMode = .byWordWrapping
        configuration.contentInsets = .init(top: 8, leading: 12, bottom: 8, trailing: 12)
        configuration.titleTextAttributesTransformer = UIConfigurationTextAttributesTransformer { [weak button] attributes in
            var attributes = attributes
            attributes.font = UIFont.preferredFont(forTextStyle: .subheadline, compatibleWith: button?.traitCollection)
            return attributes
        }
        if disclosure {
            configuration.image = UIImage(systemName: "chevron.up.chevron.down",
                withConfiguration: UIImage.SymbolConfiguration(pointSize: 9, weight: .regular))
            configuration.imagePlacement = .trailing; configuration.imagePadding = 8
        }
        button.configuration = configuration; button.tag = destructive ? 1 : 0
        button.titleLabel?.numberOfLines = 0; button.titleLabel?.adjustsFontForContentSizeCategory = true
        button.contentHorizontalAlignment = .leading
        button.setContentCompressionResistancePriority(.required, for: .vertical)
        button.accessibilityIdentifier = id; button.accessibilityLabel = title
        button.heightAnchor.constraint(greaterThanOrEqualToConstant: 44).isActive = true
        button.addAction(UIAction { _ in action() }, for: .touchUpInside)
        return button
    }

    func optionRow(_ controls: [UIView]) -> UIStackView {
        let row = UIStackView(arrangedSubviews: controls)
        row.spacing = 8; row.isLayoutMarginsRelativeArrangement = true
        row.layoutMargins = .init(top: 6, left: 14, bottom: 6, right: 14)
        return row
    }
}

@MainActor
private final class FilmSettingsOptionButton: UIButton {
    private var fittingHeight: CGFloat = 0

    override var intrinsicContentSize: CGSize {
        let size = super.intrinsicContentSize
        guard let titleLabel, titleLabel.preferredMaxLayoutWidth > 0 else { return size }
        let titleSize = titleLabel.sizeThatFits(.init(width: titleLabel.preferredMaxLayoutWidth, height: .greatestFiniteMagnitude))
        let insets = configuration?.contentInsets ?? .zero
        let height = max(titleSize.height, configuration?.image?.size.height ?? 0) + insets.top + insets.bottom
        return .init(width: size.width, height: max(size.height, ceil(height)))
    }

    override func layoutSubviews() {
        let insets = configuration?.contentInsets ?? .zero
        let imageWidth = configuration?.image.map { $0.size.width + (configuration?.imagePadding ?? 0) } ?? 0
        let width = max(0, bounds.width - insets.leading - insets.trailing - imageWidth)
        if titleLabel?.preferredMaxLayoutWidth != width {
            titleLabel?.preferredMaxLayoutWidth = width; invalidateIntrinsicContentSize()
        }
        super.layoutSubviews()
        let height = intrinsicContentSize.height
        if fittingHeight != height { fittingHeight = height; invalidateIntrinsicContentSize() }
    }
}
#endif
