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
    private var lastLoadedOffset = 0
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
        guard !closed else { return nil }
        if let task { return task }
        return loadPage(offset: 0, replacing: true, refresh: false)
    }

    @discardableResult
    public func selectFilter(_ id: String, value: String) -> Task<Void, Never>? {
        guard !closed, filters[id] != value,
              filterRows.first(where: { $0.id == id })?.options.contains(where: { $0.id == value }) == true
        else { return nil }
        retireRequest()
        filters[id] = value
        items = []
        offset = 0
        lastLoadedOffset = 0
        hasMore = true
        return loadPage(offset: 0, replacing: true, refresh: false)
    }

    @discardableResult
    public func refresh() -> Task<Void, Never>? {
        guard !closed else { return nil }
        retireRequest()
        return loadPage(offset: 0, replacing: true, refresh: true)
    }

    @discardableResult
    public func loadMore() -> Task<Void, Never>? {
        guard !closed, hasMore else { return nil }
        if let task { return task }
        return loadPage(offset: offset, replacing: false, refresh: false)
    }

    private func loadPage(offset: Int, replacing: Bool, refresh: Bool) -> Task<Void, Never> {
        error = nil
        isLoading = true
        let id = UUID()
        requestID = id
        let source = source, category = category, filters = filters
        let loadedThrough = lastLoadedOffset
        let task = Task { [weak self] in
            do {
                var pageOffset = offset
                var items: [FilmItem] = []
                var seen = Set<FilmItemIdentity>()
                var nextOffset: Int?
                var cacheError: FilmHomeError?
                while true {
                    try Task.checkCancellation()
                    let page = try await source.loadCategory(category, filters: filters, offset: pageOffset, refresh: refresh)
                    try Task.checkCancellation()
                    guard self?.requestID == id else { return }
                    if let next = page.nextOffset, next <= pageOffset { throw FilmHomeError.invalidResponse }
                    items.append(contentsOf: page.items.filter { seen.insert($0.identity).inserted })
                    if cacheError == nil { cacheError = page.cache?.errorCode.map { .runtime(code: $0) } }
                    nextOffset = page.nextOffset
                    guard replacing, pageOffset < loadedThrough, let next = nextOffset else { break }
                    pageOffset = next
                }
                guard let self, !self.closed, self.requestID == id else { return }
                if replacing {
                    self.items = items
                } else {
                    let existing = Set(self.items.map(\.identity))
                    self.items.append(contentsOf: items.filter { !existing.contains($0.identity) })
                }
                self.lastLoadedOffset = pageOffset
                self.hasMore = nextOffset != nil
                self.offset = nextOffset ?? pageOffset
                self.error = cacheError
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
        cancel()
    }

    public func cancel() {
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
