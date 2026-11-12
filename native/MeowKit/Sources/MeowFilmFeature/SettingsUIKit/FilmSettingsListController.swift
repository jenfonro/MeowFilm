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
    let table = UITableView(frame: .zero, style: .insetGrouped)
    var sections: [FilmSettingsSection] = []
    private var pendingFailure: (String, (() -> Void)?)?
    private var pendingNotice: (String, String?)?
    private var alertChoices: [String: () -> Void] = [:]
    private weak var actionAlert: UIAlertController?

    override func viewDidLoad() {
        super.viewDidLoad()
        table.backgroundColor = .clear
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
                table.separatorColor = UIColor(theme.settings.separator)
            }
            table.reloadData()
        }
    }

    func render() { if isViewLoaded { table.reloadData() } }
    func numberOfSections(in tableView: UITableView) -> Int { sections.count }
    func tableView(_ tableView: UITableView, numberOfRowsInSection section: Int) -> Int { sections[section].rows.count }
    func tableView(_ tableView: UITableView, titleForHeaderInSection section: Int) -> String? {
        sections[section].title.isEmpty ? nil : sections[section].title
    }
    func tableView(_ tableView: UITableView, willDisplayHeaderView view: UIView, forSection section: Int) {
        if usesSettingsColors {
            (view as? UITableViewHeaderFooterView)?.textLabel?.textColor = UIColor(theme.settings.muted)
        }
    }
    func tableView(_ tableView: UITableView, cellForRowAt indexPath: IndexPath) -> UITableViewCell {
        let row = sections[indexPath.section].rows[indexPath.row]
        let cell = UITableViewCell(style: .subtitle, reuseIdentifier: nil)
        if let field = row.content {
            if usesSettingsColors { applyControlColors(in: field) }
            cell.accessibilityIdentifier = row.id.isEmpty ? field.accessibilityIdentifier : row.id
            field.removeFromSuperview()
            cell.contentView.addSubview(field)
            field.translatesAutoresizingMaskIntoConstraints = false
            NSLayoutConstraint.activate([
                field.topAnchor.constraint(equalTo: cell.contentView.layoutMarginsGuide.topAnchor),
                field.bottomAnchor.constraint(equalTo: cell.contentView.layoutMarginsGuide.bottomAnchor),
                field.leadingAnchor.constraint(equalTo: cell.contentView.layoutMarginsGuide.leadingAnchor),
                field.trailingAnchor.constraint(equalTo: cell.contentView.layoutMarginsGuide.trailingAnchor),
            ])
            applyBackground(cell, at: indexPath)
            cell.selectionStyle = .none
            return cell
        }
        var content = cell.defaultContentConfiguration()
        content.text = row.title; content.secondaryText = row.detail
        content.textProperties.font = .preferredFont(forTextStyle: .body)
        content.secondaryTextProperties.font = .preferredFont(forTextStyle: .subheadline)
        if usesSettingsColors {
            content.textProperties.color = UIColor(!row.enabled ? theme.settings.muted :
                (row.destructive ? theme.settings.danger :
                    (row.action != nil && !row.disclosure ? theme.settings.accent : theme.settings.ink)))
            content.secondaryTextProperties.color = UIColor(theme.settings.muted)
        } else {
            content.textProperties.color = UIColor(row.enabled ? theme.ink : theme.muted)
            content.secondaryTextProperties.color = UIColor(theme.muted)
        }
        content.textProperties.numberOfLines = 0; content.secondaryTextProperties.numberOfLines = 0
        cell.contentConfiguration = content
        applyBackground(cell, at: indexPath)
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
    private func applyBackground(_ cell: UITableViewCell, at index: IndexPath) {
        cell.backgroundColor = .clear
        cell.backgroundView = SettingsRowBackgroundView(theme: theme, first: index.row == 0,
            last: index.row == sections[index.section].rows.count - 1, settings: usesSettingsColors)
        if usesSettingsColors {
            cell.selectedBackgroundView = SettingsRowBackgroundView(theme: theme, first: index.row == 0,
                last: index.row == sections[index.section].rows.count - 1, selected: true)
        }
        cell.contentView.heightAnchor.constraint(greaterThanOrEqualToConstant: 56).isActive = true
    }
    private func applyControlColors(in view: UIView) {
        let colors = theme.settings
        if let field = view as? UITextField { colors.apply(to: field) }
        else if let field = view as? UITextView { colors.apply(to: field) }
        else if let toggle = view as? UISwitch { toggle.onTintColor = UIColor(colors.accent) }
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
    let field = UITextField()
    field.text = value
    field.placeholder = title
    field.accessibilityLabel = title; field.accessibilityIdentifier = id
    field.font = .preferredFont(forTextStyle: .body)
    field.adjustsFontForContentSizeCategory = true
    field.autocorrectionType = .no; field.autocapitalizationType = .none
    field.leftView = UIView(frame: CGRect(x: 0, y: 0, width: 8, height: 1))
    field.leftViewMode = .always
    field.heightAnchor.constraint(greaterThanOrEqualToConstant: 44).isActive = true
    return field
}

@MainActor
func filmTextView(_ title: String, id: String, value: String = "") -> UITextView {
    let field = UITextView()
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
    let control = UISwitch()
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
    let stack = UIStackView(arrangedSubviews: [label, control])
    stack.alignment = .center; stack.spacing = 12
    stack.heightAnchor.constraint(greaterThanOrEqualToConstant: 44).isActive = true
    return .init(title: title, id: id, enabled: enabled, action: { change(!isOn) }, content: stack)
}
#endif
