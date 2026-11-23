import Foundation
import MeowUI
import MeowMusicFeature
import MeowStorage

private final class PresentationCacheRevisions: @unchecked Sendable {
    private let lock = NSLock()
    private var image = UUID(), lyric = UUID()
    private var imageClears = 0, lyricClears = 0
    func value(images: Bool) -> UUID { lock.withLock { images ? image : lyric } }
    func permits(_ expected: UUID, images: Bool) -> Bool {
        lock.withLock { (images ? image : lyric) == expected && (images ? imageClears : lyricClears) == 0 }
    }
    func perform(_ expected: UUID, images: Bool, _ operation: () throws -> Void) throws {
        try lock.withLock {
            guard !Task.isCancelled, (images ? image : lyric) == expected,
                  (images ? imageClears : lyricClears) == 0 else { throw CancellationError() }
            try operation()
        }
    }
    func beginClear(images: Bool, lyrics: Bool) {
        lock.withLock {
            if images { image = UUID(); imageClears += 1 }
            if lyrics { lyric = UUID(); lyricClears += 1 }
        }
    }
    func endClear(images: Bool, lyrics: Bool) {
        lock.withLock {
            if images { imageClears -= 1 }
            if lyrics { lyricClears -= 1 }
        }
    }
}

/// URL-indexed original image files and typed lyric lines/words in the evictable cache database.
/// Reads select one resource; playing a song only touches its two indexed timestamps.
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
        try directory.recover(referenced: Set(try db.query("SELECT filename FROM cached_images") { try $0.text(0) }))
        images = directory
        return directory
    }
    private func removeImage(_ filename: String, db: SQLiteConnection, directory: RawResourceDirectory) throws {
        try db.execute("UPDATE cached_images SET ready=0 WHERE filename=?", [.text(filename)])
        do {
            try directory.remove(filename)
            try db.execute("DELETE FROM cached_images WHERE filename=?", [.text(filename)])
        } catch {
            // An undeleted leaf remains charged and ineligible.
        }
    }
    private func makeRoom(_ db: SQLiteConnection, directory: RawResourceDirectory, incoming: Int, at: Date,
                          preserving resource: String? = nil) throws {
        let cutoff = SQLiteValue.epochMillis(at.addingTimeInterval(-Self.lifetime))
        let stale = try db.query("SELECT filename, resource_key FROM cached_images WHERE updated_at_ms<=? OR ready=0",
                                 [cutoff]) { (try $0.text(0), try $0.text(1)) }
        for (filename, res) in stale where res != resource {
            try removeImage(filename, db: db, directory: directory)
        }
        try db.execute("DELETE FROM cached_music_lyrics WHERE updated_at_ms<=?", [cutoff])
        var size = try db.queryOne("SELECT (SELECT coalesce(sum(bytes),0) FROM cached_images)+" +
            "(SELECT coalesce(sum(stored_bytes),0) FROM cached_music_lyrics)") { try $0.integer(0) } ?? 0
        if size + incoming > Self.capacity {
            let readyImages = try db.query("SELECT filename, resource_key, bytes FROM cached_images WHERE ready=1 ORDER BY updated_at_ms, resource_key") {
                (try $0.text(0), try $0.text(1), try $0.integer(2))
            }
            for (filename, res, bytes) in readyImages {
                guard size + incoming > Self.capacity else { break }
                guard res != resource else { continue }
                try removeImage(filename, db: db, directory: directory)
                if try db.queryOne("SELECT 1 FROM cached_images WHERE filename=?", [.text(filename)], map: { _ in true }) == nil {
                    size -= bytes
                }
            }
        }
        if size + incoming > Self.capacity {
            let lyricRows = try db.query("SELECT song_id, stored_bytes FROM cached_music_lyrics ORDER BY updated_at_ms, song_id") {
                (try $0.text(0), try $0.integer(1))
            }
            for (songID, storedBytes) in lyricRows {
                guard size + incoming > Self.capacity else { break }
                try db.execute("DELETE FROM cached_music_lyrics WHERE song_id=?", [.text(songID)])
                size -= storedBytes
            }
        }
        guard size + incoming <= Self.capacity else { throw SQLiteStorageError.full }
    }
    public func read(_ url: URL) -> ArtworkBytes? {
        let expected = revision
        guard !closed, imagesReadable, !Task.isCancelled, revisions.permits(expected, images: true) else { return nil }
        do {
            let value: ArtworkBytes? = try database.readSync { db in
                let now = clock()
                guard let row = try db.queryOne(
                    "SELECT filename, bytes, updated_at_ms FROM cached_images WHERE resource_key=? AND ready=1 AND updated_at_ms>? AND updated_at_ms<=?",
                    [.text(url.absoluteString), .epochMillis(now.addingTimeInterval(-Self.lifetime)), .epochMillis(now)],
                    map: { (try $0.text(0), try $0.integer(1), try $0.dateFromMillis(2)) }
                ),
                      let data = try files(db).read(row.0, maximumBytes: 4 * 1024 * 1024),
                      data.count == row.1 else { return nil }
                return .init(data: data, expiresAt: row.2.addingTimeInterval(Self.lifetime))
            }
            return !Task.isCancelled && revisions.permits(expected, images: true) ? value : nil
        } catch { return nil }
    }
    public func store(_ data: Data, for url: URL, revision expected: UUID) -> ArtworkBytes? {
        guard !closed, !Task.isCancelled, revisions.permits(expected, images: true), !data.isEmpty, data.count <= 4 * 1024 * 1024,
              MusicArtworkURL.normalized(url.absoluteString) == url else { return nil }
        let at = clock()
        var created: String?
        do {
            try database.writeSync { db in
                guard revisions.permits(expected, images: true) else { throw CancellationError() }
                if !imagesReadable {
                    try SQLiteDatabase.transaction(db) { try $0.execute("UPDATE cached_images SET ready=0") }
                    imagesReadable = true
                }
                let directory = try files(db)
                try SQLiteDatabase.transaction(db, commit: { action in
                    try revisions.perform(expected, images: true, action)
                }) { db in
                    try makeRoom(db, directory: directory, incoming: data.count, at: at, preserving: url.absoluteString)
                    let filename = try directory.write(data); created = filename
                    try db.execute("UPDATE cached_images SET ready=0 WHERE resource_key=?", [.text(url.absoluteString)])
                    try db.execute("INSERT INTO cached_images(resource_key,filename,bytes,ready,updated_at_ms) VALUES(?,?,?,1,?)",
                        [.text(url.absoluteString), .text(filename), .integer(data.count), .epochMillis(at)])
                }
                created = nil
                for filename in try db.query("SELECT filename FROM cached_images WHERE resource_key=? AND ready=0", [.text(url.absoluteString)], map: { try $0.text(0) }) {
                    try SQLiteDatabase.transaction(db) { try removeImage(filename, db: $0, directory: directory) }
                }
            }
        } catch {
            if let created {
                do { try database.writeSync { _ in try images?.remove(created) } }
                catch { images = nil }
            } else { images = nil }
            guard !Task.isCancelled, revisions.permits(expected, images: true) else { return nil }
            return .init(data: data, expiresAt: at.addingTimeInterval(Self.lifetime))
        }
        guard !Task.isCancelled, revisions.permits(expected, images: true) else { return nil }
        return .init(data: data, expiresAt: at.addingTimeInterval(Self.lifetime))
    }
    public func lyrics(songID: String, authorization: MusicQueueAuthorization) -> MusicLyricDisplaySnapshot? {
        let expected = lyricRevision
        guard !closed, lyricsReadable, !Task.isCancelled, authorization.isActive,
              revisions.permits(expected, images: false) else { return nil }
        do {
            let value = try database.readSync { db in
                try SQLiteDatabase.readSnapshot(db) { db -> MusicLyricDisplaySnapshot? in
                    let now = clock()
                    guard let sourceKey = try db.queryOne(
                        "SELECT source_key FROM cached_music_lyrics WHERE song_id=? AND updated_at_ms>? AND updated_at_ms<=?",
                        [.text(songID), .epochMillis(now.addingTimeInterval(-Self.lifetime)), .epochMillis(now)],
                        map: { try $0.text(0) }
                    ) else { return nil }
                    var grouped: [Int: [MusicLyricWord]] = [:]
                    try db.scan(
                        "SELECT line_position, time_sec, duration_sec, text FROM cached_music_lyric_words WHERE song_id=? ORDER BY line_position, position",
                        [.text(songID)]
                    ) { cursor in
                        let line = try cursor.integer(0)
                        grouped[line, default: []].append(.init(
                            time: try cursor.double(1),
                            duration: try cursor.double(2),
                            text: try cursor.text(3)
                        ))
                    }
                    var lines: [MusicLyricLine] = []
                    try db.scan(
                        "SELECT position, line_index, time_sec, end_sec, text, translation FROM cached_music_lyric_lines WHERE song_id=? ORDER BY position",
                        [.text(songID)]
                    ) { cursor in
                        let position = try cursor.integer(0)
                        let time = try cursor.double(2)
                        let end = try cursor.optionalDouble(3) ?? time
                        let words = grouped[position] ?? []
                        lines.append(.init(
                            index: try cursor.integer(1),
                            time: time,
                            end: end,
                            text: try cursor.text(4),
                            translation: try cursor.optionalText(5) ?? "",
                            words: words
                        ))
                    }
                    guard !lines.isEmpty, Self.usableLyrics(lines) else { return nil }
                    return .init(songID: songID, sourceKey: sourceKey, lines: lines)
                }
            }
            return try authorization.perform { !Task.isCancelled && revisions.permits(expected, images: false) ? value : nil }
        } catch { return nil }
    }
    public func storeLyrics(_ lines: [MusicLyricLine], sourceKey: String, songID: String,
                            revision expected: UUID, authorization: MusicQueueAuthorization) {
        guard !closed, !Task.isCancelled, revisions.permits(expected, images: false), authorization.isActive, !lines.isEmpty,
              Self.usableLyrics(lines) else { return }
        let bytes = lines.reduce(songID.utf8.count + sourceKey.utf8.count) {
            $0 + 96 + $1.text.utf8.count + $1.translation.utf8.count + $1.words.reduce(0) { $0 + 48 + $1.text.utf8.count }
        }
        guard bytes <= 1024 * 1024 else { return }
        do {
            try database.writeSync { db in
                guard revisions.permits(expected, images: false) else { throw CancellationError() }
                if !lyricsReadable {
                    try SQLiteDatabase.transaction(db) { try $0.execute("DELETE FROM cached_music_lyrics") }
                    lyricsReadable = true
                }
                let at = clock()
                try SQLiteDatabase.transaction(db, commit: { action in
                    try authorization.perform { try revisions.perform(expected, images: false, action) }
                }) { db in
                    try db.execute("DELETE FROM cached_music_lyrics WHERE song_id=?", [.text(songID)])
                    try makeRoom(db, directory: files(db), incoming: bytes, at: at)
                    try db.execute(
                        "INSERT INTO cached_music_lyrics(song_id, source_key, updated_at_ms, stored_bytes) VALUES(?,?,?,?)",
                        [.text(songID), .text(sourceKey), .epochMillis(at), .integer(bytes)]
                    )
                    for (position, line) in lines.enumerated() {
                        try db.execute(
                            "INSERT INTO cached_music_lyric_lines(song_id, position, line_index, time_sec, end_sec, text, translation) VALUES(?,?,?,?,?,?,?)",
                            [.text(songID), .integer(position), .integer(line.index), .real(line.time), .real(line.end), .text(line.text), .text(line.translation)]
                        )
                        for (index, word) in line.words.enumerated() {
                            try db.execute(
                                "INSERT INTO cached_music_lyric_words(song_id, line_position, position, time_sec, duration_sec, text) VALUES(?,?,?,?,?,?)",
                                [.text(songID), .integer(position), .integer(index), .real(word.time), .real(word.duration), .text(word.text)]
                            )
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
        guard !closed, !Task.isCancelled, confirmedAt <= clock() else { return }
        let image = revision, lyric = lyricRevision, oldest = clock().addingTimeInterval(-Self.lifetime)
        let confirmedMs = SQLiteValue.epochMillis(confirmedAt)
        let oldestMs = SQLiteValue.epochMillis(oldest)
        try? database.writeSync { db in
            if lyricsReadable, revisions.permits(lyric, images: false) {
                try SQLiteDatabase.transaction(db, commit: { try revisions.perform(lyric, images: false, $0) }) { db in
                    try db.execute("UPDATE cached_music_lyrics SET updated_at_ms=? WHERE song_id=? AND updated_at_ms<? AND updated_at_ms>?",
                        [confirmedMs, .text(songID), confirmedMs, oldestMs])
                }
            }
            if imagesReadable, let artwork, revisions.permits(image, images: true) {
                try SQLiteDatabase.transaction(db, commit: { try revisions.perform(image, images: true, $0) }) { db in
                    try db.execute("UPDATE cached_images SET updated_at_ms=? WHERE resource_key=? AND ready=1 AND updated_at_ms<? AND updated_at_ms>?",
                        [confirmedMs, .text(artwork.absoluteString), confirmedMs, oldestMs])
                }
            }
        }
    }
    public nonisolated func clearImages() async { await clear(images: true, lyrics: false) }
    public nonisolated func clearLyrics() async { await clear(images: false, lyrics: true) }
    public nonisolated func clear() async { await clear(images: true, lyrics: true) }
    private nonisolated func clear(images: Bool, lyrics: Bool) async {
        revisions.beginClear(images: images, lyrics: lyrics)
        defer { revisions.endClear(images: images, lyrics: lyrics) }
        await removeCleared(images: images, lyrics: lyrics)
    }
    private func removeCleared(images: Bool, lyrics: Bool) {
        guard !closed else { return }
        if images { imagesReadable = false }
        if lyrics { lyricsReadable = false }
        do { try database.transactionSync { db in
            if lyrics { try db.execute("DELETE FROM cached_music_lyrics") }
            if images { try db.execute("UPDATE cached_images SET ready=0") }
        }
            if images { imagesReadable = true }
            if lyrics { lyricsReadable = true }
            if images { try database.writeSync { db in
                let directory = try files(db)
                for filename in try db.query("SELECT filename FROM cached_images WHERE ready=0", map: { try $0.text(0) }) {
                    try SQLiteDatabase.transaction(db) { try removeImage(filename, db: $0, directory: directory) }
                }
            } }
        } catch {}
    }
    public func close() {
        closed = true
        revisions.beginClear(images: true, lyrics: true)
        revisions.endClear(images: true, lyrics: true)
        images = nil
    }
}
