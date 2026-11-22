import Foundation
import MeowMusicFeature
import MeowStorage

enum MusicSourceDiscoveryStore {
    static func allSongIDs(_ db: SQLiteConnection) throws -> [String] {
        try db.query("SELECT song_id FROM ios_music_source_discovery") { try $0.text(0) }
    }

    static func read(_ db: SQLiteConnection, songID: String) throws -> [String: MusicSourceDiscoveryEntry]? {
        guard try db.queryOne(
            "SELECT 1 FROM ios_music_source_discovery WHERE song_id=?",
            [.text(songID)],
            map: { _ in true }
        ) != nil else { return nil }

        let platformRows: [(platform: String, checkedAtMs: Int64)] = try db.query(
            "SELECT platform,checked_at_ms FROM ios_music_source_discovery_platforms WHERE song_id=?",
            [.text(songID)]
        ) { c in
            (platform: try c.text(0), checkedAtMs: try c.int64(1))
        }

        var keysByPlatform: [String: [String]] = [:]
        try db.scan(
            "SELECT platform,source_key FROM ios_music_source_discovery_keys WHERE song_id=? ORDER BY platform,position",
            [.text(songID)]
        ) { c in
            keysByPlatform[try c.text(0), default: []].append(try c.text(1))
        }

        var obsByPlatform: [String: [MusicSearchObservation]] = [:]
        try db.scan(
            "SELECT platform,request_id,started_at_ms,sort_order,checked_at_ms,obs_platform,query_text," +
            "context,page,limit_count,obs_scope,outcome,failure_code " +
            "FROM ios_music_source_discovery_observations WHERE song_id=? ORDER BY platform,position",
            [.text(songID)]
        ) { c in
            let platform = try c.text(0)
            guard let requestID = UUID(uuidString: try c.text(1)),
                  let scope = MusicSearchObservation.Scope(rawValue: try c.text(10)),
                  let outcome = MusicSearchObservation.Outcome(rawValue: try c.text(11)) else {
                throw MusicServiceError.corruptDocument
            }
            let failure = (try c.optionalText(12)).flatMap(MusicServiceError.init(rawValue:))
            let obs = MusicSearchObservation(
                requestID: requestID,
                startedAt: Double(try c.int64(2)),
                order: UInt64(bitPattern: try c.int64(3)),
                checkedAt: Double(try c.int64(4)),
                platform: try c.text(5),
                query: try c.text(6),
                context: try c.text(7),
                page: try c.integer(8),
                limit: try c.integer(9),
                scope: scope,
                outcome: outcome,
                failure: failure
            )
            obsByPlatform[platform, default: []].append(obs)
        }

        var result: [String: MusicSourceDiscoveryEntry] = [:]
        result.reserveCapacity(platformRows.count)
        for row in platformRows {
            result[row.platform] = MusicSourceDiscoveryEntry(
                checkedAt: Double(row.checkedAtMs),
                sourceKeys: keysByPlatform[row.platform] ?? [],
                observations: obsByPlatform[row.platform] ?? []
            )
        }
        return result
    }

    static func persist(_ db: SQLiteConnection,
                        retained: Set<String>,
                        records: [(songID: String, storedBytes: Int, platforms: [String: MusicSourceDiscoveryEntry])],
                        maximumBytes: Int) throws {
        let existingIDs = try db.query("SELECT song_id FROM ios_music_source_discovery") { try $0.text(0) }
        for id in existingIDs where !retained.contains(id) {
            try db.execute("DELETE FROM ios_music_source_discovery WHERE song_id=?", [.text(id)])
        }

        for record in records {
            try db.upsert(
                "ios_music_source_discovery",
                columns: ["song_id", "stored_bytes"],
                values: [.text(record.songID), .integer(max(0, record.storedBytes))],
                key: ["song_id"]
            )
            // Cascades to ios_music_source_discovery_keys and _observations.
            try db.execute("DELETE FROM ios_music_source_discovery_platforms WHERE song_id=?", [.text(record.songID)])
            for (platform, entry) in record.platforms.sorted(by: { $0.key < $1.key }) {
                let checkedAtMs = Int64(entry.checkedAt.isFinite ? entry.checkedAt.rounded() : 0)
                try db.execute(
                    "INSERT INTO ios_music_source_discovery_platforms(song_id,platform,checked_at_ms) VALUES(?,?,?)",
                    [.text(record.songID), .text(platform), .integer(checkedAtMs)]
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
                    let obsStartedMs = Int64(obs.startedAt.isFinite ? obs.startedAt.rounded() : 0)
                    let obsCheckedMs = Int64(obs.checkedAt.isFinite ? obs.checkedAt.rounded() : 0)
                    try db.execute(
                        "INSERT INTO ios_music_source_discovery_observations(" +
                        "song_id,platform,position,request_id,started_at_ms,sort_order,checked_at_ms," +
                        "obs_platform,query_text,context,page,limit_count,obs_scope,outcome,failure_code" +
                        ") VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)",
                        [
                            .text(record.songID),
                            .text(platform),
                            .integer(obsIdx),
                            .text(obs.requestID.uuidString),
                            .integer(obsStartedMs),
                            .integer(Int64(bitPattern: obs.order)),
                            .integer(obsCheckedMs),
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

        let rows: [(songID: String, storedBytes: Int)] = try db.query(
            "SELECT song_id,stored_bytes FROM ios_music_source_discovery ORDER BY " +
            "(SELECT max(checked_at_ms) FROM ios_music_source_discovery_platforms " +
            "WHERE ios_music_source_discovery_platforms.song_id=ios_music_source_discovery.song_id)"
        ) { c in
            (songID: try c.text(0), storedBytes: try c.integer(1))
        }
        var bytes = rows.reduce(0) { $0 + $1.storedBytes }
        for row in rows where bytes > maximumBytes {
            try db.execute("DELETE FROM ios_music_source_discovery WHERE song_id=?", [.text(row.songID)])
            bytes -= row.storedBytes
        }
    }
}
