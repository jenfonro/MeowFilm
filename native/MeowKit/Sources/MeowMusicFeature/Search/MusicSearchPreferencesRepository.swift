// SPDX-License-Identifier: GPL-3.0-only
import Foundation
import MeowStorage

#if os(iOS) || os(macOS)
enum MusicSearchPreferencesRepository {
    private static let scopeKey = "device"

    static func load(_ db: SQLiteConnection) throws -> (MusicSearchPreferences, Int) {
        guard let row = try db.queryOne(
            "SELECT e.search_playlists, p.songs_pages, p.songs_concurrency, p.songs_cache_minutes, " +
            "p.playlists_pages, p.playlists_concurrency, p.playlists_cache_minutes, p.revision " +
            "FROM music_search_policy p " +
            "LEFT JOIN ios_music_search_policy_extras e ON e.scope=p.scope " +
            "WHERE p.scope=?",
            [.text(scopeKey)],
            map: { cursor -> (Bool, Int, Int, Int, Int, Int, Int, Int) in
                guard let searchPlaylists = try cursor.optionalBool(0) else {
                    throw SQLiteStorageError.corrupt
                }
                return (
                    searchPlaylists,
                    try cursor.integer(1),
                    try cursor.integer(2),
                    try cursor.integer(3),
                    try cursor.integer(4),
                    try cursor.integer(5),
                    try cursor.integer(6),
                    try cursor.integer(7)
                )
            }
        ) else {
            return (.init(), 0)
        }
        var songPlatforms: [MusicPublicBrowsePlatform] = []
        var playlistPlatforms: [MusicPublicBrowsePlatform] = []
        try db.scan(
            "SELECT target_kind, platform FROM music_search_policy_platforms " +
            "WHERE scope=? AND list_kind='enabled' ORDER BY target_kind, position",
            [.text(scopeKey)]
        ) { cursor in
            let target = try cursor.text(0)
            guard let platform = MusicPublicBrowsePlatform(rawValue: try cursor.text(1)) else {
                throw SQLiteStorageError.corrupt
            }
            if target == "songs" { songPlatforms.append(platform) }
            else if target == "playlists" { playlistPlatforms.append(platform) }
        }
        let prefs = try MusicSearchPreferences(
            songPlatforms: songPlatforms,
            searchPlaylists: row.0,
            playlistPlatforms: playlistPlatforms,
            songPages: row.1,
            songConcurrency: row.2,
            songCacheMinutes: row.3,
            playlistPages: row.4,
            playlistConcurrency: row.5,
            playlistCacheMinutes: row.6
        ).validated()
        return (prefs, row.7)
    }

    static func save(_ value: MusicSearchPreferences, expectedRevision expected: Int, _ db: SQLiteConnection) throws {
        let current = try db.queryOne(
            "SELECT revision FROM music_search_policy WHERE scope=?",
            [.text(scopeKey)]
        ) { try $0.integer(0) } ?? 0
        guard current == expected else { throw SQLiteStorageError.conflict }
        try db.upsert(
            "music_search_policy",
            columns: [
                "scope", "revision",
                "songs_pages", "songs_concurrency", "songs_cache_minutes",
                "playlists_pages", "playlists_concurrency", "playlists_cache_minutes",
            ],
            values: [
                .text(scopeKey), .integer(expected + 1),
                .integer(value.songs.pages), .integer(value.songs.concurrency), .integer(value.songs.cacheMinutes),
                .integer(value.playlists.pages), .integer(value.playlists.concurrency), .integer(value.playlists.cacheMinutes),
            ],
            key: ["scope"]
        )
        try db.upsert(
            "ios_music_search_policy_extras",
            columns: ["scope", "search_playlists"],
            values: [.text(scopeKey), .bool(value.searchPlaylists)],
            key: ["scope"]
        )
        for (target, enabledList) in [("songs", value.songPlatforms), ("playlists", value.playlistPlatforms)] {
            for (pos, platform) in enabledList.enumerated() {
                try db.upsert(
                    "music_search_policy_platforms",
                    columns: ["scope", "target_kind", "list_kind", "position", "platform"],
                    values: [.text(scopeKey), .text(target), .text("enabled"), .integer(pos), .text(platform.rawValue)],
                    key: ["scope", "target_kind", "list_kind", "position"]
                )
            }
            try db.execute(
                "DELETE FROM music_search_policy_platforms WHERE scope=? AND target_kind=? AND list_kind='enabled' AND position>=?",
                [.text(scopeKey), .text(target), .integer(enabledList.count)]
            )
        }
    }
}
#endif
