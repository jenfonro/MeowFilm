import Foundation

/// Public catalog capability, not a declaration of account/login support.
public enum MusicPublicBrowsePlatform: String, Codable, CaseIterable, Sendable, Identifiable {
    case netease = "wy", qq = "tx", kugou = "kg", kuwo = "kw", migu = "mg"
    public var id: String { rawValue }
    public var title: String {
        switch self {
        case .netease: return "网易云音乐"
        case .qq: return "QQ 音乐"
        case .kugou: return "酷狗音乐"
        case .kuwo: return "酷我音乐"
        case .migu: return "咪咕音乐"
        }
    }
    /// Maps an account platform to its catalog platform.
    public init(accountPlatform: MusicAccountPlatform) {
        switch accountPlatform {
        case .netease: self = .netease
        case .qq: self = .qq
        case .kugou: self = .kugou
        }
    }
}

/// Catalog platforms include public providers without account capabilities.
public struct MusicPublicBrowseCollection: Sendable, Hashable, Identifiable {
    public enum Kind: String, Sendable, Codable { case playlist, album }
    public let platform: MusicPublicBrowsePlatform
    public let nativeID: String
    public let kind: Kind
    public let title: String
    public let creator: String
    public let artwork: URL?
    public let count: Int?
    public var id: String { "\(platform.rawValue):\(kind.rawValue):\(nativeID)" }
    public init(platform: MusicPublicBrowsePlatform, nativeID: String, kind: Kind = .playlist,
                title: String, creator: String = "", artwork: URL? = nil, count: Int? = nil) {
        self.platform = platform; self.nativeID = nativeID; self.kind = kind
        self.title = title; self.creator = creator; self.artwork = artwork; self.count = count
    }
}

public struct MusicPublicBrowseChannel: Sendable, Hashable, Identifiable {
    public enum Kind: String, Sendable {
        case featured, playlists, rankings, newSongs, cube, vip, unsupported
        case artistActivity = "artist-activity"
    }
    public let id: String
    public let title: String
    public let kind: Kind
    public let pageCode: String
    public init(id: String, title: String, kind: Kind, pageCode: String = "") {
        self.id = id; self.title = title; self.kind = kind; self.pageCode = pageCode
    }
}

public enum MusicPublicBrowseDestination: Sendable, Hashable {
    case home, featured, channels
    case channel(MusicPublicBrowseChannel, tag: String)
    /// Authenticated reads use the existing account port, never an anonymous fallback.
    case daily(MusicAccountPlatform)
}

public struct MusicPublicBrowseFilter: Sendable, Hashable, Identifiable {
    public let id: String
    public let title: String
    public let group: String
    public init(id: String, title: String, group: String = "") {
        self.id = id; self.title = title; self.group = group
    }
}

public enum MusicPublicBrowseContent: Sendable {
    case collection(MusicPublicBrowseCollection)
    case track(MusicRemoteTrack)
    case channel(MusicPublicBrowseChannel)
    case daily(MusicAccountPlatform)
    case action(MusicHomeAction)
    /// A known music entry that cannot be handled is not silently dropped or substituted.
    case unavailable
}
/// Display-only ranking metadata. Neither a preview nor its rank authorizes playback.
public struct MusicPublicBrowseRanking: Sendable {
    public struct Entry: Sendable {
        public let id: String
        public let title: String
        public let artist: String
        public let artwork: URL?
        public let rank: Int?
        public let lastRank: Int?
        public init(id: String, title: String, artist: String = "", artwork: URL? = nil,
                    rank: Int? = nil, lastRank: Int? = nil) {
            self.id = id; self.title = title; self.artist = artist; self.artwork = artwork
            self.rank = rank; self.lastRank = lastRank
        }
    }
    public let preview: [Entry]
    public let songArtwork: URL?
    public let updateFrequency: String
    public init(preview: [Entry] = [], songArtwork: URL? = nil, updateFrequency: String = "") {
        self.preview = Array(preview.prefix(3)); self.songArtwork = songArtwork; self.updateFrequency = updateFrequency
    }
}

public struct MusicPublicBrowseItem: Sendable, Identifiable {
    public let id: String
    public let title: String
    public let subtitle: String
    public let artwork: URL?
    public let content: MusicPublicBrowseContent
    public let ranking: MusicPublicBrowseRanking?
    public let previewSongs: [MusicRemoteTrack]
    public let recommendationReason: String
    public let coverLabel: String
    public init(id: String, title: String, subtitle: String = "", artwork: URL? = nil, content: MusicPublicBrowseContent,
                ranking: MusicPublicBrowseRanking? = nil, previewSongs: [MusicRemoteTrack] = [],
                recommendationReason: String = "", coverLabel: String = "") {
        self.id = id; self.title = title; self.subtitle = subtitle; self.artwork = artwork; self.content = content
        self.ranking = ranking; self.previewSongs = Array(previewSongs.prefix(3))
        self.recommendationReason = recommendationReason
        self.coverLabel = coverLabel
    }
}
public struct MusicPublicBrowseSectionTab: Sendable, Identifiable {
    public let id: String
    public let title: String
    public let items: [MusicPublicBrowseItem]
    public init(id: String, title: String, items: [MusicPublicBrowseItem]) {
        self.id = id; self.title = title; self.items = items
    }
}
public struct MusicPublicBrowseSection: Sendable, Identifiable {
    public enum Layout: String, Sendable {
        case list, featureRail, playlistRail, songColumns, grid, banner, artistUpdates
        case rankingStack, rankedSongs, rankingSmall
        case vipSongs, vipRankedSongs
    }
    public let id: String
    public let title: String
    public let items: [MusicPublicBrowseItem]
    public let layout: Layout
    public let avatars: [URL]
    public let channel: MusicPublicBrowseChannel?
    /// Section-local song filters are not the channel's navigation tabs.
    public let filters: [MusicPublicBrowseFilter]
    public let selectedFilter: String
    public let error: MusicServiceError?
    public let tabs: [MusicPublicBrowseSectionTab]
    public let selectedTab: String
    public init(id: String, title: String, items: [MusicPublicBrowseItem], layout: Layout = .list,
                avatars: [URL] = [], channel: MusicPublicBrowseChannel? = nil,
                filters: [MusicPublicBrowseFilter] = [], selectedFilter: String = "", error: MusicServiceError? = nil,
                tabs: [MusicPublicBrowseSectionTab] = [], selectedTab: String = "") {
        self.id = id; self.title = title; self.items = items
        self.layout = layout; self.avatars = avatars; self.channel = channel
        self.filters = filters; self.selectedFilter = selectedFilter; self.error = error
        self.tabs = tabs; self.selectedTab = selectedTab
    }
}
public struct MusicPublicBrowsePage: Sendable {
    public let sections: [MusicPublicBrowseSection]
    public let cursor: String?
    public let filters: [MusicPublicBrowseFilter]
    public let selectedFilter: String
    public let notice: String?
    public init(sections: [MusicPublicBrowseSection], cursor: String? = nil,
                filters: [MusicPublicBrowseFilter] = [], selectedFilter: String = "", notice: String? = nil) {
        self.sections = sections; self.cursor = cursor; self.filters = filters; self.selectedFilter = selectedFilter
        self.notice = notice
    }
}
public struct MusicPublicBrowseCollectionPage: Sendable {
    public let items: [MusicPublicBrowseCollection]
    public let total: Int
    public let cursor: String?
    public init(items: [MusicPublicBrowseCollection], total: Int, cursor: String? = nil) {
        self.items = items; self.total = total; self.cursor = cursor
    }
}
public struct MusicPublicBrowseDetail: Sendable {
    public let collection: MusicPublicBrowseCollection
    public let tracks: [MusicRemoteTrack]
    public let total: Int
    public let cursor: String?
    public let snapshot: String
    public let notice: String?
    /// Number of source positions examined, including unavailable members.
    /// A nonterminal page may contain no visible tracks, but must advance.
    public let consumed: Int
    public init(collection: MusicPublicBrowseCollection, tracks: [MusicRemoteTrack], total: Int,
                cursor: String?, snapshot: String, notice: String? = nil, consumed: Int? = nil) {
        self.collection = collection; self.tracks = tracks; self.total = total
        self.cursor = cursor; self.snapshot = snapshot; self.notice = notice
        self.consumed = consumed ?? tracks.count
    }
}
public protocol MusicPublicBrowsing: Sendable {
    func publicPlaylists(platform: MusicPublicBrowsePlatform, query: String, cursor: String?) async throws -> MusicPublicBrowseCollectionPage
    func publicDetail(_ collection: MusicPublicBrowseCollection, cursor: String?) async throws -> MusicPublicBrowseDetail
    func publicDiscovery(_ destination: MusicPublicBrowseDestination, cursor: String?) async throws -> MusicPublicBrowsePage
}
public protocol MusicPublicBrowseRefreshing: Sendable {
    func refreshPublicCollection(_ collection: MusicPublicBrowseCollection) async
}

public extension MusicPublicBrowsing {
    func publicPlaylists(accountPlatform: MusicAccountPlatform, query: String, cursor: String?) async throws -> MusicPublicBrowseCollectionPage {
        try await publicPlaylists(platform: .init(accountPlatform: accountPlatform), query: query, cursor: cursor)
    }
}

/// The queue owner consumes native rows in one atomic queue transaction; these
/// must NOT be converted to thousands of expiring per-page handles.
public struct MusicPublicBrowseQueueRequest: Sendable {
    public let collection: MusicPublicBrowseCollection
    public let snapshot: String
    public init(collection: MusicPublicBrowseCollection, snapshot: String) {
        self.collection = collection; self.snapshot = snapshot
    }
}
public protocol MusicPublicBrowseQueueLoading: Sendable {
    func publicQueueTracks(_ request: MusicPublicBrowseQueueRequest) async throws -> [MusicNativeTrackData]
    func openPublicQueue(_ request: MusicPublicBrowseQueueRequest) async throws -> MusicRemoteQueueSource
}

/// Optional extension of the already assembled anonymous QQ owner.
public protocol MusicPublicBrowseQQProviding: MusicPublicQQProviding {
    func publicBrowsePlaylists(query: String, page: Int) async throws -> MusicPublicBrowseQQSearch
    func publicBrowsePlaylist(id: String) async throws -> MusicPublicBrowseNativeCollection
}
public struct MusicPublicBrowseQQSearch: Sendable {
    public let items: [MusicPublicBrowseCollection]
    public let total: Int
    public let nextPage: Int?
    public init(items: [MusicPublicBrowseCollection], total: Int, nextPage: Int?) {
        self.items = items; self.total = total; self.nextPage = nextPage
    }
}
public struct MusicPublicBrowseNativeCollection: Sendable {
    public let collection: MusicPublicBrowseCollection
    public let tracks: [MusicNativeTrackData]
    public let snapshot: String
    public init(collection: MusicPublicBrowseCollection, tracks: [MusicNativeTrackData], snapshot: String) {
        self.collection = collection; self.tracks = tracks; self.snapshot = snapshot
    }
}
