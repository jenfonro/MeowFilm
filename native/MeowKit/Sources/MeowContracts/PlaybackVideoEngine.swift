import Foundation

public enum PlaybackVideoEngine: String, Codable, CaseIterable, Sendable {
    case ksPlayer = "ksplayer"
    case aether = "aether"

    public var title: String {
        switch self {
        case .ksPlayer: "KSPlayer"
        case .aether: "AetherEngine"
        }
    }

    public var maximumRate: Double {
        switch self {
        case .ksPlayer: 8
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
