#if os(iOS)
import UIKit
import MeowUI

@MainActor
struct FilmSettingsRow {
    var title: String
    var detail: String? = nil
    var id: String
    var enabled = true
    var selected = false
    var disclosure = false
    var destructive = false
    var canMove = false
    var action: (@MainActor () -> Void)? = nil
    var toggle: (isOn: Bool, onChange: @MainActor (Bool) -> Void)? = nil
    var menu: (title: String, menu: UIMenu)? = nil
    var content: UIView? = nil
}

@MainActor
struct FilmSettingsSection {
    var id: String? = nil
    var title: String
    var rows: [FilmSettingsRow]
}

/// Film settings list controller built directly on `MeowUI.SettingsListViewController`
/// (reusable `SettingsTableCell`, `DiffableDataSource`, unified `PageViewController` feedback queue).
@MainActor
class FilmSettingsListController: SettingsListViewController {
    var sections: [FilmSettingsSection] = []

    override var avoidsKeyboard: Bool { false }

    override func viewDidLoad() {
        super.viewDidLoad()
        if usesSettingsTable, !usesSettingsColors {
            table.separatorStyle = .singleLine
        }
    }

    func render() {
        guard isViewLoaded, usesSettingsTable else { return }
        var usedRowIDs: Set<String> = []
        let listSections = sections.enumerated().map { sectionIndex, section in
            let sectionID = section.id ?? "film.section.\(sectionIndex).\(section.title)"
            let rows = section.rows.enumerated().map { rowIndex, row -> SettingsListRow in
                var candidate = row.id.isEmpty ? "\(sectionID).row.\(rowIndex)" : row.id
                if !usedRowIDs.insert(candidate).inserted {
                    candidate = "\(candidate)#\(sectionIndex).\(rowIndex)"
                    usedRowIDs.insert(candidate)
                }
                let accessory: SettingsListRow.Accessory
                if let toggle = row.toggle {
                    accessory = .toggle(isOn: toggle.isOn, enabled: row.enabled, onChange: toggle.onChange)
                } else if let menu = row.menu {
                    accessory = .menu(title: menu.title, menu: menu.menu, enabled: row.enabled)
                } else if row.selected {
                    accessory = .checkmark
                } else if row.disclosure {
                    accessory = .disclosure
                } else {
                    accessory = .none
                }
                return SettingsListRow(
                    id: candidate,
                    title: row.title,
                    detail: row.detail,
                    embeddedContent: row.content,
                    accessory: accessory,
                    destructive: row.destructive,
                    enabled: row.enabled,
                    canMove: row.canMove,
                    action: row.action
                )
            }
            return SettingsListSection(id: sectionID, title: section.title.isEmpty ? nil : section.title, rows: rows)
        }
        applySections(listSections, animated: false)
    }

    func failure(_ error: Error, retry: (@MainActor () -> Void)? = nil) {
        enqueueError(error.localizedDescription, retry: retry)
    }

    func confirm(_ title: String, message: String? = nil, action: @escaping @MainActor () -> Void) {
        guard canPresentPageAlert else { return }
        presentPageConfirmation(title: title, message: message, confirmTitle: "确认",
                                destructive: true, onConfirm: action)
    }

    func showRetry(_ title: String, message: String?, retry: @escaping @MainActor () -> Void) {
        enqueueError(message ?? title, retry: retry)
    }

    func notice(_ title: String, message: String? = nil) {
        enqueueNotice(title: title, message: message)
    }
}

@MainActor
func filmTextField(_ placeholder: String, id: String, value: String = "",
                   appearance: SettingsInputAppearance = .plain) -> SettingsTextField {
    let field = SettingsTextField()
    field.inputAppearance = appearance
    field.placeholder = placeholder; field.text = value
    field.font = .preferredFont(forTextStyle: .body)
    field.adjustsFontForContentSizeCategory = true
    field.borderStyle = .none
    field.autocorrectionType = .no; field.autocapitalizationType = .none
    field.smartQuotesType = .no; field.smartDashesType = .no
    field.accessibilityIdentifier = id; field.accessibilityLabel = placeholder
    return field
}

@MainActor
func filmSwitchRow(_ title: String, id: String, isOn: Bool, enabled: Bool = true,
                   change: @escaping @MainActor (Bool) -> Void) -> FilmSettingsRow {
    FilmSettingsRow(title: title, id: id, enabled: enabled, toggle: (isOn, change))
}
#endif
