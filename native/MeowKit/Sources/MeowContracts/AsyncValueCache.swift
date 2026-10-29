import Foundation

/// Scope-owned, memory-only cache. Identity and validation belong to its caller.
/// Each consumer can cancel independently; the last cancellation stops the read.
public actor AsyncValueCache<Key: Hashable & Sendable, Value: Sendable> {
    public enum Access: Sendable, Equatable { case hit, miss, coalesced }
    private struct Entry { let value: Value; let expires: Date; let sequence: UInt64; let cost: Int }
    private struct Flight {
        let id: UUID
        let task: Task<Void, Never>
        var waiters: [UUID: CheckedContinuation<Value, Error>]
    }
    private var entries: [Key: Entry] = [:]
    private var flights: [Key: Flight] = [:]
    /// A cancelled loader can ignore cooperative cancellation. It no longer
    /// owns a key or waiter, but remains charged against the physical IO budget
    /// until its task actually returns.
    private var retiredFlights: Set<UUID> = []
    private var sequence: UInt64 = 0
    private let capacity: Int
    private let maximumCost: Int, maximumFlights: Int
    private let cost: @Sendable (Value) -> Int
    private let now: @Sendable () -> Date
    public init(capacity: Int, maximumCost: Int = .max, maximumFlights: Int = 32,
                cost: @escaping @Sendable (Value) -> Int = { _ in 1 },
                clock: @escaping @Sendable () -> Date = { Date() }) {
        self.capacity = max(1, capacity); now = clock
        self.maximumCost = max(1, maximumCost); self.maximumFlights = max(1, maximumFlights); self.cost = cost
    }
    public func value(for key: Key, lifetime: TimeInterval,
                      observe: @escaping @Sendable (Access) -> Void = { _ in },
                      load: @escaping @Sendable () async throws -> Value) async throws -> Value {
        try Task.checkCancellation()
        entries = entries.filter { $0.value.expires > now() }
        if let entry = entries[key] { observe(.hit); return entry.value }
        let waiter = UUID()
        let value = try await withTaskCancellationHandler {
            try Task.checkCancellation()
            return try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<Value, Error>) in
                if flights[key] != nil {
                    guard flights[key]!.waiters.count < 128 else {
                        continuation.resume(throwing: RequestAdmission.Failure.queueFull); return
                    }
                    observe(.coalesced)
                    flights[key]?.waiters[waiter] = continuation
                } else {
                    guard flights.count + retiredFlights.count < maximumFlights else {
                        continuation.resume(throwing: RequestAdmission.Failure.queueFull); return
                    }
                    observe(.miss)
                    let id = UUID()
                    let task = Task {
                        let result: Result<Value, Error>
                        do { result = .success(try await load()) } catch { result = .failure(error) }
                        self.finish(key: key, id: id, result: result, lifetime: lifetime)
                    }
                    flights[key] = .init(id: id, task: task, waiters: [waiter: continuation])
                }
            }
        } onCancel: {
            Task { await self.cancel(key: key, waiter: waiter) }
        }
        try Task.checkCancellation()
        return value
    }
    public func invalidate(_ key: Key? = nil) {
        let keys = key.map { [$0] } ?? Array(Set(entries.keys).union(flights.keys))
        for key in keys {
            entries.removeValue(forKey: key)
            if let flight = flights.removeValue(forKey: key) {
                retiredFlights.insert(flight.id)
                flight.task.cancel()
                for waiter in flight.waiters.values { waiter.resume(throwing: CancellationError()) }
            }
        }
    }
    private func cancel(key: Key, waiter: UUID) {
        guard var flight = flights[key], let continuation = flight.waiters.removeValue(forKey: waiter) else { return }
        if flight.waiters.isEmpty {
            flights.removeValue(forKey: key)
            retiredFlights.insert(flight.id)
            flight.task.cancel()
        }
        else { flights[key] = flight }
        continuation.resume(throwing: CancellationError())
    }
    private func finish(key: Key, id: UUID, result: Result<Value, Error>, lifetime: TimeInterval) {
        guard let flight = flights[key], flight.id == id else {
            retiredFlights.remove(id)
            return
        }
        flights.removeValue(forKey: key)
        if case .success(let value) = result, lifetime.isFinite, lifetime > 0 {
            let valueCost = cost(value)
            if valueCost >= 0, valueCost <= maximumCost {
                sequence &+= 1
                entries[key] = .init(value: value, expires: now().addingTimeInterval(min(lifetime, 600)),
                                     sequence: sequence, cost: valueCost)
                while entries.count > capacity || entries.values.reduce(0.0, { $0 + Double($1.cost) }) > Double(maximumCost),
                      let oldest = entries.min(by: { $0.value.sequence < $1.value.sequence })?.key {
                    entries.removeValue(forKey: oldest)
                }
            }
        }
        for waiter in flight.waiters.values { waiter.resume(with: result) }
    }
}
