// SPDX-License-Identifier: GPL-3.0-only
// iWebPlus b900c259: browse-data.js history; account-settings.js search fields.
import Foundation
#if os(iOS) || os(macOS)
import Combine

public struct MusicSearchPreferences: Codable, Equatable, Sendable {
    public static let platforms: [MusicPublicBrowsePlatform] = [.netease, .qq, .kugou]
    public var songPlatforms: [MusicPublicBrowsePlatform]
    public var searchPlaylists: Bool
    /// "aggregate" or a supported platform's stable key.
    public var playlistMode: String
    public var playlistPlatforms: [MusicPublicBrowsePlatform]

    public init(songPlatforms: [MusicPublicBrowsePlatform] = Self.platforms, searchPlaylists: Bool = true,
                playlistMode: String = "wy", playlistPlatforms: [MusicPublicBrowsePlatform] = Self.platforms) {
        self.songPlatforms = songPlatforms; self.searchPlaylists = searchPlaylists
        self.playlistMode = playlistMode; self.playlistPlatforms = playlistPlatforms
    }
    public var normalized: Self {
        .init(songPlatforms: Self.platforms.filter(songPlatforms.contains), searchPlaylists: searchPlaylists,
              playlistMode: playlistMode == "aggregate" || Self.platforms.contains(where: { $0.rawValue == playlistMode })
                ? playlistMode : "wy",
              playlistPlatforms: Self.platforms.filter(playlistPlatforms.contains))
    }
    public var effectivePlaylistPlatforms: [MusicPublicBrowsePlatform] {
        guard searchPlaylists else { return [] }
        let value = normalized
        return value.playlistMode == "aggregate" ? value.playlistPlatforms
            : Self.platforms.filter { $0.rawValue == value.playlistMode }
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
            preferences = value.normalized
        } else { preferences = .init() }
    }
    public func update(_ value: MusicSearchPreferences) {
        let value = value.normalized
        guard preferences != value else { return }
        preferences = value
        if let data = try? JSONEncoder().encode(value) { defaults.set(data, forKey: Self.preferencesKey) }
    }
}
#endif
