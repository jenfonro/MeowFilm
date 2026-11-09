// SPDX-License-Identifier: GPL-3.0-only
import Foundation
import MeowMusicFeature

private struct MusicSearchOriginID: Hashable, Sendable {
    let target: Int
    let origin: Int
}
private struct MusicSearchOriginRange: Hashable, Sendable {
    let page: Int
    let limit: Int
}

extension NativeMusicService: MusicSearchBatchPlaying {
    public func searchRecording(_ request: MusicSearchBatchRequest) async throws -> MusicRemoteTrack {
        try request.validate()
        try musicRequire(request.targets.count == 1, .invalidRequest)
        let retained = try retainedSearchOrigins(request), port = publicPlatforms, owner = self, own = epoch
        let songs = try await work(label: "searchRecording") {
            try await Self.searchBatchSongs(request, retained: retained, port: port, intent: nil, owner: owner, epoch: own)
        }
        let shared = try await projectSongMetadata(songs)
        return try remember(shared[0])
    }

    public func replaceSearchBatch(_ request: MusicSearchBatchRequest, expectedRevision: Int,
                                   intent: MusicQueueAuthorization) async throws -> MusicPlaybackState {
        try request.validate()
        try check(); try intent.check()
        let retained = try retainedSearchOrigins(request), own = epoch, port = publicPlatforms, owner = self
        try musicRequire(try read().queue["revision"].int == expectedRevision, .staleRevision)
        let acquired = try await work(label: "searchBatch") {
            try await Self.searchBatchSongs(request, retained: retained, port: port, intent: intent, owner: owner, epoch: own)
        }
        let songs = try await projectSongMetadata(acquired)
        try check(own); try intent.check()
        let result = try intent.perform {
            try similarQueueTransaction(["queue", "playback", "history"]) { document in
                try musicRequire(document.queue["revision"].int == expectedRevision, .staleRevision)
                let old = document.queue
                let rows = try makeEntries(songs, queue: &document.queue,
                    context: .obj([("sourceTitle", .string("搜索")),
                        ("sourceType", .string(songs.count == 1 ? "search-song" : "search")),
                        ("searchKeyword", .string(request.query))]))
                archive(old, into: &document)
                document.queue["entries"] = .array(rows)
                document.queue["currentId"] = rows[request.startIndex]["id"]
                for key in ["history", "priority", "failed"] { document.queue[key] = .array([]) }
                document.queue["source"] = .null
                similarWillChangeQueue(.replace, queue: &document.queue)
                document.queue["revision"] = .n(expectedRevision + 1)
                document.resetPosition()
                return stateDTO(document)
            }
        }
        invalidatePlayback()
        similarDidChangeQueue(.replace)
        return result
    }

    private func retainedSearchOrigins(_ request: MusicSearchBatchRequest) throws -> [MusicSearchOriginID: MJ] {
        try check()
        var retained: [MusicSearchOriginID: MJ] = [:]
        for (targetIndex, target) in request.targets.enumerated() {
            for (originIndex, origin) in target.origins.enumerated() {
                try requirePlatform(origin.platform.rawValue)
                guard origin.track.ref.kind == .handle, let handle = handles[origin.track.ref.id],
                      now().timeIntervalSince(handle.at) < 600 else { continue }
                try musicRequire(origin.matches(try MusicModel.track(handle.song, origin.track.ref)), .targetChanged)
                retained[.init(target: targetIndex, origin: originIndex)] = handle.song
            }
        }
        return retained
    }

    /// Playback, favorites and append share exact-range renewal and real source
    /// merging. No query-wide hidden catalogue or DTO-to-song conversion.
    private nonisolated static func searchBatchSongs(_ request: MusicSearchBatchRequest,
        retained: [MusicSearchOriginID: MJ], port: MusicNativePlatforms,
        intent: MusicQueueAuthorization?, owner: NativeMusicService, epoch: UUID) async throws -> [MJ] {
        let origins: [(MusicSearchOriginID, MusicSearchBatchOrigin)] = request.targets.enumerated().flatMap { targetIndex, target in
            target.origins.enumerated().map { originIndex, origin in
                (MusicSearchOriginID(target: targetIndex, origin: originIndex), origin)
            }
        }
        let values = try await withThrowingTaskGroup(of: [(MusicSearchOriginID, MJ)].self,
            returning: [MusicSearchOriginID: MJ].self) { group in
            for platform in MusicPublicBrowsePlatform.allCases {
                let selected = origins.filter { retained[$0.0] == nil && $0.1.platform == platform }
                guard !selected.isEmpty else { continue }
                group.addTask {
                    try await Self.renewSearchOrigins(selected, platform: platform,
                        query: request.query, port: port, intent: intent, owner: owner, epoch: epoch)
                }
            }
            var collected = retained, bytes = retained.values.reduce(0) { $0 + $1.bytes.count }
            try musicRequire(bytes <= 4 * 1024 * 1024, .limit)
            for try await platform in group {
                for (id, song) in platform {
                    bytes += song.bytes.count
                    try musicRequire(bytes <= 4 * 1024 * 1024 && collected[id] == nil, .limit)
                    collected[id] = song
                }
            }
            return collected
        }
        try intent?.check(); try Task.checkCancellation()
        var songs: [MJ] = [], mergedBytes = 0
        for (index, target) in request.targets.enumerated() {
            guard var song = values[.init(target: index, origin: 0)] else { throw MusicServiceError.targetChanged }
            for origin in target.origins.indices.dropFirst() {
                guard let variant = values[.init(target: index, origin: origin)] else { throw MusicServiceError.targetChanged }
                song = try MusicModel.mergeSources(song, adding: variant["sources"].array)
            }
            mergedBytes += song.bytes.count
            try musicRequire(mergedBytes <= 4 * 1024 * 1024, .limit)
            songs.append(song)
        }
        return songs
    }

    private nonisolated static func renewSearchOrigins(_ origins: [(MusicSearchOriginID, MusicSearchBatchOrigin)],
        platform: MusicPublicBrowsePlatform, query: String, port: MusicNativePlatforms,
        intent: MusicQueueAuthorization?, owner: NativeMusicService, epoch: UUID) async throws -> [(MusicSearchOriginID, MJ)] {
        var uniqueRanges = Set<MusicSearchOriginRange>()
        for (_, origin) in origins {
            uniqueRanges.insert(.init(page: origin.page, limit: origin.limit))
        }
        let ranges = uniqueRanges.sorted { first, second in
            if first.page != second.page { return first.page < second.page }
            return first.limit < second.limit
        }
        var values: [(MusicSearchOriginID, MJ)] = [], bytes = 0
        for range in ranges {
            try Task.checkCancellation(); try intent?.check()
            let observation = await owner.beginSourceDiscoveryObservation(platform: platform.rawValue, query: query,
                page: range.page, limit: range.limit, scope: .page)
            let result: MusicNativeTrackPage
            do {
                result = try await port.search(platform: platform.rawValue, query: query,
                                                page: range.page, limit: range.limit)
            } catch {
                if (try? await owner.check(epoch)) != nil {
                    await owner.recordSearchFailure(observation, failure: MusicServiceError.sanitized(error))
                }
                throw error
            }
            try Task.checkCancellation(); try intent?.check()
            guard result.total >= 0,
                  (platform == .migu || result.items.count <= range.limit),
                  result.items.allSatisfy({ $0.platform == platform.rawValue }),
                  MusicSearchPagination.validNextPage(page: range.page, limit: range.limit,
                      next: result.nextPage, hasItems: !result.items.isEmpty) else {
                throw MusicServiceError.upstreamInvalid
            }
            let candidates = try result.items.map { raw -> (MJ, MusicRemoteTrack) in
                let song = try MusicModel.normalize(raw)
                try musicRequire(song.bytes.count <= 64 * 1024, .limit)
                return (song, try MusicModel.track(song, .init(kind: .library, id: song["songId"].string)))
            }
            try await owner.check(epoch)
            let completed = await owner.finishSourceDiscoveryObservation(observation)
            try await owner.recordSearchPage(result, observation: completed)
            for (id, origin) in origins where origin.page == range.page && origin.limit == range.limit {
                guard let match = candidates.first(where: { origin.matches($0.1) }) else {
                    throw MusicServiceError.targetChanged
                }
                bytes += match.0.bytes.count
                try musicRequire(bytes <= 4 * 1024 * 1024, .limit)
                values.append((id, match.0))
            }
        }
        return values
    }
}
