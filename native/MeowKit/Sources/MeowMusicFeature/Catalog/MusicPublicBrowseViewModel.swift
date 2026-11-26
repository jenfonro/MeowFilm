import Foundation
import Combine
import MeowDebug

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
    let id: UUID
    let page: UUID
}

/// Owns page tasks only, not a library, account session, source selection or player.
@MainActor public final class MusicPublicBrowseViewModel: ObservableObject {
    @Published public internal(set) var sections: [MusicPublicBrowseSection] = []
    @Published public internal(set) var filters: [MusicPublicBrowseFilter] = []
    @Published public internal(set) var selectedFilter = ""
    @Published public internal(set) var collection: MusicPublicBrowseCollection?
    @Published public internal(set) var total: Int?
    @Published public internal(set) var isLoading = false
    @Published public internal(set) var isActing = false
    @Published public internal(set) var error: MusicServiceError?
    @Published public internal(set) var errorDiagnosticID: String?
    @Published public internal(set) var sectionDiagnosticIDs: [String: String] = [:]
    @Published public internal(set) var notice: String?
    @Published public internal(set) var actionError: MusicServiceError?
    @Published public internal(set) var actionDiagnosticID: String?
    @Published public internal(set) var actionOperation = "歌单操作"
    @Published public internal(set) var requiresExplicitNext = false
    @Published public internal(set) var usesPageNavigation = false
    @Published public internal(set) var loaded = false
    @Published public internal(set) var contentIsCurrent = false
    @Published public internal(set) var pageNumber = 1
    @Published public internal(set) var nextCursor: String?
    @Published public internal(set) var remoteSource: MusicRemoteQueueSource?
    @Published public internal(set) var dailyStyle: MusicDailyStyleSelection?
    /// Offset of the first displayed song in the source snapshot. Account
    /// pages are not necessarily 30 rows; never infer this from pageNumber.
    public internal(set) var trackOffset = 0
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
    public var needsDateRefresh: Bool {
        guard isDaily, let contentDay else { return false }
        return !Calendar.current.isDate(contentDay, inSameDayAs: Date())
    }

    let service: any MusicPublicBrowsing
    @Published public internal(set) var request: MusicPublicBrowseRequest
    @Published var previewResetRevision = 0
    var pageEpoch = UUID(), actionEpoch = UUID(), loadEpoch = UUID()
    var homeNeedsRefresh = true
    var playlistNeedsRefresh = true
    var displayedHomeSource: MusicHomeSourceSelection?
    var isHome: Bool { request == .discovery(.home) }
    /// Optional private artwork follows the settled home, not retained cards
    /// that are about to be replaced. This never gates user actions.
    var canLoadDisplayPreviews: Bool { !isHome || (!homeNeedsRefresh && !isLoading) }
    /// Module-local identity for page-owned presentations, never queue authority.
    var actionPageID: UUID { pageEpoch }
    static let discoveryCacheTTL: TimeInterval = 3600
    var closed = false, visible = false, displayVisible = false
    var contentDay: Date?
    var contentLoadedAt: Date?
    var isDiscoveryExpired: Bool {
        guard loaded, let contentLoadedAt else { return false }
        switch request {
        case .discovery(.channel), .discovery(.featured), .discovery(.channels):
            return Date().timeIntervalSince(contentLoadedAt) >= Self.discoveryCacheTTL
        default:
            return false
        }
    }
    var isDaily: Bool {
        switch request {
        case .discovery(.daily), .homeAction(.daily, _): return true
        case .accountRecommendation(_, let kind, _): return kind == "daily"
        default: return false
        }
    }
    var currentCursor: String?
    var cursorOffsets: [String: Int] = ["": 0]
    var detailSourcePositions: [MusicReference: Int] = [:]
    var requestPlatform: MusicPublicBrowsePlatform? {
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
    var failedPage: (cursor: String?, direction: Int)?
    var failedRequest: MusicPublicBrowseRequest?
    var back: [String?] = []
    var loadedPages: [String: Loaded] = [:]
    var homeTask: (id: UUID, work: Task<Void, Never>)?
    var task: Task<Loaded, Error>?
    var tabTask: Task<[MusicPublicBrowseItem], Error>?
    var actionTask: Task<Void, Error>?
    var revocationTask: Task<Void, Never>?
    var issuedIntent: UUID?
    var songObservation: Task<Void, Never>?
    var songProjectionID = UUID()
    enum ArtworkReadState {
        case loading, settled(URL?), failed(MusicServiceError)
    }
    var artworkReads: [MusicPublicBrowseItem.CoverReference: ArtworkReadState] = [:]
    var artworkDemand: Set<MusicPublicBrowseItem.CoverReference> = []
    var artworkEpoch = UUID()
    var artworkTask: Task<Void, Never>?
    let queuePage: (@MainActor (MusicRemoteQueueSource, String?) async throws -> MusicBrowsePage)?
    let releaseSource: @MainActor (MusicRemoteQueueSource) -> Void

    struct Loaded: Sendable {
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

    public func close() {
        guard !closed else { return }
        closed = true; visible = false; displayVisible = false
        songObservation?.cancel(); songObservation = nil
        invalidate(clear: true)
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
        guard !closed else { return }
        if !visible, case .detail(let collection) = request, collection.kind == .playlist { playlistNeedsRefresh = true }
        visible = true; displayVisible = true
        publishArtwork()
        observeSongs()
    }

    /// Resource ownership and actual display demand are separate. A retained
    /// page may keep its owner active while a covered page must not refill
    /// display-only content.
    public func setDisplayVisible(_ value: Bool) {
        guard !closed else {
            displayVisible = false
            artworkDemand = []
            return
        }
        displayVisible = value
        if value { publishArtwork(); loadArtworkDemand() }
        else { artworkDemand = [] }
    }

    /// Visibility only checks date-scoped data; it never suspends page work.
    public func refreshForDateIfNeeded() async {
        guard !closed, visible, displayVisible, needsDateRefresh else { return }
        invalidate(clear: true)
        await load(cursor: nil, direction: 0, refresh: true)
    }

    public func disappear() {
        visible = false; displayVisible = false
        songObservation?.cancel(); songObservation = nil
        invalidate(clear: true)
    }

    func invalidateCachedDiscovery() {
        retireArtwork()
        if isHome { homeNeedsRefresh = true }
        if loaded { contentLoadedAt = .distantPast }
    }

    func invalidate(clear: Bool) {
        retireArtwork()
        previewResetRevision &+= 1
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
            contentLoadedAt = nil
            homeNeedsRefresh = true
            displayedHomeSource = nil
        }
    }

    /// Home display and captured actions outlive a background read. Only a
    /// navigation/source/access change retires their page identity.
    func invalidateHomeRead() {
        retireArtwork()
        previewResetRevision &+= 1
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

    func retireHomeAccountEntries() {
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

    func watchSource() {
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

    deinit {
        songObservation?.cancel()
        artworkTask?.cancel()
        homeTask?.work.cancel()
        task?.cancel()
        tabTask?.cancel()
        actionTask?.cancel()
        revocationTask?.cancel()
    }
}

@MainActor public extension MusicViewModel {
    /// Same NativeMusicService instance exposed through an existing public port;
    /// no new service/account/writer and no visibility change to private state.
    var publicBrowser: (any MusicPublicBrowsing)? { accountBrowser as? any MusicPublicBrowsing }
}
