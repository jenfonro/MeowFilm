#if os(iOS) || os(macOS)
import Foundation
import Combine
import MeowRuntime

/// A display snapshot and consumer lifetime. Node owns reads, cursors, rules,
/// cards and search-to-playback facts.
@MainActor
public final class FilmSearchSession: ObservableObject {
    @Published public private(set) var snapshot: FilmSearchSnapshot
    @Published public private(set) var error: FilmPlaybackError?
    private let transport: FilmSearchTransport
    private let readSites: FilmSearchService.Sites
    private let metadataRevision: UUID
    private weak var service: FilmSearchService?
    private var sources: [FilmPlaybackSource]?
    private var allocatedID: UUID?
    private var started = false, stopped = false, released = false
    private(set) var invalidated = false
    private var work: Task<Void, Never>?
    private var admission: Task<[FilmPlaybackSource], Error>?
    private var observation: Task<Void, Never>?
    private var observationID = UUID()
    private var publicationID = UUID()
    private var releaseTask: Task<Void, Never>?
    private var scopeWaiters: [UUID: CheckedContinuation<Void, Error>] = [:]
    private var settleWaiters: [UUID: CheckedContinuation<Void, Never>] = [:]

    init(query: String, service: FilmSearchService) {
        snapshot = .init(sessionID: UUID(), query: query)
        transport = service.transport; readSites = service.sites
        metadataRevision = service.metadataRevision
        self.service = service
    }

    func belongs(to owner: FilmSearchService) -> Bool {
        service === owner && metadataRevision == owner.metadataRevision
    }

    func hasDifferentSources(_ scope: [Data: RuntimeValue]) -> Bool {
        sources.map { FilmSearchService.authorizationScope($0) != scope } ?? false
    }

    func startIfNeeded() {
        guard service?.ownsWork(for: self) == true, !invalidated, !released, !snapshot.query.isEmpty else { return }
        if !started || allocatedID == nil && error != nil {
            started = true; stopped = false; snapshot.loading = true; error = nil
            let token = publicationID
            enqueue { session in await session.open(publication: token) }
        } else if stopped {
            stopped = false
            let token = publicationID
            enqueue { session in
                if session.allocatedID == nil { await session.open(publication: token) }
                else { await session.command("resume", publication: token) }
            }
        } else if error != nil, allocatedID != nil {
            error = nil
            let token = publicationID
            enqueue { session in
                do { try await session.refresh() }
                catch {
                    guard session.publicationID == token, !session.stopped, !session.invalidated, !session.released,
                          !Self.cancelled(error) else { return }
                    session.error = Self.failure(error)
                }
            }
        } else if observation == nil, allocatedID != nil {
            observe()
        }
    }

    public func loadMore() {
        guard service?.ownsWork(for: self) == true, !invalidated, !released,
              !snapshot.loading, snapshot.hasMore, allocatedID != nil else { return }
        if stopped { startIfNeeded(); return }
        snapshot.loading = true
        let token = publicationID
        enqueue { session in await session.command("more", publication: token) }
    }

    /// Retains the Node session and its successful pages. Accepted finite
    /// producers belong to Node and can still settle after consumer stop.
    public func cancel() {
        guard !released, !stopped else { return }
        stopped = true
        publicationID = UUID()
        let token = publicationID
        admission?.cancel()
        let observer = observation
        observationID = UUID(); observation = nil
        observer?.cancel()
        snapshot.loading = false
        if [.idle, .loading].contains(snapshot.siteState) { snapshot.siteState = .cancelled }
        if [.idle, .loading].contains(snapshot.metadataState) { snapshot.metadataState = .cancelled }
        finishSettlement()
        finishScope(.failure(CancellationError()))
        enqueue { session in
            await observer?.value
            if session.allocatedID != nil { await session.command("stop", publication: token) }
            else { session.snapshot.loading = false; session.finishSettlement() }
        }
    }

    func invalidateSources() {
        invalidated = true
        snapshot.loading = false; finishSettlement()
        cancel()
    }

    /// A persistent SSE connection is not pending content work.
    public func drain() async {
        await work?.value
        guard snapshot.loading else { return }
        let id = UUID()
        await withTaskCancellationHandler {
            await withCheckedContinuation { continuation in
                if Task.isCancelled { continuation.resume() }
                else { settleWaiters[id] = continuation }
            }
        } onCancel: {
            Task { @MainActor [weak self] in self?.settleWaiters.removeValue(forKey: id)?.resume() }
        }
    }

    func waitForSourceScope() async throws {
        if allocatedID != nil { return }
        guard !invalidated, !released, !stopped, error == nil else { throw FilmSearchError.unavailable }
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

    public func refresh() async throws {
        guard let id = allocatedID, !released, !invalidated else { throw FilmSearchError.closed }
        let token = publicationID, previous = observation
        observationID = UUID(); observation = nil
        previous?.cancel(); await previous?.value
        let value = try FilmSearchSnapshot(value: await transport.call("search.snapshot", fields(id)))
        guard !released, !invalidated, !stopped, publicationID == token else { throw CancellationError() }
        try Task.checkCancellation()
        try accept(value)
        error = nil
        observe()
    }

    public func imageURL(_ path: String) -> URL? {
        guard !path.isEmpty else { return nil }
        if let url = URL(string: path), ["http", "https"].contains(url.scheme?.lowercased() ?? "") { return url }
        return RuntimeHTTPClient.metadataImageURL(path)
    }

    public func context(for card: FilmSearchCard) -> FilmSearchContext {
        FilmSearchContext(session: self, card: card)
    }

    func handoff(selectionID: String, sources: [FilmPlaybackSource],
                 owner: RuntimeHTTPClient?) async throws -> FilmSearchHandoff {
        try await waitForSourceScope()
        guard let id = allocatedID, !released else { throw FilmSearchError.closed }
        let producer = try await transport.producer()
        guard producer === owner else { throw FilmSearchError.unavailable }
        var input = fields(id)
        input["selectionId"] = .string(selectionID)
        input["sources"] = .array(sources.map { .object($0.value) })
        let handoff = try FilmSearchHandoff(await transport.call("search.handoff", input))
        guard handoff.reference.filmObject?["sessionId"]?.filmText.flatMap(UUID.init(uuidString:)) == id else {
            throw FilmSearchError.invalidResponse
        }
        return handoff
    }

    /// Establishes the terminal state before another queued operation can run.
    func release() -> Task<Void, Never> {
        if let releaseTask { return releaseTask }
        released = true; invalidated = true; stopped = true
        publicationID = UUID()
        admission?.cancel()
        observationID = UUID(); observation?.cancel()
        let accepted = work, observer = observation
        observation = nil
        let task = Task { [self] in
            await accepted?.value
            await observer?.value
            if let id = allocatedID {
                do { _ = try await transport.call("search.release", fields(id)) }
                catch { self.error = Self.failure(error) }
            }
            work = nil
        }
        releaseTask = task
        snapshot.loading = false
        if [.idle, .loading].contains(snapshot.siteState) { snapshot.siteState = .cancelled }
        if [.idle, .loading].contains(snapshot.metadataState) { snapshot.metadataState = .cancelled }
        finishSettlement()
        finishScope(.failure(FilmSearchError.closed))
        return task
    }

    private func open(publication token: UUID) async {
        do {
            guard publicationID == token, !stopped, !released else { return }
            let admission = Task { try await readSites().map(\.playbackSource) }
            self.admission = admission
            let sources = try await admission.value
            self.admission = nil
            guard publicationID == token, !stopped, !released, !invalidated else { throw CancellationError() }
            self.sources = sources
            if let service, !service.accepts(sources) { invalidateSources(); throw CancellationError() }
            // Once admitted, drain the RPC: its identity is needed for release.
            let response = try await transport.call("search.open", [
                "query": .string(snapshot.query), "sources": .array(sources.map { .object($0.value) }),
            ])
            if let text = response.filmObject?["sessionId"]?.filmText { allocatedID = UUID(uuidString: text) }
            let value = try FilmSearchSnapshot(value: response)
            allocatedID = value.sessionID
            guard publicationID == token, !stopped, !released, !invalidated else { return }
            try accept(value)
            finishScope(.success(Void()))
            observe()
        } catch {
            admission = nil
            guard publicationID == token, !released, !invalidated else { return }
            snapshot.loading = false
            if !Self.cancelled(error) { self.error = Self.failure(error) }
            finishScope(.failure(error)); finishSettlement()
        }
    }

    private func command(_ action: String, publication token: UUID) async {
        guard let id = allocatedID, !released else { return }
        guard action == "stop" || publicationID == token else { return }
        if action == "more", stopped { return }
        do {
            var input = fields(id); input["action"] = .string(action)
            let value = try FilmSearchSnapshot(value: await transport.call("search.command", input))
            guard publicationID == token, !released, !invalidated,
                  action == "stop" || !stopped else { return }
            try accept(value, stopping: action == "stop"); error = nil
            if action == "resume" { finishScope(.success(Void())); observe() }
        } catch {
            guard publicationID == token, !released, !invalidated else { return }
            snapshot.loading = false
            if !Self.cancelled(error) { self.error = Self.failure(error) }
            finishSettlement()
        }
    }

    private func observe() {
        guard observation == nil, !stopped, !released, !invalidated, let id = allocatedID else { return }
        let token = UUID(); observationID = token
        let after = snapshot.sequence, transport = transport
        observation = Task { [weak self] in
            do {
                try await transport.snapshots(sessionID: id, after: after) { [weak self] value in
                    try await self?.receive(value, token: token)
                }
            } catch {
                guard let self, self.observationID == token, !Task.isCancelled, !self.released else { return }
                self.error = Self.failure(error)
                self.snapshot.loading = false
                self.finishSettlement()
            }
            guard let self, self.observationID == token else { return }
            self.observation = nil
        }
    }

    private func receive(_ value: FilmSearchSnapshot, token: UUID) throws {
        guard observationID == token, !stopped, !released, !invalidated else { return }
        try accept(value)
        error = nil
    }

    private func accept(_ value: FilmSearchSnapshot, stopping: Bool = false) throws {
        guard value.sessionID == allocatedID, value.query.utf8.elementsEqual(snapshot.query.utf8) else {
            throw FilmSearchError.invalidResponse
        }
        guard value.sequence > snapshot.sequence else { return }
        guard value.revision >= snapshot.revision else { throw FilmSearchError.invalidResponse }
        if stopping {
            // A late open may have allocated an identity without ever publishing.
            // Stop acknowledges only control state, never its newly settled data.
            var state = snapshot.sessionID == value.sessionID ? snapshot :
                FilmSearchSnapshot(sessionID: value.sessionID, query: snapshot.query)
            state.sequence = value.sequence; state.revision = value.revision; state.round = value.round
            state.siteState = value.siteState; state.metadataState = value.metadataState
            state.loading = value.loading; state.hasMore = value.hasMore; state.noSources = value.noSources
            snapshot = state
        } else {
            snapshot = value
        }
        if !value.loading { finishSettlement() }
    }

    private func enqueue(_ operation: @escaping @MainActor (FilmSearchSession) async -> Void) {
        let previous = work
        work = Task { [self] in
            await previous?.value
            await operation(self)
        }
    }

    private func fields(_ id: UUID) -> RuntimeObject { ["sessionId": .string(id.uuidString.lowercased())] }
    private func finishScope(_ result: Result<Void, Error>) {
        let pending = Array(scopeWaiters.values); scopeWaiters.removeAll()
        for waiter in pending { waiter.resume(with: result) }
    }
    private func finishSettlement() {
        let pending = Array(settleWaiters.values); settleWaiters.removeAll()
        for waiter in pending { waiter.resume() }
    }
    private static func cancelled(_ error: Error) -> Bool {
        error is CancellationError || (error as? RuntimeHTTPError) == .cancelled
    }
    private static func failure(_ error: Error) -> FilmPlaybackError {
        if let error = error as? FilmPlaybackError { return error }
        if case RuntimeHTTPError.remote(let code) = error { return .init(code: code) }
        if let error = error as? FilmSearchError {
            switch error {
            case .invalidResponse: return .init(code: "INVALID_RESPONSE")
            case .closed: return .init(code: "CLOSED")
            case .unavailable: return .init(code: "HOST_UNAVAILABLE")
            }
        }
        return .init(code: "HOST_UNAVAILABLE")
    }

    isolated deinit {
        admission?.cancel(); observation?.cancel()
        if !released, let id = allocatedID {
            let transport = transport
            Task { _ = try? await transport.call("search.release", ["sessionId": .string(id.uuidString.lowercased())]) }
        }
    }
}
#endif
