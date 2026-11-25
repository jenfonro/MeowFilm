#if os(iOS) || os(macOS)
import Foundation
@preconcurrency import AVFoundation
import MeowDebug

/// A decoder and a bounded planar PCM queue. Preparation is silent; the
/// AVAudioEngine graph is created only after the coordinator admits output.
///
/// The real-time PCM decode/schedule pump runs off `@MainActor` on a dedicated
/// high-priority task, fenced by monotonic `(sessionRevision, outputRevision)`.
@MainActor
public final class AudioPlaybackSession {
    /// 4 Hz progress sampling while playing; discrete state edges fire immediately.
    private static let sampleInterval: TimeInterval = 0.25

    public var onFailure: (@MainActor (Error) -> Void)?
    public var onProgress: (@MainActor (Double) -> Void)?
    public var onTransport: (@MainActor (Bool, Float) -> Void)?
    public var onEnd: (@MainActor () -> Void)?
    public var onConfigurationChange: (@MainActor () -> Void)?
    public private(set) var information: AudioStreamInformation?
    public private(set) var position = 0.0
    public private(set) var rate: Float = 0
    public var volume: Float = 1 { didSet { pipeline.setVolume(volume) } }
    public var hasOutput: Bool { pipeline.hasOutput }
    public var isPlaying: Bool { pipeline.isPlaying }
    public var bufferedFrames: Int { pipeline.bufferedFrames }
    public var bufferedSeconds: Double { Double(pipeline.bufferedFrames) / 48_000.0 }
    public var underrunCount: Int { pipeline.underrunCount }
    public var isPositionDirty: Bool { positionDirty }

    private nonisolated let decoder: AudioDecoder
    private nonisolated let pipeline: AudioOutputPipeline
    private var timer: Timer?
    private var closed = false
    private var closing: Task<Void, Error>?
    private var failure: Error?
    private var sessionRevision: UInt64 = 1
    private var positionDirty = false
    private var lastReportedPosition: Double?
    private var lastReportedPlaying: Bool?
    private var lastReportedRate: Float?

    public init(input: any AudioByteInput) {
        let decoder = AudioDecoder(input: input)
        self.decoder = decoder
        self.pipeline = AudioOutputPipeline(decoder: decoder, initialSessionRevision: 1)
        pipeline.onConfigurationChange = { [weak self] expectedRevision in
            Task { @MainActor [weak self] in
                guard let self, !self.closed, self.sessionRevision == expectedRevision else { return }
                DebugUIMetrics.shared.increment("audio.configChange")
                DebugRuntime.log(.musicPlayback, sub: "audio.kernel", event: "config_change", level: .info,
                                 data: .object(["revision": .uint64(expectedRevision)]))
                self.onConfigurationChange?()
            }
        }
        pipeline.onEnd = { [weak self] finalPosition, expectedRevision in
            Task { @MainActor [weak self] in
                guard let self, !self.closed, self.sessionRevision == expectedRevision else { return }
                self.position = max(self.position, finalPosition)
                self.rate = 0
                self.stopTimer()
                self.lastReportedPlaying = false
                self.lastReportedRate = 0
                DebugRuntime.log(.musicPlayback, sub: "audio.kernel", event: "ended", level: .info, data: .object([
                    "position": .number(self.position),
                    "underrunCount": .int(self.underrunCount)
                ]))
                self.onProgress?(self.position)
                self.onEnd?()
            }
        }
        pipeline.onFailure = { [weak self] error, expectedRevision in
            Task { @MainActor [weak self] in
                guard let self, !self.closed, self.sessionRevision == expectedRevision else { return }
                self.failure = error
                self.stopTimer()
                self.rate = 0
                self.pipeline.pause()
                self.lastReportedPlaying = false
                self.lastReportedRate = 0
                DebugRuntime.logError(.musicPlayback, sub: "audio.kernel", event: "error", error: error)
                self.onFailure?(error)
            }
        }
    }

    public func prepare() async throws {
        try check()
        let revision = sessionRevision
        let info = try await decoder.prepare()
        let firstChunk = try await decoder.read()
        try check()
        guard sessionRevision == revision else { throw CancellationError() }
        guard let firstChunk, firstChunk.frameCount > 0 else { throw AudioCoreError.invalidMedia }
        information = info
        pipeline.setInitialChunk(firstChunk, sessionRevision: revision)
        position = firstChunk.position
        positionDirty = false
        DebugRuntime.log(.musicPlayback, sub: "audio.kernel", event: "prepare.ok", level: .debug, data: .object([
            "position": .number(position),
            "duration": .optional(info.duration),
            "seekable": .bool(info.seekable),
            "firstFrames": .int(firstChunk.frameCount)
        ]))
    }

    public func check() throws {
        try Task.checkCancellation()
        if closed { throw CancellationError() }
        if let failure { throw failure }
    }

    public func play(rate requested: Float) throws {
        try check()
        guard requested.isFinite, (0.5...3).contains(requested), information != nil else {
            throw AudioCoreError.outputFailed
        }
        let outcome = try pipeline.play(rate: requested, volume: volume, sessionRevision: sessionRevision)
        switch outcome {
        case .alreadyDrained(let finalPosition, let notifyEnd):
            position = max(position, finalPosition)
            rate = 0
            stopTimer()
            lastReportedPlaying = false
            lastReportedRate = 0
            if notifyEnd {
                DebugRuntime.log(.musicPlayback, sub: "audio.kernel", event: "ended", level: .info, data: .object([
                    "position": .number(position),
                    "drainedOnPlay": .bool(true)
                ]))
                onEnd?()
            }
            return
        case .playing(let buffered):
            rate = requested
            DebugRuntime.log(.musicPlayback, sub: "audio.kernel", event: "play", level: .debug, data: .object([
                "rate": .number(Double(requested)),
                "position": .number(position),
                "bufferedFrames": .int(buffered)
            ]))
            startTimerIfNeeded()
            sample()
        }
    }

    public func pause() {
        sample()
        stopTimer()
        rate = 0
        pipeline.pause()
        lastReportedPlaying = false
        lastReportedRate = 0
        onTransport?(false, 0)
        DebugRuntime.log(.musicPlayback, sub: "audio.kernel", event: "pause", level: .debug, data: .object([
            "position": .number(position)
        ]))
    }

    /// Marks the underlying decoder position dirty after a cancelled seek so the
    /// next resume resynchronizes instead of playing from an unconfirmed byte offset.
    public func markPositionDirty() {
        positionDirty = true
        sessionRevision &+= 1
        stopTimer()
        rate = 0
        pipeline.markDirty(sessionRevision: sessionRevision)
        decoder.interrupt()
    }

    /// An engine configuration reset does not reopen or seek the byte input.
    /// Retained PCM is rescheduled from the last rendered sample, including
    /// non-seekable HTTP streams.
    public func rebuildOutput() {
        sample()
        stopTimer()
        sessionRevision &+= 1
        let rev = sessionRevision
        let drained = pipeline.rebuildOutput(at: position, sessionRevision: rev)
        rate = 0
        lastReportedPlaying = false
        lastReportedRate = 0
        onTransport?(false, 0)
        DebugRuntime.log(.musicPlayback, sub: "audio.kernel", event: "rebuild_output", level: .info, data: .object([
            "position": .number(position),
            "drained": .bool(drained),
            "bufferedFrames": .int(bufferedFrames)
        ]))
        if drained {
            onEnd?()
        }
    }

    /// Joins the cancelled read before seeking the same FFmpeg owner. The
    /// returned position belongs to the first decoded frame, not a UI target.
    public func seek(to target: Double) async throws -> Double {
        try check()
        stopTimer()
        rate = 0
        sessionRevision &+= 1
        let seekRevision = sessionRevision
        lastReportedPlaying = nil
        lastReportedRate = nil
        lastReportedPosition = nil
        await pipeline.prepareForSeek(sessionRevision: seekRevision)
        try check()
        guard sessionRevision == seekRevision else { throw CancellationError() }
        try await decoder.seek(to: target)
        try check()
        guard sessionRevision == seekRevision else { throw CancellationError() }
        let chunk = try await decoder.read()
        try check()
        guard sessionRevision == seekRevision else { throw CancellationError() }
        if let chunk {
            pipeline.setInitialChunk(chunk, sessionRevision: seekRevision)
            position = chunk.position
            positionDirty = false
        } else if let duration = information?.duration, abs(duration - target) <= 0.25 {
            pipeline.markEndedAt(duration, sessionRevision: seekRevision)
            position = duration
            positionDirty = false
        } else {
            throw AudioCoreError.seekFailed
        }
        DebugRuntime.log(.musicPlayback, sub: "audio.kernel", event: "seek", level: .debug, data: .object([
            "target": .number(target),
            "actual": .number(position)
        ]))
        return position
    }

    /// Synchronous output/input revocation; close joins the physical worker.
    public func cancel() {
        guard !closed else { return }
        closed = true
        sessionRevision &+= 1
        stopTimer()
        rate = 0
        pipeline.cancel()
        decoder.interrupt()
        onFailure = nil
        onProgress = nil
        onTransport = nil
        onEnd = nil
        onConfigurationChange = nil
    }

    public func close() async throws {
        if closing == nil {
            cancel()
            let pipeline = pipeline
            let decoder = decoder
            closing = Task {
                await pipeline.joinPump()
                try await decoder.close()
                DebugRuntime.log(.musicPlayback, sub: "audio.kernel", event: "close", level: .debug)
            }
        }
        try await closing!.value
    }

    private func startTimerIfNeeded() {
        guard timer == nil else { return }
        let sampleTimer = Timer(timeInterval: Self.sampleInterval, repeats: true) { [weak self] timer in
            guard let self else {
                timer.invalidate()
                return
            }
            MainActor.assumeIsolated { self.sample() }
        }
        RunLoop.main.add(sampleTimer, forMode: .common)
        timer = sampleTimer
    }

    private func stopTimer() {
        timer?.invalidate()
        timer = nil
    }

    private func sample() {
        guard !closed else { return }
        let snap = pipeline.sample(currentPosition: position, activeRate: rate)
        if snap.position > position {
            position = snap.position
        }
        if snap.enteredUnderrun {
            DebugRuntime.log(.musicPlayback, sub: "audio.kernel", event: "underrun", level: .warn, data: .object([
                "position": .number(position),
                "bufferedFrames": .int(bufferedFrames),
                "underrunCount": .int(snap.underrunCount)
            ]))
        }
        if lastReportedPosition != position {
            lastReportedPosition = position
            onProgress?(position)
        }
        if lastReportedPlaying != snap.isTransportPlaying || lastReportedRate != rate {
            lastReportedPlaying = snap.isTransportPlaying
            lastReportedRate = rate
            onTransport?(snap.isTransportPlaying, rate)
        }
    }

    nonisolated deinit {
        pipeline.cancel()
        decoder.interrupt()
        let pipeline = pipeline
        let decoder = decoder
        Task.detached(priority: .utility) {
            await pipeline.joinPump()
            try? await decoder.close()
        }
    }
}

// MARK: - Off-Main Real-Time Audio Pump & Buffer Queue

/// Dedicated off-main coordinator owning the decoded planar PCM queue,
/// AVAudioEngine graph, and high-priority refill loop.
private final class AudioOutputPipeline: @unchecked Sendable {
    enum PlayOutcome {
        case playing(bufferedFrames: Int)
        case alreadyDrained(Double, notifyEnd: Bool)
    }

    struct SampleSnapshot {
        let position: Double
        let isTransportPlaying: Bool
        let underrunCount: Int
        let enteredUnderrun: Bool
    }

    /// 24 × 4096 frames @ 48 kHz ≈ 2.05 seconds (~768 KB) pre-scheduled in
    /// `AVAudioPlayerNode` so background IO buffer changes & UI transitions
    /// never underrun audio output.
    private static let maxQueuedChunks = 24

    private let decoder: AudioDecoder
    private let lock = NSLock()
    private let scheduleLock = NSLock()
    private var output: AudioEngineOutput?
    private var first: AudioPCMChunk?
    private var queued: [(id: UUID, chunk: AudioPCMChunk)] = []
    private var pump: (revision: UUID, task: Task<Void, Never>)?
    private var retiringPumps: [UUID: Task<Void, Never>] = [:]
    private var pumpRevision = UUID()
    private var outputRevision = UUID()
    private var sessionRevision: UInt64
    private var position = 0.0
    private var volume: Float = 1
    private var currentRate: Float = 1
    private var paused = true
    private var ended = false
    private var endDelivered = false
    private var closed = false
    private var underruns = 0
    private var wasStalled = false
    private var waiter: CheckedContinuation<Void, Never>?

    var onConfigurationChange: (@Sendable (UInt64) -> Void)?
    var onEnd: (@Sendable (Double, UInt64) -> Void)?
    var onFailure: (@Sendable (Error, UInt64) -> Void)?

    init(decoder: AudioDecoder, initialSessionRevision: UInt64) {
        self.decoder = decoder
        self.sessionRevision = initialSessionRevision
    }

    var hasOutput: Bool {
        lock.lock(); defer { lock.unlock() }
        return output != nil
    }

    var isPlaying: Bool {
        lock.lock()
        let current = output
        let isPaused = paused
        let hasBuffered = !queued.isEmpty || first != nil
        lock.unlock()
        guard !isPaused, hasBuffered, let current else { return false }
        return current.isPlaying
    }

    var bufferedFrames: Int {
        lock.lock(); defer { lock.unlock() }
        let queuedFrames = queued.reduce(0) { $0 + $1.chunk.frameCount }
        return queuedFrames + (first?.frameCount ?? 0)
    }

    var underrunCount: Int {
        lock.lock(); defer { lock.unlock() }
        return underruns
    }

    func setVolume(_ value: Float) {
        lock.lock()
        volume = value
        let current = output
        lock.unlock()
        current?.volume = value
    }

    func setInitialChunk(_ chunk: AudioPCMChunk, sessionRevision rev: UInt64) {
        lock.lock()
        sessionRevision = rev
        first = chunk
        position = chunk.position
        paused = true
        ended = false
        endDelivered = false
        wasStalled = false
        lock.unlock()
    }

    func markEndedAt(_ duration: Double, sessionRevision rev: UInt64) {
        lock.lock()
        sessionRevision = rev
        first = nil
        position = duration
        paused = true
        ended = true
        endDelivered = false
        wasStalled = false
        lock.unlock()
    }

    func play(rate requested: Float, volume requestedVolume: Float, sessionRevision rev: UInt64) throws -> PlayOutcome {
        lock.lock()
        guard !closed else {
            lock.unlock()
            throw CancellationError()
        }
        sessionRevision = rev
        volume = requestedVolume
        if ended, queued.isEmpty, first == nil {
            let finalPosition = position
            let shouldDeliver = !endDelivered
            endDelivered = true
            paused = true
            let current = output
            lock.unlock()
            current?.pause()
            return .alreadyDrained(finalPosition, notifyEnd: shouldDeliver)
        }

        let needCreateOutput = output == nil
        lock.unlock()

        let createdGraph: AudioEngineOutput?
        if needCreateOutput {
            let graph = try AudioEngineOutput()
            graph.volume = requestedVolume
            graph.onConfigurationChange = { [weak self] in
                guard let self else { return }
                let (callback, currentRev): ((@Sendable (UInt64) -> Void)?, UInt64) = self.lock.withLock {
                    (self.closed ? nil : self.onConfigurationChange, self.sessionRevision)
                }
                callback?(currentRev)
            }
            createdGraph = graph
        } else {
            createdGraph = nil
        }

        scheduleLock.lock()
        let activeOutput: AudioEngineOutput
        let chunksToSchedule: [(id: UUID, chunk: AudioPCMChunk)]
        let outRev: UUID
        let previousRate: Float
        let consumedFirst: (id: UUID, chunk: AudioPCMChunk)?
        lock.lock()
        if closed {
            lock.unlock()
            scheduleLock.unlock()
            createdGraph?.close()
            throw CancellationError()
        }
        previousRate = currentRate
        var unusedGraph: AudioEngineOutput?
        if let existing = output {
            activeOutput = existing
            unusedGraph = createdGraph
            outRev = outputRevision
            if let initial = first {
                first = nil
                let id = UUID()
                consumedFirst = (id, initial)
                queued.append((id, initial))
                chunksToSchedule = [(id, initial)]
            } else {
                consumedFirst = nil
                chunksToSchedule = []
            }
        } else if let graph = createdGraph {
            output = graph
            activeOutput = graph
            outRev = outputRevision
            if let initial = first {
                first = nil
                let id = UUID()
                consumedFirst = (id, initial)
                queued.append((id, initial))
            } else {
                consumedFirst = nil
            }
            chunksToSchedule = queued
        } else {
            lock.unlock()
            scheduleLock.unlock()
            throw AudioCoreError.outputFailed
        }
        currentRate = requested
        paused = false
        wasStalled = false
        let buffered = queued.reduce(0) { $0 + $1.chunk.frameCount }
        lock.unlock()

        if let unusedGraph { unusedGraph.close() }

        var scheduleFailure: Error?
        var scheduledCount = 0
        activeOutput.setRate(requested)
        for entry in chunksToSchedule {
            do {
                try scheduleOutput(entry.chunk, id: entry.id, on: activeOutput, outputRevision: outRev)
                scheduledCount += 1
            } catch {
                scheduleFailure = error
                break
            }
        }
        if scheduleFailure == nil {
            do { try activeOutput.play() } catch { scheduleFailure = error }
        }
        var poisonedOutput: AudioEngineOutput?
        if scheduleFailure != nil {
            lock.withLock {
                paused = true
                currentRate = previousRate
                if let consumedFirst, scheduledCount == 0 {
                    queued.removeAll { $0.id == consumedFirst.id }
                    first = consumedFirst.chunk
                }
                if output === activeOutput {
                    output = nil
                    outputRevision = UUID()
                    poisonedOutput = activeOutput
                }
            }
        }
        scheduleLock.unlock()
        poisonedOutput?.close()
        if let scheduleFailure { throw scheduleFailure }
        startPumpIfNeeded()
        return .playing(bufferedFrames: buffered)
    }

    func pause() {
        lock.lock()
        paused = true
        wasStalled = false
        let current = output
        lock.unlock()
        current?.pause()
    }

    func markDirty(sessionRevision rev: UInt64) {
        let (previous, continuation, current): (Task<Void, Never>?, CheckedContinuation<Void, Never>?, AudioEngineOutput?) = lock.withLock {
            sessionRevision = rev
            pumpRevision = UUID()
            paused = true
            wasStalled = false
            let previous = pump
            pump = nil
            if let previous {
                retiringPumps[previous.revision] = previous.task
            }
            let continuation = waiter
            waiter = nil
            return (previous?.task, continuation, output)
        }
        previous?.cancel()
        continuation?.resume()
        current?.pause()
    }

    func rebuildOutput(at currentPosition: Double, sessionRevision rev: UInt64) -> Bool {
        let (retiring, continuation, drained): (AudioEngineOutput?, CheckedContinuation<Void, Never>?, Bool) = scheduleLock.withLock {
            lock.withLock {
                sessionRevision = rev
                paused = true
                wasStalled = false
                position = max(position, currentPosition)
                outputRevision = UUID()
                let retiring = output
                output = nil
                queued = queued.compactMap { entry in
                    let skip = max(0, Int(((position - entry.chunk.position) * 48_000).rounded(.up)))
                    guard let sliced = entry.chunk.droppingFirstFrames(skip) else { return nil }
                    return (entry.id, sliced)
                }
                let continuation = waiter
                waiter = nil
                let drained = ended && queued.isEmpty && first == nil && !closed && !endDelivered
                if drained { endDelivered = true }
                return (retiring, continuation, drained)
            }
        }

        retiring?.close()
        continuation?.resume()
        return drained
    }

    func prepareForSeek(sessionRevision rev: UInt64) async {
        let (pumpsToJoin, continuation, current): ([Task<Void, Never>], CheckedContinuation<Void, Never>?, AudioEngineOutput?) = scheduleLock.withLock {
            lock.withLock {
                sessionRevision = rev
                pumpRevision = UUID()
                outputRevision = UUID()
                paused = true
                wasStalled = false
                var retiring = Array(retiringPumps.values)
                retiringPumps.removeAll()
                if let active = pump {
                    pump = nil
                    retiring.append(active.task)
                }
                let continuation = waiter
                waiter = nil
                let current = output
                queued.removeAll()
                first = nil
                ended = false
                endDelivered = false
                return (retiring, continuation, current)
            }
        }

        for task in pumpsToJoin { task.cancel() }
        continuation?.resume()
        current?.stop()
        for task in pumpsToJoin { await task.value }
        scheduleLock.withLock {
            let active: AudioEngineOutput? = lock.withLock { output }
            active?.stop()
        }
    }

    func cancel() {
        let state: (pumps: [Task<Void, Never>], continuation: CheckedContinuation<Void, Never>?, output: AudioEngineOutput?)? = scheduleLock.withLock {
            lock.withLock {
                guard !closed else { return nil }
                closed = true
                paused = true
                pumpRevision = UUID()
                outputRevision = UUID()
                var activePumps = Array(retiringPumps.values)
                if let active = pump {
                    activePumps.append(active.task)
                }
                let continuation = waiter
                waiter = nil
                let current = output
                output = nil
                first = nil
                queued.removeAll()
                onConfigurationChange = nil
                onEnd = nil
                onFailure = nil
                return (activePumps, continuation, current)
            }
        }
        guard let state else { return }

        for task in state.pumps { task.cancel() }
        state.continuation?.resume()
        state.output?.close()
    }

    func joinPump() async {
        while true {
            let tasks: [Task<Void, Never>] = lock.withLock {
                var pending = Array(retiringPumps.values)
                retiringPumps.removeAll()
                if let active = pump {
                    pump = nil
                    pending.append(active.task)
                }
                return pending
            }
            guard !tasks.isEmpty else { return }
            for task in tasks { await task.value }
        }
    }

    func sample(currentPosition: Double, activeRate: Float) -> SampleSnapshot {
        lock.lock()
        let current = output
        let isPaused = paused
        let lastEnd = queued.last?.chunk.endPosition
        let hasBuffered = !queued.isEmpty || first != nil
        let initialUnderruns = underruns
        var pos = max(position, currentPosition)
        lock.unlock()

        guard let current, !isPaused, activeRate > 0 else {
            return SampleSnapshot(position: pos, isTransportPlaying: false, underrunCount: initialUnderruns, enteredUnderrun: false)
        }
        let sampleResult = current.sampleRenderState()
        if let candidate = sampleResult.position {
            if let lastEnd {
                pos = max(pos, min(lastEnd, candidate))
            } else {
                pos = max(pos, candidate)
            }
        }
        let playing = sampleResult.isPlaying && hasBuffered
        var enteredUnderrun = false
        let currentUnderruns: Int = lock.withLock {
            if pos > position { position = pos }
            if !playing && !ended {
                if !wasStalled {
                    wasStalled = true
                    underruns += 1
                    enteredUnderrun = true
                }
            } else {
                wasStalled = false
            }
            return underruns
        }
        return SampleSnapshot(
            position: pos,
            isTransportPlaying: playing,
            underrunCount: currentUnderruns,
            enteredUnderrun: enteredUnderrun
        )
    }

    private func startPumpIfNeeded() {
        lock.lock()
        guard pump == nil, !ended, !closed else {
            lock.unlock()
            return
        }
        let myRevision = pumpRevision
        let pendingRetiring = Array(retiringPumps.values)
        let task = Task.detached(priority: .high) { [weak self] in
            guard let self else { return }
            await self.runPump(revision: myRevision, awaitingRetiring: pendingRetiring)
        }
        pump = (myRevision, task)
        lock.unlock()
    }

    private static func recordPumpThreadMetric() {
        if Thread.isMainThread {
            DebugUIMetrics.shared.increment("audio.pump.mainThread")
        } else {
            DebugUIMetrics.shared.increment("audio.pump.offMain")
        }
    }

    private func runPump(revision myRevision: UUID, awaitingRetiring: [Task<Void, Never>]) async {
        defer {
            lock.withLock {
                if pump?.revision == myRevision { pump = nil }
                retiringPumps.removeValue(forKey: myRevision)
            }
        }
        for previous in awaitingRetiring {
            await previous.value
        }
        do {
            while !Task.isCancelled {
                let needsWait: Bool = lock.withLock {
                    if closed || pumpRevision != myRevision { return false }
                    return queued.count >= Self.maxQueuedChunks
                }
                if needsWait {
                    await waitForBufferSpace(revision: myRevision)
                    continue
                }
                let stillActive: Bool = lock.withLock {
                    !closed && pumpRevision == myRevision
                }
                guard stillActive else { return }

                let chunk = try await decoder.read()
                Self.recordPumpThreadMetric()

                guard !Task.isCancelled else { return }
                let stillActiveAfterRead: Bool = lock.withLock { !closed && pumpRevision == myRevision }
                guard stillActiveAfterRead else { return }

                guard let chunk else {
                    handleEOF(revision: myRevision)
                    return
                }
                try enqueueChunkFromPump(chunk, revision: myRevision)
            }
        } catch {
            // Read current sessionRevision under lock: rebuildOutput() rotates sessionRevision
            // while keeping the live decode pump alive across route changes.
            let delivery: (callback: (@Sendable (Error, UInt64) -> Void)?, revision: UInt64) = lock.withLock {
                let report = !Task.isCancelled && !closed && pumpRevision == myRevision
                return (report ? onFailure : nil, sessionRevision)
            }
            delivery.callback?(error, delivery.revision)
        }
    }

    private func waitForBufferSpace(revision myRevision: UUID) async {
        await withCheckedContinuation { (continuation: CheckedContinuation<Void, Never>) in
            lock.lock()
            if closed || pumpRevision != myRevision || queued.count < Self.maxQueuedChunks {
                lock.unlock()
                continuation.resume()
                return
            }
            waiter = continuation
            lock.unlock()
        }
    }

    private func enqueueChunkFromPump(_ chunk: AudioPCMChunk, revision myRevision: UUID) throws {
        scheduleLock.lock()
        defer { scheduleLock.unlock() }
        let id = UUID()
        let target: (output: AudioEngineOutput?, outRev: UUID)? = lock.withLock {
            guard !closed, pumpRevision == myRevision else { return nil }
            queued.append((id, chunk))
            return (output, outputRevision)
        }
        guard let target, let activeOutput = target.output else { return }
        try scheduleOutput(chunk, id: id, on: activeOutput, outputRevision: target.outRev)
    }

    private func scheduleOutput(
        _ chunk: AudioPCMChunk,
        id: UUID,
        on activeOutput: AudioEngineOutput,
        outputRevision expectedOutRev: UUID
    ) throws {
        try activeOutput.schedule(chunk) { [weak self] in
            self?.handleChunkCompleted(id: id, outputRevision: expectedOutRev)
        }
    }

    private func handleChunkCompleted(id: UUID, outputRevision expectedOutRev: UUID) {
        lock.lock()
        guard !closed, outputRevision == expectedOutRev,
              let index = queued.firstIndex(where: { $0.id == id }) else {
            lock.unlock()
            return
        }
        position = max(position, queued[index].chunk.endPosition)
        queued.removeFirst(index + 1)
        let continuation = waiter
        waiter = nil
        // Never deliver EOF while paused: defer until resume sees an empty drained queue.
        let shouldDeliverEnd = ended && queued.isEmpty && first == nil && !paused && !endDelivered
        let finalPosition = position
        let currentSessionRev = sessionRevision
        let endCallback = shouldDeliverEnd ? onEnd : nil
        let activeOutput = shouldDeliverEnd ? output : nil
        if shouldDeliverEnd {
            endDelivered = true
            paused = true
        }
        lock.unlock()

        continuation?.resume()
        if shouldDeliverEnd {
            activeOutput?.pause()
            endCallback?(finalPosition, currentSessionRev)
        }
    }

    private func handleEOF(revision myRevision: UUID) {
        lock.lock()
        guard !closed, pumpRevision == myRevision else {
            lock.unlock()
            return
        }
        ended = true
        let shouldDeliverEnd = queued.isEmpty && first == nil && !paused && !endDelivered
        let finalPosition = position
        let currentSessionRev = sessionRevision
        let endCallback = shouldDeliverEnd ? onEnd : nil
        let activeOutput = shouldDeliverEnd ? output : nil
        if shouldDeliverEnd {
            endDelivered = true
            paused = true
        }
        lock.unlock()

        if shouldDeliverEnd {
            activeOutput?.pause()
            endCallback?(finalPosition, currentSessionRev)
        }
    }
}

// MARK: - AVAudioEngine Output Adapter

private final class AudioEngineOutput: @unchecked Sendable {
    struct RenderSample {
        let position: Double?
        let isPlaying: Bool
    }

    /// Covers 4096-frame (85.33ms) background CoreAudio render slices plus initial
    /// resume startup slice so `isPlaying` never oscillates `.playing <-> .buffering`.
    private static let renderGraceSeconds: TimeInterval = 0.35

    private let engine = AVAudioEngine()
    private let player = AVAudioPlayerNode()
    private let timePitch = AVAudioUnitTimePitch()
    private let format: AVAudioFormat
    /// Guards all AVAudioEngine / AVAudioPlayerNode / AVAudioUnitTimePitch graph operations.
    /// Lock ordering: always `graphLock -> lock`; buffer completion callbacks acquire only `lock`.
    private let graphLock = NSLock()
    private let lock = NSLock()
    private var closed = false
    private var notification: NSObjectProtocol?
    private var paused = true
    private var lastRendered: AVAudioFramePosition = 0
    private var lastAdvanceUptime: TimeInterval = 0
    private var playStartedUptime: TimeInterval = 0
    private var advancing = false
    private var revision = UUID()
    private var timeline = AudioRenderTimeline()
    private var configurationChangeHandler: (@Sendable () -> Void)?

    var onConfigurationChange: (@Sendable () -> Void)? {
        get { lock.lock(); defer { lock.unlock() }; return configurationChangeHandler }
        set { lock.lock(); configurationChangeHandler = newValue; lock.unlock() }
    }

    var volume: Float {
        get {
            graphLock.lock()
            defer { graphLock.unlock() }
            return engine.mainMixerNode.outputVolume
        }
        set {
            graphLock.lock()
            defer { graphLock.unlock() }
            guard !closed else { return }
            engine.mainMixerNode.outputVolume = newValue
        }
    }

    var isPlaying: Bool { sampleRenderState().isPlaying }

    private var sampleTimeLocked: AVAudioFramePosition? {
        guard player.isPlaying, let time = player.lastRenderTime,
              let value = player.playerTime(forNodeTime: time), value.isSampleTimeValid else { return nil }
        return value.sampleTime
    }

    func sampleRenderState() -> RenderSample {
        graphLock.lock()
        defer { graphLock.unlock() }
        let now = ProcessInfo.processInfo.systemUptime
        let engineRunning = !closed && engine.isRunning
        let nodePlaying = !closed && player.isPlaying
        let currentSample = engineRunning && nodePlaying ? sampleTimeLocked : nil

        lock.lock()
        defer { lock.unlock() }

        guard !paused, engineRunning, nodePlaying else {
            advancing = false
            return RenderSample(position: nil, isPlaying: false)
        }

        let withinStartupGrace = playStartedUptime > 0 && (now - playStartedUptime) < Self.renderGraceSeconds
        guard let currentSample else {
            return RenderSample(position: nil, isPlaying: withinStartupGrace)
        }

        if currentSample > lastRendered {
            advancing = true
            lastRendered = currentSample
            lastAdvanceUptime = now
        } else if currentSample < lastRendered {
            advancing = false
            lastRendered = currentSample
            lastAdvanceUptime = now
        } else if advancing && (now - lastAdvanceUptime) >= Self.renderGraceSeconds {
            advancing = false
        }

        let isRenderActive = advancing || withinStartupGrace
        return RenderSample(position: timeline.position(at: currentSample), isPlaying: isRenderActive)
    }

    init() throws {
        guard let format = AVAudioFormat(commonFormat: .pcmFormatFloat32,
                                         sampleRate: 48_000, channels: 2, interleaved: false) else {
            throw AudioCoreError.outputFailed
        }
        self.format = format
        engine.attach(player)
        engine.attach(timePitch)
        engine.connect(player, to: timePitch, format: format)
        engine.connect(timePitch, to: engine.mainMixerNode, format: format)
        let maxFrames: AUAudioFrameCount = 4096
        player.auAudioUnit.maximumFramesToRender = maxFrames
        timePitch.auAudioUnit.maximumFramesToRender = maxFrames
        engine.mainMixerNode.auAudioUnit.maximumFramesToRender = maxFrames
        timePitch.bypass = true
        notification = NotificationCenter.default.addObserver(
            forName: .AVAudioEngineConfigurationChange, object: engine, queue: nil
        ) { [weak self] _ in
            self?.onConfigurationChange?()
        }
    }

    deinit {
        close()
    }

    func setRate(_ rate: Float) {
        graphLock.lock()
        defer { graphLock.unlock() }
        guard !closed else { return }
        let shouldBypass = abs(rate - 1.0) < 0.0001
        if timePitch.rate != rate {
            timePitch.rate = rate
        }
        if timePitch.bypass != shouldBypass {
            timePitch.bypass = shouldBypass
        }
    }

    func play() throws {
        graphLock.lock()
        defer { graphLock.unlock() }
        guard !closed else { throw AudioCoreError.outputFailed }
        if !engine.isRunning {
            engine.prepare()
            do { try engine.start() } catch { throw AudioCoreError.outputFailed }
        }
        let now = ProcessInfo.processInfo.systemUptime
        lock.lock()
        if paused {
            paused = false
            playStartedUptime = now
            lastAdvanceUptime = now
        }
        lock.unlock()
        if !player.isPlaying { player.play() }
    }

    func pause() {
        graphLock.lock()
        defer { graphLock.unlock() }
        if !closed {
            player.pause()
        }
        lock.lock()
        paused = true
        advancing = false
        lastRendered = 0
        lastAdvanceUptime = 0
        playStartedUptime = 0
        lock.unlock()
    }

    private func stopLocked() {
        lock.lock()
        revision = UUID()
        paused = true
        advancing = false
        lastRendered = 0
        lastAdvanceUptime = 0
        playStartedUptime = 0
        timeline = AudioRenderTimeline()
        lock.unlock()
        player.stop()
    }

    func stop() {
        graphLock.lock()
        defer { graphLock.unlock() }
        guard !closed else { return }
        stopLocked()
    }

    func schedule(_ chunk: AudioPCMChunk, completion: @escaping @Sendable () -> Void) throws {
        let frameCount = chunk.frameCount
        guard frameCount > 0,
              let buffer = AVAudioPCMBuffer(pcmFormat: format, frameCapacity: AVAudioFrameCount(frameCount)),
              let channels = buffer.floatChannelData else { throw AudioCoreError.allocationFailed }
        buffer.frameLength = AVAudioFrameCount(frameCount)
        chunk.left.withUnsafeBufferPointer { src in
            if let base = src.baseAddress { channels[0].update(from: base, count: frameCount) }
        }
        chunk.right.withUnsafeBufferPointer { src in
            if let base = src.baseAddress { channels[1].update(from: base, count: frameCount) }
        }

        graphLock.lock()
        defer { graphLock.unlock() }
        guard !closed else { throw AudioCoreError.outputFailed }
        let id = UUID()
        let currentSample = sampleTimeLocked ?? 0
        lock.lock()
        let expectedRevision = revision
        _ = timeline.schedule(chunk, id: id, currentSampleTime: currentSample)
        lock.unlock()

        player.scheduleBuffer(buffer, at: nil, options: [], completionCallbackType: .dataPlayedBack) { [weak self] _ in
            guard let self else { return }
            self.lock.lock()
            guard self.revision == expectedRevision else {
                self.lock.unlock()
                return
            }
            self.timeline.complete(id)
            self.lock.unlock()
            completion()
        }
    }

    func close() {
        graphLock.lock()
        if closed {
            graphLock.unlock()
            return
        }
        closed = true
        let token = notification
        notification = nil
        onConfigurationChange = nil
        stopLocked()
        engine.stop()
        graphLock.unlock()
        if let token {
            NotificationCenter.default.removeObserver(token)
        }
    }
}

/// Maps the output node's sample clock to actual scheduled media samples.
struct AudioRenderTimeline {
    private struct Entry {
        let id: UUID
        let start: AVAudioFramePosition
        let frames: Int
        let position: Double
        var endPosition: Double { position + Double(frames) / 48_000 }
    }
    private var entries: [Entry] = []
    private var scheduledEnd: AVAudioFramePosition = 0
    private var completedPosition: Double?

    mutating func schedule(_ chunk: AudioPCMChunk, id: UUID,
                           currentSampleTime: AVAudioFramePosition) -> AVAudioFramePosition {
        let start = max(scheduledEnd, currentSampleTime)
        entries.append(.init(id: id, start: start, frames: chunk.frameCount, position: chunk.position))
        scheduledEnd = start + AVAudioFramePosition(chunk.frameCount)
        return start
    }

    func position(at sampleTime: AVAudioFramePosition) -> Double? {
        var position = completedPosition
        for entry in entries {
            guard sampleTime >= entry.start else { break }
            let frames = min(AVAudioFramePosition(entry.frames), sampleTime - entry.start)
            position = entry.position + Double(frames) / 48_000
            if frames < AVAudioFramePosition(entry.frames) { break }
        }
        return position
    }

    mutating func complete(_ id: UUID) {
        guard let index = entries.firstIndex(where: { $0.id == id }) else { return }
        completedPosition = entries[index].endPosition
        entries.removeFirst(index + 1)
    }
}
#endif
