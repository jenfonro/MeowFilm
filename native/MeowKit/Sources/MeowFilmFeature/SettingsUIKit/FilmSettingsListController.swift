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
    var action: (() -> Void)? = nil
    var content: UIView? = nil
}

@MainActor
struct FilmSettingsSection {
    var title: String
    var rows: [FilmSettingsRow]
}

/// Native list chrome shared only by the three film settings pages.
@MainActor
class FilmSettingsListController: PageViewController, UITableViewDataSource, UITableViewDelegate {
    var avoidsKeyboard: Bool { false }
    let table = UITableView(frame: .zero, style: .insetGrouped)
    var sections: [FilmSettingsSection] = []
    private var pendingFailure: (String, (() -> Void)?)?

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
        if isViewLoaded { table.reloadData() }
    }

    func render() { if isViewLoaded { table.reloadData() } }
    func numberOfSections(in tableView: UITableView) -> Int { sections.count }
    func tableView(_ tableView: UITableView, numberOfRowsInSection section: Int) -> Int { sections[section].rows.count }
    func tableView(_ tableView: UITableView, titleForHeaderInSection section: Int) -> String? {
        sections[section].title.isEmpty ? nil : sections[section].title
    }
    func tableView(_ tableView: UITableView, cellForRowAt indexPath: IndexPath) -> UITableViewCell {
        let row = sections[indexPath.section].rows[indexPath.row]
        let cell = UITableViewCell(style: .subtitle, reuseIdentifier: nil)
        if let field = row.content {
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
            cell.backgroundColor = UIColor(theme.surface)
            cell.selectionStyle = .none
            return cell
        }
        var content = cell.defaultContentConfiguration()
        content.text = row.title; content.secondaryText = row.detail
        content.textProperties.font = .preferredFont(forTextStyle: .body)
        content.secondaryTextProperties.font = .preferredFont(forTextStyle: .subheadline)
        content.textProperties.color = UIColor(row.enabled ? theme.ink : theme.muted)
        content.secondaryTextProperties.color = UIColor(theme.muted)
        content.textProperties.numberOfLines = 0; content.secondaryTextProperties.numberOfLines = 0
        cell.contentConfiguration = content
        cell.backgroundColor = UIColor(theme.surface)
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
    func tableView(_ tableView: UITableView, didSelectRowAt indexPath: IndexPath) {
        tableView.deselectRow(at: indexPath, animated: true)
        let row = sections[indexPath.section].rows[indexPath.row]
        if row.enabled { row.action?() }
    }

    func failure(_ error: Error, retry: (() -> Void)? = nil) {
        guard !(error is CancellationError) else { return }
        pendingFailure = (error.localizedDescription, retry)
        presentPendingFailure()
    }
    override func pageDidFinishAppearing() { presentPendingFailure() }
    private func presentPendingFailure() {
        guard let (message, retry) = pendingFailure, canPresentPageAlert else { return }
        pendingFailure = nil
        let alert = UIAlertController(title: "错误", message: message, preferredStyle: .alert)
        alert.addAction(UIAlertAction(title: "取消", style: .cancel))
        if let retry { alert.addAction(UIAlertAction(title: "重试", style: .default) { _ in retry() }) }
        present(alert, animated: true)
    }
    func confirm(_ title: String, message: String? = nil, action: @escaping () -> Void) {
        guard canPresentPageAlert else { return }
        let alert = UIAlertController(title: title, message: message, preferredStyle: .alert)
        alert.addAction(UIAlertAction(title: "取消", style: .cancel))
        alert.addAction(UIAlertAction(title: "确认", style: .destructive) { _ in action() })
        present(alert, animated: true)
    }
    func notice(_ title: String, message: String? = nil) {
        guard canPresentPageAlert else { return }
        let alert = UIAlertController(title: title, message: message, preferredStyle: .alert)
        alert.addAction(UIAlertAction(title: "确定", style: .default))
        present(alert, animated: true)
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
func filmSwitchRow(_ title: String, id: String, isOn: Bool, enabled: Bool, ink: UIColor,
                   change: @escaping (Bool) -> Void) -> FilmSettingsRow {
    let control = UISwitch()
    control.isOn = isOn; control.isEnabled = enabled
    control.accessibilityLabel = title; control.accessibilityIdentifier = id + ".switch"
    control.addAction(UIAction { [weak control] _ in
        if let control { change(control.isOn) }
    }, for: .valueChanged)
    let label = UILabel()
    label.text = title; label.textColor = ink
    label.font = .preferredFont(forTextStyle: .body)
    label.adjustsFontForContentSizeCategory = true; label.numberOfLines = 0
    let stack = UIStackView(arrangedSubviews: [label, control])
    stack.alignment = .center; stack.spacing = 12
    stack.heightAnchor.constraint(greaterThanOrEqualToConstant: 44).isActive = true
    return .init(title: title, id: id, enabled: enabled, action: { change(!isOn) }, content: stack)
}
#endif
