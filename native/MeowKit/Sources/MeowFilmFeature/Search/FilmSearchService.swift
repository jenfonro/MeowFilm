#if os(iOS) || os(macOS)
import Foundation
import MeowRuntime

/// App owner for query snapshots and the native projection engine.
/// Cat installations and saved rules remain owned by the existing services.
@MainActor
public final class FilmSearchService {
    public typealias Sites = @Sendable () async throws -> [FilmSearchSite]
    public typealias Rules = @Sendable () async throws -> [String]
    let processor: FilmSearchProcessor
    let data: FilmSearchData
    let sites: Sites
    let rules: Rules
    let metadata: (any FilmSearchMetadataReading)?
    private(set) var metadataRevision = UUID()
    private(set) var sourceOwner: FilmSourceService?
    private var current: FilmSearchSession?
    // Only physically pending retirements are owned here. Settled historical
    // snapshots live solely in the actual search/detail navigation consumers.
    private var retirements: [UUID: (FilmSearchSession, Task<Void, Never>)] = [:]
    private var sourceScope: [String: Int64]?
    private var closed = false
    private var closing: Task<Void, Never>?
    private var sourceObservation: Task<Void, Never>?

    public init(processor: FilmSearchProcessor, data: FilmSearchData,
                sites: @escaping Sites, rules: @escaping Rules,
                metadata: (any FilmSearchMetadataReading)? = nil) {
        self.processor = processor; self.data = data
        self.sites = sites; self.rules = rules; self.metadata = metadata
    }

    public convenience init(sources: FilmSourceService, rules: FilmRuleService,
                            data: FilmSearchData,
                            metadata: (any FilmSearchMetadataReading)? = nil) throws {
        self.init(processor: try FilmSearchProcessor(), data: data, sites: {
            try await sources.refresh()
            let snapshot = await sources.snapshot
            return Self.searchableSites(snapshot)
        }, rules: {
            try await rules.initialize()
            return try await rules.savedSnapshot().aggregateRegexRules
        }, metadata: metadata)
        sourceOwner = sources
        sourceObservation = Task { [weak self] in
            let updates = await sources.snapshots()
            for await _ in updates {
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

    nonisolated static func authorizationScope(_ sites: [FilmSearchSite]) -> [String: Int64] {
        Dictionary(uniqueKeysWithValues: sites.map { ($0.key, $0.cat.generation) })
    }

    private func sourcesChanged(_ snapshot: FilmSourceSnapshot) {
        let scope = Self.authorizationScope(Self.searchableSites(snapshot))
        sourceScope = scope
        if let session = current, session.hasDifferentSources(scope) {
            session.invalidateSources()
            retire(session)
            current = nil
        }
    }

    public func session(for keyword: String, reusing retained: FilmSearchSession? = nil) throws -> FilmSearchSession {
        guard !closed else { throw FilmSearchError.closed }
        let query = keyword.trimmingCharacters(in: .whitespacesAndNewlines)
        let session = [retained, current].compactMap { $0 }.first { candidate in
            candidate.belongs(to: self) && !candidate.invalidated &&
            candidate.snapshot.query.utf8.elementsEqual(query.utf8) &&
            (sourceScope.map { !candidate.hasDifferentSources($0) } ?? true)
        } ?? FilmSearchSession(query: query, service: self)
        if current !== session {
            if let previous = current { previous.cancel(); retire(previous) }
            current = session
        }
        session.startIfNeeded()
        return session
    }

    func ownsWork(for session: FilmSearchSession) -> Bool { !closed && current === session }

    /// Called only after the runtime accepted a settings write. Old navigation
    /// keeps its display data but cannot regain publication ownership.
    public func metadataSettingsChanged() {
        guard !closed else { return }
        metadataRevision = UUID()
        let accepted = (current.map { [$0] } ?? []) + retirements.values.map(\.0)
        for session in accepted { session.invalidateSources() }
        if let current { retire(current) }
        current = nil
    }

    func release(_ session: FilmSearchSession) {
        guard current === session else { return }
        session.cancel()
        current = nil
        retire(session)
    }

    private func retire(_ session: FilmSearchSession) {
        let id = UUID()
        let drain = Task { [weak self] in
            await session.drain()
            self?.retirements[id] = nil
        }
        retirements[id] = (session, drain)
    }

    public func close() async {
        if let closing { await closing.value; return }
        closed = true
        sourceObservation?.cancel()
        let sourceObservation = sourceObservation
        let retired = Array(retirements.values)
        let accepted = (current.map { [$0] } ?? []) + retired.map(\.0)
        for session in accepted { session.invalidateSources() }
        let processor = processor
        let task = Task {
            await sourceObservation?.value
            for session in accepted { await session.drain() }
            for (_, drain) in retired { await drain.value }
            await processor.close()
        }
        closing = task
        await task.value
        current = nil
        retirements.removeAll()
    }

    isolated deinit {
        sourceObservation?.cancel()
        let accepted = (current.map { [$0] } ?? []) + retirements.values.map(\.0)
        for session in accepted { session.invalidateSources() }
        let processor = processor
        Task {
            for session in accepted { await session.drain() }
            await processor.close()
        }
    }
}
#endif
