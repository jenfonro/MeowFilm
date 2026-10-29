// SPDX-License-Identifier: GPL-3.0-only
import Foundation
import MeowMusicFeature

extension NativeMusicService: MusicSearchBatchPlaying {
    public func replaceSearchBatch(_ request: MusicSearchBatchRequest, expectedRevision: Int,
                                   intent: MusicQueueAuthorization) async throws -> MusicPlaybackState {
        try request.validate()
        try check(); try intent.check()
        for target in request.targets { try requirePlatform(target.platform.rawValue) }
        let own = epoch, port = publicPlatforms
        try musicRequire(try read().queue["revision"].int == expectedRevision, .staleRevision)

        // A freshly displayed result is already owner-validated. Do not make
        // tapping it depend on a redundant search request. Expired/evicted
        // handles still renew their exact pages; caller DTOs never become songs.
        var retained: [Int: MJ] = [:]
        for (index, target) in request.targets.enumerated() {
            guard target.track.ref.kind == .handle, let handle = handles[target.track.ref.id],
                  now().timeIntervalSince(handle.at) < 600 else { continue }
            let track = try MusicModel.track(handle.song, target.track.ref)
            try musicRequire(target.matches(track), .targetChanged)
            retained[index] = handle.song
        }
        let captured = retained
        let songs = try await work(label: "searchBatch") {
            try await Self.searchBatchSongs(request, retained: captured, port: port, intent: intent)
        }
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

    private nonisolated static func searchBatchSongs(_ request: MusicSearchBatchRequest,
        retained: [Int: MJ], port: MusicNativePlatforms, intent: MusicQueueAuthorization) async throws -> [MJ] {
        let platforms = [MusicPublicBrowsePlatform.netease, .qq, .kugou].filter { platform in
            request.targets.enumerated().contains { retained[$0.offset] == nil && $0.element.platform == platform }
        }
        return try await withThrowingTaskGroup(of: [(Int, MJ)].self) { group in
            for platform in platforms {
                group.addTask {
                    let selected = request.targets.enumerated().filter {
                        retained[$0.offset] == nil && $0.element.platform == platform
                    }
                    let pages = Set(selected.map { $0.element.page }).sorted()
                    var values: [(Int, MJ)] = [], bytes = 0
                    for page in pages {
                        try Task.checkCancellation(); try intent.check()
                        let result = try await port.search(platform: platform.rawValue, query: request.query, page: page)
                        try Task.checkCancellation(); try intent.check()
                        try musicRequire(result.items.count <= MusicNativeSearchProtocol.maximumItems(platform.rawValue) &&
                            (0...1_000_000).contains(result.total) &&
                            result.items.allSatisfy { $0.platform == platform.rawValue } &&
                            (result.nextPage == nil || result.nextPage! > page && result.nextPage! <= 200 &&
                                !result.items.isEmpty), .upstreamInvalid)
                        let candidates = try result.items.map { raw -> (MJ, MusicRemoteTrack) in
                            let song = try MusicModel.normalize(raw)
                            try musicRequire(song.bytes.count <= 64 * 1024, .limit)
                            // A DTO projection, not a registered/library ref.
                            return (song, try MusicModel.track(song, .init(kind: .library, id: song["songId"].string)))
                        }
                        for (index, target) in selected where target.page == page {
                            guard let match = candidates.first(where: { target.matches($0.1) }) else {
                                throw MusicServiceError.targetChanged
                            }
                            bytes += match.0.bytes.count
                            try musicRequire(bytes <= 4 * 1024 * 1024, .limit)
                            values.append((index, match.0))
                        }
                    }
                    return values
                }
            }
            var values = retained, bytes = retained.values.reduce(0) { $0 + $1.bytes.count }
            try musicRequire(bytes <= 4 * 1024 * 1024, .limit)
            for try await platform in group {
                for (index, song) in platform {
                    bytes += song.bytes.count
                    try musicRequire(bytes <= 4 * 1024 * 1024 && values[index] == nil, .limit)
                    values[index] = song
                }
            }
            try intent.check(); try Task.checkCancellation()
            try musicRequire(values.count == request.targets.count, .targetChanged)
            return try request.targets.indices.map { index in
                guard let song = values[index] else { throw MusicServiceError.targetChanged }
                return song
            }
        }
    }
}
