import Foundation
import MeowStorage

enum MusicSongStore {
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
        guard let row = try db.queryOne(
            "SELECT song_id,title,artist,album,duration,baseline_duration,cover_url," +
            "local_file_id,artwork_source_key,artwork_source_image " +
            "FROM music_songs WHERE song_id=?",
            [.text(id)],
            map: { c in
                (
                    songID: try c.text(0),
                    title: try c.text(1),
                    artist: try c.text(2),
                    album: try c.text(3),
                    duration: try c.optionalDouble(4),
                    baselineDuration: try c.optionalDouble(5),
                    coverURL: try c.text(6),
                    localFileID: try c.optionalText(7),
                    artworkSourceKey: try c.optionalText(8),
                    artworkSourceImage: try c.optionalText(9)
                )
            }
        ) else { return nil }

        let songArtists = try db.query(
            "SELECT name FROM music_song_artists WHERE song_id=? ORDER BY position",
            [.text(id)]
        ) { try $0.text(0) }

        var artistsBySource: [String: [String]] = [:]
        try db.scan(
            "SELECT source_key,name FROM music_song_source_artists WHERE song_id=? ORDER BY source_key,position",
            [.text(id)]
        ) { c in
            artistsBySource[try c.text(0), default: []].append(try c.text(1))
        }

        var extraRowsBySource: [String: [SQLiteTreeNodeRow]] = [:]
        try db.scan(
            "SELECT source_key,node_id,parent_id,prop_key,item_index,kind,bool_value,int_value,real_value,text_value " +
            "FROM music_song_source_extras WHERE song_id=? ORDER BY source_key,node_id",
            [.text(id)]
        ) { c in
            let sourceKey = try c.text(0)
            extraRowsBySource[sourceKey, default: []].append(try SQLiteValueTree.readNodeRow(c, offset: 1))
        }

        var sources: [MusicSongSourceRecord] = []
        try db.scan(
            "SELECT source_key,platform,info_source,songmid,mid,native_id,hash," +
            "name,singer,album_name,duration,img,quality_known " +
            "FROM music_song_sources WHERE song_id=? ORDER BY position",
            [.text(id)]
        ) { c in
            let sourceKey = try c.text(0)
            let extras: SQLiteTreeValue?
            if let rows = extraRowsBySource[sourceKey], !rows.isEmpty {
                extras = try SQLiteValueTree.decode(rows)
            } else {
                extras = nil
            }
            sources.append(MusicSongSourceRecord(
                sourceKey: sourceKey,
                platform: try c.text(1),
                infoSource: try c.text(2),
                songmid: try c.optionalText(3),
                mid: try c.optionalText(4),
                nativeID: try c.optionalText(5),
                hash: try c.optionalText(6),
                name: try c.optionalText(7),
                singer: try c.optionalText(8),
                albumName: try c.optionalText(9),
                duration: try c.optionalDouble(10),
                img: try c.optionalText(11),
                qualityKnown: try c.optionalBool(12),
                artistNames: artistsBySource[sourceKey] ?? [],
                extras: extras
            ))
        }

        return MusicSongRecord(
            songID: row.songID,
            title: row.title,
            artist: row.artist,
            album: row.album,
            duration: row.duration,
            baselineDuration: row.baselineDuration,
            coverURL: row.coverURL,
            artistNames: songArtists,
            localFileID: row.localFileID,
            artworkSourceKey: row.artworkSourceKey,
            artworkSourceImage: row.artworkSourceImage,
            sources: sources
        )
    }

    static func writeIncremental(_ db: SQLiteConnection,
                                 song: MusicSongRecord,
                                 previous: MusicSongRecord?,
                                 matchKey: String?,
                                 searchQuery: String) throws {
        let id = song.songID
        let positiveDuration: SQLiteValue = song.duration.flatMap { $0.isFinite && $0 > 0 ? .real($0) : nil } ?? .null
        let positiveBaseline: SQLiteValue = song.baselineDuration.flatMap { $0.isFinite && $0 > 0 ? .real($0) : nil } ?? .null

        try db.upsert(
            "music_songs",
            columns: [
                "song_id", "title", "artist", "album", "duration", "baseline_duration",
                "cover_url", "local_file_id", "artwork_source_key", "artwork_source_image",
                "match_key", "search_query"
            ],
            values: [
                .text(id),
                .text(song.title),
                .text(song.artist),
                .text(song.album),
                positiveDuration,
                positiveBaseline,
                .text(song.coverURL),
                song.localFileID.flatMap { $0.isEmpty ? nil : .text($0) } ?? .null,
                song.artworkSourceKey.flatMap { $0.isEmpty ? nil : .text($0) } ?? .null,
                song.artworkSourceImage.flatMap { $0.isEmpty ? nil : .text($0) } ?? .null,
                .optional(matchKey),
                .text(searchQuery)
            ],
            key: ["song_id"]
        )

        let nextArtists = song.artistNames
        let prevArtists = previous?.artistNames ?? []
        if previous == nil || prevArtists != nextArtists {
            let prefix = previous == nil ? 0 : MusicPersistenceListDiff.commonPrefixLength(prevArtists, nextArtists)
            if previous != nil && prefix < prevArtists.count {
                try db.execute(
                    "DELETE FROM music_song_artists WHERE song_id=? AND position>=?",
                    [.text(id), .integer(prefix)]
                )
            }
            for index in prefix..<nextArtists.count {
                try db.execute(
                    "INSERT INTO music_song_artists(song_id,position,name) VALUES(?,?,?)",
                    [.text(id), .integer(index), .text(nextArtists[index])]
                )
            }
        }

        var prevSourcesByKey: [String: (pos: Int, value: MusicSongSourceRecord)] = [:]
        if let prevSources = previous?.sources {
            for (index, source) in prevSources.enumerated() {
                prevSourcesByKey[source.sourceKey] = (index, source)
            }
        }

        let nextKeys = Set(song.sources.map(\.sourceKey))
        for oldKey in prevSourcesByKey.keys where !nextKeys.contains(oldKey) {
            try db.execute(
                "DELETE FROM music_song_sources WHERE song_id=? AND source_key=?",
                [.text(id), .text(oldKey)]
            )
        }

        for (sourceIndex, source) in song.sources.enumerated() {
            let sourceKey = source.sourceKey
            if let old = prevSourcesByKey[sourceKey], old.pos == sourceIndex, old.value == source {
                continue
            }

            let sourceDuration: SQLiteValue = source.duration.flatMap { $0.isFinite && $0 > 0 ? .real($0) : nil } ?? .null
            let values: [SQLiteValue] = [
                .text(id),
                .text(sourceKey),
                .integer(sourceIndex),
                .text(source.platform),
                .text(source.infoSource),
                .optional(source.songmid),
                .optional(source.mid),
                .optional(source.nativeID),
                .optional(source.hash),
                .optional(source.name),
                .optional(source.singer),
                .optional(source.albumName),
                sourceDuration,
                .optional(source.img),
                .optional(source.qualityKnown)
            ]
            try db.upsert("music_song_sources", columns: sourceColumns, values: values, key: ["song_id", "source_key"])

            let existed = prevSourcesByKey[sourceKey] != nil
            let prevSrcArtists = prevSourcesByKey[sourceKey]?.value.artistNames ?? []
            if !existed || prevSrcArtists != source.artistNames {
                let prefix = existed ? MusicPersistenceListDiff.commonPrefixLength(prevSrcArtists, source.artistNames) : 0
                if existed && prefix < prevSrcArtists.count {
                    try db.execute(
                        "DELETE FROM music_song_source_artists WHERE song_id=? AND source_key=? AND position>=?",
                        [.text(id), .text(sourceKey), .integer(prefix)]
                    )
                }
                for artistIndex in prefix..<source.artistNames.count {
                    try db.execute(
                        "INSERT INTO music_song_source_artists(song_id,source_key,position,name) VALUES(?,?,?,?)",
                        [.text(id), .text(sourceKey), .integer(artistIndex), .text(source.artistNames[artistIndex])]
                    )
                }
            }

            if !existed || prevSourcesByKey[sourceKey]?.value.extras != source.extras {
                if existed {
                    try db.execute(
                        "DELETE FROM music_song_source_extras WHERE song_id=? AND source_key=?",
                        [.text(id), .text(sourceKey)]
                    )
                }
                if let extras = source.extras {
                    for nodeRow in try SQLiteValueTree.rows(extras) {
                        try db.execute(extraInsertSQL, [.text(id), .text(sourceKey)] + nodeRow)
                    }
                }
            }
        }
    }
}
