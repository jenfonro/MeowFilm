import UIKit
import MeowDebug
import MeowUI

/// App-level categories and general subpages are independent of either
/// business module's availability or page lifetime.
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
    var openDataManagement: (() -> Void)?

    override init(themeProvider: any PageThemeProviding) {
        super.init(themeProvider: themeProvider)
        pageTitle = "设置"
    }

    override func viewDidLoad() {
        super.viewDidLoad()
        renderSections()
    }

    private func renderSections() {
        let sections: [SettingsListSection] = [
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
                SettingsListRow(id: "settings.dataManagement", title: "数据管理",
                                accessory: .disclosure, action: { [weak self] in self?.openDataManagement?() }),
            ]),
        ]
        applySections(sections)
    }
}

extension MeowfilmSettingsViewController: DebugPageInspectable, DebugPageActionable {
    var debugPageStatus: DebugPageStatus {
        DebugPageStatus(
            target: "settings.general",
            moduleId: "settings",
            route: "settings",
            title: pageTitle ?? "设置",
            isBusy: false,
            settingsDomain: "general",
            settingsSection: nil
        )
    }

    func debugContent(maxItems: Int, includeRaw: Bool) -> DebugContentSnapshot {
        let rows: [DebugContentRow] = [
            .init(index: 0, id: "music", title: "音乐设置", actions: ["open"]),
            .init(index: 1, id: "film", title: "影视设置", actions: ["open"]),
            .init(index: 2, id: "novel", title: "小说设置", actions: ["open"]),
            .init(index: 3, id: "appearance", title: "偏好设置", actions: ["open"]),
            .init(index: 4, id: "cache", title: "缓存管理", actions: ["open"]),
            .init(index: 5, id: "data", title: "数据管理", actions: ["open"]),
        ]
        return DebugContentSnapshot(
            target: "settings.general",
            kind: "settings",
            loading: false,
            rows: Array(rows.prefix(maxItems)),
            settings: .object([
                "domain": .string("general"),
                "section": .string("root"),
                "sections": .array(["music", "film", "novel", "appearance", "cache", "data"].map(DebugJSONValue.string)),
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
            case ("data", _), (nil, 5):
                openDataManagement?()
                return .object(["opened": .string("settings.general.data")])
            default: throw DebugAPIError.invalidArgument("Unknown settings item")
            }
        default:
            throw DebugAPIError.invalidArgument("Unsupported action '\(request.action)' on MeowfilmSettingsViewController")
        }
    }
}
