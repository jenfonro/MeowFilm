#if os(iOS) || os(macOS)
import Foundation
import XCTest
import Combine
import MeowContracts
import MeowPlayback
import MeowStorage
@testable import MeowMusicFeature

private let musicSourceKey = "[\"wy\",\"123\",\"\"]"
private let musicScriptIdentity = MusicScriptIdentity(version: "1.0", contentHash: String(repeating: "a", count: 64),
    revisionHash: String(repeating: "b", count: 64))
private let musicSelection = MusicPlaybackSelection(installationId: "selected-lx", generation: 1,
    siteId: "wy", sourceKey: musicSourceKey, quality: "128k", scriptIdentity: musicScriptIdentity)
private let musicOfficialSelection = MusicPlaybackSelection(installationId: "official:synthetic", generation: 1,
    siteId: "wy", sourceKey: musicSourceKey, quality: "128k")

/// Business-port fixture, not a real platform, document or media engine.
private actor MusicModelService: MusicServing, MusicCachedPlaybackServing,
    MusicCachedPlaybackRecoveryServing, MusicResolvedSourcesServing, MusicAccountBrowsing, MusicArtworkSelecting,
    MusicSongRepositoryReading, MusicPublicBrowsing, MusicPublicPlaylistSearching {
    static let songID = "D0000000-0000-4000-8000-000000000001"
    var entries: [MusicQueueEntry] = (1...3).map { index in
        MusicQueueEntry(id: "entry-\(index)", track: MusicRemoteTrack(
            ref: MusicReference(kind: .queue, id: "entry-\(index)"), songId: MusicModelService.songID,
            title: "合成歌曲", artist: "合成歌手", duration: 180,
            sources: [MusicNativeSource(sourceKey: musicSourceKey, platform: "wy", nativeId: "123")]))
    }
    var current = "entry-1", revision = 1, settingsRevision = 0
    var mode = MusicPlaybackMode.loop
    private var requestQuality = "flac"
    private var officialSources = MusicAccountPlatform.allCases
    private var settingsFailure: MusicServiceError?
    private var settingsGate: MusicCollectionGate?
    private var settingsWrites = 0
    func configureSettings(failure: MusicServiceError? = nil, gate: MusicCollectionGate? = nil) {
        settingsFailure = failure; settingsGate = gate
    }
    func settingsWriteCount() -> Int { settingsWrites }
    var headers: [String: String] = [:]
    var facts: [MusicPlaybackFact] = []
    var moves: [(MusicDirection, MusicAdvanceReason)] = []
    var failChange = false
    var holdNextResolve = false
    var beganResolve: (@Sendable () -> Void)?
    var held: (MusicPlaybackCandidate, CheckedContinuation<MusicPlaybackCandidate, Never>)?
    var searchBegan: (@Sendable () -> Void)?
    var heldSearch: CheckedContinuation<MusicSearchPage, Never>?
    var searchReads = 0
    var searchItems: [MusicRemoteTrack] = []
    private var searchResult: MusicSearchPage?
    private var playlistSearchResult: MusicPlaylistSearchPage?
    func returnSearchResults(_ songs: MusicSearchPage, playlists: MusicPlaylistSearchPage) {
        searchResult = songs; playlistSearchResult = playlists
    }
    var historyItems: [MusicPlayedSong] = []
    private var repositoryRecords: [String: MusicRemoteTrack] = [:]
    private var repositoryObservers: [UUID: AsyncStream<Void>.Continuation] = [:]
    private var repositoryGate: MusicCollectionGate?
    func setSearchItems(_ items: [MusicRemoteTrack]) { searchItems = items }
    func setHistoryItems(_ items: [MusicPlayedSong]) { historyItems = items }
    func publishRepository(_ tracks: [MusicRemoteTrack]) {
        for track in tracks { repositoryRecords[track.songId] = track }
        for observer in repositoryObservers.values { observer.yield(()) }
    }
    func blockRepository(_ gate: MusicCollectionGate) { repositoryGate = gate }
    func repositoryTracks(songIDs: Set<String>) async -> [String: MusicRemoteTrack] {
        let result = repositoryRecords.filter { songIDs.contains($0.key) }
        if let gate = repositoryGate { repositoryGate = nil; await gate.wait() }
        return result
    }
    func songChanges() -> AsyncStream<Void> {
        let id = UUID(), pair = AsyncStream<Void>.makeStream()
        repositoryObservers[id] = pair.continuation
        pair.continuation.onTermination = { [weak self] _ in Task { await self?.removeRepositoryObserver(id) } }
        return pair.stream
    }
    private func removeRepositoryObserver(_ id: UUID) { repositoryObservers[id] = nil }
    var stateBegan: (@Sendable () -> Void)?
    var heldState: CheckedContinuation<MusicPlaybackState, Never>?
    var stateReads = 0
    var hasCachedAudio = false, hasLyrics = false
    var availability = MusicSourceAvailability(options: [
        MusicPlaybackOption(installationId: "selected-lx", generation: 1, siteId: "wy", sourceKey: musicSourceKey,
            qualities: ["128k"], title: "合成音源", version: "1.0", contentHash: String(repeating: "a", count: 64),
            revisionHash: musicScriptIdentity.revisionHash)
    ], scriptState: .ready)
    var availabilityGate: MusicCollectionGate?
    var metadataEnabled: Set<MusicPublicBrowsePlatform> = [.netease]
    var metadataFailure: MusicServiceError?
    var metadataCandidatesOverride: [MusicMetadataCandidate]?
    var metadataGate: MusicCollectionGate?
    var artworkFallbacks: [URL: URL] = [:]
    var lyricSourceKeys: [String] = []
    var cacheLookups = 0, cacheDiscards = 0, resolveCalls = 0, lyricCalls = 0
    var resultSelection: @Sendable (MusicPlaybackSelection) -> MusicPlaybackSelection = { $0 }
    var resolvedAttempts: [String] = [], cancelledAttempts: [String?] = []
    let cachedFileAuthorization = MusicQueueAuthorization()
    var remoteAuthorization: MusicQueueAuthorization?
    var recordGate: MusicCollectionGate?
    func blockRecord(_ gate: MusicCollectionGate) { recordGate = gate }
    func enableRemoteAuthorization() -> MusicQueueAuthorization {
        let value = MusicQueueAuthorization(); remoteAuthorization = value; return value
    }
    func enableCaches(audio: Bool, lyrics: Bool) { hasCachedAudio = audio; hasLyrics = lyrics }
    func cacheCounts() -> [Int] { [cacheLookups, cacheDiscards, resolveCalls, lyricCalls] }
    func returnSelection(_ selection: MusicPlaybackSelection) { resultSelection = { _ in selection } }
    func resolutionFacts() -> (resolved: [String], cancelled: [String?]) { (resolvedAttempts, cancelledAttempts) }
    func cachedPlayback(attemptID: String, occurrenceID: String,
                        selection: MusicPlaybackSelection?) async throws -> MusicCachedPlayback? {
        cacheLookups += 1
        guard hasCachedAudio, selection == nil || selection == musicOfficialSelection else { return nil }
        let value = MusicPlaybackCandidate(attemptId: attemptID, occurrenceId: occurrenceID,
            resourceId: "music:" + attemptID, url: URL(fileURLWithPath: "/synthetic-cache/complete.mp3"),
            title: "合成歌曲", requestedQuality: "128k", resolvedSelection: selection ?? musicOfficialSelection,
            fileAuthorization: cachedFileAuthorization)
        return .init(candidate: value, selection: selection ?? musicOfficialSelection)
    }
    func discardCachedPlayback(attemptID: String) async { cacheDiscards += 1; hasCachedAudio = false }
    func recoverCachedPlayback(previousAttemptID: String, selection: MusicPlaybackSelection?,
                               position: Double) async throws -> MusicPlaybackRecovery {
        await discardCachedPlayback(attemptID: previousAttemptID)
        let selected = selection ?? musicOfficialSelection
        let candidate = try await resolve(attemptId: UUID().uuidString.lowercased(),
                                          occurrenceId: current, selection: selected)
        return .init(candidate: candidate, selection: selected)
    }
    func revokeCachedFile() { cachedFileAuthorization.revoke() }

    func snapshot() -> MusicPlaybackState {
        MusicPlaybackState(revision: revision, settingsRevision: settingsRevision, mode: mode,
                           officialSources: officialSources,
                           currentId: current, total: entries.count, position: 17,
                           canNext: !entries.isEmpty, canPrevious: !entries.isEmpty, requestQuality: requestQuality)
    }
    func state() async throws -> MusicPlaybackState {
        stateReads += 1
        if let stateBegan {
            return await withCheckedContinuation { value in
                heldState = value
                self.stateBegan = nil
                stateBegan()
            }
        }
        return snapshot()
    }
    func queue() async throws -> MusicQueueSnapshot {
        .init(revision: revision, currentId: current, entries: entries.map {
            .init(id: $0.id, track: $0.track.projected(repositoryRecords), sourceTitle: $0.sourceTitle,
                  remoteSessionID: $0.remoteSessionID, displayIdentity: $0.displayIdentity)
        })
    }
    func platforms() async throws -> [MusicPlatform] { [MusicPlatform(id: "wy", title: "网易云音乐")] }
    func collections() async throws -> [MusicCollection] {
        [.init(id: "favorites", title: "收藏", kind: "favorites", count: Set(entries.map { $0.track.songId }).count)]
    }
    func search(platform: String, query: String, page: Int, limit: Int) async throws -> MusicSearchPage {
        searchReads += 1
        if let searchBegan {
            self.searchBegan = nil
            return await withCheckedContinuation { value in heldSearch = value; searchBegan() }
        }
        if let searchResult { return searchResult }
        return MusicSearchPage(platform: platform, query: query, page: page, limit: limit, total: searchItems.count,
                               nextPage: nil, items: searchItems)
    }
    func searchPublicPlaylists(platform: MusicPublicBrowsePlatform, query: String,
                               page: Int, limit: Int) throws -> MusicPlaylistSearchPage {
        guard let playlistSearchResult else { throw MusicServiceError.unsupported }
        return playlistSearchResult
    }
    func publicPlaylists(platform: MusicPublicBrowsePlatform, query: String,
                         cursor: String?) throws -> MusicPublicBrowseCollectionPage { throw MusicServiceError.unsupported }
    func publicDetail(_ collection: MusicPublicBrowseCollection, cursor: String?) throws -> MusicPublicBrowseDetail {
        throw MusicServiceError.unsupported
    }
    func publicDiscovery(_ destination: MusicPublicBrowseDestination, cursor: String?) throws -> MusicPublicBrowsePage {
        throw MusicServiceError.unsupported
    }
    // Search admission casts the injected owner to MusicAccountBrowsing before
    // reaching MusicServing.search. These account reads are not used here.
    func recommendations(platform: MusicAccountPlatform, kind: String,
                         cursor: String?) async throws -> MusicBrowsePage { throw MusicServiceError.unsupported }
    func accountCollections(platform: MusicAccountPlatform,
                            cursor: String?) async throws -> MusicAccountCollectionPage { throw MusicServiceError.unsupported }
    func accountTracks(platform: MusicAccountPlatform, collectionID: String,
                       cursor: String?) async throws -> MusicBrowsePage { throw MusicServiceError.unsupported }
    func tracks(in collection: MusicCollection) async throws -> [MusicRemoteTrack] {
        var seen = Set<String>()
        return entries.map(\.track).filter { seen.insert($0.songId).inserted }.map {
            let track = $0.projected(repositoryRecords)
            return .init(ref: .init(kind: .library, id: track.songId), songId: track.songId,
                         title: track.title, artist: track.artist, album: track.album, duration: track.duration,
                         sources: track.sources, artwork: track.artwork, artistNames: track.artistNames,
                         sourcePosition: track.sourcePosition)
        }
    }
    func createPlaylist(name: String, refs: [MusicReference]) async throws -> MusicCollection { throw MusicServiceError.unsupported }
    func editPlaylist(_ collection: MusicCollection, name: String?, refs: [MusicReference]?, order: [String]?) async throws -> MusicCollection { throw MusicServiceError.unsupported }
    func removePlaylist(_ collection: MusicCollection) async throws { throw MusicServiceError.unsupported }
    func changeQueue(_ change: MusicQueueChange, expectedRevision: Int) async throws -> MusicPlaybackState {
        if failChange { throw MusicServiceError.storageFailed }
        guard expectedRevision == revision else { throw MusicServiceError.staleRevision }
        switch change {
        case .replace(let refs, let index):
            guard !refs.isEmpty, refs.indices.contains(index) else { throw MusicServiceError.invalidRequest }
            let known = Dictionary(uniqueKeysWithValues: entries.map { ($0.track.ref, $0.track) })
            let tracks = try refs.map { ref -> MusicRemoteTrack in
                guard let track = known[ref] else { throw MusicServiceError.notFound }
                return track
            }
            entries = tracks.enumerated().map { position, track in
                let id = "entry-\(position + 1)"
                return .init(id: id, track: .init(ref: .init(kind: .queue, id: id), songId: track.songId,
                    title: track.title, artist: track.artist, album: track.album, duration: track.duration,
                    sources: track.sources, artwork: track.artwork))
            }
            current = entries[index].id
        case .select(let id):
            guard entries.contains(where: { $0.id == id }) else { throw MusicServiceError.notFound }
            current = id
        case .remove(let id):
            entries.removeAll { $0.id == id }
            if current == id { current = entries.first?.id ?? "" }
        default: throw MusicServiceError.unsupported
        }
        revision += 1; return snapshot()
    }
    func advance(_ direction: MusicDirection, reason: MusicAdvanceReason, expectedRevision: Int) async throws -> MusicAdvanceResult {
        guard revision == expectedRevision else { throw MusicServiceError.staleRevision }
        moves.append((direction, reason))
        let index = entries.firstIndex { $0.id == current } ?? 0
        if reason != .ended || mode != .single {
            current = entries[(index + (direction == .next ? 1 : entries.count - 1)) % entries.count].id
        }
        revision += 1
        return MusicAdvanceResult(state: snapshot(), selectedId: current)
    }
    func updateSettings(_ change: MusicSettingsChange) async throws -> MusicPlaybackState {
        settingsWrites += 1
        let failure = settingsFailure, gate = settingsGate
        settingsFailure = nil; settingsGate = nil
        if failure == nil || failure == .commitUnconfirmed {
            if let value = change.mode { mode = value }
            if let value = change.requestQuality { requestQuality = value }
            if let value = change.officialSources { officialSources = value }
            settingsRevision += 1
        }
        let result = snapshot()
        if let gate { await gate.wait() }
        if let failure { throw failure }
        return result
    }
    func sourceAvailability(for ref: MusicReference) async -> MusicSourceAvailability {
        let value = availability
        if let gate = availabilityGate { availabilityGate = nil; await gate.wait() }
        return value
    }
    func setAvailability(_ value: MusicSourceAvailability, gate: MusicCollectionGate? = nil) {
        availability = value; availabilityGate = gate
    }
    func metadataPreferences() -> MusicMetadataPreferences {
        .init(revision: settingsRevision, platforms: [.netease, .qq], enabledPlatforms: metadataEnabled)
    }
    func updateMetadataPreferences(_ change: MusicMetadataPreferencesChange) throws -> MusicMetadataPreferences {
        guard change.ifRevision == settingsRevision else { throw MusicServiceError.staleRevision }
        if let platforms = change.platforms, platforms != [.netease, .qq] { throw MusicServiceError.invalidRequest }
        if let enabled = change.enabledPlatforms {
            guard enabled.isSubset(of: [.netease, .qq]) else { throw MusicServiceError.invalidRequest }
            metadataEnabled = enabled
        }
        settingsRevision += 1
        return metadataPreferences()
    }
    func metadataCandidates(occurrenceID: String) async throws -> MusicMetadataCandidates {
        if let metadataFailure { throw metadataFailure }
        guard current == occurrenceID, let entry = entries.first(where: { $0.id == occurrenceID }) else {
            throw MusicServiceError.targetChanged
        }
        let track = entry.track.projected(repositoryRecords)
        let candidates = metadataCandidatesOverride ?? track.sources.map {
            MusicMetadataCandidate(source: $0, artwork: track.artwork)
        }
        let result = MusicMetadataCandidates(occurrenceID: occurrenceID, queueRevision: revision, preferences: metadataPreferences(),
            track: track,
            candidates: candidates.filter {
                MusicPublicBrowsePlatform(rawValue: $0.source.platform).map(metadataEnabled.contains) == true
            })
        if let gate = metadataGate { metadataGate = nil; await gate.wait() }
        return result
    }
    func failMetadata(_ error: MusicServiceError) { metadataFailure = error }
    func setArtworkFallback(_ next: URL, for failed: URL) { artworkFallbacks[failed] = next }
    func setMetadataArtwork(_ url: URL?) {
        entries = entries.map { entry in
            let track = entry.track
            return .init(id: entry.id, track: .init(ref: track.ref, songId: track.songId,
                title: track.title, artist: track.artist, album: track.album, duration: track.duration,
                sources: track.sources, artwork: url, artistNames: track.artistNames))
        }
    }
    func failedArtwork(songID: String, url: URL, attempted: Set<URL>) -> URL? {
        let next = artworkFallbacks[url].flatMap { attempted.contains($0) ? nil : $0 }
        setMetadataArtwork(next)
        return next
    }
    func succeededArtwork(songID: String, url: URL, replacing requested: URL) -> URL? {
        setMetadataArtwork(url)
        return url
    }
    func replaceMetadataEntry(_ entry: MusicQueueEntry, candidates: [MusicMetadataCandidate],
                              gate: MusicCollectionGate? = nil) {
        entries = [entry]; current = entry.id
        metadataCandidatesOverride = candidates; metadataGate = gate
        metadataEnabled = [.netease, .qq]
    }
    func addMetadataSource(_ source: MusicNativeSource) {
        entries = entries.map { entry in
            let track = entry.track
            return .init(id: entry.id, track: .init(ref: track.ref, songId: track.songId,
                title: track.title, artist: track.artist, album: track.album, duration: track.duration,
                sources: track.sources + [source], artwork: track.artwork))
        }
    }
    func requestedLyricSources() -> [String] { lyricSourceKeys }
    func inspectSources(for reference: MusicReference) async throws -> any MusicSourceInspectionSession {
        guard entries.contains(where: { $0.track.ref == reference }) else { throw MusicServiceError.notFound }
        return MusicModelInspectionSession(service: self)
    }
    func adoptInspected(_ selection: MusicPlaybackSelection, occurrenceID: String,
                        expectedQueueRevision: Int) throws -> MusicPlaybackSelection {
        guard revision == expectedQueueRevision else { throw MusicServiceError.staleRevision }
        guard entries.contains(where: { $0.id == occurrenceID }) else { throw MusicServiceError.notFound }
        revision += 1
        return selection
    }
    func resolve(attemptId: String, occurrenceId: String, selection: MusicPlaybackSelection) async throws -> MusicPlaybackCandidate {
        resolveCalls += 1; resolvedAttempts.append(attemptId)
        let candidate = MusicPlaybackCandidate(attemptId: attemptId, occurrenceId: occurrenceId, resourceId: "music:" + attemptId,
            url: URL(string: "https://media.invalid/synthetic.mp3")!, headers: headers,
            title: "合成歌曲", requestedQuality: selection.quality, resolvedSelection: resultSelection(selection),
            playbackAuthorization: remoteAuthorization)
        if holdNextResolve {
            holdNextResolve = false
            return await withCheckedContinuation { value in held = (candidate, value); beganResolve?() }
        }
        return candidate
    }
    func cancel(attemptId: String?) async throws { cancelledAttempts.append(attemptId) }
    func record(_ fact: MusicPlaybackFact) async throws -> Bool {
        if let gate = recordGate { recordGate = nil; await gate.wait() }
        facts.append(fact); return true
    }
    func lyrics(occurrenceId: String, sourceKey: String) async throws -> MusicLyricsSnapshot {
        lyricCalls += 1; lyricSourceKeys.append(sourceKey)
        return MusicLyricsSnapshot(occurrenceId: occurrenceId, state: hasLyrics ? .ready : .none,
            lines: hasLyrics ? [.init(index: 0, time: 0, end: 180, text: "合成歌词")] : [])
    }
    func playedSongs() async throws -> [MusicPlayedSong] { historyItems }
    func playedPlaylists() async throws -> [MusicPlayedPlaylist] { [] }
    func restoreHistory(id: String, expectedRevision: Int) async throws -> MusicPlaybackState {
        current = "entry-1"; revision += 1; return snapshot()
    }
    func recordedFacts() -> [MusicPlaybackFact] { facts }
    func recordedMoves() -> [String] { moves.map { $0.0.rawValue + ":" + $0.1.rawValue } }
    func setHeaders(_ value: [String: String]) { headers = value }
    func setFailChange(_ value: Bool) { failChange = value }
    func blockResolve(_ began: @escaping @Sendable () -> Void) { holdNextResolve = true; beganResolve = began }
    func releaseResolve() { if let held { self.held = nil; held.1.resume(returning: held.0) } }
    func blockSearch(_ began: @escaping @Sendable () -> Void) { searchBegan = began }
    func releaseSearch() {
        heldSearch?.resume(returning: MusicSearchPage(platform: "wy", query: "synthetic", page: 1,
            limit: 30, total: 1, nextPage: nil, items: [entries[0].track]))
        heldSearch = nil; searchBegan = nil
    }
    func blockState(_ began: @escaping @Sendable () -> Void) { stateBegan = began }
    func releaseState() {
        heldState?.resume(returning: snapshot())
        heldState = nil
    }
    func stateReadCount() -> Int { stateReads }
    func searchReadCount() -> Int { searchReads }
}

private actor MusicModelAccountSessions: MusicAccountsServing, MusicAccountSessionsObserving {
    private let changes: AsyncStream<MusicAccountPlatform>
    private let continuation: AsyncStream<MusicAccountPlatform>.Continuation
    private let observed: @Sendable () -> Void
    init(observed: @escaping @Sendable () -> Void) {
        let pair = AsyncStream<MusicAccountPlatform>.makeStream()
        changes = pair.stream; continuation = pair.continuation; self.observed = observed
    }
    func accountSessionChanges() -> AsyncStream<MusicAccountPlatform> { observed(); return changes }
    func send(_ platform: MusicAccountPlatform) { continuation.yield(platform) }
    func finish() { continuation.finish() }
    func statuses() -> [MusicAccountStatus] { [] }
    func validate(platform: MusicAccountPlatform) throws -> MusicAccountStatus { throw MusicServiceError.unsupported }
    func beginLogin(platform: MusicAccountPlatform) throws -> MusicLoginChallenge { throw MusicServiceError.unsupported }
    func checkLogin(challengeID: UUID) throws -> MusicLoginProgress { throw MusicServiceError.unsupported }
    func cancelLogin(challengeID: UUID) {}
    func logout(platform: MusicAccountPlatform) throws { throw MusicServiceError.unsupported }
}

private struct MusicMetadataCacheAccounts: MusicAccountsServing, MusicNativeCacheScopeProviding {
    let scope = MusicNativeCacheScope(scope: "metadata-test")
    func nativeCacheScope() -> MusicNativeCacheScope { scope }
    func checkNativeCacheScope(_ scope: MusicNativeCacheScope) throws { try scope.authorization.check() }
    func statuses() -> [MusicAccountStatus] { [] }
    func validate(platform: MusicAccountPlatform) throws -> MusicAccountStatus { throw MusicServiceError.unsupported }
    func beginLogin(platform: MusicAccountPlatform) throws -> MusicLoginChallenge { throw MusicServiceError.unsupported }
    func checkLogin(challengeID: UUID) throws -> MusicLoginProgress { throw MusicServiceError.unsupported }
    func cancelLogin(challengeID: UUID) {}
    func logout(platform: MusicAccountPlatform) throws { throw MusicServiceError.unsupported }
}

@MainActor private final class MusicModelPlayer: MusicPlaybackDriving {
    var onStop: (() -> Void)?
    var resources: [PlaybackResource] = []
    var attempts: [MusicPlayerAttempt] = []
    var stopped: [MusicPlayerAttempt] = []
    var paused: [MusicPlayerAttempt] = []
    var resumed: [MusicPlayerAttempt] = []
    var seeks: [(Double, MusicPlayerAttempt)] = []
    var acceptsSeek = false
    func play(_ resource: PlaybackResource) -> MusicPlayerAttempt {
        let value = MusicPlayerAttempt(id: UUID(), resourceID: resource.id)
        resources.append(resource); attempts.append(value); return value
    }
    func pause(_ attempt: MusicPlayerAttempt) { paused.append(attempt) }
    func resume(_ attempt: MusicPlayerAttempt) { resumed.append(attempt) }
    func seek(_ position: Double, attempt: MusicPlayerAttempt) -> Bool {
        seeks.append((position, attempt)); return acceptsSeek
    }
    func stop(_ attempt: MusicPlayerAttempt) { stopped.append(attempt); onStop?() }
}

/// Management-port facts only; no script execution or second settings writer.
private actor MusicScriptSettingsService: MusicScriptSourceManaging {
    static func item(_ id: String = "script-a", enabled: Bool = false, name: String = "音源", version: String = "1") -> MusicScriptSourceItem {
        .init(id: id, name: name, version: version, origin: .network(host: "scripts.invalid"),
              platforms: [.netease], enabled: enabled, state: enabled ? .ready : .disabled)
    }
    private var values = ["script-a": MusicScriptSettingsService.item(), "script-b": MusicScriptSettingsService.item("script-b")]
    private var gate: MusicCollectionGate?
    private var failure: MusicServiceError?
    private var writes = 0
    private var checkGate: MusicCollectionGate?
    private var checkCancelled: (@Sendable () -> Void)?
    func configure(_ failure: MusicServiceError? = nil, gate: MusicCollectionGate? = nil) {
        self.failure = failure; self.gate = gate
    }
    func blockCheck(_ gate: MusicCollectionGate, cancelled: @escaping @Sendable () -> Void) {
        checkGate = gate; checkCancelled = cancelled
    }
    func writeCount() -> Int { writes }
    func scriptSources() -> [MusicScriptSourceItem] { values.values.sorted { $0.id < $1.id } }
    private func save(_ value: MusicScriptSourceItem?, id: String) async throws {
        writes += 1
        let gate = gate, failure = failure; self.gate = nil; self.failure = nil
        if let failure, failure != .commitUnconfirmed { throw failure }
        values[id] = value
        if let gate { await gate.wait() } // Committed receipt deliberately ignores page cancellation.
        if let failure { throw failure }
    }
    func setScriptSourceEnabled(_ id: String, enabled: Bool) async throws -> MusicScriptSourceItem {
        let value = Self.item(id, enabled: enabled)
        try await save(value, id: id); return value
    }
    func removeScriptSource(_ id: String) async throws { try await save(nil, id: id) }
    func scriptSourceDetail(_ id: String) throws -> MusicScriptSourceDetail {
        guard let value = values[id] else { throw MusicServiceError.notFound }
        return .init(item: value, networkURL: "https://scripts.invalid/source.js", rollbackAvailable: true)
    }
    func editScriptSource(_ id: String, name: String, networkURL: String?) async throws -> MusicScriptSourceItem {
        let value = Self.item(id, enabled: values[id]?.enabled ?? false, name: name)
        try await save(value, id: id); return value
    }
    func updateScriptSource(_ id: String) async throws -> MusicScriptSourceItem {
        let value = Self.item(id, enabled: values[id]?.enabled ?? false, version: "2")
        try await save(value, id: id); return value
    }
    func updateScriptSource(_ id: String, data: Data, fileName: String) async throws -> MusicScriptSourceItem {
        try await updateScriptSource(id)
    }
    func rollbackScriptSource(_ id: String) async throws -> MusicScriptSourceItem {
        let value = Self.item(id, enabled: values[id]?.enabled ?? false, version: "0")
        try await save(value, id: id); return value
    }
    func importScriptSource(_ data: Data, fileName: String) async throws -> MusicScriptSourceItem {
        let value = Self.item("imported", name: fileName)
        try await save(value, id: value.id); return value
    }
    func downloadScriptSource(from url: URL) async throws -> MusicScriptSourceItem {
        try await importScriptSource(Data(), fileName: url.lastPathComponent)
    }
    func checkScriptSource(_ id: String) async throws -> MusicScriptSourceItem {
        let gate = checkGate, cancelled = checkCancelled; checkGate = nil; checkCancelled = nil
        if let gate { await withTaskCancellationHandler { await gate.wait() } onCancel: { cancelled?() } }
        try Task.checkCancellation()
        guard let value = values[id] else { throw MusicServiceError.notFound }
        return value
    }
}

final class MusicViewModelTests: XCTestCase {
    @MainActor func testSubmittedScriptManagementReceiptsSurviveClosedPageAndOnlyMatchingSaveClearsUnknown() async throws {
        let temporary = FileManager.default.temporaryDirectory.appendingPathComponent("music-settings-\(UUID().uuidString).js")
        try Data("current script source".utf8).write(to: temporary)
        defer { try? FileManager.default.removeItem(at: temporary) }
        let download = URL(string: "https://scripts.invalid/current.js")!
        for operation in ["enabled", "metadata", "update", "file-update", "rollback", "download", "import", "remove"] {
            let service = MusicModelService(), scripts = MusicScriptSettingsService()
            let model = MusicViewModel(service: service, player: MusicModelPlayer(), scriptSources: scripts)
            await model.load()
            let page = MusicSourceManagementState(model: model)
            page.start(); await page.flush()
            let before = page.scripts
            let entered = expectation(description: "\(operation) committed"), gate = MusicCollectionGate { entered.fulfill() }
            await scripts.configure(.commitUnconfirmed, gate: gate)
            switch operation {
            case "enabled": page.setScript("script-a", enabled: true)
            case "metadata": page.edit("script-a", name: "新音源", url: nil)
            case "update": page.update("script-a")
            case "file-update": page.update("script-a", file: temporary)
            case "rollback": page.rollback("script-a")
            case "download": page.download(download.absoluteString)
            case "import": page.importFiles([temporary])
            default: page.remove("script-a")
            }
            await fulfillment(of: [entered], timeout: 3)
            page.close()
            await gate.release(); await page.flush(); await model.settingsReceipts.flush()
            let key: String
            switch operation {
            case "enabled": key = "scripts/script-a/enabled"
            case "metadata": key = "scripts/script-a/metadata"
            case "download": key = "scripts/import/" + download.absoluteString
            case "import": key = "scripts/import/" + temporary.absoluteString
            case "remove": key = "scripts/script-a/installation"
            default: key = "scripts/script-a/source"
            }
            XCTAssertEqual(model.settingsReceipts.unknown.map(\.setting), [key])
            XCTAssertNotNil(model.error); XCTAssertNil(page.error)
            XCTAssertEqual(page.scripts, before, "A settled receipt must not refill the closed page")
            var writes = await scripts.writeCount(); XCTAssertEqual(writes, 1, "Unknown writes are not retried")
            let reopened = MusicSourceManagementState(model: model)
            reopened.start(); await reopened.flush()
            let authority = await scripts.scriptSources()
            XCTAssertEqual(reopened.scripts, authority)
            XCTAssertEqual(model.settingsReceipts.unknown.count, 1, "Reopening only reads facts")
            reopened.setScript("script-b", enabled: true); await reopened.flush()
            XCTAssertEqual(model.settingsReceipts.unknown.map(\.setting), [key], "Another installation cannot acknowledge this setting")
            model.notices.error = "另一操作的错误"
            switch operation {
            case "enabled": reopened.setScript("script-a", enabled: false)
            case "metadata": reopened.edit("script-a", name: "再次保存", url: nil)
            case "update", "file-update", "rollback": reopened.update("script-a")
            case "download": reopened.download(download.absoluteString)
            case "import": reopened.importFiles([temporary])
            default:
                // A deleted installation no longer has a page action. Its
                // definitive receipt belongs to the same stable store owner.
                try await model.settingsReceipts.submit(settings: [key], operation: "删除音源") {
                    try await scripts.removeScriptSource("script-a")
                }.value
            }
            await reopened.flush(); await model.settingsReceipts.flush()
            XCTAssertTrue(model.settingsReceipts.unknown.isEmpty)
            XCTAssertEqual(model.notices.error, "另一操作的错误")
            writes = await scripts.writeCount(); XCTAssertEqual(writes, 3)
            reopened.close(); await model.close()
        }
    }

    @MainActor func testScriptHealthCheckStillCancelsOnPageDepartureWithoutCreatingASettingsReceipt() async {
        let scripts = MusicScriptSettingsService()
        let model = MusicViewModel(service: MusicModelService(), player: MusicModelPlayer(), scriptSources: scripts)
        await model.load()
        let page = MusicSourceManagementState(model: model), visible = NSObject()
        page.start(); page.show(visible); await page.flush()
        let entered = expectation(description: "health read entered"), cancelled = expectation(description: "health read cancelled")
        let gate = MusicCollectionGate { entered.fulfill() }
        await scripts.blockCheck(gate, cancelled: { cancelled.fulfill() })
        page.checkScript("script-a")
        await fulfillment(of: [entered], timeout: 2)
        page.hide(visible)
        await fulfillment(of: [cancelled], timeout: 2)
        await gate.release(); await page.flush()
        XCTAssertTrue(model.settingsReceipts.unknown.isEmpty)
        XCTAssertNil(page.error); XCTAssertTrue(page.checking.isEmpty)
        let writes = await scripts.writeCount(); XCTAssertEqual(writes, 0)
        page.close(); await model.close()
    }

    @MainActor func testQualityAndOfficialSubmittedReceiptsOutliveClosedPagesAndOnlyMatchingSuccessClearsUnknown() async {
        for official in [false, true] {
            let (service, _, model) = setup()
            await model.load()
            let quality = MusicQualitySettingsState(model: model), sources = MusicSourceManagementState(model: model)
            quality.start(); sources.start()
            let entered = expectation(description: "setting submitted"), gate = MusicCollectionGate { entered.fulfill() }
            await service.configureSettings(failure: .commitUnconfirmed, gate: gate)
            if official { sources.setOfficial(.qq, enabled: false) }
            else { quality.setRequestQuality("320k") }
            await fulfillment(of: [entered], timeout: 2)
            quality.close(); sources.close()
            let queued = model.queue.map(\.id), revision = model.state.revision, position = model.position
            await gate.release(); await quality.flush(); await sources.flush(); await model.settingsReceipts.flush()
            let key = official ? "playback/officialSources" : "playback/requestQuality"
            XCTAssertEqual(model.settingsReceipts.unknown.map(\.setting), [key])
            XCTAssertNotNil(model.error, "The stable owner's unknown remains globally visible after page close")
            XCTAssertNil(model.notices.error, "It is not replayed as a closed page's ordinary failure")
            XCTAssertEqual(quality.value.requestQuality, "flac"); XCTAssertTrue(sources.official.contains(.qq))
            XCTAssertEqual(model.queue.map(\.id), queued); XCTAssertEqual(model.state.revision, revision)
            XCTAssertEqual(model.position, position)
            var writes = await service.settingsWriteCount(); XCTAssertEqual(writes, 1)
            let reopenedQuality = MusicQualitySettingsState(model: model), reopenedSources = MusicSourceManagementState(model: model)
            reopenedQuality.start(); reopenedSources.start()
            if official { XCTAssertFalse(reopenedSources.official.contains(.qq)) }
            else { XCTAssertEqual(reopenedQuality.value.requestQuality, "320k") }
            await model.updateSettings(.init(ifRevision: model.state.settingsRevision, mode: .single))
            XCTAssertEqual(model.settingsReceipts.unknown.map(\.setting), [key])
            model.notices.error = "另一操作的错误"
            if official { reopenedSources.setOfficial(.qq, enabled: true); await reopenedSources.flush() }
            else { reopenedQuality.setRequestQuality("128k"); await reopenedQuality.flush() }
            XCTAssertTrue(model.settingsReceipts.unknown.isEmpty)
            XCTAssertEqual(model.notices.error, "另一操作的错误")
            writes = await service.settingsWriteCount(); XCTAssertEqual(writes, 3)
            reopenedQuality.close(); reopenedSources.close(); await model.close()
        }
    }

    @MainActor func testReopenedSharedSourcePageReconcilesActualFactsAndReleasesOldWriteBusyState() async {
        for official in [false, true] {
            let service = MusicModelService(), scripts = MusicScriptSettingsService()
            let model = MusicViewModel(service: service, player: MusicModelPlayer(), scriptSources: scripts)
            await model.load()
            let page = MusicSourceManagementState(model: model), first = NSObject(), reopened = NSObject()
            page.start(); page.show(first); await page.flush()
            let entered = expectation(description: "source setting committed")
            let gate = MusicCollectionGate { entered.fulfill() }
            if official {
                await service.configureSettings(failure: .commitUnconfirmed, gate: gate)
                page.setOfficial(.qq, enabled: false)
            } else {
                await scripts.configure(.commitUnconfirmed, gate: gate)
                page.setScript("script-a", enabled: true)
            }
            await fulfillment(of: [entered], timeout: 2)
            page.hide(first); page.show(reopened)
            await gate.release(); await page.flush(); await model.settingsReceipts.flush()
            XCTAssertFalse(page.savingOfficial); XCTAssertTrue(page.pending.isEmpty)
            XCTAssertNil(page.error)
            if official { XCTAssertFalse(page.officialEnabled(.qq)) }
            else { XCTAssertEqual(page.item("script-a")?.enabled, true) }
            let key = official ? "playback/officialSources" : "scripts/script-a/enabled"
            XCTAssertEqual(model.settingsReceipts.unknown.map(\.setting), [key])
            page.close(); await model.close()
        }
    }

    @MainActor func testReopenedQualityPageReconcilesActualFactsAndReleasesOldWriteBusyState() async {
        let (service, _, model) = setup()
        await model.load()
        let page = MusicQualitySettingsState(model: model)
        page.start()
        let entered = expectation(description: "quality setting committed")
        let gate = MusicCollectionGate { entered.fulfill() }
        await service.configureSettings(failure: .commitUnconfirmed, gate: gate)
        page.setRequestQuality("320k"); await fulfillment(of: [entered], timeout: 2)
        page.close(); page.start()
        await gate.release(); await page.flush(); await model.settingsReceipts.flush()
        XCTAssertFalse(page.busy); XCTAssertEqual(page.value.requestQuality, "320k")
        XCTAssertEqual(model.settingsReceipts.unknown.map(\.setting), ["playback/requestQuality"])
        page.setRequestQuality("128k"); await page.flush()
        XCTAssertFalse(page.busy); XCTAssertEqual(page.value.requestQuality, "128k")
        XCTAssertTrue(model.settingsReceipts.unknown.isEmpty)
        let writes = await service.settingsWriteCount(); XCTAssertEqual(writes, 2)
        page.close(); await model.close()
    }

    @MainActor func testRepositoryChangesReachPlaybackSearchAndCollectionWithoutQueueOrMediaMutation() async throws {
        let (service, player, model) = setup()
        await model.load(); await model.choose(musicSelection); await model.playbackMetadata.flush()
        let initial = try XCTUnwrap(model.current?.track)
        await service.setHistoryItems([.init(date: "2026-11-14", at: 10, track: initial)])
        await model.loadHistory()
        await service.setSearchItems([initial])
        let search = MusicSearchResultsModel(owner: model)
        search.setAccess(available: true, generation: model.accountAccessGeneration); search.appear()
        search.submit("合成歌曲", preferences: .init(songPlatforms: [.netease], searchPlaylists: false))
        await search.finishPendingReads()
        let collection = MusicCollectionViewModel(model: model, collectionID: "favorites")
        await collection.appear()?.value
        XCTAssertNil(collection.error); XCTAssertFalse(collection.isMissing); XCTAssertFalse(collection.isLoading)
        XCTAssertEqual(collection.collection?.count, 1)
        XCTAssertEqual(collection.tracks.map(\.ref), [.init(kind: .library, id: initial.songId)])
        XCTAssertNotNil(UUID(uuidString: initial.songId))
        let queueIDs = model.queue.map(\.id), queueRefs = model.queue.map(\.track.ref)
        let collectionRefs = collection.tracks.map(\.ref), searchIDs = search.songs.map(\.id)
        let revision = model.state.revision, position = model.position, current = model.current?.id
        let attempt = model.activeNativeAttemptID, resources = player.resources.count
        let stateReads = await service.stateReadCount(), searchReads = await service.searchReadCount()
        let cacheCounts = await service.cacheCounts()
        let cover = URL(string: "https://artwork.invalid/current")!
        let currentTrack = MusicRemoteTrack(ref: .init(kind: .library, id: initial.songId), songId: initial.songId,
            title: "当前完整标题 (Live)", artist: "当前歌手", album: "当前专辑", duration: 181.5,
            sources: initial.sources + [.init(sourceKey: "qq:new", platform: "tx", nativeId: "new")],
            artwork: cover, artistNames: ["当前歌手"])
        let played = expectation(description: "player projection updated")
        let listed = expectation(description: "collection projection updated")
        let found = expectation(description: "search projection updated")
        let observations = [
            model.playbackState.$queue.filter { $0.first?.track.title == currentTrack.title }.prefix(1).sink { _ in played.fulfill() },
            collection.$tracks.filter { $0.first?.title == currentTrack.title }.prefix(1).sink { _ in listed.fulfill() },
            search.$songs.filter { $0.first?.track.title == currentTrack.title }.prefix(1).sink { _ in found.fulfill() }
        ]
        await service.publishRepository([currentTrack])
        await fulfillment(of: [played, listed, found], timeout: 2)
        observations.forEach { $0.cancel() }
        XCTAssertEqual(model.queue.map(\.id), queueIDs); XCTAssertEqual(Set(queueIDs).count, 3)
        XCTAssertEqual(model.queue.map(\.track.songId), Array(repeating: initial.songId, count: 3))
        XCTAssertEqual(model.queue.map(\.track.ref), queueRefs)
        XCTAssertEqual(collection.tracks.map(\.ref), collectionRefs); XCTAssertEqual(search.songs.map(\.id), searchIDs)
        XCTAssertEqual(model.state.revision, revision); XCTAssertEqual(model.current?.id, current)
        XCTAssertEqual(model.position, position); XCTAssertEqual(model.activeNativeAttemptID, attempt)
        XCTAssertEqual(player.resources.count, resources); XCTAssertEqual(model.metadataState.track?.title, currentTrack.title)
        XCTAssertEqual(model.metadataArtworkURL, cover)
        XCTAssertEqual(model.playedSongs.first?.track.title, currentTrack.title)
        XCTAssertEqual(model.playedSongs.first?.track.songId, initial.songId)
        XCTAssertEqual(model.playedSongs.first?.at, 10)
        let afterStateReads = await service.stateReadCount(), afterSearchReads = await service.searchReadCount()
        let afterCacheCounts = await service.cacheCounts()
        XCTAssertEqual(afterStateReads, stateReads); XCTAssertEqual(afterSearchReads, searchReads)
        XCTAssertEqual(afterCacheCounts, cacheCounts, "Projection must not fetch audio, candidates or lyrics")
        collection.disappear(); search.disappear(); await model.close()
    }

    @MainActor func testRepositoryReadFromRevokedAccessCannotRefillQueueOrMetadata() async {
        let (service, _, model) = setup()
        await model.load(); await model.playbackMetadata.flush()
        let entered = expectation(description: "repository display read delayed")
        let gate = MusicCollectionGate { entered.fulfill() }
        await service.blockRepository(gate)
        let changed = MusicRemoteTrack(ref: .init(kind: .library, id: MusicModelService.songID),
                                       songId: MusicModelService.songID, title: "迟到资料")
        await service.publishRepository([changed])
        await fulfillment(of: [entered], timeout: 2)
        let pending = model.songRepositoryObservation
        model.setAvailable(false)
        await gate.release(); await pending?.value
        XCTAssertTrue(model.queue.isEmpty); XCTAssertNil(model.metadataState.track); XCTAssertNil(model.metadataArtworkURL)
        await model.close()
    }

    @MainActor func testLateRepositoryProjectionCannotOverwriteNewerFactsWithinTheSameAccess() async throws {
        let (service, _, model) = setup()
        await model.load(); await model.playbackMetadata.flush()
        let initial = try XCTUnwrap(model.current?.track)
        func record(_ title: String) -> MusicRemoteTrack {
            .init(ref: .init(kind: .library, id: initial.songId), songId: initial.songId,
                  title: title, sources: initial.sources)
        }
        let old = record("Earlier facts"), latest = record("Current facts")
        let firstUpdate = expectation(description: "repository observation ready")
        let firstObservation = model.playbackState.$queue
            .filter { $0.first?.track.title == old.title }.prefix(1).sink { _ in firstUpdate.fulfill() }
        await service.publishRepository([old])
        await fulfillment(of: [firstUpdate], timeout: 2)
        firstObservation.cancel()
        let entered = expectation(description: "reload captured earlier repository facts")
        let gate = MusicCollectionGate { entered.fulfill() }
        await service.blockRepository(gate)
        let reload = Task { await model.load() }
        await fulfillment(of: [entered], timeout: 2)
        let changed = expectation(description: "newer repository facts displayed")
        let observation = model.playbackState.$queue
            .filter { $0.first?.track.title == latest.title }.prefix(1).sink { _ in changed.fulfill() }
        await service.publishRepository([latest])
        await fulfillment(of: [changed], timeout: 2)
        observation.cancel()
        var emittedTitles: [String] = []
        let lateChanges = model.playbackState.$queue.sink { emittedTitles.append($0.first?.track.title ?? "") }
        await gate.release(); await reload.value; await model.playbackMetadata.flush()
        XCTAssertEqual(model.current?.track.title, latest.title)
        XCTAssertEqual(model.displayTrack?.title, latest.title)
        XCTAssertFalse(emittedTitles.contains(old.title))
        XCTAssertEqual(model.queue.map(\.id), ["entry-1", "entry-2", "entry-3"])
        XCTAssertEqual(model.state.revision, 1)
        lateChanges.cancel(); await model.close()
    }

    @MainActor func testPinnedResolutionRejectsMismatchedResultSelectionBeforePlayingAndCancelsTheExactAttempt() async throws {
        let selections: [MusicPlaybackSelection] = [
            .init(installationId: "different-lx", generation: 1, siteId: "wy", sourceKey: musicSourceKey,
                quality: "128k", scriptIdentity: musicScriptIdentity),
            .init(installationId: "selected-lx", generation: 1, siteId: "tx", sourceKey: musicSourceKey,
                quality: "128k", scriptIdentity: musicScriptIdentity),
            .init(installationId: "selected-lx", generation: 1, siteId: "wy", sourceKey: "another-source",
                quality: "128k", scriptIdentity: musicScriptIdentity),
            .init(installationId: "selected-lx", generation: 1, siteId: "wy", sourceKey: musicSourceKey,
                quality: "flac", scriptIdentity: musicScriptIdentity),
            .init(installationId: "selected-lx", generation: 1, siteId: "wy", sourceKey: musicSourceKey, quality: "128k"),
            .init(installationId: "selected-lx", generation: 2, siteId: "wy", sourceKey: musicSourceKey,
                quality: "128k", scriptIdentity: musicScriptIdentity),
        ]
        for actual in selections {
            let (service, player, model) = setup()
            await service.returnSelection(actual)
            await model.load(); await model.choose(musicSelection)
            XCTAssertNotNil(model.error)
            XCTAssertTrue(player.resources.isEmpty)
            XCTAssertNil(model.activePlaybackSelection); XCTAssertNil(model.candidate)
            let facts = await service.resolutionFacts()
            XCTAssertEqual(facts.resolved.count, 1)
            XCTAssertEqual(facts.cancelled, facts.resolved.map { Optional($0) })
            await model.close()
        }
    }

    @MainActor func testMetadataPreferenceRefreshCoalescesBehindOnePhysicalRead() async {
        let (service, _, model) = setup()
        let entered = expectation(description: "metadata state read entered")
        await service.blockState { entered.fulfill() }

        model.scheduleMetadataPreferencesRefresh()
        model.scheduleMetadataPreferencesRefresh()
        await fulfillment(of: [entered], timeout: 2)
        model.scheduleMetadataPreferencesRefresh()
        let readsWhileBlocked = await service.stateReadCount()
        XCTAssertEqual(readsWhileBlocked, 1)

        await service.releaseState()
        await model.flushMetadataPreferencesRefresh()
        let completedReads = await service.stateReadCount()
        XCTAssertEqual(completedReads, 2)
        await model.close()
    }

    @MainActor func testMainActorReleaseCancelsOwnedTasks() async {
        let gate = MusicCollectionGate {}
        var model: MusicViewModel? = MusicViewModel(service: MusicModelService(), player: MusicModelPlayer())
        let task = Task { await gate.wait() }
        model?.eventTail = task

        model = nil

        XCTAssertTrue(task.isCancelled)
        await gate.release()
        await task.value
    }

    @MainActor func testAdoptedInspectedSourceStillChecksCompleteFileBeforeNetworkResolve() async throws {
        let (service, player, model) = setup()
        await service.enableCaches(audio: true, lyrics: false)
        await model.load()
        let current = try XCTUnwrap(model.current)
        let session = try await model.inspectSources(for: current.track.ref)
        let source = MusicResolvedSource(id: "inspected", selection: musicOfficialSelection,
                                         title: "合成音源", delivery: nil)

        await model.chooseInspectedSource(source, session: session)

        let counts = await service.cacheCounts()
        XCTAssertEqual(counts[0], 1)
        XCTAssertEqual(counts[2], 0, "adoption is not permission to bypass complete local bytes")
        XCTAssertEqual(player.resources.count, 1)
        XCTAssertTrue(try XCTUnwrap(player.resources.first).url.isFileURL)
        await session.close()
        await model.close()
    }

    @MainActor func testExplicitSourceReplacementKeepsLivePositionAndPausedIntent() async throws {
        let (_, player, model) = setup()
        player.acceptsSeek = true
        await model.load()
        await model.choose(musicOfficialSelection)
        let paused = try fact(model, .paused, position: 42)
        model.onPlayback(paused)
        await model.flushPlaybackFacts()
        XCTAssertEqual(model.phase, .paused)

        await model.choose(musicOfficialSelection)

        XCTAssertEqual(player.resources.count, 2)
        XCTAssertEqual(player.seeks.last?.0, 42)
        XCTAssertEqual(player.seeks.last?.1, player.attempts.last)
        XCTAssertEqual(player.paused.last, player.attempts.last)
        await model.close()
    }

    @MainActor func testSeekPropagatesDriverAcceptanceWithoutReportingCompletionOrResuming() async throws {
        let (_, player, model) = setup()
        await model.load(); await model.choose(musicOfficialSelection)
        let attempt = try XCTUnwrap(player.attempts.last)
        let previous = model.position
        for invalid in [-1.0, .nan, .infinity] { XCTAssertFalse(model.seek(to: invalid)) }
        XCTAssertTrue(player.seeks.isEmpty)
        XCTAssertFalse(model.seek(to: 12))
        XCTAssertEqual(player.seeks.count, 1)
        XCTAssertEqual(player.seeks[0].1.id, attempt.id)
        XCTAssertTrue(player.resumed.isEmpty)
        player.acceptsSeek = true
        XCTAssertTrue(model.seek(to: 14))
        XCTAssertEqual(player.seeks.count, 2)
        XCTAssertEqual(model.position, previous, "Accepted is not a fabricated completed-position event")
        XCTAssertTrue(player.resumed.isEmpty, "Resume remains an explicit caller intent")
        await model.close()
        XCTAssertFalse(model.seek(to: 20))
        XCTAssertEqual(player.seeks.count, 2)
    }

    @MainActor func testRevokedAuthorizationCannotDispatchSeekEvenWhenTheDriverWouldAccept() async throws {
        let (service, player, model) = setup()
        let authorization = await service.enableRemoteAuthorization()
        await model.load(); await model.choose(musicOfficialSelection)
        player.acceptsSeek = true
        authorization.revoke()
        XCTAssertFalse(model.seek(to: 12))
        XCTAssertTrue(player.seeks.isEmpty)
        XCTAssertTrue(player.resumed.isEmpty)
        await model.close()
    }

    @MainActor func testTerminalFactCannotSeekTheRetiringAttemptBeforeAutomaticNavigationRuns() async throws {
        let (_, player, model) = setup()
        await model.load(); await model.choose(musicOfficialSelection)
        let resource = try XCTUnwrap(model.activeResourceID), occurrence = try XCTUnwrap(model.activeOccurrenceID)
        player.acceptsSeek = true
        model.onPlayback(.init(attemptId: String(resource.dropFirst("music:".count)),
            occurrenceId: occurrence, resourceId: resource, kind: .ended, position: 180))
        XCTAssertFalse(model.seek(to: 12))
        XCTAssertTrue(player.seeks.isEmpty)
        XCTAssertTrue(player.resumed.isEmpty)
        await model.close()
    }

    @MainActor func testRevokedRemoteFactIsRejectedSynchronouslyBeforeObserverRuns() async throws {
        let (service, player, model) = setup()
        let authorization = await service.enableRemoteAuthorization()
        await model.load(); await model.choose(musicOfficialSelection)
        let resource = try XCTUnwrap(model.activeResourceID), occurrence = try XCTUnwrap(model.activeOccurrenceID)
        authorization.revoke()
        model.onPlayback(.init(attemptId: String(resource.dropFirst("music:".count)),
            occurrenceId: occurrence, resourceId: resource, kind: .playing, position: 1))
        XCTAssertNil(model.activeResourceID)
        XCTAssertEqual(player.stopped.count, 1)
        await model.flushPlaybackFacts()
        let facts = await service.recordedFacts()
        XCTAssertTrue(facts.isEmpty)
        await model.close()
    }

    @MainActor func testPauseCancelsBadFileRecoveryWithoutStartingAnotherResolution() async throws {
        let (service, player, model) = setup()
        await service.enableCaches(audio: true, lyrics: false)
        await model.load(); await model.choose(musicOfficialSelection)
        let resource = try XCTUnwrap(model.activeResourceID), occurrence = try XCTUnwrap(model.activeOccurrenceID)
        let started = expectation(description: "Network recovery suspended")
        await service.blockResolve { started.fulfill() }
        model.onPlayback(.init(attemptId: String(resource.dropFirst("music:".count)),
            occurrenceId: occurrence, resourceId: resource, kind: .failed, position: 23))
        await fulfillment(of: [started], timeout: 2)
        await model.togglePlayback()
        XCTAssertEqual(model.phase, .paused); XCTAssertNil(model.activeResourceID)
        await service.releaseResolve()
        await model.flushPlaybackFacts()
        XCTAssertEqual(player.resources.count, 1, "Late recovery cannot restart the paused song")
        let counts = await service.cacheCounts()
        XCTAssertEqual(counts[2], 1, "Pause is not a second prepareCurrent")
        await model.close()
    }

    @MainActor func testAccountRevocationStillStopsExactRemoteAttemptAfterProtectedUIClearsCandidate() async throws {
        let (service, player, model) = setup()
        let authorization = await service.enableRemoteAuthorization()
        await model.load(); await model.choose(musicOfficialSelection)
        let native = try XCTUnwrap(player.attempts.first)
        model.setAvailable(false)
        XCTAssertTrue(player.stopped.isEmpty, "Protection alone preserves admitted remote media")
        let revoked = expectation(description: "Account revocation stops retained remote attempt")
        player.onStop = { revoked.fulfill() }
        authorization.revoke()
        await fulfillment(of: [revoked], timeout: 2)
        XCTAssertEqual(player.stopped, [native])
        player.onStop = nil
        await model.close()
    }

    @MainActor func testCompleteCacheIsConsumedBeforePinnedPlatformResolutionWithoutSyntheticPlaying() async throws {
        let (service, player, model) = setup()
        XCTAssertNil(model.activeAudioIsCached)
        await service.enableCaches(audio: true, lyrics: false)
        await model.load(); await model.choose(musicOfficialSelection)
        XCTAssertEqual(model.activeAudioIsCached, true)
        XCTAssertEqual(model.activePlaybackSelection, musicOfficialSelection)
        XCTAssertEqual(model.candidate?.resolvedSelection, musicOfficialSelection)
        let counts = await service.cacheCounts(), facts = await service.recordedFacts()
        XCTAssertEqual(counts[0], 1); XCTAssertEqual(counts[2], 0)
        XCTAssertEqual(player.resources.count, 1); XCTAssertTrue(player.resources[0].url.isFileURL)
        XCTAssertTrue(facts.isEmpty, "cache lookup is not a real playing fact")
        await model.close()
        XCTAssertNil(model.activeAudioIsCached)
    }

    @MainActor func testFailedCompleteCacheEvictsOnlyItAndResolvesOriginalPinnedRouteOnce() async throws {
        let (service, player, model) = setup()
        await service.enableCaches(audio: true, lyrics: false)
        await model.load(); await model.choose(musicOfficialSelection)
        let resource = try XCTUnwrap(model.activeResourceID), occurrence = try XCTUnwrap(model.activeOccurrenceID)
        let failed = MusicPlaybackFact(attemptId: String(resource.dropFirst("music:".count)),
            occurrenceId: occurrence, resourceId: resource, kind: .failed, position: 23)
        model.onPlayback(failed); await model.flushPlaybackFacts()
        model.onPlayback(failed); await model.flushPlaybackFacts()
        let counts = await service.cacheCounts(), facts = await service.recordedFacts()
        XCTAssertEqual(counts[0], 1); XCTAssertEqual(counts[1], 1); XCTAssertEqual(counts[2], 1)
        XCTAssertEqual(model.current?.id, occurrence)
        XCTAssertEqual(player.resources.count, 2); XCTAssertFalse(player.resources[1].url.isFileURL)
        XCTAssertEqual(model.activeAudioIsCached, false)
        XCTAssertEqual(model.activePlaybackSelection, musicOfficialSelection)
        XCTAssertEqual(model.candidate?.resolvedSelection, musicOfficialSelection)
        XCTAssertFalse(facts.contains { $0.kind == .failed }, "a bad cache file is not proof the song failed")
        await model.close()
    }

    @MainActor func testCacheRevocationStopsOwnedLocalFileBeforeResumeAndProtectionNeverTreatsItAsRemote() async throws {
        let (service, player, model) = setup()
        await service.enableCaches(audio: true, lyrics: false)
        await model.load(); await model.choose(musicOfficialSelection)
        await service.revokeCachedFile()
        await model.togglePlayback()
        XCTAssertNil(model.activeResourceID); XCTAssertEqual(player.stopped.count, 1)
        XCTAssertNil(model.activeAudioIsCached)
        model.setAvailable(false)
        XCTAssertEqual(player.resources.count, 1); XCTAssertEqual(player.stopped.count, 1)
        await model.close()
    }

    @MainActor func testOrdinaryLyricsReuseStableRecordingAcrossOccurrencesAndClearOnProtection() async {
        let (service, _, model) = setup()
        await service.enableCaches(audio: false, lyrics: true)
        await model.load(); await model.choose(musicOfficialSelection)
        await model.loadLyrics(occurrenceId: "entry-1")
        XCTAssertEqual(model.lyricLines.first?.text, "合成歌词")
        await model.selectOccurrence("entry-2"); await model.choose(musicOfficialSelection)
        await model.loadLyrics(occurrenceId: "entry-2")
        let before = await service.cacheCounts()
        XCTAssertEqual(before[3], 1, "occurrence changes must not become lyric content keys")
        model.setAvailable(false); model.setAvailable(true)
        await model.load(); await model.choose(musicOfficialSelection)
        await model.loadLyrics(occurrenceId: "entry-2")
        let after = await service.cacheCounts()
        XCTAssertEqual(after[3], 2)
        await model.close()
    }

    @MainActor func testLyricsUseMetadataMappingAcrossAudioSourceAndQualityChanges() async throws {
        let (service, player, model) = setup()
        let source = MusicNativeSource(sourceKey: "qq-metadata", platform: "tx", nativeId: "metadata")
        await service.addMetadataSource(source)
        await service.enableCaches(audio: false, lyrics: true)
        _ = try await service.updateMetadataPreferences(.init(ifRevision: 0, enabledPlatforms: [.qq]))
        await model.load(); await model.choose(musicOfficialSelection)
        await model.loadLyrics(occurrenceId: "entry-1")
        XCTAssertEqual(model.lyricLines.first?.text, "合成歌词")
        let differentAudio = MusicPlaybackSelection(installationId: musicSelection.installationId,
            generation: musicSelection.generation, siteId: musicSelection.siteId,
            sourceKey: musicSelection.sourceKey, quality: "flac", scriptIdentity: musicSelection.scriptIdentity)
        await model.choose(differentAudio)
        await model.loadLyrics(occurrenceId: "entry-1")
        let requested = await service.requestedLyricSources()
        XCTAssertEqual(requested, [source.sourceKey])
        XCTAssertEqual(player.resources.count, 2)
        XCTAssertEqual(model.lyricLines.first?.text, "合成歌词")
        await model.close()
    }

    @MainActor func testDisabledMetadataDoesNotDeriveCandidatesFromAudioSelection() async throws {
        let (service, player, model) = setup()
        await service.enableCaches(audio: false, lyrics: true)
        _ = try await service.updateMetadataPreferences(.init(ifRevision: 0, enabledPlatforms: []))
        await model.load(); await model.choose(musicOfficialSelection)
        await model.loadLyrics(occurrenceId: "entry-1")
        let requested = await service.requestedLyricSources()
        XCTAssertTrue(requested.isEmpty)
        XCTAssertEqual(model.lyricState, "none"); XCTAssertTrue(model.lyricLines.isEmpty)
        XCTAssertEqual(player.resources.count, 1)
        await model.close()
    }

    @MainActor func testMetadataReadFailureDoesNotDeriveCandidatesFromAudioSelection() async {
        let (service, player, model) = setup()
        await service.enableCaches(audio: false, lyrics: true)
        await service.failMetadata(.upstreamFailed)
        await model.load(); await model.choose(musicOfficialSelection)
        await model.loadLyrics(occurrenceId: "entry-1")
        let requested = await service.requestedLyricSources()
        XCTAssertTrue(requested.isEmpty)
        XCTAssertEqual(model.lyricState, "none"); XCTAssertTrue(model.lyricLines.isEmpty)
        XCTAssertEqual(player.resources.count, 1)
        await model.close()
    }

    @MainActor func testArtworkRetainsTheEntryCoverWhileCandidatesLoadAndGainHigherPrioritySources() async {
        let service = MusicModelService(), metadata = MusicPlaybackMetadata()
        let original = URL(string: "https://artwork.invalid/original.png")!
        let preferred = URL(string: "https://artwork.invalid/preferred.png")!
        let originalSource = MusicNativeSource(sourceKey: "qq-cover", platform: "tx", nativeId: "cover")
        let extra = MusicNativeSource(sourceKey: "wy-cover", platform: "wy", nativeId: "cover")
        func entry(_ sources: [MusicNativeSource]) -> MusicQueueEntry {
            .init(id: "entry", track: .init(ref: .init(kind: .queue, id: "entry"), songId: "song",
                title: "Song", sources: sources, artwork: original))
        }
        var covers: [URL?] = []
        metadata.configure(service: service, accounts: nil) { covers.append($0.artwork) }
        metadata.setAccess(available: true, generation: UUID())
        covers.removeAll()
        let started = expectation(description: "metadata candidates held")
        let gate = MusicCollectionGate { started.fulfill() }
        let first = entry([originalSource])
        await service.replaceMetadataEntry(first, candidates: [.init(source: originalSource, artwork: original)], gate: gate)
        metadata.select(first, lyricsEnabled: false)
        XCTAssertEqual(covers, [original], "The entry cover must be published before a metadata read finishes")
        await fulfillment(of: [started], timeout: 2)
        await gate.release(); await metadata.flush()
        let enriched = entry([extra, originalSource])
        await service.replaceMetadataEntry(enriched, candidates: [
            .init(source: extra, artwork: preferred), .init(source: originalSource, artwork: original)])
        metadata.select(enriched, lyricsEnabled: false)
        await metadata.flush()
        XCTAssertTrue(covers.allSatisfy { $0 == original }, "Candidate discovery must not clear or replace an existing cover")
        await metadata.close()
    }

    @MainActor func testRepositoryProjectionWinsOverOlderMetadataReadEvenWhenArtworkMatchesInitialEntry() async {
        let service = MusicModelService(), metadata = MusicPlaybackMetadata()
        let currentURL = URL(string: "https://artwork.invalid/current.png")!
        let oldURL = URL(string: "https://artwork.invalid/old.png")!
        let source = MusicNativeSource(sourceKey: "wy-cover", platform: "wy", nativeId: "cover")
        let entry = MusicQueueEntry(id: "entry", track: .init(ref: .init(kind: .queue, id: "entry"),
            songId: "song", title: "Current title", sources: [source], artwork: currentURL))
        let old = MusicQueueEntry(id: entry.id, track: .init(ref: entry.track.ref, songId: entry.track.songId,
            title: "Older title", sources: [source], artwork: oldURL))
        let started = expectation(description: "older metadata read held")
        let gate = MusicCollectionGate { started.fulfill() }
        await service.replaceMetadataEntry(old, candidates: [.init(source: source, artwork: oldURL)], gate: gate)
        var displayed: MusicRemoteTrack?, cover: URL?
        metadata.configure(service: service, accounts: nil) { displayed = $0.track; cover = $0.artwork }
        metadata.setAccess(available: true, generation: UUID())
        metadata.select(entry, lyricsEnabled: false)
        await fulfillment(of: [started], timeout: 2)
        metadata.project(entry.track)
        await gate.release(); await metadata.flush()
        XCTAssertEqual(displayed, entry.track)
        XCTAssertEqual(cover, currentURL, "A late read cannot replace the repository's latest adopted resource")
        await metadata.close()
    }

    @MainActor func testArtworkFailureBeforeMetadataLoadSkipsDuplicateURLsAndLateFailures() async {
        let service = MusicModelService(), metadata = MusicPlaybackMetadata()
        let failed = URL(string: "https://artwork.invalid/failed.png")!
        let fallback = URL(string: "https://artwork.invalid/fallback.png")!
        let sources = (0..<3).map { MusicNativeSource(sourceKey: "wy-\($0)", platform: "wy", nativeId: "\($0)") }
        let entry = MusicQueueEntry(id: "entry", track: .init(ref: .init(kind: .queue, id: "entry"),
            songId: "song", title: "Song", sources: sources, artwork: failed))
        let started = expectation(description: "metadata candidates held")
        let gate = MusicCollectionGate { started.fulfill() }
        await service.replaceMetadataEntry(entry, candidates: [
            .init(source: sources[0], artwork: failed), .init(source: sources[1], artwork: failed),
            .init(source: sources[2], artwork: fallback)], gate: gate)
        await service.setArtworkFallback(fallback, for: failed)
        var cover: URL?
        metadata.configure(service: service, accounts: nil) { cover = $0.artwork }
        metadata.setAccess(available: true, generation: UUID())
        metadata.select(entry, lyricsEnabled: false)
        await fulfillment(of: [started], timeout: 2)
        metadata.artworkFailed(failed)
        await gate.release(); await metadata.flush()
        XCTAssertEqual(cover, fallback)
        metadata.artworkFailed(failed)
        XCTAssertEqual(cover, fallback, "A late failure for the previous URL cannot advance the current cover")
        metadata.artworkFailed(fallback)
        await metadata.flush()
        XCTAssertNil(cover, "All failed URLs must settle instead of cycling")
        await metadata.close()
    }

    @MainActor func testArtworkReferenceSurvivesAdmissionAndRestoreWithoutPlayerOwnedBytes() async {
        let service = MusicModelService(), metadata = MusicPlaybackMetadata()
        let url = URL(string: "https://artwork.invalid/cached.png")!
        let source = MusicNativeSource(sourceKey: "wy-cover", platform: "wy", nativeId: "cover")
        let entry = MusicQueueEntry(id: "entry", track: .init(ref: .init(kind: .queue, id: "entry"),
            songId: "song", title: "Song", sources: [source], artwork: url))
        let started = expectation(description: "metadata candidates held")
        let gate = MusicCollectionGate { started.fulfill() }
        var artwork: URL?
        metadata.configure(service: service, accounts: MusicMetadataCacheAccounts()) { artwork = $0.artwork }
        metadata.setAccess(available: true, generation: UUID())
        await service.replaceMetadataEntry(entry, candidates: [.init(source: source, artwork: url)], gate: gate)
        metadata.select(entry, lyricsEnabled: false)
        await fulfillment(of: [started], timeout: 2)
        XCTAssertEqual(artwork, url)
        await gate.release(); await metadata.flush()
        XCTAssertEqual(artwork, url)
        metadata.clearSelection()
        metadata.select(entry, lyricsEnabled: false, restoreOnly: true)
        await metadata.flush()
        XCTAssertEqual(artwork, url, "Restoring playback keeps a resource reference for the shared loader")
        await metadata.close()
    }

    @MainActor func testMissingArtworkUsesCandidatesAndExplicitPolicyChangesReplaceTheCover() async throws {
        let service = MusicModelService(), metadata = MusicPlaybackMetadata()
        let first = URL(string: "https://artwork.invalid/netease.png")!
        let second = URL(string: "https://artwork.invalid/qq.png")!
        let sources = [MusicNativeSource(sourceKey: "wy-cover", platform: "wy", nativeId: "cover"),
                       MusicNativeSource(sourceKey: "qq-cover", platform: "tx", nativeId: "cover")]
        let entry = MusicQueueEntry(id: "entry", track: .init(ref: .init(kind: .queue, id: "entry"),
            songId: "song", title: "Song", sources: sources))
        await service.replaceMetadataEntry(entry, candidates: [
            .init(source: sources[0], artwork: first), .init(source: sources[1], artwork: second)])
        await service.setMetadataArtwork(first)
        var cover: URL?
        metadata.configure(service: service, accounts: nil) { cover = $0.artwork }
        metadata.setAccess(available: true, generation: UUID())
        metadata.select(entry, lyricsEnabled: false); await metadata.flush()
        XCTAssertEqual(cover, first)
        _ = try await service.updateMetadataPreferences(.init(ifRevision: 0, enabledPlatforms: [.qq]))
        await service.setMetadataArtwork(second)
        metadata.select(entry, lyricsEnabled: false, force: true); await metadata.flush()
        XCTAssertEqual(cover, second)
        _ = try await service.updateMetadataPreferences(.init(ifRevision: 1, enabledPlatforms: []))
        await service.setMetadataArtwork(nil)
        metadata.select(entry, lyricsEnabled: false, force: true); await metadata.flush()
        XCTAssertNil(cover, "Explicitly disabled metadata cannot be recovered from the previous display")
        await metadata.close()
    }

    @MainActor func testArtworkSelectionRejectsOldMetadataAfterTrackChangeAndAccessRevocation() async {
        let service = MusicModelService(), metadata = MusicPlaybackMetadata()
        let source = MusicNativeSource(sourceKey: "wy-cover", platform: "wy", nativeId: "cover")
        func entry(_ id: String) -> MusicQueueEntry {
            .init(id: id, track: .init(ref: .init(kind: .queue, id: id), songId: id, title: id,
                sources: [source], artwork: URL(string: "https://artwork.invalid/\(id).png")))
        }
        let old = entry("old"), next = entry("next")
        let started = expectation(description: "old metadata held")
        let gate = MusicCollectionGate { started.fulfill() }
        var cover: URL?, occurrence: String?
        metadata.configure(service: service, accounts: nil) { cover = $0.artwork; occurrence = $0.occurrenceID }
        metadata.setAccess(available: true, generation: UUID())
        await service.replaceMetadataEntry(old, candidates: [.init(source: source, artwork: old.track.artwork)], gate: gate)
        metadata.select(old, lyricsEnabled: false)
        let oldRead = Task { await metadata.flush() }
        await fulfillment(of: [started], timeout: 2)
        await service.replaceMetadataEntry(next, candidates: [.init(source: source, artwork: next.track.artwork)])
        metadata.select(next, lyricsEnabled: false); await metadata.flush()
        XCTAssertEqual(cover, next.track.artwork); XCTAssertEqual(occurrence, next.id)
        await gate.release(); await oldRead.value
        XCTAssertEqual(cover, next.track.artwork); XCTAssertEqual(occurrence, next.id)
        metadata.setAccess(available: false, generation: UUID())
        XCTAssertNil(cover); XCTAssertNil(occurrence)
        await metadata.close()
    }

    @MainActor func testProgressDoesNotInvalidateLibrarySettingsOrQueueContent() async {
        let (_, _, model) = setup()
        var library = 0, settings = 0, queue = 0
        let observers = [model.libraryChanges.sink { library += 1 },
                         model.settingsChanges.sink { settings += 1 },
                         model.queueChanges.sink { queue += 1 }]
        model.clock.position = 12
        model.playbackState.state = MusicPlaybackState(revision: 1, position: 12)
        XCTAssertEqual(library, 0)
        XCTAssertEqual(settings, 0)
        XCTAssertEqual(queue, 0)
        withExtendedLifetime(observers) { }
        await model.close()
    }

    @MainActor func testLibraryMutationDoesNotInvalidatePlayerOrSettings() async {
        let (_, _, model) = setup()
        var library = 0, player = 0, settings = 0
        let observers = [model.libraryChanges.sink { library += 1 },
                         model.playbackChanges.sink { player += 1 },
                         model.settingsChanges.sink { settings += 1 }]
        model.libraryState.collections = [.init(id: "local", title: "本地歌单")]
        XCTAssertEqual(library, 1)
        XCTAssertEqual(player, 0)
        XCTAssertEqual(settings, 0)
        withExtendedLifetime(observers) { }
        await model.close()
    }

    @MainActor private func setup() -> (MusicModelService, MusicModelPlayer, MusicViewModel) {
        let service = MusicModelService(), player = MusicModelPlayer()
        return (service, player, MusicViewModel(service: service, player: player))
    }
    @MainActor private func fact(_ model: MusicViewModel, _ kind: MusicPlaybackFact.Kind, position: Double = 0) throws -> MusicPlaybackFact {
        let resource = try XCTUnwrap(model.activeResourceID), occurrence = try XCTUnwrap(model.activeOccurrenceID)
        return MusicPlaybackFact(attemptId: String(resource.dropFirst("music:".count)), occurrenceId: occurrence,
                                 resourceId: resource, kind: kind, position: position, duration: 180)
    }

    @MainActor func testLoadRestoresOccurrencesAndPositionWithoutResolvingOrPlaying() async throws {
        let (_, player, model) = setup(); await model.load()
        XCTAssertEqual(model.queue.map(\.id), ["entry-1", "entry-2", "entry-3"])
        XCTAssertEqual(model.queue[0].track.songId, model.queue[1].track.songId)
        XCTAssertEqual(model.position, 17); XCTAssertEqual(model.phase, .paused)
        XCTAssertTrue(player.resources.isEmpty); XCTAssertNil(model.activeResourceID)
        await model.close()
    }

    @MainActor func testPlaylistPreviewPlayKeepsMoreThanFormerTwoThousandOccurrences() async throws {
        let (service, _, model) = setup(); await model.load()
        let track = try XCTUnwrap(model.queue.first).track
        await model.play(Array(repeating: track, count: 2001), index: 2000)
        let queue = try await service.queue()
        XCTAssertNil(model.error)
        XCTAssertEqual(model.queue.count, 2001); XCTAssertEqual(model.state.total, 2001)
        XCTAssertEqual(model.state.currentId, queue.entries.last?.id)
        XCTAssertEqual(model.queue, queue.entries)
        await model.close()
    }

    @MainActor func testPlaylistPreviewPlayStillRejectsEmptyAndOutOfRangeSelection() async throws {
        let (service, _, model) = setup(); await model.load()
        let track = try XCTUnwrap(model.queue.first).track
        let before = try await service.queue()
        for (tracks, index) in [([MusicRemoteTrack](), 0), ([track], -1), ([track], 1)] {
            await model.play(tracks, index: index)
            XCTAssertEqual(model.error, MusicServiceError.invalidRequest.localizedDescription)
        }
        let after = try await service.queue()
        XCTAssertEqual(after.revision, before.revision); XCTAssertEqual(after.entries, before.entries)
        await model.close()
    }

    @MainActor func testOptionsNeverAutomaticallyPickFirstScriptAndActualPlayingAloneRecordsStart() async throws {
        let (service, player, model) = setup()
        var requests: [MusicPlaybackDestination] = []
        model.onRequestPresentation = { requests.append($0) }
        await model.load(); await model.prepareCurrent()
        XCTAssertEqual(requests, [.sources]); XCTAssertTrue(player.resources.isEmpty)
        await model.choose(musicSelection)
        XCTAssertEqual(player.resources.count, 1)
        let before = await service.recordedFacts(); XCTAssertTrue(before.isEmpty)
        model.onPlayback(try fact(model, .playing))
        await model.flushPlaybackFacts()
        let after = await service.recordedFacts(); XCTAssertEqual(after.map(\.kind), [.playing])
        await model.close()
    }

    @MainActor func testSourceAvailabilityKeepsOfficialOptionsAndScriptDiagnostics() async throws {
        let (service, player, model) = setup()
        let official = MusicPlaybackOption(installationId: musicOfficialSelection.installationId, generation: 1,
            siteId: "wy", sourceKey: musicSourceKey, qualities: ["128k"], title: "官方音源",
            version: "1", contentHash: String(repeating: "a", count: 64))
        await service.setAvailability(.init(options: [official], scriptState: .unavailable(.upstreamFailed)))
        await model.load(); await model.prepareCurrent()
        XCTAssertEqual(model.options, [official])
        XCTAssertEqual(model.scriptSourceError, MusicServiceError.upstreamFailed.localizedDescription)
        XCTAssertNil(model.error); XCTAssertTrue(player.resources.isEmpty)

        await service.setAvailability(.init(options: [], scriptState: .unavailable(.timeout)))
        await model.prepareCurrent()
        XCTAssertTrue(model.options.isEmpty)
        XCTAssertEqual(model.scriptSourceError, MusicServiceError.timeout.localizedDescription)
        let message = try XCTUnwrap(model.error)
        XCTAssertNotNil(message.range(of: "^网易云音乐 · 播放歌曲：音源不可用（诊断 [A-F0-9]{8}）$",
                                      options: .regularExpression))
        XCTAssertTrue(player.resources.isEmpty)
        await model.close()
    }

    @MainActor func testSearchAvailabilityPreservesEmptySourceAndScriptFailureSemantics() async throws {
        let (service, player, model) = setup()
        let reference = MusicReference(kind: .queue, id: "entry-1")
        for state in [MusicSourceAvailability.ScriptState.ready, .notConfigured] {
            await service.setAvailability(.init(options: [], scriptState: state))
            let options = try await model.searchPlaybackOptions(for: reference)
            XCTAssertTrue(options.isEmpty)
        }
        await service.setAvailability(.init(options: [], scriptState: .unavailable(.timeout)))
        do { _ = try await model.searchPlaybackOptions(for: reference); XCTFail("An unavailable host cannot appear as empty success") }
        catch { XCTAssertEqual(error as? MusicServiceError, .timeout) }

        let official = MusicPlaybackOption(installationId: musicOfficialSelection.installationId, generation: 1,
            siteId: "wy", sourceKey: musicSourceKey, qualities: ["128k"], title: "官方音源",
            version: "1", contentHash: String(repeating: "a", count: 64))
        let unsupported = MusicPlaybackOption(installationId: "other", generation: 1,
            siteId: "other", sourceKey: "other", qualities: ["128k"], title: "其它",
            version: "1", contentHash: String(repeating: "b", count: 64))
        await service.setAvailability(.init(options: [official, unsupported], scriptState: .unavailable(.upstreamFailed)))
        let options = try await model.searchPlaybackOptions(for: reference)
        XCTAssertEqual(options, [official])
        XCTAssertTrue(model.queue.isEmpty); XCTAssertTrue(player.resources.isEmpty)
        await model.close()
    }

    @MainActor func testSearchAvailabilityRejectsLateResultAfterAccessOrAccountChanges() async {
        for accountChanged in [false, true] {
            let (service, _, model) = setup()
            let started = expectation(description: "availability read entered")
            let gate = MusicCollectionGate { started.fulfill() }
            let availability = await service.sourceAvailability(for: .init(kind: .queue, id: "entry-1"))
            await service.setAvailability(availability, gate: gate)
            let read = Task { try await model.searchPlaybackOptions(for: .init(kind: .queue, id: "entry-1")) }
            await fulfillment(of: [started], timeout: 2)
            if accountChanged { model.accountSessionsChanged(.netease) }
            else { model.setAvailable(false) }
            await gate.release()
            do { _ = try await read.value; XCTFail("A revoked source result escaped the search boundary") }
            catch { XCTAssertEqual(error as? MusicServiceError, accountChanged ? .staleAccess : .accessUnavailable) }
            await model.close()
        }
    }

    @MainActor func testSynchronousSystemCommandAdmissionOwnsTaskAndRejectsDuplicateUntilFinished() async throws {
        let (service, player, model) = setup(); await model.load(); await model.choose(musicSelection)
        XCTAssertTrue(model.canNext); XCTAssertTrue(model.requestNext()); XCTAssertFalse(model.requestNext())
        XCTAssertTrue(model.isNavigating)
        await model.flushNavigation()
        XCTAssertFalse(model.isNavigating); XCTAssertEqual(model.state.currentId, "entry-2")
        let moves = await service.recordedMoves(); XCTAssertEqual(moves, ["next:manual"])
        XCTAssertEqual(player.resources.count, 2)
        model.setAvailable(false); XCTAssertFalse(model.canNext); XCTAssertFalse(model.requestPrevious())
        await model.close()
    }

    @MainActor func testSingleModeNaturalEndCreatesNewAttemptAtSameOccurrenceButManualNextAdvances() async throws {
        let (service, player, model) = setup(); await model.load(); await model.setMode(.single); await model.choose(musicSelection)
        let first = try XCTUnwrap(model.activeResourceID)
        model.onPlayback(try fact(model, .ended, position: 180))
        await model.flushPlaybackFacts(); await model.flushNavigation()
        XCTAssertEqual(model.state.currentId, "entry-1"); XCTAssertNotEqual(model.activeResourceID, first)
        XCTAssertEqual(player.resources.count, 2)
        XCTAssertTrue(model.requestNext()); await model.flushNavigation()
        XCTAssertEqual(model.state.currentId, "entry-2")
        let moves = await service.recordedMoves(); XCTAssertEqual(moves, ["next:ended", "next:manual"])
        await model.close()
    }

    @MainActor func testABAReturnDoesNotReadCurrentSelectionForOldCallback() async throws {
        let (service, player, model) = setup(); await model.load(); await model.choose(musicSelection)
        let old = try fact(model, .playing)
        await model.selectOccurrence("entry-2"); await model.selectOccurrence("entry-1")
        XCTAssertEqual(player.resources.count, 3); XCTAssertNotEqual(model.activeResourceID, old.resourceId)
        model.onPlayback(old)
        model.onPlayback(try fact(model, .playing))
        await model.flushPlaybackFacts()
        let recorded = await service.recordedFacts()
        XCTAssertEqual(recorded.count, 1); XCTAssertEqual(recorded.first?.resourceId, model.activeResourceID)
        await model.close()
    }

    @MainActor func testLateCancelledResolutionCannotReplaceNewerSelectedOccurrence() async throws {
        let (service, player, model) = setup(); await model.load()
        let began = expectation(description: "old resolve began")
        await service.blockResolve { began.fulfill() }
        let old = Task { await model.choose(musicSelection) }
        await fulfillment(of: [began], timeout: 2)
        await model.selectOccurrence("entry-2")
        // There was no successfully selected source yet, so selection is still explicit.
        await model.choose(musicSelection)
        let resource = model.activeResourceID
        await service.releaseResolve(); await old.value
        XCTAssertEqual(model.activeResourceID, resource); XCTAssertEqual(model.activeOccurrenceID, "entry-2")
        XCTAssertEqual(player.resources.count, 1)
        await model.close()
    }

    @MainActor func testFailedQueueCommitDoesNotStopOrRebindValidNativeAttempt() async throws {
        let (service, player, model) = setup(); await model.load(); await model.choose(musicSelection)
        let resource = model.activeResourceID
        await service.setFailChange(true); await model.selectOccurrence("entry-2")
        XCTAssertEqual(model.activeResourceID, resource); XCTAssertTrue(player.stopped.isEmpty)
        XCTAssertEqual(model.state.currentId, "entry-1"); XCTAssertNotNil(model.error)
        await model.close()
    }

    @MainActor func testRemovingDifferentOccurrenceDoesNotStopCurrentMedia() async throws {
        let (_, player, model) = setup(); await model.load(); await model.choose(musicSelection)
        let resource = model.activeResourceID
        await model.removeOccurrence("entry-3")
        XCTAssertEqual(model.activeResourceID, resource); XCTAssertEqual(model.queue.count, 2)
        XCTAssertTrue(player.stopped.isEmpty)
        await model.close()
    }

    @MainActor func testAccessRevocationClearsBusinessStateWithoutImposingNativeLockScreenStopPolicy() async throws {
        let (service, player, model) = setup(); await model.load(); await model.choose(musicSelection)
        let old = try fact(model, .playing)
        model.setAvailable(false); model.onPlayback(old); await model.flushPlaybackFacts()
        XCTAssertTrue(model.queue.isEmpty); XCTAssertTrue(model.collections.isEmpty); XCTAssertFalse(model.requestNext())
        XCTAssertTrue(player.stopped.isEmpty)
        let facts = await service.recordedFacts(); XCTAssertTrue(facts.isEmpty)
        model.setAvailable(true); await model.load()
        XCTAssertEqual(player.resources.count, 1); XCTAssertNil(model.activeResourceID)
        await model.close()
    }

    @MainActor func testSearchAccessConsumesProviderEchoFactsWithoutIdentityReadmission() async throws {
        let (service, _, model) = setup()
        await model.load()
        let track = try XCTUnwrap(model.current?.track)
        let collection = MusicPublicBrowseCollection(platform: .qq, nativeID: "canonical", title: "当前集合")
        await service.returnSearchResults(
            .init(platform: "tx", query: "canonical keyword", page: 2, limit: 60, total: 1,
                  nextPage: 7, items: [track]),
            playlists: .init(platform: .qq, query: "canonical keyword", page: 3, limit: 60,
                             total: 1, nextPage: 8, items: [collection]))
        let songs = try await model.searchPage(platform: "wy", query: "submitted keyword", page: 1, limit: 30)
        XCTAssertEqual(songs.platform, "tx"); XCTAssertEqual(songs.query, "canonical keyword")
        XCTAssertEqual(songs.page, 2); XCTAssertEqual(songs.limit, 60)
        XCTAssertEqual(songs.items, [track]); XCTAssertEqual(songs.nextPage, 7)
        let playlists = try await model.searchPlaylistPage(platform: .netease, query: "submitted keyword", page: 1, limit: 30)
        XCTAssertEqual(playlists.platform, .qq); XCTAssertEqual(playlists.query, "canonical keyword")
        XCTAssertEqual(playlists.page, 3); XCTAssertEqual(playlists.limit, 60)
        XCTAssertEqual(playlists.items, [collection]); XCTAssertEqual(playlists.nextPage, 8)
        await model.close()
    }

    @MainActor func testRevokedSearchCannotPublishLatePrivateBusinessMetadata() async throws {
        let (service, _, model) = setup(); await model.load(); model.query = "synthetic"
        let began = expectation(description: "search began")
        await service.blockSearch { began.fulfill() }
        let task = Task { try await model.searchPage(platform: "wy", query: model.query, page: 1) }
        await fulfillment(of: [began], timeout: 2)
        model.setAvailable(false); await service.releaseSearch()
        do { _ = try await task.value; XCTFail("Revoked metadata escaped the search access boundary") }
        catch { XCTAssertEqual(error as? MusicServiceError, .accessUnavailable) }
        await model.close()
    }

    @MainActor func testAccountChangeRejectsLateSearchWithoutRevokingCurrentPlayback() async throws {
        let (service, player, model) = setup()
        await model.load(); await model.choose(musicSelection)
        let playbackGeneration = model.accountAccessGeneration, searchGeneration = model.searchAccessGeneration
        let resource = model.activeResourceID, queue = model.queue
        let began = expectation(description: "old account search began")
        await service.blockSearch { began.fulfill() }
        let task = Task { try await model.searchPage(platform: "wy", query: "synthetic", page: 1) }
        await fulfillment(of: [began], timeout: 2)
        var notifications: [UUID] = []
        let observation = model.searchAccessChanges.sink { notifications.append(model.searchAccessGeneration) }
        model.accountSessionsChanged(.netease)
        XCTAssertNotEqual(model.searchAccessGeneration, searchGeneration)
        XCTAssertEqual(notifications, [model.searchAccessGeneration])
        XCTAssertEqual(model.accountAccessGeneration, playbackGeneration)
        XCTAssertEqual(model.activeResourceID, resource)
        XCTAssertEqual(model.queue, queue)
        XCTAssertTrue(player.stopped.isEmpty)
        await service.releaseSearch()
        do { _ = try await task.value; XCTFail("An old account read cannot escape the search boundary") }
        catch { XCTAssertEqual(error as? MusicServiceError, .staleAccess) }
        observation.cancel()
        await model.close()
    }

    @MainActor func testAuthoritativeAccountEventIsolatesSearchCacheWithoutUIEcho() async throws {
        let observed = expectation(description: "authoritative account stream observed")
        let accounts = MusicModelAccountSessions { observed.fulfill() }
        let service = MusicModelService(), player = MusicModelPlayer()
        let model = MusicViewModel(service: service, player: player, accounts: accounts)
        await fulfillment(of: [observed], timeout: 2)
        let results = MusicSearchResultsModel(owner: model)
        results.setAccess(available: true, generation: model.searchAccessGeneration); results.appear()
        let preferences = MusicSearchPreferences(songPlatforms: [.netease], searchPlaylists: false)
        results.submit("synthetic", preferences: preferences); await results.finishPendingReads()
        results.submit("synthetic", preferences: preferences); await results.finishPendingReads()
        let firstReads = await service.searchReadCount()
        XCTAssertEqual(firstReads, 1)
        let generation = model.searchAccessGeneration, playbackGeneration = model.accountAccessGeneration
        model.accountSessionsChanged(.netease)
        XCTAssertEqual(model.searchAccessGeneration, generation, "The authoritative stream suppresses an extra UI echo")
        let changed = expectation(description: "search account generation published")
        let observation = model.searchAccessChanges.sink {
            results.setAccess(available: model.isAvailable && !model.isClosed, generation: model.searchAccessGeneration)
            changed.fulfill()
        }
        await accounts.send(.netease)
        await fulfillment(of: [changed], timeout: 2)
        XCTAssertNotEqual(model.searchAccessGeneration, generation)
        XCTAssertEqual(model.accountAccessGeneration, playbackGeneration)
        XCTAssertTrue(results.query.isEmpty)
        results.submit("synthetic", preferences: preferences); await results.finishPendingReads()
        let nextReads = await service.searchReadCount()
        XCTAssertEqual(nextReads, 2)
        observation.cancel(); results.disappear()
        await model.close(); await accounts.finish()
    }

    @MainActor private func searchSession(_ owner: MusicViewModel) -> MusicSearchResultsModel {
        let results = MusicSearchResultsModel(owner: owner)
        results.setAccess(available: owner.isAvailable && !owner.isClosed, generation: owner.searchAccessGeneration)
        results.appear()
        results.submit("synthetic", preferences: .init(songPlatforms: [.netease], searchPlaylists: false))
        return results
    }

    @MainActor func testSearchPagesShareOnlyTheirBusinessOwnersCurrentCache() async {
        let (service, _, owner) = setup()
        let first = searchSession(owner); await first.finishPendingReads(); first.disappear()
        let second = searchSession(owner); await second.finishPendingReads()
        var reads = await service.searchReadCount(); XCTAssertEqual(reads, 1)
        let other = MusicViewModel(service: service, player: MusicModelPlayer())
        let foreign = searchSession(other); await foreign.finishPendingReads()
        reads = await service.searchReadCount(); XCTAssertEqual(reads, 2)
        XCTAssertTrue(second.songsLoaded); XCTAssertTrue(foreign.songsLoaded)
        second.disappear(); foreign.disappear()
        await owner.close(); await other.close()
    }

    @MainActor func testSearchCacheIsRetiredOnAccountChangeEvenWithoutAnyResultPageAlive() async {
        let (service, _, owner) = setup()
        var first: MusicSearchResultsModel? = searchSession(owner)
        await first?.finishPendingReads(); first?.disappear(); first = nil
        owner.accountSessionsChanged(.netease)
        let second = searchSession(owner); await second.finishPendingReads()
        var reads = await service.searchReadCount(); XCTAssertEqual(reads, 2)
        second.disappear()
        owner.setAvailable(false); owner.setAvailable(true)
        let third = searchSession(owner); await third.finishPendingReads()
        reads = await service.searchReadCount(); XCTAssertEqual(reads, 3)
        third.disappear(); await owner.close()
        let closed = searchSession(owner); await closed.finishPendingReads()
        XCTAssertFalse(closed.songsLoaded); XCTAssertTrue(closed.songs.isEmpty)
        reads = await service.searchReadCount(); XCTAssertEqual(reads, 3)
        closed.disappear()
    }

    @MainActor func testRetiredSearchLoaderCannotRefillNewPagesCache() async {
        let (service, _, owner) = setup()
        let entered = expectation(description: "retired account read entered")
        await service.blockSearch { entered.fulfill() }
        let first = searchSession(owner)
        await fulfillment(of: [entered], timeout: 2)
        owner.accountSessionsChanged(.netease)
        let second = searchSession(owner); await second.finishPendingReads()
        XCTAssertTrue(second.songsLoaded); XCTAssertTrue(second.songs.isEmpty)
        await service.releaseSearch(); await first.finishPendingReads()
        let third = searchSession(owner); await third.finishPendingReads()
        XCTAssertTrue(third.songsLoaded); XCTAssertTrue(third.songs.isEmpty)
        let reads = await service.searchReadCount(); XCTAssertEqual(reads, 2)
        first.disappear(); second.disappear(); third.disappear(); await owner.close()
    }

    @MainActor func testProgressCoalescesAndNeverFabricatesPlayedFacts() async throws {
        let (service, _, model) = setup(); await model.load(); await model.choose(musicSelection)
        for position in 0...5 {
            model.onPlayback(try fact(model, .progress, position: Double(position)))
            await model.flushPlaybackFacts()
        }
        model.onPlayback(try fact(model, .paused, position: 5)); await model.flushPlaybackFacts()
        let facts = await service.recordedFacts()
        XCTAssertEqual(facts.map(\.kind), [.progress, .progress, .paused]); XCTAssertEqual(model.position, 5)
        await model.close()
    }

    @MainActor func testPendingPlaybackFactsRemainOrderedThroughTerminalStop() async throws {
        let (service, _, model) = setup(); await model.load(); await model.choose(musicSelection)
        let started = expectation(description: "first fact awaiting persistence")
        let gate = MusicCollectionGate { started.fulfill() }
        await service.blockRecord(gate)
        model.onPlayback(try fact(model, .progress, position: 0))
        await fulfillment(of: [started], timeout: 2)
        for index in 1...9 {
            model.onPlayback(try fact(model, .progress, position: Double(index * 5)))
        }
        model.onPlayback(try fact(model, .stopped, position: 50))
        XCTAssertNil(model.error)
        await gate.release(); await model.flushPlaybackFacts()
        let facts = await service.recordedFacts()
        XCTAssertEqual(facts.map(\.kind), Array(repeating: .progress, count: 10) + [.stopped])
        XCTAssertEqual(facts.map(\.position), (0...10).map { Double($0 * 5) })
        XCTAssertNil(model.error)
        await model.close()
    }

    @MainActor func testHistoryRestoreStopsOwnedMediaButDoesNotPrepareOrAutoplay() async throws {
        let (_, player, model) = setup(); await model.load(); await model.choose(musicSelection)
        await model.restoreHistory(MusicPlayedPlaylist(id: "history", title: "历史队列", count: 3, at: 0))
        XCTAssertEqual(player.resources.count, 1); XCTAssertEqual(player.stopped.count, 1)
        XCTAssertNil(model.activeResourceID)
        await model.close()
    }

    /// Actual shared controller preflight: caller-controlled Range is rejected
    /// before decoder/network/audio activation; ordinary Referer is legal.
    @MainActor func testSharedControllerSynchronousPreflightFailureKeepsNativeUUIDAndRuntimeResource() async throws {
        let service = MusicModelService(), playback = PlaybackController()
        await service.setHeaders(["Range": "bytes=0-1"])
        let model = MusicViewModel(service: service, playback: playback)
        var captured: [PlaybackEvent] = []
        let lease = try playback.bind(owner: "music") { event in captured.append(event); model.onPlaybackEvent(event) }
        await model.load(); await model.choose(musicSelection); await model.flushPlaybackFacts()
        XCTAssertEqual(model.phase, .failed); XCTAssertFalse(playback.hasActiveMusicInput)
        XCTAssertEqual(model.activeNativeAttemptID, captured.first?.attempt.id)
        XCTAssertEqual(model.activeResourceID, captured.first?.attempt.resourceID)
        let first = try XCTUnwrap(captured.first)
        await model.choose(musicSelection); await model.flushPlaybackFacts()
        let before = await service.recordedFacts()
        model.onPlaybackEvent(first); await model.flushPlaybackFacts()
        let after = await service.recordedFacts()
        XCTAssertEqual(before.count, after.count); XCTAssertTrue(after.allSatisfy { $0.kind == .failed })
        XCTAssertNotEqual(model.activeNativeAttemptID, first.attempt.id)
        await model.close(); lease.cancel()
    }

    func testLyricTimelinePreservesDuplicateTimesWordTimingAndThreeLinePreview() {
        let lines = [
            MusicLyricLine(index: 0, time: 1, end: 2, text: "甲"),
            MusicLyricLine(index: 1, time: 1, end: 3, text: "乙"),
            MusicLyricLine(index: 2, time: 3, end: 4, text: "丙"),
            MusicLyricLine(index: 3, time: 4, end: 5, text: "丁")
        ]
        XCTAssertNil(MusicLyricTimeline.activeIndex(lines: lines, at: 0))
        XCTAssertEqual(MusicLyricTimeline.activeIndex(lines: lines, at: 1), 1)
        XCTAssertEqual(MusicLyricTimeline.preview(lines: lines, at: 100).map(\.index), [1, 2, 3])
        let word = MusicLyricWord(time: 1, duration: 2, text: "合成")
        XCTAssertEqual(MusicLyricTimeline.wordProgress(word, at: 2), 0.5)
        XCTAssertEqual(MusicLyricTimeline.wordProgress(word, at: .infinity), 0)
    }

}

private actor MusicModelInspectionSession: MusicSourceInspectionSession {
    let service: MusicModelService
    init(service: MusicModelService) { self.service = service }
    func updates() -> AsyncThrowingStream<MusicResolvedSourcesSnapshot, Error> {
        AsyncThrowingStream { $0.finish() }
    }
    func loadMore(refresh: Bool) async throws -> MusicResolvedSourcesSnapshot {
        .init(sources: [.init(id: "inspected", selection: musicOfficialSelection,
                              title: "合成音源", delivery: nil)], hasMore: false)
    }
    func adopt(sourceID: String, occurrenceID: String,
               expectedQueueRevision: Int) async throws -> MusicPlaybackSelection {
        guard sourceID == "inspected" else { throw MusicServiceError.notFound }
        return try await service.adoptInspected(musicOfficialSelection,
                                                occurrenceID: occurrenceID,
                                                expectedQueueRevision: expectedQueueRevision)
    }
    func reject(sourceKey: String, occurrenceID: String, expectedQueueRevision: Int) async throws {
        throw MusicServiceError.unsupported
    }
    func close() async {}
}
#endif
