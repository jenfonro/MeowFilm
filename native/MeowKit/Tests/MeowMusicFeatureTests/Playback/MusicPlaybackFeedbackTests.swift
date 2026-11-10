import Foundation
import Combine
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

private actor FeedbackSettingsGate {
    private var began = false, released = false
    private var starts: [CheckedContinuation<Void, Never>] = []
    private var waits: [CheckedContinuation<Void, Never>] = []
    func wait() async {
        began = true; starts.forEach { $0.resume() }; starts.removeAll()
        if !released { await withCheckedContinuation { waits.append($0) } }
    }
    func started() async {
        if !began { await withCheckedContinuation { starts.append($0) } }
    }
    func release() {
        released = true; waits.forEach { $0.resume() }; waits.removeAll()
    }
}

private actor FeedbackSettingsStore: MusicPlaybackFeedbackSettingsServing {
    private var value = MusicPlaybackFeedbackSettings(revision: 0, platforms: [:])
    private var reads = 0, writes = 0
    private var failure: MusicServiceError?
    private var writeGate: FeedbackSettingsGate?
    private var readGate: FeedbackSettingsGate?
    private var cancelledReturns = 0
    func fail(_ error: MusicServiceError?) { failure = error }
    func blockWrite(_ gate: FeedbackSettingsGate) { writeGate = gate }
    func blockRead(_ gate: FeedbackSettingsGate) { readGate = gate }
    func replace(_ value: MusicPlaybackFeedbackSettings) { self.value = value }
    func playbackFeedbackSettings() async -> MusicPlaybackFeedbackSettings {
        reads += 1
        let result = value, gate = readGate
        readGate = nil
        if let gate { await gate.wait() }
        if Task.isCancelled { cancelledReturns += 1 }
        return result
    }
    func setPlaybackFeedback(platform: MusicAccountPlatform, enabled: Bool,
                             expectedRevision: Int) async throws -> MusicPlaybackFeedbackSettings {
        writes += 1
        if let failure, failure != .commitUnconfirmed { throw failure }
        guard expectedRevision == value.revision else { throw MusicServiceError.staleRevision }
        var platforms = value.platforms
        platforms[platform] = .init(enabled: enabled, generation: (platforms[platform]?.generation ?? 0) + 1)
        value = .init(revision: value.revision + 1, platforms: platforms)
        let result = value
        if let writeGate { await writeGate.wait() }
        if Task.isCancelled { cancelledReturns += 1 }
        if let failure { throw failure }
        return result
    }
    func count() -> Int { writes }
    func readCount() -> Int { reads }
    func cancelledCount() -> Int { cancelledReturns }
}

@MainActor final class MusicPlaybackFeedbackTests: XCTestCase {
    private func candidate(_ id: String = "observed") -> MusicPlaybackCandidate {
        .init(attemptId: id, occurrenceId: "entry-1", resourceId: "music:" + id,
              url: URL(string: "https://media.invalid/synthetic.mp3")!, title: "Synthetic", requestedQuality: "128k",
              resolvedSelection: .init(installationId: "official:fixture", generation: 1,
                  siteId: "tx", sourceKey: "source-a", quality: "128k"))
    }
    private func event(_ kind: PlaybackEvent.Kind, id: UUID, owner: String = "music",
                       resource: String = "music:observed", position: Double = 0,
                       phase: PlaybackPhase = .playing, wantsPlayback: Bool = true) -> PlaybackEvent {
        .init(attempt: .init(id: id, owner: owner, resourceID: resource), kind: kind,
              snapshot: .init(owner: owner, resourceID: resource, phase: phase, position: position, duration: 180),
              wantsPlayback: wantsPlayback, error: nil)
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
        consumer.consume(event(.paused, id: id, position: 81, phase: .paused, wantsPlayback: false), candidate: item)
        consumer.consume(event(.ended, id: id, position: 180, phase: .ended, wantsPlayback: false), candidate: item)
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
            XCTAssertEqual(model.value?.revision, failure == .commitUnconfirmed ? 1 : 0)
            XCTAssertNil(model.confirmation); XCTAssertEqual(model.error, failure.localizedDescription)
            let reads = await store.readCount(); XCTAssertEqual(reads, 2)
            await store.fail(nil); await model.load()
            model.confirm(ticket); await model.flush()
            let writes = await store.count(); XCTAssertEqual(writes, 1)
            XCTAssertEqual(model.value?.platforms[.kugou]?.enabled == true, failure == .commitUnconfirmed)
        }
    }

    func testCompletedWriteReconcilesOwnerAfterPresentationCancellationButNotNewAccess() async {
        for changedAccess in [false, true] {
            let store = FeedbackSettingsStore(), gate = FeedbackSettingsGate(), generation = UUID()
            var reconciliations: [UUID?] = []
            let model = MusicPlaybackFeedbackSettingsModel(service: store, onChanged: {
                reconciliations.append($0)
            })
            model.setAccess(available: true, generation: generation); await model.load()
            reconciliations = []
            await store.blockWrite(gate)
            await store.fail(.commitUnconfirmed)
            model.update(.qq, true); await gate.started()
            if changedAccess { model.setAccess(available: false, generation: UUID()) }
            else { model.cancel() }
            await gate.release(); await model.flush()
            XCTAssertEqual(reconciliations, changedAccess ? [] : [generation])
            XCTAssertNil(model.value); XCTAssertNil(model.error); XCTAssertFalse(model.busy)
            let writes = await store.count(); XCTAssertEqual(writes, 1)
            let reads = await store.readCount(); XCTAssertEqual(reads, changedAccess ? 1 : 2)
        }
    }

    func testSharedSettingsChangesCoalesceBehindLoadAndCommitWithoutCancellingOrReplaying() async {
        for reading in [true, false] {
            let store = FeedbackSettingsStore(), gate = FeedbackSettingsGate()
            let model = MusicPlaybackFeedbackSettingsModel(service: store)
            model.setAccess(available: true, generation: UUID()); await model.load()
            var published: [Int] = []
            let observation = model.$value.compactMap { $0?.revision }.sink { published.append($0) }
            defer { observation.cancel() }
            var load: Task<Void, Never>?
            if reading {
                await store.blockRead(gate)
                load = Task { await model.load() }
            } else {
                await store.blockWrite(gate)
                model.update(.qq, true)
            }
            await gate.started()
            let latest = MusicPlaybackFeedbackSettings(revision: 2, platforms: [
                .qq: .init(enabled: true, generation: 1), .kugou: .init(enabled: true, generation: 1),
            ])
            await store.replace(latest)
            model.settingsChanged(revision: 1)
            model.settingsChanged(revision: 2)
            XCTAssertTrue(model.busy)
            let duringReads = await store.readCount(); XCTAssertEqual(duringReads, reading ? 2 : 1)
            let duringWrites = await store.count(); XCTAssertEqual(duringWrites, reading ? 0 : 1)
            await gate.release(); await load?.value; await model.flush()
            XCTAssertEqual(model.value, latest)
            XCTAssertEqual(published, [0, 2])
            XCTAssertFalse(model.busy); XCTAssertNil(model.error)
            model.settingsChanged(revision: 2); await model.flush()
            let reads = await store.readCount(); XCTAssertEqual(reads, reading ? 3 : 2)
            let writes = await store.count(); XCTAssertEqual(writes, reading ? 0 : 1)
            let cancelled = await store.cancelledCount(); XCTAssertEqual(cancelled, 0)
        }
    }

    func testInsufficientSharedRevisionStopsReadingUntilExplicitRetry() async {
        let store = FeedbackSettingsStore(), model = MusicPlaybackFeedbackSettingsModel(service: store)
        model.setAccess(available: true, generation: UUID()); await model.load()
        model.settingsChanged(revision: 2); await model.flush()
        XCTAssertNil(model.value)
        XCTAssertEqual(model.error, MusicServiceError.staleRevision.localizedDescription)
        XCTAssertFalse(model.busy)
        let stopped = await store.readCount(); XCTAssertEqual(stopped, 2)
        model.settingsChanged(revision: 2); model.settingsChanged(revision: 1)
        await model.flush()
        let unchanged = await store.readCount(); XCTAssertEqual(unchanged, stopped)
        let latest = MusicPlaybackFeedbackSettings(revision: 2, platforms: [:])
        await store.replace(latest); await model.load(); await model.flush()
        XCTAssertEqual(model.value, latest); XCTAssertNil(model.error)
        XCTAssertFalse(model.busy)
        let retried = await store.readCount(); XCTAssertEqual(retried, stopped + 1)
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
