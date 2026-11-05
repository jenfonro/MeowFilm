import Foundation
import MeowMusicFeature

extension NativeMusicService: MusicDisplayedQueuePlaying {
    public func replaceDisplayedQueue(_ tracks: [MusicRemoteTrack], index: Int,
                                      origin: MusicDisplayedQueueOrigin, expectedRevision: Int,
                                      intent: MusicQueueAuthorization) async throws -> MusicPlaybackState {
        try check(); try intent.check()
        try musicRequire(!tracks.isEmpty && tracks.indices.contains(index))
        let own = epoch, before = try read()
        try musicRequire(before.queue["revision"].int == expectedRevision, .staleRevision)
        let context: MJ, binding: MusicNativeHomeBinding?
        let historyDate: String?
        switch origin {
        case .song:
            context = .obj([("sourceType", .string("song"))])
            binding = try await currentHomeBinding(); historyDate = nil
        case .section(let title):
            _ = try MJ.string(title).text(1024, empty: true)
            context = .obj([("sourceType", .string("section")),
                            ("sourceTitle", .string(title)), ("playlist", .string(title))])
            binding = try await currentHomeBinding(); historyDate = nil
        case .history(let date):
            _ = try musicText(date, maximum: 32)
            try musicRequire(tracks.count == 1)
            context = .obj([("sourceType", .string("history-song"))])
            binding = nil; historyDate = date
        }
        try check(own); try intent.check()

        var resolved: [Int: MJ] = [:], missing: [(Int, String)] = [], bytes = 0
        var cached: [MJ] = []
        if historyDate == nil, let binding, let cache = homeCache,
           cache.source == .netease, cache.scope == binding.scope, cache.revision == binding.revision {
            for section in cache.page.sections {
                for item in section.items {
                    if case .song(let raw) = item.content { cached.append(try MusicModel.normalize(raw)) }
                }
            }
        }
        var cachedByID: [Data: MJ] = [:]
        for value in cached { cachedByID[Data(value["songId"].string.utf8)] = value }
        let historySongs: [Data: MJ]
        if let historyDate {
            let rows = (before.history["days"].array.first {
                $0["date"].string == historyDate
            })?["songs"].array
            var values: [Data: MJ] = [:]
            for row in rows ?? [] { values[Data(row["song"]["songId"].string.utf8)] = row["song"] }
            historySongs = values
        } else { historySongs = [:] }
        var resolvedByID: [Data: MJ] = [:]
        for (position, track) in tracks.enumerated() {
            try intent.check()
            try musicRequire(!track.songId.isEmpty && (1...16).contains(track.sources.count))
            try musicRequire(try JSONEncoder().encode(track).count <= 64 * 1024, .limit)
            for source in track.sources { try requirePlatform(source.platform) }
            let raw: MJ?
            if let historyDate {
                raw = historySongs[Data(track.songId.utf8)]
                guard raw != nil else { throw MusicServiceError.notFound }
            } else if let value = resolvedByID[Data(track.songId.utf8)] {
                raw = value
            } else if let value = try? song(track.ref, before) {
                raw = value
            } else {
                raw = cachedByID[Data(track.songId.utf8)]
            }
            if let raw {
                try Self.checkDisplayedIdentity(raw, track)
                bytes += raw.bytes.count
                try musicRequire(bytes <= 4 * 1024 * 1024, .limit)
                resolved[position] = raw
                resolvedByID[Data(track.songId.utf8)] = raw
            } else {
                // Reference discovery songs are NetEase recordings. Re-fetch
                // only that exact native ID, never title-search or another source.
                try musicRequire(track.sources.count == 1 && track.sources[0].platform == "wy", .targetChanged)
                let id = track.sources[0].nativeId
                try MusicHomeAction.songCollection(ids: [id]).validate()
                missing.append((position, id))
            }
        }
        if !missing.isEmpty {
            guard let binding else { throw MusicServiceError.notFound }
            let port = publicPlatforms, owner = accounts as? any MusicNativeHomeProviding
            let requested = missing.map(\.1)
            let rows = try await work(label: "musicDisplayedQueue") {
                var songs: [MusicNativeTrackData] = []
                // The reference hydration limit is 100. Batches stay under the
                // existing request budget; display-handle capacity is unchanged.
                let ids = Array(Set(requested)).sorted()
                for offset in stride(from: 0, to: ids.count, by: 100) {
                    try intent.check(); try binding.authorization.check()
                    let batch = Array(ids.dropFirst(offset).prefix(100))
                    if binding.loggedIn, let owner {
                        let data = try await owner.nativeHomePayload(.songMetadata(ids: batch), binding: binding)
                        let root = try MJ.parse(data, maximum: 2 * 1024 * 1024)
                        try musicRequire(root["songs"].isArray && root["songs"].array.count <= batch.count, .upstreamInvalid)
                        for row in root["songs"].array { songs.append(try MusicNativePlatforms.browseWYSong(row)) }
                    } else { songs += try await port.homeCollectionSongs(batch) }
                }
                return try songs.map(MusicModel.normalize)
            }
            try check(own); try intent.check()
            var fetchedByID: [Data: MJ] = [:]
            for raw in rows {
                if let source = try? MusicModel.sources(raw).first(where: { $0.platform == "wy" }) {
                    fetchedByID[Data(source.nativeId.utf8)] = raw
                }
            }
            for (position, id) in missing {
                let raw = fetchedByID[Data(id.utf8)]
                guard let raw else { throw MusicServiceError.notFound }
                try Self.checkDisplayedIdentity(raw, tracks[position])
                bytes += raw.bytes.count
                try musicRequire(bytes <= 4 * 1024 * 1024, .limit)
                resolved[position] = raw
            }
        }
        if let binding { try await checkHomeBinding(binding, epoch: own) }
        try check(own); try intent.check()
        let songs = try tracks.indices.map { position -> MJ in
            guard let raw = resolved[position] else { throw MusicServiceError.notFound }
            return raw
        }
        let commit = {
            try intent.perform {
                try self.similarQueueTransaction(["queue", "playback", "history"]) { document in
                    try musicRequire(document.queue["revision"].int == expectedRevision, .staleRevision)
                    if let historyDate {
                        // A deleted history row must not be reintroduced after
                        // suspension while flushing playback/account state.
                        let exists = document.history["days"].array.first(where: { $0["date"].string == historyDate })?["songs"]
                            .array.contains(where: { $0["song"]["songId"].string.utf8.elementsEqual(tracks[0].songId.utf8) }) ?? false
                        try musicRequire(exists, .notFound)
                    }
                    let old = document.queue
                    let entries = try self.makeEntries(songs, queue: &document.queue, context: context)
                    self.archive(old, into: &document)
                    document.queue["entries"] = .array(entries)
                    document.queue["currentId"] = entries[index]["id"]
                    for key in ["history", "priority", "failed"] { document.queue[key] = .array([]) }
                    document.queue["source"] = .null
                    self.similarWillChangeQueue(.replace, queue: &document.queue)
                    document.queue["revision"] = .n(expectedRevision + 1); document.resetPosition()
                    return self.stateDTO(document)
                }
            }
        }
        let result: MusicPlaybackState
        if let binding { result = try binding.authorization.perform(commit) }
        else { result = try commit() }
        invalidatePlayback(); similarDidChangeQueue(.replace)
        return result
    }

    private static func checkDisplayedIdentity(_ song: MJ, _ displayed: MusicRemoteTrack) throws {
        let actual = try MusicModel.track(song, displayed.ref)
        try musicRequire(actual.songId.utf8.elementsEqual(displayed.songId.utf8) &&
            actual.sources == displayed.sources, .targetChanged)
    }
}
