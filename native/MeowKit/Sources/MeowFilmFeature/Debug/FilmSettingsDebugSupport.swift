#if os(iOS)
import UIKit
import MeowContracts
import MeowDebug
import MeowUI

// MARK: - Film Settings Controllers

extension FilmSettingsViewController: DebugPageInspectable, DebugPageActionable {
    public func debugOpenSection(_ section: String) throws {
        let page: UIViewController
        switch section {
        case "search-rules":
            page = FilmRulePageViewController(owner: model.rules, group: .search, themeProvider: themeProvider)
        case "season-rules":
            page = FilmRulePageViewController(owner: model.rules, group: .season, themeProvider: themeProvider)
        case "rules":
            page = FilmSmartMatchingViewController(model: model, themeProvider: themeProvider)
        case "cats":
            page = FilmCatSourcesViewController(service: model.sources, themeProvider: themeProvider)
        case "videos":
            page = FilmVideoSourcesViewController(service: model.sources, themeProvider: themeProvider)
        case "metadata":
            page = FilmDataSourceSettingsViewController(tmdb: model.tmdb, douban: model.douban, themeProvider: themeProvider)
        case "pan":
            page = PanAccountsViewController(model: model.pan, themeProvider: themeProvider)
        case "pan-routing":
            page = FilmPanInterceptionViewController(service: model.sources, themeProvider: themeProvider)
        case "preferences":
            guard let playback = model.playback else {
                throw DebugAPIError.invalidState("Film smart playback settings unavailable")
            }
            page = FilmSmartPlaybackSettingsController(owner: playback, themeProvider: themeProvider)
        case "preferences-detail":
            guard let playback = model.playback else {
                throw DebugAPIError.invalidState("Film playback preferences unavailable")
            }
            page = FilmPlaybackPreferencesController(owner: playback, area: .preferences, themeProvider: themeProvider)
        case "site-clean":
            guard let playback = model.playback else {
                throw DebugAPIError.invalidState("Film site clean settings unavailable")
            }
            page = FilmPlaybackPreferencesController(owner: playback, area: .siteClean, themeProvider: themeProvider)
        case "auto-prepare":
            guard let playback = model.playback else {
                throw DebugAPIError.invalidState("Film auto prepare settings unavailable")
            }
            page = FilmPlaybackPreferencesController(owner: playback, area: .autoPrepare, themeProvider: themeProvider)
        case "player":
            page = FilmPlayerSettingsController(owner: model.player, themeProvider: themeProvider)
        case "cache":
            guard let playback = model.playback else {
                throw DebugAPIError.invalidState("Film cache management unavailable")
            }
            page = FilmMetadataCacheSettingsViewController(owner: playback, themeProvider: themeProvider)
        default:
            throw DebugAPIError.notFound("Unknown film settings section: \(section)")
        }
        navigationController?.pushViewController(page, animated: false)
    }

    public var debugPageStatus: DebugPageStatus {
        DebugPageStatus(
            target: "settings.film",
            moduleId: "film",
            route: "settings/film",
            title: "影视设置",
            isError: model.loadError != nil,
            errorText: model.loadError,
            settingsDomain: "film",
            settingsSection: nil
        )
    }

    public func debugContent(maxItems: Int, includeRaw: Bool) -> DebugContentSnapshot {
        DebugContentSnapshot(
            target: "settings.film",
            kind: "settings",
            error: model.loadError,
            settings: .object([
                "domain": .string("film"),
                "section": .string("root"),
                "sections": .array(["rules", "search-rules", "season-rules", "cats", "videos", "metadata", "pan", "pan-routing", "preferences", "preferences-detail", "site-clean", "auto-prepare", "player", "cache"].map(DebugJSONValue.string)),
                "values": .object([
                    "playerEngine": .string(String(describing: model.player.preferences.engine)),
                    "playbackAvailable": .bool(model.playback != nil),
                    "homeVideosCount": .int(model.snapshot?.homeVideos.count ?? 0)
                ])
            ])
        )
    }

    public func debugPerformAction(_ request: DebugActionRequest) async throws -> DebugJSONValue {
        if request.action == "setting.action", request.params["command"]?.stringValue == "refresh" {
            model.start()
            return .object(["refreshed": .bool(true)])
        }
        throw DebugAPIError.invalidArgument("Unsupported action '\(request.action)' on FilmSettingsViewController")
    }
}

extension FilmMetadataCacheSettingsViewController: DebugPageInspectable, DebugPageActionable {
    public var debugPageStatus: DebugPageStatus {
        DebugPageStatus(
            target: "settings.cache",
            moduleId: "general",
            route: "settings/cache",
            title: "缓存管理",
            isBusy: owner.busy,
            settingsDomain: "general",
            settingsSection: "cache"
        )
    }

    public func debugContent(maxItems: Int, includeRaw: Bool) -> DebugContentSnapshot {
        DebugContentSnapshot(
            target: "settings.cache",
            kind: "settings",
            loading: owner.busy,
            settings: .object([
                "domain": .string("general"),
                "section": .string("cache"),
                "scopes": .array(FilmMetadataCacheScope.allCases.map { .object(["id": .string($0.rawValue), "title": .string($0.title)]) })
            ])
        )
    }

    public func debugPerformAction(_ request: DebugActionRequest) async throws -> DebugJSONValue {
        if request.action == "setting.action", request.params["command"]?.stringValue == "clear_cache" {
            let scopeRaw = (request.params["scope"] ?? request.params["params"]?["scope"])?.stringValue
                ?? FilmMetadataCacheScope.allCases.first?.rawValue
                ?? ""
            guard let scope = FilmMetadataCacheScope(rawValue: scopeRaw) else {
                throw DebugAPIError.invalidArgument("Unknown cache scope: \(scopeRaw)")
            }
            try await owner.clearMetadataCache(scope)
            return .object(["clearedScope": .string(scope.rawValue)])
        }
        throw DebugAPIError.invalidArgument("Unsupported action '\(request.action)' on FilmMetadataCacheSettingsViewController")
    }
}

extension FilmRulePageViewController: DebugPageInspectable, DebugPageActionable {
    public var debugPageStatus: DebugPageStatus {
        let sec = group == .search ? "search-rules" : group == .season ? "season-rules" : "rules"
        return DebugPageStatus(
            target: "settings.film",
            moduleId: "film",
            route: "settings/film/\(sec)",
            title: pageTitle ?? "规则设置",
            isBusy: owner.busy || writing || testing,
            settingsDomain: "film",
            settingsSection: sec
        )
    }

    public func debugContent(maxItems: Int, includeRaw: Bool) -> DebugContentSnapshot {
        let sec = group == .search ? "search-rules" : group == .season ? "season-rules" : "rules"
        let rules = owner.rules(group)
        let rows = rules.prefix(maxItems).enumerated().map { idx, rule in
            DebugContentRow(
                index: idx,
                id: "rule:\(idx)",
                title: rule.pattern.isEmpty ? "规则 \(idx + 1)" : rule.pattern,
                subtitle: rule.replace.isEmpty ? rule.flags : "\(rule.replace) (\(rule.flags))",
                actions: []
            )
        }
        return DebugContentSnapshot(
            target: "settings.film",
            kind: "settings",
            loading: owner.busy || writing || testing,
            rows: rows,
            settings: .object([
                "domain": .string("film"),
                "section": .string(sec),
                "count": .int(rules.count)
            ])
        )
    }

    public func debugPerformAction(_ request: DebugActionRequest) async throws -> DebugJSONValue {
        if request.action == "setting.action" {
            let command = request.params["command"]?.stringValue ?? ""
            if command == "test" {
                runTest()
                return .object(["opened": .string("test")])
            }
            if command == "save" {
                save()
                return .object(["triggered": .string("save")])
            }
        }
        throw DebugAPIError.invalidArgument("Unsupported action '\(request.action)' on FilmRulePageViewController")
    }
}

extension FilmSmartMatchingViewController: DebugPageInspectable, DebugPageActionable {
    public var debugPageStatus: DebugPageStatus {
        DebugPageStatus(target: "settings.film", moduleId: "film", route: "settings/film/rules", title: pageTitle ?? "智能匹配", settingsDomain: "film", settingsSection: "rules")
    }
    public func debugContent(maxItems: Int, includeRaw: Bool) -> DebugContentSnapshot {
        DebugContentSnapshot(target: "settings.film", kind: "settings", rows: debugContentRows(maxItems: maxItems), settings: .object(["domain": .string("film"), "section": .string("rules")]))
    }
    public func debugPerformAction(_ request: DebugActionRequest) async throws -> DebugJSONValue {
        if request.action == "list.item.invoke" { return try debugInvokeRow(params: request.params) }
        throw DebugAPIError.invalidArgument("Unsupported action '\(request.action)' on FilmSmartMatchingViewController")
    }
}

extension FilmCatSourcesViewController: DebugPageInspectable, DebugPageActionable {
    public var debugPageStatus: DebugPageStatus {
        DebugPageStatus(
            target: "settings.film",
            moduleId: "film",
            route: "settings/film/cats",
            title: pageTitle ?? "猫源管理",
            isBusy: busy,
            isError: snapshot?.runtimeError != nil,
            errorText: snapshot?.runtimeError,
            settingsDomain: "film",
            settingsSection: "cats"
        )
    }
    public func debugContent(maxItems: Int, includeRaw: Bool) -> DebugContentSnapshot {
        let cats = snapshot?.cats ?? []
        let rows = cats.prefix(maxItems).enumerated().map { idx, cat in
            DebugContentRow(
                index: idx,
                id: cat.installationId,
                title: cat.name,
                subtitle: "\(filmSourceState(cat.state)) · \(filmSourceState(cat.updateState))",
                state: cat.enabled ? "on" : "off",
                actions: ["open"],
                meta: [
                    "enabled": .bool(cat.enabled),
                    "sourceType": .string(cat.sourceType),
                    "state": .string(cat.state),
                    "updateState": .string(cat.updateState),
                ]
            )
        }
        return DebugContentSnapshot(
            target: "settings.film",
            kind: "settings",
            loading: busy,
            error: snapshot?.runtimeError,
            rows: rows,
            settings: .object([
                "domain": .string("film"),
                "section": .string("cats"),
                "count": .int(cats.count),
            ])
        )
    }
    public func debugPerformAction(_ request: DebugActionRequest) async throws -> DebugJSONValue {
        switch request.action {
        case "setting.action":
            let command = request.params["command"]?.stringValue ?? ""
            if command == "import_network" {
                importSource(local: false)
                return .object(["opened": .string("network_import")])
            }
            if command == "import_local" {
                importSource(local: true)
                return .object(["opened": .string("local_import")])
            }
            throw DebugAPIError.invalidArgument("Unsupported setting.action '\(command)' on FilmCatSourcesViewController")
        case "list.item.invoke":
            let cats = snapshot?.cats ?? []
            let cat: FilmCatSource
            if let id = request.params["id"]?.stringValue {
                guard let found = cats.first(where: { $0.installationId == id }) else {
                    throw DebugAPIError.notFound("Cat source not found: \(id)")
                }
                cat = found
            } else {
                let idx = request.params["index"]?.intValue ?? 0
                guard cats.indices.contains(idx) else {
                    throw DebugAPIError.invalidArgument("Cat source index \(idx) out of range (0..<\(cats.count))")
                }
                cat = cats[idx]
            }
            navigationController?.pushViewController(
                FilmCatDetailController(service: service, installationId: cat.installationId, themeProvider: themeProvider),
                animated: false
            )
            return .object(["openedCatId": .string(cat.installationId), "name": .string(cat.name)])
        default:
            throw DebugAPIError.invalidArgument("Unsupported action '\(request.action)' on FilmCatSourcesViewController")
        }
    }
}

extension FilmVideoSourcesViewController: DebugPageInspectable, DebugPageActionable {
    public var debugPageStatus: DebugPageStatus {
        DebugPageStatus(target: "settings.film", moduleId: "film", route: "settings/film/videos", title: pageTitle ?? "视频源管理", isBusy: busy || detecting, settingsDomain: "film", settingsSection: "videos")
    }
    public func debugContent(maxItems: Int, includeRaw: Bool) -> DebugContentSnapshot {
        DebugContentSnapshot(target: "settings.film", kind: "settings", loading: busy || detecting, rows: debugManagementRows(maxItems: maxItems), settings: .object(["domain": .string("film"), "section": .string("videos")]))
    }
    public func debugPerformAction(_ request: DebugActionRequest) async throws -> DebugJSONValue {
        if request.action == "list.item.invoke" { return try debugInvokeManagementRow(params: request.params) }
        throw DebugAPIError.invalidArgument("Unsupported action '\(request.action)' on FilmVideoSourcesViewController")
    }
}

extension FilmDataSourceSettingsViewController: DebugPageInspectable {
    public var debugPageStatus: DebugPageStatus {
        DebugPageStatus(target: "settings.film", moduleId: "film", route: "settings/film/metadata", title: pageTitle ?? "数据源设置", settingsDomain: "film", settingsSection: "metadata")
    }
    public func debugContent(maxItems: Int, includeRaw: Bool) -> DebugContentSnapshot {
        DebugContentSnapshot(target: "settings.film", kind: "settings", settings: .object(["domain": .string("film"), "section": .string("metadata")]))
    }
}

extension PanAccountsViewController: DebugPageInspectable, DebugPageActionable {
    public var debugPageStatus: DebugPageStatus {
        DebugPageStatus(target: "settings.film", moduleId: "film", route: "settings/film/pan", title: pageTitle ?? "网盘账号", settingsDomain: "film", settingsSection: "pan")
    }
    public func debugContent(maxItems: Int, includeRaw: Bool) -> DebugContentSnapshot {
        let rows = model.rows.prefix(maxItems).enumerated().map { idx, row in
            let actionNames = row.actions.map { action -> String in
                switch action {
                case .login: return "login"
                case .refresh: return "refresh"
                case .logout: return "logout"
                }
            }
            return DebugContentRow(
                index: idx,
                id: row.id.rawValue,
                title: row.id.title,
                subtitle: row.statusTitle,
                actions: actionNames.isEmpty ? ["login"] : actionNames,
                meta: ["loggedIn": .bool(row.actions.contains(.logout)), "busy": .bool(row.busy)]
            )
        }
        return DebugContentSnapshot(
            target: "settings.film",
            kind: "settings",
            rows: rows,
            settings: .object(["domain": .string("film"), "section": .string("pan")])
        )
    }
    public func debugPerformAction(_ request: DebugActionRequest) async throws -> DebugJSONValue {
        guard request.action == "list.item.invoke" else {
            throw DebugAPIError.invalidArgument("Unsupported action '\(request.action)' on PanAccountsViewController")
        }
        let row: PanAccountRow
        if let id = request.params["id"]?.stringValue,
           let matched = model.rows.first(where: { $0.id.rawValue == id }) {
            row = matched
        } else {
            let idx = request.params["index"]?.intValue ?? 0
            guard model.rows.indices.contains(idx) else {
                throw DebugAPIError.invalidArgument("Pan account index \(idx) out of range")
            }
            row = model.rows[idx]
        }
        let op = request.params["action"]?.stringValue ?? "login"
        switch op {
        case "login": perform(.login, provider: row.id)
        case "refresh": perform(.refresh, provider: row.id)
        case "logout": perform(.logout, provider: row.id)
        default: throw DebugAPIError.invalidArgument("Unsupported pan account action: \(op)")
        }
        return .object(["provider": .string(row.id.rawValue), "operation": .string(op)])
    }
}

extension FilmPanInterceptionViewController: DebugPageInspectable, DebugPageActionable {
    public var debugPageStatus: DebugPageStatus {
        DebugPageStatus(target: "settings.film", moduleId: "film", route: "settings/film/pan-routing", title: pageTitle ?? "网盘拦截", settingsDomain: "film", settingsSection: "pan-routing")
    }
    public func debugContent(maxItems: Int, includeRaw: Bool) -> DebugContentSnapshot {
        DebugContentSnapshot(target: "settings.film", kind: "settings", rows: debugContentRows(maxItems: maxItems), settings: .object(["domain": .string("film"), "section": .string("pan-routing")]))
    }
    public func debugPerformAction(_ request: DebugActionRequest) async throws -> DebugJSONValue {
        if request.action == "list.item.invoke" { return try debugInvokeRow(params: request.params) }
        throw DebugAPIError.invalidArgument("Unsupported action '\(request.action)' on FilmPanInterceptionViewController")
    }
}

extension FilmSmartPlaybackSettingsController: DebugPageInspectable, DebugPageActionable {
    public var debugPageStatus: DebugPageStatus {
        DebugPageStatus(target: "settings.film", moduleId: "film", route: "settings/film/preferences", title: pageTitle ?? "智能播放", settingsDomain: "film", settingsSection: "preferences")
    }
    public func debugContent(maxItems: Int, includeRaw: Bool) -> DebugContentSnapshot {
        DebugContentSnapshot(target: "settings.film", kind: "settings", rows: debugContentRows(maxItems: maxItems), settings: .object(["domain": .string("film"), "section": .string("preferences")]))
    }
    public func debugPerformAction(_ request: DebugActionRequest) async throws -> DebugJSONValue {
        if request.action == "list.item.invoke" { return try debugInvokeRow(params: request.params) }
        throw DebugAPIError.invalidArgument("Unsupported action '\(request.action)' on FilmSmartPlaybackSettingsController")
    }
}

extension FilmPlayerSettingsController: DebugPageInspectable, DebugPageActionable {
    public var debugPageStatus: DebugPageStatus {
        DebugPageStatus(target: "settings.film", moduleId: "film", route: "settings/film/player", title: pageTitle ?? "播放器设置", settingsDomain: "film", settingsSection: "player")
    }
    public func debugContent(maxItems: Int, includeRaw: Bool) -> DebugContentSnapshot {
        DebugContentSnapshot(
            target: "settings.film",
            kind: "settings",
            rows: debugContentRows(maxItems: maxItems),
            settings: .object(["domain": .string("film"), "section": .string("player")])
        )
    }
    public func debugPerformAction(_ request: DebugActionRequest) async throws -> DebugJSONValue {
        switch request.action {
        case "list.item.invoke":
            return try debugInvokeRow(params: request.params)
        case "setting.set":
            let key = (request.params["key"]?.stringValue ?? "").trimmingCharacters(in: .whitespacesAndNewlines)
            let value = (request.params["value"]?.stringValue ?? "").trimmingCharacters(in: .whitespacesAndNewlines)
            switch key {
            case "engine", "playerEngine", "film.player.engine":
                guard let engine = PlaybackVideoEngine(rawValue: value.lowercased()) else {
                    throw DebugAPIError.invalidArgument("Unsupported playback engine '\(value)' (expected 'native' or 'aether')")
                }
                await owner.set(\.engine, engine)
                update()
                return .object(["key": .string("engine"), "value": .string(engine.rawValue)])
            default:
                throw DebugAPIError.invalidArgument("Unsupported setting key '\(key)' on FilmPlayerSettingsController")
            }
        default:
            throw DebugAPIError.invalidArgument("Unsupported action '\(request.action)' on FilmPlayerSettingsController")
        }
    }
}

extension FilmPlaybackPreferencesController: DebugPageInspectable, DebugPageActionable {
    private var debugAreaKey: String {
        switch area {
        case .preferences: return "preferences-detail"
        case .siteClean: return "site-clean"
        case .autoPrepare: return "auto-prepare"
        }
    }
    public var debugPageStatus: DebugPageStatus {
        DebugPageStatus(target: "settings.film", moduleId: "film", route: "settings/film/\(debugAreaKey)", title: pageTitle ?? "偏好设置", settingsDomain: "film", settingsSection: debugAreaKey)
    }
    public func debugContent(maxItems: Int, includeRaw: Bool) -> DebugContentSnapshot {
        DebugContentSnapshot(
            target: "settings.film",
            kind: "settings",
            rows: debugContentRows(maxItems: maxItems),
            settings: .object(["domain": .string("film"), "section": .string(debugAreaKey)])
        )
    }
    public func debugPerformAction(_ request: DebugActionRequest) async throws -> DebugJSONValue {
        if request.action == "list.item.invoke" { return try debugInvokeRow(params: request.params) }
        throw DebugAPIError.invalidArgument("Unsupported action '\(request.action)' on FilmPlaybackPreferencesController")
    }
}
#endif
