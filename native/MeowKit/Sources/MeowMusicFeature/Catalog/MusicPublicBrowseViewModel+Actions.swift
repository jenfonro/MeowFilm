import Foundation
import MeowDebug

@MainActor
extension MusicPublicBrowseViewModel {
    public func selectFilter(_ id: String) async {
        DebugRuntime.log(.musicCatalog, sub: "browse", event: "filter.select", data: .object([
            "filterId": .string(id)
        ]))
        guard !closed, visible, !isActing, selectedFilter != id,
              filters.contains(where: { $0.id == id }), case .discovery(.channel(let channel, _)) = request else { return }
        request = .discovery(.channel(channel, tag: id))
        // Keep the directory and previous pixels while revoking all old actions.
        // Only the completed new response can make its content actionable.
        invalidate(clear: false)
        loadedPages = [:]
        contentIsCurrent = false
        selectedFilter = id
        nextCursor = nil; currentCursor = nil; back = []; cursorOffsets = ["": 0]
        await load(cursor: nil, direction: 0, refresh: true)
    }

    public func selectSectionFilter(_ id: String, sectionID: String) async {
        guard !closed, visible, contentIsCurrent, !isLoading, !isActing,
              let section = sections.first(where: { $0.id == sectionID }),
              section.filters.contains(where: { $0.id == id }), section.selectedFilter != id,
              case .discovery(.channel(let channel, _)) = request else { return }
        let candidate = MusicPublicBrowseRequest.discovery(.channel(channel, tag: id))
        invalidate(clear: false)
        loadedPages = [:]
        await load(cursor: nil, direction: 0, refresh: true, requestOverride: candidate)
    }

    public func selectSectionTab(_ id: String, sectionID: String) async {
        guard !closed, visible, contentIsCurrent, !isLoading, !isActing,
              let index = sections.firstIndex(where: { $0.id == sectionID }),
              let tab = sections[index].tabs.first(where: { $0.id == id }),
              sections[index].selectedTab != id else { return }
        let old = sections[index]
        pageEpoch = UUID()
        let own = pageEpoch, readEpoch = loadEpoch
        tabTask?.cancel()
        var items = tab.items
        let trace = MusicRequestTrace(.browse, platform: requestPlatform)
        defer { if pageEpoch == own { tabTask = nil } }
        do {
            if let load = tab.loadItems {
                let work = Task { try await MusicRequestTrace.$current.withValue(trace) { try await load() } }
                tabTask = work
                items = try await withTaskCancellationHandler { try await work.value } onCancel: { work.cancel() }
            }
            guard !closed, visible, pageEpoch == own, loadEpoch == readEpoch, !Task.isCancelled else { return }
            let tabs = old.tabs.map { value in
                value.id == id ? MusicPublicBrowseSectionTab(id: value.id, title: value.title, items: items) : value
            }
            sections[index] = .init(id: old.id, title: old.title, items: items, layout: old.layout,
                avatars: old.avatars, channel: old.channel, filters: old.filters,
                selectedFilter: old.selectedFilter, tabs: tabs, selectedTab: id)
            let key = currentCursor ?? ""
            if var cached = loadedPages[key], let slot = cached.page.sections.firstIndex(where: { $0.id == sectionID }) {
                var retained = cached.page.sections; retained[slot] = sections[index]
                cached.page = .init(sections: retained, cursor: cached.page.cursor, filters: cached.page.filters,
                    selectedFilter: cached.page.selectedFilter, notice: cached.page.notice)
                loadedPages[key] = cached
            }
            sectionDiagnosticIDs[sectionID] = nil
            publishArtwork()
            await refreshSongProjections(service)
        } catch {
            guard !closed, visible, pageEpoch == own, loadEpoch == readEpoch, !Task.isCancelled else { return }
            let failure = MusicServiceError.sanitized(error)
            guard failure != .cancelled else { return }
            trace.failed(error); sectionDiagnosticIDs[sectionID] = trace.logicalID
            sections[index] = .init(id: old.id, title: old.title, items: old.items, layout: old.layout,
                avatars: old.avatars, channel: old.channel, filters: old.filters,
                selectedFilter: old.selectedFilter, error: failure, tabs: old.tabs, selectedTab: old.selectedTab)
        }
    }

    public func selectDailyBranch(_ branch: MusicHomeAction.DailyBranch) async {
        guard !closed, visible, !isActing, request.dailyPlatform == .netease,
              request.dailyBranch != branch else { return }
        let title = branch == .style ? "风格推荐" : "每日推荐"
        request = .homeAction(.daily(branch: branch), title: title)
        sections = []
        dailyStyle = nil
        total = nil
        loaded = false
        await refresh()
    }

    public func saveDailyStyle(_ selection: MusicDailyStyleSelection) async throws -> MusicDailyStyleSave {
        guard !closed, visible, !isLoading, !isActing, let source = remoteSource,
              source.dailyStyle != nil else { throw MusicServiceError.staleResult }
        let own = pageEpoch
        let action = UUID(); actionEpoch = action
        isActing = true
        defer { if actionEpoch == action { isActing = false } }
        let result: MusicDailyStyleSave
        do {
            result = try await source.saveDailyStyle(selection)
        } catch {
            if !Task.isCancelled, !closed, visible, pageEpoch == own, remoteSource?.id == source.id {
                sections = []; nextCursor = nil; currentCursor = nil
                cursorOffsets = ["": 0]; back = []; pageNumber = 1; trackOffset = 0; dailyStyle = nil
                detailSourcePositions = [:]
                loaded = false; failedPage = (cursor: nil, direction: 0)
                self.error = error as? MusicServiceError ?? MusicServiceError.sanitized(error)
            }
            throw error
        }
        guard !Task.isCancelled, !closed, visible, pageEpoch == own, remoteSource?.id == source.id else { return result }
        cursorOffsets = ["": 0]; back = []; currentCursor = nil
        await load(cursor: nil, direction: 0)
        return result
    }

    /// Public detail reads do not load the complete queue. Only an explicit
    /// playback action opens it, and resolves the selected occurrence.
    public func playbackSource(for track: MusicRemoteTrack? = nil) async throws -> (MusicRemoteQueueSource, Int) {
        guard !closed, visible, loaded, contentIsCurrent, !isLoading else { throw MusicServiceError.staleResult }
        let own = pageEpoch
        let source: MusicRemoteQueueSource
        if let remoteSource { source = remoteSource }
        else {
            guard let request = queueRequest else {
                throw MusicServiceError.unsupported
            }
            let opened = try await service.openPublicQueue(request)
            guard !closed, visible, pageEpoch == own, !Task.isCancelled else {
                release(opened); throw MusicServiceError.cancelled
            }
            remoteSource = opened; source = opened; watchSource()
        }
        if let availabilityNotice = source.context.availabilityNotice { notice = availabilityNotice }
        guard let track else { return (source, 0) }
        let position = detailSourcePositions[track.ref]
        var duplicate = 0
        selectedOccurrence: for page in (back + [currentCursor]).compactMap({ loadedPages[$0 ?? ""] }) {
            for section in page.page.sections {
                for item in section.items {
                    guard case .track(let row) = item.content else { continue }
                    if row.ref == track.ref { break selectedOccurrence }
                    if row.songId == track.songId { duplicate += 1 }
                }
            }
        }
        var matchingOccurrence = 0
        var fallback: Int?
        var cursor: String?, count = 0, seen = Set<String>()
        repeat {
            let page = try await remotePage(source, cursor: cursor)
            try Task.checkCancellation(); try source.authorization.check()
            guard !closed, visible, pageEpoch == own else { throw MusicServiceError.staleResult }
            for (index, row) in page.items.enumerated() {
                if let position, row.sourcePosition == position {
                    return (source, count + index)
                }
                guard row.songId == track.songId else { continue }
                if matchingOccurrence == duplicate, position == nil || row.sourcePosition == nil {
                    fallback = count + index
                }
                if position == nil, matchingOccurrence == duplicate {
                    return (source, count + index)
                }
                matchingOccurrence += 1
            }
            count += page.items.count
            cursor = page.cursor
            if let cursor { guard seen.insert(cursor).inserted else { throw MusicServiceError.staleResult } }
        } while cursor != nil
        if let fallback { return (source, fallback) }
        throw MusicServiceError.notFound
    }

    public func filteredQueueIndex(source: MusicRemoteQueueSource, index: Int,
                                   query: String) async throws -> Int {
        guard remoteSource?.id == source.id, index >= 0 else {
            throw MusicServiceError.staleResult
        }
        let own = pageEpoch
        var cursor: String?, seen = Set<String>(), offset = 0, matches = 0
        repeat {
            let page = try await remotePage(source, cursor: cursor)
            try Task.checkCancellation(); try source.authorization.check()
            guard !closed, visible, pageEpoch == own else { throw MusicServiceError.staleResult }
            for row in page.items {
                let included = MusicRemoteCollectionFilter.matches(row, query: query)
                if offset == index {
                    guard included else { throw MusicServiceError.notFound }
                    return matches
                }
                if included { matches += 1 }
                offset += 1
            }
            cursor = page.cursor
            if let cursor { guard seen.insert(cursor).inserted else { throw MusicServiceError.staleResult } }
        } while cursor != nil
        throw MusicServiceError.notFound
    }

    var actionErrorID: UUID? { actionError == nil ? nil : actionEpoch }

    func dismissActionError(id: UUID) {
        guard actionErrorID == id else { return }
        actionError = nil; actionDiagnosticID = nil
    }

    public func perform(operation: String = "歌单操作",
                        kind: MusicRequestTrace.Operation = .library,
                        _ body: @escaping @MainActor () async throws -> Void) async {
        guard !closed, visible, contentIsCurrent, !isActing, isHome || !isLoading else { return }
        let own = UUID()
        let trace = MusicRequestTrace(kind, platform: requestPlatform)
        trace.record(.started)
        actionEpoch = own; isActing = true; actionError = nil; actionDiagnosticID = nil; actionOperation = operation
        let newTask = Task { @MainActor in
            try await MusicRequestTrace.$current.withValue(trace) {
                try Task.checkCancellation()
                guard !self.closed, self.visible, self.actionEpoch == own else { throw MusicServiceError.staleAccess }
                try self.remoteSource?.authorization.check()
                try await body()
            }
        }
        actionTask = newTask
        defer { if actionEpoch == own { isActing = false; actionTask = nil } }
        do {
            try await withTaskCancellationHandler { try await newTask.value } onCancel: { newTask.cancel() }
            trace.record(.completed)
        } catch {
            trace.failed(error)
            if !closed, visible, actionEpoch == own, !Task.isCancelled {
                let safe = MusicServiceError.sanitized(error)
                if safe != .cancelled { actionError = safe; actionDiagnosticID = trace.logicalID }
            }
        }
    }
}
