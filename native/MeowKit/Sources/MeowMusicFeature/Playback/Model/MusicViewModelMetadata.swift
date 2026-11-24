#if os(iOS) || os(macOS)
import Foundation
import MeowContracts
import MeowDebug
import MeowPlayback
import MeowStorage

@MainActor
extension MusicViewModel {
    func observeSongRepository() {
        songRepositoryObservation?.cancel(); songRepositoryObservation = nil
        guard !isClosed, let repository = service as? any MusicSongRepositoryReading else { return }
        let access = lifecycle
        songRepositoryObservation = Task { [weak self] in
            let changes = await repository.songChanges()
            await self?.refreshRepositoryProjections(repository, access: access)
            for await changed in changes {
                guard !Task.isCancelled else { return }
                await self?.refreshRepositoryProjections(repository, access: access, changed: changed)
            }
        }
    }
    func refreshRepositoryProjections(_ repository: any MusicSongRepositoryReading, access: UUID, changed: Set<String>? = nil) async {
        guard !Task.isCancelled, !isClosed, lifecycle == access else { return }
        let projection = UUID()
        let referenced = Set(queue.map { $0.track.songId } + playedSongs.map { $0.track.songId })
        let ids = changed.map { referenced.intersection($0) } ?? referenced
        guard !ids.isEmpty else { return }
        songRepositoryProjectionID = projection
        do {
            let songs = try await repository.repositoryTracks(songIDs: ids)
            try check(access)
            guard songRepositoryProjectionID == projection else { return }
            playbackState.queue = queue.map {
                guard songs[$0.track.songId] != nil else { return $0 }
                return .init(id: $0.id, track: $0.track.projected(songs), sourceTitle: $0.sourceTitle,
                    remoteSessionID: $0.remoteSessionID, displayIdentity: $0.displayIdentity)
            }
            libraryState.playedSongs = playedSongs.map {
                guard songs[$0.track.songId] != nil else { return $0 }
                return .init(date: $0.date, at: $0.at, track: $0.track.projected(songs), displayIdentity: $0.displayIdentity)
            }
            if let current, songs[current.track.songId] != nil { playbackMetadata.project(current.track) }
        } catch {
            if !Task.isCancelled, MusicServiceError.sanitized(error) != .cancelled {
                DebugRuntime.logError(.musicLibrary, sub: "model", event: "refreshRepositoryProjections.error", error: error)
            }
        }
    }
    func configureMetadata() {
        playbackMetadata.configure(service: service, accounts: accounts) { [weak self] value in
            guard let self else { return }
            self.metadataOccurrenceID = value.occurrenceID
            metadataState.lyricLines = value.lines; metadataState.lyricState = value.lyricState
            metadataState.artworkURL = value.artwork; metadataState.track = value.track
        }
    }
    public func setPresentationCache(_ cache: (any MusicPresentationCaching)?) {
        playbackMetadata.setDisplayCache(cache)
        #if os(iOS)
        if let cache { artworkImages.configure(cache: cache, selector: service as? any MusicArtworkSelecting) }
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
        guard !isClosed else { return }
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
}
#endif
