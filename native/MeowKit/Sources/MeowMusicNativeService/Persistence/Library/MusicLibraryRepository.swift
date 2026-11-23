import Foundation
import MeowStorage

enum MusicLibraryRepository {
    static func read(_ db: SQLiteConnection, scope: String) throws -> MusicLibraryState? {
        guard let state = try db.queryOne(
            "SELECT revision,owner_id,library_revision,next_id FROM music_library_state WHERE scope=?",
            [.text(scope)],
            map: { c in
                (
                    revision: try c.integer(0),
                    ownerID: try c.text(1),
                    libraryRevision: try c.integer(2),
                    nextID: try c.integer(3)
                )
            }
        ) else { return nil }

        let status = try db.queryOne(
            "SELECT status_phase,status_at_ms,status_code FROM ios_music_library_status WHERE scope=?",
            [.text(scope)],
            map: { c in
                (
                    phase: try c.text(0),
                    atMs: try c.int64(1),
                    code: try c.optionalText(2)
                )
            }
        )

        let accounts: [MusicLibraryAccountRecord] = try db.query(
            "SELECT platform,account_id FROM music_library_accounts WHERE scope=? ORDER BY platform",
            [.text(scope)]
        ) { c in
            MusicLibraryAccountRecord(platform: try c.text(0), accountID: try c.text(1))
        }

        let statusIssues: [MusicLibraryIssueRecord] = try db.query(
            "SELECT platform,collection_id,code FROM ios_music_library_status_issues WHERE scope=? ORDER BY position",
            [.text(scope)]
        ) { c in
            MusicLibraryIssueRecord(
                platform: try c.optionalText(0),
                collectionID: try c.optionalText(1),
                code: try c.text(2)
            )
        }

        var songsByCollection: [String: [String]] = [:]
        try db.scan(
            "SELECT collection_id,song_id FROM music_collection_songs WHERE scope=? ORDER BY collection_id,position",
            [.text(scope)]
        ) { c in
            songsByCollection[try c.text(0), default: []].append(try c.text(1))
        }

        struct BindingKey: Hashable { let collectionID: String; let index: Int }
        var baselineByBinding: [BindingKey: [String]] = [:]
        try db.scan(
            "SELECT collection_id,binding_index,native_id FROM music_collection_binding_baseline " +
            "WHERE scope=? ORDER BY collection_id,binding_index,position",
            [.text(scope)]
        ) { c in
            let key = BindingKey(collectionID: try c.text(0), index: try c.integer(1))
            baselineByBinding[key, default: []].append(try c.text(2))
        }

        var mapByBinding: [BindingKey: [MusicCollectionBindingMapEntry]] = [:]
        try db.scan(
            "SELECT collection_id,binding_index,remote_entry_id,song_id FROM music_collection_binding_map " +
            "WHERE scope=? ORDER BY collection_id,binding_index,remote_entry_id",
            [.text(scope)]
        ) { c in
            let key = BindingKey(collectionID: try c.text(0), index: try c.integer(1))
            mapByBinding[key, default: []].append(MusicCollectionBindingMapEntry(
                remoteEntryID: try c.text(2),
                songID: try c.text(3)
            ))
        }

        var bindingsByCollection: [String: [MusicCollectionBindingRecord]] = [:]
        try db.scan(
            "SELECT collection_id,binding_index,platform,account_id,remote_id,writable,name " +
            "FROM music_collection_bindings WHERE scope=? ORDER BY collection_id,binding_index",
            [.text(scope)]
        ) { c in
            let collectionID = try c.text(0)
            let index = try c.integer(1)
            let key = BindingKey(collectionID: collectionID, index: index)
            bindingsByCollection[collectionID, default: []].append(MusicCollectionBindingRecord(
                platform: try c.text(2),
                accountID: try c.text(3),
                remoteID: try c.text(4),
                writable: try c.bool(5),
                name: try c.optionalText(6),
                baseline: baselineByBinding[key] ?? [],
                map: mapByBinding[key] ?? []
            ))
        }

        struct IntentKey: Hashable { let collectionID: String; let intentKey: String }
        var acksByIntent: [IntentKey: [String]] = [:]
        try db.scan(
            "SELECT collection_id,intent_key,ack_value FROM music_collection_intent_acks " +
            "WHERE scope=? ORDER BY collection_id,intent_key,position",
            [.text(scope)]
        ) { c in
            let key = IntentKey(collectionID: try c.text(0), intentKey: try c.text(1))
            acksByIntent[key, default: []].append(try c.text(2))
        }

        var intentsByCollection: [String: [MusicCollectionIntentRecord]] = [:]
        try db.scan(
            "SELECT collection_id,intent_key,present,pending,origin FROM music_collection_intents " +
            "WHERE scope=? ORDER BY collection_id,intent_key",
            [.text(scope)]
        ) { c in
            let collectionID = try c.text(0)
            let intentKey = try c.text(1)
            intentsByCollection[collectionID, default: []].append(MusicCollectionIntentRecord(
                intentKey: intentKey,
                present: try c.bool(2),
                pending: try c.bool(3),
                origin: try c.optionalText(4),
                acknowledged: acksByIntent[IntentKey(collectionID: collectionID, intentKey: intentKey)] ?? []
            ))
        }

        let collections: [MusicCollectionRecord] = try db.query(
            "SELECT collection_id,name,kind,revision,manual_order,deleted,locally_created " +
            "FROM music_collections WHERE scope=? ORDER BY position",
            [.text(scope)]
        ) { c in
            let cid = try c.text(0)
            return MusicCollectionRecord(
                id: cid,
                name: try c.text(1),
                kind: try c.text(2),
                order: songsByCollection[cid] ?? [],
                revision: try c.integer(3),
                manualOrder: try c.bool(4),
                deleted: try c.bool(5),
                locallyCreated: try c.bool(6),
                bindings: bindingsByCollection[cid] ?? [],
                intents: intentsByCollection[cid] ?? []
            )
        }

        let networkReferences: [MusicNetworkReferenceRecord] = try db.query(
            "SELECT ref_id,platform,remote_id,name,creator,artwork,song_count,revision,present,pending," +
            "rel_account_id,rel_baseline,rel_relation_id,rel_locator " +
            "FROM ios_music_network_references WHERE scope=? ORDER BY position",
            [.text(scope)]
        ) { c in
            let relation: MusicNetworkReferenceRelationRecord?
            if let accountID = try c.optionalText(10) {
                relation = MusicNetworkReferenceRelationRecord(
                    accountID: accountID,
                    baseline: try c.optionalBool(11) ?? false,
                    relationID: try c.optionalText(12) ?? "",
                    locator: try c.optionalText(13)
                )
            } else {
                relation = nil
            }
            return MusicNetworkReferenceRecord(
                id: try c.text(0),
                platform: try c.text(1),
                remoteID: try c.text(2),
                name: try c.text(3),
                creator: try c.text(4),
                artwork: try c.text(5),
                count: try c.integer(6),
                revision: try c.integer(7),
                present: try c.bool(8),
                pending: try c.bool(9),
                relation: relation
            )
        }

        let sync = try MusicSyncRepository.read(db, scope: scope)

        return MusicLibraryState(
            revision: state.revision,
            ownerID: state.ownerID,
            libraryRevision: state.libraryRevision,
            nextID: state.nextID,
            accounts: accounts,
            statusPhase: status?.phase ?? "idle",
            statusAtMs: status?.atMs ?? 0,
            statusCode: status?.code,
            statusIssues: statusIssues,
            collections: collections,
            networkReferences: networkReferences,
            sync: sync
        )
    }

    static func applyChanges(_ db: SQLiteConnection,
                             scope: String,
                             previous: MusicLibraryState?,
                             next: MusicLibraryState) throws {
        let ownerID = next.ownerID.isEmpty ? scope : next.ownerID
        let nextID = max(1, next.nextID)
        if previous == nil ||
            previous?.revision != next.revision ||
            previous?.ownerID != ownerID ||
            previous?.libraryRevision != next.libraryRevision ||
            previous?.nextID != nextID {
            try db.upsert(
                "music_library_state",
                columns: ["scope", "revision", "owner_id", "library_revision", "next_id"],
                values: [
                    .text(scope),
                    .integer(next.revision),
                    .text(ownerID),
                    .integer(next.libraryRevision),
                    .integer(nextID)
                ],
                key: ["scope"]
            )
        }

        let phase = next.statusPhase.isEmpty ? "idle" : next.statusPhase
        let statusCode = next.statusCode.flatMap { $0.isEmpty ? nil : $0 }
        if previous == nil ||
            previous?.statusPhase != phase ||
            previous?.statusAtMs != next.statusAtMs ||
            previous?.statusCode != statusCode {
            try db.upsert(
                "ios_music_library_status",
                columns: ["scope", "status_phase", "status_at_ms", "status_code"],
                values: [
                    .text(scope),
                    .text(phase),
                    .integer(next.statusAtMs),
                    .optional(statusCode)
                ],
                key: ["scope"]
            )
        }

        if previous == nil || previous?.accounts != next.accounts {
            let prevAccounts = Dictionary(uniqueKeysWithValues: (previous?.accounts ?? []).map { ($0.platform, $0.accountID) })
            let nextAccounts = Dictionary(uniqueKeysWithValues: next.accounts.map { ($0.platform, $0.accountID) })
            for platform in prevAccounts.keys where nextAccounts[platform] == nil {
                try db.execute("DELETE FROM music_library_accounts WHERE scope=? AND platform=?", [.text(scope), .text(platform)])
            }
            for account in next.accounts where prevAccounts[account.platform] != account.accountID {
                try db.upsert(
                    "music_library_accounts",
                    columns: ["scope", "platform", "account_id"],
                    values: [.text(scope), .text(account.platform), .text(account.accountID)],
                    key: ["scope", "platform"]
                )
            }
        }

        if previous == nil || previous?.statusIssues != next.statusIssues {
            for (index, issue) in next.statusIssues.enumerated() {
                try db.upsert(
                    "ios_music_library_status_issues",
                    columns: ["scope", "position", "platform", "collection_id", "code"],
                    values: [
                        .text(scope), .integer(index),
                        .optional(issue.platform.flatMap { $0.isEmpty ? nil : $0 }),
                        .optional(issue.collectionID.flatMap { $0.isEmpty ? nil : $0 }),
                        .text(issue.code)
                    ],
                    key: ["scope", "position"]
                )
            }
            if previous != nil {
                try db.execute(
                    "DELETE FROM ios_music_library_status_issues WHERE scope=? AND position>=?",
                    [.text(scope), .integer(next.statusIssues.count)]
                )
            }
        }

        if previous == nil || previous?.collections != next.collections {
            var prevColsByID: [String: (pos: Int, col: MusicCollectionRecord)] = [:]
            if let prevCols = previous?.collections {
                for (index, col) in prevCols.enumerated() {
                    prevColsByID[col.id] = (index, col)
                }
            }
            let nextColIDs = Set(next.collections.map(\.id))
            for oldCID in prevColsByID.keys where !nextColIDs.contains(oldCID) {
                try db.execute("DELETE FROM music_collections WHERE scope=? AND collection_id=?", [.text(scope), .text(oldCID)])
            }

            for (index, col) in next.collections.enumerated() {
                let old = prevColsByID[col.id]
                if let old, old.pos == index, old.col == col { continue }

                try db.upsert(
                    "music_collections",
                    columns: [
                        "scope", "collection_id", "position", "name", "kind",
                        "revision", "manual_order", "deleted", "locally_created"
                    ],
                    values: [
                        .text(scope), .text(col.id), .integer(index),
                        .text(col.name), .text(col.kind),
                        .integer(col.revision), .bool(col.manualOrder),
                        .bool(col.deleted), .bool(col.locallyCreated)
                    ],
                    key: ["scope", "collection_id"]
                )

                if old == nil || old!.col.order != col.order {
                    // music_collection_songs enforces UNIQUE(scope, collection_id, song_id);
                    // delete from the first diverging position before inserting so reorders,
                    // prepends, and non-tail removals never collide with an old position row.
                    let prevOrder = old?.col.order ?? []
                    var prefix = 0
                    while prefix < prevOrder.count && prefix < col.order.count && prevOrder[prefix] == col.order[prefix] {
                        prefix += 1
                    }
                    if old != nil && prefix < prevOrder.count {
                        try db.execute(
                            "DELETE FROM music_collection_songs WHERE scope=? AND collection_id=? AND position>=?",
                            [.text(scope), .text(col.id), .integer(prefix)]
                        )
                    }
                    for songPos in prefix..<col.order.count {
                        try db.execute(
                            "INSERT INTO music_collection_songs(scope,collection_id,position,song_id) VALUES(?,?,?,?)",
                            [.text(scope), .text(col.id), .integer(songPos), .text(col.order[songPos])]
                        )
                    }
                }

                if old == nil || old!.col.bindings != col.bindings {
                    let prevBindings = old?.col.bindings ?? []
                    for (bIndex, binding) in col.bindings.enumerated() {
                        let prevBinding = bIndex < prevBindings.count ? prevBindings[bIndex] : nil
                        if prevBinding == binding { continue }
                        try db.upsert(
                            "music_collection_bindings",
                            columns: ["scope", "collection_id", "binding_index", "platform", "account_id", "remote_id", "writable", "name"],
                            values: [
                                .text(scope), .text(col.id), .integer(bIndex),
                                .text(binding.platform), .text(binding.accountID),
                                .text(binding.remoteID), .bool(binding.writable),
                                .optional(binding.name)
                            ],
                            key: ["scope", "collection_id", "binding_index"]
                        )

                        if prevBinding == nil || prevBinding!.baseline != binding.baseline {
                            for (pos, nativeID) in binding.baseline.enumerated() {
                                try db.upsert(
                                    "music_collection_binding_baseline",
                                    columns: ["scope", "collection_id", "binding_index", "position", "native_id"],
                                    values: [.text(scope), .text(col.id), .integer(bIndex), .integer(pos), .text(nativeID)],
                                    key: ["scope", "collection_id", "binding_index", "position"]
                                )
                            }
                            if prevBinding != nil {
                                try db.execute(
                                    "DELETE FROM music_collection_binding_baseline WHERE scope=? AND collection_id=? AND binding_index=? AND position>=?",
                                    [.text(scope), .text(col.id), .integer(bIndex), .integer(binding.baseline.count)]
                                )
                            }
                        }

                        if prevBinding == nil || prevBinding!.map != binding.map {
                            let prevMap = Dictionary(uniqueKeysWithValues: (prevBinding?.map ?? []).map { ($0.remoteEntryID, $0.songID) })
                            let nextEntries = binding.map.filter { !$0.songID.isEmpty }
                            let nextKeys = Set(nextEntries.map(\.remoteEntryID))
                            for oldRemoteID in prevMap.keys where !nextKeys.contains(oldRemoteID) {
                                try db.execute(
                                    "DELETE FROM music_collection_binding_map WHERE scope=? AND collection_id=? AND binding_index=? AND remote_entry_id=?",
                                    [.text(scope), .text(col.id), .integer(bIndex), .text(oldRemoteID)]
                                )
                            }
                            for entry in nextEntries where prevMap[entry.remoteEntryID] != entry.songID {
                                try db.upsert(
                                    "music_collection_binding_map",
                                    columns: ["scope", "collection_id", "binding_index", "song_id", "remote_entry_id"],
                                    values: [.text(scope), .text(col.id), .integer(bIndex), .text(entry.songID), .text(entry.remoteEntryID)],
                                    key: ["scope", "collection_id", "binding_index", "remote_entry_id"]
                                )
                            }
                        }
                    }
                    if old != nil && col.bindings.count < prevBindings.count {
                        try db.execute(
                            "DELETE FROM music_collection_bindings WHERE scope=? AND collection_id=? AND binding_index>=?",
                            [.text(scope), .text(col.id), .integer(col.bindings.count)]
                        )
                    }
                }

                if old == nil || old!.col.intents != col.intents {
                    let prevIntents = Dictionary(uniqueKeysWithValues: (old?.col.intents ?? []).map { ($0.intentKey, $0) })
                    let nextIntentKeys = Set(col.intents.map(\.intentKey))
                    for oldKey in prevIntents.keys where !nextIntentKeys.contains(oldKey) {
                        try db.execute(
                            "DELETE FROM music_collection_intents WHERE scope=? AND collection_id=? AND intent_key=?",
                            [.text(scope), .text(col.id), .text(oldKey)]
                        )
                    }
                    for intent in col.intents {
                        let oldIntent = prevIntents[intent.intentKey]
                        if oldIntent == intent { continue }
                        try db.upsert(
                            "music_collection_intents",
                            columns: ["scope", "collection_id", "intent_key", "present", "pending", "origin"],
                            values: [
                                .text(scope), .text(col.id), .text(intent.intentKey),
                                .bool(intent.present), .bool(intent.pending),
                                .optional(intent.origin)
                            ],
                            key: ["scope", "collection_id", "intent_key"]
                        )
                        if oldIntent == nil || oldIntent!.acknowledged != intent.acknowledged {
                            for (pos, ack) in intent.acknowledged.enumerated() {
                                try db.upsert(
                                    "music_collection_intent_acks",
                                    columns: ["scope", "collection_id", "intent_key", "position", "ack_value"],
                                    values: [.text(scope), .text(col.id), .text(intent.intentKey), .integer(pos), .text(ack)],
                                    key: ["scope", "collection_id", "intent_key", "position"]
                                )
                            }
                            if oldIntent != nil {
                                try db.execute(
                                    "DELETE FROM music_collection_intent_acks WHERE scope=? AND collection_id=? AND intent_key=? AND position>=?",
                                    [.text(scope), .text(col.id), .text(intent.intentKey), .integer(intent.acknowledged.count)]
                                )
                            }
                        }
                    }
                }
            }
        }

        if previous == nil || previous?.networkReferences != next.networkReferences {
            var prevRefsByID: [String: (pos: Int, ref: MusicNetworkReferenceRecord)] = [:]
            if let prevRefs = previous?.networkReferences {
                for (index, ref) in prevRefs.enumerated() {
                    prevRefsByID[ref.id] = (index, ref)
                }
            }
            let nextRefIDs = Set(next.networkReferences.map(\.id))
            for oldID in prevRefsByID.keys where !nextRefIDs.contains(oldID) {
                try db.execute("DELETE FROM ios_music_network_references WHERE scope=? AND ref_id=?", [.text(scope), .text(oldID)])
            }
            for (index, ref) in next.networkReferences.enumerated() {
                if let old = prevRefsByID[ref.id], old.pos == index, old.ref == ref { continue }
                try db.upsert(
                    "ios_music_network_references",
                    columns: [
                        "scope", "ref_id", "position", "platform", "remote_id", "name", "creator", "artwork",
                        "song_count", "revision", "present", "pending",
                        "rel_account_id", "rel_baseline", "rel_relation_id", "rel_locator"
                    ],
                    values: [
                        .text(scope), .text(ref.id), .integer(index),
                        .text(ref.platform), .text(ref.remoteID),
                        .text(ref.name), .text(ref.creator),
                        .text(ref.artwork), .integer(ref.count),
                        .integer(ref.revision), .bool(ref.present),
                        .bool(ref.pending),
                        .optional(ref.relation?.accountID),
                        .optional(ref.relation?.baseline),
                        .optional(ref.relation?.relationID),
                        .optional(ref.relation?.locator)
                    ],
                    key: ["scope", "ref_id"]
                )
            }
        }

        if previous == nil || previous?.sync != next.sync {
            try MusicSyncRepository.applyChanges(db, scope: scope, previous: previous?.sync, next: next.sync)
        }
    }
}
