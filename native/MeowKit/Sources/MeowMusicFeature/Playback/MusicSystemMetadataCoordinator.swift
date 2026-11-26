// SPDX-License-Identifier: GPL-3.0-only
#if os(iOS) || os(macOS)
import Foundation
import Combine
import MeowContracts
import MeowPlayback

// Product-lifetime projection of playback state into system media metadata.
// Reference: iWebPlus b900c259 static/utils.js:424-440. The ORIGINAL lyric is
// the title; the favorite-marked song and artist become the second line.
// Translation and player/background visibility do not affect system metadata.
@MainActor public final class MusicSystemMetadataCoordinator {
    private let model: MusicViewModel
    private let playback: PlaybackController
    private let appearance: MusicAppearancePreferencesStore
    private let favorites: MusicMiniPlayerFavorites
    private nonisolated let lifetime: Lifetime
    private var observations: Set<AnyCancellable> {
        get { lifetime.observations }
        set { lifetime.observations = newValue }
    }
    private(set) var pendingUpdate: Task<Void, Never>? {
        get { lifetime.pendingUpdate }
        set { lifetime.pendingUpdate = newValue }
    }
    private var updateRevision = UUID()
    private var closed: Bool {
        get { lifetime.closed }
        set { lifetime.closed = newValue }
    }
    private var binding: MusicSystemMetadataBinding? {
        get { lifetime.binding }
        set { lifetime.binding = newValue }
    }
    private var publishedAttempt: PlayAttempt? {
        get { lifetime.publishedAttempt }
        set { lifetime.publishedAttempt = newValue }
    }
    private var publishedMetadata: PlaybackNowPlayingMetadata? {
        get { lifetime.publishedMetadata }
        set { lifetime.publishedMetadata = newValue }
    }
    private var observedLyricIndex: Int?

    /// Hold one per music product owner, alongside the same model, shared
    /// playback controller and appearance instance used by mini/player/settings.
    public init(model: MusicViewModel, playback: PlaybackController,
                appearance: MusicAppearancePreferencesStore) {
        self.model = model; self.playback = playback; self.appearance = appearance
        self.lifetime = Lifetime(playback: playback, appearance: appearance)
        favorites = model.favorites

        Publishers.MergeMany(model.playbackChanges, model.metadataChanges, model.libraryChanges,
                             model.noticeChanges).sink { [weak self] in
            self?.observedLyricIndex = nil
            self?.schedule()
        }.store(in: &observations)
        model.clock.$position.sink { [weak self] position in
            guard let self, !self.closed,
                  let entry = self.model.current,
                  self.model.metadataOccurrenceID == entry.id,
                  !self.model.lyricLines.isEmpty else { return }
            let nextIndex = MusicLyricTimeline.activeIndex(lines: self.model.lyricLines, at: position)
            if nextIndex != self.observedLyricIndex {
                self.observedLyricIndex = nextIndex
                self.schedule()
            }
        }.store(in: &observations)
        appearance.$preferences.removeDuplicates().dropFirst().sink { [weak self] _ in self?.schedule() }.store(in: &observations)
        favorites.favoriteChanges.sink { [weak self] in self?.schedule() }.store(in: &observations)
        playback.$snapshot
            .map { ($0.owner, $0.resourceID, $0.phase) }
            .removeDuplicates { $0 == $1 }
            .dropFirst()
            .sink { [weak self] _ in self?.schedule() }
            .store(in: &observations)

        model.playbackState.$state.sink { [weak self] state in
            guard let self else { return }
            if let binding = self.binding, state.currentId != binding.occurrenceID { self.revoke() }
        }.store(in: &observations)
        model.playbackState.$queue.sink { [weak self] queue in
            guard let self, let binding = self.binding else { return }
            guard let entry = queue.first(where: { $0.id == binding.occurrenceID }),
                  binding.matches(entry.track) else { self.revoke(); return }
        }.store(in: &observations)
        playback.$currentAttempt.sink { [weak self] next in
            guard let self else { return }
            if self.binding?.attempt != next, self.binding != nil { self.revoke() }
            if let next, next.owner == "music" {
                _ = self.playback.clearNowPlayingMetadata(attempt: next)
            } else if next != nil {
                self.appearance.setSystemArtworkEnabled(false)
            }
            self.schedule()
        }.store(in: &observations)
        refresh()
    }

    /// Idempotent and synchronous. Does not stop playback or clear a successor
    /// domain's system information. No queued callback can reopen this bridge.
    public func close() {
        updateRevision = UUID()
        lifetime.close()
    }

    private func schedule() {
        guard !closed, pendingUpdate == nil else { return }
        let revision = updateRevision
        pendingUpdate = Task { @MainActor [weak self] in
            guard let self, !Task.isCancelled, !self.closed, self.updateRevision == revision else { return }
            self.pendingUpdate = nil
            self.refresh()
        }
    }

    private func refresh() {
        guard !closed else { return }
        if model.isClosed {
            revoke(); suppressCurrentMusicAttempt()
            return
        }
        guard let entry = model.current, let attempt = playback.currentAttempt,
              let next = MusicSystemMetadataBinding(
                entry: entry, attempt: attempt, activeAttemptID: model.activeNativeAttemptID,
                activeResourceID: model.activeResourceID, activeOccurrenceID: model.activeOccurrenceID,
                snapshot: playback.snapshot),
              playback.isCurrent(attempt) else {
            if binding != nil { revoke() }
            return
        }
        if let old = binding, old != next { revoke() }
        binding = next
        appearance.selectSystemArtwork(model.displayTrack ?? entry.track)

        let value = Self.metadata(
            track: model.displayTrack ?? entry.track,
            lines: model.metadataOccurrenceID == entry.id ? model.lyricLines : [], position: model.position,
            preferences: appearance.preferences,
            favorite: favorites.loaded && favorites.isFavorite(entry.track),
            artwork: appearance.displayedArtwork
        )
        guard publishedAttempt != attempt || publishedMetadata != value else { return }
        if playback.setNowPlayingMetadata(value, attempt: attempt) {
            publishedAttempt = attempt; publishedMetadata = value
        }
    }

    static func metadata(track: MusicRemoteTrack, lines: [MusicLyricLine], position: Double,
                         preferences: MusicAppearancePreferences, favorite: Bool,
                         artwork: MusicDisplayedArtwork?) -> PlaybackNowPlayingMetadata {
        let lyric = MusicLyricTimeline.activeIndex(lines: lines, at: position).map { lines[$0].text }
        let text = preferences.lockScreenMetadata(songTitle: track.title, songArtist: track.artist,
                                                  lyric: lyric, isFavorite: favorite)
        let image = artwork.flatMap { $0.matches(track) ? PlaybackNowPlayingArtwork(data: $0.data) : nil }
        return .init(title: text.title, artist: text.artist, album: text.album, artwork: image)
    }

    private func suppressCurrentMusicAttempt() {
        guard let attempt = playback.currentAttempt, attempt.owner == "music" else { return }
        _ = playback.clearNowPlayingMetadata(attempt: attempt)
    }

    private func revoke() {
        lifetime.revoke()
    }

    nonisolated deinit {
        let holder = lifetime
        if Thread.isMainThread {
            MainActor.assumeIsolated { holder.close() }
        } else {
            DispatchQueue.main.async { MainActor.assumeIsolated { holder.close() } }
        }
    }

    @MainActor
    private final class Lifetime {
        let playback: PlaybackController
        let appearance: MusicAppearancePreferencesStore
        var observations = Set<AnyCancellable>()
        var pendingUpdate: Task<Void, Never>?
        var closed = false
        var binding: MusicSystemMetadataBinding?
        var publishedAttempt: PlayAttempt?
        var publishedMetadata: PlaybackNowPlayingMetadata?

        init(playback: PlaybackController, appearance: MusicAppearancePreferencesStore) {
            self.playback = playback
            self.appearance = appearance
        }

        func revoke() {
            let old = publishedAttempt ?? binding?.attempt
            binding = nil; publishedAttempt = nil; publishedMetadata = nil
            appearance.setSystemArtworkEnabled(false)
            if let old { _ = playback.clearNowPlayingMetadata(attempt: old) }
        }

        func close() {
            guard !closed else { return }
            closed = true
            observations.removeAll()
            pendingUpdate?.cancel(); pendingUpdate = nil
            revoke()
        }
    }
}

/// Domain admission, separate from the shared controller's generic exact-
/// attempt fence. A reused URL/resource/song is not ownership of a new attempt.
struct MusicSystemMetadataBinding: Equatable {
    let attempt: PlayAttempt
    let occurrenceID: String
    let songKey: String

    init?(entry: MusicQueueEntry, attempt: PlayAttempt, activeAttemptID: UUID?,
          activeResourceID: String?, activeOccurrenceID: String?,
          snapshot: PlaybackSnapshot) {
        guard attempt.owner == "music", attempt.id == activeAttemptID,
              attempt.resourceID == activeResourceID, entry.id == activeOccurrenceID,
              snapshot.owner == attempt.owner, snapshot.resourceID == attempt.resourceID,
              snapshot.phase != .idle, snapshot.phase != .failed else { return nil }
        self.attempt = attempt; occurrenceID = entry.id
        songKey = MusicAppearanceSongIdentity(track: entry.track).stableKey
    }

    func matches(_ track: MusicRemoteTrack) -> Bool {
        songKey.utf16.elementsEqual(MusicAppearanceSongIdentity(track: track).stableKey.utf16)
    }
}
#endif
