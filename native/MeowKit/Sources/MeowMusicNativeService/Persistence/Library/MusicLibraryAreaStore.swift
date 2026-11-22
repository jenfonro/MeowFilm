import Foundation
import MeowStorage

enum MusicLibraryAreaStore {
    static func read(_ db: SQLiteConnection, scope: String) throws -> MJ? {
        guard let state = try db.queryOne(
            "SELECT revision,owner_id,library_revision,next_id,status_phase,status_at,status_code " +
            "FROM music_library_state WHERE scope=?",
            [.text(scope)],
            map: { c in
                (
                    revision: try c.integer(0),
                    ownerID: try c.text(1),
                    libraryRevision: try c.integer(2),
                    nextID: try c.integer(3),
                    statusPhase: try c.text(4),
                    statusAt: try c.double(5),
                    statusCode: try c.optionalText(6)
                )
            }
        ) else { return nil }

        let accountFields: [(String, MJ)] = try db.query(
            "SELECT platform,account_id FROM music_library_accounts WHERE scope=? ORDER BY platform",
            [.text(scope)]
        ) { (try $0.text(0), .string(try $0.text(1))) }

        let statusIssues: [MJ] = try db.query(
            "SELECT platform,collection_id,code FROM ios_music_library_status_issues WHERE scope=? ORDER BY position",
            [.text(scope)]
        ) { c in
            var fields: [(String, MJ)] = []
            if let platform = try c.optionalText(0) { fields.append(("platform", .string(platform))) }
            if let collectionID = try c.optionalText(1) { fields.append(("collectionID", .string(collectionID))) }
            fields.append(("code", .string(try c.text(2))))
            return .obj(fields)
        }

        var statusFields: [(String, MJ)] = [
            ("phase", .string(state.statusPhase)),
            ("at", .number(state.statusAt)),
            ("issues", .array(statusIssues))
        ]
        if let code = state.statusCode {
            statusFields.append(("code", .string(code)))
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

        var mapByBinding: [BindingKey: [(String, MJ)]] = [:]
        try db.scan(
            "SELECT collection_id,binding_index,remote_entry_id,song_id FROM music_collection_binding_map " +
            "WHERE scope=? ORDER BY collection_id,binding_index,remote_entry_id",
            [.text(scope)]
        ) { c in
            let key = BindingKey(collectionID: try c.text(0), index: try c.integer(1))
            mapByBinding[key, default: []].append((try c.text(2), .string(try c.text(3))))
        }

        var bindingsByCollection: [String: [MJ]] = [:]
        try db.scan(
            "SELECT collection_id,binding_index,platform,account_id,remote_id,writable,name " +
            "FROM music_collection_bindings WHERE scope=? ORDER BY collection_id,binding_index",
            [.text(scope)]
        ) { c in
            let collectionID = try c.text(0)
            let index = try c.integer(1)
            let key = BindingKey(collectionID: collectionID, index: index)
            var fields: [(String, MJ)] = [
                ("platform", .string(try c.text(2))),
                ("accountId", .string(try c.text(3))),
                ("id", .string(try c.text(4))),
                ("writable", .bool(try c.bool(5))),
                ("baseline", .strings(baselineByBinding[key] ?? [])),
                ("map", .obj(mapByBinding[key] ?? []))
            ]
            if let name = try c.optionalText(6) { fields.append(("name", .string(name))) }
            bindingsByCollection[collectionID, default: []].append(.obj(fields))
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

        var intentsByCollection: [String: [(String, MJ)]] = [:]
        try db.scan(
            "SELECT collection_id,intent_key,present,pending,origin FROM music_collection_intents " +
            "WHERE scope=? ORDER BY collection_id,intent_key",
            [.text(scope)]
        ) { c in
            let collectionID = try c.text(0)
            let intentKey = try c.text(1)
            var fields: [(String, MJ)] = [
                ("present", .bool(try c.bool(2))),
                ("pending", .bool(try c.bool(3)))
            ]
            if let origin = try c.optionalText(4) { fields.append(("origin", .string(origin))) }
            if let acks = acksByIntent[IntentKey(collectionID: collectionID, intentKey: intentKey)], !acks.isEmpty {
                fields.append(("acknowledged", .strings(acks)))
            }
            intentsByCollection[collectionID, default: []].append((intentKey, .obj(fields)))
        }

        let collections: [MJ] = try db.query(
            "SELECT collection_id,name,kind,revision,manual_order,deleted,locally_created " +
            "FROM music_collections WHERE scope=? ORDER BY position",
            [.text(scope)]
        ) { c in
            let cid = try c.text(0)
            var fields: [(String, MJ)] = [
                ("id", .string(cid)),
                ("name", .string(try c.text(1))),
                ("kind", .string(try c.text(2))),
                ("order", .strings(songsByCollection[cid] ?? [])),
                ("revision", .n(try c.integer(3))),
                ("manualOrder", .bool(try c.bool(4))),
                ("bindings", .array(bindingsByCollection[cid] ?? [])),
                ("intents", .obj(intentsByCollection[cid] ?? []))
            ]
            if try c.bool(5) { fields.append(("deleted", .bool(true))) }
            if try c.bool(6) { fields.append(("locallyCreated", .bool(true))) }
            return .obj(fields)
        }

        let networkReferences: [MJ] = try db.query(
            "SELECT ref_id,platform,remote_id,name,creator,artwork,song_count,revision,present,pending," +
            "rel_account_id,rel_baseline,rel_relation_id,rel_locator " +
            "FROM ios_music_network_references WHERE scope=? ORDER BY position",
            [.text(scope)]
        ) { c in
            let relation: MJ
            if let accountID = try c.optionalText(10) {
                var relFields: [(String, MJ)] = [
                    ("accountId", .string(accountID)),
                    ("baseline", .bool(try c.optionalBool(11) ?? false)),
                    ("relationID", .string(try c.optionalText(12) ?? ""))
                ]
                if let locator = try c.optionalText(13) { relFields.append(("locator", .string(locator))) }
                relation = .obj(relFields)
            } else {
                relation = .null
            }
            return .obj([
                ("id", .string(try c.text(0))),
                ("platform", .string(try c.text(1))),
                ("remoteID", .string(try c.text(2))),
                ("name", .string(try c.text(3))),
                ("creator", .string(try c.text(4))),
                ("artwork", .string(try c.text(5))),
                ("count", .n(try c.integer(6))),
                ("revision", .n(try c.integer(7))),
                ("present", .bool(try c.bool(8))),
                ("pending", .bool(try c.bool(9))),
                ("relation", relation)
            ])
        }

        let innerLibrary: MJ = .obj([
            ("version", .n(1)),
            ("ownerId", .string(state.ownerID)),
            ("revision", .n(state.libraryRevision)),
            ("nextId", .n(state.nextID)),
            ("collections", .array(collections)),
            ("networkReferences", .array(networkReferences)),
            ("accounts", .obj(accountFields)),
            ("status", .obj(statusFields))
        ])

        var areaFields: [(String, MJ)] = [
            ("schema", .n(1)),
            ("scope", .string(scope)),
            ("revision", .n(state.revision)),
            ("library", innerLibrary)
        ]
        if let sync = try readSync(db, scope: scope) {
            areaFields.append(("sync", sync))
        }
        return .obj(areaFields)
    }

    static func write(_ db: SQLiteConnection, scope: String, value: MJ) throws {
        let previous = try read(db, scope: scope)
        let lib = value["library"]
        let prevLib = previous?["library"]
        let status = lib["status"]

        try db.upsert(
            "music_library_state",
            columns: [
                "scope", "revision", "owner_id", "library_revision", "next_id",
                "status_phase", "status_at", "status_code"
            ],
            values: [
                .text(scope),
                .integer(value["revision"].int),
                .text(lib["ownerId"].string.isEmpty ? scope : lib["ownerId"].string),
                .integer(lib["revision"].int),
                .integer(max(1, lib["nextId"].int)),
                .text(status["phase"].string.isEmpty ? "idle" : status["phase"].string),
                .real(MusicPersistenceHelpers.finiteDouble(status["at"], fallback: 0)),
                MusicPersistenceHelpers.optionalNonEmpty(status["code"])
            ],
            key: ["scope"]
        )

        // Incremental diff for accounts
        if previous == nil || !MusicPersistenceHelpers.semanticEquals(lib["accounts"], prevLib?["accounts"] ?? .null) {
            let nextAccounts = Dictionary(uniqueKeysWithValues: lib["accounts"].fields.compactMap { k, v in v.isString ? (k, v.string) : nil })
            let prevAccounts = Dictionary(uniqueKeysWithValues: (prevLib?["accounts"].fields ?? []).compactMap { k, v in v.isString ? (k, v.string) : nil })
            for plat in prevAccounts.keys where nextAccounts[plat] == nil {
                try db.execute("DELETE FROM music_library_accounts WHERE scope=? AND platform=?", [.text(scope), .text(plat)])
            }
            for (plat, accID) in nextAccounts where prevAccounts[plat] != accID {
                try db.upsert("music_library_accounts", columns: ["scope", "platform", "account_id"],
                              values: [.text(scope), .text(plat), .text(accID)], key: ["scope", "platform"])
            }
        }

        // Incremental diff for status issues
        if previous == nil || !MusicPersistenceHelpers.semanticEquals(status["issues"], prevLib?["status"]["issues"] ?? .null) {
            try db.execute("DELETE FROM ios_music_library_status_issues WHERE scope=?", [.text(scope)])
            for (index, issue) in status["issues"].array.enumerated() {
                let colID = issue["collectionID"].isString ? issue["collectionID"] : issue["collectionId"]
                try db.execute(
                    "INSERT INTO ios_music_library_status_issues(scope,position,platform,collection_id,code) VALUES(?,?,?,?,?)",
                    [
                        .text(scope), .integer(index),
                        MusicPersistenceHelpers.optionalNonEmpty(issue["platform"]),
                        MusicPersistenceHelpers.optionalNonEmpty(colID),
                        .text(issue["code"].string)
                    ]
                )
            }
        }

        // Row-level incremental diff for collections (never DELETE FROM music_collections WHERE scope=?)
        var prevColsByID: [String: (pos: Int, col: MJ)] = [:]
        if let prevCols = prevLib?["collections"].array {
            for (idx, col) in prevCols.enumerated() {
                prevColsByID[col["id"].string] = (idx, col)
            }
        }
        let nextCols = lib["collections"].array
        let nextColIDs = Set(nextCols.map { $0["id"].string })
        for oldCID in prevColsByID.keys where !nextColIDs.contains(oldCID) {
            try db.execute("DELETE FROM music_collections WHERE scope=? AND collection_id=?", [.text(scope), .text(oldCID)])
        }

        for (index, col) in nextCols.enumerated() {
            let cid = col["id"].string
            let old = prevColsByID[cid]
            if let old, old.pos == index, MusicPersistenceHelpers.semanticEquals(col, old.col) {
                continue
            }

            try db.upsert(
                "music_collections",
                columns: [
                    "scope", "collection_id", "position", "name", "kind",
                    "revision", "manual_order", "deleted", "locally_created"
                ],
                values: [
                    .text(scope), .text(cid), .integer(index),
                    .text(col["name"].string), .text(col["kind"].string),
                    .integer(col["revision"].int), .bool(col["manualOrder"].bool),
                    .bool(col["deleted"].bool), .bool(col["locallyCreated"].bool)
                ],
                key: ["scope", "collection_id"]
            )

            let nextOrder = col["order"].strings
            let prevOrder = old?.col["order"].strings ?? []
            if old == nil || prevOrder != nextOrder {
                let prefix = old == nil ? 0 : MusicPersistenceHelpers.commonPrefixLength(prevOrder, nextOrder)
                if old != nil && prefix < prevOrder.count {
                    try db.execute(
                        "DELETE FROM music_collection_songs WHERE scope=? AND collection_id=? AND position>=?",
                        [.text(scope), .text(cid), .integer(prefix)]
                    )
                }
                for songPos in prefix..<nextOrder.count {
                    try db.execute(
                        "INSERT INTO music_collection_songs(scope,collection_id,position,song_id) VALUES(?,?,?,?)",
                        [.text(scope), .text(cid), .integer(songPos), .text(nextOrder[songPos])]
                    )
                }
            }

            if old == nil || !MusicPersistenceHelpers.semanticEquals(col["bindings"], old!.col["bindings"]) {
                if old != nil {
                    try db.execute("DELETE FROM music_collection_bindings WHERE scope=? AND collection_id=?", [.text(scope), .text(cid)])
                }
                for (bIndex, binding) in col["bindings"].array.enumerated() {
                    try db.execute(
                        "INSERT INTO music_collection_bindings(" +
                        "scope,collection_id,binding_index,platform,account_id,remote_id,writable,name" +
                        ") VALUES(?,?,?,?,?,?,?,?)",
                        [
                            .text(scope), .text(cid), .integer(bIndex),
                            .text(binding["platform"].string), .text(binding["accountId"].string),
                            .text(binding["id"].string), .bool(binding["writable"].bool),
                            MusicPersistenceHelpers.optionalString(binding["name"])
                        ]
                    )
                    for (pos, nativeID) in binding["baseline"].strings.enumerated() {
                        try db.execute(
                            "INSERT INTO music_collection_binding_baseline(" +
                            "scope,collection_id,binding_index,position,native_id" +
                            ") VALUES(?,?,?,?,?)",
                            [.text(scope), .text(cid), .integer(bIndex), .integer(pos), .text(nativeID)]
                        )
                    }
                    for (remoteEntryID, songVal) in binding["map"].fields where songVal.isString && !songVal.string.isEmpty {
                        try db.execute(
                            "INSERT INTO music_collection_binding_map(" +
                            "scope,collection_id,binding_index,song_id,remote_entry_id" +
                            ") VALUES(?,?,?,?,?)",
                            [.text(scope), .text(cid), .integer(bIndex), .text(songVal.string), .text(remoteEntryID)]
                        )
                    }
                }
            }

            if old == nil || !MusicPersistenceHelpers.semanticEquals(col["intents"], old!.col["intents"]) {
                let prevIntents = Dictionary(uniqueKeysWithValues: (old?.col["intents"].fields ?? []).map { ($0.0, $0.1) })
                let nextIntents = col["intents"].fields
                let nextIntentKeys = Set(nextIntents.map(\.0))
                for oldKey in prevIntents.keys where !nextIntentKeys.contains(oldKey) {
                    try db.execute(
                        "DELETE FROM music_collection_intents WHERE scope=? AND collection_id=? AND intent_key=?",
                        [.text(scope), .text(cid), .text(oldKey)]
                    )
                }
                for (intentKey, intent) in nextIntents {
                    if let oldIntent = prevIntents[intentKey], MusicPersistenceHelpers.semanticEquals(intent, oldIntent) {
                        continue
                    }
                    try db.upsert(
                        "music_collection_intents",
                        columns: ["scope", "collection_id", "intent_key", "present", "pending", "origin"],
                        values: [
                            .text(scope), .text(cid), .text(intentKey),
                            .bool(intent["present"].bool), .bool(intent["pending"].bool),
                            MusicPersistenceHelpers.optionalString(intent["origin"])
                        ],
                        key: ["scope", "collection_id", "intent_key"]
                    )
                    let nextAcks = intent["acknowledged"].strings
                    let prevAcks = prevIntents[intentKey]?["acknowledged"].strings ?? []
                    if prevIntents[intentKey] == nil || prevAcks != nextAcks {
                        try db.execute(
                            "DELETE FROM music_collection_intent_acks WHERE scope=? AND collection_id=? AND intent_key=?",
                            [.text(scope), .text(cid), .text(intentKey)]
                        )
                        for (pos, ack) in nextAcks.enumerated() {
                            try db.execute(
                                "INSERT INTO music_collection_intent_acks(" +
                                "scope,collection_id,intent_key,position,ack_value" +
                                ") VALUES(?,?,?,?,?)",
                                [.text(scope), .text(cid), .text(intentKey), .integer(pos), .text(ack)]
                            )
                        }
                    }
                }
            }
        }

        // Incremental diff for network references
        var prevRefsByID: [String: (pos: Int, ref: MJ)] = [:]
        if let prevRefs = prevLib?["networkReferences"].array {
            for (idx, ref) in prevRefs.enumerated() { prevRefsByID[ref["id"].string] = (idx, ref) }
        }
        let nextRefs = lib["networkReferences"].array
        let nextRefIDs = Set(nextRefs.map { $0["id"].string })
        for oldID in prevRefsByID.keys where !nextRefIDs.contains(oldID) {
            try db.execute("DELETE FROM ios_music_network_references WHERE scope=? AND ref_id=?", [.text(scope), .text(oldID)])
        }
        for (index, ref) in nextRefs.enumerated() {
            let refID = ref["id"].string
            if let old = prevRefsByID[refID], old.pos == index, MusicPersistenceHelpers.semanticEquals(ref, old.ref) {
                continue
            }
            let rel = ref["relation"]
            let hasRelation = rel.isObject && rel["accountId"].isString
            try db.upsert(
                "ios_music_network_references",
                columns: [
                    "scope", "ref_id", "position", "platform", "remote_id", "name", "creator", "artwork",
                    "song_count", "revision", "present", "pending",
                    "rel_account_id", "rel_baseline", "rel_relation_id", "rel_locator"
                ],
                values: [
                    .text(scope), .text(refID), .integer(index),
                    .text(ref["platform"].string), .text(ref["remoteID"].string),
                    .text(ref["name"].string), .text(ref["creator"].string),
                    .text(ref["artwork"].string), .integer(ref["count"].int),
                    .integer(ref["revision"].int), .bool(ref["present"].bool),
                    .bool(ref["pending"].bool),
                    hasRelation ? .text(rel["accountId"].string) : .null,
                    hasRelation ? .bool(rel["baseline"].bool) : .null,
                    hasRelation ? MusicPersistenceHelpers.optionalString(rel["relationID"]) : .null,
                    hasRelation ? MusicPersistenceHelpers.optionalString(rel["locator"]) : .null
                ],
                key: ["scope", "ref_id"]
            )
        }

        let nextSync: MJ? = value.contains("sync") ? value["sync"] : nil
        let prevSync: MJ? = (previous?.contains("sync") == true) ? previous?["sync"] : nil
        let syncUnchanged: Bool
        switch (nextSync, prevSync) {
        case (nil, nil): syncUnchanged = true
        case (let a?, let b?): syncUnchanged = MusicPersistenceHelpers.semanticEquals(a, b)
        default: syncUnchanged = false
        }
        if !syncUnchanged {
            try writeSync(db, scope: scope, sync: nextSync)
        }
    }

    // MARK: - iOS Music Sync State

    private static func readSync(_ db: SQLiteConnection, scope: String) throws -> MJ? {
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

        var manualSelections: [MJ] = []
        var autoSelections: [MJ] = []
        try db.scan(
            "SELECT section,platform,favorites,playlists FROM ios_music_sync_selections WHERE scope=? ORDER BY section,position",
            [.text(scope)]
        ) { sel in
            let entry: MJ = .obj([
                ("platform", .string(try sel.text(1))),
                ("favorites", .bool(try sel.bool(2))),
                ("playlists", .bool(try sel.bool(3)))
            ])
            if try sel.text(0) == "manual" { manualSelections.append(entry) }
            else { autoSelections.append(entry) }
        }

        let scopes: [(String, MJ)] = try db.query(
            "SELECT platform,account_scope FROM ios_music_sync_scopes WHERE scope=? ORDER BY platform",
            [.text(scope)]
        ) { (try $0.text(0), .string(try $0.text(1))) }

        let issues: [MJ] = try db.query(
            "SELECT platform,collection_id,code FROM ios_music_sync_issues WHERE scope=? ORDER BY position",
            [.text(scope)]
        ) { item in
            var fields: [(String, MJ)] = []
            if let platform = try item.optionalText(0) { fields.append(("platform", .string(platform))) }
            if let collectionID = try item.optionalText(1) { fields.append(("collectionID", .string(collectionID))) }
            fields.append(("code", .string(try item.text(2))))
            return .obj(fields)
        }

        let autoBindings: [(String, MJ)] = try db.query(
            "SELECT platform,account_id,binding_scope FROM ios_music_sync_auto_bindings WHERE scope=? ORDER BY platform",
            [.text(scope)]
        ) {
            (try $0.text(0), .obj([
                ("accountID", .string(try $0.text(1))),
                ("scope", .string(try $0.text(2)))
            ]))
        }

        let relationshipPlatforms: [String] = try db.query(
            "SELECT platform FROM ios_music_sync_relationship_platforms WHERE scope=? ORDER BY position",
            [.text(scope)]
        ) { try $0.text(0) }

        struct FenceMemberKey: Hashable { let fenceIndex: Int; let memberSet: String }
        var fenceMembers: [FenceMemberKey: [MJ]] = [:]
        try db.scan(
            "SELECT fence_index,member_set,native_id,occurrence_id FROM ios_music_sync_fence_members " +
            "WHERE scope=? ORDER BY fence_index,member_set,position",
            [.text(scope)]
        ) { member in
            let key = FenceMemberKey(fenceIndex: try member.integer(0), memberSet: try member.text(1))
            fenceMembers[key, default: []].append(.obj([
                ("nativeID", .string(try member.text(2))),
                ("occurrenceID", .string(try member.optionalText(3) ?? ""))
            ]))
        }

        let fences: [MJ] = try db.query(
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
            var mutationFields: [(String, MJ)] = [
                ("id", .string(try f.optionalText(8) ?? "")),
                ("action", .string(try f.optionalText(9) ?? "")),
                ("directoryRevision", .string(try f.optionalText(12) ?? "")),
                ("members", .array(fenceMembers[FenceMemberKey(fenceIndex: idx, memberSet: "mutation")] ?? []))
            ]
            if let cid = try f.optionalText(10) { mutationFields.append(("collectionID", .string(cid))) }
            if let name = try f.optionalText(11) { mutationFields.append(("name", .string(name))) }
            if let beforeID = try f.optionalText(13) {
                mutationFields.append(("before", .obj([
                    ("id", .string(beforeID)),
                    ("name", .string(try f.optionalText(14) ?? "")),
                    ("kind", .string(try f.optionalText(15) ?? "")),
                    ("writable", .bool(try f.optionalBool(16) ?? false)),
                    ("canRename", .bool(try f.optionalBool(17) ?? false)),
                    ("canDelete", .bool(try f.optionalBool(18) ?? false)),
                    ("canOrder", .bool(try f.optionalBool(19) ?? false)),
                    ("members", .array(fenceMembers[FenceMemberKey(fenceIndex: idx, memberSet: "before")] ?? [])),
                    ("revision", .string(try f.optionalText(20) ?? ""))
                ])))
            }
            if let subRemoteID = try f.optionalText(21) {
                var subFields: [(String, MJ)] = [
                    ("remoteID", .string(subRemoteID)),
                    ("relationID", .string(try f.optionalText(22) ?? "")),
                    ("name", .string(try f.optionalText(23) ?? "")),
                    ("creator", .string(try f.optionalText(24) ?? "")),
                    ("artwork", .string(try f.optionalText(25) ?? ""))
                ]
                if let count = try f.optionalInteger(26) { subFields.append(("count", .n(count))) }
                mutationFields.append(("beforeSubscription", .obj(subFields)))
            }

            var fenceFields: [(String, MJ)] = [
                ("platform", .string(try f.text(1))),
                ("accountID", .string(try f.text(2))),
                ("scope", .string(try f.text(3))),
                ("localID", .string(try f.optionalText(4) ?? "")),
                ("localRevision", .n(try f.optionalInteger(5) ?? 0)),
                ("mutation", .obj(mutationFields))
            ]
            if let ackID = try f.optionalText(6) { fenceFields.append(("acknowledgedID", .string(ackID))) }
            if let ackName = try f.optionalText(7) { fenceFields.append(("acknowledgedName", .string(ackName))) }
            return .obj(fenceFields)
        }

        var autoFields: [(String, MJ)] = [
            ("revision", .n(row.autoRev)),
            ("selections", .array(autoSelections)),
            ("bindings", .obj(autoBindings)),
            ("intentRevision", .n(row.autoIntentRev)),
            ("settledIntentRevision", .n(row.autoSettledRev)),
            ("relationshipPlatforms", .strings(relationshipPlatforms))
        ]
        if let orderOnly = row.autoOrderOnly { autoFields.append(("orderOnly", .string(orderOnly))) }

        return .obj([
            ("schema", .n(1)),
            ("selections", .array(manualSelections)),
            ("scopes", .obj(scopes)),
            ("fences", .array(fences)),
            ("phase", .string(row.phase)),
            ("issues", .array(issues)),
            ("automatic", .obj(autoFields))
        ])
    }

    private static func writeSync(_ db: SQLiteConnection, scope: String, sync: MJ?) throws {
        guard let sync, sync.isObject else {
            try db.execute("DELETE FROM ios_music_sync_state WHERE scope=?", [.text(scope)])
            return
        }
        let auto = sync["automatic"]
        try db.upsert(
            "ios_music_sync_state",
            columns: [
                "scope", "phase", "auto_revision", "auto_intent_revision",
                "auto_settled_intent_revision", "auto_order_only"
            ],
            values: [
                .text(scope),
                .text(sync["phase"].string.isEmpty ? "idle" : sync["phase"].string),
                .integer(auto["revision"].int),
                .integer(auto["intentRevision"].int),
                .integer(auto["settledIntentRevision"].int),
                MusicPersistenceHelpers.optionalNonEmpty(auto["orderOnly"])
            ],
            key: ["scope"]
        )

        try db.execute("DELETE FROM ios_music_sync_selections WHERE scope=?", [.text(scope)])
        for (section, items) in [("manual", sync["selections"].array), ("automatic", auto["selections"].array)] {
            for (index, sel) in items.enumerated() {
                try db.execute(
                    "INSERT INTO ios_music_sync_selections(scope,section,position,platform,favorites,playlists) VALUES(?,?,?,?,?,?)",
                    [
                        .text(scope), .text(section), .integer(index),
                        .text(sel["platform"].string), .bool(sel["favorites"].bool), .bool(sel["playlists"].bool)
                    ]
                )
            }
        }

        try db.execute("DELETE FROM ios_music_sync_scopes WHERE scope=?", [.text(scope)])
        for (platform, accountScope) in sync["scopes"].fields where accountScope.isString {
            try db.execute(
                "INSERT INTO ios_music_sync_scopes(scope,platform,account_scope) VALUES(?,?,?)",
                [.text(scope), .text(platform), .text(accountScope.string)]
            )
        }

        try db.execute("DELETE FROM ios_music_sync_issues WHERE scope=?", [.text(scope)])
        for (index, issue) in sync["issues"].array.enumerated() {
            let colID = issue["collectionID"].isString ? issue["collectionID"] : issue["collectionId"]
            try db.execute(
                "INSERT INTO ios_music_sync_issues(scope,position,platform,collection_id,code) VALUES(?,?,?,?,?)",
                [
                    .text(scope), .integer(index),
                    MusicPersistenceHelpers.optionalNonEmpty(issue["platform"]),
                    MusicPersistenceHelpers.optionalNonEmpty(colID),
                    .text(issue["code"].string)
                ]
            )
        }

        try db.execute("DELETE FROM ios_music_sync_auto_bindings WHERE scope=?", [.text(scope)])
        for (platform, binding) in auto["bindings"].fields where binding.isObject {
            try db.execute(
                "INSERT INTO ios_music_sync_auto_bindings(scope,platform,account_id,binding_scope) VALUES(?,?,?,?)",
                [.text(scope), .text(platform), .text(binding["accountID"].string), .text(binding["scope"].string)]
            )
        }

        try db.execute("DELETE FROM ios_music_sync_relationship_platforms WHERE scope=?", [.text(scope)])
        for (index, platform) in auto["relationshipPlatforms"].strings.enumerated() {
            try db.execute(
                "INSERT INTO ios_music_sync_relationship_platforms(scope,position,platform) VALUES(?,?,?)",
                [.text(scope), .integer(index), .text(platform)]
            )
        }

        try db.execute("DELETE FROM ios_music_sync_fences WHERE scope=?", [.text(scope)])
        for (index, fence) in sync["fences"].array.enumerated() {
            let mut = fence["mutation"]
            let before = mut["before"]
            let hasBefore = before.isObject
            let sub = mut["beforeSubscription"]
            let hasSub = sub.isObject
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
                    .text(fence["platform"].string), .text(fence["accountID"].string),
                    .text(fence["scope"].string), .text(fence["localID"].string),
                    .integer(fence["localRevision"].int),
                    MusicPersistenceHelpers.optionalString(fence["acknowledgedID"]),
                    MusicPersistenceHelpers.optionalString(fence["acknowledgedName"]),
                    MusicPersistenceHelpers.optionalString(mut["id"]),
                    MusicPersistenceHelpers.optionalString(mut["action"]),
                    MusicPersistenceHelpers.optionalString(mut["collectionID"]),
                    MusicPersistenceHelpers.optionalString(mut["name"]),
                    MusicPersistenceHelpers.optionalString(mut["directoryRevision"]),
                    hasBefore ? MusicPersistenceHelpers.optionalString(before["id"]) : .null,
                    hasBefore ? MusicPersistenceHelpers.optionalString(before["name"]) : .null,
                    hasBefore ? MusicPersistenceHelpers.optionalString(before["kind"]) : .null,
                    hasBefore ? .bool(before["writable"].bool) : .null,
                    hasBefore ? .bool(before["canRename"].bool) : .null,
                    hasBefore ? .bool(before["canDelete"].bool) : .null,
                    hasBefore ? .bool(before["canOrder"].bool) : .null,
                    hasBefore ? MusicPersistenceHelpers.optionalString(before["revision"]) : .null,
                    hasSub ? MusicPersistenceHelpers.optionalString(sub["remoteID"]) : .null,
                    hasSub ? MusicPersistenceHelpers.optionalString(sub["relationID"]) : .null,
                    hasSub ? MusicPersistenceHelpers.optionalString(sub["name"]) : .null,
                    hasSub ? MusicPersistenceHelpers.optionalString(sub["creator"]) : .null,
                    hasSub ? MusicPersistenceHelpers.optionalString(sub["artwork"]) : .null,
                    hasSub ? MusicPersistenceHelpers.optionalInt(sub["count"]) : .null
                ]
            )
            for (mPos, member) in mut["members"].array.enumerated() {
                try db.execute(
                    "INSERT INTO ios_music_sync_fence_members(scope,fence_index,member_set,position,native_id,occurrence_id) " +
                    "VALUES(?,?,?,?,?,?)",
                    [
                        .text(scope), .integer(index), .text("mutation"), .integer(mPos),
                        .text(member["nativeID"].string), MusicPersistenceHelpers.optionalString(member["occurrenceID"])
                    ]
                )
            }
            if hasBefore {
                for (mPos, member) in before["members"].array.enumerated() {
                    try db.execute(
                        "INSERT INTO ios_music_sync_fence_members(scope,fence_index,member_set,position,native_id,occurrence_id) " +
                        "VALUES(?,?,?,?,?,?)",
                        [
                            .text(scope), .integer(index), .text("before"), .integer(mPos),
                            .text(member["nativeID"].string), MusicPersistenceHelpers.optionalString(member["occurrenceID"])
                        ]
                    )
                }
            }
        }
    }
}
