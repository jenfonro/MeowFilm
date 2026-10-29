import Foundation
import Combine

@MainActor public final class MusicImportHistoryViewModel: ObservableObject {
    @Published public private(set) var snapshot: MusicImportHistorySnapshot = .empty
    @Published public private(set) var busy = false
    @Published public private(set) var confirmationID: UUID?
    @Published public var error: String?
    private let service: any MusicImportHistoryProviding
    private let onChanged: @MainActor () async -> Void
    private var confirmation: MusicImportRollbackAuthorization?
    private var epoch = UUID()
    private var generation: UUID?
    private var available = false, foreground = false, closed = false

    public init(service: any MusicImportHistoryProviding, onChanged: @escaping @MainActor () async -> Void = {}) {
        self.service = service; self.onChanged = onChanged
    }
    public func access(available: Bool, generation: UUID) {
        guard !closed, !Task.isCancelled else { return }
        guard generation != self.generation || self.available != available else { return }
        self.generation = generation; self.available = available
        epoch = UUID(); cancelConfirmation(); snapshot = .empty; busy = false; error = nil
    }
    public func setForeground(_ value: Bool) {
        foreground = value
        if !value { cancelConfirmation(); epoch = UUID(); busy = false }
    }
    public func cancelConfirmation() {
        confirmation?.revoke(); confirmation = nil; confirmationID = nil
    }
    public func deactivate() {
        available = false; foreground = false; epoch = UUID()
        cancelConfirmation(); snapshot = .empty; error = nil; busy = false
    }
    public func close() { deactivate(); closed = true }
    private func current(_ value: UUID) -> Bool { !closed && available && foreground && epoch == value && !Task.isCancelled }
    public func load() async {
        guard !busy, current(epoch) else { return }
        let own = epoch; busy = true; error = nil; cancelConfirmation()
        defer { if epoch == own { busy = false } }
        do {
            let value = try await service.importHistory()
            guard current(own) else { return }
            snapshot = value
        } catch { if current(own) { self.error = message(error) } }
    }
    public func requestRollback() -> UUID? {
        guard !busy, current(epoch), case .imported(let batch, _, true) = snapshot else { return nil }
        cancelConfirmation()
        let value = MusicImportRollbackAuthorization(batchID: batch)
        confirmation = value; confirmationID = value.id
        return value.id
    }
    public func confirmRollback(_ id: UUID) async {
        guard !busy, current(epoch), let authorization = confirmation, authorization.id == id else { return }
        let own = epoch; busy = true; error = nil
        defer {
            authorization.revoke()
            if epoch == own {
                if confirmation === authorization { confirmation = nil; confirmationID = nil }
                busy = false
            }
        }
        do {
            try await service.rollbackImportedLibrary(authorization)
            guard current(own) else { return }
            let next = try await service.importHistory()
            guard current(own) else { return }
            snapshot = next
            await onChanged()
        } catch { if current(own) { self.error = message(error) } }
    }
    private func message(_ error: Error) -> String {
        (error as? MusicServiceError ?? .storageFailed).localizedDescription
    }
}
