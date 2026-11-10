import Foundation

/// One decoder and one synchronized output, including its silent candidate
/// stage. Only the injected PlaybackController may commit/unmute and run it.
@MainActor
public final class VideoSession {
    public let output = VideoSampleOutput()
    public private(set) var info: VideoStreamInfo?
    public var onFailure: ((VideoKernelFailure) -> Void)?
    public var onEnd: (() -> Void)?
    public var onProgress: ((Double) -> Void)?
    public var onTransportRate: ((Float) -> Void)?
    private let decoder: VideoDecoder
    private var firstVideo: VideoSample?
    private var leadingAudio: [VideoSample] = []
    private var videoFrames = VideoFrameBuffer(maximumBytes: 64 * 1024 * 1024, maximumCount: 8)
    private var audioFrames = VideoFrameBuffer(maximumBytes: 1024 * 1024, maximumCount: 64)
    private var producer: Task<Void, Never>?
    private var videoConsumer: Task<Void, Never>?
    private var audioConsumer: Task<Void, Never>?
    private var observer: Any?
    private var rateObservation: NSKeyValueObservation?
    private var endTime: Double?
    private var videoFinished = false
    private var audioFinished = false
    private var running = false
    private var cancelled = false
    private var closed = false
    private var failure: VideoKernelFailure?
    private var desiredRate: Float = 0
    private var seekOperation: Task<Double, Error>?
    private var seekRevision = UUID()
    private var closeTask: Task<Void, Never>?
    private var resumingTask: Task<Void, Never>?
    // Internal timing witness; tests cancel before the operation can start.
    var onSeekInterrupted: (() -> Void)?

    public init() throws {
        decoder = try VideoDecoder()
        output.onFailure = { [weak self] error in self?.report(error) }
    }
    public var position: Double { output.position }

    public func prepare(url: URL, format: String?, clearKeys: [String: Data],
                        position: Double) async throws {
        try check()
        info = try await decoder.open(url: url, format: format, clearKeys: clearKeys)
        try check()
        output.configure(hasAudio: info?.hasAudio == true)
        if position > 0 { try await decoder.seek(to: position) }
        try await decodeFirstFrame()
    }

    private func decodeFirstFrame() async throws {
        firstVideo = nil; leadingAudio.removeAll()
        let deadline = ContinuousClock.now.advanced(by: .seconds(15))
        var audioBytes = 0
        while ContinuousClock.now < deadline {
            try check()
            guard let sample = try await decoder.read() else { throw VideoKernelFailure.formatFailed }
            if sample.kind == .video {
                firstVideo = sample
                leadingAudio.removeAll { $0.endTime <= sample.position }
                info = try await decoder.info()
                return
            }
            leadingAudio.append(sample); audioBytes += sample.byteCount
            // Only a bounded audio pre-roll is needed before the first picture.
            // This is a queue resource budget, not a media/format admission cap.
            while audioBytes > 4 * 1024 * 1024, leadingAudio.count > 1 {
                audioBytes -= leadingAudio.removeFirst().byteCount
            }
        }
        throw VideoKernelFailure.timedOut
    }

    public func renderFirstVideoFrame() async throws {
        try check()
        guard let firstVideo else { throw VideoKernelFailure.outputFailed }
        try await output.renderFirstVideoFrame(firstVideo)
        try check()
    }

    public func align(to position: Double) async throws {
        try check()
        decoder.interrupt()
        try await decoder.seek(to: position)
        try check()
        try await decodeFirstFrame()
    }

    public func commit(rate: Float, volume: Float) {
        guard !cancelled, !closed, firstVideo != nil else { return }
        output.commit(volume: volume)
        startFeeding()
        setRate(rate)
        observer = output.synchronizer.addPeriodicTimeObserver(
            forInterval: .init(seconds: 0.5, preferredTimescale: 600), queue: .main
        ) { [weak self] _ in
            MainActor.assumeIsolated {
                guard let self, !self.cancelled else { return }
                self.onProgress?(self.output.position)
                if let endTime = self.endTime, self.videoFinished, self.audioFinished,
                   self.output.position >= endTime {
                    self.endTime = nil; self.setRate(0); self.onEnd?()
                }
            }
        }
        rateObservation = output.synchronizer.observe(\.rate, options: [.initial, .new]) { [weak self] _, _ in
            Task { @MainActor [weak self] in
                guard let self, !self.cancelled else { return }
                self.onTransportRate?(self.output.rate)
            }
        }
    }

    public func setRate(_ rate: Float) {
        guard !cancelled, !closed else { return }
        desiredRate = rate
        guard rate > 0, !running else {
            output.setRate(rate); return
        }
        // A live seek consumes the latest desiredRate when its own target is
        // ready. Starting a resume here would cancel that user-selected target.
        if let seekOperation, !seekOperation.isCancelled { return }
        // A cancelled native seek can have interrupted demux and torn down its
        // queues. An explicit resume must re-establish real feeding at the
        // displayed time before running the clock; rate alone would fake play.
        if resumingTask == nil {
            let revision = seekRevision
            resumingTask = Task { [weak self] in
                guard let self else { return }
                defer { self.resumingTask = nil }
                guard self.seekRevision == revision else { return }
                do { _ = try await self.seek(to: self.position) }
                catch { self.report(error) }
            }
        }
    }

    public func seek(to position: Double) async throws -> Double {
        try check()
        let revision = UUID(); seekRevision = revision
        let previous = seekOperation; previous?.cancel()
        decoder.interrupt()
        running = false; endTime = nil
        output.setRate(0)
        onSeekInterrupted?()
        let operation = Task { [self] in
            defer {
                if seekRevision == revision { seekOperation = nil }
            }
            if let previous { _ = await previous.result }
            // Interruption has already stopped native reads. Even an operation
            // cancelled before it starts must retire that feeding generation.
            await stopFeeding()
            try check()
            guard seekRevision == revision else { throw VideoKernelFailure.cancelled }
            return try await performSeek(to: position)
        }
        seekOperation = operation
        return try await withTaskCancellationHandler {
            try await operation.value
        } onCancel: {
            operation.cancel()
        }
    }

    private func performSeek(to position: Double) async throws -> Double {
        try await decoder.seek(to: position)
        try await decodeFirstFrame()
        await output.stopFeeding(removingImage: false)
        try check()
        guard let firstVideo else { throw VideoKernelFailure.seekFailed }
        output.resetTime(to: firstVideo.position)
        try await output.enqueue(firstVideo)
        try check()
        startFeeding()
        // A newer pause/rate command received while seeking wins.
        output.setRate(desiredRate)
        return firstVideo.position
    }

    private func startFeeding() {
        guard !running, !cancelled else { return }
        running = true; endTime = nil; videoFinished = false; audioFinished = false
        videoFrames = VideoFrameBuffer(maximumBytes: 64 * 1024 * 1024, maximumCount: 8)
        audioFrames = VideoFrameBuffer(maximumBytes: 1024 * 1024, maximumCount: 64)
        let videoFrames = videoFrames, audioFrames = audioFrames, output = output
        var finalTime = firstVideo?.endTime ?? position
        let audioPrelude = leadingAudio; leadingAudio.removeAll()
        producer = Task { [weak self, decoder] in
            do {
                for sample in audioPrelude { try await audioFrames.push(sample); finalTime = max(finalTime, sample.endTime) }
                while let sample = try await decoder.read() {
                    try Task.checkCancellation()
                    finalTime = max(finalTime, sample.endTime)
                    try await (sample.kind == .video ? videoFrames : audioFrames).push(sample)
                }
                videoFrames.finish(); audioFrames.finish()
                guard !Task.isCancelled, let self, !self.cancelled else { return }
                self.endTime = finalTime
            } catch {
                videoFrames.finish(error); audioFrames.finish(error)
                self?.report(error)
            }
        }
        videoConsumer = Task { [weak self] in
            do {
                while let sample = try await videoFrames.pop() { try await output.enqueue(sample) }
                if !Task.isCancelled { self?.videoFinished = true }
            }
            catch { self?.report(error) }
        }
        audioConsumer = Task { [weak self] in
            do {
                while let sample = try await audioFrames.pop() { try await output.enqueue(sample) }
                if !Task.isCancelled { self?.audioFinished = true }
            }
            catch { self?.report(error) }
        }
    }

    private func report(_ error: Error) {
        guard !(error is CancellationError), !cancelled, !Task.isCancelled, failure == nil else { return }
        let value = error as? VideoKernelFailure ?? .decodeFailed
        if value == .cancelled || value == .interrupted { return }
        failure = value
        setRate(0)
        onFailure?(value)
    }

    private func stopFeeding() async {
        running = false; endTime = nil
        let tasks = [producer, videoConsumer, audioConsumer].compactMap { $0 }
        producer = nil; videoConsumer = nil; audioConsumer = nil
        tasks.forEach { $0.cancel() }
        videoFrames.finish(VideoKernelFailure.cancelled); audioFrames.finish(VideoKernelFailure.cancelled)
        await output.stopFeeding(removingImage: false)
        for task in tasks { await task.value }
    }

    public func cancel() {
        cancelled = true
        seekRevision = UUID(); seekOperation?.cancel()
        resumingTask?.cancel()
        decoder.cancel()
        producer?.cancel(); videoConsumer?.cancel(); audioConsumer?.cancel()
        videoFrames.finish(VideoKernelFailure.cancelled); audioFrames.finish(VideoKernelFailure.cancelled)
        output.cancel()
    }

    public func close() async {
        if closeTask == nil {
            closed = true; cancel()
            onFailure = nil; onEnd = nil; onProgress = nil; onTransportRate = nil
            onSeekInterrupted = nil
            rateObservation?.invalidate(); rateObservation = nil
            if let observer { output.synchronizer.removeTimeObserver(observer); self.observer = nil }
            closeTask = Task {
                if let seekOperation = self.seekOperation { _ = await seekOperation.result; self.seekOperation = nil }
                if let resumingTask = self.resumingTask { await resumingTask.value; self.resumingTask = nil }
                await self.stopFeeding()
                await self.decoder.close()
                await self.output.close()
                self.firstVideo = nil; self.leadingAudio.removeAll(); self.info = nil
            }
        }
        await closeTask!.value
    }

    private func check() throws {
        try Task.checkCancellation()
        if let failure { throw failure }
        if cancelled || closed { throw VideoKernelFailure.cancelled }
    }
}
