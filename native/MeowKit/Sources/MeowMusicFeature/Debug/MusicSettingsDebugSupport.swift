#if os(iOS)
import UIKit
import MeowDebug
import MeowUI

// MARK: - Music Settings Controllers

extension MusicSettingsViewController: DebugPageInspectable, DebugPageActionable {
    public func debugOpenSection(_ section: String) throws {
        switch section {
        case "accounts":
            open(.aggregation, animated: false)
        case "sources":
            open(.sources, animated: false)
        case "exclusions":
            open(.exclusions, animated: false)
        case "search":
            open(.search, animated: false)
        case "playback":
            open(.playback, animated: false)
        case "quality":
            open(.playback, animated: false)
            let qualityVC = MusicQualitySettingsViewController(model: model, spatial: false, appearance: appearance)
            navigationController?.pushViewController(qualityVC, animated: false)
        case "metadata":
            open(.metadata, animated: false)
        case "home-source":
            break
        default:
            throw DebugAPIError.notFound("Unknown music settings section: \(section)")
        }
    }

    public var debugPageStatus: DebugPageStatus {
        DebugPageStatus(
            target: "settings.music",
            moduleId: "music",
            route: "settings/music",
            title: "音乐设置",
            isBusy: sourceSettings.isSaving,
            settingsDomain: "music",
            settingsSection: nil
        )
    }

    public func debugContent(maxItems: Int, includeRaw: Bool) -> DebugContentSnapshot {
        var values = model.debugSettingsJSON(appearance: appearance).objectValue ?? [:]
        if let sel = sourceSettings.selection {
            values["homeSourceSelected"] = .string(sel.selected.rawValue)
            values["homeSourceAvailable"] = .array(sel.available.map { .string($0.rawValue) })
        }
        return DebugContentSnapshot(
            target: "settings.music",
            kind: "settings",
            loading: sourceSettings.isSaving,
            rows: debugContentRows(maxItems: maxItems),
            settings: .object([
                "domain": .string("music"),
                "section": .string("root"),
                "sections": .array(["accounts", "sources", "exclusions", "search", "playback", "quality", "metadata", "home-source"].map(DebugJSONValue.string)),
                "values": .object(values)
            ])
        )
    }

    public func debugPerformAction(_ request: DebugActionRequest) async throws -> DebugJSONValue {
        switch request.action {
        case "list.item.invoke":
            return try debugInvokeRow(params: request.params)
        case "setting.set":
            guard let key = request.params["key"]?.stringValue, let val = request.params["value"] else {
                throw DebugAPIError.invalidArgument("Missing 'key' or 'value'")
            }
            switch key {
            case "lockLyric":
                guard let b = val.boolValue else { throw DebugAPIError.invalidArgument("Expected bool for lockLyric") }
                let ok = await appearance.set(\.lockLyric, b)
                return .object(["key": .string(key), "saved": .bool(ok)])
            case "homeSource", "catalogSource":
                guard let raw = val.stringValue, let plat = MusicPublicBrowsePlatform(rawValue: raw) else {
                    throw DebugAPIError.invalidArgument("Invalid music catalog platform")
                }
                sourceSettings.select(plat)
                return .object(["key": .string(key), "selected": .string(plat.rawValue)])
            case "lyricsEnabled":
                guard let b = val.boolValue else { throw DebugAPIError.invalidArgument("Expected bool") }
                await model.updateSettings(.init(ifRevision: model.state.settingsRevision, lyricsEnabled: b))
                return .object(["key": .string(key), "saved": .bool(true)])
            case "showTranslation":
                guard let b = val.boolValue else { throw DebugAPIError.invalidArgument("Expected bool") }
                await model.updateSettings(.init(ifRevision: model.state.settingsRevision, showTranslation: b))
                return .object(["key": .string(key), "saved": .bool(true)])
            case "requestQuality":
                guard let q = val.stringValue else { throw DebugAPIError.invalidArgument("Expected string") }
                await model.updateSettings(.init(ifRevision: model.state.settingsRevision, requestQuality: q))
                return .object(["key": .string(key), "saved": .bool(true)])
            default:
                throw DebugAPIError.invalidArgument("Unsupported music setting key: \(key)")
            }
        case "setting.action":
            let cmd = request.params["command"]?.stringValue ?? ""
            if cmd == "refresh" {
                await model.load()
                return .object(["refreshed": .bool(true)])
            }
            throw DebugAPIError.invalidArgument("Unsupported music setting command: \(cmd)")
        default:
            throw DebugAPIError.invalidArgument("Unsupported action '\(request.action)' on MusicSettingsViewController")
        }
    }
}

extension MusicAppearanceSettingsViewController: DebugPageInspectable, DebugPageActionable {
    public var debugPageStatus: DebugPageStatus {
        DebugPageStatus(
            target: "settings.general",
            moduleId: "general",
            route: "settings/general/appearance",
            title: "偏好设置",
            settingsDomain: "general",
            settingsSection: "appearance"
        )
    }

    public func debugContent(maxItems: Int, includeRaw: Bool) -> DebugContentSnapshot {
        DebugContentSnapshot(
            target: "settings.general",
            kind: "settings",
            rows: debugContentRows(maxItems: maxItems),
            settings: .object([
                "domain": .string("general"),
                "section": .string("appearance"),
                "values": .object([
                    "colorMode": .string(appearancePreferencesDebug().colorMode.rawValue),
                    "playlistView": .string(appearancePreferencesDebug().playlistView.rawValue),
                    "lockLyric": .bool(appearancePreferencesDebug().lockLyric)
                ])
            ])
        )
    }

    public func debugPerformAction(_ request: DebugActionRequest) async throws -> DebugJSONValue {
        if request.action == "list.item.invoke" { return try debugInvokeRow(params: request.params) }
        throw DebugAPIError.invalidArgument("Use general setting.set via host for appearance updates")
    }

    private func appearancePreferencesDebug() -> MusicAppearancePreferences {
        (themeProvider as? MusicAppearancePreferencesStore)?.preferences ?? MusicAppearancePreferences()
    }
}

extension MusicAccountsViewController: DebugPageInspectable, DebugPageActionable {
    public var debugPageStatus: DebugPageStatus {
        DebugPageStatus(target: "settings.music", moduleId: "music", route: "settings/music/accounts", title: "平台账号", isBusy: state.busy, isError: state.error != nil, errorText: state.error, settingsDomain: "music", settingsSection: "accounts")
    }
    public func debugContent(maxItems: Int, includeRaw: Bool) -> DebugContentSnapshot {
        let accounts = state.rows.map { row -> DebugJSONValue in
            .object([
                "platform": .string(row.account.platform.rawValue),
                "loggedIn": .bool(row.account.phase == .signedIn),
                "phase": .string(row.account.phase.rawValue),
                "nickname": .string(row.account.nickname),
                "statusText": .string(row.account.phase.title),
                "canLogin": .bool(row.account.canLogin),
                "pending": .bool(state.pendingAccounts.contains(row.account.platform))
            ])
        }
        return DebugContentSnapshot(
            target: "settings.music",
            kind: "settings",
            loading: state.busy,
            error: state.error,
            rows: debugContentRows(maxItems: maxItems),
            settings: .object([
                "domain": .string("music"),
                "section": .string("accounts"),
                "available": .bool(state.isActive),
                "canSync": .bool(state.canSync),
                "accounts": .array(accounts)
            ])
        )
    }
    public func debugPerformAction(_ request: DebugActionRequest) async throws -> DebugJSONValue {
        if request.action == "list.item.invoke" { return try debugInvokeRow(params: request.params) }
        throw DebugAPIError.invalidArgument("Unsupported action '\(request.action)' on MusicAccountsViewController")
    }
}

extension MusicSourceManagementViewController: DebugPageInspectable {
    public var debugPageStatus: DebugPageStatus {
        DebugPageStatus(target: "settings.music", moduleId: "music", route: "settings/music/sources", title: "音源管理", isBusy: state.savingOfficial || state.importing, isError: state.error != nil, errorText: state.error, settingsDomain: "music", settingsSection: "sources")
    }
    public func debugContent(maxItems: Int, includeRaw: Bool) -> DebugContentSnapshot {
        let scripts = state.scripts.prefix(maxItems).map { s -> DebugJSONValue in
            .object([
                "id": .string(s.id),
                "name": .string(s.name),
                "enabled": .bool(s.enabled),
                "version": .string(s.version),
                "state": .string(s.state.rawValue)
            ])
        }
        return DebugContentSnapshot(
            target: "settings.music",
            kind: "settings",
            loading: state.savingOfficial || state.importing,
            error: state.error,
            settings: .object([
                "domain": .string("music"),
                "section": .string("sources"),
                "official": .array(state.official.map { .string($0.rawValue) }),
                "scripts": .array(scripts)
            ])
        )
    }
}

extension MusicSourceExclusionsViewController: DebugPageInspectable, DebugPageActionable {
    public var debugPageStatus: DebugPageStatus {
        DebugPageStatus(target: "settings.music", moduleId: "music", route: "settings/music/exclusions", title: "错源管理", settingsDomain: "music", settingsSection: "exclusions")
    }
    public func debugContent(maxItems: Int, includeRaw: Bool) -> DebugContentSnapshot {
        DebugContentSnapshot(target: "settings.music", kind: "settings", rows: debugContentRows(maxItems: maxItems), settings: .object(["domain": .string("music"), "section": .string("exclusions")]))
    }
    public func debugPerformAction(_ request: DebugActionRequest) async throws -> DebugJSONValue {
        if request.action == "list.item.invoke" { return try debugInvokeRow(params: request.params) }
        throw DebugAPIError.invalidArgument("Unsupported action '\(request.action)' on MusicSourceExclusionsViewController")
    }
}

extension MusicSearchSettingsViewController: DebugPageInspectable, DebugPageActionable {
    public var debugPageStatus: DebugPageStatus {
        DebugPageStatus(target: "settings.music", moduleId: "music", route: "settings/music/search", title: "搜索设置", settingsDomain: "music", settingsSection: "search")
    }
    public func debugContent(maxItems: Int, includeRaw: Bool) -> DebugContentSnapshot {
        DebugContentSnapshot(
            target: "settings.music",
            kind: "settings",
            rows: debugContentRows(maxItems: maxItems),
            settings: .object([
                "domain": .string("music"),
                "section": .string("search"),
                "searchPlaylists": .bool(state.value.searchPlaylists),
                "songPlatforms": .array(state.value.songs.platforms.map { .string($0.rawValue) }),
                "playlistPlatforms": .array(state.value.playlists.platforms.map { .string($0.rawValue) }),
                "platformDialogOrder": platformWindow.map { .array($0.order.map { .string($0.rawValue) }) } ?? .null
            ])
        )
    }
    public func debugPerformAction(_ request: DebugActionRequest) async throws -> DebugJSONValue {
        switch request.action {
        case "list.item.invoke":
            return try debugInvokeRow(params: request.params)
        case "platform.toggle":
            guard let window = platformWindow else {
                throw DebugAPIError.invalidState("MusicSearchPlatformsViewController is not open")
            }
            guard let raw = request.params["platform"]?.stringValue,
                  let platform = MusicPublicBrowsePlatform(rawValue: raw),
                  let enabled = request.params["enabled"]?.boolValue else {
                throw DebugAPIError.invalidArgument("Provide 'platform' and boolean 'enabled'")
            }
            await window.setEnabled(platform, enabled: enabled)?.value
            return .object([
                "kind": .string(window.kind.rawValue),
                "platforms": .array(state.settings(window.kind).platforms.map { .string($0.rawValue) })
            ])
        case "platform.move":
            guard let window = platformWindow else {
                throw DebugAPIError.invalidState("MusicSearchPlatformsViewController is not open")
            }
            guard let from = request.params["from"]?.intValue,
                  let to = request.params["to"]?.intValue else {
                throw DebugAPIError.invalidArgument("Provide integer 'from' and 'to'")
            }
            await window.movePlatform(from: from, to: to)?.value
            return .object([
                "kind": .string(window.kind.rawValue),
                "order": .array(window.order.map { .string($0.rawValue) }),
                "platforms": .array(state.settings(window.kind).platforms.map { .string($0.rawValue) })
            ])
        default:
            throw DebugAPIError.invalidArgument("Unsupported action '\(request.action)' on MusicSearchSettingsViewController")
        }
    }
}

extension MusicPlaybackSettingsViewController: DebugPageInspectable, DebugPageActionable {
    public var debugPageStatus: DebugPageStatus {
        DebugPageStatus(target: "settings.music", moduleId: "music", route: "settings/music/playback", title: "播放设置", settingsDomain: "music", settingsSection: "playback")
    }
    public func debugContent(maxItems: Int, includeRaw: Bool) -> DebugContentSnapshot {
        DebugContentSnapshot(target: "settings.music", kind: "settings", rows: debugContentRows(maxItems: maxItems), settings: .object(["domain": .string("music"), "section": .string("playback")]))
    }
    public func debugPerformAction(_ request: DebugActionRequest) async throws -> DebugJSONValue {
        if request.action == "list.item.invoke" { return try debugInvokeRow(params: request.params) }
        throw DebugAPIError.invalidArgument("Unsupported action '\(request.action)' on MusicPlaybackSettingsViewController")
    }
}

extension MusicQualitySettingsViewController: DebugPageInspectable, DebugPageActionable {
    public var debugPageStatus: DebugPageStatus {
        DebugPageStatus(target: "settings.music", moduleId: "music", route: "settings/music/quality", title: "音质设置", settingsDomain: "music", settingsSection: "quality")
    }
    public func debugContent(maxItems: Int, includeRaw: Bool) -> DebugContentSnapshot {
        DebugContentSnapshot(target: "settings.music", kind: "settings", rows: debugContentRows(maxItems: maxItems), settings: .object(["domain": .string("music"), "section": .string("quality")]))
    }
    public func debugPerformAction(_ request: DebugActionRequest) async throws -> DebugJSONValue {
        if request.action == "list.item.invoke" { return try debugInvokeRow(params: request.params) }
        throw DebugAPIError.invalidArgument("Unsupported action '\(request.action)' on MusicQualitySettingsViewController")
    }
}

extension MusicMetadataSettingsViewController: DebugPageInspectable, DebugPageActionable {
    public var debugPageStatus: DebugPageStatus {
        DebugPageStatus(target: "settings.music", moduleId: "music", route: "settings/music/metadata", title: "歌词／封面设置", settingsDomain: "music", settingsSection: "metadata")
    }
    public func debugContent(maxItems: Int, includeRaw: Bool) -> DebugContentSnapshot {
        DebugContentSnapshot(target: "settings.music", kind: "settings", rows: debugContentRows(maxItems: maxItems), settings: .object(["domain": .string("music"), "section": .string("metadata")]))
    }
    public func debugPerformAction(_ request: DebugActionRequest) async throws -> DebugJSONValue {
        if request.action == "list.item.invoke" { return try debugInvokeRow(params: request.params) }
        throw DebugAPIError.invalidArgument("Unsupported action '\(request.action)' on MusicMetadataSettingsViewController")
    }
}

#endif
