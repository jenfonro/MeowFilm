import XCTest
import MeowMusicFeature
import MeowMusicTestSupport
import MeowRuntime
import MeowStorage
@testable import MeowMusicNativeService

private extension MusicPublicBrowseSection {
    var tracks: [MusicRemoteTrack] {
        items.compactMap { if case .track(let track) = $0.content { return track }; return nil }
    }
}
private final class HomeSnapshotFixture: MusicHomeSnapshotStoring, @unchecked Sendable {
    private let lock = NSLock()
    private var data: Data?
    private var reads = 0
    func read() -> Data? { lock.lock(); defer { lock.unlock() }; reads += 1; return data }
    func readCount() -> Int { lock.lock(); defer { lock.unlock() }; return reads }
    func write(_ value: Data) { lock.lock(); data = value; lock.unlock() }
    func clear() { lock.lock(); data = nil; lock.unlock() }
}
private func canonicalHome(_ ids: [String] = ["41"], next: Bool = false, artists: Bool = true) -> Data {
    let items = ids.enumerated().map { index, id -> MJ in
        let track = MusicNativePublicBrowseFixture.track(id, title: "Synthetic song " + id)
        var item = try! MJ.parse(RuntimeJSON.encode(MusicPlatformFixture.song(track)))
        item["key"] = .string("songs:\(id):\(index)")
        return item
    }
    var sections: [MJ] = [.obj([("id", .string("songs")), ("title", .string("完整栏目")),
        ("type", .string("songs")), ("items", .array(items))])]
    if artists { sections.append(canonicalArtistSection()) }
    var page = MJ.obj([("sections", .array(sections))])
    if next { page["continuation"] = .obj([("cursor", .string("4"))]) }
    return page.bytes
}
private func canonicalArtistSection() -> MJ {
    .obj([("id", .string("artists")), ("title", .string("艺人新作")), ("layout", .string("artist-updates")),
        ("avatars", .array([.string("https://p1.music.126.net/avatar.jpg")])),
        ("action", .obj([("type", .string("channel")), ("channel", .obj([
            ("id", .string("artist-activity")), ("channelKind", .string("artist-activity")), ("name", .string("艺人新作"))]))])),
        ("items", .array([.obj([("key", .string("wy:album:42")), ("id", .string("42")),
            ("name", .string("新专辑")), ("creator", .string("艺人")), ("count", .n(8)),
            ("cover", .string("https://p1.music.126.net/album.jpg")), ("coverLabel", .string("新专辑")),
            ("action", .obj([("type", .string("album"))]))])]))])
}
private actor HomeAccountFixture: MusicNativeAccountProviding, MusicNativeHomeDisplayProviding {
    var binding: MusicNativeHomeBinding
    var calls: [MusicNativeHomeRequest] = []
    var gate: NativeMusicGate?
    var bindingFailure: MusicServiceError?
    var bindingCalls = 0
    var homePayload: Data?
    var activityPayload = MJ.obj([("sections", .array([canonicalArtistSection()]))]).bytes
    var failure: MusicServiceError?
    var dailyPage: MusicNativeTrackPage?
    init(loggedIn: Bool = true) { binding = .init(scope: loggedIn ? "synthetic-account-a" : "guest", revision: UUID(), loggedIn: loggedIn) }
    func suspend(_ gate: NativeMusicGate) { self.gate = gate }
    func setHome(_ value: Data) { homePayload = value }
    func setActivity(_ value: Data) { activityPayload = value }
    func setDaily(_ value: MusicNativeTrackPage) { dailyPage = value }
    func fail(_ error: MusicServiceError?) { failure = error }
    func changeAccount() {
        binding.authorization.revoke()
        binding = .init(scope: "synthetic-account-b", revision: UUID(), loggedIn: true)
    }
    func failBinding(_ failure: MusicServiceError?) { bindingFailure = failure }
    func nativeHomeBinding() throws -> MusicNativeHomeBinding {
        bindingCalls += 1
        if let bindingFailure { throw bindingFailure }
        return binding
    }
    func nativeHomeDisplayScope() -> MusicNativeCacheScope {
        .init(scope: binding.scope, revision: binding.revision, authorization: binding.authorization)
    }
    func checkNativeHomeDisplayScope(_ value: MusicNativeCacheScope) throws {
        try value.authorization.check()
        guard value == nativeHomeDisplayScope() else { throw MusicServiceError.staleResult }
    }
    func checkNativeHomeBinding(_ value: MusicNativeHomeBinding) throws {
        try value.authorization.check()
        guard value == binding else { throw MusicServiceError.staleResult }
    }
    func nativeHomeChanges() -> AsyncStream<Void> { AsyncStream { $0.yield(()); $0.finish() } }
    func nativeHomePayload(_ request: MusicNativeHomeRequest, binding: MusicNativeHomeBinding) async throws -> Data {
        calls.append(request); try checkNativeHomeBinding(binding)
        if let gate { await gate.wait() }
        try checkNativeHomeBinding(binding)
        if let failure { throw failure }
        switch request {
        case .home(let continuation), .featured(let continuation):
            return homePayload ?? canonicalHome(continuation == nil ? ["41"] : ["43"], next: continuation == nil, artists: continuation == nil)
        case .artistActivity: return activityPayload
        case .songMetadata, .heartbeatSeed, .playlistMetadata, .vipSongs:
            XCTFail("Canonical home consumption must not supplement already supplied content")
            throw MusicServiceError.invalidRequest
        }
    }
    func nativeRecommendations(platform: MusicAccountPlatform, kind: String, cursor: String?) throws -> MusicNativeTrackPage {
        guard platform == .netease, kind == "daily", let dailyPage else { throw MusicServiceError.unsupported }
        return dailyPage
    }
    func nativeCollections(platform: MusicAccountPlatform, cursor: String?) throws -> MusicAccountCollectionPage { throw MusicServiceError.unsupported }
    func nativeTracks(platform: MusicAccountPlatform, collectionID: String, cursor: String?) throws -> MusicNativeTrackPage { throw MusicServiceError.unsupported }
    func nativeOptions(platform: MusicAccountPlatform, nativeID: String, sourceKey: String) throws -> [MusicPlaybackOption] { throw MusicServiceError.unsupported }
    func nativeResolve(platform: MusicAccountPlatform, nativeID: String, selection: MusicPlaybackSelection) throws -> MusicNativeAudio { throw MusicServiceError.unsupported }
}

final class MusicNativeHomeServiceTests: XCTestCase {
    private func service(_ account: HomeAccountFixture, store: HomeSnapshotFixture = .init(),
                         peer: any MusicPlatformFixtureResponding = NativeMusicResultFixture(failure: .upstreamFailed),
                         document: NativeMusicTestLibrary = .init()) async throws -> NativeMusicService {
        let value = NativeMusicService(database: document, accounts: account, platformClient: try peer.makeClient(),
            enabledPlatforms: [.netease, .qq, .kugou], homeSnapshot: store, clock: { NativeMusicFixture.date })
        try await value.setAccess(available: true, generation: 1); return value
    }
    private func heartbeatHome(songID: String? = nil) -> Data {
        var action = MJ.obj([("type", .string("heartbeat"))])
        if let songID { action["songId"] = .string(songID) }
        let item = MJ.obj([("name", .string("心动")), ("action", action)])
        let section = MJ.obj([("id", .string("entries")), ("type", .string("entries")), ("items", .array([item]))])
        return MJ.obj([("sections", .array([section]))]).bytes
    }
    private func heartbeatAction(_ page: MusicPublicBrowsePage) throws -> MusicHomeAction {
        guard case .action(let action) = try XCTUnwrap(page.sections.first?.items.first).content else { throw MusicServiceError.invalidResponse }
        return action
    }
    func testUnseededHomeStaysExecutableAndRefreshDoesNotReadOrProveASeed() async throws {
        let account = HomeAccountFixture(), value = try await service(account)
        await account.setHome(heartbeatHome())
        let first = try await value.homePage(cursor: nil, refresh: true)
        XCTAssertEqual(try heartbeatAction(first), .heartbeat)
        let cached = try await value.homePage(cursor: nil, refresh: false)
        XCTAssertEqual(try heartbeatAction(cached), .heartbeat)
        _ = try await value.homePage(cursor: nil, refresh: true)
        let calls = await account.calls; XCTAssertEqual(calls, [.home(), .home()])
        await value.close()
    }
    func testDisplayedSeedIsConsumedWithoutSupplementAndRealHomeFailureKeepsSavedState() async throws {
        let account = HomeAccountFixture(), store = HomeSnapshotFixture(), value = try await service(account, store: store)
        await account.setHome(heartbeatHome(songID: "102"))
        let shown = try await value.homePage(cursor: nil, refresh: true)
        XCTAssertEqual(try heartbeatAction(shown), .heartbeatSong(songID: "102"))
        let saved = store.read(), queue = try await value.queue()
        await account.fail(.upstreamInvalid)
        await assertMusicFailure(.upstreamInvalid) { _ = try await value.homePage(cursor: nil, refresh: true) }
        let unchanged = try await value.queue(); XCTAssertEqual(unchanged.entries.map(\.id), queue.entries.map(\.id))
        XCTAssertEqual(unchanged.currentId, queue.currentId); XCTAssertEqual(store.read(), saved)
        await value.close()
    }
    func testChangedAccountCannotPublishLateHomeAction() async throws {
        let account = HomeAccountFixture(), store = HomeSnapshotFixture(), gate = NativeMusicGate()
        await account.setHome(heartbeatHome()); await account.suspend(gate)
        let value = try await service(account, store: store), request = Task { try await value.homePage(cursor: nil, refresh: true) }
        await gate.started(); await account.changeAccount(); await gate.release()
        do { _ = try await request.value; XCTFail("retired home published") }
        catch { XCTAssertTrue([.staleAccess, .staleResult].contains(error as? MusicServiceError)) }
        XCTAssertNil(store.read()); await value.close()
    }
    func testEmptyFavoritesDoNotDisableAnUnseededHeartbeatAction() async throws {
        let account = HomeAccountFixture(), value = try await service(account)
        await account.setHome(heartbeatHome())
        let page = try await value.homePage(cursor: nil, refresh: true)
        XCTAssertEqual(try heartbeatAction(page), .heartbeat)
        let calls = await account.calls; XCTAssertEqual(calls, [.home()])
        await value.close()
    }
    func testDisplayedQueueUsesOwnedRecordsAndPreservesSectionHistory() async throws {
        let account = HomeAccountFixture(), document = NativeMusicTestLibrary(), value = try await service(account, document: document)
        let page = try await value.homePage(cursor: nil, refresh: true), track = try XCTUnwrap(page.sections.first?.tracks.first)
        let calls = await account.calls.count
        let first = try await value.replaceDisplayedQueue([track, track], index: 1, origin: .section(title: "原版推荐栏目"), expectedRevision: 0, intent: .init())
        XCTAssertEqual(first.total, 2); XCTAssertEqual(first.queueSourceTitle, "原版推荐栏目")
        let queue = try await value.queue(); XCTAssertNotEqual(queue.entries[0].id, queue.entries[1].id)
        XCTAssertEqual(queue.currentId, queue.entries[1].id)
        let single = try await value.replaceDisplayedQueue([track], index: 0, origin: .song, expectedRevision: first.revision, intent: .init())
        XCTAssertNil(single.queueSourceTitle)
        let root = try MJ.parse(XCTUnwrap(document.snapshot().0))
        XCTAssertEqual(root["areas"]["history"]["playlists"].array.first?["title"].string, "原版推荐栏目")
        XCTAssertEqual(root["areas"]["queue"]["entries"].array.first?["songId"].string, track.songId)
        let after = await account.calls.count; XCTAssertEqual(after, calls); await value.close()
    }
    func testDisplayedQueueReadsCurrentRepositoryAndRejectsForeignRecordOrRevokedIntent() async throws {
        let account = HomeAccountFixture(), document = NativeMusicTestLibrary(), value = try await service(account, document: document)
        let page = try await value.homePage(cursor: nil, refresh: true), track = try XCTUnwrap(page.sections.first?.tracks.first)
        let replacement = try await service(account, document: document)
        let state = try await replacement.replaceDisplayedQueue([track], index: 0, origin: .song, expectedRevision: 0, intent: .init())
        let rows = try await replacement.queue(); XCTAssertEqual(rows.entries.first?.track.songId, track.songId)
        let intent = MusicQueueAuthorization(); intent.revoke(); let before = document.snapshot().0
        await assertMusicFailure(.staleAccess) { _ = try await replacement.replaceDisplayedQueue([track], index: 0, origin: .song, expectedRevision: state.revision, intent: intent) }
        XCTAssertEqual(document.snapshot().0, before)
        let foreign = try await service(account)
        await assertMusicFailure(.notFound) { _ = try await foreign.replaceDisplayedQueue([track], index: 0, origin: .song, expectedRevision: 0, intent: .init()) }
        await value.close(); await replacement.close(); await foreign.close()
    }
    func testHomeConsumesVisibleArtistContentAndExplicitActivityUsesCurrentBinding() async throws {
        let account = HomeAccountFixture(), value = try await service(account)
        let page = try await value.homePage(cursor: nil, refresh: true)
        XCTAssertEqual(page.sections.map(\.layout), [.songColumns, .artistUpdates]); XCTAssertNotNil(page.cursor)
        guard case .collection(let album)? = page.sections.last?.items.first?.content else { return XCTFail("album required") }
        XCTAssertEqual(album.nativeID, "42"); XCTAssertEqual(album.count, 8); XCTAssertEqual(album.kind, .album)
        let activity = try await value.publicDiscovery(.channel(.init(id: "artist-activity", title: "艺人新作", kind: .artistActivity), tag: ""), cursor: nil)
        XCTAssertEqual(activity.sections.first?.avatars.count, 1); XCTAssertEqual(activity.sections.first?.channel?.kind, .artistActivity)
        let calls = await account.calls; XCTAssertEqual(calls, [.home(), .artistActivity()]); await value.close()
    }
    func testGuestKeepsAnonymousHomeWithoutAccountSupplement() async throws {
        let account = HomeAccountFixture(loggedIn: false), peer = NativeMusicResultFixture([.object(["sections": .array([])])])
        let value = try await service(account, peer: peer), page = try await value.homePage(cursor: nil, refresh: true)
        XCTAssertTrue(page.sections.isEmpty)
        let calls = await account.calls, requests = await peer.calls(); XCTAssertTrue(calls.isEmpty); XCTAssertEqual(requests.map(\.operation), ["home"])
        await value.close()
    }
    func testLastSuccessRestoresPermanentReferencesWithoutRequestAuthorityAndRefreshBypassesCache() async throws {
        let account = HomeAccountFixture(), store = HomeSnapshotFixture(), value = try await service(account, store: store)
        let first = try await value.homePage(cursor: nil, refresh: true)
        _ = try await value.publicDiscovery(.home, cursor: nil)
        let before = await account.calls; XCTAssertEqual(before, [.home()])
        let text = String(decoding: try XCTUnwrap(store.read()), as: UTF8.self)
        XCTAssertFalse(text.contains("cursor")); XCTAssertFalse(text.contains("authorization")); XCTAssertFalse(text.contains("synthetic-account"))
        XCTAssertFalse(text.contains("musicInfo")); XCTAssertFalse(text.contains("Synthetic song"))
        let snapshot = try await value.restoredHome(), restored = try XCTUnwrap(snapshot)
        XCTAssertNil(restored.cursor); XCTAssertEqual(restored.sections.map(\.layout), first.sections.map(\.layout))
        XCTAssertEqual(restored.sections[0].tracks.first?.ref, first.sections[0].tracks.first?.ref)
        _ = try await value.homePage(cursor: nil, refresh: true)
        let refreshed = await account.calls; XCTAssertEqual(refreshed, [.home(), .home()]); await value.close()
    }
    func testAccountChangeKeepsPublicDisplayButRejectsOldContinuation() async throws {
        let account = HomeAccountFixture(), store = HomeSnapshotFixture(), document = NativeMusicTestLibrary(), value = try await service(account, store: store, document: document)
        let first = try await value.homePage(cursor: nil, refresh: true), cursor = try XCTUnwrap(first.cursor), saved = store.read()
        await account.changeAccount()
        let restored = try await value.restoredHome(); XCTAssertEqual(restored?.sections.map(\.id), first.sections.map(\.id)); XCTAssertEqual(store.read(), saved)
        await assertMusicFailure(.staleResult) { _ = try await value.homePage(cursor: cursor, refresh: false) }
        let restarted = try await service(account, store: store, document: document), cold = try await restarted.restoredHome()
        XCTAssertEqual(cold?.sections.map(\.id), first.sections.map(\.id)); await value.close(); await restarted.close()
    }
    func testSnapshotPreloadIsReusedWithoutReadingOrValidatingAccounts() async throws {
        let account = HomeAccountFixture(), store = HomeSnapshotFixture(), document = NativeMusicTestLibrary(), previous = try await service(account, store: store, document: document)
        _ = try await previous.homePage(cursor: nil, refresh: true); await previous.close()
        let restarted = try await service(account, store: store, document: document), before = store.readCount(), bindingCalls = await account.bindingCalls
        let preload = try await restarted.restoredHome(); XCTAssertNotNil(preload); XCTAssertEqual(store.readCount(), before + 1)
        await account.changeAccount(); await account.failBinding(.storageFailed); store.clear()
        let reused = try await restarted.restoredHome(); XCTAssertEqual(reused?.sections.map(\.id), preload?.sections.map(\.id)); XCTAssertEqual(store.readCount(), before + 1)
        let after = await account.bindingCalls, cache = await restarted.homeCache; XCTAssertEqual(after, bindingCalls); XCTAssertNil(cache)
        await restarted.close()
    }
    func testSourceRoundTripKeepsOneSnapshotButNeverDisplaysAnotherSource() async throws {
        let account = HomeAccountFixture(), store = HomeSnapshotFixture(), value = try await service(account, store: store)
        let first = try await value.homePage(cursor: nil, refresh: true), saved = store.read()
        _ = try await value.selectHomeSource(.kugou, ifRevision: 0)
        let foreign = try await value.restoredHome(); XCTAssertNil(foreign); XCTAssertEqual(store.read(), saved)
        _ = try await value.selectHomeSource(.netease, ifRevision: 1)
        let restored = try await value.restoredHome(); XCTAssertEqual(restored?.sections.map(\.id), first.sections.map(\.id)); XCTAssertEqual(store.read(), saved)
        await value.close()
    }
    func testColdOfflineSnapshotAndObservationNeverAcquireLiveAccountBinding() async throws {
        let account = HomeAccountFixture(), store = HomeSnapshotFixture(), document = NativeMusicTestLibrary(), previous = try await service(account, store: store, document: document)
        let page = try await previous.homePage(cursor: nil, refresh: true), saved = store.read()
        await account.failBinding(.unavailable); let before = await account.bindingCalls
        let restarted = try await service(account, store: store, document: document), restored = try await restarted.restoredHome()
        XCTAssertEqual(restored?.sections.map(\.id), page.sections.map(\.id)); XCTAssertNil(restored?.cursor)
        var changes = 0; for await _ in await restarted.homeChanges() { changes += 1 }
        let after = await account.bindingCalls; XCTAssertEqual(before, after); XCTAssertEqual(changes, 0)
        await assertMusicFailure(.unavailable) { _ = try await restarted.homePage(cursor: nil, refresh: false) }
        XCTAssertEqual(store.read(), saved); await previous.close(); await restarted.close()
    }
    func testLateAccountResponseCannotOverwriteNewScopeCache() async throws {
        let account = HomeAccountFixture(), store = HomeSnapshotFixture(), gate = NativeMusicGate()
        await account.suspend(gate)
        let value = try await service(account, store: store), request = Task { try await value.homePage(cursor: nil, refresh: true) }
        await gate.started(); await account.changeAccount(); await gate.release()
        do { _ = try await request.value; XCTFail("revoked response published") }
        catch { XCTAssertTrue([MusicServiceError.staleAccess, .staleResult].contains(error as? MusicServiceError)) }
        XCTAssertNil(store.read()); await value.close()
    }
    func testSnapshotRoundTripsTypedActionAndDoesNotInterpretUnconsumedExecutableURL() throws {
        let action = MusicHomeAction.similar(sourceIDs: ["1", "2"], sourceName: "艺人")
        let page = MusicNativePublicBrowseDiscovery(sections: [.init(id: "entries", title: "", items: [.init(id: "similar", title: "相似漫游", content: .action(action))], layout: .featureRail)])
        let data = try MusicNativeHomeSnapshot.encode(page, source: .netease)
        var raw = try MJ.parse(data); raw["url"] = .string("https://invalid.example/executable")
        let restored = try XCTUnwrap(MusicNativeHomeSnapshot.decode(raw.bytes, source: .netease))
        guard case .action(let result) = restored.sections[0].items[0].content else { return XCTFail("typed action required") }
        XCTAssertEqual(result, action)
    }
    func testSnapshotHasOnlyCurrentDisplayFormatAndDocumentByteBoundary() throws {
        let title = String(repeating: "a", count: 1000)
        let items = (1...650).map { MusicNativePublicBrowseItem(id: "playlist-\($0)", title: title, content: .collection(.init(platform: .netease, nativeID: String($0), title: title))) }
        let encoded = try MusicNativeHomeSnapshot.encode(.init(sections: [.init(id: "large", title: "", items: items)]), source: .netease)
        XCTAssertGreaterThan(encoded.count, 400_000); XCTAssertLessThan(encoded.count, MusicNativeHomeSnapshot.maximumBytes)
        let decoded = try XCTUnwrap(MusicNativeHomeSnapshot.decode(encoded, source: .netease)); XCTAssertEqual(decoded.sections.first?.items.count, 650)
        XCTAssertEqual(Set(try MJ.parse(encoded).fields.map(\.0)), ["source", "sections"])
        let document = MusicNativeHomeSnapshotDocument { throw MusicServiceError.unavailable }
        XCTAssertThrowsError(try document.write(Data(count: MusicNativeHomeSnapshot.maximumBytes + 1))) { XCTAssertEqual($0 as? MusicServiceError, .limit) }
    }
    func testImageQueriesSurviveRepositoryAndHomeReferenceRestoreWithoutCredentials() async throws {
        let image = "http://p1.music.126.net/cover.jpg?param=800y800&quality=90&extra=a%2Fb", expected = image.replacingOccurrences(of: "http:", with: "https:")
        let peer = NativeMusicResultFixture([.object(["sections": .array([.object(["id": .string("songs"), "type": .string("songs"),
            "items": .array([try MusicPlatformFixture.song(MusicNativePublicBrowseFixture.track("41", artwork: image))])])])])])
        let account = HomeAccountFixture(loggedIn: false), store = HomeSnapshotFixture(), document = NativeMusicTestLibrary()
        let value = try await service(account, store: store, peer: peer, document: document), page = try await value.homePage(cursor: nil, refresh: true)
        XCTAssertEqual(page.sections.first?.tracks.first?.artwork?.absoluteString, expected); await value.close()
        let restarted = try await service(account, store: store, document: document), restored = try await restarted.restoredHome()
        XCTAssertEqual(restored?.sections.first?.tracks.first?.artwork?.absoluteString, expected)
        for query in ["token=synthetic", "%63ookie=synthetic", "MUSIC_U=synthetic", "MUSIC%5FA=synthetic", "__csrf=synthetic", "csrf_token=synthetic", "uin=42", "qm_keyst=synthetic", "vkey=synthetic", "userid=42&t1=synthetic"] {
            let raw = "https://p1.music.126.net/a.jpg?param=200y200&" + query
            XCTAssertEqual(MusicModel.publicURL(.string(raw)), .string(""), query)
            let cleaned = try MusicModel.normalize(MusicNativePublicBrowseFixture.track("41", artwork: raw))
            XCTAssertEqual(cleaned["cover_url"].string, "", query)
            XCTAssertEqual(cleaned["sources"].array[0]["musicInfo"]["img"], .string(""), query)
        }
        await restarted.close()
    }
    func testSnapshotDocumentRestoresIndependentCDNQueriesWithoutPersistingCredentials() throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString, isDirectory: true)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: false); defer { try? FileManager.default.removeItem(at: root) }
        let factory: @Sendable () throws -> DocumentStore = { try DocumentStore(baseURL: root, namespace: MusicNativeHomeSnapshotDocument.namespace) }
        let document = MusicNativeHomeSnapshotDocument(storeFactory: factory), image = URL(string: "http://independent-cdn.invalid/cover.jpg?param=800y800&extra=a%2Fb")!
        let secret = URL(string: "https://independent-cdn.invalid/avatar.jpg?MUSIC_U=synthetic-private")!
        let collection = MusicPublicBrowseCollection(platform: .netease, nativeID: "42", title: "歌单", artwork: image)
        let page = MusicNativePublicBrowseDiscovery(sections: [.init(id: "s", title: "", items: [
            .init(id: "playlist", title: "歌单", artwork: image, content: .collection(collection)),
            .init(id: "private", title: "歌单", artwork: secret, content: .collection(collection))])])
        try document.write(MusicNativeHomeSnapshot.encode(page, source: .netease))
        let reopened = MusicNativeHomeSnapshotDocument(storeFactory: factory), data = try XCTUnwrap(reopened.read())
        XCTAssertFalse(String(decoding: data, as: UTF8.self).contains("synthetic-private"))
        let restored = try XCTUnwrap(MusicNativeHomeSnapshot.decode(data, source: .netease)), expected = "https://independent-cdn.invalid/cover.jpg?param=800y800&extra=a%2Fb"
        XCTAssertEqual(restored.sections[0].items[0].artwork?.absoluteString, expected); XCTAssertNil(restored.sections[0].items[1].artwork)
        guard case .collection(let saved) = restored.sections[0].items[0].content else { return XCTFail("collection required") }
        XCTAssertEqual(saved.artwork?.absoluteString, expected); try reopened.clear(); XCTAssertNil(try document.read())
    }
    func testDailyRecommendationRowsKeepNormalizedAlbumArtwork() async throws {
        let account = HomeAccountFixture(), value = try await service(account)
        await account.setDaily(.init(items: [MusicNativePublicBrowseFixture.track("41", artwork: "http://p1.music.126.net/daily.jpg?param=300y300")]))
        let page = try await value.publicDiscovery(.daily(.netease), cursor: nil), item = try XCTUnwrap(page.sections.first?.items.first)
        XCTAssertEqual(item.artwork?.absoluteString, "https://p1.music.126.net/daily.jpg?param=300y300")
        guard case .track(let track) = item.content else { return XCTFail("daily row must stay playable") }
        XCTAssertEqual(track.artwork, item.artwork); await value.close()
    }
    func testHomePagesDoNotSupplementArtistActivityAndEmptySectionsStayAbsent() async throws {
        let account = HomeAccountFixture(), value = try await service(account)
        let first = try await value.homePage(cursor: nil, refresh: true), next = try await value.homePage(cursor: XCTUnwrap(first.cursor), refresh: false)
        XCTAssertEqual(next.sections.map(\.layout), [.songColumns])
        await account.setHome(canonicalHome(artists: false))
        let refreshed = try await value.homePage(cursor: nil, refresh: true); XCTAssertEqual(refreshed.sections.map(\.layout), [.songColumns])
        let calls = await account.calls; XCTAssertFalse(calls.contains { if case .artistActivity = $0 { return true }; return false }); await value.close()
    }
    func testExistingArtistSectionDoesNotTriggerAnotherActivityRead() async throws {
        let account = HomeAccountFixture(), value = try await service(account)
        await account.setHome(MJ.obj([("sections", .array([canonicalArtistSection()]))]).bytes)
        let page = try await value.homePage(cursor: nil, refresh: true)
        XCTAssertEqual(page.sections.map(\.layout), [.artistUpdates]); XCTAssertEqual(page.sections.first?.channel?.identity, "wy:artist-activity")
        XCTAssertEqual(page.sections.first?.items.map(\.title), ["新专辑"])
        let calls = await account.calls; XCTAssertEqual(calls, [.home()]); await value.close()
    }
    func testCurrentHomeAndColdSnapshotReadSharedSongRecordsWithoutHydration() async throws {
        let account = HomeAccountFixture(), store = HomeSnapshotFixture(), document = NativeMusicTestLibrary(), value = try await service(account, store: store, document: document)
        await account.setHome(canonicalHome(["41", "43", "41"], artists: true))
        let live = try await value.homePage(cursor: nil, refresh: true); await value.close(); await account.failBinding(.sourceUnavailable)
        let restarted = try await service(account, store: store, document: document), snapshot = try await restarted.restoredHome()
        let cached = try XCTUnwrap(snapshot)
        for page in [live, cached] {
            XCTAssertEqual(page.sections.map(\.layout), [.songColumns, .artistUpdates])
            XCTAssertEqual(page.sections[0].tracks.map { $0.sources[0].nativeId }, ["41", "43", "41"])
            XCTAssertEqual(page.sections[0].tracks[0].songId, page.sections[0].tracks[2].songId)
            XCTAssertNotEqual(page.sections[0].tracks[0].songId, page.sections[0].tracks[1].songId)
            XCTAssertNotEqual(page.sections[0].items[0].id, page.sections[0].items[2].id)
            guard case .collection(let album) = page.sections[1].items[0].content else { return XCTFail("album required") }
            XCTAssertEqual(album.nativeID, "42"); XCTAssertEqual(album.count, 8)
        }
        let calls = await account.calls; XCTAssertEqual(calls, [.home()]); await restarted.close()
    }
    func testCachedArtistAlbumKeepsSummaryIndependentlyOfMemberSong() async throws {
        let account = HomeAccountFixture(), store = HomeSnapshotFixture(), document = NativeMusicTestLibrary(), value = try await service(account, store: store, document: document)
        await account.setHome(canonicalHome(["41"]))
        let live = try await value.homePage(cursor: nil, refresh: true), snapshot = try await value.restoredHome()
        let restored = try XCTUnwrap(snapshot)
        XCTAssertEqual(restored.sections[0].tracks.first?.songId, live.sections[0].tracks.first?.songId)
        guard case .collection(let album) = restored.sections[1].items[0].content else { return XCTFail("album required") }
        XCTAssertEqual(album.title, "新专辑"); XCTAssertEqual(album.count, 8); XCTAssertEqual(album.artwork?.absoluteString, "https://p1.music.126.net/album.jpg")
        XCTAssertNotEqual(album.artwork, restored.sections[0].tracks.first?.artwork); await value.close()
    }
    func testGuestConsumesCanonicalPublicContentWithoutPrivateAccountReads() async throws {
        let account = HomeAccountFixture(loggedIn: false), peer = NativeMusicResultFixture([try RuntimeJSON.decode(canonicalHome(["71"], artists: false))])
        let value = try await service(account, peer: peer), page = try await value.homePage(cursor: nil, refresh: true)
        XCTAssertEqual(page.sections[0].tracks.map { $0.sources[0].nativeId }, ["71"])
        let calls = await account.calls, requests = await peer.calls(); XCTAssertTrue(calls.isEmpty); XCTAssertEqual(requests.map(\.operation), ["home"])
        await value.close()
    }
    func testRestoredDisplayKeepsPublicAlbumsAndSimilarActionsButCannotRestoreAccountEntrancesOrTabs() async throws {
        let store = HomeSnapshotFixture(), account = HomeAccountFixture(loggedIn: false)
        let channel = MusicPublicBrowseChannel(id: "artist-activity", title: "关注艺人", kind: .artistActivity, platform: .netease)
        let album = MusicPublicBrowseCollection(platform: .netease, nativeID: "7", kind: .album, title: "公开专辑")
        let entries: [MusicNativePublicBrowseItem] = [
            .init(id: "similar", title: "", content: .action(.similar(sourceIDs: ["41"], sourceName: ""))),
            .init(id: "similar-artist", title: "", content: .action(.similarArtist(sourceIDs: ["42"], sourceName: ""))),
            .init(id: "heartbeat", title: "", content: .action(.heartbeat), requiresAccount: true),
            .init(id: "heartbeat-song", title: "", content: .action(.heartbeatSong(songID: "41")), requiresAccount: true),
            .init(id: "daily", title: "", content: .daily, requiresAccount: true),
            .init(id: "artist", title: "", content: .channel(channel), requiresAccount: true),
            .init(id: "private-playlist", title: album.title, content: .collection(album), requiresAccount: true)]
        let page = MusicNativePublicBrowseDiscovery(sections: [.init(id: "mixed", title: "推荐", items: [
            .init(id: "album", title: album.title, content: .collection(album)), .init(id: "fm", title: "漫游", content: .action(.fm(mode: .default, submode: "", entry: nil)))
        ] + entries, channel: channel), .init(id: "private", title: "", items: [
            .init(id: "style", title: "", content: .action(.daily(branch: .style)), requiresAccount: true)]),
            .init(id: "tabs", title: "", items: [], tabs: [
                .init(id: "private", title: "账号", items: [
                    .init(id: "heartbeat", title: "", content: .action(.heartbeat), requiresAccount: true)]),
                .init(id: "public", title: "公开", items: entries)
            ], selectedTab: "private")])
        store.write(try MusicNativeHomeSnapshot.encode(page, source: .netease))
        let value = try await service(account, store: store), snapshot = try await value.restoredHome()
        let restored = try XCTUnwrap(snapshot)
        XCTAssertEqual(restored.sections.count, 2)
        XCTAssertEqual(restored.sections[0].items.map(\.id), ["album", "fm", "similar", "similar-artist"])
        XCTAssertNil(restored.sections[0].channel)
        XCTAssertEqual(restored.sections[1].tabs.map(\.id), ["public"])
        XCTAssertEqual(restored.sections[1].selectedTab, "public")
        XCTAssertEqual(restored.sections[1].items.map(\.id), ["similar", "similar-artist"])
        XCTAssertEqual(restored.sections[1].tabs[0].items.map(\.id), ["similar", "similar-artist"])
        let calls = await account.calls, bindings = await account.bindingCalls; XCTAssertTrue(calls.isEmpty); XCTAssertEqual(bindings, 0); await value.close()
    }
    func testSnapshotWithoutRuntimeAccountDeclarationsIsDiscardedWithoutAcquiringAccountBinding() async throws {
        let account = HomeAccountFixture(loggedIn: false), store = HomeSnapshotFixture()
        let entry = MusicNativePublicBrowseItem(id: "private", title: "旧入口",
            content: .action(.daily(branch: .default)), requiresAccount: true)
        var raw = try JSONSerialization.jsonObject(with: MusicNativeHomeSnapshot.encode(
            .init(sections: [.init(id: "entries", title: "", items: [entry])]), source: .netease)) as! [String: Any]
        var sections = raw["sections"] as! [[String: Any]]
        var items = sections[0]["items"] as! [[String: Any]]
        items[0].removeValue(forKey: "requiresAccount")
        sections[0]["items"] = items; raw["sections"] = sections
        store.write(try JSONSerialization.data(withJSONObject: raw))
        let value = try await service(account, store: store), restored = try await value.restoredHome()
        XCTAssertNil(restored)
        let calls = await account.bindingCalls; XCTAssertEqual(calls, 0); await value.close()
    }
    func testHomeSourceSelectionRejectsUnimplementedProvidersWithoutDiscardingValidCache() async throws {
        let account = HomeAccountFixture(), store = HomeSnapshotFixture(), value = try await service(account, store: store)
        let first = try await value.homePage(cursor: nil, refresh: true), saved = store.read(), before = await account.calls
        let selection = try await value.homeSourceSelection(); XCTAssertEqual(selection, .init(selected: .netease, revision: 0, available: [.netease, .kugou]))
        _ = try await value.selectHomeSource(.netease, ifRevision: 0)
        await assertMusicFailure(.unsupported) { _ = try await value.selectHomeSource(.qq, ifRevision: 0) }
        let cached = try await value.homePage(cursor: nil, refresh: false); XCTAssertEqual(cached.sections.map(\.id), first.sections.map(\.id))
        let after = await account.calls; XCTAssertEqual(after, before); XCTAssertEqual(store.read(), saved)
        XCTAssertNil(try MusicNativeHomeSnapshot.decode(XCTUnwrap(saved), source: .qq)); await value.close()
    }
    func testUnimplementedChannelSourceNeverFallsBackToNetease() async throws {
        let peer = NativeMusicResultFixture(), value = try await service(HomeAccountFixture(), peer: peer)
        for kind in [MusicPublicBrowseChannel.Kind.playlists, .rankings, .featured, .artistActivity, .vip] {
            await assertMusicFailure(.unsupported) { _ = try await value.publicDiscovery(.channel(.init(id: "same-id", title: "频道", kind: kind, platform: .qq), tag: ""), cursor: nil) }
        }
        let calls = await peer.calls(); XCTAssertTrue(calls.isEmpty); await value.close()
    }
    func testVIPConsumesCurrentTerminalResultWithoutANativeContinuationGate() async throws {
        let account = HomeAccountFixture(), peer = NativeMusicResultFixture([try RuntimeJSON.decode(canonicalHome(["91"], artists: false))])
        let value = try await service(account, peer: peer)
        _ = try await value.homeSourceSelection()
        let binding = try await value.currentHomeBinding(), destination = MusicPublicBrowseDestination.channel(.init(id: "vip", title: "VIP", kind: .vip), tag: "")
        let revision = await value.homeSourceRevision, epoch = await value.epoch
        let key = "wy:" + MusicNativePublicBrowseDiscovery.key(destination) + ":" + revision.uuidString + ":" + binding.revision.uuidString
        let position = try MusicNativePublicBrowseCursor.read(nil, key: key, epoch: epoch), cursor = try position.encode(key: key, epoch: epoch, offset: 0)
        let page = try await value.publicDiscovery(destination, cursor: cursor)
        XCTAssertEqual(page.sections.first?.tracks.first?.sources.first?.nativeId, "91"); XCTAssertNil(page.cursor)
        let requests = await peer.calls(), payloads = await account.calls
        XCTAssertEqual(requests.map(\.operation), ["channel"]); XCTAssertEqual(requests.first?.platform, "wy")
        XCTAssertEqual(requests.first?.parameters[music: "id"], .string("vip")); XCTAssertTrue(payloads.isEmpty)
        await value.close()
    }
    func testActivityFailureKeepsLastHomeAndRevokedExplicitActivityCannotPublish() async throws {
        let account = HomeAccountFixture(), store = HomeSnapshotFixture(), value = try await service(account, store: store)
        _ = try await value.homePage(cursor: nil, refresh: true); let saved = store.read()
        let destination = MusicPublicBrowseDestination.channel(.init(id: "artist-activity", title: "艺人", kind: .artistActivity), tag: "")
        await account.fail(.networkOffline)
        await assertMusicFailure(.networkOffline) { _ = try await value.publicDiscovery(destination, cursor: nil) }
        XCTAssertEqual(store.read(), saved); await value.close()
        let otherAccount = HomeAccountFixture(), otherStore = HomeSnapshotFixture(), gate = NativeMusicGate()
        await otherAccount.suspend(gate)
        let other = try await service(otherAccount, store: otherStore), request = Task { try await other.publicDiscovery(destination, cursor: nil) }
        await gate.started(); await otherAccount.changeAccount(); await gate.release()
        do { _ = try await request.value; XCTFail("revoked activity published") }
        catch { XCTAssertTrue([MusicServiceError.staleAccess, .staleResult].contains(error as? MusicServiceError)) }
        XCTAssertNil(otherStore.read()); await other.close()
    }
    func testEmptyVisibleGuestPageAdvancesUsingCanonicalContinuation() async throws {
        let first: RuntimeValue = .object(["sections": .array([]), "consumed": .number(1), "continuation": .object(["cursor": .string("1")])])
        let peer = NativeMusicResultFixture([first, try RuntimeJSON.decode(canonicalHome(["71"], artists: false))]), account = HomeAccountFixture(loggedIn: false)
        let value = try await service(account, peer: peer), page = try await value.homePage(cursor: nil, refresh: true)
        XCTAssertTrue(page.sections.isEmpty); XCTAssertEqual(page.consumed, 1)
        let second = try await value.homePage(cursor: XCTUnwrap(page.cursor), refresh: false)
        XCTAssertEqual(second.sections.first?.tracks.first?.sources.first?.nativeId, "71"); XCTAssertNil(second.cursor)
        let calls = await peer.calls(); XCTAssertEqual(calls.count, 2); XCTAssertEqual(calls[1].parameters[music: "continuation"], .object(["cursor": .string("1")]))
        let accountCalls = await account.calls; XCTAssertTrue(accountCalls.isEmpty); await value.close()
    }
    func testSongColumnSelectionReplacesAllSevenTracksInOneQueueTransaction() async throws {
        let account = HomeAccountFixture(), document = NativeMusicTestLibrary(), value = try await service(account, document: document), ids = (1...7).map(String.init)
        await account.setHome(canonicalHome(ids, artists: false))
        let page = try await value.homePage(cursor: nil, refresh: true), section = try XCTUnwrap(page.sections.first)
        XCTAssertEqual(section.layout, .songColumns)
        let reads = await account.calls, writes = document.snapshot().2
        let state = try await value.replaceDisplayedQueue(section.tracks, index: 5, origin: .section(title: section.title), expectedRevision: 0, intent: .init())
        let queue = try await value.queue(); XCTAssertEqual(state.total, 7); XCTAssertEqual(queue.entries.map { $0.track.sources[0].nativeId }, ids)
        XCTAssertEqual(Set(queue.entries.map(\.id)).count, 7); XCTAssertEqual(Set(queue.entries.map { $0.track.songId }).count, 7)
        XCTAssertEqual(queue.currentId, queue.entries[5].id); XCTAssertEqual(document.snapshot().2, writes + 1)
        let after = await account.calls; XCTAssertEqual(after, reads); await value.close()
    }
}
