// SPDX-License-Identifier: GPL-3.0-only
import Foundation

/// One selected recording and its exact search origin. `track` is identity
/// evidence only. A live ref may reuse its owner-held, identity-checked song;
/// expired refs require exact-page renewal, never trusting DTOs as source data.
public struct MusicSearchBatchTarget: Sendable {
    public let platform: MusicPublicBrowsePlatform
    public let page: Int
    public let track: MusicRemoteTrack
    public init(platform: MusicPublicBrowsePlatform, page: Int, track: MusicRemoteTrack) {
        self.platform = platform; self.page = page; self.track = track
    }
    public func matches(_ refreshed: MusicRemoteTrack) -> Bool {
        MusicSearchFusion.sameSearchTarget(refreshed, track, platform: platform)
    }
}

/// An in-memory request capturing visible selection and order, not a new search.
public struct MusicSearchBatchRequest: Sendable {
    public static let maximumCount = 2000
    public let query: String
    public let targets: [MusicSearchBatchTarget]
    public let startIndex: Int
    public init(query: String, targets: [MusicSearchBatchTarget], startIndex: Int = 0) {
        self.query = query; self.targets = targets; self.startIndex = startIndex
    }
    public func validate() throws {
        guard targets.count <= Self.maximumCount else { throw MusicServiceError.limit }
        guard !targets.isEmpty, targets.indices.contains(startIndex),
              !query.isEmpty, query == query.trimmingCharacters(in: .whitespacesAndNewlines),
              query.utf8.count <= 800 else { throw MusicServiceError.invalidRequest }
        var bytes = query.utf8.count
        for target in targets {
            guard [MusicPublicBrowsePlatform.netease, .qq, .kugou].contains(target.platform), (1...100).contains(target.page),
                  !target.track.songId.isEmpty, !target.track.title.isEmpty,
                  target.track.duration.isFinite, target.track.duration >= 0,
                  (1...10).contains(target.track.sources.count),
                  target.track.sources.contains(where: {
                      $0.platform == target.platform.rawValue && !$0.nativeId.isEmpty && !$0.sourceKey.isEmpty
                  }) else { throw MusicServiceError.invalidRequest }
            let size = try JSONEncoder().encode(target.track).count
            guard size <= 64 * 1024 else { throw MusicServiceError.limit }
            bytes += size
            guard bytes <= 4 * 1024 * 1024 else { throw MusicServiceError.limit }
        }
    }
}

/// Optional native capability. Reuse live owner records or renew expired pages, validate every
/// recording/source, then perform ONE existing queue/history/playback CAS.
/// No handle registration, durable search catalogue, partial queue or fallback.
/// Caller owns cancellation/selection admission through subsequent playback.
public protocol MusicSearchBatchPlaying: Sendable {
    func replaceSearchBatch(_ request: MusicSearchBatchRequest, expectedRevision: Int,
                            intent: MusicQueueAuthorization) async throws -> MusicPlaybackState
}
