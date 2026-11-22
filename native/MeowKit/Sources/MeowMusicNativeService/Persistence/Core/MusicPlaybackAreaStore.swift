import Foundation
import MeowMusicFeature
import MeowStorage

enum MusicPlaybackAreaStore {
    private static let defaultMetadataPlatforms = MusicPublicBrowsePlatform.allCases.map(\.rawValue)
    private static let defaultOfficialSources = MusicAccountPlatform.allCases.map(\.rawValue)

    private struct ExclusionRow: Equatable {
        let kind: String
        let id: String
        let songID: String
        let title: String
        let artist: String
        let provider: String
        let platform: String
        let sourceKey: String?
    }

    static func read(_ db: SQLiteConnection, scope: String) throws -> MJ? {
        guard let row = try db.queryOne(
            "SELECT revision,mode,lyrics_enabled,show_translation,quality_request,position_occurrence_id,position_seconds " +
            "FROM music_playback_settings WHERE scope=?",
            [.text(scope)],
            map: { c in
                (
                    revision: try c.integer(0),
                    mode: try c.integer(1),
                    lyricsEnabled: try c.bool(2),
                    showTranslation: try c.bool(3),
                    qualityRequest: try c.optionalText(4),
                    positionOccurrenceID: try c.optionalText(5),
                    positionSeconds: try c.double(6)
                )
            }
        ) else { return nil }

        var lists: [String: [String]] = [:]
        try db.scan(
            "SELECT list_kind,item_value FROM music_playback_lists WHERE scope=? ORDER BY list_kind,position",
            [.text(scope)]
        ) { c in
            lists[try c.text(0), default: []].append(try c.text(1))
        }

        let qualityFields: [(String, MJ)] = [
            ("qualityOrder", .strings(lists["qualityOrder"] ?? [])),
            ("qualityEnabled", .strings(lists["qualityEnabled"] ?? [])),
            ("spatialOrder", .strings(lists["spatialOrder"] ?? [])),
            ("spatialEnabled", .strings(lists["spatialEnabled"] ?? []))
        ]
        var fields: [(String, MJ)] = [
            ("schema", .n(1)),
            ("revision", .n(row.revision)),
            ("mode", .n(row.mode)),
            ("qualitySettings", .obj(qualityFields)),
            ("officialSources", .strings(lists["officialEnabled"] ?? [])),
            ("metadataPlatforms", .strings(lists["metadataPlatforms"] ?? [])),
            ("metadataEnabled", .strings(lists["metadataEnabled"] ?? [])),
            ("lyricsEnabled", .bool(row.lyricsEnabled)),
            ("showTranslation", .bool(row.showTranslation)),
            ("position", .obj([
                ("occurrenceId", row.positionOccurrenceID.map(MJ.string) ?? .null),
                ("seconds", .number(row.positionSeconds))
            ]))
        ]
        if let requestQuality = row.qualityRequest {
            fields.append(("requestQuality", .string(requestQuality)))
        }

        var sourceExclusions: [MJ] = []
        var recordingExclusions: [MJ] = []
        try db.scan(
            "SELECT exclusion_kind,exclusion_id,song_id,title,artist,provider,platform,source_key " +
            "FROM music_source_exclusions WHERE scope=? ORDER BY exclusion_kind,position",
            [.text(scope)]
        ) { ex in
            let kind = try ex.text(0)
            var item: [(String, MJ)] = [
                ("id", .string(try ex.text(1))),
                ("songID", .string(try ex.text(2))),
                ("title", .string(try ex.text(3))),
                ("artist", .string(try ex.text(4)))
            ]
            let provider = try ex.text(5)
            if kind == "source" || !provider.isEmpty {
                item.append(("provider", .string(provider)))
            }
            item.append(("platform", .string(try ex.text(6))))
            if let sourceKey = try ex.optionalText(7) {
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
            "music_playback_settings",
            columns: [
                "scope", "revision", "mode", "has_mode", "lyrics_enabled", "show_translation",
                "quality_request", "position_occurrence_id", "position_seconds"
            ],
            values: [
                .text(scope),
                .integer(value["revision"].int),
                .integer(value["mode"].isInteger ? value["mode"].int : 1),
                .integer(1),
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
            ("officialEnabled", value.contains("officialSources") ? value["officialSources"].strings : defaultOfficialSources),
            ("metadataPlatforms", value.contains("metadataPlatforms") ? value["metadataPlatforms"].strings : defaultMetadataPlatforms),
            ("metadataEnabled", value.contains("metadataEnabled") ? value["metadataEnabled"].strings : defaultMetadataPlatforms)
        ]

        for (kind, nextItems) in listEntries {
            let prevItems = try db.query(
                "SELECT item_value FROM music_playback_lists WHERE scope=? AND list_kind=? ORDER BY position",
                [.text(scope), .text(kind)]
            ) { try $0.text(0) }
            if prevItems == nextItems { continue }
            let prefix = MusicPersistenceHelpers.commonPrefixLength(prevItems, nextItems)
            if prefix < prevItems.count {
                try db.execute(
                    "DELETE FROM music_playback_lists WHERE scope=? AND list_kind=? AND position>=?",
                    [.text(scope), .text(kind), .integer(prefix)]
                )
            }
            for index in prefix..<nextItems.count {
                try db.execute(
                    "INSERT INTO music_playback_lists(scope,list_kind,position,item_value) VALUES(?,?,?,?)",
                    [.text(scope), .text(kind), .integer(index), .text(nextItems[index])]
                )
            }
        }

        struct ExKey: Hashable { let kind: String; let id: String }
        var prevExclusions: [ExKey: (pos: Int, row: ExclusionRow)] = [:]
        try db.scan(
            "SELECT exclusion_kind,exclusion_id,position,song_id,title,artist,provider,platform,source_key " +
            "FROM music_source_exclusions WHERE scope=?",
            [.text(scope)]
        ) { c in
            let kind = try c.text(0)
            let id = try c.text(1)
            let pos = try c.integer(2)
            prevExclusions[ExKey(kind: kind, id: id)] = (pos, ExclusionRow(
                kind: kind, id: id, songID: try c.text(3), title: try c.text(4),
                artist: try c.text(5), provider: try c.text(6), platform: try c.text(7),
                sourceKey: try c.optionalText(8)
            ))
        }

        var nextExclusions: [(pos: Int, row: ExclusionRow)] = []
        var nextKeys = Set<ExKey>()
        for (kind, field) in [("source", "sourceExclusions"), ("recording", "recordingSourceExclusions")] {
            for (index, row) in value[field].array.enumerated() {
                let rec = ExclusionRow(
                    kind: kind,
                    id: row["id"].string,
                    songID: row["songID"].string,
                    title: row["title"].string,
                    artist: row["artist"].string,
                    provider: row["provider"].string,
                    platform: row["platform"].string,
                    sourceKey: MusicPersistenceHelpers.optionalTextValue(row["sourceKey"])
                )
                let key = ExKey(kind: kind, id: rec.id)
                nextKeys.insert(key)
                nextExclusions.append((index, rec))
            }
        }

        for key in prevExclusions.keys where !nextKeys.contains(key) {
            try db.execute(
                "DELETE FROM music_source_exclusions WHERE scope=? AND exclusion_kind=? AND exclusion_id=?",
                [.text(scope), .text(key.kind), .text(key.id)]
            )
        }

        for (index, rec) in nextExclusions {
            let key = ExKey(kind: rec.kind, id: rec.id)
            if let old = prevExclusions[key], old.pos == index, old.row == rec { continue }
            try db.upsert(
                "music_source_exclusions",
                columns: [
                    "scope", "exclusion_kind", "exclusion_id", "position", "song_id",
                    "title", "artist", "provider", "platform", "source_key"
                ],
                values: [
                    .text(scope), .text(rec.kind), .text(rec.id), .integer(index), .text(rec.songID),
                    .text(rec.title), .text(rec.artist), .text(rec.provider), .text(rec.platform),
                    .optional(rec.sourceKey)
                ],
                key: ["scope", "exclusion_kind", "exclusion_id"]
            )
        }
    }
}
