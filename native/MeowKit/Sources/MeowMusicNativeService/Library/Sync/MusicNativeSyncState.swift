// SPDX-License-Identifier: GPL-3.0-only
import Foundation
import MeowMusicFeature

struct MusicNativeSyncFence: Codable {
    let platform: MusicAccountPlatform
    let accountID: String
    let scope: String
    let localID: String
    let localRevision: Int
    let mutation: MusicLibrarySyncMutation
    var acknowledgedID: String?
    var acknowledgedName: String?

    func confirmed(in snapshot: MusicLibrarySyncSnapshot) -> Bool {
        guard snapshot.accountID == accountID else { return false }
        let id = mutation.action == .create ? acknowledgedID : mutation.collectionID
        guard let id else { return false } // Lost creation ID is never inferred from name.
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
        case .create, .delete: return false
        }
    }
}

/// Synchronization metadata in the library wrapper, without credentials or media URLs.
struct MusicNativeSyncLedger: Codable {
    var schema = 1
    var selections: [MusicLibrarySyncSelection] = []
    var scopes: [String: String] = [:]
    var fences: [MusicNativeSyncFence] = []
    var phase: MusicLibrarySyncState.Phase = .idle
    var issues: [MusicLibrarySyncIssue] = []
    // Optional on disk: old manual selections NEVER opt a user into automatic
    // writes. Other library fields retain their current values.
    var automatic: MusicNativeAutomaticSyncPreferences?
    static func load(_ wrapper: MJ) throws -> Self {
        guard wrapper.contains("sync") else { return .init() }
        let value: Self
        do { value = try JSONDecoder().decode(Self.self, from: wrapper["sync"].bytes) }
        catch { throw MusicServiceError.corruptDocument }
        guard value.schema == 1, value.selections.count <= 3,
              Set(value.selections.map(\.platform)).count == value.selections.count,
              value.scopes.count <= 3, value.scopes.keys.allSatisfy({ MusicAccountPlatform(rawValue: $0) != nil }),
              value.fences.count <= 512, Set(value.fences.map { $0.mutation.id }).count == value.fences.count,
              value.issues.count <= 128 else { throw MusicServiceError.corruptDocument }
        try value.automatic?.validate()
        for fence in value.fences {
            guard fence.localRevision >= 0, !fence.localID.isEmpty, !fence.accountID.isEmpty, !fence.scope.isEmpty,
                  fence.localID.utf8.count <= 1024, fence.accountID.utf8.count <= 128, fence.scope.utf8.count <= 1024,
                  wrapper["library"]["collections"].array.contains(where: { $0["id"].string == fence.localID }),
                  wrapper["library"]["accounts"][fence.platform.rawValue].string == fence.accountID,
                  value.scopes[fence.platform.rawValue] == fence.scope,
                  fence.mutation.members.count <= 20_000,
                  Set(fence.mutation.before?.members.map(\.occurrenceID) ?? []).count == (fence.mutation.before?.members.count ?? 0) else {
                throw MusicServiceError.corruptDocument
            }
            let mutation = fence.mutation
            if mutation.action == .create {
                guard mutation.collectionID == nil, mutation.before == nil, mutation.members.isEmpty,
                      mutation.name?.isEmpty == false, !mutation.directoryRevision.isEmpty,
                      fence.acknowledgedID == nil || fence.acknowledgedID?.isEmpty == false else { throw MusicServiceError.corruptDocument }
            } else {
                guard let before = mutation.before, before.id == mutation.collectionID, !before.id.isEmpty,
                      before.writable, before.members.count <= 20_000, !before.revision.isEmpty,
                      fence.acknowledgedID == nil, fence.acknowledgedName == nil else { throw MusicServiceError.corruptDocument }
                switch mutation.action {
                case .create: break
                case .delete:
                    guard before.canDelete, mutation.members.isEmpty else { throw MusicServiceError.corruptDocument }
                case .rename:
                    guard before.canRename, mutation.name?.isEmpty == false, mutation.members.isEmpty else { throw MusicServiceError.corruptDocument }
                case .order:
                    guard before.canOrder,
                          before.members.map(\.occurrenceID).sorted() == mutation.members.map(\.occurrenceID).sorted(),
                          before.members.map(\.nativeID).sorted() == mutation.members.map(\.nativeID).sorted() else {
                        throw MusicServiceError.corruptDocument
                    }
                case .add, .remove:
                    guard (1...100).contains(mutation.members.count),
                          Set(mutation.members.map(\.nativeID)).count == mutation.members.count else { throw MusicServiceError.corruptDocument }
                }
            }
            for member in fence.mutation.members + (fence.mutation.before?.members ?? []) {
                guard !member.nativeID.isEmpty, !member.occurrenceID.isEmpty,
                      member.nativeID.utf8.count <= 128, member.occurrenceID.utf8.count <= 128 else { throw MusicServiceError.corruptDocument }
                let raw = try MJ.parse(member.track, maximum: 64 * 1024)
                try MusicModel.rejectSecrets(raw)
                let song = try MusicModel.normalize(.init(platform: fence.platform.rawValue, data: member.track))
                guard try MusicModel.sources(song).contains(where: {
                    $0.platform == fence.platform.rawValue && $0.nativeId == member.nativeID
                }) else { throw MusicServiceError.corruptDocument }
            }
        }
        return value
    }
    func json() throws -> MJ {
        let encoder = JSONEncoder(); encoder.outputFormatting = [.sortedKeys, .withoutEscapingSlashes]
        let data = try encoder.encode(self)
        try musicRequire(data.count <= 5 * 1024 * 1024, .limit)
        return try MJ.parse(data)
    }
}

struct MusicNativeAutomaticSyncBinding: Codable, Equatable {
    let accountID: String
    let scope: String
}

struct MusicNativeAutomaticSyncPreferences: Codable {
    var revision = 0
    var selections: [MusicLibrarySyncSelection] = []
    // Stable nonsecret identities, NOT credential revisions or live permits.
    // They are established only by a foreground settings/manual-sync decision.
    var bindings: [String: MusicNativeAutomaticSyncBinding] = [:]
    var intentRevision = 0
    var settledIntentRevision = 0
    var orderOnly: String?

    func validate() throws {
        let maximum = 9_007_199_254_740_991
        try musicRequire((0...maximum).contains(revision) &&
            (0...maximum).contains(intentRevision) &&
            (0...intentRevision).contains(settledIntentRevision) &&
            selections.count <= 3 && Set(selections.map(\.platform)).count == selections.count &&
            selections.allSatisfy { !$0.kinds.isEmpty } &&
            bindings.count <= 3 &&
            bindings.keys.allSatisfy { key in selections.contains { $0.platform.rawValue == key } },
            .corruptDocument)
        for binding in bindings.values {
            try musicRequire(!binding.accountID.isEmpty && binding.accountID.utf8.count <= 1024 &&
                !binding.scope.isEmpty && binding.scope.utf8.count <= 1024, .corruptDocument)
        }
        if let orderOnly {
            try musicRequire(orderOnly == "favorites" ||
                orderOnly.range(of: "^playlist:[1-9][0-9]*$", options: .regularExpression) != nil,
                .corruptDocument)
        }
    }
}

struct MusicNativeSyncRound {
    let request: MusicLibrarySyncRequest
    let epoch: UUID
    let port: any MusicNativeLibrarySyncProviding
    var sessions: [String: MusicLibrarySyncSession] = [:]
    var snapshots: [String: MusicLibrarySyncSnapshot] = [:]
    var library: MJ
    var ledger: MusicNativeSyncLedger
    var revision: Int
    var writes = 0
    mutating func issue(_ code: String, platform: MusicAccountPlatform? = nil, collection: String? = nil) {
        let issue = MusicLibrarySyncIssue(platform: platform, collectionID: collection, code: code)
        if !ledger.issues.contains(issue), ledger.issues.count < 128 { ledger.issues.append(issue) }
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
    static func bindingKey(_ binding: MJ) -> String {
        MJ.array([binding["platform"], binding["accountId"], binding["id"]]).json
    }
    static func map(_ binding: MJ, songs: MJ) throws -> MJ {
        var result = MJ.obj()
        for (key, value) in binding["map"].fields {
            // Fixed iWeb uses native/occurrence -> song. Early migrated
            // music-core validation also accepted the reverse orientation.
            if songs.contains(value.string) { result[key] = value }
            else if songs.contains(key), value.isString {
                try musicRequire(!result.contains(value.string) || result[value.string].string == key, .corruptDocument)
                result[value.string] = .string(key)
            } else { throw MusicServiceError.corruptDocument }
        }
        return result
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
