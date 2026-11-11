import Foundation

/// Blocking system calls have one process-wide serial lane, including release
/// from a retired controller before a successor controller activates audio.
struct PlaybackAudioSessionActions: Sendable {
    let activate: @Sendable () throws -> Void
    let deactivate: @Sendable () throws -> Void
}

final class PlaybackAudioSessionOperations: @unchecked Sendable {
    private static let queue = DispatchQueue(label: "meow.playback.audio-session")
    private let actions: PlaybackAudioSessionActions
    // Accessed only on queue. Failed release retains the actual active fact so
    // the next explicit release retries rather than silently discarding it.
    private var active = false
    private var requiresActivation = false

    init(actions: PlaybackAudioSessionActions) { self.actions = actions }

    func enqueue(active requested: Bool) -> PlaybackAudioSessionOperation {
        let operation = PlaybackAudioSessionOperation()
        Self.queue.async { [self] in
            var failure: NativePlaybackError?
            if active != requested || (requested && requiresActivation) {
                do {
                    if requested { try actions.activate() } else { try actions.deactivate() }
                    active = requested
                    requiresActivation = false
                } catch {
                    failure = requested ? .audioSessionUnavailable : .audioSessionDeactivationFailed
                    // A failed release preserves active output, but a later
                    // acquire must still perform real system admission.
                    requiresActivation = true
                }
            }
            operation.complete(failure)
        }
        return operation
    }

    func invalidateActivation() {
        Self.queue.async { [self] in requiresActivation = true }
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
