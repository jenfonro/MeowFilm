import Foundation

/// Ordinary playback memory, not a download store. Reference: iWebPlus
/// b900c259, static/player.js:171-199. Hits neither renew TTL nor reorder entries.
/// This owner is internal to the feature; it adds no durable schema or service API.
///
/// Placement in the original chain: player.js:47-65 orders metadata mappings;
/// playback.js:263-367 reads the display snapshot and tries those mappings with
/// an eight-second deadline each; fetchSongLyric owns this ten-minute cache.
/// Candidate policy, deadlines, display persistence/restore and rendering are
/// NOT implemented here. Cache each selected metadata mapping, not audio's
/// lastSelection and not the final aggregate/fallback result.
///
/// Bind access synchronously, before admitting reads. A request never activates
/// its own scope. Account/credential/metadata-policy changes must rotate access,
/// even if the platform name or the local account ID did not change.
final class MusicPlaybackLyricCache: @unchecked Sendable {
    static let lifetime: TimeInterval = 600
    static let maximumEntries = 100
    static let maximumEntryBytes = 1_024 * 1_024
    static let maximumBytes = 4 * 1_024 * 1_024

    /// Identity of the lyric content, NOT a UI request/queue occurrence or audio
    /// attempt. Byte-exact JSON avoids String's canonical-equivalence merging.
    struct Key: Hashable, Sendable {
        private let identity: Data

        /// The current DTO exposes recording/source identity and duration, but
        /// not the original song revision, rejected source list or full source locator.
        /// Pass the actual content revision when available; otherwise the owner
        /// must be cleared on changes to those unexposed inputs. Do not substitute
        /// the queue/settings revision or selectionID: ordinary navigation and
        /// quality/display changes must not defeat the ten-minute content cache.
        init(track: MusicRemoteTrack, source: MusicNativeSource, contentRevision: Int? = nil) throws {
            guard Self.publicPlatforms.contains(source.platform) else { throw MusicServiceError.sourceRequired }
            identity = try Self.encode(track: track, source: source, revision: contentRevision)
        }

        private static let publicPlatforms = ["wy", "tx", "kg", "kw", "mg"]
        private static func encode(track: MusicRemoteTrack, source: MusicNativeSource,
                                   revision: Int?) throws -> Data {
            guard !track.songId.isEmpty, track.duration.isFinite, track.duration >= 0,
                  track.sources.contains(source) else { throw MusicServiceError.sourceRequired }
            struct Identity: Encodable {
                let songID: String
                let revision: Int?
                let title: String
                let artist: String
                let album: String
                let duration: Double
                let sources: [MusicNativeSource]
                let sourceKey: String
                let platform: String
            }
            let value = Identity(songID: track.songId, revision: revision, title: track.title,
                                 artist: track.artist, album: track.album, duration: track.duration,
                                 sources: track.sources, sourceKey: source.sourceKey,
                                 platform: source.platform)
            let encoder = JSONEncoder()
            encoder.outputFormatting = [.sortedKeys, .withoutEscapingSlashes]
            let data = try encoder.encode(value)
            guard data.count <= 64 * 1_024 else { throw MusicServiceError.limit }
            return data
        }
    }

    struct Usage: Equatable, Sendable {
        let entries: Int
        let bytes: Int
        /// Includes cancelled producers until their actual completion.
        let producers: Int
        let waiters: Int
    }

    /// One consumer of a shared producer. Registration is synchronous so a new
    /// same-key consumer can join BEFORE the old UI consumer is cancelled.
    /// Await value() once; cancel() is synchronous and never spawns another Task.
    final class Request: @unchecked Sendable {
        fileprivate let id = UUID()
        fileprivate let occurrenceID: String
        fileprivate let scope: UUID
        fileprivate let generation: UUID
        fileprivate let producerID: UUID?
        private weak var owner: MusicPlaybackLyricCache?
        private let lock = NSLock()
        private var result: Result<MusicLyricsSnapshot, Error>?
        private var continuation: CheckedContinuation<MusicLyricsSnapshot, Error>?
        private var awaiting = false
        private var cancelled = false

        fileprivate init(owner: MusicPlaybackLyricCache, occurrenceID: String, scope: UUID,
                         generation: UUID, producerID: UUID?) {
            self.owner = owner; self.occurrenceID = occurrenceID; self.scope = scope
            self.generation = generation; self.producerID = producerID
        }
        func value() async throws -> MusicLyricsSnapshot {
            try await withTaskCancellationHandler {
                try Task.checkCancellation()
                let value: MusicLyricsSnapshot = try await withCheckedThrowingContinuation { next in
                    lock.lock()
                    if awaiting {
                        lock.unlock(); next.resume(throwing: MusicServiceError.invalidRequest)
                    } else {
                        awaiting = true
                        if let result { lock.unlock(); next.resume(with: result) }
                        else { continuation = next; lock.unlock() }
                    }
                }
                try checkAccess()
                return value
            } onCancel: { self.cancel() }
        }
        /// Also call synchronously at the UI publication point, after its await.
        /// Revocation may have happened between value() returning and the actor
        /// resuming; this checks the owner's internal scope, not only a caller UUID.
        func checkAccess() throws {
            try Task.checkCancellation()
            guard !isCancelled else { throw CancellationError() }
            guard let owner else { throw MusicServiceError.closed }
            try owner.checkDelivery(scope: scope, generation: generation)
        }
        func cancel() {
            revoke()
            owner?.cancel(self)
        }
        fileprivate var isCancelled: Bool {
            lock.lock(); defer { lock.unlock() }; return cancelled
        }
        fileprivate func revoke() {
            lock.lock(); cancelled = true; lock.unlock()
            finish(.failure(CancellationError()))
        }
        fileprivate func finish(_ value: Result<MusicLyricsSnapshot, Error>) {
            lock.lock()
            guard result == nil else { lock.unlock(); return }
            result = value
            let waiting = continuation; continuation = nil
            lock.unlock()
            waiting?.resume(with: value)
        }
    }

    private struct Entry {
        let lines: [MusicLyricLine]
        let until: TimeInterval
        let bytes: Int
    }
    /// All mutable producer state is protected by the owner's lock.
    private final class Producer {
        let id = UUID()
        let key: Key
        let scope: UUID
        let occurrenceID: String
        var accepting = true
        var waiters: [UUID: Request] = [:]
        var task: Task<Void, Never>?
        init(key: Key, scope: UUID, occurrenceID: String) {
            self.key = key; self.scope = scope; self.occurrenceID = occurrenceID
        }
    }
    private struct Retirement {
        var tasks: [Task<Void, Never>] = []
        var requests: [Request] = []
        func finish() {
            // No cancellation handler or continuation runs under the cache lock.
            requests.forEach { $0.revoke() }
            tasks.forEach { $0.cancel() }
        }
    }

    private let lock = NSLock()
    private let now: @Sendable () -> TimeInterval
    private var available = false
    private var closed = false
    private var generation: UUID?
    private var scope = UUID()
    private var entries: [Key: Entry] = [:]
    private var insertionOrder: [Key] = []
    private var bytes = 0
    private var active: [Key: UUID] = [:]
    private var producers: [UUID: Producer] = [:]
    private var waiterCount = 0

    init(now: @escaping @Sendable () -> TimeInterval = { ProcessInfo.processInfo.systemUptime }) {
        self.now = now
    }

    var usage: Usage {
        locked { Usage(entries: entries.count, bytes: bytes, producers: producers.count, waiters: waiterCount) }
    }

    /// Finite snapshot drain for controlled teardown/tests. Revocation never
    /// calls this: a cancelled dependency may not cooperate with cancellation.
    func flush() async {
        let tasks = locked { producers.values.compactMap(\.task) }
        for task in tasks { await task.value }
    }

    func setAccess(available: Bool, generation: UUID) {
        let retirement = locked { () -> Retirement in
            guard !closed, self.available != available || self.generation != generation else { return Retirement() }
            self.available = available; self.generation = generation
            return clearLocked()
        }
        retirement.finish()
    }

    /// Use for recording/catalogue changes not represented in Key.
    /// Access remains unchanged; old responses and already-delivered handles
    /// cannot repopulate/read the freshly cleared scope.
    func removeAll() {
        let retirement = locked { clearLocked() }
        retirement.finish()
    }

    /// Selection/lyric-disable revocation preserves completed content for replay.
    func cancelPending() {
        let retirement = locked { retireLocked() }
        retirement.finish()
    }

    func close() {
        let retirement = locked { () -> Retirement in
            closed = true; available = false
            return clearLocked()
        }
        retirement.finish()
    }

    func request(for key: Key, occurrenceID: String, generation: UUID,
                 load: @escaping @Sendable () async throws -> MusicLyricsSnapshot) throws -> Request {
        guard !occurrenceID.isEmpty, occurrenceID.utf8.count <= 4_096 else { throw MusicServiceError.invalidRequest }
        try Task.checkCancellation()
        let time = now()
        return try locked {
            guard !closed else { throw MusicServiceError.closed }
            guard available else { throw MusicServiceError.accessUnavailable }
            guard self.generation == generation else { throw MusicServiceError.staleAccess }
            if let entry = entries[key], entry.until > time {
                let request = Request(owner: self, occurrenceID: occurrenceID, scope: scope,
                                      generation: generation, producerID: nil)
                request.finish(.success(.init(occurrenceId: occurrenceID, state: .ready, lines: entry.lines)))
                return request
            }
            let producer: Producer
            let isNew: Bool
            if let id = active[key], let current = producers[id] {
                producer = current; isNew = false
            } else {
                // Retired work stays tracked until its actual completion; it
                // does not prevent a new valid recording/access scope from reading.
                producer = Producer(key: key, scope: scope, occurrenceID: occurrenceID); isNew = true
                producers[producer.id] = producer; active[key] = producer.id
            }
            let request = Request(owner: self, occurrenceID: occurrenceID, scope: scope,
                                  generation: generation, producerID: producer.id)
            producer.waiters[request.id] = request; waiterCount += 1
            if isNew {
                let id = producer.id
                producer.task = Task { [weak self] in
                    let result: Result<MusicLyricsSnapshot, Error>
                    do {
                        try Task.checkCancellation()
                        guard self?.canStart(id) == true else { throw CancellationError() }
                        let value = try await load()
                        try Task.checkCancellation()
                        result = .success(value)
                    } catch { result = .failure(error) }
                    self?.complete(id, result: result)
                }
            }
            return request
        }
    }

    private func locked<T>(_ action: () throws -> T) rethrows -> T {
        lock.lock(); defer { lock.unlock() }; return try action()
    }
    private func checkDelivery(scope: UUID, generation: UUID) throws {
        try locked {
            guard !closed else { throw MusicServiceError.closed }
            guard available, self.scope == scope, self.generation == generation else { throw MusicServiceError.staleAccess }
        }
    }
    private func canStart(_ id: UUID) -> Bool {
        locked { !closed && available && producers[id].map { $0.accepting && $0.scope == scope } == true }
    }
    private func cancel(_ request: Request) {
        let task = locked { () -> Task<Void, Never>? in
            guard let id = request.producerID, let producer = producers[id],
                  producer.waiters.removeValue(forKey: request.id) != nil else { return nil }
            waiterCount -= 1
            guard producer.waiters.isEmpty else { return nil }
            producer.accepting = false
            if active[producer.key] == id { active.removeValue(forKey: producer.key) }
            return producer.task
        }
        task?.cancel()
    }
    private func clearLocked() -> Retirement {
        scope = UUID(); entries.removeAll(); insertionOrder.removeAll(); bytes = 0
        return retireLocked()
    }
    private func retireLocked() -> Retirement {
        var retirement = Retirement()
        active.removeAll()
        for producer in producers.values where producer.accepting {
            producer.accepting = false
            retirement.requests.append(contentsOf: producer.waiters.values)
            if let task = producer.task { retirement.tasks.append(task) }
            producer.waiters.removeAll()
        }
        waiterCount = 0
        return retirement
    }
    private func complete(_ id: UUID, result: Result<MusicLyricsSnapshot, Error>) {
        // Costing is bounded and outside the lock, so protection revocation does
        // not wait for serialization. Recheck scope after this work.
        let cost: Int?
        if case .success(let value) = result, value.state == .ready, !value.lines.isEmpty {
            cost = Self.storageBytes(value.lines)
        } else { cost = nil }
        let time = now()
        let deliveries = locked { () -> [(Request, Result<MusicLyricsSnapshot, Error>)] in
            guard let producer = producers.removeValue(forKey: id) else { return [] }
            if active[producer.key] == id { active.removeValue(forKey: producer.key) }
            let waiting = Array(producer.waiters.values)
            waiterCount -= waiting.count
            guard !closed, available, producer.accepting, producer.scope == scope else {
                return waiting.map { ($0, .failure(CancellationError())) }
            }
            var checked = result
            if case .success(let value) = result,
               !value.occurrenceId.utf8.elementsEqual(producer.occurrenceID.utf8) {
                checked = .failure(MusicServiceError.lyricIdentityMismatch)
            }
            if case .success(let value) = checked, let cost, waiting.contains(where: { !$0.isCancelled }) {
                if let previous = entries.removeValue(forKey: producer.key) { bytes -= previous.bytes }
                insertionOrder.removeAll { $0 == producer.key }
                entries[producer.key] = Entry(lines: value.lines, until: time + Self.lifetime, bytes: cost)
                insertionOrder.append(producer.key); bytes += cost
                while entries.count > Self.maximumEntries || bytes > Self.maximumBytes {
                    let oldest = insertionOrder.removeFirst()
                    if let entry = entries.removeValue(forKey: oldest) { bytes -= entry.bytes }
                }
            }
            return waiting.map { request in
                (request, checked.map { .init(occurrenceId: request.occurrenceID, state: $0.state, lines: $0.lines) })
            }
        }
        for (request, value) in deliveries { request.finish(value) }
    }

    /// Reference budgets count JSON UTF-16 code units * 2, not UTF-8 bytes.
    /// Our retained value is the existing parsed line payload (raw JS lyrics are
    /// not exposed by MusicServing). Count its complete JSON shape,
    /// including translation, word timing and escaping; never allocate a second
    /// whole lyric JSON/Data/String merely to discover it is oversized.
    static func storageBytes(_ lines: [MusicLyricLine]) -> Int? {
        var counter = LyricJSONCost(limit: maximumEntryBytes / 2)
        guard counter.add(2) else { return nil } // []
        for (index, line) in lines.enumerated() {
            guard (index == 0 || counter.add(1)),
                  counter.add(#"{"index":,"time":,"end":,"text":,"translation":,"words":[]}"#.utf16.count),
                  counter.number(line.index), counter.number(line.time), counter.number(line.end),
                  counter.string(line.text), counter.string(line.translation) else { return nil }
            for (wordIndex, word) in line.words.enumerated() {
                guard (wordIndex == 0 || counter.add(1)),
                      counter.add(#"{"time":,"duration":,"text":}"#.utf16.count),
                      counter.number(word.time), counter.number(word.duration), counter.string(word.text) else { return nil }
            }
        }
        return counter.units * 2
    }
    private struct LyricJSONCost {
        let limit: Int
        var units = 0
        private let encoder = JSONEncoder()
        init(limit: Int) { self.limit = limit }
        mutating func add(_ count: Int) -> Bool {
            guard count <= limit - units else { return false }
            units += count; return true
        }
        mutating func string(_ value: String) -> Bool {
            guard add(2) else { return false }
            for unit in value.utf16 {
                let cost: Int
                switch unit {
                case 0x22, 0x5c, 0x08, 0x0c, 0x0a, 0x0d, 0x09: cost = 2
                case 0..<0x20: cost = 6
                default: cost = 1
                }
                guard add(cost) else { return false }
            }
            return true
        }
        mutating func number<T: Encodable>(_ value: T) -> Bool {
            guard let data = try? encoder.encode(value) else { return false }
            return add(data.count) // JSON numbers contain only ASCII.
        }
    }

    deinit { close() }
}
