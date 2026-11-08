import Foundation
import XCTest
import MeowContracts
import MeowMusicFeature
@testable import MeowMusicNativeService

private actor PlaybackMetadataQQ: MusicPublicQQProviding {
    let gate: NativeMusicGate?
    let failure: MusicServiceError?
    private var searches = 0
    init(gate: NativeMusicGate? = nil, failure: MusicServiceError? = nil) {
        self.gate = gate; self.failure = failure
    }
    func search(query: String, page: Int, limit: Int) async throws -> MusicNativeTrackPage {
        searches += 1
        if let gate { await gate.wait() }
        if let failure { throw failure }
        return .init(items: [MusicNativeCandidateDiscoveryFixture.track(id: "mapped")], total: 1)
    }
    func lyrics(mid: String) -> MusicNativeLyrics { .init(text: "") }
    func calls() -> Int { searches }
}

@MainActor private final class MetadataPlaybackPlayer: MusicPlaybackDriving {
    var resources: [PlaybackResource] = []
    func play(_ resource: PlaybackResource) -> MusicPlayerAttempt {
        resources.append(resource); return .init(id: UUID(), resourceID: resource.id)
    }
    func pause(_ attempt: MusicPlayerAttempt) {}
    func resume(_ attempt: MusicPlayerAttempt) {}
    func stop(_ attempt: MusicPlayerAttempt) {}
    func seek(_ position: Double, attempt: MusicPlayerAttempt) -> Bool { true }
}

final class MusicPlaybackMetadataIntegrationTests: XCTestCase {
    private func seed() throws -> MJ {
        try MusicModel.normalize(MusicNativeCandidateDiscoveryFixture.track("wy", id: "1"))
    }
    func testQueueProjectsSharedFactsWithoutChangingItsSnapshotIdentityOrSearching() async throws {
        let original = try seed(), qq = PlaybackMetadataQQ()
        let service = try await NativeMusicFixture.service(
            MusicNativeCandidateDiscoveryFixture.store(song: original), qq: qq)
        let before = try await service.queue()
        try await service.retainSongMetadata([MusicModel.normalize(
            MusicNativeCandidateDiscoveryFixture.track(id: "mapped"))])
        let after = try await service.queue(), document = try await service.read()
        XCTAssertEqual(after.revision, before.revision); XCTAssertEqual(after.currentId, before.currentId)
        XCTAssertEqual(after.entries.map(\.id), before.entries.map(\.id))
        XCTAssertEqual(after.entries[0].displayIdentity, before.entries[0].displayIdentity)
        XCTAssertEqual(Set(after.entries[0].track.sources.map(\.platform)), ["wy", "tx"])
        XCTAssertEqual(document.queue["entries"].array[0]["song"], original)
        let metadata = await service.sourceDiscoveryState(original), calls = await qq.calls()
        XCTAssertEqual(metadata.entries["tx"]?.checkedAt, 0)
        XCTAssertEqual(metadata.entries["tx"]?.observations, [])
        XCTAssertEqual(calls, 0)
        await service.close()
    }
    func testFullRemoteQueueAndCollectionAdoptUnseenRowsButKeepRawStartIdentity() async throws {
        for saveCollection in [false, true] {
            let original = try seed(), qq = PlaybackMetadataQQ()
            let service = try await NativeMusicFixture.service(
                MusicNativeCandidateDiscoveryFixture.store(song: original), qq: qq)
            _ = try await service.queue()
            var rows = (0..<30).map { MusicRemoteCollectionActionsFixture.track("row-\($0)") }
            rows.append(MusicNativeCandidateDiscoveryFixture.track(id: "mapped"))
            let source = MusicRemoteCollectionActionsFixture.source(MusicRemoteCollectionActionsFixture.pages(rows))
            let preview = try await service.remoteQueuePage(source, cursor: nil)
            XCTAssertEqual(preview.items.count, 30)
            if saveCollection {
                _ = try await service.saveRemoteCollection(source: source, name: "Acquired", intent: .init())
            } else {
                _ = try await service.replaceRemoteQueue(source,
                    start: .init(index: 0, track: preview.items[0]), expectedRevision: 1, intent: .init())
                let document = try await service.read()
                XCTAssertEqual(document.queue["entries"].array.last?["song"]["sources"].array.count, 1,
                               "Shared display metadata cannot rewrite the source snapshot")
            }
            let shared = try await service.sharedSongMetadata(original), calls = await qq.calls()
            XCTAssertEqual(Set(shared["sources"].array.map { $0["source"].string }), ["wy", "tx"])
            XCTAssertEqual(calls, 0, "Full acquired rows are imported, never title-searched")
            await source.close(); await service.close()
        }
    }
    @MainActor func testCurrentDiscoveryMayProjectFactsIntoOtherOccurrencesWithoutCancellingPlayback() async throws {
        let original = try seed(), store = try MusicNativeCandidateDiscoveryFixture.store(song: original)
        var document = try MusicDocument(store.read(), runID: "metadata-consumer")
        document.queue["entries"] = .array(document.queue["entries"].array + [
            .obj([("id", .string("entry-2")), ("song", original)])])
        document.queue["serial"] = .n(2)
        store.replace(try document.encode(changed: ["queue"]))
        let service = try await NativeMusicFixture.service(store, scripts: NativeMusicScriptFixture(), qq: PlaybackMetadataQQ())
        let player = MetadataPlaybackPlayer(), model = MusicViewModel(service: service, player: player)
        await model.load()
        XCTAssertEqual(model.queue[1].track.sources.map(\.platform), ["wy"])
        await model.prepareCurrent()
        XCTAssertNotNil(model.activeResourceID); XCTAssertNil(model.error); XCTAssertEqual(player.resources.count, 1)
        XCTAssertEqual(Set(model.queue[1].track.sources.map(\.platform)), ["wy", "tx"])
        let raw = try await service.read()
        XCTAssertEqual(raw.queue["entries"].array[1]["song"], original)
        await model.close(); await service.close()
    }
    func testInspectionKeepsRequestStartAndCompletionEvidenceIncludingFailure() async throws {
        let failures: [MusicServiceError?] = [nil, .upstreamFailed]
        for failure in failures {
            let original = try seed(), gate = NativeMusicGate(), clock = NativeMusicFixtureClock()
            let qq = PlaybackMetadataQQ(gate: gate, failure: failure)
            let service = NativeMusicService(document: try MusicNativeCandidateDiscoveryFixture.store(song: original),
                scripts: NativeMusicScriptFixture(), qq: qq, enabledPlatforms: [.qq],
                transport: NativeMusicNoNetwork(), clock: { clock.now() })
            try await service.setAccess(available: true, generation: 1)
            let inspection = try await service.inspectSources(for: .init(kind: .queue, id: "entry-1"))
            _ = try await inspection.loadMore(refresh: false)
            await gate.started()
            clock.advance(2)
            try await MusicCandidateDiscoveryDeadline.run(seconds: 3, timing: .init()) {
                await gate.release()
                for try await value in await inspection.updates() where !value.isLoading { return }
                throw MusicServiceError.closed
            }
            let metadata = await service.sourceDiscoveryState(original)
            let observation = try XCTUnwrap(metadata.entries["tx"]?.observations.first)
            XCTAssertEqual(observation.startedAt, NativeMusicFixture.date.timeIntervalSince1970 * 1000)
            XCTAssertEqual(observation.checkedAt, clock.now().timeIntervalSince1970 * 1000)
            XCTAssertEqual(observation.query, "Synthetic recording Fixture")
            XCTAssertEqual(observation.context, "public"); XCTAssertEqual(observation.page, 1)
            XCTAssertEqual(observation.limit, 30); XCTAssertEqual(observation.scope, .recording)
            XCTAssertEqual(observation.outcome, failure == nil ? .found : .failed)
            clock.advance(60)
            _ = try await service.queue()
            let reread = await service.sourceDiscoveryState(original)
            XCTAssertEqual(reread.entries["tx"]?.observations.first, observation,
                           "Projection cannot manufacture a newer search check")
            await inspection.close(); await service.close()
        }
    }
}
