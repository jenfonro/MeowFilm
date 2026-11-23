// SPDX-License-Identifier: GPL-3.0-only
import Foundation
import MeowMusicFeature
import MeowMusicPlatform

/// Durable write facts only. Live mutation tracks are request material, not a
/// second persistent song repository or an input to unknown-write recovery.
struct MusicNativeSyncMemberProof: Equatable, Sendable {
    let nativeID: String
    let occurrenceID: String
    init(nativeID: String, occurrenceID: String) {
        self.nativeID = nativeID; self.occurrenceID = occurrenceID
    }
    init(_ member: MusicLibrarySyncMember) {
        nativeID = member.nativeID; occurrenceID = member.occurrenceID
    }
}

struct MusicNativeSyncCollectionProof: Equatable, Sendable {
    let id: String
    let name: String
    let kind: MusicLibrarySyncKind
    let writable: Bool
    let canRename: Bool
    let canDelete: Bool
    let canOrder: Bool
    let members: [MusicNativeSyncMemberProof]
    let revision: String
    init(id: String, name: String, kind: MusicLibrarySyncKind, writable: Bool,
         canRename: Bool, canDelete: Bool, canOrder: Bool,
         members: [MusicNativeSyncMemberProof], revision: String) {
        self.id = id; self.name = name; self.kind = kind; self.writable = writable
        self.canRename = canRename; self.canDelete = canDelete; self.canOrder = canOrder
        self.members = members; self.revision = revision
    }
    init(_ collection: MusicLibrarySyncCollection) {
        id = collection.id; name = collection.name; kind = collection.kind; writable = collection.writable
        canRename = collection.canRename; canDelete = collection.canDelete; canOrder = collection.canOrder
        members = collection.members.map(MusicNativeSyncMemberProof.init); revision = collection.revision
    }
}

struct MusicNativeSyncWriteProof: Equatable, Sendable {
    let id: UUID
    let action: MusicLibrarySyncMutation.Action
    let collectionID: String?
    let name: String?
    let before: MusicNativeSyncCollectionProof?
    let beforeSubscription: MusicLibrarySyncSubscription?
    let directoryRevision: String
    let members: [MusicNativeSyncMemberProof]
    init(id: UUID, action: MusicLibrarySyncMutation.Action, collectionID: String?, name: String?,
         before: MusicNativeSyncCollectionProof?, beforeSubscription: MusicLibrarySyncSubscription?,
         directoryRevision: String, members: [MusicNativeSyncMemberProof]) {
        self.id = id; self.action = action; self.collectionID = collectionID; self.name = name
        self.before = before; self.beforeSubscription = beforeSubscription
        self.directoryRevision = directoryRevision; self.members = members
    }
    init(_ mutation: MusicLibrarySyncMutation) {
        id = mutation.id; action = mutation.action; collectionID = mutation.collectionID; name = mutation.name
        before = mutation.before.map(MusicNativeSyncCollectionProof.init)
        beforeSubscription = mutation.beforeSubscription; directoryRevision = mutation.directoryRevision
        members = mutation.members.map(MusicNativeSyncMemberProof.init)
    }
}

struct MusicNativeSyncFence: Equatable, Sendable {
    let platform: MusicAccountPlatform
    let accountID: String
    let scope: String
    let localID: String
    let localRevision: Int
    let mutation: MusicNativeSyncWriteProof
    var acknowledgedID: String?
    var acknowledgedName: String?

    func confirmed(in snapshot: MusicLibrarySyncSnapshot) -> Bool {
        guard snapshot.accountID == accountID else { return false }
        if mutation.action.isSubscription {
            guard let id = mutation.collectionID else { return false }
            return snapshot.subscriptions.contains(where: { $0.remoteID == id }) == (mutation.action == .subscribe)
        }
        let id = mutation.action == .create ? acknowledgedID : mutation.collectionID
        guard let id else { return false }
        let actual = snapshot.collections.first { $0.id == id }
        if mutation.action == .delete { return actual == nil }
        guard let actual, actual.writable else { return false }
        if mutation.action == .create {
            return actual.kind == .playlist && actual.name == (acknowledgedName ?? mutation.name) && actual.members.isEmpty
        }
        guard let before = mutation.before, actual.kind == before.kind,
              actual.canDelete == before.canDelete, actual.canRename == before.canRename else { return false }
        let beforeIDs = before.members.map(\.occurrenceID), actualIDs = actual.members.map(\.occurrenceID)
        switch mutation.action {
        case .rename: return actual.name == mutation.name && actualIDs == beforeIDs
        case .order:
            return actual.name == before.name && actualIDs == mutation.members.map(\.occurrenceID) &&
                actual.members.map(\.nativeID) == mutation.members.map(\.nativeID)
        case .remove:
            let removed = Set(mutation.members.map(\.nativeID))
            return actual.name == before.name &&
                actualIDs == before.members.filter { !removed.contains($0.nativeID) }.map(\.occurrenceID) &&
                actual.members.map(\.nativeID) == before.members.filter { !removed.contains($0.nativeID) }.map(\.nativeID)
        case .add:
            let old = Dictionary(uniqueKeysWithValues: before.members.map { ($0.occurrenceID, $0.nativeID) })
            let surviving = actual.members.filter { old[$0.occurrenceID] != nil }
            let added = actual.members.filter { old[$0.occurrenceID] == nil }
            return actual.name == before.name && surviving.map(\.occurrenceID) == beforeIDs &&
                surviving.allSatisfy { old[$0.occurrenceID] == $0.nativeID } &&
                added.map(\.nativeID).sorted() == mutation.members.map(\.nativeID).sorted()
        case .create, .delete, .subscribe, .unsubscribe: return false
        }
    }
}

/// Synchronization metadata in the library state, without credentials or media URLs.
struct MusicNativeSyncLedger: Equatable, Sendable {
    var selections: [MusicLibrarySyncSelection] = []
    var scopes: [String: String] = [:]
    var fences: [MusicNativeSyncFence] = []
    var phase: MusicLibrarySyncState.Phase = .idle
    var issues: [MusicLibrarySyncIssue] = []
    var automatic = MusicNativeAutomaticSyncPreferences()

    init() {}

    static func load(_ library: MusicLibraryState) throws -> Self {
        guard let record = library.sync else { return .init() }
        var value = Self()
        guard let phase = MusicLibrarySyncState.Phase(rawValue: record.phase) else {
            throw MusicServiceError.corruptStorage
        }
        value.phase = phase
        value.selections = try record.manualSelections.map { row in
            guard let platform = MusicAccountPlatform(rawValue: row.platform) else {
                throw MusicServiceError.corruptStorage
            }
            return MusicLibrarySyncSelection(platform: platform, favorites: row.favorites, playlists: row.playlists)
        }
        value.scopes = Dictionary(uniqueKeysWithValues: record.scopes.map { ($0.platform, $0.accountScope) })
        value.issues = try record.issues.map { row in
            let platform: MusicAccountPlatform?
            if let raw = row.platform {
                guard let parsed = MusicAccountPlatform(rawValue: raw) else { throw MusicServiceError.corruptStorage }
                platform = parsed
            } else {
                platform = nil
            }
            return MusicLibrarySyncIssue(platform: platform, collectionID: row.collectionID, code: row.code)
        }
        value.automatic = MusicNativeAutomaticSyncPreferences(
            revision: record.autoRevision,
            selections: try record.automaticSelections.map { row in
                guard let platform = MusicAccountPlatform(rawValue: row.platform) else {
                    throw MusicServiceError.corruptStorage
                }
                return MusicLibrarySyncSelection(platform: platform, favorites: row.favorites, playlists: row.playlists)
            },
            bindings: Dictionary(uniqueKeysWithValues: record.autoBindings.map {
                ($0.platform, MusicNativeAutomaticSyncBinding(accountID: $0.accountID, scope: $0.bindingScope))
            }),
            intentRevision: record.autoIntentRevision,
            settledIntentRevision: record.autoSettledIntentRevision,
            orderOnly: record.autoOrderOnly,
            relationshipPlatforms: try record.relationshipPlatforms.map { raw in
                guard let platform = MusicAccountPlatform(rawValue: raw) else {
                    throw MusicServiceError.corruptStorage
                }
                return platform
            }
        )
        value.fences = try record.fences.map { row in
            guard let platform = MusicAccountPlatform(rawValue: row.platform),
                  let mutIDRaw = row.mutationID, let mutID = UUID(uuidString: mutIDRaw),
                  let actionRaw = row.mutationAction, let action = MusicLibrarySyncMutation.Action(rawValue: actionRaw) else {
                throw MusicServiceError.corruptStorage
            }
            let before: MusicNativeSyncCollectionProof?
            if let b = row.before {
                guard let kind = MusicLibrarySyncKind(rawValue: b.kind) else { throw MusicServiceError.corruptStorage }
                before = MusicNativeSyncCollectionProof(
                    id: b.id, name: b.name, kind: kind, writable: b.writable,
                    canRename: b.canRename, canDelete: b.canDelete, canOrder: b.canOrder,
                    members: b.members.map { MusicNativeSyncMemberProof(nativeID: $0.nativeID, occurrenceID: $0.occurrenceID ?? "") },
                    revision: b.revision
                )
            } else {
                before = nil
            }
            let beforeSub: MusicLibrarySyncSubscription? = row.beforeSubscription.map {
                MusicLibrarySyncSubscription(
                    remoteID: $0.remoteID, relationID: $0.relationID, name: $0.name,
                    creator: $0.creator, artwork: $0.artwork, count: $0.count
                )
            }
            let proof = MusicNativeSyncWriteProof(
                id: mutID, action: action, collectionID: row.mutationCollectionID, name: row.mutationName,
                before: before, beforeSubscription: beforeSub,
                directoryRevision: row.mutationDirectoryRevision ?? "",
                members: row.mutationMembers.map { MusicNativeSyncMemberProof(nativeID: $0.nativeID, occurrenceID: $0.occurrenceID ?? "") }
            )
            return MusicNativeSyncFence(
                platform: platform, accountID: row.accountID, scope: row.fenceScope,
                localID: row.localID, localRevision: row.localRevision, mutation: proof,
                acknowledgedID: row.acknowledgedID, acknowledgedName: row.acknowledgedName
            )
        }

        guard value.selections.count <= 3,
              Set(value.selections.map(\.platform)).count == value.selections.count,
              value.scopes.count <= 3, value.scopes.keys.allSatisfy({ MusicAccountPlatform(rawValue: $0) != nil }),
              Set(value.fences.map { $0.mutation.id }).count == value.fences.count else {
            throw MusicServiceError.corruptStorage
        }
        try value.automatic.validate()
        for fence in value.fences {
            guard fence.localRevision >= 0, !fence.localID.isEmpty, !fence.accountID.isEmpty, !fence.scope.isEmpty,
                  (fence.mutation.action.isSubscription
                    ? library.networkReferences.contains(where: {
                        $0.id == fence.localID && $0.platform == fence.platform.rawValue &&
                            $0.remoteID == fence.mutation.collectionID
                    })
                    : library.collections.contains(where: { $0.id == fence.localID })),
                  library.accountID(for: fence.platform.rawValue) == fence.accountID,
                  value.scopes[fence.platform.rawValue] == fence.scope,
                  Set(fence.mutation.before?.members.map(\.occurrenceID) ?? []).count == (fence.mutation.before?.members.count ?? 0) else {
                throw MusicServiceError.corruptStorage
            }
            let mutation = fence.mutation
            if mutation.action.isSubscription {
                guard mutation.before == nil, mutation.members.isEmpty,
                      mutation.collectionID?.isEmpty == false, !mutation.directoryRevision.isEmpty,
                      fence.acknowledgedID == nil, fence.acknowledgedName == nil else { throw MusicServiceError.corruptStorage }
                if mutation.action == .subscribe {
                    guard mutation.beforeSubscription == nil, mutation.name?.isEmpty == false else { throw MusicServiceError.corruptStorage }
                } else {
                    guard let before = mutation.beforeSubscription, before.remoteID == mutation.collectionID,
                          !before.relationID.isEmpty, mutation.name == nil else { throw MusicServiceError.corruptStorage }
                }
            } else if mutation.action == .create {
                guard mutation.collectionID == nil, mutation.before == nil, mutation.members.isEmpty,
                      mutation.beforeSubscription == nil,
                      mutation.name?.isEmpty == false, !mutation.directoryRevision.isEmpty,
                      fence.acknowledgedID == nil || fence.acknowledgedID?.isEmpty == false else { throw MusicServiceError.corruptStorage }
            } else {
                guard let before = mutation.before, before.id == mutation.collectionID, !before.id.isEmpty,
                      before.writable, !before.revision.isEmpty,
                      mutation.beforeSubscription == nil,
                      fence.acknowledgedID == nil, fence.acknowledgedName == nil else { throw MusicServiceError.corruptStorage }
                switch mutation.action {
                case .create, .subscribe, .unsubscribe: break
                case .delete:
                    guard before.canDelete, mutation.members.isEmpty else { throw MusicServiceError.corruptStorage }
                case .rename:
                    guard before.canRename, mutation.name?.isEmpty == false, mutation.members.isEmpty else { throw MusicServiceError.corruptStorage }
                case .order:
                    guard before.canOrder,
                          before.members.map(\.occurrenceID).sorted() == mutation.members.map(\.occurrenceID).sorted(),
                          before.members.map(\.nativeID).sorted() == mutation.members.map(\.nativeID).sorted() else {
                        throw MusicServiceError.corruptStorage
                    }
                case .add, .remove:
                    guard (1...100).contains(mutation.members.count),
                          Set(mutation.members.map(\.nativeID)).count == mutation.members.count else { throw MusicServiceError.corruptStorage }
                }
            }
            for member in fence.mutation.members + (fence.mutation.before?.members ?? []) {
                guard !member.nativeID.isEmpty, !member.occurrenceID.isEmpty else { throw MusicServiceError.corruptStorage }
            }
        }
        return value
    }

    func toRecord() -> MusicSyncStateRecord {
        MusicSyncStateRecord(
            phase: phase.rawValue,
            autoRevision: automatic.revision,
            autoIntentRevision: automatic.intentRevision,
            autoSettledIntentRevision: automatic.settledIntentRevision,
            autoOrderOnly: automatic.orderOnly,
            manualSelections: selections.map {
                MusicSyncSelectionRecord(platform: $0.platform.rawValue, favorites: $0.favorites, playlists: $0.playlists)
            },
            automaticSelections: automatic.selections.map {
                MusicSyncSelectionRecord(platform: $0.platform.rawValue, favorites: $0.favorites, playlists: $0.playlists)
            },
            scopes: scopes.keys.sorted().map { MusicSyncScopeRecord(platform: $0, accountScope: scopes[$0]!) },
            issues: issues.map {
                MusicLibraryIssueRecord(platform: $0.platform?.rawValue, collectionID: $0.collectionID, code: $0.code)
            },
            autoBindings: automatic.bindings.keys.sorted().map {
                let b = automatic.bindings[$0]!
                return MusicSyncAutoBindingRecord(platform: $0, accountID: b.accountID, bindingScope: b.scope)
            },
            relationshipPlatforms: automatic.relationshipPlatforms.map(\.rawValue),
            fences: fences.map { fence in
                let beforeRecord: MusicSyncFenceBeforeRecord? = fence.mutation.before.map { b in
                    MusicSyncFenceBeforeRecord(
                        id: b.id, name: b.name, kind: b.kind.rawValue, writable: b.writable,
                        canRename: b.canRename, canDelete: b.canDelete, canOrder: b.canOrder,
                        revision: b.revision,
                        members: b.members.map { MusicSyncFenceMemberRecord(nativeID: $0.nativeID, occurrenceID: $0.occurrenceID) }
                    )
                }
                let subRecord: MusicSyncFenceBeforeSubscriptionRecord? = fence.mutation.beforeSubscription.map { s in
                    MusicSyncFenceBeforeSubscriptionRecord(
                        remoteID: s.remoteID, relationID: s.relationID, name: s.name,
                        creator: s.creator, artwork: s.artwork, count: s.count
                    )
                }
                return MusicSyncFenceRecord(
                    platform: fence.platform.rawValue,
                    accountID: fence.accountID,
                    fenceScope: fence.scope,
                    localID: fence.localID,
                    localRevision: fence.localRevision,
                    acknowledgedID: fence.acknowledgedID,
                    acknowledgedName: fence.acknowledgedName,
                    mutationID: fence.mutation.id.uuidString,
                    mutationAction: fence.mutation.action.rawValue,
                    mutationCollectionID: fence.mutation.collectionID,
                    mutationName: fence.mutation.name,
                    mutationDirectoryRevision: fence.mutation.directoryRevision,
                    mutationMembers: fence.mutation.members.map {
                        MusicSyncFenceMemberRecord(nativeID: $0.nativeID, occurrenceID: $0.occurrenceID)
                    },
                    before: beforeRecord,
                    beforeSubscription: subRecord
                )
            }
        )
    }
}

struct MusicNativeAutomaticSyncBinding: Equatable, Sendable {
    let accountID: String
    let scope: String
}

struct MusicNativeAutomaticSyncPreferences: Equatable, Sendable {
    var revision = 0
    var selections: [MusicLibrarySyncSelection] = []
    var bindings: [String: MusicNativeAutomaticSyncBinding] = [:]
    var intentRevision = 0
    var settledIntentRevision = 0
    var orderOnly: String?
    var relationshipPlatforms: [MusicAccountPlatform] = []

    func validate() throws {
        try musicRequire((0...musicMaxSafeInteger).contains(revision) &&
            (0...musicMaxSafeInteger).contains(intentRevision) &&
            (0...intentRevision).contains(settledIntentRevision) &&
            selections.count <= 3 && Set(selections.map(\.platform)).count == selections.count &&
            selections.allSatisfy { !$0.kinds.isEmpty } &&
            bindings.count <= 3 &&
            bindings.keys.allSatisfy { key in selections.contains { $0.platform.rawValue == key } },
            .corruptStorage)
        try musicRequire(Set(relationshipPlatforms).count == relationshipPlatforms.count &&
            relationshipPlatforms.allSatisfy { platform in selections.contains { $0.platform == platform && $0.playlists } } &&
            (relationshipPlatforms.isEmpty || orderOnly == nil), .corruptStorage)
        for binding in bindings.values {
            try musicRequire(!binding.accountID.isEmpty && !binding.scope.isEmpty, .corruptStorage)
        }
        if let orderOnly {
            try musicRequire(orderOnly == "favorites" ||
                orderOnly.range(of: "^playlist:[1-9][0-9]*$", options: .regularExpression) != nil,
                .corruptStorage)
        }
    }
}

struct MusicNativeSyncRound {
    let request: MusicLibrarySyncRequest
    let epoch: UUID
    let port: any MusicNativeLibrarySyncProviding
    var sessions: [String: MusicLibrarySyncSession] = [:]
    var snapshots: [String: MusicLibrarySyncSnapshot] = [:]
    var library: MusicLibraryState
    var ledger: MusicNativeSyncLedger
    var revision: Int
    var relationshipsOnly = false
    mutating func issue(_ code: String, platform: MusicAccountPlatform? = nil, collection: String? = nil) {
        let issue = MusicLibrarySyncIssue(platform: platform, collectionID: collection, code: code)
        if !ledger.issues.contains(issue) { ledger.issues.append(issue) }
    }
}

enum MusicNativeSyncLogic {
    static func unique(_ ids: [String]) -> [String] {
        var seen = Set<Data>()
        return ids.filter { seen.insert(Data($0.utf8)).inserted }
    }

    /// iWebPlus mergeOrder: stable existing anchors, including conflicting
    /// anchors. Do not replace it with sorting or append-only concatenation.
    static func mergeOrder(_ base: [String], _ incoming: [String]) -> [String] {
        var result = unique(base), previous: String?
        let input = unique(incoming)
        var index = 0
        while index < input.count {
            if result.contains(input[index]) { previous = input[index]; index += 1; continue }
            var run: [String] = []
            while index < input.count && !result.contains(input[index]) { run.append(input[index]); index += 1 }
            let after = previous.flatMap { result.firstIndex(of: $0) } ?? -1
            let before = index < input.count ? (result.firstIndex(of: input[index]) ?? -1) : -1
            let at = before > after ? before : after >= 0 ? after + 1 : result.count
            result.insert(contentsOf: run, at: at); previous = run.last
        }
        return result
    }

    static func bindingKey(_ binding: MusicCollectionBindingRecord) -> String {
        MusicWireCodec.canonicalStrings([binding.platform, binding.accountID, binding.remoteID])
    }

    static func mutationKey(_ mutation: MusicLibrarySyncMutation) -> String {
        let parts = [
            MusicWireCodec.quote(mutation.action.rawValue),
            mutation.collectionID.map(MusicWireCodec.quote) ?? "null",
            mutation.name.map(MusicWireCodec.quote) ?? "null",
            MusicWireCodec.canonicalStrings(mutation.members.map(\.nativeID))
        ]
        return "[" + parts.joined(separator: ",") + "]"
    }

    static func code(_ error: Error) -> String {
        switch MusicServiceError.sanitized(error) {
        case .cancelled, .staleAccess, .accessUnavailable, .closed: return "cancelled"
        case .targetChanged: return "accountChanged"
        case .revisionConflict, .staleRevision, .staleResult: return "conflict"
        case .authRequired, .verificationRequired: return "authRequired"
        case .sessionExpired: return "sessionExpired"
        case .commitUnconfirmed: return "unconfirmed"
        case .limit: return "limit"
        default: return "failed"
        }
    }
}
