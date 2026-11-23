#if os(iOS) || os(macOS)
import Foundation
@preconcurrency import AVFoundation
import MeowDebug

/// A decoder and a bounded PCM queue. Preparation is silent; the graph is
/// created only after the product has admitted this session's output.
///
/// The real-time PCM decode/deinterleave/schedule pump runs completely off
/// `@MainActor` on a dedicated user-interactive pipeline so UI transitions,
/// fullscreen player opens, and app backgrounding cannot starve audio output.
@MainActor
public final class AudioPlaybackSession {
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
    var hasDecodedToEnd: Bool { pipeline.hasDecodedToEnd }

    private let decoder: AudioDecoder
    private let pipeline: AudioOutputPipeline
    private var timer: Timer?
    private var closed = false
    private var closing: Task<Void, Error>?
    private var failure: Error?
    private var lastReportedPosition: Double?
    private var lastReportedPlaying: Bool?
    private var lastReportedRate: Float?

    public init(input: any AudioByteInput) {
        let decoder = AudioDecoder(input: input)
        self.decoder = decoder
        self.pipeline = AudioOutputPipeline(decoder: decoder, makeOutput: { try AudioEngineOutput() })
        pipeline.onConfigurationChange = { [weak self] in
            Task { @MainActor [weak self] in
                guard let self, !self.closed else { return }
                DebugUIMetrics.shared.increment("audio.configChange")
                DebugRuntime.log(.musicPlayback, sub: "audio.kernel", event: "config_change", level: .info)
                self.onConfigurationChange?()
            }
        }
        pipeline.onEnd = { [weak self] finalPosition in
            Task { @MainActor [weak self] in
                guard let self, !self.closed else { return }
                self.position = max(self.position, finalPosition)
                self.rate = 0
                self.timer?.invalidate()
                self.timer = nil
                self.lastReportedPlaying = false
                self.lastReportedRate = 0
                self.onProgress?(self.position)
                self.onEnd?()
            }
        }
        pipeline.onFailure = { [weak self] error in
            Task { @MainActor [weak self] in
                guard let self, !self.closed else { return }
                self.failure = error
                self.pause()
                self.onFailure?(error)
            }
        }
    }

    public func prepare() async throws {
        try check()
        information = try await decoder.prepare()
        let firstChunk = try await decoder.read()
        try check()
        guard let firstChunk, firstChunk.frameCount > 0 else { throw AudioCoreError.invalidMedia }
        pipeline.setInitialChunk(firstChunk)
        position = firstChunk.position
        DebugRuntime.log(.musicPlayback, sub: "audio.kernel", event: "prepare.ok", level: .debug, data: .object([
            "position": .number(position),
            "duration": .optional(information?.duration),
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
        let outcome = try pipeline.play(rate: requested, volume: volume)
        switch outcome {
        case .alreadyDrained(let finalPosition, let notifyEnd):
            position = max(position, finalPosition)
            rate = 0
            timer?.invalidate()
            timer = nil
            lastReportedPlaying = false
            lastReportedRate = 0
            if notifyEnd { onEnd?() }
            return
        case .playing(let buffered):
            rate = requested
            DebugRuntime.log(.musicPlayback, sub: "audio.kernel", event: "play", level: .debug, data: .object([
                "rate": .number(Double(requested)),
                "position": .number(position),
                "bufferedFrames": .int(buffered)
            ]))
            if timer == nil {
                timer = Timer.scheduledTimer(withTimeInterval: 0.05, repeats: true) { [weak self] _ in
                    MainActor.assumeIsolated { self?.sample() }
                }
            }
        }
    }

    public func pause() {
        sample()
        timer?.invalidate()
        timer = nil
        rate = 0
        pipeline.pause()
        lastReportedPlaying = false
        lastReportedRate = 0
        onTransport?(false, 0)
        DebugRuntime.log(.musicPlayback, sub: "audio.kernel", event: "pause", level: .debug, data: .object([
            "position": .number(position)
        ]))
    }

    /// An engine configuration reset does not reopen or seek the byte input.
    /// Retained PCM is rescheduled from the last rendered sample, including
    /// sources whose HTTP server cannot seek.
    public func rebuildOutput() {
        sample()
        timer?.invalidate()
        timer = nil
        let drained = pipeline.rebuildOutput(at: position)
        rate = 0
        lastReportedPlaying = false
        lastReportedRate = 0
        onTransport?(false, 0)
        if drained {
            onEnd?()
        }
    }

    /// Joins the cancelled read before seeking the same FFmpeg owner. The
    /// returned position belongs to the first decoded frame, not a UI target.
    public func seek(to target: Double) async throws -> Double {
        try check()
        timer?.invalidate()
        timer = nil
        rate = 0
        lastReportedPlaying = nil
        lastReportedRate = nil
        lastReportedPosition = nil
        await pipeline.prepareForSeek()
        try check()
        try await decoder.seek(to: target)
        let chunk = try await decoder.read()
        try check()
        if let chunk {
            pipeline.setInitialChunk(chunk)
            position = chunk.position
        } else if let duration = information?.duration, abs(duration - target) <= 0.25 {
            pipeline.markEndedAt(duration)
            position = duration
        } else {
            throw AudioCoreError.seekFailed
        }
        return position
    }

    /// Synchronous output/input revocation; close joins the physical worker.
    public func cancel() {
        guard !closed else { return }
        closed = true
        timer?.invalidate()
        timer = nil
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
            }
        }
        try await closing!.value
    }

    private func sample() {
        guard !closed else { return }
        let snap = pipeline.sample(currentPosition: position, activeRate: rate)
        if snap.position > position {
            position = snap.position
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

    isolated deinit {
        timer?.invalidate()
        pipeline.cancel()
        decoder.interrupt()
        let decoder = decoder
        Task { try? await decoder.close() }
    }
}

// MARK: - Off-Main Real-Time Audio Pump & Buffer Queue

/// Dedicated off-main coordinator owning the decoded PCM queue, AVAudioEngine
/// graph, and user-interactive refill loop. Zero PCM decode/deinterleave or
/// `scheduleBuffer` work ever touches `@MainActor`.
private final class AudioOutputPipeline: @unchecked Sendable {
    enum PlayOutcome {
        case playing(bufferedFrames: Int)
        case alreadyDrained(Double, notifyEnd: Bool)
    }

    struct SampleSnapshot {
        let position: Double
        let isTransportPlaying: Bool
    }

    /// 24 × 4096 frames @ 48 kHz ≈ 2.05 seconds (~768 KB) pre-scheduled in
    /// `AVAudioPlayerNode` so background IO buffer changes & UI transitions
    /// never underrun audio output.
    private static let maxQueuedChunks = 24

    private let decoder: AudioDecoder
    private let makeOutput: @Sendable () throws -> any AudioPCMOutput
    private let lock = NSLock()
    /// Serializes `AVAudioPlayerNode` scheduling, graph swap, and seek stop so
    /// `play()`, `runPump()`, `rebuildOutput()`, and `prepareForSeek()` can never
    /// interleave out-of-order PCM buffers or leak a stale pre-seek buffer.
    private let scheduleLock = NSLock()
    private var output: (any AudioPCMOutput)?
    private var first: AudioPCMChunk?
    private var queued: [(id: UUID, chunk: AudioPCMChunk)] = []
    private var pump: Task<Void, Never>?
    private var revision = UUID()
    private var outputRevision = UUID()
    private var position = 0.0
    private var volume: Float = 1
    private var ended = false
    private var endDelivered = false
    private var closed = false
    private var waiter: CheckedContinuation<Void, Never>?

    var onConfigurationChange: (@Sendable () -> Void)?
    var onEnd: (@Sendable (Double) -> Void)?
    var onFailure: (@Sendable (Error) -> Void)?

    init(decoder: AudioDecoder, makeOutput: @escaping @Sendable () throws -> any AudioPCMOutput) {
        self.decoder = decoder
        self.makeOutput = makeOutput
    }

    var hasOutput: Bool {
        lock.lock(); defer { lock.unlock() }
        return output != nil
    }

    var isPlaying: Bool {
        lock.lock()
        let current = output
        lock.unlock()
        return current?.isPlaying == true
    }

    var bufferedFrames: Int {
        lock.lock(); defer { lock.unlock() }
        return queued.reduce(0) { $0 + $1.chunk.frameCount }
    }

    var hasDecodedToEnd: Bool {
        lock.lock(); defer { lock.unlock() }
        return ended
    }

    func setVolume(_ value: Float) {
        lock.lock()
        volume = value
        let current = output
        lock.unlock()
        current?.volume = value
    }

    func setInitialChunk(_ chunk: AudioPCMChunk) {
        lock.lock()
        first = chunk
        position = chunk.position
        ended = false
        endDelivered = false
        lock.unlock()
    }

    func markEndedAt(_ duration: Double) {
        lock.lock()
        first = nil
        position = duration
        ended = true
        endDelivered = true
        lock.unlock()
    }

    func play(rate requested: Float, volume requestedVolume: Float) throws -> PlayOutcome {
        lock.lock()
        guard !closed else {
            lock.unlock()
            throw CancellationError()
        }
        volume = requestedVolume
        if ended, queued.isEmpty, first == nil {
            let finalPosition = position
            let shouldDeliver = !endDelivered
            endDelivered = true
            let current = output
            lock.unlock()
            current?.pause()
            return .alreadyDrained(finalPosition, notifyEnd: shouldDeliver)
        }

        let needCreateOutput = output == nil
        lock.unlock()

        // Create AVAudioEngine graph outside locks so engine setup never blocks state reads.
        let createdGraph: (any AudioPCMOutput)?
        if needCreateOutput {
            let graph = try makeOutput()
            graph.volume = requestedVolume
            graph.onConfigurationChange = { [weak self] in
                self?.onConfigurationChange?()
            }
            createdGraph = graph
        } else {
            createdGraph = nil
        }

        scheduleLock.lock()
        let activeOutput: any AudioPCMOutput
        let chunksToSchedule: [(id: UUID, chunk: AudioPCMChunk)]
        let outRev: UUID
        lock.lock()
        if closed {
            lock.unlock()
            scheduleLock.unlock()
            createdGraph?.close()
            throw CancellationError()
        }
        var unusedGraph: (any AudioPCMOutput)?
        if let existing = output {
            activeOutput = existing
            unusedGraph = createdGraph
            outRev = outputRevision
            if let initial = first {
                first = nil
                let id = UUID()
                queued.append((id, initial))
                chunksToSchedule = [(id, initial)]
            } else {
                chunksToSchedule = []
            }
        } else if let graph = createdGraph {
            output = graph
            activeOutput = graph
            outRev = outputRevision
            if let initial = first {
                first = nil
                queued.append((UUID(), initial))
            }
            chunksToSchedule = queued
        } else {
            lock.unlock()
            scheduleLock.unlock()
            throw AudioCoreError.outputFailed
        }
        lock.unlock()

        if let unusedGraph { unusedGraph.close() }

        var scheduleFailure: Error?
        var failedDuringSchedule = false
        activeOutput.setRate(requested)
        for entry in chunksToSchedule {
            do {
                try scheduleOutput(entry.chunk, id: entry.id, on: activeOutput, outputRevision: outRev)
            } catch {
                scheduleFailure = error
                failedDuringSchedule = true
                break
            }
        }
        if scheduleFailure == nil {
            do { try activeOutput.play() } catch { scheduleFailure = error }
        }
        var poisonedOutput: (any AudioPCMOutput)?
        if scheduleFailure != nil && (failedDuringSchedule || createdGraph != nil) {
            lock.withLock {
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

        lock.lock()
        let buffered = queued.reduce(0) { $0 + $1.chunk.frameCount }
        lock.unlock()
        return .playing(bufferedFrames: buffered)
    }

    func pause() {
        lock.lock()
        let current = output
        lock.unlock()
        current?.pause()
    }

    func rebuildOutput(at currentPosition: Double) -> Bool {
        let (retiring, continuation, drained): ((any AudioPCMOutput)?, CheckedContinuation<Void, Never>?, Bool) = scheduleLock.withLock {
            lock.withLock {
                position = max(position, currentPosition)
                outputRevision = UUID()
                let retiring = output
                output = nil
                queued = queued.compactMap { entry in
                    let skip = max(0, Int(((position - entry.chunk.position) * 48_000).rounded(.up)))
                    guard skip < entry.chunk.frameCount else { return nil }
                    if skip == 0 { return entry }
                    let chunk = AudioPCMChunk(
                        samples: Array(entry.chunk.samples.dropFirst(skip * 2)),
                        position: entry.chunk.position + Double(skip) / 48_000
                    )
                    return (entry.id, chunk)
                }
                let continuation = waiter
                waiter = nil
                let drained = ended && queued.isEmpty && !closed && !endDelivered
                if drained { endDelivered = true }
                return (retiring, continuation, drained)
            }
        }

        retiring?.close()
        continuation?.resume()
        return drained
    }

    func prepareForSeek() async {
        let (previous, continuation, current): (Task<Void, Never>?, CheckedContinuation<Void, Never>?, (any AudioPCMOutput)?) = scheduleLock.withLock {
            lock.withLock {
                revision = UUID()
                outputRevision = UUID()
                let previous = pump
                pump = nil
                let continuation = waiter
                waiter = nil
                let current = output
                queued.removeAll()
                first = nil
                ended = false
                endDelivered = false
                return (previous, continuation, current)
            }
        }

        previous?.cancel()
        continuation?.resume()
        current?.stop()
        await previous?.value
        // Drain any remaining scheduled buffers on the reused player node after pump exit.
        scheduleLock.withLock {
            let active: (any AudioPCMOutput)? = lock.withLock { output }
            active?.stop()
        }
    }

    func cancel() {
        let state: (pump: Task<Void, Never>?, continuation: CheckedContinuation<Void, Never>?, output: (any AudioPCMOutput)?)? = scheduleLock.withLock {
            lock.withLock {
                guard !closed else { return nil }
                closed = true
                revision = UUID()
                outputRevision = UUID()
                let activePump = pump
                let continuation = waiter
                waiter = nil
                let current = output
                output = nil
                first = nil
                queued.removeAll()
                onConfigurationChange = nil
                onEnd = nil
                onFailure = nil
                return (activePump, continuation, current)
            }
        }
        guard let state else { return }

        state.pump?.cancel()
        state.continuation?.resume()
        state.output?.close()
    }

    func joinPump() async {
        let activePump: Task<Void, Never>? = lock.withLock {
            let active = pump
            pump = nil
            return active
        }
        await activePump?.value
    }

    func sample(currentPosition: Double, activeRate: Float) -> SampleSnapshot {
        lock.lock()
        let current = output
        let lastEnd = queued.last?.chunk.endPosition
        let hasBuffered = !queued.isEmpty
        var pos = max(position, currentPosition)
        lock.unlock()

        guard let current else {
            return SampleSnapshot(position: pos, isTransportPlaying: false)
        }
        if let candidate = current.renderedPosition {
            if let lastEnd {
                pos = max(pos, min(lastEnd, candidate))
            }
            lock.lock()
            if pos > position { position = pos }
            lock.unlock()
        }
        let playing = activeRate > 0 && current.isPlaying && hasBuffered
        return SampleSnapshot(position: pos, isTransportPlaying: playing)
    }

    private func startPumpIfNeeded() {
        lock.lock()
        guard pump == nil, !ended, !closed else {
            lock.unlock()
            return
        }
        let myRevision = revision
        let task = Task.detached(priority: .high) { [weak self] in
            guard let self else { return }
            await self.runPump(revision: myRevision)
        }
        pump = task
        lock.unlock()
    }

    private static func recordPumpThreadMetric() {
        if Thread.isMainThread {
            DebugUIMetrics.shared.increment("audio.pump.mainThread")
        } else {
            DebugUIMetrics.shared.increment("audio.pump.offMain")
        }
    }

    private func runPump(revision myRevision: UUID) async {
        defer {
            lock.withLock {
                if revision == myRevision { pump = nil }
            }
        }
        do {
            while !Task.isCancelled {
                let needsWait: Bool = lock.withLock {
                    if closed || revision != myRevision { return false }
                    return queued.count >= Self.maxQueuedChunks
                }
                if needsWait {
                    await waitForBufferSpace(revision: myRevision)
                    continue
                }
                let stillActive: Bool = lock.withLock {
                    !closed && revision == myRevision
                }
                guard stillActive else { return }

                let chunk = try await decoder.read()
                Self.recordPumpThreadMetric()

                guard !Task.isCancelled else { return }
                let stillActiveAfterRead: Bool = lock.withLock { !closed && revision == myRevision }
                guard stillActiveAfterRead else { return }

                guard let chunk else {
                    handleEOF(revision: myRevision)
                    return
                }
                try enqueueChunkFromPump(chunk, revision: myRevision)
            }
        } catch {
            let failureCallback: (@Sendable (Error) -> Void)? = lock.withLock {
                let report = !Task.isCancelled && !closed && revision == myRevision
                return report ? onFailure : nil
            }
            failureCallback?(error)
        }
    }

    private func waitForBufferSpace(revision myRevision: UUID) async {
        await withCheckedContinuation { (continuation: CheckedContinuation<Void, Never>) in
            lock.lock()
            if closed || revision != myRevision || queued.count < Self.maxQueuedChunks {
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
        let target: (output: (any AudioPCMOutput)?, outRev: UUID)? = lock.withLock {
            guard !closed, revision == myRevision else { return nil }
            queued.append((id, chunk))
            return (output, outputRevision)
        }
        guard let target, let activeOutput = target.output else { return }
        try scheduleOutput(chunk, id: id, on: activeOutput, outputRevision: target.outRev)
    }

    private func scheduleOutput(
        _ chunk: AudioPCMChunk,
        id: UUID,
        on activeOutput: any AudioPCMOutput,
        outputRevision expectedOutRev: UUID
    ) throws {
        try activeOutput.schedule(chunk) { [weak self] in
            self?.handleChunkCompleted(id: id, outputRevision: expectedOutRev)
        }
    }

    private func handleChunkCompleted(id: UUID, outputRevision expectedOutRev: UUID) {
        lock.lock()
        guard !closed, outputRevision == expectedOutRev,
              output?.isRunning == true,
              let index = queued.firstIndex(where: { $0.id == id }) else {
            lock.unlock()
            return
        }
        position = max(position, queued[index].chunk.endPosition)
        queued.removeFirst(index + 1)
        let continuation = waiter
        waiter = nil
        let shouldDeliverEnd = ended && queued.isEmpty && !endDelivered
        let finalPosition = position
        let endCallback = shouldDeliverEnd ? onEnd : nil
        let activeOutput = shouldDeliverEnd ? output : nil
        if shouldDeliverEnd { endDelivered = true }
        lock.unlock()

        continuation?.resume()
        if shouldDeliverEnd {
            activeOutput?.pause()
            endCallback?(finalPosition)
        }
    }

    private func handleEOF(revision myRevision: UUID) {
        lock.lock()
        guard !closed, revision == myRevision else {
            lock.unlock()
            return
        }
        ended = true
        let shouldDeliverEnd = queued.isEmpty && !endDelivered
        let finalPosition = position
        let endCallback = shouldDeliverEnd ? onEnd : nil
        let activeOutput = shouldDeliverEnd ? output : nil
        if shouldDeliverEnd { endDelivered = true }
        lock.unlock()

        if shouldDeliverEnd {
            activeOutput?.pause()
            endCallback?(finalPosition)
        }
    }
}

// MARK: - AudioPCMOutput & AVAudioEngine Adapter

protocol AudioPCMOutput: AnyObject, Sendable {
    var volume: Float { get set }
    var isPlaying: Bool { get }
    var isRunning: Bool { get }
    var renderedPosition: Double? { get }
    var onConfigurationChange: (@Sendable () -> Void)? { get set }
    func setRate(_ rate: Float)
    func play() throws
    func pause()
    func stop()
    func schedule(_ chunk: AudioPCMChunk, completion: @escaping @Sendable () -> Void) throws
    func close()
}

private final class AudioEngineOutput: AudioPCMOutput, @unchecked Sendable {
    private let engine = AVAudioEngine()
    private let player = AVAudioPlayerNode()
    private let timePitch = AVAudioUnitTimePitch()
    private let format: AVAudioFormat
    private let lock = NSLock()
    private var notification: NSObjectProtocol?
    private var lastRendered: AVAudioFramePosition = 0
    private var lastAdvanceUptime: TimeInterval = 0
    private var advancing = false
    private var revision = UUID()
    private var timeline = AudioRenderTimeline()
    private var configurationChangeHandler: (@Sendable () -> Void)?

    /// Covers 4096-frame (85.33ms) background CoreAudio render slices sampled on a
    /// 50ms UI timer so `isPlaying` never oscillates `.playing <-> .buffering` in background.
    private static let backgroundSliceGrace: TimeInterval = 0.25

    var onConfigurationChange: (@Sendable () -> Void)? {
        get { lock.lock(); defer { lock.unlock() }; return configurationChangeHandler }
        set { lock.lock(); configurationChangeHandler = newValue; lock.unlock() }
    }

    var volume: Float {
        get { engine.mainMixerNode.outputVolume }
        set { engine.mainMixerNode.outputVolume = newValue }
    }

    var isPlaying: Bool {
        lock.lock()
        let isAdvancing = advancing
        lock.unlock()
        return player.isPlaying && engine.isRunning && isAdvancing
    }

    var isRunning: Bool { engine.isRunning }

    private var sampleTime: AVAudioFramePosition? {
        guard player.isPlaying, let time = player.lastRenderTime,
              let value = player.playerTime(forNodeTime: time), value.isSampleTimeValid else { return nil }
        return value.sampleTime
    }

    var renderedPosition: Double? {
        guard let sampleTime else {
            lock.lock()
            advancing = false
            lastAdvanceUptime = 0
            lock.unlock()
            return nil
        }
        let now = ProcessInfo.processInfo.systemUptime
        lock.lock()
        defer { lock.unlock() }
        if sampleTime > lastRendered {
            advancing = true
            lastRendered = sampleTime
            lastAdvanceUptime = now
        } else if sampleTime < lastRendered {
            advancing = false
            lastRendered = sampleTime
            lastAdvanceUptime = 0
        } else if advancing && (now - lastAdvanceUptime) >= Self.backgroundSliceGrace {
            advancing = false
        }
        return timeline.position(at: sampleTime)
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
        // iOS increases CoreAudio render slice size up to 4096 frames in background;
        // configure every AU in the graph up front so entering background never glitches.
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

    func setRate(_ rate: Float) {
        let shouldBypass = abs(rate - 1.0) < 0.0001
        if timePitch.rate != rate {
            timePitch.rate = rate
        }
        if timePitch.bypass != shouldBypass {
            timePitch.bypass = shouldBypass
        }
    }

    func play() throws {
        if !engine.isRunning {
            engine.prepare()
            do { try engine.start() } catch { throw AudioCoreError.outputFailed }
        }
        if !player.isPlaying { player.play() }
    }

    func pause() {
        player.pause()
        lock.lock()
        advancing = false
        lastAdvanceUptime = 0
        lock.unlock()
    }

    func stop() {
        lock.lock()
        revision = UUID()
        advancing = false
        lastRendered = 0
        lastAdvanceUptime = 0
        timeline = AudioRenderTimeline()
        lock.unlock()
        player.stop()
    }

    func schedule(_ chunk: AudioPCMChunk, completion: @escaping @Sendable () -> Void) throws {
        guard let buffer = AVAudioPCMBuffer(pcmFormat: format, frameCapacity: AVAudioFrameCount(chunk.frameCount)),
              let channels = buffer.floatChannelData else { throw AudioCoreError.allocationFailed }
        buffer.frameLength = buffer.frameCapacity
        let left = channels[0]
        let right = channels[1]
        chunk.samples.withUnsafeBufferPointer { source in
            guard let base = source.baseAddress else { return }
            for frame in 0..<chunk.frameCount {
                left[frame] = base[frame * 2]
                right[frame] = base[frame * 2 + 1]
            }
        }
        let id = UUID()
        let currentSample = sampleTime ?? 0
        lock.lock()
        let expectedRevision = revision
        _ = timeline.schedule(chunk, id: id, currentSampleTime: currentSample)
        lock.unlock()

        // Use gapless FIFO queueing (`at: nil`) so buffers always concatenate
        // seamlessly without sample-clock drift clipping or silence gaps.
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
        if let notification {
            NotificationCenter.default.removeObserver(notification)
            self.notification = nil
        }
        onConfigurationChange = nil
        stop()
        engine.stop()
    }
}

/// Maps the output node's sample clock to actual scheduled media samples.
/// A node can keep rendering silence during underrun; that gap is not music.
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
