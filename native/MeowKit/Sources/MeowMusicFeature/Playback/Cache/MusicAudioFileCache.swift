import Foundation
import MeowContracts

/// Complete, previously played music bytes. This is an evictable cache, not a
/// user download, offline library item, playlist record or playback authority.
public struct MusicAudioFileCacheIdentity: Codable, Equatable, Sendable {
    public let scope: String
    public let songID: String
    public let sourceKey: String
    public let platform: String
    public let nativeID: String
    public let requestedQuality: String
    public let provider: String
    public let generation: Int
    public var isOfficial: Bool { provider.hasPrefix("official:") }
    public var selection: MusicPlaybackSelection {
        .init(installationId: provider, generation: generation, siteId: platform, sourceKey: sourceKey, quality: requestedQuality)
    }
    public init(scope: String, songID: String, sourceKey: String, platform: String,
                nativeID: String, requestedQuality: String, provider: String, generation: Int = 1) {
        self.scope = scope; self.songID = songID; self.sourceKey = sourceKey
        self.platform = platform; self.nativeID = nativeID
        self.requestedQuality = requestedQuality
        self.provider = provider; self.generation = generation
    }
}

/// A provider has excluded trial/DRM/explicitly disallowed media. This does not
/// establish completeness or measured quality: the native file inspector must
/// still prove both before publication.
public struct MusicAudioFileCachePolicy: Sendable {
    public let allowed: Bool
    public init(allowed: Bool) {
        self.allowed = allowed
    }
    public static let denied = Self(allowed: false)
}

/// Opaque file revision, not media identity or a playback permission. A late
/// touch/removal for a replaced file cannot operate on its successor.
public struct MusicAudioFileCacheVersion: Equatable, Sendable {
    public let rawValue: String
    public init(rawValue: String) { self.rawValue = rawValue }
}

/// Active read lease preventing eviction or clear from unlinking an in-use local audio file.
/// Releasing is idempotent and automatically runs in `deinit` if not explicitly released.
public final class MusicAudioFileReadLease: @unchecked Sendable {
    private let lock = NSLock()
    private var onRelease: (@Sendable () -> Void)?

    public init(onRelease: @escaping @Sendable () -> Void) {
        self.onRelease = onRelease
    }

    public func release() {
        let callback: (@Sendable () -> Void)? = lock.withLock {
            let current = onRelease
            onRelease = nil
            return current
        }
        callback?()
    }

    deinit {
        release()
    }
}

public struct MusicAudioFileCacheStats: Sendable, Equatable {
    public let entryCount: Int
    public let totalDiskBytes: Int64
    public let reservedBytes: Int64
    public let pendingDeleteCount: Int
    public let pendingDeleteBytes: Int64
    public let activeReadLeases: Int
    public let pendingCaptures: Int
    public let capacityBytes: Int64
    public let closed: Bool

    public init(entryCount: Int, totalDiskBytes: Int64, reservedBytes: Int64,
                pendingDeleteCount: Int, pendingDeleteBytes: Int64,
                activeReadLeases: Int, pendingCaptures: Int, capacityBytes: Int64, closed: Bool) {
        self.entryCount = entryCount; self.totalDiskBytes = totalDiskBytes
        self.reservedBytes = reservedBytes; self.pendingDeleteCount = pendingDeleteCount
        self.pendingDeleteBytes = pendingDeleteBytes; self.activeReadLeases = activeReadLeases
        self.pendingCaptures = pendingCaptures; self.capacityBytes = capacityBytes
        self.closed = closed
    }
}

public struct MusicAudioFileCacheHit: Sendable {
    public let identity: MusicAudioFileCacheIdentity
    public let fileURL: URL
    public let duration: Double
    public let measuredQuality: String
    public let expiresAt: Date
    public let readLease: MusicAudioFileReadLease?
    public var version: MusicAudioFileCacheVersion { .init(rawValue: fileURL.lastPathComponent) }
    public init(identity: MusicAudioFileCacheIdentity, fileURL: URL, duration: Double,
                measuredQuality: String, expiresAt: Date, readLease: MusicAudioFileReadLease? = nil) {
        self.identity = identity; self.fileURL = fileURL; self.duration = duration
        self.measuredQuality = measuredQuality; self.expiresAt = expiresAt
        self.readLease = readLease
    }
}

public struct MusicAudioFileCacheRequest: Sendable {
    public let identity: MusicAudioFileCacheIdentity
    public let audio: MusicNativeAudio
    public let expectedDuration: Double
    public let confirmedAt: Date
    public let authorization: MusicQueueAuthorization
    public let playbackAuthorization: MusicQueueAuthorization
    public init(identity: MusicAudioFileCacheIdentity, audio: MusicNativeAudio,
                expectedDuration: Double, confirmedAt: Date, authorization: MusicQueueAuthorization,
                playbackAuthorization: MusicQueueAuthorization = MusicQueueAuthorization()) {
        self.identity = identity; self.audio = audio; self.expectedDuration = expectedDuration
        self.confirmedAt = confirmedAt; self.authorization = authorization
        self.playbackAuthorization = playbackAuthorization
    }
}

public struct MusicAudioFilePlaybackRequest: Sendable {
    public let identity: MusicAudioFileCacheIdentity
    public let audio: MusicNativeAudio
    public let expectedDuration: Double
    public let authorization: MusicQueueAuthorization
    public let playbackAuthorization: MusicQueueAuthorization
    public init(identity: MusicAudioFileCacheIdentity, audio: MusicNativeAudio, expectedDuration: Double,
                authorization: MusicQueueAuthorization, playbackAuthorization: MusicQueueAuthorization) {
        self.identity = identity; self.audio = audio; self.expectedDuration = expectedDuration
        self.authorization = authorization; self.playbackAuthorization = playbackAuthorization
    }
}

public protocol MusicAudioFilePlayback: PlaybackByteSource {
    /// Only the playback owner supplies an accepted real-playing timestamp.
    func confirmPlayback(at date: Date)
    func result() async -> MusicAudioFileCacheVersion?
}

/// Implementations are passive at construction, bounded to 256 MiB total and 30 days
/// since actual play. Active read leases protect playing files from eviction/clear unlink.
public protocol MusicAudioFileCaching: Sendable {
    func lookup(scope: String, songID: String, authorization: MusicQueueAuthorization,
                accepts: @escaping @Sendable (MusicAudioFileCacheIdentity) -> Bool) async -> MusicAudioFileCacheHit?
    @discardableResult func storeAfterPlayback(_ request: MusicAudioFileCacheRequest) async -> MusicAudioFileCacheVersion?
    func preparePlayback(_ request: MusicAudioFilePlaybackRequest) async throws -> any MusicAudioFilePlayback
    func touch(_ identity: MusicAudioFileCacheIdentity, version: MusicAudioFileCacheVersion, confirmedAt: Date,
               authorization: MusicQueueAuthorization) async
    func remove(_ identity: MusicAudioFileCacheIdentity, version: MusicAudioFileCacheVersion) async
    func cancelPending() async
    func clear() async throws
    /// Account changes revoke official files only; independent script history remains.
    func clearOfficial() async throws
    func stats() async -> MusicAudioFileCacheStats
    func close() async
}

public struct MusicCachedPlayback: Sendable {
    public let candidate: MusicPlaybackCandidate
    public let selection: MusicPlaybackSelection
    public init(candidate: MusicPlaybackCandidate, selection: MusicPlaybackSelection) {
        self.candidate = candidate; self.selection = selection
    }
}

public protocol MusicCachedPlaybackServing: Sendable {
    func cachedPlayback(attemptID: String, occurrenceID: String,
                        selection: MusicPlaybackSelection?) async throws -> MusicCachedPlayback?
    func discardCachedPlayback(attemptID: String) async
}
