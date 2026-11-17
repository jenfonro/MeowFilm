#if os(iOS)
import XCTest
import UIKit
import MeowUI
@testable import MeowMusicFeature

private final class ArtworkImageFixture: @unchecked Sendable {
    struct Response {
        let status: Int
        let data: Data
        var held = false
        var error: URLError?
    }
    let url = URL(string: "https://artwork-cdn.invalid/\(UUID().uuidString).png")!
    let started: (@Sendable (Int) -> Void)?
    let stopped: (@Sendable () -> Void)?
    private let responses: [Response]
    private let lock = NSLock()
    private var reads = 0, stops = 0
    private var active: [ObjectIdentifier: URLProtocol] = [:]
    var requestCount: Int { lock.lock(); defer { lock.unlock() }; return reads }
    var stopCount: Int { lock.lock(); defer { lock.unlock() }; return stops }

    init(_ responses: [Response], started: (@Sendable (Int) -> Void)? = nil,
         stopped: (@Sendable () -> Void)? = nil) {
        self.responses = responses; self.started = started; self.stopped = stopped
        ArtworkImageProtocol.register(self)
    }
    func respond(_ instance: URLProtocol) {
        lock.lock()
        reads += 1
        let count = reads, response = responses[min(reads - 1, responses.count - 1)]
        if response.held { active[ObjectIdentifier(instance)] = instance }
        lock.unlock()
        if let error = response.error {
            started?(count)
            instance.client?.urlProtocol(instance, didFailWithError: error)
            return
        }
        let headers = ["Content-Type": "image/png", "X-Content-Type-Options": "nosniff"]
        let head = HTTPURLResponse(url: url, statusCode: response.status, httpVersion: "HTTP/1.1", headerFields: headers)!
        instance.client?.urlProtocol(instance, didReceive: head, cacheStoragePolicy: .notAllowed)
        if !response.data.isEmpty { instance.client?.urlProtocol(instance, didLoad: response.data) }
        started?(count)
        if !response.held { instance.client?.urlProtocolDidFinishLoading(instance) }
    }
    func stop(_ instance: URLProtocol) {
        lock.lock(); active[ObjectIdentifier(instance)] = nil; stops += 1
        let notify = stops == 1
        lock.unlock()
        if notify { stopped?() }
    }
    func finish() {
        lock.lock(); let instances = Array(active.values); active.removeAll(); lock.unlock()
        for instance in instances { instance.client?.urlProtocolDidFinishLoading(instance) }
    }
    func close() { finish(); ArtworkImageProtocol.remove(url) }
}

private final class ArtworkImageProtocol: URLProtocol, @unchecked Sendable {
    private static let lock = NSLock()
    private static var fixtures: [URL: ArtworkImageFixture] = [:]
    private var fixture: ArtworkImageFixture?
    static func register(_ fixture: ArtworkImageFixture) {
        lock.lock(); fixtures[fixture.url] = fixture; lock.unlock()
    }
    static func remove(_ url: URL) { lock.lock(); fixtures[url] = nil; lock.unlock() }
    override class func canInit(with request: URLRequest) -> Bool {
        lock.lock(); defer { lock.unlock() }
        return request.url.flatMap { fixtures[$0] } != nil
    }
    override class func canonicalRequest(for request: URLRequest) -> URLRequest { request }
    override func startLoading() {
        Self.lock.lock(); fixture = request.url.flatMap { Self.fixtures[$0] }; Self.lock.unlock()
        fixture?.respond(self)
    }
    override func stopLoading() { fixture?.stop(self) }
}

private final class ArtworkPresentationFixture: MusicPresentationCaching, @unchecked Sendable {
    private let lock = NSLock()
    private var imageRevision = UUID()
    private var bytes: [URL: ArtworkBytes] = [:]
    private var readURLs: [URL] = [], storeURLs: [URL] = []
    private var readGates: [URL: ArtworkFlightGate] = [:], storeGates: [URL: ArtworkFlightGate] = [:]
    let lyricRevision = UUID()
    var revision: UUID { lock.withLock { imageRevision } }
    func blockReads(_ urls: [URL], on gate: ArtworkFlightGate) {
        lock.withLock { for url in urls { readGates[url] = gate } }
    }
    func blockStores(_ urls: [URL], on gate: ArtworkFlightGate) {
        lock.withLock { for url in urls { storeGates[url] = gate } }
    }
    func statistics() -> (reads: [URL], stores: [URL], saved: Int) {
        lock.withLock { (readURLs, storeURLs, bytes.count) }
    }
    func read(_ url: URL) async -> ArtworkBytes? {
        let (value, gate) = lock.withLock {
            readURLs.append(url)
            return (bytes[url], readGates.removeValue(forKey: url))
        }
        await gate?.wait()
        return value
    }
    func store(_ data: Data, for url: URL, revision: UUID) async -> ArtworkBytes? {
        let gate = lock.withLock {
            storeURLs.append(url)
            return storeGates.removeValue(forKey: url)
        }
        await gate?.wait()
        return lock.withLock {
            guard revision == imageRevision else { return nil }
            let value = ArtworkBytes(data: data, expiresAt: Date().addingTimeInterval(30 * 86_400))
            bytes[url] = value; return value
        }
    }
    func clearImages() async { lock.withLock { imageRevision = UUID(); bytes.removeAll() } }
    func lyrics(songID: String, authorization: MusicQueueAuthorization) async -> MusicLyricDisplaySnapshot? { nil }
    func storeLyrics(_ lines: [MusicLyricLine], sourceKey: String, songID: String,
                     revision: UUID, authorization: MusicQueueAuthorization) async { XCTFail("Unexpected lyric write") }
    func played(songID: String, artwork: URL?, confirmedAt: Date) async { XCTFail("Unexpected playback renewal") }
    func clearLyrics() async { XCTFail("Unexpected lyric clear") }
    func clear() async { await clearImages(); await clearLyrics() }
}

private actor ArtworkFlightGate {
    private let entered: XCTestExpectation
    private let cancelled: XCTestExpectation?
    private var released = false
    private var waiters: [CheckedContinuation<Void, Never>] = []
    init(_ entered: XCTestExpectation, cancelled: XCTestExpectation? = nil) {
        self.entered = entered; self.cancelled = cancelled
    }
    func wait() async {
        entered.fulfill()
        let cancelled = self.cancelled
        // Observe logical cancellation without claiming that the physical read
        // or save has stopped. Only release resumes that operation.
        await withTaskCancellationHandler {
            if !released { await withCheckedContinuation { waiters.append($0) } }
        } onCancel: { cancelled?.fulfill() }
    }
    func release() {
        released = true
        waiters.forEach { $0.resume() }; waiters.removeAll()
    }
}

private actor ArtworkFlightNetwork {
    private let data: Data
    private var gates: [URL: ArtworkFlightGate] = [:]
    private var requests: [URL] = []
    private var activeURLs: [URL: Int] = [:]
    private var active = 0, peak = 0
    private var overlapped = false
    init(_ data: Data) { self.data = data }
    func block(_ urls: [URL], on gate: ArtworkFlightGate) {
        for url in urls { gates[url] = gate }
    }
    func statistics() -> (requests: [URL], active: Int, peak: Int, overlapped: Bool) {
        (requests, active, peak, overlapped)
    }
    func read(_ url: URL) async -> Data {
        requests.append(url)
        overlapped = overlapped || activeURLs[url, default: 0] > 0
        activeURLs[url, default: 0] += 1
        active += 1; peak = max(peak, active)
        let gate = gates.removeValue(forKey: url)
        await gate?.wait()
        activeURLs[url, default: 0] -= 1; active -= 1
        return data
    }
}

private actor ArtworkSelectionFixture: MusicArtworkSelecting {
    private(set) var failures = 0
    func failedArtwork(songID: String, url: URL, attempted: Set<URL>) -> URL? {
        failures += 1; return nil
    }
    func succeededArtwork(songID: String, url: URL, replacing requested: URL) -> URL? { url }
}

@MainActor
final class MusicArtworkImagesTests: XCTestCase {
    private func images() -> MusicArtworkImages {
        let configuration = URLSessionConfiguration.ephemeral
        configuration.protocolClasses = [ArtworkImageProtocol.self]
        return MusicArtworkImages(http: MusicArtworkHTTP(configuration: configuration))
    }
    private func png(_ color: UIColor = .red) -> Data {
        UIGraphicsImageRenderer(size: CGSize(width: 20, height: 20)).pngData { context in
            color.setFill(); context.fill(CGRect(x: 0, y: 0, width: 20, height: 20))
        }
    }
    private func cancelled<T>(_ task: Task<T, Error>, file: StaticString = #filePath, line: UInt = #line) async {
        do { _ = try await task.value; XCTFail("Cancelled consumer succeeded", file: file, line: line) }
        catch { XCTAssertTrue(error is CancellationError, file: file, line: line) }
    }

    func testSharedArtworkLimitsHTTPFlightsToFourAndCompletesQueuedURLs() async throws {
        let data = png(), cache = ArtworkPresentationFixture(), images = images()
        images.configure(cache: cache, selector: nil)
        let started = expectation(description: "four physical HTTP reads"); started.expectedFulfillmentCount = 4
        let active = (0..<4).map { _ in
            ArtworkImageFixture([.init(status: 200, data: data, held: true)], started: { _ in started.fulfill() })
        }
        let nextStarted = expectation(description: "queued HTTP reads admitted"); nextStarted.expectedFulfillmentCount = 4
        let waiting = (0..<4).map { _ in
            ArtworkImageFixture([.init(status: 200, data: data, held: true)], started: { _ in nextStarted.fulfill() })
        }
        defer { (active + waiting).forEach { $0.close() } }
        let first = active.map { fixture in Task { try await images.read(fixture.url) } }
        await fulfillment(of: [started], timeout: 3)
        let entered = expectation(description: "queued consumers entered"); entered.expectedFulfillmentCount = 4
        let queued = waiting.map { fixture in
            Task { entered.fulfill(); return try await images.read(fixture.url) }
        }
        await fulfillment(of: [entered], timeout: 3)
        XCTAssertEqual(active.reduce(0) { $0 + $1.requestCount }, 4)
        XCTAssertTrue(waiting.allSatisfy { $0.requestCount == 0 })
        XCTAssertEqual(Set(cache.statistics().reads), Set(active.map(\.url)), "Waiting URLs must not read cache bytes")
        XCTAssertTrue(cache.statistics().stores.isEmpty)
        active.forEach { $0.finish() }
        await fulfillment(of: [nextStarted], timeout: 3)
        for task in first {
            let value = try await task.value
            XCTAssertEqual(value.1, data)
        }
        waiting.forEach { $0.finish() }
        for task in queued {
            let value = try await task.value
            XCTAssertEqual(value.1, data)
        }
        XCTAssertTrue((active + waiting).allSatisfy { $0.requestCount == 1 })
        XCTAssertEqual(cache.statistics().reads.count, 8); XCTAssertEqual(cache.statistics().stores.count, 8)
        XCTAssertEqual(cache.statistics().saved, 8)
        await images.close()
    }

    func testArtworkSlotsCoverByteReadDecodeAndStoreIncludingSuppliedBytes() async throws {
        let cache = ArtworkPresentationFixture(), data = png(), supplied = png(.blue)
        let network = ArtworkFlightNetwork(data)
        let images = ArtworkImages(maximumBytes: MusicArtworkHTTP.maximumBytes, byteCache: cache) {
            await network.read($0)
        }
        let urls = (0..<6).map { URL(string: "https://artwork-cdn.invalid/slots/\($0).png")! }
        let readEntered = expectation(description: "four physical cache reads"); readEntered.expectedFulfillmentCount = 4
        let storeEntered = expectation(description: "four physical saves"); storeEntered.expectedFulfillmentCount = 4
        let readGate = ArtworkFlightGate(readEntered), storeGate = ArtworkFlightGate(storeEntered)
        cache.blockReads(Array(urls.prefix(4)), on: readGate)
        cache.blockStores(Array(urls.prefix(4)), on: storeGate)
        let first = urls.prefix(4).map { url in Task { try await images.read(url) } }
        await fulfillment(of: [readEntered], timeout: 3)
        let entered = expectation(description: "cold and supplied-byte demands queued"); entered.expectedFulfillmentCount = 2
        let cold = Task { entered.fulfill(); return try await images.read(urls[4]) }
        var suppliedFinished = false
        let provided = Task {
            entered.fulfill()
            let value = try await images.read(urls[5], cachedData: supplied)
            suppliedFinished = true
            return value
        }
        await fulfillment(of: [entered], timeout: 3)
        XCTAssertEqual(cache.statistics().reads.count, 4); XCTAssertFalse(suppliedFinished)
        let beforeReadRelease = await network.statistics(); XCTAssertTrue(beforeReadRelease.requests.isEmpty)
        await readGate.release()
        await fulfillment(of: [storeEntered], timeout: 3)
        XCTAssertEqual(cache.statistics().reads.count, 4); XCTAssertEqual(cache.statistics().stores.count, 4)
        XCTAssertFalse(suppliedFinished, "Supplied bytes must not decode ahead of the shared execution boundary")
        let beforeStoreRelease = await network.statistics()
        XCTAssertEqual(Set(beforeStoreRelease.requests), Set(urls.prefix(4)))
        await storeGate.release()
        for task in first { _ = try await task.value }
        let coldValue = try await cold.value
        XCTAssertEqual(coldValue.1, data)
        let suppliedValue = try await provided.value
        XCTAssertEqual(suppliedValue.1, supplied); XCTAssertTrue(images.cached(urls[5]) === suppliedValue.0)
        let hot = try await images.read(urls[5])
        XCTAssertTrue(hot.0 === suppliedValue.0); XCTAssertNil(hot.1)
        let final = await network.statistics()
        XCTAssertEqual(Set(final.requests), Set(urls.prefix(5)))
        XCTAssertEqual(Set(cache.statistics().reads), Set(urls.prefix(5)))
        XCTAssertEqual(Set(cache.statistics().stores), Set(urls.prefix(5)))
        XCTAssertEqual(cache.statistics().saved, 5)
        await images.close()
    }

    func testQueuedLastArtworkConsumerCancelsWithoutByteReadOrHTTP() async throws {
        let cache = ArtworkPresentationFixture(), data = png(), images = images()
        images.configure(cache: cache, selector: nil)
        let started = expectation(description: "four HTTP slots occupied"); started.expectedFulfillmentCount = 4
        let active = (0..<4).map { _ in
            ArtworkImageFixture([.init(status: 200, data: data, held: true)], started: { _ in started.fulfill() })
        }
        let waiting = ArtworkImageFixture([.init(status: 200, data: data, held: true)])
        defer { (active + [waiting]).forEach { $0.close() } }
        let first = active.map { fixture in Task { try await images.read(fixture.url) } }
        await fulfillment(of: [started], timeout: 3)
        let entered = expectation(description: "last queued consumer entered"), settled = expectation(description: "pure queued cancellation settled")
        let queued = Task {
            entered.fulfill()
            defer { settled.fulfill() }
            return try await images.read(waiting.url)
        }
        await fulfillment(of: [entered], timeout: 3)
        queued.cancel()
        await fulfillment(of: [settled], timeout: 3)
        XCTAssertEqual(waiting.requestCount, 0); XCTAssertFalse(cache.statistics().reads.contains(waiting.url))
        await cancelled(queued)
        active.forEach { $0.finish() }
        for task in first { _ = try await task.value }
        XCTAssertEqual(waiting.requestCount, 0)
        XCTAssertEqual(Set(cache.statistics().reads), Set(active.map(\.url)))
        XCTAssertEqual(Set(cache.statistics().stores), Set(active.map(\.url)))
        await images.close()
    }

    func testSharedArtworkConsumerCancellationDoesNotReleaseAnOccupiedSlot() async throws {
        let data = png(), images = images()
        let started = expectation(description: "four active HTTP flights"); started.expectedFulfillmentCount = 4
        let active = (0..<4).map { _ in
            ArtworkImageFixture([.init(status: 200, data: data, held: true)], started: { _ in started.fulfill() })
        }
        let admitted = expectation(description: "queued HTTP flight starts after shared producer finishes")
        let waiting = ArtworkImageFixture([.init(status: 200, data: data, held: true)], started: { _ in admitted.fulfill() })
        defer { (active + [waiting]).forEach { $0.close() } }
        let first = active.map { fixture in Task { try await images.read(fixture.url) } }
        await fulfillment(of: [started], timeout: 3)
        let entered = expectation(description: "second shared consumer and queued demand entered"); entered.expectedFulfillmentCount = 2
        let remaining = Task { entered.fulfill(); return try await images.read(active[0].url) }
        let queued = Task { entered.fulfill(); return try await images.read(waiting.url) }
        await fulfillment(of: [entered], timeout: 3)
        first[0].cancel(); await cancelled(first[0])
        XCTAssertEqual(active[0].stopCount, 0); XCTAssertEqual(active[0].requestCount, 1)
        XCTAssertEqual(waiting.requestCount, 0, "Detaching one shared consumer does not return the producer's slot")
        active[0].finish()
        let remainingValue = try await remaining.value
        XCTAssertEqual(remainingValue.1, data)
        await fulfillment(of: [admitted], timeout: 3)
        (Array(active.dropFirst()) + [waiting]).forEach { $0.finish() }
        for task in first.dropFirst() { _ = try await task.value }
        _ = try await queued.value
        XCTAssertTrue((active + [waiting]).allSatisfy { $0.requestCount == 1 })
        await images.close()
    }

    func testSharedQueuedArtworkConsumersUseOneSlotAndCancelIndependently() async throws {
        let data = png(), images = images()
        let started = expectation(description: "all four slots active"); started.expectedFulfillmentCount = 4
        let active = (0..<4).map { _ in
            ArtworkImageFixture([.init(status: 200, data: data, held: true)], started: { _ in started.fulfill() })
        }
        let admitted = expectation(description: "one shared queued HTTP read")
        let waiting = ArtworkImageFixture([.init(status: 200, data: data, held: true)], started: { _ in admitted.fulfill() })
        defer { (active + [waiting]).forEach { $0.close() } }
        let first = active.map { fixture in Task { try await images.read(fixture.url) } }
        await fulfillment(of: [started], timeout: 3)
        let entered = expectation(description: "both queued consumers entered"); entered.expectedFulfillmentCount = 2
        let cancelledConsumer = Task { entered.fulfill(); return try await images.read(waiting.url) }
        let remaining = Task { entered.fulfill(); return try await images.read(waiting.url) }
        await fulfillment(of: [entered], timeout: 3)
        cancelledConsumer.cancel(); await cancelled(cancelledConsumer)
        XCTAssertEqual(waiting.requestCount, 0)
        active[0].finish(); _ = try await first[0].value
        await fulfillment(of: [admitted], timeout: 3)
        waiting.finish()
        let remainingValue = try await remaining.value
        XCTAssertEqual(remainingValue.1, data)
        active.dropFirst().forEach { $0.finish() }
        for task in first.dropFirst() { _ = try await task.value }
        XCTAssertEqual(waiting.requestCount, 1, "Shared queued demand must not reserve a slot per consumer")
        await images.close()
    }

    func testLastArtworkCancellationKeepsSlotUntilUncooperativeReadReturns() async throws {
        let cache = ArtworkPresentationFixture(), network = ArtworkFlightNetwork(png())
        let images = ArtworkImages(maximumBytes: MusicArtworkHTTP.maximumBytes, byteCache: cache) {
            await network.read($0)
        }
        let urls = (0..<5).map { URL(string: "https://artwork-cdn.invalid/cancel/\($0).png")! }
        let started = expectation(description: "four occupied slots"); started.expectedFulfillmentCount = 4
        let cancellation = expectation(description: "last consumer's physical cancellation observed")
        let lastGate = ArtworkFlightGate(started, cancelled: cancellation), otherGate = ArtworkFlightGate(started)
        await network.block([urls[0]], on: lastGate)
        await network.block(Array(urls[1..<4]), on: otherGate)
        var lastSettled = false
        let last = Task {
            defer { lastSettled = true }
            return try await images.read(urls[0])
        }
        let other = urls[1..<4].map { url in Task { try await images.read(url) } }
        await fulfillment(of: [started], timeout: 3)
        let entered = expectation(description: "replacement URL queued")
        let waiting = Task { entered.fulfill(); return try await images.read(urls[4]) }
        await fulfillment(of: [entered], timeout: 3)
        last.cancel()
        await fulfillment(of: [cancellation], timeout: 3)
        XCTAssertFalse(lastSettled, "Logical cancellation must retain the physical producer and its slot")
        let draining = await network.statistics()
        XCTAssertEqual(draining.active, 4); XCTAssertEqual(draining.requests.count, 4)
        XCTAssertFalse(cache.statistics().reads.contains(urls[4]))
        await lastGate.release()
        await cancelled(last)
        _ = try await waiting.value
        XCTAssertTrue(lastSettled); XCTAssertFalse(cache.statistics().stores.contains(urls[0]))
        let admitted = await network.statistics()
        XCTAssertEqual(admitted.requests.filter { $0 == urls[4] }.count, 1)
        await otherGate.release()
        for task in other { _ = try await task.value }
        let final = await network.statistics()
        XCTAssertEqual(final.active, 0); XCTAssertEqual(final.peak, 4)
        XCTAssertEqual(cache.statistics().saved, 4)
        await images.close()
    }

    func testArtworkCloseSettlesQueuedDemandAndWaitsForUncooperativePhysicalReads() async {
        let cache = ArtworkPresentationFixture(), network = ArtworkFlightNetwork(png())
        let images = ArtworkImages(maximumBytes: MusicArtworkHTTP.maximumBytes, byteCache: cache) {
            await network.read($0)
        }
        let urls = (0..<6).map { URL(string: "https://artwork-cdn.invalid/close/\($0).png")! }
        let started = expectation(description: "four physical reads"); started.expectedFulfillmentCount = 4
        let cancelledReads = expectation(description: "four physical reads observe cancellation"); cancelledReads.expectedFulfillmentCount = 4
        let gate = ArtworkFlightGate(started, cancelled: cancelledReads)
        await network.block(Array(urls.prefix(4)), on: gate)
        let active = urls.prefix(4).map { url in Task { try await images.read(url) } }
        await fulfillment(of: [started], timeout: 3)
        let entered = expectation(description: "queued close demands entered"); entered.expectedFulfillmentCount = 2
        let settled = expectation(description: "queued close demands settle without physical work"); settled.expectedFulfillmentCount = 2
        let waiting = urls.suffix(2).map { url in
            Task { entered.fulfill(); defer { settled.fulfill() }; return try await images.read(url) }
        }
        await fulfillment(of: [entered], timeout: 3)
        var closed = false
        let closing = Task { await images.close(); closed = true }
        await fulfillment(of: [cancelledReads, settled], timeout: 3)
        XCTAssertFalse(closed, "Task.cancel cannot acknowledge a still-running physical read")
        let draining = await network.statistics()
        XCTAssertEqual(draining.active, 4); XCTAssertEqual(draining.requests.count, 4)
        XCTAssertEqual(Set(cache.statistics().reads), Set(urls.prefix(4)))
        await gate.release()
        await closing.value
        for task in active + waiting { await cancelled(task) }
        let final = await network.statistics()
        XCTAssertTrue(closed); XCTAssertEqual(final.active, 0); XCTAssertEqual(final.peak, 4)
        XCTAssertEqual(final.requests.count, 4); XCTAssertTrue(cache.statistics().stores.isEmpty)
        XCTAssertEqual(cache.statistics().saved, 0)
    }

    func testArtworkRevisionChangesDrainOldSameURLAndDiscardQueuedDemand() async throws {
        for replaceCache in [false, true] {
            let cache = ArtworkPresentationFixture(), freshCache = replaceCache ? ArtworkPresentationFixture() : cache
            let network = ArtworkFlightNetwork(png())
            let images = ArtworkImages(maximumBytes: MusicArtworkHTTP.maximumBytes, byteCache: cache) {
                await network.read($0)
            }
            let urls = (0..<5).map { URL(string: "https://artwork-cdn.invalid/revision/\($0).png")! }
            let started = expectation(description: "old physical reads"); started.expectedFulfillmentCount = 4
            let cancellation = expectation(description: "old producer cancellation observed")
            cancellation.expectedFulfillmentCount = replaceCache ? 4 : 1
            let sameKeyGate = ArtworkFlightGate(started, cancelled: cancellation)
            let otherGate = ArtworkFlightGate(started, cancelled: replaceCache ? cancellation : nil)
            await network.block([urls[0]], on: sameKeyGate)
            await network.block(Array(urls[1..<4]), on: otherGate)
            let old = urls.prefix(4).map { url in Task { try await images.read(url) } }
            await fulfillment(of: [started], timeout: 3)
            let entered = expectation(description: "old queued demand entered")
            let waiting = Task { entered.fulfill(); return try await images.read(urls[4]) }
            await fulfillment(of: [entered], timeout: 3)
            if replaceCache { images.setByteCache(freshCache) } else { await cache.clearImages() }
            let successorEntered = expectation(description: "same-key successor entered")
            let successor = Task { successorEntered.fulfill(); return try await images.read(urls[0]) }
            await fulfillment(of: [successorEntered, cancellation], timeout: 3)
            let draining = await network.statistics()
            XCTAssertEqual(draining.active, 4); XCTAssertEqual(draining.requests.count, 4)
            XCTAssertFalse(cache.statistics().reads.contains(urls[4]))
            // Make three slots available while the old same-key read is still
            // physically held. Capacity alone cannot mask overlapping successors.
            await otherGate.release()
            for task in old.dropFirst() { await cancelled(task) }
            await cancelled(waiting)
            let sameKeyDraining = await network.statistics()
            XCTAssertEqual(sameKeyDraining.active, 1); XCTAssertEqual(sameKeyDraining.requests.count, 4)
            await sameKeyGate.release()
            await cancelled(old[0])
            _ = try await successor.value
            let final = await network.statistics()
            XCTAssertFalse(final.overlapped, "The old resource must physically drain before its same-key successor")
            XCTAssertEqual(final.peak, 4); XCTAssertEqual(final.active, 0)
            XCTAssertEqual(final.requests.filter { $0 == urls[0] }.count, 2)
            XCTAssertFalse(final.requests.contains(urls[4]))
            XCTAssertEqual(freshCache.statistics().stores, [urls[0]])
            XCTAssertEqual(freshCache.statistics().saved, 1)
            if replaceCache { XCTAssertTrue(cache.statistics().stores.isEmpty); XCTAssertEqual(cache.statistics().saved, 0) }
            await images.close()
        }
    }

    func testColdConsumersShareTransportAndDecodedImageWhileOtherURLsRemainIndependent() async throws {
        let started = expectation(description: "shared transport started")
        let otherStarted = expectation(description: "other transport started")
        let secondEntered = expectation(description: "second consumer admitted")
        let fixture = ArtworkImageFixture([.init(status: 200, data: png(), held: true)],
            started: { _ in started.fulfill() })
        let other = ArtworkImageFixture([.init(status: 200, data: png(.blue), held: true)],
            started: { _ in otherStarted.fulfill() })
        defer { fixture.close(); other.close() }
        let images = images()
        let first = Task { try await images.read(fixture.url) }
        let second = Task {
            secondEntered.fulfill()
            return try await images.read(fixture.url)
        }
        let independent = Task { try await images.read(other.url) }
        await fulfillment(of: [started, otherStarted, secondEntered], timeout: 3)
        fixture.finish(); other.finish()
        let a = try await first.value, b = try await second.value, c = try await independent.value
        XCTAssertEqual(fixture.requestCount, 1); XCTAssertEqual(other.requestCount, 1)
        XCTAssertTrue(a.0 === b.0, "Sharing must include decoding, not only HTTP bytes")
        XCTAssertFalse(a.0 === c.0)
        XCTAssertEqual(a.1, b.1)
        let warm = try await images.read(fixture.url)
        XCTAssertTrue(warm.0 === a.0); XCTAssertNil(warm.1)
        XCTAssertEqual(fixture.requestCount, 1)
    }

    func testMusicAndFilmArtworkOwnersKeepFlightsAndCachesSeparate() async throws {
        let started = expectation(description: "each artwork owner starts its own read")
        started.expectedFulfillmentCount = 2
        let fixture = ArtworkImageFixture([.init(status: 200, data: png(), held: true)],
            started: { _ in started.fulfill() })
        defer { fixture.close() }
        let configuration = URLSessionConfiguration.ephemeral
        configuration.protocolClasses = [ArtworkImageProtocol.self]
        let http = MusicArtworkHTTP(configuration: configuration)
        let music = images()
        let film = ArtworkImages(maximumBytes: 4 * 1_024 * 1_024) { try await http.read($0) }
        let musicRead = Task { try await music.read(fixture.url) }
        let filmRead = Task { try await film.read(fixture.url) }
        await fulfillment(of: [started], timeout: 3)
        fixture.finish()
        let musicImage = try await musicRead.value, filmImage = try await filmRead.value
        XCTAssertEqual(fixture.requestCount, 2, "Different domain owners must not share a flight")
        XCTAssertFalse(musicImage.0 === filmImage.0, "Each domain owns its decoded cache")
        XCTAssertTrue(music.cached(fixture.url) === musicImage.0)
        XCTAssertTrue(film.cached(fixture.url) === filmImage.0)
        let warmMusic = try await music.read(fixture.url), warmFilm = try await film.read(fixture.url)
        XCTAssertTrue(warmMusic.0 === musicImage.0)
        XCTAssertTrue(warmFilm.0 === filmImage.0)
        XCTAssertNil(warmMusic.1); XCTAssertNil(warmFilm.1)
        XCTAssertEqual(fixture.requestCount, 2)
    }

    func testCancellingOneConsumerDoesNotStopTheRemainingConsumer() async throws {
        let started = expectation(description: "transport started")
        let joined = expectation(description: "remaining consumer admitted")
        let data = png()
        let fixture = ArtworkImageFixture([.init(status: 200, data: data, held: true)],
            started: { _ in started.fulfill() })
        defer { fixture.close() }
        let images = images()
        let first = Task { try await images.read(fixture.url) }
        let remaining = Task {
            joined.fulfill()
            return try await images.read(fixture.url)
        }
        await fulfillment(of: [started, joined], timeout: 3)
        first.cancel()
        do { _ = try await first.value; XCTFail("Cancelled consumer succeeded") }
        catch { XCTAssertTrue(error is CancellationError) }
        XCTAssertEqual(fixture.stopCount, 0)
        fixture.finish()
        let value = try await remaining.value
        XCTAssertEqual(value.1, data)
        XCTAssertEqual(fixture.requestCount, 1)
    }

    func testLastCancellationWaitsForPhysicalStopAndTheURLCanBeReadAgain() async throws {
        let started = expectation(description: "initial transport started")
        let stopped = expectation(description: "physical transport stopped")
        let settled = expectation(description: "last consumer returned")
        let restarted = expectation(description: "replacement transport started")
        let fixture = ArtworkImageFixture([.init(status: 200, data: png(), held: true)],
            started: { count in (count == 1 ? started : restarted).fulfill() },
            stopped: { stopped.fulfill() })
        defer { fixture.close() }
        let images = images()
        let first = Task {
            defer { settled.fulfill() }
            do { _ = try await images.read(fixture.url); XCTFail("Abandoned image accepted") }
            catch { XCTAssertTrue(error is CancellationError) }
        }
        await fulfillment(of: [started], timeout: 3)
        first.cancel()
        await fulfillment(of: [stopped, settled], timeout: 3, enforceOrder: true)
        await first.value
        XCTAssertNil(images.cached(fixture.url))
        let replacement = Task { try await images.read(fixture.url) }
        await fulfillment(of: [restarted], timeout: 3)
        fixture.finish()
        _ = try await replacement.value
        XCTAssertEqual(fixture.requestCount, 2)
        XCTAssertNotNil(images.cached(fixture.url))
    }

    func testFailedViewDoesNotHotRetryButExplicitRetryAndWarmReuseRecover() async throws {
        let fixture = ArtworkImageFixture([.init(status: 503, data: Data()),
                                           .init(status: 200, data: png())])
        defer { fixture.close() }
        let images = images(), view = MusicArtworkView(images: images)
        let failed = expectation(description: "first read failed")
        view.onFailure = { _ in failed.fulfill() }
        view.load(fixture.url)
        await fulfillment(of: [failed], timeout: 3)
        for _ in 0..<20 { view.load(fixture.url) }
        XCTAssertEqual(fixture.requestCount, 1); XCTAssertNil(view.image)
        let recovered = expectation(description: "explicit retry displayed image")
        view.onImage = { _, _ in recovered.fulfill() }
        view.retryFailedLoad()
        await fulfillment(of: [recovered], timeout: 3)
        let image = try XCTUnwrap(view.image)
        view.retryFailedLoad(); view.load(fixture.url)
        let warm = MusicArtworkView(images: images)
        warm.load(fixture.url)
        XCTAssertTrue(warm.image === image); XCTAssertTrue(view.image === image)
        XCTAssertEqual(fixture.requestCount, 2)
    }

    func testNewVisibleAttachmentRetriesNetworkFailureButHiddenAttachmentDoesNot() async throws {
        let fixture = ArtworkImageFixture([.init(status: 503, data: Data()),
                                           .init(status: 200, data: png())])
        defer { fixture.close() }
        let artwork = MusicArtworkView(images: images())
        let failed = expectation(description: "network failed")
        artwork.onFailure = { _ in failed.fulfill() }
        artwork.load(fixture.url)
        await fulfillment(of: [failed], timeout: 3)
        let root = UIViewController()
        let scene = UIApplication.shared.connectedScenes.compactMap { $0 as? UIWindowScene }.first
        let window = scene.map { UIWindow(windowScene: $0) }
            ?? UIWindow(frame: CGRect(x: 0, y: 0, width: 390, height: 844))
        window.rootViewController = root; window.makeKeyAndVisible()
        defer { window.isHidden = true; window.rootViewController = nil }
        let parent = UIView(frame: CGRect(x: 10, y: 10, width: 80, height: 80))
        parent.isHidden = true; root.view.addSubview(parent)
        artwork.frame = CGRect(x: 0, y: 0, width: 48, height: 48)
        parent.addSubview(artwork)
        XCTAssertEqual(fixture.requestCount, 1)
        artwork.removeFromSuperview(); parent.isHidden = false
        let recovered = expectation(description: "new display recovered image")
        artwork.onImage = { _, _ in recovered.fulfill() }
        parent.addSubview(artwork)
        await fulfillment(of: [recovered], timeout: 3)
        XCTAssertNotNil(artwork.image); XCTAssertEqual(fixture.requestCount, 2)
    }

    func testDecodeFailureDoesNotRetryWhenShownOrRefreshedAndNewURLStillLoads() async throws {
        let retried = expectation(description: "deterministic image is not requested again")
        retried.isInverted = true
        let invalid = ArtworkImageFixture([.init(status: 200, data: Data([1, 2, 3]))],
            started: { if $0 > 1 { retried.fulfill() } })
        let valid = ArtworkImageFixture([.init(status: 200, data: png(.blue))])
        defer { invalid.close(); valid.close() }
        let artwork = MusicArtworkView(images: images())
        let failed = expectation(description: "decode failed")
        artwork.onFailure = { _ in failed.fulfill() }
        artwork.load(invalid.url)
        await fulfillment(of: [failed], timeout: 3)
        artwork.onFailure = { _ in XCTFail("A deterministic failure must not be requested again") }
        let root = UIViewController()
        let scene = UIApplication.shared.connectedScenes.compactMap { $0 as? UIWindowScene }.first
        let window = scene.map { UIWindow(windowScene: $0) }
            ?? UIWindow(frame: CGRect(x: 0, y: 0, width: 390, height: 844))
        window.rootViewController = root; window.makeKeyAndVisible()
        defer { window.isHidden = true; window.rootViewController = nil }
        artwork.frame = CGRect(x: 10, y: 10, width: 48, height: 48)
        root.view.addSubview(artwork)
        for _ in 0..<3 {
            artwork.load(invalid.url)
            artwork.retryFailedLoad()
            MusicArtworkView.retryFailedLoads(in: root.view)
            artwork.removeFromSuperview()
            root.view.addSubview(artwork)
        }
        await fulfillment(of: [retried], timeout: 0.1)
        XCTAssertEqual(invalid.requestCount, 1); XCTAssertNil(artwork.image)
        let displayed = expectation(description: "new URL displayed")
        artwork.onImage = { _, _ in displayed.fulfill() }
        artwork.load(valid.url)
        await fulfillment(of: [displayed], timeout: 3)
        XCTAssertEqual(valid.requestCount, 1); XCTAssertNotNil(artwork.image)
    }

    func testDeterministicLoadErrorsDoNotRetryAndTransientErrorsRecover() async throws {
        let fixed: [URLError.Code] = [
            .badURL, .unsupportedURL, .cannotDecodeRawData, .cannotDecodeContentData,
            .dataLengthExceedsMaximum
        ]
        let transient: [URLError.Code] = [.timedOut, .networkConnectionLost, .notConnectedToInternet, .unknown]
        for code in fixed + transient {
            let fixture = ArtworkImageFixture([
                .init(status: 200, data: Data(), error: URLError(code)),
                .init(status: 200, data: png())
            ])
            defer { fixture.close() }
            let view = MusicArtworkView(images: images())
            let failed = expectation(description: "failure \(code)")
            view.onFailure = { _ in failed.fulfill() }
            view.load(fixture.url)
            await fulfillment(of: [failed], timeout: 3)
            let recovered = expectation(description: "retry \(code)")
            recovered.isInverted = fixed.contains(code)
            view.onImage = { _, _ in recovered.fulfill() }
            for _ in 0..<3 { view.load(fixture.url); view.retryFailedLoad() }
            await fulfillment(of: [recovered], timeout: fixed.contains(code) ? 0.1 : 3)
            XCTAssertEqual(fixture.requestCount, fixed.contains(code) ? 1 : 2, "\(code)")
            XCTAssertEqual(view.image == nil, fixed.contains(code), "\(code)")
        }
    }

    func testCancelledReadStaysNeutralAndCanLoadAgain() async throws {
        let started = expectation(description: "cancelled transport started")
        let fixture = ArtworkImageFixture([
            .init(status: 200, data: Data(), error: URLError(.cancelled)),
            .init(status: 200, data: png())
        ], started: { if $0 == 1 { started.fulfill() } })
        defer { fixture.close() }
        let view = MusicArtworkView(images: images())
        let failure = expectation(description: "cancellation does not report failure")
        failure.isInverted = true
        view.onFailure = { _ in failure.fulfill() }
        view.load(fixture.url)
        await fulfillment(of: [started], timeout: 3)
        await fulfillment(of: [failure], timeout: 0.1)
        XCTAssertEqual(fixture.requestCount, 1); XCTAssertNil(view.image)
        let displayed = expectation(description: "load after cancellation")
        view.onImage = { _, _ in displayed.fulfill() }
        view.load(fixture.url)
        await fulfillment(of: [displayed], timeout: 3)
        XCTAssertEqual(fixture.requestCount, 2); XCTAssertNotNil(view.image)
    }

    func testCancelledTrackReadAllowsOneNewSameTrackBindingWithoutFailureOrHotReload() async throws {
        let started = expectation(description: "track read cancelled")
        let fixture = ArtworkImageFixture([
            .init(status: 200, data: Data(), error: URLError(.cancelled)),
            .init(status: 200, data: png())
        ], started: { if $0 == 1 { started.fulfill() } })
        defer { fixture.close() }
        let images = images(), cache = ArtworkPresentationFixture(), selector = ArtworkSelectionFixture()
        images.configure(cache: cache, selector: selector)
        let view = MusicArtworkView(images: images)
        let songID = UUID().uuidString
        let track = MusicRemoteTrack(ref: .init(kind: .library, id: songID),
            songId: songID, title: "Track", artwork: fixture.url)
        let failure = expectation(description: "cancellation remains neutral"); failure.isInverted = true
        view.onFailure = { _ in failure.fulfill() }
        view.load(track)
        await fulfillment(of: [started], timeout: 3)
        await fulfillment(of: [failure], timeout: 0.1)
        let failedSelections = await selector.failures
        XCTAssertEqual(failedSelections, 0); XCTAssertEqual(fixture.requestCount, 1)
        XCTAssertNil(view.image); XCTAssertNil(images.cached(fixture.url))
        let displayed = expectation(description: "new same-track demand displays image")
        view.onImage = { _, _ in displayed.fulfill() }
        for _ in 0..<20 { view.load(track) }
        await fulfillment(of: [displayed], timeout: 3)
        let image = try XCTUnwrap(view.image)
        view.onImage = nil
        for _ in 0..<20 { view.load(track); view.retryFailedLoad() }
        XCTAssertTrue(view.image === image); XCTAssertEqual(fixture.requestCount, 2)
        let after = await selector.failures; XCTAssertEqual(after, 0)
        await images.close()
    }

    func testClearedTrackReadWaitsForNewVisibleDemandBeforeTheSameViewReloads() async throws {
        let started = expectation(description: "track read started")
        let data = png()
        let fixture = ArtworkImageFixture([
            .init(status: 200, data: data, held: true), .init(status: 200, data: data)
        ], started: { if $0 == 1 { started.fulfill() } })
        defer { fixture.close() }
        let images = images(), cache = ArtworkPresentationFixture(), selector = ArtworkSelectionFixture()
        images.configure(cache: cache, selector: selector)
        let view = MusicArtworkView(images: images)
        let songID = UUID().uuidString
        let track = MusicRemoteTrack(ref: .init(kind: .library, id: songID),
            songId: songID, title: "Track", artwork: fixture.url)
        let premature = expectation(description: "clear neither fails nor refills"); premature.isInverted = true
        view.onFailure = { _ in premature.fulfill() }; view.onImage = { _, _ in premature.fulfill() }
        view.load(track)
        await fulfillment(of: [started], timeout: 3)
        await cache.clearImages(); fixture.finish()
        await fulfillment(of: [premature], timeout: 0.1)
        let stored = await cache.read(fixture.url), failures = await selector.failures
        XCTAssertNil(stored); XCTAssertNil(images.cached(fixture.url)); XCTAssertNil(view.image)
        XCTAssertEqual(fixture.requestCount, 1); XCTAssertEqual(failures, 0)
        let root = UIViewController()
        let scene = UIApplication.shared.connectedScenes.compactMap { $0 as? UIWindowScene }.first
        let window = scene.map { UIWindow(windowScene: $0) }
            ?? UIWindow(frame: CGRect(x: 0, y: 0, width: 390, height: 844))
        window.rootViewController = root; window.makeKeyAndVisible()
        defer { window.isHidden = true; window.rootViewController = nil }
        view.frame = CGRect(x: 10, y: 10, width: 48, height: 48)
        let displayed = expectation(description: "visible same-view demand reloads")
        view.onFailure = { _ in XCTFail("Cancelled track must not rotate candidates") }
        view.onImage = { _, _ in displayed.fulfill() }
        root.view.addSubview(view)
        await fulfillment(of: [displayed], timeout: 3)
        view.onImage = nil
        for _ in 0..<20 { view.load(track); MusicArtworkView.retryFailedLoads(in: root.view) }
        let replacement = await cache.read(fixture.url), after = await selector.failures
        XCTAssertEqual(replacement?.data, data); XCTAssertNotNil(view.image)
        XCTAssertEqual(fixture.requestCount, 2); XCTAssertEqual(after, 0)
        await images.close()
    }

    func testCancellationErrorDoesNotReportArtworkFailure() async {
        let images = ArtworkImages(maximumBytes: 1_024) { _ in throw CancellationError() }
        let view = ArtworkView(images: images)
        let failure = expectation(description: "CancellationError remains neutral")
        failure.isInverted = true
        view.onFailure = { _ in failure.fulfill() }
        view.load(URL(string: "https://artwork-cdn.invalid/cancelled")!)
        await fulfillment(of: [failure], timeout: 0.1)
        XCTAssertNil(view.image)
    }

    func testWebPVariantsDecodeBoundedPixelsAndShareWarmImages() async throws {
        for kind in ["lossy", "lossless", "extended", "animated"] {
            let fixture = ArtworkImageFixture([.init(status: 200, data: webP(kind))])
            defer { fixture.close() }
            let images = images()
            let (image, bytes, resource) = try await images.read(fixture.url)
            XCTAssertEqual(bytes, webP(kind)); XCTAssertEqual(resource, fixture.url)
            let cgImage = try XCTUnwrap(image.cgImage)
            XCTAssertEqual(cgImage.width, kind == "lossy" ? 1_024 : 8)
            XCTAssertEqual(cgImage.height, kind == "lossy" ? 512 : 4)
            let (warm, warmBytes, warmResource) = try await images.read(fixture.url)
            XCTAssertNil(warmBytes); XCTAssertEqual(warmResource, fixture.url)
            XCTAssertTrue(warm === image); XCTAssertEqual(fixture.requestCount, 1)
            let pixel = try rgba(cgImage, x: kind == "animated" ? 3 : 1, y: 1)
            if kind == "lossy" {
                XCTAssertGreaterThan(pixel[0], 200); XCTAssertLessThan(pixel[1], 40)
                XCTAssertLessThan(pixel[2], 60); XCTAssertEqual(pixel[3], 255)
            } else {
                XCTAssertEqual(pixel[3], 128)
                if kind == "animated" {
                    XCTAssertGreaterThan(pixel[0], 120); XCTAssertLessThan(pixel[1], 15)
                } else {
                    XCTAssertGreaterThan(pixel[2], 95); XCTAssertLessThan(pixel[0], 10)
                }
                XCTAssertEqual(try rgba(cgImage, x: 0, y: 0)[3], 0)
            }
        }
    }

    private func rgba(_ image: CGImage, x: Int, y: Int) throws -> [UInt8] {
        var pixel = [UInt8](repeating: 0, count: 4)
        try pixel.withUnsafeMutableBytes { bytes in
            let context = try XCTUnwrap(CGContext(data: bytes.baseAddress, width: 1, height: 1,
                bitsPerComponent: 8, bytesPerRow: 4, space: CGColorSpaceCreateDeviceRGB(),
                bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue | CGBitmapInfo.byteOrder32Big.rawValue))
            context.setBlendMode(.copy)
            // The context's origin is bottom-left; fixture coordinates are top-left.
            context.draw(image, in: CGRect(x: -CGFloat(x), y: CGFloat(y + 1 - image.height),
                                          width: CGFloat(image.width), height: CGFloat(image.height)))
        }
        return pixel
    }

    private func webP(_ kind: String) -> Data {
        // Independent Pillow/libwebp fixtures: oversized opaque red, blue alpha,
        // extended blue alpha, and a red first frame with a different later frame.
        let encoded: String
        switch kind {
        case "lossy": encoded = """
            UklGRjAJAABXRUJQVlA4ICQJAACQEAGdASpABiADPm02mUmkIyKhIAgAgA2JaW7hd2Ee3AAAE9gHvtk5D32ych77ZOQ99snIe+2T
            kPfbJyHvtk5D32ych77ZOQ99snIe+2TkPfbJyHvtk5D32ych77ZOQ99snIe+2TkPfbJyHvtk5D32ych77ZOQ99snIe+2TkPfbJyH
            vtk5D32ych77ZOQ99snIe+2TkPfbJyHvtk5D32ych77ZOQ99snIe+2TkPfbJyHvtk5D32ych77ZOQ99snIe+2TkPfbJyHvtk5D32
            ych77ZOQ99snIe+2TkPfbJyHvtk5D32ych77ZOQ99snIe+2TkPfbJyHvtk5D32ych77ZOQ99snIe+2TkPfbJyHvtk5D32ych77ZO
            Q99snIe+2TkPfbJyHvtk5D32ych77ZOQ99snIe+2TkPfbJyHvtk5D32ych77ZOQ99snIe+2TkPfbJyHvtk5D32ych77ZOQ99snIe
            +2TkPfbJyHvtk5D32ych77ZOQ99snIe+2TkPfbJyHvtk5D32ych77ZOQ99snIe+2TkPfbJyHvtk5D32ych77ZOQ99snIe+2TkPfb
            JyHvtk5D32ych77ZOQ99snIe+2TkPfbJyHvtk5D32ych77ZOQ99snIe+2TkPfbJyHvtk5D32ych77ZOQ99snIe+2TkPfbJyHvtk5
            D32ych77ZOQ99snIe+2TkPfbJyHvtk5D32ych77ZOQ99snIe+2TkPfbJyHvtk5D32ych77ZOQ99snIe+2TkPfbJyHvtk5D32ych7
            7ZOQ99snIe+2TkPfbJyHvtk5D32ych77ZOQ99snIe+2TkPfbJyHvtk5D32ych77ZOQ99snIe+2TkPfbJyHvtk5D32ych77ZOQ99s
            nIe+2TkPfbJyHvtk5D32ych77ZOQ99snIe+2TkPfbJyHvtk5D32ych77ZOQ99snIe+2TkPfbJyHvtk5D32ych77ZOQ99snIe+2Tk
            PfbJyHvtk5D32ych77ZOQ99snIe+2TkPfbJyHvtk5D32ych77ZOQ99snIe+2TkPfbJyHvtk5D32ych77ZOQ99snIe+2TkPfbJyHv
            tk5D32ych77ZOQ99snIe+2TkPfbJyHvtk5D32ych77ZOQ99snIe+2TkPfbJyHvtk5D32ych77ZOQ99snIe+2TkPfbJyHvtk5D32y
            ch77ZOQ99snIe+2TkPfbJyHvtk5D32ych77ZOQ99snIe+2TkPfbJyHvtk5D32ych77ZOQ99snIe+2TkPfbJyHvtk5D32ych77ZOQ
            99snIe+2TkPfbJyHvtk5D32ych77ZOQ99snIe+2TkPfbJyHvtk5D32ych77ZOQ99snIe+2TkPfbJyHvtk5D32ych77ZOQ99snIe+
            2TkPfbJyHvtk5D32ych77ZOQ99snIe+2TkPfbJyHvtk5D32ych77ZOQ99snIe+2TkPfbJyHvtk5D32ych77ZOQ99snIe+2TkPfbJ
            yHvtk5D32ych77ZOQ99snIe+2TkPfbJyHvtk5D32ych77ZOQ99snIe+2TkPfbJyHvtk5D32ych77ZOQ99snIe+2TkPfbJyHvtk5D
            32ych77ZOQ99snIe+2TkPfbJyHvtk5D32ych77ZOQ99snIe+2TkPfbJyHvtk5D32ych77ZOQ99snIe+2TkPfbJyHvtk5D32ych77
            ZOQ99snIe+2TkPfbJyHvtk5D32ych77ZOQ99snIe+2TkPfbJyHvtk5D32ych77ZOQ99snIe+2TkPfbJyHvtk5D32ych77ZOQ99sn
            Ie+2TkPfbJyHvtk5D32ych77ZOQ99snIe+2TkPfbJyHvtk5D32ych77ZOQ99snIe+2TkPfbJyHvtk5D32ych77ZOQ99snIe+2TkP
            fbJyHvtk5D32ych77ZOQ99snIe+2TkPfbJyHvtk5D32ych77ZOQ99snIe+2TkPfbJyHvtk5D32ych77ZOQ99snIe+2TkPfbJyHvt
            k5D32ych77ZOQ99snIe+2TkPfbJyHvtk5D32ych77ZOQ99snIe+2TkPfbJyHvtk5D32ych77ZOQ99snIe+2TkPfbJyHvtk5D32yc
            h77ZOQ99snIe+2TkPfbJyHvtk5D32ych77ZOQ99snIe+2TkPfbJyHvtk5D32ych77ZOQ99snIe+2TkPfbJyHvtk5D32ych77ZOQ9
            9snIe+2TkPfbJyHvtk5D32ych77ZOQ99snIe+2TkPfbJyHvtk5D32ych77ZOQ99snIe+2TkPfbJyHvtk5D32ych77ZOQ99snIe+2
            TkPfbJyHvtk5D32ych77ZOQ99snIe+2TkPfbJyHvtk5D32ych77ZOQ99snIe+2TkPfbJyHvtk5D32ych77ZOQ99snIe+2TkPfbJy
            Hvtk5D32ych77ZOQ99snIe+2TkPfbJyHvtk5D32ych77ZOQ99snIe+2TkPfbJyHvtk5D32ych77ZOQ99snIe+2TkPfbJyHvtk5D3
            2ych77ZOQ99snIe+2TkPfbJyHvtk5D32ych77ZOQ99snIe+2TkPfbJyHvtk5D32ych77ZOQ99snIe+2TkPfbJyHvtk5D32ych77Z
            OQ99snIe+2TkPfbJyHvtk5D32ych77ZOQ99snIe+2TkPfbJyHvtk5D32ych77ZOQ99snIe+2TkPfbJyHvtk5D32ych77ZOQ99snI
            e+2TkPfbJyHvtk5D32ych77ZOQ99snIe+2TkPfbJyHvtk5D32ych77ZOQ99snIe+2TkPfbJyHvtk5D32ych77ZOQ99snIe+2TkPf
            bJyHvtk5D32ych77ZOQ99snIe+2TkPfbJyHvtk5D32ych77ZOQ99snIe+2TkPfbJyHvtk5D32ych77ZOQ99snIe+2TkPfbJyHvtk
            5D32ych77ZOQ99snIe+2TkPfbJyHvtk5D32ych77ZOP4AAD+/7bUP/+tt383vA///fQD/vQD/vQD/BAAAAAAAAAAAAAAAAAAAAAA
            AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA
            AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=
            """
        case "lossless": encoded = """
            UklGRiIAAABXRUJQVlA4TBUAAAAvB8AAEA8wFKMwyAMY8Lj/f0T/wwEA
            """
        case "extended": encoded = """
            UklGRmgAAABXRUJQVlA4WAoAAAAQAAAABwAAAwAAQUxQSA0AAAABDzCAERHi/v8R/Q8HAFZQOCA0AAAAkAEAnQEqCAAEAAEAHCWg
            AnS6AAOYAP78HE/y2A7CqFP/nEX/ziL/5xF/5xF/i2kdCk8AAA==
            """
        case "animated": encoded = """
            UklGRogAAABXRUJQVlA4WAoAAAASAAAABwAAAwAAQU5JTQYAAAAAAAAAAABBTk1GLAAAAAEAAAAAAAMAAAIAAGQAAAJWUDhMFAAA
            AC8DgAAQDzAUoz8eAxjwmIeI/ocBQU5NRigAAAAAAAAAAAAHAAADAABkAAAAVlA4TBAAAAAvB8AAAAdQ5KhM/wMR0f8A
            """
        default: preconditionFailure("Unknown WebP fixture")
        }
        return Data(base64Encoded: encoded, options: .ignoreUnknownCharacters)!
    }

    func testChangingURLCancelsOldReadWithoutOverwritingTheNewImage() async throws {
        let started = expectation(description: "old URL started")
        let stopped = expectation(description: "old URL stopped")
        let old = ArtworkImageFixture([.init(status: 200, data: png(), held: true)],
            started: { _ in started.fulfill() }, stopped: { stopped.fulfill() })
        let new = ArtworkImageFixture([.init(status: 200, data: png(.blue))])
        defer { old.close(); new.close() }
        let images = images(), artwork = MusicArtworkView(images: images)
        let displayed = expectation(description: "new URL displayed")
        artwork.onImage = { _, _ in displayed.fulfill() }
        artwork.onFailure = { _ in XCTFail("Cancelled old URL must not publish a failure") }
        artwork.load(old.url)
        await fulfillment(of: [started], timeout: 3)
        artwork.load(new.url)
        await fulfillment(of: [stopped, displayed], timeout: 3)
        old.finish()
        XCTAssertTrue(artwork.image === images.cached(new.url))
        XCTAssertNil(images.cached(old.url))
    }
}
#endif
