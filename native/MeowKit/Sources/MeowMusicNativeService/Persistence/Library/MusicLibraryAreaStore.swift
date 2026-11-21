import Foundation
import MeowStorage

enum MusicLibraryAreaStore {
    static func read(_ db: SQLiteConnection, scope: String) throws -> MJ? {
        guard let state = try db.query(
            "SELECT revision,owner_id,library_revision,next_id,status_phase,status_at,status_code " +
            "FROM music_library_state WHERE scope=?",
            [.text(scope)]
        ).first else { return nil }

        let accountFields: [(String, MJ)] = try db.query(
            "SELECT platform,account_id FROM music_library_accounts WHERE scope=? ORDER BY platform",
            [.text(scope)]
        ).map { (try $0.text("platform"), .string(try $0.text("account_id"))) }

        let statusIssues: [MJ] = try db.query(
            "SELECT platform,collection_id,code FROM music_library_status_issues WHERE scope=? ORDER BY position",
            [.text(scope)]
        ).map { row in
            var fields: [(String, MJ)] = []
            if let platform = try row.optionalText("platform") { fields.append(("platform", .string(platform))) }
            if let collectionID = try row.optionalText("collection_id") {
                fields.append(("collectionID", .string(collectionID)))
            }
            fields.append(("code", .string(try row.text("code"))))
            return .obj(fields)
        }

        var statusFields: [(String, MJ)] = [
            ("phase", .string(try state.text("status_phase"))),
            ("at", .number(try state.double("status_at"))),
            ("issues", .array(statusIssues))
        ]
        if let code = try state.optionalText("status_code") {
            statusFields.append(("code", .string(code)))
        }

        let collectionRows = try db.query(
            "SELECT collection_id,name,kind,revision,manual_order,deleted,locally_created " +
            "FROM music_collections WHERE scope=? ORDER BY position",
            [.text(scope)]
        )

        var songsByCollection: [String: [String]] = [:]
        for row in try db.query(
            "SELECT collection_id,song_id FROM music_collection_songs WHERE scope=? ORDER BY collection_id,position",
            [.text(scope)]
        ) {
            songsByCollection[try row.text("collection_id"), default: []].append(try row.text("song_id"))
        }

        struct BindingKey: Hashable {
            let collectionID: String
            let index: Int
        }
        var baselineByBinding: [BindingKey: [String]] = [:]
        for row in try db.query(
            "SELECT collection_id,binding_index,native_id FROM music_collection_binding_baseline " +
            "WHERE scope=? ORDER BY collection_id,binding_index,position",
            [.text(scope)]
        ) {
            let key = BindingKey(collectionID: try row.text("collection_id"), index: try row.integer("binding_index"))
            baselineByBinding[key, default: []].append(try row.text("native_id"))
        }

        var mapByBinding: [BindingKey: [(String, MJ)]] = [:]
        for row in try db.query(
            "SELECT collection_id,binding_index,remote_entry_id,song_id FROM music_collection_binding_map " +
            "WHERE scope=? ORDER BY collection_id,binding_index,remote_entry_id",
            [.text(scope)]
        ) {
            let key = BindingKey(collectionID: try row.text("collection_id"), index: try row.integer("binding_index"))
            mapByBinding[key, default: []].append((try row.text("remote_entry_id"), .string(try row.text("song_id"))))
        }

        var bindingsByCollection: [String: [MJ]] = [:]
        for row in try db.query(
            "SELECT collection_id,binding_index,platform,account_id,remote_id,writable,name " +
            "FROM music_collection_bindings WHERE scope=? ORDER BY collection_id,binding_index",
            [.text(scope)]
        ) {
            let collectionID = try row.text("collection_id")
            let index = try row.integer("binding_index")
            let key = BindingKey(collectionID: collectionID, index: index)
            var fields: [(String, MJ)] = [
                ("platform", .string(try row.text("platform"))),
                ("accountId", .string(try row.text("account_id"))),
                ("id", .string(try row.text("remote_id"))),
                ("writable", .bool(try row.bool("writable"))),
                ("baseline", .strings(baselineByBinding[key] ?? [])),
                ("map", .obj(mapByBinding[key] ?? []))
            ]
            if let name = try row.optionalText("name") {
                fields.append(("name", .string(name)))
            }
            bindingsByCollection[collectionID, default: []].append(.obj(fields))
        }

        struct IntentKey: Hashable {
            let collectionID: String
            let intentKey: String
        }
        var acksByIntent: [IntentKey: [String]] = [:]
        for row in try db.query(
            "SELECT collection_id,intent_key,ack_value FROM music_collection_intent_acks " +
            "WHERE scope=? ORDER BY collection_id,intent_key,position",
            [.text(scope)]
        ) {
            let key = IntentKey(collectionID: try row.text("collection_id"), intentKey: try row.text("intent_key"))
            acksByIntent[key, default: []].append(try row.text("ack_value"))
        }

        var intentsByCollection: [String: [(String, MJ)]] = [:]
        for row in try db.query(
            "SELECT collection_id,intent_key,present,pending,origin FROM music_collection_intents " +
            "WHERE scope=? ORDER BY collection_id,intent_key",
            [.text(scope)]
        ) {
            let collectionID = try row.text("collection_id")
            let intentKey = try row.text("intent_key")
            var fields: [(String, MJ)] = [
                ("present", .bool(try row.bool("present"))),
                ("pending", .bool(try row.bool("pending")))
            ]
            if let origin = try row.optionalText("origin") {
                fields.append(("origin", .string(origin)))
            }
            if let acks = acksByIntent[IntentKey(collectionID: collectionID, intentKey: intentKey)], !acks.isEmpty {
                fields.append(("acknowledged", .strings(acks)))
            }
            intentsByCollection[collectionID, default: []].append((intentKey, .obj(fields)))
        }

        var collections: [MJ] = []
        collections.reserveCapacity(collectionRows.count)
        for row in collectionRows {
            let cid = try row.text("collection_id")
            var fields: [(String, MJ)] = [
                ("id", .string(cid)),
                ("name", .string(try row.text("name"))),
                ("kind", .string(try row.text("kind"))),
                ("order", .strings(songsByCollection[cid] ?? [])),
                ("revision", .n(try row.integer("revision"))),
                ("manualOrder", .bool(try row.bool("manual_order"))),
                ("bindings", .array(bindingsByCollection[cid] ?? [])),
                ("intents", .obj(intentsByCollection[cid] ?? []))
            ]
            if try row.bool("deleted") { fields.append(("deleted", .bool(true))) }
            if try row.bool("locally_created") { fields.append(("locallyCreated", .bool(true))) }
            collections.append(.obj(fields))
        }

        let networkReferences: [MJ] = try db.query(
            "SELECT ref_id,platform,remote_id,name,creator,artwork,song_count,revision,present,pending," +
            "rel_account_id,rel_baseline,rel_relation_id,rel_locator " +
            "FROM ios_music_network_references WHERE scope=? ORDER BY position",
            [.text(scope)]
        ).map { row in
            let relation: MJ
            if let accountID = try row.optionalText("rel_account_id") {
                var relFields: [(String, MJ)] = [
                    ("accountId", .string(accountID)),
                    ("baseline", .bool(try row.optionalBool("rel_baseline") ?? false)),
                    ("relationID", .string(try row.optionalText("rel_relation_id") ?? ""))
                ]
                if let locator = try row.optionalText("rel_locator") {
                    relFields.append(("locator", .string(locator)))
                }
                relation = .obj(relFields)
            } else {
                relation = .null
            }
            return .obj([
                ("id", .string(try row.text("ref_id"))),
                ("platform", .string(try row.text("platform"))),
                ("remoteID", .string(try row.text("remote_id"))),
                ("name", .string(try row.text("name"))),
                ("creator", .string(try row.text("creator"))),
                ("artwork", .string(try row.text("artwork"))),
                ("count", .n(try row.integer("song_count"))),
                ("revision", .n(try row.integer("revision"))),
                ("present", .bool(try row.bool("present"))),
                ("pending", .bool(try row.bool("pending"))),
                ("relation", relation)
            ])
        }

        let innerLibrary: MJ = .obj([
            ("version", .n(1)),
            ("ownerId", .string(try state.text("owner_id"))),
            ("revision", .n(try state.integer("library_revision"))),
            ("nextId", .n(try state.integer("next_id"))),
            ("collections", .array(collections)),
            ("networkReferences", .array(networkReferences)),
            ("accounts", .obj(accountFields)),
            ("status", .obj(statusFields))
        ])

        var areaFields: [(String, MJ)] = [
            ("schema", .n(1)),
            ("scope", .string(scope)),
            ("revision", .n(try state.integer("revision"))),
            ("library", innerLibrary)
        ]
        if let sync = try readSync(db, scope: scope) {
            areaFields.append(("sync", sync))
        }
        return .obj(areaFields)
    }

    static func write(_ db: SQLiteConnection, scope: String, value: MJ) throws {
        let lib = value["library"]
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

        try db.execute("DELETE FROM music_library_accounts WHERE scope=?", [.text(scope)])
        for (platform, accountID) in lib["accounts"].fields where accountID.isString {
            try db.execute(
                "INSERT INTO music_library_accounts(scope,platform,account_id) VALUES(?,?,?)",
                [.text(scope), .text(platform), .text(accountID.string)]
            )
        }

        try db.execute("DELETE FROM music_library_status_issues WHERE scope=?", [.text(scope)])
        for (index, issue) in status["issues"].array.enumerated() {
            let colID = issue["collectionID"].isString ? issue["collectionID"] : issue["collectionId"]
            try db.execute(
                "INSERT INTO music_library_status_issues(scope,position,platform,collection_id,code) VALUES(?,?,?,?,?)",
                [
                    .text(scope), .integer(index),
                    MusicPersistenceHelpers.optionalNonEmpty(issue["platform"]),
                    MusicPersistenceHelpers.optionalNonEmpty(colID),
                    .text(issue["code"].string)
                ]
            )
        }

        // Cascades to music_collection_songs, _bindings, _binding_baseline, _binding_map, _intents, _intent_acks.
        try db.execute("DELETE FROM music_collections WHERE scope=?", [.text(scope)])
        for (index, col) in lib["collections"].array.enumerated() {
            let cid = col["id"].string
            try db.execute(
                "INSERT INTO music_collections(" +
                "scope,collection_id,position,name,kind,revision,manual_order,deleted,locally_created" +
                ") VALUES(?,?,?,?,?,?,?,?,?)",
                [
                    .text(scope), .text(cid), .integer(index),
                    .text(col["name"].string), .text(col["kind"].string),
                    .integer(col["revision"].int), .bool(col["manualOrder"].bool),
                    .bool(col["deleted"].bool), .bool(col["locallyCreated"].bool)
                ]
            )
            for (songPos, songID) in col["order"].strings.enumerated() {
                try db.execute(
                    "INSERT INTO music_collection_songs(scope,collection_id,position,song_id) VALUES(?,?,?,?)",
                    [.text(scope), .text(cid), .integer(songPos), .text(songID)]
                )
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
            for (intentKey, intent) in col["intents"].fields {
                try db.execute(
                    "INSERT INTO music_collection_intents(" +
                    "scope,collection_id,intent_key,present,pending,origin" +
                    ") VALUES(?,?,?,?,?,?)",
                    [
                        .text(scope), .text(cid), .text(intentKey),
                        .bool(intent["present"].bool), .bool(intent["pending"].bool),
                        MusicPersistenceHelpers.optionalString(intent["origin"])
                    ]
                )
                for (pos, ack) in intent["acknowledged"].strings.enumerated() {
                    try db.execute(
                        "INSERT INTO music_collection_intent_acks(" +
                        "scope,collection_id,intent_key,position,ack_value" +
                        ") VALUES(?,?,?,?,?)",
                        [.text(scope), .text(cid), .text(intentKey), .integer(pos), .text(ack)]
                    )
                }
            }
        }

        try db.execute("DELETE FROM ios_music_network_references WHERE scope=?", [.text(scope)])
        for (index, ref) in lib["networkReferences"].array.enumerated() {
            let rel = ref["relation"]
            let hasRelation = rel.isObject && rel["accountId"].isString
            try db.execute(
                "INSERT INTO ios_music_network_references(" +
                "scope,ref_id,position,platform,remote_id,name,creator,artwork,song_count,revision," +
                "present,pending,rel_account_id,rel_baseline,rel_relation_id,rel_locator" +
                ") VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)",
                [
                    .text(scope), .text(ref["id"].string), .integer(index),
                    .text(ref["platform"].string), .text(ref["remoteID"].string),
                    .text(ref["name"].string), .text(ref["creator"].string),
                    .text(ref["artwork"].string), .integer(ref["count"].int),
                    .integer(ref["revision"].int), .bool(ref["present"].bool),
                    .bool(ref["pending"].bool),
                    hasRelation ? .text(rel["accountId"].string) : .null,
                    hasRelation ? .bool(rel["baseline"].bool) : .null,
                    hasRelation ? MusicPersistenceHelpers.optionalString(rel["relationID"]) : .null,
                    hasRelation ? MusicPersistenceHelpers.optionalString(rel["locator"]) : .null
                ]
            )
        }

        try writeSync(db, scope: scope, sync: value.contains("sync") ? value["sync"] : nil)
    }

    // MARK: - iOS Music Sync State

    private static func readSync(_ db: SQLiteConnection, scope: String) throws -> MJ? {
        guard let row = try db.query(
            "SELECT phase,auto_revision,auto_intent_revision,auto_settled_intent_revision,auto_order_only " +
            "FROM ios_music_sync_state WHERE scope=?",
            [.text(scope)]
        ).first else { return nil }

        var manualSelections: [MJ] = []
        var autoSelections: [MJ] = []
        for sel in try db.query(
            "SELECT section,platform,favorites,playlists FROM ios_music_sync_selections WHERE scope=? ORDER BY section,position",
            [.text(scope)]
        ) {
            let entry: MJ = .obj([
                ("platform", .string(try sel.text("platform"))),
                ("favorites", .bool(try sel.bool("favorites"))),
                ("playlists", .bool(try sel.bool("playlists")))
            ])
            if try sel.text("section") == "manual" { manualSelections.append(entry) }
            else { autoSelections.append(entry) }
        }

        let scopes: [(String, MJ)] = try db.query(
            "SELECT platform,account_scope FROM ios_music_sync_scopes WHERE scope=? ORDER BY platform",
            [.text(scope)]
        ).map { (try $0.text("platform"), .string(try $0.text("account_scope"))) }

        let issues: [MJ] = try db.query(
            "SELECT platform,collection_id,code FROM ios_music_sync_issues WHERE scope=? ORDER BY position",
            [.text(scope)]
        ).map { item in
            var fields: [(String, MJ)] = []
            if let platform = try item.optionalText("platform") { fields.append(("platform", .string(platform))) }
            if let collectionID = try item.optionalText("collection_id") {
                fields.append(("collectionID", .string(collectionID)))
            }
            fields.append(("code", .string(try item.text("code"))))
            return .obj(fields)
        }

        let autoBindings: [(String, MJ)] = try db.query(
            "SELECT platform,account_id,binding_scope FROM ios_music_sync_auto_bindings WHERE scope=? ORDER BY platform",
            [.text(scope)]
        ).map {
            (try $0.text("platform"), .obj([
                ("accountID", .string(try $0.text("account_id"))),
                ("scope", .string(try $0.text("binding_scope")))
            ]))
        }

        let relationshipPlatforms: [String] = try db.query(
            "SELECT platform FROM ios_music_sync_relationship_platforms WHERE scope=? ORDER BY position",
            [.text(scope)]
        ).map { try $0.text("platform") }

        struct FenceMemberKey: Hashable {
            let fenceIndex: Int
            let memberSet: String
        }
        var fenceMembers: [FenceMemberKey: [MJ]] = [:]
        for member in try db.query(
            "SELECT fence_index,member_set,native_id,occurrence_id FROM ios_music_sync_fence_members " +
            "WHERE scope=? ORDER BY fence_index,member_set,position",
            [.text(scope)]
        ) {
            let key = FenceMemberKey(
                fenceIndex: try member.integer("fence_index"),
                memberSet: try member.text("member_set")
            )
            fenceMembers[key, default: []].append(.obj([
                ("nativeID", .string(try member.text("native_id"))),
                ("occurrenceID", .string(try member.optionalText("occurrence_id") ?? ""))
            ]))
        }

        let fences: [MJ] = try db.query(
            "SELECT * FROM ios_music_sync_fences WHERE scope=? ORDER BY fence_index",
            [.text(scope)]
        ).map { f in
            let idx = try f.integer("fence_index")
            var mutationFields: [(String, MJ)] = [
                ("id", .string(try f.optionalText("mut_id") ?? "")),
                ("action", .string(try f.optionalText("mut_action") ?? "")),
                ("directoryRevision", .string(try f.optionalText("mut_directory_revision") ?? "")),
                ("members", .array(fenceMembers[FenceMemberKey(fenceIndex: idx, memberSet: "mutation")] ?? []))
            ]
            if let cid = try f.optionalText("mut_collection_id") { mutationFields.append(("collectionID", .string(cid))) }
            if let name = try f.optionalText("mut_name") { mutationFields.append(("name", .string(name))) }
            if let beforeID = try f.optionalText("before_id") {
                mutationFields.append(("before", .obj([
                    ("id", .string(beforeID)),
                    ("name", .string(try f.optionalText("before_name") ?? "")),
                    ("kind", .string(try f.optionalText("before_kind") ?? "")),
                    ("writable", .bool(try f.optionalBool("before_writable") ?? false)),
                    ("canRename", .bool(try f.optionalBool("before_can_rename") ?? false)),
                    ("canDelete", .bool(try f.optionalBool("before_can_delete") ?? false)),
                    ("canOrder", .bool(try f.optionalBool("before_can_order") ?? false)),
                    ("members", .array(fenceMembers[FenceMemberKey(fenceIndex: idx, memberSet: "before")] ?? [])),
                    ("revision", .string(try f.optionalText("before_revision") ?? ""))
                ])))
            }
            if let subRemoteID = try f.optionalText("before_sub_remote_id") {
                var subFields: [(String, MJ)] = [
                    ("remoteID", .string(subRemoteID)),
                    ("relationID", .string(try f.optionalText("before_sub_relation_id") ?? "")),
                    ("name", .string(try f.optionalText("before_sub_name") ?? "")),
                    ("creator", .string(try f.optionalText("before_sub_creator") ?? "")),
                    ("artwork", .string(try f.optionalText("before_sub_artwork") ?? ""))
                ]
                if let count = try f.optionalInteger("before_sub_count") {
                    subFields.append(("count", .n(count)))
                }
                mutationFields.append(("beforeSubscription", .obj(subFields)))
            }

            var fenceFields: [(String, MJ)] = [
                ("platform", .string(try f.text("platform"))),
                ("accountID", .string(try f.text("account_id"))),
                ("scope", .string(try f.text("fence_scope"))),
                ("localID", .string(try f.optionalText("local_id") ?? "")),
                ("localRevision", .n(try f.optionalInteger("local_revision") ?? 0)),
                ("mutation", .obj(mutationFields))
            ]
            if let ackID = try f.optionalText("acknowledged_id") { fenceFields.append(("acknowledgedID", .string(ackID))) }
            if let ackName = try f.optionalText("acknowledged_name") { fenceFields.append(("acknowledgedName", .string(ackName))) }
            return .obj(fenceFields)
        }

        var autoFields: [(String, MJ)] = [
            ("revision", .n(try row.integer("auto_revision"))),
            ("selections", .array(autoSelections)),
            ("bindings", .obj(autoBindings)),
            ("intentRevision", .n(try row.integer("auto_intent_revision"))),
            ("settledIntentRevision", .n(try row.integer("auto_settled_intent_revision"))),
            ("relationshipPlatforms", .strings(relationshipPlatforms))
        ]
        if let orderOnly = try row.optionalText("auto_order_only") {
            autoFields.append(("orderOnly", .string(orderOnly)))
        }

        return .obj([
            ("schema", .n(1)),
            ("selections", .array(manualSelections)),
            ("scopes", .obj(scopes)),
            ("fences", .array(fences)),
            ("phase", .string(try row.text("phase"))),
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

        // Cascades to ios_music_sync_fence_members.
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
