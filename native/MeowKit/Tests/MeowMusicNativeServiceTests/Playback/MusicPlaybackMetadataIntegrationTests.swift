import Foundation
import XCTest
import MeowContracts
import MeowMusicFeature
import MeowMusicPlatform
import MeowMusicTestSupport
import MeowRuntime
@testable import MeowMusicNativeService

private actor PlaybackMetadataQQ: MusicPlatformFixtureResponding {
    func respond(_ request: MusicPlatformFixture.Request) async throws -> RuntimeValue {
        switch request.operation {
        case "search":
            return try MusicPlatformFixture.page(await search(query: request.parameters[music: "keyword"].musicText,
                page: Int(request.parameters[music: "page"].musicNumber ?? 1),
                limit: Int(request.parameters[music: "limit"].musicNumber ?? 30)), platform: request.platform)
        case "lyrics": return .object(["text": .string(try await lyrics(mid: request.parameters[music: "musicInfo"][music: "songmid"].musicText).text)])
        default: throw MusicPlatformFixture.Failure.remote("UNSUPPORTED")
        }
    }

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
        try NativeMusicFixture.storedSong(MusicNativeCandidateDiscoveryFixture.track("wy", id: "1"))
    }
    func testQueueProjectsSharedFactsWithoutChangingItsSnapshotIdentityOrSearching() async throws {
        let original = try seed(), qq = PlaybackMetadataQQ()
        let service = try await NativeMusicFixture.service(
            MusicNativeCandidateDiscoveryFixture.store(song: original), platform: qq)
        let before = try await service.queue()
        let initialDocument = try await service.read()
        try await service.retainSongMetadata([MusicModel.normalize(
            MusicNativeCandidateDiscoveryFixture.track(id: "mapped"))])
        let after = try await service.queue(), document = try await service.read()
        XCTAssertEqual(after.revision, before.revision); XCTAssertEqual(after.currentId, before.currentId)
        XCTAssertEqual(after.entries.map(\.id), before.entries.map(\.id))
        XCTAssertEqual(after.entries[0].displayIdentity, before.entries[0].displayIdentity)
        XCTAssertEqual(Set(after.entries[0].track.sources.map(\.platform)), ["wy", "tx"])
        XCTAssertEqual(document.queue, initialDocument.queue)
        XCTAssertEqual(document.queue["entries"].array[0]["songId"], original["songId"])
        let metadata = try await service.sourceDiscoveryState(original), calls = await qq.calls()
        XCTAssertNil(metadata.entries["tx"], "Acquired metadata is not a performed query")
        XCTAssertEqual(calls, 0)
        await service.close()
    }
    func testFullRemoteQueueAdoptsUnseenRowsAndReferencesCurrentRepository() async throws {
        let original = try seed(), qq = PlaybackMetadataQQ()
        let service = try await NativeMusicFixture.service(
            MusicNativeCandidateDiscoveryFixture.store(song: original), platform: qq)
        _ = try await service.queue()
        var rows = (0..<30).map { MusicRemoteCollectionActionsFixture.track("row-\($0)") }
        rows.append(MusicNativeCandidateDiscoveryFixture.track(id: "mapped"))
        let source = MusicRemoteCollectionActionsFixture.source(MusicRemoteCollectionActionsFixture.pages(rows))
        let preview = try await service.remoteQueuePage(source, cursor: nil)
        XCTAssertEqual(preview.items.count, 30)
        _ = try await service.replaceRemoteQueue(source,
            start: .init(index: 0), expectedRevision: 1, intent: .init())
        let document = try await service.read()
        let last = try XCTUnwrap(document.queue["entries"].array.last)
        XCTAssertEqual(last["songId"], original["songId"])
        XCTAssertEqual(Set((try document.fixtureSong(last["songId"].string))["sources"].array.map {
            $0["source"].string
        }), ["wy", "tx"])
        let shared = try await service.sharedSongMetadata(original), calls = await qq.calls()
        XCTAssertEqual(Set(shared["sources"].array.map { $0["source"].string }), ["wy", "tx"])
        XCTAssertEqual(calls, 0, "Full acquired rows are imported, never title-searched")
        await source.close(); await service.close()
    }
    func testSavingPreviewedRemoteCollectionDoesNotImportUnseenMetadataOrChangeQueue() async throws {
        let original = try seed(), qq = PlaybackMetadataQQ()
        let service = try await NativeMusicFixture.service(
            MusicNativeCandidateDiscoveryFixture.store(song: original), platform: qq)
        _ = try await service.queue()
        var rows = (0..<30).map { MusicRemoteCollectionActionsFixture.track("row-\($0)") }
        rows.append(MusicNativeCandidateDiscoveryFixture.track(id: "mapped"))
        let pages = MusicRemoteCollectionActionsFixture.pages(rows)
        let source = MusicRemoteCollectionActionsFixture.source(pages)
        let preview = try await service.remoteQueuePage(source, cursor: nil)
        XCTAssertEqual(preview.items.count, 30)
        let before = try await service.read()
        let saved = try await service.saveRemoteCollection(source: source, name: "Acquired", intent: .init())
        XCTAssertEqual(saved.kind, "network")
        XCTAssertEqual(saved.networkReference, .init(platform: .qq, remoteID: "501"))
        XCTAssertFalse(saved.canEdit)
        let collections = try await service.collections()
        XCTAssertEqual(collections.filter { $0.networkReference != nil }, [saved])
        let after = try await service.read()
        XCTAssertEqual(after.queue, before.queue)
        XCTAssertEqual(after.playback, before.playback)
        let shared = try await service.sharedSongMetadata(original)
        // Only song-root field order is unspecified; nested source values stay byte-exact.
        XCTAssertEqual(MJ.obj(shared.fields.sorted { $0.0 < $1.0 }).bytes,
                       MJ.obj(original.fields.sorted { $0.0 < $1.0 }).bytes,
                       "Saving a reference cannot import metadata from unseen members")
        let reads = await pages.events(), calls = await qq.calls()
        XCTAssertEqual(reads, ["root"], "Saving a previewed reference must not read additional members")
        XCTAssertEqual(calls, 0, "Saving a reference does not search for song metadata")
        await source.close(); await service.close()
    }
    @MainActor func testCurrentDiscoveryKeepsFeatureOccurrencesStableAndProjectsCurrentMetadata() async throws {
        let original = try seed(), store = try MusicNativeCandidateDiscoveryFixture.store(song: original)
        var document = try MusicDocument(store.read(), runID: "metadata-consumer")
        document.queue["entries"] = .array(document.queue["entries"].array + [
            .obj([("id", .string("entry-2")), ("songId", original["songId"])])])
        document.queue["serial"] = .n(2)
        store.replace(try document.encode(changed: ["queue"]))
        let service = try await NativeMusicFixture.service(store, scripts: NativeMusicScriptFixture(), platform: PlaybackMetadataQQ())
        let player = MetadataPlaybackPlayer(), model = MusicViewModel(service: service, player: player)
        await model.load()
        let initialIDs = model.queue.map(\.id), revision = model.state.revision
        let rawBefore = try await service.read().queue
        XCTAssertEqual(model.queue[1].track.sources.map(\.platform), ["wy"])
        await model.prepareCurrent()
        XCTAssertNotNil(model.activeResourceID); XCTAssertNil(model.error); XCTAssertEqual(player.resources.count, 1)
        for _ in 0..<1000 where model.queue[1].track.sources.count < 2 { await Task.yield() }
        XCTAssertEqual(Set(model.queue[1].track.sources.map(\.platform)), ["wy", "tx"])
        XCTAssertEqual(model.queue.map(\.id), initialIDs); XCTAssertEqual(model.state.revision, revision)
        let projected = try await service.queue()
        XCTAssertEqual(Set(projected.entries[1].track.sources.map(\.platform)), ["wy", "tx"])
        let raw = try await service.read()
        XCTAssertEqual(raw.queue, rawBefore)
        XCTAssertEqual(raw.queue["entries"].array[1]["songId"], original["songId"])
        await model.close(); await service.close()
    }
    func testInspectionKeepsRequestStartAndCompletionEvidenceIncludingFailure() async throws {
        let failures: [MusicServiceError?] = [nil, .upstreamFailed]
        for failure in failures {
            let original = try seed(), gate = NativeMusicGate(), clock = NativeMusicFixtureClock()
            let qq = PlaybackMetadataQQ(gate: gate, failure: failure)
            let service = NativeMusicService(database: try MusicNativeCandidateDiscoveryFixture.store(song: original),
                scripts: NativeMusicScriptFixture(), platformClient: try qq.makeClient(), enabledPlatforms: [.qq],
                clock: { clock.now() })
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
            let metadata = try await service.sourceDiscoveryState(original)
            let observation = try XCTUnwrap(metadata.entries["tx"]?.observations.first)
            XCTAssertEqual(observation.startedAt, NativeMusicFixture.date.timeIntervalSince1970 * 1000)
            XCTAssertEqual(observation.checkedAt, clock.now().timeIntervalSince1970 * 1000)
            XCTAssertEqual(observation.query, "Synthetic recording Fixture")
            XCTAssertEqual(observation.context, "public"); XCTAssertEqual(observation.page, 1)
            XCTAssertEqual(observation.limit, 30); XCTAssertEqual(observation.scope, .recording)
            XCTAssertEqual(observation.outcome, failure == nil ? .found : .failed)
            clock.advance(60)
            _ = try await service.queue()
            let reread = try await service.sourceDiscoveryState(original)
            XCTAssertEqual(reread.entries["tx"]?.observations.first, observation,
                           "Projection cannot manufacture a newer search check")
            await inspection.close(); await service.close()
        }
    }
}
