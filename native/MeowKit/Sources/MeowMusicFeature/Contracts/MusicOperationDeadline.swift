import Foundation
import MeowContracts

/// Bounded deadline runner that does not wait for an uncooperative dependency
/// to unwind. Native owners still track underlying work until completion.
public enum MusicOperationDeadline {
    public static func run<T: Sendable>(
        seconds: Double,
        operation: @escaping @Sendable () async throws -> T
    ) async throws -> T {
        guard seconds.isFinite, seconds > 0, seconds <= 35 else { throw MusicServiceError.invalidRequest }
        try Task.checkCancellation()
        let gate = SingleShotAsyncGate<T>()
        let task = Task {
            do {
                let value = try await operation()
                try Task.checkCancellation()
                gate.finish(.success(value))
            } catch {
                gate.finish(.failure(error))
            }
        }
        let timer = Task {
            do { try await Task.sleep(nanoseconds: UInt64(seconds * 1_000_000_000)) } catch { return }
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
