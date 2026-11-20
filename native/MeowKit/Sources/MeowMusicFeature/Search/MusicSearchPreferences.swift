// SPDX-License-Identifier: GPL-3.0-only
// iWebPlus b900c259: browse-data.js history; account-settings.js search fields.
import Foundation
import MeowStorage
#if os(iOS) || os(macOS)
import Combine

public struct MusicSearchPreferences: Codable, Equatable, Sendable {
    public static let platforms = MusicPublicBrowsePlatform.allCases
    public static let defaultPages = 1, defaultConcurrency = 1, defaultCacheMinutes = 60
    public static let maximumCacheMinutes = 1440
    public struct Kind: Codable, Equatable, Sendable {
        public var platforms: [MusicPublicBrowsePlatform]
        public var pages: Int
        public var concurrency: Int
        public var cacheMinutes: Int
        public init(platforms: [MusicPublicBrowsePlatform] = MusicSearchPreferences.platforms,
                    pages: Int = MusicSearchPreferences.defaultPages,
                    concurrency: Int = MusicSearchPreferences.defaultConcurrency,
                    cacheMinutes: Int = MusicSearchPreferences.defaultCacheMinutes) {
            var seen = Set<MusicPublicBrowsePlatform>()
            self.platforms = platforms.filter { seen.insert($0).inserted }
            self.pages = pages
            self.concurrency = concurrency; self.cacheMinutes = cacheMinutes
        }
        private enum CodingKeys: String, CodingKey {
            case platforms, pages, concurrency, cacheMinutes
        }
        public init(from decoder: Decoder) throws {
            let container = try decoder.container(keyedBy: CodingKeys.self)
            self = try Self(
                platforms: container.decode([MusicPublicBrowsePlatform].self, forKey: .platforms),
                pages: container.decode(Int.self, forKey: .pages),
                concurrency: container.decode(Int.self, forKey: .concurrency),
                cacheMinutes: container.decode(Int.self, forKey: .cacheMinutes)
            ).validated()
        }
        func validated() throws -> Self {
            guard pages >= 1, concurrency >= 1,
                  (1...MusicSearchPreferences.maximumCacheMinutes).contains(cacheMinutes) else {
                throw MusicServiceError.invalidRequest
            }
            return .init(platforms: platforms,
                         pages: pages, concurrency: concurrency, cacheMinutes: cacheMinutes)
        }
    }
    public var searchPlaylists: Bool
    public var songs: Kind
    public var playlists: Kind

    public init(songPlatforms: [MusicPublicBrowsePlatform] = Self.platforms, searchPlaylists: Bool = true,
                playlistPlatforms: [MusicPublicBrowsePlatform] = Self.platforms,
                pages: Int = Self.defaultPages, concurrency: Int = Self.defaultConcurrency,
                cacheMinutes: Int = Self.defaultCacheMinutes,
                songPages: Int? = nil, songConcurrency: Int? = nil, songCacheMinutes: Int? = nil,
                playlistPages: Int? = nil, playlistConcurrency: Int? = nil,
                playlistCacheMinutes: Int? = nil) {
        self.searchPlaylists = searchPlaylists
        songs = .init(platforms: songPlatforms, pages: songPages ?? pages,
                      concurrency: songConcurrency ?? concurrency,
                      cacheMinutes: songCacheMinutes ?? cacheMinutes)
        playlists = .init(platforms: playlistPlatforms, pages: playlistPages ?? pages,
                          concurrency: playlistConcurrency ?? concurrency,
                          cacheMinutes: playlistCacheMinutes ?? cacheMinutes)
    }
    func validated() throws -> Self {
        var value = self
        value.songs = try songs.validated()
        value.playlists = try playlists.validated()
        return value
    }
    public var songPlatforms: [MusicPublicBrowsePlatform] {
        get {
            var seen = Set<MusicPublicBrowsePlatform>()
            return songs.platforms.filter { seen.insert($0).inserted }
        }
        set { songs.platforms = newValue }
    }
    private enum CodingKeys: String, CodingKey {
        case searchPlaylists, songs, playlists
    }
    public init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        searchPlaylists = try container.decode(Bool.self, forKey: .searchPlaylists)
        songs = try container.decode(Kind.self, forKey: .songs)
        playlists = try container.decode(Kind.self, forKey: .playlists)
    }
    public func encode(to encoder: Encoder) throws {
        var container = encoder.container(keyedBy: CodingKeys.self)
        try container.encode(songs, forKey: .songs)
        try container.encode(playlists, forKey: .playlists)
        try container.encode(searchPlaylists, forKey: .searchPlaylists)
    }
    public var effectivePlaylistPlatforms: [MusicPublicBrowsePlatform] {
        guard searchPlaylists else { return [] }
        return playlistPlatforms
    }
    public var playlistPlatforms: [MusicPublicBrowsePlatform] {
        get {
            var seen = Set<MusicPublicBrowsePlatform>()
            return playlists.platforms.filter { seen.insert($0).inserted }
        }
        set { playlists.platforms = newValue }
    }
}

/// Music platform search policy only. Shared keyword history has its own owner.
@MainActor public final class MusicSearchPreferencesStore: ObservableObject {
    @Published public private(set) var preferences = MusicSearchPreferences()
    @Published public private(set) var persistenceError: String?
    private var database: SQLiteDatabase?
    private var revision = 0
    private var epoch = UUID()
    private var closed = false

    public init(database: SQLiteDatabase? = nil) { self.database = database }
    public func bind(database: SQLiteDatabase) async {
        guard self.database == nil, !closed else { return }
        self.database = database; await reload()
    }
    nonisolated private static func load(_ db: SQLiteConnection) throws -> (MusicSearchPreferences, Int) {
        guard let row = try db.query("SELECT * FROM ios_music_search_preferences WHERE singleton=1").first else { return (.init(), 0) }
        let platforms = try db.query("SELECT * FROM ios_music_search_platforms ORDER BY kind,ordinal")
        func values(_ kind: String) throws -> [MusicPublicBrowsePlatform] {
            try platforms.filter { try $0.text("kind") == kind }.map {
                guard let platform = MusicPublicBrowsePlatform(rawValue: try $0.text("platform")) else { throw SQLiteStorageError.corrupt }
                return platform
            }
        }
        return (try MusicSearchPreferences(songPlatforms: values("song"), searchPlaylists: row.bool("searchPlaylists"),
            playlistPlatforms: values("playlist"), songPages: row.integer("song_pages"),
            songConcurrency: row.integer("song_concurrency"), songCacheMinutes: row.integer("song_cache_minutes"),
            playlistPages: row.integer("playlist_pages"), playlistConcurrency: row.integer("playlist_concurrency"),
            playlistCacheMinutes: row.integer("playlist_cache_minutes")).validated(), try row.integer("row_revision"))
    }
    public func reload() async {
        guard !closed, let database else { return }
        let ticket = UUID(); epoch = ticket
        do {
            let result = try await database.snapshot(Self.load)
            guard !closed, epoch == ticket else { return }
            preferences = result.0; revision = result.1; persistenceError = nil
        } catch { if !closed, epoch == ticket { persistenceError = "搜索偏好读取失败" } }
    }
    @discardableResult
    public func update(_ value: MusicSearchPreferences) async -> Bool {
        guard !closed else { return false }
        guard let value = try? value.validated() else { return false }
        guard preferences != value else { return true }
        let ticket = UUID(); epoch = ticket
        let expected = revision
        do {
            if let database {
                try await database.transaction { db in
                    let current = try db.query("SELECT row_revision FROM ios_music_search_preferences WHERE singleton=1")
                        .first?.integer("row_revision") ?? 0
                    guard current == expected else { throw SQLiteStorageError.conflict }
                    try db.upsert("ios_music_search_preferences",
                        columns: ["singleton", "searchPlaylists", "song_pages", "song_concurrency", "song_cache_minutes",
                                  "playlist_pages", "playlist_concurrency", "playlist_cache_minutes", "row_revision"],
                        values: [.integer(1), .bool(value.searchPlaylists), .integer(value.songs.pages),
                            .integer(value.songs.concurrency), .integer(value.songs.cacheMinutes),
                            .integer(value.playlists.pages), .integer(value.playlists.concurrency),
                            .integer(value.playlists.cacheMinutes), .integer(expected + 1)], key: ["singleton"])
                    let rows = [("song", value.songPlatforms), ("playlist", value.playlistPlatforms)].flatMap { kind, platforms in
                        platforms.enumerated().map { [SQLiteValue.text(kind), .integer($0.offset), .text($0.element.rawValue)] }
                    }
                    try db.reconcile("ios_music_search_platforms", columns: ["kind", "ordinal", "platform"],
                        key: ["kind", "ordinal"], rows: rows, replaceChanged: true)
                }
            }
            if !closed, epoch == ticket { preferences = value; revision = expected + 1; persistenceError = nil }
            return true
        } catch {
            if !closed, epoch == ticket { persistenceError = "搜索偏好保存失败" }
            return false
        }
    }
    public func close() async {
        closed = true; epoch = UUID()
        _ = try? await database?.flush()
    }
}
#endif
