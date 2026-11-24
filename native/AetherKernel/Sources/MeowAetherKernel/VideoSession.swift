import Foundation
import Combine
import IOSurface
@preconcurrency import AVFoundation
internal import AetherEngine
#if os(iOS)
import UIKit
#else
import AppKit
#endif

public enum AetherVideoFailure: Error, Equatable, Sendable {
    case cancelled, unsupportedFormat, inputFailed, outputFailed, timedOut, seekFailed
}

public struct AetherVideoChapter: Equatable, Sendable {
    public let title: String
    public let start: Double
    public let end: Double
}

public struct AetherVideoTrack: Equatable, Sendable {
    public enum Kind: Equatable, Sendable { case audio, subtitle }
    public let id: Int
    public let kind: Kind
    public let name: String
    public let codec: String
    public let language: String?
    public let isEnabled: Bool
}

public struct AetherVideoDiagnostics: Equatable, Sendable {
    public let videoFormat: String
    public let dolbyVisionProfile: Int?
    public let bufferedSeconds: Double
    public let isBuffering: Bool
    public let hardwareDecoding: Bool
}

private final class AetherLogSinkBox: @unchecked Sendable {
    static let shared = AetherLogSinkBox()
    private let lock = NSLock()
    private var handler: AetherVideoSession.LogHandler?

    func set(_ handler: AetherVideoSession.LogHandler?) {
        lock.lock()
        self.handler = handler
        lock.unlock()
        if handler != nil {
            EngineLog.structuredHandler = Self.makeNonisolatedBridge()
        } else {
            EngineLog.structuredHandler = nil
        }
    }

    /// Created in a nonisolated static context so Swift 6 never attaches @MainActor isolation
    /// to EngineLog.structuredHandler (which executes on FFmpeg / demux / muxer background threads).
    private static func makeNonisolatedBridge() -> (String, EngineLog.Category, EngineLog.Level) -> Void {
        return { line, category, _ in
            AetherLogSinkBox.shared.emit(category: category.rawValue, line: line)
        }
    }

    private func emit(category: String, line: String) {
        lock.lock()
        let sink = handler
        lock.unlock()
        guard let sink else { return }
        sink(category, Self.redact(line))
    }

    /// Strips URLs, Cookie/Authorization/token headers and query secrets from Aether internal logs
    /// before forwarding them into `--debug` (matches MeowVideoKernel and Windows FilmKernel redaction).
    private static func redact(_ input: String) -> String {
        var text = input
        // Replace full URLs (http/https/rtsp/rtmp/file) up to whitespace or delimiter.
        let urlRange = NSRange(text.startIndex..<text.endIndex, in: text)
        text = urlRegex.stringByReplacingMatches(in: text, options: [], range: urlRange, withTemplate: "<redacted-url>")
        // Replace sensitive header/credential assignments (Cookie, Authorization, Set-Cookie, token, key, sig, auth).
        let credRange = NSRange(text.startIndex..<text.endIndex, in: text)
        text = credRegex.stringByReplacingMatches(in: text, options: [], range: credRange, withTemplate: "$1=<redacted>")
        return text
    }

    private static let urlRegex = try! NSRegularExpression(
        pattern: #"(?i)\b(?:https?|rtsp|rtmp|file)://[^\s'"\)\]\},>]+"#
    )
    private static let credRegex = try! NSRegularExpression(
        pattern: #"(?i)\b(authorization|proxy-authorization|set-cookie|cookie|x-auth-token|access_token|token|sig|signature|key)\s*[:=]\s*[^\s,;\r\n]+"#
    )
}

private final class AetherSessionCleanupBox: @unchecked Sendable {
    private let lock = NSLock()
    private var engine: AetherEngine?
    private var cancelled = false

    init(engine: AetherEngine) {
        self.engine = engine
    }

    func markCancelled() {
        lock.lock()
        cancelled = true
        lock.unlock()
    }

    func clear() {
        lock.lock()
        cancelled = true
        engine = nil
        lock.unlock()
    }

    func takeForDeinit() -> AetherEngine? {
        lock.lock()
        defer { lock.unlock() }
        guard !cancelled, let engine else { return nil }
        cancelled = true
        self.engine = nil
        return engine
    }
}

/// The dynamic-library boundary owns Aether's FFmpeg symbols. No upstream type
/// or network credential crosses into the application's playback coordinator.
@MainActor
public final class AetherVideoSession {
    public typealias LogHandler = @Sendable (_ category: String, _ message: String) -> Void

    /// Installs or clears the redacted Aether log sink used in --debug mode.
    public nonisolated static func setLogHandler(_ handler: LogHandler?) {
        AetherLogSinkBox.shared.set(handler)
    }

    private let engine: AetherEngine
    private nonisolated let cleanupBox: AetherSessionCleanupBox
    private let view = AetherPlayerView(frame: .zero)
    private var subscriptions = Set<AnyCancellable>()
    private var preparation: Task<Void, Error>?
    private var seekOperation: Task<Double, Error>?
    private var renderings: [UUID: Task<Void, Error>] = [:]
    private var closing: Task<Void, Never>?
    private var progress: Task<Void, Never>?
    private var cancelled = false
    private var committed = false
    private var presented = false
    private var failure: AetherVideoFailure?
    private var seekRevision = UUID()
    private var lastLayer: ObjectIdentifier?
    private var lastRate: Float = 0
    private var desiredGravity: AVLayerVideoGravity = .resizeAspect
    private var precedingDisplayedSurfaceID: IOSurfaceID?
    private var activeSeekState: ActiveSeekOutcome = .none
    private var firstFrameWaiters: [UUID: CheckedContinuation<Void, Never>] = [:]

    private enum ActiveSeekOutcome {
        case none
        case inFlight
        case landed
        case failed
    }

    public var onFailure: (@MainActor (AetherVideoFailure) -> Void)?
    public var onProgress: (@MainActor (Double) -> Void)?
    public var onTransportRate: (@MainActor (Float) -> Void)?
    public var onPlaybackIntent: (@MainActor (Bool) -> Void)?
    public var onEnd: (@MainActor () -> Void)?
    public var onDisplayLayerChanged: (() -> Void)?

    public var position: Double {
        let value = engine.currentTime
        return value.isFinite ? max(0, value) : 0
    }
    public var duration: Double? {
        engine.duration.isFinite && engine.duration > 0 ? engine.duration : nil
    }
    public var bufferedPosition: Double {
        let value = engine.bufferedPosition
        return value.isFinite ? max(0, value) : 0
    }
    public var isBuffering: Bool { !cancelled && engine.isBuffering }
    public var videoSize: CGSize {
        let w = Int(engine.sourceVideoWidth), h = Int(engine.sourceVideoHeight)
        return w > 0 && h > 0 ? CGSize(width: w, height: h) : .zero
    }
    public var seekable: Bool {
        !cancelled && failure == nil && engine.canSeek
    }
    public var chapters: [AetherVideoChapter] {
        engine.mediaChapters.compactMap {
            let end = $0.startSeconds + $0.durationSeconds
            guard $0.startSeconds.isFinite, $0.startSeconds >= 0, end.isFinite, end > $0.startSeconds else { return nil }
            return AetherVideoChapter(title: $0.name, start: $0.startSeconds, end: end)
        }
    }
    public var audioTracks: [AetherVideoTrack] {
        let active = engine.activeAudioTrackIndex
        return engine.audioTracks.map {
            AetherVideoTrack(id: $0.id, kind: .audio, name: $0.name, codec: $0.codec,
                             language: $0.language, isEnabled: active == $0.id)
        }
    }
    public var subtitleTracks: [AetherVideoTrack] {
        let active = engine.activeSubtitleTrackIndex
        return engine.subtitleTracks.map {
            AetherVideoTrack(id: $0.id, kind: .subtitle, name: $0.name, codec: $0.codec,
                             language: $0.language, isEnabled: active == $0.id)
        }
    }
    public var diagnostics: AetherVideoDiagnostics {
        AetherVideoDiagnostics(
            videoFormat: String(describing: engine.videoFormat),
            dolbyVisionProfile: engine.sourceDVProfile,
            bufferedSeconds: bufferedPosition,
            isBuffering: isBuffering,
            hardwareDecoding: playerLayer != nil
        )
    }
    public var volume: Float {
        get { engine.volume }
        set { engine.volume = newValue.isFinite ? min(1, max(0, newValue)) : 0 }
    }
    public var rate: Float { cancelled || !committed ? 0 : engine.outputRate }
    public var playerLayer: AVPlayerLayer? { engine.nativePlayerLayer }
    public var displayLayer: AVSampleBufferDisplayLayer? { engine.softwarePiPSource?.layer }
    #if os(iOS)
    public var presentationView: UIView { view }
    #else
    public var presentationView: NSView { view }
    #endif

    public init() throws {
        let created = try AetherEngine(hostManagedLifecycle: true)
        engine = created
        cleanupBox = AetherSessionCleanupBox(engine: created)
        engine.ownsAudioNowPlaying = false
        engine.ownsVideoNowPlayingSession = false
        engine.ownsVideoNowPlayingCommands = false
        engine.ownsSoftwareNowPlaying = false
        engine.rendererAudioSessionActivationEnabled = false
        engine.deactivatesAudioSessionOnStop = false
        engine.backgroundPlaybackEnabled = false
        engine.retainsVideoPipelineWhenBackgrounded = true
        engine.volume = 0
        engine.bind(view: view)
        engine.onExternalPlaybackIntent = { [weak self] playing in
            guard let self, self.committed, !self.cancelled else { return }
            self.onPlaybackIntent?(playing)
        }
        engine.$state.sink { [weak self] state in
            guard let self, !self.cancelled else { return }
            self.notifyLayer()
            self.publishRateIfChanged()
            if case .error = state {
                let error = Self.classify(info: self.engine.errorInfo)
                self.failure = error
                self.wakeFirstFrameWaiters()
                if self.committed { self.onFailure?(error) }
            } else if state == .ended, self.committed {
                self.stopProgressLoop()
                self.onEnd?()
            } else if state == .playing, self.committed {
                self.startProgressLoop()
            } else if state == .paused {
                self.stopProgressLoop()
            }
        }.store(in: &subscriptions)
        engine.$isBuffering.sink { [weak self] _ in
            guard let self, !self.cancelled else { return }
            self.publishRateIfChanged()
        }.store(in: &subscriptions)
        engine.$hasFirstFrameReadyForDisplay.sink { [weak self] ready in
            guard let self, !self.cancelled, ready else { return }
            self.notifyLayer()
            self.wakeFirstFrameWaiters()
        }.store(in: &subscriptions)
        engine.seekEvents.sink { [weak self] event in
            guard let self, !self.cancelled, event.origin == .programmatic else { return }
            switch event.outcome {
            case .landed:
                self.activeSeekState = .landed
                self.wakeFirstFrameWaiters()
            case .stalled, .rejected:
                self.activeSeekState = .failed
                self.wakeFirstFrameWaiters()
            case .began, .superseded:
                break
            }
        }.store(in: &subscriptions)
    }

    deinit {
        if let engine = cleanupBox.takeForDeinit() {
            Task { @MainActor in await engine.stopAndDrain() }
        }
    }

    public func prepare(url: URL, format: String?, clearKeys: [String: Data],
                        position: Double, headers: [String: String]) async throws {
        try check()
        guard clearKeys.isEmpty else { throw AetherVideoFailure.unsupportedFormat }
        let transportManaged: Set<String> = [
            "connection", "keep-alive", "te", "trailer", "transfer-encoding",
            "upgrade", "expect", "range", "accept-encoding", "if-range", "content-length", "host"
        ]
        var options = LoadOptions()
        options.autoplay = false
        options.httpHeaders = headers.filter { !transportManaged.contains($0.key.lowercased()) }
        options.displayCriteriaOwner = .host
        // Prefer Aether's loopback pipeline when supported; if Aether reroutes standard HLS onto
        // NativeAVPlayerHost, runtime-host.patch keeps AVURLAssetHTTPCookiesKey empty and all
        // Aether URLSessions cookie-isolated.
        options.nativeRemoteHLS = false
        let engine = engine
        let task = Task<Void, Error> {
            do {
                _ = try await engine.load(url: url, startPosition: position, options: options)
            } catch {
                if error is CancellationError || Task.isCancelled { throw AetherVideoFailure.cancelled }
                if AetherEngine.mediaCapabilityUnsupported(error) { throw AetherVideoFailure.unsupportedFormat }
                throw Self.classify(info: engine.errorInfo)
            }
        }
        let deadline = Task { [weak self] in
            do { try await Task.sleep(for: .seconds(15)) } catch { return }
            task.cancel()
            await MainActor.run { [weak self] in
                guard let self, !self.cancelled else { return }
                self.failure = .timedOut
                self.engine.stop()
            }
        }
        preparation = task
        defer {
            deadline.cancel()
            preparation = nil
        }
        try await withTaskCancellationHandler {
            do {
                try await task.value
            } catch {
                if failure == .timedOut { throw AetherVideoFailure.timedOut }
                if cancelled || Task.isCancelled { throw AetherVideoFailure.cancelled }
                throw error
            }
            try check()
            guard playerLayer != nil || displayLayer != nil else { throw AetherVideoFailure.unsupportedFormat }
            engine.pause()
            engine.videoGravity = desiredGravity
            notifyLayer()
        } onCancel: {
            deadline.cancel()
            task.cancel()
            Task { @MainActor [weak self] in self?.cancel() }
        }
    }

    public func align(to position: Double) async throws {
        try check()
        if !seekable {
            try await renderFirstVideoFrame()
            return
        }
        _ = try await seek(to: position)
    }

    public func renderFirstVideoFrame() async throws {
        try await waitForPresentedFrame(requireNewSurface: precedingDisplayedSurfaceID != nil)
    }

    private func waitForPresentedFrame(requireNewSurface: Bool) async throws {
        try check()
        if presented { return }
        let revision = seekRevision
        let task = Task { [self] in
            defer { self.precedingDisplayedSurfaceID = nil }
            let deadline = ContinuousClock.now.advanced(by: .seconds(15))
            // Event-driven wait for engine readiness before inspecting displayed IOSurface.
            if !engine.hasFirstFrameReadyForDisplay {
                await waitForFirstFrameSignal(until: deadline)
            }
            while true {
                try check()
                guard revision == seekRevision else { throw AetherVideoFailure.cancelled }
                if case .error = engine.state {
                    throw Self.classify(info: engine.errorInfo)
                }
                if activeSeekState == .failed {
                    throw AetherVideoFailure.seekFailed
                }
                if let image = displayedPixelBuffer(),
                   CVPixelBufferGetWidth(image) > 0, CVPixelBufferGetHeight(image) > 0 {
                    let currentID = Self.surfaceID(of: image)
                    let surfaceChanged: Bool
                    if !requireNewSurface || displayLayer != nil {
                        // Software path already flushed displayLayer removing old image, so any
                        // non-nil displayedPixelBuffer() is a freshly rendered frame even if the
                        // CVPixelBufferPool recycled the same IOSurfaceID.
                        surfaceChanged = true
                    } else if activeSeekState == .landed {
                        // Native AVPlayerLayer confirmed seek landing via SeekEvent.Outcome.landed.
                        surfaceChanged = true
                    } else if let prevID = precedingDisplayedSurfaceID, let curID = currentID {
                        surfaceChanged = (prevID != curID)
                    } else {
                        surfaceChanged = false
                    }
                    if surfaceChanged {
                        presented = true
                        engine.videoGravity = desiredGravity
                        notifyLayer()
                        return
                    }
                }
                guard ContinuousClock.now < deadline else { throw AetherVideoFailure.timedOut }
                try await Task.sleep(for: .milliseconds(30))
            }
        }
        let renderingID = UUID()
        renderings[renderingID] = task
        defer { renderings[renderingID] = nil }
        try await withTaskCancellationHandler {
            try await task.value
        } onCancel: {
            task.cancel()
            Task { @MainActor [weak self] in
                self?.precedingDisplayedSurfaceID = nil
                self?.wakeFirstFrameWaiters()
            }
        }
        try check()
    }

    private func waitForFirstFrameSignal(until deadline: ContinuousClock.Instant) async {
        let token = UUID()
        let timeoutTask = Task { @MainActor [weak self] in
            let remaining = ContinuousClock.now.duration(to: deadline)
            if remaining > .zero {
                try? await Task.sleep(for: remaining)
            }
            guard let self, let cont = self.firstFrameWaiters.removeValue(forKey: token) else { return }
            cont.resume()
        }
        defer { timeoutTask.cancel() }
        await withTaskCancellationHandler {
            await withCheckedContinuation { (continuation: CheckedContinuation<Void, Never>) in
                if self.cancelled || self.failure != nil || self.engine.hasFirstFrameReadyForDisplay ||
                   self.activeSeekState == .failed {
                    continuation.resume()
                } else {
                    self.firstFrameWaiters[token] = continuation
                }
            }
        } onCancel: {
            Task { @MainActor [weak self] in
                guard let self, let cont = self.firstFrameWaiters.removeValue(forKey: token) else { return }
                cont.resume()
            }
        }
    }

    private func wakeFirstFrameWaiters() {
        let waiters = firstFrameWaiters
        firstFrameWaiters.removeAll()
        for cont in waiters.values {
            cont.resume()
        }
    }

    public func validateForCommit() throws {
        try check()
        guard presented, engine.isSessionReady else { throw AetherVideoFailure.outputFailed }
    }

    public func commit(rate: Float, volume: Float) {
        guard !cancelled, presented, failure == nil else { return }
        committed = true
        self.volume = volume
        engine.videoGravity = desiredGravity
        notifyLayer()
        setRate(rate)
        if rate > 0 { startProgressLoop() }
    }

    public func setRate(_ rate: Float) {
        guard !cancelled, committed, rate.isFinite else { return }
        if rate <= 0 {
            engine.pause()
            stopProgressLoop()
        } else {
            let clamped = max(0.25, min(rate, 2))
            engine.setRate(clamped)
            if engine.state != .playing { engine.play() }
            startProgressLoop()
        }
        publishRateIfChanged()
    }

    public func seek(to position: Double) async throws -> Double {
        try check()
        guard seekable, position.isFinite, position >= 0 else { throw AetherVideoFailure.seekFailed }
        let previous = seekOperation
        previous?.cancel()
        renderings.values.forEach { $0.cancel() }
        let revision = UUID()
        seekRevision = revision
        presented = false
        let operation = Task<Double, Error> { [self] in
            if let previous { _ = await previous.result }
            try check()
            guard seekRevision == revision else { throw AetherVideoFailure.cancelled }
            precedingDisplayedSurfaceID = displayedPixelBuffer().flatMap(Self.surfaceID(of:))
            activeSeekState = .inFlight
            displayLayer?.sampleBufferRenderer.flush(removingDisplayedImage: true, completionHandler: nil)
            await engine.seek(to: position, exact: true)
            if case .error = engine.state {
                precedingDisplayedSurfaceID = nil
                activeSeekState = .none
                throw AetherVideoFailure.seekFailed
            }
            if activeSeekState == .failed {
                precedingDisplayedSurfaceID = nil
                activeSeekState = .none
                throw AetherVideoFailure.seekFailed
            }
            try check()
            guard seekRevision == revision else {
                precedingDisplayedSurfaceID = nil
                activeSeekState = .none
                throw AetherVideoFailure.cancelled
            }
            if !committed { engine.pause() }
            try await waitForPresentedFrame(requireNewSurface: true)
            activeSeekState = .none
            guard seekRevision == revision else { throw AetherVideoFailure.cancelled }
            return self.position
        }
        seekOperation = operation
        defer { if seekRevision == revision { seekOperation = nil } }
        return try await withTaskCancellationHandler {
            try await operation.value
        } onCancel: {
            operation.cancel()
            Task { @MainActor [weak self] in
                guard let self, self.seekRevision == revision else { return }
                self.precedingDisplayedSurfaceID = nil
                self.activeSeekState = .none
                self.renderings.values.forEach { $0.cancel() }
                self.wakeFirstFrameWaiters()
            }
        }
    }

    public func selectAudioTrack(id: Int) throws {
        try check()
        guard engine.audioTracks.contains(where: { $0.id == id }) else {
            throw AetherVideoFailure.outputFailed
        }
        engine.selectAudioTrack(index: id)
    }

    public func selectSubtitleTrack(id: Int?) throws {
        try check()
        guard let id else {
            engine.clearSubtitle()
            return
        }
        guard engine.subtitleTracks.contains(where: { $0.id == id }) else {
            throw AetherVideoFailure.outputFailed
        }
        engine.selectSubtitleTrack(index: id)
    }

    public func setVideoGravity(_ gravity: AVLayerVideoGravity) {
        desiredGravity = gravity
        engine.videoGravity = gravity
    }

    public func setPictureInPictureActive(_ active: Bool) {
        engine.pictureInPictureActive = active
    }

    private func startProgressLoop() {
        guard progress == nil, !cancelled, committed else { return }
        progress = Task { [weak self] in
            while !Task.isCancelled {
                do { try await Task.sleep(for: .milliseconds(500)) } catch { return }
                guard let self, !self.cancelled, self.committed else { return }
                self.publishRateIfChanged()
                self.onProgress?(self.position)
            }
        }
    }

    private func stopProgressLoop() {
        progress?.cancel()
        progress = nil
    }

    private func publishRateIfChanged() {
        guard committed, !cancelled else { return }
        let current = rate
        if lastRate != current {
            lastRate = current
            onTransportRate?(current)
        }
    }

    private func notifyLayer() {
        let layer: CALayer? = playerLayer ?? displayLayer
        let identity = layer.map(ObjectIdentifier.init)
        if identity != lastLayer {
            lastLayer = identity
            engine.videoGravity = desiredGravity
            onDisplayLayerChanged?()
        }
    }

    public func cancel() {
        guard !cancelled else { return }
        cancelled = true
        committed = false
        cleanupBox.markCancelled()
        preparation?.cancel()
        seekOperation?.cancel()
        stopProgressLoop()
        renderings.values.forEach { $0.cancel() }
        wakeFirstFrameWaiters()
        precedingDisplayedSurfaceID = nil
        activeSeekState = .none
        engine.pause()
        engine.stop()
    }

    public func close() async {
        if let closing { await closing.value; return }
        let preparation = preparation, seekOperation = seekOperation, renderings = Array(renderings.values)
        cancel()
        let task = Task { [self] in
            _ = await preparation?.result
            _ = await seekOperation?.result
            for rendering in renderings { _ = await rendering.result }
            await engine.stopAndDrain()
            cleanupBox.clear()
            engine.unbind(view: view)
            view.removeFromSuperview()
            subscriptions.removeAll()
            onFailure = nil; onProgress = nil; onTransportRate = nil; onEnd = nil
            engine.onExternalPlaybackIntent = nil; onPlaybackIntent = nil
            precedingDisplayedSurfaceID = nil
            onDisplayLayerChanged = nil
        }
        closing = task
        await task.value
    }

    private func check() throws {
        if cancelled || Task.isCancelled { throw AetherVideoFailure.cancelled }
        if let failure { throw failure }
    }

    private func displayedPixelBuffer() -> CVPixelBuffer? {
        if let layer = playerLayer { return layer.displayedPixelBuffer() }
        return displayLayer?.sampleBufferRenderer.displayedPixelBuffer()
    }

    private static func surfaceID(of buffer: CVPixelBuffer) -> IOSurfaceID? {
        guard let surface = CVPixelBufferGetIOSurface(buffer)?.takeUnretainedValue() else { return nil }
        let id = IOSurfaceGetID(surface)
        return id != 0 ? id : nil
    }

    private static func classify(info: PlaybackErrorInfo?) -> AetherVideoFailure {
        switch info?.kind {
        case .hlsFormatUnsupported, .dolbyVisionRequiresHardware, .demuxedAudioLiveUnsupported,
             .audioBridgeProducedNoOutput: return .unsupportedFormat
        case .softwarePipelineFailed, .nativeItemFailed: return .outputFailed
        default: return .inputFailed
        }
    }
}
