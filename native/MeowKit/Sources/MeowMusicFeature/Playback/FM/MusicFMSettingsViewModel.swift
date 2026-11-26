#if os(iOS) || os(macOS)
import Foundation
import Combine

@MainActor
final class MusicFMSettingsViewModel: ObservableObject {
    @Published private(set) var pendingChoice: String?
    @Published private(set) var error: String?
    @Published private(set) var selectionCompleted = false
    @Published private(set) var selection: MusicHomeAction?
    private let owner: MusicViewModel
    private let requireActiveFM: Bool
    private var generation: UUID?
    private var closed = false
    private var work: Task<Void, Never>?
    private var workID: UUID?
    private var lastFailure: String?
    private var reloadRequested = false

    init(owner: MusicViewModel, requireActiveFM: Bool = false) {
        self.owner = owner
        self.requireActiveFM = requireActiveFM
        selection = owner.activeHomeFM
    }

    var hasAccess: Bool {
        !closed && generation == owner.searchAccessGeneration && !owner.isClosed
    }

    func syncAccess() {
        guard !closed, !owner.isClosed, generation != owner.searchAccessGeneration else { return }
        generation = owner.searchAccessGeneration
        invalidate()
        readSelection()
    }

    func select(_ choice: MusicFMModeChoice) {
        guard hasAccess, work == nil, !requireActiveFM || owner.isFMActive,
              !owner.isFMActive || !choice.matches(selection) || lastFailure != nil else { return }
        let id = UUID(), access = owner.searchAccessGeneration
        pendingChoice = choice.id
        workID = id; error = nil; lastFailure = nil; selectionCompleted = false
        work = Task { [weak self] in
            guard let self else { return }
            defer { finish(id) }
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
                self.error = failure.localizedDescription
            }
        }
    }

    private func readSelection() {
        guard hasAccess else { return }
        guard work == nil else { reloadRequested = true; return }
        guard let service = owner.service else { selection = owner.activeHomeFM; return }
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
        pendingChoice = nil; work = nil; workID = nil
        if reloadRequested { reloadRequested = false; readSelection() }
    }

    func dismissError(_ value: String) { if error == value { error = nil } }
    func acknowledgeSelection() { selectionCompleted = false }
    func close() { guard !closed else { return }; closed = true; invalidate() }

    private func current(_ id: UUID, access: UUID) -> Bool {
        hasAccess && !Task.isCancelled && workID == id &&
            owner.searchAccessGeneration == access && (!requireActiveFM || owner.isFMActive)
    }

    private func invalidate() {
        pendingChoice = nil; work?.cancel(); reloadRequested = false
        error = nil; lastFailure = nil; selectionCompleted = false; selection = nil
    }

    deinit { work?.cancel() }
}
#endif
