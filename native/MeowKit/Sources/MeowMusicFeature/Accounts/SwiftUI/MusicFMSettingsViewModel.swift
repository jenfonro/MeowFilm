#if os(iOS) || os(macOS)
import Foundation
import Combine

@MainActor
final class MusicFMSettingsViewModel: ObservableObject {
    @Published private var request: (id: UUID, access: UUID, choice: String)?
    @Published private(set) var error: String?
    @Published private(set) var selectionCompleted = false
    private let owner: MusicViewModel
    private var generation: UUID?
    private var available = false
    private var closed = false
    private var work: Task<Void, Never>?
    private var lastFailure: String?

    init(owner: MusicViewModel) { self.owner = owner }

    var pendingChoice: String? { request?.choice }
    var hasAccess: Bool {
        !closed && available && generation == owner.accountAccessGeneration && owner.isAvailable && !owner.isClosed
    }

    func setAccess(available: Bool, generation: UUID) {
        guard !closed, self.available != available || self.generation != generation else { return }
        self.available = available
        self.generation = generation
        invalidate()
    }

    func select(_ choice: MusicFMModeChoice) {
        guard hasAccess, work == nil, owner.isFMActive,
              !choice.matches(owner.activeHomeFM) || lastFailure != nil else { return }
        let id = UUID(), access = owner.accountAccessGeneration
        request = (id, access, choice.id)
        error = nil
        lastFailure = nil
        selectionCompleted = false
        work = Task { [weak self] in
            guard let self else { return }
            defer {
                if request?.id == id {
                    request = nil
                    work = nil
                }
            }
            guard current(id, access: access) else { return }
            do {
                try await owner.playHomeAction(choice.action, title: choice.title, forceReload: true)
                guard current(id, access: access) else { return }
                guard choice.matches(owner.activeHomeFM), owner.isFMActive else { throw MusicServiceError.staleResult }
                selectionCompleted = true
            } catch {
                guard current(id, access: access) else { return }
                let failure = MusicServiceError.sanitized(error).localizedDescription
                lastFailure = failure
                self.error = failure
            }
        }
    }

    func dismissError(_ value: String) {
        if error == value { error = nil }
    }

    func acknowledgeSelection() { selectionCompleted = false }

    func close() {
        closed = true
        invalidate()
    }

    private func current(_ id: UUID, access: UUID) -> Bool {
        hasAccess && !Task.isCancelled && request?.id == id && request?.access == access &&
            owner.accountAccessGeneration == access
    }

    private func invalidate() {
        request = nil
        work?.cancel()
        work = nil
        error = nil
        lastFailure = nil
        selectionCompleted = false
    }

    deinit { work?.cancel() }
}
#endif
