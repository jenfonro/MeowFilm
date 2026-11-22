import Foundation
import MeowMusicFeature
import MeowStorage

enum MusicSongStore {
    private static let pureScalarInfoKeys: Set<String> = [
        "source", "hash", "name", "singer", "albumName", "duration", "img", "qualityKnown", "artistNames"
    ]
    private static let indexedIDKeys: Set<String> = ["songmid", "mid", "id"]

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

    private static func readIDAsText(_ value: MJ) -> (text: String?, needsTypedExtra: Bool) {
        switch value {
        case .null: return (nil, false)
        case .string(let text): return (text, false)
        case .number(let number):
            guard number.isFinite else { return (nil, true) }
            if number.rounded(.towardZero) == number,
               abs(number) <= Double(MusicPersistenceHelpers.maxSafeInteger),
               let integer = Int64(exactly: number) {
                return (String(integer), true)
            }
            return (MJ.number(number).json, true)
        case .upstreamNumber(let token):
            return (token, true)
        default:
            return (nil, true)
        }
    }

    private static func mergeFields(_ base: [(String, MJ)], overrides: [(String, MJ)]) -> [(String, MJ)] {
        var result = base
        for (key, val) in overrides {
            if let idx = result.firstIndex(where: { $0.0.utf8.elementsEqual(key.utf8) }) {
                result[idx].1 = val
            } else {
                result.append((key, val))
            }
        }
        return result
    }

    static func read(_ db: SQLiteConnection, id: String) throws -> MJ? {
        guard let row = try db.queryOne(
            "SELECT song_id,title,artist,album,duration,baseline_duration,cover_url," +
            "local_file_id,artwork_source_key,artwork_source_image,fingerprint " +
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
                    artworkSourceImage: try c.optionalText(9),
                    fingerprint: try c.optionalText(10)
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

        var extraRowsBySource: [String: [SQLiteRow]] = [:]
        for item in try db.query(
            "SELECT source_key,node_id,parent_id,prop_key,item_index,kind,bool_value,int_value,real_value,text_value " +
            "FROM music_song_source_extras WHERE song_id=? ORDER BY source_key,node_id",
            [.text(id)]
        ) {
            extraRowsBySource[try item.text("source_key"), default: []].append(item)
        }

        var sources: [MJ] = []
        try db.scan(
            "SELECT source_key,platform,info_source,songmid,mid,native_id,hash," +
            "name,singer,album_name,duration,img,quality_known " +
            "FROM music_song_sources WHERE song_id=? ORDER BY position",
            [.text(id)]
        ) { src in
            let sourceKey = try src.text(0)
            let platform = try src.text(1)
            let infoSource = try src.text(2)
            var infoFields: [(String, MJ)] = [("source", .string(infoSource))]
            if let songmid = try src.optionalText(3) { infoFields.append(("songmid", .string(songmid))) }
            if let mid = try src.optionalText(4) { infoFields.append(("mid", .string(mid))) }
            if let nativeID = try src.optionalText(5) { infoFields.append(("id", .string(nativeID))) }
            if let hash = try src.optionalText(6) { infoFields.append(("hash", .string(hash))) }
            if let name = try src.optionalText(7) { infoFields.append(("name", .string(name))) }
            if let singer = try src.optionalText(8) { infoFields.append(("singer", .string(singer))) }
            if let albumName = try src.optionalText(9) { infoFields.append(("albumName", .string(albumName))) }
            if let duration = try src.optionalDouble(10) { infoFields.append(("duration", .number(duration))) }
            if let img = try src.optionalText(11) { infoFields.append(("img", .string(img))) }
            if let qualityKnown = try src.optionalBool(12) { infoFields.append(("qualityKnown", .bool(qualityKnown))) }
            if let artists = artistsBySource[sourceKey], !artists.isEmpty {
                infoFields.append(("artistNames", .strings(artists)))
            }

            var extraSourceFields: [(String, MJ)] = []
            if let extraRows = extraRowsBySource[sourceKey], !extraRows.isEmpty {
                let tree = try SQLiteValueTree.value(extraRows)
                let decoded = try MusicPersistenceHelpers.mj(from: tree)
                if decoded["musicInfo"].isObject {
                    infoFields = mergeFields(infoFields, overrides: decoded["musicInfo"].fields)
                }
                if decoded["source"].isObject {
                    extraSourceFields = decoded["source"].fields
                }
            }

            var sourceFields: [(String, MJ)] = [
                ("sourceKey", .string(sourceKey)),
                ("source", .string(platform)),
                ("musicInfo", .obj(infoFields))
            ]
            sourceFields.append(contentsOf: extraSourceFields)
            sources.append(.obj(sourceFields))
        }

        var songFields: [(String, MJ)] = [
            ("songId", .string(row.songID)),
            ("title", .string(row.title)),
            ("artist", .string(row.artist)),
            ("album", .string(row.album))
        ]
        if let duration = row.duration { songFields.append(("duration", .number(duration))) }
        if let baselineDuration = row.baselineDuration { songFields.append(("baselineDuration", .number(baselineDuration))) }
        songFields.append(("cover_url", .string(row.coverURL)))
        if !songArtists.isEmpty { songFields.append(("artistNames", .strings(songArtists))) }
        if let localFileID = row.localFileID { songFields.append(("localFileId", .string(localFileID))) }
        if let artworkSourceKey = row.artworkSourceKey { songFields.append(("artworkSourceKey", .string(artworkSourceKey))) }
        if let artworkSourceImage = row.artworkSourceImage { songFields.append(("artworkSourceImage", .string(artworkSourceImage))) }
        if let fingerprint = row.fingerprint { songFields.append(("fingerprint", .string(fingerprint))) }
        songFields.append(("sources", .array(sources)))
        return .obj(songFields)
    }

    static func writeIncremental(_ db: SQLiteConnection, song: MJ, previous: MJ?, id: String,
                                 lookupKey: String?, searchQuery: String, baselineDuration: Double?) throws {
        let resolvedBaseline: SQLiteValue
        if let baselineDuration, baselineDuration.isFinite, baselineDuration > 0 {
            resolvedBaseline = .real(baselineDuration)
        } else {
            resolvedBaseline = MusicPersistenceHelpers.positiveDouble(song["baselineDuration"])
        }

        try db.upsert(
            "music_songs",
            columns: [
                "song_id", "title", "artist", "album", "duration", "baseline_duration",
                "cover_url", "local_file_id", "artwork_source_key", "artwork_source_image",
                "lookup_key", "search_query", "fingerprint"
            ],
            values: [
                .text(id),
                .text(song["title"].string),
                .text(song["artist"].string),
                .text(song["album"].string),
                MusicPersistenceHelpers.positiveDouble(song["duration"]),
                resolvedBaseline,
                .text(song["cover_url"].string),
                MusicPersistenceHelpers.optionalNonEmpty(song["localFileId"]),
                MusicPersistenceHelpers.optionalNonEmpty(song["artworkSourceKey"]),
                MusicPersistenceHelpers.optionalNonEmpty(song["artworkSourceImage"]),
                .optional(lookupKey),
                .text(searchQuery),
                MusicPersistenceHelpers.optionalNonEmpty(song["fingerprint"])
            ],
            key: ["song_id"]
        )

        let nextArtists = song["artistNames"].strings
        let prevArtists = previous?["artistNames"].strings ?? []
        if previous == nil || prevArtists != nextArtists {
            let prefix = previous == nil ? 0 : MusicPersistenceHelpers.commonPrefixLength(prevArtists, nextArtists)
            if previous != nil && prefix < prevArtists.count {
                try db.execute("DELETE FROM music_song_artists WHERE song_id=? AND position>=?", [.text(id), .integer(prefix)])
            }
            for index in prefix..<nextArtists.count {
                try db.execute(
                    "INSERT INTO music_song_artists(song_id,position,name) VALUES(?,?,?)",
                    [.text(id), .integer(index), .text(nextArtists[index])]
                )
            }
        }

        var prevSourcesByKey: [String: (pos: Int, value: MJ)] = [:]
        if let prevSources = previous?["sources"].array {
            for (idx, src) in prevSources.enumerated() {
                prevSourcesByKey[src["sourceKey"].string] = (idx, src)
            }
        }

        let nextSources = song["sources"].array
        let nextKeys = Set(nextSources.map { $0["sourceKey"].string })
        for oldKey in prevSourcesByKey.keys where !nextKeys.contains(oldKey) {
            try db.execute("DELETE FROM music_song_sources WHERE song_id=? AND source_key=?", [.text(id), .text(oldKey)])
        }

        for (sourceIndex, source) in nextSources.enumerated() {
            let sourceKey = source["sourceKey"].string
            if let old = prevSourcesByKey[sourceKey],
               old.pos == sourceIndex,
               MusicPersistenceHelpers.semanticEquals(source, old.value) {
                continue
            }

            let info = source["musicInfo"]
            let platform = source["source"].string.isEmpty ? info["source"].string : source["source"].string
            let infoSource = info["source"].string.isEmpty ? platform : info["source"].string

            let songmid = readIDAsText(info["songmid"])
            let mid = readIDAsText(info["mid"])
            let nativeID = readIDAsText(info["id"])

            let values: [SQLiteValue] = [
                .text(id),
                .text(sourceKey),
                .integer(sourceIndex),
                .text(platform),
                .text(infoSource),
                .optional(songmid.text),
                .optional(mid.text),
                .optional(nativeID.text),
                MusicPersistenceHelpers.optionalString(info["hash"]),
                MusicPersistenceHelpers.optionalString(info["name"]),
                MusicPersistenceHelpers.optionalString(info["singer"]),
                MusicPersistenceHelpers.optionalString(info["albumName"]),
                MusicPersistenceHelpers.positiveDouble(info["duration"]),
                MusicPersistenceHelpers.optionalString(info["img"]),
                MusicPersistenceHelpers.optionalBool(info["qualityKnown"])
            ]
            try db.upsert("music_song_sources", columns: sourceColumns, values: values, key: ["song_id", "source_key"])

            let nextSrcArtists = info["artistNames"].strings
            let prevSrcArtists = prevSourcesByKey[sourceKey]?.value["musicInfo"]["artistNames"].strings ?? []
            let existed = prevSourcesByKey[sourceKey] != nil
            if !existed || prevSrcArtists != nextSrcArtists {
                let prefix = existed ? MusicPersistenceHelpers.commonPrefixLength(prevSrcArtists, nextSrcArtists) : 0
                if existed && prefix < prevSrcArtists.count {
                    try db.execute(
                        "DELETE FROM music_song_source_artists WHERE song_id=? AND source_key=? AND position>=?",
                        [.text(id), .text(sourceKey), .integer(prefix)]
                    )
                }
                for artistIndex in prefix..<nextSrcArtists.count {
                    try db.execute(
                        "INSERT INTO music_song_source_artists(song_id,source_key,position,name) VALUES(?,?,?,?)",
                        [.text(id), .text(sourceKey), .integer(artistIndex), .text(nextSrcArtists[artistIndex])]
                    )
                }
            }

            let extraInfo = info.fields.filter { field in
                if pureScalarInfoKeys.contains(field.0) { return false }
                if indexedIDKeys.contains(field.0) {
                    switch field.0 {
                    case "songmid": return songmid.needsTypedExtra
                    case "mid": return mid.needsTypedExtra
                    case "id": return nativeID.needsTypedExtra
                    default: return false
                    }
                }
                return true
            }
            let extraSource = source.fields.filter { !["sourceKey", "source", "musicInfo"].contains($0.0) }
            try db.execute("DELETE FROM music_song_source_extras WHERE song_id=? AND source_key=?", [.text(id), .text(sourceKey)])
            if !extraInfo.isEmpty || !extraSource.isEmpty {
                var extraPayload: [(String, MJ)] = []
                if !extraInfo.isEmpty { extraPayload.append(("musicInfo", .obj(extraInfo))) }
                if !extraSource.isEmpty { extraPayload.append(("source", .obj(extraSource))) }
                let tree = try MusicPersistenceHelpers.tree(from: .obj(extraPayload))
                for nodeRow in try SQLiteValueTree.rows(tree) {
                    try db.execute(extraInsertSQL, [.text(id), .text(sourceKey)] + nodeRow)
                }
            }
        }
    }
}
