import Foundation
import XCTest
import MeowMusicFeature
@testable import MeowMusicNativeService

final class MusicNativeCachedPlaybackRecoveryTests: XCTestCase {
    func testFinalScopeHandoffUsesCurrentMetadataWithoutRepeatingResolveOrHistory() async throws {
        for metadata in [101, 130] {
            let f = try await MusicRecoveryTestFixture.make(cached: true, duration: 100)
            _ = try await f.service.record(.init(attemptId: f.original.attemptId, occurrenceId: f.original.occurrenceId,
                resourceId: f.original.resourceId, kind: .playing, position: 24, duration: 100))
            let history = try f.historyDocument(), before = try await f.service.read()
            let gate = MusicNativeCandidateDiscoveryBarrier()
            await f.accounts.setDuration(100)
            await f.accounts.holdScopeCheckAfterResolve(gate)
            let task = Task { try await f.service.recoverCachedPlayback(previousAttemptID: f.original.attemptId,
                selection: f.selection, position: 29) }
            await gate.started(1)
            let corrected = try MusicModel.normalize(MusicNativeCandidateDiscoveryFixture.track(
                "tx", id: "101", title: "Final handoff title", duration: metadata))
            _ = try await f.service.adoptSongs([corrected], authoritative: true)
            await gate.release()
            var expectedPlayback = before.playback
            if metadata == 101 {
                let result = try await task.value
                XCTAssertEqual(result.candidate.title, "Final handoff title")
                XCTAssertEqual(result.candidate.startPosition, 29)
                XCTAssertEqual(result.selection, f.selection)
                let prepared = try await f.service.read()
                XCTAssertEqual(prepared.queue, before.queue)
                XCTAssertEqual(prepared.playback, before.playback)
                XCTAssertEqual(try f.historyDocument(), history)
                _ = try await f.service.record(.init(attemptId: result.candidate.attemptId,
                    occurrenceId: result.candidate.occurrenceId, resourceId: result.candidate.resourceId,
                    kind: .playing, position: 29, duration: 100))
                expectedPlayback["position"]["seconds"] = .number(29)
            } else {
                do { _ = try await task.value; XCTFail("Final authorization cannot admit outdated media") }
                catch { XCTAssertEqual(MusicServiceError.sanitized(error), .invalidMediaCandidate) }
            }
            let after = try await f.service.read(), counts = await f.accounts.counts()
            XCTAssertEqual(after.queue, before.queue)
            XCTAssertEqual(after.playback, expectedPlayback)
            XCTAssertEqual(try f.historyDocument(), history)
            XCTAssertEqual(counts.resolves, 1)
            XCTAssertEqual(counts.refreshes, 0)
            await f.service.close()
        }
    }

    func testNormalDurationCorrectionKeepsCompleteFileRecoveryAndAcceptedStartedFact() async throws {
        for manual in [false, true] {
            let f = try await MusicRecoveryTestFixture.make(cached: true, duration: 100)
            _ = try await f.service.record(.init(attemptId: f.original.attemptId, occurrenceId: f.original.occurrenceId,
                resourceId: f.original.resourceId, kind: .playing, position: 24, duration: 100))
            let history = try f.historyDocument()
            let corrected = try MusicModel.normalize(MusicNativeCandidateDiscoveryFixture.track(
                "tx", id: "101", title: "Current complete-file title", duration: 101))
            _ = try await f.service.adoptSongs([corrected], authoritative: true)
            await f.accounts.setDuration(101)
            let before = try await f.service.read()
            let result = try await f.service.recoverCachedPlayback(previousAttemptID: f.original.attemptId,
                selection: manual ? f.selection : nil, position: 29)
            XCTAssertFalse(result.candidate.isCachedAudio)
            XCTAssertEqual(result.candidate.title, "Current complete-file title")
            XCTAssertEqual(result.candidate.occurrenceId, f.original.occurrenceId)
            XCTAssertEqual(result.candidate.startPosition, 29)
            XCTAssertEqual(result.selection, f.selection)
            let after = try await f.service.read(), counts = await f.accounts.counts()
            XCTAssertEqual(after.queue, before.queue)
            XCTAssertEqual(after.playback, before.playback)
            XCTAssertEqual(try f.historyDocument(), history)
            XCTAssertEqual(counts.resolves, 1)
            XCTAssertEqual(counts.refreshes, 0)
            _ = try await f.service.record(.init(attemptId: result.candidate.attemptId, occurrenceId: result.candidate.occurrenceId,
                resourceId: result.candidate.resourceId, kind: .playing, position: 29, duration: 101))
            XCTAssertEqual(try f.historyDocument(), history, "The accepted playing fact does not create a second history item")
            await f.service.close()
        }
    }

    func testUnpinnedCompleteCacheKeepsItsExistingCachedSelectionWithoutAnAccountResolve() async throws {
        let f = try await MusicRecoveryTestFixture.make(cached: true)
        let hit = try await f.service.cachedPlayback(attemptID: UUID().uuidString.lowercased(),
            occurrenceID: f.original.occurrenceId, selection: nil)
        let value = try XCTUnwrap(hit)
        let expected = f.selection
        XCTAssertEqual(value.selection, expected)
        XCTAssertEqual(value.candidate.resolvedSelection, expected)
        let attempt = await f.service.attempt
        XCTAssertEqual(attempt?.selection, expected)
        XCTAssertTrue(value.candidate.isCachedAudio); XCTAssertTrue(value.candidate.url.isFileURL)
        _ = try value.candidate.resource()
        let counts = await f.accounts.counts()
        XCTAssertEqual(counts.resolves, 0); XCTAssertEqual(counts.refreshes, 0)
        let history = try await f.service.playedSongs(); XCTAssertTrue(history.isEmpty)
        await f.service.close()
    }

    func testBadFilePreservesAcceptedStartedFactAndPositionWithoutWritingSecondHistory() async throws {
        let f = try await MusicRecoveryTestFixture.make(cached: true)
        XCTAssertEqual(f.original.resolvedSelection, f.selection)
        _ = try await f.service.record(f.fact(f.original, kind: .playing, position: 24))
        let history = try f.historyDocument(), bytes = f.store.snapshot().0
        f.clock.advance(30)
        let result = try await f.service.recoverCachedPlayback(previousAttemptID: f.original.attemptId,
            selection: f.selection, position: 29)
        XCTAssertFalse(result.candidate.url.isFileURL)
        XCTAssertEqual(result.candidate.occurrenceId, f.original.occurrenceId)
        XCTAssertEqual(result.candidate.startPosition, 29)
        XCTAssertEqual(result.selection, f.selection)
        XCTAssertEqual(result.candidate.resolvedSelection, f.selection)
        let actual = await f.service.attempt
        XCTAssertEqual(actual?.selection, result.candidate.resolvedSelection)
        XCTAssertEqual(f.store.snapshot().0, bytes, "Recovery does not synthesize pause/playing/progress")
        _ = try await f.service.record(f.fact(result.candidate, kind: .playing, position: 29))
        XCTAssertEqual(try f.historyDocument(), history)
        await f.service.close()
    }

    func testBadFileBeforeFirstActualPlayingDoesNotManufactureStarted() async throws {
        let f = try await MusicRecoveryTestFixture.make(cached: true)
        let result = try await f.service.recoverCachedPlayback(previousAttemptID: f.original.attemptId,
            selection: f.selection, position: 0)
        let before = try await f.service.playedSongs(); XCTAssertTrue(before.isEmpty)
        _ = try await f.service.record(f.fact(result.candidate, kind: .playing, position: 1))
        let after = try await f.service.playedSongs(); XCTAssertEqual(after.count, 1)
        await f.service.close()
    }

    func testAccountScopeChangeWhileResolvingCannotPublishCachedContinuation() async throws {
        let f = try await MusicRecoveryTestFixture.make(cached: true), gate = NativeMusicGate()
        await f.accounts.holdResolve(gate)
        let work = Task {
            try await f.service.recoverCachedPlayback(previousAttemptID: f.original.attemptId,
                selection: f.selection, position: 31)
        }
        await gate.started()
        await f.accounts.rotate()
        await gate.release()
        do { _ = try await work.value; XCTFail("Revoked cache scope published") }
        catch { XCTAssertEqual(error as? MusicServiceError, .staleAccess) }
        let history = try await f.service.playedSongs(); XCTAssertTrue(history.isEmpty)
        await f.service.close()
    }

    func testCancellationByOriginalAttemptCannotCancelANewerUserAttempt() async throws {
        let f = try await MusicRecoveryTestFixture.make(cached: true), gate = NativeMusicGate()
        await f.accounts.holdResolve(gate)
        let work = Task {
            try await f.service.recoverCachedPlayback(previousAttemptID: f.original.attemptId,
                selection: f.selection, position: 31)
        }
        await gate.started()
        try await f.service.cancel(attemptId: f.original.attemptId)
        let next = try await f.service.resolve(attemptId: UUID().uuidString.lowercased(),
            occurrenceId: f.original.occurrenceId, selection: f.selection)
        await gate.release()
        do { _ = try await work.value; XCTFail("Cancelled recovery published") } catch {}
        let accepted = try await f.service.record(f.fact(next, kind: .playing))
        XCTAssertTrue(accepted)
        await f.service.close()
    }

    func testRevokedRemoteAuthorizationCannotCommitHistoryOrProgress() async throws {
        let f = try await MusicRecoveryTestFixture.make(authorized: true)
        let before = f.store.snapshot().0
        f.original.playbackAuthorization?.revoke()
        do { _ = try await f.service.record(f.fact(f.original, kind: .playing)); XCTFail("Revoked fact committed") }
        catch { XCTAssertEqual(error as? MusicServiceError, .staleAccess) }
        XCTAssertEqual(f.store.snapshot().0, before)
        await f.service.close()
    }
}
