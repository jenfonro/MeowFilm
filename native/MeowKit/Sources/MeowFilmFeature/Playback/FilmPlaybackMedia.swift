import Foundation
#if os(iOS)
import UIKit
#endif

public enum FilmVideoScale: String, CaseIterable, Sendable {
    case fit, fill, stretch
    public var title: String {
        switch self {
        case .fit: "原始比例"
        case .fill: "填满"
        case .stretch: "拉伸"
        }
    }
}

public enum FilmExternalPlayer: CaseIterable, Sendable {
    case infuse, nPlayer

    public var title: String { self == .infuse ? "Infuse" : "nPlayer" }

    func url(for input: URL) throws -> URL {
        let text = input.absoluteString
        let target: String
        switch self {
        case .infuse:
            let characters = CharacterSet(charactersIn: "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_.!~*'()")
            guard let encoded = text.addingPercentEncoding(withAllowedCharacters: characters) else {
                throw FilmPlaybackError.invalidResponse
            }
            target = "infuse://x-callback-url/play?url=\(encoded)"
        case .nPlayer:
            let scheme = input.scheme!.lowercased()
            target = "nplayer-\(scheme)\(text.dropFirst(scheme.count))"
        }
        guard let url = URL(string: target) else { throw FilmPlaybackError.invalidResponse }
        return url
    }
}

public struct FilmMediaState: Equatable, Sendable {
    public var sessionID: UUID?
    public var attemptID: String?
    public var position: Double = 0
    public var duration: Double = 0
    public var paused = true
    public var buffering = false
    public var rate: Double = 1
    public var volume: Double = 1
    public var pictureInPicture = false
    public var pictureInPictureAvailable = false
    public init() {}
}

public enum FilmMediaEvent: Sendable {
    case state(FilmMediaState)
    case started(attemptID: String)
    case failed(attemptID: String, error: FilmPlaybackError = .init(code: "MEDIA_UNAVAILABLE"))
    case ended(attemptID: String)
    case ownerEnded(attemptID: String)
    case systemOwnerEnded(attemptID: String)
}

/// App composition adapts its single shared PlaybackController. The feature
/// neither creates an engine nor knows the engine's credentials or private types.
@MainActor
public protocol FilmPlaybackMediaControlling: AnyObject {
    /// Formal playback only. The native owner silently prepares and atomically
    /// commits after real output. Temporary business admission waits do not
    /// release the candidate. Recheck canCommit synchronously at the actual swap.
    /// started is the output receipt; returning from play is not a watch fact.
    func play(_ input: FilmPlaybackMediaInput, preservePosition: Bool,
              waitForCommit: @escaping @MainActor () async throws -> Void,
              canCommit: @escaping @MainActor () -> Bool,
              release: @escaping @MainActor () async throws -> Void,
              receive: @escaping @MainActor (FilmMediaEvent) -> Void) async throws
    /// Revoke and physically drain the candidate, retaining healthy current output.
    func cancelPending(sessionID: UUID) async throws
    func stop(sessionID: UUID) async throws
    /// Physical decoder/output drain, independent of the runtime release ACK.
    func isLocallyDrained(sessionID: UUID) -> Bool
    func isCurrent(sessionID: UUID, attemptID: String) -> Bool
    /// Returns true only if this call actually pauses the exact playing owner.
    func pause(sessionID: UUID, attemptID: String) -> Bool
    func resume(sessionID: UUID, attemptID: String, userInitiated: Bool)
    func seek(sessionID: UUID, to position: Double)
    func setRate(sessionID: UUID, rate: Double)
    func setVolume(sessionID: UUID, volume: Double)
    func startPictureInPicture(sessionID: UUID)
    func stopPictureInPicture(sessionID: UUID)
    func hasSystemOwner(sessionID: UUID) -> Bool
    #if os(iOS)
    /// Passive presentation of the shared owner, never another decoder/player.
    func makeVideoView(sessionID: UUID) -> UIView
    func setVideoScale(sessionID: UUID, scale: FilmVideoScale)
    #endif
}
