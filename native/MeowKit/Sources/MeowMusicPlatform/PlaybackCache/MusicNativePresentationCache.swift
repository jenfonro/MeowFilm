import Foundation
import MeowUI
import MeowMusicFeature
import MeowStorage

private final class PresentationCacheRevisions: @unchecked Sendable {
    private let lock = NSLock()
    private var image = UUID(), lyric = UUID()
    func value(images: Bool) -> UUID { lock.withLock { images ? image : lyric } }
    func clear(images: Bool, lyrics: Bool) {
        lock.withLock {
            if images { image = UUID() }
            if lyrics { lyric = UUID() }
        }
    }
}

/// URL-indexed original image files and typed lyric lines/words. Reads select
/// one resource; playing a song only touches its two indexed timestamps.
public actor MusicNativePresentationCache: MusicPresentationCaching {
    private static let lifetime: TimeInterval = 30 * 86_400
    private static let capacity = Int(NativeMusicAudioCacheHTTPPolicy.maximumBytes)
    private nonisolated let revisions = PresentationCacheRevisions()
    public nonisolated var revision: UUID { revisions.value(images: true) }
    public nonisolated var lyricRevision: UUID { revisions.value(images: false) }
    private let database: SQLiteDatabase
    private let clock: @Sendable () -> Date
    private var images: RawResourceDirectory?
    private var closed = false
    private var imagesReadable = true, lyricsReadable = true

    public init(database: SQLiteDatabase, clock: @escaping @Sendable () -> Date = { Date() }) {
        self.database = database; self.clock = clock
    }
    private func files(_ db: SQLiteConnection) throws -> RawResourceDirectory {
        if let images { return images }
        let directory = try RawResourceDirectory(dataRoot: database.dataRoot, namespace: "music-images")
        try directory.recover(referenced: Set(try db.query("SELECT filename FROM ios_music_images").map { try $0.text("filename") }))
        images = directory
        return directory
    }
    private func removeImage(_ row: SQLiteRow, db: SQLiteConnection, directory: RawResourceDirectory) throws {
        let resource = try row.text("resource")
        try db.execute("UPDATE ios_music_images SET ready=0 WHERE resource=?", [.text(resource)])
        do {
            try directory.remove(row.text("filename"))
            try db.execute("DELETE FROM ios_music_images WHERE resource=?", [.text(resource)])
        } catch {
            // An undeleted leaf remains charged and ineligible.
        }
    }
    private func makeRoom(_ db: SQLiteConnection, directory: RawResourceDirectory, incoming: Int, at: Date) throws {
        for row in try db.query("SELECT * FROM ios_music_images WHERE updated_at<=? OR ready=0", [.optional(at.addingTimeInterval(-Self.lifetime))]) {
            try removeImage(row, db: db, directory: directory)
        }
        try db.execute("DELETE FROM ios_music_lyrics WHERE updated_at<=?", [.optional(at.addingTimeInterval(-Self.lifetime))])
        var size = try db.query("SELECT (SELECT coalesce(sum(bytes),0) FROM ios_music_images)+" +
            "(SELECT coalesce(sum(bytes),0) FROM ios_music_lyrics) AS bytes").first!.integer("bytes")
        for row in try db.query("SELECT * FROM ios_music_images WHERE ready=1 ORDER BY updated_at,resource") where size + incoming > Self.capacity {
            try removeImage(row, db: db, directory: directory)
            if try db.query("SELECT resource FROM ios_music_images WHERE resource=?", [row["resource"]]).isEmpty {
                size -= try row.integer("bytes")
            }
        }
        for row in try db.query("SELECT song_id,bytes FROM ios_music_lyrics ORDER BY updated_at,song_id") where size + incoming > Self.capacity {
            try db.execute("DELETE FROM ios_music_lyrics WHERE song_id=?", [row["song_id"]])
            size -= try row.integer("bytes")
        }
        guard size + incoming <= Self.capacity else { throw SQLiteStorageError.full }
    }
    public func read(_ url: URL) -> ArtworkBytes? {
        guard !closed, imagesReadable else { return nil }
        do {
            return try database.readSync { db in
                let now = clock()
                guard let row = try db.query("SELECT * FROM ios_music_images WHERE resource=? AND ready=1 AND updated_at>? AND updated_at<=?",
                    [.text(url.absoluteString), .optional(now.addingTimeInterval(-Self.lifetime)), .optional(now)]).first,
                      let data = try files(db).read(row.text("filename"), maximumBytes: 4 * 1024 * 1024),
                      data.count == (try row.integer("bytes")) else { return nil }
                return .init(data: data, expiresAt: Date(timeIntervalSince1970: try row.double("updated_at")).addingTimeInterval(Self.lifetime))
            }
        } catch { return nil }
    }
    public func store(_ data: Data, for url: URL, revision expected: UUID) -> ArtworkBytes? {
        guard !closed, imagesReadable, !Task.isCancelled, revision == expected, !data.isEmpty, data.count <= 4 * 1024 * 1024,
              MusicArtworkURL.normalized(url.absoluteString) == url else { return nil }
        let at = clock()
        var created: String?
        do {
            try database.writeSync(ownedBytes: data.count) { db in
                let directory = try files(db)
                try SQLiteDatabase.transaction(db, commit: { action in
                    guard revision == expected else { throw SQLiteStorageError.conflict }
                    try action()
                }) { db in
                    if let old = try db.query("SELECT * FROM ios_music_images WHERE resource=?", [.text(url.absoluteString)]).first {
                        try removeImage(old, db: db, directory: directory)
                        guard try db.query("SELECT resource FROM ios_music_images WHERE resource=?", [.text(url.absoluteString)]).isEmpty else {
                            throw SQLiteStorageError.io
                        }
                    }
                    try makeRoom(db, directory: directory, incoming: data.count, at: at)
                    let filename = try directory.write(data); created = filename
                    try db.execute("INSERT INTO ios_music_images(resource,filename,updated_at,bytes,ready) VALUES(?,?,?,?,1)",
                        [.text(url.absoluteString), .text(filename), .optional(at), .integer(data.count)])
                }
            }
        } catch {
            if let created { try? database.writeSync { _ in try images?.remove(created) } }
            guard revision == expected else { return nil }
            return .init(data: data, expiresAt: at.addingTimeInterval(Self.lifetime))
        }
        guard revision == expected else { return nil }
        return .init(data: data, expiresAt: at.addingTimeInterval(Self.lifetime))
    }
    public func lyrics(songID: String, authorization: MusicQueueAuthorization) -> MusicLyricDisplaySnapshot? {
        guard !closed, lyricsReadable, authorization.isActive else { return nil }
        do {
            let value = try database.readSync { db in
                try SQLiteDatabase.readSnapshot(db) { db -> MusicLyricDisplaySnapshot? in
                    let now = clock()
                    guard let row = try db.query("SELECT source_key FROM ios_music_lyrics WHERE song_id=? AND updated_at>? AND updated_at<=?",
                        [.text(songID), .optional(now.addingTimeInterval(-Self.lifetime)), .optional(now)]).first else { return nil }
                    var grouped: [Int: [MusicLyricWord]] = [:]
                    for word in try db.query("SELECT line_position,time,duration,text FROM ios_music_lyric_words WHERE song_id=? ORDER BY line_position,position",
                        [.text(songID)]) {
                        let line = try word.integer("line_position")
                        grouped[line, default: []].append(.init(time: try word.double("time"),
                            duration: try word.double("duration"), text: try word.text("text")))
                    }
                    var lines: [MusicLyricLine] = []
                    for line in try db.query("SELECT * FROM ios_music_lyric_lines WHERE song_id=? ORDER BY position", [.text(songID)]) {
                        let words = grouped[try line.integer("position")] ?? []
                        lines.append(.init(index: try line.integer("line_index"), time: try line.double("time"), end: try line.double("end"),
                            text: try line.text("text"), translation: try line.text("translation"), words: words))
                    }
                    guard !lines.isEmpty, Self.usableLyrics(lines) else { return nil }
                    return .init(songID: songID, sourceKey: try row.text("source_key"), lines: lines)
                }
            }
            return try authorization.perform { value }
        } catch { return nil }
    }
    public func storeLyrics(_ lines: [MusicLyricLine], sourceKey: String, songID: String,
                            revision expected: UUID, authorization: MusicQueueAuthorization) {
        guard !closed, lyricsReadable, !Task.isCancelled, lyricRevision == expected, authorization.isActive, !lines.isEmpty,
              Self.usableLyrics(lines) else { return }
        let bytes = lines.reduce(songID.utf8.count + sourceKey.utf8.count) {
            $0 + 96 + $1.text.utf8.count + $1.translation.utf8.count + $1.words.reduce(0) { $0 + 48 + $1.text.utf8.count }
        }
        guard bytes <= 1024 * 1024 else { return }
        do {
            try database.writeSync(ownedBytes: bytes) { db in
                try SQLiteDatabase.transaction(db, commit: { action in try authorization.perform { try action() } }) { db in
                    try db.execute("DELETE FROM ios_music_lyrics WHERE song_id=?", [.text(songID)])
                    try makeRoom(db, directory: files(db), incoming: bytes, at: clock())
                    try db.execute("INSERT INTO ios_music_lyrics VALUES(?,?,?,?)", [.text(songID), .text(sourceKey), .optional(clock()), .integer(bytes)])
                    for (position, line) in lines.enumerated() {
                        try db.execute("INSERT INTO ios_music_lyric_lines VALUES(?,?,?,?,?,?,?)",
                            [.text(songID), .integer(position), .integer(line.index), .real(line.time), .real(line.end), .text(line.text), .text(line.translation)])
                        for (index, word) in line.words.enumerated() {
                            try db.execute("INSERT INTO ios_music_lyric_words VALUES(?,?,?,?,?,?)",
                                [.text(songID), .integer(position), .integer(index), .real(word.time), .real(word.duration), .text(word.text)])
                        }
                    }
                }
            }
        } catch {}
    }
    private static func usableLyrics(_ lines: [MusicLyricLine]) -> Bool {
        var previous = -Double.infinity
        return lines.allSatisfy { line in
            defer { previous = line.time }
            return line.time.isFinite && line.end.isFinite && line.time >= 0 && line.end >= line.time &&
                line.time >= previous &&
                line.words.allSatisfy { $0.time.isFinite && $0.duration.isFinite && $0.duration >= 0 }
        }
    }
    public func played(songID: String, artwork: URL?, confirmedAt: Date) {
        guard !closed, confirmedAt <= clock() else { return }
        try? database.transactionSync { db in
            try db.execute("UPDATE ios_music_lyrics SET updated_at=max(updated_at,?) WHERE song_id=?", [.optional(confirmedAt), .text(songID)])
            if let artwork {
                try db.execute("UPDATE ios_music_images SET updated_at=max(updated_at,?) WHERE resource=? AND ready=1",
                    [.optional(confirmedAt), .text(artwork.absoluteString)])
            }
        }
    }
    public func clearImages() { clear(images: true, lyrics: false) }
    public func clearLyrics() { clear(images: false, lyrics: true) }
    public func clear() { clear(images: true, lyrics: true) }
    private func clear(images: Bool, lyrics: Bool) {
        guard !closed else { return }
        revisions.clear(images: images, lyrics: lyrics)
        if images { imagesReadable = false }
        if lyrics { lyricsReadable = false }
        do { try database.transactionSync { db in
            if lyrics { try db.execute("DELETE FROM ios_music_lyrics") }
            if images {
                let directory = try files(db)
                for row in try db.query("SELECT * FROM ios_music_images") { try removeImage(row, db: db, directory: directory) }
            }
        }
            if images { imagesReadable = true }
            if lyrics { lyricsReadable = true }
        } catch {}
    }
    public func close() {
        closed = true; revisions.clear(images: true, lyrics: true); images = nil
    }
}
