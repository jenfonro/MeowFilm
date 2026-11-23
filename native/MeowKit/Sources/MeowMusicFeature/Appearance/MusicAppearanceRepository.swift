// SPDX-License-Identifier: GPL-3.0-only
import Foundation
import MeowUI
import MeowStorage

struct MusicSongColorCache: Sendable {
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
}

enum MusicAppearanceRepository {
    private static let scopeKey = "device"

    static func loadPreferences(_ db: SQLiteConnection) throws -> MusicAppearancePreferences {
        guard let next: MusicAppearancePreferences = try db.queryOne(
            "SELECT a.playlist_view, a.player_theme, a.color_mode, a.color_palette, " +
            "e.lock_lyric, e.lyric_translation, e.lyric_background " +
            "FROM music_appearance a LEFT JOIN ios_music_appearance_extras e ON e.scope=a.scope " +
            "WHERE a.scope=?",
            [.text(scopeKey)],
            map: { cursor in
                guard let playlistView = MusicPlaylistAppearance(rawValue: try cursor.text(0)),
                      let playerTheme = MusicPlayerAppearance(rawValue: try cursor.text(1)),
                      let colorMode = MusicColorMode(rawValue: try cursor.text(2)) else {
                    throw SQLiteStorageError.invalidValue
                }
                return MusicAppearancePreferences(
                    playlistView: playlistView,
                    playerTheme: playerTheme,
                    colorMode: colorMode,
                    colorPalette: try decodePalette(try cursor.text(3)),
                    lockLyric: try cursor.optionalBool(4) ?? true,
                    lyricTranslation: try cursor.optionalBool(5) ?? true,
                    lyricBackground: try cursor.optionalBool(6) ?? true
                )
            }
        ) else {
            return .init()
        }
        return next
    }

    static func loadColors(_ cacheDb: SQLiteConnection) throws -> MusicSongColorCache {
        var cache = MusicSongColorCache()
        let recent: [(key: String, palette: PageColorway)] = try cacheDb.query(
            "SELECT song_key, palette FROM ios_music_song_palettes ORDER BY used_at_ms DESC, song_key LIMIT 128"
        ) { cursor in
            let songKey = try cursor.text(0)
            guard let palette = PageColorway(rawValue: try cursor.text(1)) else { throw SQLiteStorageError.corrupt }
            return (songKey, palette)
        }
        for entry in recent.reversed() {
            cache.insert(entry.palette, for: entry.key)
        }
        if let rawLast = try cacheDb.queryOne("SELECT last_palette FROM ios_music_palette_state WHERE singleton=1", map: { try $0.text(0) }) {
            guard let palette = PageColorway(rawValue: rawLast) else { throw SQLiteStorageError.corrupt }
            cache.last = palette
        }
        return cache
    }

    static func savePreferences(_ value: MusicAppearancePreferences, _ db: SQLiteConnection) throws {
        let changed = try db.execute(
            "INSERT INTO music_appearance(scope,revision,playlist_view,player_theme,color_mode,color_palette) " +
            "VALUES(?,1,?,?,?,?) " +
            "ON CONFLICT(scope) DO UPDATE SET " +
            "revision=music_appearance.revision+1," +
            "playlist_view=excluded.playlist_view," +
            "player_theme=excluded.player_theme," +
            "color_mode=excluded.color_mode," +
            "color_palette=excluded.color_palette " +
            "WHERE music_appearance.revision < 9007199254740991",
            [
                .text(scopeKey),
                .text(value.playlistView.rawValue),
                .text(value.playerTheme.rawValue),
                .text(value.colorMode.rawValue),
                .text(encodePalette(value.colorPalette)),
            ]
        )
        guard changed == 1 else { throw SQLiteStorageError.conflict }
        try db.upsert(
            "ios_music_appearance_extras",
            columns: ["scope", "lock_lyric", "lyric_translation", "lyric_background"],
            values: [
                .text(scopeKey),
                .bool(value.lockLyric),
                .bool(value.lyricTranslation),
                .bool(value.lyricBackground),
            ],
            key: ["scope"]
        )
    }

    static func resetPreferences(_ db: SQLiteConnection) throws {
        // Cascades to ios_music_appearance_extras.
        try db.execute("DELETE FROM music_appearance WHERE scope=?", [.text(scopeKey)])
    }

    static func saveColors(_ colors: [(key: String, palette: PageColorway)], last: PageColorway?, _ cacheDb: SQLiteConnection) throws {
        var ordinal = try cacheDb.queryOne("SELECT COALESCE(MAX(used_at_ms), 0) FROM ios_music_song_palettes") { try $0.int64(0) } ?? 0
        let nowMs = Int64((Date().timeIntervalSince1970 * 1000).rounded())
        for value in colors {
            ordinal = max(ordinal + 1, nowMs)
            try cacheDb.upsert(
                "ios_music_song_palettes",
                columns: ["song_key", "palette", "used_at_ms"],
                values: [.text(value.key), .text(value.palette.rawValue), .integer(ordinal)],
                key: ["song_key"]
            )
        }
        try cacheDb.execute(
            "DELETE FROM ios_music_song_palettes WHERE song_key IN " +
            "(SELECT song_key FROM ios_music_song_palettes ORDER BY used_at_ms DESC, song_key LIMIT -1 OFFSET 128)"
        )
        if let last {
            try cacheDb.upsert(
                "ios_music_palette_state",
                columns: ["singleton", "last_palette"],
                values: [.integer(1), .text(last.rawValue)],
                key: ["singleton"]
            )
        }
    }

    private static func decodePalette(_ text: String) throws -> PagePalette {
        if let preset = PageColorway(rawValue: text) { return .preset(preset) }
        if text.count == 7, text.first == "#", let rgb = UInt32(text.dropFirst(), radix: 16) {
            return .custom(rgb)
        }
        throw SQLiteStorageError.invalidValue
    }

    private static func encodePalette(_ palette: PagePalette) -> String {
        switch palette {
        case .preset(let value): return value.rawValue
        case .custom(let rgb): return String(format: "#%06X", rgb & 0xffffff)
        }
    }
}
