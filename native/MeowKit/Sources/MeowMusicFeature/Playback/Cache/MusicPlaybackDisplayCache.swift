import Foundation

/// Last selected recording's presentation, not a media URL, playback grant,
/// queue or download. It may be read during cold restoration without playback.
public struct MusicPlaybackDisplayIdentity: Codable, Equatable, Sendable {
    public let scope: String
    public let occurrenceID: String
    public let songID: String
    public let contentFingerprint: String
    public init(scope: String, occurrenceID: String, songID: String, contentFingerprint: String) {
        self.scope = scope; self.occurrenceID = occurrenceID
        self.songID = songID; self.contentFingerprint = contentFingerprint
    }
}

public struct MusicPlaybackDisplaySnapshot: Codable, Sendable {
    public let identity: MusicPlaybackDisplayIdentity
    public let lyricSourceKey: String?
    public let lines: [MusicLyricLine]
    public let artworkSourceKey: String?
    public let artwork: Data?
    public init(identity: MusicPlaybackDisplayIdentity, lyricSourceKey: String? = nil,
                lines: [MusicLyricLine] = [], artworkSourceKey: String? = nil, artwork: Data? = nil) {
        self.identity = identity; self.lyricSourceKey = lyricSourceKey; self.lines = lines
        self.artworkSourceKey = artworkSourceKey; self.artwork = artwork
    }
}

/// Optional evictable native presentation persistence. Each patch preserves the
/// other part only for exactly the same identity. A revoked selection cannot
/// overwrite the newer selection, even when disk initialization was suspended.
public protocol MusicPlaybackDisplayCaching: Sendable {
    func read(_ identity: MusicPlaybackDisplayIdentity,
              authorization: MusicQueueAuthorization) async -> MusicPlaybackDisplaySnapshot?
    func storeLyrics(_ lines: [MusicLyricLine], sourceKey: String,
                     identity: MusicPlaybackDisplayIdentity, authorization: MusicQueueAuthorization) async
    func storeArtwork(_ data: Data, sourceKey: String,
                      identity: MusicPlaybackDisplayIdentity, authorization: MusicQueueAuthorization) async
    func clear() async
}
