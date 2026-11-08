import Foundation
import XCTest
import MeowContracts
import MeowPlayback
@testable import MeowMusicFeature
@testable import MeowMusicNativeService

// Only the return boundary is gated; resolution, renewal, pins, queue and facts
// all execute on the real NativeMusicService. No upstream or native audio I/O.
private actor RenewalReceiptGate {
    private var entered = false, released = false
    private var waiter: CheckedContinuation<Void, Never>?
    func wait() async {
        entered = true
        if !released { await withCheckedContinuation { waiter = $0 } }
    }
    func started() async throws {
        let end = ContinuousClock.now.advanced(by: .seconds(3))
        while !entered {
            guard ContinuousClock.now < end else { throw MusicServiceError.timeout }
            try await Task.sleep(for: .milliseconds(2))
        }
    }
    func release() { released = true; waiter?.resume(); waiter = nil }
}

private actor RenewalReceiptService: MusicServing, MusicSourcePlaybackServing, MusicPlaybackRecoveryServing {
    let base: NativeMusicService
    let gate: RenewalReceiptGate
    var returned: MusicPlaybackRecovery?
    var cancelled: [String] = []
    var facts: [MusicPlaybackFact] = []
    init(_ base: NativeMusicService, gate: RenewalReceiptGate) { self.base = base; self.gate = gate }
    func receipt() -> MusicPlaybackRecovery? { returned }
    func evidence() -> ([String], [MusicPlaybackFact]) { (cancelled, facts) }
    func recoverPlayback(previousAttemptID: String, attemptID: String, rejectedURL: URL,
                         position: Double) async throws -> MusicPlaybackRecovery {
        let value = try await base.recoverPlayback(previousAttemptID: previousAttemptID, attemptID: attemptID,
                                                   rejectedURL: rejectedURL, position: position)
        returned = value
        await gate.wait() // Deliberately return even after the caller cancels.
        return value
    }
    func state() async throws -> MusicPlaybackState { return try await base.state() }
    func platforms() async throws -> [MusicPlatform] { return try await base.platforms() }
    func search(platform: String, query: String, page: Int, limit: Int) async throws -> MusicSearchPage { return try await base.search(platform: platform, query: query, page: page, limit: limit) }
    func collections() async throws -> [MusicCollection] { return try await base.collections() }
    func tracks(in collection: MusicCollection) async throws -> [MusicRemoteTrack] { return try await base.tracks(in: collection) }
    func createPlaylist(name: String, refs: [MusicReference]) async throws -> MusicCollection { return try await base.createPlaylist(name: name, refs: refs) }
    func editPlaylist(_ collection: MusicCollection, name: String?, refs: [MusicReference]?, order: [String]?) async throws -> MusicCollection { return try await base.editPlaylist(collection, name: name, refs: refs, order: order) }
    func removePlaylist(_ collection: MusicCollection) async throws { try await base.removePlaylist(collection) }
    func queue() async throws -> MusicQueueSnapshot { return try await base.queue() }
    func changeQueue(_ change: MusicQueueChange, expectedRevision: Int) async throws -> MusicPlaybackState { return try await base.changeQueue(change, expectedRevision: expectedRevision) }
    func advance(_ direction: MusicDirection, reason: MusicAdvanceReason, expectedRevision: Int) async throws -> MusicAdvanceResult { return try await base.advance(direction, reason: reason, expectedRevision: expectedRevision) }
    func updateSettings(_ change: MusicSettingsChange) async throws -> MusicPlaybackState { return try await base.updateSettings(change) }
    func resolve(attemptId: String, occurrenceId: String, selection: MusicPlaybackSelection) async throws -> MusicPlaybackCandidate { return try await base.resolve(attemptId: attemptId, occurrenceId: occurrenceId, selection: selection) }
    func cancel(attemptId: String?) async throws { if let attemptId { cancelled.append(attemptId) }; try await base.cancel(attemptId: attemptId) }
    func record(_ fact: MusicPlaybackFact) async throws -> Bool { facts.append(fact); return try await base.record(fact) }
    func lyrics(occurrenceId: String, sourceKey: String) async throws -> MusicLyricsSnapshot { return try await base.lyrics(occurrenceId: occurrenceId, sourceKey: sourceKey) }
    func playedSongs() async throws -> [MusicPlayedSong] { return try await base.playedSongs() }
    func playedPlaylists() async throws -> [MusicPlayedPlaylist] { return try await base.playedPlaylists() }
    func restoreHistory(id: String, expectedRevision: Int) async throws -> MusicPlaybackState { return try await base.restoreHistory(id: id, expectedRevision: expectedRevision) }
    func sourceAvailability(for ref: MusicReference) async throws -> MusicSourceAvailability { return try await base.sourceAvailability(for: ref) }
    func metadataPreferences() async throws -> MusicMetadataPreferences { return try await base.metadataPreferences() }
    func updateMetadataPreferences(_ change: MusicMetadataPreferencesChange) async throws -> MusicMetadataPreferences { return try await base.updateMetadataPreferences(change) }
    func metadataCandidates(occurrenceID: String) async throws -> MusicMetadataCandidates { return try await base.metadataCandidates(occurrenceID: occurrenceID) }
    func inspectSources(for reference: MusicReference) async throws -> any MusicSourceInspectionSession { return try await base.inspectSources(for: reference) }
    func sourceSession(occurrenceID: String, expectedQueueRevision: Int) async throws -> any MusicSourcePlaybackSession { return try await base.sourceSession(occurrenceID: occurrenceID, expectedQueueRevision: expectedQueueRevision) }
    func activateSource(attemptID: String) async throws { try await base.activateSource(attemptID: attemptID) }
    func discardSource(attemptID: String) async { await base.discardSource(attemptID: attemptID) }
    func sourceConfigurationChanged() async { await base.sourceConfigurationChanged() }
}

private actor RenewalPeerScripts: MusicScriptResolving {
    var enabled = false
    var requests: [MusicScriptRequest] = []
    func enable() { enabled = true }
    func sources() -> [MusicScriptSource] { enabled ? [snapshot()] : [] }
    func source(installationID: String) -> MusicScriptSource { snapshot() }
    private func snapshot() -> MusicScriptSource {
        .init(installationID: "peer", generation: 1, enabled: enabled, state: enabled ? .ready : .disabled, version: "1",
              contentHash: String(repeating: "a", count: 64), revisionHash: String(repeating: "a", count: 64),
              capabilities: [.init(siteID: "tx", title: "Peer", actions: ["musicUrl"], qualities: ["flac", "128k"])])
    }
    func resolve(request: MusicScriptRequest) -> MusicScriptResult<MusicNativeAudio> {
        requests.append(request)
        return .init(value: .init(url: URL(string: "https://media.invalid/peer.\(request.quality)")!,
                     delivery: .init(quality: request.quality, evidence: .officialResponse, duration: 180)), source: snapshot())
    }
    func lyrics(request: MusicScriptRequest) -> MusicScriptResult<MusicNativeLyrics> {
        .init(value: .init(text: ""), source: snapshot())
    }
    func calls() -> [MusicScriptRequest] { requests }
}

@MainActor private final class RenewalReceiptPlayer: MusicPlaybackDriving {
    var resources: [PlaybackResource] = []
    var pausedAttempts: [MusicPlayerAttempt] = []
    var stoppedResources: [String] = []
    var onPlay: ((PlaybackResource) -> Void)?
    func play(_ resource: PlaybackResource) -> MusicPlayerAttempt {
        resources.append(resource); onPlay?(resource)
        return .init(id: UUID(), resourceID: resource.id)
    }
    func stop(_ attempt: MusicPlayerAttempt) { stoppedResources.append(attempt.resourceID) }
    func pause(_ attempt: MusicPlayerAttempt) { pausedAttempts.append(attempt) }
    func resume(_ attempt: MusicPlayerAttempt) {}
    func seek(_ position: Double, attempt: MusicPlayerAttempt) -> Bool { true }
}

@MainActor final class MusicRenewalAcceptanceBoundaryTests: XCTestCase {
    private enum Boundary { case expired, cancelled, revoked, revokedDuringPlay, healthy }
    func testExpiredRenewalContinuesLockedTierAndKeepsPausePosition() async throws {
        try await scenario(.expired, intent: .quality("flac"), paused: true)
    }
    func testExpiredRenewalContinuesAutomaticSameTier() async throws {
        try await scenario(.expired, intent: .automatic, paused: false)
    }
    func testExpiredExactRenewalNeverSubstitutesPeer() async throws {
        try await scenario(.expired, intent: nil, paused: true)
    }
    func testCancelledReturnedRenewalIsReleasedWithoutPeerOrFacts() async throws {
        try await scenario(.cancelled, intent: .quality("flac"), paused: true)
    }
    func testRevokedReturnedRenewalIsReleasedWithoutPeerOrFacts() async throws {
        try await scenario(.revoked, intent: .quality("flac"), paused: true)
    }
    func testRevocationDuringNativeAcceptanceReleasesAttemptWithoutPeer() async throws {
        try await scenario(.revokedDuringPlay, intent: .quality("flac"), paused: true)
    }
    func testHealthyRenewalControlRetainsPauseAndPosition() async throws {
        try await scenario(.healthy, intent: .quality("flac"), paused: true)
    }
    private func scenario(_ boundary: Boundary, intent: MusicSourceIntent?, paused: Bool) async throws {
        let clock = MusicRecoveryTestClock()
        clock.advance(Date().timeIntervalSince(clock.now()))
        let accounts = MusicRecoveryTestAccounts(platform: .qq, nativeID: "101", clock: clock)
        let grant = await accounts.enablePlaybackAuthorization()
        let scripts = RenewalPeerScripts()
        let song = try MusicModel.normalize(MusicNativeCandidateDiscoveryFixture.track("tx", id: "101"))
        let store = try MusicNativeCandidateDiscoveryFixture.store(song: song)
        let native = try await NativeMusicFixture.service(store,
            scripts: scripts, accounts: accounts, clock: { clock.now() })
        let gate = RenewalReceiptGate(), service = RenewalReceiptService(native, gate: gate)
        defer { Task { await gate.release() } }
        let player = RenewalReceiptPlayer(), model = MusicViewModel(service: service, player: player)
        await model.load()
        if let intent { await model.selectSource(intent: intent) }
        else {
            let key = try XCTUnwrap(model.current?.track.sources.first?.sourceKey)
            await model.choose(.init(installationId: "official:recovery-synthetic", generation: 1,
                                     siteId: "tx", sourceKey: key, quality: "flac"))
        }
        let original = try XCTUnwrap(model.candidate)
        XCTAssertTrue(model.activePlaybackSelection?.installationId.hasPrefix("official:") == true)
        func fact(_ kind: MusicPlaybackFact.Kind) {
            model.onPlayback(.init(attemptId: original.attemptId, occurrenceId: original.occurrenceId,
                resourceId: original.resourceId, kind: kind, position: 37, duration: 180))
        }
        fact(.playing); await model.flushPlaybackFacts()
        if paused { fact(.paused); await model.flushPlaybackFacts() }
        let history = try await native.playedSongs()
        let savedHistory = try MusicDocument(store.read(), runID: "renewal-receipt").history
        await scripts.enable()
        // Session invalidation changes no settings/pin; the real official renewal
        // is still eligible, while a legal same-tier peer is now discoverable.
        await native.sourceConfigurationChanged()
        if boundary == .expired {
            clock.advance(-120)
            await accounts.setResult(MusicRecoveryTestAccounts.renewedURL, expiresAt: clock.now().addingTimeInterval(60))
        }
        fact(.failed)
        try await gate.started()
        let returned = await service.receipt()
        let receipt = try XCTUnwrap(returned)
        XCTAssertEqual(receipt.candidate.startPosition, 37)
        if boundary == .expired {
            XCTAssertLessThan(try XCTUnwrap(receipt.candidate.expiresAt), Date().timeIntervalSince1970 * 1000)
        }
        if boundary == .cancelled { await model.togglePlayback() }
        if boundary == .revoked { grant.revoke() }
        if boundary == .revokedDuringPlay {
            player.onPlay = { if $0.url == MusicRecoveryTestAccounts.renewedURL { grant.revoke() } }
        }
        await gate.release()
        await model.flushPlaybackFacts()
        let evidence = await service.evidence(), calls = await scripts.calls(), counts = await accounts.counts()
        XCTAssertEqual(counts.refreshes, 1, "The real Native renewal must reach its return boundary")
        if boundary == .healthy {
            XCTAssertFalse(evidence.0.contains(receipt.candidate.attemptId))
            XCTAssertEqual(model.candidate?.url, MusicRecoveryTestAccounts.renewedURL)
            XCTAssertTrue(calls.isEmpty)
        } else {
            XCTAssertTrue(evidence.0.contains(receipt.candidate.attemptId), "Every returned but unaccepted renewal is released")
            do {
                _ = try await native.record(.init(attemptId: receipt.candidate.attemptId,
                    occurrenceId: receipt.candidate.occurrenceId, resourceId: receipt.candidate.resourceId,
                    kind: .playing, position: 37, duration: 180))
                XCTFail("Released renewal still owns facts")
            } catch { XCTAssertEqual(MusicServiceError.sanitized(error), .staleResult) }
            if boundary == .expired, intent != nil {
                XCTAssertEqual(model.activePlaybackSelection?.installationId, "peer")
                XCTAssertEqual(model.activePlaybackSelection?.quality, "flac")
                XCTAssertEqual(calls.map(\.quality), ["flac"])
            } else {
                XCTAssertTrue(calls.isEmpty)
                XCTAssertNil(model.activeNativeAttemptID)
            }
        }
        XCTAssertFalse(evidence.1.contains { $0.attemptId == receipt.candidate.attemptId }, "No unaccepted renewal facts")
        let afterHistory = try await native.playedSongs()
        XCTAssertEqual(afterHistory.map(\.id), history.map(\.id))
        XCTAssertEqual(afterHistory.map(\.track.songId), history.map(\.track.songId))
        // Each history read issues fresh ephemeral handles. Compare the entire
        // durable history instead: no bad media may alter its tracks or facts.
        XCTAssertEqual(try MusicDocument(store.read(), runID: "renewal-receipt").history, savedHistory)
        if boundary != .revokedDuringPlay {
            XCTAssertEqual(player.resources.filter { $0.url == MusicRecoveryTestAccounts.renewedURL }.count,
                           boundary == .healthy ? 1 : 0)
        } else {
            XCTAssertEqual(player.resources.filter { $0.url == MusicRecoveryTestAccounts.renewedURL }.count, 1)
            XCTAssertTrue(player.stoppedResources.contains(receipt.candidate.resourceId))
        }
        if boundary == .healthy || (boundary == .expired && intent != nil) {
            XCTAssertEqual(model.candidate?.startPosition, 37)
            if paused {
                XCTAssertTrue(player.pausedAttempts.contains { $0.id == model.activeNativeAttemptID })
            }
        }
        if boundary == .cancelled { XCTAssertEqual(model.phase, .paused); XCTAssertNil(model.error) }
        await model.close(); await native.close()
    }
}
