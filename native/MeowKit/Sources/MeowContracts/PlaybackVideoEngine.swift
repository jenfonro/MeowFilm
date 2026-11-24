import Foundation

public enum PlaybackVideoEngine: String, Codable, CaseIterable, Sendable {
    case native = "native"
    case aether = "aether"

    public var title: String {
        switch self {
        case .native: "原生内核"
        case .aether: "AetherEngine"
        }
    }

    public var maximumRate: Double {
        switch self {
        case .native: 8
        case .aether: 2
        }
    }
}

/// Declared container chapters on the decoder's zero-based playback timeline.
public struct PlaybackVideoChapter: Equatable, Sendable {
    public let title: String
    public let start: Double
    public let end: Double

    public init(title: String, start: Double, end: Double) {
        self.title = title
        self.start = start
        self.end = end
    }
}

public struct PlaybackVideoTrack: Equatable, Sendable {
    public enum Kind: String, Equatable, Sendable {
        case video, audio, subtitle
    }

    public let id: Int
    public let kind: Kind
    public let name: String
    public let codec: String
    public let language: String?
    public let isEnabled: Bool

    public init(id: Int, kind: Kind, name: String, codec: String, language: String?, isEnabled: Bool) {
        self.id = id
        self.kind = kind
        self.name = name
        self.codec = codec
        self.language = language
        self.isEnabled = isEnabled
    }
}

public struct PlaybackVideoDiagnostics: Equatable, Sendable {
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
    public let dolbyVisionProfile: Int?
    public let videoFormat: String?
    public let bufferedSeconds: Double
    public let audioUnderruns: UInt64
    public let submittedPictures: UInt64
    public let lastErrorCode: String?

    public init(
        droppedFrames: UInt64 = 0,
        avSyncDiffSeconds: Double = 0,
        videoQueuePackets: Int = 0,
        audioQueuePackets: Int = 0,
        subtitleQueuePackets: Int = 0,
        videoFrameQueue: Int = 0,
        hardwareDecoding: Bool = false,
        hwDecodeFallback: Bool = false,
        isHDR: Bool = false,
        dolbyVision: Bool = false,
        dolbyVisionProfile: Int? = nil,
        videoFormat: String? = nil,
        bufferedSeconds: Double = 0,
        audioUnderruns: UInt64 = 0,
        submittedPictures: UInt64 = 0,
        lastErrorCode: String? = nil
    ) {
        self.droppedFrames = droppedFrames
        self.avSyncDiffSeconds = avSyncDiffSeconds
        self.videoQueuePackets = videoQueuePackets
        self.audioQueuePackets = audioQueuePackets
        self.subtitleQueuePackets = subtitleQueuePackets
        self.videoFrameQueue = videoFrameQueue
        self.hardwareDecoding = hardwareDecoding
        self.hwDecodeFallback = hwDecodeFallback
        self.isHDR = isHDR
        self.dolbyVision = dolbyVision
        self.dolbyVisionProfile = dolbyVisionProfile
        self.videoFormat = videoFormat
        self.bufferedSeconds = bufferedSeconds
        self.audioUnderruns = audioUnderruns
        self.submittedPictures = submittedPictures
        self.lastErrorCode = lastErrorCode
    }
}
