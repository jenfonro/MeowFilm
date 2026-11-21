import Foundation
import MeowMusicFeature
import MeowStorage

enum MusicSourceDiscoveryStore {
    static func allSongIDs(_ db: SQLiteConnection) throws -> [String] {
        try db.query("SELECT song_id FROM ios_music_source_discovery").map { try $0.text("song_id") }
    }

    static func read(_ db: SQLiteConnection, songID: String) throws -> [String: MusicSourceDiscoveryEntry]? {
        guard try !db.query(
            "SELECT song_id FROM ios_music_source_discovery WHERE song_id=?",
            [.text(songID)]
        ).isEmpty else { return nil }

        let platformRows = try db.query(
            "SELECT platform,checked_at FROM ios_music_source_discovery_platforms WHERE song_id=?",
            [.text(songID)]
        )

        var keysByPlatform: [String: [String]] = [:]
        for row in try db.query(
            "SELECT platform,source_key FROM ios_music_source_discovery_keys WHERE song_id=? ORDER BY platform,position",
            [.text(songID)]
        ) {
            keysByPlatform[try row.text("platform"), default: []].append(try row.text("source_key"))
        }

        var obsByPlatform: [String: [MusicSearchObservation]] = [:]
        for row in try db.query(
            "SELECT platform,request_id,started_at,sort_order,checked_at,obs_platform,query_text," +
            "context,page,limit_count,obs_scope,outcome,failure_code " +
            "FROM ios_music_source_discovery_observations WHERE song_id=? ORDER BY platform,position",
            [.text(songID)]
        ) {
            let platform = try row.text("platform")
            guard let scope = MusicSearchObservation.Scope(rawValue: try row.text("obs_scope")),
                  let outcome = MusicSearchObservation.Outcome(rawValue: try row.text("outcome")) else {
                throw MusicServiceError.corruptDocument
            }
            let failure = (try row.optionalText("failure_code")).flatMap(MusicServiceError.init(rawValue:))
            let obs = MusicSearchObservation(
                requestID: try row.uuid("request_id"),
                startedAt: try row.double("started_at"),
                order: UInt64(bitPattern: try row.int64("sort_order")),
                checkedAt: try row.double("checked_at"),
                platform: try row.text("obs_platform"),
                query: try row.text("query_text"),
                context: try row.text("context"),
                page: try row.integer("page"),
                limit: try row.integer("limit_count"),
                scope: scope,
                outcome: outcome,
                failure: failure
            )
            obsByPlatform[platform, default: []].append(obs)
        }

        var result: [String: MusicSourceDiscoveryEntry] = [:]
        result.reserveCapacity(platformRows.count)
        for row in platformRows {
            let platform = try row.text("platform")
            result[platform] = MusicSourceDiscoveryEntry(
                checkedAt: try row.double("checked_at"),
                sourceKeys: keysByPlatform[platform] ?? [],
                observations: obsByPlatform[platform] ?? []
            )
        }
        return result
    }

    static func persist(_ db: SQLiteConnection,
                        retained: Set<String>,
                        records: [(songID: String, storedBytes: Int, platforms: [String: MusicSourceDiscoveryEntry])],
                        maximumBytes: Int) throws {
        for row in try db.query("SELECT song_id FROM ios_music_source_discovery") {
            let id = try row.text("song_id")
            if !retained.contains(id) {
                try db.execute("DELETE FROM ios_music_source_discovery WHERE song_id=?", [.text(id)])
            }
        }

        for record in records {
            guard try !db.query("SELECT song_id FROM music_songs WHERE song_id=?", [.text(record.songID)]).isEmpty else {
                continue
            }
            try db.upsert(
                "ios_music_source_discovery",
                columns: ["song_id", "stored_bytes"],
                values: [.text(record.songID), .integer(max(0, record.storedBytes))],
                key: ["song_id"]
            )
            // Cascades to ios_music_source_discovery_keys and _observations.
            try db.execute("DELETE FROM ios_music_source_discovery_platforms WHERE song_id=?", [.text(record.songID)])
            for (platform, entry) in record.platforms.sorted(by: { $0.key < $1.key }) {
                try db.execute(
                    "INSERT INTO ios_music_source_discovery_platforms(song_id,platform,checked_at) VALUES(?,?,?)",
                    [.text(record.songID), .text(platform), .real(entry.checkedAt)]
                )
                var seenKeys = Set<String>()
                var keyPos = 0
                for key in entry.sourceKeys where seenKeys.insert(key).inserted {
                    try db.execute(
                        "INSERT INTO ios_music_source_discovery_keys(song_id,platform,position,source_key) VALUES(?,?,?,?)",
                        [.text(record.songID), .text(platform), .integer(keyPos), .text(key)]
                    )
                    keyPos += 1
                }
                for (obsIdx, obs) in entry.observations.enumerated() {
                    try db.execute(
                        "INSERT INTO ios_music_source_discovery_observations(" +
                        "song_id,platform,position,request_id,started_at,sort_order,checked_at," +
                        "obs_platform,query_text,context,page,limit_count,obs_scope,outcome,failure_code" +
                        ") VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)",
                        [
                            .text(record.songID),
                            .text(platform),
                            .integer(obsIdx),
                            .text(obs.requestID.uuidString),
                            .real(obs.startedAt),
                            .integer(Int64(bitPattern: obs.order)),
                            .real(obs.checkedAt),
                            .text(obs.platform),
                            .text(obs.query),
                            .text(obs.context),
                            .integer(obs.page),
                            .integer(obs.limit),
                            .text(obs.scope.rawValue),
                            .text(obs.outcome.rawValue),
                            .optional(obs.failureCode)
                        ]
                    )
                }
            }
        }

        let rows = try db.query(
            "SELECT song_id,stored_bytes FROM ios_music_source_discovery ORDER BY " +
            "(SELECT max(checked_at) FROM ios_music_source_discovery_platforms " +
            "WHERE ios_music_source_discovery_platforms.song_id=ios_music_source_discovery.song_id)"
        )
        var bytes = try rows.reduce(0) { try $0 + $1.integer("stored_bytes") }
        for row in rows where bytes > maximumBytes {
            try db.execute("DELETE FROM ios_music_source_discovery WHERE song_id=?", [row["song_id"]])
            bytes -= try row.integer("stored_bytes")
        }
    }
}
