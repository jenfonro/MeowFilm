#if os(iOS) || os(macOS)
import Foundation
import MeowDebug
import MeowRuntime

/// Owns consumer admission and display lifetimes; the borrowed Node runtime owns
/// live video sources, search sessions, rules and all content/cache state.
@MainActor
public final class FilmSearchService {
    let transport: FilmSearchTransport
    private(set) var metadataRevision = UUID()
    private(set) var currentSourceSignature: String?
    private var current: FilmSearchSession?
    private final class WeakSession {
        weak var value: FilmSearchSession?
        init(_ value: FilmSearchSession) { self.value = value }
    }
    private nonisolated(unsafe) var sessions: [WeakSession] = []
    private var retirements: [UUID: Task<Void, Never>] = [:]
    private var closed = false
    private var closing: Task<Void, Never>?
    private nonisolated(unsafe) var sourceObservation: Task<Void, Never>?

    public init(transport: FilmSearchTransport) {
        self.transport = transport
    }

    public convenience init(sources: FilmSourceService,
                            connection: @escaping @Sendable () async throws -> RuntimeHTTPClient) {
        self.init(sources: sources, transport: FilmSearchTransport(connection: connection))
    }

    convenience init(sources: FilmSourceService, transport: FilmSearchTransport) {
        self.init(transport: transport)
        sourceObservation = Task { [weak self] in
            for await _ in await sources.snapshots() {
                guard !Task.isCancelled else { return }
                let snapshot = await sources.snapshot
                guard !Task.isCancelled else { return }
                self?.sourcesChanged(snapshot)
            }
        }
    }

    nonisolated static func signature(for snapshot: FilmSourceSnapshot) -> String {
        snapshot.videos
            .sorted { $0.order < $1.order }
            .compactMap { source -> String? in
                guard source.options.enabled, source.options.searchEnabled, source.actions.contains("search"),
                      let cat = snapshot.cats.first(where: { $0.installationId == source.key.installationId }),
                      cat.enabled else { return nil }
                return "\(cat.installationId):\(cat.generation):\(source.key.siteKey):\(source.order):\(source.options.aggregateImages)"
            }
            .joined(separator: "|")
    }

    private func sourcesChanged(_ snapshot: FilmSourceSnapshot) {
        let sig = Self.signature(for: snapshot)
        currentSourceSignature = sig
        let stale = sessions.compactMap(\.value).filter { $0.hasDifferentSources(sig) }
        if !stale.isEmpty {
            DebugRuntime.log(.search, sub: "service", event: "sources.changed", level: .debug,
                             data: .object(["releasedCount": .int(stale.count)]))
        }
        for session in stale { release(session) }
    }

    public func session(for keyword: String, reusing retained: FilmSearchSession? = nil) throws -> FilmSearchSession {
        guard !closed else { throw FilmSearchError.closed }
        let query = keyword.trimmingCharacters(in: .whitespacesAndNewlines)
        let previous = [retained, current].compactMap { $0 }.first { candidate in
            candidate.belongs(to: self) && candidate.snapshot.query.utf8.elementsEqual(query.utf8)
        }
        let reusable = previous.flatMap { candidate in
            !candidate.invalidated && (currentSourceSignature.map { !candidate.hasDifferentSources($0) } ?? true) ? candidate : nil
        }
        let session = reusable ?? FilmSearchSession(query: query, service: self, presentation: previous?.snapshot)
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

    public func runtimeDidBecomeAvailable() {
        guard !closed else { return }
        current?.runtimeDidBecomeAvailable()
    }

    /// Accepted settings changes revoke old queries without starting a new one.
    public func metadataSettingsChanged() {
        guard !closed else { return }
        metadataRevision = UUID()
        let active = sessions.compactMap(\.value)
        if !active.isEmpty {
            DebugRuntime.log(.search, sub: "service", event: "metadata.changed", level: .debug,
                             data: .object(["releasedCount": .int(active.count)]))
        }
        for session in active { release(session) }
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

    nonisolated deinit {
        sourceObservation?.cancel()
        let live = sessions.compactMap(\.value)
        guard !live.isEmpty else { return }
        if Thread.isMainThread {
            MainActor.assumeIsolated {
                for session in live { _ = session.release() }
            }
        } else {
            DispatchQueue.main.async {
                MainActor.assumeIsolated {
                    for session in live { _ = session.release() }
                }
            }
        }
    }
}
#endif
