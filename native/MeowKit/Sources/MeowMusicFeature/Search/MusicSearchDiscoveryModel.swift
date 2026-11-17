#if os(iOS) || os(macOS)
import Foundation
import Combine

/// Page-owned projections and admission. Native caches, credentials and queue
/// transactions remain with their existing owners.
@MainActor
final class MusicSearchDiscoveryModel: ObservableObject {
    enum ChartContent {
        case idle, loading, loaded(MusicPublicBrowseDetail), failed(MusicServiceError)
    }
    enum Target: Hashable {
        case hot, charts, members(String)
    }
    struct Failure {
        let target: Target
        let error: MusicServiceError
    }
    struct Playback {
        let tracks: [MusicRemoteTrack]
        let index: Int
    }

    @Published private(set) var hot: [MusicSearchHotKeyword] = []
    @Published private(set) var charts: [MusicSearchDiscoveryChart] = []
    @Published private(set) var chartContent: [String: ChartContent] = [:]
    @Published private(set) var loadedComponents: Set<MusicSearchDiscoveryFailure.Component> = []
    @Published private(set) var componentFailures: [MusicSearchDiscoveryFailure.Component: MusicServiceError] = [:]
    @Published private(set) var loadingComponents: Set<MusicSearchDiscoveryFailure.Component> = []
    @Published private(set) var suggestionOffset = 0
    @Published private(set) var hotCanPlay = false
    @Published private(set) var isPreparingPlayback = false
    private let provider: any MusicSearchDiscoveryProviding
    private let browser: any MusicPublicBrowsing
    private let hotPlayback: (any MusicSearchHotPlaybackProviding)?
    private var available = false, visible = false, closed = false
    private var generation = UUID(), request = UUID(), action = UUID()
    private var visibleCharts: Set<String> = []
    private var discoveryTask: Task<Void, Never>?
    private var memberTask: Task<Void, Never>?
    private var hotPlaybackTask: Task<[MusicRemoteTrack], Error>?
    private var songObservation: Task<Void, Never>?
    private var songProjectionID = UUID()

    init(provider: any MusicSearchDiscoveryProviding, browser: any MusicPublicBrowsing,
         hotPlayback: (any MusicSearchHotPlaybackProviding)? = nil) {
        self.provider = provider; self.browser = browser; self.hotPlayback = hotPlayback
    }

    var suggestions: [String] {
        guard !hot.isEmpty else { return [] }
        return (0..<min(6, hot.count)).map { hot[(suggestionOffset + $0) % hot.count].keyword }
    }

    var failures: [Failure] {
        let primary = MusicSearchDiscoveryFailure.Component.allCases.compactMap { component -> Failure? in
            guard let error = componentFailures[component] else { return nil }
            return .init(target: component == .hot ? .hot : .charts, error: error)
        }
        return primary + charts.compactMap { chart in
            guard case .failed(let error) = chartContent[chart.id] else { return nil }
            return .init(target: .members(chart.id), error: error)
        }
    }

    func rotateSuggestions() {
        guard hot.count > 6 else { return }
        suggestionOffset = (suggestionOffset + 6) % hot.count
    }

    func setAccess(available: Bool, generation: UUID) {
        guard !closed, self.available != available || self.generation != generation else { return }
        self.available = available; self.generation = generation
        invalidate()
        observeSongs()
        if visible { loadMissingDiscovery() }
    }

    func appear() {
        guard !closed else { return }
        visible = true
        observeSongs()
        loadMissingDiscovery()
        loadVisibleMembers()
    }

    func disappear() {
        visible = false; visibleCharts = []
        songObservation?.cancel(); songObservation = nil
        cancelPlaybackAction()
        isPreparingPlayback = false
        // Already admitted display reads remain valid while this retained page
        // is covered; visibility only stops new member requests.
    }

    func close() {
        guard !closed else { return }
        closed = true; available = false; visible = false
        invalidate()
    }

    func setVisibleCharts(_ ids: Set<String>) {
        guard visible, available, !closed else { return }
        let current = ids.intersection(Set(charts.map(\.id)))
        guard current != visibleCharts else { return }
        visibleCharts = current
        loadVisibleMembers()
    }

    func members(of chartID: String) -> [MusicPublicBrowseMember] {
        guard case .loaded(let detail) = chartContent[chartID] else { return [] }
        return Array(detail.members.prefix(20))
    }
    private func observeSongs() {
        songObservation?.cancel(); songObservation = nil
        guard available, visible, !closed, let repository = browser as? any MusicSongRepositoryReading else { return }
        let access = generation
        songObservation = Task { [weak self] in
            let changes = await repository.songChanges()
            await self?.refreshSongProjections(repository, access: access)
            for await changed in changes {
                guard !Task.isCancelled else { return }
                await self?.refreshSongProjections(repository, access: access, changed: changed)
            }
        }
    }
    private func refreshSongProjections(_ repository: any MusicSongRepositoryReading, access: UUID, changed: Set<String>? = nil) async {
        guard !Task.isCancelled, available, visible, !closed, generation == access else { return }
        let own = request, projection = UUID()
        let referenced = Set(chartContent.values.flatMap { value -> [String] in
            guard case .loaded(let detail) = value else { return [] }
            return detail.members.map { $0.track.songId }
        })
        let ids = changed.map { referenced.intersection($0) } ?? referenced
        guard !ids.isEmpty else { return }
        songProjectionID = projection
        do {
            let songs = try await repository.repositoryTracks(songIDs: ids)
            guard admits(own, access: access), visible, songProjectionID == projection else { return }
            for key in Array(chartContent.keys) {
                guard case .loaded(let detail) = chartContent[key],
                      detail.members.contains(where: { songs[$0.track.songId] != nil }) else { continue }
                chartContent[key] = .loaded(.init(collection: detail.collection, members: detail.members.map {
                    guard songs[$0.track.songId] != nil else { return $0 }
                    return .init(nativeID: $0.nativeID, sourcePosition: $0.sourcePosition, track: $0.track.projected(songs))
                }, total: detail.total, cursor: detail.cursor, notice: detail.notice, consumed: detail.consumed))
            }
        } catch {}
    }

    func retry(_ targets: Set<Target>) {
        guard available, visible, !closed else { return }
        var components: Set<MusicSearchDiscoveryFailure.Component> = []
        if targets.contains(.hot), componentFailures[.hot] != nil { components.insert(.hot) }
        if targets.contains(.charts), componentFailures[.charts] != nil { components.insert(.charts) }
        loadDiscovery(components)
        for chart in charts where targets.contains(.members(chart.id)) {
            if case .failed = chartContent[chart.id] { chartContent[chart.id] = .idle }
        }
        loadVisibleMembers()
    }

    /// Existing chart members already reference the permanent repository.
    func preparePlayback(chartID: String, nativeID: String?) async throws -> Playback {
        try Task.checkCancellation()
        guard available, visible, !closed else { throw MusicServiceError.staleAccess }
        let rows = members(of: chartID)
        guard !rows.isEmpty else { throw MusicServiceError.contentUnavailable }
        let index: Int
        if let nativeID {
            guard let selected = rows.firstIndex(where: { $0.nativeID == nativeID }) else { throw MusicServiceError.notFound }
            index = selected
        } else { index = 0 }
        return .init(tracks: rows.map(\.track), index: index)
    }

    /// Resolve the currently displayed ordered keywords with the provider's
    /// dedicated one-shot batch endpoint. Consume the provider's actual ordered
    /// result without comparing its count with the requested keywords.
    func prepareHotPlayback() async throws -> Playback {
        try Task.checkCancellation()
        guard available, visible, !closed, hotCanPlay, let hotPlayback else {
            throw MusicServiceError.unsupported
        }
        guard !isPreparingPlayback else { throw MusicServiceError.busy }
        let keywords = hot.map(\.keyword)
        guard !keywords.isEmpty else {
            throw MusicServiceError.contentUnavailable
        }
        let own = UUID(), page = request, access = generation
        action = own; isPreparingPlayback = true
        let trace = MusicRequestTrace(.playback, platform: .netease)
        let task = Task {
            try await MusicRequestTrace.$current.withValue(trace) {
                try await hotPlayback.searchHotPlayback(keywords: keywords)
            }
        }
        hotPlaybackTask = task
        defer {
            if action == own { hotPlaybackTask = nil; isPreparingPlayback = false }
        }
        let tracks = try await withTaskCancellationHandler { try await task.value } onCancel: { task.cancel() }
        try Task.checkCancellation()
        guard available, visible, !closed, request == page, generation == access, action == own else {
            throw MusicServiceError.cancelled
        }
        return .init(tracks: tracks, index: 0)
    }

    private func loadMissingDiscovery() {
        let missing = Set(MusicSearchDiscoveryFailure.Component.allCases).subtracting(loadedComponents)
            .subtracting(Set(componentFailures.keys))
        loadDiscovery(missing)
    }

    private func loadDiscovery(_ components: Set<MusicSearchDiscoveryFailure.Component>) {
        guard available, visible, !closed, discoveryTask == nil, !components.isEmpty else { return }
        let own = request, access = generation, provider = provider
        loadingComponents = components
        for component in components { componentFailures.removeValue(forKey: component) }
        discoveryTask = Task { [weak self] in
            defer {
                if let self, self.request == own {
                    self.discoveryTask = nil; self.loadingComponents = []
                }
            }
            do {
                let result = try await provider.searchDiscovery(components: components)
                guard let self, self.admits(own, access: access) else { return }
                let failed = Set(result.failures.map(\.component))
                for failure in result.failures { self.componentFailures[failure.component] = failure.error }
                if components.contains(.hot), !failed.contains(.hot) {
                    self.hot = result.hot
                    self.hotCanPlay = result.hotCanPlay && self.hotPlayback != nil && !result.hot.isEmpty
                    self.suggestionOffset = 0; self.loadedComponents.insert(.hot)
                }
                if components.contains(.charts), !failed.contains(.charts) {
                    self.charts = result.charts; self.loadedComponents.insert(.charts)
                    let retained = Set(result.charts.map(\.id))
                    self.chartContent = self.chartContent.filter { retained.contains($0.key) }
                    self.visibleCharts.formIntersection(retained)
                }
                self.loadVisibleMembers()
            } catch {
                guard let self, self.admits(own, access: access) else { return }
                let failure = MusicServiceError.sanitized(error)
                for component in components { self.componentFailures[component] = failure }
            }
        }
    }

    private func loadVisibleMembers() {
        guard available, visible, !closed, memberTask == nil,
              let chart = charts.first(where: {
                  guard visibleCharts.contains($0.id) else { return false }
                  if case .idle = chartContent[$0.id] ?? .idle { return true }
                  return false
              }) else { return }
        let own = request, access = generation, browser = browser
        chartContent[chart.id] = .loading
        // At most one page-owned member read; other visible demand is admitted
        // in catalogue order after the current execution actually finishes.
        memberTask = Task { [weak self] in
            defer {
                if let self, self.request == own {
                    self.memberTask = nil
                    self.loadVisibleMembers()
                }
            }
            do {
                let trace = MusicRequestTrace(.playlist, platform: .netease)
                let result = try await MusicRequestTrace.$current.withValue(trace) {
                    try await browser.publicDetail(chart.collection, cursor: nil)
                }
                guard let self, self.admits(own, access: access) else { return }
                self.chartContent[chart.id] = .loaded(result)
                if let repository = browser as? any MusicSongRepositoryReading {
                    await self.refreshSongProjections(repository, access: access)
                }
            } catch {
                guard let self, self.admits(own, access: access) else { return }
                self.chartContent[chart.id] = .failed(MusicServiceError.sanitized(error))
            }
        }
    }

    private func admits(_ own: UUID, access: UUID) -> Bool {
        !Task.isCancelled && !closed && available && request == own && generation == access
    }

    private func invalidate() {
        songObservation?.cancel(); songObservation = nil
        request = UUID(); action = UUID()
        discoveryTask?.cancel(); discoveryTask = nil
        memberTask?.cancel(); memberTask = nil
        hotPlaybackTask?.cancel(); hotPlaybackTask = nil
        hot = []; charts = []; chartContent = [:]; visibleCharts = []
        loadedComponents = []; componentFailures = [:]; loadingComponents = []
        suggestionOffset = 0; hotCanPlay = false; isPreparingPlayback = false
    }

    private func cancelPlaybackAction() {
        action = UUID()
        hotPlaybackTask?.cancel(); hotPlaybackTask = nil
    }

    deinit {
        discoveryTask?.cancel(); memberTask?.cancel()
        hotPlaybackTask?.cancel()
        songObservation?.cancel()
    }
}
#endif
