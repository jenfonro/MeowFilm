import Foundation

public enum RuntimeBridgeSessionError: Error, Sendable, Equatable {
    case notConfigured, alreadyConfigured
}

/// App-owned, single-launch connection shared by independent runtime modules.
/// The complete reverse-call table is frozen before any caller can launch.
public actor RuntimeBridgeSession {
    private let launcher: any RuntimeBridgeLaunching
    private var handlers: [String: RuntimeBridgeConnection.Handler]?
    private var launch: Task<Void, Never>?
    private var outcome: Result<RuntimeBridgeConnection, Error>?
    private var waiters: [UUID: CheckedContinuation<RuntimeBridgeConnection, Error>] = [:]
    private var closed = false

    public init(launcher: any RuntimeBridgeLaunching) { self.launcher = launcher }

    public func configure(handlers: [String: RuntimeBridgeConnection.Handler]) throws {
        guard !closed else { throw RuntimeBridgeError.closed }
        guard self.handlers == nil else { throw RuntimeBridgeSessionError.alreadyConfigured }
        guard handlers.keys.allSatisfy(RuntimeBridgeFrame.validMethod) else { throw RuntimeBridgeError.invalidMessage }
        self.handlers = handlers
    }

    /// Cancelling a borrower cancels only its wait, never the shared launch.
    /// Launch failure and EOF are terminal; no business requests are replayed.
    public func connection() async throws -> RuntimeBridgeConnection {
        guard !Task.isCancelled else { throw RuntimeBridgeError.cancelled }
        guard !closed else { throw RuntimeBridgeError.closed }
        guard let handlers else { throw RuntimeBridgeSessionError.notConfigured }
        if let outcome {
            let connection = try outcome.get()
            guard !connection.isClosed else { throw RuntimeBridgeError.closed }
            return connection
        }
        let id = UUID()
        let connection = try await withTaskCancellationHandler {
            try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<RuntimeBridgeConnection, Error>) in
                guard !Task.isCancelled else {
                    continuation.resume(throwing: RuntimeBridgeError.cancelled); return
                }
                waiters[id] = continuation
                if launch == nil {
                    launch = Task {
                        let result: Result<RuntimeBridgeConnection, Error>
                        do { result = .success(try await launcher.connect(handlers: handlers)) }
                        catch { result = .failure(error) }
                        await finish(result)
                    }
                }
            }
        } onCancel: {
            Task { await self.cancel(id) }
        }
        guard !Task.isCancelled else { throw RuntimeBridgeError.cancelled }
        return connection
    }

    /// Waits even for a launcher that ignores cancellation and returns late.
    /// Repeated close calls share the same launch/drain completion.
    public func close() async {
        closed = true
        let pending = waiters
        waiters.removeAll()
        for waiter in pending.values { waiter.resume(throwing: RuntimeBridgeError.closed) }
        launch?.cancel()
        if case .success(let connection) = outcome { await connection.closeAndWait() }
        await launch?.value
        await launcher.waitForExit()
    }

    private func cancel(_ id: UUID) {
        waiters.removeValue(forKey: id)?.resume(throwing: RuntimeBridgeError.cancelled)
    }

    private func finish(_ result: Result<RuntimeBridgeConnection, Error>) async {
        outcome = result
        if closed {
            if case .success(let connection) = result { await connection.closeAndWait() }
            return
        }
        let pending = waiters
        waiters.removeAll()
        for waiter in pending.values {
            if case .success(let connection) = result, connection.isClosed {
                waiter.resume(throwing: RuntimeBridgeError.closed)
            } else { waiter.resume(with: result) }
        }
    }
}
