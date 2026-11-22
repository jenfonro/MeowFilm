import Foundation
import MeowStorage

/// Native editable video intent and read-only runtime probe facts.
struct FilmSourceRepository: Sendable {
    private static let ownerKey = "film.sources"
    let database: SQLiteDatabase

    func load() async throws -> (FilmSourceDocument, Int) {
        try await database.snapshot { db in
            let revision = Int(try db.ownerRevision(Self.ownerKey))

            var sourceActions: [FilmSourceKey: [String]] = [:]
            try db.scan("SELECT installation_id,site_key,action FROM film_source_site_actions ORDER BY installation_id,site_key,position") { c in
                let key = FilmSourceKey(installationId: try c.text(0), siteKey: try c.text(1))
                sourceActions[key, default: []].append(try c.text(2))
            }

            var sourceMethods: [FilmSourceKey: [String: String]] = [:]
            try db.scan("SELECT installation_id,site_key,action,method FROM film_source_site_methods") { c in
                let key = FilmSourceKey(installationId: try c.text(0), siteKey: try c.text(1))
                sourceMethods[key, default: [:]][try c.text(2)] = try c.text(3)
            }

            var sitesByCat: [String: [FilmSourceSite]] = [:]
            try db.scan("SELECT installation_id,site_key,name,api FROM film_source_sites ORDER BY installation_id,position") { c in
                let id = try c.text(0)
                let key = try c.text(1)
                let identity = FilmSourceKey(installationId: id, siteKey: key)
                sitesByCat[id, default: []].append(FilmSourceSite(
                    key: key,
                    name: try c.text(2),
                    api: try c.text(3),
                    actions: sourceActions[identity] ?? [],
                    methods: sourceMethods[identity] ?? [:]
                ))
            }

            let cats: [StoredFilmCat] = try db.query(
                "SELECT installation_id,input_revision,imported_at_ms,updated_at_ms,update_state,update_error,source_hash " +
                "FROM film_source_probes ORDER BY position,installation_id"
            ) { c in
                let id = try c.text(0)
                return StoredFilmCat(FilmCatSource(
                    installationId: id,
                    name: "",
                    sourceType: "network",
                    importedAt: try c.optionalInt64(2),
                    updatedAt: try c.optionalInt64(3),
                    updateState: try c.text(4),
                    updateError: try c.optionalText(5),
                    sourceHash: try c.optionalText(6),
                    sites: sitesByCat[id] ?? [],
                    inputRevision: try c.int64(1)
                ))
            }

            var videoActions: [FilmSourceKey: [String]] = [:]
            try db.scan("SELECT installation_id,site_key,action FROM film_video_actions ORDER BY installation_id,site_key,position") { c in
                let key = FilmSourceKey(installationId: try c.text(0), siteKey: try c.text(1))
                videoActions[key, default: []].append(try c.text(2))
            }

            var videoMethods: [FilmSourceKey: [String: String]] = [:]
            try db.scan("SELECT installation_id,site_key,action,method FROM film_video_methods") { c in
                let key = FilmSourceKey(installationId: try c.text(0), siteKey: try c.text(1))
                videoMethods[key, default: [:]][try c.text(2)] = try c.text(3)
            }

            var probeSteps: [FilmSourceKey: [FilmSourceProbeStep]] = [:]
            try db.scan("SELECT installation_id,site_key,operation,state,error FROM film_video_probe_steps ORDER BY installation_id,site_key,position") { c in
                let key = FilmSourceKey(installationId: try c.text(0), siteKey: try c.text(1))
                probeSteps[key, default: []].append(FilmSourceProbeStep(
                    try c.text(2), try c.text(3), try c.optionalText(4)
                ))
            }

            let videos: [FilmVideoSource] = try db.query(
                "SELECT installation_id,site_key,sort_order,name,api,enabled,home_visible,search_enabled,aggregate_images," +
                "detection_result,detection_checked_at_ms,detection_playback_resolved " +
                "FROM film_video_sources ORDER BY sort_order,position,installation_id,site_key"
            ) { c in
                let id = try c.text(0)
                let key = try c.text(1)
                let identity = FilmSourceKey(installationId: id, siteKey: key)
                let detection: FilmSourceDetection?
                if let result = try c.optionalText(9),
                   let checkedAt = try c.optionalInt64(10),
                   let playbackResolved = try c.optionalBool(11) {
                    detection = FilmSourceDetection(
                        result: result,
                        checkedAt: checkedAt,
                        playbackResolved: playbackResolved,
                        steps: probeSteps[identity] ?? []
                    )
                } else {
                    detection = nil
                }
                return FilmVideoSource(
                    key: identity,
                    name: try c.text(3),
                    api: try c.text(4),
                    actions: videoActions[identity] ?? [],
                    methods: videoMethods[identity] ?? [:],
                    options: .init(
                        enabled: try c.bool(5),
                        homeVisible: try c.bool(6),
                        searchEnabled: try c.bool(7),
                        aggregateImages: try c.bool(8)
                    ),
                    order: try c.integer(2),
                    detection: detection
                )
            }

            let deleted = try db.query("SELECT installation_id,site_key FROM film_source_deletions ORDER BY position,installation_id,site_key") {
                FilmSourceKey(installationId: try $0.text(0), siteKey: try $0.text(1))
            }
            let document = FilmSourceDocument(cats: cats, videos: videos, deleted: deleted)
            try Self.validate(document)
            return (document, revision)
        }
    }

    func save(_ value: FilmSourceDocument, expected: Int) async throws -> Int {
        try Self.validate(value)
        var ownedBytes = 0
        for cat in value.cats {
            for site in cat.sites { ownedBytes += site.api.utf8.count + site.name.utf8.count }
        }
        for video in value.videos {
            ownedBytes += video.api.utf8.count + video.name.utf8.count
            for action in video.actions { ownedBytes += action.utf8.count }
        }
        return try await database.transaction(ownedBytes: ownedBytes) { db in
            let current = try db.ownerRevision(Self.ownerKey)
            guard current == Int64(expected), current < 9_007_199_254_740_991 else {
                throw FilmSourceError(code: "STORAGE_CONFLICT")
            }

            // Probes cascade to sites, site actions, and site methods.
            try db.execute("DELETE FROM film_source_probes")
            for (catPos, cat) in value.cats.enumerated() {
                try db.execute(
                    "INSERT INTO film_source_probes(" +
                    "installation_id,position,name,source_type,source_url,enabled,input_revision," +
                    "imported_at_ms,updated_at_ms,update_state,update_error,source_hash" +
                    ") VALUES(?,?,'','network',NULL,1,?,?,?,?,?,?)",
                    [
                        .text(cat.installationId), .integer(catPos), .integer(cat.inputRevision),
                        cat.importedAt.map(SQLiteValue.integer) ?? .null,
                        cat.updatedAt.map(SQLiteValue.integer) ?? .null,
                        .text(cat.updateState), .optional(cat.updateError), .optional(cat.sourceHash)
                    ]
                )
                for (sitePos, site) in cat.sites.enumerated() {
                    try db.execute(
                        "INSERT INTO film_source_sites(installation_id,site_key,position,name,api) VALUES(?,?,?,?,?)",
                        [.text(cat.installationId), .text(site.key), .integer(sitePos), .text(site.name), .text(site.api)]
                    )
                    for (actionPos, action) in site.actions.enumerated() {
                        try db.execute(
                            "INSERT INTO film_source_site_actions(installation_id,site_key,position,action) VALUES(?,?,?,?)",
                            [.text(cat.installationId), .text(site.key), .integer(actionPos), .text(action)]
                        )
                    }
                    for (action, method) in site.methods {
                        try db.execute(
                            "INSERT INTO film_source_site_methods(installation_id,site_key,action,method) VALUES(?,?,?,?)",
                            [.text(cat.installationId), .text(site.key), .text(action), .text(method)]
                        )
                    }
                }
            }

            // Video sources cascade to video actions, methods, and probe steps.
            try db.execute("DELETE FROM film_video_sources")
            for (videoPos, video) in value.videos.enumerated() {
                let probe = video.detection
                try db.execute(
                    "INSERT INTO film_video_sources(" +
                    "installation_id,site_key,position,sort_order,name,api,enabled,home_visible,search_enabled,aggregate_images," +
                    "detection_result,detection_checked_at_ms,detection_playback_resolved" +
                    ") VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?)",
                    [
                        .text(video.key.installationId), .text(video.key.siteKey),
                        .integer(videoPos), .integer(video.order),
                        .text(video.name), .text(video.api),
                        .bool(video.options.enabled), .bool(video.options.homeVisible),
                        .bool(video.options.searchEnabled), .bool(video.options.aggregateImages),
                        .optional(probe?.result),
                        probe.map { .integer($0.checkedAt) } ?? .null,
                        probe.map { .bool($0.playbackResolved) } ?? .null
                    ]
                )
                for (actionPos, action) in video.actions.enumerated() {
                    try db.execute(
                        "INSERT INTO film_video_actions(installation_id,site_key,position,action) VALUES(?,?,?,?)",
                        [.text(video.key.installationId), .text(video.key.siteKey), .integer(actionPos), .text(action)]
                    )
                }
                for (action, method) in video.methods {
                    try db.execute(
                        "INSERT INTO film_video_methods(installation_id,site_key,action,method) VALUES(?,?,?,?)",
                        [.text(video.key.installationId), .text(video.key.siteKey), .text(action), .text(method)]
                    )
                }
                if let probe {
                    for (stepPos, step) in probe.steps.enumerated() {
                        try db.execute(
                            "INSERT INTO film_video_probe_steps(installation_id,site_key,position,operation,state,error) VALUES(?,?,?,?,?,?)",
                            [
                                .text(video.key.installationId), .text(video.key.siteKey), .integer(stepPos),
                                .text(step.operation), .text(step.state), .optional(step.error)
                            ]
                        )
                    }
                }
            }

            try db.execute("DELETE FROM film_source_deletions")
            for (pos, key) in value.deleted.enumerated() {
                try db.execute(
                    "INSERT INTO film_source_deletions(installation_id,site_key,position) VALUES(?,?,?)",
                    [.text(key.installationId), .text(key.siteKey), .integer(pos)]
                )
            }

            let next = try db.commitOwnerRevision(Self.ownerKey, expectedRevision: current)
            return Int(next)
        }
    }

    private static func validate(_ value: FilmSourceDocument) throws {
        func valid(_ key: FilmSourceKey) -> Bool { !key.installationId.isEmpty && !key.siteKey.isEmpty }
        guard Set(value.cats.map(\.installationId)).count == value.cats.count,
              value.cats.allSatisfy({ cat in
                  !cat.installationId.isEmpty && cat.inputRevision > 0 && cat.inputRevision <= 9_007_199_254_740_991 &&
                  cat.sites.allSatisfy({ !$0.key.isEmpty }) && Set(cat.sites.map(\.key)).count == cat.sites.count
              }),
              value.videos.allSatisfy({ valid($0.key) }),
              Set(value.videos.map(\.key)).count == value.videos.count,
              value.videos.filter({ $0.options.aggregateImages }).count <= 1,
              value.deleted.allSatisfy(valid), Set(value.deleted).count == value.deleted.count else {
            throw FilmSourceError(code: "INVALID_DOCUMENT")
        }
    }
}
