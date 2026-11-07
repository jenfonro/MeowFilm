import Combine
import Foundation

@MainActor
public final class FilmCategoryModel: ObservableObject {
    public let category: FilmCategory
    @Published public private(set) var filters: [String: String]
    @Published public private(set) var items: [FilmItem] = []
    @Published public private(set) var isLoading = false
    @Published public private(set) var error: FilmHomeError?
    @Published public private(set) var hasMore = true
    var filterRows: [FilmCategoryFilter] { category.filterRows(for: filters) }

    private let source: any FilmCategorySource
    private var offset = 0
    private var started = false
    private var closed = false
    private var requestID: UUID?
    private var task: Task<Void, Never>?

    public init(category: FilmCategory, source: any FilmCategorySource) {
        self.category = category
        self.source = source
        filters = category.defaultFilters
    }

    @discardableResult
    public func loadIfNeeded() -> Task<Void, Never>? {
        guard !started else { return task }
        return loadMore()
    }

    @discardableResult
    public func selectFilter(_ id: String, value: String) -> Task<Void, Never>? {
        guard !closed, filters[id] != value,
              filterRows.first(where: { $0.id == id })?.options.contains(where: { $0.id == value }) == true
        else { return nil }
        filters[id] = value
        return refresh()
    }

    @discardableResult
    public func refresh() -> Task<Void, Never>? {
        guard !closed else { return nil }
        retireRequest()
        items = []
        offset = 0
        hasMore = true
        return loadMore()
    }

    @discardableResult
    public func loadMore() -> Task<Void, Never>? {
        guard !closed, hasMore else { return nil }
        if let task { return task }
        started = true
        error = nil
        isLoading = true
        let id = UUID()
        requestID = id
        let source = source, category = category, filters = filters, offset = offset
        let task = Task { [weak self] in
            do {
                try Task.checkCancellation()
                let page = try await source.loadCategory(category, filters: filters, offset: offset)
                try Task.checkCancellation()
                guard let self, !self.closed, self.requestID == id else { return }
                if let next = page.nextOffset, next <= offset { throw FilmHomeError.invalidResponse }
                var seen = Set(self.items.map(\.identity))
                self.items.append(contentsOf: page.items.filter { seen.insert($0.identity).inserted })
                self.hasMore = page.nextOffset != nil
                self.offset = page.nextOffset ?? offset
                self.finish(id)
            } catch {
                guard let self, !self.closed, self.requestID == id else { return }
                if !(error is CancellationError) && !Task.isCancelled && (error as? URLError)?.code != .cancelled {
                    self.error = FilmHomeError.from(error)
                }
                self.finish(id)
            }
        }
        self.task = task
        return task
    }

    public func close() {
        closed = true
        retireRequest()
    }

    private func finish(_ id: UUID) {
        guard requestID == id else { return }
        task = nil
        requestID = nil
        isLoading = false
    }

    private func retireRequest() {
        requestID = nil
        task?.cancel()
        task = nil
        isLoading = false
    }

    deinit { task?.cancel() }
}
