import Foundation
import MeowStorage

enum MusicQueueRepository {
    private static let entryColumns = ["scope", "entry_id", "position", "song_id", "context_id"]
    private static let stateSelectColumns =
        "q.current_id,q.serial,q.revision,e.run_id," +
        "q.source_id,q.source_title,q.source_platform," +
        "e.source_type,e.source_source_title,e.source_source_id,e.source_playlist," +
        "e.source_seed_entry_id,e.source_remote_session,e.source_daily_branch,e.source_search_keyword"
    private static let similarSelectColumns =
        "seed_song_id,seed_title,seed_entry_id,from_home,provider,continue_enabled,offset_val,exhausted," +
        "similar_scope,status,provider_pid,provider_sid,provider_count,has_return_queue,return_current_id," +
        MusicSourceRecord.columns(prefix: "return_source_").joined(separator: ",")
    private static let fmSelectColumns =
        "provider,mode,submode,entry,rule_id,tab_tag_id,source_name,continue_enabled"

    static func read(_ db: SQLiteConnection, scope: String) throws -> MusicQueueState? {
        guard let state = try db.queryOne(
            "SELECT \(stateSelectColumns) FROM music_queue_state q " +
            "LEFT JOIN ios_music_queue_extras e ON e.scope=q.scope " +
            "WHERE q.scope=?",
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
            "SELECT entry_id,song_id,context_id FROM music_queue_entries WHERE scope=? ORDER BY position", [.text(scope)]
        ) { c in (id: try c.text(0), songID: try c.text(1), contextID: try c.optionalText(2)) }

        var contextIDs = Set(rawEntries.compactMap(\.contextID))
        var lists: [String: [String]] = [:]
        try db.scan("SELECT list_kind,item_value FROM music_queue_list_items WHERE scope=? ORDER BY list_kind,position", [.text(scope)]) { c in
            lists[try c.text(0), default: []].append(try c.text(1))
        }

        let similarRaw = try readSimilar(db, scope: scope, contextIDs: &contextIDs)
        let contexts = try MusicPlayContextStore.loadMap(db, ids: contextIDs)
        let entries = rawEntries.map { row in
            MusicQueueEntryRecord(id: row.id, songID: row.songID, context: row.contextID.flatMap { contexts[$0] } ?? .empty)
        }
        let similar = similarRaw?(contexts)
        let fm = try readFM(db, scope: scope)

        return MusicQueueState(
            currentID: state.currentID, serial: state.serial, revision: state.revision, runID: state.runID,
            source: state.source, entries: entries, priority: lists["priority"] ?? [],
            history: lists["history"] ?? [], failed: lists["failed"] ?? [], similar: similar, fm: fm
        )
    }

    static func applyChanges(_ db: SQLiteConnection,
                             scope: String,
                             previous: MusicQueueState?,
                             next: MusicQueueState) throws {
        let stateCols = ["scope", "current_id", "serial", "revision"] +
            MusicSourceRecord.sharedColumns(prefix: "source_")
        let extrasCols = ["scope", "run_id"] + MusicSourceRecord.extrasColumns(prefix: "source_")
        let currentID = next.currentID.flatMap { $0.isEmpty ? nil : $0 }
        let runID = next.runID.flatMap { $0.isEmpty ? nil : $0 }
        let sourceRec = next.source ?? .empty
        if previous == nil ||
            previous?.currentID != currentID ||
            previous?.serial != next.serial ||
            previous?.revision != next.revision ||
            previous?.runID != runID ||
            previous?.source != next.source {
            let stateVals: [SQLiteValue] = [
                .text(scope),
                .optional(currentID),
                .integer(next.serial),
                .integer(next.revision)
            ] + sourceRec.sharedSQLiteValues
            try db.upsert("music_queue_state", columns: stateCols, values: stateVals, key: ["scope"])

            if runID == nil && !sourceRec.hasIOSExtras {
                try db.execute("DELETE FROM ios_music_queue_extras WHERE scope=?", [.text(scope)])
            } else {
                let extrasVals: [SQLiteValue] = [.text(scope), .optional(runID)] + sourceRec.extrasSQLiteValues
                try db.upsert("ios_music_queue_extras", columns: extrasCols, values: extrasVals, key: ["scope"])
            }
        }

        var contextsTouched = false

        if previous == nil || previous?.entries != next.entries {
            if previous == nil {
                if try db.execute("DELETE FROM music_queue_entries WHERE scope=?", [.text(scope)]) > 0 {
                    contextsTouched = true
                }
            }
            var prevEntries: [String: (pos: Int, row: MusicQueueEntryRecord)] = [:]
            if let prevList = previous?.entries {
                for (index, row) in prevList.enumerated() {
                    prevEntries[row.id] = (index, row)
                }
            }
            let nextEntryIDs = Set(next.entries.map(\.id))
            for (oldID, old) in prevEntries where !nextEntryIDs.contains(oldID) {
                try db.execute("DELETE FROM music_queue_entries WHERE scope=? AND entry_id=?", [.text(scope), .text(oldID)])
                if !old.row.context.isEmpty { contextsTouched = true }
            }
            for (index, entry) in next.entries.enumerated() {
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
            ("priority", next.priority, previous?.priority),
            ("history", next.history, previous?.history),
            ("failed", next.failed, previous?.failed)
        ]
        for (kind, nextItems, prevItemsOpt) in listPairs {
            if previous != nil && prevItemsOpt == nextItems { continue }
            for (index, item) in nextItems.enumerated() {
                try db.upsert(
                    "music_queue_list_items",
                    columns: ["scope", "list_kind", "position", "item_value"],
                    values: [.text(scope), .text(kind), .integer(index), .text(item)],
                    key: ["scope", "list_kind", "position"]
                )
            }
            try db.execute(
                "DELETE FROM music_queue_list_items WHERE scope=? AND list_kind=? AND position>=?",
                [.text(scope), .text(kind), .integer(nextItems.count)]
            )
        }

        if previous == nil || previous?.similar != next.similar {
            let hadReturnContexts = previous?.similar?.returnQueue?.entries.contains(where: { !$0.context.isEmpty }) ?? (previous == nil)
            if hadReturnContexts { contextsTouched = true }
            try applySimilarChanges(db, scope: scope, previous: previous?.similar, next: next.similar)
        }

        if previous == nil || previous?.fm != next.fm {
            try applyFMChanges(db, scope: scope, next: next.fm)
        }

        if contextsTouched {
            try MusicPlayContextStore.pruneOrphans(db)
        }
    }

    private static func readFM(_ db: SQLiteConnection, scope: String) throws -> MusicQueueFMState? {
        try db.queryOne("SELECT \(fmSelectColumns) FROM ios_music_queue_fm_state WHERE scope=?", [.text(scope)]) { c in
            MusicQueueFMState(provider: try c.text(0), mode: try c.optionalText(1), submode: try c.text(2),
                entry: try c.optionalText(3), ruleID: try c.optionalText(4), tabTagID: try c.optionalText(5),
                sourceName: try c.text(6), continueEnabled: try c.bool(7))
        }
    }

    private static func applyFMChanges(_ db: SQLiteConnection, scope: String, next: MusicQueueFMState?) throws {
        guard let next else {
            try db.execute("DELETE FROM ios_music_queue_fm_state WHERE scope=?", [.text(scope)])
            return
        }
        try db.upsert(
            "ios_music_queue_fm_state",
            columns: ["scope", "provider", "mode", "submode", "entry", "rule_id", "tab_tag_id", "source_name", "continue_enabled"],
            values: [.text(scope), .text(next.provider), .optional(next.mode), .text(next.submode), .optional(next.entry),
                     .optional(next.ruleID), .optional(next.tabTagID), .text(next.sourceName), .bool(next.continueEnabled)],
            key: ["scope"]
        )
    }

    private static func readSimilar(
        _ db: SQLiteConnection, scope: String, contextIDs: inout Set<String>
    ) throws -> (([String: MusicPlayContextRecord]) -> MusicQueueSimilarState)? {
        guard let row = try db.queryOne(
            "SELECT \(similarSelectColumns) FROM ios_music_queue_similar_state WHERE scope=?", [.text(scope)],
            map: { c in
                (seedSongID: try c.optionalText(0), seedTitle: try c.optionalText(1) ?? "",
                 seedEntryID: try c.optionalText(2), fromHome: try c.optionalBool(3) ?? false,
                 provider: try c.optionalText(4) ?? "", continueEnabled: try c.optionalBool(5) ?? false,
                 offsetVal: try c.optionalInteger(6) ?? 0, exhausted: try c.optionalBool(7) ?? false,
                 similarScope: try c.optionalText(8) ?? "", status: try c.optionalText(9) ?? "ready",
                 providerPID: try c.optionalText(10), providerSID: try c.optionalText(11),
                 providerCount: try c.optionalInteger(12), hasReturnQueue: try c.bool(13),
                 returnCurrentID: try c.optionalText(14), returnSource: try MusicSourceRecord.read(from: c, offset: 15))
            }
        ) else { return nil }

        var lists: [String: [String]] = [:]
        try db.scan("SELECT list_kind,item_value FROM ios_music_queue_similar_list_items WHERE scope=? ORDER BY list_kind,position", [.text(scope)]) { c in
            lists[try c.text(0), default: []].append(try c.text(1))
        }
        let returnEntries: [(id: String, songID: String, contextID: String?)] = row.hasReturnQueue
            ? try db.query("SELECT entry_id,song_id,context_id FROM ios_music_queue_similar_return_entries WHERE scope=? ORDER BY position", [.text(scope)]) { c in
                (id: try c.text(0), songID: try c.text(1), contextID: try c.optionalText(2))
            } : []
        for item in returnEntries { if let cid = item.contextID { contextIDs.insert(cid) } }

        var batchEntries: [String: [String]] = [:]
        try db.scan("SELECT batch_id,entry_id FROM ios_music_queue_similar_batch_entries WHERE scope=? ORDER BY batch_id,position", [.text(scope)]) { c in
            batchEntries[try c.text(0), default: []].append(try c.text(1))
        }
        let batches: [MusicQueueSimilarBatchRecord] = try db.query(
            "SELECT batch_id,based_on_song_id,based_on_title,provider FROM ios_music_queue_similar_batches WHERE scope=? ORDER BY position", [.text(scope)]
        ) { c in
            let batchID = try c.text(0)
            return MusicQueueSimilarBatchRecord(id: batchID, basedOnSongID: try c.optionalText(1),
                basedOnTitle: try c.optionalText(2) ?? "", provider: try c.optionalText(3) ?? "",
                entryIDs: batchEntries[batchID] ?? [])
        }

        return { contexts in
            let returnQueue: MusicQueueSimilarReturnRecord? = row.hasReturnQueue ? MusicQueueSimilarReturnRecord(
                entries: returnEntries.map { MusicQueueEntryRecord(id: $0.id, songID: $0.songID, context: $0.contextID.flatMap { contexts[$0] } ?? .empty) },
                currentID: row.returnCurrentID, priority: lists["return_priority"] ?? [],
                history: lists["return_history"] ?? [], source: row.returnSource
            ) : nil
            return MusicQueueSimilarState(
                seedSongID: row.seedSongID, seedTitle: row.seedTitle, seedEntryID: row.seedEntryID,
                fromHome: row.fromHome, provider: row.provider, continueEnabled: row.continueEnabled,
                offsetVal: row.offsetVal, exhausted: row.exhausted, similarScope: row.similarScope,
                status: row.status, providerPID: row.providerPID, providerSID: row.providerSID,
                providerCount: row.providerCount, returnQueue: returnQueue,
                sourceIDs: lists["source_ids"] ?? [], attempted: lists["attempted"] ?? [], batches: batches
            )
        }
    }

    private static func applySimilarChanges(_ db: SQLiteConnection,
                                            scope: String,
                                            previous: MusicQueueSimilarState?,
                                            next: MusicQueueSimilarState?) throws {
        guard let next else {
            try db.execute("DELETE FROM ios_music_queue_similar_state WHERE scope=?", [.text(scope)])
            return
        }

        let hasReturn = next.returnQueue != nil
        let cols = [
            "scope", "seed_song_id", "seed_title", "seed_entry_id", "from_home", "provider",
            "continue_enabled", "offset_val", "exhausted", "similar_scope", "status",
            "provider_pid", "provider_sid", "provider_count", "has_return_queue", "return_current_id"
        ] + MusicSourceRecord.columns(prefix: "return_source_")
        let returnSourceRec = next.returnQueue?.source ?? .empty
        let vals: [SQLiteValue] = [
            .text(scope),
            .optional(next.seedSongID.flatMap { $0.isEmpty ? nil : $0 }),
            .text(next.seedTitle),
            .optional(next.seedEntryID.flatMap { $0.isEmpty ? nil : $0 }),
            .bool(next.fromHome),
            .text(next.provider),
            .bool(next.continueEnabled),
            .integer(next.offsetVal),
            .bool(next.exhausted),
            .text(next.similarScope),
            .text(next.status),
            .optional(next.providerPID.flatMap { $0.isEmpty ? nil : $0 }),
            .optional(next.providerSID.flatMap { $0.isEmpty ? nil : $0 }),
            .optional(next.providerCount),
            .bool(hasReturn),
            .optional(next.returnQueue?.currentID.flatMap { $0.isEmpty ? nil : $0 })
        ] + returnSourceRec.sqliteValues

        try db.upsert("ios_music_queue_similar_state", columns: cols, values: vals, key: ["scope"])

        let prevReturnEntries = previous?.returnQueue?.entries ?? []
        let nextReturnEntries = next.returnQueue?.entries ?? []
        if previous == nil || prevReturnEntries != nextReturnEntries {
            if previous == nil {
                try db.execute("DELETE FROM ios_music_queue_similar_return_entries WHERE scope=?", [.text(scope)])
            }
            var prevByID: [String: (pos: Int, row: MusicQueueEntryRecord)] = [:]
            for (idx, row) in prevReturnEntries.enumerated() { prevByID[row.id] = (idx, row) }
            let nextIDs = Set(nextReturnEntries.map(\.id))
            for oldID in prevByID.keys where !nextIDs.contains(oldID) {
                try db.execute(
                    "DELETE FROM ios_music_queue_similar_return_entries WHERE scope=? AND entry_id=?",
                    [.text(scope), .text(oldID)]
                )
            }
            for (index, entry) in nextReturnEntries.enumerated() {
                if let old = prevByID[entry.id], old.pos == index, old.row == entry { continue }
                let contextID = try MusicPlayContextStore.upsert(db, context: entry.context)
                try db.upsert(
                    "ios_music_queue_similar_return_entries",
                    columns: entryColumns,
                    values: [.text(scope), .text(entry.id), .integer(index), .text(entry.songID), .optional(contextID)],
                    key: ["scope", "entry_id"]
                )
            }
        }

        let listGroups: [(String, [String], [String]?)] = [
            ("return_priority", next.returnQueue?.priority ?? [], previous?.returnQueue?.priority),
            ("return_history", next.returnQueue?.history ?? [], previous?.returnQueue?.history),
            ("source_ids", next.sourceIDs, previous?.sourceIDs),
            ("attempted", next.attempted, previous?.attempted)
        ]
        for (kind, items, prevItems) in listGroups {
            if previous != nil && prevItems == items { continue }
            for (index, item) in items.enumerated() {
                try db.upsert(
                    "ios_music_queue_similar_list_items",
                    columns: ["scope", "list_kind", "position", "item_value"],
                    values: [.text(scope), .text(kind), .integer(index), .text(item)],
                    key: ["scope", "list_kind", "position"]
                )
            }
            try db.execute(
                "DELETE FROM ios_music_queue_similar_list_items WHERE scope=? AND list_kind=? AND position>=?",
                [.text(scope), .text(kind), .integer(items.count)]
            )
        }

        if previous == nil || previous?.batches != next.batches {
            if previous == nil {
                try db.execute("DELETE FROM ios_music_queue_similar_batches WHERE scope=?", [.text(scope)])
            }
            var prevBatchesByID: [String: (pos: Int, batch: MusicQueueSimilarBatchRecord)] = [:]
            for (idx, b) in (previous?.batches ?? []).enumerated() { prevBatchesByID[b.id] = (idx, b) }
            let nextBatchIDs = Set(next.batches.map(\.id))
            for oldID in prevBatchesByID.keys where !nextBatchIDs.contains(oldID) {
                try db.execute(
                    "DELETE FROM ios_music_queue_similar_batches WHERE scope=? AND batch_id=?",
                    [.text(scope), .text(oldID)]
                )
            }
            for (index, batch) in next.batches.enumerated() {
                let old = prevBatchesByID[batch.id]
                if let old, old.pos == index, old.batch == batch { continue }
                try db.upsert(
                    "ios_music_queue_similar_batches",
                    columns: ["scope", "batch_id", "position", "based_on_song_id", "based_on_title", "provider"],
                    values: [
                        .text(scope), .text(batch.id), .integer(index),
                        .optional(batch.basedOnSongID.flatMap { $0.isEmpty ? nil : $0 }),
                        .text(batch.basedOnTitle),
                        .text(batch.provider)
                    ],
                    key: ["scope", "batch_id"]
                )
                if old == nil || old!.batch.entryIDs != batch.entryIDs {
                    for (eIndex, entryID) in batch.entryIDs.enumerated() {
                        try db.upsert(
                            "ios_music_queue_similar_batch_entries",
                            columns: ["scope", "batch_id", "position", "entry_id"],
                            values: [.text(scope), .text(batch.id), .integer(eIndex), .text(entryID)],
                            key: ["scope", "batch_id", "position"]
                        )
                    }
                    try db.execute(
                        "DELETE FROM ios_music_queue_similar_batch_entries WHERE scope=? AND batch_id=? AND position>=?",
                        [.text(scope), .text(batch.id), .integer(batch.entryIDs.count)]
                    )
                }
            }
        }
    }
}
