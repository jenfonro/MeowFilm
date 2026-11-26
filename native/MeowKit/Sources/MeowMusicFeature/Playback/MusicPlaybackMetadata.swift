import Foundation
#if os(iOS) || os(macOS)
import MeowContracts
import MeowDebug

/// Lyrics use permanent-song/source references. Artwork bytes belong only to
/// the shared image loader; this owner publishes the repository's chosen URL.
@MainActor final class MusicPlaybackMetadata {
    private struct LyricCacheKey: Hashable, Sendable {
        let songID: String
        let sourceKey: String
    }

    struct Presentation {
        let occurrenceID: String?
        let track: MusicRemoteTrack?
        let lyricState: String
        let lines: [MusicLyricLine]
        let artwork: URL?
    }
    private var service: NativeMusicService?
    private var accounts: NativeMusicAccountService?
    private var display: MusicNativePresentationCache?
    private var apply: (@MainActor (Presentation) -> Void)?
    private let lyrics = AsyncValueCache<LyricCacheKey, MusicLyricsSnapshot>(capacity: 64)
    private var closed = false
    private var session = UUID()
    private var projection = UUID()
    private var entry: MusicQueueEntry?
    private var track: MusicRemoteTrack?
    private var enabled = true
    private var restoreOnly = false
    private var task: Task<Void, Never>?
    private var artworkTask: Task<Void, Never>?
    private var revocations: Task<Void, Never>?
    private var authorization: MusicQueueAuthorization?
    private var candidates: [MusicMetadataCandidate] = []
    private var coverURL: URL?
    private var failedArtwork: Set<URL> = []
    private var state = "idle"
    private var lines: [MusicLyricLine] = []

    func configure(service: NativeMusicService?, accounts: NativeMusicAccountService?,
                   apply: @escaping @MainActor (Presentation) -> Void) {
        self.service = service; self.accounts = accounts
        self.apply = apply
    }
    func setDisplayCache(_ cache: MusicNativePresentationCache?) { display = cache }
    private func cancelLoad() {
        session = UUID(); task?.cancel(); task = nil; artworkTask?.cancel(); artworkTask = nil
        revocations?.cancel(); revocations = nil
        authorization?.revoke(); authorization = nil
    }
    private func invalidate() {
        cancelLoad()
        entry = nil; track = nil; candidates = []; coverURL = nil; failedArtwork.removeAll()
        state = "idle"; lines = []; publish()
    }
    private func publish() {
        apply?(.init(occurrenceID: entry?.id, track: track, lyricState: state, lines: lines, artwork: coverURL))
    }
    private func check(_ id: UUID) throws {
        try Task.checkCancellation()
        guard !closed, session == id else { throw MusicServiceError.cancelled }
        try authorization?.check()
    }
    func clearSelection() { invalidate() }
    func project(_ current: MusicRemoteTrack) {
        guard !closed, entry?.track.songId == current.songId else { return }
        projection = UUID()
        track = current; coverURL = current.artwork
        publish()
    }

    func select(_ entry: MusicQueueEntry, lyricsEnabled: Bool, restoreOnly: Bool = false,
                force: Bool = false, refreshSources: Bool = false) {
        guard !closed else { return }
        if !force, !(refreshSources && (state != "ready" && lyricsEnabled || coverURL == nil)),
           self.entry == entry, enabled == lyricsEnabled, authorization?.isActive == true,
           self.restoreOnly == restoreOnly || !self.restoreOnly { return }
        let sameSong = self.entry?.track.songId == entry.track.songId
        if force { Task { await lyrics.invalidate() } }
        cancelLoad()
        if !sameSong {
            candidates = []; coverURL = entry.track.artwork; failedArtwork.removeAll(); lines = []
        } else if force { failedArtwork.removeAll() }
        self.entry = entry; track = entry.track; enabled = lyricsEnabled; self.restoreOnly = restoreOnly
        if !lyricsEnabled { lines = [] }
        state = lyricsEnabled ? "loading" : "disabled"; publish()
        let id = session
        task = Task { [weak self] in
            guard let self else { return }
            await self.load(entry, session: id)
            if self.session == id { self.task = nil }
        }
    }
    func flush() async {
        await task?.value; await artworkTask?.value
    }
    func debugLyricCacheStats() async -> DebugJSONValue {
        let stats = await lyrics.stats()
        return .object([
            "entryCount": .int(stats.entryCount),
            "inFlightCount": .int(stats.inFlightCount),
            "totalCost": .int(stats.totalCost),
            "capacity": stats.capacity.map(DebugJSONValue.int) ?? .null,
            "maximumCost": .int(stats.maximumCost)
        ])
    }
    func clearMemoryLyricCache() async { await lyrics.invalidate() }
    private func load(_ entry: MusicQueueEntry, session id: UUID) async {
        do {
            guard let service else { throw MusicServiceError.unavailable }
            let artworkAtStart = coverURL, projectedAtStart = projection
            let metadata = try await service.metadataCandidates(occurrenceID: entry.id)
            try check(id)
            candidates = metadata.candidates
            if projection == projectedAtStart {
                if track == entry.track { track = metadata.track }
                if coverURL == artworkAtStart { coverURL = metadata.track.artwork }
            }
            let scope = try await accounts?.nativeCacheScope()
            try check(id)
            let fence = scope.map { MusicQueueAuthorization(parent: $0.authorization) } ?? MusicQueueAuthorization()
            authorization = fence
            let changes = fence.revocations()
            revocations = Task { [weak self] in
                for await _ in changes {
                    guard !Task.isCancelled, let self, self.session == id else { return }
                    await self.lyrics.invalidate()
                    guard !Task.isCancelled, self.session == id else { return }
                    self.invalidate()
                    return
                }
            }
            let cacheRevision = display?.lyricRevision
            let saved = await display?.lyrics(songID: metadata.track.songId, authorization: fence)
            try check(id)
            if let scope { try await accounts?.checkNativeCacheScope(scope); try check(id) }
            publish()
            guard enabled else { state = "disabled"; publish(); return }
            if restoreOnly {
                if let saved, display?.lyricRevision == cacheRevision,
                   candidates.contains(where: { $0.source.sourceKey == saved.sourceKey }) { lines = saved.lines }
                state = lines.isEmpty ? "none" : "ready"; publish(); return
            }
            for candidate in candidates {
                try check(id)
                if let saved, saved.sourceKey == candidate.source.sourceKey,
                   display?.lyricRevision == cacheRevision, !saved.lines.isEmpty {
                    lines = saved.lines; state = "ready"; publish(); return
                }
                let key = LyricCacheKey(songID: metadata.track.songId, sourceKey: candidate.source.sourceKey)
                do {
                    let value = try await MusicOperationDeadline.run(seconds: 8) {
                        try await self.lyrics.value(for: key, lifetime: 300) {
                            let result = try await service.lyrics(occurrenceId: entry.id, sourceKey: candidate.source.sourceKey)
                            guard result.state == .ready, !result.lines.isEmpty else { throw MusicServiceError.notFound }
                            return result
                        }
                    }
                    try check(id)
                    if let scope { try await accounts?.checkNativeCacheScope(scope); try check(id) }
                    try fence.perform { lines = value.lines; state = "ready"; publish() }
                    if let display, let cacheRevision {
                        await display.storeLyrics(value.lines, sourceKey: candidate.source.sourceKey,
                            songID: metadata.track.songId, revision: cacheRevision, authorization: fence)
                    }
                    return
                } catch {
                    try check(id)
                    let failure = MusicServiceError.sanitized(error)
                    if [.accessUnavailable, .staleAccess, .closed, .cancelled, .targetChanged].contains(failure) { throw error }
                }
            }
            try check(id)
            state = "none"; lines = []; publish()
        } catch {
            guard session == id, !closed else { return }
            state = enabled ? "none" : "disabled"; lines = []; publish()
        }
    }

    func artworkFailed(_ url: URL?) {
        guard !closed, let entry, let url, coverURL == url, let service else { return }
        failedArtwork.insert(url)
        let id = session, attempted = failedArtwork
        artworkTask?.cancel()
        artworkTask = Task { [weak self] in
            do {
                let next = try await service.failedArtwork(songID: entry.track.songId, url: url, attempted: attempted)
                guard let self else { return }
                try self.check(id)
                guard self.coverURL == url else { return }
                self.coverURL = next; self.publish()
            } catch {
                if MusicServiceError.sanitized(error) != .cancelled {
                    DebugRuntime.logError(.musicPlayback, sub: "metadata", event: "artworkFailed.error", error: error)
                }
            }
        }
    }
    func artworkLoaded(_ url: URL?, resourceURL: URL) {
        guard !closed, let entry, let url, coverURL == url, let service else { return }
        let id = session
        artworkTask?.cancel()
        artworkTask = Task { [weak self] in
            do {
                let selected = try await service.succeededArtwork(songID: entry.track.songId,
                    url: resourceURL, replacing: url)
                guard let self else { return }
                try self.check(id)
                guard self.coverURL == url, let track = self.track else { return }
                self.coverURL = selected
                self.track = .init(ref: track.ref, songId: track.songId, title: track.title,
                    artist: track.artist, album: track.album, duration: track.duration, sources: track.sources,
                    artwork: selected, artistNames: track.artistNames, sourcePosition: track.sourcePosition)
                self.publish()
            } catch {
                if MusicServiceError.sanitized(error) != .cancelled {
                    DebugRuntime.logError(.musicPlayback, sub: "metadata", event: "artworkLoaded.error", error: error)
                }
            }
        }
    }
    func close() async {
        guard !closed else { return }
        let load = task, artwork = artworkTask, revoked = revocations
        invalidate(); closed = true; apply = nil
        await lyrics.invalidate()
        await load?.value; await artwork?.value; await revoked?.value
    }
    deinit { task?.cancel(); artworkTask?.cancel(); revocations?.cancel(); authorization?.revoke() }
}
#endif
