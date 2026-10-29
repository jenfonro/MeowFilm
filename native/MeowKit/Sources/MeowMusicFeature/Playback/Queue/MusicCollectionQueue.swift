/// Plays a filtered local collection through its queue owner while retaining
/// collection/history provenance.
///
/// refs is an ordered, nonempty, distinct subset (at most 2,000) of the exact
/// collection revision's library references; index addresses that subset.
/// The queue revision and collection revision are checked in one atomic write.
/// Source ID/title and entry collectionId/sourceTitle come from stored data,
/// not caller-provided display metadata.
///
/// Errors use MusicServiceError: staleRevision for either version conflict,
/// notFound for a missing/deleted collection, invalidRequest for an invalid
/// index/reference kind/nonmember/duplicate, limit for capacity, plus the
/// existing access, cancellation, storage and commitUnconfirmed errors.
/// A failed or unconfirmed write must not be automatically replayed.
public protocol MusicCollectionQueueServing: Sendable {
    func changeCollectionQueue(collection: MusicCollection, refs: [MusicReference], index: Int,
                               expectedRevision: Int) async throws -> MusicPlaybackState
}
