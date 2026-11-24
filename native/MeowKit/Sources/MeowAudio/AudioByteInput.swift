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

/// Planar 48 kHz stereo float32 PCM chunk directly ready for AVAudioPCMBuffer copies.
public struct AudioPCMChunk: Sendable {
    public let left: [Float]
    public let right: [Float]
    public let position: Double
    public var frameCount: Int { left.count }
    public var endPosition: Double { position + Double(frameCount) / 48_000 }

    public init(left: [Float], right: [Float], position: Double) {
        precondition(left.count == right.count)
        self.left = left
        self.right = right
        self.position = position
    }

    public func droppingFirstFrames(_ skip: Int) -> AudioPCMChunk? {
        guard skip >= 0, skip < frameCount else { return nil }
        if skip == 0 { return self }
        return AudioPCMChunk(
            left: Array(left.dropFirst(skip)),
            right: Array(right.dropFirst(skip)),
            position: position + Double(skip) / 48_000
        )
    }
}
