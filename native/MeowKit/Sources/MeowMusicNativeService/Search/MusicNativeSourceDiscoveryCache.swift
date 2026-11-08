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
    public let observations: [MusicSearchObservation]
    public init(checkedAt: Double, sources: [MusicSourceDiscoverySource],
                observations: [MusicSearchObservation] = []) {
        self.checkedAt = checkedAt; self.sources = sources
        self.observations = observations
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

public struct MusicSourceDiscoveryMetadata: Sendable {
    public let recording: MusicSourceDiscoveryRecording
    public let platform: String
    public let sources: [MusicSourceDiscoverySource]
    public init(recording: MusicSourceDiscoveryRecording, platform: String, sources: [MusicSourceDiscoverySource]) {
        self.recording = recording; self.platform = platform; self.sources = sources
    }
    func observation(_ request: MusicSearchObservation) -> MusicSearchObservation {
        let outcome: MusicSearchObservation.Outcome = request.outcome == .failed ? .failed :
            !sources.isEmpty ? .found : request.scope == .recording ? .notFound : .incomplete
        return request.result(outcome, failure: request.failureCode.flatMap(MusicServiceError.init(rawValue:)))
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
    /// Already acquired metadata adds positive facts without claiming a query.
    @discardableResult func include(_ metadata: [MusicSourceDiscoveryMetadata]) async -> Bool
    func observe(fingerprint: String, duration: Double, observation: MusicSearchObservation,
                 sources: [MusicSourceDiscoverySource]) async
    func observe(_ metadata: [MusicSourceDiscoveryMetadata], observation: MusicSearchObservation) async
    /// Source keys the user confirmed are not this recording.
    func excluded(fingerprint: String, duration: Double) async -> Set<String>
    func exclude(fingerprint: String, duration: Double, sourceKey: String) async
    func persist(retaining recordings: [MusicSourceDiscoveryRecording]) async
}

public extension MusicSourceDiscoveryCaching {
    // Existing injected caches can keep their original discovery contract.
    @discardableResult func include(_ metadata: [MusicSourceDiscoveryMetadata]) async -> Bool { false }
    func observe(fingerprint: String, duration: Double, observation: MusicSearchObservation,
                 sources: [MusicSourceDiscoverySource]) async {
        guard observation.outcome == .found || observation.outcome == .notFound else { return }
        await record(fingerprint: fingerprint, duration: duration, platform: observation.platform,
                     checkedAt: observation.checkedAt, sources: sources)
    }
    func observe(_ metadata: [MusicSourceDiscoveryMetadata], observation: MusicSearchObservation) async {
        for item in metadata where item.platform == observation.platform {
            await observe(fingerprint: item.recording.fingerprint, duration: item.recording.duration,
                          observation: item.observation(observation), sources: item.sources)
        }
    }
}

private enum MusicSourceDiscoveryLimits {
    static let maximumSources = 16

    static func valid(fingerprint: String, duration: Double) -> Bool {
        !fingerprint.isEmpty && fingerprint.utf8.count <= 4 * 1024 &&
            duration.isFinite && (0...604_800).contains(duration)
    }

    static func validKey(_ key: String) -> Bool { !key.isEmpty && key.utf8.count <= 1024 }

    static func valid(_ observation: MusicSearchObservation, platform: String, at: Double) -> Bool {
        observation.platform == platform && MusicNativeSearchProtocol.platforms.contains(platform) &&
            observation.startedAt.isFinite && observation.startedAt > 0 &&
            observation.checkedAt.isFinite && observation.checkedAt >= observation.startedAt &&
            observation.checkedAt <= at && (1...100).contains(observation.limit) && observation.page > 0 &&
            !observation.context.isEmpty && observation.context.utf8.count <= 1024 &&
            observation.query.utf8.count <= 800 &&
            observation.failureCode.map({ MusicServiceError(rawValue: $0) != nil }) != false &&
            (observation.outcome != .notFound || observation.scope == .recording)
    }

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
    var observations: [MusicSearchObservation]
    /// Sorted source keys whose full rows came from accepted query responses.
    /// Snapshot imports cannot fill fields those responses removed.
    var observedSourceKeys: [String]
}

private struct SourceDiscoveryEnvelope: Codable {
    var version = 1
    var records: [String: SourceDiscoveryRecord] = [:]

    private func storedKey(fingerprint: String, duration: Double, candidates: [String]? = nil) -> String? {
        guard MusicSourceDiscoveryLimits.valid(fingerprint: fingerprint, duration: duration) else { return nil }
        let key = MusicRecordingIdentity.hash(fingerprint)
        if let record = records[key], record.matches(fingerprint: fingerprint, duration: duration) { return key }
        return (candidates ?? Array(records.keys)).sorted().first {
            $0.hasPrefix(key + ":") && records[$0]?.matches(fingerprint: fingerprint, duration: duration) == true
        }
    }

    private func stored(fingerprint: String, duration: Double) -> SourceDiscoveryRecord? {
        storedKey(fingerprint: fingerprint, duration: duration).flatMap { records[$0] }
    }

    /// No age filter here: each consumer applies its own window to empty checks.
    func entries(fingerprint: String, duration: Double, at: Double) -> [String: MusicSourceDiscoveryEntry] {
        guard at.isFinite, let record = stored(fingerprint: fingerprint, duration: duration) else { return [:] }
        var result: [String: MusicSourceDiscoveryEntry] = [:]
        for (platform, value) in record.platforms {
            guard value.checkedAt.isFinite, value.checkedAt >= 0, value.checkedAt <= at,
                  value.observations.allSatisfy({ MusicSourceDiscoveryLimits.valid($0, platform: platform, at: at) }),
                  let sources = MusicSourceDiscoveryLimits.sources(value.sources, platform: platform) else { continue }
            result[platform] = .init(checkedAt: value.checkedAt, sources: sources, observations: value.observations)
        }
        return result
    }

    func excluded(fingerprint: String, duration: Double) -> Set<String> {
        Set(stored(fingerprint: fingerprint, duration: duration)?.excluded ?? [])
    }

    private func current(fingerprint: String, duration: Double, at: Double,
                         candidates: [String]? = nil) -> (String, SourceDiscoveryRecord) {
        let key = MusicRecordingIdentity.hash(fingerprint)
        if let found = storedKey(fingerprint: fingerprint, duration: duration, candidates: candidates) { return (found, records[found]!) }
        let newKey = records[key] == nil ? key : key + ":" + MusicRecordingIdentity.hash(String(duration))
        return (newKey, .init(fingerprint: fingerprint, duration: duration, updatedAt: at, platforms: [:], excluded: []))
    }

    mutating func record(fingerprint: String, duration: Double, platform: String, checkedAt: Double,
                         at: Double, sources: [MusicSourceDiscoverySource]) -> Bool {
        guard MusicSourceDiscoveryLimits.valid(fingerprint: fingerprint, duration: duration),
              checkedAt.isFinite, checkedAt > 0, checkedAt <= at,
              let sources = MusicSourceDiscoveryLimits.sources(sources, platform: platform) else { return false }
        var (key, record) = current(fingerprint: fingerprint, duration: duration, at: checkedAt)
        if let existing = record.platforms[platform], existing.checkedAt > checkedAt { return false }
        // Preserve positive timing evidence: a later incomplete observation must
        // not relabel other platforms' successful checks as another recording.
        if record.duration <= 0 { record.duration = duration }
        record.updatedAt = max(record.updatedAt, checkedAt)
        record.platforms[platform] = .init(checkedAt: checkedAt, sources: sources, observations: [],
                                           observedSourceKeys: sources.map(\.sourceKey).sorted())
        records[key] = record
        return true
    }

    mutating func include(fingerprint: String, duration: Double, platform: String,
                         sources: [MusicSourceDiscoverySource], at: Double, key: String) -> Bool {
        guard MusicSourceDiscoveryLimits.valid(fingerprint: fingerprint, duration: duration),
              at.isFinite, at > 0, !sources.isEmpty,
              let incoming = MusicSourceDiscoveryLimits.sources(sources, platform: platform) else { return false }
        var record = records[key] ?? .init(fingerprint: fingerprint, duration: duration,
                                         updatedAt: at, platforms: [:], excluded: [])
        var value = record.platforms[platform] ?? .init(checkedAt: 0, sources: [], observations: [], observedSourceKeys: [])
        let previous = value.sources
        value.sources = merging(value.sources, incoming: incoming, replacing: [],
                                preserving: Set(value.observedSourceKeys))
        guard value.sources != previous else { return false }
        record.platforms[platform] = value
        if record.duration <= 0 { record.duration = duration }
        record.updatedAt = max(record.updatedAt, at)
        records[key] = record
        return true
    }

    mutating func observe(fingerprint: String, duration: Double, observation: MusicSearchObservation,
                         sources: [MusicSourceDiscoverySource], at: Double, candidates: [String]? = nil) -> Bool {
        guard MusicSourceDiscoveryLimits.valid(fingerprint: fingerprint, duration: duration),
              MusicSourceDiscoveryLimits.valid(observation, platform: observation.platform, at: at),
              let incoming = MusicSourceDiscoveryLimits.sources(sources, platform: observation.platform),
              observation.outcome != .notFound || observation.scope == .recording && incoming.isEmpty,
              observation.outcome != .found || !incoming.isEmpty else { return false }
        var (key, record) = current(fingerprint: fingerprint, duration: duration,
                                    at: observation.checkedAt, candidates: candidates)
        var value = record.platforms[observation.platform] ??
            .init(checkedAt: 0, sources: [], observations: [], observedSourceKeys: [])
        if let previous = value.observations.first(where: { $0.sameEvidence(as: observation) }) {
            guard observation.isNewer(than: previous) ||
                observation.requestID == previous.requestID && observation.outcome != previous.outcome else { return false }
        }
        let newest = value.observations.max { $1.isNewer(than: $0) }
        let latest = newest == nil || !newest!.isNewer(than: observation)
        if observation.outcome == .found, latest {
            value.sources = merging(value.sources, incoming: incoming, replacing: Set(incoming.map(\.sourceKey)),
                                    preserving: [])
            let accepted = value.sources.filter { incoming.contains($0) }.map(\.sourceKey)
            value.observedSourceKeys = Set(value.observedSourceKeys).union(accepted).sorted()
        }
        value.observations.removeAll { $0.sameEvidence(as: observation) }
        value.observations.append(observation)
        if latest && (observation.outcome == .found || observation.outcome == .notFound) {
            value.checkedAt = observation.checkedAt
        }
        record.platforms[observation.platform] = value
        if record.duration <= 0 { record.duration = duration }
        record.updatedAt = max(record.updatedAt, observation.checkedAt)
        records[key] = record
        return true
    }

    /// A real response replaces its accepted rows, including absent optional
    /// fields. Only rows not yet observed by a query allow snapshot enrichment.
    private func merging(_ known: [MusicSourceDiscoverySource], incoming: [MusicSourceDiscoverySource],
                         replacing: Set<String>, preserving: Set<String>) -> [MusicSourceDiscoverySource] {
        var result = known
        for source in incoming {
            if let index = result.firstIndex(where: { $0.sourceKey == source.sourceKey }) {
                if result[index] == source { continue }
                if !replacing.contains(source.sourceKey) && preserving.contains(source.sourceKey) { continue }
                guard let old = MusicSourceDiscoveryLimits.row(result[index], platform: source.source),
                      let new = MusicSourceDiscoveryLimits.row(source, platform: source.source),
                      MusicModel.compatible(old, new, sameNative: true) else { continue }
                if replacing.contains(source.sourceKey) {
                    guard result.enumerated().allSatisfy({ position, known in
                        guard position != index else { return true }
                        guard let other = MusicSourceDiscoveryLimits.row(known, platform: known.source) else { return false }
                        return MusicModel.compatible(other, new)
                    }) else { continue }
                    result[index] = source
                    continue
                }
                let song = MJ.obj([("songId", .string("metadata")), ("title", MusicModel.sourceData(old)["name"]),
                    ("artist", MusicModel.sourceData(old)["singer"]), ("album", .string("")),
                    ("duration", .number(MusicModel.nativeNumber(MusicModel.sourceData(old)["duration"]))),
                    ("sources", .array([old]))])
                guard let merged = try? MusicModel.mergeSources(song, adding: [new]),
                      let row = merged["sources"].array.first else { continue }
                result[index] = .init(sourceKey: row["sourceKey"].string, source: source.source,
                                      musicInfo: row["musicInfo"].json)
            } else if result.count < MusicSourceDiscoveryLimits.maximumSources {
                guard let row = MusicSourceDiscoveryLimits.row(source, platform: source.source),
                      result.allSatisfy({ known in
                          guard let other = MusicSourceDiscoveryLimits.row(known, platform: known.source) else { return false }
                          return MusicModel.compatible(other, row)
                      }) else { continue }
                result.append(source)
            }
        }
        return result
    }

    mutating func exclude(fingerprint: String, duration: Double, sourceKey: String, at: Double) -> Bool {
        guard MusicSourceDiscoveryLimits.valid(fingerprint: fingerprint, duration: duration),
              MusicSourceDiscoveryLimits.validKey(sourceKey), at.isFinite, at > 0 else { return false }
        var (key, record) = current(fingerprint: fingerprint, duration: duration, at: at)
        var keys = Set(record.excluded)
        guard keys.insert(sourceKey).inserted else { return false }
        if record.duration <= 0 { record.duration = duration }
        record.excluded = keys.sorted()
        record.updatedAt = max(record.updatedAt, at)
        records[key] = record
        return true
    }

    /// Request ordering, rather than a late response's completion time, decides
    /// source precedence. Exclusions accumulate independently of query freshness.
    mutating func absorb(_ stored: SourceDiscoveryEnvelope, at: Double) {
        for (key, disk) in stored.records {
            guard MusicSourceDiscoveryLimits.valid(fingerprint: disk.fingerprint, duration: disk.duration) else { continue }
            var clean = disk
            clean.excluded = MusicSourceDiscoveryLimits.keys(disk.excluded)
            clean.platforms = disk.platforms.filter { platform, value in
                value.checkedAt.isFinite && value.checkedAt >= 0 && value.checkedAt <= at &&
                    MusicSourceDiscoveryLimits.sources(value.sources, platform: platform) != nil &&
                    value.observations.allSatisfy { MusicSourceDiscoveryLimits.valid($0, platform: platform, at: at) }
            }
            guard var memory = records[key] else { records[key] = clean; continue }
            guard memory.fingerprint == clean.fingerprint,
                  MusicSourceDiscoveryLimits.durationCompatible(memory.duration, clean.duration) else { continue }
            for (platform, value) in clean.platforms {
                guard let previous = memory.platforms[platform] else { memory.platforms[platform] = value; continue }
                var merged = previous
                let memoryFound = previous.observations.filter { $0.outcome == .found }.max { $1.isNewer(than: $0) }
                let diskFound = value.observations.filter { $0.outcome == .found }.max { $1.isNewer(than: $0) }
                let preferDisk: Bool
                if let memoryFound { preferDisk = diskFound?.isNewer(than: memoryFound) == true }
                else { preferDisk = diskFound != nil || value.checkedAt > previous.checkedAt }
                let memoryObserved = Set(previous.observedSourceKeys), diskObserved = Set(value.observedSourceKeys)
                let replacing = preferDisk ? diskObserved : diskObserved.subtracting(memoryObserved)
                merged.sources = merging(previous.sources, incoming: value.sources,
                                         replacing: replacing, preserving: memoryObserved)
                let accepted = merged.sources.filter {
                    diskObserved.contains($0.sourceKey) && value.sources.contains($0)
                }.map(\.sourceKey)
                merged.observedSourceKeys = memoryObserved.union(accepted).sorted()
                merged.checkedAt = max(previous.checkedAt, value.checkedAt)
                for observation in value.observations {
                    if let old = merged.observations.first(where: { $0.sameEvidence(as: observation) }),
                       !observation.isNewer(than: old) { continue }
                    merged.observations.removeAll { $0.sameEvidence(as: observation) }
                    merged.observations.append(observation)
                }
                if let latest = merged.observations.filter({ $0.outcome == .found || $0.outcome == .notFound })
                    .max(by: { $1.isNewer(than: $0) }) {
                    merged.checkedAt = latest.checkedAt
                }
                memory.platforms[platform] = merged
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
    @discardableResult mutating func apply(maximumBytes: Int, _ change: (inout SourceDiscoveryEnvelope) -> Bool) -> Bool {
        var next = self
        guard change(&next), SourceDiscoveryDocument.encode(&next, maximumBytes: maximumBytes) != nil else { return false }
        self = next
        return true
    }

    mutating func include(_ metadata: [MusicSourceDiscoveryMetadata], at: Double) -> Bool {
        var keys = Dictionary(grouping: records.keys) { records[$0]!.fingerprint }
        var changed = false
        for item in metadata {
            let recording = item.recording
            let (key, _) = current(fingerprint: recording.fingerprint, duration: recording.duration,
                                   at: at, candidates: keys[recording.fingerprint] ?? [])
            if include(fingerprint: item.recording.fingerprint, duration: item.recording.duration,
                       platform: item.platform, sources: item.sources, at: at, key: key) {
                changed = true
                if keys[recording.fingerprint]?.contains(key) != true {
                    keys[recording.fingerprint, default: []].append(key)
                }
            }
        }
        return changed
    }

    mutating func observe(_ metadata: [MusicSourceDiscoveryMetadata], observation: MusicSearchObservation,
                          at: Double) -> Bool {
        var keys = Dictionary(grouping: records.keys) { records[$0]!.fingerprint }
        var changed = false
        for item in metadata where item.platform == observation.platform {
            let recording = item.recording, candidates = keys[item.recording.fingerprint] ?? []
            let (key, _) = current(fingerprint: recording.fingerprint, duration: recording.duration,
                                   at: at, candidates: candidates)
            if observe(fingerprint: recording.fingerprint, duration: recording.duration,
                       observation: item.observation(observation), sources: item.sources, at: at, candidates: candidates) {
                changed = true
                if !candidates.contains(key) {
                    keys[recording.fingerprint, default: []].append(key)
                }
            }
        }
        return changed
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
                merged.absorb(stored, at: now().timeIntervalSince1970 * 1000)
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
    @discardableResult public func include(_ metadata: [MusicSourceDiscoveryMetadata]) async -> Bool {
        guard !Task.isCancelled, !metadata.isEmpty else { return false }
        _ = prepare()
        let at = now().timeIntervalSince1970 * 1000
        return envelope.apply(maximumBytes: maximumBytes) { $0.include(metadata, at: at) }
    }
    public func observe(fingerprint: String, duration: Double, observation: MusicSearchObservation,
                        sources: [MusicSourceDiscoverySource]) async {
        guard !Task.isCancelled else { return }
        _ = prepare()
        let at = now().timeIntervalSince1970 * 1000
        envelope.apply(maximumBytes: maximumBytes) {
            $0.observe(fingerprint: fingerprint, duration: duration, observation: observation, sources: sources, at: at)
        }
    }
    public func observe(_ metadata: [MusicSourceDiscoveryMetadata], observation: MusicSearchObservation) async {
        guard !Task.isCancelled, !metadata.isEmpty else { return }
        _ = prepare()
        let at = now().timeIntervalSince1970 * 1000
        envelope.apply(maximumBytes: maximumBytes) { $0.observe(metadata, observation: observation, at: at) }
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
    @discardableResult public func include(_ metadata: [MusicSourceDiscoveryMetadata]) async -> Bool {
        guard !Task.isCancelled, !metadata.isEmpty else { return false }
        let at = now().timeIntervalSince1970 * 1000
        return envelope.apply(maximumBytes: maximumBytes) { $0.include(metadata, at: at) }
    }
    public func observe(fingerprint: String, duration: Double, observation: MusicSearchObservation,
                        sources: [MusicSourceDiscoverySource]) async {
        guard !Task.isCancelled else { return }
        let at = now().timeIntervalSince1970 * 1000
        envelope.apply(maximumBytes: maximumBytes) {
            $0.observe(fingerprint: fingerprint, duration: duration, observation: observation, sources: sources, at: at)
        }
    }
    public func observe(_ metadata: [MusicSourceDiscoveryMetadata], observation: MusicSearchObservation) async {
        guard !Task.isCancelled, !metadata.isEmpty else { return }
        let at = now().timeIntervalSince1970 * 1000
        envelope.apply(maximumBytes: maximumBytes) { $0.observe(metadata, observation: observation, at: at) }
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

/// A successful query projected from the same owner, without admitting its rows.
/// This transient value is never persisted or passed to a source merge.
struct MusicSourceDiscoveryCheck: Sendable {
    let checkedAt: Double
    let observations: [MusicSearchObservation]
}

/// Source admission and executed query evidence are independent projections.
struct MusicSourceDiscoveryState: Sendable {
    /// Original check times, including empty checks of any age.
    var entries: [String: MusicSourceDiscoveryEntry] = [:]
    var checks: [String: MusicSourceDiscoveryCheck] = [:]
    /// Never contains the song's own primary source.
    var excluded: Set<String> = []
    var at: Double = 0
    func hasCurrentCheck(_ platform: String, window: Double) -> Bool {
        checks[platform].map { at - $0.checkedAt < window } ?? false
    }
    func current(window: Double) -> [String: MusicSourceDiscoveryEntry] {
        entries.filter { _, value in
            if !value.sources.isEmpty { return true }
            if value.observations.isEmpty { return value.checkedAt > 0 && at - value.checkedAt < window }
            return value.observations.contains {
                $0.scope == .recording && $0.outcome == .notFound && at - $0.checkedAt < window
            }
        }
    }
}

extension NativeMusicService {
    /// Compatible, non-excluded cache evidence with original check times.
    func sourceDiscoveryState(_ song: MJ, query: String? = nil, context: String = "public") async -> MusicSourceDiscoveryState {
        let own = epoch
        do {
            try await retainLibrarySongMetadata()
            try check(own)
            try await retainSongMetadata([song])
            try check(own)
        } catch { return .init() }
        return await cachedSourceDiscoveryState(song, query: query, context: context)
    }

    /// Projection after a batch has already imported its acquired metadata.
    func cachedSourceDiscoveryState(_ song: MJ, query: String? = nil, context: String = "public") async -> MusicSourceDiscoveryState {
        let own = epoch
        guard (try? check(own)) != nil else { return .init() }
        let at = now().timeIntervalSince1970 * 1000
        let descriptor = MusicModel.descriptor(song), fingerprint = descriptor.fingerprint
        guard !fingerprint.isEmpty else { return .init(at: at) }
        let excluded = await sourceDiscoveryExcluded(song, descriptor: descriptor)
        guard (try? check(own)) != nil else { return .init(at: at) }
        let stored = await sourceDiscovery.entries(fingerprint: fingerprint, duration: descriptor.duration)
        guard (try? check(own)) != nil else { return .init(at: at) }
        var result: [String: MusicSourceDiscoveryEntry] = [:]
        var checks: [String: MusicSourceDiscoveryCheck] = [:]
        let query = MusicRecordingIdentity.normalize(query ?? sourceDiscoveryQuery(song))
        for (platform, value) in stored {
            guard MusicNativeSearchProtocol.platforms.contains(platform) else { continue }
            let observations = value.observations.filter {
                $0.context == context && ($0.outcome == .found ||
                    $0.scope == .recording && MusicRecordingIdentity.normalize($0.query) == query)
            }
            let successful = observations.filter { $0.outcome == .found || $0.outcome == .notFound }
            let checkedAt = value.observations.isEmpty ? value.checkedAt :
                successful.max(by: { $1.isNewer(than: $0) })?.checkedAt ?? 0
            if checkedAt.isFinite, checkedAt > 0, checkedAt <= at {
                checks[platform] = .init(checkedAt: checkedAt, observations: successful)
            }
            let relevant = MusicSourceDiscoveryEntry(checkedAt: checkedAt, sources: value.sources,
                                                      observations: observations)
            guard !value.sources.isEmpty || value.observations.isEmpty || !observations.isEmpty,
                  let entry = compatibleSourceDiscoveryEntry(song, platform: platform, entry: relevant, excluded: excluded)
            else { continue }
            result[platform] = entry
        }
        return .init(entries: result, checks: checks, excluded: excluded, at: at)
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
        guard entry.checkedAt.isFinite, entry.checkedAt >= 0,
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
        // Incompatible or explicitly excluded positive rows are not evidence of
        // a successful empty query for this recording.
        guard entry.sources.isEmpty || !accepted.isEmpty else { return nil }
        return .init(checkedAt: entry.checkedAt, sources: accepted, observations: entry.observations)
    }

    func mergeSourceDiscoveryCache(_ song: MJ, platform: String,
                                   entry: MusicSourceDiscoveryEntry) -> MJ {
        var result = song
        for source in entry.sources {
            guard let row = MusicSourceDiscoveryLimits.row(source, platform: platform),
                  MusicModel.compatible(result, row),
                  result["sources"].array.allSatisfy({ MusicModel.compatible($0, row) }) else { continue }
            var rows = result["sources"].array
            if let index = rows.firstIndex(where: { $0["sourceKey"] == row["sourceKey"] }) {
                // The owner row is complete. Falling back to a display snapshot
                // here would resurrect fields removed by a newer real response.
                rows[index] = row
            } else { rows.append(row) }
            var projected = result
            projected["sources"] = .array(rows)
            guard let merged = try? MusicModel.cleanSong(projected) else { continue }
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
                    entry: .init(checkedAt: entry.checkedAt, sources: [entry.sources[index]],
                                 observations: entry.observations))
            }
        }
        return result
    }

    func sourceDiscoveryMetadata(_ song: MJ, platform: String, rows: [MJ]) -> MusicSourceDiscoveryMetadata? {
        let descriptor = MusicModel.descriptor(song), fingerprint = descriptor.fingerprint
        guard !fingerprint.isEmpty,
              MusicNativeSearchProtocol.platforms.contains(platform) else { return nil }
        var stored: [MusicSourceDiscoverySource] = [], accepted: [MJ] = []
        var positions: [String: Int] = [:]
        for raw in rows {
            guard (try? MusicModel.rejectSecrets(raw)) != nil,
                  let clean = try? MusicModel.cleanSource(raw), clean["source"].string == platform else { return nil }
            guard MusicModel.compatible(song, clean),
                  song["sources"].array.allSatisfy({ MusicModel.compatible($0, clean) }),
                  accepted.allSatisfy({ MusicModel.compatible($0, clean) }) else { continue }
            if let index = positions[clean["sourceKey"].string] {
                var existing = song
                existing["sources"] = .array([accepted[index]])
                guard let merged = try? MusicModel.mergeSources(existing, adding: [clean], preferIncoming: true),
                      let row = merged["sources"].array.first else { return nil }
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
        return .init(recording: .init(fingerprint: fingerprint, duration: descriptor.duration),
                     platform: platform, sources: stored)
    }

    func recordSourceDiscovery(_ song: MJ, platform: String, rows: [MJ],
                               observation: MusicSearchObservation? = nil) async {
        guard (try? check()) != nil,
              let metadata = sourceDiscoveryMetadata(song, platform: platform, rows: rows) else { return }
        let own = epoch, recording = metadata.recording
        if let observation {
            guard observation.platform == platform else { return }
            await sourceDiscovery.observe(fingerprint: recording.fingerprint, duration: recording.duration,
                observation: metadata.observation(observation), sources: metadata.sources)
        } else {
            await sourceDiscovery.record(fingerprint: recording.fingerprint, duration: recording.duration,
                platform: platform, checkedAt: now().timeIntervalSince1970 * 1000, sources: metadata.sources)
        }
        if (try? check(own)) != nil { scheduleSourceDiscoveryPersist() }
    }

    func recordSourceDiscovery(_ song: MJ, platform: String, page: MusicNativeTrackPage,
                               observation: MusicSearchObservation? = nil) async {
        guard page.total >= 0, page.items.allSatisfy({ $0.platform == platform }) else { return }
        var rows: [MJ] = []
        for value in page.items {
            guard let candidate = try? MusicModel.normalize(value),
                  let row = candidate["sources"].array.first else { return }
            rows.append(row)
        }
        await recordSourceDiscovery(song, platform: platform, rows: rows, observation: observation)
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
            guard (try? await retainLibrarySongMetadata()) != nil else {
                sourceDiscoveryPersistPending = true; return
            }
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
