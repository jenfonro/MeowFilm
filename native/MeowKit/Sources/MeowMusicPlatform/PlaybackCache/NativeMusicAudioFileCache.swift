import Foundation
import Darwin
import MeowDebug
import MeowContracts
import MeowStorage

/// Product-owned, evictable audio file cache (256 MiB, 30-day TTL).
/// File I/O runs outside SQLite write transactions; active read leases protect playing files from unlink.
public actor NativeMusicAudioFileCache {
    private let rootURL: URL
    private let database: SQLiteDatabase
    private let transport = URLSessionNativeMusicAudioCacheTransport()
    private let inspector = NativeMusicAudioCacheInspector()
    private let capacity = NativeMusicAudioCacheHTTPPolicy.maximumBytes
    private var directory: NativeMusicAudioCacheDirectory?
    private var entries: [String: NativeMusicAudioCacheEntry] = [:]
    private var pending: [UUID: Pending] = [:]
    private var activeReadLeases: [String: Int] = [:]
    private var pendingDeletes: [String: Int64] = [:]
    private var closed = false
    private var captureCutoff: Date?
    private var officialCaptureCutoff: Date?
    private static let lifetime: TimeInterval = 30 * 86_400
    private static let maximumEntries = 2048

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

    public init(database: SQLiteDatabase, rootURL: URL) {
        self.database = database; self.rootURL = rootURL
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
                do {
                    try directory?.remove(file)
                } catch {
                    DebugRuntime.logError(.storage, sub: "music.audioCache", event: "deferredDelete.failed", error: error)
                }
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
                do {
                    try directory.remove(file)
                } catch {
                    DebugRuntime.logError(.storage, sub: "music.audioCache", event: "delete.failed", error: error)
                }
            }
        }
    }

    private var protectedFiles: Set<String> {
        Set(activeReadLeases.keys).union(pendingDeletes.keys)
    }

    private var deferredDeleteBytes: Int64 {
        pendingDeletes.values.reduce(0, +)
    }

    private static func actualDiskBytes(in directoryURL: URL) -> Int64 {
        let normalized = directoryURL.standardizedFileURL
        guard normalized.isFileURL else { return 0 }
        let fm = FileManager.default
        guard fm.fileExists(atPath: normalized.path) else { return 0 }
        let names: [String]
        do {
            names = try fm.contentsOfDirectory(atPath: normalized.path)
        } catch {
            return 0
        }
        var total: Int64 = 0
        for name in names {
            let path = normalized.appendingPathComponent(name, isDirectory: false).path
            var info = stat()
            if lstat(path, &info) == 0, info.st_mode & mode_t(S_IFMT) == mode_t(S_IFREG), info.st_size > 0 {
                total += Int64(info.st_size)
            }
        }
        return total
    }

    public func stats() -> MusicAudioFileCacheStats {
        let entryCount: Int
        if directory != nil || closed {
            entryCount = entries.count
        } else {
            do {
                entryCount = try database.readSync { db in
                    try db.queryOne("SELECT count(*) FROM ios_music_audio_files") {
                        try $0.integer(0)
                    }
                } ?? 0
            } catch {
                DebugRuntime.logError(.storage, sub: "music.audioCache", event: "stats.error", error: error)
                entryCount = entries.count
            }
        }
        let deferred = deferredDeleteBytes
        let reserved = pending.values.reduce(Int64(0)) { $0 + $1.reserved }
        let leaseCount = activeReadLeases.values.reduce(0, +)
        let diskBytes = closed ? 0 : Self.actualDiskBytes(in: directory?.url ?? rootURL)
        return MusicAudioFileCacheStats(
            entryCount: entryCount,
            totalDiskBytes: diskBytes,
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
                    if directory.matches(candidate.file, expectedSize: candidate.fileSize) {
                        selected = candidate
                        break
                    }
                    brokenKeys.append(candidate.key)
                }
                if !brokenKeys.isEmpty {
                    var next = entries
                    for broken in brokenKeys { next.removeValue(forKey: broken) }
                    do {
                        try commitIndex(next, directory: directory)
                    } catch {
                        DebugRuntime.logError(.storage, sub: "music.audioCache", event: "lookup.pruneBrokenFailed", error: error)
                    }
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
        } catch {
            if !(error is CancellationError) {
                DebugRuntime.logError(.storage, sub: "music.audioCache", event: "lookup.error", error: error)
            }
            return nil
        }
    }

    public func preparePlayback(_ request: MusicAudioFilePlaybackRequest) async throws -> NativeMusicAudioCacheSink {
        let now = Date()
        guard !closed, !Task.isCancelled, request.authorization.isActive, request.playbackAuthorization.isActive,
              request.audio.playbackAuthorization?.isActive != false, Self.valid(request.identity),
              request.expectedDuration.isFinite, request.expectedDuration >= 0,
              NativeMusicAudioCacheHTTPPolicy.permits(request.audio.url),
              request.audio.expiresAt.map({ $0.timeIntervalSince1970.isFinite && $0 > now }) ?? true else {
            throw NativeMusicAudioCacheFailure.miss
        }
        let persist = request.audio.fileCachePolicy.allowed &&
            NativeMusicAudioCacheInspection.supportedRequest(request.audio.delivery?.quality ?? request.identity.requestedQuality)
        let key = Self.key(request.identity)
        let id = UUID(), fence = NativeMusicAudioCacheFence(authorization: request.authorization)
        let directory = try request.authorization.perform { try open() }
        try prune(directory)
        try request.authorization.check(); try request.playbackAuthorization.check()
        let sink = try NativeMusicAudioCacheSink(id: id, directory: directory, fence: fence,
            playbackAuthorization: request.playbackAuthorization,
            released: { [weak self] in Task { await self?.finished(id: id) } }) { [weak self] length in
                guard let self else { throw NativeMusicAudioCacheFailure.miss }
                try await self.reserve(id: id, bytes: length)
            }
        pending[id] = Pending(id: id, key: key, identity: request.identity, fence: fence, sink: sink)
        let http = NativeMusicAudioCacheHTTPRequest(url: request.audio.url, headers: request.audio.headers)
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
            let version: MusicAudioFileCacheVersion?
            do {
                try sink.checkReceivingAuthorization()
                try await transport.transfer(http, into: sink)
                let file = try await Task.detached(priority: .utility) { try sink.seal() }.value
                if persist {
                    let measured = try await inspector.inspect(file)
                    try fence.check()
                    if let confirmed = try await sink.firstPlayback() {
                        let capture = MusicAudioFileCacheRequest(identity: request.identity, audio: request.audio,
                            expectedDuration: request.expectedDuration, confirmedAt: confirmed,
                            authorization: request.authorization, playbackAuthorization: request.playbackAuthorization)
                        version = try self.publish(id: id, request: capture, inspection: measured)
                    } else { version = nil }
                } else {
                    try fence.check()
                    version = nil
                }
                sink.complete(published: version != nil)
            } catch {
                if !(error is CancellationError) {
                    DebugRuntime.logError(.storage, sub: "music.audioCache", event: "capture.error", error: error)
                }
                sink.cancel()
                version = nil
            }
            revocation.cancel(); playbackRevocation.cancel()
            await revocation.value; await playbackRevocation.value
            self.finished(id: id)
            return version
        }
        pending[id]?.task = task
        sink.attach(task)
        return sink
    }

    public func touch(_ identity: MusicAudioFileCacheIdentity, version: MusicAudioFileCacheVersion, confirmedAt: Date,
                      authorization: MusicQueueAuthorization) async {
        guard !closed, !Task.isCancelled, authorization.isActive,
              Self.valid(identity), Self.confirmed(confirmedAt, now: Date()) else { return }
        do {
            try authorization.perform {
                let directory = try open()
                try prune(directory)
                let key = Self.key(identity)
                guard var entry = entries[key], entry.identity == identity, entry.file == version.rawValue,
                      directory.matches(entry.file, expectedSize: entry.fileSize) else { return }
                entry.lastPlayedAt = max(entry.lastPlayedAt, confirmedAt)
                entry.expiresAt = Self.expiry(entry.lastPlayedAt)
                var next = entries; next[key] = entry
                try commitIndex(next, directory: directory)
            }
        } catch {
            DebugRuntime.logError(.storage, sub: "music.audioCache", event: "touch.error", error: error)
        }
    }

    public func remove(_ identity: MusicAudioFileCacheIdentity, version: MusicAudioFileCacheVersion) async {
        guard !closed, Self.valid(identity) else { return }
        do {
            let directory = try open(), key = Self.key(identity)
            guard entries[key]?.identity == identity, entries[key]?.file == version.rawValue else { return }
            var next = entries; next.removeValue(forKey: key)
            try commitIndex(next, directory: directory)
        } catch {
            DebugRuntime.logError(.storage, sub: "music.audioCache", event: "remove.error", error: error)
        }
    }

    public func cancelPending() async {
        fenceQueuedCaptures()
        await cancelAndDrainCaptures()
    }

    /// Explicitly clears cached audio; decoupled from presentation cache.
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
    }

    /// Revokes official account audio files while preserving independent script history.
    public func clearOfficial() async throws {
        guard !closed else { return }
        let now = Date()
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
        if let directory {
            for file in pendingDeletes.keys where activeReadLeases[file, default: 0] == 0 {
                do {
                    try directory.remove(file)
                } catch {
                    DebugRuntime.logError(.storage, sub: "music.audioCache", event: "close.unlinkPendingDeleteError", error: error)
                }
            }
        }
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
        // Also reject previously confirmed requests whose caller Task has not
        // entered this actor yet. A later real-playing event can start anew.
        // Equal/backward wall times conservatively miss rather than resurrect
        // pre-clear work; publication itself uses the independent monotonic cap.
        let now = Date()
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
            let otherPendingKeys = Set(pending.values.filter { $0.id != id }.map(\.key))
            var total = next.values.reduce(Int64(0)) { $0 + $1.fileSize } +
                deferredDeleteBytes + bytes +
                pending.values.reduce(Int64(0)) { $0 + $1.reserved }
            for victim in next.values.sorted(by: Self.oldest)
                where total > capacity && !otherPendingKeys.contains(victim.key) && activeReadLeases[victim.file, default: 0] == 0 {
                next.removeValue(forKey: victim.key)
                total -= victim.fileSize
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
            let now = Date()
            guard Self.confirmed(request.confirmedAt, now: now),
                  captureCutoff.map({ request.confirmedAt > $0 }) ?? true,
                  !request.identity.isOfficial || (officialCaptureCutoff.map({ request.confirmedAt > $0 }) ?? true),
                  Self.expiry(request.confirmedAt) > now else {
                throw NativeMusicAudioCacheFailure.miss
            }
            let quality = try inspection.quality(for: request.audio.delivery?.quality ?? request.identity.requestedQuality)
            guard value.mayPublish else { return nil }
            let directory = try open()
            let name = UUID().uuidString.lowercased() + "." + inspection.container
            let fileSize = try value.sink.publish(as: name)
            var committed = false
            defer {
                if !committed {
                    do { try directory.remove(name) }
                    catch {
                        DebugRuntime.logError(.storage, sub: "music.audioCache", event: "rollback.removeFailed", error: error)
                    }
                }
            }
            let entry = NativeMusicAudioCacheEntry(key: value.key, identity: request.identity, file: name,
                fileSize: fileSize, inspection: inspection, quality: quality,
                lastPlayedAt: request.confirmedAt,
                expiresAt: Self.expiry(request.confirmedAt))
            let replaced = entries[value.key]
            let replacedDeferredBytes: Int64 = {
                guard let replaced, replaced.file != name, activeReadLeases[replaced.file, default: 0] > 0 else { return 0 }
                return max(0, replaced.fileSize - (pendingDeletes[replaced.file] ?? 0))
            }()
            var next = entries; next[value.key] = entry
            let otherPendingKeys = Set(pending.values.filter { $0.id != id }.map(\.key))
            var totalBytes = next.values.reduce(Int64(0)) { $0 + $1.fileSize } + deferredDeleteBytes + replacedDeferredBytes
            if next.count > Self.maximumEntries || totalBytes > capacity {
                for victim in next.values.sorted(by: Self.oldest)
                where (next.count > Self.maximumEntries || totalBytes > capacity) &&
                    victim.key != value.key &&
                    !otherPendingKeys.contains(victim.key) &&
                    activeReadLeases[victim.file, default: 0] == 0 {
                    next.removeValue(forKey: victim.key)
                    totalBytes -= victim.fileSize
                }
            }
            guard next.count <= Self.maximumEntries, totalBytes <= capacity else {
                throw NativeMusicAudioCacheFailure.miss
            }
            try commitIndex(next, directory: directory) {
                try value.fence.check()
                guard Self.expiry(request.confirmedAt) > Date() else {
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
            try db.query(NativeMusicAudioCacheEntry.selectSQL, maximumRows: Self.maximumEntries) { try NativeMusicAudioCacheEntry($0) }
        }
        let now = Date()
        for entry in saved {
            guard entry.expiresAt > now, value.matches(entry.file, expectedSize: entry.fileSize) else { continue }
            loaded[entry.key] = entry
        }
        var total = loaded.values.reduce(Int64(0)) { $0 + $1.fileSize }
        for entry in loaded.values.sorted(by: Self.oldest) where total > capacity || loaded.count > Self.maximumEntries {
            loaded.removeValue(forKey: entry.key); total -= entry.fileSize
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
        let now = Date()
        let next = entries.filter { _, entry in
            entry.expiresAt > now
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
            try deleteOrDefer(item.file, size: item.fileSize, in: directory, strict: strictDelete)
        }
    }

    private static func oldest(_ a: NativeMusicAudioCacheEntry, _ b: NativeMusicAudioCacheEntry) -> Bool {
        a.lastPlayedAt == b.lastPlayedAt ? a.key < b.key : a.lastPlayedAt < b.lastPlayedAt
    }
    private static func key(_ value: MusicAudioFileCacheIdentity) -> String {
        let fields = [value.scope, value.songID, value.provider, value.platform, value.sourceKey, value.requestedQuality]
        let bytes = Data(fields.map { "\($0.utf8.count):\($0)" }.joined().utf8)
        return musicSHA256Hex(bytes)
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
        now.timeIntervalSince1970.isFinite
    }
    private static func expiry(_ date: Date) -> Date {
        date.addingTimeInterval(lifetime)
    }
}

private struct NativeMusicAudioCacheEntry {
    let key: String
    let identity: MusicAudioFileCacheIdentity
    let file: String
    let fileSize: Int64
    let inspection: NativeMusicAudioCacheInspection
    let quality: String
    var lastPlayedAt: Date
    var expiresAt: Date
    static let columns = ["cache_key", "file", "quality", "last_played_at_ms", "expires_at_ms",
        "scope", "song_id", "source_key", "platform", "native_id", "requested_quality", "provider", "generation",
        "file_size", "container", "codec", "duration", "decoded_frames", "sample_rate", "channels", "bitrate",
        "bit_depth", "lossless"]
    static let selectSQL = "SELECT " + columns.joined(separator: ",") + " FROM ios_music_audio_files"
    var cells: [SQLiteValue] {
        [.text(key), .text(file), .text(quality), .epochMillis(lastPlayedAt), .epochMillis(expiresAt),
         .text(identity.scope), .text(identity.songID), .text(identity.sourceKey), .text(identity.platform),
         .text(identity.nativeID), .text(identity.requestedQuality), .text(identity.provider), .integer(identity.generation),
         .integer(fileSize), .text(inspection.container), .text(inspection.codec), .real(inspection.duration),
         .integer(inspection.decodedFrames), .real(inspection.sampleRate), .integer(inspection.channels),
         .real(inspection.bitrate), .optional(inspection.bitDepth), .bool(inspection.lossless)]
    }
    init(key: String, identity: MusicAudioFileCacheIdentity, file: String, fileSize: Int64,
         inspection: NativeMusicAudioCacheInspection, quality: String, lastPlayedAt: Date, expiresAt: Date) {
        self.key = key; self.identity = identity; self.file = file; self.fileSize = fileSize; self.inspection = inspection
        self.quality = quality; self.lastPlayedAt = lastPlayedAt; self.expiresAt = expiresAt
    }
    init(_ c: SQLiteCursor) throws {
        key = try c.text(0); file = try c.text(1); quality = try c.text(2)
        lastPlayedAt = try c.dateFromMillis(3)
        expiresAt = try c.dateFromMillis(4)
        identity = .init(scope: try c.text(5), songID: try c.text(6), sourceKey: try c.text(7),
            platform: try c.text(8), nativeID: try c.text(9), requestedQuality: try c.text(10),
            provider: try c.text(11), generation: try c.integer(12))
        fileSize = try c.int64(13)
        inspection = .init(container: try c.text(14), codec: try c.text(15), duration: try c.double(16),
            decodedFrames: try c.int64(17), sampleRate: try c.double(18), channels: try c.integer(19),
            bitrate: try c.double(20), bitDepth: try c.optionalInteger(21), lossless: try c.bool(22))
    }
}

final class NativeMusicAudioCacheFence: @unchecked Sendable {
    private let lock = NSLock()
    private let authorization: MusicQueueAuthorization
    private var cancelled = false
    init(authorization: MusicQueueAuthorization) { self.authorization = authorization }
    func cancel() { lock.withLock { cancelled = true } }
    func check() throws { try perform {} }
    func perform<T>(_ body: () throws -> T) throws -> T {
        try authorization.perform {
            guard !lock.withLock({ cancelled }), !Task.isCancelled else {
                throw NativeMusicAudioCacheFailure.miss
            }
            return try body()
        }
    }
}

/// The transport cannot choose a destination or obtain a path/file handle.
/// Bounds, EOF, disk reservation and cancellation are enforced by this sink.
public final class NativeMusicAudioCacheSink: PlaybackByteSource, @unchecked Sendable {
    public let id: UUID
    private let lock = NSLock()
    private var directory: NativeMusicAudioCacheDirectory?
    private let fence: NativeMusicAudioCacheFence
    private let playbackAuthorization: MusicQueueAuthorization
    private let reserve: @Sendable (Int64) async throws -> Void
    private let released: @Sendable () -> Void
    let name: String
    private var descriptor: Int32
    private var activePreads: Int = 0
    private var pendingCloseFD: Int32 = -1
    private var expected: Int64?
    private var mime = "application/octet-stream"
    private var received: Int64 = 0
    private var sealed = false
    private var published = false
    private var bodyComplete = false
    private var cancelled = false
    private var sealedSize: Int64?
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
         playbackAuthorization: MusicQueueAuthorization,
         released: @escaping @Sendable () -> Void = {},
         reserve: @escaping @Sendable (Int64) async throws -> Void) throws {
        self.directory = directory; self.fence = fence
        self.reserve = reserve
        self.id = id
        self.released = released
        self.playbackAuthorization = playbackAuthorization
        let temporary = UUID().uuidString.lowercased() + ".part"
        name = temporary
        descriptor = try directory.create(temporary)
    }

    func accept(_ response: NativeMusicAudioCacheHTTPResponse) async throws {
        try checkReceivingAuthorization()
        let length = try NativeMusicAudioCacheHTTPPolicy.length(response)
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
    func write(_ data: Data) throws {
        try fence.perform {
            lock.lock(); defer { lock.unlock() }
            guard descriptor >= 0, !sealed, !bodyComplete, !cancelled,
                  Int64(data.count) <= (expected ?? NativeMusicAudioCacheHTTPPolicy.maximumBytes) - received else {
                throw NativeMusicAudioCacheFailure.miss
            }
            try NativeMusicAudioCacheDirectory.write(data, to: descriptor)
            received += Int64(data.count)
            if let expected, received < expected { cacheProgress = .init(received: received, length: expected) }
        }
        notifyChanges()
    }

    /// Called by the transport at successful HTTP EOF, never by a byte-count
    /// heuristic. This linearizes a song switch against body completion.
    func finishBody() throws {
        // A delayed AsyncStream revocation observer cannot promote bytes to a
        // complete body after this request's selection lifetime has ended.
        try playbackAuthorization.perform {
            try fence.perform {
                lock.lock(); defer { lock.unlock() }
                guard descriptor >= 0, !cancelled, !bodyComplete, !sealed,
                      received > 0, expected.map({ $0 == received }) ?? true else { throw NativeMusicAudioCacheFailure.miss }
                expected = received
                byteInformation = .init(length: received, mime: mime)
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
            let pinnedSize = try NativeMusicAudioCacheDirectory.pinnedSize(descriptor)
            guard pinnedSize == expected else { throw NativeMusicAudioCacheFailure.miss }
            let copy = fcntl(descriptor, F_DUPFD_CLOEXEC, 0)
            guard copy >= 0 else { throw NativeMusicAudioCacheFailure.miss }
            sealed = true; sealedSize = pinnedSize
            return NativeMusicAudioCacheFile(descriptor: copy, size: expected, fence: fence)
        }
    }

    func publish(as targetName: String) throws -> Int64 {
        lock.lock(); defer { lock.unlock() }
        guard descriptor >= 0, sealed, !published, let directory, let sealedSize else {
            throw NativeMusicAudioCacheFailure.miss
        }
        try directory.rename(self.name, to: targetName)
        published = true
        return sealedSize
    }

    func cancel() {
        lock.lock()
        let wasCancelled = cancelled
        cancelled = true
        let fd = descriptor; descriptor = -1
        let closeNow: Int32
        if fd >= 0 {
            if activePreads > 0 {
                pendingCloseFD = fd
                closeNow = -1
            } else {
                closeNow = fd
            }
        } else {
            closeNow = -1
        }
        let remove = !published
        let directory = self.directory; self.directory = nil
        let task = captureTask
        lock.unlock()
        task?.cancel()
        notifyChanges()
        if closeNow >= 0 { Darwin.close(closeNow) }
        if remove {
            do { try directory?.remove(name) }
            catch {
                DebugRuntime.logError(.storage, sub: "music.audioCache", event: "sink.removeFailed", error: error)
            }
        }
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

    public func readAvailable(offset: Int64, count: Int) throws -> Data? {
        guard offset >= 0, count > 0, count <= 65_536 else { throw NativeMusicAudioCacheFailure.miss }
        try fence.check()
        let step: (fd: Int32, length: Int)? = try lock.withLock {
            if readerRetired { throw CancellationError() }
            if cancelled || descriptor < 0 { throw NativeMusicAudioCacheFailure.miss }
            guard let expected, offset <= expected else { throw NativeMusicAudioCacheFailure.miss }
            if offset == expected { return (-1, 0) }
            guard offset < received else { return nil }
            let length = Int(min(Int64(count), received - offset))
            activePreads += 1
            return (descriptor, length)
        }
        guard let step else { return nil }
        if step.length == 0 { return Data() }
        var data = Data(count: step.length)
        var result: Int
        repeat {
            result = data.withUnsafeMutableBytes { bytes in
                Darwin.pread(step.fd, bytes.baseAddress, step.length, off_t(offset))
            }
        } while result < 0 && errno == EINTR
        let deferredClose: Int32 = lock.withLock {
            activePreads -= 1
            if activePreads == 0, pendingCloseFD >= 0 {
                let closeFD = pendingCloseFD
                pendingCloseFD = -1
                return closeFD
            }
            return -1
        }
        if deferredClose >= 0 { Darwin.close(deferredClose) }
        guard result == step.length else { throw NativeMusicAudioCacheFailure.miss }
        return data
    }

    public func read(offset: Int64, count: Int) async throws -> Data {
        while true {
            try Task.checkCancellation()
            let revision = lock.withLock { changeRevision }
            if let data = try readAvailable(offset: offset, count: count) {
                try Task.checkCancellation()
                return data
            }
            try await waitForChange(after: revision)
        }
    }

    public func confirmPlayback(at date: Date) {
        guard date.timeIntervalSince1970.isFinite, date.timeIntervalSince1970 > 0 else { return }
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

/// Read-only pinned descriptor for native inspection: the decoder never follows
/// a URL path, symlink, redirect or external container reference.
final class NativeMusicAudioCacheFile: @unchecked Sendable {
    private let descriptor: Int32
    private let fence: NativeMusicAudioCacheFence
    let size: Int64
    init(descriptor: Int32, size: Int64, fence: NativeMusicAudioCacheFence) {
        self.descriptor = descriptor; self.size = size; self.fence = fence
    }
    func checkCancellation() throws { try fence.check() }
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

/// Local atomic cache directory for `<dataRoot>/resources-v1/music-audio`.
/// Excluded from iCloud backup; stages writes to `.part` and atomically renames on commit.
final class NativeMusicAudioCacheDirectory: @unchecked Sendable {
    let url: URL

    init(url: URL) throws {
        let normalized = url.standardizedFileURL
        guard normalized.isFileURL, !normalized.path.isEmpty else { throw NativeMusicAudioCacheFailure.miss }
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

    func matches(_ name: String, expectedSize: Int64) -> Bool {
        guard Self.media(name), expectedSize > 0, expectedSize <= NativeMusicAudioCacheHTTPPolicy.maximumBytes else {
            return false
        }
        let path = url.appendingPathComponent(name, isDirectory: false).path
        var info = stat()
        guard stat(path, &info) == 0, info.st_mode & mode_t(S_IFMT) == mode_t(S_IFREG) else { return false }
        return Int64(info.st_size) == expectedSize
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
                do { try fm.removeItem(at: target) }
                catch CocoaError.fileNoSuchFile { continue }
                catch {
                    DebugRuntime.logError(.storage, sub: "music.audioCache", event: "sweep.removeFailed", error: error)
                }
            }
        }
    }

    static func pinnedSize(_ descriptor: Int32) throws -> Int64 {
        var info = stat()
        guard fstat(descriptor, &info) == 0, info.st_mode & mode_t(S_IFMT) == mode_t(S_IFREG),
              info.st_size > 0 else { throw NativeMusicAudioCacheFailure.miss }
        return Int64(info.st_size)
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
