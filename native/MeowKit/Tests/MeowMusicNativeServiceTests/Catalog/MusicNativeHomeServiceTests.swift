import XCTest
import MeowMusicFeature
@testable import MeowMusicNativeService

private final class HomeSnapshotFixture: MusicHomeSnapshotStoring, @unchecked Sendable {
    private let lock = NSLock()
    private var data: Data?
    func read() -> Data? { lock.lock(); defer { lock.unlock() }; return data }
    func write(_ value: Data) { lock.lock(); data = value; lock.unlock() }
    func clear() { lock.lock(); data = nil; lock.unlock() }
}
private actor HomeAccountFixture: MusicNativeAccountProviding, MusicNativeHomeDisplayProviding {
    var binding: MusicNativeHomeBinding
    var calls: [MusicNativeHomeRequest] = []
    var gate: NativeMusicGate?
    var bindingFailure: MusicServiceError?
    var bindingCalls = 0
    init(loggedIn: Bool = true) { binding = .init(scope: loggedIn ? "synthetic-account-a" : "guest", revision: UUID(), loggedIn: loggedIn) }
    func suspend(_ gate: NativeMusicGate) { self.gate = gate }
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
        switch request {
        case .home, .featured:
            if let gate { await gate.wait() }
            try checkNativeHomeBinding(binding)
            return Data(#"{"code":200,"data":{"blocks":[{"positionCode":"songs","dslData":{"items":[{"resourceType":"song","id":"41"}]}}],"hasMore":true,"cursor":"4"}}"#.utf8)
        case .artistActivity:
            return Data(#"{"code":200,"data":{"title":"艺人新作","newWorks":[{"info":{"blockType":"mv","songLists":[{"id":8}]}},{"publishTime":1234,"info":{"blockType":"album","albumId":"42","albumSongCount":8,"blockTitle":{"resourceName":"新专辑","artistName":"艺人","resourcePicUrl":"https://p1.music.126.net/album.jpg","imgUrl":"https://p1.music.126.net/avatar.jpg"},"songLists":[{"id":41}]}}]}}"#.utf8)
        case .songMetadata(let ids):
            return MJ.obj([("code", .n(200)), ("songs", .array(ids.map(MusicNativePublicBrowseFixture.song)))]).bytes
        case .playlistMetadata, .vipSongs: throw MusicServiceError.invalidRequest
        }
    }
    func nativeRecommendations(platform: MusicAccountPlatform, kind: String, cursor: String?) throws -> MusicNativeTrackPage { throw MusicServiceError.unsupported }
    func nativeCollections(platform: MusicAccountPlatform, cursor: String?) throws -> MusicAccountCollectionPage { throw MusicServiceError.unsupported }
    func nativeTracks(platform: MusicAccountPlatform, collectionID: String, cursor: String?) throws -> MusicNativeTrackPage { throw MusicServiceError.unsupported }
    func nativeOptions(platform: MusicAccountPlatform, nativeID: String, sourceKey: String) throws -> [MusicPlaybackOption] { throw MusicServiceError.unsupported }
    func nativeResolve(platform: MusicAccountPlatform, nativeID: String, selection: MusicPlaybackSelection) throws -> MusicNativeAudio { throw MusicServiceError.unsupported }
}

final class MusicNativeHomeServiceTests: XCTestCase {
    private func service(_ account: HomeAccountFixture, store: HomeSnapshotFixture = .init(),
                         http: any MusicNativeHTTPTransport = NativeMusicNoNetwork(),
                         document: NativeMusicMemoryDocument = .init()) async throws -> NativeMusicService {
        let value = NativeMusicService(document: document, accounts: account, qq: NativeMusicQQFixture(),
            enabledPlatforms: [.netease, .qq, .kugou], homeSnapshot: store, transport: http, clock: { NativeMusicFixture.date })
        try await value.setAccess(available: true, generation: 1); return value
    }
    func testDisplayedHomeQueueRecoversOwnedRowsAndPreservesSectionHistory() async throws {
        let account = HomeAccountFixture(), document = NativeMusicMemoryDocument()
        let value = try await service(account, document: document)
        let page = try await value.homePage(cursor: nil, refresh: true)
        guard case .track(let track) = page.sections[0].items[0].content else { return XCTFail("song row required") }
        let stale = MusicRemoteTrack(ref: .init(kind: .handle, id: "expired-display-handle"),
            songId: track.songId, title: track.title, artist: track.artist, album: track.album,
            duration: track.duration, sources: track.sources, artwork: track.artwork)
        let calls = await account.calls.count
        let first = try await value.replaceDisplayedQueue([stale, stale], index: 1,
            origin: .section(title: "原版推荐栏目"), expectedRevision: 0, intent: .init())
        XCTAssertEqual(first.total, 2)
        let rows = try await value.queue()
        XCTAssertNotEqual(rows.entries[0].id, rows.entries[1].id)
        XCTAssertEqual(rows.currentId, rows.entries[1].id)
        let after = await account.calls.count
        XCTAssertEqual(calls, after, "an owned display snapshot does not require a new network catalogue")
        _ = try await value.replaceDisplayedQueue([stale], index: 0, origin: .song,
            expectedRevision: first.revision, intent: .init())
        let root = try MJ.parse(XCTUnwrap(document.snapshot().0))
        XCTAssertEqual(root["records"][MusicModel.key("history")]["playlists"].array.first?["title"].string, "原版推荐栏目")
        XCTAssertEqual(root["records"][MusicModel.key("queue")]["entries"].array.first?["context"]["sourceType"].string, "song")
    }
    func testDisplayedHomeQueueRehydratesExactNativeIDAndRejectsRevokedIntent() async throws {
        let account = HomeAccountFixture(), value = try await service(account)
        let page = try await value.homePage(cursor: nil, refresh: true)
        guard case .track(let track) = page.sections[0].items[0].content else { return XCTFail("song row required") }
        let document = NativeMusicMemoryDocument(), replacement = try await service(account, document: document)
        let state = try await replacement.replaceDisplayedQueue([track], index: 0,
            origin: .section(title: "原版推荐"), expectedRevision: 0, intent: .init())
        XCTAssertEqual(state.total, 1)
        let rows = try await replacement.queue()
        XCTAssertEqual(rows.entries.first?.track.songId, track.songId)
        XCTAssertEqual(rows.entries.first?.track.sources, track.sources)
        let intent = MusicQueueAuthorization(); intent.revoke()
        let before = document.snapshot()
        do {
            _ = try await replacement.replaceDisplayedQueue([track], index: 0, origin: .song,
                expectedRevision: state.revision, intent: intent)
            XCTFail("revoked display intent committed")
        } catch { XCTAssertEqual(error as? MusicServiceError, .staleAccess) }
        XCTAssertEqual(document.snapshot().0, before.0)
    }
    func testSignedInHomeAndArtistMetadataUseSameNativeBindingNotAnonymousHTTP() async throws {
        let account = HomeAccountFixture(), value = try await service(account)
        let page = try await value.homePage(cursor: nil, refresh: true)
        XCTAssertEqual(page.sections.count, 2)
        XCTAssertEqual(page.sections.last?.layout, .artistUpdates)
        XCTAssertEqual(page.sections.last?.title, "艺人新作")
        XCTAssertEqual(page.sections.last?.avatars.count, 1)
        XCTAssertEqual(page.sections.last?.channel?.kind, .artistActivity)
        XCTAssertNotNil(page.cursor)
        let calls = await account.calls
        XCTAssertEqual(calls.count, 3)
        XCTAssertTrue(calls.contains(.home())); XCTAssertTrue(calls.contains(.artistActivity()))
        XCTAssertTrue(calls.contains(.songMetadata(ids: ["41"])))
    }
    func testGuestKeepsAnonymousHomeAndDoesNotSynthesizeArtistActivity() async throws {
        let account = HomeAccountFixture(loggedIn: false)
        let http = NativeMusicHTTPFixture([try MJ.parse(Data(#"{"code":200,"data":{"blocks":[]}}"#.utf8))])
        let value = try await service(account, http: http)
        let page = try await value.homePage(cursor: nil, refresh: true)
        XCTAssertTrue(page.sections.isEmpty)
        let calls = await account.calls; XCTAssertTrue(calls.isEmpty)
        let anonymous = await http.calls(); XCTAssertEqual(anonymous.count, 1)
    }
    func testLastSuccessRestoresWithoutCursorOrPersistedHandlesAndRefreshBypassesCache() async throws {
        let account = HomeAccountFixture(), store = HomeSnapshotFixture()
        let value = try await service(account, store: store)
        let first = try await value.homePage(cursor: nil, refresh: true)
        let before = await account.calls.count
        _ = try await value.publicDiscovery(.home, cursor: nil)
        let after = await account.calls.count; XCTAssertEqual(before, after)
        let encoded = try XCTUnwrap(store.read()), text = String(decoding: encoded, as: UTF8.self)
        XCTAssertFalse(text.contains("cursor")); XCTAssertFalse(text.contains("\"ref\"")); XCTAssertFalse(text.contains("authorization"))
        let snapshot = try await value.restoredHome()
        let restored = try XCTUnwrap(snapshot)
        XCTAssertNil(restored.cursor)
        XCTAssertEqual(restored.sections.map(\.id), first.sections.map(\.id))
        XCTAssertEqual(restored.sections.last?.channel?.kind, .artistActivity)
        if case .track(let old) = first.sections[0].items[0].content,
           case .track(let new) = restored.sections[0].items[0].content { XCTAssertNotEqual(old.ref, new.ref) }
        else { XCTFail("expected freshly registered native track handles") }
        _ = try await value.homePage(cursor: nil, refresh: true)
        let refreshed = await account.calls.count; XCTAssertEqual(refreshed, before * 2)
    }
    func testAccountChangeRejectsOldContinuationAndMismatchedSnapshot() async throws {
        let account = HomeAccountFixture(), store = HomeSnapshotFixture()
        let value = try await service(account, store: store)
        let first = try await value.homePage(cursor: nil, refresh: true)
        let cursor = try XCTUnwrap(first.cursor)
        await account.changeAccount()
        let restored = try await value.restoredHome(); XCTAssertNil(restored)
        do { _ = try await value.homePage(cursor: cursor, refresh: false); XCTFail("old account cursor accepted") }
        catch { XCTAssertEqual(error as? MusicServiceError, .staleResult) }
    }
    func testColdOfflineSnapshotAndObservationNeverAcquireLiveAccountBinding() async throws {
        let account = HomeAccountFixture(), store = HomeSnapshotFixture()
        let previous = try await service(account, store: store)
        let page = try await previous.homePage(cursor: nil, refresh: true)
        let saved = try XCTUnwrap(store.read())
        await account.failBinding(.unavailable)
        let before = await account.bindingCalls
        let restarted = try await service(account, store: store)
        let snapshot = try await restarted.restoredHome()
        XCTAssertEqual(snapshot?.sections.map(\.id), page.sections.map(\.id))
        XCTAssertNil(snapshot?.cursor)
        var changes = 0
        for await _ in await restarted.homeChanges() { changes += 1 }
        let after = await account.bindingCalls
        XCTAssertEqual(before, after, "Neither restore nor observation may validate the account")
        XCTAssertEqual(changes, 0)
        do { _ = try await restarted.homePage(cursor: nil, refresh: false); XCTFail("Display scope authorized HTTP") }
        catch { XCTAssertEqual(error as? MusicServiceError, .unavailable) }
        XCTAssertEqual(store.read(), saved)
        await previous.close(); await restarted.close()
    }
    func testLateAccountResponseCannotOverwriteNewScopeCache() async throws {
        let account = HomeAccountFixture(), store = HomeSnapshotFixture(), gate = NativeMusicGate()
        await account.suspend(gate)
        let value = try await service(account, store: store)
        let request = Task { try await value.homePage(cursor: nil, refresh: true) }
        await gate.started(); await account.changeAccount(); await gate.release()
        do { _ = try await request.value; XCTFail("revoked response published") }
        catch { XCTAssertTrue([MusicServiceError.staleAccess, .staleResult].contains(error as? MusicServiceError ?? .unavailable)) }
        XCTAssertNil(store.read())
    }
    func testSnapshotRoundTripsActionWithoutAcceptingRawExecutableURLs() throws {
        let action = MusicHomeAction.similar(sourceIDs: ["1", "2"], sourceName: "艺人")
        let page = MusicNativePublicBrowseDiscovery(sections: [.init(id: "entries", title: "",
            items: [.init(id: "similar", title: "相似漫游", content: .action(action))], layout: .featureRail)])
        let data = try MusicNativeHomeSnapshot.encode(page, scope: "synthetic")
        let restored = try XCTUnwrap(MusicNativeHomeSnapshot.decode(data, scope: "synthetic"))
        if case .action(let value) = restored.sections[0].items[0].content { XCTAssertEqual(value, action) }
        else { XCTFail("action was downgraded") }
        var raw = try MJ.parse(data); raw["url"] = .string("https://invalid.example/executable")
        XCTAssertThrowsError(try MusicNativeHomeSnapshot.decode(raw.bytes, scope: "synthetic"))
    }
}
