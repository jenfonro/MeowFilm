import UIKit
import MeowDebug
import MeowUI

/// App-level categories and the cold-start reset request are independent of
/// either business module's availability or page lifetime.
@MainActor
final class MeowfilmSettingsViewController: SettingsListViewController {
    override var pageChromePolicy: PageChromePolicy {
        PageChromePolicy(hidesDock: true, hidesMiniPlayer: true, isSettingsBranchRoot: true)
    }

    var openMusic: (() -> Void)?
    var openFilm: (() -> Void)?
    var openNovel: (() -> Void)?
    var openPreferences: (() -> Void)?
    var openCache: (() -> Void)?
    private let resetUserData: (@MainActor () async throws -> Void)?
    private var requestingReset = false

    init(themeProvider: any PageThemeProviding, resetUserData: (@MainActor () async throws -> Void)? = nil) {
        self.resetUserData = resetUserData
        super.init(themeProvider: themeProvider)
        pageTitle = "设置"
    }

    override func viewDidLoad() {
        super.viewDidLoad()
        renderSections()
    }

    private func renderSections() {
        var sections: [SettingsListSection] = [
            SettingsListSection(id: "categories", rows: [
                SettingsListRow(id: "settings.category.music", title: "音乐设置",
                                accessory: .disclosure, action: { [weak self] in self?.openMusic?() }),
                SettingsListRow(id: "settings.category.film", title: "影视设置",
                                accessory: .disclosure, action: { [weak self] in self?.openFilm?() }),
                SettingsListRow(id: "settings.category.novel", title: "小说设置",
                                accessory: .disclosure, action: { [weak self] in self?.openNovel?() }),
            ]),
            SettingsListSection(id: "app", rows: [
                SettingsListRow(id: "settings.preferences", title: "偏好设置",
                                accessory: .disclosure, action: { [weak self] in self?.openPreferences?() }),
                SettingsListRow(id: "settings.cache", title: "缓存管理",
                                accessory: .disclosure, action: { [weak self] in self?.openCache?() }),
            ]),
        ]
        if resetUserData != nil {
            sections.append(SettingsListSection(id: "danger", rows: [
                SettingsListRow(
                    id: "settings.clearUserData",
                    title: requestingReset ? "正在记录清除请求…" : "清空本机数据",
                    destructive: true,
                    enabled: !requestingReset,
                    action: { [weak self] in self?.confirmUserDataReset() }
                ),
            ]))
        }
        applySections(sections)
    }

    @discardableResult
    func confirmUserDataReset() -> Bool {
        guard resetUserData != nil, !requestingReset, canPresentPageAlert else { return false }
        return presentPageConfirmation(
            title: "清空本机数据？",
            message: "将删除本机登录凭据、设备身份和密钥、本地歌单、收藏与书架、历史与阅读进度、导入音源与书源、偏好及缓存，无法撤销。云端数据和 App 外的原始文件不受影响。\n\n确认后需从多任务列表关闭 App，再重新打开才能完成清除。",
            confirmTitle: "清除并等待重启",
            cancelTitle: "取消",
            destructive: true,
            onConfirm: { [weak self] in self?.requestUserDataReset() }
        ) != nil
    }

    func requestUserDataReset() {
        guard let resetUserData, !requestingReset else { return }
        requestingReset = true
        renderSections()
        // The App owns the operation; navigating away cannot cancel an admitted
        // reset request or start a second business session in this process.
        Task { [weak self] in
            do { try await resetUserData() }
            catch {
                self?.requestingReset = false
                self?.renderSections()
                self?.enqueueError("无法记录清除请求，请重试")
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
            case ("clear_user_data", _), (nil, 5):
                guard confirmUserDataReset() else {
                    throw DebugAPIError.invalidState(
                        "Unable to present reset confirmation alert (canPresentPageAlert=\(canPresentPageAlert), isPageVisible=\(isPageVisible), hasWindow=\(view.window != nil), presented=\(presentedViewController != nil))"
                    )
                }
                return .object(["opened": .string("dialog.reset_confirm")])
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
