import Foundation
import Combine

@MainActor
public final class NovelFeature: ObservableObject {
    public let library: NovelLibrary
    public let rules: NovelRuleProcessor
    public let sourcesClient: NovelSourceClient
    public let web: NovelWebBroker
    #if os(iOS)
    public lazy var images = NovelImageStore(sourcesClient: sourcesClient)
    #endif
    @Published public private(set) var local = NovelLibrarySnapshot()
    @Published public private(set) var sources: [NovelSource] = []
    @Published public var error: String?
    public private(set) var closed = false
    private var startup: Task<Void, Never>?
    private var sourceRefresh = UUID()
    private var sessions: [UUID: NovelReadingSession] = [:]
    private var closing: Task<Void, Never>?
    public init(library: NovelLibrary, rules: NovelRuleProcessor, runtime: NovelRuntime) {
        self.library = library; self.rules = rules; sourcesClient = NovelSourceClient(runtime: runtime)
        web = NovelWebBroker(runtime: runtime)
        web.onError = { [weak self] in self?.error = $0 }
    }
    public func start() {
        guard startup == nil, !closed else { return }
        startup = Task { [weak self] in
            guard let self else { return }
            await web.start()
            do { try await reloadLocal() } catch { self.error = error.localizedDescription }
            do { try await refreshSources() } catch { self.error = error.localizedDescription }
        }
    }
    public func reloadLocal() async throws {
        let value = try await library.snapshot()
        guard !closed, !Task.isCancelled else { return }; local = value
    }
    public func refreshSources() async throws {
        let token = UUID(); sourceRefresh = token
        let value = try await sourcesClient.list()
        try Task.checkCancellation(); guard !closed else { throw NovelError.closed }
        guard sourceRefresh == token else { return }
        sources = value.sorted { $0.order == $1.order ? $0.id < $1.id : $0.order < $1.order }
        #if os(iOS)
        images.invalidate(activeSources: sources)
        #endif
        for session in sessions.values { session.sourcesChanged() }
    }
    public func source(for book: NovelBook) throws -> NovelSource {
        guard !closed else { throw NovelError.closed }
        guard let source = sources.first(where: { $0.id == book.installationId && $0.enabled }) else { throw NovelError.noSource }
        return source
    }
    func accepts(_ source: NovelSource) -> Bool {
        !closed && sources.contains { $0.id == source.id && $0.revision == source.revision && $0.enabled }
    }
    public func setShelf(_ book: NovelBook, present: Bool) async throws {
        let entries = try await library.snapshot().entries
        try Task.checkCancellation(); guard !closed else { throw NovelError.closed }
        if let existing = NovelLibrary.find(in: entries, book: book, identity: "") {
            try await library.setShelf(existing.id, present: present)
            try await reloadLocal()
            return
        }
        let key = try await rules.identity(book)
        try Task.checkCancellation(); guard !closed else { throw NovelError.closed }
        let latest = try await library.snapshot().entries
        try Task.checkCancellation(); guard !closed else { throw NovelError.closed }
        if let existing = NovelLibrary.find(in: latest, book: book, identity: key) {
            try await library.setShelf(existing.id, present: present)
            try await reloadLocal()
        } else if present {
            _ = try await library.retain(book, identity: key, shelf: true)
            try await reloadLocal()
        }
    }
    public func clearHistory() async throws {
        try await library.clearHistory()
        try await reloadLocal()
    }
    public func saveSettings(_ settings: NovelReadingSettings) async throws {
        try await library.saveSettings(settings); try await reloadLocal()
        for session in sessions.values { session.reprocess() }
    }
    public func saveRules(_ value: [NovelRule]) async throws {
        try await library.saveRules(value); try await reloadLocal()
        for session in sessions.values { session.reprocess() }
    }
    public func makeReader(book: NovelBook) -> NovelReadingSession {
        let session = NovelReadingSession(feature: self, book: book)
        sessions[session.id] = session; return session
    }
    func releaseReader(_ session: NovelReadingSession) { sessions[session.id] = nil }
    public func flush() async { for session in sessions.values { await session.flush() } }
    public func suspend() { web.suspend(); for session in sessions.values { session.suspend() } }
    public func close() async {
        if let closing { await closing.value; return }
        closed = true; startup?.cancel()
        let readers = Array(sessions.values), startup = startup, web = web, client = sourcesClient, rules = rules, library = library
        #if os(iOS)
        let imageStore = images
        #endif
        let task = Task {
            await web.close()
            for session in readers { await session.close() }
            await startup?.value
            #if os(iOS)
            await imageStore.close()
            #endif
            await rules.close()
            do { try await client.runtime.close() }
            catch { self.error = error.localizedDescription }
            await library.close()
        }
        closing = task; await task.value; sessions.removeAll()
    }
}
