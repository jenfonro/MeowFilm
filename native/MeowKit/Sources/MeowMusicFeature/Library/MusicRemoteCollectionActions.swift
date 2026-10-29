import Foundation

public enum MusicRemoteCollectionFilter {
    public static func matches(_ track: MusicRemoteTrack, query: String) -> Bool {
        matches(title: track.title, artist: track.artist, album: track.album, query: query)
    }
    public static func matches(title: String, artist: String, album: String, query: String) -> Bool {
        let query = query.trimmingCharacters(in: .whitespacesAndNewlines).localizedLowercase
        return query.isEmpty || [title, artist, album].contains {
            $0.localizedLowercase.range(of: query, options: .literal) != nil
        }
    }
}

/// Native actions for complete remote collection snapshots.
///
/// Reference: iWebPlus b900c259, browse.js importCloudPlaylist/filteredSongs/
/// playDetail and browse-data.js importPlaylist. Both actions finish reading
/// the bounded source snapshot before publishing anything. Display handles
/// and the currently visible page are never the input to either action.
public protocol MusicRemoteCollectionServing: Sendable {
    /// Creates one device-local playlist, including every source page. The
    /// normal library identity merge removes duplicate collection membership.
    /// This does not bind an account, request a remote write, or change playback.
    ///
    /// `intent` belongs to this save attempt (cancel it when its dialog closes).
    /// The caller continues to own and close `source`. A failed read/validation
    /// or CAS must not leave a partially created playlist.
    func saveRemoteCollection(source: MusicRemoteQueueSource, name: String,
                              intent: MusicQueueAuthorization) async throws -> MusicCollection

    /// Returns a separate, finite read snapshot, preserving matching occurrence
    /// order and duplicates. Matches a trimmed, lowercased query literally
    /// against title, artist OR album; an empty query matches every row.
    /// No matches throws MusicServiceError.notFound, not an empty playable queue.
    ///
    /// `intent` fences construction only. Once accepted, the caller owns the
    /// returned source and must close it on replacement or leaving the detail.
    /// Its authorization is independent but synchronously subordinate to the
    /// parent's authorization. Closing/revoking the result never closes/revokes
    /// the parent. Parent validation and native access/epoch checks still apply
    /// to every read/commit, including previously cached pages.
    ///
    /// Use MusicRemoteQueueServing.remoteQueuePage/replaceRemoteQueue (or their
    /// model entry points) with the result and its filtered occurrence index.
    /// Constructing it neither alters the queue nor resolves/plays media.
    func filteredRemoteQueue(source: MusicRemoteQueueSource, query: String,
                             intent: MusicQueueAuthorization) async throws -> MusicRemoteQueueSource
}
