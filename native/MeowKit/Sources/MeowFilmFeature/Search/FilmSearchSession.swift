#if os(iOS) || os(macOS)
import Foundation
import Combine
import MeowRuntime

/// A query owns cursors, both display orders and all acquired source/metadata
/// values. Views and detail navigation borrow this same observable owner.
@MainActor
public final class FilmSearchSession: ObservableObject {
    @Published public private(set) var snapshot: FilmSearchSnapshot
    private let processor: FilmSearchProcessor
    private let data: FilmSearchData
    private let readSites: FilmSearchService.Sites
    private let readRules: FilmSearchService.Rules
    private let metadata: (any FilmSearchMetadataReading)?
    private let metadataRevision: UUID
    private let sourceOwner: FilmSourceService?
    private weak var service: FilmSearchService?
    private struct Cursor: Sendable {
        let site: FilmSearchSite
        var next = 1
        var finished = false
        var identities: Set<Data> = []
    }
    private var cursors: [Cursor] = []
    struct Page {
        let site: FilmSearchSite
        let read: FilmSearchRead
    }
    private(set) var pages: [Page] = []
    private(set) var metadataReads: [String: FilmSearchRead] = [:]
    var admittedSources: [FilmPlaybackSource] { cursors.map { $0.site.playbackSource } }
    func confirmedSearch(sourceKey: String) -> RuntimeValue? {
        guard let cursor = cursors.first(where: { $0.site.key == sourceKey }), cursor.next > 1 else { return nil }
        return .object(["sourceKey": .string(sourceKey), "page": .number(Double(cursor.next - 1)),
                        "completed": .bool(cursor.finished)])
    }
    private var aggregateRules: [String] = []
    private var coverSite = ""
    private var order: [String] = [], rawOrder: [String] = []
    private var started = false, prepared = false, interrupted = false, initialSettled = false
    private var pendingSites: Set<Int> = []
    private var siteSucceededThisRound = false
    private var settledDetails: Set<String> = []
    private(set) var invalidated = false
    private var token = UUID()
    private var task: Task<Void, Never>?
    private var retired: [UUID: Task<Void, Never>] = [:]
    private var scopeWaiters: [UUID: CheckedContinuation<Void, Error>] = [:]

    init(query: String, service: FilmSearchService) {
        snapshot = FilmSearchSnapshot(sessionID: UUID(), query: query)
        processor = service.processor; data = service.data
        readSites = service.sites; readRules = service.rules
        metadata = service.metadata
        metadataRevision = service.metadataRevision
        sourceOwner = service.sourceOwner
        self.service = service
    }

    func belongs(to owner: FilmSearchService) -> Bool {
        service === owner && metadataRevision == owner.metadataRevision
    }

    func startIfNeeded() {
        guard service?.ownsWork(for: self) == true, !invalidated, !snapshot.query.isEmpty, !snapshot.loading else { return }
        if !started {
            started = true
            launch(initial: true)
        } else if interrupted {
            interrupted = false
            launch(initial: !initialSettled, resuming: true)
        }
    }

    func hasDifferentSources(_ scope: [String: Int64]) -> Bool {
        (prepared || !cursors.isEmpty) && FilmSearchService.authorizationScope(cursors.map(\.site)) != scope
    }

    func invalidateSources() {
        cancel()
        invalidated = true
        snapshot.hasMore = false
        snapshot.siteState = .cancelled
    }

    public func loadMore() {
        guard service?.ownsWork(for: self) == true, !invalidated, !snapshot.loading, prepared, snapshot.hasMore else { return }
        if interrupted { startIfNeeded(); return }
        launch(initial: false)
    }

    public func cancel() {
        finishSourceScope(.failure(CancellationError()))
        let run = token
        token = UUID()
        if let task {
            task.cancel(); retired[run] = task; self.task = nil
            Task { [weak self] in
                await task.value
                self?.retired[run] = nil
            }
        }
        guard snapshot.loading else { return }
        interrupted = true
        snapshot.loading = false
        if snapshot.siteState == .loading { snapshot.siteState = .cancelled }
        if snapshot.metadataState == .loading { snapshot.metadataState = .cancelled }
    }

    public func drain() async {
        var accepted = retired
        if let task { accepted[token] = task }
        for work in accepted.values { await work.value }
        for key in accepted.keys { retired[key] = nil }
    }

    public func imageURL(_ path: String) -> URL? {
        guard !path.isEmpty else { return nil }
        if let url = URL(string: path), ["http", "https"].contains(url.scheme?.lowercased() ?? "") { return url }
        return metadata?.imageURL(path: path)
    }

    public func context(for card: FilmSearchCard) -> FilmSearchContext {
        FilmSearchContext(session: self, card: card)
    }

    /// Admission waits for the source list only, never for all site responses.
    func waitForSourceScope() async throws {
        if prepared { return }
        guard !invalidated, snapshot.loading, snapshot.siteState != .failed else { throw FilmSearchError.unavailable }
        let id = UUID()
        try await withTaskCancellationHandler {
            try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<Void, Error>) in
                if Task.isCancelled { continuation.resume(throwing: CancellationError()) }
                else { scopeWaiters[id] = continuation }
            }
        } onCancel: {
            Task { @MainActor [weak self] in
                self?.scopeWaiters.removeValue(forKey: id)?.resume(throwing: CancellationError())
            }
        }
    }

    private func finishSourceScope(_ result: Result<Void, Error>) {
        let waiting = Array(scopeWaiters.values)
        scopeWaiters.removeAll()
        for waiter in waiting { waiter.resume(with: result) }
    }

    private func launch(initial: Bool, resuming: Bool = false) {
        let run = UUID(); token = run
        if !resuming || snapshot.round == 0 { snapshot.round += 1 }
        if !resuming { siteSucceededThisRound = false }
        if prepared, !resuming { pendingSites = Set(cursors.indices.filter { !cursors[$0].finished }) }
        snapshot.loading = true
        snapshot.siteState = .loading
        if initial, metadata == nil { snapshot.metadataState = .unavailable }
        else if initial, [.idle, .cancelled].contains(snapshot.metadataState) { snapshot.metadataState = .loading }
        task = Task { [weak self] in
            guard let self else { return }
            await self.execute(initial: initial, resuming: resuming, run: run)
        }
    }

    private enum Event: Sendable {
        case preparation([FilmSearchSite]?, [String]?)
        case site(Int, FilmSearchRead?)
        case metadata(FilmSearchRead?, String)
        case detail(String, FilmSearchRead?)
    }

    private func execute(initial: Bool, resuming: Bool, run: UUID) async {
        guard valid(run) else { return }
        // Both modes always use the last settled round, including after an
        // interrupted round resumes. A streaming callback is never a new batch.
        let previousOrder = order, previousRawOrder = rawOrder
        let data = data, metadata = metadata, processor = processor
        let query = snapshot.query
        let readSites = readSites, readRules = readRules
        var displayFailed = false
        await withTaskGroup(of: Event.self) { group in
            if !prepared {
                group.addTask {
                    do {
                        async let sites = readSites()
                        async let rules = readRules()
                        let prepared = try await (sites, rules)
                        return .preparation(prepared.0, prepared.1)
                    } catch { return .preparation(nil, nil) }
                }
            } else {
                for index in pendingSites {
                    let cursor = cursors[index]
                    group.addTask { await Self.readSite(cursor, index: index, query: query, data: data) }
                }
            }
            let needsSearch = initial && snapshot.metadataSearch == nil && snapshot.metadataState == .loading
            if needsSearch, let metadata {
                group.addTask {
                    do {
                        var read = try await metadata.searchRead(query: query)
                        let payload = read.value
                        var effectiveQuery = query
                        guard let results = payload.catField("results")?.catArray else { throw FilmSearchError.invalidResponse }
                        if results.isEmpty {
                            let fallback = try await processor.invoke(["operation": .string("fallback"), "query": .string(query)])
                            if let word = fallback.catField("query")?.catText, !word.isEmpty {
                                let next = try await metadata.searchRead(query: word)
                                guard let nextResults = next.value.catField("results")?.catArray else { throw FilmSearchError.invalidResponse }
                                if !nextResults.isEmpty { read = next; effectiveQuery = word }
                            }
                        }
                        return .metadata(read, effectiveQuery)
                    } catch { return .metadata(nil, query) }
                }
            } else if initial, snapshot.metadataSearch != nil, let metadata {
                for item in snapshot.tmdbItems where !settledDetails.contains(Self.metadataKey(item)) {
                    guard let type = item.catField("tmdbType")?.catText,
                          let id = item.catField("tmdbId")?.catInteger else { continue }
                    snapshot.metadataState = .loading
                    group.addTask { await Self.readDetail(type: type, id: id, metadata: metadata) }
                }
            }
            if prepared { completeSitesIfSettled() }
            while let event = await group.next() {
                guard valid(run), !displayFailed else { group.cancelAll(); continue }
                do {
                    switch event {
                    case .preparation(let sites, let rules):
                        if let sites, let rules {
                            cursors = sites.map { Cursor(site: $0) }
                            aggregateRules = rules
                            coverSite = sites.first(where: { $0.source.options.aggregateImages })?.key ?? ""
                            snapshot.noSources = sites.isEmpty
                            prepared = true
                            finishSourceScope(.success(()))
                            if let sourceOwner {
                                let current = await sourceOwner.snapshot
                                guard valid(run) else { group.cancelAll(); continue }
                                if hasDifferentSources(FilmSearchService.authorizationScope(FilmSearchService.searchableSites(current))) {
                                    invalidateSources()
                                    group.cancelAll()
                                    continue
                                }
                            }
                            pendingSites = Set(cursors.indices)
                            for index in pendingSites {
                                let cursor = cursors[index]
                                group.addTask { await Self.readSite(cursor, index: index, query: query, data: data) }
                            }
                            completeSitesIfSettled()
                        } else {
                            snapshot.siteState = .failed
                            finishSourceScope(.failure(FilmSearchError.unavailable))
                        }
                    case .site(let index, let read):
                        if let read {
                            try await acceptSite(read, index: index, run: run)
                            siteSucceededThisRound = true
                        }
                        guard valid(run) else { group.cancelAll(); continue }
                        pendingSites.remove(index)
                        completeSitesIfSettled()
                    case .metadata(let read, let effectiveQuery):
                        if let read {
                            let payload = read.value
                            let mapped = try await processor.invoke(["operation": .string("tmdb"),
                                "query": .string(query), "payload": payload])
                            guard valid(run), let items = mapped.catField("items")?.catArray else {
                                throw FilmSearchError.invalidResponse
                            }
                            snapshot.tmdbItems = items
                            snapshot.metadataSearch = payload
                            metadataReads["search"] = read
                            snapshot.metadataQuery = effectiveQuery
                            snapshot.metadataState = .completed
                            if let metadata {
                                for item in items {
                                    guard let type = item.catField("tmdbType")?.catText,
                                          let id = item.catField("tmdbId")?.catInteger else { continue }
                                    snapshot.metadataState = .loading
                                    group.addTask { await Self.readDetail(type: type, id: id, metadata: metadata) }
                                }
                            }
                        } else { snapshot.metadataState = .failed }
                    case .detail(let key, let read):
                        if let read, let index = snapshot.tmdbItems.firstIndex(where: { Self.metadataKey($0) == key }) {
                            let enhanced = try await processor.invoke(["operation": .string("detail"),
                                "item": snapshot.tmdbItems[index], "payload": read.value])
                            guard valid(run) else { group.cancelAll(); continue }
                            snapshot.tmdbItems[index] = enhanced
                            snapshot.metadataDetails[key] = read.value
                            metadataReads[key] = read
                        }
                        settledDetails.insert(key)
                        completeMetadataIfSettled()
                    }
                } catch {
                    guard valid(run) else { group.cancelAll(); continue }
                    switch event {
                    case .preparation: snapshot.siteState = .failed
                    case .metadata: snapshot.metadataState = .failed
                    case .detail(let key, _):
                        settledDetails.insert(key)
                        completeMetadataIfSettled()
                    case .site(let index, _):
                        pendingSites.remove(index)
                        completeSitesIfSettled()
                    }
                }
                do { try await render(previousOrder: previousOrder, previousRawOrder: previousRawOrder, run: run) }
                catch {
                    guard valid(run) else { group.cancelAll(); continue }
                    displayFailed = true
                    snapshot.siteState = .failed
                    snapshot.metadataState = .failed
                    finishSourceScope(.failure(FilmSearchError.invalidResponse))
                    group.cancelAll()
                }
            }
        }
        guard valid(run) else { return }
        // A cancellation can arrive after accepting a page but before rendering
        // it. Resuming a round with no remaining reads must still publish it.
        if resuming, !displayFailed {
            do { try await render(previousOrder: previousOrder, previousRawOrder: previousRawOrder, run: run) }
            catch {
                guard valid(run) else { return }
                snapshot.siteState = .failed
                snapshot.metadataState = .failed
            }
        }
        guard valid(run) else { return }
        if snapshot.metadataState == .loading { snapshot.metadataState = .completed }
        if initial { initialSettled = true }
        if !prepared { finishSourceScope(.failure(FilmSearchError.unavailable)) }
        snapshot.loading = false
        snapshot.revision += 1
        snapshot.hasMore = cursors.contains { !$0.finished }
        order = snapshot.cards.map(\.displayKey)
        rawOrder = snapshot.rawCards.map(\.displayKey)
        task = nil
    }

    private nonisolated static func readSite(_ cursor: Cursor, index: Int, query: String,
                                             data: FilmSearchData) async -> Event {
        do {
            let read = try await data.search(site: cursor.site, query: query, page: cursor.next)
            let payload = try sitePayload(read.value)
            return .site(index, .init(value: payload, producer: read.producer, cacheEntry: read.cacheEntry))
        } catch { return .site(index, nil) }
    }

    private nonisolated static func readDetail(type: String, id: Int,
                                               metadata: any FilmSearchMetadataReading) async -> Event {
        do {
            let read = try await metadata.detailRead(type: type, id: id)
            guard case .object = read.value else { throw FilmSearchError.invalidResponse }
            return .detail("\(type):\(id)", read)
        } catch { return .detail("\(type):\(id)", nil) }
    }

    private func acceptSite(_ read: FilmSearchRead, index: Int, run: UUID) async throws {
        let payload = try Self.sitePayload(read.value)
        let cursor = cursors[index], site = cursor.site
        try await validateSource(site, run: run)
        let result = try await processor.invoke([
            "operation": .string("site"), "query": .string(snapshot.query),
            "aggregateRules": .array(aggregateRules.map(RuntimeValue.string)),
            "site": .object(["siteKey": .string(site.key), "siteId": .string(site.source.key.siteKey),
                "installationId": .string(site.cat.installationId), "generation": .number(Double(site.cat.generation)),
                "siteName": .string(site.source.name), "order": .number(Double(site.source.order))]),
            "page": .number(Double(cursor.next)), "payload": payload])
        guard valid(run) else { throw CancellationError() }
        try await validateSource(site, run: run)
        guard let items = result.catField("items")?.catArray else { throw FilmSearchError.invalidResponse }
        var next = cursor
        let added = try items.filter { item in
            guard let id = item.catField("detailId") else { throw FilmSearchError.invalidResponse }
            _ = try FilmPlaybackIdentifier(id)
            return next.identities.insert(try RuntimeJSON.encode(id)).inserted
        }
        if let pageCount = result.catField("pageCount")?.catInteger {
            next.finished = next.next >= pageCount
        } else { next.finished = items.isEmpty || added.isEmpty }
        pages.append(Page(site: site, read: read))
        next.next += 1
        cursors[index] = next
        snapshot.siteItems.append(contentsOf: added)
        snapshot.hasMore = cursors.contains { !$0.finished }
    }

    private func validateSource(_ site: FilmSearchSite, run: UUID) async throws {
        if let sourceOwner {
            let current = await sourceOwner.snapshot
            guard valid(run) else { throw CancellationError() }
            guard FilmSearchService.searchableSites(current).contains(where: {
                $0.key == site.key && $0.cat.generation == site.cat.generation
            }) else {
                invalidateSources()
                throw CancellationError()
            }
        }
    }

    private func render(previousOrder: [String], previousRawOrder: [String], run: UUID) async throws {
        var fields: [String: RuntimeValue] = ["operation": .string("display"), "query": .string(snapshot.query),
            "aggregateRules": .array(aggregateRules.map(RuntimeValue.string)),
            "siteItems": .array(snapshot.siteItems), "tmdbItems": .array(snapshot.tmdbItems),
            "siteOrder": .array(cursors.map { .string($0.site.key) }), "searchCoverSite": .string(coverSite)]
        fields["rawListMode"] = .bool(false)
        fields["previousOrder"] = .array(previousOrder.map(RuntimeValue.string))
        let grouped = try await processor.invoke(fields)
        guard valid(run) else { throw CancellationError() }
        fields["rawListMode"] = .bool(true)
        fields["previousOrder"] = .array(previousRawOrder.map(RuntimeValue.string))
        let raw = try await processor.invoke(fields)
        guard valid(run) else { throw CancellationError() }
        if let sourceOwner {
            let current = await sourceOwner.snapshot
            guard valid(run) else { throw CancellationError() }
            if hasDifferentSources(FilmSearchService.authorizationScope(FilmSearchService.searchableSites(current))) {
                invalidateSources()
                throw CancellationError()
            }
        }
        guard let cards = grouped.catField("items")?.catArray, let rawCards = raw.catField("items")?.catArray else {
            throw FilmSearchError.invalidResponse
        }
        snapshot.cards = cards.map(FilmSearchCard.init)
        snapshot.rawCards = rawCards.map(FilmSearchCard.init)
        snapshot.revision += 1
    }

    private func valid(_ run: UUID) -> Bool { token == run && !Task.isCancelled }
    private func completeSitesIfSettled() {
        guard pendingSites.isEmpty else { return }
        snapshot.siteState = cursors.isEmpty || siteSucceededThisRound ? .completed : .failed
    }
    private func completeMetadataIfSettled() {
        if snapshot.metadataSearch != nil, snapshot.tmdbItems.allSatisfy({ settledDetails.contains(Self.metadataKey($0)) }) {
            snapshot.metadataState = .completed
        }
    }
    private nonisolated static func metadataKey(_ item: RuntimeValue) -> String {
        "\(item.catField("tmdbType")?.catText ?? ""):\(item.catField("tmdbId")?.catInteger ?? 0)"
    }
    private nonisolated static func sitePayload(_ value: RuntimeValue) throws -> RuntimeValue {
        let payload: RuntimeValue
        if case .string(let text) = value { payload = try CatScriptTransport.parse(Data(text.utf8)) }
        else { payload = value }
        guard case .object = payload, payload.catField("list")?.catArray != nil,
              payload.catField("ok") != .bool(false), payload.catField("success") != .bool(false),
              payload.catField("error")?.catText?.isEmpty != false else { throw FilmSearchError.invalidResponse }
        return payload
    }

    isolated deinit { task?.cancel(); for work in retired.values { work.cancel() } }
}
#endif
