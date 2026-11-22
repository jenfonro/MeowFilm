import Foundation
import MeowMusicFeature
import MeowStorage

protocol MusicNativeLibraryReading: Sendable {
    func readRevision() throws -> Int
    func readSnapshot() throws -> MusicNativeLibrarySnapshot
    func song(id: String) throws -> MusicSongRecord?
    func containsSong(id: String) throws -> Bool
    func sourceOwner(key: String) throws -> String?
    func sourceOwners(keys: Set<String>) throws -> [String: String]
    func candidateIDs(matchKey: String) throws -> [String]
    func candidateIDs(matchKeys: Set<String>) throws -> [String: [String]]
    func querySongIDs(query: String) throws -> [String]
    func allSongIDs() throws -> [String]
}

extension MusicNativeLibraryReading {
    func readRevision() throws -> Int { try readSnapshot().revision }
    func sourceOwners(keys: Set<String>) throws -> [String: String] {
        var result: [String: String] = [:]
        for key in keys { if let owner = try sourceOwner(key: key) { result[key] = owner } }
        return result
    }
    func candidateIDs(matchKeys: Set<String>) throws -> [String: [String]] {
        var result: [String: [String]] = [:]
        for key in matchKeys { result[key] = try candidateIDs(matchKey: key) }
        return result
    }
}

protocol MusicNativeLibraryWriting: MusicNativeLibraryReading {
    var changedSongIDs: Set<String> { get }
    func saveSong(_ song: MusicSongRecord, matchKey: String?, searchQuery: String) throws
    func saveLibrary(_ value: MusicLibraryAreaRecord) throws
    func saveQueue(_ value: MusicQueueAreaRecord) throws
    func saveHistory(_ value: MusicHistoryAreaRecord) throws
    func savePlayback(_ value: MusicPlaybackAreaRecord) throws
    func saveCatalogSource(_ value: MusicCatalogSourceAreaRecord) throws
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

/// The music owner borrows the App permanent data database's off-main lane. It never
/// opens a second SQLite file or closes the shared connection. Songs, relationships,
/// and area records settle in the same transaction as `owner_revisions`.
public final class MusicNativeLibraryDatabase: MusicNativeLibraryEngine, @unchecked Sendable {
    private let database: SQLiteDatabase
    private let scope: String
    private let ownerKey: String
    private let stateLock = NSLock()
    private var closed = false
    private var active = false
    private var cachedRevision: Int?
    private var cachedAreas = MusicNativeLibraryAreas()

    public init(database: SQLiteDatabase, scope: String = "device") {
        self.database = database
        self.scope = scope
        self.ownerKey = "music.library:\(scope)"
    }

    private func read<T>(_ action: (SQLiteConnection) throws -> T) throws -> T {
        try database.readSync { db in
            try musicRequire(!stateLock.withLock { closed }, .closed)
            return try action(db)
        }
    }

    private func revision(_ db: SQLiteConnection) throws -> Int {
        let rev = try db.ownerRevision(ownerKey)
        guard let revisionInt = Int(exactly: rev) else { throw MusicServiceError.corruptDocument }
        return revisionInt
    }

    func readRevision() throws -> Int { try read(revision) }

    func readSnapshot() throws -> MusicNativeLibrarySnapshot {
        try read { db in
            try SQLiteDatabase.readSnapshot(db) { db in
                let current = try revision(db)
                let cached: MusicNativeLibraryAreas? = stateLock.withLock {
                    !active && cachedRevision == current ? cachedAreas : nil
                }
                if let cached {
                    return MusicNativeLibrarySnapshot(revision: current, areas: cached)
                }
                let areas = MusicNativeLibraryAreas(
                    library: try MusicLibraryAreaStore.read(db, scope: scope),
                    queue: try MusicQueueAreaStore.read(db, scope: scope),
                    history: try MusicHistoryAreaStore.read(db, scope: scope),
                    playback: try MusicPlaybackAreaStore.read(db, scope: scope),
                    catalogSource: try MusicCatalogSourceAreaStore.read(db, scope: scope)
                )
                stateLock.withLock {
                    if !active && !closed && (cachedRevision == nil || current >= cachedRevision!) {
                        cachedAreas = areas
                        cachedRevision = current
                    }
                }
                return MusicNativeLibrarySnapshot(revision: current, areas: areas)
            }
        }
    }

    func song(id: String) throws -> MusicSongRecord? {
        try read { db in try SQLiteDatabase.readSnapshot(db) { try MusicSongStore.read($0, id: id) } }
    }

    func containsSong(id: String) throws -> Bool {
        try read { try $0.queryOne("SELECT 1 FROM music_songs WHERE song_id=?", [.text(id)]) { _ in true } != nil }
    }

    func sourceOwner(key: String) throws -> String? {
        try read { try $0.queryOne("SELECT song_id FROM music_song_sources WHERE source_key=?", [.text(key)]) { try $0.text(0) } }
    }

    func sourceOwners(keys: Set<String>) throws -> [String: String] {
        try read { db in
            let values = Array(keys)
            guard !values.isEmpty else { return [:] }
            let queryBatches: (SQLiteConnection) throws -> [String: String] = { conn in
                var result: [String: String] = [:]
                for start in stride(from: 0, to: values.count, by: 500) {
                    let batch = Array(values[start..<min(start + 500, values.count)])
                    let placeholders = Array(repeating: "?", count: batch.count).joined(separator: ",")
                    try conn.scan(
                        "SELECT source_key,song_id FROM music_song_sources WHERE source_key IN (\(placeholders))",
                        batch.map(SQLiteValue.text)
                    ) { row in
                        result[try row.text(0)] = try row.text(1)
                    }
                }
                return result
            }
            return values.count > 500 ? try SQLiteDatabase.readSnapshot(db, queryBatches) : try queryBatches(db)
        }
    }

    func candidateIDs(matchKey: String) throws -> [String] {
        try read { try $0.query("SELECT song_id FROM music_songs WHERE match_key=? ORDER BY song_id", [.text(matchKey)]) { try $0.text(0) } }
    }

    func candidateIDs(matchKeys: Set<String>) throws -> [String: [String]] {
        try read { db in
            let values = Array(matchKeys)
            guard !values.isEmpty else { return [:] }
            let queryBatches: (SQLiteConnection) throws -> [String: [String]] = { conn in
                var result: [String: [String]] = [:]
                for start in stride(from: 0, to: values.count, by: 500) {
                    let batch = Array(values[start..<min(start + 500, values.count)])
                    let placeholders = Array(repeating: "?", count: batch.count).joined(separator: ",")
                    try conn.scan(
                        "SELECT match_key,song_id FROM music_songs WHERE match_key IN (\(placeholders)) ORDER BY match_key,song_id",
                        batch.map(SQLiteValue.text)
                    ) { row in
                        result[try row.text(0), default: []].append(try row.text(1))
                    }
                }
                return result
            }
            return values.count > 500 ? try SQLiteDatabase.readSnapshot(db, queryBatches) : try queryBatches(db)
        }
    }

    func querySongIDs(query: String) throws -> [String] {
        try read { try $0.query("SELECT song_id FROM music_songs WHERE search_query=? ORDER BY song_id", [.text(query)]) { try $0.text(0) } }
    }

    func allSongIDs() throws -> [String] {
        try read { try $0.query("SELECT song_id FROM music_songs ORDER BY song_id") { try $0.text(0) } }
    }

    func withTransaction<T>(commit: (_ action: () throws -> Void) throws -> Void,
                            _ body: (any MusicNativeLibraryWriting) throws -> T) throws -> T {
        try database.writeSync { db in
            try stateLock.withLock {
                try musicRequire(!closed && !active, .storageConflict)
                active = true
            }
            var before = 0
            let writer = Writer(database: self, connection: db)
            var after: Int?
            defer {
                writer.active = false
                stateLock.withLock { active = false }
            }
            do {
                let result = try SQLiteDatabase.transaction(db, commit: commit) { db in
                    before = try revision(db)
                    writer.baseRevision = before
                    let result = try body(writer)
                    if writer.changed {
                        try musicRequire(before < Int(MusicPersistenceListDiff.maxSafeInteger), .limit)
                        let committed = try db.commitOwnerRevision(ownerKey, expectedRevision: Int64(before))
                        guard let nextInt = Int(exactly: committed) else { throw MusicServiceError.limit }
                        after = nextInt
                    }
                    return result
                }
                if let after {
                    stateLock.withLock {
                        if cachedRevision == before {
                            if let library = writer.savedLibrary { cachedAreas.library = library }
                            if let queue = writer.savedQueue { cachedAreas.queue = queue }
                            if let history = writer.savedHistory { cachedAreas.history = history }
                            if let playback = writer.savedPlayback { cachedAreas.playback = playback }
                            if let catalogSource = writer.savedCatalogSource { cachedAreas.catalogSource = catalogSource }
                            cachedRevision = after
                        } else {
                            cachedAreas = MusicNativeLibraryAreas()
                            cachedRevision = nil
                        }
                    }
                }
                return result
            } catch {
                stateLock.withLock {
                    cachedAreas = MusicNativeLibraryAreas()
                    cachedRevision = nil
                }
                throw error
            }
        }
    }

    public func close() throws {
        stateLock.withLock {
            closed = true
            cachedAreas = MusicNativeLibraryAreas()
            cachedRevision = nil
        }
    }

    private final class Writer: MusicNativeLibraryWriting, @unchecked Sendable {
        let database: MusicNativeLibraryDatabase
        let connection: SQLiteConnection
        var active = true
        var baseRevision = 0
        var changed = false
        var changedSongIDs: Set<String> = []
        var savedLibrary: MusicLibraryAreaRecord?
        var savedQueue: MusicQueueAreaRecord?
        var savedHistory: MusicHistoryAreaRecord?
        var savedPlayback: MusicPlaybackAreaRecord?
        var savedCatalogSource: MusicCatalogSourceAreaRecord?

        private var loadedSongs: [String: MusicSongRecord] = [:]
        private var missingSongs: Set<String> = []
        private var resolvedSourceOwners: [String: String] = [:]
        private var missingSourceOwners: Set<String> = []
        private var candidateRows: [String: [String]] = [:]
        private var resolvedCandidateKeys: Set<String> = []

        init(database: MusicNativeLibraryDatabase, connection: SQLiteConnection) {
            self.database = database
            self.connection = connection
        }

        private func check() throws {
            let isDatabaseActive = database.stateLock.withLock { database.active }
            try musicRequire(active && isDatabaseActive && connection.isInTransaction, .storageConflict)
        }

        func readRevision() throws -> Int { try database.revision(connection) }

        func readSnapshot() throws -> MusicNativeLibrarySnapshot {
            let current = try database.revision(connection)
            let areas = MusicNativeLibraryAreas(
                library: try savedLibrary ?? MusicLibraryAreaStore.read(connection, scope: database.scope),
                queue: try savedQueue ?? MusicQueueAreaStore.read(connection, scope: database.scope),
                history: try savedHistory ?? MusicHistoryAreaStore.read(connection, scope: database.scope),
                playback: try savedPlayback ?? MusicPlaybackAreaStore.read(connection, scope: database.scope),
                catalogSource: try savedCatalogSource ?? MusicCatalogSourceAreaStore.read(connection, scope: database.scope)
            )
            return MusicNativeLibrarySnapshot(revision: current, areas: areas)
        }

        func song(id: String) throws -> MusicSongRecord? {
            if let cached = loadedSongs[id] { return cached }
            if missingSongs.contains(id) { return nil }
            let record = try MusicSongStore.read(connection, id: id)
            if let record { loadedSongs[id] = record }
            else { missingSongs.insert(id) }
            return record
        }

        func containsSong(id: String) throws -> Bool {
            if loadedSongs[id] != nil { return true }
            if missingSongs.contains(id) { return false }
            let exists = try connection.queryOne("SELECT 1 FROM music_songs WHERE song_id=?", [.text(id)]) { _ in true } != nil
            if !exists { missingSongs.insert(id) }
            return exists
        }

        func sourceOwner(key: String) throws -> String? {
            if let owner = resolvedSourceOwners[key] { return owner }
            if missingSourceOwners.contains(key) { return nil }
            let owner = try connection.queryOne(
                "SELECT song_id FROM music_song_sources WHERE source_key=?",
                [.text(key)]
            ) { try $0.text(0) }
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
                try connection.scan(
                    "SELECT source_key,song_id FROM music_song_sources WHERE source_key IN (\(placeholders))",
                    batch.map(SQLiteValue.text)
                ) { row in
                    resolvedSourceOwners[try row.text(0)] = try row.text(1)
                }
            }
            missingSourceOwners.formUnion(unresolved.subtracting(resolvedSourceOwners.keys))
            return resolvedSourceOwners.filter { keys.contains($0.key) }
        }

        func candidateIDs(matchKey: String) throws -> [String] {
            if resolvedCandidateKeys.contains(matchKey) { return candidateRows[matchKey] ?? [] }
            let rows = try connection.query(
                "SELECT song_id FROM music_songs WHERE match_key=? ORDER BY song_id",
                [.text(matchKey)]
            ) { try $0.text(0) }
            resolvedCandidateKeys.insert(matchKey)
            candidateRows[matchKey] = rows
            return rows
        }

        func candidateIDs(matchKeys: Set<String>) throws -> [String: [String]] {
            let unresolved = matchKeys.subtracting(resolvedCandidateKeys)
            let values = Array(unresolved)
            for start in stride(from: 0, to: values.count, by: 500) {
                let batch = Array(values[start..<min(start + 500, values.count)])
                let placeholders = Array(repeating: "?", count: batch.count).joined(separator: ",")
                try connection.scan(
                    "SELECT match_key,song_id FROM music_songs WHERE match_key IN (\(placeholders)) ORDER BY match_key,song_id",
                    batch.map(SQLiteValue.text)
                ) { row in
                    candidateRows[try row.text(0), default: []].append(try row.text(1))
                }
            }
            resolvedCandidateKeys.formUnion(unresolved)
            return Dictionary(uniqueKeysWithValues: matchKeys.map { ($0, candidateRows[$0] ?? []) })
        }

        func querySongIDs(query: String) throws -> [String] {
            try connection.query(
                "SELECT song_id FROM music_songs WHERE search_query=? ORDER BY song_id",
                [.text(query)]
            ) { try $0.text(0) }
        }

        func allSongIDs() throws -> [String] {
            try connection.query("SELECT song_id FROM music_songs ORDER BY song_id") { try $0.text(0) }
        }

        private func cachedArea<T>(_ extract: (MusicNativeLibraryAreas) -> T?) -> (hit: Bool, value: T?) {
            database.stateLock.withLock {
                if database.cachedRevision == baseRevision {
                    return (true, extract(database.cachedAreas))
                }
                return (false, nil)
            }
        }

        func saveLibrary(_ value: MusicLibraryAreaRecord) throws {
            try check()
            let previous: MusicLibraryAreaRecord?
            if let savedLibrary {
                previous = savedLibrary
            } else {
                let cached = cachedArea(\.library)
                previous = cached.hit ? cached.value : (try MusicLibraryAreaStore.read(connection, scope: database.scope))
            }
            if previous == value { return }
            try MusicLibraryAreaStore.write(connection, scope: database.scope, value: value, previous: previous)
            savedLibrary = value
            changed = true
        }

        func saveQueue(_ value: MusicQueueAreaRecord) throws {
            try check()
            let previous: MusicQueueAreaRecord?
            if let savedQueue {
                previous = savedQueue
            } else {
                let cached = cachedArea(\.queue)
                previous = cached.hit ? cached.value : (try MusicQueueAreaStore.read(connection, scope: database.scope))
            }
            if previous == value { return }
            try MusicQueueAreaStore.write(connection, scope: database.scope, value: value, previous: previous)
            savedQueue = value
            changed = true
        }

        func saveHistory(_ value: MusicHistoryAreaRecord) throws {
            try check()
            let previous: MusicHistoryAreaRecord?
            if let savedHistory {
                previous = savedHistory
            } else {
                let cached = cachedArea(\.history)
                previous = cached.hit ? cached.value : (try MusicHistoryAreaStore.read(connection, scope: database.scope))
            }
            if previous == value { return }
            try MusicHistoryAreaStore.write(connection, scope: database.scope, value: value, previous: previous)
            savedHistory = value
            changed = true
        }

        func savePlayback(_ value: MusicPlaybackAreaRecord) throws {
            try check()
            let previous: MusicPlaybackAreaRecord?
            if let savedPlayback {
                previous = savedPlayback
            } else {
                let cached = cachedArea(\.playback)
                previous = cached.hit ? cached.value : (try MusicPlaybackAreaStore.read(connection, scope: database.scope))
            }
            if previous == value { return }
            try MusicPlaybackAreaStore.write(connection, scope: database.scope, value: value, previous: previous)
            savedPlayback = value
            changed = true
        }

        func saveCatalogSource(_ value: MusicCatalogSourceAreaRecord) throws {
            try check()
            let previous: MusicCatalogSourceAreaRecord?
            if let savedCatalogSource {
                previous = savedCatalogSource
            } else {
                let cached = cachedArea(\.catalogSource)
                previous = cached.hit ? cached.value : (try MusicCatalogSourceAreaStore.read(connection, scope: database.scope))
            }
            if previous == value { return }
            try MusicCatalogSourceAreaStore.write(connection, scope: database.scope, value: value)
            savedCatalogSource = value
            changed = true
        }

        func saveSong(_ song: MusicSongRecord, matchKey: String?, searchQuery: String) throws {
            try check()
            let id = song.songID
            try musicRequire(UUID(uuidString: id) != nil, .songIDConflict)
            let old = try self.song(id: id)
            if let old, old == song { return }
            var seenKeys = Set<String>()
            for source in song.sources {
                try musicRequire(!source.sourceKey.isEmpty && seenKeys.insert(source.sourceKey).inserted, .nativeIDConflict)
            }
            if let old {
                try musicRequire(old.sources.allSatisfy { seenKeys.contains($0.sourceKey) }, .nativeIDConflict)
                if let oldBaseline = old.baselineDuration {
                    try musicRequire(oldBaseline == song.baselineDuration, .storageConflict)
                }
            }
            for source in song.sources {
                if let owner = try sourceOwner(key: source.sourceKey) {
                    try musicRequire(owner == id, .nativeIDConflict)
                }
            }
            try MusicSongStore.writeIncremental(
                connection,
                song: song,
                previous: old,
                matchKey: matchKey,
                searchQuery: searchQuery
            )
            changed = true
            changedSongIDs.insert(id)
            loadedSongs[id] = song
            missingSongs.remove(id)
            for sourceKey in seenKeys {
                resolvedSourceOwners[sourceKey] = id
                missingSourceOwners.remove(sourceKey)
            }
            if old == nil, let matchKey, resolvedCandidateKeys.contains(matchKey) {
                var list = candidateRows[matchKey] ?? []
                if !list.contains(id) {
                    list.append(id)
                    list.sort()
                    candidateRows[matchKey] = list
                }
            }
        }
    }
}
