#if os(iOS) || os(macOS)
import Foundation
import Combine
import XCTest
import MeowContracts
@testable import MeowMusicFeature
@testable import MeowMusicNativeService

@MainActor private final class FeedbackSettingsPlayer: MusicPlaybackDriving {
    func play(_ resource: PlaybackResource) -> MusicPlayerAttempt {
        XCTFail("Settings reconciliation must not start media")
        return .init(id: UUID(), resourceID: resource.id)
    }
    func pause(_ attempt: MusicPlayerAttempt) { XCTFail("Settings reconciliation must not pause media") }
    func resume(_ attempt: MusicPlayerAttempt) { XCTFail("Settings reconciliation must not resume media") }
    func seek(_ position: Double, attempt: MusicPlayerAttempt) -> Bool {
        XCTFail("Settings reconciliation must not seek media"); return false
    }
    func stop(_ attempt: MusicPlayerAttempt) { XCTFail("Settings reconciliation must not stop media") }
}

@MainActor final class MusicPlaybackFeedbackSettingsOwnershipTests: XCTestCase {
    private func settings(_ owner: MusicViewModel) async throws
        -> (MusicAccountsSettingsState, MusicPlaybackFeedbackSettingsModel) {
        let page = MusicAccountsSettingsState(owner: owner)
        let feedback = try XCTUnwrap(page.feedback)
        feedback.setAccess(available: true, generation: owner.accountAccessGeneration)
        await feedback.load()
        return (page, feedback)
    }

    func testFeedbackWriteUpdatesSharedSettingsBeforeNextQualityCAS() async throws {
        let store = NativeMusicMemoryDocument(), service = try await NativeMusicFixture.service(store)
        let owner = MusicViewModel(service: service, player: FeedbackSettingsPlayer())
        let (page, feedback) = try await settings(owner)
        let writes = store.snapshot().2
        feedback.update(.qq, true); await feedback.flush()
        XCTAssertNil(feedback.error)
        XCTAssertEqual(owner.state.settingsRevision, feedback.value?.revision)
        let quality = MusicQualitySettingsState(model: owner)
        quality.start()
        await owner.updateSettings(.init(ifRevision: quality.value.revision, qualityEnabled: ["standard"]))
        XCTAssertNil(owner.error)
        XCTAssertEqual(owner.state.settingsRevision, 2)
        XCTAssertEqual(owner.state.qualityEnabled, ["standard"])
        XCTAssertFalse(owner.hasLoadedLibrary)
        XCTAssertTrue(owner.queue.isEmpty)
        XCTAssertEqual(store.snapshot().2, writes + 2)
        page.close(); await owner.close(); await service.close()
    }

    func testRetainedAccountsObserveQualityWriteBeforeSingleFeedbackCAS() async throws {
        let store = NativeMusicMemoryDocument(), service = try await NativeMusicFixture.service(store)
        let owner = MusicViewModel(service: service, player: FeedbackSettingsPlayer())
        let page = MusicAccountsSettingsState(owner: owner)
        let feedback = try XCTUnwrap(page.feedback)
        let loaded = expectation(description: "Feedback settings loaded")
        let initial = feedback.$value.compactMap { $0 }.filter { $0.revision == 0 }.prefix(1)
            .sink { _ in loaded.fulfill() }
        defer { initial.cancel() }
        page.start()
        await fulfillment(of: [loaded], timeout: 3); await feedback.flush()
        feedback.update(.kugou, true)
        let oldConfirmation = try XCTUnwrap(feedback.confirmation)
        let updated = expectation(description: "Retained feedback receives shared quality revision")
        let observation = feedback.$value.compactMap { $0 }.filter { $0.revision == 1 }.prefix(1)
            .sink { _ in updated.fulfill() }
        defer { observation.cancel() }
        let quality = MusicQualitySettingsState(model: owner)
        quality.start()
        await owner.updateSettings(.init(ifRevision: quality.value.revision, qualityEnabled: ["standard"]))
        XCTAssertNil(owner.error)
        await fulfillment(of: [updated], timeout: 3); await feedback.flush()
        XCTAssertEqual(feedback.value?.revision, owner.state.settingsRevision)
        XCTAssertNil(feedback.confirmation)
        let writes = store.snapshot().2
        feedback.confirm(oldConfirmation); await feedback.flush()
        XCTAssertEqual(store.snapshot().2, writes)
        feedback.update(.qq, true); await feedback.flush()
        XCTAssertNil(feedback.error)
        XCTAssertEqual(feedback.value?.platforms[.qq]?.enabled, true)
        XCTAssertEqual(owner.state.settingsRevision, 2)
        XCTAssertEqual(store.snapshot().2, writes + 1)
        XCTAssertFalse(owner.hasLoadedLibrary)
        page.close(); await owner.close(); await service.close()
    }

    func testFeedbackConflictReadsSharedAuthorityWithoutReplaying() async throws {
        let store = NativeMusicMemoryDocument(), service = try await NativeMusicFixture.service(store)
        let owner = MusicViewModel(service: service, player: FeedbackSettingsPlayer())
        let (page, feedback) = try await settings(owner)
        let changed = try await service.updateSettings(.init(ifRevision: 0, qualityEnabled: ["standard"]))
        let before = store.snapshot()
        feedback.update(.qq, true); await feedback.flush()
        XCTAssertEqual(feedback.error, MusicServiceError.staleRevision.localizedDescription)
        XCTAssertEqual(feedback.value?.revision, changed.settingsRevision)
        XCTAssertEqual(feedback.value?.platforms[.qq]?.enabled, false)
        XCTAssertEqual(owner.state, changed)
        XCTAssertEqual(store.snapshot().0, before.0)
        XCTAssertEqual(store.snapshot().2, before.2)
        feedback.update(.qq, true); await feedback.flush()
        XCTAssertNil(feedback.error)
        XCTAssertEqual(owner.state.settingsRevision, 2)
        XCTAssertEqual(owner.state.qualityEnabled, ["standard"])
        XCTAssertEqual(store.snapshot().2, before.2 + 1)
        page.close(); await owner.close(); await service.close()
    }

    func testSettingsReadbackKeepsQueueFactsSurvivesCallerCancellationAndRejectsOldAccessOrRevision() async throws {
        let store = NativeMusicMemoryDocument(), service = try await NativeMusicFixture.service(store)
        let owner = MusicViewModel(service: service, player: FeedbackSettingsPlayer())
        let generation = owner.accountAccessGeneration
        _ = try await service.setPlaybackFeedback(platform: .qq, enabled: true, expectedRevision: 0)
        let readback = Task { try await owner.reconcilePlaybackFeedbackSettings(accessGeneration: generation) }
        readback.cancel(); try await readback.value
        XCTAssertEqual(owner.state.settingsRevision, 1)
        owner.playbackState.state = .init(revision: 5, currentId: "retained-entry", total: 1,
            position: 12, canNext: true, nextPreviewID: "retained-next", sourceCollectionID: "retained-collection")
        owner.clock.position = 15
        try await owner.reconcilePlaybackFeedbackSettings(accessGeneration: generation)
        XCTAssertEqual(owner.state.settingsRevision, 1)
        XCTAssertEqual(owner.state.revision, 5)
        XCTAssertEqual(owner.state.currentId, "retained-entry")
        XCTAssertEqual(owner.state.position, 12)
        XCTAssertEqual(owner.state.nextPreviewID, "retained-next")
        XCTAssertEqual(owner.state.sourceCollectionID, "retained-collection")
        XCTAssertEqual(owner.clock.position, 15)
        let newer = MusicPlaybackState(revision: 5, settingsRevision: 5)
        owner.playbackState.state = newer
        try await owner.reconcilePlaybackFeedbackSettings(accessGeneration: generation)
        XCTAssertEqual(owner.state, newer)
        let reads = store.snapshot().1
        do {
            try await owner.reconcilePlaybackFeedbackSettings(accessGeneration: UUID())
            XCTFail("An old access generation must not read or publish settings")
        } catch { XCTAssertEqual(error as? MusicServiceError, .staleAccess) }
        XCTAssertEqual(store.snapshot().1, reads)
        await owner.close(); await service.close()
    }
}
#endif
