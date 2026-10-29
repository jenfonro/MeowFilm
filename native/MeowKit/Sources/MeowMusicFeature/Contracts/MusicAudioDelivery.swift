import Foundation

/// Evidence returned with a resolved resource. Never inferred from requested
/// quality, catalogue capability, file extension or successful URL parsing.
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
