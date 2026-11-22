#if os(iOS)
import UIKit

/// Explicit horizontal hairline divider inside settings forms and cards.
@MainActor
public final class SettingsDividerView: UIView, PageThemeable {
    public init(theme: PageTheme? = nil) {
        super.init(frame: .zero)
        isUserInteractionEnabled = false
        accessibilityElementsHidden = true
        heightAnchor.constraint(equalToConstant: SettingsVisualMetrics.separatorHeight).isActive = true
        if let theme { applyTheme(theme) }
        else { backgroundColor = ManagementAppearance.line }
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    public func applyTheme(_ theme: PageTheme) {
        backgroundColor = UIColor(theme.settings.separator)
    }
}

/// Reusable section header for `SettingsTableView`.
@MainActor
public final class SettingsSectionHeaderView: UITableViewHeaderFooterView, PageThemeable {
    public static let reuseIdentifier = "meow.settings.sectionHeader"
    private let label = UILabel()
    private let stack = UIStackView()

    public override init(reuseIdentifier: String?) {
        super.init(reuseIdentifier: reuseIdentifier)
        backgroundConfiguration = .clear()
        label.font = .preferredFont(forTextStyle: .subheadline)
        label.adjustsFontForContentSizeCategory = true
        label.numberOfLines = 0
        stack.axis = .vertical
        stack.isLayoutMarginsRelativeArrangement = true
        stack.addArrangedSubview(label)
        contentView.addSubview(stack)
        stack.pinEdges(to: contentView)
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    public func configure(title: String, firstSection: Bool, theme: PageTheme) {
        label.text = title
        label.font = .preferredFont(forTextStyle: .subheadline, compatibleWith: traitCollection)
        let top: CGFloat = firstSection ? SettingsVisualMetrics.contentTopSpacing : 8
        let horizontal = SettingsVisualMetrics.rowHorizontalPadding
        stack.directionalLayoutMargins = NSDirectionalEdgeInsets(
            top: top, leading: horizontal, bottom: 8, trailing: horizontal
        )
        applyTheme(theme)
    }

    public func applyTheme(_ theme: PageTheme) {
        label.textColor = UIColor(theme.settings.muted)
    }
}

/// Segmented control conforming to `PageThemeable` with guaranteed Regular font weight.
@MainActor
public final class SettingsSegmentedControl: UISegmentedControl, PageThemeable {
    public enum Style {
        case settings
        case accentPill
        case header
    }

    public var style: Style = .settings

    public override func layoutSubviews() {
        super.layoutSubviews()
        if style == .accentPill {
            layer.cornerRadius = min(bounds.width, bounds.height) / 2
            clipsToBounds = true
        }
    }

    public func applyTheme(_ theme: PageTheme) {
        switch style {
        case .settings:
            let colors = theme.settings
            backgroundColor = UIColor(colors.input)
            selectedSegmentTintColor = UIColor(colors.surface)
            let attributes: [NSAttributedString.Key: Any] = [
                .foregroundColor: UIColor(colors.ink),
                .font: UIFont.preferredFont(forTextStyle: .subheadline, compatibleWith: traitCollection),
            ]
            setTitleTextAttributes(attributes, for: .normal)
            setTitleTextAttributes(attributes, for: .selected)
        case .accentPill:
            let colors = theme.settings
            let font = UIFont.preferredFont(forTextStyle: .body, compatibleWith: traitCollection)
            backgroundColor = UIColor(colors.surface)
            selectedSegmentTintColor = UIColor(colors.accent)
            layer.borderColor = UIColor(colors.border).cgColor
            layer.borderWidth = 1
            let normalBG = Self.solidImage(UIColor(colors.surface))
            let selectedBG = Self.solidImage(UIColor(colors.accent))
            let clearDivider = Self.solidImage(.clear)
            setBackgroundImage(normalBG, for: .normal, barMetrics: .default)
            setBackgroundImage(selectedBG, for: .selected, barMetrics: .default)
            for left in [UIControl.State.normal, .selected] {
                for right in [UIControl.State.normal, .selected] {
                    setDividerImage(clearDivider, forLeftSegmentState: left, rightSegmentState: right, barMetrics: .default)
                }
            }
            setTitleTextAttributes([.font: font, .foregroundColor: UIColor(colors.ink)], for: .normal)
            setTitleTextAttributes([.font: font, .foregroundColor: UIColor(colors.onAccent)], for: .selected)
        case .header:
            let font = UIFont.systemFont(ofSize: 13, weight: .regular)
            backgroundColor = UIColor(theme.control)
            selectedSegmentTintColor = UIColor(theme.surface)
            setTitleTextAttributes([.font: font, .foregroundColor: UIColor(theme.muted)], for: .normal)
            setTitleTextAttributes([.font: font, .foregroundColor: UIColor(theme.ink)], for: .selected)
            setTitleTextAttributes([.font: font, .foregroundColor: UIColor(theme.muted).withAlphaComponent(0.5)], for: .disabled)
        }
    }

    private static func solidImage(_ color: UIColor) -> UIImage {
        UIGraphicsImageRenderer(size: CGSize(width: 1, height: 1)).image { context in
            color.setFill()
            context.fill(CGRect(x: 0, y: 0, width: 1, height: 1))
        }
    }
}

extension SettingsStatusBadge: PageThemeable {}
#endif
