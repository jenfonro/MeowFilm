#if os(iOS)
import UIKit

public enum SettingsInputAppearance: Equatable, Sendable {
    case plain, underline, outlined
}

/// Input geometry belongs to the field, so theme and size changes keep one shell.
@MainActor
open class SettingsTextField: UITextField {
    public var inputAppearance: SettingsInputAppearance = .outlined {
        didSet { updateAppearance(); setNeedsLayout() }
    }
    private var inputTheme = SettingsTheme(dark: false)
    private let underline = CALayer()

    public override init(frame: CGRect) {
        super.init(frame: frame)
        layer.addSublayer(underline)
    }

    public required init?(coder: NSCoder) {
        super.init(coder: coder)
        layer.addSublayer(underline)
    }

    open override var intrinsicContentSize: CGSize {
        let size = super.intrinsicContentSize
        return CGSize(width: size.width, height: max(36, (font?.lineHeight ?? 20) + 16))
    }

    open override func point(inside point: CGPoint, with event: UIEvent?) -> Bool {
        bounds.insetBy(dx: -max(0, (44 - bounds.width) / 2),
            dy: -max(0, (44 - bounds.height) / 2)).contains(point)
    }

    open override func textRect(forBounds bounds: CGRect) -> CGRect {
        super.textRect(forBounds: bounds).insetBy(dx: inputAppearance == .outlined ? 12 : 0, dy: 0)
    }

    open override func editingRect(forBounds bounds: CGRect) -> CGRect {
        super.editingRect(forBounds: bounds).insetBy(dx: inputAppearance == .outlined ? 12 : 0, dy: 0)
    }

    open override func layoutSubviews() {
        super.layoutSubviews()
        updateAppearance()
    }

    func applyInputTheme(_ theme: SettingsTheme) {
        inputTheme = theme; updateAppearance()
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
open class SettingsTextView: UITextView {
    public var inputAppearance: SettingsInputAppearance = .outlined {
        didSet { updateAppearance(); setNeedsLayout() }
    }
    private var inputTheme = SettingsTheme(dark: false)
    private let underline = CALayer()

    public override init(frame: CGRect, textContainer: NSTextContainer?) {
        super.init(frame: frame, textContainer: textContainer)
        layer.addSublayer(underline)
    }

    public required init?(coder: NSCoder) {
        super.init(coder: coder)
        layer.addSublayer(underline)
    }

    open override func layoutSubviews() {
        super.layoutSubviews()
        updateAppearance()
        let vertical = max(10, (font?.lineHeight ?? 20) / 2)
        let horizontal: CGFloat = inputAppearance == .outlined ? 12 : 0
        let inset = UIEdgeInsets(top: vertical, left: horizontal, bottom: vertical, right: horizontal)
        if textContainerInset != inset { textContainerInset = inset }
        textContainer.lineFragmentPadding = 0
    }

    func applyInputTheme(_ theme: SettingsTheme) {
        inputTheme = theme; updateAppearance()
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
public final class SettingsInputSection: UIStackView {
    public let titleLabel = UILabel()
    public let input: UIView

    public init(title: String, input: UIView) {
        self.input = input
        super.init(frame: .zero)
        axis = .vertical; spacing = 5
        titleLabel.text = title; titleLabel.numberOfLines = 0
        titleLabel.font = .preferredFont(forTextStyle: .subheadline)
        titleLabel.adjustsFontForContentSizeCategory = true
        titleLabel.textColor = ManagementAppearance.muted
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
