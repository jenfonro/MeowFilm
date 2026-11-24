import Foundation

/// Single-shot lock + continuation gate that decouples logical completion or
/// cancellation from physical async work settlement.
public final class SingleShotAsyncGate<Value: Sendable>: @unchecked Sendable {
    private let lock = NSLock()
    private var result: Result<Value, Error>?
    private var waiters: [CheckedContinuation<Value, Error>] = []

    public init() {}

    public func finish(_ value: Result<Value, Error>) {
        lock.lock()
        guard result == nil else { lock.unlock(); return }
        result = value
        let pending = waiters
        waiters.removeAll()
        lock.unlock()
        for waiting in pending {
            waiting.resume(with: value)
        }
    }

    public func value() async throws -> Value {
        try await withCheckedThrowingContinuation { waiting in
            lock.lock()
            if let result {
                lock.unlock()
                waiting.resume(with: result)
            } else {
                waiters.append(waiting)
                lock.unlock()
            }
        }
    }
}
