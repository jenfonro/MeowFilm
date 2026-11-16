import Foundation
@preconcurrency import AVFoundation
import KSPlayer
#if os(iOS)
import UIKit
#else
import AppKit
#endif

/// Hosts the complete native KSPlayer surface, not a detached rendering layer.
@MainActor
public final class VideoOutput {
    #if os(iOS)
    public let presentationView = UIView()
    #else
    public let presentationView = NSView()
    #endif
    public private(set) var hasPresentedFrame = false
    public var onDisplayLayerChanged: (() -> Void)?
    public var displayLayer: AVSampleBufferDisplayLayer? { player?.videoOutput?.displayLayer }
    /// Actual decoded storage, not a promise that the frame reached the screen.
    public var pixelBuffer: CVPixelBuffer? { player?.videoOutput?.pixelBuffer?.cvPixelBuffer }
    private weak var player: KSMEPlayer?
    private var rendering: Task<Void, Error>?
    private var frameRevision = UUID()
    private var cancelled = false

    public init() {
        #if os(macOS)
        presentationView.wantsLayer = true
        #endif
    }

    public var rate: Float {
        guard !cancelled, let player, player.isPlaying, player.loadState == .playable else { return 0 }
        return player.playbackRate
    }
    public var volume: Float {
        get { player?.playbackVolume ?? 0 }
        set { player?.playbackVolume = newValue }
    }

    func install(_ player: KSMEPlayer) throws {
        guard let view = player.view else { throw VideoKernelFailure.formatFailed }
        self.player = player
        player.onDisplayLayerChanged = { [weak self] _ in self?.onDisplayLayerChanged?() }
        view.frame = presentationView.bounds
        #if os(iOS)
        view.autoresizingMask = [.flexibleWidth, .flexibleHeight]
        #else
        view.autoresizingMask = [.width, .height]
        #endif
        presentationView.addSubview(view)
    }

    func invalidateFrame() {
        hasPresentedFrame = false
        frameRevision = UUID()
        rendering?.cancel()
        player?.videoOutput?.flush()
    }

    public func renderFirstVideoFrame() async throws {
        guard !cancelled, let player, player.isReadyToPlay else { throw VideoKernelFailure.cancelled }
        if hasPresentedFrame { return }
        rendering?.cancel()
        let revision = UUID(); frameRevision = revision
        let operation = Task { try await player.waitForFirstVideoFrame() }
        rendering = operation
        defer { if frameRevision == revision { rendering = nil } }
        do {
            try await withTaskCancellationHandler {
                try await operation.value
                try Task.checkCancellation()
                guard !cancelled, revision == frameRevision else { throw VideoKernelFailure.cancelled }
                hasPresentedFrame = true
            } onCancel: {
                operation.cancel()
            }
        } catch is CancellationError { throw VideoKernelFailure.cancelled }
        catch let error as FrameOutputError {
            switch error {
            case .cancelled: throw VideoKernelFailure.cancelled
            case .timedOut: throw VideoKernelFailure.timedOut
            default: throw VideoKernelFailure.outputFailed
            }
        }
    }

    func cancel() {
        cancelled = true
        frameRevision = UUID()
        rendering?.cancel()
    }

    func close() async {
        cancel()
        if let rendering { _ = await rendering.result }
        rendering = nil
        onDisplayLayerChanged = nil
        player?.onDisplayLayerChanged = nil
        player?.view?.removeFromSuperview()
        player = nil
        presentationView.removeFromSuperview()
    }
}
