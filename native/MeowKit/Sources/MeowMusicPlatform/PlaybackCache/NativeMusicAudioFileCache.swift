import Foundation
import CryptoKit
import Darwin
import MeowMusicFeature

/// Product-owned, disposable cache, NOT a download library. Pass a new private
/// leaf under the app's canonical Caches directory (its parent must exist).
/// Existing non-cache directories, symlink ancestors and concurrent owners are
/// refused. init performs no filesystem or network work.
public actor NativeMusicAudioFileCache: MusicAudioFileCaching {
    private let rootURL: URL
    private let clock: @Sendable () -> Date
    private let transport: any NativeMusicAudioCacheTransport
    private let inspector: any NativeMusicAudioCacheInspecting
    private let capacity: Int64
    private let requiresCachesDirectory: Bool
    private let timing: NativeMusicAudioCacheTiming
    private var directory: NativeMusicAudioCacheDirectory?
    private var entries: [String: NativeMusicAudioCacheEntry] = [:]
    private var pending: [UUID: Pending] = [:]
    private var closed = false
    private var captureCutoff: Date?
    private static let lifetime: TimeInterval = 30 * 86_400

    private struct Pending {
        let id: UUID
        let key: String
        let identity: MusicAudioFileCacheIdentity
        let fence: NativeMusicAudioCacheFence
        let sink: NativeMusicAudioCacheSink
        var task: Task<MusicAudioFileCacheVersion?, Never>?
        var reserved: Int64 = 0
    }

    public init(rootURL: URL, clock: @escaping @Sendable () -> Date = { Date() }) {
        self.rootURL = rootURL; self.clock = clock
        transport = URLSessionNativeMusicAudioCacheTransport()
        inspector = NativeMusicAudioCacheInspector()
        capacity = NativeMusicAudioCacheHTTPPolicy.maximumBytes
        requiresCachesDirectory = true
        timing = .init()
    }

    /// Trusted in-process test/platform dependencies, never script-provided.
    public init(rootURL: URL, clock: @escaping @Sendable () -> Date = { Date() },
                transport: any NativeMusicAudioCacheTransport, inspector: any NativeMusicAudioCacheInspecting) {
        self.rootURL = rootURL; self.clock = clock; self.transport = transport; self.inspector = inspector
        capacity = NativeMusicAudioCacheHTTPPolicy.maximumBytes
        requiresCachesDirectory = true
        timing = .init()
    }

    // A smaller bound for deterministic eviction regression; production cannot
    // enlarge the reference's 256 MiB limit through public configuration.
    init(rootURL: URL, clock: @escaping @Sendable () -> Date,
         transport: any NativeMusicAudioCacheTransport, inspector: any NativeMusicAudioCacheInspecting,
         capacity: Int64, timing: NativeMusicAudioCacheTiming = .init()) {
        self.rootURL = rootURL; self.clock = clock; self.transport = transport; self.inspector = inspector
        self.capacity = max(0, min(capacity, NativeMusicAudioCacheHTTPPolicy.maximumBytes))
        requiresCachesDirectory = false
        self.timing = timing
    }

    public func lookup(scope: String, songID: String, authorization: MusicQueueAuthorization,
                       accepts: @escaping @Sendable (MusicAudioFileCacheIdentity) -> Bool) async -> MusicAudioFileCacheHit? {
        guard !closed, !Task.isCancelled, authorization.isActive,
              Self.text(scope), Self.text(songID) else { return nil }
        do {
            return try authorization.perform {
                let directory = try open()
                try prune(directory)
                guard let entry = entries[Self.key(scope, songID)],
                      accepts(entry.identity), authorization.isActive, !Task.isCancelled,
                      try directory.matches(entry.file, stamp: entry.stamp) else { return nil }
                // Neither wall time nor lastPlayedAt is updated by a read.
                return .init(identity: entry.identity, fileURL: directory.url.appendingPathComponent(entry.file),
                             duration: entry.inspection.duration, measuredQuality: entry.quality,
                             expiresAt: entry.expiresAt)
            }
        } catch { return nil }
    }

    @discardableResult public func storeAfterPlayback(_ request: MusicAudioFileCacheRequest) async -> MusicAudioFileCacheVersion? {
        let now = clock()
        guard !closed, !Task.isCancelled, request.authorization.isActive, request.playbackAuthorization.isActive,
              captureCutoff.map({ request.confirmedAt > $0 }) ?? true,
              Self.valid(request.identity), Self.confirmed(request.confirmedAt, now: now),
              request.expectedDuration.isFinite, request.expectedDuration > 0,
              request.audio.fileCachePolicy.allowed,
              Self.licenseValid(request.audio.fileCachePolicy.licenseExpiresAt, now: now),
              Self.expiry(request.confirmedAt, request.audio.fileCachePolicy.licenseExpiresAt) > now,
              NativeMusicAudioCacheInspection.supportedRequest(request.audio.delivery?.quality ?? request.identity.requestedQuality),
              NativeMusicAudioCacheHTTPPolicy.permits(request.audio.url, platform: request.identity.platform)
        else { return nil }
        // A URL expiry is only a fetch eligibility check. It is NEVER persisted
        // or used to expire already captured audio.
        if let expiry = request.audio.expiresAt,
           !expiry.timeIntervalSince1970.isFinite || expiry <= now { return nil }
        let key = Self.key(request.identity.scope, request.identity.songID)
        do {
            let directory = try open()
            try prune(directory)
            if var old = entries[key], old.identity == request.identity,
               (try? old.inspection.quality(for: request.audio.delivery?.quality ?? request.identity.requestedQuality,
                                           expectedDuration: request.expectedDuration)) != nil,
               try directory.matches(old.file, stamp: old.stamp) {
                try request.authorization.perform {
                    try Task.checkCancellation()
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
            // Like the reference stream owner, at most eight ephemeral fills;
            // all reservations share ONE 256 MiB budget. Cancelled work stays
            // charged until it has actually released its pinned descriptors.
            for value in pending.values where value.key == key && value.identity != request.identity {
                cancelCapture(value)
            }
            guard pending.count < 8, !pending.values.contains(where: { $0.key == key }) else { return nil }
            let id = UUID(), fence = NativeMusicAudioCacheFence(authorization: request.authorization, timing: timing)
            let sink = try NativeMusicAudioCacheSink(directory: directory, fence: fence,
                playbackAuthorization: request.playbackAuthorization,
                platform: request.identity.platform) { [weak self] length in
                    guard let self else { throw NativeMusicAudioCacheFailure.miss }
                    try await self.reserve(id: id, bytes: length)
                }
            pending[id] = Pending(id: id, key: key, identity: request.identity, fence: fence, sink: sink)
            let http = NativeMusicAudioCacheHTTPRequest(url: request.audio.url, platform: request.identity.platform)
            let transport = self.transport, inspector = self.inspector
            let task = Task<MusicAudioFileCacheVersion?, Never> {
                defer { self.finished(id: id) }
                do {
                    try sink.checkReceivingAuthorization()
                    try await transport.transfer(http, into: sink)
                    let file = try sink.seal()
                    let measured = try await inspector.inspect(file, expectedDuration: request.expectedDuration)
                    try fence.check()
                    return try self.publish(id: id, request: request, inspection: measured)
                } catch { /* Evictable cache failure does not affect remote playback. */ }
                return nil
            }
            pending[id]?.task = task
            let revocations = request.authorization.revocations()
            let revocation = Task {
                for await _ in revocations {
                    fence.cancel(); sink.cancel(); task.cancel()
                    break
                }
            }
            let playbackRevocations = request.playbackAuthorization.revocations()
            let playbackRevocation = Task {
                for await _ in playbackRevocations {
                    if sink.retireIfIncomplete() { task.cancel() }
                    break
                }
            }
            // One sleeper per capture. On wake it rechecks the actual byte/EOF
            // deadline; no per-chunk tasks, polling loop or wall-clock lease.
            let deadline = Task {
                do {
                    try await fence.waitForExpiry()
                    fence.cancel(); sink.cancel(); task.cancel()
                } catch {}
            }
            let published = await withTaskCancellationHandler {
                await task.value
            } onCancel: {
                fence.cancel(); sink.cancel(); task.cancel()
            }
            revocation.cancel(); playbackRevocation.cancel(); deadline.cancel()
            await revocation.value; await playbackRevocation.value; await deadline.value
            return published
        } catch { /* Invalid/unavailable storage is a miss. */ }
        return nil
    }

    public func touch(_ identity: MusicAudioFileCacheIdentity, version: MusicAudioFileCacheVersion, confirmedAt: Date,
                      authorization: MusicQueueAuthorization) async {
        guard !closed, !Task.isCancelled, authorization.isActive,
              Self.valid(identity), Self.confirmed(confirmedAt, now: clock()) else { return }
        do {
            try authorization.perform {
                let directory = try open()
                try prune(directory)
                let key = Self.key(identity.scope, identity.songID)
                guard var entry = entries[key], entry.identity == identity, entry.file == version.rawValue,
                      try directory.matches(entry.file, stamp: entry.stamp) else { return }
                entry.lastPlayedAt = max(entry.lastPlayedAt, confirmedAt)
                entry.expiresAt = Self.expiry(entry.lastPlayedAt, entry.licenseExpiresAt)
                var next = entries; next[key] = entry
                try commitIndex(next, directory: directory)
            }
        } catch {}
    }

    public func remove(_ identity: MusicAudioFileCacheIdentity, version: MusicAudioFileCacheVersion) async {
        guard !closed, Self.valid(identity) else { return }
        // Pending fills have no published file revision yet. Retiring an old
        // failed file must not cancel a new same-recording replacement.
        do {
            let directory = try open(), key = Self.key(identity.scope, identity.songID)
            guard entries[key]?.identity == identity, entries[key]?.file == version.rawValue else { return }
            var next = entries; next.removeValue(forKey: key)
            try commitIndex(next, directory: directory)
        } catch {}
    }

    public func cancelPending() async {
        fenceQueuedCaptures()
        cancelCaptures()
    }

    public func clear() async {
        guard !closed else { return }
        fenceQueuedCaptures()
        cancelCaptures() // Synchronous fence BEFORE any further work.
        do {
            let directory = try open()
            try commitIndex([:], directory: directory)
            try directory.recover(keeping: [], activeTemporaries: Set(pending.values.map(\.sink.name)))
        } catch {}
    }

    public func close() async {
        closed = true
        cancelCaptures()
        entries.removeAll()
        // In-flight descriptors retain the directory lock until actual release.
        directory = nil
    }

    private func cancelCaptures() {
        for value in pending.values { cancelCapture(value) }
    }
    private func fenceQueuedCaptures() {
        // Also reject previously confirmed requests whose caller Task has not
        // entered this actor yet. A later real-playing event can start anew.
        // Equal/backward wall times conservatively miss rather than resurrect
        // pre-clear work; publication itself uses the independent monotonic cap.
        let now = clock()
        captureCutoff = max(captureCutoff ?? now, now)
    }
    private func cancelCapture(_ value: Pending) {
        value.fence.cancel(); value.sink.cancel(); value.task?.cancel()
    }

    private func finished(id: UUID) {
        guard let value = pending.removeValue(forKey: id) else { return }
        value.fence.cancel(); value.sink.cancel()
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
            var total = next.values.reduce(Int64(0)) { $0 + $1.stamp.size } + bytes +
                pending.values.reduce(Int64(0)) { $0 + $1.reserved }
            for victim in next.values.sorted(by: Self.oldest) where total > capacity && !protectedKeys.contains(victim.key) {
                next.removeValue(forKey: victim.key); total -= victim.stamp.size
            }
            guard total <= capacity else { throw NativeMusicAudioCacheFailure.miss }
            try commitIndex(next, directory: directory)
            // Deleted files must really have been reclaimed before allocating
            // replacement bytes. No uncharged crash/orphan files are ignored.
            try directory.recover(keeping: Set(entries.values.map(\.file)),
                                  activeTemporaries: Set(pending.values.map(\.sink.name)))
            pending[id]?.reserved = bytes
        }
    }

    private func publish(id: UUID, request: MusicAudioFileCacheRequest,
                         inspection: NativeMusicAudioCacheInspection) throws -> MusicAudioFileCacheVersion {
        guard !closed, let value = pending[id] else { throw NativeMusicAudioCacheFailure.miss }
        return try value.fence.perform {
            let now = clock()
            guard Self.confirmed(request.confirmedAt, now: now),
                  Self.licenseValid(request.audio.fileCachePolicy.licenseExpiresAt, now: now),
                  Self.expiry(request.confirmedAt, request.audio.fileCachePolicy.licenseExpiresAt) > now else {
                throw NativeMusicAudioCacheFailure.miss
            }
            let quality = try inspection.quality(for: request.audio.delivery?.quality ?? request.identity.requestedQuality,
                                                 expectedDuration: request.expectedDuration)
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
            guard next.count <= 4096, next.values.reduce(Int64(0), { $0 + $1.stamp.size }) <= capacity else {
                throw NativeMusicAudioCacheFailure.miss
            }
            try commitIndex(next, directory: directory) {
                try value.fence.check()
                guard Self.expiry(request.confirmedAt, request.audio.fileCachePolicy.licenseExpiresAt) > clock() else {
                    throw NativeMusicAudioCacheFailure.miss
                }
            }
            committed = true
            return .init(rawValue: name)
        }
    }

    private func open() throws -> NativeMusicAudioCacheDirectory {
        guard !closed else { throw NativeMusicAudioCacheFailure.miss }
        if let directory { try directory.check(); return directory }
        guard rootURL.isFileURL, rootURL.baseURL == nil,
              rootURL.host == nil || rootURL.host == "",
              rootURL.query == nil, rootURL.fragment == nil else { throw NativeMusicAudioCacheFailure.miss }
        var ownedURL = rootURL
        if requiresCachesDirectory {
            // Resolve ONLY the system-provided existing base, never a caller's
            // potentially hostile path. Production will not create a download
            // or a backed-up Documents/Application Support media library.
            guard let provided = FileManager.default.urls(for: .cachesDirectory, in: .userDomainMask).first else {
                throw NativeMusicAudioCacheFailure.miss
            }
            // Foundation may abbreviate /private/var back to /var even after
            // resolvingSymlinksInPath. Resolve only the OS-provided base using
            // POSIX; every caller-supplied descendant is still opened NOFOLLOW.
            guard let pointer = realpath(provided.path, nil) else { throw NativeMusicAudioCacheFailure.miss }
            defer { free(pointer) }
            let canonical = String(cString: pointer)
            if rootURL.path.hasPrefix(provided.path + "/") {
                let suffix = rootURL.path.dropFirst(provided.path.count)
                ownedURL = URL(fileURLWithPath: canonical + suffix, isDirectory: true)
            } else {
                guard rootURL.path.hasPrefix(canonical + "/") else { throw NativeMusicAudioCacheFailure.miss }
            }
        }
        let value = try NativeMusicAudioCacheDirectory(url: ownedURL)
        var loaded: [String: NativeMusicAudioCacheEntry] = [:]
        if let bytes = try value.readIndex() {
            // A corrupt/old index is not authority to touch arbitrary paths.
            if let index = try? JSONDecoder().decode(NativeMusicAudioCacheIndex.self, from: bytes),
               index.version == 1, index.entries.count <= 4096 {
                for entry in index.entries {
                    guard Self.valid(entry.identity), entry.key == Self.key(entry.identity.scope, entry.identity.songID),
                          loaded[entry.key] == nil, Self.confirmed(entry.lastPlayedAt, now: clock()),
                          Self.licenseValid(entry.licenseExpiresAt, now: clock()), entry.expiresAt > clock(),
                          entry.expiresAt == Self.expiry(entry.lastPlayedAt, entry.licenseExpiresAt),
                          (try? entry.inspection.quality(for: entry.quality,
                                                        expectedDuration: entry.inspection.duration)) == entry.quality,
                          (try? value.matches(entry.file, stamp: entry.stamp)) == true else { continue }
                    loaded[entry.key] = entry
                }
            }
        }
        var total = loaded.values.reduce(Int64(0)) { $0 + $1.stamp.size }
        for entry in loaded.values.sorted(by: Self.oldest) where total > capacity {
            loaded.removeValue(forKey: entry.key); total -= entry.stamp.size
        }
        try value.writeIndex(Self.encode(loaded))
        try value.recover(keeping: Set(loaded.values.map(\.file)), activeTemporaries: [])
        directory = value; entries = loaded
        return value
    }

    private func prune(_ directory: NativeMusicAudioCacheDirectory) throws {
        let now = clock()
        let next = try entries.filter { _, entry in
            guard entry.expiresAt > now, Self.licenseValid(entry.licenseExpiresAt, now: now) else { return false }
            return try directory.matches(entry.file, stamp: entry.stamp)
        }
        if next.count != entries.count { try commitIndex(next, directory: directory) }
    }

    private func commitIndex(_ next: [String: NativeMusicAudioCacheEntry],
                             directory: NativeMusicAudioCacheDirectory,
                             validate: () throws -> Void = {}) throws {
        let removed = Set(entries.values.map(\.file)).subtracting(next.values.map(\.file))
        try directory.writeIndex(Self.encode(next), validate: validate)
        entries = next
        // Index first for removal, file first for publication. A crash can leave
        // an orphan, never an index pointing at a partially written file.
        for file in removed { try? directory.remove(file) }
    }

    private static func encode(_ entries: [String: NativeMusicAudioCacheEntry]) throws -> Data {
        let encoder = JSONEncoder(); encoder.outputFormatting = [.sortedKeys]
        let bytes = try encoder.encode(NativeMusicAudioCacheIndex(version: 1,
            entries: entries.values.sorted { $0.key < $1.key }))
        guard bytes.count <= NativeMusicAudioCacheDirectory.indexLimit else { throw NativeMusicAudioCacheFailure.miss }
        return bytes
    }
    private static func oldest(_ a: NativeMusicAudioCacheEntry, _ b: NativeMusicAudioCacheEntry) -> Bool {
        a.lastPlayedAt == b.lastPlayedAt ? a.key < b.key : a.lastPlayedAt < b.lastPlayedAt
    }
    private static func key(_ scope: String, _ songID: String) -> String {
        // Length-delimited identity; no caller-controlled path components.
        let bytes = Data("\(scope.utf8.count):\(scope)\(songID.utf8.count):\(songID)".utf8)
        return SHA256.hash(data: bytes).map { String(format: "%02x", $0) }.joined()
    }
    private static func text(_ value: String) -> Bool {
        !value.isEmpty && value.utf8.count <= 2048 &&
        value == value.trimmingCharacters(in: .whitespacesAndNewlines) &&
        !value.unicodeScalars.contains { $0.value < 32 || $0.value == 127 }
    }
    private static func valid(_ value: MusicAudioFileCacheIdentity) -> Bool {
        [value.scope, value.songID, value.sourceKey, value.nativeID, value.requestedQuality,
         value.playbackSignature].allSatisfy(text) && ["wy", "tx", "kg"].contains(value.platform)
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
}

private struct NativeMusicAudioCacheIndex: Codable {
    let version: Int
    let entries: [NativeMusicAudioCacheEntry]
}
private struct NativeMusicAudioCacheEntry: Codable {
    let key: String
    let identity: MusicAudioFileCacheIdentity
    let file: String
    let stamp: NativeMusicAudioCacheStamp
    let inspection: NativeMusicAudioCacheInspection
    let quality: String
    var lastPlayedAt: Date
    var licenseExpiresAt: Date?
    var expiresAt: Date
}

/// Serializes cancellation with the final synchronous authorization+index CAS.
/// The lock order is authorization -> fence -> sink; never the reverse.
final class NativeMusicAudioCacheFence: @unchecked Sendable {
    private let lock = NSRecursiveLock()
    private let authorization: MusicQueueAuthorization
    private let timing: NativeMusicAudioCacheTiming
    private var deadline: NativeMusicAudioCacheDeadline
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
            guard !cancelled, !Task.isCancelled, deadline.remaining(at: timing.now()) > .zero else {
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
    private func remaining() -> Duration {
        lock.lock(); defer { lock.unlock() }
        return cancelled ? .zero : deadline.remaining(at: timing.now())
    }
    func waitForExpiry() async throws {
        while true {
            try Task.checkCancellation()
            let duration = remaining()
            if duration <= .zero { return }
            try await timing.sleep(duration)
        }
    }
}

/// The transport cannot choose a destination or obtain a path/file handle.
/// Bounds, EOF, disk reservation and cancellation are enforced by this sink.
public final class NativeMusicAudioCacheSink: @unchecked Sendable {
    private let lock = NSLock()
    private var directory: NativeMusicAudioCacheDirectory?
    private let fence: NativeMusicAudioCacheFence
    private let playbackAuthorization: MusicQueueAuthorization
    private let platform: String
    private let reserve: @Sendable (Int64) async throws -> Void
    let name: String
    private var descriptor: Int32
    private var expected: Int64?
    private var received: Int64 = 0
    private var sealed = false
    private var published = false
    private var bodyComplete = false
    private var cancelled = false
    private var sealedStamp: NativeMusicAudioCacheStamp?

    init(directory: NativeMusicAudioCacheDirectory, fence: NativeMusicAudioCacheFence,
         playbackAuthorization: MusicQueueAuthorization, platform: String,
         reserve: @escaping @Sendable (Int64) async throws -> Void) throws {
        self.directory = directory; self.fence = fence; self.platform = platform; self.reserve = reserve
        self.playbackAuthorization = playbackAuthorization
        let temporary = UUID().uuidString.lowercased() + ".part"
        name = temporary
        descriptor = try directory.create(temporary)
    }
    public func accept(_ response: NativeMusicAudioCacheHTTPResponse) async throws {
        try checkReceivingAuthorization()
        let length = try NativeMusicAudioCacheHTTPPolicy.length(response, platform: platform)
        try await reserve(length)
        try accepted(length)
    }
    func checkReceivingAuthorization() throws {
        try playbackAuthorization.perform { try fence.check() }
    }
    private func accepted(_ length: Int64) throws {
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
                  let expected, Int64(data.count) <= expected - received else {
                throw NativeMusicAudioCacheFailure.miss
            }
            try NativeMusicAudioCacheDirectory.write(data, to: descriptor)
            received += Int64(data.count)
            // Only successful nonempty body writes count as progress, never
            // response headers, empty data callbacks or UI progress.
            try fence.received(data.count)
        }
    }
    /// Called by the transport at successful HTTP EOF, never by a byte-count
    /// heuristic. This linearizes a song switch against body completion.
    public func finishBody() throws {
        // A delayed AsyncStream revocation observer cannot promote bytes to a
        // complete body after this request's selection lifetime has ended.
        try playbackAuthorization.perform {
            try fence.perform {
                lock.lock(); defer { lock.unlock() }
                guard descriptor >= 0, !cancelled, !bodyComplete, !sealed,
                      let expected, expected == received else { throw NativeMusicAudioCacheFailure.miss }
                try fence.completedBody()
                bodyComplete = true
            }
        }
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
            guard descriptor >= 0, !sealed, bodyComplete, !cancelled, let directory,
                  let expected, received == expected else {
                throw NativeMusicAudioCacheFailure.miss
            }
            let pinned = try NativeMusicAudioCacheDirectory.pinnedStamp(descriptor)
            guard pinned.size == expected, try directory.stamp(name) == pinned,
                  fsync(descriptor) == 0, fchmod(descriptor, 0o400) == 0 else {
                throw NativeMusicAudioCacheFailure.miss
            }
            let copy = fcntl(descriptor, F_DUPFD_CLOEXEC, 0)
            guard copy >= 0 else { throw NativeMusicAudioCacheFailure.miss }
            sealed = true; sealedStamp = pinned
            return NativeMusicAudioCacheFile(descriptor: copy, size: expected, fence: fence)
        }
    }
    func publish(as name: String) throws -> NativeMusicAudioCacheStamp {
        lock.lock(); defer { lock.unlock() }
        guard descriptor >= 0, sealed, !published, let directory, let sealedStamp,
              try NativeMusicAudioCacheDirectory.pinnedStamp(descriptor) == sealedStamp,
              try directory.stamp(self.name) == sealedStamp else { throw NativeMusicAudioCacheFailure.miss }
        try directory.rename(self.name, to: name, replacing: false)
        guard (try? directory.stamp(name)) == sealedStamp else {
            try? directory.remove(name)
            throw NativeMusicAudioCacheFailure.miss
        }
        published = true
        return sealedStamp
    }
    func cancel() {
        lock.lock()
        cancelled = true
        let fd = descriptor; descriptor = -1
        let remove = !published
        let directory = self.directory; self.directory = nil
        lock.unlock()
        if fd >= 0 { Darwin.close(fd) }
        if remove { try? directory?.remove(name) }
    }
    deinit { cancel() }
}

/// Read-only pinned descriptor for native inspection: the decoder never follows
/// a URL path, symlink, redirect or external container reference.
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
    init(_ value: stat) {
        device = Int64(value.st_dev); inode = UInt64(value.st_ino); size = Int64(value.st_size)
        seconds = Int64(value.st_mtimespec.tv_sec); nanos = Int64(value.st_mtimespec.tv_nsec)
    }
}

/// All mutations are single-component *at() operations on a pinned directory.
/// No recursive deletes, identity-derived filenames, URL-based file IO, or
/// resolving a hostile symlink and then trusting its destination.
final class NativeMusicAudioCacheDirectory: @unchecked Sendable {
    static let indexLimit = 8 * 1024 * 1024
    private static let marker = Data("meowmusic.native-audio-file-cache.v1\n".utf8)
    let url: URL
    private let descriptor: Int32
    private let lockDescriptor: Int32
    private let identity: NativeMusicAudioCacheStamp

    init(url: URL, prepareMarker: (Int32) throws -> Void = NativeMusicAudioCacheProtection.apply(to:)) throws {
        let fd = try Self.openDirectory(url, createLeaf: true)
        var lockFD: Int32 = -1
        do {
            var info = stat()
            guard fstat(fd, &info) == 0, info.st_uid == geteuid(),
                  info.st_mode & 0o077 == 0 else { throw NativeMusicAudioCacheFailure.miss }
            let children = try Self.names(fd)
            if !children.contains(".owner") {
                guard children.isEmpty else { throw NativeMusicAudioCacheFailure.miss }
                let marker = openat(fd, ".owner", O_WRONLY | O_CREAT | O_EXCL | O_NOFOLLOW | O_CLOEXEC, 0o600)
                guard marker >= 0 else { throw NativeMusicAudioCacheFailure.miss }
                defer { Darwin.close(marker) }
                let created = try Self.regular(marker)
                do {
                    try prepareMarker(marker)
                    try Self.write(Self.marker, to: marker)
                    guard fsync(marker) == 0 else { throw NativeMusicAudioCacheFailure.miss }
                } catch {
                    // Roll back only this invocation's newly created marker.
                    // Never adopt, repair or delete a pre-existing invalid one.
                    var current = stat()
                    if fstatat(fd, ".owner", &current, AT_SYMLINK_NOFOLLOW) == 0,
                       current.st_dev == created.st_dev, current.st_ino == created.st_ino,
                       current.st_nlink == 1 {
                        _ = unlinkat(fd, ".owner", 0)
                    }
                    throw error
                }
            }
            guard try Self.read(".owner", at: fd, maximum: 128) == Self.marker else {
                throw NativeMusicAudioCacheFailure.miss
            }
            try NativeMusicAudioCacheProtection.apply(to: fd)
            lockFD = openat(fd, ".lock", O_RDWR | O_CREAT | O_NOFOLLOW | O_NONBLOCK | O_CLOEXEC, 0o600)
            guard lockFD >= 0 else { throw NativeMusicAudioCacheFailure.miss }
            _ = try Self.regular(lockFD)
            guard flock(lockFD, LOCK_EX | LOCK_NB) == 0 else { throw NativeMusicAudioCacheFailure.miss }
            try NativeMusicAudioCacheProtection.apply(to: lockFD)
            self.url = url; descriptor = fd; lockDescriptor = lockFD; identity = NativeMusicAudioCacheStamp(info)
        } catch {
            if lockFD >= 0 { Darwin.close(lockFD) }
            Darwin.close(fd); throw error
        }
    }

    func check() throws {
        let fresh = try Self.openDirectory(url, createLeaf: false)
        defer { Darwin.close(fresh) }
        var info = stat()
        guard fstat(fresh, &info) == 0, Int64(info.st_dev) == identity.device,
              UInt64(info.st_ino) == identity.inode, info.st_uid == geteuid(),
              info.st_mode & 0o077 == 0 else { throw NativeMusicAudioCacheFailure.miss }
        try NativeMusicAudioCacheProtection.check(fresh)
    }
    func create(_ name: String) throws -> Int32 {
        try check()
        guard Self.temporary(name) else { throw NativeMusicAudioCacheFailure.miss }
        let fd = openat(descriptor, name, O_RDWR | O_CREAT | O_EXCL | O_NOFOLLOW | O_CLOEXEC, 0o600)
        guard fd >= 0 else { throw NativeMusicAudioCacheFailure.miss }
        do {
            _ = try Self.regular(fd)
            try NativeMusicAudioCacheProtection.apply(to: fd)
            return fd
        } catch {
            Darwin.close(fd); try? remove(name); throw error
        }
    }
    func stamp(_ name: String) throws -> NativeMusicAudioCacheStamp {
        try check()
        guard Self.owned(name) else { throw NativeMusicAudioCacheFailure.miss }
        let fd = openat(descriptor, name, O_RDONLY | O_NOFOLLOW | O_NONBLOCK | O_CLOEXEC)
        guard fd >= 0 else { throw NativeMusicAudioCacheFailure.miss }
        defer { Darwin.close(fd) }
        try NativeMusicAudioCacheProtection.check(fd)
        return NativeMusicAudioCacheStamp(try Self.regular(fd))
    }
    func matches(_ name: String, stamp: NativeMusicAudioCacheStamp) throws -> Bool {
        guard Self.media(name), stamp.size > 0, stamp.size <= NativeMusicAudioCacheHTTPPolicy.maximumBytes else { return false }
        // A missing/modified file is a miss. Unsafe filesystem objects are
        // never opened or removed by following a link.
        return (try? self.stamp(name)) == stamp
    }
    func remove(_ name: String) throws {
        try check()
        guard Self.owned(name), name != "index.json" else { throw NativeMusicAudioCacheFailure.miss }
        var info = stat()
        if fstatat(descriptor, name, &info, AT_SYMLINK_NOFOLLOW) != 0 {
            if errno == ENOENT { return }
            throw NativeMusicAudioCacheFailure.miss
        }
        try Self.validateRegular(info)
        guard unlinkat(descriptor, name, 0) == 0 else { throw NativeMusicAudioCacheFailure.miss }
    }
    func rename(_ from: String, to: String, replacing: Bool) throws {
        try check()
        guard Self.temporary(from), Self.owned(to) else { throw NativeMusicAudioCacheFailure.miss }
        _ = try stamp(from)
        var info = stat()
        if fstatat(descriptor, to, &info, AT_SYMLINK_NOFOLLOW) == 0 {
            guard replacing, to == "index.json" else { throw NativeMusicAudioCacheFailure.miss }
            try Self.validateRegular(info)
        } else if errno != ENOENT { throw NativeMusicAudioCacheFailure.miss }
        guard renameat(descriptor, from, descriptor, to) == 0 else { throw NativeMusicAudioCacheFailure.miss }
        // Atomic visibility is supplied by rename; directory sync is best effort
        // on filesystems that do not support fsync on directory descriptors.
        _ = fsync(descriptor)
    }
    func readIndex() throws -> Data? {
        try check()
        var info = stat()
        if fstatat(descriptor, "index.json", &info, AT_SYMLINK_NOFOLLOW) != 0 {
            if errno == ENOENT { return nil }
            throw NativeMusicAudioCacheFailure.miss
        }
        try Self.validateRegular(info)
        return try Self.read("index.json", at: descriptor, maximum: Self.indexLimit)
    }
    func writeIndex(_ bytes: Data, validate: () throws -> Void = {}) throws {
        guard bytes.count <= Self.indexLimit else { throw NativeMusicAudioCacheFailure.miss }
        let name = UUID().uuidString.lowercased() + ".index-part"
        let fd = try create(name)
        defer { Darwin.close(fd); try? remove(name) }
        try Self.write(bytes, to: fd)
        guard fsync(fd) == 0 else { throw NativeMusicAudioCacheFailure.miss }
        try validate() // Last deadline/authorization check before atomic visibility.
        try rename(name, to: "index.json", replacing: true)
    }
    func recover(keeping: Set<String>, activeTemporaries: Set<String>) throws {
        try check()
        let names = try Self.names(descriptor)
        // Refuse unknown contents; never clear a caller's data directory.
        guard names.allSatisfy({ [".owner", ".lock", "index.json"].contains($0) || Self.owned($0) }) else {
            throw NativeMusicAudioCacheFailure.miss
        }
        for name in names where (Self.media(name) || Self.temporary(name)) &&
            !keeping.contains(name) && !activeTemporaries.contains(name) {
            try remove(name)
        }
    }
    private static func media(_ name: String) -> Bool { named(name, extensions: ["mp3", "flac", "m4a", "wav"]) }
    private static func temporary(_ name: String) -> Bool { named(name, extensions: ["part", "index-part"]) }
    private static func owned(_ name: String) -> Bool { name == "index.json" || media(name) || temporary(name) }
    private static func named(_ name: String, extensions: Set<String>) -> Bool {
        let pieces = name.split(separator: ".", omittingEmptySubsequences: false)
        guard pieces.count == 2, extensions.contains(String(pieces[1])),
              let id = UUID(uuidString: String(pieces[0])) else { return false }
        return id.uuidString.lowercased() == pieces[0]
    }
    private static func openDirectory(_ url: URL, createLeaf: Bool) throws -> Int32 {
        guard url.isFileURL, url.baseURL == nil, url.host == nil || url.host == "",
              url.query == nil, url.fragment == nil, url.path.hasPrefix("/"),
              !url.path.utf8.contains(0) else { throw NativeMusicAudioCacheFailure.miss }
        let parts = url.path.split(separator: "/", omittingEmptySubsequences: true).map(String.init)
        guard parts.count >= 2, parts.allSatisfy({ $0 != "." && $0 != ".." && !$0.contains("\\") }) else {
            throw NativeMusicAudioCacheFailure.miss
        }
        var fd = Darwin.open("/", O_RDONLY | O_DIRECTORY | O_NOFOLLOW | O_CLOEXEC)
        guard fd >= 0 else { throw NativeMusicAudioCacheFailure.miss }
        for (i, part) in parts.enumerated() {
            var next = openat(fd, part, O_RDONLY | O_DIRECTORY | O_NOFOLLOW | O_CLOEXEC)
            if next < 0, errno == ENOENT, createLeaf, i == parts.count - 1 {
                guard mkdirat(fd, part, 0o700) == 0 || errno == EEXIST else {
                    Darwin.close(fd); throw NativeMusicAudioCacheFailure.miss
                }
                next = openat(fd, part, O_RDONLY | O_DIRECTORY | O_NOFOLLOW | O_CLOEXEC)
            }
            Darwin.close(fd)
            guard next >= 0 else { throw NativeMusicAudioCacheFailure.miss }
            fd = next
        }
        return fd
    }
    private static func validateRegular(_ info: stat) throws {
        guard info.st_mode & mode_t(S_IFMT) == mode_t(S_IFREG), info.st_nlink == 1,
              info.st_uid == geteuid(), info.st_size >= 0, info.st_mode & 0o077 == 0 else {
            throw NativeMusicAudioCacheFailure.miss
        }
    }
    private static func regular(_ fd: Int32) throws -> stat {
        var info = stat()
        guard fstat(fd, &info) == 0 else { throw NativeMusicAudioCacheFailure.miss }
        try validateRegular(info)
        return info
    }
    static func pinnedStamp(_ descriptor: Int32) throws -> NativeMusicAudioCacheStamp {
        NativeMusicAudioCacheStamp(try regular(descriptor))
    }
    private static func read(_ name: String, at directory: Int32, maximum: Int) throws -> Data {
        let fd = openat(directory, name, O_RDONLY | O_NOFOLLOW | O_NONBLOCK | O_CLOEXEC)
        guard fd >= 0 else { throw NativeMusicAudioCacheFailure.miss }
        defer { Darwin.close(fd) }
        let info = try regular(fd)
        try NativeMusicAudioCacheProtection.check(fd)
        guard info.st_size <= off_t(maximum) else { throw NativeMusicAudioCacheFailure.miss }
        var bytes = Data(count: Int(info.st_size))
        try bytes.withUnsafeMutableBytes { buffer in
            var offset = 0
            while offset < buffer.count {
                let count = Darwin.read(fd, buffer.baseAddress!.advanced(by: offset), buffer.count - offset)
                if count < 0 && errno == EINTR { continue }
                guard count > 0 else { throw NativeMusicAudioCacheFailure.miss }
                offset += count
            }
        }
        return bytes
    }
    static func write(_ data: Data, to fd: Int32) throws {
        try data.withUnsafeBytes { buffer in
            var offset = 0
            while offset < buffer.count {
                let count = Darwin.write(fd, buffer.baseAddress!.advanced(by: offset), buffer.count - offset)
                if count < 0 && errno == EINTR { continue }
                guard count > 0 else { throw NativeMusicAudioCacheFailure.miss }
                offset += count
            }
        }
    }
    private static func names(_ fd: Int32) throws -> [String] {
        // openat(".") creates an independent directory cursor (dup would share
        // offsets and make subsequent crash cleanup silently enumerate nothing).
        let copy = openat(fd, ".", O_RDONLY | O_DIRECTORY | O_NOFOLLOW | O_CLOEXEC)
        guard copy >= 0 else { throw NativeMusicAudioCacheFailure.miss }
        guard let stream = fdopendir(copy) else { Darwin.close(copy); throw NativeMusicAudioCacheFailure.miss }
        defer { closedir(stream) }
        var result: [String] = []
        errno = 0
        while let entry = readdir(stream) {
            let length = Int(entry.pointee.d_namlen) + 1
            let name = withUnsafePointer(to: &entry.pointee.d_name) {
                $0.withMemoryRebound(to: CChar.self, capacity: length) { String(cString: $0) }
            }
            if name != "." && name != ".." { result.append(name) }
            guard result.count <= 8192 else { throw NativeMusicAudioCacheFailure.miss }
            errno = 0
        }
        guard errno == 0 else { throw NativeMusicAudioCacheFailure.miss }
        return result
    }
    deinit { Darwin.close(lockDescriptor); Darwin.close(descriptor) }
}

/// iOS Data Protection is set and checked on owned descriptors, not via a
/// path-based Foundation attribute setter that could follow a swapped symlink.
/// Darwin's public F_{SET,GET}PROTECTIONCLASS uses class B (2) for
/// NSFileProtectionCompleteUnlessOpen. macOS/Simulator are not device evidence.
/// Simulator can report successful SET yet return its fixed class C from GET.
/// Only the physical-device branch enforces Data Protection; descriptor,
/// ownership, permissions, no-follow and authorization checks apply everywhere.
enum NativeMusicAudioCacheProtection {
    static let completeUnlessOpen: Int32 = 2
    static func apply(to descriptor: Int32) throws {
        #if os(iOS) && !targetEnvironment(simulator)
        if fcntl(descriptor, F_SETPROTECTIONCLASS, completeUnlessOpen) != 0 {
            throw NativeMusicAudioCacheFailure.miss
        }
        try check(descriptor)
        #endif
    }
    static func check(_ descriptor: Int32) throws {
        #if os(iOS) && !targetEnvironment(simulator)
        let actual = fcntl(descriptor, F_GETPROTECTIONCLASS)
        guard actual == completeUnlessOpen else { throw NativeMusicAudioCacheFailure.miss }
        #endif
    }
}
