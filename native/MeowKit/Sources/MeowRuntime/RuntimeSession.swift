import Foundation

public protocol RuntimeLaunching: Sendable {
    func connect() async throws -> RuntimeHTTPClient
    func close() async
    func waitForExit() async
}

/// App-owned, single-launch runtime shared by independent business modules.
public actor RuntimeSession {
    private let launcher: any RuntimeLaunching
    private var launch: Task<Void, Never>?
    private var outcome: Result<RuntimeHTTPClient, Error>?
    private var waiters: [UUID: CheckedContinuation<RuntimeHTTPClient, Error>] = [:]
    private var stopping: Task<Void, Never>?
    private var closed = false
    private nonisolated let notificationLifetime = RuntimeSettingsNotificationLifetime()
    private var notifications: [UUID: Task<Void, Never>] = [:]

    public init(launcher: any RuntimeLaunching) { self.launcher = launcher }

    /// Cancelling a borrower cancels its wait, never the shared startup.
    public func connection() async throws -> RuntimeHTTPClient {
        guard !Task.isCancelled else { throw RuntimeHTTPError.cancelled }
        guard !closed else { throw RuntimeHTTPError.closed }
        if let outcome {
            let client = try outcome.get()
            guard !client.isClosed else { throw RuntimeHTTPError.closed }
            return client
        }
        let id = UUID()
        let client = try await withTaskCancellationHandler {
            try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<RuntimeHTTPClient, Error>) in
                guard !Task.isCancelled else {
                    continuation.resume(throwing: RuntimeHTTPError.cancelled); return
                }
                waiters[id] = continuation
                if launch == nil {
                    notificationLifetime.begin()
                    launch = Task {
                        let result: Result<RuntimeHTTPClient, Error>
                        do { result = .success(try await launcher.connect()) }
                        catch { result = .failure(error) }
                        await finish(result)
                    }
                }
            }
        } onCancel: {
            Task { await self.cancel(id) }
        }
        guard !Task.isCancelled else { throw RuntimeHTTPError.cancelled }
        guard !closed, !client.isClosed else { throw RuntimeHTTPError.closed }
        return client
    }

    /// Synchronous admission drops offline changes immediately. It neither
    /// launches a runtime nor makes a settings save wait for a connection.
    public nonisolated func settingsChanged(_ block: String) {
        guard let generation = notificationLifetime.current() else { return }
        Task { await self.notify(block, generation: generation) }
    }

    /// Observation may borrow an existing startup, but can never initiate one.
    public func connectionIfStarted() async -> RuntimeHTTPClient? {
        guard !closed, launch != nil else { return nil }
        return try? await connection()
    }

    private func notify(_ block: String, generation: UUID) {
        guard !closed, notificationLifetime.current() == generation else { return }
        let startup = launch, id = UUID()
        notifications[id] = Task { [weak self] in
            await startup?.value
            if !Task.isCancelled, let client = await self?.notificationClient(generation) {
                try? await client.settingsChanged(block)
            }
            await self?.finishedNotification(id)
        }
    }

    private func notificationClient(_ generation: UUID) -> RuntimeHTTPClient? {
        guard !closed, notificationLifetime.current() == generation,
              case .success(let client)? = outcome, !client.isClosed else { return nil }
        return client
    }

    private func finishedNotification(_ id: UUID) { notifications[id] = nil }

    /// All callers wait for the same physical executor exit, even if a caller
    /// is cancelled or startup returns after shutdown has begun.
    public func close() async {
        if let stopping { await stopping.value; return }
        closed = true
        notificationLifetime.end()
        let notifications = Array(notifications.values)
        self.notifications.removeAll()
        for notification in notifications { notification.cancel() }
        let pending = waiters
        waiters.removeAll()
        for waiter in pending.values { waiter.resume(throwing: RuntimeHTTPError.closed) }
        let launcher = launcher, launch = launch, outcome = outcome
        let stopping = Task {
            if case .success(let client)? = outcome { await client.close() }
            await launcher.close()
            await launch?.value
            await launcher.waitForExit()
            for notification in notifications { await notification.value }
        }
        self.stopping = stopping
        await stopping.value
    }

    private func cancel(_ id: UUID) {
        waiters.removeValue(forKey: id)?.resume(throwing: RuntimeHTTPError.cancelled)
    }

    private func finish(_ result: Result<RuntimeHTTPClient, Error>) async {
        outcome = result
        if case .failure = result { notificationLifetime.end() }
        if closed {
            if case .success(let client) = result { await client.close() }
            return
        }
        let pending = waiters
        waiters.removeAll()
        for waiter in pending.values {
            if case .success(let client) = result, client.isClosed {
                waiter.resume(throwing: RuntimeHTTPError.closed)
            } else { waiter.resume(with: result) }
        }
    }
}

private final class RuntimeSettingsNotificationLifetime: @unchecked Sendable {
    private let lock = NSLock()
    private var generation: UUID?
    func begin() { lock.lock(); generation = UUID(); lock.unlock() }
    func end() { lock.lock(); generation = nil; lock.unlock() }
    func current() -> UUID? { lock.lock(); defer { lock.unlock() }; return generation }
}
