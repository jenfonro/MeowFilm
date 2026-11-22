import Foundation
import Combine

public struct NovelSearchSourceState: Equatable, Sendable {
    public enum Status: Equatable, Sendable { case idle, loading, loaded, exhausted, failed(String), cancelled }
    public var source: NovelSource
    public var page: Int = 0
    public var status: Status = .idle
}

@MainActor
public final class NovelSearchSession: ObservableObject {
    public let feature: NovelFeature
    @Published public private(set) var books: [NovelSearchBook] = []
    @Published public private(set) var states: [NovelSearchSourceState] = []
    @Published public private(set) var loading = false
    @Published public var error: String?
    public private(set) var query = ""
    private var raw: [NovelBook] = []
    private var task: Task<Void, Never>?
    private var running: [UUID: Task<Void, Never>] = [:]
    private var generation = UUID()
    private var closed = false
    private var active = false
    private let matchingBook: NovelBook?
    private var sourceObservation: AnyCancellable?
    public init(feature: NovelFeature, matchingBook: NovelBook? = nil) {
        self.feature = feature; self.matchingBook = matchingBook
        sourceObservation = feature.$sources.dropFirst().sink { [weak self] sources in
            guard let self, !closed, !query.isEmpty else { return }
            let available = sources.filter { $0.enabled && $0.actions.contains("search") }
            guard states.map(\.source) != available else { return }
            configure(query, sources: available, start: active)
        }
    }
    public var canLoadMore: Bool { states.contains { if case .exhausted = $0.status { return false }; return true } }
    public func submit(_ keyword: String, force: Bool = false) {
        guard !closed else { return }
        active = true
        let keyword = keyword.trimmingCharacters(in: .whitespacesAndNewlines)
        guard force || keyword != query || states.isEmpty else {
            if !loading, states.contains(where: { $0.status == .idle || $0.status == .cancelled }) { loadMore() }
            return
        }
        configure(keyword, sources: feature.sources.filter { $0.enabled && $0.actions.contains("search") }, start: true)
    }
    private func configure(_ keyword: String, sources: [NovelSource], start: Bool) {
        cancel(); query = keyword; books = []; raw = []
        states = sources.map { .init(source: $0) }
        active = start
        if start { loadMore() }
    }
    public func loadMore() {
        guard !closed, !loading, !query.isEmpty else { return }
        active = true
        let requested = states.filter { if case .exhausted = $0.status { return false }; return true }
        guard !requested.isEmpty else { return }
        let token = generation, taskID = UUID(), client = feature.sourcesClient, keyword = query
        loading = true
        for index in states.indices where requested.contains(where: { $0.source.id == states[index].source.id }) { states[index].status = .loading }
        task = Task { [weak self] in
            guard let self else { return }
            defer { running[taskID] = nil; if generation == token { loading = false } }
            await withTaskGroup(of: (String, Int, Result<[NovelBook], Error>).self) { group in
                for state in requested {
                    group.addTask {
                        do { return (state.source.id, state.page + 1, .success(try await client.search(state.source, keyword: keyword, page: state.page + 1))) }
                        catch { return (state.source.id, state.page + 1, .failure(error)) }
                    }
                }
                for await (id, page, result) in group {
                    guard !closed, !Task.isCancelled, generation == token,
                          let index = states.firstIndex(where: { $0.source.id == id }) else { continue }
                    guard feature.accepts(states[index].source) else { states[index].status = .cancelled; continue }
                    switch result {
                    case .success(let incoming):
                        let existing = Set(raw.map { $0.installationId + "\n" + $0.bookUrl })
                        let added = incoming.filter { !existing.contains($0.installationId + "\n" + $0.bookUrl) }
                        raw.append(contentsOf: added); states[index].page = page
                        states[index].status = added.isEmpty ? .exhausted : .loaded
                        do {
                            var merged = try await feature.rules.merge(raw, sources: feature.sources)
                            if let matchingBook { merged = try await feature.rules.sourceCandidates(merged, for: matchingBook) }
                            guard !closed, !Task.isCancelled, generation == token else { continue }
                            books = merged
                        } catch { if generation == token, !Task.isCancelled { self.error = error.localizedDescription } }
                    case .failure(let failure): states[index].status = .failed(failure.localizedDescription)
                    }
                }
            }
        }
        running[taskID] = task
    }
    public func cancel() {
        generation = UUID(); loading = false; active = false
        task?.cancel(); task = nil
        for index in states.indices { if states[index].status == .loading { states[index].status = .cancelled } }
    }
    public func close() async {
        closed = true; sourceObservation = nil; cancel()
        for task in Array(running.values) { await task.value }; running.removeAll()
    }
}
