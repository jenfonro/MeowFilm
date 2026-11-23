import Foundation
import MeowMusicFeature
import MeowStorage

enum MusicSourceDiscoveryStore {
    static let ttlMillis: Int64 = 30 * 86_400 * 1_000

    static func readAll(_ db: SQLiteConnection, nowMs: Int64) throws -> [String: [String: MusicSourceDiscoveryEntry]] {
        let cutoff = nowMs - ttlMillis
        let validSongIDs = Set(try db.query(
            "SELECT song_id FROM ios_music_source_discovery WHERE updated_at_ms>? AND updated_at_ms<=?",
            [.integer(cutoff), .integer(nowMs)]
        ) { try $0.text(0) })
        guard !validSongIDs.isEmpty else { return [:] }

        var platformsBySong: [String: [(platform: String, checkedAtMs: Int64)]] = [:]
        try db.scan("SELECT song_id,platform,checked_at_ms FROM ios_music_source_discovery_platforms") { c in
            let songID = try c.text(0)
            guard validSongIDs.contains(songID) else { return }
            platformsBySong[songID, default: []].append((try c.text(1), try c.int64(2)))
        }

        struct KeyPair: Hashable { let songID: String; let platform: String }
        var keysByPair: [KeyPair: [String]] = [:]
        try db.scan(
            "SELECT song_id,platform,source_key FROM ios_music_source_discovery_keys ORDER BY song_id,platform,position"
        ) { c in
            let songID = try c.text(0)
            guard validSongIDs.contains(songID) else { return }
            keysByPair[KeyPair(songID: songID, platform: try c.text(1)), default: []].append(try c.text(2))
        }

        var obsByPair: [KeyPair: [MusicSearchObservation]] = [:]
        try db.scan(
            "SELECT song_id,platform,request_id,started_at_ms,sort_order,checked_at_ms,obs_platform,query_text," +
            "context,page,limit_count,obs_scope,outcome,failure_code " +
            "FROM ios_music_source_discovery_observations ORDER BY song_id,platform,position"
        ) { c in
            let songID = try c.text(0)
            guard validSongIDs.contains(songID) else { return }
            let platform = try c.text(1)
            guard let requestID = UUID(uuidString: try c.text(2)),
                  let scope = MusicSearchObservation.Scope(rawValue: try c.text(11)),
                  let outcome = MusicSearchObservation.Outcome(rawValue: try c.text(12)) else {
                throw MusicServiceError.corruptStorage
            }
            let failure = (try c.optionalText(13)).flatMap(MusicServiceError.init(rawValue:))
            let obs = MusicSearchObservation(
                requestID: requestID,
                startedAt: Double(try c.int64(3)),
                order: UInt64(bitPattern: try c.int64(4)),
                checkedAt: Double(try c.int64(5)),
                platform: try c.text(6),
                query: try c.text(7),
                context: try c.text(8),
                page: try c.integer(9),
                limit: try c.integer(10),
                scope: scope,
                outcome: outcome,
                failure: failure
            )
            obsByPair[KeyPair(songID: songID, platform: platform), default: []].append(obs)
        }

        var result: [String: [String: MusicSourceDiscoveryEntry]] = [:]
        result.reserveCapacity(validSongIDs.count)
        for (songID, rows) in platformsBySong {
            var platforms: [String: MusicSourceDiscoveryEntry] = [:]
            platforms.reserveCapacity(rows.count)
            for row in rows {
                let pair = KeyPair(songID: songID, platform: row.platform)
                platforms[row.platform] = MusicSourceDiscoveryEntry(
                    checkedAt: Double(row.checkedAtMs),
                    sourceKeys: keysByPair[pair] ?? [],
                    observations: obsByPair[pair] ?? []
                )
            }
            result[songID] = platforms
        }
        return result
    }

    static func read(_ db: SQLiteConnection, songID: String, nowMs: Int64) throws -> [String: MusicSourceDiscoveryEntry]? {
        let cutoff = nowMs - ttlMillis
        guard try db.queryOne(
            "SELECT 1 FROM ios_music_source_discovery WHERE song_id=? AND updated_at_ms>? AND updated_at_ms<=?",
            [.text(songID), .integer(cutoff), .integer(nowMs)],
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
                throw MusicServiceError.corruptStorage
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
                        maximumBytes: Int,
                        nowMs: Int64) throws {
        let cutoff = nowMs - ttlMillis
        try db.execute(
            "DELETE FROM ios_music_source_discovery WHERE updated_at_ms<=? OR updated_at_ms>?",
            [.integer(cutoff), .integer(nowMs)]
        )
        let existingIDs = try db.query("SELECT song_id FROM ios_music_source_discovery") { try $0.text(0) }
        for id in existingIDs where !retained.contains(id) {
            try db.execute("DELETE FROM ios_music_source_discovery WHERE song_id=?", [.text(id)])
        }

        for record in records {
            try db.upsert(
                "ios_music_source_discovery",
                columns: ["song_id", "updated_at_ms", "stored_bytes"],
                values: [.text(record.songID), .integer(nowMs), .integer(max(0, record.storedBytes))],
                key: ["song_id"]
            )
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

        var bytes = try db.queryOne("SELECT COALESCE(SUM(stored_bytes), 0) FROM ios_music_source_discovery") { try $0.integer(0) } ?? 0
        if bytes > maximumBytes {
            let rows: [(songID: String, storedBytes: Int)] = try db.query(
                "SELECT song_id,stored_bytes FROM ios_music_source_discovery ORDER BY updated_at_ms ASC, song_id ASC"
            ) { c in
                (songID: try c.text(0), storedBytes: try c.integer(1))
            }
            for row in rows {
                if bytes <= maximumBytes { break }
                try db.execute("DELETE FROM ios_music_source_discovery WHERE song_id=?", [.text(row.songID)])
                bytes -= row.storedBytes
            }
        }
    }

    static func clear(_ db: SQLiteConnection) throws {
        try db.execute("DELETE FROM ios_music_source_discovery")
    }

    static func stats(_ db: SQLiteConnection) throws -> (songCount: Int, storedBytes: Int64) {
        try db.queryOne("SELECT count(*), coalesce(sum(stored_bytes), 0) FROM ios_music_source_discovery") {
            (try $0.integer(0), try $0.int64(1))
        } ?? (0, 0)
    }
}
