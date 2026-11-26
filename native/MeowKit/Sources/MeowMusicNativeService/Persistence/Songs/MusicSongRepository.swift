import Foundation
import MeowStorage

enum MusicSongRepository {
    private static let sourceColumns: [String] = [
        "song_id", "source_key", "position", "platform", "info_source",
        "songmid", "mid", "native_id", "hash",
        "name", "singer", "album_name", "duration", "img", "quality_known"
    ]

    private static let extraInsertSQL: String = {
        let cols = ["song_id", "source_key"] + SQLiteValueTree.columns
        let placeholders = Array(repeating: "?", count: cols.count).joined(separator: ",")
        return "INSERT INTO music_song_source_extras(\(cols.joined(separator: ","))) VALUES(\(placeholders))"
    }()

    static func read(_ db: SQLiteConnection, id: String) throws -> MusicSongRecord? {
        try readBatch(db, ids: [id])[id]
    }

    /// Batched 5-query loader eliminating 5×N round-trips for playlists, queues, and history.
    static func readBatch(_ db: SQLiteConnection, ids: Set<String>) throws -> [String: MusicSongRecord] {
        let uniqueIDs = Array(ids)
        guard !uniqueIDs.isEmpty else { return [:] }
        struct BaseRow {
            let songID: String, title: String, artist: String, album: String
            let duration: Double?, baselineDuration: Double?, coverURL: String
            let artworkSourceKey: String?, artworkSourceImage: String?
        }
        var baseRows: [String: BaseRow] = [:]
        var songArtists: [String: [String]] = [:]
        var sourceArtists: [String: [String: [String]]] = [:]
        var sourceExtras: [String: [String: [SQLiteTreeNodeRow]]] = [:]
        var sourcesBySong: [String: [MusicSongSourceRecord]] = [:]

        for start in stride(from: 0, to: uniqueIDs.count, by: 500) {
            let batch = Array(uniqueIDs[start..<min(start + 500, uniqueIDs.count)])
            let placeholders = Array(repeating: "?", count: batch.count).joined(separator: ",")
            let args = batch.map(SQLiteValue.text)

            try db.scan(
                "SELECT song_id,title,artist,album,duration,baseline_duration,cover_url,artwork_source_key,artwork_source_image " +
                "FROM music_songs WHERE song_id IN (\(placeholders))", args
            ) { c in
                let songID = try c.text(0)
                baseRows[songID] = BaseRow(songID: songID, title: try c.text(1), artist: try c.text(2), album: try c.text(3),
                    duration: try c.optionalDouble(4), baselineDuration: try c.optionalDouble(5), coverURL: try c.text(6),
                    artworkSourceKey: try c.optionalText(7), artworkSourceImage: try c.optionalText(8))
            }
            try db.scan("SELECT song_id,name FROM music_song_artists WHERE song_id IN (\(placeholders)) ORDER BY song_id,position", args) { c in
                songArtists[try c.text(0), default: []].append(try c.text(1))
            }
            try db.scan(
                "SELECT song_id,source_key,name FROM music_song_source_artists WHERE song_id IN (\(placeholders)) ORDER BY song_id,source_key,position", args
            ) { c in
                sourceArtists[try c.text(0), default: [:]][try c.text(1), default: []].append(try c.text(2))
            }
            try db.scan(
                "SELECT song_id,source_key,node_id,parent_id,prop_key,item_index,kind,bool_value,int_value,real_value,text_value " +
                "FROM music_song_source_extras WHERE song_id IN (\(placeholders)) ORDER BY song_id,source_key,node_id", args
            ) { c in
                sourceExtras[try c.text(0), default: [:]][try c.text(1), default: []].append(try SQLiteValueTree.readNodeRow(c, offset: 2))
            }
            try db.scan(
                "SELECT song_id,source_key,platform,info_source,songmid,mid,native_id,hash,name,singer,album_name,duration,img,quality_known " +
                "FROM music_song_sources WHERE song_id IN (\(placeholders)) ORDER BY song_id,position", args
            ) { c in
                let songID = try c.text(0), sourceKey = try c.text(1)
                let rows = sourceExtras[songID]?[sourceKey] ?? []
                let extras = rows.isEmpty ? nil : try SQLiteValueTree.decode(rows)
                sourcesBySong[songID, default: []].append(MusicSongSourceRecord(
                    sourceKey: sourceKey, platform: try c.text(2), infoSource: try c.text(3),
                    songmid: try c.optionalText(4), mid: try c.optionalText(5), nativeID: try c.optionalText(6),
                    hash: try c.optionalText(7), name: try c.optionalText(8), singer: try c.optionalText(9),
                    albumName: try c.optionalText(10), duration: try c.optionalDouble(11), img: try c.optionalText(12),
                    qualityKnown: try c.optionalBool(13), artistNames: sourceArtists[songID]?[sourceKey] ?? [], extras: extras
                ))
            }
        }

        var result: [String: MusicSongRecord] = [:]
        result.reserveCapacity(baseRows.count)
        for (songID, row) in baseRows {
            result[songID] = MusicSongRecord(
                songID: row.songID, title: row.title, artist: row.artist, album: row.album,
                duration: row.duration, baselineDuration: row.baselineDuration, coverURL: row.coverURL,
                artistNames: songArtists[songID] ?? [], artworkSourceKey: row.artworkSourceKey,
                artworkSourceImage: row.artworkSourceImage, sources: sourcesBySong[songID] ?? []
            )
        }
        return result
    }

    static func upsertSong(_ db: SQLiteConnection, song: MusicSongRecord, previous: MusicSongRecord?,
                           matchKey: String?, searchQuery: String) throws {
        let id = song.songID
        let positiveDuration: SQLiteValue = song.duration.flatMap { $0.isFinite && $0 > 0 ? .real($0) : nil } ?? .null
        let positiveBaseline: SQLiteValue = song.baselineDuration.flatMap { $0.isFinite && $0 > 0 ? .real($0) : nil } ?? .null
        try db.upsert(
            "music_songs",
            columns: ["song_id", "title", "artist", "album", "duration", "baseline_duration",
                      "cover_url", "artwork_source_key", "artwork_source_image", "match_key", "search_query"],
            values: [.text(id), .text(song.title), .text(song.artist), .text(song.album),
                     positiveDuration, positiveBaseline, .text(song.coverURL),
                     song.artworkSourceKey.flatMap { $0.isEmpty ? nil : .text($0) } ?? .null,
                     song.artworkSourceImage.flatMap { $0.isEmpty ? nil : .text($0) } ?? .null,
                     .optional(matchKey), .text(searchQuery)],
            key: ["song_id"]
        )
        if previous == nil || previous?.artistNames != song.artistNames {
            for (index, artistName) in song.artistNames.enumerated() {
                try db.upsert("music_song_artists", columns: ["song_id", "position", "name"],
                              values: [.text(id), .integer(index), .text(artistName)], key: ["song_id", "position"])
            }
            try db.execute("DELETE FROM music_song_artists WHERE song_id=? AND position>=?", [.text(id), .integer(song.artistNames.count)])
        }

        var prevSourcesByKey: [String: (pos: Int, value: MusicSongSourceRecord)] = [:]
        for (index, source) in (previous?.sources ?? []).enumerated() { prevSourcesByKey[source.sourceKey] = (index, source) }
        let nextKeys = Set(song.sources.map(\.sourceKey))
        for oldKey in prevSourcesByKey.keys where !nextKeys.contains(oldKey) {
            try db.execute("DELETE FROM music_song_sources WHERE song_id=? AND source_key=?", [.text(id), .text(oldKey)])
        }
        for (sourceIndex, source) in song.sources.enumerated() {
            let sourceKey = source.sourceKey
            if let old = prevSourcesByKey[sourceKey], old.pos == sourceIndex, old.value == source { continue }
            let sourceDuration: SQLiteValue = source.duration.flatMap { $0.isFinite && $0 > 0 ? .real($0) : nil } ?? .null
            let values: [SQLiteValue] = [
                .text(id), .text(sourceKey), .integer(sourceIndex), .text(source.platform), .text(source.infoSource),
                .optional(source.songmid), .optional(source.mid), .optional(source.nativeID), .optional(source.hash),
                .optional(source.name), .optional(source.singer), .optional(source.albumName),
                sourceDuration, .optional(source.img), .optional(source.qualityKnown)
            ]
            try db.upsert("music_song_sources", columns: sourceColumns, values: values, key: ["song_id", "source_key"])

            let existed = prevSourcesByKey[sourceKey] != nil
            if !existed || prevSourcesByKey[sourceKey]?.value.artistNames != source.artistNames {
                for (artistIndex, name) in source.artistNames.enumerated() {
                    try db.upsert("music_song_source_artists", columns: ["song_id", "source_key", "position", "name"],
                                  values: [.text(id), .text(sourceKey), .integer(artistIndex), .text(name)],
                                  key: ["song_id", "source_key", "position"])
                }
                try db.execute("DELETE FROM music_song_source_artists WHERE song_id=? AND source_key=? AND position>=?",
                               [.text(id), .text(sourceKey), .integer(source.artistNames.count)])
            }
            if !existed || prevSourcesByKey[sourceKey]?.value.extras != source.extras {
                try db.execute("DELETE FROM music_song_source_extras WHERE song_id=? AND source_key=?", [.text(id), .text(sourceKey)])
                if let extras = source.extras {
                    for nodeRow in try SQLiteValueTree.rows(extras) {
                        try db.execute(extraInsertSQL, [.text(id), .text(sourceKey)] + nodeRow)
                    }
                }
            }
        }
    }
}
