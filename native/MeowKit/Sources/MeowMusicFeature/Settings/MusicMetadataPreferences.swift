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

/// Current permanent record and its policy-enabled source mappings.
/// Callers must fence publication by their own access/session generation and
/// re-read on queue, mapping or metadata-policy changes.
public struct MusicMetadataCandidates: Equatable, Sendable {
    public let occurrenceID: String
    public let queueRevision: Int
    public let preferences: MusicMetadataPreferences
    public let track: MusicRemoteTrack
    public let candidates: [MusicMetadataCandidate]

    public init(occurrenceID: String, queueRevision: Int, preferences: MusicMetadataPreferences,
                track: MusicRemoteTrack,
                candidates: [MusicMetadataCandidate]) {
        self.occurrenceID = occurrenceID; self.queueRevision = queueRevision
        self.preferences = preferences; self.track = track
        self.candidates = candidates
    }
}

/// Optional image failure selects only already-known repository candidates.
/// It neither searches platforms nor resolves audio.
public protocol MusicArtworkSelecting: Sendable {
    func failedArtwork(songID: String, url: URL, attempted: Set<URL>) async throws -> URL?
    /// The actual bytes may be the existing bounded network rendition. Only
    /// its real URL is cached/referenced, never an alias under the requested URL.
    func succeededArtwork(songID: String, url: URL, replacing requested: URL) async throws -> URL?
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
