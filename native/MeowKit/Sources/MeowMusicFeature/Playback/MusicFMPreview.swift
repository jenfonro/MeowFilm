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
    func nativeFMPreferenceScope(_ binding: MusicNativeHomeBinding) async throws -> String
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
        case .fm(let mode, let submode, _): return .fm(mode: mode, submode: submode, entry: nil)
        case .ruleFM(let rule, let tab, _): return .ruleFM(ruleID: rule, tabTagID: tab, sourceName: "")
        default: return nil
        }
    }
}

public struct MusicFMPlaybackSnapshot: Sendable {
    public let id: UUID
    public let action: MusicHomeAction
    public let continueEnabled: Bool
    public let loading: Bool
    public let error: MusicServiceError?
    public let errorID: UUID?
    public init(id: UUID, action: MusicHomeAction, continueEnabled: Bool, loading: Bool, error: MusicServiceError?, errorID: UUID? = nil) {
        self.id = id; self.action = action; self.continueEnabled = continueEnabled; self.loading = loading; self.error = error; self.errorID = errorID
    }
}

public protocol MusicFMPlaybackServing: Sendable {
    func rememberedFM() async throws -> MusicHomeAction
    func fmPlaybackSnapshot() async throws -> MusicFMPlaybackSnapshot?
    /// true selects FM playback with queue order and continuation together.
    func setFMContinue(_ enabled: Bool, sessionID: UUID) async throws
}
