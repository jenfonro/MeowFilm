#if os(iOS) || os(macOS)
import Foundation
import Combine
import MeowDebug
import MeowRuntime

/// A display snapshot and consumer lifetime. Node owns reads, cursors, rules,
/// cards and search-to-playback facts.
@MainActor
public final class FilmSearchSession: ObservableObject {
    private static let maxObserveReconnectAttempts = 3

    @Published public private(set) var snapshot: FilmSearchSnapshot
    @Published public private(set) var error: FilmPlaybackError?
    private var transport: FilmSearchTransport
    private let readSites: FilmSearchService.Sites
    private let metadataRevision: UUID
    private weak var service: FilmSearchService?
    private var sources: [FilmPlaybackSource]?
    private var allocatedID: UUID?
    private var started = false, stopped = false, released = false
    private var runtimeClosed = false
    private var presentationVisible = false
    private(set) var invalidated = false
    private var work: Task<Void, Never>?
    private var admission: Task<[FilmPlaybackSource], Error>?
    private var observation: Task<Void, Never>?
    private var runtimeAvailabilityTask: Task<Void, Never>?
    private var observationID = UUID()
    private var publicationID = UUID()
    private var releaseTask: Task<Void, Never>?
    private var settleWaiters: [UUID: CheckedContinuation<Void, Never>] = [:]

    init(query: String, service: FilmSearchService, presentation: FilmSearchSnapshot? = nil) {
        let initial = FilmSearchSnapshot(sessionID: UUID(), query: query)
        snapshot = presentation.map { initial.retainingDisplay($0) } ?? initial
        transport = service.transport.renewed(); readSites = service.sites
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
        if runtimeClosed {
            publicationID = UUID(); observationID = UUID()
            observation?.cancel(); observation = nil
            transport = transport.renewed()
            allocatedID = nil; runtimeClosed = false; started = false
        }
        if !started || allocatedID == nil && error != nil {
            started = true; stopped = false; snapshot.loading = true; clearError()
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
            clearError()
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

    func setPresentationVisible(_ visible: Bool) {
        presentationVisible = visible
        if visible { startIfNeeded() }
    }

    func runtimeDidBecomeAvailable() {
        guard presentationVisible, !released, !invalidated else { return }
        runtimeAvailabilityTask?.cancel()
        let publication = publicationID
        runtimeAvailabilityTask = Task { [weak self] in
            guard let self else { return }
            defer { if self.publicationID == publication { self.runtimeAvailabilityTask = nil } }
            let borrowed = await self.transport.borrowedProducer()
            guard !Task.isCancelled, self.presentationVisible, !self.released, !self.invalidated,
                  self.publicationID == publication else { return }
            if borrowed?.isClosed == true || self.runtimeClosed || (self.allocatedID == nil && self.error != nil) {
                DebugRuntime.log(.search, sub: "session", event: "runtime.recover", level: .info, data: .object([
                    "hadSession": .bool(self.allocatedID != nil),
                ]))
                self.runtimeClosed = true
                self.startIfNeeded()
            }
        }
    }

    public func reconnect() {
        guard !released, !invalidated else { return }
        if runtimeClosed || allocatedID == nil {
            startIfNeeded()
            return
        }
        clearError()
        let token = publicationID
        enqueue { session in
            do { try await session.refresh() }
            catch {
                guard session.publicationID == token, !session.stopped, !session.invalidated, !session.released,
                      !Self.cancelled(error) else { return }
                session.error = Self.failure(error)
            }
        }
    }

    public func loadMore() {
        guard service?.ownsWork(for: self) == true, !invalidated, !released,
              !snapshot.loading, allocatedID != nil else { return }
        let wasStopped = stopped
        if wasStopped {
            stopped = false
            clearError()
        } else {
            guard snapshot.hasMore else { return }
        }
        snapshot.loading = true
        let token = publicationID
        enqueue { session in
            await session.command(wasStopped ? "resume" : "more", publication: token)
        }
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

    public func refresh() async throws {
        guard let id = allocatedID, !released, !invalidated else { throw FilmSearchError.closed }
        let token = publicationID, previous = observation
        observationID = UUID(); observation = nil
        previous?.cancel(); await previous?.value
        let value = try FilmSearchSnapshot(value: await transport.call("search.snapshot", fields(id)))
        guard !released, !invalidated, !stopped, publicationID == token else { throw CancellationError() }
        try Task.checkCancellation()
        try accept(value)
        clearError()
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

    /// Node owns `session.selections` and resolves late-TMDB-carrier folding for
    /// a live `selectionId`; do not reject a selection just because `snapshot.cards` folded it.
    func reference(for selectionID: String) -> RuntimeValue? {
        guard let id = allocatedID, !runtimeClosed, !released, !invalidated,
              snapshot.sessionID == id, !selectionID.isEmpty else { return nil }
        return .object(["sessionId": .string(id.uuidString.lowercased()), "selectionId": .string(selectionID)])
    }

    /// Establishes the terminal state before another queued operation can run.
    func release() -> Task<Void, Never> {
        if let releaseTask { return releaseTask }
        released = true; invalidated = true; stopped = true
        publicationID = UUID()
        runtimeAvailabilityTask?.cancel(); runtimeAvailabilityTask = nil
        admission?.cancel()
        observationID = UUID(); observation?.cancel()
        let accepted = work, observer = observation
        observation = nil
        let task = Task { [self] in
            await accepted?.value
            await observer?.value
            if let id = allocatedID {
                do { _ = try await transport.call("search.release", fields(id)) }
                catch {
                    self.error = Self.failure(error)
                    DebugRuntime.logError(.search, sub: "session", event: "release.error", error: error)
                }
            }
            work = nil
        }
        releaseTask = task
        snapshot.loading = false
        if [.idle, .loading].contains(snapshot.siteState) { snapshot.siteState = .cancelled }
        if [.idle, .loading].contains(snapshot.metadataState) { snapshot.metadataState = .cancelled }
        finishSettlement()
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
            DebugRuntime.log(.search, sub: "session", event: "open.ok", level: .debug, data: .object([
                "sessionId": .string(value.sessionID.uuidString.lowercased()),
                "sourceCount": .int(sources.count),
            ]))
            guard publicationID == token, !stopped, !released, !invalidated else { return }
            try accept(value)
            observe()
        } catch {
            admission = nil
            guard publicationID == token, !released, !invalidated else { return }
            snapshot.loading = false
            if !Self.cancelled(error) {
                self.error = Self.failure(error)
                DebugRuntime.logError(.search, sub: "session", event: "open.error", error: error)
                let disconnected = (await transport.borrowedProducer())?.isClosed == true
                guard publicationID == token, !released, !invalidated else { return }
                self.runtimeClosed = disconnected
            }
            finishSettlement()
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
            try accept(value, stopping: action == "stop"); clearError()
            if action == "resume" || action == "more" { observe() }
        } catch {
            guard publicationID == token, !released, !invalidated else { return }
            snapshot.loading = false
            if !Self.cancelled(error) {
                self.error = Self.failure(error)
                DebugRuntime.logError(.search, sub: "session", event: "command.error", error: error,
                                      data: .object(["action": .string(action)]))
                let disconnected = (await transport.borrowedProducer())?.isClosed == true
                guard publicationID == token, !released, !invalidated else { return }
                self.runtimeClosed = disconnected
            }
            finishSettlement()
        }
    }

    private func observe() {
        guard observation == nil, !stopped, !released, !invalidated, let id = allocatedID else { return }
        let token = UUID(); observationID = token
        let transport = transport
        observation = Task { [weak self] in
            var reconnectAttempt = 0
            while let self, self.observationID == token, !Task.isCancelled, !self.stopped, !self.released, !self.invalidated {
                let after = self.snapshot.sequence
                do {
                    try await transport.snapshots(sessionID: id, after: after) { [weak self] value in
                        try await self?.receive(value, token: token)
                    }
                    break
                } catch {
                    guard self.observationID == token, !Task.isCancelled, !self.released else { break }
                    if Self.cancelled(error) { break }
                    let disconnected = (await transport.borrowedProducer())?.isClosed == true
                    guard self.observationID == token, !Task.isCancelled, !self.released else { break }
                    if self.snapshot.sequence > after { reconnectAttempt = 0 }
                    if disconnected || self.stopped || self.invalidated || reconnectAttempt >= Self.maxObserveReconnectAttempts {
                        self.error = Self.failure(error)
                        self.snapshot.loading = false
                        self.runtimeClosed = disconnected
                        DebugRuntime.logError(.search, sub: "session", event: "observe.error", error: error,
                                              data: .object(["disconnected": .bool(disconnected), "attempts": .int(reconnectAttempt)]))
                        self.finishSettlement()
                        break
                    }
                    var caughtUp = false
                    while !caughtUp {
                        let delayMs: UInt64 = 500 << UInt64(reconnectAttempt)
                        reconnectAttempt += 1
                        DebugRuntime.log(.search, sub: "session", event: "observe.reconnect", level: .info, data: .object([
                            "attempt": .int(reconnectAttempt),
                            "delayMs": .int(Int(delayMs)),
                        ]))
                        do {
                            try await Task.sleep(nanoseconds: delayMs * 1_000_000)
                            guard self.observationID == token, !Task.isCancelled, !self.stopped, !self.released, !self.invalidated else { break }
                            let latest = try FilmSearchSnapshot(value: await transport.call("search.snapshot", self.fields(id)))
                            guard self.observationID == token, !Task.isCancelled, !self.stopped, !self.released, !self.invalidated else { break }
                            try self.accept(latest)
                            self.clearError()
                            caughtUp = true
                        } catch {
                            guard self.observationID == token, !Task.isCancelled, !self.released else { break }
                            if Self.cancelled(error) { break }
                            let reconnectDisconnected = (await transport.borrowedProducer())?.isClosed == true
                            guard self.observationID == token, !Task.isCancelled, !self.released else { break }
                            if reconnectDisconnected || self.stopped || self.invalidated || reconnectAttempt >= Self.maxObserveReconnectAttempts {
                                self.error = Self.failure(error)
                                self.snapshot.loading = false
                                self.runtimeClosed = reconnectDisconnected
                                DebugRuntime.logError(.search, sub: "session", event: "observe.reconnect.failed", error: error)
                                self.finishSettlement()
                                break
                            }
                        }
                    }
                    if !caughtUp { break }
                }
            }
            guard let self, self.observationID == token else { return }
            self.observation = nil
        }
    }

    private func receive(_ value: FilmSearchSnapshot, token: UUID) throws {
        guard observationID == token, !stopped, !released, !invalidated else { return }
        try accept(value)
        clearError()
    }

    private func accept(_ value: FilmSearchSnapshot, stopping: Bool = false) throws {
        guard value.sessionID == allocatedID, value.query.utf8.elementsEqual(snapshot.query.utf8) else {
            throw FilmSearchError.invalidResponse
        }
        if value.sessionID == snapshot.sessionID {
            guard value.sequence > snapshot.sequence else { return }
            guard value.revision >= snapshot.revision else { throw FilmSearchError.invalidResponse }
        }
        if stopping {
            // A late open may have allocated an identity without ever publishing.
            // Stop acknowledges only control state, never its newly settled data.
            var state = value.retainingDisplay(snapshot)
            state.sequence = value.sequence; state.revision = value.revision
            state.siteState = value.siteState; state.metadataState = value.metadataState
            state.loading = value.loading; state.hasMore = value.hasMore; state.noSources = value.noSources
            snapshot = state
        } else {
            snapshot = value.retainingLoadingDisplay(snapshot)
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
    private func clearError() {
        if error != nil { error = nil }
    }

    private func finishSettlement() {
        let pending = Array(settleWaiters.values); settleWaiters.removeAll()
        for waiter in pending { waiter.resume() }
    }
    private static func cancelled(_ error: Error) -> Bool {
        error is CancellationError ||
            (error as? FilmPlaybackError)?.code == "CANCELLED" ||
            (error as? RuntimeHTTPError) == .cancelled ||
            (error as? RuntimeHTTPError) == .remote("CANCELLED")
    }
    private static func failure(_ error: Error) -> FilmPlaybackError {
        if let error = error as? FilmPlaybackError { return error }
        if let error = error as? RuntimeHTTPError {
            switch error {
            case .closed: return .closed
            case .cancelled: return .init(code: "CANCELLED")
            case .timeout: return .init(code: "TIMEOUT")
            case .invalidRequest: return .init(code: "INVALID_REQUEST")
            case .invalidResponse: return .invalidResponse
            case .remote(let code): return .init(code: code)
            }
        }
        if let error = error as? FilmSearchError {
            switch error {
            case .invalidResponse: return .init(code: "INVALID_RESPONSE")
            case .closed: return .init(code: "CLOSED")
            case .unavailable: return .init(code: "HOST_UNAVAILABLE")
            }
        }
        if error is CancellationError { return .init(code: "CANCELLED") }
        return .init(code: "HOST_UNAVAILABLE")
    }

    isolated deinit {
        runtimeAvailabilityTask?.cancel()
        admission?.cancel(); observation?.cancel()
        if !released, let id = allocatedID {
            let transport = transport
            Task {
                do { _ = try await transport.call("search.release", ["sessionId": .string(id.uuidString.lowercased())]) }
                catch { DebugRuntime.logError(.search, sub: "session", event: "deinit.release.error", error: error) }
            }
        }
    }
}
#endif
