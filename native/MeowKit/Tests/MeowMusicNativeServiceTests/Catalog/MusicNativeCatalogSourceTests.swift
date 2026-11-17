import XCTest
import MeowMusicFeature
import MeowMusicTestSupport
@testable import MeowMusicNativeService

private typealias F = KugouDiscoveryFixture

private struct CatalogFailingDocument: NativeMusicTestLibraryWrapping {
    let backingLibrary = NativeMusicTestLibrary()
    func readSnapshot() throws -> MusicNativeLibrarySnapshot { throw MusicServiceError.storageFailed }
    func withTransaction<T>(commit: (_ action: () throws -> Void) throws -> Void, _ body: (any MusicNativeLibraryWriting) throws -> T) throws -> T { throw MusicServiceError.storageFailed }
}

private actor CatalogAccount: MusicNativeAccountProviding, MusicNativeHomeDisplayProviding {
    private var binding: MusicNativeHomeBinding
    private var pulls = 0, pending = false, finished = false
    private var waiter: CheckedContinuation<Bool, Never>?
    private var pullWaiters: [(Int, CheckedContinuation<Void, Never>)] = []
    private var checkCount = 0
    private var heldCheck: (Int, NativeMusicGate)?
    private(set) var payloadCalls = 0
    init(loggedIn: Bool = false) {
        binding = .init(scope: "synthetic-guest-a", revision: UUID(), loggedIn: loggedIn)
    }
    func holdCheck(_ number: Int, gate: NativeMusicGate) { heldCheck = (number, gate) }
    func nativeHomeBinding() -> MusicNativeHomeBinding { binding }
    func nativeHomeDisplayScope() -> MusicNativeCacheScope {
        .init(scope: binding.scope, revision: binding.revision, authorization: binding.authorization)
    }
    func checkNativeHomeDisplayScope(_ value: MusicNativeCacheScope) throws {
        try value.authorization.check()
        guard value == nativeHomeDisplayScope() else { throw MusicServiceError.staleResult }
    }
    func checkNativeHomeBinding(_ value: MusicNativeHomeBinding) async throws {
        checkCount += 1
        if let heldCheck, checkCount == heldCheck.0 { await heldCheck.1.wait() }
        try value.authorization.check()
        guard value == binding else { throw MusicServiceError.staleResult }
    }
    func nativeHomeChanges() -> AsyncStream<Void> {
        AsyncStream(unfolding: { await self.nextChange() ? () : nil })
    }
    private func nextChange() async -> Bool {
        pulls += 1
        let ready = pullWaiters.filter { $0.0 <= pulls }
        pullWaiters.removeAll { $0.0 <= pulls }; ready.forEach { $0.1.resume() }
        if finished { return false }
        if pending { pending = false; return true }
        return await withCheckedContinuation { waiter = $0 }
    }
    func waitForPulls(_ count: Int) async {
        if pulls < count { await withCheckedContinuation { pullWaiters.append((count, $0)) } }
    }
    func changeAccount() {
        binding.authorization.revoke()
        binding = .init(scope: "synthetic-guest-b", revision: UUID(), loggedIn: false)
        if let waiter { self.waiter = nil; waiter.resume(returning: true) }
        else { pending = true }
    }
    func finish() { finished = true; waiter?.resume(returning: false); waiter = nil }
    func nativeHomePayload(_ request: MusicNativeHomeRequest, binding: MusicNativeHomeBinding) throws -> Data {
        payloadCalls += 1
        guard binding.loggedIn else { throw MusicServiceError.authRequired }
        switch request {
        case .home, .featured:
            let section = MJ.obj([("id", .string("songs")), ("type", .string("songs")),
                ("items", .array([MusicNativePublicBrowseFixture.song("41")]))])
            return MJ.obj([("sections", .array([section]))]).bytes
        case .artistActivity:
            return Data(#"{"sections":[]}"#.utf8)
        case .songMetadata(let ids):
            return MJ.obj([("items", .array(ids.map(MusicNativePublicBrowseFixture.song)))]).bytes
        default: throw MusicServiceError.unsupported
        }
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
    func nativeOptions(platform: MusicAccountPlatform, nativeID: String, sourceKey: String) throws -> [MusicPlaybackOption] {
        throw MusicServiceError.unsupported
    }
    func nativeResolve(platform: MusicAccountPlatform, nativeID: String, selection: MusicPlaybackSelection) throws -> MusicNativeAudio {
        throw MusicServiceError.unsupported
    }
}
final class MusicNativeCatalogSourceTests: XCTestCase {
    private func envelope(_ records: [(String, MJ)]) -> Data {
        MJ.obj([("revision", .n(0)), ("areas", .obj(records)), ("songs", .obj())]).bytes
    }
    func testDefaultIsNeteaseWithoutWritesAndUnavailableSourcesAreRejected() async throws {
        let peer = KugouDiscoveryPeer(), document = NativeMusicTestLibrary(), service = try await kugouService(peer, document: document, select: false)
        let selected = try await service.homeSourceSelection(); XCTAssertEqual(selected.selected, .netease); XCTAssertEqual(selected.revision, 0)
        XCTAssertEqual(document.snapshot().2, 0)
        for source in [MusicPublicBrowsePlatform.qq, .kuwo, .migu] { await assertMusicFailure(.unsupported) { _ = try await service.selectHomeSource(source, ifRevision: 0) } }
        await finishKugou(service, peer, operations: [])
    }
    func testDisabledSelectedSourceDoesNotFallBackButCanBeChanged() async throws {
        let peer = KugouDiscoveryPeer(), document = NativeMusicTestLibrary()
        let first = try await kugouService(peer, document: document); await first.close()
        let service = try await kugouService(peer, document: document, enabled: [.netease], select: false)
        await assertMusicFailure(.unsupported) { _ = try await service.homePage(cursor: nil, refresh: true) }
        let selected = try await service.selectHomeSource(.netease, ifRevision: 1); XCTAssertEqual(selected.selected, .netease)
        await finishKugou(service, peer, operations: [])
    }
    func testSaveIsDurableIdempotentAndUsedByFirstHomeAfterRecreation() async throws {
        let peer = KugouDiscoveryPeer(), document = NativeMusicTestLibrary(), service = try await kugouService(peer, document: document)
        _ = try await service.selectHomeSource(.kugou, ifRevision: 1); XCTAssertEqual(document.snapshot().2, 1); await service.close()
        let restored = try await kugouService(peer, document: document, select: false), selected = try await restored.homeSourceSelection()
        XCTAssertEqual(selected.selected, .kugou); XCTAssertEqual(selected.revision, 1)
        let page = try await restored.homePage(cursor: nil, refresh: true); XCTAssertEqual(page.kugouCollectionIDs.first, "collection_3_123456_1_0")
        await finishKugou(restored, peer, operations: ["home"])
    }
    func testRevisionConflictsWithinAndBetweenServicesNeverWrite() async throws {
        let peer = KugouDiscoveryPeer(), document = NativeMusicTestLibrary(), first = try await kugouService(peer, document: document, select: false)
        let second = try await kugouService(peer, document: document, select: false); _ = try await second.homeSourceSelection()
        _ = try await first.selectHomeSource(.kugou, ifRevision: 0); let before = document.snapshot()
        await assertMusicFailure(.staleRevision) { _ = try await first.selectHomeSource(.netease, ifRevision: 0) }
        await assertMusicFailure(.staleRevision) { _ = try await second.selectHomeSource(.netease, ifRevision: 0) }
        let current = try await second.homeSourceSelection(); XCTAssertEqual(current.selected, .kugou); XCTAssertEqual(current.revision, 1)
        XCTAssertEqual(document.snapshot().0, before.0); XCTAssertEqual(document.snapshot().2, before.2)
        await second.close(); await finishKugou(first, peer, operations: [])
    }
    func testCorruptPreferenceFailsCatalogueWithoutRepairOrRequests() async throws {
        for raw in [#"{"schema":1,"revision":0,"platform":"tx"}"#, #"{"schema":1,"revision":0,"platform":"WY"}"#,
            #"{"schema":1,"revision":0}"#, #"{"schema":1,"revision":0,"platform":"kg","extra":true}"#,
            #"{"schema":2,"revision":0,"platform":"kg"}"#, #"{"schema":1,"revision":1.5,"platform":"kg"}"#,
            #"{"schema":1,"revision":"1","platform":"kg"}"#, #"{"schema":1,"revision":-1,"platform":"kg"}"#, #""kg""#, "null"] {
            let peer = KugouDiscoveryPeer(), document = NativeMusicTestLibrary(envelope([("catalogSource", F.json(raw))])), service = try await kugouService(peer, document: document, select: false)
            await assertMusicFailure(.corruptDocument) { _ = try await service.homeSourceSelection() }
            await assertMusicFailure(.corruptDocument) { _ = try await service.homePage(cursor: nil, refresh: true) }
            await assertMusicFailure(.corruptDocument) { _ = try await service.publicDiscovery(.channels, cursor: nil) }
            XCTAssertEqual(document.snapshot().2, 0); await finishKugou(service, peer, operations: [])
        }
    }
    func testStorageReadAndSaveFailuresAreNotDefaultSourceSuccess() async throws {
        let peer = KugouDiscoveryPeer(), service = try await kugouService(peer, document: CatalogFailingDocument(), select: false)
        await assertMusicFailure(.storageFailed) { _ = try await service.homeSourceSelection() }
        await assertMusicFailure(.storageFailed) { _ = try await service.homePage(cursor: nil, refresh: true) }
        await finishKugou(service, peer, operations: [])
        let document = NativeMusicTestLibrary(), saving = try await kugouService(peer, document: document, select: false)
        document.fail(.storageFailed)
        await assertMusicFailure(.storageFailed) { _ = try await saving.selectHomeSource(.kugou, ifRevision: 0) }
        let selected = try await saving.homeSourceSelection(); XCTAssertEqual(selected.selected, .netease); XCTAssertEqual(selected.revision, 0)
        XCTAssertEqual(document.snapshot().2, 0); await finishKugou(saving, peer, operations: [])
    }
    func testSourceSwitchRetiresInFlightHomeDirectoryAndTagsIncludingLateFailure() async throws {
        for target in [MusicPublicBrowseDestination.home, .channels, .channel(.kugou("playlist", .playlists), tag: "587")] {
            for failure in [false, true] {
                let snapshot = KugouSnapshotStore(), gate = NativeMusicGate(), operation = target == .home ? "home" : target == .channels ? "channels" : "channel"
                let peer = KugouDiscoveryPeer { request, _ in if failure { throw MusicServiceError.upstreamFailed }; return try F.reply(request) }
                await peer.hold(operation, gate: gate)
                let service = try await kugouService(peer, snapshot: snapshot), task = Task { try await service.publicDiscovery(target, cursor: nil) }
                await gate.started(); _ = try await service.selectHomeSource(.netease, ifRevision: 1); await gate.release()
                do { _ = try await task.value; XCTFail("retired source published") }
                catch { XCTAssertTrue([MusicServiceError.cancelled, .staleResult].contains(MusicServiceError.sanitized(error))) }
                await peer.waitUntilIdle(); XCTAssertNil(snapshot.read()); let cache = await service.homeCache; XCTAssertNil(cache)
                await finishKugou(service, peer, operations: [operation])
            }
        }
    }
    func testNeteaseAccountChangesInvalidateNeteaseButNotKugouCache() async throws {
        for source in [MusicPublicBrowsePlatform.netease, .kugou] {
            let account = CatalogAccount(), snapshot = KugouSnapshotStore(), peer = KugouDiscoveryPeer(), service = try await kugouService(peer, snapshot: snapshot, accounts: account, select: source == .kugou)
            if source == .kugou { _ = try await service.homePage(cursor: nil, refresh: true) }
            let saved = snapshot.read(), before = await peer.calls().count, stream = await service.homeChanges()
            let observer = Task { () -> Int in var count = 0; for await _ in stream { count += 1 }; return count }
            await account.waitForPulls(1); await account.changeAccount(); await account.waitForPulls(2)
            if source == .kugou {
                _ = try await service.homePage(cursor: nil, refresh: false)
                let after = await peer.calls().count; XCTAssertEqual(after, before); XCTAssertEqual(snapshot.read(), saved); XCTAssertNotNil(saved)
            }
            await service.close(); await account.finish(); let count = await observer.value; XCTAssertEqual(count, source == .kugou ? 0 : 1)
            await finishKugou(service, peer)
        }
    }
    func testLateHomeBindingCompletionCannotEvictNewCacheAfterSourceRoundTrip() async throws {
        let account = CatalogAccount(loggedIn: true), gate = NativeMusicGate(), snapshot = KugouSnapshotStore(), peer = KugouDiscoveryPeer()
        let service = try await kugouService(peer, snapshot: snapshot, accounts: account, select: false)
        await account.holdCheck(1, gate: gate)
        let old = Task { try await service.homePage(cursor: nil, refresh: true) }; await gate.started()
        _ = try await service.selectHomeSource(.kugou, ifRevision: 0); _ = try await service.selectHomeSource(.netease, ifRevision: 1)
        let current = try await service.homePage(cursor: nil, refresh: true), saved = snapshot.read(), calls = await account.payloadCalls
        await gate.release(); await assertMusicFailure(.staleResult) { _ = try await old.value }
        let cached = try await service.homePage(cursor: nil, refresh: false), after = await account.payloadCalls
        XCTAssertEqual(after, calls); XCTAssertEqual(cached.sections.map(\.id), current.sections.map(\.id)); XCTAssertEqual(snapshot.read(), saved)
        await finishKugou(service, peer, operations: [])
    }
    func testSavingSourceDoesNotRewriteLibraryQueuePlaybackOrHistory() async throws {
        let peer = KugouDiscoveryPeer(), document = NativeMusicTestLibrary(), service = try await NativeMusicFixture.service(document)
        let track = try await service.search(platform: "tx", query: "kept", page: 1, limit: 30).items[0]
        _ = try await service.changeQueue(.replace([track.ref], index: 0), expectedRevision: 0)
        let before = try await service.read(); _ = try await service.selectHomeSource(.kugou, ifRevision: 0); let after = try await service.read()
        XCTAssertEqual(after.library, before.library); XCTAssertEqual(after.queue, before.queue); XCTAssertEqual(after.playback, before.playback); XCTAssertEqual(after.history, before.history)
        await finishKugou(service, peer, operations: [])
    }
    func testForeignCollectionUsesItsPlatformAndNeverChangesHomePreference() async throws {
        for platform in [MusicPublicBrowsePlatform.netease, .kugou] {
            let peer = NativeMusicResultFixture(failure: .upstreamFailed), document = NativeMusicTestLibrary(), service = try await kugouService(peer, document: document, select: platform == .netease)
            let before = try await service.homeSourceSelection(), writes = document.snapshot().2
            await assertMusicFailure(.upstreamFailed) { _ = try await service.publicDetail(.init(platform: platform, nativeID: "foreign", title: "Foreign"), cursor: nil) }
            let calls = await peer.calls(), after = try await service.homeSourceSelection()
            XCTAssertEqual(calls.map(\.platform), [platform.rawValue]); XCTAssertEqual(calls.map(\.operation), ["playlist"]); XCTAssertEqual(after, before); XCTAssertEqual(document.snapshot().2, writes)
            await service.close()
        }
    }
    func testChannelAndHomeContinuationsBelongToSelectedSourceRevision() async throws {
        let peer = KugouDiscoveryPeer { request, _ in
            if request.platform == "wy" {
                return NativeDiscoveryFixture.page([NativeDiscoveryFixture.section("foreign",
                    items: [NativeDiscoveryFixture.collection("wy-foreign")])])
            }
            return try F.reply(request)
        }
        let service = try await kugouService(peer)
        let before = try await service.homeSourceSelection()
        let foreign = try await service.publicDiscovery(.channel(.init(id: "foreign", title: "", kind: .playlists, platform: .netease), tag: ""), cursor: nil)
        guard case .collection(let collection)? = foreign.sections.first?.items.first?.content else {
            await service.close(); return XCTFail("collection required")
        }
        XCTAssertEqual(collection.platform, .netease); XCTAssertEqual(collection.nativeID, "wy-foreign")
        let calls = await peer.calls(), first = try XCTUnwrap(calls.first), after = try await service.homeSourceSelection()
        XCTAssertEqual(calls.map(\.operation), ["channel"]); XCTAssertEqual(first.platform, "wy")
        XCTAssertEqual(first.parameters[music: "id"].musicText, "foreign")
        XCTAssertEqual(after, before)
        let home = try await service.homePage(cursor: nil, refresh: true), target = MusicPublicBrowseDestination.channel(.kugou("playlist", .playlists), tag: "587")
        let channel = try await service.publicDiscovery(target, cursor: nil)
        let homeCursor = try XCTUnwrap(home.cursor), channelCursor = try XCTUnwrap(channel.cursor)
        _ = try await service.selectHomeSource(.netease, ifRevision: 1); _ = try await service.selectHomeSource(.kugou, ifRevision: 2)
        await assertMusicFailure(.staleResult) { _ = try await service.homePage(cursor: homeCursor, refresh: false) }
        await assertMusicFailure(.staleResult) { _ = try await service.publicDiscovery(target, cursor: channelCursor) }
        await finishKugou(service, peer, operations: ["channel", "home", "channel"])
    }
    func testScopedChannelUsesItsDeclaredPlatformWithoutChangingHomePreference() async throws {
        let peer = KugouDiscoveryPeer { _, _ in
            NativeDiscoveryFixture.page([NativeDiscoveryFixture.section("featured",
                items: [NativeDiscoveryFixture.collection("wy-featured")])])
        }
        let document = NativeMusicTestLibrary(), service = try await kugouService(peer, document: document)
        let before = try await service.homeSourceSelection(), writes = document.snapshot().2
        let channel = MusicPublicBrowseChannel(id: "featured", title: "Featured", kind: .featured, platform: .netease)
        let page = try await service.publicDiscovery(.channel(channel, tag: ""), cursor: nil)
        guard case .collection(let collection)? = page.sections.first?.items.first?.content else {
            await service.close(); return XCTFail("collection required")
        }
        XCTAssertEqual(collection.platform, .netease); XCTAssertEqual(collection.nativeID, "wy-featured")
        let calls = await peer.calls(), first = try XCTUnwrap(calls.first), after = try await service.homeSourceSelection()
        XCTAssertEqual(calls.map(\.platform), ["wy"]); XCTAssertEqual(calls.map(\.operation), ["channel"])
        XCTAssertEqual(first.parameters[music: "id"].musicText, "featured")
        XCTAssertEqual(after, before); XCTAssertEqual(document.snapshot().2, writes)
        await finishKugou(service, peer, operations: ["channel"])
    }
    func testCurrentKugouSnapshotColdReadPreservesRepositoryLocatorsWithoutAuthority() async throws {
        let peer = KugouDiscoveryPeer(), document = NativeMusicTestLibrary(), snapshot = KugouSnapshotStore(), service = try await kugouService(peer, document: document, snapshot: snapshot)
        let live = try await service.homePage(cursor: nil, refresh: true), bytes = try XCTUnwrap(snapshot.read())
        let storedBytes = document.snapshot().0, writes = document.snapshot().2
        await service.close()
        let reopened = try await kugouService(peer, document: document, snapshot: snapshot, select: false)
        let before = try await reopened.read(), restoredValue = try await reopened.restoredHome(), restored = try XCTUnwrap(restoredValue)
        guard case .track(let old)? = live.kugouSection("new")?.items.first?.content, case .track(let new)? = restored.kugouSection("new")?.items.first?.content else { return XCTFail("track required") }
        XCTAssertEqual(old.songId, new.songId); XCTAssertEqual(old.ref, new.ref); XCTAssertEqual(new.sources[0].nativeId, F.hash(1)); XCTAssertNil(restored.cursor)
        let decoded = try XCTUnwrap(MusicNativeHomeSnapshot.decode(bytes, source: .kugou))
        guard case .record(let id)? = decoded.sections.first(where: { $0.id == "new" })?.items.first?.content else { return XCTFail("record required") }
        XCTAssertEqual(id, old.songId); XCTAssertFalse(String(decoding: bytes, as: UTF8.self).contains("musicInfo")); XCTAssertNil(try MusicNativeHomeSnapshot.decode(bytes, source: .netease))
        let secondValue = try await reopened.restoredHome(), second = try XCTUnwrap(secondValue)
        guard case .track(let again)? = second.kugouSection("new")?.items.first?.content else { return XCTFail("track required") }
        XCTAssertEqual(again, new); XCTAssertNil(second.cursor)
        let current = try await reopened.read()
        XCTAssertEqual(current.library, before.library); XCTAssertEqual(current.queue, before.queue)
        XCTAssertEqual(current.playback, before.playback); XCTAssertEqual(current.history, before.history)
        XCTAssertEqual(document.snapshot().0, storedBytes); XCTAssertEqual(document.snapshot().2, writes)
        XCTAssertEqual(snapshot.read(), bytes)
        await finishKugou(reopened, peer, operations: ["home"])
    }
    func testAdoptingNewDurableRevisionRetiresCursorEvenWhenPlatformIsUnchanged() async throws {
        let peer = KugouDiscoveryPeer(), document = NativeMusicTestLibrary(), reader = try await kugouService(peer, document: document)
        let page = try await reader.homePage(cursor: nil, refresh: true), cursor = try XCTUnwrap(page.cursor), writer = try await kugouService(peer, document: document, select: false)
        _ = try await writer.selectHomeSource(.netease, ifRevision: 1); _ = try await writer.selectHomeSource(.kugou, ifRevision: 2)
        let current = try await reader.homeSourceSelection(); XCTAssertEqual(current.revision, 3)
        await assertMusicFailure(.staleResult) { _ = try await reader.homePage(cursor: cursor, refresh: false) }; await writer.close(); await finishKugou(reader, peer, operations: ["home"])
    }
    func testDisplayedKugouQueueReadsOwnedRecordsWithoutNetworkAndRejectsUnknownID() async throws {
        let peer = KugouDiscoveryPeer(), service = try await kugouService(peer), page = try await service.homePage(cursor: nil, refresh: true)
        guard case .track(let track)? = page.kugouSection("new")?.items.first?.content else { return XCTFail("song required") }
        let playback = try await service.replaceDisplayedQueue([track], index: 0, origin: .section(title: "酷狗新歌"), expectedRevision: 0, intent: .init())
        let queue = try await service.queue(); XCTAssertEqual(queue.entries[0].track.songId, track.songId)
        let id = UUID().uuidString.lowercased(), unknown = MusicRemoteTrack(ref: .init(kind: .library, id: id), songId: id, title: "Unknown", sources: [])
        await assertMusicFailure(.notFound) { _ = try await service.replaceDisplayedQueue([unknown], index: 0, origin: .song, expectedRevision: playback.revision, intent: .init()) }
        await finishKugou(service, peer, operations: ["home"])
    }
    func testSameRecordingKeepsBothSourceOwnershipsAndDistinctQueueOccurrences() async throws {
        let peer = KugouDiscoveryPeer { _, _ in try F.songs(2, title: "Same recording") }, service = try await kugouService(peer)
        let page = try await service.homePage(cursor: nil, refresh: true), tracks = page.kugouItems.compactMap { if case .track(let value) = $0.content { return value }; return nil }
        XCTAssertEqual(tracks.count, 2); XCTAssertEqual(tracks[0].songId, tracks[1].songId)
        let result = try await service.replaceDisplayedQueue(tracks + [tracks[0]], index: 1, origin: .section(title: "同录音"), expectedRevision: 0, intent: .init()), queue = try await service.queue()
        XCTAssertEqual(result.total, 3); XCTAssertEqual(Set(queue.entries.map(\.id)).count, 3); XCTAssertEqual(queue.currentId, queue.entries[1].id)
        XCTAssertTrue(queue.entries.allSatisfy { $0.track.songId == tracks[0].songId && $0.track.sources.count == 2 })
        await finishKugou(service, peer, operations: ["home"])
    }
}
