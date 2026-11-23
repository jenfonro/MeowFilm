#if os(iOS)
import UIKit
import MeowContracts
import MeowDebug
import MeowUI

// MARK: - MusicLibraryViewController

extension MusicLibraryViewController: DebugPageInspectable, DebugPageActionable, DebugPageRefreshable {
    public var debugPageStatus: DebugPageStatus {
        DebugPageStatus(
            target: "music.library",
            moduleId: "music",
            route: "music/library",
            title: "我的",
            isBusy: loading || model.isLoading,
            isError: loadError != nil,
            errorText: loadError
        )
    }

    public func debugContent(maxItems: Int, includeRaw: Bool) -> DebugContentSnapshot {
        let localCollections = model.collections.filter { $0.networkReference == nil }
        let networkCollections = model.collections.filter { $0.networkReference != nil }
        func mapItems(_ list: [MusicCollection], limit: Int) -> [DebugContentItem] {
            list.prefix(limit).enumerated().map { idx, c in
                DebugContentItem(
                    index: idx,
                    id: c.id,
                    kind: "playlist",
                    title: c.title,
                    subtitle: "\(c.count) 首",
                    badge: c.kind,
                    coverUrl: c.artwork?.absoluteString,
                    actions: ["open", "play"],
                    meta: ["count": .int(c.count), "kind": .string(c.kind)]
                )
            }
        }
        let localItems = mapItems(localCollections, limit: maxItems)
        let networkItems = mapItems(networkCollections, limit: max(0, maxItems - localItems.count))
        let sections: [DebugContentSection] = [
            DebugContentSection(index: 0, id: "local", title: "我的歌单", kind: "playlist", layout: "list", itemCount: localCollections.count, items: localItems),
            DebugContentSection(index: 1, id: "network", title: "网络歌单", kind: "playlist", layout: "list", itemCount: networkCollections.count, items: networkItems)
        ]
        return DebugContentSnapshot(
            target: "music.library",
            kind: "sections",
            loading: loading || model.isLoading,
            error: loadError,
            sections: sections
        )
    }

    public func debugPerformAction(_ request: DebugActionRequest) async throws -> DebugJSONValue {
        guard request.action == "section.item.invoke" || request.action == "list.item.invoke" else {
            throw DebugAPIError.invalidArgument("Unsupported action '\(request.action)' on MusicLibraryViewController")
        }
        let localCollections = model.collections.filter { $0.networkReference == nil }
        let networkCollections = model.collections.filter { $0.networkReference != nil }
        let collection: MusicCollection
        if let id = (request.params["itemId"] ?? request.params["id"])?.stringValue {
            guard let found = model.collections.first(where: { $0.id == id }) else {
                throw DebugAPIError.notFound("Collection not found: \(id)")
            }
            collection = found
        } else {
            let list: [MusicCollection]
            if let sId = request.params["sectionId"]?.stringValue {
                switch sId {
                case "local": list = localCollections
                case "network": list = networkCollections
                default: throw DebugAPIError.notFound("Unknown library sectionId: \(sId)")
                }
            } else {
                let sIdx = request.params["sectionIndex"]?.intValue ?? 0
                switch sIdx {
                case 0: list = localCollections
                case 1: list = networkCollections
                default: throw DebugAPIError.invalidArgument("Library sectionIndex \(sIdx) out of range")
                }
            }
            let iIdx = (request.params["itemIndex"] ?? request.params["index"])?.intValue ?? 0
            guard list.indices.contains(iIdx) else {
                throw DebugAPIError.invalidArgument("Collection index \(iIdx) out of range")
            }
            collection = list[iIdx]
        }
        let verb = request.params["verb"]?.stringValue ?? "open"
        if verb == "play" {
            await model.play(collection, index: 0)
        } else {
            router?.showLocalCollection(collection)
        }
        return .object(["collectionId": .string(collection.id), "verb": .string(verb)])
    }

    public func debugRefresh() {
        loadLibrary(refresh: true)
    }
}

// MARK: - MusicCollectionViewController

extension MusicCollectionViewController: DebugPageInspectable, DebugPageActionable, DebugPageRefreshable {
    public var debugPageStatus: DebugPageStatus {
        let isDaily = remotePage?.request.dailyPlatform != nil
        return DebugPageStatus(
            target: isDaily ? "music.daily" : "music.playlist",
            moduleId: "music",
            route: isDaily ? "music/daily" : "music/playlist",
            title: pageTitle ?? (isDaily ? "每日推荐" : "歌单"),
            isBusy: remotePage?.isLoading == true || localPage?.isLoading == true,
            isError: remotePage?.error != nil || localPage?.error != nil,
            errorText: remotePage?.error?.localizedDescription ?? localPage?.error?.localizedDescription
        )
    }

    public func debugContent(maxItems: Int, includeRaw: Bool) -> DebugContentSnapshot {
        let projectedRows = orderedRows.prefix(maxItems).enumerated().map { idx, entry -> DebugContentRow in
            let track = entry.row.track
            return DebugContentRow(
                index: idx,
                id: track.songId,
                title: track.title,
                subtitle: [track.artist, track.album].filter { !$0.isEmpty }.joined(separator: " - "),
                selected: model.current?.track.songId == track.songId,
                actions: ["play", "open"],
                meta: ["duration": .number(track.duration)]
            )
        }
        let isDaily = remotePage?.request.dailyPlatform != nil
        let isNeteaseDaily = remotePage?.request.dailyPlatform == .netease
        let dailyBranch: String? = isNeteaseDaily
            ? (remotePage?.request.dailyBranch == .style ? "style" : "default")
            : nil
        let dailyTabs: [DebugTabItem] = isNeteaseDaily ? [
            DebugTabItem(group: "daily-branch", index: 0, id: "default", title: "默认推荐", selected: dailyBranch == "default"),
            DebugTabItem(group: "daily-branch", index: 1, id: "style", title: "风格推荐", selected: dailyBranch == "style")
        ] : []
        let canEditStyle = remotePage.map { canActRemotely($0) && $0.remoteSource?.dailyStyle != nil } ?? false
        let dailyStyleTags = remotePage?.dailyStyle?.tags.map(\.name) ?? []
        var detail: [String: DebugJSONValue] = [
            "title": .string(pageTitle ?? ""),
            "trackCount": .int(orderedRows.count),
            "isLocal": .bool(localPage != nil),
            "isDaily": .bool(isDaily)
        ]
        if let dailyBranch {
            detail["dailyBranch"] = .string(dailyBranch)
            detail["canEditStyle"] = .bool(canEditStyle)
            detail["dailyStyleTags"] = .array(dailyStyleTags.map(DebugJSONValue.string))
        }
        return DebugContentSnapshot(
            target: debugPageStatus.target,
            kind: "list",
            loading: debugPageStatus.isBusy,
            error: debugPageStatus.errorText,
            tabs: dailyTabs,
            rows: projectedRows,
            hasMore: remotePage?.nextCursor != nil,
            detail: .object(detail)
        )
    }

    public func debugPerformAction(_ request: DebugActionRequest) async throws -> DebugJSONValue {
        switch request.action {
        case "tab.select":
            guard let page = remotePage, page.request.dailyPlatform == .netease else {
                throw DebugAPIError.invalidState("Daily branch selection is only available on Netease daily recommendations")
            }
            guard !page.isActing else {
                throw DebugAPIError.invalidState("Daily recommendation page is busy")
            }
            let branch: MusicHomeAction.DailyBranch
            if let idx = request.params["index"]?.intValue {
                switch idx {
                case 0: branch = .default
                case 1: branch = .style
                default: throw DebugAPIError.invalidArgument("Daily branch index \(idx) out of range (0..<2)")
                }
            } else if let id = request.params["id"]?.stringValue {
                switch id.lowercased() {
                case "default", "默认推荐": branch = .default
                case "style", "风格推荐": branch = .style
                default: throw DebugAPIError.notFound("Unknown daily branch: \(id)")
                }
            } else {
                throw DebugAPIError.invalidArgument("Provide 'index' or 'id' for tab.select")
            }
            dailyBranches.selectedSegmentIndex = branch == .style ? 1 : 0
            if page.request.dailyBranch != branch {
                prepareDailyBranchSwitch()
                await page.selectDailyBranch(branch)
            }
            return .object([
                "dailyBranch": .string(branch == .style ? "style" : "default"),
                "canEditStyle": .bool(canActRemotely(page) && page.remoteSource?.dailyStyle != nil)
            ])
        case "daily.style", "style.open":
            guard let page = remotePage, page.request.dailyPlatform == .netease else {
                throw DebugAPIError.invalidState("Daily style editor is only available on Netease daily recommendations")
            }
            guard canPresentPageAlert, canActRemotely(page), page.remoteSource?.dailyStyle != nil else {
                throw DebugAPIError.invalidState("Daily style editor is not currently available")
            }
            openDailyStyle()
            return .object(["opened": .bool(true)])
        case "list.item.invoke":
            let targetIndex: Int
            if let idx = request.params["index"]?.intValue {
                guard orderedRows.indices.contains(idx) else {
                    throw DebugAPIError.invalidArgument("Row index \(idx) out of range (0..<\(orderedRows.count))")
                }
                targetIndex = idx
            } else if let id = request.params["id"]?.stringValue {
                guard let idx = orderedRows.firstIndex(where: { $0.row.track.songId == id }) else {
                    throw DebugAPIError.notFound("Track not found: \(id)")
                }
                targetIndex = idx
            } else {
                throw DebugAPIError.invalidArgument("Provide 'index' or 'id'")
            }
            tableView(table, didSelectRowAt: IndexPath(row: targetIndex, section: 0))
            return .object(["playedIndex": .int(targetIndex), "songId": .string(orderedRows[targetIndex].row.track.songId)])
        case "list.load_more":
            loadNextRemotePage()
            return .object(["triggered": .bool(true)])
        default:
            throw DebugAPIError.invalidArgument("Unsupported action '\(request.action)' on MusicCollectionViewController")
        }
    }

    public func debugRefresh() {
        refresh()
    }
}


// MARK: - MusicSearchViewController & MusicSearchResultsViewController

extension MusicSearchViewController: DebugPageInspectable, DebugPageActionable {
    public func debugSubmitImmediate(query text: String, targetID: String? = nil) {
        if let targetID, !targetID.isEmpty, searchTargets.contains(where: { $0.id == targetID }) {
            searchTargetID = targetID
        }
        let trimmed = text.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty else { return }
        submit(trimmed, animated: false)
    }

    public var debugPageStatus: DebugPageStatus {
        DebugPageStatus(
            target: isMusicTarget ? "music.discovery" : "search",
            moduleId: searchTargetID.isEmpty ? "music" : searchTargetID,
            route: "search/discovery",
            title: "搜索",
            isBusy: isMusicTarget && discoveryPage?.loadingComponents.isEmpty == false
        )
    }

    public func debugContent(maxItems: Int, includeRaw: Bool) -> DebugContentSnapshot {
        let targetTabs = searchTargets.enumerated().map { idx, opt in
            DebugTabItem(group: "search-target", index: idx, id: opt.id, title: opt.title, selected: opt.id == searchTargetID)
        }
        let historyTerms = history.entries.map(\.text)
        let suggestions = isMusicTarget ? (discoveryPage?.suggestions ?? []) : []
        var sections: [DebugContentSection] = []
        if !historyTerms.isEmpty {
            sections.append(DebugContentSection(
                index: sections.count,
                id: "history",
                title: "搜索历史",
                kind: "mixed",
                layout: "list",
                items: historyTerms.prefix(maxItems).enumerated().map { idx, term in
                    DebugContentItem(index: idx, id: term, kind: "action", title: term, actions: ["open"])
                }
            ))
        }
        if isMusicTarget, !suggestions.isEmpty {
            sections.append(DebugContentSection(
                index: sections.count,
                id: "suggestions",
                title: "热搜推荐",
                kind: "mixed",
                layout: "list",
                items: suggestions.prefix(maxItems).enumerated().map { idx, term in
                    DebugContentItem(index: idx, id: term, kind: "action", title: term, actions: ["open"])
                }
            ))
        }
        return DebugContentSnapshot(
            target: debugPageStatus.target,
            kind: "search",
            loading: isMusicTarget && discoveryPage?.loadingComponents.isEmpty == false,
            tabs: targetTabs,
            sections: sections,
            extra: ["query": .string(query), "searchTarget": .string(searchTargetID)]
        )
    }

    public func debugPerformAction(_ request: DebugActionRequest) async throws -> DebugJSONValue {
        switch request.action {
        case "search.submit":
            guard let keyword = request.params["query"]?.stringValue, !keyword.isEmpty else {
                throw DebugAPIError.invalidArgument("Missing 'query' for search.submit")
            }
            debugSubmitImmediate(query: keyword, targetID: request.params["target"]?.stringValue)
            return .object(["submitted": .string(keyword), "target": .string(searchTargetID)])
        case "tab.select":
            if let id = request.params["id"]?.stringValue, searchTargets.contains(where: { $0.id == id }) {
                searchTargetID = id
                return .object(["target": .string(searchTargetID)])
            }
            if let idx = request.params["index"]?.intValue, searchTargets.indices.contains(idx) {
                searchTargetID = searchTargets[idx].id
                return .object(["target": .string(searchTargetID)])
            }
            throw DebugAPIError.invalidArgument("Invalid search target selection")
        case "section.item.invoke":
            let historyTerms = history.entries.map(\.text)
            let suggestions = isMusicTarget ? (discoveryPage?.suggestions ?? []) : []
            let sId = request.params["sectionId"]?.stringValue
            let sIdx = request.params["sectionIndex"]?.intValue ?? 0
            let iIdx = request.params["itemIndex"]?.intValue ?? 0
            let useSuggestions = isMusicTarget && (sId == "suggestions" || (sId == nil && historyTerms.isEmpty) || sIdx == 1)
            let list = useSuggestions ? suggestions : historyTerms
            guard list.indices.contains(iIdx) else { throw DebugAPIError.invalidArgument("Item index out of range") }
            submit(list[iIdx], animated: false)
            return .object(["submitted": .string(list[iIdx])])
        default:
            throw DebugAPIError.invalidArgument("Unsupported action '\(request.action)' on MusicSearchViewController")
        }
    }
}

extension MusicSearchResultsViewController: DebugPageInspectable, DebugPageActionable {
    public func debugSelectTab(_ tab: String) -> Bool {
        let lower = tab.lowercased()
        for kind in kinds {
            switch kind {
            case .all where lower == "all" || lower == "综合": select(kind, animated: false); return true
            case .songs where lower == "songs" || lower == "song" || lower == "单曲": select(kind, animated: false); return true
            case .playlists where lower == "playlists" || lower == "playlist" || lower == "歌单": select(kind, animated: false); return true
            case .additional(let id) where lower == id.lowercased(): select(kind, animated: false); return true
            default: break
            }
        }
        return false
    }

    public var debugPageStatus: DebugPageStatus {
        let busy: Bool
        switch selected {
        case .additional(let id):
            busy = (additionalContents[id] as? DebugPageInspectable)?.debugPageStatus.isBusy ?? false
        default:
            busy = results.songsLoading || results.playlistsLoading
        }
        return DebugPageStatus(
            target: "search",
            moduleId: searchTargetID.isEmpty ? "music" : searchTargetID,
            route: "search/results",
            title: submittedKeyword,
            isBusy: busy
        )
    }

    public func debugContent(maxItems: Int, includeRaw: Bool) -> DebugContentSnapshot {
        let tabs = kinds.enumerated().map { idx, kind -> DebugTabItem in
            let (id, title): (String, String)
            switch kind {
            case .all: (id, title) = ("all", "综合")
            case .songs: (id, title) = ("songs", "单曲")
            case .playlists: (id, title) = ("playlists", "歌单")
            case .additional(let raw):
                let name = additionalCategories.first(where: { $0.target.id == raw })?.target.title ?? raw
                (id, title) = (raw, name)
            }
            return DebugTabItem(group: "search-tab", index: idx, id: id, title: title, selected: kind == selected)
        }
        if case .additional(let id) = selected,
           let inspectable = additionalContents[id] as? DebugPageInspectable {
            var snap = inspectable.debugContent(maxItems: maxItems, includeRaw: includeRaw)
            snap.target = "search"
            snap.kind = "search"
            snap.tabs = tabs
            snap.extra["query"] = .string(submittedKeyword)
            snap.extra["searchTarget"] = .string(id)
            return snap
        }
        let projectedRows: [DebugContentRow]
        if selected == .playlists {
            projectedRows = renderedPlaylists.prefix(maxItems).enumerated().map { idx, pl in
                DebugContentRow(index: idx, id: pl.nativeID, title: pl.title, subtitle: pl.creator, actions: ["open"])
            }
        } else {
            projectedRows = renderedSongs.prefix(maxItems).enumerated().map { idx, song in
                DebugContentRow(
                    index: idx,
                    id: song.track.songId,
                    title: song.track.title,
                    subtitle: [song.track.artist, song.track.album].filter { !$0.isEmpty }.joined(separator: " - "),
                    actions: ["play", "open"],
                    meta: ["platforms": .array(song.matchedPlatforms.map(DebugJSONValue.string))]
                )
            }
        }
        return DebugContentSnapshot(
            target: "search",
            kind: "search",
            loading: results.songsLoading || results.playlistsLoading,
            tabs: tabs,
            rows: projectedRows,
            extra: ["query": .string(submittedKeyword), "searchTarget": .string(searchTargetID)]
        )
    }

    public func debugPerformAction(_ request: DebugActionRequest) async throws -> DebugJSONValue {
        switch request.action {
        case "search.submit":
            guard let keyword = request.params["query"]?.stringValue, !keyword.isEmpty else {
                throw DebugAPIError.invalidArgument("Missing 'query'")
            }
            debugSubmit(query: keyword, targetID: request.params["target"]?.stringValue)
            if let tab = request.params["tab"]?.stringValue { _ = debugSelectTab(tab) }
            return .object(["submitted": .string(keyword), "target": .string(searchTargetID)])
        case "tab.select":
            if let id = request.params["id"]?.stringValue {
                guard debugSelectTab(id) else { throw DebugAPIError.notFound("Search tab not found: \(id)") }
            } else if let idx = request.params["index"]?.intValue, kinds.indices.contains(idx) {
                select(kinds[idx], animated: false)
            } else {
                throw DebugAPIError.invalidArgument("Invalid search tab selection")
            }
            return .object(["selectedTarget": .string(searchTargetID)])
        case "list.item.invoke", "list.load_more":
            if case .additional(let id) = selected,
               let actionable = additionalContents[id] as? DebugPageActionable {
                return try await actionable.debugPerformAction(request)
            }
            if request.action == "list.load_more" {
                if selected == .playlists { results.more(.playlists) } else { results.more(.songs) }
                return .object(["triggered": .bool(true)])
            }
            guard let idx = request.params["index"]?.intValue else {
                throw DebugAPIError.invalidArgument("Missing 'index' for list.item.invoke")
            }
            if selected == .playlists {
                guard renderedPlaylists.indices.contains(idx) else { throw DebugAPIError.invalidArgument("Playlist index out of range") }
                router?.showPublicCollection(renderedPlaylists[idx])
                return .object(["openedPlaylist": .string(renderedPlaylists[idx].nativeID)])
            } else {
                guard renderedSongs.indices.contains(idx) else { throw DebugAPIError.invalidArgument("Song index out of range") }
                await model.play(renderedSongs[idx].track)
                return .object(["playedSongId": .string(renderedSongs[idx].track.songId)])
            }
        default:
            throw DebugAPIError.invalidArgument("Unsupported action '\(request.action)' on MusicSearchResultsViewController")
        }
    }
}

#endif
