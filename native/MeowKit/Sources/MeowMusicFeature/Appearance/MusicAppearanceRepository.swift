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
    private static let columns = [
        "playlist_view", "player_theme", "color_mode", "color_palette",
        "lock_lyric", "lyric_translation", "lyric_background",
    ]

    static func loadPreferences(_ db: SQLiteConnection) throws -> MusicAppearancePreferences {
        guard let next: MusicAppearancePreferences = try db.queryOne(
            "SELECT playlist_view, player_theme, color_mode, color_palette, lock_lyric, lyric_translation, lyric_background " +
            "FROM music_appearance WHERE scope=?",
            [.text(scopeKey)]
        , map: { cursor in
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
                lockLyric: try cursor.bool(4),
                lyricTranslation: try cursor.bool(5),
                lyricBackground: try cursor.bool(6)
            )
        }) else {
            return .init()
        }
        return next
    }

    static func loadAll(_ db: SQLiteConnection) throws -> (MusicAppearancePreferences, MusicSongColorCache) {
        var cache = MusicSongColorCache()
        try db.scan("SELECT song_key, palette FROM ios_music_song_palettes ORDER BY used_at, song_key LIMIT 128") { cursor in
            let songKey = try cursor.text(0)
            guard let palette = PageColorway(rawValue: try cursor.text(1)) else { throw SQLiteStorageError.corrupt }
            cache.insert(palette, for: songKey)
        }
        if let rawLast = try db.queryOne("SELECT last_palette FROM ios_music_palette_state WHERE singleton=1", map: { try $0.text(0) }) {
            guard let palette = PageColorway(rawValue: rawLast) else { throw SQLiteStorageError.corrupt }
            cache.last = palette
        }
        return (try loadPreferences(db), cache)
    }

    static func savePreferences(_ value: MusicAppearancePreferences, _ db: SQLiteConnection) throws {
        let revision = try db.queryOne(
            "SELECT revision FROM music_appearance WHERE scope=?",
            [.text(scopeKey)]
        ) { try $0.int64(0) } ?? 0
        guard revision < 9_007_199_254_740_991 else { throw SQLiteStorageError.conflict }
        let values: [SQLiteValue] = [
            .text(scopeKey),
            .integer(revision + 1),
            .text(value.playlistView.rawValue),
            .text(value.playerTheme.rawValue),
            .text(value.colorMode.rawValue),
            .text(encodePalette(value.colorPalette)),
            .bool(value.lockLyric),
            .bool(value.lyricTranslation),
            .bool(value.lyricBackground),
        ]
        try db.upsert(
            "music_appearance",
            columns: ["scope", "revision"] + columns,
            values: values,
            key: ["scope"]
        )
    }

    static func resetPreferences(_ db: SQLiteConnection) throws {
        try db.execute("DELETE FROM music_appearance WHERE scope=?", [.text(scopeKey)])
    }

    static func saveColors(_ colors: [(key: String, palette: PageColorway)], last: PageColorway?, _ db: SQLiteConnection) throws {
        var ordinal = try db.queryOne("SELECT COALESCE(MAX(used_at), 0) FROM ios_music_song_palettes") { try $0.double(0) } ?? 0
        for value in colors {
            ordinal += 1
            try db.upsert(
                "ios_music_song_palettes",
                columns: ["song_key", "palette", "used_at"],
                values: [.text(value.key), .text(value.palette.rawValue), .real(ordinal)],
                key: ["song_key"]
            )
        }
        try db.execute(
            "DELETE FROM ios_music_song_palettes WHERE song_key IN " +
            "(SELECT song_key FROM ios_music_song_palettes ORDER BY used_at DESC, song_key LIMIT -1 OFFSET 128)"
        )
        if let last {
            try db.upsert(
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
