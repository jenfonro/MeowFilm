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
}
#endif
