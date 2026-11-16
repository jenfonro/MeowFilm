import Foundation
import XCTest
import AVFoundation
@testable import MeowAudio

/// Real FFmpeg C ABI, synthetic WAV bytes, no output device or account.
final class AudioDecoderLifecycleTests: XCTestCase {
    func testStereoPCMIsDecodedToEOFInBoundedChunks() async throws {
        let input = AudioDecoderTestInput(frames: 48_000)
        let decoder = AudioDecoder(input: input)
        let info = try await decoder.prepare()
        XCTAssertEqual(info.sampleRate, 48_000)
        XCTAssertEqual(info.channels, 2)
        XCTAssertEqual(try XCTUnwrap(info.duration), 1, accuracy: 1 / 48_000)
        XCTAssertTrue(info.seekable)
        var frames = 0
        while let chunk = try await decoder.read() {
            XCTAssertGreaterThan(chunk.frameCount, 0)
            XCTAssertLessThanOrEqual(chunk.frameCount, 4_096)
            XCTAssertEqual(chunk.position, Double(frames) / 48_000, accuracy: 1 / 48_000)
            XCTAssertTrue(chunk.samples.allSatisfy { $0 == 0 })
            frames += chunk.frameCount
        }
        XCTAssertEqual(frames, 48_000)
        let measured = try await decoder.information()
        XCTAssertEqual(measured.decodedSourceFrames, 48_000)
        XCTAssertEqual(measured.sourceSampleRate, 48_000)
        XCTAssertTrue(measured.isComplete)
        try await decoder.close()
        try await decoder.close()
        XCTAssertEqual(input.closeCount, 1)
    }

    @MainActor
    func testPreparationAndFractionalSeekRemainSilent() async throws {
        let session = AudioPlaybackSession(input: AudioDecoderTestInput(frames: 48_000))
        try await session.prepare()
        XCTAssertFalse(session.hasOutput)
        XCTAssertFalse(session.isPlaying)
        let target = 0.312_567
        let actual = try await session.seek(to: target)
        XCTAssertGreaterThanOrEqual(actual, target - 1 / 48_000)
        XCTAssertLessThanOrEqual(actual, target + 1 / 48_000)
        XCTAssertFalse(session.hasOutput)
        XCTAssertEqual(session.rate, 0)
        try await session.close()
        XCTAssertFalse(session.hasOutput)
    }

    @MainActor
    func testCancellingQueuedReadDoesNotInterruptActivePreparation() async throws {
        let entered = expectation(description: "native prepare entered byte callback")
        let input = AudioDecoderTestInput(frames: 48_000)
        input.blockNextRead(entered: entered, releaseOnCancel: false)
        let decoder = AudioDecoder(input: input)
        let preparing = Task { try await decoder.prepare() }
        await fulfillment(of: [entered], timeout: 3)
        let queued = expectation(description: "read requested behind native preparation")
        let reading = Task {
            queued.fulfill()
            return try await decoder.read()
        }
        await fulfillment(of: [queued], timeout: 3)
        reading.cancel()
        XCTAssertEqual(input.cancellationCount, 0)
        input.releaseRead()
        _ = try await preparing.value
        do { _ = try await reading.value; XCTFail("Cancelled queued read must not run") }
        catch is CancellationError {}
        XCTAssertEqual(input.cancellationCount, 0)
        let chunk = try await decoder.read()
        XCTAssertNotNil(chunk)
        try await decoder.close()
    }

    @MainActor
    func testCloseJoinsNativeCallbackAndPhysicalInputRetirement() async throws {
        let entered = expectation(description: "native callback physically blocked")
        let closing = expectation(description: "input close began")
        let input = AudioDecoderTestInput(frames: 48_000)
        input.blockNextRead(entered: entered, releaseOnCancel: false)
        let closeGate = AudioDecoderCloseGate()
        input.onClose = { closing.fulfill(); await closeGate.wait() }
        let decoder = AudioDecoder(input: input)
        let preparing = Task { try await decoder.prepare() }
        await fulfillment(of: [entered], timeout: 3)
        var finished = false
        let retirement = Task { try await decoder.close(); finished = true }
        await fulfillment(of: [closing], timeout: 3)
        XCTAssertFalse(finished)
        input.releaseRead()
        do { _ = try await preparing.value; XCTFail("Retired preparation must not become ready") }
        catch is CancellationError {}
        XCTAssertFalse(finished, "Native completion alone is not physical input retirement")
        await closeGate.release()
        try await retirement.value
        XCTAssertTrue(finished)
        XCTAssertEqual(input.closeCount, 1)
        do { _ = try await decoder.read(); XCTFail("Closed decoder must not read again") }
        catch is CancellationError {}
    }

    @MainActor
    func testTemporaryReadCancellationPreservesPCMUntilActualEOF() async throws {
        let total = 25 * 48_000
        let input = AudioDecoderTestInput(frames: total)
        let decoder = AudioDecoder(input: input)
        _ = try await decoder.prepare()
        let entered = expectation(description: "read beyond native probe buffers")
        input.blockNextRead(entered: entered, releaseOnCancel: true)
        var frames = 0
        let reading = Task {
            while let chunk = try await decoder.read() { frames += chunk.frameCount }
        }
        await fulfillment(of: [entered], timeout: 5)
        reading.cancel()
        do { try await reading.value; XCTFail("Blocked read must report cancellation") }
        catch is CancellationError {}
        while let chunk = try await decoder.read() { frames += chunk.frameCount }
        XCTAssertEqual(frames, total)
        try await decoder.close()
    }

    func testFailedInputCloseIsStickyAndCannotReopenTheDecoder() async throws {
        let input = AudioDecoderTestInput(frames: 48_000)
        input.onClose = { throw AudioCoreError.inputFailed }
        let decoder = AudioDecoder(input: input)
        _ = try await decoder.prepare()
        for _ in 0..<2 {
            do { try await decoder.close(); XCTFail("Physical close failure must remain observable") }
            catch { XCTAssertEqual(error as? AudioCoreError, .inputFailed) }
        }
        XCTAssertEqual(input.closeCount, 1)
        do { _ = try await decoder.prepare(); XCTFail("Failed retirement cannot reopen the owner") }
        catch is CancellationError {}
    }

    @MainActor
    func testCancellationAfterPCMConsumptionRetainsOneChunkUntilReadOrSeek() async throws {
        for seekAfterCancellation in [false, true] {
            let entered = expectation(description: "PCM consumed before Swift delivery")
            let gate = AudioPCMDeliveryGate(entered: entered)
            let decoder = AudioDecoder(input: AudioDecoderTestInput(frames: 48_000),
                                       beforePCMDelivery: { gate.waitOnce() })
            _ = try await decoder.prepare()
            let reading = Task { try await decoder.read() }
            await fulfillment(of: [entered], timeout: 3)
            reading.cancel()
            gate.release()
            do { _ = try await reading.value; XCTFail("Cancellation won the consumed-PCM handoff") }
            catch is CancellationError {}
            let start = seekAfterCancellation ? 24_000 : 0
            if seekAfterCancellation { try await decoder.seek(to: 0.5) }
            var frames = start
            while let chunk = try await decoder.read() {
                XCTAssertEqual(chunk.position, Double(frames) / 48_000, accuracy: 1 / 48_000)
                frames += chunk.frameCount
            }
            XCTAssertEqual(frames, 48_000)
            try await decoder.close()
        }
    }
}

/// A controlled output completion boundary with the real native decoder.
/// These assertions do not substitute for actual AVAudioEngine delivery.
@MainActor
final class AudioPlaybackOutputLifecycleTests: XCTestCase {
    func testRebuildAfterRenderedEOFDeliversOneEndWithoutRestartingEmptyOutput() async throws {
        let output = AudioLifecycleOutput()
        var creations = 0, ends = 0
        let session = AudioPlaybackSession(input: AudioDecoderTestInput(frames: 4_800), makeOutput: {
            creations += 1
            return output
        })
        session.onEnd = { ends += 1 }
        try await session.prepare()
        try session.play(rate: 1)
        await waitUntil { session.hasDecodedToEnd }
        XCTAssertEqual(output.scheduled.reduce(0) { $0 + $1.chunk.frameCount }, 4_800)
        XCTAssertEqual(ends, 0, "Decoded EOF is not rendered EOF")
        output.renderedFrames = 4_800
        session.rebuildOutput()
        XCTAssertEqual(session.position, 0.1, accuracy: 1 / 48_000)
        XCTAssertEqual(session.bufferedFrames, 0)
        XCTAssertEqual(ends, 1)
        XCTAssertEqual(session.rate, 0)
        try session.play(rate: 1)
        XCTAssertEqual(creations, 1, "Drained EOF must not create an empty output graph")
        for entry in output.scheduled { entry.complete() }
        XCTAssertEqual(ends, 1, "Retired output completions cannot publish another end")
        try await session.close()
    }

    func testSeekToEndAndRepeatedRebuildCannotTurnExplicitPositionIntoNaturalEnd() async throws {
        let session = AudioPlaybackSession(input: AudioDecoderTestInput(frames: 4_800),
                                           makeOutput: { XCTFail("Seek to EOF is silent"); return AudioLifecycleOutput() })
        var ends = 0
        session.onEnd = { ends += 1 }
        try await session.prepare()
        let position = try await session.seek(to: 0.1)
        XCTAssertEqual(position, 0.1, accuracy: 1 / 48_000)
        session.rebuildOutput()
        try session.play(rate: 1)
        session.rebuildOutput()
        XCTAssertEqual(ends, 0)
        XCTAssertFalse(session.hasOutput)
        try await session.close()
    }

    func testBoundedQueuePauseRatesSeekAndCloseRejectOldOutputCompletions() async throws {
        let output = AudioLifecycleOutput()
        let session = AudioPlaybackSession(input: AudioDecoderTestInput(frames: 10 * 48_000),
                                           makeOutput: { output })
        var ends = 0
        session.onEnd = { ends += 1 }
        try await session.prepare()
        try session.play(rate: 1)
        await waitUntil { output.scheduled.count == 8 }
        XCTAssertLessThanOrEqual(session.bufferedFrames, 8 * 4_096)
        let buffered = session.bufferedFrames
        session.pause()
        XCTAssertEqual(session.rate, 0)
        XCTAssertFalse(session.isPlaying)
        XCTAssertEqual(session.bufferedFrames, buffered)
        try session.play(rate: 0.5)
        try session.play(rate: 3)
        XCTAssertEqual(output.rates, [1, 0.5, 3])
        XCTAssertEqual(session.bufferedFrames, buffered)
        let old = output.scheduled
        let position = try await session.seek(to: 0.312_567)
        for entry in old { entry.complete() }
        XCTAssertEqual(session.position, position, accuracy: 1 / 48_000)
        XCTAssertEqual(session.bufferedFrames, 0)
        XCTAssertEqual(ends, 0)
        try session.play(rate: 1)
        await waitUntil { output.scheduled.count > old.count }
        let closingPosition = session.position
        try await session.close()
        for entry in output.scheduled { entry.complete() }
        XCTAssertFalse(session.hasOutput)
        XCTAssertEqual(session.bufferedFrames, 0)
        XCTAssertEqual(session.position, closingPosition, accuracy: 1 / 48_000)
        XCTAssertEqual(ends, 0)
    }

    private func waitUntil(_ predicate: @escaping @MainActor () -> Bool) async {
        let ready = expectation(for: NSPredicate { _, _ in
            MainActor.assumeIsolated { predicate() }
        }, evaluatedWith: nil)
        await fulfillment(of: [ready], timeout: 5)
    }
}

@MainActor
private final class AudioLifecycleOutput: AudioPCMOutput {
    var volume: Float = 1
    var isPlaying = false
    var isRunning = false
    var renderedFrames: AVAudioFramePosition? = 0
    var initialPosition: Double?
    var renderedPosition: Double? {
        guard let renderedFrames, let initialPosition else { return nil }
        return initialPosition + Double(renderedFrames) / 48_000
    }
    var onConfigurationChange: (@MainActor () -> Void)?
    var rates: [Float] = []
    var scheduled: [(chunk: AudioPCMChunk, complete: @MainActor () -> Void)] = []
    func setRate(_ rate: Float) { rates.append(rate) }
    func play() throws { isRunning = true; isPlaying = true }
    func pause() { isPlaying = false }
    func stop() { isPlaying = false; renderedFrames = 0; initialPosition = nil }
    func schedule(_ chunk: AudioPCMChunk, completion: @escaping @MainActor () -> Void) throws {
        if initialPosition == nil { initialPosition = chunk.position }
        scheduled.append((chunk, completion))
    }
    func close() { isRunning = false; isPlaying = false }
}

final class AudioRenderTimelineTests: XCTestCase {
    func testUnderrunNodeClockGapDoesNotAdvanceMediaOrRelabelNextPCM() {
        var timeline = AudioRenderTimeline()
        let first = UUID(), second = UUID()
        let chunk = AudioPCMChunk(samples: [Float](repeating: 0, count: 4_096 * 2), position: 0)
        XCTAssertEqual(timeline.schedule(chunk, id: first, currentSampleTime: 0), 0)
        XCTAssertEqual(timeline.position(at: 2_048) ?? -1, Double(2_048) / 48_000, accuracy: 1 / 48_000)
        timeline.complete(first)
        XCTAssertEqual(timeline.position(at: 24_000) ?? -1, chunk.endPosition, accuracy: 1 / 48_000)
        let next = AudioPCMChunk(samples: chunk.samples, position: chunk.endPosition)
        XCTAssertEqual(timeline.schedule(next, id: second, currentSampleTime: 24_000), 24_000)
        XCTAssertEqual(timeline.position(at: 24_000) ?? -1, next.position, accuracy: 1 / 48_000)
        XCTAssertEqual(timeline.position(at: 26_048) ?? -1, Double(6_144) / 48_000, accuracy: 1 / 48_000)
        timeline.complete(second)
        XCTAssertEqual(timeline.position(at: 30_000) ?? -1, next.endPosition, accuracy: 1 / 48_000)
    }

    func testContinuousSchedulingAndResetRejectRetiredCompletion() {
        var timeline = AudioRenderTimeline()
        let old = UUID(), current = UUID()
        let chunk = AudioPCMChunk(samples: [Float](repeating: 0, count: 4_096 * 2), position: 0)
        XCTAssertEqual(timeline.schedule(chunk, id: old, currentSampleTime: 0), 0)
        let next = AudioPCMChunk(samples: chunk.samples, position: chunk.endPosition)
        XCTAssertEqual(timeline.schedule(next, id: UUID(), currentSampleTime: 1_024), 4_096)
        XCTAssertEqual(timeline.position(at: 7_000) ?? -1, Double(7_000) / 48_000, accuracy: 1 / 48_000)
        timeline = AudioRenderTimeline()
        let sought = AudioPCMChunk(samples: chunk.samples, position: 3.312_567)
        XCTAssertEqual(timeline.schedule(sought, id: current, currentSampleTime: 0), 0)
        timeline.complete(old)
        XCTAssertEqual(timeline.position(at: 0) ?? -1, sought.position, accuracy: 1 / 48_000)
    }
}

private final class AudioPCMDeliveryGate: @unchecked Sendable {
    private let lock = NSLock()
    private let semaphore = DispatchSemaphore(value: 0)
    private var waited = false
    private let entered: XCTestExpectation
    init(entered: XCTestExpectation) { self.entered = entered }
    func waitOnce() {
        lock.lock()
        let first = !waited; waited = true
        lock.unlock()
        if first {
            entered.fulfill()
            XCTAssertEqual(semaphore.wait(timeout: .now() + 5), .success)
        }
    }
    func release() { semaphore.signal() }
}

private final class AudioDecoderTestInput: AudioByteInput, @unchecked Sendable {
    let formatHint: String? = nil
    let loopbackRoot: String? = nil
    private let bytes: Data
    private let condition = NSCondition()
    private var position = 0
    private var cancelled = false
    private var closed = false
    private var blocked = false
    private var block: XCTestExpectation?
    private var releaseOnCancel = false
    private var cancellations = 0
    private var closes = 0
    var onClose: (@Sendable () async throws -> Void)?
    var isReadCancelled: Bool { condition.lock(); defer { condition.unlock() }; return cancelled || closed }
    var cancellationCount: Int { condition.lock(); defer { condition.unlock() }; return cancellations }
    var closeCount: Int { condition.lock(); defer { condition.unlock() }; return closes }

    init(frames: Int) {
        var data = Data("RIFF".utf8)
        func append<T: FixedWidthInteger>(_ value: T) {
            var little = value.littleEndian
            withUnsafeBytes(of: &little) { data.append(contentsOf: $0) }
        }
        append(UInt32(36 + frames * 4))
        data.append(Data("WAVEfmt ".utf8))
        append(UInt32(16)); append(UInt16(1)); append(UInt16(2))
        append(UInt32(48_000)); append(UInt32(192_000))
        append(UInt16(4)); append(UInt16(16))
        data.append(Data("data".utf8)); append(UInt32(frames * 4))
        data.append(Data(repeating: 0, count: frames * 4))
        bytes = data
    }

    func blockNextRead(entered: XCTestExpectation, releaseOnCancel: Bool) {
        condition.lock(); defer { condition.unlock() }
        block = entered
        self.releaseOnCancel = releaseOnCancel
    }
    func releaseRead() {
        condition.lock(); blocked = false; condition.broadcast(); condition.unlock()
    }
    func read(maximumBytes: Int) throws -> Data {
        condition.lock(); defer { condition.unlock() }
        if let block {
            self.block = nil; blocked = true; block.fulfill()
            while blocked { condition.wait() }
        }
        if cancelled || closed { throw CancellationError() }
        let end = min(bytes.count, position + min(maximumBytes, 997))
        let result = bytes.subdata(in: position..<end)
        position = end
        return result
    }
    func seek(_ request: AudioByteSeek) throws -> Int64 {
        condition.lock(); defer { condition.unlock() }
        if cancelled || closed { throw CancellationError() }
        switch request {
        case .size: return Int64(bytes.count)
        case .offset(let offset, let origin):
            let base: Int64
            switch origin { case .start: base = 0; case .current: base = Int64(position); case .end: base = Int64(bytes.count) }
            let (target, overflow) = base.addingReportingOverflow(offset)
            guard !overflow, target >= 0, target <= Int64(bytes.count) else { throw AudioCoreError.seekFailed }
            position = Int(target)
            return target
        }
    }
    func cancelReads() {
        condition.lock(); defer { condition.unlock() }
        cancelled = true; cancellations += 1
        if releaseOnCancel { blocked = false }
        condition.broadcast()
    }
    func resumeReads() { condition.lock(); if !closed { cancelled = false }; condition.unlock() }
    private func beginClose() {
        condition.lock(); closed = true; closes += 1; condition.broadcast(); condition.unlock()
    }
    func close() async throws { beginClose(); try await onClose?() }
}

private actor AudioDecoderCloseGate {
    private var released = false
    private var waiter: CheckedContinuation<Void, Never>?
    func wait() async { if !released { await withCheckedContinuation { waiter = $0 } } }
    func release() { released = true; waiter?.resume(); waiter = nil }
}
