import Foundation

// Product/domain adapters supply presentation metadata to the
// existing PlaybackController; no domain identifiers, fetching or persistence.
// Existing callers retain the resource title until they explicitly override it.
public struct PlaybackNowPlayingArtwork: Equatable, Sendable {
    public static let maximumBytes = 2 * 1_024 * 1_024
    public let data: Data

    /// An already-loaded, encoded image, never a URL or a request instruction.
    /// The system adapter additionally bounds native thumbnail decoding.
    public init?(data: Data) {
        guard !data.isEmpty, data.count <= Self.maximumBytes else { return nil }
        self.data = data
    }
}

public struct PlaybackNowPlayingMetadata: Equatable, Sendable {
    public var title: String
    public var artist: String?
    public var album: String?
    public var artwork: PlaybackNowPlayingArtwork?

    public init(title: String, artist: String? = nil, album: String? = nil,
                artwork: PlaybackNowPlayingArtwork? = nil) {
        self.title = title; self.artist = artist; self.album = album; self.artwork = artwork
    }
}

#if os(iOS) || os(macOS)
/// Distinguishes "no override" from explicit privacy suppression. A stale clear
/// is just as dangerous as a stale update, so both require the complete attempt.
struct PlaybackNowPlayingMetadataState {
    private var attempt: PlayAttempt?
    private var overridden = false
    private var metadata: PlaybackNowPlayingMetadata?

    mutating func begin(_ attempt: PlayAttempt) {
        self.attempt = attempt; overridden = false; metadata = nil
    }
    mutating func end() { attempt = nil; overridden = false; metadata = nil }

    @discardableResult
    mutating func set(_ value: PlaybackNowPlayingMetadata?, attempt: PlayAttempt) -> Bool {
        guard self.attempt == attempt else { return false }
        overridden = true; metadata = value
        return true
    }

    func value(for attempt: PlayAttempt?, fallbackTitle: String) -> PlaybackNowPlayingMetadata? {
        guard let attempt, self.attempt == attempt else { return nil }
        return overridden ? metadata : .init(title: fallbackTitle)
    }
}
#endif
