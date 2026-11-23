import Foundation
import MeowMusicFeature

/// Synchronous protection fence, separate from the service actor's queue.
/// An already-entered atomic commit settles before revoke returns; a queued
/// old grant cannot reopen the document after revocation.
final class MusicNativeAccess: @unchecked Sendable {
    private let lock = NSLock()
    private var available = false
    private var closed = false
    private var revokedThrough: UInt64 = 0
    private var callbacks: [UUID: @Sendable () -> Void] = [:]

    func grant(_ value: Bool, generation: UInt64) throws {
        lock.lock(); defer { lock.unlock() }
        guard !closed else { throw MusicServiceError.closed }
        guard !value || generation > revokedThrough else { throw MusicServiceError.staleAccess }
        available = value
        if !value { revokedThrough = max(revokedThrough, generation) }
    }

    func revoke(generation: UInt64, close: Bool = false) {
        lock.lock()
        available = false
        closed = closed || close
        revokedThrough = max(revokedThrough, generation)
        let callbacks = Array(callbacks.values)
        lock.unlock()
        for callback in callbacks { callback() }
    }

    func perform<T>(_ operation: () throws -> T) throws -> T {
        lock.lock(); defer { lock.unlock() }
        guard !closed else { throw MusicServiceError.closed }
        guard available else { throw MusicServiceError.accessUnavailable }
        return try operation()
    }

    func check() throws { try perform {} }

    func register(_ id: UUID, cancel: @escaping @Sendable () -> Void) {
        lock.lock()
        callbacks[id] = cancel
        let revoked = !available || closed
        lock.unlock()
        if revoked { cancel() }
    }

    func remove(_ id: UUID) {
        lock.lock(); defer { lock.unlock() }
        callbacks.removeValue(forKey: id)
    }
}

extension NativeMusicService {
    public nonisolated func revokeAccess(generation: UInt64) {
        access.revoke(generation: generation)
    }
}

/// The same access fence protects lazy row reads as control-state admission.
/// It owns neither records nor indexes; all facts remain in the SQLite owner.
final class MusicNativeLibraryAccessReader: MusicNativeLibraryReading, @unchecked Sendable {
    let database: any MusicNativeLibraryReading
    let access: MusicNativeAccess
    init(database: any MusicNativeLibraryReading, access: MusicNativeAccess) { self.database = database; self.access = access }
    private func read<T>(_ action: () throws -> T) throws -> T {
        try access.check()
        let value = try action()
        // Never hold the grant lock while waiting for the database lane, which
        // acquires the same lock only for an actual commit.
        return try access.perform { value }
    }
    func readRevision() throws -> Int { try read { try database.readRevision() } }
    func readSnapshot() throws -> MusicLibrarySnapshot { try read { try database.readSnapshot() } }
    func song(id: String) throws -> MusicSongRecord? { try read { try database.song(id: id) } }
    func containsSong(id: String) throws -> Bool { try read { try database.containsSong(id: id) } }
    func sourceOwner(key: String) throws -> String? { try read { try database.sourceOwner(key: key) } }
    func sourceOwners(keys: Set<String>) throws -> [String: String] { try read { try database.sourceOwners(keys: keys) } }
    func candidateIDs(matchKey: String) throws -> [String] { try read { try database.candidateIDs(matchKey: matchKey) } }
    func candidateIDs(matchKeys: Set<String>) throws -> [String: [String]] { try read { try database.candidateIDs(matchKeys: matchKeys) } }
    func querySongIDs(query: String) throws -> [String] { try read { try database.querySongIDs(query: query) } }
    func allSongIDs() throws -> [String] { try read { try database.allSongIDs() } }
}
