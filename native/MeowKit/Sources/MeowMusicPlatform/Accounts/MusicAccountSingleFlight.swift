import Foundation
import MeowMusicFeature

private enum MusicAccountFlightContext {
    @TaskLocal static var id: UUID?
}

/// Deduplicates one exact read, not the permissions of its callers. Each caller
/// still validates its own account/selection before and after waiting.
/// Cancellation is per waiter; only the final departure cancels the work.
/// The account owner's run() retains the physical HTTP slot until I/O settles.
final class MusicAccountSingleFlight<Key: Hashable & Sendable, Value: Sendable>: @unchecked Sendable {
    private final class Flight {
        let id = UUID()
        var waiters: [UUID: MusicAccountOperation<Value>] = [:]
        var task: Task<Void, Never>?
    }
    private let lock = NSLock()
    private var flights: [Key: Flight] = [:]
    private let joined: (@Sendable (Key) -> Void)?

    init(onJoin: (@Sendable (Key) -> Void)? = nil) { joined = onJoin }

    /// Credential-free diagnostics, also used to synchronize deterministic
    /// consumer cancellation tests without sleeps or assumptions about scheduling.
    var waiterCount: Int {
        lock.lock(); defer { lock.unlock() }
        return flights.values.reduce(0) { $0 + $1.waiters.count }
    }

    private func enroll(_ key: Key, id: UUID, gate: MusicAccountOperation<Value>) throws -> (UUID, Bool) {
        lock.lock(); defer { lock.unlock() }
        if let flight = flights[key] {
            guard flight.waiters.count < 64 else { throw MusicServiceError.busy }
            flight.waiters[id] = gate
            return (flight.id, false)
        }
        guard flights.count < 24 else { throw MusicServiceError.busy }
        let flight = Flight()
        flight.waiters[id] = gate; flights[key] = flight
        return (flight.id, true)
    }
    private func install(_ task: Task<Void, Never>, key: Key, flightID: UUID) {
        lock.lock()
        if let flight = flights[key], flight.id == flightID {
            flight.task = task
            lock.unlock()
        } else { lock.unlock(); task.cancel() }
    }
    private func finish(_ result: Result<Value, Error>, key: Key, flightID: UUID) {
        lock.lock()
        guard let flight = flights[key], flight.id == flightID else { lock.unlock(); return }
        flights.removeValue(forKey: key)
        let waiters = Array(flight.waiters.values)
        lock.unlock()
        for waiter in waiters { waiter.finish(result) }
    }
    private func cancel(_ key: Key, flightID: UUID, waiterID: UUID) {
        lock.lock()
        guard let flight = flights[key], flight.id == flightID,
              let waiter = flight.waiters.removeValue(forKey: waiterID) else { lock.unlock(); return }
        let task = flight.waiters.isEmpty ? flight.task : nil
        if flight.waiters.isEmpty { flights.removeValue(forKey: key) }
        lock.unlock()
        waiter.finish(.failure(MusicServiceError.cancelled))
        task?.cancel()
    }
    func cancel(excludingCurrent: Bool = false, where predicate: (Key) -> Bool = { _ in true }) {
        lock.lock()
        // A confirmed authentication failure must finish its own revoke/CAS
        // before delivering the precise failure to all same-flight waiters.
        // Cancel every OTHER stale flight, but never self-cancel that cleanup.
        let current = excludingCurrent ? MusicAccountFlightContext.id : nil
        let keys = flights.keys.filter { predicate($0) && flights[$0]?.id != current }
        let retired = keys.compactMap { flights.removeValue(forKey: $0) }
        lock.unlock()
        for flight in retired {
            for waiter in flight.waiters.values { waiter.finish(.failure(MusicServiceError.cancelled)) }
            flight.task?.cancel()
        }
    }
    func value(for key: Key, trace: MusicRequestTrace? = nil,
               operation: @escaping @Sendable () async throws -> Value) async throws -> Value {
        try Task.checkCancellation()
        let waiterID = UUID(), gate = MusicAccountOperation<Value>()
        let (flightID, start) = try enroll(key, id: waiterID, gate: gate)
        trace?.record(start ? .cacheMiss : .coalesced)
        joined?(key)
        if start {
            let task = Task { [weak self] in
                let result: Result<Value, Error>
                do {
                    try Task.checkCancellation()
                    result = .success(try await MusicAccountFlightContext.$id.withValue(flightID) {
                        try await operation()
                    })
                } catch { result = .failure(error) }
                self?.finish(result, key: key, flightID: flightID)
            }
            install(task, key: key, flightID: flightID)
        }
        let value = try await withTaskCancellationHandler {
            try await gate.value()
        } onCancel: { [self] in
            // Finish immediately even if physical work won its completion race.
            gate.finish(.failure(MusicServiceError.cancelled))
            cancel(key, flightID: flightID, waiterID: waiterID)
        }
        try Task.checkCancellation()
        return value
    }
    deinit { cancel() }
}
