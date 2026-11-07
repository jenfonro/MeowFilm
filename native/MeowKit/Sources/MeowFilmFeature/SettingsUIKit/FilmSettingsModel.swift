#if os(iOS)
import Foundation
import Combine

/// UI lifetime belongs to App composition, independently of music and Node.
@MainActor
public final class FilmSettingsModel: ObservableObject {
    public let rules: FilmRulesDraft
    public let sources: FilmSourceService
    @Published public private(set) var snapshot: FilmSourceSnapshot?
    @Published public private(set) var loadError: String?
    @Published public private(set) var rulesError: String?
    @Published public private(set) var sourcesError: String?
    private var observation: Task<Void, Never>?
    private var loading: Task<Void, Never>?
    private var generation = UUID()

    public init(rules: FilmRuleService, sources: FilmSourceService) {
        self.rules = FilmRulesDraft(service: rules); self.sources = sources
    }
    public func start(loadError: String? = nil) {
        close()
        self.loadError = loadError
        rulesError = nil; sourcesError = nil
        let generation = UUID(); self.generation = generation
        loading = Task { [weak self, rules] in
            do {
                try await rules.service.initialize()
                try Task.checkCancellation()
                try await rules.load()
                self?.rulesError = nil
            }
            catch {
                guard let self, !Task.isCancelled, self.generation == generation else { return }
                self.rulesError = error.localizedDescription
                self.loadError = error.localizedDescription
            }
        }
        observation = Task { [weak self, sources] in
            do {
                try await sources.initialize()
                guard !Task.isCancelled else { return }
                self?.sourcesError = nil
            } catch {
                guard let self, !Task.isCancelled, self.generation == generation else { return }
                self.sourcesError = error.localizedDescription
                self.loadError = error.localizedDescription
            }
            for await snapshot in await sources.snapshots() {
                guard let self, !Task.isCancelled, self.generation == generation else { return }
                self.snapshot = snapshot
            }
        }
    }
    public func close() {
        generation = UUID(); loading?.cancel(); loading = nil
        observation?.cancel(); observation = nil
    }
    deinit { loading?.cancel(); observation?.cancel() }
}
#endif
