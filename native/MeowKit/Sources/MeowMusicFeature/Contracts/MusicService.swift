import Foundation
import MeowContracts

/// Closed diagnostics. Provider URLs, credentials and arbitrary error messages
/// never become observable business state.
public enum MusicServiceError: String, Error, Sendable, LocalizedError {
    case invalidRequest = "INVALID_REQUEST", invalidResponse = "INVALID_RESPONSE", limit = "LIMIT"
    case cancelled = "CANCELLED", timeout = "TIMEOUT", closed = "CLOSED", busy = "BUSY"
    case unavailable = "UNAVAILABLE", accessUnavailable = "ACCESS_UNAVAILABLE", staleAccess = "STALE_ACCESS"
    case staleResult = "STALE_RESULT", staleRevision = "STALE_REVISION"
    case storageFailed = "STORAGE_FAILED", storageConflict = "STORAGE_CONFLICT"
    case storagePolicyMismatch = "STORAGE_POLICY_MISMATCH", storagePermissionDenied = "STORAGE_PERMISSION_DENIED"
    case commitUnconfirmed = "COMMIT_UNCONFIRMED", corruptDocument = "CORRUPT_DOCUMENT"
    case notFound = "NOT_FOUND", handleExpired = "HANDLE_EXPIRED"
    case sourceRequired = "SOURCE_REQUIRED", sourceUnavailable = "SOURCE_UNAVAILABLE", targetChanged = "TARGET_CHANGED"
    case qualityDisabled = "QUALITY_DISABLED", upstreamFailed = "UPSTREAM_FAILED", upstreamInvalid = "UPSTREAM_INVALID"
    case authRequired = "AUTH_REQUIRED", verificationRequired = "VERIFICATION_REQUIRED", rateLimited = "RATE_LIMITED"
    case sessionExpired = "SESSION_EXPIRED"
    case unsupported = "UNSUPPORTED", invalidMediaCandidate = "INVALID_MEDIA_CANDIDATE"
    case invalidImport = "INVALID_IMPORT", revisionConflict = "REVISION_CONFLICT"
    case nativeIDConflict = "NATIVE_ID_CONFLICT", songIDConflict = "SONG_ID_CONFLICT", favoritesProtected = "FAVORITES_PROTECTED"
    case lyricIdentityMismatch = "LYRIC_IDENTITY_MISMATCH", lyricSessionChanged = "LYRIC_SESSION_CHANGED"
    case invalidLyricResponse = "INVALID_LYRIC_RESPONSE", lyricFetchFailed = "LYRIC_FETCH_FAILED", lyricTimeout = "LYRIC_TIMEOUT"
    case resolutionFailed = "RESOLUTION_FAILED", resolutionTimeout = "RESOLUTION_TIMEOUT"
    case permissionDenied = "PERMISSION_DENIED", previewOnly = "PREVIEW_ONLY"
    case networkOffline = "NETWORK_OFFLINE", connectionFailed = "CONNECTION_FAILED"
    case requestRejected = "REQUEST_REJECTED", audioUnavailable = "AUDIO_UNAVAILABLE"
    case contentUnavailable = "CONTENT_UNAVAILABLE"
    case invalidScript = "INVALID_SCRIPT", insecureScriptURL = "INSECURE_SCRIPT_URL"
    case scriptRedirect = "SCRIPT_REDIRECT", scriptDownloadFailed = "SCRIPT_DOWNLOAD_FAILED"
    case scriptFailed = "SCRIPT_FAILED", scriptRuntimeUnavailable = "SCRIPT_RUNTIME_UNAVAILABLE"
    case scriptCleanupFailed = "SCRIPT_CLEANUP_FAILED"

    public var errorDescription: String? {
        switch self {
        case .cancelled: return "已取消"
        case .accessUnavailable, .staleAccess: return "音乐数据不可用"
        case .sourceRequired: return "未选择音源"
        case .sourceUnavailable: return "音源不可用"
        case .targetChanged, .staleResult, .staleRevision, .revisionConflict, .storageConflict: return "内容已更改"
        case .authRequired: return "未登录"
        case .sessionExpired: return "登录已失效，需要重新登录"
        case .permissionDenied: return "平台未授予此内容的访问或播放权限"
        case .previewOnly: return "平台当前仅提供试听，无法完整播放"
        case .networkOffline: return "网络已断开"
        case .connectionFailed: return "无法连接平台服务"
        case .requestRejected: return "平台拒绝了本次请求，原因尚未明确"
        case .audioUnavailable: return "平台未返回可播放地址，原因尚未明确"
        case .contentUnavailable: return "内容当前不可见或不可用"
        case .verificationRequired: return "平台需要验证"
        case .rateLimited: return "请求过于频繁"
        case .qualityDisabled: return "音质不可用"
        case .limit: return "超过大小限制"
        case .timeout, .lyricTimeout, .resolutionTimeout: return "请求超时"
        case .commitUnconfirmed: return "保存结果未确认"
        case .storageFailed: return "保存失败"
        case .storagePolicyMismatch: return "本机密钥保护属性不匹配"
        case .storagePermissionDenied: return "当前安装缺少钥匙串访问权限，请检查签名"
        case .corruptDocument: return "音乐数据损坏"
        case .invalidImport: return "曲库文件无效"
        case .favoritesProtected: return "收藏夹不可删除"
        case .handleExpired: return "内容已过期"
        case .notFound: return "内容不存在"
        case .busy: return "音乐服务忙"
        case .lyricIdentityMismatch, .lyricSessionChanged, .invalidLyricResponse, .lyricFetchFailed: return "歌词获取失败"
        case .invalidMediaCandidate, .resolutionFailed: return "播放地址获取失败"
        case .upstreamInvalid, .invalidResponse: return "平台响应格式异常"
        case .upstreamFailed: return "平台返回失败，原因尚未明确"
        case .unavailable: return "服务未能完成请求，原因尚未明确"
        case .invalidRequest: return "请求参数无效"
        case .closed: return "服务已关闭"
        case .unsupported: return "平台不支持此操作"
        case .invalidScript: return "音源脚本无效"
        case .insecureScriptURL: return "仅支持 HTTPS 地址"
        case .scriptRedirect: return "音源地址发生重定向"
        case .scriptDownloadFailed: return "音源下载失败"
        case .scriptFailed: return "音源脚本运行失败"
        case .scriptRuntimeUnavailable: return "音源运行环境不可用"
        case .scriptCleanupFailed: return "音源运行资源未能完全释放"
        default: return "请求未完成（\(rawValue)）"
        }
    }

    public var isAuthenticationFailure: Bool { self == .authRequired || self == .sessionExpired }

    public static func sanitized(_ error: Error) -> MusicServiceError {
        if Task.isCancelled || error is CancellationError { return .cancelled }
        if let error = error as? URLError {
            switch error.code {
            case .cancelled: return .cancelled
            case .timedOut: return .timeout
            case .notConnectedToInternet: return .networkOffline
            default: return .connectionFailed
            }
        }
        if let error = error as? MusicSourceDeclarationFailure { return error.failure }
        return error as? MusicServiceError ?? .unavailable
    }

    /// Controlled UI context; never interpolates the upstream error body.
    public func message(operation: String, platform: String? = nil,
                        diagnosticID: String? = nil) -> String {
        let context = [platform, operation].compactMap { $0 }.filter { !$0.isEmpty }.joined(separator: " · ")
        let detail = self == .authRequired
            ? "此操作需要登录对应平台"
            : errorDescription ?? rawValue
        let diagnostic = diagnosticID.flatMap {
            $0.range(of: "^[A-Fa-f0-9]{8}$", options: .regularExpression) == nil ? nil : $0
        }
        return "\(context)：\(detail)" + (diagnostic.map { "（诊断 \($0)）" } ?? "")
    }
}

public struct MusicReference: Codable, Hashable, Sendable {
    public enum Kind: String, Codable, Sendable { case handle, library, queue }
    public let kind: Kind
    public let id: String
    public init(kind: Kind, id: String) { self.kind = kind; self.id = id }
    public static func == (lhs: Self, rhs: Self) -> Bool {
        lhs.kind == rhs.kind && lhs.id.utf8.elementsEqual(rhs.id.utf8)
    }
    public func hash(into hasher: inout Hasher) {
        hasher.combine(kind); hasher.combine(id.utf8.count)
        for byte in id.utf8 { hasher.combine(byte) }
    }
}

public struct MusicNativeSource: Codable, Equatable, Sendable, Identifiable {
    public let sourceKey: String
    public let platform: String
    public let nativeId: String
    public var id: String { sourceKey }
    public init(sourceKey: String, platform: String, nativeId: String) {
        self.sourceKey = sourceKey; self.platform = platform; self.nativeId = nativeId
    }
    public static func == (lhs: Self, rhs: Self) -> Bool {
        lhs.sourceKey.utf8.elementsEqual(rhs.sourceKey.utf8) && lhs.platform == rhs.platform &&
        lhs.nativeId.utf8.elementsEqual(rhs.nativeId.utf8)
    }
}

public struct MusicRemoteTrack: Codable, Equatable, Sendable, Identifiable {
    public let ref: MusicReference
    public let songId: String
    public let title: String
    public let artist: String
    public let album: String
    public let duration: Double
    public let sources: [MusicNativeSource]
    public let artwork: URL?
    public var id: MusicReference { ref }
    public init(ref: MusicReference, songId: String, title: String, artist: String = "", album: String = "",
                duration: Double = 0, sources: [MusicNativeSource] = [], artwork: URL? = nil) {
        self.ref = ref; self.songId = songId; self.title = title; self.artist = artist
        self.album = album; self.duration = duration; self.sources = sources
        self.artwork = artwork
    }
}

public struct MusicPlatform: Codable, Equatable, Sendable, Identifiable {
    public let id: String
    public let title: String
    public let search: Bool
    public let lyrics: Bool
    public let accountState: String
    public init(id: String, title: String, search: Bool = true, lyrics: Bool = true, accountState: String = "unauthorized") {
        self.id = id; self.title = title; self.search = search; self.lyrics = lyrics; self.accountState = accountState
    }
}

public enum MusicPlaybackMode: Int, Codable, CaseIterable, Sendable {
    case loop = 1, shuffle = 2, single = 3
    public var title: String {
        switch self { case .loop: return "列表循环"; case .shuffle: return "随机播放"; case .single: return "单曲循环" }
    }
    public var symbol: String {
        switch self { case .loop: return "repeat"; case .shuffle: return "shuffle"; case .single: return "repeat.1" }
    }
}

public struct MusicPlaybackState: Codable, Equatable, Sendable {
    public let revision: Int
    public let settingsRevision: Int
    public let mode: MusicPlaybackMode
    public let requestQuality: String
    public let qualityEnabled: [String]
    public let qualityOrder: [String]
    public let spatialEnabled: [String]
    public let spatialOrder: [String]
    public let lyricsEnabled: Bool
    public let showTranslation: Bool
    /// Built-in audio-source admission; it never changes accounts, search, lyrics or artwork.
    public let officialSources: [MusicAccountPlatform]
    public let currentId: String?
    public let total: Int
    public let position: Double
    public let canNext: Bool
    public let canPrevious: Bool
    /// Read-only occurrence previews. nil also means the next choice is not
    /// known yet (shuffle/recommendation), not that navigation is forbidden.
    /// These DTO fields are derived from the queue; they are not persisted.
    public let nextPreviewID: String?
    public let previousPreviewID: String?
    public let sourceCollectionID: String?
    public let queueSourceTitle: String?
    public init(revision: Int = 0, settingsRevision: Int = 0, mode: MusicPlaybackMode = .loop,
                qualityEnabled: [String] = ["standard"], qualityOrder: [String] = ["standard"],
                spatialEnabled: [String] = [], spatialOrder: [String] = [],
                lyricsEnabled: Bool = true, showTranslation: Bool = true,
                officialSources: [MusicAccountPlatform] = MusicAccountPlatform.allCases, currentId: String? = nil,
                total: Int = 0, position: Double = 0, canNext: Bool = false, canPrevious: Bool = false,
                nextPreviewID: String? = nil, previousPreviewID: String? = nil,
                sourceCollectionID: String? = nil, requestQuality: String = "flac",
                queueSourceTitle: String? = nil) {
        self.revision = revision; self.settingsRevision = settingsRevision; self.mode = mode
        self.requestQuality = requestQuality
        self.qualityEnabled = qualityEnabled; self.qualityOrder = qualityOrder
        self.spatialEnabled = spatialEnabled; self.spatialOrder = spatialOrder
        self.lyricsEnabled = lyricsEnabled; self.showTranslation = showTranslation
        self.officialSources = officialSources
        self.currentId = currentId; self.total = total; self.position = position
        self.canNext = canNext; self.canPrevious = canPrevious
        self.nextPreviewID = nextPreviewID; self.previousPreviewID = previousPreviewID
        self.sourceCollectionID = sourceCollectionID
        self.queueSourceTitle = queueSourceTitle
    }
}

public struct MusicCollection: Codable, Equatable, Sendable, Identifiable {
    public let id: String
    public let title: String
    public let kind: String
    public let count: Int
    public let revision: Int
    public let artwork: URL?
    public let networkReference: MusicNetworkPlaylistReference?
    public var canEdit: Bool { networkReference == nil && ["playlist", "favorites"].contains(kind) }
    public var remoteCollection: MusicPublicBrowseCollection? {
        networkReference.map {
            .init(platform: $0.platform, nativeID: $0.remoteID, title: title,
                  creator: $0.creator, artwork: artwork, count: count)
        }
    }
    public init(id: String, title: String, kind: String = "playlist", count: Int = 0, revision: Int = 0,
                artwork: URL? = nil, networkReference: MusicNetworkPlaylistReference? = nil) {
        self.id = id; self.title = title; self.kind = kind; self.count = count; self.revision = revision
        self.artwork = artwork; self.networkReference = networkReference
    }
}

public struct MusicQueueEntry: Codable, Equatable, Sendable, Identifiable {
    public let id: String
    public let track: MusicRemoteTrack
    public let sourceTitle: String?
    /// Read-only occurrence provenance. A session ID is never authorization.
    public let remoteSessionID: UUID?
    public let displayIdentity: MusicQueueDisplayIdentity?
    public init(id: String, track: MusicRemoteTrack, sourceTitle: String? = nil, remoteSessionID: UUID? = nil,
                displayIdentity: MusicQueueDisplayIdentity? = nil) {
        self.id = id; self.track = track; self.sourceTitle = sourceTitle; self.remoteSessionID = remoteSessionID
        self.displayIdentity = displayIdentity
    }
}
public struct MusicQueueSnapshot: Sendable {
    public let revision: Int
    public let currentId: String?
    public let entries: [MusicQueueEntry]
    public init(revision: Int, currentId: String?, entries: [MusicQueueEntry]) {
        self.revision = revision; self.currentId = currentId; self.entries = entries
    }
}
public struct MusicSearchPage: Sendable {
    public let platform: String
    public let query: String
    public let page: Int
    public let limit: Int
    public let total: Int
    public let nextPage: Int?
    public let items: [MusicRemoteTrack]
    public let observation: MusicSearchObservation?
    public init(platform: String, query: String, page: Int, limit: Int = 30, total: Int, nextPage: Int?,
                items: [MusicRemoteTrack], observation: MusicSearchObservation? = nil) {
        self.platform = platform; self.query = query; self.page = page; self.limit = limit
        self.total = total; self.nextPage = nextPage; self.items = items
        self.observation = observation
    }
}

public enum MusicQueueChange: Sendable {
    case replace([MusicReference], index: Int)
    case append([MusicReference], next: Bool)
    case select(String), remove(String), reorder([String]), clear
    case collection(id: String, revision: Int, index: Int)
}
public enum MusicDirection: String, Sendable { case next, previous }
public enum MusicAdvanceReason: String, Sendable { case manual, ended }
public struct MusicAdvanceResult: Sendable {
    public let state: MusicPlaybackState
    public let selectedId: String?
    public init(state: MusicPlaybackState, selectedId: String?) { self.state = state; self.selectedId = selectedId }
}
public struct MusicSettingsChange: Sendable {
    public let ifRevision: Int
    public var mode: MusicPlaybackMode?
    public var requestQuality: String?
    public var qualityEnabled: [String]?
    public var qualityOrder: [String]?
    public var spatialEnabled: [String]?
    public var spatialOrder: [String]?
    public var lyricsEnabled: Bool?
    public var showTranslation: Bool?
    public var officialSources: [MusicAccountPlatform]?
    public init(ifRevision: Int, mode: MusicPlaybackMode? = nil, qualityEnabled: [String]? = nil,
                qualityOrder: [String]? = nil, spatialEnabled: [String]? = nil, spatialOrder: [String]? = nil,
                lyricsEnabled: Bool? = nil, showTranslation: Bool? = nil,
                officialSources: [MusicAccountPlatform]? = nil, requestQuality: String? = nil) {
        self.requestQuality = requestQuality
        self.ifRevision = ifRevision; self.mode = mode; self.qualityEnabled = qualityEnabled; self.qualityOrder = qualityOrder
        self.spatialEnabled = spatialEnabled; self.spatialOrder = spatialOrder
        self.lyricsEnabled = lyricsEnabled; self.showTranslation = showTranslation
        self.officialSources = officialSources
    }
}

public struct MusicPlaybackSelection: Codable, Equatable, Sendable {
    public let installationId: String
    public let generation: Int
    public let siteId: String
    public let sourceKey: String
    public let quality: String
    /// Present for LX selections; official accounts keep their own identity.
    public let scriptIdentity: MusicScriptIdentity?
    public init(installationId: String, generation: Int, siteId: String, sourceKey: String, quality: String,
                scriptIdentity: MusicScriptIdentity? = nil) {
        self.installationId = installationId; self.generation = generation; self.siteId = siteId
        self.sourceKey = sourceKey; self.quality = quality
        self.scriptIdentity = scriptIdentity
    }
}
public struct MusicPlaybackOption: Codable, Equatable, Sendable, Identifiable {
    public let installationId: String
    public let generation: Int
    public let siteId: String
    public let sourceKey: String
    public let qualities: [String]
    public let title: String
    public let version: String
    public let contentHash: String
    public let revisionHash: String?
    public var id: String { installationId + ":" + siteId + ":" + sourceKey }
    public init(installationId: String, generation: Int, siteId: String, sourceKey: String,
                qualities: [String], title: String, version: String, contentHash: String, revisionHash: String? = nil) {
        self.installationId = installationId; self.generation = generation; self.siteId = siteId; self.sourceKey = sourceKey
        self.qualities = qualities; self.title = title; self.version = version; self.contentHash = contentHash
        self.revisionHash = revisionHash
    }
    public func selection(quality: String) -> MusicPlaybackSelection {
        MusicPlaybackSelection(installationId: installationId, generation: generation, siteId: siteId, sourceKey: sourceKey,
            quality: quality, scriptIdentity: revisionHash.map {
                .init(version: version, contentHash: contentHash, revisionHash: $0)
            })
    }
}
public struct MusicPlaybackCandidate: Sendable {
    public let attemptId: String
    public let occurrenceId: String
    public let resourceId: String
    public let url: URL
    public let headers: [String: String]
    public let title: String
    public let startPosition: Double
    public let requestedQuality: String
    /// Actual executed selection; a cold request is bound to its positive generation.
    public let resolvedSelection: MusicPlaybackSelection
    public let expiresAt: Double?
    public let fileAuthorization: MusicQueueAuthorization?
    public let playbackAuthorization: MusicQueueAuthorization?
    public let delivery: MusicAudioDelivery?
    public let byteSource: (any PlaybackByteSource)?
    public var isCachedAudio: Bool { fileAuthorization != nil }
    public init(attemptId: String, occurrenceId: String, resourceId: String, url: URL, headers: [String: String] = [:],
                title: String, startPosition: Double = 0, requestedQuality: String,
                resolvedSelection: MusicPlaybackSelection, expiresAt: Double? = nil,
                fileAuthorization: MusicQueueAuthorization? = nil, delivery: MusicAudioDelivery? = nil,
                playbackAuthorization: MusicQueueAuthorization? = nil,
                byteSource: (any PlaybackByteSource)? = nil) {
        self.attemptId = attemptId; self.occurrenceId = occurrenceId; self.resourceId = resourceId; self.url = url
        self.headers = headers; self.title = title; self.startPosition = startPosition
        self.requestedQuality = requestedQuality; self.expiresAt = expiresAt
        self.resolvedSelection = resolvedSelection
        self.fileAuthorization = fileAuthorization
        self.delivery = delivery; self.playbackAuthorization = playbackAuthorization
        self.byteSource = byteSource
    }
    func validateResolvedSelection(for requested: MusicPlaybackSelection) throws {
        let resolved = resolvedSelection, official = requested.installationId.hasPrefix("official:")
        guard resolved.installationId.utf8.elementsEqual(requested.installationId.utf8),
              resolved.siteId.utf8.elementsEqual(requested.siteId.utf8),
              resolved.sourceKey.utf8.elementsEqual(requested.sourceKey.utf8),
              resolved.quality.utf8.elementsEqual(requested.quality.utf8),
              requestedQuality.utf8.elementsEqual(requested.quality.utf8),
              resolved.scriptIdentity == requested.scriptIdentity,
              official || isCachedAudio && url.isFileURL || resolved.scriptIdentity != nil,
              resolved.generation > 0 && resolved.generation <= 9_007_199_254_740_991,
              resolved.generation == requested.generation || !official && requested.generation == 0 else {
            throw MusicServiceError.invalidResponse
        }
    }
    public func resource() throws -> PlaybackResource {
        try fileAuthorization?.check()
        try playbackAuthorization?.check()
        return try PlaybackResource(id: resourceId, url: url, title: title, headers: headers, startPosition: startPosition,
                                    byteSource: byteSource)
    }
}
public struct MusicPlaybackFact: Sendable {
    public enum Kind: String, Sendable { case playing, progress, paused, ended, failed, stopped }
    public let attemptId: String
    public let occurrenceId: String
    public let resourceId: String
    public let kind: Kind
    public let position: Double
    public let duration: Double?
    public init(attemptId: String, occurrenceId: String, resourceId: String, kind: Kind, position: Double, duration: Double? = nil) {
        self.attemptId = attemptId; self.occurrenceId = occurrenceId; self.resourceId = resourceId
        self.kind = kind; self.position = position; self.duration = duration
    }
}

public struct MusicLyricWord: Codable, Equatable, Sendable {
    public let time: Double
    /// Source duration, or the interval to a subsequent real lyric marker.
    /// Zero highlights immediately at `time` and holds until the line leaves.
    public let duration: Double
    public let text: String
    public init(time: Double, duration: Double, text: String) { self.time = time; self.duration = duration; self.text = text }
}
public struct MusicLyricLine: Codable, Equatable, Sendable, Identifiable {
    public let index: Int
    /// Nonnegative line seek position; source word times may precede zero.
    public let time: Double
    /// Line display boundary; never a substitute for a word's missing end marker.
    public let end: Double
    public let text: String
    public let translation: String
    /// Empty for line-only lyrics. Entries require real source timestamps.
    public let words: [MusicLyricWord]
    public var id: Int { index }
    public init(index: Int, time: Double, end: Double, text: String, translation: String = "",
                words: [MusicLyricWord] = []) {
        self.index = index; self.time = time; self.end = end; self.text = text; self.translation = translation
        self.words = words
    }
}
public struct MusicLyricsSnapshot: Sendable {
    public enum State: String, Sendable { case ready, none, disabled }
    public let occurrenceId: String
    public let state: State
    public let lines: [MusicLyricLine]
    public init(occurrenceId: String, state: State, lines: [MusicLyricLine]) {
        self.occurrenceId = occurrenceId; self.state = state; self.lines = lines
    }
}

/// Same last-at-or-before and collapsed three-line centering semantics as the
/// core LyricTiming/locateLyricLine functions. Uses real native media time.
public enum MusicLyricTimeline {
    public static func activeIndex(lines: [MusicLyricLine], at time: Double) -> Int? {
        let value = time.isFinite ? max(0, time) : 0
        var lo = 0, hi = lines.count
        while lo < hi {
            let mid = (lo + hi) / 2
            if lines[mid].time <= value { lo = mid + 1 } else { hi = mid }
        }
        return lo > 0 ? lo - 1 : nil
    }
    public static func wordProgress(_ word: MusicLyricWord, at time: Double) -> Double {
        guard time.isFinite else { return 0 }
        if word.duration <= 0 { return time >= word.time ? 1 : 0 }
        return min(1, max(0, (time - word.time) / word.duration))
    }
    public static func preview(lines: [MusicLyricLine], at time: Double) -> [MusicLyricLine] {
        guard !lines.isEmpty else { return [] }
        let active = activeIndex(lines: lines, at: time) ?? 0
        if lines.count <= 3 { return lines }
        let center = max(1, min(lines.count - 2, active))
        return Array(lines[(center - 1)...(center + 1)])
    }
}

public struct MusicPlayedSong: Sendable, Identifiable {
    public let date: String
    public let at: Double
    public let track: MusicRemoteTrack
    public let displayIdentity: MusicQueueDisplayIdentity?
    /// Multiple plays of the same recording on one day remain distinct rows.
    public var id: String { date + ":" + String(at.bitPattern, radix: 16) + ":" + track.songId }
    public init(date: String, at: Double, track: MusicRemoteTrack,
                displayIdentity: MusicQueueDisplayIdentity? = nil) {
        self.date = date; self.at = at; self.track = track
        self.displayIdentity = displayIdentity
    }
}
public struct MusicPlayedPlaylist: Codable, Sendable, Identifiable {
    public let id: String
    public let title: String
    public let count: Int
    public let at: Double
    public init(id: String, title: String, count: Int, at: Double) { self.id = id; self.title = title; self.count = count; self.at = at }
}
/// A single injected business port. No runtime connection, storage authority,
/// player, credential lookup or periodic polling is created by this protocol.
public protocol MusicServing: MusicSourceAvailabilityProviding, MusicMetadataPreferencesServing {
    func state() async throws -> MusicPlaybackState
    func platforms() async throws -> [MusicPlatform]
    func search(platform: String, query: String, page: Int, limit: Int) async throws -> MusicSearchPage
    func collections() async throws -> [MusicCollection]
    func tracks(in collection: MusicCollection) async throws -> [MusicRemoteTrack]
    func createPlaylist(name: String, refs: [MusicReference]) async throws -> MusicCollection
    func editPlaylist(_ collection: MusicCollection, name: String?, refs: [MusicReference]?, order: [String]?) async throws -> MusicCollection
    func removePlaylist(_ collection: MusicCollection) async throws
    func queue() async throws -> MusicQueueSnapshot
    func changeQueue(_ change: MusicQueueChange, expectedRevision: Int) async throws -> MusicPlaybackState
    func advance(_ direction: MusicDirection, reason: MusicAdvanceReason, expectedRevision: Int) async throws -> MusicAdvanceResult
    func updateSettings(_ change: MusicSettingsChange) async throws -> MusicPlaybackState
    func resolve(attemptId: String, occurrenceId: String, selection: MusicPlaybackSelection) async throws -> MusicPlaybackCandidate
    func cancel(attemptId: String?) async throws
    func record(_ fact: MusicPlaybackFact) async throws -> Bool
    func lyrics(occurrenceId: String, sourceKey: String) async throws -> MusicLyricsSnapshot
    func playedSongs() async throws -> [MusicPlayedSong]
    func playedPlaylists() async throws -> [MusicPlayedPlaylist]
    func restoreHistory(id: String, expectedRevision: Int) async throws -> MusicPlaybackState
}
