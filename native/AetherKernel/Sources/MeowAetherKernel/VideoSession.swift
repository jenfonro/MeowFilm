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

/// The dynamic-library boundary owns Aether's FFmpeg symbols. No upstream type
/// or network credential crosses into the application's playback coordinator.
@MainActor
public final class AetherVideoSession {
    private let engine: AetherEngine
    private let view = AetherPlayerView(frame: .zero)
    private var subscriptions = Set<AnyCancellable>()
    private var preparation: Task<Void, Error>?
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
    private var precedingDisplayedFrame: CVPixelBuffer?

    public var onFailure: (@MainActor (AetherVideoFailure) -> Void)?
    public var onProgress: (@MainActor (Double) -> Void)?
    public var onTransportRate: (@MainActor (Float) -> Void)?
    public var onPlaybackIntent: (@MainActor (Bool) -> Void)?
    public var onEnd: (@MainActor () -> Void)?
    public var onDisplayLayerChanged: (() -> Void)?
    public var position: Double { max(0, engine.currentTime) }
    public var duration: Double? {
        engine.duration.isFinite && engine.duration > 0 ? engine.duration : nil
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
    public var volume: Float {
        get { engine.volume }
        set { engine.volume = newValue }
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
        engine = try AetherEngine(hostManagedLifecycle: true)
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
            if case .error = state {
                let error = Self.classify(info: self.engine.errorInfo)
                self.failure = error
                if self.committed { self.onFailure?(error) }
            } else if state == .ended, self.committed { self.onEnd?() }
        }.store(in: &subscriptions)
    }

    public func prepare(url: URL, format: String?, clearKeys: [String: Data],
                        position: Double, headers: [String: String]) async throws {
        try check()
        guard clearKeys.isEmpty else { throw AetherVideoFailure.unsupportedFormat }
        var options = LoadOptions()
        options.autoplay = false
        options.httpHeaders = headers
        options.displayCriteriaOwner = .host
        options.nativeRemoteHLS = format == "hls"
        let engine = engine
        let task = Task {
            do {
                _ = try await engine.load(url: url, startPosition: position, options: options)
            } catch {
                if error is CancellationError { throw AetherVideoFailure.cancelled }
                if AetherEngine.mediaCapabilityUnsupported(error) { throw AetherVideoFailure.unsupportedFormat }
                throw Self.classify(info: engine.errorInfo)
            }
        }
        preparation = task
        defer { preparation = nil }
        try await withTaskCancellationHandler {
            try await task.value
            try check()
            guard playerLayer != nil || displayLayer != nil else { throw AetherVideoFailure.unsupportedFormat }
            engine.pause()
            notifyLayer()
        } onCancel: {
            task.cancel()
            Task { @MainActor [weak self] in self?.cancel() }
        }
    }

    public func align(to position: Double) async throws {
        if !seekable { try await renderFirstVideoFrame() }
        _ = try await seek(to: position)
    }

    public func renderFirstVideoFrame() async throws {
        try check()
        if presented { return }
        let revision = seekRevision
        let task = Task { [self] in
            let deadline = ContinuousClock.now.advanced(by: .seconds(15))
            while true {
                try check()
                guard revision == seekRevision else { throw AetherVideoFailure.cancelled }
                // Read actual displayed storage; layer readiness alone is not an output receipt.
                let image = displayedPixelBuffer()
                if let image, CVPixelBufferGetWidth(image) > 0, CVPixelBufferGetHeight(image) > 0,
                   precedingDisplayedFrame.map({ !Self.sameStorage($0, image) }) ?? true {
                    precedingDisplayedFrame = nil
                    presented = true; notifyLayer(); return
                }
                guard ContinuousClock.now < deadline else { throw AetherVideoFailure.timedOut }
                try await Task.sleep(for: .milliseconds(10))
            }
        }
        let renderingID = UUID()
        renderings[renderingID] = task
        defer { renderings[renderingID] = nil }
        try await withTaskCancellationHandler {
            try await task.value
        } onCancel: { task.cancel() }
        try check()
    }

    public func validateForCommit() throws {
        try check()
        guard presented, engine.isSessionReady else { throw AetherVideoFailure.outputFailed }
    }

    public func commit(rate: Float, volume: Float) {
        guard !cancelled, presented, failure == nil else { return }
        committed = true; self.volume = volume; setRate(rate)
        progress = Task { [weak self] in
            while !Task.isCancelled {
                do { try await Task.sleep(for: .milliseconds(250)) } catch { return }
                guard let self, !self.cancelled else { return }
                self.notifyLayer()
                if self.lastRate != self.rate {
                    self.lastRate = self.rate; self.onTransportRate?(self.rate)
                }
                self.onProgress?(self.position)
            }
        }
    }

    public func setRate(_ rate: Float) {
        guard !cancelled, committed else { return }
        if rate <= 0 { engine.pause() }
        else {
            engine.setRate(min(rate, 2))
            if engine.state == .paused { engine.play() }
        }
    }

    public func seek(to position: Double) async throws -> Double {
        try check()
        guard seekable, position.isFinite, position >= 0 else { throw AetherVideoFailure.seekFailed }
        let revision = UUID(); seekRevision = revision
        presented = false
        renderings.values.forEach { $0.cancel() }
        precedingDisplayedFrame = abs(position - self.position) > 0.1 ? displayedPixelBuffer() : nil
        displayLayer?.flushAndRemoveImage()
        await engine.seek(to: position, exact: true)
        try check()
        guard revision == seekRevision else { throw AetherVideoFailure.cancelled }
        if !committed { engine.pause() }
        try await renderFirstVideoFrame()
        return self.position
    }

    public func setVideoGravity(_ gravity: AVLayerVideoGravity) { engine.videoGravity = gravity }
    public func setPictureInPictureActive(_ active: Bool) { engine.pictureInPictureActive = active }
    private func notifyLayer() {
        let layer: CALayer? = playerLayer ?? displayLayer
        let identity = layer.map(ObjectIdentifier.init)
        if identity != lastLayer { lastLayer = identity; onDisplayLayerChanged?() }
    }

    public func cancel() {
        cancelled = true; preparation?.cancel(); progress?.cancel()
        renderings.values.forEach { $0.cancel() }
        engine.pause(); engine.stop()
    }

    public func close() async {
        if let closing { await closing.value; return }
        let preparation = preparation, renderings = Array(renderings.values), progress = progress
        cancel()
        let task = Task { [self] in
            _ = await preparation?.result
            for rendering in renderings { _ = await rendering.result }
            await progress?.value
            await engine.stopAndDrain()
            engine.unbind(view: view)
            view.removeFromSuperview()
            subscriptions.removeAll()
            onFailure = nil; onProgress = nil; onTransportRate = nil; onEnd = nil
            engine.onExternalPlaybackIntent = nil; onPlaybackIntent = nil
            precedingDisplayedFrame = nil
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

    private static func classify(info: PlaybackErrorInfo?) -> AetherVideoFailure {
        switch info?.kind {
        case .hlsFormatUnsupported, .dolbyVisionRequiresHardware, .demuxedAudioLiveUnsupported,
             .audioBridgeProducedNoOutput: return .unsupportedFormat
        case .softwarePipelineFailed, .nativeItemFailed: return .outputFailed
        default: return .inputFailed
        }
    }

    private static func sameStorage(_ first: CVPixelBuffer, _ second: CVPixelBuffer) -> Bool {
        if let a = CVPixelBufferGetIOSurface(first)?.takeUnretainedValue(),
           let b = CVPixelBufferGetIOSurface(second)?.takeUnretainedValue() {
            return IOSurfaceGetID(a) == IOSurfaceGetID(b)
        }
        return first === second
    }
}
