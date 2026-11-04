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
    private var sequence: UInt64 = 0
    private let capacity: Int?
    private let maximumCost: Int
    private let cost: @Sendable (Value) -> Int
    private let now: @Sendable () -> Date
    /// `nil` capacity keeps only the cost budget; a finite capacity is still
    /// normalized to at least one entry.
    public init(capacity: Int?, maximumCost: Int = .max,
                cost: @escaping @Sendable (Value) -> Int = { _ in 1 },
                clock: @escaping @Sendable () -> Date = { Date() }) {
        self.capacity = capacity.map { max(1, $0) }; now = clock
        self.maximumCost = max(1, maximumCost); self.cost = cost
    }
    public func value(for key: Key, lifetime: TimeInterval,
                      observe: @escaping @Sendable (Access) -> Void = { _ in },
                      load: @escaping @Sendable () async throws -> Value) async throws -> Value {
        try Task.checkCancellation()
        entries = entries.filter { $0.value.expires > now() }
        if let entry = entries[key] {
            sequence &+= 1
            entries[key] = .init(value: entry.value, expires: entry.expires, sequence: sequence, cost: entry.cost)
            observe(.hit); return entry.value
        }
        let waiter = UUID()
        let value = try await withTaskCancellationHandler {
            try Task.checkCancellation()
            return try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<Value, Error>) in
                if flights[key] != nil {
                    observe(.coalesced)
                    flights[key]?.waiters[waiter] = continuation
                } else {
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
                flight.task.cancel()
                for waiter in flight.waiters.values { waiter.resume(throwing: CancellationError()) }
            }
        }
    }
    private func cancel(key: Key, waiter: UUID) {
        guard var flight = flights[key], let continuation = flight.waiters.removeValue(forKey: waiter) else { return }
        if flight.waiters.isEmpty {
            flights.removeValue(forKey: key)
            flight.task.cancel()
        }
        else { flights[key] = flight }
        continuation.resume(throwing: CancellationError())
    }
    private func finish(key: Key, id: UUID, result: Result<Value, Error>, lifetime: TimeInterval) {
        guard let flight = flights[key], flight.id == id else { return }
        flights.removeValue(forKey: key)
        if case .success(let value) = result, lifetime.isFinite, lifetime > 0 {
            let valueCost = cost(value)
            if valueCost >= 0, valueCost <= maximumCost {
                sequence &+= 1
                entries[key] = .init(value: value, expires: now().addingTimeInterval(lifetime),
                                     sequence: sequence, cost: valueCost)
                while (capacity.map { entries.count > $0 } ?? false) ||
                      entries.values.reduce(0.0, { $0 + Double($1.cost) }) > Double(maximumCost),
                      let oldest = entries.min(by: { $0.value.sequence < $1.value.sequence })?.key {
                    entries.removeValue(forKey: oldest)
                }
            }
        }
        for waiter in flight.waiters.values { waiter.resume(with: result) }
    }
}
