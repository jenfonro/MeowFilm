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
        register(SettingsTableCell.self, forCellReuseIdentifier: SettingsTableCell.reuseIdentifier)
        register(SettingsSectionHeaderView.self,
                 forHeaderFooterViewReuseIdentifier: SettingsSectionHeaderView.reuseIdentifier)
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    /// Settings delegates replace only the first grouped header's native gap.
    /// Safe-area adjustment and the following sections keep their normal policy.
    public func heightForHeader(in section: Int, title: String? = nil) -> CGFloat {
        if title?.isEmpty == false { return UITableView.automaticDimension }
        return section == 0 ? SettingsVisualMetrics.contentTopSpacing : SettingsVisualMetrics.sectionSpacing
    }

    public func viewForHeader(in section: Int, title: String?, theme: PageTheme) -> UIView? {
        guard let title, !title.isEmpty else { return UIView() }
        let header = dequeueReusableHeaderFooterView(
            withIdentifier: SettingsSectionHeaderView.reuseIdentifier
        ) as? SettingsSectionHeaderView ?? SettingsSectionHeaderView(reuseIdentifier: SettingsSectionHeaderView.reuseIdentifier)
        header.configure(title: title, firstSection: section == 0, theme: theme)
        return header
    }
}
#endif
