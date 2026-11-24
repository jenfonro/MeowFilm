#if os(iOS)
import Combine
import UIKit
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
    private var desiredEnabled: Set<MusicPublicBrowsePlatform>
    private var mutationTask: Task<Void, Never>?
    private var mutationToken = UUID()
    private var isActive = true
    private var observations: Set<AnyCancellable> = []
    private let table = MusicSearchPlatformTableView(frame: .zero, style: .plain)
    private var projection = IndexedDiffableProjection<Int, MusicPublicBrowsePlatform, RowState>()
    private var dataSource: DataSource!

    init(state: MusicSearchSettingsState, kind: MusicSearchSettingsKind,
         appearance: MusicAppearancePreferencesStore) {
        self.state = state
        self.kind = kind
        let enabled = state.settings(kind).platforms
        self.order = enabled + MusicSearchPreferences.platforms.filter { !enabled.contains($0) }
        self.desiredEnabled = Set(enabled)
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
        table.register(SettingsTableCell.self, forCellReuseIdentifier: SettingsTableCell.reuseIdentifier)
        table.accessibilityIdentifier = "music.search.\(kind.rawValue).platformTable"
        addBodyView(table)

        dataSource = DataSource(tableView: table) { [weak self] tableView, indexPath, platform in
            let cell = tableView.dequeueReusableCell(
                withIdentifier: SettingsTableCell.reuseIdentifier, for: indexPath
            ) as! SettingsTableCell
            self?.configure(cell: cell, platform: platform, index: indexPath.row)
            return cell
        }
        dataSource.owner = self
        dataSource.defaultRowAnimation = .none
        table.setEditing(true, animated: false)

        state.$value.dropFirst().removeDuplicates().receive(on: RunLoop.main).sink { [weak self] _ in
            self?.syncOrderAndRender()
        }.store(in: &observations)
        state.$error.dropFirst().removeDuplicates().receive(on: RunLoop.main).sink { [weak self] message in
            self?.setErrorMessage(message)
        }.store(in: &observations)
        setErrorMessage(state.error)
        syncOrderAndRender()
    }

    override func dialogWillClose() {
        super.dialogWillClose()
        isActive = false
        observations.removeAll()
    }

    override func applyTheme() {
        super.applyTheme()
        guard isViewLoaded, dataSource != nil else { return }
        for indexPath in table.indexPathsForVisibleRows ?? [] {
            guard let platform = dataSource.itemIdentifier(for: indexPath),
                  let cell = table.cellForRow(at: indexPath) as? SettingsTableCell else { continue }
            configure(cell: cell, platform: platform, index: indexPath.row)
        }
    }

    private func syncOrderAndRender(forceReconfigure: Bool = false) {
        guard isViewLoaded, dataSource != nil else { return }
        if mutationTask == nil {
            let enabled = state.settings(kind).platforms
            desiredEnabled = Set(enabled)
            if order.filter({ enabled.contains($0) }) != enabled {
                order = enabled + order.filter { !enabled.contains($0) }
            }
        }
        let enabledSet = desiredEnabled
        let lastIndex = order.count - 1
        let items = order.enumerated().map { index, platform in
            RowState(platform: platform, enabled: enabledSet.contains(platform), first: index == 0, last: index == lastIndex)
        }
        let update = projection.update(section: 0, items: items, id: { $0.platform })
        let applied = dataSource.applyIfNeeded(update, animatingDifferences: false)
        if forceReconfigure && !applied {
            for indexPath in table.indexPathsForVisibleRows ?? [] {
                guard let platform = dataSource.itemIdentifier(for: indexPath),
                      let cell = table.cellForRow(at: indexPath) as? SettingsTableCell else { continue }
                configure(cell: cell, platform: platform, index: indexPath.row)
            }
        }
        table.invalidateIntrinsicContentSize()
    }

    private func configure(cell: SettingsTableCell, platform: MusicPublicBrowsePlatform, index: Int) {
        let enabled = desiredEnabled.contains(platform)
        let first = index == 0
        let last = index == order.count - 1
        let row = SettingsListRow(
            id: "music.search.\(kind.rawValue).platform.\(platform.rawValue)",
            title: platform.title,
            leadingIcon: .asset(platform.settingsLogoName),
            accessory: .toggle(isOn: enabled, enabled: true) { [weak self] nextEnabled in
                self?.setEnabled(platform, enabled: nextEnabled)
            },
            canMove: true
        )
        cell.configure(row: row, theme: theme, first: first, last: last)
    }

    private func enqueueMutation(_ updated: [MusicPublicBrowsePlatform]) -> Task<Void, Never> {
        let previous = mutationTask
        let token = UUID()
        mutationToken = token
        let state = self.state, kind = self.kind
        let task = Task { [weak self] in
            await previous?.value
            let saved = await state.setPlatforms(updated, kind: kind)
            if !saved {
                await state.reload()
            }
            guard let self else { return }
            if self.mutationToken == token {
                self.mutationTask = nil
                self.syncOrderAndRender(forceReconfigure: !saved)
            }
        }
        mutationTask = task
        return task
    }

    @discardableResult
    func movePlatform(from source: Int, to destination: Int) -> Task<Void, Never>? {
        guard isActive, table.window != nil,
              order.indices.contains(source), order.indices.contains(destination) else { return nil }
        let platform = order.remove(at: source)
        order.insert(platform, at: destination)
        let updated = order.filter { desiredEnabled.contains($0) }
        syncOrderAndRender()
        return enqueueMutation(updated)
    }

    @discardableResult
    func setEnabled(_ platform: MusicPublicBrowsePlatform, enabled: Bool) -> Task<Void, Never>? {
        guard isActive, table.window != nil else { return nil }
        if enabled { desiredEnabled.insert(platform) } else { desiredEnabled.remove(platform) }
        let updated = order.filter { desiredEnabled.contains($0) }
        return enqueueMutation(updated)
    }

    func tableView(_ tableView: UITableView, editingStyleForRowAt indexPath: IndexPath) -> UITableViewCell.EditingStyle {
        .none
    }

    func tableView(_ tableView: UITableView, shouldIndentWhileEditingRowAt indexPath: IndexPath) -> Bool {
        false
    }
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
