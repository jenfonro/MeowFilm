#if os(iOS) || os(macOS)
import XCTest
import AVFoundation
import MeowContracts
import MeowPlayback
#if os(iOS)
import UIKit
#else
import AppKit
#endif
@testable import MeowPlaybackVideo

@MainActor
final class PlaybackVideoCandidateDecoderTests: XCTestCase {
    private let url = URL(string: "https://media.invalid/fixture")!
    private let headers = ["Cookie": "fixture=one", "X-Fixture": "authorized"]
    private let keys = ["000102030405060708090a0b0c0d0e0f": Data(16..<32)]

    private func prepare(_ candidate: PlaybackVideoCandidateDecoder, position: Double = 12) async throws {
        try await candidate.prepare(url: url, format: "dash", clearKeys: keys,
                                    position: position, headers: headers)
    }

    func testUnsupportedPreparationReplacesOnlyAfterDrainAndPreservesAllInput() async throws {
        let first = CandidateTestDecoder(engine: .aether)
        let second = CandidateTestDecoder(engine: .ksPlayer)
        first.prepareError = PlaybackVideoError.unsupportedFormat
        var factories = 0
        let candidate = PlaybackVideoCandidateDecoder(decoder: first) {
            XCTAssertTrue(first.closed)
            factories += 1
            return second
        }
        let view = candidate.presentationView
        candidate.setVideoGravity(.resizeAspectFill)
        try await prepare(candidate)
        XCTAssertEqual(factories, 1)
        XCTAssertEqual(first.closeCount, 1)
        XCTAssertEqual(second.inputs, first.inputs)
        XCTAssertEqual(second.inputs.first?.url, url)
        XCTAssertEqual(second.inputs.first?.headers, headers)
        XCTAssertEqual(second.inputs.first?.keys, keys)
        XCTAssertEqual(second.inputs.first?.format, "dash")
        XCTAssertEqual(second.inputs.first?.position, 12)
        XCTAssertEqual(candidate.engine, .ksPlayer)
        XCTAssertTrue(candidate.presentationView === view)
        XCTAssertEqual(second.display.gravity, .resizeAspectFill)
        XCTAssertTrue(second.display.presentationView.superview === view)
        XCTAssertNil(first.display.presentationView.superview)
        await candidate.close()
        XCTAssertEqual(first.closeCount, 1)
        XCTAssertEqual(second.closeCount, 1)
    }

    func testUnsupportedReplacementIsNeverRetried() async throws {
        let first = CandidateTestDecoder(engine: .aether)
        let second = CandidateTestDecoder(engine: .ksPlayer)
        first.prepareError = PlaybackVideoError.unsupportedFormat
        second.prepareError = PlaybackVideoError.unsupportedFormat
        var factories = 0
        let candidate = PlaybackVideoCandidateDecoder(decoder: first) { factories += 1; return second }
        do { try await prepare(candidate); XCTFail("The replacement is also unsupported") }
        catch { XCTAssertEqual(error as? PlaybackVideoError, .unsupportedFormat) }
        XCTAssertEqual(factories, 1)
        XCTAssertEqual(second.inputs.count, 1)
        await candidate.close()
    }

    func testSeekabilityFollowsTheActualDecoderAndIsRevokedOnCancellation() async throws {
        let first = CandidateTestDecoder(engine: .aether)
        let second = CandidateTestDecoder(engine: .ksPlayer)
        first.seekable = false
        first.prepareError = PlaybackVideoError.unsupportedFormat
        let candidate = PlaybackVideoCandidateDecoder(decoder: first) { second }
        XCTAssertFalse(candidate.seekable)
        try await prepare(candidate)
        XCTAssertTrue(candidate.seekable)
        second.seekable = false
        XCTAssertFalse(candidate.seekable)
        second.seekable = true
        candidate.cancel()
        XCTAssertFalse(candidate.seekable)
        await candidate.close()
    }

    func testInputFailureAndCancellationNeverSelectFallback() async throws {
        for error in [PlaybackVideoError.inputFailed, .cancelled, .timedOut] {
            let first = CandidateTestDecoder(engine: .aether)
            first.prepareError = error
            var factories = 0
            let candidate = PlaybackVideoCandidateDecoder(decoder: first) {
                factories += 1
                return CandidateTestDecoder(engine: .ksPlayer)
            }
            do { try await prepare(candidate); XCTFail("The original failure must propagate") }
            catch { XCTAssertEqual(error as? PlaybackVideoError, first.prepareError as? PlaybackVideoError) }
            XCTAssertEqual(factories, 0)
            XCTAssertEqual(candidate.engine, .aether)
            await candidate.close()
        }
    }

    func testUnsupportedAlignmentUsesLatestPositionOnTheSameInput() async throws {
        let first = CandidateTestDecoder(engine: .aether)
        let second = CandidateTestDecoder(engine: .ksPlayer)
        first.alignError = PlaybackVideoError.unsupportedFormat
        let candidate = PlaybackVideoCandidateDecoder(decoder: first) { second }
        try await prepare(candidate)
        try await candidate.align(to: 27.5)
        XCTAssertEqual(second.inputs.first?.position, 27.5)
        XCTAssertEqual(second.inputs.first?.headers, headers)
        XCTAssertEqual(second.inputs.first?.keys, keys)
        XCTAssertEqual(first.closeCount, 1)
        XCTAssertEqual(candidate.position, 27.5)
        await candidate.close()
    }

    func testUnsupportedFirstFramePreparesAndRendersTheReplacement() async throws {
        let first = CandidateTestDecoder(engine: .aether)
        let second = CandidateTestDecoder(engine: .ksPlayer)
        first.frameError = PlaybackVideoError.unsupportedFormat
        let candidate = PlaybackVideoCandidateDecoder(decoder: first) { second }
        try await prepare(candidate)
        try await candidate.align(to: 18)
        try await candidate.renderFirstVideoFrame()
        XCTAssertEqual(first.frames, 1)
        XCTAssertEqual(second.frames, 1)
        XCTAssertEqual(second.inputs.first?.position, 18)
        XCTAssertEqual(second.inputs.first?.url, url)
        XCTAssertEqual(candidate.engine, .ksPlayer)
        await candidate.close()
    }

    func testCancelDuringDrainDoesNotCreateAReplacement() async throws {
        let first = CandidateTestDecoder(engine: .aether)
        first.prepareError = PlaybackVideoError.unsupportedFormat
        let gate = CandidateDrainGate(entered: expectation(description: "Real close has started"))
        first.drain = gate
        var factories = 0
        let candidate = PlaybackVideoCandidateDecoder(decoder: first) {
            factories += 1
            return CandidateTestDecoder(engine: .ksPlayer)
        }
        let work = Task { try await prepare(candidate) }
        await fulfillment(of: [gate.entered], timeout: 2)
        candidate.cancel()
        gate.open()
        do { try await work.value; XCTFail("A cancelled candidate cannot reopen") }
        catch { XCTAssertEqual(error as? PlaybackVideoError, .cancelled) }
        XCTAssertEqual(factories, 0)
        XCTAssertTrue(first.closed)
        await candidate.close()
    }

    func testTaskCancellationDuringDrainWaitsForRetirementWithoutFallback() async throws {
        let first = CandidateTestDecoder(engine: .aether)
        first.prepareError = PlaybackVideoError.unsupportedFormat
        let gate = CandidateDrainGate(entered: expectation(description: "Close entered"))
        first.drain = gate
        var factories = 0
        let candidate = PlaybackVideoCandidateDecoder(decoder: first) {
            factories += 1
            return CandidateTestDecoder(engine: .ksPlayer)
        }
        let work = Task { try await prepare(candidate) }
        await fulfillment(of: [gate.entered], timeout: 2)
        work.cancel()
        XCTAssertFalse(first.closed)
        XCTAssertEqual(factories, 0)
        gate.open()
        do { try await work.value; XCTFail("Task cancellation must propagate") }
        catch { XCTAssertTrue(error is CancellationError) }
        XCTAssertTrue(first.closed)
        XCTAssertEqual(factories, 0)
        await candidate.close()
    }

    func testCancelledCandidateDoesNotAdmitAnInput() async throws {
        let first = CandidateTestDecoder(engine: .aether)
        let candidate = PlaybackVideoCandidateDecoder(decoder: first) {
            XCTFail("Cancellation cannot fall back")
            return CandidateTestDecoder(engine: .ksPlayer)
        }
        candidate.cancel()
        do { try await prepare(candidate); XCTFail("The candidate was already cancelled") }
        catch { XCTAssertEqual(error as? PlaybackVideoError, .cancelled) }
        XCTAssertTrue(first.inputs.isEmpty)
        await candidate.close()
    }

    func testCancelledCandidateOrTaskDoesNotEnterFirstFrameRendering() async throws {
        for cancelTask in [false, true] {
            let first = CandidateTestDecoder(engine: .aether)
            var factories = 0
            let candidate = PlaybackVideoCandidateDecoder(decoder: first) {
                factories += 1
                return CandidateTestDecoder(engine: .ksPlayer)
            }
            try await prepare(candidate)
            if cancelTask {
                let gate = CandidateDrainGate(entered: expectation(description: "Before first frame"))
                let work = Task {
                    await gate.wait()
                    try await candidate.renderFirstVideoFrame()
                }
                await fulfillment(of: [gate.entered], timeout: 2)
                work.cancel()
                gate.open()
                do { try await work.value; XCTFail("Task cancellation must prevent rendering") }
                catch { XCTAssertTrue(error is CancellationError) }
            } else {
                candidate.cancel()
                do { try await candidate.renderFirstVideoFrame(); XCTFail("A cancelled candidate cannot render") }
                catch { XCTAssertEqual(error as? PlaybackVideoError, .cancelled) }
            }
            XCTAssertEqual(first.frames, 0)
            XCTAssertEqual(factories, 0)
            await candidate.close()
        }
    }

    func testCommittedCandidateNeverReplacesAnUnsupportedDecoder() async throws {
        let first = CandidateTestDecoder(engine: .aether)
        var factories = 0
        let candidate = PlaybackVideoCandidateDecoder(decoder: first) {
            factories += 1
            return CandidateTestDecoder(engine: .ksPlayer)
        }
        try await prepare(candidate)
        try await candidate.renderFirstVideoFrame()
        candidate.commit(rate: 1.5, volume: 0.25)
        first.frameError = PlaybackVideoError.unsupportedFormat
        do { try await candidate.renderFirstVideoFrame(); XCTFail("Post-commit failure must propagate") }
        catch { XCTAssertEqual(error as? PlaybackVideoError, .unsupportedFormat) }
        XCTAssertEqual(factories, 0)
        XCTAssertEqual(candidate.engine, .aether)
        XCTAssertEqual(candidate.rate, 1.5)
        await candidate.close()
    }

    func testRetiredDecoderCallbacksCannotPublishForItsReplacement() async throws {
        let first = CandidateTestDecoder(engine: .aether)
        let second = CandidateTestDecoder(engine: .ksPlayer)
        first.prepareError = PlaybackVideoError.unsupportedFormat
        let candidate = PlaybackVideoCandidateDecoder(decoder: first) { second }
        let oldFailure = first.onFailure, oldProgress = first.onProgress
        let oldRate = first.onTransportRate, oldIntent = first.onPlaybackIntent
        let oldEnd = first.onEnd, oldLayer = first.display.onDisplayLayerChanged
        var events = [String]()
        candidate.onFailure = { _ in events.append("failure") }
        candidate.onProgress = { _ in events.append("progress") }
        candidate.onTransportRate = { _ in events.append("rate") }
        candidate.onPlaybackIntent = { _ in events.append("intent") }
        candidate.onEnd = { events.append("end") }
        candidate.onDisplayLayerChanged = { events.append("layer") }
        try await prepare(candidate)
        oldFailure?(PlaybackVideoError.outputFailed); oldProgress?(99)
        oldRate?(0); oldIntent?(false); oldEnd?(); oldLayer?()
        XCTAssertTrue(events.isEmpty)
        second.onProgress?(3)
        XCTAssertEqual(events, ["progress"])
        await candidate.close()
        second.onPlaybackIntent?(true); second.onEnd?()
        XCTAssertEqual(events, ["progress"])
    }
}

@MainActor
private final class CandidateDrainGate {
    let entered: XCTestExpectation
    private var continuation: CheckedContinuation<Void, Never>?
    init(entered: XCTestExpectation) { self.entered = entered }
    func wait() async {
        await withCheckedContinuation { continuation in
            self.continuation = continuation
            entered.fulfill()
        }
    }
    func open() { continuation?.resume(); continuation = nil }
}

@MainActor
private final class CandidateTestDecoder: PlaybackVideoDecoding {
    struct Input: Equatable {
        let url: URL
        let format: String?
        let keys: [String: Data]
        let position: Double
        let headers: [String: String]
    }
    let engine: PlaybackVideoEngine
    let display = CandidateTestOutput()
    var output: any PlaybackVideoOutput { display }
    var position: Double = 0
    var duration: Double? { 90 }
    var seekable = true
    var onFailure: (@MainActor (Error) -> Void)?
    var onProgress: (@MainActor (Double) -> Void)?
    var onTransportRate: (@MainActor (Float) -> Void)?
    var onPlaybackIntent: (@MainActor (Bool) -> Void)?
    var onEnd: (@MainActor () -> Void)?
    var prepareError: Error?, alignError: Error?, frameError: Error?
    var inputs = [Input]()
    var frames = 0
    var closeCount = 0
    var closed = false
    var drain: CandidateDrainGate?
    private var closing: Task<Void, Never>?
    init(engine: PlaybackVideoEngine) { self.engine = engine }
    func prepare(url: URL, format: String?, clearKeys: [String: Data], position: Double,
                 headers: [String: String]) async throws {
        inputs.append(.init(url: url, format: format, keys: clearKeys, position: position, headers: headers))
        if let prepareError { throw prepareError }
        self.position = position
    }
    func align(to position: Double) async throws {
        if let alignError { throw alignError }
        self.position = position
    }
    func renderFirstVideoFrame() async throws {
        frames += 1
        if let frameError { throw frameError }
    }
    func validateForCommit() throws {}
    func commit(rate: Float, volume: Float) { display.rate = rate; display.volume = volume }
    func setRate(_ rate: Float) { display.rate = rate }
    func seek(to position: Double) async throws -> Double { self.position = position; return position }
    func cancel() { display.rate = 0 }
    func close() async {
        if let closing { await closing.value; return }
        closeCount += 1
        cancel()
        let closing = Task { [self] in await drain?.wait(); closed = true }
        self.closing = closing
        await closing.value
    }
}

@MainActor
private final class CandidateTestOutput: PlaybackVideoOutput {
    var volume: Float = 0
    var rate: Float = 0
    var displayLayer: AVSampleBufferDisplayLayer? { nil }
    var onDisplayLayerChanged: (() -> Void)?
    var gravity: AVLayerVideoGravity = .resizeAspect
    #if os(iOS)
    let presentationView = UIView()
    #else
    let presentationView = NSView()
    #endif
    func setVideoGravity(_ gravity: AVLayerVideoGravity) { self.gravity = gravity }
}
#endif
