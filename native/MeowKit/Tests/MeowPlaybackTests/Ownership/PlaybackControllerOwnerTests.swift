#if os(iOS) || os(macOS)
import Foundation
import Combine
import AVFoundation
import XCTest
import MeowContracts
@testable import MeowPlayback

/// Only passive construction and deliberately rejected preflight. These tests
/// do not create AVPlayerItem, read media, contact a network or activate audio.
final class PlaybackControllerOwnerTests: XCTestCase {
    private func rejectedResource(id: String = "same", title: String = "测试") throws -> PlaybackResource {
        try PlaybackResource(id: id, url: URL(fileURLWithPath: "/not-read/by-tests"),
                             title: title, headers: ["X-Test": "synthetic"])
    }

    func testRegisterEnableUpdateAndCancelArePassive() async throws {
        try await MainActor.run {
            let controller = PlaybackController()
            let player = controller.player
            var callbacks = 0
            let lease = try controller.bind(
                owner: "one", commands: .init(previous: { _ in callbacks += 1; return true },
                                             next: { _ in callbacks += 1; return true })
            ) { _ in callbacks += 1 }
            XCTAssertFalse(lease.isCancelled)
            XCTAssertTrue(lease.isEnabled)
            lease.setEnabled(false)
            lease.setEnabled(true)
            lease.updateCommands(.init())
            lease.cancel()
            XCTAssertEqual(callbacks, 0)
            XCTAssertNil(controller.currentAttempt)
            XCTAssertNil(controller.player.currentItem)
            XCTAssertTrue(controller.player === player)
            XCTAssertEqual(player.rate, 0)
            XCTAssertEqual(controller.snapshot, PlaybackSnapshot())
            XCTAssertNil(controller.lastError)
        }
    }

    func testFailedAttemptIsReturnedAndEventKeepsTypedFailureAndIdentity() async throws {
        let resource = try rejectedResource()
        try await MainActor.run {
            let controller = PlaybackController()
            var events: [PlaybackEvent] = []
            let lease = try controller.bind(owner: "one") { events.append($0) }
            defer { lease.cancel() }
            let attempt = controller.playAttempt(resource: resource, owner: "one", autoplay: false)
            XCTAssertEqual(controller.currentAttempt, attempt)
            XCTAssertTrue(controller.isCurrent(attempt))
            XCTAssertEqual(events.map(\.kind), [.failed])
            XCTAssertEqual(events.first?.attempt, attempt)
            XCTAssertEqual(events.first?.snapshot, controller.snapshot)
            XCTAssertEqual(events.first?.error as? PlaybackNetworkError, .headersOnLocalFile)
            XCTAssertNil(controller.player.currentItem)
            XCTAssertFalse(controller.pause(attempt: attempt))
            XCTAssertFalse(controller.resume(attempt: attempt))
            XCTAssertFalse(controller.seek(to: 20, attempt: attempt))
            XCTAssertTrue(controller.stop(attempt: attempt))
            XCTAssertFalse(controller.isCurrent(attempt))
        }
    }

    func testSameResourceABARejectsOldAttemptScopedControlsAndStop() async throws {
        let media = try rejectedResource()
        await MainActor.run {
            let controller = PlaybackController()
            let old = controller.playAttempt(resource: media, owner: "one")
            _ = controller.playAttempt(resource: media, owner: "two")
            let current = controller.playAttempt(resource: media, owner: "one")
            XCTAssertNotEqual(old, current)
            XCTAssertFalse(controller.isCurrent(old))
            XCTAssertFalse(controller.pause(attempt: old))
            XCTAssertFalse(controller.resume(attempt: old))
            XCTAssertFalse(controller.seek(to: 3, attempt: old))
            XCTAssertFalse(controller.stop(attempt: old))
            XCTAssertEqual(controller.currentAttempt, current)
            XCTAssertTrue(controller.stop(attempt: current))
            XCTAssertNil(controller.currentAttempt)
            XCTAssertFalse(controller.stop(attempt: current))
        }
    }

    func testIdenticalSnapshotStillPublishesEachAttemptForPresentationRebinding() async throws {
        let media = try rejectedResource()
        await MainActor.run {
            let controller = PlaybackController()
            var attempts: [PlayAttempt] = []
            var snapshots: [PlaybackSnapshot] = []
            let attemptObservation = controller.$currentAttempt.sink {
                if let attempt = $0 { attempts.append(attempt) }
            }
            let snapshotObservation = controller.$snapshot.sink { snapshots.append($0) }
            defer { attemptObservation.cancel(); snapshotObservation.cancel() }
            let first = controller.playAttempt(resource: media, owner: "one")
            let second = controller.playAttempt(resource: media, owner: "one")
            XCTAssertEqual(attempts, [first, second])
            XCTAssertEqual(snapshots.count, 2, "Initial idle plus a single distinct failed snapshot")
            XCTAssertNotEqual(first.id, second.id)
            XCTAssertEqual(controller.currentAttempt, second)
            XCTAssertNil(controller.player.currentItem)
        }
    }

    func testCompletionSeekRejectsFailedStaleAndStoppedAttemptsWithoutCallback() async throws {
        let media = try rejectedResource()
        await MainActor.run {
            let controller = PlaybackController()
            let old = controller.playAttempt(resource: media, owner: "one")
            let current = controller.playAttempt(resource: media, owner: "one")
            var calls = 0
            XCTAssertFalse(controller.seek(to: 20, attempt: old) { _ in calls += 1 })
            XCTAssertFalse(controller.seek(to: 20, attempt: current) { _ in calls += 1 })
            XCTAssertFalse(controller.seek(to: .nan, attempt: current) { _ in calls += 1 })
            XCTAssertFalse(controller.cancelSeek(attempt: old))
            XCTAssertFalse(controller.cancelSeek(attempt: current))
            controller.stop()
            XCTAssertFalse(controller.seek(to: 20, attempt: current) { _ in calls += 1 })
            XCTAssertEqual(calls, 0)
            XCTAssertNil(controller.player.currentItem)
        }
    }

    func testFailureCallbackCanReplaceAttemptWithoutOldPublicationOverwritingIt() async throws {
        let first = try rejectedResource(id: "first", title: "旧标题")
        let second = try rejectedResource(id: "second", title: "新标题")
        try await MainActor.run {
            let controller = PlaybackController()
            let player = controller.player
            var next: PlayAttempt?
            var events: [PlaybackEvent] = []
            let one = try controller.bind(owner: "one") { event in
                events.append(event)
                if event.kind == .failed {
                    next = controller.playAttempt(resource: second, owner: "two")
                }
            }
            let two = try controller.bind(owner: "two") { events.append($0) }
            defer { one.cancel(); two.cancel() }
            let returned = controller.playAttempt(resource: first, owner: "one")
            XCTAssertEqual(events.map(\.kind), [.failed, .replaced, .failed])
            XCTAssertEqual(events.first?.attempt, returned)
            XCTAssertEqual(events.first?.snapshot.resourceID, "first")
            XCTAssertEqual(events.last?.attempt, next)
            XCTAssertNotEqual(returned, next)
            XCTAssertEqual(controller.currentAttempt, next)
            XCTAssertEqual(controller.snapshot.resourceID, "second")
            XCTAssertEqual(controller.snapshot.owner, "two")
            XCTAssertEqual(controller.title, "新标题")
            XCTAssertFalse(controller.stop(attempt: returned))
            XCTAssertTrue(controller.player === player)
            XCTAssertNil(controller.player.currentItem)
        }
    }

    func testFailureCallbackCanStopAndNoFailedStateIsRepublishedAfterIt() async throws {
        let media = try rejectedResource()
        try await MainActor.run {
            let controller = PlaybackController()
            var events: [PlaybackEvent.Kind] = []
            let lease = try controller.bind(owner: "one") { event in
                events.append(event.kind)
                if event.kind == .failed { XCTAssertTrue(controller.stop(attempt: event.attempt)) }
            }
            defer { lease.cancel() }
            let attempt = controller.playAttempt(resource: media, owner: "one")
            XCTAssertEqual(events, [.failed, .stopped])
            XCTAssertFalse(controller.isCurrent(attempt))
            XCTAssertNil(controller.currentAttempt)
            XCTAssertNil(controller.lastError)
            XCTAssertEqual(controller.title, "")
            XCTAssertEqual(controller.snapshot, PlaybackSnapshot())
            XCTAssertNil(controller.player.currentItem)
        }
    }

    func testRetiringCallbackCanReplaceAgainAndIntermediateFailureIsNotRetargeted() async throws {
        let first = try rejectedResource(id: "first")
        let second = try rejectedResource(id: "second")
        let third = try rejectedResource(id: "third", title: "最终标题")
        try await MainActor.run {
            let controller = PlaybackController()
            var latest: PlayAttempt?
            var secondEvents: [PlaybackEvent.Kind] = []
            let one = try controller.bind(owner: "one") { event in
                if event.kind == .replaced { latest = controller.playAttempt(resource: third, owner: "three") }
            }
            let two = try controller.bind(owner: "two") { secondEvents.append($0.kind) }
            let three = try controller.bind(owner: "three") { _ in }
            defer { one.cancel(); two.cancel(); three.cancel() }
            _ = controller.playAttempt(resource: first, owner: "one")
            let intermediate = controller.playAttempt(resource: second, owner: "two")
            XCTAssertEqual(controller.currentAttempt, latest)
            XCTAssertNotEqual(controller.currentAttempt, intermediate)
            XCTAssertEqual(controller.snapshot.resourceID, "third")
            XCTAssertEqual(controller.title, "最终标题")
            XCTAssertEqual(secondEvents, [.replaced])
            XCTAssertNil(controller.player.currentItem)
        }
    }

    func testCancelledOrDisabledDomainDoesNotReceiveNewFailures() async throws {
        let media = try rejectedResource()
        try await MainActor.run {
            let controller = PlaybackController()
            var calls = 0
            let lease = try controller.bind(owner: "one", enabled: false) { _ in calls += 1 }
            _ = controller.playAttempt(resource: media, owner: "one")
            XCTAssertEqual(calls, 0)
            lease.setEnabled(true)
            XCTAssertEqual(calls, 0)
            _ = controller.playAttempt(resource: media, owner: "one")
            XCTAssertEqual(calls, 2, "Exit of the failed attempt and failure of the new attempt")
            lease.cancel()
            _ = controller.playAttempt(resource: media, owner: "one")
            controller.stop()
            XCTAssertEqual(calls, 2)
        }
    }

    func testControllerReleaseDoesNotLeaveRouterRetainedByLease() async throws {
        try await MainActor.run {
            var controller: PlaybackController? = PlaybackController()
            weak var reference = controller
            let lease = try controller!.bind(owner: "one", commands: .init(next: { _ in true })) { _ in }
            controller = nil
            XCTAssertNil(reference)
            XCTAssertTrue(lease.isCancelled)
            XCTAssertFalse(lease.updateCommands(.init()))
            lease.cancel()
        }
    }

    func testSynchronousPublishedSubscriberCannotLeaveOldSnapshotOverNewAttempt() async throws {
        let first = try rejectedResource(id: "first", title: "旧")
        let second = try rejectedResource(id: "second", title: "新")
        try await MainActor.run {
            let controller = PlaybackController()
            var next: PlayAttempt?
            var events: [PlaybackEvent] = []
            let one = try controller.bind(owner: "one") { events.append($0) }
            let two = try controller.bind(owner: "two") { events.append($0) }
            let observation = controller.$snapshot.sink { snapshot in
                if snapshot.owner == "one", snapshot.phase == .failed {
                    next = controller.playAttempt(resource: second, owner: "two")
                }
            }
            defer { observation.cancel(); one.cancel(); two.cancel() }
            let old = controller.playAttempt(resource: first, owner: "one")
            XCTAssertEqual(controller.currentAttempt, next)
            XCTAssertNotEqual(old, next)
            XCTAssertEqual(controller.snapshot.owner, "two")
            XCTAssertEqual(controller.snapshot.resourceID, "second")
            XCTAssertEqual(controller.title, "新")
            XCTAssertEqual(events.map(\.kind), [.replaced, .failed])
            XCTAssertEqual(events.last?.attempt, next)
            XCTAssertNil(controller.player.currentItem)
        }
    }
}
#endif
