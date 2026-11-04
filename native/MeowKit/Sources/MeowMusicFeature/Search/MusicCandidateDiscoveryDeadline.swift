import Foundation

/// Injectable timer for bounded, deterministic tests; production uses monotonic Task.sleep.
public struct MusicCandidateDiscoveryTiming: Sendable {
    public let sleep: @Sendable (UInt64) async throws -> Void
    public init(sleep: @escaping @Sendable (UInt64) async throws -> Void = { try await Task.sleep(nanoseconds: $0) }) {
        self.sleep = sleep
    }
}

/// Does not wait for an uncooperative dependency to unwind. The native owner
/// still tracks its underlying work until actual completion.
public enum MusicCandidateDiscoveryDeadline {
    public static func run<T: Sendable>(seconds: Double, timing: MusicCandidateDiscoveryTiming = .init(),
        operation: @escaping @Sendable () async throws -> T) async throws -> T {
        guard seconds.isFinite, seconds > 0, seconds <= 35 else { throw MusicServiceError.invalidRequest }
        try Task.checkCancellation()
        let gate = MusicCandidateDiscoveryGate<T>()
        let task = Task {
            do { gate.finish(.success(try await operation())) }
            catch { gate.finish(.failure(error)) }
        }
        let timer = Task {
            do { try await timing.sleep(UInt64(seconds * 1_000_000_000)) } catch { return }
            gate.finish(.failure(MusicServiceError.timeout)); task.cancel()
        }
        defer { timer.cancel() }
        return try await withTaskCancellationHandler {
            let result = try await gate.value()
            try Task.checkCancellation()
            return result
        } onCancel: {
            task.cancel(); gate.finish(.failure(MusicServiceError.cancelled))
        }
    }
}

private final class MusicCandidateDiscoveryGate<T: Sendable>: @unchecked Sendable {
    private let lock = NSLock()
    private var result: Result<T, Error>?
    private var continuation: CheckedContinuation<T, Error>?
    func finish(_ value: Result<T, Error>) {
        lock.lock()
        guard result == nil else { lock.unlock(); return }
        result = value
        let waiting = continuation; continuation = nil
        lock.unlock()
        waiting?.resume(with: value)
    }
    func value() async throws -> T {
        try await withCheckedThrowingContinuation { continuation in
            lock.lock()
            if let result { lock.unlock(); continuation.resume(with: result) }
            else { self.continuation = continuation; lock.unlock() }
        }
    }
}
