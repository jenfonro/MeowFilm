import Foundation
import XCTest
import MeowContracts
@testable import MeowPlayback

/// Deterministic policy/registration tests. Native completion signals are
/// explicit inputs, not real AVPlayer seeks or periodic-position substitutes.
final class PlaybackSeekCompletionTests: XCTestCase {
    private func resource() throws -> PlaybackResource {
        try PlaybackResource(id: "same-resource", url: URL(string: "https://example.invalid/not-requested")!,
                             title: "测试")
    }

    func testAcceptanceReadinessAndPositionSamplesCannotCompleteSeek() async throws {
        let media = try resource()
        try await MainActor.run {
            let h = SeekHarness()
            let attempt = h.play(media)
            var results: [PlaybackSeekResult] = []
            XCTAssertTrue(h.request(45, attempt: attempt) { results.append($0) })
            XCTAssertEqual(h.callbacks.seeks.registrationCount, 1)
            h.state.progress(generation: attempt.id, position: 45)
            h.ready(attempt)
            h.state.progress(generation: attempt.id, position: 45)
            h.publish()
            XCTAssertTrue(results.isEmpty)
            let operation = try XCTUnwrap(h.state.takeSeekIfPossible())
            h.state.progress(generation: attempt.id, position: 45)
            h.state.transport(generation: attempt.id, state: .playing)
            h.publish()
            XCTAssertTrue(results.isEmpty)
            h.complete(operation, finished: true, actual: 44.875)
            XCTAssertEqual(results.count, 1)
            XCTAssertEqual(results[0].attempt, attempt)
            XCTAssertEqual(results[0].outcome, .completed)
            XCTAssertTrue(results[0].succeeded)
            XCTAssertEqual(results[0].actualPosition, 44.875, "Not the requested position or a threshold")
            XCTAssertEqual(h.callbacks.seeks.registrationCount, 0)
        }
    }

    func testFailedPreparationSettlesQueuedSeekWithoutAStartedNativeSeek() async throws {
        let media = try resource()
        try await MainActor.run {
            let h = SeekHarness()
            let attempt = h.play(media)
            var results: [PlaybackSeekResult] = []
            XCTAssertTrue(h.request(30, attempt: attempt) { results.append($0) })
            let pending = try XCTUnwrap(h.state.pendingSeek)
            XCTAssertNil(h.state.takeSeekIfPossible())
            h.state.fail(NativePlaybackError.preparationFailed, generation: attempt.id)
            h.publish()
            XCTAssertEqual(results.map(\.outcome), [.failed])
            XCTAssertFalse(results[0].succeeded)
            XCTAssertNil(results[0].actualPosition)
            h.complete(pending, finished: true, actual: 30)
            XCTAssertEqual(results.count, 1)
        }
    }

    func testStaleAttemptRejectsRequestWithoutCancellingCurrentOperation() async throws {
        let media = try resource()
        try await MainActor.run {
            let h = SeekHarness()
            let old = h.play(media)
            let current = h.play(media)
            h.ready(current)
            var currentResults: [PlaybackSeekResult] = []
            var rejectedCalls = 0
            XCTAssertTrue(h.request(30, attempt: current) { currentResults.append($0) })
            let pending = h.state.outstandingSeek
            XCTAssertFalse(h.request(50, attempt: old) { _ in rejectedCalls += 1 })
            XCTAssertEqual(h.state.outstandingSeek, pending)
            XCTAssertTrue(currentResults.isEmpty)
            XCTAssertEqual(rejectedCalls, 0)
            XCTAssertEqual(h.callbacks.seeks.registrationCount, 1)
        }
    }

    func testInvalidNewSeekDoesNotSupersedeValidAcceptedSeek() async throws {
        let media = try resource()
        try await MainActor.run {
            let h = SeekHarness()
            let attempt = h.play(media)
            h.ready(attempt)
            var results: [PlaybackSeekResult] = []
            var rejectedCalls = 0
            XCTAssertTrue(h.request(30, attempt: attempt) { results.append($0) })
            let pending = h.state.outstandingSeek
            for invalid in [Double.nan, .infinity, -1] {
                XCTAssertFalse(h.request(invalid, attempt: attempt) { _ in rejectedCalls += 1 })
            }
            XCTAssertEqual(h.state.outstandingSeek, pending)
            XCTAssertTrue(results.isEmpty)
            XCTAssertEqual(rejectedCalls, 0)
            let operation = try XCTUnwrap(h.state.takeSeekIfPossible())
            h.complete(operation, finished: true, actual: 30)
            XCTAssertEqual(results.map(\.outcome), [.completed])
        }
    }

    func testResourceReplacementAndABACompleteOnlyTheOriginalCallbackOnce() async throws {
        let media = try resource()
        try await MainActor.run {
            let h = SeekHarness()
            let old = h.play(media, owner: "one")
            h.ready(old)
            var oldResults: [PlaybackSeekResult] = []
            var newResults: [PlaybackSeekResult] = []
            XCTAssertTrue(h.request(20, attempt: old) { oldResults.append($0) })
            let oldOperation = try XCTUnwrap(h.state.takeSeekIfPossible())
            _ = h.play(media, owner: "two")
            let current = h.play(media, owner: "one")
            h.ready(current)
            XCTAssertEqual(oldResults.map(\.outcome), [.replaced])
            XCTAssertEqual(oldResults[0].attempt, old)
            XCTAssertNil(oldResults[0].actualPosition)
            XCTAssertFalse(oldResults[0].succeeded)
            XCTAssertTrue(h.request(60, attempt: current) { newResults.append($0) })
            let newOperation = try XCTUnwrap(h.state.takeSeekIfPossible())
            h.complete(oldOperation, finished: true, actual: 20)
            h.complete(oldOperation, finished: false, actual: 0)
            XCTAssertEqual(oldResults.count, 1)
            XCTAssertTrue(newResults.isEmpty)
            XCTAssertEqual(h.state.activeSeek, newOperation)
            h.complete(newOperation, finished: true, actual: 60.25)
            XCTAssertEqual(newResults.map(\.outcome), [.completed])
            XCTAssertEqual(newResults[0].attempt, current)
            XCTAssertEqual(newResults[0].actualPosition, 60.25)
        }
    }

    func testStopSettlesAcceptedSeekAndLateCompletionCannotResurrectIt() async throws {
        let media = try resource()
        try await MainActor.run {
            let h = SeekHarness()
            let attempt = h.play(media)
            h.ready(attempt)
            var results: [PlaybackSeekResult] = []
            XCTAssertTrue(h.request(40, attempt: attempt) { results.append($0) })
            let operation = try XCTUnwrap(h.state.takeSeekIfPossible())
            XCTAssertFalse(h.state.stop(owner: "other"))
            h.publish()
            XCTAssertTrue(results.isEmpty)
            XCTAssertTrue(h.stop(attempt))
            XCTAssertEqual(results.map(\.outcome), [.stopped])
            XCTAssertNil(results[0].actualPosition)
            h.complete(operation, finished: true, actual: 40)
            XCTAssertEqual(results.count, 1)
            XCTAssertNil(h.state.attempt)
        }
    }

    func testPlaybackFailuresSettleActiveSeekExactlyOnce() async throws {
        let media = try resource()
        try await MainActor.run {
            let errors: [NativePlaybackError] = [.playbackFailed, .audioSessionUnavailable, .mediaServicesReset]
            for error in errors {
                let h = SeekHarness()
                let attempt = h.play(media)
                h.ready(attempt)
                var results: [PlaybackSeekResult] = []
                XCTAssertTrue(h.request(40, attempt: attempt) { results.append($0) })
                let operation = try XCTUnwrap(h.state.takeSeekIfPossible())
                h.state.fail(error, generation: attempt.id)
                h.publish()
                h.complete(operation, finished: true, actual: 40)
                h.state.fail(error, generation: attempt.id)
                h.publish()
                XCTAssertEqual(results.map(\.outcome), [.failed])
                XCTAssertFalse(results[0].succeeded)
                XCTAssertNil(results[0].actualPosition)
            }
        }
    }

    func testTimeoutWinsBeforeLateNativeCompletionAndCannotCompleteTwice() async throws {
        let media = try resource()
        try await MainActor.run {
            let h = SeekHarness()
            let attempt = h.play(media)
            h.ready(attempt)
            var results: [PlaybackSeekResult] = []
            XCTAssertTrue(h.request(40, attempt: attempt) { results.append($0) })
            let operation = try XCTUnwrap(h.state.takeSeekIfPossible())
            h.state.seekTimedOut(generation: attempt.id, id: operation.id)
            h.publish()
            h.complete(operation, finished: true, actual: 40)
            h.state.seekTimedOut(generation: attempt.id, id: operation.id)
            h.publish()
            XCTAssertEqual(results.map(\.outcome), [.timedOut])
            XCTAssertFalse(results[0].succeeded)
            XCTAssertNil(results[0].actualPosition)
            XCTAssertEqual(h.state.error as? NativePlaybackError, .seekTimedOut)
        }
    }

    func testNativeCompletionWinsBeforeOldDeadlineWithoutBeingReclassified() async throws {
        let media = try resource()
        try await MainActor.run {
            let h = SeekHarness()
            let attempt = h.play(media)
            h.ready(attempt)
            var results: [PlaybackSeekResult] = []
            XCTAssertTrue(h.request(40, attempt: attempt) { results.append($0) })
            let operation = try XCTUnwrap(h.state.takeSeekIfPossible())
            h.complete(operation, finished: true, actual: 39.9)
            h.state.seekTimedOut(generation: attempt.id, id: operation.id)
            h.publish()
            h.complete(operation, finished: true, actual: 40)
            XCTAssertEqual(results.map(\.outcome), [.completed])
            XCTAssertEqual(results[0].actualPosition, 39.9)
            XCTAssertNil(h.state.error)
        }
    }

    func testUnseekableReadyTimelineCanTimeOutThePendingOperation() async throws {
        let media = try resource()
        try await MainActor.run {
            let h = SeekHarness()
            let attempt = h.play(media)
            h.state.ready(generation: attempt.id, duration: nil, ranges: [])
            h.publish()
            var results: [PlaybackSeekResult] = []
            XCTAssertTrue(h.request(15, attempt: attempt) { results.append($0) })
            let pending = try XCTUnwrap(h.state.pendingSeek)
            XCTAssertNil(h.state.takeSeekIfPossible())
            h.state.seekTimedOut(generation: attempt.id, id: pending.id)
            h.publish()
            XCTAssertEqual(results.map(\.outcome), [.timedOut])
            XCTAssertEqual(results[0].attempt, attempt)
        }
    }

    func testNewSeekSupersedesOldOperationAndLateOldSignalsDoNotAffectNewOne() async throws {
        let media = try resource()
        try await MainActor.run {
            let h = SeekHarness()
            let attempt = h.play(media)
            h.ready(attempt)
            var first: [PlaybackSeekResult] = []
            var second: [PlaybackSeekResult] = []
            XCTAssertTrue(h.request(20, attempt: attempt) { first.append($0) })
            let old = try XCTUnwrap(h.state.takeSeekIfPossible())
            XCTAssertTrue(h.request(70, attempt: attempt) { second.append($0) })
            let current = try XCTUnwrap(h.state.takeSeekIfPossible())
            XCTAssertEqual(first.map(\.outcome), [.superseded])
            XCTAssertNil(first[0].actualPosition)
            h.complete(old, finished: false, actual: 20)
            h.complete(old, finished: true, actual: 20)
            h.state.seekTimedOut(generation: attempt.id, id: old.id)
            h.publish()
            XCTAssertTrue(second.isEmpty)
            XCTAssertNil(h.state.error)
            XCTAssertEqual(h.state.activeSeek, current)
            h.complete(current, finished: true, actual: 69.75)
            XCTAssertEqual(first.count, 1)
            XCTAssertEqual(second.map(\.outcome), [.completed])
            XCTAssertEqual(second[0].actualPosition, 69.75)
        }
    }

    func testLegacySeekWithoutCompletionStillSettlesPreviousTrackedSeek() async throws {
        let media = try resource()
        try await MainActor.run {
            let h = SeekHarness()
            let attempt = h.play(media)
            h.ready(attempt)
            var results: [PlaybackSeekResult] = []
            XCTAssertTrue(h.request(20, attempt: attempt) { results.append($0) })
            let old = try XCTUnwrap(h.state.takeSeekIfPossible())
            XCTAssertTrue(h.request(50, attempt: attempt, completion: nil))
            XCTAssertEqual(results.map(\.outcome), [.superseded])
            let current = try XCTUnwrap(h.state.takeSeekIfPossible())
            h.complete(old, finished: true, actual: 20)
            h.complete(current, finished: true, actual: 50)
            XCTAssertEqual(results.count, 1)
            XCTAssertEqual(h.callbacks.seeks.registrationCount, 0)
        }
    }

    func testExplicitCancelSettlesPendingAndActiveSeeksAndPausesWithoutRollbackClaim() async throws {
        let media = try resource()
        try await MainActor.run {
            for started in [false, true] {
                let h = SeekHarness()
                let attempt = h.play(media)
                h.ready(attempt)
                h.state.progress(generation: attempt.id, position: 8)
                h.publish()
                var results: [PlaybackSeekResult] = []
                XCTAssertTrue(h.request(40, attempt: attempt) { results.append($0) })
                let operation = try XCTUnwrap(started ? h.state.takeSeekIfPossible() : h.state.pendingSeek)
                XCTAssertTrue(h.cancel(attempt))
                XCTAssertFalse(h.cancel(attempt))
                h.complete(operation, finished: false, actual: 9)
                h.complete(operation, finished: true, actual: 40)
                XCTAssertEqual(results.map(\.outcome), [.cancelled])
                XCTAssertFalse(results[0].succeeded)
                XCTAssertNil(results[0].actualPosition)
                XCTAssertEqual(h.state.snapshot.position, 8, "Last confirmed snapshot, not a native rollback")
                XCTAssertEqual(h.state.snapshot.phase, .paused)
                XCTAssertFalse(h.state.wantsPlayback)
                XCTAssertNil(h.state.error)
            }
        }
    }

    func testStaleCancellationCannotCancelNewAttemptsSeek() async throws {
        let media = try resource()
        try await MainActor.run {
            let h = SeekHarness()
            let old = h.play(media)
            let current = h.play(media)
            h.ready(current)
            var results: [PlaybackSeekResult] = []
            XCTAssertTrue(h.request(50, attempt: current) { results.append($0) })
            let pending = h.state.outstandingSeek
            XCTAssertFalse(h.cancel(old))
            XCTAssertFalse(h.stop(old))
            XCTAssertEqual(h.state.outstandingSeek, pending)
            XCTAssertTrue(results.isEmpty)
        }
    }

    func testInterruptedNativeSeekAndInvalidCompletionSamplesNeverReportSuccess() async throws {
        let media = try resource()
        try await MainActor.run {
            let samples: [(Bool, Double)] = [(false, 40), (true, .nan), (true, .infinity), (true, -1)]
            for (finished, actual) in samples {
                let h = SeekHarness()
                let attempt = h.play(media)
                h.ready(attempt)
                var results: [PlaybackSeekResult] = []
                XCTAssertTrue(h.request(40, attempt: attempt) { results.append($0) })
                let operation = try XCTUnwrap(h.state.takeSeekIfPossible())
                h.complete(operation, finished: finished, actual: actual)
                XCTAssertEqual(results.map(\.outcome), [.failed])
                XCTAssertFalse(results[0].succeeded)
                XCTAssertNil(results[0].actualPosition)
                XCTAssertEqual(h.state.error as? NativePlaybackError, .seekFailed)
            }
        }
    }

    func testSuccessfulSeekToEndDoesNotEmitNaturalEnd() async throws {
        let media = try resource()
        try await MainActor.run {
            let h = SeekHarness()
            let attempt = h.play(media)
            h.ready(attempt)
            var events: [PlaybackEvent.Kind] = []
            var results: [PlaybackSeekResult] = []
            let lease = try h.callbacks.owners.bind(owner: "one") { events.append($0.kind) }
            defer { lease.cancel() }
            XCTAssertTrue(h.request(999, attempt: attempt) { results.append($0) })
            let operation = try XCTUnwrap(h.state.takeSeekIfPossible())
            h.complete(operation, finished: true, actual: 120)
            XCTAssertEqual(results.map(\.outcome), [.completed])
            XCTAssertEqual(results[0].actualPosition, 120)
            XCTAssertEqual(events, [.progress])
            XCTAssertEqual(h.state.snapshot.phase, .ended)
        }
    }

    func testSeekCompletionWhileRestoredPausedDoesNotInventPlayed() async throws {
        let media = try resource()
        try await MainActor.run {
            let h = SeekHarness()
            let attempt = h.play(media, autoplay: false)
            h.ready(attempt)
            var events: [PlaybackEvent.Kind] = []
            var results: [PlaybackSeekResult] = []
            let lease = try h.callbacks.owners.bind(owner: "one") { events.append($0.kind) }
            defer { lease.cancel() }
            XCTAssertTrue(h.request(20, attempt: attempt) { results.append($0) })
            let operation = try XCTUnwrap(h.state.takeSeekIfPossible())
            h.complete(operation, finished: true, actual: 19.99)
            XCTAssertTrue(results[0].succeeded)
            XCTAssertEqual(results[0].actualPosition, 19.99)
            XCTAssertFalse(events.contains(.played))
            XCTAssertEqual(h.state.snapshot.phase, .paused)
            XCTAssertFalse(h.state.shouldPlay)
        }
    }

    func testSuccessfulCompletionCanReenterNewSeekWithoutOldCleanupCancellingIt() async throws {
        let media = try resource()
        try await MainActor.run {
            let h = SeekHarness()
            let attempt = h.play(media)
            h.ready(attempt)
            var results: [PlaybackSeekResult] = []
            var acceptedNext = false
            XCTAssertTrue(h.request(20, attempt: attempt) { result in
                results.append(result)
                acceptedNext = h.request(70, attempt: attempt) { results.append($0) }
            })
            let old = try XCTUnwrap(h.state.takeSeekIfPossible())
            h.complete(old, finished: true, actual: 20)
            XCTAssertTrue(acceptedNext)
            let current = try XCTUnwrap(h.state.takeSeekIfPossible())
            XCTAssertNotEqual(old.id, current.id)
            XCTAssertEqual(h.callbacks.seeks.registrationCount, 1)
            h.complete(old, finished: true, actual: 20)
            XCTAssertEqual(h.state.activeSeek, current)
            h.complete(current, finished: true, actual: 70.1)
            XCTAssertEqual(results.map(\.outcome), [.completed, .completed])
            XCTAssertEqual(results.map(\.actualPosition), [20, 70.1])
            XCTAssertEqual(h.callbacks.seeks.registrationCount, 0)
        }
    }

    func testReplacementCompletionCanReenterAnotherPlayWithoutOverwritingLatestAttempt() async throws {
        let media = try resource()
        try await MainActor.run {
            let h = SeekHarness()
            let old = h.play(media)
            h.ready(old)
            var results: [PlaybackSeekResult] = []
            var latest: PlayAttempt?
            XCTAssertTrue(h.request(30, attempt: old) { result in
                results.append(result)
                XCTAssertFalse(h.stop(result.attempt), "Retired attempt cannot stop its successor")
                latest = h.play(media, owner: "three")
            })
            let operation = try XCTUnwrap(h.state.takeSeekIfPossible())
            let intermediate = h.play(media, owner: "two")
            XCTAssertEqual(h.state.attempt, latest)
            XCTAssertNotEqual(h.state.attempt, intermediate)
            XCTAssertEqual(h.state.snapshot.owner, "three")
            h.complete(operation, finished: true, actual: 30)
            XCTAssertEqual(results.map(\.outcome), [.replaced])
            XCTAssertEqual(h.state.attempt, latest)
        }
    }

    func testLifecycleAndSeekCallbacksShareNonrecursiveDeliveryBoundary() async throws {
        let media = try resource()
        try await MainActor.run {
            let h = SeekHarness()
            let attempt = h.play(media)
            h.ready(attempt)
            var depth = 0
            var maximum = 0
            var outcomes: [PlaybackSeekResult.Outcome] = []
            let lease = try h.callbacks.owners.bind(owner: "one") { event in
                depth += 1
                maximum = max(maximum, depth)
                defer { depth -= 1 }
                if event.kind == .progress {
                    XCTAssertTrue(h.request(80, attempt: attempt) { result in
                        depth += 1
                        maximum = max(maximum, depth)
                        defer { depth -= 1 }
                        outcomes.append(result.outcome)
                    })
                    XCTAssertTrue(h.cancel(attempt))
                }
            }
            defer { lease.cancel() }
            XCTAssertTrue(h.request(40, attempt: attempt) { result in
                depth += 1
                maximum = max(maximum, depth)
                defer { depth -= 1 }
                outcomes.append(result.outcome)
            })
            let operation = try XCTUnwrap(h.state.takeSeekIfPossible())
            h.complete(operation, finished: true, actual: 40)
            XCTAssertEqual(outcomes, [.completed, .cancelled])
            XCTAssertEqual(maximum, 1)
            XCTAssertNil(h.state.outstandingSeek)
            XCTAssertEqual(h.callbacks.seeks.registrationCount, 0)
        }
    }

    func testRegistryRejectsDuplicateOrForeignIdentityAndConsumesBeforeCallback() async throws {
        let media = try resource()
        await MainActor.run {
            var state = PlaybackStateMachine()
            state.begin(resource: media, owner: "one")
            let attempt = state.attempt!
            let foreign = PlayAttempt(id: UUID(), owner: attempt.owner, resourceID: attempt.resourceID)
            let store = PlaybackSeekCompletions()
            let id = UUID()
            let completed = PlaybackSeekResult(attempt: attempt, operationID: id, outcome: .completed, actualPosition: 12)
            var first = 0
            var duplicate = 0
            XCTAssertTrue(store.register(operationID: id, attempt: attempt) { result in
                first += 1
                XCTAssertEqual(store.registrationCount, 0)
                XCTAssertEqual(result, completed)
                store.enqueue([completed])
                store.deliverPending()
            })
            XCTAssertFalse(store.register(operationID: id, attempt: attempt) { _ in duplicate += 1 })
            store.enqueue([PlaybackSeekResult(attempt: foreign, operationID: id, outcome: .completed, actualPosition: 12)])
            XCTAssertEqual(store.registrationCount, 1)
            store.enqueue([completed, completed])
            store.deliverPending()
            XCTAssertEqual(first, 1)
            XCTAssertEqual(duplicate, 0)
            XCTAssertFalse(store.hasPending)
        }
    }

    func testCancelAllDoesNotReclassifyAlreadyCompletedQueuedResult() async throws {
        let media = try resource()
        await MainActor.run {
            var state = PlaybackStateMachine()
            state.begin(resource: media, owner: "one")
            let attempt = state.attempt!
            let store = PlaybackSeekCompletions()
            let id = UUID()
            var results: [PlaybackSeekResult] = []
            XCTAssertTrue(store.register(operationID: id, attempt: attempt) { results.append($0) })
            store.enqueue([PlaybackSeekResult(attempt: attempt, operationID: id, outcome: .completed, actualPosition: 9)])
            store.cancelAll()
            store.deliverPending()
            store.cancelAll()
            store.deliverPending()
            XCTAssertEqual(results.map(\.outcome), [.completed])
            XCTAssertEqual(results[0].actualPosition, 9)
        }
    }

    func testRegistryDeinitCancelsPendingRegistrationAndReleasesCapturedObject() async throws {
        let media = try resource()
        await MainActor.run {
            var state = PlaybackStateMachine()
            state.begin(resource: media, owner: "one")
            let attempt = state.attempt!
            var store: PlaybackSeekCompletions? = PlaybackSeekCompletions()
            weak var weakStore = store
            var owner: SeekCompletionOwner? = SeekCompletionOwner()
            weak var weakOwner = owner
            var results: [PlaybackSeekResult] = []
            XCTAssertTrue(store!.register(operationID: UUID(), attempt: attempt) { [captured = owner!] result in
                captured.calls += 1
                results.append(result)
            })
            owner = nil
            XCTAssertNotNil(weakOwner)
            store = nil
            XCTAssertNil(weakStore)
            XCTAssertNil(weakOwner)
            XCTAssertEqual(results.map(\.outcome), [.cancelled])
            XCTAssertEqual(results[0].attempt, attempt)
            XCTAssertFalse(results[0].succeeded)
            XCTAssertNil(results[0].actualPosition)
        }
    }
}

@MainActor
private final class SeekHarness {
    var state = PlaybackStateMachine()
    let callbacks = PlaybackCallbackDelivery()

    @discardableResult
    func play(_ resource: PlaybackResource, owner: String = "one", autoplay: Bool = true) -> PlayAttempt {
        state.begin(resource: resource, owner: owner, autoplay: autoplay)
        let attempt = state.attempt!
        publish()
        return attempt
    }

    func ready(_ attempt: PlayAttempt) {
        state.ready(generation: attempt.id, duration: 120, ranges: [])
        publish()
    }

    func request(
        _ position: TimeInterval, attempt: PlayAttempt,
        completion: (@MainActor (PlaybackSeekResult) -> Void)?
    ) -> Bool {
        guard state.attempt == attempt, state.requestSeek(to: position),
              let operation = state.outstandingSeek else { return false }
        if let completion {
            guard callbacks.seeks.register(operationID: operation.id, attempt: attempt, completion: completion) else {
                return false
            }
        }
        publish()
        return true
    }

    func complete(_ operation: PlaybackStateMachine.Seek, finished: Bool, actual: TimeInterval) {
        state.completeSeek(operation, finished: finished, actualPosition: actual)
        publish()
    }

    @discardableResult
    func cancel(_ attempt: PlayAttempt) -> Bool {
        guard state.attempt == attempt, state.cancelSeek() else { return false }
        publish()
        return true
    }

    @discardableResult
    func stop(_ attempt: PlayAttempt) -> Bool {
        guard state.attempt == attempt, state.stop(owner: attempt.owner) else { return false }
        publish()
        return true
    }

    func publish() {
        callbacks.owners.updateContext(state.commandContext)
        callbacks.owners.enqueue(state.takeEvents())
        callbacks.seeks.enqueue(state.takeSeekResults())
        callbacks.deliverPending()
    }
}

@MainActor
private final class SeekCompletionOwner {
    var calls = 0
}
