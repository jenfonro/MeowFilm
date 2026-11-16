import Foundation

public enum AudioSeekOrigin: Sendable { case start, current, end }
public enum AudioByteSeek: Sendable {
    case offset(Int64, AudioSeekOrigin)
    case size
}

/// Reads run on the decoder worker, never the main actor or render thread.
/// Cancelling one read does not retire its immutable input representation.
public protocol AudioByteInput: AnyObject, Sendable {
    var formatHint: String? { get }
    var loopbackRoot: String? { get }
    var primaryURL: String? { get }
    var isReadCancelled: Bool { get }
    func read(maximumBytes: Int) throws -> Data
    func seek(_ request: AudioByteSeek) throws -> Int64
    func cancelReads()
    func resumeReads()
    func close() async throws
}

public extension AudioByteInput {
    var primaryURL: String? { nil }
}

public enum AudioCoreError: Error, Equatable, LocalizedError, Sendable {
    case cancelled, invalidMedia, inputFailed, unsupportedFormat
    case allocationFailed, outputFailed, seekFailed

    public var errorDescription: String? {
        switch self {
        case .cancelled: return "音频操作已取消"
        case .invalidMedia: return "音频数据无效"
        case .inputFailed: return "音频读取失败"
        case .unsupportedFormat: return "不支持此音频格式"
        case .allocationFailed: return "音频资源不足"
        case .outputFailed: return "音频输出失败"
        case .seekFailed: return "播放位置调整失败"
        }
    }
}

public struct AudioStreamInformation: Sendable {
    public let duration: Double?
    public let sampleRate: Int
    public let channels: Int
    public let seekable: Bool
    public let sourceSampleRate: Int
    public let sourceChannels: Int
    public let decodedSourceFrames: Int64
    public let isComplete: Bool
}

public struct AudioPCMChunk: Sendable {
    public let samples: [Float]
    public let position: Double
    public var frameCount: Int { samples.count / 2 }
    public var endPosition: Double { position + Double(frameCount) / 48_000 }
}
