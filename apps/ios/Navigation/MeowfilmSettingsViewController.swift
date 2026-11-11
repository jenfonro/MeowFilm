import UIKit
import MeowUI

/// App-level categories and the cold-start reset request are independent of
/// either business module's availability or page lifetime.
@MainActor
final class MeowfilmSettingsViewController: PageViewController, UITableViewDataSource, UITableViewDelegate {
    var openMusic: (() -> Void)?
    var openFilm: (() -> Void)?
    var openPreferences: (() -> Void)?
    var openCache: (() -> Void)?
    private let resetUserData: (@MainActor () async throws -> Void)?
    private var requestingReset = false
    private let table = UITableView(frame: .zero, style: .insetGrouped)

    init(themeProvider: any PageThemeProviding, resetUserData: (@MainActor () async throws -> Void)? = nil) {
        self.resetUserData = resetUserData
        super.init(themeProvider: themeProvider)
        pageTitle = "设置"
    }

    override func viewDidLoad() {
        super.viewDidLoad()
        table.backgroundColor = .clear; table.dataSource = self; table.delegate = self
        table.rowHeight = UITableView.automaticDimension; table.estimatedRowHeight = 56
        view.addSubview(table); table.translatesAutoresizingMaskIntoConstraints = false
        NSLayoutConstraint.activate([
            table.topAnchor.constraint(equalTo: contentTopAnchor),
            table.leadingAnchor.constraint(equalTo: view.safeAreaLayoutGuide.leadingAnchor),
            table.trailingAnchor.constraint(equalTo: view.safeAreaLayoutGuide.trailingAnchor),
            table.bottomAnchor.constraint(equalTo: view.bottomAnchor),
        ])
    }
    override func applyTheme() { super.applyTheme(); if isViewLoaded { table.reloadData() } }
    func numberOfSections(in tableView: UITableView) -> Int { resetUserData == nil ? 2 : 3 }
    func tableView(_ tableView: UITableView, numberOfRowsInSection section: Int) -> Int { section < 2 ? 2 : 1 }
    func tableView(_ tableView: UITableView, cellForRowAt indexPath: IndexPath) -> UITableViewCell {
        let cell = UITableViewCell(style: .default, reuseIdentifier: nil)
        let label: String, identifier: String
        switch (indexPath.section, indexPath.row) {
        case (0, 0): label = "音乐设置"; identifier = "settings.category.music"
        case (0, _): label = "影视设置"; identifier = "settings.category.film"
        case (1, 0): label = "偏好设置"; identifier = "settings.preferences"
        case (1, _): label = "缓存管理"; identifier = "settings.cache"
        default: label = requestingReset ? "正在记录清除请求…" : "清空本机数据"; identifier = "settings.clearUserData"
        }
        var content = cell.defaultContentConfiguration()
        content.text = label
        content.textProperties.font = .preferredFont(forTextStyle: .body)
        content.textProperties.color = indexPath.section == 2 ? .systemRed : UIColor(theme.ink)
        content.textProperties.numberOfLines = 0
        cell.contentConfiguration = content
        cell.backgroundColor = .clear
        cell.backgroundView = SettingsRowBackgroundView(theme: theme,
            first: indexPath.row == 0, last: indexPath.row == tableView.numberOfRows(inSection: indexPath.section) - 1)
        cell.accessoryType = indexPath.section == 2 ? .none : .disclosureIndicator
        cell.accessibilityTraits = .button
        cell.isAccessibilityElement = true
        cell.accessibilityLabel = label; cell.accessibilityIdentifier = identifier
        if indexPath.section == 2, requestingReset { cell.accessibilityTraits.insert(.notEnabled) }
        return cell
    }
    func tableView(_ tableView: UITableView, didSelectRowAt indexPath: IndexPath) {
        tableView.deselectRow(at: indexPath, animated: true)
        switch (indexPath.section, indexPath.row) {
        case (0, 0): openMusic?()
        case (0, _): openFilm?()
        case (1, 0): openPreferences?()
        case (1, _): openCache?()
        default: confirmUserDataReset()
        }
    }

    func confirmUserDataReset() {
        guard resetUserData != nil, !requestingReset, canPresentPageAlert else { return }
        let alert = UIAlertController(title: "清空本机数据？",
            message: "将删除本机登录凭据、设备身份和密钥、本地歌单与收藏、历史、导入音源、偏好及缓存，无法撤销。云端数据和 App 外的原始文件不受影响。\n\n确认后需从多任务列表关闭 App，再重新打开才能完成清除。",
            preferredStyle: .alert)
        alert.addAction(UIAlertAction(title: "取消", style: .cancel))
        alert.addAction(UIAlertAction(title: "清除并等待重启", style: .destructive) { [weak self, weak alert] _ in
            guard let self, let alert else { return }
            dismissPageModal(alert) { [weak self] in self?.requestUserDataReset() }
        })
        present(alert, animated: true)
    }

    func requestUserDataReset() {
        guard let resetUserData, !requestingReset else { return }
        requestingReset = true; table.reloadData()
        // The App owns the operation; navigating away cannot cancel an admitted
        // reset request or start a second business session in this process.
        Task { [weak self] in
            do { try await resetUserData() }
            catch {
                self?.requestingReset = false; self?.table.reloadData()
                self?.showError("无法记录清除请求，请重试")
            }
        }
    }
}
