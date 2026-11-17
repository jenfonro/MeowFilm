import Foundation
import MeowMusicFeature

extension NativeMusicService: MusicSearchBatchPlaying {
    public func searchRecording(_ request: MusicSearchBatchRequest) async throws -> MusicRemoteTrack {
        try musicRequire(request.targets.count == 1)
        return try repositoryTrack(song(request.targets[0].track.ref, read()))
    }
    public func replaceSearchBatch(_ request: MusicSearchBatchRequest, expectedRevision: Int,
                                   intent: MusicQueueAuthorization) async throws -> MusicPlaybackState {
        try check(); try intent.check(); try musicRequire(request.targets.indices.contains(request.startIndex))
        let result = try intent.perform {
            try similarQueueTransaction(["queue", "playback", "history"]) { document in
                try musicRequire(document.queue["revision"].int == expectedRevision, .staleRevision)
                let songs = try request.targets.map { try song($0.track.ref, document) }
                let old = document.queue
                let rows = try makeEntries(songs, document: &document, context: .obj([
                    ("sourceTitle", .string("搜索")), ("sourceType", .string(songs.count == 1 ? "search-song" : "search")),
                    ("searchKeyword", .string(request.query))]))
                try archive(old, into: &document)
                document.queue["entries"] = .array(rows); document.queue["currentId"] = rows[request.startIndex]["id"]
                for key in ["history", "priority", "failed"] { document.queue[key] = .array([]) }
                document.queue["source"] = .obj([("sourceType", .string(songs.count == 1 ? "search-song" : "search")), ("sourceTitle", .string("搜索"))])
                if songs.count == 1 { document.queue["source"]["seedEntryId"] = rows[0]["id"] }
                similarWillChangeQueue(.replace, queue: &document.queue)
                document.queue["revision"] = .n(expectedRevision + 1); document.resetPosition()
                return stateDTO(document)
            }
        }
        invalidatePlayback(reason: .ui); similarDidChangeQueue(.replace)
        if request.targets.count == 1, platformEnabled("wy"), accounts is any MusicNativeSimilarModeProviding,
           let current = result.currentId {
            let song = try song(.init(kind: .queue, id: current), read())
            if MusicNativeSimilarData.nativeID(song) != nil { similarMode.queue = .init(revision: result.revision, currentEntryID: current, seed: song) }
        }
        return result
    }
}
