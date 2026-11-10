import Foundation
import XCTest
import MeowContracts
@testable import MeowPlayback
@testable import MeowMusicFeature
@testable import MeowMusicNativeService

// Synthetic media ports exercise public playback behavior without network I/O.
private actor SourceHandoffBoundaryGate {
    private var entered = false, released = false
    func wait() async throws {
        entered = true
        let end = ContinuousClock.now.advanced(by: .seconds(5))
        while !released {
            try Task.checkCancellation()
            guard ContinuousClock.now < end else { throw MusicServiceError.timeout }
            try await Task.sleep(for: .milliseconds(2))
        }
    }
    func started() async throws {
        let end = ContinuousClock.now.advanced(by: .seconds(3))
        while !entered {
            guard ContinuousClock.now < end else { throw MusicServiceError.timeout }
            try await Task.sleep(for: .milliseconds(2))
        }
    }
    func release() { released = true }
}

private actor SourceHandoffBoundaryScripts: MusicScriptResolving {
    private var enabled = true
    private var resolutions = 0
    private var expiresAt: Date?
    func setEnabled(_ value: Bool) { enabled = value }
    func setExpiration(_ value: Date?) { expiresAt = value }
    func resolutionCount() -> Int { resolutions }
    func sources() -> [MusicScriptSource] { ["a", "b"].map(snapshot) }
    private func snapshot(_ id: String) -> MusicScriptSource {
        .init(installationID: id, generation: 1, enabled: enabled, state: enabled ? .ready : .disabled, version: "1",
              contentHash: String(repeating: "a", count: 64), revisionHash: String(repeating: "a", count: 64),
              capabilities: [.init(siteID: "tx", title: id, actions: ["musicUrl"], qualities: ["128k", "flac"])])
    }
    func source(installationID: String) -> MusicScriptSource { snapshot(installationID) }
    func resolve(request: MusicScriptRequest) -> MusicScriptResult<MusicNativeAudio> {
        resolutions += 1
        return .init(value: .init(url: URL(string: "https://media.invalid/\(request.installationID).\(request.quality)")!,
                     expiresAt: expiresAt,
                     delivery: .init(quality: request.quality, evidence: .officialResponse, duration: 180)),
                     source: snapshot(request.installationID))
    }
    func lyrics(request: MusicScriptRequest) -> MusicScriptResult<MusicNativeLyrics> {
        .init(value: .init(text: ""), source: snapshot(request.installationID))
    }
}

// Preserve all existing synthetic account grant checks. Only expose the actual
// media quality, as the production Netease adapter does for a downgraded reply.
private struct SourceHandoffBoundaryAccounts: MusicNativeAccountProviding, MusicNativePlaybackRecoveryAuthorizing {
    let base: MusicRecoveryTestAccounts
    let renewalQuality: String?
    func nativeOptions(platform: MusicAccountPlatform, nativeID: String, sourceKey: String) async throws -> [MusicPlaybackOption] {
        try await base.nativeOptions(platform: platform, nativeID: nativeID, sourceKey: sourceKey)
    }
    func nativeRecoveryOptions(platform: MusicAccountPlatform, nativeID: String, selection: MusicPlaybackSelection) async throws -> [MusicPlaybackOption] {
        try await base.nativeRecoveryOptions(platform: platform, nativeID: nativeID, selection: selection)
    }
    func nativeResolve(platform: MusicAccountPlatform, nativeID: String, selection: MusicPlaybackSelection) async throws -> MusicNativeAudio {
        annotate(try await base.nativeResolve(platform: platform, nativeID: nativeID, selection: selection), quality: selection.quality)
    }
    func nativeRefreshAudio(platform: MusicAccountPlatform, nativeID: String, selection: MusicPlaybackSelection, rejectedURL: URL) async throws -> MusicNativeAudio {
        annotate(try await base.nativeRefreshAudio(platform: platform, nativeID: nativeID, selection: selection, rejectedURL: rejectedURL),
                 quality: renewalQuality)
    }
    private func annotate(_ audio: MusicNativeAudio, quality: String?) -> MusicNativeAudio {
        .init(url: audio.url, headers: audio.headers, expiresAt: audio.expiresAt, fileCachePolicy: audio.fileCachePolicy,
              delivery: quality.map { .init(quality: $0, evidence: .officialResponse, duration: 180) },
              playbackAuthorization: audio.playbackAuthorization)
    }
    func nativeRecommendations(platform: MusicAccountPlatform, kind: String, cursor: String?) throws -> MusicNativeTrackPage {
        throw MusicServiceError.unsupported
    }
    func nativeCollections(platform: MusicAccountPlatform, cursor: String?) throws -> MusicAccountCollectionPage {
        throw MusicServiceError.unsupported
    }
    func nativeTracks(platform: MusicAccountPlatform, collectionID: String, cursor: String?) throws -> MusicNativeTrackPage {
        throw MusicServiceError.unsupported
    }
}

@MainActor private final class SourceHandoffBoundaryPlayer: MusicPlaybackHandoffDriving {
    var resources: [PlaybackResource] = [], preparations: [PlaybackResource] = []
    var synchronizationFailures = 0, synchronizations = 0, commits = 0, stops = 0, cancels = 0, pauses = 0
    var synchronizationError: (any Error)?
    var duration: Double? = 180
    var onSynchronize: (() -> Void)?
    var waitUntil: Date?
    var gate: SourceHandoffBoundaryGate?
    var samplePosition = 0.0
    var onPlay: ((MusicPlayerAttempt, PlaybackResource) -> Void)?
    var onStop: ((MusicPlayerAttempt) -> Void)?
    func play(_ resource: PlaybackResource) -> MusicPlayerAttempt {
        resources.append(resource)
        let attempt = MusicPlayerAttempt(id: UUID(), resourceID: resource.id)
        onPlay?(attempt, resource)
        return attempt
    }
    func preparePlayback(_ resource: PlaybackResource, autoplay: Bool) async throws -> MusicPreparedPlayback {
        .init(duration: self.duration, synchronize: {}, commit: {
            self.samplePosition = resource.startPosition
            return self.play(resource)
        }, cancel: {})
    }
    func prepareReplacement(_ resource: PlaybackResource, replacing: MusicPlayerAttempt) async throws -> MusicPreparedPlayback {
        preparations.append(resource)
        if let gate { self.gate = nil; try await gate.wait() }
        return .init(duration: self.duration, synchronize: {
            self.synchronizations += 1
            self.onSynchronize?()
            if let waitUntil = self.waitUntil {
                try await Task.sleep(for: .seconds(max(0, waitUntil.timeIntervalSinceNow)))
            }
            if let error = self.synchronizationError { throw error }
            if self.synchronizationFailures > 0 {
                self.synchronizationFailures -= 1
                throw NativePlaybackError.seekFailed
            }
        }, commit: {
            self.commits += 1
            return self.play(resource)
        }, cancel: { self.cancels += 1 })
    }
    func stop(_ attempt: MusicPlayerAttempt) { stops += 1; onStop?(attempt); samplePosition = 0 }
    func pause(_ attempt: MusicPlayerAttempt) { pauses += 1 }
    func resume(_ attempt: MusicPlayerAttempt) {}
    func seek(_ position: Double, attempt: MusicPlayerAttempt) -> Bool { true }
}

@MainActor final class MusicSourceHandoffBoundaryTests: XCTestCase {
    private func setup(_ service: NativeMusicService, player: SourceHandoffBoundaryPlayer) async throws -> MusicViewModel {
        let initial = try await service.state()
        _ = try await service.updateSettings(.init(ifRevision: initial.settingsRevision, requestQuality: "128k"))
        let model = MusicViewModel(service: service, player: player)
        await model.load(); await model.prepareCurrent()
        try await fact(model, .playing)
        XCTAssertEqual(model.activePlaybackSelection?.quality, "128k")
        return model
    }
    private func fact(_ model: MusicViewModel, _ kind: MusicPlaybackFact.Kind) async throws {
        let c = try XCTUnwrap(model.candidate)
        model.onPlayback(.init(attemptId: c.attemptId, occurrenceId: c.occurrenceId,
                               resourceId: c.resourceId, kind: kind, position: 23, duration: 180))
        await model.flushPlaybackFacts()
    }
    func testOfficialRenewalCannotDowngradeLockedQuality() async throws {
        try await renewal(actual: "128k")
    }
    func testSameQualityOfficialRenewalControl() async throws {
        try await renewal(actual: "flac")
    }
    func testUnknownOfficialRenewalQualityRemainsUnknown() async throws {
        try await renewal(actual: nil)
    }
    func testOfficialRenewalCannotDowngradeExactResult() async throws {
        try await renewal(actual: "128k", exact: true)
    }
    func testAutomaticRecoveryRejectsDowngradedRenewalBeforeSessionFallback() async throws {
        try await renewal(actual: "128k", automatic: true)
    }
    func testOfficialRenewalAuthorityFailuresCannotStartAnotherSource() async throws {
        for failure in [MusicServiceError.authRequired, .sessionExpired, .verificationRequired, .staleAccess,
                        .accessUnavailable, .targetChanged, .staleRevision, .storageConflict, .cancelled] {
            try await renewalFailure(failure, allowsPeer: false)
        }
    }
    func testOfficialRenewalProviderAndContentFailuresAllowSameTierPeer() async throws {
        for failure in [MusicServiceError.connectionFailed, .rateLimited, .audioUnavailable] {
            try await renewalFailure(failure, allowsPeer: true)
        }
    }
    func testOfficialFailureWithoutCurrentRenewalPinUsesCommittedQualitySession() async throws {
        for hasAcceptedFact in [false, true] {
            let clock = MusicRecoveryTestClock()
            let accounts = MusicRecoveryTestAccounts(platform: .qq, nativeID: "101", clock: clock)
            let scripts = SourceHandoffBoundaryScripts()
            await scripts.setEnabled(false)
            let song = try MusicModel.normalize(MusicNativeCandidateDiscoveryFixture.track("tx", id: "101"))
            let service = try await NativeMusicFixture.service(MusicNativeCandidateDiscoveryFixture.store(song: song),
                scripts: scripts, accounts: accounts, clock: { clock.now() })
            let player = SourceHandoffBoundaryPlayer(), model = MusicViewModel(service: service, player: player)
            await model.load(); await model.switchQuality("flac")
            XCTAssertEqual(model.activePlaybackSelection?.installationId, "official:recovery-synthetic")
            if hasAcceptedFact {
                try await fact(model, .playing)
                await model.updateSettings(.init(ifRevision: model.state.settingsRevision, lyricsEnabled: false))
            }
            await scripts.setEnabled(true)
            await service.sourceConfigurationChanged()
            try await fact(model, .failed)
            let counts = await accounts.counts()
            XCTAssertEqual(counts.refreshes, 0, "No committed pin, or a known policy change, skips optional URL renewal")
            XCTAssertEqual(model.activePlaybackSelection?.quality, "flac")
            XCTAssertFalse(model.activePlaybackSelection?.installationId.hasPrefix("official:") ?? true)
            await model.close(); await service.close()
        }
    }
    private func renewalFailure(_ failure: MusicServiceError, allowsPeer: Bool) async throws {
        let clock = MusicRecoveryTestClock()
        let accounts = MusicRecoveryTestAccounts(platform: .qq, nativeID: "101", clock: clock)
        let scripts = SourceHandoffBoundaryScripts()
        await scripts.setEnabled(false)
        let song = try MusicModel.normalize(MusicNativeCandidateDiscoveryFixture.track("tx", id: "101"))
        let service = try await NativeMusicFixture.service(MusicNativeCandidateDiscoveryFixture.store(song: song),
            scripts: scripts, accounts: accounts, clock: { clock.now() })
        let player = SourceHandoffBoundaryPlayer(), model = MusicViewModel(service: service, player: player)
        await model.load(); await model.prepareCurrent(); try await fact(model, .playing)
        XCTAssertEqual(model.activePlaybackSelection?.installationId, "official:recovery-synthetic")
        XCTAssertEqual(model.activePlaybackSelection?.quality, "flac")
        let opened = player.resources.count
        await scripts.setEnabled(true)
        await service.sourceConfigurationChanged()
        await accounts.failRefresh(failure)
        try await fact(model, .failed)
        let calls = await scripts.resolutionCount(), counts = await accounts.counts()
        XCTAssertEqual(counts.refreshes, 1)
        if allowsPeer {
            XCTAssertGreaterThan(calls, 0)
            XCTAssertEqual(player.resources.count, opened + 1)
            XCTAssertFalse(model.activePlaybackSelection?.installationId.hasPrefix("official:") ?? true)
            XCTAssertEqual(model.activePlaybackSelection?.quality, "flac")
        } else {
            XCTAssertEqual(calls, 0, "Authority failures cannot authorize requests to another source")
            XCTAssertEqual(player.resources.count, opened)
            XCTAssertEqual(model.phase, failure == .cancelled ? .paused : .failed)
            if failure == .cancelled { XCTAssertNil(model.error) }
            else { XCTAssertNotNil(model.error) }
        }
        let exclusions = try await service.sourceExclusions()
        XCTAssertTrue(exclusions.isEmpty)
        await model.close(); await service.close()
    }
    private func renewal(actual: String?, exact: Bool = false, automatic: Bool = false) async throws {
        let clock = MusicRecoveryTestClock()
        let accounts = MusicRecoveryTestAccounts(platform: .qq, nativeID: "101", clock: clock)
        await accounts.setResolveURL(URL(string: "https://media.invalid/original-128k")!, quality: "128k")
        await accounts.setResolveURL(URL(string: "https://media.invalid/original-flac")!, quality: "flac")
        let song = try MusicModel.normalize(MusicNativeCandidateDiscoveryFixture.track("tx", id: "101"))
        let service = try await NativeMusicFixture.service(MusicNativeCandidateDiscoveryFixture.store(song: song),
            accounts: SourceHandoffBoundaryAccounts(base: accounts, renewalQuality: actual), clock: { clock.now() })
        let player = SourceHandoffBoundaryPlayer(), model = try await setup(service, player: player)
        await model.switchQuality("flac"); try await fact(model, .playing)
        if exact {
            let stops = player.stops, commits = player.commits
            await model.choose(try XCTUnwrap(model.activePlaybackSelection))
            XCTAssertEqual(player.stops, stops, "Public exact selection uses the same seamless handoff")
            XCTAssertEqual(player.commits, commits + 1)
            try await fact(model, .playing)
        } else if automatic {
            await model.updateSettings(.init(ifRevision: model.state.settingsRevision, requestQuality: "flac"))
            await model.prepareCurrent(); try await fact(model, .playing)
        }
        XCTAssertEqual(model.activePlaybackSelection?.quality, "flac")
        XCTAssertEqual(model.candidate?.delivery?.quality, "flac")
        try await fact(model, .failed)
        let counts = await accounts.counts()
        let openedRenewal = player.resources.contains { $0.url == MusicRecoveryTestAccounts.renewedURL }
        XCTAssertEqual(counts.refreshes, 1)
        if actual == nil {
            XCTAssertTrue(openedRenewal)
            XCTAssertNil(model.candidate?.delivery, "A requested tier is not measured quality evidence")
        } else if actual == "flac" {
            XCTAssertTrue(openedRenewal)
            XCTAssertEqual(model.candidate?.delivery?.quality, "flac")
        } else {
            XCTAssertFalse(openedRenewal, "A renewed URL of the wrong actual tier must not reach the player")
            if !automatic {
                XCTAssertNotEqual(model.candidate?.delivery?.quality, "128k", "Manual quality intent cannot silently degrade on renewal")
            } else {
                XCTAssertEqual(model.activePlaybackSelection?.quality, "128k")
                XCTAssertEqual(model.candidate?.url.lastPathComponent, "original-128k")
            }
        }
        XCTAssertEqual(model.state.requestQuality, automatic ? "flac" : "128k")
        await model.close(); await service.close()
    }
    func testFinalSynchronizationFailureContinuesSameTier() async throws {
        try await synchronization(failFirst: true)
    }
    func testSuccessfulSynchronizationControl() async throws {
        try await synchronization(failFirst: false)
    }
    func testFinalDurationRejectsLateMismatchWithoutRetiringDonor() async throws {
        let initialDurations: [Double?] = [nil, 180]
        for initial in initialDurations {
            let scripts = SourceHandoffBoundaryScripts()
            let service = try await NativeMusicFixture.service(MusicNativeCandidateDiscoveryFixture.store(), scripts: scripts)
            let player = SourceHandoffBoundaryPlayer(), model = try await setup(service, player: player)
            let donor = model.activeResourceID
            player.duration = initial
            player.onSynchronize = { player.duration = 191 }
            await model.switchQuality("flac")
            XCTAssertEqual(player.commits, 0)
            XCTAssertEqual(player.stops, 0)
            XCTAssertEqual(model.activeResourceID, donor)
            XCTAssertEqual(model.activePlaybackSelection?.quality, "128k")
            XCTAssertEqual(player.cancels, 2, "Both same-tier preparations are rejected, not the healthy donor")
            await model.close(); await service.close()
        }
    }
    func testFinalDurationWithinBoundaryOrStillUnknownAllowsHandoff() async throws {
        let finalDurations: [Double?] = [190, nil]
        for final in finalDurations {
            let scripts = SourceHandoffBoundaryScripts()
            let service = try await NativeMusicFixture.service(MusicNativeCandidateDiscoveryFixture.store(), scripts: scripts)
            let player = SourceHandoffBoundaryPlayer(), model = try await setup(service, player: player)
            player.duration = nil
            player.onSynchronize = { player.duration = final }
            await model.switchQuality("flac")
            XCTAssertEqual(player.commits, 1)
            XCTAssertEqual(player.stops, 0)
            XCTAssertEqual(model.activePlaybackSelection?.quality, "flac")
            await model.close(); await service.close()
        }
    }
    func testURLExpiringDuringFinalSynchronizationKeepsDonor() async throws {
        let scripts = SourceHandoffBoundaryScripts()
        let service = try await NativeMusicFixture.service(MusicNativeCandidateDiscoveryFixture.store(),
            scripts: scripts, clock: { Date() })
        let player = SourceHandoffBoundaryPlayer(), model = try await setup(service, player: player)
        let donor = model.activeResourceID, previous = try XCTUnwrap(model.activePlaybackSelection)
        // The wait is tied to this actual URL deadline, not a guess about actor scheduling.
        let expiry = Date().addingTimeInterval(3)
        await scripts.setExpiration(expiry)
        player.waitUntil = expiry.addingTimeInterval(0.05)
        await model.choose(.init(installationId: previous.installationId, generation: previous.generation,
            siteId: previous.siteId, sourceKey: previous.sourceKey, quality: "flac"))
        XCTAssertEqual(player.synchronizations, 1, "The URL must be admitted before expiring during final synchronization")
        XCTAssertEqual(player.commits, 0)
        XCTAssertEqual(player.stops, 0)
        XCTAssertEqual(model.activeResourceID, donor)
        await model.close(); await service.close()
    }
    func testActivationExpiryContinuesSameTierWithoutRetiringDonor() async throws {
        try await activationExpiry(exact: false)
    }
    func testActivationExpiryOfExactResultDoesNotSubstitutePeer() async throws {
        try await activationExpiry(exact: true)
    }
    private func activationExpiry(exact: Bool) async throws {
        let clock = MusicRecoveryTestClock(), scripts = SourceHandoffBoundaryScripts()
        let service = try await NativeMusicFixture.service(MusicNativeCandidateDiscoveryFixture.store(),
            scripts: scripts, clock: { clock.now() })
        let player = SourceHandoffBoundaryPlayer(), model = try await setup(service, player: player)
        let donor = model.activeResourceID, previous = try XCTUnwrap(model.activePlaybackSelection)
        await scripts.setExpiration(clock.now().addingTimeInterval(60))
        let gate = SourceHandoffBoundaryGate(); player.gate = gate
        let task = Task {
            if exact {
                await model.choose(.init(installationId: previous.installationId, generation: previous.generation,
                    siteId: previous.siteId, sourceKey: previous.sourceKey, quality: "flac"))
            } else { await model.switchQuality("flac") }
        }
        try await gate.started()
        XCTAssertEqual(player.commits, 0)
        clock.advance(61)
        await scripts.setExpiration(nil)
        await gate.release(); await task.value
        XCTAssertEqual(player.preparations.count, exact ? 1 : 2)
        XCTAssertEqual(player.cancels, 1)
        XCTAssertEqual(player.commits, exact ? 0 : 1)
        XCTAssertEqual(player.stops, 0)
        XCTAssertEqual(model.activePlaybackSelection?.quality, exact ? "128k" : "flac")
        if exact { XCTAssertEqual(model.activeResourceID, donor) }
        else { XCTAssertNotEqual(model.activeResourceID, donor) }
        await model.close(); await service.close()
    }
    func testInitialActivationExpiryContinuesSameTierBeforePlaying() async throws {
        let clock = MusicRecoveryTestClock(), scripts = SourceHandoffBoundaryScripts()
        await scripts.setExpiration(clock.now().addingTimeInterval(60))
        let service = try await NativeMusicFixture.service(MusicNativeCandidateDiscoveryFixture.store(),
            scripts: scripts, clock: { clock.now() })
        let player = SourceHandoffBoundaryPlayer(), model = MusicViewModel(service: service, player: player)
        await model.load()
        let session = try await service.sourceSession(occurrenceID: "entry-1", expectedQueueRevision: model.state.revision)
        let sourceKey = try XCTUnwrap(model.current?.track.sources.first?.sourceKey)
        let expiring = try await session.match(intent: .exact(.init(
            installationId: "a", generation: 1, siteId: "tx", sourceKey: sourceKey, quality: "flac")))
        await service.discardSource(attemptID: expiring.candidate.attemptId)
        // Only A is expiring. B must remain a healthy same-tier peer even when
        // both providers could otherwise finish before first-winner cancellation.
        await scripts.setExpiration(nil)
        let updates = await session.updates()
        var initialUpdate = updates.makeAsyncIterator()
        let seeded = try await initialUpdate.next()
        XCTAssertEqual(seeded?.sources.map(\.selection.installationId), ["a"])
        let gate = SourceHandoffBoundaryGate()
        // Hold the existing fact-drain boundary, not matching or its native
        // admission. No native player has been created for this song yet.
        model.eventTail = Task { try? await gate.wait() }
        let task = Task { await model.prepareCurrent() }
        try await MusicCandidateDiscoveryDeadline.run(seconds: 3, timing: .init()) {
            for try await snapshot in updates where !snapshot.isLoading && !snapshot.sources.isEmpty { return }
            throw MusicServiceError.closed
        }
        XCTAssertTrue(player.resources.isEmpty)
        clock.advance(61)
        await gate.release(); await task.value
        XCTAssertEqual(player.resources.count, 1, "Only a healthy peer is handed to the native player")
        XCTAssertEqual(model.activePlaybackSelection?.installationId, "b")
        XCTAssertEqual(model.activePlaybackSelection?.quality, "flac")
        XCTAssertTrue(player.preparations.isEmpty)
        XCTAssertEqual(player.stops, 0)
        let history = try await service.playedSongs()
        XCTAssertTrue(history.isEmpty, "Preparing a candidate is not an actual playing fact")
        await model.close(); await service.close()
    }
    func testInitialFinalAcceptanceRejectsExpiredURLAndContinuesSameTier() async throws {
        let clock = MusicRecoveryTestClock(), scripts = SourceHandoffBoundaryScripts()
        // Freeze the native admission clock before the UI's current time to
        // deterministically model expiry across the actor-return boundary.
        clock.advance(Date().timeIntervalSince(clock.now()) - 120)
        await scripts.setExpiration(clock.now().addingTimeInterval(60))
        let service = try await NativeMusicFixture.service(MusicNativeCandidateDiscoveryFixture.store(),
            scripts: scripts, clock: { clock.now() })
        let player = SourceHandoffBoundaryPlayer(), model = MusicViewModel(service: service, player: player)
        await model.load()
        let key = try XCTUnwrap(model.current?.track.sources.first?.sourceKey)
        let session = try await service.sourceSession(occurrenceID: "entry-1", expectedQueueRevision: model.state.revision)
        let seeded = try await session.match(intent: .exact(.init(
            installationId: "a", generation: 1, siteId: "tx", sourceKey: key, quality: "flac")))
        XCTAssertLessThan(try XCTUnwrap(seeded.candidate.expiresAt), Date().timeIntervalSince1970 * 1000)
        await service.discardSource(attemptID: seeded.candidate.attemptId)
        await scripts.setExpiration(nil)
        await model.prepareCurrent()
        XCTAssertEqual(player.resources.count, 1)
        XCTAssertEqual(model.activePlaybackSelection?.installationId, "b")
        XCTAssertEqual(model.activePlaybackSelection?.quality, "flac")
        XCTAssertEqual(player.stops, 0)
        await model.close(); await service.close()
    }
    func testFinalSynchronizationCancellationAndAuthorityFailureDoNotConsumePeers() async throws {
        let failures: [any Error] = [MusicServiceError.cancelled, MusicServiceError.staleAccess,
            MusicServiceError.staleResult, MusicServiceError.targetChanged, MusicServiceError.qualityDisabled,
            PlaybackNetworkError.cancelled, NativePlaybackError.audioSessionUnavailable, NativePlaybackError.mediaServicesReset]
        for failure in failures {
            let scripts = SourceHandoffBoundaryScripts()
            let service = try await NativeMusicFixture.service(MusicNativeCandidateDiscoveryFixture.store(), scripts: scripts)
            let player = SourceHandoffBoundaryPlayer(), model = try await setup(service, player: player)
            let donor = model.activeResourceID
            player.synchronizationError = failure
            await model.switchQuality("flac")
            XCTAssertEqual(player.preparations.count, 1)
            XCTAssertEqual(player.commits, 0)
            XCTAssertEqual(player.stops, 0)
            XCTAssertEqual(player.cancels, 1)
            XCTAssertEqual(model.activeResourceID, donor)
            XCTAssertEqual(model.activePlaybackSelection?.quality, "128k")
            if failure as? MusicServiceError == .cancelled || failure as? PlaybackNetworkError == .cancelled { XCTAssertNil(model.error) }
            else { XCTAssertNotNil(model.error) }
            await model.close(); await service.close()
        }
    }
    private func synchronization(failFirst: Bool) async throws {
        let scripts = SourceHandoffBoundaryScripts()
        let service = try await NativeMusicFixture.service(MusicNativeCandidateDiscoveryFixture.store(), scripts: scripts)
        let player = SourceHandoffBoundaryPlayer(), model = try await setup(service, player: player)
        let old = model.activeResourceID
        player.synchronizationFailures = failFirst ? 1 : 0
        await model.switchQuality("flac")
        XCTAssertEqual(player.preparations.count, failFirst ? 2 : 1, "A genuinely bad final media seek must allow the next same-tier candidate")
        XCTAssertEqual(player.commits, 1)
        XCTAssertEqual(model.activePlaybackSelection?.quality, "flac")
        XCTAssertNotEqual(model.activeResourceID, old)
        XCTAssertEqual(player.stops, 0, "The healthy donor must not be stopped while searching/preparing")
        XCTAssertEqual(player.cancels, failFirst ? 1 : 0)
        await model.close(); await service.close()
    }
    func testSettingsCancellationPreservesCommittedQualityIntent() async throws {
        try await settingsCancels(pending: true)
    }
    func testSettingsWithoutPendingSelectionControl() async throws {
        try await settingsCancels(pending: false)
    }
    private func settingsCancels(pending: Bool) async throws {
        let scripts = SourceHandoffBoundaryScripts()
        let service = try await NativeMusicFixture.service(MusicNativeCandidateDiscoveryFixture.store(), scripts: scripts)
        let player = SourceHandoffBoundaryPlayer(), model = try await setup(service, player: player)
        await model.switchQuality("flac"); try await fact(model, .playing)
        XCTAssertEqual(model.activePlaybackSelection?.quality, "flac")
        let old = model.activeResourceID, commitsBefore = player.commits
        if pending {
            let gate = SourceHandoffBoundaryGate(); player.gate = gate
            let task = Task { await model.switchQuality("128k") }
            try await gate.started()
            await model.updateSettings(.init(ifRevision: model.state.settingsRevision, lyricsEnabled: false))
            await gate.release(); await task.value
        } else {
            await model.updateSettings(.init(ifRevision: model.state.settingsRevision, lyricsEnabled: false))
        }
        XCTAssertEqual(model.activeResourceID, old, "Unrelated settings retain the healthy committed stream")
        XCTAssertEqual(player.commits, commitsBefore)
        XCTAssertEqual(player.stops, 0)
        try await fact(model, .failed)
        XCTAssertEqual(model.activePlaybackSelection?.quality, "flac", "Lyrics/settings cancellation is not authorization to lose the existing manual tier")
        XCTAssertEqual(model.state.requestQuality, "128k", "The global default must not be rewritten to mask the failure")
        await model.close(); await service.close()
    }

    private func feedbackModel(_ service: NativeMusicService, player: SourceHandoffBoundaryPlayer,
                               clock: NativeFeedbackClock, waitForReceipt: Bool = true) async throws -> MusicViewModel {
        let state = try await service.state()
        _ = try await service.setPlaybackFeedback(platform: .qq, enabled: true, expectedRevision: state.settingsRevision)
        let model = MusicViewModel(service: service, player: player)
        player.onPlay = { [weak model, weak player] native, resource in
            model?.onPlaybackEvent(.init(attempt: .init(id: native.id, owner: "music", resourceID: resource.id),
                kind: .played, snapshot: .init(owner: "music", resourceID: resource.id, phase: .playing,
                    position: player?.samplePosition ?? 0, duration: 180),
                wantsPlayback: true, observedAt: clock.now(), occurredAt: 1000 + clock.now()))
        }
        player.onStop = { [weak model, weak player] native in
            model?.onPlaybackEvent(.init(attempt: .init(id: native.id, owner: "music", resourceID: native.resourceID),
                kind: .stopped, snapshot: .init(owner: "music", resourceID: native.resourceID, phase: .playing,
                    position: player?.samplePosition ?? 0, duration: 180),
                wantsPlayback: false, observedAt: clock.now(), occurredAt: 1000 + clock.now()))
        }
        await model.load()
        await model.updateSettings(.init(ifRevision: model.state.settingsRevision, requestQuality: "128k"))
        await model.prepareCurrent(); await model.flushPlaybackFacts()
        let controller = await service.playbackFeedback
        if waitForReceipt { await controller.flush() }
        return model
    }
    private func feedbackProgress(_ model: MusicViewModel, player: SourceHandoffBoundaryPlayer,
                                  clock: NativeFeedbackClock, at: Double, position: Double,
                                  kind: PlaybackEvent.Kind = .progress) async throws {
        let candidate = try XCTUnwrap(model.candidate), native = try XCTUnwrap(model.nativeAttempt)
        clock.set(at); player.samplePosition = position
        model.onPlaybackEvent(.init(attempt: .init(id: native.id, owner: "music", resourceID: candidate.resourceId),
            kind: kind, snapshot: .init(owner: "music", resourceID: candidate.resourceId,
                phase: kind == .ended ? .ended : .playing, position: position, duration: 180),
            wantsPlayback: kind != .ended, observedAt: at, occurredAt: 1000 + at))
        await model.flushPlaybackFacts()
    }
    func testFeedbackUsesRealFeatureHandoffAndFailedPreparationDoesNotSplitListen() async throws {
        let accounts = NativeFeedbackAccounts(), clock = NativeFeedbackClock()
        await accounts.configure(result: .confirmed)
        let service = try await NativeMusicFixture.service(MusicNativeCandidateDiscoveryFixture.store(),
            scripts: SourceHandoffBoundaryScripts(), accounts: accounts, monotonicClock: { clock.now() })
        let player = SourceHandoffBoundaryPlayer()
        let model = try await feedbackModel(service, player: player, clock: clock)
        let initial = model.activeNativeAttemptID
        try await feedbackProgress(model, player: player, clock: clock, at: 3, position: 3)
        await model.switchQuality("flac"); await model.flushPlaybackFacts()
        XCTAssertNotEqual(model.activeNativeAttemptID, initial)
        try await feedbackProgress(model, player: player, clock: clock, at: 5, position: 5)
        let healthy = model.activeNativeAttemptID
        player.duration = 230
        await model.switchQuality("128k"); await model.flushPlaybackFacts()
        XCTAssertEqual(model.activeNativeAttemptID, healthy)
        let before = await accounts.recorded(); XCTAssertEqual(before.map(\.event), [.start])
        try await feedbackProgress(model, player: player, clock: clock, at: 7, position: 7)
        clock.set(8); player.samplePosition = 8
        await model.close()
        let controller = await service.playbackFeedback
        await controller.flush()
        let rows = await accounts.recorded()
        XCTAssertEqual(rows.map(\.event), [.start, .end])
        XCTAssertEqual(rows.first?.instanceID, rows.last?.instanceID)
        XCTAssertEqual(rows.last?.listened, 8); XCTAssertEqual(rows.last?.reason, .interrupt)
        XCTAssertEqual(rows.last?.position, 8)
        XCTAssertEqual(accounts.releases.count(), 1)
        await service.close()
    }
    func testPreparedCloseStopsSynchronouslyAndConcurrentCloseDrainsFactsNotNetwork() async throws {
        let accounts = NativeFeedbackAccounts(), clock = NativeFeedbackClock(), gate = NativeMusicGate()
        await accounts.configure(result: .confirmed, gate: gate)
        let service = try await NativeMusicFixture.service(MusicNativeCandidateDiscoveryFixture.store(),
            scripts: SourceHandoffBoundaryScripts(), accounts: accounts, monotonicClock: { clock.now() })
        let player = SourceHandoffBoundaryPlayer()
        let model = try await feedbackModel(service, player: player, clock: clock, waitForReceipt: false)
        await gate.started()
        try await feedbackProgress(model, player: player, clock: clock, at: 2, position: 2)
        clock.set(3); player.samplePosition = 3
        let stops = player.stops
        model.prepareForClose()
        XCTAssertEqual(player.stops, stops + 1)
        XCTAssertFalse(model.canNavigate)
        model.prepareForClose()
        XCTAssertEqual(player.stops, stops + 1)
        let closed = expectation(description: "Both model close callers drain local exit facts")
        closed.expectedFulfillmentCount = 2
        let first = Task { await model.close(); closed.fulfill() }
        let second = Task { await model.close(); closed.fulfill() }
        await fulfillment(of: [closed], timeout: 3)
        let settled = model.isClosed
        let positionBeforeRevoke = try await service.state().position
        await gate.release(); await first.value; await second.value
        let controller = await service.playbackFeedback
        await controller.flush()
        let rows = await accounts.recorded()
        XCTAssertTrue(settled); XCTAssertEqual(positionBeforeRevoke, 3)
        XCTAssertEqual(rows.map(\.event), [.start, .end])
        XCTAssertEqual(rows.first?.instanceID, rows.last?.instanceID)
        XCTAssertEqual(rows.last?.listened, 3); XCTAssertEqual(rows.last?.position, 3)
        XCTAssertEqual(rows.last?.occurredAt, 1003); XCTAssertEqual(rows.last?.reason, .interrupt)
        await model.close()
        XCTAssertEqual(player.stops, stops + 1); XCTAssertEqual(accounts.releases.count(), 1)
        await service.close()
    }

    func testFeatureDuplicateOccurrenceAndNaturalReplayCreateSeparateInstances() async throws {
        let accounts = NativeFeedbackAccounts(), clock = NativeFeedbackClock()
        await accounts.configure(result: .confirmed)
        let store = try MusicNativeCandidateDiscoveryFixture.store()
        let service = try await NativeMusicFixture.service(store, scripts: SourceHandoffBoundaryScripts(),
            accounts: accounts, monotonicClock: { clock.now() })
        let first = try await service.queue(), ref = try XCTUnwrap(first.entries.first?.track.ref)
        _ = try await service.changeQueue(.replace([ref, ref], index: 0), expectedRevision: first.revision)
        let player = SourceHandoffBoundaryPlayer(), model = try await feedbackModel(service, player: player, clock: clock)
        let controller = await service.playbackFeedback
        await model.setMode(.single)
        let original = model.activeOccurrenceID
        try await feedbackProgress(model, player: player, clock: clock, at: 2, position: 2)
        clock.set(3); player.samplePosition = 0
        await model.next(); await model.flushPlaybackFacts()
        await controller.flush()
        let duplicate = model.activeOccurrenceID
        XCTAssertNotEqual(original, duplicate)
        try await feedbackProgress(model, player: player, clock: clock, at: 4, position: 1)
        let selectedNative = model.activeNativeAttemptID
        clock.set(5); player.samplePosition = 0
        await model.selectOccurrence(try XCTUnwrap(duplicate)); await model.flushPlaybackFacts()
        await controller.flush()
        XCTAssertEqual(model.activeOccurrenceID, duplicate)
        XCTAssertNotEqual(model.activeNativeAttemptID, selectedNative)
        let native = model.activeNativeAttemptID
        // Natural completion in single mode replays the same queue occurrence.
        try await feedbackProgress(model, player: player, clock: clock, at: 7, position: 2, kind: .ended)
        player.samplePosition = 0
        await model.flushNavigation(); await model.flushPlaybackFacts()
        await controller.flush()
        XCTAssertEqual(model.activeOccurrenceID, duplicate)
        XCTAssertNotEqual(model.activeNativeAttemptID, native)
        clock.set(8); player.samplePosition = 1
        await model.close()
        await controller.flush()
        let rows = await accounts.recorded(), starts = rows.filter { $0.event == .start }
        XCTAssertEqual(starts.count, 4)
        XCTAssertEqual(Set(starts.map(\.instanceID)).count, 4)
        XCTAssertEqual(rows.filter { $0.event == .end }.map(\.reason), [.ui, .ui, .playend, .interrupt])
        await service.close()
    }

    func testFeatureManualNextFreezesUIBeforeDelayedStartReceipt() async throws {
        let accounts = NativeFeedbackAccounts(), clock = NativeFeedbackClock(), gate = NativeMusicGate()
        await accounts.configure(result: .confirmed, gate: gate)
        let service = try await NativeMusicFixture.service(MusicNativeCandidateDiscoveryFixture.store(),
            scripts: SourceHandoffBoundaryScripts(), accounts: accounts, monotonicClock: { clock.now() })
        let first = try await service.queue(), ref = try XCTUnwrap(first.entries.first?.track.ref)
        _ = try await service.changeQueue(.replace([ref, ref], index: 0), expectedRevision: first.revision)
        let player = SourceHandoffBoundaryPlayer()
        let model = try await feedbackModel(service, player: player, clock: clock, waitForReceipt: false)
        await gate.started()
        let before = await accounts.recorded(), original = try XCTUnwrap(before.first?.instanceID)
        try await feedbackProgress(model, player: player, clock: clock, at: 2, position: 2)
        await accounts.configure(result: .confirmed)
        clock.set(3); player.samplePosition = 0
        await model.next(); await model.flushPlaybackFacts()
        await gate.release()
        let controller = await service.playbackFeedback
        await controller.flush()
        let rows = await accounts.recorded()
        let end = try XCTUnwrap(rows.first { $0.event == .end && $0.instanceID == original })
        XCTAssertEqual(rows.filter { $0.event == .start }.count, 2)
        XCTAssertEqual(end.reason, .ui); XCTAssertEqual(end.listened, 2); XCTAssertEqual(end.occurredAt, 1002)
        await model.close(); await controller.flush(); await service.close()
    }

    func testCommittedManualNextDrainsAcceptedProgressBeforeFreezingOldListen() async throws {
        try await queuedFeedbackAtCommittedNext(revoking: false)
    }

    func testRevocationRejectsAcceptedProgressAtCommittedNextWithoutEnd() async throws {
        try await queuedFeedbackAtCommittedNext(revoking: true)
    }

    private func queuedFeedbackAtCommittedNext(revoking: Bool) async throws {
        let accounts = NativeFeedbackAccounts(), clock = NativeFeedbackClock(), gate = NativeMusicGate()
        await accounts.configure(result: .confirmed)
        let service = try await NativeMusicFixture.service(MusicNativeCandidateDiscoveryFixture.store(),
            scripts: SourceHandoffBoundaryScripts(), accounts: accounts, monotonicClock: { clock.now() })
        let first = try await service.queue(), ref = try XCTUnwrap(first.entries.first?.track.ref)
        _ = try await service.changeQueue(.replace([ref, ref], index: 0), expectedRevision: first.revision)
        let player = SourceHandoffBoundaryPlayer(), model = try await feedbackModel(service, player: player, clock: clock)
        let controller = await service.playbackFeedback
        let before = await accounts.recorded(), original = try XCTUnwrap(before.first?.instanceID)
        let candidate = try XCTUnwrap(model.candidate), native = try XCTUnwrap(model.nativeAttempt)
        // Pin the window after navigation's initial drain: another accepted
        // native observation is now waiting behind the preceding local fact.
        model.eventTail = Task { await gate.wait() }
        await gate.started()
        clock.set(2); player.samplePosition = 2
        model.onPlaybackEvent(.init(attempt: .init(id: native.id, owner: "music", resourceID: candidate.resourceId),
            kind: .progress, snapshot: .init(owner: "music", resourceID: candidate.resourceId,
                phase: .playing, position: 2, duration: 180), wantsPlayback: true, observedAt: 2, occurredAt: 1002))
        model.playbackState.isChangingQueue = true
        let state = try await service.state()
        let next = try await service.advance(.next, reason: .manual, expectedRevision: state.revision)
        XCTAssertNotEqual(try XCTUnwrap(next.selectedId), candidate.occurrenceId)
        model.invalidateSelection(stopMedia: true, reason: .ui)
        model.playbackState.state = next.state
        let pending = await accounts.recorded()
        XCTAssertEqual(pending.map(\.event), [.start], "Queue CAS cannot freeze ahead of the accepted native fact")
        if revoking {
            model.setAvailable(false)
            try await service.setAccess(available: false, generation: 2)
        }
        clock.set(3); player.samplePosition = 0
        await gate.release(); await model.flushPlaybackFacts()
        if !revoking {
            await model.prepareCurrent(); await model.flushPlaybackFacts()
            model.playbackState.isChangingQueue = false
            await controller.flush()
            let rows = await accounts.recorded()
            let end = try XCTUnwrap(rows.first { $0.event == .end && $0.instanceID == original })
            XCTAssertEqual(end.reason, .ui); XCTAssertEqual(end.listened, 2); XCTAssertEqual(end.mediaListened, 2)
            XCTAssertEqual(end.position, 2); XCTAssertEqual(end.occurredAt, 1002)
            let starts = rows.filter { $0.event == .start }
            XCTAssertEqual(starts.count, 2); XCTAssertNotEqual(starts.first?.instanceID, starts.last?.instanceID)
            try await feedbackProgress(model, player: player, clock: clock, at: 5, position: 2)
        }
        await model.close(); await controller.flush()
        let final = await accounts.recorded()
        if revoking { XCTAssertEqual(final.map(\.event), [.start]) }
        else {
            let ends = final.filter { $0.event == .end }
            XCTAssertEqual(ends.map(\.listened), [2, 2])
            XCTAssertEqual(ends.map(\.reason), [.ui, .interrupt])
            XCTAssertNotEqual(ends.first?.instanceID, ends.last?.instanceID)
        }
        await service.close()
    }

    func testFeatureExhaustedMediaFailureKeepsExceptionThroughClose() async throws {
        let accounts = NativeFeedbackAccounts(), clock = NativeFeedbackClock(), scripts = SourceHandoffBoundaryScripts()
        await accounts.configure(result: .confirmed)
        let service = try await NativeMusicFixture.service(MusicNativeCandidateDiscoveryFixture.store(),
            scripts: scripts, accounts: accounts, monotonicClock: { clock.now() })
        let player = SourceHandoffBoundaryPlayer(), model = try await feedbackModel(service, player: player, clock: clock)
        try await feedbackProgress(model, player: player, clock: clock, at: 2, position: 2)
        await scripts.setEnabled(false)
        player.duration = 230 // No remaining official preparation matches this recording either.
        let candidate = try XCTUnwrap(model.candidate), native = try XCTUnwrap(model.nativeAttempt)
        clock.set(3)
        model.onPlaybackEvent(.init(attempt: .init(id: native.id, owner: "music", resourceID: candidate.resourceId),
            kind: .failed, snapshot: .init(owner: "music", resourceID: candidate.resourceId, phase: .failed,
                position: 3, duration: 180), wantsPlayback: false, observedAt: 3, occurredAt: 1003))
        await model.flushPlaybackFacts()
        XCTAssertEqual(model.phase, .failed)
        await model.close()
        let controller = await service.playbackFeedback
        await controller.flush()
        let rows = await accounts.recorded()
        XCTAssertEqual(rows.map(\.event), [.start, .end])
        XCTAssertEqual(rows.first?.instanceID, rows.last?.instanceID)
        XCTAssertEqual(rows.last?.reason, .exception)
        await service.close()
    }

    func testFeatureWrongDurationStopsOutputAndCannotPairOldAudioWithReplacement() async throws {
        let accounts = NativeFeedbackAccounts(), clock = NativeFeedbackClock()
        await accounts.configure(result: .confirmed)
        let service = try await NativeMusicFixture.service(MusicNativeCandidateDiscoveryFixture.store(),
            scripts: SourceHandoffBoundaryScripts(), accounts: accounts, monotonicClock: { clock.now() })
        let player = SourceHandoffBoundaryPlayer(), model = try await feedbackModel(service, player: player, clock: clock)
        try await feedbackProgress(model, player: player, clock: clock, at: 2, position: 2)
        let before = await accounts.recorded(), old = try XCTUnwrap(before.first?.instanceID)
        let candidate = try XCTUnwrap(model.candidate), native = try XCTUnwrap(model.nativeAttempt)
        clock.set(3); player.samplePosition = 3
        let pauses = player.pauses
        model.onPlaybackEvent(.init(attempt: .init(id: native.id, owner: "music", resourceID: candidate.resourceId),
            kind: .progress, snapshot: .init(owner: "music", resourceID: candidate.resourceId,
                phase: .playing, position: 3, duration: 240), wantsPlayback: true, observedAt: 3, occurredAt: 1003))
        XCTAssertGreaterThan(player.pauses, pauses, "Wrong output stops before asynchronous recovery")
        await model.flushPlaybackFacts()
        await model.close()
        let controller = await service.playbackFeedback
        await controller.flush()
        let rows = await accounts.recorded()
        XCTAssertFalse(rows.contains { $0.event == .end && $0.instanceID == old })
        XCTAssertTrue(rows.filter { $0.event == .end }.allSatisfy { $0.listened == 0 },
            "The wrong-audio jump cannot be credited to a freshly admitted replacement")
        await service.close()
    }

    func testSynchronousWrongDurationDuringHandoffStopsBeforeCommitReturns() async throws {
        let accounts = NativeFeedbackAccounts(), clock = NativeFeedbackClock()
        await accounts.configure(result: .confirmed)
        let service = try await NativeMusicFixture.service(MusicNativeCandidateDiscoveryFixture.store(),
            scripts: SourceHandoffBoundaryScripts(), accounts: accounts, monotonicClock: { clock.now() })
        let player = SourceHandoffBoundaryPlayer(), model = try await feedbackModel(service, player: player, clock: clock)
        try await feedbackProgress(model, player: player, clock: clock, at: 2, position: 2)
        let healthy = player.onPlay
        var rejected = false, stoppedSynchronously = false
        player.onPlay = { [weak model, weak player] native, resource in
            guard !rejected else { healthy?(native, resource); return }
            rejected = true
            let pauses = player?.pauses ?? 0
            model?.onPlaybackEvent(.init(attempt: .init(id: native.id, owner: "music", resourceID: resource.id),
                kind: .played, snapshot: .init(owner: "music", resourceID: resource.id, phase: .playing,
                    position: player?.samplePosition ?? 0, duration: 240),
                wantsPlayback: true, observedAt: clock.now(), occurredAt: 1000 + clock.now()))
            stoppedSynchronously = (player?.pauses ?? 0) > pauses
        }
        clock.set(3); player.samplePosition = 3
        await model.switchQuality("flac"); await model.flushPlaybackFacts()
        XCTAssertTrue(rejected); XCTAssertTrue(stoppedSynchronously)
        await model.close()
        let controller = await service.playbackFeedback
        await controller.flush()
        let rows = await accounts.recorded()
        let original = try XCTUnwrap(rows.first?.instanceID)
        XCTAssertFalse(rows.contains { $0.event == .end && $0.instanceID == original })
        XCTAssertTrue(rows.filter { $0.event == .end }.allSatisfy { $0.listened == 0 })
        await service.close()
    }
}
