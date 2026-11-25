import Foundation
import MeowMusicFeature
import MeowStorage

protocol MusicNativeLibraryReading: Sendable {
    func readRevision() throws -> Int
    func readSnapshot() throws -> MusicLibrarySnapshot
    func song(id: String) throws -> MusicSongRecord?
    func songs(ids: Set<String>) throws -> [String: MusicSongRecord]
    func containsSong(id: String) throws -> Bool
    func containsSongs(ids: Set<String>) throws -> Set<String>
    func sourceOwner(key: String) throws -> String?
    func sourceOwners(keys: Set<String>) throws -> [String: String]
    func candidateIDs(matchKey: String) throws -> [String]
    func candidateIDs(matchKeys: Set<String>) throws -> [String: [String]]
    func querySongIDs(query: String) throws -> [String]
    func allSongIDs() throws -> [String]
}

extension MusicNativeLibraryReading {
    func readRevision() throws -> Int { try readSnapshot().revision }
    func songs(ids: Set<String>) throws -> [String: MusicSongRecord] {
        var result: [String: MusicSongRecord] = [:]
        for id in ids { if let value = try song(id: id) { result[id] = value } }
        return result
    }
    func containsSongs(ids: Set<String>) throws -> Set<String> {
        var result: Set<String> = []
        for id in ids where try containsSong(id: id) { result.insert(id) }
        return result
    }
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
    func applyLibraryChanges(from previous: MusicLibraryState?, to next: MusicLibraryState) throws
    func applyQueueChanges(from previous: MusicQueueState?, to next: MusicQueueState) throws
    func applyHistoryChanges(from previous: MusicHistoryState?, to next: MusicHistoryState) throws
    func applyPlaybackChanges(from previous: MusicPlaybackSettingsState?, to next: MusicPlaybackSettingsState) throws
    func saveCatalogSource(_ value: MusicCatalogSourceState) throws
}

/// The music owner borrows the App permanent data database's off-main lane. It never
/// opens a second SQLite file or closes the shared connection. Songs, relationships,
/// and domain changes settle in the same transaction as `owner_revisions`.
public final class MusicNativeLibraryDatabase: MusicNativeLibraryReading, @unchecked Sendable {
    private let database: SQLiteDatabase
    private let scope: String
    private let ownerKey: String
    private let stateLock = NSLock()
    private var closed = false
    private var active = false

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
        guard let revisionInt = Int(exactly: rev) else { throw MusicServiceError.corruptStorage }
        return revisionInt
    }

    func readRevision() throws -> Int { try read(revision) }

    func readSnapshot() throws -> MusicLibrarySnapshot {
        try read { db in
            try SQLiteDatabase.readSnapshot(db) { db in
                let current = try revision(db)
                return MusicLibrarySnapshot(
                    revision: current,
                    library: try MusicLibraryRepository.read(db, scope: scope),
                    queue: try MusicQueueRepository.read(db, scope: scope),
                    history: try MusicHistoryRepository.read(db, scope: scope),
                    playback: try MusicPlaybackSettingsRepository.read(db, scope: scope),
                    catalogSource: try MusicCatalogSourceRepository.read(db, scope: scope)
                )
            }
        }
    }

    func song(id: String) throws -> MusicSongRecord? {
        try read { db in try SQLiteDatabase.readSnapshot(db) { try MusicSongRepository.read($0, id: id) } }
    }

    func songs(ids: Set<String>) throws -> [String: MusicSongRecord] {
        guard !ids.isEmpty else { return [:] }
        return try read { db in try SQLiteDatabase.readSnapshot(db) { try MusicSongRepository.readBatch($0, ids: ids) } }
    }

    func containsSong(id: String) throws -> Bool {
        try read { try $0.queryOne("SELECT 1 FROM music_songs WHERE song_id=?", [.text(id)]) { _ in true } != nil }
    }

    func containsSongs(ids: Set<String>) throws -> Set<String> {
        guard !ids.isEmpty else { return [] }
        return try read { db in
            let values = Array(ids)
            var found: Set<String> = []
            for start in stride(from: 0, to: values.count, by: 500) {
                let batch = Array(values[start..<min(start + 500, values.count)])
                let placeholders = Array(repeating: "?", count: batch.count).joined(separator: ",")
                try db.scan("SELECT song_id FROM music_songs WHERE song_id IN (\(placeholders))", batch.map(SQLiteValue.text)) { row in
                    found.insert(try row.text(0))
                }
            }
            return found
        }
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
            let writer = Writer(database: self, connection: db)
            defer {
                writer.active = false
                stateLock.withLock { active = false }
            }
            return try SQLiteDatabase.transaction(db, commit: commit) { db in
                let before = try revision(db)
                let result = try body(writer)
                if writer.changed {
                    try musicRequire(before < musicMaxSafeInteger, .limit)
                    let committed = try db.commitOwnerRevision(ownerKey, expectedRevision: Int64(before))
                    guard Int(exactly: committed) != nil else { throw MusicServiceError.limit }
                }
                return result
            }
        }
    }

    public func close() throws {
        stateLock.withLock { closed = true }
    }

    private final class Writer: MusicNativeLibraryWriting, @unchecked Sendable {
        let database: MusicNativeLibraryDatabase
        let connection: SQLiteConnection
        var active = true
        var changed = false
        var changedSongIDs: Set<String> = []

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

        func readSnapshot() throws -> MusicLibrarySnapshot {
            let current = try database.revision(connection)
            return MusicLibrarySnapshot(
                revision: current,
                library: try MusicLibraryRepository.read(connection, scope: database.scope),
                queue: try MusicQueueRepository.read(connection, scope: database.scope),
                history: try MusicHistoryRepository.read(connection, scope: database.scope),
                playback: try MusicPlaybackSettingsRepository.read(connection, scope: database.scope),
                catalogSource: try MusicCatalogSourceRepository.read(connection, scope: database.scope)
            )
        }

        func song(id: String) throws -> MusicSongRecord? {
            if let cached = loadedSongs[id] { return cached }
            if missingSongs.contains(id) { return nil }
            let record = try MusicSongRepository.read(connection, id: id)
            if let record { loadedSongs[id] = record }
            else { missingSongs.insert(id) }
            return record
        }

        func songs(ids: Set<String>) throws -> [String: MusicSongRecord] {
            let unresolved = ids.subtracting(loadedSongs.keys).subtracting(missingSongs)
            if !unresolved.isEmpty {
                let fetched = try MusicSongRepository.readBatch(connection, ids: unresolved)
                for (id, record) in fetched { loadedSongs[id] = record }
                missingSongs.formUnion(unresolved.subtracting(fetched.keys))
            }
            var result: [String: MusicSongRecord] = [:]
            result.reserveCapacity(ids.count)
            for id in ids { if let value = loadedSongs[id] { result[id] = value } }
            return result
        }

        func containsSong(id: String) throws -> Bool {
            if loadedSongs[id] != nil { return true }
            if missingSongs.contains(id) { return false }
            let exists = try connection.queryOne("SELECT 1 FROM music_songs WHERE song_id=?", [.text(id)]) { _ in true } != nil
            if !exists { missingSongs.insert(id) }
            return exists
        }

        func containsSongs(ids: Set<String>) throws -> Set<String> {
            var found: Set<String> = []
            var unresolved: [String] = []
            for id in ids {
                if loadedSongs[id] != nil { found.insert(id) }
                else if !missingSongs.contains(id) { unresolved.append(id) }
            }
            for start in stride(from: 0, to: unresolved.count, by: 500) {
                let batch = Array(unresolved[start..<min(start + 500, unresolved.count)])
                let placeholders = Array(repeating: "?", count: batch.count).joined(separator: ",")
                var batchFound: Set<String> = []
                try connection.scan("SELECT song_id FROM music_songs WHERE song_id IN (\(placeholders))", batch.map(SQLiteValue.text)) { row in
                    batchFound.insert(try row.text(0))
                }
                found.formUnion(batchFound)
                missingSongs.formUnion(Set(batch).subtracting(batchFound))
            }
            return found
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

        func applyLibraryChanges(from previous: MusicLibraryState?, to next: MusicLibraryState) throws {
            try check()
            if previous == next { return }
            try MusicLibraryRepository.applyChanges(connection, scope: database.scope, previous: previous, next: next)
            changed = true
        }

        func applyQueueChanges(from previous: MusicQueueState?, to next: MusicQueueState) throws {
            try check()
            if previous == next { return }
            try MusicQueueRepository.applyChanges(connection, scope: database.scope, previous: previous, next: next)
            changed = true
        }

        func applyHistoryChanges(from previous: MusicHistoryState?, to next: MusicHistoryState) throws {
            try check()
            if previous == next { return }
            try MusicHistoryRepository.applyChanges(connection, scope: database.scope, previous: previous, next: next)
            changed = true
        }

        func applyPlaybackChanges(from previous: MusicPlaybackSettingsState?, to next: MusicPlaybackSettingsState) throws {
            try check()
            if previous == next { return }
            try MusicPlaybackSettingsRepository.applyChanges(connection, scope: database.scope, previous: previous, next: next)
            changed = true
        }

        func saveCatalogSource(_ value: MusicCatalogSourceState) throws {
            try check()
            try MusicCatalogSourceRepository.save(connection, scope: database.scope, value: value)
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
            try MusicSongRepository.upsertSong(
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
            if let matchKey, !matchKey.isEmpty, resolvedCandidateKeys.contains(matchKey) {
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
