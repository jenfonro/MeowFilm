import Foundation

struct FilmPlayerControlIdentity: Equatable, Sendable {
    let sessionID: UUID
    let attemptID: String
}

enum FilmPlayerScreenRegion: Equatable, Sendable {
    case left, center, right

    init(x: Double, width: Double) {
        if x < width / 3 { self = .left }
        else if x < width * 2 / 3 { self = .center }
        else { self = .right }
    }
}

/// One gesture locks one operation, including a disabled operation. It cannot
/// become a seek or an episode switch after initially changing volume/brightness.
struct FilmPlayerPanInteraction {
    enum Mode: Equatable {
        case none, seek, brightness, volume, episodes
    }
    enum Change: Equatable {
        case seek(Double), brightness(Double), volume(Double)
    }
    enum Completion: Equatable {
        case seek(Double), episode(next: Bool)
    }

    let identity: FilmPlayerControlIdentity
    let preferences: FilmPlayerPreferences
    let region: FilmPlayerScreenRegion
    let shortVideo: Bool
    let initialPosition: Double
    let duration: Double
    let initialVolume: Double
    let initialBrightness: Double
    private(set) var mode: Mode?
    private(set) var previewPosition: Double?
    private var verticalTranslation: Double = 0
    private var height: Double = 0

    init(identity: FilmPlayerControlIdentity, preferences: FilmPlayerPreferences,
         region: FilmPlayerScreenRegion, shortVideo: Bool,
         initialPosition: Double, duration: Double, initialVolume: Double, initialBrightness: Double) {
        self.identity = identity; self.preferences = preferences; self.region = region
        self.shortVideo = shortVideo; self.initialPosition = initialPosition; self.duration = duration
        self.initialVolume = initialVolume; self.initialBrightness = initialBrightness
    }

    mutating func update(x: Double, y: Double, width: Double, height: Double) -> Change? {
        guard width > 0, height > 0, x.isFinite, y.isFinite else { return nil }
        if mode == nil {
            guard x != 0 || y != 0 else { return nil }
            if abs(x) >= abs(y) {
                mode = preferences.swipeSeek && duration > 0 ? .seek : Mode.none
            } else {
                switch region {
                case .left: mode = preferences.swipeBrightness ? .brightness : Mode.none
                case .right: mode = preferences.swipeVolume ? .volume : Mode.none
                case .center: mode = preferences.swipeEpisodes && shortVideo ? .episodes : Mode.none
                }
            }
        }
        verticalTranslation = y; self.height = height
        switch mode {
        case .seek:
            let position = min(duration, max(0, initialPosition + x / width * duration))
            previewPosition = position
            return .seek(position)
        case .brightness: return .brightness(min(1, max(0, initialBrightness - y / height)))
        case .volume: return .volume(min(1, max(0, initialVolume - y / height)))
        default: return nil
        }
    }

    mutating func finish(cancelled: Bool, current: FilmPlayerControlIdentity?) -> Completion? {
        defer { mode = Mode.none; previewPosition = nil }
        guard !cancelled, identity == current else { return nil }
        if mode == .seek, let previewPosition { return .seek(previewPosition) }
        if mode == .episodes, abs(verticalTranslation) >= max(44, height * 0.2) {
            return .episode(next: verticalTranslation < 0)
        }
        return nil
    }
}
