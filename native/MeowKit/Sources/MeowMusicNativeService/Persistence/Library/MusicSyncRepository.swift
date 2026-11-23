import Foundation
import MeowStorage

enum MusicSyncRepository {
    static func read(_ db: SQLiteConnection, scope: String) throws -> MusicSyncStateRecord? {
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

    static func applyChanges(_ db: SQLiteConnection,
                             scope: String,
                             previous: MusicSyncStateRecord?,
                             next: MusicSyncStateRecord?) throws {
        guard let next else {
            try db.execute("DELETE FROM ios_music_sync_state WHERE scope=?", [.text(scope)])
            return
        }

        if previous == nil ||
            previous?.phase != next.phase ||
            previous?.autoRevision != next.autoRevision ||
            previous?.autoIntentRevision != next.autoIntentRevision ||
            previous?.autoSettledIntentRevision != next.autoSettledIntentRevision ||
            previous?.autoOrderOnly != next.autoOrderOnly {
            try db.upsert(
                "ios_music_sync_state",
                columns: [
                    "scope", "phase", "auto_revision", "auto_intent_revision",
                    "auto_settled_intent_revision", "auto_order_only"
                ],
                values: [
                    .text(scope),
                    .text(next.phase.isEmpty ? "idle" : next.phase),
                    .integer(next.autoRevision),
                    .integer(next.autoIntentRevision),
                    .integer(next.autoSettledIntentRevision),
                    .optional(next.autoOrderOnly.flatMap { $0.isEmpty ? nil : $0 })
                ],
                key: ["scope"]
            )
        }

        for (section, prevItems, nextItems) in [
            ("manual", previous?.manualSelections, next.manualSelections),
            ("automatic", previous?.automaticSelections, next.automaticSelections)
        ] {
            if previous != nil && prevItems == nextItems { continue }
            if previous == nil {
                try db.execute("DELETE FROM ios_music_sync_selections WHERE scope=? AND section=?", [.text(scope), .text(section)])
            }
            let nextPlatforms = Set(nextItems.map(\.platform))
            for old in prevItems ?? [] where !nextPlatforms.contains(old.platform) {
                try db.execute(
                    "DELETE FROM ios_music_sync_selections WHERE scope=? AND section=? AND platform=?",
                    [.text(scope), .text(section), .text(old.platform)]
                )
            }
            for (index, sel) in nextItems.enumerated() {
                try db.upsert(
                    "ios_music_sync_selections",
                    columns: ["scope", "section", "position", "platform", "favorites", "playlists"],
                    values: [.text(scope), .text(section), .integer(index), .text(sel.platform), .bool(sel.favorites), .bool(sel.playlists)],
                    key: ["scope", "section", "platform"]
                )
            }
        }

        if previous == nil || previous?.scopes != next.scopes {
            if previous == nil {
                try db.execute("DELETE FROM ios_music_sync_scopes WHERE scope=?", [.text(scope)])
            }
            let nextPlatforms = Set(next.scopes.map(\.platform))
            for old in previous?.scopes ?? [] where !nextPlatforms.contains(old.platform) {
                try db.execute(
                    "DELETE FROM ios_music_sync_scopes WHERE scope=? AND platform=?",
                    [.text(scope), .text(old.platform)]
                )
            }
            for item in next.scopes {
                try db.upsert(
                    "ios_music_sync_scopes",
                    columns: ["scope", "platform", "account_scope"],
                    values: [.text(scope), .text(item.platform), .text(item.accountScope)],
                    key: ["scope", "platform"]
                )
            }
        }

        if previous == nil || previous?.issues != next.issues {
            for (index, issue) in next.issues.enumerated() {
                try db.upsert(
                    "ios_music_sync_issues",
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
            try db.execute(
                "DELETE FROM ios_music_sync_issues WHERE scope=? AND position>=?",
                [.text(scope), .integer(next.issues.count)]
            )
        }

        if previous == nil || previous?.autoBindings != next.autoBindings {
            if previous == nil {
                try db.execute("DELETE FROM ios_music_sync_auto_bindings WHERE scope=?", [.text(scope)])
            }
            let nextPlatforms = Set(next.autoBindings.map(\.platform))
            for old in previous?.autoBindings ?? [] where !nextPlatforms.contains(old.platform) {
                try db.execute(
                    "DELETE FROM ios_music_sync_auto_bindings WHERE scope=? AND platform=?",
                    [.text(scope), .text(old.platform)]
                )
            }
            for binding in next.autoBindings {
                try db.upsert(
                    "ios_music_sync_auto_bindings",
                    columns: ["scope", "platform", "account_id", "binding_scope"],
                    values: [.text(scope), .text(binding.platform), .text(binding.accountID), .text(binding.bindingScope)],
                    key: ["scope", "platform"]
                )
            }
        }

        if previous == nil || previous?.relationshipPlatforms != next.relationshipPlatforms {
            for (index, platform) in next.relationshipPlatforms.enumerated() {
                try db.upsert(
                    "ios_music_sync_relationship_platforms",
                    columns: ["scope", "position", "platform"],
                    values: [.text(scope), .integer(index), .text(platform)],
                    key: ["scope", "position"]
                )
            }
            try db.execute(
                "DELETE FROM ios_music_sync_relationship_platforms WHERE scope=? AND position>=?",
                [.text(scope), .integer(next.relationshipPlatforms.count)]
            )
        }

        if previous == nil || previous?.fences != next.fences {
            let prevFences = previous?.fences ?? []
            for (index, fence) in next.fences.enumerated() {
                let prevFence = index < prevFences.count ? prevFences[index] : nil
                if prevFence == fence { continue }
                let before = fence.before
                let sub = fence.beforeSubscription
                try db.upsert(
                    "ios_music_sync_fences",
                    columns: [
                        "scope", "fence_index", "platform", "account_id", "fence_scope", "local_id", "local_revision",
                        "acknowledged_id", "acknowledged_name", "mut_id", "mut_action", "mut_collection_id", "mut_name",
                        "mut_directory_revision", "before_id", "before_name", "before_kind", "before_writable",
                        "before_can_rename", "before_can_delete", "before_can_order", "before_revision",
                        "before_sub_remote_id", "before_sub_relation_id", "before_sub_name", "before_sub_creator",
                        "before_sub_artwork", "before_sub_count"
                    ],
                    values: [
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
                    ],
                    key: ["scope", "fence_index"]
                )

                for (memberSet, nextMembers, prevMembers) in [
                    ("mutation", fence.mutationMembers, prevFence?.mutationMembers),
                    ("before", before?.members ?? [], prevFence?.before?.members)
                ] {
                    if prevFence != nil && prevMembers == nextMembers { continue }
                    for (mPos, member) in nextMembers.enumerated() {
                        try db.upsert(
                            "ios_music_sync_fence_members",
                            columns: ["scope", "fence_index", "member_set", "position", "native_id", "occurrence_id"],
                            values: [
                                .text(scope), .integer(index), .text(memberSet), .integer(mPos),
                                .text(member.nativeID), .optional(member.occurrenceID)
                            ],
                            key: ["scope", "fence_index", "member_set", "position"]
                        )
                    }
                    try db.execute(
                        "DELETE FROM ios_music_sync_fence_members WHERE scope=? AND fence_index=? AND member_set=? AND position>=?",
                        [.text(scope), .integer(index), .text(memberSet), .integer(nextMembers.count)]
                    )
                }
            }
            if previous == nil || next.fences.count < prevFences.count {
                try db.execute(
                    "DELETE FROM ios_music_sync_fences WHERE scope=? AND fence_index>=?",
                    [.text(scope), .integer(next.fences.count)]
                )
            }
        }
    }
}
