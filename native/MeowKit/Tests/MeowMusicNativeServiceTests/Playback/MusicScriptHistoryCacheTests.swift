import Foundation
import XCTest
import MeowContracts
@testable import MeowMusicFeature
import MeowMusicPlatform
@testable import MeowMusicNativeService

private actor HistoryScripts: MusicScriptResolving {
    var requests = 0, lookups = 0, lyricRequests = 0
    var enabled = true, generation = 1
    func configure(enabled: Bool, generation: Int) { self.enabled = enabled; self.generation = generation }
    func sources() -> [MusicScriptSource] { lookups += 1; return enabled ? [snapshot()] : [] }
    func source(installationID: String) throws -> MusicScriptSource {
        lookups += 1
        guard enabled else { throw MusicServiceError.sourceUnavailable }
        return snapshot()
    }
    func snapshot() -> MusicScriptSource {
        .init(installationID: "history-script", generation: generation, enabled: enabled, state: enabled ? .ready : .disabled,
              version: String(generation), contentHash: String(repeating: "a", count: 64), revisionHash: String(repeating: "a", count: 64),
              capabilities: [.init(siteID: "tx", title: "History", actions: ["musicUrl"], qualities: ["flac", "128k"])])
    }
    func resolve(request: MusicScriptRequest) -> MusicScriptResult<MusicNativeAudio> {
        requests += 1
        return .init(value: .init(url: URL(string: "https://script-media.invalid/recording.flac?secret=not-persisted")!,
            fileCachePolicy: .init(allowed: true), delivery: .init(quality: "flac", evidence: .officialResponse, duration: 180)), source: snapshot())
    }
    func lyrics(request: MusicScriptRequest) -> MusicScriptResult<MusicNativeLyrics> {
        lyricRequests += 1
        return .init(value: .init(text: ""), source: snapshot())
    }
    func counts() -> (Int, Int, Int) { (requests, lookups, lyricRequests) }
}
private struct HistoryBytes: NativeMusicAudioCacheTransport {
    func transfer(_ request: NativeMusicAudioCacheHTTPRequest, into sink: NativeMusicAudioCacheSink) async throws {
        try await sink.accept(.init(url: request.url, status: 200,
            headers: ["Content-Length": "32", "Content-Type": "audio/wav"]))
        try sink.write(Data(repeating: 0, count: 32)); try sink.finishBody()
    }
}
private struct HistoryMeasurement: NativeMusicAudioCacheInspecting {
    // Deterministic measurement; real complete decoding is covered by the platform cache tests.
    func inspect(_ file: NativeMusicAudioCacheFile, expectedDuration: Double) -> NativeMusicAudioCacheInspection {
        .init(container: "wav", codec: "pcm", duration: 180, decodedFrames: 7_938_000, sampleRate: 44_100,
              channels: 2, bitrate: 1_411_200, bitDepth: 16, lossless: true, complete: true, protectedContent: false)
    }
}

@MainActor private final class HistoryPlaybackPlayer: MusicPlaybackDriving {
    var resources: [PlaybackResource] = []
    func play(_ resource: PlaybackResource) -> MusicPlayerAttempt {
        resources.append(resource); return .init(id: UUID(), resourceID: resource.id)
    }
    func stop(_ attempt: MusicPlayerAttempt) {}
    func pause(_ attempt: MusicPlayerAttempt) {}
    func resume(_ attempt: MusicPlayerAttempt) {}
    func seek(_ position: Double, attempt: MusicPlayerAttempt) -> Bool { true }
}

final class MusicScriptHistoryCacheTests: XCTestCase {
    @MainActor
    func testPlayedScriptFileSurvivesRestartDeletionAndUpdateWithoutConsultingScriptOrAccount() async throws {
        for change in ["unchanged", "updated", "disabled", "deleted"] {
            let root = try FileManager.default.url(for: .cachesDirectory, in: .userDomainMask, appropriateFor: nil, create: true)
                .appendingPathComponent("meow-script-history-test-" + UUID().uuidString)
            defer { try? FileManager.default.removeItem(at: root) }
            let clock = MusicRecoveryTestClock(), scripts = HistoryScripts()
            let store = try MusicNativeCandidateDiscoveryFixture.store()
            func cache() -> NativeMusicAudioFileCache {
                .init(rootURL: root, clock: { clock.now() }, transport: HistoryBytes(), inspector: HistoryMeasurement())
            }
            let file = cache()
            let service = NativeMusicService(document: store, scripts: scripts, qq: NativeMusicQQFixture(),
                audioFileCache: file, transport: NativeMusicNoNetwork(), clock: { clock.now() })
            try await service.setAccess(available: true, generation: 1)
            clock.advance(1) // Actual play follows the access-transition capture cutoff.
            let session = try await service.sourceSession(occurrenceID: "entry-1", expectedQueueRevision: 1)
            let selected = try await session.match(quality: "flac", sourceID: nil)
            XCTAssertNotNil(selected.selection.scriptIdentity)
            XCTAssertEqual(selected.candidate.resolvedSelection, selected.selection)
            try await service.activateSource(attemptID: selected.candidate.attemptId)
            let before = await file.lookup(scope: "script-history", songID: try MusicNativeCandidateDiscoveryFixture.song()["songId"].string,
                authorization: MusicQueueAuthorization(), accepts: { _ in true })
            XCTAssertNil(before, "Resolve/activate is not actual playback")
            _ = try await service.record(.init(attemptId: selected.candidate.attemptId, occurrenceId: "entry-1",
                resourceId: selected.candidate.resourceId, kind: .playing, position: 20, duration: 180))
            // Drain the actor's finite capture, not polling filesystem/static logs.
            try await service.stageAudioCacheForTestsToDrain()
            let captured = await file.lookup(scope: "script-history", songID: try MusicNativeCandidateDiscoveryFixture.song()["songId"].string,
                authorization: MusicQueueAuthorization(), accepts: { _ in true })
            XCTAssertEqual(captured?.identity.provider, "history-script")
            XCTAssertEqual(captured?.identity.generation, 1)
            await service.close()
            await scripts.configure(enabled: change == "unchanged" || change == "updated",
                generation: change == "unchanged" ? 1 : 9)
            let beforeCounts = await scripts.counts()
            let reopenedFile = cache()
            let accounts = MusicRecoveryTestAccounts(platform: .qq, nativeID: "UnusedMID", clock: clock)
            let reopened = NativeMusicService(document: store, scripts: change == "deleted" ? nil : scripts,
                accounts: accounts, qq: NativeMusicQQFixture(),
                audioFileCache: reopenedFile, transport: NativeMusicNoNetwork(), clock: { clock.now() })
            try await reopened.setAccess(available: true, generation: 2)
            let state = try await reopened.state()
            let player = HistoryPlaybackPlayer(), model = MusicViewModel(service: reopened, player: player)
            await model.load()
            XCTAssertTrue(player.resources.isEmpty); XCTAssertNil(model.candidate)
            await model.prepareCurrent()
            XCTAssertNil(model.error)
            let candidate = try XCTUnwrap(model.candidate)
            XCTAssertTrue(candidate.isCachedAudio); XCTAssertTrue(candidate.url.isFileURL)
            XCTAssertTrue(candidate.fileAuthorization?.isActive == true)
            XCTAssertNil(candidate.resolvedSelection.scriptIdentity)
            XCTAssertEqual(model.activePlaybackSelection, candidate.resolvedSelection)
            XCTAssertEqual(player.resources.map(\.url), [candidate.url])
            let featureScriptCounts = await scripts.counts(), featureAccountCalls = await accounts.counts()
            let featureScopeReads = await accounts.scopeReadCount()
            XCTAssertEqual(featureScriptCounts.0, beforeCounts.0); XCTAssertEqual(featureScriptCounts.1, beforeCounts.1)
            XCTAssertEqual(featureScriptCounts.2, beforeCounts.2)
            XCTAssertEqual(featureScopeReads, 0); XCTAssertEqual(featureAccountCalls.options, 0)
            XCTAssertEqual(featureAccountCalls.resolves, 0); XCTAssertEqual(featureAccountCalls.refreshes, 0)
            await model.close()
            let next = try await reopened.sourceSession(occurrenceID: "entry-1", expectedQueueRevision: state.revision)
            let hit = try await next.match(quality: "flac", sourceID: nil)
            XCTAssertTrue(hit.candidate.isCachedAudio); XCTAssertTrue(hit.candidate.url.isFileURL)
            XCTAssertEqual(hit.selection.installationId, selected.selection.installationId)
            XCTAssertEqual(hit.selection.generation, selected.selection.generation)
            XCTAssertEqual(hit.selection.siteId, selected.selection.siteId)
            XCTAssertEqual(hit.selection.sourceKey, selected.selection.sourceKey)
            XCTAssertEqual(hit.selection.quality, selected.selection.quality)
            XCTAssertNil(hit.selection.scriptIdentity)
            XCTAssertEqual(hit.candidate.resolvedSelection, hit.selection)
            XCTAssertEqual(candidate.resolvedSelection, hit.selection)
            let afterCounts = await scripts.counts()
            XCTAssertEqual(afterCounts.0, beforeCounts.0); XCTAssertEqual(afterCounts.1, beforeCounts.1)
            XCTAssertEqual(afterCounts.2, beforeCounts.2)
            let scopeReads = await accounts.scopeReadCount(), accountCalls = await accounts.counts()
            XCTAssertEqual(scopeReads, 0); XCTAssertEqual(accountCalls.options, 0); XCTAssertEqual(accountCalls.resolves, 0)
            let official = MusicPlaybackSelection(installationId: "official:recovery-synthetic", generation: 1,
                siteId: hit.selection.siteId, sourceKey: hit.selection.sourceKey, quality: "flac")
            _ = try await reopened.cachedPlayback(attemptID: UUID().uuidString.lowercased(), occurrenceID: "entry-1", selection: official)
            try await reopened.activateSource(attemptID: hit.candidate.attemptId)
            _ = try await reopened.record(.init(attemptId: hit.candidate.attemptId, occurrenceId: "entry-1",
                resourceId: hit.candidate.resourceId, kind: .playing, position: 21, duration: 180))
            await accounts.rotate()
            _ = try await reopened.cachedPlayback(attemptID: UUID().uuidString.lowercased(), occurrenceID: "entry-1", selection: official)
            XCTAssertTrue(hit.candidate.fileAuthorization?.isActive == true, "Account scope rotation must not retire a playing script file")
            let progress = try await reopened.record(.init(attemptId: hit.candidate.attemptId, occurrenceId: "entry-1",
                resourceId: hit.candidate.resourceId, kind: .progress, position: 25, duration: 180))
            XCTAssertTrue(progress)
            await reopenedFile.clearOfficial()
            let retained = try await next.match(quality: "flac", sourceID: nil)
            XCTAssertTrue(retained.candidate.isCachedAudio)
            try await reopened.excludeSource(for: .init(kind: .queue, id: "entry-1"), provider: "history-script", platform: "tx")
            do { _ = try await next.match(quality: "flac", sourceID: nil); XCTFail("Excluded history played") } catch {}
            let rows = try await reopened.sourceExclusions()
            try await reopened.restoreSourceExclusion(id: XCTUnwrap(rows.first?.id))
            let restored = try await next.match(quality: "flac", sourceID: nil)
            XCTAssertTrue(restored.candidate.isCachedAudio)
            let index = try Data(contentsOf: root.appendingPathComponent("index.json"))
            XCTAssertFalse(String(decoding: index, as: UTF8.self).contains("secret="))
            await reopened.close()
        }
    }
}

// Test-only deterministic actor drain: production does not expose capture internals.
private extension NativeMusicService {
    func stageAudioCacheForTestsToDrain() async throws {
        // Preparation owns the source before any playing fact; wait for that
        // owner and drain captures without an unbounded sleep.
        try await MusicCandidateDiscoveryDeadline.run(seconds: 3, timing: .init()) {
            while await self.audioCacheAttempt?.source == nil { try Task.checkCancellation(); await Task.yield() }
            let captures = await Array(self.audioCacheCaptures.values)
            for task in captures { await task.value }
        }
    }
}
