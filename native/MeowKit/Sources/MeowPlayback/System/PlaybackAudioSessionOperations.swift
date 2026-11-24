#if os(iOS)
import Foundation
@preconcurrency import AVFoundation

/// Serializes blocking AVAudioSession activation/deactivation on one process-wide lane.
final class PlaybackAudioSessionOperations: @unchecked Sendable {
    private static let queue = DispatchQueue(label: "meow.playback.audio-session")
    // Accessed only on Self.queue.
    private var active = false
    private var requiresActivation = false

    func enqueue(active requested: Bool) -> PlaybackAudioSessionOperation {
        let operation = PlaybackAudioSessionOperation()
        Self.queue.async { [self] in
            var failure: NativePlaybackError?
            if active != requested || (requested && requiresActivation) {
                do {
                    let session = AVAudioSession.sharedInstance()
                    if requested {
                        try session.setCategory(.playback, mode: .default, options: [])
                        try session.setActive(true)
                    } else {
                        try session.setActive(false, options: [.notifyOthersOnDeactivation])
                    }
                    active = requested
                    requiresActivation = false
                } catch {
                    failure = requested ? .audioSessionUnavailable : .audioSessionDeactivationFailed
                    requiresActivation = true
                }
            }
            operation.complete(failure)
        }
        return operation
    }

    /// Called when the OS already deactivated our audio session (interruption began / media services reset).
    /// Updates internal tracking without calling setActive(false) during an active system interruption.
    func markExternallyDeactivated() {
        Self.queue.async { [self] in
            active = false
            requiresActivation = true
        }
    }
}

/// One immutable result. Queue submission precedes awaiting, so suspension can
/// neither reorder OS calls nor lose a result that completes before its waiter.
final class PlaybackAudioSessionOperation: @unchecked Sendable {
    private enum State {
        case pending([CheckedContinuation<NativePlaybackError?, Never>])
        case completed(NativePlaybackError?)
    }
    private let lock = NSLock()
    private var state = State.pending([])

    var value: NativePlaybackError? {
        get async {
            await withCheckedContinuation { continuation in
                lock.lock()
                switch state {
                case .pending(var waiters):
                    waiters.append(continuation)
                    state = .pending(waiters)
                    lock.unlock()
                case .completed(let failure):
                    lock.unlock()
                    continuation.resume(returning: failure)
                }
            }
        }
    }

    fileprivate func complete(_ failure: NativePlaybackError?) {
        lock.lock()
        guard case .pending(let waiters) = state else {
            lock.unlock()
            preconditionFailure("An audio operation completes exactly once")
        }
        state = .completed(failure)
        lock.unlock()
        for waiter in waiters { waiter.resume(returning: failure) }
    }
}
#endif
