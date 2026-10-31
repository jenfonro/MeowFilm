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
    private var chartPlaybackTask: Task<MusicPublicBrowseDetail, Error>?
    private var hotPlaybackTask: Task<[MusicRemoteTrack], Error>?

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
        if visible { loadMissingDiscovery() }
    }

    func appear() {
        guard !closed else { return }
        visible = true
        loadMissingDiscovery()
        loadVisibleMembers()
    }

    func disappear() {
        visible = false; visibleCharts = []
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
        return detail.members.filter { $0.sourcePosition < 20 }
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

    /// Reconsume only the bounded member page on an explicit action. This
    /// reissues local handles from valid native data, never trusts an old DTO
    /// as a song, and never opens the complete collection queue.
    func preparePlayback(chartID: String, nativeID: String?) async throws -> Playback {
        try Task.checkCancellation()
        guard available, visible, !closed else { throw MusicServiceError.staleAccess }
        guard !isPreparingPlayback, case .loaded(let displayed) = chartContent[chartID] else {
            throw MusicServiceError.busy
        }
        let expected = members(of: chartID)
        guard !expected.isEmpty else { throw MusicServiceError.contentUnavailable }
        let index: Int
        if let nativeID {
            guard let selected = expected.firstIndex(where: { $0.nativeID == nativeID }) else {
                throw MusicServiceError.targetChanged
            }
            index = selected
        } else { index = 0 }
        let own = UUID(), page = request, access = generation, browser = browser
        action = own; isPreparingPlayback = true
        let trace = MusicRequestTrace(.playback, platform: .netease)
        let task = Task {
            try await MusicRequestTrace.$current.withValue(trace) {
                try await browser.publicDetail(displayed.collection, cursor: nil)
            }
        }
        chartPlaybackTask = task
        defer {
            if action == own { chartPlaybackTask = nil; isPreparingPlayback = false }
        }
        let result = try await withTaskCancellationHandler { try await task.value } onCancel: { task.cancel() }
        try Task.checkCancellation()
        guard available, visible, !closed, request == page, generation == access, action == own else {
            throw MusicServiceError.cancelled
        }
        guard result.collection.id == chartID, result.snapshot == displayed.snapshot else {
            throw MusicServiceError.staleResult
        }
        try validate(result, collection: displayed.collection)
        let current = result.members.filter { $0.sourcePosition < 20 }
        guard current.count == expected.count else { throw MusicServiceError.targetChanged }
        for (old, fresh) in zip(expected, current) {
            guard old.sourcePosition == fresh.sourcePosition, old.nativeID == fresh.nativeID,
                  MusicSearchFusion.sameSearchTarget(old.track, fresh.track, platform: .netease) else {
                throw MusicServiceError.targetChanged
            }
        }
        return .init(tracks: current.map(\.track), index: index)
    }

    /// Resolve the currently displayed ordered keywords with the provider's
    /// dedicated one-shot batch endpoint. The result must stay positionally
    /// aligned; a short or reordered-looking response is never compacted.
    func prepareHotPlayback() async throws -> Playback {
        try Task.checkCancellation()
        guard available, visible, !closed, hotCanPlay, let hotPlayback else {
            throw MusicServiceError.unsupported
        }
        guard !isPreparingPlayback else { throw MusicServiceError.busy }
        let keywords = hot.map(\.keyword)
        guard !keywords.isEmpty, keywords.count <= 20 else {
            throw MusicServiceError.contentUnavailable
        }
        let signature = keywords.map { Data($0.utf8) }
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
        guard hotCanPlay, hot.map({ Data($0.keyword.utf8) }) == signature,
              tracks.count == keywords.count,
              tracks.allSatisfy({ track in
                  track.ref.kind == .handle && !track.songId.isEmpty && !track.title.isEmpty &&
                      track.sources.contains {
                          $0.platform == MusicPublicBrowsePlatform.netease.rawValue &&
                              !$0.nativeId.isEmpty && !$0.sourceKey.isEmpty
                      }
              }) else { throw MusicServiceError.upstreamInvalid }
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
                guard result.hot.count <= 20, result.charts.count <= 3,
                      (!result.hotCanPlay || !result.hot.isEmpty),
                      Set(result.charts.map(\.id)).count == result.charts.count,
                      result.hot.allSatisfy({ (1...20).contains($0.rank) && !$0.keyword.isEmpty }),
                      Set(result.hot.map { Data($0.keyword.utf8) }).count == result.hot.count,
                      Set(result.hot.map(\.rank)).count == result.hot.count,
                      result.hot.map(\.rank) == result.hot.map(\.rank).sorted(),
                      result.charts.allSatisfy({ $0.collection.platform == .netease && $0.collection.kind == .playlist }),
                      result.failures.allSatisfy({ components.contains($0.component) }),
                      Set(result.failures.map(\.component)).count == result.failures.count else {
                    throw MusicServiceError.upstreamInvalid
                }
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
                try self.validate(result, collection: chart.collection)
                self.chartContent[chart.id] = .loaded(result)
            } catch {
                guard let self, self.admits(own, access: access) else { return }
                self.chartContent[chart.id] = .failed(MusicServiceError.sanitized(error))
            }
        }
    }

    private func validate(_ detail: MusicPublicBrowseDetail, collection: MusicPublicBrowseCollection) throws {
        let members = detail.members
        guard detail.collection.id == collection.id, members.count <= 30,
              (members.count...30).contains(detail.consumed), detail.total >= detail.consumed,
              detail.snapshot.range(of: "^[a-f0-9]{64}$", options: .regularExpression) != nil,
              Set(members.map(\.sourcePosition)).count == members.count,
              members.map(\.sourcePosition) == members.map(\.sourcePosition).sorted(),
              Set(members.map(\.nativeID)).count == members.count,
              members.allSatisfy({ member in
                  (0..<detail.consumed).contains(member.sourcePosition) && !member.nativeID.isEmpty &&
                      member.track.ref.kind == .handle &&
                      member.track.sources.contains {
                          $0.platform == collection.platform.rawValue && $0.nativeId == member.nativeID
                      }
              }) else { throw MusicServiceError.upstreamInvalid }
    }

    private func admits(_ own: UUID, access: UUID) -> Bool {
        !Task.isCancelled && !closed && available && request == own && generation == access
    }

    private func invalidate() {
        request = UUID(); action = UUID()
        discoveryTask?.cancel(); discoveryTask = nil
        memberTask?.cancel(); memberTask = nil
        chartPlaybackTask?.cancel(); chartPlaybackTask = nil
        hotPlaybackTask?.cancel(); hotPlaybackTask = nil
        hot = []; charts = []; chartContent = [:]; visibleCharts = []
        loadedComponents = []; componentFailures = [:]; loadingComponents = []
        suggestionOffset = 0; hotCanPlay = false; isPreparingPlayback = false
    }

    private func cancelPlaybackAction() {
        action = UUID()
        chartPlaybackTask?.cancel(); chartPlaybackTask = nil
        hotPlaybackTask?.cancel(); hotPlaybackTask = nil
    }

    deinit {
        discoveryTask?.cancel(); memberTask?.cancel()
        chartPlaybackTask?.cancel(); hotPlaybackTask?.cancel()
    }
}
#endif
