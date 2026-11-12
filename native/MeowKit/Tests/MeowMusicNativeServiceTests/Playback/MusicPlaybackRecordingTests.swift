import Foundation
import XCTest
import MeowMusicFeature
@testable import MeowMusicNativeService

private enum RecordingPreparationError: Error { case preparationFailed }
private actor RecordingFailingBytes: MusicAudioFileCaching {
    private var preparations = 0
    func lookup(scope: String, songID: String, authorization: MusicQueueAuthorization,
                accepts: @escaping @Sendable (MusicAudioFileCacheIdentity) -> Bool) -> MusicAudioFileCacheHit? { nil }
    func storeAfterPlayback(_ request: MusicAudioFileCacheRequest) -> MusicAudioFileCacheVersion? { nil }
    func preparePlayback(_ request: MusicAudioFilePlaybackRequest) throws -> any MusicAudioFilePlayback {
        preparations += 1
        throw RecordingPreparationError.preparationFailed
    }
    func touch(_ identity: MusicAudioFileCacheIdentity, version: MusicAudioFileCacheVersion,
               confirmedAt: Date, authorization: MusicQueueAuthorization) {}
    func remove(_ identity: MusicAudioFileCacheIdentity, version: MusicAudioFileCacheVersion) {}
    func cancelPending() {}
    func clear() {}
    func clearOfficial() {}
    func close() {}
    func count() -> Int { preparations }
}

/// Synthetic byte failures and official/source results; no media or credentials.
final class MusicPlaybackRecordingTests: XCTestCase {
    func testPreparationFailureWithOrWithoutExpandedSourcesKeepsQueueAndOriginalError() async throws {
        for enrich in [false, true] {
            let store = try MusicNativeCandidateDiscoveryFixture.store()
            let bytes = RecordingFailingBytes()
            let service = NativeMusicService(document: store, scripts: NativeMusicScriptFixture(),
                qq: NativeMusicQQFixture(), enabledPlatforms: [.qq], audioFileCache: bytes,
                transport: NativeMusicNoNetwork(), clock: { NativeMusicFixture.date })
            try await service.setAccess(available: true, generation: 1)
            if enrich {
                let peer = try MusicModel.normalize(MusicNativeCandidateDiscoveryFixture.track(id: "PeerMID"))
                try await service.retainSongMetadata([peer])
            }
            let before = store.snapshot()
            let session = try await service.sourceSession(occurrenceID: "entry-1", expectedQueueRevision: 1)
            do {
                _ = try await session.match(intent: .quality("flac"))
                XCTFail("Injected byte preparation failure must surface")
            } catch {
                let failure = MusicServiceError.sanitized(error)
                XCTAssertEqual(failure, .unavailable)
                XCTAssertTrue(error is RecordingPreparationError, "Preparation failure must not become a queue error")
            }
            let count = await bytes.count()
            XCTAssertEqual(count, 1)
            let state = try await service.state()
            XCTAssertEqual(state.currentId, "entry-1")
            XCTAssertEqual(state.revision, 1)
            XCTAssertEqual(store.snapshot().0, before.0)
            XCTAssertEqual(store.snapshot().2, before.2)
            let document = try MusicDocument(store.read(), runID: "audit")
            XCTAssertEqual(document.queue["entries"].array.count, 1)
            XCTAssertEqual(document.queue["entries"].array[0]["song"]["sources"].array.count, 1)
            await session.close(); await service.close()
        }
    }

    func testRemoteQueueAcceptsExpandedDisplayMemberButRejectsDifferentPrimary() async throws {
        let native = MusicNativeCandidateDiscoveryFixture.track()
        let service = try await NativeMusicFixture.service(MusicNativeCandidateDiscoveryFixture.store())
        let peer = try MusicModel.normalize(MusicNativeCandidateDiscoveryFixture.track(id: "PeerMID"))
        try await service.retainSongMetadata([peer])
        let visible = try await service.metadataPage(.init(items: [native], total: 1)).items[0]
        let source = MusicRemoteQueueSource(context: .init(kind: .playlist, id: "synthetic", title: "Audit", platform: "tx"),
            loadPage: { _ in .init(items: [native], total: 1) }, validate: {}, close: {})
        let raw = try await service.remoteQueuePage(source, cursor: nil).items[0]
        XCTAssertEqual(visible.songId, raw.songId)
        XCTAssertEqual(visible.sources.first, raw.sources.first)
        XCTAssertEqual(visible.sources.count, 2)
        XCTAssertEqual(raw.sources.count, 1)
        let wrong = MusicRemoteQueueStart(index: 0, songID: visible.songId,
            primarySource: visible.sources[1])
        do {
            _ = try await service.replaceRemoteQueue(source, start: wrong, expectedRevision: 1, intent: .init())
            XCTFail("A different primary recording must not be accepted")
        } catch { XCTAssertEqual(error as? MusicServiceError, .targetChanged) }
        let before = try await service.state()
        XCTAssertEqual(before.revision, 1)
        let accepted = try await service.replaceRemoteQueue(source, start: .init(index: 0, track: visible),
            expectedRevision: 1, intent: MusicQueueAuthorization())
        XCTAssertEqual(accepted.revision, 2)
        XCTAssertEqual(accepted.total, 1)
        await source.close(); await service.close()
    }

    func testExpandedRecordingServesLyricsHistoryMetadataAndExactRenewalWithoutQueueWrite() async throws {
        for route in ["session", "discovery", "direct"] {
            let original = try MusicModel.normalize(MusicNativeCandidateDiscoveryFixture.track("wy", id: "101"))
            let peer = try MusicModel.normalize(MusicNativeCandidateDiscoveryFixture.track("tx", id: "101"))
            let store = try MusicNativeCandidateDiscoveryFixture.store(song: original)
            let clock = MusicRecoveryTestClock(), accounts = MusicRecoveryTestAccounts(platform: .qq, nativeID: "101", clock: clock)
            let service = NativeMusicService(document: store, accounts: accounts, qq: NativeMusicQQFixture(),
                enabledPlatforms: [.qq], transport: NativeMusicNoNetwork(), clock: { clock.now() })
            try await service.setAccess(available: true, generation: 1)
            try await service.retainSongMetadata([peer])
            let before = try await service.read()
            let options = try await accounts.nativeOptions(platform: .qq, nativeID: "101",
                sourceKey: peer["sources"].array[0]["sourceKey"].string)
            let option = try XCTUnwrap(options.first)
            let candidate: MusicPlaybackCandidate
            if route == "session" {
                let session = try await service.sourceSession(occurrenceID: "entry-1", expectedQueueRevision: 1)
                let prepared = try await session.match(intent: .exact(option.selection(quality: "flac")))
                candidate = prepared.candidate
                try await service.activateSource(attemptID: candidate.attemptId)
            } else if route == "discovery" {
                let session = try await service.beginCandidateDiscovery(occurrenceID: "entry-1", expectedQueueRevision: 1)
                let snapshot = try await session.snapshot(discover: false, force: false)
                let target = try XCTUnwrap(snapshot.options.first { $0.playback.siteId == "tx" }).target(quality: "flac")
                candidate = try await session.resolve(target, attemptID: UUID().uuidString.lowercased())
                await session.close(retainingAttemptID: candidate.attemptId)
            } else {
                candidate = try await service.resolve(attemptId: UUID().uuidString.lowercased(), occurrenceId: "entry-1",
                    selection: option.selection(quality: "flac"))
            }
            let prepared = try await service.read()
            XCTAssertEqual(prepared.queue, before.queue, route)
            XCTAssertEqual(prepared.playback, before.playback, route)
            let accepted = try await service.record(.init(attemptId: candidate.attemptId, occurrenceId: "entry-1",
                resourceId: candidate.resourceId, kind: .playing, position: 24, duration: 180))
            XCTAssertTrue(accepted, route)
            let playing = try await service.read()
            XCTAssertEqual(playing.queue, before.queue, route)
            XCTAssertEqual(playing.history["days"].array.first?["songs"].array.first?["song"]["sources"].array.count, 2)
            let metadata = try await service.metadataCandidates(occurrenceID: "entry-1")
            XCTAssertTrue(metadata.candidates.contains { $0.source == peerSource(peer) }, route)
            let lyrics = try await service.lyrics(occurrenceId: "entry-1", sourceKey: option.sourceKey)
            XCTAssertFalse(lyrics.lines.isEmpty, route)
            let recovery = try await service.recoverPlayback(previousAttemptID: candidate.attemptId,
                attemptID: UUID().uuidString.lowercased(), rejectedURL: candidate.url, position: 25)
            XCTAssertEqual(recovery.selection, candidate.resolvedSelection, route)
            XCTAssertNotEqual(recovery.candidate.url, candidate.url, route)
            let renewed = try await service.read()
            XCTAssertEqual(renewed.queue, before.queue, route)
            await service.close()
        }
    }

    func testFeedbackUsesAcceptedPeerWhileQueueKeepsOriginalMember() async throws {
        let original = try MusicModel.normalize(MusicNativeCandidateDiscoveryFixture.track("wy", id: "101"))
        let peer = try MusicModel.normalize(MusicNativeCandidateDiscoveryFixture.track(id: "PeerMID"))
        let store = try MusicNativeCandidateDiscoveryFixture.store(song: original)
        let accounts = NativeFeedbackAccounts()
        let service = try await NativeMusicFixture.service(store, accounts: accounts)
        _ = try await service.setPlaybackFeedback(platform: .qq, enabled: true, expectedRevision: 0)
        try await service.retainSongMetadata([peer])
        let before = try await service.read()
        let session = try await service.sourceSession(occurrenceID: "entry-1", expectedQueueRevision: 1)
        let prepared = try await session.match(intent: .quality("128k"))
        try await service.activateSource(attemptID: prepared.candidate.attemptId)
        let accepted = try await service.record(.init(attemptId: prepared.candidate.attemptId, occurrenceId: "entry-1",
            resourceId: prepared.candidate.resourceId, kind: .playing, position: 0, duration: 180))
        XCTAssertTrue(accepted)
        await service.consumePlaybackFeedback(NativeFeedbackFixture.sample(1, at: 0, position: 0,
            kind: .playing, attempt: prepared.candidate.attemptId))
        let controller = await service.playbackFeedback
        await controller.flush()
        await service.consumePlaybackFeedback(NativeFeedbackFixture.sample(2, at: 5, position: 5,
            kind: .stopped, running: false, attempt: prepared.candidate.attemptId))
        await controller.flush()
        let reports = await accounts.recorded(), after = try await service.read()
        XCTAssertEqual(reports.map(\.event), [.start, .end])
        XCTAssertTrue(reports.allSatisfy { $0.target.platform == .qq && $0.target.nativeID == "PeerMID" })
        XCTAssertEqual(after.queue, before.queue)
        await session.close(); await service.close()
    }

    private func peerSource(_ song: MJ) -> MusicNativeSource {
        let row = song["sources"].array[0]
        return .init(sourceKey: row["sourceKey"].string, platform: "tx", nativeId: "101")
    }
}
