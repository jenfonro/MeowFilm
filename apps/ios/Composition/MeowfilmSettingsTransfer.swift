import Foundation
import CoreFoundation
import Combine
import MeowContracts
import MeowDebug
import MeowPlayback
import MeowMusicFeature
import MeowMusicPlatform
import MeowMusicNativeService
import MeowFilmFeature
import MeowNovelFeature
import MeowRuntime
import MeowStorage
import MeowUI

enum MeowfilmSettingsTransferError: LocalizedError {
    case unavailable
    case busy
    case invalidFile

    var errorDescription: String? {
        switch self {
        case .unavailable: return "数据服务暂不可用"
        case .busy: return "正在处理其他导入导出任务"
        case .invalidFile: return "设置文件无效（需为 UTF-8 JSON 对象）"
        }
    }
}

/// Cross-domain plaintext settings export and tolerant partial-consumption import (`meowfilm.settings` v1).
/// Only external JSON files use `JSONSerialization`; all persistence goes through typed native/runtime owners.
@MainActor
final class MeowfilmSettingsTransfer: ObservableObject {
    private static let musicThreePlatforms: [String] = ["wy", "tx", "kg"]
    private static let musicFivePlatforms: [String] = ["wy", "tx", "kg", "kw", "mg"]
    private static let musicQualityCategories: [String] = ["master", "hires", "flac", "320k", "128k"]
    private static let musicSpatialCategories: [String] = ["dolby", "sky", "galaxy"]
    private static let musicQualityRequests: Set<String> = [
        "master", "hires", "flac", "320k", "128k", "standard", "dolby", "sky", "galaxy"
    ]

    weak var assembly: MeowfilmAssembly?
    @Published private(set) var isBusy = false

    init(assembly: MeowfilmAssembly) {
        self.assembly = assembly
    }

    func exportData() async throws -> Data {
        guard !isBusy else { throw MeowfilmSettingsTransferError.busy }
        guard let assembly, !assembly.closed, let resources = assembly.resources else {
            throw MeowfilmSettingsTransferError.unavailable
        }
        isBusy = true
        defer { isBusy = false }

        let root = try await buildExportDocument(assembly: assembly, resources: resources)
        return try JSONSerialization.data(
            withJSONObject: root,
            options: [.prettyPrinted, .sortedKeys, .withoutEscapingSlashes]
        )
    }

    func importData(_ rawData: Data) async throws {
        guard !isBusy else { throw MeowfilmSettingsTransferError.busy }
        guard let assembly, !assembly.closed, let resources = assembly.resources else {
            throw MeowfilmSettingsTransferError.unavailable
        }
        var data = rawData
        if data.starts(with: [0xEF, 0xBB, 0xBF]) {
            data.removeFirst(3)
        }
        guard String(data: data, encoding: .utf8) != nil,
              let parsed = try? JSONSerialization.jsonObject(with: data, options: []),
              let root = parsed as? [String: Any] else {
            throw MeowfilmSettingsTransferError.invalidFile
        }

        isBusy = true
        defer { isBusy = false }

        await applyImportRoot(root, assembly: assembly, resources: resources)
    }

    func exportToFile(path: String) async throws -> URL {
        let trimmed = path.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty else { throw MeowfilmSettingsTransferError.invalidFile }
        let bytes = try await exportData()
        let url = URL(fileURLWithPath: trimmed).standardizedFileURL
        try FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
        try bytes.write(to: url, options: [.atomic])
        try? FileManager.default.setAttributes([.posixPermissions: 0o600], ofItemAtPath: url.path)
        return url
    }

    func importFromFile(path: String) async throws {
        let trimmed = path.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty else { throw MeowfilmSettingsTransferError.invalidFile }
        let url = URL(fileURLWithPath: trimmed).standardizedFileURL
        let bytes = try await ImportedFileReader.read(url)
        try await importData(bytes)
    }

    // MARK: - Export

    private func buildExportDocument(assembly: MeowfilmAssembly, resources: MeowfilmResources) async throws -> [String: Any] {
        let appearancePrefs = assembly.appearance.preferences
        let volumes = assembly.volumePreferences.exportedVolumes()
        let playerPrefs = assembly.playerPreferences.preferences

        let homeSelection = try? await assembly.service?.homeSourceSelection()
        let catalogPlatform = homeSelection?.selected.rawValue ?? "wy"

        let tmdb = try await resources.filmMetadata.settings()
        let douban = try await resources.filmDoubanSettings.settings()
        let filmPrefs = try await resources.filmSettings.repository.exportPreferencesRecord()
        try? await resources.filmRules.initialize()
        let filmRules = (try? await resources.filmRules.savedSnapshot()) ?? FilmRuleSet()
        let (catSettings, videoSources) = try await resources.filmSourceSettings.exportSettings()
        let panAccounts = try await resources.filmSettings.repository.exportPanAccountRecords()

        let musicAccounts = (try? await resources.musicAccounts?.exportOfflineAccounts()) ?? []
        let playbackState = (try? await assembly.service?.state()) ?? MusicPlaybackState()
        let metadataPrefs = try? await assembly.service?.metadataPreferences()
        let feedbackSettings = try? await assembly.service?.playbackFeedbackSettings()
        let autoSyncPrefs = try? await assembly.service?.automaticLibrarySyncPreferences()
        let searchPrefs = assembly.music?.searchPreferences.preferences ?? MusicSearchPreferences()
        let musicScripts = await exportMusicNetworkScripts(resources: resources)

        let novelSnapshot = (try? await resources.novelLibrary.snapshot()) ?? NovelLibrarySnapshot()
        let novelClient = NovelSourceClient(runtime: resources.novelRuntime)
        let novelSources = (try? await novelClient.exportAllDefinitions()) ?? []
        let novelCookiesValue = await novelClient.exportCookies()
        let novelCookiesJSON: [Any]
        if let encoded = try? RuntimeJSON.encode(novelCookiesValue),
           let arr = (try? JSONSerialization.jsonObject(with: encoded)) as? [Any] {
            novelCookiesJSON = arr
        } else {
            novelCookiesJSON = []
        }

        let catMappings: [[String: Any]] = catSettings.mappings.map { m in
            ["provider": m.provider, "aliases": m.aliases]
        }
        let catScripts: [[String: Any]] = catSettings.scripts.map { s in
            [
                "installationId": s.installationId,
                "name": s.name,
                "sourceUrl": s.sourceUrl,
                "enabled": s.enabled,
                "inputRevision": s.inputRevision,
            ]
        }
        let probesJSON: [[String: Any]] = videoSources.probes.map { p in
            let sites: [[String: Any]] = p.sites.map { site in
                [
                    "key": site.key,
                    "name": site.name,
                    "api": site.api,
                    "actions": site.actions,
                    "methods": site.methods,
                ]
            }
            return [
                "installationId": p.installationId,
                "importedAt": p.importedAt as Any? ?? NSNull(),
                "updatedAt": p.updatedAt as Any? ?? NSNull(),
                "updateState": p.updateState,
                "updateError": p.updateError as Any? ?? NSNull(),
                "sourceHash": p.sourceHash as Any? ?? NSNull(),
                "inputRevision": max(1, p.inputRevision),
                "sites": sites,
            ]
        }
        let videosJSON: [[String: Any]] = videoSources.videos.map { v in
            let detObj: Any
            if let det = v.detection {
                let steps: [[String: Any]] = det.steps.map { st in
                    [
                        "operation": st.operation,
                        "state": st.state,
                        "error": st.error as Any? ?? NSNull(),
                    ]
                }
                detObj = [
                    "result": det.result,
                    "checkedAt": det.checkedAt,
                    "playbackResolved": det.playbackResolved,
                    "steps": steps,
                ] as [String: Any]
            } else {
                detObj = NSNull()
            }
            return [
                "installationId": v.key.installationId,
                "siteKey": v.key.siteKey,
                "name": v.name,
                "api": v.api,
                "order": v.order,
                "enabled": v.options.enabled,
                "homeVisible": v.options.homeVisible,
                "searchEnabled": v.options.searchEnabled,
                "aggregateImages": v.options.aggregateImages,
                "actions": v.actions,
                "methods": v.methods,
                "detection": detObj,
            ]
        }
        let deletedJSON: [[String: Any]] = videoSources.deleted.map { d in
            ["installationId": d.installationId, "siteKey": d.siteKey]
        }

        var panAccountsObj: [String: Any] = [:]
        for acc in panAccounts {
            var creds: [String: Any] = [:]
            if let v = acc.cookie, !v.isEmpty { creds["cookie"] = v }
            if let v = acc.username, !v.isEmpty { creds["username"] = v }
            if let v = acc.password, !v.isEmpty { creds["password"] = v }
            if let v = acc.authorization, !v.isEmpty { creds["authorization"] = v }
            if let v = acc.refreshedAtMs { creds["refreshedAt"] = v }
            if !creds.isEmpty { panAccountsObj[acc.provider] = creds }
        }

        var musicAccountsObj: [String: Any] = [:]
        for acc in musicAccounts {
            musicAccountsObj[acc.platform.rawValue] = [
                "userId": acc.userID,
                "displayName": acc.displayName,
                "avatar": acc.avatar as Any? ?? NSNull(),
                "cookie": acc.cookie,
                "credentials": acc.credentials,
            ] as [String: Any]
        }

        let feedbackEnabled = MusicAccountPlatform.allCases.compactMap { plat -> String? in
            feedbackSettings?.platforms[plat]?.enabled == true ? plat.rawValue : nil
        }

        var musicSyncObj: [String: Any] = [:]
        for plat in MusicAccountPlatform.allCases {
            let sel = autoSyncPrefs?.selections.first { $0.platform == plat }
            musicSyncObj[plat.rawValue] = [
                "favorites": sel?.favorites ?? false,
                "playlists": sel?.playlists ?? false,
            ]
        }

        let metaOrder = metadataPrefs?.platforms.map(\.rawValue) ?? Self.musicFivePlatforms
        let metaEnabled = metaOrder.filter { raw in
            guard let p = MusicPublicBrowsePlatform(rawValue: raw) else { return false }
            return metadataPrefs?.enabledPlatforms.contains(p) ?? true
        }

        func searchKindJSON(_ kind: MusicSearchPreferences.Kind) -> [String: Any] {
            let enabled = kind.platforms.map(\.rawValue)
            let order = enabled + Self.musicFivePlatforms.filter { !enabled.contains($0) }
            return [
                "order": order,
                "enabled": enabled,
                "pages": kind.pages,
                "concurrency": kind.concurrency,
                "cacheMinutes": kind.cacheMinutes,
            ]
        }

        let novelReading = novelSnapshot.settings
        let readingMode = novelReading.mode == .instant ? "none" : novelReading.mode.rawValue
        let bgImageBase64 = novelReading.backgroundImage?.base64EncodedString() ?? ""
        let novelRulesJSON: [[String: Any]] = novelSnapshot.rules.map { r in
            [
                "id": r.id,
                "name": r.name,
                "group": r.group,
                "pattern": r.pattern,
                "replacement": r.replacement,
                "isRegex": r.isRegex,
                "enabled": r.isEnabled,
                "scopeTitle": r.scopeTitle,
                "scopeContent": r.scopeContent,
                "scope": r.scope,
                "excludeScope": r.excludeScope,
                "order": r.order,
                "timeoutMillisecond": r.timeoutMillisecond,
            ]
        }
        let novelSourcesJSON: [[String: Any]] = novelSources.map { s in
            let defObj: Any
            if let defData = s.definition.data(using: .utf8),
               let parsed = try? JSONSerialization.jsonObject(with: defData) {
                defObj = parsed
            } else {
                defObj = s.definition
            }
            return [
                "installationId": s.installationId,
                "name": s.name,
                "sourceUrl": s.sourceUrl,
                "group": s.group,
                "order": Int64(s.order.rounded()),
                "enabled": s.enabled,
                "enabledExplore": s.enabledExplore,
                "protocol": s.protocol,
                "definition": defObj,
            ]
        }

        return [
            "format": "meowfilm.settings",
            "version": 1,
            "general": [
                "musicAppearance": [
                    "playlistView": appearancePrefs.playlistView.rawValue,
                    "playerTheme": appearancePrefs.playerTheme.rawValue,
                    "colorMode": appearancePrefs.colorMode.rawValue,
                    "colorPalette": MusicAppearancePreferencesStore.encodePalette(appearancePrefs.colorPalette),
                ],
                "musicCatalogSource": catalogPlatform,
                "playbackVolumes": volumes,
            ],
            "ios": [
                "musicAppearanceExtras": [
                    "lockLyric": appearancePrefs.lockLyric,
                    "lyricTranslation": appearancePrefs.lyricTranslation,
                    "lyricBackground": appearancePrefs.lyricBackground,
                ],
                "musicSearchPlaylists": searchPrefs.searchPlaylists,
                "filmPlayer": [
                    "engine": playerPrefs.engine.rawValue,
                    "defaultRate": playerPrefs.defaultRate,
                    "holdRate": playerPrefs.holdRate,
                    "seekStep": playerPrefs.seekStep,
                    "showsControlCenter": playerPrefs.showsControlCenter,
                    "recognizesChapters": playerPrefs.recognizesChapters,
                    "singleTapCenter": playerPrefs.singleTapCenter.rawValue,
                    "singleTapSides": playerPrefs.singleTapSides.rawValue,
                    "doubleTapCenter": playerPrefs.doubleTapCenter.rawValue,
                    "doubleTapSides": playerPrefs.doubleTapSides.rawValue,
                    "swipeVolume": playerPrefs.swipeVolume,
                    "swipeBrightness": playerPrefs.swipeBrightness,
                    "swipeSeek": playerPrefs.swipeSeek,
                    "swipeEpisodes": playerPrefs.swipeEpisodes,
                    "hapticHold": playerPrefs.hapticHold,
                    "hapticBrightness": playerPrefs.hapticBrightness,
                    "hapticVolume": playerPrefs.hapticVolume,
                    "hapticSeek": playerPrefs.hapticSeek,
                ],
            ],
            "film": [
                "tmdb": [
                    "apiBase": tmdb.apiBase,
                    "imageBase": tmdb.imageBase,
                    "apiKey": tmdb.apiKey,
                    "accessToken": tmdb.accessToken,
                    "language": tmdb.language,
                    "region": tmdb.region,
                    "includeAdult": tmdb.includeAdult,
                ],
                "douban": [
                    "apiBase": douban.apiBase,
                    "searchBase": douban.searchBase,
                    "imageBase": douban.imageBase,
                    "apiProxy": douban.apiProxy,
                    "imageProxy": douban.imageProxy,
                    "cookie": douban.cookie,
                ],
                "preferences": [
                    "ruleOrder": filmPrefs.ruleOrder,
                    "panOrder": filmPrefs.panOrder,
                    "keywords": filmPrefs.keywords,
                    "siteCleanKeywords": filmPrefs.siteCleanKeywords,
                    "iosAutoPrepare": filmPrefs.iosAutoPrepare,
                    "metadataViewProvider": filmPrefs.metadataViewProvider,
                ],
                "rules": [
                    "episodeCleanRegexRules": filmRules.episodeCleanRegexRules,
                    "episodeRules": Self.encodeMatchRules(filmRules.episodeRules),
                    "movieRules": Self.encodeMatchRules(filmRules.movieRules),
                    "aggregateRegexRules": filmRules.aggregateRegexRules,
                    "seasonRules": Self.encodeMatchRules(filmRules.seasonRules),
                ],
                "cat": [
                    "port": catSettings.port,
                    "providers": catSettings.providers,
                    "mappings": catMappings,
                    "scripts": catScripts,
                ],
                "videoSources": [
                    "probes": probesJSON,
                    "videos": videosJSON,
                    "deleted": deletedJSON,
                ],
            ],
            "pan": [
                "accounts": panAccountsObj,
            ],
            "music": [
                "accounts": musicAccountsObj,
                "playback": [
                    "mode": playbackState.mode.rawValue,
                    "lyricsEnabled": playbackState.lyricsEnabled,
                    "showTranslation": playbackState.showTranslation,
                    "qualityRequest": playbackState.requestQuality,
                    "qualityOrder": playbackState.qualityOrder,
                    "qualityEnabled": playbackState.qualityEnabled,
                    "spatialOrder": playbackState.spatialOrder,
                    "spatialEnabled": playbackState.spatialEnabled,
                    "officialPlatforms": Self.musicThreePlatforms,
                    "officialEnabled": playbackState.officialSources.map(\.rawValue),
                    "metadataPlatforms": metaOrder,
                    "metadataEnabled": metaEnabled,
                ],
                "search": [
                    "songs": searchKindJSON(searchPrefs.songs),
                    "playlists": searchKindJSON(searchPrefs.playlists),
                ],
                "feedback": [
                    "enabled": feedbackEnabled,
                ],
                "sync": musicSyncObj,
                "scripts": musicScripts,
            ],
            "novel": [
                "reading": [
                    "mode": readingMode,
                    "fontSize": novelReading.fontSize,
                    "bold": novelReading.fontWeight > 0,
                    "fontFamily": novelReading.fontName,
                    "lineSpacing": novelReading.lineSpacing,
                    "paragraphSpacing": novelReading.paragraphSpacing,
                    "margin": novelReading.margin,
                    "contentWidth": novelReading.contentWidth,
                    "conversion": novelReading.conversion,
                    "theme": novelReading.theme,
                    "foreground": novelReading.foreground,
                    "background": novelReading.background,
                    "backgroundImage": bgImageBase64,
                    "autoSpeed": novelReading.automaticSpeed,
                ],
                "rules": novelRulesJSON,
                "sources": novelSourcesJSON,
                "cookies": novelCookiesJSON,
            ],
        ]
    }

    private func exportMusicNetworkScripts(resources: MeowfilmResources) async -> [[String: Any]] {
        var result: [[String: Any]] = []
        guard let list = try? await resources.musicScripts.scriptSources() else { return result }
        for item in list {
            guard case .network = item.origin else { continue }
            guard let detail = try? await resources.musicScripts.scriptSourceDetail(item.id),
                  let url = detail.networkURL, !url.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
                continue
            }
            result.append([
                "name": item.name,
                "sourceUrl": url,
                "enabled": item.enabled,
            ])
        }
        return result
    }

    // MARK: - Import (Tolerant Partial Consumption)

    private func applyImportRoot(_ root: [String: Any], assembly: MeowfilmAssembly, resources: MeowfilmResources) async {
        var filmTouched = false

        let generalBlock = Self.dict(root["general"])
        let iosBlock = Self.dict(root["ios"])

        if generalBlock != nil || iosBlock != nil {
            await applyGeneralAndIOSBlocks(general: generalBlock, ios: iosBlock, assembly: assembly)
        }

        if let panBlock = Self.dict(root["pan"]), let accountsObj = Self.dict(panBlock["accounts"]) {
            await applyPanAccounts(accountsObj, resources: resources)
            filmTouched = true
        }

        if let filmBlock = Self.dict(root["film"]) {
            await applyFilmBlock(filmBlock, assembly: assembly, resources: resources)
            filmTouched = true
        }

        if filmTouched {
            await assembly.filmSettings?.reloadAfterImport()
        }

        if let musicBlock = Self.dict(root["music"]) {
            await applyMusicBlock(musicBlock, iosBlock: iosBlock, assembly: assembly, resources: resources)
        }

        if let novelBlock = Self.dict(root["novel"]) {
            await applyNovelBlock(novelBlock, assembly: assembly, resources: resources)
        }
    }

    private func applyGeneralAndIOSBlocks(
        general: [String: Any]?,
        ios: [String: Any]?,
        assembly: MeowfilmAssembly
    ) async {
        let appObj = general.flatMap { Self.dict($0["musicAppearance"]) }
        let extrasObj = ios.flatMap { Self.dict($0["musicAppearanceExtras"]) }
        if appObj != nil || extrasObj != nil {
            let pv = appObj.flatMap { Self.str($0["playlistView"]) }.flatMap(MusicPlaylistAppearance.init(rawValue:))
            let pt = appObj.flatMap { Self.str($0["playerTheme"]) }.flatMap(MusicPlayerAppearance.init(rawValue:))
            let cm = appObj.flatMap { Self.str($0["colorMode"]) }.flatMap(MusicColorMode.init(rawValue:))
            let cp = appObj.flatMap { Self.str($0["colorPalette"]) }.flatMap(MusicAppearancePreferencesStore.decodePalette)
            let lockLyric = extrasObj.flatMap { Self.bool($0["lockLyric"]) }
            let lyricTrans = extrasObj.flatMap { Self.bool($0["lyricTranslation"]) }
            let lyricBg = extrasObj.flatMap { Self.bool($0["lyricBackground"]) }

            _ = await assembly.appearance.applyImportedPreferences { prefs in
                if let pv { prefs.playlistView = pv }
                if let pt { prefs.playerTheme = pt }
                if let cm { prefs.colorMode = cm }
                if let cp { prefs.colorPalette = cp }
                if let lockLyric { prefs.lockLyric = lockLyric }
                if let lyricTrans { prefs.lyricTranslation = lyricTrans }
                if let lyricBg { prefs.lyricBackground = lyricBg }
            }
        }

        if let catStr = general.flatMap({ Self.str($0["musicCatalogSource"]) }),
           ["wy", "kg"].contains(catStr),
           let plat = MusicPublicBrowsePlatform(rawValue: catStr),
           let service = assembly.service {
            if let cur = try? await service.homeSourceSelection(), cur.selected != plat {
                _ = try? await service.selectHomeSource(plat, ifRevision: cur.revision)
            }
        }

        if let vols = general.flatMap({ Self.dict($0["playbackVolumes"]) }) {
            if let musicVol = Self.double(vols["music"]), (0.0...1.0).contains(musicVol) {
                assembly.volumePreferences.saveVolume(musicVol, for: "music")
            }
            if let filmVol = Self.double(vols["film"]), (0.0...1.0).contains(filmVol) {
                assembly.volumePreferences.saveVolume(filmVol, for: "film")
            }
        }

        if let fp = ios.flatMap({ Self.dict($0["filmPlayer"]) }) {
            let current = assembly.playerPreferences.preferences
            var candidate = current
            if let raw = Self.str(fp["engine"]), let eng = PlaybackVideoEngine(rawValue: raw) {
                candidate.engine = eng
            }
            if let r = Self.double(fp["defaultRate"]), FilmPlayerPreferences.defaultRates(for: .native).contains(r) {
                candidate.defaultRate = min(r, candidate.engine.maximumRate)
            }
            if let hr = Self.double(fp["holdRate"]), FilmPlayerPreferences.holdRates(for: .native).contains(hr) {
                candidate.holdRate = min(hr, candidate.engine.maximumRate)
            }
            if let ss = Self.double(fp["seekStep"]), FilmPlayerPreferences.seekSteps.contains(ss) {
                candidate.seekStep = ss
            }
            if let b = Self.bool(fp["showsControlCenter"]) { candidate.showsControlCenter = b }
            if let b = Self.bool(fp["recognizesChapters"]) { candidate.recognizesChapters = b }
            if let raw = Self.str(fp["singleTapCenter"]), let act = FilmPlayerTapAction(rawValue: raw), act != .seek {
                candidate.singleTapCenter = act
            }
            if let raw = Self.str(fp["singleTapSides"]), let act = FilmPlayerTapAction(rawValue: raw) {
                candidate.singleTapSides = act
            }
            if let raw = Self.str(fp["doubleTapCenter"]), let act = FilmPlayerTapAction(rawValue: raw),
               act == .none || act == .playPause {
                candidate.doubleTapCenter = act
            }
            if let raw = Self.str(fp["doubleTapSides"]), let act = FilmPlayerTapAction(rawValue: raw),
               act != .toggleControls {
                candidate.doubleTapSides = act
            }
            if let b = Self.bool(fp["swipeVolume"]) { candidate.swipeVolume = b }
            if let b = Self.bool(fp["swipeBrightness"]) { candidate.swipeBrightness = b }
            if let b = Self.bool(fp["swipeSeek"]) { candidate.swipeSeek = b }
            if let b = Self.bool(fp["swipeEpisodes"]) { candidate.swipeEpisodes = b }
            if let b = Self.bool(fp["hapticHold"]) { candidate.hapticHold = b }
            if let b = Self.bool(fp["hapticBrightness"]) { candidate.hapticBrightness = b }
            if let b = Self.bool(fp["hapticVolume"]) { candidate.hapticVolume = b }
            if let b = Self.bool(fp["hapticSeek"]) { candidate.hapticSeek = b }
            _ = await assembly.playerPreferences.replace(candidate)
        }

        if let searchPlaylists = ios.flatMap({ Self.bool($0["musicSearchPlaylists"]) }),
           let store = assembly.music?.searchPreferences {
            var cur = store.preferences
            cur.searchPlaylists = searchPlaylists
            _ = await store.update(cur)
        }
    }

    private func applyPanAccounts(_ accountsObj: [String: Any], resources: MeowfilmResources) async {
        var map: [String: ExportedPanAccountRecord?] = [:]
        for provider in ["baidu", "quark", "uc", "tianyi", "yidong"] {
            guard let creds = Self.dict(accountsObj[provider]) else {
                map[provider] = nil
                continue
            }
            let cookie = Self.str(creds["cookie"])
            let username = Self.str(creds["username"])
            let password = Self.str(creds["password"])
            let authorization = Self.str(creds["authorization"])
            let refreshedAt = Self.int64(creds["refreshedAt"])
            let hasAny = (cookie?.isEmpty == false) || (username?.isEmpty == false) ||
                (password?.isEmpty == false) || (authorization?.isEmpty == false) || refreshedAt != nil
            map[provider] = hasAny ? ExportedPanAccountRecord(
                provider: provider,
                cookie: cookie,
                username: username,
                password: password,
                authorization: authorization,
                refreshedAtMs: refreshedAt
            ) : nil
        }
        await resources.filmSettings.repository.replaceImportedPanAccounts(map)
    }

    private func applyFilmBlock(_ block: [String: Any], assembly: MeowfilmAssembly, resources: MeowfilmResources) async {
        if let tmdbObj = Self.dict(block["tmdb"]) {
            var changed = false
            for key in ["apiBase", "imageBase", "apiKey", "accessToken", "language", "region", "includeAdult"] {
                guard let raw = tmdbObj[key] else { continue }
                let rv: RuntimeValue? = key == "includeAdult"
                    ? Self.bool(raw).map(RuntimeValue.bool)
                    : Self.str(raw).map(RuntimeValue.string)
                guard let rv else { continue }
                if (try? await resources.filmMetadata.updateSettings([key: rv])) != nil {
                    changed = true
                }
            }
            if changed { assembly.filmSearch?.metadataSettingsChanged() }
        }

        if let doubanObj = Self.dict(block["douban"]) {
            for key in ["apiBase", "searchBase", "imageBase", "apiProxy", "imageProxy", "cookie"] {
                guard let s = Self.str(doubanObj[key]) else { continue }
                _ = try? await resources.filmDoubanSettings.updateSettings([key: .string(s)])
            }
        }

        if let prefsObj = Self.dict(block["preferences"]) {
            try? await resources.filmSettings.repository.saveImportedPreferences(
                ruleOrder: Self.strArray(prefsObj["ruleOrder"]),
                panOrder: Self.strArray(prefsObj["panOrder"]),
                keywords: Self.strArray(prefsObj["keywords"]),
                siteCleanKeywords: Self.strArray(prefsObj["siteCleanKeywords"]),
                iosAutoPrepare: Self.bool(prefsObj["iosAutoPrepare"]),
                metadataViewProvider: Self.str(prefsObj["metadataViewProvider"])
            )
        }

        if let rulesObj = Self.dict(block["rules"]) {
            try? await resources.filmRules.initialize()
            let current = (try? await resources.filmRules.savedSnapshot()) ?? FilmRuleSet()
            let next = FilmRuleSet(
                episodeCleanRegexRules: Self.strArray(rulesObj["episodeCleanRegexRules"]) ?? current.episodeCleanRegexRules,
                episodeRules: Self.parseMatchRules(rulesObj["episodeRules"]) ?? current.episodeRules,
                movieRules: Self.parseMatchRules(rulesObj["movieRules"]) ?? current.movieRules,
                aggregateRegexRules: Self.strArray(rulesObj["aggregateRegexRules"]) ?? current.aggregateRegexRules,
                seasonRules: Self.parseMatchRules(rulesObj["seasonRules"]) ?? current.seasonRules
            )
            _ = try? await resources.filmRules.save(next)
        }

        if let catObj = Self.dict(block["cat"]) {
            let port = Self.int(catObj["port"]).flatMap { (0...65535).contains($0) ? $0 : nil }
            var providers: [String: Bool]?
            if let provObj = Self.dict(catObj["providers"]) {
                var pMap: [String: Bool] = [:]
                for (k, v) in provObj {
                    if let flag = Self.bool(v) { pMap[k] = flag }
                }
                providers = pMap
            }
            var mappings: [ExportedFilmCatMapping]?
            if let mapArr = catObj["mappings"] as? [Any] {
                mappings = mapArr.compactMap { item -> ExportedFilmCatMapping? in
                    guard let obj = Self.dict(item), let prov = Self.str(obj["provider"]) else { return nil }
                    return ExportedFilmCatMapping(provider: prov, aliases: Self.strArray(obj["aliases"]) ?? [])
                }
            }
            var scripts: [ExportedFilmCatScript]?
            if let scrArr = catObj["scripts"] as? [Any] {
                scripts = scrArr.compactMap { item -> ExportedFilmCatScript? in
                    guard let obj = Self.dict(item) else { return nil }
                    let url = (Self.str(obj["sourceUrl"]) ?? "").trimmingCharacters(in: .whitespacesAndNewlines)
                    guard !url.isEmpty else { return nil }
                    let name = Self.str(obj["name"]).flatMap { $0.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty ? nil : $0 } ?? url
                    let id = Self.str(obj["installationId"]) ?? ""
                    let enabled = Self.bool(obj["enabled"]) ?? true
                    let rev = Self.int64(obj["inputRevision"]).flatMap { $0 >= 1 ? $0 : nil } ?? 1
                    return ExportedFilmCatScript(installationId: id, name: name, sourceUrl: url, enabled: enabled, inputRevision: rev)
                }
            }
            try? await resources.filmSourceSettings.replaceImportedCatSettings(
                port: port,
                providers: providers,
                mappings: mappings,
                scripts: scripts
            )
        }

        if let vsObj = Self.dict(block["videoSources"]) {
            let probes: [ExportedFilmProbe]? = (vsObj["probes"] as? [Any]).map { arr in
                arr.compactMap { item -> ExportedFilmProbe? in
                    guard let obj = Self.dict(item), let id = Self.str(obj["installationId"]) else { return nil }
                    let imp = Self.int64(obj["importedAt"])
                    let upd = Self.int64(obj["updatedAt"])
                    let uState = Self.str(obj["updateState"]) ?? "idle"
                    let uErr = Self.str(obj["updateError"])
                    let hash = Self.str(obj["sourceHash"])
                    let inRev = Self.int64(obj["inputRevision"]).flatMap { $0 >= 1 ? $0 : nil } ?? 1
                    let sites: [FilmSourceSite] = ((obj["sites"] as? [Any]) ?? []).compactMap { sItem in
                        guard let sObj = Self.dict(sItem),
                              let sKey = Self.str(sObj["key"]),
                              let sName = Self.str(sObj["name"]),
                              let sApi = Self.str(sObj["api"]) else { return nil }
                        let sActions = Self.strArray(sObj["actions"]) ?? []
                        let sMethods = Self.strDict(sObj["methods"])
                        return FilmSourceSite(key: sKey, name: sName, api: sApi, actions: sActions, methods: sMethods)
                    }
                    return ExportedFilmProbe(
                        installationId: id,
                        importedAt: imp,
                        updatedAt: upd,
                        updateState: uState,
                        updateError: uErr,
                        sourceHash: hash,
                        inputRevision: inRev,
                        sites: sites
                    )
                }
            }

            let videos: [FilmVideoSource]? = (vsObj["videos"] as? [Any]).map { arr in
                var list: [FilmVideoSource] = []
                for (idx, item) in arr.enumerated() {
                    guard let obj = Self.dict(item),
                          let id = Self.str(obj["installationId"]),
                          let siteKey = Self.str(obj["siteKey"]),
                          let name = Self.str(obj["name"]),
                          let api = Self.str(obj["api"]) else { continue }
                    let order = Self.int(obj["order"]).flatMap { $0 >= 0 ? $0 : nil } ?? idx
                    let en = Self.bool(obj["enabled"]) ?? true
                    let home = Self.bool(obj["homeVisible"]) ?? true
                    let search = Self.bool(obj["searchEnabled"]) ?? true
                    let agg = Self.bool(obj["aggregateImages"]) ?? false
                    let actions = Self.strArray(obj["actions"]) ?? []
                    let methods = Self.strDict(obj["methods"])
                    var det: FilmSourceDetection?
                    if let detObj = Self.dict(obj["detection"]),
                       let dRes = Self.str(detObj["result"]),
                       let dChk = Self.int64(detObj["checkedAt"]),
                       let dPlay = Self.bool(detObj["playbackResolved"]) {
                        let steps: [FilmSourceProbeStep] = ((detObj["steps"] as? [Any]) ?? []).compactMap { stItem in
                            guard let stObj = Self.dict(stItem),
                                  let op = Self.str(stObj["operation"]),
                                  let stState = Self.str(stObj["state"]) else { return nil }
                            return FilmSourceProbeStep(op, stState, Self.str(stObj["error"]))
                        }
                        det = FilmSourceDetection(result: dRes, checkedAt: dChk, playbackResolved: dPlay, steps: steps)
                    }
                    list.append(FilmVideoSource(
                        key: FilmSourceKey(installationId: id, siteKey: siteKey),
                        name: name,
                        api: api,
                        actions: actions,
                        methods: methods,
                        options: FilmVideoOptions(enabled: en, homeVisible: home, searchEnabled: search, aggregateImages: agg),
                        order: order,
                        detection: det
                    ))
                }
                return list
            }

            let deleted: [FilmSourceKey]? = (vsObj["deleted"] as? [Any]).map { arr in
                arr.compactMap { item -> FilmSourceKey? in
                    guard let obj = Self.dict(item),
                          let id = Self.str(obj["installationId"]),
                          let siteKey = Self.str(obj["siteKey"]) else { return nil }
                    return FilmSourceKey(installationId: id, siteKey: siteKey)
                }
            }

            try? await resources.filmSourceSettings.replaceImportedVideoSources(
                probes: probes,
                videos: videos,
                deleted: deleted
            )
        }
    }

    private func applyMusicBlock(
        _ block: [String: Any],
        iosBlock: [String: Any]?,
        assembly: MeowfilmAssembly,
        resources: MeowfilmResources
    ) async {
        if let accountsObj = Self.dict(block["accounts"]), let musicAccounts = resources.musicAccounts {
            var targets: [MusicAccountPlatform: MusicOfflineAccountRecord?] = [:]
            for plat in MusicAccountPlatform.allCases {
                guard let item = Self.dict(accountsObj[plat.rawValue]) else {
                    targets[plat] = nil
                    continue
                }
                let uid = Self.str(item["userId"]) ?? ""
                let dname = Self.str(item["displayName"]) ?? ""
                let avatar = Self.str(item["avatar"])
                let cookie = Self.str(item["cookie"]) ?? ""
                let creds = Self.strDict(item["credentials"])
                if creds.isEmpty && cookie.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
                    targets[plat] = nil
                } else {
                    targets[plat] = MusicOfflineAccountRecord(
                        platform: plat,
                        userID: uid,
                        displayName: dname,
                        avatar: avatar,
                        cookie: cookie,
                        credentials: creds
                    )
                }
            }
            try? await musicAccounts.replaceOfflineAccounts(targets)
        }

        if let pbObj = Self.dict(block["playback"]), let service = assembly.service {
            if let current = try? await service.state() {
                let mode = Self.int(pbObj["mode"]).flatMap(MusicPlaybackMode.init(rawValue:))
                let lyricsEnabled = Self.bool(pbObj["lyricsEnabled"])
                let showTranslation = Self.bool(pbObj["showTranslation"])
                let requestQuality = Self.str(pbObj["qualityRequest"]).flatMap {
                    Self.musicQualityRequests.contains($0) ? $0 : nil
                }

                let qOrder: [String]? = Self.strArray(pbObj["qualityOrder"]).map {
                    Self.normalizeSubset($0, allowed: Self.musicQualityCategories, appendMissing: true)
                }
                let effectiveQOrder = qOrder ?? current.qualityOrder
                let qEnabled: [String]? = Self.strArray(pbObj["qualityEnabled"]).map {
                    Self.normalizeSubset($0, allowed: effectiveQOrder, appendMissing: false)
                }

                let sOrder: [String]? = Self.strArray(pbObj["spatialOrder"]).map {
                    Self.normalizeSubset($0, allowed: Self.musicSpatialCategories, appendMissing: true)
                }
                let effectiveSOrder = sOrder ?? current.spatialOrder
                let sEnabled: [String]? = Self.strArray(pbObj["spatialEnabled"]).map {
                    Self.normalizeSubset($0, allowed: effectiveSOrder, appendMissing: false)
                }

                let officialSources: [MusicAccountPlatform]? = Self.strArray(pbObj["officialEnabled"]).map { arr in
                    Self.normalizeSubset(arr, allowed: Self.musicThreePlatforms, appendMissing: false)
                        .compactMap(MusicAccountPlatform.init(rawValue:))
                }

                let change = MusicSettingsChange(
                    ifRevision: current.settingsRevision,
                    mode: mode,
                    qualityEnabled: qEnabled,
                    qualityOrder: qOrder,
                    spatialEnabled: sEnabled,
                    spatialOrder: sOrder,
                    lyricsEnabled: lyricsEnabled,
                    showTranslation: showTranslation,
                    officialSources: officialSources,
                    requestQuality: requestQuality
                )
                if let music = assembly.music {
                    await music.updateSettings(change)
                } else {
                    _ = try? await service.updateSettings(change)
                }
            }

            if pbObj["metadataPlatforms"] != nil || pbObj["metadataEnabled"] != nil,
               let curMeta = try? await service.metadataPreferences() {
                let mOrder: [MusicPublicBrowsePlatform]? = Self.strArray(pbObj["metadataPlatforms"]).map { arr in
                    Self.normalizeSubset(arr, allowed: Self.musicFivePlatforms, appendMissing: true)
                        .compactMap(MusicPublicBrowsePlatform.init(rawValue:))
                }
                let mEnabled: Set<MusicPublicBrowsePlatform>? = Self.strArray(pbObj["metadataEnabled"]).map { arr in
                    Set(arr.compactMap(MusicPublicBrowsePlatform.init(rawValue:)))
                }
                _ = try? await service.updateMetadataPreferences(MusicMetadataPreferencesChange(
                    ifRevision: curMeta.revision,
                    platforms: mOrder,
                    enabledPlatforms: mEnabled
                ))
            }
        }

        if let searchObj = Self.dict(block["search"]), let store = assembly.music?.searchPreferences {
            let cur = store.preferences
            let songs = Self.dict(searchObj["songs"]).map { Self.parseSearchKind($0, fallback: cur.songs) } ?? cur.songs
            let playlists = Self.dict(searchObj["playlists"]).map { Self.parseSearchKind($0, fallback: cur.playlists) } ?? cur.playlists
            let searchPlaylists = iosBlock.flatMap({ Self.bool($0["musicSearchPlaylists"]) }) ?? cur.searchPlaylists
            let next = MusicSearchPreferences(
                songPlatforms: songs.platforms,
                searchPlaylists: searchPlaylists,
                playlistPlatforms: playlists.platforms,
                songPages: songs.pages,
                songConcurrency: songs.concurrency,
                songCacheMinutes: songs.cacheMinutes,
                playlistPages: playlists.pages,
                playlistConcurrency: playlists.concurrency,
                playlistCacheMinutes: playlists.cacheMinutes
            )
            _ = await store.update(next)
        }

        if let fbObj = Self.dict(block["feedback"]),
           let fbEnabled = Self.strArray(fbObj["enabled"]),
           let service = assembly.service {
            let targetSet = Set(fbEnabled.compactMap(MusicAccountPlatform.init(rawValue:)))
            for plat in MusicAccountPlatform.allCases {
                guard let current = try? await service.playbackFeedbackSettings() else { break }
                let desired = targetSet.contains(plat)
                if current.platforms[plat]?.enabled == desired { continue }
                _ = try? await service.setPlaybackFeedback(platform: plat, enabled: desired, expectedRevision: current.revision)
            }
        }

        if let syncObj = Self.dict(block["sync"]), let service = assembly.service {
            var selections: [MusicLibrarySyncSelection] = []
            for plat in MusicAccountPlatform.allCases {
                guard let pObj = Self.dict(syncObj[plat.rawValue]) else { continue }
                let fav = Self.bool(pObj["favorites"]) ?? false
                let pl = Self.bool(pObj["playlists"]) ?? false
                if fav || pl {
                    selections.append(MusicLibrarySyncSelection(platform: plat, favorites: fav, playlists: pl))
                }
            }
            try? await service.replaceOfflineSyncPreferences(selections)
        }

        if let scriptsArr = block["scripts"] as? [Any] {
            await replaceMusicNetworkScripts(scriptsArr, resources: resources)
        }
    }

    private func replaceMusicNetworkScripts(_ scriptsArr: [Any], resources: MeowfilmResources) async {
        guard let existing = try? await resources.musicScripts.scriptSources() else { return }
        for item in existing {
            if Task.isCancelled { return }
            try? await resources.musicScripts.removeScriptSource(item.id)
        }
        var seenUrls = Set<String>()
        for raw in scriptsArr {
            if Task.isCancelled { return }
            guard let obj = Self.dict(raw) else { continue }
            let urlText = (Self.str(obj["sourceUrl"]) ?? "").trimmingCharacters(in: .whitespacesAndNewlines)
            guard !urlText.isEmpty,
                  let url = URL(string: urlText),
                  let scheme = url.scheme?.lowercased(), ["http", "https"].contains(scheme),
                  url.host?.isEmpty == false,
                  seenUrls.insert(urlText).inserted else { continue }
            let desiredName = Self.str(obj["name"]).flatMap {
                let t = $0.trimmingCharacters(in: .whitespacesAndNewlines)
                return t.isEmpty ? nil : t
            }
            let enabled = Self.bool(obj["enabled"]) ?? true
            guard let downloaded = try? await resources.musicScripts.downloadScriptSource(from: url) else { continue }
            if let desiredName, desiredName != downloaded.name {
                _ = try? await resources.musicScripts.editScriptSource(downloaded.id, name: desiredName, networkURL: nil)
            }
            if enabled != downloaded.enabled {
                _ = try? await resources.musicScripts.setScriptSourceEnabled(downloaded.id, enabled: enabled)
            }
        }
    }

    private func applyNovelBlock(_ block: [String: Any], assembly: MeowfilmAssembly, resources: MeowfilmResources) async {
        let currentSnap = (try? await resources.novelLibrary.snapshot()) ?? NovelLibrarySnapshot()

        if let readObj = Self.dict(block["reading"]) {
            let cur = currentSnap.settings
            var next = cur
            if let m = Self.str(readObj["mode"]) {
                switch m {
                case "cover": next.mode = .cover
                case "slide": next.mode = .slide
                case "none": next.mode = .instant
                case "scroll": next.mode = .scroll
                default: break
                }
            }
            if let fs = Self.double(readObj["fontSize"]), (12...48).contains(fs) {
                next.fontSize = fs
            }
            if let b = Self.bool(readObj["bold"]) {
                next.fontWeight = b ? 0.35 : 0
            }
            if let ff = Self.str(readObj["fontFamily"]), !ff.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
                next.fontName = ff
            }
            if let ls = Self.double(readObj["lineSpacing"]), ls >= 0 {
                next.lineSpacing = ls
            }
            if let ps = Self.double(readObj["paragraphSpacing"]), ps >= 0 {
                next.paragraphSpacing = ps
            }
            if let mg = Self.double(readObj["margin"]), mg >= 0, (mg * 2).isFinite {
                next.margin = mg
            }
            if let cw = Self.double(readObj["contentWidth"]), cw > 0 {
                next.contentWidth = cw
            }
            if let conv = Self.str(readObj["conversion"]), ["none", "simplified", "traditional"].contains(conv) {
                next.conversion = conv
            }
            if let th = Self.int(readObj["theme"]), NovelReadingSettings.themes.indices.contains(th) {
                next.theme = th
            }
            if let fg = Self.str(readObj["foreground"]), Self.isValidOptionalHexColor(fg) {
                next.foreground = fg
            }
            if let bg = Self.str(readObj["background"]), Self.isValidOptionalHexColor(bg) {
                next.background = bg
            }
            if let bgImg = Self.str(readObj["backgroundImage"]) {
                if bgImg.isEmpty {
                    next.backgroundImage = nil
                } else if let decoded = Data(base64Encoded: bgImg) {
                    next.backgroundImage = decoded
                }
            }
            if let spd = Self.double(readObj["autoSpeed"]), (1.0...10.0).contains(spd) {
                next.automaticSpeed = spd
            }
            if let novel = assembly.novel {
                try? await novel.saveSettings(next)
            } else {
                try? await resources.novelLibrary.saveSettings(next)
            }
        }

        if let rulesArr = block["rules"] as? [Any] {
            var seenIDs = Set<String>()
            var parsedRules: [NovelRule] = []
            for (idx, raw) in rulesArr.enumerated() {
                guard let obj = Self.dict(raw),
                      let pattern = Self.str(obj["pattern"]), !pattern.isEmpty else { continue }
                var rule = NovelRule()
                if let rid = Self.str(obj["id"]), !rid.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty,
                   seenIDs.insert(rid).inserted {
                    rule.id = rid
                } else {
                    let fresh = UUID().uuidString.lowercased()
                    seenIDs.insert(fresh)
                    rule.id = fresh
                }
                rule.name = Self.str(obj["name"]) ?? ""
                rule.group = Self.str(obj["group"]) ?? ""
                rule.pattern = pattern
                rule.replacement = Self.str(obj["replacement"]) ?? ""
                rule.isRegex = Self.bool(obj["isRegex"]) ?? true
                rule.isEnabled = Self.bool(obj["enabled"]) ?? true
                rule.scopeTitle = Self.bool(obj["scopeTitle"]) ?? false
                rule.scopeContent = Self.bool(obj["scopeContent"]) ?? true
                rule.scope = Self.str(obj["scope"]) ?? ""
                rule.excludeScope = Self.str(obj["excludeScope"]) ?? ""
                rule.order = Self.int(obj["order"]) ?? idx
                rule.timeoutMillisecond = Self.int(obj["timeoutMillisecond"]).flatMap { $0 > 0 ? $0 : nil } ?? 3000
                parsedRules.append(rule)
            }
            if let novel = assembly.novel {
                try? await novel.saveRules(parsedRules)
            } else {
                try? await resources.novelLibrary.saveRules(parsedRules)
            }
        }

        let novelClient = NovelSourceClient(runtime: resources.novelRuntime)

        if let sourcesArr = block["sources"] as? [Any] {
            var items: [ExportedNovelSourceItem] = []
            for (idx, raw) in sourcesArr.enumerated() {
                guard let obj = Self.dict(raw), let defVal = obj["definition"] else { continue }
                let defText: String
                if let s = defVal as? String {
                    defText = s
                } else if JSONSerialization.isValidJSONObject(defVal),
                          let bytes = try? JSONSerialization.data(withJSONObject: defVal),
                          let s = String(data: bytes, encoding: .utf8) {
                    defText = s
                } else {
                    defText = ""
                }
                guard !defText.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else { continue }
                let id = Self.str(obj["installationId"]) ?? ""
                let name = Self.str(obj["name"]) ?? ""
                let url = Self.str(obj["sourceUrl"]) ?? ""
                let grp = Self.str(obj["group"]) ?? ""
                let ord = Self.double(obj["order"]) ?? Double(idx)
                let en = Self.bool(obj["enabled"]) ?? true
                let exp = Self.bool(obj["enabledExplore"]) ?? true
                let proto = Self.str(obj["protocol"]) ?? ""
                items.append(ExportedNovelSourceItem(
                    installationId: id,
                    name: name,
                    sourceUrl: url,
                    group: grp,
                    order: ord,
                    enabled: en,
                    enabledExplore: exp,
                    protocol: proto,
                    definition: defText
                ))
            }
            try? await novelClient.replaceAllDefinitions(items)
        }

        if let cookiesArr = block["cookies"] as? [Any],
           JSONSerialization.isValidJSONObject(cookiesArr),
           let encoded = try? JSONSerialization.data(withJSONObject: cookiesArr),
           let runtimeItems = try? RuntimeJSON.decode(encoded) {
            await novelClient.replaceCookies(runtimeItems)
        }

        if let novel = assembly.novel {
            try? await novel.reloadLocal()
            try? await novel.refreshSources()
        }
    }

    // MARK: - Tolerant JSON Helpers

    private static func parseSearchKind(_ obj: [String: Any], fallback: MusicSearchPreferences.Kind) -> MusicSearchPreferences.Kind {
        let platforms: [MusicPublicBrowsePlatform]
        if let enArr = strArray(obj["enabled"]) {
            platforms = normalizeSubset(enArr, allowed: musicFivePlatforms, appendMissing: false)
                .compactMap(MusicPublicBrowsePlatform.init(rawValue:))
        } else {
            platforms = fallback.platforms
        }
        let pages = int(obj["pages"]).flatMap { $0 >= 1 ? $0 : nil } ?? fallback.pages
        let conc = int(obj["concurrency"]).flatMap { $0 >= 1 ? $0 : nil } ?? fallback.concurrency
        let cache = int(obj["cacheMinutes"]).flatMap { (1...1440).contains($0) ? $0 : nil } ?? fallback.cacheMinutes
        return MusicSearchPreferences.Kind(platforms: platforms, pages: pages, concurrency: conc, cacheMinutes: cache)
    }

    private static func encodeMatchRules(_ rules: [FilmMatchRule]) -> [[String: Any]] {
        rules.map { ["pattern": $0.pattern, "replace": $0.replace, "flags": $0.flags] }
    }

    private static func parseMatchRules(_ raw: Any?) -> [FilmMatchRule]? {
        guard let arr = raw as? [Any] else { return nil }
        return arr.compactMap { item -> FilmMatchRule? in
            guard let obj = dict(item), let pat = str(obj["pattern"]),
                  !pat.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else { return nil }
            return FilmMatchRule(pattern: pat, replace: str(obj["replace"]) ?? "", flags: str(obj["flags"]) ?? "")
        }
    }

    private static func normalizeSubset(_ items: [String], allowed: [String], appendMissing: Bool) -> [String] {
        let allowedSet = Set(allowed)
        var result: [String] = []
        for item in items where allowedSet.contains(item) && !result.contains(item) {
            result.append(item)
        }
        if appendMissing {
            for a in allowed where !result.contains(a) {
                result.append(a)
            }
        }
        return result
    }

    private static func isValidOptionalHexColor(_ text: String) -> Bool {
        text.isEmpty || (text.count == 7 && text.first == "#" && UInt64(text.dropFirst(), radix: 16) != nil)
    }

    private static func dict(_ raw: Any?) -> [String: Any]? { raw as? [String: Any] }
    private static func str(_ raw: Any?) -> String? { raw as? String }
    private static func bool(_ raw: Any?) -> Bool? {
        guard let num = raw as? NSNumber, CFGetTypeID(num) == CFBooleanGetTypeID() else { return nil }
        return num.boolValue
    }
    private static func int(_ raw: Any?) -> Int? {
        guard let num = raw as? NSNumber, CFGetTypeID(num) != CFBooleanGetTypeID() else { return nil }
        let d = num.doubleValue
        guard d.isFinite, d.rounded(.towardZero) == d, d >= Double(Int.min), d <= Double(Int.max) else { return nil }
        return Int(d)
    }
    private static func int64(_ raw: Any?) -> Int64? {
        guard let num = raw as? NSNumber, CFGetTypeID(num) != CFBooleanGetTypeID() else { return nil }
        let d = num.doubleValue
        guard d.isFinite, d.rounded(.towardZero) == d, d >= -9_007_199_254_740_991, d <= 9_007_199_254_740_991 else { return nil }
        return Int64(d)
    }
    private static func double(_ raw: Any?) -> Double? {
        guard let num = raw as? NSNumber, CFGetTypeID(num) != CFBooleanGetTypeID() else { return nil }
        let d = num.doubleValue
        return d.isFinite ? d : nil
    }
    private static func strArray(_ raw: Any?) -> [String]? {
        guard let arr = raw as? [Any] else { return nil }
        return arr.compactMap { $0 as? String }
    }
    private static func strDict(_ raw: Any?) -> [String: String] {
        guard let obj = raw as? [String: Any] else { return [:] }
        var out: [String: String] = [:]
        for (k, v) in obj {
            if let s = v as? String { out[k] = s }
        }
        return out
    }
}
