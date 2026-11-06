import UIKit
import MeowUI

/// App-level categories are independent of either business module's owner.
@MainActor
final class MeowfilmSettingsViewController: PageViewController, UITableViewDataSource, UITableViewDelegate {
    var openMusic: (() -> Void)?
    var openFilm: (() -> Void)?
    private let table = UITableView(frame: .zero, style: .insetGrouped)

    override func viewDidLoad() {
        pageTitle = "设置"
        super.viewDidLoad()
        table.backgroundColor = .clear; table.dataSource = self; table.delegate = self
        table.rowHeight = UITableView.automaticDimension; table.estimatedRowHeight = 54
        view.addSubview(table); table.translatesAutoresizingMaskIntoConstraints = false
        NSLayoutConstraint.activate([
            table.topAnchor.constraint(equalTo: contentTopAnchor),
            table.leadingAnchor.constraint(equalTo: view.safeAreaLayoutGuide.leadingAnchor),
            table.trailingAnchor.constraint(equalTo: view.safeAreaLayoutGuide.trailingAnchor),
            table.bottomAnchor.constraint(equalTo: view.bottomAnchor),
        ])
    }
    override func applyTheme() { super.applyTheme(); if isViewLoaded { table.reloadData() } }
    func tableView(_ tableView: UITableView, numberOfRowsInSection section: Int) -> Int { 2 }
    func tableView(_ tableView: UITableView, cellForRowAt indexPath: IndexPath) -> UITableViewCell {
        let cell = UITableViewCell(style: .default, reuseIdentifier: nil)
        var content = cell.defaultContentConfiguration()
        content.text = indexPath.row == 0 ? "音乐" : "影视"
        content.textProperties.font = .preferredFont(forTextStyle: .body)
        content.textProperties.color = UIColor(theme.ink)
        content.textProperties.numberOfLines = 0
        cell.contentConfiguration = content; cell.backgroundColor = UIColor(theme.surface)
        cell.accessoryType = .disclosureIndicator; cell.accessibilityTraits = .button
        cell.isAccessibilityElement = true
        cell.accessibilityLabel = indexPath.row == 0 ? "音乐" : "影视"
        cell.accessibilityIdentifier = indexPath.row == 0 ? "settings.category.music" : "settings.category.film"
        return cell
    }
    func tableView(_ tableView: UITableView, didSelectRowAt indexPath: IndexPath) {
        tableView.deselectRow(at: indexPath, animated: true)
        if indexPath.row == 0 { openMusic?() } else { openFilm?() }
    }
}
