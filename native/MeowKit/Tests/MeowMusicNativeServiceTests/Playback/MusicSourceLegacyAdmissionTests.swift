import Foundation
import XCTest
import MeowMusicFeature
@testable import MeowMusicNativeService

private struct LegacyAdmissionScripts: MusicScriptResolving {
    let listingFailure: MusicServiceError
    func sources() throws -> [MusicScriptSource] { throw listingFailure }
    func source(installationID: String) throws -> MusicScriptSource { throw MusicServiceError.unsupported }
    func resolve(request: MusicScriptRequest) throws -> MusicScriptResult<MusicNativeAudio> { throw MusicServiceError.unsupported }
    func lyrics(request: MusicScriptRequest) throws -> MusicScriptResult<MusicNativeLyrics> { throw MusicServiceError.unsupported }
}

private struct LegacyAdmissionAccounts: MusicNativeAccountProviding, MusicNativeCacheScopeProviding {
    let base: MusicRecoveryTestAccounts
    var optionsFailure: MusicServiceError? = nil
    var stageGate: MusicNativeCandidateDiscoveryBarrier? = nil
    func nativeOptions(platform: MusicAccountPlatform, nativeID: String, sourceKey: String) async throws -> [MusicPlaybackOption] {
        if let optionsFailure { throw optionsFailure }
        return try await base.nativeOptions(platform: platform, nativeID: nativeID, sourceKey: sourceKey)
    }
    func nativeResolve(platform: MusicAccountPlatform, nativeID: String,
                       selection: MusicPlaybackSelection) async throws -> MusicNativeAudio {
        let audio = try await base.nativeResolve(platform: platform, nativeID: nativeID, selection: selection)
        return .init(url: audio.url, expiresAt: audio.expiresAt,
                     fileCachePolicy: .init(allowed: true), playbackAuthorization: audio.playbackAuthorization)
    }
    func nativeCacheScope() async throws -> MusicNativeCacheScope {
        if let stageGate { await stageGate.enter() }
        return try await base.nativeCacheScope()
    }
    func checkNativeCacheScope(_ scope: MusicNativeCacheScope) async throws { try await base.checkNativeCacheScope(scope) }
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

private struct LegacyAdmissionEmptyCache: MusicAudioFileCaching {
    func lookup(scope: String, songID: String, authorization: MusicQueueAuthorization,
                accepts: @escaping @Sendable (MusicAudioFileCacheIdentity) -> Bool) -> MusicAudioFileCacheHit? { nil }
    func preparePlayback(_ request: MusicAudioFilePlaybackRequest) async throws -> any MusicAudioFilePlayback { MusicTestByteSource() }
    func storeAfterPlayback(_ request: MusicAudioFileCacheRequest) -> MusicAudioFileCacheVersion? { nil }
    func touch(_ identity: MusicAudioFileCacheIdentity, version: MusicAudioFileCacheVersion,
               confirmedAt: Date, authorization: MusicQueueAuthorization) {}
    func remove(_ identity: MusicAudioFileCacheIdentity, version: MusicAudioFileCacheVersion) {}
    func cancelPending() {}
    func clear() {}
    func clearOfficial() {}
    func close() {}
}

final class MusicSourceLegacyAdmissionTests: XCTestCase {
    private let reference = MusicReference(kind: .queue, id: "entry-1")
    private func accounts(_ clock: MusicRecoveryTestClock) -> MusicRecoveryTestAccounts {
        .init(platform: .qq, nativeID: "101", clock: clock)
    }
    private func store() throws -> NativeMusicMemoryDocument {
        try MusicNativeCandidateDiscoveryFixture.store(song:
            MusicModel.normalize(MusicNativeCandidateDiscoveryFixture.track("tx", id: "101")))
    }

    func testAvailabilityKeepsOfficialOptionsWhenScriptListingFails() async throws {
        for failure in [MusicServiceError.scriptRuntimeUnavailable, .scriptFailed, .connectionFailed,
                        .networkOffline, .timeout, .rateLimited, .busy, .requestRejected] {
            let clock = MusicRecoveryTestClock(), accounts = self.accounts(clock)
            let service = try await NativeMusicFixture.service(store(),
                scripts: LegacyAdmissionScripts(listingFailure: failure), accounts: accounts, clock: { clock.now() })
            let available = try await service.sourceAvailability(for: reference)
            XCTAssertEqual(available.options.count, 1)
            let official = try XCTUnwrap(available.options.first)
            XCTAssertTrue(official.installationId.hasPrefix("official:"))
            XCTAssertEqual(available.scriptState, .unavailable(failure))
            let exclusions = try await service.sourceExclusions(), counts = await accounts.counts()
            XCTAssertTrue(exclusions.isEmpty)
            XCTAssertEqual(counts.resolves, 0, "Capability inspection must not resolve audio")
            await service.close()
        }
    }

    func testAvailabilityKeepsScriptsWhenOfficialCapabilityFails() async throws {
        for failure in [MusicServiceError.connectionFailed, .networkOffline, .timeout, .upstreamFailed,
                        .rateLimited, .busy, .requestRejected] {
            let clock = MusicRecoveryTestClock(), scripts = NativeMusicScriptFixture()
            let service = try await NativeMusicFixture.service(store(), scripts: scripts,
                accounts: LegacyAdmissionAccounts(base: accounts(clock), optionsFailure: failure), clock: { clock.now() })
            let available = try await service.sourceAvailability(for: reference)
            XCTAssertEqual(available.options.map(\.installationId), [NativeMusicFixture.installation])
            XCTAssertEqual(available.scriptState, .ready)
            let calls = await scripts.calls(), exclusions = try await service.sourceExclusions()
            XCTAssertTrue(calls.isEmpty); XCTAssertTrue(exclusions.isEmpty)
            await service.close()
        }
    }

    func testAvailabilitySkipsSignedOutOrUnconfiguredOfficialCapabilities() async throws {
        for failure in [MusicServiceError.authRequired, .sessionExpired, .unavailable, .unsupported] {
            let clock = MusicRecoveryTestClock()
            let service = try await NativeMusicFixture.service(store(), scripts: NativeMusicScriptFixture(),
                accounts: LegacyAdmissionAccounts(base: accounts(clock), optionsFailure: failure), clock: { clock.now() })
            let available = try await service.sourceAvailability(for: reference)
            XCTAssertEqual(available.options.map(\.installationId), [NativeMusicFixture.installation])
            await service.close()
        }
    }

    func testSourceSessionKeepsSameTierScriptWhenOfficialCapabilityRequestIsRejected() async throws {
        try await rejectedOfficialCapability(discovery: false)
    }
    func testCandidateDiscoveryKeepsSameTierScriptWhenOfficialCapabilityRequestIsRejected() async throws {
        try await rejectedOfficialCapability(discovery: true)
    }
    private func rejectedOfficialCapability(discovery: Bool) async throws {
        let clock = MusicRecoveryTestClock(), scripts = NativeMusicScriptFixture()
        let accounts = LegacyAdmissionAccounts(base: self.accounts(clock), optionsFailure: .requestRejected)
        let service = try await NativeMusicFixture.service(store(), scripts: scripts,
            accounts: accounts, clock: { clock.now() })
        if discovery {
            let session = try await service.beginCandidateDiscovery(occurrenceID: "entry-1", expectedQueueRevision: 1)
            let snapshot = try await session.snapshot(discover: false, force: false)
            XCTAssertEqual(snapshot.options.map(\.playback.installationId), [NativeMusicFixture.installation])
            XCTAssertTrue(snapshot.issues.contains {
                $0.stage == .capability && $0.platform == "tx" && $0.failure == .requestRejected
            })
            let target = try XCTUnwrap(snapshot.options.first).target(quality: "flac")
            let candidate = try await session.resolve(target, attemptID: UUID().uuidString.lowercased())
            XCTAssertEqual(candidate.requestedQuality, "flac")
            await session.close(retainingAttemptID: nil)
        } else {
            let session = try await service.sourceSession(occurrenceID: "entry-1", expectedQueueRevision: 1)
            let result = try await session.match(intent: .quality("flac"))
            XCTAssertEqual(result.selection.installationId, NativeMusicFixture.installation)
            XCTAssertEqual(result.selection.quality, "flac")
            await service.discardSource(attemptID: result.candidate.attemptId)
        }
        let calls = await scripts.calls(), counts = await accounts.base.counts()
        XCTAssertEqual(calls.count, 1)
        XCTAssertEqual(calls.first?.quality, "flac")
        XCTAssertEqual(counts.resolves, 0)
        let exclusions = try await service.sourceExclusions(), history = try await service.playedSongs()
        XCTAssertTrue(exclusions.isEmpty)
        XCTAssertTrue(history.isEmpty, "Capability fallback and resolution are not playback facts")
        await service.close()
    }

    func testAvailabilityPropagatesGlobalFailuresFromEitherCapabilityPort() async throws {
        for scriptFailure in [true, false] {
            for failure in [MusicServiceError.staleAccess, .accessUnavailable, .targetChanged, .staleRevision,
                            .storageConflict, .storageFailed, .corruptDocument, .verificationRequired, .closed, .cancelled] {
                let clock = MusicRecoveryTestClock()
                let scripts: any MusicScriptResolving
                if scriptFailure { scripts = LegacyAdmissionScripts(listingFailure: failure) }
                else { scripts = NativeMusicScriptFixture() }
                let service = try await NativeMusicFixture.service(store(), scripts: scripts,
                    accounts: LegacyAdmissionAccounts(base: accounts(clock), optionsFailure: scriptFailure ? nil : failure),
                    clock: { clock.now() })
                do {
                    _ = try await service.sourceAvailability(for: reference)
                    XCTFail("Global failure must not become a partial capability success")
                } catch { XCTAssertEqual(error as? MusicServiceError, failure) }
                await service.close()
            }
        }
    }

    func testLegacyResolveRejectsExclusionAddedDuringScriptResolution() async throws {
        try await exclusionDuringScriptResolution(discovery: false, exclusion: .matching)
    }
    func testCandidateResolveRejectsExclusionAddedDuringScriptResolution() async throws {
        try await exclusionDuringScriptResolution(discovery: true, exclusion: .matching)
    }
    func testLegacyResolversKeepOtherSongAndPlatformExclusionsIsolated() async throws {
        for discovery in [false, true] {
            for exclusion in [Exclusion.otherSong, .otherPlatform] {
                try await exclusionDuringScriptResolution(discovery: discovery, exclusion: exclusion)
            }
        }
    }

    private enum Exclusion { case matching, otherSong, otherPlatform }
    private func exclusionDuringScriptResolution(discovery: Bool, exclusion: Exclusion) async throws {
        let store = try self.store()
        var document = try MusicDocument(store.read(), runID: "legacy-admission")
        let other = try MusicModel.normalize(MusicNativeCandidateDiscoveryFixture.track("tx", id: "102", title: "Other recording"))
        document.queue["entries"] = .array(document.queue["entries"].array +
            [.obj([("id", .string("entry-2")), ("song", other)])])
        document.queue["serial"] = .n(2)
        store.replace(try document.encode(changed: ["queue"]))
        let gate = MusicNativeCandidateDiscoveryBarrier(), scripts = MusicNativeCandidateDiscoveryScripts()
        await scripts.configure(barrier: gate)
        let service = try await NativeMusicFixture.service(store, scripts: scripts)
        let session: (any MusicCandidateDiscoverySession)?
        if discovery { session = try await service.beginCandidateDiscovery(occurrenceID: "entry-1", expectedQueueRevision: 1) }
        else { session = nil }
        let target: MusicCandidateDiscoveryTarget
        if let session {
            let snapshot = try await session.snapshot(discover: false, force: false)
            target = try XCTUnwrap(snapshot.options.first).target(quality: "flac")
        } else {
            let available = try await service.sourceAvailability(for: reference)
            target = MusicCandidateDiscoveryOption(playback: try XCTUnwrap(available.options.first)).target(quality: "flac")
        }
        let selection = target.selection, attempt = UUID().uuidString.lowercased()
        let task = Task {
            if let session {
                return try await session.resolve(target, attemptID: attempt)
            }
            return try await service.resolve(attemptId: attempt, occurrenceId: "entry-1", selection: selection)
        }
        await gate.started(1)
        do {
            try await service.excludeSource(for: exclusion == .otherSong ? .init(kind: .queue, id: "entry-2") : reference,
                provider: selection.installationId, platform: exclusion == .otherPlatform ? "wy" : "tx")
        } catch { await gate.release(); _ = try? await task.value; await service.close(); throw error }
        await gate.release()
        let fact = MusicPlaybackFact(attemptId: attempt, occurrenceId: "entry-1", resourceId: "music:" + attempt,
            kind: .playing, position: 20, duration: 180)
        if exclusion == .matching {
            do { _ = try await task.value; XCTFail("Excluded script must not return a candidate") }
            catch { XCTAssertEqual(error as? MusicServiceError, .sourceUnavailable) }
            do { _ = try await service.record(fact); XCTFail("Rejected attempt must not accept playback facts") }
            catch { XCTAssertEqual(error as? MusicServiceError, .staleResult) }
            let history = try await service.playedSongs()
            XCTAssertTrue(history.isEmpty)
        } else {
            let candidate = try await task.value
            XCTAssertEqual(candidate.attemptId, attempt)
            let accepted = try await service.record(fact)
            XCTAssertTrue(accepted)
            let history = try await service.playedSongs()
            XCTAssertEqual(history.count, 1)
        }
        let calls = await scripts.calls()
        XCTAssertEqual(calls.count, 1)
        await session?.close(retainingAttemptID: nil)
        await service.close()
    }

    func testLegacyResolveRechecksExclusionAfterFinalCacheStageWait() async throws {
        try await exclusionDuringCacheStage(discovery: false)
    }
    func testCandidateResolveRechecksExclusionAfterFinalCacheStageWait() async throws {
        try await exclusionDuringCacheStage(discovery: true)
    }
    func testOfficialLegacyResolversKeepOtherSongAndPlatformExclusionsIsolatedAfterFinalStage() async throws {
        for discovery in [false, true] {
            for exclusion in [Exclusion.otherSong, .otherPlatform] {
                try await exclusionDuringCacheStage(discovery: discovery, change: .exclusion(exclusion))
            }
        }
    }
    func testOfficialLegacyResolversRejectQueueOrSettingsChangesAfterFinalStage() async throws {
        for discovery in [false, true] {
            for change in [CacheStageChange.queue, .settings] {
                try await exclusionDuringCacheStage(discovery: discovery, change: change)
            }
        }
    }
    private enum CacheStageChange { case exclusion(Exclusion), queue, settings }
    private func exclusionDuringCacheStage(discovery: Bool, change: CacheStageChange = .exclusion(.matching)) async throws {
        let clock = MusicRecoveryTestClock(), gate = MusicNativeCandidateDiscoveryBarrier()
        let accounts = LegacyAdmissionAccounts(base: self.accounts(clock), stageGate: gate)
        let store = try self.store()
        var document = try MusicDocument(store.read(), runID: "official-legacy-admission")
        let other = try MusicModel.normalize(MusicNativeCandidateDiscoveryFixture.track("tx", id: "102", title: "Other recording"))
        document.queue["entries"] = .array(document.queue["entries"].array +
            [.obj([("id", .string("entry-2")), ("song", other)])])
        document.queue["serial"] = .n(2)
        store.replace(try document.encode(changed: ["queue"]))
        let service = NativeMusicService(document: store, accounts: accounts, qq: NativeMusicQQFixture(),
            audioFileCache: LegacyAdmissionEmptyCache(), transport: NativeMusicNoNetwork(), clock: { clock.now() })
        try await service.setAccess(available: true, generation: 1)
        let session: (any MusicCandidateDiscoverySession)?
        if discovery { session = try await service.beginCandidateDiscovery(occurrenceID: "entry-1", expectedQueueRevision: 1) }
        else { session = nil }
        let target: MusicCandidateDiscoveryTarget
        if let session {
            let snapshot = try await session.snapshot(discover: false, force: false)
            target = try XCTUnwrap(snapshot.options.first).target(quality: "flac")
        } else {
            let available = try await service.sourceAvailability(for: reference)
            target = MusicCandidateDiscoveryOption(playback: try XCTUnwrap(available.options.first)).target(quality: "flac")
        }
        let selection = target.selection, attempt = UUID().uuidString.lowercased()
        let task = Task {
            if let session {
                return try await session.resolve(target, attemptID: attempt)
            }
            return try await service.resolve(attemptId: attempt, occurrenceId: "entry-1", selection: selection)
        }
        await gate.started(1)
        let counts = await accounts.base.counts()
        XCTAssertEqual(counts.resolves, 1, "The mutation is inserted after audio resolution, while final cache staging waits")
        let expectedFailure: MusicServiceError?
        do {
            switch change {
            case .exclusion(let exclusion):
                try await service.excludeSource(for: exclusion == .otherSong ? .init(kind: .queue, id: "entry-2") : reference,
                    provider: selection.installationId, platform: exclusion == .otherPlatform ? "wy" : "tx")
                expectedFailure = exclusion == .matching ? .sourceUnavailable : nil
            case .queue:
                // Change the queue revision without selecting another occurrence
                // or explicitly cancelling the pending attempt.
                let changed = try await service.changeQueue(.reorder(["entry-2", "entry-1"]), expectedRevision: 1)
                XCTAssertEqual(changed.currentId, "entry-1")
                XCTAssertEqual(changed.revision, 2)
                expectedFailure = .staleRevision
            case .settings:
                let state = try await service.state()
                _ = try await service.updateSettings(.init(ifRevision: state.settingsRevision, lyricsEnabled: false))
                expectedFailure = .staleRevision
            }
        } catch { await gate.release(); _ = try? await task.value; await service.close(); throw error }
        await gate.release()
        let fact = MusicPlaybackFact(attemptId: attempt, occurrenceId: "entry-1", resourceId: "music:" + attempt,
            kind: .playing, position: 20, duration: 180)
        if let expectedFailure {
            do { _ = try await task.value; XCTFail("Matching exclusion or changed revision must prevent publication") }
            catch { XCTAssertEqual(error as? MusicServiceError, expectedFailure) }
            do { _ = try await service.record(fact); XCTFail("Rejected candidate must not own a valid playback fact") }
            catch { XCTAssertEqual(error as? MusicServiceError, .staleResult) }
            let history = try await service.playedSongs()
            XCTAssertTrue(history.isEmpty)
        } else {
            let candidate = try await task.value
            XCTAssertEqual(candidate.attemptId, attempt)
            XCTAssertEqual(candidate.requestedQuality, selection.quality)
            let accepted = try await service.record(fact)
            XCTAssertTrue(accepted)
            let history = try await service.playedSongs()
            XCTAssertEqual(history.count, 1)
        }
        await session?.close(retainingAttemptID: nil)
        await service.close()
    }
}
