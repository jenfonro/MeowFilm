#if os(iOS) || os(macOS)
import Foundation
import Combine

@MainActor
final class MusicFMSettingsViewModel: ObservableObject {
    @Published private var request: (id: UUID, access: UUID, choice: String)?
    @Published private(set) var error: String?
    @Published private(set) var selectionCompleted = false
    @Published private(set) var selection: MusicHomeAction?
    private let owner: MusicViewModel
    private var generation: UUID?
    private var available = false
    private var closed = false
    private var work: Task<Void, Never>?
    private var workID: UUID?
    private var lastFailure: String?
    private var reloadRequested = false

    init(owner: MusicViewModel) { self.owner = owner; selection = owner.activeHomeFM }

    var pendingChoice: String? { request?.choice }
    var hasAccess: Bool {
        !closed && available && generation == owner.searchAccessGeneration && owner.isAvailable && !owner.isClosed
    }

    func setAccess(available: Bool, generation: UUID) {
        guard !closed, self.available != available || self.generation != generation else { return }
        self.available = available
        self.generation = generation
        invalidate()
        readSelection()
    }

    func select(_ choice: MusicFMModeChoice) {
        guard hasAccess, work == nil, owner.isFMActive,
              !choice.matches(selection) || lastFailure != nil else { return }
        let id = UUID(), access = owner.searchAccessGeneration
        request = (id, access, choice.id)
        workID = id
        error = nil
        lastFailure = nil
        selectionCompleted = false
        work = Task { [weak self] in
            guard let self else { return }
            defer { finish(id) }
            guard current(id, access: access) else { return }
            do {
                try await owner.playHomeAction(choice.action, title: choice.title, forceReload: true)
                guard current(id, access: access) else { return }
                guard choice.matches(owner.activeHomeFM), owner.isFMActive else { throw MusicServiceError.staleResult }
                selection = owner.activeHomeFM
                selectionCompleted = true
            } catch {
                guard current(id, access: access) else { return }
                let failure = MusicServiceError.sanitized(error)
                guard failure != .cancelled else { return }
                lastFailure = failure.localizedDescription
                if failure != .commitUnconfirmed { self.error = failure.localizedDescription }
                else { reloadRequested = true }
            }
        }
    }

    private func readSelection() {
        guard hasAccess else { return }
        guard work == nil else { reloadRequested = true; return }
        guard let service = owner.service as? any MusicFMPlaybackServing else {
            selection = owner.activeHomeFM
            return
        }
        let id = UUID(), access = owner.searchAccessGeneration
        workID = id
        work = Task { [weak self] in
            guard let self else { return }
            defer { finish(id) }
            do {
                let value = try await service.rememberedFM()
                guard hasAccess, !Task.isCancelled, workID == id, generation == access else { return }
                selection = value
            } catch {
                guard hasAccess, !Task.isCancelled, workID == id, generation == access else { return }
                let failure = MusicServiceError.sanitized(error)
                if failure != .cancelled { self.error = failure.localizedDescription }
            }
        }
    }

    private func finish(_ id: UUID) {
        guard workID == id else { return }
        request = nil; work = nil; workID = nil
        if reloadRequested {
            reloadRequested = false
            readSelection()
        }
    }

    func dismissError(_ value: String) {
        if error == value { error = nil }
    }

    func acknowledgeSelection() { selectionCompleted = false }
    func finishPending() async { while let task = work { await task.value } }

    func close() {
        guard !closed else { return }
        closed = true
        invalidate()
    }

    private func current(_ id: UUID, access: UUID) -> Bool {
        hasAccess && !Task.isCancelled && request?.id == id && request?.access == access &&
            owner.searchAccessGeneration == access && owner.isFMActive
    }

    private func invalidate() {
        request = nil
        work?.cancel()
        reloadRequested = false
        error = nil
        lastFailure = nil
        selectionCompleted = false
        selection = nil
    }

    deinit { work?.cancel() }
}
#endif
