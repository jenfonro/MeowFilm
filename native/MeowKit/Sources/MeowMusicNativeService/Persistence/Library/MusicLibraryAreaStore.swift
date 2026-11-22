import Foundation
import MeowStorage

enum MusicLibraryAreaStore {
    static func read(_ db: SQLiteConnection, scope: String) throws -> MusicLibraryAreaRecord? {
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

        let sync = try readSync(db, scope: scope)

        return MusicLibraryAreaRecord(
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

    static func write(_ db: SQLiteConnection, scope: String, value: MusicLibraryAreaRecord, previous: MusicLibraryAreaRecord?) throws {
        let ownerID = value.ownerID.isEmpty ? scope : value.ownerID
        let nextID = max(1, value.nextID)
        if previous == nil ||
            previous?.revision != value.revision ||
            previous?.ownerID != ownerID ||
            previous?.libraryRevision != value.libraryRevision ||
            previous?.nextID != nextID {
            try db.upsert(
                "music_library_state",
                columns: ["scope", "revision", "owner_id", "library_revision", "next_id"],
                values: [
                    .text(scope),
                    .integer(value.revision),
                    .text(ownerID),
                    .integer(value.libraryRevision),
                    .integer(nextID)
                ],
                key: ["scope"]
            )
        }

        let phase = value.statusPhase.isEmpty ? "idle" : value.statusPhase
        let statusCode = value.statusCode.flatMap { $0.isEmpty ? nil : $0 }
        if previous == nil ||
            previous?.statusPhase != phase ||
            previous?.statusAtMs != value.statusAtMs ||
            previous?.statusCode != statusCode {
            try db.upsert(
                "ios_music_library_status",
                columns: ["scope", "status_phase", "status_at_ms", "status_code"],
                values: [
                    .text(scope),
                    .text(phase),
                    .integer(value.statusAtMs),
                    .optional(statusCode)
                ],
                key: ["scope"]
            )
        }

        if previous == nil || previous?.accounts != value.accounts {
            let prevAccounts = Dictionary(uniqueKeysWithValues: (previous?.accounts ?? []).map { ($0.platform, $0.accountID) })
            let nextAccounts = Dictionary(uniqueKeysWithValues: value.accounts.map { ($0.platform, $0.accountID) })
            for platform in prevAccounts.keys where nextAccounts[platform] == nil {
                try db.execute("DELETE FROM music_library_accounts WHERE scope=? AND platform=?", [.text(scope), .text(platform)])
            }
            for account in value.accounts where prevAccounts[account.platform] != account.accountID {
                try db.upsert(
                    "music_library_accounts",
                    columns: ["scope", "platform", "account_id"],
                    values: [.text(scope), .text(account.platform), .text(account.accountID)],
                    key: ["scope", "platform"]
                )
            }
        }

        if previous == nil || previous?.statusIssues != value.statusIssues {
            if previous != nil {
                try db.execute("DELETE FROM ios_music_library_status_issues WHERE scope=?", [.text(scope)])
            }
            for (index, issue) in value.statusIssues.enumerated() {
                try db.execute(
                    "INSERT INTO ios_music_library_status_issues(scope,position,platform,collection_id,code) VALUES(?,?,?,?,?)",
                    [
                        .text(scope), .integer(index),
                        .optional(issue.platform.flatMap { $0.isEmpty ? nil : $0 }),
                        .optional(issue.collectionID.flatMap { $0.isEmpty ? nil : $0 }),
                        .text(issue.code)
                    ]
                )
            }
        }

        if previous == nil || previous?.collections != value.collections {
            var prevColsByID: [String: (pos: Int, col: MusicCollectionRecord)] = [:]
            if let prevCols = previous?.collections {
                for (index, col) in prevCols.enumerated() {
                    prevColsByID[col.id] = (index, col)
                }
            }
            let nextColIDs = Set(value.collections.map(\.id))
            for oldCID in prevColsByID.keys where !nextColIDs.contains(oldCID) {
                try db.execute("DELETE FROM music_collections WHERE scope=? AND collection_id=?", [.text(scope), .text(oldCID)])
            }

            for (index, col) in value.collections.enumerated() {
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

                let prevOrder = old?.col.order ?? []
                if old == nil || prevOrder != col.order {
                    let prefix = old == nil ? 0 : MusicPersistenceListDiff.commonPrefixLength(prevOrder, col.order)
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
                    if old != nil {
                        try db.execute("DELETE FROM music_collection_bindings WHERE scope=? AND collection_id=?", [.text(scope), .text(col.id)])
                    }
                    for (bIndex, binding) in col.bindings.enumerated() {
                        try db.execute(
                            "INSERT INTO music_collection_bindings(" +
                            "scope,collection_id,binding_index,platform,account_id,remote_id,writable,name" +
                            ") VALUES(?,?,?,?,?,?,?,?)",
                            [
                                .text(scope), .text(col.id), .integer(bIndex),
                                .text(binding.platform), .text(binding.accountID),
                                .text(binding.remoteID), .bool(binding.writable),
                                .optional(binding.name)
                            ]
                        )
                        for (pos, nativeID) in binding.baseline.enumerated() {
                            try db.execute(
                                "INSERT INTO music_collection_binding_baseline(" +
                                "scope,collection_id,binding_index,position,native_id" +
                                ") VALUES(?,?,?,?,?)",
                                [.text(scope), .text(col.id), .integer(bIndex), .integer(pos), .text(nativeID)]
                            )
                        }
                        for entry in binding.map where !entry.songID.isEmpty {
                            try db.execute(
                                "INSERT INTO music_collection_binding_map(" +
                                "scope,collection_id,binding_index,song_id,remote_entry_id" +
                                ") VALUES(?,?,?,?,?)",
                                [.text(scope), .text(col.id), .integer(bIndex), .text(entry.songID), .text(entry.remoteEntryID)]
                            )
                        }
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
                        if let oldIntent = prevIntents[intent.intentKey], oldIntent == intent { continue }
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
                        if prevIntents[intent.intentKey] == nil || prevIntents[intent.intentKey]?.acknowledged != intent.acknowledged {
                            if prevIntents[intent.intentKey] != nil {
                                try db.execute(
                                    "DELETE FROM music_collection_intent_acks WHERE scope=? AND collection_id=? AND intent_key=?",
                                    [.text(scope), .text(col.id), .text(intent.intentKey)]
                                )
                            }
                            for (pos, ack) in intent.acknowledged.enumerated() {
                                try db.execute(
                                    "INSERT INTO music_collection_intent_acks(" +
                                    "scope,collection_id,intent_key,position,ack_value" +
                                    ") VALUES(?,?,?,?,?)",
                                    [.text(scope), .text(col.id), .text(intent.intentKey), .integer(pos), .text(ack)]
                                )
                            }
                        }
                    }
                }
            }
        }

        if previous == nil || previous?.networkReferences != value.networkReferences {
            var prevRefsByID: [String: (pos: Int, ref: MusicNetworkReferenceRecord)] = [:]
            if let prevRefs = previous?.networkReferences {
                for (index, ref) in prevRefs.enumerated() {
                    prevRefsByID[ref.id] = (index, ref)
                }
            }
            let nextRefIDs = Set(value.networkReferences.map(\.id))
            for oldID in prevRefsByID.keys where !nextRefIDs.contains(oldID) {
                try db.execute("DELETE FROM ios_music_network_references WHERE scope=? AND ref_id=?", [.text(scope), .text(oldID)])
            }
            for (index, ref) in value.networkReferences.enumerated() {
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

        if previous == nil || previous?.sync != value.sync {
            try writeSync(db, scope: scope, sync: value.sync, hadPrevious: previous?.sync != nil)
        }
    }

    // MARK: - iOS Music Sync State

    private static func readSync(_ db: SQLiteConnection, scope: String) throws -> MusicSyncStateRecord? {
        guard let row = try db.queryOne(
            "SELECT phase,auto_revision,auto_intent_revision,auto_settled_intent_revision,auto_order_only " +
            "FROM ios_music_sync_state WHERE scope=?",
            [.text(scope)],
            map: { c in
                (
                    phase: try c.text(0),
                    autoRev: try c.integer(1),
                    autoIntentRev: try c.integer(2),
                    autoSettledRev: try c.integer(3),
                    autoOrderOnly: try c.optionalText(4)
                )
            }
        ) else { return nil }

        var manualSelections: [MusicSyncSelectionRecord] = []
        var automaticSelections: [MusicSyncSelectionRecord] = []
        try db.scan(
            "SELECT section,platform,favorites,playlists FROM ios_music_sync_selections WHERE scope=? ORDER BY section,position",
            [.text(scope)]
        ) { c in
            let entry = MusicSyncSelectionRecord(
                platform: try c.text(1),
                favorites: try c.bool(2),
                playlists: try c.bool(3)
            )
            if try c.text(0) == "manual" {
                manualSelections.append(entry)
            } else {
                automaticSelections.append(entry)
            }
        }

        let scopes: [MusicSyncScopeRecord] = try db.query(
            "SELECT platform,account_scope FROM ios_music_sync_scopes WHERE scope=? ORDER BY platform",
            [.text(scope)]
        ) { c in
            MusicSyncScopeRecord(platform: try c.text(0), accountScope: try c.text(1))
        }

        let issues: [MusicLibraryIssueRecord] = try db.query(
            "SELECT platform,collection_id,code FROM ios_music_sync_issues WHERE scope=? ORDER BY position",
            [.text(scope)]
        ) { c in
            MusicLibraryIssueRecord(
                platform: try c.optionalText(0),
                collectionID: try c.optionalText(1),
                code: try c.text(2)
            )
        }

        let autoBindings: [MusicSyncAutoBindingRecord] = try db.query(
            "SELECT platform,account_id,binding_scope FROM ios_music_sync_auto_bindings WHERE scope=? ORDER BY platform",
            [.text(scope)]
        ) { c in
            MusicSyncAutoBindingRecord(
                platform: try c.text(0),
                accountID: try c.text(1),
                bindingScope: try c.text(2)
            )
        }

        let relationshipPlatforms: [String] = try db.query(
            "SELECT platform FROM ios_music_sync_relationship_platforms WHERE scope=? ORDER BY position",
            [.text(scope)]
        ) { try $0.text(0) }

        struct FenceMemberKey: Hashable { let fenceIndex: Int; let memberSet: String }
        var fenceMembers: [FenceMemberKey: [MusicSyncFenceMemberRecord]] = [:]
        try db.scan(
            "SELECT fence_index,member_set,native_id,occurrence_id FROM ios_music_sync_fence_members " +
            "WHERE scope=? ORDER BY fence_index,member_set,position",
            [.text(scope)]
        ) { c in
            let key = FenceMemberKey(fenceIndex: try c.integer(0), memberSet: try c.text(1))
            fenceMembers[key, default: []].append(MusicSyncFenceMemberRecord(
                nativeID: try c.text(2),
                occurrenceID: try c.optionalText(3)
            ))
        }

        let fences: [MusicSyncFenceRecord] = try db.query(
            "SELECT fence_index,platform,account_id,fence_scope,local_id,local_revision," +
            "acknowledged_id,acknowledged_name,mut_id,mut_action,mut_collection_id,mut_name," +
            "mut_directory_revision,before_id,before_name,before_kind,before_writable," +
            "before_can_rename,before_can_delete,before_can_order,before_revision," +
            "before_sub_remote_id,before_sub_relation_id,before_sub_name,before_sub_creator," +
            "before_sub_artwork,before_sub_count " +
            "FROM ios_music_sync_fences WHERE scope=? ORDER BY fence_index",
            [.text(scope)]
        ) { f in
            let idx = try f.integer(0)
            let before: MusicSyncFenceBeforeRecord?
            if let beforeID = try f.optionalText(13) {
                before = MusicSyncFenceBeforeRecord(
                    id: beforeID,
                    name: try f.optionalText(14) ?? "",
                    kind: try f.optionalText(15) ?? "",
                    writable: try f.optionalBool(16) ?? false,
                    canRename: try f.optionalBool(17) ?? false,
                    canDelete: try f.optionalBool(18) ?? false,
                    canOrder: try f.optionalBool(19) ?? false,
                    revision: try f.optionalText(20) ?? "",
                    members: fenceMembers[FenceMemberKey(fenceIndex: idx, memberSet: "before")] ?? []
                )
            } else {
                before = nil
            }
            let beforeSub: MusicSyncFenceBeforeSubscriptionRecord?
            if let subRemoteID = try f.optionalText(21) {
                beforeSub = MusicSyncFenceBeforeSubscriptionRecord(
                    remoteID: subRemoteID,
                    relationID: try f.optionalText(22) ?? "",
                    name: try f.optionalText(23) ?? "",
                    creator: try f.optionalText(24) ?? "",
                    artwork: try f.optionalText(25) ?? "",
                    count: try f.optionalInteger(26)
                )
            } else {
                beforeSub = nil
            }
            return MusicSyncFenceRecord(
                platform: try f.text(1),
                accountID: try f.text(2),
                fenceScope: try f.text(3),
                localID: try f.optionalText(4) ?? "",
                localRevision: try f.optionalInteger(5) ?? 0,
                acknowledgedID: try f.optionalText(6),
                acknowledgedName: try f.optionalText(7),
                mutationID: try f.optionalText(8),
                mutationAction: try f.optionalText(9),
                mutationCollectionID: try f.optionalText(10),
                mutationName: try f.optionalText(11),
                mutationDirectoryRevision: try f.optionalText(12),
                mutationMembers: fenceMembers[FenceMemberKey(fenceIndex: idx, memberSet: "mutation")] ?? [],
                before: before,
                beforeSubscription: beforeSub
            )
        }

        return MusicSyncStateRecord(
            phase: row.phase,
            autoRevision: row.autoRev,
            autoIntentRevision: row.autoIntentRev,
            autoSettledIntentRevision: row.autoSettledRev,
            autoOrderOnly: row.autoOrderOnly,
            manualSelections: manualSelections,
            automaticSelections: automaticSelections,
            scopes: scopes,
            issues: issues,
            autoBindings: autoBindings,
            relationshipPlatforms: relationshipPlatforms,
            fences: fences
        )
    }

    private static func writeSync(_ db: SQLiteConnection,
                                  scope: String,
                                  sync: MusicSyncStateRecord?,
                                  hadPrevious: Bool) throws {
        guard let sync else {
            if hadPrevious {
                try db.execute("DELETE FROM ios_music_sync_state WHERE scope=?", [.text(scope)])
            }
            return
        }

        try db.upsert(
            "ios_music_sync_state",
            columns: [
                "scope", "phase", "auto_revision", "auto_intent_revision",
                "auto_settled_intent_revision", "auto_order_only"
            ],
            values: [
                .text(scope),
                .text(sync.phase.isEmpty ? "idle" : sync.phase),
                .integer(sync.autoRevision),
                .integer(sync.autoIntentRevision),
                .integer(sync.autoSettledIntentRevision),
                .optional(sync.autoOrderOnly.flatMap { $0.isEmpty ? nil : $0 })
            ],
            key: ["scope"]
        )

        if hadPrevious {
            try db.execute("DELETE FROM ios_music_sync_selections WHERE scope=?", [.text(scope)])
            try db.execute("DELETE FROM ios_music_sync_scopes WHERE scope=?", [.text(scope)])
            try db.execute("DELETE FROM ios_music_sync_issues WHERE scope=?", [.text(scope)])
            try db.execute("DELETE FROM ios_music_sync_auto_bindings WHERE scope=?", [.text(scope)])
            try db.execute("DELETE FROM ios_music_sync_relationship_platforms WHERE scope=?", [.text(scope)])
            try db.execute("DELETE FROM ios_music_sync_fences WHERE scope=?", [.text(scope)])
        }

        for (section, items) in [("manual", sync.manualSelections), ("automatic", sync.automaticSelections)] {
            for (index, sel) in items.enumerated() {
                try db.execute(
                    "INSERT INTO ios_music_sync_selections(scope,section,position,platform,favorites,playlists) VALUES(?,?,?,?,?,?)",
                    [.text(scope), .text(section), .integer(index), .text(sel.platform), .bool(sel.favorites), .bool(sel.playlists)]
                )
            }
        }

        for item in sync.scopes {
            try db.execute(
                "INSERT INTO ios_music_sync_scopes(scope,platform,account_scope) VALUES(?,?,?)",
                [.text(scope), .text(item.platform), .text(item.accountScope)]
            )
        }

        for (index, issue) in sync.issues.enumerated() {
            try db.execute(
                "INSERT INTO ios_music_sync_issues(scope,position,platform,collection_id,code) VALUES(?,?,?,?,?)",
                [
                    .text(scope), .integer(index),
                    .optional(issue.platform.flatMap { $0.isEmpty ? nil : $0 }),
                    .optional(issue.collectionID.flatMap { $0.isEmpty ? nil : $0 }),
                    .text(issue.code)
                ]
            )
        }

        for binding in sync.autoBindings {
            try db.execute(
                "INSERT INTO ios_music_sync_auto_bindings(scope,platform,account_id,binding_scope) VALUES(?,?,?,?)",
                [.text(scope), .text(binding.platform), .text(binding.accountID), .text(binding.bindingScope)]
            )
        }

        for (index, platform) in sync.relationshipPlatforms.enumerated() {
            try db.execute(
                "INSERT INTO ios_music_sync_relationship_platforms(scope,position,platform) VALUES(?,?,?)",
                [.text(scope), .integer(index), .text(platform)]
            )
        }

        for (index, fence) in sync.fences.enumerated() {
            let before = fence.before
            let sub = fence.beforeSubscription
            try db.execute(
                "INSERT INTO ios_music_sync_fences(" +
                "scope,fence_index,platform,account_id,fence_scope,local_id,local_revision," +
                "acknowledged_id,acknowledged_name,mut_id,mut_action,mut_collection_id,mut_name," +
                "mut_directory_revision,before_id,before_name,before_kind,before_writable," +
                "before_can_rename,before_can_delete,before_can_order,before_revision," +
                "before_sub_remote_id,before_sub_relation_id,before_sub_name,before_sub_creator," +
                "before_sub_artwork,before_sub_count" +
                ") VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)",
                [
                    .text(scope), .integer(index),
                    .text(fence.platform), .text(fence.accountID),
                    .text(fence.fenceScope), .text(fence.localID),
                    .integer(fence.localRevision),
                    .optional(fence.acknowledgedID),
                    .optional(fence.acknowledgedName),
                    .optional(fence.mutationID),
                    .optional(fence.mutationAction),
                    .optional(fence.mutationCollectionID),
                    .optional(fence.mutationName),
                    .optional(fence.mutationDirectoryRevision),
                    .optional(before?.id),
                    .optional(before?.name),
                    .optional(before?.kind),
                    .optional(before?.writable),
                    .optional(before?.canRename),
                    .optional(before?.canDelete),
                    .optional(before?.canOrder),
                    .optional(before?.revision),
                    .optional(sub?.remoteID),
                    .optional(sub?.relationID),
                    .optional(sub?.name),
                    .optional(sub?.creator),
                    .optional(sub?.artwork),
                    .optional(sub?.count)
                ]
            )
            for (mPos, member) in fence.mutationMembers.enumerated() {
                try db.execute(
                    "INSERT INTO ios_music_sync_fence_members(scope,fence_index,member_set,position,native_id,occurrence_id) " +
                    "VALUES(?,?,?,?,?,?)",
                    [
                        .text(scope), .integer(index), .text("mutation"), .integer(mPos),
                        .text(member.nativeID), .optional(member.occurrenceID)
                    ]
                )
            }
            if let before {
                for (mPos, member) in before.members.enumerated() {
                    try db.execute(
                        "INSERT INTO ios_music_sync_fence_members(scope,fence_index,member_set,position,native_id,occurrence_id) " +
                        "VALUES(?,?,?,?,?,?)",
                        [
                            .text(scope), .integer(index), .text("before"), .integer(mPos),
                            .text(member.nativeID), .optional(member.occurrenceID)
                        ]
                    )
                }
            }
        }
    }
}
