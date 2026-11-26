#if os(iOS) || os(macOS)
import Foundation
import Combine

@MainActor
public final class MusicHomeSourceSettingsModel: ObservableObject {
    @Published public private(set) var selection: MusicHomeSourceSelection?
    @Published public private(set) var isSaving = false
    public var onError: ((String) -> Void)?
    private let model: MusicViewModel
    private var active = false
    private var request: UUID?
    private var work: Task<Void, Never>?

    public init(model: MusicViewModel) { self.model = model }

    public func activate() {
        guard !active else { return }
        active = true; loadSelection()
    }

    public func deactivate() { active = false; invalidate() }

    public func select(_ platform: MusicPublicBrowsePlatform) {
        guard active, !model.isClosed,
              let service = model.service, let current = selection, current.selected != platform,
              current.available.contains(platform), !isSaving else { return }
        let id = UUID(); request = id; isSaving = true
        work = Task { [weak self] in
            do {
                let value = try await service.selectHomeSource(platform, ifRevision: current.revision)
                guard let self, isCurrent(id) else { return }
                work = nil; selection = value; isSaving = false
            } catch {
                let value = try? await service.homeSourceSelection()
                guard let self, isCurrent(id) else { return }
                work = nil; selection = value; isSaving = false
                report(error, operation: "保存数据源")
            }
        }
    }

    private func loadSelection() {
        invalidate()
        guard active, !model.isClosed, let service = model.service else { return }
        let id = UUID(); request = id
        work = Task { [weak self] in
            do {
                let value = try await service.homeSourceSelection()
                guard let self, isCurrent(id) else { return }
                work = nil; selection = value
            } catch {
                guard let self, isCurrent(id) else { return }
                work = nil; selection = nil; report(error, operation: "读取数据源")
            }
        }
    }

    private func isCurrent(_ id: UUID) -> Bool {
        active && request == id && !model.isClosed && !Task.isCancelled
    }

    private func invalidate() {
        request = nil; work?.cancel(); work = nil; selection = nil; isSaving = false
    }

    private func report(_ error: Error, operation: String) {
        let failure = MusicServiceError.sanitized(error)
        if failure != .cancelled { onError?(failure.message(operation: operation)) }
    }

    deinit { work?.cancel() }
}
#endif
