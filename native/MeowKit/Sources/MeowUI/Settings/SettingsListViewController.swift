#if os(iOS)
import UIKit
import MeowDebug

/// Declarative row model for the unified native UIKit `SettingsListViewController`.
@MainActor
public struct SettingsListRow {
    public enum Accessory {
        case none
        case disclosure
        case checkmark
        case value(String, disclosure: Bool = false)
        case toggle(isOn: Bool, enabled: Bool = true, onChange: @MainActor (Bool) -> Void)
        case menu(title: String, menu: UIMenu, enabled: Bool = true)
        case custom(UIView)
    }

    public let id: String
    public let title: String
    public let detail: String?
    public let detailDestructive: Bool
    public let leadingView: UIView?
    public let embeddedContent: UIView?
    public let accessory: Accessory
    public let destructive: Bool
    public let enabled: Bool
    public let accessibilityLabel: String?
    public let accessibilityValue: String?
    public let canMove: Bool
    public let action: (@MainActor () -> Void)?

    public init(
        id: String,
        title: String = "",
        detail: String? = nil,
        detailDestructive: Bool = false,
        leadingView: UIView? = nil,
        embeddedContent: UIView? = nil,
        accessory: Accessory = .none,
        destructive: Bool = false,
        enabled: Bool = true,
        accessibilityLabel: String? = nil,
        accessibilityValue: String? = nil,
        canMove: Bool = false,
        action: (@MainActor () -> Void)? = nil
    ) {
        self.id = id
        self.title = title
        self.detail = detail
        self.detailDestructive = detailDestructive
        self.leadingView = leadingView
        self.embeddedContent = embeddedContent
        self.accessory = accessory
        self.destructive = destructive
        self.enabled = enabled
        self.accessibilityLabel = accessibilityLabel
        self.accessibilityValue = accessibilityValue
        self.canMove = canMove
        self.action = action
    }
}

@MainActor
public struct SettingsListSection {
    public let id: String
    public let title: String?
    public let rows: [SettingsListRow]

    public init(id: String, title: String? = nil, rows: [SettingsListRow]) {
        self.id = id
        self.title = title
        self.rows = rows
    }
}

/// Unified pure UIKit settings table controller backed by `UITableViewDiffableDataSource`
/// and reusable `SettingsTableCell` + `SettingsSectionHeaderView` (zero `reloadData()`).
@MainActor
open class SettingsListViewController: PageViewController, UITableViewDelegate {
    public let table = SettingsTableView()
    public private(set) var currentSections: [SettingsListSection] = []
    open var usesSettingsColors: Bool { true }
    open var usesSettingsTable: Bool { true }
    open var avoidsKeyboard: Bool { true }
    open var allowsRowReordering: Bool { false }

    private var rowsByID: [String: SettingsListRow] = [:]
    private var sectionMetaByRowID: [String: (first: Bool, last: Bool)] = [:]
    private var sectionsByID: [String: SettingsListSection] = [:]
    private var dataSource: DataSource!

    private final class DataSource: UITableViewDiffableDataSource<String, String> {
        weak var owner: SettingsListViewController?

        override func tableView(_ tableView: UITableView, canEditRowAt indexPath: IndexPath) -> Bool {
            guard let owner, owner.allowsRowReordering,
                  let id = itemIdentifier(for: indexPath),
                  let row = owner.rowsByID[id] else { return false }
            return row.canMove
        }

        override func tableView(_ tableView: UITableView, canMoveRowAt indexPath: IndexPath) -> Bool {
            guard let owner, owner.allowsRowReordering,
                  let id = itemIdentifier(for: indexPath),
                  let row = owner.rowsByID[id] else { return false }
            return row.canMove
        }

        override func tableView(_ tableView: UITableView, moveRowAt sourceIndexPath: IndexPath,
                                to destinationIndexPath: IndexPath) {
            owner?.didMoveRow(from: sourceIndexPath, to: destinationIndexPath)
        }
    }

    open override func viewDidLoad() {
        super.viewDidLoad()
        guard usesSettingsTable else { return }
        table.delegate = self
        table.keyboardDismissMode = .interactive
        table.sectionFooterHeight = 0
        view.addSubview(table)
        table.translatesAutoresizingMaskIntoConstraints = false
        NSLayoutConstraint.activate([
            table.topAnchor.constraint(equalTo: contentTopAnchor),
            table.leadingAnchor.constraint(equalTo: view.safeAreaLayoutGuide.leadingAnchor),
            table.trailingAnchor.constraint(equalTo: view.safeAreaLayoutGuide.trailingAnchor),
            table.bottomAnchor.constraint(equalTo: avoidsKeyboard
                ? view.keyboardLayoutGuide.topAnchor : view.safeAreaLayoutGuide.bottomAnchor),
        ])

        dataSource = DataSource(tableView: table) { [weak self] tableView, indexPath, rowID in
            let cell = tableView.dequeueReusableCell(
                withIdentifier: SettingsTableCell.reuseIdentifier, for: indexPath
            ) as! SettingsTableCell
            self?.configure(cell: cell, rowID: rowID)
            return cell
        }
        dataSource.owner = self
        dataSource.defaultRowAnimation = .none
        table.dataSource = dataSource
        if allowsRowReordering {
            table.setEditing(true, animated: false)
            table.allowsSelectionDuringEditing = true
        }
        registerForTraitChanges([UITraitPreferredContentSizeCategory.self]) {
            (page: SettingsListViewController, _: UITraitCollection) in
            page.reconfigureVisibleRows()
        }
        if !currentSections.isEmpty {
            applySections(currentSections, animated: false)
        }
    }

    public func row(forID id: String) -> SettingsListRow? {
        rowsByID[id]
    }

    @discardableResult
    public func invokeRow(id: String) -> Bool {
        guard let row = rowsByID[id], row.enabled else { return false }
        if let action = row.action {
            action()
            return true
        }
        if case .toggle(let isOn, let enabled, let onChange) = row.accessory, enabled {
            onChange(!isOn)
            return true
        }
        return false
    }

    open func applySections(_ sections: [SettingsListSection], animated: Bool = false) {
        currentSections = sections
        var nextRows: [String: SettingsListRow] = [:]
        var nextMeta: [String: (first: Bool, last: Bool)] = [:]
        var nextSections: [String: SettingsListSection] = [:]
        var snapshot = NSDiffableDataSourceSnapshot<String, String>()
        var existingIDs = Set(dataSource?.snapshot().itemIdentifiers ?? [])
        var toReconfigure: [String] = []

        for section in sections {
            nextSections[section.id] = section
            snapshot.appendSections([section.id])
            let count = section.rows.count
            var ids: [String] = []
            ids.reserveCapacity(count)
            for (index, row) in section.rows.enumerated() {
                nextRows[row.id] = row
                nextMeta[row.id] = (index == 0, index == count - 1)
                ids.append(row.id)
                if existingIDs.remove(row.id) != nil {
                    toReconfigure.append(row.id)
                }
            }
            snapshot.appendItems(ids, toSection: section.id)
        }
        rowsByID = nextRows
        sectionMetaByRowID = nextMeta
        sectionsByID = nextSections

        guard isViewLoaded, let dataSource else { return }
        if !toReconfigure.isEmpty {
            snapshot.reconfigureItems(toReconfigure)
        }
        dataSource.apply(snapshot, animatingDifferences: animated)
        refreshVisibleHeaders()
    }

    open override func applyTheme() {
        super.applyTheme()
        guard isViewLoaded, usesSettingsTable else { return }
        reconfigureVisibleRows()
        refreshVisibleHeaders()
    }

    public func reconfigureVisibleRows() {
        guard isViewLoaded, let dataSource else { return }
        for indexPath in table.indexPathsForVisibleRows ?? [] {
            guard let rowID = dataSource.itemIdentifier(for: indexPath),
                  let cell = table.cellForRow(at: indexPath) as? SettingsTableCell else { continue }
            configure(cell: cell, rowID: rowID)
        }
    }

    private func refreshVisibleHeaders() {
        guard isViewLoaded else { return }
        for sectionIndex in 0..<currentSections.count {
            if let header = table.headerView(forSection: sectionIndex) as? SettingsSectionHeaderView {
                header.configure(title: currentSections[sectionIndex].title ?? "",
                                 firstSection: sectionIndex == 0, theme: theme)
            }
        }
    }

    private func configure(cell: SettingsTableCell, rowID: String) {
        guard let row = rowsByID[rowID], let meta = sectionMetaByRowID[rowID] else { return }
        var value: String?
        var disclosure = false
        var selected = false
        var toggle: (isOn: Bool, enabled: Bool, onChange: @MainActor (Bool) -> Void)?
        var menu: (title: String, menu: UIMenu, enabled: Bool)?
        var trailingView: UIView?

        switch row.accessory {
        case .none:
            break
        case .disclosure:
            disclosure = true
        case .checkmark:
            selected = true
        case .value(let text, let showsDisclosure):
            value = text
            disclosure = showsDisclosure
        case .toggle(let isOn, let toggleEnabled, let onChange):
            toggle = (isOn, toggleEnabled, onChange)
        case .menu(let title, let uiMenu, let menuEnabled):
            menu = (title, uiMenu, menuEnabled)
        case .custom(let view):
            trailingView = view
        }

        cell.configure(
            title: row.title,
            detail: row.detail,
            detailDestructive: row.detailDestructive,
            value: value,
            leadingView: row.leadingView,
            trailingView: trailingView,
            toggle: toggle,
            menu: menu,
            embeddedContent: row.embeddedContent,
            theme: theme,
            first: meta.first,
            last: meta.last,
            destructive: row.destructive,
            enabled: row.enabled,
            actionable: row.action != nil,
            selected: selected,
            disclosure: disclosure,
            usesSettingsColors: usesSettingsColors,
            accessibilityIdentifier: row.id,
            accessibilityLabel: row.accessibilityLabel,
            accessibilityValue: row.accessibilityValue
        )
    }

    open func didMoveRow(from sourceIndexPath: IndexPath, to destinationIndexPath: IndexPath) {}

    public func tableView(_ tableView: UITableView, heightForHeaderInSection section: Int) -> CGFloat {
        guard section < currentSections.count else { return 0 }
        return usesSettingsColors
            ? table.heightForHeader(in: section, title: currentSections[section].title)
            : (currentSections[section].title?.isEmpty == false ? UITableView.automaticDimension : 0.01)
    }

    public func tableView(_ tableView: UITableView, viewForHeaderInSection section: Int) -> UIView? {
        guard section < currentSections.count else { return nil }
        let title = currentSections[section].title
        if usesSettingsColors {
            return table.viewForHeader(in: section, title: title, theme: theme)
        }
        guard let title, !title.isEmpty else { return UIView() }
        return table.viewForHeader(in: section, title: title, theme: theme)
    }

    public func tableView(_ tableView: UITableView, didSelectRowAt indexPath: IndexPath) {
        tableView.deselectRow(at: indexPath, animated: true)
        guard let rowID = dataSource.itemIdentifier(for: indexPath),
              let row = rowsByID[rowID], row.enabled else { return }
        row.action?()
    }

    public func tableView(_ tableView: UITableView, editingStyleForRowAt indexPath: IndexPath) -> UITableViewCell.EditingStyle {
        .none
    }

    public func tableView(_ tableView: UITableView, shouldIndentWhileEditingRowAt indexPath: IndexPath) -> Bool {
        false
    }

    public func tableView(_ tableView: UITableView,
                          targetIndexPathForMoveFromRowAt sourceIndexPath: IndexPath,
                          toProposedIndexPath proposedDestinationIndexPath: IndexPath) -> IndexPath {
        guard sourceIndexPath.section == proposedDestinationIndexPath.section else { return sourceIndexPath }
        return proposedDestinationIndexPath
    }

    // MARK: - Debug Support Helpers

    public func debugContentRows(maxItems: Int) -> [DebugContentRow] {
        let flat = currentSections.flatMap { section in
            section.rows.map { (sectionTitle: section.title, row: $0) }
        }
        return flat.prefix(maxItems).enumerated().map { idx, entry in
            let row = entry.row
            var actions: [String] = []
            var badge: String?
            var meta: [String: DebugJSONValue] = ["enabled": .bool(row.enabled)]
            if let sectionTitle = entry.sectionTitle, !sectionTitle.isEmpty {
                meta["section"] = .string(sectionTitle)
            }
            switch row.accessory {
            case .none:
                if row.action != nil { actions.append("invoke") }
            case .disclosure:
                actions.append("open")
            case .checkmark:
                badge = "selected"
                if row.action != nil { actions.append("invoke") }
            case .value(let text, _):
                badge = text
                if row.action != nil { actions.append("invoke") }
            case .toggle(let isOn, let enabled, _):
                badge = isOn ? "on" : "off"
                meta["isOn"] = .bool(isOn)
                if enabled { actions.append("toggle") }
            case .menu(let title, _, _):
                badge = title
            case .custom:
                if row.action != nil { actions.append("invoke") }
            }
            return DebugContentRow(
                index: idx,
                id: row.id,
                title: row.title.isEmpty ? (row.accessibilityLabel ?? row.id) : row.title,
                subtitle: row.detail,
                state: badge,
                actions: actions,
                meta: meta
            )
        }
    }

    public func debugInvokeRow(params: [String: DebugJSONValue]) throws -> DebugJSONValue {
        let allRows = currentSections.flatMap(\.rows)
        let target: SettingsListRow
        if let id = params["id"]?.stringValue {
            guard let found = allRows.first(where: { $0.id == id }) else {
                throw DebugAPIError.notFound("Settings row not found: \(id)")
            }
            target = found
        } else {
            let idx = params["index"]?.intValue ?? 0
            guard allRows.indices.contains(idx) else {
                throw DebugAPIError.invalidArgument("Settings row index \(idx) out of range (0..<\(allRows.count))")
            }
            target = allRows[idx]
        }
        guard invokeRow(id: target.id) else {
            throw DebugAPIError.invalidState("Settings row '\(target.id)' is not actionable or disabled")
        }
        return .object(["invoked": .string(target.id), "title": .string(target.title)])
    }
}
#endif
