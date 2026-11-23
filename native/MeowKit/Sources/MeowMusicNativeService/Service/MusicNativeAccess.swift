import Foundation
import MeowMusicFeature

/// Close-only cancellation fence for NativeMusicService.
final class MusicNativeAccess: @unchecked Sendable {
    private let lock = NSLock()
    private var closed = false
    private var callbacks: [UUID: @Sendable () -> Void] = [:]

    func close() {
        lock.lock()
        guard !closed else { lock.unlock(); return }
        closed = true
        let callbacks = Array(callbacks.values)
        self.callbacks.removeAll()
        lock.unlock()
        for callback in callbacks { callback() }
    }

    func perform<T>(_ operation: () throws -> T) throws -> T {
        lock.lock(); defer { lock.unlock() }
        guard !closed else { throw MusicServiceError.closed }
        return try operation()
    }

    func check() throws { try perform {} }

    func register(_ id: UUID, cancel: @escaping @Sendable () -> Void) {
        lock.lock()
        let isClosed = closed
        if !isClosed { callbacks[id] = cancel }
        lock.unlock()
        if isClosed { cancel() }
    }

    func remove(_ id: UUID) {
        lock.lock(); defer { lock.unlock() }
        callbacks.removeValue(forKey: id)
    }
}
