#if os(iOS) || os(macOS)
import Foundation
import XCTest
import MeowContracts
import MeowMusicFeature
@testable import MeowPlayback

@MainActor
final class MusicMediaHandoffTests: XCTestCase {
    private func model(service: any MusicServing, player: MediaHandoffPlayer) async -> MusicViewModel {
        let model = MusicViewModel(service: service, player: player)
        player.deliver = { [weak model] in model?.onPlayback($0) }
        await model.load()
        return model
    }

    func testHandoffStopsAddressedMusicPreservesAcceptedFactsQueueAndExplicitResume() async throws {
        let service = MusicCandidateViewModelService(), player = MediaHandoffPlayer()
        let model = await model(service: service, player: player)
        await model.prepareCurrent()
        let attempt = try XCTUnwrap(player.current), queue = model.queue
        player.emit(.played, position: 42)
        // No drain before handoff: even the first playing/history write is
        // still queued when the real-shaped native exit enters the public port.
        model.stopForMediaHandoff()
        XCTAssertNil(player.current); XCTAssertNil(model.activeNativeAttemptID)
        XCTAssertFalse(model.acceptNext(attempt))
        XCTAssertFalse(model.acceptPlayPause(.play, attempt: attempt))
        XCTAssertEqual(model.queue, queue); XCTAssertEqual(model.position, 42)
        XCTAssertEqual(model.phase, .idle)
        await model.flushPlaybackFacts()
        let counts = await service.metrics()
        XCTAssertEqual(counts.facts.map(\.kind), [.playing, .stopped])
        XCTAssertEqual(counts.facts.map(\.position), [42, 42])
        XCTAssertEqual(counts.facts.last?.resourceId, attempt.resourceID)
        XCTAssertNil(model.error); XCTAssertEqual(model.phase, .idle)

        await model.togglePlayback()
        XCTAssertEqual(player.resources.count, 2)
        XCTAssertNotEqual(player.current?.id, attempt.id)
        XCTAssertEqual(player.current?.id, model.activeNativeAttemptID)
        XCTAssertEqual(model.queue, queue)
        await model.close()
    }

    func testHandoffRejectsLateCandidateDiscoveryWithoutBlockingExplicitPlay() async {
        let service = MusicCandidateViewModelService(), player = MediaHandoffPlayer()
        let model = await model(service: service, player: player)
        let began = expectation(description: "non-cooperative candidate resolution")
        let gate = MusicCandidateViewModelGate { began.fulfill() }
        await service.configure(resolve: gate)
        let old = Task { await model.togglePlayback() }
        await fulfillment(of: [began], timeout: 2)
        XCTAssertNil(model.activeNativeAttemptID)
        let queue = model.queue
        model.stopForMediaHandoff()
        let film = player.takeFilm()
        await gate.release(); await old.value
        XCTAssertEqual(player.current, film); XCTAssertTrue(player.resources.isEmpty)
        XCTAssertNil(model.activeNativeAttemptID); XCTAssertFalse(model.isResolving)
        XCTAssertEqual(model.queue, queue); XCTAssertNil(model.error)
        let counts = await service.metrics()
        XCTAssertFalse(counts.cancelled.isEmpty)

        await model.togglePlayback()
        XCTAssertEqual(player.resources.count, 1)
        XCTAssertEqual(player.current?.id, model.activeNativeAttemptID)
        await model.close()
    }

    func testHandoffRejectsLateSourceMatchWithoutBlockingExplicitPlay() async throws {
        try await checkPendingSource(mediaPreparation: false)
    }

    func testHandoffCancelsLatePreparedMediaWithoutCommittingOrBlockingExplicitPlay() async throws {
        try await checkPendingSource(mediaPreparation: true)
    }

    private func checkPendingSource(mediaPreparation: Bool) async throws {
        let service = MediaHandoffService(), player = MediaHandoffPlayer()
        let model = await model(service: service, player: player)
        let began = expectation(description: mediaPreparation ? "media preparation" : "source match")
        let gate = MusicCandidateViewModelGate { began.fulfill() }
        if mediaPreparation { player.preparationGate = gate }
        else { await service.holdSourceMatch(gate) }
        let old = Task { await model.switchQuality("128k") }
        await fulfillment(of: [began], timeout: 2)
        XCTAssertNil(model.activeNativeAttemptID)
        let queue = model.queue
        model.stopForMediaHandoff()
        let film = player.takeFilm()
        await gate.release(); await old.value
        XCTAssertEqual(player.current, film); XCTAssertTrue(player.resources.isEmpty)
        XCTAssertEqual(player.commits, 0)
        XCTAssertEqual(player.cancelledPreparations, mediaPreparation ? 1 : 0)
        XCTAssertNil(model.activeNativeAttemptID); XCTAssertFalse(model.isResolving)
        XCTAssertEqual(model.queue, queue); XCTAssertNil(model.error)
        let discarded = await service.discardedSources()
        XCTAssertEqual(discarded.count, 1)

        await model.togglePlayback()
        XCTAssertEqual(player.resources.count, 1)
        XCTAssertEqual(player.current?.id, model.activeNativeAttemptID)
        await model.close()
    }

    func testHandoffRejectsLateCommittedQueueSelection() async throws {
        try await checkCommittedAction(history: false, queueRefresh: false)
    }

    func testHandoffRejectsQueueRefreshAfterSelectionCommit() async throws {
        try await checkCommittedAction(history: false, queueRefresh: true)
    }

    func testHandoffRejectsLateCommittedHistoryRestore() async throws {
        try await checkCommittedAction(history: true, queueRefresh: false)
    }

    func testHandoffRejectsQueueRefreshAfterHistoryCommit() async throws {
        try await checkCommittedAction(history: true, queueRefresh: true)
    }

    private func checkCommittedAction(history: Bool, queueRefresh: Bool) async throws {
        let service = MediaHandoffService(), player = MediaHandoffPlayer()
        let model = await model(service: service, player: player)
        await model.prepareCurrent()
        player.emit(.played, position: 42)
        await model.flushPlaybackFacts()
        let began = expectation(description: queueRefresh ? "committed queue refresh" : "commit acknowledgement")
        let gate = MusicCandidateViewModelGate { began.fulfill() }
        await service.holdCommit(gate, queueRefresh: queueRefresh)
        let old = Task {
            if history { await model.restoreHistory(detail: MediaHandoffService.history) }
            else { await model.selectOccurrence("entry-2") }
        }
        await fulfillment(of: [began], timeout: 2)
        let committed = await service.state()
        XCTAssertEqual(committed.currentId, "entry-2")
        model.stopForMediaHandoff()
        let film = player.takeFilm(), state = model.state, queue = model.queue
        XCTAssertFalse(model.isChangingQueue); XCTAssertFalse(model.isNavigating)
        await gate.release(); await old.value; await model.flushPlaybackFacts()
        XCTAssertEqual(player.current, film); XCTAssertEqual(player.resources.count, 1)
        XCTAssertEqual(model.state, state); XCTAssertEqual(model.queue, queue)
        XCTAssertNil(model.activeNativeAttemptID); XCTAssertEqual(model.phase, .idle)
        XCTAssertNil(model.error); XCTAssertFalse(model.isChangingQueue)
        let persisted = await service.state()
        XCTAssertEqual(persisted, committed, "Handoff must not roll back an already committed queue")

        await model.selectOccurrence("entry-1")
        XCTAssertEqual(player.resources.count, 2)
        XCTAssertEqual(model.current?.id, "entry-1")
        XCTAssertEqual(player.current?.id, model.activeNativeAttemptID)
        XCTAssertFalse(model.isChangingQueue)
        await model.close()
    }

    func testHandoffRejectsLateHomeQueueOpenWithoutBlockingNewHomePlay() async throws {
        try await checkHomeAction(page: false)
    }

    func testHandoffRejectsLateHomeQueuePageWithoutBlockingNewHomePlay() async throws {
        try await checkHomeAction(page: true)
    }

    private func checkHomeAction(page: Bool) async throws {
        let service = MediaHandoffService(), player = MediaHandoffPlayer()
        let model = await model(service: service, player: player)
        let began = expectation(description: page ? "home page" : "home queue open")
        let gate = MusicCandidateViewModelGate { began.fulfill() }
        await service.holdHome(gate, page: page)
        let old = Task { () -> MusicServiceError? in
            do {
                try await model.playHomeAction(.daily(branch: .default), title: "Fixture")
                return nil
            } catch { return MusicServiceError.sanitized(error) }
        }
        await fulfillment(of: [began], timeout: 2)
        let state = model.state, queue = model.queue
        model.stopForMediaHandoff()
        let film = player.takeFilm()
        await gate.release()
        let failure = await old.value, oldCommits = await service.remoteCommitCount()
        XCTAssertEqual(failure, .cancelled); XCTAssertEqual(oldCommits, 0)
        XCTAssertEqual(player.current, film); XCTAssertTrue(player.resources.isEmpty)
        XCTAssertEqual(model.state, state); XCTAssertEqual(model.queue, queue)
        XCTAssertFalse(model.isPreparingRemoteQueue); XCTAssertNil(model.error)

        try await model.playHomeAction(.daily(branch: .default), title: "Fixture")
        let newCommits = await service.remoteCommitCount()
        XCTAssertEqual(newCommits, 1); XCTAssertEqual(player.resources.count, 1)
        XCTAssertEqual(player.current?.id, model.activeNativeAttemptID)
        XCTAssertFalse(model.isPreparingRemoteQueue); XCTAssertFalse(model.isChangingQueue)
        await model.close()
    }
}

/// Addressed native events, with no decoder, network, audio session or timer.
/// Preparation deliberately ignores cancellation until its test-owned gate opens.
@MainActor
private final class MediaHandoffPlayer: MusicPlaybackHandoffDriving {
    var current: PlayAttempt?
    var resources: [PlaybackResource] = []
    var deliver: ((PlaybackEvent) -> Void)?
    var preparationGate: MusicCandidateViewModelGate?
    var commits = 0, cancelledPreparations = 0
    private var position = 0.0

    func play(_ resource: PlaybackResource) -> MusicPlayerAttempt {
        let next = PlayAttempt(id: UUID(), owner: "music", resourceID: resource.id)
        current = next; position = resource.startPosition
        resources.append(resource)
        emit(.preparing, position: position)
        return .init(id: next.id, resourceID: next.resourceID)
    }
    func emit(_ kind: PlaybackEvent.Kind, position: Double) {
        guard let current, current.owner == "music" else { return }
        self.position = position
        deliver?(.init(attempt: current, kind: kind,
            snapshot: .init(owner: "music", resourceID: current.resourceID,
                phase: kind == .played ? .playing : .preparing, position: position, duration: 180),
            error: nil))
    }
    func stop(_ attempt: MusicPlayerAttempt) {
        guard let old = current, old.owner == "music", old.id == attempt.id,
              old.resourceID == attempt.resourceID else { return }
        current = nil
        deliver?(.init(attempt: old, kind: .stopped,
            snapshot: .init(owner: "music", resourceID: old.resourceID,
                phase: .idle, position: position, duration: 180), error: nil))
    }
    func takeFilm() -> PlayAttempt {
        let film = PlayAttempt(id: UUID(), owner: "film", resourceID: "film:synthetic")
        current = film
        return film
    }
    func pause(_ attempt: MusicPlayerAttempt) {}
    func resume(_ attempt: MusicPlayerAttempt) {}
    func seek(_ position: Double, attempt: MusicPlayerAttempt) -> Bool { false }
    func prepareReplacement(_ resource: PlaybackResource, replacing: MusicPlayerAttempt) async throws -> MusicPreparedPlayback {
        try await preparePlayback(resource, autoplay: true)
    }
    func preparePlayback(_ resource: PlaybackResource, autoplay: Bool) async throws -> MusicPreparedPlayback {
        let prepared = MusicPreparedPlayback(duration: 180, synchronize: {}, commit: {
            self.commits += 1
            return self.play(resource)
        }, cancel: { self.cancelledPreparations += 1 })
        let gate = preparationGate; preparationGate = nil
        if let gate { await gate.wait() }
        return prepared
    }
}

/// Reuses the existing synthetic queue/resolution service. Only the source,
/// history and home ports under test add non-cooperative suspension points.
private actor MediaHandoffService: MusicServing, MusicSourcePlaybackServing, MusicHistoryManaging,
    MusicHomeActionQueueServing, MusicRemoteQueueServing {
    private let base = MusicCandidateViewModelService()
    private var matchGate: MusicCandidateViewModelGate?
    private var acknowledgementGate: MusicCandidateViewModelGate?
    private var refreshAfterCommit: MusicCandidateViewModelGate?
    private var queueGate: MusicCandidateViewModelGate?
    private var openGate: MusicCandidateViewModelGate?
    private var pageGate: MusicCandidateViewModelGate?
    private var discarded: [String] = []
    private var remoteCommits = 0

    static var history: MusicHistoryDetail {
        .init(summary: .init(id: "handoff-history", title: "Fixture", count: 1, at: 0),
            entries: [.init(id: "archived", index: 0, title: "Synthetic", artist: "Fixture",
                album: "", artwork: nil, duration: 180)], selectedIndex: 0, sourceLabel: nil,
            actionToken: .init(rawValue: "synthetic-history-action"))
    }
    func holdSourceMatch(_ gate: MusicCandidateViewModelGate) { matchGate = gate }
    func holdCommit(_ gate: MusicCandidateViewModelGate, queueRefresh: Bool) {
        if queueRefresh { refreshAfterCommit = gate }
        else { acknowledgementGate = gate }
    }
    func holdHome(_ gate: MusicCandidateViewModelGate, page: Bool) {
        if page { pageGate = gate } else { openGate = gate }
    }
    func discardedSources() -> [String] { discarded }
    func remoteCommitCount() -> Int { remoteCommits }
    private func acknowledge(_ next: MusicPlaybackState) async -> MusicPlaybackState {
        queueGate = refreshAfterCommit; refreshAfterCommit = nil
        let gate = acknowledgementGate; acknowledgementGate = nil
        if let gate { await gate.wait() }
        return next
    }
    func state() async -> MusicPlaybackState { await base.state() }
    func queue() async -> MusicQueueSnapshot {
        let snapshot = await base.queue(), gate = queueGate; queueGate = nil
        if let gate { await gate.wait() }
        return snapshot
    }
    func changeQueue(_ change: MusicQueueChange, expectedRevision: Int) async throws -> MusicPlaybackState {
        let next = try await base.changeQueue(change, expectedRevision: expectedRevision)
        return await acknowledge(next)
    }
    func restoreHistory(detail: MusicHistoryDetail, startIndex: Int?, expectedRevision: Int) async throws -> MusicPlaybackState {
        let next = try await base.changeQueue(.select("entry-2"), expectedRevision: expectedRevision)
        return await acknowledge(next)
    }
    func historyDetail(id: String) -> MusicHistoryDetail { Self.history }
    func removeHistory(detail: MusicHistoryDetail) throws { throw MusicServiceError.unsupported }
    func clearHistory(_ category: MusicHistoryCategory) throws { throw MusicServiceError.unsupported }

    func sourceSession(occurrenceID: String, expectedQueueRevision: Int) async throws -> any MusicSourcePlaybackSession {
        let state = await base.state()
        guard state.currentId == occurrenceID, state.revision == expectedQueueRevision else { throw MusicServiceError.staleRevision }
        return Session(owner: self, occurrenceID: occurrenceID)
    }
    private func match(_ intent: MusicSourceIntent, occurrenceID: String) async throws -> MusicPreparedSource {
        let selection: MusicPlaybackSelection
        if case .exact(let exact) = intent { selection = exact }
        else { selection = MusicCandidateViewModelService.option().playback.selection(quality: "128k") }
        let candidate = try await base.resolve(attemptId: UUID().uuidString.lowercased(),
            occurrenceId: occurrenceID, selection: selection)
        let gate = matchGate; matchGate = nil
        if let gate { await gate.wait() }
        return .init(candidate: candidate, selection: selection)
    }
    func activateSource(attemptID: String) {}
    func discardSource(attemptID: String) async { discarded.append(attemptID); await base.cancelExact(attemptID) }
    func sourceConfigurationChanged() {}
    func inspectSources(for reference: MusicReference) throws -> any MusicSourceInspectionSession { throw MusicServiceError.unsupported }

    func openHomeActionQueue(_ action: MusicHomeAction, title: String) async -> MusicRemoteQueueSource {
        let gate = openGate; openGate = nil
        if let gate { await gate.wait() }
        return .init(context: .init(kind: .recommendation, id: "handoff-home", title: title, platform: "tx"),
            loadPage: { _ in .init(items: []) })
    }
    func remoteQueuePage(_ source: MusicRemoteQueueSource, cursor: String?) async -> MusicBrowsePage {
        let gate = pageGate; pageGate = nil
        if let gate { await gate.wait() }
        return .init(items: [MusicCandidateViewModelService.track("entry-2")], cursor: nil)
    }
    func replaceRemoteQueue(_ source: MusicRemoteQueueSource, start: MusicRemoteQueueStart,
                            expectedRevision: Int, intent: MusicQueueAuthorization) async throws -> MusicPlaybackState {
        try source.authorization.check(); try intent.check()
        let next = try await base.changeQueue(.select("entry-2"), expectedRevision: expectedRevision)
        remoteCommits += 1
        return next
    }
    func openAccountQueue(platform: MusicAccountPlatform, kind: MusicAccountQueueKind,
                          title: String) throws -> MusicRemoteQueueSource { throw MusicServiceError.unsupported }
    func advanceFM(_ source: MusicRemoteQueueSource, direction: MusicDirection, reason: MusicAdvanceReason,
                   expectedRevision: Int, intent: MusicQueueAuthorization) throws -> MusicAdvanceResult { throw MusicServiceError.unsupported }

    func sourceAvailability(for ref: MusicReference) async -> MusicSourceAvailability { await base.sourceAvailability(for: ref) }
    func resolve(attemptId: String, occurrenceId: String, selection: MusicPlaybackSelection) async throws -> MusicPlaybackCandidate {
        try await base.resolve(attemptId: attemptId, occurrenceId: occurrenceId, selection: selection)
    }
    func cancel(attemptId: String?) async throws { try await base.cancel(attemptId: attemptId) }
    func record(_ fact: MusicPlaybackFact) async throws -> Bool { try await base.record(fact) }
    func advance(_ direction: MusicDirection, reason: MusicAdvanceReason, expectedRevision: Int) async throws -> MusicAdvanceResult {
        try await base.advance(direction, reason: reason, expectedRevision: expectedRevision)
    }
    func updateSettings(_ change: MusicSettingsChange) async throws -> MusicPlaybackState { try await base.updateSettings(change) }
    func lyrics(occurrenceId: String, sourceKey: String) async -> MusicLyricsSnapshot { await base.lyrics(occurrenceId: occurrenceId, sourceKey: sourceKey) }
    func platforms() async -> [MusicPlatform] { await base.platforms() }
    func collections() async -> [MusicCollection] { await base.collections() }
    func playedSongs() async -> [MusicPlayedSong] { await base.playedSongs() }
    func playedPlaylists() async -> [MusicPlayedPlaylist] { await base.playedPlaylists() }
    func tracks(in collection: MusicCollection) async -> [MusicRemoteTrack] { await base.tracks(in: collection) }
    func search(platform: String, query: String, page: Int, limit: Int) throws -> MusicSearchPage { throw MusicServiceError.unsupported }
    func createPlaylist(name: String, refs: [MusicReference]) throws -> MusicCollection { throw MusicServiceError.unsupported }
    func editPlaylist(_ collection: MusicCollection, name: String?, refs: [MusicReference]?, order: [String]?) throws -> MusicCollection { throw MusicServiceError.unsupported }
    func removePlaylist(_ collection: MusicCollection) throws { throw MusicServiceError.unsupported }
    func restoreHistory(id: String, expectedRevision: Int) throws -> MusicPlaybackState { throw MusicServiceError.unsupported }
    func metadataPreferences() throws -> MusicMetadataPreferences { throw MusicServiceError.unsupported }
    func updateMetadataPreferences(_ change: MusicMetadataPreferencesChange) throws -> MusicMetadataPreferences { throw MusicServiceError.unsupported }
    func metadataCandidates(occurrenceID: String) throws -> MusicMetadataCandidates { throw MusicServiceError.unsupported }

    private struct Session: MusicSourcePlaybackSession {
        let owner: MediaHandoffService, occurrenceID: String
        func match(intent: MusicSourceIntent) async throws -> MusicPreparedSource { try await owner.match(intent, occurrenceID: occurrenceID) }
        func match(quality: String?, sourceID: String?) async throws -> MusicPreparedSource {
            try await match(intent: .quality(quality ?? "128k"))
        }
        func beginSelection() async {}
        func rejectMedia(candidate: MusicPlaybackCandidate, selection: MusicPlaybackSelection) async {}
        func cancelInspection() async {}
        func updates() async -> AsyncThrowingStream<MusicResolvedSourcesSnapshot, Error> { .init { $0.finish() } }
        func loadMore(refresh: Bool) async throws -> MusicResolvedSourcesSnapshot { throw MusicServiceError.unsupported }
        func adopt(sourceID: String, occurrenceID: String, expectedQueueRevision: Int) async throws -> MusicPlaybackSelection { throw MusicServiceError.unsupported }
        func reject(sourceKey: String, occurrenceID: String, expectedQueueRevision: Int) async throws { throw MusicServiceError.unsupported }
        func close() async {}
    }
}
#endif
