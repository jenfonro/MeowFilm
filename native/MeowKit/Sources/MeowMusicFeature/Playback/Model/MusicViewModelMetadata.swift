#if os(iOS) || os(macOS)
import Foundation
import MeowContracts
import MeowPlayback
import MeowStorage

@MainActor
extension MusicViewModel {
    func configureMetadata() {
        playbackMetadata.configure(service: service, accounts: accounts) { [weak self] value in
            guard let self else { return }
            self.metadataOccurrenceID = value.occurrenceID
            metadataState.lyricLines = value.lines; metadataState.lyricState = value.lyricState
            metadataState.artworkURL = value.artwork; metadataState.artworkData = value.cachedArtwork
        }
        playbackMetadata.setAccess(available: isAvailable, generation: lifecycle)
    }
    public func setPlaybackDisplayCache(_ cache: (any MusicPlaybackDisplayCaching)?) {
        playbackMetadata.setDisplayCache(cache)
    }
    public func artworkFailed(_ url: URL?) { playbackMetadata.artworkFailed(url) }
    public func capturedArtwork(_ data: Data, url: URL?) { playbackMetadata.capturedArtwork(data, url: url) }
    func beginPlaybackMetadata(restoreOnly: Bool = false, fallbackSourceKey: String? = nil, force: Bool = false) {
        guard let current else { playbackMetadata.clearSelection(); return }
        playbackMetadata.select(current, lyricsEnabled: state.lyricsEnabled, restoreOnly: restoreOnly,
                                fallbackSourceKey: fallbackSourceKey, force: force)
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
