import Foundation
@preconcurrency import AVFoundation
import QuartzCore
import CoreImage
import Metal

private final class VideoPresentationLayer: CALayer {
    override func layoutSublayers() {
        super.layoutSublayers()
        CATransaction.begin(); CATransaction.setDisableActions(true)
        sublayers?.forEach { $0.frame = bounds }
        CATransaction.commit()
    }
}

/// Sample-buffer output only. No AVAudioSession, remote command or business
/// owner is created here. Audio/video always share this one timebase.
@MainActor
public final class VideoSampleOutput {
    public let displayLayer = AVSampleBufferDisplayLayer()
    public let presentationLayer: CALayer = VideoPresentationLayer()
    public let synchronizer = AVSampleBufferRenderSynchronizer()
    private let audio = AVSampleBufferAudioRenderer()
    private let feed: VideoRendererFeed
    private var firstFrameContinuation: CheckedContinuation<Void, Error>?
    private var firstFrameRevision = UUID()
    private var status: NSKeyValueObservation?
    private var deadline: Task<Void, Never>?
    private var preview: CAMetalLayer?
    private var command: MTLCommandBuffer?
    private var closed = false
    private var cancelled = false
    private var closeTask: Task<Void, Never>?
    private var hasAudio = false
    public var onFailure: ((VideoKernelFailure) -> Void)?
    private var renderObservations: [NSKeyValueObservation] = []

    public init() {
        feed = VideoRendererFeed(video: displayLayer.sampleBufferRenderer, audio: audio)
        audio.isMuted = true
        presentationLayer.addSublayer(displayLayer)
        synchronizer.addRenderer(displayLayer.sampleBufferRenderer)
        synchronizer.setRate(0, time: .zero)
        renderObservations = [
            displayLayer.sampleBufferRenderer.observe(\.status, options: [.new]) { [weak self] renderer, _ in
                if renderer.status == .failed { Task { @MainActor [weak self] in self?.renderFailed() } }
            },
            audio.observe(\.status, options: [.new]) { [weak self] renderer, _ in
                if renderer.status == .failed { Task { @MainActor [weak self] in self?.renderFailed() } }
            }
        ]
    }

    public var position: Double {
        let value = synchronizer.currentTime().seconds
        return value.isFinite ? max(0, value) : 0
    }
    public var rate: Float { synchronizer.rate }
    public func configure(hasAudio: Bool) {
        if hasAudio, !self.hasAudio {
            synchronizer.addRenderer(audio)
            self.hasAudio = true
        }
    }
    public var volume: Float {
        get { audio.volume }
        set { audio.volume = newValue }
    }

    public func setRate(_ rate: Float) { synchronizer.setRate(rate, time: synchronizer.currentTime()) }
    public func commit(volume: Float) { audio.volume = volume; audio.isMuted = false }

    public func enqueue(_ sample: VideoSample) async throws {
        guard !closed, !cancelled else { throw VideoKernelFailure.cancelled }
        try await feed.enqueue(sample)
    }

    /// At rate zero, an actual displayed pixel buffer is a witness of output.
    /// renderer.status/URL success/decoded input alone are NEVER that witness.
    public func renderFirstVideoFrame(_ sample: VideoSample) async throws {
        guard !closed, !cancelled else { throw VideoKernelFailure.cancelled }
        guard sample.kind == .video, CMSampleBufferGetImageBuffer(sample.buffer) != nil else {
            throw VideoKernelFailure.outputFailed
        }
        finishFirstFrame(.failure(VideoKernelFailure.cancelled))
        let revision = UUID(); firstFrameRevision = revision
        await feed.stopAndFlush(removingImage: true)
        guard revision == firstFrameRevision, !cancelled, !closed else { throw VideoKernelFailure.cancelled }
        preview?.removeFromSuperlayer(); preview = nil
        synchronizer.setRate(0, time: CMTime(seconds: sample.position, preferredTimescale: 1_000_000))
        try await enqueue(sample)
        guard revision == firstFrameRevision, !closed, !cancelled else { throw VideoKernelFailure.cancelled }
        try await withTaskCancellationHandler {
            try Task.checkCancellation()
            try await withCheckedThrowingContinuation { continuation in
                firstFrameContinuation = continuation
                deadline = Task { [weak self] in
                    let limit = ContinuousClock.now.advanced(by: .seconds(15))
                    while !Task.isCancelled {
                        guard let self, revision == self.firstFrameRevision,
                              self.firstFrameContinuation != nil else { return }
                        if #available(iOS 17.4, macOS 14.4, *) {
                            // Readiness can precede the displayed pixel buffer,
                            // which has no completion notification of its own.
                            self.checkDisplayedFrame(revision: revision)
                            guard self.firstFrameContinuation != nil else { return }
                        }
                        if ContinuousClock.now >= limit {
                            self.finishFirstFrame(.failure(VideoKernelFailure.timedOut), revision: revision)
                            return
                        }
                        do { try await Task.sleep(for: .milliseconds(16)) } catch { return }
                    }
                }
                status = displayLayer.sampleBufferRenderer.observe(\.status, options: [.initial, .new]) { [weak self] renderer, _ in
                    if renderer.status == .failed {
                        Task { @MainActor [weak self] in
                            self?.finishFirstFrame(.failure(VideoKernelFailure.outputFailed), revision: revision)
                        }
                    }
                }
                if #unavailable(iOS 17.4, macOS 14.4) {
                    presentLegacyFirstFrame(sample, revision: revision)
                }
            }
        } onCancel: {
            Task { @MainActor [weak self] in
                self?.finishFirstFrame(.failure(VideoKernelFailure.cancelled), revision: revision)
            }
        }
    }

    @available(iOS 17.4, macOS 14.4, *)
    private func checkDisplayedFrame(revision: UUID) {
        guard revision == firstFrameRevision, firstFrameContinuation != nil, displayLayer.isReadyForDisplay,
              let pixel = displayLayer.sampleBufferRenderer.displayedPixelBuffer(),
              CVPixelBufferGetWidth(pixel) > 0, CVPixelBufferGetHeight(pixel) > 0 else { return }
        finishFirstFrame(.success(()))
    }

    /// iOS 17.0–17.3 has no displayed-buffer query. Present the exact decoded
    /// image once through CoreImage/Metal and await MTLDrawable's presentation
    /// callback. The preview stays UNDER the timed sample-buffer layer; it is
    /// not a second decoder, ongoing video path, clock or PiP owner.
    private func presentLegacyFirstFrame(_ sample: VideoSample, revision: UUID) {
#if targetEnvironment(simulator)
        // The simulator SDK omits MTLDrawable's presentation callback. Before
        // iOS 17.4 it cannot provide this output witness; GPU completion is not one.
        finishFirstFrame(.failure(VideoKernelFailure.outputFailed), revision: revision)
#else
        guard let pixel = CMSampleBufferGetImageBuffer(sample.buffer), let device = MTLCreateSystemDefaultDevice(),
              let queue = device.makeCommandQueue(), let command = queue.makeCommandBuffer(),
              presentationLayer.superlayer != nil, presentationLayer.bounds.width > 0,
              presentationLayer.bounds.height > 0 else {
            finishFirstFrame(.failure(VideoKernelFailure.outputFailed)); return
        }
        let layer = CAMetalLayer()
        layer.device = device; layer.pixelFormat = .rgba16Float
        layer.framebufferOnly = false; layer.wantsExtendedDynamicRangeContent = true
        layer.frame = presentationLayer.bounds
        layer.drawableSize = CGSize(width: max(1, layer.bounds.width), height: max(1, layer.bounds.height))
        presentationLayer.insertSublayer(layer, below: displayLayer); preview = layer
        guard let drawable = layer.nextDrawable(),
              let colorSpace = CGColorSpace(name: CGColorSpace.extendedLinearSRGB) else {
            finishFirstFrame(.failure(VideoKernelFailure.outputFailed)); return
        }
        let source = CIImage(cvPixelBuffer: pixel)
        let size = layer.drawableSize
        let factor = min(size.width / source.extent.width, size.height / source.extent.height)
        let image = source.transformed(by: CGAffineTransform(scaleX: factor, y: factor))
            .transformed(by: CGAffineTransform(translationX: (size.width - source.extent.width * factor) / 2,
                                               y: (size.height - source.extent.height * factor) / 2))
            .composited(over: CIImage(color: .black).cropped(to: CGRect(origin: .zero, size: size)))
        let context = CIContext(mtlDevice: device)
        context.render(image, to: drawable.texture, commandBuffer: command,
            bounds: CGRect(origin: .zero, size: size), colorSpace: colorSpace)
        self.command = command
        drawable.addPresentedHandler { [weak self] _ in
            Task { @MainActor [weak self] in self?.finishFirstFrame(.success(()), revision: revision) }
        }
        command.addCompletedHandler { [weak self, context] completed in
            _ = context
            if completed.status == .error {
                Task { @MainActor [weak self] in
                    self?.finishFirstFrame(.failure(VideoKernelFailure.outputFailed), revision: revision)
                }
            }
        }
        command.present(drawable); command.commit()
#endif
    }

    private func finishFirstFrame(_ result: Result<Void, Error>) {
        let continuation = firstFrameContinuation; firstFrameContinuation = nil
        deadline?.cancel(); deadline = nil
        status?.invalidate(); status = nil
        continuation?.resume(with: result)
    }

    private func finishFirstFrame(_ result: Result<Void, Error>, revision: UUID) {
        guard revision == firstFrameRevision else { return }
        finishFirstFrame(result)
    }

    private func renderFailed() {
        guard !closed else { return }
        finishFirstFrame(.failure(VideoKernelFailure.outputFailed))
        onFailure?(.outputFailed)
    }

    public func cancel() {
        cancelled = true
        synchronizer.setRate(0, time: synchronizer.currentTime())
        finishFirstFrame(.failure(VideoKernelFailure.cancelled))
        feed.cancelRequests()
    }

    public func stopFeeding(removingImage: Bool) async {
        synchronizer.setRate(0, time: synchronizer.currentTime())
        finishFirstFrame(.failure(VideoKernelFailure.cancelled))
        await feed.stopAndFlush(removingImage: removingImage)
        if removingImage { preview?.removeFromSuperlayer(); preview = nil }
    }

    public func resetTime(to position: Double) {
        synchronizer.setRate(0, time: CMTime(seconds: position, preferredTimescale: 1_000_000))
    }

    public func close() async {
        if closeTask == nil {
            closed = true
            onFailure = nil
            renderObservations.forEach { $0.invalidate() }; renderObservations.removeAll()
            closeTask = Task {
                await self.stopFeeding(removingImage: true)
                if let command = self.command, command.status != .completed, command.status != .error {
                    await withCheckedContinuation { continuation in
                        command.addCompletedHandler { _ in continuation.resume() }
                    }
                }
                self.command = nil
                await self.removeRenderer(self.displayLayer.sampleBufferRenderer)
                if self.hasAudio { await self.removeRenderer(self.audio) }
                self.presentationLayer.removeFromSuperlayer()
            }
        }
        await closeTask!.value
    }

    private func removeRenderer(_ renderer: any AVQueuedSampleBufferRendering) async {
        await withCheckedContinuation { continuation in
            synchronizer.removeRenderer(renderer, at: .invalid) { _ in continuation.resume() }
        }
    }
}
