#if os(iOS) || os(macOS)
import Foundation
import XCTest
import MeowMusicFeature

final class MusicCandidateViewModelTests: XCTestCase {
    @MainActor private func setup() async -> (MusicCandidateViewModelService, MusicCandidateViewModelPlayer, MusicViewModel) {
        let service = MusicCandidateViewModelService(), player = MusicCandidateViewModelPlayer()
        let model = MusicViewModel(service: service, player: player)
        await model.load()
        return (service, player, model)
    }
    @MainActor func testExplicitPlayDiscoversRefreshesCASAndPlaysResolvedCandidateExactlyOnce() async throws {
        let (service, player, model) = await setup()
        var requests: [MusicPlaybackDestination] = []
        model.onRequestPresentation = { requests.append($0) }
        await service.configure(discovery: true)
        XCTAssertTrue(player.resources.isEmpty)
        await model.play(MusicCandidateViewModelService.track("entry-1"))
        let counts = await service.metrics()
        XCTAssertEqual(counts.begins, 1); XCTAssertEqual(counts.commits, 1)
        XCTAssertEqual(counts.automatic, 1); XCTAssertEqual(counts.manual, 0); XCTAssertEqual(counts.options, 0)
        XCTAssertEqual(model.state.revision, 3); XCTAssertEqual(model.queue[0].track.sources.count, 2)
        XCTAssertEqual(model.position, 23); XCTAssertEqual(player.resources.count, 1)
        XCTAssertEqual(player.resources[0].id, model.activeResourceID)
        XCTAssertEqual(model.activeOccurrenceID, "entry-3"); XCTAssertTrue(requests.isEmpty)
        XCTAssertTrue(counts.facts.isEmpty)
        let resource = try XCTUnwrap(model.activeResourceID)
        model.onPlayback(.init(attemptId: String(resource.dropFirst(6)), occurrenceId: "entry-3",
            resourceId: resource, kind: .playing, position: 23))
        await model.flushPlaybackFacts()
        let recorded = await service.metrics(); XCTAssertEqual(recorded.facts.count, 1)
        await model.close()
    }

    @MainActor func testForcedPickerAndManualFailureNeverStartAutomaticPolicy() async throws {
        let (service, player, model) = await setup()
        var requests: [MusicPlaybackDestination] = []
        model.onRequestPresentation = { requests.append($0) }
        await service.configure(manualFailure: .upstreamFailed)
        await model.prepareCurrent(forceSelection: true)
        XCTAssertEqual(requests, [.sources])
        await model.choose(try XCTUnwrap(model.options.first).selection(quality: "128k"))
        let counts = await service.metrics()
        XCTAssertEqual(counts.options, 1); XCTAssertEqual(counts.manual, 1); XCTAssertEqual(counts.begins, 0)
        XCTAssertTrue(player.resources.isEmpty); XCTAssertNotNil(model.error)
        await model.close()
    }

    @MainActor func testExhaustedDiscoveryRefreshesCommittedRevisionWithoutPlaying() async throws {
        let (service, player, model) = await setup()
        await service.configure(discovery: true, automaticFailure: .upstreamFailed)
        await model.prepareCurrent()
        let counts = await service.metrics()
        XCTAssertEqual(counts.commits, 1); XCTAssertEqual(counts.automatic, 1)
        XCTAssertEqual(model.state.revision, 2); XCTAssertEqual(model.queue[0].track.sources.count, 2)
        XCTAssertTrue(player.resources.isEmpty)
        XCTAssertMusicDiagnosticMessage(model.error, "QQ 音乐 · 播放歌曲：音源不可用")
        await model.close()
    }

    @MainActor func testTerminalPermissionFailureDoesNotTryAnotherCandidate() async {
        let (service, player, model) = await setup()
        await service.configure(automaticFailure: .authRequired)
        await model.togglePlayback()
        let counts = await service.metrics()
        XCTAssertEqual(counts.automatic, 1); XCTAssertEqual(counts.commits, 0)
        XCTAssertMusicDiagnosticMessage(model.error, "QQ 音乐 · 播放歌曲：此操作需要登录对应平台")
        XCTAssertTrue(player.resources.isEmpty)
        await model.close()
    }

    @MainActor func testForcePickerCancelsInFlightAutomaticResolution() async {
        let (service, player, model) = await setup()
        var requests: [MusicPlaybackDestination] = []
        model.onRequestPresentation = { requests.append($0) }
        let began = expectation(description: "automatic entered")
        let gate = MusicCandidateViewModelGate { began.fulfill() }
        await service.configure(resolve: gate)
        let task = Task { await model.prepareCurrent() }
        await fulfillment(of: [began], timeout: 2)
        await model.prepareCurrent(forceSelection: true)
        await gate.release(); await task.value
        XCTAssertEqual(requests, [.sources]); XCTAssertTrue(player.resources.isEmpty)
        let counts = await service.metrics(); XCTAssertFalse(counts.cancelled.isEmpty)
        XCTAssertEqual(counts.manual, 0)
        await model.close()
    }

    @MainActor func testReturnedCandidateIsReleasedAfterCloseLockSettingsOrCallerCancellationDuringRefresh() async {
        for action in ["close", "lock", "settings", "cancel"] {
            let (service, player, model) = await setup()
            let began = expectation(description: "returned candidate refresh \(action)")
            let gate = MusicCandidateViewModelGate { began.fulfill() }
            await service.configure(refresh: gate)
            let task = Task { await model.prepareCurrent() }
            await fulfillment(of: [began], timeout: 2)
            switch action {
            case "close": await model.close()
            case "lock": model.setAvailable(false)
            case "settings": await model.updateSettings(.init(ifRevision: 0, qualityEnabled: ["standard"]))
            default: task.cancel()
            }
            await gate.release(); await task.value
            let counts = await service.metrics(), ready = await service.activeAttempt()
            XCTAssertEqual(counts.retained.count, 1)
            XCTAssertTrue(counts.retained.allSatisfy { counts.cancelled.contains($0) }, action)
            XCTAssertNil(ready); XCTAssertNil(model.activeResourceID); XCTAssertTrue(player.resources.isEmpty)
            XCTAssertFalse(model.isResolving)
            await model.close()
        }
    }

    @MainActor func testLateOldRefreshCannotCancelOrReplaceNewOccurrence() async {
        let (service, player, model) = await setup()
        let began = expectation(description: "old refresh")
        let gate = MusicCandidateViewModelGate { began.fulfill() }
        await service.configure(refresh: gate)
        let old = Task { await model.prepareCurrent() }
        await fulfillment(of: [began], timeout: 2)
        await model.selectOccurrence("entry-2")
        let resource = model.activeResourceID
        await gate.release(); await old.value
        XCTAssertEqual(model.activeResourceID, resource); XCTAssertEqual(model.activeOccurrenceID, "entry-2")
        XCTAssertEqual(player.resources.count, 1)
        let ready = await service.activeAttempt()
        XCTAssertEqual(ready.map { "music:" + $0 }, resource)
        await model.close()
    }

    @MainActor func testMismatchedStateAndQueueRefreshNeverPublishesPartialSnapshotOrPlays() async {
        let (service, player, model) = await setup()
        let began = expectation(description: "state captured")
        let gate = MusicCandidateViewModelGate { began.fulfill() }
        await service.configure(refresh: gate)
        let oldQueue = model.queue
        let task = Task { await model.prepareCurrent() }
        await fulfillment(of: [began], timeout: 2)
        await service.mutateQueueDuringRefresh()
        await gate.release(); await task.value
        XCTAssertEqual(model.queue, oldQueue); XCTAssertEqual(model.state.revision, 1)
        XCTAssertTrue(player.resources.isEmpty); XCTAssertNotNil(model.error)
        let ready = await service.activeAttempt(); XCTAssertNil(ready)
        await model.close()
    }
}
#endif
