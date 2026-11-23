#if os(iOS)
import Combine
import UIKit
import MeowDebug
import MeowUI

/// Pure UIKit floating card for enabling and reordering search platforms (zero SwiftUI, zero mirror-flip hack,
/// zero `table.reloadData()`).
@MainActor
final class MusicSearchPlatformsViewController: FloatingCardDialogController, UITableViewDelegate {
    private final class DataSource: UITableViewDiffableDataSource<Int, MusicPublicBrowsePlatform> {
        weak var owner: MusicSearchPlatformsViewController?

        override func tableView(_ tableView: UITableView, canEditRowAt indexPath: IndexPath) -> Bool {
            owner?.isActive == true
        }

        override func tableView(_ tableView: UITableView, canMoveRowAt indexPath: IndexPath) -> Bool {
            owner?.isActive == true
        }

        override func tableView(_ tableView: UITableView, moveRowAt sourceIndexPath: IndexPath,
                                to destinationIndexPath: IndexPath) {
            owner?.movePlatform(from: sourceIndexPath.row, to: destinationIndexPath.row)
        }
    }

    private struct RowState: Equatable {
        let platform: MusicPublicBrowsePlatform
        let enabled: Bool
        let first: Bool
        let last: Bool
    }

    private let state: MusicSearchSettingsState
    let kind: MusicSearchSettingsKind
    var order: [MusicPublicBrowsePlatform]
    private var isActive = true
    private var observation: AnyCancellable?
    private let table = MusicSearchPlatformTableView(frame: .zero, style: .plain)
    private var projection = IndexedDiffableProjection<Int, MusicPublicBrowsePlatform, RowState>()
    private var dataSource: DataSource!

    init(state: MusicSearchSettingsState, kind: MusicSearchSettingsKind,
         appearance: MusicAppearancePreferencesStore) {
        self.state = state
        self.kind = kind
        let enabled = state.settings(kind).platforms
        self.order = enabled + MusicSearchPreferences.platforms.filter { !enabled.contains($0) }
        super.init(title: kind.platformTitle, themeProvider: appearance)
    }

    override func viewDidLoad() {
        super.viewDidLoad()
        view.accessibilityIdentifier = "music.search.\(kind.rawValue).platformsWindow"
        table.backgroundColor = .clear
        table.separatorStyle = .none
        table.isScrollEnabled = false
        table.rowHeight = UITableView.automaticDimension
        table.estimatedRowHeight = SettingsVisualMetrics.rowHeight
        table.contentInsetAdjustmentBehavior = .never
        table.delegate = self
        table.register(MusicSearchPlatformCell.self, forCellReuseIdentifier: MusicSearchPlatformCell.reuseIdentifier)
        table.accessibilityIdentifier = "music.search.\(kind.rawValue).platformTable"
        addBodyView(table)

        dataSource = DataSource(tableView: table) { [weak self] tableView, indexPath, platform in
            let cell = tableView.dequeueReusableCell(
                withIdentifier: MusicSearchPlatformCell.reuseIdentifier, for: indexPath
            ) as! MusicSearchPlatformCell
            self?.configure(cell: cell, platform: platform, index: indexPath.row)
            return cell
        }
        dataSource.owner = self
        dataSource.defaultRowAnimation = .none
        table.setEditing(true, animated: false)

        observation = state.$value.removeDuplicates().receive(on: RunLoop.main).sink { [weak self] _ in
            self?.syncOrderAndRender()
        }
        syncOrderAndRender()
    }

    override func dialogWillClose() {
        super.dialogWillClose()
        isActive = false
        observation?.cancel()
        observation = nil
    }

    override func applyTheme() {
        super.applyTheme()
        guard isViewLoaded, dataSource != nil else { return }
        for indexPath in table.indexPathsForVisibleRows ?? [] {
            guard let platform = dataSource.itemIdentifier(for: indexPath),
                  let cell = table.cellForRow(at: indexPath) as? MusicSearchPlatformCell else { continue }
            configure(cell: cell, platform: platform, index: indexPath.row)
        }
    }

    private func syncOrderAndRender() {
        guard isViewLoaded, dataSource != nil else { return }
        let enabled = state.settings(kind).platforms
        if order.filter({ enabled.contains($0) }) != enabled {
            order = enabled + order.filter { !enabled.contains($0) }
        }
        let enabledSet = Set(enabled)
        let lastIndex = order.count - 1
        let items = order.enumerated().map { index, platform in
            RowState(platform: platform, enabled: enabledSet.contains(platform), first: index == 0, last: index == lastIndex)
        }
        let update = projection.update(section: 0, items: items, id: { $0.platform })
        dataSource.applyIfNeeded(update, animatingDifferences: false)
        table.invalidateIntrinsicContentSize()
    }

    private func configure(cell: MusicSearchPlatformCell, platform: MusicPublicBrowsePlatform, index: Int) {
        let enabled = state.settings(kind).platforms.contains(platform)
        let first = index == 0
        let last = index == order.count - 1
        cell.configure(
            platform: platform,
            enabled: enabled,
            theme: theme,
            first: first,
            last: last,
            identifier: "music.search.\(kind.rawValue).platform.\(platform.rawValue)"
        ) { [weak self] nextEnabled in
            self?.setEnabled(platform, enabled: nextEnabled)
        }
    }

    @discardableResult
    func movePlatform(from source: Int, to destination: Int) -> Task<Void, Never>? {
        guard isActive, table.window != nil,
              order.indices.contains(source), order.indices.contains(destination) else { return nil }
        let platform = order.remove(at: source)
        order.insert(platform, at: destination)
        let enabled = state.settings(kind).platforms
        let updated = order.filter { enabled.contains($0) }
        let state = self.state, kind = self.kind
        return Task { [weak self] in
            if !(await state.setPlatforms(updated, kind: kind)) {
                await state.reload()
            }
            self?.syncOrderAndRender()
        }
    }

    @discardableResult
    func setEnabled(_ platform: MusicPublicBrowsePlatform, enabled: Bool) -> Task<Void, Never>? {
        guard isActive, table.window != nil else { return nil }
        var platforms = Set(state.settings(kind).platforms)
        if enabled { platforms.insert(platform) } else { platforms.remove(platform) }
        let updated = order.filter { platforms.contains($0) }
        let state = self.state, kind = self.kind
        return Task {
            if !(await state.setPlatforms(updated, kind: kind)) {
                await state.reload()
            }
        }
    }

    func tableView(_ tableView: UITableView, editingStyleForRowAt indexPath: IndexPath) -> UITableViewCell.EditingStyle {
        .none
    }

    func tableView(_ tableView: UITableView, shouldIndentWhileEditingRowAt indexPath: IndexPath) -> Bool {
        false
    }

    override var debugDialogTypeName: String { "MusicSearchPlatformsViewController" }
}

@MainActor
private final class MusicSearchPlatformCell: UITableViewCell {
    static let reuseIdentifier = "MusicSearchPlatformCell"

    private let rowBackground = SettingsRowBackgroundView(selected: false)
    private let logo = UIImageView()
    private let title = UILabel()
    private let switchInput = SettingsSwitch()
    private var logoSize: [NSLayoutConstraint] = []
    private var onChange: (@MainActor (Bool) -> Void)?

    override init(style: UITableViewCell.CellStyle, reuseIdentifier: String?) {
        super.init(style: style, reuseIdentifier: reuseIdentifier)
        backgroundColor = .clear
        backgroundView = rowBackground
        selectionStyle = .none
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
        switchInput.control.addTarget(self, action: #selector(enabledChanged), for: .valueChanged)

        let row = UIStackView(arrangedSubviews: [logo, title, switchInput])
        row.axis = .horizontal
        row.alignment = .center
        row.spacing = 12
        row.translatesAutoresizingMaskIntoConstraints = false
        contentView.addSubview(row)
        logoSize = [
            logo.widthAnchor.constraint(equalToConstant: 20),
            logo.heightAnchor.constraint(equalToConstant: 20),
        ]
        NSLayoutConstraint.activate(logoSize + [
            row.leadingAnchor.constraint(equalTo: contentView.leadingAnchor, constant: SettingsVisualMetrics.rowHorizontalPadding),
            row.trailingAnchor.constraint(equalTo: contentView.trailingAnchor, constant: -12),
            row.topAnchor.constraint(equalTo: contentView.topAnchor, constant: SettingsVisualMetrics.rowVerticalPadding),
            row.bottomAnchor.constraint(equalTo: contentView.bottomAnchor, constant: -SettingsVisualMetrics.rowVerticalPadding),
            contentView.heightAnchor.constraint(greaterThanOrEqualToConstant: SettingsVisualMetrics.rowHeight),
        ])
        registerForTraitChanges([UITraitPreferredContentSizeCategory.self]) { (cell: MusicSearchPlatformCell, _: UITraitCollection) in
            cell.updateMetrics()
        }
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    override func prepareForReuse() {
        super.prepareForReuse()
        onChange = nil
        logo.image = nil
        switchInput.control.accessibilityLabel = nil
        switchInput.control.accessibilityIdentifier = nil
    }

    func configure(platform: MusicPublicBrowsePlatform, enabled: Bool, theme: PageTheme,
                   first: Bool, last: Bool, identifier: String,
                   onChange: @escaping @MainActor (Bool) -> Void) {
        rowBackground.update(theme: theme, first: first, last: last, selected: false, settings: true, showsSeparator: !last)
        if let assetName = platform.settingsLogoName,
           let image = UIImage(named: assetName, in: .main, compatibleWith: traitCollection) {
            logo.image = image.withRenderingMode(.alwaysOriginal)
        } else {
            logo.image = UIImage(systemName: "music.note")
        }
        logo.tintColor = UIColor(theme.settings.muted)
        title.text = platform.title
        title.textColor = UIColor(theme.settings.ink)
        updateMetrics()
        switchInput.applyTheme(theme)
        switchInput.control.isOn = enabled
        switchInput.control.accessibilityLabel = platform.title
        switchInput.control.accessibilityIdentifier = identifier
        self.onChange = onChange
    }

    private func updateMetrics() {
        title.font = .preferredFont(forTextStyle: .body, compatibleWith: traitCollection)
        for constraint in logoSize { constraint.constant = max(20, title.font.lineHeight) }
    }

    @objc private func enabledChanged() { onChange?(switchInput.control.isOn) }
}

@MainActor
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
