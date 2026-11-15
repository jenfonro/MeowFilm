import Foundation
#if os(iOS) || os(macOS)
import Combine

/// Lyrics use permanent-song/source references. Artwork bytes belong only to
/// the shared image loader; this owner publishes the repository's chosen URL.
@MainActor final class MusicPlaybackMetadata {
    struct Presentation {
        let occurrenceID: String?
        let track: MusicRemoteTrack?
        let lyricState: String
        let lines: [MusicLyricLine]
        let artwork: URL?
    }
    private var service: (any MusicServing)?
    private var accounts: (any MusicNativeCacheScopeProviding)?
    private var display: (any MusicPresentationCaching)?
    private var apply: (@MainActor (Presentation) -> Void)?
    private let lyrics = MusicPlaybackLyricCache()
    private var generation = UUID()
    private var available = false, closed = false
    private var session = UUID()
    private var projection = UUID()
    private var entry: MusicQueueEntry?
    private var track: MusicRemoteTrack?
    private var enabled = true
    private var restoreOnly = false
    private var request: MusicPlaybackLyricCache.Request?
    private var task: Task<Void, Never>?
    private var artworkTask: Task<Void, Never>?
    private var revocations: Task<Void, Never>?
    private var authorization: MusicQueueAuthorization?
    private var candidates: [MusicMetadataCandidate] = []
    private var coverURL: URL?
    private var failedArtwork: Set<URL> = []
    private var state = "idle"
    private var lines: [MusicLyricLine] = []

    func configure(service: (any MusicServing)?, accounts: (any MusicAccountsServing)?,
                   apply: @escaping @MainActor (Presentation) -> Void) {
        self.service = service; self.accounts = accounts as? any MusicNativeCacheScopeProviding
        self.apply = apply
    }
    func setDisplayCache(_ cache: (any MusicPresentationCaching)?) { display = cache }
    func setAccess(available: Bool, generation: UUID) {
        guard !closed else { return }
        if self.available != available || self.generation != generation { invalidate() }
        self.available = available; self.generation = generation
        lyrics.setAccess(available: available, generation: generation)
    }
    private func cancelLoad() {
        session = UUID(); task?.cancel(); task = nil; artworkTask?.cancel(); artworkTask = nil
        request?.cancel(); request = nil; revocations?.cancel(); revocations = nil
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
    private func check(_ id: UUID, access: UUID) throws {
        try Task.checkCancellation()
        guard available, !closed, session == id, generation == access else { throw MusicServiceError.cancelled }
        try authorization?.check()
    }
    func clearSelection() { invalidate() }
    func project(_ current: MusicRemoteTrack) {
        guard available, !closed, entry?.track.songId == current.songId else { return }
        projection = UUID()
        track = current; coverURL = current.artwork
        publish()
    }

    func select(_ entry: MusicQueueEntry, lyricsEnabled: Bool, restoreOnly: Bool = false,
                force: Bool = false, refreshSources: Bool = false) {
        guard available, !closed else { return }
        if !force, !(refreshSources && (state != "ready" && lyricsEnabled || coverURL == nil)),
           self.entry == entry, enabled == lyricsEnabled, authorization?.isActive == true,
           self.restoreOnly == restoreOnly || !self.restoreOnly { return }
        let sameSong = self.entry?.track.songId == entry.track.songId
        if force { lyrics.removeAll() }
        cancelLoad()
        if !sameSong {
            candidates = []; coverURL = entry.track.artwork; failedArtwork.removeAll(); lines = []
        } else if force { failedArtwork.removeAll() }
        self.entry = entry; track = entry.track; enabled = lyricsEnabled; self.restoreOnly = restoreOnly
        if !lyricsEnabled { lines = [] }
        state = lyricsEnabled ? "loading" : "disabled"; publish()
        let id = session, access = generation
        task = Task { [weak self] in
            guard let self else { return }
            await self.load(entry, session: id, access: access)
            if self.session == id { self.task = nil }
        }
    }
    func flush() async {
        await task?.value; await artworkTask?.value; await lyrics.flush()
    }
    private func load(_ entry: MusicQueueEntry, session id: UUID, access: UUID) async {
        do {
            guard let service else { throw MusicServiceError.unavailable }
            let artworkAtStart = coverURL, projectedAtStart = projection
            let metadata = try await service.metadataCandidates(occurrenceID: entry.id)
            try check(id, access: access)
            candidates = metadata.candidates
            if projection == projectedAtStart {
                if track == entry.track { track = metadata.track }
                if coverURL == artworkAtStart { coverURL = metadata.track.artwork }
            }
            let scope = try await accounts?.nativeCacheScope()
            try check(id, access: access)
            let fence = scope.map { MusicQueueAuthorization(parent: $0.authorization) } ?? MusicQueueAuthorization()
            authorization = fence
            let changes = fence.revocations()
            revocations = Task { [weak self] in
                for await _ in changes {
                    guard !Task.isCancelled, let self, self.session == id else { return }
                    self.lyrics.removeAll(); self.invalidate()
                    return
                }
            }
            let cacheRevision = display?.lyricRevision
            let saved = await display?.lyrics(songID: metadata.track.songId, authorization: fence)
            try check(id, access: access)
            if let scope { try await accounts?.checkNativeCacheScope(scope); try check(id, access: access) }
            publish()
            guard enabled else { state = "disabled"; publish(); return }
            if restoreOnly {
                if let saved, display?.lyricRevision == cacheRevision,
                   candidates.contains(where: { $0.source.sourceKey == saved.sourceKey }) { lines = saved.lines }
                state = lines.isEmpty ? "none" : "ready"; publish(); return
            }
            for candidate in candidates {
                try check(id, access: access)
                if let saved, saved.sourceKey == candidate.source.sourceKey,
                   display?.lyricRevision == cacheRevision, !saved.lines.isEmpty {
                    lines = saved.lines; state = "ready"; publish(); return
                }
                let key = try MusicPlaybackLyricCache.Key(track: metadata.track, source: candidate.source)
                let next = try lyrics.request(for: key, occurrenceID: entry.id, generation: access) {
                    try await service.lyrics(occurrenceId: entry.id, sourceKey: candidate.source.sourceKey)
                }
                let previous = request; request = next; previous?.cancel()
                do {
                    let value = try await MusicCandidateDiscoveryDeadline.run(seconds: 8) { try await next.value() }
                    try check(id, access: access); try next.checkAccess()
                    if let scope { try await accounts?.checkNativeCacheScope(scope); try check(id, access: access) }
                    guard value.state == .ready, !value.lines.isEmpty else { next.cancel(); continue }
                    try fence.perform { lines = value.lines; state = "ready"; publish() }
                    if let display, let cacheRevision {
                        await display.storeLyrics(value.lines, sourceKey: candidate.source.sourceKey,
                            songID: metadata.track.songId, revision: cacheRevision, authorization: fence)
                    }
                    return
                } catch {
                    next.cancel()
                    try check(id, access: access)
                    let failure = MusicServiceError.sanitized(error)
                    if [.accessUnavailable, .staleAccess, .closed, .cancelled, .targetChanged].contains(failure) { throw error }
                }
            }
            try check(id, access: access)
            state = "none"; lines = []; publish()
        } catch {
            guard session == id, generation == access, available, !closed else { return }
            state = enabled ? "none" : "disabled"; lines = []; publish()
        }
    }

    func artworkFailed(_ url: URL?) {
        guard available, !closed, let entry, let url, coverURL == url,
              let selector = service as? any MusicArtworkSelecting else { return }
        failedArtwork.insert(url)
        let id = session, access = generation, attempted = failedArtwork
        artworkTask?.cancel()
        artworkTask = Task { [weak self] in
            do {
                let next = try await selector.failedArtwork(songID: entry.track.songId, url: url, attempted: attempted)
                guard let self else { return }
                try self.check(id, access: access)
                guard self.coverURL == url else { return }
                self.coverURL = next; self.publish()
            } catch {}
        }
    }
    func artworkLoaded(_ url: URL?, resourceURL: URL) {
        guard available, !closed, let entry, let url, coverURL == url,
              let selector = service as? any MusicArtworkSelecting else { return }
        let id = session, access = generation
        artworkTask?.cancel()
        artworkTask = Task { [weak self] in
            do {
                let selected = try await selector.succeededArtwork(songID: entry.track.songId,
                    url: resourceURL, replacing: url)
                guard let self else { return }
                try self.check(id, access: access)
                guard self.coverURL == url, let track = self.track else { return }
                self.coverURL = selected
                self.track = .init(ref: track.ref, songId: track.songId, title: track.title,
                    artist: track.artist, album: track.album, duration: track.duration, sources: track.sources,
                    artwork: selected, artistNames: track.artistNames, sourcePosition: track.sourcePosition)
                self.publish()
            } catch {}
        }
    }
    func close() async {
        guard !closed else { return }
        let load = task, artwork = artworkTask, revoked = revocations
        invalidate(); closed = true; available = false; lyrics.close(); apply = nil
        await load?.value; await artwork?.value; await revoked?.value; await lyrics.flush()
    }
    deinit { task?.cancel(); artworkTask?.cancel(); request?.cancel(); revocations?.cancel(); authorization?.revoke(); lyrics.close() }
}
#endif
