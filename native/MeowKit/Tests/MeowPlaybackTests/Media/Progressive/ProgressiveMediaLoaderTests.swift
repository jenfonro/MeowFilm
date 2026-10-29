import Foundation
import XCTest
import MeowContracts
@testable import MeowPlayback
#if canImport(AVFoundation)
import AVFoundation
#endif

/// Real URLSession delegate plumbing, deterministic URLProtocol bytes. No
/// internet, account, localhost server, Node, real media or decoder claim.
final class ProgressiveMediaLoaderTests: XCTestCase {
    private func configuration() -> URLSessionConfiguration {
        let value = URLSessionConfiguration.ephemeral
        value.protocolClasses = [PlaybackFixtureProtocol.self]
        return value
    }

    private func plan(_ url: URL) throws -> PlaybackNetworkPlan {
        try PlaybackNetworkPlan(resource: PlaybackResource(
            id: "synthetic", url: url, title: "测试", headers: ["Referer": "https://page.invalid/",
                                                               "Cookie": "sid=synthetic",
                                                               "Authorization": "Bearer synthetic"]),
                                options: .init(format: .progressive))
    }

    private func makeLimits() -> PlaybackNetworkLimits {
        var value = PlaybackNetworkLimits()
        value.windowBytes = 4096; value.deliveryBytes = 1024
        value.tasksPerHost = 1
        return value
    }

    func testRangeWindowsStreamOnlyRequestedBytesWithNativeHeadersAndValidator() async throws {
        let body = Data((0..<20000).map { UInt8($0 % 251) })
        let fixture = PlaybackHTTPFixture(body: body)
        defer { fixture.close() }
        let finished = expectation(description: "requested range")
        let sink = TestProgressiveSink(range: try .init(requestedOffset: 500, currentOffset: 600,
                                                        requestedLength: 10000, toEnd: false),
                                       finished: finished)
        let budget = PlaybackNetworkBudget(limits: makeLimits())
        let loader = ProgressiveMediaLoader(plan: try plan(fixture.url), budget: budget,
                                            configuration: configuration()) { _ in XCTFail("unexpected network failure") }
        defer { loader.invalidate() }
        loader.enqueue(sink)
        await fulfillment(of: [finished], timeout: 5)
        XCTAssertNil(sink.result.error)
        XCTAssertEqual(sink.result.bytes, body.subdata(in: 600..<10500))
        XCTAssertLessThanOrEqual(sink.result.largestChunk, 1024)
        XCTAssertEqual(sink.result.finishes, 1)
        XCTAssertEqual(fixture.requests.map { $0.value(forHTTPHeaderField: "Range") },
                       ["bytes=600-4695", "bytes=4696-8791", "bytes=8792-10499"])
        XCTAssertNil(fixture.requests.first?.value(forHTTPHeaderField: "If-Range"))
        XCTAssertEqual(fixture.requests.last?.value(forHTTPHeaderField: "If-Range"), "\"fixture-v1\"")
        for request in fixture.requests {
            XCTAssertEqual(request.value(forHTTPHeaderField: "Cookie"), "sid=synthetic")
            XCTAssertEqual(request.value(forHTTPHeaderField: "Authorization"), "Bearer synthetic")
            XCTAssertEqual(request.value(forHTTPHeaderField: "Accept-Encoding"), "identity")
            XCTAssertFalse(request.httpShouldHandleCookies)
        }
        XCTAssertEqual(budget.usage.activeTasks, 0)
    }

    func testToEOFUsesBoundedWindowsRatherThanOpenEndedHTTPRange() async throws {
        let fixture = PlaybackHTTPFixture(body: Data(repeating: 23, count: 9300))
        defer { fixture.close() }
        let finished = expectation(description: "EOF")
        let sink = TestProgressiveSink(range: try .init(requestedOffset: 0, currentOffset: 0,
                                                        requestedLength: 1, toEnd: true), finished: finished)
        let loader = ProgressiveMediaLoader(plan: try plan(fixture.url),
                                            budget: PlaybackNetworkBudget(limits: makeLimits()),
                                            configuration: configuration()) { _ in XCTFail("unexpected failure") }
        defer { loader.invalidate() }
        loader.enqueue(sink)
        await fulfillment(of: [finished], timeout: 5)
        XCTAssertEqual(sink.result.bytes.count, 9300)
        XCTAssertEqual(fixture.requests.map { $0.value(forHTTPHeaderField: "Range") },
                       ["bytes=0-4095", "bytes=4096-8191", "bytes=8192-9299"])
        XCTAssertEqual(sink.result.information?.length, 9300)
    }

    func testMetadataRequestDoesNotDownloadWholeFile() async throws {
        let fixture = PlaybackHTTPFixture(body: Data(repeating: 1, count: 20000))
        defer { fixture.close() }
        let finished = expectation(description: "metadata")
        let sink = TestProgressiveSink(range: nil, finished: finished)
        let loader = ProgressiveMediaLoader(plan: try plan(fixture.url), budget: PlaybackNetworkBudget(),
                                            configuration: configuration()) { _ in XCTFail("unexpected failure") }
        defer { loader.invalidate() }
        loader.enqueue(sink)
        await fulfillment(of: [finished], timeout: 5)
        XCTAssertEqual(sink.result.information?.length, 20000)
        XCTAssertTrue(sink.result.bytes.isEmpty)
        XCTAssertEqual(fixture.requests.count, 1)
        XCTAssertEqual(fixture.requests.first?.value(forHTTPHeaderField: "Range"), "bytes=0-1")
    }

    func testCancellationFencesDeliveryButDoesNotReleaseTaskBeforeDidComplete() async throws {
        let started = expectation(description: "task started")
        let fixture = PlaybackHTTPFixture(body: Data(), mode: .hold, onStart: { started.fulfill() })
        defer { fixture.close() }
        let neverFinished = expectation(description: "cancelled sink is not completed twice")
        neverFinished.isInverted = true
        let sink = TestProgressiveSink(range: try .init(requestedOffset: 0, currentOffset: 0,
                                                        requestedLength: 1000, toEnd: false), finished: neverFinished)
        var limits = makeLimits(); limits.activeTasks = 1
        let budget = PlaybackNetworkBudget(limits: limits)
        let loader = ProgressiveMediaLoader(plan: try plan(fixture.url), budget: budget,
                                            configuration: configuration()) { _ in XCTFail("cancellation is not failure") }
        defer { loader.invalidate() }
        loader.enqueue(sink)
        await fulfillment(of: [started], timeout: 5)
        let settled = expectation(description: "actual URLSession completion")
        let observation = budget.observe {
            if budget.usage.activeTasks == 0 { settled.fulfill() }
        }
        defer { budget.unobserve(observation) }
        loader.performSynchronized {
            loader.cancel(sink)
            // didComplete cannot run midway through this serial operation.
            XCTAssertEqual(budget.usage.activeTasks, 1)
            XCTAssertNil(budget.acquire(host: "successor.invalid", bytes: 1))
        }
        await fulfillment(of: [settled], timeout: 5)
        await fulfillment(of: [neverFinished], timeout: 0.05)
        XCTAssertTrue(sink.result.bytes.isEmpty)
        XCTAssertEqual(budget.usage.activeTasks, 0)
    }

    func testReentrantRevocationStopsFurtherChunksInSameDelegateCallback() async throws {
        let fixture = PlaybackHTTPFixture(body: Data(repeating: 31, count: 4096))
        defer { fixture.close() }
        let revoked = expectation(description: "revoked during first byte delivery")
        let sink = TestProgressiveSink(range: try .init(requestedOffset: 100, currentOffset: 100,
                                                        requestedLength: 3000, toEnd: false), finished: nil)
        let budget = PlaybackNetworkBudget(limits: makeLimits())
        let loader = ProgressiveMediaLoader(plan: try plan(fixture.url), budget: budget,
                                            configuration: configuration()) { _ in XCTFail("revocation is not failure") }
        defer { loader.invalidate() }
        sink.onData = { [weak loader] in
            loader?.invalidate()
            revoked.fulfill()
        }
        loader.enqueue(sink)
        await fulfillment(of: [revoked], timeout: 5)
        loader.performSynchronized {
            XCTAssertEqual(sink.result.deliveries, 1)
            XCTAssertLessThanOrEqual(sink.result.bytes.count, 1024)
            XCTAssertEqual(sink.result.finishes, 0)
        }
    }

    func testBudgetWaitIsBoundedAndCannotStartAHiddenUnaccountedTask() async throws {
        var limits = makeLimits(); limits.activeTasks = 1; limits.budgetWaitSeconds = 0.05
        let budget = PlaybackNetworkBudget(limits: limits)
        let oldTask = try XCTUnwrap(budget.acquire(host: "retired.invalid", bytes: 4096))
        defer { oldTask.complete() }
        let fixture = PlaybackHTTPFixture(body: Data(repeating: 1, count: 1000))
        defer { fixture.close() }
        let failed = expectation(description: "budget deadline")
        let sink = TestProgressiveSink(range: try .init(requestedOffset: 0, currentOffset: 0,
                                                        requestedLength: 100, toEnd: false), finished: failed)
        let loader = ProgressiveMediaLoader(plan: try plan(fixture.url), budget: budget,
                                            configuration: configuration()) { error in
            XCTAssertEqual(error, .budgetWaitTimedOut)
        }
        defer { loader.invalidate() }
        loader.enqueue(sink)
        await fulfillment(of: [failed], timeout: 2)
        XCTAssertEqual(sink.result.error, .budgetWaitTimedOut)
        XCTAssertTrue(fixture.requests.isEmpty)
        XCTAssertEqual(budget.usage.activeTasks, 1)
    }

    func testTruncatedResponseFailsInsteadOfReportingEOFSuccess() async throws {
        let fixture = PlaybackHTTPFixture(body: Data(repeating: 19, count: 1000), mode: .truncate)
        defer { fixture.close() }
        let finished = expectation(description: "truncated")
        let sink = TestProgressiveSink(range: try .init(requestedOffset: 0, currentOffset: 0,
                                                        requestedLength: 1000, toEnd: false), finished: finished)
        let loader = ProgressiveMediaLoader(plan: try plan(fixture.url), budget: PlaybackNetworkBudget(),
                                            configuration: configuration()) { _ in }
        defer { loader.invalidate() }
        loader.enqueue(sink)
        await fulfillment(of: [finished], timeout: 5)
        XCTAssertNotNil(sink.result.error)
        XCTAssertEqual(sink.result.finishes, 1)
    }

    func testDisguisedHLSBodyIsNotFedToProgressiveDecoder() async throws {
        let fixture = PlaybackHTTPFixture(body: Data("#EXTM3U\n#EXT-X-TARGETDURATION:6\n".utf8))
        defer { fixture.close() }
        let finished = expectation(description: "HLS rejected")
        let sink = TestProgressiveSink(range: try .init(requestedOffset: 0, currentOffset: 0,
                                                        requestedLength: 200, toEnd: true), finished: finished)
        let loader = ProgressiveMediaLoader(plan: try plan(fixture.url), budget: PlaybackNetworkBudget(),
                                            configuration: configuration()) { _ in }
        defer { loader.invalidate() }
        loader.enqueue(sink)
        await fulfillment(of: [finished], timeout: 5)
        XCTAssertEqual(sink.result.error, .hlsRequiresExplicitFormat)
        XCTAssertTrue(sink.result.bytes.isEmpty)
        XCTAssertEqual(fixture.requests.count, 1)
    }

    #if canImport(AVFoundation)
    func testPublicAVAssetLoaderParsesSyntheticWAVOverControlledURLSession() async throws {
        let fixture = PlaybackHTTPFixture(body: Self.wave(), pathExtension: "wav", mime: "audio/wav")
        defer { fixture.close() }
        let budget = PlaybackNetworkBudget(limits: makeLimits())
        let binding = try NativePlaybackAsset(plan: plan(fixture.url), budget: budget,
                                               configuration: configuration()) { _ in }
        defer { binding.invalidate() }
        XCTAssertEqual(binding.asset.url.scheme, "meow-progressive")
        XCTAssertFalse(binding.asset.url.absoluteString.contains(fixture.url.host!))
        let playable = try await binding.asset.load(.isPlayable)
        let duration = try await binding.asset.load(.duration)
        XCTAssertTrue(playable)
        XCTAssertEqual(duration.seconds, 0.25, accuracy: 0.01)
        XCTAssertFalse(fixture.requests.isEmpty)
        XCTAssertTrue(fixture.requests.allSatisfy { $0.value(forHTTPHeaderField: "Authorization") == "Bearer synthetic" })
    }

    private static func wave() -> Data {
        // 1/4 second of PCM silence; generated fixture, not a downloaded asset.
        let size = 12000 * 2
        var bytes = Data()
        func text(_ value: String) { bytes.append(contentsOf: value.utf8) }
        func u16(_ value: UInt16) {
            bytes.append(UInt8(truncatingIfNeeded: value)); bytes.append(UInt8(truncatingIfNeeded: value >> 8))
        }
        func u32(_ value: UInt32) {
            for shift in stride(from: 0, through: 24, by: 8) { bytes.append(UInt8(truncatingIfNeeded: value >> shift)) }
        }
        text("RIFF"); u32(UInt32(36 + size)); text("WAVEfmt "); u32(16)
        u16(1); u16(1); u32(48000); u32(96000); u16(2); u16(16)
        text("data"); u32(UInt32(size)); bytes.append(Data(repeating: 0, count: size))
        return bytes
    }
    #endif
}

private final class TestProgressiveSink: ProgressiveMediaSink, @unchecked Sendable {
    struct Result {
        var bytes = Data()
        var information: ProgressiveContentInformation?
        var error: PlaybackNetworkError?
        var finishes = 0
        var deliveries = 0
        var largestChunk = 0
    }
    let range: ProgressiveReadRange?
    var onData: (() -> Void)?
    private let lock = NSLock()
    private var state = Result()
    private let finished: XCTestExpectation?
    var result: Result { lock.lock(); defer { lock.unlock() }; return state }
    init(range: ProgressiveReadRange?, finished: XCTestExpectation?) { self.range = range; self.finished = finished }
    func receive(_ information: ProgressiveContentInformation) {
        lock.lock(); state.information = information; lock.unlock()
    }
    func receive(_ data: Data) {
        lock.lock()
        state.bytes.append(data); state.deliveries += 1
        state.largestChunk = max(state.largestChunk, data.count)
        lock.unlock()
        onData?()
    }
    func finish(_ error: PlaybackNetworkError?) {
        lock.lock(); state.error = error; state.finishes += 1; lock.unlock()
        finished?.fulfill()
    }
}

private final class PlaybackHTTPFixture: @unchecked Sendable {
    enum Mode: Equatable { case normal, hold, truncate }
    let url: URL
    private let body: Data
    private let mode: Mode
    private let mime: String
    private let onStart: (() -> Void)?
    private let lock = NSLock()
    private var seen: [URLRequest] = []
    var requests: [URLRequest] { lock.lock(); defer { lock.unlock() }; return seen }

    init(body: Data, mode: Mode = .normal, pathExtension: String = "mp3",
         mime: String = "audio/mpeg", onStart: (() -> Void)? = nil) {
        self.body = body; self.mode = mode; self.mime = mime; self.onStart = onStart
        url = URL(string: "https://\(UUID().uuidString.lowercased()).fixture.invalid/media.\(pathExtension)")!
        PlaybackFixtureProtocol.register(self)
    }
    func close() { PlaybackFixtureProtocol.remove(url) }

    func respond(to protocolInstance: URLProtocol) {
        let request = protocolInstance.request
        lock.lock(); seen.append(request); lock.unlock()
        onStart?()
        guard mode != .hold else { return }
        guard let raw = request.value(forHTTPHeaderField: "Range"), raw.hasPrefix("bytes=") else {
            protocolInstance.client?.urlProtocol(protocolInstance, didFailWithError: URLError(.badServerResponse)); return
        }
        let parts = raw.dropFirst(6).split(separator: "-")
        guard parts.count == 2, let start = Int(parts[0]), let requestedEnd = Int(parts[1]), start < body.count else {
            protocolInstance.client?.urlProtocol(protocolInstance, didFailWithError: URLError(.badServerResponse)); return
        }
        let end = min(requestedEnd, body.count - 1)
        let response = HTTPURLResponse(url: url, statusCode: 206, httpVersion: "HTTP/1.1", headerFields: [
            "Content-Range": "bytes \(start)-\(end)/\(body.count)", "Content-Length": "\(end - start + 1)",
            "Content-Type": mime, "ETag": "\"fixture-v1\"",
            "Set-Cookie": "ambient=not-adopted; Path=/; Secure"
        ])!
        protocolInstance.client?.urlProtocol(protocolInstance, didReceive: response, cacheStoragePolicy: .notAllowed)
        let bytes = body.subdata(in: start..<(end + 1))
        protocolInstance.client?.urlProtocol(protocolInstance, didLoad: mode == .truncate ? Data(bytes.dropLast()) : bytes)
        protocolInstance.client?.urlProtocolDidFinishLoading(protocolInstance)
    }
}

private final class PlaybackFixtureProtocol: URLProtocol, @unchecked Sendable {
    private static let lock = NSLock()
    private static var fixtures: [URL: PlaybackHTTPFixture] = [:]
    static func register(_ fixture: PlaybackHTTPFixture) {
        lock.lock(); fixtures[fixture.url] = fixture; lock.unlock()
    }
    static func remove(_ url: URL) { lock.lock(); fixtures[url] = nil; lock.unlock() }
    override class func canInit(with request: URLRequest) -> Bool {
        lock.lock(); defer { lock.unlock() }
        return request.url.flatMap { fixtures[$0] } != nil
    }
    override class func canonicalRequest(for request: URLRequest) -> URLRequest { request }
    override func startLoading() {
        Self.lock.lock()
        let fixture = request.url.flatMap { Self.fixtures[$0] }
        Self.lock.unlock()
        fixture?.respond(to: self)
    }
    override func stopLoading() {}
}
