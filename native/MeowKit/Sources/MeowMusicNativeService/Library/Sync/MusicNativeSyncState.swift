// SPDX-License-Identifier: GPL-3.0-only
import Foundation
import MeowMusicPlatform

/// Durable write facts only. Live mutation tracks are request material, not a
/// second persistent song repository or an input to unknown-write recovery.
struct MusicNativeSyncMemberProof: Equatable, Sendable {
    let nativeID: String
    let occurrenceID: String
    init(nativeID: String, occurrenceID: String) { self.nativeID = nativeID; self.occurrenceID = occurrenceID }
    init(_ member: MusicLibrarySyncMember) { nativeID = member.nativeID; occurrenceID = member.occurrenceID }
}

struct MusicNativeSyncCollectionProof: Equatable, Sendable {
    let id: String, name: String, kind: MusicLibrarySyncKind
    let writable: Bool, canRename: Bool, canDelete: Bool, canOrder: Bool
    let members: [MusicNativeSyncMemberProof], revision: String
    init(id: String, name: String, kind: MusicLibrarySyncKind, writable: Bool, canRename: Bool,
         canDelete: Bool, canOrder: Bool, members: [MusicNativeSyncMemberProof], revision: String) {
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
    let id: UUID, action: MusicLibrarySyncMutation.Action
    let collectionID: String?, name: String?
    let before: MusicNativeSyncCollectionProof?, beforeSubscription: MusicLibrarySyncSubscription?
    let directoryRevision: String, members: [MusicNativeSyncMemberProof]
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
    var directoryIDs: [String] = []
    var acknowledgedID: String?
    var acknowledgedName: String?

    func resolvedTargetID(in snapshot: MusicLibrarySyncSnapshot) -> String? {
        if mutation.action != .create { return mutation.collectionID }
        if let acknowledgedID, !acknowledgedID.isEmpty { return acknowledgedID }
        guard let expectedName = acknowledgedName ?? mutation.name, !expectedName.isEmpty else { return nil }
        let existing = Set(directoryIDs)
        let candidates = snapshot.collections.filter {
            $0.writable && $0.kind == .playlist && !existing.contains($0.id) &&
                MusicModel.norm($0.name) == MusicModel.norm(expectedName) && $0.members.isEmpty
        }
        return candidates.count == 1 ? candidates[0].id : nil
    }

    func confirmed(in snapshot: MusicLibrarySyncSnapshot) -> Bool {
        guard snapshot.accountID == accountID else { return false }
        if mutation.action.isSubscription {
            guard let id = mutation.collectionID else { return false }
            return snapshot.subscriptions.contains(where: { $0.remoteID == id }) == (mutation.action == .subscribe)
        }
        guard let id = resolvedTargetID(in: snapshot) else { return false }
        let actual = snapshot.collections.first { $0.id == id }
        if mutation.action == .delete { return actual == nil }
        guard let actual, actual.writable else { return false }
        if mutation.action == .create {
            guard let expectedName = acknowledgedName ?? mutation.name, !expectedName.isEmpty else { return false }
            return actual.kind == .playlist &&
                (actual.name == expectedName || MusicModel.norm(actual.name) == MusicModel.norm(expectedName)) &&
                actual.members.isEmpty
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
        value.phase = MusicLibrarySyncState.Phase(rawValue: record.phase) ?? .idle
        value.selections = record.manualSelections.compactMap { row in
            MusicAccountPlatform(rawValue: row.platform).map {
                MusicLibrarySyncSelection(platform: $0, favorites: row.favorites, playlists: row.playlists)
            }
        }
        value.scopes = Dictionary(uniqueKeysWithValues: record.scopes.map { ($0.platform, $0.accountScope) })
        value.issues = record.issues.map { row in
            MusicLibrarySyncIssue(platform: row.platform.flatMap(MusicAccountPlatform.init(rawValue:)),
                                  collectionID: row.collectionID, code: row.code)
        }
        value.automatic = MusicNativeAutomaticSyncPreferences(
            revision: record.autoRevision,
            selections: record.automaticSelections.compactMap { row in
                MusicAccountPlatform(rawValue: row.platform).map {
                    MusicLibrarySyncSelection(platform: $0, favorites: row.favorites, playlists: row.playlists)
                }
            },
            bindings: Dictionary(uniqueKeysWithValues: record.autoBindings.map {
                ($0.platform, MusicNativeAutomaticSyncBinding(accountID: $0.accountID, scope: $0.bindingScope))
            }),
            intentRevision: record.autoIntentRevision,
            settledIntentRevision: record.autoSettledIntentRevision,
            orderOnly: record.autoOrderOnly,
            relationshipPlatforms: record.relationshipPlatforms.compactMap(MusicAccountPlatform.init(rawValue:))
        )
        value.fences = record.fences.compactMap { row in
            guard let platform = MusicAccountPlatform(rawValue: row.platform),
                  let mutIDRaw = row.mutationID, let mutID = UUID(uuidString: mutIDRaw),
                  let actionRaw = row.mutationAction, let action = MusicLibrarySyncMutation.Action(rawValue: actionRaw) else {
                return nil
            }
            let before: MusicNativeSyncCollectionProof? = row.before.flatMap { b in
                MusicLibrarySyncKind(rawValue: b.kind).map { kind in
                    MusicNativeSyncCollectionProof(
                        id: b.id, name: b.name, kind: kind, writable: b.writable,
                        canRename: b.canRename, canDelete: b.canDelete, canOrder: b.canOrder,
                        members: b.members.map { MusicNativeSyncMemberProof(nativeID: $0.nativeID, occurrenceID: $0.occurrenceID ?? "") },
                        revision: b.revision
                    )
                }
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
                directoryIDs: row.directoryIDs,
                acknowledgedID: row.acknowledgedID, acknowledgedName: row.acknowledgedName
            )
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
                    directoryIDs: fence.directoryIDs,
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
}

struct MusicNativeSyncRound {
    let request: MusicLibrarySyncRequest
    let epoch: UUID
    let port: NativeMusicAccountService
    var automaticBindings: [String: MusicNativeAutomaticSyncBinding]? = nil
    var automaticAuthorization: MusicQueueAuthorization? = nil
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
    func checkGuard() throws {
        if let automaticAuthorization {
            try automaticAuthorization.check()
            try Task.checkCancellation()
        }
    }
    func beginSession(platform: MusicAccountPlatform) async throws -> MusicLibrarySyncSession {
        try checkGuard()
        if let automaticBindings {
            guard let binding = automaticBindings[platform.rawValue] else { throw MusicServiceError.verificationRequired }
            let session = try await port.beginLibrarySync(platform: platform)
            do {
                try checkGuard()
                guard session.platform == platform, binding.accountID == session.accountID,
                      binding.scope == session.scope else { throw MusicServiceError.targetChanged }
                try await port.checkLibrarySync(session); try checkGuard()
                return session
            } catch { await port.endLibrarySync(session); throw error }
        }
        return try await port.beginLibrarySync(platform: platform)
    }
    func checkSession(_ session: MusicLibrarySyncSession) async throws {
        try checkGuard(); try await port.checkLibrarySync(session); try checkGuard()
    }
    func readSync(_ session: MusicLibrarySyncSession, kinds: [MusicLibrarySyncKind]) async throws -> MusicLibrarySyncSnapshot {
        try await checkSession(session)
        let value = try await port.readLibrarySync(session, kinds: kinds)
        try await checkSession(session)
        return value
    }
    func readSubscriptions(_ session: MusicLibrarySyncSession) async throws -> MusicLibrarySyncSnapshot {
        try await checkSession(session)
        let value = try await port.readLibrarySubscriptions(session)
        try await checkSession(session)
        return value
    }
    func writeSync(_ session: MusicLibrarySyncSession, mutation: MusicLibrarySyncMutation) async throws -> MusicLibrarySyncWriteResult {
        if automaticAuthorization != nil {
            do { try await checkSession(session) }
            catch { return .notDispatched(MusicServiceError.sanitized(error)) }
        }
        return try await port.writeLibrarySync(session, mutation: mutation)
    }
    func endSession(_ session: MusicLibrarySyncSession) async {
        await port.endLibrarySync(session)
    }
}

enum MusicNativeSyncLogic {
    static func unique(_ ids: [String]) -> [String] {
        var seen = Set<String>()
        return ids.filter { seen.insert($0).inserted }
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
        case .limit: return "limit"
        default: return "failed"
        }
    }
}
