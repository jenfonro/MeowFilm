import Foundation

/// Owns *removal* actions, not just callback generation guards. The adapter
/// stores KVO invalidations, NotificationCenter removals, AVPlayer time-observer
/// removals and MPRemoteCommand target removals here.
@MainActor
final class PlaybackObservationScope {
    private var removals: [() -> Void] = []

    var count: Int { removals.count }

    func insert(_ removal: @escaping () -> Void) {
        removals.append(removal)
    }

    func cancel() {
        let current = removals
        removals.removeAll()
        for remove in current.reversed() { remove() }
    }

    // Swift 6.2 compiler, including Swift 5 language mode. Explicit cancellation
    // is the deterministic path; this is a main-actor-safe final safety net.
    isolated deinit {
        cancel()
    }
}
