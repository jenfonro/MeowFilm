import Foundation
import MeowMusicFeature
import MeowStorage

enum MusicPlaybackAreaStore {
    private static let defaultMetadataPlatforms = MusicPublicBrowsePlatform.allCases.map(\.rawValue)
    private static let defaultOfficialSources = MusicAccountPlatform.allCases.map(\.rawValue)

    static func read(_ db: SQLiteConnection, scope: String) throws -> MJ? {
        guard let row = try db.query(
            "SELECT revision,mode,lyrics_enabled,show_translation,quality_request,position_occurrence_id,position_seconds " +
            "FROM ios_music_playback_settings WHERE scope=?",
            [.text(scope)]
        ).first else { return nil }

        var lists: [String: [String]] = [:]
        for item in try db.query(
            "SELECT list_kind,item_value FROM ios_music_playback_lists WHERE scope=? ORDER BY list_kind,position",
            [.text(scope)]
        ) {
            lists[try item.text("list_kind"), default: []].append(try item.text("item_value"))
        }

        let qualityFields: [(String, MJ)] = [
            ("qualityOrder", .strings(lists["qualityOrder"] ?? [])),
            ("qualityEnabled", .strings(lists["qualityEnabled"] ?? [])),
            ("spatialOrder", .strings(lists["spatialOrder"] ?? [])),
            ("spatialEnabled", .strings(lists["spatialEnabled"] ?? []))
        ]
        var fields: [(String, MJ)] = [
            ("schema", .n(1)),
            ("revision", .n(try row.integer("revision"))),
            ("mode", .n(try row.integer("mode"))),
            ("qualitySettings", .obj(qualityFields)),
            ("officialSources", .strings(lists["officialSources"] ?? [])),
            ("metadataPlatforms", .strings(lists["metadataPlatforms"] ?? [])),
            ("metadataEnabled", .strings(lists["metadataEnabled"] ?? [])),
            ("lyricsEnabled", .bool(try row.bool("lyrics_enabled"))),
            ("showTranslation", .bool(try row.bool("show_translation"))),
            ("position", .obj([
                ("occurrenceId", (try row.optionalText("position_occurrence_id")).map(MJ.string) ?? .null),
                ("seconds", .number(try row.double("position_seconds")))
            ]))
        ]
        if let requestQuality = try row.optionalText("quality_request") {
            fields.append(("requestQuality", .string(requestQuality)))
        }

        var sourceExclusions: [MJ] = []
        var recordingExclusions: [MJ] = []
        for ex in try db.query(
            "SELECT exclusion_kind,exclusion_id,song_id,title,artist,provider,platform,source_key " +
            "FROM ios_music_source_exclusions WHERE scope=? ORDER BY exclusion_kind,position",
            [.text(scope)]
        ) {
            let kind = try ex.text("exclusion_kind")
            var item: [(String, MJ)] = [
                ("id", .string(try ex.text("exclusion_id"))),
                ("songID", .string(try ex.text("song_id"))),
                ("title", .string(try ex.text("title"))),
                ("artist", .string(try ex.text("artist")))
            ]
            let provider = try ex.text("provider")
            if kind == "source" || !provider.isEmpty {
                item.append(("provider", .string(provider)))
            }
            item.append(("platform", .string(try ex.text("platform"))))
            if let sourceKey = try ex.optionalText("source_key") {
                item.append(("sourceKey", .string(sourceKey)))
            }
            if kind == "source" {
                sourceExclusions.append(.obj(item))
            } else {
                recordingExclusions.append(.obj(item))
            }
        }
        if !sourceExclusions.isEmpty {
            fields.append(("sourceExclusions", .array(sourceExclusions)))
        }
        if !recordingExclusions.isEmpty {
            fields.append(("recordingSourceExclusions", .array(recordingExclusions)))
        }
        return .obj(fields)
    }

    static func write(_ db: SQLiteConnection, scope: String, value: MJ) throws {
        let quality = value["qualitySettings"]
        let requestQuality: SQLiteValue
        if case .string(let text) = value["requestQuality"], !text.isEmpty {
            requestQuality = .text(text)
        } else {
            requestQuality = .null
        }
        let position = value["position"]
        let occurrenceID = MusicPersistenceHelpers.optionalNonEmpty(position["occurrenceId"])
        let seconds = MusicPersistenceHelpers.finiteDouble(position["seconds"], fallback: 0)

        try db.upsert(
            "ios_music_playback_settings",
            columns: [
                "scope", "revision", "mode", "lyrics_enabled", "show_translation",
                "quality_request", "position_occurrence_id", "position_seconds"
            ],
            values: [
                .text(scope),
                .integer(value["revision"].int),
                .integer(value["mode"].isInteger ? value["mode"].int : 1),
                .bool(value.contains("lyricsEnabled") ? value["lyricsEnabled"].bool : true),
                .bool(value.contains("showTranslation") ? value["showTranslation"].bool : true),
                requestQuality,
                occurrenceID,
                .real(seconds)
            ],
            key: ["scope"]
        )

        let listEntries: [(String, [String])] = [
            ("qualityOrder", quality.contains("qualityOrder") ? quality["qualityOrder"].strings : MusicModel.qualityOrder),
            ("qualityEnabled", quality.contains("qualityEnabled") ? quality["qualityEnabled"].strings : MusicModel.qualityOrder),
            ("spatialOrder", quality.contains("spatialOrder") ? quality["spatialOrder"].strings : MusicModel.spatialOrder),
            ("spatialEnabled", quality.contains("spatialEnabled") ? quality["spatialEnabled"].strings : MusicModel.spatialOrder),
            ("officialSources", value.contains("officialSources") ? value["officialSources"].strings : defaultOfficialSources),
            ("metadataPlatforms", value.contains("metadataPlatforms") ? value["metadataPlatforms"].strings : defaultMetadataPlatforms),
            ("metadataEnabled", value.contains("metadataEnabled") ? value["metadataEnabled"].strings : defaultMetadataPlatforms)
        ]

        try db.execute("DELETE FROM ios_music_playback_lists WHERE scope=?", [.text(scope)])
        for (kind, items) in listEntries {
            for (index, item) in items.enumerated() {
                try db.execute(
                    "INSERT INTO ios_music_playback_lists(scope,list_kind,position,item_value) VALUES(?,?,?,?)",
                    [.text(scope), .text(kind), .integer(index), .text(item)]
                )
            }
        }

        try db.execute("DELETE FROM ios_music_source_exclusions WHERE scope=?", [.text(scope)])
        for (kind, field) in [("source", "sourceExclusions"), ("recording", "recordingSourceExclusions")] {
            for (index, row) in value[field].array.enumerated() {
                try db.execute(
                    "INSERT INTO ios_music_source_exclusions(" +
                    "scope,exclusion_kind,exclusion_id,position,song_id,title,artist,provider,platform,source_key" +
                    ") VALUES(?,?,?,?,?,?,?,?,?,?)",
                    [
                        .text(scope),
                        .text(kind),
                        .text(row["id"].string),
                        .integer(index),
                        .text(row["songID"].string),
                        .text(row["title"].string),
                        .text(row["artist"].string),
                        .text(row["provider"].string),
                        .text(row["platform"].string),
                        MusicPersistenceHelpers.optionalString(row["sourceKey"])
                    ]
                )
            }
        }
    }
}
