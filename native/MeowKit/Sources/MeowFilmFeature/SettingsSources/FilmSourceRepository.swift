import Foundation
import MeowStorage

/// Native editable video intent and read-only runtime probe facts.
struct FilmSourceRepository: Sendable {
    let database: SQLiteDatabase

    func load() async throws -> (FilmSourceDocument, Int) {
        try await database.snapshot { db in
            let revision = try db.query("SELECT revision FROM ios_film_source_state WHERE singleton=1").first!.integer("revision")
            func grouped(_ table: String, ordered: Bool = false) throws -> [FilmSourceKey: [SQLiteRow]] {
                try Dictionary(grouping: db.query("SELECT * FROM \(table)" + (ordered ? " ORDER BY ordinal" : ""))) {
                    FilmSourceKey(installationId: try $0.text("installationId"), siteKey: try $0.text("siteKey"))
                }
            }
            func actions(_ rows: [SQLiteRow]) throws -> [String] { try rows.map { try $0.text("action") } }
            func methods(_ rows: [SQLiteRow]) throws -> [String: String] {
                Dictionary(uniqueKeysWithValues: try rows.map { (try $0.text("action"), try $0.text("method")) })
            }
            let siteRows = try Dictionary(grouping: db.query("SELECT * FROM ios_film_source_sites ORDER BY ordinal")) {
                Data(try $0.text("installationId").utf8)
            }
            let sourceActions = try grouped("ios_film_source_actions", ordered: true)
            let sourceMethods = try grouped("ios_film_source_methods")
            let videoActions = try grouped("ios_film_video_actions", ordered: true)
            let videoMethods = try grouped("ios_film_video_methods")
            let detections = try grouped("ios_film_video_detections")
            let probeSteps = try grouped("ios_film_video_probe_steps", ordered: true)
            let cats = try db.query("SELECT * FROM ios_film_source_probes ORDER BY installationId").map { row in
                let id = try row.text("installationId")
                let sites = try (siteRows[Data(id.utf8)] ?? []).map { site in
                    let key = try site.text("siteKey")
                    let identity = FilmSourceKey(installationId: id, siteKey: key)
                    return FilmSourceSite(key: key, name: try site.text("name"), api: try site.text("api"),
                        actions: try actions(sourceActions[identity] ?? []), methods: try methods(sourceMethods[identity] ?? []))
                }
                return StoredFilmCat(FilmCatSource(installationId: id, name: "", sourceType: "network",
                    importedAt: try row.optionalInteger("importedAt").map(Int64.init),
                    updatedAt: try row.optionalInteger("updatedAt").map(Int64.init),
                    updateState: try row.text("updateState"), updateError: try row.optionalText("updateError"),
                    sourceHash: try row.optionalText("sourceHash"), sites: sites,
                    inputRevision: Int64(try row.integer("inputRevision"))))
            }
            let videos = try db.query("SELECT * FROM ios_film_video_intents ORDER BY ordinal,installationId,siteKey").map { row in
                let id = try row.text("installationId"), key = try row.text("siteKey")
                let identity = FilmSourceKey(installationId: id, siteKey: key)
                var detection: FilmSourceDetection?
                if let probe = detections[identity]?.first {
                    let steps = try (probeSteps[identity] ?? []).map {
                        FilmSourceProbeStep(try $0.text("operation"), try $0.text("state"), try $0.optionalText("error"))
                    }
                    detection = FilmSourceDetection(result: try probe.text("result"),
                        checkedAt: Int64(try probe.integer("checkedAt")), playbackResolved: try probe.bool("playbackResolved"), steps: steps)
                }
                return FilmVideoSource(key: .init(installationId: id, siteKey: key),
                    name: try row.text("name"), api: try row.text("api"),
                    actions: try actions(videoActions[identity] ?? []), methods: try methods(videoMethods[identity] ?? []),
                    options: .init(enabled: try row.bool("enabled"), homeVisible: try row.bool("homeVisible"),
                                   searchEnabled: try row.bool("searchEnabled"), aggregateImages: try row.bool("aggregateImages")),
                    order: try row.integer("ordinal"), detection: detection)
            }
            let deleted = try db.query("SELECT * FROM ios_film_source_deletions ORDER BY installationId,siteKey").map {
                FilmSourceKey(installationId: try $0.text("installationId"), siteKey: try $0.text("siteKey"))
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
            guard try db.query("SELECT revision FROM ios_film_source_state WHERE singleton=1").first!.integer("revision") == expected else {
                throw FilmSourceError(code: "STORAGE_CONFLICT")
            }
            try db.reconcile("ios_film_source_probes",
                columns: ["installationId", "inputRevision", "importedAt", "updatedAt", "updateState", "updateError", "sourceHash"],
                key: ["installationId"], rows: value.cats.map {
                    [.text($0.installationId), .integer($0.inputRevision), $0.importedAt.map(SQLiteValue.integer) ?? .null,
                     $0.updatedAt.map(SQLiteValue.integer) ?? .null, .text($0.updateState), .optional($0.updateError), .optional($0.sourceHash)]
                })
            var sites: [[SQLiteValue]] = [], siteActions: [[SQLiteValue]] = [], siteMethods: [[SQLiteValue]] = []
            for cat in value.cats {
                for (ordinal, site) in cat.sites.enumerated() {
                    let identity: [SQLiteValue] = [.text(cat.installationId), .text(site.key)]
                    sites.append(identity + [.integer(ordinal), .text(site.name), .text(site.api)])
                    siteActions += site.actions.enumerated().map { identity + [.integer($0.offset), .text($0.element)] }
                    siteMethods += site.methods.map { identity + [.text($0.key), .text($0.value)] }
                }
            }
            try db.reconcile("ios_film_source_sites", columns: ["installationId", "siteKey", "ordinal", "name", "api"],
                             key: ["installationId", "siteKey"], rows: sites)
            try saveChildren(db, prefix: "ios_film_source", actions: siteActions, methods: siteMethods)
            if let selected = value.videos.first(where: { $0.options.aggregateImages }) {
                try db.execute("UPDATE ios_film_video_intents SET aggregateImages=0 WHERE aggregateImages=1 AND NOT(installationId=? AND siteKey=?)",
                               [.text(selected.key.installationId), .text(selected.key.siteKey)])
            } else { try db.execute("UPDATE ios_film_video_intents SET aggregateImages=0 WHERE aggregateImages=1") }
            let videoRows = value.videos.map {
                [SQLiteValue.text($0.key.installationId), .text($0.key.siteKey), .integer($0.order), .text($0.name), .text($0.api),
                 .bool($0.options.enabled), .bool($0.options.homeVisible), .bool($0.options.searchEnabled), .bool($0.options.aggregateImages)]
            }
            try db.reconcile("ios_film_video_intents",
                columns: ["installationId", "siteKey", "ordinal", "name", "api", "enabled", "homeVisible", "searchEnabled", "aggregateImages"],
                key: ["installationId", "siteKey"], rows: videoRows)
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
                             columns: ["installationId", "siteKey", "result", "checkedAt", "playbackResolved"],
                             key: ["installationId", "siteKey"], rows: detections)
            try db.reconcile("ios_film_video_probe_steps",
                             columns: ["installationId", "siteKey", "ordinal", "operation", "state", "error"],
                             key: ["installationId", "siteKey", "ordinal"], rows: steps)
            try db.reconcile("ios_film_source_deletions", columns: ["installationId", "siteKey"],
                             key: ["installationId", "siteKey"],
                             rows: value.deleted.map { [.text($0.installationId), .text($0.siteKey)] })
            guard try db.execute("UPDATE ios_film_source_state SET revision=revision+1 WHERE singleton=1 AND revision=? AND revision<9007199254740991",
                                 [.integer(expected)]) == 1 else { throw FilmSourceError(code: "STORAGE_CONFLICT") }
            return expected + 1
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
        try db.reconcile("\(prefix)_actions", columns: ["installationId", "siteKey", "ordinal", "action"],
                         key: ["installationId", "siteKey", "ordinal"], rows: actions)
        try db.reconcile("\(prefix)_methods", columns: ["installationId", "siteKey", "action", "method"],
                         key: ["installationId", "siteKey", "action"], rows: methods)
    }
}
