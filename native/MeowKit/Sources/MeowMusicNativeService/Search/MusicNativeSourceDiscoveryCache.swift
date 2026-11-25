import Foundation
import MeowDebug
import MeowMusicFeature
import MeowStorage

struct MusicSourceDiscoveryObservation: Sendable {
    let platform: String
    let page: MusicNativeTrackPage?
    let failure: MusicServiceError?
}

enum MusicSourceDiscoveryEligibility {
    /// Only a durable recording identity can be matched across platforms.
    static func searchable(_ song: MusicSongRecord) -> Bool {
        MusicModel.descriptor(song).lookupKey != nil && MusicModel.descriptor(song).duration != nil
    }
    static func needsPlatform(_ platform: String, song: MusicSongRecord, force: Bool,
                              cache: MusicSourceDiscoveryEntry? = nil) -> Bool {
        guard MusicModel.sites.contains(platform) else { return false }
        if force { return true }
        if cache != nil { return false }
        let rows = song.sources.filter { $0.platform == platform }
        return rows.isEmpty || rows.contains { !metadataComplete($0) }
    }
    static func metadataComplete(_ source: MusicSongSourceRecord) -> Bool {
        func known(_ value: String?) -> Bool {
            let text = MusicModel.norm(value ?? "")
            return !text.isEmpty && !text.hasPrefix("未知") && !text.hasPrefix("unknown")
        }
        let duration = source.duration ?? 0
        return known(source.name) && known(source.singer) && duration.isFinite && duration > 0
    }
}

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

public actor MusicNativePersistentSourceDiscoveryCache {
    private let database: SQLiteDatabase
    private let maximumBytes: Int
    private var loaded: Set<String> = []
    private var allLoaded = false
    private var dirty: Set<String> = []
    private var state = SourceDiscoveryState()
    public init(database: SQLiteDatabase, maximumBytes: Int = 4 * 1024 * 1024) {
        self.database = database; self.maximumBytes = maximumBytes
    }
    private static func nowMillis() -> Int64 {
        Int64((Date().timeIntervalSince1970 * 1000).rounded())
    }
    private func mergeSaved(_ saved: [String: MusicSourceDiscoveryEntry], for songID: String) {
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
    private func prepare(_ songID: String) -> Bool {
        if allLoaded || loaded.contains(songID) { return true }
        do {
            let nowMs = Self.nowMillis()
            let saved = try database.readSync { db in
                try SQLiteDatabase.readSnapshot(db) { db in
                    try MusicSourceDiscoveryStore.read(db, songID: songID, nowMs: nowMs)
                }
            }
            if let saved { mergeSaved(saved, for: songID) }
            loaded.insert(songID); return true
        } catch {
            DebugRuntime.logError(.storage, sub: "music.discoveryCache", event: "prepare.error", error: error)
            return false
        }
    }
    public func entries() -> [String: [String: MusicSourceDiscoveryEntry]] {
        if !allLoaded {
            let nowMs = Self.nowMillis()
            do {
                let all = try database.readSync { db in
                    try SQLiteDatabase.readSnapshot(db) { try MusicSourceDiscoveryStore.readAll($0, nowMs: nowMs) }
                }
                for (songID, saved) in all where !loaded.contains(songID) {
                    mergeSaved(saved, for: songID)
                    loaded.insert(songID)
                }
                allLoaded = true
            } catch {
                DebugRuntime.logError(.storage, sub: "music.discoveryCache", event: "readAll.error", error: error)
            }
        }
        return state.records
    }
    public func entries(songID: String) -> [String: MusicSourceDiscoveryEntry] {
        _ = prepare(songID)
        return state.records[songID] ?? [:]
    }
    public func observe(songID: String, observation: MusicSearchObservation, sourceKeys: [String]) {
        _ = prepare(songID)
        state.observe(songID: songID, observation: observation, sourceKeys: sourceKeys, maximumBytes: maximumBytes)
        dirty.insert(songID)
    }
    public func persist(retaining songIDs: [String]) {
        let retained = Set(songIDs)
        // Evict unretained songs from in-memory state as well as SQLite.
        for id in Array(state.records.keys) where !retained.contains(id) {
            state.records.removeValue(forKey: id)
            loaded.remove(id)
            dirty.remove(id)
        }
        let savedIDs = dirty.intersection(retained).filter { prepare($0) }
        let records = savedIDs.compactMap { id -> (songID: String, storedBytes: Int, platforms: [String: MusicSourceDiscoveryEntry])? in
            guard let platforms = state.records[id] else { return nil }
            return (id, SourceDiscoveryState.bytes(platforms), platforms)
        }
        do {
            let nowMs = Self.nowMillis()
            try database.transactionSync { db in
                try MusicSourceDiscoveryStore.persist(db, retained: retained, records: records,
                                                      maximumBytes: maximumBytes, nowMs: nowMs)
            }
            dirty.subtract(savedIDs); dirty.formIntersection(state.records.keys)
        } catch {
            DebugRuntime.logError(.storage, sub: "music.discoveryCache", event: "persist.error", error: error)
        }
    }
    public func clear() throws {
        state.records.removeAll()
        loaded.removeAll()
        dirty.removeAll()
        allLoaded = true
        try database.transactionSync { db in
            try MusicSourceDiscoveryStore.clear(db)
        }
    }
    public func debugStats() -> DebugJSONValue {
        let dbStats = (try? database.readSync { try MusicSourceDiscoveryStore.stats($0) }) ?? (songCount: 0, storedBytes: 0)
        return .object([
            "songCount": .int(dbStats.songCount),
            "storedBytes": .integer(dbStats.storedBytes),
            "memorySongCount": .int(state.records.count),
            "dirtyCount": .int(dirty.count),
            "capacityBytes": .int(maximumBytes)
        ])
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
        guard !closed else { return }
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
