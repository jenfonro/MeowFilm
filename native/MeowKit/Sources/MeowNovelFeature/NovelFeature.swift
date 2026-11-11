import Foundation
import Combine
import MeowRuntime

@MainActor
public final class NovelFeature: ObservableObject {
    public let library: NovelLibrary
    public let rules: NovelRuleProcessor
    public let sourcesClient: NovelSourceClient
    public let web: NovelWebBroker
    @Published public private(set) var local = NovelLibrarySnapshot()
    @Published public private(set) var sources: [NovelSource] = []
    @Published public var error: String?
    public private(set) var closed = false
    private var startup: Task<Void, Never>?
    private var sessions: [UUID: NovelReadingSession] = [:]
    private var closing: Task<Void, Never>?
    public init(library: NovelLibrary, rules: NovelRuleProcessor, runtime: any NovelRuntimeServing) {
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
        let value = try await sourcesClient.list()
        try Task.checkCancellation(); guard !closed else { throw NovelError.closed }
        sources = value.sorted { $0.order == $1.order ? $0.id < $1.id : $0.order < $1.order }
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
    public func retain(_ book: NovelBook, shelf: Bool = false) async throws -> NovelBook {
        try await retainWithIdentity(book, shelf: shelf).book
    }
    func retainWithIdentity(_ book: NovelBook, shelf: Bool = false) async throws -> (book: NovelBook, identity: String) {
        let key = try await rules.identity(book)
        try Task.checkCancellation(); guard !closed else { throw NovelError.closed }
        let saved = try await library.retain(book, identity: key, shelf: shelf)
        try await reloadLocal(); return (saved, key)
    }
    public func setShelf(_ book: NovelBook, present: Bool) async throws {
        let entries = try await library.snapshot().entries
        try Task.checkCancellation(); guard !closed else { throw NovelError.closed }
        let existing = entries.first { $0.id == book.id } ??
            entries.first { $0.book.installationId == book.installationId && $0.book.bookUrl == book.bookUrl }
        if let existing {
            try await library.setShelf(existing.id, present: present); try await reloadLocal()
        } else {
            _ = try await retain(book, shelf: present)
        }
    }
    public func pin(_ id: String) async throws { try await library.pin(id); try await reloadLocal() }
    public func deleteHistory(_ id: String) async throws { try await library.deleteHistory(id); try await reloadLocal() }
    public func saveSettings(_ settings: NovelReadingSettings) async throws {
        try await library.saveSettings(settings); try await reloadLocal()
        for session in sessions.values { session.reprocess() }
    }
    public func saveRules(_ value: [NovelRule]) async throws {
        for rule in value { try await rules.validate(rule) }
        try await library.saveRules(value); try await reloadLocal()
        for session in sessions.values { session.reprocess() }
    }
    public func makeReader(book: NovelBook) -> NovelReadingSession {
        makeReader(book: book) { [rules] content, book, source, value, conversion in
            try await rules.transform(content, book: book, source: source, rules: value, conversion: conversion)
        }
    }
    func makeReader(book: NovelBook, transform: @escaping NovelReadingSession.Transform) -> NovelReadingSession {
        let session = NovelReadingSession(feature: self, book: book, transform: transform)
        sessions[session.id] = session; return session
    }
    func releaseReader(_ session: NovelReadingSession) { sessions[session.id] = nil }
    public func flush() async { for session in sessions.values { await session.flush() } }
    public func suspend() { web.suspend(); for session in sessions.values { session.suspend() } }
    public func close() async {
        if let closing { await closing.value; return }
        closed = true; startup?.cancel()
        let readers = Array(sessions.values), startup = startup, web = web, client = sourcesClient, rules = rules, library = library
        let task = Task {
            await web.close()
            for session in readers { await session.close() }
            await startup?.value
            await rules.close()
            do { try await client.runtime.close() }
            catch { self.error = error.localizedDescription }
            await library.close()
        }
        closing = task; await task.value; sessions.removeAll()
    }
}
