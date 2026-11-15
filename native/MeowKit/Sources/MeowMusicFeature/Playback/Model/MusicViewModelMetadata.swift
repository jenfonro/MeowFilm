#if os(iOS) || os(macOS)
import Foundation
import MeowContracts
import MeowPlayback
import MeowStorage

@MainActor
extension MusicViewModel {
    func observeSongRepository() {
        songRepositoryObservation?.cancel(); songRepositoryObservation = nil
        guard isAvailable, !isClosed, let repository = service as? any MusicSongRepositoryReading else { return }
        let access = lifecycle
        songRepositoryObservation = Task { [weak self] in
            let changes = await repository.songChanges()
            await self?.refreshRepositoryProjections(repository, access: access)
            for await _ in changes {
                guard !Task.isCancelled else { return }
                await self?.refreshRepositoryProjections(repository, access: access)
            }
        }
    }
    func refreshRepositoryProjections(_ repository: any MusicSongRepositoryReading, access: UUID) async {
        guard !Task.isCancelled, isAvailable, !isClosed, lifecycle == access else { return }
        let projection = UUID()
        songRepositoryProjectionID = projection
        let ids = Set(queue.map { $0.track.songId } + playedSongs.map { $0.track.songId })
        do {
            let songs = try await repository.repositoryTracks(songIDs: ids)
            try check(access)
            guard songRepositoryProjectionID == projection else { return }
            playbackState.queue = queue.map {
                .init(id: $0.id, track: $0.track.projected(songs), sourceTitle: $0.sourceTitle,
                    remoteSessionID: $0.remoteSessionID, displayIdentity: $0.displayIdentity)
            }
            libraryState.playedSongs = playedSongs.map {
                .init(date: $0.date, at: $0.at, track: $0.track.projected(songs), displayIdentity: $0.displayIdentity)
            }
            if let current { playbackMetadata.project(current.track) }
        } catch { /* A display read does not change queue or media admission. */ }
    }
    func configureMetadata() {
        playbackMetadata.configure(service: service, accounts: accounts) { [weak self] value in
            guard let self else { return }
            self.metadataOccurrenceID = value.occurrenceID
            metadataState.lyricLines = value.lines; metadataState.lyricState = value.lyricState
            metadataState.artworkURL = value.artwork; metadataState.track = value.track
        }
        playbackMetadata.setAccess(available: isAvailable, generation: lifecycle)
    }
    public func setPresentationCache(_ cache: (any MusicPresentationCaching)?) {
        playbackMetadata.setDisplayCache(cache)
        #if os(iOS)
        if let cache { MusicArtworkImages.shared.configure(cache: cache, selector: service as? any MusicArtworkSelecting) }
        #endif
    }
    public func artworkFailed(_ url: URL?) { playbackMetadata.artworkFailed(url) }
    public func artworkLoaded(_ url: URL, resourceURL: URL) { playbackMetadata.artworkLoaded(url, resourceURL: resourceURL) }
    func beginPlaybackMetadata(restoreOnly: Bool = false, force: Bool = false, refreshSources: Bool = false) {
        guard let current else { playbackMetadata.clearSelection(); return }
        playbackMetadata.select(current, lyricsEnabled: state.lyricsEnabled, restoreOnly: restoreOnly,
                                force: force, refreshSources: refreshSources)
    }
    public func metadataPreferencesChanged() async {
        let own = lifecycle
        do {
            let value = try await requireService().state(); try check(own); playbackState.state = value
            beginPlaybackMetadata(force: true)
        } catch { report(error, epoch: own, operation: "读取歌词与封面设置", kind: .settings) }
    }
    func scheduleMetadataPreferencesRefresh() {
        guard isAvailable, !isClosed else { return }
        metadataPreferencesRefreshRequested = true
        guard metadataPreferencesRefreshTask == nil else { return }
        metadataPreferencesRefreshRequested = false
        metadataPreferencesRefreshTask = Task { [weak self] in
            guard let self else { return }
            await self.metadataPreferencesChanged()
            self.metadataPreferencesRefreshTask = nil
            if self.metadataPreferencesRefreshRequested {
                self.scheduleMetadataPreferencesRefresh()
            }
        }
    }
    func flushMetadataPreferencesRefresh() async {
        while let pending = metadataPreferencesRefreshTask { await pending.value }
    }
}
#endif
