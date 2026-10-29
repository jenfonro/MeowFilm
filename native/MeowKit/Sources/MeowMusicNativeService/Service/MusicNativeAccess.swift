import Foundation
import MeowMusicFeature

/// Synchronous protection fence, separate from the service actor's queue.
/// An already-entered atomic commit settles before revoke returns; a queued
/// old grant cannot reopen the document after a lock-screen notification.
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
