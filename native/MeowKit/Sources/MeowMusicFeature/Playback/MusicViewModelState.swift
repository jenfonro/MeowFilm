#if os(iOS) || os(macOS)
import Foundation
import MeowContracts
import MeowPlayback

/// Read-only feature projections; mutations belong to the functional owners.
/// The coordinator exposes actions, not another global observation source.
public extension MusicViewModel {
    var state: MusicPlaybackState { playbackState.state }
    var platforms: [MusicPlatform] { libraryState.platforms }
    var scriptSourceError: String? { playbackState.scriptSourceError }
    var collections: [MusicCollection] { libraryState.collections }
    var queue: [MusicQueueEntry] { playbackState.queue }
    var isLoading: Bool { libraryState.isLoading }
    var isResolving: Bool { playbackState.isResolving }
    var isNavigating: Bool { playbackState.isNavigating }
    var isChangingQueue: Bool { playbackState.isChangingQueue }
    var isAvailable: Bool { accessState.isAvailable }
    var isClosed: Bool { accessState.isClosed }
    var options: [MusicPlaybackOption] { playbackState.options }
    var lyricState: String { metadataState.lyricState }
    var lyricLines: [MusicLyricLine] { metadataState.lyricLines }
    var metadataArtworkURL: URL? { metadataState.artworkURL }
    var metadataArtworkData: Data? { metadataState.artworkData }
    var phase: PlaybackPhase { playbackState.phase }
    var playedSongs: [MusicPlayedSong] { libraryState.playedSongs }
    var playedPlaylists: [MusicPlayedPlaylist] { libraryState.playedPlaylists }
    var error: String? {
        get { notices.error }
        set { notices.error = newValue }
    }
    var queueNotice: String? { notices.queueNotice }
    var isPreparingRemoteQueue: Bool { playbackState.isPreparingRemoteQueue }
    var isFMActive: Bool { playbackState.isFMActive }
    var similarMode: MusicSimilarModeSnapshot? { playbackState.similarMode }
    var canEnterSimilarMode: Bool { playbackState.canEnterSimilarMode }
    var canEnterSongRecommendations: Bool { playbackState.canEnterSongRecommendations }
    var fmPlayback: MusicFMPlaybackSnapshot? { playbackState.fmPlayback }
    var playbackSourceTitle: String {
        if isFMActive { return "私人漫游" }
        if let snapshot = similarMode, snapshot.active { return snapshot.sourceTitle }
        if queueRecommendations?.active == true { return "推荐" }
        return state.queueSourceTitle ?? "普通列表"
    }
    var isChangingSimilarMode: Bool { playbackState.isChangingSimilarMode }
    var isChangingQueueRecommendations: Bool { playbackState.isChangingQueueRecommendations }
    var queueRecommendations: MusicQueueRecommendationSnapshot? {
        guard recommendationQueueRevision == state.revision,
              recommendationAccount == searchAccessGeneration,
              playbackState.queueRecommendations?.currentEntryID == current?.id,
              !isFMActive, similarMode?.active != true else { return nil }
        return playbackState.queueRecommendations
    }
    var activeHomeFM: MusicHomeAction? { playbackState.activeHomeFM }
}
#endif
