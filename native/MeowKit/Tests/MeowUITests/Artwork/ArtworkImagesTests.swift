#if os(iOS)
import Foundation
import UIKit
import XCTest
@testable import MeowUI

private actor ArtworkReadGate {
    let entered: XCTestExpectation
    private var released = false
    private var waiters: [CheckedContinuation<Void, Never>] = []
    init(_ entered: XCTestExpectation) { self.entered = entered }
    func wait() async {
        entered.fulfill()
        if !released { await withCheckedContinuation { waiters.append($0) } }
    }
    func release() {
        released = true
        waiters.forEach { $0.resume() }; waiters.removeAll()
    }
}

/// A byte port, not a replacement for the native persistent resource owner.
private final class ArtworkTestBytes: ArtworkByteCaching, @unchecked Sendable {
    private let lock = NSLock()
    private var epoch = UUID()
    private var values: [URL: ArtworkBytes] = [:]
    private var readURLs: [URL] = [], writtenURLs: [URL] = []
    var expiresAt = Date.distantFuture
    var failWrites = false
    var revision: UUID { lock.withLock { epoch } }
    func read(_ url: URL) async -> ArtworkBytes? {
        lock.withLock { readURLs.append(url); return values[url] }
    }
    func store(_ data: Data, for url: URL, revision: UUID) async -> ArtworkBytes? {
        lock.withLock {
            guard revision == epoch, !failWrites else { return nil }
            let saved = ArtworkBytes(data: data, expiresAt: expiresAt)
            writtenURLs.append(url); values[url] = saved
            return saved
        }
    }
    func clear() { lock.withLock { epoch = UUID(); values.removeAll() } }
    func statistics() -> (reads: [URL], writes: [URL], saved: Int) {
        lock.withLock { (readURLs, writtenURLs, values.count) }
    }
}

private actor ArtworkTestNetwork {
    private var bytes: [URL: Data] = [:]
    private var failures: [URL: URLError.Code] = [:]
    private var gates: [URL: ArtworkReadGate] = [:]
    private var cancellation: (@Sendable () -> Void)?
    private var requests: [URL] = []
    func set(_ url: URL, data: Data) { bytes[url] = data }
    func fail(_ url: URL, _ code: URLError.Code?) { failures[url] = code }
    func block(_ url: URL, _ gate: ArtworkReadGate, cancelled: (@Sendable () -> Void)? = nil) {
        gates[url] = gate; cancellation = cancelled
    }
    func log() -> [URL] { requests }
    func load(_ url: URL) async throws -> Data {
        requests.append(url)
        let data = bytes[url], failure = failures[url], gate = gates.removeValue(forKey: url)
        let cancelled = cancellation
        if let gate {
            // Cancellation is observed, but the physical producer deliberately
            // remains alive until released by the test.
            await withTaskCancellationHandler { await gate.wait() } onCancel: { cancelled?() }
        }
        if let failure { throw URLError(failure) }
        guard let data else { throw URLError(.fileDoesNotExist) }
        return data
    }
}

@MainActor
final class ArtworkImagesTests: XCTestCase {
    private let url = URL(string: "https://artwork.invalid/cover?size=256&token=public")!
    private func png(_ color: UIColor = .red) -> Data {
        UIGraphicsImageRenderer(size: CGSize(width: 2, height: 2)).pngData {
            color.setFill(); $0.fill(CGRect(x: 0, y: 0, width: 2, height: 2))
        }
    }
    private func loader(_ bytes: ArtworkTestBytes, _ network: ArtworkTestNetwork) -> ArtworkImages {
        ArtworkImages(maximumBytes: 4 * 1_024 * 1_024, byteCache: bytes) { try await network.load($0) }
    }
    private func cancelled<T>(_ task: Task<T, Error>, file: StaticString = #filePath, line: UInt = #line) async {
        do { _ = try await task.value; XCTFail("Cancelled consumer received an image", file: file, line: line) }
        catch { XCTAssertTrue(error is CancellationError, file: file, line: line) }
    }

    func testEmptyCacheDownloadsOnceThenMemoryAndRebuiltLoaderReuseOriginalBytes() async throws {
        let bytes = ArtworkTestBytes(), network = ArtworkTestNetwork(), data = png()
        await network.set(url, data: data)
        let first = loader(bytes, network)
        let (image, original) = try await first.read(url)
        XCTAssertEqual(original, data)
        let (hot, hotBytes) = try await first.read(url)
        XCTAssertTrue(hot === image); XCTAssertNil(hotBytes)
        XCTAssertEqual(bytes.statistics().reads, [url])
        XCTAssertEqual(bytes.statistics().writes, [url])
        await first.close()
        let rebuilt = loader(bytes, network)
        let (_, coldBytes) = try await rebuilt.read(url)
        XCTAssertEqual(coldBytes, data)
        let requests = await network.log()
        XCTAssertEqual(requests, [url], "Rebuilding the loader must not download a saved resource again")
        XCTAssertEqual(bytes.statistics().reads, [url, url])
        XCTAssertEqual(bytes.statistics().writes, [url])
        await rebuilt.close()
    }

    func testFullURLQueryAndSizeRemainDistinctResources() async throws {
        let bytes = ArtworkTestBytes(), network = ArtworkTestNetwork(), images = loader(bytes, network)
        let small = URL(string: "https://artwork.invalid/cover?size=64&token=public")!
        let otherQuery = URL(string: "https://artwork.invalid/cover?size=256&token=other")!
        for value in [url, small, otherQuery] { await network.set(value, data: png()) }
        for value in [url, small, otherQuery, url] { _ = try await images.read(value) }
        let requests = await network.log()
        XCTAssertEqual(requests, [url, small, otherQuery])
        XCTAssertEqual(bytes.statistics().writes, [url, small, otherQuery])
        await images.close()
    }

    func testCorruptOrExpiredSavedBytesAreMissesAndWriteFailureDoesNotLoseDecodedImage() async throws {
        for corrupt in [false, true] {
            let bytes = ArtworkTestBytes(), network = ArtworkTestNetwork()
            bytes.expiresAt = corrupt ? .distantFuture : .distantPast
            _ = await bytes.store(corrupt ? Data("broken".utf8) : png(), for: url, revision: bytes.revision)
            bytes.failWrites = true
            await network.set(url, data: png(.blue))
            let images = loader(bytes, network)
            let (image, data) = try await images.read(url)
            XCTAssertNotNil(data); XCTAssertTrue(images.cached(url) === image)
            let requests = await network.log(); XCTAssertEqual(requests, [url])
            XCTAssertEqual(bytes.statistics().writes.count, 1, "A failed cache save must not manufacture a durable receipt")
            await images.close()
        }
    }

    func testNetworkDecodeAndResponseBudgetFailuresAreNotCachedAsSuccess() async {
        let data = png()
        let cases: [(Data, URLError.Code?, URLError.Code)] = [
            (data, .notConnectedToInternet, .notConnectedToInternet),
            (Data("not an image".utf8), nil, .cannotDecodeContentData),
            (Data(data.dropLast(12)), nil, .cannotDecodeContentData),
            (Data(repeating: 0, count: 4 * 1_024 * 1_024 + 1), nil, .dataLengthExceedsMaximum)
        ]
        for (data, failure, expected) in cases {
            let bytes = ArtworkTestBytes(), network = ArtworkTestNetwork(), images = loader(bytes, network)
            await network.set(url, data: data); await network.fail(url, failure)
            do { _ = try await images.read(url); XCTFail("Invalid resource became a successful image") }
            catch { XCTAssertEqual((error as? URLError)?.code, expected) }
            XCTAssertNil(images.cached(url)); XCTAssertTrue(bytes.statistics().writes.isEmpty)
            await images.close()
        }
    }

    func testTwoConsumersShareOneResourceAndCancellingOneKeepsTheOtherConsumer() async throws {
        let bytes = ArtworkTestBytes(), network = ArtworkTestNetwork(), images = loader(bytes, network)
        let entered = expectation(description: "one physical image read")
        let gate = ArtworkReadGate(entered)
        await network.set(url, data: png()); await network.block(url, gate)
        let first = Task { try await images.read(url) }
        await fulfillment(of: [entered], timeout: 2)
        // Both registrations run on the UI actor; yield before cancelling so
        // the second consumer has entered the shared read.
        let joined = expectation(description: "second consumer registered")
        let second = Task { joined.fulfill(); return try await images.read(url) }
        await fulfillment(of: [joined], timeout: 2)
        first.cancel()
        await cancelled(first)
        await gate.release()
        _ = try await second.value
        let requests = await network.log(); XCTAssertEqual(requests, [url])
        XCTAssertEqual(bytes.statistics().writes, [url])
        await images.close()
    }

    func testLastCancellationAndCloseWaitForPhysicalProducerAndDoNotPublishLateBytes() async {
        for close in [false, true] {
            let bytes = ArtworkTestBytes(), network = ArtworkTestNetwork(), images = loader(bytes, network)
            let entered = expectation(description: "physical read entered")
            let cancellation = expectation(description: "physical cancellation observed")
            let gate = ArtworkReadGate(entered)
            await network.set(url, data: png())
            await network.block(url, gate, cancelled: { cancellation.fulfill() })
            var finished = false
            let consumer = Task { try await images.read(url) }
            await fulfillment(of: [entered], timeout: 2)
            let ending = Task {
                if close { await images.close() } else { consumer.cancel() }
                if !close { _ = await consumer.result }
                finished = true
            }
            await fulfillment(of: [cancellation], timeout: 2)
            XCTAssertFalse(finished, "Logical cancellation cannot acknowledge an uncooperative physical read")
            await gate.release()
            await ending.value; await cancelled(consumer)
            XCTAssertTrue(finished); XCTAssertNil(images.cached(url))
            XCTAssertTrue(bytes.statistics().writes.isEmpty)
            await images.close()
        }
    }

    func testClearRevokesOldWritesAndMemoryButRetainsAlreadyDisplayedImage() async throws {
        let bytes = ArtworkTestBytes(), network = ArtworkTestNetwork(), images = loader(bytes, network)
        await network.set(url, data: png())
        let shown = (try await images.read(url)).0
        let delayedURL = URL(string: "https://artwork.invalid/delayed")!
        let entered = expectation(description: "old cache epoch download entered"), gate = ArtworkReadGate(entered)
        let cancellation = expectation(description: "new demand cancels cleared epoch producer")
        await network.set(delayedURL, data: png(.blue))
        await network.block(delayedURL, gate, cancelled: { cancellation.fulfill() })
        let old = Task { try await images.read(delayedURL) }
        await fulfillment(of: [entered], timeout: 2)
        bytes.clear()
        XCTAssertNil(images.cached(url)); XCTAssertNotNil(shown.cgImage)
        let fresh = Task { try await images.read(delayedURL) }
        await fulfillment(of: [cancellation], timeout: 2)
        let beforeDrain = await network.log()
        XCTAssertEqual(beforeDrain, [url, delayedURL], "A fresh demand must first drain the old resource producer")
        await gate.release(); await cancelled(old)
        _ = try await fresh.value
        let requests = await network.log()
        XCTAssertEqual(requests, [url, delayedURL, delayedURL])
        XCTAssertEqual(bytes.statistics().writes, [url, delayedURL], "Only the fresh epoch may persist delayed bytes")
        XCTAssertEqual(bytes.statistics().saved, 1)
        await images.close()
    }

    func testExpiredMemoryDoesNotBecomeImmortalThroughRepeatedUIReads() async throws {
        let bytes = ArtworkTestBytes(), network = ArtworkTestNetwork(), images = loader(bytes, network)
        // The port returns the real expiry. An already-expired receipt must
        // never become an unbounded memory hit.
        bytes.expiresAt = .distantPast
        await network.set(url, data: png())
        _ = try await images.read(url)
        XCTAssertNil(images.cached(url))
        _ = try await images.read(url)
        let requests = await network.log(); XCTAssertEqual(requests, [url, url])
        await images.close()
    }

    func testSameSongRetainsPixelsUntilReplacementReadyButSwitchingSongClearsThem() async throws {
        let bytes = ArtworkTestBytes(), network = ArtworkTestNetwork(), images = loader(bytes, network)
        let nextURL = URL(string: "https://artwork.invalid/new")!
        await network.set(url, data: png()); await network.set(nextURL, data: png(.blue))
        let view = ArtworkView(size: 48, images: images)
        let loaded = expectation(description: "initial image displayed")
        view.onImage = { _, _ in loaded.fulfill() }
        view.load(url, displayIdentity: "song-a")
        await fulfillment(of: [loaded], timeout: 2)
        let original = try XCTUnwrap(view.image)
        let entered = expectation(description: "replacement image delayed"), gate = ArtworkReadGate(entered)
        await network.block(nextURL, gate)
        let replaced = expectation(description: "replacement image displayed")
        view.onImage = { _, _ in replaced.fulfill() }
        view.load(nextURL, displayIdentity: "song-a")
        await fulfillment(of: [entered], timeout: 2)
        XCTAssertTrue(view.image === original)
        view.load(nextURL, displayIdentity: "song-a")
        XCTAssertTrue(view.image === original)
        await gate.release(); await fulfillment(of: [replaced], timeout: 2)
        XCTAssertFalse(view.image === original)
        let missing = URL(string: "https://artwork.invalid/other-song")!
        let failed = expectation(description: "new song image failed")
        view.onFailure = { _ in failed.fulfill() }
        view.load(missing, displayIdentity: "song-b")
        XCTAssertNil(view.image, "The previous song's pixels must not stand in for this song")
        await fulfillment(of: [failed], timeout: 2)
        XCTAssertNil(view.image)
        await images.close()
    }

    func testFailedSameSongReplacementKeepsPixelsAndOnlyExplicitRetryRestartsTransientFailure() async throws {
        let bytes = ArtworkTestBytes(), network = ArtworkTestNetwork(), images = loader(bytes, network)
        let nextURL = URL(string: "https://artwork.invalid/retry")!
        await network.set(url, data: png()); await network.set(nextURL, data: png(.blue))
        let view = ArtworkView(size: 48, images: images)
        let loaded = expectation(description: "initial image ready")
        view.onImage = { _, _ in loaded.fulfill() }
        view.load(url, displayIdentity: "song")
        await fulfillment(of: [loaded], timeout: 2)
        let original = try XCTUnwrap(view.image)
        await network.fail(nextURL, .timedOut)
        let failed = expectation(description: "replacement failed")
        view.onFailure = { _ in failed.fulfill() }
        view.load(nextURL, displayIdentity: "song")
        await fulfillment(of: [failed], timeout: 2)
        XCTAssertTrue(view.image === original)
        for _ in 0..<10 { view.load(nextURL, displayIdentity: "song") }
        var requests = await network.log(); XCTAssertEqual(requests, [url, nextURL])
        await network.fail(nextURL, nil)
        let retried = expectation(description: "explicit recovery ready")
        view.onImage = { _, _ in retried.fulfill() }
        view.retryFailedLoad()
        XCTAssertTrue(view.image === original)
        await fulfillment(of: [retried], timeout: 2)
        requests = await network.log(); XCTAssertEqual(requests, [url, nextURL, nextURL])
        XCTAssertFalse(view.image === original)
        await images.close()
    }

    func testDifferentLocalViewSizesAndSongsShareTheSameDownloadedResource() async throws {
        let bytes = ArtworkTestBytes(), network = ArtworkTestNetwork(), images = loader(bytes, network)
        await network.set(url, data: png())
        let small = ArtworkView(size: 40, images: images), large = ArtworkView(size: 256, images: images)
        let ready = expectation(description: "both local sizes displayed"); ready.expectedFulfillmentCount = 2
        small.onImage = { _, _ in ready.fulfill() }; large.onImage = { _, _ in ready.fulfill() }
        small.load(url, displayIdentity: "song-a"); large.load(url, displayIdentity: "song-b")
        await fulfillment(of: [ready], timeout: 2)
        XCTAssertNotNil(small.image); XCTAssertTrue(small.image === large.image)
        let requests = await network.log(); XCTAssertEqual(requests, [url])
        XCTAssertEqual(bytes.statistics().writes, [url])
        await images.close()
    }

    func testOldRequestCannotReplaceNewBindingAndRebindingWaitsForCancelledProducerToDrain() async {
        let bytes = ArtworkTestBytes(), network = ArtworkTestNetwork(), images = loader(bytes, network)
        let entered = expectation(description: "old binding entered"), cancellation = expectation(description: "old binding cancelled")
        let gate = ArtworkReadGate(entered)
        await network.set(url, data: png()); await network.block(url, gate, cancelled: { cancellation.fulfill() })
        let view = ArtworkView(size: 48, images: images)
        view.load(url, displayIdentity: "old-song")
        await fulfillment(of: [entered], timeout: 2)
        view.load(nil, displayIdentity: "new-song")
        await fulfillment(of: [cancellation], timeout: 2)
        let ready = expectation(description: "new binding displayed")
        view.onImage = { _, _ in ready.fulfill() }
        view.load(url, displayIdentity: "new-song")
        XCTAssertNil(view.image)
        var requests = await network.log(); XCTAssertEqual(requests, [url])
        await gate.release()
        await fulfillment(of: [ready], timeout: 2)
        requests = await network.log(); XCTAssertEqual(requests, [url, url])
        XCTAssertEqual(bytes.statistics().writes, [url], "The old binding never saves its late bytes")
        await images.close()
    }

    func testDeterministicFailureDoesNotRetryOnRebindingAndMissingSameSongURLRetainsPixels() async throws {
        let bytes = ArtworkTestBytes(), network = ArtworkTestNetwork(), images = loader(bytes, network)
        await network.set(url, data: png())
        let view = ArtworkView(size: 48, images: images)
        let ready = expectation(description: "original ready")
        view.onImage = { _, _ in ready.fulfill() }
        view.load(url, displayIdentity: "song")
        await fulfillment(of: [ready], timeout: 2)
        let original = try XCTUnwrap(view.image), broken = URL(string: "https://artwork.invalid/broken")!
        await network.set(broken, data: Data("broken".utf8))
        let failed = expectation(description: "deterministic decode failure")
        view.onFailure = { _ in failed.fulfill() }
        view.load(broken, displayIdentity: "song")
        await fulfillment(of: [failed], timeout: 2)
        view.retryFailedLoad(); view.load(broken, displayIdentity: "song")
        let requests = await network.log(); XCTAssertEqual(requests, [url, broken])
        XCTAssertTrue(view.image === original)
        view.load(nil, displayIdentity: "song")
        XCTAssertTrue(view.image === original, "Exhausting optional artwork cannot erase this song's displayed success")
        view.load(nil, displayIdentity: "different-song")
        XCTAssertNil(view.image)
        await images.close()
    }
}
#endif
