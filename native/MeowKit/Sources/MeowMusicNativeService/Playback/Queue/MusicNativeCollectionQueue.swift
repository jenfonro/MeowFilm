// SPDX-License-Identifier: Apache-2.0
// Same native transaction/occurrence/history semantics as MusicNativeQueue's
// .collection branch, with an explicitly validated filtered membership list.
import Foundation
import MeowMusicFeature

extension NativeMusicService: MusicCollectionQueueServing {
    public func changeCollectionQueue(collection requested: MusicCollection, refs: [MusicReference],
                                      index: Int, expectedRevision: Int) async throws -> MusicPlaybackState {
        let result = try similarQueueTransaction(["queue", "playback", "history"]) { d in
            try musicRequire(d.queue["revision"].int == expectedRevision, .staleRevision)
            let source = try collection(requested.id, d)
            try musicRequire(source["revision"].int == requested.revision, .staleRevision)
            try musicRequire(!refs.isEmpty && refs.indices.contains(index))
            try musicRequire(expectedRevision < 9_007_199_254_740_991, .limit)

            // A queue occurrence or transient handle that happens to resolve
            // to the same recording is not this local collection admission.
            // Compare opaque IDs byte-for-byte, not Swift canonical equivalence.
            let ids = musicIDs(refs.map(\.id)), members = musicIDs(source["order"].strings)
            try musicRequire(refs.allSatisfy { $0.kind == .library } &&
                ids.count == refs.count && ids.isSubset(of: members))
            let songs = try refs.map { try song($0, d) }
            let old = d.queue
            let context = MJ.obj([("collectionId", source["id"]), ("sourceTitle", source["name"])])
            let rows = try makeEntries(songs, queue: &d.queue, context: context)
            archive(old, into: &d)
            d.queue["entries"] = .array(rows)
            d.queue["currentId"] = rows[index]["id"]
            for key in ["history", "priority", "failed"] { d.queue[key] = .array([]) }
            d.queue["source"] = .obj([("id", source["id"]), ("title", source["name"])])
            similarWillChangeQueue(.replace, queue: &d.queue)
            d.queue["revision"] = .n(expectedRevision + 1)
            d.resetPosition()
            return stateDTO(d)
        }
        // Match changeQueue: invalidate the old playback/lyrics work only after
        // a confirmed CAS, never on malformed input or a revision/write failure.
        invalidatePlayback()
        similarDidChangeQueue(.replace)
        return result
    }
}
