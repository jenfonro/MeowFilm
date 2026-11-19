import Foundation

/// The App-owned account model retains real online start/cancel receipts across
/// page lifetimes. Admission is synchronous; presentation tasks never cancel it.
@MainActor
final class PanAccountOperations {
    private struct Key: Hashable {
        let provider: PanProviderID
        let local: Bool
    }
    private struct Pending {
        let id: UUID
        let drain: Task<Void, Never>
    }
    private var pending: [Key: Pending] = [:]

    func perform<Value: Sendable>(_ provider: PanProviderID,
                                 operation: @escaping @MainActor @Sendable () async throws -> Value) -> Task<Value, Error> {
        enqueue(Key(provider: provider, local: false), operation: operation)
    }

    /// A local credential edit must not wait for a retired network start/cancel.
    /// Both lanes remain retained until their actual execution has finished.
    func performLocal<Value: Sendable>(_ provider: PanProviderID,
                                      operation: @escaping @MainActor @Sendable () async throws -> Value) -> Task<Value, Error> {
        enqueue(Key(provider: provider, local: true), operation: operation)
    }

    private func enqueue<Value: Sendable>(_ key: Key,
                                         operation: @escaping @MainActor @Sendable () async throws -> Value) -> Task<Value, Error> {
        let prior = pending[key]?.drain
        let id = UUID()
        // Admission happens synchronously with the user's action, before any
        // page can close or enqueue a successor. This task is never UI-cancelled.
        let work = Task {
            await prior?.value
            return try await operation()
        }
        let drain = Task { [weak self] in
            _ = await work.result
            if self?.pending[key]?.id == id { self?.pending[key] = nil }
        }
        pending[key] = Pending(id: id, drain: drain)
        return work
    }

    func drain(_ provider: PanProviderID) -> Task<Void, Never>? {
        let drains = pending.filter { $0.key.provider == provider }.map { $0.value.drain }
        guard !drains.isEmpty else { return nil }
        return Task { for drain in drains { await drain.value } }
    }
    func drainAll() -> Task<Void, Never>? {
        let drains = pending.values.map(\.drain)
        guard !drains.isEmpty else { return nil }
        return Task { for drain in drains { await drain.value } }
    }
}
