// SPDX-License-Identifier: GPL-3.0-only
import Foundation
import MeowMusicFeature
import MeowStorage

/// A clean source row stored outside the song model. musicInfo remains JSON
/// text so the cache has no second locator or credential representation.
public struct MusicSourceDiscoverySource: Codable, Equatable, Sendable {
    public let sourceKey: String
    public let source: String
    public let musicInfo: String
    public init(sourceKey: String, source: String, musicInfo: String) {
        self.sourceKey = sourceKey; self.source = source; self.musicInfo = musicInfo
    }
}

public struct MusicSourceDiscoveryEntry: Equatable, Sendable {
    public let checkedAt: Double
    public let sources: [MusicSourceDiscoverySource]
    public init(checkedAt: Double, sources: [MusicSourceDiscoverySource]) {
        self.checkedAt = checkedAt; self.sources = sources
    }
}

/// One recording as keyed by the cache: fingerprint plus compatible duration.
public struct MusicSourceDiscoveryRecording: Hashable, Sendable {
    public let fingerprint: String
    public let duration: Double
    public init(fingerprint: String, duration: Double) {
        self.fingerprint = fingerprint; self.duration = duration
    }
}

/// A disposable per-recording discovery cache. Reads and writes are served from
/// memory; only `persist(retaining:)` writes durable state, limited to the given
/// (local library) recordings. A failed read is a miss and a failed write is
/// ignored; neither can reject playback.
public protocol MusicSourceDiscoveryCaching: Sendable {
    /// False when `persist` has no durable effect, so callers can skip computing its input.
    var persistsRecords: Bool { get }
    func entries(fingerprint: String, duration: Double) async -> [String: MusicSourceDiscoveryEntry]
    func record(fingerprint: String, duration: Double, platform: String, checkedAt: Double,
                sources: [MusicSourceDiscoverySource]) async
    /// Source keys the user confirmed are not this recording.
    func excluded(fingerprint: String, duration: Double) async -> Set<String>
    func exclude(fingerprint: String, duration: Double, sourceKey: String) async
    func persist(retaining recordings: [MusicSourceDiscoveryRecording]) async
}

private enum MusicSourceDiscoveryLimits {
    static let maximumSources = 16

    static func valid(fingerprint: String, duration: Double) -> Bool {
        !fingerprint.isEmpty && fingerprint.utf8.count <= 4 * 1024 &&
            duration.isFinite && (0...604_800).contains(duration)
    }

    static func validKey(_ key: String) -> Bool { !key.isEmpty && key.utf8.count <= 1024 }

    static func durationCompatible(_ cached: Double, _ requested: Double) -> Bool {
        MusicRecordingIdentity(title: "", artist: "", duration: cached)
            .isCompatible(with: .init(title: "", artist: "", duration: requested), sameNative: true)
    }

    static func row(_ source: MusicSourceDiscoverySource, platform: String) -> MJ? {
        guard source.source == platform, source.sourceKey.utf8.count <= 1024,
              source.musicInfo.utf8.count <= 16 * 1024,
              let info = try? MJ.parse(Data(source.musicInfo.utf8), maximum: 16 * 1024),
              (try? MusicModel.rejectSecrets(info)) != nil,
              let clean = try? MusicModel.cleanSource(.obj([("sourceKey", .string(source.sourceKey)),
                ("source", .string(source.source)), ("musicInfo", info)])),
              clean["sourceKey"].string == source.sourceKey,
              clean["source"].string == platform else { return nil }
        return clean
    }

    static func sources(_ sources: [MusicSourceDiscoverySource], platform: String) -> [MusicSourceDiscoverySource]? {
        guard MusicNativeSearchProtocol.platforms.contains(platform), sources.count <= maximumSources else { return nil }
        var result: [MusicSourceDiscoverySource] = []
        for source in sources {
            guard let clean = row(source, platform: platform) else { return nil }
            result.append(.init(sourceKey: clean["sourceKey"].string, source: platform,
                                musicInfo: clean["musicInfo"].json))
        }
        return result
    }

    static func keys(_ values: [String]) -> [String] { Set(values.filter(validKey)).sorted() }
}

private struct SourceDiscoveryRecord: Codable {
    var fingerprint: String
    var duration: Double
    var updatedAt: Double
    var platforms: [String: SourceDiscoveryPlatform]
    /// Sorted, unique; current writers include the empty array.
    var excluded: [String]

    func matches(fingerprint: String, duration: Double) -> Bool {
        self.fingerprint == fingerprint &&
            MusicSourceDiscoveryLimits.valid(fingerprint: self.fingerprint, duration: self.duration) &&
            MusicSourceDiscoveryLimits.durationCompatible(self.duration, duration)
    }
}

private struct SourceDiscoveryPlatform: Codable {
    var checkedAt: Double
    var sources: [MusicSourceDiscoverySource]
}

private struct SourceDiscoveryEnvelope: Codable {
    var version = 1
    var records: [String: SourceDiscoveryRecord] = [:]

    private func stored(fingerprint: String, duration: Double) -> SourceDiscoveryRecord? {
        guard MusicSourceDiscoveryLimits.valid(fingerprint: fingerprint, duration: duration),
              let record = records[MusicRecordingIdentity.hash(fingerprint)],
              record.matches(fingerprint: fingerprint, duration: duration) else { return nil }
        return record
    }

    /// No age filter here: each consumer applies its own window to empty checks.
    func entries(fingerprint: String, duration: Double, at: Double) -> [String: MusicSourceDiscoveryEntry] {
        guard at.isFinite, let record = stored(fingerprint: fingerprint, duration: duration) else { return [:] }
        var result: [String: MusicSourceDiscoveryEntry] = [:]
        for (platform, value) in record.platforms {
            guard value.checkedAt.isFinite, value.checkedAt > 0, value.checkedAt <= at,
                  let sources = MusicSourceDiscoveryLimits.sources(value.sources, platform: platform) else { continue }
            result[platform] = .init(checkedAt: value.checkedAt, sources: sources)
        }
        return result
    }

    func excluded(fingerprint: String, duration: Double) -> Set<String> {
        Set(stored(fingerprint: fingerprint, duration: duration)?.excluded ?? [])
    }

    private func current(fingerprint: String, duration: Double, at: Double) -> SourceDiscoveryRecord {
        let key = MusicRecordingIdentity.hash(fingerprint)
        if let record = records[key], record.fingerprint == fingerprint,
           MusicSourceDiscoveryLimits.durationCompatible(record.duration, duration) { return record }
        return .init(fingerprint: fingerprint, duration: duration, updatedAt: at, platforms: [:], excluded: [])
    }

    mutating func record(fingerprint: String, duration: Double, platform: String, checkedAt: Double,
                         at: Double, sources: [MusicSourceDiscoverySource]) -> Bool {
        guard MusicSourceDiscoveryLimits.valid(fingerprint: fingerprint, duration: duration),
              checkedAt.isFinite, checkedAt > 0, checkedAt <= at,
              let sources = MusicSourceDiscoveryLimits.sources(sources, platform: platform) else { return false }
        var record = current(fingerprint: fingerprint, duration: duration, at: checkedAt)
        if let existing = record.platforms[platform], existing.checkedAt > checkedAt { return false }
        // Preserve positive timing evidence: a later incomplete observation must
        // not relabel other platforms' successful checks as another recording.
        if record.duration <= 0 { record.duration = duration }
        record.updatedAt = max(record.updatedAt, checkedAt)
        record.platforms[platform] = .init(checkedAt: checkedAt, sources: sources)
        records[MusicRecordingIdentity.hash(fingerprint)] = record
        return true
    }

    mutating func exclude(fingerprint: String, duration: Double, sourceKey: String, at: Double) -> Bool {
        guard MusicSourceDiscoveryLimits.valid(fingerprint: fingerprint, duration: duration),
              MusicSourceDiscoveryLimits.validKey(sourceKey), at.isFinite, at > 0 else { return false }
        var record = current(fingerprint: fingerprint, duration: duration, at: at)
        var keys = Set(record.excluded)
        guard keys.insert(sourceKey).inserted else { return false }
        if record.duration <= 0 { record.duration = duration }
        record.excluded = keys.sorted()
        record.updatedAt = max(record.updatedAt, at)
        records[MusicRecordingIdentity.hash(fingerprint)] = record
        return true
    }

    /// Merges an older durable document into newer memory: per platform the most
    /// recent check wins and exclusions accumulate. A stored record of another
    /// recording under the same key is older evidence and is discarded.
    mutating func absorb(_ stored: SourceDiscoveryEnvelope) {
        for (key, disk) in stored.records {
            guard MusicSourceDiscoveryLimits.valid(fingerprint: disk.fingerprint, duration: disk.duration) else { continue }
            var clean = disk
            clean.excluded = MusicSourceDiscoveryLimits.keys(disk.excluded)
            guard var memory = records[key] else { records[key] = clean; continue }
            guard memory.fingerprint == clean.fingerprint,
                  MusicSourceDiscoveryLimits.durationCompatible(memory.duration, clean.duration) else { continue }
            for (platform, value) in clean.platforms where value.checkedAt > (memory.platforms[platform]?.checkedAt ?? 0) {
                memory.platforms[platform] = value
            }
            let keys = Set(memory.excluded).union(clean.excluded)
            memory.excluded = keys.sorted()
            if memory.duration <= 0 { memory.duration = clean.duration }
            memory.updatedAt = max(memory.updatedAt, clean.updatedAt)
            records[key] = memory
        }
    }

    func retaining(_ recordings: [MusicSourceDiscoveryRecording]) -> SourceDiscoveryEnvelope {
        let wanted = Dictionary(grouping: recordings, by: \.fingerprint)
        var value = self
        value.records = records.filter { _, record in
            wanted[record.fingerprint]?.contains { record.matches(fingerprint: $0.fingerprint, duration: $0.duration) } ?? false
        }
        return value
    }

    /// Applies a change only if the result still fits the byte budget (trimming
    /// the oldest recordings); otherwise memory is left unchanged.
    mutating func apply(maximumBytes: Int, _ change: (inout SourceDiscoveryEnvelope) -> Bool) {
        var next = self
        guard change(&next), SourceDiscoveryDocument.encode(&next, maximumBytes: maximumBytes) != nil else { return }
        self = next
    }
}

private enum SourceDiscoveryDocument {
    static func load(_ data: Data?, maximumBytes: Int) -> SourceDiscoveryEnvelope? {
        guard let data, data.count <= maximumBytes,
              let envelope = try? JSONDecoder().decode(SourceDiscoveryEnvelope.self, from: data),
              envelope.version == 1 else { return nil }
        return envelope
    }

    static func encode(_ envelope: inout SourceDiscoveryEnvelope, maximumBytes: Int) -> Data? {
        guard maximumBytes > 0 else { return nil }
        var value = envelope
        value.records = value.records.filter { _, record in
            MusicSourceDiscoveryLimits.valid(fingerprint: record.fingerprint, duration: record.duration) &&
                record.updatedAt.isFinite
        }
        let encoder = JSONEncoder(); encoder.outputFormatting = [.sortedKeys, .withoutEscapingSlashes]
        guard let data = try? encoder.encode(value) else { return nil }
        if data.count <= maximumBytes { envelope = value; return data }
        let oldest = value.records.sorted {
            $0.value.updatedAt == $1.value.updatedAt ? $0.key < $1.key : $0.value.updatedAt < $1.value.updatedAt
        }
        // Account for exact JSON row/key bytes, then encode once after eviction
        // rather than repeatedly encoding the whole document for every old row.
        var bytes = data.count
        for (key, record) in oldest {
            guard let row = try? encoder.encode(record), let encodedKey = try? encoder.encode(key) else { return nil }
            bytes -= row.count + encodedKey.count + 1 + (value.records.count > 1 ? 1 : 0)
            value.records.removeValue(forKey: key)
            if bytes <= maximumBytes { break }
        }
        guard let trimmed = try? encoder.encode(value), trimmed.count <= maximumBytes else { return nil }
        envelope = value
        return trimmed
    }
}

/// Persistent implementation for the product, an ordinary cache document. No
/// credentials, audio URLs or handles are stored; every source row is
/// reconstructed through MusicModel.cleanSource. Memory is authoritative; the
/// stored document is merged in once and only the retained (library) subset is
/// written back.
public actor MusicNativePersistentSourceDiscoveryCache: MusicSourceDiscoveryCaching {
    public static let namespace = "music.source-discovery.v1"
    private static let key = "records"
    private let factory: @Sendable () throws -> DocumentStore
    private let now: @Sendable () -> Date
    private let maximumBytes: Int
    private var document: DocumentStore?
    private var envelope = SourceDiscoveryEnvelope()
    private var loaded = false
    /// Bytes known to be on disk; nil forces the next persist to write.
    private var durable: Data?
    public nonisolated var persistsRecords: Bool { true }

    public init(storeFactory: @escaping @Sendable () throws -> DocumentStore,
                clock: @escaping @Sendable () -> Date = { Date() },
                maximumBytes: Int = 4 * 1024 * 1024) {
        factory = storeFactory; now = clock; self.maximumBytes = maximumBytes
    }

    private func store() throws -> DocumentStore {
        if let document { return document }
        let value = try factory(); document = value; return value
    }
    /// A failed read stays retryable and memory keeps serving. An undecodable
    /// document counts as loaded so the next persist replaces it.
    private func prepare() -> Bool {
        if loaded { return true }
        let data: Data?
        do { data = try store().read(key: Self.key) } catch { return false }
        loaded = true
        if let data {
            if let stored = SourceDiscoveryDocument.load(data, maximumBytes: maximumBytes) {
                var merged = envelope
                merged.absorb(stored)
                if SourceDiscoveryDocument.encode(&merged, maximumBytes: maximumBytes) != nil { envelope = merged }
                durable = data
            }
        } else {
            var empty = SourceDiscoveryEnvelope()
            durable = SourceDiscoveryDocument.encode(&empty, maximumBytes: maximumBytes)
        }
        return true
    }
    public func entries(fingerprint: String, duration: Double) async -> [String: MusicSourceDiscoveryEntry] {
        guard !Task.isCancelled else { return [:] }
        _ = prepare()
        return envelope.entries(fingerprint: fingerprint, duration: duration, at: now().timeIntervalSince1970 * 1000)
    }
    public func excluded(fingerprint: String, duration: Double) async -> Set<String> {
        _ = prepare()
        return envelope.excluded(fingerprint: fingerprint, duration: duration)
    }
    public func record(fingerprint: String, duration: Double, platform: String, checkedAt: Double,
                       sources: [MusicSourceDiscoverySource]) async {
        guard !Task.isCancelled else { return }
        _ = prepare()
        let at = now().timeIntervalSince1970 * 1000
        envelope.apply(maximumBytes: maximumBytes) {
            $0.record(fingerprint: fingerprint, duration: duration, platform: platform,
                      checkedAt: checkedAt, at: at, sources: sources)
        }
    }
    public func exclude(fingerprint: String, duration: Double, sourceKey: String) async {
        _ = prepare()
        let at = now().timeIntervalSince1970 * 1000
        envelope.apply(maximumBytes: maximumBytes) {
            $0.exclude(fingerprint: fingerprint, duration: duration, sourceKey: sourceKey, at: at)
        }
    }
    /// Never overwrites a document that could not be read and merged.
    public func persist(retaining recordings: [MusicSourceDiscoveryRecording]) async {
        guard prepare() else { return }
        var subset = envelope.retaining(recordings)
        guard let data = SourceDiscoveryDocument.encode(&subset, maximumBytes: maximumBytes),
              data != durable else { return }
        do { try store().write(data, key: Self.key); durable = data }
        catch { durable = nil }
    }
}

/// Test and default implementation. It applies the same validation and byte
/// budget without touching the filesystem; nothing is persisted.
public actor MusicNativeMemorySourceDiscoveryCache: MusicSourceDiscoveryCaching {
    private let now: @Sendable () -> Date
    private let maximumBytes: Int
    private var envelope = SourceDiscoveryEnvelope()
    public nonisolated var persistsRecords: Bool { false }

    public init(clock: @escaping @Sendable () -> Date = { Date() },
                maximumBytes: Int = 4 * 1024 * 1024) {
        now = clock; self.maximumBytes = maximumBytes
    }
    public func entries(fingerprint: String, duration: Double) async -> [String: MusicSourceDiscoveryEntry] {
        guard !Task.isCancelled else { return [:] }
        return envelope.entries(fingerprint: fingerprint, duration: duration, at: now().timeIntervalSince1970 * 1000)
    }
    public func excluded(fingerprint: String, duration: Double) async -> Set<String> {
        envelope.excluded(fingerprint: fingerprint, duration: duration)
    }
    public func record(fingerprint: String, duration: Double, platform: String, checkedAt: Double,
                       sources: [MusicSourceDiscoverySource]) async {
        guard !Task.isCancelled else { return }
        let at = now().timeIntervalSince1970 * 1000
        envelope.apply(maximumBytes: maximumBytes) {
            $0.record(fingerprint: fingerprint, duration: duration, platform: platform,
                      checkedAt: checkedAt, at: at, sources: sources)
        }
    }
    public func exclude(fingerprint: String, duration: Double, sourceKey: String) async {
        let at = now().timeIntervalSince1970 * 1000
        envelope.apply(maximumBytes: maximumBytes) {
            $0.exclude(fingerprint: fingerprint, duration: duration, sourceKey: sourceKey, at: at)
        }
    }
    public func persist(retaining recordings: [MusicSourceDiscoveryRecording]) async {}
}

/// Retry windows for empty (no compatible source) platform checks. Checks that
/// found a compatible source never expire by age.
enum MusicSourceDiscoveryWindow {
    static let playback: Double = 24 * 60 * 60 * 1000
    static let librarySync: Double = 30 * 24 * 60 * 60 * 1000
    /// Positive-match freshness for a conditional recheck; age alone triggers nothing.
    static let recheck: Double = 7 * 24 * 60 * 60 * 1000
}

/// Compatible, non-excluded cache evidence for one song.
struct MusicSourceDiscoveryState: Sendable {
    /// Original check times, including empty checks of any age.
    var entries: [String: MusicSourceDiscoveryEntry] = [:]
    /// Never contains the song's own primary source.
    var excluded: Set<String> = []
    var at: Double = 0
    func current(window: Double) -> [String: MusicSourceDiscoveryEntry] {
        entries.filter { !$0.value.sources.isEmpty || at - $0.value.checkedAt < window }
    }
}

extension NativeMusicService {
    /// Compatible, non-excluded cache evidence with original check times.
    func sourceDiscoveryState(_ song: MJ) async -> MusicSourceDiscoveryState {
        let at = now().timeIntervalSince1970 * 1000
        let descriptor = MusicModel.descriptor(song), fingerprint = descriptor.fingerprint
        guard !fingerprint.isEmpty else { return .init(at: at) }
        let excluded = await sourceDiscoveryExcluded(song, descriptor: descriptor)
        let stored = await sourceDiscovery.entries(fingerprint: fingerprint, duration: descriptor.duration)
        var result: [String: MusicSourceDiscoveryEntry] = [:]
        for (platform, value) in stored {
            guard MusicNativeSearchProtocol.platforms.contains(platform),
                  let entry = compatibleSourceDiscoveryEntry(song, platform: platform, entry: value, excluded: excluded)
            else { continue }
            result[platform] = entry
        }
        return .init(entries: result, excluded: excluded, at: at)
    }

    /// The user's "not this song" decisions for this recording. The song's own
    /// primary source defines the recording and is never excluded from it.
    func sourceDiscoveryExcluded(_ song: MJ) async -> Set<String> {
        await sourceDiscoveryExcluded(song, descriptor: MusicModel.descriptor(song))
    }
    private func sourceDiscoveryExcluded(_ song: MJ, descriptor: MusicRecordingIdentity) async -> Set<String> {
        guard !descriptor.fingerprint.isEmpty else { return [] }
        var keys = await sourceDiscovery.excluded(fingerprint: descriptor.fingerprint, duration: descriptor.duration)
        if let primary = song["sources"].array.first?["sourceKey"].string { keys.remove(primary) }
        return keys
    }

    func excludeSourceDiscovery(_ song: MJ, sourceKey: String) async {
        let descriptor = MusicModel.descriptor(song)
        guard !descriptor.fingerprint.isEmpty,
              song["sources"].array.first?["sourceKey"].string != sourceKey else { return }
        await sourceDiscovery.exclude(fingerprint: descriptor.fingerprint, duration: descriptor.duration,
                                      sourceKey: sourceKey)
        let songID = song["songId"].string
        sourceResults[songID]?.removeAll { $0.option.sourceKey == sourceKey }
        scheduleSourceDiscoveryPersist()
    }

    func admitRecordingSource(_ song: MJ, sourceKey: String) async throws {
        let excluded = await sourceDiscoveryExcluded(song)
        try check()
        try musicRequire(!excluded.contains(sourceKey), .sourceUnavailable)
    }

    func compatibleSourceDiscoveryEntry(_ song: MJ, platform: String, entry: MusicSourceDiscoveryEntry,
                                        excluded: Set<String> = []) -> MusicSourceDiscoveryEntry? {
        guard entry.checkedAt.isFinite, entry.checkedAt > 0,
              entry.sources.count <= MusicSourceDiscoveryLimits.maximumSources,
              entry.sources.allSatisfy({ $0.source == platform }) else { return nil }
        var accepted: [MusicSourceDiscoverySource] = [], rows: [MJ] = []
        for source in entry.sources where !excluded.contains(source.sourceKey) {
            guard let clean = MusicSourceDiscoveryLimits.row(source, platform: platform) else { return nil }
            guard MusicModel.compatible(song, clean),
                  song["sources"].array.allSatisfy({ MusicModel.compatible($0, clean) }),
                  rows.allSatisfy({ MusicModel.compatible($0, clean) }) else { continue }
            accepted.append(.init(sourceKey: clean["sourceKey"].string, source: platform,
                                  musicInfo: clean["musicInfo"].json))
            rows.append(clean)
        }
        return .init(checkedAt: entry.checkedAt, sources: accepted)
    }

    func mergeSourceDiscoveryCache(_ song: MJ, platform: String,
                                   entry: MusicSourceDiscoveryEntry) -> MJ {
        var result = song
        for source in entry.sources {
            guard let row = MusicSourceDiscoveryLimits.row(source, platform: platform),
                  let merged = try? MusicModel.mergeSources(result, adding: [row]) else { continue }
            result = merged
        }
        return result
    }

    func mergeSourceDiscoveryCache(_ song: MJ,
                                   entries: [String: MusicSourceDiscoveryEntry]) -> MJ {
        var result = song
        let count = entries.values.map { $0.sources.count }.max() ?? 0
        let platforms = MusicNativeSearchProtocol.platforms.sorted()
        // As with live discovery, album editions from one platform must not
        // consume every durable source slot before another platform is considered.
        for index in 0..<count {
            for platform in platforms {
                guard let entry = entries[platform], entry.sources.indices.contains(index) else { continue }
                result = mergeSourceDiscoveryCache(result, platform: platform,
                    entry: .init(checkedAt: entry.checkedAt, sources: [entry.sources[index]]))
            }
        }
        return result
    }

    func recordSourceDiscovery(_ song: MJ, platform: String, rows: [MJ]) async {
        let descriptor = MusicModel.descriptor(song), fingerprint = descriptor.fingerprint
        guard !fingerprint.isEmpty,
              MusicNativeSearchProtocol.platforms.contains(platform) else { return }
        var stored: [MusicSourceDiscoverySource] = [], accepted: [MJ] = []
        var positions: [String: Int] = [:]
        for raw in rows {
            guard (try? MusicModel.rejectSecrets(raw)) != nil,
                  let clean = try? MusicModel.cleanSource(raw), clean["source"].string == platform else { return }
            guard MusicModel.compatible(song, clean),
                  song["sources"].array.allSatisfy({ MusicModel.compatible($0, clean) }),
                  accepted.allSatisfy({ MusicModel.compatible($0, clean) }) else { continue }
            if let index = positions[clean["sourceKey"].string] {
                var existing = song
                existing["sources"] = .array([accepted[index]])
                guard let merged = try? MusicModel.mergeSources(existing, adding: [clean], preferIncoming: true),
                      let row = merged["sources"].array.first else { return }
                accepted[index] = row
                stored[index] = .init(sourceKey: row["sourceKey"].string, source: platform, musicInfo: row["musicInfo"].json)
                continue
            }
            guard stored.count < MusicSourceDiscoveryLimits.maximumSources else { continue }
            positions[clean["sourceKey"].string] = accepted.count
            stored.append(.init(sourceKey: clean["sourceKey"].string, source: platform,
                                musicInfo: clean["musicInfo"].json))
            accepted.append(clean)
        }
        await sourceDiscovery.record(fingerprint: fingerprint, duration: descriptor.duration,
            platform: platform, checkedAt: now().timeIntervalSince1970 * 1000,
            sources: stored)
        scheduleSourceDiscoveryPersist()
    }

    func recordSourceDiscovery(_ song: MJ, platform: String, page: MusicNativeTrackPage) async {
        guard page.total >= 0, page.items.allSatisfy({ $0.platform == platform }) else { return }
        var rows: [MJ] = []
        for value in page.items {
            guard let candidate = try? MusicModel.normalize(value),
                  let row = candidate["sources"].array.first else { return }
            rows.append(row)
        }
        await recordSourceDiscovery(song, platform: platform, rows: rows)
    }

    // MARK: Durable scope (aligned with Windows MusicSongMatcher)

    /// One owned persist task, rerun while new triggers arrive. Library commits,
    /// recorded checks, exclusions and close() trigger it.
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
            // A locked/closed document keeps the request pending for the next trigger.
            guard let state = try? read() else { sourceDiscoveryPersistPending = true; return }
            let memo = sourceDiscoveryRecordingMemo
            let (recordings, next) = await Self.sourceDiscoveryRecordings(state.library["library"], memo: memo)
            sourceDiscoveryRecordingMemo = next
            await sourceDiscovery.persist(retaining: recordings)
        }
    }

    /// Waits for the current persist; a request left pending by a failed run is
    /// retried once, never looped.
    func flushSourceDiscoveryPersist() async {
        if let task = sourceDiscoveryPersistTask { await task.value }
        if sourceDiscoveryPersistPending, sourceDiscoveryPersistTask == nil { scheduleSourceDiscoveryPersist() }
        if let task = sourceDiscoveryPersistTask { await task.value }
    }

    /// Recordings of members of undeleted favorites/playlist collections. Runs
    /// off the actor; unchanged songs reuse their descriptor through the memo.
    static func sourceDiscoveryRecordings(_ library: MJ, memo: [String: MusicSourceDiscoveryRecording])
        async -> (recordings: [MusicSourceDiscoveryRecording], memo: [String: MusicSourceDiscoveryRecording]) {
        var members = Set<Data>()
        for collection in library["collections"].array
            where !collection["deleted"].bool && ["favorites", "playlist"].contains(collection["kind"].string) {
            for id in collection["order"].strings { members.insert(Data(id.utf8)) }
        }
        var recordings = Set<MusicSourceDiscoveryRecording>(), next: [String: MusicSourceDiscoveryRecording] = [:]
        for (id, song) in library["songs"].fields where members.contains(Data(id.utf8)) {
            // Exactly the inputs MusicModel.descriptor reads.
            let key = MJ.array([song["title"], song["artist"], song["duration"]]).json
            let recording: MusicSourceDiscoveryRecording
            if let known = memo[key] ?? next[key] { recording = known }
            else {
                let descriptor = MusicModel.descriptor(song)
                guard !descriptor.fingerprint.isEmpty else { continue }
                recording = .init(fingerprint: descriptor.fingerprint, duration: descriptor.duration)
            }
            next[key] = recording; recordings.insert(recording)
        }
        return (Array(recordings), next)
    }
}
