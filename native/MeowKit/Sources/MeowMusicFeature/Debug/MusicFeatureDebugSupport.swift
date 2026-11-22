#if os(iOS)
import UIKit
import MeowContracts
import MeowDebug
import MeowPlayback
import MeowUI

// MARK: - MusicViewModel Debug Playback Control & State

@MainActor
public extension MusicViewModel {
    func debugPlaybackState(playback: PlaybackController) -> DebugJSONValue {
        let currentEntry = current
        let currentIndex = currentEntry.flatMap { entry in queue.firstIndex(where: { $0.id == entry.id }) }
        let selection = activePlaybackSelection
        let phaseString: String
        switch playback.snapshot.phase {
        case .idle: phaseString = "stopped"
        case .preparing, .buffering: phaseString = "buffering"
        case .playing: phaseString = playback.isOutputPlaying || playback.wantsPlayback ? "playing" : "paused"
        case .paused: phaseString = "paused"
        case .ended: phaseString = "stopped"
        case .failed: phaseString = "error"
        }
        let lyricText: String? = currentLyricIndex.flatMap { idx in
            lyricLines.indices.contains(idx) ? lyricLines[idx].text : nil
        }
        let trackJSON: DebugJSONValue
        if let track = currentEntry?.track {
            trackJSON = .object([
                "occurrenceId": .string(currentEntry?.id ?? ""),
                "songId": .string(track.songId),
                "title": .string(track.title),
                "artist": .string(track.artist),
                "album": .string(track.album),
                "duration": .number(track.duration),
                "artworkUrl": .optional(track.artwork?.absoluteString)
            ])
        } else {
            trackJSON = .null
        }
        let sourceJSON: DebugJSONValue = selection.map { sel in
            .object([
                "installationId": .string(sel.installationId),
                "siteId": .string(sel.siteId),
                "sourceKey": .string(sel.sourceKey),
                "quality": .string(sel.quality)
            ])
        } ?? .null

        return .object([
            "state": .string(phaseString),
            "track": trackJSON,
            "source": sourceJSON,
            "quality": .optional(selection?.quality ?? state.requestQuality),
            "positionSec": .number(playback.snapshot.owner == "music" ? playback.currentPosition : 0),
            "durationSec": .optional(duration ?? currentEntry?.track.duration),
            "bufferedSec": .number(Double(playback.debugSnapshot()["audio"]?["bufferedFrames"]?.intValue ?? 0) / 48_000.0),
            "mode": .string(state.mode.title),
            "modeRaw": .int(state.mode.rawValue),
            "queueIndex": .optional(currentIndex),
            "queueCount": .int(queue.count),
            "lyricLine": .optional(lyricText),
            "lyricCount": .int(lyricLines.count),
            "sourceTitle": .string(playbackSourceTitle),
            "isFMActive": .bool(isFMActive),
            "availableOptionsCount": .int(options.count),
            "lastError": .optional(error ?? (playback.snapshot.owner == "music" ? playback.snapshot.error : nil))
        ])
    }

    func debugPerformPlaybackCommand(
        _ command: String,
        params: [String: DebugJSONValue],
        playback: PlaybackController
    ) async throws -> DebugJSONValue {
        DebugRuntime.log(.musicPlayback, sub: "debug.command", event: command, data: .object(params))
        switch command {
        case "play":
            if playback.snapshot.owner == "music", playback.isOutputPlaying || playback.wantsPlayback {
                return debugPlaybackState(playback: playback)
            }
            if current == nil, let first = queue.first {
                await selectOccurrence(first.id)
            } else {
                await togglePlayback()
            }
        case "pause":
            if playback.snapshot.owner == "music", playback.wantsPlayback || playback.isOutputPlaying {
                await togglePlayback()
            }
        case "toggle":
            await togglePlayback()
        case "stop":
            stopForMediaHandoff()
        case "next":
            await next()
        case "previous", "prev":
            await previous()
        case "seek":
            guard let seconds = params["seconds"]?.doubleValue else {
                throw DebugAPIError.invalidArgument("Missing numeric 'seconds' for seek")
            }
            guard seek(to: seconds) else {
                throw DebugAPIError.invalidState("Music playback is not seekable right now")
            }
        case "mode":
            let mode: MusicPlaybackMode
            if let raw = params["mode"]?.intValue, let m = MusicPlaybackMode(rawValue: raw) {
                mode = m
            } else if let str = params["mode"]?.stringValue?.lowercased() {
                switch str {
                case "loop", "list", "列表循环": mode = .loop
                case "shuffle", "random", "随机播放": mode = .shuffle
                case "single", "repeat_one", "单曲循环": mode = .single
                default: throw DebugAPIError.invalidArgument("Unknown music playback mode: \(str)")
                }
            } else {
                throw DebugAPIError.invalidArgument("Missing 'mode' parameter")
            }
            await setMode(mode)
        case "play_queue_index":
            guard let index = params["index"]?.intValue else {
                throw DebugAPIError.invalidArgument("Missing 'index' for play_queue_index")
            }
            guard queue.indices.contains(index) else {
                throw DebugAPIError.invalidArgument("Queue index \(index) out of range (0..<\(queue.count))")
            }
            await selectOccurrence(queue[index].id)
        case "select_source":
            let chosen: MusicPlaybackOption
            if let idx = params["index"]?.intValue {
                guard options.indices.contains(idx) else {
                    throw DebugAPIError.invalidArgument("Source option index \(idx) out of range")
                }
                chosen = options[idx]
            } else if let id = params["id"]?.stringValue {
                guard let found = options.first(where: { $0.id == id || $0.sourceKey == id }) else {
                    throw DebugAPIError.notFound("Music source option not found: \(id)")
                }
                chosen = found
            } else {
                throw DebugAPIError.invalidArgument("Provide 'index' or 'id' for select_source")
            }
            let quality = params["quality"]?.stringValue ?? chosen.qualities.first ?? state.requestQuality
            let selection = MusicPlaybackSelection(
                installationId: chosen.installationId,
                generation: chosen.generation,
                siteId: chosen.siteId,
                sourceKey: chosen.sourceKey,
                quality: quality
            )
            await choose(selection)
        case "start_fm":
            let modeStr = params["mode"]?.stringValue ?? "DEFAULT"
            let fmMode = MusicHomeAction.FMMode(rawValue: modeStr) ?? .default
            let submode = params["submode"]?.stringValue ?? ""
            try await playHomeAction(.fm(mode: fmMode, submode: submode, entry: nil), title: "私人漫游", forceReload: false)
        default:
            throw DebugAPIError.invalidArgument("Unsupported music playback command: \(command)")
        }
        return debugPlaybackState(playback: playback)
    }

    func debugSettingsJSON(appearance: MusicAppearancePreferencesStore) -> DebugJSONValue {
        .object([
            "requestQuality": .string(state.requestQuality),
            "qualityEnabled": .array(state.qualityEnabled.map(DebugJSONValue.string)),
            "qualityOrder": .array(state.qualityOrder.map(DebugJSONValue.string)),
            "spatialEnabled": .array(state.spatialEnabled.map(DebugJSONValue.string)),
            "spatialOrder": .array(state.spatialOrder.map(DebugJSONValue.string)),
            "lyricsEnabled": .bool(state.lyricsEnabled),
            "showTranslation": .bool(state.showTranslation),
            "officialSources": .array(state.officialSources.map { .string($0.rawValue) }),
            "lockLyric": .bool(appearance.preferences.lockLyric),
            "colorMode": .string(appearance.preferences.colorMode.rawValue),
            "playlistView": .string(appearance.preferences.playlistView.rawValue),
            "mode": .string(state.mode.title),
            "platformsCount": .int(platforms.count),
            "collectionsCount": .int(collections.count),
            "scriptSourceError": .optional(scriptSourceError)
        ])
    }
}

// MARK: - MusicBrowseViewController

extension MusicBrowseViewController: DebugPageInspectable, DebugPageActionable, DebugPageRefreshable {
    public var debugPageStatus: DebugPageStatus {
        let isHome = page?.request == .discovery(.home) || embedded
        return DebugPageStatus(
            target: isHome ? "music.home" : "music.browse",
            moduleId: "music",
            route: isHome ? "music/home" : "music/browse",
            title: pageTitle ?? (isHome ? "首页" : "推荐"),
            isBusy: page?.isLoading == true,
            statusText: page?.notice,
            isError: page?.error != nil || page?.actionError != nil,
            errorText: page?.error?.localizedDescription ?? page?.actionError?.localizedDescription
        )
    }

    public func debugContent(maxItems: Int, includeRaw: Bool) -> DebugContentSnapshot {
        let filterItems = renderedFilters.enumerated().map { idx, filter in
            DebugFilterItem(
                group: "filter",
                index: idx,
                id: filter.id,
                title: filter.title,
                selected: filter.id == renderedFilter
            )
        }
        var remaining = maxItems
        let projectedSections = sections.enumerated().map { sIdx, section -> DebugContentSection in
            let take = min(remaining, section.items.count)
            remaining = max(0, remaining - take)
            let items = section.items.prefix(take).enumerated().map { iIdx, item -> DebugContentItem in
                let kindStr = item.kind?.rawValue ?? Self.debugKind(for: item.content)
                var actions = ["open"]
                switch item.content {
                case .track, .action: actions.append("play")
                case .collection: actions.append("play")
                default: break
                }
                var meta: [String: DebugJSONValue] = [
                    "requiresAccount": .bool(item.requiresAccount)
                ]
                if !item.recommendationReason.isEmpty { meta["reason"] = .string(item.recommendationReason) }
                if let playCount = item.playCount { meta["playCount"] = .int(playCount) }
                return DebugContentItem(
                    index: iIdx,
                    id: item.id,
                    kind: kindStr,
                    title: item.title,
                    subtitle: item.subtitle.isEmpty ? nil : item.subtitle,
                    badge: item.coverLabel.isEmpty ? (item.label.isEmpty ? nil : item.label) : item.coverLabel,
                    coverUrl: item.artwork?.absoluteString,
                    actions: actions,
                    meta: meta
                )
            }
            return DebugContentSection(
                index: sIdx,
                id: section.id,
                title: section.title,
                kind: section.items.first?.kind?.rawValue ?? "mixed",
                layout: Self.debugLayout(for: section.layout),
                hasMore: section.channel != nil,
                itemCount: section.items.count,
                items: items
            )
        }
        var sectionTabs: [DebugTabItem] = []
        for (sIdx, section) in sections.enumerated() where !section.tabs.isEmpty {
            for (tIdx, tab) in section.tabs.enumerated() {
                sectionTabs.append(DebugTabItem(
                    group: "section-tab:\(sIdx)",
                    index: tIdx,
                    id: tab.id,
                    title: tab.title,
                    selected: tab.id == section.selectedTab
                ))
            }
        }
        return DebugContentSnapshot(
            target: debugPageStatus.target,
            kind: sections.isEmpty ? "empty" : "sections",
            loading: page?.isLoading == true,
            error: page?.error?.localizedDescription,
            tabs: sectionTabs,
            filters: filterItems,
            sections: projectedSections,
            hasMore: page?.nextCursor != nil
        )
    }

    public func debugPerformAction(_ request: DebugActionRequest) async throws -> DebugJSONValue {
        switch request.action {
        case "section.item.invoke":
            let section = try resolveSection(request.params)
            let item = try resolveItem(request.params, in: section)
            let verb = request.params["verb"]?.stringValue ?? "open"
            if verb == "play" {
                play(item, section: section)
            } else {
                open(item, section: section)
            }
            return .object(["invoked": .bool(true), "sectionId": .string(section.id), "itemId": .string(item.id), "verb": .string(verb)])
        case "section.more":
            let section = try resolveSection(request.params)
            guard let channel = section.channel else {
                throw DebugAPIError.invalidState("Section '\(section.title)' has no 'more' channel")
            }
            router?.selectChannel(channel)
            return .object(["openedChannel": .string(channel.identity)])
        case "tab.select":
            let group = request.params["group"]?.stringValue ?? "filter"
            if group == "filter" {
                guard let page else { throw DebugAPIError.invalidState("Browse page has no view model") }
                let targetFilter: MusicPublicBrowseFilter
                if let idx = request.params["index"]?.intValue {
                    guard renderedFilters.indices.contains(idx) else {
                        throw DebugAPIError.invalidArgument("Filter index \(idx) out of range")
                    }
                    targetFilter = renderedFilters[idx]
                } else if let id = request.params["id"]?.stringValue {
                    guard let found = renderedFilters.first(where: { $0.id == id || $0.title == id }) else {
                        throw DebugAPIError.notFound("Filter not found: \(id)")
                    }
                    targetFilter = found
                } else {
                    throw DebugAPIError.invalidArgument("Provide 'index' or 'id' for tab.select")
                }
                await page.selectFilter(targetFilter.id)
                return .object(["selectedFilter": .string(targetFilter.id)])
            } else if group.hasPrefix("section-tab:") {
                let idxStr = String(group.dropFirst("section-tab:".count))
                guard let sIdx = Int(idxStr), sections.indices.contains(sIdx) else {
                    throw DebugAPIError.invalidArgument("Invalid section-tab group: \(group)")
                }
                let section = sections[sIdx]
                let tabId: String
                if let tIdx = request.params["index"]?.intValue {
                    guard section.tabs.indices.contains(tIdx) else {
                        throw DebugAPIError.invalidArgument("Section tab index out of range")
                    }
                    tabId = section.tabs[tIdx].id
                } else if let id = request.params["id"]?.stringValue {
                    tabId = id
                } else {
                    throw DebugAPIError.invalidArgument("Provide 'index' or 'id'")
                }
                selectChoice(tabId, section: section)
                return .object(["selectedSectionTab": .string(tabId)])
            }
            throw DebugAPIError.invalidArgument("Unsupported tab group on MusicBrowseViewController: \(group)")
        case "list.load_more":
            guard let page else { throw DebugAPIError.invalidState("Browse page unavailable") }
            await page.appendNext()
            return .object(["loaded": .bool(true)])
        default:
            throw DebugAPIError.invalidArgument("Unsupported action '\(request.action)' on MusicBrowseViewController")
        }
    }

    public func debugRefresh() {
        guard let page else { return }
        runPageTask { await page.refresh() }
    }

    private func resolveSection(_ params: [String: DebugJSONValue]) throws -> MusicPublicBrowseSection {
        if let sIdx = params["sectionIndex"]?.intValue {
            guard sections.indices.contains(sIdx) else {
                throw DebugAPIError.invalidArgument("sectionIndex \(sIdx) out of range (0..<\(sections.count))")
            }
            return sections[sIdx]
        }
        if let sId = params["sectionId"]?.stringValue {
            guard let found = sections.first(where: { $0.id == sId || $0.title == sId }) else {
                throw DebugAPIError.notFound("Section not found: \(sId)")
            }
            return found
        }
        guard let first = sections.first else {
            throw DebugAPIError.invalidState("No sections available")
        }
        return first
    }

    private func resolveItem(_ params: [String: DebugJSONValue], in section: MusicPublicBrowseSection) throws -> MusicPublicBrowseItem {
        if let iIdx = params["itemIndex"]?.intValue {
            guard section.items.indices.contains(iIdx) else {
                throw DebugAPIError.invalidArgument("itemIndex \(iIdx) out of range (0..<\(section.items.count))")
            }
            return section.items[iIdx]
        }
        if let iId = params["itemId"]?.stringValue {
            guard let found = section.items.first(where: { $0.id == iId }) else {
                throw DebugAPIError.notFound("Item not found in section '\(section.id)': \(iId)")
            }
            return found
        }
        throw DebugAPIError.invalidArgument("Provide 'itemIndex' or 'itemId'")
    }

    private static func debugKind(for content: MusicPublicBrowseContent) -> String {
        switch content {
        case .collection(let c): return c.kind.rawValue
        case .track: return "song"
        case .channel: return "channel"
        case .daily: return "daily"
        case .action: return "action"
        case .unavailable: return "action"
        }
    }

    private static func debugLayout(for layout: MusicPublicBrowseSection.Layout) -> String {
        switch layout {
        case .banner: return "banner"
        case .grid, .artistGrid, .channelGrid: return "grid"
        case .list, .rankingStack, .rankedSongs, .rankingSmall, .vipSongs, .vipRankedSongs, .albumRanking, .artistUpdates: return "list"
        case .featureRail, .playlistRail, .songColumns: return "rail"
        }
    }
}

// MARK: - MusicChannelsViewController

extension MusicChannelsViewController: DebugPageInspectable, DebugPageActionable, DebugPageRefreshable {
    public var debugActiveChannelPage: MusicBrowseViewController? {
        if let current = pager.viewControllers?.first as? MusicBrowseViewController {
            return current
        }
        if let id = selection.channel?.identity { return pages[id] }
        return nil
    }

    public func debugSelectChannel(key: String?, index: Int?) throws {
        if let idx = index {
            guard channels.indices.contains(idx) else {
                throw DebugAPIError.invalidArgument("channelIndex \(idx) out of range (0..<\(channels.count))")
            }
            select(channels[idx])
            return
        }
        if let key, !key.isEmpty {
            guard let found = channels.first(where: { $0.identity == key || $0.id == key || $0.title == key }) else {
                throw DebugAPIError.notFound("Music channel not found: \(key)")
            }
            select(found)
        }
    }

    public var debugPageStatus: DebugPageStatus {
        let child = debugActiveChannelPage?.debugPageStatus
        return DebugPageStatus(
            target: "music.channels",
            moduleId: "music",
            route: "music/channels" + (selection.channel.map { "/\($0.identity)" } ?? ""),
            title: selection.channel?.title ?? "频道",
            isBusy: (catalogue?.isLoading == true && channels.isEmpty) || (child?.isBusy == true),
            statusText: child?.statusText,
            isError: catalogue?.error != nil || (child?.isError == true),
            errorText: catalogue?.error?.localizedDescription ?? child?.errorText
        )
    }

    public func debugContent(maxItems: Int, includeRaw: Bool) -> DebugContentSnapshot {
        let channelTabs = channels.enumerated().map { idx, ch in
            DebugTabItem(
                group: "channel",
                index: idx,
                id: ch.identity,
                title: ch.title,
                selected: ch.identity == selection.channel?.identity
            )
        }
        var snapshot = debugActiveChannelPage?.debugContent(maxItems: maxItems, includeRaw: includeRaw)
            ?? DebugContentSnapshot(target: "music.channels", kind: "sections", loading: catalogue?.isLoading == true)
        snapshot.target = "music.channels"
        snapshot.tabs = channelTabs + snapshot.tabs
        return snapshot
    }

    public func debugPerformAction(_ request: DebugActionRequest) async throws -> DebugJSONValue {
        if request.action == "channel.picker" {
            showPicker()
            return .object(["pickerPresented": .bool(true)])
        }
        if request.action == "tab.select", (request.params["group"]?.stringValue ?? "channel") == "channel" {
            try debugSelectChannel(key: request.params["id"]?.stringValue, index: request.params["index"]?.intValue)
            return .object(["selectedChannel": .optional(selection.channel?.identity)])
        }
        guard let child = debugActiveChannelPage else {
            throw DebugAPIError.invalidState("No active channel browse page loaded yet")
        }
        return try await child.debugPerformAction(request)
    }

    public func debugRefresh() {
        if let child = debugActiveChannelPage {
            child.debugRefresh()
        } else if let catalogue {
            runPageTask { await catalogue.refresh() }
        }
    }
}

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

// MARK: - MusicPlayerViewController

extension MusicPlayerViewController: DebugPageInspectable {
    public var debugPageStatus: DebugPageStatus {
        DebugPageStatus(
            target: "music.player",
            moduleId: "music",
            route: "music/player",
            title: content.model.current?.track.title ?? "播放器",
            isBusy: content.model.isResolving,
            isError: content.model.error != nil,
            errorText: content.model.error
        )
    }

    public func debugContent(maxItems: Int, includeRaw: Bool) -> DebugContentSnapshot {
        let playerState = content.model.debugPlaybackState(playback: content.playback)
        return DebugContentSnapshot(
            target: "music.player",
            kind: "player",
            loading: content.model.isResolving,
            error: content.model.error,
            player: playerState
        )
    }
}

// MARK: - MusicQueueViewController

extension MusicQueueViewController: DebugPageInspectable, DebugPageActionable {
    public func debugSelectTab(_ tabId: String) {
        loadViewIfNeeded()
        switch tabId {
        case "current", "queue":
            pager.select("current", animated: false)
        case "songs", "history":
            pager.select("songs", animated: false)
        case "playlists", "archives":
            if let first = model.playedPlaylists.first {
                pager.select("history:" + first.id, animated: false)
            } else {
                pager.select("songs", animated: false)
            }
        default:
            pager.select(tabId, animated: false)
        }
    }

    public var debugPageStatus: DebugPageStatus {
        let target: String
        if selected == "songs" { target = "music.history" }
        else if selected.hasPrefix("history:") { target = "music.archives" }
        else { target = "music.queue" }
        return DebugPageStatus(
            target: target,
            moduleId: "music",
            route: "music/queue/\(selected)",
            title: "播放队列",
            isBusy: model.isChangingQueue,
            isError: model.error != nil,
            errorText: model.error
        )
    }

    public func debugContent(maxItems: Int, includeRaw: Bool) -> DebugContentSnapshot {
        let tabs: [DebugTabItem] = [
            DebugTabItem(group: "queue-tab", index: 0, id: "current", title: "当前播放", selected: selected == "current"),
            DebugTabItem(group: "queue-tab", index: 1, id: "songs", title: "历史单曲", selected: selected == "songs"),
            DebugTabItem(group: "queue-tab", index: 2, id: "playlists", title: "历史歌单", selected: selected.hasPrefix("history:"))
        ]
        let projectedRows: [DebugContentRow]
        if selected == "songs" {
            projectedRows = model.playedSongs.prefix(maxItems).enumerated().map { idx, row in
                DebugContentRow(
                    index: idx,
                    id: row.id,
                    title: row.track.title,
                    subtitle: "\(row.track.artist) · \(row.date)",
                    actions: ["play"]
                )
            }
        } else if selected.hasPrefix("history:") {
            projectedRows = model.playedPlaylists.prefix(maxItems).enumerated().map { idx, pl in
                DebugContentRow(
                    index: idx,
                    id: pl.id,
                    title: pl.title,
                    selected: selected == "history:" + pl.id,
                    actions: ["open", "play"]
                )
            }
        } else {
            projectedRows = entries.prefix(maxItems).enumerated().map { idx, entry in
                DebugContentRow(
                    index: idx,
                    id: entry.id,
                    title: entry.track.title,
                    subtitle: entry.track.artist,
                    selected: entry.id == model.current?.id,
                    actions: ["play", "delete"]
                )
            }
        }
        return DebugContentSnapshot(
            target: debugPageStatus.target,
            kind: "list",
            loading: model.isChangingQueue,
            error: model.error,
            tabs: tabs,
            rows: projectedRows
        )
    }

    public func debugPerformAction(_ request: DebugActionRequest) async throws -> DebugJSONValue {
        switch request.action {
        case "tab.select":
            if let id = request.params["id"]?.stringValue {
                debugSelectTab(id)
            } else if let idx = request.params["index"]?.intValue {
                let ids = ["current", "songs", "playlists"]
                guard ids.indices.contains(idx) else { throw DebugAPIError.invalidArgument("Queue tab index out of range") }
                debugSelectTab(ids[idx])
            } else {
                throw DebugAPIError.invalidArgument("Provide 'id' or 'index'")
            }
            return .object(["selected": .string(selected)])
        case "list.item.invoke":
            let verb = request.params["verb"]?.stringValue ?? "play"
            if selected == "songs" {
                guard let idx = request.params["index"]?.intValue, model.playedSongs.indices.contains(idx) else {
                    throw DebugAPIError.invalidArgument("Invalid played song index")
                }
                await model.playHistorySong(model.playedSongs[idx])
                return .object(["playedHistorySongIndex": .int(idx)])
            } else if selected.hasPrefix("history:") {
                guard let idx = request.params["index"]?.intValue, model.playedPlaylists.indices.contains(idx) else {
                    throw DebugAPIError.invalidArgument("Invalid played playlist index")
                }
                await model.restoreHistory(model.playedPlaylists[idx])
                return .object(["restoredPlaylistId": .string(model.playedPlaylists[idx].id)])
            } else {
                guard let idx = request.params["index"]?.intValue, entries.indices.contains(idx) else {
                    throw DebugAPIError.invalidArgument("Invalid queue index")
                }
                let entry = entries[idx]
                if verb == "delete" {
                    await model.removeOccurrence(entry.id)
                } else {
                    await model.selectOccurrence(entry.id)
                }
                return .object(["occurrenceId": .string(entry.id), "verb": .string(verb)])
            }
        default:
            throw DebugAPIError.invalidArgument("Unsupported action '\(request.action)' on MusicQueueViewController")
        }
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
                "available": .bool(state.available),
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
        DebugContentSnapshot(target: "settings.music", kind: "settings", rows: debugContentRows(maxItems: maxItems), settings: .object(["domain": .string("music"), "section": .string("search")]))
    }
    public func debugPerformAction(_ request: DebugActionRequest) async throws -> DebugJSONValue {
        if request.action == "list.item.invoke" { return try debugInvokeRow(params: request.params) }
        throw DebugAPIError.invalidArgument("Unsupported action '\(request.action)' on MusicSearchSettingsViewController")
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

extension MusicFMSettingsViewController: DebugPageInspectable, DebugPageActionable {
    public var debugPageStatus: DebugPageStatus {
        DebugPageStatus(
            target: "music.fm",
            moduleId: "music",
            route: "music/fm/settings",
            title: "私人漫游",
            isBusy: model.pendingChoice != nil,
            isError: model.error != nil,
            errorText: model.error
        )
    }
    public func debugContent(maxItems: Int, includeRaw: Bool) -> DebugContentSnapshot {
        let rows = (MusicFMModeChoice.main + MusicFMModeChoice.scenes).prefix(maxItems).enumerated().map { idx, choice in
            DebugContentRow(
                index: idx,
                id: choice.id,
                title: choice.title,
                selected: choice.matches(model.selection),
                actions: ["select"]
            )
        }
        return DebugContentSnapshot(
            target: "music.fm",
            kind: "list",
            loading: model.pendingChoice != nil,
            error: model.error,
            rows: rows
        )
    }
    public func debugPerformAction(_ request: DebugActionRequest) async throws -> DebugJSONValue {
        guard request.action == "list.item.invoke" || request.action == "section.item.invoke" else {
            throw DebugAPIError.invalidArgument("Unsupported action '\(request.action)' on MusicFMSettingsViewController")
        }
        let choices = MusicFMModeChoice.main + MusicFMModeChoice.scenes
        let choice: MusicFMModeChoice
        if let idx = (request.params["index"] ?? request.params["itemIndex"])?.intValue {
            guard choices.indices.contains(idx) else {
                throw DebugAPIError.invalidArgument("FM choice index \(idx) out of range (0..<\(choices.count))")
            }
            choice = choices[idx]
        } else if let id = (request.params["id"] ?? request.params["itemId"])?.stringValue {
            guard let found = choices.first(where: { $0.id == id || $0.submode == id || $0.title == id }) else {
                throw DebugAPIError.notFound("FM choice not found: \(id)")
            }
            choice = found
        } else {
            throw DebugAPIError.invalidArgument("Provide 'index' or 'id' for \(request.action)")
        }
        model.select(choice)
        return .object(["choiceId": .string(choice.id), "title": .string(choice.title)])
    }
}

extension MusicChannelsPickerViewController: DebugPageInspectable {
    public var debugPageStatus: DebugPageStatus {
        DebugPageStatus(
            target: "music.channels",
            moduleId: "music",
            route: "music/channels/picker",
            title: "全部频道"
        )
    }
    public func debugContent(maxItems: Int, includeRaw: Bool) -> DebugContentSnapshot {
        let rows = channels.prefix(maxItems).enumerated().map { idx, ch in
            DebugContentRow(
                index: idx,
                id: ch.identity,
                title: ch.title,
                selected: ch.identity == selectedID
            )
        }
        return DebugContentSnapshot(target: "music.channels", kind: "list", rows: rows)
    }
}

extension MusicDailyStyleViewController: DebugPageInspectable, DebugPageActionable {
    public var debugPageStatus: DebugPageStatus {
        DebugPageStatus(
            target: "music.daily",
            moduleId: "music",
            route: "music/daily/style",
            title: "风格推荐",
            isBusy: model.working,
            isError: model.error != nil,
            errorText: model.error
        )
    }
    public func debugContent(maxItems: Int, includeRaw: Bool) -> DebugContentSnapshot {
        let tabs = model.categories.enumerated().map { idx, cat in
            DebugTabItem(group: "category", index: idx, id: cat.id, title: cat.name, selected: cat.id == model.category)
        }
        let rows = (model.selectedCategory?.tags ?? []).prefix(maxItems).enumerated().map { idx, tag in
            DebugContentRow(
                index: idx,
                id: tag.id,
                title: tag.name,
                selected: model.selected.contains(tag.id),
                actions: ["toggle"]
            )
        }
        return DebugContentSnapshot(
            target: "music.daily",
            kind: "list",
            loading: model.working,
            error: model.error,
            tabs: tabs,
            rows: rows,
            extra: [
                "canSave": .bool(model.canSave),
                "canRetry": .bool(model.canRetry),
                "selectedCount": .int(model.selected.count)
            ]
        )
    }
    public func debugPerformAction(_ request: DebugActionRequest) async throws -> DebugJSONValue {
        switch request.action {
        case "tab.select":
            let categories = model.categories
            let target: MusicDailyStyleCategory
            if let idx = request.params["index"]?.intValue {
                guard categories.indices.contains(idx) else {
                    throw DebugAPIError.invalidArgument("Category index \(idx) out of range (0..<\(categories.count))")
                }
                target = categories[idx]
            } else if let id = request.params["id"]?.stringValue {
                guard let found = categories.first(where: { $0.id == id || $0.name == id }) else {
                    throw DebugAPIError.notFound("Daily style category not found: \(id)")
                }
                target = found
            } else {
                throw DebugAPIError.invalidArgument("Provide 'index' or 'id' for tab.select")
            }
            model.selectCategory(target.id)
            return .object(["categoryId": .string(target.id), "name": .string(target.name)])
        case "list.item.invoke", "section.item.invoke":
            let tags = model.selectedCategory?.tags ?? []
            let tag = try { () throws -> MusicDailyStyleTag in
                if let idx = (request.params["index"] ?? request.params["itemIndex"])?.intValue {
                    guard tags.indices.contains(idx) else {
                        throw DebugAPIError.invalidArgument("Tag index \(idx) out of range (0..<\(tags.count))")
                    }
                    return tags[idx]
                } else if let id = (request.params["id"] ?? request.params["itemId"])?.stringValue {
                    guard let found = tags.first(where: { $0.id == id || $0.name == id }) else {
                        throw DebugAPIError.notFound("Daily style tag not found: \(id)")
                    }
                    return found
                } else {
                    throw DebugAPIError.invalidArgument("Provide 'index' or 'id' for \(request.action)")
                }
            }()
            model.selectTag(tag.id)
            return .object([
                "tagId": .string(tag.id),
                "name": .string(tag.name),
                "selected": .bool(model.selected.contains(tag.id))
            ])
        case "form.submit", "daily.save":
            guard model.canSave else {
                throw DebugAPIError.invalidState("Daily style selection cannot be saved right now")
            }
            model.save()
            await model.finishPending()
            return .object(["saved": .bool(model.saved), "error": .optional(model.error)])
        case "retry":
            guard model.canRetry else {
                throw DebugAPIError.invalidState("Daily style editor cannot retry right now")
            }
            model.retry()
            await model.finishPending()
            return .object(["retried": .bool(true), "categoriesCount": .int(model.categories.count)])
        default:
            throw DebugAPIError.invalidArgument("Unsupported action '\(request.action)' on MusicDailyStyleViewController")
        }
    }
}
#endif
