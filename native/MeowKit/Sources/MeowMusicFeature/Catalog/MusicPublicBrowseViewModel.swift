import Foundation
import Combine

public enum MusicPublicBrowseRequest: Sendable, Hashable {
    case playlists(MusicPublicBrowsePlatform, query: String)
    case detail(MusicPublicBrowseCollection)
    case discovery(MusicPublicBrowseDestination)
    case homeAction(MusicHomeAction, title: String)
    case accountRecommendation(MusicAccountPlatform, kind: String, title: String)

    public var dailyPlatform: MusicAccountPlatform? {
        switch self {
        case .discovery(.daily(let platform)): return platform
        case .homeAction(.daily, _): return .netease
        case .accountRecommendation(let platform, let kind, _) where kind == "daily": return platform
        default: return nil
        }
    }

    public var dailyBranch: MusicHomeAction.DailyBranch? {
        guard dailyPlatform != nil else { return nil }
        if case .homeAction(.daily(let branch), _) = self { return branch }
        return .default
    }
}
public struct MusicPublicBrowseActionIntent: Sendable {
    fileprivate let id: UUID
    fileprivate let page: UUID
    fileprivate let access: UUID
}

/// Owns page tasks only, not a library, account session, source selection or player.
@MainActor public final class MusicPublicBrowseViewModel: ObservableObject {
    @Published public private(set) var sections: [MusicPublicBrowseSection] = []
    @Published public private(set) var filters: [MusicPublicBrowseFilter] = []
    @Published public private(set) var selectedFilter = ""
    @Published public private(set) var collection: MusicPublicBrowseCollection?
    @Published public private(set) var total: Int?
    @Published public private(set) var isLoading = false
    @Published public private(set) var isActing = false
    @Published public private(set) var error: MusicServiceError?
    @Published public private(set) var errorDiagnosticID: String?
    @Published public private(set) var sectionDiagnosticIDs: [String: String] = [:]
    @Published public private(set) var notice: String?
    @Published public private(set) var actionError: MusicServiceError?
    @Published public private(set) var actionDiagnosticID: String?
    @Published public private(set) var actionOperation = "歌单操作"
    @Published public private(set) var requiresExplicitNext = false
    @Published public private(set) var usesPageNavigation = false
    @Published public private(set) var loaded = false
    @Published public private(set) var contentIsCurrent = false
    @Published public private(set) var pageNumber = 1
    @Published public private(set) var nextCursor: String?
    @Published public private(set) var remoteSource: MusicRemoteQueueSource?
    @Published public private(set) var dailyStyle: MusicDailyStyleSelection?
    /// Offset of the first displayed song in the source snapshot. Account
    /// pages are not necessarily 30 rows; never infer this from pageNumber.
    public private(set) var trackOffset = 0
    public var canPrevious: Bool { !isHome && !back.isEmpty && !isLoading }
    public var queueRequest: MusicPublicBrowseQueueRequest? {
        guard let collection, loaded, contentIsCurrent, !isLoading else { return nil }
        return .init(collection: collection)
    }
    public var canPlayRemoteQueue: Bool { remoteSource != nil || queueRequest != nil }
    public var tracks: [MusicRemoteTrack] {
        sections.flatMap(\.items).compactMap { if case .track(let value) = $0.content { return value }; return nil }
    }
    public var networkSaveCollection: MusicPublicBrowseCollection? {
        guard case .detail(let requested) = request, requested.kind == .playlist else { return nil }
        return collection ?? requested
    }
    public var canSaveCollection: Bool { networkSaveCollection != nil }
    public var errorContext: String {
        switch request {
        case .detail(let collection): return collection.platform.title + " · 歌单读取"
        case .playlists(let platform, _): return platform.title + " · 歌单搜索"
        case .accountRecommendation(let platform, _, _): return platform.title + " · 推荐读取"
        case .discovery(.daily(let platform)): return platform.title + " · 推荐读取"
        case .discovery(.channel(let channel, _)): return channel.platform.title + " · 音乐内容读取"
        // Home actions are NetEase account/radio recipes.
        case .homeAction: return "网易云 · 音乐内容读取"
        default: return "音乐内容读取"
        }
    }
    private let service: any MusicPublicBrowsing
    @Published public private(set) var request: MusicPublicBrowseRequest
    private var generation = UUID(), pageEpoch = UUID(), actionEpoch = UUID()
    private var loadEpoch = UUID()
    private var homeNeedsRefresh = true
    private var playlistNeedsRefresh = true
    private var displayedHomeSource: MusicHomeSourceSelection?
    private var isHome: Bool { request == .discovery(.home) }
    /// Optional private artwork follows the settled home, not retained cards
    /// that are about to be replaced. This never gates user actions.
    var canLoadDisplayPreviews: Bool { !isHome || (!homeNeedsRefresh && !isLoading) }
    /// Module-local identity for page-owned presentations, never queue authority.
    var actionPageID: UUID { pageEpoch }
    private var available = false, visible = false, displayVisible = false
    private var contentDay: Date?
    private var isDaily: Bool {
        switch request {
        case .discovery(.daily), .homeAction(.daily, _): return true
        case .accountRecommendation(_, let kind, _): return kind == "daily"
        default: return false
        }
    }
    private var currentCursor: String?
    private var cursorOffsets: [String: Int] = ["": 0]
    private var detailSourcePositions: [MusicReference: Int] = [:]
    private var requestPlatform: MusicPublicBrowsePlatform? {
        switch request {
        case .detail(let collection): return collection.platform
        case .playlists(let platform, _): return platform
        case .accountRecommendation(let platform, _, _): return .init(accountPlatform: platform)
        case .discovery(.daily(let platform)): return .init(accountPlatform: platform)
        case .discovery(.channel(let channel, _)): return channel.platform
        case .homeAction: return .netease
        default: return nil
        }
    }
    private var failedPage: (cursor: String?, direction: Int)?
    private var failedRequest: MusicPublicBrowseRequest?
    private var back: [String?] = []
    private var loadedPages: [String: Loaded] = [:]
    private var homeTask: (id: UUID, work: Task<Void, Never>)?
    private var task: Task<Loaded, Error>?
    private var tabTask: Task<[MusicPublicBrowseItem], Error>?
    private var actionTask: Task<Void, Error>?
    private var revocationTask: Task<Void, Never>?
    private var issuedIntent: UUID?
    private var songObservation: Task<Void, Never>?
    private var songProjectionID = UUID()
    enum ArtworkReadState {
        case loading, settled(URL?), failed(MusicServiceError)
    }
    private(set) var artworkReads: [MusicPublicBrowseItem.CoverReference: ArtworkReadState] = [:]
    private var artworkDemand: Set<MusicPublicBrowseItem.CoverReference> = []
    private var artworkEpoch = UUID()
    private var artworkTask: Task<Void, Never>?
    private let queuePage: (@MainActor (MusicRemoteQueueSource, String?) async throws -> MusicBrowsePage)?
    private let releaseSource: @MainActor (MusicRemoteQueueSource) -> Void
    private struct Loaded: Sendable {
        var page: MusicPublicBrowsePage
        var collection: MusicPublicBrowseCollection?
        var total: Int?
        var source: MusicRemoteQueueSource?
        var dailyStyle: MusicDailyStyleSelection?
        var consumed: Int?
        var detailMembers: [MusicPublicBrowseMember]?
        var homeSource: MusicHomeSourceSelection?
    }
    public init(service: any MusicPublicBrowsing, request: MusicPublicBrowseRequest,
                queuePage: (@MainActor (MusicRemoteQueueSource, String?) async throws -> MusicBrowsePage)? = nil,
                releaseSource: (@MainActor (MusicRemoteQueueSource) -> Void)? = nil) {
        self.service = service; self.request = request
        self.queuePage = queuePage; self.releaseSource = releaseSource ?? Self.releaseDefaultSource
    }
    // Keep asynchronous cleanup out of a nested default-argument closure.
    // Revocation is synchronous; the task owns only the source, never the page.
    private nonisolated static func releaseDefaultSource(_ source: MusicRemoteQueueSource) {
        source.revoke()
        Task { [source] in await source.close() }
    }
    public func setAccess(available: Bool, generation: UUID) {
        guard self.available != available || self.generation != generation else { return }
        self.available = available; self.generation = generation
        songObservation?.cancel(); songObservation = nil
        invalidate(clear: true)
        observeSongs()
    }
    /// Personal queues use account leases. Public pages remain untouched;
    /// home/featured/channel account invalidation already has its native stream.
    @discardableResult public func invalidateAccount(_ platform: MusicAccountPlatform) -> Bool {
        if artworkReads.keys.contains(where: { $0.platform == .init(accountPlatform: platform) }) {
            retireArtwork()
        }
        let dependency: MusicAccountPlatform
        switch request {
        case .accountRecommendation(let value, _, _), .discovery(.daily(let value)): dependency = value
        case .homeAction(.songCollection, _): return false
        case .homeAction: dependency = .netease
        default: return false
        }
        guard dependency == platform || remoteSource?.context.platform == platform.rawValue else { return false }
        invalidate(clear: true)
        return true
    }
    public func appear() {
        if !visible, case .detail(let collection) = request, collection.kind == .playlist { playlistNeedsRefresh = true }
        visible = true; displayVisible = true
        publishArtwork()
        observeSongs()
    }
    /// Resource ownership and actual display demand are separate. A retained
    /// page may keep its owner active while a covered page must not refill
    /// display-only content.
    public func setDisplayVisible(_ value: Bool) {
        displayVisible = value
        if value { publishArtwork(); loadArtworkDemand() }
        else { artworkDemand = [] }
    }
    /// UIKit reports actual visible cards. Retained pages/tabs share the same
    /// content snapshot, but configuring a cell alone never starts a read.
    func setVisibleArtworkItems(_ items: [MusicPublicBrowseItem]) {
        let current = sections.flatMap(\.items)
        artworkDemand = Set(items.compactMap { item in
            guard displayVisible, item.artwork == nil, let ref = item.coverRef,
                  current.contains(where: { $0.id == item.id && $0.coverRef == ref && $0.artwork == nil }) else { return nil }
            return ref
        })
        let known = (sections + loadedPages.values.flatMap { $0.page.sections }).flatMap {
            $0.items + $0.tabs.flatMap(\.items)
        }.compactMap { item -> MusicPublicBrowseCollection? in
            if case .collection(let value) = item.content { return value }; return nil
        } + loadedPages.values.compactMap(\.collection)
        for ref in artworkDemand where artworkReads[ref] == nil {
            if let cover = known.first(where: {
                $0.platform == ref.platform && $0.kind == ref.kind && $0.nativeID == ref.id && $0.artwork != nil
            })?.artwork { artworkReads[ref] = .settled(cover) }
        }
        publishArtwork()
        loadArtworkDemand()
    }
    private func loadArtworkDemand() {
        guard available, visible, displayVisible, contentIsCurrent, canLoadDisplayPreviews, !isLoading,
              artworkTask == nil, let reader = service as? any MusicPublicPlaylistSummaryReading else { return }
        let missing = artworkDemand.filter { artworkReads[$0] == nil }
            .sorted { ($0.platform.rawValue, $0.id) < ($1.platform.rawValue, $1.id) }
        guard let first = missing.first else { return }
        let batch = Array(missing.filter { $0.platform == first.platform }.prefix(30))
        let own = artworkEpoch, access = generation
        for ref in batch { artworkReads[ref] = .loading }
        artworkTask = Task { [weak self] in
            let result: Result<[MusicPublicBrowseCollection], Error>
            do { result = .success(try await reader.publicPlaylistSummaries(platform: first.platform, ids: batch.map(\.id))) }
            catch { result = .failure(error) }
            guard let self, !Task.isCancelled, self.available, self.generation == access,
                  self.artworkEpoch == own else { return }
            for ref in batch {
                switch result {
                case .success(let rows):
                    self.artworkReads[ref] = .settled(rows.first {
                        $0.platform == ref.platform && $0.kind == ref.kind && $0.nativeID == ref.id && $0.artwork != nil
                    }?.artwork)
                case .failure(let error): self.artworkReads[ref] = .failed(MusicServiceError.sanitized(error))
                }
            }
            self.artworkTask = nil
            self.publishArtwork()
            self.loadArtworkDemand()
        }
    }
    private func publishArtwork() {
        guard displayVisible else { return }
        let covers = artworkReads.compactMapValues { state -> URL? in
            if case .settled(let url) = state { return url }; return nil
        }
        guard !covers.isEmpty else { return }
        let needsFill: (MusicPublicBrowseSection) -> Bool = { section in
            (section.items + section.tabs.flatMap(\.items)).contains {
                $0.artwork == nil && $0.coverRef.flatMap { covers[$0] } != nil
            }
        }
        if sections.contains(where: needsFill) { sections = sections.map { $0.fillingArtwork(covers) } }
        for key in Array(loadedPages.keys) {
            guard var value = loadedPages[key], value.page.sections.contains(where: needsFill) else { continue }
            value.page = .init(sections: value.page.sections.map { $0.fillingArtwork(covers) },
                cursor: value.page.cursor, filters: value.page.filters, selectedFilter: value.page.selectedFilter,
                notice: value.page.notice, consumed: value.page.consumed)
            loadedPages[key] = value
        }
    }
    private func retireArtwork() {
        artworkEpoch = UUID()
        artworkTask?.cancel(); artworkTask = nil
        artworkDemand = []; artworkReads = [:]
    }
    public var needsDateRefresh: Bool {
        guard isDaily, let contentDay else { return false }
        return !Calendar.current.isDate(contentDay, inSameDayAs: Date())
    }
    /// Visibility only checks date-scoped data; it never suspends page work.
    public func refreshForDateIfNeeded() async {
        guard available, visible, displayVisible, needsDateRefresh else { return }
        invalidate(clear: true)
        await load(cursor: nil, direction: 0, refresh: true)
    }
    /// The view owns this structured observation task. It can remain subscribed
    /// while an inactive home tab retains its display, but never polls.
    public func observeHomeChanges() async {
        switch request {
        case .discovery(.home), .discovery(.featured), .discovery(.channels), .discovery(.channel): break
        default: return
        }
        guard let home = service as? any MusicHomeBrowsing else { return }
        let access = generation
        let stream = await home.homeChanges()
        for await _ in stream {
            guard !Task.isCancelled, generation == access else { return }
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
                    guard !Task.isCancelled, generation == access else { return }
                    if let displayedHomeSource, displayedHomeSource != source {
                        invalidate(clear: true)
                    } else {
                        invalidateHomeRead()
                        retireHomeAccountEntries()
                    }
                    displayedHomeSource = source
                    homeNeedsRefresh = true
                } catch {
                    guard !Task.isCancelled, generation == access else { return }
                    self.error = MusicServiceError.sanitized(error)
                    continue
                }
            } else {
                invalidate(clear: true)
            }
            if available && visible && displayVisible { await refresh() }
        }
    }
    public func restoreHomeIfNeeded() async {
        guard available, visible, displayVisible, !loaded, !isLoading, case .discovery(.home) = request,
              let home = service as? any MusicHomeBrowsing else { return }
        let own = pageEpoch, access = generation
        do {
            let source = try await home.homeSourceSelection()
            let value = try await home.restoredHome()
            guard !Task.isCancelled, available, visible, !loaded, !isLoading,
                  pageEpoch == own, generation == access, let value else { return }
            // The snapshot may contain several successful response batches,
            // but never a persisted continuation or account grant.
            guard value.cursor == nil else { return }
            displayedHomeSource = source
            sections = value.sections; notice = value.notice; loaded = true; contentIsCurrent = true
        } catch {
            // A disposable snapshot must not prevent the actual home request.
        }
    }
    public func disappear() {
        visible = false; displayVisible = false
        songObservation?.cancel(); songObservation = nil
        invalidate(clear: true)
    }
    private func observeSongs() {
        guard available, visible, songObservation == nil,
              let repository = service as? any MusicSongRepositoryReading else { return }
        let access = generation
        songObservation = Task { [weak self] in
            let changes = await repository.songChanges()
            await self?.refreshSongProjections(repository, access: access)
            for await changed in changes {
                guard !Task.isCancelled else { return }
                await self?.refreshSongProjections(repository, access: access, changed: changed)
            }
        }
    }
    private func refreshSongProjections(_ repository: any MusicSongRepositoryReading, access: UUID, changed: Set<String>? = nil) async {
        guard !Task.isCancelled, available, visible, generation == access else { return }
        let projection = UUID(), page = pageEpoch
        let songs: [String: MusicRemoteTrack]
        let referenced = (sections + loadedPages.values.flatMap { $0.page.sections })
            .reduce(into: Set<String>()) { $0.formUnion($1.songIDs) }
        let ids = changed.map { referenced.intersection($0) } ?? referenced
        guard !ids.isEmpty else { return }
        songProjectionID = projection
        do { songs = try await repository.repositoryTracks(songIDs: ids) }
        catch { return } // A display read never reverses a committed mutation.
        guard !Task.isCancelled, available, visible, generation == access,
              pageEpoch == page, songProjectionID == projection else { return }
        sections = sections.map { $0.projected(songs) }
        for key in Array(loadedPages.keys) {
            guard var value = loadedPages[key] else { continue }
            value.page = .init(sections: value.page.sections.map { $0.projected(songs) },
                cursor: value.page.cursor, filters: value.page.filters, selectedFilter: value.page.selectedFilter,
                notice: value.page.notice, consumed: value.page.consumed)
            loadedPages[key] = value
        }
    }
    private func invalidate(clear: Bool) {
        retireArtwork()
        pageEpoch = UUID(); actionEpoch = UUID(); loadEpoch = UUID()
        issuedIntent = nil
        homeTask?.work.cancel(); homeTask = nil
        task?.cancel(); task = nil; actionTask?.cancel(); actionTask = nil
        tabTask?.cancel(); tabTask = nil
        revocationTask?.cancel(); revocationTask = nil
        isLoading = false; isActing = false; error = nil; actionError = nil; notice = nil
        errorDiagnosticID = nil; actionDiagnosticID = nil
        sectionDiagnosticIDs = [:]
        failedPage = nil; failedRequest = nil
        if clear {
            loadedPages = [:]
            if let remoteSource { releaseSource(remoteSource) }; remoteSource = nil
            sections = []; filters = []; selectedFilter = ""; collection = nil; total = nil
            nextCursor = nil; currentCursor = nil; back = []; loaded = false; pageNumber = 1
            cursorOffsets = ["": 0]; trackOffset = 0
            detailSourcePositions = [:]
            requiresExplicitNext = false; usesPageNavigation = false
            dailyStyle = nil
            contentIsCurrent = false
            contentDay = nil
            homeNeedsRefresh = true
            displayedHomeSource = nil
        }
    }
    /// Home display and captured actions outlive a background read. Only a
    /// navigation/source/access change retires their page identity.
    private func invalidateHomeRead() {
        retireArtwork()
        loadEpoch = UUID()
        homeTask?.work.cancel(); homeTask = nil
        task?.cancel(); task = nil
        tabTask?.cancel(); tabTask = nil
        isLoading = false
        error = nil; errorDiagnosticID = nil
        failedPage = nil; failedRequest = nil
        loadedPages = [:]
        currentCursor = nil; nextCursor = nil; back = []
        cursorOffsets = ["": 0]; trackOffset = 0
        detailSourcePositions = [:]
        pageNumber = 1; requiresExplicitNext = false; usesPageNavigation = false
    }
    private func retireHomeAccountEntries() {
        guard sections.contains(where: {
            $0.items.contains(where: \.requiresAccount) || $0.tabs.contains(where: {
                $0.items.contains(where: \.requiresAccount)
            })
        }) else { return }
        sections = sections.compactMap { section in
            let tabs = section.tabs.compactMap { tab -> MusicPublicBrowseSectionTab? in
                let items = tab.items.filter { !$0.requiresAccount }
                return items.isEmpty ? nil : .init(id: tab.id, title: tab.title, items: items, loadItems: tab.loadItems)
            }
            let selected = tabs.first { $0.id == section.selectedTab } ?? tabs.first
            let items = selected?.items ?? section.items.filter { !$0.requiresAccount }
            guard !items.isEmpty || !tabs.isEmpty || section.error != nil else { return nil }
            return .init(id: section.id, title: section.title, items: items, layout: section.layout,
                avatars: section.avatars, channel: section.channel, filters: section.filters,
                selectedFilter: section.selectedFilter, error: section.error, tabs: tabs,
                selectedTab: selected?.id ?? "")
        }
    }
    public func selectFilter(_ id: String) async {
        guard available, visible, !isActing, selectedFilter != id,
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
    /// Explicit section-scoped selection, validated against the still-visible
    /// response. A failed feed read must not erase the channel's other content.
    public func selectSectionFilter(_ id: String, sectionID: String, intent: MusicPublicBrowseActionIntent) async {
        guard available, visible, !isLoading, !isActing,
              issuedIntent == intent.id, pageEpoch == intent.page, generation == intent.access,
              let section = sections.first(where: { $0.id == sectionID }),
              section.filters.contains(where: { $0.id == id }), section.selectedFilter != id,
              case .discovery(.channel(let channel, _)) = request else { return }
        let candidate = MusicPublicBrowseRequest.discovery(.channel(channel, tag: id))
        invalidate(clear: false)
        loadedPages = [:]
        // Publish the new target only with a successful feed. Failure or
        // cancellation must never attach an old cursor to a new algorithm.
        await load(cursor: nil, direction: 0, refresh: true, requestOverride: candidate)
    }
    /// Uses returned data; only a tab with missing display fields invokes its
    /// scoped loader. No channel/algorithm request, playback or persistence.
    public func selectSectionTab(_ id: String, sectionID: String, intent: MusicPublicBrowseActionIntent) async {
        guard available, visible, !isLoading, !isActing,
              issuedIntent == intent.id, pageEpoch == intent.page, generation == intent.access,
              let index = sections.firstIndex(where: { $0.id == sectionID }),
              let tab = sections[index].tabs.first(where: { $0.id == id }),
              sections[index].selectedTab != id else { return }
        let old = sections[index]
        issuedIntent = nil; pageEpoch = UUID()
        let own = pageEpoch, access = generation, readEpoch = loadEpoch
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
            guard available, visible, generation == access, pageEpoch == own,
                  loadEpoch == readEpoch, !Task.isCancelled else { return }
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
            if let repository = service as? any MusicSongRepositoryReading {
                await refreshSongProjections(repository, access: access)
            }
        } catch {
            guard available, visible, generation == access, pageEpoch == own,
                  loadEpoch == readEpoch, !Task.isCancelled else { return }
            let failure = MusicServiceError.sanitized(error)
            guard failure != .cancelled else { return }
            trace.failed(error); sectionDiagnosticIDs[sectionID] = trace.logicalID
            sections[index] = .init(id: old.id, title: old.title, items: old.items, layout: old.layout,
                avatars: old.avatars, channel: old.channel, filters: old.filters,
                selectedFilter: old.selectedFilter, error: failure, tabs: old.tabs, selectedTab: old.selectedTab)
        }
    }
    public func selectDailyBranch(_ branch: MusicHomeAction.DailyBranch) async {
        guard available, visible, !isActing, request.dailyPlatform == .netease,
              request.dailyBranch != branch else { return }
        let title = branch == .style ? "风格推荐" : "每日推荐"
        request = .homeAction(.daily(branch: branch), title: title)
        sections = []
        dailyStyle = nil
        total = nil
        loaded = false
        await refresh()
    }
    public func saveDailyStyle(_ selection: MusicDailyStyleSelection,
                               receipts: MusicSettingsReceiptOwner) async throws -> MusicDailyStyleSave {
        guard available, visible, !isLoading, !isActing, let source = remoteSource,
              let editor = source.dailyStyle else { throw MusicServiceError.staleResult }
        let own = pageEpoch, access = generation
        let action = UUID(); actionEpoch = action
        isActing = true
        defer { if actionEpoch == action { isActing = false } }
        let work = receipts.submit(settings: ["daily-style/" + editor.scope + "/selection"],
                                   operation: "保存推荐风格") {
            let result = try await source.saveDailyStyle(selection)
            guard result.saveConfirmed else { throw MusicServiceError.commitUnconfirmed }
            return result
        }
        let result: MusicDailyStyleSave
        do {
            result = try await work.value
        } catch {
            if !Task.isCancelled, available, visible, generation == access, pageEpoch == own, remoteSource?.id == source.id {
                // The remote write may have happened. Pre-write cursors must
                // not remain actionable while the sheet reports a failed read.
                sections = []; nextCursor = nil; currentCursor = nil
                cursorOffsets = ["": 0]; back = []; pageNumber = 1; trackOffset = 0; dailyStyle = nil
                detailSourcePositions = [:]
                loaded = false; failedPage = (cursor: nil, direction: 0)
                let failure = error as? MusicServiceError ?? MusicServiceError.sanitized(error)
                self.error = failure == .commitUnconfirmed ? nil : failure
                if failure == .commitUnconfirmed { await load(cursor: nil, direction: 0) }
            }
            throw error
        }
        guard !Task.isCancelled, available, visible, generation == access,
              pageEpoch == own, remoteSource?.id == source.id else { return result }
        // Re-render through the same source and normal display projection.
        // Its first page is the exact readback already returned by save, not
        // another recommendation request or a second account mutation.
        cursorOffsets = ["": 0]; back = []; currentCursor = nil
        await load(cursor: nil, direction: 0)
        // Projection failures belong to this page read, not to the settled
        // write receipt. The load publishes its own current error.
        return result
    }
    public func refresh() async {
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
                guard own == pageEpoch, available, visible, !Task.isCancelled else { return }
            }
        }
        await load(cursor: nil, direction: 0, refresh: true)
    }
    public func loadIfNeeded() async {
        if playlistNeedsRefresh, case .detail(let collection) = request, collection.kind == .playlist,
           available, visible, !isLoading {
            await refresh()
            return
        }
        if isHome, !isLoading, available, visible, displayVisible,
           let displayed = displayedHomeSource, let home = service as? any MusicHomeBrowsing {
            let access = generation, page = pageEpoch
            do {
                // Source changes during the initial read can precede the
                // subscription. Reconcile locally on return, without reloading
                // an unchanged home or mistaking account changes for navigation.
                let current = try await home.homeSourceSelection()
                guard !Task.isCancelled, available, visible, displayVisible,
                      generation == access, pageEpoch == page, !isLoading else { return }
                if current != displayed {
                    invalidate(clear: true)
                    displayedHomeSource = current
                }
            } catch {
                guard !Task.isCancelled, generation == access, pageEpoch == page else { return }
                self.error = MusicServiceError.sanitized(error)
                return
            }
        }
        if isHome, homeNeedsRefresh, !isLoading, available, visible, displayVisible {
            await refresh()
            return
        }
        guard !loaded, !isLoading, error == nil else { return }
        await load(cursor: nil, direction: 0)
    }
    /// Public detail reads do not load the complete queue. Only an explicit
    /// playback action opens it, and resolves the selected occurrence.
    public func playbackSource(for track: MusicRemoteTrack? = nil) async throws -> (MusicRemoteQueueSource, Int) {
        guard available, visible, loaded, contentIsCurrent, !isLoading else { throw MusicServiceError.staleResult }
        let own = pageEpoch, access = generation
        let source: MusicRemoteQueueSource
        if let remoteSource { source = remoteSource }
        else {
            guard let request = queueRequest, let provider = service as? any MusicPublicBrowseQueueLoading else {
                throw MusicServiceError.unsupported
            }
            let opened = try await provider.openPublicQueue(request)
            guard available, visible, pageEpoch == own, generation == access, !Task.isCancelled else {
                releaseSource(opened); throw MusicServiceError.cancelled
            }
            remoteSource = opened; source = opened; watchSource()
        }
        if let availabilityNotice = source.context.availabilityNotice { notice = availabilityNotice }
        guard let track else { return (source, 0) }
        guard let queuePage else { throw MusicServiceError.unsupported }
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
            let page = try await queuePage(source, cursor)
            try Task.checkCancellation(); try source.authorization.check()
            guard available, visible, pageEpoch == own, generation == access else { throw MusicServiceError.staleResult }
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
    /// Count matches in the full source prefix, not in the currently displayed
    /// (possibly paginated) rows. Duplicates keep their occurrence position.
    public func filteredQueueIndex(source: MusicRemoteQueueSource, index: Int,
                                   query: String) async throws -> Int {
        guard let queuePage, remoteSource?.id == source.id, index >= 0 else {
            throw MusicServiceError.staleResult
        }
        let own = pageEpoch, access = generation
        var cursor: String?, seen = Set<String>(), offset = 0, matches = 0
        repeat {
            let page = try await queuePage(source, cursor)
            try Task.checkCancellation(); try source.authorization.check()
            guard available, visible, pageEpoch == own, generation == access else { throw MusicServiceError.staleResult }
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
    private func load(cursor: String?, direction: Int, refresh: Bool = false,
                      requestOverride: MusicPublicBrowseRequest? = nil) async {
        guard isHome else {
            await loadPage(cursor: cursor, direction: direction, refresh: refresh, requestOverride: requestOverride)
            return
        }
        if let homeTask { await homeTask.work.value; return }
        guard available, visible, !Task.isCancelled else { return }
        let id = UUID()
        let work = Task {
            var cursor = cursor, direction = direction, refresh = refresh
            while !Task.isCancelled, available, visible, homeTask?.id == id {
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
    private func loadPage(cursor: String?, direction: Int, refresh: Bool = false,
                          requestOverride: MusicPublicBrowseRequest? = nil) async -> Bool {
        guard available, visible, !isLoading else { return false }
        guard let offset = cursorOffsets[cursor ?? ""] else { error = .staleResult; return false }
        if direction > 0, let cursor, back.contains(where: { $0 == cursor }) {
            error = .invalidResponse; return false
        }
        let own = UUID(), access = generation, request = requestOverride ?? request, service = service
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
            guard available, visible, generation == access, loadEpoch == own else {
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
                await refreshSongProjections(repository, access: access)
            }
            trace.record(.completed)
            return true
        } catch {
            trace.failed(error)
            guard available, visible, generation == access, loadEpoch == own, !Task.isCancelled else { return false }
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
    private func watchSource() {
        revocationTask?.cancel()
        guard let source = remoteSource else { return }
        revocationTask = Task { [weak self] in
            for await _ in source.authorization.revocations() {
                guard !Task.isCancelled, let self, self.remoteSource?.id == source.id else { return }
                self.invalidate(clear: true); self.error = .staleAccess
                return
            }
        }
    }
    /// Callback is explicit user intent. The existing queue/player owner must
    /// additionally fence its own transaction and playback after suspension.
    public func makeActionIntent() -> MusicPublicBrowseActionIntent? {
        guard available, visible, contentIsCurrent, !isActing, isHome || !isLoading else { return nil }
        let id = UUID(); issuedIntent = id
        return .init(id: id, page: pageEpoch, access: generation)
    }
    var actionErrorID: UUID? { actionError == nil ? nil : actionEpoch }
    func dismissActionError(id: UUID) {
        guard actionErrorID == id else { return }
        actionError = nil; actionDiagnosticID = nil
    }
    public func perform(intent: MusicPublicBrowseActionIntent, operation: String = "歌单操作",
                        kind: MusicRequestTrace.Operation = .library,
                        _ body: @escaping @MainActor () async throws -> Void) async {
        guard available, visible, contentIsCurrent, !isActing, isHome || !isLoading, issuedIntent == intent.id,
              pageEpoch == intent.page, generation == intent.access else { return }
        issuedIntent = nil
        let own = UUID(), access = generation
        let trace = MusicRequestTrace(kind, platform: requestPlatform)
        trace.record(.started)
        actionEpoch = own; isActing = true; actionError = nil; actionDiagnosticID = nil; actionOperation = operation
        let newTask = Task { @MainActor in
            try await MusicRequestTrace.$current.withValue(trace) {
            try Task.checkCancellation()
            guard self.available, self.visible, self.generation == access, self.actionEpoch == own else { throw MusicServiceError.staleAccess }
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
            if available, visible, generation == access, actionEpoch == own, !Task.isCancelled {
                let safe = MusicServiceError.sanitized(error)
                if safe != .cancelled { actionError = safe; actionDiagnosticID = trace.logicalID }
            }
        }
    }
    deinit { songObservation?.cancel(); artworkTask?.cancel() }
}

private extension MusicPublicBrowseSection {
    func fillingArtwork(_ covers: [MusicPublicBrowseItem.CoverReference: URL]) -> Self {
        .init(id: id, title: title, items: items.map { $0.fillingArtwork(covers) }, layout: layout,
            avatars: avatars, channel: channel, filters: filters, selectedFilter: selectedFilter, error: error,
            tabs: tabs.map { .init(id: $0.id, title: $0.title, items: $0.items.map { $0.fillingArtwork(covers) },
                                   loadItems: $0.loadItems) }, selectedTab: selectedTab)
    }
}
private extension MusicPublicBrowseItem {
    func fillingArtwork(_ covers: [CoverReference: URL]) -> Self {
        guard artwork == nil, let ref = coverRef, let cover = covers[ref] else { return self }
        let next: MusicPublicBrowseContent
        if case .collection(let value) = content, value.platform == ref.platform,
           value.kind == ref.kind, value.nativeID == ref.id, value.artwork == nil {
            next = .collection(.init(platform: value.platform, nativeID: value.nativeID, kind: value.kind,
                title: value.title, creator: value.creator, artwork: cover, count: value.count, playCount: value.playCount))
        } else { next = content }
        return .init(id: id, title: title, subtitle: subtitle, artwork: cover, content: next,
            ranking: ranking, previewSongs: previewSongs, recommendationReason: recommendationReason,
            coverLabel: coverLabel, requiresAccount: requiresAccount, kind: kind, label: label,
            presentation: presentation, coverRef: coverRef, contextText: contextText, playCount: playCount)
    }
}

@MainActor public extension MusicViewModel {
    /// Same NativeMusicService instance exposed through an existing public port;
    /// no new service/account/writer and no visibility change to private state.
    var publicBrowser: (any MusicPublicBrowsing)? { accountBrowser as? any MusicPublicBrowsing }
}
