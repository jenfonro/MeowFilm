import Foundation
import CryptoKit
import Darwin
import MeowDebug
import MeowMusicFeature
import MeowContracts
import MeowStorage

/// Product-owned, evictable audio file cache (256 MiB, 30-day TTL).
/// File I/O runs outside SQLite write transactions; active read leases protect playing files from unlink.
public actor NativeMusicAudioFileCache: MusicAudioFileCaching {
    private let rootURL: URL
    private let database: SQLiteDatabase
    private let clock: @Sendable () -> Date
    private let transport: any NativeMusicAudioCacheTransport
    private let inspector: any NativeMusicAudioCacheInspecting
    private let capacity: Int64
    private let timing: NativeMusicAudioCacheTiming
    private let presentationCache: (any MusicPresentationCaching)?
    private var directory: NativeMusicAudioCacheDirectory?
    private var entries: [String: NativeMusicAudioCacheEntry] = [:]
    private var pending: [UUID: Pending] = [:]
    private var activeReadLeases: [String: Int] = [:]
    private var pendingDeletes: [String: Int64] = [:]
    private var closed = false
    private var captureCutoff: Date?
    private var officialCaptureCutoff: Date?
    private static let lifetime: TimeInterval = 30 * 86_400

    private struct Pending {
        let id: UUID
        let key: String
        let identity: MusicAudioFileCacheIdentity
        let fence: NativeMusicAudioCacheFence
        let sink: NativeMusicAudioCacheSink
        var task: Task<MusicAudioFileCacheVersion?, Never>?
        var reserved: Int64 = 0
        var mayPublish = true
    }

    public init(database: SQLiteDatabase, rootURL: URL, clock: @escaping @Sendable () -> Date = { Date() },
                presentationCache: (any MusicPresentationCaching)? = nil) {
        self.database = database; self.rootURL = rootURL; self.clock = clock
        self.presentationCache = presentationCache
        transport = URLSessionNativeMusicAudioCacheTransport()
        inspector = NativeMusicAudioCacheInspector()
        capacity = NativeMusicAudioCacheHTTPPolicy.maximumBytes
        timing = .init()
    }

    private func issueReadLease(for file: String) -> MusicAudioFileReadLease {
        activeReadLeases[file, default: 0] += 1
        return MusicAudioFileReadLease { [weak self] in
            Task { await self?.releaseReadLease(for: file) }
        }
    }

    private func releaseReadLease(for file: String) {
        guard let count = activeReadLeases[file] else { return }
        if count <= 1 {
            activeReadLeases.removeValue(forKey: file)
            if pendingDeletes.removeValue(forKey: file) != nil,
               !entries.values.contains(where: { $0.file == file }) {
                try? directory?.remove(file)
            }
        } else {
            activeReadLeases[file] = count - 1
        }
    }

    private func deleteOrDefer(_ file: String, size: Int64, in directory: NativeMusicAudioCacheDirectory, strict: Bool = false) throws {
        if activeReadLeases[file, default: 0] > 0 {
            pendingDeletes[file] = max(pendingDeletes[file] ?? 0, size)
        } else {
            pendingDeletes.removeValue(forKey: file)
            if strict {
                try directory.remove(file)
            } else {
                try? directory.remove(file)
            }
        }
    }

    private var protectedFiles: Set<String> {
        Set(activeReadLeases.keys).union(pendingDeletes.keys)
    }

    private var deferredDeleteBytes: Int64 {
        pendingDeletes.values.reduce(0, +)
    }

    public func stats() -> MusicAudioFileCacheStats {
        let indexedBytes = entries.values.reduce(Int64(0)) { $0 + $1.stamp.size }
        let deferred = deferredDeleteBytes
        let reserved = pending.values.reduce(Int64(0)) { $0 + $1.reserved }
        let leaseCount = activeReadLeases.values.reduce(0, +)
        return MusicAudioFileCacheStats(
            entryCount: entries.count,
            totalDiskBytes: indexedBytes + deferred,
            reservedBytes: reserved,
            pendingDeleteCount: pendingDeletes.count,
            pendingDeleteBytes: deferred,
            activeReadLeases: leaseCount,
            pendingCaptures: pending.count,
            capacityBytes: capacity,
            closed: closed
        )
    }

    public func debugCacheStats() -> DebugJSONValue {
        let s = stats()
        return .object([
            "entryCount": .int(s.entryCount),
            "totalDiskBytes": .integer(s.totalDiskBytes),
            "reservedBytes": .integer(s.reservedBytes),
            "pendingDeleteCount": .int(s.pendingDeleteCount),
            "pendingDeleteBytes": .integer(s.pendingDeleteBytes),
            "activeReadLeases": .int(s.activeReadLeases),
            "pendingCount": .int(s.pendingCaptures),
            "capacityBytes": .integer(s.capacityBytes),
            "closed": .bool(s.closed),
            "rootPath": .string(rootURL.path)
        ])
    }

    public func lookup(scope: String, songID: String, authorization: MusicQueueAuthorization,
                       accepts: @escaping @Sendable (MusicAudioFileCacheIdentity) -> Bool) async -> MusicAudioFileCacheHit? {
        guard !closed, !Task.isCancelled, authorization.isActive,
              Self.text(scope), Self.text(songID) else { return nil }
        do {
            let result: MusicAudioFileCacheHit? = try authorization.perform {
                let directory = try open()
                try prune(directory)
                let matches = entries.values
                    .filter { $0.identity.scope == scope && $0.identity.songID == songID && accepts($0.identity) }
                    .sorted(by: { Self.oldest($1, $0) })
                var brokenKeys: [String] = []
                var selected: NativeMusicAudioCacheEntry?
                for candidate in matches {
                    if directory.matches(candidate.file, stamp: candidate.stamp) {
                        selected = candidate
                        break
                    }
                    brokenKeys.append(candidate.key)
                }
                if !brokenKeys.isEmpty {
                    var next = entries
                    for broken in brokenKeys { next.removeValue(forKey: broken) }
                    try? commitIndex(next, directory: directory)
                }
                guard let entry = selected, authorization.isActive, !Task.isCancelled else { return nil }
                let lease = issueReadLease(for: entry.file)
                return .init(
                    identity: entry.identity,
                    fileURL: directory.url.appendingPathComponent(entry.file),
                    duration: entry.inspection.duration,
                    measuredQuality: entry.quality,
                    expiresAt: entry.expiresAt,
                    readLease: lease
                )
            }
            if !authorization.isActive {
                result?.readLease?.release()
                return nil
            }
            return result
        } catch { return nil }
    }

    @discardableResult public func storeAfterPlayback(_ request: MusicAudioFileCacheRequest) async -> MusicAudioFileCacheVersion? {
        let now = clock()
        guard !closed, !Task.isCancelled, request.authorization.isActive, request.playbackAuthorization.isActive,
              request.audio.playbackAuthorization?.isActive != false, Self.valid(request.identity),
              Self.confirmed(request.confirmedAt, now: now),
              captureCutoff.map({ request.confirmedAt > $0 }) ?? true,
              !request.identity.isOfficial || (officialCaptureCutoff.map({ request.confirmedAt > $0 }) ?? true),
              request.expectedDuration.isFinite, request.expectedDuration > 0, request.audio.fileCachePolicy.allowed,
              Self.licenseValid(request.audio.fileCachePolicy.licenseExpiresAt, now: now),
              NativeMusicAudioCacheInspection.supportedRequest(request.audio.delivery?.quality ?? request.identity.requestedQuality),
              NativeMusicAudioCacheHTTPPolicy.permits(request.audio.url, platform: request.identity.platform,
                                                      scriptMedia: !request.identity.isOfficial),
              request.audio.expiresAt.map({ $0.timeIntervalSince1970.isFinite && $0 > now }) ?? true else { return nil }
        do {
            let key = Self.key(request.identity)
            let directory = try open()
            try prune(directory)
            if var old = entries[key], old.identity == request.identity,
               (try? old.inspection.quality(for: request.audio.delivery?.quality ?? request.identity.requestedQuality,
                                           expectedDuration: request.expectedDuration)) != nil,
               directory.matches(old.file, stamp: old.stamp) {
                try request.authorization.perform {
                    old.lastPlayedAt = max(old.lastPlayedAt, request.confirmedAt)
                    if let expiry = request.audio.fileCachePolicy.licenseExpiresAt {
                        old.licenseExpiresAt = min(old.licenseExpiresAt ?? expiry, expiry)
                    }
                    old.expiresAt = Self.expiry(old.lastPlayedAt, old.licenseExpiresAt)
                    var next = entries; next[key] = old
                    try commitIndex(next, directory: directory)
                }
                return .init(rawValue: old.file)
            }
            for value in pending.values where value.key == key && value.identity != request.identity { cancelCapture(value) }
            guard !pending.values.contains(where: { $0.key == key }) else { return nil }
            let source = try await preparePlayback(.init(identity: request.identity, audio: request.audio,
                expectedDuration: request.expectedDuration, authorization: request.authorization,
                playbackAuthorization: request.playbackAuthorization), streaming: false)
            source.confirmPlayback(at: request.confirmedAt)
            let sink = pending[source.id]?.sink
            return await withTaskCancellationHandler { await source.result() } onCancel: { sink?.cancel() }
        } catch { return nil }
    }

    public func preparePlayback(_ request: MusicAudioFilePlaybackRequest) async throws -> any MusicAudioFilePlayback {
        try await preparePlayback(request, streaming: true)
    }

    private func preparePlayback(_ request: MusicAudioFilePlaybackRequest, streaming: Bool) async throws -> any MusicAudioFilePlayback {
        let now = clock()
        guard !closed, !Task.isCancelled, request.authorization.isActive, request.playbackAuthorization.isActive,
              request.audio.playbackAuthorization?.isActive != false, Self.valid(request.identity),
              request.expectedDuration.isFinite, request.expectedDuration >= 0,
              Self.licenseValid(request.audio.fileCachePolicy.licenseExpiresAt, now: now),
              NativeMusicAudioCacheHTTPPolicy.permits(request.audio.url, platform: request.identity.platform,
                                                      scriptMedia: !request.identity.isOfficial, streaming: streaming),
              request.audio.expiresAt.map({ $0.timeIntervalSince1970.isFinite && $0 > now }) ?? true else {
            throw NativeMusicAudioCacheFailure.miss
        }
        let persist = request.audio.fileCachePolicy.allowed && request.expectedDuration > 0 &&
            NativeMusicAudioCacheInspection.supportedRequest(request.audio.delivery?.quality ?? request.identity.requestedQuality) &&
            NativeMusicAudioCacheHTTPPolicy.permits(request.audio.url, platform: request.identity.platform,
                                                    scriptMedia: !request.identity.isOfficial)
        let key = Self.key(request.identity)
        let id = UUID(), fence = NativeMusicAudioCacheFence(authorization: request.authorization, timing: timing)
        let directory = try request.authorization.perform { try open() }
        try prune(directory)
        try request.authorization.check(); try request.playbackAuthorization.check()
        let sink = try NativeMusicAudioCacheSink(id: id, directory: directory, fence: fence,
            playbackAuthorization: request.playbackAuthorization, platform: request.identity.platform,
            scriptMedia: !request.identity.isOfficial, streaming: streaming, clock: clock,
            released: { [weak self] in Task { await self?.finished(id: id) } }) { [weak self] length in
                guard let self else { throw NativeMusicAudioCacheFailure.miss }
                try await self.reserve(id: id, bytes: length)
            }
        pending[id] = Pending(id: id, key: key, identity: request.identity, fence: fence, sink: sink)
        let http = NativeMusicAudioCacheHTTPRequest(url: request.audio.url, platform: request.identity.platform,
            scriptMedia: !request.identity.isOfficial, headers: request.audio.headers, streaming: streaming)
        let transport = self.transport, inspector = self.inspector
        let task = Task<MusicAudioFileCacheVersion?, Never> {
            let revocation = Task {
                for await _ in request.authorization.revocations() {
                    fence.cancel(); sink.cancel(); break
                }
            }
            let playbackRevocation = Task {
                for await _ in request.playbackAuthorization.revocations() {
                    _ = sink.retireIfIncomplete(); break
                }
            }
            let deadline = Task {
                do {
                    if try await fence.waitForExpiry() { fence.cancel(); sink.cancel() }
                } catch {}
            }
            let version: MusicAudioFileCacheVersion?
            do {
                try sink.checkReceivingAuthorization()
                try await transport.transfer(http, into: sink)
                let file = try sink.seal()
                if persist {
                    let measured = try await inspector.inspect(file, expectedDuration: request.expectedDuration)
                    try fence.finishedInspection()
                    deadline.cancel(); await deadline.value
                    try fence.check()
                    if let confirmed = try await sink.firstPlayback() {
                        let capture = MusicAudioFileCacheRequest(identity: request.identity, audio: request.audio,
                            expectedDuration: request.expectedDuration, confirmedAt: confirmed,
                            authorization: request.authorization, playbackAuthorization: request.playbackAuthorization)
                        version = try await self.publish(id: id, request: capture, inspection: measured)
                    } else { version = nil }
                } else {
                    try fence.finishedInspection()
                    version = nil
                }
                sink.complete(published: version != nil)
            } catch {
                sink.cancel()
                version = nil
            }
            revocation.cancel(); playbackRevocation.cancel(); deadline.cancel()
            await revocation.value; await playbackRevocation.value; await deadline.value
            await self.finished(id: id)
            return version
        }
        pending[id]?.task = task
        sink.attach(task)
        return sink
    }

    public func touch(_ identity: MusicAudioFileCacheIdentity, version: MusicAudioFileCacheVersion, confirmedAt: Date,
                      authorization: MusicQueueAuthorization) async {
        guard !closed, !Task.isCancelled, authorization.isActive,
              Self.valid(identity), Self.confirmed(confirmedAt, now: clock()) else { return }
        do {
            try authorization.perform {
                let directory = try open()
                try prune(directory)
                let key = Self.key(identity)
                guard var entry = entries[key], entry.identity == identity, entry.file == version.rawValue,
                      directory.matches(entry.file, stamp: entry.stamp) else { return }
                entry.lastPlayedAt = max(entry.lastPlayedAt, confirmedAt)
                entry.expiresAt = Self.expiry(entry.lastPlayedAt, entry.licenseExpiresAt)
                var next = entries; next[key] = entry
                try commitIndex(next, directory: directory)
            }
        } catch {}
    }

    public func remove(_ identity: MusicAudioFileCacheIdentity, version: MusicAudioFileCacheVersion) async {
        guard !closed, Self.valid(identity) else { return }
        do {
            let directory = try open(), key = Self.key(identity)
            guard entries[key]?.identity == identity, entries[key]?.file == version.rawValue else { return }
            var next = entries; next.removeValue(forKey: key)
            try commitIndex(next, directory: directory)
        } catch {}
    }

    public func cancelPending() async {
        fenceQueuedCaptures()
        await cancelAndDrainCaptures()
    }

    /// Explicitly clears cached audio (and paired presentation cache if configured).
    /// Active read leases keep currently playing files alive until playback ends.
    public func clear() async throws {
        guard !closed else { return }
        fenceQueuedCaptures()
        for id in pending.keys { pending[id]?.mayPublish = false }
        for value in pending.values { value.sink.discardPublication() }
        let directory = try open()
        try commitIndex([:], directory: directory, strictDelete: true)
        let keepFiles = protectedFiles
        try directory.recover(keeping: keepFiles, activeTemporaries: Set(pending.values.map(\.sink.name)), strict: true)
        if let presentationCache {
            try await presentationCache.clear()
        }
    }

    /// Revokes official account audio files while preserving independent script history.
    public func clearOfficial() async throws {
        guard !closed else { return }
        let now = clock()
        officialCaptureCutoff = max(officialCaptureCutoff ?? now, now)
        let captures = pending.values.filter { $0.identity.isOfficial }
        for value in captures { cancelCapture(value) }
        for value in captures { _ = await value.task?.value }
        let directory = try open()
        try commitIndex(entries.filter { !$0.value.identity.isOfficial }, directory: directory)
    }

    public func close() async {
        guard !closed else { return }
        closed = true
        await cancelAndDrainCaptures()
        entries.removeAll()
        activeReadLeases.removeAll()
        pendingDeletes.removeAll()
        directory = nil
    }

    private func cancelAndDrainCaptures() async {
        let captures = Array(pending.values)
        for value in captures { cancelCapture(value) }
        for value in captures { _ = await value.task?.value }
        for value in captures { finished(id: value.id) }
    }

    private func fenceQueuedCaptures() {
        let now = clock()
        captureCutoff = max(captureCutoff ?? now, now)
    }

    private func cancelCapture(_ value: Pending) {
        value.fence.cancel(); value.sink.cancel(); value.task?.cancel()
    }

    private func finished(id: UUID) {
        guard let value = pending[id], !value.sink.finishCapture() else { return }
        pending.removeValue(forKey: id)
    }

    private func reserve(id: UUID, bytes: Int64) throws {
        guard !closed, let value = pending[id], value.reserved == 0, bytes > 0, bytes <= capacity else {
            throw NativeMusicAudioCacheFailure.miss
        }
        try value.fence.perform {
            let directory = try open()
            try prune(directory)
            var next = entries
            let protectedKeys = Set(pending.values.map(\.key))
            var total = next.values.reduce(Int64(0)) { $0 + $1.stamp.size } +
                deferredDeleteBytes + bytes +
                pending.values.reduce(Int64(0)) { $0 + $1.reserved }
            for victim in next.values.sorted(by: Self.oldest)
                where total > capacity && !protectedKeys.contains(victim.key) && activeReadLeases[victim.file, default: 0] == 0 {
                next.removeValue(forKey: victim.key)
                total -= victim.stamp.size
            }
            guard total <= capacity else { throw NativeMusicAudioCacheFailure.miss }
            try commitIndex(next, directory: directory)
            try directory.recover(keeping: Set(entries.values.map(\.file)).union(protectedFiles),
                                  activeTemporaries: Set(pending.values.map(\.sink.name)))
            pending[id]?.reserved = bytes
        }
    }

    private func publish(id: UUID, request: MusicAudioFileCacheRequest,
                         inspection: NativeMusicAudioCacheInspection) throws -> MusicAudioFileCacheVersion? {
        guard !closed, let value = pending[id] else { throw NativeMusicAudioCacheFailure.miss }
        return try value.fence.perform {
            let now = clock()
            guard Self.confirmed(request.confirmedAt, now: now),
                  captureCutoff.map({ request.confirmedAt > $0 }) ?? true,
                  !request.identity.isOfficial || (officialCaptureCutoff.map({ request.confirmedAt > $0 }) ?? true),
                  Self.licenseValid(request.audio.fileCachePolicy.licenseExpiresAt, now: now),
                  Self.expiry(request.confirmedAt, request.audio.fileCachePolicy.licenseExpiresAt) > now else {
                throw NativeMusicAudioCacheFailure.miss
            }
            let quality = try inspection.quality(for: request.audio.delivery?.quality ?? request.identity.requestedQuality,
                                                 expectedDuration: request.expectedDuration)
            guard value.mayPublish else { return nil }
            let directory = try open()
            let name = UUID().uuidString.lowercased() + "." + inspection.container
            let stamp = try value.sink.publish(as: name)
            var committed = false
            defer { if !committed { try? directory.remove(name) } }
            let entry = NativeMusicAudioCacheEntry(key: value.key, identity: request.identity, file: name,
                stamp: stamp, inspection: inspection, quality: quality,
                lastPlayedAt: request.confirmedAt,
                licenseExpiresAt: request.audio.fileCachePolicy.licenseExpiresAt,
                expiresAt: Self.expiry(request.confirmedAt, request.audio.fileCachePolicy.licenseExpiresAt))
            var next = entries; next[value.key] = entry
            guard next.count <= 4096,
                  next.values.reduce(Int64(0), { $0 + $1.stamp.size }) + deferredDeleteBytes <= capacity else {
                throw NativeMusicAudioCacheFailure.miss
            }
            try commitIndex(next, directory: directory) {
                try value.fence.check()
                guard Self.expiry(request.confirmedAt, request.audio.fileCachePolicy.licenseExpiresAt) > clock() else {
                    throw NativeMusicAudioCacheFailure.miss
                }
            }
            committed = true
            pending[id]?.reserved = 0
            return .init(rawValue: name)
        }
    }

    private func open() throws -> NativeMusicAudioCacheDirectory {
        guard !closed else { throw NativeMusicAudioCacheFailure.miss }
        if let directory { return directory }
        let value = try NativeMusicAudioCacheDirectory(url: rootURL)
        var loaded: [String: NativeMusicAudioCacheEntry] = [:]
        let saved = try database.readSync { db in
            try db.query(NativeMusicAudioCacheEntry.selectSQL, maximumRows: 4096) { try NativeMusicAudioCacheEntry($0) }
        }
        let now = clock()
        for entry in saved {
            let expectedExpiry = Self.expiry(entry.lastPlayedAt, entry.licenseExpiresAt)
            guard Self.valid(entry.identity), entry.key == Self.key(entry.identity),
                  loaded[entry.key] == nil, Self.confirmed(entry.lastPlayedAt, now: now),
                  Self.licenseValid(entry.licenseExpiresAt, now: now), entry.expiresAt > now,
                  Self.sameEpochMillis(entry.expiresAt, expectedExpiry),
                  (try? entry.inspection.quality(for: entry.quality,
                                                expectedDuration: entry.inspection.duration)) == entry.quality,
                  value.matches(entry.file, stamp: entry.stamp) else { continue }
            loaded[entry.key] = entry
        }
        var total = loaded.values.reduce(Int64(0)) { $0 + $1.stamp.size }
        for entry in loaded.values.sorted(by: Self.oldest) where total > capacity {
            loaded.removeValue(forKey: entry.key); total -= entry.stamp.size
        }
        entries = Dictionary(uniqueKeysWithValues: saved.map { ($0.key, $0) })
        if loaded.count != saved.count {
            do { try commitIndex(loaded, directory: value) }
            catch { entries = [:]; throw error }
        }
        try value.recover(keeping: Set(loaded.values.map(\.file)), activeTemporaries: [])
        directory = value; entries = loaded
        return value
    }

    private func prune(_ directory: NativeMusicAudioCacheDirectory) throws {
        let now = clock()
        let next = entries.filter { _, entry in
            entry.expiresAt > now && Self.licenseValid(entry.licenseExpiresAt, now: now)
        }
        if next.count != entries.count { try commitIndex(next, directory: directory) }
    }

    private func commitIndex(_ next: [String: NativeMusicAudioCacheEntry],
                             directory: NativeMusicAudioCacheDirectory,
                             strictDelete: Bool = false,
                             validate: () throws -> Void = {}) throws {
        let keptFiles = Set(next.values.map(\.file))
        let removedEntries = entries.values.filter { !keptFiles.contains($0.file) }
        let changed = next.values.filter { entries[$0.key]?.cells != $0.cells }
        let deleted = entries.keys.filter { next[$0] == nil }
        if !deleted.isEmpty || !changed.isEmpty {
            try database.writeSync { db in
                try SQLiteDatabase.transaction(db, commit: { action in try validate(); try action() }) { db in
                    for key in deleted { try db.execute("DELETE FROM ios_music_audio_files WHERE cache_key=?", [.text(key)]) }
                    for row in changed {
                        try db.upsert("ios_music_audio_files", columns: NativeMusicAudioCacheEntry.columns, values: row.cells, key: ["cache_key"])
                    }
                }
            }
        } else {
            try validate()
        }
        entries = next
        // Delete unreferenced files outside SQLite transaction, honoring active read leases.
        for item in removedEntries {
            try deleteOrDefer(item.file, size: item.stamp.size, in: directory, strict: strictDelete)
        }
    }

    private static func oldest(_ a: NativeMusicAudioCacheEntry, _ b: NativeMusicAudioCacheEntry) -> Bool {
        a.lastPlayedAt == b.lastPlayedAt ? a.key < b.key : a.lastPlayedAt < b.lastPlayedAt
    }
    private static func key(_ value: MusicAudioFileCacheIdentity) -> String {
        let fields = [value.scope, value.songID, value.provider, value.platform, value.sourceKey, value.requestedQuality]
        let bytes = Data(fields.map { "\($0.utf8.count):\($0)" }.joined().utf8)
        return SHA256.hash(data: bytes).map { String(format: "%02x", $0) }.joined()
    }
    private static func text(_ value: String) -> Bool {
        !value.isEmpty && value.utf8.count <= 2048 &&
        value == value.trimmingCharacters(in: .whitespacesAndNewlines) &&
        !value.unicodeScalars.contains { $0.value < 32 || $0.value == 127 }
    }
    private static func valid(_ value: MusicAudioFileCacheIdentity) -> Bool {
        !value.sourceKey.isEmpty && !value.nativeID.isEmpty &&
            [value.scope, value.songID, value.requestedQuality, value.provider].allSatisfy(text) && value.generation > 0 &&
            (value.isOfficial ? ["wy", "tx", "kg"] : ["wy", "tx", "kg", "kw", "mg"]).contains(value.platform)
    }
    private static func confirmed(_ date: Date, now: Date) -> Bool {
        date.timeIntervalSince1970.isFinite && date.timeIntervalSince1970 > 0 &&
        now.timeIntervalSince1970.isFinite && date <= now
    }
    private static func licenseValid(_ date: Date?, now: Date) -> Bool {
        date.map { $0.timeIntervalSince1970.isFinite && $0 > now } ?? true
    }
    private static func expiry(_ date: Date, _ license: Date?) -> Date {
        min(date.addingTimeInterval(lifetime), license ?? .distantFuture)
    }
    private static func sameEpochMillis(_ a: Date, _ b: Date) -> Bool {
        Int64((a.timeIntervalSince1970 * 1000).rounded()) == Int64((b.timeIntervalSince1970 * 1000).rounded())
    }
}

private struct NativeMusicAudioCacheEntry {
    let key: String
    let identity: MusicAudioFileCacheIdentity
    let file: String
    let stamp: NativeMusicAudioCacheStamp
    let inspection: NativeMusicAudioCacheInspection
    let quality: String
    var lastPlayedAt: Date
    var licenseExpiresAt: Date?
    var expiresAt: Date
    static let columns = ["cache_key", "file", "quality", "last_played_at_ms", "license_expires_at_ms", "expires_at_ms",
        "scope", "song_id", "source_key", "platform", "native_id", "requested_quality", "provider", "generation",
        "stamp_device", "stamp_inode", "stamp_size", "stamp_seconds", "stamp_nanos", "container", "codec", "duration",
        "decoded_frames", "sample_rate", "channels", "bitrate", "bit_depth", "lossless"]
    static let selectSQL = "SELECT " + columns.joined(separator: ",") + " FROM ios_music_audio_files"
    var cells: [SQLiteValue] {
        [.text(key), .text(file), .text(quality), .epochMillis(lastPlayedAt), .optionalEpochMillis(licenseExpiresAt), .epochMillis(expiresAt),
         .text(identity.scope), .text(identity.songID), .text(identity.sourceKey), .text(identity.platform),
         .text(identity.nativeID), .text(identity.requestedQuality), .text(identity.provider), .integer(identity.generation),
         .integer(stamp.device), .text(String(stamp.inode)), .integer(stamp.size), .integer(stamp.seconds), .integer(stamp.nanos),
         .text(inspection.container), .text(inspection.codec), .real(inspection.duration), .integer(inspection.decodedFrames),
         .real(inspection.sampleRate), .integer(inspection.channels), .real(inspection.bitrate), .optional(inspection.bitDepth),
         .bool(inspection.lossless)]
    }
    init(key: String, identity: MusicAudioFileCacheIdentity, file: String, stamp: NativeMusicAudioCacheStamp,
         inspection: NativeMusicAudioCacheInspection, quality: String, lastPlayedAt: Date, licenseExpiresAt: Date?, expiresAt: Date) {
        self.key = key; self.identity = identity; self.file = file; self.stamp = stamp; self.inspection = inspection
        self.quality = quality; self.lastPlayedAt = lastPlayedAt; self.licenseExpiresAt = licenseExpiresAt; self.expiresAt = expiresAt
    }
    init(_ c: SQLiteCursor) throws {
        key = try c.text(0); file = try c.text(1); quality = try c.text(2)
        lastPlayedAt = try c.dateFromMillis(3)
        licenseExpiresAt = try c.optionalDateFromMillis(4)
        expiresAt = try c.dateFromMillis(5)
        identity = .init(scope: try c.text(6), songID: try c.text(7), sourceKey: try c.text(8),
            platform: try c.text(9), nativeID: try c.text(10), requestedQuality: try c.text(11),
            provider: try c.text(12), generation: try c.integer(13))
        guard let inode = UInt64(try c.text(15)) else { throw SQLiteStorageError.corrupt }
        stamp = .init(device: try c.int64(14), inode: inode, size: try c.int64(16),
            seconds: try c.int64(17), nanos: try c.int64(18))
        inspection = .init(container: try c.text(19), codec: try c.text(20), duration: try c.double(21),
            decodedFrames: try c.int64(22), sampleRate: try c.double(23), channels: try c.integer(24),
            bitrate: try c.double(25), bitDepth: try c.optionalInteger(26), lossless: try c.bool(27),
            complete: true, protectedContent: false)
    }
}

final class NativeMusicAudioCacheFence: @unchecked Sendable {
    private let lock = NSRecursiveLock()
    private let authorization: MusicQueueAuthorization
    private let timing: NativeMusicAudioCacheTiming
    private var deadline: NativeMusicAudioCacheDeadline
    private var inspected = false
    private var cancelled = false
    init(authorization: MusicQueueAuthorization, timing: NativeMusicAudioCacheTiming = .init()) {
        self.authorization = authorization; self.timing = timing
        deadline = .init(now: timing.now())
    }
    func cancel() { lock.lock(); cancelled = true; lock.unlock() }
    func check() throws { try perform {} }
    func perform<T>(_ body: () throws -> T) throws -> T {
        try authorization.perform {
            lock.lock(); defer { lock.unlock() }
            guard !cancelled, !Task.isCancelled, inspected || deadline.remaining(at: timing.now()) > .zero else {
                throw NativeMusicAudioCacheFailure.miss
            }
            return try body()
        }
    }
    func received(_ count: Int) throws {
        try perform { try deadline.received(count, at: timing.now()) }
    }
    func completedBody() throws {
        try perform { try deadline.completedBody(at: timing.now()) }
    }
    func finishedInspection() throws {
        try perform { inspected = true }
    }
    private func expiryState() -> (Bool, Duration) {
        lock.lock(); defer { lock.unlock() }
        return (inspected, cancelled ? .zero : deadline.remaining(at: timing.now()))
    }
    func waitForExpiry() async throws -> Bool {
        while true {
            try Task.checkCancellation()
            let (finished, duration) = expiryState()
            if finished { return false }
            if duration <= .zero { return true }
            try await timing.sleep(duration)
        }
    }
}

public final class NativeMusicAudioCacheSink: MusicAudioFilePlayback, @unchecked Sendable {
    public let id: UUID
    private let lock = NSLock()
    private var directory: NativeMusicAudioCacheDirectory?
    private let fence: NativeMusicAudioCacheFence
    private let playbackAuthorization: MusicQueueAuthorization
    private let platform: String
    private let scriptMedia: Bool
    private let streaming: Bool
    private let reserve: @Sendable (Int64) async throws -> Void
    private let clock: @Sendable () -> Date
    private let released: @Sendable () -> Void
    let name: String
    private var descriptor: Int32
    private var expected: Int64?
    private var mime = "application/octet-stream"
    private var received: Int64 = 0
    private var sealed = false
    private var published = false
    private var bodyComplete = false
    private var cancelled = false
    private var sealedStamp: NativeMusicAudioCacheStamp?
    private var readerRetired = false
    private var captureFinished = false
    private var confirmedAt: Date?
    private var publicationDiscarded = false
    private var captureTask: Task<MusicAudioFileCacheVersion?, Never>?
    private var byteInformation: PlaybackByteInformation?
    private var cacheProgress = PlaybackCacheProgress(received: 0, length: 0)
    private var changeRevision: UInt64 = 0
    private var changes: [UUID: CheckedContinuation<Void, Error>] = [:]
    private var progressObservers: [UUID: AsyncStream<PlaybackCacheProgress>.Continuation] = [:]

    init(id: UUID = UUID(), directory: NativeMusicAudioCacheDirectory, fence: NativeMusicAudioCacheFence,
         playbackAuthorization: MusicQueueAuthorization, platform: String, scriptMedia: Bool = false, streaming: Bool = false,
         clock: @escaping @Sendable () -> Date = { Date() },
         released: @escaping @Sendable () -> Void = {},
         reserve: @escaping @Sendable (Int64) async throws -> Void) throws {
        self.directory = directory; self.fence = fence; self.platform = platform; self.scriptMedia = scriptMedia
        self.streaming = streaming; self.reserve = reserve
        self.id = id; self.clock = clock
        self.released = released
        self.playbackAuthorization = playbackAuthorization
        let temporary = UUID().uuidString.lowercased() + ".part"
        name = temporary
        descriptor = try directory.create(temporary)
    }

    public func accept(_ response: NativeMusicAudioCacheHTTPResponse) async throws {
        try checkReceivingAuthorization()
        let length = try NativeMusicAudioCacheHTTPPolicy.length(response, platform: platform, scriptMedia: scriptMedia, streaming: streaming)
        try await reserve(length ?? NativeMusicAudioCacheHTTPPolicy.maximumBytes)
        try accepted(length)
        let mime = response.headers.first { $0.key.lowercased() == "content-type" }?.value
            .split(separator: ";", maxSplits: 1).first.map(String.init) ?? "application/octet-stream"
        lock.withLock {
            self.mime = mime
            byteInformation = length.map { .init(length: $0, mime: mime) }
            cacheProgress = .init(received: 0, length: length ?? 0)
        }
        notifyChanges()
    }

    func checkReceivingAuthorization() throws {
        try playbackAuthorization.perform { try fence.check() }
    }

    private func accepted(_ length: Int64?) throws {
        try fence.perform {
            lock.lock(); defer { lock.unlock() }
            guard descriptor >= 0, expected == nil, !sealed, !cancelled else { throw NativeMusicAudioCacheFailure.miss }
            expected = length
        }
    }

    public func write(_ data: Data) throws {
        try fence.perform {
            lock.lock(); defer { lock.unlock() }
            guard descriptor >= 0, !sealed, !bodyComplete, !cancelled,
                  Int64(data.count) <= (expected ?? NativeMusicAudioCacheHTTPPolicy.maximumBytes) - received else {
                throw NativeMusicAudioCacheFailure.miss
            }
            try NativeMusicAudioCacheDirectory.write(data, to: descriptor)
            received += Int64(data.count)
            if let expected, received < expected { cacheProgress = .init(received: received, length: expected) }
            try fence.received(data.count)
        }
        notifyChanges()
    }

    public func finishBody() throws {
        try playbackAuthorization.perform {
            try fence.perform {
                lock.lock(); defer { lock.unlock() }
                guard descriptor >= 0, !cancelled, !bodyComplete, !sealed,
                      received > 0, expected.map({ $0 == received }) ?? true else { throw NativeMusicAudioCacheFailure.miss }
                expected = received
                byteInformation = .init(length: received, mime: mime)
                try fence.completedBody()
                bodyComplete = true
            }
        }
        notifyChanges()
    }

    func retireIfIncomplete() -> Bool {
        lock.lock()
        if bodyComplete { lock.unlock(); return false }
        cancelled = true
        lock.unlock()
        fence.cancel(); cancel()
        return true
    }

    func seal() throws -> NativeMusicAudioCacheFile {
        try fence.perform {
            lock.lock(); defer { lock.unlock() }
            guard descriptor >= 0, !sealed, bodyComplete, !cancelled, let expected, received == expected else {
                throw NativeMusicAudioCacheFailure.miss
            }
            guard fsync(descriptor) == 0 else { throw NativeMusicAudioCacheFailure.miss }
            let pinned = try NativeMusicAudioCacheDirectory.pinnedStamp(descriptor)
            guard pinned.size == expected else { throw NativeMusicAudioCacheFailure.miss }
            let copy = fcntl(descriptor, F_DUPFD_CLOEXEC, 0)
            guard copy >= 0 else { throw NativeMusicAudioCacheFailure.miss }
            sealed = true; sealedStamp = pinned
            return NativeMusicAudioCacheFile(descriptor: copy, size: expected, fence: fence)
        }
    }

    func publish(as targetName: String) throws -> NativeMusicAudioCacheStamp {
        lock.lock(); defer { lock.unlock() }
        guard descriptor >= 0, sealed, !published, let directory, let sealedStamp else {
            throw NativeMusicAudioCacheFailure.miss
        }
        try directory.rename(self.name, to: targetName)
        published = true
        return sealedStamp
    }

    func cancel() {
        lock.lock()
        let wasCancelled = cancelled
        cancelled = true
        let fd = descriptor; descriptor = -1
        let remove = !published
        let directory = self.directory; self.directory = nil
        let task = captureTask
        lock.unlock()
        task?.cancel()
        notifyChanges()
        if fd >= 0 { Darwin.close(fd) }
        if remove { try? directory?.remove(name) }
        if !wasCancelled { released() }
    }

    func attach(_ task: Task<MusicAudioFileCacheVersion?, Never>) {
        lock.lock(); captureTask = task
        let stopped = cancelled
        lock.unlock()
        if stopped { task.cancel() }
    }

    @discardableResult func finishCapture() -> Bool {
        lock.lock()
        captureFinished = true
        let release = cancelled || readerRetired
        let retain = !published && !release
        lock.unlock()
        if release { cancel() }
        return retain
    }

    func complete(published: Bool) {
        lock.lock()
        if published { cacheProgress = .init(received: received, length: expected ?? received, complete: true) }
        lock.unlock()
        notifyChanges()
    }

    public var progress: PlaybackCacheProgress {
        lock.lock(); defer { lock.unlock() }
        return cacheProgress
    }

    public func updates() -> AsyncStream<PlaybackCacheProgress> {
        AsyncStream(bufferingPolicy: .bufferingNewest(1)) { continuation in
            let id = UUID()
            lock.lock()
            let ended = cancelled || readerRetired
            if !ended { progressObservers[id] = continuation }
            continuation.yield(cacheProgress)
            lock.unlock()
            if ended { continuation.finish() }
            continuation.onTermination = { [weak self] _ in
                guard let self else { return }
                self.lock.lock(); self.progressObservers[id] = nil; self.lock.unlock()
            }
        }
    }

    private func notifyChanges() {
        lock.lock()
        changeRevision &+= 1
        let waiters = Array(changes.values); changes.removeAll()
        let observers = Array(progressObservers.values), progress = cacheProgress
        let ended = cancelled || readerRetired
        if ended { progressObservers.removeAll() }
        lock.unlock()
        for waiter in waiters { waiter.resume() }
        for observer in observers {
            observer.yield(progress)
            if ended { observer.finish() }
        }
    }

    private func waitForChange(after revision: UInt64) async throws {
        let id = UUID()
        try await withTaskCancellationHandler {
            try Task.checkCancellation()
            try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<Void, Error>) in
                lock.lock()
                if Task.isCancelled { lock.unlock(); continuation.resume(throwing: CancellationError()) }
                else if cancelled || changeRevision != revision { lock.unlock(); continuation.resume() }
                else { changes[id] = continuation; lock.unlock() }
            }
        } onCancel: {
            self.lock.lock(); let waiter = self.changes.removeValue(forKey: id); self.lock.unlock()
            waiter?.resume(throwing: CancellationError())
        }
    }

    public func information() async throws -> PlaybackByteInformation {
        while true {
            try Task.checkCancellation()
            try fence.check()
            let (information, revision, cancelled, retired) = lock.withLock { (byteInformation, changeRevision, self.cancelled, readerRetired) }
            if retired { throw CancellationError() }
            if cancelled { throw NativeMusicAudioCacheFailure.miss }
            if let information { return information }
            try await waitForChange(after: revision)
        }
    }

    public func read(offset: Int64, count: Int) async throws -> Data {
        guard offset >= 0, count > 0, count <= 65_536 else { throw NativeMusicAudioCacheFailure.miss }
        while true {
            try Task.checkCancellation()
            try fence.check()
            let (revision, data): (UInt64, Data?) = try lock.withLock {
                if readerRetired { throw CancellationError() }
                if cancelled { throw NativeMusicAudioCacheFailure.miss }
                guard let expected, offset <= expected else { throw NativeMusicAudioCacheFailure.miss }
                if offset == expected { return (changeRevision, Data()) }
                guard offset < received else { return (changeRevision, nil) }
                let length = Int(min(Int64(count), received - offset))
                var data = Data(count: length)
                var result: Int
                repeat {
                    result = data.withUnsafeMutableBytes { bytes in
                        Darwin.pread(descriptor, bytes.baseAddress, length, off_t(offset))
                    }
                } while result < 0 && errno == EINTR
                guard result == length else { throw NativeMusicAudioCacheFailure.miss }
                return (changeRevision, data)
            }
            if let data {
                try Task.checkCancellation()
                return data
            }
            try await waitForChange(after: revision)
        }
    }

    public func confirmPlayback(at date: Date) {
        guard date.timeIntervalSince1970.isFinite, date.timeIntervalSince1970 > 0, date <= clock() else { return }
        lock.lock()
        if !cancelled, confirmedAt == nil { confirmedAt = date }
        lock.unlock()
        notifyChanges()
    }

    func firstPlayback() async throws -> Date? {
        while true {
            try Task.checkCancellation()
            let (date, revision, ended, discarded) = lock.withLock {
                (confirmedAt, changeRevision, cancelled || readerRetired && confirmedAt == nil, publicationDiscarded)
            }
            if ended { throw CancellationError() }
            if discarded { return nil }
            if let date { return date }
            try await waitForChange(after: revision)
        }
    }

    func discardPublication() {
        lock.withLock { publicationDiscarded = true }
        notifyChanges()
    }

    public func result() async -> MusicAudioFileCacheVersion? {
        let task = lock.withLock { captureTask }
        return await task?.value
    }

    public func retire() {
        lock.lock()
        readerRetired = true
        let incomplete = !bodyComplete || confirmedAt == nil, release = captureFinished
        lock.unlock()
        playbackAuthorization.revoke()
        notifyChanges()
        if incomplete || release { cancel() }
    }

    deinit { cancel() }
}

public final class NativeMusicAudioCacheFile: @unchecked Sendable {
    private let descriptor: Int32
    private let fence: NativeMusicAudioCacheFence
    public let size: Int64
    init(descriptor: Int32, size: Int64, fence: NativeMusicAudioCacheFence) {
        self.descriptor = descriptor; self.size = size; self.fence = fence
    }
    public func checkCancellation() throws { try fence.check() }
    public func read(offset: Int64, count: Int) throws -> Data {
        guard count >= 0, count <= 1024 * 1024, offset >= 0, offset <= size,
              Int64(count) <= size - offset else { throw NativeMusicAudioCacheFailure.miss }
        var data = Data(count: count)
        let received = try data.withUnsafeMutableBytes { buffer in
            try self.read(offset: offset, into: buffer.baseAddress, count: count)
        }
        guard received == count else { throw NativeMusicAudioCacheFailure.miss }
        return data
    }
    func read(offset: Int64, into buffer: UnsafeMutableRawPointer?, count: Int) throws -> Int {
        try fence.check()
        guard offset >= 0, offset <= size, count >= 0 else { throw NativeMusicAudioCacheFailure.miss }
        let count = min(count, Int(size - offset))
        if count == 0 { return 0 }
        guard let buffer else { throw NativeMusicAudioCacheFailure.miss }
        var done = 0
        while done < count {
            try fence.check()
            let amount = pread(descriptor, buffer.advanced(by: done), min(65_536, count - done), off_t(offset) + off_t(done))
            if amount < 0 && errno == EINTR { continue }
            guard amount > 0 else { throw NativeMusicAudioCacheFailure.miss }
            done += amount
        }
        return done
    }
    deinit { Darwin.close(descriptor) }
}

struct NativeMusicAudioCacheStamp: Codable, Equatable {
    let device: Int64
    let inode: UInt64
    let size: Int64
    let seconds: Int64
    let nanos: Int64
    init(device: Int64, inode: UInt64, size: Int64, seconds: Int64, nanos: Int64) {
        self.device = device; self.inode = inode; self.size = size; self.seconds = seconds; self.nanos = nanos
    }
    init(_ value: stat) {
        device = Int64(value.st_dev); inode = UInt64(value.st_ino); size = Int64(value.st_size)
        seconds = Int64(value.st_mtimespec.tv_sec); nanos = Int64(value.st_mtimespec.tv_nsec)
    }
}

/// Local atomic cache directory for `<dataRoot>/resources-v1/music-audio`.
/// Excluded from iCloud backup; stages writes to `.part` and atomically renames on commit.
final class NativeMusicAudioCacheDirectory: @unchecked Sendable {
    let url: URL

    init(url: URL) throws {
        let normalized = url.standardizedFileURL
        guard normalized.isFileURL else { throw NativeMusicAudioCacheFailure.miss }
        let fm = FileManager.default
        do {
            try fm.createDirectory(at: normalized, withIntermediateDirectories: true)
        } catch {
            throw NativeMusicAudioCacheFailure.miss
        }
        #if os(iOS)
        Self.excludeFromBackup(normalized.deletingLastPathComponent())
        Self.excludeFromBackup(normalized)
        #endif
        self.url = normalized
    }

    func create(_ name: String) throws -> Int32 {
        guard Self.temporary(name) else { throw NativeMusicAudioCacheFailure.miss }
        let path = url.appendingPathComponent(name, isDirectory: false).path
        let fd = Darwin.open(path, O_RDWR | O_CREAT | O_EXCL | O_CLOEXEC, 0o600)
        guard fd >= 0 else { throw NativeMusicAudioCacheFailure.miss }
        return fd
    }

    func matches(_ name: String, stamp: NativeMusicAudioCacheStamp) -> Bool {
        guard Self.media(name), stamp.size > 0, stamp.size <= NativeMusicAudioCacheHTTPPolicy.maximumBytes else {
            return false
        }
        let path = url.appendingPathComponent(name, isDirectory: false).path
        var info = stat()
        guard stat(path, &info) == 0, info.st_mode & mode_t(S_IFMT) == mode_t(S_IFREG) else { return false }
        return Int64(info.st_size) == stamp.size
    }

    func remove(_ name: String) throws {
        let path = url.appendingPathComponent(name, isDirectory: false).path
        if unlink(path) != 0 && errno != ENOENT {
            throw NativeMusicAudioCacheFailure.miss
        }
    }

    func rename(_ from: String, to: String) throws {
        guard Self.temporary(from), Self.media(to) else { throw NativeMusicAudioCacheFailure.miss }
        let src = url.appendingPathComponent(from, isDirectory: false).path
        let dst = url.appendingPathComponent(to, isDirectory: false).path
        guard Darwin.rename(src, dst) == 0 else { throw NativeMusicAudioCacheFailure.miss }
    }

    /// Removes unindexed audio files, interrupted `.part` files, and stray leftovers.
    func recover(keeping: Set<String>, activeTemporaries: Set<String>, strict: Bool = false) throws {
        let fm = FileManager.default
        guard fm.fileExists(atPath: url.path) else { return }
        let names: [String]
        do {
            names = try fm.contentsOfDirectory(atPath: url.path)
        } catch {
            throw NativeMusicAudioCacheFailure.miss
        }
        for name in names {
            if Self.media(name) && keeping.contains(name) { continue }
            if Self.temporary(name) && activeTemporaries.contains(name) { continue }
            let target = url.appendingPathComponent(name, isDirectory: false)
            if strict {
                do {
                    try fm.removeItem(at: target)
                } catch CocoaError.fileNoSuchFile {
                    continue
                } catch {
                    throw NativeMusicAudioCacheFailure.miss
                }
            } else {
                try? fm.removeItem(at: target)
            }
        }
    }

    static func pinnedStamp(_ descriptor: Int32) throws -> NativeMusicAudioCacheStamp {
        var info = stat()
        guard fstat(descriptor, &info) == 0, info.st_mode & mode_t(S_IFMT) == mode_t(S_IFREG),
              info.st_size >= 0 else { throw NativeMusicAudioCacheFailure.miss }
        return NativeMusicAudioCacheStamp(info)
    }

    static func write(_ data: Data, to fd: Int32) throws {
        try data.withUnsafeBytes { buffer in
            guard let base = buffer.baseAddress else { return }
            var offset = 0
            while offset < buffer.count {
                let count = Darwin.write(fd, base.advanced(by: offset), buffer.count - offset)
                if count < 0 && errno == EINTR { continue }
                guard count > 0 else { throw NativeMusicAudioCacheFailure.miss }
                offset += count
            }
        }
    }

    private static func media(_ name: String) -> Bool { named(name, extensions: ["mp3", "flac", "m4a", "wav"]) }
    private static func temporary(_ name: String) -> Bool { named(name, extensions: ["part"]) }
    private static func named(_ name: String, extensions: Set<String>) -> Bool {
        let pieces = name.split(separator: ".", omittingEmptySubsequences: false)
        guard pieces.count == 2, extensions.contains(String(pieces[1])),
              let id = UUID(uuidString: String(pieces[0])) else { return false }
        return id.uuidString.lowercased() == pieces[0]
    }

    #if os(iOS)
    private static func excludeFromBackup(_ url: URL) {
        var mutable = url
        var values = URLResourceValues()
        values.isExcludedFromBackup = true
        try? mutable.setResourceValues(values)
    }
    #endif
}
