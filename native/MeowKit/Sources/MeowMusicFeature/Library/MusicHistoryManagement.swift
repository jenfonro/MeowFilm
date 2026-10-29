import Foundation

/// In-memory action binding, not a song reference or a persisted credential.
/// Treat the value as opaque; only its issuing service may accept it.
public struct MusicHistoryActionToken: Sendable, Equatable {
    public let rawValue: String
    public init(rawValue: String) { self.rawValue = rawValue }
}

/// An occurrence in an archived queue. This row grants no playback, source,
/// library or download access, and does not occupy the temporary handle cache.
public struct MusicHistoryDisplayEntry: Sendable, Equatable, Identifiable {
    public let id: String
    public let index: Int
    public let title: String
    public let artist: String
    public let album: String
    public let artwork: URL?
    public let duration: Double
    public let displayIdentity: MusicQueueDisplayIdentity?

    public init(id: String, index: Int, title: String, artist: String, album: String,
                artwork: URL?, duration: Double, displayIdentity: MusicQueueDisplayIdentity? = nil) {
        self.id = id; self.index = index; self.title = title; self.artist = artist
        self.album = album; self.artwork = artwork; self.duration = duration
        self.displayIdentity = displayIdentity
    }
}

/// The complete, ordered display snapshot, including duplicate songs.
/// `selectedIndex` is the effective restore selection (nil only for an empty
/// snapshot). Do not reconstruct MusicReferences from occurrence IDs.
public struct MusicHistoryDetail: Sendable, Identifiable {
    public let summary: MusicPlayedPlaylist
    public let entries: [MusicHistoryDisplayEntry]
    public let selectedIndex: Int?
    public let sourceLabel: String?
    public let actionToken: MusicHistoryActionToken
    public var id: String { summary.id }

    public init(summary: MusicPlayedPlaylist, entries: [MusicHistoryDisplayEntry],
                selectedIndex: Int?, sourceLabel: String?, actionToken: MusicHistoryActionToken) {
        self.summary = summary; self.entries = entries; self.selectedIndex = selectedIndex
        self.sourceLabel = sourceLabel; self.actionToken = actionToken
    }
}

/// History operations shared by the queue UI/model and native music service.
///
/// Reference: iWebPlus b900c259 queue-sheet.js, playback-history.js and
/// queue.js. The UI's snapshot rows remain display-only. An explicit restore
/// index is also available to callers, without creating per-row handles.
public protocol MusicHistoryManaging: Sendable {
    /// No mutation, provider request, or media resolution. At most the existing
    /// 2,000 queue occurrences, never a first-page or 600-handle approximation.
    func historyDetail(id: String) async throws -> MusicHistoryDetail

    /// Deletes only this archived playlist, not played-song days, the active
    /// queue, saved collections, downloads or files. The full snapshot must
    /// still match the detail, in the same service/access generation.
    func removeHistory(detail: MusicHistoryDetail) async throws

    /// Atomically archives the replaced queue and restores the bound snapshot
    /// in the existing transaction/schema. Nil resumes its selected occurrence;
    /// an explicit index selects that exact occurrence, including duplicates.
    ///
    /// Also requires the current queue revision. The model must then refresh
    /// queue/state and prepareCurrent using the existing player. This operation
    /// does not resolve media, claim playback started or write a listening fact.
    ///
    /// A changed snapshot or issuer/generation rejects with targetChanged;
    /// unavailable access, missing history and revision conflicts retain the
    /// existing typed errors. Cancellation after CAS may be commitUnconfirmed:
    /// reread state rather than assuming rollback or retrying automatically.
    func restoreHistory(detail: MusicHistoryDetail, startIndex: Int?,
                        expectedRevision: Int) async throws -> MusicPlaybackState
}
