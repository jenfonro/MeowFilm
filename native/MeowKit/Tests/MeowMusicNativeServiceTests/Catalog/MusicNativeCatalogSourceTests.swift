import XCTest
import MeowMusicFeature
@testable import MeowMusicNativeService

private typealias F = KugouDiscoveryFixture

private struct CatalogFailingDocument: MusicNativeDocumentStoring {
    func read() throws -> Data? { throw MusicServiceError.storageFailed }
    func compareAndSwap(expected: Data?, replacement: Data) throws { throw MusicServiceError.storageFailed }
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
            return Data(#"{"code":200,"data":{"blocks":[{"positionCode":"songs","dslData":{"items":[{"resourceType":"song","id":"41"}]}}],"hasMore":false}}"#.utf8)
        case .artistActivity:
            return Data(#"{"code":200,"data":{"newWorks":[]}}"#.utf8)
        case .songMetadata(let ids):
            return MJ.obj([("code", .n(200)), ("songs", .array(ids.map(MusicNativePublicBrowseFixture.song)))]).bytes
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
        MJ.obj([("version", .n(1)), ("scope", .string("device")), ("revision", .n(0)),
                ("records", .obj(records.map { (MusicModel.key($0.0), $0.1) }))]).bytes
    }
    func testDefaultIsNeteaseWithoutWritesAndUnavailableSourcesAreRejected() async throws {
        let document = NativeMusicMemoryDocument(), gateway = KugouGateway { _, _ in nil }
        let value = try await kugouService(gateway, document: document, select: false)
        let selection = try await value.homeSourceSelection()
        XCTAssertEqual(selection, .init(selected: .netease, revision: 0, available: [.netease, .kugou]))
        for source in [MusicPublicBrowsePlatform.qq, .kuwo, .migu] {
            await assertMusicFailure(.unsupported) { _ = try await value.selectHomeSource(source, ifRevision: 0) }
        }
        XCTAssertEqual(document.snapshot().2, 0); XCTAssertNil(document.snapshot().0)
        await finishKugou(value, gateway, paths: [])
    }
    func testDisabledSelectedSourceDoesNotFallBackButCanBeChanged() async throws {
        let gateway = KugouGateway { _, _ in nil }
        let value = try await kugouService(gateway, enabled: [.kugou, .qq], select: false)
        let selection = try await value.homeSourceSelection()
        XCTAssertEqual(selection, .init(selected: .netease, revision: 0, available: [.kugou]))
        await assertMusicFailure(.unsupported) { _ = try await value.homePage(cursor: nil, refresh: true) }
        let saved = try await value.selectHomeSource(.kugou, ifRevision: 0)
        XCTAssertEqual(saved, .init(selected: .kugou, revision: 1, available: [.kugou]))
        await finishKugou(value, gateway, paths: [])
    }
    func testSaveIsDurableIdempotentAndUsedByTheFirstHomeReadAfterRecreation() async throws {
        let document = NativeMusicMemoryDocument(), gateway = KugouGateway { call, _ in F.homeResponse(call) }
        let value = try await kugouService(gateway, document: document)
        let root = try MJ.parse(XCTUnwrap(document.snapshot().0))
        XCTAssertEqual(root["records"][MusicModel.key("catalogSource")],
                       .obj([("schema", .n(1)), ("revision", .n(1)), ("platform", .string("kg"))]))
        XCTAssertEqual(document.snapshot().2, 1)
        let saved = try await value.selectHomeSource(.kugou, ifRevision: 1)
        XCTAssertEqual(saved.selected, .kugou); XCTAssertEqual(saved.revision, 1)
        XCTAssertEqual(document.snapshot().2, 1)
        await value.close()
        let restored = try await kugouService(gateway, document: document, select: false)
        let selection = try await restored.homeSourceSelection()
        XCTAssertEqual(selection, saved)
        let page = try await restored.homePage(cursor: nil, refresh: false)
        XCTAssertEqual(page.kugouCollectionIDs.first, "collection_3_123456_1_0")
        XCTAssertEqual(document.snapshot().2, 1)
        await finishKugou(restored, gateway, paths: [F.recommend, F.newSongs, F.rankList])
    }
    func testRevisionConflictsWithinAndBetweenServicesNeverWrite() async throws {
        let document = NativeMusicMemoryDocument(), gateway = KugouGateway { _, _ in nil }
        let first = try await kugouService(gateway, document: document, select: false)
        let second = try await kugouService(gateway, document: document, select: false)
        let old = try await second.homeSourceSelection()
        _ = try await first.selectHomeSource(.kugou, ifRevision: 0)
        let before = document.snapshot()
        await assertMusicFailure(.staleRevision) { _ = try await first.selectHomeSource(.netease, ifRevision: 0) }
        await assertMusicFailure(.staleRevision) { _ = try await second.selectHomeSource(.netease, ifRevision: old.revision) }
        let current = try await second.homeSourceSelection()
        XCTAssertEqual(current.selected, .kugou); XCTAssertEqual(current.revision, 1)
        XCTAssertEqual(document.snapshot().0, before.0); XCTAssertEqual(document.snapshot().2, before.2)
        await second.close()
        await finishKugou(first, gateway, paths: [])
    }
    func testCorruptPreferenceFailsAllCatalogueEntryPointsWithoutHTTPOrRepairWrites() async throws {
        let records = [
            #"{"schema":1,"revision":0,"platform":"tx"}"#,
            #"{"schema":1,"revision":0,"platform":"WY"}"#,
            #"{"schema":1,"revision":0}"#,
            #"{"schema":1,"revision":0,"platform":"kg","extra":true}"#,
            #"{"schema":2,"revision":0,"platform":"kg"}"#,
            #"{"schema":1,"revision":1.5,"platform":"kg"}"#,
            #"{"schema":1,"revision":"1","platform":"kg"}"#,
            #"{"schema":1,"revision":-1,"platform":"kg"}"#,
            #""kg""#, "null"]
        for record in records {
            let document = NativeMusicMemoryDocument(envelope([("catalogSource", F.json(record))]))
            let gateway = KugouGateway { _, _ in nil }
            let value = try await kugouService(gateway, document: document, select: false)
            await assertMusicFailure(.corruptDocument) { _ = try await value.homeSourceSelection() }
            await assertMusicFailure(.corruptDocument) { _ = try await value.homePage(cursor: nil, refresh: true) }
            await assertMusicFailure(.corruptDocument) { _ = try await value.publicDiscovery(.channels, cursor: nil) }
            XCTAssertEqual(document.snapshot().2, 0)
            await finishKugou(value, gateway, paths: [])
        }
    }
    func testStorageReadAndSaveFailuresAreNotDefaultSourceSuccess() async throws {
        let gateway = KugouGateway { _, _ in nil }
        let value = try await kugouService(gateway, document: CatalogFailingDocument(), select: false)
        await assertMusicFailure(.storageFailed) { _ = try await value.homeSourceSelection() }
        await assertMusicFailure(.storageFailed) { _ = try await value.homePage(cursor: nil, refresh: true) }
        await assertMusicFailure(.storageFailed) { _ = try await value.publicDiscovery(.channels, cursor: nil) }
        await finishKugou(value, gateway, paths: [])
        let document = NativeMusicMemoryDocument(), saveGateway = KugouGateway { _, _ in nil }
        let saving = try await kugouService(saveGateway, document: document, select: false)
        document.fail(.storageFailed)
        await assertMusicFailure(.storageFailed) { _ = try await saving.selectHomeSource(.kugou, ifRevision: 0) }
        let selection = try await saving.homeSourceSelection()
        XCTAssertEqual(selection.selected, .netease); XCTAssertEqual(selection.revision, 0)
        XCTAssertEqual(document.snapshot().2, 0)
        await finishKugou(saving, saveGateway, paths: [])
    }
    func testSourceSwitchRetiresInFlightHomeDirectoryAndTagsIncludingLateFailure() async throws {
        for destination in [MusicPublicBrowseDestination.home, .channels,
                            .channel(.kugou("playlist", .playlists), tag: "587")] {
            for fail in [false, true] {
                let snapshot = KugouSnapshotStore(), gate = NativeMusicGate()
                let gateway = KugouGateway { call, _ in fail ? F.failure(7000) : F.homeResponse(call) }
                let path = destination == .home ? F.recommend : F.tags
                await gateway.hold(path, gate: gate)
                let value = try await kugouService(gateway, snapshot: snapshot)
                let request = Task { try await value.publicDiscovery(destination, cursor: nil) }
                await gate.started()
                let selected = try await value.selectHomeSource(.netease, ifRevision: 1)
                XCTAssertEqual(selected.selected, .netease); XCTAssertEqual(selected.revision, 2)
                await gate.release()
                do { _ = try await request.value; XCTFail("Old source published") }
                catch { XCTAssertTrue([MusicServiceError.cancelled, .staleResult].contains(MusicServiceError.sanitized(error))) }
                await gateway.waitUntilIdle()
                XCTAssertNil(snapshot.read())
                let cache = await value.homeCache
                XCTAssertNil(cache)
                await finishKugou(value, gateway, paths: [path])
            }
        }
    }
    func testNeteaseAccountChangesInvalidateNeteaseButNotKugouContentOrSnapshot() async throws {
        for source in [MusicPublicBrowsePlatform.netease, .kugou] {
            let account = CatalogAccount(), snapshot = KugouSnapshotStore()
            let gateway = KugouGateway { call, _ in F.homeResponse(call) }
            let value = try await kugouService(gateway, snapshot: snapshot, accounts: account, select: source == .kugou)
            if source == .kugou { _ = try await value.homePage(cursor: nil, refresh: true) }
            let saved = snapshot.read(), before = await gateway.paths()
            let stream = await value.homeChanges()
            let observer = Task { () -> Int in
                var count = 0
                for await _ in stream { count += 1 }
                return count
            }
            await account.waitForPulls(1)
            await account.changeAccount()
            await account.waitForPulls(2)
            if source == .kugou {
                _ = try await value.homePage(cursor: nil, refresh: false)
                let after = await gateway.paths()
                XCTAssertEqual(after, before)
                XCTAssertEqual(snapshot.read(), saved); XCTAssertNotNil(saved)
            }
            await value.close(); await account.finish()
            let count = await observer.value
            XCTAssertEqual(count, source == .kugou ? 0 : 1)
            await finishKugou(value, gateway, paths: source == .kugou ? [F.recommend, F.newSongs, F.rankList] : [])
        }
    }
    func testLateHomeBindingCompletionCannotEvictNewCacheAfterSourceRoundTrip() async throws {
        let account = CatalogAccount(loggedIn: true), gate = NativeMusicGate(), snapshot = KugouSnapshotStore()
        let gateway = KugouGateway { _, _ in nil }
        let value = try await kugouService(gateway, snapshot: snapshot, accounts: account, select: false)
        // The adapter checks before activity and after enrichment; suspend the
        // service's final binding check, after HTTP work has already completed.
        await account.holdCheck(3, gate: gate)
        let old = Task { try await value.homePage(cursor: nil, refresh: true) }
        await gate.started()
        _ = try await value.selectHomeSource(.kugou, ifRevision: 0)
        _ = try await value.selectHomeSource(.netease, ifRevision: 1)
        let current = try await value.homePage(cursor: nil, refresh: true)
        let saved = try XCTUnwrap(snapshot.read()), calls = await account.payloadCalls
        await gate.release()
        await assertMusicFailure(.staleResult) { _ = try await old.value }
        let cache = await value.homeCache
        XCTAssertNotNil(cache, "A retired request must not clear a newer same-platform cache")
        let cached = try await value.homePage(cursor: nil, refresh: false)
        let after = await account.payloadCalls
        XCTAssertEqual(after, calls, "No re-fetch after a late failure from another source revision")
        XCTAssertEqual(cached.kugouItems.map(\.id), current.kugouItems.map(\.id))
        XCTAssertEqual(snapshot.read(), saved)
        await finishKugou(value, gateway, paths: [])
    }
    func testSavingSourceDoesNotRewriteLibraryQueuePlaybackOrHistory() async throws {
        let song = try NativeMusicFixture.song()
        var queue = MusicModel.queue, history = MusicModel.history, playback = MusicModel.playback
        queue["serial"] = .n(1); queue["currentId"] = .string("entry-1")
        queue["entries"] = .array([.obj([("id", .string("entry-1")), ("key", song["songId"]), ("song", song),
            ("context", .obj([("sourceType", .string("song"))]))])])
        history["days"] = .array([.obj([("date", .string("2026-11-06")),
            ("songs", .array([.obj([("song", song)])]))])])
        playback["mode"] = .n(2); playback["officialSources"] = .strings(["tx"])
        let records: [(String, MJ)] = [("library", MusicModel.library), ("queue", queue), ("playback", playback), ("history", history)]
        let document = NativeMusicMemoryDocument(envelope(records)), gateway = KugouGateway { _, _ in nil }
        let value = try await kugouService(gateway, document: document)
        let saved = try MJ.parse(XCTUnwrap(document.snapshot().0))["records"]
        for (key, expected) in records { XCTAssertEqual(saved[MusicModel.key(key)], expected, key) }
        await finishKugou(value, gateway, paths: [])
    }
    func testForeignCollectionDetailsUseResourcePlatformAndNeverChangePreference() async throws {
        let cases: [(MusicPublicBrowsePlatform, String, String, String)] = [
            (.netease, "42", "interfacepc.music.163.com", "/eapi/v6/playlist/detail"),
            (.kugou, "collection_3_123456_1_0", "gateway.kugou.com", "/pubsongs/v2/get_other_list_file_nofilt")]
        for (platform, id, host, path) in cases {
            let document = NativeMusicMemoryDocument(), http = NativeMusicHTTPFixture([])
            let value = try await kugouService(http, document: document, select: platform == .netease)
            let before = try await value.homeSourceSelection(), writes = document.snapshot().2
            await assertMusicFailure(.upstreamFailed) {
                _ = try await value.publicDetail(.init(platform: platform, nativeID: id, title: "Foreign"), cursor: nil)
            }
            let calls = await http.calls(), after = try await value.homeSourceSelection()
            XCTAssertEqual(calls.count, 1); XCTAssertEqual(calls.first?.url.host, host)
            XCTAssertEqual(calls.first?.url.path, path)
            XCTAssertEqual(after, before); XCTAssertEqual(document.snapshot().2, writes)
            await value.close()
        }
    }
    func testChannelAndHomeContinuationsBelongToTheSelectedSourceRevision() async throws {
        let gateway = KugouGateway { call, _ in F.homeResponse(call) }
        let value = try await kugouService(gateway)
        for kind in [MusicPublicBrowseChannel.Kind.playlists, .featured] {
            await assertMusicFailure(.staleResult) {
                _ = try await value.publicDiscovery(.channel(.init(id: "foreign", title: "", kind: kind,
                    platform: .netease), tag: ""), cursor: nil)
            }
        }
        await assertMusicFailure(.unsupported) {
            _ = try await value.publicDiscovery(.channel(.init(id: "chart", title: "", kind: .rankings,
                platform: .qq), tag: ""), cursor: nil)
        }
        let noRequests = await gateway.paths()
        XCTAssertTrue(noRequests.isEmpty)
        let home = try await value.homePage(cursor: nil, refresh: true)
        let destination = MusicPublicBrowseDestination.channel(.kugou("playlist", .playlists), tag: "587")
        let channel = try await value.publicDiscovery(destination, cursor: nil)
        let homeCursor = try XCTUnwrap(home.cursor), channelCursor = try XCTUnwrap(channel.cursor)
        _ = try await value.selectHomeSource(.netease, ifRevision: 1)
        _ = try await value.selectHomeSource(.kugou, ifRevision: 2)
        await assertMusicFailure(.staleResult) { _ = try await value.homePage(cursor: homeCursor, refresh: false) }
        await assertMusicFailure(.staleResult) { _ = try await value.publicDiscovery(destination, cursor: channelCursor) }
        await finishKugou(value, gateway, paths: [F.recommend, F.newSongs, F.rankList, F.tags, F.recommend])
    }
    func testKugouSnapshotRoundTripPreservesNativeLocatorsAndQualitiesWithoutAuthority() async throws {
        let snapshot = KugouSnapshotStore(), document = NativeMusicMemoryDocument()
        let gateway = KugouGateway { call, _ in
            if call.path == F.newSongs {
                var response = F.newSongPage(1, total: 1), rows = response["data"].array
                rows[0]["filesize"] = .n(3473110); response["data"] = .array(rows); return response
            }
            return F.homeResponse(call)
        }
        let value = try await kugouService(gateway, document: document, snapshot: snapshot)
        let live = try await value.homePage(cursor: nil, refresh: true)
        let bytes = try XCTUnwrap(snapshot.read())
        await value.close()
        let restoredService = try await kugouService(gateway, document: document, snapshot: snapshot, select: false)
        let restoredValue = try await restoredService.restoredHome(), restored = try XCTUnwrap(restoredValue)
        XCTAssertEqual(restored.sections.map(\.id), live.sections.map(\.id))
        XCTAssertEqual(restored.kugouItems.map(\.id), live.kugouItems.map(\.id)); XCTAssertNil(restored.cursor)
        guard case .track(let old)? = live.kugouSection("new")?.items.first?.content,
              case .track(let new)? = restored.kugouSection("new")?.items.first?.content else { return XCTFail("Expected tracks") }
        XCTAssertEqual(new.songId, old.songId); XCTAssertEqual(new.sources, old.sources)
        XCTAssertEqual(new.sources.first?.platform, "kg"); XCTAssertEqual(new.sources.first?.nativeId, F.hash(1))
        XCTAssertNotEqual(new.ref, old.ref)
        let decoded = try XCTUnwrap(MusicNativeHomeSnapshot.decode(bytes, source: .kugou, scope: "kugou:guest"))
        guard case .song(let raw)? = decoded.sections.first(where: { $0.id == "new" })?.items.first?.content else {
            return XCTFail("Expected native song")
        }
        XCTAssertEqual(try MJ.parse(raw.data)["types"].array.map { $0["type"].string }, ["128k"])
        XCTAssertNil(try MusicNativeHomeSnapshot.decode(bytes, source: .netease, scope: "kugou:guest"))
        var root = try MJ.parse(bytes), sections = root["sections"].array
        sections[0]["channel"]["platform"] = .string("wy"); root["sections"] = .array(sections)
        XCTAssertThrowsError(try MusicNativeHomeSnapshot.decode(root.bytes, source: .kugou, scope: "kugou:guest"))
        await finishKugou(restoredService, gateway, paths: [F.recommend, F.newSongs, F.rankList])
    }
    func testAdoptingNewDurableRevisionRetiresCursorEvenWhenPlatformIsUnchanged() async throws {
        let document = NativeMusicMemoryDocument(), gateway = KugouGateway { call, _ in F.homeResponse(call) }
        let reader = try await kugouService(gateway, document: document)
        let page = try await reader.homePage(cursor: nil, refresh: true), cursor = try XCTUnwrap(page.cursor)
        let writer = try await kugouService(gateway, document: document, select: false)
        _ = try await writer.selectHomeSource(.netease, ifRevision: 1)
        _ = try await writer.selectHomeSource(.kugou, ifRevision: 2)
        let adopted = try await reader.homeSourceSelection()
        XCTAssertEqual(adopted.selected, .kugou); XCTAssertEqual(adopted.revision, 3)
        await assertMusicFailure(.staleResult) { _ = try await reader.homePage(cursor: cursor, refresh: false) }
        await writer.close()
        await finishKugou(reader, gateway, paths: [F.recommend, F.newSongs, F.rankList])
    }
    func testDisplayedKugouQueueRecoversOnlyOwnedRowsWithoutNetworkOrNeteaseFallback() async throws {
        let gateway = KugouGateway { call, _ in F.homeResponse(call) }
        let value = try await kugouService(gateway)
        let page = try await value.homePage(cursor: nil, refresh: true)
        guard case .track(let track)? = page.kugouSection("new")?.items.first?.content else { return XCTFail("Expected track") }
        let stale = MusicRemoteTrack(ref: .init(kind: .handle, id: "expired-display-handle"),
            songId: track.songId, title: track.title, artist: track.artist, album: track.album,
            duration: track.duration, sources: track.sources, artwork: track.artwork)
        let playback = try await value.replaceDisplayedQueue([stale], index: 0,
            origin: .section(title: "酷狗新歌"), expectedRevision: 0, intent: .init())
        let queue = try await value.queue()
        XCTAssertEqual(queue.entries.first?.track.sources, track.sources)
        XCTAssertEqual(queue.entries.first?.track.songId, track.songId)
        let unknown = MusicRemoteTrack(ref: .init(kind: .handle, id: "another-expired-handle"),
            songId: "song:unknown", title: "Not in snapshot",
            sources: [.init(sourceKey: "kg:unknown", platform: "kg", nativeId: F.hash(99))])
        await assertMusicFailure(.targetChanged) {
            _ = try await value.replaceDisplayedQueue([unknown], index: 0, origin: .song,
                expectedRevision: playback.revision, intent: .init())
        }
        await finishKugou(value, gateway, paths: [F.recommend, F.newSongs, F.rankList])
    }
    func testDisplayedKugouRowsWithSameRecordingKeepDistinctNativeIdentitiesAndOccurrences() async throws {
        let gateway = KugouGateway { call, _ in
            if call.path == F.newSongs {
                var response = F.newSongPage(2, total: 2), rows = response["data"].array
                rows[1]["filename"] = rows[0]["filename"]; response["data"] = .array(rows); return response
            }
            return F.homeResponse(call)
        }
        let value = try await kugouService(gateway)
        let page = try await value.homePage(cursor: nil, refresh: true)
        let tracks = page.kugouItems.compactMap { item -> MusicRemoteTrack? in
            if case .track(let track) = item.content { return track }; return nil
        }
        XCTAssertEqual(tracks.count, 2)
        XCTAssertEqual(tracks[0].songId, tracks[1].songId)
        XCTAssertNotEqual(tracks[0].sources, tracks[1].sources)
        let first = try await value.replaceDisplayedQueue(tracks, index: 1, origin: .section(title: "同录音"),
            expectedRevision: 0, intent: .init())
        let fresh = try await value.queue()
        XCTAssertEqual(fresh.entries.map { $0.track.sources[0].nativeId }, [F.hash(1), F.hash(2)])
        let stale = tracks.map { track in
            MusicRemoteTrack(ref: .init(kind: .handle, id: "expired"), songId: track.songId,
                title: track.title, artist: track.artist, album: track.album, duration: track.duration, sources: track.sources)
        }
        _ = try await value.replaceDisplayedQueue(stale + [stale[0]], index: 1, origin: .section(title: "同录音"),
            expectedRevision: first.revision, intent: .init())
        let restored = try await value.queue()
        XCTAssertEqual(restored.entries.map { $0.track.sources[0].nativeId }, [F.hash(1), F.hash(2), F.hash(1)])
        XCTAssertEqual(Set(restored.entries.map(\.id)).count, 3)
        XCTAssertEqual(restored.currentId, restored.entries[1].id)
        await finishKugou(value, gateway, paths: [F.recommend, F.newSongs, F.rankList])
    }
}
