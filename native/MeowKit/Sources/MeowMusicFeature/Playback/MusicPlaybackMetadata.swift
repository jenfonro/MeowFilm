import Foundation
#if os(iOS) || os(macOS)
import Combine

/// One metadata session per recording/occurrence, independent of audio
/// resolution. Audio quality/source changes do not restart lyrics or artwork.
@MainActor final class MusicPlaybackMetadata {
    struct Presentation {
        let occurrenceID: String?
        let lyricState: String
        let lines: [MusicLyricLine]
        let artwork: URL?
        let cachedArtwork: Data?
    }
    private var service: (any MusicServing)?
    private var accounts: (any MusicNativeCacheScopeProviding)?
    private var display: (any MusicPlaybackDisplayCaching)?
    private var apply: (@MainActor (Presentation) -> Void)?
    private let lyrics = MusicPlaybackLyricCache()
    private var generation = UUID()
    private var available = false, closed = false
    private var session = UUID()
    private var entry: MusicQueueEntry?
    private var enabled = true
    private var restoreOnly = false
    private var fallbackSourceKey: String?
    private var request: MusicPlaybackLyricCache.Request?
    private var task: Task<Void, Never>?
    private var revocations: Task<Void, Never>?
    private var authorization: MusicQueueAuthorization?
    private var identity: MusicPlaybackDisplayIdentity?
    private var candidates: [MusicMetadataCandidate] = []
    private var coverIndex = 0
    private var state = "idle"
    private var lines: [MusicLyricLine] = []
    private var coverData: Data?
    private var contentFingerprints: [String: String] = [:]

    func configure(service: (any MusicServing)?, accounts: (any MusicAccountsServing)?,
                   apply: @escaping @MainActor (Presentation) -> Void) {
        self.service = service; self.accounts = accounts as? any MusicNativeCacheScopeProviding
        self.apply = apply
    }
    func setDisplayCache(_ cache: (any MusicPlaybackDisplayCaching)?) { display = cache }
    func setAccess(available: Bool, generation: UUID) {
        guard !closed else { return }
        if self.available != available || self.generation != generation {
            invalidate(); contentFingerprints.removeAll()
        }
        self.available = available; self.generation = generation
        lyrics.setAccess(available: available, generation: generation)
    }
    private func invalidate() {
        session = UUID(); task?.cancel(); task = nil
        request?.cancel(); request = nil; revocations?.cancel(); revocations = nil
        authorization?.revoke(); authorization = nil
        entry = nil; identity = nil; candidates = []; coverIndex = 0; coverData = nil
        state = "idle"; lines = []; publish()
    }
    private func publish() {
        apply?(.init(occurrenceID: entry?.id, lyricState: state, lines: lines,
            artwork: candidates.indices.contains(coverIndex) ? candidates[coverIndex].artwork : nil,
            cachedArtwork: coverData))
    }
    private func check(_ id: UUID, access: UUID) throws {
        try Task.checkCancellation()
        guard available, !closed, session == id, generation == access else { throw MusicServiceError.cancelled }
        try authorization?.check()
    }
    func clearSelection() { invalidate() }

    /// Safe to call before audio preparation; it never resolves or plays media.
    func select(_ entry: MusicQueueEntry, lyricsEnabled: Bool, restoreOnly: Bool = false,
                fallbackSourceKey: String? = nil, force: Bool = false) {
        guard available, !closed else { return }
        if !force, self.entry == entry, enabled == lyricsEnabled, authorization?.isActive != false,
           service is any MusicMetadataPreferencesServing || self.fallbackSourceKey == fallbackSourceKey,
           self.restoreOnly == restoreOnly || !self.restoreOnly { return }
        if force { lyrics.removeAll() }
        invalidate()
        self.entry = entry; enabled = lyricsEnabled; self.restoreOnly = restoreOnly
        self.fallbackSourceKey = fallbackSourceKey
        state = lyricsEnabled ? "loading" : "disabled"; publish()
        let id = session, access = generation
        task = Task { [weak self] in
            guard let self else { return }
            await self.load(entry, session: id, access: access, fallbackSourceKey: fallbackSourceKey)
            if self.session == id { self.task = nil }
        }
    }
    func flush() async { await task?.value }

    private func load(_ entry: MusicQueueEntry, session id: UUID, access: UUID,
                      fallbackSourceKey: String?) async {
        do {
            guard let service else { throw MusicServiceError.unavailable }
            let metadata: MusicMetadataCandidates?
            if let source = service as? any MusicMetadataPreferencesServing {
                metadata = try await source.metadataCandidates(occurrenceID: entry.id)
            } else { metadata = nil }
            try check(id, access: access)
            if let metadata {
                guard metadata.occurrenceID == entry.id, metadata.track == entry.track else {
                    throw MusicServiceError.targetChanged
                }
                candidates = metadata.candidates
                if let old = contentFingerprints[entry.track.songId], old != metadata.contentFingerprint {
                    lyrics.removeAll()
                }
                if contentFingerprints.count >= 100 { contentFingerprints.removeAll() }
                contentFingerprints[entry.track.songId] = metadata.contentFingerprint
            } else {
                // Compatibility services retain their exact requested source;
                // they do not gain an implicit metadata search policy.
                candidates = entry.track.sources.filter { $0.sourceKey == fallbackSourceKey }
                    .map { .init(source: $0, artwork: entry.track.artwork) }
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
            // No persistent guest namespace is fabricated for a compatibility
            // service without a local account-scope provider.
            if let scope, let metadata {
                identity = .init(scope: scope.scope, occurrenceID: entry.id, songID: entry.track.songId,
                                 contentFingerprint: metadata.contentFingerprint)
            }
            let saved: MusicPlaybackDisplaySnapshot?
            if let display, let identity { saved = await display.read(identity, authorization: fence) }
            else { saved = nil }
            try check(id, access: access)
            if let scope { try await accounts?.checkNativeCacheScope(scope); try check(id, access: access) }
            if let key = saved?.artworkSourceKey,
               let index = candidates.firstIndex(where: { $0.source.sourceKey == key }),
               let data = saved?.artwork {
                coverIndex = index; coverData = data
            } else {
                coverIndex = candidates.firstIndex(where: { $0.artwork != nil }) ?? candidates.count
            }
            publish()
            guard enabled else { state = "disabled"; publish(); return }
            if restoreOnly {
                if let saved, let key = saved.lyricSourceKey,
                   candidates.contains(where: { $0.source.sourceKey == key }) {
                    lines = saved.lines
                }
                state = lines.isEmpty ? "none" : "ready"; publish(); return
            }
            for candidate in candidates {
                try check(id, access: access)
                if let saved, saved.lyricSourceKey == candidate.source.sourceKey, !saved.lines.isEmpty {
                    lines = saved.lines; state = "ready"; publish(); return
                }
                let key = try MusicPlaybackLyricCache.Key(track: entry.track, source: candidate.source,
                                                          contentRevision: metadata?.contentRevision)
                let next = try lyrics.request(for: key, occurrenceID: entry.id, generation: access) {
                    try await service.lyrics(occurrenceId: entry.id, sourceKey: candidate.source.sourceKey)
                }
                // Register before cancellation so same-key consumers can join.
                let previous = request; request = next; previous?.cancel()
                do {
                    let value = try await MusicCandidateDiscoveryDeadline.run(seconds: 8) { try await next.value() }
                    try check(id, access: access); try next.checkAccess()
                    if let scope { try await accounts?.checkNativeCacheScope(scope); try check(id, access: access) }
                    guard value.occurrenceId == entry.id else { throw MusicServiceError.lyricIdentityMismatch }
                    guard value.state == .ready, !value.lines.isEmpty else { next.cancel(); continue }
                    try fence.perform { lines = value.lines; state = "ready"; publish() }
                    if let display, let identity {
                        await display.storeLyrics(value.lines, sourceKey: candidate.source.sourceKey,
                                                  identity: identity, authorization: fence)
                    }
                    return
                } catch {
                    next.cancel()
                    try check(id, access: access)
                    let failure = MusicServiceError.sanitized(error)
                    if [.accessUnavailable, .staleAccess, .closed, .cancelled, .targetChanged].contains(failure) { throw error }
                    // No title search or alternate recording: advance only to
                    // the next already-mapped, policy-enabled metadata source.
                }
            }
            try check(id, access: access)
            state = "none"; lines = []; publish()
        } catch {
            guard session == id, generation == access, available, !closed else { return }
            state = enabled ? "none" : "disabled"; lines = []; publish()
        }
    }

    /// Called only for the current image's failure, not its loading placeholder.
    func artworkFailed(_ url: URL?) {
        guard available, !closed, authorization?.isActive == true,
              candidates.indices.contains(coverIndex), candidates[coverIndex].artwork == url else { return }
        coverData = nil
        repeat { coverIndex += 1 }
        while candidates.indices.contains(coverIndex) && candidates[coverIndex].artwork == nil
        publish()
    }
    func capturedArtwork(_ data: Data, url: URL?) {
        guard available, !closed, data.count <= 8 * 1024 * 1024, !data.isEmpty,
              let authorization, authorization.isActive, let identity, let display,
              candidates.indices.contains(coverIndex), candidates[coverIndex].artwork == url else { return }
        let sourceKey = candidates[coverIndex].source.sourceKey
        coverData = data; publish()
        Task { await display.storeArtwork(data, sourceKey: sourceKey, identity: identity, authorization: authorization) }
    }
    func close() {
        guard !closed else { return }
        invalidate(); closed = true; available = false; lyrics.close(); apply = nil
    }
    deinit { task?.cancel(); request?.cancel(); revocations?.cancel(); authorization?.revoke(); lyrics.close() }
}
#endif
