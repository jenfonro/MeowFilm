import Foundation

/// Completion of one accepted seek, not of a whole playback attempt.
/// Domain command IDs belong in the caller's closure, not in the shared player.
public struct PlaybackSeekResult: Equatable, Sendable {
    public enum Outcome: Equatable, Sendable {
        case completed, superseded, replaced, stopped, cancelled, failed, timedOut
    }

    public let attempt: PlayAttempt
    public let outcome: Outcome
    public var succeeded: Bool { outcome == .completed }
    /// A sample read in the matching successful native seek completion.
    /// Never the requested position, a periodic approximation or failure sample.
    public let actualPosition: TimeInterval?
    let operationID: UUID

    init(
        attempt: PlayAttempt, operationID: UUID, outcome: Outcome,
        actualPosition: TimeInterval? = nil
    ) {
        self.attempt = attempt
        self.operationID = operationID
        let validCompletion = outcome == .completed
            && actualPosition?.isFinite == true && (actualPosition ?? -1) >= 0
        self.outcome = outcome == .completed && !validCompletion ? .failed : outcome
        self.actualPosition = validCompletion ? actualPosition : nil
    }
}

/// Foundation-only single-shot registrations. Entries are consumed BEFORE
/// invoking client code, including results for an already retired attempt.
@MainActor
final class PlaybackSeekCompletions {
    private struct Registration {
        let attempt: PlayAttempt
        let completion: @MainActor (PlaybackSeekResult) -> Void
    }

    private struct Delivery {
        let result: PlaybackSeekResult
        let completion: @MainActor (PlaybackSeekResult) -> Void
    }

    private final class Storage: @unchecked Sendable {
        var registrations: [UUID: Registration] = [:]
        var pending: [Delivery] = []
        var isDelivering = false

        @MainActor func cancelAllAndDeliver() {
            let current = registrations
            registrations.removeAll()
            for (id, registration) in current {
                pending.append(Delivery(
                    result: PlaybackSeekResult(attempt: registration.attempt, operationID: id, outcome: .cancelled),
                    completion: registration.completion
                ))
            }
            deliverPending()
        }

        @MainActor func deliverPending() {
            guard !isDelivering else { return }
            isDelivering = true
            var index = 0
            defer {
                pending.removeAll(keepingCapacity: false)
                isDelivering = false
            }
            while index < pending.count {
                let delivery = pending[index]
                index += 1
                delivery.completion(delivery.result)
            }
        }
    }

    private nonisolated let storage = Storage()

    var hasPending: Bool { !storage.pending.isEmpty }

    @discardableResult
    func register(
        operationID: UUID, attempt: PlayAttempt,
        completion: @escaping @MainActor (PlaybackSeekResult) -> Void
    ) -> Bool {
        guard storage.registrations[operationID] == nil else { return false }
        storage.registrations[operationID] = Registration(attempt: attempt, completion: completion)
        return true
    }

    func enqueue(_ results: [PlaybackSeekResult]) {
        for result in results {
            guard let registration = storage.registrations[result.operationID],
                  registration.attempt == result.attempt else { continue }
            storage.registrations.removeValue(forKey: result.operationID)
            storage.pending.append(Delivery(result: result, completion: registration.completion))
        }
    }

    func deliverPending() {
        storage.deliverPending()
    }

    nonisolated deinit {
        let storage = storage
        if Thread.isMainThread {
            MainActor.assumeIsolated { storage.cancelAllAndDeliver() }
        } else {
            DispatchQueue.main.async {
                MainActor.assumeIsolated { storage.cancelAllAndDeliver() }
            }
        }
    }
}

/// One controller's callback boundary, not a global event bus. Sharing this
/// drain prevents lifecycle and seek callbacks from recursively draining one
/// another when a client replaces playback or issues another seek.
@MainActor
final class PlaybackCallbackDelivery {
    let owners = PlaybackOwnerRouter()
    let seeks = PlaybackSeekCompletions()
    private var isDelivering = false

    func deliverPending() {
        guard !isDelivering else { return }
        isDelivering = true
        defer { isDelivering = false }
        while seeks.hasPending || owners.hasPending {
            seeks.deliverPending()
            owners.deliverPending()
        }
    }
}
