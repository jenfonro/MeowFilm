import Foundation

/// A displayed recording is identity evidence, not a durable handle or a
/// playback grant. Source labels belong to the original user action.
public enum MusicDisplayedQueueOrigin: Sendable {
    case song
    case section(title: String)
    case history(date: String)
}

/// Optional native v1 capability. Resolve the exact displayed recordings from
/// owned data or their native IDs, then commit one queue/history transaction.
/// No arbitrary display metadata is persisted, no relevance search/fallback
/// is allowed, and a revoked intent or unconfirmed commit cannot be replayed.
public protocol MusicDisplayedQueuePlaying: Sendable {
    func replaceDisplayedQueue(_ tracks: [MusicRemoteTrack], index: Int,
                               origin: MusicDisplayedQueueOrigin, expectedRevision: Int,
                               intent: MusicQueueAuthorization) async throws -> MusicPlaybackState
}
