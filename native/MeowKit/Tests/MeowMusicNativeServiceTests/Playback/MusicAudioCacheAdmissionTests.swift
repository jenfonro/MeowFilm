import Foundation
import XCTest
import MeowMusicFeature
import MeowMusicPlatform
@testable import MeowMusicNativeService

final class MusicAudioCacheAdmissionTests: XCTestCase {
    func testLookupRechecksExclusionAfterSuspensionWithoutBlockingOtherSongsOrPlatforms() async throws {
        for official in [false, true] {
            for excluded in ["same", "other-song", "other-platform", "other-provider"] {
                let song = try MusicNativeCandidateDiscoveryFixture.song()
                let store = try MusicNativeCandidateDiscoveryFixture.store(song: song)
                var document = try MusicDocument(store.read(), runID: "cache-admission")
                let other = try MusicModel.normalize(MusicNativeCandidateDiscoveryFixture.track(id: "OtherMID", title: "Other recording"))
                XCTAssertNotEqual(other["songId"], song["songId"], "The unrelated-song control must be a different recording")
                document.queue["entries"] = .array(document.queue["entries"].array + [
                    .obj([("id", .string("entry-2")), ("song", other)])])
                document.queue["serial"] = .n(2)
                store.replace(try document.encode(changed: ["queue"]))
                let source = try XCTUnwrap(MusicModel.sources(song).first)
                let provider = official ? AdmissionAccounts.provider : "historical-script"
                let scope = official ? "old-account-scope" : "script-history"
                let identity = MusicAudioFileCacheIdentity(scope: scope, songID: song["songId"].string,
                    sourceKey: source.sourceKey, platform: "tx", nativeID: source.nativeId, requestedQuality: "flac",
                    playbackSignature: official ? NativeMusicService.audioCacheSignature(song: song, playback: document.playback) :
                        NativeMusicService.scriptAudioSignature(song), provider: provider)
                let gate = MusicNativeCandidateDiscoveryBarrier()
                let file = AdmissionLookupCache(hit: .init(identity: identity,
                    fileURL: URL(fileURLWithPath: "/synthetic-cache/recording.flac"), duration: 180,
                    measuredQuality: "flac", expiresAt: NativeMusicFixture.date.addingTimeInterval(3600)), gate: gate)
                let service = NativeMusicService(document: store, accounts: AdmissionAccounts(), qq: NativeMusicQQFixture(),
                    audioFileCache: file, transport: NativeMusicNoNetwork(), clock: { NativeMusicFixture.date })
                try await service.setAccess(available: true, generation: 1)
                let id = UUID().uuidString.lowercased()
                let lookup = Task { try await service.cachedPlayback(attemptID: id, occurrenceID: "entry-1", selection: identity.selection) }
                await gate.started(1)
                let before = try MusicDocument(store.read(), runID: "before-exclusion").playback["revision"]
                try await service.excludeSource(for: .init(kind: .queue, id: excluded == "other-song" ? "entry-2" : "entry-1"),
                    provider: excluded == "other-provider" ? "different-script" : provider,
                    platform: excluded == "other-platform" ? "wy" : "tx")
                XCTAssertEqual(try MusicDocument(store.read(), runID: "after-exclusion").playback["revision"], before)
                await gate.release()
                if excluded == "same" {
                    do { _ = try await lookup.value; XCTFail("An excluded cached candidate escaped lookup") }
                    catch { XCTAssertEqual(MusicServiceError.sanitized(error), .sourceUnavailable) }
                    do {
                        _ = try await service.record(.init(attemptId: id, occurrenceId: "entry-1", resourceId: "music:" + id,
                            kind: .playing, position: 20, duration: 180))
                        XCTFail("Rejected lookup accepted a playing fact")
                    } catch { XCTAssertEqual(MusicServiceError.sanitized(error), .staleResult) }
                } else {
                    let result = try await lookup.value
                    XCTAssertEqual(result?.selection, identity.selection)
                    let accepted = try await service.record(.init(attemptId: id, occurrenceId: "entry-1", resourceId: "music:" + id,
                        kind: .playing, position: 20, duration: 180))
                    XCTAssertTrue(accepted)
                }
                await service.close()
            }
        }
    }

    func testPublicPlayingStageFillsWithoutAccountRotation() async throws {
        try await assertDeferredPlayingFill(progressDuringScope: false)
    }

    func testPublicPlayingThenProgressDuringScopeFillsExactlyOnce() async throws {
        try await assertDeferredPlayingFill(progressDuringScope: true)
    }

    private func assertDeferredPlayingFill(progressDuringScope: Bool) async throws {
        let clock = MusicRecoveryTestClock(), bytes = AdmissionBytes()
        let root = try cacheRoot()
        defer { try? FileManager.default.removeItem(at: root) }
        let real = NativeMusicAudioFileCache(rootURL: root, clock: { clock.now() }, transport: bytes, inspector: AdmissionMeasurement())
        let called = expectation(description: "fill called"), finished = expectation(description: "fill completed")
        let file = AdmissionFillCache(real: real, clock: clock, called: called, finished: finished)
        let accounts = AdmissionAccounts()
        let store = try MusicNativeCandidateDiscoveryFixture.store()
        let service = NativeMusicService(document: store, accounts: accounts,
            qq: NativeMusicQQFixture(), audioFileCache: file, transport: NativeMusicNoNetwork(), clock: { clock.now() })
        try await service.setAccess(available: true, generation: 1)
        clock.advance(1)
        let session = try await service.sourceSession(occurrenceID: "entry-1", expectedQueueRevision: 1)
        let selected = try await session.match(quality: "flac", sourceID: nil)
        try await service.activateSource(attemptID: selected.candidate.attemptId)
        let gate = MusicNativeCandidateDiscoveryBarrier()
        await accounts.holdScope(gate)
        let accepted = try await service.record(fact(selected.candidate))
        XCTAssertTrue(accepted)
        let history = try MusicDocument(store.read(), runID: "deferred-fill").history
        await gate.started(1)
        if progressDuringScope {
            let progressed = try await service.record(fact(selected.candidate, kind: .progress))
            XCTAssertTrue(progressed)
        }
        await gate.release()
        await fulfillment(of: [called, finished], timeout: 3)
        for kind: MusicPlaybackFact.Kind in [.progress, .playing, .progress] {
            let accepted = try await service.record(fact(selected.candidate, kind: kind))
            XCTAssertTrue(accepted)
        }
        await drainCaptures(service)
        let versions = await file.versions
        XCTAssertEqual(versions.count, 1); XCTAssertNotNil(versions.first ?? nil)
        let requests = await file.requests
        XCTAssertEqual(requests.count, 1)
        let scope = await accounts.scope
        let hit = await real.lookup(scope: scope.scope, songID: try MusicNativeCandidateDiscoveryFixture.song()["songId"].string,
            authorization: scope.authorization, accepts: { _ in true })
        XCTAssertNotNil(hit)
        let transfers = await bytes.calls
        XCTAssertEqual(transfers, 1)
        XCTAssertEqual(try MusicDocument(store.read(), runID: "deferred-fill").history, history,
                       "Progress and staging must not manufacture another history event")
        let played = try await service.playedSongs()
        XCTAssertEqual(played.count, 1)
        await service.close()
    }

    func testDeferredStageDoesNotTreatProgressOrInactiveFactsAsPlaying() async throws {
        for kind: MusicPlaybackFact.Kind in [.progress, .paused, .ended, .failed, .stopped] {
            let clock = MusicRecoveryTestClock(), bytes = AdmissionBytes()
            let root = try cacheRoot()
            defer { try? FileManager.default.removeItem(at: root) }
            let real = NativeMusicAudioFileCache(rootURL: root, clock: { clock.now() },
                transport: bytes, inspector: AdmissionMeasurement())
            let file = AdmissionFillCache(real: real, clock: clock), accounts = AdmissionAccounts()
            let store = try MusicNativeCandidateDiscoveryFixture.store()
            let service = NativeMusicService(document: store, accounts: accounts, qq: NativeMusicQQFixture(),
                audioFileCache: file, transport: NativeMusicNoNetwork(), clock: { clock.now() })
            try await service.setAccess(available: true, generation: 1)
            clock.advance(1)
            let session = try await service.sourceSession(occurrenceID: "entry-1", expectedQueueRevision: 1)
            let selected = try await session.match(intent: .quality("flac"))
            try await service.activateSource(attemptID: selected.candidate.attemptId)
            let gate = MusicNativeCandidateDiscoveryBarrier()
            await accounts.holdScope(gate)
            let accepted = try await service.record(fact(selected.candidate, kind: kind == .progress ? .progress : .playing))
            XCTAssertTrue(accepted)
            await gate.started(1)
            if kind != .progress {
                let inactive = try await service.record(fact(selected.candidate, kind: kind))
                XCTAssertTrue(inactive)
            }
            if kind == .paused {
                let progressed = try await service.record(fact(selected.candidate, kind: .progress))
                XCTAssertTrue(progressed)
            }
            let history = try MusicDocument(store.read(), runID: "inactive-stage").history
            await gate.release()
            // Observe actual staging, not a guessed delay. This uses internals
            // only to drain work; all stimuli and cache/history assertions are real.
            try await waitForStage(service, candidate: selected.candidate)
            let terminal = [.ended, .failed, .stopped].contains(kind)
            let progressed = try await service.record(fact(selected.candidate, kind: .progress))
            XCTAssertEqual(progressed, !terminal)
            await drainCaptures(service)
            let requests = await file.requests, transfers = await bytes.calls
            XCTAssertTrue(requests.isEmpty, "No current accepted playing state: \(kind)")
            XCTAssertEqual(transfers, 0)
            XCTAssertEqual(try MusicDocument(store.read(), runID: "inactive-stage").history, history)
            let played = try await service.playedSongs()
            XCTAssertEqual(played.count, kind == .progress ? 0 : 1)
            if !terminal {
                let resumed = try await service.record(fact(selected.candidate))
                XCTAssertTrue(resumed, "A real playing fact may start filling after progress-only or pause")
                await drainCaptures(service)
                let versions = await file.versions, requests = await file.requests
                XCTAssertEqual(requests.count, 1)
                XCTAssertEqual(versions.count, 1); XCTAssertNotNil(versions.first ?? nil)
                let afterResume = try await service.playedSongs()
                XCTAssertEqual(afterResume.count, 1)
            }
            await service.close()
        }
    }

    func testAwaitedStageCannotCarryPlayingIntoAnotherRecording() async throws {
        let clock = MusicRecoveryTestClock(), bytes = AdmissionBytes()
        let root = try cacheRoot()
        defer { try? FileManager.default.removeItem(at: root) }
        let real = NativeMusicAudioFileCache(rootURL: root, clock: { clock.now() },
            transport: bytes, inspector: AdmissionMeasurement())
        let file = AdmissionFillCache(real: real, clock: clock), accounts = AdmissionAccounts()
        let service = NativeMusicService(document: try MusicNativeCandidateDiscoveryFixture.store(),
            accounts: accounts, qq: NativeMusicQQFixture(), audioFileCache: file,
            transport: NativeMusicNoNetwork(), clock: { clock.now() })
        try await service.setAccess(available: true, generation: 1)
        clock.advance(1)
        let session = try await service.sourceSession(occurrenceID: "entry-1", expectedQueueRevision: 1)
        let old = try await session.match(intent: .quality("flac"))
        try await service.activateSource(attemptID: old.candidate.attemptId)
        let playing = try await service.record(fact(old.candidate))
        XCTAssertTrue(playing)
        try await MusicCandidateDiscoveryDeadline.run(seconds: 3) {
            while await service.audioCacheAttempt?.captured != true {
                try Task.checkCancellation(); await Task.yield()
            }
        }
        await drainCaptures(service)
        let song = try MusicNativeCandidateDiscoveryFixture.song()
        let staged = await service.audioCacheAttempt
        let audio = try XCTUnwrap(staged?.audio)
        let gate = MusicNativeCandidateDiscoveryBarrier()
        await accounts.holdScope(gate)
        // The first-fact stage is complete. Await this additional real stage
        // explicitly so the negative assertion cannot pass before it returns.
        let pending = Task {
            try await service.stageAudioCache(audio: audio, song: song, selection: old.selection,
                                              attemptID: old.candidate.attemptId)
        }
        await gate.started(1)
        let page = try await service.search(platform: "tx", query: "Different recording", page: 1, limit: 30)
        let other = try XCTUnwrap(page.items.first)
        XCTAssertNotEqual(other.songId, song["songId"].string)
        let state = try await service.state()
        let next = try await service.changeQueue(.replace([other.ref], index: 0), expectedRevision: state.revision)
        let current = try await service.sourceSession(occurrenceID: XCTUnwrap(next.currentId),
                                                      expectedQueueRevision: next.revision)
        let selected = try await current.match(intent: .quality("flac"))
        try await service.activateSource(attemptID: selected.candidate.attemptId)
        let progressed = try await service.record(fact(selected.candidate, kind: .progress))
        XCTAssertTrue(progressed)
        try await waitForStage(service, candidate: selected.candidate)
        await gate.release()
        do { try await pending.value; XCTFail("An old playing attempt was staged for another recording") }
        catch { XCTAssertEqual(MusicServiceError.sanitized(error), .staleResult) }
        await drainCaptures(service)
        let before = await file.requests, transfers = await bytes.calls
        XCTAssertEqual(before.count, 1)
        XCTAssertEqual(before.first?.identity.songID, song["songId"].string)
        XCTAssertEqual(transfers, 1)
        let history = try await service.playedSongs()
        XCTAssertEqual(history.map(\.track.songId), [song["songId"].string])
        let started = try await service.record(fact(selected.candidate))
        XCTAssertTrue(started)
        await drainCaptures(service)
        let after = await file.requests, versions = await file.versions
        XCTAssertEqual(after.map(\.identity.songID), [song["songId"].string, other.songId])
        XCTAssertEqual(versions.count, 2); XCTAssertTrue(versions.allSatisfy { $0 != nil })
        await service.close()
    }

    func testAwaitedStageRejectsOldMediaAfterAccountScopeRotation() async throws {
        let clock = MusicRecoveryTestClock(), bytes = AdmissionBytes()
        let root = try cacheRoot()
        defer { try? FileManager.default.removeItem(at: root) }
        let real = NativeMusicAudioFileCache(rootURL: root, clock: { clock.now() }, transport: bytes, inspector: AdmissionMeasurement())
        let file = AdmissionFillCache(real: real, clock: clock)
        let accounts = AdmissionAccounts(), song = try MusicNativeCandidateDiscoveryFixture.song()
        let source = try XCTUnwrap(MusicModel.sources(song).first)
        let service = NativeMusicService(document: try MusicNativeCandidateDiscoveryFixture.store(song: song), accounts: accounts,
            qq: NativeMusicQQFixture(), audioFileCache: file, transport: NativeMusicNoNetwork(), clock: { clock.now() })
        try await service.setAccess(available: true, generation: 1)
        clock.advance(1)
        let options = await accounts.nativeOptions(platform: .qq, nativeID: source.nativeId, sourceKey: source.sourceKey)
        let selection = try XCTUnwrap(options.first).selection(quality: "flac")
        // The real resolver installs the committed attempt and stages its media;
        // no playing fact has started a fill and no first-fact task is pending.
        let candidate = try await service.resolve(attemptId: UUID().uuidString.lowercased(),
            occurrenceId: "entry-1", selection: selection)
        let committed = await service.attempt
        XCTAssertEqual(committed?.id, candidate.attemptId)
        XCTAssertEqual(committed?.ready, true)
        let staged = await service.audioCacheAttempt
        let original = try XCTUnwrap(staged)
        let audio = try XCTUnwrap(original.audio)
        XCTAssertEqual(original.identity.scope, "old-account-scope")
        let gate = MusicNativeCandidateDiscoveryBarrier()
        await accounts.holdScope(gate)
        let staging = Task {
            try await service.stageAudioCache(audio: audio, song: song, selection: selection, attemptID: candidate.attemptId)
        }
        await gate.started(1)
        await accounts.rotate()
        await file.clearOfficial()
        await gate.release()
        do {
            try await staging.value
            XCTFail("Revoked media was staged under a replacement account scope")
        } catch { XCTAssertEqual(MusicServiceError.sanitized(error), .staleAccess) }
        let after = await service.audioCacheAttempt
        XCTAssertNil(after, "Completed staging must not bind old media to the replacement scope")
        // The public first-fact task replays its latest fact after optional
        // staging. Exercise that real consumer and drain any resulting capture.
        await service.audioCacheAcceptedFact(fact(candidate))
        let captures = await Array(service.audioCacheCaptures.values)
        for capture in captures { await capture.value }
        let requests = await file.requests, versions = await file.versions
        XCTAssertTrue(requests.isEmpty, "Must not submit old media under the replacement scope")
        XCTAssertTrue(versions.isEmpty)
        let scope = await accounts.scope
        XCTAssertEqual(scope.scope, "replacement-account-scope")
        let hit = await real.lookup(scope: scope.scope, songID: song["songId"].string,
            authorization: scope.authorization, accepts: { _ in true })
        XCTAssertNil(hit)
        let transfers = await bytes.calls
        XCTAssertEqual(transfers, 0)
        await service.close()
    }

    func testMediaRevokedAfterStageBeforeRealFillIsRejectedWithoutScopeRotation() async throws {
        for revoke in [false, true] {
            let clock = MusicRecoveryTestClock(), bytes = AdmissionBytes()
            let root = try cacheRoot()
            defer { try? FileManager.default.removeItem(at: root) }
            let real = NativeMusicAudioFileCache(rootURL: root, clock: { clock.now() }, transport: bytes, inspector: AdmissionMeasurement())
            let gate = MusicNativeCandidateDiscoveryBarrier()
            let called = expectation(description: "fill request staged"), finished = expectation(description: "real fill returned")
            let file = AdmissionFillCache(real: real, clock: clock, called: called, finished: finished, gate: gate)
            let accounts = AdmissionAccounts()
            let service = NativeMusicService(document: try MusicNativeCandidateDiscoveryFixture.store(), accounts: accounts,
                qq: NativeMusicQQFixture(), audioFileCache: file, transport: NativeMusicNoNetwork(), clock: { clock.now() })
            try await service.setAccess(available: true, generation: 1)
            clock.advance(1)
            let session = try await service.sourceSession(occurrenceID: "entry-1", expectedQueueRevision: 1)
            let selected = try await session.match(quality: "flac", sourceID: nil)
            try await service.activateSource(attemptID: selected.candidate.attemptId)
            let accepted = try await service.record(fact(selected.candidate))
            XCTAssertTrue(accepted)
            await gate.started(1)
            let progressed = try await service.record(fact(selected.candidate, kind: .progress))
            XCTAssertTrue(progressed)
            if revoke { await accounts.revokeMedia() }
            let requests = await file.requests
            let request = try XCTUnwrap(requests.first)
            XCTAssertTrue(request.authorization.isActive, "Account scope remains valid in this case")
            XCTAssertEqual(request.playbackAuthorization.isActive, !revoke, "Fill must retain original media authority")
            await gate.release()
            await fulfillment(of: [called, finished], timeout: 3)
            let versions = await file.versions
            XCTAssertEqual(versions.count, 1)
            XCTAssertEqual((versions.first ?? nil) != nil, !revoke)
            let transfers = await bytes.calls
            XCTAssertEqual(transfers, revoke ? 0 : 1)
            await service.close()
        }
    }

    func testDirectRealPortRejectsRevokedAudioEvenWithFreshRequestFences() async throws {
        for revoke in [false, true] {
            let clock = MusicRecoveryTestClock(), bytes = AdmissionBytes()
            let root = try cacheRoot()
            defer { try? FileManager.default.removeItem(at: root) }
            let real = NativeMusicAudioFileCache(rootURL: root, clock: { clock.now() }, transport: bytes, inspector: AdmissionMeasurement())
            let media = MusicQueueAuthorization()
            if revoke { media.revoke() }
            let identity = MusicAudioFileCacheIdentity(scope: "replacement-account-scope", songID: "synthetic-song", sourceKey: "tx:synthetic",
                platform: "tx", nativeID: "synthetic", requestedQuality: "flac", playbackSignature: "signature", provider: AdmissionAccounts.provider)
            let version = await real.storeAfterPlayback(.init(identity: identity, audio: AdmissionAccounts.audio(media),
                expectedDuration: 180, confirmedAt: clock.now(), authorization: MusicQueueAuthorization(),
                playbackAuthorization: MusicQueueAuthorization()))
            XCTAssertEqual(version != nil, !revoke)
            let transfers = await bytes.calls
            XCTAssertEqual(transfers, revoke ? 0 : 1)
            let hit = await real.lookup(scope: identity.scope, songID: identity.songID,
                authorization: MusicQueueAuthorization(), accepts: { _ in true })
            XCTAssertEqual(hit != nil, !revoke)
            await real.close()
        }
    }

    func testStageRejectsAlreadyRevokedMediaBeforeScopeRead() async throws {
        let accounts = AdmissionAccounts(), gate = MusicNativeCandidateDiscoveryBarrier()
        let song = try MusicNativeCandidateDiscoveryFixture.song()
        let source = try XCTUnwrap(MusicModel.sources(song).first)
        let hit = MusicAudioFileCacheHit(identity: .init(scope: "unused", songID: song["songId"].string,
            sourceKey: source.sourceKey, platform: "tx", nativeID: source.nativeId, requestedQuality: "flac",
            playbackSignature: "unused", provider: AdmissionAccounts.provider), fileURL: URL(fileURLWithPath: "/unused.flac"),
            duration: 180, measuredQuality: "flac", expiresAt: NativeMusicFixture.date.addingTimeInterval(3600))
        let service = NativeMusicService(document: try MusicNativeCandidateDiscoveryFixture.store(), accounts: accounts,
            qq: NativeMusicQQFixture(), audioFileCache: AdmissionLookupCache(hit: hit, gate: gate), transport: NativeMusicNoNetwork())
        try await service.setAccess(available: true, generation: 1)
        let media = MusicQueueAuthorization(); media.revoke()
        do {
            try await service.stageAudioCache(audio: AdmissionAccounts.audio(media), song: song, selection: hit.identity.selection,
                attemptID: UUID().uuidString.lowercased())
            XCTFail("Revoked media reached staging")
        } catch { XCTAssertEqual(MusicServiceError.sanitized(error), .staleAccess) }
        let reads = await accounts.scopeReads
        XCTAssertEqual(reads, 0)
        await service.close()
    }

    private func cacheRoot() throws -> URL {
        try FileManager.default.url(for: .cachesDirectory, in: .userDomainMask, appropriateFor: nil, create: true)
            .appendingPathComponent("meow-cache-admission-test-" + UUID().uuidString)
    }
    private func drainCaptures(_ service: NativeMusicService) async {
        let captures = await Array(service.audioCacheCaptures.values)
        for capture in captures { await capture.value }
    }
    private func waitForStage(_ service: NativeMusicService, candidate: MusicPlaybackCandidate) async throws {
        try await MusicCandidateDiscoveryDeadline.run(seconds: 3) {
            while await service.audioCacheAttempt?.attemptID != candidate.attemptId {
                try Task.checkCancellation(); await Task.yield()
            }
        }
    }
    private func fact(_ candidate: MusicPlaybackCandidate, kind: MusicPlaybackFact.Kind = .playing) -> MusicPlaybackFact {
        .init(attemptId: candidate.attemptId, occurrenceId: candidate.occurrenceId,
              resourceId: candidate.resourceId, kind: kind, position: 20, duration: 180)
    }
}

private actor AdmissionAccounts: MusicNativeAccountProviding, MusicNativeCacheScopeProviding {
    static let provider = "official:cache-admission"
    var scope = MusicNativeCacheScope(scope: "old-account-scope")
    private let media = MusicQueueAuthorization()
    private var scopeGate: MusicNativeCandidateDiscoveryBarrier?
    var scopeReads = 0
    static func audio(_ authorization: MusicQueueAuthorization) -> MusicNativeAudio {
        .init(url: URL(string: "https://isure.stream.qqmusic.qq.com/synthetic.flac")!, fileCachePolicy: .init(allowed: true),
            delivery: .init(quality: "flac", evidence: .officialResponse, duration: 180), playbackAuthorization: authorization)
    }
    func holdScope(_ gate: MusicNativeCandidateDiscoveryBarrier) { scopeGate = gate }
    func revokeMedia() { media.revoke() }
    func rotate() {
        media.revoke(); scope.authorization.revoke(); scope = .init(scope: "replacement-account-scope")
    }
    func nativeCacheScope() async throws -> MusicNativeCacheScope {
        scopeReads += 1
        if let gate = scopeGate {
            scopeGate = nil
            await gate.enter()
        }
        try scope.authorization.check()
        return scope // Deliberately reads the new scope after the suspension.
    }
    func checkNativeCacheScope(_ value: MusicNativeCacheScope) throws {
        try value.authorization.check()
        guard value == scope else { throw MusicServiceError.staleAccess }
    }
    func nativeOptions(platform: MusicAccountPlatform, nativeID: String, sourceKey: String) -> [MusicPlaybackOption] {
        guard platform == .qq else { return [] }
        return [.init(installationId: Self.provider, generation: 1, siteId: "tx", sourceKey: sourceKey,
            qualities: ["flac"], title: "Synthetic", version: "1", contentHash: String(repeating: "a", count: 64))]
    }
    func nativeResolve(platform: MusicAccountPlatform, nativeID: String, selection: MusicPlaybackSelection) -> MusicNativeAudio { Self.audio(media) }
    func nativeRecommendations(platform: MusicAccountPlatform, kind: String, cursor: String?) throws -> MusicNativeTrackPage { throw MusicServiceError.unsupported }
    func nativeCollections(platform: MusicAccountPlatform, cursor: String?) throws -> MusicAccountCollectionPage { throw MusicServiceError.unsupported }
    func nativeTracks(platform: MusicAccountPlatform, collectionID: String, cursor: String?) throws -> MusicNativeTrackPage { throw MusicServiceError.unsupported }
}

private actor AdmissionLookupCache: MusicAudioFileCaching {
    let hit: MusicAudioFileCacheHit
    let gate: MusicNativeCandidateDiscoveryBarrier
    init(hit: MusicAudioFileCacheHit, gate: MusicNativeCandidateDiscoveryBarrier) { self.hit = hit; self.gate = gate }
    func lookup(scope: String, songID: String, authorization: MusicQueueAuthorization,
                accepts: @escaping @Sendable (MusicAudioFileCacheIdentity) -> Bool) async -> MusicAudioFileCacheHit? {
        guard authorization.isActive, hit.identity.scope == scope, hit.identity.songID == songID, accepts(hit.identity) else { return nil }
        await gate.enter()
        return hit // Eligibility was captured before suspension, just like an asynchronous cache port.
    }
    func storeAfterPlayback(_ request: MusicAudioFileCacheRequest) -> MusicAudioFileCacheVersion? { nil }
    func touch(_ identity: MusicAudioFileCacheIdentity, version: MusicAudioFileCacheVersion, confirmedAt: Date, authorization: MusicQueueAuthorization) {}
    func remove(_ identity: MusicAudioFileCacheIdentity, version: MusicAudioFileCacheVersion) {}
    func cancelPending() {}
    func clear() {}
    func clearOfficial() {}
    func close() {}
}

private actor AdmissionFillCache: MusicAudioFileCaching {
    let real: NativeMusicAudioFileCache
    let clock: MusicRecoveryTestClock
    let called: XCTestExpectation?, finished: XCTestExpectation?
    let gate: MusicNativeCandidateDiscoveryBarrier?
    var requests: [MusicAudioFileCacheRequest] = []
    var versions: [MusicAudioFileCacheVersion?] = []
    init(real: NativeMusicAudioFileCache, clock: MusicRecoveryTestClock, called: XCTestExpectation? = nil,
         finished: XCTestExpectation? = nil, gate: MusicNativeCandidateDiscoveryBarrier? = nil) {
        self.real = real; self.clock = clock; self.called = called; self.finished = finished; self.gate = gate
    }
    func lookup(scope: String, songID: String, authorization: MusicQueueAuthorization,
                accepts: @escaping @Sendable (MusicAudioFileCacheIdentity) -> Bool) async -> MusicAudioFileCacheHit? {
        await real.lookup(scope: scope, songID: songID, authorization: authorization, accepts: accepts)
    }
    func storeAfterPlayback(_ request: MusicAudioFileCacheRequest) async -> MusicAudioFileCacheVersion? {
        requests.append(request); called?.fulfill()
        if let gate { await gate.enter() }
        let result = await real.storeAfterPlayback(request)
        versions.append(result); finished?.fulfill()
        return result
    }
    func touch(_ identity: MusicAudioFileCacheIdentity, version: MusicAudioFileCacheVersion, confirmedAt: Date, authorization: MusicQueueAuthorization) async {
        await real.touch(identity, version: version, confirmedAt: confirmedAt, authorization: authorization)
    }
    func remove(_ identity: MusicAudioFileCacheIdentity, version: MusicAudioFileCacheVersion) async { await real.remove(identity, version: version) }
    func cancelPending() async { await real.cancelPending() }
    func clear() async { await real.clear() }
    func clearOfficial() async {
        await real.clearOfficial()
        clock.advance(1) // A stale fill must not be rejected merely by the clear cutoff.
    }
    func close() async { await real.close() }
}

private actor AdmissionBytes: NativeMusicAudioCacheTransport {
    var calls = 0
    func transfer(_ request: NativeMusicAudioCacheHTTPRequest, into sink: NativeMusicAudioCacheSink) async throws {
        calls += 1
        try await sink.accept(.init(url: request.url, status: 200, headers: ["Content-Length": "32", "Content-Type": "audio/wav"]))
        try sink.write(Data(repeating: 0, count: 32)); try sink.finishBody()
    }
}
private struct AdmissionMeasurement: NativeMusicAudioCacheInspecting {
    func inspect(_ file: NativeMusicAudioCacheFile, expectedDuration: Double) -> NativeMusicAudioCacheInspection {
        .init(container: "wav", codec: "pcm", duration: 180, decodedFrames: 7_938_000, sampleRate: 44_100,
              channels: 2, bitrate: 1_411_200, bitDepth: 16, lossless: true, complete: true, protectedContent: false)
    }
}
