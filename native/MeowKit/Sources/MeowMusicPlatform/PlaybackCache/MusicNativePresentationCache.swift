import Foundation
import MeowDebug
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
/// Aligned across iOS and Windows: 128 MiB shared budget, 30-day TTL, 4 MiB image cap, 1 MiB lyric cap.
/// Disk I/O always executes outside SQLite write transactions.
public actor MusicNativePresentationCache: MusicPresentationCaching {
    public static let lifetime: TimeInterval = 30 * 86_400
    public static let capacityBytes: Int64 = 128 * 1024 * 1024
    public static let maximumImageBytes = 4 * 1024 * 1024
    public static let maximumLyricBytes = 1024 * 1024

    private nonisolated let revisions = PresentationCacheRevisions()
    public nonisolated var revision: UUID { revisions.value(images: true) }
    public nonisolated var lyricRevision: UUID { revisions.value(images: false) }

    private let database: SQLiteDatabase
    private let clock: @Sendable () -> Date
    private var directory: RawResourceDirectory?
    private var recovered = false
    private var closed = false

    public init(database: SQLiteDatabase, clock: @escaping @Sendable () -> Date = { Date() }) {
        self.database = database
        self.clock = clock
    }

    private func ensureDirectory() throws -> RawResourceDirectory {
        if let directory { return directory }
        let created = try RawResourceDirectory(dataRoot: database.dataRoot, namespace: "music-images")
        directory = created
        return created
    }

    private func recoverOnceIfNeeded(_ dir: RawResourceDirectory) {
        guard !recovered else { return }
        recovered = true
        do {
            let referenced = try database.readSync { db in
                Set(try db.query("SELECT filename FROM cached_images") { try $0.text(0) })
            }
            try dir.recover(referenced: referenced)
        } catch {
            recovered = false
        }
    }

    private func pruneInTransaction(_ db: SQLiteConnection, incoming: Int, at: Date) throws -> [String] {
        let cutoff = SQLiteValue.epochMillis(at.addingTimeInterval(-Self.lifetime))
        let nowMs = SQLiteValue.epochMillis(at)
        var evictedFiles = try db.query(
            "SELECT filename FROM cached_images WHERE updated_at_ms<=? OR updated_at_ms>?",
            [cutoff, nowMs]
        ) { try $0.text(0) }
        try db.execute("DELETE FROM cached_images WHERE updated_at_ms<=? OR updated_at_ms>?", [cutoff, nowMs])
        try db.execute("DELETE FROM cached_music_lyrics WHERE updated_at_ms<=? OR updated_at_ms>?", [cutoff, nowMs])

        var total = try db.queryOne(
            "SELECT (SELECT coalesce(sum(bytes),0) FROM cached_images)+" +
            "(SELECT coalesce(sum(stored_bytes),0) FROM cached_music_lyrics)"
        ) { try $0.int64(0) } ?? 0

        let incoming64 = Int64(incoming)
        if total + incoming64 > Self.capacityBytes {
            let candidates = try db.query("""
                SELECT 'image', resource_key, filename, updated_at_ms, bytes FROM cached_images
                UNION ALL
                SELECT 'lyrics', song_id, '', updated_at_ms, stored_bytes FROM cached_music_lyrics
                ORDER BY 4 ASC, 2 ASC, 1 ASC
                """) {
                (kind: try $0.text(0), key: try $0.text(1), file: try $0.text(2), bytes: try $0.int64(4))
            }
            for item in candidates {
                guard total + incoming64 > Self.capacityBytes else { break }
                if item.kind == "image" {
                    try db.execute("DELETE FROM cached_images WHERE resource_key=?", [.text(item.key)])
                    evictedFiles.append(item.file)
                    total -= item.bytes
                } else {
                    try db.execute("DELETE FROM cached_music_lyrics WHERE song_id=?", [.text(item.key)])
                    total -= item.bytes
                }
            }
        }
        guard total + incoming64 <= Self.capacityBytes else { throw SQLiteStorageError.full }
        return evictedFiles
    }

    public func read(_ url: URL) -> ArtworkBytes? {
        let expected = revision
        guard !closed, !Task.isCancelled, revisions.permits(expected, images: true) else { return nil }
        let row: (filename: String, bytes: Int, updatedAt: Date)?
        do {
            row = try database.readSync { db in
                let now = clock()
                return try db.queryOne(
                    "SELECT filename, bytes, updated_at_ms FROM cached_images WHERE resource_key=? AND updated_at_ms>? AND updated_at_ms<=?",
                    [.text(url.absoluteString), .epochMillis(now.addingTimeInterval(-Self.lifetime)), .epochMillis(now)],
                    map: { (try $0.text(0), try $0.integer(1), try $0.dateFromMillis(2)) }
                )
            }
        } catch {
            return nil
        }
        guard let row, !Task.isCancelled, revisions.permits(expected, images: true) else { return nil }
        guard let dir = try? ensureDirectory() else { return nil }
        guard let data = try? dir.read(row.filename, maximumBytes: Self.maximumImageBytes),
              data.count == row.bytes else {
            // Self-heal broken/missing disk entry without blocking callers.
            try? dir.remove(row.filename)
            _ = try? database.transactionSync { db in
                try db.execute("DELETE FROM cached_images WHERE filename=?", [.text(row.filename)])
            }
            return nil
        }
        guard !Task.isCancelled, revisions.permits(expected, images: true) else { return nil }
        return ArtworkBytes(data: data, expiresAt: row.updatedAt.addingTimeInterval(Self.lifetime))
    }

    public func store(_ data: Data, for url: URL, revision expected: UUID) -> ArtworkBytes? {
        guard !closed, !Task.isCancelled, revisions.permits(expected, images: true),
              !data.isEmpty, data.count <= Self.maximumImageBytes,
              MusicArtworkURL.normalized(url.absoluteString) == url else { return nil }
        let at = clock()
        let dir: RawResourceDirectory
        do {
            dir = try ensureDirectory()
        } catch {
            return ArtworkBytes(data: data, expiresAt: at.addingTimeInterval(Self.lifetime))
        }
        recoverOnceIfNeeded(dir)

        // Stage and commit file outside the SQLite write transaction.
        let newFilename: String
        do {
            newFilename = try dir.write(data)
        } catch {
            guard !Task.isCancelled, revisions.permits(expected, images: true) else { return nil }
            return ArtworkBytes(data: data, expiresAt: at.addingTimeInterval(Self.lifetime))
        }

        var filesToDelete: [String] = []
        do {
            try database.writeSync { db in
                guard revisions.permits(expected, images: true) else { throw CancellationError() }
                try SQLiteDatabase.transaction(db, commit: { action in
                    try revisions.perform(expected, images: true, action)
                }) { db in
                    let previous = try db.query(
                        "SELECT filename FROM cached_images WHERE resource_key=?",
                        [.text(url.absoluteString)]
                    ) { try $0.text(0) }
                    try db.execute("DELETE FROM cached_images WHERE resource_key=?", [.text(url.absoluteString)])
                    let evicted = try pruneInTransaction(db, incoming: data.count, at: at)
                    try db.execute(
                        "INSERT INTO cached_images(resource_key,filename,bytes,updated_at_ms) VALUES(?,?,?,?)",
                        [.text(url.absoluteString), .text(newFilename), .integer(data.count), .epochMillis(at)]
                    )
                    filesToDelete = previous + evicted
                }
            }
        } catch {
            try? dir.remove(newFilename)
            guard !Task.isCancelled, revisions.permits(expected, images: true) else { return nil }
            return ArtworkBytes(data: data, expiresAt: at.addingTimeInterval(Self.lifetime))
        }

        // Delete replaced/evicted files outside the SQLite write transaction.
        for filename in Set(filesToDelete) where filename != newFilename {
            try? dir.remove(filename)
        }
        guard !Task.isCancelled, revisions.permits(expected, images: true) else { return nil }
        return ArtworkBytes(data: data, expiresAt: at.addingTimeInterval(Self.lifetime))
    }

    public func remove(_ url: URL, revision expected: UUID) async {
        guard !closed, revisions.permits(expected, images: true) else { return }
        var removedFiles: [String] = []
        do {
            try database.writeSync { db in
                try SQLiteDatabase.transaction(db, commit: { action in
                    try revisions.perform(expected, images: true, action)
                }) { db in
                    removedFiles = try db.query(
                        "SELECT filename FROM cached_images WHERE resource_key=?",
                        [.text(url.absoluteString)]
                    ) { try $0.text(0) }
                    try db.execute("DELETE FROM cached_images WHERE resource_key=?", [.text(url.absoluteString)])
                }
            }
        } catch {
            return
        }
        if let dir = try? ensureDirectory() {
            for filename in removedFiles {
                try? dir.remove(filename)
            }
        }
    }

    public func lyrics(songID: String, authorization: MusicQueueAuthorization) -> MusicLyricDisplaySnapshot? {
        let expected = lyricRevision
        guard !closed, !Task.isCancelled, authorization.isActive,
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
        guard !closed, !Task.isCancelled, revisions.permits(expected, images: false), authorization.isActive,
              !lines.isEmpty, Self.usableLyrics(lines) else { return }
        let bytes = lines.reduce(songID.utf8.count + sourceKey.utf8.count) {
            $0 + 96 + $1.text.utf8.count + $1.translation.utf8.count + $1.words.reduce(0) { $0 + 48 + $1.text.utf8.count }
        }
        guard bytes <= Self.maximumLyricBytes else { return }

        var evictedFiles: [String] = []
        do {
            try database.writeSync { db in
                guard revisions.permits(expected, images: false) else { throw CancellationError() }
                let at = clock()
                try SQLiteDatabase.transaction(db, commit: { action in
                    try authorization.perform { try revisions.perform(expected, images: false, action) }
                }) { db in
                    try db.execute("DELETE FROM cached_music_lyrics WHERE song_id=?", [.text(songID)])
                    evictedFiles = try pruneInTransaction(db, incoming: bytes, at: at)
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
        } catch {
            return
        }
        if !evictedFiles.isEmpty, let dir = try? ensureDirectory() {
            for filename in evictedFiles { try? dir.remove(filename) }
        }
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
            if revisions.permits(lyric, images: false) {
                try SQLiteDatabase.transaction(db, commit: { try revisions.perform(lyric, images: false, $0) }) { db in
                    try db.execute("UPDATE cached_music_lyrics SET updated_at_ms=? WHERE song_id=? AND updated_at_ms<? AND updated_at_ms>?",
                        [confirmedMs, .text(songID), confirmedMs, oldestMs])
                }
            }
            if let artwork, revisions.permits(image, images: true) {
                try SQLiteDatabase.transaction(db, commit: { try revisions.perform(image, images: true, $0) }) { db in
                    try db.execute("UPDATE cached_images SET updated_at_ms=? WHERE resource_key=? AND updated_at_ms<? AND updated_at_ms>?",
                        [confirmedMs, .text(artwork.absoluteString), confirmedMs, oldestMs])
                }
            }
        }
    }

    public nonisolated func clearImages() async throws { try await clear(images: true, lyrics: false) }
    public nonisolated func clearLyrics() async throws { try await clear(images: false, lyrics: true) }
    public nonisolated func clear() async throws { try await clear(images: true, lyrics: true) }

    private nonisolated func clear(images: Bool, lyrics: Bool) async throws {
        revisions.beginClear(images: images, lyrics: lyrics)
        defer { revisions.endClear(images: images, lyrics: lyrics) }
        try await removeCleared(images: images, lyrics: lyrics)
    }

    private func removeCleared(images: Bool, lyrics: Bool) throws {
        guard !closed else { return }
        try database.transactionSync { db in
            if lyrics { try db.execute("DELETE FROM cached_music_lyrics") }
            if images { try db.execute("DELETE FROM cached_images") }
        }
        if images {
            let dir = try ensureDirectory()
            try dir.recover(referenced: [], strict: true)
            recovered = true
        }
    }

    public func stats() -> MusicPresentationCacheStats {
        if closed {
            return MusicPresentationCacheStats(
                imageCount: 0,
                imageBytes: 0,
                lyricCount: 0,
                lyricBytes: 0,
                totalBytes: 0,
                capacityBytes: Self.capacityBytes,
                diskFileCount: 0,
                diskBytes: 0,
                closed: true
            )
        }
        let dbStats: (Int, Int64, Int, Int64) = (try? database.readSync { db in
            let imageRow = try db.queryOne(
                "SELECT count(*), coalesce(sum(bytes), 0) FROM cached_images"
            ) { (try $0.integer(0), try $0.int64(1)) } ?? (0, 0)
            let lyricRow = try db.queryOne(
                "SELECT count(*), coalesce(sum(stored_bytes), 0) FROM cached_music_lyrics"
            ) { (try $0.integer(0), try $0.int64(1)) } ?? (0, 0)
            return (imageRow.0, imageRow.1, lyricRow.0, lyricRow.1)
        }) ?? (0, 0, 0, 0)
        let disk = (try? ensureDirectory())?.diskStats() ?? (fileCount: 0, totalBytes: 0)
        return MusicPresentationCacheStats(
            imageCount: dbStats.0,
            imageBytes: dbStats.1,
            lyricCount: dbStats.2,
            lyricBytes: dbStats.3,
            totalBytes: dbStats.1 + dbStats.3,
            capacityBytes: Self.capacityBytes,
            diskFileCount: disk.fileCount,
            diskBytes: disk.totalBytes,
            closed: closed
        )
    }

    public func debugStats() -> DebugJSONValue {
        let s = stats()
        return .object([
            "imageCount": .int(s.imageCount),
            "imageBytes": .integer(s.imageBytes),
            "lyricCount": .int(s.lyricCount),
            "lyricBytes": .integer(s.lyricBytes),
            "totalBytes": .integer(s.totalBytes),
            "capacityBytes": .integer(s.capacityBytes),
            "diskFileCount": .int(s.diskFileCount),
            "diskBytes": .integer(s.diskBytes),
            "closed": .bool(s.closed)
        ])
    }

    public func close() {
        closed = true
        revisions.beginClear(images: true, lyrics: true)
        revisions.endClear(images: true, lyrics: true)
        directory = nil
    }
}
