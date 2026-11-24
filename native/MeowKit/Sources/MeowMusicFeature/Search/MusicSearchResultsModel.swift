// SPDX-License-Identifier: GPL-3.0-only
#if os(iOS) || os(macOS)
import Foundation
import Combine
import MeowContracts
import MeowDebug

public enum MusicSearchSection: Hashable, Sendable { case songs, playlists }
public struct MusicSearchPlatformFailure: Sendable, Identifiable {
    public let platform: MusicPublicBrowsePlatform
    public let error: MusicServiceError
    public let diagnosticID: String?
    public var id: String { platform.rawValue }
}

/// Owns the query's independent song/playlist rounds and cursors.
/// Queue, account, library writes and playback remain with their existing owners.
@MainActor public final class MusicSearchResultsModel: ObservableObject {
    public typealias SongRead = @MainActor @Sendable (MusicPublicBrowsePlatform, String, Int, Int) async throws -> MusicSearchPage
    public typealias PlaylistRead = @MainActor @Sendable (MusicPublicBrowsePlatform, String, Int, Int) async throws -> MusicPlaylistSearchPage
    public typealias RecordingRead = @MainActor @Sendable (MusicSearchBatchRequest) async throws -> MusicRemoteTrack
    public typealias ObservationWrite = @MainActor @Sendable ([MusicSearchPage], [MusicReference]) async throws -> Void
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

    private struct PlatformState<Item: Sendable> {
        var items: [Item] = []
        var ranks: [String: Double] = [:]
        var pages: [MusicSearchPage] = []
        var roundItems: [Item] = []
        var lastPage = 0
        var next: Int? = 1
        var error: MusicServiceError?
        var diagnosticID: String?
    }
    private typealias CacheKey = MusicSearchResultsCache.Key
    private let readSongs: SongRead
    private let readPlaylists: PlaylistRead
    private let readRecording: RecordingRead
    private let writeObservations: ObservationWrite
    private let cacheProvider: @MainActor () -> MusicSearchResultsCache.Storage
    private var songStates: [MusicPublicBrowsePlatform: PlatformState<MusicRemoteTrack>] = [:]
    private var playlistStates: [MusicPublicBrowsePlatform: PlatformState<MusicPublicBrowseCollection>] = [:]
    private var songTask: Task<Void, Never>?, playlistTask: Task<Void, Never>?
    private var repository: (any MusicSongRepositoryReading)?
    private var songObservation: Task<Void, Never>?, songProjectionTask: Task<Void, Never>?
    private var songProjectionID = UUID()
    private var songToken = UUID(), playlistToken = UUID()
    private var recoveryID: UUID?
    private var access = UUID()
    private var available = false, visible = false
    private var songRound = 0, playlistRound = 0
    private var cache: MusicSearchResultsCache.Storage
    private enum ReadMode { case initial, more, retry }
    private var usable: Bool { available && visible && !query.isEmpty }

    init(readSongs: @escaping SongRead, readPlaylists: @escaping PlaylistRead,
         readRecording: @escaping RecordingRead, writeObservations: @escaping ObservationWrite = { _, _ in },
         cacheProvider: @escaping @MainActor () -> MusicSearchResultsCache.Storage) {
        self.readSongs = readSongs; self.readPlaylists = readPlaylists
        self.readRecording = readRecording
        self.writeObservations = writeObservations
        self.cacheProvider = cacheProvider; cache = cacheProvider()
    }
    convenience init(owner: MusicViewModel) {
        self.init(readSongs: { [weak owner] platform, query, page, limit in
            guard let owner else { throw MusicServiceError.closed }
            return try await owner.searchPage(platform: platform.rawValue, query: query, page: page, limit: limit)
        }, readPlaylists: { [weak owner] platform, query, page, limit in
            guard let owner else { throw MusicServiceError.closed }
            return try await owner.searchPlaylistPage(platform: platform, query: query, page: page, limit: limit)
        }, readRecording: { [weak owner] request in
            guard let owner else { throw MusicServiceError.closed }
            return try await owner.searchRecording(request)
        }, writeObservations: { [weak owner] pages, references in
            guard let owner else { throw MusicServiceError.closed }
            try await owner.observeSearchPages(pages, references: references)
        }, cacheProvider: { [weak owner] in
            owner?.searchResultsCache ?? MusicSearchResultsCache.make()
        })
        repository = owner.service as? any MusicSongRepositoryReading
    }
    public var moreSongs: Bool { songStates.values.contains { $0.next != nil } }
    public var morePlaylists: Bool { playlistStates.values.contains { $0.next != nil } }
    public var songPlatformsEmpty: Bool { preferences.songPlatforms.isEmpty }
    public var playlistPlatformsEmpty: Bool { preferences.effectivePlaylistPlatforms.isEmpty }

    /// Returns true only when access changed, clearing rows/cursors and binding
    /// to that access generation's cache.
    @discardableResult
    public func setAccess(available: Bool, generation: UUID) -> Bool {
        guard access != generation || self.available != available else { return false }
        clear()
        if access != generation || !available {
            // An old loader retains only its old cache. It cannot refill the new
            // account's cache even if physical cancellation is non-cooperative.
            cache = cacheProvider()
        }
        access = generation; self.available = available
        songObservation?.cancel(); songObservation = nil
        songProjectionTask?.cancel(); songProjectionTask = nil
        observeSongs()
        return true
    }
    public func appear() {
        guard !visible else { return }
        visible = true
        observeSongs()
        if !songsCancelled { loadSongs(.initial) }
        if !playlistsCancelled { loadPlaylists(.initial) }
    }
    public func disappear() {
        visible = false; recoveryID = nil
        songObservation?.cancel(); songObservation = nil
        songProjectionTask?.cancel(); songProjectionTask = nil
        cancel(.songs, byUser: false); cancel(.playlists, byUser: false)
        automaticPaginationHeld = true
    }
    public func resumePagination() { if usable { automaticPaginationHeld = false } }
    private func observeSongs() {
        guard visible, available, songObservation == nil, let repository else { return }
        songObservation = Task { [weak self] in
            let changes = await repository.songChanges()
            await self?.refreshSongProjections()
            for await changed in changes {
                guard !Task.isCancelled else { return }
                await self?.refreshSongProjections(changed: changed)
            }
        }
    }
    private func refreshSongProjections(changed: Set<String>? = nil) async {
        guard !Task.isCancelled, usable, let repository else { return }
        let own = UUID(), request = requestID, generation = access
        let referenced = Set(songStates.values.flatMap { $0.items + $0.roundItems + $0.pages.flatMap(\.items) }.map(\.songId))
        let ids = changed.map { referenced.intersection($0) } ?? referenced
        guard !ids.isEmpty else { return }
        songProjectionID = own
        do {
            let current = try await repository.repositoryTracks(songIDs: ids)
            guard accepts(request, generation), songProjectionID == own else { return }
            for platform in Array(songStates.keys) {
                guard var state = songStates[platform] else { continue }
                state.items = state.items.map { $0.projected(current) }
                state.roundItems = state.roundItems.map { $0.projected(current) }
                state.pages = state.pages.map { page in
                    .init(platform: page.platform, query: page.query, page: page.page, limit: page.limit,
                        total: page.total, nextPage: page.nextPage, items: page.items.map { $0.projected(current) },
                        observation: page.observation)
                }
                songStates[platform] = state
            }
            songs = songs.map {
                guard current[$0.track.songId] != nil || $0.variants.contains(where: { current[$0.songId] != nil }) else { return $0 }
                return .init(id: $0.id, track: $0.track.projected(current), variants: $0.variants.map { $0.projected(current) },
                    matchedPlatforms: $0.matchedPlatforms, score: $0.score)
            }
        } catch {
            if MusicServiceError.sanitized(error) != .cancelled {
                DebugRuntime.logError(.search, sub: "music.results", event: "refreshSongProjections.error", error: error)
            }
        }
    }
    public func holdPagination() { automaticPaginationHeld = true }
    public func submit(_ keyword: String, preferences: MusicSearchPreferences) {
        clear()
        query = keyword.trimmingCharacters(in: .whitespacesAndNewlines)
        self.preferences = preferences
        DebugRuntime.log(.search, sub: "music", event: "submit", level: .debug, data: .object([
            "query": .string(query),
            "songPlatforms": .array(preferences.songPlatforms.map { .string($0.rawValue) }),
            "playlistPlatforms": .array(preferences.effectivePlaylistPlatforms.map { .string($0.rawValue) })
        ]))
        for platform in self.preferences.songPlatforms { songStates[platform] = .init() }
        for platform in self.preferences.effectivePlaylistPlatforms { playlistStates[platform] = .init() }
        loadSongs(.initial); loadPlaylists(.initial)
    }
    public func clear() {
        songProjectionID = UUID(); songProjectionTask?.cancel(); songProjectionTask = nil
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
        return result
    }
    public func makeBatchRequest(for row: MusicSearchSong) throws -> MusicSearchBatchRequest {
        guard usable else { throw MusicServiceError.accessUnavailable }
        let result = MusicSearchBatchRequest(query: query, targets: [try batchTarget(row)])
        return result
    }
    /// Reads the selected recording through the native owner's same batch
    /// resolver. Search rows keep their permanent identities and positions.
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
        return result
    }
    private func batchTarget(_ row: MusicSearchSong, preferred: MusicRemoteTrack? = nil) throws -> MusicSearchBatchTarget {
        if let preferred {
            guard row.variants.contains(where: { $0.ref == preferred.ref }) else { throw MusicServiceError.invalidRequest }
            return .init(track: preferred)
        }
        return .init(track: row.track)
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
            let pages = self.preferences.songPlatforms.flatMap { self.songStates[$0]?.pages ?? [] }
            let references = Array(Set(self.preferences.songPlatforms.flatMap {
                self.songStates[$0]?.items.map(\.ref) ?? []
            }))
            do { try await self.writeObservations(pages, references) }
            catch {
                // The executed request already owns its facts. Expired display
                // references cannot turn a successful page into a failed search.
                if !Task.isCancelled, MusicServiceError.sanitized(error) != .cancelled {
                    DebugRuntime.logError(.search, sub: "music.results", event: "writeObservations.error", error: error)
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
        let cache = cache, reader = readSongs
        do {
            try await MusicSearchRoundReader.read(platform: platform, start: start,
                pages: settings.pages, concurrency: settings.concurrency, load: { range in
                    guard self.accepts(request, generation), self.songToken == token else { throw MusicServiceError.cancelled }
                    let key = CacheKey(kind: .songs, platform: platform, query: keyword, request: range)
                    let cached = try await cache.value(for: key, lifetime: TimeInterval(settings.cacheMinutes) * 60,
                        observe: { trace.record($0 == .hit ? .cacheHit : $0 == .miss ? .cacheMiss : .coalesced) }) {
                        let value = try await MusicRequestTrace.$current.withValue(trace) {
                            try await MusicOperationDeadline.run(seconds: 25) {
                                try await reader(platform, keyword, range.page, range.limit)
                            }
                        }
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
        let rankOffset = max((Double(range.page) - 1) * Double(range.limit), state.ranks.values.max() ?? 0)
        var seen = Dictionary(grouping: state.items) { MusicSearchFusion.sourceIdentity($0, platform: platform) }
        for (index, item) in page.items.enumerated() {
            let key = MusicSearchFusion.sourceIdentity(item, platform: platform)
            if let evidence = seen[key] {
                if evidence.contains(item) { continue }
            } else {
                state.ranks[key] = rankOffset + Double(index) + 1
            }
            seen[key, default: []].append(item)
            state.items.append(item); state.roundItems.append(item)
        }
        state.lastPage = range.page; state.next = page.nextPage
        state.pages.removeAll { $0.page == page.page && $0.limit == page.limit }
        state.pages.append(page)
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
        let cache = cache, reader = readPlaylists
        do {
            try await MusicSearchRoundReader.read(platform: platform, start: start,
                pages: settings.pages, concurrency: settings.concurrency, load: { range in
                    guard self.accepts(request, generation), self.playlistToken == token else { throw MusicServiceError.cancelled }
                    let key = CacheKey(kind: .playlists, platform: platform, query: keyword, request: range)
                    let cached = try await cache.value(for: key, lifetime: TimeInterval(settings.cacheMinutes) * 60,
                        observe: { trace.record($0 == .hit ? .cacheHit : $0 == .miss ? .cacheMiss : .coalesced) }) {
                        let value = try await MusicRequestTrace.$current.withValue(trace) {
                            try await MusicOperationDeadline.run(seconds: 25) {
                                try await reader(platform, keyword, range.page, range.limit)
                            }
                        }
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
        let rankOffset = max((Double(range.page) - 1) * Double(range.limit), state.ranks.values.max() ?? 0)
        var seen = Set(state.items.map(\.id))
        for (index, item) in page.items.enumerated() where seen.insert(item.id).inserted {
            state.items.append(item); state.roundItems.append(item)
            state.ranks[item.id] = rankOffset + Double(index) + 1
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
        if repository != nil {
            songProjectionTask?.cancel()
            songProjectionTask = Task { [weak self] in await self?.refreshSongProjections() }
        }
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
    deinit { songTask?.cancel(); playlistTask?.cancel(); songObservation?.cancel(); songProjectionTask?.cancel() }
}
#endif
