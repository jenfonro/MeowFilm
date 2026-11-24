import Foundation
@preconcurrency import AVFoundation
import CoreMedia
import CoreVideo
import QuartzCore
#if os(iOS)
import UIKit
#else
import AppKit
#endif

/// Unified AVSampleBufferDisplayLayer video surface (8-bit SDR + 10-bit HDR10/HLG/DoVi,
/// bounds-aware hardware layer rotation/flip, persisted videoGravity, foreground recovery,
/// responsive subtitle overlay, and event-driven first-frame confirmation).
@MainActor
public final class VideoOutput {
    #if os(iOS)
    private final class ContainerView: UIView {
        var onLayout: (() -> Void)?
        override func layoutSubviews() {
            super.layoutSubviews()
            onLayout?()
        }
    }
    private final class DisplayLayerView: UIView {
        override class var layerClass: AnyClass { AVSampleBufferDisplayLayer.self }
        var sampleLayer: AVSampleBufferDisplayLayer { layer as! AVSampleBufferDisplayLayer }
    }
    public var presentationView: UIView { containerView }
    private let containerView = ContainerView()
    #else
    private final class ContainerView: NSView {
        var onLayout: (() -> Void)?
        override func layout() {
            super.layout()
            onLayout?()
        }
    }
    private final class DisplayLayerView: NSView {
        let sampleLayer = AVSampleBufferDisplayLayer()
        override init(frame frameRect: NSRect) {
            super.init(frame: frameRect)
            // Layer-hosting NSView must assign `layer` BEFORE enabling `wantsLayer`.
            layer = sampleLayer
            wantsLayer = true
        }
        required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }
    }
    public var presentationView: NSView { containerView }
    private let containerView = ContainerView()
    #endif

    private let displayView = DisplayLayerView(frame: .zero)
    private let subtitleTextLayer = CATextLayer()
    private let subtitleImageLayer = CALayer()
    private var videoGravity: AVLayerVideoGravity = .resizeAspect
    private var rotationDegrees: Int = 0
    private var hflip: Bool = false
    private var vflip: Bool = false
    private var currentSubtitleCue: VideoSubtitleCue?

    public private(set) var hasPresentedFrame = false
    public private(set) var pixelBuffer: CVPixelBuffer?
    public var onDisplayLayerChanged: (() -> Void)?
    public var displayLayer: AVSampleBufferDisplayLayer? { displayView.sampleLayer }
    public var playerLayer: AVPlayerLayer? { nil }

    // Callbacks wired by VideoSession
    var readRate: (() -> Float)?
    var readVolume: (() -> Float)?
    var writeVolume: ((Float) -> Void)?
    var onNeedsFlushRecovery: (() -> Void)?

    private var firstFrameContinuations: [UUID: CheckedContinuation<Void, Error>] = [:]
    private var cancelled = false
    private var lifecycleObservers: [NSObjectProtocol] = []

    public init() {
        #if os(iOS)
        containerView.backgroundColor = .black
        containerView.clipsToBounds = true
        #else
        containerView.wantsLayer = true
        containerView.layer?.backgroundColor = CGColor(red: 0, green: 0, blue: 0, alpha: 1)
        #endif
        containerView.addSubview(displayView)

        displayView.sampleLayer.videoGravity = videoGravity
        displayView.sampleLayer.backgroundColor = CGColor(red: 0, green: 0, blue: 0, alpha: 1)

        subtitleImageLayer.contentsGravity = .resizeAspect
        subtitleImageLayer.isHidden = true
        subtitleTextLayer.alignmentMode = .center
        subtitleTextLayer.isWrapped = true
        subtitleTextLayer.fontSize = 18
        subtitleTextLayer.foregroundColor = CGColor(red: 1, green: 1, blue: 1, alpha: 1)
        subtitleTextLayer.shadowColor = CGColor(red: 0, green: 0, blue: 0, alpha: 1)
        subtitleTextLayer.shadowOpacity = 0.85
        subtitleTextLayer.shadowRadius = 2
        subtitleTextLayer.shadowOffset = CGSize(width: 0, height: 1)
        subtitleTextLayer.isHidden = true

        #if os(iOS)
        let scale = max(1, containerView.traitCollection.displayScale)
        subtitleTextLayer.contentsScale = scale
        subtitleImageLayer.contentsScale = scale
        containerView.layer.addSublayer(subtitleImageLayer)
        containerView.layer.addSublayer(subtitleTextLayer)
        installLifecycleObservers()
        #else
        containerView.layer?.addSublayer(subtitleImageLayer)
        containerView.layer?.addSublayer(subtitleTextLayer)
        #endif

        containerView.onLayout = { [weak self] in
            self?.layoutDisplayAndSubtitles()
        }
        layoutDisplayAndSubtitles()
    }

    public var rate: Float {
        guard !cancelled else { return 0 }
        return readRate?() ?? 0
    }

    public var volume: Float {
        get { readVolume?() ?? 0 }
        set { writeVolume?(newValue) }
    }

    public func setVideoGravity(_ gravity: AVLayerVideoGravity) {
        videoGravity = gravity
        displayView.sampleLayer.videoGravity = gravity
    }

    public func setPictureInPictureActive(_ active: Bool) {
        // AVSampleBufferDisplayLayer remains attached to PiP; nothing extra needed.
    }

    func applyGeometry(rotationDegrees: Int, hflip: Bool, vflip: Bool) {
        self.rotationDegrees = rotationDegrees
        self.hflip = hflip
        self.vflip = vflip
        layoutDisplayAndSubtitles()
    }

    private func layoutDisplayAndSubtitles() {
        let bounds = containerView.bounds
        let quarterTurn = rotationDegrees == 90 || rotationDegrees == 270
        let targetSize = quarterTurn
            ? CGSize(width: bounds.height, height: bounds.width)
            : bounds.size

        var transform = CGAffineTransform.identity
        if hflip || vflip {
            transform = transform.scaledBy(x: hflip ? -1 : 1, y: vflip ? -1 : 1)
        }
        if rotationDegrees != 0 {
            let radians = CGFloat(rotationDegrees) * .pi / 180.0
            transform = transform.concatenating(CGAffineTransform(rotationAngle: radians))
        }

        CATransaction.begin()
        CATransaction.setDisableActions(true)
        #if os(iOS)
        displayView.transform = .identity
        displayView.bounds = CGRect(origin: .zero, size: targetSize)
        displayView.center = CGPoint(x: bounds.midX, y: bounds.midY)
        displayView.transform = transform
        let scale = max(1, containerView.traitCollection.displayScale)
        subtitleTextLayer.contentsScale = scale
        subtitleImageLayer.contentsScale = scale
        #else
        displayView.frame = bounds
        displayView.sampleLayer.anchorPoint = CGPoint(x: 0.5, y: 0.5)
        displayView.sampleLayer.bounds = CGRect(origin: .zero, size: targetSize)
        displayView.sampleLayer.position = CGPoint(x: bounds.midX, y: bounds.midY)
        displayView.sampleLayer.setAffineTransform(transform)
        #endif
        displayView.sampleLayer.videoGravity = videoGravity
        layoutSubtitleLayersLocked(in: bounds)
        CATransaction.commit()
    }

    /// Enqueues one decoded CVPixelBuffer onto AVSampleBufferDisplayLayer.
    /// Always checks layer failure / flush requirement BEFORE readiness (fixes foreground black screen).
    func enqueue(pixelBuffer: CVPixelBuffer, pts: Double, displayImmediately: Bool) -> Bool {
        guard !cancelled else { return false }
        let layer = displayView.sampleLayer
        let renderer = layer.sampleBufferRenderer

        if renderer.status == .failed || renderer.requiresFlushToResumeDecoding {
            renderer.flush()
            layer.videoGravity = videoGravity
        }
        guard renderer.isReadyForMoreMediaData else { return false }

        var formatDesc: CMVideoFormatDescription?
        guard CMVideoFormatDescriptionCreateForImageBuffer(
            allocator: kCFAllocatorDefault,
            imageBuffer: pixelBuffer,
            formatDescriptionOut: &formatDesc
        ) == noErr, let formatDesc else { return false }

        var timing = CMSampleTimingInfo(
            duration: .invalid,
            presentationTimeStamp: CMTime(seconds: max(0, pts), preferredTimescale: 90_000),
            decodeTimeStamp: .invalid
        )
        var sampleBuffer: CMSampleBuffer?
        guard CMSampleBufferCreateReadyWithImageBuffer(
            allocator: kCFAllocatorDefault,
            imageBuffer: pixelBuffer,
            formatDescription: formatDesc,
            sampleTiming: &timing,
            sampleBufferOut: &sampleBuffer
        ) == noErr, let sampleBuffer else { return false }

        if displayImmediately {
            if let attachments = CMSampleBufferGetSampleAttachmentsArray(sampleBuffer, createIfNecessary: true) {
                let dict = unsafeBitCast(CFArrayGetValueAtIndex(attachments, 0), to: CFMutableDictionary.self)
                CFDictionarySetValue(
                    dict,
                    Unmanaged.passUnretained(kCMSampleAttachmentKey_DisplayImmediately).toOpaque(),
                    Unmanaged.passUnretained(kCFBooleanTrue).toOpaque()
                )
            }
        }

        renderer.enqueue(sampleBuffer)
        self.pixelBuffer = pixelBuffer
        if !hasPresentedFrame {
            hasPresentedFrame = true
            let pending = firstFrameContinuations
            firstFrameContinuations.removeAll()
            for continuation in pending.values {
                continuation.resume(returning: ())
            }
        }
        return true
    }

    func updateSubtitle(_ cue: VideoSubtitleCue?) {
        currentSubtitleCue = cancelled ? nil : cue
        CATransaction.begin()
        CATransaction.setDisableActions(true)
        layoutSubtitleLayersLocked(in: containerView.bounds)
        CATransaction.commit()
    }

    private func layoutSubtitleLayersLocked(in bounds: CGRect) {
        guard let cue = currentSubtitleCue, !cancelled else {
            subtitleTextLayer.isHidden = true
            subtitleTextLayer.string = nil
            subtitleImageLayer.isHidden = true
            subtitleImageLayer.contents = nil
            return
        }
        if let image = cue.image {
            subtitleTextLayer.isHidden = true
            subtitleTextLayer.string = nil
            let frame = CGRect(
                x: bounds.width * cue.rect.origin.x,
                y: bounds.height * cue.rect.origin.y,
                width: max(1, bounds.width * cue.rect.width),
                height: max(1, bounds.height * cue.rect.height)
            )
            subtitleImageLayer.frame = frame.isEmpty ? bounds : frame
            subtitleImageLayer.contents = image
            subtitleImageLayer.isHidden = false
        } else if let text = cue.text, !text.isEmpty {
            subtitleImageLayer.isHidden = true
            subtitleImageLayer.contents = nil
            let marginX: CGFloat = 20
            let height = min(bounds.height * 0.3, 96)
            subtitleTextLayer.frame = CGRect(
                x: marginX,
                y: max(0, bounds.height - height - 18),
                width: max(1, bounds.width - marginX * 2),
                height: height
            )
            subtitleTextLayer.string = text
            subtitleTextLayer.isHidden = false
        } else {
            subtitleTextLayer.isHidden = true
            subtitleImageLayer.isHidden = true
        }
    }

    func invalidateFrame() {
        hasPresentedFrame = false
        pixelBuffer = nil
        displayView.sampleLayer.sampleBufferRenderer.flush(removingDisplayedImage: true, completionHandler: nil)
        displayView.sampleLayer.videoGravity = videoGravity
        updateSubtitle(nil)
    }

    func failFirstFrameWaiters(_ error: VideoKernelFailure) {
        let pending = firstFrameContinuations
        firstFrameContinuations.removeAll()
        for continuation in pending.values {
            continuation.resume(throwing: error)
        }
    }

    public func renderFirstVideoFrame(timeoutSeconds: Double = 15) async throws {
        if cancelled { throw VideoKernelFailure.cancelled }
        if hasPresentedFrame { return }
        let token = UUID()
        let timeoutTask = Task { @MainActor [weak self] in
            do { try await Task.sleep(for: .seconds(timeoutSeconds)) } catch { return }
            guard let self, let cont = self.firstFrameContinuations.removeValue(forKey: token) else { return }
            cont.resume(throwing: VideoKernelFailure.timedOut)
        }
        defer { timeoutTask.cancel() }
        try await withTaskCancellationHandler {
            try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<Void, Error>) in
                if self.cancelled {
                    continuation.resume(throwing: VideoKernelFailure.cancelled)
                } else if self.hasPresentedFrame {
                    continuation.resume(returning: ())
                } else {
                    self.firstFrameContinuations[token] = continuation
                }
            }
        } onCancel: {
            Task { @MainActor [weak self] in
                guard let self, let cont = self.firstFrameContinuations.removeValue(forKey: token) else { return }
                cont.resume(throwing: VideoKernelFailure.cancelled)
            }
        }
    }

    func cancel() {
        cancelled = true
        failFirstFrameWaiters(.cancelled)
    }

    func close() async {
        cancel()
        removeLifecycleObservers()
        containerView.onLayout = nil
        displayView.sampleLayer.sampleBufferRenderer.flush(removingDisplayedImage: true, completionHandler: nil)
        pixelBuffer = nil
        onDisplayLayerChanged = nil
        onNeedsFlushRecovery = nil
        readRate = nil; readVolume = nil; writeVolume = nil
        updateSubtitle(nil)
        containerView.removeFromSuperview()
    }

    #if os(iOS)
    private func installLifecycleObservers() {
        let nc = NotificationCenter.default
        lifecycleObservers.append(
            nc.addObserver(forName: UIApplication.didBecomeActiveNotification, object: nil, queue: .main) { [weak self] _ in
                MainActor.assumeIsolated {
                    guard let self, !self.cancelled else { return }
                    let renderer = self.displayView.sampleLayer.sampleBufferRenderer
                    if renderer.status == .failed || renderer.requiresFlushToResumeDecoding {
                        renderer.flush()
                        self.displayView.sampleLayer.videoGravity = self.videoGravity
                        self.onNeedsFlushRecovery?()
                    }
                }
            }
        )
    }
    #endif

    private func removeLifecycleObservers() {
        for observer in lifecycleObservers {
            NotificationCenter.default.removeObserver(observer)
        }
        lifecycleObservers.removeAll()
    }
}
