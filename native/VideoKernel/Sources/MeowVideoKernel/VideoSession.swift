import Foundation
import AVFoundation
import CoreGraphics
@preconcurrency import CoreVideo
import CMeowVideoFFmpeg

final class VideoLogSinkBox: @unchecked Sendable {
    static let shared = VideoLogSinkBox()
    private let lock = NSLock()
    private var handler: VideoSession.LogHandler?

    func set(_ handler: VideoSession.LogHandler?) {
        lock.lock()
        self.handler = handler
        lock.unlock()
        if handler != nil {
            mvk_set_log_sink { level, messagePtr in
                guard let messagePtr else { return }
                let msg = String(cString: messagePtr)
                VideoLogSinkBox.shared.emit(level: level, message: msg)
            }
        } else {
            mvk_set_log_sink(nil)
        }
    }

    func emit(level: Int32, message: String) {
        lock.lock()
        let sink = handler
        lock.unlock()
        sink?(level, message)
    }
}

private final class VideoSessionCleanupBox: @unchecked Sendable {
    private let lock = NSLock()
    private var engine: PlaybackEngine?
    private var output: VideoOutput?
    private var closed = false

    func attachOutput(_ output: VideoOutput) {
        lock.lock()
        if !closed {
            self.output = output
        }
        lock.unlock()
    }

    func attach(_ engine: PlaybackEngine) {
        lock.lock()
        if !closed {
            self.engine = engine
        }
        lock.unlock()
    }

    func clear() {
        lock.lock()
        closed = true
        engine = nil
        output = nil
        lock.unlock()
    }

    func takeForDeinit() -> (engine: PlaybackEngine?, output: VideoOutput?) {
        lock.lock()
        defer { lock.unlock() }
        guard !closed else { return (nil, nil) }
        closed = true
        let takenEngine = engine
        let takenOutput = output
        engine = nil
        output = nil
        return (takenEngine, takenOutput)
    }
}

/// Main-actor session boundary over the self-owned FFmpeg 8.1.2 + VideoToolbox + AVSampleBufferDisplayLayer kernel.
/// Owns decoding, rendering, sync, tracks, subtitles, chapters and diagnostics; never owns business sessions or AVAudioSession.
@MainActor
public final class VideoSession {
    public typealias LogHandler = @Sendable (_ level: Int32, _ message: String) -> Void

    public nonisolated static func setLogHandler(_ handler: LogHandler?) {
        VideoLogSinkBox.shared.set(handler)
    }

    public let output = VideoOutput()
    public private(set) var info: VideoStreamInfo?
    public var onFailure: (@MainActor (VideoKernelFailure) -> Void)?
    public var onEnd: (@MainActor () -> Void)?
    public var onProgress: (@MainActor (Double) -> Void)?
    public var onTransportRate: (@MainActor (Float) -> Void)?
    public var onBuffering: (@MainActor (Bool) -> Void)?

    private nonisolated let cleanupBox = VideoSessionCleanupBox()
    private let presentGate = PresentGate()
    private var engine: PlaybackEngine?
    private var preparation: Task<Void, Error>?
    private var seekOperation: Task<Double, Error>?
    private var recoveryTask: Task<Void, Never>?
    private var progressTask: Task<Void, Never>?
    private var closeTask: Task<Void, Never>?
    private var seekRevision = UUID()
    private var confirmedPosition: Double = 0
    private var needsRecoverySeek = false
    private var desiredRate: Float = 0
    private var committed = false
    private var cancelled = false
    private var failure: VideoKernelFailure?

    public init() throws {
        cleanupBox.attachOutput(output)
    }

    deinit {
        let (engine, output) = cleanupBox.takeForDeinit()
        if let engine {
            engine.cancel()
            DispatchQueue.global(qos: .utility).async {
                engine.closeSync()
            }
        }
        if let output {
            if Thread.isMainThread {
                MainActor.assumeIsolated {
                    output.closeSync()
                }
            } else {
                DispatchQueue.main.async {
                    MainActor.assumeIsolated {
                        output.closeSync()
                    }
                }
            }
        }
    }

    /// Side-effect-free position read: preserves requested resume position until first frame commits.
    public var position: Double {
        guard seekOperation == nil, output.hasPresentedFrame, let engine else {
            return confirmedPosition
        }
        let value = engine.position
        return value.isFinite ? max(0, value) : confirmedPosition
    }

    public var bufferedPosition: Double {
        engine?.bufferedPosition ?? confirmedPosition
    }

    public var isBuffering: Bool {
        !cancelled && (engine?.isBuffering ?? false)
    }

    public var videoSize: CGSize {
        guard let info, info.width > 0, info.height > 0 else { return .zero }
        return CGSize(width: info.width, height: info.height)
    }

    public var seekable: Bool {
        guard !cancelled, failure == nil, let engine else { return false }
        return engine.seekable
    }

    public var chapters: [VideoChapter] { info?.chapters ?? [] }
    public var tracks: [VideoTrack] { engine?.tracks ?? [] }
    public var diagnostics: VideoKernelDiagnostics? { engine?.diagnostics }

    public func prepare(url: URL, format: String?, clearKeys: [String: Data],
                        position: Double, headers: [String: String] = [:]) async throws {
        try check()
        guard engine == nil else { throw VideoKernelFailure.openFailed }
        confirmedPosition = position.isFinite ? max(0, position) : 0

        let core = PlaybackEngine()
        engine = core
        cleanupBox.attach(core)
        wireEngine(core)

        let task = Task<VideoStreamInfo, Error> {
            try await Task.detached(priority: .userInitiated) {
                try core.open(url: url, format: format, clearKeys: clearKeys,
                              startPosition: position, headers: headers)
            }.value
        }
        let deadline = Task { @MainActor [weak self] in
            do { try await Task.sleep(for: .seconds(15)) } catch { return }
            guard let self, !self.cancelled else { return }
            self.failure = .timedOut
            core.cancel()
            task.cancel()
        }
        preparation = Task {
            _ = try await task.value
        }
        defer {
            deadline.cancel()
            preparation = nil
        }

        do {
            let streamInfo = try await withTaskCancellationHandler {
                try await task.value
            } onCancel: {
                deadline.cancel()
                core.cancel()
                task.cancel()
                Task { @MainActor [weak self] in self?.cancel() }
            }
            try check()
            info = streamInfo
            output.applyGeometry(rotationDegrees: streamInfo.rotationDegrees,
                                 hflip: streamInfo.hflip, vflip: streamInfo.vflip)
            output.onDisplayLayerChanged?()
        } catch {
            if failure == .timedOut { throw VideoKernelFailure.timedOut }
            if cancelled || Task.isCancelled || error is CancellationError {
                throw VideoKernelFailure.cancelled
            }
            let mapped = error as? VideoKernelFailure ?? .openFailed
            failure = mapped
            core.cancel()
            throw mapped
        }
    }

    public func renderFirstVideoFrame() async throws {
        try check()
        do { try await output.renderFirstVideoFrame(timeoutSeconds: 15) }
        catch {
            try check()
            throw error as? VideoKernelFailure ?? .outputFailed
        }
        try check()
        if let engine { confirmedPosition = max(0, engine.position) }
    }

    public func align(to position: Double) async throws {
        try check()
        if !seekable {
            try await renderFirstVideoFrame()
            return
        }
        _ = try await seek(to: position)
    }

    public func validateForCommit() throws {
        try check()
        guard output.hasPresentedFrame, engine != nil, info != nil else {
            throw VideoKernelFailure.outputFailed
        }
    }

    public func commit(rate: Float, volume: Float) {
        guard !cancelled, failure == nil, output.hasPresentedFrame, let engine else { return }
        committed = true
        desiredRate = rate.isFinite ? rate : 0
        engine.commit(rate: desiredRate, volume: volume)
        publishRate()
        if desiredRate > 0 { startProgressLoop() }
    }

    public func setRate(_ rate: Float) {
        guard !cancelled, rate.isFinite else { return }
        desiredRate = rate
        guard committed, let engine else { return }
        if rate <= 0 {
            recoveryTask?.cancel()
            recoveryTask = nil
            guard seekOperation == nil else { return }
            engine.setRate(0)
            stopProgressLoop()
            publishRate()
            return
        }
        if seekOperation != nil || recoveryTask != nil { return }
        if needsRecoverySeek && seekable {
            let recoveryPos = confirmedPosition
            recoveryTask = Task { @MainActor [weak self] in
                guard let self else { return }
                defer { self.recoveryTask = nil }
                guard !self.cancelled, self.committed, self.desiredRate > 0 else { return }
                do {
                    _ = try await self.seek(to: recoveryPos)
                } catch {
                    if !self.cancelled && self.committed && self.desiredRate > 0 && !self.needsRecoverySeek {
                        self.engine?.setRate(self.desiredRate)
                        self.startProgressLoop()
                        self.publishRate()
                    }
                }
            }
            return
        }
        engine.setRate(rate)
        startProgressLoop()
        publishRate()
    }

    public func seek(to position: Double) async throws -> Double {
        try check()
        guard seekable, position.isFinite, position >= 0, let engine else {
            throw VideoKernelFailure.seekFailed
        }
        recoveryTask?.cancel()
        recoveryTask = nil
        let previous = seekOperation
        previous?.cancel()
        let revision = UUID()
        seekRevision = revision

        let wasPlaying = committed && desiredRate > 0
        if wasPlaying {
            engine.setRate(0)
            stopProgressLoop()
        }

        let operation = Task<Double, Error> { [self] in
            if let previous { _ = await previous.result }
            try check()
            guard seekRevision == revision else { throw VideoKernelFailure.cancelled }
            // Mark recovery before mutating FFmpeg stream position so any cancellation (including
            // chained supersede + cancel) cannot resume from an unconfirmed target.
            if committed { needsRecoverySeek = true }
            let ticket = try engine.beginSeek(to: position)
            presentGate.setMinimumSerial(ticket.videoSerial)
            output.invalidateFrame()

            _ = try await engine.waitSeek(ticket)
            try check()
            guard seekRevision == revision else { throw VideoKernelFailure.cancelled }

            try await output.renderFirstVideoFrame(timeoutSeconds: 15)
            try check()
            guard seekRevision == revision else { throw VideoKernelFailure.cancelled }

            needsRecoverySeek = false
            let actual = engine.position
            confirmedPosition = actual.isFinite ? max(0, actual) : max(0, position)
            if committed && desiredRate > 0 {
                engine.setRate(desiredRate)
                startProgressLoop()
            }
            publishRate()
            return confirmedPosition
        }
        seekOperation = operation
        defer { if seekRevision == revision { seekOperation = nil } }

        return try await withTaskCancellationHandler {
            try await operation.value
        } onCancel: {
            operation.cancel()
        }
    }

    public func selectTrack(_ id: Int32, kind: VideoTrack.Kind) async throws {
        try check()
        guard let engine else { throw VideoKernelFailure.formatFailed }
        try await engine.selectTrack(id: id, kind: kind)
    }

    public func disableSubtitle() {
        engine?.disableSubtitleAsync()
        output.updateSubtitle(nil)
    }

    public func cancel() {
        guard !cancelled else { return }
        cancelled = true
        committed = false
        needsRecoverySeek = false
        stopProgressLoop()
        recoveryTask?.cancel()
        recoveryTask = nil
        preparation?.cancel()
        seekOperation?.cancel()
        output.cancel()
        presentGate.cancel()
        engine?.cancel()
    }

    public func close() async {
        if let closeTask { await closeTask.value; return }
        cancel()
        let engine = engine, prep = preparation, seeking = seekOperation, recovery = recoveryTask
        let task = Task { [self] in
            _ = await recovery?.value
            _ = await prep?.result
            _ = await seeking?.result
            if let engine {
                await Task.detached(priority: .utility) {
                    engine.closeSync()
                }.value
            }
            await output.close()
            cleanupBox.clear()
            self.engine = nil
            self.info = nil
            self.onFailure = nil; self.onEnd = nil
            self.onProgress = nil; self.onTransportRate = nil; self.onBuffering = nil
        }
        closeTask = task
        await task.value
    }

    private final class PresentGate: @unchecked Sendable {
        private let lock = NSLock()
        private var inflight = 0
        private var minSerial = 0
        private var cancelled = false

        func setMinimumSerial(_ serial: Int) {
            lock.lock()
            if serial > minSerial { minSerial = serial }
            lock.unlock()
        }

        func cancel() {
            lock.lock()
            cancelled = true
            lock.unlock()
        }

        func tryEnter(serial: Int) -> Bool {
            lock.lock()
            defer { lock.unlock() }
            guard !cancelled, serial >= minSerial, inflight < 2 else { return false }
            inflight += 1
            return true
        }

        func isAccepted(_ serial: Int) -> Bool {
            lock.lock()
            defer { lock.unlock() }
            return !cancelled && serial >= minSerial
        }

        func leave() {
            lock.lock()
            inflight = max(0, inflight - 1)
            lock.unlock()
        }
    }

    // MARK: - Internal Wiring

    private func wireEngine(_ core: PlaybackEngine) {
        output.readRate = { [weak core] in core?.activeRate ?? 0 }
        output.readVolume = { [weak core] in core?.volume ?? 0 }
        output.writeVolume = { [weak core] val in core?.volume = val }
        output.onNeedsFlushRecovery = { [weak self] in
            guard let self, !self.cancelled, let pb = self.output.pixelBuffer else { return }
            _ = self.output.enqueue(pixelBuffer: pb, pts: self.position, displayImmediately: true)
        }
        let presentGate = self.presentGate
        core.presentFrame = { [weak output] pixelBuffer, pts, immediate, subtitle, serial, onResult in
            guard presentGate.tryEnter(serial: serial) else { return false }
            DispatchQueue.main.async {
                MainActor.assumeIsolated {
                    defer { presentGate.leave() }
                    guard presentGate.isAccepted(serial), let output else {
                        onResult?(false)
                        return
                    }
                    let ok = output.enqueue(pixelBuffer: pixelBuffer, pts: pts, displayImmediately: immediate)
                    if ok {
                        output.updateSubtitle(subtitle)
                    }
                    onResult?(ok)
                }
            }
            return true
        }
        core.onGeometryChanged = { [weak self] rot, hflip, vflip, width, height in
            Task { @MainActor [weak self] in
                guard let self, !self.cancelled else { return }
                if let current = self.info {
                    self.info = VideoStreamInfo(
                        duration: current.duration,
                        codec: current.codec,
                        bitDepth: current.bitDepth,
                        width: width,
                        height: height,
                        rotationDegrees: rot,
                        hflip: hflip,
                        vflip: vflip,
                        hasAudio: current.hasAudio,
                        sampleRate: current.sampleRate,
                        audioChannels: current.audioChannels,
                        isHDR: current.isHDR,
                        dolbyVisionProfile: current.dolbyVisionProfile,
                        chapters: current.chapters
                    )
                }
                self.output.applyGeometry(rotationDegrees: rot, hflip: hflip, vflip: vflip)
            }
        }
        core.onFailure = { [weak self] err in
            Task { @MainActor [weak self] in
                guard let self, !self.cancelled, self.failure == nil else { return }
                self.failure = err
                self.stopProgressLoop()
                self.output.failFirstFrameWaiters(err)
                if self.committed { self.onFailure?(err) }
            }
        }
        core.onEnd = { [weak self] in
            Task { @MainActor [weak self] in
                guard let self, !self.cancelled else { return }
                self.desiredRate = 0
                self.stopProgressLoop()
                if let dur = self.info?.duration { self.confirmedPosition = dur }
                if self.committed {
                    self.publishRate()
                    self.onEnd?()
                } else {
                    self.output.failFirstFrameWaiters(.formatFailed)
                }
            }
        }
        core.onBufferingChanged = { [weak self] isBuffering in
            Task { @MainActor [weak self] in
                guard let self, !self.cancelled else { return }
                self.onBuffering?(isBuffering)
                self.publishRate()
            }
        }
    }

    private func startProgressLoop() {
        guard progressTask == nil, !cancelled, committed else { return }
        progressTask = Task { [weak self] in
            while !Task.isCancelled {
                do { try await Task.sleep(for: .milliseconds(500)) } catch { return }
                guard let self, !self.cancelled, self.committed else { return }
                let pos = self.position
                self.confirmedPosition = pos
                self.onProgress?(pos)
            }
        }
    }

    private func stopProgressLoop() {
        progressTask?.cancel()
        progressTask = nil
    }

    private func publishRate() {
        guard committed, !cancelled else { return }
        onTransportRate?(output.rate)
    }

    private func check() throws {
        try Task.checkCancellation()
        if cancelled { throw VideoKernelFailure.cancelled }
        if let failure { throw failure }
    }
}
