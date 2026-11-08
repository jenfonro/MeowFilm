import Foundation
import XCTest
import MeowMusicFeature
@testable import MeowMusicNativeService

final class MusicNativePlaybackRecoveryTests: XCTestCase {
    private func fails<T>(_ expected: MusicServiceError, file: StaticString = #filePath, line: UInt = #line,
                          _ body: () async throws -> T) async {
        do { _ = try await body(); XCTFail("expected rejection", file: file, line: line) }
        catch { XCTAssertEqual(error as? MusicServiceError, expected, file: file, line: line) }
    }

    func testExactThreePlatformRefreshPreservesOccurrencePositionStartedAndExistingFactWriter() async throws {
        for platform in MusicAccountPlatform.allCases {
            let f = try await MusicRecoveryTestFixture.make(platform: platform)
            XCTAssertEqual(f.original.resolvedSelection, f.selection)
            _ = try await f.service.record(f.fact(f.original, kind: .playing, position: 23))
            let history = try f.historyDocument(), before = f.store.snapshot().0
            f.clock.advance(20)
            await f.accounts.expireGrant()
            let value = try await f.recover(position: 37.25)
            XCTAssertNotEqual(value.candidate.attemptId, f.original.attemptId)
            XCTAssertEqual(value.candidate.occurrenceId, f.original.occurrenceId)
            XCTAssertEqual(value.candidate.resourceId, "music:" + value.candidate.attemptId)
            XCTAssertEqual(value.candidate.url, MusicRecoveryTestAccounts.renewedURL)
            XCTAssertEqual(value.candidate.startPosition, 37.25)
            XCTAssertEqual(value.candidate.requestedQuality, "flac")
            XCTAssertEqual(value.selection, f.selection)
            XCTAssertEqual(value.candidate.resolvedSelection, f.selection)
            let actual = await f.service.attempt
            XCTAssertEqual(actual?.selection, value.candidate.resolvedSelection)
            XCTAssertFalse(value.candidate.isCachedAudio)
            XCTAssertEqual(f.store.snapshot().0, before, "refresh does not manufacture a fact or write progress")
            let calls = await f.accounts.requests(), counts = await f.accounts.counts()
            XCTAssertEqual(calls.count, 1)
            XCTAssertEqual(calls[0].platform, platform)
            XCTAssertEqual(calls[0].selection, f.selection)
            XCTAssertEqual(calls[0].rejectedURL, f.original.url)
            XCTAssertEqual(counts.resolves, 1); XCTAssertEqual(counts.refreshes, 1)
            XCTAssertEqual(counts.options, 3, "exact grant is renewed and checked again, no discovery")

            _ = try await f.service.record(f.fact(value.candidate, kind: .playing))
            XCTAssertEqual(try f.historyDocument(), history, "same occurrence must not rewrite its played timestamp")
            await fails(.staleResult) { try await f.service.record(f.fact(f.original, kind: .progress)) }
            let stored = String(decoding: try XCTUnwrap(f.store.snapshot().0), as: UTF8.self)
            XCTAssertFalse(stored.contains("media.invalid")); XCTAssertFalse(stored.contains("revision=renewed"))
            await f.service.close()
        }
    }

    func testRecoveryBeforeFirstPlayingCreatesNoHistoryUntilRealReplacementFact() async throws {
        let f = try await MusicRecoveryTestFixture.make()
        let value = try await f.recover()
        let before = try await f.service.playedSongs()
        XCTAssertTrue(before.isEmpty)
        _ = try await f.service.record(f.fact(value.candidate, kind: .playing))
        let after = try await f.service.playedSongs()
        XCTAssertEqual(after.count, 1)
        await f.service.close()
    }

    func testCandidateDiscoveryPublishesTheSameRecoveryPinAndOwner() async throws {
        let f = try await MusicRecoveryTestFixture.make(discovery: true)
        XCTAssertEqual(f.original.resolvedSelection, f.selection)
        let before = f.store.snapshot().0
        let value = try await f.recover()
        XCTAssertEqual(value.selection, f.selection)
        XCTAssertEqual(value.candidate.resolvedSelection, f.selection)
        XCTAssertEqual(value.candidate.occurrenceId, "entry-1")
        XCTAssertEqual(f.store.snapshot().0, before)
        _ = try await f.service.record(f.fact(value.candidate, kind: .playing))
        let history = try await f.service.playedSongs()
        XCTAssertEqual(history.count, 1)
        await f.service.close()
    }

    func testForeignOrFileURLCannotEvictThePinnedRemoteURL() async throws {
        let f = try await MusicRecoveryTestFixture.make()
        let before = await f.accounts.counts()
        for url in [URL(string: "https://media.invalid/some-other-song.mp3")!,
                    URL(fileURLWithPath: "/synthetic/private/cache.flac")] {
            await fails(.invalidMediaCandidate) {
                try await f.service.recoverPlayback(previousAttemptID: f.original.attemptId,
                    attemptID: UUID().uuidString.lowercased(), rejectedURL: url, position: 20)
            }
        }
        let unchanged = await f.accounts.counts()
        XCTAssertEqual(unchanged.options, before.options); XCTAssertEqual(unchanged.refreshes, 0)
        _ = try await f.recover()
        await f.service.close()
    }

    func testBadPositionAttemptIDsAndSpentIDsAreRejectedBeforeAnyAccountWork() async throws {
        let f = try await MusicRecoveryTestFixture.make()
        let baseline = await f.accounts.counts()
        for position in [-1, Double.nan, Double.infinity, 604_801] {
            await fails(.invalidRequest) { try await f.recover(position: position) }
        }
        for id in [f.original.attemptId, "not-an-attempt-id"] {
            await fails(.staleResult) { try await f.recover(attemptID: id) }
        }
        await fails(.staleResult) {
            try await f.service.recoverPlayback(previousAttemptID: UUID().uuidString.lowercased(),
                attemptID: UUID().uuidString.lowercased(), rejectedURL: f.original.url, position: 20)
        }
        let counts = await f.accounts.counts()
        XCTAssertEqual(counts.options, baseline.options); XCTAssertEqual(counts.refreshes, 0)
        await f.service.close()
    }

    func testTerminalFactCannotBeResurrectedAsAMediaRetry() async throws {
        for kind in [MusicPlaybackFact.Kind.failed, .ended, .stopped] {
            let f = try await MusicRecoveryTestFixture.make()
            _ = try await f.service.record(f.fact(f.original, kind: kind))
            let before = await f.accounts.counts()
            await fails(.staleResult) { try await f.recover() }
            let after = await f.accounts.counts()
            XCTAssertEqual(before.options, after.options); XCTAssertEqual(after.refreshes, 0)
            await f.service.close()
        }
    }

    func testQueueSettingsAndRecordingChangesInvalidatePublishedPinBeforeNetworking() async throws {
        for mutation in ["queue", "settings", "recording"] {
            let f = try await MusicRecoveryTestFixture.make()
            if mutation == "queue" {
                _ = try await f.service.changeQueue(.reorder(["entry-1"]), expectedRevision: 1)
            } else if mutation == "settings" {
                let state = try await f.service.state()
                _ = try await f.service.updateSettings(.init(ifRevision: state.settingsRevision, showTranslation: false))
            } else {
                let old = try f.store.read()
                var d = try MusicDocument(old, runID: "fixture")
                var entries = d.queue["entries"].array
                // A reused stable locator with changed recording data, even
                // without a bumped revision, must not borrow the original pin.
                entries[0]["song"] = try MusicModel.normalize(MusicNativeCandidateDiscoveryFixture.track(
                    "tx", id: "101", title: "Different synthetic recording"))
                d.queue["entries"] = .array(entries)
                try f.store.compareAndSwap(expected: old, replacement: d.encode(changed: ["queue"]))
            }
            let before = await f.accounts.counts()
            await fails(mutation == "recording" ? .targetChanged : .staleRevision) { try await f.recover() }
            let after = await f.accounts.counts()
            XCTAssertEqual(after.options, before.options); XCTAssertEqual(after.refreshes, 0)
            await f.service.close()
        }
    }

    func testChangedAccountInstallationOrQualityGrantNeverRefreshesOrSwitchesSource() async throws {
        for mutation in ["generation", "installation", "quality", "signed-out"] {
            let f = try await MusicRecoveryTestFixture.make()
            switch mutation {
            case "generation": await f.accounts.rotate()
            case "installation": await f.accounts.replaceInstallation()
            case "quality": await f.accounts.allow(["128k"])
            default: await f.accounts.failOptions(.authRequired)
            }
            let expected: MusicServiceError = mutation == "signed-out" ? .authRequired :
                mutation == "quality" ? .sourceUnavailable : .targetChanged
            await fails(expected) { try await f.recover() }
            let calls = await f.accounts.counts()
            XCTAssertEqual(calls.refreshes, 0); XCTAssertEqual(calls.resolves, 1)
            if mutation == "generation" || mutation == "installation" {
                XCTAssertEqual(calls.options, 1, "replacement account must not receive even a capability request")
            }
            await f.service.close()
        }
    }

    func testAccountOrQualityChangeAfterRefreshCannotPublishAndOldOwnerCanRecordFinalFailure() async throws {
        for account in [true, false] {
            let f = try await MusicRecoveryTestFixture.make()
            await f.accounts.changeAfterRefresh(account: account)
            await fails(account ? .targetChanged : .sourceUnavailable) { try await f.recover() }
            let history = try await f.service.playedSongs()
            XCTAssertTrue(history.isEmpty)
            let counts = await f.accounts.counts()
            XCTAssertEqual(counts.refreshes, 1)
            _ = try await f.service.record(f.fact(f.original, kind: .failed))
            let queue = try await f.service.queue()
            XCTAssertEqual(queue.currentId, "entry-1")
            await f.service.close()
        }
    }

    func testDuplicateConcurrentAndSuccessiveRefreshesShareOneBudget() async throws {
        let f = try await MusicRecoveryTestFixture.make(), gate = NativeMusicGate()
        await f.accounts.holdRefresh(gate)
        let first = Task { try await f.recover() }
        await gate.started()
        await fails(.staleResult) { try await f.recover() }
        await gate.release()
        let value = try await first.value
        await fails(.staleResult) {
            try await f.service.recoverPlayback(previousAttemptID: value.candidate.attemptId,
                attemptID: UUID().uuidString.lowercased(), rejectedURL: value.candidate.url, position: 40)
        }
        let counts = await f.accounts.counts()
        XCTAssertEqual(counts.refreshes, 1); XCTAssertEqual(counts.resolves, 1)
        await f.service.close()
    }

    func testFailureConsumesRefreshBudgetButLeavesOldOwnerForTerminalFact() async throws {
        let f = try await MusicRecoveryTestFixture.make()
        await f.accounts.failRefresh(.upstreamFailed)
        await fails(.upstreamFailed) { try await f.recover() }
        await fails(.staleResult) { try await f.recover() }
        let counts = await f.accounts.counts()
        XCTAssertEqual(counts.refreshes, 1)
        _ = try await f.service.record(f.fact(f.original, kind: .failed))
        await f.service.close()
    }

    func testUnchangedInvalidOrAlreadyExpiredReplacementIsNeverPublishedOrRetried() async throws {
        let cases: [(URL, Date?)] = [
            (MusicRecoveryTestAccounts.originalURL, nil),
            (URL(fileURLWithPath: "/synthetic/not-a-remote-candidate.flac"), nil),
            (MusicRecoveryTestAccounts.renewedURL, NativeMusicFixture.date.addingTimeInterval(-1))
        ]
        for (url, expiration) in cases {
            let f = try await MusicRecoveryTestFixture.make()
            await f.accounts.setResult(url, expiresAt: expiration)
            await fails(.invalidMediaCandidate) { try await f.recover() }
            await fails(.staleResult) { try await f.recover() }
            let counts = await f.accounts.counts()
            XCTAssertEqual(counts.refreshes, 1)
            _ = try await f.service.record(f.fact(f.original, kind: .failed))
            await f.service.close()
        }
    }

    func testProgressDuringRefreshPreservesNewlyObservedStartedFlagWithoutDoubleHistory() async throws {
        let f = try await MusicRecoveryTestFixture.make(), gate = NativeMusicGate()
        await f.accounts.holdRefresh(gate)
        let task = Task { try await f.recover() }
        await gate.started()
        _ = try await f.service.record(f.fact(f.original, kind: .playing, position: 25))
        let history = try f.historyDocument()
        f.clock.advance(5)
        await gate.release()
        let value = try await task.value
        _ = try await f.service.record(f.fact(value.candidate, kind: .playing))
        XCTAssertEqual(try f.historyDocument(), history)
        await f.service.close()
    }

    func testSettingsChangeDuringRefreshDiscardsLateURLWithoutFactOrFallback() async throws {
        let f = try await MusicRecoveryTestFixture.make(), gate = NativeMusicGate()
        await f.accounts.holdRefresh(gate)
        let task = Task { try await f.recover() }
        await gate.started()
        let state = try await f.service.state()
        _ = try await f.service.updateSettings(.init(ifRevision: state.settingsRevision, qualityEnabled: ["standard"]))
        await gate.release()
        await fails(.staleRevision) { try await task.value }
        let history = try await f.service.playedSongs(), counts = await f.accounts.counts()
        XCTAssertTrue(history.isEmpty); XCTAssertEqual(counts.refreshes, 1); XCTAssertEqual(counts.resolves, 1)
        await f.service.close()
    }

    func testSupersedingResolutionSurvivesUncooperativeLateRecoveryCallback() async throws {
        let f = try await MusicRecoveryTestFixture.make(), gate = NativeMusicGate()
        await f.accounts.holdRefresh(gate)
        let task = Task { try await f.recover() }
        await gate.started()
        let newer = try await f.service.resolve(attemptId: UUID().uuidString.lowercased(),
            occurrenceId: "entry-1", selection: f.selection)
        await gate.release()
        do { _ = try await task.value; XCTFail("late recovery must not replace newer intent") }
        catch { XCTAssertTrue([MusicServiceError.cancelled, .staleResult].contains(MusicServiceError.sanitized(error))) }
        _ = try await f.service.record(f.fact(newer, kind: .playing))
        let history = try await f.service.playedSongs()
        XCTAssertEqual(history.count, 1)
        await f.service.close()
    }

    func testCancellationDuringFinalReauthorizationDoesNotRollBackToOldAttempt() async throws {
        let f = try await MusicRecoveryTestFixture.make(), gate = NativeMusicGate()
        let counts = await f.accounts.counts(), nextID = UUID().uuidString.lowercased()
        await f.accounts.holdOptions(call: counts.options + 2, gate: gate)
        let task = Task { try await f.recover(attemptID: nextID) }
        await gate.started()
        try await f.service.cancel(attemptId: nextID)
        await gate.release()
        do { _ = try await task.value; XCTFail("cancelled provisional attempt") } catch {}
        await fails(.staleResult) { try await f.service.record(f.fact(f.original, kind: .playing)) }
        let current = await f.service.attempt
        XCTAssertNil(current)
        await f.service.close()
    }

    func testProtectionAndAccountRevocationSuppressSuspendedRefresh() async throws {
        for protection in [true, false] {
            let f = try await MusicRecoveryTestFixture.make(), gate = NativeMusicGate()
            await f.accounts.holdRefresh(gate)
            let task = Task { try await f.recover() }
            await gate.started()
            if protection {
                f.service.revokeAccess(generation: 1)
                try await f.service.setAccess(available: false, generation: 2)
            } else { await f.accounts.rotate() }
            await gate.release()
            do { _ = try await task.value; XCTFail("revoked recovery") } catch {}
            let counts = await f.accounts.counts()
            XCTAssertEqual(counts.refreshes, 1); XCTAssertEqual(counts.resolves, 1)
            await f.service.close()
        }
    }

    func testScriptsAndOwnersWithoutOptionalRefreshPortDoNotUseRecoveryAsFallback() async throws {
        let scripts = NativeMusicScriptFixture(), store = try MusicNativeCandidateDiscoveryFixture.store()
        let scriptService = try await NativeMusicFixture.service(store, scripts: scripts)
        let options = (try await scriptService.sourceAvailability(for: .init(kind: .queue, id: "entry-1"))).options
        let script = try XCTUnwrap(options.first)
        let candidate = try await scriptService.resolve(attemptId: UUID().uuidString.lowercased(),
            occurrenceId: "entry-1", selection: script.selection(quality: "flac"))
        await fails(.staleResult) {
            try await scriptService.recoverPlayback(previousAttemptID: candidate.attemptId,
                attemptID: UUID().uuidString.lowercased(), rejectedURL: candidate.url, position: 0)
        }
        let calls = await scripts.calls()
        XCTAssertEqual(calls.count, 1)
        await scriptService.close()

        let peer = NativeMusicAccountFixture()
        let service = try await NativeMusicFixture.service(try MusicNativeCandidateDiscoveryFixture.store(), accounts: peer)
        let official = (try await service.sourceAvailability(for: .init(kind: .queue, id: "entry-1"))).options
        let selected = try XCTUnwrap(official.first).selection(quality: "128k")
        let original = try await service.resolve(attemptId: UUID().uuidString.lowercased(),
            occurrenceId: "entry-1", selection: selected)
        await fails(.unsupported) {
            try await service.recoverPlayback(previousAttemptID: original.attemptId,
                attemptID: UUID().uuidString.lowercased(), rejectedURL: original.url, position: 0)
        }
        let events = await peer.events()
        XCTAssertEqual(events, ["options", "resolve"])
        await service.close()
    }
}
