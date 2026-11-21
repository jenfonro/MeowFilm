import Foundation
import MeowMusicFeature
import MeowStorage

public struct MusicNativeLibraryRevision: Sendable, Equatable {
    public let revision: Int
    public let receipt: String
    public init(revision: Int, receipt: String) { self.revision = revision; self.receipt = receipt }
}

public enum MusicNativeLibraryCommitError: Error {
    case receiptLost
    case outcomeUnknown(before: MusicNativeLibraryRevision, after: MusicNativeLibraryRevision)
}

struct MusicNativeLibrarySnapshot: Sendable {
    let revision: Int
    let receipt: String
    let areas: [String: MJ]
    var identity: MusicNativeLibraryRevision { .init(revision: revision, receipt: receipt) }
    init(revision: Int, receipt: String, areas: [String: MJ]) {
        self.revision = revision; self.receipt = receipt; self.areas = areas
    }
}

protocol MusicNativeLibraryReading: Sendable {
    func readRevision() throws -> MusicNativeLibraryRevision
    func readSnapshot() throws -> MusicNativeLibrarySnapshot
    func song(id: String) throws -> MJ?
    func containsSong(id: String) throws -> Bool
    func sourceOwner(key: String) throws -> String?
    func sourceOwners(keys: Set<String>) throws -> [String: String]
    func candidateIDs(lookupKey: String) throws -> [String]
    func candidateIDs(lookupKeys: Set<String>) throws -> [String: [String]]
    func querySongIDs(query: String) throws -> [String]
    func allSongIDs() throws -> [String]
}
extension MusicNativeLibraryReading {
    func readRevision() throws -> MusicNativeLibraryRevision { try readSnapshot().identity }
    func sourceOwners(keys: Set<String>) throws -> [String: String] {
        var result: [String: String] = [:]
        for key in keys { if let owner = try sourceOwner(key: key) { result[key] = owner } }
        return result
    }
    func candidateIDs(lookupKeys: Set<String>) throws -> [String: [String]] {
        var result: [String: [String]] = [:]
        for key in lookupKeys { result[key] = try candidateIDs(lookupKey: key) }
        return result
    }
}
protocol MusicNativeLibraryWriting: MusicNativeLibraryReading {
    var changedSongIDs: Set<String> { get }
    func saveSong(_ song: MJ) throws
    func saveArea(_ area: String, value: MJ) throws
}
public protocol MusicNativeLibraryStoring: Sendable {
    func close() throws
}
protocol MusicNativeLibraryEngine: MusicNativeLibraryStoring, MusicNativeLibraryReading {
    func withTransaction<T>(commit: (_ action: () throws -> Void) throws -> Void,
                            _ body: (any MusicNativeLibraryWriting) throws -> T) throws -> T
}
extension MusicNativeLibraryEngine {
    func withTransaction<T>(_ body: (any MusicNativeLibraryWriting) throws -> T) throws -> T {
        try withTransaction(commit: { try $0() }, body)
    }
}

/// The music owner borrows the App database's off-main lane. It never opens a
/// second SQLite file or closes the shared connection. Songs, relationships and
/// control columns settle in the same transaction as the exact commit receipt.
public final class MusicNativeLibraryDatabase: MusicNativeLibraryEngine, @unchecked Sendable {
    private let database: SQLiteDatabase
    private let scope: String
    private let stateLock = NSLock()
    private var closed = false
    private var active = false
    private var cachedAreaRevision: MusicNativeLibraryRevision?
    private var cachedAreas: [String: MJ] = [:]

    public init(database: SQLiteDatabase, scope: String = "device") {
        self.database = database; self.scope = scope
    }

    private static func readArea(_ area: String, from db: SQLiteConnection, scope: String) throws -> MJ? {
        switch area {
        case "library": return try MusicLibraryAreaStore.read(db, scope: scope)
        case "queue": return try MusicQueueAreaStore.read(db, scope: scope)
        case "history": return try MusicHistoryAreaStore.read(db, scope: scope)
        case "playback": return try MusicPlaybackAreaStore.read(db, scope: scope)
        case "catalogSource": return try MusicCatalogSourceAreaStore.read(db, scope: scope)
        default: throw MusicServiceError.corruptDocument
        }
    }

    private static func writeArea(_ area: String, to db: SQLiteConnection, scope: String, value: MJ) throws {
        switch area {
        case "library": try MusicLibraryAreaStore.write(db, scope: scope, value: value)
        case "queue": try MusicQueueAreaStore.write(db, scope: scope, value: value)
        case "history": try MusicHistoryAreaStore.write(db, scope: scope, value: value)
        case "playback": try MusicPlaybackAreaStore.write(db, scope: scope, value: value)
        case "catalogSource": try MusicCatalogSourceAreaStore.write(db, scope: scope, value: value)
        default: throw MusicServiceError.invalidRequest
        }
    }

    private func read<T>(_ action: (SQLiteConnection) throws -> T) throws -> T {
        try database.readSync { db in
            try musicRequire(!stateLock.withLock { closed }, .closed)
            return try action(db)
        }
    }
    private func revision(_ db: SQLiteConnection) throws -> MusicNativeLibraryRevision {
        guard let row = try db.query("SELECT revision,receipt FROM music_owner_state WHERE scope=?", [.text(scope)]).first else {
            return .init(revision: 0, receipt: "")
        }
        let revision = try row.integer("revision"), receipt = try row.text("receipt")
        try musicRequire(revision == 0 ? receipt.isEmpty : UUID(uuidString: receipt) != nil, .corruptDocument)
        return .init(revision: revision, receipt: receipt)
    }
    func readRevision() throws -> MusicNativeLibraryRevision { try read(revision) }
    func readSnapshot() throws -> MusicNativeLibrarySnapshot {
        try read { db in
            try SQLiteDatabase.readSnapshot(db) { db in
                let current = try revision(db)
                let cached: [String: MJ]? = stateLock.withLock {
                    !active && cachedAreaRevision == current ? cachedAreas : nil
                }
                if let cached {
                    return MusicNativeLibrarySnapshot(revision: current.revision, receipt: current.receipt, areas: cached)
                }
                var values: [String: MJ] = [:]
                for name in MusicDocument.areas {
                    if let value = try Self.readArea(name, from: db, scope: scope) {
                        values[name] = value
                    }
                }
                stateLock.withLock {
                    if !active && !closed && (cachedAreaRevision == nil || current.revision >= cachedAreaRevision!.revision) {
                        cachedAreas = values
                        cachedAreaRevision = current
                    }
                }
                return MusicNativeLibrarySnapshot(revision: current.revision, receipt: current.receipt, areas: values)
            }
        }
    }
    private func readSong(_ db: SQLiteConnection, id: String) throws -> MJ? {
        guard let value = try MusicSongStore.read(db, id: id) else { return nil }
        try MusicLibraryValidation.validateSong(value, id: id)
        return value
    }
    func song(id: String) throws -> MJ? {
        try read { db in try SQLiteDatabase.readSnapshot(db) { try readSong($0, id: id) } }
    }
    func containsSong(id: String) throws -> Bool {
        try read { try !$0.query("SELECT song_id FROM music_songs WHERE song_id=?", [.text(id)]).isEmpty }
    }
    func sourceOwner(key: String) throws -> String? {
        try read { try $0.query("SELECT song_id FROM music_song_sources WHERE source_key=?", [.text(key)]).first?.text("song_id") }
    }
    func sourceOwners(keys: Set<String>) throws -> [String: String] {
        try read { db in
            var result: [String: String] = [:]
            let values = Array(keys)
            for start in stride(from: 0, to: values.count, by: 500) {
                let batch = Array(values[start..<min(start + 500, values.count)])
                let placeholders = Array(repeating: "?", count: batch.count).joined(separator: ",")
                for row in try db.query("SELECT source_key,song_id FROM music_song_sources WHERE source_key IN (\(placeholders))",
                    batch.map(SQLiteValue.text)) {
                    result[try row.text("source_key")] = try row.text("song_id")
                }
            }
            return result
        }
    }
    func candidateIDs(lookupKey: String) throws -> [String] {
        try read { try $0.query("SELECT song_id FROM music_songs WHERE lookup_key=? ORDER BY song_id", [.text(lookupKey)])
            .map { try $0.text("song_id") } }
    }
    func candidateIDs(lookupKeys: Set<String>) throws -> [String: [String]] {
        try read { db in
            var result: [String: [String]] = [:]
            let values = Array(lookupKeys)
            for start in stride(from: 0, to: values.count, by: 500) {
                let batch = Array(values[start..<min(start + 500, values.count)])
                let placeholders = Array(repeating: "?", count: batch.count).joined(separator: ",")
                try db.rows("SELECT lookup_key,song_id FROM music_songs WHERE lookup_key IN (\(placeholders)) ORDER BY lookup_key,song_id",
                    batch.map(SQLiteValue.text)) { row in
                    result[try row.text("lookup_key"), default: []].append(try row.text("song_id"))
                }
            }
            return result
        }
    }
    func querySongIDs(query: String) throws -> [String] {
        try read { try $0.query("SELECT song_id FROM music_songs WHERE search_query=? ORDER BY song_id", [.text(query)])
            .map { try $0.text("song_id") } }
    }
    func allSongIDs() throws -> [String] {
        try read { try $0.query("SELECT song_id FROM music_songs ORDER BY song_id").map { try $0.text("song_id") } }
    }
    func withTransaction<T>(commit: (_ action: () throws -> Void) throws -> Void,
                            _ body: (any MusicNativeLibraryWriting) throws -> T) throws -> T {
        try database.writeSync { db in
            try stateLock.withLock {
                try musicRequire(!closed && !active, .storageConflict)
                active = true
            }
            var before = MusicNativeLibraryRevision(revision: 0, receipt: "")
            let writer = Writer(database: self, connection: db)
            var after: MusicNativeLibraryRevision?
            defer {
                writer.active = false
                stateLock.withLock { active = false }
            }
            do {
                let result = try SQLiteDatabase.transaction(db, commit: commit) { db in
                    before = try revision(db)
                    let result = try body(writer)
                    if writer.changed {
                        try musicRequire(before.revision < 9_007_199_254_740_991, .limit)
                        let next = MusicNativeLibraryRevision(revision: before.revision + 1, receipt: UUID().uuidString)
                        try db.upsert("music_owner_state", columns: ["scope", "revision", "receipt"],
                            values: [.text(scope), .integer(next.revision), .text(next.receipt)], key: ["scope"])
                        after = next
                    }
                    return result
                }
                if let after {
                    stateLock.withLock {
                        if cachedAreaRevision == before {
                            cachedAreas.merge(writer.areaValues) { _, value in value }
                            cachedAreaRevision = after
                        } else {
                            cachedAreas = [:]
                            cachedAreaRevision = nil
                        }
                    }
                }
                return result
            } catch {
                stateLock.withLock {
                    cachedAreas = [:]
                    cachedAreaRevision = nil
                }
                if !db.isInTransaction, let after {
                    guard let current = try? revision(db) else {
                        throw MusicNativeLibraryCommitError.outcomeUnknown(before: before, after: after)
                    }
                    if current == after { throw MusicNativeLibraryCommitError.receiptLost }
                    if current != before { throw MusicNativeLibraryCommitError.outcomeUnknown(before: before, after: after) }
                }
                throw error
            }
        }
    }
    public func close() throws {
        stateLock.withLock {
            closed = true
            cachedAreas = [:]
            cachedAreaRevision = nil
        }
    }

    private final class Writer: MusicNativeLibraryWriting, @unchecked Sendable {
        let database: MusicNativeLibraryDatabase
        let connection: SQLiteConnection
        var active = true
        var changed = false
        var changedSongIDs: Set<String> = []
        var areaValues: [String: MJ] = [:]
        init(database: MusicNativeLibraryDatabase, connection: SQLiteConnection) {
            self.database = database; self.connection = connection
        }
        private func check() throws {
            let isDatabaseActive = database.stateLock.withLock { database.active }
            try musicRequire(active && isDatabaseActive && connection.isInTransaction, .storageConflict)
        }
        private var resolvedSourceOwners: [String: String] = [:]
        private var missingSourceOwners: Set<String> = []
        private var candidateRows: [String: [String]] = [:]
        private var resolvedCandidateKeys: Set<String> = []
        func readRevision() throws -> MusicNativeLibraryRevision { try database.revision(connection) }
        func readSnapshot() throws -> MusicNativeLibrarySnapshot { try database.readSnapshot() }
        func song(id: String) throws -> MJ? { try database.readSong(connection, id: id) }
        func containsSong(id: String) throws -> Bool {
            try !connection.query("SELECT song_id FROM music_songs WHERE song_id=?", [.text(id)]).isEmpty
        }
        func sourceOwner(key: String) throws -> String? {
            if let owner = resolvedSourceOwners[key] { return owner }
            if missingSourceOwners.contains(key) { return nil }
            let owner = try connection.query(
                "SELECT song_id FROM music_song_sources WHERE source_key=?",
                [.text(key)]
            ).first?.text("song_id")
            if let owner { resolvedSourceOwners[key] = owner }
            else { missingSourceOwners.insert(key) }
            return owner
        }
        func sourceOwners(keys: Set<String>) throws -> [String: String] {
            let unresolved = keys.subtracting(resolvedSourceOwners.keys).subtracting(missingSourceOwners)
            let values = Array(unresolved)
            for start in stride(from: 0, to: values.count, by: 500) {
                let batch = Array(values[start..<min(start + 500, values.count)])
                let placeholders = Array(repeating: "?", count: batch.count).joined(separator: ",")
                for row in try connection.query("SELECT source_key,song_id FROM music_song_sources WHERE source_key IN (\(placeholders))",
                    batch.map(SQLiteValue.text)) {
                    resolvedSourceOwners[try row.text("source_key")] = try row.text("song_id")
                }
            }
            missingSourceOwners.formUnion(unresolved.subtracting(resolvedSourceOwners.keys))
            return resolvedSourceOwners.filter { keys.contains($0.key) }
        }
        func candidateIDs(lookupKey: String) throws -> [String] {
            if resolvedCandidateKeys.contains(lookupKey) { return candidateRows[lookupKey] ?? [] }
            let rows = try connection.query(
                "SELECT song_id FROM music_songs WHERE lookup_key=? ORDER BY song_id",
                [.text(lookupKey)]
            ).map { try $0.text("song_id") }
            resolvedCandidateKeys.insert(lookupKey); candidateRows[lookupKey] = rows
            return rows
        }
        func candidateIDs(lookupKeys: Set<String>) throws -> [String: [String]] {
            let unresolved = lookupKeys.subtracting(resolvedCandidateKeys)
            let values = Array(unresolved)
            for start in stride(from: 0, to: values.count, by: 500) {
                let batch = Array(values[start..<min(start + 500, values.count)])
                let placeholders = Array(repeating: "?", count: batch.count).joined(separator: ",")
                try connection.rows("SELECT lookup_key,song_id FROM music_songs WHERE lookup_key IN (\(placeholders)) ORDER BY lookup_key,song_id",
                    batch.map(SQLiteValue.text)) { row in
                    candidateRows[try row.text("lookup_key"), default: []].append(try row.text("song_id"))
                }
            }
            resolvedCandidateKeys.formUnion(unresolved)
            return Dictionary(uniqueKeysWithValues: lookupKeys.map { ($0, candidateRows[$0] ?? []) })
        }
        func querySongIDs(query: String) throws -> [String] {
            try connection.query(
                "SELECT song_id FROM music_songs WHERE search_query=? ORDER BY song_id",
                [.text(query)]
            ).map { try $0.text("song_id") }
        }
        func allSongIDs() throws -> [String] {
            try connection.query("SELECT song_id FROM music_songs ORDER BY song_id").map { try $0.text("song_id") }
        }
        func saveArea(_ area: String, value: MJ) throws {
            try check()
            try musicRequire(MusicDocument.areas.contains(area), .invalidRequest)
            let previous: MJ?
            if let pending = areaValues[area] {
                previous = pending
            } else {
                let currentRevision = try database.revision(connection)
                let cached: (Bool, MJ?) = database.stateLock.withLock {
                    database.cachedAreaRevision == currentRevision ? (true, database.cachedAreas[area]) : (false, nil)
                }
                if cached.0 {
                    previous = cached.1
                } else {
                    previous = try MusicNativeLibraryDatabase.readArea(area, from: connection, scope: database.scope)
                }
            }
            if let previous, value.stable == previous.stable { return }
            try MusicNativeLibraryDatabase.writeArea(area, to: connection, scope: database.scope, value: value)
            areaValues[area] = value
            changed = true
        }
        func saveSong(_ song: MJ) throws {
            try check()
            let id = song["songId"].string
            try MusicLibraryValidation.validateSong(song, id: id)
            let old = try MusicSongStore.read(connection, id: id)
            if let old, song.stable == old.stable { return }
            let sourceKeys = Set(song["sources"].array.map { $0["sourceKey"].string })
            try musicRequire(old?["sources"].array.allSatisfy { sourceKeys.contains($0["sourceKey"].string) } ?? true, .nativeIDConflict)
            if let old, old["baselineDuration"] != .null {
                try musicRequire(old["baselineDuration"] == song["baselineDuration"], .storageConflict)
            }
            for source in song["sources"].array {
                if let owner = try sourceOwner(key: source["sourceKey"].string) { try musicRequire(owner == id, .nativeIDConflict) }
            }
            let evidence = MusicModel.descriptor(song)
            let query = MusicRecordingIdentity.normalize(song["title"].string + " " + song["artist"].string)
            try MusicSongStore.write(
                connection,
                song: song,
                id: id,
                lookupKey: evidence.lookupKey,
                searchQuery: query,
                baselineDuration: evidence.duration
            )
            changed = true; changedSongIDs.insert(id)
            for source in sourceKeys {
                resolvedSourceOwners[source] = id
                missingSourceOwners.remove(source)
            }
        }
    }
}
