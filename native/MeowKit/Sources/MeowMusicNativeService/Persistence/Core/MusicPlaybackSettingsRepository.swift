import Foundation
import MeowStorage

enum MusicPlaybackSettingsRepository {
    static func read(_ db: SQLiteConnection, scope: String) throws -> MusicPlaybackSettingsState? {
        guard let row = try db.queryOne(
            "SELECT revision,mode,lyrics_enabled,quality_request " +
            "FROM music_playback_settings WHERE scope=?",
            [.text(scope)],
            map: { c in
                (
                    revision: try c.integer(0),
                    mode: try c.integer(1),
                    lyricsEnabled: try c.bool(2),
                    qualityRequest: try c.optionalText(3)
                )
            }
        ) else { return nil }

        let state = try db.queryOne(
            "SELECT show_translation,occurrence_id,position_seconds FROM ios_music_playback_state WHERE scope=?",
            [.text(scope)],
            map: { c in
                (
                    showTranslation: try c.bool(0),
                    occurrenceID: try c.optionalText(1),
                    seconds: try c.double(2)
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

        let sourceExclusions: [MusicSourceExclusionRecord] = try db.query(
            "SELECT exclusion_id,song_id,provider,platform,provider_name,title,artist,excluded_at_ms " +
            "FROM music_source_exclusions WHERE scope=? ORDER BY position",
            [.text(scope)]
        ) { c in
            MusicSourceExclusionRecord(
                id: try c.text(0),
                songID: try c.text(1),
                provider: try c.text(2),
                platform: try c.text(3),
                providerName: try c.text(4),
                title: try c.text(5),
                artist: try c.text(6),
                excludedAtMs: try c.int64(7)
            )
        }

        let recordingExclusions: [MusicRecordingExclusionRecord] = try db.query(
            "SELECT exclusion_id,song_id,provider,platform,provider_name,title,artist,source_key,excluded_at_ms " +
            "FROM ios_music_recording_exclusions WHERE scope=? ORDER BY position",
            [.text(scope)]
        ) { c in
            MusicRecordingExclusionRecord(
                id: try c.text(0),
                songID: try c.text(1),
                provider: try c.text(2),
                platform: try c.text(3),
                providerName: try c.text(4),
                title: try c.text(5),
                artist: try c.text(6),
                sourceKey: try c.text(7),
                excludedAtMs: try c.int64(8)
            )
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

        let fmSelections: [MusicFMSelectionRecord] = try db.query(
            "SELECT fm_scope,provider,mode,submode,rule_id,tab_tag_id " +
            "FROM ios_music_fm_selections WHERE scope=? ORDER BY fm_scope",
            [.text(scope)]
        ) { c in
            MusicFMSelectionRecord(
                fmScope: try c.text(0),
                provider: try c.text(1),
                mode: try c.optionalText(2),
                submode: try c.text(3),
                ruleID: try c.optionalText(4),
                tabTagID: try c.optionalText(5)
            )
        }

        return MusicPlaybackSettingsState(
            revision: row.revision,
            mode: row.mode,
            lyricsEnabled: row.lyricsEnabled,
            showTranslation: state?.showTranslation ?? true,
            qualityRequest: row.qualityRequest,
            positionOccurrenceID: state?.occurrenceID,
            positionSeconds: state?.seconds ?? 0,
            qualityOrder: lists["qualityOrder"] ?? [],
            qualityEnabled: lists["qualityEnabled"] ?? [],
            spatialOrder: lists["spatialOrder"] ?? [],
            spatialEnabled: lists["spatialEnabled"] ?? [],
            officialEnabled: lists["officialEnabled"] ?? [],
            metadataPlatforms: lists["metadataPlatforms"] ?? [],
            metadataEnabled: lists["metadataEnabled"] ?? [],
            sourceExclusions: sourceExclusions,
            recordingExclusions: recordingExclusions,
            feedback: feedback,
            fmSelections: fmSelections
        )
    }

    static func applyChanges(_ db: SQLiteConnection,
                             scope: String,
                             previous: MusicPlaybackSettingsState?,
                             next: MusicPlaybackSettingsState) throws {
        let requestQuality: SQLiteValue = (next.qualityRequest?.isEmpty == false) ? .text(next.qualityRequest!) : .null
        if previous == nil ||
            previous?.revision != next.revision ||
            previous?.mode != next.mode ||
            previous?.lyricsEnabled != next.lyricsEnabled ||
            previous?.qualityRequest != next.qualityRequest {
            try db.upsert(
                "music_playback_settings",
                columns: ["scope", "revision", "mode", "lyrics_enabled", "quality_request"],
                values: [
                    .text(scope),
                    .integer(next.revision),
                    .integer(next.mode),
                    .bool(next.lyricsEnabled),
                    requestQuality
                ],
                key: ["scope"]
            )
        }

        let occurrenceID: SQLiteValue = (next.positionOccurrenceID?.isEmpty == false) ? .text(next.positionOccurrenceID!) : .null
        let seconds = next.positionSeconds.isFinite ? next.positionSeconds : 0
        if previous == nil ||
            previous?.showTranslation != next.showTranslation ||
            previous?.positionOccurrenceID != next.positionOccurrenceID ||
            previous?.positionSeconds != seconds {
            try db.upsert(
                "ios_music_playback_state",
                columns: ["scope", "show_translation", "occurrence_id", "position_seconds"],
                values: [.text(scope), .bool(next.showTranslation), occurrenceID, .real(seconds)],
                key: ["scope"]
            )
        }

        let listPairs: [(String, [String], [String]?)] = [
            ("qualityOrder", next.qualityOrder, previous?.qualityOrder),
            ("qualityEnabled", next.qualityEnabled, previous?.qualityEnabled),
            ("spatialOrder", next.spatialOrder, previous?.spatialOrder),
            ("spatialEnabled", next.spatialEnabled, previous?.spatialEnabled),
            ("officialEnabled", next.officialEnabled, previous?.officialEnabled),
            ("metadataPlatforms", next.metadataPlatforms, previous?.metadataPlatforms),
            ("metadataEnabled", next.metadataEnabled, previous?.metadataEnabled)
        ]

        for (kind, nextItems, prevItemsOpt) in listPairs {
            if previous != nil && prevItemsOpt == nextItems { continue }
            for (index, item) in nextItems.enumerated() {
                try db.upsert(
                    "music_playback_lists",
                    columns: ["scope", "list_kind", "position", "item_value"],
                    values: [.text(scope), .text(kind), .integer(index), .text(item)],
                    key: ["scope", "list_kind", "position"]
                )
            }
            if previous != nil {
                try db.execute(
                    "DELETE FROM music_playback_lists WHERE scope=? AND list_kind=? AND position>=?",
                    [.text(scope), .text(kind), .integer(nextItems.count)]
                )
            }
        }

        if previous == nil || previous?.sourceExclusions != next.sourceExclusions {
            var prevByID: [String: (pos: Int, row: MusicSourceExclusionRecord)] = [:]
            for (index, row) in (previous?.sourceExclusions ?? []).enumerated() {
                prevByID[row.id] = (index, row)
            }
            let nextIDs = Set(next.sourceExclusions.map(\.id))
            for oldID in prevByID.keys where !nextIDs.contains(oldID) {
                try db.execute(
                    "DELETE FROM music_source_exclusions WHERE scope=? AND exclusion_id=?",
                    [.text(scope), .text(oldID)]
                )
            }
            for (index, rec) in next.sourceExclusions.enumerated() {
                if let old = prevByID[rec.id], old.pos == index, old.row == rec { continue }
                try db.upsert(
                    "music_source_exclusions",
                    columns: [
                        "scope", "exclusion_id", "position", "song_id",
                        "provider", "platform", "provider_name", "title", "artist", "excluded_at_ms"
                    ],
                    values: [
                        .text(scope), .text(rec.id), .integer(index), .text(rec.songID),
                        .text(rec.provider), .text(rec.platform), .text(rec.providerName),
                        .text(rec.title), .text(rec.artist), .integer(rec.excludedAtMs)
                    ],
                    key: ["scope", "exclusion_id"]
                )
            }
        }

        if previous == nil || previous?.recordingExclusions != next.recordingExclusions {
            var prevByID: [String: (pos: Int, row: MusicRecordingExclusionRecord)] = [:]
            for (index, row) in (previous?.recordingExclusions ?? []).enumerated() {
                prevByID[row.id] = (index, row)
            }
            let nextIDs = Set(next.recordingExclusions.map(\.id))
            for oldID in prevByID.keys where !nextIDs.contains(oldID) {
                try db.execute(
                    "DELETE FROM ios_music_recording_exclusions WHERE scope=? AND exclusion_id=?",
                    [.text(scope), .text(oldID)]
                )
            }
            for (index, rec) in next.recordingExclusions.enumerated() {
                if let old = prevByID[rec.id], old.pos == index, old.row == rec { continue }
                try db.upsert(
                    "ios_music_recording_exclusions",
                    columns: [
                        "scope", "exclusion_id", "position", "song_id",
                        "provider", "platform", "provider_name", "title", "artist", "source_key", "excluded_at_ms"
                    ],
                    values: [
                        .text(scope), .text(rec.id), .integer(index), .text(rec.songID),
                        .text(rec.provider), .text(rec.platform), .text(rec.providerName),
                        .text(rec.title), .text(rec.artist), .text(rec.sourceKey), .integer(rec.excludedAtMs)
                    ],
                    key: ["scope", "exclusion_id"]
                )
            }
        }

        if previous == nil || previous?.feedback != next.feedback {
            var prevFeedback: [String: (pos: Int, row: MusicFeedbackPlatformRecord)] = [:]
            for (index, row) in (previous?.feedback ?? []).enumerated() {
                prevFeedback[row.platform] = (index, row)
            }
            let nextPlatforms = Set(next.feedback.map(\.platform))
            for oldPlatform in prevFeedback.keys where !nextPlatforms.contains(oldPlatform) {
                try db.execute(
                    "DELETE FROM music_feedback_settings WHERE scope=? AND platform=?",
                    [.text(scope), .text(oldPlatform)]
                )
            }
            for (index, entry) in next.feedback.enumerated() {
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

        if previous == nil || previous?.fmSelections != next.fmSelections {
            var prevByScope: [String: MusicFMSelectionRecord] = [:]
            for item in previous?.fmSelections ?? [] { prevByScope[item.fmScope] = item }
            let nextScopes = Set(next.fmSelections.map(\.fmScope))
            for oldScope in prevByScope.keys where !nextScopes.contains(oldScope) {
                try db.execute(
                    "DELETE FROM ios_music_fm_selections WHERE scope=? AND fm_scope=?",
                    [.text(scope), .text(oldScope)]
                )
            }
            for item in next.fmSelections {
                if prevByScope[item.fmScope] == item { continue }
                try db.upsert(
                    "ios_music_fm_selections",
                    columns: ["scope", "fm_scope", "provider", "mode", "submode", "rule_id", "tab_tag_id"],
                    values: [
                        .text(scope),
                        .text(item.fmScope),
                        .text(item.provider),
                        .optional(item.mode),
                        .text(item.submode),
                        .optional(item.ruleID),
                        .optional(item.tabTagID)
                    ],
                    key: ["scope", "fm_scope"]
                )
            }
        }
    }
}
