#if os(iOS) || os(macOS)
import Foundation
import MeowContracts

/// OS boundary, not a second media owner. macOS tests can drive the same
/// transfer/retirement policy without claiming that a fake is real system PiP.
@MainActor
protocol PlaybackVideoPictureInPictureControlling: AnyObject {
    var isActive: Bool { get }
    var hasSystemOwner: Bool { get }
    var state: PlaybackVideoPictureInPictureState { get }
    func rebind(transport: PlaybackVideoTransport, lease: PlaybackVideoLease,
                controller: PlaybackController, attempt: PlayAttempt)
    func start() -> Bool
    func stop()
    func invalidateState()
    func close() async
}

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
#endif
