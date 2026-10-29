import Foundation

/// Recovery result for a remote-media failure. This is not
/// candidate discovery, a player, a file-cache authorization or a playback fact.
public struct MusicPlaybackRecovery: Sendable {
    public let candidate: MusicPlaybackCandidate
    public let selection: MusicPlaybackSelection
    public init(candidate: MusicPlaybackCandidate, selection: MusicPlaybackSelection) {
        self.candidate = candidate; self.selection = selection
    }
}

public protocol MusicPlaybackRecoveryServing: Sendable {
    /// Call before recording the terminal failure or cancelling the old attempt.
    /// The owner verifies its previously published URL, occurrence, source,
    /// account generation and queue/settings snapshot. A fresh attempt ID is
    /// consumed even if recovery fails. One original attempt chain can refresh
    /// at most once; no script, quality fallback or account switch is allowed.
    ///
    /// `position` is the last observed media position, not elapsed UI time.
    /// A returned URL does not establish playing or delivered quality. The
    /// consumer must still admit the candidate through the existing player
    /// and send actual facts to the same business owner.
    func recoverPlayback(previousAttemptID: String, attemptID: String,
                         rejectedURL: URL, position: Double) async throws -> MusicPlaybackRecovery
}

/// Optional account-side recovery admission. Unlike unpinned nativeOptions,
/// this must check the original installation/generation before ANY provider
/// call, and keep that binding across suspension. It cannot restore, log in or
/// silently use another account. Cache-display scope is not this permission.
public protocol MusicNativePlaybackRecoveryAuthorizing: MusicNativeAccountURLRefreshing {
    func nativeRecoveryOptions(platform: MusicAccountPlatform, nativeID: String,
                               selection: MusicPlaybackSelection) async throws -> [MusicPlaybackOption]
}
