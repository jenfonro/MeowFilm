import Combine
import Foundation

@MainActor
public final class FilmHomeModel: ObservableObject {
    public let sources: [FilmSourceDescriptor]
    @Published public private(set) var selectedSourceID: String?
    @Published public private(set) var sections: [FilmHomeSection] = []
    @Published public private(set) var isLoading = false
    @Published public private(set) var error: FilmHomeError?

    private struct Snapshot {
        let sections: [FilmHomeSection]
        let error: FilmHomeError?
    }

    private let sourceByID: [String: any FilmSource]
    private var snapshots: [String: Snapshot] = [:]
    private var requestID: UUID?
    private var loadTask: Task<Void, Never>?
    private var isClosed = false

    public init(sources: [any FilmSource] = [DoubanFilmSource()]) {
        let descriptors = sources.map(\.descriptor)
        self.sources = descriptors
        self.selectedSourceID = descriptors.first?.id
        self.sourceByID = Dictionary(uniqueKeysWithValues: zip(descriptors.map(\.id), sources))
    }

    public func categoryModel(sectionID: String) -> FilmCategoryModel? {
        guard let selectedSourceID,
              let source = sourceByID[selectedSourceID] as? any FilmCategorySource,
              let category = source.categories.first(where: { $0.rawValue == sectionID }) else { return nil }
        return FilmCategoryModel(category: category, source: source)
    }

    func supportsCategory(sectionID: String) -> Bool {
        guard let selectedSourceID, let source = sourceByID[selectedSourceID] as? any FilmCategorySource else { return false }
        return source.categories.contains { $0.rawValue == sectionID }
    }

    @discardableResult
    public func loadIfNeeded() -> Task<Void, Never>? {
        guard !isClosed else { return nil }
        if let loadTask { return loadTask }
        guard let selectedSourceID, snapshots[selectedSourceID] == nil else { return nil }
        return startLoad()
    }

    @discardableResult
    public func refresh() -> Task<Void, Never>? {
        guard !isClosed else { return nil }
        retireRequest()
        return startLoad()
    }

    @discardableResult
    public func selectSource(id: String) -> Task<Void, Never>? {
        guard !isClosed, sourceByID[id] != nil else { return nil }
        if selectedSourceID == id { return loadIfNeeded() }
        retireRequest()
        selectedSourceID = id
        restoreSnapshot()
        return loadIfNeeded()
    }

    public func cancel() {
        retireRequest()
        restoreSnapshot()
    }

    public func close() {
        isClosed = true
        cancel()
    }

    private func startLoad() -> Task<Void, Never>? {
        guard let selectedSourceID, let source = sourceByID[selectedSourceID] else { return nil }
        let requestID = UUID()
        self.requestID = requestID
        error = nil
        isLoading = true
        let task = Task<Void, Never> { [weak self] in
            do {
                try Task.checkCancellation()
                let sections = try await source.loadHome()
                try Task.checkCancellation()
                self?.finish(.success(sections), sourceID: selectedSourceID, requestID: requestID)
            } catch {
                if error is CancellationError || Task.isCancelled || (error as? URLError)?.code == .cancelled {
                    self?.finishCancellation(sourceID: selectedSourceID, requestID: requestID)
                } else {
                    self?.finish(.failure(FilmHomeError.from(error)),
                                 sourceID: selectedSourceID, requestID: requestID)
                }
            }
        }
        loadTask = task
        return task
    }

    private func canPublish(sourceID: String, requestID: UUID) -> Bool {
        !isClosed && self.requestID == requestID && selectedSourceID == sourceID
    }

    private func finish(_ result: Result<[FilmHomeSection], FilmHomeError>,
                        sourceID: String, requestID: UUID) {
        guard canPublish(sourceID: sourceID, requestID: requestID) else { return }
        self.requestID = nil
        loadTask = nil
        switch result {
        case .success(let sections):
            self.sections = sections
            error = nil
        case .failure(let error):
            self.error = error
        }
        snapshots[sourceID] = Snapshot(sections: sections, error: error)
        isLoading = false
    }

    private func finishCancellation(sourceID: String, requestID: UUID) {
        guard canPublish(sourceID: sourceID, requestID: requestID) else { return }
        self.requestID = nil
        loadTask = nil
        restoreSnapshot()
    }

    private func retireRequest() {
        requestID = nil
        let task = loadTask
        loadTask = nil
        task?.cancel()
        isLoading = false
    }

    private func restoreSnapshot() {
        let snapshot = selectedSourceID.flatMap { snapshots[$0] }
        sections = snapshot?.sections ?? []
        error = snapshot?.error
        isLoading = false
    }

    deinit {
        loadTask?.cancel()
    }
}
