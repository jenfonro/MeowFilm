import Foundation
import XCTest
import MeowContracts
@testable import MeowMusicFeature
@testable import MeowMusicNativeService

@MainActor private final class SettingsReceiptPlayer: MusicPlaybackDriving {
    func play(_ resource: PlaybackResource) -> MusicPlayerAttempt {
        XCTFail("Settings must not start playback")
        return .init(id: UUID(), resourceID: resource.id)
    }
    func pause(_ attempt: MusicPlayerAttempt) { XCTFail("Settings must not pause playback") }
    func resume(_ attempt: MusicPlayerAttempt) { XCTFail("Settings must not resume playback") }
    func seek(_ position: Double, attempt: MusicPlayerAttempt) -> Bool { XCTFail("Settings must not seek"); return false }
    func stop(_ attempt: MusicPlayerAttempt) { XCTFail("Settings must not stop playback") }
}

// The real native service commits the document. Only delivery of its receipt is held.
private actor SettingsReceiptService: MusicServing {
    enum Receipt { case success, unconfirmed, failureBeforeCommit }
    let native: NativeMusicService
    let gate: NativeMusicGate
    let receipt: Receipt
    var intercepted = false
    init(native: NativeMusicService, gate: NativeMusicGate, receipt: Receipt) {
        self.native = native; self.gate = gate; self.receipt = receipt
    }
    func updateSettings(_ change: MusicSettingsChange) async throws -> MusicPlaybackState {
        guard !intercepted else { return try await native.updateSettings(change) }
        intercepted = true
        if case .failureBeforeCommit = receipt {
            await gate.wait()
            throw MusicServiceError.storageFailed
        }
        let value = try await native.updateSettings(change)
        await gate.wait()
        if case .unconfirmed = receipt { throw MusicServiceError.commitUnconfirmed }
        return value
    }
    func state() async throws -> MusicPlaybackState { try await native.state() }
    func platforms() async throws -> [MusicPlatform] { try await native.platforms() }
    func collections() async throws -> [MusicCollection] { try await native.collections() }
    func queue() async throws -> MusicQueueSnapshot { try await native.queue() }
    func sourceAvailability(for ref: MusicReference) async throws -> MusicSourceAvailability { throw MusicServiceError.unsupported }
    func search(platform: String, query: String, page: Int, limit: Int) async throws -> MusicSearchPage { throw MusicServiceError.unsupported }
    func tracks(in collection: MusicCollection) async throws -> [MusicRemoteTrack] { throw MusicServiceError.unsupported }
    func createPlaylist(name: String, refs: [MusicReference]) async throws -> MusicCollection { throw MusicServiceError.unsupported }
    func editPlaylist(_ collection: MusicCollection, name: String?, refs: [MusicReference]?, order: [String]?) async throws -> MusicCollection { throw MusicServiceError.unsupported }
    func removePlaylist(_ collection: MusicCollection) async throws { throw MusicServiceError.unsupported }
    func changeQueue(_ change: MusicQueueChange, expectedRevision: Int) async throws -> MusicPlaybackState { throw MusicServiceError.unsupported }
    func advance(_ direction: MusicDirection, reason: MusicAdvanceReason, expectedRevision: Int) async throws -> MusicAdvanceResult { throw MusicServiceError.unsupported }
    func resolve(attemptId: String, occurrenceId: String, selection: MusicPlaybackSelection) async throws -> MusicPlaybackCandidate { throw MusicServiceError.unsupported }
    func cancel(attemptId: String?) async throws {}
    func record(_ fact: MusicPlaybackFact) async throws -> Bool { throw MusicServiceError.unsupported }
    func lyrics(occurrenceId: String, sourceKey: String) async throws -> MusicLyricsSnapshot { throw MusicServiceError.unsupported }
    func playedSongs() async throws -> [MusicPlayedSong] { throw MusicServiceError.unsupported }
    func playedPlaylists() async throws -> [MusicPlayedPlaylist] { throw MusicServiceError.unsupported }
    func restoreHistory(id: String, expectedRevision: Int) async throws -> MusicPlaybackState { throw MusicServiceError.unsupported }
    func metadataPreferences() async throws -> MusicMetadataPreferences { try await native.metadataPreferences() }
    func updateMetadataPreferences(_ change: MusicMetadataPreferencesChange) async throws -> MusicMetadataPreferences {
        try await native.updateMetadataPreferences(change)
    }
    func metadataCandidates(occurrenceID: String) async throws -> MusicMetadataCandidates {
        XCTFail("Settings must not request playback metadata")
        throw MusicServiceError.unsupported
    }
}

@MainActor final class MusicSettingsReceiptTests: XCTestCase {
    func testCancelledQualityReceiptReopensWithCommittedValueAndCanSaveAgain() async throws {
        try await receipt(official: false, mode: .success)
    }
    func testUnconfirmedQualityReceiptReopensWithCommittedValueAndCanSaveAgain() async throws {
        try await receipt(official: false, mode: .unconfirmed)
    }
    func testCancelledOfficialReceiptReopensWithCommittedValueAndCanSaveAgain() async throws {
        try await receipt(official: true, mode: .success)
    }
    func testUnconfirmedOfficialReceiptReopensWithCommittedValueAndCanSaveAgain() async throws {
        try await receipt(official: true, mode: .unconfirmed)
    }
    private func receipt(official: Bool, mode: SettingsReceiptService.Receipt) async throws {
        let native = try await NativeMusicFixture.service(), gate = NativeMusicGate()
        let service = SettingsReceiptService(native: native, gate: gate, receipt: mode)
        let model = MusicViewModel(service: service, player: SettingsReceiptPlayer())
        await model.load()
        // The queue/clock belong to another operation while the settings receipt is suspended.
        let quality = MusicQualitySettingsState(model: model)
        let sources = MusicSourceManagementState(model: model)
        quality.start(); sources.start()
        if official { sources.setOfficial(.qq, enabled: false) }
        else { quality.setRequestQuality("320k") }
        await gate.started()
        quality.close(); sources.close()
        model.playbackState.state = .init(revision: 7, currentId: "retained", total: 2,
            position: 12, canNext: true, nextPreviewID: "next", sourceCollectionID: "collection")
        model.clock.position = 19
        await gate.release()
        await quality.flush(); await sources.flush()
        let saved = try await native.state()
        XCTAssertEqual(saved.settingsRevision, 1)
        XCTAssertEqual(model.state.settingsRevision, saved.settingsRevision)
        XCTAssertEqual(model.state.requestQuality, saved.requestQuality)
        XCTAssertEqual(model.state.officialSources, saved.officialSources)
        XCTAssertEqual(model.state.revision, 7)
        XCTAssertEqual(model.state.currentId, "retained")
        XCTAssertEqual(model.state.total, 2)
        XCTAssertEqual(model.state.position, 12)
        XCTAssertEqual(model.state.nextPreviewID, "next")
        XCTAssertEqual(model.state.sourceCollectionID, "collection")
        XCTAssertEqual(model.clock.position, 19)
        XCTAssertEqual(quality.value.requestQuality, "flac", "Closed page must not receive the late success")
        XCTAssertTrue(sources.official.contains(.qq), "Closed page must not receive the late success")
        XCTAssertNil(model.error)
        let reopenedQuality = MusicQualitySettingsState(model: model)
        let reopenedSources = MusicSourceManagementState(model: model)
        reopenedQuality.start(); reopenedSources.start()
        if official {
            XCTAssertFalse(reopenedSources.official.contains(.qq))
            reopenedSources.setOfficial(.qq, enabled: true)
            await reopenedSources.flush()
        } else {
            XCTAssertEqual(reopenedQuality.value.requestQuality, "320k")
            reopenedQuality.setRequestQuality("128k")
            await reopenedQuality.flush()
        }
        let next = try await native.state()
        XCTAssertEqual(next.settingsRevision, 2)
        XCTAssertEqual(model.state.settingsRevision, 2)
        if official { XCTAssertTrue(next.officialSources.contains(.qq)) }
        else { XCTAssertEqual(next.requestQuality, "128k") }
        XCTAssertNil(model.error)
        reopenedQuality.close(); reopenedSources.close()
        await model.close(); await native.close()
    }

    func testUnconfirmedReceiptWithoutPageCancellationReadsAuthorityBeforeNextSave() async throws {
        let native = try await NativeMusicFixture.service(), gate = NativeMusicGate()
        let service = SettingsReceiptService(native: native, gate: gate, receipt: .unconfirmed)
        let model = MusicViewModel(service: service, player: SettingsReceiptPlayer())
        await model.load()
        let page = MusicQualitySettingsState(model: model)
        page.start(); page.setRequestQuality("320k")
        await gate.started(); await gate.release(); await page.flush()
        XCTAssertEqual(page.value.requestQuality, "320k")
        XCTAssertEqual(page.value.revision, 1)
        XCTAssertNotNil(model.error, "An uncertain receipt is not silently reported as success")
        page.setRequestQuality("128k"); await page.flush()
        let saved = try await native.state()
        XCTAssertEqual(saved.requestQuality, "128k"); XCTAssertEqual(saved.settingsRevision, 2)
        page.close(); await model.close(); await native.close()
    }

    func testFailureBeforeCommitKeepsSavedSettingsAndRevision() async throws {
        let native = try await NativeMusicFixture.service(), gate = NativeMusicGate()
        let service = SettingsReceiptService(native: native, gate: gate, receipt: .failureBeforeCommit)
        let model = MusicViewModel(service: service, player: SettingsReceiptPlayer())
        await model.load()
        let quality = MusicQualitySettingsState(model: model)
        quality.start(); quality.setRequestQuality("320k")
        await gate.started(); await gate.release(); await quality.flush()
        let saved = try await native.state()
        XCTAssertEqual(saved.requestQuality, "flac"); XCTAssertEqual(saved.settingsRevision, 0)
        XCTAssertEqual(quality.value.requestQuality, "flac"); XCTAssertEqual(quality.value.revision, 0)
        XCTAssertNotNil(model.error)
        quality.close(); await model.close(); await native.close()
    }

    func testAccessRevocationPreventsCommittedReceiptFromPublishing() async throws {
        for mode in [SettingsReceiptService.Receipt.success, .unconfirmed] {
            let native = try await NativeMusicFixture.service(), gate = NativeMusicGate()
            let service = SettingsReceiptService(native: native, gate: gate, receipt: mode)
            let model = MusicViewModel(service: service, player: SettingsReceiptPlayer())
            await model.load()
            let quality = MusicQualitySettingsState(model: model)
            quality.start(); quality.setRequestQuality("320k")
            await gate.started()
            model.setAvailable(false); quality.reconcileAccess()
            let revoked = model.state
            await gate.release(); await quality.flush()
            XCTAssertEqual(model.state, revoked)
            XCTAssertNil(model.error)
            let saved = try await native.state()
            XCTAssertEqual(saved.requestQuality, "320k"); XCTAssertEqual(saved.settingsRevision, 1)
            quality.close(); await model.close(); await native.close()
        }
    }
}
