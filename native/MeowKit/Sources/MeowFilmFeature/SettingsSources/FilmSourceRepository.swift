import Foundation
import MeowStorage

/// Native editable video intent and read-only runtime probe facts.
struct FilmSourceRepository: Sendable {
    private static let ownerKey = "film.sources"
    let database: SQLiteDatabase

    func load() async throws -> (FilmSourceState, Int) {
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
            let state = FilmSourceState(cats: cats, videos: videos, deleted: deleted)
            return (state, revision)
        }
    }

    func save(_ value: FilmSourceState, previous: FilmSourceState, expected: Int) async throws -> Int {
        try await database.transaction { db in
            let current = try db.ownerRevision(Self.ownerKey)
            guard current == Int64(expected) else {
                throw FilmSourceError(code: "STORAGE_CONFLICT")
            }

            // Incremental diff on film_source_probes (cascades to sites, site actions, site methods).
            let prevCats = Dictionary(uniqueKeysWithValues: previous.cats.enumerated().map { ($0.element.installationId, ($0.offset, $0.element)) })
            let nextCatIDs = Set(value.cats.map(\.installationId))
            for removedID in prevCats.keys where !nextCatIDs.contains(removedID) {
                try db.execute("DELETE FROM film_source_probes WHERE installation_id=?", [.text(removedID)])
            }
            for (catPos, cat) in value.cats.enumerated() {
                let old = prevCats[cat.installationId]
                if let old, old.0 == catPos, old.1 == cat { continue }
                try db.upsert(
                    "film_source_probes",
                    columns: [
                        "installation_id", "position", "input_revision",
                        "imported_at_ms", "updated_at_ms", "update_state", "update_error", "source_hash"
                    ],
                    values: [
                        .text(cat.installationId), .integer(catPos), .integer(cat.inputRevision),
                        cat.importedAt.map(SQLiteValue.integer) ?? .null,
                        cat.updatedAt.map(SQLiteValue.integer) ?? .null,
                        .text(cat.updateState), .optional(cat.updateError), .optional(cat.sourceHash)
                    ],
                    key: ["installation_id"]
                )
                if old == nil || old!.1.sites != cat.sites {
                    try db.execute("DELETE FROM film_source_sites WHERE installation_id=?", [.text(cat.installationId)])
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
            }

            // Incremental diff on film_video_sources (cascades to actions, methods, probe steps).
            let prevVideos = Dictionary(uniqueKeysWithValues: previous.videos.enumerated().map { ($0.element.key, ($0.offset, $0.element)) })
            let nextVideoKeys = Set(value.videos.map(\.key))
            for removedKey in prevVideos.keys where !nextVideoKeys.contains(removedKey) {
                try db.execute(
                    "DELETE FROM film_video_sources WHERE installation_id=? AND site_key=?",
                    [.text(removedKey.installationId), .text(removedKey.siteKey)]
                )
            }
            // Clear aggregate_images before upserts if the unique aggregate-images owner is moving between rows.
            let prevAgg = previous.videos.first(where: { $0.options.aggregateImages })?.key
            let nextAgg = value.videos.first(where: { $0.options.aggregateImages })?.key
            if let oldAgg = prevAgg, oldAgg != nextAgg, nextVideoKeys.contains(oldAgg) {
                try db.execute(
                    "UPDATE film_video_sources SET aggregate_images=0 WHERE installation_id=? AND site_key=?",
                    [.text(oldAgg.installationId), .text(oldAgg.siteKey)]
                )
            }
            for (videoPos, video) in value.videos.enumerated() {
                let old = prevVideos[video.key]
                if let old, old.0 == videoPos, old.1 == video { continue }
                let probe = video.detection
                try db.upsert(
                    "film_video_sources",
                    columns: [
                        "installation_id", "site_key", "position", "sort_order", "name", "api",
                        "enabled", "home_visible", "search_enabled", "aggregate_images",
                        "detection_result", "detection_checked_at_ms", "detection_playback_resolved"
                    ],
                    values: [
                        .text(video.key.installationId), .text(video.key.siteKey),
                        .integer(videoPos), .integer(video.order),
                        .text(video.name), .text(video.api),
                        .bool(video.options.enabled), .bool(video.options.homeVisible),
                        .bool(video.options.searchEnabled), .bool(video.options.aggregateImages),
                        .optional(probe?.result),
                        probe.map { .integer($0.checkedAt) } ?? .null,
                        probe.map { .bool($0.playbackResolved) } ?? .null
                    ],
                    key: ["installation_id", "site_key"]
                )
                if old == nil || old!.1.actions != video.actions {
                    try db.execute(
                        "DELETE FROM film_video_actions WHERE installation_id=? AND site_key=?",
                        [.text(video.key.installationId), .text(video.key.siteKey)]
                    )
                    for (actionPos, action) in video.actions.enumerated() {
                        try db.execute(
                            "INSERT INTO film_video_actions(installation_id,site_key,position,action) VALUES(?,?,?,?)",
                            [.text(video.key.installationId), .text(video.key.siteKey), .integer(actionPos), .text(action)]
                        )
                    }
                }
                if old == nil || old!.1.methods != video.methods {
                    try db.execute(
                        "DELETE FROM film_video_methods WHERE installation_id=? AND site_key=?",
                        [.text(video.key.installationId), .text(video.key.siteKey)]
                    )
                    for (action, method) in video.methods {
                        try db.execute(
                            "INSERT INTO film_video_methods(installation_id,site_key,action,method) VALUES(?,?,?,?)",
                            [.text(video.key.installationId), .text(video.key.siteKey), .text(action), .text(method)]
                        )
                    }
                }
                if old == nil || old!.1.detection?.steps != probe?.steps {
                    try db.execute(
                        "DELETE FROM film_video_probe_steps WHERE installation_id=? AND site_key=?",
                        [.text(video.key.installationId), .text(video.key.siteKey)]
                    )
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
            }

            // Incremental diff on film_source_deletions.
            let prevDeleted = Dictionary(uniqueKeysWithValues: previous.deleted.enumerated().map { ($0.element, $0.offset) })
            let nextDeletedSet = Set(value.deleted)
            for removedKey in prevDeleted.keys where !nextDeletedSet.contains(removedKey) {
                try db.execute(
                    "DELETE FROM film_source_deletions WHERE installation_id=? AND site_key=?",
                    [.text(removedKey.installationId), .text(removedKey.siteKey)]
                )
            }
            for (pos, key) in value.deleted.enumerated() {
                if prevDeleted[key] == pos { continue }
                try db.upsert(
                    "film_source_deletions",
                    columns: ["installation_id", "site_key", "position"],
                    values: [.text(key.installationId), .text(key.siteKey), .integer(pos)],
                    key: ["installation_id", "site_key"]
                )
            }

            let next = try db.commitOwnerRevision(Self.ownerKey, expectedRevision: current)
            return Int(next)
        }
    }
}
