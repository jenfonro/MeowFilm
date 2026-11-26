import Combine
import Foundation
import MeowDebug
import MeowRuntime

@MainActor
public final class FilmHistoryModel: ObservableObject {
    private static let pageSize = 100

    private let transport: FilmPlaybackTransport
    @Published public private(set) var items: [FilmItem] = []
    @Published public private(set) var isLoading = false
    @Published public private(set) var hasMore = false
    @Published public private(set) var error: FilmPlaybackError?

    private var nextCursor: RuntimeObject?
    private var activeTask: Task<Void, Never>?
    private var closed = false
    private var loadedOnce = false

    public init(transport: FilmPlaybackTransport) {
        self.transport = transport
    }

    public func loadIfNeeded() {
        guard !closed, !loadedOnce, !isLoading else { return }
        reload()
    }

    public func reload() {
        guard !closed else { return }
        activeTask?.cancel()
        isLoading = true
        error = nil
        activeTask = Task { [weak self, transport] in
            do {
                let raw = try await transport.call(
                    "library.get",
                    parameters: .object(["limit": .number(Double(Self.pageSize))])
                )
                guard !Task.isCancelled, let self, !self.closed else { return }
                let parsed = try Self.parsePage(raw)
                self.items = parsed.items
                self.nextCursor = parsed.cursor
                self.hasMore = parsed.cursor != nil
                self.loadedOnce = true
                self.isLoading = false
                self.error = nil
            } catch {
                guard !Task.isCancelled, let self, !self.closed else { return }
                self.isLoading = false
                self.error = (error as? FilmPlaybackError) ?? FilmPlaybackError(code: "LIBRARY_UPDATE_FAILED")
                DebugRuntime.logError(.filmPlayback, sub: "history.model", event: "reload.failed", error: error)
            }
        }
    }

    public func loadMore() {
        guard !closed, !isLoading, let cursor = nextCursor else { return }
        activeTask?.cancel()
        isLoading = true
        error = nil
        activeTask = Task { [weak self, transport] in
            do {
                let raw = try await transport.call(
                    "library.get",
                    parameters: .object([
                        "limit": .number(Double(Self.pageSize)),
                        "historyAfter": .object(cursor)
                    ])
                )
                guard !Task.isCancelled, let self, !self.closed else { return }
                let parsed = try Self.parsePage(raw)
                var seen = Set(self.items.map { $0.targetID ?? $0.id })
                var merged = self.items
                for candidate in parsed.items {
                    let key = candidate.targetID ?? candidate.id
                    if seen.insert(key).inserted {
                        merged.append(candidate)
                    }
                }
                self.items = merged
                self.nextCursor = parsed.cursor
                self.hasMore = parsed.cursor != nil
                self.isLoading = false
            } catch {
                guard !Task.isCancelled, let self, !self.closed else { return }
                self.isLoading = false
                self.error = (error as? FilmPlaybackError) ?? FilmPlaybackError(code: "LIBRARY_UPDATE_FAILED")
                DebugRuntime.logError(.filmPlayback, sub: "history.model", event: "loadMore.failed", error: error)
            }
        }
    }

    public func clearAll() async throws {
        guard !closed else { throw FilmPlaybackError(code: "CLOSED") }
        activeTask?.cancel()
        activeTask = nil
        _ = try await transport.call("library.remove", parameters: .object(["kind": .string("history")]))
        guard !closed else { return }
        items = []
        nextCursor = nil
        hasMore = false
        isLoading = false
        error = nil
    }

    public func cancel() {
        activeTask?.cancel()
        activeTask = nil
        isLoading = false
    }

    public func close() {
        closed = true
        cancel()
    }

    private static func parsePage(_ raw: RuntimeValue) throws -> (items: [FilmItem], cursor: RuntimeObject?) {
        let fields = try raw.filmRequiredObject()
        let historyRows = fields["history"]?.filmArray ?? []
        var items: [FilmItem] = []
        items.reserveCapacity(historyRows.count)
        for row in historyRows {
            guard let record = try? FilmPlaybackHistory(row), let entry = record.entry else { continue }
            let posterURL = FilmPlaybackPresentation.artwork(.string(entry.poster))
            let sourceID = entry.tmdb != nil ? "tmdb" : entry.douban != nil ? "douban" : "history"
            let itemID = entry.tmdb.map { String($0.id) } ?? entry.douban?.id ?? entry.targetId
            items.append(FilmItem(
                sourceID: sourceID,
                id: itemID,
                title: entry.title,
                kind: entry.kind,
                posterURL: posterURL,
                largePosterURL: posterURL,
                score: entry.score,
                subtitle: entry.siteLabel,
                entry: entry
            ))
        }
        let cursor = fields["historyAfter"]?.filmObject
        return (items, cursor)
    }
}
