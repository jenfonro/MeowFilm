#if os(iOS)
import Foundation
@preconcurrency import AVKit
import MeowContracts

/// The OS controller may move between successful attempts in ONE video lease.
/// ContentSource delegates do not move: each captures its immutable attempt, so
/// a delayed control from the preceding source cannot operate the new owner.
@MainActor
final class PlaybackVideoPictureInPicture: NSObject, AVPictureInPictureControllerDelegate,
    PlaybackVideoPictureInPictureControlling {
    private var source: PlaybackVideoPictureInPictureSource
    private var controller: AVPictureInPictureController?
    private var observation: NSKeyValueObservation?
    private var closing: [CheckedContinuation<Void, Never>] = []
    private var invalidated = false
    private var startRequested = false
    private var stopRequested = false
    private var rebinding = false
    private var systemOwnerAttempt: PlayAttempt?

    init(transport: PlaybackVideoTransport, lease: PlaybackVideoLease,
         controller: PlaybackController, attempt: PlayAttempt) {
        source = .init(transport: transport, lease: lease, playback: controller, attempt: attempt)
        super.init()
        guard AVPictureInPictureController.isPictureInPictureSupported() else { return }
        let content = AVPictureInPictureController.ContentSource(
            sampleBufferDisplayLayer: transport.output.displayLayer, playbackDelegate: source)
        let pip = AVPictureInPictureController(contentSource: content)
        self.controller = pip; pip.delegate = self
        pip.canStartPictureInPictureAutomaticallyFromInline = false
        observation = pip.observe(\.isPictureInPicturePossible, options: [.initial, .new]) { [weak self] _, _ in
            Task { @MainActor [weak self] in self?.publishActualState() }
        }
    }

    private var isCurrent: Bool { !invalidated && source.isCurrent }
    var isActive: Bool {
        isCurrent && !startRequested && !stopRequested && controller?.isPictureInPictureActive == true
    }
    var hasSystemOwner: Bool { systemOwnerAttempt != nil || controller?.isPictureInPictureActive == true }
    var state: PlaybackVideoPictureInPictureState {
        guard let controller, !invalidated else { return .unavailable }
        if stopRequested { return .stopping }
        if isActive { return .active }
        if startRequested { return .starting }
        return controller.isPictureInPicturePossible ? .possible : .unavailable
    }

    /// Called after the new identity and surface commit, with this object
    /// removed from old retirement. Never copy the preceding `.active` state.
    func rebind(transport: PlaybackVideoTransport, lease: PlaybackVideoLease,
                controller: PlaybackController, attempt: PlayAttempt) {
        let owned = hasSystemOwner
        source = .init(transport: transport, lease: lease, playback: controller, attempt: attempt)
        if owned { systemOwnerAttempt = attempt }
        rebinding = true
        self.controller?.contentSource = .init(
            sampleBufferDisplayLayer: transport.output.displayLayer, playbackDelegate: source)
        rebinding = false
        // Do not call clients inside PlaybackController's commit transaction.
        // started carries the immediate OS observation; reconcile a stop/failure
        // during contentSource replacement after that transaction.
        Task { @MainActor [weak self] in self?.publishActualState() }
    }

    func start() -> Bool {
        guard isCurrent, let controller, controller.isPictureInPicturePossible,
              !controller.isPictureInPictureActive, !startRequested, !stopRequested else { return false }
        startRequested = true
        publishActualState()
        guard isCurrent else { startRequested = false; return false }
        controller.startPictureInPicture(); return true
    }
    func stop() {
        guard let controller else { return }
        if startRequested || controller.isPictureInPictureActive { stopRequested = true }
        controller.stopPictureInPicture()
    }
    func invalidateState() { controller?.invalidatePlaybackState() }

    func close() async {
        invalidated = true
        observation?.invalidate(); observation = nil
        if let controller, startRequested || controller.isPictureInPictureActive || controller.isPictureInPictureSuspended {
            await withCheckedContinuation { continuation in
                closing.append(continuation); controller.stopPictureInPicture()
            }
        }
        controller?.delegate = nil; controller?.contentSource = nil; controller = nil
        systemOwnerAttempt = nil
    }

    private func publishActualState() {
        publish(state)
    }
    private func publish(_ state: PlaybackVideoPictureInPictureState) {
        guard !rebinding, isCurrent, let transport = source.transport else { return }
        let ended = systemOwnerAttempt != nil &&
            [.possible, .unavailable, .failed].contains(state)
        if state == .active { systemOwnerAttempt = source.attempt }
        else if ended { systemOwnerAttempt = nil }
        source.lease?.updatePictureInPicture(state, transport: transport, attempt: source.attempt,
                                             systemOwnerEnded: ended)
    }

    func pictureInPictureControllerWillStartPictureInPicture(_ controller: AVPictureInPictureController) {
        startRequested = true
        guard isCurrent else { controller.stopPictureInPicture(); return }; publishActualState()
    }
    func pictureInPictureControllerDidStartPictureInPicture(_ controller: AVPictureInPictureController) {
        startRequested = false
        guard isCurrent, !stopRequested else { controller.stopPictureInPicture(); return }
        publishActualState()
    }
    func pictureInPictureController(_ controller: AVPictureInPictureController,
                                   failedToStartPictureInPictureWithError error: Error) {
        startRequested = false; stopRequested = false
        publish(.failed)
        finishClose()
    }
    func pictureInPictureControllerWillStopPictureInPicture(_ controller: AVPictureInPictureController) {
        stopRequested = true; publishActualState()
    }
    func pictureInPictureControllerDidStopPictureInPicture(_ controller: AVPictureInPictureController) {
        startRequested = false; stopRequested = false
        publishActualState()
        finishClose()
    }
    func pictureInPictureController(_ controller: AVPictureInPictureController,
        restoreUserInterfaceForPictureInPictureStopWithCompletionHandler completionHandler: @escaping (Bool) -> Void) {
        let source = source
        Task {
            guard source.isCurrent, let restore = source.lease?.restorePictureInPictureUI else {
                completionHandler(false); return
            }
            let restored = await restore()
            completionHandler(restored && source.isCurrent)
        }
    }
    private func finishClose() {
        let waiters = closing; closing.removeAll()
        waiters.forEach { $0.resume() }
    }
}

extension PlaybackVideoPictureInPictureSource: AVPictureInPictureSampleBufferPlaybackDelegate {
    func pictureInPictureController(_ controller: AVPictureInPictureController, setPlaying playing: Bool) {
        setPlaying(playing)
    }
    func pictureInPictureControllerTimeRangeForPlayback(_ controller: AVPictureInPictureController) -> CMTimeRange {
        guard isCurrent else { return .zero }
        guard let duration = transport?.duration else {
            return CMTimeRange(start: .negativeInfinity, duration: .positiveInfinity)
        }
        return CMTimeRange(start: .zero, duration: .init(seconds: duration, preferredTimescale: 600))
    }
    func pictureInPictureControllerIsPlaybackPaused(_ controller: AVPictureInPictureController) -> Bool {
        isPaused
    }
    func pictureInPictureController(_ controller: AVPictureInPictureController,
                                    didTransitionToRenderSize newRenderSize: CMVideoDimensions) {}
    func pictureInPictureController(_ controller: AVPictureInPictureController,
        skipByInterval skipInterval: CMTime, completion completionHandler: @escaping () -> Void) {
        skip(by: skipInterval.seconds, completion: completionHandler)
    }
}
#endif
