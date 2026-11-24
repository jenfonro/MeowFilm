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
        let metrics = DebugUIMetrics.shared.snapshot()
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
            "renderPlaybackCount": metrics["film.detail.renderPlayback"] ?? .int(0),
            "applyThemeCount": metrics["film.detail.applyTheme"] ?? .int(0)
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
#endif
