// SPDX-License-Identifier: GPL-3.0-only
// SearchSession semantics (b900c259), using native request closures, not Workers.
#if os(iOS) || os(macOS)
import Foundation
import Combine

public enum MusicSearchSection: Equatable, Sendable { case songs, playlists }
public struct MusicSearchPlatformFailure: Sendable, Identifiable {
    public let platform: MusicPublicBrowsePlatform
    public let error: MusicServiceError
    public let diagnosticID: String?
    public var id: String { platform.rawValue }
}

/// A page-owned search session. No queue, account, library writer or media
/// engine. Both sections survive tab switches and temporary navigation cover.
@MainActor public final class MusicSearchResultsModel: ObservableObject {
    public typealias SongRead = @MainActor @Sendable (MusicPublicBrowsePlatform, String, Int) async throws -> MusicSearchPage
    public typealias PlaylistRead = @MainActor @Sendable (MusicPublicBrowsePlatform, String, String?) async throws -> MusicPublicBrowseCollectionPage
    @Published public private(set) var query = ""
    @Published public private(set) var requestID = UUID()
    @Published public private(set) var preferences = MusicSearchPreferences()
    @Published public private(set) var songs: [MusicSearchSong] = []
    @Published public private(set) var playlists: [MusicPublicBrowseCollection] = []
    @Published public private(set) var songsLoading = false
    @Published public private(set) var playlistsLoading = false
    @Published public private(set) var songsCancelled = false
    @Published public private(set) var playlistsCancelled = false
    @Published public private(set) var songFailures: [MusicSearchPlatformFailure] = []
    @Published public private(set) var playlistFailures: [MusicSearchPlatformFailure] = []
    @Published public private(set) var songsLoaded = false
    @Published public private(set) var playlistsLoaded = false
    /// Detail-return restoration cannot itself cause another page fetch.
    @Published public private(set) var automaticPaginationHeld = false

    private struct Songs {
        var items: [MusicRemoteTrack] = []
        var ranks: [String: Int] = [:]
        var originPages: [String: Int] = [:]
        var page = 0
        var next: Int? = 1
        var stalled = 0
        var error: MusicServiceError?
        var diagnosticID: String?
    }
    private struct Playlists {
        var items: [MusicPublicBrowseCollection] = []
        var ranks: [String: Int] = [:]
        var acceptedPages: [[String]] = []
        var pages = 0
        var cursor: String?
        var more = true
        var stalled = 0
        var error: MusicServiceError?
        var diagnosticID: String?
    }
    private let readSongs: SongRead
    private let readPlaylists: PlaylistRead
    private let timing: MusicCandidateDiscoveryTiming
    private var songStates: [MusicPublicBrowsePlatform: Songs] = [:]
    private var playlistStates: [MusicPublicBrowsePlatform: Playlists] = [:]
    private var songTask: Task<Void, Never>?
    private var playlistTask: Task<Void, Never>?
    private var songToken = UUID(), playlistToken = UUID()
    private var pendingSongPlatforms = Set<MusicPublicBrowsePlatform>()
    private var recoveryID: UUID?
    private var access = UUID()
    private var available = false, visible = false
    private enum ReadMode: Equatable { case initial, more, retry }
    private var usable: Bool { available && visible && !query.isEmpty }

    public init(readSongs: @escaping SongRead, readPlaylists: @escaping PlaylistRead,
                timing: MusicCandidateDiscoveryTiming = .init()) {
        self.readSongs = readSongs; self.readPlaylists = readPlaylists; self.timing = timing
    }
    public var moreSongs: Bool { songStates.values.contains { $0.next != nil && $0.page < 100 && $0.stalled < 4 } }
    public var morePlaylists: Bool { playlistStates.values.contains { $0.more && $0.pages < 100 && $0.stalled < 4 } }
    public var songPlatformsEmpty: Bool { preferences.songPlatforms.isEmpty }
    public var playlistPlatformsEmpty: Bool { preferences.effectivePlaylistPlatforms.isEmpty }

    public func setAccess(available: Bool, generation: UUID) {
        if access != generation || self.available != available {
            clear()
            access = generation
            self.available = available
        }
    }
    public func appear() {
        guard !visible else { return }
        visible = true
        if !songsCancelled { loadSongs(.initial) }
        if !playlistsCancelled { loadPlaylists(.initial) }
    }
    public func disappear() {
        visible = false
        recoveryID = nil
        cancel(.songs, byUser: false); cancel(.playlists, byUser: false)
        automaticPaginationHeld = true
    }
    public func resumePagination() { if usable { automaticPaginationHeld = false } }
    public func holdPagination() { automaticPaginationHeld = true }

    public func submit(_ keyword: String, preferences: MusicSearchPreferences) {
        clear()
        query = keyword.trimmingCharacters(in: .whitespacesAndNewlines)
        self.preferences = preferences.normalized
        for platform in self.preferences.songPlatforms { songStates[platform] = Songs() }
        for platform in self.preferences.effectivePlaylistPlatforms { playlistStates[platform] = Playlists() }
        loadSongs(.initial); loadPlaylists(.initial)
    }
    public func clear() {
        recoveryID = nil
        cancel(.songs, byUser: false); cancel(.playlists, byUser: false)
        requestID = UUID(); query = ""; songs = []; playlists = []
        songStates = [:]; playlistStates = [:]; songFailures = []; playlistFailures = []
        songsLoaded = false; playlistsLoaded = false
        songsCancelled = false; playlistsCancelled = false; automaticPaginationHeld = false
    }
    public func cancel(_ section: MusicSearchSection, byUser: Bool = true) {
        switch section {
        case .songs:
            songToken = UUID(); songTask?.cancel(); songTask = nil; songsLoading = false
            pendingSongPlatforms = []
            if byUser { songsCancelled = true; recoveryID = nil }
        case .playlists:
            playlistToken = UUID(); playlistTask?.cancel(); playlistTask = nil; playlistsLoading = false
            if byUser { playlistsCancelled = true }
        }
    }
    public func more(_ section: MusicSearchSection, automatic: Bool = false) {
        guard !automatic || !automaticPaginationHeld else { return }
        switch section {
        case .songs: if !songsCancelled && songFailures.isEmpty { loadSongs(.more) }
        case .playlists: if !playlistsCancelled && playlistFailures.isEmpty { loadPlaylists(.more) }
        }
    }
    public func retry(_ section: MusicSearchSection) {
        switch section {
        case .songs: loadSongs(.retry)
        case .playlists: loadPlaylists(.retry)
        }
    }
    /// Explicit user restart after the upstream changed an already loaded
    /// playlist prefix. Never clear rows merely because a cursor expired.
    public func restartPlaylists() {
        guard usable else { return }
        cancel(.playlists, byUser: false)
        playlistStates = Dictionary(uniqueKeysWithValues: preferences.effectivePlaylistPlatforms.map { ($0, Playlists()) })
        playlistsCancelled = false; playlistsLoaded = false
        publishPlaylists()
        loadPlaylists(.initial)
    }
    /// Snapshot the current selected order synchronously at the UI action.
    /// The native writer reuses live owner records or renews missing pages;
    /// the complete batch never depends on retaining all display handles.
    public func makeBatchRequest(startIndex: Int = 0) throws -> MusicSearchBatchRequest {
        guard usable else { throw MusicServiceError.accessUnavailable }
        guard songs.count <= MusicSearchBatchRequest.maximumCount else { throw MusicServiceError.limit }
        let targets = try songs.map { row -> MusicSearchBatchTarget in
            let (platform, _, page) = try origin(of: row.track)
            return .init(platform: platform, page: page, track: row.track)
        }
        let request = MusicSearchBatchRequest(query: query, targets: targets, startIndex: startIndex)
        try request.validate()
        return request
    }
    public func makeBatchRequest(for row: MusicSearchSong) throws -> MusicSearchBatchRequest {
        guard usable else { throw MusicServiceError.accessUnavailable }
        let (platform, _, page) = try origin(of: row.track)
        let request = MusicSearchBatchRequest(query: query,
            targets: [.init(platform: platform, page: page, track: row.track)])
        try request.validate()
        return request
    }
    /// For deterministic callers/tests; never a polling loop.
    public func finishPendingReads() async {
        let songs = songTask, playlists = playlistTask
        await songs?.value; await playlists?.value
    }

    /// Handles are a bounded, expiring owner cache, not a persistent catalogue.
    /// Re-read only the captured query/platform/page before a single-row action.
    /// An absent or changed recording fails closed; other pages/platforms are
    /// never searched as a fallback and accumulated display rows are not pruned.
    public func refreshTrack(_ row: MusicSearchSong, variant: MusicRemoteTrack? = nil) async throws -> MusicRemoteTrack {
        try Task.checkCancellation()
        guard usable else { throw MusicServiceError.accessUnavailable }
        guard recoveryID == nil else { throw MusicServiceError.busy }
        let selected = variant ?? row.track
        guard row.variants.contains(selected) else { throw MusicServiceError.invalidRequest }
        let (platform, key, page) = try origin(of: selected)
        let own = UUID(), request = requestID, generation = access, keyword = query
        let interrupted = pendingSongPlatforms
        holdPagination()
        cancel(.songs, byUser: false)
        recoveryID = own
        defer {
            if recoveryID == own {
                recoveryID = nil
                // Do not lose other platforms' in-flight first/next pages when
                // pausing them to avoid the same-platform native epoch fence.
                if usable, requestID == request, access == generation, !songsCancelled {
                    loadSongs(.more, restrictingTo: interrupted)
                }
            }
        }
        let reader = readSongs
        let result = try await MusicCandidateDiscoveryDeadline.run(seconds: 25, timing: timing) {
            try await reader(platform, keyword, page)
        }
        guard accepts(request, generation), recoveryID == own else { throw MusicServiceError.staleResult }
        guard result.platform == platform.rawValue, result.query == keyword, result.page == page,
              result.items.count <= 200, result.total >= 0 else { throw MusicServiceError.invalidResponse }
        let matches = result.items.filter { MusicSearchFusion.sameSearchTarget($0, selected, platform: platform) }
        guard let fresh = matches.first, var state = songStates[platform],
              state.originPages[key] == page,
              let index = state.items.firstIndex(where: { MusicSearchFusion.sameSearchTarget($0, selected, platform: platform) }) else {
            throw MusicServiceError.staleResult
        }
        state.items[index] = fresh
        songStates[platform] = state
        publishSongs()
        return fresh
    }

    private func origin(of selected: MusicRemoteTrack) throws -> (MusicPublicBrowsePlatform, String, Int) {
        for platform in preferences.songPlatforms {
            let key = MusicSearchFusion.sourceIdentity(selected, platform: platform)
            if let state = songStates[platform], let page = state.originPages[key],
               state.items.contains(where: { MusicSearchFusion.sameSearchTarget($0, selected, platform: platform) }) {
                return (platform, key, page)
            }
        }
        throw MusicServiceError.staleResult
    }

    private func loadSongs(_ mode: ReadMode, restrictingTo requested: Set<MusicPublicBrowsePlatform>? = nil) {
        guard usable, !songsLoading, recoveryID == nil else { return }
        let platforms = preferences.songPlatforms.filter { platform in
            if let requested, !requested.contains(platform) { return false }
            guard let state = songStates[platform], state.next != nil else { return false }
            switch mode {
            case .initial: return state.page == 0 && state.error == nil
            case .more: return state.error == nil
            case .retry: return state.error != nil || state.page == 0 || songsCancelled
            }
        }
        if platforms.isEmpty {
            if preferences.songPlatforms.isEmpty { songsLoaded = true }
            if mode == .retry { songsCancelled = false }
            return
        }
        let own = UUID(), request = requestID, generation = access, keyword = query
        let reader = readSongs, timing = timing
        songToken = own; songsLoading = true; songsCancelled = false
        pendingSongPlatforms = Set(platforms)
        let budget = min(2000, 5000 / max(1, preferences.songPlatforms.count))
        // Requires the native search port's per-platform epoch isolation.
        // Never route these requests through MusicViewModel.search(), whose
        // The shared query/task slot admits one search at a time.
        songTask = Task { [weak self] in
            guard let self else { return }
            defer { if self.songToken == own { self.songsLoading = false; self.songTask = nil } }
            await withTaskGroup(of: Void.self) { group in
              for platform in platforms {
                group.addTask { @MainActor in
                guard self.accepts(request, generation), self.songToken == own,
                      var state = self.songStates[platform], let page = state.next else { return }
                defer { if self.songToken == own { self.pendingSongPlatforms.remove(platform) } }
                let trace = MusicRequestTrace(.search, platform: platform)
                trace.record(.started)
                state.error = nil; state.diagnosticID = nil; self.songStates[platform] = state; self.publishSongs()
                do {
                    let result = try await MusicRequestTrace.$current.withValue(trace) {
                        try await MusicCandidateDiscoveryDeadline.run(seconds: 25, timing: timing) {
                            try await reader(platform, keyword, page)
                        }
                    }
                    guard self.accepts(request, generation), self.songToken == own else { return }
                    guard result.platform == platform.rawValue, result.query == keyword, result.page == page,
                          result.items.count <= 200, result.total >= 0,
                          result.nextPage == nil || result.nextPage! > page else { throw MusicServiceError.invalidResponse }
                    var seen = Set(state.items.map { MusicSearchFusion.sourceIdentity($0, platform: platform) })
                    let added = result.items.filter { seen.insert(MusicSearchFusion.sourceIdentity($0, platform: platform)).inserted }
                    for (index, item) in result.items.enumerated() {
                        let key = MusicSearchFusion.sourceIdentity(item, platform: platform)
                        // The three native adapters request 30 rows per page.
                        // Duplicate rows must not compact later candidates' ranks.
                        if state.ranks[key] == nil { state.ranks[key] = (page - 1) * 30 + index + 1 }
                    }
                    let accepted = added.prefix(max(0, budget - state.items.count))
                    for item in accepted {
                        state.originPages[MusicSearchFusion.sourceIdentity(item, platform: platform)] = page
                    }
                    state.items.append(contentsOf: accepted)
                    state.stalled = added.isEmpty ? state.stalled + 1 : 0
                    state.page = page; state.next = result.nextPage
                    if state.next != nil && (state.page >= 100 || state.stalled >= 4 || state.items.count >= budget) {
                        state.next = nil; state.error = .limit
                    }
                    self.songStates[platform] = state; self.songsLoaded = true
                    trace.record(.completed)
                } catch {
                    trace.failed(error)
                    guard self.accepts(request, generation), self.songToken == own else { return }
                    let failure = MusicServiceError.sanitized(error)
                    state.error = failure == .cancelled ? nil : failure
                    state.diagnosticID = failure == .cancelled ? nil : trace.logicalID
                    self.songStates[platform] = state; self.songsLoaded = true
                }
                self.publishSongs()
                }
              }
            }
        }
    }
    private func loadPlaylists(_ mode: ReadMode) {
        guard usable, preferences.searchPlaylists, !playlistsLoading else { return }
        let platforms = preferences.effectivePlaylistPlatforms.filter { platform in
            guard let state = playlistStates[platform], state.more else { return false }
            switch mode {
            case .initial: return state.pages == 0 && state.error == nil
            case .more: return state.error == nil
            case .retry: return state.error != nil || state.pages == 0 || playlistsCancelled
            }
        }
        if platforms.isEmpty {
            if preferences.effectivePlaylistPlatforms.isEmpty { playlistsLoaded = true }
            if mode == .retry { playlistsCancelled = false }
            return
        }
        let own = UUID(), request = requestID, generation = access, keyword = query
        let reader = readPlaylists, timing = timing
        playlistToken = own; playlistsLoading = true; playlistsCancelled = false
        let budget = min(2000, 5000 / max(1, preferences.effectivePlaylistPlatforms.count))
        playlistTask = Task { [weak self] in
            guard let self else { return }
            defer { if self.playlistToken == own { self.playlistsLoading = false; self.playlistTask = nil } }
            await withTaskGroup(of: Void.self) { group in
              for platform in platforms {
                group.addTask { @MainActor in
                guard self.accepts(request, generation), self.playlistToken == own,
                      var state = self.playlistStates[platform] else { return }
                let trace = MusicRequestTrace(.search, platform: platform)
                trace.record(.started)
                state.error = nil; state.diagnosticID = nil; self.playlistStates[platform] = state; self.publishPlaylists()
                let cursor = state.cursor, acceptedPages = state.acceptedPages
                do {
                    let result = try await MusicRequestTrace.$current.withValue(trace) {
                        try await MusicCandidateDiscoveryDeadline.run(seconds: 25, timing: timing) {
                            try await MusicSearchPlaylistRecovery.read(platform: platform, query: keyword, cursor: cursor,
                                acceptedPages: acceptedPages, reader: reader)
                        }
                    }
                    guard self.accepts(request, generation), self.playlistToken == own else { return }
                    guard result.items.count <= 200, result.items.allSatisfy({ $0.platform == platform }),
                          result.cursor == nil || result.cursor != cursor else { throw MusicServiceError.invalidResponse }
                    var seen = Set(state.items.map(\.id))
                    let added = result.items.filter { seen.insert($0.id).inserted }
                    for (index, item) in result.items.enumerated() where state.ranks[item.id] == nil {
                        state.ranks[item.id] = state.pages * 30 + index + 1
                    }
                    state.items.append(contentsOf: added.prefix(max(0, budget - state.items.count)))
                    state.stalled = added.isEmpty ? state.stalled + 1 : 0
                    state.acceptedPages.append(result.items.map(\.id))
                    state.pages += 1; state.cursor = result.cursor; state.more = result.cursor != nil
                    if state.more && (state.pages >= 100 || state.stalled >= 4 || state.items.count >= budget) {
                        state.more = false; state.error = .limit
                    }
                    self.playlistStates[platform] = state; self.playlistsLoaded = true
                    trace.record(.completed)
                } catch {
                    trace.failed(error)
                    guard self.accepts(request, generation), self.playlistToken == own else { return }
                    let failure = MusicServiceError.sanitized(error)
                    state.error = failure == .cancelled ? nil : failure
                    state.diagnosticID = failure == .cancelled ? nil : trace.logicalID
                    self.playlistStates[platform] = state; self.playlistsLoaded = true
                }
                self.publishPlaylists()
                }
              }
            }
        }
    }
    private func accepts(_ request: UUID, _ generation: UUID) -> Bool {
        !Task.isCancelled && usable && requestID == request && access == generation
    }
    private func publishSongs() {
        songs = MusicSearchFusion.songs(preferences.songPlatforms.map {
            .init(platform: $0, items: songStates[$0]?.items ?? [], ranks: songStates[$0]?.ranks ?? [:])
        })
        songFailures = preferences.songPlatforms.compactMap { platform in
            songStates[platform]?.error.map { .init(platform: platform, error: $0, diagnosticID: songStates[platform]?.diagnosticID) }
        }
    }
    private func publishPlaylists() {
        playlists = MusicSearchFusion.playlists(preferences.effectivePlaylistPlatforms.map {
            .init(platform: $0, items: playlistStates[$0]?.items ?? [], ranks: playlistStates[$0]?.ranks ?? [:])
        })
        playlistFailures = preferences.effectivePlaylistPlatforms.compactMap { platform in
            playlistStates[platform]?.error.map { .init(platform: platform, error: $0, diagnosticID: playlistStates[platform]?.diagnosticID) }
        }
    }
    deinit { songTask?.cancel(); playlistTask?.cancel() }
}
#endif
