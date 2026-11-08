#if os(iOS) || os(macOS)
import Foundation
import XCTest
import Combine
import MeowContracts
@testable import MeowMusicFeature

/// Deliberately returns after cancellation, like a delayed service acknowledgement.
@MainActor
private final class HomeSourceSettingsGate {
    let entered = XCTestExpectation(description: "service request entered")
    let returned = XCTestExpectation(description: "service request returned")
    private var continuation: CheckedContinuation<Void, Never>?

    func wait() async {
        entered.fulfill()
        await withCheckedContinuation { continuation = $0 }
    }

    func release() {
        continuation?.resume()
        continuation = nil
    }
}

@MainActor
private final class HomeSourceSettingsService: MusicServing, MusicAccountBrowsing, MusicHomeBrowsing {
    var value = MusicHomeSourceSelection(selected: .netease, revision: 0, available: [.netease, .kugou])
    var reads = 0
    var writes: [(platform: MusicPublicBrowsePlatform, revision: Int)] = []
    var readGate: HomeSourceSettingsGate?
    var writeGate: HomeSourceSettingsGate?
    var readFailure: Error?
    var writeFailure: MusicServiceError?
    var readCancellations: [Bool] = []
    var writeCancellations: [Bool] = []

    func homeSourceSelection() async throws -> MusicHomeSourceSelection {
        reads += 1
        let captured = value, gate = readGate, failure = readFailure
        readGate = nil; readFailure = nil
        if let gate {
            await gate.wait()
            readCancellations.append(Task.isCancelled)
            gate.returned.fulfill()
        }
        if let failure { throw failure }
        return captured
    }

    func selectHomeSource(_ source: MusicPublicBrowsePlatform, ifRevision: Int) async throws -> MusicHomeSourceSelection {
        writes.append((source, ifRevision))
        let gate = writeGate, failure = writeFailure
        writeGate = nil; writeFailure = nil
        let result: Result<MusicHomeSourceSelection, MusicServiceError>
        if ifRevision != value.revision {
            result = .failure(.staleRevision)
        } else if !value.available.contains(source) {
            result = .failure(.unsupported)
        } else if let failure, failure != .commitUnconfirmed {
            result = .failure(failure)
        } else {
            if source != value.selected {
                value = .init(selected: source, revision: value.revision + 1, available: value.available)
            }
            result = failure.map { .failure($0) } ?? .success(value)
        }
        if let gate {
            await gate.wait()
            writeCancellations.append(Task.isCancelled)
            gate.returned.fulfill()
        }
        return try result.get()
    }

    func restoredHome() async throws -> MusicPublicBrowsePage? { throw MusicServiceError.unsupported }
    func homePage(cursor: String?, refresh: Bool) async throws -> MusicPublicBrowsePage { throw MusicServiceError.unsupported }
    func homeChanges() async -> AsyncStream<Void> {
        XCTFail("Source settings must not start home content observation")
        return AsyncStream { $0.finish() }
    }
    func publicDiscovery(_ destination: MusicPublicBrowseDestination, cursor: String?) async throws -> MusicPublicBrowsePage { throw MusicServiceError.unsupported }
    func publicPlaylists(platform: MusicPublicBrowsePlatform, query: String, cursor: String?) async throws -> MusicPublicBrowseCollectionPage { throw MusicServiceError.unsupported }
    func publicDetail(_ collection: MusicPublicBrowseCollection, cursor: String?) async throws -> MusicPublicBrowseDetail { throw MusicServiceError.unsupported }
    func recommendations(platform: MusicAccountPlatform, kind: String, cursor: String?) async throws -> MusicBrowsePage { throw MusicServiceError.unsupported }
    func accountCollections(platform: MusicAccountPlatform, cursor: String?) async throws -> MusicAccountCollectionPage { throw MusicServiceError.unsupported }
    func accountTracks(platform: MusicAccountPlatform, collectionID: String, cursor: String?) async throws -> MusicBrowsePage { throw MusicServiceError.unsupported }
    func state() async throws -> MusicPlaybackState { throw MusicServiceError.unsupported }
    func queue() async throws -> MusicQueueSnapshot { throw MusicServiceError.unsupported }
    func platforms() async throws -> [MusicPlatform] { throw MusicServiceError.unsupported }
    func collections() async throws -> [MusicCollection] { throw MusicServiceError.unsupported }
    func search(platform: String, query: String, page: Int, limit: Int) async throws -> MusicSearchPage { throw MusicServiceError.unsupported }
    func tracks(in collection: MusicCollection) async throws -> [MusicRemoteTrack] { throw MusicServiceError.unsupported }
    func createPlaylist(name: String, refs: [MusicReference]) async throws -> MusicCollection { throw MusicServiceError.unsupported }
    func editPlaylist(_ collection: MusicCollection, name: String?, refs: [MusicReference]?, order: [String]?) async throws -> MusicCollection { throw MusicServiceError.unsupported }
    func removePlaylist(_ collection: MusicCollection) async throws { throw MusicServiceError.unsupported }
    func changeQueue(_ change: MusicQueueChange, expectedRevision: Int) async throws -> MusicPlaybackState { throw MusicServiceError.unsupported }
    func advance(_ direction: MusicDirection, reason: MusicAdvanceReason, expectedRevision: Int) async throws -> MusicAdvanceResult { throw MusicServiceError.unsupported }
    func updateSettings(_ change: MusicSettingsChange) async throws -> MusicPlaybackState { throw MusicServiceError.unsupported }
    func sourceAvailability(for ref: MusicReference) async throws -> MusicSourceAvailability { throw MusicServiceError.unsupported }
    func metadataPreferences() async throws -> MusicMetadataPreferences { throw MusicServiceError.unsupported }
    func updateMetadataPreferences(_ change: MusicMetadataPreferencesChange) async throws -> MusicMetadataPreferences { throw MusicServiceError.unsupported }
    func metadataCandidates(occurrenceID: String) async throws -> MusicMetadataCandidates { throw MusicServiceError.unsupported }
    func resolve(attemptId: String, occurrenceId: String, selection: MusicPlaybackSelection) async throws -> MusicPlaybackCandidate { throw MusicServiceError.unsupported }
    func cancel(attemptId: String?) async {}
    func record(_ fact: MusicPlaybackFact) async throws -> Bool { throw MusicServiceError.unsupported }
    func lyrics(occurrenceId: String, sourceKey: String) async throws -> MusicLyricsSnapshot { throw MusicServiceError.unsupported }
    func playedSongs() async throws -> [MusicPlayedSong] { throw MusicServiceError.unsupported }
    func playedPlaylists() async throws -> [MusicPlayedPlaylist] { throw MusicServiceError.unsupported }
    func restoreHistory(id: String, expectedRevision: Int) async throws -> MusicPlaybackState { throw MusicServiceError.unsupported }
}

@MainActor
private final class HomeSourceSettingsPlayer: MusicPlaybackDriving {
    func play(_ resource: PlaybackResource) -> MusicPlayerAttempt {
        XCTFail("Source settings must not start playback")
        return .init(id: UUID(), resourceID: resource.id)
    }
    func pause(_ attempt: MusicPlayerAttempt) {}
    func resume(_ attempt: MusicPlayerAttempt) {}
    func seek(_ position: Double, attempt: MusicPlayerAttempt) -> Bool { false }
    func stop(_ attempt: MusicPlayerAttempt) {}
}

@MainActor
final class MusicHomeSourceSettingsTests: XCTestCase {
    private func makeOwner(_ service: HomeSourceSettingsService) -> MusicViewModel {
        MusicViewModel(service: service, player: HomeSourceSettingsPlayer())
    }

    private func observeSelection(
        _ state: MusicHomeSourceSettingsModel, equals value: MusicHomeSourceSelection?
    ) -> (XCTestExpectation, AnyCancellable) {
        let changed = expectation(description: "authoritative selection published")
        let observation = state.$selection.filter { $0 == value }.prefix(1).sink { _ in changed.fulfill() }
        return (changed, observation)
    }

    private func observeSaveCompletion(_ state: MusicHomeSourceSettingsModel) -> (XCTestExpectation, AnyCancellable) {
        let completed = expectation(description: "source save settled")
        let observation = state.$isSaving.dropFirst().filter { !$0 }.prefix(1).sink { _ in completed.fulfill() }
        return (completed, observation)
    }

    private func activate(_ state: MusicHomeSourceSettingsModel, expecting value: MusicHomeSourceSelection) async {
        let (changed, observation) = observeSelection(state, equals: value)
        state.activate()
        await fulfillment(of: [changed], timeout: 2)
        observation.cancel()
        XCTAssertEqual(state.selection, value)
    }

    private func save(_ state: MusicHomeSourceSettingsModel, _ platform: MusicPublicBrowsePlatform) async {
        let (completed, observation) = observeSaveCompletion(state)
        state.select(platform)
        await fulfillment(of: [completed], timeout: 2)
        observation.cancel()
        XCTAssertFalse(state.isSaving)
    }

    private func releaseWithoutPublishing(_ gate: HomeSourceSettingsGate, state: MusicHomeSourceSettingsModel) async {
        let changed = expectation(description: "obsolete request must not publish or report an error")
        changed.isInverted = true
        let observation = state.objectWillChange.sink { changed.fulfill() }
        state.onError = { _ in changed.fulfill() }
        gate.release()
        // The positive receipt proves the late response actually returned; the
        // inverted expectation checks its observable aftermath, without sleeps.
        await fulfillment(of: [gate.returned], timeout: 2)
        await fulfillment(of: [changed], timeout: 0.1)
        observation.cancel()
    }

    func testReadsAvailableSourcesAndUsesConfirmedRevisionForSingleFlightWrites() async {
        let service = HomeSourceSettingsService()
        service.value = .init(selected: .netease, revision: 7, available: [.kugou, .netease])
        let owner = makeOwner(service), state = MusicHomeSourceSettingsModel(model: owner)
        state.select(.kugou)
        XCTAssertNil(state.selection); XCTAssertFalse(state.isSaving)
        await activate(state, expecting: service.value)
        state.activate()
        state.select(.netease); state.select(.qq)
        XCTAssertEqual(service.reads, 1); XCTAssertTrue(service.writes.isEmpty)

        let previous = service.value, gate = HomeSourceSettingsGate()
        service.writeGate = gate
        let (completed, observation) = observeSaveCompletion(state)
        state.select(.kugou)
        await fulfillment(of: [gate.entered], timeout: 2)
        XCTAssertTrue(state.isSaving)
        XCTAssertEqual(state.selection, previous, "An attempted selection is not a confirmed value")
        state.select(.netease); state.select(.kugou)
        XCTAssertEqual(service.writes.count, 1)
        XCTAssertEqual(service.writes.first?.platform, .kugou)
        XCTAssertEqual(service.writes.first?.revision, 7)
        gate.release()
        await fulfillment(of: [completed, gate.returned], timeout: 2)
        observation.cancel()
        XCTAssertEqual(state.selection, .init(selected: .kugou, revision: 8, available: [.kugou, .netease]))
        XCTAssertFalse(state.isSaving)
        state.deactivate(); await owner.close()
    }

    func testSeparateEntriesReopenTheSameAuthoritativeSelection() async {
        let service = HomeSourceSettingsService(), owner = makeOwner(service)
        let fullSettings = MusicHomeSourceSettingsModel(model: owner)
        let shortcut = MusicHomeSourceSettingsModel(model: owner)
        await activate(fullSettings, expecting: service.value)
        await save(fullSettings, .kugou)
        fullSettings.deactivate()
        await activate(shortcut, expecting: service.value)
        XCTAssertEqual(shortcut.selection?.selected, .kugou)
        await save(shortcut, .netease)
        shortcut.deactivate()
        await activate(fullSettings, expecting: service.value)
        XCTAssertEqual(fullSettings.selection?.selected, .netease)
        XCTAssertEqual(fullSettings.selection?.revision, 2)
        XCTAssertEqual(service.reads, 3)
        XCTAssertEqual(service.writes.map(\.revision), [0, 1])
        fullSettings.deactivate(); await owner.close()
    }

    func testStaleRevisionRereadsAuthorityWithoutReplayingTheSelection() async {
        let service = HomeSourceSettingsService(), owner = makeOwner(service)
        let first = MusicHomeSourceSettingsModel(model: owner), second = MusicHomeSourceSettingsModel(model: owner)
        await activate(first, expecting: service.value)
        await activate(second, expecting: service.value)
        var errors: [String] = []
        second.onError = { errors.append($0) }
        await save(first, .kugou)
        await save(second, .kugou)
        XCTAssertEqual(second.selection, service.value)
        XCTAssertEqual(second.selection?.revision, 1)
        XCTAssertEqual(service.reads, 3)
        XCTAssertEqual(service.writes.map(\.revision), [0, 0])
        XCTAssertEqual(errors, [MusicServiceError.staleRevision.message(operation: "保存数据源")])
        await save(second, .netease)
        XCTAssertEqual(service.writes.map(\.revision), [0, 0, 1])
        XCTAssertEqual(second.selection?.revision, 2)
        first.deactivate(); second.deactivate(); await owner.close()
    }

    func testSaveFailuresRereadAuthorityIncludingUnconfirmedCommitAndSilentCancellation() async {
        for failure in [MusicServiceError.storageFailed, .storageConflict, .commitUnconfirmed, .cancelled] {
            let service = HomeSourceSettingsService(), owner = makeOwner(service)
            let state = MusicHomeSourceSettingsModel(model: owner)
            var errors: [String] = []
            state.onError = { errors.append($0) }
            await activate(state, expecting: service.value)
            service.writeFailure = failure
            await save(state, .kugou)
            XCTAssertEqual(service.reads, 2)
            XCTAssertEqual(service.writes.count, 1)
            XCTAssertEqual(state.selection, service.value)
            XCTAssertEqual(state.selection?.selected, failure == .commitUnconfirmed ? .kugou : .netease)
            XCTAssertEqual(state.selection?.revision, failure == .commitUnconfirmed ? 1 : 0)
            XCTAssertEqual(errors, failure == .cancelled ? [] : [failure.message(operation: "保存数据源")])
            state.deactivate(); await owner.close()
        }
    }

    func testFailedReadbackClearsSelectionAndReopeningRecovers() async {
        let service = HomeSourceSettingsService(), owner = makeOwner(service)
        let state = MusicHomeSourceSettingsModel(model: owner)
        var errors: [String] = []
        state.onError = { errors.append($0) }
        await activate(state, expecting: service.value)
        service.writeFailure = .storageFailed; service.readFailure = MusicServiceError.unavailable
        await save(state, .kugou)
        XCTAssertNil(state.selection)
        XCTAssertEqual(errors, [MusicServiceError.storageFailed.message(operation: "保存数据源")])
        state.select(.kugou)
        XCTAssertEqual(service.writes.count, 1)
        state.deactivate()
        await activate(state, expecting: service.value)
        XCTAssertEqual(service.reads, 3)
        state.deactivate(); await owner.close()
    }

    func testReadFailureReportsOnlySanitizedContextAndReopeningRetries() async {
        let service = HomeSourceSettingsService(), owner = makeOwner(service)
        let state = MusicHomeSourceSettingsModel(model: owner)
        service.readFailure = NSError(domain: "SyntheticProvider", code: 1,
            userInfo: [NSLocalizedDescriptionKey: "synthetic private response body"])
        let reported = expectation(description: "sanitized read error")
        var errors: [String] = []
        state.onError = { errors.append($0); reported.fulfill() }
        state.activate()
        await fulfillment(of: [reported], timeout: 2)
        XCTAssertNil(state.selection); XCTAssertFalse(state.isSaving)
        XCTAssertEqual(errors, [MusicServiceError.unavailable.message(operation: "读取数据源")])
        state.deactivate()
        await activate(state, expecting: service.value)
        XCTAssertEqual(service.reads, 2)
        state.deactivate(); await owner.close()
    }

    func testDeactivateBeforeTasksBeginPreventsReadsAndWrites() async {
        let service = HomeSourceSettingsService(), owner = makeOwner(service)
        let state = MusicHomeSourceSettingsModel(model: owner)
        state.activate(); state.deactivate()
        await activate(state, expecting: service.value)
        XCTAssertEqual(service.reads, 1)
        state.select(.kugou); state.deactivate()
        XCTAssertNil(state.selection); XCTAssertFalse(state.isSaving)
        await activate(state, expecting: service.value)
        XCTAssertEqual(service.reads, 2); XCTAssertTrue(service.writes.isEmpty)
        XCTAssertEqual(state.selection?.selected, .netease)
        state.deactivate(); await owner.close()
    }

    func testDeactivateCancelsLateReadAndReentryRejectsItsValueOrError() async {
        for failure in [nil, MusicServiceError.storageFailed] {
            let service = HomeSourceSettingsService(), owner = makeOwner(service)
            let state = MusicHomeSourceSettingsModel(model: owner), gate = HomeSourceSettingsGate()
            service.readGate = gate; service.readFailure = failure
            state.activate()
            await fulfillment(of: [gate.entered], timeout: 2)
            state.deactivate()
            service.value = .init(selected: .kugou, revision: 10, available: [.netease, .kugou])
            await activate(state, expecting: service.value)
            await releaseWithoutPublishing(gate, state: state)
            XCTAssertEqual(state.selection, service.value)
            XCTAssertEqual(service.readCancellations, [true])
            XCTAssertEqual(service.reads, 2)
            state.deactivate(); await owner.close()
        }
    }

    func testDeactivateCancelsLateSaveWithoutPublishingOrStartingFailureReadback() async {
        for failure in [nil, MusicServiceError.commitUnconfirmed, .storageFailed] {
            let service = HomeSourceSettingsService(), owner = makeOwner(service)
            let state = MusicHomeSourceSettingsModel(model: owner), gate = HomeSourceSettingsGate()
            await activate(state, expecting: service.value)
            service.writeGate = gate; service.writeFailure = failure
            state.select(.kugou)
            await fulfillment(of: [gate.entered], timeout: 2)
            state.deactivate()
            XCTAssertFalse(state.isSaving); XCTAssertNil(state.selection)
            // Another entry changes the authority before the old acknowledgement arrives.
            service.value = .init(selected: .netease, revision: 20, available: [.netease, .kugou])
            await activate(state, expecting: service.value)
            await releaseWithoutPublishing(gate, state: state)
            XCTAssertEqual(state.selection, service.value)
            XCTAssertFalse(state.isSaving)
            XCTAssertEqual(service.writeCancellations, [true])
            XCTAssertEqual(service.writes.count, 1); XCTAssertEqual(service.reads, 2)
            state.deactivate(); await owner.close()
        }
    }

    func testUnavailableAccessClearsStateAndRestorationReloadsWithoutReactivation() async {
        let service = HomeSourceSettingsService(), owner = makeOwner(service)
        let state = MusicHomeSourceSettingsModel(model: owner), gate = HomeSourceSettingsGate()
        owner.setAvailable(false)
        state.activate(); state.select(.kugou)
        XCTAssertNil(state.selection); XCTAssertEqual(service.reads, 0); XCTAssertTrue(service.writes.isEmpty)
        let (loaded, loadObservation) = observeSelection(state, equals: service.value)
        owner.setAvailable(true)
        await fulfillment(of: [loaded], timeout: 2)
        loadObservation.cancel()

        service.writeGate = gate
        state.select(.kugou)
        await fulfillment(of: [gate.entered], timeout: 2)
        let (cleared, clearObservation) = observeSelection(state, equals: nil)
        owner.setAvailable(false)
        state.select(.netease)
        await fulfillment(of: [cleared], timeout: 2)
        clearObservation.cancel()
        XCTAssertNil(state.selection); XCTAssertFalse(state.isSaving)
        state.select(.kugou)
        XCTAssertEqual(service.writes.count, 1); XCTAssertEqual(service.reads, 1)

        let (restored, restoreObservation) = observeSelection(state, equals: service.value)
        owner.setAvailable(true)
        await fulfillment(of: [restored], timeout: 2)
        restoreObservation.cancel()
        await releaseWithoutPublishing(gate, state: state)
        XCTAssertEqual(state.selection, service.value)
        XCTAssertEqual(service.writeCancellations, [true])
        XCTAssertEqual(service.reads, 2)
        state.deactivate(); await owner.close()
    }

    func testAccountGenerationChangeInvalidatesReadEvenWhileAccessRemainsAvailable() async {
        let service = HomeSourceSettingsService(), owner = makeOwner(service)
        let state = MusicHomeSourceSettingsModel(model: owner), gate = HomeSourceSettingsGate()
        service.readGate = gate
        state.activate()
        await fulfillment(of: [gate.entered], timeout: 2)
        service.value = .init(selected: .kugou, revision: 30, available: [.netease, .kugou])
        let (changed, observation) = observeSelection(state, equals: service.value)
        owner.accessState.generation = UUID()
        await fulfillment(of: [changed], timeout: 2)
        observation.cancel()
        XCTAssertTrue(owner.isAvailable)
        await releaseWithoutPublishing(gate, state: state)
        XCTAssertEqual(state.selection, service.value)
        XCTAssertEqual(service.readCancellations, [true])
        XCTAssertEqual(service.reads, 2)
        state.deactivate(); await owner.close()
    }

    func testAccountGenerationChangeInvalidatesSaveAndRejectsOldRevisionBeforeNotificationDelivery() async {
        let service = HomeSourceSettingsService(), owner = makeOwner(service)
        let state = MusicHomeSourceSettingsModel(model: owner), gate = HomeSourceSettingsGate()
        await activate(state, expecting: service.value)
        service.writeGate = gate; service.writeFailure = .storageFailed
        state.select(.kugou)
        await fulfillment(of: [gate.entered], timeout: 2)
        service.value = .init(selected: .kugou, revision: 40, available: [.netease, .kugou])
        let (changed, observation) = observeSelection(state, equals: service.value)
        owner.accessState.generation = UUID()
        await fulfillment(of: [changed], timeout: 2)
        observation.cancel()
        await releaseWithoutPublishing(gate, state: state)
        XCTAssertEqual(state.selection, service.value); XCTAssertFalse(state.isSaving)
        XCTAssertEqual(service.writeCancellations, [true])
        XCTAssertEqual(service.reads, 2); XCTAssertEqual(service.writes.count, 1)

        owner.accessState.generation = UUID()
        state.select(.netease)
        XCTAssertFalse(state.isSaving, "The live generation fences writes before accessChanges is delivered")
        XCTAssertEqual(service.writes.count, 1)
        state.deactivate(); await owner.close()
    }

    func testAccountGenerationChangeDuringFailureReadbackSuppressesOldValueAndError() async {
        let service = HomeSourceSettingsService(), owner = makeOwner(service)
        let state = MusicHomeSourceSettingsModel(model: owner), gate = HomeSourceSettingsGate()
        await activate(state, expecting: service.value)
        service.readGate = gate; service.writeFailure = .storageFailed
        state.select(.kugou)
        await fulfillment(of: [gate.entered], timeout: 2)
        XCTAssertTrue(state.isSaving)
        service.value = .init(selected: .kugou, revision: 50, available: [.netease, .kugou])
        let (changed, observation) = observeSelection(state, equals: service.value)
        owner.accessState.generation = UUID()
        await fulfillment(of: [changed], timeout: 2)
        observation.cancel()
        await releaseWithoutPublishing(gate, state: state)
        XCTAssertEqual(state.selection, service.value); XCTAssertFalse(state.isSaving)
        XCTAssertEqual(service.readCancellations, [true])
        XCTAssertEqual(service.reads, 3); XCTAssertEqual(service.writes.count, 1)
        state.deactivate(); await owner.close()
    }

    func testClosedOwnerCancelsPendingSaveAndCannotBeReactivated() async {
        let service = HomeSourceSettingsService(), owner = makeOwner(service)
        let state = MusicHomeSourceSettingsModel(model: owner), gate = HomeSourceSettingsGate()
        await activate(state, expecting: service.value)
        service.writeGate = gate; service.writeFailure = .storageFailed
        state.select(.kugou)
        await fulfillment(of: [gate.entered], timeout: 2)
        let (cleared, observation) = observeSelection(state, equals: nil)
        await owner.close()
        await fulfillment(of: [cleared], timeout: 2)
        observation.cancel()
        XCTAssertTrue(owner.isClosed); XCTAssertNil(state.selection); XCTAssertFalse(state.isSaving)
        state.deactivate(); state.activate(); state.select(.kugou)
        await releaseWithoutPublishing(gate, state: state)
        XCTAssertEqual(service.writeCancellations, [true])
        XCTAssertEqual(service.reads, 1); XCTAssertEqual(service.writes.count, 1)
        XCTAssertNil(state.selection); XCTAssertFalse(state.isSaving)
        state.deactivate()
    }

    func testReleasingEntryCancelsWorkWithoutRetainingThePresentationOwner() async {
        for saving in [false, true] {
            let service = HomeSourceSettingsService(), owner = makeOwner(service)
            var state: MusicHomeSourceSettingsModel? = MusicHomeSourceSettingsModel(model: owner)
            let gate = HomeSourceSettingsGate()
            if saving {
                await activate(state!, expecting: service.value)
                service.writeGate = gate
                state?.select(.kugou)
            } else {
                service.readGate = gate
                state?.activate()
            }
            await fulfillment(of: [gate.entered], timeout: 2)
            weak var released = state
            state = nil
            XCTAssertNil(released, "A suspended service request must not retain its presentation owner")
            gate.release()
            await fulfillment(of: [gate.returned], timeout: 2)
            XCTAssertEqual(saving ? service.writeCancellations : service.readCancellations, [true])
            await owner.close()
        }
    }
}
#endif
