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

public struct MusicPresentationCacheStats: Sendable, Equatable {
    public let imageCount: Int
    public let imageBytes: Int64
    public let lyricCount: Int
    public let lyricBytes: Int64
    public let totalBytes: Int64
    public let capacityBytes: Int64
    public let diskFileCount: Int
    public let diskBytes: Int64
    public let closed: Bool

    public init(imageCount: Int, imageBytes: Int64, lyricCount: Int, lyricBytes: Int64,
                totalBytes: Int64, capacityBytes: Int64, diskFileCount: Int, diskBytes: Int64, closed: Bool) {
        self.imageCount = imageCount; self.imageBytes = imageBytes
        self.lyricCount = lyricCount; self.lyricBytes = lyricBytes
        self.totalBytes = totalBytes; self.capacityBytes = capacityBytes
        self.diskFileCount = diskFileCount; self.diskBytes = diskBytes
        self.closed = closed
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
    func clearImages() async throws
    func clearLyrics() async throws
    func clear() async throws
    func stats() async -> MusicPresentationCacheStats
}
