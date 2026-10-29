import Foundation

/// Cancellation ends the logical wait, not the physical task. The owner retains
/// its slot until that task settles, including non-cooperative injected I/O.
final class MusicAccountOperation<Value: Sendable>: @unchecked Sendable {
    private let lock = NSLock()
    private var result: Result<Value, Error>?
    private var continuation: CheckedContinuation<Value, Error>?

    func finish(_ value: Result<Value, Error>) {
        lock.lock()
        guard result == nil else { lock.unlock(); return }
        result = value
        let waiting = continuation; continuation = nil
        lock.unlock()
        waiting?.resume(with: value)
    }

    func value() async throws -> Value {
        try await withCheckedThrowingContinuation { waiting in
            lock.lock()
            if let result { lock.unlock(); waiting.resume(with: result) }
            else { continuation = waiting; lock.unlock() }
        }
    }
}
