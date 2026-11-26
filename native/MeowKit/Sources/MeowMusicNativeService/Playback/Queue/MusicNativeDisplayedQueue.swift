import Foundation
import MeowMusicPlatform

extension NativeMusicService {
    public func replaceDisplayedQueue(_ tracks: [MusicRemoteTrack], index: Int,
                                      origin: MusicDisplayedQueueOrigin, expectedRevision: Int,
                                      intent: MusicQueueAuthorization) async throws -> MusicPlaybackState {
        try check(); try intent.check()
        try musicRequire(tracks.indices.contains(index))
        let context: MusicPlayContextRecord, historyDate: String?
        switch origin {
        case .song:
            context = MusicPlayContextRecord(sourceType: "song")
            historyDate = nil
        case .section(let title):
            context = MusicPlayContextRecord(sourceTitle: title, playlist: title, sourceType: "section")
            historyDate = nil
        case .history(let date):
            context = MusicPlayContextRecord(sourceType: "history-song")
            historyDate = date
        }
        let result = try intent.perform {
            try transaction([.queue, .playback, .history]) { state in
                try musicRequire(state.queue.revision == expectedRevision, .staleRevision)
                if let historyDate {
                    let ids = Set(state.history.days.first { $0.date == historyDate }?.songs.map(\.songID) ?? [])
                    try musicRequire(tracks.allSatisfy { ids.contains($0.songId) }, .notFound)
                }
                let songs = try repositorySongs(tracks.map(\.songId), state)
                let old = state.queue
                let entries = try makeEntries(songs, state: &state, context: context)
                try archive(old, into: &state)
                state.queue.entries = entries
                state.queue.currentID = entries[index].id
                state.queue.history = []; state.queue.priority = []; state.queue.failed = []
                state.queue.source = MusicSourceRecord.fromContext(context)
                similarWillChangeQueue(.replace, queue: &state.queue)
                state.queue.revision = expectedRevision + 1
                state.resetPosition()
                return stateDTO(state)
            }
        }
        invalidatePlayback(reason: .ui); similarDidChangeQueue(.replace)
        return result
    }
}
