import Foundation
import MeowStorage

enum MusicPlaybackAreaStore {
    static func read(_ db: SQLiteConnection, scope: String) throws -> MusicPlaybackAreaRecord? {
        guard let row = try db.queryOne(
            "SELECT revision,mode,lyrics_enabled,show_translation,quality_request " +
            "FROM music_playback_settings WHERE scope=?",
            [.text(scope)],
            map: { c in
                (
                    revision: try c.integer(0),
                    mode: try c.integer(1),
                    lyricsEnabled: try c.bool(2),
                    showTranslation: try c.bool(3),
                    qualityRequest: try c.optionalText(4)
                )
            }
        ) else { return nil }

        let position = try db.queryOne(
            "SELECT occurrence_id,position_seconds FROM ios_music_playback_position WHERE scope=?",
            [.text(scope)],
            map: { c in
                (
                    occurrenceID: try c.optionalText(0),
                    seconds: try c.double(1)
                )
            }
        )

        var lists: [String: [String]] = [:]
        try db.scan(
            "SELECT list_kind,item_value FROM music_playback_lists WHERE scope=? ORDER BY list_kind,position",
            [.text(scope)]
        ) { c in
            lists[try c.text(0), default: []].append(try c.text(1))
        }

        var sourceExclusions: [MusicSourceExclusionRecord] = []
        var recordingExclusions: [MusicSourceExclusionRecord] = []
        try db.scan(
            "SELECT exclusion_kind,exclusion_id,song_id,provider,platform,provider_name,title,artist,source_key,excluded_at_ms " +
            "FROM music_source_exclusions WHERE scope=? ORDER BY exclusion_kind,position",
            [.text(scope)]
        ) { c in
            let kind = try c.text(0)
            let record = MusicSourceExclusionRecord(
                kind: kind,
                id: try c.text(1),
                songID: try c.text(2),
                provider: try c.text(3),
                platform: try c.text(4),
                providerName: try c.text(5),
                title: try c.text(6),
                artist: try c.text(7),
                sourceKey: try c.optionalText(8),
                excludedAtMs: try c.int64(9)
            )
            if kind == "source" {
                sourceExclusions.append(record)
            } else {
                recordingExclusions.append(record)
            }
        }

        let feedback: [MusicFeedbackPlatformRecord] = try db.query(
            "SELECT platform,enabled,generation FROM music_feedback_settings WHERE scope=? ORDER BY position,platform",
            [.text(scope)]
        ) { c in
            MusicFeedbackPlatformRecord(
                platform: try c.text(0),
                enabled: try c.bool(1),
                generation: try c.integer(2)
            )
        }

        return MusicPlaybackAreaRecord(
            revision: row.revision,
            mode: row.mode,
            lyricsEnabled: row.lyricsEnabled,
            showTranslation: row.showTranslation,
            qualityRequest: row.qualityRequest,
            positionOccurrenceID: position?.occurrenceID,
            positionSeconds: position?.seconds ?? 0,
            qualityOrder: lists["qualityOrder"] ?? [],
            qualityEnabled: lists["qualityEnabled"] ?? [],
            spatialOrder: lists["spatialOrder"] ?? [],
            spatialEnabled: lists["spatialEnabled"] ?? [],
            officialEnabled: lists["officialEnabled"] ?? [],
            metadataPlatforms: lists["metadataPlatforms"] ?? [],
            metadataEnabled: lists["metadataEnabled"] ?? [],
            sourceExclusions: sourceExclusions,
            recordingExclusions: recordingExclusions,
            feedback: feedback
        )
    }

    static func write(_ db: SQLiteConnection, scope: String, value: MusicPlaybackAreaRecord, previous: MusicPlaybackAreaRecord?) throws {
        let requestQuality: SQLiteValue = (value.qualityRequest?.isEmpty == false) ? .text(value.qualityRequest!) : .null
        if previous == nil ||
            previous?.revision != value.revision ||
            previous?.mode != value.mode ||
            previous?.lyricsEnabled != value.lyricsEnabled ||
            previous?.showTranslation != value.showTranslation ||
            previous?.qualityRequest != value.qualityRequest {
            try db.upsert(
                "music_playback_settings",
                columns: ["scope", "revision", "mode", "lyrics_enabled", "show_translation", "quality_request"],
                values: [
                    .text(scope),
                    .integer(value.revision),
                    .integer(value.mode),
                    .bool(value.lyricsEnabled),
                    .bool(value.showTranslation),
                    requestQuality
                ],
                key: ["scope"]
            )
        }

        let occurrenceID: SQLiteValue = (value.positionOccurrenceID?.isEmpty == false) ? .text(value.positionOccurrenceID!) : .null
        let seconds = value.positionSeconds.isFinite ? value.positionSeconds : 0
        if previous == nil ||
            previous?.positionOccurrenceID != value.positionOccurrenceID ||
            previous?.positionSeconds != seconds {
            try db.upsert(
                "ios_music_playback_position",
                columns: ["scope", "occurrence_id", "position_seconds"],
                values: [.text(scope), occurrenceID, .real(seconds)],
                key: ["scope"]
            )
        }

        let listPairs: [(String, [String], [String]?)] = [
            ("qualityOrder", value.qualityOrder, previous?.qualityOrder),
            ("qualityEnabled", value.qualityEnabled, previous?.qualityEnabled),
            ("spatialOrder", value.spatialOrder, previous?.spatialOrder),
            ("spatialEnabled", value.spatialEnabled, previous?.spatialEnabled),
            ("officialEnabled", value.officialEnabled, previous?.officialEnabled),
            ("metadataPlatforms", value.metadataPlatforms, previous?.metadataPlatforms),
            ("metadataEnabled", value.metadataEnabled, previous?.metadataEnabled)
        ]

        if previous == nil {
            try db.execute("DELETE FROM music_playback_lists WHERE scope=?", [.text(scope)])
        }
        for (kind, nextItems, prevItemsOpt) in listPairs {
            let prevItems = prevItemsOpt ?? []
            if previous != nil && prevItems == nextItems { continue }
            let prefix = previous == nil ? 0 : MusicPersistenceListDiff.commonPrefixLength(prevItems, nextItems)
            if previous != nil && prefix < prevItems.count {
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

        if previous == nil ||
            previous?.sourceExclusions != value.sourceExclusions ||
            previous?.recordingExclusions != value.recordingExclusions {
            struct ExKey: Hashable { let kind: String; let id: String }
            var prevMap: [ExKey: (pos: Int, row: MusicSourceExclusionRecord)] = [:]
            if let previous {
                for (index, row) in previous.sourceExclusions.enumerated() {
                    prevMap[ExKey(kind: "source", id: row.id)] = (index, row)
                }
                for (index, row) in previous.recordingExclusions.enumerated() {
                    prevMap[ExKey(kind: "recording", id: row.id)] = (index, row)
                }
            } else {
                try db.execute("DELETE FROM music_source_exclusions WHERE scope=?", [.text(scope)])
            }

            var nextItems: [(pos: Int, row: MusicSourceExclusionRecord)] = []
            var nextKeys = Set<ExKey>()
            for (index, row) in value.sourceExclusions.enumerated() {
                let normalized = MusicSourceExclusionRecord(
                    kind: "source", id: row.id, songID: row.songID,
                    provider: row.provider, platform: row.platform, providerName: row.providerName,
                    title: row.title, artist: row.artist, sourceKey: row.sourceKey, excludedAtMs: row.excludedAtMs
                )
                let key = ExKey(kind: "source", id: normalized.id)
                nextKeys.insert(key)
                nextItems.append((index, normalized))
            }
            for (index, row) in value.recordingExclusions.enumerated() {
                let normalized = MusicSourceExclusionRecord(
                    kind: "recording", id: row.id, songID: row.songID,
                    provider: row.provider, platform: row.platform, providerName: row.providerName,
                    title: row.title, artist: row.artist, sourceKey: row.sourceKey, excludedAtMs: row.excludedAtMs
                )
                let key = ExKey(kind: "recording", id: normalized.id)
                nextKeys.insert(key)
                nextItems.append((index, normalized))
            }

            for key in prevMap.keys where !nextKeys.contains(key) {
                try db.execute(
                    "DELETE FROM music_source_exclusions WHERE scope=? AND exclusion_kind=? AND exclusion_id=?",
                    [.text(scope), .text(key.kind), .text(key.id)]
                )
            }
            for (index, rec) in nextItems {
                let key = ExKey(kind: rec.kind, id: rec.id)
                if let old = prevMap[key], old.pos == index, old.row == rec { continue }
                try db.upsert(
                    "music_source_exclusions",
                    columns: [
                        "scope", "exclusion_kind", "exclusion_id", "position", "song_id",
                        "provider", "platform", "provider_name", "title", "artist", "source_key", "excluded_at_ms"
                    ],
                    values: [
                        .text(scope), .text(rec.kind), .text(rec.id), .integer(index), .text(rec.songID),
                        .text(rec.provider), .text(rec.platform), .text(rec.providerName),
                        .text(rec.title), .text(rec.artist), .optional(rec.sourceKey), .integer(rec.excludedAtMs)
                    ],
                    key: ["scope", "exclusion_kind", "exclusion_id"]
                )
            }
        }

        if previous == nil || previous?.feedback != value.feedback {
            var prevFeedback: [String: (pos: Int, row: MusicFeedbackPlatformRecord)] = [:]
            if let previous {
                for (index, row) in previous.feedback.enumerated() {
                    prevFeedback[row.platform] = (index, row)
                }
            } else {
                try db.execute("DELETE FROM music_feedback_settings WHERE scope=?", [.text(scope)])
            }
            let nextPlatforms = Set(value.feedback.map(\.platform))
            for oldPlatform in prevFeedback.keys where !nextPlatforms.contains(oldPlatform) {
                try db.execute(
                    "DELETE FROM music_feedback_settings WHERE scope=? AND platform=?",
                    [.text(scope), .text(oldPlatform)]
                )
            }
            for (index, entry) in value.feedback.enumerated() {
                if let old = prevFeedback[entry.platform], old.pos == index, old.row == entry { continue }
                try db.upsert(
                    "music_feedback_settings",
                    columns: ["scope", "platform", "enabled", "generation", "position"],
                    values: [
                        .text(scope),
                        .text(entry.platform),
                        .bool(entry.enabled),
                        .integer(max(0, entry.generation)),
                        .integer(index)
                    ],
                    key: ["scope", "platform"]
                )
            }
        }
    }
}
