#if os(iOS)
import UIKit

/// The table chrome used by native settings pages.
///
/// Rows own their card segment through `SettingsRowBackgroundView`; the table
/// therefore does not add UIKit's full-width separator on top of that card.
@MainActor
public final class SettingsTableView: UITableView {
    public init() {
        super.init(frame: .zero, style: .insetGrouped)
        backgroundColor = .clear
        separatorStyle = .none
        directionalLayoutMargins = NSDirectionalEdgeInsets(
            top: 0, leading: SettingsVisualMetrics.cardHorizontalInset,
            bottom: 0, trailing: SettingsVisualMetrics.cardHorizontalInset
        )
        cellLayoutMarginsFollowReadableWidth = false
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    /// Settings delegates replace only the first grouped header's native gap.
    /// Safe-area adjustment and the following sections keep their normal policy.
    public func heightForHeader(in section: Int, title: String? = nil) -> CGFloat {
        guard section == 0 else { return UITableView.automaticDimension }
        return title?.isEmpty == false ? UITableView.automaticDimension : SettingsVisualMetrics.contentTopSpacing
    }

    public func viewForHeader(in section: Int, title: String?, theme: PageTheme) -> UIView? {
        guard section == 0 else { return nil }
        guard let title, !title.isEmpty else { return UIView() }
        let label = UILabel()
        label.text = title
        label.font = .preferredFont(forTextStyle: .subheadline, compatibleWith: traitCollection)
        label.adjustsFontForContentSizeCategory = true
        label.numberOfLines = 0
        label.textColor = UIColor(theme.settings.muted)
        let content = UIStackView(arrangedSubviews: [label])
        content.axis = .vertical
        content.isLayoutMarginsRelativeArrangement = true
        content.directionalLayoutMargins = NSDirectionalEdgeInsets(
            top: SettingsVisualMetrics.contentTopSpacing,
            leading: SettingsVisualMetrics.cardHorizontalInset + SettingsVisualMetrics.rowHorizontalPadding,
            bottom: 8,
            trailing: SettingsVisualMetrics.cardHorizontalInset + SettingsVisualMetrics.rowHorizontalPadding
        )
        let header = UITableViewHeaderFooterView(reuseIdentifier: nil)
        header.backgroundConfiguration = .clear()
        header.contentView.addSubview(content); content.translatesAutoresizingMaskIntoConstraints = false
        NSLayoutConstraint.activate([
            content.topAnchor.constraint(equalTo: header.contentView.topAnchor),
            content.bottomAnchor.constraint(equalTo: header.contentView.bottomAnchor),
            content.leadingAnchor.constraint(equalTo: header.contentView.leadingAnchor),
            content.trailingAnchor.constraint(equalTo: header.contentView.trailingAnchor),
        ])
        return header
    }
}
#endif
