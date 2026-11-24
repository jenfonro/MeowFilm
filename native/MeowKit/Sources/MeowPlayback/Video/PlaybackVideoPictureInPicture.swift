#if os(iOS)
import Foundation
@preconcurrency import AVKit
import MeowContracts
import MeowDebug

/// One immutable command fence per ContentSource. The OS may retain an older
/// source after the same PiP controller has been rebound to a newer attempt.
@MainActor
final class PlaybackVideoPictureInPictureSource: NSObject {
    weak var transport: PlaybackVideoTransport?
    weak var lease: PlaybackVideoLease?
    private weak var playback: PlaybackController?
    let attempt: PlayAttempt

    init(transport: PlaybackVideoTransport, lease: PlaybackVideoLease,
         playback: PlaybackController, attempt: PlayAttempt) {
        self.transport = transport; self.lease = lease; self.playback = playback; self.attempt = attempt
        super.init()
    }
    var isCurrent: Bool { playback?.isCurrent(attempt) == true && lease?.currentAttempt == attempt }
    var isPaused: Bool { !isCurrent || playback?.wantsPlayback != true }

    func setPlaying(_ playing: Bool) {
        guard isCurrent, let playback else { return }
        _ = playback.systemCommand(playing ? .play : .pause, generation: attempt.id)
    }
    func skip(by interval: Double, completion: @escaping () -> Void) {
        guard isCurrent, let playback, let transport else { completion(); return }
        let target = max(0, transport.position + interval)
        if !playback.seek(to: target, attempt: attempt, completion: { _ in completion() }) { completion() }
    }
}

/// The OS controller may move between successful attempts in ONE video lease.
/// ContentSource delegates do not move: each captures its immutable attempt, so
/// a delayed control from the preceding source cannot operate the new owner.
@MainActor
final class PlaybackVideoPictureInPicture: NSObject, AVPictureInPictureControllerDelegate {
    private var source: PlaybackVideoPictureInPictureSource
    private var controller: AVPictureInPictureController?
    private var observation: NSKeyValueObservation?
    private var closing: [CheckedContinuation<Void, Never>] = []
    private var closeTimeoutTask: Task<Void, Never>?
    private var invalidated = false
    private var startRequested = false
    private var stopRequested = false
    private var rebinding = false
    private var systemOwnerAttempt: PlayAttempt?

    init(transport: PlaybackVideoTransport, lease: PlaybackVideoLease,
         controller: PlaybackController, attempt: PlayAttempt) {
        source = .init(transport: transport, lease: lease, playback: controller, attempt: attempt)
        super.init()
        bind(transport: transport)
    }

    private func bind(transport: PlaybackVideoTransport) {
        guard AVPictureInPictureController.isPictureInPictureSupported(),
              let content = contentSource(transport) else { return }
        let pip = AVPictureInPictureController(contentSource: content)
        self.controller = pip; pip.delegate = self
        pip.canStartPictureInPictureAutomaticallyFromInline = false
        observation = pip.observe(\.isPictureInPicturePossible, options: [.initial, .new]) { [weak self] _, _ in
            Task { @MainActor [weak self] in self?.publishActualState() }
        }
    }

    private func contentSource(_ transport: PlaybackVideoTransport) -> AVPictureInPictureController.ContentSource? {
        if let playerLayer = transport.output.playerLayer { return .init(playerLayer: playerLayer) }
        if let displayLayer = transport.output.displayLayer {
            return .init(sampleBufferDisplayLayer: displayLayer, playbackDelegate: source)
        }
        return nil
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
        let previousTransport = source.transport
        source = .init(transport: transport, lease: lease, playback: controller, attempt: attempt)
        let content = contentSource(transport)
        if previousTransport !== transport || content == nil {
            previousTransport?.output.setPictureInPictureActive(false)
        }
        guard let content else {
            // New output has no PiP-capable layer: tear down the stale OS controller cleanly.
            observation?.invalidate(); observation = nil
            self.controller?.stopPictureInPicture()
            self.controller?.delegate = nil
            self.controller?.contentSource = nil
            self.controller = nil
            startRequested = false; stopRequested = false
            systemOwnerAttempt = nil
            Task { @MainActor [weak self] in
                guard let self, self.isCurrent, let currentTransport = self.source.transport else { return }
                self.source.lease?.updatePictureInPicture(.unavailable, transport: currentTransport,
                                                          attempt: self.source.attempt, systemOwnerEnded: owned)
            }
            return
        }
        if owned { systemOwnerAttempt = attempt }
        rebinding = true
        if let pictureInPicture = self.controller {
            pictureInPicture.contentSource = content
        } else {
            bind(transport: transport)
        }
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
        DebugRuntime.log(.filmPlayback, sub: "pip", event: "start.request", level: .info, data: .object([
            "attemptId": .optional(source.transport?.media.attemptID)
        ]))
        publishActualState()
        guard isCurrent else { startRequested = false; return false }
        controller.startPictureInPicture(); return true
    }

    func stop() {
        guard let controller else { return }
        if startRequested || controller.isPictureInPictureActive { stopRequested = true }
        DebugRuntime.log(.filmPlayback, sub: "pip", event: "stop.request", level: .info, data: .object([
            "attemptId": .optional(source.transport?.media.attemptID)
        ]))
        controller.stopPictureInPicture()
    }

    func invalidateState() { controller?.invalidatePlaybackState() }

    func close() async {
        invalidated = true
        observation?.invalidate(); observation = nil
        if let controller, startRequested || controller.isPictureInPictureActive || controller.isPictureInPictureSuspended {
            // AVKit treats stopPictureInPicture() as a no-op when PiP is not yet active or suspended;
            // bound exit wait so close() never deadlocks playback drain or app shutdown.
            closeTimeoutTask?.cancel()
            closeTimeoutTask = Task { @MainActor [weak self] in
                do { try await Task.sleep(for: .seconds(2)) } catch { return }
                guard let self else { return }
                DebugRuntime.log(.filmPlayback, sub: "pip", event: "close.timeout", level: .warn)
                self.finishClose()
            }
            await withTaskCancellationHandler {
                await withCheckedContinuation { continuation in
                    closing.append(continuation)
                    controller.stopPictureInPicture()
                }
            } onCancel: {
                Task { @MainActor [weak self] in self?.finishClose() }
            }
            closeTimeoutTask?.cancel()
            closeTimeoutTask = nil
        }
        controller?.delegate = nil; controller?.contentSource = nil; controller = nil
        source.transport?.output.setPictureInPictureActive(false)
        startRequested = false; stopRequested = false
        systemOwnerAttempt = nil
    }

    private func publishActualState() {
        publish(state)
    }

    private func publish(_ state: PlaybackVideoPictureInPictureState) {
        guard !rebinding, isCurrent, let transport = source.transport else { return }
        transport.output.setPictureInPictureActive(state == .active || state == .starting)
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
        DebugRuntime.log(.filmPlayback, sub: "pip", event: "started", level: .info, data: .object([
            "attemptId": .optional(source.transport?.media.attemptID)
        ]))
        guard isCurrent, !stopRequested else { controller.stopPictureInPicture(); return }
        publishActualState()
    }
    func pictureInPictureController(_ controller: AVPictureInPictureController,
                                   failedToStartPictureInPictureWithError error: Error) {
        startRequested = false; stopRequested = false
        DebugRuntime.log(.filmPlayback, sub: "pip", event: "failed", level: .error, data: .object([
            "attemptId": .optional(source.transport?.media.attemptID),
            "reason": .string(error.localizedDescription)
        ]))
        publish(.failed)
        finishClose()
    }
    func pictureInPictureControllerWillStopPictureInPicture(_ controller: AVPictureInPictureController) {
        stopRequested = true; publishActualState()
    }
    func pictureInPictureControllerDidStopPictureInPicture(_ controller: AVPictureInPictureController) {
        startRequested = false; stopRequested = false
        DebugRuntime.log(.filmPlayback, sub: "pip", event: "stopped", level: .info, data: .object([
            "attemptId": .optional(source.transport?.media.attemptID)
        ]))
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
        closeTimeoutTask?.cancel()
        closeTimeoutTask = nil
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
