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
        while let data = try? JSONEncoder().encode(self), data.count > maximumBytes,
              let oldest = records.min(by: {
                  ($0.value.values.map(\.checkedAt).max() ?? 0) < ($1.value.values.map(\.checkedAt).max() ?? 0)
              })?.key { records.removeValue(forKey: oldest) }
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
    private let factory: @Sendable () throws -> DocumentStore
    private let maximumBytes: Int
    private var store: DocumentStore?
    private var loaded = false
    private var document = SourceDiscoveryDocument()
    private var persisted: Data?
    public init(storeFactory: @escaping @Sendable () throws -> DocumentStore, maximumBytes: Int = 4 * 1024 * 1024) {
        factory = storeFactory; self.maximumBytes = maximumBytes
    }
    private func prepare() -> Bool {
        if loaded { return true }
        do {
            if store == nil { store = try factory() }
            if let data = try store?.read(key: "checks"), data.count <= maximumBytes {
                persisted = data
                var saved = (try? JSONDecoder().decode(SourceDiscoveryDocument.self, from: data)) ?? .init()
                for (id, platforms) in document.records {
                    for entry in platforms.values {
                        for observation in entry.observations.sorted(by: { $1.isNewer(than: $0) }) {
                            saved.observe(songID: id, observation: observation, sourceKeys: entry.sourceKeys,
                                          maximumBytes: maximumBytes)
                        }
                    }
                }
                document = saved
            }
            loaded = true; return true
        } catch { return false }
    }
    public func entries() -> [String: [String: MusicSourceDiscoveryEntry]] {
        _ = prepare(); return document.records
    }
    public func observe(songID: String, observation: MusicSearchObservation, sourceKeys: [String]) {
        // A failed disk read is a recoverable cache miss, not permission to
        // overwrite unread storage. The in-memory query status remains usable.
        _ = prepare()
        document.observe(songID: songID, observation: observation, sourceKeys: sourceKeys, maximumBytes: maximumBytes)
    }
    public func persist(retaining songIDs: [String]) {
        guard prepare() else { return }
        let retained = Set(songIDs)
        let subset = SourceDiscoveryDocument(records: document.records.filter { retained.contains($0.key) })
        let encoder = JSONEncoder()
        encoder.outputFormatting = .sortedKeys
        guard let data = try? encoder.encode(subset), data.count <= maximumBytes, data != persisted else { return }
        do {
            try store?.write(data, key: "checks")
            persisted = data
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
