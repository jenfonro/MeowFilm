// SPDX-License-Identifier: GPL-3.0-only
// iWebPlus b900c259: browse-data.js history; account-settings.js search fields.
import Foundation
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
        searchPlaylists = try container.decodeIfPresent(Bool.self, forKey: .searchPlaylists) ?? true
        songs = try container.decodeIfPresent(Kind.self, forKey: .songs) ?? .init()
        playlists = try container.decodeIfPresent(Kind.self, forKey: .playlists) ?? .init()
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
    public static let preferencesKey = "music.search.preferences.v1"
    @Published public private(set) var preferences = MusicSearchPreferences()
    private let defaults: UserDefaults

    public init(defaults: UserDefaults = .standard) {
        self.defaults = defaults
        reload()
    }
    public func reload() {
        if let data = defaults.data(forKey: Self.preferencesKey),
           let value = try? JSONDecoder().decode(MusicSearchPreferences.self, from: data) {
            preferences = value
            if let clean = try? JSONEncoder().encode(preferences), clean != data {
                defaults.set(clean, forKey: Self.preferencesKey)
            }
        } else { preferences = .init() }
    }
    @discardableResult
    public func update(_ value: MusicSearchPreferences) -> Bool {
        guard let value = try? value.validated() else { return false }
        guard let data = try? JSONEncoder().encode(value) else { return false }
        guard preferences != value else { return true }
        preferences = value
        defaults.set(data, forKey: Self.preferencesKey)
        return true
    }
}
#endif
