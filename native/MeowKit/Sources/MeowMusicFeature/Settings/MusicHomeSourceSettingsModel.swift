#if os(iOS) || os(macOS)
import Foundation
import Combine

/// Each visible entry owns its presentation state; the existing home browser
/// remains the authority for the selection and its compare-and-swap revision.
@MainActor
public final class MusicHomeSourceSettingsModel: ObservableObject {
    @Published public private(set) var selection: MusicHomeSourceSelection?
    @Published public private(set) var isSaving = false
    public var onError: ((String) -> Void)?
    private let model: MusicViewModel
    private var active = false
    private var access: UUID?
    private var request: UUID?
    private var work: Task<Void, Never>?
    private var accessObservation: AnyCancellable?
    private var homeBrowser: (any MusicHomeBrowsing)? { model.publicBrowser as? any MusicHomeBrowsing }

    public init(model: MusicViewModel) {
        self.model = model
        // accessChanges originates in @Published willSet; read the committed scope.
        accessObservation = model.accessChanges.receive(on: RunLoop.main).sink { [weak self] in
            self?.reconcileAccess()
        }
    }

    public func activate() {
        guard !active else { return }
        active = true
        reconcileAccess()
    }

    public func deactivate() {
        active = false
        access = nil
        invalidate()
    }

    public func select(_ platform: MusicPublicBrowsePlatform) {
        guard active, model.isAvailable, !model.isClosed, access == model.accountAccessGeneration,
              let homeBrowser, let current = selection, current.selected != platform,
              current.available.contains(platform), !isSaving, let access else { return }
        let request = UUID()
        self.request = request
        isSaving = true
        work = Task { [weak self] in
            guard self?.isCurrent(request, access: access) == true else { return }
            do {
                let value = try await homeBrowser.selectHomeSource(platform, ifRevision: current.revision)
                guard let self, isCurrent(request, access: access) else { return }
                work = nil
                selection = value
                isSaving = false
            } catch {
                guard self?.isCurrent(request, access: access) == true else { return }
                // A failed acknowledgement can still have committed; never replay the write.
                let value = try? await homeBrowser.homeSourceSelection()
                guard let self, isCurrent(request, access: access) else { return }
                work = nil
                selection = value
                isSaving = false
                report(error, operation: "保存数据源")
            }
        }
    }

    private func reconcileAccess() {
        let next = active && model.isAvailable && !model.isClosed ? model.accountAccessGeneration : nil
        guard access != next else { return }
        invalidate()
        access = next
        guard let access, let homeBrowser else { return }
        let request = UUID()
        self.request = request
        work = Task { [weak self] in
            guard self?.isCurrent(request, access: access) == true else { return }
            let result: Result<MusicHomeSourceSelection, Error>
            do { result = .success(try await homeBrowser.homeSourceSelection()) }
            catch { result = .failure(error) }
            guard let self, isCurrent(request, access: access) else { return }
            work = nil
            switch result {
            case .success(let value): selection = value
            case .failure(let error):
                selection = nil
                report(error, operation: "读取数据源")
            }
        }
    }

    private func isCurrent(_ request: UUID, access: UUID) -> Bool {
        active && self.request == request && self.access == access &&
            model.accountAccessGeneration == access && model.isAvailable && !model.isClosed && !Task.isCancelled
    }

    private func invalidate() {
        request = nil
        work?.cancel()
        work = nil
        selection = nil
        isSaving = false
    }

    private func report(_ error: Error, operation: String) {
        let failure = MusicServiceError.sanitized(error)
        if failure != .cancelled { onError?(failure.message(operation: operation)) }
    }

    deinit { work?.cancel() }
}
#endif
