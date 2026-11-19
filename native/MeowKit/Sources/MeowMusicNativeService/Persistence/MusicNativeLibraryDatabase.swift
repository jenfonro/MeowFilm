import Foundation
import MeowMusicFeature
import MeowRuntime
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

public struct MusicNativeLibrarySnapshot: Sendable {
    public let revision: Int
    public let receipt: String
    /// In-memory wire projections only. No encoded area is persisted.
    public let areas: [String: Data]
    public var identity: MusicNativeLibraryRevision { .init(revision: revision, receipt: receipt) }
    public init(revision: Int, receipt: String, areas: [String: Data]) {
        self.revision = revision; self.receipt = receipt; self.areas = areas
    }
}

public protocol MusicNativeLibraryReading: Sendable {
    func readRevision() throws -> MusicNativeLibraryRevision
    func readSnapshot() throws -> MusicNativeLibrarySnapshot
    func songData(id: String) throws -> Data?
    func containsSong(id: String) throws -> Bool
    func sourceOwner(key: String) throws -> String?
    func sourceOwners(keys: Set<String>) throws -> [String: String]
    func candidateIDs(lookupKey: String) throws -> [String]
    func candidateIDs(lookupKeys: Set<String>) throws -> [String: [String]]
    func querySongIDs(query: String) throws -> [String]
    func allSongIDs() throws -> [String]
}
public extension MusicNativeLibraryReading {
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
public protocol MusicNativeLibraryWriting: MusicNativeLibraryReading {
    var changedSongIDs: Set<String> { get }
    func saveSong(_ data: Data) throws
    func saveArea(_ area: String, value: RuntimeValue) throws
}
public protocol MusicNativeLibraryStoring: MusicNativeLibraryReading {
    func withTransaction<T>(commit: (_ action: () throws -> Void) throws -> Void,
                            _ body: (any MusicNativeLibraryWriting) throws -> T) throws -> T
    func close() throws
}
public extension MusicNativeLibraryStoring {
    func withTransaction<T>(_ body: (any MusicNativeLibraryWriting) throws -> T) throws -> T {
        try withTransaction(commit: { try $0() }, body)
    }
}

/// The music owner borrows the App database's off-main lane. It never opens a
/// second SQLite file or closes the shared connection. Songs, relationships and
/// control columns settle in the same transaction as the exact commit receipt.
public final class MusicNativeLibraryDatabase: MusicNativeLibraryStoring, @unchecked Sendable {
    private let database: SQLiteDatabase
    private let scope: String
    // Only accessed on the borrowed database lane, including nested callbacks.
    private var closed = false
    private var active = false
    private var cachedAreaRevision: MusicNativeLibraryRevision?
    private var cachedAreas: [String: RuntimeValue] = [:]

    public init(database: SQLiteDatabase, scope: String = "device") {
        self.database = database; self.scope = scope
    }
    private func read<T>(_ action: (SQLiteConnection) throws -> T) throws -> T {
        try database.readSync { db in
            try musicRequire(!closed, .closed)
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
    public func readRevision() throws -> MusicNativeLibraryRevision { try read(revision) }
    public func readSnapshot() throws -> MusicNativeLibrarySnapshot {
        try read { db in
            try SQLiteDatabase.readSnapshot(db) { db in
                let current = try revision(db)
                var values = cachedAreas
                if cachedAreaRevision != current {
                    values = [:]
                    for name in MusicDocument.areas {
                        guard let record = MusicStorageRecords.areas[name] else { throw MusicServiceError.corruptDocument }
                        values[name] = try record.read(db, key: [.text(scope)])
                    }
                    if !active { cachedAreas = values; cachedAreaRevision = current }
                }
                return MusicNativeLibrarySnapshot(revision: current.revision, receipt: current.receipt,
                    areas: try values.mapValues(RuntimeJSON.encode))
            }
        }
    }
    private func song(_ db: SQLiteConnection, id: String) throws -> Data? {
        guard let value = try MusicStorageRecords.song.read(db, key: [.text(id)]) else { return nil }
        let data = try RuntimeJSON.encode(value)
        try MusicLibraryValidation.validateSong(MJ.parse(data), id: id)
        return data
    }
    public func songData(id: String) throws -> Data? {
        try read { db in try SQLiteDatabase.readSnapshot(db) { try song($0, id: id) } }
    }
    public func containsSong(id: String) throws -> Bool {
        try read { try !$0.query("SELECT song_id FROM music_songs WHERE song_id=?", [.text(id)]).isEmpty }
    }
    public func sourceOwner(key: String) throws -> String? {
        try read { try $0.query("SELECT song_id FROM music_song_sources WHERE sources_key=?", [.text(key)]).first?.text("song_id") }
    }
    public func sourceOwners(keys: Set<String>) throws -> [String: String] {
        try read { db in
            var result: [String: String] = [:]
            let values = Array(keys)
            for start in stride(from: 0, to: values.count, by: 500) {
                let batch = Array(values[start..<min(start + 500, values.count)])
                let placeholders = Array(repeating: "?", count: batch.count).joined(separator: ",")
                for row in try db.query("SELECT sources_key,song_id FROM music_song_sources WHERE sources_key IN (\(placeholders))",
                    batch.map(SQLiteValue.text)) {
                    result[try row.text("sources_key")] = try row.text("song_id")
                }
            }
            return result
        }
    }
    public func candidateIDs(lookupKey: String) throws -> [String] {
        try read { try $0.query("SELECT song_id FROM music_songs WHERE lookup_key=? ORDER BY song_id", [.text(lookupKey)])
            .map { try $0.text("song_id") } }
    }
    public func candidateIDs(lookupKeys: Set<String>) throws -> [String: [String]] {
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
    public func querySongIDs(query: String) throws -> [String] {
        try read { try $0.query("SELECT song_id FROM music_songs WHERE search_query=? ORDER BY song_id", [.text(query)])
            .map { try $0.text("song_id") } }
    }
    public func allSongIDs() throws -> [String] {
        try read { try $0.query("SELECT song_id FROM music_songs ORDER BY song_id").map { try $0.text("song_id") } }
    }
    public func withTransaction<T>(commit: (_ action: () throws -> Void) throws -> Void,
                                  _ body: (any MusicNativeLibraryWriting) throws -> T) throws -> T {
        try database.writeSync { db in
            try musicRequire(!closed && !active, .storageConflict)
            var before = MusicNativeLibraryRevision(revision: 0, receipt: "")
            let writer = Writer(database: self, connection: db)
            var after: MusicNativeLibraryRevision?
            active = true
            defer { active = false; writer.active = false }
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
                    if cachedAreaRevision == before {
                        cachedAreas.merge(writer.areaValues) { _, value in value }
                        cachedAreaRevision = after
                    } else { cachedAreas = [:]; cachedAreaRevision = nil }
                }
                return result
            } catch {
                cachedAreas = [:]; cachedAreaRevision = nil
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
        try database.readSync { _ in closed = true; cachedAreas = [:]; cachedAreaRevision = nil }
    }

    private final class Writer: MusicNativeLibraryWriting, @unchecked Sendable {
        let database: MusicNativeLibraryDatabase
        let connection: SQLiteConnection
        var active = true
        var changed = false
        var changedSongIDs: Set<String> = []
        var areaValues: [String: RuntimeValue] = [:]
        init(database: MusicNativeLibraryDatabase, connection: SQLiteConnection) {
            self.database = database; self.connection = connection
        }
        private func check() throws {
            try musicRequire(active && database.active && connection.isInTransaction, .storageConflict)
        }
        private var resolvedSourceOwners: [String: String] = [:]
        private var missingSourceOwners: Set<String> = []
        private var candidateRows: [String: [String]] = [:]
        private var resolvedCandidateKeys: Set<String> = []
        func readRevision() throws -> MusicNativeLibraryRevision { try database.readRevision() }
        func readSnapshot() throws -> MusicNativeLibrarySnapshot { try database.readSnapshot() }
        func songData(id: String) throws -> Data? { try database.songData(id: id) }
        func containsSong(id: String) throws -> Bool { try database.containsSong(id: id) }
        func sourceOwner(key: String) throws -> String? {
            if let owner = resolvedSourceOwners[key] { return owner }
            if missingSourceOwners.contains(key) { return nil }
            let owner = try database.sourceOwner(key: key)
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
                for row in try connection.query("SELECT sources_key,song_id FROM music_song_sources WHERE sources_key IN (\(placeholders))",
                    batch.map(SQLiteValue.text)) {
                    resolvedSourceOwners[try row.text("sources_key")] = try row.text("song_id")
                }
            }
            missingSourceOwners.formUnion(unresolved.subtracting(resolvedSourceOwners.keys))
            return resolvedSourceOwners.filter { keys.contains($0.key) }
        }
        func candidateIDs(lookupKey: String) throws -> [String] {
            if resolvedCandidateKeys.contains(lookupKey) { return candidateRows[lookupKey] ?? [] }
            let rows = try database.candidateIDs(lookupKey: lookupKey)
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
        func querySongIDs(query: String) throws -> [String] { try database.querySongIDs(query: query) }
        func allSongIDs() throws -> [String] { try database.allSongIDs() }
        func saveArea(_ area: String, value: RuntimeValue) throws {
            try check()
            try musicRequire(MusicDocument.areas.contains(area), .invalidRequest)
            guard let record = MusicStorageRecords.areas[area] else { throw MusicServiceError.invalidRequest }
            let key: [SQLiteValue] = [.text(database.scope)]
            let previous: RuntimeValue?
            if let pending = areaValues[area] { previous = pending }
            else if database.cachedAreaRevision == (try database.revision(connection)) { previous = database.cachedAreas[area] }
            else { previous = try record.read(connection, key: key) }
            if value == previous { return }
            try record.save(connection, value: value, previous: previous, key: key)
            areaValues[area] = value
            changed = true
        }
        func saveSong(_ data: Data) throws {
            try check()
            let song = try MJ.parse(data), id = song["songId"].string
            try MusicLibraryValidation.validateSong(song, id: id)
            let record = MusicStorageRecords.song, key: [SQLiteValue] = [.text(id)]
            let previous = try record.read(connection, key: key)
            let old = try previous.map { try MJ.parse(RuntimeJSON.encode($0)) }
            let value = try RuntimeJSON.decode(data)
            if value == previous { return }
            let sourceKeys = Set(song["sources"].array.map { $0["sourceKey"].string })
            try musicRequire(old?["sources"].array.allSatisfy { sourceKeys.contains($0["sourceKey"].string) } ?? true, .nativeIDConflict)
            if let old, old["baselineDuration"] != .null {
                try musicRequire(old["baselineDuration"] == song["baselineDuration"], .storageConflict)
            }
            for source in song["sources"].array {
                if let owner = try sourceOwner(key: source["sourceKey"].string) { try musicRequire(owner == id, .nativeIDConflict) }
            }
            try record.save(connection, value: value, previous: previous, key: key)
            let evidence = MusicModel.descriptor(song)
            let query = MusicRecordingIdentity.normalize(song["title"].string + " " + song["artist"].string)
            try connection.execute("UPDATE music_songs SET lookup_key=?,search_query=?,baseline_duration=? WHERE song_id=?",
                [.optional(evidence.lookupKey), .text(query), .optional(evidence.duration), .text(id)])
            changed = true; changedSongIDs.insert(id)
            for source in sourceKeys {
                resolvedSourceOwners[source] = id
                missingSourceOwners.remove(source)
            }
        }
    }
}
