// SPDX-License-Identifier: GPL-3.0-only
import Foundation
import MeowUI
import MeowStorage

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
    public var title: String { self == .song ? "灵动模式" : "固定颜色" }
}

public struct MusicAppearancePreferences: Codable, Equatable, Sendable {
    public var playlistView: MusicPlaylistAppearance
    public var playerTheme: MusicPlayerAppearance
    public var colorMode: MusicColorMode
    public var colorPalette: PagePalette
    public var lockLyric: Bool
    public var lyricTranslation: Bool
    public var lyricBackground: Bool

    public init(playlistView: MusicPlaylistAppearance = .grid, playerTheme: MusicPlayerAppearance = .starlight,
                colorMode: MusicColorMode = .song, colorPalette: PagePalette = .preset(.lilac),
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
        playlistView = try values.decode(MusicPlaylistAppearance.self, forKey: .playlistView)
        playerTheme = try values.decode(MusicPlayerAppearance.self, forKey: .playerTheme)
        colorMode = try values.decode(MusicColorMode.self, forKey: .colorMode)
        colorPalette = try values.decode(PagePalette.self, forKey: .colorPalette)
        lockLyric = try values.decode(Bool.self, forKey: .lockLyric)
        lyricTranslation = try values.decode(Bool.self, forKey: .lyricTranslation)
        lyricBackground = try values.decode(Bool.self, forKey: .lyricBackground)
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
    /// as title, never the translation. Keep the song and artist on the second line.
    public func lockScreenMetadata(songTitle: String, songArtist: String, lyric: String?,
                                   isFavorite: Bool) -> MusicLockScreenMetadata {
        let title = songTitle + (isFavorite ? " ♡︎" : "")
        if lockLyric, let lyric, !lyric.isEmpty {
            return .init(title: lyric, artist: songArtist.isEmpty ? title : title + " / " + songArtist,
                         album: "我的曲库")
        }
        return .init(title: title, artist: songArtist, album: "我的曲库")
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
        ticket.songKey.utf16.elementsEqual(MusicAppearanceSongIdentity(track: track).stableKey.utf16)
    }
}

#if os(iOS) || os(macOS)
import Combine

/// One owner is injected into the music root, settings and player. No singleton,
/// notification bus or another persistence authority.
@MainActor public final class MusicAppearancePreferencesStore: ObservableObject, PageThemeProviding {
    @Published public private(set) var preferences = MusicAppearancePreferences()
    @Published public private(set) var effectivePalette: PagePalette = .preset(.lilac)
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
    public var pagePalette: PagePalette { effectivePalette }
    public var animatesPageColorway: Bool { animatesPaletteChanges }
    public var pageThemeChanges: AnyPublisher<Void, Never> {
        $effectivePalette.removeDuplicates().dropFirst().map { _ in () }.eraseToAnyPublisher()
    }
    private var database: SQLiteDatabase?
    private var cacheDatabase: SQLiteDatabase?
    private var epoch = UUID()
    private var closed = false
    private var pendingColors: [Data: (key: String, palette: PageColorway)] = [:]
    private var pendingLast: PageColorway?
    private var savedLast: PageColorway?
    private var colorWriter: Task<Void, Never>?
    private var cache = MusicSongColorCache()
    private var songKey = ""
    private var generation = UUID()
    private var artworkID: String?
    private var previewPalette: PagePalette?

    public init(database: SQLiteDatabase? = nil, cacheDatabase: SQLiteDatabase? = nil) {
        self.database = database
        self.cacheDatabase = cacheDatabase
    }
    public func bind(database: SQLiteDatabase, cacheDatabase: SQLiteDatabase) async throws {
        guard self.database == nil || self.database === database, !closed else { return }
        let loadedPreferences = try await database.snapshot { try MusicAppearanceRepository.loadPreferences($0) }
        let loadedColors = try await cacheDatabase.snapshot { try MusicAppearanceRepository.loadColors($0) }
        guard !closed else { return }
        self.database = database
        self.cacheDatabase = cacheDatabase
        cache = loadedColors; savedLast = cache.last
        publishPreferences(loadedPreferences)
    }

    /// Call on foreground if another native preferences owner was used.
    /// A palette read is not a cache touch; matches the reference Map order.
    public func reload() async {
        guard !closed, let database else { return }
        let ticket = UUID(); epoch = ticket
        do {
            let next = try await database.read { try MusicAppearanceRepository.loadPreferences($0) }
            guard epoch == ticket, !closed else { return }
            publishPreferences(next); persistenceError = nil
        } catch { if epoch == ticket, !closed { persistenceError = "音乐偏好读取失败" } }
    }
    private func publishPreferences(_ next: MusicAppearancePreferences) {
        let colorChanged = next.colorMode != preferences.colorMode || next.colorPalette != preferences.colorPalette
        if colorChanged {
            generation = UUID(); coverPreferenceRevision = UUID()
            if next.colorMode == .song, !songKey.isEmpty {
                cache.last = cache.color(for: songKey) ?? MusicSongPalette.fallback(for: songKey)
            }
        }
        preferences = next
        publishPalette(animated: false)
    }

    @discardableResult public func set<Value>(_ keyPath: WritableKeyPath<MusicAppearancePreferences, Value>,
                                              _ value: Value) async -> Bool {
        // Rebase a field edit; a stale settings page must not overwrite another
        // page's unrelated native preference. No remote/server write is made.
        await edit { $0[keyPath: keyPath] = value }
    }

    @discardableResult public func selectFixedPalette(_ palette: PagePalette) async -> Bool {
        await edit { $0.colorMode = .fixed; $0.colorPalette = palette }
    }

    public func previewFixedColor(_ rgb: UInt32) {
        previewPalette = .custom(rgb)
        publishPalette(animated: false)
    }

    public func endColorPreview() {
        previewPalette = nil
        publishPalette(animated: false)
    }

    /// Explicit recovery only; never removes the original export or library.
    public func resetPreferences() async {
        guard !closed else { return }
        let ticket = UUID(); epoch = ticket
        do {
            if let database {
                try await database.transaction { try MusicAppearanceRepository.resetPreferences($0) }
            }
            if !closed, epoch == ticket { publishPreferences(.init()); persistenceError = nil }
        } catch {
            if !closed, epoch == ticket { persistenceError = "音乐偏好保存失败" }
        }
    }

    /// Stable song identity, never queue occurrence. Multiple views
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
            displayGeneration = UUID(); displayArtworkID = artworkID
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
        guard acceptsDisplayedArtwork(ticket), !data.isEmpty else { return false }
        if displayedArtwork?.ticket != ticket || displayedArtwork?.data != data {
            displayedArtwork = .init(ticket: ticket, data: data)
        }
        return true
    }

    func selectSystemArtwork(_ track: MusicRemoteTrack) {
        let identity = MusicAppearanceSongIdentity(track: track)
        let url = track.artwork?.absoluteString ?? ""
        let targetChanged = systemArtworkTarget.map {
            !$0.songKey.utf16.elementsEqual(identity.stableKey.utf16) || !$0.artworkID.utf16.elementsEqual(url.utf16)
        } ?? false
        systemArtworkTarget = (identity.stableKey, url)
        selectSong(identity) // Idempotent: progress/lyrics never reset tickets.
        if displayArtworkID?.utf16.elementsEqual(url.utf16) != true {
            displayGeneration = UUID(); displayArtworkID = url
        }
        if targetChanged, systemArtworkEnabled { displayedArtworkRevision = UUID() }
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
        if cache.color(for: songKey) != found { pendingColors[Data(songKey.utf8)] = (songKey, found) }
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
        if cache.color(for: identity.stableKey) != found {
            pendingColors[Data(identity.stableKey.utf8)] = (identity.stableKey, found)
        }
        cache.insert(found, for: identity.stableKey); persistColors()
        return found
    }

    func accepts(_ ticket: MusicAppearanceCoverTicket) -> Bool {
        ticket.generation == generation && ticket.songKey.utf16.elementsEqual(songKey.utf16) &&
        preferences.colorMode == .song
    }
    @discardableResult private func edit(_ change: @escaping @Sendable (inout MusicAppearancePreferences) -> Void) async -> Bool {
        guard !closed else { return false }
        let ticket = UUID(); epoch = ticket
        do {
            let next: MusicAppearancePreferences
            if let database {
                next = try await database.transaction { db in
                    var updated = try MusicAppearanceRepository.loadPreferences(db)
                    change(&updated)
                    try MusicAppearanceRepository.savePreferences(updated, db)
                    return updated
                }
            } else {
                var local = preferences; change(&local); next = local
            }
            if epoch == ticket, !closed { publishPreferences(next); persistenceError = nil; persistColors() }
            return true
        } catch {
            if epoch == ticket, !closed { persistenceError = "音乐偏好保存失败" }
            return false
        }
    }
    private func publishPalette(animated: Bool) {
        animatesPaletteChanges = animated
        let next = previewPalette ?? (preferences.colorMode == .fixed ? preferences.colorPalette : .preset(cache.last))
        if effectivePalette != next { effectivePalette = next }
    }
    private func persistColors() {
        guard !closed else { return }
        if cache.last != savedLast { pendingLast = cache.last }
        // Retain only the latest bounded palette cache, not every sampled cover.
        let retained = Set(cache.colors.map { Data($0[0].utf8) })
        pendingColors = pendingColors.filter { retained.contains($0.key) }
        guard colorWriter == nil, let cacheDatabase, pendingLast != nil || !pendingColors.isEmpty else { return }
        colorWriter = Task {
            while pendingLast != nil || !pendingColors.isEmpty {
                let last = pendingLast
                let colors = cache.colors.compactMap { pendingColors[Data($0[0].utf8)] }
                pendingLast = nil; pendingColors.removeAll()
                do {
                    try await cacheDatabase.transaction { db in
                        try MusicAppearanceRepository.saveColors(colors, last: last, db)
                    }
                    if let last { savedLast = last }
                } catch { persistenceError = "歌曲配色保存失败" }
            }
            colorWriter = nil
        }
    }
    public func close() async {
        closed = true; epoch = UUID()
        await colorWriter?.value
        _ = try? await cacheDatabase?.flush()
        _ = try? await database?.flush()
    }
}
#endif
