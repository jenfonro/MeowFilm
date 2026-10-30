// SPDX-License-Identifier: GPL-3.0-only
import Foundation
import MeowUI
import CoreFoundation

// Reference: iWebPlus b900c259:
// - browse-settings.js:229-266 / account-settings.js:3-19: grid, starlight,
//   song/lilac, lockLyric=true. Only starlight is registered in player-theme.js.
// - player-layout.js:153-176 / player-layout.css:119,147-167: local translation
//   and background switches default on; neither is visible in record preview.
// - app-colors.js:11-35,126-182: retain last palette, 128 ordered cache entries,
//   fallback before cover decode, invalidate late covers on song/mode changes.
// Device-local appearance preferences and song-color cache. Consumers observe
// the shared preference document and published palette changes.

public enum MusicPlaylistAppearance: String, Codable, CaseIterable, Sendable {
    case grid, list
    public var title: String { self == .grid ? "封面网格" : "封面列表" }
}

public enum MusicPlayerAppearance: String, Codable, CaseIterable, Sendable {
    case starlight
    public var title: String { "星光" }
}

public enum MusicColorMode: String, Codable, CaseIterable, Sendable {
    case song, fixed
    public var title: String { self == .song ? "跟随歌曲" : "固定配色" }
}

public struct MusicAppearancePreferences: Codable, Equatable, Sendable {
    public var playlistView: MusicPlaylistAppearance
    public var playerTheme: MusicPlayerAppearance
    public var colorMode: MusicColorMode
    public var colorPalette: PageColorway
    public var lockLyric: Bool
    public var lyricTranslation: Bool
    public var lyricBackground: Bool

    public init(playlistView: MusicPlaylistAppearance = .grid, playerTheme: MusicPlayerAppearance = .starlight,
                colorMode: MusicColorMode = .song, colorPalette: PageColorway = .lilac,
                lockLyric: Bool = true, lyricTranslation: Bool = true, lyricBackground: Bool = true) {
        self.playlistView = playlistView; self.playerTheme = playerTheme
        self.colorMode = colorMode; self.colorPalette = colorPalette; self.lockLyric = lockLyric
        self.lyricTranslation = lyricTranslation; self.lyricBackground = lyricBackground
    }

    private enum CodingKeys: String, CodingKey {
        case playlistView, playerTheme, colorMode, colorPalette, lockLyric, lyricTranslation, lyricBackground
    }
    public init(from decoder: Decoder) throws {
        let values = try decoder.container(keyedBy: CodingKeys.self)
        playlistView = (try? values.decode(MusicPlaylistAppearance.self, forKey: .playlistView)) ?? .grid
        playerTheme = (try? values.decode(MusicPlayerAppearance.self, forKey: .playerTheme)) ?? .starlight
        colorMode = (try? values.decode(MusicColorMode.self, forKey: .colorMode)) ?? .song
        colorPalette = (try? values.decode(PageColorway.self, forKey: .colorPalette)) ?? .lilac
        lockLyric = (try? values.decode(Bool.self, forKey: .lockLyric)) ?? true
        lyricTranslation = (try? values.decode(Bool.self, forKey: .lyricTranslation)) ?? true
        lyricBackground = (try? values.decode(Bool.self, forKey: .lyricBackground)) ?? true
    }

    /// Display only. Never clears parsed lines, rebuilds a queue, seeks or refetches lyrics.
    public func translation(_ text: String, expanded: Bool) -> String? {
        expanded && lyricTranslation && !text.isEmpty ? text : nil
    }
    public func lyricAccessibilityLabel(text: String, translation: String, expanded: Bool) -> String {
        text + (self.translation(translation, expanded: expanded).map { "\n" + $0 } ?? "")
    }
    public func showsLyricBackground(expanded: Bool) -> Bool { expanded && lyricBackground }
    public var translationActionLabel: String { lyricTranslation ? "隐藏翻译歌词" : "显示翻译歌词" }
    public var backgroundActionLabel: String { lyricBackground ? "隐藏歌词背景" : "显示歌词背景" }

    /// Feed the existing shared system-session owner; do not create another
    /// MPNowPlayingInfoCenter writer. Original utils.js:423-440 uses the lyric
    /// as title and moves the song title to artist, never the translated line.
    public func lockScreenMetadata(songTitle: String, lyric: String?, isFavorite: Bool) -> MusicLockScreenMetadata {
        let title = songTitle + (isFavorite ? " ♡︎" : "")
        if lockLyric, let lyric, !lyric.isEmpty {
            return .init(title: lyric, artist: title, album: "我的曲库")
        }
        return .init(title: title, artist: "meowfilm", album: "我的曲库")
    }
}

public struct MusicLockScreenMetadata: Equatable, Sendable {
    public let title: String
    public let artist: String
    public let album: String
}

/// A callback ticket belongs to one store, selected song and cover revision.
/// It must be captured BEFORE asynchronous image work, not on completion.
public struct MusicAppearanceCoverTicket: Equatable, Sendable {
    fileprivate let generation: UUID
    fileprivate let songKey: String
    public let artworkID: String
}

// Ephemeral display-image admission is independent of fixed/song palette mode.
// Captured before AsyncImage mounts; never persisted with appearance settings.
struct MusicDisplayedArtworkTicket: Equatable {
    let generation: UUID
    let songKey: String
    let artworkID: String
}

struct MusicDisplayedArtwork {
    let ticket: MusicDisplayedArtworkTicket
    let data: Data

    func matches(_ track: MusicRemoteTrack) -> Bool {
        ticket.songKey.utf16.elementsEqual(MusicAppearanceSongIdentity(track: track).stableKey.utf16) &&
            ticket.artworkID.utf16.elementsEqual((track.artwork?.absoluteString ?? "").utf16)
    }
}

private struct MusicAppearanceDocument: Codable {
    var version = 1
    var preferences: MusicAppearancePreferences
}

private struct MusicSongColorCache: Codable {
    var version = 1
    var last: PageColorway = .lilac
    var colors: [[String]] = []

    func color(for key: String) -> PageColorway? {
        colors.first { $0[0].utf16.elementsEqual(key.utf16) }.flatMap { PageColorway(rawValue: $0[1]) }
    }
    mutating func insert(_ palette: PageColorway, for key: String) {
        guard !key.isEmpty, key.utf16.count <= 2048 else { return }
        if color(for: key) == palette { return }
        colors.removeAll { $0[0].utf16.elementsEqual(key.utf16) }
        colors.append([key, palette.rawValue])
        if colors.count > 128 { colors.removeFirst(colors.count - 128) }
    }
    static func read(_ data: Data?) -> Self {
        guard let data, data.count <= 2_000_000,
              let json = (try? JSONSerialization.jsonObject(with: data)) as? [String: Any],
              let version = json["version"] as? NSNumber, CFGetTypeID(version) != CFBooleanGetTypeID(),
              version.doubleValue == 1, let entries = json["colors"] as? [Any] else { return .init() }
        var result = Self()
        result.last = PageColorway(rawValue: json["last"] as? String ?? "") ?? .lilac
        let valid = entries.compactMap { entry -> [String]? in
            guard let pair = entry as? [Any], pair.count >= 2,
                  let key = pair[0] as? String, key.utf16.count <= 2048,
                  let palette = pair[1] as? String, PageColorway(rawValue: palette) != nil else { return nil }
            return [key, palette]
        }.suffix(128)
        // Map construction keeps the first insertion position for duplicate keys.
        for pair in valid {
            if let index = result.colors.firstIndex(where: { $0[0].utf16.elementsEqual(pair[0].utf16) }) {
                result.colors[index] = pair
            } else { result.colors.append(pair) }
        }
        return result
    }
}

#if os(iOS) || os(macOS)
import Combine

/// One owner is injected into the music root, settings and player. No singleton,
/// notification bus or observation of unrelated UserDefaults domains.
@MainActor public final class MusicAppearancePreferencesStore: ObservableObject, PageThemeProviding {
    public static let preferencesKey = "music.ui.appearance.v1"
    public static let songColorsKey = "music.ui.songColors.v1"
    @Published public private(set) var preferences = MusicAppearancePreferences()
    @Published public private(set) var effectivePalette: PageColorway = .lilac
    @Published public private(set) var persistenceError: String?
    // A mode round-trip can end on the same value before SwiftUI renders.
    // This revision still invalidates/re-samples the already displayed cover.
    @Published private(set) var coverPreferenceRevision = UUID()
    @Published private(set) var displayedArtwork: MusicDisplayedArtwork?
    @Published private(set) var displayedArtworkRevision = UUID()
    private var displayGeneration = UUID()
    private var displayArtworkID: String?
    private var systemArtworkEnabled = true
    private var systemArtworkTarget: (songKey: String, artworkID: String)?
    /// Only song/cover changes fade. Preference and system scheme changes sync.
    public private(set) var animatesPaletteChanges = false
    public var pageColorway: PageColorway { effectivePalette }
    public var animatesPageColorway: Bool { animatesPaletteChanges }
    public var pageThemeChanges: AnyPublisher<Void, Never> {
        $effectivePalette.removeDuplicates().dropFirst().map { _ in () }.eraseToAnyPublisher()
    }
    private let defaults: UserDefaults
    private var cache = MusicSongColorCache()
    private var songKey = ""
    private var generation = UUID()
    private var artworkID: String?

    public init(defaults: UserDefaults = .standard) {
        self.defaults = defaults
        cache = MusicSongColorCache.read(defaults.data(forKey: Self.songColorsKey))
        reload()
    }

    /// Call on foreground if another native preferences owner was used.
    /// A palette read is not a cache touch; matches the reference Map order.
    public func reload() {
        var next = MusicAppearancePreferences()
        persistenceError = nil
        if let raw = defaults.object(forKey: Self.preferencesKey) {
            guard let data = raw as? Data,
                  let document = try? JSONDecoder().decode(MusicAppearanceDocument.self, from: data) else {
                persistenceError = "音乐偏好读取失败"; return
            }
            guard document.version == 1 else {
                persistenceError = "音乐偏好版本不受支持"; return
            }
            next = document.preferences
        }
        let colorChanged = next.colorMode != preferences.colorMode || next.colorPalette != preferences.colorPalette
        if colorChanged { generation = UUID(); coverPreferenceRevision = UUID() }
        preferences = next
        publishPalette(animated: false)
    }

    @discardableResult public func set<Value>(_ keyPath: WritableKeyPath<MusicAppearancePreferences, Value>,
                                              _ value: Value) -> Bool {
        // Rebase a field edit; a stale settings page must not overwrite another
        // page's unrelated native preference. No remote/server write is made.
        reload()
        guard persistenceError == nil else { return false }
        var next = preferences
        next[keyPath: keyPath] = value
        return save(next)
    }

    @discardableResult public func selectFixedPalette(_ palette: PageColorway) -> Bool {
        reload()
        guard persistenceError == nil else { return false }
        var next = preferences
        next.colorMode = .fixed; next.colorPalette = palette
        return save(next)
    }

    /// Explicit recovery only; never removes the original export or library.
    public func resetPreferences() {
        save(.init())
    }

    /// Stable song identity, never queue occurrence/ref handle. Multiple views
    /// of the same current song do not invalidate each other's pending cover.
    /// nil keeps the last color while invalidating the preceding song's cover.
    public func selectSong(_ identity: MusicAppearanceSongIdentity?) {
        let nextKey = identity?.stableKey ?? ""
        guard !songKey.utf16.elementsEqual(nextKey.utf16) else { return }
        generation = UUID()
        artworkID = nil
        displayGeneration = UUID(); displayArtworkID = nil; displayedArtwork = nil
        songKey = nextKey
        if !songKey.isEmpty {
            cache.last = cache.color(for: songKey) ?? MusicSongPalette.fallback(for: songKey)
        }
        publishPalette(animated: true); persistColors()
    }

    /// The caller must pass the chosen DISPLAY cover, not an obsolete source
    /// cover. Reuses its existing decoded image; this owner does not fetch it.
    public func beginCover(for identity: MusicAppearanceSongIdentity, artworkID: String,
                           isPlaceholder: Bool = false) -> MusicAppearanceCoverTicket? {
        guard preferences.colorMode == .song, !isPlaceholder, !artworkID.isEmpty,
              !songKey.isEmpty, songKey.utf16.elementsEqual(identity.stableKey.utf16) else { return nil }
        // Mini-player and full player can display the same cover concurrently.
        // A second consumer of that exact cover shares its admission revision.
        if self.artworkID?.utf16.elementsEqual(artworkID.utf16) != true {
            generation = UUID()
            self.artworkID = artworkID
        }
        return .init(generation: generation, songKey: songKey, artworkID: artworkID)
    }

    public func cancelCover() { generation = UUID(); artworkID = nil }

    func beginDisplayedArtwork(for identity: MusicAppearanceSongIdentity,
                               artworkID: String) -> MusicDisplayedArtworkTicket? {
        guard !artworkID.isEmpty, !songKey.isEmpty,
              songKey.utf16.elementsEqual(identity.stableKey.utf16) else { return nil }
        if let target = systemArtworkTarget {
            guard target.songKey.utf16.elementsEqual(identity.stableKey.utf16),
                  target.artworkID.utf16.elementsEqual(artworkID.utf16) else { return nil }
        }
        if displayArtworkID?.utf16.elementsEqual(artworkID.utf16) != true {
            displayGeneration = UUID(); displayArtworkID = artworkID; displayedArtwork = nil
        }
        return .init(generation: displayGeneration, songKey: songKey, artworkID: artworkID)
    }

    func acceptsDisplayedArtwork(_ ticket: MusicDisplayedArtworkTicket) -> Bool {
        systemArtworkEnabled && ticket.generation == displayGeneration &&
            ticket.songKey.utf16.elementsEqual(songKey.utf16) &&
            displayArtworkID?.utf16.elementsEqual(ticket.artworkID.utf16) == true
    }

    @discardableResult
    func applyDisplayedArtwork(_ data: Data, ticket: MusicDisplayedArtworkTicket) -> Bool {
        guard acceptsDisplayedArtwork(ticket), !data.isEmpty, data.count <= 2 * 1_024 * 1_024 else { return false }
        if displayedArtwork?.ticket != ticket || displayedArtwork?.data != data {
            displayedArtwork = .init(ticket: ticket, data: data)
        }
        return true
    }

    func selectSystemArtwork(_ track: MusicRemoteTrack) {
        let identity = MusicAppearanceSongIdentity(track: track)
        let url = track.artwork?.absoluteString ?? ""
        systemArtworkTarget = (identity.stableKey, url)
        selectSong(identity) // Idempotent: progress/lyrics never reset tickets.
        if displayArtworkID?.utf16.elementsEqual(url.utf16) != true {
            displayGeneration = UUID(); displayArtworkID = url; displayedArtwork = nil
        }
        setSystemArtworkEnabled(true)
    }

    /// Called by the product-owned metadata bridge, not a view disappearance.
    /// Revocation drops bytes and invalidates in-flight producers synchronously.
    /// Re-enable may reuse a previously displayed Image, never a late old load.
    func setSystemArtworkEnabled(_ enabled: Bool) {
        guard systemArtworkEnabled != enabled else { return }
        systemArtworkEnabled = enabled
        if !enabled {
            displayGeneration = UUID(); displayedArtwork = nil
        }
        displayedArtworkRevision = UUID()
    }

    @discardableResult public func applyCover(rgba: [UInt8], ticket: MusicAppearanceCoverTicket) -> Bool {
        guard accepts(ticket), let found = MusicSongPalette.fromPixels(rgba) else { return false }
        cache.insert(found, for: songKey); cache.last = found
        publishPalette(animated: true); persistColors()
        return true
    }

    /// Optional preload sampling warms only this song's bounded cache. It does
    /// not change the selected song, even if the result arrives late.
    @discardableResult public func primeCover(rgba: [UInt8], for identity: MusicAppearanceSongIdentity,
                                             isPlaceholder: Bool = false) -> PageColorway? {
        guard !isPlaceholder, !identity.stableKey.isEmpty,
              let found = MusicSongPalette.fromPixels(rgba) else { return nil }
        cache.insert(found, for: identity.stableKey); persistColors()
        return found
    }

    func accepts(_ ticket: MusicAppearanceCoverTicket) -> Bool {
        ticket.generation == generation && ticket.songKey.utf16.elementsEqual(songKey.utf16) &&
        preferences.colorMode == .song
    }
    @discardableResult private func save(_ next: MusicAppearancePreferences) -> Bool {
        let document = MusicAppearanceDocument(preferences: next)
        guard let data = try? JSONEncoder().encode(document) else {
            persistenceError = "音乐偏好保存失败"; return false
        }
        defaults.set(data, forKey: Self.preferencesKey)
        if preferences.colorMode != next.colorMode || preferences.colorPalette != next.colorPalette {
            generation = UUID()
            coverPreferenceRevision = UUID()
            // Switching back to song mode uses the latest cached cover/fallback.
            if next.colorMode == .song, !songKey.isEmpty {
                cache.last = cache.color(for: songKey) ?? MusicSongPalette.fallback(for: songKey)
            }
        }
        preferences = next; persistenceError = nil
        publishPalette(animated: false)
        return true
    }
    private func publishPalette(animated: Bool) {
        animatesPaletteChanges = animated
        let next = preferences.colorMode == .fixed ? preferences.colorPalette : cache.last
        if effectivePalette != next { effectivePalette = next }
    }
    private func persistColors() {
        if let data = try? JSONEncoder().encode(cache) { defaults.set(data, forKey: Self.songColorsKey) }
    }
}
#endif
