#if os(iOS) || os(macOS)
import Foundation
import MeowContracts

public enum PlaybackVideoError: Error, Equatable, LocalizedError, Sendable {
    case invalidInput, cancelled, staleAttempt, inputFailed, unsupportedFormat
    case decodingFailed, outputFailed, seekFailed, timedOut, decryptionFailed
    case resourceReleaseFailed, pictureInPictureUnavailable, pictureInPictureFailed

    public var errorDescription: String? {
        switch self {
        case .invalidInput: return "视频输入无效"
        case .cancelled: return "视频操作已取消"
        case .staleAttempt: return "视频播放操作已失效"
        case .inputFailed: return "视频读取失败"
        case .unsupportedFormat: return "视频格式不受支持"
        case .decodingFailed: return "视频解码失败"
        case .outputFailed: return "视频输出失败"
        case .seekFailed: return "视频定位失败"
        case .timedOut: return "视频操作超时"
        case .decryptionFailed: return "视频解密失败"
        case .resourceReleaseFailed: return "视频资源释放失败"
        case .pictureInPictureUnavailable: return "画中画当前不可用"
        case .pictureInPictureFailed: return "画中画启动失败"
        }
    }
}

public enum PlaybackVideoPictureInPictureState: Equatable, Sendable {
    case unavailable, possible, starting, active, stopping, failed
}

/// Contains only identities and sanitized observations, never the media URL,
/// ClearKey material, HTTP headers or an FFmpeg/OS error description.
public struct PlaybackVideoEvent {
    public enum Kind: Equatable, Sendable {
        case preparing, prepared, started, progress, paused, ended, failed, stopped
        case pictureInPictureChanged, systemOwnerEnded
    }
    public let attemptID: String
    public let nativeAttempt: PlayAttempt?
    public let kind: Kind
    public let snapshot: PlaybackSnapshot
    public let wantsPlayback: Bool
    public let error: PlaybackVideoError?
    public let pictureInPicture: PlaybackVideoPictureInPictureState
}
#endif
