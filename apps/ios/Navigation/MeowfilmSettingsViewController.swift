import UIKit
import MeowDebug
import MeowUI

/// App-level categories and the cold-start reset request are independent of
/// either business module's availability or page lifetime.
@MainActor
final class MeowfilmSettingsViewController: PageViewController, UITableViewDataSource, UITableViewDelegate {
    var openMusic: (() -> Void)?
    var openFilm: (() -> Void)?
    var openNovel: (() -> Void)?
    var openPreferences: (() -> Void)?
    var openCache: (() -> Void)?
    private let resetUserData: (@MainActor () async throws -> Void)?
    private var requestingReset = false
    private let table = SettingsTableView()

    init(themeProvider: any PageThemeProviding, resetUserData: (@MainActor () async throws -> Void)? = nil) {
        self.resetUserData = resetUserData
        super.init(themeProvider: themeProvider)
        pageTitle = "设置"
    }

    override func viewDidLoad() {
        super.viewDidLoad()
        table.dataSource = self; table.delegate = self
        table.rowHeight = UITableView.automaticDimension; table.estimatedRowHeight = 56
        view.addSubview(table); table.translatesAutoresizingMaskIntoConstraints = false
        NSLayoutConstraint.activate([
            table.topAnchor.constraint(equalTo: contentTopAnchor),
            table.leadingAnchor.constraint(equalTo: view.safeAreaLayoutGuide.leadingAnchor),
            table.trailingAnchor.constraint(equalTo: view.safeAreaLayoutGuide.trailingAnchor),
            table.bottomAnchor.constraint(equalTo: view.bottomAnchor),
        ])
    }
    override func applyTheme() {
        super.applyTheme()
        guard isViewLoaded else { return }
        view.tintColor = UIColor(theme.settings.accent)
        table.separatorColor = UIColor(theme.settings.separator)
        table.reloadData()
    }
    func numberOfSections(in tableView: UITableView) -> Int { resetUserData == nil ? 2 : 3 }
    func tableView(_ tableView: UITableView, numberOfRowsInSection section: Int) -> Int { section == 0 ? 3 : section == 1 ? 2 : 1 }
    func tableView(_ tableView: UITableView, heightForHeaderInSection section: Int) -> CGFloat {
        table.heightForHeader(in: section)
    }
    func tableView(_ tableView: UITableView, viewForHeaderInSection section: Int) -> UIView? {
        table.viewForHeader(in: section, title: nil, theme: theme)
    }
    func tableView(_ tableView: UITableView, cellForRowAt indexPath: IndexPath) -> UITableViewCell {
        let label: String, identifier: String
        switch (indexPath.section, indexPath.row) {
        case (0, 0): label = "音乐设置"; identifier = "settings.category.music"
        case (0, 1): label = "影视设置"; identifier = "settings.category.film"
        case (0, _): label = "小说设置"; identifier = "settings.category.novel"
        case (1, 0): label = "偏好设置"; identifier = "settings.preferences"
        case (1, _): label = "缓存管理"; identifier = "settings.cache"
        default: label = requestingReset ? "正在记录清除请求…" : "清空本机数据"; identifier = "settings.clearUserData"
        }
        let isLast = indexPath.row == tableView.numberOfRows(inSection: indexPath.section) - 1
        let cell = SettingsTableCell(title: label, theme: theme,
            first: indexPath.row == 0, last: isLast, destructive: indexPath.section == 2)
        cell.accessoryType = indexPath.section == 2 ? .none : .disclosureIndicator
        cell.selectionStyle = indexPath.section == 2 && requestingReset ? .none : .default
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
        case (0, 1): openFilm?()
        case (0, _): openNovel?()
        case (1, 0): openPreferences?()
        case (1, _): openCache?()
        default: confirmUserDataReset()
        }
    }

    func confirmUserDataReset() {
        guard resetUserData != nil, !requestingReset, canPresentPageAlert else { return }
        let alert = UIAlertController(title: "清空本机数据？",
            message: "将删除本机登录凭据、设备身份和密钥、本地歌单、收藏与书架、历史与阅读进度、导入音源与书源、偏好及缓存，无法撤销。云端数据和 App 外的原始文件不受影响。\n\n确认后需从多任务列表关闭 App，再重新打开才能完成清除。",
            preferredStyle: .alert)
        let cancelHandler: @MainActor () -> Void = {}
        let confirmHandler: @MainActor () -> Void = { [weak self, weak alert] in
            guard let self, let alert else { return }
            dismissPageModal(alert) { [weak self] in self?.requestUserDataReset() }
        }
        alert.addAction(UIAlertAction(title: "取消", style: .cancel) { _ in })
        alert.addAction(UIAlertAction(title: "清除并等待重启", style: .destructive) { _ in confirmHandler() })
        DebugAlertActionRegistry.shared.register(alert: alert, actions: [
            .init(index: 0, label: "取消", kind: "cancel", handler: cancelHandler),
            .init(index: 1, label: "清除并等待重启", kind: "destructive", handler: confirmHandler)
        ])
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

extension MeowfilmSettingsViewController: DebugPageInspectable, DebugPageActionable {
    var debugPageStatus: DebugPageStatus {
        DebugPageStatus(
            target: "settings.general",
            moduleId: "settings",
            route: "settings",
            title: pageTitle ?? "设置",
            isBusy: requestingReset,
            settingsDomain: "general",
            settingsSection: nil
        )
    }

    func debugContent(maxItems: Int, includeRaw: Bool) -> DebugContentSnapshot {
        var rows: [DebugContentRow] = [
            .init(index: 0, id: "music", title: "音乐设置", actions: ["open"]),
            .init(index: 1, id: "film", title: "影视设置", actions: ["open"]),
            .init(index: 2, id: "novel", title: "小说设置", actions: ["open"]),
            .init(index: 3, id: "appearance", title: "偏好设置", actions: ["open"]),
            .init(index: 4, id: "cache", title: "缓存管理", actions: ["open"])
        ]
        if resetUserData != nil {
            rows.append(.init(
                index: 5,
                id: "clear_user_data",
                title: requestingReset ? "正在记录清除请求…" : "清空本机数据",
                actions: ["open"]
            ))
        }
        return DebugContentSnapshot(
            target: "settings.general",
            kind: "settings",
            loading: requestingReset,
            rows: Array(rows.prefix(maxItems)),
            settings: .object([
                "domain": .string("general"),
                "section": .string("root"),
                "sections": .array(["music", "film", "novel", "appearance", "cache"].map(DebugJSONValue.string)),
                "requestingReset": .bool(requestingReset),
                "resetAvailable": .bool(resetUserData != nil)
            ])
        )
    }

    func debugPerformAction(_ request: DebugActionRequest) async throws -> DebugJSONValue {
        switch request.action {
        case "list.item.invoke":
            let id = request.params["id"]?.stringValue
            let idx = request.params["index"]?.intValue
            switch (id, idx) {
            case ("music", _), (nil, 0): openMusic?(); return .object(["opened": .string("settings.music")])
            case ("film", _), (nil, 1): openFilm?(); return .object(["opened": .string("settings.film")])
            case ("novel", _), (nil, 2): openNovel?(); return .object(["opened": .string("settings.novel")])
            case ("appearance", _), (nil, 3): openPreferences?(); return .object(["opened": .string("settings.general.appearance")])
            case ("cache", _), (nil, 4): openCache?(); return .object(["opened": .string("settings.cache")])
            case ("clear_user_data", _), (nil, 5): confirmUserDataReset(); return .object(["opened": .string("dialog.reset_confirm")])
            default: throw DebugAPIError.invalidArgument("Unknown settings item")
            }
        case "setting.action":
            let cmd = request.params["command"]?.stringValue ?? ""
            if cmd == "reset_user_data" {
                guard request.params["confirm"]?.stringValue == "RESET_ALL_USER_DATA" else {
                    throw DebugAPIError.invalidState("reset_user_data requires confirm == 'RESET_ALL_USER_DATA'")
                }
                requestUserDataReset()
                return .object(["requested": .bool(true)])
            }
            throw DebugAPIError.invalidArgument("Unsupported setting.action command: \(cmd)")
        default:
            throw DebugAPIError.invalidArgument("Unsupported action '\(request.action)' on MeowfilmSettingsViewController")
        }
    }
}
