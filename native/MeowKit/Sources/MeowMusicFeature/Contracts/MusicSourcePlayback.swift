import Foundation

/// User intent survives candidate changes and recovery. A concrete result pins
/// provider/platform/recording/quality, not an expiring URL or script generation.
public enum MusicSourceIntent: Sendable {
    case automatic
    case quality(String)
    case exact(MusicPlaybackSelection)
}

/// Prepared source data does not retire the currently playing attempt.
public struct MusicPreparedSource: Sendable {
    public let candidate: MusicPlaybackCandidate
    public let selection: MusicPlaybackSelection
    public init(candidate: MusicPlaybackCandidate, selection: MusicPlaybackSelection) {
        self.candidate = candidate; self.selection = selection
    }
}
public protocol MusicSourcePlaybackSession: MusicSourceInspectionSession {
    /// nil quality means the global automatic quality order; an explicit value never degrades.
    func match(quality: String?, sourceID: String?) async throws -> MusicPreparedSource
    func match(intent: MusicSourceIntent) async throws -> MusicPreparedSource
    func beginSelection() async
    func rejectMedia(candidate: MusicPlaybackCandidate, selection: MusicPlaybackSelection) async
    func cancelInspection() async
}
public protocol MusicSourcePlaybackServing: MusicResolvedSourcesServing {
    func sourceSession(occurrenceID: String, expectedQueueRevision: Int) async throws -> any MusicSourcePlaybackSession
    func activateSource(attemptID: String) async throws
    func discardSource(attemptID: String) async
    func sourceConfigurationChanged() async
}

public enum MusicSourceExclusionScope: Sendable, Equatable {
    case providerPlatform
    case recording(sourceKey: String)
}

public struct MusicSourceExclusion: Sendable, Equatable, Identifiable {
    public let id: String
    public let songID: String
    public let title: String
    public let artist: String
    public let provider: String
    public let platform: String
    public let scope: MusicSourceExclusionScope
    public init(id: String, songID: String, title: String, artist: String, provider: String, platform: String,
                scope: MusicSourceExclusionScope = .providerPlatform) {
        self.id = id; self.songID = songID; self.title = title; self.artist = artist
        self.provider = provider; self.platform = platform; self.scope = scope
    }
    public static func providerID(_ id: String, platform: String) -> String {
        id.hasPrefix("official:") ? "official:" + platform : id
    }
}
public protocol MusicSourceExclusionServing: Sendable {
    func sourceExclusions() async throws -> [MusicSourceExclusion]
    func excludeSource(for reference: MusicReference, provider: String, platform: String) async throws
    func restoreSourceExclusion(id: String) async throws
}
public protocol MusicOfficialSourceChecking: Sendable {
    func checkOfficialSource(_ platform: MusicAccountPlatform) async throws
}

/// A declaration permission failure is not an audio-content answer. Preserve
/// this boundary through selection, then expose the original service error.
public struct MusicSourceDeclarationFailure: Error, Sendable {
    public let failure: MusicServiceError
    public init(failure: MusicServiceError) { self.failure = failure }
}

public enum MusicSourceFailureClassifier {
    /// Session/authority/storage errors never authorize trying another target.
    public static func mayTryAnother(_ failure: MusicServiceError) -> Bool {
        switch failure {
        case .sourceUnavailable, .unsupported, .upstreamFailed, .upstreamInvalid,
             .invalidMediaCandidate, .resolutionFailed, .resolutionTimeout, .timeout,
             .connectionFailed, .networkOffline, .scriptFailed, .scriptRuntimeUnavailable,
             .rateLimited, .busy, .requestRejected:
            return true
        default: return false
        }
    }

    /// The platform answered that this recording cannot be played as requested
    /// (missing, removed, no address, no permission, preview only).
    public static func isContentFailure(_ failure: MusicServiceError) -> Bool {
        switch failure {
        case .notFound, .contentUnavailable, .audioUnavailable, .permissionDenied, .previewOnly:
            return true
        default: return false
        }
    }
}
