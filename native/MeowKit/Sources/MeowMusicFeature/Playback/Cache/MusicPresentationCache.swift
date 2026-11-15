import Foundation
import MeowUI

public struct MusicLyricDisplaySnapshot: Codable, Sendable {
    public let songID: String
    public let sourceKey: String
    public let lines: [MusicLyricLine]
    public init(songID: String, sourceKey: String, lines: [MusicLyricLine]) {
        self.songID = songID; self.sourceKey = sourceKey; self.lines = lines
    }
}

/// One native resource owner for shared images and permanent-song lyrics.
/// Image clears and lyric clears have independent publication fences.
public protocol MusicPresentationCaching: ArtworkByteCaching {
    var lyricRevision: UUID { get }
    func lyrics(songID: String, authorization: MusicQueueAuthorization) async -> MusicLyricDisplaySnapshot?
    func storeLyrics(_ lines: [MusicLyricLine], sourceKey: String, songID: String,
                     revision: UUID, authorization: MusicQueueAuthorization) async
    func played(songID: String, artwork: URL?, confirmedAt: Date) async
    func clearImages() async
    func clearLyrics() async
    func clear() async
}
