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

    // The controller's lifetime holder releases internal scopes on MainActor,
    // even after a background final release. Keep removal synchronous.
    nonisolated deinit {
        MainActor.assumeIsolated { cancel() }
    }
}
