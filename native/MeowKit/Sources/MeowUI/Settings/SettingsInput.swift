#if os(iOS)
import UIKit

public enum SettingsInputAppearance: Equatable, Sendable {
    case plain, underline, outlined
}

/// Input geometry belongs to the field, so theme and size changes keep one shell.
@MainActor
public final class SettingsTextField: UITextField, PageThemeable {
    public var inputAppearance: SettingsInputAppearance = .outlined {
        didSet { updateAppearance(); setNeedsLayout() }
    }
    private var inputTheme = PageTheme().settings
    private let underline = CALayer()

    public override init(frame: CGRect) {
        super.init(frame: frame)
        layer.addSublayer(underline)
    }

    public required init?(coder: NSCoder) {
        super.init(coder: coder)
        layer.addSublayer(underline)
    }

    public override var intrinsicContentSize: CGSize {
        let size = super.intrinsicContentSize
        return CGSize(width: size.width, height: max(36, (font?.lineHeight ?? 20) + 16))
    }

    public override func point(inside point: CGPoint, with event: UIEvent?) -> Bool {
        bounds.insetBy(dx: -max(0, (44 - bounds.width) / 2),
            dy: -max(0, (44 - bounds.height) / 2)).contains(point)
    }

    public override func textRect(forBounds bounds: CGRect) -> CGRect {
        super.textRect(forBounds: bounds).insetBy(dx: inputAppearance == .outlined ? 12 : 0, dy: 0)
    }

    public override func editingRect(forBounds bounds: CGRect) -> CGRect {
        super.editingRect(forBounds: bounds).insetBy(dx: inputAppearance == .outlined ? 12 : 0, dy: 0)
    }

    public override func layoutSubviews() {
        super.layoutSubviews()
        updateAppearance()
    }

    func applyInputTheme(_ theme: SettingsTheme) {
        inputTheme = theme; updateAppearance()
    }

    public func applyTheme(_ theme: PageTheme) {
        theme.settings.apply(to: self)
    }

    private func updateAppearance() {
        backgroundColor = inputAppearance == .outlined ? UIColor(inputTheme.input) : .clear
        layer.cornerRadius = inputAppearance == .outlined ? min(bounds.width, bounds.height) / 2 : 0
        layer.borderWidth = inputAppearance == .outlined ? 1 : 0
        layer.borderColor = UIColor(inputTheme.inputBorder).cgColor
        underline.isHidden = inputAppearance != .underline
        underline.backgroundColor = UIColor(inputTheme.separator).cgColor
        underline.frame = CGRect(x: bounds.minX, y: bounds.maxY - SettingsVisualMetrics.separatorHeight,
            width: bounds.width, height: SettingsVisualMetrics.separatorHeight)
    }
}

@MainActor
public final class SettingsTextView: UITextView, PageThemeable, UITextViewDelegate {
    public var inputAppearance: SettingsInputAppearance = .outlined {
        didSet { updateAppearance(); setNeedsLayout() }
    }
    public var onTextChanged: ((String) -> Void)? {
        didSet {
            if onTextChanged != nil, delegate == nil { delegate = self }
        }
    }
    private var inputTheme = PageTheme().settings
    private let underline = CALayer()
    private var minimumRows: Int?
    private var baseMinimumHeight: CGFloat = 0
    private var minimumHeightConstraint: NSLayoutConstraint?

    public override init(frame: CGRect, textContainer: NSTextContainer?) {
        super.init(frame: frame, textContainer: textContainer)
        layer.addSublayer(underline)
        registerForTraitChanges([UITraitPreferredContentSizeCategory.self]) { (text: SettingsTextView, _: UITraitCollection) in
            text.updateMinimumHeight()
        }
    }

    public required init?(coder: NSCoder) {
        super.init(coder: coder)
        layer.addSublayer(underline)
    }

    public func configureMinimumRows(_ rows: Int, minimumHeight: CGFloat) {
        minimumRows = rows
        baseMinimumHeight = minimumHeight
        if minimumHeightConstraint == nil {
            let constraint = heightAnchor.constraint(equalToConstant: minimumHeight)
            constraint.isActive = true
            minimumHeightConstraint = constraint
        }
        updateMinimumHeight()
    }

    public override func didMoveToWindow() {
        super.didMoveToWindow()
        updateMinimumHeight()
    }

    public override func layoutSubviews() {
        super.layoutSubviews()
        updateAppearance()
        let vertical = max(10, (font?.lineHeight ?? 20) / 2)
        let horizontal: CGFloat = inputAppearance == .outlined ? 12 : 0
        let inset = UIEdgeInsets(top: vertical, left: horizontal, bottom: vertical, right: horizontal)
        if textContainerInset != inset { textContainerInset = inset }
        textContainer.lineFragmentPadding = 0
        updateMinimumHeight()
    }

    private func updateMinimumHeight() {
        guard let minimumRows, let minimumHeightConstraint else { return }
        let height = max(
            UIFontMetrics.default.scaledValue(for: baseMinimumHeight, compatibleWith: traitCollection),
            ceil((font?.lineHeight ?? 20) * CGFloat(minimumRows) + textContainerInset.top + textContainerInset.bottom)
        )
        if minimumHeightConstraint.constant != height {
            minimumHeightConstraint.constant = height
        }
    }

    public func textViewDidChange(_ textView: UITextView) {
        onTextChanged?(textView.text)
    }

    func applyInputTheme(_ theme: SettingsTheme) {
        inputTheme = theme; updateAppearance()
    }

    public func applyTheme(_ theme: PageTheme) {
        theme.settings.apply(to: self)
    }

    private func updateAppearance() {
        backgroundColor = inputAppearance == .outlined ? UIColor(inputTheme.input) : .clear
        layer.cornerRadius = 0
        layer.borderWidth = inputAppearance == .outlined ? 1 : 0
        layer.borderColor = UIColor(inputTheme.inputBorder).cgColor
        underline.isHidden = inputAppearance != .underline
        underline.backgroundColor = UIColor(inputTheme.separator).cgColor
        underline.frame = CGRect(x: bounds.minX, y: bounds.maxY - SettingsVisualMetrics.separatorHeight,
            width: bounds.width, height: SettingsVisualMetrics.separatorHeight)
    }
}

/// A field's title and input share the same content edge inside forms and dialogs.
@MainActor
public final class SettingsInputSection: UIStackView, PageThemeable {
    public let titleLabel = UILabel()
    public let input: UIView

    public init(title: String, input: UIView) {
        self.input = input
        super.init(frame: .zero)
        axis = .vertical; spacing = 5
        titleLabel.text = title; titleLabel.numberOfLines = 0
        titleLabel.font = .preferredFont(forTextStyle: .subheadline)
        titleLabel.adjustsFontForContentSizeCategory = true
        titleLabel.textColor = UIColor(PageTheme().settings.muted)
        titleLabel.setContentCompressionResistancePriority(.required, for: .vertical)
        input.setContentCompressionResistancePriority(.required, for: .vertical)
        addArrangedSubview(titleLabel); addArrangedSubview(input)
    }

    @available(*, unavailable)
    public required init(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    public override func point(inside point: CGPoint, with event: UIEvent?) -> Bool {
        super.point(inside: point, with: event) ||
            input.point(inside: input.convert(point, from: self), with: event)
    }

    public func applyTheme(_ theme: PageTheme) {
        titleLabel.textColor = UIColor(theme.settings.muted)
        if let field = input as? UITextField { theme.settings.apply(to: field) }
        if let field = input as? UITextView { theme.settings.apply(to: field) }
    }
}
#endif
