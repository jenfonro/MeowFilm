// SPDX-License-Identifier: GPL-3.0-only
import Foundation
import MeowAccounts
import MeowMusicFeature

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

enum MusicAccountSyncPreflight {
    static func subscription(_ mutation: MusicLibrarySyncMutation, snapshot: MusicLibrarySyncSnapshot) throws {
        guard let id = mutation.collectionID, mutation.directoryRevision == snapshot.revision,
              mutation.before == nil, mutation.members.isEmpty,
              !snapshot.ownedCollectionIDs.contains(id),
              !snapshot.collections.contains(where: { $0.id == id }) else { throw MusicServiceError.revisionConflict }
        let fresh = snapshot.subscriptions.first { $0.remoteID == id }
        if mutation.action == .subscribe {
            guard fresh == nil, mutation.beforeSubscription == nil else { throw MusicServiceError.revisionConflict }
        } else {
            guard let fresh, fresh == mutation.beforeSubscription else { throw MusicServiceError.revisionConflict }
        }
    }
    static func collection(_ mutation: MusicLibrarySyncMutation, snapshot: MusicLibrarySyncSnapshot) throws -> MusicLibrarySyncCollection? {
        if mutation.action == .create {
            guard mutation.before == nil, mutation.collectionID == nil, mutation.members.isEmpty,
                  mutation.directoryRevision == snapshot.revision else { throw MusicServiceError.revisionConflict }
            return nil
        }
        guard let before = mutation.before, mutation.collectionID == before.id,
              let fresh = snapshot.collections.first(where: { $0.id == before.id }),
              fresh.revision == before.revision, fresh.writable else { throw MusicServiceError.revisionConflict }
        switch mutation.action {
        case .delete: guard fresh.canDelete else { throw MusicServiceError.favoritesProtected }
        case .rename: guard fresh.canRename else { throw MusicServiceError.favoritesProtected }
        case .order:
            guard fresh.canOrder else { throw MusicServiceError.unsupported }
            guard Set(mutation.members.map(\.occurrenceID)).count == mutation.members.count,
                  Set(fresh.members.map(\.occurrenceID)).count == fresh.members.count,
                  mutation.members.map(\.occurrenceID).sorted() == fresh.members.map(\.occurrenceID).sorted(),
                  Dictionary(uniqueKeysWithValues: mutation.members.map { ($0.occurrenceID, $0.nativeID) }) ==
                    Dictionary(uniqueKeysWithValues: fresh.members.map { ($0.occurrenceID, $0.nativeID) }) else {
                throw MusicServiceError.revisionConflict
            }
        case .add, .remove:
            let ids = Set(fresh.members.map(\.nativeID))
            guard !mutation.members.isEmpty, Set(mutation.members.map(\.nativeID)).count == mutation.members.count,
                  mutation.members.allSatisfy({ ids.contains($0.nativeID) == (mutation.action == .remove) }) else {
                throw MusicServiceError.revisionConflict
            }
        default: break
        }
        return fresh
    }
}
