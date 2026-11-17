import Foundation
import MeowMusicFeature

extension NativeMusicService: MusicDisplayedQueuePlaying {
    public func replaceDisplayedQueue(_ tracks: [MusicRemoteTrack], index: Int,
                                      origin: MusicDisplayedQueueOrigin, expectedRevision: Int,
                                      intent: MusicQueueAuthorization) async throws -> MusicPlaybackState {
        try check(); try intent.check()
        try musicRequire(tracks.indices.contains(index))
        let context: MJ, historyDate: String?
        switch origin {
        case .song: context = .obj([("sourceType", .string("song"))]); historyDate = nil
        case .section(let title):
            context = .obj([("sourceType", .string("section")), ("sourceTitle", .string(title)), ("playlist", .string(title))]); historyDate = nil
        case .history(let date): context = .obj([("sourceType", .string("history-song"))]); historyDate = date
        }
        let result = try intent.perform {
            try similarQueueTransaction(["queue", "playback", "history"]) { document in
                try musicRequire(document.queue["revision"].int == expectedRevision, .staleRevision)
                if let historyDate {
                    let ids = Set(document.history["days"].array.first { $0["date"].string == historyDate }?["songs"].array.map { $0["songId"].string } ?? [])
                    try musicRequire(tracks.allSatisfy { ids.contains($0.songId) }, .notFound)
                }
                let songs = try tracks.map { try repositorySong($0.songId, document) }
                let old = document.queue
                let entries = try makeEntries(songs, document: &document, context: context)
                try archive(old, into: &document)
                document.queue["entries"] = .array(entries); document.queue["currentId"] = entries[index]["id"]
                for key in ["history", "priority", "failed"] { document.queue[key] = .array([]) }
                document.queue["source"] = context
                similarWillChangeQueue(.replace, queue: &document.queue)
                document.queue["revision"] = .n(expectedRevision + 1); document.resetPosition()
                return stateDTO(document)
            }
        }
        invalidatePlayback(reason: .ui); similarDidChangeQueue(.replace)
        return result
    }
}
