import Foundation

/// v1, transient only. The account owner binds both optional preview reads and
/// actual FM batches to the same existing guest/signed-in home authorization.
/// A rule preview contains artwork only, never songs or playback authority.
public struct MusicNativeFMResponse: Sendable {
    public let tracks: [MusicNativeTrackData]
    public let artwork: URL?
    public init(tracks: [MusicNativeTrackData] = [], artwork: URL? = nil) {
        self.tracks = tracks; self.artwork = artwork
    }
}
public protocol MusicNativeFMProviding: MusicNativeHomeProviding {
    func nativeFM(_ action: MusicHomeAction, preview: Bool,
                  binding: MusicNativeHomeBinding) async throws -> MusicNativeFMResponse
}
public struct MusicFMPreview: Sendable {
    public let artwork: URL?
    /// Display lifetime only; eviction, replacement and logout revoke it.
    public let authorization: MusicQueueAuthorization
    public init(artwork: URL?, authorization: MusicQueueAuthorization) {
        self.artwork = artwork; self.authorization = authorization
    }
}
public protocol MusicFMPreviewServing: Sendable {
    /// Called only by a visible FM card. No queue, playback, history, feedback,
    /// URL resolution, audio-file cache or persistence side effect is allowed.
    func fmPreview(_ action: MusicHomeAction) async throws -> MusicFMPreview?
}
public extension MusicHomeAction {
    /// Source labels do not create a different radio configuration.
    var fmConfiguration: MusicHomeAction? {
        switch self {
        case .fm: return self
        case .ruleFM(let rule, let tab, _): return .ruleFM(ruleID: rule, tabTagID: tab, sourceName: "")
        default: return nil
        }
    }
}
