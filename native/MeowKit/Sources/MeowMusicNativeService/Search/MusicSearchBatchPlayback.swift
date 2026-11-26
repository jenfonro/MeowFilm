import Foundation
import MeowMusicPlatform

extension NativeMusicService {
    public func searchRecording(_ request: MusicSearchBatchRequest) async throws -> MusicRemoteTrack {
        try musicRequire(request.targets.count == 1)
        return try repositoryTrack(song(request.targets[0].track.ref, read()))
    }
    public func replaceSearchBatch(_ request: MusicSearchBatchRequest, expectedRevision: Int,
                                   intent: MusicQueueAuthorization) async throws -> MusicPlaybackState {
        try check(); try intent.check(); try musicRequire(request.targets.indices.contains(request.startIndex))
        let result = try intent.perform {
            try transaction([.queue, .playback, .history]) { state in
                try musicRequire(state.queue.revision == expectedRevision, .staleRevision)
                let songs = try request.targets.map { try song($0.track.ref, state) }
                let old = state.queue
                let sourceType = songs.count == 1 ? "search-song" : "search"
                let rows = try makeEntries(
                    songs,
                    state: &state,
                    context: MusicPlayContextRecord(
                        sourceTitle: "搜索",
                        sourceType: sourceType,
                        searchKeyword: request.query
                    )
                )
                try archive(old, into: &state)
                state.queue.entries = rows; state.queue.currentID = rows[request.startIndex].id
                state.queue.history = []; state.queue.priority = []; state.queue.failed = []
                state.queue.source = MusicSourceRecord(
                    sourceType: sourceType,
                    sourceTitle: "搜索",
                    seedEntryId: songs.count == 1 ? rows[0].id : nil
                )
                similarWillChangeQueue(.replace, queue: &state.queue)
                state.queue.revision = expectedRevision + 1; state.resetPosition()
                return stateDTO(state)
            }
        }
        invalidatePlayback(reason: .ui); similarDidChangeQueue(.replace)
        if request.targets.count == 1, platformEnabled("wy"), accounts != nil,
           let current = result.currentId {
            let song = try song(.init(kind: .queue, id: current), read())
            if MusicNativeSimilarData.nativeID(song) != nil { similarMode.queue = .init(revision: result.revision, currentEntryID: current, seed: song) }
        }
        return result
    }
}
