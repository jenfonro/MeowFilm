import Foundation
import MeowMusicFeature
import MeowStorage
import MeowRuntime

/// Search status refers to permanent records. No musicInfo or song metadata is
/// duplicated here; accepted source facts live only in the song repository.
public struct MusicSourceDiscoveryEntry: Codable, Equatable, Sendable {
    public let checkedAt: Double
    public let sourceKeys: [String]
    public let observations: [MusicSearchObservation]
    public init(checkedAt: Double, sourceKeys: [String], observations: [MusicSearchObservation] = []) {
        self.checkedAt = checkedAt; self.sourceKeys = sourceKeys; self.observations = observations
    }
}
public protocol MusicSourceDiscoveryCaching: Sendable {
    var persistsRecords: Bool { get }
    func entries() async -> [String: [String: MusicSourceDiscoveryEntry]]
    func entries(songID: String) async -> [String: MusicSourceDiscoveryEntry]
    func observe(songID: String, observation: MusicSearchObservation, sourceKeys: [String]) async
    func persist(retaining songIDs: [String]) async
}
public extension MusicSourceDiscoveryCaching {
    func entries(songID: String) async -> [String: MusicSourceDiscoveryEntry] {
        await entries()[songID] ?? [:]
    }
}

private struct SourceDiscoveryDocument: Codable {
    var records: [String: [String: MusicSourceDiscoveryEntry]] = [:]
    mutating func observe(songID: String, observation: MusicSearchObservation, sourceKeys: [String], maximumBytes: Int) {
        var platforms = records[songID] ?? [:]
        let previous = platforms[observation.platform]
        if let last = previous?.observations.max(by: { $1.isNewer(than: $0) }), !observation.isNewer(than: last) { return }
        let outcome: MusicSearchObservation.Outcome = observation.outcome == .failed ? .failed :
            !sourceKeys.isEmpty ? .found : observation.scope == .recording ? .notFound : .incomplete
        let current = observation.result(outcome, failure: observation.failureCode.flatMap(MusicServiceError.init(rawValue:)))
        // An unrelated/incomplete page is not a negative check for a recording.
        let retained = previous?.observations.filter { !$0.sameEvidence(as: current) } ?? []
        platforms[observation.platform] = .init(checkedAt: outcome == .failed || outcome == .incomplete ? previous?.checkedAt ?? 0 : current.checkedAt,
            sourceKeys: outcome == .failed || outcome == .incomplete ? previous?.sourceKeys ?? [] : sourceKeys,
            observations: retained + [current])
        records[songID] = platforms
        while records.reduce(0, { $0 + $1.key.utf8.count + Self.bytes($1.value) }) > maximumBytes,
              let oldest = records.min(by: {
                  ($0.value.values.map(\.checkedAt).max() ?? 0) < ($1.value.values.map(\.checkedAt).max() ?? 0)
              })?.key { records.removeValue(forKey: oldest) }
    }
    static func bytes(_ platforms: [String: MusicSourceDiscoveryEntry]) -> Int {
        var size = 0
        for (platform, entry) in platforms {
            size += platform.utf8.count + 96
            for key in entry.sourceKeys { size += key.utf8.count + 16 }
            for observation in entry.observations {
                size += 256 + observation.query.utf8.count + observation.context.utf8.count
                size += observation.platform.utf8.count + (observation.failureCode?.utf8.count ?? 0)
            }
        }
        return size
    }
}

public actor MusicNativeMemorySourceDiscoveryCache: MusicSourceDiscoveryCaching {
    public nonisolated var persistsRecords: Bool { false }
    private var document = SourceDiscoveryDocument()
    private let maximumBytes: Int
    public init(maximumBytes: Int = 4 * 1024 * 1024) { self.maximumBytes = maximumBytes }
    public func entries() -> [String: [String: MusicSourceDiscoveryEntry]] { document.records }
    public func observe(songID: String, observation: MusicSearchObservation, sourceKeys: [String]) {
        document.observe(songID: songID, observation: observation, sourceKeys: sourceKeys, maximumBytes: maximumBytes)
    }
    public func persist(retaining songIDs: [String]) {}
}

public actor MusicNativePersistentSourceDiscoveryCache: MusicSourceDiscoveryCaching {
    public static let namespace = "music.source-checks.v1"
    public nonisolated var persistsRecords: Bool { true }
    private let database: SQLiteDatabase
    private let maximumBytes: Int
    private var loaded: Set<String> = []
    private var dirty: Set<String> = []
    private var document = SourceDiscoveryDocument()
    private struct StoredRecord: Codable {
        let storedBytes: Int
        let platforms: [String: MusicSourceDiscoveryEntry]
    }
    public init(database: SQLiteDatabase, maximumBytes: Int = 4 * 1024 * 1024) {
        self.database = database; self.maximumBytes = maximumBytes
    }
    private func prepare(_ songID: String) -> Bool {
        if loaded.contains(songID) { return true }
        do {
            let saved = try database.readSync { db in
                try SQLiteDatabase.readSnapshot(db) { db in
                    try MusicStorageRecords.music_source_discovery.read(db, key: [.text(songID)]).map {
                        try JSONDecoder().decode(StoredRecord.self, from: RuntimeJSON.encode($0))
                    }
                }
            }
            if let saved {
                var combined = SourceDiscoveryDocument(records: [songID: saved.platforms])
                if let platforms = document.records[songID] {
                    for entry in platforms.values {
                        for observation in entry.observations.sorted(by: { $1.isNewer(than: $0) }) {
                            combined.observe(songID: songID, observation: observation, sourceKeys: entry.sourceKeys,
                                          maximumBytes: maximumBytes)
                        }
                    }
                }
                document.records[songID] = combined.records[songID]
            }
            loaded.insert(songID); return true
        } catch { return false }
    }
    public func entries() -> [String: [String: MusicSourceDiscoveryEntry]] {
        if let ids = try? database.readSync({ try $0.query("SELECT song_id FROM music_source_discovery").map { try $0.text("song_id") } }) {
            for id in ids { _ = prepare(id) }
        }
        return document.records
    }
    public func entries(songID: String) -> [String: MusicSourceDiscoveryEntry] {
        _ = prepare(songID)
        return document.records[songID] ?? [:]
    }
    public func observe(songID: String, observation: MusicSearchObservation, sourceKeys: [String]) {
        // A failed disk read is a recoverable cache miss, not permission to
        // overwrite unread storage. The in-memory query status remains usable.
        _ = prepare(songID)
        document.observe(songID: songID, observation: observation, sourceKeys: sourceKeys, maximumBytes: maximumBytes)
        dirty.insert(songID)
    }
    public func persist(retaining songIDs: [String]) {
        let retained = Set(songIDs)
        let savedIDs = dirty.intersection(retained).filter { prepare($0) }
        let records = savedIDs.compactMap { id -> (String, StoredRecord)? in
            guard let platforms = document.records[id] else { return nil }
            return (id, .init(storedBytes: SourceDiscoveryDocument.bytes(platforms), platforms: platforms))
        }
        do {
            let record = MusicStorageRecords.music_source_discovery
            try database.transactionSync(ownedBytes: records.reduce(0) { $0 + $1.1.storedBytes }) { db in
                for row in try db.query("SELECT song_id FROM music_source_discovery") {
                    let id = try row.text("song_id")
                    if !retained.contains(id) { try db.execute("DELETE FROM music_source_discovery WHERE song_id=?", [.text(id)]) }
                }
                for (id, saved) in records {
                    let value = try RuntimeJSON.decode(JSONEncoder().encode(saved)), key: [SQLiteValue] = [.text(id)]
                    try record.save(db, value: value, previous: record.read(db, key: key), key: key)
                }
                let rows = try db.query("SELECT song_id,storedBytes FROM music_source_discovery ORDER BY " +
                    "(SELECT max(checkedAt) FROM music_source_discovery_platforms WHERE music_source_discovery_platforms.song_id=music_source_discovery.song_id)")
                var bytes = try rows.reduce(0) { try $0 + $1.integer("storedBytes") }
                for row in rows where bytes > maximumBytes {
                    try db.execute("DELETE FROM music_source_discovery WHERE song_id=?", [row["song_id"]])
                    bytes -= try row.integer("storedBytes")
                }
            }
            dirty.subtract(savedIDs); dirty.formIntersection(document.records.keys)
        } catch {}
    }
}

enum MusicSourceDiscoveryWindow {
    static let playback: Double = 24 * 60 * 60 * 1000
    static let librarySync: Double = 30 * 24 * 60 * 60 * 1000
    static let recheck: Double = 7 * 24 * 60 * 60 * 1000
}
struct MusicSourceDiscoveryCheck: Sendable {
    let checkedAt: Double
    let observations: [MusicSearchObservation]
}
struct MusicSourceDiscoveryState: Sendable {
    var entries: [String: MusicSourceDiscoveryEntry] = [:]
    var checks: [String: MusicSourceDiscoveryCheck] = [:]
    var excluded: Set<String> = []
    var at: Double = 0
    func hasCurrentCheck(_ platform: String, window: Double) -> Bool {
        checks[platform].map { at - $0.checkedAt < window } ?? false
    }
    func current(window: Double) -> [String: MusicSourceDiscoveryEntry] {
        entries.filter { _, value in
            !value.sourceKeys.isEmpty || value.observations.contains {
                $0.scope == .recording && $0.outcome == .notFound && at - $0.checkedAt < window
            }
        }
    }
}

extension NativeMusicService {
    func sourceDiscoveryState(_ song: MJ, query: String? = nil, context: String = "public") async throws -> MusicSourceDiscoveryState {
        let own = epoch, id = song["songId"].string
        let stored = await sourceDiscovery.entries(songID: id)
        try check(own)
        let current = try repositorySong(id, read()), at = now().timeIntervalSince1970 * 1000
        let excluded = try recordingSourceExclusions(current)
        let knownKeys = Set(current["sources"].array.map { $0["sourceKey"].string }).subtracting(excluded)
        let query = MusicRecordingIdentity.normalize(query ?? sourceDiscoveryQuery(current))
        var result = MusicSourceDiscoveryState(excluded: excluded, at: at)
        for (platform, entry) in stored {
            let observations = entry.observations.filter {
                $0.context == context && ($0.outcome == .found ||
                    $0.scope == .recording && MusicRecordingIdentity.normalize($0.query) == query)
            }
            let successful = observations.filter { $0.outcome == .found || $0.outcome == .notFound }
            if let last = successful.max(by: { $1.isNewer(than: $0) }) {
                result.checks[platform] = .init(checkedAt: last.checkedAt, observations: successful)
            }
            let keys = entry.sourceKeys.filter(knownKeys.contains)
            if entry.sourceKeys.isEmpty || !keys.isEmpty {
                result.entries[platform] = .init(checkedAt: entry.checkedAt, sourceKeys: keys, observations: observations)
            }
        }
        return result
    }
    func admitRecordingSource(_ song: MJ, sourceKey: String) async throws {
        try musicRequire(!recordingSourceExclusions(song).contains(sourceKey), .sourceUnavailable)
    }
    func recordSourceDiscovery(_ song: MJ, platform: String, rows: [MJ], observation: MusicSearchObservation) async throws {
        let own = epoch, id = song["songId"].string
        let input = rows.map { source -> MJ in
            let info = MusicModel.sourceData(source)
            var value = MJ.obj([("title", info["name"]), ("artist", info["singer"]), ("album", info["albumName"]),
                ("duration", info["duration"]), ("cover_url", info["img"]), ("sources", .array([source]))])
            if info["artistNames"].isArray { value["artistNames"] = info["artistNames"] }
            return value
        }
        // This path applies immediately after the caller's lifetime/queue
        // check. Do not suspend between that check and accepting source facts.
        let adopted = try adoptSearchSongs(input, observation: observation, checks: [:])
        let keys = adopted.filter { $0["songId"].string == id }.flatMap { value in
            value["sources"].array.filter { $0["source"].string == platform }.map { $0["sourceKey"].string }
        }
        noteSourceObservation(observation, songID: id)
        await sourceDiscovery.observe(songID: id, observation: observation, sourceKeys: Array(Set(keys)).sorted())
        try check(own); scheduleSourceDiscoveryPersist()
    }
    func recordSourceDiscovery(_ song: MJ, platform: String, page: MusicNativeTrackPage,
                               observation: MusicSearchObservation) async throws {
        let rows = try page.items.map(MusicModel.normalize).flatMap { $0["sources"].array }
        try await recordSourceDiscovery(song, platform: platform, rows: rows, observation: observation)
    }
    func scheduleSourceDiscoveryPersist() {
        guard sourceDiscovery.persistsRecords, !closed else { return }
        sourceDiscoveryPersistPending = true
        guard sourceDiscoveryPersistTask == nil else { return }
        sourceDiscoveryPersistTask = Task { await self.runSourceDiscoveryPersist() }
    }
    private func runSourceDiscoveryPersist() async {
        defer { sourceDiscoveryPersistTask = nil }
        while sourceDiscoveryPersistPending {
            sourceDiscoveryPersistPending = false
            guard let state = try? read() else { sourceDiscoveryPersistPending = true; return }
            let ids = state.library["library"]["collections"].array.filter { !$0["deleted"].bool }.flatMap { $0["order"].strings }
            await sourceDiscovery.persist(retaining: ids)
        }
    }
    func flushSourceDiscoveryPersist() async {
        if let task = sourceDiscoveryPersistTask { await task.value }
        if sourceDiscoveryPersistPending, sourceDiscoveryPersistTask == nil { scheduleSourceDiscoveryPersist() }
        if let task = sourceDiscoveryPersistTask { await task.value }
    }
}
