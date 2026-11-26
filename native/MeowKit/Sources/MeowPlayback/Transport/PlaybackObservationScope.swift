import Foundation

/// Owns *removal* actions, not just callback generation guards. The adapter
/// stores NotificationCenter, timer and MPRemoteCommand target removals here.
@MainActor
final class PlaybackObservationScope {
    private final class Box: @unchecked Sendable {
        var removals: [@MainActor () -> Void] = []
        @MainActor func cancelAll() {
            let current = removals
            removals.removeAll()
            for remove in current.reversed() { remove() }
        }
    }

    private nonisolated let box = Box()

    nonisolated init() {}

    func insert(_ removal: @escaping @MainActor () -> Void) {
        box.removals.append(removal)
    }

    func cancel() {
        box.cancelAll()
    }

    nonisolated deinit {
        let box = box
        if Thread.isMainThread {
            MainActor.assumeIsolated { box.cancelAll() }
        } else {
            DispatchQueue.main.async {
                MainActor.assumeIsolated { box.cancelAll() }
            }
        }
    }
}
