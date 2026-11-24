import Foundation
import CoreGraphics
import CMeowVideoFFmpeg

public enum VideoKernelFailure: Error, Equatable, Sendable {
    case cancelled
    case openFailed
    case formatFailed
    case decodeFailed
    case seekFailed
    case outputFailed
    case timedOut
    case transportFailed
    case decryptionFailed

    static func fromFFmpeg(_ code: Int32, encrypted: Bool = false) -> VideoKernelFailure {
        if code == mvk_averror_exit() { return .cancelled }
        if encrypted && (code == -1 || code == mvk_averror_invaliddata()) {
            return .decryptionFailed
        }
        if mvk_is_network_error(code) { return .transportFailed }
        if code == mvk_averror_invaliddata() { return .formatFailed }
        return .openFailed
    }
}

public struct VideoChapter: Sendable, Equatable {
    public let title: String
    public let start: Double
    public let end: Double

    public init(title: String, start: Double, end: Double) {
        self.title = title
        self.start = start
        self.end = end
    }
}

public struct VideoTrack: Sendable, Equatable {
    public enum Kind: String, Equatable, Sendable {
        case video, audio, subtitle
    }

    public let id: Int32
    public let kind: Kind
    public let name: String
    public let codec: String
    public let language: String?
    public let isEnabled: Bool

    public init(id: Int32, kind: Kind, name: String, codec: String, language: String?, isEnabled: Bool) {
        self.id = id
        self.kind = kind
        self.name = name
        self.codec = codec
        self.language = language
        self.isEnabled = isEnabled
    }
}

public struct VideoStreamInfo: Sendable, Equatable {
    public let duration: Double?
    public let codec: String
    public let bitDepth: Int
    /// Display width/height accounting for stream rotation metadata.
    public let width: Int
    public let height: Int
    public let rotationDegrees: Int
    public let hflip: Bool
    public let vflip: Bool
    public let hasAudio: Bool
    public let sampleRate: Double?
    public let audioChannels: Int?
    public let isHDR: Bool
    public let dolbyVisionProfile: UInt8?
    public let chapters: [VideoChapter]
}

public struct VideoSubtitleCue: Sendable {
    public let start: Double
    public let end: Double
    public let text: String?
    public let image: CGImage?
    public let rect: CGRect // normalized 0..1 when bitmap

    public init(start: Double, end: Double, text: String?, image: CGImage? = nil, rect: CGRect = .zero) {
        self.start = start
        self.end = end
        self.text = text
        self.image = image
        self.rect = rect
    }
}

public struct VideoKernelDiagnostics: Sendable, Equatable {
    public let droppedFrames: UInt64
    public let avSyncDiffSeconds: Double
    public let videoQueuePackets: Int
    public let audioQueuePackets: Int
    public let subtitleQueuePackets: Int
    public let videoFrameQueue: Int
    public let hardwareDecoding: Bool
    public let hwDecodeFallback: Bool
    public let isHDR: Bool
    public let dolbyVision: Bool
    public let bufferedSeconds: Double
    public let audioUnderruns: UInt64
    public let submittedPictures: UInt64
    public let lastErrorCode: String?
}
