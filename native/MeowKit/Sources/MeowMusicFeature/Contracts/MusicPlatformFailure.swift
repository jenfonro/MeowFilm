import Foundation
import MeowContracts
import os

/// These are platform protocol semantics, not a generic HTTP auth policy.
/// In particular, a plain 403 has no evidence that logging in will fix it.
public enum MusicPlatformFailure {
    public static func http(_ status: Int) -> MusicServiceError? {
        switch status {
        case 200...299: return nil
        case 401: return .authRequired
        case 403: return .requestRejected
        case 404: return .notFound
        case 429: return .rateLimited
        default: return .upstreamFailed
        }
    }
    public static func netease(_ code: Int) -> MusicServiceError? {
        switch code {
        case 200: return nil
        case 301, 401: return .authRequired
        case 403: return .requestRejected
        case 404: return .notFound
        case 429, 509: return .rateLimited
        case 250, 460, 461: return .verificationRequired
        default: return .upstreamFailed
        }
    }
}

/// A fixed schema keeps arbitrary URLs, response bodies and credentials out of
/// diagnostics. IDs are request-local, never an account or media identifier.
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
    public struct Event: Sendable {
        public let id: String, logicalID: String
        public let operation: Operation, phase: Phase
        public let platform: MusicPublicBrowsePlatform?
        public let http: Int?, code: Int?, failure: MusicServiceError?
        public let milliseconds: Int
    }
    @TaskLocal public static var current: MusicRequestTrace?
    /// Explicit, task-scoped test/diagnostic injection; no global event bus,
    /// file sink, raw response or user identity.
    @TaskLocal public static var observer: (@Sendable (Event) -> Void)?
    public let id: String
    public let logicalID: String
    public let operation: Operation
    public let platform: MusicPublicBrowsePlatform?
    private let started = Date()
    private let sink: (@Sendable (Event) -> Void)?
    public init(_ operation: Operation, platform: MusicPublicBrowsePlatform? = nil) {
        self.operation = operation; id = String(UUID().uuidString.prefix(8))
        self.platform = platform ?? Self.current?.platform
        logicalID = Self.current?.logicalID ?? id
        sink = Self.observer
    }
    public func record(_ phase: Phase, http: Int? = nil, code: Int? = nil, failure: MusicServiceError? = nil) {
        let elapsed = Int(min(86_400, max(0, Date().timeIntervalSince(started))) * 1000)
        sink?(.init(id: id, logicalID: logicalID, operation: operation, phase: phase,
                    platform: platform,
                    http: http, code: code, failure: failure, milliseconds: elapsed))
        Self.logger.info("id=\(id, privacy: .public) logical=\(logicalID, privacy: .public) op=\(operation.rawValue, privacy: .public) platform=\(platform?.rawValue ?? "-", privacy: .public) phase=\(phase.rawValue, privacy: .public) http=\(http ?? 0) code=\(code ?? 0) error=\(failure?.rawValue ?? "-", privacy: .public) ms=\(elapsed)")
    }
    private static let logger = Logger(subsystem: "dev.meowfilm.aggregate", category: "requests")
    public func failed(_ error: Error) {
        let failure = MusicServiceError.sanitized(error)
        record(failure == .cancelled ? .cancelled : .failed, failure: failure)
    }
}
