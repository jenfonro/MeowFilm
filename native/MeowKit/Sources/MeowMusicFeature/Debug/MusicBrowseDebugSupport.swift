#if os(iOS)
import UIKit
import MeowDebug
import MeowUI

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
        let hasExplicitSelection = renderedFilters.contains(where: \.selected)
        let filterItems = renderedFilters.enumerated().map { idx, filter in
            DebugFilterItem(
                group: filter.group.isEmpty ? "filter" : filter.group,
                index: idx,
                id: filter.id,
                title: filter.title,
                selected: hasExplicitSelection ? filter.selected : (filter.id == renderedFilter)
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
            let matchingGroup = group == "filter"
                ? renderedFilters
                : renderedFilters.filter { $0.group == group }
            if group == "filter" || !matchingGroup.isEmpty {
                guard let page else { throw DebugAPIError.invalidState("Browse page has no view model") }
                let pool = matchingGroup.isEmpty ? renderedFilters : matchingGroup
                let targetFilter: MusicPublicBrowseFilter
                if let idx = request.params["index"]?.intValue {
                    guard pool.indices.contains(idx) else {
                        throw DebugAPIError.invalidArgument("Filter index \(idx) out of range")
                    }
                    targetFilter = pool[idx]
                } else if let id = request.params["id"]?.stringValue {
                    guard let found = pool.first(where: { $0.id == id || $0.title == id }) else {
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
        case "tags.toggle":
            let rowIndex = request.params["rowIndex"]?.intValue ?? 0
            tags.toggleRowExpansion(at: rowIndex)
            return .object(["expanded": .bool(tags.isExpanded), "rowIndex": .int(rowIndex)])
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


// MARK: - MusicChannelsPickerViewController & MusicDailyStyleViewController

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
