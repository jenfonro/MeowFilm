import Foundation
import Combine

public enum MusicPublicBrowseRequest: Sendable, Hashable {
    case playlists(MusicPublicBrowsePlatform, query: String)
    case detail(MusicPublicBrowseCollection)
    case discovery(MusicPublicBrowseDestination)
    case homeAction(MusicHomeAction, title: String)
    case accountRecommendation(MusicAccountPlatform, kind: String, title: String)
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
    public var canPrevious: Bool { !back.isEmpty && !isLoading }
    public var queueRequest: MusicPublicBrowseQueueRequest? {
        guard let collection, let snapshot, loaded, contentIsCurrent, !isLoading else { return nil }
        return .init(collection: collection, snapshot: snapshot)
    }
    public var tracks: [MusicRemoteTrack] {
        sections.flatMap(\.items).compactMap { if case .track(let value) = $0.content { return value }; return nil }
    }
    public var canSaveCollection: Bool { remoteSource != nil || queueRequest != nil }
    public var errorContext: String {
        switch request {
        case .detail(let collection): return collection.platform.title + " · 歌单读取"
        case .playlists(let platform, _): return platform.title + " · 歌单搜索"
        case .accountRecommendation(let platform, _, _): return platform.title + " · 推荐读取"
        case .discovery(.daily(let platform)): return platform.title + " · 推荐读取"
        case .discovery(.channel(let channel, _)): return channel.platform.title + " · 音乐内容读取"
        default: return "网易云 · 音乐内容读取"
        }
    }
    private let service: any MusicPublicBrowsing
    @Published public private(set) var request: MusicPublicBrowseRequest
    private var generation = UUID(), pageEpoch = UUID(), actionEpoch = UUID()
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
    private var snapshot: String?
    private var currentCursor: String?
    private var cursorOffsets: [String: Int] = ["": 0]
    private struct PageOccurrence {
        let cursor: String
        let sourcePosition: Int
    }
    private var trackPageOccurrences: [MusicReference: PageOccurrence] = [:]
    private var detailSourcePositions: [MusicReference: Int] = [:]
    private var requestPlatform: MusicPublicBrowsePlatform? {
        switch request {
        case .detail(let collection): return collection.platform
        case .playlists(let platform, _): return platform
        case .accountRecommendation(let platform, _, _): return .init(accountPlatform: platform)
        case .discovery(.daily(let platform)): return .init(accountPlatform: platform)
        case .discovery(.channel(let channel, _)): return channel.platform
        default: return .netease
        }
    }
    private var failedPage: (cursor: String?, direction: Int)?
    private var failedRequest: MusicPublicBrowseRequest?
    private var back: [String?] = []
    private var loadedPages: [String: Loaded] = [:]
    private var task: Task<Loaded, Error>?
    private var tabTask: Task<[MusicPublicBrowseItem], Error>?
    private var loadingCursor: String?
    private var actionTask: Task<Void, Error>?
    private var dailyStyleTask: Task<MusicDailyStyleSave, Error>?
    private var revocationTask: Task<Void, Never>?
    private var issuedIntent: UUID?
    private let queuePage: (@MainActor (MusicRemoteQueueSource, String?) async throws -> MusicBrowsePage)?
    private let releaseSource: @MainActor (MusicRemoteQueueSource) -> Void
    private struct Loaded: Sendable {
        var page: MusicPublicBrowsePage
        var collection: MusicPublicBrowseCollection?
        var total: Int?
        var snapshot: String?
        var source: MusicRemoteQueueSource?
        var dailyStyle: MusicDailyStyleSelection?
        var consumed: Int?
        var detailMembers: [MusicPublicBrowseMember]?
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
        invalidate(clear: true)
    }
    /// Personal queues use account leases. Public pages remain untouched;
    /// home/featured/channel account invalidation already has its native stream.
    @discardableResult public func invalidateAccount(_ platform: MusicAccountPlatform) -> Bool {
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
    public func appear() { visible = true; displayVisible = true }
    /// Resource ownership and actual display demand are separate. A retained
    /// page may keep its owner active while a covered page must not refill
    /// display-only content.
    public func setDisplayVisible(_ value: Bool) { displayVisible = value }
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
            guard scoped else { continue }
            invalidate(clear: true)
            if available && visible && displayVisible { await refresh() }
        }
    }
    public func restoreHomeIfNeeded() async {
        guard available, visible, displayVisible, !loaded, !isLoading, case .discovery(.home) = request,
              let home = service as? any MusicHomeBrowsing else { return }
        let own = pageEpoch, access = generation
        do {
            let value = try await home.restoredHome()
            guard !Task.isCancelled, available, visible, !loaded, !isLoading,
                  pageEpoch == own, generation == access, let value else { return }
            // Restored handles are freshly issued by the native owner; persisted
            // continuations are forbidden even if a faulty adapter returns one.
            guard value.cursor == nil, value.sections.count <= 60,
                  value.sections.reduce(0, { $0 + $1.items.count }) <= 600 else { return }
            sections = value.sections; notice = value.notice; loaded = true; contentIsCurrent = true
        } catch {
            // A disposable snapshot must not prevent the actual home request.
        }
    }
    public func disappear() {
        visible = false; displayVisible = false
        invalidate(clear: true)
    }
    private func invalidate(clear: Bool) {
        pageEpoch = UUID(); actionEpoch = UUID()
        issuedIntent = nil
        task?.cancel(); task = nil; actionTask?.cancel(); actionTask = nil
        tabTask?.cancel(); tabTask = nil
        dailyStyleTask?.cancel(); dailyStyleTask = nil
        revocationTask?.cancel(); revocationTask = nil
        isLoading = false; isActing = false; error = nil; actionError = nil; notice = nil
        errorDiagnosticID = nil; actionDiagnosticID = nil
        sectionDiagnosticIDs = [:]
        failedPage = nil; failedRequest = nil
        if clear {
            loadedPages = [:]
            if let remoteSource { releaseSource(remoteSource) }; remoteSource = nil
            sections = []; filters = []; selectedFilter = ""; collection = nil; total = nil; snapshot = nil
            nextCursor = nil; currentCursor = nil; back = []; loaded = false; pageNumber = 1
            cursorOffsets = ["": 0]; trackOffset = 0
            trackPageOccurrences = [:]; detailSourcePositions = [:]
            requiresExplicitNext = false; usesPageNavigation = false
            dailyStyle = nil
            contentIsCurrent = false
            contentDay = nil
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
        let own = pageEpoch, access = generation
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
            guard available, visible, generation == access, pageEpoch == own, !Task.isCancelled else { return }
            guard items.count <= 600, Set(items.map(\.id)).count == items.count else {
                throw MusicServiceError.invalidResponse
            }
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
        } catch {
            guard available, visible, generation == access, pageEpoch == own, !Task.isCancelled else { return }
            let failure = MusicServiceError.sanitized(error)
            guard failure != .cancelled else { return }
            trace.failed(error); sectionDiagnosticIDs[sectionID] = trace.logicalID
            sections[index] = .init(id: old.id, title: old.title, items: old.items, layout: old.layout,
                avatars: old.avatars, channel: old.channel, filters: old.filters,
                selectedFilter: old.selectedFilter, error: failure, tabs: old.tabs, selectedTab: old.selectedTab)
        }
    }
    public func selectDailyBranch(_ branch: MusicHomeAction.DailyBranch) async {
        let title = branch == .style ? "风格推荐" : "每日推荐"
        switch request {
        case .homeAction(.daily, _), .discovery(.daily(.netease)): break
        default: return
        }
        request = .homeAction(.daily(branch: branch), title: title)
        await refresh()
    }
    public func saveDailyStyle(_ selection: MusicDailyStyleSelection) async throws -> MusicDailyStyleSave {
        guard available, visible, !isLoading, !isActing, let source = remoteSource,
              source.dailyStyle != nil else { throw MusicServiceError.staleResult }
        let own = pageEpoch, access = generation
        let action = UUID(); actionEpoch = action
        isActing = true
        defer { if actionEpoch == action { isActing = false; dailyStyleTask = nil } }
        let work = Task { try await source.saveDailyStyle(selection) }
        dailyStyleTask = work
        let result: MusicDailyStyleSave
        do {
            result = try await withTaskCancellationHandler { try await work.value } onCancel: { work.cancel() }
        } catch {
            if available, visible, generation == access, pageEpoch == own, remoteSource?.id == source.id {
                // The remote write may have happened. Old handles/cursors must
                // not remain actionable while the sheet reports a failed read.
                sections = []; nextCursor = nil; currentCursor = nil; snapshot = nil
                cursorOffsets = ["": 0]; back = []; pageNumber = 1; trackOffset = 0; dailyStyle = nil
                trackPageOccurrences = [:]; detailSourcePositions = [:]
                loaded = false; failedPage = (cursor: nil, direction: 0)
                self.error = Task.isCancelled ? .commitUnconfirmed : MusicServiceError.sanitized(error)
            }
            throw error
        }
        try Task.checkCancellation()
        guard available, visible, generation == access, pageEpoch == own, remoteSource?.id == source.id else {
            throw MusicServiceError.staleResult
        }
        // Re-render through the same source and normal display-handle path.
        // Its first page is the exact readback already returned by save, not
        // another recommendation request or a second account mutation.
        cursorOffsets = ["": 0]; back = []; currentCursor = nil; snapshot = nil
        await load(cursor: nil, direction: 0)
        guard available, visible, generation == access, remoteSource?.id == source.id else {
            throw MusicServiceError.staleResult
        }
        isActing = false
        if let error { throw error }
        return result
    }
    public func refresh() async {
        let isHome = request == .discovery(.home)
        if isHome, isLoading, loadingCursor == nil {
            // Match the reference's single homeTask; repeated pull/activation
            // must not cancel and restart the same first-page request.
            if let task { _ = try? await task.value }
            return
        }
        // Keep rows and scroll anchors, not an old queue source or admission.
        invalidate(clear: false)
        loadedPages = [:]
        contentIsCurrent = false
        if let remoteSource { releaseSource(remoteSource) }; remoteSource = nil
        snapshot = nil; currentCursor = nil; nextCursor = nil; back = []
        cursorOffsets = ["": 0]; trackOffset = 0
        trackPageOccurrences = [:]; detailSourcePositions = [:]
        pageNumber = 1; requiresExplicitNext = false; usesPageNavigation = false
        if case .detail(let collection) = request,
           let refreshing = service as? any MusicPublicBrowseRefreshing {
            let own = pageEpoch
            await refreshing.refreshPublicCollection(collection)
            guard own == pageEpoch, available, visible, !Task.isCancelled else { return }
        }
        await load(cursor: nil, direction: 0, refresh: true)
    }
    public func loadIfNeeded() async {
        guard !loaded, !isLoading, error == nil else { return }
        await load(cursor: nil, direction: 0)
    }
    /// Public detail reads do not load the complete queue. Only an explicit
    /// play/save action opens it, and resolves the selected stable recording.
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
        // Only WY playlists omit inaccessible rows while retaining a raw-ID
        // cursor; their IDs are proven unique by the provider. Every other
        // source uses complete occurrence offsets, including repeated songs.
        let expectedIndex: Int?
        if case .detail(let collection) = request, collection.platform == .netease, collection.kind == .playlist {
            expectedIndex = nil
        } else {
            guard let index = queueIndex(of: track) else { throw MusicServiceError.staleResult }
            expectedIndex = index
        }
        var cursor: String?, count = 0, seen = Set<String>()
        repeat {
            let page = try await queuePage(source, cursor)
            try Task.checkCancellation(); try source.authorization.check()
            guard available, visible, pageEpoch == own, generation == access else { throw MusicServiceError.staleResult }
            if let index = page.items.indices.first(where: {
                (expectedIndex == nil || count + $0 == expectedIndex) && Self.sameActionIdentity(page.items[$0], track)
            }) {
                return (source, count + index)
            }
            count += page.items.count
            cursor = page.cursor
            if let cursor { guard seen.insert(cursor).inserted else { throw MusicServiceError.staleResult } }
        } while cursor != nil
        throw MusicServiceError.notFound
    }
    /// Count matches in the full source prefix, not in the currently displayed
    /// (possibly paginated) rows. Duplicates keep their occurrence position.
    public func filteredQueueIndex(source: MusicRemoteQueueSource, index: Int,
                                   track: MusicRemoteTrack, query: String) async throws -> Int {
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
                    guard included, Self.sameActionIdentity(row, track) else { throw MusicServiceError.targetChanged }
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
        if let failedPage { await load(cursor: failedPage.cursor, direction: failedPage.direction, requestOverride: failedRequest) }
        else if !loaded { await refresh() }
        else { await load(cursor: currentCursor, direction: 0) }
    }
    public func next() async {
        guard let nextCursor, !isLoading else { return }
        await load(cursor: nextCursor, direction: 1)
    }
    public func appendNext() async {
        guard let cursor = nextCursor, !isLoading, sections.allSatisfy({ $0.error == nil }) else { return }
        if usesPageNavigation { await next(); return }
        await load(cursor: cursor, direction: 2)
    }
    public func queueIndex(of track: MusicRemoteTrack) -> Int? {
        if let position = detailSourcePositions[track.ref] { return position }
        return tracks.firstIndex(where: { $0.ref == track.ref }).map { trackOffset + $0 }
    }
    /// Visible rows may outlive the bounded native
    /// handle cache. Reissue only the selected occurrence's original page; do
    /// not replace display rows, alter pagination, or extend handle lifetimes.
    public func refreshedActionTrack(_ track: MusicRemoteTrack) async throws -> MusicRemoteTrack {
        var details: [String: MusicPublicBrowseDetail] = [:]
        return try await refreshedActionTrack(track, details: &details)
    }
    /// Batch actions reuse each original detail page once while keeping every
    /// selected occurrence's own identity checks.
    public func refreshedActionTracks(_ tracks: [MusicRemoteTrack]) async throws -> [MusicRemoteTrack] {
        var details: [String: MusicPublicBrowseDetail] = [:]
        var renewed: [MusicRemoteTrack] = []
        renewed.reserveCapacity(tracks.count)
        for track in tracks { renewed.append(try await refreshedActionTrack(track, details: &details)) }
        return renewed
    }
    private func refreshedActionTrack(
        _ track: MusicRemoteTrack,
        details: inout [String: MusicPublicBrowseDetail]
    ) async throws -> MusicRemoteTrack {
        try Task.checkCancellation()
        guard available, visible, loaded, contentIsCurrent, !isLoading, let index = queueIndex(of: track),
              let displayed = tracks.first(where: { $0.ref == track.ref }),
              Self.sameActionIdentity(displayed, track) else { throw MusicServiceError.staleResult }
        // Public-page cursors and queue cursors are different contracts, even
        // after an explicit action has opened a queue for this collection.
        if case .detail(let collection) = request, let occurrence = trackPageOccurrences[track.ref] {
            let own = pageEpoch, access = generation
            let source = remoteSource
            try source?.authorization.check()
            let result: MusicPublicBrowseDetail
            if let cached = details[occurrence.cursor] {
                result = cached
            } else {
                result = try await service.publicDetail(collection, cursor: occurrence.cursor.isEmpty ? nil : occurrence.cursor)
                details[occurrence.cursor] = result
            }
            try Task.checkCancellation()
            // The observer is asynchronous; a revoked queue must fence this
            // result even before its observation task invalidates the page.
            try source?.authorization.check()
            guard available, visible, pageEpoch == own, generation == access, result.snapshot == snapshot else {
                throw MusicServiceError.staleResult
            }
            guard let member = result.members.first(where: {
                $0.sourcePosition == occurrence.sourcePosition
            }) else {
                if collection.platform == .netease, collection.kind == .playlist {
                    throw MusicServiceError.notFound
                }
                throw MusicServiceError.targetChanged
            }
            guard Self.sameActionIdentity(member.track, track) else {
                throw MusicServiceError.targetChanged
            }
            return member.track
        }
        guard let source = remoteSource else { return displayed }
        guard let queuePage else { throw MusicServiceError.unsupported }
        try source.authorization.check()
        let own = pageEpoch, access = generation
        guard let page = cursorOffsets.filter({ $0.value <= index }).max(by: { $0.value < $1.value }) else {
            throw MusicServiceError.staleResult
        }
        let result = try await queuePage(source, page.key.isEmpty ? nil : page.key)
        try Task.checkCancellation()
        guard available, visible, loaded, !isLoading, generation == access, pageEpoch == own,
              remoteSource?.id == source.id, queueIndex(of: track) == index else { throw MusicServiceError.staleResult }
        try source.authorization.check()
        let localIndex = index - page.value
        guard result.items.count <= 300, result.items.indices.contains(localIndex) else {
            throw MusicServiceError.invalidResponse
        }
        let renewed = result.items[localIndex]
        guard Self.sameActionIdentity(track, renewed) else { throw MusicServiceError.targetChanged }
        return renewed
    }
    private static func sameActionIdentity(_ lhs: MusicRemoteTrack, _ rhs: MusicRemoteTrack) -> Bool {
        lhs.songId.utf8.elementsEqual(rhs.songId.utf8) && lhs.sources.count == rhs.sources.count &&
            zip(lhs.sources, rhs.sources).allSatisfy {
                $0.0.sourceKey.utf8.elementsEqual($0.1.sourceKey.utf8) &&
                $0.0.platform.utf8.elementsEqual($0.1.platform.utf8) &&
                $0.0.nativeId.utf8.elementsEqual($0.1.nativeId.utf8)
            }
    }
    public func previous() async {
        guard !back.isEmpty, !isLoading else { return }
        await load(cursor: back[back.count - 1], direction: -1)
    }
    private func load(cursor: String?, direction: Int, refresh: Bool = false,
                      requestOverride: MusicPublicBrowseRequest? = nil) async {
        guard available, visible, !isLoading else { return }
        guard let offset = cursorOffsets[cursor ?? ""] else { error = .staleResult; return }
        if direction > 0, let cursor, back.contains(where: { $0 == cursor }) {
            error = .invalidResponse; return
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
        pageEpoch = own; isLoading = true; loadingCursor = cursor; error = nil; errorDiagnosticID = nil
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
                            subtitle: track.artist, artwork: track.artwork, content: .track(track))
                    }
                    return .init(page: .init(sections: [.init(id: source.context.id, title: title, items: items)],
                        cursor: result.cursor), snapshot: source.id.uuidString, source: source)
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
                            subtitle: track.artist, artwork: track.artwork, content: .track(track))
                    }
                    return .init(page: .init(sections: [.init(id: source.context.id, title: title, items: items)],
                                            cursor: result.cursor), snapshot: source.id.uuidString, source: source,
                                 dailyStyle: result.dailyStyle)
                } catch {
                    if savedSource == nil { releaseSource(source) }; throw error
                }
            case .playlists(let platform, let query):
                let result = try await service.publicPlaylists(platform: platform, query: query, cursor: cursor)
                guard result.items.count <= 30, result.total >= result.items.count,
                      result.items.allSatisfy({ $0.platform == platform && $0.kind == .playlist }),
                      Set(result.items.map(\.id)).count == result.items.count else { throw MusicServiceError.invalidResponse }
                let items = result.items.map { value in
                    MusicPublicBrowseItem(id: value.id, title: value.title, subtitle: value.creator, artwork: value.artwork, content: .collection(value))
                }
                return .init(page: .init(sections: [.init(id: "playlists", title: platform.title, items: items)], cursor: result.cursor), total: result.total)
            case .detail(let collection):
                let result = try await service.publicDetail(collection, cursor: cursor)
                let positions = result.members.map(\.sourcePosition)
                guard result.collection.id == collection.id, result.members.count <= 30,
                      (result.members.count...30).contains(result.consumed),
                      result.total >= offset + result.consumed, !result.snapshot.isEmpty,
                      result.cursor == nil || result.consumed > 0,
                      positions == positions.sorted(), Set(positions).count == positions.count,
                      positions.allSatisfy({ (offset..<(offset + result.consumed)).contains($0) }),
                      result.members.allSatisfy({ member in
                          !member.nativeID.isEmpty && member.track.sources.contains {
                              $0.platform == collection.platform.rawValue && $0.nativeId == member.nativeID
                          }
                      }) else { throw MusicServiceError.invalidResponse }
                let items = result.members.map { member in
                    let track = member.track
                    return MusicPublicBrowseItem(id: "\(member.sourcePosition):\(track.songId)", title: track.title,
                        subtitle: track.artist, artwork: track.artwork, content: .track(track))
                }
                return .init(page: .init(sections: [.init(id: collection.id, title: result.collection.title, items: items)],
                             cursor: result.cursor, notice: result.notice),
                             collection: result.collection, total: result.total, snapshot: result.snapshot, source: savedSource,
                             consumed: result.consumed, detailMembers: result.members)
            case .discovery(let destination):
                if destination == .home, let home = service as? any MusicHomeBrowsing {
                    let page = try await home.homePage(cursor: cursor, refresh: refresh)
                    return .init(page: page, consumed: page.consumed)
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
                                subtitle: track.artist, artwork: track.artwork, content: .track(track))
                        }
                        return .init(page: .init(sections: [.init(id: "daily", title: "每日推荐", items: items)], cursor: result.cursor),
                                     snapshot: source.id.uuidString, source: source)
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
            if own == pageEpoch { task = nil; isLoading = false }
        }
        do {
            let result = try await withTaskCancellationHandler { try await newTask.value } onCancel: { newTask.cancel() }
            unpublished = result.source
            try Task.checkCancellation()
            guard available, visible, generation == access, pageEpoch == own else {
                return
            }
            if isDaily {
                guard Calendar.current.isDate(day, inSameDayAs: Date()),
                      contentDay.map({ Calendar.current.isDate($0, inSameDayAs: Date()) }) == true else {
                    throw MusicServiceError.staleResult
                }
            }
            let count = result.page.sections.reduce(0) { $0 + $1.items.count }
            guard count <= (request == .discovery(.home) ? 600 : 300), result.page.filters.count <= 300,
                  result.consumed == nil || result.consumed! >= 0,
                  result.page.sections.reduce(0, { count, section in
                      count + (section.tabs.isEmpty ? section.items.count : section.tabs.reduce(0) { $0 + $1.items.count })
                  }) <= 600,
                  Set(result.page.sections.map(\.id)).count == result.page.sections.count,
                  result.page.sections.allSatisfy({ section in
                      Set(section.items.map(\.id)).count == section.items.count && section.filters.count <= 201 &&
                          Set(section.filters.map(\.id)).count == section.filters.count &&
                          section.tabs.count <= 30 && Set(section.tabs.map(\.id)).count == section.tabs.count &&
                          section.tabs.reduce(0, { $0 + $1.items.count }) <= 600 &&
                          section.tabs.allSatisfy { Set($0.items.map(\.id)).count == $0.items.count } &&
                          (section.tabs.isEmpty || section.tabs.first(where: { $0.id == section.selectedTab })?.items.map(\.id) == section.items.map(\.id))
                  }),
                  result.page.cursor == nil || result.page.cursor != cursor && (result.consumed ?? count) > 0 else {
                throw MusicServiceError.invalidResponse
            }
            if direction > 0, let next = result.page.cursor,
               next == currentCursor || back.contains(where: { $0 == next }) {
                throw MusicServiceError.invalidResponse
            }
            if direction != 0, let old = snapshot, old != result.snapshot { throw MusicServiceError.staleResult }
            sectionDiagnosticIDs = Dictionary(uniqueKeysWithValues: result.page.sections.compactMap { section in
                guard let error = section.error else { return nil }
                trace.record(.failed, failure: error)
                return (section.id, trace.logicalID)
            })
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
                if requestOverride == nil { contentIsCurrent = false }
                failedPage = (nil, 0); failedRequest = requestOverride
                return
            }
            if direction == 2 {
                merged = sections
                for section in result.page.sections {
                    if let index = merged.firstIndex(where: { $0.id == section.id && $0.layout == section.layout }) {
                        var ids = Set(merged[index].items.map(\.id))
                        let items = merged[index].items + section.items.filter { ids.insert($0.id).inserted }
                        merged[index] = .init(id: section.id, title: section.title, items: items, layout: section.layout,
                                              avatars: section.avatars, channel: section.channel,
                                              filters: section.filters, selectedFilter: section.selectedFilter, error: section.error,
                                              tabs: section.tabs, selectedTab: section.selectedTab)
                    } else { merged.append(section) }
                }
                if result.page.cursor != nil, result.consumed == nil,
                   merged.reduce(0, { $0 + $1.items.count }) == sections.reduce(0, { $0 + $1.items.count }) {
                    throw MusicServiceError.invalidResponse
                }
            }
            let songCount = result.page.sections.flatMap(\.items).filter {
                if case .track = $0.content { return true }; return false
            }.count
            let consumed = result.consumed ?? songCount
            if requestOverride == nil, let next = result.page.cursor, let saved = cursorOffsets[next],
               saved != offset + consumed { throw MusicServiceError.staleResult }
            // Validate the complete next state before publishing any cursor,
            // back-stack, or rows. A rejected append must remain retryable.
            if direction == 0 && cursor == nil { back = []; pageNumber = 1; cursorOffsets = ["": 0] }
            if direction > 0 { back.append(currentCursor); pageNumber += 1 }
            if direction == -1 { back.removeLast(); pageNumber -= 1 }
            if direction != 2 { trackOffset = offset }
            if let next = result.page.cursor { cursorOffsets[next] = offset + consumed }
            if direction != 2 {
                trackPageOccurrences = [:]
                detailSourcePositions = [:]
            }
            if let members = result.detailMembers {
                for member in members {
                    detailSourcePositions[member.track.ref] = member.sourcePosition
                    trackPageOccurrences[member.track.ref] = .init(
                        cursor: cursor ?? "", sourcePosition: member.sourcePosition
                    )
                }
            }
            currentCursor = cursor; nextCursor = result.page.cursor; sections = merged
            if requestOverride != nil { loadedPages = [:] }
            loadedPages[cursor ?? ""] = result
            self.request = request
            notice = result.page.notice
            filters = result.page.filters; selectedFilter = result.page.selectedFilter
            collection = result.collection; total = result.total; snapshot = result.snapshot; remoteSource = result.source; loaded = true
            contentIsCurrent = true
            if case .discovery = request { requiresExplicitNext = false }
            else { requiresExplicitNext = count == 0 && result.page.cursor != nil }
            if direction == 1 { usesPageNavigation = true }
            if cursor == nil || result.dailyStyle != nil { dailyStyle = result.dailyStyle }
            failedPage = nil; failedRequest = nil
            watchSource()
            trace.record(.completed)
        } catch {
            trace.failed(error)
            guard available, visible, generation == access, pageEpoch == own, !Task.isCancelled else { return }
            let safe = MusicServiceError.sanitized(error)
            if safe != .cancelled {
                self.error = safe; errorDiagnosticID = trace.logicalID
                failedPage = (cursor, direction); failedRequest = requestOverride
            }
            if safe.isAuthenticationFailure || [.accessUnavailable, .staleAccess, .staleResult, .notFound, .handleExpired].contains(safe) {
                loadedPages = [:]
                if let remoteSource { releaseSource(remoteSource) }; remoteSource = nil
                sections = []; notice = nil; nextCursor = nil; snapshot = nil; loaded = false
                collection = nil; total = nil; currentCursor = nil; back = []; pageNumber = 1; failedPage = nil; failedRequest = nil
                cursorOffsets = ["": 0]; trackOffset = 0
                trackPageOccurrences = [:]; detailSourcePositions = [:]
                contentIsCurrent = false
            }
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
        guard available, visible, contentIsCurrent, !isActing, !isLoading else { return nil }
        let id = UUID(); issuedIntent = id
        return .init(id: id, page: pageEpoch, access: generation)
    }
    public func perform(intent: MusicPublicBrowseActionIntent, operation: String = "歌单操作",
                        kind: MusicRequestTrace.Operation = .library,
                        _ body: @escaping @MainActor () async throws -> Void) async {
        guard available, visible, contentIsCurrent, !isActing, !isLoading, issuedIntent == intent.id,
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
}

@MainActor public extension MusicViewModel {
    /// Same NativeMusicService instance exposed through an existing public port;
    /// no new service/account/writer and no visibility change to private state.
    var publicBrowser: (any MusicPublicBrowsing)? { accountBrowser as? any MusicPublicBrowsing }
}
