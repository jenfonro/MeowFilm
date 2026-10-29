import Foundation

/// Candidate discovery data without credentials, media URLs or mutable global sources.
public struct MusicCandidateDiscoveryOption: Sendable, Equatable {
    public let playback: MusicPlaybackOption
    /// LX configuration/content revision; official options use their account generation.
    public let revisionHash: String?
    public init(playback: MusicPlaybackOption, revisionHash: String? = nil) {
        self.playback = playback; self.revisionHash = revisionHash
    }
    public func target(quality: String) -> MusicCandidateDiscoveryTarget {
        .init(option: self, quality: quality)
    }
}

public struct MusicCandidateDiscoveryTarget: Sendable, Equatable {
    public let option: MusicCandidateDiscoveryOption
    public let quality: String
    public var selection: MusicPlaybackSelection { option.playback.selection(quality: quality) }
    public init(option: MusicCandidateDiscoveryOption, quality: String) {
        self.option = option; self.quality = quality
    }
    public func sameRequest(as other: Self) -> Bool {
        selection == other.selection && option.playback.version == other.option.playback.version &&
        option.playback.contentHash == other.option.playback.contentHash && option.revisionHash == other.option.revisionHash
    }
}

public struct MusicCandidateDiscoveryIssue: Sendable, Equatable {
    public enum Stage: Sendable, Equatable { case search, identity, capability, resolution }
    public let stage: Stage
    public let platform: String
    public let failure: MusicServiceError
    public init(stage: Stage, platform: String, failure: MusicServiceError) {
        self.stage = stage; self.platform = platform; self.failure = failure
    }
}

public struct MusicCandidateDiscoverySnapshot: Sendable {
    public let track: MusicRemoteTrack
    public let state: MusicPlaybackState
    public let options: [MusicCandidateDiscoveryOption]
    public let issues: [MusicCandidateDiscoveryIssue]
    public let scriptState: MusicSourceAvailability.ScriptState
    public let searched: Bool
    public init(track: MusicRemoteTrack, state: MusicPlaybackState, options: [MusicCandidateDiscoveryOption],
                issues: [MusicCandidateDiscoveryIssue] = [],
                scriptState: MusicSourceAvailability.ScriptState = .notConfigured, searched: Bool = false) {
        self.track = track; self.state = state; self.options = options
        self.issues = issues; self.scriptState = scriptState; self.searched = searched
    }
}

/// A recording/queue/access-bound session, not a new runtime or document writer.
/// Discovery stages metadata; resolving a discovered target adopts it through the
/// existing owner's CAS. Closing cancels only this session's work/attempt.
public protocol MusicCandidateDiscoverySession: Sendable {
    func snapshot(discover: Bool, force: Bool) async throws -> MusicCandidateDiscoverySnapshot
    /// Explicit single-writer adoption, including empty/partial discovery backoff.
    func commitDiscovery() async throws
    func resolve(_ target: MusicCandidateDiscoveryTarget, attemptID: String) async throws -> MusicPlaybackCandidate
    func cancel(attemptID: String) async
    func close(retainingAttemptID: String?) async
}

public protocol MusicCandidateDiscoveryServing: Sendable {
    func beginCandidateDiscovery(occurrenceID: String, expectedQueueRevision: Int) async throws -> any MusicCandidateDiscoverySession
}

public protocol MusicCandidateDiscoverySelecting: Sendable {
    func plan(_ snapshot: MusicCandidateDiscoverySnapshot,
              preferred: MusicCandidateDiscoveryTarget?) throws -> [MusicCandidateDiscoveryTarget]
}

public enum MusicCandidateDiscoveryMode: Sendable {
    /// Exactly one pinned request. Never search, lower quality or change source on failure.
    case manual(MusicCandidateDiscoveryTarget)
    /// Explicit opt-in by the music business owner, never a host/resolve fallback.
    case automatic(preferred: MusicCandidateDiscoveryTarget? = nil, forceDiscovery: Bool = false)
}

public struct MusicCandidateDiscoveryResolution: Sendable {
    public let candidate: MusicPlaybackCandidate
    public let target: MusicCandidateDiscoveryTarget
    public let attempts: Int
    public let issues: [MusicCandidateDiscoveryIssue]
    /// Requested quality and a valid URL do not establish delivered quality or playback.
    public var playbackVerified: Bool { false }
    public var qualityVerified: Bool { false }
}

public struct MusicCandidateDiscoveryFailure: Error, Sendable {
    public let failure: MusicServiceError
    public let issues: [MusicCandidateDiscoveryIssue]
    public let attempts: Int
    public init(failure: MusicServiceError, issues: [MusicCandidateDiscoveryIssue], attempts: Int) {
        self.failure = failure; self.issues = issues; self.attempts = attempts
    }
}

public enum MusicCandidateDiscoveryFailures {
    /// Session/authority/storage errors never authorize trying another target.
    public static func mayTryAnother(_ failure: MusicServiceError) -> Bool {
        switch failure {
        case .sourceUnavailable, .unsupported, .upstreamFailed, .upstreamInvalid,
             .invalidMediaCandidate, .resolutionFailed, .resolutionTimeout, .timeout:
            return true
        default: return false
        }
    }
}
