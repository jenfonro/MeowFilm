import Foundation
import XCTest
import MeowContracts
@testable import MeowPlayback
@testable import MeowMusicFeature

private actor FeedbackFeatureSink: MusicPlaybackFeedbackConsuming {
    var samples: [MusicPlaybackFeedbackSample] = []
    var cancellations: [String?] = []
    func consumePlaybackFeedback(_ sample: MusicPlaybackFeedbackSample) { samples.append(sample) }
    func cancelPlaybackFeedback(attemptID: String?) { cancellations.append(attemptID) }
    func recorded() -> [MusicPlaybackFeedbackSample] { samples }
}

private actor FeedbackSettingsStore: MusicPlaybackFeedbackSettingsServing {
    private var value = MusicPlaybackFeedbackSettings(revision: 0, platforms: [:])
    private var writes = 0
    private var failure: MusicServiceError?
    func fail(_ error: MusicServiceError?) { failure = error }
    func playbackFeedbackSettings() -> MusicPlaybackFeedbackSettings { value }
    func setPlaybackFeedback(platform: MusicAccountPlatform, enabled: Bool,
                             expectedRevision: Int) throws -> MusicPlaybackFeedbackSettings {
        writes += 1
        if let failure { throw failure }
        guard expectedRevision == value.revision else { throw MusicServiceError.staleRevision }
        var platforms = value.platforms
        platforms[platform] = .init(enabled: enabled, generation: (platforms[platform]?.generation ?? 0) + 1)
        value = .init(revision: value.revision + 1, platforms: platforms)
        return value
    }
    func count() -> Int { writes }
}

@MainActor final class MusicPlaybackFeedbackTests: XCTestCase {
    private func candidate(_ id: String = "observed") -> MusicPlaybackCandidate {
        .init(attemptId: id, occurrenceId: "entry-1", resourceId: "music:" + id,
              url: URL(string: "https://media.invalid/synthetic.mp3")!, title: "Synthetic", requestedQuality: "128k")
    }
    private func event(_ kind: PlaybackEvent.Kind, id: UUID, owner: String = "music",
                       resource: String = "music:observed", position: Double = 0,
                       phase: PlaybackPhase = .playing) -> PlaybackEvent {
        .init(attempt: .init(id: id, owner: owner, resourceID: resource), kind: kind,
              snapshot: .init(owner: owner, resourceID: resource, phase: phase, position: position, duration: 180), error: nil)
    }
    func testOnlyAcceptedMusicEventsProduceOrderedObservations() async {
        let sink = FeedbackFeatureSink(), consumer = MusicPlaybackFeedbackConsumer(service: sink, clock: { 10 })
        let id = UUID(), item = candidate()
        consumer.consume(event(.preparing, id: id), candidate: item)
        consumer.consume(event(.prepared, id: id), candidate: item)
        consumer.consume(event(.played, id: id, owner: "film"), candidate: item)
        consumer.consume(event(.played, id: id, resource: "music:foreign"), candidate: item)
        await consumer.flush()
        let empty = await sink.recorded(); XCTAssertTrue(empty.isEmpty)
        consumer.consume(event(.progress, id: id), candidate: item)
        consumer.consume(event(.played, id: id), candidate: item, automatic: true)
        consumer.discontinuity()
        consumer.consume(event(.progress, id: id, position: 80), candidate: item)
        consumer.consume(event(.paused, id: id, position: 81, phase: .paused), candidate: item)
        consumer.consume(event(.ended, id: id, position: 180, phase: .ended), candidate: item)
        await consumer.flush()
        let rows = await sink.recorded()
        XCTAssertEqual(rows.map(\.sequence), [1, 2, 3, 4, 5])
        XCTAssertEqual(rows.map(\.kind), [.progress, .playing, .progress, .paused, .ended])
        XCTAssertEqual(rows.map(\.hasPlayed), [false, true, true, true, true])
        XCTAssertEqual(rows[1].automatic, true)
        XCTAssertNil(rows[0].automatic)
        XCTAssertTrue(rows[2].discontinuity); XCTAssertFalse(rows[3].discontinuity)
        XCTAssertFalse(rows[3].running)
        XCTAssertTrue(rows.allSatisfy { $0.nativeAttemptID == id && $0.attemptID == item.attemptId && $0.observedAt == 10 })
    }

    func testInvalidationDropsQueuedOldObservationsAndResetsNativeBaseline() async {
        let sink = FeedbackFeatureSink(), consumer = MusicPlaybackFeedbackConsumer(service: sink)
        consumer.consume(event(.played, id: UUID()), candidate: candidate())
        consumer.invalidate() // No actor yield: old queued admission cannot run.
        let next = UUID()
        consumer.consume(event(.progress, id: next, resource: "music:next"), candidate: candidate("next"))
        await consumer.flush()
        let rows = await sink.recorded()
        XCTAssertEqual(rows.count, 1)
        XCTAssertEqual(rows.first?.attemptID, "next")
        XCTAssertEqual(rows.first?.sequence, 1)
        XCTAssertFalse(rows.first?.hasPlayed ?? true)
        XCTAssertTrue(rows.first?.discontinuity ?? false)
    }

    func testKGRequiresCurrentExplicitConfirmationAndCancelDoesNotWrite() async throws {
        let store = FeedbackSettingsStore(), model = MusicPlaybackFeedbackSettingsModel(service: store)
        model.setAccess(available: true, generation: UUID()); await model.load()
        model.update(.kugou, true)
        let cancelled = try XCTUnwrap(model.confirmation)
        XCTAssertNotEqual(model.value?.platforms[.kugou]?.enabled, true)
        var writes = await store.count(); XCTAssertEqual(writes, 0)
        model.dismissConfirmation(); model.confirm(cancelled); await model.flush()
        writes = await store.count(); XCTAssertEqual(writes, 0)
        model.update(.kugou, true)
        let confirmed = try XCTUnwrap(model.confirmation)
        model.confirm(confirmed); model.confirm(confirmed); await model.flush()
        writes = await store.count(); XCTAssertEqual(writes, 1)
        XCTAssertEqual(model.value?.platforms[.kugou], MusicPlaybackFeedbackSetting(enabled: true, generation: 1))
        model.update(.kugou, false); await model.flush()
        XCTAssertNil(model.confirmation)
        XCTAssertEqual(model.value?.platforms[.kugou]?.enabled, false)
    }

    func testConfirmationCannotSurviveRereadOtherSettingOrAccessEpochEvenForSameRevision() async throws {
        for change in ["reread", "generation", "lock", "disappear", "other-setting"] {
            let store = FeedbackSettingsStore(), model = MusicPlaybackFeedbackSettingsModel(service: store)
            let generation = UUID()
            model.setAccess(available: true, generation: generation); await model.load()
            model.update(.kugou, true)
            let old = try XCTUnwrap(model.confirmation)
            switch change {
            case "reread": await model.load()
            case "generation":
                model.setAccess(available: true, generation: UUID()); await model.load()
            case "lock":
                model.setAccess(available: false, generation: generation)
                model.setAccess(available: true, generation: generation); await model.load()
            case "disappear": model.cancel(); await model.load()
            default: model.update(.qq, true); await model.flush()
            }
            model.update(.kugou, true) // New dialog must not revive the old action.
            model.confirm(old); await model.flush()
            let writes = await store.count()
            XCTAssertEqual(writes, change == "other-setting" ? 1 : 0, change)
            XCTAssertNotEqual(model.value?.platforms[.kugou]?.enabled, true, change)
        }
    }

    func testCASOrUnknownResultInvalidatesConsentAndIsNeverAutomaticallyReplayed() async throws {
        for failure in [MusicServiceError.staleRevision, .storageConflict, .commitUnconfirmed] {
            let store = FeedbackSettingsStore(), model = MusicPlaybackFeedbackSettingsModel(service: store)
            model.setAccess(available: true, generation: UUID()); await model.load()
            await store.fail(failure)
            model.update(.kugou, true); let ticket = try XCTUnwrap(model.confirmation)
            model.confirm(ticket); await model.flush()
            XCTAssertNil(model.value); XCTAssertNil(model.confirmation); XCTAssertNotNil(model.error)
            await store.fail(nil); await model.load()
            model.confirm(ticket); await model.flush()
            let writes = await store.count(); XCTAssertEqual(writes, 1)
            XCTAssertNotEqual(model.value?.platforms[.kugou]?.enabled, true)
        }
    }

    func testAuthorizationKeepsPhysicalCancellationSlotAndHandlesEarlyCompletion() throws {
        let gate = MusicPlaybackFeedbackAuthorization(), id = UUID(), early = UUID()
        gate.remove(early)
        try gate.register(early) {}
        XCTAssertEqual(gate.inFlight, 0)
        try gate.register(id) {}
        gate.revoke()
        XCTAssertEqual(gate.inFlight, 1)
        XCTAssertThrowsError(try gate.checkLocal())
        gate.remove(id)
        XCTAssertEqual(gate.inFlight, 0)
        let racing = UUID()
        XCTAssertThrowsError(try gate.register(racing) {})
        XCTAssertEqual(gate.inFlight, 1)
        gate.remove(racing)
        XCTAssertEqual(gate.inFlight, 0)
    }
}
