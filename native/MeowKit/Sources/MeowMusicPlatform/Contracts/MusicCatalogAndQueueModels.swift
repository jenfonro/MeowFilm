import Foundation

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
    public init(accountPlatform: MusicAccountPlatform) {
        switch accountPlatform {
        case .netease: self = .netease
        case .qq: self = .qq
        case .kugou: self = .kugou
        }
    }
}

public struct MusicPublicBrowseCollection: Codable, Sendable, Hashable, Identifiable {
    public enum Kind: String, Sendable, Codable { case playlist, album }
    public let platform: MusicPublicBrowsePlatform
    public let nativeID: String
    public let kind: Kind
    public let title: String
    public let creator: String
    public let artwork: URL?
    public let count: Int?
    public let playCount: Int?
    public var id: String { "\(platform.rawValue):\(kind.rawValue):\(nativeID)" }
    public init(platform: MusicPublicBrowsePlatform, nativeID: String, kind: Kind = .playlist,
                title: String, creator: String = "", artwork: URL? = nil, count: Int? = nil, playCount: Int? = nil) {
        self.platform = platform; self.nativeID = nativeID; self.kind = kind
        self.title = title; self.creator = creator; self.artwork = artwork; self.count = count
        self.playCount = playCount
    }
}

public struct MusicPublicBrowseChannel: Codable, Sendable, Hashable, Identifiable {
    public enum Kind: String, Codable, Sendable {
        case featured, playlists, rankings, artists, chart, cube, vip, unsupported
        case newSongs = "new-songs"
        case artistActivity = "artist-activity"
    }
    public let id: String
    public let title: String
    public let kind: Kind
    public let pageCode: String
    public let platform: MusicPublicBrowsePlatform
    public var identity: String { "\(platform.rawValue):\(id)" }
    public init(id: String, title: String, kind: Kind, pageCode: String = "",
                platform: MusicPublicBrowsePlatform = .netease) {
        self.id = id; self.title = title; self.kind = kind; self.pageCode = pageCode; self.platform = platform
    }
}

public enum MusicPublicBrowseDestination: Sendable, Hashable {
    case home, featured, channels
    case channel(MusicPublicBrowseChannel, tag: String)
    case daily(MusicAccountPlatform)
}

public struct MusicPublicBrowseFilter: Codable, Sendable, Hashable, Identifiable {
    public let id: String
    public let title: String
    public let group: String
    public let selected: Bool
    public init(id: String, title: String, group: String = "", selected: Bool = false) {
        self.id = id; self.title = title; self.group = group; self.selected = selected
    }
}

public enum MusicPublicBrowseContent: Sendable {
    case collection(MusicPublicBrowseCollection)
    case track(MusicRemoteTrack)
    case channel(MusicPublicBrowseChannel)
    case daily(MusicAccountPlatform)
    case action(MusicHomeAction)
    case unavailable
}

public struct MusicPublicBrowseRanking: Codable, Sendable {
    public struct Entry: Codable, Sendable {
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
    public enum Kind: String, Codable, Sendable {
        case song, playlist, album, artist, channel, chart, daily, fm, similar, heartbeat, unsupported
        case songCollection = "song-collection"
    }
    public struct CoverReference: Codable, Sendable, Hashable {
        public let platform: MusicPublicBrowsePlatform
        public let kind: MusicPublicBrowseCollection.Kind
        public let id: String
        public init(platform: MusicPublicBrowsePlatform, kind: MusicPublicBrowseCollection.Kind = .playlist, id: String) {
            self.platform = platform; self.kind = kind; self.id = id
        }
    }
    public let id: String
    public let title: String
    public let subtitle: String
    public let artwork: URL?
    public let content: MusicPublicBrowseContent
    public let ranking: MusicPublicBrowseRanking?
    public let previewSongs: [MusicRemoteTrack]
    public let coverLabel: String
    public let requiresAccount: Bool
    public let kind: Kind?
    public let label: String
    public let contextText: String
    public let playCount: Int?
    public let presentation: String
    public let coverRef: CoverReference?
    public init(id: String, title: String, subtitle: String = "", artwork: URL? = nil, content: MusicPublicBrowseContent,
                ranking: MusicPublicBrowseRanking? = nil, previewSongs: [MusicRemoteTrack] = [],
                coverLabel: String = "", requiresAccount: Bool = false,
                kind: Kind? = nil, label: String = "", presentation: String = "", coverRef: CoverReference? = nil,
                contextText: String = "", playCount: Int? = nil) {
        self.id = id; self.title = title; self.subtitle = subtitle; self.artwork = artwork; self.content = content
        self.ranking = ranking; self.previewSongs = Array(previewSongs.prefix(3))
        self.coverLabel = coverLabel; self.requiresAccount = requiresAccount
        self.contextText = contextText; self.playCount = playCount
        self.kind = kind; self.label = label; self.presentation = presentation; self.coverRef = coverRef
    }
}

public struct MusicPublicBrowseSectionTab: Sendable, Identifiable {
    public let id: String
    public let title: String
    public let items: [MusicPublicBrowseItem]
    public let loadItems: (@Sendable () async throws -> [MusicPublicBrowseItem])?
    public init(id: String, title: String, items: [MusicPublicBrowseItem],
                loadItems: (@Sendable () async throws -> [MusicPublicBrowseItem])? = nil) {
        self.id = id; self.title = title; self.items = items; self.loadItems = loadItems
    }
}

public struct MusicPublicBrowseSection: Sendable, Identifiable {
    public enum Layout: String, Codable, Sendable {
        case list = "song-list", featureRail = "feature-rail", playlistRail = "playlist-rail"
        case songColumns = "song-columns", grid = "playlist-grid", banner
        case artistGrid = "artist-grid", channelGrid = "channel-grid", artistUpdates = "artist-updates"
        case rankingStack = "ranking", rankedSongs = "ranked-songs", rankingSmall = "ranking-small"
        case vipSongs = "vip-songs", vipRankedSongs = "vip-ranked-songs", albumRanking = "album-ranking"
    }
    public let id: String
    public let title: String
    public let items: [MusicPublicBrowseItem]
    public let layout: Layout
    public let avatars: [URL]
    public let channel: MusicPublicBrowseChannel?
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
    public let consumed: Int?
    public init(sections: [MusicPublicBrowseSection], cursor: String? = nil,
                filters: [MusicPublicBrowseFilter] = [], selectedFilter: String = "", notice: String? = nil,
                consumed: Int? = nil) {
        self.sections = sections; self.cursor = cursor; self.filters = filters; self.selectedFilter = selectedFilter
        self.notice = notice; self.consumed = consumed
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

public struct MusicPublicBrowseMember: Sendable {
    public let nativeID: String
    public let sourcePosition: Int?
    public let track: MusicRemoteTrack
    public init(nativeID: String, sourcePosition: Int?, track: MusicRemoteTrack) {
        self.nativeID = nativeID; self.sourcePosition = sourcePosition; self.track = track
    }
}

public struct MusicPublicBrowseDetail: Sendable {
    public let collection: MusicPublicBrowseCollection
    public let members: [MusicPublicBrowseMember]
    public var tracks: [MusicRemoteTrack] { members.map(\.track) }
    public let total: Int
    public let cursor: String?
    public let notice: String?
    public let consumed: Int
    public init(collection: MusicPublicBrowseCollection, members: [MusicPublicBrowseMember], total: Int,
                cursor: String?, notice: String? = nil, consumed: Int? = nil) {
        self.collection = collection; self.members = members; self.total = total
        self.cursor = cursor; self.notice = notice
        self.consumed = consumed ?? members.count
    }
}

public struct MusicPublicBrowseQueueRequest: Sendable {
    public let collection: MusicPublicBrowseCollection
    public init(collection: MusicPublicBrowseCollection) { self.collection = collection }
}

public struct MusicPublicBrowseCollectionSearch: Sendable {
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
    public init(collection: MusicPublicBrowseCollection, tracks: [MusicNativeTrackData]) {
        self.collection = collection; self.tracks = tracks
    }
}

public enum MusicHomeAction: Codable, Hashable, Sendable {
    public enum FMMode: String, Codable, Hashable, Sendable {
        case `default` = "DEFAULT", familiar = "FAMILIAR", explore = "EXPLORE"
        case puzzle = "PUZZLE_MODE_RCMD", scene = "SCENE_RCMD"
    }
    public enum FMEntry: String, Codable, Hashable, Sendable { case channel }
    public enum DailyBranch: String, Codable, Hashable, Sendable { case `default`, style }

    case fm(mode: FMMode, submode: String, entry: FMEntry?)
    case ruleFM(ruleID: String, tabTagID: String, sourceName: String)
    case similar(sourceIDs: [String], sourceName: String)
    case similarArtist(sourceIDs: [String], sourceName: String)
    case daily(branch: DailyBranch)
    case heartbeat
    case heartbeatSong(songID: String)
    case songCollection(ids: [String])

    public var isSimilar: Bool {
        switch self { case .similar, .similarArtist: return true; default: return false }
    }
    public var isHeartbeat: Bool {
        switch self { case .heartbeat, .heartbeatSong: return true; default: return false }
    }
    public var isPersonalFM: Bool {
        switch self { case .fm, .ruleFM: return true; default: return false }
    }
    public var fmConfiguration: MusicHomeAction? {
        switch self {
        case .fm(let mode, let submode, _): return .fm(mode: mode, submode: submode, entry: nil)
        case .ruleFM(let rule, let tab, _): return .ruleFM(ruleID: rule, tabTagID: tab, sourceName: "")
        default: return nil
        }
    }

    private enum CodingKeys: String, CodingKey {
        case type, provider, mode, submode, entry, ruleId, tabTagId, sourceName
        case sourceType, sourceIds, branch, ids, songId
    }
    public init(from decoder: Decoder) throws {
        let data = try decoder.container(keyedBy: CodingKeys.self)
        switch try data.decode(String.self, forKey: .type) {
        case "fm":
            let provider = try data.decodeIfPresent(String.self, forKey: .provider) ?? ""
            if provider == "rule" {
                self = .ruleFM(ruleID: try data.decode(String.self, forKey: .ruleId),
                               tabTagID: try data.decode(String.self, forKey: .tabTagId),
                               sourceName: try data.decodeIfPresent(String.self, forKey: .sourceName) ?? "")
            } else {
                guard provider.isEmpty else {
                    throw DecodingError.dataCorruptedError(forKey: .provider, in: data, debugDescription: "Unknown FM provider")
                }
                self = .fm(mode: try data.decode(FMMode.self, forKey: .mode),
                           submode: try data.decode(String.self, forKey: .submode),
                           entry: try data.decodeIfPresent(FMEntry.self, forKey: .entry))
            }
        case "similar":
            let type = try data.decode(String.self, forKey: .sourceType)
            let ids = try data.decode([String].self, forKey: .sourceIds)
            let name = try data.decodeIfPresent(String.self, forKey: .sourceName) ?? ""
            switch type {
            case "song": self = .similar(sourceIDs: ids, sourceName: name)
            case "artist": self = .similarArtist(sourceIDs: ids, sourceName: name)
            default: throw MusicServiceError.invalidRequest
            }
        case "daily": self = .daily(branch: try data.decode(DailyBranch.self, forKey: .branch))
        case "heartbeat":
            if let id = try data.decodeIfPresent(String.self, forKey: .songId) { self = .heartbeatSong(songID: id) }
            else { self = .heartbeat }
        case "song-collection":
            self = .songCollection(ids: try data.decode([String].self, forKey: .ids))
        default:
            throw DecodingError.dataCorruptedError(forKey: .type, in: data, debugDescription: "Unknown home action")
        }
    }
    public func encode(to encoder: Encoder) throws {
        var data = encoder.container(keyedBy: CodingKeys.self)
        switch self {
        case .fm(let mode, let submode, let entry):
            try data.encode("fm", forKey: .type)
            try data.encode(mode, forKey: .mode)
            try data.encode(submode, forKey: .submode)
            try data.encodeIfPresent(entry, forKey: .entry)
        case .ruleFM(let ruleID, let tabTagID, let sourceName):
            try data.encode("fm", forKey: .type)
            try data.encode("rule", forKey: .provider)
            try data.encode(ruleID, forKey: .ruleId)
            try data.encode(tabTagID, forKey: .tabTagId)
            try data.encode(sourceName, forKey: .sourceName)
        case .similar(let sourceIDs, let sourceName):
            try data.encode("similar", forKey: .type)
            try data.encode("song", forKey: .sourceType)
            try data.encode(sourceIDs, forKey: .sourceIds)
            if !sourceName.isEmpty { try data.encode(sourceName, forKey: .sourceName) }
        case .similarArtist(let sourceIDs, let sourceName):
            try data.encode("similar", forKey: .type)
            try data.encode("artist", forKey: .sourceType)
            try data.encode(sourceIDs, forKey: .sourceIds)
            if !sourceName.isEmpty { try data.encode(sourceName, forKey: .sourceName) }
        case .heartbeatSong(let id):
            try data.encode("heartbeat", forKey: .type)
            try data.encode(id, forKey: .songId)
        case .daily(let branch):
            try data.encode("daily", forKey: .type)
            try data.encode(branch, forKey: .branch)
        case .heartbeat: try data.encode("heartbeat", forKey: .type)
        case .songCollection(let ids):
            try data.encode("song-collection", forKey: .type)
            try data.encode(ids, forKey: .ids)
        }
    }
}

public struct MusicHomeSourceSelection: Sendable, Equatable {
    public let selected: MusicPublicBrowsePlatform
    public let revision: Int
    public let available: [MusicPublicBrowsePlatform]
    public init(selected: MusicPublicBrowsePlatform, revision: Int, available: [MusicPublicBrowsePlatform]) {
        self.selected = selected; self.revision = revision; self.available = available
    }
}

public struct MusicNativeHomeBinding: Sendable, Equatable {
    public let scope: String
    public let revision: UUID
    public let loggedIn: Bool
    public let authorization: MusicQueueAuthorization

    public init(scope: String, revision: UUID, loggedIn: Bool,
                authorization: MusicQueueAuthorization = MusicQueueAuthorization()) {
        self.scope = scope; self.revision = revision; self.loggedIn = loggedIn
        self.authorization = authorization
    }
    public static func == (lhs: Self, rhs: Self) -> Bool {
        lhs.scope == rhs.scope && lhs.revision == rhs.revision && lhs.loggedIn == rhs.loggedIn &&
            lhs.authorization === rhs.authorization
    }
}

public struct MusicNativeHomeContinuation: Sendable, Equatable {
    public let cursor: String
    public let blockCodeOrderList: String?
    public let loadedPositionCodes: [String]

    public init(cursor: String, blockCodeOrderList: String? = nil,
                loadedPositionCodes: [String] = []) throws {
        self.cursor = cursor
        self.blockCodeOrderList = blockCodeOrderList
        self.loadedPositionCodes = loadedPositionCodes
    }
}

public enum MusicNativeHomeRequest: Sendable, Equatable {
    case home(continuation: MusicNativeHomeContinuation? = nil)
    case featured(continuation: MusicNativeHomeContinuation? = nil)
    case artistActivity(before: String? = nil)
    case vipSongs
    case songMetadata(ids: [String])
    case playlistMetadata(ids: [String])
    case heartbeatSeed
}

public struct MusicDailyStyleTag: Sendable, Equatable, Identifiable {
    public let id: String
    public let name: String
    public init(id: String, name: String) { self.id = id; self.name = name }
}

public struct MusicDailyStyleCategory: Sendable, Equatable, Identifiable {
    public let id: String
    public let name: String
    public let tags: [MusicDailyStyleTag]
    public init(id: String, name: String, tags: [MusicDailyStyleTag]) {
        self.id = id; self.name = name; self.tags = tags
    }
}

public struct MusicDailyStyleSelection: Sendable, Equatable {
    public let category: String
    public let tags: [MusicDailyStyleTag]
    public init(category: String, tags: [MusicDailyStyleTag]) { self.category = category; self.tags = tags }
    public func validate(in categories: [MusicDailyStyleCategory]) throws {
        guard let value = categories.first(where: { $0.id == category }), !tags.isEmpty,
              Set(tags.map(\.id)).count == tags.count,
              tags.allSatisfy({ tag in value.tags.contains { $0.id == tag.id } }) else {
            throw MusicServiceError.invalidRequest
        }
    }
}

public struct MusicDailyStyleSave: Sendable {
    public let page: MusicNativeTrackPage
    public let saveConfirmed: Bool
    public let selectionChanged: Bool
    public init(page: MusicNativeTrackPage, saveConfirmed: Bool, selectionChanged: Bool) {
        self.page = page; self.saveConfirmed = saveConfirmed; self.selectionChanged = selectionChanged
    }
}

public struct MusicDailyStyleEditing: Sendable {
    public let scope: String
    public let categories: @Sendable () async throws -> [MusicDailyStyleCategory]
    public let save: @Sendable (MusicDailyStyleSelection) async throws -> MusicDailyStyleSave
    public init(scope: String, categories: @escaping @Sendable () async throws -> [MusicDailyStyleCategory],
                save: @escaping @Sendable (MusicDailyStyleSelection) async throws -> MusicDailyStyleSave) {
        self.scope = scope; self.categories = categories; self.save = save
    }
}

public struct MusicNativeFMResponse: Sendable {
    public let tracks: [MusicNativeTrackData]
    public let artwork: URL?
    public init(tracks: [MusicNativeTrackData] = [], artwork: URL? = nil) {
        self.tracks = tracks; self.artwork = artwork
    }
}

public struct MusicFMPreview: Sendable {
    public let artwork: URL?
    public let authorization: MusicQueueAuthorization
    public init(artwork: URL?, authorization: MusicQueueAuthorization) {
        self.artwork = artwork; self.authorization = authorization
    }
}

public struct MusicFMPlaybackSnapshot: Sendable, Equatable {
    public let id: UUID
    public let action: MusicHomeAction
    public let continueEnabled: Bool
    public let loading: Bool
    public let error: MusicServiceError?
    public let errorID: UUID?
    public init(id: UUID, action: MusicHomeAction, continueEnabled: Bool, loading: Bool, error: MusicServiceError?, errorID: UUID? = nil) {
        self.id = id; self.action = action; self.continueEnabled = continueEnabled; self.loading = loading; self.error = error; self.errorID = errorID
    }
}

public final class MusicQueueAuthorization: @unchecked Sendable, CustomStringConvertible, CustomReflectable {
    private let lock = NSRecursiveLock()
    private var active = true
    private var observers: [UUID: AsyncStream<Void>.Continuation] = [:]
    private var children: [UUID: @Sendable () -> Void] = [:]
    private let parent: MusicQueueAuthorization?
    private let parentRegistration = UUID()
    public init() { parent = nil }
    public init(parent: MusicQueueAuthorization) {
        self.parent = parent
        parent.addChild(parentRegistration) { [weak self] in self?.revoke() }
    }
    public var isActive: Bool {
        guard parent?.isActive != false else { return false }
        lock.lock(); defer { lock.unlock() }; return active
    }
    public func revoke() {
        lock.lock(); active = false
        let values = Array(observers.values), descendants = Array(children.values)
        observers.removeAll(); children.removeAll(); lock.unlock()
        for revoke in descendants { revoke() }
        for value in values { value.yield(()); value.finish() }
    }
    private func addChild(_ id: UUID, revoke: @escaping @Sendable () -> Void) {
        lock.lock()
        if active { children[id] = revoke; lock.unlock() }
        else { lock.unlock(); revoke() }
    }
    private func removeChild(_ id: UUID) {
        lock.lock(); children.removeValue(forKey: id); lock.unlock()
    }
    public func revocations() -> AsyncStream<Void> {
        AsyncStream { continuation in
            let id = UUID()
            lock.lock()
            if active { observers[id] = continuation; lock.unlock() }
            else { lock.unlock(); continuation.yield(()); continuation.finish() }
            continuation.onTermination = { [weak self] _ in self?.removeObserver(id) }
        }
    }
    private func removeObserver(_ id: UUID) {
        lock.lock(); observers.removeValue(forKey: id); lock.unlock()
    }
    public func check() throws {
        try perform {}
    }
    public func register(_ id: UUID, cancel: @escaping @Sendable () -> Void) throws {
        do { try perform { children[id] = cancel } }
        catch { cancel(); throw error }
    }
    public func remove(_ id: UUID) {
        removeChild(id)
    }
    public func perform<T>(_ operation: () throws -> T) throws -> T {
        if let parent { return try parent.perform { try performLocal(operation) } }
        return try performLocal(operation)
    }
    private func performLocal<T>(_ operation: () throws -> T) throws -> T {
        lock.lock(); defer { lock.unlock() }
        guard active else { throw MusicServiceError.staleAccess }
        return try operation()
    }
    deinit {
        parent?.removeChild(parentRegistration)
    }
    public var description: String { "MusicQueueAuthorization" }
    public var customMirror: Mirror { Mirror(self, children: [:]) }
}

public enum MusicAccountQueueKind: Sendable, Equatable {
    case collection(id: String)
    case recommendation(kind: String)
}

public final class MusicAccountQueueLease: @unchecked Sendable, Identifiable, CustomStringConvertible, CustomReflectable {
    public let id: UUID
    public let platform: MusicAccountPlatform
    public let authorization: MusicQueueAuthorization
    public init(id: UUID = UUID(), platform: MusicAccountPlatform) {
        self.id = id; self.platform = platform; authorization = MusicQueueAuthorization()
    }
    public func revoke() { authorization.revoke() }
    public var description: String { "MusicAccountQueueLease" }
    public var customMirror: Mirror { Mirror(self, children: [:]) }
}

public struct MusicRemoteQueueContext: Sendable, Equatable {
    public enum Kind: String, Sendable { case playlist, favorites, recommendation, album, fm }
    public let kind: Kind
    public let id: String
    public let title: String
    public let platform: String
    public let unavailableCount: Int
    public let collection: MusicPublicBrowseCollection?
    public var availabilityNotice: String? {
        unavailableCount > 0 ? "\(unavailableCount) 首歌曲当前不可见或不可用" : nil
    }
    public init(kind: Kind, id: String, title: String, platform: String, unavailableCount: Int = 0,
                collection: MusicPublicBrowseCollection? = nil) {
        self.kind = kind; self.id = id; self.title = title; self.platform = platform
        self.unavailableCount = max(0, unavailableCount); self.collection = collection
    }
}

public struct MusicRemoteQueueStart: Sendable {
    public let index: Int
    public init(index: Int) { self.index = index }
}

public actor MusicRemoteQueueSource {
    public nonisolated let id = UUID()
    public nonisolated let context: MusicRemoteQueueContext
    public nonisolated let authorization: MusicQueueAuthorization
    private enum Loader: Sendable {
        case fresh(@Sendable (String?) async throws -> MusicNativeTrackPage)
        case references(@Sendable (String?, MusicQueueAuthorization) async throws -> MusicQueueReferencePage)
    }
    private let load: Loader
    private var adopt: (@Sendable ([MusicNativeTrackData], MusicQueueAuthorization) async throws -> [MusicQueueTrackReference])?
    private var project: (@Sendable ([MusicQueueTrackReference]) async throws -> [MusicNativeTrackData])?
    private let validateBinding: @Sendable () async throws -> Void
    private let release: @Sendable () async -> Void
    public nonisolated let dailyStyle: MusicDailyStyleEditing?
    public nonisolated let homeAction: MusicHomeAction?
    public nonisolated let fmPreferenceScope: String?
    private var pages: [String: MusicQueueReferencePage] = [:]
    private var loading = false
    private var savingStyle = false
    private var contentRevision = UUID()
    private var closed = false
    private var closing: Task<Void, Never>?
    private static let maximumReadbackCacheBytes = 6 * 1024 * 1024

    public init(context: MusicRemoteQueueContext,
                authorization: MusicQueueAuthorization = MusicQueueAuthorization(),
                loadPage: @escaping @Sendable (String?) async throws -> MusicNativeTrackPage,
                validate: @escaping @Sendable () async throws -> Void = {},
                close: @escaping @Sendable () async -> Void = {},
                dailyStyle: MusicDailyStyleEditing? = nil, homeAction: MusicHomeAction? = nil,
                fmPreferenceScope: String? = nil) {
        self.context = context; self.authorization = authorization
        load = .fresh(loadPage); validateBinding = validate; release = close
        self.dailyStyle = dailyStyle; self.homeAction = homeAction; self.fmPreferenceScope = fmPreferenceScope
    }
    public init(context: MusicRemoteQueueContext,
                authorization: MusicQueueAuthorization = MusicQueueAuthorization(),
                loadReferences: @escaping @Sendable (String?, MusicQueueAuthorization) async throws -> MusicQueueReferencePage,
                validate: @escaping @Sendable () async throws -> Void = {},
                close: @escaping @Sendable () async -> Void = {},
                homeAction: MusicHomeAction? = nil, fmPreferenceScope: String? = nil) {
        self.context = context; self.authorization = authorization
        load = .references(loadReferences); validateBinding = validate; release = close
        dailyStyle = nil; self.homeAction = homeAction; self.fmPreferenceScope = fmPreferenceScope
    }
    public func bindSongRepository(
        adopt: @escaping @Sendable ([MusicNativeTrackData], MusicQueueAuthorization) async throws -> [MusicQueueTrackReference],
        project: @escaping @Sendable ([MusicQueueTrackReference]) async throws -> [MusicNativeTrackData]
    ) {
        guard self.project == nil else { return }
        self.adopt = adopt; self.project = project
    }
    public func validate() async throws {
        try Task.checkCancellation(); try authorization.check()
        guard !closed else { throw MusicServiceError.closed }
        try await validateBinding()
        try Task.checkCancellation(); try authorization.check()
        guard !closed else { throw MusicServiceError.closed }
    }
    public func page(cursor: String?) async throws -> MusicNativeTrackPage {
        let value = try await referencePage(cursor: cursor)
        let items = try await projectTracks(value.items)
        try await validate()
        return .init(items: items, total: value.total, nextPage: value.nextPage, cursor: value.cursor, dailyStyle: value.dailyStyle)
    }
    public func referencePage(cursor: String?) async throws -> MusicQueueReferencePage {
        try await referencePage(cursor: cursor, intent: authorization)
    }
    private func referencePage(cursor: String?, intent: MusicQueueAuthorization) async throws -> MusicQueueReferencePage {
        try await validate()
        try intent.check()
        guard !savingStyle else { throw MusicServiceError.busy }
        let key = cursor ?? ""
        if let saved = pages[key] { return saved }
        if let cursor {
            guard pages.values.contains(where: { $0.cursor == cursor }) else { throw MusicServiceError.staleResult }
        }
        guard !loading else { throw MusicServiceError.busy }
        loading = true; defer { loading = false }
        let reading = MusicQueueAuthorization(parent: intent)
        let value = try await withTaskCancellationHandler {
            try await readPage(cursor, intent: reading, caching: true)
        } onCancel: { reading.revoke() }
        try reading.perform { pages[key] = value }
        return value
    }
    private func readPage(_ cursor: String?, intent: MusicQueueAuthorization,
                          caching: Bool) async throws -> MusicQueueReferencePage {
        switch load {
        case .fresh(let load):
            let value = try await load(cursor)
            try await validate(); try intent.check()
            try checkPage(cursor: cursor, next: value.cursor, caching: caching)
            guard let adopt else { throw MusicServiceError.unavailable }
            let items = try await adopt(value.items, intent)
            try await validate(); try intent.check()
            return .init(items: items, total: value.total, nextPage: value.nextPage, cursor: value.cursor, dailyStyle: value.dailyStyle)
        case .references(let load):
            let value = try await load(cursor, intent)
            try await validate(); try intent.check()
            try checkPage(cursor: cursor, next: value.cursor, caching: caching)
            return value
        }
    }
    private func checkPage(cursor: String?, next: String?, caching: Bool) throws {
        guard next != cursor || cursor == nil,
              !caching || next == nil || pages[next!] == nil else { throw MusicServiceError.invalidResponse }
    }
    private func projectTracks(_ items: [MusicQueueTrackReference]) async throws -> [MusicNativeTrackData] {
        guard let project else { throw MusicServiceError.unavailable }
        return try await project(items)
    }
    public func completeTracks(intent: MusicQueueAuthorization) async throws -> [MusicNativeTrackData] {
        let items = try await completeReferences(intent: intent)
        let value = try await projectTracks(items)
        try intent.check(); try await validate()
        return value
    }
    public func completeReferences(intent: MusicQueueAuthorization) async throws -> [MusicQueueTrackReference] {
        let revision = contentRevision
        var rows: [MusicQueueTrackReference] = [], cursor: String?, visited = Set<String>()
        repeat {
            try intent.check()
            guard visited.insert(cursor ?? "").inserted else { throw MusicServiceError.invalidResponse }
            let value = try await referencePage(cursor: cursor, intent: intent)
            guard revision == contentRevision else { throw MusicServiceError.staleResult }
            rows += value.items; cursor = value.cursor
        } while cursor != nil
        try intent.check(); try await validate()
        guard revision == contentRevision else { throw MusicServiceError.staleResult }
        return rows
    }
    public func saveDailyStyle(_ selection: MusicDailyStyleSelection) async throws -> MusicDailyStyleSave {
        try await validate()
        guard let dailyStyle else { throw MusicServiceError.unsupported }
        guard !loading, !savingStyle else { throw MusicServiceError.busy }
        savingStyle = true; contentRevision = UUID()
        pages.removeAll()
        defer { savingStyle = false }
        let result = try await dailyStyle.save(selection)
        let bytes = result.page.items.reduce(0) { $0 + $1.data.count }
        if !closed, authorization.isActive, bytes <= Self.maximumReadbackCacheBytes, let adopt {
            if let items = try? await adopt(result.page.items, authorization), !closed, authorization.isActive {
                pages = ["": .init(items: items, total: result.page.total, nextPage: result.page.nextPage,
                    cursor: result.page.cursor, dailyStyle: result.page.dailyStyle)]
            }
        }
        return result
    }
    public func nextFMReferences(intent: MusicQueueAuthorization) async throws -> [MusicQueueTrackReference] {
        guard context.kind == .fm else { throw MusicServiceError.invalidRequest }
        try await validate(); try intent.check()
        guard !loading else { throw MusicServiceError.busy }
        loading = true; defer { loading = false }
        var rows: [MusicQueueTrackReference] = [], cursor: String?, visited = Set<String>()
        repeat {
            try intent.check(); try await validate()
            guard visited.insert(cursor ?? "").inserted else { throw MusicServiceError.invalidResponse }
            let value = try await readPage(cursor, intent: intent, caching: false)
            try intent.check(); try await validate()
            rows += value.items; cursor = value.cursor
        } while cursor != nil
        return rows
    }
    public nonisolated func revoke() { authorization.revoke() }
    public func close() async {
        revoke()
        if let closing { await closing.value; return }
        closed = true; pages.removeAll()
        let release = self.release
        let closing = Task { await release() }
        self.closing = closing
        await closing.value
    }
}

public struct MusicQueueDisplayIdentity: Codable, Equatable, Sendable {
    public let songID: String
    public let occurrenceID: String
    public let sourceType: String
    public let sourceID: String
    public let hasContext: Bool

    public init(songID: String, occurrenceID: String, sourceType: String,
                sourceID: String, hasContext: Bool) {
        self.songID = songID; self.occurrenceID = occurrenceID
        self.sourceType = sourceType; self.sourceID = sourceID; self.hasContext = hasContext
    }

    public func matches(_ current: MusicQueueDisplayIdentity?) -> Bool {
        guard let current, !songID.isEmpty, songID == current.songID else { return false }
        if !sourceType.isEmpty || !sourceID.isEmpty {
            return sourceType == current.sourceType && sourceID == current.sourceID
        }
        return !hasContext && !current.hasContext && !occurrenceID.isEmpty && occurrenceID == current.occurrenceID
    }
}

public enum MusicDisplayedQueueOrigin: Sendable {
    case song
    case section(title: String)
    case history(date: String)
}

public enum MusicRemoteCollectionFilter {
    public static func matches(_ track: MusicRemoteTrack, query: String) -> Bool {
        matches(title: track.title, artist: track.artist, album: track.album, query: query)
    }
    public static func matches(title: String, artist: String, album: String, query: String) -> Bool {
        let query = query.trimmingCharacters(in: .whitespacesAndNewlines).localizedLowercase
        return query.isEmpty || [title, artist, album].contains {
            $0.localizedLowercase.range(of: query, options: .literal) != nil
        }
    }
}

public enum MusicLibrarySyncKind: String, Codable, CaseIterable, Sendable { case favorites, playlist }

public struct MusicLibrarySyncSelection: Codable, Equatable, Sendable {
    public let platform: MusicAccountPlatform
    public var favorites: Bool
    public var playlists: Bool
    public var kinds: [MusicLibrarySyncKind] { (favorites ? [.favorites] : []) + (playlists ? [.playlist] : []) }
    public init(platform: MusicAccountPlatform, favorites: Bool = false, playlists: Bool = false) {
        self.platform = platform; self.favorites = favorites; self.playlists = playlists
    }
}

public struct MusicLibrarySyncRequest: Sendable {
    public let id: UUID
    public let selections: [MusicLibrarySyncSelection]
    public let confirmDestructive: Bool
    public init(id: UUID = UUID(), selections: [MusicLibrarySyncSelection], confirmDestructive: Bool = false) {
        self.id = id; self.selections = selections; self.confirmDestructive = confirmDestructive
    }
}

public struct MusicLibrarySyncIssue: Codable, Equatable, Sendable {
    public let platform: MusicAccountPlatform?
    public let collectionID: String?
    public let code: String
    public init(platform: MusicAccountPlatform? = nil, collectionID: String? = nil, code: String) {
        self.platform = platform; self.collectionID = collectionID; self.code = code
    }
    public var title: String {
        switch code {
        case "unmapped": return "部分歌曲未匹配"
        case "orderUnsupported": return "QQ 音乐远端排序不可用"
        case "readOnly": return "只读歌单，未回写"
        case "unconfirmed": return "远端写入待确认"
        case "accountChanged": return "平台账号已变更"
        case "emptyRemote": return "远端歌单为空，需确认后同步"
        case "bulkRemoteDelete": return "远端大批量删除，需确认后同步"
        case "conflict": return "歌单已变更"
        case "cancelled": return "已取消"
        case "authRequired": return "账号待验证"
        case "sessionExpired": return "登录已失效，需要重新登录"
        case "limit": return "本轮同步已达上限"
        case "automaticBindingRequired": return "请确认当前账号后同步"
        case "automaticPreferencesChanged": return "同步设置已变更"
        default: return "同步失败"
        }
    }
}

public struct MusicLibrarySyncState: Sendable {
    public enum Phase: String, Codable, Sendable {
        case idle, pulling, pushing, synced, partial, blocked, cancelled
        public var title: String {
            switch self {
            case .idle: return "未同步"
            case .pulling: return "读取中"
            case .pushing: return "同步中"
            case .synced: return "已同步"
            case .partial: return "部分完成"
            case .blocked: return "同步待处理"
            case .cancelled: return "已取消"
            }
        }
    }
    public let selections: [MusicLibrarySyncSelection]
    public let phase: Phase
    public let pending: Int
    public let unresolvedFences: Int
    public let issues: [MusicLibrarySyncIssue]
    public var hasUnresolvedFences: Bool { unresolvedFences > 0 }
    public init(selections: [MusicLibrarySyncSelection] = [], phase: Phase = .idle, pending: Int = 0,
                unresolvedFences: Int = 0, issues: [MusicLibrarySyncIssue] = []) {
        self.selections = selections; self.phase = phase; self.pending = pending
        self.unresolvedFences = unresolvedFences; self.issues = issues
    }
}

public struct MusicAutomaticLibrarySyncPreferences: Equatable, Sendable {
    public let revision: Int
    public let selections: [MusicLibrarySyncSelection]
    public init(revision: Int = 0, selections: [MusicLibrarySyncSelection] = []) {
        self.revision = revision; self.selections = selections
    }
}

public struct MusicAutomaticLibrarySyncSaveResult: Sendable {
    public let preferences: MusicAutomaticLibrarySyncPreferences
    public let synchronization: Task<Result<MusicLibrarySyncState, MusicServiceError>, Never>
    public init(preferences: MusicAutomaticLibrarySyncPreferences,
                synchronization: Task<Result<MusicLibrarySyncState, MusicServiceError>, Never>) {
        self.preferences = preferences; self.synchronization = synchronization
    }
}

public enum MusicAutomaticLibrarySyncEvent: Sendable {
    case localMutation
    case refreshed
}

public struct MusicAutomaticLibrarySyncRequest: Sendable {
    public enum Reason: Sendable, Equatable { case foreground, interval, localMutation, manual }
    public let id: UUID
    public let reason: Reason
    public let preferenceRevision: Int?
    public let authorization: MusicQueueAuthorization
    public init(id: UUID = UUID(), reason: Reason, preferenceRevision: Int? = nil,
                authorization: MusicQueueAuthorization) {
        self.id = id; self.reason = reason; self.preferenceRevision = preferenceRevision
        self.authorization = authorization
    }
}

public struct MusicLibrarySyncSession: Sendable, Equatable {
    public let id: UUID
    public let platform: MusicAccountPlatform
    public let accountID: String
    public let scope: String
    public init(id: UUID, platform: MusicAccountPlatform, accountID: String, scope: String) {
        self.id = id; self.platform = platform; self.accountID = accountID; self.scope = scope
    }
}

public struct MusicLibrarySyncMember: Codable, Sendable, Equatable {
    public let nativeID: String
    public let occurrenceID: String
    public let track: Data
    public init(nativeID: String, occurrenceID: String, track: Data) {
        self.nativeID = nativeID; self.occurrenceID = occurrenceID; self.track = track
    }
}

public struct MusicLibrarySyncCollection: Codable, Sendable, Equatable {
    public let id: String
    public let name: String
    public let kind: MusicLibrarySyncKind
    public let writable: Bool
    public let canRename: Bool
    public let canDelete: Bool
    public let canOrder: Bool
    public let members: [MusicLibrarySyncMember]
    public let revision: String
    public init(id: String, name: String, kind: MusicLibrarySyncKind, writable: Bool,
                canRename: Bool, canDelete: Bool, canOrder: Bool,
                members: [MusicLibrarySyncMember], revision: String) {
        self.id = id; self.name = name; self.kind = kind; self.writable = writable
        self.canRename = canRename; self.canDelete = canDelete; self.canOrder = canOrder
        self.members = members; self.revision = revision
    }
}

public struct MusicLibrarySyncSnapshot: Sendable {
    public let accountID: String
    public let collections: [MusicLibrarySyncCollection]
    public let subscriptions: [MusicLibrarySyncSubscription]
    public let ownedCollectionIDs: [String]
    public let revision: String
    public init(accountID: String, collections: [MusicLibrarySyncCollection],
                subscriptions: [MusicLibrarySyncSubscription] = [], revision: String,
                ownedCollectionIDs: [String] = []) {
        self.accountID = accountID; self.collections = collections
        self.subscriptions = subscriptions; self.revision = revision
        self.ownedCollectionIDs = ownedCollectionIDs
    }
}

public struct MusicLibrarySyncSubscription: Codable, Sendable, Equatable {
    public let remoteID: String
    public let relationID: String
    public let name: String
    public let creator: String
    public let artwork: String
    public let count: Int?
    public init(remoteID: String, relationID: String, name: String, creator: String = "",
                artwork: String = "", count: Int? = nil) {
        self.remoteID = remoteID; self.relationID = relationID; self.name = name
        self.creator = creator; self.artwork = artwork; self.count = count
    }
}

public struct MusicLibrarySyncMutation: Codable, Sendable, Equatable {
    public enum Action: String, Codable, Sendable {
        case create, delete, rename, add, remove, order, subscribe, unsubscribe
        public var isSubscription: Bool { self == .subscribe || self == .unsubscribe }
    }
    public let id: UUID
    public let action: Action
    public let collectionID: String?
    public let name: String?
    public let before: MusicLibrarySyncCollection?
    public let beforeSubscription: MusicLibrarySyncSubscription?
    public let directoryRevision: String
    public let members: [MusicLibrarySyncMember]
    public init(id: UUID = UUID(), action: Action, collectionID: String? = nil, name: String? = nil,
                before: MusicLibrarySyncCollection? = nil, directoryRevision: String = "",
                members: [MusicLibrarySyncMember] = [], beforeSubscription: MusicLibrarySyncSubscription? = nil) {
        self.id = id; self.action = action; self.collectionID = collectionID; self.name = name
        self.before = before; self.beforeSubscription = beforeSubscription
        self.directoryRevision = directoryRevision; self.members = members
    }
}

public enum MusicLibrarySyncWriteResult: Sendable {
    case acknowledged(createdID: String?, createdName: String?)
    case notDispatched(MusicServiceError)
    case rejected(MusicServiceError)
}

public struct MusicNetworkPlaylistReference: Codable, Equatable, Sendable {
    public let platform: MusicPublicBrowsePlatform
    public let remoteID: String
    public let creator: String
    public init(platform: MusicPublicBrowsePlatform, remoteID: String, creator: String = "") {
        self.platform = platform; self.remoteID = remoteID; self.creator = creator
    }
}

public struct MusicHistoryDisplayEntry: Sendable, Equatable, Identifiable {
    public let id: String
    public let index: Int
    public let title: String
    public let artist: String
    public let album: String
    public let artwork: URL?
    public let duration: Double
    public let displayIdentity: MusicQueueDisplayIdentity?

    public init(id: String, index: Int, title: String, artist: String, album: String,
                artwork: URL?, duration: Double, displayIdentity: MusicQueueDisplayIdentity? = nil) {
        self.id = id; self.index = index; self.title = title; self.artist = artist
        self.album = album; self.artwork = artwork; self.duration = duration
        self.displayIdentity = displayIdentity
    }
}

public struct MusicHistoryDetail: Sendable, Identifiable {
    public let summary: MusicPlayedPlaylist
    public let entries: [MusicHistoryDisplayEntry]
    public let selectedIndex: Int?
    public let sourceLabel: String?
    public var id: String { summary.id }

    public init(summary: MusicPlayedPlaylist, entries: [MusicHistoryDisplayEntry],
                selectedIndex: Int?, sourceLabel: String?) {
        self.summary = summary; self.entries = entries
        self.selectedIndex = selectedIndex; self.sourceLabel = sourceLabel
    }
}

public enum MusicHistoryCategory: Equatable, Sendable {
    case songs, playlists
}

public struct MusicSimilarHeartbeatContext: Sendable, Equatable, Codable {
    public let playlistID: String
    public let seedID: String
    public let count: Int
    public init(playlistID: String, seedID: String, count: Int = 0) {
        self.playlistID = playlistID; self.seedID = seedID; self.count = count
    }
}

public enum MusicNativeSimilarModeRequest: Sendable, Equatable {
    case heartbeat(lastID: String?, fromHome: Bool, context: MusicSimilarHeartbeatContext?)
    case songRoaming(sourceIDs: [String])
    case artistRoaming(sourceIDs: [String])
    case songRecommendations(seedID: String, offset: Int)
}

public struct MusicNativeSimilarModeBatch: Sendable {
    public let items: [MusicNativeTrackData]
    public let heartbeatContext: MusicSimilarHeartbeatContext?
    public let hasMore: Bool?
    public let offset: Int?
    public init(items: [MusicNativeTrackData], heartbeatContext: MusicSimilarHeartbeatContext? = nil,
                hasMore: Bool? = nil, offset: Int? = nil) {
        self.items = items; self.heartbeatContext = heartbeatContext; self.hasMore = hasMore; self.offset = offset
    }
}

public enum MusicSimilarModeProvider: String, Sendable, Codable {
    case heartbeat
    case songRoaming = "song-roaming"
    case artistRoaming = "artist-roaming"
    case queueRecommendations = "queue-recommendations"
}

public struct MusicSimilarModeSong: Sendable, Equatable {
    public let title: String
    public let artist: String
    public let album: String
    public let artwork: URL?
    public init(title: String, artist: String = "", album: String = "", artwork: URL? = nil) {
        self.title = title; self.artist = artist; self.album = album; self.artwork = artwork
    }
}

public struct MusicSimilarModeCandidate: Sendable, Identifiable, Equatable {
    public let id: String
    public let song: MusicSimilarModeSong
    public init(id: String, song: MusicSimilarModeSong) { self.id = id; self.song = song }
}

public struct MusicSimilarModeBatch: Sendable, Identifiable, Equatable {
    public let id: String
    public let basedOn: MusicSimilarModeSong
    public let provider: MusicSimilarModeProvider
    public let entryIDs: [String]
    public init(id: String, basedOn: MusicSimilarModeSong, provider: MusicSimilarModeProvider, entryIDs: [String]) {
        self.id = id; self.basedOn = basedOn; self.provider = provider; self.entryIDs = entryIDs
    }
}

public struct MusicSimilarModeSnapshot: Sendable, Identifiable, Equatable {
    public enum Status: String, Sendable { case loading, ready, empty, error }
    public let id: UUID, active: Bool, status: Status, seed: MusicSimilarModeSong, seedEntryID: String?
    public let continueEnabled: Bool, candidates: [MusicSimilarModeCandidate], batches: [MusicSimilarModeBatch]
    public let sourceIDs: [String], fromHome: Bool, provider: MusicSimilarModeProvider
    public let error: MusicServiceError?, errorID: UUID?, canRetry: Bool
    public init(id: UUID, active: Bool, status: Status, seed: MusicSimilarModeSong, seedEntryID: String?,
                continueEnabled: Bool, candidates: [MusicSimilarModeCandidate], batches: [MusicSimilarModeBatch],
                sourceIDs: [String], fromHome: Bool, provider: MusicSimilarModeProvider,
                error: MusicServiceError?, canRetry: Bool, errorID: UUID? = nil) {
        self.id = id; self.active = active; self.status = status; self.seed = seed; self.seedEntryID = seedEntryID
        self.continueEnabled = continueEnabled; self.candidates = candidates; self.batches = batches
        self.sourceIDs = sourceIDs; self.fromHome = fromHome; self.provider = provider
        self.error = error; self.errorID = errorID; self.canRetry = canRetry
    }
}

public extension MusicSimilarModeSnapshot {
    var canRestoreQueue: Bool { active && (provider == .heartbeat || provider == .queueRecommendations) }
    var sourceTitle: String {
        switch provider {
        case .heartbeat: return "心动"
        case .songRoaming: return "相似歌曲"
        case .artistRoaming: return "相似艺人"
        case .queueRecommendations: return "推荐"
        }
    }
    func headings(for entryIDs: [String]) -> [String: String] {
        guard active else { return [:] }
        var byEntry: [String: MusicSimilarModeBatch] = [:]
        for batch in batches { for id in batch.entryIDs { byEntry[id] = batch } }
        var previous = "", result: [String: String] = [:]
        for id in entryIDs {
            let batch = byEntry[id]
            let group = batch?.id ?? (id == seedEntryID ? "seed" : "manual")
            if group != previous {
                result[id] = batch.map {
                    $0.provider == .queueRecommendations ? "更多推荐" : "根据 \($0.basedOn.title) 推荐"
                } ?? (group == "seed" ? "推荐种子" : "手动加入")
            }
            previous = group
        }
        return result
    }
}

public enum MusicSimilarModeStart: Sendable, Equatable {
    case heartbeat
    case heartbeatSong(songID: String)
    case recommendations(occurrenceID: String, expectedRevision: Int)
    case currentHeartbeat(occurrenceID: String, expectedRevision: Int)
    case songRoaming(sourceIDs: [String], sourceName: String)
    case artistRoaming(sourceIDs: [String], sourceName: String)
}

public struct MusicSimilarModeResult: Sendable {
    public enum PlaybackAction: Sendable, Equatable {
        case keepCurrent
        case playCurrent
        case togglePlayback
        case restoreOnly
    }
    public let state: MusicPlaybackState
    public let action: PlaybackAction
    public init(state: MusicPlaybackState, action: PlaybackAction) { self.state = state; self.action = action }
}

public struct MusicQueueRecommendationSnapshot: Sendable, Identifiable, Equatable {
    public let id: UUID, status: MusicSimilarModeSnapshot.Status, currentEntryID: String
    public let active: Bool, continueEnabled: Bool, candidates: [MusicSimilarModeCandidate], exhausted: Bool
    public let error: MusicServiceError?, errorID: UUID?, canRetry: Bool
    public init(id: UUID, status: MusicSimilarModeSnapshot.Status, currentEntryID: String,
                continueEnabled: Bool, candidates: [MusicSimilarModeCandidate], exhausted: Bool,
                error: MusicServiceError?, canRetry: Bool, active: Bool = false, errorID: UUID? = nil) {
        self.id = id; self.status = status; self.currentEntryID = currentEntryID
        self.active = active; self.continueEnabled = continueEnabled; self.candidates = candidates
        self.exhausted = exhausted; self.error = error; self.errorID = errorID; self.canRetry = canRetry
    }
}

public struct MusicSearchBatchTarget: Sendable {
    public let track: MusicRemoteTrack
    public init(track: MusicRemoteTrack) { self.track = track }
}

public struct MusicSearchBatchRequest: Sendable {
    public let query: String
    public let targets: [MusicSearchBatchTarget]
    public let startIndex: Int
    public init(query: String, targets: [MusicSearchBatchTarget], startIndex: Int = 0) {
        self.query = query; self.targets = targets; self.startIndex = startIndex
    }
}

public struct MusicSearchHotKeyword: Sendable, Equatable, Identifiable {
    public let keyword: String
    public let rank: Int
    public let score: Double?
    public var id: String { Data(keyword.utf8).base64EncodedString() }
    public init(keyword: String, rank: Int, score: Double? = nil) {
        self.keyword = keyword; self.rank = rank; self.score = score
    }
}

public struct MusicSearchDiscoveryChart: Sendable, Identifiable {
    public let collection: MusicPublicBrowseCollection
    public var id: String { collection.id }
    public init(collection: MusicPublicBrowseCollection) { self.collection = collection }
}

public struct MusicSearchDiscoveryFailure: Sendable, Identifiable {
    public enum Component: String, Sendable, Hashable, CaseIterable { case hot, charts }
    public let component: Component
    public let error: MusicServiceError
    public var id: String { component.rawValue }
    public init(component: Component, error: MusicServiceError) { self.component = component; self.error = error }
}

public struct MusicSearchDiscoverySnapshot: Sendable {
    public let hot: [MusicSearchHotKeyword]
    public let hotCanPlay: Bool
    public let charts: [MusicSearchDiscoveryChart]
    public let failures: [MusicSearchDiscoveryFailure]
    public let validUntil: Date
    public init(hot: [MusicSearchHotKeyword], hotCanPlay: Bool = false,
                charts: [MusicSearchDiscoveryChart],
                failures: [MusicSearchDiscoveryFailure] = [], validUntil: Date) {
        self.hot = hot; self.hotCanPlay = hotCanPlay; self.charts = charts
        self.failures = failures; self.validUntil = validUntil
    }
}

public struct MusicSearchObservation: Codable, Equatable, Sendable {
    public enum Scope: String, Codable, Sendable { case page, recording }
    public enum Outcome: String, Codable, Sendable { case found, notFound, incomplete, failed }
    public let requestID: UUID
    public let startedAt: Double
    public let order: UInt64
    public let checkedAt: Double
    public let platform: String
    public let query: String
    public let context: String
    public let page: Int
    public let limit: Int
    public let scope: Scope
    public let outcome: Outcome
    public let failureCode: String?

    public init(requestID: UUID = UUID(), startedAt: Double, order: UInt64 = 0,
                checkedAt: Double, platform: String, query: String, context: String = "public",
                page: Int = 1, limit: Int = 30, scope: Scope = .page,
                outcome: Outcome, failure: MusicServiceError? = nil) {
        self.requestID = requestID; self.startedAt = startedAt; self.order = order
        self.checkedAt = checkedAt; self.platform = platform; self.query = query
        self.context = context; self.page = page; self.limit = limit; self.scope = scope
        self.outcome = outcome; failureCode = failure?.rawValue
    }

    public func result(_ outcome: Outcome, scope: Scope? = nil,
                       checkedAt: Double? = nil, failure: MusicServiceError? = nil) -> Self {
        .init(requestID: requestID, startedAt: startedAt, order: order, checkedAt: checkedAt ?? self.checkedAt,
              platform: platform, query: query, context: context, page: page, limit: limit,
              scope: scope ?? self.scope, outcome: outcome, failure: failure)
    }

    public func isNewer(than other: Self) -> Bool {
        startedAt > other.startedAt || startedAt == other.startedAt && order > other.order
    }

    public func sameQuery(as other: Self) -> Bool {
        platform == other.platform && context == other.context &&
            MusicRecordingIdentity.normalize(query) == MusicRecordingIdentity.normalize(other.query) &&
            page == other.page && limit == other.limit && scope == other.scope
    }

    public func sameEvidence(as other: Self) -> Bool {
        func kind(_ outcome: Outcome) -> Int {
            switch outcome {
            case .found, .notFound: return 0
            case .incomplete: return 1
            case .failed: return 2
            }
        }
        return sameQuery(as: other) && kind(outcome) == kind(other.outcome)
    }
}

public enum MusicSearchPagination {
    public struct Request: Hashable, Sendable {
        public let page: Int
        public let limit: Int
        public var nextPage: Int? {
            let (next, overflow) = page.addingReportingOverflow(1)
            return overflow ? nil : next
        }
        public init(page: Int, limit: Int) { self.page = page; self.limit = limit }
    }
    public static func pageSize(_ platform: MusicPublicBrowsePlatform) -> Int { 30 }
}

public struct MusicPlaylistSearchPage: Sendable {
    public let platform: MusicPublicBrowsePlatform
    public let query: String
    public let page: Int
    public let limit: Int
    public let total: Int
    public let nextPage: Int?
    public let items: [MusicPublicBrowseCollection]
    public init(platform: MusicPublicBrowsePlatform, query: String, page: Int, limit: Int = 30,
                total: Int, nextPage: Int?, items: [MusicPublicBrowseCollection]) {
        self.platform = platform; self.query = query; self.page = page; self.limit = limit
        self.total = total; self.nextPage = nextPage; self.items = items
    }
}

public struct MusicPlaybackFeedbackSample: Sendable {
    public enum Kind: Sendable, Equatable { case playing, progress, paused, unavailable, ended, failed, stopped }
    public let attemptID: String, occurrenceID: String, resourceID: String
    public let nativeAttemptID: UUID
    public let sequence: UInt64
    public let observedAt: TimeInterval, occurredAt: TimeInterval
    public let kind: Kind
    public let position: Double, duration: Double, rate: Double
    public let running: Bool, hasPlayed: Bool, discontinuity: Bool
    public let automatic: Bool?
    public let reason: MusicPlaybackFeedbackReport.Reason?
    public init(attemptID: String, occurrenceID: String, resourceID: String, nativeAttemptID: UUID,
                sequence: UInt64, observedAt: TimeInterval, kind: Kind, position: Double,
                duration: Double, rate: Double = 1, running: Bool, hasPlayed: Bool, discontinuity: Bool = false,
                automatic: Bool? = nil, occurredAt: TimeInterval = Date().timeIntervalSince1970,
                reason: MusicPlaybackFeedbackReport.Reason? = nil) {
        self.attemptID = attemptID; self.occurrenceID = occurrenceID; self.resourceID = resourceID
        self.nativeAttemptID = nativeAttemptID; self.sequence = sequence; self.observedAt = observedAt
        self.kind = kind; self.position = position; self.duration = duration; self.rate = rate
        self.running = running; self.hasPlayed = hasPlayed; self.discontinuity = discontinuity
        self.automatic = automatic; self.occurredAt = occurredAt; self.reason = reason
    }
}

public struct MusicPlaybackFeedbackOrigin: Sendable, Equatable {
    public enum Kind: String, Sendable { case song, search, playlist, album, daily, fm, similar, recommendations, heartbeat }
    public let kind: Kind
    public let platform: MusicPublicBrowsePlatform?
    public let sourceID: String, searchKeyword: String
    public let action: MusicHomeAction?
    public let seedIDs: [String]
    public init(kind: Kind, platform: MusicPublicBrowsePlatform? = nil, sourceID: String = "",
                searchKeyword: String = "", action: MusicHomeAction? = nil, seedIDs: [String] = []) {
        self.kind = kind; self.platform = platform; self.sourceID = sourceID
        self.searchKeyword = searchKeyword; self.action = action; self.seedIDs = seedIDs
    }
}

public struct MusicPlaybackFeedbackSetting: Sendable, Equatable {
    public let enabled: Bool
    public let generation: Int
    public init(enabled: Bool = false, generation: Int = 0) { self.enabled = enabled; self.generation = generation }
}

public struct MusicPlaybackFeedbackSettings: Sendable, Equatable {
    public let revision: Int
    public let platforms: [MusicAccountPlatform: MusicPlaybackFeedbackSetting]
    public init(revision: Int, platforms: [MusicAccountPlatform: MusicPlaybackFeedbackSetting]) {
        self.revision = revision; self.platforms = platforms
    }
}

public struct MusicPlaybackFeedbackTarget: Sendable, Equatable {
    public let platform: MusicAccountPlatform
    public let nativeID: String
    public let mixsongID: String?
    public let source: String, sourceID: String, algorithm: String
    public let automatic: Bool
    public let origin: MusicPlaybackFeedbackOrigin?
    public init(platform: MusicAccountPlatform, nativeID: String, mixsongID: String? = nil,
                source: String = "", sourceID: String = "", algorithm: String = "", automatic: Bool = false,
                origin: MusicPlaybackFeedbackOrigin? = nil) {
        self.platform = platform; self.nativeID = nativeID; self.mixsongID = mixsongID
        self.source = source; self.sourceID = sourceID; self.algorithm = algorithm; self.automatic = automatic
        self.origin = origin
    }
    public func isSameRecording(as other: Self) -> Bool {
        platform == other.platform && nativeID == other.nativeID && mixsongID == other.mixsongID
    }
}

public struct MusicPlaybackFeedbackReport: Sendable, Equatable {
    public enum Event: Sendable, Equatable { case start, end }
    public enum Reason: String, Sendable { case playend, interrupt, exception, ui }
    public let instanceID: UUID
    public let target: MusicPlaybackFeedbackTarget
    public let event: Event
    public let duration: Double, listened: Double, mediaListened: Double, position: Double, rate: Double
    public let startedAt: TimeInterval, occurredAt: TimeInterval
    public let reason: Reason?
    public init(instanceID: UUID, target: MusicPlaybackFeedbackTarget, event: Event, duration: Double,
                listened: Double, mediaListened: Double, position: Double, rate: Double, reason: Reason? = nil,
                startedAt: TimeInterval, occurredAt: TimeInterval) {
        self.instanceID = instanceID; self.target = target; self.event = event; self.duration = duration
        self.listened = listened; self.mediaListened = mediaListened; self.position = position
        self.rate = rate; self.reason = reason
        self.startedAt = startedAt; self.occurredAt = occurredAt
    }
}

public enum MusicPlaybackFeedbackReceipt: Sendable, Equatable {
    case registered
    case confirmed
    case unconfirmed
}

public struct MusicPlaybackFeedbackSession: Sendable, Equatable {
    public let id: UUID
    public let platform: MusicAccountPlatform
    public let settingGeneration: Int
    public let registersLocally: Bool
    public init(id: UUID = UUID(), platform: MusicAccountPlatform, settingGeneration: Int, registersLocally: Bool) {
        self.id = id; self.platform = platform
        self.settingGeneration = settingGeneration; self.registersLocally = registersLocally
    }
}
