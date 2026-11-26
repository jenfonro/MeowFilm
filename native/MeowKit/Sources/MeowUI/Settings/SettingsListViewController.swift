#if os(iOS)
import UIKit
import MeowDebug

/// Declarative row model for the unified native UIKit `SettingsListViewController`.
/// All custom view slots are compile-time constrained to `ThemeableView` (`UIView & PageThemeable`).
@MainActor
public struct SettingsListRow {
    public enum LeadingIcon: Equatable {
        case symbol(String)
        case asset(String?, fallbackSymbol: String = "music.note")
        case badge(assetName: String?, fallbackSymbol: String = "music.note", size: CGFloat = 36)
    }

    public enum Accessory {
        case none
        case disclosure
        case checkmark
        case value(String, disclosure: Bool = false)
        case toggle(isOn: Bool, enabled: Bool = true, onChange: @MainActor (Bool) -> Void)
        case menu(title: String, menu: UIMenu, enabled: Bool = true)
        case loading(accessibilityLabel: String)
        case hint(String)
        case action(title: String, enabled: Bool = true, destructive: Bool = false, onTap: @MainActor () -> Void)
    }

    public let id: String
    public let title: String
    public let detail: String?
    public let detailDestructive: Bool
    public let leadingIcon: LeadingIcon?
    public let embeddedContent: ThemeableView?
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
        leadingIcon: LeadingIcon? = nil,
        embeddedContent: ThemeableView? = nil,
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
        self.leadingIcon = leadingIcon
        self.embeddedContent = embeddedContent
        self.accessory = accessory
        self.destructive = destructive
        self.enabled = enabled
        self.accessibilityLabel = accessibilityLabel
        self.accessibilityValue = accessibilityValue
        self.canMove = canMove
        self.action = action
    }

    struct DisplaySignature: Equatable {
        enum AccessorySignature: Equatable {
            case none
            case disclosure
            case checkmark
            case value(String, Bool)
            case toggle(Bool, Bool)
            case menu(String, Bool, [String])
            case loading(String)
            case hint(String)
            case action(String, Bool, Bool)
        }

        let title: String
        let detail: String?
        let detailDestructive: Bool
        let destructive: Bool
        let enabled: Bool
        let hasAction: Bool
        let canMove: Bool
        let accessibilityLabel: String?
        let accessibilityValue: String?
        let leadingIcon: LeadingIcon?
        let embeddedID: ObjectIdentifier?
        let accessory: AccessorySignature
        let first: Bool
        let last: Bool
    }

    func displaySignature(first: Bool, last: Bool) -> DisplaySignature {
        let acc: DisplaySignature.AccessorySignature
        switch accessory {
        case .none: acc = .none
        case .disclosure: acc = .disclosure
        case .checkmark: acc = .checkmark
        case .value(let text, let disc): acc = .value(text, disc)
        case .toggle(let isOn, let enabled, _): acc = .toggle(isOn, enabled)
        case .menu(let title, let menu, let enabled):
            let items = menu.children.map { element -> String in
                if let action = element as? UIAction {
                    return "\(action.title):\(action.state.rawValue):\(action.attributes.rawValue)"
                }
                return element.title
            }
            acc = .menu(title, enabled, items)
        case .loading(let label): acc = .loading(label)
        case .hint(let text): acc = .hint(text)
        case .action(let title, let enabled, let destructive, _): acc = .action(title, enabled, destructive)
        }
        return DisplaySignature(
            title: title,
            detail: detail,
            detailDestructive: detailDestructive,
            destructive: destructive,
            enabled: enabled,
            hasAction: action != nil,
            canMove: canMove,
            accessibilityLabel: accessibilityLabel,
            accessibilityValue: accessibilityValue,
            leadingIcon: leadingIcon,
            embeddedID: embeddedContent.map(ObjectIdentifier.init),
            accessory: acc,
            first: first,
            last: last
        )
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

/// Unified pure UIKit settings table controller backed by `MeowUI.IndexedDiffableProjection`
/// and persistent-slot `SettingsTableCell` + `SettingsSectionHeaderView` (zero `reloadData()`).
@MainActor
open class SettingsListViewController: PageViewController, UITableViewDelegate {
    public let table = SettingsTableView()
    public private(set) var currentSections: [SettingsListSection] = []
    open var usesSettingsColors: Bool { true }
    open var avoidsKeyboard: Bool { true }
    open var allowsRowReordering: Bool { false }

    private struct ProjectedRow {
        let row: SettingsListRow
        let first: Bool
        let last: Bool
        let signature: SettingsListRow.DisplaySignature
    }

    private var projection = IndexedDiffableProjection<String, String, ProjectedRow>()
    private var dataSource: DataSource!

    private final class DataSource: UITableViewDiffableDataSource<String, String> {
        weak var owner: SettingsListViewController?

        override func tableView(_ tableView: UITableView, canEditRowAt indexPath: IndexPath) -> Bool {
            guard let owner, owner.allowsRowReordering,
                  let id = itemIdentifier(for: indexPath),
                  let entry = owner.projection[id] else { return false }
            return entry.row.canMove
        }

        override func tableView(_ tableView: UITableView, canMoveRowAt indexPath: IndexPath) -> Bool {
            guard let owner, owner.allowsRowReordering,
                  let id = itemIdentifier(for: indexPath),
                  let entry = owner.projection[id] else { return false }
            return entry.row.canMove
        }

        override func tableView(_ tableView: UITableView, moveRowAt sourceIndexPath: IndexPath,
                                to destinationIndexPath: IndexPath) {
            owner?.didMoveRow(from: sourceIndexPath, to: destinationIndexPath)
        }
    }

    open override func viewDidLoad() {
        super.viewDidLoad()
        table.delegate = self
        table.keyboardDismissMode = .interactive
        table.sectionFooterHeight = 0
        table.separatorColor = UIColor(usesSettingsColors ? theme.settings.separator : theme.border)
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

    @discardableResult
    public func invokeRow(id: String) -> Bool {
        guard let row = projection[id]?.row, row.enabled else { return false }
        if let action = row.action {
            action()
            return true
        }
        if case .toggle(let isOn, let enabled, let onChange) = row.accessory, enabled {
            onChange(!isOn)
            return true
        }
        if case .action(_, let enabled, _, let onTap) = row.accessory, enabled {
            onTap()
            return true
        }
        return false
    }

    open func applySections(_ sections: [SettingsListSection], animated: Bool = false) {
        var seenSections: Set<String> = []
        var seenRows: Set<String> = []
        var normalizedSections: [SettingsListSection] = []
        var projectedInput: [(id: String, items: [ProjectedRow])] = []
        normalizedSections.reserveCapacity(sections.count)
        projectedInput.reserveCapacity(sections.count)

        for section in sections where seenSections.insert(section.id).inserted {
            var dedupedRows: [SettingsListRow] = []
            dedupedRows.reserveCapacity(section.rows.count)
            for row in section.rows where seenRows.insert(row.id).inserted {
                dedupedRows.append(row)
            }
            normalizedSections.append(SettingsListSection(id: section.id, title: section.title, rows: dedupedRows))
            let count = dedupedRows.count
            let projected = dedupedRows.enumerated().map { index, row in
                let first = index == 0
                let last = index == count - 1
                return ProjectedRow(
                    row: row,
                    first: first,
                    last: last,
                    signature: row.displaySignature(first: first, last: last)
                )
            }
            projectedInput.append((id: section.id, items: projected))
        }
        let previousSections = currentSections
        currentSections = normalizedSections

        guard isViewLoaded, let dataSource else { return }
        let update = projection.update(
            sections: projectedInput,
            id: { $0.row.id },
            isChanged: { $0.signature != $1.signature }
        )
        let previousTitlePresence = Dictionary(
            previousSections.map { ($0.id, $0.title?.isEmpty == false) },
            uniquingKeysWith: { first, _ in first }
        )
        let headerTransitionSections = normalizedSections.compactMap { section -> String? in
            guard let hadTitle = previousTitlePresence[section.id] else { return nil }
            let hasTitle = section.title?.isEmpty == false
            return hadTitle != hasTitle ? section.id : nil
        }
        if update.hasChanges {
            dataSource.apply(update.snapshot, animatingDifferences: animated)
        }
        if !headerTransitionSections.isEmpty {
            var headerSnapshot = dataSource.snapshot()
            let valid = Set(headerSnapshot.sectionIdentifiers)
            let reloadIDs = headerTransitionSections.filter { valid.contains($0) }
            if !reloadIDs.isEmpty {
                headerSnapshot.reloadSections(reloadIDs)
                dataSource.apply(headerSnapshot, animatingDifferences: false)
            }
        }
        // Refresh closures on unchanged visible cells without triggering a cell reconfigure pass.
        let changedSet = Set(update.changedItemIDs)
        for indexPath in table.indexPathsForVisibleRows ?? [] {
            guard let rowID = dataSource.itemIdentifier(for: indexPath),
                  !changedSet.contains(rowID),
                  let entry = projection[rowID],
                  let cell = table.cellForRow(at: indexPath) as? SettingsTableCell else { continue }
            cell.refreshInteractiveHandlers(row: entry.row)
        }
        refreshVisibleHeaders()
    }

    open override func applyTheme() {
        super.applyTheme()
        guard isViewLoaded else { return }
        table.separatorColor = UIColor(usesSettingsColors ? theme.settings.separator : theme.border)
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
        guard let entry = projection[rowID] else { return }
        cell.configure(
            row: entry.row,
            theme: theme,
            first: entry.first,
            last: entry.last,
            usesSettingsColors: usesSettingsColors
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
        return table.viewForHeader(in: section, title: currentSections[section].title, theme: theme)
    }

    public func tableView(_ tableView: UITableView, didSelectRowAt indexPath: IndexPath) {
        tableView.deselectRow(at: indexPath, animated: true)
        guard let rowID = dataSource.itemIdentifier(for: indexPath),
              let row = projection[rowID]?.row, row.enabled else { return }
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
            case .loading(let label):
                badge = label
            case .hint(let text):
                badge = text
                if row.action != nil { actions.append("invoke") }
            case .action(let title, let enabled, _, _):
                badge = title
                if enabled { actions.append("invoke") }
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
