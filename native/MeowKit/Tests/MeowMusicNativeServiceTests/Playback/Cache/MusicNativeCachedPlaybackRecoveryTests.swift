import Foundation
import XCTest
import MeowMusicFeature
@testable import MeowMusicNativeService

final class MusicNativeCachedPlaybackRecoveryTests: XCTestCase {
    func testBadFilePreservesAcceptedStartedFactAndPositionWithoutWritingSecondHistory() async throws {
        let f = try await MusicRecoveryTestFixture.make(cached: true)
        _ = try await f.service.record(f.fact(f.original, kind: .playing, position: 24))
        let history = try f.historyDocument(), bytes = f.store.snapshot().0
        f.clock.advance(30)
        let result = try await f.service.recoverCachedPlayback(previousAttemptID: f.original.attemptId,
            selection: f.selection, position: 29)
        XCTAssertFalse(result.candidate.url.isFileURL)
        XCTAssertEqual(result.candidate.occurrenceId, f.original.occurrenceId)
        XCTAssertEqual(result.candidate.startPosition, 29)
        XCTAssertEqual(result.selection, f.selection)
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
