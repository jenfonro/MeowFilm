import Foundation
import XCTest
import MeowAudio
import MeowContracts
@testable import MeowPlayback

/// Actual URLSession callbacks with controlled HTTP responses, not an upstream
/// or audio-device acceptance test.
@MainActor
final class PlaybackHTTPStreamInputTests: XCTestCase {
    func testNonRangeAndUnknownLengthResponsesRemainReadableToEOF() async throws {
        for mode in [AudioStreamFixture.Mode.withoutRanges, .withoutLength] {
            let fixture = AudioStreamFixture(mode: mode)
            defer { fixture.close() }
            let budget = PlaybackNetworkBudget()
            let input = try makeInput(fixture, budget: budget)
            let first = try await Task.detached { try input.read(maximumBytes: 32) }.value
            do {
                _ = try await Task.detached { try input.seek(.offset(0, .current)) }.value
                XCTFail("The actual response does not support seeking")
            } catch { XCTAssertEqual(error as? AudioCoreError, .unsupportedFormat) }
            let tail = try await Task.detached {
                var bytes = Data()
                while true {
                    let next = try input.read(maximumBytes: 128)
                    if next.isEmpty { return bytes }
                    bytes.append(next)
                }
            }.value
            XCTAssertEqual(first + tail, fixture.body)
            try await input.close()
            XCTAssertEqual(budget.usage.activeTasks, 0)
            XCTAssertEqual(budget.usage.reservedPayloadBytes, 0)
        }
    }

    func testCancelledSeekJoinsOldRequestAndResumesAfterRetainedBytes() async throws {
        let stopped = expectation(description: "old HTTP request physically stopping")
        let fixture = AudioStreamFixture(holdFirst: true, stopped: stopped)
        defer { fixture.close() }
        let budget = PlaybackNetworkBudget()
        let input = try makeInput(fixture, budget: budget, beforeSeekRetirement: { $0.cancelReads() })
        let first = try await Task.detached { try input.read(maximumBytes: 32) }.value
        XCTAssertEqual(first, fixture.body.prefix(32))
        let seek = Task.detached { try input.seek(.offset(100, .start)) }
        await fulfillment(of: [stopped], timeout: 3)
        do { _ = try await seek.value; XCTFail("Cancelled seek must not retarget the input") }
        catch is CancellationError {}
        input.resumeReads()
        let finished = expectation(description: "resumed input reached actual EOF")
        let reading = Task.detached {
            defer { finished.fulfill() }
            var bytes = Data()
            while true {
                let next = try input.read(maximumBytes: 128)
                if next.isEmpty { return bytes }
                bytes.append(next)
            }
        }
        fixture.releaseStop()
        await fulfillment(of: [finished], timeout: 3)
        try await input.close()
        let bytes = try await reading.value
        XCTAssertEqual(first + bytes, fixture.body)
        XCTAssertEqual(fixture.ranges, ["bytes=0-", "bytes=64-"])
        XCTAssertEqual(budget.usage.activeTasks, 0)
        XCTAssertEqual(budget.usage.reservedPayloadBytes, 0)
    }

    func testSeekCannotAcceptAChangedHTTPRepresentation() async throws {
        let stopped = expectation(description: "old representation retired")
        let fixture = AudioStreamFixture(holdFirst: true, changed: true, stopped: stopped)
        defer { fixture.close() }
        let input = try makeInput(fixture, budget: PlaybackNetworkBudget())
        _ = try await Task.detached { try input.read(maximumBytes: 32) }.value
        let seek = Task.detached { try input.seek(.offset(128, .start)) }
        await fulfillment(of: [stopped], timeout: 3)
        fixture.releaseStop()
        _ = try await seek.value
        do {
            _ = try await Task.detached { try input.read(maximumBytes: 32) }.value
            XCTFail("A new validator must not replace the owned representation")
        } catch { XCTAssertEqual(error as? PlaybackNetworkError, .resourceChanged) }
        try await input.close()
        XCTAssertEqual(fixture.ranges, ["bytes=0-", "bytes=128-"])
    }

    private func makeInput(_ fixture: AudioStreamFixture,
                           budget: PlaybackNetworkBudget,
                           beforeSeekRetirement: (@Sendable (PlaybackHTTPStreamInput) -> Void)? = nil) throws -> PlaybackHTTPStreamInput {
        let plan = try PlaybackNetworkPlan(resource: .init(id: "stream", url: fixture.url, title: "Stream"))
        let configuration = URLSessionConfiguration.ephemeral
        configuration.protocolClasses = [AudioStreamProtocol.self]
        return PlaybackHTTPStreamInput(plan: plan, budget: budget, configuration: configuration,
                                       beforeSeekRetirement: beforeSeekRetirement)
    }
}

private final class AudioStreamFixture: @unchecked Sendable {
    enum Mode { case ranges, withoutRanges, withoutLength }
    let url = URL(string: "https://\(UUID().uuidString.lowercased()).audio-stream.invalid/media")!
    let body = Data((0..<1_024).map { UInt8($0 % 251) })
    private let mode: Mode
    private let holdFirst: Bool
    private let changed: Bool
    private let stopped: XCTestExpectation?
    private let condition = NSCondition()
    private var released = false
    private var requests: [URLRequest] = []
    var ranges: [String] {
        condition.lock(); defer { condition.unlock() }
        return requests.compactMap { $0.value(forHTTPHeaderField: "Range") }
    }

    init(mode: Mode = .ranges, holdFirst: Bool = false, changed: Bool = false,
         stopped: XCTestExpectation? = nil) {
        self.mode = mode; self.holdFirst = holdFirst; self.changed = changed; self.stopped = stopped
        AudioStreamProtocol.register(self)
    }
    func close() { releaseStop(); AudioStreamProtocol.remove(url) }
    func releaseStop() { condition.lock(); released = true; condition.broadcast(); condition.unlock() }
    func stop(_ index: Int) {
        guard holdFirst, index == 1 else { return }
        condition.lock(); defer { condition.unlock() }
        stopped?.fulfill()
        while !released { condition.wait() }
    }
    func respond(_ target: AudioStreamProtocol) {
        condition.lock(); requests.append(target.request); let index = requests.count; condition.unlock()
        target.index = index
        let range = target.request.value(forHTTPHeaderField: "Range") ?? ""
        let start = mode == .ranges ? Int(range.dropFirst(6).dropLast()) ?? 0 : 0
        var headers = ["Content-Type": "audio/mpeg", "ETag": changed && index > 1 ? "\"v2\"" : "\"v1\""]
        if mode != .withoutLength { headers["Content-Length"] = "\(body.count - start)" }
        if mode == .ranges { headers["Content-Range"] = "bytes \(start)-\(body.count - 1)/\(body.count)" }
        let response = HTTPURLResponse(url: url, statusCode: mode == .ranges ? 206 : 200,
                                       httpVersion: "HTTP/1.1", headerFields: headers)!
        target.client?.urlProtocol(target, didReceive: response, cacheStoragePolicy: .notAllowed)
        let end = holdFirst && index == 1 ? 64 : body.count
        target.client?.urlProtocol(target, didLoad: body.subdata(in: start..<end))
        if !(holdFirst && index == 1) { target.client?.urlProtocolDidFinishLoading(target) }
    }
}

private final class AudioStreamProtocol: URLProtocol, @unchecked Sendable {
    private static let lock = NSLock()
    private static var fixtures: [URL: AudioStreamFixture] = [:]
    private var fixture: AudioStreamFixture?
    var index = 0
    static func register(_ fixture: AudioStreamFixture) {
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
    override func stopLoading() { fixture?.stop(index) }
}
