// SPDX-License-Identifier: GPL-3.0-only
import Foundation
import MeowStorage

#if os(iOS) || os(macOS)
enum MusicSearchPreferencesRepository {
    private static let scopeKey = "device"

    static func load(_ db: SQLiteConnection) throws -> (MusicSearchPreferences, Int) {
        guard let row = try db.queryOne(
            "SELECT search_playlists, songs_pages, songs_concurrency, songs_cache_minutes, " +
            "playlists_pages, playlists_concurrency, playlists_cache_minutes, revision " +
            "FROM music_search_policy WHERE scope=?",
            [.text(scopeKey)],
            map: { cursor in
                (
                    try cursor.bool(0),
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
                "scope", "revision", "search_playlists",
                "songs_pages", "songs_concurrency", "songs_cache_minutes",
                "playlists_pages", "playlists_concurrency", "playlists_cache_minutes",
            ],
            values: [
                .text(scopeKey), .integer(expected + 1), .bool(value.searchPlaylists),
                .integer(value.songs.pages), .integer(value.songs.concurrency), .integer(value.songs.cacheMinutes),
                .integer(value.playlists.pages), .integer(value.playlists.concurrency), .integer(value.playlists.cacheMinutes),
            ],
            key: ["scope"]
        )
        try db.execute("DELETE FROM music_search_policy_platforms WHERE scope=?", [.text(scopeKey)])
        let allPlatforms = MusicPublicBrowsePlatform.allCases
        for (target, enabledList) in [("songs", value.songPlatforms), ("playlists", value.playlistPlatforms)] {
            let order = enabledList + allPlatforms.filter { !enabledList.contains($0) }
            for (pos, platform) in order.enumerated() {
                try db.execute(
                    "INSERT INTO music_search_policy_platforms(scope,target_kind,list_kind,position,platform) VALUES(?,?,?,?,?)",
                    [.text(scopeKey), .text(target), .text("order"), .integer(pos), .text(platform.rawValue)]
                )
            }
            for (pos, platform) in enabledList.enumerated() {
                try db.execute(
                    "INSERT INTO music_search_policy_platforms(scope,target_kind,list_kind,position,platform) VALUES(?,?,?,?,?)",
                    [.text(scopeKey), .text(target), .text("enabled"), .integer(pos), .text(platform.rawValue)]
                )
            }
        }
    }
}
#endif
