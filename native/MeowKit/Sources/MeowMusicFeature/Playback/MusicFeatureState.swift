#if os(iOS) || os(macOS)
import Foundation
import Combine
import MeowContracts
import MeowPlayback

/// The feature coordinator applies service receipts; these owners retain the
/// state for their own consumers without publishing unrelated feature changes.
@MainActor
public final class MusicAccessState: ObservableObject {
    @Published public internal(set) var isAvailable = true
    @Published public internal(set) var isClosed = false
    @Published public internal(set) var generation = UUID()
}

@MainActor
public final class MusicLibraryState: ObservableObject {
    @Published public internal(set) var collections: [MusicCollection] = []
    @Published public internal(set) var platforms: [MusicPlatform] = []
    @Published public internal(set) var isLoading = false
    @Published public internal(set) var isLoaded = false
    @Published public internal(set) var playedSongs: [MusicPlayedSong] = []
    @Published public internal(set) var playedPlaylists: [MusicPlayedPlaylist] = []
    var songsRead = UUID(), playlistsRead = UUID()
}

@MainActor
public final class MusicPlaybackFeatureState: ObservableObject {
    @Published public internal(set) var state = MusicPlaybackState()
    @Published public internal(set) var queue: [MusicQueueEntry] = []
    @Published public internal(set) var phase: PlaybackPhase = .idle
    @Published public internal(set) var isResolving = false
    @Published public internal(set) var isNavigating = false
    @Published public internal(set) var isChangingQueue = false
    @Published public internal(set) var options: [MusicPlaybackOption] = []
    @Published public internal(set) var scriptSourceError: String?
    @Published public internal(set) var isPreparingRemoteQueue = false
    @Published public internal(set) var isFMActive = false
    @Published public internal(set) var similarMode: MusicSimilarModeSnapshot?
    @Published public internal(set) var canEnterSimilarMode = false
    @Published public internal(set) var isChangingSimilarMode = false
    @Published public internal(set) var activeHomeFM: MusicHomeAction?
}

@MainActor
public final class MusicMetadataState: ObservableObject {
    @Published public internal(set) var lyricState = "idle"
    @Published public internal(set) var lyricLines: [MusicLyricLine] = []
    @Published public internal(set) var artworkURL: URL?
    @Published public internal(set) var artworkData: Data?
    @Published internal var occurrenceID: String?
}

@MainActor
public final class MusicNoticeState: ObservableObject {
    @Published public var error: String?
    @Published public internal(set) var queueNotice: String?
}

/// Settings have their own view projection. Progress persistence and queue
/// revisions must not invalidate a settings form.
private struct MusicSettingsProjection: Equatable {
    let revision: Int
    let mode: MusicPlaybackMode
    let qualities, qualityOrder, spatial, spatialOrder: [String]
    let lyrics, translation: Bool

    init(_ state: MusicPlaybackState) {
        revision = state.settingsRevision
        mode = state.mode
        qualities = state.qualityEnabled
        qualityOrder = state.qualityOrder
        spatial = state.spatialEnabled
        spatialOrder = state.spatialOrder
        lyrics = state.lyricsEnabled
        translation = state.showTranslation
    }
}

/// Only visual queue facts, not a storage revision or persisted position.
private struct MusicQueueProjection: Equatable {
    let current, next, previous, fmNext, fmPrevious, collection: String?
    let mode: MusicPlaybackMode
    let total: Int
    let canNext, canPrevious: Bool

    init(_ state: MusicPlaybackState) {
        current = state.currentId
        next = state.nextPreviewID
        previous = state.previousPreviewID
        fmNext = state.fmNextPreviewID
        fmPrevious = state.fmPreviousPreviewID
        collection = state.sourceCollectionID
        mode = state.mode
        total = state.total
        canNext = state.canNext
        canPrevious = state.canPrevious
    }
}

private func changed<P: Publisher>(_ publisher: P) -> AnyPublisher<Void, Never>
where P.Failure == Never {
    publisher.dropFirst().map { _ in () }.eraseToAnyPublisher()
}

private func distinctChanges<P: Publisher>(_ publisher: P) -> AnyPublisher<Void, Never>
where P.Failure == Never, P.Output: Equatable {
    changed(publisher.removeDuplicates())
}

private func merged(_ publishers: AnyPublisher<Void, Never>...) -> AnyPublisher<Void, Never> {
    Publishers.MergeMany(publishers).eraseToAnyPublisher()
}

public extension MusicViewModel {
    var accessChanges: AnyPublisher<Void, Never> {
        merged(distinctChanges(accessState.$isAvailable),
               distinctChanges(accessState.$isClosed),
               distinctChanges(accessState.$generation))
    }

    var searchAccessChanges: AnyPublisher<Void, Never> {
        merged(accessChanges, accountChanges.map { _ in () }.eraseToAnyPublisher())
    }

    var libraryChanges: AnyPublisher<Void, Never> {
        merged(distinctChanges(libraryState.$collections),
               distinctChanges(libraryState.$platforms),
               distinctChanges(libraryState.$isLoading),
               distinctChanges(libraryState.$isLoaded))
    }

    var settingsChanges: AnyPublisher<Void, Never> {
        distinctChanges(playbackState.$state.map(MusicSettingsProjection.init))
    }

    var queueChanges: AnyPublisher<Void, Never> {
        merged(distinctChanges(playbackState.$queue),
               distinctChanges(playbackState.$state.map(MusicQueueProjection.init)),
               distinctChanges(playbackState.$isChangingQueue),
               distinctChanges(playbackState.$isFMActive),
               changed(playbackState.$similarMode),
               distinctChanges(playbackState.$isChangingSimilarMode),
               distinctChanges(notices.$queueNotice))
    }

    var playbackChanges: AnyPublisher<Void, Never> {
        merged(queueChanges, settingsChanges,
               distinctChanges(playbackState.$phase),
               distinctChanges(playbackState.$isResolving),
               distinctChanges(playbackState.$isNavigating),
               distinctChanges(playbackState.$options),
               distinctChanges(playbackState.$scriptSourceError),
               distinctChanges(playbackState.$isPreparingRemoteQueue),
               distinctChanges(playbackState.$canEnterSimilarMode),
               distinctChanges(playbackState.$activeHomeFM))
    }

    var metadataChanges: AnyPublisher<Void, Never> {
        merged(distinctChanges(metadataState.$lyricState),
               distinctChanges(metadataState.$lyricLines),
               distinctChanges(metadataState.$artworkURL),
               distinctChanges(metadataState.$artworkData),
               distinctChanges(metadataState.$occurrenceID))
    }

    internal var fmArtworkChanges: AnyPublisher<Void, Never> {
        let current = playbackState.$state.map(\.currentId).removeDuplicates()
            .combineLatest(playbackState.$queue.removeDuplicates())
            .map { id, queue in queue.first { $0.id == id } }.removeDuplicates()
        let metadata = metadataState.$artworkURL.removeDuplicates()
            .combineLatest(metadataState.$occurrenceID.removeDuplicates())
        let ready = playbackState.$phase.map { $0 != .failed }.removeDuplicates()
            .combineLatest(playbackState.$isPreparingRemoteQueue.removeDuplicates(),
                           playbackState.$isFMActive.removeDuplicates())
            .map { valid, preparing, active in valid && !preparing && active }.removeDuplicates()
        return changed(playbackState.$activeHomeFM.map { $0?.fmConfiguration }.removeDuplicates()
            .combineLatest(current, metadata, ready)
            .map { [weak self] configuration, current, metadata, ready in
                self?.fmArtwork(configuration: configuration, current: current,
                    metadataURL: metadata.0, metadataOccurrence: metadata.1, ready: ready)
            }.removeDuplicates())
    }

    var historyChanges: AnyPublisher<Void, Never> {
        merged(changed(libraryState.$playedSongs), changed(libraryState.$playedPlaylists))
    }

    var importChanges: AnyPublisher<Void, Never> {
        importWorkflow.objectWillChange.eraseToAnyPublisher()
    }

    var noticeChanges: AnyPublisher<Void, Never> {
        merged(distinctChanges(notices.$error), distinctChanges(notices.$queueNotice))
    }
}
#endif
