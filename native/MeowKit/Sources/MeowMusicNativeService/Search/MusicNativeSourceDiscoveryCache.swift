import Foundation
import MeowMusicFeature
import MeowStorage

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

private struct SourceDiscoveryState {
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
    private var state = SourceDiscoveryState()
    private let maximumBytes: Int
    public init(maximumBytes: Int = 4 * 1024 * 1024) { self.maximumBytes = maximumBytes }
    public func entries() -> [String: [String: MusicSourceDiscoveryEntry]] { state.records }
    public func observe(songID: String, observation: MusicSearchObservation, sourceKeys: [String]) {
        state.observe(songID: songID, observation: observation, sourceKeys: sourceKeys, maximumBytes: maximumBytes)
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
    private var state = SourceDiscoveryState()
    public init(database: SQLiteDatabase, maximumBytes: Int = 4 * 1024 * 1024) {
        self.database = database; self.maximumBytes = maximumBytes
    }
    private func prepare(_ songID: String) -> Bool {
        if loaded.contains(songID) { return true }
        do {
            let saved = try database.readSync { db in
                try SQLiteDatabase.readSnapshot(db) { db in
                    try MusicSourceDiscoveryStore.read(db, songID: songID)
                }
            }
            if let saved {
                var combined = SourceDiscoveryState(records: [songID: saved])
                if let platforms = state.records[songID] {
                    for entry in platforms.values {
                        for observation in entry.observations.sorted(by: { $1.isNewer(than: $0) }) {
                            combined.observe(songID: songID, observation: observation, sourceKeys: entry.sourceKeys,
                                          maximumBytes: maximumBytes)
                        }
                    }
                }
                state.records[songID] = combined.records[songID]
            }
            loaded.insert(songID); return true
        } catch { return false }
    }
    public func entries() -> [String: [String: MusicSourceDiscoveryEntry]] {
        if let ids = try? database.readSync({ try MusicSourceDiscoveryStore.allSongIDs($0) }) {
            for id in ids { _ = prepare(id) }
        }
        return state.records
    }
    public func entries(songID: String) -> [String: MusicSourceDiscoveryEntry] {
        _ = prepare(songID)
        return state.records[songID] ?? [:]
    }
    public func observe(songID: String, observation: MusicSearchObservation, sourceKeys: [String]) {
        // A failed disk read is a recoverable cache miss, not permission to
        // overwrite unread storage. The in-memory query status remains usable.
        _ = prepare(songID)
        state.observe(songID: songID, observation: observation, sourceKeys: sourceKeys, maximumBytes: maximumBytes)
        dirty.insert(songID)
    }
    public func persist(retaining songIDs: [String]) {
        let retained = Set(songIDs)
        let savedIDs = dirty.intersection(retained).filter { prepare($0) }
        let records = savedIDs.compactMap { id -> (songID: String, storedBytes: Int, platforms: [String: MusicSourceDiscoveryEntry])? in
            guard let platforms = state.records[id] else { return nil }
            return (id, SourceDiscoveryState.bytes(platforms), platforms)
        }
        do {
            try database.transactionSync(ownedBytes: records.reduce(0) { $0 + $1.storedBytes }) { db in
                try MusicSourceDiscoveryStore.persist(db, retained: retained, records: records, maximumBytes: maximumBytes)
            }
            dirty.subtract(savedIDs); dirty.formIntersection(state.records.keys)
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
    func sourceDiscoveryState(_ song: MusicSongRecord, query: String? = nil, context: String = "public") async throws -> MusicSourceDiscoveryState {
        let own = epoch, id = song.songID
        let stored = await sourceDiscovery.entries(songID: id)
        try check(own)
        let current = try repositorySong(id, read()), at = now().timeIntervalSince1970 * 1000
        let excluded = try recordingSourceExclusions(current)
        let knownKeys = Set(current.sources.map(\.sourceKey)).subtracting(excluded)
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
    func admitRecordingSource(_ song: MusicSongRecord, sourceKey: String) async throws {
        try musicRequire(!recordingSourceExclusions(song).contains(sourceKey), .sourceUnavailable)
    }
    func recordSourceDiscovery(_ song: MusicSongRecord, platform: String, rows: [MusicSongSourceRecord], observation: MusicSearchObservation) async throws {
        let own = epoch, id = song.songID
        let input = rows.map { source -> MusicSongRecord in
            MusicSongRecord(
                title: source.name ?? "",
                artist: source.singer ?? "",
                album: source.albumName ?? "",
                duration: source.duration,
                coverURL: source.img ?? "",
                artistNames: source.artistNames,
                sources: [source]
            )
        }
        // This path applies immediately after the caller's lifetime/queue
        // check. Do not suspend between that check and accepting source facts.
        let adopted = try adoptSearchSongs(input, observation: observation, checks: [:])
        let keys = adopted.filter { $0.songID == id }.flatMap { value in
            value.sources.filter { $0.platform == platform }.map(\.sourceKey)
        }
        noteSourceObservation(observation, songID: id)
        await sourceDiscovery.observe(songID: id, observation: observation, sourceKeys: Array(Set(keys)).sorted())
        try check(own); scheduleSourceDiscoveryPersist()
    }
    func recordSourceDiscovery(_ song: MusicSongRecord, platform: String, page: MusicNativeTrackPage,
                               observation: MusicSearchObservation) async throws {
        let rows = try page.items.map(MusicModel.normalize).flatMap(\.sources)
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
            let ids = state.library.collections.filter { !$0.deleted }.flatMap(\.order)
            await sourceDiscovery.persist(retaining: ids)
        }
    }
    func flushSourceDiscoveryPersist() async {
        if let task = sourceDiscoveryPersistTask { await task.value }
        if sourceDiscoveryPersistPending, sourceDiscoveryPersistTask == nil { scheduleSourceDiscoveryPersist() }
        if let task = sourceDiscoveryPersistTask { await task.value }
    }
}
