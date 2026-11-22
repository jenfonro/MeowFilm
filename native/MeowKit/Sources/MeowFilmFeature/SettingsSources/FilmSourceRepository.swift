import Foundation
import MeowStorage

/// Native editable video intent and read-only runtime probe facts.
struct FilmSourceRepository: Sendable {
    private static let ownerKey = "film.sources"
    let database: SQLiteDatabase

    func load() async throws -> (FilmSourceDocument, Int) {
        try await database.snapshot { db in
            let revision = Int(try db.ownerRevision(Self.ownerKey).revision)
            func grouped(_ table: String, ordered: Bool = false) throws -> [FilmSourceKey: [SQLiteRow]] {
                try Dictionary(grouping: db.query("SELECT * FROM \(table)" + (ordered ? " ORDER BY position" : ""))) {
                    FilmSourceKey(installationId: try $0.text("installation_id"), siteKey: try $0.text("site_key"))
                }
            }
            func actions(_ rows: [SQLiteRow]) throws -> [String] { try rows.map { try $0.text("action") } }
            func methods(_ rows: [SQLiteRow]) throws -> [String: String] {
                Dictionary(uniqueKeysWithValues: try rows.map { (try $0.text("action"), try $0.text("method")) })
            }
            let siteRows = try Dictionary(grouping: db.query("SELECT * FROM ios_film_source_sites ORDER BY position")) {
                Data(try $0.text("installation_id").utf8)
            }
            let sourceActions = try grouped("ios_film_source_actions", ordered: true)
            let sourceMethods = try grouped("ios_film_source_methods")
            let videoActions = try grouped("ios_film_video_actions", ordered: true)
            let videoMethods = try grouped("ios_film_video_methods")
            let detections = try grouped("ios_film_video_detections")
            let probeSteps = try grouped("ios_film_video_probe_steps", ordered: true)
            let cats = try db.query("SELECT * FROM ios_film_source_probes ORDER BY installation_id").map { row in
                let id = try row.text("installation_id")
                let sites = try (siteRows[Data(id.utf8)] ?? []).map { site in
                    let key = try site.text("site_key")
                    let identity = FilmSourceKey(installationId: id, siteKey: key)
                    return FilmSourceSite(key: key, name: try site.text("name"), api: try site.text("api"),
                        actions: try actions(sourceActions[identity] ?? []), methods: try methods(sourceMethods[identity] ?? []))
                }
                return StoredFilmCat(FilmCatSource(installationId: id, name: "", sourceType: "network",
                    importedAt: try row.optionalInteger("imported_at").map(Int64.init),
                    updatedAt: try row.optionalInteger("updated_at").map(Int64.init),
                    updateState: try row.text("update_state"), updateError: try row.optionalText("update_error"),
                    sourceHash: try row.optionalText("source_hash"), sites: sites,
                    inputRevision: Int64(try row.integer("input_revision"))))
            }
            let videos = try db.query("SELECT * FROM ios_film_video_intents ORDER BY position, installation_id, site_key").map { row in
                let id = try row.text("installation_id"), key = try row.text("site_key")
                let identity = FilmSourceKey(installationId: id, siteKey: key)
                var detection: FilmSourceDetection?
                if let probe = detections[identity]?.first {
                    let steps = try (probeSteps[identity] ?? []).map {
                        FilmSourceProbeStep(try $0.text("operation"), try $0.text("state"), try $0.optionalText("error"))
                    }
                    detection = FilmSourceDetection(result: try probe.text("result"),
                        checkedAt: Int64(try probe.integer("checked_at")), playbackResolved: try probe.bool("playback_resolved"), steps: steps)
                }
                return FilmVideoSource(key: .init(installationId: id, siteKey: key),
                    name: try row.text("name"), api: try row.text("api"),
                    actions: try actions(videoActions[identity] ?? []), methods: try methods(videoMethods[identity] ?? []),
                    options: .init(enabled: try row.bool("enabled"), homeVisible: try row.bool("home_visible"),
                                   searchEnabled: try row.bool("search_enabled"), aggregateImages: try row.bool("aggregate_images")),
                    order: try row.integer("position"), detection: detection)
            }
            let deleted = try db.query("SELECT installation_id, site_key FROM film_source_deletions ORDER BY position, installation_id, site_key") {
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
            guard current.revision == Int64(expected), current.revision < 9_007_199_254_740_991 else {
                throw FilmSourceError(code: "STORAGE_CONFLICT")
            }
            try db.reconcile("ios_film_source_probes",
                columns: ["installation_id", "input_revision", "imported_at", "updated_at", "update_state", "update_error", "source_hash"],
                key: ["installation_id"], rows: value.cats.map {
                    [.text($0.installationId), .integer($0.inputRevision), $0.importedAt.map(SQLiteValue.integer) ?? .null,
                     $0.updatedAt.map(SQLiteValue.integer) ?? .null, .text($0.updateState), .optional($0.updateError), .optional($0.sourceHash)]
                })
            var sites: [[SQLiteValue]] = [], siteActions: [[SQLiteValue]] = [], siteMethods: [[SQLiteValue]] = []
            for cat in value.cats {
                for (position, site) in cat.sites.enumerated() {
                    let identity: [SQLiteValue] = [.text(cat.installationId), .text(site.key)]
                    sites.append(identity + [.integer(position), .text(site.name), .text(site.api)])
                    siteActions += site.actions.enumerated().map { identity + [.integer($0.offset), .text($0.element)] }
                    siteMethods += site.methods.map { identity + [.text($0.key), .text($0.value)] }
                }
            }
            try db.reconcile("ios_film_source_sites", columns: ["installation_id", "site_key", "position", "name", "api"],
                             key: ["installation_id", "site_key"], rows: sites)
            try saveChildren(db, prefix: "ios_film_source", actions: siteActions, methods: siteMethods)
            if let selected = value.videos.first(where: { $0.options.aggregateImages }) {
                try db.execute("UPDATE ios_film_video_intents SET aggregate_images=0 WHERE aggregate_images=1 AND NOT(installation_id=? AND site_key=?)",
                               [.text(selected.key.installationId), .text(selected.key.siteKey)])
            } else { try db.execute("UPDATE ios_film_video_intents SET aggregate_images=0 WHERE aggregate_images=1") }
            let videoRows = value.videos.map {
                [SQLiteValue.text($0.key.installationId), .text($0.key.siteKey), .integer($0.order), .text($0.name), .text($0.api),
                 .bool($0.options.enabled), .bool($0.options.homeVisible), .bool($0.options.searchEnabled), .bool($0.options.aggregateImages)]
            }
            try db.reconcile("ios_film_video_intents",
                columns: ["installation_id", "site_key", "position", "name", "api", "enabled", "home_visible", "search_enabled", "aggregate_images"],
                key: ["installation_id", "site_key"], rows: videoRows)
            var actions: [[SQLiteValue]] = [], methods: [[SQLiteValue]] = [], detections: [[SQLiteValue]] = [], steps: [[SQLiteValue]] = []
            for video in value.videos {
                let identity: [SQLiteValue] = [.text(video.key.installationId), .text(video.key.siteKey)]
                actions += video.actions.enumerated().map { identity + [.integer($0.offset), .text($0.element)] }
                methods += video.methods.map { identity + [.text($0.key), .text($0.value)] }
                if let probe = video.detection {
                    detections.append(identity + [.text(probe.result), .integer(probe.checkedAt), .bool(probe.playbackResolved)])
                    steps += probe.steps.enumerated().map {
                        identity + [.integer($0.offset), .text($0.element.operation), .text($0.element.state), .optional($0.element.error)]
                    }
                }
            }
            try saveChildren(db, prefix: "ios_film_video", actions: actions, methods: methods)
            try db.reconcile("ios_film_video_detections",
                             columns: ["installation_id", "site_key", "result", "checked_at", "playback_resolved"],
                             key: ["installation_id", "site_key"], rows: detections)
            try db.reconcile("ios_film_video_probe_steps",
                             columns: ["installation_id", "site_key", "position", "operation", "state", "error"],
                             key: ["installation_id", "site_key", "position"], rows: steps)
            try db.reconcile("film_source_deletions", columns: ["installation_id", "site_key", "position"],
                             key: ["installation_id", "site_key"],
                             rows: value.deleted.enumerated().map { [.text($0.element.installationId), .text($0.element.siteKey), .integer($0.offset)] })
            let next = try db.commitOwnerRevision(
                Self.ownerKey,
                expectedRevision: current.revision,
                expectedReceipt: current.receipt,
                operationID: UUID().uuidString.lowercased()
            )
            return Int(next.revision)
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

    private func saveChildren(_ db: SQLiteConnection, prefix: String, actions: [[SQLiteValue]], methods: [[SQLiteValue]]) throws {
        try db.reconcile("\(prefix)_actions", columns: ["installation_id", "site_key", "position", "action"],
                         key: ["installation_id", "site_key", "position"], rows: actions)
        try db.reconcile("\(prefix)_methods", columns: ["installation_id", "site_key", "action", "method"],
                         key: ["installation_id", "site_key", "action"], rows: methods)
    }
}
