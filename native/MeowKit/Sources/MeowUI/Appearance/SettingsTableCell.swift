#if os(iOS)
import UIKit

/// A settings row with shared typography, spacing, surface, and divider rules.
@MainActor
public final class SettingsTableCell: UITableViewCell {
    public init(title: String, detail: String? = nil, theme: PageTheme,
                first: Bool, last: Bool, destructive: Bool = false) {
        super.init(style: .subtitle, reuseIdentifier: nil)

        var content = UIListContentConfiguration.subtitleCell()
        content.text = title
        content.secondaryText = detail
        content.directionalLayoutMargins = NSDirectionalEdgeInsets(
            top: SettingsVisualMetrics.rowVerticalPadding, leading: SettingsVisualMetrics.rowHorizontalPadding,
            bottom: SettingsVisualMetrics.rowVerticalPadding, trailing: SettingsVisualMetrics.rowHorizontalPadding
        )
        content.textProperties.font = .preferredFont(forTextStyle: .body)
        content.textProperties.adjustsFontForContentSizeCategory = true
        content.textProperties.color = UIColor(destructive ? theme.settings.danger : theme.settings.ink)
        content.textProperties.numberOfLines = 0
        content.secondaryTextProperties.font = .preferredFont(forTextStyle: .subheadline)
        content.secondaryTextProperties.adjustsFontForContentSizeCategory = true
        content.secondaryTextProperties.color = UIColor(theme.settings.muted)
        content.secondaryTextProperties.numberOfLines = 0
        contentConfiguration = content

        backgroundColor = .clear
        tintColor = UIColor(theme.settings.accent)
        backgroundView = SettingsRowBackgroundView(theme: theme, first: first, last: last,
            showsSeparator: !last)
        selectedBackgroundView = SettingsRowBackgroundView(theme: theme, first: first, last: last,
            selected: true, showsSeparator: !last)
        selectionStyle = .default
        contentView.heightAnchor.constraint(greaterThanOrEqualToConstant: SettingsVisualMetrics.rowHeight).isActive = true
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }
}
#endif
