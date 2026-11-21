import Foundation
import MeowStorage

enum MusicQueueAreaStore {
    private static let entryInsertSQL: String = {
        let cols = ["scope", "entry_id", "position", "song_id"] + MusicPersistenceHelpers.contextColumns
        let placeholders = Array(repeating: "?", count: cols.count).joined(separator: ",")
        return "INSERT INTO music_queue_entries(\(cols.joined(separator: ","))) VALUES(\(placeholders))"
    }()

    private static let returnEntryInsertSQL: String = {
        let cols = ["scope", "entry_id", "position", "song_id"] + MusicPersistenceHelpers.contextColumns
        let placeholders = Array(repeating: "?", count: cols.count).joined(separator: ",")
        return "INSERT INTO music_queue_similar_return_entries(\(cols.joined(separator: ","))) VALUES(\(placeholders))"
    }()

    static func read(_ db: SQLiteConnection, scope: String) throws -> MJ? {
        guard let state = try db.query(
            "SELECT * FROM music_queue_state WHERE scope=?",
            [.text(scope)]
        ).first else { return nil }

        let entries: [MJ] = try db.query(
            "SELECT * FROM music_queue_entries WHERE scope=? ORDER BY position",
            [.text(scope)]
        ).map { row in
            var fields: [(String, MJ)] = [
                ("id", .string(try row.text("entry_id"))),
                ("songId", .string(try row.text("song_id")))
            ]
            if let context = try MusicPersistenceHelpers.readContext(row) {
                fields.append(("context", context))
            }
            return .obj(fields)
        }

        var lists: [String: [String]] = [:]
        for item in try db.query(
            "SELECT list_kind,item_value FROM music_queue_list_items WHERE scope=? ORDER BY list_kind,position",
            [.text(scope)]
        ) {
            lists[try item.text("list_kind"), default: []].append(try item.text("item_value"))
        }

        var fields: [(String, MJ)] = [
            ("schema", .n(1)),
            ("entries", .array(entries)),
            ("currentId", (try state.optionalText("current_id")).map(MJ.string) ?? .null),
            ("priority", .strings(lists["priority"] ?? [])),
            ("history", .strings(lists["history"] ?? [])),
            ("source", try MusicPersistenceHelpers.readSource(state, prefix: "source_")),
            ("serial", .n(try state.integer("serial"))),
            ("revision", .n(try state.integer("revision"))),
            ("failed", .strings(lists["failed"] ?? []))
        ]
        if let runID = try state.optionalText("run_id") {
            fields.append(("runId", .string(runID)))
        }
        if let similar = try readSimilar(db, scope: scope) {
            fields.append(("similar", similar))
        }
        return .obj(fields)
    }

    static func write(_ db: SQLiteConnection, scope: String, value: MJ) throws {
        let stateCols = ["scope", "current_id", "serial", "revision", "run_id"] +
            MusicPersistenceHelpers.sourceColumns(prefix: "source_")
        let stateVals: [SQLiteValue] = [
            .text(scope),
            MusicPersistenceHelpers.optionalNonEmpty(value["currentId"]),
            .integer(value["serial"].int),
            .integer(value["revision"].int),
            MusicPersistenceHelpers.optionalNonEmpty(value["runId"])
        ] + MusicPersistenceHelpers.bindSource(value["source"])

        try db.upsert("music_queue_state", columns: stateCols, values: stateVals, key: ["scope"])

        try db.execute("DELETE FROM music_queue_entries WHERE scope=?", [.text(scope)])
        for (index, entry) in value["entries"].array.enumerated() {
            let vals: [SQLiteValue] = [
                .text(scope), .text(entry["id"].string), .integer(index), .text(entry["songId"].string)
            ] + MusicPersistenceHelpers.bindContext(entry["context"])
            try db.execute(entryInsertSQL, vals)
        }

        try db.execute("DELETE FROM music_queue_list_items WHERE scope=?", [.text(scope)])
        for kind in ["priority", "history", "failed"] {
            for (index, item) in value[kind].strings.enumerated() {
                try db.execute(
                    "INSERT INTO music_queue_list_items(scope,list_kind,position,item_value) VALUES(?,?,?,?)",
                    [.text(scope), .text(kind), .integer(index), .text(item)]
                )
            }
        }

        try writeSimilar(db, scope: scope, similar: value["similar"])
    }

    private static func readSimilar(_ db: SQLiteConnection, scope: String) throws -> MJ? {
        guard let row = try db.query(
            "SELECT * FROM music_queue_similar_state WHERE scope=?",
            [.text(scope)]
        ).first else { return nil }

        var lists: [String: [String]] = [:]
        for item in try db.query(
            "SELECT list_kind,item_value FROM music_queue_similar_list_items WHERE scope=? ORDER BY list_kind,position",
            [.text(scope)]
        ) {
            lists[try item.text("list_kind"), default: []].append(try item.text("item_value"))
        }

        let returnQueue: MJ
        if try row.bool("has_return_queue") {
            let returnEntries: [MJ] = try db.query(
                "SELECT * FROM music_queue_similar_return_entries WHERE scope=? ORDER BY position",
                [.text(scope)]
            ).map { entry in
                var fields: [(String, MJ)] = [
                    ("id", .string(try entry.text("entry_id"))),
                    ("songId", .string(try entry.text("song_id")))
                ]
                if let context = try MusicPersistenceHelpers.readContext(entry) {
                    fields.append(("context", context))
                }
                return .obj(fields)
            }
            returnQueue = .obj([
                ("entries", .array(returnEntries)),
                ("currentId", (try row.optionalText("return_current_id")).map(MJ.string) ?? .null),
                ("priority", .strings(lists["return_priority"] ?? [])),
                ("history", .strings(lists["return_history"] ?? [])),
                ("source", try MusicPersistenceHelpers.readSource(row, prefix: "return_source_"))
            ])
        } else {
            returnQueue = .null
        }

        var batchEntries: [String: [String]] = [:]
        for item in try db.query(
            "SELECT batch_id,entry_id FROM music_queue_similar_batch_entries WHERE scope=? ORDER BY batch_id,position",
            [.text(scope)]
        ) {
            batchEntries[try item.text("batch_id"), default: []].append(try item.text("entry_id"))
        }

        let batches: [MJ] = try db.query(
            "SELECT batch_id,based_on_song_id,based_on_title,provider FROM music_queue_similar_batches " +
            "WHERE scope=? ORDER BY position",
            [.text(scope)]
        ).map { batch in
            let batchID = try batch.text("batch_id")
            return .obj([
                ("id", .string(batchID)),
                ("basedOnSongId", (try batch.optionalText("based_on_song_id")).map(MJ.string) ?? .null),
                ("basedOnTitle", .string(try batch.optionalText("based_on_title") ?? "")),
                ("provider", .string(try batch.optionalText("provider") ?? "")),
                ("entryIds", .strings(batchEntries[batchID] ?? []))
            ])
        }

        let pid = try MusicPersistenceHelpers.readIdentifier(row, kindColumn: "provider_pid_kind", valueColumn: "provider_pid_value")
        let sid = try MusicPersistenceHelpers.readIdentifier(row, kindColumn: "provider_sid_kind", valueColumn: "provider_sid_value")
        let count = try row.optionalInteger("provider_count")
        let providerContext: MJ
        if pid != nil || sid != nil || count != nil {
            var ctx: [(String, MJ)] = []
            if let pid { ctx.append(("pid", pid)) }
            if let sid { ctx.append(("sid", sid)) }
            if let count { ctx.append(("count", .n(count))) }
            providerContext = .obj(ctx)
        } else {
            providerContext = .null
        }

        let seedSongId: MJ = (try row.optionalText("seed_song_id")).map(MJ.string) ?? .null
        let seedTitle = try row.optionalText("seed_title") ?? ""
        let seedEntryId: MJ = (try row.optionalText("seed_entry_id")).map(MJ.string) ?? .null
        let fromHome = try row.optionalBool("from_home") ?? false
        let provider = try row.optionalText("provider") ?? ""
        let continueEnabled = try row.optionalBool("continue_enabled") ?? false
        let offsetVal = try row.optionalInteger("offset_val") ?? 0
        let exhausted = try row.optionalBool("exhausted") ?? false
        let similarScope = try row.optionalText("similar_scope") ?? ""
        let status = try row.optionalText("status") ?? "ready"

        var fields: [(String, MJ)] = []
        fields.reserveCapacity(16)
        fields.append(("schema", .n(1)))
        fields.append(("seedSongId", seedSongId))
        fields.append(("seedTitle", .string(seedTitle)))
        fields.append(("seedEntryId", seedEntryId))
        fields.append(("returnQueue", returnQueue))
        fields.append(("sourceIds", .strings(lists["source_ids"] ?? [])))
        fields.append(("fromHome", .bool(fromHome)))
        fields.append(("provider", .string(provider)))
        fields.append(("providerContext", providerContext))
        fields.append(("continue", .bool(continueEnabled)))
        fields.append(("offset", .n(offsetVal)))
        fields.append(("exhausted", .bool(exhausted)))
        fields.append(("batches", .array(batches)))
        fields.append(("attempted", .strings(lists["attempted"] ?? [])))
        fields.append(("scope", .string(similarScope)))
        fields.append(("status", .string(status)))
        return .obj(fields)
    }

    private static func writeSimilar(_ db: SQLiteConnection, scope: String, similar: MJ) throws {
        guard similar.isObject else {
            try db.execute("DELETE FROM music_queue_similar_state WHERE scope=?", [.text(scope)])
            return
        }
        let pCtx = similar["providerContext"]
        let (pidKind, pidVal) = pCtx.isObject ? MusicPersistenceHelpers.bindIdentifier(pCtx["pid"]) : (.null, .null)
        let (sidKind, sidVal) = pCtx.isObject ? MusicPersistenceHelpers.bindIdentifier(pCtx["sid"]) : (.null, .null)
        let pCount = pCtx.isObject ? MusicPersistenceHelpers.optionalInt(pCtx["count"]) : .null
        let returnQueue = similar["returnQueue"]
        let hasReturn = returnQueue.isObject

        let cols = [
            "scope", "seed_song_id", "seed_title", "seed_entry_id", "from_home", "provider",
            "continue_enabled", "offset_val", "exhausted", "similar_scope", "status",
            "provider_pid_kind", "provider_pid_value", "provider_sid_kind", "provider_sid_value",
            "provider_count", "has_return_queue", "return_current_id"
        ] + MusicPersistenceHelpers.sourceColumns(prefix: "return_source_")
        let vals: [SQLiteValue] = [
            .text(scope),
            MusicPersistenceHelpers.optionalNonEmpty(similar["seedSongId"]),
            MusicPersistenceHelpers.optionalString(similar["seedTitle"]),
            MusicPersistenceHelpers.optionalNonEmpty(similar["seedEntryId"]),
            .bool(similar["fromHome"].bool),
            MusicPersistenceHelpers.optionalString(similar["provider"]),
            .bool(similar["continue"].bool),
            .integer(similar["offset"].int),
            .bool(similar["exhausted"].bool),
            MusicPersistenceHelpers.optionalString(similar["scope"]),
            MusicPersistenceHelpers.optionalString(similar["status"]),
            pidKind, pidVal, sidKind, sidVal, pCount,
            .bool(hasReturn),
            hasReturn ? MusicPersistenceHelpers.optionalNonEmpty(returnQueue["currentId"]) : .null
        ] + (hasReturn ? MusicPersistenceHelpers.bindSource(returnQueue["source"]) : Array(repeating: .null, count: 12))

        try db.upsert("music_queue_similar_state", columns: cols, values: vals, key: ["scope"])

        try db.execute("DELETE FROM music_queue_similar_return_entries WHERE scope=?", [.text(scope)])
        if hasReturn {
            for (index, entry) in returnQueue["entries"].array.enumerated() {
                let rowVals: [SQLiteValue] = [
                    .text(scope), .text(entry["id"].string), .integer(index), .text(entry["songId"].string)
                ] + MusicPersistenceHelpers.bindContext(entry["context"])
                try db.execute(returnEntryInsertSQL, rowVals)
            }
        }

        try db.execute("DELETE FROM music_queue_similar_list_items WHERE scope=?", [.text(scope)])
        let listGroups: [(String, [String])] = [
            ("return_priority", hasReturn ? returnQueue["priority"].strings : []),
            ("return_history", hasReturn ? returnQueue["history"].strings : []),
            ("source_ids", similar["sourceIds"].strings),
            ("attempted", similar["attempted"].strings)
        ]
        for (kind, items) in listGroups {
            for (index, item) in items.enumerated() {
                try db.execute(
                    "INSERT INTO music_queue_similar_list_items(scope,list_kind,position,item_value) VALUES(?,?,?,?)",
                    [.text(scope), .text(kind), .integer(index), .text(item)]
                )
            }
        }

        // Cascades to music_queue_similar_batch_entries.
        try db.execute("DELETE FROM music_queue_similar_batches WHERE scope=?", [.text(scope)])
        for (index, batch) in similar["batches"].array.enumerated() {
            let batchID = batch["id"].string
            try db.execute(
                "INSERT INTO music_queue_similar_batches(scope,batch_id,position,based_on_song_id,based_on_title,provider) " +
                "VALUES(?,?,?,?,?,?)",
                [
                    .text(scope), .text(batchID), .integer(index),
                    MusicPersistenceHelpers.optionalNonEmpty(batch["basedOnSongId"]),
                    MusicPersistenceHelpers.optionalString(batch["basedOnTitle"]),
                    MusicPersistenceHelpers.optionalString(batch["provider"])
                ]
            )
            for (eIndex, entryID) in batch["entryIds"].strings.enumerated() {
                try db.execute(
                    "INSERT INTO music_queue_similar_batch_entries(scope,batch_id,position,entry_id) VALUES(?,?,?,?)",
                    [.text(scope), .text(batchID), .integer(eIndex), .text(entryID)]
                )
            }
        }
    }
}
