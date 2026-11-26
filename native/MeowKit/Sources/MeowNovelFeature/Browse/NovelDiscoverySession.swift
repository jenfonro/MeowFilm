import Foundation
import Combine
import MeowDebug
import MeowRuntime

public struct NovelDiscoveryItem: Identifiable, Equatable, Sendable {
    public var id: String
    public var title: String
    public var url: String
    public init(id: String, title: String, url: String) {
        self.id = id; self.title = title; self.url = url
    }
}

public struct NovelHomeSection: Identifiable, Equatable, Sendable {
    public enum State: Equatable, Sendable {
        case pending
        case loading
        case ready
        case failed(String)
    }

    public var id: String
    public var title: String
    public var options: [NovelDiscoveryItem]
    public var books: [NovelBook]
    public var state: State
    public var admitted: Bool

    public init(
        id: String,
        title: String,
        options: [NovelDiscoveryItem],
        books: [NovelBook] = [],
        state: State = .pending,
        admitted: Bool = false
    ) {
        self.id = id
        self.title = title
        self.options = options
        self.books = books
        self.state = state
        self.admitted = admitted
    }

    public var statusText: String {
        switch state {
        case .pending, .loading: return "加载中…"
        case .ready: return "暂无书籍"
        case .failed(let message): return message.isEmpty ? "栏目加载失败" : message
        }
    }

    static func make(_ kinds: [NovelExploreKind]) -> [Self] {
        var sections: [Self] = []
        var heading: (id: String, title: String)?
        for (index, kind) in kinds.enumerated() {
            let title = kind.title.trimmingCharacters(in: .whitespacesAndNewlines)
            let url = kind.url.trimmingCharacters(in: .whitespacesAndNewlines)
            if url.isEmpty {
                if !title.isEmpty { heading = ("group-\(index)", title) }
                continue
            }
            let item = NovelDiscoveryItem(id: "item-\(index)", title: title.isEmpty ? "未命名" : title, url: url)
            if let heading {
                if sections.last?.id != heading.id {
                    sections.append(.init(id: heading.id, title: heading.title, options: []))
                }
                sections[sections.count - 1].options.append(item)
            } else {
                sections.append(.init(id: item.id, title: item.title, options: [item]))
            }
        }
        return sections
    }
}

@MainActor
public final class NovelDiscoverySession: ObservableObject {
    private static let initialAdmittedCount = 4
    private static let maxConcurrentSectionLoads = 3

    public let feature: NovelFeature
    @Published public private(set) var selectedSource: NovelSource?
    @Published public private(set) var sections: [NovelHomeSection] = []
    @Published public private(set) var loading = false
    @Published public var error: String?

    private var initialized = false
    private var active = false
    private var closed = false
    private var needsKindsReload = true
    private var kindsFailed = false
    private var generation = UUID()
    private var kindsTask: Task<Void, Never>?
    private var sectionTasks: [String: Task<Void, Never>] = [:]
    private var running: [UUID: Task<Void, Never>] = [:]
    private var sourceObservation: AnyCancellable?

    public init(feature: NovelFeature) {
        self.feature = feature
        sourceObservation = feature.$sources.dropFirst().sink { [weak self] sources in
            self?.sourcesChanged(sources)
        }
    }

    public var availableSources: [NovelSource] { feature.sources.filter(Self.canExplore) }

    public var allLoadedBooks: [NovelBook] {
        sections.flatMap(\.books)
    }

    public func activate() {
        guard !closed, !feature.closed else { return }
        active = true
        feature.start()
        if needsKindsReload && !kindsFailed {
            beginKindsLoad()
        } else {
            scheduleAdmittedSections()
        }
    }

    public func deactivate() {
        active = false
        invalidate()
    }

    public func selectSource(id: String) {
        guard !closed, let source = availableSources.first(where: { $0.id == id }) else { return }
        guard selectedSource?.id != source.id || selectedSource?.revision != source.revision else { return }
        invalidate()
        selectedSource = source
        clearSections()
        needsKindsReload = true
        kindsFailed = false
        if active { beginKindsLoad() }
    }

    public func refresh() {
        guard !closed else { return }
        DebugRuntime.log(.novel, sub: "discovery", event: "refresh", level: .debug)
        invalidate()
        needsKindsReload = true
        kindsFailed = false
        if active { beginKindsLoad() }
    }

    public func retry() {
        guard !closed, active else { return }
        if needsKindsReload || sections.isEmpty {
            kindsFailed = false
            beginKindsLoad()
        } else {
            for index in sections.indices where sections[index].admitted {
                if case .failed = sections[index].state { sections[index].state = .pending }
            }
            error = nil
            scheduleAdmittedSections()
        }
    }

    public func admitSection(id: String) {
        guard !closed, let index = sections.firstIndex(where: { $0.id == id }) else { return }
        if !sections[index].admitted {
            sections[index].admitted = true
        }
        if active {
            scheduleAdmittedSections()
        }
    }

    public func categoryModel(sectionID: String) -> NovelCategoryModel? {
        guard !closed,
              let source = selectedSource,
              accepts(source),
              let section = sections.first(where: { $0.id == sectionID }),
              !section.options.isEmpty else { return nil }
        let seeded: [NovelBook]? = (section.state == .ready) ? section.books : nil
        return NovelCategoryModel(feature: feature, source: source, section: section, initialBooks: seeded)
    }

    static func canExplore(_ source: NovelSource) -> Bool {
        source.enabled && source.enabledExplore && source.actions.contains("exploreKinds") && source.actions.contains("explore")
    }

    private func accepts(_ source: NovelSource) -> Bool {
        !feature.closed && availableSources.contains { $0.id == source.id && $0.revision == source.revision }
    }

    private func sourcesChanged(_ sources: [NovelSource]) {
        guard !closed else { return }
        objectWillChange.send()
        guard initialized else { return }
        guard let previous = selectedSource else {
            guard let source = sources.first(where: Self.canExplore) else { return }
            selectedSource = source
            needsKindsReload = true
            kindsFailed = false
            if active { beginKindsLoad() }
            return
        }
        guard let current = sources.first(where: { $0.id == previous.id }), Self.canExplore(current) else {
            invalidate()
            // Retain the selected identity. A disappearing source never selects
            // another source on the user's behalf.
            if let current = sources.first(where: { $0.id == previous.id }) { selectedSource = current }
            clearSections()
            needsKindsReload = false
            kindsFailed = false
            error = NovelError.noSource.localizedDescription
            return
        }
        selectedSource = current
        if current.revision != previous.revision || !Self.canExplore(previous) {
            invalidate()
            clearSections()
            needsKindsReload = true
            kindsFailed = false
        }
        if active {
            if needsKindsReload && !kindsFailed {
                beginKindsLoad()
            } else {
                scheduleAdmittedSections()
            }
        }
    }

    private func clearSections() {
        sections = []
        error = nil
    }

    private func invalidate() {
        generation = UUID()
        kindsTask?.cancel()
        kindsTask = nil
        for task in sectionTasks.values { task.cancel() }
        sectionTasks.removeAll()
        for index in sections.indices {
            if sections[index].state == .loading {
                sections[index].state = .pending
            }
        }
        syncLoadingFlag()
        // Keep every accepted task until completion so close() drains late transports.
        for task in running.values { task.cancel() }
    }

    private func syncLoadingFlag() {
        let next = kindsTask != nil || !sectionTasks.isEmpty
        if loading != next {
            loading = next
        }
    }

    private func beginKindsLoad() {
        invalidate()
        kindsFailed = false
        error = nil
        let token = generation
        let taskID = UUID()
        let task = Task { [weak self] in
            guard let self else { return }
            defer {
                self.running[taskID] = nil
                if self.generation == token {
                    self.kindsTask = nil
                    self.syncLoadingFlag()
                }
            }
            do {
                try Task.checkCancellation()
                guard !self.closed, self.generation == token else { return }
                if !self.initialized {
                    try await self.feature.refreshSources()
                    try Task.checkCancellation()
                    guard !self.closed, self.generation == token else { return }
                    self.initialized = true
                    if let selected = self.selectedSource,
                       let current = self.feature.sources.first(where: { $0.id == selected.id }) {
                        self.selectedSource = current
                    }
                }
                if self.selectedSource == nil {
                    self.selectedSource = self.availableSources.first
                }
                guard let source = self.selectedSource else {
                    self.needsKindsReload = false
                    return
                }
                guard self.accepts(source) else {
                    self.needsKindsReload = false
                    self.error = NovelError.noSource.localizedDescription
                    return
                }
                let rawKinds = try await self.feature.sourcesClient.kinds(source)
                guard self.isCurrent(token, source: source) else { return }
                var built = NovelHomeSection.make(rawKinds)
                for idx in 0..<min(Self.initialAdmittedCount, built.count) {
                    built[idx].admitted = true
                }
                self.sections = built
                self.needsKindsReload = false
                self.kindsTask = nil
                self.scheduleAdmittedSections()
            } catch {
                guard !self.closed, !Task.isCancelled, self.generation == token else { return }
                if error is CancellationError { return }
                self.kindsFailed = true
                self.error = Self.errorText(error)
            }
        }
        kindsTask = task
        running[taskID] = task
        syncLoadingFlag()
    }

    private func scheduleAdmittedSections() {
        guard active, !closed, kindsTask == nil,
              let source = selectedSource, accepts(source) else {
            syncLoadingFlag()
            return
        }
        let token = generation
        for index in sections.indices {
            if sectionTasks.count >= Self.maxConcurrentSectionLoads { break }
            let section = sections[index]
            guard section.admitted, section.state == .pending, sectionTasks[section.id] == nil else { continue }
            guard let defaultOption = section.options.first else {
                sections[index].state = .ready
                continue
            }
            sections[index].state = .loading
            let sectionID = section.id
            let url = defaultOption.url
            let taskID = UUID()
            let task = Task { [weak self] in
                guard let self else { return }
                defer {
                    self.running[taskID] = nil
                    if self.generation == token {
                        self.sectionTasks[sectionID] = nil
                        self.scheduleAdmittedSections()
                    }
                }
                do {
                    try Task.checkCancellation()
                    let incoming = try await self.feature.sourcesClient.explore(source, url: url, page: 1)
                    guard self.isCurrent(token, source: source),
                          let idx = self.sections.firstIndex(where: { $0.id == sectionID }) else { return }
                    var seen = Set<String>()
                    let unique = incoming.filter { seen.insert($0.installationId + "\n" + $0.bookUrl).inserted }
                    self.sections[idx].books = unique
                    self.sections[idx].state = .ready
                } catch {
                    guard self.isCurrent(token, source: source),
                          let idx = self.sections.firstIndex(where: { $0.id == sectionID }) else { return }
                    if error is CancellationError || Task.isCancelled {
                        self.sections[idx].state = .pending
                        return
                    }
                    let message = Self.errorText(error)
                    self.sections[idx].state = .failed(message)
                    if self.error == nil {
                        self.error = message
                    }
                }
            }
            sectionTasks[sectionID] = task
            running[taskID] = task
        }
        syncLoadingFlag()
    }

    private func isCurrent(_ token: UUID, source: NovelSource) -> Bool {
        !closed && !Task.isCancelled && generation == token &&
            selectedSource?.id == source.id && selectedSource?.revision == source.revision && accepts(source)
    }

    static func errorText(_ error: Error) -> String {
        (error as? RuntimeHTTPError) == .remote("WEB_INTERACTION_REQUIRED")
            ? "书源需要网页验证"
            : error.localizedDescription
    }

    public func close() async {
        closed = true
        active = false
        sourceObservation = nil
        invalidate()
        for task in Array(running.values) { await task.value }
    }
}

@MainActor
public final class NovelCategoryModel: ObservableObject {
    public let feature: NovelFeature
    public let source: NovelSource
    public let sectionID: String
    public let title: String
    public let options: [NovelDiscoveryItem]

    @Published public private(set) var selectedOptionID: String
    @Published public private(set) var books: [NovelBook] = []
    @Published public private(set) var isLoading = false
    @Published public private(set) var hasMore = true
    @Published public private(set) var error: String?

    private var page = 0
    private var hasLoadedInitial = false
    private var closed = false
    private var requestID: UUID?
    private var task: Task<Void, Never>?
    private var running: [UUID: Task<Void, Never>] = [:]
    private var sourceObservation: AnyCancellable?

    public init(
        feature: NovelFeature,
        source: NovelSource,
        section: NovelHomeSection,
        initialBooks: [NovelBook]? = nil
    ) {
        self.feature = feature
        self.source = source
        self.sectionID = section.id
        self.title = section.title
        self.options = section.options
        self.selectedOptionID = section.options.first?.id ?? ""
        if let initialBooks {
            self.books = initialBooks
            self.page = 1
            self.hasLoadedInitial = true
            self.hasMore = !initialBooks.isEmpty
        }
        sourceObservation = feature.$sources.dropFirst().sink { [weak self] sources in
            self?.sourcesChanged(sources)
        }
    }

    public var selectedOption: NovelDiscoveryItem? {
        options.first(where: { $0.id == selectedOptionID }) ?? options.first
    }

    @discardableResult
    public func loadIfNeeded() -> Task<Void, Never>? {
        guard !closed else { return nil }
        if let task { return task }
        guard !hasLoadedInitial, error == nil else { return nil }
        return loadPage(targetPage: 1, replacing: true)
    }

    @discardableResult
    public func selectOption(_ id: String) -> Task<Void, Never>? {
        guard !closed, selectedOptionID != id,
              options.contains(where: { $0.id == id }) else { return nil }
        retireRequest()
        selectedOptionID = id
        books = []
        page = 0
        hasLoadedInitial = false
        hasMore = true
        error = nil
        return loadPage(targetPage: 1, replacing: true)
    }

    @discardableResult
    public func refresh() -> Task<Void, Never>? {
        guard !closed else { return nil }
        retireRequest()
        return loadPage(targetPage: 1, replacing: true)
    }

    @discardableResult
    public func loadMore() -> Task<Void, Never>? {
        guard !closed, hasMore else { return nil }
        if let task { return task }
        let nextPage = page == 0 ? 1 : page + 1
        return loadPage(targetPage: nextPage, replacing: page == 0)
    }

    public func cancel() {
        retireRequest()
    }

    public func close() async {
        closed = true
        sourceObservation = nil
        retireRequest()
        for task in Array(running.values) { await task.value }
    }

    private func acceptsSource() -> Bool {
        !feature.closed && feature.sources.contains {
            $0.id == source.id && $0.revision == source.revision && NovelDiscoverySession.canExplore($0)
        }
    }

    private func sourcesChanged(_ sources: [NovelSource]) {
        guard !closed else { return }
        let stillValid = sources.contains {
            $0.id == source.id && $0.revision == source.revision && NovelDiscoverySession.canExplore($0)
        }
        if !stillValid {
            retireRequest()
            hasMore = false
            error = NovelError.noSource.localizedDescription
        }
    }

    private func loadPage(targetPage: Int, replacing: Bool) -> Task<Void, Never>? {
        guard acceptsSource(), let option = selectedOption else {
            hasMore = false
            error = NovelError.noSource.localizedDescription
            return nil
        }
        error = nil
        isLoading = true
        let reqID = UUID()
        let taskID = UUID()
        requestID = reqID
        let source = source
        let url = option.url
        let loadTask = Task { [weak self] in
            guard let self else { return }
            defer {
                self.running[taskID] = nil
                if self.requestID == reqID {
                    self.task = nil
                    self.requestID = nil
                    self.isLoading = false
                }
            }
            do {
                try Task.checkCancellation()
                let incoming = try await self.feature.sourcesClient.explore(source, url: url, page: targetPage)
                try Task.checkCancellation()
                guard !self.closed, self.requestID == reqID, self.acceptsSource() else { return }
                var seen = Set<String>()
                let uniqueIncoming = incoming.filter { seen.insert($0.installationId + "\n" + $0.bookUrl).inserted }
                if replacing {
                    self.books = uniqueIncoming
                    self.hasMore = !uniqueIncoming.isEmpty
                } else {
                    let existing = Set(self.books.map { $0.installationId + "\n" + $0.bookUrl })
                    let added = uniqueIncoming.filter { !existing.contains($0.installationId + "\n" + $0.bookUrl) }
                    self.books.append(contentsOf: added)
                    self.hasMore = !added.isEmpty
                }
                self.page = targetPage
                self.hasLoadedInitial = true
            } catch {
                guard !self.closed, self.requestID == reqID else { return }
                if !(error is CancellationError) && !Task.isCancelled {
                    self.error = NovelDiscoverySession.errorText(error)
                }
            }
        }
        task = loadTask
        running[taskID] = loadTask
        return loadTask
    }

    private func retireRequest() {
        requestID = nil
        task?.cancel()
        task = nil
        isLoading = false
    }

    deinit {
        task?.cancel()
    }
}
