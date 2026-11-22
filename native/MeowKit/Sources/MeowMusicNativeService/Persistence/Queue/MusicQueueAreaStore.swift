import Foundation
import MeowStorage

enum MusicQueueAreaStore {
    private static let entryColumns = ["scope", "entry_id", "position", "song_id"] + MusicPersistenceHelpers.contextColumns
    private static let entrySelectColumns = "entry_id,song_id," + MusicPersistenceHelpers.contextColumns.joined(separator: ",")
    private static let stateSelectColumns =
        "current_id,serial,revision,run_id," + MusicPersistenceHelpers.sourceColumns(prefix: "source_").joined(separator: ",")
    private static let similarSelectColumns =
        "seed_song_id,seed_title,seed_entry_id,from_home,provider,continue_enabled,offset_val,exhausted," +
        "similar_scope,status,provider_pid,provider_sid,provider_count,has_return_queue,return_current_id," +
        MusicPersistenceHelpers.sourceColumns(prefix: "return_source_").joined(separator: ",")

    private struct QueueEntryRow: Equatable {
        let id: String
        let songID: String
        let context: MusicPlayContextRecord
    }

    static func read(_ db: SQLiteConnection, scope: String) throws -> MJ? {
        guard let state = try db.queryOne(
            "SELECT \(stateSelectColumns) FROM music_queue_state WHERE scope=?",
            [.text(scope)],
            map: { c in
                (
                    currentID: try c.optionalText(0),
                    serial: try c.integer(1),
                    revision: try c.integer(2),
                    runID: try c.optionalText(3),
                    source: try MusicSourceRecord.read(from: c, offset: 4)
                )
            }
        ) else { return nil }

        let entries: [MJ] = try db.query(
            "SELECT \(entrySelectColumns) FROM music_queue_entries WHERE scope=? ORDER BY position",
            [.text(scope)]
        ) { c in
            var fields: [(String, MJ)] = [
                ("id", .string(try c.text(0))),
                ("songId", .string(try c.text(1)))
            ]
            if let context = try MusicPlayContextRecord.read(from: c, offset: 2).toMJ() {
                fields.append(("context", context))
            }
            return .obj(fields)
        }

        var lists: [String: [String]] = [:]
        try db.scan(
            "SELECT list_kind,item_value FROM music_queue_list_items WHERE scope=? ORDER BY list_kind,position",
            [.text(scope)]
        ) { c in
            lists[try c.text(0), default: []].append(try c.text(1))
        }

        var fields: [(String, MJ)] = [
            ("schema", .n(1)),
            ("entries", .array(entries)),
            ("currentId", state.currentID.map(MJ.string) ?? .null),
            ("priority", .strings(lists["priority"] ?? [])),
            ("history", .strings(lists["history"] ?? [])),
            ("source", state.source?.toMJ() ?? .null),
            ("serial", .n(state.serial)),
            ("revision", .n(state.revision)),
            ("failed", .strings(lists["failed"] ?? []))
        ]
        if let runID = state.runID {
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
        let sourceRec = MusicSourceRecord.from(value["source"]) ?? .empty
        let stateVals: [SQLiteValue] = [
            .text(scope),
            MusicPersistenceHelpers.optionalNonEmpty(value["currentId"]),
            .integer(value["serial"].int),
            .integer(value["revision"].int),
            MusicPersistenceHelpers.optionalNonEmpty(value["runId"])
        ] + sourceRec.sqliteValues

        try db.upsert("music_queue_state", columns: stateCols, values: stateVals, key: ["scope"])

        // Row-level incremental diff for queue entries (track switch = 0 entry writes!)
        var prevEntries: [String: (pos: Int, row: QueueEntryRow)] = [:]
        try db.scan(
            "SELECT position,\(entrySelectColumns) FROM music_queue_entries WHERE scope=?",
            [.text(scope)]
        ) { c in
            let pos = try c.integer(0)
            let id = try c.text(1)
            prevEntries[id] = (pos, QueueEntryRow(
                id: id,
                songID: try c.text(2),
                context: try MusicPlayContextRecord.read(from: c, offset: 3)
            ))
        }

        let nextEntries: [QueueEntryRow] = value["entries"].array.map { entry in
            QueueEntryRow(
                id: entry["id"].string,
                songID: entry["songId"].string,
                context: MusicPlayContextRecord.from(entry["context"])
            )
        }
        let nextEntryIDs = Set(nextEntries.map(\.id))
        for oldID in prevEntries.keys where !nextEntryIDs.contains(oldID) {
            try db.execute("DELETE FROM music_queue_entries WHERE scope=? AND entry_id=?", [.text(scope), .text(oldID)])
        }

        for (index, entry) in nextEntries.enumerated() {
            if let old = prevEntries[entry.id] {
                if old.pos == index && old.row == entry { continue }
                if old.row == entry {
                    try db.execute(
                        "UPDATE music_queue_entries SET position=? WHERE scope=? AND entry_id=?",
                        [.integer(index), .text(scope), .text(entry.id)]
                    )
                    continue
                }
            }
            let vals: [SQLiteValue] = [.text(scope), .text(entry.id), .integer(index), .text(entry.songID)] + entry.context.sqliteValues
            try db.upsert("music_queue_entries", columns: entryColumns, values: vals, key: ["scope", "entry_id"])
        }

        // Prefix-diff for priority / history / failed lists
        for kind in ["priority", "history", "failed"] {
            let nextItems = value[kind].strings
            let prevItems = try db.query(
                "SELECT item_value FROM music_queue_list_items WHERE scope=? AND list_kind=? ORDER BY position",
                [.text(scope), .text(kind)]
            ) { try $0.text(0) }
            if prevItems == nextItems { continue }
            let prefix = MusicPersistenceHelpers.commonPrefixLength(prevItems, nextItems)
            if prefix < prevItems.count {
                try db.execute(
                    "DELETE FROM music_queue_list_items WHERE scope=? AND list_kind=? AND position>=?",
                    [.text(scope), .text(kind), .integer(prefix)]
                )
            }
            for index in prefix..<nextItems.count {
                try db.execute(
                    "INSERT INTO music_queue_list_items(scope,list_kind,position,item_value) VALUES(?,?,?,?)",
                    [.text(scope), .text(kind), .integer(index), .text(nextItems[index])]
                )
            }
        }

        let prevSimilar = try readSimilar(db, scope: scope)
        let nextSimilar: MJ? = value["similar"].isObject ? value["similar"] : nil
        let similarSame: Bool
        switch (prevSimilar, nextSimilar) {
        case (nil, nil): similarSame = true
        case (let a?, let b?): similarSame = MusicPersistenceHelpers.semanticEquals(a, b)
        default: similarSame = false
        }
        if !similarSame {
            try writeSimilar(db, scope: scope, similar: value["similar"])
        }
    }

    private static func readSimilar(_ db: SQLiteConnection, scope: String) throws -> MJ? {
        guard let row = try db.queryOne(
            "SELECT \(similarSelectColumns) FROM music_queue_similar_state WHERE scope=?",
            [.text(scope)],
            map: { c in
                (
                    seedSongID: try c.optionalText(0),
                    seedTitle: try c.optionalText(1) ?? "",
                    seedEntryID: try c.optionalText(2),
                    fromHome: try c.optionalBool(3) ?? false,
                    provider: try c.optionalText(4) ?? "",
                    continueEnabled: try c.optionalBool(5) ?? false,
                    offsetVal: try c.optionalInteger(6) ?? 0,
                    exhausted: try c.optionalBool(7) ?? false,
                    similarScope: try c.optionalText(8) ?? "",
                    status: try c.optionalText(9) ?? "ready",
                    providerPID: try c.optionalText(10),
                    providerSID: try c.optionalText(11),
                    providerCount: try c.optionalInteger(12),
                    hasReturnQueue: try c.bool(13),
                    returnCurrentID: try c.optionalText(14),
                    returnSource: try MusicSourceRecord.read(from: c, offset: 15)
                )
            }
        ) else { return nil }

        var lists: [String: [String]] = [:]
        try db.scan(
            "SELECT list_kind,item_value FROM music_queue_similar_list_items WHERE scope=? ORDER BY list_kind,position",
            [.text(scope)]
        ) { c in
            lists[try c.text(0), default: []].append(try c.text(1))
        }

        let returnQueue: MJ
        if row.hasReturnQueue {
            let returnEntries: [MJ] = try db.query(
                "SELECT \(entrySelectColumns) FROM music_queue_similar_return_entries WHERE scope=? ORDER BY position",
                [.text(scope)]
            ) { entry in
                var fields: [(String, MJ)] = [
                    ("id", .string(try entry.text(0))),
                    ("songId", .string(try entry.text(1)))
                ]
                if let context = try MusicPlayContextRecord.read(from: entry, offset: 2).toMJ() {
                    fields.append(("context", context))
                }
                return .obj(fields)
            }
            returnQueue = .obj([
                ("entries", .array(returnEntries)),
                ("currentId", row.returnCurrentID.map(MJ.string) ?? .null),
                ("priority", .strings(lists["return_priority"] ?? [])),
                ("history", .strings(lists["return_history"] ?? [])),
                ("source", row.returnSource?.toMJ() ?? .null)
            ])
        } else {
            returnQueue = .null
        }

        var batchEntries: [String: [String]] = [:]
        try db.scan(
            "SELECT batch_id,entry_id FROM music_queue_similar_batch_entries WHERE scope=? ORDER BY batch_id,position",
            [.text(scope)]
        ) { c in
            batchEntries[try c.text(0), default: []].append(try c.text(1))
        }

        let batches: [MJ] = try db.query(
            "SELECT batch_id,based_on_song_id,based_on_title,provider FROM music_queue_similar_batches " +
            "WHERE scope=? ORDER BY position",
            [.text(scope)]
        ) { batch in
            let batchID = try batch.text(0)
            return .obj([
                ("id", .string(batchID)),
                ("basedOnSongId", (try batch.optionalText(1)).map(MJ.string) ?? .null),
                ("basedOnTitle", .string(try batch.optionalText(2) ?? "")),
                ("provider", .string(try batch.optionalText(3) ?? "")),
                ("entryIds", .strings(batchEntries[batchID] ?? []))
            ])
        }

        let providerContext: MJ
        if row.providerPID != nil || row.providerSID != nil || row.providerCount != nil {
            var ctx: [(String, MJ)] = []
            if let pid = row.providerPID { ctx.append(("pid", .string(pid))) }
            if let sid = row.providerSID { ctx.append(("sid", .string(sid))) }
            if let count = row.providerCount { ctx.append(("count", .n(count))) }
            providerContext = .obj(ctx)
        } else {
            providerContext = .null
        }

        return .obj([
            ("schema", .n(1)),
            ("seedSongId", row.seedSongID.map(MJ.string) ?? .null),
            ("seedTitle", .string(row.seedTitle)),
            ("seedEntryId", row.seedEntryID.map(MJ.string) ?? .null),
            ("returnQueue", returnQueue),
            ("sourceIds", .strings(lists["source_ids"] ?? [])),
            ("fromHome", .bool(row.fromHome)),
            ("provider", .string(row.provider)),
            ("providerContext", providerContext),
            ("continue", .bool(row.continueEnabled)),
            ("offset", .n(row.offsetVal)),
            ("exhausted", .bool(row.exhausted)),
            ("batches", .array(batches)),
            ("attempted", .strings(lists["attempted"] ?? [])),
            ("scope", .string(row.similarScope)),
            ("status", .string(row.status))
        ])
    }

    private static func writeSimilar(_ db: SQLiteConnection, scope: String, similar: MJ) throws {
        guard similar.isObject else {
            try db.execute("DELETE FROM music_queue_similar_state WHERE scope=?", [.text(scope)])
            return
        }
        let pCtx = similar["providerContext"]
        let pidVal: SQLiteValue = (pCtx.isObject && !pCtx["pid"].id.isEmpty) ? .text(pCtx["pid"].id) : .null
        let sidVal: SQLiteValue = (pCtx.isObject && !pCtx["sid"].id.isEmpty) ? .text(pCtx["sid"].id) : .null
        let pCount = pCtx.isObject ? MusicPersistenceHelpers.optionalInt(pCtx["count"]) : .null
        let returnQueue = similar["returnQueue"]
        let hasReturn = returnQueue.isObject

        let cols = [
            "scope", "seed_song_id", "seed_title", "seed_entry_id", "from_home", "provider",
            "continue_enabled", "offset_val", "exhausted", "similar_scope", "status",
            "provider_pid", "provider_sid", "provider_count", "has_return_queue", "return_current_id"
        ] + MusicPersistenceHelpers.sourceColumns(prefix: "return_source_")
        let returnSourceRec = hasReturn ? (MusicSourceRecord.from(returnQueue["source"]) ?? .empty) : .empty
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
            pidVal, sidVal, pCount,
            .bool(hasReturn),
            hasReturn ? MusicPersistenceHelpers.optionalNonEmpty(returnQueue["currentId"]) : .null
        ] + returnSourceRec.sqliteValues

        try db.upsert("music_queue_similar_state", columns: cols, values: vals, key: ["scope"])

        try db.execute("DELETE FROM music_queue_similar_return_entries WHERE scope=?", [.text(scope)])
        if hasReturn {
            for (index, entry) in returnQueue["entries"].array.enumerated() {
                let rowVals: [SQLiteValue] = [
                    .text(scope), .text(entry["id"].string), .integer(index), .text(entry["songId"].string)
                ] + MusicPlayContextRecord.from(entry["context"]).sqliteValues
                try db.upsert("music_queue_similar_return_entries", columns: entryColumns, values: rowVals, key: ["scope", "entry_id"])
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
