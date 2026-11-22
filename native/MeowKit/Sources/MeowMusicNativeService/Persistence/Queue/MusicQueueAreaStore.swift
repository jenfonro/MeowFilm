import Foundation
import MeowStorage

enum MusicQueueAreaStore {
    private static let entryColumns = ["scope", "entry_id", "position", "song_id", "context_id"]
    private static let stateSelectColumns =
        "current_id,serial,revision,run_id," + MusicSourceRecord.columns(prefix: "source_").joined(separator: ",")
    private static let similarSelectColumns =
        "seed_song_id,seed_title,seed_entry_id,from_home,provider,continue_enabled,offset_val,exhausted," +
        "similar_scope,status,provider_pid,provider_sid,provider_count,has_return_queue,return_current_id," +
        MusicSourceRecord.columns(prefix: "return_source_").joined(separator: ",")

    static func read(_ db: SQLiteConnection, scope: String) throws -> MusicQueueAreaRecord? {
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

        let rawEntries: [(id: String, songID: String, contextID: String?)] = try db.query(
            "SELECT entry_id,song_id,context_id FROM music_queue_entries WHERE scope=? ORDER BY position",
            [.text(scope)]
        ) { c in
            (id: try c.text(0), songID: try c.text(1), contextID: try c.optionalText(2))
        }

        var contextIDs = Set(rawEntries.compactMap(\.contextID))

        var lists: [String: [String]] = [:]
        try db.scan(
            "SELECT list_kind,item_value FROM music_queue_list_items WHERE scope=? ORDER BY list_kind,position",
            [.text(scope)]
        ) { c in
            lists[try c.text(0), default: []].append(try c.text(1))
        }

        let similarRaw = try readSimilarRaw(db, scope: scope)
        if let retEntries = similarRaw?.returnEntries {
            for row in retEntries {
                if let cid = row.contextID { contextIDs.insert(cid) }
            }
        }

        let contexts = try MusicPlayContextStore.loadMap(db, ids: contextIDs)

        let entries = rawEntries.map { row in
            MusicQueueEntryRecord(
                id: row.id,
                songID: row.songID,
                context: row.contextID.flatMap { contexts[$0] } ?? .empty
            )
        }

        let similar = similarRaw.map { raw in
            buildSimilar(raw: raw, contexts: contexts)
        }

        return MusicQueueAreaRecord(
            currentID: state.currentID,
            serial: state.serial,
            revision: state.revision,
            runID: state.runID,
            source: state.source,
            entries: entries,
            priority: lists["priority"] ?? [],
            history: lists["history"] ?? [],
            failed: lists["failed"] ?? [],
            similar: similar
        )
    }

    static func write(_ db: SQLiteConnection, scope: String, value: MusicQueueAreaRecord, previous: MusicQueueAreaRecord?) throws {
        let stateCols = ["scope", "current_id", "serial", "revision", "run_id"] +
            MusicSourceRecord.columns(prefix: "source_")
        let currentID = value.currentID.flatMap { $0.isEmpty ? nil : $0 }
        let runID = value.runID.flatMap { $0.isEmpty ? nil : $0 }
        let sourceRec = value.source ?? .empty
        if previous == nil ||
            previous?.currentID != currentID ||
            previous?.serial != value.serial ||
            previous?.revision != value.revision ||
            previous?.runID != runID ||
            previous?.source != value.source {
            let stateVals: [SQLiteValue] = [
                .text(scope),
                .optional(currentID),
                .integer(value.serial),
                .integer(value.revision),
                .optional(runID)
            ] + sourceRec.sqliteValues
            try db.upsert("music_queue_state", columns: stateCols, values: stateVals, key: ["scope"])
        }

        var contextsTouched = false

        if previous == nil || previous?.entries != value.entries {
            var prevEntries: [String: (pos: Int, row: MusicQueueEntryRecord)] = [:]
            if let prevList = previous?.entries {
                for (index, row) in prevList.enumerated() {
                    prevEntries[row.id] = (index, row)
                }
            }
            let nextEntryIDs = Set(value.entries.map(\.id))
            for (oldID, old) in prevEntries where !nextEntryIDs.contains(oldID) {
                try db.execute("DELETE FROM music_queue_entries WHERE scope=? AND entry_id=?", [.text(scope), .text(oldID)])
                if !old.row.context.isEmpty { contextsTouched = true }
            }
            for (index, entry) in value.entries.enumerated() {
                if let old = prevEntries[entry.id] {
                    if old.pos == index && old.row == entry { continue }
                    if old.row == entry {
                        try db.execute(
                            "UPDATE music_queue_entries SET position=? WHERE scope=? AND entry_id=?",
                            [.integer(index), .text(scope), .text(entry.id)]
                        )
                        continue
                    }
                    if old.row.context != entry.context && !old.row.context.isEmpty {
                        contextsTouched = true
                    }
                }
                let contextID = try MusicPlayContextStore.upsert(db, context: entry.context)
                try db.upsert(
                    "music_queue_entries",
                    columns: entryColumns,
                    values: [.text(scope), .text(entry.id), .integer(index), .text(entry.songID), .optional(contextID)],
                    key: ["scope", "entry_id"]
                )
            }
        }

        let listPairs: [(String, [String], [String]?)] = [
            ("priority", value.priority, previous?.priority),
            ("history", value.history, previous?.history),
            ("failed", value.failed, previous?.failed)
        ]
        for (kind, nextItems, prevItemsOpt) in listPairs {
            let prevItems = prevItemsOpt ?? []
            if previous != nil && prevItems == nextItems { continue }
            let prefix = previous == nil ? 0 : MusicPersistenceListDiff.commonPrefixLength(prevItems, nextItems)
            if previous != nil && prefix < prevItems.count {
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

        if previous == nil || previous?.similar != value.similar {
            let hadReturnContexts = previous?.similar?.returnQueue?.entries.contains(where: { !$0.context.isEmpty }) ?? false
            if hadReturnContexts { contextsTouched = true }
            try writeSimilar(db, scope: scope, similar: value.similar, hadPrevious: previous?.similar != nil)
        }

        if contextsTouched {
            try MusicPlayContextStore.pruneOrphans(db)
        }
    }

    private struct SimilarRaw {
        let seedSongID: String?
        let seedTitle: String
        let seedEntryID: String?
        let fromHome: Bool
        let provider: String
        let continueEnabled: Bool
        let offsetVal: Int
        let exhausted: Bool
        let similarScope: String
        let status: String
        let providerPID: String?
        let providerSID: String?
        let providerCount: Int?
        let hasReturnQueue: Bool
        let returnCurrentID: String?
        let returnSource: MusicSourceRecord?
        let returnEntries: [(id: String, songID: String, contextID: String?)]
        let lists: [String: [String]]
        let batches: [MusicQueueSimilarBatchRecord]
    }

    private static func readSimilarRaw(_ db: SQLiteConnection, scope: String) throws -> SimilarRaw? {
        guard let row = try db.queryOne(
            "SELECT \(similarSelectColumns) FROM ios_music_queue_similar_state WHERE scope=?",
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
            "SELECT list_kind,item_value FROM ios_music_queue_similar_list_items WHERE scope=? ORDER BY list_kind,position",
            [.text(scope)]
        ) { c in
            lists[try c.text(0), default: []].append(try c.text(1))
        }

        let returnEntries: [(id: String, songID: String, contextID: String?)]
        if row.hasReturnQueue {
            returnEntries = try db.query(
                "SELECT entry_id,song_id,context_id FROM ios_music_queue_similar_return_entries WHERE scope=? ORDER BY position",
                [.text(scope)]
            ) { c in
                (id: try c.text(0), songID: try c.text(1), contextID: try c.optionalText(2))
            }
        } else {
            returnEntries = []
        }

        var batchEntries: [String: [String]] = [:]
        try db.scan(
            "SELECT batch_id,entry_id FROM ios_music_queue_similar_batch_entries WHERE scope=? ORDER BY batch_id,position",
            [.text(scope)]
        ) { c in
            batchEntries[try c.text(0), default: []].append(try c.text(1))
        }

        let batches: [MusicQueueSimilarBatchRecord] = try db.query(
            "SELECT batch_id,based_on_song_id,based_on_title,provider FROM ios_music_queue_similar_batches " +
            "WHERE scope=? ORDER BY position",
            [.text(scope)]
        ) { c in
            let batchID = try c.text(0)
            return MusicQueueSimilarBatchRecord(
                id: batchID,
                basedOnSongID: try c.optionalText(1),
                basedOnTitle: try c.optionalText(2) ?? "",
                provider: try c.optionalText(3) ?? "",
                entryIDs: batchEntries[batchID] ?? []
            )
        }

        return SimilarRaw(
            seedSongID: row.seedSongID,
            seedTitle: row.seedTitle,
            seedEntryID: row.seedEntryID,
            fromHome: row.fromHome,
            provider: row.provider,
            continueEnabled: row.continueEnabled,
            offsetVal: row.offsetVal,
            exhausted: row.exhausted,
            similarScope: row.similarScope,
            status: row.status,
            providerPID: row.providerPID,
            providerSID: row.providerSID,
            providerCount: row.providerCount,
            hasReturnQueue: row.hasReturnQueue,
            returnCurrentID: row.returnCurrentID,
            returnSource: row.returnSource,
            returnEntries: returnEntries,
            lists: lists,
            batches: batches
        )
    }

    private static func buildSimilar(raw: SimilarRaw, contexts: [String: MusicPlayContextRecord]) -> MusicQueueSimilarRecord {
        let returnQueue: MusicQueueSimilarReturnRecord?
        if raw.hasReturnQueue {
            let entries = raw.returnEntries.map { entry in
                MusicQueueEntryRecord(
                    id: entry.id,
                    songID: entry.songID,
                    context: entry.contextID.flatMap { contexts[$0] } ?? .empty
                )
            }
            returnQueue = MusicQueueSimilarReturnRecord(
                entries: entries,
                currentID: raw.returnCurrentID,
                priority: raw.lists["return_priority"] ?? [],
                history: raw.lists["return_history"] ?? [],
                source: raw.returnSource
            )
        } else {
            returnQueue = nil
        }
        return MusicQueueSimilarRecord(
            seedSongID: raw.seedSongID,
            seedTitle: raw.seedTitle,
            seedEntryID: raw.seedEntryID,
            fromHome: raw.fromHome,
            provider: raw.provider,
            continueEnabled: raw.continueEnabled,
            offsetVal: raw.offsetVal,
            exhausted: raw.exhausted,
            similarScope: raw.similarScope,
            status: raw.status,
            providerPID: raw.providerPID,
            providerSID: raw.providerSID,
            providerCount: raw.providerCount,
            returnQueue: returnQueue,
            sourceIDs: raw.lists["source_ids"] ?? [],
            attempted: raw.lists["attempted"] ?? [],
            batches: raw.batches
        )
    }

    private static func writeSimilar(_ db: SQLiteConnection,
                                     scope: String,
                                     similar: MusicQueueSimilarRecord?,
                                     hadPrevious: Bool) throws {
        guard let similar else {
            if hadPrevious {
                try db.execute("DELETE FROM ios_music_queue_similar_state WHERE scope=?", [.text(scope)])
            }
            return
        }

        let hasReturn = similar.returnQueue != nil
        let cols = [
            "scope", "seed_song_id", "seed_title", "seed_entry_id", "from_home", "provider",
            "continue_enabled", "offset_val", "exhausted", "similar_scope", "status",
            "provider_pid", "provider_sid", "provider_count", "has_return_queue", "return_current_id"
        ] + MusicSourceRecord.columns(prefix: "return_source_")
        let returnSourceRec = similar.returnQueue?.source ?? .empty
        let vals: [SQLiteValue] = [
            .text(scope),
            .optional(similar.seedSongID.flatMap { $0.isEmpty ? nil : $0 }),
            .text(similar.seedTitle),
            .optional(similar.seedEntryID.flatMap { $0.isEmpty ? nil : $0 }),
            .bool(similar.fromHome),
            .text(similar.provider),
            .bool(similar.continueEnabled),
            .integer(similar.offsetVal),
            .bool(similar.exhausted),
            .text(similar.similarScope),
            .text(similar.status),
            .optional(similar.providerPID.flatMap { $0.isEmpty ? nil : $0 }),
            .optional(similar.providerSID.flatMap { $0.isEmpty ? nil : $0 }),
            .optional(similar.providerCount),
            .bool(hasReturn),
            .optional(similar.returnQueue?.currentID.flatMap { $0.isEmpty ? nil : $0 })
        ] + returnSourceRec.sqliteValues

        try db.upsert("ios_music_queue_similar_state", columns: cols, values: vals, key: ["scope"])

        if hadPrevious {
            try db.execute("DELETE FROM ios_music_queue_similar_return_entries WHERE scope=?", [.text(scope)])
            try db.execute("DELETE FROM ios_music_queue_similar_list_items WHERE scope=?", [.text(scope)])
            try db.execute("DELETE FROM ios_music_queue_similar_batches WHERE scope=?", [.text(scope)])
        }

        if let returnQueue = similar.returnQueue {
            for (index, entry) in returnQueue.entries.enumerated() {
                let contextID = try MusicPlayContextStore.upsert(db, context: entry.context)
                try db.upsert(
                    "ios_music_queue_similar_return_entries",
                    columns: entryColumns,
                    values: [.text(scope), .text(entry.id), .integer(index), .text(entry.songID), .optional(contextID)],
                    key: ["scope", "entry_id"]
                )
            }
        }

        let listGroups: [(String, [String])] = [
            ("return_priority", similar.returnQueue?.priority ?? []),
            ("return_history", similar.returnQueue?.history ?? []),
            ("source_ids", similar.sourceIDs),
            ("attempted", similar.attempted)
        ]
        for (kind, items) in listGroups {
            for (index, item) in items.enumerated() {
                try db.execute(
                    "INSERT INTO ios_music_queue_similar_list_items(scope,list_kind,position,item_value) VALUES(?,?,?,?)",
                    [.text(scope), .text(kind), .integer(index), .text(item)]
                )
            }
        }

        for (index, batch) in similar.batches.enumerated() {
            try db.execute(
                "INSERT INTO ios_music_queue_similar_batches(scope,batch_id,position,based_on_song_id,based_on_title,provider) " +
                "VALUES(?,?,?,?,?,?)",
                [
                    .text(scope), .text(batch.id), .integer(index),
                    .optional(batch.basedOnSongID.flatMap { $0.isEmpty ? nil : $0 }),
                    .text(batch.basedOnTitle),
                    .text(batch.provider)
                ]
            )
            for (eIndex, entryID) in batch.entryIDs.enumerated() {
                try db.execute(
                    "INSERT INTO ios_music_queue_similar_batch_entries(scope,batch_id,position,entry_id) VALUES(?,?,?,?)",
                    [.text(scope), .text(batch.id), .integer(eIndex), .text(entryID)]
                )
            }
        }
    }
}
