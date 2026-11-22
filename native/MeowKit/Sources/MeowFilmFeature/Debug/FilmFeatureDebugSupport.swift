#if os(iOS)
import UIKit
import MeowContracts
import MeowDebug
import MeowUI

// MARK: - FilmPlaybackModel Debug State & Control

@MainActor
public extension FilmPlaybackModel {
    func debugPlaybackState() -> DebugJSONValue {
        let stateStr: String
        switch playbackStatus {
        case .idle: stateStr = "stopped"
        case .loading, .searching, .preparing, .prepared, .buffering: stateStr = "buffering"
        case .playing: stateStr = "playing"
        case .paused: stateStr = "paused"
        case .ended: stateStr = "stopped"
        case .unavailable, .failed: stateStr = "error"
        }
        let currentCandidate = snapshot?.current?.candidate ?? snapshot?.attempt?.candidate
        let titleStr = snapshot?.workInfo?.title ?? snapshot?.target.title ?? ""
        return .object([
            "state": .string(stateStr),
            "stage": .optional(snapshot?.stage),
            "engine": .string(String(describing: mediaState.engine)),
            "title": .string(titleStr),
            "source": .optional(currentCandidate?.source.name),
            "episode": .optional(currentCandidate?.name),
            "line": .optional(currentCandidate?.lineLabel),
            "positionSec": .number(mediaState.position),
            "durationSec": .number(mediaState.duration),
            "bufferedSec": .number(mediaState.position),
            "paused": .bool(mediaState.paused),
            "buffering": .bool(mediaState.buffering),
            "rate": .number(mediaState.rate),
            "volume": .number(mediaState.volume),
            "pictureInPicture": .bool(mediaState.pictureInPicture),
            "videoSize": .null,
            "audioTracks": .array([]),
            "subtitleTracks": .array([]),
            "selectedAudioTrack": .null,
            "selectedSubtitleTrack": .null,
            "mediaUrl": .optional(currentCandidate?.path),
            "sourcesCount": .int(snapshot?.sources.count ?? 0),
            "episodesCount": .int(snapshot?.episodes.count ?? 0),
            "candidatesCount": .int(snapshot?.candidates.count ?? 0),
            "lastError": .optional(actionError?.code ?? observationError?.code ?? snapshot?.error)
        ])
    }

    func debugPerformPlaybackCommand(
        _ command: String,
        params: [String: DebugJSONValue]
    ) async throws -> DebugJSONValue {
        DebugRuntime.log(.filmPlayback, sub: "debug.command", event: command, data: .object(params))
        switch command {
        case "play":
            if mediaState.paused { pauseOrResume() }
        case "pause":
            if !mediaState.paused { pauseOrResume() }
        case "toggle":
            pauseOrResume()
        case "stop":
            _ = send("stop")
        case "next":
            _ = navigateFromSystem(forward: true)
        case "previous", "prev":
            _ = navigateFromSystem(forward: false)
        case "seek":
            guard let seconds = params["seconds"]?.doubleValue else {
                throw DebugAPIError.invalidArgument("Missing numeric 'seconds' for seek")
            }
            seek(to: seconds)
        case "rate":
            guard let rate = params["rate"]?.doubleValue else {
                throw DebugAPIError.invalidArgument("Missing numeric 'rate'")
            }
            setRate(rate)
        case "select_episode":
            let episodes = snapshot?.episodes ?? []
            let targetEp: FilmPlaybackEpisode
            if let idx = params["index"]?.intValue {
                guard episodes.indices.contains(idx) else {
                    throw DebugAPIError.invalidArgument("Episode index \(idx) out of range (0..<\(episodes.count))")
                }
                targetEp = episodes[idx]
            } else if let id = params["id"]?.stringValue {
                guard let found = episodes.first(where: { $0.displayID == id || $0.name == id }) else {
                    throw DebugAPIError.notFound("Episode not found: \(id)")
                }
                targetEp = found
            } else {
                throw DebugAPIError.invalidArgument("Provide 'index' or 'id' for select_episode")
            }
            selectTarget(targetEp.playbackTarget)
        case "select_source":
            let sources = snapshot?.sources ?? []
            let chosen: FilmPlaybackSourceRow
            if let idx = params["index"]?.intValue {
                guard sources.indices.contains(idx) else {
                    throw DebugAPIError.invalidArgument("Film source index \(idx) out of range (0..<\(sources.count))")
                }
                chosen = sources[idx]
            } else if let id = params["id"]?.stringValue {
                guard let found = sources.first(where: { $0.id == id || $0.title == id || $0.source.key == id }) else {
                    throw DebugAPIError.notFound("Film source not found: \(id)")
                }
                chosen = found
            } else {
                throw DebugAPIError.invalidArgument("Provide 'index' or 'id' for select_source")
            }
            browse(sourceID: chosen.id)
        case "select_audio_track", "select_subtitle_track":
            throw DebugAPIError.unsupported("iOS FilmPlaybackModel does not expose independent audio/subtitle track switching yet")
        default:
            throw DebugAPIError.invalidArgument("Unsupported film playback command: \(command)")
        }
        return debugPlaybackState()
    }
}

// MARK: - FilmHomeViewController

extension FilmHomeViewController: DebugPageInspectable, DebugPageActionable, DebugPageRefreshable {
    public func debugFindItem(id: String) -> (item: FilmItem, category: FilmCategory)? {
        for sec in sections {
            if let found = sec.items.first(where: { $0.id == id || $0.businessID == id }) {
                let cat = model.categoryModel(sectionID: sec.id)?.category ?? .movie
                return (found, cat)
            }
        }
        return nil
    }

    public var debugPageStatus: DebugPageStatus {
        DebugPageStatus(
            target: "film.home",
            moduleId: "film",
            route: "film/home",
            title: "影视",
            isBusy: model.isLoading,
            isError: model.error != nil,
            errorText: model.error?.localizedDescription
        )
    }

    public func debugContent(maxItems: Int, includeRaw: Bool) -> DebugContentSnapshot {
        var remaining = maxItems
        let projectedSections = sections.enumerated().map { sIdx, sec -> DebugContentSection in
            let take = min(remaining, sec.items.count)
            remaining = max(0, remaining - take)
            let items = sec.items.prefix(take).enumerated().map { iIdx, item in
                DebugContentItem(
                    index: iIdx,
                    id: item.businessID ?? item.id,
                    kind: "film",
                    title: item.title,
                    subtitle: item.subtitle.isEmpty ? nil : item.subtitle,
                    badge: item.score.isEmpty ? nil : item.score,
                    coverUrl: item.posterURL?.absoluteString,
                    actions: ["open"],
                    meta: ["sourceId": .string(item.sourceID), "itemId": .string(item.id)]
                )
            }
            return DebugContentSection(
                index: sIdx,
                id: sec.id,
                title: sec.title,
                kind: "film",
                layout: "rail",
                hasMore: model.supportsCategory(sectionID: sec.id),
                itemCount: sec.items.count,
                items: items
            )
        }
        return DebugContentSnapshot(
            target: "film.home",
            kind: sections.isEmpty ? "empty" : "sections",
            loading: model.isLoading,
            error: model.error?.localizedDescription,
            sections: projectedSections
        )
    }

    public func debugPerformAction(_ request: DebugActionRequest) async throws -> DebugJSONValue {
        switch request.action {
        case "section.item.invoke":
            let sec = try resolveSection(request.params)
            let item: FilmItem
            if let iIdx = request.params["itemIndex"]?.intValue {
                guard sec.items.indices.contains(iIdx) else {
                    throw DebugAPIError.invalidArgument("itemIndex \(iIdx) out of range")
                }
                item = sec.items[iIdx]
            } else if let id = request.params["itemId"]?.stringValue {
                guard let found = sec.items.first(where: { $0.id == id || $0.businessID == id }) else {
                    throw DebugAPIError.notFound("Film item not found: \(id)")
                }
                item = found
            } else {
                throw DebugAPIError.invalidArgument("Provide 'itemIndex' or 'itemId'")
            }
            let category = model.categoryModel(sectionID: sec.id)?.category
            if let onSelectPlaybackItem {
                onSelectPlaybackItem(item, category)
            } else {
                onSelectItem?(item)
            }
            return .object(["openedFilmId": .string(item.id), "title": .string(item.title)])
        case "section.more":
            let sec = try resolveSection(request.params)
            guard let catModel = model.categoryModel(sectionID: sec.id) else {
                throw DebugAPIError.invalidState("Section does not have a category view")
            }
            onSelectCategory?(catModel)
            return .object(["openedCategory": .string(catModel.category.rawValue)])
        default:
            throw DebugAPIError.invalidArgument("Unsupported action '\(request.action)' on FilmHomeViewController")
        }
    }

    private func resolveSection(_ params: [String: DebugJSONValue]) throws -> FilmHomeSection {
        if let sId = params["sectionId"]?.stringValue {
            guard let found = sections.first(where: { $0.id == sId }) else {
                throw DebugAPIError.notFound("Film section not found: \(sId)")
            }
            return found
        }
        let sIdx = params["sectionIndex"]?.intValue ?? 0
        guard sections.indices.contains(sIdx) else {
            throw DebugAPIError.invalidArgument("sectionIndex \(sIdx) out of range (0..<\(sections.count))")
        }
        return sections[sIdx]
    }

    public func debugRefresh() {
        _ = model.refresh()
    }

    public func debugCategoryModel(category: String?) -> FilmCategoryModel? {
        if let category, !category.isEmpty {
            if let direct = model.categoryModel(sectionID: category) { return direct }
            for sec in sections where sec.id == category || sec.title == category {
                if let found = model.categoryModel(sectionID: sec.id) { return found }
            }
            return nil
        }
        for candidate in ["movie", "tv", "show", "anime"] {
            if let direct = model.categoryModel(sectionID: candidate) { return direct }
        }
        for sec in sections {
            if let found = model.categoryModel(sectionID: sec.id) { return found }
        }
        return nil
    }
}

// MARK: - FilmCategoryViewController

extension FilmCategoryViewController: DebugPageInspectable, DebugPageActionable, DebugPageRefreshable {
    public var debugPageStatus: DebugPageStatus {
        DebugPageStatus(
            target: "film.data",
            moduleId: "film",
            route: "film/data/\(model.category.rawValue)",
            title: model.category.title,
            isBusy: model.isLoading,
            isError: model.error != nil,
            errorText: model.error?.localizedDescription
        )
    }

    public func debugContent(maxItems: Int, includeRaw: Bool) -> DebugContentSnapshot {
        var filterItems: [DebugFilterItem] = []
        for row in model.filterRows {
            let selectedVal = model.filters[row.id] ?? ""
            for (idx, opt) in row.options.enumerated() {
                filterItems.append(DebugFilterItem(
                    group: row.id,
                    index: idx,
                    id: opt.id,
                    title: opt.title,
                    selected: opt.id == selectedVal
                ))
            }
        }
        let items = renderedItems.prefix(maxItems).enumerated().map { idx, item in
            DebugContentItem(
                index: idx,
                id: item.businessID ?? item.id,
                kind: "film",
                title: item.title,
                subtitle: item.subtitle.isEmpty ? nil : item.subtitle,
                badge: item.score.isEmpty ? nil : item.score,
                coverUrl: item.posterURL?.absoluteString,
                actions: ["open"]
            )
        }
        let sec = DebugContentSection(
            index: 0,
            id: model.category.rawValue,
            title: model.category.title,
            kind: "film",
            layout: "grid",
            hasMore: model.hasMore,
            itemCount: renderedItems.count,
            items: items
        )
        return DebugContentSnapshot(
            target: "film.data",
            kind: "sections",
            loading: model.isLoading,
            error: model.error?.localizedDescription,
            filters: filterItems,
            sections: [sec],
            hasMore: model.hasMore
        )
    }

    public func debugPerformAction(_ request: DebugActionRequest) async throws -> DebugJSONValue {
        switch request.action {
        case "section.item.invoke", "list.item.invoke":
            let idx = (request.params["itemIndex"] ?? request.params["index"])?.intValue ?? 0
            guard renderedItems.indices.contains(idx) else {
                throw DebugAPIError.invalidArgument("Film item index \(idx) out of range")
            }
            let item = renderedItems[idx]
            onSelectItem?(item)
            return .object(["openedFilmId": .string(item.id), "title": .string(item.title)])
        case "tab.select":
            let group = request.params["group"]?.stringValue ?? model.filterRows.first?.id ?? "category"
            guard let row = model.filterRows.first(where: { $0.id == group }) else {
                throw DebugAPIError.notFound("Filter group not found: \(group)")
            }
            let value: String
            if let id = request.params["id"]?.stringValue {
                guard row.options.contains(where: { $0.id == id }) else {
                    throw DebugAPIError.notFound("Filter option not found in group '\(group)': \(id)")
                }
                value = id
            } else if let idx = request.params["index"]?.intValue, row.options.indices.contains(idx) {
                value = row.options[idx].id
            } else {
                throw DebugAPIError.invalidArgument("Provide 'id' or 'index'")
            }
            _ = model.selectFilter(group, value: value)
            return .object(["group": .string(group), "selected": .string(value)])
        case "list.load_more":
            _ = model.loadMore()
            return .object(["triggered": .bool(true)])
        default:
            throw DebugAPIError.invalidArgument("Unsupported action '\(request.action)' on FilmCategoryViewController")
        }
    }

    public func debugRefresh() {
        _ = model.refresh()
    }
}

// MARK: - FilmDetailViewController, FilmDirectoryViewController, FilmPlayerViewController

extension FilmDetailViewController: DebugPageInspectable, DebugPageActionable, DebugPageRefreshable {
    public var debugPageStatus: DebugPageStatus {
        DebugPageStatus(
            target: "film.detail",
            moduleId: "film",
            route: "film/detail/\(originalDetail.item.id)",
            title: model?.snapshot?.workInfo?.title ?? originalDetail.item.title,
            isBusy: model?.playbackStatus.busy == true || model?.opening == true,
            statusText: model?.playbackStatus.title,
            isError: model?.actionError != nil || model?.observationError != nil,
            errorText: model?.actionError?.code ?? model?.observationError?.code
        )
    }

    public func debugContent(maxItems: Int, includeRaw: Bool) -> DebugContentSnapshot {
        let snap = model?.presentation ?? model?.snapshot
        let sources = (snap?.sources ?? []).prefix(maxItems).enumerated().map { idx, s -> DebugJSONValue in
            .object(["index": .int(idx), "id": .string(s.id), "title": .string(s.title), "state": .string(s.state), "error": .optional(s.error)])
        }
        let episodes = (snap?.episodes ?? []).prefix(maxItems).enumerated().map { idx, ep -> DebugContentRow in
            DebugContentRow(
                index: idx,
                id: ep.displayID,
                title: ep.name ?? "第\(ep.position.episode ?? (idx + 1))集",
                selected: snap?.target.id == ep.playbackTarget.id,
                actions: ["play", "select"]
            )
        }
        let detailJSON: DebugJSONValue = .object([
            "id": .string(originalDetail.item.id),
            "title": .string(snap?.workInfo?.title ?? originalDetail.item.title),
            "stage": .optional(snap?.stage),
            "searchComplete": .bool(snap?.searchComplete ?? false),
            "sources": .array(sources),
            "episodesCount": .int(snap?.episodes.count ?? 0),
            "candidatesCount": .int(snap?.candidates.count ?? 0),
            "episodeListVisible": .bool(episodeListVisible),
            "virtualizedEpisodeItems": .int(episodeCollection.currentItems.count),
            "visibleEpisodeCells": .int(episodeCollection.collectionView.visibleCells.count),
            "renderPlaybackCount": .int(debugRenderPlaybackCount),
            "applyThemeCount": .int(debugApplyThemeCount)
        ])
        return DebugContentSnapshot(
            target: "film.detail",
            kind: "detail",
            loading: debugPageStatus.isBusy,
            error: debugPageStatus.errorText,
            rows: episodes,
            detail: detailJSON
        )
    }

    public func debugPerformAction(_ request: DebugActionRequest) async throws -> DebugJSONValue {
        switch request.action {
        case "list.item.invoke":
            guard let model else { throw DebugAPIError.invalidState("Film playback model unavailable") }
            let episodes = (model.presentation ?? model.snapshot)?.episodes ?? []
            let idx = request.params["index"]?.intValue ?? 0
            guard episodes.indices.contains(idx) else {
                throw DebugAPIError.invalidArgument("Episode index \(idx) out of range")
            }
            model.selectTarget(episodes[idx].playbackTarget)
            if (request.params["verb"]?.stringValue ?? "play") == "play" {
                onPresentPlayback?()
            }
            return .object(["selectedEpisodeIndex": .int(idx)])
        case "tab.select":
            guard let model else { throw DebugAPIError.invalidState("Film playback model unavailable") }
            let sources = (model.presentation ?? model.snapshot)?.sources ?? []
            if let idx = request.params["index"]?.intValue, sources.indices.contains(idx) {
                model.browse(sourceID: sources[idx].id)
                return .object(["selectedSourceId": .string(sources[idx].id)])
            }
            if let id = request.params["id"]?.stringValue {
                guard sources.contains(where: { $0.id == id }) else {
                    throw DebugAPIError.notFound("Film source not found: \(id)")
                }
                model.browse(sourceID: id)
                return .object(["selectedSourceId": .string(id)])
            }
            throw DebugAPIError.invalidArgument("Provide 'index' or 'id' for source selection")
        case "episode.layout":
            let listMode = request.params["list"]?.boolValue ?? !episodeListVisible
            episodeListVisible = listMode
            renderSeason()
            return .object(["episodeListVisible": .bool(episodeListVisible)])
        default:
            throw DebugAPIError.invalidArgument("Unsupported action '\(request.action)' on FilmDetailViewController")
        }
    }

    public func debugRefresh() {
        _ = model?.refreshBrowsing()
    }

    public func debugOpenDirectory() throws {
        guard let model, let browsing = model.presentation?.browsing, browsing.error == nil,
              browsing.sourceID != nil, !browsing.originalFiles.isEmpty else {
            throw DebugAPIError.invalidState("Film directory is unavailable for the current detail")
        }
        showDirectory(animated: false)
    }
}

extension FilmDirectoryViewController: DebugPageInspectable, DebugPageRefreshable {
    public var debugPageStatus: DebugPageStatus {
        DebugPageStatus(
            target: "film.data",
            moduleId: "film",
            route: "film/directory",
            title: pageTitle ?? "原始目录",
            isBusy: model.presentation?.browsing.loading == true,
            isError: model.actionError != nil || model.observationError != nil,
            errorText: model.actionError?.code ?? model.observationError?.code
        )
    }

    public func debugContent(maxItems: Int, includeRaw: Bool) -> DebugContentSnapshot {
        let entries = displayedEntries ?? []
        let rows = entries.prefix(maxItems).enumerated().map { idx, entry -> DebugContentRow in
            switch entry {
            case .folder(let name):
                return DebugContentRow(index: idx, id: "folder:\(name)", title: name, subtitle: "目录", actions: ["open"])
            case .file(let file):
                return DebugContentRow(index: idx, id: file.id, title: file.name, subtitle: file.quality ?? file.resolution, actions: ["open", "play"])
            }
        }
        return DebugContentSnapshot(
            target: "film.data",
            kind: "list",
            loading: debugPageStatus.isBusy,
            error: debugPageStatus.errorText,
            rows: rows
        )
    }

    public func debugRefresh() {
        _ = model.refreshBrowsing()
    }
}

extension FilmPlayerViewController: DebugPageInspectable {
    public var debugPageStatus: DebugPageStatus {
        DebugPageStatus(
            target: "film.playback",
            moduleId: "film",
            route: "film/playback",
            title: model.snapshot?.workInfo?.title ?? "视频播放",
            isBusy: model.playbackStatus.busy,
            statusText: model.playbackStatus.title,
            isError: model.actionError != nil || model.observationError != nil,
            errorText: model.actionError?.code ?? model.observationError?.code
        )
    }

    public func debugContent(maxItems: Int, includeRaw: Bool) -> DebugContentSnapshot {
        DebugContentSnapshot(
            target: "film.playback",
            kind: "player",
            loading: model.playbackStatus.busy,
            error: debugPageStatus.errorText,
            player: model.debugPlaybackState()
        )
    }
}

// MARK: - FilmSearchResultsContent

extension FilmSearchResultsContent: DebugPageInspectable, DebugPageActionable {
    public var debugPageStatus: DebugPageStatus {
        DebugPageStatus(
            target: "search",
            moduleId: "film",
            route: "search/film",
            title: "影视搜索",
            isBusy: session?.snapshot.loading == true,
            isError: session?.error != nil,
            errorText: session?.error?.code
        )
    }

    public func debugContent(maxItems: Int, includeRaw: Bool) -> DebugContentSnapshot {
        let rows = presentation.identifiers.prefix(maxItems).enumerated().compactMap { idx, id -> DebugContentRow? in
            guard let card = presentation.cards[id] else { return nil }
            return DebugContentRow(
                index: idx,
                id: card.selection.businessID,
                title: card.content.item.title,
                subtitle: card.content.label.isEmpty ? nil : card.content.label,
                actions: ["open"]
            )
        }
        return DebugContentSnapshot(
            target: "search",
            kind: "search",
            loading: session?.snapshot.loading == true,
            error: session?.error?.code,
            rows: rows,
            hasMore: session?.snapshot.hasMore == true
        )
    }

    public func debugPerformAction(_ request: DebugActionRequest) async throws -> DebugJSONValue {
        switch request.action {
        case "list.item.invoke":
            guard let session else { throw DebugAPIError.invalidState("No active film search session") }
            let idx = request.params["index"]?.intValue ?? 0
            guard presentation.identifiers.indices.contains(idx),
                  let card = presentation.cards[presentation.identifiers[idx]] else {
                throw DebugAPIError.invalidArgument("Film search index \(idx) out of range")
            }
            onSelect?(session.context(for: card.selection))
            return .object(["openedFilmId": .string(card.selection.businessID)])
        case "list.load_more":
            session?.loadMore()
            return .object(["triggered": .bool(true)])
        default:
            throw DebugAPIError.invalidArgument("Unsupported action '\(request.action)' on FilmSearchResultsContent")
        }
    }
}

// MARK: - Film Settings Controllers

extension FilmSettingsViewController: DebugPageInspectable, DebugPageActionable {
    public func debugOpenSection(_ section: String) throws {
        let page: UIViewController
        switch section {
        case "search-rules":
            page = FilmRulePageViewController(owner: model.rules, group: .search, themeProvider: themeProvider)
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
                "sections": .array(["rules", "search-rules", "cats", "videos", "metadata", "pan", "pan-routing", "preferences", "preferences-detail", "site-clean", "auto-prepare", "player", "cache"].map(DebugJSONValue.string)),
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
        let sec = group == .search ? "search-rules" : "rules"
        return DebugPageStatus(
            target: "settings.film",
            moduleId: "film",
            route: "settings/film/\(sec)",
            title: pageTitle ?? "规则设置",
            settingsDomain: "film",
            settingsSection: sec
        )
    }
    public func debugContent(maxItems: Int, includeRaw: Bool) -> DebugContentSnapshot {
        let sec = group == .search ? "search-rules" : "rules"
        return DebugContentSnapshot(
            target: "settings.film",
            kind: "settings",
            rows: debugContentRows(maxItems: maxItems),
            settings: .object(["domain": .string("film"), "section": .string(sec)])
        )
    }
    public func debugPerformAction(_ request: DebugActionRequest) async throws -> DebugJSONValue {
        if request.action == "list.item.invoke" { return try debugInvokeRow(params: request.params) }
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
        DebugPageStatus(target: "settings.film", moduleId: "film", route: "settings/film/videos", title: pageTitle ?? "视频源管理", settingsDomain: "film", settingsSection: "videos")
    }
    public func debugContent(maxItems: Int, includeRaw: Bool) -> DebugContentSnapshot {
        DebugContentSnapshot(target: "settings.film", kind: "settings", rows: debugContentRows(maxItems: maxItems), settings: .object(["domain": .string("film"), "section": .string("videos")]))
    }
    public func debugPerformAction(_ request: DebugActionRequest) async throws -> DebugJSONValue {
        if request.action == "list.item.invoke" { return try debugInvokeRow(params: request.params) }
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
        return DebugContentSnapshot(
            target: "settings.film",
            kind: "settings",
            rows: debugContentRows(maxItems: maxItems),
            settings: .object(["domain": .string("film"), "section": .string("player")])
        )
    }
    public func debugPerformAction(_ request: DebugActionRequest) async throws -> DebugJSONValue {
        if request.action == "list.item.invoke" { return try debugInvokeRow(params: request.params) }
        throw DebugAPIError.invalidArgument("Unsupported action '\(request.action)' on FilmPlayerSettingsController")
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
        return DebugContentSnapshot(
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
