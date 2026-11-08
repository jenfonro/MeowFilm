import Foundation

/// Complete, previously played music bytes. This is an evictable cache, not a
/// user download, offline library item, playlist record or playback authority.
public struct MusicAudioFileCacheIdentity: Codable, Equatable, Sendable {
    public let scope: String
    public let songID: String
    public let sourceKey: String
    public let platform: String
    public let nativeID: String
    public let requestedQuality: String
    public let playbackSignature: String
    public let provider: String
    public let generation: Int
    public var isOfficial: Bool { provider.hasPrefix("official:") }
    public var selection: MusicPlaybackSelection {
        .init(installationId: provider, generation: generation, siteId: platform, sourceKey: sourceKey, quality: requestedQuality)
    }
    public init(scope: String, songID: String, sourceKey: String, platform: String,
                nativeID: String, requestedQuality: String, playbackSignature: String, provider: String, generation: Int = 1) {
        self.scope = scope; self.songID = songID; self.sourceKey = sourceKey
        self.platform = platform; self.nativeID = nativeID
        self.requestedQuality = requestedQuality; self.playbackSignature = playbackSignature
        self.provider = provider; self.generation = generation
    }
}

/// A provider has excluded trial/DRM/explicitly disallowed media. This does not
/// establish completeness or measured quality: the native file inspector must
/// still prove both before publication. URL expiry is NOT a file licence expiry.
public struct MusicAudioFileCachePolicy: Sendable {
    public let allowed: Bool
    public let licenseExpiresAt: Date?
    public init(allowed: Bool, licenseExpiresAt: Date? = nil) {
        self.allowed = allowed; self.licenseExpiresAt = licenseExpiresAt
    }
    public static let denied = Self(allowed: false)
}

/// Opaque file revision, not media identity or a playback permission. A late
/// touch/removal for a replaced file cannot operate on its successor.
public struct MusicAudioFileCacheVersion: Equatable, Sendable {
    public let rawValue: String
    public init(rawValue: String) { self.rawValue = rawValue }
}

public struct MusicAudioFileCacheHit: Sendable {
    public let identity: MusicAudioFileCacheIdentity
    public let fileURL: URL
    public let duration: Double
    public let measuredQuality: String
    public let expiresAt: Date
    public var version: MusicAudioFileCacheVersion { .init(rawValue: fileURL.lastPathComponent) }
    public init(identity: MusicAudioFileCacheIdentity, fileURL: URL, duration: Double,
                measuredQuality: String, expiresAt: Date) {
        self.identity = identity; self.fileURL = fileURL; self.duration = duration
        self.measuredQuality = measuredQuality; self.expiresAt = expiresAt
    }
}

public struct MusicAudioFileCacheRequest: Sendable {
    public let identity: MusicAudioFileCacheIdentity
    public let audio: MusicNativeAudio
    public let expectedDuration: Double
    public let confirmedAt: Date
    public let authorization: MusicQueueAuthorization
    /// Song selection lifetime is separate from account/protection authority.
    /// Retiring selection cancels an incomplete body, but an already complete
    /// body may finish validation while authorization and the deadline remain.
    public let playbackAuthorization: MusicQueueAuthorization
    public init(identity: MusicAudioFileCacheIdentity, audio: MusicNativeAudio,
                expectedDuration: Double, confirmedAt: Date, authorization: MusicQueueAuthorization,
                playbackAuthorization: MusicQueueAuthorization = MusicQueueAuthorization()) {
        self.identity = identity; self.audio = audio; self.expectedDuration = expectedDuration
        self.confirmedAt = confirmedAt; self.authorization = authorization
        self.playbackAuthorization = playbackAuthorization
    }
}

/// Implementations are passive at construction, bounded to the reference's
/// 256 MiB total and 30 days since actual play, and fail as cache misses.
/// Lookup does not renew age. Cancellation/clear must prevent old publication.
public protocol MusicAudioFileCaching: Sendable {
    func lookup(scope: String, songID: String, authorization: MusicQueueAuthorization,
                accepts: @escaping @Sendable (MusicAudioFileCacheIdentity) -> Bool) async -> MusicAudioFileCacheHit?
    @discardableResult func storeAfterPlayback(_ request: MusicAudioFileCacheRequest) async -> MusicAudioFileCacheVersion?
    func touch(_ identity: MusicAudioFileCacheIdentity, version: MusicAudioFileCacheVersion, confirmedAt: Date,
               authorization: MusicQueueAuthorization) async
    func remove(_ identity: MusicAudioFileCacheIdentity, version: MusicAudioFileCacheVersion) async
    func cancelPending() async
    func clear() async
    /// Account changes revoke official files only; independent script history remains.
    func clearOfficial() async
    func close() async
}

public struct MusicCachedPlayback: Sendable {
    public let candidate: MusicPlaybackCandidate
    public let selection: MusicPlaybackSelection
    public init(candidate: MusicPlaybackCandidate, selection: MusicPlaybackSelection) {
        self.candidate = candidate; self.selection = selection
    }
}

/// Automatic play checks this before platform discovery. Manual selection may
/// only use a matching source/quality; a miss continues the existing resolver.
public protocol MusicCachedPlaybackServing: Sendable {
    func cachedPlayback(attemptID: String, occurrenceID: String,
                        selection: MusicPlaybackSelection?) async throws -> MusicCachedPlayback?
    func discardCachedPlayback(attemptID: String) async
}

/// Bad-file recovery belongs to the existing playback owner. The owner pins
/// the cache's account scope and transfers its accepted playing fact; callers
/// cannot supply a "started" flag or turn a cache miss into an offline item.
public protocol MusicCachedPlaybackRecoveryServing: Sendable {
    func recoverCachedPlayback(previousAttemptID: String, selection: MusicPlaybackSelection?,
                               position: Double) async throws -> MusicPlaybackRecovery
}
