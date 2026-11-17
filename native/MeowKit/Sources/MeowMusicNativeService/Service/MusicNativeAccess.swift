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
    func readSnapshot() throws -> MusicNativeLibrarySnapshot { try access.perform { try database.readSnapshot() } }
    func songData(id: String) throws -> Data? { try access.perform { try database.songData(id: id) } }
    func containsSong(id: String) throws -> Bool { try access.perform { try database.containsSong(id: id) } }
    func sourceOwner(key: String) throws -> String? { try access.perform { try database.sourceOwner(key: key) } }
    func candidateIDs(lookupKey: String) throws -> [String] { try access.perform { try database.candidateIDs(lookupKey: lookupKey) } }
    func querySongIDs(query: String) throws -> [String] { try access.perform { try database.querySongIDs(query: query) } }
    func allSongIDs() throws -> [String] { try access.perform { try database.allSongIDs() } }
}
