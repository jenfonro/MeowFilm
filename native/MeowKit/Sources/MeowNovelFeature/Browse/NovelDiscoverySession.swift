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

public struct NovelDiscoveryChannel: Identifiable, Equatable, Sendable {
    public var id: String
    public var title: String
    public var items: [NovelDiscoveryItem]
    public init(id: String, title: String, items: [NovelDiscoveryItem]) {
        self.id = id; self.title = title; self.items = items
    }

    static func make(_ kinds: [NovelExploreKind]) -> [Self] {
        var channels: [Self] = []
        var heading: (id: String, title: String)?
        for (index, kind) in kinds.enumerated() {
            let title = kind.title.trimmingCharacters(in: .whitespacesAndNewlines)
            if kind.url.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
                if !title.isEmpty { heading = ("group-\(index)", title) }
                continue
            }
            let item = NovelDiscoveryItem(id: "item-\(index)", title: title.isEmpty ? "未命名" : title, url: kind.url)
            if let heading {
                if channels.last?.id != heading.id {
                    channels.append(.init(id: heading.id, title: heading.title, items: []))
                }
                channels[channels.count - 1].items.append(item)
            } else {
                channels.append(.init(id: item.id, title: item.title, items: [item]))
            }
        }
        return channels
    }
}

@MainActor
public final class NovelDiscoverySession: ObservableObject {
    public let feature: NovelFeature
    @Published public private(set) var selectedSource: NovelSource?
    @Published public private(set) var channels: [NovelDiscoveryChannel] = []
    @Published public private(set) var selectedChannelID: String?
    @Published public private(set) var selectedKindID: String?
    @Published public private(set) var books: [NovelBook] = []
    @Published public private(set) var loading = false
    @Published public private(set) var canLoadMore = false
    @Published public var error: String?

    private enum Request: Equatable { case refresh, page }
    private var unfinished: Request? = .refresh
    private var failedRequest: Request?
    private var initialized = false
    private var active = false
    private var closed = false
    private var page = 0
    private var generation = UUID()
    private var running: [UUID: Task<Void, Never>] = [:]
    private var sourceObservation: AnyCancellable?

    public init(feature: NovelFeature) {
        self.feature = feature
        sourceObservation = feature.$sources.dropFirst().sink { [weak self] sources in
            self?.sourcesChanged(sources)
        }
    }

    public var availableSources: [NovelSource] { feature.sources.filter(Self.canExplore) }
    public var selectedChannel: NovelDiscoveryChannel? { channels.first { $0.id == selectedChannelID } }
    public var selectedKind: NovelDiscoveryItem? { selectedChannel?.items.first { $0.id == selectedKindID } }

    public func activate() {
        guard !closed, !feature.closed else { return }
        active = true
        feature.start()
        if !loading, let unfinished { begin(unfinished) }
    }

    public func deactivate() { active = false; invalidate() }

    public func selectSource(id: String) {
        guard !closed, let source = availableSources.first(where: { $0.id == id }) else { return }
        guard selectedSource?.id != source.id || selectedSource?.revision != source.revision else { return }
        invalidate(); selectedSource = source; clearSource()
        unfinished = .refresh
        if active { begin(.refresh) }
    }

    public func selectChannel(id: String) {
        guard !closed, let channel = channels.first(where: { $0.id == id }),
              let kind = channel.items.first, selectedChannelID != id else { return }
        select(channel: channel, kind: kind)
    }

    public func selectKind(id: String) {
        guard !closed, let channel = selectedChannel,
              let kind = channel.items.first(where: { $0.id == id }), selectedKindID != id else { return }
        select(channel: channel, kind: kind)
    }

    private func select(channel: NovelDiscoveryChannel, kind: NovelDiscoveryItem) {
        invalidate(); selectedChannelID = channel.id; selectedKindID = kind.id
        books = []; page = 0; canLoadMore = false; error = nil; unfinished = .page
        if active { begin(.page) }
    }

    public func refresh() {
        guard !closed else { return }
        DebugRuntime.log(.novel, sub: "discovery", event: "refresh", level: .debug)
        unfinished = .refresh
        if active { begin(.refresh) }
    }

    public func loadMore() {
        guard !closed, active, !loading, canLoadMore else { return }
        DebugRuntime.log(.novel, sub: "discovery", event: "load_more", level: .debug, data: .object([
            "page": .int(page)
        ]))
        begin(.page)
    }

    public func retry() {
        guard !closed, active, !loading else { return }
        begin(failedRequest ?? .refresh)
    }

    private static func canExplore(_ source: NovelSource) -> Bool {
        source.enabled && source.enabledExplore && source.actions.contains("exploreKinds") && source.actions.contains("explore")
    }

    private func accepts(_ source: NovelSource) -> Bool {
        !feature.closed && availableSources.contains { $0.id == source.id && $0.revision == source.revision }
    }

    private func sourcesChanged(_ sources: [NovelSource]) {
        guard !closed else { return }
        // Forward changes even when only an unselected sidebar source changed.
        objectWillChange.send()
        guard initialized else { return }
        guard let previous = selectedSource else {
            guard let source = sources.first(where: Self.canExplore) else { return }
            selectedSource = source; unfinished = .refresh
            if active { begin(.refresh) }
            return
        }
        guard let current = sources.first(where: { $0.id == previous.id }), Self.canExplore(current) else {
            invalidate()
            // Retain the selected identity. A disappearing source never selects
            // another source on the user's behalf.
            if let current = sources.first(where: { $0.id == previous.id }) { selectedSource = current }
            clearSource(); unfinished = nil; error = NovelError.noSource.localizedDescription
            return
        }
        selectedSource = current
        if current.revision != previous.revision || !Self.canExplore(previous) {
            invalidate(); clearSource(); unfinished = .refresh
        }
        if active, !loading, let unfinished { begin(unfinished) }
    }

    private func clearSource() {
        channels = []; selectedChannelID = nil; selectedKindID = nil
        books = []; page = 0; canLoadMore = false; error = nil; failedRequest = nil
    }

    private func invalidate() {
        generation = UUID(); loading = false
        // Cancellation signals the actual runtime calls. Keep every accepted
        // task until its completion so close can drain even late transports.
        for task in running.values { task.cancel() }
    }

    private func begin(_ request: Request) {
        invalidate(); unfinished = request; failedRequest = nil; loading = true; error = nil
        let token = generation, taskID = UUID()
        let task = Task { [weak self] in
            guard let self else { return }
            defer {
                running[taskID] = nil
                if generation == token { loading = false }
            }
            do {
                try Task.checkCancellation()
                guard !closed, generation == token else { return }
                if !initialized {
                    try await feature.refreshSources()
                    try Task.checkCancellation()
                    guard !closed, generation == token else { return }
                    initialized = true
                    if let selectedSource, let current = feature.sources.first(where: { $0.id == selectedSource.id }) {
                        self.selectedSource = current
                    }
                }
                if selectedSource == nil { selectedSource = availableSources.first }
                guard let source = selectedSource else { unfinished = nil; return }
                guard accepts(source) else {
                    unfinished = nil; error = NovelError.noSource.localizedDescription; return
                }
                var nextChannels = channels
                var channel = selectedChannel
                var kind = selectedKind
                let selectedURL = kind?.url
                if request == .refresh {
                    let kinds = try await feature.sourcesClient.kinds(source)
                    guard isCurrent(token, source: source) else { return }
                    nextChannels = NovelDiscoveryChannel.make(kinds)
                    channel = nextChannels.first { $0.id == selectedChannelID && $0.items.contains { $0.url == selectedURL } } ??
                        nextChannels.first { $0.items.contains { $0.url == selectedURL } } ?? nextChannels.first
                    kind = channel?.items.first { $0.id == selectedKindID && $0.url == selectedURL } ??
                        channel?.items.first { $0.url == selectedURL } ?? channel?.items.first
                }
                let nextPage = request == .refresh ? 1 : page + 1
                let incoming: [NovelBook]
                if let kind {
                    incoming = try await feature.sourcesClient.explore(source, url: kind.url, page: nextPage)
                } else { incoming = [] }
                guard isCurrent(token, source: source) else { return }
                var nextBooks = request == .refresh ? [] : books
                var identities = Set(nextBooks.map { $0.installationId + "\n" + $0.bookUrl })
                let added = incoming.filter { identities.insert($0.installationId + "\n" + $0.bookUrl).inserted }
                nextBooks.append(contentsOf: added)
                // No partial refresh: kinds and first-page books are published
                // only once the complete request succeeded.
                channels = nextChannels; selectedChannelID = channel?.id; selectedKindID = kind?.id
                books = nextBooks; page = kind == nil ? 0 : nextPage
                canLoadMore = !added.isEmpty; unfinished = nil
            } catch {
                guard !closed, !Task.isCancelled, generation == token else { return }
                if error is CancellationError { return }
                unfinished = nil
                failedRequest = request
                self.error = (error as? RuntimeHTTPError) == .remote("WEB_INTERACTION_REQUIRED") ?
                    "书源需要网页验证" : error.localizedDescription
            }
        }
        running[taskID] = task
    }

    private func isCurrent(_ token: UUID, source: NovelSource) -> Bool {
        !closed && !Task.isCancelled && generation == token &&
            selectedSource?.id == source.id && selectedSource?.revision == source.revision && accepts(source)
    }

    public func close() async {
        closed = true; active = false; sourceObservation = nil; invalidate()
        for task in Array(running.values) { await task.value }
    }
}
