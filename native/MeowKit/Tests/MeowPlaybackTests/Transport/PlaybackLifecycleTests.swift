import Foundation
import XCTest
import MeowContracts
@testable import MeowPlayback

/// Pure lifecycle policy. No AVPlayer, AVURLAsset, I/O, clock or UI.
final class PlaybackLifecycleTests: XCTestCase {
    private func resource(
        id: String = "same-resource", start: TimeInterval = 0, headers: [String: String] = [:]
    ) throws -> PlaybackResource {
        try PlaybackResource(id: id, url: URL(string: "https://example.invalid/not-requested")!,
                             title: "测试", headers: headers, startPosition: start)
    }

    func testAttemptIdentityIsIndependentOfResourceAndEachPlayIsNew() throws {
        var state = PlaybackStateMachine()
        let media = try resource()
        let a = state.begin(resource: media, owner: "one")
        let first = try XCTUnwrap(state.attempt)
        let b = state.begin(resource: media, owner: "two")
        let c = state.begin(resource: media, owner: "one")
        XCTAssertEqual(first.id, a)
        XCTAssertEqual(first.owner, "one")
        XCTAssertEqual(first.resourceID, media.id)
        XCTAssertNotEqual(a, b)
        XCTAssertNotEqual(a, c)
        XCTAssertNotEqual(b, c)
        XCTAssertEqual(state.attempt?.resourceID, first.resourceID)
        XCTAssertNotEqual(state.attempt, first)
    }

    func testPreparedRequiresReadinessAndOnlyActualTransportProducesPlayed() throws {
        var state = PlaybackStateMachine()
        let token = state.begin(resource: try resource(start: 20), owner: "one")
        XCTAssertEqual(state.takeEvents().map(\.kind), [.preparing])
        state.resume()
        XCTAssertTrue(state.takeEvents().isEmpty)
        state.ready(generation: token, duration: 120, ranges: [])
        state.ready(generation: token, duration: 120, ranges: [])
        XCTAssertEqual(state.takeEvents().map(\.kind), [.prepared])
        let seek = try XCTUnwrap(state.takeSeekIfPossible())
        state.completeSeek(seek, finished: true, actualPosition: 20)
        XCTAssertEqual(state.takeEvents().map(\.kind), [.progress])
        state.transport(generation: token, state: .waiting)
        XCTAssertTrue(state.takeEvents().isEmpty)
        state.transport(generation: token, state: .playing)
        state.transport(generation: token, state: .playing)
        XCTAssertEqual(state.takeEvents().map(\.kind), [.played])
    }

    func testRestorePausedPreparesAndSeeksWithoutPlayedOrAutoplay() throws {
        var state = PlaybackStateMachine()
        let token = state.begin(resource: try resource(start: 34), owner: "one", autoplay: false)
        XCTAssertFalse(state.wantsPlayback)
        XCTAssertEqual(state.snapshot.phase, .paused)
        _ = state.takeEvents()
        state.ready(generation: token, duration: 120, ranges: [])
        let prepared = try XCTUnwrap(state.takeEvents().first)
        XCTAssertEqual(prepared.kind, .prepared)
        XCTAssertEqual(prepared.snapshot.phase, .paused)
        let seek = try XCTUnwrap(state.takeSeekIfPossible())
        state.completeSeek(seek, finished: true, actualPosition: 34)
        XCTAssertEqual(state.takeEvents().map(\.kind), [.progress])
        XCTAssertEqual(state.snapshot.phase, .paused)
        XCTAssertFalse(state.shouldPlay)
        state.resume()
        XCTAssertTrue(state.takeEvents().isEmpty)
        state.transport(generation: token, state: .playing)
        XCTAssertEqual(state.takeEvents().map(\.kind), [.played])
    }

    func testPauseAndNativePauseAreOneEdgeButActualResumeCanPlayAgain() throws {
        var state = PlaybackStateMachine()
        let token = state.begin(resource: try resource(), owner: "one")
        state.ready(generation: token, duration: 120, ranges: [])
        state.transport(generation: token, state: .playing)
        _ = state.takeEvents()
        state.pause()
        state.pause()
        state.transport(generation: token, state: .paused)
        XCTAssertEqual(state.takeEvents().map(\.kind), [.paused])
        state.resume()
        XCTAssertTrue(state.takeEvents().isEmpty)
        state.transport(generation: token, state: .playing)
        XCTAssertEqual(state.takeEvents().map(\.kind), [.played])
    }

    func testNativePauseDoesNotRequireAControllerPauseCall() throws {
        var state = PlaybackStateMachine()
        let token = state.begin(resource: try resource(), owner: "one")
        state.ready(generation: token, duration: 120, ranges: [])
        state.transport(generation: token, state: .playing)
        _ = state.takeEvents()
        state.transport(generation: token, state: .paused)
        state.transport(generation: token, state: .paused)
        XCTAssertEqual(state.takeEvents().map(\.kind), [.paused])
        XCTAssertFalse(state.wantsPlayback)
    }

    func testInterruptedAndResumedIntentIsNotActualPlayed() throws {
        var state = PlaybackStateMachine()
        let token = state.begin(resource: try resource(), owner: "one")
        state.ready(generation: token, duration: 120, ranges: [])
        state.transport(generation: token, state: .playing)
        _ = state.takeEvents()
        state.interruptionBegan()
        state.transport(generation: token, state: .paused)
        state.interruptionEnded(shouldResume: true)
        XCTAssertEqual(state.takeEvents().map(\.kind), [.paused])
        XCTAssertTrue(state.shouldPlay)
        state.transport(generation: token, state: .playing)
        XCTAssertEqual(state.takeEvents().map(\.kind), [.played])
    }

    func testStopEndsInterruptionTrackingSoLaterExplicitPlayCannotRemainLatched() throws {
        var state = PlaybackStateMachine()
        let old = state.begin(resource: try resource(), owner: "one")
        state.ready(generation: old, duration: 120, ranges: [])
        state.interruptionBegan()
        state.stop()
        XCTAssertFalse(state.isInterrupted)
        // The platform observer scope is gone while stopped; no end callback
        // needs to arrive before an unrelated explicit new attempt can proceed.
        let current = state.begin(resource: try resource(), owner: "two")
        state.ready(generation: current, duration: 120, ranges: [])
        XCTAssertTrue(state.shouldPlay)
        XCTAssertFalse(state.accepts(old))
    }

    func testTerminalFailureEndsInterruptionTrackingWithoutAutomaticRecovery() throws {
        var state = PlaybackStateMachine()
        let old = state.begin(resource: try resource(), owner: "one")
        state.ready(generation: old, duration: 120, ranges: [])
        state.interruptionBegan()
        state.fail(NativePlaybackError.playbackFailed, generation: old)
        XCTAssertFalse(state.isInterrupted)
        XCTAssertFalse(state.resume())
        let current = state.begin(resource: try resource(), owner: "one")
        state.ready(generation: current, duration: 120, ranges: [])
        XCTAssertTrue(state.shouldPlay)
    }

    func testProgressUsesFiniteActualSamplesAndSuppressesDuplicatesAndPendingSeek() throws {
        var state = PlaybackStateMachine()
        let token = state.begin(resource: try resource(start: 50), owner: "one")
        state.progress(generation: token, position: 0)
        state.ready(generation: token, duration: 120, ranges: [])
        let seek = try XCTUnwrap(state.takeSeekIfPossible())
        state.progress(generation: token, position: 2)
        XCTAssertEqual(state.takeEvents().map(\.kind), [.preparing, .prepared])
        state.completeSeek(seek, finished: true, actualPosition: 49.9)
        let event = try XCTUnwrap(state.takeEvents().first)
        XCTAssertEqual(event.kind, .progress)
        XCTAssertEqual(event.snapshot.position, 49.9)
        for position in [49.9, Double.nan, .infinity, -1] {
            state.progress(generation: token, position: position)
        }
        XCTAssertTrue(state.takeEvents().isEmpty)
        state.progress(generation: token, position: 55)
        XCTAssertEqual(state.takeEvents().first?.snapshot.position, 55)
        XCTAssertEqual(event.snapshot.position, 49.9, "Prior samples are immutable")
    }

    func testProgressReportsChangedDurationEvenAtSamePosition() throws {
        var state = PlaybackStateMachine()
        let token = state.begin(resource: try resource(), owner: "one")
        state.ready(generation: token, duration: 120, ranges: [])
        state.progress(generation: token, position: 5)
        _ = state.takeEvents()
        state.updateTimeline(generation: token, duration: 130, ranges: [])
        state.progress(generation: token, position: 5)
        let event = try XCTUnwrap(state.takeEvents().first)
        XCTAssertEqual(event.kind, .progress)
        XCTAssertEqual(event.snapshot.duration, 130)
    }

    func testNaturalEndKeepsObservedPositionAndIsOncePerAttemptEvenAfterLegacyReplay() throws {
        var state = PlaybackStateMachine()
        let token = state.begin(resource: try resource(), owner: "one")
        state.ready(generation: token, duration: 120, ranges: [])
        _ = state.takeEvents()
        XCTAssertTrue(state.end(generation: token))
        XCTAssertFalse(state.end(generation: token))
        let events = state.takeEvents()
        XCTAssertEqual(events.map(\.kind), [.ended])
        XCTAssertEqual(events.first?.attempt.id, token)
        XCTAssertEqual(events.first?.snapshot.position, 0)
        XCTAssertEqual(state.snapshot.position, 120)
        state.resume()
        let seek = try XCTUnwrap(state.takeSeekIfPossible())
        XCTAssertFalse(state.end(generation: token), "End cannot finish an active replay seek")
        state.completeSeek(seek, finished: true, actualPosition: 0)
        state.transport(generation: token, state: .playing)
        state.end(generation: token)
        XCTAssertFalse(state.takeEvents().contains { $0.kind == .ended })
        XCTAssertEqual(state.snapshot.phase, .ended)
    }

    func testSeekingToEndIsNotNaturalEndOrQueueAdvance() throws {
        var state = PlaybackStateMachine()
        let token = state.begin(resource: try resource(start: 999), owner: "one")
        state.ready(generation: token, duration: 120, ranges: [])
        _ = state.takeEvents()
        let seek = try XCTUnwrap(state.takeSeekIfPossible())
        state.completeSeek(seek, finished: true, actualPosition: 120)
        XCTAssertEqual(state.snapshot.phase, .ended)
        XCTAssertFalse(state.end(generation: token))
        XCTAssertEqual(state.takeEvents().map(\.kind), [.progress])
        // A later explicit replay can still have its first natural end.
        state.resume()
        let replay = try XCTUnwrap(state.takeSeekIfPossible())
        state.completeSeek(replay, finished: true, actualPosition: 0)
        state.transport(generation: token, state: .playing)
        state.end(generation: token)
        XCTAssertEqual(state.takeEvents().filter { $0.kind == .ended }.count, 1)
    }

    func testNewAttemptAllowsAnotherNaturalEndForTheSameResource() throws {
        var state = PlaybackStateMachine()
        let media = try resource()
        for _ in 0..<3 {
            let token = state.begin(resource: media, owner: "one")
            state.ready(generation: token, duration: 120, ranges: [])
            _ = state.takeEvents()
            state.end(generation: token)
            XCTAssertEqual(state.takeEvents().map(\.kind), [.ended])
        }
    }


    func testOldGenerationCannotEmitAnyLifecycleEventAfterABA() throws {
        var state = PlaybackStateMachine()
        let media = try resource()
        let old = state.begin(resource: media, owner: "one")
        state.ready(generation: old, duration: 120, ranges: [])
        state.begin(resource: try resource(id: "b"), owner: "two")
        let current = state.begin(resource: media, owner: "one")
        _ = state.takeEvents()
        state.ready(generation: old, duration: 500, ranges: [])
        state.transport(generation: old, state: .playing)
        state.progress(generation: old, position: 80)
        state.end(generation: old)
        state.fail(NativePlaybackError.playbackFailed, generation: old)
        XCTAssertTrue(state.takeEvents().isEmpty)
        XCTAssertEqual(state.attempt?.id, current)
        XCTAssertEqual(state.snapshot.phase, .preparing)
    }

    func testFailureIsOnceAndBlocksAllLaterSuccessEvents() throws {
        var state = PlaybackStateMachine()
        let token = state.begin(resource: try resource(), owner: "one")
        state.ready(generation: token, duration: 120, ranges: [])
        state.progress(generation: token, position: 10)
        _ = state.takeEvents()
        state.fail(NativePlaybackError.playbackFailed, generation: token)
        state.fail(NativePlaybackError.preparationFailed, generation: token)
        state.ready(generation: token, duration: 120, ranges: [])
        state.progress(generation: token, position: 12)
        state.transport(generation: token, state: .playing)
        state.end(generation: token)
        let events = state.takeEvents()
        XCTAssertEqual(events.map(\.kind), [.failed])
        XCTAssertEqual(events.first?.error as? NativePlaybackError, .playbackFailed)
        XCTAssertEqual(events.first?.snapshot.position, 10)
        XCTAssertEqual(events.first?.attempt.id, token)
    }

    func testPreflightFailureHasIdentityButNoInventedPreparation() throws {
        var state = PlaybackStateMachine()
        let token = state.begin(resource: try resource(headers: ["Range": "bytes=0-"]), owner: "one")
        let events = state.takeEvents()
        XCTAssertEqual(events.map(\.kind), [.failed])
        XCTAssertEqual(events.first?.attempt.id, token)
        XCTAssertEqual(events.first?.error as? PlaybackNetworkError, .invalidHeaders)
        XCTAssertFalse(state.commandContext.isActive)
    }

    func testReplacementAndStopKeepTheRetiringSnapshotWithoutNaturalEnd() throws {
        var state = PlaybackStateMachine()
        let token = state.begin(resource: try resource(), owner: "one")
        state.ready(generation: token, duration: 120, ranges: [])
        state.progress(generation: token, position: 23)
        _ = state.takeEvents()
        let next = state.begin(resource: try resource(id: "episode"), owner: "two")
        let events = state.takeEvents()
        XCTAssertEqual(events.map(\.kind), [.replaced, .preparing])
        XCTAssertEqual(events[0].attempt.id, token)
        XCTAssertEqual(events[0].snapshot.position, 23)
        XCTAssertEqual(events[0].snapshot.owner, "one")
        XCTAssertEqual(events[1].attempt.id, next)
        XCTAssertFalse(state.stop(owner: "one"))
        XCTAssertTrue(state.takeEvents().isEmpty)
        XCTAssertTrue(state.stop(owner: "two"))
        XCTAssertEqual(state.takeEvents().map(\.kind), [.stopped])
        XCTAssertTrue(state.stop())
        XCTAssertTrue(state.takeEvents().isEmpty)
    }

    func testIdleAndFailedRejectEveryTransportCommand() throws {
        let commands: [PlaybackTransportCommand] = [.play, .pause, .toggle, .stop, .seek(0)]
        var state = PlaybackStateMachine()
        let old = UUID()
        for command in commands {
            XCTAssertFalse(state.commandContext.accepts(command, generation: old))
        }
        let token = state.begin(resource: try resource(), owner: "one")
        state.ready(generation: token, duration: 120, ranges: [])
        for command in commands {
            XCTAssertTrue(state.commandContext.accepts(command, generation: token))
        }
        state.fail(NativePlaybackError.playbackFailed, generation: token)
        for command in commands {
            XCTAssertFalse(state.commandContext.accepts(command, generation: token))
        }
        state.stop()
        for command in commands {
            XCTAssertFalse(state.commandContext.accepts(command, generation: token))
        }
    }

    func testTransportCommandsCheckGenerationAndRealSeekability() throws {
        var state = PlaybackStateMachine()
        let old = state.begin(resource: try resource(), owner: "one")
        let token = state.begin(resource: try resource(), owner: "one")
        XCTAssertFalse(state.commandContext.accepts(.play, generation: old))
        XCTAssertTrue(state.commandContext.accepts(.play, generation: token))
        XCTAssertFalse(state.commandContext.accepts(.seek(10), generation: token))
        state.ready(generation: token, duration: nil, ranges: [])
        XCTAssertFalse(state.commandContext.accepts(.seek(10), generation: token))
        state.updateTimeline(generation: token, duration: 120, ranges: [])
        XCTAssertTrue(state.commandContext.accepts(.seek(10), generation: token))
        for invalid in [Double.nan, .infinity, -1] {
            XCTAssertFalse(state.commandContext.accepts(.seek(invalid), generation: token))
        }
        state.end(generation: token)
        XCTAssertTrue(state.commandContext.accepts(.play, generation: token), "Ended still permits explicit replay")
    }
}
