// SPDX-License-Identifier: GPL-3.0-only
import Foundation
import MeowStorage

#if os(iOS) || os(macOS)
enum MusicSearchPreferencesRepository {
    static func load(_ db: SQLiteConnection) throws -> (MusicSearchPreferences, Int) {
        guard let row = try db.queryOne(
            "SELECT search_playlists, song_pages, song_concurrency, song_cache_minutes, " +
            "playlist_pages, playlist_concurrency, playlist_cache_minutes, row_revision " +
            "FROM ios_music_search_preferences WHERE singleton=1"
        , map: { cursor in
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
        }) else {
            return (.init(), 0)
        }
        var songPlatforms: [MusicPublicBrowsePlatform] = []
        var playlistPlatforms: [MusicPublicBrowsePlatform] = []
        try db.scan("SELECT kind, platform FROM ios_music_search_platforms ORDER BY kind, position") { cursor in
            let kind = try cursor.text(0)
            guard let platform = MusicPublicBrowsePlatform(rawValue: try cursor.text(1)) else {
                throw SQLiteStorageError.corrupt
            }
            if kind == "song" { songPlatforms.append(platform) }
            else if kind == "playlist" { playlistPlatforms.append(platform) }
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
            "SELECT row_revision FROM ios_music_search_preferences WHERE singleton=1"
        ) { try $0.integer(0) } ?? 0
        guard current == expected else { throw SQLiteStorageError.conflict }
        try db.upsert(
            "ios_music_search_preferences",
            columns: [
                "singleton", "search_playlists", "song_pages", "song_concurrency", "song_cache_minutes",
                "playlist_pages", "playlist_concurrency", "playlist_cache_minutes", "row_revision",
            ],
            values: [
                .integer(1), .bool(value.searchPlaylists), .integer(value.songs.pages),
                .integer(value.songs.concurrency), .integer(value.songs.cacheMinutes),
                .integer(value.playlists.pages), .integer(value.playlists.concurrency),
                .integer(value.playlists.cacheMinutes), .integer(expected + 1),
            ],
            key: ["singleton"]
        )
        let rows = [("song", value.songPlatforms), ("playlist", value.playlistPlatforms)].flatMap { kind, platforms in
            platforms.enumerated().map { [SQLiteValue.text(kind), .integer($0.offset), .text($0.element.rawValue)] }
        }
        try db.reconcile(
            "ios_music_search_platforms",
            columns: ["kind", "position", "platform"],
            key: ["kind", "position"],
            rows: rows,
            replaceChanged: true
        )
    }
}
#endif
