#if os(iOS)
import UIKit

@MainActor
public final class SettingsStatusBadge: UIView {
    public enum Status: Sendable {
        case neutral, success, warning, danger
    }

    private let label = UILabel(), icon = UIImageView()
    private var status: Status
    private var theme: PageTheme

    public init(status: Status, text: String, theme: PageTheme) {
        self.status = status; self.theme = theme
        super.init(frame: .zero)
        label.text = text; label.numberOfLines = 0
        label.adjustsFontForContentSizeCategory = true
        icon.contentMode = .scaleAspectFit
        let stack = UIStackView(arrangedSubviews: [icon, label])
        stack.alignment = .center; stack.spacing = 4
        stack.translatesAutoresizingMaskIntoConstraints = false; addSubview(stack)
        NSLayoutConstraint.activate([
            stack.leadingAnchor.constraint(equalTo: leadingAnchor, constant: 8),
            stack.trailingAnchor.constraint(equalTo: trailingAnchor, constant: -8),
            stack.topAnchor.constraint(equalTo: topAnchor, constant: 4),
            stack.bottomAnchor.constraint(equalTo: bottomAnchor, constant: -4),
            icon.widthAnchor.constraint(equalToConstant: 12),
            icon.heightAnchor.constraint(equalToConstant: 12),
        ])
        setContentHuggingPriority(.defaultHigh, for: .vertical)
        isAccessibilityElement = true; accessibilityLabel = text
        registerForTraitChanges([UITraitPreferredContentSizeCategory.self]) { (badge: SettingsStatusBadge, _: UITraitCollection) in
            badge.applyTheme(badge.theme)
        }
        applyTheme(theme)
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    public func update(status: Status, text: String, theme: PageTheme) {
        self.status = status
        label.text = text
        accessibilityLabel = text
        applyTheme(theme)
    }

    public func applyTheme(_ theme: PageTheme) {
        self.theme = theme
        let colors = theme.settings
        let ink: PageColor, symbol: String
        switch status {
        case .neutral: ink = colors.muted; symbol = "minus.circle"
        case .success: ink = colors.success; symbol = "checkmark.circle"
        case .warning: ink = colors.warning; symbol = "exclamationmark.circle"
        case .danger: ink = colors.danger; symbol = "xmark.circle"
        }
        label.font = UIFontMetrics(forTextStyle: .caption1).scaledFont(for: .systemFont(ofSize: 12),
            compatibleWith: traitCollection)
        label.textColor = UIColor(ink); icon.tintColor = UIColor(ink)
        icon.image = UIImage(systemName: symbol, withConfiguration: UIImage.SymbolConfiguration(pointSize: 12, weight: .regular))
        backgroundColor = UIColor(colors.surface.mixed(with: ink, ratio: theme.dark ? 0.12 : 0.08))
        invalidateIntrinsicContentSize()
        setNeedsLayout()
    }

    public override var intrinsicContentSize: CGSize {
        let width = bounds.width > 32 ? bounds.width - 32 : CGFloat.greatestFiniteMagnitude
        let size = label.sizeThatFits(CGSize(width: width, height: .greatestFiniteMagnitude))
        return CGSize(width: size.width + 32, height: max(12, ceil(size.height)) + 8)
    }

    public override func layoutSubviews() {
        super.layoutSubviews()
        if label.preferredMaxLayoutWidth != label.bounds.width {
            label.preferredMaxLayoutWidth = label.bounds.width
            invalidateIntrinsicContentSize()
        }
        layer.cornerRadius = min(bounds.width, bounds.height) / 2
    }
}
#endif
