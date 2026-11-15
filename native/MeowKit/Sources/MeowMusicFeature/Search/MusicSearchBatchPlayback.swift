// SPDX-License-Identifier: GPL-3.0-only
import Foundation

/// The selected permanent recording. Search ranges and display snapshots are
/// not authority and do not need renewal before a queue action.
public struct MusicSearchBatchTarget: Sendable {
    public let track: MusicRemoteTrack
    public init(track: MusicRemoteTrack) { self.track = track }
}

public struct MusicSearchBatchRequest: Sendable {
    public let query: String
    public let targets: [MusicSearchBatchTarget]
    public let startIndex: Int
    public init(query: String, targets: [MusicSearchBatchTarget], startIndex: Int = 0) {
        self.query = query; self.targets = targets; self.startIndex = startIndex
    }
}

public protocol MusicSearchBatchPlaying: Sendable {
    func searchRecording(_ request: MusicSearchBatchRequest) async throws -> MusicRemoteTrack
    func replaceSearchBatch(_ request: MusicSearchBatchRequest, expectedRevision: Int,
                            intent: MusicQueueAuthorization) async throws -> MusicPlaybackState
}
