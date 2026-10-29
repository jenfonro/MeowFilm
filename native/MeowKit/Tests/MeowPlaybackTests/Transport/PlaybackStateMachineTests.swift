import Foundation
import XCTest
import MeowContracts
@testable import MeowPlayback

/// Deterministic policy tests: no AVURLAsset, file read, media request or account.
final class PlaybackStateMachineTests: XCTestCase {
    private func resource(
        id: String = "track", start: TimeInterval = 0, headers: [String: String] = [:]
    ) throws -> PlaybackResource {
        try PlaybackResource(id: id, url: URL(string: "https://example.invalid/media")!,
                             title: "测试媒体", headers: headers, startPosition: start)
    }

    private func readyState(start: TimeInterval = 0) throws -> (PlaybackStateMachine, UUID) {
        var state = PlaybackStateMachine()
        let generation = state.begin(resource: try resource(start: start), owner: "music")
        state.ready(generation: generation, duration: 120, ranges: [])
        return (state, generation)
    }

    func testInitialStateHasNoOwnerIntentOrSession() {
        let state = PlaybackStateMachine()
        XCTAssertEqual(state.snapshot, PlaybackSnapshot())
        XCTAssertNil(state.generation)
        XCTAssertFalse(state.isPlayable)
        XCTAssertFalse(state.shouldPlay)
        XCTAssertFalse(state.canSeek)
        XCTAssertNil(state.error)
    }

    func testBeginPreparesWithoutClaimingPlayingOrCompletedResume() throws {
        var state = PlaybackStateMachine()
        state.begin(resource: try resource(start: 34), owner: "music")
        XCTAssertEqual(state.snapshot.phase, .preparing)
        XCTAssertEqual(state.snapshot.owner, "music")
        XCTAssertEqual(state.snapshot.resourceID, "track")
        XCTAssertEqual(state.title, "测试媒体")
        XCTAssertEqual(state.snapshot.position, 0)
        XCTAssertEqual(state.pendingSeek?.position, 34)
        XCTAssertFalse(state.shouldPlay)
        XCTAssertNil(state.takeSeekIfPossible())
    }

    func testProgressiveHeadersAreAdmittedWithoutClaimingReadiness() throws {
        var state = PlaybackStateMachine()
        state.begin(resource: try resource(headers: ["X-Test": "synthetic"]), owner: "film")
        XCTAssertNil(state.error)
        XCTAssertEqual(state.snapshot.phase, .preparing)
        XCTAssertFalse(state.shouldPlay)
        XCTAssertTrue(state.isPlayable)
        XCTAssertNil(state.pendingSeek)
    }

    func testEvenEmptyHeaderValuesAndFileHeadersAreNotSilentlyIgnored() throws {
        var state = PlaybackStateMachine()
        let value = try PlaybackResource(id: "file", url: URL(fileURLWithPath: "/not-read/file"),
                                         title: "文件", headers: ["X-Test": ""])
        state.begin(resource: value, owner: "film")
        XCTAssertEqual(state.snapshot.phase, .failed)
        XCTAssertEqual(state.error as? PlaybackNetworkError, .headersOnLocalFile)
    }

    func testBlankOwnerFailsBeforePlayback() throws {
        var state = PlaybackStateMachine()
        state.begin(resource: try resource(), owner: " \n ")
        XCTAssertEqual(state.error as? ContractError, .invalidIdentifier)
        XCTAssertFalse(state.isPlayable)
    }

    func testEveryPlayHasNewGenerationEvenForSameOwnerAndResource() throws {
        var state = PlaybackStateMachine()
        let first = state.begin(resource: try resource(), owner: "music")
        let second = state.begin(resource: try resource(), owner: "music")
        XCTAssertNotEqual(first, second)
        let expected = state.snapshot
        state.ready(generation: first, duration: 200, ranges: [])
        state.progress(generation: first, position: 17)
        state.transport(generation: first, state: .playing)
        state.end(generation: first)
        state.fail(NativePlaybackError.playbackFailed, generation: first)
        XCTAssertEqual(state.snapshot, expected)
        XCTAssertNil(state.error)
    }

    func testSwitchToFilmTransfersOwnershipAndScopedMusicStopDoesNothing() throws {
        var state = PlaybackStateMachine()
        state.begin(resource: try resource(), owner: "music")
        state.begin(resource: try resource(id: "episode"), owner: "film")
        let expected = state.snapshot
        let generation = state.generation
        XCTAssertFalse(state.stop(owner: "music"))
        XCTAssertEqual(state.snapshot, expected)
        XCTAssertEqual(state.generation, generation)
        XCTAssertTrue(state.stop(owner: "film"))
        XCTAssertEqual(state.snapshot.phase, .idle)
    }

    func testUnscopedStopClearsStateAndRejectsOldCallbacks() throws {
        var (state, generation) = try readyState()
        XCTAssertTrue(state.stop())
        state.progress(generation: generation, position: 100)
        state.end(generation: generation)
        state.fail(NativePlaybackError.playbackFailed, generation: generation)
        XCTAssertEqual(state.snapshot, PlaybackSnapshot())
        XCTAssertNil(state.generation)
        XCTAssertEqual(state.title, "")
        XCTAssertNil(state.error)
    }

    func testIdleTransportOperationsAreNoOps() {
        var state = PlaybackStateMachine()
        state.pause()
        XCTAssertFalse(state.resume())
        XCTAssertFalse(state.requestSeek(to: 1))
        XCTAssertTrue(state.stop())
        XCTAssertEqual(state.snapshot, PlaybackSnapshot())
    }

    func testReadyAllowsPlaybackButOnlyTransportConfirmsPlaying() throws {
        var (state, generation) = try readyState()
        XCTAssertTrue(state.shouldPlay)
        XCTAssertEqual(state.snapshot.phase, .buffering)
        state.transport(generation: generation, state: .playing)
        XCTAssertEqual(state.snapshot.phase, .playing)
    }


    func testRepeatedReadinessAndResumeDoNotRevertActualPlaying() throws {
        var (state, generation) = try readyState()
        state.transport(generation: generation, state: .playing)
        state.ready(generation: generation, duration: 120, ranges: [])
        XCTAssertTrue(state.resume())
        XCTAssertEqual(state.snapshot.phase, .playing)
    }

    func testNativeTransportControlsUpdateIntentAndBuffering() throws {
        var (state, generation) = try readyState()
        state.transport(generation: generation, state: .playing)
        state.transport(generation: generation, state: .paused)
        XCTAssertFalse(state.wantsPlayback)
        XCTAssertEqual(state.snapshot.phase, .paused)
        state.transport(generation: generation, state: .waiting)
        XCTAssertTrue(state.wantsPlayback)
        XCTAssertEqual(state.snapshot.phase, .buffering)
        state.transport(generation: generation, state: .playing)
        XCTAssertEqual(state.snapshot.phase, .playing)
    }

    func testResumeWaitsForReadinessThenSuccessfulSeek() throws {
        var state = PlaybackStateMachine()
        let generation = state.begin(resource: try resource(start: 32), owner: "music")
        XCTAssertNil(state.takeSeekIfPossible())
        state.ready(generation: generation, duration: 120, ranges: [])
        XCTAssertFalse(state.shouldPlay)
        let seek = try XCTUnwrap(state.takeSeekIfPossible())
        XCTAssertEqual(seek.position, 32)
        state.progress(generation: generation, position: 0)
        state.transport(generation: generation, state: .paused)
        XCTAssertTrue(state.wantsPlayback)
        XCTAssertFalse(state.shouldPlay)
        state.completeSeek(seek, finished: true, actualPosition: 32.1)
        XCTAssertTrue(state.shouldPlay)
        XCTAssertEqual(state.snapshot.position, 32.1)
        XCTAssertEqual(state.snapshot.phase, .buffering)
    }

    func testPauseWhileLoadingSurvivesReadiness() throws {
        var state = PlaybackStateMachine()
        let generation = state.begin(resource: try resource(), owner: "music")
        state.pause()
        state.ready(generation: generation, duration: 120, ranges: [])
        XCTAssertFalse(state.shouldPlay)
        XCTAssertEqual(state.snapshot.phase, .paused)
    }

    func testPauseDuringResumeSeekDoesNotAutoplayOnCompletion() throws {
        var (state, _) = try readyState(start: 20)
        let seek = try XCTUnwrap(state.takeSeekIfPossible())
        state.pause()
        state.completeSeek(seek, finished: true, actualPosition: 20)
        XCTAssertEqual(state.snapshot.phase, .paused)
        XCTAssertFalse(state.shouldPlay)
        XCTAssertEqual(state.snapshot.position, 20)
    }

    func testManualSeekBeforeReadinessSupersedesResumePosition() throws {
        var state = PlaybackStateMachine()
        let generation = state.begin(resource: try resource(start: 10), owner: "music")
        XCTAssertTrue(state.requestSeek(to: 40))
        state.ready(generation: generation, duration: 120, ranges: [])
        XCTAssertEqual(state.takeSeekIfPossible()?.position, 40)
    }

    func testNewSeekRejectsOldCompletionEvenWithinSameResource() throws {
        var (state, _) = try readyState(start: 20)
        let old = try XCTUnwrap(state.takeSeekIfPossible())
        XCTAssertTrue(state.requestSeek(to: 60))
        let current = try XCTUnwrap(state.takeSeekIfPossible())
        state.completeSeek(old, finished: false, actualPosition: 20)
        XCTAssertNil(state.error)
        XCTAssertEqual(state.activeSeek, current)
        state.completeSeek(current, finished: true, actualPosition: 60)
        XCTAssertEqual(state.snapshot.position, 60)
        XCTAssertTrue(state.shouldPlay)
    }

    func testOldSeekAndTimeoutCannotAffectNewResource() throws {
        var (state, _) = try readyState(start: 20)
        let old = try XCTUnwrap(state.takeSeekIfPossible())
        state.begin(resource: try resource(id: "other"), owner: "film")
        let expected = state.snapshot
        state.completeSeek(old, finished: true, actualPosition: 20)
        state.seekTimedOut(generation: old.generation, id: old.id)
        XCTAssertEqual(state.snapshot, expected)
        XCTAssertNil(state.error)
    }

    func testCancelledSeekFailsInsteadOfStartingFromZero() throws {
        var (state, _) = try readyState(start: 20)
        let seek = try XCTUnwrap(state.takeSeekIfPossible())
        state.completeSeek(seek, finished: false, actualPosition: 0)
        XCTAssertEqual(state.error as? NativePlaybackError, .seekFailed)
        XCTAssertEqual(state.snapshot.phase, .failed)
        XCTAssertFalse(state.shouldPlay)
        XCTAssertFalse(state.resume())
    }

    func testSuccessfulSeekWithInvalidActualPositionStillFails() throws {
        for value in [Double.nan, Double.infinity, -1] {
            var (state, _) = try readyState(start: 20)
            let seek = try XCTUnwrap(state.takeSeekIfPossible())
            state.completeSeek(seek, finished: true, actualPosition: value)
            XCTAssertEqual(state.error as? NativePlaybackError, .seekFailed)
            XCTAssertFalse(state.shouldPlay)
        }
    }

    func testSeekBeyondDurationClampsAndFinishesWithoutAutoplay() throws {
        var (state, _) = try readyState(start: 999)
        let seek = try XCTUnwrap(state.takeSeekIfPossible())
        XCTAssertEqual(seek.position, 120)
        state.completeSeek(seek, finished: true, actualPosition: 120)
        XCTAssertEqual(state.snapshot.phase, .ended)
        XCTAssertEqual(state.snapshot.position, 120)
        XCTAssertFalse(state.shouldPlay)
    }

    func testLiveSeekUsesActualRangesIncludingGaps() throws {
        var state = PlaybackStateMachine()
        let generation = state.begin(resource: try resource(start: 185), owner: "film")
        let early = try XCTUnwrap(PlaybackStateMachine.TimeRange(start: 100, end: 150))
        let late = try XCTUnwrap(PlaybackStateMachine.TimeRange(start: 200, end: 250))
        state.ready(generation: generation, duration: .infinity, ranges: [late, early])
        XCTAssertNil(state.snapshot.duration)
        XCTAssertTrue(state.canSeek)
        XCTAssertEqual(state.takeSeekIfPossible()?.position, 200)
    }

    func testUnseekableReadyItemWaitsAndCanTimeOut() throws {
        var state = PlaybackStateMachine()
        let generation = state.begin(resource: try resource(start: 10), owner: "film")
        state.ready(generation: generation, duration: nil, ranges: [])
        let pending = try XCTUnwrap(state.pendingSeek)
        XCTAssertNil(state.takeSeekIfPossible())
        XCTAssertFalse(state.canSeek)
        XCTAssertFalse(state.shouldPlay)
        state.seekTimedOut(generation: generation, id: pending.id)
        XCTAssertEqual(state.error as? NativePlaybackError, .seekTimedOut)
        XCTAssertFalse(state.resume())
    }

    func testLateSeekabilityUnblocksQueuedLiveSeek() throws {
        var state = PlaybackStateMachine()
        let generation = state.begin(resource: try resource(start: 10), owner: "film")
        state.ready(generation: generation, duration: nil, ranges: [])
        XCTAssertNil(state.takeSeekIfPossible())
        state.updateTimeline(generation: generation, duration: nil,
                             ranges: [try XCTUnwrap(PlaybackStateMachine.TimeRange(start: 100, end: 200))])
        XCTAssertEqual(state.takeSeekIfPossible()?.position, 100)
    }

    func testOldTimeoutDoesNotCancelNewerSeek() throws {
        var (state, generation) = try readyState(start: 20)
        let old = try XCTUnwrap(state.pendingSeek)
        state.requestSeek(to: 40)
        state.seekTimedOut(generation: generation, id: old.id)
        XCTAssertNil(state.error)
        XCTAssertEqual(state.pendingSeek?.position, 40)
    }

    func testInvalidSeeksLeaveExistingOperationUntouched() throws {
        var (state, _) = try readyState(start: 20)
        let pending = state.pendingSeek
        for invalid in [Double.nan, Double.infinity, -1] {
            XCTAssertFalse(state.requestSeek(to: invalid))
            XCTAssertEqual(state.pendingSeek, pending)
        }
    }

    func testEndRetainsBusinessIdentityAndStopsIntent() throws {
        var (state, generation) = try readyState()
        state.end(generation: generation)
        XCTAssertEqual(state.snapshot.phase, .ended)
        XCTAssertEqual(state.snapshot.owner, "music")
        XCTAssertEqual(state.snapshot.resourceID, "track")
        XCTAssertEqual(state.snapshot.position, 120)
        XCTAssertFalse(state.wantsPlayback)
    }

    func testReplaySeeksToStartBeforePlayingAgain() throws {
        var (state, generation) = try readyState()
        state.end(generation: generation)
        state.resume()
        XCTAssertFalse(state.shouldPlay)
        let seek = try XCTUnwrap(state.takeSeekIfPossible())
        XCTAssertEqual(seek.position, 0)
        state.completeSeek(seek, finished: true, actualPosition: 0)
        XCTAssertTrue(state.shouldPlay)
        XCTAssertEqual(state.snapshot.position, 0)
    }

    func testStaleEndCannotTerminateReplaySeek() throws {
        var (state, generation) = try readyState()
        state.end(generation: generation)
        state.resume()
        let seek = try XCTUnwrap(state.takeSeekIfPossible())
        state.end(generation: generation)
        XCTAssertEqual(state.activeSeek, seek)
        XCTAssertNotEqual(state.snapshot.phase, .ended)
    }

    func testInvalidDurationsAndProgressNeverEnterSnapshot() throws {
        var (state, generation) = try readyState()
        for invalid in [Double.nan, Double.infinity, -1, 0] {
            state.updateTimeline(generation: generation, duration: invalid, ranges: [])
            XCTAssertNil(state.snapshot.duration)
        }
        state.updateTimeline(generation: generation, duration: 120, ranges: [])
        state.progress(generation: generation, position: 10)
        for invalid in [Double.nan, Double.infinity, -1] {
            state.progress(generation: generation, position: invalid)
            XCTAssertEqual(state.snapshot.position, 10)
        }
        state.progress(generation: generation, position: 999)
        XCTAssertEqual(state.snapshot.position, 120)
    }

    func testInvalidRangesAreDiscarded() {
        XCTAssertNil(PlaybackStateMachine.TimeRange(start: -.infinity, end: 100))
        XCTAssertNil(PlaybackStateMachine.TimeRange(start: 10, end: .nan))
        XCTAssertNil(PlaybackStateMachine.TimeRange(start: -1, end: 10))
        XCTAssertNil(PlaybackStateMachine.TimeRange(start: 10, end: 10))
        XCTAssertNil(PlaybackStateMachine.TimeRange(start: 20, end: 10))
    }

    func testInterruptionResumesOnlyWithSystemPermissionAndPriorIntent() throws {
        var (state, generation) = try readyState()
        state.transport(generation: generation, state: .playing)
        state.interruptionBegan()
        state.transport(generation: generation, state: .paused)
        XCTAssertTrue(state.wantsPlayback)
        XCTAssertFalse(state.shouldPlay)
        XCTAssertEqual(state.snapshot.phase, .paused)
        state.interruptionEnded(shouldResume: true)
        XCTAssertTrue(state.shouldPlay)
        XCTAssertEqual(state.snapshot.phase, .buffering)
    }

    func testInterruptionWithoutResumePermissionRemainsPaused() throws {
        var (state, _) = try readyState()
        state.interruptionBegan()
        state.interruptionEnded(shouldResume: false)
        XCTAssertFalse(state.shouldPlay)
        XCTAssertFalse(state.wantsPlayback)
        XCTAssertEqual(state.snapshot.phase, .paused)
    }

    func testUserPauseDuringInterruptionPreventsAutomaticResume() throws {
        var (state, _) = try readyState()
        state.interruptionBegan()
        state.pause()
        state.interruptionEnded(shouldResume: true)
        XCTAssertFalse(state.shouldPlay)
        XCTAssertEqual(state.snapshot.phase, .paused)
    }

    func testSwitchDuringInterruptionDoesNotInheritOldResumePermission() throws {
        var (state, _) = try readyState()
        state.interruptionBegan()
        let new = state.begin(resource: try resource(id: "film"), owner: "film")
        state.ready(generation: new, duration: 300, ranges: [])
        XCTAssertTrue(state.isInterrupted)
        state.interruptionEnded(shouldResume: true)
        XCTAssertFalse(state.shouldPlay)
        XCTAssertEqual(state.snapshot.phase, .paused)
    }

    func testExplicitResumeForNewItemDuringInterruptionExpressesNewIntent() throws {
        var (state, _) = try readyState()
        state.interruptionBegan()
        let new = state.begin(resource: try resource(id: "film"), owner: "film")
        state.ready(generation: new, duration: 300, ranges: [])
        state.resume()
        XCTAssertFalse(state.shouldPlay)
        state.interruptionEnded(shouldResume: true)
        XCTAssertTrue(state.shouldPlay)
    }

    func testRepeatedInterruptionAndUnpairedEndAreHarmless() throws {
        var (state, _) = try readyState()
        state.interruptionEnded(shouldResume: false)
        XCTAssertTrue(state.shouldPlay)
        state.interruptionBegan()
        state.interruptionBegan()
        state.interruptionEnded(shouldResume: true)
        XCTAssertTrue(state.shouldPlay)
        state.pause()
        state.interruptionEnded(shouldResume: true)
        XCTAssertFalse(state.shouldPlay)
    }

    func testStopDuringInterruptionCannotResumeDeletedResource() throws {
        var (state, generation) = try readyState()
        state.interruptionBegan()
        state.stop()
        state.interruptionEnded(shouldResume: true)
        state.transport(generation: generation, state: .playing)
        XCTAssertEqual(state.snapshot.phase, .idle)
        XCTAssertFalse(state.shouldPlay)
    }

    func testMediaResetClearsInterruptionButRequiresExplicitNewPlay() throws {
        var (state, _) = try readyState()
        state.interruptionBegan()
        state.mediaServicesReset()
        XCTAssertFalse(state.isInterrupted)
        XCTAssertEqual(state.error as? NativePlaybackError, .mediaServicesReset)
        XCTAssertFalse(state.resume())
        let next = state.begin(resource: try resource(), owner: "music")
        state.ready(generation: next, duration: 120, ranges: [])
        XCTAssertTrue(state.shouldPlay)
    }

    func testFailureCannotBeOverwrittenByLaterSuccessEvents() throws {
        var (state, generation) = try readyState(start: 20)
        let seek = try XCTUnwrap(state.takeSeekIfPossible())
        state.fail(NativePlaybackError.playbackFailed, generation: generation)
        let failed = state.snapshot
        state.completeSeek(seek, finished: true, actualPosition: 20)
        state.ready(generation: generation, duration: 120, ranges: [])
        state.transport(generation: generation, state: .playing)
        state.progress(generation: generation, position: 30)
        state.end(generation: generation)
        XCTAssertEqual(state.snapshot, failed)
        XCTAssertNil(state.outstandingSeek)
    }

    func testTimelineRefreshDoesNotInventTransportChange() throws {
        var (state, generation) = try readyState()
        state.transport(generation: generation, state: .playing)
        state.updateTimeline(generation: generation, duration: 130, ranges: [])
        XCTAssertEqual(state.snapshot.phase, .playing)
        XCTAssertEqual(state.snapshot.duration, 130)
    }

    func testNativeScrubAfterEndLeavesPausedAtActualPosition() throws {
        var (state, generation) = try readyState()
        state.end(generation: generation)
        state.progress(generation: generation, position: 120)
        XCTAssertEqual(state.snapshot.phase, .ended)
        state.progress(generation: generation, position: 30)
        XCTAssertEqual(state.snapshot.phase, .paused)
        XCTAssertEqual(state.snapshot.position, 30)
        XCTAssertFalse(state.shouldPlay)
    }

    func testStallNeverOverridesManualPause() throws {
        var (state, generation) = try readyState()
        state.stalled(generation: generation)
        XCTAssertEqual(state.snapshot.phase, .buffering)
        state.pause()
        state.stalled(generation: generation)
        XCTAssertEqual(state.snapshot.phase, .paused)
    }
}
