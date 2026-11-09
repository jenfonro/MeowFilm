import XCTest
import MeowMusicFeature
import MeowStorage
@testable import MeowMusicNativeService

private final class HomeSnapshotFixture: MusicHomeSnapshotStoring, @unchecked Sendable {
    private let lock = NSLock()
    private var data: Data?
    private var reads = 0
    func read() -> Data? { lock.lock(); defer { lock.unlock() }; reads += 1; return data }
    func readCount() -> Int { lock.lock(); defer { lock.unlock() }; return reads }
    func write(_ value: Data) { lock.lock(); data = value; lock.unlock() }
    func clear() { lock.lock(); data = nil; lock.unlock() }
}
private actor HomeAccountFixture: MusicNativeAccountProviding, MusicNativeHomeDisplayProviding {
    var binding: MusicNativeHomeBinding
    var calls: [MusicNativeHomeRequest] = []
    var gate: NativeMusicGate?
    var bindingFailure: MusicServiceError?
    var bindingCalls = 0
    var homePayload: Data?
    var activityPayload: Data?
    var activityGate: NativeMusicGate?
    var activityFailure: MusicServiceError?
    var dailyPage: MusicNativeTrackPage?
    init(loggedIn: Bool = true) { binding = .init(scope: loggedIn ? "synthetic-account-a" : "guest", revision: UUID(), loggedIn: loggedIn) }
    func suspend(_ gate: NativeMusicGate) { self.gate = gate }
    func setHome(_ value: Data) { homePayload = value }
    func setActivity(_ value: Data) { activityPayload = value }
    func setDaily(_ value: MusicNativeTrackPage) { dailyPage = value }
    func suspendActivity(_ gate: NativeMusicGate) { activityGate = gate }
    func failActivity(_ value: MusicServiceError) { activityFailure = value }
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
        case .home(let continuation), .featured(let continuation):
            if let gate { await gate.wait() }
            try checkNativeHomeBinding(binding)
            if let homePayload { return homePayload }
            if continuation != nil {
                return Data(#"{"code":200,"data":{"blocks":[{"positionCode":"songs","dslData":{"items":[{"resourceType":"song","id":"43"}]}}],"hasMore":false}}"#.utf8)
            }
            return Data(#"{"code":200,"data":{"blocks":[{"positionCode":"songs","dslData":{"items":[{"resourceType":"song","id":"41"}]}}],"hasMore":true,"cursor":"4"}}"#.utf8)
        case .artistActivity:
            if let activityGate { await activityGate.wait() }
            try checkNativeHomeBinding(binding)
            if let activityFailure { throw activityFailure }
            if let activityPayload { return activityPayload }
            return Data(#"{"code":200,"data":{"title":"艺人新作","newWorks":[{"info":{"blockType":"mv","songLists":[{"id":8}]}},{"publishTime":1234,"info":{"blockType":"album","albumId":"42","albumSongCount":8,"blockTitle":{"resourceName":"新专辑","artistName":"艺人","resourcePicUrl":"https://p1.music.126.net/album.jpg","imgUrl":"https://p1.music.126.net/avatar.jpg"},"songLists":[{"id":41}]}}]}}"#.utf8)
        case .songMetadata(let ids):
            return MJ.obj([("code", .n(200)), ("songs", .array(ids.map(MusicNativePublicBrowseFixture.song)))]).bytes
        case .playlistMetadata, .vipSongs: throw MusicServiceError.invalidRequest
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
        XCTAssertEqual(first.queueSourceTitle, "原版推荐栏目")
        let rows = try await value.queue()
        XCTAssertNotEqual(rows.entries[0].id, rows.entries[1].id)
        XCTAssertEqual(rows.currentId, rows.entries[1].id)
        let after = await account.calls.count
        XCTAssertEqual(calls, after, "an owned display snapshot does not require a new network catalogue")
        let single = try await value.replaceDisplayedQueue([stale], index: 0, origin: .song,
            expectedRevision: first.revision, intent: .init())
        XCTAssertNil(single.queueSourceTitle, "a new song entry must not retain the previous section title")
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
    func testHomeReadsVisibleContentAndExplicitArtistActivityUsesSameAccountBinding() async throws {
        let account = HomeAccountFixture(), value = try await service(account)
        let page = try await value.homePage(cursor: nil, refresh: true)
        XCTAssertEqual(page.sections.map(\.layout), [.songColumns, .artistUpdates])
        XCTAssertNotNil(page.cursor)
        let homeCalls = await account.calls
        XCTAssertEqual(homeCalls, [.home(), .artistActivity(), .songMetadata(ids: ["41"])])
        let activity = try await value.publicDiscovery(.channel(
            .init(id: "artist", title: "艺人新作", kind: .artistActivity), tag: ""), cursor: nil)
        XCTAssertEqual(activity.sections.last?.layout, .artistUpdates)
        XCTAssertEqual(activity.sections.last?.title, "艺人新作")
        XCTAssertEqual(activity.sections.last?.avatars.count, 1)
        XCTAssertEqual(activity.sections.last?.channel?.kind, .artistActivity)
        let calls = await account.calls
        XCTAssertEqual(calls.count, 4)
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
        XCTAssertFalse(text.contains("\"scope\"")); XCTAssertFalse(text.contains("\"revision\""))
        XCTAssertFalse(text.contains("synthetic-account"))
        let snapshot = try await value.restoredHome()
        let restored = try XCTUnwrap(snapshot)
        XCTAssertNil(restored.cursor)
        XCTAssertEqual(restored.sections.map(\.id), first.sections.map(\.id))
        XCTAssertEqual(restored.sections.map(\.layout), [.songColumns, .artistUpdates])
        XCTAssertEqual(restored.sections.flatMap(\.items).map(\.subtitle), first.sections.flatMap(\.items).map(\.subtitle))
        if case .track(let old) = first.sections[0].items[0].content,
           case .track(let new) = restored.sections[0].items[0].content { XCTAssertNotEqual(old.ref, new.ref) }
        else { XCTFail("expected freshly registered native track handles") }
        _ = try await value.homePage(cursor: nil, refresh: true)
        let refreshed = await account.calls.count; XCTAssertEqual(refreshed, before * 2)
    }
    func testAccountChangeKeepsDisplaySnapshotButRejectsOldContinuation() async throws {
        let account = HomeAccountFixture(), store = HomeSnapshotFixture()
        let value = try await service(account, store: store)
        let first = try await value.homePage(cursor: nil, refresh: true)
        let cursor = try XCTUnwrap(first.cursor)
        let saved = store.read()
        await account.changeAccount()
        let restored = try await value.restoredHome()
        XCTAssertEqual(restored?.sections.map(\.id), first.sections.map(\.id))
        XCTAssertNil(restored?.cursor); XCTAssertEqual(store.read(), saved)
        do { _ = try await value.homePage(cursor: cursor, refresh: false); XCTFail("old account cursor accepted") }
        catch { XCTAssertEqual(error as? MusicServiceError, .staleResult) }
        let restarted = try await service(account, store: store)
        let crossAccount = try await restarted.restoredHome()
        XCTAssertEqual(crossAccount?.sections.map(\.id), first.sections.map(\.id))
        await value.close(); await restarted.close()
    }
    func testSnapshotPreloadIsReusedWithoutReadingOrValidatingAccounts() async throws {
        let account = HomeAccountFixture(), store = HomeSnapshotFixture()
        let previous = try await service(account, store: store)
        _ = try await previous.homePage(cursor: nil, refresh: true)
        await previous.close()
        let restarted = try await service(account, store: store)
        let before = store.readCount(), bindingCalls = await account.bindingCalls
        let preload = try await restarted.restoredHome()
        XCTAssertNotNil(preload); XCTAssertEqual(store.readCount(), before + 1)
        await account.changeAccount(); await account.failBinding(.storageFailed)
        store.clear()
        let reused = try await restarted.restoredHome()
        XCTAssertEqual(reused?.sections.map(\.id), preload?.sections.map(\.id))
        XCTAssertEqual(store.readCount(), before + 1)
        let after = await account.bindingCalls; XCTAssertEqual(after, bindingCalls)
        let cache = await restarted.homeCache; XCTAssertNil(cache, "Display data is never a request cache")
        await restarted.close()
    }
    func testSourceRoundTripKeepsOneSnapshotButNeverDisplaysAnotherSource() async throws {
        let account = HomeAccountFixture(), store = HomeSnapshotFixture()
        let value = try await service(account, store: store)
        let first = try await value.homePage(cursor: nil, refresh: true), saved = store.read()
        _ = try await value.selectHomeSource(.kugou, ifRevision: 0)
        let foreign = try await value.restoredHome()
        XCTAssertNil(foreign); XCTAssertEqual(store.read(), saved)
        _ = try await value.selectHomeSource(.netease, ifRevision: 1)
        let restored = try await value.restoredHome()
        XCTAssertEqual(restored?.sections.map(\.id), first.sections.map(\.id))
        XCTAssertEqual(store.read(), saved)
        await value.close()
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
        let data = try MusicNativeHomeSnapshot.encode(page, source: .netease)
        let restored = try XCTUnwrap(MusicNativeHomeSnapshot.decode(data, source: .netease))
        if case .action(let value) = restored.sections[0].items[0].content { XCTAssertEqual(value, action) }
        else { XCTFail("action was downgraded") }
        var raw = try MJ.parse(data); raw["url"] = .string("https://invalid.example/executable")
        XCTAssertThrowsError(try MusicNativeHomeSnapshot.decode(raw.bytes, source: .netease))
    }
    func testSnapshotHasOnlyDisplaySchemaAndUsesDocumentByteBoundary() throws {
        let title = String(repeating: "a", count: 1000)
        let items = (1...650).map { id in
            MusicNativePublicBrowseItem(id: "playlist-\(id)", title: title,
                content: .collection(.init(platform: .netease, nativeID: String(id), title: title)))
        }
        let page = MusicNativePublicBrowseDiscovery(sections: [.init(id: "large", title: "", items: items)])
        let encoded = try MusicNativeHomeSnapshot.encode(page, source: .netease)
        XCTAssertGreaterThan(encoded.count, 400_000)
        XCTAssertLessThan(encoded.count, MusicNativeHomeSnapshot.maximumBytes)
        let decoded = try XCTUnwrap(MusicNativeHomeSnapshot.decode(encoded, source: .netease))
        XCTAssertEqual(decoded.sections.first?.items.count, 650)
        let root = try MJ.parse(encoded)
        XCTAssertEqual(Set(root.fields.map(\.0)), ["schema", "source", "sections"])
        XCTAssertEqual(root["schema"], .n(3))
        let document = MusicNativeHomeSnapshotDocument { throw MusicServiceError.unavailable }
        XCTAssertThrowsError(try document.write(Data(count: MusicNativeHomeSnapshot.maximumBytes + 1))) {
            XCTAssertEqual($0 as? MusicServiceError, .limit)
        }
    }
    func testImageQueriesSurviveNormalizationLibraryCleaningAndHomeSnapshotRestore() throws {
        let image = "http://p1.music.126.net/cover.jpg?param=800y800&quality=90&extra=a%2Fb"
        let expected = image.replacingOccurrences(of: "http:", with: "https:")
        var raw = MusicNativePublicBrowseFixture.song("41")
        raw["al"]["picUrl"] = .string(image)
        let track = try MusicNativePlatforms.browseWYSong(raw)
        let song = try MusicModel.normalize(track)
        XCTAssertEqual(song["cover_url"].string, expected)
        let cleaned = try MusicModel.cleanSong(song)
        XCTAssertEqual(cleaned["cover_url"].string, expected)
        XCTAssertEqual(cleaned["sources"].array[0]["musicInfo"]["img"].string, expected)
        let collection = MusicPublicBrowseCollection(platform: .netease, nativeID: "42", title: "歌单",
                                                     artwork: URL(string: image))
        let items: [MusicNativePublicBrowseItem] = [
            .init(id: "song", title: "歌曲", artwork: URL(string: image), content: .song(track)),
            .init(id: "playlist", title: "歌单", artwork: collection.artwork, content: .collection(collection))
        ]
        let page = MusicNativePublicBrowseDiscovery(sections: [.init(id: "s", title: "", items: items)])
        let encoded = try MusicNativeHomeSnapshot.encode(page, source: .netease)
        let restored = try XCTUnwrap(MusicNativeHomeSnapshot.decode(encoded, source: .netease))
        XCTAssertEqual(restored.sections[0].items.compactMap { $0.artwork?.absoluteString }, [expected, expected])
        guard case .song(let restoredTrack) = restored.sections[0].items[0].content,
              case .collection(let restoredCollection) = restored.sections[0].items[1].content else {
            return XCTFail("Content identities must survive snapshot restore")
        }
        XCTAssertEqual(try MJ.parse(restoredTrack.data)["img"].string, expected)
        XCTAssertEqual(restoredCollection.artwork?.absoluteString, expected)
        XCTAssertEqual(MusicModel.publicURL(.string("https://p1.music.126.net/a.jpg?token=synthetic")), .string(""))
        XCTAssertEqual(MusicModel.publicURL(.string("https://p1.music.126.net/a.jpg?%63ookie=synthetic")), .string(""))
        for query in ["MUSIC_U=synthetic", "MUSIC%5FA=synthetic", "__csrf=synthetic", "csrf_token=synthetic",
                      "uin=42", "qm_keyst=synthetic", "vkey=synthetic", "userid=42&t1=synthetic"] {
            let value = "https://p1.music.126.net/a.jpg?param=200y200&" + query
            XCTAssertEqual(MusicModel.publicURL(.string(value)), .string(""), query)
            var credential = MusicNativePublicBrowseFixture.song("41")
            credential["al"]["picUrl"] = .string(value)
            let cleaned = try MusicModel.normalize(MusicNativePlatforms.browseWYSong(credential))
            XCTAssertEqual(cleaned["cover_url"].string, "", query)
            XCTAssertFalse(cleaned["sources"].array[0]["musicInfo"].contains("img"), query)
        }
    }
    func testSnapshotDocumentRestoresIndependentCDNQueriesWithoutPersistingCredentials() throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString, isDirectory: true)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: false)
        defer { try? FileManager.default.removeItem(at: root) }
        let factory: @Sendable () throws -> DocumentStore = {
            try DocumentStore(baseURL: root, namespace: MusicNativeHomeSnapshotDocument.namespace)
        }
        let document = MusicNativeHomeSnapshotDocument(storeFactory: factory)
        let image = URL(string: "http://independent-cdn.invalid/cover.jpg?param=800y800&extra=a%2Fb")!
        let secret = URL(string: "https://independent-cdn.invalid/avatar.jpg?MUSIC_U=synthetic-private")!
        let collection = MusicPublicBrowseCollection(platform: .netease, nativeID: "42", title: "歌单", artwork: image)
        let page = MusicNativePublicBrowseDiscovery(sections: [.init(id: "s", title: "", items: [
            .init(id: "playlist", title: "歌单", artwork: image, content: .collection(collection)),
            .init(id: "private", title: "歌单", artwork: secret, content: .collection(collection))
        ])])
        try document.write(MusicNativeHomeSnapshot.encode(page, source: .netease))
        let reopened = MusicNativeHomeSnapshotDocument(storeFactory: factory)
        let data = try XCTUnwrap(reopened.read())
        XCTAssertFalse(String(decoding: data, as: UTF8.self).contains("synthetic-private"))
        let restored = try XCTUnwrap(MusicNativeHomeSnapshot.decode(data, source: .netease))
        let expected = "https://independent-cdn.invalid/cover.jpg?param=800y800&extra=a%2Fb"
        XCTAssertEqual(restored.sections[0].items[0].artwork?.absoluteString, expected)
        XCTAssertNil(restored.sections[0].items[1].artwork)
        guard case .collection(let restoredCollection) = restored.sections[0].items[0].content else {
            return XCTFail("Collection identity must survive document restore")
        }
        XCTAssertEqual(restoredCollection.artwork?.absoluteString, expected)
        try reopened.clear()
        XCTAssertNil(try document.read())
    }
    func testDailyRecommendationRowsKeepNormalizedAlbumArtwork() async throws {
        let account = HomeAccountFixture(), value = try await service(account)
        var raw = MusicNativePublicBrowseFixture.song("41")
        raw["al"]["picUrl"] = .string("http://p1.music.126.net/daily.jpg?param=300y300")
        await account.setDaily(.init(items: [try MusicNativePlatforms.browseWYSong(raw)]))
        let page = try await value.publicDiscovery(.daily(.netease), cursor: nil)
        let item = try XCTUnwrap(page.sections.first?.items.first)
        XCTAssertEqual(item.artwork?.absoluteString, "https://p1.music.126.net/daily.jpg?param=300y300")
        guard case .track(let track) = item.content else { return XCTFail("daily row must stay playable") }
        XCTAssertEqual(track.artwork, item.artwork)
        await value.close()
    }

    func testArtistActivityIsOnlySupplementedOnTheFirstHomePageAndEmptyWorksStayAbsent() async throws {
        let account = HomeAccountFixture(), value = try await service(account)
        let first = try await value.homePage(cursor: nil, refresh: true)
        let next = try await value.homePage(cursor: XCTUnwrap(first.cursor), refresh: false)
        XCTAssertEqual(next.sections.map(\.layout), [.songColumns])
        let calls = await account.calls
        XCTAssertEqual(calls.filter { if case .artistActivity = $0 { return true }; return false }.count, 1)
        await account.setActivity(Data(#"{"code":200,"data":{"newWorks":[],"hasMore":false}}"#.utf8))
        let empty = try await value.homePage(cursor: nil, refresh: true)
        XCTAssertEqual(empty.sections.map(\.layout), [.songColumns])
        await value.close()
    }

    func testExistingArtistSectionDoesNotTriggerAnotherActivityRead() async throws {
        let account = HomeAccountFixture(), value = try await service(account)
        await account.setHome(Data(#"""
        {"code":200,"data":{"blocks":[{"positionCode":"followed-artists","dslData":{
          "header":{"title":"关注的新作","action":"orpheus://nm/mainTab/select?tab=music&musicTabCode=artist-activity"},
          "items":[{"resourceType":"album","id":"42","title":"真实新作"}]}}]}}
        """#.utf8))
        let page = try await value.homePage(cursor: nil, refresh: true)
        XCTAssertEqual(page.sections.map(\.layout), [.artistUpdates])
        XCTAssertEqual(page.sections.first?.channel?.identity, "wy:artist-activity")
        let calls = await account.calls
        XCTAssertEqual(calls, [.home()])
        await value.close()
    }

    func testGuestFiltersAccountResourcesBeforeMetadataReadsButKeepsAnonymousFM() async throws {
        let account = HomeAccountFixture(loggedIn: false)
        let root = try MJ.parse(Data(#"""
        {"code":200,"data":{"blocks":[
          {"positionCode":"entries","dslData":{"items":[
            {"resourceType":"dailySongs","title":"账号推荐"},
            {"resourceType":"star","title":"收藏"},
            {"resourceType":"similarSong","title":"相似推荐","action":"orpheus://nm/play/similarFM?sourceType=song&sourceId=%5B41%5D"},
            {"resourceType":"dragon_ball","title":"会员","action":"orpheus://rnpage?component=rn-vip-portal"},
            {"resourceType":"fm","title":"漫游"}
          ]}},
          {"positionCode":"PAGE_RECOMMEND_MY_SHEET","dslData":{"items":[{"resourceType":"song","id":"999"}]}},
          {"positionCode":"restricted","dslData":{"header":{"title":"任意标题",
            "action":"orpheus://nm/mainTab/select?tab=music&musicTabCode=artist-activity"},
            "items":[{"resourceType":"song","id":"998"}]}},
          {"positionCode":"public","dslData":{"title":"公开歌曲","items":[{"resourceType":"song","id":"71"}]}}
        ]}}
        """#.utf8))
        let http = NativeMusicHTTPFixture([root,
            .obj([("code", .n(200)), ("songs", .array([MusicNativePublicBrowseFixture.song("71")]))])])
        let value = try await service(account, http: http)
        let page = try await value.homePage(cursor: nil, refresh: true)
        XCTAssertEqual(page.sections.map(\.layout), [.featureRail, .songColumns])
        let actions = page.sections[0].items.compactMap { item -> MusicHomeAction? in
            guard case .action(let action) = item.content else { return nil }
            return action
        }
        XCTAssertEqual(page.sections[0].items.count, 3)
        XCTAssertEqual(actions, [
            .heartbeat, .similar(sourceIDs: ["41"], sourceName: ""),
            .fm(mode: .default, submode: "", entry: nil)
        ], "valid account entrances remain visible; execution requests login")
        XCTAssertEqual(page.sections[1].items.count, 1)
        let calls = await account.calls
        XCTAssertTrue(calls.isEmpty, "Guest home must not read account recommendations or artist activity")
        let reads = await http.calls()
        XCTAssertEqual(reads.count, 2)
        XCTAssertEqual(reads[1].url.path, "/weapi/v3/song/detail")
        await value.close()
    }

    func testHomeSourceSelectionRejectsUnimplementedProvidersWithoutDiscardingValidCache() async throws {
        let account = HomeAccountFixture(), store = HomeSnapshotFixture(), value = try await service(account, store: store)
        let first = try await value.homePage(cursor: nil, refresh: true)
        let saved = store.read(), before = await account.calls
        let selection = try await value.homeSourceSelection()
        XCTAssertEqual(selection, .init(selected: .netease, revision: 0, available: [.netease, .kugou]))
        _ = try await value.selectHomeSource(.netease, ifRevision: 0)
        do { _ = try await value.selectHomeSource(.qq, ifRevision: 0); XCTFail("Unimplemented home source accepted") }
        catch { XCTAssertEqual(error as? MusicServiceError, .unsupported) }
        let cached = try await value.homePage(cursor: nil, refresh: false)
        XCTAssertEqual(cached.sections.map(\.id), first.sections.map(\.id))
        let after = await account.calls
        XCTAssertEqual(before, after); XCTAssertEqual(store.read(), saved)
        let data = try XCTUnwrap(saved)
        XCTAssertNil(try MusicNativeHomeSnapshot.decode(data, source: .qq))
        var root = try MJ.parse(data)
        var sections = root["sections"].array
        sections[1]["channel"]["platform"] = .string("tx")
        root["sections"] = .array(sections)
        XCTAssertThrowsError(try MusicNativeHomeSnapshot.decode(root.bytes, source: .netease))
        await value.close()
    }

    func testUnimplementedChannelSourceNeverFallsBackToNetease() async throws {
        let http = NativeMusicHTTPFixture([]), value = try await service(HomeAccountFixture(), http: http)
        for kind in [MusicPublicBrowseChannel.Kind.playlists, .rankings, .featured, .artistActivity, .vip] {
            do {
                _ = try await value.publicDiscovery(.channel(.init(id: "same-id", title: "频道", kind: kind,
                    platform: .qq), tag: ""), cursor: nil)
                XCTFail("Foreign channel routed to NetEase")
            } catch { XCTAssertEqual(error as? MusicServiceError, .unsupported) }
        }
        let calls = await http.calls(); XCTAssertTrue(calls.isEmpty)
        await value.close()
    }

    func testVIPRejectsNonNilCursorEvenWhenItEncodesAnEmptyFirstPosition() async throws {
        let account = HomeAccountFixture(), http = NativeMusicHTTPFixture([])
        let value = try await service(account, http: http)
        _ = try await value.homeSourceSelection()
        let binding = try await value.currentHomeBinding()
        let destination = MusicPublicBrowseDestination.channel(.init(id: "vip", title: "VIP", kind: .vip), tag: "")
        let revision = await value.homeSourceRevision, epoch = await value.epoch
        let key = MusicPublicBrowsePlatform.netease.rawValue + ":" + MusicNativePublicBrowseDiscovery.key(destination) +
            ":" + revision.uuidString + ":" + binding.revision.uuidString
        let position = try MusicNativePublicBrowseCursor.read(nil, key: key, epoch: epoch, now: NativeMusicFixture.date)
        let cursor = try position.encode(key: key, epoch: epoch, offset: 0)
        await assertMusicFailure(.invalidRequest) { _ = try await value.publicDiscovery(destination, cursor: cursor) }
        let payloads = await account.calls, requests = await http.calls()
        XCTAssertTrue(payloads.isEmpty); XCTAssertTrue(requests.isEmpty)
        await value.close()
    }

    func testActivityFailureKeepsLastSuccessAndLateRevokedActivityCannotPublish() async throws {
        let account = HomeAccountFixture(), store = HomeSnapshotFixture(), value = try await service(account, store: store)
        _ = try await value.homePage(cursor: nil, refresh: true)
        let saved = store.read()
        await account.failActivity(.networkOffline)
        do { _ = try await value.homePage(cursor: nil, refresh: true); XCTFail("Activity error hidden as guest success") }
        catch { XCTAssertEqual(error as? MusicServiceError, .networkOffline) }
        XCTAssertEqual(store.read(), saved)
        await value.close()

        let replacement = HomeAccountFixture(), otherStore = HomeSnapshotFixture(), gate = NativeMusicGate()
        await replacement.suspendActivity(gate)
        let other = try await service(replacement, store: otherStore)
        let request = Task { try await other.homePage(cursor: nil, refresh: true) }
        await gate.started(); await replacement.changeAccount(); await gate.release()
        do { _ = try await request.value; XCTFail("Revoked activity published") }
        catch { XCTAssertTrue([MusicServiceError.staleAccess, .staleResult].contains(error as? MusicServiceError ?? .unavailable)) }
        XCTAssertNil(otherStore.read())
        let calls = await replacement.calls
        XCTAssertEqual(calls, [.home(), .artistActivity()])
        await other.close()
    }

    func testGuestFilteredBlocksAdvanceWithoutHydratingTheirPrivateContents() async throws {
        let account = HomeAccountFixture(loggedIn: false)
        let http = NativeMusicHTTPFixture([
            try MJ.parse(Data(#"""
            {"code":200,"data":{"blocks":[{"positionCode":"PAGE_RECOMMEND_MY_SHEET","dslData":"not-public-json"}],
              "hasMore":true,"cursor":"1"}}
            """#.utf8)),
            try MJ.parse(Data(#"""
            {"code":200,"data":{"blocks":[{"positionCode":"public","dslData":{"title":"歌单",
              "items":[{"resourceType":"playlist","id":"41","title":"公开歌单",
                "coverImg":"https://p1.music.126.net/public.jpg"}]}}],"hasMore":false}}
            """#.utf8)),
        ])
        let value = try await service(account, http: http)
        let first = try await value.homePage(cursor: nil, refresh: true)
        XCTAssertTrue(first.sections.isEmpty); XCTAssertEqual(first.consumed, 1)
        let second = try await value.homePage(cursor: XCTUnwrap(first.cursor), refresh: false)
        XCTAssertEqual(second.sections.first?.items.first?.title, "公开歌单")
        XCTAssertNil(second.cursor)
        let calls = await account.calls; XCTAssertTrue(calls.isEmpty)
        let reads = await http.calls(); XCTAssertEqual(reads.count, 2)
        await value.close()
    }

    func testSongColumnSelectionReplacesAllSevenTracksInOneQueueTransaction() async throws {
        let account = HomeAccountFixture(), document = NativeMusicMemoryDocument(), value = try await service(account, document: document)
        let ids = (1...7).map(String.init)
        await account.setHome(MJ.obj([("code", .n(200)), ("data", .obj([("blocks", .array([
            .obj([("positionCode", .string("songs")), ("dslData", .obj([
                ("title", .string("完整栏目")), ("items", .array(ids.map {
                    .obj([("resourceType", .string("song")), ("id", .string($0))])
                }))
            ]))])
        ]))]))]).bytes)
        let page = try await value.homePage(cursor: nil, refresh: true)
        let section = try XCTUnwrap(page.sections.first)
        XCTAssertEqual(section.layout, .songColumns)
        let tracks = section.items.compactMap { if case .track(let value) = $0.content { return value }; return nil }
        let reads = await account.calls, writes = document.snapshot().2
        let state = try await value.replaceDisplayedQueue(tracks, index: 5, origin: .section(title: section.title),
            expectedRevision: 0, intent: .init())
        let queue = try await value.queue()
        XCTAssertEqual(state.total, 7)
        XCTAssertEqual(queue.entries.map { $0.track.sources[0].nativeId }, ids)
        XCTAssertEqual(queue.currentId, queue.entries[5].id)
        XCTAssertEqual(document.snapshot().2, writes + 1)
        let after = await account.calls; XCTAssertEqual(after, reads)
        await value.close()
    }
}
