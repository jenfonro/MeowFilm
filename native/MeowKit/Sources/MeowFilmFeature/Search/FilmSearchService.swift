#if os(iOS) || os(macOS)
import Foundation
import MeowRuntime

/// Owns consumer admission and display lifetimes; the borrowed Node runtime owns
/// search sessions, rules and all content/cache state.
@MainActor
public final class FilmSearchService {
    public typealias Sites = @Sendable () async throws -> [FilmSearchSite]
    let transport: FilmSearchTransport
    let sites: Sites
    private(set) var metadataRevision = UUID()
    private var current: FilmSearchSession?
    private final class WeakSession {
        weak var value: FilmSearchSession?
        init(_ value: FilmSearchSession) { self.value = value }
    }
    private var sessions: [WeakSession] = []
    private var retirements: [UUID: Task<Void, Never>] = [:]
    private var sourceScope: [Data: RuntimeValue]?
    private var closed = false
    private var closing: Task<Void, Never>?
    private var sourceObservation: Task<Void, Never>?

    public init(transport: FilmSearchTransport, sites: @escaping Sites) {
        self.transport = transport; self.sites = sites
    }

    public convenience init(sources: FilmSourceService,
                            connection: @escaping @Sendable () async throws -> RuntimeHTTPClient) {
        self.init(sources: sources, transport: FilmSearchTransport(connection: connection))
    }

    convenience init(sources: FilmSourceService, transport: FilmSearchTransport) {
        self.init(transport: transport, sites: {
            try await sources.refresh()
            return Self.searchableSites(await sources.snapshot)
        })
        sourceObservation = Task { [weak self] in
            for await _ in await sources.snapshots() {
                guard !Task.isCancelled else { return }
                let snapshot = await sources.snapshot
                guard !Task.isCancelled else { return }
                self?.sourcesChanged(snapshot)
            }
        }
    }

    nonisolated static func searchableSites(_ snapshot: FilmSourceSnapshot) -> [FilmSearchSite] {
        snapshot.videos.compactMap { source in
            guard source.options.enabled, source.options.searchEnabled, source.actions.contains("search"),
                  let cat = snapshot.cats.first(where: { $0.installationId.utf8.elementsEqual(source.key.installationId.utf8) }),
                  cat.enabled else { return nil }
            return FilmSearchSite(cat: cat, source: source)
        }.sorted { $0.source.order < $1.source.order }
    }

    nonisolated static func authorizationScope(_ sources: [FilmPlaybackSource]) -> [Data: RuntimeValue] {
        Dictionary(uniqueKeysWithValues: sources.map { (Data($0.key!.utf8), .object($0.value)) })
    }

    func accepts(_ sources: [FilmPlaybackSource]) -> Bool {
        sourceScope.map { Self.authorizationScope(sources) == $0 } ?? true
    }

    private func sourcesChanged(_ snapshot: FilmSourceSnapshot) {
        let scope = Self.authorizationScope(Self.searchableSites(snapshot).map(\.playbackSource))
        sourceScope = scope
        for session in sessions.compactMap(\.value) where session.hasDifferentSources(scope) {
            session.invalidateSources()
            if current === session { current = nil }
        }
    }

    public func session(for keyword: String, reusing retained: FilmSearchSession? = nil) throws -> FilmSearchSession {
        guard !closed else { throw FilmSearchError.closed }
        let query = keyword.trimmingCharacters(in: .whitespacesAndNewlines)
        let reusable = [retained, current].compactMap { $0 }.first { candidate in
            candidate.belongs(to: self) && !candidate.invalidated &&
            candidate.snapshot.query.utf8.elementsEqual(query.utf8) &&
            (sourceScope.map { !candidate.hasDifferentSources($0) } ?? true)
        }
        let session = reusable ?? FilmSearchSession(query: query, service: self)
        sessions.removeAll { $0.value == nil }
        if reusable == nil { sessions.append(WeakSession(session)) }
        if current !== session {
            current?.cancel()
            current = session
        }
        session.startIfNeeded()
        return session
    }

    func ownsWork(for session: FilmSearchSession) -> Bool { !closed && current === session }

    /// Accepted settings changes revoke old queries without starting a new one.
    public func metadataSettingsChanged() {
        guard !closed else { return }
        metadataRevision = UUID()
        for session in sessions.compactMap(\.value) { session.invalidateSources() }
        current = nil
    }

    func release(_ session: FilmSearchSession) {
        if current === session { current = nil }
        let release = session.release()
        let id = UUID()
        retirements[id] = Task { [weak self] in
            await release.value
            self?.retirements[id] = nil
        }
    }

    public func close() async {
        if let closing { await closing.value; return }
        closed = true
        sourceObservation?.cancel()
        let sourceObservation = sourceObservation
        let accepted = sessions.compactMap(\.value).map { $0.release() }
        let retired = Array(retirements.values)
        let task = Task {
            await sourceObservation?.value
            for task in accepted { await task.value }
            for task in retired { await task.value }
        }
        closing = task
        await task.value
        current = nil; sessions.removeAll(); retirements.removeAll()
    }

    isolated deinit {
        sourceObservation?.cancel()
        for session in sessions.compactMap(\.value) { _ = session.release() }
    }
}
#endif
