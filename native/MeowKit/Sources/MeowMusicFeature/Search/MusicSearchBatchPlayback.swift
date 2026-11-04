// SPDX-License-Identifier: GPL-3.0-only
import Foundation

/// One native variant and its exact search range. `track` is identity
/// evidence only. A live ref may reuse its owner-held, identity-checked song;
/// expired refs require exact-page renewal, never trusting DTOs as source data.
public struct MusicSearchBatchOrigin: Sendable {
    public let platform: MusicPublicBrowsePlatform
    public let page: Int
    public let limit: Int
    public let track: MusicRemoteTrack
    public init(platform: MusicPublicBrowsePlatform, page: Int, limit: Int = 30, track: MusicRemoteTrack) {
        self.platform = platform; self.page = page; self.limit = limit; self.track = track
    }
    public func matches(_ refreshed: MusicRemoteTrack) -> Bool {
        MusicSearchFusion.sameSearchTarget(refreshed, track, platform: platform)
    }
}

/// Origins are ordered by display preference. Every included platform remains
/// backed by its real native owner; fusion never manufactures a joined ref.
public struct MusicSearchBatchTarget: Sendable {
    public let origins: [MusicSearchBatchOrigin]
    public var track: MusicRemoteTrack { origins[0].track }
    public var platform: MusicPublicBrowsePlatform { origins[0].platform }
    public var page: Int { origins[0].page }
    public init(origins: [MusicSearchBatchOrigin]) throws {
        guard !origins.isEmpty else { throw MusicServiceError.invalidRequest }
        self.origins = origins
    }
    public init(platform: MusicPublicBrowsePlatform, page: Int, limit: Int = 30, track: MusicRemoteTrack) {
        origins = [.init(platform: platform, page: page, limit: limit, track: track)]
    }
}

/// An in-memory request capturing visible selection and order, not a new search.
public struct MusicSearchBatchRequest: Sendable {
    public let query: String
    public let targets: [MusicSearchBatchTarget]
    public let startIndex: Int
    public init(query: String, targets: [MusicSearchBatchTarget], startIndex: Int = 0) {
        self.query = query; self.targets = targets; self.startIndex = startIndex
    }
    public func validate() throws {
        guard !targets.isEmpty, targets.indices.contains(startIndex),
              !query.isEmpty, query == query.trimmingCharacters(in: .whitespacesAndNewlines),
              query.utf8.count <= 800 else { throw MusicServiceError.invalidRequest }
        var bytes = query.utf8.count
        for target in targets {
            guard (1...16).contains(target.origins.count),
                  Set(target.origins.map {
                      Data(MusicSearchFusion.sourceIdentity($0.track, platform: $0.platform).utf8)
                  }).count == target.origins.count else {
                throw MusicServiceError.invalidRequest
            }
            for origin in target.origins {
                _ = try MusicSearchPagination.offset(platform: origin.platform, page: origin.page, limit: origin.limit)
                guard !origin.track.songId.isEmpty, !origin.track.title.isEmpty,
                      origin.track.duration.isFinite, origin.track.duration >= 0,
                      (1...16).contains(origin.track.sources.count),
                      origin.track.sources.contains(where: {
                          $0.platform == origin.platform.rawValue && !$0.nativeId.isEmpty && !$0.sourceKey.isEmpty
                      }), target.origins.allSatisfy({ MusicSearchFusion.sameRecording($0.track, origin.track) }) else {
                    throw MusicServiceError.invalidRequest
                }
                let size = try JSONEncoder().encode(origin.track).count
                guard size <= 64 * 1024 else { throw MusicServiceError.limit }
                bytes += size
                guard bytes <= 4 * 1024 * 1024 else { throw MusicServiceError.limit }
            }
        }
    }
}

/// Optional native capability. Reuse live owner records or renew expired pages, validate every
/// recording/source, then perform ONE existing queue/history/playback CAS.
/// A single-recording read may register one temporary ref for existing library
/// and append operations; batch playback does not register its entire selection.
/// Caller owns cancellation/selection admission through subsequent playback.
public protocol MusicSearchBatchPlaying: Sendable {
    func searchRecording(_ request: MusicSearchBatchRequest) async throws -> MusicRemoteTrack
    func replaceSearchBatch(_ request: MusicSearchBatchRequest, expectedRevision: Int,
                            intent: MusicQueueAuthorization) async throws -> MusicPlaybackState
}
