import Foundation
import XCTest
import MeowContracts
@testable import MeowPlayback

/// Exercises the same state/event/command routing used by the native adapter,
/// without constructing an AVPlayer or mounting a presentation view.
final class PlaybackOwnerRouterTests: XCTestCase {
    private func resource(id: String = "shared-id") throws -> PlaybackResource {
        try PlaybackResource(id: id, url: URL(string: "https://example.invalid/not-requested")!, title: "测试")
    }

    func testOwnerReceivesLifecycleWithoutAnyPageAndOtherOwnerDoesNot() async throws {
        let media = try resource()
        try await MainActor.run {
            let h = LifecycleHarness()
            var one: [PlaybackEvent] = []
            var two: [PlaybackEvent] = []
            let a = try h.router.bind(owner: "one") { one.append($0) }
            let b = try h.router.bind(owner: "two") { two.append($0) }
            defer { a.cancel(); b.cancel() }
            let attempt = h.play(media, owner: "one")
            h.state.ready(generation: attempt.id, duration: 120, ranges: [])
            h.state.transport(generation: attempt.id, state: .playing)
            h.state.progress(generation: attempt.id, position: 20)
            h.state.pause()
            h.publish()
            h.state.end(generation: attempt.id)
            h.publish()
            XCTAssertEqual(one.map(\.kind), [.preparing, .prepared, .played, .progress, .paused, .ended])
            XCTAssertTrue(one.allSatisfy { $0.attempt == attempt })
            XCTAssertTrue(two.isEmpty)
        }
    }

    func testSwitchDeliversFinalSnapshotToOldOwnerAndNewEventsOnlyToNewOwner() async throws {
        let media = try resource()
        try await MainActor.run {
            let h = LifecycleHarness()
            var one: [PlaybackEvent] = []
            var two: [PlaybackEvent] = []
            let a = try h.router.bind(owner: "one") { one.append($0) }
            let b = try h.router.bind(owner: "two") { two.append($0) }
            defer { a.cancel(); b.cancel() }
            let old = h.play(media, owner: "one")
            h.state.ready(generation: old.id, duration: 120, ranges: [])
            h.state.progress(generation: old.id, position: 42)
            h.publish()
            let current = h.play(media, owner: "two")
            let exit = try XCTUnwrap(one.last)
            XCTAssertEqual(exit.kind, .replaced)
            XCTAssertEqual(exit.attempt, old)
            XCTAssertEqual(exit.snapshot.position, 42)
            XCTAssertEqual(two.map(\.kind), [.preparing])
            XCTAssertEqual(two.first?.attempt, current)
            h.state.progress(generation: old.id, position: 100)
            h.state.end(generation: old.id)
            h.publish()
            XCTAssertEqual(one.last?.kind, .replaced)
            XCTAssertEqual(two.count, 1)
        }
    }

    func testQueuedOldSuccessCannotCrossSameResourceABA() async throws {
        let media = try resource()
        try await MainActor.run {
            let h = LifecycleHarness()
            var events: [PlaybackEvent] = []
            let lease = try h.router.bind(owner: "one") { events.append($0) }
            defer { lease.cancel() }
            let old = h.play(media, owner: "one")
            h.state.ready(generation: old.id, duration: 120, ranges: [])
            h.router.enqueue(h.state.takeEvents()) // Capture, intentionally delay delivery.
            _ = h.play(media, owner: "two")
            let current = h.play(media, owner: "one")
            h.router.deliverPending()
            XCTAssertFalse(events.contains { $0.kind == .prepared })
            XCTAssertEqual(events.filter { $0.kind == .preparing }.map(\.attempt), [old, current])
            XCTAssertEqual(events.filter { $0.kind == .replaced }.map(\.attempt), [old])
        }
    }

    func testReentrantPreparedReplacementDropsRemainingOldBatchWithoutRecursiveCallbacks() async throws {
        let media = try resource()
        try await MainActor.run {
            let h = LifecycleHarness()
            var events: [PlaybackEvent] = []
            var depth = 0
            var maximumDepth = 0
            var next: PlayAttempt?
            let a = try h.router.bind(owner: "one") { event in
                depth += 1
                maximumDepth = max(maximumDepth, depth)
                defer { depth -= 1 }
                events.append(event)
                if event.kind == .prepared { next = h.play(media, owner: "two") }
            }
            let b = try h.router.bind(owner: "two") { event in
                depth += 1
                maximumDepth = max(maximumDepth, depth)
                defer { depth -= 1 }
                events.append(event)
            }
            defer { a.cancel(); b.cancel() }
            let old = h.play(media, owner: "one")
            h.state.ready(generation: old.id, duration: 120, ranges: [])
            h.state.transport(generation: old.id, state: .playing)
            h.state.progress(generation: old.id, position: 4)
            h.publish()
            XCTAssertEqual(maximumDepth, 1)
            XCTAssertEqual(events.map(\.kind), [.preparing, .prepared, .replaced, .preparing])
            XCTAssertEqual(h.state.attempt, next)
            XCTAssertEqual(h.state.snapshot.phase, .preparing)
            XCTAssertFalse(events.contains { $0.kind == .played || $0.kind == .progress })
        }
    }

    func testReentrantNaturalEndCanStartNextAttemptAndOldStackDoesNotStopIt() async throws {
        let media = try resource()
        try await MainActor.run {
            let h = LifecycleHarness()
            var ends: [PlayAttempt] = []
            var next: PlayAttempt?
            let lease = try h.router.bind(owner: "one") { event in
                if event.kind == .ended {
                    ends.append(event.attempt)
                    next = h.play(media, owner: "one")
                }
            }
            defer { lease.cancel() }
            let old = h.play(media, owner: "one")
            h.state.ready(generation: old.id, duration: 120, ranges: [])
            h.publish()
            h.state.end(generation: old.id)
            h.publish()
            XCTAssertEqual(ends, [old])
            XCTAssertEqual(h.state.attempt, next)
            XCTAssertTrue(h.state.wantsPlayback)
            h.state.end(generation: old.id)
            h.state.fail(NativePlaybackError.playbackFailed, generation: old.id)
            h.publish()
            XCTAssertEqual(ends, [old])
            XCTAssertEqual(h.state.snapshot.phase, .preparing)
        }
    }

    func testReentrantStopDropsRemainingSuccessAndDeliversExitOnce() async throws {
        let media = try resource()
        try await MainActor.run {
            let h = LifecycleHarness()
            var kinds: [PlaybackEvent.Kind] = []
            let lease = try h.router.bind(owner: "one") { event in
                kinds.append(event.kind)
                if event.kind == .prepared {
                    h.state.stop(owner: "one")
                    h.publish()
                }
            }
            defer { lease.cancel() }
            let attempt = h.play(media, owner: "one")
            h.state.ready(generation: attempt.id, duration: 120, ranges: [])
            h.state.transport(generation: attempt.id, state: .playing)
            h.publish()
            XCTAssertEqual(kinds, [.preparing, .prepared, .stopped])
            XCTAssertNil(h.state.attempt)
            XCTAssertFalse(h.state.commandContext.isActive)
        }
    }

    func testSelfCancellationDropsRemainingQueuedEventsAndCommands() async throws {
        let media = try resource()
        try await MainActor.run {
            let h = LifecycleHarness()
            var events: [PlaybackEvent.Kind] = []
            var lease: PlaybackOwnerLease?
            lease = try h.router.bind(owner: "one", commands: .init(next: { _ in true })) { event in
                events.append(event.kind)
                if event.kind == .prepared { lease?.cancel() }
            }
            defer { lease?.cancel(); lease = nil }
            let attempt = h.play(media, owner: "one")
            let route = try XCTUnwrap(h.router.navigationRoutes.next)
            h.state.ready(generation: attempt.id, duration: 120, ranges: [])
            h.state.transport(generation: attempt.id, state: .playing)
            h.state.progress(generation: attempt.id, position: 8)
            h.publish()
            XCTAssertEqual(events, [.preparing, .prepared])
            XCTAssertTrue(lease?.isCancelled == true)
            XCTAssertNil(h.router.navigationRoutes.next)
            XCTAssertFalse(h.router.perform(route))
        }
    }

    func testCancelBeforeDeliveryIsIdempotentAndDoesNotStopMedia() async throws {
        let media = try resource()
        try await MainActor.run {
            let h = LifecycleHarness()
            var count = 0
            let lease = try h.router.bind(owner: "one") { _ in count += 1 }
            let attempt = h.play(media, owner: "one")
            h.state.ready(generation: attempt.id, duration: 120, ranges: [])
            h.router.enqueue(h.state.takeEvents())
            lease.cancel()
            lease.cancel()
            h.router.deliverPending()
            XCTAssertEqual(count, 1)
            XCTAssertTrue(lease.isCancelled)
            XCTAssertFalse(lease.isEnabled)
            XCTAssertFalse(lease.setEnabled(true))
            XCTAssertFalse(lease.updateCommands(.init()))
            XCTAssertEqual(h.state.attempt, attempt)
            XCTAssertTrue(h.state.shouldPlay)
        }
    }

    func testLeaseDeinitRemovesBindingAndCapabilities() async throws {
        let media = try resource()
        try await MainActor.run {
            let h = LifecycleHarness()
            var calls = 0
            var lease: PlaybackOwnerLease? = try h.router.bind(
                owner: "one", commands: .init(next: { _ in true }),
                onEvent: { _ in calls += 1 }
            )
            weak var weakLease = lease
            _ = h.play(media, owner: "one")
            XCTAssertNotNil(h.router.navigationRoutes.next)
            lease = nil
            XCTAssertNil(weakLease)
            XCTAssertNil(h.router.navigationRoutes.next)
            _ = h.play(media, owner: "one")
            XCTAssertEqual(calls, 1)
        }
    }

    func testCancelledBindingReleasesCapturedDomainObject() async throws {
        try await MainActor.run {
            let router = PlaybackOwnerRouter()
            var sentinel: DomainLifetimeSentinel? = DomainLifetimeSentinel()
            weak var reference = sentinel
            let lease = try router.bind(owner: "one") { [captured = sentinel!] _ in captured.calls += 1 }
            sentinel = nil
            XCTAssertNotNil(reference)
            lease.cancel()
            XCTAssertNil(reference)
        }
    }

    func testDestroyingRouterCancelsSurvivingLeaseWithoutRetainingIt() async throws {
        try await MainActor.run {
            var router: PlaybackOwnerRouter? = PlaybackOwnerRouter()
            weak var reference = router
            let lease = try router!.bind(owner: "one") { _ in }
            router = nil
            XCTAssertNil(reference)
            XCTAssertTrue(lease.isCancelled)
            XCTAssertFalse(lease.updateCommands(.init(next: { _ in true })))
        }
    }

    func testOldLeaseCannotCancelOrUpdateReplacementBinding() async throws {
        let media = try resource()
        try await MainActor.run {
            let h = LifecycleHarness()
            var oldCalls = 0
            var newCalls = 0
            let old = try h.router.bind(owner: "one") { _ in oldCalls += 1 }
            let current = try h.router.bind(owner: "one", commands: .init(next: { _ in true })) { _ in newCalls += 1 }
            defer { current.cancel() }
            XCTAssertTrue(old.isCancelled)
            XCTAssertFalse(old.setEnabled(false))
            XCTAssertFalse(old.updateCommands(.init()))
            old.cancel()
            _ = h.play(media, owner: "one")
            XCTAssertEqual(oldCalls, 0)
            XCTAssertEqual(newCalls, 1)
            XCTAssertNotNil(h.router.navigationRoutes.next)
        }
    }

    func testReplacingBindingDoesNotRetargetAlreadyQueuedEvents() async throws {
        let media = try resource()
        try await MainActor.run {
            let h = LifecycleHarness()
            var oldCalls = 0
            var newCalls = 0
            let old = try h.router.bind(owner: "one") { _ in oldCalls += 1 }
            let attempt = h.play(media, owner: "one")
            h.state.ready(generation: attempt.id, duration: 120, ranges: [])
            h.router.enqueue(h.state.takeEvents())
            let current = try h.router.bind(owner: "one") { _ in newCalls += 1 }
            defer { old.cancel(); current.cancel() }
            h.router.deliverPending()
            XCTAssertEqual(oldCalls, 1)
            XCTAssertEqual(newCalls, 0)
            h.state.progress(generation: attempt.id, position: 12)
            h.publish()
            XCTAssertEqual(newCalls, 1)
        }
    }

    func testBindingAfterPlaybackDoesNotSynthesizeOrReplayPlayed() async throws {
        let media = try resource()
        try await MainActor.run {
            let h = LifecycleHarness()
            let attempt = h.play(media, owner: "one")
            h.state.ready(generation: attempt.id, duration: 120, ranges: [])
            h.state.transport(generation: attempt.id, state: .playing)
            h.state.pause()
            h.publish()
            var events: [PlaybackEvent.Kind] = []
            let lease = try h.router.bind(owner: "one") { events.append($0.kind) }
            defer { lease.cancel() }
            h.publish()
            XCTAssertTrue(events.isEmpty)
            XCTAssertEqual(h.state.snapshot.phase, .paused)
            h.state.progress(generation: attempt.id, position: 4)
            h.publish()
            XCTAssertEqual(events, [.progress])
        }
    }

    func testDisabledOwnerReceivesNothingAndEnableDoesNotReplay() async throws {
        let media = try resource()
        try await MainActor.run {
            let h = LifecycleHarness()
            var events: [PlaybackEvent.Kind] = []
            let lease = try h.router.bind(owner: "one", enabled: false,
                                         commands: .init(next: { _ in true })) { events.append($0.kind) }
            defer { lease.cancel() }
            let attempt = h.play(media, owner: "one")
            h.state.ready(generation: attempt.id, duration: 120, ranges: [])
            h.state.transport(generation: attempt.id, state: .playing)
            h.publish()
            XCTAssertTrue(events.isEmpty)
            XCTAssertNil(h.router.navigationRoutes.next)
            XCTAssertTrue(lease.setEnabled(true))
            h.publish()
            XCTAssertTrue(events.isEmpty)
            h.state.progress(generation: attempt.id, position: 2)
            h.publish()
            XCTAssertEqual(events, [.progress])
            XCTAssertNotNil(h.router.navigationRoutes.next)
        }
    }

    func testDisableEnableDoesNotResurrectQueuedEventsOrCommandRoute() async throws {
        let media = try resource()
        try await MainActor.run {
            let h = LifecycleHarness()
            var count = 0
            let lease = try h.router.bind(owner: "one", commands: .init(next: { _ in true })) { _ in count += 1 }
            defer { lease.cancel() }
            let attempt = h.play(media, owner: "one")
            let old = try XCTUnwrap(h.router.navigationRoutes.next)
            h.state.ready(generation: attempt.id, duration: 120, ranges: [])
            h.router.enqueue(h.state.takeEvents())
            lease.setEnabled(false)
            lease.setEnabled(true)
            h.router.deliverPending()
            XCTAssertEqual(count, 1)
            XCTAssertFalse(h.router.perform(old))
            XCTAssertTrue(h.router.perform(try XCTUnwrap(h.router.navigationRoutes.next)))
        }
    }

    func testReentrantBindingReplacementDoesNotReceiveRemainingOldBatch() async throws {
        let media = try resource()
        try await MainActor.run {
            let h = LifecycleHarness()
            var oldKinds: [PlaybackEvent.Kind] = []
            var newKinds: [PlaybackEvent.Kind] = []
            var replacement: PlaybackOwnerLease?
            let old = try h.router.bind(owner: "one") { event in
                oldKinds.append(event.kind)
                if event.kind == .prepared {
                    replacement = try? h.router.bind(owner: "one") { newKinds.append($0.kind) }
                }
            }
            defer { old.cancel(); replacement?.cancel() }
            let attempt = h.play(media, owner: "one")
            h.state.ready(generation: attempt.id, duration: 120, ranges: [])
            h.state.transport(generation: attempt.id, state: .playing)
            h.publish()
            XCTAssertTrue(old.isCancelled)
            XCTAssertNotNil(replacement)
            XCTAssertEqual(oldKinds, [.preparing, .prepared])
            XCTAssertTrue(newKinds.isEmpty)
            h.state.progress(generation: attempt.id, position: 6)
            h.publish()
            XCTAssertEqual(newKinds, [.progress])
        }
    }

    func testEmptyOwnerRejectedBeforeBindingChangesOrCommands() async throws {
        try await MainActor.run {
            let router = PlaybackOwnerRouter()
            var changes = 0
            router.onBindingsChanged = { changes += 1 }
            XCTAssertThrowsError(try router.bind(owner: " \n ") { _ in }) {
                XCTAssertEqual($0 as? ContractError, .invalidIdentifier)
            }
            XCTAssertEqual(changes, 0)
            XCTAssertNil(router.navigationRoutes.previous)
            XCTAssertNil(router.navigationRoutes.next)
        }
    }

    func testNavigationRequiresExplicitCurrentOwnerHandler() async throws {
        let media = try resource()
        try await MainActor.run {
            let h = LifecycleHarness()
            var calls: [PlayAttempt] = []
            let one = try h.router.bind(owner: "one", commands: .init(next: { calls.append($0); return true })) { _ in }
            let two = try h.router.bind(owner: "two") { _ in }
            defer { one.cancel(); two.cancel() }
            let current = h.play(media, owner: "two")
            XCTAssertNil(h.router.navigationRoutes.previous)
            XCTAssertNil(h.router.navigationRoutes.next)
            two.updateCommands(.init(previous: { calls.append($0); return true }))
            XCTAssertNil(h.router.navigationRoutes.next)
            let route = try XCTUnwrap(h.router.navigationRoutes.previous)
            XCTAssertTrue(h.router.perform(route))
            XCTAssertEqual(calls, [current])
            _ = h.play(media, owner: "unregistered")
            XCTAssertNil(h.router.navigationRoutes.previous)
            XCTAssertNil(h.router.navigationRoutes.next)
        }
    }

    func testNavigationRoutesBindGenerationAcrossOwnersAndSameResourceABA() async throws {
        let media = try resource()
        try await MainActor.run {
            let h = LifecycleHarness()
            var calls: [PlayAttempt] = []
            let one = try h.router.bind(owner: "one", commands: .init(next: { calls.append($0); return true })) { _ in }
            let two = try h.router.bind(owner: "two", commands: .init(next: { calls.append($0); return true })) { _ in }
            defer { one.cancel(); two.cancel() }
            _ = h.play(media, owner: "one")
            let old = try XCTUnwrap(h.router.navigationRoutes.next)
            _ = h.play(media, owner: "two")
            let other = try XCTUnwrap(h.router.navigationRoutes.next)
            let current = h.play(media, owner: "one")
            XCTAssertFalse(h.router.perform(old))
            XCTAssertFalse(h.router.perform(other))
            XCTAssertTrue(calls.isEmpty)
            XCTAssertTrue(h.router.perform(try XCTUnwrap(h.router.navigationRoutes.next)))
            XCTAssertEqual(calls, [current])
        }
    }

    func testUpdatingCommandCapabilityRevokesCapturedRouteWithoutLosingBusinessEvents() async throws {
        let media = try resource()
        try await MainActor.run {
            let h = LifecycleHarness()
            var calls: [String] = []
            var kinds: [PlaybackEvent.Kind] = []
            let lease = try h.router.bind(owner: "one", commands: .init(next: { _ in calls.append("old"); return true })) {
                kinds.append($0.kind)
            }
            defer { lease.cancel() }
            let attempt = h.play(media, owner: "one")
            let route = try XCTUnwrap(h.router.navigationRoutes.next)
            h.state.ready(generation: attempt.id, duration: 120, ranges: [])
            h.router.enqueue(h.state.takeEvents())
            lease.updateCommands(.init(previous: { _ in calls.append("new"); return true }))
            h.router.deliverPending()
            XCTAssertEqual(kinds, [.preparing, .prepared])
            XCTAssertFalse(h.router.perform(route))
            XCTAssertNil(h.router.navigationRoutes.next)
            XCTAssertTrue(h.router.perform(try XCTUnwrap(h.router.navigationRoutes.previous)))
            XCTAssertEqual(calls, ["new"])
        }
    }

    func testReplacingBindingRevokesCapturedNavigationEvenForSameAttempt() async throws {
        let media = try resource()
        try await MainActor.run {
            let h = LifecycleHarness()
            var calls = 0
            let old = try h.router.bind(owner: "one", commands: .init(next: { _ in calls += 1; return true })) { _ in }
            _ = h.play(media, owner: "one")
            let route = try XCTUnwrap(h.router.navigationRoutes.next)
            let current = try h.router.bind(owner: "one", commands: .init(next: { _ in calls += 10; return true })) { _ in }
            defer { old.cancel(); current.cancel() }
            XCTAssertFalse(h.router.perform(route))
            XCTAssertTrue(h.router.perform(try XCTUnwrap(h.router.navigationRoutes.next)))
            XCTAssertEqual(calls, 10)
        }
    }

    func testNavigationHandlerResultIsReturnedWithoutAutomaticFallback() async throws {
        let media = try resource()
        try await MainActor.run {
            let h = LifecycleHarness()
            var selected = 0
            var other = 0
            let one = try h.router.bind(owner: "one", commands: .init(next: { _ in selected += 1; return false })) { _ in }
            let two = try h.router.bind(owner: "two", commands: .init(next: { _ in other += 1; return true })) { _ in }
            defer { one.cancel(); two.cancel() }
            let attempt = h.play(media, owner: "one")
            XCTAssertFalse(h.router.perform(try XCTUnwrap(h.router.navigationRoutes.next)))
            XCTAssertEqual(selected, 1)
            XCTAssertEqual(other, 0)
            XCTAssertEqual(h.state.attempt, attempt)
        }
    }

    func testNavigationHandlerCanReplacePlaybackWithoutOldCommandWritingAfterward() async throws {
        let media = try resource()
        try await MainActor.run {
            let h = LifecycleHarness()
            var passed: PlayAttempt?
            var next: PlayAttempt?
            let one = try h.router.bind(owner: "one", commands: .init(next: { attempt in
                passed = attempt
                next = h.play(media, owner: "two")
                return true
            })) { _ in }
            let two = try h.router.bind(owner: "two", commands: .init(previous: { _ in true })) { _ in }
            defer { one.cancel(); two.cancel() }
            let old = h.play(media, owner: "one")
            let route = try XCTUnwrap(h.router.navigationRoutes.next)
            XCTAssertTrue(h.router.perform(route))
            XCTAssertEqual(passed, old)
            XCTAssertEqual(h.state.attempt, next)
            XCTAssertNil(h.router.navigationRoutes.next)
            XCTAssertEqual(h.router.navigationRoutes.previous?.attempt, next)
            XCTAssertFalse(h.router.perform(route))
            XCTAssertTrue(h.state.wantsPlayback)
        }
    }

    func testNavigationHandlerCanCancelItselfWithoutASecondInvocation() async throws {
        let media = try resource()
        try await MainActor.run {
            let h = LifecycleHarness()
            var count = 0
            var lease: PlaybackOwnerLease?
            lease = try h.router.bind(owner: "one", commands: .init(next: { _ in
                count += 1
                lease?.cancel()
                return true
            })) { _ in }
            defer { lease?.cancel(); lease = nil }
            _ = h.play(media, owner: "one")
            let route = try XCTUnwrap(h.router.navigationRoutes.next)
            XCTAssertTrue(h.router.perform(route))
            XCTAssertFalse(h.router.perform(route))
            XCTAssertEqual(count, 1)
            XCTAssertNil(h.router.navigationRoutes.next)
        }
    }

    func testIdleFailedAndStoppedHaveNoNavigationButEndedCanUseExplicitNext() async throws {
        let media = try resource()
        try await MainActor.run {
            let h = LifecycleHarness()
            let lease = try h.router.bind(owner: "one", commands: .init(previous: { _ in true }, next: { _ in true })) { _ in }
            defer { lease.cancel() }
            XCTAssertNil(h.router.navigationRoutes.previous)
            XCTAssertNil(h.router.navigationRoutes.next)
            let attempt = h.play(media, owner: "one")
            let route = try XCTUnwrap(h.router.navigationRoutes.next)
            h.state.ready(generation: attempt.id, duration: 120, ranges: [])
            h.state.end(generation: attempt.id)
            h.publish()
            XCTAssertTrue(h.router.perform(route))
            h.state.fail(NativePlaybackError.playbackFailed, generation: attempt.id)
            h.publish()
            XCTAssertNil(h.router.navigationRoutes.previous)
            XCTAssertNil(h.router.navigationRoutes.next)
            XCTAssertFalse(h.router.perform(route))
            h.state.stop()
            h.publish()
            XCTAssertNil(h.router.navigationRoutes.next)
            XCTAssertFalse(h.router.perform(route))
        }
    }
}

@MainActor
private final class LifecycleHarness {
    var state = PlaybackStateMachine()
    let router = PlaybackOwnerRouter()

    @discardableResult
    func play(_ resource: PlaybackResource, owner: String) -> PlayAttempt {
        state.begin(resource: resource, owner: owner)
        let attempt = state.attempt!
        publish()
        return attempt
    }

    func publish() {
        router.updateContext(state.commandContext)
        router.enqueue(state.takeEvents())
        router.deliverPending()
    }
}

@MainActor
private final class DomainLifetimeSentinel {
    var calls = 0
}
