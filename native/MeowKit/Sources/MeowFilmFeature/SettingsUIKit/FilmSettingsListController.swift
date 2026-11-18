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
    var action: (() -> Void)? = nil
    var content: UIView? = nil
    var stylesContent = true
}

@MainActor
struct FilmSettingsSection {
    var title: String
    var rows: [FilmSettingsRow]
}

/// Native list chrome shared by film settings pages.
@MainActor
class FilmSettingsListController: PageViewController, UITableViewDataSource, UITableViewDelegate {
    var avoidsKeyboard: Bool { false }
    var usesSettingsColors: Bool { true }
    var usesSettingsTable: Bool { true }
    let table = SettingsTableView()
    var sections: [FilmSettingsSection] = []
    private var pendingFailure: (String, (() -> Void)?)?
    private var pendingNotice: (String, String?)?
    private var alertChoices: [String: () -> Void] = [:]
    private weak var actionAlert: UIAlertController?

    override func viewDidLoad() {
        super.viewDidLoad()
        guard usesSettingsTable else { return }
        if !usesSettingsColors { table.separatorStyle = .singleLine }
        table.rowHeight = UITableView.automaticDimension
        table.estimatedRowHeight = 60
        table.keyboardDismissMode = .interactive
        table.dataSource = self; table.delegate = self
        table.translatesAutoresizingMaskIntoConstraints = false
        view.addSubview(table)
        NSLayoutConstraint.activate([
            table.topAnchor.constraint(equalTo: contentTopAnchor),
            table.leadingAnchor.constraint(equalTo: view.safeAreaLayoutGuide.leadingAnchor),
            table.trailingAnchor.constraint(equalTo: view.safeAreaLayoutGuide.trailingAnchor),
            table.bottomAnchor.constraint(equalTo: avoidsKeyboard
                ? view.keyboardLayoutGuide.topAnchor : view.safeAreaLayoutGuide.bottomAnchor),
        ])
    }

    override func applyTheme() {
        super.applyTheme()
        if isViewLoaded {
            if usesSettingsColors {
                view.tintColor = UIColor(theme.settings.accent)
            }
            for cell in table.visibleCells {
                guard let path = table.indexPath(for: cell) else { continue }
                let row = sections[path.section].rows[path.row]
                cell.tintColor = UIColor(usesSettingsColors ? theme.settings.accent : theme.accent)
                (cell.backgroundView as? SettingsRowBackgroundView)?.applyTheme(theme)
                (cell.selectedBackgroundView as? SettingsRowBackgroundView)?.applyTheme(theme)
                if var content = cell.contentConfiguration as? UIListContentConfiguration {
                    applyTextColors(to: &content, row: row)
                    cell.contentConfiguration = content
                }
                if usesSettingsColors, row.stylesContent, let content = row.content {
                    applyControlColors(in: content)
                }
            }
            if usesSettingsColors {
                for section in sections.indices {
                    if let header = table.headerView(forSection: section) {
                        applyHeaderColors(in: header)
                    }
                }
            }
        }
    }

    override func responds(to selector: Selector!) -> Bool {
        // The non-settings directory keeps UIKit's native grouped-header policy.
        if !usesSettingsColors && (
            selector == #selector(UITableViewDelegate.tableView(_:heightForHeaderInSection:)) ||
            selector == #selector(UITableViewDelegate.tableView(_:viewForHeaderInSection:))
        ) { return false }
        return super.responds(to: selector)
    }

    func render() { if isViewLoaded { table.reloadData() } }
    func numberOfSections(in tableView: UITableView) -> Int { sections.count }
    func tableView(_ tableView: UITableView, numberOfRowsInSection section: Int) -> Int { sections[section].rows.count }
    func tableView(_ tableView: UITableView, titleForHeaderInSection section: Int) -> String? {
        if usesSettingsColors && section == 0 { return nil }
        return sections[section].title.isEmpty ? nil : sections[section].title
    }
    func tableView(_ tableView: UITableView, heightForHeaderInSection section: Int) -> CGFloat {
        usesSettingsColors ? table.heightForHeader(in: section, title: sections[section].title) : UITableView.automaticDimension
    }
    func tableView(_ tableView: UITableView, viewForHeaderInSection section: Int) -> UIView? {
        usesSettingsColors ? table.viewForHeader(in: section, title: sections[section].title, theme: theme) : nil
    }
    func tableView(_ tableView: UITableView, willDisplayHeaderView view: UIView, forSection section: Int) {
        if usesSettingsColors, section != 0 {
            let label = (view as? UITableViewHeaderFooterView)?.textLabel
            label?.font = .preferredFont(forTextStyle: .subheadline)
            label?.adjustsFontForContentSizeCategory = true
            label?.textColor = UIColor(theme.settings.muted)
        }
    }
    func tableView(_ tableView: UITableView, cellForRowAt indexPath: IndexPath) -> UITableViewCell {
        let row = sections[indexPath.section].rows[indexPath.row]
        let first = indexPath.row == 0
        let last = indexPath.row == sections[indexPath.section].rows.count - 1
        let cell = SettingsTableCell(title: row.title, detail: row.detail, theme: theme,
            first: first, last: last, destructive: row.destructive)
        var content = cell.contentConfiguration as! UIListContentConfiguration
        if !usesSettingsColors {
            // The playback directory borrows this list but is not a settings page.
            content = cell.defaultContentConfiguration()
            content.text = row.title; content.secondaryText = row.detail
            content.textProperties.font = .preferredFont(forTextStyle: .body)
            content.secondaryTextProperties.font = .preferredFont(forTextStyle: .subheadline)
            cell.tintColor = UIColor(theme.accent)
            cell.backgroundView = SettingsRowBackgroundView(theme: theme, first: first, last: last, settings: false)
            cell.selectedBackgroundView = nil
            cell.contentView.heightAnchor.constraint(greaterThanOrEqualToConstant: 56).isActive = true
        }
        if let field = row.content {
            if usesSettingsColors && row.stylesContent { applyControlColors(in: field) }
            cell.accessibilityIdentifier = row.id.isEmpty ? field.accessibilityIdentifier : row.id
            cell.contentConfiguration = nil
            cell.contentView.directionalLayoutMargins = content.directionalLayoutMargins
            field.removeFromSuperview()
            cell.contentView.addSubview(field)
            field.translatesAutoresizingMaskIntoConstraints = false
            NSLayoutConstraint.activate([
                field.topAnchor.constraint(equalTo: cell.contentView.layoutMarginsGuide.topAnchor),
                field.bottomAnchor.constraint(equalTo: cell.contentView.layoutMarginsGuide.bottomAnchor),
                field.leadingAnchor.constraint(equalTo: cell.contentView.layoutMarginsGuide.leadingAnchor),
                field.trailingAnchor.constraint(equalTo: cell.contentView.layoutMarginsGuide.trailingAnchor),
            ])
            cell.selectionStyle = .none
            return cell
        }
        applyTextColors(to: &content, row: row)
        content.textProperties.numberOfLines = 0; content.secondaryTextProperties.numberOfLines = 0
        cell.contentConfiguration = content
        cell.isAccessibilityElement = true
        cell.accessibilityIdentifier = row.id
        cell.accessibilityLabel = row.title
        cell.accessibilityValue = row.detail
        cell.accessoryType = row.selected ? .checkmark : (row.disclosure ? .disclosureIndicator : .none)
        cell.selectionStyle = row.action == nil || !row.enabled ? .none : .default
        cell.accessibilityTraits = row.action == nil ? .staticText : .button
        if !row.enabled { cell.accessibilityTraits.insert(.notEnabled) }
        if row.selected { cell.accessibilityTraits.insert(.selected) }
        return cell
    }
    private func applyTextColors(to content: inout UIListContentConfiguration, row: FilmSettingsRow) {
        if usesSettingsColors {
            content.textProperties.color = UIColor(!row.enabled ? theme.settings.muted :
                (row.destructive ? theme.settings.danger :
                    (row.action != nil && !row.disclosure ? theme.settings.accent : theme.settings.ink)))
            content.secondaryTextProperties.color = UIColor(theme.settings.muted)
        } else {
            content.textProperties.color = UIColor(row.enabled ? theme.ink : theme.muted)
            content.secondaryTextProperties.color = UIColor(theme.muted)
        }
    }
    private func applyHeaderColors(in view: UIView) {
        if let label = view as? UILabel { label.textColor = UIColor(theme.settings.muted) }
        for child in view.subviews { applyHeaderColors(in: child) }
    }
    private func applyControlColors(in view: UIView) {
        let colors = theme.settings
        if let section = view as? SettingsInputSection { section.applyTheme(theme) }
        else if let field = view as? UITextField { colors.apply(to: field) }
        else if let field = view as? UITextView { colors.apply(to: field) }
        else if let toggle = view as? UISwitch { toggle.onTintColor = UIColor(colors.accent) }
        else if let button = view as? PageHeaderTextButton {
            button.setTitleColor(UIColor(colors.accent), for: .normal)
            button.setTitleColor(UIColor(colors.muted), for: .disabled)
        }
        else if let control = view as? UISegmentedControl {
            control.backgroundColor = UIColor(colors.input)
            control.selectedSegmentTintColor = UIColor(colors.surface)
            control.setTitleTextAttributes([.foregroundColor: UIColor(colors.ink)], for: .normal)
            control.setTitleTextAttributes([.foregroundColor: UIColor(colors.ink)], for: .selected)
        } else {
            for child in view.subviews { applyControlColors(in: child) }
        }
    }
    func tableView(_ tableView: UITableView, didSelectRowAt indexPath: IndexPath) {
        tableView.deselectRow(at: indexPath, animated: true)
        let row = sections[indexPath.section].rows[indexPath.row]
        if row.enabled { row.action?() }
    }

    func failure(_ error: Error, retry: (() -> Void)? = nil) {
        guard !(error is CancellationError) else { return }
        pendingFailure = (error.localizedDescription, retry)
        presentPendingFeedback()
    }
    override func pageDidFinishAppearing() { presentPendingFeedback() }
    private func presentPendingFeedback() {
        guard canPresentPageAlert else { return }
        if let (message, retry) = pendingFailure {
            pendingFailure = nil
            presentActionAlert("错误", message: message, choices: retry.map { [("重试", UIAlertAction.Style.default, $0)] } ?? [])
        } else if let (title, message) = pendingNotice {
            pendingNotice = nil
            let alert = UIAlertController(title: title, message: message, preferredStyle: .alert)
            alert.addAction(UIAlertAction(title: "确定", style: .default) { [weak self, weak alert] _ in
                guard let self, let alert else { return }
                dismissPageModal(alert)
            })
            present(alert, animated: true)
        }
    }
    func confirm(_ title: String, message: String? = nil, action: @escaping () -> Void) {
        presentActionAlert(title, message: message, choices: [("确认", .destructive, action)])
    }
    func retryNotice(_ title: String, message: String, retry: @escaping () -> Void) {
        presentActionAlert(title, message: message, choices: [("重试", .default, retry)])
    }
    private func presentActionAlert(_ title: String, message: String?,
                                    choices: [(String, UIAlertAction.Style, () -> Void)]) {
        guard canPresentPageAlert else { return }
        let alert = UIAlertController(title: title, message: message, preferredStyle: .alert)
        actionAlert = alert
        alertChoices = ["取消": {}]
        for (title, _, action) in choices { alertChoices[title] = action }
        for (title, style) in [("取消", UIAlertAction.Style.cancel)] + choices.map({ ($0.0, $0.1) }) {
            alert.addAction(UIAlertAction(title: title, style: style) { [weak self] _ in self?.selectAlertChoice(title) })
        }
        present(alert, animated: true)
    }
    // The same choice path serves UIKit actions and component tests. A retry may
    // fail immediately, so release the previous alert's slot before executing it.
    func selectAlertChoice(_ title: String) {
        guard let alert = actionAlert, let action = alertChoices[title] else { return }
        alertChoices.removeAll(); actionAlert = nil
        dismissPageModal(alert, completion: action)
    }
    func notice(_ title: String, message: String? = nil) {
        pendingNotice = (title, message)
        presentPendingFeedback()
    }
}

@MainActor
func filmTextField(_ title: String, id: String, value: String = "") -> UITextField {
    let field = SettingsTextField()
    field.text = value
    field.placeholder = title
    field.accessibilityLabel = title; field.accessibilityIdentifier = id
    field.font = .preferredFont(forTextStyle: .body)
    field.adjustsFontForContentSizeCategory = true
    field.autocorrectionType = .no; field.autocapitalizationType = .none
    field.heightAnchor.constraint(greaterThanOrEqualToConstant: 36).isActive = true
    return field
}

@MainActor
func filmTextView(_ title: String, id: String, value: String = "") -> UITextView {
    let field = SettingsTextView()
    field.text = value; field.accessibilityLabel = title; field.accessibilityIdentifier = id
    field.font = .preferredFont(forTextStyle: .body)
    field.adjustsFontForContentSizeCategory = true; field.backgroundColor = .clear
    field.autocorrectionType = .no; field.autocapitalizationType = .none
    field.smartQuotesType = .no; field.smartDashesType = .no
    field.heightAnchor.constraint(equalToConstant: 160).isActive = true
    return field
}

@MainActor
func filmSwitchRow(_ title: String, id: String, isOn: Bool, enabled: Bool,
                   change: @escaping (Bool) -> Void) -> FilmSettingsRow {
    let switchView = SettingsSwitch()
    let control = switchView.control
    control.isOn = isOn; control.isEnabled = enabled
    control.accessibilityLabel = title; control.accessibilityIdentifier = id + ".switch"
    control.addAction(UIAction { [weak control] _ in
        if let control { change(control.isOn) }
    }, for: .valueChanged)
    let label = UILabel()
    label.text = title
    label.textColor = UIColor { UIColor(SettingsTheme(dark: $0.userInterfaceStyle == .dark).ink) }
    label.font = .preferredFont(forTextStyle: .body)
    label.adjustsFontForContentSizeCategory = true; label.numberOfLines = 0
    let stack = FilmSwitchRowStack(arrangedSubviews: [label, switchView])
    stack.alignment = .center; stack.spacing = 12
    return .init(title: title, id: id, enabled: enabled, action: { change(!isOn) }, content: stack)
}

@MainActor
private final class FilmSwitchRowStack: UIStackView {
    override func point(inside point: CGPoint, with event: UIEvent?) -> Bool {
        super.point(inside: point, with: event) || arrangedSubviews.contains {
            $0.point(inside: $0.convert(point, from: self), with: event)
        }
    }
}
#endif
