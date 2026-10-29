// SPDX-License-Identifier: GPL-3.0-only
import Foundation

public struct MusicChorusPoint: Codable, Sendable, Equatable {
    public let start: Double
    public let end: Double
    public init(start: Double, end: Double) { self.start = start; self.end = end }
    public func fits(recordingDuration: Double, actualDuration: Double) -> Bool {
        start.isFinite && end.isFinite && start >= 0 && end > start &&
        recordingDuration.isFinite && actualDuration.isFinite &&
        recordingDuration > 0 && actualDuration > 0 &&
        abs(recordingDuration - actualDuration) <= max(3, min(recordingDuration, actualDuration) * 0.02) &&
        end <= recordingDuration && end <= actualDuration
    }
}
public protocol MusicChorusProviding: Sendable {
    func chorus(attemptID: String, actualDuration: Double) async throws -> MusicChorusPoint?
}
public protocol MusicNativeChorusProviding: Sendable {
    func nativeChorus(nativeID: String, selection: MusicPlaybackSelection,
                      recordingDuration: Double, actualDuration: Double) async throws -> MusicChorusPoint?
}
