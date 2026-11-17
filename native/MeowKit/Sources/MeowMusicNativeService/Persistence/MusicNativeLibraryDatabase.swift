import Foundation
import MeowMusicFeature
import CSQLite
import MeowStorage
import Darwin

public struct MusicNativeLibraryRevision: Sendable, Equatable {
    public let revision: Int
    public let receipt: String
    public init(revision: Int, receipt: String) { self.revision = revision; self.receipt = receipt }
}

public enum MusicNativeLibraryCommitError: Error {
    /// SQLite verified this transaction's receipt after the callback failed.
    case receiptLost
    case outcomeUnknown(before: MusicNativeLibraryRevision, after: MusicNativeLibraryRevision)
}

public struct MusicNativeLibrarySnapshot: Sendable {
    public let revision: Int
    public let receipt: String
    public let areas: [String: Data]
    public var identity: MusicNativeLibraryRevision { .init(revision: revision, receipt: receipt) }
    public init(revision: Int, receipt: String, areas: [String: Data]) {
        self.revision = revision; self.receipt = receipt; self.areas = areas
    }
}

public protocol MusicNativeLibraryReading: Sendable {
    func readSnapshot() throws -> MusicNativeLibrarySnapshot
    func songData(id: String) throws -> Data?
    func containsSong(id: String) throws -> Bool
    func sourceOwner(key: String) throws -> String?
    func candidateIDs(lookupKey: String) throws -> [String]
    func querySongIDs(query: String) throws -> [String]
    func allSongIDs() throws -> [String]
}

public protocol MusicNativeLibraryWriting: MusicNativeLibraryReading {
    var changedSongIDs: Set<String> { get }
    func saveSong(_ data: Data) throws
    func saveArea(_ area: String, data: Data) throws
}

public protocol MusicNativeLibraryStoring: MusicNativeLibraryReading {
    /// The candidate revision is visible to reads inside `commit`; its action
    /// commits that exact transaction before returning.
    func withTransaction<T>(commit: (_ action: () throws -> Void) throws -> Void, _ body: (any MusicNativeLibraryWriting) throws -> T) throws -> T
    func close() throws
}

/// Passive until admitted by the native service. Only the new SQLite file is
/// opened; ordinary documents, including the retired library, are not inspected.
public extension MusicNativeLibraryStoring {
    func withTransaction<T>(_ body: (any MusicNativeLibraryWriting) throws -> T) throws -> T {
        try withTransaction(commit: { try $0() }, body)
    }
}

public final class MusicNativeLibraryDatabase: MusicNativeLibraryStoring, @unchecked Sendable {
    public static let filename = "music-library-v1.sqlite3"
    private let lock = NSRecursiveLock()
    private let location: @Sendable () throws -> URL
    private var connection: OpaquePointer?
    private var closed = false
    private var transactionActive = false
    private var cachedSnapshot: MusicNativeLibrarySnapshot?
    // Callers resolve trusted system bases before appending owned paths.
    // Never resolve links here: NOFOLLOW must still reject owned path links.
    public init(url: URL) { location = { url } }
    public init(urlFactory: @escaping @Sendable () throws -> URL) { location = urlFactory }
    /// Only for an existing trusted system base, before appending owned paths.
    /// Foundation's resolver may retain /var, which SQLite NOFOLLOW rejects.
    public static func canonicalSystemBaseURL(_ base: URL) throws -> URL {
        guard base.isFileURL, !base.path.utf8.contains(0),
              let path = base.path.withCString({ Darwin.realpath($0, nil) }) else {
            throw MusicServiceError.storageFailed
        }
        defer { Darwin.free(path) }
        return URL(fileURLWithPath: String(cString: path), isDirectory: true)
    }
    deinit { if let connection { sqlite3_close_v2(connection) } }

    private func database() throws -> OpaquePointer {
        try musicRequire(!closed, .closed)
        if let connection { return connection }
        let url: URL
        do {
            url = try location()
            try musicRequire(url.isFileURL && !url.path.utf8.contains(0), .storageFailed)
            let directory = url.deletingLastPathComponent()
            var attributes: [FileAttributeKey: Any] = [.posixPermissions: 0o700]
            #if os(iOS)
            attributes[.protectionKey] = FileProtectionType.completeUntilFirstUserAuthentication
            #endif
            try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true, attributes: attributes)
            try musicRequire(try directory.resourceValues(forKeys: [.isSymbolicLinkKey]).isSymbolicLink == false, .storageFailed)
            #if os(iOS)
            let protection = try FileManager.default.attributesOfItem(atPath: directory.path)[.protectionKey]
            try musicRequire(NativeFileProtectionVerification.inspect(protection) != .rejected, .storageFailed)
            #endif
        } catch { throw (error as? MusicServiceError) ?? MusicServiceError.storageFailed }
        var db: OpaquePointer?
        let result = sqlite3_open_v2(url.path, &db, SQLITE_OPEN_READWRITE | SQLITE_OPEN_CREATE | SQLITE_OPEN_FULLMUTEX | SQLITE_OPEN_NOFOLLOW, nil)
        guard result == SQLITE_OK, let db else { if let db { sqlite3_close_v2(db) }; throw MusicServiceError.storageFailed }
        connection = db
        do {
            sqlite3_busy_timeout(db, 5_000)
            try execute("PRAGMA foreign_keys=ON")
            let version = try integers("PRAGMA user_version").first ?? 0
            if version == 0 {
                // An unknown populated file is not a new empty database.
                try musicRequire(try strings("SELECT name FROM sqlite_master WHERE type='table'").isEmpty, .corruptDocument)
                try execute("BEGIN IMMEDIATE")
                do {
                    try execute("CREATE TABLE state (id INTEGER PRIMARY KEY CHECK(id=1), revision INTEGER NOT NULL, receipt TEXT NOT NULL)")
                    try execute("INSERT INTO state VALUES(1,0,'')")
                    try execute("CREATE TABLE areas (name TEXT PRIMARY KEY, data TEXT NOT NULL)")
                    try execute("CREATE TABLE songs (id TEXT PRIMARY KEY, lookup_key TEXT, search_query TEXT NOT NULL, data TEXT NOT NULL)")
                    try execute("CREATE INDEX songs_lookup ON songs(lookup_key)")
                    try execute("CREATE INDEX songs_query ON songs(search_query)")
                    try execute("CREATE TABLE song_sources (source_key TEXT PRIMARY KEY, song_id TEXT NOT NULL REFERENCES songs(id), position INTEGER NOT NULL, data TEXT NOT NULL, UNIQUE(song_id,position))")
                    try execute("CREATE INDEX song_sources_owner ON song_sources(song_id,position)")
                    try execute("PRAGMA user_version=1")
                    try execute("COMMIT")
                } catch { try? execute("ROLLBACK"); throw error }
            } else { try musicRequire(version == 1, .corruptDocument) }
            try musicRequire(try strings("PRAGMA journal_mode=WAL").first == "wal", .storageFailed)
            try execute("PRAGMA synchronous=FULL")
            try musicRequire(try integers("PRAGMA foreign_keys").first == 1 && integers("PRAGMA synchronous").first == 2, .storageFailed)
            // Verify the current schema and state, never repair unknown input.
            _ = try snapshotUnlocked()
            _ = try strings("SELECT id FROM songs LIMIT 0")
            _ = try strings("SELECT source_key FROM song_sources LIMIT 0")
            return db
        } catch {
            connection = nil; sqlite3_close_v2(db)
            throw (error as? MusicServiceError) ?? MusicServiceError.storageFailed
        }
    }
    private var transient: sqlite3_destructor_type { unsafeBitCast(-1, to: sqlite3_destructor_type.self) }
    private func statement(_ sql: String, _ values: [Data?] = []) throws -> OpaquePointer {
        let db = try database()
        var stmt: OpaquePointer?
        guard sqlite3_prepare_v2(db, sql, -1, &stmt, nil) == SQLITE_OK, let stmt else { throw MusicServiceError.storageFailed }
        for (offset, value) in values.enumerated() {
            let code: Int32
            if let value {
                let buffer = value.isEmpty ? Data([0]) : value
                code = buffer.withUnsafeBytes { sqlite3_bind_text(stmt, Int32(offset + 1), $0.baseAddress?.assumingMemoryBound(to: CChar.self), Int32(value.count), transient) }
            } else { code = sqlite3_bind_null(stmt, Int32(offset + 1)) }
            if code != SQLITE_OK { sqlite3_finalize(stmt); throw MusicServiceError.storageFailed }
        }
        return stmt
    }
    private func execute(_ sql: String, _ values: [Data?] = []) throws {
        let stmt = try statement(sql, values); defer { sqlite3_finalize(stmt) }
        let code = sqlite3_step(stmt)
        try musicRequire(code == SQLITE_DONE || code == SQLITE_ROW, code == SQLITE_CONSTRAINT ? .nativeIDConflict : .storageFailed)
    }
    private func bytes(_ stmt: OpaquePointer, _ column: Int32) -> Data {
        let count = Int(sqlite3_column_bytes(stmt, column))
        guard count > 0, let pointer = sqlite3_column_blob(stmt, column) else { return Data() }
        return Data(bytes: pointer, count: count)
    }
    private func strings(_ sql: String, _ values: [Data?] = []) throws -> [String] {
        let stmt = try statement(sql, values); defer { sqlite3_finalize(stmt) }
        var result: [String] = []
        while true {
            let code = sqlite3_step(stmt)
            if code == SQLITE_DONE { return result }
            try musicRequire(code == SQLITE_ROW, .storageFailed)
            guard let text = String(data: bytes(stmt, 0), encoding: .utf8) else { throw MusicServiceError.corruptDocument }
            result.append(text)
        }
    }
    private func integers(_ sql: String) throws -> [Int] {
        let stmt = try statement(sql); defer { sqlite3_finalize(stmt) }
        var result: [Int] = []
        while true {
            let code = sqlite3_step(stmt)
            if code == SQLITE_DONE { return result }
            try musicRequire(code == SQLITE_ROW, .storageFailed)
            result.append(Int(sqlite3_column_int64(stmt, 0)))
        }
    }
    private func revisionUnlocked() throws -> MusicNativeLibraryRevision {
        let stmt = try statement("SELECT revision,receipt FROM state WHERE id=1")
        defer { sqlite3_finalize(stmt) }
        try musicRequire(sqlite3_step(stmt) == SQLITE_ROW, .corruptDocument)
        let revision = Int(sqlite3_column_int64(stmt, 0))
        guard let receipt = String(data: bytes(stmt, 1), encoding: .utf8) else { throw MusicServiceError.corruptDocument }
        try musicRequire((0...9_007_199_254_740_991).contains(revision) &&
            (revision == 0 ? receipt.isEmpty : UUID(uuidString: receipt) != nil), .corruptDocument)
        try musicRequire(sqlite3_step(stmt) == SQLITE_DONE, .corruptDocument)
        return .init(revision: revision, receipt: receipt)
    }
    private func snapshotUnlocked() throws -> MusicNativeLibrarySnapshot {
        let revision = try revisionUnlocked()
        let stmt = try statement("SELECT name,data FROM areas"); defer { sqlite3_finalize(stmt) }
        var areas: [String: Data] = [:]
        while true {
            let code = sqlite3_step(stmt)
            if code == SQLITE_DONE { return .init(revision: revision.revision, receipt: revision.receipt, areas: areas) }
            try musicRequire(code == SQLITE_ROW, .storageFailed)
            guard let name = String(data: bytes(stmt, 0), encoding: .utf8), MusicDocument.areas.contains(name) else { throw MusicServiceError.corruptDocument }
            areas[name] = bytes(stmt, 1)
        }
    }
    public func readSnapshot() throws -> MusicNativeLibrarySnapshot {
        lock.lock(); defer { lock.unlock() }
        _ = try database()
        if transactionActive { return try snapshotUnlocked() }
        try execute("BEGIN")
        do {
            let revision = try revisionUnlocked()
            let value: MusicNativeLibrarySnapshot
            if let cachedSnapshot, cachedSnapshot.identity == revision { value = cachedSnapshot }
            else { value = try snapshotUnlocked(); cachedSnapshot = value }
            try execute("COMMIT"); return value
        }
        catch { try? execute("ROLLBACK"); throw error }
    }
    public func songData(id: String) throws -> Data? {
        lock.lock(); defer { lock.unlock() }
        let stmt = try statement("SELECT data FROM songs WHERE id=?", [Data(id.utf8)]); defer { sqlite3_finalize(stmt) }
        let code = sqlite3_step(stmt)
        if code == SQLITE_DONE { return nil }
        try musicRequire(code == SQLITE_ROW, .storageFailed)
        var song = try MJ.parse(bytes(stmt, 0))
        let sources = try statement("SELECT data FROM song_sources WHERE song_id=? ORDER BY position", [Data(id.utf8)])
        defer { sqlite3_finalize(sources) }
        var rows: [MJ] = []
        while true {
            let code = sqlite3_step(sources)
            if code == SQLITE_DONE { break }
            try musicRequire(code == SQLITE_ROW, .storageFailed)
            rows.append(try MJ.parse(bytes(sources, 0)))
        }
        song["sources"] = .array(rows)
        try MusicLibraryValidation.validateSong(song, id: id)
        return song.bytes
    }
    public func containsSong(id: String) throws -> Bool {
        lock.lock(); defer { lock.unlock() }
        return try !strings("SELECT id FROM songs WHERE id=?", [Data(id.utf8)]).isEmpty
    }
    public func sourceOwner(key: String) throws -> String? {
        lock.lock(); defer { lock.unlock() }
        return try strings("SELECT song_id FROM song_sources WHERE source_key=?", [Data(key.utf8)]).first
    }
    public func candidateIDs(lookupKey: String) throws -> [String] {
        lock.lock(); defer { lock.unlock() }
        return try strings("SELECT id FROM songs WHERE lookup_key=?", [Data(lookupKey.utf8)])
    }
    public func querySongIDs(query: String) throws -> [String] {
        lock.lock(); defer { lock.unlock() }
        return try strings("SELECT id FROM songs WHERE search_query=?", [Data(query.utf8)])
    }
    public func allSongIDs() throws -> [String] {
        lock.lock(); defer { lock.unlock() }
        return try strings("SELECT id FROM songs ORDER BY id")
    }
    public func withTransaction<T>(commit: (_ action: () throws -> Void) throws -> Void, _ body: (any MusicNativeLibraryWriting) throws -> T) throws -> T {
        lock.lock(); defer { lock.unlock() }
        try musicRequire(!transactionActive, .storageConflict)
        try execute("BEGIN IMMEDIATE"); transactionActive = true
        defer { transactionActive = false }
        let before = try revisionUnlocked()
        let writer = Writer(database: self)
        var after: MusicNativeLibraryRevision?
        defer { writer.active = false }
        do {
            let result = try body(writer)
            if writer.changed {
                try musicRequire(before.revision < 9_007_199_254_740_991, .limit)
                let nextReceipt = UUID().uuidString
                after = .init(revision: before.revision + 1, receipt: nextReceipt)
                try execute("UPDATE state SET revision=revision+1,receipt=? WHERE id=1", [Data(nextReceipt.utf8)])
            }
            try commit { try execute("COMMIT") }
            if writer.changed { cachedSnapshot = nil }
            return result
        } catch {
            cachedSnapshot = nil
            if let connection, sqlite3_get_autocommit(connection) != 0 {
                // An adapter may lose a receipt after SQLite completed COMMIT.
                // Durable transaction identity distinguishes this from rollback;
                // the caller must read current facts, never replay the mutation.
                if let after {
                    guard let current = try? revisionUnlocked() else {
                        throw MusicNativeLibraryCommitError.outcomeUnknown(before: before, after: after)
                    }
                    if current == after { throw MusicNativeLibraryCommitError.receiptLost }
                    if current != before {
                        throw MusicNativeLibraryCommitError.outcomeUnknown(before: before, after: after)
                    }
                }
            } else { try? execute("ROLLBACK") }
            throw error
        }
    }
    public func close() throws {
        lock.lock(); defer { lock.unlock() }
        try musicRequire(!transactionActive, .storageConflict)
        if let connection {
            try musicRequire(sqlite3_close_v2(connection) == SQLITE_OK, .storageFailed)
            self.connection = nil
        }
        cachedSnapshot = nil; closed = true
    }
    private final class Writer: MusicNativeLibraryWriting, @unchecked Sendable {
        let database: MusicNativeLibraryDatabase
        var changed = false
        var active = true
        var changedSongIDs: Set<String> = []
        init(database: MusicNativeLibraryDatabase) { self.database = database }
        func readSnapshot() throws -> MusicNativeLibrarySnapshot { try database.readSnapshot() }
        func songData(id: String) throws -> Data? { try database.songData(id: id) }
        func containsSong(id: String) throws -> Bool { try database.containsSong(id: id) }
        func sourceOwner(key: String) throws -> String? { try database.sourceOwner(key: key) }
        func candidateIDs(lookupKey: String) throws -> [String] { try database.candidateIDs(lookupKey: lookupKey) }
        func querySongIDs(query: String) throws -> [String] { try database.querySongIDs(query: query) }
        func allSongIDs() throws -> [String] { try database.allSongIDs() }
        func saveArea(_ area: String, data: Data) throws {
            database.lock.lock(); defer { database.lock.unlock() }
            try musicRequire(active && database.transactionActive, .storageConflict)
            try musicRequire(MusicDocument.areas.contains(area), .invalidRequest)
            let stmt = try database.statement("SELECT data FROM areas WHERE name=?", [Data(area.utf8)])
            defer { sqlite3_finalize(stmt) }
            let code = sqlite3_step(stmt)
            try musicRequire(code == SQLITE_ROW || code == SQLITE_DONE, .storageFailed)
            if code == SQLITE_ROW && database.bytes(stmt, 0) == data { return }
            try database.execute("INSERT INTO areas(name,data) VALUES(?,?) ON CONFLICT(name) DO UPDATE SET data=excluded.data", [Data(area.utf8), data])
            changed = true
        }
        func saveSong(_ data: Data) throws {
            database.lock.lock(); defer { database.lock.unlock() }
            try musicRequire(active && database.transactionActive, .storageConflict)
            let song = try MJ.parse(data)
            let id = song["songId"].string
            try MusicLibraryValidation.validateSong(song, id: id)
            let old = try database.songData(id: id).map { try MJ.parse($0) }
            if old == song { return }
            let sources = song["sources"].array
            let keys = Set(sources.map { $0["sourceKey"].string })
            try musicRequire(old?["sources"].array.allSatisfy { keys.contains($0["sourceKey"].string) } ?? true, .nativeIDConflict)
            if let old, old["baselineDuration"] != .null {
                try musicRequire(old["baselineDuration"] == song["baselineDuration"], .storageConflict)
            }
            for source in sources {
                if let owner = try sourceOwner(key: source["sourceKey"].string) { try musicRequire(owner == id, .nativeIDConflict) }
            }
            var metadata = song; metadata.remove("sources")
            var previousMetadata = old ?? .null; previousMetadata.remove("sources")
            if metadata != previousMetadata {
                let evidence = MusicModel.descriptor(song)
                let query = MusicRecordingIdentity.normalize(song["title"].string + " " + song["artist"].string)
                try database.execute("INSERT INTO songs(id,lookup_key,search_query,data) VALUES(?,?,?,?) ON CONFLICT(id) DO UPDATE SET lookup_key=excluded.lookup_key,search_query=excluded.search_query,data=excluded.data", [Data(id.utf8), evidence.lookupKey.map { Data($0.utf8) }, Data(query.utf8), metadata.bytes])
            }
            if sources != old?["sources"].array {
                // Only the changed song's ordered sources are maintained.
                // A title/album/artwork selection update never rewrites sources.
                try database.execute("DELETE FROM song_sources WHERE song_id=?", [Data(id.utf8)])
                for (position, source) in sources.enumerated() {
                    try database.execute("INSERT INTO song_sources(source_key,song_id,position,data) VALUES(?,?,CAST(? AS INTEGER),?)", [Data(source["sourceKey"].string.utf8), Data(id.utf8), Data(String(position).utf8), source.bytes])
                }
            }
            changed = true; changedSongIDs.insert(id)
        }
    }
}
