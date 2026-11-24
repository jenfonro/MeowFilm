import Foundation
import Combine
import MeowDebug

public struct NovelSearchSourceState: Equatable, Sendable {
    public enum Status: Equatable, Sendable { case idle, loading, loaded, exhausted, failed(String), cancelled }
    public var source: NovelSource
    public var page: Int = 0
    public var status: Status = .idle
}

@MainActor
public final class NovelSearchSession: ObservableObject {
    private static let maxConcurrency = 6
    private static let mergeBatchThreshold = 4
    private static let mergeDebounceNanoseconds: UInt64 = 100_000_000

    public let feature: NovelFeature
    @Published public private(set) var books: [NovelSearchBook] = []
    @Published public private(set) var states: [NovelSearchSourceState] = []
    @Published public private(set) var loading = false
    @Published public var error: String?
    public private(set) var query = ""
    private var raw: [NovelBook] = []
    private var task: Task<Void, Never>?
    private var mergeTask: Task<Void, Never>?
    private var mergeTaskID: UUID?
    private var mergingInFlight = false
    private var mergeDirty = false
    private var mergePendingSources = 0
    private var running: [UUID: Task<Void, Never>] = [:]
    private var generation = UUID()
    private var closed = false
    private var active = false
    let matchingBook: NovelBook?
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
    public var allSourcesFailed: Bool {
        !states.isEmpty && states.allSatisfy { if case .failed = $0.status { return true }; return false }
    }
    public var allSourcesCancelled: Bool {
        !states.isEmpty && states.contains { $0.status == .cancelled } && states.allSatisfy {
            switch $0.status { case .cancelled, .failed, .idle: return true; default: return false }
        }
    }
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
        cancel(); query = keyword; books = []; raw = []; error = nil
        states = sources.map { .init(source: $0) }
        active = start
        if start { loadMore() }
    }
    public func loadMore() {
        guard !closed, !loading, !query.isEmpty else { return }
        active = true
        error = nil
        let requested = states.filter { if case .exhausted = $0.status { return false }; return true }
        guard !requested.isEmpty else { return }
        let token = generation, taskID = UUID(), client = feature.sourcesClient, keyword = query
        loading = true
        DebugRuntime.log(.search, sub: "novel", event: "wave.start", level: .info, data: .object([
            "query": .string(keyword),
            "sourceCount": .int(requested.count),
            "maxConcurrency": .int(Self.maxConcurrency),
            "matchingBook": .bool(matchingBook != nil)
        ]))
        task = Task { [weak self] in
            guard let self else { return }
            defer {
                running[taskID] = nil
                if generation == token { loading = false }
            }
            var nextIndex = 0
            var inFlight = 0
            await withTaskGroup(of: (String, Int, Result<[NovelBook], Error>).self) { group in
                while true {
                    while inFlight < Self.maxConcurrency, nextIndex < requested.count,
                          !closed, !Task.isCancelled, generation == token {
                        let candidate = requested[nextIndex]
                        nextIndex += 1
                        guard let stateIndex = states.firstIndex(where: { $0.source.id == candidate.source.id }) else { continue }
                        guard feature.accepts(states[stateIndex].source) else {
                            states[stateIndex].status = .cancelled
                            continue
                        }
                        states[stateIndex].status = .loading
                        let source = states[stateIndex].source
                        let nextPage = states[stateIndex].page + 1
                        inFlight += 1
                        group.addTask {
                            do { return (source.id, nextPage, .success(try await client.search(source, keyword: keyword, page: nextPage))) }
                            catch { return (source.id, nextPage, .failure(error)) }
                        }
                    }
                    guard inFlight > 0 else { break }
                    guard let (id, page, result) = await group.next() else { break }
                    inFlight -= 1
                    guard !closed, !Task.isCancelled, generation == token,
                          let index = states.firstIndex(where: { $0.source.id == id }) else { continue }
                    if !feature.accepts(states[index].source) {
                        states[index].status = .cancelled
                    } else {
                        switch result {
                        case .success(let incoming):
                            var existing = Set(raw.map { $0.installationId + "\n" + $0.bookUrl })
                            let added = incoming.filter { existing.insert($0.installationId + "\n" + $0.bookUrl).inserted }
                            raw.append(contentsOf: added)
                            states[index].page = page
                            states[index].status = added.isEmpty ? .exhausted : .loaded
                            DebugRuntime.log(.search, sub: "novel", event: "source.done", level: .debug, data: .object([
                                "sourceId": .string(id),
                                "page": .int(page),
                                "incomingCount": .int(incoming.count),
                                "addedCount": .int(added.count),
                                "exhausted": .bool(added.isEmpty)
                            ]))
                            if !added.isEmpty {
                                mergeDirty = true
                                mergePendingSources += 1
                                scheduleMerge(token: token, immediate: mergePendingSources >= Self.mergeBatchThreshold)
                            }
                        case .failure(let failure):
                            states[index].status = .failed(failure.localizedDescription)
                            DebugRuntime.logError(.search, sub: "novel", event: "source.error", error: failure, data: .object([
                                "sourceId": .string(id),
                                "page": .int(page)
                            ]), level: .warn)
                        }
                    }
                }
            }
            if !closed, !Task.isCancelled, generation == token {
                if let pendingMerge = mergeTask {
                    await pendingMerge.value
                }
                if mergeDirty, !closed, !Task.isCancelled, generation == token {
                    await flushMerge(token: token)
                }
                let failedCount = states.reduce(0) { count, item in
                    if case .failed = item.status { return count + 1 }
                    return count
                }
                let exhaustedCount = states.reduce(0) { count, item in
                    if case .exhausted = item.status { return count + 1 }
                    return count
                }
                DebugRuntime.log(.search, sub: "novel", event: "wave.end", level: .info, data: .object([
                    "query": .string(keyword),
                    "rawCount": .int(raw.count),
                    "booksCount": .int(books.count),
                    "failedCount": .int(failedCount),
                    "exhaustedCount": .int(exhaustedCount)
                ]))
            }
        }
        running[taskID] = task
    }
    private func scheduleMerge(token: UUID, immediate: Bool) {
        if let current = mergeTask {
            if immediate, !mergingInFlight {
                current.cancel()
                mergeTask = nil
                mergeTaskID = nil
            } else {
                return
            }
        }
        let mergeID = UUID()
        mergeTaskID = mergeID
        let next = Task { [weak self] in
            guard let self else { return }
            defer {
                running[mergeID] = nil
                if mergeTaskID == mergeID {
                    mergeTask = nil
                    mergeTaskID = nil
                }
            }
            if !immediate {
                do { try await Task.sleep(nanoseconds: Self.mergeDebounceNanoseconds) }
                catch { return }
            }
            await flushMerge(token: token)
        }
        mergeTask = next
        running[mergeID] = next
    }
    private func flushMerge(token: UUID) async {
        guard !mergingInFlight else { return }
        mergingInFlight = true
        defer {
            mergingInFlight = false
            if !closed && mergeDirty && mergeTask == nil {
                scheduleMerge(token: generation, immediate: false)
            }
        }
        while mergeDirty, !closed, !Task.isCancelled, generation == token {
            mergeDirty = false
            mergePendingSources = 0
            let snapshot = raw
            let activeSources = feature.sources
            do {
                var merged = try await feature.rules.merge(snapshot, sources: activeSources)
                if let matchingBook {
                    merged = try await feature.rules.sourceCandidates(merged, for: matchingBook)
                }
                guard !closed, !Task.isCancelled, generation == token else { return }
                books = merged
                DebugRuntime.log(.search, sub: "novel", event: "merge.flush", level: .debug, data: .object([
                    "rawCount": .int(snapshot.count),
                    "mergedCount": .int(merged.count),
                    "matchingBook": .bool(matchingBook != nil)
                ]))
            } catch {
                if generation == token, !Task.isCancelled, !closed {
                    self.error = error.localizedDescription
                }
                return
            }
        }
    }
    public func cancel() {
        generation = UUID(); loading = false; active = false
        mergeDirty = false; mergePendingSources = 0
        mergeTask?.cancel(); mergeTask = nil; mergeTaskID = nil
        task?.cancel(); task = nil
        for index in states.indices { if states[index].status == .loading { states[index].status = .cancelled } }
    }
    public func close() async {
        closed = true; sourceObservation = nil; cancel()
        for task in Array(running.values) { await task.value }; running.removeAll()
    }
}
