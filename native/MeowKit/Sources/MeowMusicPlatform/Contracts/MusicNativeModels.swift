import Foundation

public struct MusicNativeTrackData: Sendable {
    public struct Display: Codable, Sendable {
        public let title: String
        public let artist: String
        public let album: String
        public let duration: Double
        public let artwork: URL?
        public init(title: String, artist: String, album: String, duration: Double, artwork: URL?) {
            self.title = title; self.artist = artist; self.album = album
            self.duration = duration; self.artwork = artwork
        }
    }
    public let platform: String
    public let data: Data
    public let display: Display?
    public let sourcePosition: Int?
    public init(platform: String, data: Data, sourcePosition: Int? = nil, display: Display? = nil) {
        self.platform = platform; self.data = data; self.sourcePosition = sourcePosition; self.display = display
    }
}

public struct MusicNativeTrackPage: Sendable {
    public let items: [MusicNativeTrackData]
    public let total: Int
    public let nextPage: Int?
    public let cursor: String?
    public let dailyStyle: MusicDailyStyleSelection?
    public init(items: [MusicNativeTrackData], total: Int = 0, nextPage: Int? = nil, cursor: String? = nil,
                dailyStyle: MusicDailyStyleSelection? = nil) {
        self.items = items; self.total = total; self.nextPage = nextPage; self.cursor = cursor
        self.dailyStyle = dailyStyle
    }
}

public struct MusicQueueTrackReference: Sendable {
    public let songID: String
    public let occurrenceID: String
    public let platform: String
    public let sourceKey: String
    public let sourcePosition: Int?
    public let recommendation: Data?
    public init(songID: String, occurrenceID: String, platform: String, sourceKey: String,
                sourcePosition: Int? = nil, recommendation: Data? = nil) {
        self.songID = songID; self.occurrenceID = occurrenceID
        self.platform = platform; self.sourceKey = sourceKey; self.sourcePosition = sourcePosition
        self.recommendation = recommendation
    }
}

public struct MusicQueueReferencePage: Sendable {
    public let items: [MusicQueueTrackReference]
    public let total: Int
    public let nextPage: Int?
    public let cursor: String?
    public let dailyStyle: MusicDailyStyleSelection?
    public init(items: [MusicQueueTrackReference], total: Int = 0, nextPage: Int? = nil, cursor: String? = nil,
                dailyStyle: MusicDailyStyleSelection? = nil) {
        self.items = items; self.total = total; self.nextPage = nextPage
        self.cursor = cursor; self.dailyStyle = dailyStyle
    }
}

public struct MusicNativeLyrics: Sendable {
    public let text: String
    public let translation: String
    public let romanization: String
    public init(text: String, translation: String = "", romanization: String = "") {
        self.text = text; self.translation = translation; self.romanization = romanization
    }
}

public struct MusicNativeAudio: Sendable {
    public let url: URL
    public let headers: [String: String]
    public let expiresAt: Date?
    public let fileCachePolicy: MusicAudioFileCachePolicy
    public let delivery: MusicAudioDelivery?
    public let duration: Double?
    public let playbackAuthorization: MusicQueueAuthorization?
    public init(url: URL, headers: [String: String] = [:], expiresAt: Date? = nil,
                fileCachePolicy: MusicAudioFileCachePolicy = .denied, delivery: MusicAudioDelivery? = nil,
                playbackAuthorization: MusicQueueAuthorization? = nil, duration: Double? = nil) {
        self.url = url; self.headers = headers; self.expiresAt = expiresAt
        self.fileCachePolicy = fileCachePolicy
        self.delivery = delivery; self.playbackAuthorization = playbackAuthorization
        self.duration = duration ?? delivery?.duration
    }
}

public struct MusicScriptCapability: Sendable, Equatable {
    public let siteID: String
    public let title: String
    public let actions: [String]
    public let qualities: [String]
    public init(siteID: String, title: String, actions: [String], qualities: [String]) {
        self.siteID = siteID; self.title = title; self.actions = actions; self.qualities = qualities
    }
}

public struct MusicScriptIdentity: Codable, Sendable, Equatable {
    public let version: String
    public let contentHash: String
    public let revisionHash: String
    public init(version: String, contentHash: String, revisionHash: String) {
        self.version = version; self.contentHash = contentHash; self.revisionHash = revisionHash
    }
    public static func == (lhs: Self, rhs: Self) -> Bool {
        lhs.version.utf8.elementsEqual(rhs.version.utf8) &&
            lhs.contentHash.utf8.elementsEqual(rhs.contentHash.utf8) &&
            lhs.revisionHash.utf8.elementsEqual(rhs.revisionHash.utf8)
    }
}

public struct MusicScriptSource: Sendable, Equatable {
    public let installationID: String
    public let generation: Int
    public let enabled: Bool
    public let state: MusicScriptSourceItem.State
    public let version: String
    public let contentHash: String
    public let revisionHash: String
    public let capabilities: [MusicScriptCapability]
    public var identity: MusicScriptIdentity {
        .init(version: version, contentHash: contentHash, revisionHash: revisionHash)
    }
    public var isCandidate: Bool { enabled && (state == .idle || state == .starting || state == .ready) }
    public init(installationID: String, generation: Int, enabled: Bool, state: MusicScriptSourceItem.State, version: String,
                contentHash: String, revisionHash: String, capabilities: [MusicScriptCapability]) {
        self.installationID = installationID; self.generation = generation; self.enabled = enabled; self.state = state
        self.version = version; self.contentHash = contentHash; self.revisionHash = revisionHash; self.capabilities = capabilities
    }
}

public struct MusicScriptRequest: Sendable {
    public let source: MusicScriptSource
    public var installationID: String { source.installationID }
    public var generation: Int { source.generation }
    public let siteID: String
    public let sourceKey: String
    public let quality: String
    public let musicInfo: Data
    public init(source: MusicScriptSource, siteID: String, sourceKey: String, quality: String, musicInfo: Data) {
        self.source = source; self.siteID = siteID
        self.sourceKey = sourceKey; self.quality = quality; self.musicInfo = musicInfo
    }
}

public struct MusicScriptResult<Value: Sendable>: Sendable {
    public let value: Value
    public let source: MusicScriptSource
    public init(value: Value, source: MusicScriptSource) { self.value = value; self.source = source }
}

public struct MusicScriptSourceItem: Sendable, Equatable, Identifiable {
    public enum Origin: Sendable, Equatable { case file, network(host: String) }
    public enum State: String, Sendable, Equatable { case disabled, idle, starting, ready, failed }
    public let id: String
    public let name: String
    public let version: String
    public let author: String
    public let summary: String
    public let homepage: String
    public let origin: Origin
    public let platforms: [MusicPublicBrowsePlatform]
    public let enabled: Bool
    public let state: State
    public let check: MusicScriptSourceCheck?
    public init(id: String, name: String, version: String, author: String = "", summary: String = "",
                homepage: String = "", origin: Origin, platforms: [MusicPublicBrowsePlatform],
                enabled: Bool, state: State, check: MusicScriptSourceCheck? = nil) {
        self.id = id; self.name = name; self.version = version; self.author = author
        self.summary = summary; self.homepage = homepage; self.origin = origin
        self.platforms = platforms; self.enabled = enabled; self.state = state; self.check = check
    }
}

public struct MusicScriptSourceCheck: Sendable, Equatable {
    public let summary: String
    public let checkedAt: Date
    public let platforms: [String: String]
    public init(summary: String, checkedAt: Date, platforms: [String: String]) {
        self.summary = summary; self.checkedAt = checkedAt; self.platforms = platforms
    }
}

public struct MusicScriptSourceDetail: Sendable {
    public let item: MusicScriptSourceItem
    public let networkURL: String?
    public let rollbackAvailable: Bool
    public init(item: MusicScriptSourceItem, networkURL: String?, rollbackAvailable: Bool) {
        self.item = item; self.networkURL = networkURL; self.rollbackAvailable = rollbackAvailable
    }
}

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

public struct MusicAudioFileCachePolicy: Sendable {
    public let allowed: Bool
    public init(allowed: Bool) { self.allowed = allowed }
    public static let denied = Self(allowed: false)
}

public struct MusicAudioFileCacheVersion: Equatable, Sendable {
    public let rawValue: String
    public init(rawValue: String) { self.rawValue = rawValue }
}

public final class MusicAudioFileReadLease: @unchecked Sendable {
    private let lock = NSLock()
    private var onRelease: (@Sendable () -> Void)?
    public init(onRelease: @escaping @Sendable () -> Void) { self.onRelease = onRelease }
    public func release() {
        let callback: (@Sendable () -> Void)? = lock.withLock {
            let current = onRelease
            onRelease = nil
            return current
        }
        callback?()
    }
    deinit { release() }
}

public struct MusicAudioFileCacheStats: Sendable, Equatable {
    public let entryCount: Int, totalDiskBytes: Int64, reservedBytes: Int64
    public let pendingDeleteCount: Int, pendingDeleteBytes: Int64
    public let activeReadLeases: Int, pendingCaptures: Int, capacityBytes: Int64, closed: Bool
    public init(entryCount: Int, totalDiskBytes: Int64, reservedBytes: Int64, pendingDeleteCount: Int,
                pendingDeleteBytes: Int64, activeReadLeases: Int, pendingCaptures: Int, capacityBytes: Int64, closed: Bool) {
        self.entryCount = entryCount; self.totalDiskBytes = totalDiskBytes; self.reservedBytes = reservedBytes
        self.pendingDeleteCount = pendingDeleteCount; self.pendingDeleteBytes = pendingDeleteBytes
        self.activeReadLeases = activeReadLeases; self.pendingCaptures = pendingCaptures
        self.capacityBytes = capacityBytes; self.closed = closed
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

public struct MusicCachedPlayback: Sendable {
    public let candidate: MusicPlaybackCandidate
    public let selection: MusicPlaybackSelection
    public init(candidate: MusicPlaybackCandidate, selection: MusicPlaybackSelection) {
        self.candidate = candidate; self.selection = selection
    }
}

public struct MusicNativeCacheScope: Sendable, Equatable {
    public let scope: String
    public let revision: UUID
    public let authorization: MusicQueueAuthorization

    public init(scope: String, revision: UUID = UUID(),
                authorization: MusicQueueAuthorization = MusicQueueAuthorization()) {
        self.scope = scope; self.revision = revision; self.authorization = authorization
    }

    public static func == (lhs: Self, rhs: Self) -> Bool {
        lhs.scope == rhs.scope && lhs.revision == rhs.revision && lhs.authorization === rhs.authorization
    }
}

public enum MusicNativeCacheLifecycleEvent: Sendable, Equatable {
    case accountChanged
    case closed
}

public struct MusicLyricDisplaySnapshot: Codable, Sendable {
    public let songID: String
    public let sourceKey: String
    public let lines: [MusicLyricLine]
    public init(songID: String, sourceKey: String, lines: [MusicLyricLine]) {
        self.songID = songID; self.sourceKey = sourceKey; self.lines = lines
    }
}

public struct MusicPresentationCacheStats: Sendable, Equatable {
    public let imageCount: Int, imageBytes: Int64, lyricCount: Int, lyricBytes: Int64
    public let totalBytes: Int64, capacityBytes: Int64, diskFileCount: Int, diskBytes: Int64, closed: Bool
    public init(imageCount: Int, imageBytes: Int64, lyricCount: Int, lyricBytes: Int64,
                totalBytes: Int64, capacityBytes: Int64, diskFileCount: Int, diskBytes: Int64, closed: Bool) {
        self.imageCount = imageCount; self.imageBytes = imageBytes
        self.lyricCount = lyricCount; self.lyricBytes = lyricBytes
        self.totalBytes = totalBytes; self.capacityBytes = capacityBytes
        self.diskFileCount = diskFileCount; self.diskBytes = diskBytes; self.closed = closed
    }
}
