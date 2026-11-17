#if os(iOS)
import SwiftUI
import UIKit
import MeowUI

@MainActor
final class MusicSearchPlatformsViewController: MusicSettingsPageViewController<MusicSearchPlatformsContent> {
    private let state: MusicSearchSettingsState
    let kind: MusicSearchSettingsKind

    init(state: MusicSearchSettingsState, kind: MusicSearchSettingsKind,
         appearance: MusicAppearancePreferencesStore) {
        self.state = state
        self.kind = kind
        super.init(appearance: appearance, content: MusicSearchPlatformsContent(
            state: state, kind: kind, theme: PageTheme(colors: appearance.pagePalette)
        ))
        pageTitle = kind.platformTitle
        modalPresentationStyle = .overFullScreen
        modalTransitionStyle = .crossDissolve
    }

    override func loadView() { view = AccountFloatingBackdrop() }
    override var usesPageHeader: Bool { false }

    override func applyTheme() {
        super.applyTheme()
        guard contentThemeChanged() else { return }
        setContent(MusicSearchPlatformsContent(state: state, kind: kind, theme: theme,
            close: { [weak self] in self?.dismiss(animated: true) }))
    }
}

@MainActor
struct MusicSearchPlatformsContent: View {
    @ObservedObject var state: MusicSearchSettingsState
    let kind: MusicSearchSettingsKind
    let theme: PageTheme
    var close: () -> Void = {}

    var body: some View {
        AccountFloatingCard(theme: theme) {
            VStack(spacing: 12) {
                AccountCardHeader(title: kind.platformTitle, theme: theme, close: close,
                                  leading: { EmptyView() }, trailing: { EmptyView() })
                MusicSearchPlatformTable(state: state, kind: kind, theme: theme)
            }
        }
        .font(.body.weight(.regular))
        .accessibilityIdentifier("music.search.\(kind.rawValue).platformWindow")
    }
}

/// UIKit owns the same native row-move interaction used by the collection editor.
/// The complete display order lasts only for this window; enabled priority is saved in preferences.
@MainActor
private struct MusicSearchPlatformTable: UIViewRepresentable {
    let state: MusicSearchSettingsState
    let kind: MusicSearchSettingsKind
    let theme: PageTheme

    func makeCoordinator() -> Coordinator { Coordinator(state: state, kind: kind, theme: theme) }

    func makeUIView(context: Context) -> MusicSearchPlatformTableView {
        let table = MusicSearchPlatformTableView(frame: .zero, style: .plain)
        table.backgroundColor = .clear
        table.separatorStyle = .none
        table.isScrollEnabled = false
        table.rowHeight = UITableView.automaticDimension
        table.estimatedRowHeight = SettingsVisualMetrics.rowHeight
        table.register(MusicSearchPlatformCell.self, forCellReuseIdentifier: MusicSearchPlatformCell.reuseIdentifier)
        // Mirror native editing chrome and restore the content separately.
        // UITableView keeps its reorder handle trailing even with forced RTL.
        table.transform = CGAffineTransform(scaleX: -1, y: 1)
        table.dataSource = context.coordinator
        table.delegate = context.coordinator
        context.coordinator.table = table
        table.setEditing(true, animated: false)
        table.accessibilityIdentifier = "music.search.\(kind.rawValue).platformTable"
        return table
    }

    func updateUIView(_ table: MusicSearchPlatformTableView, context: Context) {
        context.coordinator.update(theme: theme)
        table.reloadData()
        table.invalidateIntrinsicContentSize()
    }

    func sizeThatFits(_ proposal: ProposedViewSize, uiView table: MusicSearchPlatformTableView,
                      context: Context) -> CGSize? {
        guard let width = proposal.width else { return nil }
        // Measuring SwiftUI must not synchronously lay out the table or enter a
        // hosted cell's AttributeGraph. UIKit reports its completed row sizing.
        return CGSize(width: width, height: table.intrinsicContentSize.height)
    }

    static func dismantleUIView(_ table: MusicSearchPlatformTableView, coordinator: Coordinator) {
        coordinator.active = false
        table.setEditing(false, animated: false)
        table.dataSource = nil
        table.delegate = nil
    }

    @MainActor
    final class Coordinator: NSObject, UITableViewDataSource, UITableViewDelegate {
        private let state: MusicSearchSettingsState
        private let kind: MusicSearchSettingsKind
        private var theme: PageTheme
        private var order: [MusicPublicBrowsePlatform]
        weak var table: UITableView?
        var active = true

        init(state: MusicSearchSettingsState, kind: MusicSearchSettingsKind, theme: PageTheme) {
            self.state = state; self.kind = kind; self.theme = theme
            let enabled = state.settings(kind).platforms
            order = enabled + MusicSearchPreferences.platforms.filter { !enabled.contains($0) }
        }

        func update(theme: PageTheme) {
            self.theme = theme
            let enabled = state.settings(kind).platforms
            if order.filter({ enabled.contains($0) }) != enabled {
                order = enabled + order.filter { !enabled.contains($0) }
            }
        }

        func tableView(_ tableView: UITableView, numberOfRowsInSection section: Int) -> Int { order.count }

        func tableView(_ tableView: UITableView, cellForRowAt indexPath: IndexPath) -> UITableViewCell {
            let platform = order[indexPath.row]
            let cell = tableView.dequeueReusableCell(withIdentifier: MusicSearchPlatformCell.reuseIdentifier,
                                                     for: indexPath) as! MusicSearchPlatformCell
            cell.configure(platform: platform, enabled: state.settings(kind).platforms.contains(platform),
                           theme: theme, traits: tableView.traitCollection,
                           identifier: "music.search.\(kind.rawValue).platform.\(platform.rawValue)") { [weak self] enabled in
                self?.setEnabled(platform, enabled: enabled)
            }
            let last = indexPath.row == order.count - 1
            cell.backgroundView = SettingsRowBackgroundView(theme: theme, first: indexPath.row == 0,
                last: last, showsSeparator: !last)
            return cell
        }

        func tableView(_ tableView: UITableView, canMoveRowAt indexPath: IndexPath) -> Bool { active }
        func tableView(_ tableView: UITableView, editingStyleForRowAt indexPath: IndexPath)
            -> UITableViewCell.EditingStyle { .none }
        func tableView(_ tableView: UITableView, shouldIndentWhileEditingRowAt indexPath: IndexPath) -> Bool { false }

        func tableView(_ tableView: UITableView, moveRowAt sourceIndexPath: IndexPath,
                       to destinationIndexPath: IndexPath) {
            guard active, tableView.window != nil else { return }
            let platform = order.remove(at: sourceIndexPath.row)
            order.insert(platform, at: destinationIndexPath.row)
            let enabled = state.settings(kind).platforms
            if !state.setPlatforms(order.filter { enabled.contains($0) }, kind: kind) { state.reload() }
            tableView.reloadData()
        }

        private func setEnabled(_ platform: MusicPublicBrowsePlatform, enabled: Bool) {
            guard active, table?.window != nil else { return }
            var platforms = Set(state.settings(kind).platforms)
            if enabled { platforms.insert(platform) }
            else { platforms.remove(platform) }
            if !state.setPlatforms(order.filter { platforms.contains($0) }, kind: kind) { state.reload() }
        }
    }
}

/// Native content can self-size without updating another SwiftUI graph while
/// UITableView configures its editing chrome.
private final class MusicSearchPlatformCell: UITableViewCell {
    static let reuseIdentifier = "MusicSearchPlatformCell"
    private let logo = UIImageView()
    private let title = UILabel()
    private let switchInput = SettingsSwitch()
    private var enabledSwitch: UISwitch { switchInput.control }
    private var logoSize: [NSLayoutConstraint] = []
    private var onChange: ((Bool) -> Void)?

    override init(style: UITableViewCell.CellStyle, reuseIdentifier: String?) {
        super.init(style: style, reuseIdentifier: reuseIdentifier)
        backgroundColor = .clear
        selectionStyle = .none
        contentView.transform = CGAffineTransform(scaleX: -1, y: 1)
        contentView.semanticContentAttribute = .forceLeftToRight
        showsReorderControl = true
        isAccessibilityElement = false
        logo.contentMode = .scaleAspectFit
        logo.isAccessibilityElement = false
        title.numberOfLines = 0
        title.adjustsFontForContentSizeCategory = true
        title.isAccessibilityElement = false
        title.setContentCompressionResistancePriority(.required, for: .vertical)
        switchInput.setContentHuggingPriority(.required, for: .horizontal)
        switchInput.setContentCompressionResistancePriority(.required, for: .horizontal)
        enabledSwitch.addTarget(self, action: #selector(enabledChanged), for: .valueChanged)
        let row = UIStackView(arrangedSubviews: [logo, title, switchInput])
        row.axis = .horizontal
        row.alignment = .center
        row.spacing = 12
        row.semanticContentAttribute = .forceLeftToRight
        row.translatesAutoresizingMaskIntoConstraints = false
        contentView.addSubview(row)
        logoSize = [
            logo.widthAnchor.constraint(equalToConstant: 0),
            logo.heightAnchor.constraint(equalToConstant: 0)
        ]
        NSLayoutConstraint.activate(logoSize + [
            row.leadingAnchor.constraint(equalTo: contentView.leadingAnchor, constant: 12),
            row.trailingAnchor.constraint(equalTo: contentView.trailingAnchor,
                                          constant: -SettingsVisualMetrics.rowHorizontalPadding),
            row.topAnchor.constraint(equalTo: contentView.topAnchor, constant: SettingsVisualMetrics.rowVerticalPadding),
            row.bottomAnchor.constraint(equalTo: contentView.bottomAnchor, constant: -SettingsVisualMetrics.rowVerticalPadding),
            contentView.heightAnchor.constraint(greaterThanOrEqualToConstant: SettingsVisualMetrics.rowHeight)
        ])
        registerForTraitChanges([UITraitPreferredContentSizeCategory.self]) { (cell: MusicSearchPlatformCell, _: UITraitCollection) in
            cell.updateMetrics(traits: cell.traitCollection)
        }
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    func configure(platform: MusicPublicBrowsePlatform, enabled: Bool, theme: PageTheme,
                   traits: UITraitCollection, identifier: String, onChange: @escaping (Bool) -> Void) {
        if let assetName = platform.settingsLogoName {
            logo.image = UIImage(named: assetName, in: .main, compatibleWith: traits)?.withRenderingMode(.alwaysOriginal)
        } else {
            logo.image = UIImage(systemName: "music.note")
        }
        logo.tintColor = UIColor(theme.settings.muted)
        title.text = platform.title
        updateMetrics(traits: traits)
        title.textColor = UIColor(theme.settings.ink)
        enabledSwitch.onTintColor = UIColor(theme.settings.accent)
        enabledSwitch.isOn = enabled
        enabledSwitch.accessibilityLabel = platform.title
        enabledSwitch.accessibilityIdentifier = identifier
        self.onChange = onChange
    }

    private func updateMetrics(traits: UITraitCollection) {
        title.font = .preferredFont(forTextStyle: .body, compatibleWith: traits)
        for constraint in logoSize { constraint.constant = title.font.lineHeight }
    }

    @objc private func enabledChanged() { onChange?(enabledSwitch.isOn) }
}

private final class MusicSearchPlatformTableView: UITableView {
    override var contentSize: CGSize {
        didSet {
            if contentSize != oldValue { invalidateIntrinsicContentSize() }
        }
    }

    override var intrinsicContentSize: CGSize {
        let height = contentSize.height > 0 ? contentSize.height
            : estimatedRowHeight * CGFloat(MusicSearchPreferences.platforms.count)
        return CGSize(width: UIView.noIntrinsicMetric, height: height)
    }
}
#endif
