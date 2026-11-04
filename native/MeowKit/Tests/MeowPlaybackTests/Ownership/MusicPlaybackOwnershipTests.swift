#if os(iOS) || os(macOS)
import Foundation
import XCTest
import MeowContracts
@testable import MeowPlayback
import MeowMusicFeature

private let crossDomainSelection = MusicPlaybackSelection(
    installationId: "synthetic-installation", generation: 1, siteId: "wy",
    sourceKey: "synthetic-source", quality: "128k"
)

/// Deliberately ignores cancellation; only the test can settle the dependency.
private actor CrossDomainNavigationGate {
    private let began: @Sendable () -> Void
    private var released = false
    private var waiter: CheckedContinuation<Void, Never>?
    init(_ began: @escaping @Sendable () -> Void) { self.began = began }
    func wait() async {
        began()
        if !released { await withCheckedContinuation { waiter = $0 } }
    }
    func release() {
        released = true
        let old = waiter; waiter = nil; old?.resume()
    }
}

/// Public business-port fixture; no native service tables, media I/O or accounts.
private actor CrossDomainNavigationService: MusicServing {
    private let entries: [MusicQueueEntry] = (1...3).map { index in
        let id = "entry-\(index)"
        return .init(id: id, track: .init(
            ref: .init(kind: .queue, id: id), songId: "song-\(index)",
            title: "Synthetic \(index)", artist: "Fixture", duration: 180,
            sources: [.init(sourceKey: "synthetic-source", platform: "wy", nativeId: "song-\(index)")]
        ))
    }
    private var current = "entry-1", revision = 1
    private var nextAdvance: (CrossDomainNavigationGate, MusicServiceError?)?
    private var endedGate: CrossDomainNavigationGate?
    private var nextChangeFailure: MusicServiceError?
    private var advances = 0
    private var facts: [MusicPlaybackFact] = []

    func holdAdvance(_ gate: CrossDomainNavigationGate, failure: MusicServiceError? = nil) {
        nextAdvance = (gate, failure)
    }
    func holdEnded(_ gate: CrossDomainNavigationGate) { endedGate = gate }
    func failNextChange(_ error: MusicServiceError) { nextChangeFailure = error }
    func advanceCount() -> Int { advances }
    func recorded() -> [MusicPlaybackFact] { facts }
    func state() -> MusicPlaybackState {
        .init(revision: revision, currentId: current, total: entries.count,
              position: 17, canNext: true, canPrevious: true)
    }
    func queue() -> MusicQueueSnapshot { .init(revision: revision, currentId: current, entries: entries) }
    func changeQueue(_ change: MusicQueueChange, expectedRevision: Int) throws -> MusicPlaybackState {
        guard revision == expectedRevision else { throw MusicServiceError.staleRevision }
        if let failure = nextChangeFailure { nextChangeFailure = nil; throw failure }
        guard case .select(let id) = change, entries.contains(where: { $0.id == id }) else {
            throw MusicServiceError.unsupported
        }
        current = id; revision += 1
        return state()
    }
    func advance(_ direction: MusicDirection, reason: MusicAdvanceReason,
                 expectedRevision: Int) async throws -> MusicAdvanceResult {
        guard revision == expectedRevision else { throw MusicServiceError.staleRevision }
        advances += 1
        let index = entries.firstIndex { $0.id == current } ?? 0
        current = entries[(index + (direction == .next ? 1 : entries.count - 1)) % entries.count].id
        revision += 1
        // Simulate a committed queue before holding its acknowledgement.
        let result = MusicAdvanceResult(state: state(), selectedId: current)
        let held = nextAdvance; nextAdvance = nil
        if let held {
            await held.0.wait()
            if let failure = held.1 { throw failure }
        }
        return result
    }
    func playbackOptions(for ref: MusicReference) -> [MusicPlaybackOption] {
        [.init(installationId: crossDomainSelection.installationId, generation: 1, siteId: "wy",
               sourceKey: "synthetic-source", qualities: ["128k"], title: "Synthetic",
               version: "1", contentHash: String(repeating: "a", count: 64))]
    }
    func resolve(attemptId: String, occurrenceId: String,
                 selection: MusicPlaybackSelection) -> MusicPlaybackCandidate {
        .init(attemptId: attemptId, occurrenceId: occurrenceId, resourceId: "music:" + attemptId,
              url: URL(string: "https://media.invalid/synthetic.mp3")!,
              title: "Synthetic", requestedQuality: selection.quality)
    }
    func record(_ fact: MusicPlaybackFact) async -> Bool {
        facts.append(fact)
        if fact.kind == .ended, let gate = endedGate {
            endedGate = nil
            await gate.wait()
        }
        return true
    }
    func cancel(attemptId: String?) {}
    func lyrics(occurrenceId: String, sourceKey: String) -> MusicLyricsSnapshot {
        .init(occurrenceId: occurrenceId, state: .none, lines: [])
    }
    func platforms() -> [MusicPlatform] { [] }
    func collections() -> [MusicCollection] { [] }
    func tracks(in collection: MusicCollection) -> [MusicRemoteTrack] { [] }
    func playedSongs() -> [MusicPlayedSong] { [] }
    func playedPlaylists() -> [MusicPlayedPlaylist] { [] }
    func search(platform: String, query: String, page: Int, limit: Int) throws -> MusicSearchPage { throw MusicServiceError.unsupported }
    func createPlaylist(name: String, refs: [MusicReference]) throws -> MusicCollection { throw MusicServiceError.unsupported }
    func editPlaylist(_ collection: MusicCollection, name: String?, refs: [MusicReference]?,
                      order: [String]?) throws -> MusicCollection { throw MusicServiceError.unsupported }
    func removePlaylist(_ collection: MusicCollection) throws { throw MusicServiceError.unsupported }
    func updateSettings(_ change: MusicSettingsChange) throws -> MusicPlaybackState { throw MusicServiceError.unsupported }
    func restoreHistory(id: String, expectedRevision: Int) throws -> MusicPlaybackState { throw MusicServiceError.unsupported }
    func prepareImport(data: Data, sourceId: String, ownerId: String) throws -> MusicImportPreview { throw MusicServiceError.unsupported }
    func commitImport(_ preview: MusicImportPreview) throws -> MusicImportReport { throw MusicServiceError.unsupported }
    func releaseImport(_ preview: MusicImportPreview) {}
    func rollbackImport(batchId: String) throws { throw MusicServiceError.unsupported }
}

/// This suite belongs to the playback owner, which alone creates addressed
/// attempts/events. The music consumer is exercised only through its public port.
/// It never constructs AVPlayer or performs media I/O.
@MainActor private final class CrossDomainNavigationPlayer: MusicPlaybackDriving {
    private(set) var current: PlayAttempt?
    private(set) var musicPlays: [String] = []
    private(set) var stopRequests: [MusicPlayerAttempt] = []
    var deliver: ((PlaybackEvent) -> Void)?
    private var phase: PlaybackPhase = .idle
    private var position = 17.0

    private func event(_ attempt: PlayAttempt, kind: PlaybackEvent.Kind,
                       phase: PlaybackPhase, position: Double) -> PlaybackEvent {
        .init(attempt: attempt, kind: kind,
              snapshot: .init(owner: attempt.owner, resourceID: attempt.resourceID,
                              phase: phase, position: position, duration: 180), error: nil)
    }
    func play(_ resource: PlaybackResource) -> MusicPlayerAttempt {
        let old = current, oldPhase = phase, oldPosition = position
        let next = PlayAttempt(id: UUID(), owner: "music", resourceID: resource.id)
        current = next; phase = .preparing; position = 17
        musicPlays.append(resource.id)
        if let old, old.owner == "music" {
            deliver?(event(old, kind: .replaced, phase: oldPhase, position: oldPosition))
        }
        deliver?(event(next, kind: .preparing, phase: .preparing, position: position))
        return .init(id: next.id, resourceID: next.resourceID)
    }
    func takeFilm() -> PlayAttempt {
        let old = current, oldPhase = phase, oldPosition = position
        let film = PlayAttempt(id: UUID(), owner: "film", resourceID: "film:synthetic")
        current = film; phase = .playing; position = 0
        if let old, old.owner == "music" {
            deliver?(event(old, kind: .replaced, phase: oldPhase, position: oldPosition))
        }
        return film
    }
    func emit(_ kind: PlaybackEvent.Kind, position: Double) {
        guard let current, current.owner == "music" else { return }
        self.position = position
        if kind == .played { phase = .playing }
        if kind == .ended { phase = .ended }
        deliver?(event(current, kind: kind, phase: phase, position: position))
    }
    func stop(_ attempt: MusicPlayerAttempt) {
        stopRequests.append(attempt)
        guard let old = current, old.owner == "music", old.id == attempt.id,
              old.resourceID == attempt.resourceID else { return }
        current = nil
        let exit = event(old, kind: .stopped, phase: phase, position: position)
        phase = .idle; deliver?(exit)
    }
    func pause(_ attempt: MusicPlayerAttempt) {}
    func resume(_ attempt: MusicPlayerAttempt) {}
    func seek(_ position: Double, attempt: MusicPlayerAttempt) -> Bool { false }
}

@MainActor final class MusicPlaybackOwnershipTests: XCTestCase {
    private func setup() async -> (CrossDomainNavigationService, CrossDomainNavigationPlayer, MusicViewModel) {
        let service = CrossDomainNavigationService(), player = CrossDomainNavigationPlayer()
        let model = MusicViewModel(service: service, player: player)
        player.deliver = { [weak model] event in model?.onPlayback(event) }
        await model.load(); await model.choose(crossDomainSelection)
        player.emit(.played, position: 42)
        await model.flushPlaybackFacts()
        return (service, player, model)
    }
    private func holdNext(_ service: CrossDomainNavigationService, model: MusicViewModel,
                          attempt: PlayAttempt, failure: MusicServiceError? = nil) async
        -> (CrossDomainNavigationGate, Task<Void, Never>) {
        let began = expectation(description: "advance committed and suspended")
        let draining = expectation(description: "captured original navigation task")
        let gate = CrossDomainNavigationGate { began.fulfill() }
        await service.holdAdvance(gate, failure: failure)
        XCTAssertTrue(model.acceptNext(attempt))
        let drain = Task { @MainActor in
            draining.fulfill()
            await model.flushNavigation()
        }
        await fulfillment(of: [began, draining], timeout: 2)
        return (gate, drain)
    }

    func testFilmTakeoverRejectsLateNextSuccessButPreservesCommittedQueueAndExitProgress() async throws {
        let (service, player, model) = await setup()
        let old = try XCTUnwrap(player.current)
        let (gate, drain) = await holdNext(service, model: model, attempt: old)
        defer { Task { await gate.release() } }
        let film = player.takeFilm()
        await model.flushPlaybackFacts()
        let before = model.state
        let committed = await service.state()
        XCTAssertEqual(committed.currentId, "entry-2")
        XCTAssertFalse(model.isNavigating); XCTAssertFalse(model.isChangingQueue)
        XCTAssertNil(model.activeNativeAttemptID); XCTAssertFalse(model.acceptNext(old))
        await service.failNextChange(.storageFailed)
        await model.selectOccurrence("entry-3")
        let actionError = try XCTUnwrap(model.error)
        XCTAssertNotNil(actionError.range(
            of: "^网易云音乐 · 更新播放队列：保存失败（诊断 [A-F0-9]{8}）$", options: .regularExpression))

        await gate.release(); await drain.value
        XCTAssertEqual(player.current, film); XCTAssertEqual(player.musicPlays.count, 1)
        XCTAssertTrue(player.stopRequests.isEmpty)
        XCTAssertEqual(model.state, before); XCTAssertEqual(model.phase, .idle)
        XCTAssertEqual(model.error, actionError, "Late navigation cannot replace the newer error or its diagnostic ID")
        let after = await service.state(), facts = await service.recorded()
        XCTAssertEqual(after, committed)
        XCTAssertEqual(facts.map(\.kind), [.playing, .stopped])
        XCTAssertEqual(facts.last?.position, 42); XCTAssertEqual(facts.last?.resourceId, old.resourceID)

        await model.selectOccurrence("entry-3")
        XCTAssertEqual(model.state.currentId, "entry-3")
        XCTAssertEqual(player.musicPlays.count, 2); XCTAssertEqual(player.current?.owner, "music")
        XCTAssertEqual(player.current?.id, model.activeNativeAttemptID)
        await model.close()
    }

    func testLateNextErrorCannotClearNewExplicitPlaybackNavigationOrError() async throws {
        let (service, player, model) = await setup()
        let (oldGate, oldDrain) = await holdNext(service, model: model,
            attempt: try XCTUnwrap(player.current), failure: .invalidResponse)
        defer { Task { await oldGate.release() } }
        _ = player.takeFilm()
        await model.flushPlaybackFacts()
        await model.selectOccurrence("entry-3")
        let current = try XCTUnwrap(player.current)
        XCTAssertEqual(player.musicPlays.count, 2)
        let (newGate, newDrain) = await holdNext(service, model: model, attempt: current)
        defer { Task { await newGate.release() } }
        // A newer explicit queue action reports busy while the newer next is held.
        await model.selectOccurrence("entry-2")
        let state = model.state, resource = model.activeResourceID, phase = model.phase
        let actionError = try XCTUnwrap(model.error)
        XCTAssertNotNil(actionError.range(
            of: "^更新播放队列：音乐服务忙（诊断 [A-F0-9]{8}）$", options: .regularExpression))

        await oldGate.release(); await oldDrain.value
        XCTAssertEqual(player.current, current); XCTAssertEqual(player.musicPlays.count, 2)
        XCTAssertEqual(model.state, state); XCTAssertEqual(model.activeResourceID, resource)
        XCTAssertEqual(model.phase, phase); XCTAssertTrue(model.isNavigating); XCTAssertTrue(model.isChangingQueue)
        XCTAssertEqual(model.error, actionError, "Late navigation cannot replace the newer error or its diagnostic ID")

        await newGate.release(); await newDrain.value
        XCTAssertEqual(player.musicPlays.count, 3); XCTAssertEqual(player.current?.owner, "music")
        XCTAssertFalse(model.isNavigating); XCTAssertFalse(model.isChangingQueue)
        let moves = await service.advanceCount(); XCTAssertEqual(moves, 2)
        await model.close()
    }

    func testStoppedAttemptRetiresHeldNextWithoutRollingBackQueueOrRestarting() async throws {
        let (service, player, model) = await setup()
        let old = try XCTUnwrap(player.current)
        let (gate, drain) = await holdNext(service, model: model, attempt: old)
        defer { Task { await gate.release() } }
        player.stop(.init(id: old.id, resourceID: old.resourceID))
        await model.flushPlaybackFacts()
        await gate.release(); await drain.value
        XCTAssertNil(player.current); XCTAssertEqual(player.musicPlays.count, 1)
        XCTAssertFalse(model.isNavigating); XCTAssertFalse(model.isChangingQueue)
        let committed = await service.state(), facts = await service.recorded()
        XCTAssertEqual(committed.currentId, "entry-2")
        XCTAssertEqual(facts.last?.kind, .stopped); XCTAssertEqual(facts.last?.position, 42)
        await model.selectOccurrence("entry-3")
        XCTAssertEqual(player.musicPlays.count, 2)
        await model.close()
    }

    func testLateEndedAcknowledgementKeepsHistoryButCannotEnqueueNextAfterFilmTakeover() async throws {
        let (service, player, model) = await setup()
        let began = expectation(description: "ended fact persisted before acknowledgement")
        let gate = CrossDomainNavigationGate { began.fulfill() }
        defer { Task { await gate.release() } }
        await service.holdEnded(gate)
        player.emit(.ended, position: 180)
        await fulfillment(of: [began], timeout: 2)
        let film = player.takeFilm()
        await gate.release(); await model.flushPlaybackFacts(); await model.flushNavigation()
        XCTAssertEqual(player.current, film); XCTAssertEqual(player.musicPlays.count, 1)
        let moves = await service.advanceCount(), facts = await service.recorded()
        XCTAssertEqual(moves, 0); XCTAssertEqual(facts.last?.kind, .ended)
        XCTAssertEqual(facts.last?.position, 180)
        await model.selectOccurrence("entry-2")
        XCTAssertEqual(player.musicPlays.count, 2); XCTAssertEqual(player.current?.owner, "music")
        await model.close()
    }
}
#endif
