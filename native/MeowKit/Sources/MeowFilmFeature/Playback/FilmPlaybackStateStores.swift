import Combine
import Foundation
import MeowContracts

/// High-frequency playback telemetry store (position, buffering, rate, chapters, preferences).
/// Observed exclusively by active video player UI (`FilmPlayerViewController`) so playback
/// ticks never wake browsing or detail pages.
@MainActor
public final class FilmMediaStateStore: ObservableObject {
    public let didChange = PassthroughSubject<Void, Never>()
    @Published public internal(set) var mediaState = FilmMediaState() {
        didSet { didChange.send() }
    }
    @Published public internal(set) var nativeStatus: FilmPlaybackStatus? {
        didSet { didChange.send() }
    }

    public init() {}

    func notifyChange() {
        objectWillChange.send()
        didChange.send()
    }
}

/// Low-frequency browsing/detail/directory/error presentation store.
/// Observed by `FilmDetailViewController` and `FilmDirectoryViewController`; completely
/// isolated from high-frequency playback position/buffer ticks and progress-only snapshots.
@MainActor
public final class FilmPresentationStateStore: ObservableObject {
    @Published public internal(set) var snapshot: FilmPlaybackSnapshot?
    @Published public internal(set) var presentation: FilmPlaybackSnapshot?
    @Published public internal(set) var opening = false
    @Published public internal(set) var observationError: FilmPlaybackError?
    @Published public internal(set) var actionError: FilmPlaybackError?
    @Published public internal(set) var metadataViewProvider: FilmMetadataProvider = .tmdb
    @Published internal var selectedAggregateProvider: FilmMetadataProvider?
    @Published public internal(set) var mediaStopError: FilmPlaybackError?
    @Published public internal(set) var sessionReleaseError: FilmPlaybackError?
    /// Coarse lifecycle playback status (never mutated by high-frequency progress or transient buffering ticks).
    @Published public internal(set) var nativeStatus: FilmPlaybackStatus?

    public init() {}
}
