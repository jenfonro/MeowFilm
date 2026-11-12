// SPDX-License-Identifier: GPL-3.0-only
import Foundation
import CryptoKit
import MeowAccounts
import MeowMusicFeature
import MeowJSON

protocol MusicAccountSyncProvider: MusicAccountProvider {
    func syncRead(kinds: [MusicLibrarySyncKind], credential: AccountCredential,
                  guard authorization: MusicAccountSyncAuthorization) async throws -> MusicLibrarySyncSnapshot
    func syncReadSubscriptions(credential: AccountCredential,
                               guard authorization: MusicAccountSyncAuthorization) async throws -> MusicLibrarySyncSnapshot
    func syncWrite(_ mutation: MusicLibrarySyncMutation, credential: AccountCredential,
                   guard authorization: MusicAccountSyncAuthorization) async throws -> MusicLibrarySyncWriteResult
}

/// Per-write proof, independent of the account owner's early cancellation gate.
/// Once stopped, even a non-cooperative token completion cannot start the send.
final class MusicAccountSyncDispatch: @unchecked Sendable {
    private let lock = NSLock()
    private var stopped = false, started = false
    func begin() throws {
        lock.lock(); defer { lock.unlock() }
        guard !stopped, !Task.isCancelled else { throw MusicServiceError.cancelled }
        started = true
    }
    func stopBeforeDispatch() -> Bool {
        lock.lock(); defer { lock.unlock() }
        stopped = true
        return !started
    }
}

/// The owner revokes this synchronously before crossing an actor during
/// protection/logout/relogin. Providers check it before each write, not only
/// when accepting the final result.
final class MusicAccountSyncAuthorization: @unchecked Sendable {
    private let lock = NSLock()
    private var active = true
    private let parent: MusicAccountSyncAuthorization?
    private let dispatch: MusicAccountSyncDispatch?
    let current: @Sendable () async throws -> Void
    init(current: @escaping @Sendable () async throws -> Void) {
        self.current = current; parent = nil; dispatch = nil
    }
    init(parent: MusicAccountSyncAuthorization, dispatch: MusicAccountSyncDispatch) {
        self.parent = parent; self.dispatch = dispatch; current = parent.current
    }
    func revoke() { lock.lock(); active = false; lock.unlock() }
    func checkLocal() throws {
        try parent?.checkLocal()
        lock.lock(); let allowed = active; lock.unlock()
        guard allowed else { throw MusicServiceError.staleResult }
        if Task.isCancelled { throw MusicServiceError.cancelled }
    }
    func check() async throws { try checkLocal(); try await current(); try checkLocal() }
    func beginDispatch() throws {
        // Linearize the send boundary with the session's synchronous revoke,
        // not a check whose lock is released before marking dispatch started.
        try (parent ?? self).beginDispatch(dispatch)
    }
    private func beginDispatch(_ dispatch: MusicAccountSyncDispatch?) throws {
        lock.lock(); defer { lock.unlock() }
        guard active else { throw MusicServiceError.staleResult }
        if Task.isCancelled { throw MusicServiceError.cancelled }
        try dispatch?.begin()
    }
}

struct MusicAccountSyncBinding {
    let session: MusicLibrarySyncSession
    let lease: AccountCredentialUpdateLease
    let epoch: UUID
    let revision: Int
    let expires: Date
    let authorization: MusicAccountSyncAuthorization
    func revoke() { authorization.revoke(); lease.revoke() }
}

enum MusicAccountSyncWire {
    static func encoded<T: Encodable>(_ value: T) throws -> Data {
        let encoder = JSONEncoder(); encoder.outputFormatting = [.sortedKeys, .withoutEscapingSlashes]
        return try encoder.encode(value)
    }
    static func hash(_ data: Data) -> String { SHA256.hash(data: data).map { String(format: "%02x", $0) }.joined() }
    static func name(_ value: String?, maximum: Int = 100) throws -> String {
        let value = try MusicQQWire.string(value, maximum: 600)
        guard !value.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty, value.unicodeScalars.count <= maximum,
              !value.unicodeScalars.contains(where: { $0.value < 32 || $0.value == 127 }) else { throw MusicServiceError.invalidRequest }
        return value
    }
    static func member(_ value: JSONValue, platform: MusicAccountPlatform) throws -> MusicLibrarySyncMember {
        let data = try encoded(value), raw = try MusicQQWire.decode(data)
        let id: String
        switch platform {
        case .netease: id = try MusicNeteaseWire.id(raw["songmid"])
        case .qq: id = try MusicQQWire.id(raw["songmid"], mid: true)
        case .kugou: id = try MusicKugouWire.hash(raw["hash"] ?? raw["songmid"])
        }
        let occurrence = platform == .kugou && raw["fileid"] != nil ? try MusicKugouWire.id(raw["fileid"]) : id
        return .init(nativeID: id, occurrenceID: occurrence, track: data)
    }
    static func collection(id: String, name: String, kind: MusicLibrarySyncKind, writable: Bool,
                           canOrder: Bool, members: [MusicLibrarySyncMember], fingerprint: Data = Data()) throws -> MusicLibrarySyncCollection {
        guard Set(members.map(\.occurrenceID)).count == members.count else { throw MusicServiceError.upstreamInvalid }
        let bytes = try encoded(members)
        guard bytes.count <= 4 * 1024 * 1024 else { throw MusicServiceError.limit }
        let revision = hash(try encoded([id, name, kind.rawValue, String(writable), String(canOrder)]) + bytes + fingerprint)
        return .init(id: id, name: name, kind: kind, writable: writable,
                     canRename: writable && kind == .playlist, canDelete: writable && kind == .playlist,
                     canOrder: writable && canOrder, members: members, revision: revision)
    }
    static func snapshot(_ rows: [MusicLibrarySyncCollection], subscriptions: [MusicLibrarySyncSubscription] = [],
                         accountID: String,
                         kinds: [MusicLibrarySyncKind], directory: Data? = nil) throws -> MusicLibrarySyncSnapshot {
        guard !kinds.isEmpty, Set(kinds).count == kinds.count, rows.count + subscriptions.count <= 2000,
              Set(rows.map(\.id)).count == rows.count, rows.allSatisfy({ kinds.contains($0.kind) }),
              rows.allSatisfy(\.writable), Set(subscriptions.map(\.remoteID)).count == subscriptions.count,
              (kinds.contains(.playlist) || subscriptions.isEmpty),
              !kinds.contains(.favorites) || rows.filter({ $0.kind == .favorites }).count == 1 else {
            throw MusicServiceError.upstreamInvalid
        }
        for subscription in subscriptions {
            guard !subscription.remoteID.isEmpty, !subscription.relationID.isEmpty,
                  subscription.remoteID.utf8.count <= 200, subscription.relationID.utf8.count <= 256,
                  !subscription.name.isEmpty, subscription.name.utf8.count <= 600,
                  subscription.creator.utf8.count <= 600, subscription.artwork.utf8.count <= 4096,
                  subscription.count == nil || subscription.count! >= 0 else { throw MusicServiceError.upstreamInvalid }
        }
        let bytes = try encoded(rows) + encoded(subscriptions)
        guard bytes.count <= 4 * 1024 * 1024 else { throw MusicServiceError.limit }
        return .init(accountID: accountID, collections: rows, subscriptions: subscriptions, revision: hash(directory ?? bytes))
    }
    static func subscriptionPreflight(_ mutation: MusicLibrarySyncMutation,
                                      subscriptions: [MusicLibrarySyncSubscription], directoryRevision: String,
                                      ownedIDs: [String]) throws {
        guard mutation.action.isSubscription, let id = mutation.collectionID, !id.isEmpty,
              mutation.before == nil, mutation.members.isEmpty,
              mutation.directoryRevision == directoryRevision,
              !ownedIDs.contains(id) else { throw MusicServiceError.revisionConflict }
        let fresh = subscriptions.first { $0.remoteID == id }
        if mutation.action == .subscribe {
            _ = try MusicQQWire.string(mutation.name, maximum: 600)
            guard fresh == nil, mutation.beforeSubscription == nil else { throw MusicServiceError.revisionConflict }
        } else {
            guard let fresh, fresh == mutation.beforeSubscription, mutation.name == nil else { throw MusicServiceError.revisionConflict }
        }
    }
    static func preflight(_ mutation: MusicLibrarySyncMutation, snapshot: MusicLibrarySyncSnapshot, nameLimit: Int = 100) throws -> MusicLibrarySyncCollection? {
        guard !mutation.action.isSubscription, mutation.beforeSubscription == nil else { throw MusicServiceError.invalidRequest }
        if mutation.action == .create {
            _ = try name(mutation.name, maximum: nameLimit)
            guard mutation.before == nil, mutation.collectionID == nil, mutation.members.isEmpty,
                  mutation.directoryRevision == snapshot.revision else { throw MusicServiceError.revisionConflict }
            return nil
        }
        guard let before = mutation.before, mutation.collectionID == before.id,
              let fresh = snapshot.collections.first(where: { $0.id == before.id }),
              fresh.revision == before.revision, fresh.writable else { throw MusicServiceError.revisionConflict }
        switch mutation.action {
        case .create, .subscribe, .unsubscribe: break
        case .delete:
            guard fresh.canDelete else { throw MusicServiceError.favoritesProtected }
            guard mutation.members.isEmpty else { throw MusicServiceError.invalidRequest }
        case .rename:
            guard fresh.canRename else { throw MusicServiceError.favoritesProtected }
            guard mutation.members.isEmpty else { throw MusicServiceError.invalidRequest }
            _ = try name(mutation.name, maximum: nameLimit)
        case .order:
            guard fresh.canOrder else { throw MusicServiceError.unsupported }
            guard Set(mutation.members.map(\.occurrenceID)).count == mutation.members.count,
                  fresh.members.map(\.occurrenceID).sorted() == mutation.members.map(\.occurrenceID).sorted(),
                  Dictionary(uniqueKeysWithValues: fresh.members.map { ($0.occurrenceID, $0.nativeID) }) ==
                    Dictionary(uniqueKeysWithValues: mutation.members.map { ($0.occurrenceID, $0.nativeID) }) else {
                throw MusicServiceError.invalidRequest
            }
        case .add, .remove:
            guard (1...100).contains(mutation.members.count),
                  Set(mutation.members.map(\.nativeID)).count == mutation.members.count else { throw MusicServiceError.invalidRequest }
            let ids = Set(fresh.members.map(\.nativeID))
            guard mutation.members.allSatisfy({ ids.contains($0.nativeID) == (mutation.action == .remove) }) else {
                throw MusicServiceError.revisionConflict
            }
        }
        return fresh
    }
}
