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

/// A disposable per-recording discovery cache. A failed read is a miss and a
/// failed write is ignored; neither can reject playback.
public protocol MusicSourceDiscoveryCaching: Sendable {
    func entries(fingerprint: String, duration: Double) async -> [String: MusicSourceDiscoveryEntry]
    func record(fingerprint: String, duration: Double, platform: String, checkedAt: Double,
                sources: [MusicSourceDiscoverySource]) async
}

private enum MusicSourceDiscoveryLimits {
    static let maximumSources = 16

    static func valid(fingerprint: String, duration: Double) -> Bool {
        !fingerprint.isEmpty && fingerprint.utf8.count <= 4 * 1024 &&
            duration.isFinite && (0...604_800).contains(duration)
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
}

private struct SourceDiscoveryRecord: Codable {
    var fingerprint: String
    var duration: Double
    var updatedAt: Double
    var platforms: [String: SourceDiscoveryPlatform]
}

private struct SourceDiscoveryPlatform: Codable {
    var checkedAt: Double
    var sources: [MusicSourceDiscoverySource]
}

private struct SourceDiscoveryEnvelope: Codable {
    var version = 1
    var records: [String: SourceDiscoveryRecord] = [:]

    func entries(fingerprint: String, duration: Double, at: Double,
                 ttl: TimeInterval) -> [String: MusicSourceDiscoveryEntry] {
        guard MusicSourceDiscoveryLimits.valid(fingerprint: fingerprint, duration: duration),
              at.isFinite, let record = records[MusicRecordingIdentity.hash(fingerprint)],
              record.fingerprint == fingerprint,
              MusicSourceDiscoveryLimits.valid(fingerprint: record.fingerprint, duration: record.duration),
              MusicSourceDiscoveryLimits.durationCompatible(record.duration, duration) else { return [:] }
        var result: [String: MusicSourceDiscoveryEntry] = [:]
        for (platform, value) in record.platforms {
            guard value.checkedAt.isFinite, value.checkedAt > 0, value.checkedAt <= at,
                  at - value.checkedAt < ttl * 1000,
                  let sources = MusicSourceDiscoveryLimits.sources(value.sources, platform: platform) else { continue }
            result[platform] = .init(checkedAt: value.checkedAt, sources: sources)
        }
        return result
    }

    mutating func record(fingerprint: String, duration: Double, platform: String, checkedAt: Double,
                         at: Double, sources: [MusicSourceDiscoverySource]) -> Bool {
        guard MusicSourceDiscoveryLimits.valid(fingerprint: fingerprint, duration: duration),
              checkedAt.isFinite, checkedAt > 0, checkedAt <= at,
              let sources = MusicSourceDiscoveryLimits.sources(sources, platform: platform) else { return false }
        let key = MusicRecordingIdentity.hash(fingerprint)
        var record = records[key] ?? .init(fingerprint: fingerprint, duration: duration,
                                           updatedAt: checkedAt, platforms: [:])
        if record.fingerprint != fingerprint ||
            !MusicSourceDiscoveryLimits.durationCompatible(record.duration, duration) {
            record = .init(fingerprint: fingerprint, duration: duration, updatedAt: checkedAt, platforms: [:])
        }
        if let existing = record.platforms[platform], existing.checkedAt > checkedAt { return false }
        // Preserve positive timing evidence: a later incomplete observation must
        // not relabel other platforms' successful checks as another recording.
        if record.duration <= 0 { record.duration = duration }
        record.updatedAt = max(record.updatedAt, checkedAt)
        record.platforms[platform] = .init(checkedAt: checkedAt, sources: sources)
        records[key] = record
        return true
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

/// Encrypted implementation for the product. No credentials, audio URLs or handles
/// are stored; every source row is reconstructed through MusicModel.cleanSource.
public actor MusicNativeEncryptedSourceDiscoveryCache: MusicSourceDiscoveryCaching {
    public static let namespace = "music.source-discovery.v1"
    private static let key = "records"
    private let factory: @Sendable () throws -> EncryptedDocumentStore
    private let now: @Sendable () -> Date
    private let ttl: TimeInterval
    private let maximumBytes: Int
    private var document: EncryptedDocumentStore?
    private var envelope: SourceDiscoveryEnvelope?

    public init(storeFactory: @escaping @Sendable () throws -> EncryptedDocumentStore,
                clock: @escaping @Sendable () -> Date = { Date() },
                ttl: TimeInterval = 30 * 24 * 60 * 60,
                maximumBytes: Int = 4 * 1024 * 1024) {
        factory = storeFactory; now = clock; self.ttl = ttl; self.maximumBytes = maximumBytes
    }

    private func store() throws -> EncryptedDocumentStore {
        if let document { return document }
        let value = try factory(); document = value; return value
    }
    private func load() -> SourceDiscoveryEnvelope? {
        if let envelope { return envelope }
        do {
            let data = try store().read(key: Self.key)
            let loaded: SourceDiscoveryEnvelope
            if let data {
                guard let value = SourceDiscoveryDocument.load(data, maximumBytes: maximumBytes) else { return nil }
                loaded = value
            } else { loaded = .init() }
            envelope = loaded
            return loaded
        } catch { return nil }
    }
    public func entries(fingerprint: String, duration: Double) async -> [String: MusicSourceDiscoveryEntry] {
        guard !Task.isCancelled, let envelope = load() else { return [:] }
        return envelope.entries(fingerprint: fingerprint, duration: duration,
            at: now().timeIntervalSince1970 * 1000, ttl: ttl)
    }
    public func record(fingerprint: String, duration: Double, platform: String, checkedAt: Double,
                       sources: [MusicSourceDiscoverySource]) async {
        guard !Task.isCancelled else { return }
        var next = load() ?? .init()
        guard next.record(fingerprint: fingerprint, duration: duration, platform: platform, checkedAt: checkedAt,
            at: now().timeIntervalSince1970 * 1000, sources: sources),
            let data = SourceDiscoveryDocument.encode(&next, maximumBytes: maximumBytes),
            !Task.isCancelled else { return }
        do {
            try store().write(data, key: Self.key)
            envelope = next
        } catch { envelope = nil }
    }
}

/// Test and default implementation. It applies the same validation, TTL and
/// byte budget without touching the filesystem or Keychain.
public actor MusicNativeMemorySourceDiscoveryCache: MusicSourceDiscoveryCaching {
    private let now: @Sendable () -> Date
    private let ttl: TimeInterval
    private let maximumBytes: Int
    private var envelope = SourceDiscoveryEnvelope()

    public init(clock: @escaping @Sendable () -> Date = { Date() },
                ttl: TimeInterval = 30 * 24 * 60 * 60,
                maximumBytes: Int = 4 * 1024 * 1024) {
        now = clock; self.ttl = ttl; self.maximumBytes = maximumBytes
    }
    public func entries(fingerprint: String, duration: Double) async -> [String: MusicSourceDiscoveryEntry] {
        guard !Task.isCancelled else { return [:] }
        return envelope.entries(fingerprint: fingerprint, duration: duration,
            at: now().timeIntervalSince1970 * 1000, ttl: ttl)
    }
    public func record(fingerprint: String, duration: Double, platform: String, checkedAt: Double,
                       sources: [MusicSourceDiscoverySource]) async {
        guard !Task.isCancelled else { return }
        var next = envelope
        if next.record(fingerprint: fingerprint, duration: duration, platform: platform, checkedAt: checkedAt,
            at: now().timeIntervalSince1970 * 1000, sources: sources),
           SourceDiscoveryDocument.encode(&next, maximumBytes: maximumBytes) != nil {
            envelope = next
        }
    }
}

extension NativeMusicService {
    func sourceDiscoveryEntries(_ song: MJ) async -> [String: MusicSourceDiscoveryEntry] {
        let descriptor = MusicModel.descriptor(song), fingerprint = descriptor.fingerprint
        guard !fingerprint.isEmpty else { return [:] }
        let stored = await sourceDiscovery.entries(fingerprint: fingerprint, duration: descriptor.duration)
        var result: [String: MusicSourceDiscoveryEntry] = [:]
        for (platform, value) in stored {
            guard MusicNativeSearchProtocol.platforms.contains(platform),
                  let entry = compatibleSourceDiscoveryEntry(song, platform: platform, entry: value)
            else { continue }
            result[platform] = entry
        }
        return result
    }

    func compatibleSourceDiscoveryEntry(_ song: MJ, platform: String,
                                        entry: MusicSourceDiscoveryEntry) -> MusicSourceDiscoveryEntry? {
        guard entry.checkedAt.isFinite, entry.checkedAt > 0,
              entry.sources.count <= MusicSourceDiscoveryLimits.maximumSources,
              entry.sources.allSatisfy({ $0.source == platform }) else { return nil }
        var accepted: [MusicSourceDiscoverySource] = [], rows: [MJ] = []
        for source in entry.sources {
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
}
