#if os(iOS)
import UIKit

/// Scrollable management/editor form using explicit `SettingsDividerView` and `PageThemeable` tracking
/// (zero `subviews` crawling).
@MainActor
public final class ManagementFormView: UIScrollView, PageThemeable {
    public let fields = UIStackView()
    private lazy var fieldsTop = fields.topAnchor.constraint(equalTo: contentLayoutGuide.topAnchor, constant: 12)
    private struct WeakThemeable { weak var value: (any PageThemeable)? }
    private var themeables: [WeakThemeable] = []
    private var checkRows: [(row: UIStackView, spacer: UIView)] = []

    public var boxedInputs = false {
        didSet {
            fields.spacing = boxedInputs ? 14 : 0
            fields.isLayoutMarginsRelativeArrangement = boxedInputs
            fields.layoutMargins = boxedInputs ? .init(top: 16, left: 16, bottom: 16, right: 16) : .zero
            fields.layer.cornerRadius = boxedInputs ? 22 : 18
        }
    }

    public override init(frame: CGRect) {
        super.init(frame: frame)
        keyboardDismissMode = .interactive
        alwaysBounceVertical = true
        contentInsetAdjustmentBehavior = .never
        fields.axis = .vertical
        fields.layer.cornerRadius = 18
        fields.clipsToBounds = true
        addSubview(fields)
        fields.translatesAutoresizingMaskIntoConstraints = false
        NSLayoutConstraint.activate([
            fieldsTop,
            fields.bottomAnchor.constraint(equalTo: contentLayoutGuide.bottomAnchor, constant: -12),
            fields.leadingAnchor.constraint(equalTo: contentLayoutGuide.leadingAnchor, constant: 12),
            fields.trailingAnchor.constraint(equalTo: contentLayoutGuide.trailingAnchor, constant: -12),
            fields.widthAnchor.constraint(equalTo: frameLayoutGuide.widthAnchor, constant: -24),
        ])
        registerForTraitChanges([UITraitPreferredContentSizeCategory.self]) { (form: ManagementFormView, _: UITraitCollection) in
            form.updateCheckRows()
        }
    }

    public convenience init(contentTopSpacing: CGFloat) {
        self.init(frame: .zero)
        fieldsTop.constant = contentTopSpacing
    }

    @available(*, unavailable)
    public required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    public override func didMoveToWindow() {
        super.didMoveToWindow()
        updateCheckRows()
    }

    public func clear() {
        fields.arrangedSubviews.forEach { $0.removeFromSuperview() }
        themeables.removeAll()
        checkRows.removeAll()
    }

    public func registerThemeable(_ item: any PageThemeable) {
        guard !themeables.contains(where: { $0.value === item }) else { return }
        themeables.append(WeakThemeable(value: item))
    }

    @discardableResult
    public func text(_ label: String, value: String, rows: Int = 1, id: String,
                     theme: PageTheme? = nil, appearance: SettingsInputAppearance = .plain,
                     in container: UIStackView? = nil,
                     changed: @escaping (String) -> Void) -> UIView {
        let input: UIView
        if rows > 1 {
            let text = SettingsTextView()
            text.configureMinimumRows(
                rows,
                minimumHeight: boxedInputs ? max(92, CGFloat(rows) * 19.5 + 18) : max(72, CGFloat(rows) * 23 + 24)
            )
            text.text = value; text.onTextChanged = changed
            text.inputAppearance = appearance
            text.autocorrectionType = .no; text.autocapitalizationType = .none
            text.smartQuotesType = .no; text.smartDashesType = .no
            text.font = UIFontMetrics.default.scaledFont(for: .systemFont(ofSize: boxedInputs ? 13 : 17), compatibleWith: traitCollection)
            text.adjustsFontForContentSizeCategory = true; text.backgroundColor = .clear
            text.textContainer.lineFragmentPadding = 0
            input = text
        } else {
            let text = SettingsTextField()
            text.text = value
            text.font = UIFontMetrics.default.scaledFont(for: .systemFont(ofSize: boxedInputs ? 13 : 17), compatibleWith: traitCollection)
            text.adjustsFontForContentSizeCategory = true
            text.inputAppearance = appearance
            text.autocorrectionType = .no; text.autocapitalizationType = .none
            text.smartQuotesType = .no; text.smartDashesType = .no
            text.heightAnchor.constraint(greaterThanOrEqualToConstant: 36).isActive = true
            text.setContentCompressionResistancePriority(.required, for: .vertical)
            text.setContentCompressionResistancePriority(.defaultLow, for: .horizontal)
            text.addAction(UIAction { [weak text] _ in changed(text?.text ?? "") }, for: .editingChanged)
            input = text
        }
        let section = SettingsInputSection(title: label, input: input)
        section.spacing = boxedInputs ? 8 : 5
        section.isLayoutMarginsRelativeArrangement = true
        section.layoutMargins = boxedInputs ? .zero : .init(top: 11, left: 14, bottom: 10, right: 14)
        let resolvedSettings = (theme ?? PageTheme(dark: traitCollection.userInterfaceStyle == .dark)).settings
        section.titleLabel.textColor = UIColor(resolvedSettings.muted)
        section.titleLabel.font = UIFontMetrics.default.scaledFont(for: .systemFont(ofSize: boxedInputs ? 13 : 12), compatibleWith: traitCollection)
        if let text = input as? UITextField { resolvedSettings.apply(to: text) }
        if let text = input as? UITextView { resolvedSettings.apply(to: text) }
        input.accessibilityIdentifier = id; input.accessibilityLabel = label
        if let container { registerThemeable(section); addSection(section, to: container, theme: theme) }
        else { addSection(section) }
        return input
    }

    public func addSection(_ section: UIView) {
        if let themeable = section as? any PageThemeable { registerThemeable(themeable) }
        fields.addArrangedSubview(section)
        let separator = SettingsDividerView()
        registerThemeable(separator)
        fields.addArrangedSubview(separator)
        if boxedInputs {
            fields.setCustomSpacing(fields.spacing / 2, after: section)
            fields.setCustomSpacing(fields.spacing / 2, after: separator)
        }
        updateCheckRows()
    }

    private func addSection(_ section: UIView, to container: UIStackView, theme: PageTheme?) {
        if !container.arrangedSubviews.isEmpty, !boxedInputs {
            let separator = SettingsDividerView(theme: theme)
            registerThemeable(separator)
            container.addArrangedSubview(separator)
        }
        container.addArrangedSubview(section)
    }

    public func addCheckRow(_ buttons: [UIButton], spacing: CGFloat = 14) {
        let spacer = UIView()
        let row = UIStackView(arrangedSubviews: buttons + [spacer])
        row.spacing = spacing
        for button in buttons {
            if let themeable = button as? any PageThemeable { registerThemeable(themeable) }
        }
        checkRows.append((row: row, spacer: spacer))
        addSection(row)
    }

    private func updateCheckRows() {
        let vertical = traitCollection.preferredContentSizeCategory.isAccessibilityCategory
        for entry in checkRows {
            entry.row.axis = vertical ? .vertical : .horizontal
            entry.spacer.isHidden = vertical
        }
    }

    public static func check(_ title: String, value: Bool, round: Bool = false, changed: @escaping (Bool) -> Void) -> UIButton {
        let button = ManagementCheckButton(round: round)
        button.isSelected = value
        button.contentHorizontalAlignment = .leading
        button.setTitle("  " + title, for: .normal)
        button.setContentHuggingPriority(.defaultHigh, for: .horizontal)
        button.setContentCompressionResistancePriority(.defaultHigh, for: .horizontal)
        button.setContentCompressionResistancePriority(.required, for: .vertical)
        button.heightAnchor.constraint(greaterThanOrEqualToConstant: 32).isActive = true
        button.accessibilityLabel = title
        button.accessibilityValue = value ? "已勾选" : "未勾选"
        button.addAction(UIAction { [weak button] _ in
            guard let button else { return }
            button.isSelected.toggle()
            button.accessibilityValue = button.isSelected ? "已勾选" : "未勾选"
            changed(button.isSelected)
        }, for: .touchUpInside)
        return button
    }

    public func applyTheme(_ theme: PageTheme) {
        let settings = theme.settings
        fields.backgroundColor = UIColor(settings.surface)
        tintColor = UIColor(settings.accent)
        themeables.removeAll { $0.value == nil }
        for entry in themeables {
            entry.value?.applyTheme(theme)
        }
    }
}

@MainActor
private final class ManagementCheckButton: UIButton, PageThemeable {
    private let shape: String, size: CGFloat, fontSize: CGFloat
    private var currentTheme: PageTheme?
    init(round: Bool) {
        shape = round ? "circle" : "square"; size = round ? 20 : 18; fontSize = round ? 14 : 13
        super.init(frame: .zero)
        titleLabel?.numberOfLines = 0; titleLabel?.lineBreakMode = .byWordWrapping; titleLabel?.adjustsFontForContentSizeCategory = true
        registerForTraitChanges([UITraitUserInterfaceStyle.self, UITraitPreferredContentSizeCategory.self]) { (button: ManagementCheckButton, _: UITraitCollection) in button.applyStyle() }
        applyStyle()
    }
    @available(*, unavailable) required init?(coder: NSCoder) { fatalError() }
    override func didMoveToWindow() { super.didMoveToWindow(); if window != nil { applyStyle() } }
    override var intrinsicContentSize: CGSize {
        let size = super.intrinsicContentSize
        guard let titleLabel, titleLabel.preferredMaxLayoutWidth > 0 else { return size }
        let titleSize = titleLabel.sizeThatFits(.init(width: titleLabel.preferredMaxLayoutWidth, height: .greatestFiniteMagnitude))
        return .init(width: size.width, height: max(size.height, ceil(titleSize.height)))
    }
    override func layoutSubviews() {
        let width = max(0, contentRect(forBounds: bounds).width - (currentImage?.size.width ?? 0))
        if titleLabel?.preferredMaxLayoutWidth != width {
            titleLabel?.preferredMaxLayoutWidth = width; invalidateIntrinsicContentSize()
        }
        super.layoutSubviews()
    }
    func applyTheme(_ theme: PageTheme) {
        currentTheme = theme
        applyStyle()
    }
    private func applyStyle() {
        let settings = (currentTheme ?? PageTheme(dark: traitCollection.userInterfaceStyle == .dark)).settings
        titleLabel?.font = UIFontMetrics.default.scaledFont(for: .systemFont(ofSize: fontSize), compatibleWith: traitCollection)
        let config = UIImage.SymbolConfiguration(pointSize: size, weight: .regular)
        setImage(UIImage(systemName: shape, withConfiguration: config)?.withTintColor(UIColor(settings.muted), renderingMode: .alwaysOriginal), for: .normal)
        setImage(UIImage(systemName: "checkmark.\(shape).fill", withConfiguration: config)?.withTintColor(UIColor(settings.accent), renderingMode: .alwaysOriginal), for: .selected)
        setTitleColor(UIColor(settings.ink), for: .normal); setTitleColor(UIColor(settings.ink), for: .selected)
        invalidateIntrinsicContentSize()
    }
}
#endif
