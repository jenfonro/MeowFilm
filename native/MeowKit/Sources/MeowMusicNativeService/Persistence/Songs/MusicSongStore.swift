import Foundation
import MeowMusicFeature
import MeowStorage

enum MusicSongStore {
    private static let sourceIDFields: [(json: String, prefix: String)] = [
        ("songmid", "songmid"),
        ("mid", "mid"),
        ("id", "id"),
        ("songId", "song_id"),
        ("qqSongId", "qq_song_id"),
        ("songType", "song_type"),
        ("type", "type"),
        ("copyrightId", "copyright_id"),
        ("strMediaMid", "str_media_mid"),
        ("albumId", "album_id"),
        ("album_id", "album_id_snake"),
        ("albumMid", "album_mid"),
        ("albumAudioId", "album_audio_id"),
        ("mixsongid", "mixsongid"),
        ("fileid", "fileid"),
        ("fileId", "file_id_camel"),
        ("contentId", "content_id"),
        ("size", "size")
    ]

    private static let sourceKnownInfoKeys: Set<String> = Set(
        sourceIDFields.map(\.json) + [
            "source", "hash", "md5", "androidMd5", "name", "singer",
            "albumName", "duration", "img", "qualityKnown",
            "artistNames", "types", "_types", "audioFormats", "newRateFormats"
        ]
    )

    private static let formatIDFields: [(json: String, prefix: String)] = [
        ("formatType", "format_type"),
        ("resourceType", "resource_type"),
        ("size", "size"),
        ("androidSize", "android_size"),
        ("iosSize", "ios_size"),
        ("asize", "asize"),
        ("isize", "isize"),
        ("format", "format"),
        ("androidFormat", "android_format"),
        ("iosFormat", "ios_format"),
        ("aformat", "aformat"),
        ("iformat", "iformat"),
        ("bitrate", "bitrate"),
        ("sampleRate", "sample_rate"),
        ("bitDepth", "bit_depth"),
        ("fileType", "file_type"),
        ("androidFileType", "android_file_type"),
        ("iosFileType", "ios_file_type"),
        ("iosAccuracyLevel", "ios_accuracy_level"),
        ("androidAccuracyLevel", "android_accuracy_level"),
        ("fileId", "file_id"),
        ("contentId", "content_id")
    ]

    private static let sourceInsertSQL: String = {
        var cols = ["song_id", "source_key", "position", "platform", "info_source"]
        for field in sourceIDFields {
            cols.append("\(field.prefix)_kind")
            cols.append("\(field.prefix)_value")
        }
        cols.append(contentsOf: [
            "hash", "md5", "android_md5", "name", "singer", "album_name",
            "duration", "img", "quality_known"
        ])
        let placeholders = Array(repeating: "?", count: cols.count).joined(separator: ",")
        return "INSERT INTO music_song_sources(\(cols.joined(separator: ","))) VALUES(\(placeholders))"
    }()

    private static let formatInsertSQL: String = {
        var cols = ["song_id", "source_key", "list_kind", "position"]
        for field in formatIDFields {
            cols.append("\(field.prefix)_kind")
            cols.append("\(field.prefix)_value")
        }
        cols.append(contentsOf: ["hash", "md5", "android_md5"])
        let placeholders = Array(repeating: "?", count: cols.count).joined(separator: ",")
        return "INSERT INTO music_song_source_formats(\(cols.joined(separator: ","))) VALUES(\(placeholders))"
    }()

    private static let extraInsertSQL: String = {
        let cols = ["song_id", "source_key"] + SQLiteValueTree.columns
        let placeholders = Array(repeating: "?", count: cols.count).joined(separator: ",")
        return "INSERT INTO music_song_source_extras(\(cols.joined(separator: ","))) VALUES(\(placeholders))"
    }()

    static func read(_ db: SQLiteConnection, id: String) throws -> MJ? {
        guard let row = try db.query(
            "SELECT song_id,title,artist,album,duration,baseline_duration,cover_url," +
            "local_file_id,artwork_source_key,artwork_source_image,fingerprint " +
            "FROM music_songs WHERE song_id=?",
            [.text(id)]
        ).first else { return nil }

        let songArtists = try db.query(
            "SELECT name FROM music_song_artists WHERE song_id=? ORDER BY position",
            [.text(id)]
        ).map { try $0.text("name") }

        let sourceRows = try db.query(
            "SELECT * FROM music_song_sources WHERE song_id=? ORDER BY position",
            [.text(id)]
        )

        var artistsBySource: [String: [String]] = [:]
        for item in try db.query(
            "SELECT source_key,name FROM music_song_source_artists WHERE song_id=? ORDER BY source_key,position",
            [.text(id)]
        ) {
            artistsBySource[try item.text("source_key"), default: []].append(try item.text("name"))
        }

        var typesBySource: [String: [MJ]] = [:]
        var underscoreTypesBySource: [String: [(String, MJ)]] = [:]
        for item in try db.query(
            "SELECT source_key,list_kind,entry_key,type_kind,type_value,hash,size_kind,size_value " +
            "FROM music_song_source_types WHERE song_id=? ORDER BY source_key,list_kind,position",
            [.text(id)]
        ) {
            let key = try item.text("source_key")
            let kind = try item.text("list_kind")
            var entryFields: [(String, MJ)] = []
            if kind == "types",
               let typeVal = try MusicPersistenceHelpers.readIdentifier(item, kindColumn: "type_kind", valueColumn: "type_value") {
                entryFields.append(("type", typeVal))
            }
            if let hash = try item.optionalText("hash") {
                entryFields.append(("hash", .string(hash)))
            }
            if let sizeVal = try MusicPersistenceHelpers.readIdentifier(item, kindColumn: "size_kind", valueColumn: "size_value") {
                entryFields.append(("size", sizeVal))
            }
            if kind == "types" {
                typesBySource[key, default: []].append(.obj(entryFields))
            } else {
                underscoreTypesBySource[key, default: []].append((try item.text("entry_key"), .obj(entryFields)))
            }
        }

        var formatsBySource: [String: [String: [MJ]]] = [:]
        for item in try db.query(
            "SELECT * FROM music_song_source_formats WHERE song_id=? ORDER BY source_key,list_kind,position",
            [.text(id)]
        ) {
            let key = try item.text("source_key")
            let kind = try item.text("list_kind")
            var formatFields: [(String, MJ)] = []
            for field in formatIDFields {
                if let val = try MusicPersistenceHelpers.readIdentifier(
                    item,
                    kindColumn: "\(field.prefix)_kind",
                    valueColumn: "\(field.prefix)_value"
                ) {
                    formatFields.append((field.json, val))
                }
            }
            if let hash = try item.optionalText("hash") { formatFields.append(("hash", .string(hash))) }
            if let md5 = try item.optionalText("md5") { formatFields.append(("md5", .string(md5))) }
            if let androidMd5 = try item.optionalText("android_md5") {
                formatFields.append(("androidMd5", .string(androidMd5)))
            }
            formatsBySource[key, default: [:]][kind, default: []].append(.obj(formatFields))
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
        sources.reserveCapacity(sourceRows.count)
        for src in sourceRows {
            let sourceKey = try src.text("source_key")
            let platform = try src.text("platform")
            let infoSource = try src.text("info_source")
            var infoFields: [(String, MJ)] = [("source", .string(infoSource))]
            for field in sourceIDFields {
                if let val = try MusicPersistenceHelpers.readIdentifier(
                    src,
                    kindColumn: "\(field.prefix)_kind",
                    valueColumn: "\(field.prefix)_value"
                ) {
                    infoFields.append((field.json, val))
                }
            }
            if let hash = try src.optionalText("hash") { infoFields.append(("hash", .string(hash))) }
            if let md5 = try src.optionalText("md5") { infoFields.append(("md5", .string(md5))) }
            if let androidMd5 = try src.optionalText("android_md5") { infoFields.append(("androidMd5", .string(androidMd5))) }
            if let name = try src.optionalText("name") { infoFields.append(("name", .string(name))) }
            if let singer = try src.optionalText("singer") { infoFields.append(("singer", .string(singer))) }
            if let albumName = try src.optionalText("album_name") { infoFields.append(("albumName", .string(albumName))) }
            if let duration = try src.optionalDouble("duration") { infoFields.append(("duration", .number(duration))) }
            if let img = try src.optionalText("img") { infoFields.append(("img", .string(img))) }
            if let qualityKnown = try src.optionalBool("quality_known") {
                infoFields.append(("qualityKnown", .bool(qualityKnown)))
            }
            if let artists = artistsBySource[sourceKey], !artists.isEmpty {
                infoFields.append(("artistNames", .strings(artists)))
            }
            if let types = typesBySource[sourceKey], !types.isEmpty {
                infoFields.append(("types", .array(types)))
            }
            if let underscoreTypes = underscoreTypesBySource[sourceKey], !underscoreTypes.isEmpty {
                infoFields.append(("_types", .obj(underscoreTypes)))
            }
            if let audioFormats = formatsBySource[sourceKey]?["audioFormats"], !audioFormats.isEmpty {
                infoFields.append(("audioFormats", .array(audioFormats)))
            }
            if let newRateFormats = formatsBySource[sourceKey]?["newRateFormats"], !newRateFormats.isEmpty {
                infoFields.append(("newRateFormats", .array(newRateFormats)))
            }

            var extraSourceFields: [(String, MJ)] = []
            if let extraRows = extraRowsBySource[sourceKey], !extraRows.isEmpty {
                let tree = try SQLiteValueTree.value(extraRows)
                let decoded = try MusicPersistenceHelpers.mj(from: tree)
                if decoded["musicInfo"].isObject {
                    infoFields.append(contentsOf: decoded["musicInfo"].fields)
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
            ("songId", .string(try row.text("song_id"))),
            ("title", .string(try row.text("title"))),
            ("artist", .string(try row.text("artist"))),
            ("album", .string(try row.text("album")))
        ]
        if let duration = try row.optionalDouble("duration") {
            songFields.append(("duration", .number(duration)))
        }
        if let baselineDuration = try row.optionalDouble("baseline_duration") {
            songFields.append(("baselineDuration", .number(baselineDuration)))
        }
        songFields.append(("cover_url", .string(try row.text("cover_url"))))
        if !songArtists.isEmpty {
            songFields.append(("artistNames", .strings(songArtists)))
        }
        if let localFileID = try row.optionalText("local_file_id") {
            songFields.append(("localFileId", .string(localFileID)))
        }
        if let artworkSourceKey = try row.optionalText("artwork_source_key") {
            songFields.append(("artworkSourceKey", .string(artworkSourceKey)))
        }
        if let artworkSourceImage = try row.optionalText("artwork_source_image") {
            songFields.append(("artworkSourceImage", .string(artworkSourceImage)))
        }
        if let fingerprint = try row.optionalText("fingerprint") {
            songFields.append(("fingerprint", .string(fingerprint)))
        }
        songFields.append(("sources", .array(sources)))
        return .obj(songFields)
    }

    static func write(_ db: SQLiteConnection, song: MJ, id: String,
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

        try db.execute("DELETE FROM music_song_artists WHERE song_id=?", [.text(id)])
        for (index, name) in song["artistNames"].strings.enumerated() {
            try db.execute(
                "INSERT INTO music_song_artists(song_id,position,name) VALUES(?,?,?)",
                [.text(id), .integer(index), .text(name)]
            )
        }

        // Cascades to music_song_source_artists, _types, _formats, _extras.
        try db.execute("DELETE FROM music_song_sources WHERE song_id=?", [.text(id)])
        for (sourceIndex, source) in song["sources"].array.enumerated() {
            let sourceKey = source["sourceKey"].string
            let info = source["musicInfo"]
            let platform = source["source"].string.isEmpty ? info["source"].string : source["source"].string
            let infoSource = info["source"].string.isEmpty ? platform : info["source"].string

            var values: [SQLiteValue] = [
                .text(id),
                .text(sourceKey),
                .integer(sourceIndex),
                .text(platform),
                .text(infoSource)
            ]
            for field in sourceIDFields {
                let (kind, val) = MusicPersistenceHelpers.bindIdentifier(info[field.json])
                values.append(kind)
                values.append(val)
            }
            values.append(contentsOf: [
                MusicPersistenceHelpers.optionalString(info["hash"]),
                MusicPersistenceHelpers.optionalString(info["md5"]),
                MusicPersistenceHelpers.optionalString(info["androidMd5"]),
                MusicPersistenceHelpers.optionalString(info["name"]),
                MusicPersistenceHelpers.optionalString(info["singer"]),
                MusicPersistenceHelpers.optionalString(info["albumName"]),
                MusicPersistenceHelpers.positiveDouble(info["duration"]),
                MusicPersistenceHelpers.optionalString(info["img"]),
                MusicPersistenceHelpers.optionalBool(info["qualityKnown"])
            ])
            try db.execute(sourceInsertSQL, values)

            for (artistIndex, artistName) in info["artistNames"].strings.enumerated() {
                try db.execute(
                    "INSERT INTO music_song_source_artists(song_id,source_key,position,name) VALUES(?,?,?,?)",
                    [.text(id), .text(sourceKey), .integer(artistIndex), .text(artistName)]
                )
            }

            for (typeIndex, row) in info["types"].array.enumerated() {
                let (typeKind, typeVal) = row.isString
                    ? MusicPersistenceHelpers.bindIdentifier(row)
                    : MusicPersistenceHelpers.bindIdentifier(row["type"])
                let (sizeKind, sizeVal) = row.isObject
                    ? MusicPersistenceHelpers.bindIdentifier(row["size"])
                    : (.null, .null)
                try db.execute(
                    "INSERT INTO music_song_source_types(" +
                    "song_id,source_key,list_kind,entry_key,position,type_kind,type_value,hash,size_kind,size_value" +
                    ") VALUES(?,?,?,?,?,?,?,?,?,?)",
                    [
                        .text(id), .text(sourceKey), .text("types"), .text(String(typeIndex)),
                        .integer(typeIndex), typeKind, typeVal,
                        row.isObject ? MusicPersistenceHelpers.optionalString(row["hash"]) : .null,
                        sizeKind, sizeVal
                    ]
                )
            }

            for (typeIndex, pair) in info["_types"].fields.enumerated() {
                let (entryKey, entryVal) = pair
                let (sizeKind, sizeVal) = entryVal.isObject
                    ? MusicPersistenceHelpers.bindIdentifier(entryVal["size"])
                    : (.null, .null)
                try db.execute(
                    "INSERT INTO music_song_source_types(" +
                    "song_id,source_key,list_kind,entry_key,position,type_kind,type_value,hash,size_kind,size_value" +
                    ") VALUES(?,?,?,?,?,?,?,?,?,?)",
                    [
                        .text(id), .text(sourceKey), .text("_types"), .text(entryKey),
                        .integer(typeIndex), .null, .null,
                        entryVal.isObject ? MusicPersistenceHelpers.optionalString(entryVal["hash"]) : .null,
                        sizeKind, sizeVal
                    ]
                )
            }

            for listKind in ["audioFormats", "newRateFormats"] {
                for (fmtIndex, fmt) in info[listKind].array.enumerated() {
                    var fmtValues: [SQLiteValue] = [
                        .text(id), .text(sourceKey), .text(listKind), .integer(fmtIndex)
                    ]
                    for field in formatIDFields {
                        let (kind, val) = MusicPersistenceHelpers.bindIdentifier(fmt[field.json])
                        fmtValues.append(kind)
                        fmtValues.append(val)
                    }
                    fmtValues.append(contentsOf: [
                        MusicPersistenceHelpers.optionalString(fmt["hash"]),
                        MusicPersistenceHelpers.optionalString(fmt["md5"]),
                        MusicPersistenceHelpers.optionalString(fmt["androidMd5"])
                    ])
                    try db.execute(formatInsertSQL, fmtValues)
                }
            }

            let unknownInfo = info.fields.filter { !sourceKnownInfoKeys.contains($0.0) }
            let unknownSource = source.fields.filter { !["sourceKey", "source", "musicInfo"].contains($0.0) }
            if !unknownInfo.isEmpty || !unknownSource.isEmpty {
                var extraPayload: [(String, MJ)] = []
                if !unknownInfo.isEmpty { extraPayload.append(("musicInfo", .obj(unknownInfo))) }
                if !unknownSource.isEmpty { extraPayload.append(("source", .obj(unknownSource))) }
                let tree = try MusicPersistenceHelpers.tree(from: .obj(extraPayload))
                for nodeRow in try SQLiteValueTree.rows(tree) {
                    try db.execute(extraInsertSQL, [.text(id), .text(sourceKey)] + nodeRow)
                }
            }
        }
    }
}
