// SPDX-License-Identifier: GPL-3.0-only
#if os(iOS) || os(macOS)
import Foundation
import Combine
import MeowContracts
import MeowPlayback

// Product-lifetime projection of playback state into system media metadata.
// Reference: iWebPlus b900c259 static/utils.js:424-440. The ORIGINAL lyric is
// the title; the favorite-marked song title becomes artist. Translation and
// player/background visibility preferences do not affect system metadata.
@MainActor public final class MusicSystemMetadataCoordinator {
    private let model: MusicViewModel
    private let playback: PlaybackController
    private let appearance: MusicAppearancePreferencesStore
    private let favorites: MusicMiniPlayerFavorites
    private var observations = Set<AnyCancellable>()
    private(set) var pendingUpdate: Task<Void, Never>?
    private var updateRevision = UUID()
    private var closed = false
    private var available: Bool
    private var modelClosed: Bool
    private var binding: MusicSystemMetadataBinding?
    private var publishedAttempt: PlayAttempt?
    private var publishedMetadata: PlaybackNowPlayingMetadata?
    private var favoritesActive = false
    private var favoritesNeedReload = true

    /// Hold one per music product owner, alongside the same model, shared
    /// playback controller and appearance instance used by mini/player/settings.
    public init(model: MusicViewModel, playback: PlaybackController,
                appearance: MusicAppearancePreferencesStore) {
        self.model = model; self.playback = playback; self.appearance = appearance
        favorites = MusicMiniPlayerFavorites(model: model)
        available = model.isAvailable; modelClosed = model.isClosed

        // ObservableObject emits BEFORE a setter finishes. Coalesce ordinary
        // updates onto the actor's next turn, then read one coherent snapshot.
        model.objectWillChange.sink { [weak self] in self?.schedule() }.store(in: &observations)
        model.clock.objectWillChange.sink { [weak self] in self?.schedule() }.store(in: &observations)
        appearance.objectWillChange.sink { [weak self] in self?.schedule() }.store(in: &observations)
        favorites.objectWillChange.sink { [weak self] in self?.schedule() }.store(in: &observations)
        playback.objectWillChange.sink { [weak self] in self?.schedule() }.store(in: &observations)

        // Privacy barriers use the emitted NEW value synchronously, not the
        // old stored property and not a deferred receive(on:)/polling timer.
        model.$isAvailable.sink { [weak self] value in
            guard let self else { return }
            self.available = value
            if !value { self.revoke(); self.suppressCurrentMusicAttempt() }
            self.schedule()
        }.store(in: &observations)
        model.$isClosed.sink { [weak self] value in
            guard let self else { return }
            self.modelClosed = value
            if value { self.revoke(); self.suppressCurrentMusicAttempt() }
            self.schedule()
        }.store(in: &observations)
        model.$state.sink { [weak self] state in
            guard let self else { return }
            if let binding = self.binding, state.currentId != binding.occurrenceID { self.revoke() }
        }.store(in: &observations)
        model.$queue.sink { [weak self] queue in
            guard let self, let binding = self.binding else { return }
            guard let entry = queue.first(where: { $0.id == binding.occurrenceID }),
                  binding.matches(entry.track) else { self.revoke(); return }
        }.store(in: &observations)
        // readCollectionPage republishes its catalogue. Identical snapshots
        // must not recursively trigger another favorites read.
        model.$collections.removeDuplicates().sink { [weak self] _ in
            self?.favoritesNeedReload = true; self?.schedule()
        }.store(in: &observations)
        playback.$currentAttempt.sink { [weak self] next in
            guard let self else { return }
            if self.binding?.attempt != next, self.binding != nil { self.revoke() }
            if let next, next.owner == "music" {
                // The native attempt is published before the model receives
                // playAttempt's return value. Stay suppressed until BOTH agree.
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
        guard !closed else { return }
        closed = true; updateRevision = UUID()
        observations.removeAll()
        pendingUpdate?.cancel(); pendingUpdate = nil
        revoke()
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
        guard available, !modelClosed, model.isAvailable, !model.isClosed,
              let entry = model.current, let attempt = playback.currentAttempt,
              let next = MusicSystemMetadataBinding(
                entry: entry, attempt: attempt, activeAttemptID: model.activeNativeAttemptID,
                activeResourceID: model.activeResourceID, activeOccurrenceID: model.activeOccurrenceID,
                access: model.accountAccessGeneration, snapshot: playback.snapshot),
              playback.isCurrent(attempt) else {
            if binding != nil || !available || modelClosed { revoke() }
            return
        }
        if let old = binding, old != next { revoke() }
        binding = next
        appearance.selectSystemArtwork(model.displayTrack ?? entry.track)
        if !favoritesActive {
            favoritesActive = true; favoritesNeedReload = false
            favorites.setActive(true)
        } else if favoritesNeedReload {
            favoritesNeedReload = false; favorites.reload()
        }

        let value = Self.metadata(
            track: model.displayTrack ?? entry.track, lines: model.lyricLines, position: model.position,
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
        let text = preferences.lockScreenMetadata(songTitle: track.title, lyric: lyric, isFavorite: favorite)
        let image = artwork.flatMap { $0.matches(track) ? PlaybackNowPlayingArtwork(data: $0.data) : nil }
        return .init(title: text.title, artist: text.artist, album: text.album, artwork: image)
    }

    private func suppressCurrentMusicAttempt() {
        guard let attempt = playback.currentAttempt, attempt.owner == "music" else { return }
        _ = playback.clearNowPlayingMetadata(attempt: attempt)
    }

    private func revoke() {
        let old = publishedAttempt ?? binding?.attempt
        binding = nil; publishedAttempt = nil; publishedMetadata = nil
        if favoritesActive {
            favoritesActive = false; favoritesNeedReload = true
            favorites.suspend()
        }
        appearance.setSystemArtworkEnabled(false)
        if let old { _ = playback.clearNowPlayingMetadata(attempt: old) }
    }

    isolated deinit { close() }
}

/// Domain admission, separate from the shared controller's generic exact-
/// attempt fence. A reused URL/resource/song is not ownership of a new attempt.
struct MusicSystemMetadataBinding: Equatable {
    let attempt: PlayAttempt
    let occurrenceID: String
    let access: UUID
    let songKey: String
    let artworkID: String

    init?(entry: MusicQueueEntry, attempt: PlayAttempt, activeAttemptID: UUID?,
          activeResourceID: String?, activeOccurrenceID: String?, access: UUID,
          snapshot: PlaybackSnapshot) {
        guard attempt.owner == "music", attempt.id == activeAttemptID,
              attempt.resourceID == activeResourceID, entry.id == activeOccurrenceID,
              snapshot.owner == attempt.owner, snapshot.resourceID == attempt.resourceID,
              snapshot.phase != .idle, snapshot.phase != .failed else { return nil }
        self.attempt = attempt; occurrenceID = entry.id; self.access = access
        songKey = MusicAppearanceSongIdentity(track: entry.track).stableKey
        artworkID = entry.track.artwork?.absoluteString ?? ""
    }

    func matches(_ track: MusicRemoteTrack) -> Bool {
        songKey.utf16.elementsEqual(MusicAppearanceSongIdentity(track: track).stableKey.utf16) &&
            artworkID.utf16.elementsEqual((track.artwork?.absoluteString ?? "").utf16)
    }
}
#endif
