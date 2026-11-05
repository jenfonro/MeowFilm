import Foundation
import XCTest
@testable import MeowMusicFeature

private final class PlaybackLyricClock: @unchecked Sendable {
    private let lock = NSLock()
    private var time: TimeInterval = 0
    func read() -> TimeInterval { lock.lock(); defer { lock.unlock() }; return time }
    func set(_ value: TimeInterval) { lock.lock(); time = value; lock.unlock() }
}

/// Deliberately ignores cancellation until released. No timers, polling,
/// networking, download store or runtime is involved.
private final class PlaybackLyricGate: @unchecked Sendable {
    private let lock = NSLock()
    private let started: [XCTestExpectation]
    private var pending: [Int: (CheckedContinuation<MusicLyricsSnapshot, Error>, MusicLyricsSnapshot)] = [:]
    private var released: Set<Int> = []
    private var open = false
    private var callCount = 0
    private var cancellationCount = 0
    init(started: [XCTestExpectation]) { self.started = started }
    var calls: Int { lock.lock(); defer { lock.unlock() }; return callCount }
    var cancellations: Int { lock.lock(); defer { lock.unlock() }; return cancellationCount }
    func load(_ value: MusicLyricsSnapshot) async throws -> MusicLyricsSnapshot {
        try await withTaskCancellationHandler {
            try await withCheckedThrowingContinuation { continuation in
                lock.lock()
                let index = callCount; callCount += 1
                let ready = open || released.contains(index)
                if !ready { pending[index] = (continuation, value) }
                let signal = index < started.count ? started[index] : nil
                lock.unlock()
                signal?.fulfill()
                if ready { continuation.resume(returning: value) }
            }
        } onCancel: {
            self.lock.lock(); self.cancellationCount += 1; self.lock.unlock()
        }
    }
    func release(_ index: Int) {
        lock.lock(); released.insert(index)
        let waiting = pending.removeValue(forKey: index); lock.unlock()
        if let (continuation, value) = waiting { continuation.resume(returning: value) }
    }
    func releaseAll() {
        lock.lock(); open = true; let waiting = Array(pending.values); pending.removeAll(); lock.unlock()
        waiting.forEach { $0.0.resume(returning: $0.1) }
    }
}

final class MusicPlaybackLyricCacheTests: XCTestCase {
    private typealias Cache = MusicPlaybackLyricCache
    private func line(_ text: String, translation: String = "", words: [MusicLyricWord] = []) -> MusicLyricLine {
        .init(index: 0, time: 0, end: 5, text: text, translation: translation, timing: "line", words: words)
    }
    private func snapshot(_ text: String = "歌词", occurrence: String = "first") -> MusicLyricsSnapshot {
        .init(occurrenceId: occurrence, state: .ready, lines: [line(text)])
    }
    private func track(_ song: String = "song", occurrence: String = "first", source: String = "source",
                       platform: String = "wy", duration: Double = 5, title: String = "歌") -> MusicRemoteTrack {
        .init(ref: .init(kind: .queue, id: occurrence), songId: song, title: title, duration: duration,
              sources: [.init(sourceKey: source, platform: platform, nativeId: "native")])
    }
    private func source(_ key: String = "source", platform: String = "wy") -> MusicNativeSource {
        .init(sourceKey: key, platform: platform, nativeId: "native")
    }
    private func cacheKey(_ song: String = "song") throws -> Cache.Key {
        try .init(track: track(song), source: source())
    }
    private func owner(_ generation: UUID, clock: PlaybackLyricClock = .init()) -> Cache {
        let cache = Cache(now: { clock.read() })
        cache.setAccess(available: true, generation: generation)
        return cache
    }
    private func put(_ cache: Cache, key: Cache.Key, generation: UUID,
                     value: MusicLyricsSnapshot) async throws -> MusicLyricsSnapshot {
        let request = try cache.request(for: key, occurrenceID: value.occurrenceId, generation: generation) { value }
        return try await request.value()
    }
    private func hit(_ cache: Cache, key: Cache.Key, generation: UUID,
                     occurrence: String = "first") async throws -> MusicLyricsSnapshot {
        let request = try cache.request(for: key, occurrenceID: occurrence, generation: generation) {
            throw MusicServiceError.notFound
        }
        return try await request.value()
    }
    private func assertCancelled(_ request: Cache.Request, file: StaticString = #filePath, line: UInt = #line) async {
        do { _ = try await request.value(); XCTFail("A revoked consumer received lyrics", file: file, line: line) }
        catch { XCTAssertTrue(error is CancellationError, "\(error)", file: file, line: line) }
    }
    private func encodedCost(_ lines: [MusicLyricLine]) throws -> Int {
        let encoder = JSONEncoder(); encoder.outputFormatting = [.sortedKeys, .withoutEscapingSlashes]
        let json = try XCTUnwrap(String(data: encoder.encode(lines), encoding: .utf8))
        return json.utf16.count * 2
    }
    private func exactLimitLines() throws -> [MusicLyricLine] {
        let overhead = try encodedCost([line("")])
        let lines = [line(String(repeating: "a", count: (Cache.maximumEntryBytes - overhead) / 2))]
        XCTAssertEqual(try encodedCost(lines), Cache.maximumEntryBytes)
        return lines
    }

    func testExplicitAccessAdmissionNeverLetsAStaleReadReactivateAScope() throws {
        let cache = Cache(), old = UUID(), current = UUID(), key = try cacheKey()
        let value = snapshot()
        XCTAssertThrowsError(try cache.request(for: key, occurrenceID: "first", generation: old) { value }) {
            XCTAssertEqual($0 as? MusicServiceError, .accessUnavailable)
        }
        cache.setAccess(available: true, generation: current)
        XCTAssertThrowsError(try cache.request(for: key, occurrenceID: "first", generation: old) { value }) {
            XCTAssertEqual($0 as? MusicServiceError, .staleAccess)
        }
        XCTAssertEqual(cache.usage, .init(entries: 0, bytes: 0, producers: 0, waiters: 0))
        cache.setAccess(available: false, generation: current)
        XCTAssertThrowsError(try cache.request(for: key, occurrenceID: "first", generation: current) { value }) {
            XCTAssertEqual($0 as? MusicServiceError, .accessUnavailable)
        }
    }

    func testTTLStartsAtSuccessfulCompletionAndHitsDoNotExtendIt() async throws {
        let generation = UUID(), clock = PlaybackLyricClock(), key = try cacheKey()
        let cache = owner(generation, clock: clock)
        let started = expectation(description: "producer entered")
        let gate = PlaybackLyricGate(started: [started]), original = snapshot("original")
        defer { gate.releaseAll(); cache.close() }
        clock.set(100)
        let request = try cache.request(for: key, occurrenceID: "first", generation: generation) { try await gate.load(original) }
        await fulfillment(of: [started], timeout: 2)
        clock.set(500); gate.releaseAll()
        _ = try await request.value()
        clock.set(1_099.999)
        let cached = try await hit(cache, key: key, generation: generation)
        XCTAssertEqual(cached.lines, original.lines)
        clock.set(1_100)
        let refreshed = try await put(cache, key: key, generation: generation, value: snapshot("refreshed"))
        XCTAssertEqual(refreshed.lines.first?.text, "refreshed")
        XCTAssertEqual(cache.usage.entries, 1)
        XCTAssertEqual(cache.usage.bytes, try encodedCost(refreshed.lines))
    }

    func testSameKeyCoalescesAndRebindsQueueOccurrenceWithoutMutatingCachedLines() async throws {
        let generation = UUID(), cache = owner(generation), key = try cacheKey()
        let started = expectation(description: "one producer")
        let gate = PlaybackLyricGate(started: [started]), original = snapshot()
        defer { gate.releaseAll(); cache.close() }
        let first = try cache.request(for: key, occurrenceID: "first", generation: generation) { try await gate.load(original) }
        let second = try cache.request(for: key, occurrenceID: "duplicate-occurrence", generation: generation) {
            throw MusicServiceError.invalidRequest
        }
        XCTAssertEqual(cache.usage.producers, 1); XCTAssertEqual(cache.usage.waiters, 2)
        await fulfillment(of: [started], timeout: 2)
        gate.releaseAll()
        let a = try await first.value(), b = try await second.value()
        XCTAssertEqual(a.occurrenceId, "first"); XCTAssertEqual(b.occurrenceId, "duplicate-occurrence")
        XCTAssertEqual(a.lines, b.lines); XCTAssertEqual(gate.calls, 1)
        var changedCopy = a.lines; changedCopy[0] = line("local copy")
        let c = try await hit(cache, key: key, generation: generation, occurrence: "third")
        XCTAssertEqual(c.occurrenceId, "third"); XCTAssertEqual(c.lines, original.lines)
        XCTAssertNotEqual(changedCopy, c.lines)
    }

    func testCancellingOneConsumerDoesNotCancelTheSharedProducer() async throws {
        let generation = UUID(), cache = owner(generation), key = try cacheKey()
        let started = expectation(description: "shared producer")
        let gate = PlaybackLyricGate(started: [started]), value = snapshot()
        defer { gate.releaseAll(); cache.close() }
        let first = try cache.request(for: key, occurrenceID: "first", generation: generation) { try await gate.load(value) }
        let second = try cache.request(for: key, occurrenceID: "second", generation: generation) {
            throw MusicServiceError.invalidRequest
        }
        await fulfillment(of: [started], timeout: 2)
        let cancelledTask = Task { try await first.value() }
        cancelledTask.cancel()
        do { _ = try await cancelledTask.value; XCTFail("Cancellation must wake this waiter") }
        catch { XCTAssertTrue(error is CancellationError) }
        XCTAssertEqual(cache.usage.waiters, 1); XCTAssertEqual(cache.usage.producers, 1)
        XCTAssertEqual(gate.cancellations, 0)
        gate.releaseAll()
        let result = try await second.value()
        XCTAssertEqual(result.occurrenceId, "second"); XCTAssertEqual(result.lines, value.lines)
        XCTAssertEqual(cache.usage.entries, 1)
    }

    func testLastWaiterCancellationAllowsFreshSameKeyButLateOldValueCannotOverwriteIt() async throws {
        let generation = UUID(), cache = owner(generation), key = try cacheKey()
        let oldStarted = expectation(description: "old producer"), newStarted = expectation(description: "new producer")
        let gate = PlaybackLyricGate(started: [oldStarted, newStarted])
        let oldValue = snapshot("old"), newValue = snapshot("new")
        defer { gate.releaseAll(); cache.close() }
        let old = try cache.request(for: key, occurrenceID: "first", generation: generation) { try await gate.load(oldValue) }
        await fulfillment(of: [oldStarted], timeout: 2)
        old.cancel()
        await assertCancelled(old)
        XCTAssertEqual(cache.usage.producers, 1); XCTAssertEqual(cache.usage.waiters, 0)
        let fresh = try cache.request(for: key, occurrenceID: "first", generation: generation) { try await gate.load(newValue) }
        await fulfillment(of: [newStarted], timeout: 2)
        XCTAssertEqual(cache.usage.producers, 2); XCTAssertEqual(gate.cancellations, 1)
        gate.release(1)
        let result = try await fresh.value(); XCTAssertEqual(result.lines, newValue.lines)
        gate.release(0); await cache.flush()
        let cached = try await hit(cache, key: key, generation: generation)
        XCTAssertEqual(cached.lines, newValue.lines)
        XCTAssertEqual(cache.usage.producers, 0); XCTAssertEqual(cache.usage.entries, 1)
    }

    func testAccountChangeRevokesOldWaitersAndLateCompletionDoesNotTouchNewScope() async throws {
        let oldGeneration = UUID(), newGeneration = UUID(), cache = owner(oldGeneration), key = try cacheKey()
        let started = expectation(description: "old account producer")
        let gate = PlaybackLyricGate(started: [started]), oldValue = snapshot("old account")
        defer { gate.releaseAll(); cache.close() }
        let old = try cache.request(for: key, occurrenceID: "first", generation: oldGeneration) { try await gate.load(oldValue) }
        await fulfillment(of: [started], timeout: 2)
        cache.setAccess(available: true, generation: newGeneration)
        await assertCancelled(old)
        let newValue = snapshot("new account")
        _ = try await put(cache, key: key, generation: newGeneration, value: newValue)
        gate.releaseAll(); await cache.flush()
        let cached = try await hit(cache, key: key, generation: newGeneration)
        XCTAssertEqual(cached.lines, newValue.lines)
        XCTAssertThrowsError(try cache.request(for: key, occurrenceID: "first", generation: oldGeneration) { oldValue }) {
            XCTAssertEqual($0 as? MusicServiceError, .staleAccess)
        }
    }

    func testProtectionRegrantWithSameExternalGenerationCannotReviveDeliveredHit() async throws {
        let generation = UUID(), cache = owner(generation), key = try cacheKey(), value = snapshot()
        _ = try await put(cache, key: key, generation: generation, value: value)
        let delivered = try cache.request(for: key, occurrenceID: "first", generation: generation) { value }
        cache.setAccess(available: false, generation: generation)
        XCTAssertEqual(cache.usage.entries, 0); XCTAssertEqual(cache.usage.bytes, 0)
        cache.setAccess(available: true, generation: generation)
        do { _ = try await delivered.value(); XCTFail("A delivered old-scope hit must be fenced") }
        catch { XCTAssertEqual(error as? MusicServiceError, .staleAccess) }
        let fresh = try await put(cache, key: key, generation: generation, value: snapshot("after unlock"))
        XCTAssertEqual(fresh.lines.first?.text, "after unlock")
    }

    func testCompletedRequestCanBeRecheckedAtTheUIPublicationBoundary() async throws {
        let generation = UUID(), cache = owner(generation), key = try cacheKey(), value = snapshot()
        let request = try cache.request(for: key, occurrenceID: "first", generation: generation) { value }
        _ = try await request.value()
        XCTAssertNoThrow(try request.checkAccess())
        cache.removeAll()
        XCTAssertThrowsError(try request.checkAccess()) {
            XCTAssertEqual($0 as? MusicServiceError, .staleAccess)
        }
    }

    func testClearWithinSameGenerationRevokesWorkButRepeatedAccessBindingIsANoop() async throws {
        let generation = UUID(), cache = owner(generation), key = try cacheKey()
        let started = expectation(description: "pending before clear")
        let gate = PlaybackLyricGate(started: [started]), value = snapshot()
        defer { gate.releaseAll(); cache.close() }
        let request = try cache.request(for: key, occurrenceID: "first", generation: generation) { try await gate.load(value) }
        await fulfillment(of: [started], timeout: 2)
        cache.setAccess(available: true, generation: generation)
        XCTAssertEqual(cache.usage.waiters, 1); XCTAssertEqual(gate.cancellations, 0)
        cache.removeAll()
        await assertCancelled(request)
        gate.releaseAll(); await cache.flush()
        XCTAssertEqual(cache.usage, .init(entries: 0, bytes: 0, producers: 0, waiters: 0))
    }

    func testCancelPendingForSelectionOrLyricDisablePreservesCompletedContent() async throws {
        let generation = UUID(), cache = owner(generation), cachedKey = try cacheKey("cached"), pendingKey = try cacheKey("pending")
        let value = snapshot()
        _ = try await put(cache, key: cachedKey, generation: generation, value: value)
        let started = expectation(description: "pending selection")
        let gate = PlaybackLyricGate(started: [started])
        defer { gate.releaseAll(); cache.close() }
        let pending = try cache.request(for: pendingKey, occurrenceID: "first", generation: generation) { try await gate.load(value) }
        await fulfillment(of: [started], timeout: 2)
        cache.cancelPending()
        await assertCancelled(pending)
        let cached = try await hit(cache, key: cachedKey, generation: generation)
        XCTAssertEqual(cached.lines, value.lines)
        gate.releaseAll(); await cache.flush()
        XCTAssertEqual(cache.usage.entries, 1); XCTAssertEqual(cache.usage.producers, 0)
    }

    func testScopeRotationTracksRetiredProducersWithoutBlockingFreshReads() async throws {
        var generation = UUID()
        let cache = owner(generation), key = try cacheKey()
        let started = (0..<6).map { expectation(description: "held producer \($0)") }
        let gate = PlaybackLyricGate(started: started)
        defer { gate.releaseAll(); cache.close() }
        for index in started.indices {
            let value = snapshot("old-\(index)")
            let request = try cache.request(for: key, occurrenceID: "first", generation: generation) { try await gate.load(value) }
            await fulfillment(of: [started[index]], timeout: 2)
            generation = UUID(); cache.setAccess(available: true, generation: generation)
            await assertCancelled(request)
            XCTAssertEqual(cache.usage.producers, index + 1)
            XCTAssertEqual(cache.usage.waiters, 0)
        }
        let current = snapshot("current")
        _ = try await put(cache, key: key, generation: generation, value: current)
        XCTAssertEqual(gate.calls, 6); XCTAssertEqual(gate.cancellations, 6)
        gate.releaseAll(); await cache.flush()
        let cached = try await hit(cache, key: key, generation: generation)
        XCTAssertEqual(cached.lines, current.lines)
        XCTAssertEqual(cache.usage.producers, 0); XCTAssertEqual(cache.usage.entries, 1)
    }

    func testManyOccurrencesShareOneProducerAndCancelIndependently() async throws {
        let generation = UUID(), cache = owner(generation), key = try cacheKey(), value = snapshot()
        let started = expectation(description: "shared producer"), gate = PlaybackLyricGate(started: [started])
        defer { gate.releaseAll(); cache.close() }
        let first = try cache.request(for: key, occurrenceID: "first", generation: generation) { try await gate.load(value) }
        var requests: [(String, Cache.Request)] = []
        for index in 1..<40 {
            let occurrence = "occurrence-\(index)"
            let request = try cache.request(for: key, occurrenceID: occurrence, generation: generation) {
                throw MusicServiceError.invalidRequest
            }
            requests.append((occurrence, request))
        }
        XCTAssertEqual(cache.usage.waiters, 40); XCTAssertEqual(cache.usage.producers, 1)
        await fulfillment(of: [started], timeout: 2)
        first.cancel(); await assertCancelled(first)
        let added = try cache.request(for: key, occurrenceID: "added", generation: generation) { value }
        requests.append(("added", added))
        XCTAssertEqual(cache.usage.waiters, 40); XCTAssertEqual(cache.usage.producers, 1)
        XCTAssertEqual(gate.cancellations, 0)
        gate.releaseAll()
        for (occurrence, request) in requests {
            let result = try await request.value()
            XCTAssertEqual(result.occurrenceId, occurrence); XCTAssertEqual(result.lines, value.lines)
        }
        XCTAssertEqual(gate.calls, 1)
        XCTAssertEqual(cache.usage.waiters, 0); XCTAssertEqual(cache.usage.producers, 0)
    }

    func testErrorsEmptyAndDisabledResponsesAreNeverNegativeCached() async throws {
        let generation = UUID(), cache = owner(generation), key = try cacheKey()
        for state in [MusicLyricsSnapshot.State.none, .disabled, .ready] {
            let empty = MusicLyricsSnapshot(occurrenceId: "first", state: state, lines: [])
            _ = try await put(cache, key: key, generation: generation, value: empty)
            XCTAssertEqual(cache.usage.entries, 0)
            let request = try cache.request(for: key, occurrenceID: "first", generation: generation) {
                throw MusicServiceError.lyricFetchFailed
            }
            do { _ = try await request.value(); XCTFail("An empty response was cached") }
            catch { XCTAssertEqual(error as? MusicServiceError, .lyricFetchFailed) }
        }
        XCTAssertEqual(cache.usage.bytes, 0)
        let success = try await put(cache, key: key, generation: generation, value: snapshot("retry"))
        XCTAssertEqual(success.lines.first?.text, "retry"); XCTAssertEqual(cache.usage.entries, 1)
    }

    func testMismatchedProducerOccurrenceIsRejectedBeforeCacheInsertion() async throws {
        let generation = UUID(), cache = owner(generation), key = try cacheKey(), wrong = snapshot(occurrence: "other")
        let request = try cache.request(for: key, occurrenceID: "first", generation: generation) { wrong }
        do { _ = try await request.value(); XCTFail("Wrong queue occurrence was accepted") }
        catch { XCTAssertEqual(error as? MusicServiceError, .lyricIdentityMismatch) }
        XCTAssertEqual(cache.usage.entries, 0)
        _ = try await put(cache, key: key, generation: generation, value: snapshot("correct"))
        XCTAssertEqual(cache.usage.entries, 1)
    }

    func testHundredEntryLimitEvictsOldestInsertionRatherThanLeastRecentHit() async throws {
        let generation = UUID(), cache = owner(generation), value = snapshot()
        for index in 0..<100 {
            _ = try await put(cache, key: cacheKey("song-\(index)"), generation: generation, value: value)
        }
        _ = try await hit(cache, key: cacheKey("song-0"), generation: generation)
        _ = try await put(cache, key: cacheKey("song-100"), generation: generation, value: value)
        XCTAssertEqual(cache.usage.entries, 100)
        _ = try await hit(cache, key: cacheKey("song-1"), generation: generation)
        do { _ = try await hit(cache, key: cacheKey("song-0"), generation: generation); XCTFail("A hit incorrectly reordered FIFO") }
        catch { XCTAssertEqual(error as? MusicServiceError, .notFound) }
        XCTAssertEqual(cache.usage.entries, 100)
    }

    func testUTF16JSONAccountingIncludesEscapesTranslationAndWordPayload() throws {
        let lines = [line("猫😀/\"\\\n\u{0001}", translation: "译\t😀",
                          words: [.init(time: 0.125, duration: 1.25, text: "词\r\u{000c}")])]
        XCTAssertEqual(Cache.storageBytes(lines), try encodedCost(lines))
        XCTAssertNil(Cache.storageBytes([.init(index: 0, time: .nan, end: 1, text: "invalid number")]))
    }

    func testOneMiBEntryBoundaryAndOversizeBypassDoNotBreakPlayback() async throws {
        let generation = UUID(), cache = owner(generation), exact = try exactLimitLines()
        let exactKey = try cacheKey("exact"), oversizeKey = try cacheKey("oversize")
        let value = MusicLyricsSnapshot(occurrenceId: "first", state: .ready, lines: exact)
        _ = try await put(cache, key: exactKey, generation: generation, value: value)
        XCTAssertEqual(cache.usage.bytes, 1_048_576); XCTAssertEqual(cache.usage.entries, 1)
        let oversized = MusicLyricsSnapshot(occurrenceId: "first", state: .ready, lines: [line(exact[0].text + "a")])
        XCTAssertEqual(try encodedCost(oversized.lines), 1_048_578)
        XCTAssertNil(Cache.storageBytes(oversized.lines))
        let delivered = try await put(cache, key: oversizeKey, generation: generation, value: oversized)
        XCTAssertEqual(delivered.lines, oversized.lines)
        let retry = try await put(cache, key: oversizeKey, generation: generation, value: snapshot("uncached retry"))
        XCTAssertEqual(retry.lines.first?.text, "uncached retry")
        let retained = try await hit(cache, key: exactKey, generation: generation)
        XCTAssertEqual(retained.lines, exact)
    }

    func testFourMiBTotalLimitUsesTheSameInsertionOrder() async throws {
        let generation = UUID(), cache = owner(generation)
        let value = MusicLyricsSnapshot(occurrenceId: "first", state: .ready, lines: try exactLimitLines())
        for index in 0..<4 { _ = try await put(cache, key: cacheKey("large-\(index)"), generation: generation, value: value) }
        XCTAssertEqual(cache.usage.bytes, 4_194_304); XCTAssertEqual(cache.usage.entries, 4)
        _ = try await hit(cache, key: cacheKey("large-0"), generation: generation)
        _ = try await put(cache, key: cacheKey("large-4"), generation: generation, value: value)
        XCTAssertEqual(cache.usage.bytes, 4_194_304); XCTAssertEqual(cache.usage.entries, 4)
        _ = try await hit(cache, key: cacheKey("large-1"), generation: generation)
        do { _ = try await hit(cache, key: cacheKey("large-0"), generation: generation); XCTFail("Byte budget did not evict oldest") }
        catch { XCTAssertEqual(error as? MusicServiceError, .notFound) }
    }

    func testKeysUseContentAndExactSourceNotOccurrenceOrUnicodeNormalization() throws {
        let original = try Cache.Key(track: track(), source: source())
        XCTAssertEqual(original, try Cache.Key(track: track(occurrence: "duplicate"), source: source()))
        XCTAssertNotEqual(original, try Cache.Key(track: track(source: "other"), source: source("other")))
        XCTAssertNotEqual(original, try Cache.Key(track: track(duration: 6), source: source()))
        XCTAssertNotEqual(original, try Cache.Key(track: track(), source: source(), contentRevision: 2))
        let composed = try Cache.Key(track: track(source: "\u{00e9}"), source: source("\u{00e9}"))
        let decomposed = try Cache.Key(track: track(source: "e\u{0301}"), source: source("e\u{0301}"))
        XCTAssertNotEqual(composed, decomposed)
        XCTAssertThrowsError(try Cache.Key(track: track(), source: source("not-present"))) {
            XCTAssertEqual($0 as? MusicServiceError, .sourceRequired)
        }
        XCTAssertThrowsError(try Cache.Key(track: track(platform: "script"), source: source(platform: "script"))) {
            XCTAssertEqual($0 as? MusicServiceError, .sourceRequired)
        }
    }

    func testMetadataCandidateKeyIsNotBoundToTheCurrentlyPlayingAudioPlatform() async throws {
        let generation = UUID(), cache = owner(generation)
        let wy = MusicNativeSource(sourceKey: "wy-recording", platform: "wy", nativeId: "1")
        let tx = MusicNativeSource(sourceKey: "tx-recording", platform: "tx", nativeId: "2")
        let recording = MusicRemoteTrack(ref: .init(kind: .queue, id: "first"), songId: "same-recording",
                                        title: "歌", duration: 5, sources: [wy, tx])
        let wyKey = try Cache.Key(track: recording, source: wy)
        // This is a single explicit metadata candidate. The cache neither
        // selects nor silently falls back to the currently playing WY source.
        let txKey = try Cache.Key(track: recording, source: tx)
        XCTAssertNotEqual(txKey, wyKey)
        _ = try await put(cache, key: txKey, generation: generation, value: snapshot("QQ lyrics"))
        _ = try await put(cache, key: wyKey, generation: generation, value: snapshot("WY lyrics"))
        let txHit = try await hit(cache, key: txKey, generation: generation)
        let wyHit = try await hit(cache, key: wyKey, generation: generation)
        XCTAssertEqual(txHit.lines.first?.text, "QQ lyrics"); XCTAssertEqual(wyHit.lines.first?.text, "WY lyrics")
        XCTAssertEqual(cache.usage.entries, 2)
        XCTAssertThrowsError(try Cache.Key(track: recording,
                                           source: .init(sourceKey: tx.sourceKey, platform: "tx", nativeId: "wrong")))
    }

    func testCloseIsTerminalAndDoesNotAwaitAnUncooperativeProducer() async throws {
        let generation = UUID(), cache = owner(generation), key = try cacheKey(), value = snapshot()
        let started = expectation(description: "producer before close"), gate = PlaybackLyricGate(started: [started])
        defer { gate.releaseAll() }
        let request = try cache.request(for: key, occurrenceID: "first", generation: generation) { try await gate.load(value) }
        await fulfillment(of: [started], timeout: 2)
        cache.close()
        await assertCancelled(request)
        cache.setAccess(available: true, generation: UUID())
        XCTAssertThrowsError(try cache.request(for: key, occurrenceID: "first", generation: generation) { value }) {
            XCTAssertEqual($0 as? MusicServiceError, .closed)
        }
        XCTAssertEqual(cache.usage.producers, 1)
        gate.releaseAll(); await cache.flush()
        XCTAssertEqual(cache.usage, .init(entries: 0, bytes: 0, producers: 0, waiters: 0))
    }

    func testProducerDoesNotRetainOwnerAcrossASuspendedLoader() async throws {
        let generation = UUID(), key = try cacheKey(), value = snapshot()
        var cache: Cache? = owner(generation)
        weak var weakOwner = cache
        let started = expectation(description: "producer before owner release"), gate = PlaybackLyricGate(started: [started])
        defer { gate.releaseAll() }
        let request = try XCTUnwrap(cache).request(for: key, occurrenceID: "first", generation: generation) { try await gate.load(value) }
        await fulfillment(of: [started], timeout: 2)
        cache = nil
        XCTAssertNil(weakOwner)
        XCTAssertEqual(gate.cancellations, 1)
        await assertCancelled(request)
    }
}
