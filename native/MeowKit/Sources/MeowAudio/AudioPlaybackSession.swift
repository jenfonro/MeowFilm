#if os(iOS) || os(macOS)
import Foundation
@preconcurrency import AVFoundation

/// A decoder and a bounded PCM queue. Preparation is silent; the graph is
/// created only after the product has admitted this session's output.
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
    public var volume: Float = 1 { didSet { output?.volume = volume } }
    public var hasOutput: Bool { output != nil }
    public var isPlaying: Bool { output?.isPlaying == true }
    public var bufferedFrames: Int { queued.reduce(0) { $0 + $1.chunk.frameCount } }
    var hasDecodedToEnd: Bool { ended }

    private let decoder: AudioDecoder
    private let makeOutput: @MainActor () throws -> any AudioPCMOutput
    private var output: (any AudioPCMOutput)?
    private var first: AudioPCMChunk?
    private var queued: [(id: UUID, chunk: AudioPCMChunk)] = []
    private var pump: Task<Void, Never>?
    private var timer: Timer?
    private var revision = UUID()
    private var outputRevision = UUID()
    private var ended = false
    private var endDelivered = false
    private var closed = false
    private var closing: Task<Void, Error>?
    private var failure: Error?
    private var waiter: CheckedContinuation<Void, Never>?

    public convenience init(input: any AudioByteInput) {
        self.init(input: input, makeOutput: { try AudioEngineOutput() })
    }

    init(input: any AudioByteInput, makeOutput: @escaping @MainActor () throws -> any AudioPCMOutput) {
        decoder = AudioDecoder(input: input)
        self.makeOutput = makeOutput
    }

    public func prepare() async throws {
        try check()
        information = try await decoder.prepare()
        first = try await decoder.read()
        try check()
        guard let first, first.frameCount > 0 else { throw AudioCoreError.invalidMedia }
        position = first.position
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
        if ended, queued.isEmpty, first == nil {
            rate = 0
            finishIfDrained()
            return
        }
        if output == nil {
            let graph = try makeOutput()
            graph.volume = volume
            graph.onConfigurationChange = { [weak self] in self?.onConfigurationChange?() }
            output = graph
            for entry in queued { try scheduleOutput(entry.chunk, id: entry.id) }
        }
        guard let output else { throw AudioCoreError.outputFailed }
        rate = requested
        output.setRate(requested)
        if let first {
            self.first = nil
            try schedule(first)
        }
        try output.play()
        if timer == nil {
            timer = Timer.scheduledTimer(withTimeInterval: 0.05, repeats: true) { [weak self] _ in
                MainActor.assumeIsolated { self?.sample() }
            }
        }
        startPump()
    }

    public func pause() {
        sample()
        rate = 0
        output?.pause()
        onTransport?(false, 0)
    }

    /// An engine configuration reset does not reopen or seek the byte input.
    /// Retained PCM is rescheduled from the last rendered sample, including
    /// sources whose HTTP server cannot seek.
    public func rebuildOutput() {
        sample()
        outputRevision = UUID()
        output?.close(); output = nil; rate = 0
        queued = queued.compactMap { entry in
            let skip = max(0, Int(((position - entry.chunk.position) * 48_000).rounded(.up)))
            guard skip < entry.chunk.frameCount else { return nil }
            if skip == 0 { return entry }
            let chunk = AudioPCMChunk(samples: Array(entry.chunk.samples.dropFirst(skip * 2)),
                                      position: entry.chunk.position + Double(skip) / 48_000)
            return (entry.id, chunk)
        }
        onTransport?(false, 0)
        wakePump()
        finishIfDrained()
    }

    /// Joins the cancelled read before seeking the same FFmpeg owner. The
    /// returned position belongs to the first decoded frame, not a UI target.
    public func seek(to target: Double) async throws -> Double {
        try check()
        revision = UUID()
        outputRevision = UUID()
        let previous = pump
        pump = nil
        previous?.cancel()
        wakePump()
        output?.stop()
        queued.removeAll()
        first = nil
        ended = false
        endDelivered = false
        rate = 0
        await previous?.value
        try check()
        try await decoder.seek(to: target)
        let chunk = try await decoder.read()
        try check()
        if let chunk {
            first = chunk
            position = chunk.position
        } else if let duration = information?.duration, abs(duration - target) <= 0.25 {
            position = duration
            ended = true
            endDelivered = true
        } else { throw AudioCoreError.seekFailed }
        return position
    }

    /// Synchronous output/input revocation; close joins the physical worker.
    public func cancel() {
        guard !closed else { return }
        closed = true
        revision = UUID()
        outputRevision = UUID()
        pump?.cancel()
        wakePump()
        decoder.interrupt()
        timer?.invalidate(); timer = nil
        output?.close(); output = nil
        first = nil; queued.removeAll(); rate = 0
        onFailure = nil; onProgress = nil; onTransport = nil; onEnd = nil
        onConfigurationChange = nil
    }

    public func close() async throws {
        if closing == nil {
            cancel()
            let pump = pump, decoder = decoder
            self.pump = nil
            closing = Task {
                await pump?.value
                try await decoder.close()
            }
        }
        try await closing!.value
    }

    private func startPump() {
        guard pump == nil, !ended, !closed else { return }
        let revision = revision
        pump = Task { [weak self] in
            guard let self else { return }
            defer { if self.revision == revision { self.pump = nil } }
            do {
                while !Task.isCancelled, !self.closed, self.revision == revision {
                    // At most eight 4096-frame buffers (~0.68 s), plus one
                    // native decoder chunk. No network work on the render thread.
                    if self.queued.count >= 8 {
                        await withCheckedContinuation { self.waiter = $0 }
                        continue
                    }
                    let chunk = try await self.decoder.read()
                    guard !Task.isCancelled, !self.closed, self.revision == revision else { return }
                    guard let chunk else {
                        self.ended = true
                        self.finishIfDrained()
                        return
                    }
                    try self.schedule(chunk)
                }
            } catch {
                guard !Task.isCancelled, !self.closed, self.revision == revision else { return }
                self.failure = error
                self.pause()
                self.onFailure?(error)
            }
        }
    }

    private func schedule(_ chunk: AudioPCMChunk) throws {
        guard !closed else { return }
        let id = UUID()
        queued.append((id, chunk))
        try scheduleOutput(chunk, id: id)
    }

    private func scheduleOutput(_ chunk: AudioPCMChunk, id: UUID) throws {
        guard let output else { return }
        let revision = outputRevision
        try output.schedule(chunk) { [weak self] in
            guard let self, !self.closed, self.outputRevision == revision,
                  self.output?.isRunning == true,
                  let index = self.queued.firstIndex(where: { $0.id == id }) else { return }
            self.position = max(self.position, self.queued[index].chunk.endPosition)
            self.queued.removeFirst(index + 1)
            self.onProgress?(self.position)
            self.wakePump()
            self.finishIfDrained()
        }
    }

    private func sample() {
        guard !closed, let output else { return }
        if let candidate = output.renderedPosition {
            if let last = queued.last {
                position = max(position, min(last.chunk.endPosition, candidate))
            }
            onProgress?(position)
        }
        onTransport?(rate > 0 && output.isPlaying && !queued.isEmpty, rate)
    }

    private func finishIfDrained() {
        guard ended, queued.isEmpty, !closed, !endDelivered else { return }
        endDelivered = true
        output?.pause()
        rate = 0
        timer?.invalidate(); timer = nil
        onEnd?()
    }

    private func wakePump() {
        let continuation = waiter; waiter = nil
        continuation?.resume()
    }

    isolated deinit {
        timer?.invalidate()
        output?.close()
        pump?.cancel()
        waiter?.resume()
        decoder.interrupt()
        let decoder = decoder
        Task { try? await decoder.close() }
    }
}

@MainActor
protocol AudioPCMOutput: AnyObject {
    var volume: Float { get set }
    var isPlaying: Bool { get }
    var isRunning: Bool { get }
    var renderedPosition: Double? { get }
    var onConfigurationChange: (@MainActor () -> Void)? { get set }
    func setRate(_ rate: Float)
    func play() throws
    func pause()
    func stop()
    func schedule(_ chunk: AudioPCMChunk, completion: @escaping @MainActor () -> Void) throws
    func close()
}

@MainActor
private final class AudioEngineOutput: AudioPCMOutput {
    private let engine = AVAudioEngine()
    private let player = AVAudioPlayerNode()
    private let timePitch = AVAudioUnitTimePitch()
    private let format: AVAudioFormat
    private var notification: NSObjectProtocol?
    private var lastRendered: AVAudioFramePosition = 0
    private var advancing = false
    private var revision = UUID()
    private var timeline = AudioRenderTimeline()
    var onConfigurationChange: (@MainActor () -> Void)?
    var volume: Float = 1 { didSet { engine.mainMixerNode.outputVolume = volume } }
    var isPlaying: Bool { player.isPlaying && engine.isRunning && advancing }
    var isRunning: Bool { engine.isRunning }
    private var sampleTime: AVAudioFramePosition? {
        guard player.isPlaying, let time = player.lastRenderTime,
              let value = player.playerTime(forNodeTime: time), value.isSampleTimeValid else { return nil }
        return value.sampleTime
    }
    var renderedPosition: Double? {
        guard let sampleTime else {
            advancing = false
            return nil
        }
        advancing = sampleTime > lastRendered
        lastRendered = sampleTime
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
        notification = NotificationCenter.default.addObserver(
            forName: .AVAudioEngineConfigurationChange, object: engine, queue: .main
        ) { [weak self] _ in
            MainActor.assumeIsolated { self?.onConfigurationChange?() }
        }
    }

    func setRate(_ rate: Float) { timePitch.rate = rate }
    func play() throws {
        if !engine.isRunning {
            engine.prepare()
            do { try engine.start() } catch { throw AudioCoreError.outputFailed }
        }
        if !player.isPlaying { player.play() }
    }
    func pause() { player.pause(); advancing = false }
    func stop() {
        revision = UUID()
        player.stop()
        advancing = false; lastRendered = 0
        timeline = AudioRenderTimeline()
    }
    func schedule(_ chunk: AudioPCMChunk, completion: @escaping @MainActor () -> Void) throws {
        guard let buffer = AVAudioPCMBuffer(pcmFormat: format, frameCapacity: AVAudioFrameCount(chunk.frameCount)),
              let channels = buffer.floatChannelData else { throw AudioCoreError.allocationFailed }
        buffer.frameLength = buffer.frameCapacity
        for frame in 0..<chunk.frameCount {
            channels[0][frame] = chunk.samples[frame * 2]
            channels[1][frame] = chunk.samples[frame * 2 + 1]
        }
        let id = UUID(), revision = revision
        let start = timeline.schedule(chunk, id: id, currentSampleTime: sampleTime ?? 0)
        let time = AVAudioTime(sampleTime: start, atRate: format.sampleRate)
        player.scheduleBuffer(buffer, at: time, completionCallbackType: .dataPlayedBack) { [weak self] _ in
            Task { @MainActor [weak self] in
                guard let self, self.revision == revision else { return }
                self.timeline.complete(id)
                completion()
            }
        }
    }
    func close() {
        if let notification { NotificationCenter.default.removeObserver(notification); self.notification = nil }
        onConfigurationChange = nil
        stop(); engine.stop()
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
