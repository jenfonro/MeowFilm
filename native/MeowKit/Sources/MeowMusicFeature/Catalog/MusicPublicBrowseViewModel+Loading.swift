import Foundation
import MeowDebug

@MainActor
extension MusicPublicBrowseViewModel {
    /// The view owns this structured observation task. It can remain subscribed
    /// while an inactive home tab retains its display, but never polls.
    public func observeHomeChanges() async {
        switch request {
        case .discovery(.home), .discovery(.featured), .discovery(.channels), .discovery(.channel): break
        default: return
        }
        guard let home = service as? any MusicHomeBrowsing else { return }
        let stream = await home.homeChanges()
        for await _ in stream {
            guard !Task.isCancelled, !closed else { return }
            let scoped: Bool
            switch request {
            case .discovery(.home), .discovery(.featured), .discovery(.channels): scoped = true
            case .discovery(.channel(let channel, _)):
                scoped = [.featured, .artistActivity, .vip].contains(channel.kind)
            default: scoped = false
            }
            retireArtwork()
            guard scoped else { continue }
            if isHome {
                do {
                    let source = try await home.homeSourceSelection()
                    guard !Task.isCancelled, !closed else { return }
                    if let displayedHomeSource, displayedHomeSource != source {
                        invalidate(clear: true)
                    } else {
                        invalidateHomeRead()
                        retireHomeAccountEntries()
                    }
                    displayedHomeSource = source
                    homeNeedsRefresh = true
                } catch {
                    guard !Task.isCancelled, !closed else { return }
                    self.error = MusicServiceError.sanitized(error)
                    continue
                }
            } else {
                invalidate(clear: true)
            }
            if !closed && visible && displayVisible { await refresh() }
        }
    }

    public func restoreHomeIfNeeded() async {
        guard !closed, visible, displayVisible, !loaded, !isLoading, case .discovery(.home) = request,
              let home = service as? any MusicHomeBrowsing else { return }
        let own = pageEpoch
        do {
            let source = try await home.homeSourceSelection()
            let value = try await home.restoredHome()
            guard !Task.isCancelled, !closed, visible, !loaded, !isLoading,
                  pageEpoch == own, let value else { return }
            // The snapshot may contain several successful response batches,
            // but never a persisted continuation or account grant.
            guard value.cursor == nil else { return }
            displayedHomeSource = source
            sections = value.sections; notice = value.notice; loaded = true; contentIsCurrent = true
        } catch {
            // A disposable snapshot must not prevent the actual home request.
        }
    }

    public func refresh() async {
        DebugRuntime.log(.musicCatalog, sub: "browse", event: "refresh", data: .object([
            "isHome": .bool(isHome)
        ]))
        if isHome {
            // Reentry joins the complete home read, including its continuations.
            if let homeTask { await homeTask.work.value; return }
            invalidateHomeRead()
            homeNeedsRefresh = false
            await load(cursor: nil, direction: 0, refresh: true)
            return
        }
        // Keep rows and scroll anchors, not an old queue source or admission.
        invalidate(clear: false)
        loadedPages = [:]
        contentIsCurrent = false
        if let remoteSource { releaseSource(remoteSource) }; remoteSource = nil
        currentCursor = nil; nextCursor = nil; back = []
        cursorOffsets = ["": 0]; trackOffset = 0
        detailSourcePositions = [:]
        pageNumber = 1; requiresExplicitNext = false; usesPageNavigation = false
        if case .detail(let collection) = request {
            playlistNeedsRefresh = false
            if let refreshing = service as? any MusicPublicBrowseRefreshing {
                let own = pageEpoch
                await refreshing.refreshPublicCollection(collection)
                guard own == pageEpoch, !closed, visible, !Task.isCancelled else { return }
            }
        }
        await load(cursor: nil, direction: 0, refresh: true)
    }

    public func loadIfNeeded() async {
        if playlistNeedsRefresh, case .detail(let collection) = request, collection.kind == .playlist,
           !closed, visible, !isLoading {
            await refresh()
            return
        }
        if isHome, !isLoading, !closed, visible, displayVisible,
           let displayed = displayedHomeSource, let home = service as? any MusicHomeBrowsing {
            let page = pageEpoch
            do {
                // Source changes during the initial read can precede the
                // subscription. Reconcile locally on return, without reloading
                // an unchanged home or mistaking account changes for navigation.
                let current = try await home.homeSourceSelection()
                guard !Task.isCancelled, !closed, visible, displayVisible,
                      pageEpoch == page, !isLoading else { return }
                if current != displayed {
                    invalidate(clear: true)
                    displayedHomeSource = current
                }
            } catch {
                guard !Task.isCancelled, !closed, pageEpoch == page else { return }
                self.error = MusicServiceError.sanitized(error)
                return
            }
        }
        if isHome, homeNeedsRefresh, !isLoading, !closed, visible, displayVisible {
            await refresh()
            return
        }
        guard !loaded, !isLoading, error == nil else { return }
        await load(cursor: nil, direction: 0)
    }

    public func retry() async {
        guard !isActing else { return }
        if isHome {
            if let failedPage, failedPage.cursor != nil {
                await load(cursor: failedPage.cursor, direction: failedPage.direction)
            } else { await refresh() }
            return
        }
        if let failedPage { await load(cursor: failedPage.cursor, direction: failedPage.direction, requestOverride: failedRequest) }
        else if !loaded { await refresh() }
        else { await load(cursor: currentCursor, direction: 0) }
    }

    public func next() async {
        guard !isHome, let nextCursor, !isLoading else { return }
        await load(cursor: nextCursor, direction: 1)
    }

    public func appendNext() async {
        guard !isHome, let cursor = nextCursor, !isLoading, sections.allSatisfy({ $0.error == nil }) else { return }
        if usesPageNavigation { await next(); return }
        await load(cursor: cursor, direction: 2)
    }

    public func previous() async {
        guard canPrevious else { return }
        await load(cursor: back[back.count - 1], direction: -1)
    }

    func load(cursor: String?, direction: Int, refresh: Bool = false,
              requestOverride: MusicPublicBrowseRequest? = nil) async {
        guard isHome else {
            await loadPage(cursor: cursor, direction: direction, refresh: refresh, requestOverride: requestOverride)
            return
        }
        if let homeTask { await homeTask.work.value; return }
        guard !closed, visible, !Task.isCancelled else { return }
        let id = UUID()
        let work = Task {
            var cursor = cursor, direction = direction, refresh = refresh
            while !Task.isCancelled, !closed, visible, homeTask?.id == id {
                guard await loadPage(cursor: cursor, direction: direction, refresh: refresh),
                      error == nil, sections.allSatisfy({ $0.error == nil }),
                      let next = nextCursor else { return }
                cursor = next; direction = 2; refresh = false
            }
        }
        homeTask = (id, work)
        defer { if homeTask?.id == id { homeTask = nil } }
        await withTaskCancellationHandler { await work.value } onCancel: { work.cancel() }
    }

    @discardableResult
    func loadPage(cursor: String?, direction: Int, refresh: Bool = false,
                  requestOverride: MusicPublicBrowseRequest? = nil) async -> Bool {
        guard !closed, visible, !isLoading else { return false }
        guard let offset = cursorOffsets[cursor ?? ""] else { error = .staleResult; return false }
        if direction > 0, let cursor, back.contains(where: { $0 == cursor }) {
            error = .invalidResponse; return false
        }
        let own = UUID(), request = requestOverride ?? request, service = service
        let day = Calendar.current.startOfDay(for: Date())
        if isDaily, cursor == nil, remoteSource == nil { contentDay = day }
        let savedSource = remoteSource
        let cached = direction != 0 && requestOverride == nil ? loadedPages[cursor ?? ""] : nil
        let queuePage = queuePage, releaseSource = releaseSource
        let trace = MusicRequestTrace(.browse, platform: requestPlatform)
        trace.record(.started)
        tabTask?.cancel(); tabTask = nil
        loadEpoch = own
        if !isHome { pageEpoch = own }
        isLoading = true; error = nil; errorDiagnosticID = nil
        let newTask = Task<Loaded, Error> {
            try await MusicRequestTrace.$current.withValue(trace) {
                if var cached {
                    if let savedSource { try await savedSource.validate(); cached.source = savedSource }
                    return cached
                }
                switch request {
                case .accountRecommendation(let platform, let kind, let title):
                    guard let provider = service as? any MusicRemoteQueueServing, let queuePage else {
                        throw MusicServiceError.unsupported
                    }
                    let source: MusicRemoteQueueSource
                    if let savedSource { source = savedSource }
                    else { source = try await provider.openAccountQueue(platform: platform,
                        kind: .recommendation(kind: kind), title: title) }
                    do {
                        let result = try await queuePage(source, cursor)
                        try Task.checkCancellation()
                        let items = result.items.enumerated().map { index, track in
                            MusicPublicBrowseItem(id: "\(offset + index):\(track.songId)", title: track.title,
                                subtitle: track.artist, artwork: track.artwork, content: track.songId.isEmpty ? .unavailable : .track(track))
                        }
                        return .init(page: .init(sections: [.init(id: source.context.id, title: title, items: items)],
                            cursor: result.cursor), source: source)
                    } catch {
                        if savedSource == nil { releaseSource(source) }; throw error
                    }
                case .homeAction(let action, let title):
                    guard let provider = service as? any MusicHomeActionQueueServing, let queuePage else {
                        throw MusicServiceError.unsupported
                    }
                    let source: MusicRemoteQueueSource
                    if let savedSource { source = savedSource }
                    else { source = try await provider.openHomeActionQueue(action, title: title) }
                    do {
                        let result = try await queuePage(source, cursor)
                        try Task.checkCancellation()
                        let items = result.items.enumerated().map { index, track in
                            MusicPublicBrowseItem(id: "\(offset + index):\(track.songId)", title: track.title,
                                subtitle: track.artist, artwork: track.artwork, content: track.songId.isEmpty ? .unavailable : .track(track))
                        }
                        return .init(page: .init(sections: [.init(id: source.context.id, title: title, items: items)],
                                                cursor: result.cursor), source: source,
                                     dailyStyle: result.dailyStyle)
                    } catch {
                        if savedSource == nil { releaseSource(source) }; throw error
                    }
                case .playlists(let platform, let query):
                    let result = try await service.publicPlaylists(platform: platform, query: query, cursor: cursor)
                    let items = result.items.enumerated().map { index, value in
                        MusicPublicBrowseItem(id: "\(cursor ?? "first"):\(index):\(value.id)",
                            title: value.title, subtitle: value.creator, artwork: value.artwork,
                            content: value.nativeID.isEmpty ? .unavailable : .collection(value))
                    }
                    return .init(page: .init(sections: [.init(id: "playlists", title: platform.title, items: items,
                        layout: .playlistRail)], cursor: result.cursor), total: result.total)
                case .detail(let collection):
                    let result = try await service.publicDetail(collection, cursor: cursor)
                    let items = result.members.enumerated().map { index, member in
                        let track = member.track
                        return MusicPublicBrowseItem(id: "\(cursor ?? "first"):\(index):\(track.songId)", title: track.title,
                            subtitle: track.artist, artwork: track.artwork, content: track.songId.isEmpty ? .unavailable : .track(track))
                    }
                    return .init(page: .init(sections: [.init(id: collection.id, title: result.collection.title, items: items)],
                                 cursor: result.cursor, notice: result.notice),
                                 collection: result.collection, total: result.total, source: savedSource,
                                 consumed: result.consumed, detailMembers: result.members)
                case .discovery(let destination):
                    if destination == .home, let home = service as? any MusicHomeBrowsing {
                        let source = try await home.homeSourceSelection()
                        let page = try await home.homePage(cursor: cursor, refresh: refresh)
                        return .init(page: page, consumed: page.consumed, homeSource: source)
                    }
                    if case .daily(let platform) = destination, let queuePage,
                       let provider = service as? any MusicRemoteQueueServing {
                        let source: MusicRemoteQueueSource
                        if let savedSource { source = savedSource }
                        else { source = try await provider.openAccountQueue(platform: platform, kind: .recommendation(kind: "daily"), title: "每日推荐") }
                        do {
                            let result = try await queuePage(source, cursor)
                            try Task.checkCancellation()
                            let items = result.items.enumerated().map { index, track in
                                MusicPublicBrowseItem(id: "\(offset + index):\(track.songId)", title: track.title,
                                    subtitle: track.artist, artwork: track.artwork, content: track.songId.isEmpty ? .unavailable : .track(track))
                            }
                            return .init(page: .init(sections: [.init(id: "daily", title: "每日推荐", items: items)], cursor: result.cursor),
                                         source: source)
                        } catch {
                            if savedSource == nil { releaseSource(source) }; throw error
                        }
                    }
                    let page = try await service.publicDiscovery(destination, cursor: cursor)
                    return .init(page: page, consumed: page.consumed)
                }
            }
        }
        task = newTask
        var unpublished: MusicRemoteQueueSource?
        defer {
            if let unpublished, unpublished.id != remoteSource?.id { releaseSource(unpublished) }
            if own == loadEpoch { task = nil; isLoading = false; publishArtwork(); loadArtworkDemand() }
        }
        do {
            let result = try await withTaskCancellationHandler { try await newTask.value } onCancel: { newTask.cancel() }
            unpublished = result.source
            try Task.checkCancellation()
            guard !closed, visible, loadEpoch == own else {
                return false
            }
            if isDaily {
                guard Calendar.current.isDate(day, inSameDayAs: Date()),
                      contentDay.map({ Calendar.current.isDate($0, inSameDayAs: Date()) }) == true else {
                    throw MusicServiceError.staleResult
                }
            }
            let count = result.page.sections.reduce(0) { $0 + $1.items.count }
            if let next = result.page.cursor, next == cursor ||
                direction > 0 && (next == currentCursor || back.contains(where: { $0 == next })) {
                throw MusicServiceError.invalidResponse
            }
            if isHome, direction == 2, let failure = result.page.sections.compactMap(\.error).first {
                // The service has not committed this failed batch's cursor.
                // Keep successful content and retry the same continuation.
                throw failure
            }
            sectionDiagnosticIDs = Dictionary(result.page.sections.compactMap { section in
                guard let error = section.error else { return nil }
                trace.record(.failed, failure: error)
                return (section.id, trace.logicalID)
            }, uniquingKeysWith: { _, newest in newest })
            var merged = result.page.sections
            if direction == 0, cursor == nil, result.page.sections.contains(where: { $0.error != nil }) {
                // Preserve the displayed feed and its committed selection on
                // a refresh/filter failure. The explicit retry owns the failed
                // candidate; automatic pagination remains stopped.
                merged = result.page.sections.map { section in
                    guard let failure = section.error,
                          let old = sections.first(where: { $0.id == section.id && $0.layout == section.layout }) else { return section }
                    return .init(id: old.id, title: old.title, items: old.items, layout: old.layout,
                                 avatars: old.avatars, channel: old.channel, filters: old.filters,
                                 selectedFilter: old.selectedFilter, error: failure,
                                 tabs: old.tabs, selectedTab: old.selectedTab)
                }
                sections = merged; filters = result.page.filters; selectedFilter = result.page.selectedFilter
                notice = result.page.notice; loaded = true
                // A failed tag read must not authorize retained pixels as the
                // new selection. Section-local retries keep their old target.
                if requestOverride == nil, !isHome { contentIsCurrent = false }
                failedPage = (nil, 0); failedRequest = requestOverride
                return false
            }
            if direction == 2 {
                merged = sections
                for section in result.page.sections {
                    if let index = merged.firstIndex(where: { $0.id == section.id && $0.layout == section.layout }) {
                        var identifiers = Set(merged[index].items.map(\.id))
                        let items = merged[index].items + section.items.filter { identifiers.insert($0.id).inserted }
                        merged[index] = .init(id: section.id, title: section.title, items: items, layout: section.layout,
                                              avatars: section.avatars, channel: section.channel,
                                              filters: section.filters, selectedFilter: section.selectedFilter, error: section.error,
                                              tabs: section.tabs, selectedTab: section.selectedTab)
                    } else { merged.append(section) }
                }
            }
            let songCount = result.page.sections.flatMap(\.items).filter {
                if case .track = $0.content { return true }; return false
            }.count
            let consumed = result.consumed ?? songCount
            if direction == 0 && cursor == nil { back = []; pageNumber = 1; cursorOffsets = ["": 0] }
            if direction > 0 { back.append(currentCursor); pageNumber += 1 }
            if direction == -1 { back.removeLast(); pageNumber -= 1 }
            if direction != 2 { trackOffset = offset }
            if let next = result.page.cursor { cursorOffsets[next] = offset + consumed }
            if direction != 2 {
                detailSourcePositions = [:]
            }
            if let members = result.detailMembers {
                for member in members {
                    detailSourcePositions[member.track.ref] = member.sourcePosition
                }
            }
            currentCursor = cursor; nextCursor = result.page.cursor; sections = merged
            if let source = result.homeSource { displayedHomeSource = source }
            if requestOverride != nil { loadedPages = [:] }
            loadedPages[cursor ?? ""] = result
            self.request = request
            notice = result.page.notice
            filters = result.page.filters; selectedFilter = result.page.selectedFilter
            collection = result.collection; total = result.total; remoteSource = result.source; loaded = true
            contentIsCurrent = true
            if case .discovery = request { requiresExplicitNext = false }
            else { requiresExplicitNext = count == 0 && result.page.cursor != nil }
            if direction == 1 { usesPageNavigation = true }
            if cursor == nil || result.dailyStyle != nil { dailyStyle = result.dailyStyle }
            failedPage = nil; failedRequest = nil
            watchSource()
            if let repository = service as? any MusicSongRepositoryReading {
                await refreshSongProjections(repository)
            }
            trace.record(.completed)
            return true
        } catch {
            trace.failed(error)
            guard !closed, visible, loadEpoch == own, !Task.isCancelled else { return false }
            let safe = MusicServiceError.sanitized(error)
            if safe != .cancelled {
                self.error = safe; errorDiagnosticID = trace.logicalID
                failedPage = (cursor, direction); failedRequest = requestOverride
            }
            if !isHome, safe.isAuthenticationFailure || [.accessUnavailable, .staleAccess, .staleResult, .notFound].contains(safe) {
                loadedPages = [:]
                if let remoteSource { releaseSource(remoteSource) }; remoteSource = nil
                sections = []; notice = nil; nextCursor = nil; loaded = false
                collection = nil; total = nil; currentCursor = nil; back = []; pageNumber = 1; failedPage = nil; failedRequest = nil
                cursorOffsets = ["": 0]; trackOffset = 0
                detailSourcePositions = [:]
                contentIsCurrent = false
            }
            return false
        }
    }
}
