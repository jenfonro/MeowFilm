import Foundation

/// Bounded FIFO admission across hosts. A lease covers physical work, including
/// cancellation teardown; the caller releases it only after that work finishes.
/// This is a concurrency budget, not a requests-per-minute policy.
public actor RequestAdmission {
    public enum Failure: Error, Sendable, Equatable { case closed, queueFull, timedOut, coolingDown }
    public enum Priority: Sendable, Equatable { case normal, foreground }
    private struct Lease { let host: String; let scope: UUID? }
    private struct Waiter {
        let id: UUID
        let host: String
        let scope: UUID?
        let priority: Priority
        let continuation: CheckedContinuation<UUID, Error>
        let timer: Task<Void, Never>
    }
    private let maximum: Int, perHost: Int, waitingLimit: Int
    private let waitNanoseconds: UInt64
    private var active: [UUID: Lease] = [:]
    private var waiting: [Waiter] = []
    private var cooldowns: [String: Date] = [:]
    private var closed = false
    private var closedScopes = Set<UUID>()
    private var foregroundBurst = 0
    private let now: @Sendable () -> Date

    public init(maximum: Int = 6, perHost: Int = 2, waitingLimit: Int = 96, waitSeconds: Double = 25,
                clock: @escaping @Sendable () -> Date = { Date() }) {
        self.maximum = max(1, maximum); self.perHost = max(1, perHost)
        self.waitingLimit = max(1, waitingLimit)
        waitNanoseconds = UInt64(max(0.01, min(60, waitSeconds.isFinite ? waitSeconds : 25)) * 1_000_000_000)
        now = clock
    }
    public func acquire(host: String, scope: UUID? = nil, priority: Priority = .normal) async throws -> UUID {
        try Task.checkCancellation()
        guard !closed, scope.map({ !closedScopes.contains($0) }) ?? true else { throw Failure.closed }
        let instant = now()
        cooldowns = cooldowns.filter { $0.value > instant }
        guard cooldowns[host] == nil else { throw Failure.coolingDown }
        let id = UUID()
        if available(host), waiting.isEmpty {
            active[id] = .init(host: host, scope: scope); return id
        }
        guard waiting.count < waitingLimit else { throw Failure.queueFull }
        let lease = try await withTaskCancellationHandler {
            try Task.checkCancellation()
            return try await withCheckedThrowingContinuation { continuation in
                let timer = Task { [weak self, waitNanoseconds] in
                    do { try await Task.sleep(nanoseconds: waitNanoseconds) } catch { return }
                    await self?.remove(id, error: Failure.timedOut)
                }
                waiting.append(.init(id: id, host: host, scope: scope, priority: priority,
                                     continuation: continuation, timer: timer))
                drain()
            }
        } onCancel: {
            Task { await self.remove(id, error: CancellationError()) }
        }
        // Cancellation can win after drain resumed a waiter but before the
        // suspended caller resumed. Do not leak that newly assigned lease.
        if Task.isCancelled { release(lease); throw CancellationError() }
        return lease
    }
    public func release(_ id: UUID) {
        active.removeValue(forKey: id)
        drain()
    }
    /// Only a server-confirmed limit calls this. No request is retried here.
    public func coolDown(host: String, seconds: TimeInterval) {
        guard !closed else { return }
        let until = now().addingTimeInterval(max(1, min(300, seconds.isFinite ? seconds : 2)))
        cooldowns[host] = max(cooldowns[host] ?? .distantPast, until)
        let rejected = waiting.filter { $0.host == host }
        waiting.removeAll { $0.host == host }
        for value in rejected {
            value.timer.cancel(); value.continuation.resume(throwing: Failure.coolingDown)
        }
        drain()
    }
    public func close() {
        closed = true
        let rejected = waiting; waiting.removeAll(); cooldowns.removeAll()
        for value in rejected {
            value.timer.cancel(); value.continuation.resume(throwing: Failure.closed)
        }
    }
    /// An injected product budget may be shared by several transports. Closing
    /// one transport revokes only its waiters, never another owner's requests.
    public func close(scope: UUID) {
        closedScopes.insert(scope)
        let rejected = waiting.filter { $0.scope == scope }
        waiting.removeAll { $0.scope == scope }
        for value in rejected {
            value.timer.cancel(); value.continuation.resume(throwing: Failure.closed)
        }
        drain()
    }
    public var counts: (active: Int, waiting: Int) { (active.count, waiting.count) }
    private func available(_ host: String) -> Bool {
        active.count < maximum && active.values.filter { $0.host == host }.count < perHost
    }
    private func remove(_ id: UUID, error: Error) {
        guard let index = waiting.firstIndex(where: { $0.id == id }) else { return }
        let value = waiting.remove(at: index)
        value.timer.cancel(); value.continuation.resume(throwing: error)
        drain()
    }
    private func drain() {
        guard !closed else { return }
        // FIFO within each lane; at most three foreground requests can pass an
        // eligible ordinary read. Playback is prompt without starving browsing.
        while let first = waiting.firstIndex(where: { available($0.host) }) {
            let normal = waiting.firstIndex { $0.priority == .normal && available($0.host) }
            let foreground = waiting.firstIndex { $0.priority == .foreground && available($0.host) }
            let index = foregroundBurst < 3 ? (foreground ?? normal ?? first) : (normal ?? foreground ?? first)
            let value = waiting.remove(at: index)
            foregroundBurst = value.priority == .foreground ? min(3, foregroundBurst + 1) : 0
            value.timer.cancel(); active[value.id] = .init(host: value.host, scope: value.scope)
            value.continuation.resume(returning: value.id)
        }
    }

    public nonisolated static func retryAfter(_ value: String?, now: Date = Date()) -> TimeInterval? {
        guard let value, value.utf8.count <= 128 else { return nil }
        let text = value.trimmingCharacters(in: .whitespacesAndNewlines)
        if !text.isEmpty, text.utf8.allSatisfy({ (48...57).contains($0) }), let seconds = Double(text), seconds.isFinite {
            return min(300, max(0, seconds))
        }
        let parser = DateFormatter()
        parser.locale = Locale(identifier: "en_US_POSIX"); parser.timeZone = TimeZone(secondsFromGMT: 0)
        parser.dateFormat = "EEE',' dd MMM yyyy HH':'mm':'ss 'GMT'"
        guard let date = parser.date(from: text) else { return nil }
        return min(300, max(0, date.timeIntervalSince(now)))
    }
}
