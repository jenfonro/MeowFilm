import Foundation
import os
import MeowContracts

/// Closed diagnostics. Provider URLs, credentials and arbitrary error messages
/// never become observable business state.
public enum MusicServiceError: String, Error, Sendable, LocalizedError {
    case invalidRequest = "INVALID_REQUEST", invalidResponse = "INVALID_RESPONSE", limit = "LIMIT"
    case cancelled = "CANCELLED", timeout = "TIMEOUT", closed = "CLOSED", busy = "BUSY"
    case unavailable = "UNAVAILABLE", accessUnavailable = "ACCESS_UNAVAILABLE", staleAccess = "STALE_ACCESS"
    case staleResult = "STALE_RESULT", staleRevision = "STALE_REVISION"
    case storageFailed = "STORAGE_FAILED", storageConflict = "STORAGE_CONFLICT"
    case corruptStorage = "CORRUPT_STORAGE"
    case notFound = "NOT_FOUND"
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
    case scriptDownloadFailed = "SCRIPT_DOWNLOAD_FAILED"
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
        case .storageFailed: return "保存失败"
        case .corruptStorage: return "音乐数据损坏"
        case .invalidImport: return "曲库文件无效"
        case .favoritesProtected: return "收藏夹不可删除"
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
        case .insecureScriptURL: return "请输入有效的 .js 音源地址"
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

    public func message(operation: String, platform: String? = nil,
                        diagnosticID: String? = nil) -> String {
        let context = [platform, operation].compactMap { $0 }.filter { !$0.isEmpty }.joined(separator: " · ")
        let detail = self == .authRequired
            ? "此操作需要登录对应平台"
            : errorDescription ?? rawValue
        let diagnostic = diagnosticID.flatMap { value -> String? in
            let bytes = value.utf8
            guard bytes.count == 8, bytes.allSatisfy({
                (48...57).contains($0) || (65...70).contains($0) || (97...102).contains($0)
            }) else { return nil }
            return value
        }
        return "\(context)：\(detail)" + (diagnostic.map { "（诊断 \($0)）" } ?? "")
    }
}

public struct MusicSourceDeclarationFailure: Error, Sendable {
    public let failure: MusicServiceError
    public init(failure: MusicServiceError) { self.failure = failure }
}

public enum MusicSourceFailureClassifier {
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

    public static func isContentFailure(_ failure: MusicServiceError) -> Bool {
        switch failure {
        case .notFound, .contentUnavailable, .audioUnavailable, .permissionDenied, .previewOnly:
            return true
        default: return false
        }
    }
}

public enum MusicAccountPlatform: String, Codable, CaseIterable, Sendable, Identifiable {
    case netease = "wy", qq = "tx", kugou = "kg"
    public var id: String { rawValue }
    public var title: String {
        switch self { case .netease: return "网易云音乐"; case .qq: return "QQ 音乐"; case .kugou: return "酷狗音乐" }
    }
}

public struct MusicAccountStatus: Sendable, Equatable, Identifiable {
    public enum Phase: String, Sendable {
        case signedOut, signingIn, requiresValidation, signedIn, unavailable, removalPending
        public var title: String {
            switch self {
            case .signedOut: "未登录"; case .signingIn: "登录中"; case .requiresValidation: "待验证"
            case .signedIn: "已登录"; case .unavailable: "不可用"; case .removalPending: "退出未完成"
            }
        }
    }
    public let platform: MusicAccountPlatform, phase: Phase, nickname: String, canLogin: Bool
    public let profile: MusicAccountProfile?, playbackFeedbackAvailable: Bool
    public var id: MusicAccountPlatform { platform }
    public init(platform: MusicAccountPlatform, phase: Phase, nickname: String = "", canLogin: Bool = false,
                profile: MusicAccountProfile? = nil, playbackFeedbackAvailable: Bool = false) {
        self.platform = platform; self.phase = phase; self.nickname = nickname; self.canLogin = canLogin
        self.profile = profile; self.playbackFeedbackAvailable = playbackFeedbackAvailable
    }
}

public struct MusicAccountMembership: Codable, Sendable, Equatable {
    public let vip: Bool?, tier: String, expiresAt: Date?
    public init(vip: Bool? = nil, tier: String = "", expiresAt: Date? = nil) {
        self.vip = vip; self.tier = tier; self.expiresAt = expiresAt
    }
    public init(from decoder: Decoder) throws {
        enum K: String, CodingKey { case vip, tier, expiresAt }
        let c = try decoder.container(keyedBy: K.self)
        vip = try c.decodeIfPresent(Bool.self, forKey: .vip)
        tier = try c.decodeIfPresent(String.self, forKey: .tier) ?? ""
        expiresAt = try c.decodeIfPresent(Date.self, forKey: .expiresAt)
    }
    public func title(at date: Date) -> String {
        guard let vip else { return "会员状态未知" }
        if let expiresAt, expiresAt <= date { return "会员已过期" }
        return vip ? (tier.isEmpty ? "VIP" : tier) + " · 会员有效" : "非会员"
    }
}

public struct MusicAccountProfile: Codable, Sendable, Equatable {
    public let nickname: String, avatarURL: URL?, membership: MusicAccountMembership
    public init(nickname: String = "", avatarURL: URL? = nil, membership: MusicAccountMembership = .init()) {
        self.nickname = nickname; self.avatarURL = avatarURL; self.membership = membership
    }
    public init(from decoder: Decoder) throws {
        enum K: String, CodingKey { case nickname, avatarURL, membership }
        let c = try decoder.container(keyedBy: K.self)
        nickname = try c.decodeIfPresent(String.self, forKey: .nickname) ?? ""
        avatarURL = try c.decodeIfPresent(URL.self, forKey: .avatarURL)
        membership = try c.decodeIfPresent(MusicAccountMembership.self, forKey: .membership) ?? .init()
    }
}

public enum MusicAccountLoginMethod: String, Sendable, CaseIterable {
    case sms, qr
    public var title: String { self == .sms ? "验证码登录" : "二维码登录" }
}

public struct MusicSMSSendReceipt: Sendable, Equatable {
    public let retryAt: Date
    public init(retryAt: Date) { self.retryAt = retryAt }
}

public struct MusicSMSFailure: Error, LocalizedError, Sendable {
    public enum Detail: Sendable, Equatable { case invalidPhone, invalidCode, accountSelectionRequired }
    public let reason: MusicServiceError
    public let retryAt: Date?
    public let detail: Detail?
    public init(reason: MusicServiceError, retryAt: Date? = nil, detail: Detail? = nil) {
        self.reason = reason; self.retryAt = retryAt; self.detail = detail
    }
    public var errorDescription: String? {
        switch detail {
        case .invalidPhone: return "验证码发送失败，手机号无效"
        case .invalidCode: return "验证码错误或已过期"
        case .accountSelectionRequired: return "该手机号关联多个账号，请使用二维码登录"
        case nil: return reason.localizedDescription
        }
    }
}

public struct MusicLoginChallenge: Sendable, Identifiable {
    public let id: UUID
    public let platform: MusicAccountPlatform
    public let image: Data
    public let expiresAt: Date
    public init(id: UUID, platform: MusicAccountPlatform, image: Data, expiresAt: Date) {
        self.id = id; self.platform = platform; self.image = image; self.expiresAt = expiresAt
    }
}

public enum MusicLoginProgress: String, Sendable {
    case waiting, scanned, confirmed, expired
    public var title: String {
        switch self { case .waiting: return "等待扫码"; case .scanned: return "已扫码"; case .confirmed: return "已登录"; case .expired: return "二维码已过期" }
    }
}

public struct MusicBrowsePage: Sendable {
    public let items: [MusicRemoteTrack]
    public let cursor: String?
    public let dailyStyle: MusicDailyStyleSelection?
    public init(items: [MusicRemoteTrack], cursor: String?, dailyStyle: MusicDailyStyleSelection? = nil) {
        self.items = items; self.cursor = cursor; self.dailyStyle = dailyStyle
    }
}

public struct MusicAccountCollection: Codable, Equatable, Sendable, Identifiable {
    public let id: String
    public let title: String
    public let count: Int?
    public let owned: Bool
    public init(id: String, title: String, count: Int?, owned: Bool) {
        self.id = id; self.title = title; self.count = count; self.owned = owned
    }
}

public struct MusicAccountCollectionPage: Sendable {
    public let items: [MusicAccountCollection]
    public let cursor: String?
    public init(items: [MusicAccountCollection], cursor: String?) { self.items = items; self.cursor = cursor }
}

public struct MusicAccountFilePayload: Sendable, CustomStringConvertible, CustomDebugStringConvertible, CustomReflectable {
    private let bytes: Data
    public init(fileData: Data) throws { bytes = fileData }
    public func dataForFile() -> Data { bytes }
    public var description: String { "MusicAccountFilePayload(<redacted>)" }
    public var debugDescription: String { description }
    public var customMirror: Mirror { Mirror(self, children: ["value": "<redacted>"]) }
}

public enum MusicAccountTransferError: Error, LocalizedError, Sendable, Equatable {
    case invalidFile, noAccounts, fileOperation
    public var errorDescription: String? {
        switch self {
        case .invalidFile: return "账号文件格式无效"
        case .noAccounts: return "无可导出账号"
        case .fileOperation: return "账号文件无法读取或保存"
        }
    }
}

public enum MusicAccountImportPolicy: Sendable, Equatable { case skip, overwrite }

public final class MusicAccountImportPlan: @unchecked Sendable, CustomStringConvertible, CustomDebugStringConvertible, CustomReflectable {
    public let conflicts: [MusicAccountPlatform]
    public let id = UUID()
    private let lock = NSLock()
    private var cancellation: (@Sendable (UUID) -> Void)?
    public init(conflicts: [MusicAccountPlatform], cancel: @escaping @Sendable (UUID) -> Void) {
        self.conflicts = conflicts; cancellation = cancel
    }
    public func cancel() {
        lock.lock(); let action = cancellation; cancellation = nil; lock.unlock()
        action?(id)
    }
    public var isCancelled: Bool { lock.lock(); defer { lock.unlock() }; return cancellation == nil }
    deinit { cancel() }
    public var description: String { "MusicAccountImportPlan(<opaque>)" }
    public var debugDescription: String { description }
    public var customMirror: Mirror { Mirror(self, children: ["value": "<opaque>"]) }
}

public struct MusicAccountImportSummary: Sendable, Equatable {
    public var imported: [MusicAccountPlatform] = []
    public var skipped: [MusicAccountPlatform] = []
    public var failed: [MusicAccountPlatform] = []
    public var cancelled: [MusicAccountPlatform] = []
    public init() {}
    public var message: String {
        [("已导入", imported), ("已跳过", skipped), ("失败", failed), ("已取消", cancelled)]
            .map { title, platforms in
                title + " \(platforms.count)" + (platforms.isEmpty ? "" : "：" + platforms.map(\.title).joined(separator: "、"))
            }.joined(separator: "\n")
    }
}

public struct MusicReference: Codable, Hashable, Sendable {
    public enum Kind: String, Codable, Sendable { case library, queue }
    public let kind: Kind
    public let id: String
    public let occurrence: String?
    public init(kind: Kind, id: String, occurrence: String? = nil) { self.kind = kind; self.id = id; self.occurrence = occurrence }
}

public struct MusicNativeSource: Codable, Equatable, Sendable, Identifiable {
    public let sourceKey: String
    public let platform: String
    public let nativeId: String
    public var id: String { sourceKey }
    public init(sourceKey: String, platform: String, nativeId: String) {
        self.sourceKey = sourceKey; self.platform = platform; self.nativeId = nativeId
    }
}

public struct MusicRemoteTrack: Codable, Equatable, Sendable, Identifiable {
    public let ref: MusicReference
    public let songId: String
    public let title: String
    public let artist: String
    public let artistNames: [String]?
    public let album: String
    public let duration: Double
    public let sources: [MusicNativeSource]
    public let artwork: URL?
    public let sourcePosition: Int?
    public var id: MusicReference { ref }
    public init(ref: MusicReference, songId: String, title: String, artist: String = "", album: String = "",
                duration: Double = 0, sources: [MusicNativeSource] = [], artwork: URL? = nil, artistNames: [String]? = nil,
                sourcePosition: Int? = nil) {
        self.ref = ref; self.songId = songId; self.title = title; self.artist = artist
        self.album = album; self.duration = duration; self.sources = sources; self.artistNames = artistNames
        self.artwork = artwork
        self.sourcePosition = sourcePosition
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
    public let revision: Int, settingsRevision: Int, mode: MusicPlaybackMode, requestQuality: String
    public let qualityEnabled: [String], qualityOrder: [String], spatialEnabled: [String], spatialOrder: [String]
    public let lyricsEnabled: Bool, showTranslation: Bool, officialSources: [MusicAccountPlatform]
    public let currentId: String?, total: Int, position: Double, canNext: Bool, canPrevious: Bool
    public let nextPreviewID: String?, previousPreviewID: String?, sourceCollectionID: String?, queueSourceTitle: String?
    public init(revision: Int = 0, settingsRevision: Int = 0, mode: MusicPlaybackMode = .loop,
                qualityEnabled: [String] = ["standard"], qualityOrder: [String] = ["standard"],
                spatialEnabled: [String] = [], spatialOrder: [String] = [],
                lyricsEnabled: Bool = true, showTranslation: Bool = true,
                officialSources: [MusicAccountPlatform] = MusicAccountPlatform.allCases, currentId: String? = nil,
                total: Int = 0, position: Double = 0, canNext: Bool = false, canPrevious: Bool = false,
                nextPreviewID: String? = nil, previousPreviewID: String? = nil,
                sourceCollectionID: String? = nil, requestQuality: String = "flac", queueSourceTitle: String? = nil) {
        self.revision = revision; self.settingsRevision = settingsRevision; self.mode = mode
        self.requestQuality = requestQuality; self.qualityEnabled = qualityEnabled; self.qualityOrder = qualityOrder
        self.spatialEnabled = spatialEnabled; self.spatialOrder = spatialOrder
        self.lyricsEnabled = lyricsEnabled; self.showTranslation = showTranslation
        self.officialSources = officialSources; self.currentId = currentId; self.total = total; self.position = position
        self.canNext = canNext; self.canPrevious = canPrevious
        self.nextPreviewID = nextPreviewID; self.previousPreviewID = previousPreviewID
        self.sourceCollectionID = sourceCollectionID; self.queueSourceTitle = queueSourceTitle
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
    public let duration: Double
    public let text: String
    public init(time: Double, duration: Double, text: String) { self.time = time; self.duration = duration; self.text = text }
}

public struct MusicLyricLine: Codable, Equatable, Sendable, Identifiable {
    public let index: Int
    public let time: Double
    public let end: Double
    public let text: String
    public let translation: String
    public let romanization: String
    public let words: [MusicLyricWord]
    public var id: Int { index }
    public init(index: Int, time: Double, end: Double, text: String, translation: String = "",
                romanization: String = "", words: [MusicLyricWord] = []) {
        self.index = index; self.time = time; self.end = end; self.text = text; self.translation = translation
        self.romanization = romanization; self.words = words
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

public enum MusicArtworkURL {
    public static func normalized(_ text: String) -> URL? {
        let trimmed = text.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty,
              let parts = URLComponents(string: trimmed.replacingOccurrences(of: "{size}", with: "480")),
              let scheme = parts.scheme?.lowercased(), scheme == "http" || scheme == "https",
              let host = parts.host, !host.isEmpty else { return nil }
        return parts.url
    }
}

public struct MusicAudioDelivery: Codable, Sendable, Equatable {
    public enum Evidence: String, Codable, Sendable { case officialResponse, decodedFile }
    public let quality: String
    public let evidence: Evidence
    public let bitrate: Double?
    public let sampleRate: Double?
    public let duration: Double?
    public init(quality: String, evidence: Evidence, bitrate: Double? = nil,
                sampleRate: Double? = nil, duration: Double? = nil) {
        self.quality = quality; self.evidence = evidence
        self.bitrate = bitrate; self.sampleRate = sampleRate; self.duration = duration
    }
}

public enum MusicOperationDeadline {
    public static func run<T: Sendable>(
        seconds: Double,
        operation: @escaping @Sendable () async throws -> T
    ) async throws -> T {
        guard seconds.isFinite, seconds > 0, seconds <= 35 else { throw MusicServiceError.invalidRequest }
        try Task.checkCancellation()
        let gate = SingleShotAsyncGate<T>()
        let task = Task {
            do {
                let value = try await operation()
                try Task.checkCancellation()
                gate.finish(.success(value))
            } catch {
                gate.finish(.failure(error))
            }
        }
        let timer = Task {
            do { try await Task.sleep(nanoseconds: UInt64(seconds * 1_000_000_000)) } catch { return }
            gate.finish(.failure(MusicServiceError.timeout)); task.cancel()
        }
        defer { timer.cancel() }
        return try await withTaskCancellationHandler {
            let result = try await gate.value()
            try Task.checkCancellation()
            return result
        } onCancel: {
            task.cancel(); gate.finish(.failure(MusicServiceError.cancelled))
        }
    }
}

public struct MusicRequestTrace: Sendable {
    public enum Phase: String, Sendable { case started, networkAttempt, completed, failed, cacheHit, cacheMiss, coalesced, cancelled }
    public enum Operation: String, Sendable {
        case publicAPI, accountAPI, playlist, audio, playback, search, browse, artwork, media, library, settings, queue
        public var title: String {
            switch self {
            case .audio, .playback: return "播放歌曲"
            case .search: return "搜索歌曲"
            case .playlist: return "歌单读取"
            case .browse, .publicAPI: return "音乐内容读取"
            case .accountAPI: return "平台账号请求"
            case .artwork: return "封面读取"
            case .media: return "音频读取"
            case .library: return "曲库操作"
            case .settings: return "保存设置"
            case .queue: return "播放队列操作"
            }
        }
    }
    @TaskLocal public static var current: MusicRequestTrace?
    public let id: String
    public let logicalID: String
    public let operation: Operation
    public let platform: MusicPublicBrowsePlatform?
    private let started = Date()

    public init(_ operation: Operation, platform: MusicPublicBrowsePlatform? = nil) {
        self.operation = operation; id = String(UUID().uuidString.prefix(8))
        self.platform = platform ?? Self.current?.platform
        logicalID = Self.current?.logicalID ?? id
    }

    public func record(_ phase: Phase, http: Int? = nil, code: Int? = nil, failure: MusicServiceError? = nil) {
        let elapsed = Int(min(86_400, max(0, Date().timeIntervalSince(started))) * 1000)
        Self.logger.info("id=\(id, privacy: .public) logical=\(logicalID, privacy: .public) op=\(operation.rawValue, privacy: .public) platform=\(platform?.rawValue ?? "-", privacy: .public) phase=\(phase.rawValue, privacy: .public) http=\(http ?? 0) code=\(code ?? 0) error=\(failure?.rawValue ?? "-", privacy: .public) ms=\(elapsed)")
    }

    private static let logger = Logger(subsystem: "dev.meowfilm.aggregate", category: "requests")

    public func failed(_ error: Error) {
        let failure = MusicServiceError.sanitized(error)
        record(failure == .cancelled ? .cancelled : .failed, failure: failure)
    }
}

public struct MusicRecordingIdentity: Sendable {
    private static let whitespace = CharacterSet(charactersIn:
        "\u{0009}\u{000A}\u{000B}\u{000C}\u{000D}\u{0020}\u{0085}\u{00A0}\u{1680}" +
        "\u{2000}\u{2001}\u{2002}\u{2003}\u{2004}\u{2005}\u{2006}\u{2007}\u{2008}\u{2009}\u{200A}" +
        "\u{2028}\u{2029}\u{202F}\u{205F}\u{3000}")
    public let lookupKey: String?
    public let duration: Double?

    public init(title: String, artist: String, duration: Double, artistNames: [String]? = nil) {
        let name = Self.normalize(title)
        let names = (artistNames ?? [artist]).map(Self.normalize)
        if Self.known(name), !names.isEmpty, names.allSatisfy(Self.known) {
            let sorted = Set(names).sorted { $0.utf16.lexicographicallyPrecedes($1.utf16) }
            let key: [String: Any] = ["title": name, "artists": sorted]
            let data = try! JSONSerialization.data(withJSONObject: key, options: [.sortedKeys, .withoutEscapingSlashes])
            lookupKey = String(decoding: data, as: UTF8.self)
        } else { lookupKey = nil }
        self.duration = duration.isFinite && duration > 0 ? duration : nil
    }
    public init(_ track: MusicRemoteTrack) {
        self.init(title: track.title, artist: track.artist, duration: track.duration, artistNames: track.artistNames)
    }
    public func matches(_ incoming: Self) -> Bool {
        guard let key = lookupKey, key == incoming.lookupKey,
              let baseline = duration, let proposed = incoming.duration else { return false }
        let baselineMs = Int64((baseline * 1000).rounded())
        let proposedMs = Int64((proposed * 1000).rounded())
        return abs(baselineMs - proposedMs) <= 5000
    }
    public static func normalize(_ value: String) -> String {
        let compatible = value.precomposedStringWithCompatibilityMapping
        var lowered = String.UnicodeScalarView()
        for scalar in compatible.unicodeScalars {
            lowered.append((65...90).contains(scalar.value) ? UnicodeScalar(scalar.value + 32)! : scalar)
        }
        return String(lowered).components(separatedBy: Self.whitespace)
            .filter { !$0.isEmpty }.joined(separator: " ")
    }
    private static let unknownNames: Set<String> = [
        "未知歌手", "未知歌曲", "unknown", "unknown artist", "unknown title"
    ]
    private static func known(_ text: String) -> Bool {
        !text.isEmpty && !unknownNames.contains(text)
    }
}

public struct MusicChorusPoint: Codable, Sendable, Equatable {
    public let start: Double
    public let end: Double
    public init(start: Double, end: Double) { self.start = start; self.end = end }
    public func fits(recordingDuration: Double, actualDuration: Double) -> Bool {
        start.isFinite && end.isFinite && start >= 0 && end > start &&
        actualDuration.isFinite && actualDuration > 0 && end <= actualDuration
    }
}

public struct MusicResolvedSource: Sendable, Equatable, Identifiable {
    public let id: String
    public let selection: MusicPlaybackSelection
    public let title: String
    public let delivery: MusicAudioDelivery?
    public init(id: String, selection: MusicPlaybackSelection, title: String, delivery: MusicAudioDelivery?) {
        self.id = id; self.selection = selection; self.title = title; self.delivery = delivery
    }
}

public struct MusicResolvedSourcesSnapshot: Sendable {
    public let sources: [MusicResolvedSource]
    public let hasMore: Bool
    public let isLoading: Bool
    public init(sources: [MusicResolvedSource], hasMore: Bool, isLoading: Bool = false) {
        self.sources = sources; self.hasMore = hasMore; self.isLoading = isLoading
    }
}

public enum MusicSourceIntent: Sendable {
    case automatic
    case quality(String)
    case exact(MusicPlaybackSelection)
}

public struct MusicPreparedSource: Sendable {
    public let candidate: MusicPlaybackCandidate
    public let selection: MusicPlaybackSelection
    public init(candidate: MusicPlaybackCandidate, selection: MusicPlaybackSelection) {
        self.candidate = candidate; self.selection = selection
    }
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

public struct MusicPlaybackRecovery: Sendable {
    public let candidate: MusicPlaybackCandidate
    public let selection: MusicPlaybackSelection
    public init(candidate: MusicPlaybackCandidate, selection: MusicPlaybackSelection) {
        self.candidate = candidate; self.selection = selection
    }
}

public enum MusicSourceQualityPolicy {
    public static let qualities = ["128k", "192k", "320k", "flac", "flac24bit", "master", "jyeffect", "dolby", "sky", "vivid"]
    public static let inspectionQualities = ["320k", "192k", "flac", "flac24bit", "master", "128k",
                                             "dolby", "sky", "vivid", "jyeffect"]
    public static let groups = ["master", "spatial", "enhanced", "hires", "lossless", "high", "standard"]
    public static let spatialTypes = ["dolby", "dts_x", "sony360", "sky", "vivid", "qq_atmos", "viper_atmos"]

    public static func group(_ quality: String) -> String {
        switch quality {
        case "master", "jymaster", "qq_master", "viper_tape":
            return "master"
        case "spatial", "dolby", "dts_x", "sony360", "sky", "vivid", "qq_atmos", "viper_atmos",
             "atmos", "atmos_plus", "atmos_2":
            return "spatial"
        case "enhanced", "jyeffect", "qq_enhanced", "viper_clear":
            return "enhanced"
        case "hires", "flac24bit":
            return "hires"
        case "lossless", "flac":
            return "lossless"
        case "high", "higher", "exhigh", "320k", "192k":
            return "high"
        case "standard", "128k":
            return "standard"
        default:
            return quality
        }
    }

    public static func levels(for category: String, state: MusicPlaybackState) -> [String] {
        switch category {
        case "master": return ["master"]
        case "hires": return ["flac24bit"]
        case "lossless": return ["flac"]
        case "high": return ["320k", "192k"]
        case "standard": return ["128k"]
        case "enhanced": return ["jyeffect"]
        case "spatial":
            let spatialOrder = state.spatialOrder + state.spatialEnabled.filter { !state.spatialOrder.contains($0) }
            return spatialOrder.filter { qualities.contains($0) && state.spatialEnabled.contains($0) }
        default:
            return []
        }
    }

    public static func ordered(_ state: MusicPlaybackState, automatic: Bool) -> [String] {
        let order = state.qualityOrder + groups.filter { !state.qualityOrder.contains($0) }
        let request = state.requestQuality
        let requestGroup = group(request)
        let start = (!automatic || request == "auto") ? 0 : (order.firstIndex(of: requestGroup) ?? 0)
        var result: [String] = []
        for tier in order.dropFirst(start) where state.qualityEnabled.contains(tier) {
            let tierLevels = levels(for: tier, state: state)
            if automatic, request != "auto", tier == requestGroup, tierLevels.contains(request) {
                result += tierLevels.drop(while: { $0 != request })
            } else {
                result += tierLevels
            }
        }
        return result
    }
}

public struct MusicMetadataPreferences: Equatable, Sendable {
    public let revision: Int
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
    public let platforms: [MusicPublicBrowsePlatform]?
    public let enabledPlatforms: Set<MusicPublicBrowsePlatform>?

    public init(ifRevision: Int, platforms: [MusicPublicBrowsePlatform]? = nil,
                enabledPlatforms: Set<MusicPublicBrowsePlatform>? = nil) {
        self.ifRevision = ifRevision; self.platforms = platforms
        self.enabledPlatforms = enabledPlatforms
    }
}

public struct MusicMetadataCandidate: Equatable, Sendable, Identifiable {
    public let source: MusicNativeSource
    public let artwork: URL?
    public var id: String { source.sourceKey }

    public init(source: MusicNativeSource, artwork: URL? = nil) {
        self.source = source; self.artwork = artwork
    }
}

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
