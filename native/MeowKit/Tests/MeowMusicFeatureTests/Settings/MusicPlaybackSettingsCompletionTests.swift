#if os(iOS) || os(macOS)
import Foundation
import XCTest
import MeowContracts
@testable import MeowMusicFeature

private actor SettingsCompletionGate {
    private let started: XCTestExpectation
    private let cancelled: XCTestExpectation?
    private var released = false
    private var waits: [CheckedContinuation<Void, Never>] = []
    init(started: XCTestExpectation, cancelled: XCTestExpectation? = nil) {
        self.started = started; self.cancelled = cancelled
    }
    func wait() async {
        started.fulfill()
        let cancelled = cancelled
        await withTaskCancellationHandler {
            if !released { await withCheckedContinuation { waits.append($0) } }
        } onCancel: { cancelled?.fulfill() }
    }
    func release() { released = true; waits.forEach { $0.resume() }; waits.removeAll() }
}

private actor SettingsCompletionService: MusicServing {
    static let source = MusicNativeSource(sourceKey: "wy:101", platform: "wy", nativeId: "101")
    static let track = MusicRemoteTrack(ref: .init(kind: .library, id: "song"), songId: "song",
        title: "Synthetic", sources: [source])
    static let entry = MusicQueueEntry(id: "current", track: track)
    private let gate: SettingsCompletionGate
    private var revision = 0, reads = 0
    private var quality = "flac", enabled = true
    private var official = MusicAccountPlatform.allCases
    init(gate: SettingsCompletionGate) { self.gate = gate }
    func lyricReads() -> Int { reads }
    func state() -> MusicPlaybackState {
        .init(settingsRevision: revision, lyricsEnabled: enabled, officialSources: official,
              currentId: Self.entry.id, total: 1, requestQuality: quality)
    }
    func updateSettings(_ change: MusicSettingsChange) throws -> MusicPlaybackState {
        guard change.ifRevision == revision else { throw MusicServiceError.staleRevision }
        revision += 1
        if let value = change.requestQuality { quality = value }
        if let value = change.lyricsEnabled { enabled = value }
        if let value = change.officialSources { official = value }
        return state()
    }
    func metadataCandidates(occurrenceID: String) -> MusicMetadataCandidates {
        .init(occurrenceID: occurrenceID, queueRevision: 0,
              preferences: .init(revision: revision, platforms: [.netease], enabledPlatforms: [.netease]),
              track: Self.track, candidates: [.init(source: Self.source)])
    }
    func lyrics(occurrenceId: String, sourceKey: String) async -> MusicLyricsSnapshot {
        reads += 1
        await gate.wait() // Intentionally ignores cancellation until the test releases the actual producer.
        return .init(occurrenceId: occurrenceId, state: .ready,
                     lines: [.init(index: 0, time: 0, end: 10, text: "Synthetic lyric")])
    }
    func platforms() -> [MusicPlatform] { [] }
    func collections() -> [MusicCollection] { [] }
    func queue() -> MusicQueueSnapshot { .init(revision: 0, currentId: Self.entry.id, entries: [Self.entry]) }
    func cancel(attemptId: String?) {}
    func record(_ fact: MusicPlaybackFact) -> Bool { true }
    func sourceAvailability(for ref: MusicReference) throws -> MusicSourceAvailability { throw MusicServiceError.unsupported }
    func search(platform: String, query: String, page: Int, limit: Int) throws -> MusicSearchPage { throw MusicServiceError.unsupported }
    func tracks(in collection: MusicCollection) throws -> [MusicRemoteTrack] { throw MusicServiceError.unsupported }
    func createPlaylist(name: String, refs: [MusicReference]) throws -> MusicCollection { throw MusicServiceError.unsupported }
    func editPlaylist(_ collection: MusicCollection, name: String?, refs: [MusicReference]?, order: [String]?) throws -> MusicCollection { throw MusicServiceError.unsupported }
    func removePlaylist(_ collection: MusicCollection) throws { throw MusicServiceError.unsupported }
    func changeQueue(_ change: MusicQueueChange, expectedRevision: Int) throws -> MusicPlaybackState { throw MusicServiceError.unsupported }
    func advance(_ direction: MusicDirection, reason: MusicAdvanceReason, expectedRevision: Int) throws -> MusicAdvanceResult { throw MusicServiceError.unsupported }
    func resolve(attemptId: String, occurrenceId: String, selection: MusicPlaybackSelection) throws -> MusicPlaybackCandidate { throw MusicServiceError.unsupported }
    func playedSongs() -> [MusicPlayedSong] { [] }
    func playedPlaylists() -> [MusicPlayedPlaylist] { [] }
    func restoreHistory(id: String, expectedRevision: Int) throws -> MusicPlaybackState { throw MusicServiceError.unsupported }
    func metadataPreferences() throws -> MusicMetadataPreferences { throw MusicServiceError.unsupported }
    func updateMetadataPreferences(_ change: MusicMetadataPreferencesChange) throws -> MusicMetadataPreferences { throw MusicServiceError.unsupported }
}

@MainActor private final class SettingsCompletionPlayer: MusicPlaybackDriving {
    func play(_ resource: PlaybackResource) -> MusicPlayerAttempt {
        XCTFail("Saving settings cannot start playback")
        return .init(id: UUID(), resourceID: resource.id)
    }
    func pause(_ attempt: MusicPlayerAttempt) { XCTFail("Unexpected pause") }
    func resume(_ attempt: MusicPlayerAttempt) { XCTFail("Unexpected resume") }
    func seek(_ position: Double, attempt: MusicPlayerAttempt) -> Bool { XCTFail("Unexpected seek"); return false }
    func stop(_ attempt: MusicPlayerAttempt) { XCTFail("No native attempt was started") }
}

@MainActor final class MusicPlaybackSettingsCompletionTests: XCTestCase {
    private func model(_ service: SettingsCompletionService) async -> MusicViewModel {
        let model = MusicViewModel(service: service, player: SettingsCompletionPlayer())
        model.playbackState.state = await service.state()
        model.playbackState.queue = [SettingsCompletionService.entry]
        model.candidate = .init(attemptId: "attempt", occurrenceId: "current", resourceId: "resource",
            url: URL(string: "https://synthetic.invalid/audio")!, title: "Synthetic", requestedQuality: "flac",
            resolvedSelection: .init(installationId: "official:synthetic", generation: 1,
                                    siteId: "wy", sourceKey: "wy:101", quality: "flac"))
        return model
    }

    func testQualityAndOfficialSourceCommitsDoNotWaitForLyricsAndStillRefreshThem() async {
        for official in [false, true] {
            let started = expectation(description: "lyric producer started")
            let gate = SettingsCompletionGate(started: started), service = SettingsCompletionService(gate: gate)
            let model = await model(service), committed = expectation(description: "local setting finished")
            let saving = Task {
                await model.updateSettings(official ? .init(ifRevision: 0, officialSources: [.qq])
                    : .init(ifRevision: 0, requestQuality: "320k"))
                committed.fulfill()
            }
            await fulfillment(of: [started, committed], timeout: 2)
            await model.settingsReceipts.flush()
            XCTAssertEqual(model.state.settingsRevision, 1); XCTAssertNil(model.error)
            if official { XCTAssertEqual(model.state.officialSources, [.qq]) }
            else { XCTAssertEqual(model.state.requestQuality, "320k") }
            let reads = await service.lyricReads(); XCTAssertEqual(reads, 1)
            await gate.release(); await saving.value; await model.playbackMetadata.flush()
            XCTAssertEqual(model.lyricLines.map(\.text), ["Synthetic lyric"])
            await model.close()
        }
    }

    func testDisablingLyricsCommitsWithoutWaitingAndRejectsTheOldResult() async {
        let started = expectation(description: "old lyric producer")
        let gate = SettingsCompletionGate(started: started), service = SettingsCompletionService(gate: gate)
        let model = await model(service)
        await model.updateSettings(.init(ifRevision: 0, requestQuality: "320k"))
        await fulfillment(of: [started], timeout: 2)
        await model.updateSettings(.init(ifRevision: 1, lyricsEnabled: false))
        XCTAssertFalse(model.state.lyricsEnabled); XCTAssertEqual(model.state.settingsRevision, 2)
        await gate.release(); await model.playbackMetadata.flush()
        XCTAssertTrue(model.lyricLines.isEmpty); XCTAssertEqual(model.lyricState, "disabled")
        await model.close()
    }

    func testCloseCancelsAndDrainsTheIndependentLyricProducer() async {
        let started = expectation(description: "lyric producer"), cancelled = expectation(description: "producer cancellation")
        let gate = SettingsCompletionGate(started: started, cancelled: cancelled)
        let model = await model(SettingsCompletionService(gate: gate))
        await model.updateSettings(.init(ifRevision: 0, requestQuality: "320k"))
        await fulfillment(of: [started], timeout: 2)
        var closed = false
        let closing = Task { await model.close(); closed = true }
        await fulfillment(of: [cancelled], timeout: 2)
        XCTAssertFalse(closed, "The owner must drain the real producer, not just its cancelled waiter")
        await gate.release(); await closing.value
        XCTAssertTrue(closed); XCTAssertTrue(model.isClosed); XCTAssertTrue(model.lyricLines.isEmpty)
    }
}
#endif
