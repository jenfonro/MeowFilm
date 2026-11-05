import Foundation

/// Preferences for ordinary playback metadata, independent of audio
/// delivery, quality and downloads. The revision is the existing playback
/// settings revision, not a separate counter.
public struct MusicMetadataPreferences: Equatable, Sendable {
    public let revision: Int
    /// All platforms available in this product, including user-disabled rows.
    public let platforms: [MusicPublicBrowsePlatform]
    public let enabledPlatforms: Set<MusicPublicBrowsePlatform>

    public init(revision: Int, platforms: [MusicPublicBrowsePlatform],
                enabledPlatforms: Set<MusicPublicBrowsePlatform>) {
        self.revision = revision; self.platforms = platforms
        self.enabledPlatforms = enabledPlatforms
    }
}

public struct MusicMetadataPreferencesChange: Sendable {
    public let ifRevision: Int
    /// If supplied, a complete permutation of the product's available platforms.
    public let platforms: [MusicPublicBrowsePlatform]?
    /// nil preserves the value; an empty set explicitly disables every row.
    public let enabledPlatforms: Set<MusicPublicBrowsePlatform>?

    public init(ifRevision: Int, platforms: [MusicPublicBrowsePlatform]? = nil,
                enabledPlatforms: Set<MusicPublicBrowsePlatform>? = nil) {
        self.ifRevision = ifRevision; self.platforms = platforms
        self.enabledPlatforms = enabledPlatforms
    }
}

public struct MusicMetadataCandidate: Equatable, Sendable, Identifiable {
    public let source: MusicNativeSource
    /// This mapping's cover, never the selected audio source's cover.
    public let artwork: URL?
    public var id: String { source.sourceKey }

    public init(source: MusicNativeSource, artwork: URL? = nil) {
        self.source = source; self.artwork = artwork
    }
}

/// One read-only snapshot of the current occurrence and exact recording
/// mappings. At most two candidates per platform, ten across the five platforms.
/// Callers must fence publication by their own access/session generation and
/// re-read on queue, mapping or metadata-policy changes.
public struct MusicMetadataCandidates: Equatable, Sendable {
    public let occurrenceID: String
    public let queueRevision: Int
    public let preferences: MusicMetadataPreferences
    public let track: MusicRemoteTrack
    /// The song's revision when it is present in the stored record.
    /// Never replace it with queueRevision or preferences.revision in cache keys.
    public let contentRevision: Int?
    /// Opaque, credential-free content identity, excluding occurrence, audio
    /// delivery and preference revisions. It also covers the source locator and
    /// that MusicRemoteTrack cannot express. Invalidate a cached song if it changes.
    public let contentFingerprint: String
    public let candidates: [MusicMetadataCandidate]

    public init(occurrenceID: String, queueRevision: Int, preferences: MusicMetadataPreferences,
                track: MusicRemoteTrack, contentRevision: Int?, contentFingerprint: String,
                candidates: [MusicMetadataCandidate]) {
        self.occurrenceID = occurrenceID; self.queueRevision = queueRevision
        self.preferences = preferences; self.track = track
        self.contentRevision = contentRevision; self.contentFingerprint = contentFingerprint
        self.candidates = candidates
    }
}

/// Metadata responsibilities required by MusicServing.
/// Reads do not initialize storage. Updates use the existing protected
/// document/CAS writer, preserve unavailable platforms and unrelated records,
/// and never replay a stale or unconfirmed write.
///
/// Errors use MusicServiceError (including staleRevision, corruptDocument,
/// storageConflict, commitUnconfirmed and access/cancellation failures).
/// Candidate reads perform no search, audio resolution, network or script work.
/// Empty candidates mean no eligible established mapping, not permission to
/// search for a similarly titled recording.
public protocol MusicMetadataPreferencesServing: Sendable {
    func metadataPreferences() async throws -> MusicMetadataPreferences
    func updateMetadataPreferences(_ change: MusicMetadataPreferencesChange) async throws -> MusicMetadataPreferences
    func metadataCandidates(occurrenceID: String) async throws -> MusicMetadataCandidates
}
