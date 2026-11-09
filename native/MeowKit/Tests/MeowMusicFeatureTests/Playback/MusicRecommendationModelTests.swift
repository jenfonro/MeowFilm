#if os(iOS) || os(macOS)
import Foundation
import XCTest
import Combine
import MeowContracts
import MeowPlayback
@testable import MeowMusicFeature

@MainActor
final class MusicRecommendationModelTests: XCTestCase {
    func testInitialSnapshotAndPreviewKeepOrdinaryQueueSourceAndContinuationOff() async throws {
        let service = RecommendationModelService(), player = RecommendationModelPlayer()
        let model = MusicViewModel(service: service, player: player)
        await model.load()
        let before = model.queue, state = model.state
        XCTAssertNotNil(model.queueRecommendations, "Initial reads cannot depend on a change stream's first event")
        XCTAssertFalse(try XCTUnwrap(model.queueRecommendations).continueEnabled)
        await model.previewQueueRecommendations()
        XCTAssertEqual(model.queue.map(\.id), before.map(\.id))
        XCTAssertEqual(model.state.currentId, state.currentId)
        XCTAssertEqual(model.state.sourceCollectionID, state.sourceCollectionID)
        XCTAssertEqual(model.queueRecommendations?.candidates.map(\.id), ["candidate-a", "candidate-b"])
        XCTAssertNil(model.similarMode)
        let calls = await service.calls()
        XCTAssertEqual(calls.previews, 1); XCTAssertEqual(calls.queueWrites, 0)
        XCTAssertTrue(calls.starts.isEmpty); XCTAssertEqual(calls.enters, 0)
        XCTAssertTrue(player.resources.isEmpty)
        await model.close()
    }

    func testHomeDisplayedSearchBatchAndDiscoveryQueueExposeIndependentRecommendations() async throws {
        let service = RecommendationModelService(), player = RecommendationModelPlayer()
        let model = MusicViewModel(service: service, player: player)
        await model.load()
        let tracks = [RecommendationModelService.track("41"), RecommendationModelService.track("42")]
        await model.playDisplayed(tracks, index: 1, origin: .section(title: "首页歌曲"))
        await model.loadSimilarMode()
        XCTAssertNotNil(model.queueRecommendations); XCTAssertFalse(try XCTUnwrap(model.queueRecommendations).continueEnabled)
        XCTAssertNil(model.similarMode)
        let search = MusicSearchBatchRequest(query: "合成查询", targets: tracks.map {
            .init(platform: .netease, page: 1, track: $0)
        }, startIndex: 0)
        await model.playSearchBatch(search)
        await model.loadSimilarMode()
        XCTAssertNotNil(model.queueRecommendations); XCTAssertFalse(try XCTUnwrap(model.queueRecommendations).continueEnabled)
        await model.play(tracks, index: 0)
        await model.loadSimilarMode()
        XCTAssertNotNil(model.queueRecommendations); XCTAssertNil(model.similarMode)
        let calls = await service.calls()
        XCTAssertEqual(calls.displayed, 1); XCTAssertEqual(calls.searches, 1)
        XCTAssertTrue(calls.starts.isEmpty); XCTAssertEqual(calls.enters, 0)
        await model.close()
    }

    func testBackgroundRefreshCannotSupersedeExplicitCandidatePreview() async throws {
        let service = RecommendationModelService(), player = RecommendationModelPlayer()
        let model = MusicViewModel(service: service, player: player)
        await model.load()
        let entered = expectation(description: "explicit preview reading its seed")
        let gate = MusicCollectionGate { entered.fulfill() }
        await service.blockModeRead(gate)
        let preview = Task { await model.previewQueueRecommendations() }
        await fulfillment(of: [entered], timeout: 2)
        await model.loadSimilarMode()
        await gate.release(); await preview.value
        XCTAssertEqual(model.queueRecommendations?.candidates.map(\.id), ["candidate-a", "candidate-b"])
        let calls = await service.calls()
        XCTAssertEqual(calls.previews, 1)
        XCTAssertEqual(calls.queueWrites, 0)
        XCTAssertTrue(player.resources.isEmpty)
        await model.close()
    }

    func testQueuedRefreshCannotDisplaceExplicitRecommendationRead() async throws {
        let service = RecommendationModelService(), player = RecommendationModelPlayer()
        let model = MusicViewModel(service: service, player: player)
        await model.load()
        _ = await service.advance(.next, reason: .manual, expectedRevision: model.state.revision)
        let expectedID = await service.rotateToken()
        _ = await service.previewQueueRecommendations()
        model.requestRecommendationRefresh()
        await model.loadSimilarMode()
        let snapshot = try XCTUnwrap(model.queueRecommendations)
        XCTAssertEqual(snapshot.id, expectedID)
        XCTAssertEqual(snapshot.currentEntryID, "tail-next")
        XCTAssertEqual(snapshot.candidates.map(\.id), ["candidate-a", "candidate-b"])
        XCTAssertFalse(snapshot.continueEnabled)
        XCTAssertEqual(model.current?.id, "tail-next")
        XCTAssertTrue(player.resources.isEmpty, "Reading a new queue projection cannot start audio")
        await model.close()
    }

    func testReopenedPreviewWaitsForPriorSheetCancellation() async throws {
        let service = RecommendationModelService(), model = MusicViewModel(service: service, player: RecommendationModelPlayer())
        await model.load()
        let cancelling = expectation(description: "former sheet cancelling")
        let cancellation = MusicCollectionGate { cancelling.fulfill() }
        await service.blockPreviewCancellation(cancellation)
        model.cancelQueueRecommendationPreview()
        await fulfillment(of: [cancelling], timeout: 2)
        let reading = expectation(description: "new sheet reading")
        let modeRead = MusicCollectionGate { reading.fulfill() }
        await service.blockModeRead(modeRead)
        let preview = Task { await model.previewQueueRecommendations() }
        await fulfillment(of: [reading], timeout: 2)
        await modeRead.release(); await cancellation.release(); await preview.value
        XCTAssertEqual(model.queueRecommendations?.candidates.count, 2)
        let events = await service.previewEvents()
        XCTAssertEqual(events, ["cancelled", "preview"])
        await model.close()
    }

    func testRefreshDuringSubmittedPreviewUsesLatestCommittedQueue() async throws {
        let service = RecommendationModelService(), player = RecommendationModelPlayer()
        let model = MusicViewModel(service: service, player: player)
        await model.load()
        let entered = expectation(description: "submitted preview holding former queue")
        let gate = MusicCollectionGate { entered.fulfill() }
        await service.blockSnapshot(gate)
        let preview = Task { await model.previewQueueRecommendations() }
        await fulfillment(of: [entered], timeout: 2)
        _ = await service.advance(.next, reason: .manual, expectedRevision: model.state.revision)
        await model.loadSimilarMode()
        let revision = model.state.revision
        XCTAssertEqual(model.current?.id, "tail-next")
        XCTAssertEqual(model.queueRecommendations?.currentEntryID, "tail-next")
        await gate.release(); await preview.value
        XCTAssertEqual(model.state.revision, revision)
        XCTAssertEqual(model.current?.id, "tail-next")
        XCTAssertEqual(model.queueRecommendations?.currentEntryID, "tail-next")
        XCTAssertTrue(player.resources.isEmpty, "A batch notification only refreshes rows")
        await model.close()
    }

    func testCandidateSelectionAppendsWholeBatchAndPlaysSelectedOccurrence() async throws {
        let service = RecommendationModelService(), player = RecommendationModelPlayer()
        let model = MusicViewModel(service: service, player: player)
        await model.load(); await model.previewQueueRecommendations()
        let before = model.queue.map(\.id), snapshot = try XCTUnwrap(model.queueRecommendations)
        model.lastSelection = RecommendationModelService.selection
        await model.playQueueRecommendation(snapshotID: snapshot.id, candidateID: "candidate-b")
        XCTAssertEqual(model.queue.map(\.id), before + ["recommended-a", "recommended-b"])
        XCTAssertEqual(model.current?.id, "recommended-b")
        XCTAssertEqual(player.resources.count, 1)
        XCTAssertEqual(model.state.sourceCollectionID, "original-list")
        let calls = await service.calls()
        XCTAssertEqual(calls.choices, ["candidate-b"]); XCTAssertEqual(calls.enters, 0)
        await model.close()
    }

    func testQueueSwitchAndRetryNeverEnterOrExitSimilarMode() async throws {
        let service = RecommendationModelService(), player = RecommendationModelPlayer()
        let model = MusicViewModel(service: service, player: player)
        await model.load()
        let snapshot = try XCTUnwrap(model.queueRecommendations)
        await model.setQueueRecommendationContinue(true, snapshotID: snapshot.id)
        XCTAssertTrue(try XCTUnwrap(model.queueRecommendations).continueEnabled)
        await model.setQueueRecommendationContinue(false, snapshotID: snapshot.id)
        XCTAssertFalse(try XCTUnwrap(model.queueRecommendations).continueEnabled)
        await service.failRecommendations()
        await model.loadSimilarMode()
        XCTAssertTrue(try XCTUnwrap(model.queueRecommendations).canRetry)
        await model.retryQueueRecommendations(snapshotID: snapshot.id)
        XCTAssertEqual(model.queueRecommendations?.status, .ready)
        let calls = await service.calls()
        XCTAssertEqual(calls.queueSwitches, [true, false]); XCTAssertEqual(calls.retries, 1)
        XCTAssertEqual(calls.enters, 0); XCTAssertEqual(calls.exits, 0)
        XCTAssertTrue(player.resources.isEmpty)
        await model.close()
    }

    func testHeartbeatHomeAndCloudIntentAndCurrentOccurrenceHaveDistinctTypedStarts() async throws {
        let service = RecommendationModelService(), model = MusicViewModel(service: service, player: RecommendationModelPlayer())
        await model.load()
        try await model.playHomeAction(.heartbeat, title: "心动模式")
        await model.exitSimilarMode()
        await model.startHeartbeat() // the cloud-likes button's same-account intent
        await model.exitSimilarMode()
        let entry = try XCTUnwrap(model.current).id, revision = model.state.revision
        await model.startCurrentHeartbeat(occurrenceID: entry, expectedRevision: revision)
        let starts = await service.startRequests()
        XCTAssertEqual(starts, [.heartbeat, .heartbeat,
            .currentHeartbeat(occurrenceID: entry, expectedRevision: revision)])
        XCTAssertFalse(try XCTUnwrap(model.similarMode).continueEnabled)
        await model.startCurrentHeartbeat(occurrenceID: "browse-row", expectedRevision: revision)
        let count = await service.startRequests().count
        XCTAssertEqual(count, 3)
        await model.close()
    }

    func testHeartbeatContinueOffPreservesSessionAndReturnRestoresWithoutPlaying() async throws {
        let service = RecommendationModelService(), player = RecommendationModelPlayer()
        let model = MusicViewModel(service: service, player: player)
        await model.load()
        let original = model.queue.map(\.id), current = model.current?.id
        await model.startHeartbeat()
        let snapshot = try XCTUnwrap(model.similarMode)
        await model.setSimilarContinue(true, snapshotID: snapshot.id)
        await model.setSimilarContinue(false, snapshotID: snapshot.id)
        XCTAssertEqual(model.similarMode?.id, snapshot.id)
        XCTAssertTrue(try XCTUnwrap(model.similarMode).active)
        let exits = await service.exitCount()
        XCTAssertEqual(exits, 0)
        await model.exitSimilarMode()
        XCTAssertEqual(model.queue.map(\.id), original); XCTAssertEqual(model.current?.id, current)
        XCTAssertNil(model.similarMode)
        XCTAssertTrue(player.resources.isEmpty, "Returning an unopened current seed does not start audio")
        await model.close()
    }

    func testHeartbeatReturnKeepsPlayingSeedWithoutAudioCommands() async throws {
        try await returnCurrentSeed(paused: false)
    }

    func testHeartbeatReturnKeepsPausedSeedWithoutAudioCommands() async throws {
        try await returnCurrentSeed(paused: true)
    }

    private func returnCurrentSeed(paused: Bool) async throws {
        let service = RecommendationModelService(), player = RecommendationModelPlayer()
        let model = MusicViewModel(service: service, player: player)
        await model.load()
        let original = model.queue.map(\.id)
        model.lastSelection = RecommendationModelService.selection
        await model.prepareCurrent()
        let candidate = try XCTUnwrap(model.candidate)
        let playing = MusicPlaybackFact(attemptId: candidate.attemptId, occurrenceId: candidate.occurrenceId,
            resourceId: candidate.resourceId, kind: .playing, position: 43, duration: 180)
        model.onPlayback(playing)
        await model.flushPlaybackFacts()
        await model.startCurrentHeartbeat(occurrenceID: candidate.occurrenceId, expectedRevision: model.state.revision)
        XCTAssertTrue(try XCTUnwrap(model.similarMode).active)
        if paused {
            await model.togglePlayback()
            model.onPlayback(.init(attemptId: candidate.attemptId, occurrenceId: candidate.occurrenceId,
                resourceId: candidate.resourceId, kind: .paused, position: 43, duration: 180))
            await model.flushPlaybackFacts()
        }
        XCTAssertNil(model.error)
        XCTAssertEqual(model.phase, paused ? .paused : .playing)
        let media = try XCTUnwrap(model.nativeAttempt), phase = model.phase
        let plays = player.resources.count, stops = player.stops, pauses = player.pauses, resumes = player.resumes, seeks = player.seeks
        let resolutions = await service.calls().resolutions

        await model.exitSimilarMode()

        XCTAssertNil(model.error)
        XCTAssertEqual(model.queue.map(\.id), original)
        XCTAssertEqual(model.current?.id, candidate.occurrenceId)
        XCTAssertEqual(model.activeResourceID, candidate.resourceId)
        XCTAssertEqual(model.nativeAttempt, media); XCTAssertEqual(model.phase, phase)
        XCTAssertEqual(model.position, 43); XCTAssertEqual(model.state.position, 43)
        XCTAssertEqual(player.resources.count, plays); XCTAssertEqual(player.stops, stops)
        XCTAssertEqual(player.pauses, pauses); XCTAssertEqual(player.resumes, resumes); XCTAssertEqual(player.seeks, seeks)
        let calls = await service.calls()
        XCTAssertEqual(calls.resolutions, resolutions); XCTAssertEqual(calls.exits, 1)
        XCTAssertNil(model.similarMode)
        await model.close()
    }

    func testReturnCancelsWaitingHeartbeatAndDrainsBeforeRestoring() async throws {
        for lateFailure in [false, true] {
            let service = RecommendationModelService(), player = RecommendationModelPlayer()
            let model = MusicViewModel(service: service, player: player)
            await model.load()
            let entered = expectation(description: "heartbeat waiting")
            let gate = MusicCollectionGate { entered.fulfill() }
            await service.blockStart(gate, fail: lateFailure)
            let start = Task { await model.startHeartbeat() }
            await fulfillment(of: [entered], timeout: 2)
            let cancelled = expectation(description: "entry audio admission cancelled")
            let observation = model.playbackState.$isChangingSimilarMode.dropFirst().filter { !$0 }.prefix(1)
                .sink { _ in cancelled.fulfill() }
            let returning = Task { await model.exitSimilarMode() }
            await fulfillment(of: [cancelled], timeout: 2)
            await model.exitSimilarMode()
            let earlyExits = await service.exitCount()
            XCTAssertEqual(earlyExits, 0, "A second Return cannot bypass the first entry's drain")
            await gate.release(); await start.value; await returning.value
            observation.cancel()
            XCTAssertTrue(player.resources.isEmpty)
            XCTAssertNil(model.similarMode)
            XCTAssertNil(model.error, "A cancelled entry cannot publish its late upstream failure")
            XCTAssertEqual(model.current?.id, "original-2")
            let exits = await service.exitCount()
            XCTAssertEqual(exits, 1)
            await model.close()
        }
    }

    func testReturnBeforeHeartbeatSubmissionCancelsWithoutReportingAccessFailure() async throws {
        let service = RecommendationModelService(), player = RecommendationModelPlayer()
        let model = MusicViewModel(service: service, player: player)
        await model.load()
        let entered = expectation(description: "heartbeat pausing prior navigation")
        let gate = MusicCollectionGate { entered.fulfill() }
        await service.blockPause(gate)
        let start = Task { await model.startHeartbeat() }
        await fulfillment(of: [entered], timeout: 2)
        await model.exitSimilarMode()
        await gate.release(); await start.value
        let calls = await service.calls()
        XCTAssertTrue(calls.starts.isEmpty)
        XCTAssertEqual(calls.exits, 1)
        XCTAssertNil(model.similarMode)
        XCTAssertNil(model.error)
        XCTAssertEqual(model.current?.id, "original-2")
        XCTAssertTrue(player.resources.isEmpty)
        await model.close()
    }

    func testUncancelledHeartbeatServiceFailuresRemainVisible() async throws {
        for failure in [MusicServiceError.upstreamFailed, .staleAccess] {
            let service = RecommendationModelService(), player = RecommendationModelPlayer()
            let model = MusicViewModel(service: service, player: player)
            await model.load()
            await service.failNextStart(failure)
            await model.startHeartbeat()
            XCTAssertTrue(try XCTUnwrap(model.error).hasPrefix("开启心动模式：" + failure.localizedDescription))
            XCTAssertNil(model.similarMode)
            XCTAssertTrue(player.resources.isEmpty)
            await model.close()
        }
    }

    func testPauseWhileCandidateWaitsKeepsCommittedBatchWithoutLatePlayback() async throws {
        let service = RecommendationModelService(), player = RecommendationModelPlayer()
        let model = MusicViewModel(service: service, player: player)
        await model.load(); await model.previewQueueRecommendations()
        let snapshot = try XCTUnwrap(model.queueRecommendations)
        let entered = expectation(description: "candidate waiting")
        let gate = MusicCollectionGate { entered.fulfill() }
        await service.blockChoice(gate)
        model.lastSelection = RecommendationModelService.selection
        let choice = Task { await model.playQueueRecommendation(snapshotID: snapshot.id, candidateID: "candidate-b") }
        await fulfillment(of: [entered], timeout: 2)
        await model.togglePlayback()
        await gate.release(); await choice.value
        XCTAssertEqual(model.current?.id, "recommended-b")
        XCTAssertEqual(model.queue.suffix(2).map(\.id), ["recommended-a", "recommended-b"])
        XCTAssertTrue(player.resources.isEmpty)
        XCTAssertEqual(model.phase, .paused)
        await model.close()
    }

    func testPauseDuringAudioPreparationRevokesCandidateAdmission() async throws {
        let service = RecommendationModelService(), player = RecommendationModelPlayer()
        let model = MusicViewModel(service: service, player: player)
        await model.load(); await model.previewQueueRecommendations()
        let snapshot = try XCTUnwrap(model.queueRecommendations)
        let entered = expectation(description: "audio availability waiting")
        let gate = MusicCollectionGate { entered.fulfill() }
        await service.blockAvailability(gate)
        model.lastSelection = RecommendationModelService.selection
        let choice = Task { await model.playQueueRecommendation(snapshotID: snapshot.id, candidateID: "candidate-a") }
        await fulfillment(of: [entered], timeout: 2)
        await model.togglePlayback()
        await gate.release(); await choice.value
        XCTAssertTrue(player.resources.isEmpty)
        await model.close()
    }

    func testOlderSnapshotCannotReplaceNewTokenOrSurviveAccountAndQueueChanges() async throws {
        let service = RecommendationModelService(), model = MusicViewModel(service: service, player: RecommendationModelPlayer())
        await model.load()
        let entered = expectation(description: "older snapshot captured")
        let gate = MusicCollectionGate { entered.fulfill() }
        await service.blockSnapshot(gate)
        let old = Task { await model.loadSimilarMode() }
        await fulfillment(of: [entered], timeout: 2)
        let freshID = await service.rotateToken()
        await model.loadSimilarMode()
        XCTAssertEqual(model.queueRecommendations?.id, freshID)
        await gate.release(); await old.value
        XCTAssertEqual(model.queueRecommendations?.id, freshID)
        model.accountSessionsChanged(.netease)
        XCTAssertNil(model.queueRecommendations)
        await model.loadSimilarMode()
        let before = try XCTUnwrap(model.queueRecommendations).id
        await model.play([RecommendationModelService.track("91")], index: 0)
        XCTAssertNotEqual(model.queueRecommendations?.id, before)
        await model.close()
    }

    func testAccountChangeRejectsHeldSnapshotAndCancelledChoiceCannotPlay() async throws {
        let service = RecommendationModelService(), player = RecommendationModelPlayer()
        let model = MusicViewModel(service: service, player: player)
        await model.load()
        let readEntered = expectation(description: "former account snapshot waiting")
        let readGate = MusicCollectionGate { readEntered.fulfill() }
        await service.blockSnapshot(readGate)
        let read = Task { await model.loadSimilarMode() }
        await fulfillment(of: [readEntered], timeout: 2)
        model.accountSessionsChanged(.netease)
        await readGate.release(); await read.value
        XCTAssertNil(model.queueRecommendations)
        await model.loadSimilarMode(); await model.previewQueueRecommendations()
        let snapshot = try XCTUnwrap(model.queueRecommendations)
        let choiceEntered = expectation(description: "cancelled choice waiting")
        let choiceGate = MusicCollectionGate { choiceEntered.fulfill() }
        await service.blockChoice(choiceGate)
        let choice = Task { await model.playQueueRecommendation(snapshotID: snapshot.id, candidateID: "candidate-a") }
        await fulfillment(of: [choiceEntered], timeout: 2)
        choice.cancel()
        await choiceGate.release(); await choice.value
        XCTAssertEqual(model.queue.map(\.id), ["original-1", "original-2"])
        XCTAssertTrue(player.resources.isEmpty)
        await model.close()
    }

    func testFormerAccountContinuationFailureDoesNotPublishError() async throws {
        let service = RecommendationModelService(), model = MusicViewModel(service: service, player: RecommendationModelPlayer())
        await model.load()
        let snapshot = try XCTUnwrap(model.queueRecommendations)
        let entered = expectation(description: "former account setting waiting")
        let gate = MusicCollectionGate { entered.fulfill() }
        await service.blockSwitchFailure(gate)
        let setting = Task { await model.setQueueRecommendationContinue(true, snapshotID: snapshot.id) }
        await fulfillment(of: [entered], timeout: 2)
        model.accountSessionsChanged(.netease)
        await gate.release(); await setting.value
        XCTAssertNil(model.error)
        XCTAssertNil(model.queueRecommendations)
        await model.close()
    }

    func testAccountChangeCancelsWaitingCandidateSelectionWithoutLatePlayback() async throws {
        let service = RecommendationModelService(), player = RecommendationModelPlayer()
        let model = MusicViewModel(service: service, player: player)
        await model.load(); await model.previewQueueRecommendations()
        let snapshot = try XCTUnwrap(model.queueRecommendations)
        let entered = expectation(description: "former account candidate waiting")
        let gate = MusicCollectionGate { entered.fulfill() }
        await service.blockChoice(gate)
        let choice = Task { await model.playQueueRecommendation(snapshotID: snapshot.id, candidateID: "candidate-b") }
        await fulfillment(of: [entered], timeout: 2)
        model.accountSessionsChanged(.netease)
        await gate.release(); await choice.value
        XCTAssertEqual(model.queue.map(\.id), ["original-1", "original-2"])
        XCTAssertFalse(model.isChangingQueueRecommendations)
        XCTAssertNil(model.queueRecommendations)
        XCTAssertNil(model.error)
        XCTAssertTrue(player.resources.isEmpty)
        await model.close()
    }

    func testAccountChangeWhileTailNavigationWaitsCannotPlayLateSelection() async throws {
        let service = RecommendationModelService(), player = RecommendationModelPlayer()
        let model = MusicViewModel(service: service, player: player)
        await model.load()
        model.lastSelection = RecommendationModelService.selection
        let entered = expectation(description: "former account tail waiting")
        let gate = MusicCollectionGate { entered.fulfill() }
        await service.blockAdvance(gate)
        let navigation = Task { await model.next() }
        await fulfillment(of: [entered], timeout: 2)
        model.accountSessionsChanged(.netease)
        await gate.release(); await navigation.value
        XCTAssertNil(model.error)
        XCTAssertTrue(player.resources.isEmpty)
        await model.close()
    }

    func testPausedTailNavigationDoesNotPlayLateSelection() async throws {
        let service = RecommendationModelService(), player = RecommendationModelPlayer()
        let model = MusicViewModel(service: service, player: player)
        await model.load()
        model.lastSelection = RecommendationModelService.selection
        let entered = expectation(description: "tail navigation waiting")
        let gate = MusicCollectionGate { entered.fulfill() }
        await service.blockAdvance(gate)
        let navigation = Task { await model.next() }
        await fulfillment(of: [entered], timeout: 2)
        await model.togglePlayback()
        await gate.release(); await navigation.value
        XCTAssertEqual(model.current?.id, "tail-next")
        XCTAssertTrue(player.resources.isEmpty)
        XCTAssertEqual(model.phase, .paused)
        await model.close()
    }

    func testFMChoicesExposeFourModesAndAllFortyThreeOfficialScenes() {
        XCTAssertEqual(MusicFMModeChoice.main.map(\.mode), [.default, .familiar, .explore, .puzzle])
        let expected = [
            "NIGHT_EMO", "EXERCISE", "SLEEP_HELP", "RELAX", "CHEERFUL", "LYRICAL", "CURE", "FOCUS", "SWEET",
            "RHYTHM_BLUES", "RAINY", "GAMES", "RAP", "K_POP", "ORIGINAL_MUSICIAL", "ELECTRONIC", "COMMUTE",
            "TAKE_SHOWER", "COFFEE_SHOP", "ROCK", "INSPIRATIONAL", "CHINESE", "ENGLISH", "YUEYU", "MANYAO",
            "JINGDIAN", "LIGHT", "GUOFENG", "FOLK", "ACG", "GUDIAN", "JAZZ", "JAPANESE", "GLOBAL", "FRANCH",
            "BLUE", "DANCE", "LATIN", "PUNK", "COUNTRY", "MUSICAL", "YINGSHI", "HOT_SONG"
        ]
        XCTAssertEqual(MusicFMModeChoice.scenes.map(\.submode), expected)
        XCTAssertEqual(Set(MusicFMModeChoice.scenes.map(\.id)).count, 43)
        XCTAssertEqual(MusicFMModeChoice.scenes.last?.title, "热歌")
        for choice in MusicFMModeChoice.main + MusicFMModeChoice.scenes {
            XCTAssertTrue(choice.matches(choice.action))
            XCTAssertEqual(MusicFMModeChoice.title(for: choice.action), choice.title)
        }
    }

    func testFMSelectionPublishesOnlyCommittedChoiceAndPreservesConfigurationOnFailure() async throws {
        let service = RecommendationModelService(), model = MusicViewModel(service: service, player: RecommendationModelPlayer())
        await model.load()
        let initial = MusicFMModeChoice.main[0], next = MusicFMModeChoice.main[2]
        try await model.playHomeAction(initial.action, title: initial.title)
        let settings = MusicFMSettingsViewModel(owner: model)
        settings.setAccess(available: true, generation: model.searchAccessGeneration)
        let entered = expectation(description: "FM choice waiting")
        let gate = MusicCollectionGate { entered.fulfill() }
        await service.blockFM(gate, fail: true)
        settings.select(next)
        await fulfillment(of: [entered], timeout: 2)
        XCTAssertEqual(settings.pendingChoice, next.id)
        XCTAssertEqual(model.activeHomeFM, initial.action)
        settings.select(MusicFMModeChoice.main[1])
        XCTAssertEqual(settings.pendingChoice, next.id, "The existing selector admits one choice at a time")
        let failed = expectation(description: "FM failure reported")
        let errorObservation = settings.$error.compactMap { $0 }.prefix(1).sink { _ in failed.fulfill() }
        await gate.release()
        await fulfillment(of: [failed], timeout: 2)
        XCTAssertEqual(model.activeHomeFM, initial.action)
        XCTAssertNil(settings.pendingChoice)
        errorObservation.cancel(); settings.close()
        await model.close()
    }

    func testClosingFMSelectionCancelsWaitingChoiceWithoutPublishingIt() async throws {
        let service = RecommendationModelService(), model = MusicViewModel(service: service, player: RecommendationModelPlayer())
        await model.load()
        let initial = MusicFMModeChoice.main[0], next = try XCTUnwrap(MusicFMModeChoice.scenes.last)
        try await model.playHomeAction(initial.action, title: initial.title)
        let settings = MusicFMSettingsViewModel(owner: model)
        settings.setAccess(available: true, generation: model.searchAccessGeneration)
        let entered = expectation(description: "cancelled FM choice waiting")
        let gate = MusicCollectionGate { entered.fulfill() }
        await service.blockFM(gate)
        settings.select(next)
        await fulfillment(of: [entered], timeout: 2)
        settings.close()
        await gate.release()
        await settings.finishPending()
        XCTAssertEqual(model.activeHomeFM, initial.action)
        XCTAssertFalse(settings.selectionCompleted)
        XCTAssertNil(settings.pendingChoice)
        await model.close()
    }

    func testFMCommittedChoiceUpdatesAfterWaitAndAccountChangeRejectsOldChoice() async throws {
        let service = RecommendationModelService(), model = MusicViewModel(service: service, player: RecommendationModelPlayer())
        await model.load()
        let initial = MusicFMModeChoice.main[0], next = try XCTUnwrap(MusicFMModeChoice.scenes.last)
        try await model.playHomeAction(initial.action, title: initial.title)
        let settings = MusicFMSettingsViewModel(owner: model)
        settings.setAccess(available: true, generation: model.searchAccessGeneration)
        let entered = expectation(description: "successful FM choice waiting")
        let gate = MusicCollectionGate { entered.fulfill() }
        await service.blockFM(gate)
        settings.select(next)
        await fulfillment(of: [entered], timeout: 2)
        XCTAssertEqual(model.activeHomeFM, initial.action)
        await gate.release(); await settings.finishPending()
        XCTAssertEqual(model.activeHomeFM, next.action)
        XCTAssertTrue(settings.selectionCompleted)
        settings.acknowledgeSelection()
        let oldEntered = expectation(description: "former account FM choice waiting")
        let oldGate = MusicCollectionGate { oldEntered.fulfill() }
        await service.blockFM(oldGate)
        settings.select(MusicFMModeChoice.main[1])
        await fulfillment(of: [oldEntered], timeout: 2)
        model.accountSessionsChanged(.netease)
        await oldGate.release(); await settings.finishPending()
        XCTAssertNil(model.activeHomeFM)
        XCTAssertFalse(settings.selectionCompleted)
        settings.close()
        await model.close()
    }
}

private actor RecommendationModelService: MusicServing, MusicSimilarModeServing, MusicQueueRecommendationServing,
    MusicDisplayedQueuePlaying, MusicSearchBatchPlaying, MusicHomeActionQueueServing, MusicRemoteQueueServing {
    struct Calls: Sendable {
        var previews = 0, queueWrites = 0, enters = 0, exits = 0, retries = 0, displayed = 0, searches = 0, resolutions = 0
        var choices: [String] = [], queueSwitches: [Bool] = [], starts: [MusicSimilarModeStart] = []
    }
    static let selection = MusicPlaybackSelection(installationId: "official:synthetic", generation: 1,
        siteId: "wy", sourceKey: "wy:101", quality: "128k")
    static func track(_ id: String) -> MusicRemoteTrack {
        .init(ref: .init(kind: .handle, id: id), songId: id, title: "合成歌曲 " + id,
              sources: [.init(sourceKey: "wy:" + id, platform: "wy", nativeId: id)])
    }
    private var entries: [MusicQueueEntry] = ["101", "102"].enumerated().map {
        .init(id: "original-\($0.offset + 1)", track: track($0.element))
    }
    private var current = "original-2", revision = 3, token = UUID(), continuation = false
    private var position: Double = 0
    private var mode: MusicSimilarModeSnapshot?, checkpoint: ([MusicQueueEntry], String)?
    private var status = MusicSimilarModeSnapshot.Status.ready
    private var candidates: [MusicSimilarModeCandidate] = []
    private var counts = Calls()
    private var startGate: MusicCollectionGate?, choiceGate: MusicCollectionGate?, snapshotGate: MusicCollectionGate?
    private var availabilityGate: MusicCollectionGate?, advanceGate: MusicCollectionGate?
    private var modeReadGate: MusicCollectionGate?, previewCancellationGate: MusicCollectionGate?, switchFailureGate: MusicCollectionGate?
    private var previewOrder: [String] = []
    private var fmGate: MusicCollectionGate?, fmFailure = false
    private var startFailure: MusicServiceError?
    private var pauseGate: MusicCollectionGate?
    func calls() -> Calls { counts }
    func startRequests() -> [MusicSimilarModeStart] { counts.starts }
    func exitCount() -> Int { counts.exits }
    func blockStart(_ gate: MusicCollectionGate, fail: Bool = false) {
        startGate = gate; startFailure = fail ? .upstreamFailed : nil
    }
    func failNextStart(_ failure: MusicServiceError) { startFailure = failure }
    func blockPause(_ gate: MusicCollectionGate) { pauseGate = gate }
    func blockChoice(_ gate: MusicCollectionGate) { choiceGate = gate }
    func blockSnapshot(_ gate: MusicCollectionGate) { snapshotGate = gate }
    func blockAvailability(_ gate: MusicCollectionGate) { availabilityGate = gate }
    func blockAdvance(_ gate: MusicCollectionGate) { advanceGate = gate }
    func blockModeRead(_ gate: MusicCollectionGate) { modeReadGate = gate }
    func blockPreviewCancellation(_ gate: MusicCollectionGate) { previewCancellationGate = gate }
    func blockSwitchFailure(_ gate: MusicCollectionGate) { switchFailureGate = gate }
    func previewEvents() -> [String] { previewOrder }
    func blockFM(_ gate: MusicCollectionGate, fail: Bool = false) { fmGate = gate; fmFailure = fail }
    func failRecommendations() { status = .error }
    @discardableResult func rotateToken() -> UUID { token = UUID(); return token }
    func state() -> MusicPlaybackState {
        .init(revision: revision, currentId: current, total: entries.count, position: position, canNext: true,
              sourceCollectionID: "original-list")
    }
    func queue() -> MusicQueueSnapshot { .init(revision: revision, currentId: current, entries: entries) }
    func queueRecommendationSnapshot() async -> MusicQueueRecommendationSnapshot? {
        guard mode == nil else { return nil }
        let value = MusicQueueRecommendationSnapshot(id: token, status: status, currentEntryID: current,
            continueEnabled: continuation, candidates: candidates, exhausted: false,
            error: status == .error ? .upstreamFailed : nil, canRetry: status == .error)
        let gate = snapshotGate; snapshotGate = nil
        if let gate { await gate.wait() }
        return value
    }
    func previewQueueRecommendations() async -> MusicQueueRecommendationSnapshot? {
        counts.previews += 1; previewOrder.append("preview")
        candidates = ["a", "b"].map { .init(id: "candidate-" + $0, song: .init(title: "推荐 " + $0)) }
        return await queueRecommendationSnapshot()
    }
    func playQueueRecommendation(snapshotID: UUID, candidateID: String) async throws -> MusicSimilarModeResult {
        let gate = choiceGate; choiceGate = nil
        if let gate { await gate.wait() }
        try Task.checkCancellation()
        guard token == snapshotID, candidates.contains(where: { $0.id == candidateID }) else { throw MusicServiceError.targetChanged }
        counts.choices.append(candidateID)
        entries += ["a", "b"].map { .init(id: "recommended-" + $0, track: Self.track($0 == "a" ? "201" : "202")) }
        current = candidateID == "candidate-a" ? "recommended-a" : "recommended-b"
        revision += 1; counts.queueWrites += 1; candidates = []; token = UUID()
        return .init(state: state(), action: .playCurrent)
    }
    func setQueueRecommendationContinue(_ enabled: Bool, snapshotID: UUID) async throws {
        let gate = switchFailureGate; switchFailureGate = nil
        if let gate { await gate.wait(); throw MusicServiceError.upstreamFailed }
        guard token == snapshotID else { throw MusicServiceError.targetChanged }
        continuation = enabled; counts.queueSwitches.append(enabled)
    }
    func retryQueueRecommendations(snapshotID: UUID) throws -> MusicSimilarModeResult {
        guard token == snapshotID, status == .error else { throw MusicServiceError.targetChanged }
        status = .ready; counts.retries += 1
        return .init(state: state(), action: .keepCurrent)
    }
    func cancelQueueRecommendationPreview() async {
        let gate = previewCancellationGate; previewCancellationGate = nil
        if let gate { await gate.wait() }
        previewOrder.append("cancelled")
    }
    func similarModeSnapshot() async -> MusicSimilarModeSnapshot? {
        let gate = modeReadGate; modeReadGate = nil
        if let gate { await gate.wait() }
        return mode
    }
    func similarModeChanges() -> AsyncStream<Void> { .init { $0.finish() } }
    func canEnterSimilarMode() -> Bool { true }
    func previewSimilarMode() -> MusicSimilarModeSnapshot? { mode }
    func enterSimilarMode() throws -> MusicSimilarModeResult { counts.enters += 1; throw MusicServiceError.unsupported }
    func startSimilarMode(_ start: MusicSimilarModeStart) async throws -> MusicSimilarModeResult {
        counts.starts.append(start)
        let gate = startGate, failure = startFailure; startGate = nil; startFailure = nil
        if let gate { await gate.wait() }
        if let failure { throw failure }
        try Task.checkCancellation()
        checkpoint = (entries, current)
        mode = .init(id: UUID(), active: true, status: .ready, seed: .init(title: "喜欢种子"), seedEntryID: current,
            continueEnabled: false, candidates: [], batches: [], sourceIDs: [], fromHome: start == .heartbeat,
            provider: .heartbeat, error: nil, canRetry: false)
        entries.append(.init(id: "heartbeat-1", track: Self.track("301")))
        revision += 1
        return .init(state: state(), action: .keepCurrent)
    }
    func exitSimilarMode() -> MusicSimilarModeResult {
        counts.exits += 1
        if let checkpoint { entries = checkpoint.0; current = checkpoint.1; revision += 1 }
        checkpoint = nil; mode = nil
        return .init(state: state(), action: .keepCurrent)
    }
    func playSimilarCandidate(snapshotID: UUID, candidateID: String) throws -> MusicSimilarModeResult { throw MusicServiceError.unsupported }
    func setSimilarContinue(_ enabled: Bool, snapshotID: UUID) throws {
        guard let value = mode, value.id == snapshotID else { throw MusicServiceError.targetChanged }
        mode = .init(id: value.id, active: true, status: .ready, seed: value.seed, seedEntryID: value.seedEntryID,
            continueEnabled: enabled, candidates: [], batches: [], sourceIDs: [], fromHome: value.fromHome,
            provider: .heartbeat, error: nil, canRetry: false)
    }
    func retrySimilarMode(snapshotID: UUID) throws -> MusicSimilarModeResult { throw MusicServiceError.unsupported }
    func cancelSimilarMode() { mode = nil }
    func recordSimilarPlaybackFact(_ fact: MusicPlaybackFact) -> Bool { record(fact) }
    func pauseSimilarNavigation() async {
        let gate = pauseGate; pauseGate = nil
        if let gate { await gate.wait() }
    }
    private func replace(_ tracks: [MusicRemoteTrack], index: Int) -> MusicPlaybackState {
        entries = tracks.enumerated().map { .init(id: "replacement-\(revision)-\($0.offset)", track: $0.element) }
        current = entries[index].id; revision += 1; counts.queueWrites += 1
        token = UUID(); continuation = false; mode = nil; candidates = []
        return state()
    }
    func replaceDisplayedQueue(_ tracks: [MusicRemoteTrack], index: Int, origin: MusicDisplayedQueueOrigin,
                               expectedRevision: Int, intent: MusicQueueAuthorization) throws -> MusicPlaybackState {
        try intent.check(); counts.displayed += 1
        return replace(tracks, index: index)
    }
    func searchRecording(_ request: MusicSearchBatchRequest) -> MusicRemoteTrack { request.targets[request.startIndex].track }
    func replaceSearchBatch(_ request: MusicSearchBatchRequest, expectedRevision: Int,
                            intent: MusicQueueAuthorization) throws -> MusicPlaybackState {
        try intent.check(); counts.searches += 1
        return replace(request.targets.map(\.track), index: request.startIndex)
    }
    func changeQueue(_ change: MusicQueueChange, expectedRevision: Int) throws -> MusicPlaybackState {
        guard case .replace(let refs, let index) = change else { throw MusicServiceError.unsupported }
        return replace(refs.map { Self.track($0.id) }, index: index)
    }
    func advance(_ direction: MusicDirection, reason: MusicAdvanceReason, expectedRevision: Int) async -> MusicAdvanceResult {
        let gate = advanceGate; advanceGate = nil
        if let gate { await gate.wait() }
        entries.append(.init(id: "tail-next", track: Self.track("401"))); current = "tail-next"; revision += 1
        return .init(state: state(), selectedId: current)
    }
    func sourceAvailability(for ref: MusicReference) async -> MusicSourceAvailability {
        let gate = availabilityGate; availabilityGate = nil
        if let gate { await gate.wait() }
        return .init(options: [.init(installationId: Self.selection.installationId, generation: 1, siteId: "wy",
            sourceKey: Self.selection.sourceKey, qualities: ["128k"], title: "合成音源", version: "1", contentHash: "")], scriptState: .ready)
    }
    func openHomeActionQueue(_ action: MusicHomeAction, title: String) async throws -> MusicRemoteQueueSource {
        let gate = fmGate, fail = fmFailure; fmGate = nil; fmFailure = false
        if let gate { await gate.wait() }
        try Task.checkCancellation()
        if fail { throw MusicServiceError.upstreamFailed }
        return .init(context: .init(kind: .fm, id: "synthetic-fm", title: title, platform: "wy"),
            loadPage: { _ in .init(items: []) })
    }
    func openAccountQueue(platform: MusicAccountPlatform, kind: MusicAccountQueueKind,
                          title: String) throws -> MusicRemoteQueueSource { throw MusicServiceError.unsupported }
    func remoteQueuePage(_ source: MusicRemoteQueueSource, cursor: String?) async throws -> MusicBrowsePage {
        .init(items: [Self.track("501")], cursor: nil)
    }
    func replaceRemoteQueue(_ source: MusicRemoteQueueSource, start: MusicRemoteQueueStart,
                            expectedRevision: Int, intent: MusicQueueAuthorization) throws -> MusicPlaybackState {
        try intent.check(); try source.authorization.check()
        entries = [.init(id: "fm-\(revision)", track: Self.track("501"), remoteSessionID: source.id)]
        current = entries[0].id; revision += 1
        return state()
    }
    func advanceFM(_ source: MusicRemoteQueueSource, direction: MusicDirection, reason: MusicAdvanceReason,
                   expectedRevision: Int, intent: MusicQueueAuthorization) throws -> MusicAdvanceResult {
        throw MusicServiceError.unsupported
    }
    func resolve(attemptId: String, occurrenceId: String, selection: MusicPlaybackSelection) -> MusicPlaybackCandidate {
        counts.resolutions += 1
        return .init(attemptId: attemptId, occurrenceId: occurrenceId, resourceId: "music:" + attemptId,
            url: URL(string: "https://media.invalid/synthetic.mp3")!, title: "合成歌曲", requestedQuality: "128k",
            resolvedSelection: selection)
    }
    func platforms() -> [MusicPlatform] { [] }
    func collections() -> [MusicCollection] { [] }
    func tracks(in collection: MusicCollection) -> [MusicRemoteTrack] { [] }
    func search(platform: String, query: String, page: Int, limit: Int) throws -> MusicSearchPage { throw MusicServiceError.unsupported }
    func createPlaylist(name: String, refs: [MusicReference]) throws -> MusicCollection { throw MusicServiceError.unsupported }
    func editPlaylist(_ collection: MusicCollection, name: String?, refs: [MusicReference]?, order: [String]?) throws -> MusicCollection { throw MusicServiceError.unsupported }
    func removePlaylist(_ collection: MusicCollection) throws { throw MusicServiceError.unsupported }
    func updateSettings(_ change: MusicSettingsChange) -> MusicPlaybackState { state() }
    func metadataPreferences() throws -> MusicMetadataPreferences { throw MusicServiceError.unsupported }
    func updateMetadataPreferences(_ change: MusicMetadataPreferencesChange) throws -> MusicMetadataPreferences { throw MusicServiceError.unsupported }
    func metadataCandidates(occurrenceID: String) throws -> MusicMetadataCandidates { throw MusicServiceError.unsupported }
    func cancel(attemptId: String?) {}
    func record(_ fact: MusicPlaybackFact) -> Bool { position = fact.position; return true }
    func lyrics(occurrenceId: String, sourceKey: String) -> MusicLyricsSnapshot { .init(occurrenceId: occurrenceId, state: .none, lines: []) }
    func playedSongs() -> [MusicPlayedSong] { [] }
    func playedPlaylists() -> [MusicPlayedPlaylist] { [] }
    func restoreHistory(id: String, expectedRevision: Int) throws -> MusicPlaybackState { throw MusicServiceError.unsupported }
}

@MainActor private final class RecommendationModelPlayer: MusicPlaybackDriving {
    var resources: [PlaybackResource] = []
    var pauses = 0, resumes = 0, stops = 0
    var seeks: [Double] = []
    func play(_ resource: PlaybackResource) -> MusicPlayerAttempt {
        resources.append(resource); return .init(id: UUID(), resourceID: resource.id)
    }
    func pause(_ attempt: MusicPlayerAttempt) { pauses += 1 }
    func resume(_ attempt: MusicPlayerAttempt) { resumes += 1 }
    func seek(_ position: Double, attempt: MusicPlayerAttempt) -> Bool { seeks.append(position); return false }
    func stop(_ attempt: MusicPlayerAttempt) { stops += 1 }
}
#endif
