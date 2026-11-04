// SPDX-License-Identifier: GPL-3.0-only
#if os(iOS) || os(macOS)
import Foundation
import Combine
import MeowContracts

public enum MusicSearchSection: Hashable, Sendable { case songs, playlists }
public struct MusicSearchPlatformFailure: Sendable, Identifiable {
    public let platform: MusicPublicBrowsePlatform
    public let error: MusicServiceError
    public let diagnosticID: String?
    public var id: String { platform.rawValue }
}

/// Owns the query's independent song/playlist rounds, cursors and temporary cache.
/// Queue, account, library writes and playback remain with their existing owners.
@MainActor public final class MusicSearchResultsModel: ObservableObject {
    public typealias SongRead = @MainActor @Sendable (MusicPublicBrowsePlatform, String, Int, Int) async throws -> MusicSearchPage
    public typealias PlaylistRead = @MainActor @Sendable (MusicPublicBrowsePlatform, String, Int, Int) async throws -> MusicPlaylistSearchPage
    public typealias RecordingRead = @MainActor @Sendable (MusicSearchBatchRequest) async throws -> MusicRemoteTrack
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
    @Published public private(set) var automaticPaginationHeld = false

    private struct SongOrigin: Sendable {
        let request: MusicSearchPagination.Request
        let index: Int
    }
    private struct PlatformState<Item: Sendable> {
        var items: [Item] = []
        var ranks: [String: Int] = [:]
        var origins: [MusicReference: SongOrigin] = [:]
        var roundItems: [Item] = []
        var lastPage = 0
        var next: Int? = 1
        var error: MusicServiceError?
        var diagnosticID: String?
    }
    private struct CacheKey: Hashable, Sendable {
        let kind: MusicSearchSection
        let platform: MusicPublicBrowsePlatform
        let query: String
        let request: MusicSearchPagination.Request
    }
    private enum CachedPage: Sendable {
        case songs(MusicSearchPage)
        case playlists(MusicPlaylistSearchPage)
    }
    private let readSongs: SongRead
    private let readPlaylists: PlaylistRead
    private let readRecording: RecordingRead
    private let timing: MusicCandidateDiscoveryTiming
    private let now: @Sendable () -> Date
    private var songStates: [MusicPublicBrowsePlatform: PlatformState<MusicRemoteTrack>] = [:]
    private var playlistStates: [MusicPublicBrowsePlatform: PlatformState<MusicPublicBrowseCollection>] = [:]
    private var songTask: Task<Void, Never>?, playlistTask: Task<Void, Never>?
    private var songToken = UUID(), playlistToken = UUID()
    private var recoveryID: UUID?
    private var access = UUID()
    private var available = false, visible = false
    private var songRound = 0, playlistRound = 0
    private var cache: AsyncValueCache<CacheKey, CachedPage>
    private enum ReadMode { case initial, more, retry }
    private var usable: Bool { available && visible && !query.isEmpty }

    public init(readSongs: @escaping SongRead, readPlaylists: @escaping PlaylistRead,
                readRecording: @escaping RecordingRead,
                timing: MusicCandidateDiscoveryTiming = .init(),
                clock: @escaping @Sendable () -> Date = { Date() }) {
        self.readSongs = readSongs; self.readPlaylists = readPlaylists
        self.readRecording = readRecording
        self.timing = timing; now = clock; cache = Self.makeCache(clock)
    }
    public var moreSongs: Bool { songStates.values.contains { $0.next != nil } }
    public var morePlaylists: Bool { playlistStates.values.contains { $0.next != nil } }
    public var songPlatformsEmpty: Bool { preferences.songPlatforms.isEmpty }
    public var playlistPlatformsEmpty: Bool { preferences.effectivePlaylistPlatforms.isEmpty }

    /// Returns true only when the call actually cleared the previous account's
    /// rows, cursors and cache.
    @discardableResult
    public func setAccess(available: Bool, generation: UUID) -> Bool {
        guard access != generation || self.available != available else { return false }
        clear()
        if access != generation || !available {
            // An old loader retains only its old cache. It cannot refill the new
            // account's cache even if physical cancellation is non-cooperative.
            cache = Self.makeCache(now)
        }
        access = generation; self.available = available
        return true
    }
    public func appear() {
        guard !visible else { return }
        visible = true
        if !songsCancelled { loadSongs(.initial) }
        if !playlistsCancelled { loadPlaylists(.initial) }
    }
    public func disappear() {
        visible = false; recoveryID = nil
        cancel(.songs, byUser: false); cancel(.playlists, byUser: false)
        automaticPaginationHeld = true
    }
    public func resumePagination() { if usable { automaticPaginationHeld = false } }
    public func holdPagination() { automaticPaginationHeld = true }
    public func submit(_ keyword: String, preferences: MusicSearchPreferences) {
        clear()
        query = keyword.trimmingCharacters(in: .whitespacesAndNewlines)
        self.preferences = preferences
        for platform in self.preferences.songPlatforms { songStates[platform] = .init() }
        for platform in self.preferences.effectivePlaylistPlatforms { playlistStates[platform] = .init() }
        loadSongs(.initial); loadPlaylists(.initial)
    }
    public func clear() {
        recoveryID = nil
        cancel(.songs, byUser: false); cancel(.playlists, byUser: false)
        requestID = UUID(); query = ""; songs = []; playlists = []
        songStates = [:]; playlistStates = [:]; songFailures = []; playlistFailures = []
        songsLoaded = false; playlistsLoaded = false
        songsCancelled = false; playlistsCancelled = false; automaticPaginationHeld = false
        songRound = 0; playlistRound = 0
    }
    public func cancel(_ section: MusicSearchSection, byUser: Bool = true) {
        switch section {
        case .songs:
            songToken = UUID(); songTask?.cancel(); songTask = nil; songsLoading = false
            if byUser { songsCancelled = true; recoveryID = nil }
        case .playlists:
            playlistToken = UUID(); playlistTask?.cancel(); playlistTask = nil; playlistsLoading = false
            if byUser { playlistsCancelled = true }
        }
    }
    public func more(_ section: MusicSearchSection, automatic: Bool = false) {
        guard !automatic || !automaticPaginationHeld else { return }
        switch section {
        case .songs: if !songsCancelled { loadSongs(.more) }
        case .playlists: if !playlistsCancelled { loadPlaylists(.more) }
        }
    }
    public func retry(_ section: MusicSearchSection) {
        switch section {
        case .songs: loadSongs(.retry)
        case .playlists: loadPlaylists(.retry)
        }
    }
    public func makeBatchRequest(startIndex: Int = 0) throws -> MusicSearchBatchRequest {
        guard usable else { throw MusicServiceError.accessUnavailable }
        let targets = try songs.map { try batchTarget($0) }
        let result = MusicSearchBatchRequest(query: query, targets: targets, startIndex: startIndex)
        try result.validate(); return result
    }
    public func makeBatchRequest(for row: MusicSearchSong) throws -> MusicSearchBatchRequest {
        guard usable else { throw MusicServiceError.accessUnavailable }
        let result = MusicSearchBatchRequest(query: query, targets: [try batchTarget(row)])
        try result.validate(); return result
    }
    public func finishPendingReads() async {
        let songs = songTask, playlists = playlistTask
        await songs?.value; await playlists?.value
    }
    /// Reads the selected recording through the native owner's same batch
    /// resolver. Search rows keep their origins, identities and positions.
    public func refreshTrack(_ row: MusicSearchSong, variant: MusicRemoteTrack? = nil) async throws -> MusicRemoteTrack {
        try Task.checkCancellation()
        guard usable else { throw MusicServiceError.accessUnavailable }
        guard recoveryID == nil else { throw MusicServiceError.busy }
        let target = try batchTarget(row, preferred: variant)
        let own = UUID(), request = requestID, generation = access, keyword = query
        holdPagination(); recoveryID = own
        defer { if recoveryID == own { recoveryID = nil } }
        let result = try await readRecording(.init(query: keyword, targets: [target]))
        guard accepts(request, generation), recoveryID == own else { throw MusicServiceError.staleResult }
        guard target.origins.allSatisfy({ origin in
            MusicSearchFusion.sameSearchTarget(result, origin.track, platform: origin.platform)
        }) else {
            throw MusicServiceError.targetChanged
        }
        return result
    }
    private func batchTarget(_ row: MusicSearchSong, preferred: MusicRemoteTrack? = nil) throws -> MusicSearchBatchTarget {
        if let preferred, !row.variants.contains(preferred) { throw MusicServiceError.invalidRequest }
        var platforms = preferences.songPlatforms
        if let preferred, let index = platforms.firstIndex(where: { platform in
            preferred.sources.contains { $0.platform == platform.rawValue }
        }) {
            platforms.insert(platforms.remove(at: index), at: 0)
        }
        var origins: [MusicSearchBatchOrigin] = []
        for platform in platforms {
            var candidates = row.variants.filter { $0.sources.contains { $0.platform == platform.rawValue } }
            guard !candidates.isEmpty else { continue }
            guard let state = songStates[platform] else { throw MusicServiceError.staleResult }
            if let preferred, let index = candidates.firstIndex(of: preferred) {
                candidates.insert(candidates.remove(at: index), at: 0)
            }
            var keys: [String] = []
            var selected: [String: (track: MusicRemoteTrack, origin: SongOrigin)] = [:]
            for track in candidates {
                guard let origin = state.origins[track.ref], state.items[origin.index] == track else { continue }
                let key = MusicSearchFusion.sourceIdentity(track, platform: platform)
                if let previous = selected[key] {
                    if origin.index > previous.origin.index { selected[key] = (track, origin) }
                } else {
                    keys.append(key); selected[key] = (track, origin)
                }
            }
            guard !keys.isEmpty else { throw MusicServiceError.staleResult }
            for key in keys {
                let value = selected[key]!
                origins.append(.init(platform: platform, page: value.origin.request.page,
                                     limit: value.origin.request.limit, track: value.track))
            }
        }
        let target = try MusicSearchBatchTarget(origins: origins)
        try MusicSearchBatchRequest(query: query, targets: [target]).validate()
        return target
    }
    private func eligible<Item>(_ state: PlatformState<Item>, mode: ReadMode, cancelled: Bool) -> Bool {
        guard state.next != nil else { return false }
        switch mode {
        case .initial: return state.lastPage == 0 && state.error == nil
        case .more: return true
        case .retry: return state.error != nil || state.lastPage == 0 || cancelled
        }
    }
    private func loadSongs(_ mode: ReadMode) {
        guard usable, !songsLoading, recoveryID == nil else { return }
        let platforms = preferences.songPlatforms.filter {
            songStates[$0].map { eligible($0, mode: mode, cancelled: songsCancelled) } == true
        }
        guard !platforms.isEmpty else {
            if preferences.songPlatforms.isEmpty { songsLoaded = true }
            if mode == .retry { songsCancelled = false; publishSongs(first: songRound <= 1) }
            return
        }
        if mode != .initial || songRound == 0 { songRound += 1 }
        let first = songRound == 1, own = UUID(), request = requestID, generation = access
        let keyword = query, settings = preferences.songs
        songToken = own; songsLoading = true; songsCancelled = false
        songTask = Task { [weak self] in
            guard let self else { return }
            await withTaskGroup(of: Void.self) { group in
                for platform in platforms {
                    group.addTask { @MainActor in
                        await self.readSongPlatform(platform, keyword: keyword, settings: settings,
                            request: request, generation: generation, token: own, first: first)
                    }
                }
            }
            guard self.accepts(request, generation), self.songToken == own else { return }
            self.publishSongs(first: first)
            self.songsLoading = false; self.songTask = nil
        }
    }
    private func readSongPlatform(_ platform: MusicPublicBrowsePlatform, keyword: String,
        settings: MusicSearchPreferences.Kind, request: UUID, generation: UUID, token: UUID, first: Bool) async {
        guard accepts(request, generation), songToken == token, let start = songStates[platform]?.next else { return }
        songStates[platform]?.error = nil; songStates[platform]?.diagnosticID = nil
        let trace = MusicRequestTrace(.search, platform: platform)
        trace.record(.started)
        let cache = cache, reader = readSongs, timing = timing
        do {
            try await MusicSearchRoundReader.read(platform: platform, start: start,
                pages: settings.pages, concurrency: settings.concurrency, load: { range in
                    guard self.accepts(request, generation), self.songToken == token else { throw MusicServiceError.cancelled }
                    let key = CacheKey(kind: .songs, platform: platform, query: keyword, request: range)
                    let cached = try await cache.value(for: key, lifetime: TimeInterval(settings.cacheMinutes) * 60,
                        observe: { trace.record($0 == .hit ? .cacheHit : $0 == .miss ? .cacheMiss : .coalesced) }) {
                        let value = try await MusicRequestTrace.$current.withValue(trace) {
                            try await MusicCandidateDiscoveryDeadline.run(seconds: 25, timing: timing) {
                                try await reader(platform, keyword, range.page, range.limit)
                            }
                        }
                        try Self.validate(value, platform: platform, keyword: keyword, range: range)
                        return .songs(value)
                    }
                    guard case .songs(let value) = cached else { throw MusicServiceError.invalidResponse }
                    return value
                }, accept: { range, result in
                    guard self.accepts(request, generation), self.songToken == token else { throw MusicServiceError.cancelled }
                    try self.acceptSong(result, platform: platform, range: range, first: first)
                    return result.nextPage
                })
            trace.record(.completed)
        } catch {
            trace.failed(error)
            guard accepts(request, generation), songToken == token else { return }
            let failure = MusicServiceError.sanitized(error)
            songStates[platform]?.error = failure == .cancelled ? nil : failure
            songStates[platform]?.diagnosticID = failure == .cancelled ? nil : trace.logicalID
        }
        if accepts(request, generation), songToken == token { songsLoaded = true }
    }
    private func acceptSong(_ page: MusicSearchPage, platform: MusicPublicBrowsePlatform,
                            range: MusicSearchPagination.Request, first: Bool) throws {
        guard var state = songStates[platform] else { throw MusicServiceError.staleResult }
        var seen = Dictionary(grouping: state.items) { MusicSearchFusion.sourceIdentity($0, platform: platform) }
        for (index, item) in page.items.enumerated() {
            let key = MusicSearchFusion.sourceIdentity(item, platform: platform)
            if let evidence = seen[key] {
                guard evidence.allSatisfy({ MusicSearchFusion.sameRecording($0, item) }) else {
                    throw MusicServiceError.nativeIDConflict
                }
                if evidence.contains(item) { continue }
            } else {
                state.ranks[key] = (range.page - 1) * range.limit + index + 1
            }
            state.origins[item.ref] = .init(request: range, index: state.items.count)
            seen[key, default: []].append(item)
            state.items.append(item); state.roundItems.append(item)
        }
        state.lastPage = range.page; state.next = page.nextPage
        songStates[platform] = state; songsLoaded = true
        if first { publishSongs(first: true) }
    }
    private func loadPlaylists(_ mode: ReadMode) {
        guard usable, preferences.searchPlaylists, !playlistsLoading else { return }
        let platforms = preferences.effectivePlaylistPlatforms.filter {
            playlistStates[$0].map { eligible($0, mode: mode, cancelled: playlistsCancelled) } == true
        }
        guard !platforms.isEmpty else {
            if preferences.effectivePlaylistPlatforms.isEmpty { playlistsLoaded = true }
            if mode == .retry { playlistsCancelled = false; publishPlaylists(first: playlistRound <= 1) }
            return
        }
        if mode != .initial || playlistRound == 0 { playlistRound += 1 }
        let first = playlistRound == 1, own = UUID(), request = requestID, generation = access
        let keyword = query, settings = preferences.playlists
        playlistToken = own; playlistsLoading = true; playlistsCancelled = false
        playlistTask = Task { [weak self] in
            guard let self else { return }
            await withTaskGroup(of: Void.self) { group in
                for platform in platforms {
                    group.addTask { @MainActor in
                        await self.readPlaylistPlatform(platform, keyword: keyword, settings: settings,
                            request: request, generation: generation, token: own, first: first)
                    }
                }
            }
            guard self.accepts(request, generation), self.playlistToken == own else { return }
            self.publishPlaylists(first: first)
            self.playlistsLoading = false; self.playlistTask = nil
        }
    }
    private func readPlaylistPlatform(_ platform: MusicPublicBrowsePlatform, keyword: String,
        settings: MusicSearchPreferences.Kind, request: UUID, generation: UUID, token: UUID, first: Bool) async {
        guard accepts(request, generation), playlistToken == token, let start = playlistStates[platform]?.next else { return }
        playlistStates[platform]?.error = nil; playlistStates[platform]?.diagnosticID = nil
        let trace = MusicRequestTrace(.playlist, platform: platform)
        trace.record(.started)
        let cache = cache, reader = readPlaylists, timing = timing
        do {
            try await MusicSearchRoundReader.read(platform: platform, start: start,
                pages: settings.pages, concurrency: settings.concurrency, load: { range in
                    guard self.accepts(request, generation), self.playlistToken == token else { throw MusicServiceError.cancelled }
                    let key = CacheKey(kind: .playlists, platform: platform, query: keyword, request: range)
                    let cached = try await cache.value(for: key, lifetime: TimeInterval(settings.cacheMinutes) * 60,
                        observe: { trace.record($0 == .hit ? .cacheHit : $0 == .miss ? .cacheMiss : .coalesced) }) {
                        let value = try await MusicRequestTrace.$current.withValue(trace) {
                            try await MusicCandidateDiscoveryDeadline.run(seconds: 25, timing: timing) {
                                try await reader(platform, keyword, range.page, range.limit)
                            }
                        }
                        try Self.validate(value, platform: platform, keyword: keyword, range: range)
                        return .playlists(value)
                    }
                    guard case .playlists(let value) = cached else { throw MusicServiceError.invalidResponse }
                    return value
                }, accept: { range, result in
                    guard self.accepts(request, generation), self.playlistToken == token else { throw MusicServiceError.cancelled }
                    self.acceptPlaylist(result, platform: platform, range: range, first: first)
                    return result.nextPage
                })
            trace.record(.completed)
        } catch {
            trace.failed(error)
            guard accepts(request, generation), playlistToken == token else { return }
            let failure = MusicServiceError.sanitized(error)
            playlistStates[platform]?.error = failure == .cancelled ? nil : failure
            playlistStates[platform]?.diagnosticID = failure == .cancelled ? nil : trace.logicalID
        }
        if accepts(request, generation), playlistToken == token { playlistsLoaded = true }
    }
    private func acceptPlaylist(_ page: MusicPlaylistSearchPage, platform: MusicPublicBrowsePlatform,
                                range: MusicSearchPagination.Request, first: Bool) {
        guard var state = playlistStates[platform] else { return }
        var seen = Set(state.items.map(\.id))
        for (index, item) in page.items.enumerated() where seen.insert(item.id).inserted {
            state.items.append(item); state.roundItems.append(item)
            state.ranks[item.id] = (range.page - 1) * range.limit + index + 1
        }
        state.lastPage = range.page; state.next = page.nextPage
        playlistStates[platform] = state; playlistsLoaded = true
        if first { publishPlaylists(first: true) }
    }
    private func accepts(_ request: UUID, _ generation: UUID) -> Bool {
        !Task.isCancelled && usable && requestID == request && access == generation
    }
    private func publishSongs(first: Bool) {
        let batches = preferences.songPlatforms.map {
            MusicSearchFusion.SongBatch(platform: $0,
                items: first ? songStates[$0]?.items ?? [] : songStates[$0]?.roundItems ?? [],
                ranks: songStates[$0]?.ranks ?? [:])
        }
        songs = first ? MusicSearchFusion.songs(batches, platformOrder: preferences.songPlatforms)
            : MusicSearchFusion.appendSongs(displayed: songs, adding: batches, platformOrder: preferences.songPlatforms)
        for platform in preferences.songPlatforms { songStates[platform]?.roundItems = [] }
        songFailures = preferences.songPlatforms.compactMap { platform in
            songStates[platform]?.error.map { .init(platform: platform, error: $0, diagnosticID: songStates[platform]?.diagnosticID) }
        }
    }
    private func publishPlaylists(first: Bool) {
        let platforms = preferences.effectivePlaylistPlatforms
        let batches = platforms.map {
            MusicSearchFusion.PlaylistBatch(platform: $0,
                items: first ? playlistStates[$0]?.items ?? [] : playlistStates[$0]?.roundItems ?? [],
                ranks: playlistStates[$0]?.ranks ?? [:])
        }
        playlists = first ? MusicSearchFusion.playlists(batches, platformOrder: platforms)
            : MusicSearchFusion.appendPlaylists(displayed: playlists, adding: batches, platformOrder: platforms)
        for platform in platforms { playlistStates[platform]?.roundItems = [] }
        playlistFailures = platforms.compactMap { platform in
            playlistStates[platform]?.error.map { .init(platform: platform, error: $0, diagnosticID: playlistStates[platform]?.diagnosticID) }
        }
    }
    private nonisolated static func validate(_ result: MusicSearchPage, platform: MusicPublicBrowsePlatform,
        keyword: String, range: MusicSearchPagination.Request) throws {
        // Migu's adapter validates raw groups before expanding or deduplicating editions.
        guard result.platform == platform.rawValue, result.query == keyword, result.page == range.page,
              result.limit == range.limit, result.total >= 0,
              (platform == .migu || result.items.count <= range.limit),
              MusicSearchPagination.validNextPage(page: range.page, limit: range.limit,
                  next: result.nextPage, hasItems: !result.items.isEmpty),
              result.items.allSatisfy({ !$0.songId.isEmpty && $0.duration.isFinite && $0.duration >= 0 &&
                  $0.sources.contains(where: { $0.platform == platform.rawValue && !$0.nativeId.isEmpty && !$0.sourceKey.isEmpty }) }) else {
            throw MusicServiceError.invalidResponse
        }
    }
    private nonisolated static func validate(_ result: MusicPlaylistSearchPage, platform: MusicPublicBrowsePlatform,
        keyword: String, range: MusicSearchPagination.Request) throws {
        guard result.platform == platform, result.query == keyword, result.page == range.page,
              result.limit == range.limit, result.total >= 0, result.items.count <= range.limit,
              MusicSearchPagination.validNextPage(page: range.page, limit: range.limit,
                  next: result.nextPage, hasItems: !result.items.isEmpty),
              result.items.allSatisfy({ $0.platform == platform && $0.kind == .playlist }) else {
            throw MusicServiceError.invalidResponse
        }
    }
    private nonisolated static func makeCache(_ clock: @escaping @Sendable () -> Date) -> AsyncValueCache<CacheKey, CachedPage> {
        .init(capacity: nil, maximumCost: 32 * 1024 * 1024, cost: { (value: CachedPage) -> Int in
            switch value {
            case .songs(let page):
                var cost = page.query.utf8.count + 256
                for track in page.items {
                    cost += track.songId.utf8.count + track.title.utf8.count
                    cost += track.artist.utf8.count + track.album.utf8.count
                    cost += (track.artwork?.absoluteString.utf8.count ?? 0) + 256
                    for source in track.sources {
                        cost += source.sourceKey.utf8.count + source.nativeId.utf8.count + 64
                    }
                }
                return cost
            case .playlists(let page):
                var cost = page.query.utf8.count + 256
                for playlist in page.items {
                    cost += playlist.id.utf8.count + playlist.title.utf8.count
                    cost += playlist.creator.utf8.count + (playlist.artwork?.absoluteString.utf8.count ?? 0) + 192
                }
                return cost
            }
        }, clock: clock)
    }
    deinit { songTask?.cancel(); playlistTask?.cancel() }
}
#endif
