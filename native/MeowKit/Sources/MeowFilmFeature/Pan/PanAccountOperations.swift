import Foundation

/// The App-owned account model retains real write/start/cancel receipts across
/// page lifetimes. Admission is synchronous; presentation tasks never cancel it.
@MainActor
final class PanAccountOperations {
    private struct Pending {
        let id: UUID
        let drain: Task<Void, Never>
    }
    private var pending: [PanProviderID: Pending] = [:]

    func perform<Value: Sendable>(_ provider: PanProviderID,
                                 operation: @escaping @MainActor @Sendable () async throws -> Value) -> Task<Value, Error> {
        let prior = pending[provider]?.drain
        let id = UUID()
        // Admission happens synchronously with the user's action, before any
        // page can close or enqueue a successor. This task is never UI-cancelled.
        let work = Task {
            await prior?.value
            return try await operation()
        }
        let drain = Task { [weak self] in
            _ = await work.result
            if self?.pending[provider]?.id == id { self?.pending[provider] = nil }
        }
        pending[provider] = Pending(id: id, drain: drain)
        return work
    }

    func drain(_ provider: PanProviderID) -> Task<Void, Never>? { pending[provider]?.drain }
    func drainAll() -> Task<Void, Never>? {
        let drains = pending.values.map(\.drain)
        guard !drains.isEmpty else { return nil }
        return Task { for drain in drains { await drain.value } }
    }
}
