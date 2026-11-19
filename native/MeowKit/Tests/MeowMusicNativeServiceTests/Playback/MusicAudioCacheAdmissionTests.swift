import Foundation
import XCTest
import MeowStorage
import MeowStorageTestSupport
import MeowMusicFeature
import MeowMusicPlatform
@testable import MeowMusicNativeService

final class MusicAudioCacheAdmissionTests: XCTestCase {
    func testPublicOfficialResolveUsesCurrentRepositoryAfterAccountSuspension() async throws {
        let cases: [(metadata: Int, allowed: Bool)] = [(180, true), (190, true), (191, false)]
        for value in cases {
            let song = try NativeMusicFixture.storedSong(MusicNativeCandidateDiscoveryFixture.track(duration: 100))
            let source = try XCTUnwrap(MusicModel.sources(song).first)
            let selected = MusicPlaybackSelection(installationId: AdmissionAccounts.provider, generation: 1,
                siteId: source.platform, sourceKey: source.sourceKey, quality: "flac")
            let gate = MusicNativeCandidateDiscoveryBarrier(), accounts = AdmissionAccounts()
            await accounts.holdResolve(gate)
            let service = try await NativeMusicFixture.service(MusicNativeCandidateDiscoveryFixture.store(song: song),
                accounts: accounts)
            let before = try await service.read(), attemptID = UUID().uuidString.lowercased()
            let task = Task { try await service.resolve(attemptId: attemptID, occurrenceId: "entry-1", selection: selected) }
            await gate.started(1)
            let corrected = try MusicModel.normalize(MusicNativeCandidateDiscoveryFixture.track(
                title: "Current official title", duration: value.metadata))
            let adopted = try await service.adoptSongs([corrected], authoritative: true)
            XCTAssertEqual(adopted.first?["songId"], song["songId"])
            await gate.release()
            if value.allowed {
                let result = try await task.value
                XCTAssertEqual(result.title, "Current official title")
                XCTAssertEqual(result.delivery?.duration, 180)
                XCTAssertEqual(result.resolvedSelection, selected); XCTAssertEqual(result.requestedQuality, "flac")
                XCTAssertEqual(result.attemptId, attemptID); XCTAssertEqual(result.occurrenceId, "entry-1")
                XCTAssertNil(result.byteSource)
            } else {
                do { _ = try await task.value; XCTFail("Official media outside the current duration boundary was published") }
                catch { XCTAssertEqual(MusicServiceError.sanitized(error), .invalidMediaCandidate) }
            }
            let requests = await accounts.resolutionRequests
            XCTAssertEqual(requests.count, 1); XCTAssertEqual(requests.first?.platform, .qq)
            XCTAssertEqual(requests.first?.nativeID, source.nativeId); XCTAssertEqual(requests.first?.selection, selected)
            let after = try await service.read()
            XCTAssertEqual(after.queue, before.queue); XCTAssertEqual(after.playback, before.playback)
            XCTAssertEqual(after.history, before.history)
            XCTAssertEqual((try after.fixtureSong(song["songId"].string))["songId"], song["songId"])
            XCTAssertEqual((try after.fixtureSong(song["songId"].string))["baselineDuration"].number, 100)
            await service.close()
        }
    }

    func testPublicResolveKeepsCurrentTitleAndMediaAdmissionAfterCachePreparation() async throws {
        let cases: [(metadata: Int, allowed: Bool)] = [(181, true), (190, true), (191, false)]
        for value in cases {
            let song = try NativeMusicFixture.storedSong(MusicNativeCandidateDiscoveryFixture.track(duration: 180))
            let source = try XCTUnwrap(MusicModel.sources(song).first)
            let identity = MusicAudioFileCacheIdentity(scope: "old-account-scope", songID: song["songId"].string,
                sourceKey: source.sourceKey, platform: source.platform, nativeID: source.nativeId,
                requestedQuality: "flac", provider: AdmissionAccounts.provider)
            let gate = MusicNativeCandidateDiscoveryBarrier(), accounts = AdmissionAccounts()
            let file = AdmissionLookupCache(hit: .init(identity: identity,
                fileURL: URL(fileURLWithPath: "/synthetic-cache/current.flac"), duration: 180, measuredQuality: "flac",
                expiresAt: NativeMusicFixture.date.addingTimeInterval(3600)),
                gate: MusicNativeCandidateDiscoveryBarrier(), prepareGate: gate)
            let service = NativeMusicService(database: try MusicNativeCandidateDiscoveryFixture.store(song: song),
                accounts: accounts, platformClient: try NativeMusicFixture.client(), audioFileCache: file,
                clock: { NativeMusicFixture.date })
            try await service.setAccess(available: true, generation: 1)
            let before = try await service.read(), attemptID = UUID().uuidString.lowercased()
            let selected = identity.selection
            let task = Task { try await service.resolve(attemptId: attemptID, occurrenceId: "entry-1", selection: selected) }
            await gate.started(1)
            let corrected = try MusicModel.normalize(MusicNativeCandidateDiscoveryFixture.track(
                title: "Current prepared title", duration: value.metadata))
            _ = try await service.adoptSongs([corrected], authoritative: true)
            await gate.release()
            if value.allowed {
                let result = try await task.value
                XCTAssertEqual(result.title, "Current prepared title")
                XCTAssertEqual(result.delivery?.duration, 180)
                XCTAssertEqual(result.resolvedSelection, selected); XCTAssertEqual(result.requestedQuality, "flac")
                XCTAssertEqual(result.attemptId, attemptID); XCTAssertEqual(result.occurrenceId, "entry-1")
                XCTAssertNotNil(result.byteSource)
            } else {
                do { _ = try await task.value; XCTFail("Stale prepared media escaped the public resolver") }
                catch { XCTAssertEqual(MusicServiceError.sanitized(error), .invalidMediaCandidate) }
            }
            let prepared = await file.requests, requests = await accounts.resolutionRequests
            XCTAssertEqual(prepared.count, 1); XCTAssertEqual(prepared.first?.expectedDuration, 180)
            XCTAssertEqual(prepared.first?.identity.selection, selected)
            XCTAssertEqual(requests.count, 1); XCTAssertEqual(requests.first?.selection, selected)
            let after = try await service.read()
            XCTAssertEqual(after.queue, before.queue); XCTAssertEqual(after.playback, before.playback)
            XCTAssertEqual(after.history, before.history)
            XCTAssertEqual((try after.fixtureSong(song["songId"].string))["songId"], song["songId"])
            XCTAssertEqual((try after.fixtureSong(song["songId"].string))["baselineDuration"].number, 180)
            await service.close()
        }
    }

    func testLookupUsesCurrentDurationAndTitleAfterMetadataCorrection() async throws {
        let cases: [(metadata: Int, media: Double, allowed: Bool)] = [(101, 100, true), (130, 130, true), (130, 100, false)]
        for official in [false, true] {
            for value in cases {
                let song = try NativeMusicFixture.storedSong(MusicNativeCandidateDiscoveryFixture.track(duration: 100))
                let source = try XCTUnwrap(MusicModel.sources(song).first)
                let identity = MusicAudioFileCacheIdentity(scope: official ? "old-account-scope" : "script-history",
                    songID: song["songId"].string, sourceKey: source.sourceKey, platform: source.platform, nativeID: source.nativeId,
                    requestedQuality: "flac", provider: official ? AdmissionAccounts.provider : "historical-script")
                let gate = MusicNativeCandidateDiscoveryBarrier()
                let file = AdmissionLookupCache(hit: .init(identity: identity,
                    fileURL: URL(fileURLWithPath: "/synthetic-cache/current.flac"), duration: value.media,
                    measuredQuality: "flac", expiresAt: NativeMusicFixture.date.addingTimeInterval(3600)), gate: gate)
                let service = NativeMusicService(database: try MusicNativeCandidateDiscoveryFixture.store(song: song),
                    accounts: AdmissionAccounts(), platformClient: try NativeMusicFixture.client(),
                    audioFileCache: file, clock: { NativeMusicFixture.date })
                try await service.setAccess(available: true, generation: 1)
                let before = try await service.read()
                let lookup = Task { try await service.cachedPlayback(attemptID: UUID().uuidString.lowercased(),
                    occurrenceID: "entry-1", selection: nil) }
                await gate.started(1)
                let corrected = try MusicModel.normalize(MusicNativeCandidateDiscoveryFixture.track(
                    title: "Current cached title", duration: value.metadata))
                let adopted = try await service.adoptSongs([corrected], authoritative: true)
                XCTAssertEqual(adopted.first?["songId"], song["songId"])
                await gate.release()
                let result = try await lookup.value
                XCTAssertEqual(result != nil, value.allowed)
                if value.allowed {
                    XCTAssertEqual(result?.candidate.title, "Current cached title")
                    XCTAssertEqual(result?.candidate.delivery?.duration, value.media)
                    XCTAssertEqual(result?.selection, identity.selection)
                }
                let after = try await service.read(), requests = await file.requests
                XCTAssertEqual(after.queue, before.queue)
                XCTAssertEqual(after.playback, before.playback)
                XCTAssertTrue(requests.isEmpty, "Complete-file admission does not start a transfer")
                let history = try await service.playedSongs()
                XCTAssertTrue(history.isEmpty)
                await service.close()
            }
        }
    }

    func testFilePreparationUsesCurrentMetadataAfterScopeAndFileSuspensions() async throws {
        for phase in ["scope", "file"] {
            let song = try NativeMusicFixture.storedSong(MusicNativeCandidateDiscoveryFixture.track(duration: 100))
            let source = try XCTUnwrap(MusicModel.sources(song).first)
            let identity = MusicAudioFileCacheIdentity(scope: "old-account-scope", songID: song["songId"].string,
                sourceKey: source.sourceKey, platform: source.platform, nativeID: source.nativeId,
                requestedQuality: "flac", provider: AdmissionAccounts.provider)
            let gate = MusicNativeCandidateDiscoveryBarrier(), accounts = AdmissionAccounts()
            if phase == "scope" { await accounts.holdScope(gate) }
            let file = AdmissionLookupCache(hit: .init(identity: identity,
                fileURL: URL(fileURLWithPath: "/synthetic-cache/current.flac"), duration: 100, measuredQuality: "flac",
                expiresAt: NativeMusicFixture.date.addingTimeInterval(3600)),
                gate: MusicNativeCandidateDiscoveryBarrier(), prepareGate: phase == "file" ? gate : nil)
            let service = NativeMusicService(database: try MusicNativeCandidateDiscoveryFixture.store(song: song),
                accounts: accounts, platformClient: try NativeMusicFixture.client(), audioFileCache: file)
            try await service.setAccess(available: true, generation: 1)
            let before = try await service.read()
            let duration = phase == "scope" ? 130.0 : 100.0
            let audio = MusicNativeAudio(url: URL(string: "https://isure.stream.qqmusic.qq.com/current.flac")!,
                delivery: .init(quality: "flac", evidence: .officialResponse, duration: duration))
            let task = Task { try await service.prepareAudioCache(audio: audio, song: song, selection: identity.selection,
                attemptID: UUID().uuidString.lowercased(), playbackAuthorization: MusicQueueAuthorization(), preparing: true) }
            await gate.started(1)
            let corrected = try MusicModel.normalize(MusicNativeCandidateDiscoveryFixture.track(title: "Current prepared title", duration: 130))
            _ = try await service.adoptSongs([corrected], authoritative: true)
            await gate.release()
            if phase == "scope" {
                let result = try await task.value
                let prepared = try XCTUnwrap(result)
                XCTAssertEqual(prepared.expectedDuration, 130)
                prepared.source?.retire()
                prepared.playbackAuthorization.revoke(); prepared.authorization.revoke()
            } else {
                do { _ = try await task.value; XCTFail("A stale receipt escaped final file preparation") }
                catch { XCTAssertEqual(MusicServiceError.sanitized(error), .invalidMediaCandidate) }
            }
            let requests = await file.requests
            XCTAssertEqual(requests.count, 1)
            XCTAssertEqual(requests.first?.expectedDuration, phase == "scope" ? 130 : 100)
            let after = try await service.read()
            XCTAssertEqual(after.queue, before.queue)
            XCTAssertEqual(after.playback, before.playback)
            let history = try await service.playedSongs()
            XCTAssertTrue(history.isEmpty)
            await service.close()
        }
    }

    func testPreparedCachedFileRechecksCurrentMediaAtActivation() async throws {
        let song = try NativeMusicFixture.storedSong(MusicNativeCandidateDiscoveryFixture.track(duration: 100))
        let source = try XCTUnwrap(MusicModel.sources(song).first)
        let identity = MusicAudioFileCacheIdentity(scope: "script-history", songID: song["songId"].string,
            sourceKey: source.sourceKey, platform: source.platform, nativeID: source.nativeId,
            requestedQuality: "flac", provider: "historical-script")
        let gate = MusicNativeCandidateDiscoveryBarrier()
        await gate.release()
        let file = AdmissionLookupCache(hit: .init(identity: identity,
            fileURL: URL(fileURLWithPath: "/synthetic-cache/current.flac"), duration: 100, measuredQuality: "flac",
            expiresAt: NativeMusicFixture.date.addingTimeInterval(3600)), gate: gate)
        let service = NativeMusicService(database: try MusicNativeCandidateDiscoveryFixture.store(song: song),
            platformClient: try NativeMusicFixture.client(), audioFileCache: file, clock: { NativeMusicFixture.date })
        try await service.setAccess(available: true, generation: 1)
        let result = try await service.cachedPlayback(attemptID: UUID().uuidString.lowercased(), occurrenceID: "entry-1",
            selection: nil, requestedQuality: "flac", preparing: true)
        let prepared = try XCTUnwrap(result)
        let corrected = try MusicModel.normalize(MusicNativeCandidateDiscoveryFixture.track(duration: 130))
        _ = try await service.adoptSongs([corrected], authoritative: true)
        do { try await service.activateSource(attemptID: prepared.candidate.attemptId); XCTFail("Stale prepared media activated") }
        catch { XCTAssertEqual(MusicServiceError.sanitized(error), .invalidMediaCandidate) }
        await service.discardSource(attemptID: prepared.candidate.attemptId)
        let history = try await service.playedSongs()
        XCTAssertTrue(history.isEmpty)
        await service.close()
    }

    func testCachedPeerPlaybackUsesSharedRecordingWithoutQueueWrite() async throws {
        for preparing in [false, true] {
            let original = try NativeMusicFixture.storedSong(MusicNativeCandidateDiscoveryFixture.track("wy", id: "101"))
            let peer = try MusicModel.normalize(MusicNativeCandidateDiscoveryFixture.track(id: "PeerMID"))
            let source = try XCTUnwrap(MusicModel.sources(peer).first)
            let identity = MusicAudioFileCacheIdentity(scope: "script-history", songID: original["songId"].string,
                sourceKey: source.sourceKey, platform: source.platform, nativeID: source.nativeId,
                requestedQuality: "flac",
                provider: "historical-script")
            let gate = MusicNativeCandidateDiscoveryBarrier()
            await gate.release()
            let file = AdmissionLookupCache(hit: .init(identity: identity,
                fileURL: URL(fileURLWithPath: "/synthetic-cache/recording.flac"), duration: 180,
                measuredQuality: "flac", expiresAt: NativeMusicFixture.date.addingTimeInterval(3600)), gate: gate)
            let store = try MusicNativeCandidateDiscoveryFixture.store(song: original)
            let service = NativeMusicService(database: store, platformClient: try NativeMusicFixture.client(), audioFileCache: file,
                clock: { NativeMusicFixture.date })
            try await service.setAccess(available: true, generation: 1)
            try await service.retainSongMetadata([peer])
            let before = try await service.read(), writes = store.snapshot().2
            let hit = try await service.cachedPlayback(attemptID: UUID().uuidString.lowercased(),
                occurrenceID: "entry-1", selection: identity.selection, requestedQuality: nil, preparing: preparing)
            let candidate = try XCTUnwrap(hit?.candidate)
            XCTAssertEqual(hit?.selection, identity.selection)
            if preparing { try await service.activateSource(attemptID: candidate.attemptId) }
            let prepared = try await service.read()
            XCTAssertEqual(prepared.queue, before.queue)
            XCTAssertEqual(prepared.playback, before.playback)
            XCTAssertEqual(store.snapshot().2, writes)
            let accepted = try await service.record(.init(attemptId: candidate.attemptId, occurrenceId: "entry-1",
                resourceId: candidate.resourceId, kind: .playing, position: 24, duration: 180))
            XCTAssertTrue(accepted)
            let after = try await service.read()
            XCTAssertEqual(after.queue, before.queue)
            let recorded = try XCTUnwrap(after.history["days"].array.first?["songs"].array.first)
            XCTAssertTrue((try after.fixtureSong(recorded["songId"].string))["sources"].array
                .contains { $0["sourceKey"].string == source.sourceKey } == true)
            await service.close()
        }
    }

    func testLookupRechecksExclusionAfterSuspensionWithoutBlockingOtherSongsOrPlatforms() async throws {
        for official in [false, true] {
            for excluded in ["same", "other-song", "other-platform", "other-provider"] {
                let song = try MusicNativeCandidateDiscoveryFixture.song()
                let store = try MusicNativeCandidateDiscoveryFixture.store(song: song)
                var document = try MusicDocument(store.read(), runID: "cache-admission")
                let other = try NativeMusicFixture.storedSong(MusicNativeCandidateDiscoveryFixture.track(id: "OtherMID", title: "Other recording"))
                XCTAssertNotEqual(other["songId"], song["songId"], "The unrelated-song control must be a different recording")
                document.library["library"]["songs"][other["songId"].string] = other
                document.queue["entries"] = .array(document.queue["entries"].array + [
                    .obj([("id", .string("entry-2")), ("songId", other["songId"])])])
                document.queue["serial"] = .n(2)
                store.replace(try document.encode(changed: ["library", "queue"]))
                let source = try XCTUnwrap(MusicModel.sources(song).first)
                let provider = official ? AdmissionAccounts.provider : "historical-script"
                let scope = official ? "old-account-scope" : "script-history"
                let identity = MusicAudioFileCacheIdentity(scope: scope, songID: song["songId"].string,
                    sourceKey: source.sourceKey, platform: "tx", nativeID: source.nativeId, requestedQuality: "flac",
                    provider: provider)
                let gate = MusicNativeCandidateDiscoveryBarrier()
                let file = AdmissionLookupCache(hit: .init(identity: identity,
                    fileURL: URL(fileURLWithPath: "/synthetic-cache/recording.flac"), duration: 180,
                    measuredQuality: "flac", expiresAt: NativeMusicFixture.date.addingTimeInterval(3600)), gate: gate)
                let service = NativeMusicService(database: store, accounts: AdmissionAccounts(), platformClient: try NativeMusicFixture.client(),
                    audioFileCache: file, clock: { NativeMusicFixture.date })
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

    func testPreparationDownloadsBeforePlayingAndPauseDoesNotStopIt() async throws {
        let clock = MusicRecoveryTestClock(), bytes = AdmissionBytes()
        let database = try storageTestDatabase(self), root = database.dataRoot.appendingPathComponent("audio")
        let real = NativeMusicAudioFileCache(database: database, rootURL: root, clock: { clock.now() }, transport: bytes, inspector: AdmissionMeasurement())
        let file = AdmissionFillCache(real: real, clock: clock), accounts = AdmissionAccounts()
        let song = try MusicNativeCandidateDiscoveryFixture.song()
        let store = try MusicNativeCandidateDiscoveryFixture.store(song: song)
        let service = NativeMusicService(database: store, accounts: accounts, platformClient: try NativeMusicFixture.client(),
            audioFileCache: file, clock: { clock.now() })
        try await service.setAccess(available: true, generation: 1)
        clock.advance(1)
        let session = try await service.sourceSession(occurrenceID: "entry-1", expectedQueueRevision: 1)
        let selected = try await session.match(intent: .quality("flac"))
        try await service.activateSource(attemptID: selected.candidate.attemptId)
        let source = try XCTUnwrap(selected.candidate.byteSource)
        let paused = try await service.record(fact(selected.candidate, kind: .paused))
        XCTAssertTrue(paused)
        _ = try await source.information()
        let data = try await source.read(offset: 0, count: 32)
        XCTAssertEqual(data.count, 32, "Preparation, not playback position, drives the whole transfer")
        let requests = await file.requests, calls = await bytes.calls
        XCTAssertEqual(requests.count, 1); XCTAssertEqual(calls, 1)
        XCTAssertFalse(source.progress.complete, "Unplayed media is not a published cache entry")
        let progressed = try await service.record(fact(selected.candidate, kind: .progress))
        XCTAssertTrue(progressed)
        let noHistory = try await service.playedSongs()
        XCTAssertEqual(noHistory.count, 0, "Progress alone cannot manufacture played history")
        let played = try await service.record(fact(selected.candidate))
        XCTAssertTrue(played)
        _ = try await service.record(fact(selected.candidate, kind: .paused))
        await drainCaptures(service)
        XCTAssertTrue(source.progress.complete)
        let history = try await service.playedSongs()
        XCTAssertEqual(history.count, 1)
        let scope = await accounts.scope
        let hit = await real.lookup(scope: scope.scope, songID: song["songId"].string,
            authorization: scope.authorization, accepts: { _ in true })
        XCTAssertNotNil(hit)
        // Publication must not retire the reader that is still playing this file.
        let again = try await source.read(offset: 0, count: 32)
        XCTAssertEqual(again, data)
        await service.close()
    }

    func testInactiveFactsCannotPublishUnplayedDownloadOrCreateHistory() async throws {
        for kind: MusicPlaybackFact.Kind in [.progress, .paused, .ended, .failed, .stopped] {
            let clock = MusicRecoveryTestClock(), database = try storageTestDatabase(self)
            let root = database.dataRoot.appendingPathComponent("audio")
            let real = NativeMusicAudioFileCache(database: database, rootURL: root, clock: { clock.now() }, transport: AdmissionBytes(), inspector: AdmissionMeasurement())
            let accounts = AdmissionAccounts()
            let service = NativeMusicService(database: try MusicNativeCandidateDiscoveryFixture.store(), accounts: accounts,
                platformClient: try NativeMusicFixture.client(), audioFileCache: real, clock: { clock.now() })
            try await service.setAccess(available: true, generation: 1)
            let session = try await service.sourceSession(occurrenceID: "entry-1", expectedQueueRevision: 1)
            let selected = try await session.match(intent: .quality("flac"))
            try await service.activateSource(attemptID: selected.candidate.attemptId)
            let source = try XCTUnwrap(selected.candidate.byteSource)
            _ = try await source.information()
            _ = try await source.read(offset: 0, count: 32)
            _ = try await service.record(fact(selected.candidate, kind: kind))
            source.retire()
            await drainCaptures(service)
            XCTAssertFalse(source.progress.complete)
            let history = try await service.playedSongs()
            XCTAssertTrue(history.isEmpty, String(describing: kind))
            await service.close()
        }
    }

    func testPreparationRejectsOldAttemptAfterScopeSuspensionAndQueueChange() async throws {
        let clock = MusicRecoveryTestClock(), bytes = AdmissionBytes(), database = try storageTestDatabase(self)
        let root = database.dataRoot.appendingPathComponent("audio")
        let real = NativeMusicAudioFileCache(database: database, rootURL: root, clock: { clock.now() }, transport: bytes, inspector: AdmissionMeasurement())
        let accounts = AdmissionAccounts(), file = AdmissionFillCache(real: real, clock: clock)
        let service = NativeMusicService(database: try MusicNativeCandidateDiscoveryFixture.store(), accounts: accounts,
            platformClient: try NativeMusicFixture.client(), audioFileCache: file, clock: { clock.now() })
        try await service.setAccess(available: true, generation: 1)
        let session = try await service.sourceSession(occurrenceID: "entry-1", expectedQueueRevision: 1)
        let old = try await session.match(intent: .quality("flac"))
        try await service.activateSource(attemptID: old.candidate.attemptId)
        _ = try await service.record(fact(old.candidate, kind: .paused))
        let captured = await service.audioCacheAttempt
        let staged = try XCTUnwrap(captured)
        let audio = try XCTUnwrap(staged.audio)
        let current = try await service.read(), song = try await service.entrySong(current.queue["entries"].array[0], current)
        let gate = MusicNativeCandidateDiscoveryBarrier()
        await accounts.holdScope(gate)
        let preparing = Task {
            try await service.prepareAudioCache(audio: audio, song: song, selection: old.selection,
                attemptID: old.candidate.attemptId, playbackAuthorization: MusicQueueAuthorization())
        }
        await gate.started(1)
        let page = try await service.search(platform: "tx", query: "Different recording", page: 1, limit: 30)
        let other = try XCTUnwrap(page.items.first), state = try await service.state()
        _ = try await service.changeQueue(.replace([other.ref], index: 0), expectedRevision: state.revision)
        await gate.release()
        do { _ = try await preparing.value; XCTFail("An old recording escaped preparation") }
        catch { XCTAssertEqual(MusicServiceError.sanitized(error), .staleResult) }
        let requests = await file.requests
        XCTAssertEqual(requests.count, 1, "Stale work must not start another transfer")
        await service.close()
    }

    func testPreparationRechecksMediaAfterScopeRotationOrMediaRevocation() async throws {
        for rotation in [false, true] { try await checkRevokedPreparation(rotation: rotation) }
    }
    private func checkRevokedPreparation(rotation: Bool) async throws {
        let clock = MusicRecoveryTestClock(), bytes = AdmissionBytes(), database = try storageTestDatabase(self)
        let root = database.dataRoot.appendingPathComponent("audio")
        let real = NativeMusicAudioFileCache(database: database, rootURL: root, clock: { clock.now() }, transport: bytes, inspector: AdmissionMeasurement())
        let file = AdmissionFillCache(real: real, clock: clock), accounts = AdmissionAccounts()
        let song = try MusicNativeCandidateDiscoveryFixture.song(), source = try XCTUnwrap(MusicModel.sources(song).first)
        let service = NativeMusicService(database: try MusicNativeCandidateDiscoveryFixture.store(song: song), accounts: accounts,
            platformClient: try NativeMusicFixture.client(), audioFileCache: file, clock: { clock.now() })
        try await service.setAccess(available: true, generation: 1)
        let options = await accounts.nativeOptions(platform: .qq, nativeID: source.nativeId, sourceKey: source.sourceKey)
        let selection = try XCTUnwrap(options.first).selection(quality: "flac")
        let candidate = try await service.resolve(attemptId: UUID().uuidString.lowercased(), occurrenceId: "entry-1", selection: selection)
        let captured = await service.audioCacheAttempt
        let original = try XCTUnwrap(captured), audio = try XCTUnwrap(original.audio)
        let gate = MusicNativeCandidateDiscoveryBarrier()
        await accounts.holdScope(gate)
        let preparing = Task {
            try await service.prepareAudioCache(audio: audio, song: song, selection: selection,
                attemptID: candidate.attemptId, playbackAuthorization: MusicQueueAuthorization())
        }
        await gate.started(1)
        if rotation { await accounts.rotate() } else { await accounts.revokeMedia() }
        await gate.release()
        do { _ = try await preparing.value; XCTFail("Revoked media adopted a replacement account") }
        catch { XCTAssertEqual(MusicServiceError.sanitized(error), .staleAccess) }
        let requests = await file.requests
        XCTAssertEqual(requests.count, 1)
        let scope = await accounts.scope
        let hit = await real.lookup(scope: scope.scope, songID: song["songId"].string,
            authorization: scope.authorization, accepts: { _ in true })
        XCTAssertNil(hit)
        await service.close()
    }

    func testDirectRealPortRejectsRevokedAudioEvenWithFreshRequestFences() async throws {
        for revoke in [false, true] {
            let clock = MusicRecoveryTestClock(), bytes = AdmissionBytes()
            let database = try storageTestDatabase(self), root = database.dataRoot.appendingPathComponent("audio")
            let real = NativeMusicAudioFileCache(database: database, rootURL: root, clock: { clock.now() }, transport: bytes, inspector: AdmissionMeasurement())
            let media = MusicQueueAuthorization()
            if revoke { media.revoke() }
            let identity = MusicAudioFileCacheIdentity(scope: "replacement-account-scope", songID: "synthetic-song", sourceKey: "tx:synthetic",
                platform: "tx", nativeID: "synthetic", requestedQuality: "flac", provider: AdmissionAccounts.provider)
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

    func testPreparationRejectsAlreadyRevokedMediaBeforeScopeRead() async throws {
        let accounts = AdmissionAccounts(), gate = MusicNativeCandidateDiscoveryBarrier()
        let song = try MusicNativeCandidateDiscoveryFixture.song()
        let source = try XCTUnwrap(MusicModel.sources(song).first)
        let hit = MusicAudioFileCacheHit(identity: .init(scope: "unused", songID: song["songId"].string,
            sourceKey: source.sourceKey, platform: "tx", nativeID: source.nativeId, requestedQuality: "flac",
            provider: AdmissionAccounts.provider), fileURL: URL(fileURLWithPath: "/unused.flac"),
            duration: 180, measuredQuality: "flac", expiresAt: NativeMusicFixture.date.addingTimeInterval(3600))
        let service = NativeMusicService(database: try MusicNativeCandidateDiscoveryFixture.store(song: song), accounts: accounts,
            platformClient: try NativeMusicFixture.client(), audioFileCache: AdmissionLookupCache(hit: hit, gate: gate))
        try await service.setAccess(available: true, generation: 1)
        let media = MusicQueueAuthorization(); media.revoke()
        do {
            _ = try await service.prepareAudioCache(audio: AdmissionAccounts.audio(media), song: song, selection: hit.identity.selection,
                attemptID: UUID().uuidString.lowercased(), playbackAuthorization: MusicQueueAuthorization())
            XCTFail("Revoked media reached staging")
        } catch { XCTAssertEqual(MusicServiceError.sanitized(error), .staleAccess) }
        let reads = await accounts.scopeReads
        XCTAssertEqual(reads, 0)
        await service.close()
    }

    private func drainCaptures(_ service: NativeMusicService) async {
        let captures = await Array(service.audioCacheCaptures.values)
        for capture in captures { await capture.value }
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
    private var resolveGate: MusicNativeCandidateDiscoveryBarrier?
    var scopeReads = 0
    var resolutionRequests: [(platform: MusicAccountPlatform, nativeID: String, selection: MusicPlaybackSelection)] = []
    static func audio(_ authorization: MusicQueueAuthorization) -> MusicNativeAudio {
        .init(url: URL(string: "https://isure.stream.qqmusic.qq.com/synthetic.flac")!, fileCachePolicy: .init(allowed: true),
            delivery: .init(quality: "flac", evidence: .officialResponse, duration: 180), playbackAuthorization: authorization)
    }
    func holdScope(_ gate: MusicNativeCandidateDiscoveryBarrier) { scopeGate = gate }
    func holdResolve(_ gate: MusicNativeCandidateDiscoveryBarrier) { resolveGate = gate }
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
    func nativeResolve(platform: MusicAccountPlatform, nativeID: String, selection: MusicPlaybackSelection) async -> MusicNativeAudio {
        resolutionRequests.append((platform, nativeID, selection))
        if let gate = resolveGate { resolveGate = nil; await gate.enter() }
        return Self.audio(media)
    }
    func nativeRecommendations(platform: MusicAccountPlatform, kind: String, cursor: String?) throws -> MusicNativeTrackPage { throw MusicServiceError.unsupported }
    func nativeCollections(platform: MusicAccountPlatform, cursor: String?) throws -> MusicAccountCollectionPage { throw MusicServiceError.unsupported }
    func nativeTracks(platform: MusicAccountPlatform, collectionID: String, cursor: String?) throws -> MusicNativeTrackPage { throw MusicServiceError.unsupported }
}

private actor AdmissionLookupCache: MusicAudioFileCaching {
    let hit: MusicAudioFileCacheHit
    let gate: MusicNativeCandidateDiscoveryBarrier
    let prepareGate: MusicNativeCandidateDiscoveryBarrier?
    var requests: [MusicAudioFilePlaybackRequest] = []
    init(hit: MusicAudioFileCacheHit, gate: MusicNativeCandidateDiscoveryBarrier,
         prepareGate: MusicNativeCandidateDiscoveryBarrier? = nil) {
        self.hit = hit; self.gate = gate; self.prepareGate = prepareGate
    }
    func lookup(scope: String, songID: String, authorization: MusicQueueAuthorization,
                accepts: @escaping @Sendable (MusicAudioFileCacheIdentity) -> Bool) async -> MusicAudioFileCacheHit? {
        guard authorization.isActive, hit.identity.scope == scope, hit.identity.songID == songID, accepts(hit.identity) else { return nil }
        await gate.enter()
        return hit // Eligibility was captured before suspension, just like an asynchronous cache port.
    }
    func preparePlayback(_ request: MusicAudioFilePlaybackRequest) async throws -> any MusicAudioFilePlayback {
        requests.append(request)
        await prepareGate?.enter()
        return MusicTestByteSource()
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
    var requests: [MusicAudioFilePlaybackRequest] = []
    init(real: NativeMusicAudioFileCache, clock: MusicRecoveryTestClock) { self.real = real; self.clock = clock }
    func lookup(scope: String, songID: String, authorization: MusicQueueAuthorization,
                accepts: @escaping @Sendable (MusicAudioFileCacheIdentity) -> Bool) async -> MusicAudioFileCacheHit? {
        await real.lookup(scope: scope, songID: songID, authorization: authorization, accepts: accepts)
    }
    func preparePlayback(_ request: MusicAudioFilePlaybackRequest) async throws -> any MusicAudioFilePlayback {
        requests.append(request)
        return try await real.preparePlayback(request)
    }
    func storeAfterPlayback(_ request: MusicAudioFileCacheRequest) async -> MusicAudioFileCacheVersion? { await real.storeAfterPlayback(request) }
    func touch(_ identity: MusicAudioFileCacheIdentity, version: MusicAudioFileCacheVersion, confirmedAt: Date, authorization: MusicQueueAuthorization) async {
        await real.touch(identity, version: version, confirmedAt: confirmedAt, authorization: authorization)
    }
    func remove(_ identity: MusicAudioFileCacheIdentity, version: MusicAudioFileCacheVersion) async { await real.remove(identity, version: version) }
    func cancelPending() async { await real.cancelPending() }
    func clear() async { await real.clear() }
    func clearOfficial() async { await real.clearOfficial(); clock.advance(1) }
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
