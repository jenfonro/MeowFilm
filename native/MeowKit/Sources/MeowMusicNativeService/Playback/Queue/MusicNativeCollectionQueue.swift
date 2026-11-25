// SPDX-License-Identifier: Apache-2.0
// Same native transaction/occurrence/history semantics as MusicNativeQueue's
// .collection branch, with an explicitly validated filtered membership list.
import Foundation
import MeowMusicFeature

extension NativeMusicService: MusicCollectionQueueServing {
    public func changeCollectionQueue(collection requested: MusicCollection, refs: [MusicReference],
                                      index: Int, expectedRevision: Int) async throws -> MusicPlaybackState {
        let result = try similarQueueTransaction([.queue, .playback, .history]) { d in
            try musicRequire(d.queue.revision == expectedRevision, .staleRevision)
            let source = try collection(requested.id, d)
            try musicRequire(source.revision == requested.revision, .staleRevision)
            try musicRequire(!refs.isEmpty && refs.indices.contains(index))
            try musicRequire(expectedRevision < musicMaxSafeInteger, .limit)

            let ids = Set(refs.map(\.id)), members = Set(source.order)
            try musicRequire(refs.allSatisfy { $0.kind == .library } &&
                ids.count == refs.count && ids.isSubset(of: members))
            let songs = try refs.map { try song($0, d) }
            let old = d.queue
            let context = MusicPlayContextRecord(collectionId: source.id, sourceTitle: source.name)
            let rows = try makeEntries(songs, state: &d, context: context)
            try archive(old, into: &d)
            d.queue.entries = rows
            d.queue.currentID = rows[index].id
            d.queue.history = []; d.queue.priority = []; d.queue.failed = []
            d.queue.source = MusicSourceRecord(id: source.id, title: source.name)
            similarWillChangeQueue(.replace, queue: &d.queue)
            d.queue.revision = expectedRevision + 1
            d.resetPosition()
            return stateDTO(d)
        }
        invalidatePlayback(reason: .ui)
        similarDidChangeQueue(.replace)
        return result
    }
}
