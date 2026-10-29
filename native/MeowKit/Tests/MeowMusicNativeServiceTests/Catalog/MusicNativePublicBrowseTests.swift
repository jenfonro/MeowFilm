import XCTest
import CommonCrypto
import MeowMusicFeature
@testable import MeowMusicNativeService

enum MusicNativePublicBrowseFixture {
    static let id = "9007199254740993"
    static let collection = MusicPublicBrowseCollection(platform: .netease, nativeID: "41", title: "Synthetic playlist")
    static func song(_ id: String = MusicNativePublicBrowseFixture.id) -> MJ {
        .obj([("id", .string(id)), ("name", .string("Synthetic song")), ("ar", .array([.obj([("name", .string("Artist"))])])),
            ("al", .obj([("id", .n(7)), ("name", .string("Album"))])), ("dt", .n(180000)),
            ("h", .obj([("size", .n(1024))]))])
    }
    static func head(ids: [String] = [MusicNativePublicBrowseFixture.id], version: Int = 1) -> MJ {
        .obj([("code", .n(200)), ("playlist", .obj([("id", .n(41)), ("name", .string("Synthetic playlist")),
            ("creator", .obj([("userId", .n(99)), ("nickname", .string("Creator"))])),
            ("trackCount", .n(ids.count)), ("trackUpdateTime", .n(version)),
            ("trackIds", .array(ids.map { .obj([("id", .string($0))]) }))]))])
    }
    static func decryptEAPI(_ request: MusicNativeHTTPRequest) throws -> (String, MJ) {
        let body = try XCTUnwrap(request.body)
        let chars = Array(String(decoding: body, as: UTF8.self).dropFirst(7))
        let bytes = stride(from: 0, to: chars.count, by: 2).map { UInt8(String(chars[$0..<$0+2]), radix: 16)! }
        let key = Array("e82ckenh8dichen8".utf8)
        var output = [UInt8](repeating: 0, count: bytes.count + 16), written = 0
        let capacity = output.count
        XCTAssertEqual(CCCrypt(CCOperation(kCCDecrypt), CCAlgorithm(kCCAlgorithmAES),
            CCOptions(kCCOptionPKCS7Padding | kCCOptionECBMode), key, key.count, nil, bytes, bytes.count,
            &output, capacity, &written), CCCryptorStatus(kCCSuccess))
        let parts = String(decoding: output.prefix(written), as: UTF8.self).components(separatedBy: "-36cd479b6b5-")
        XCTAssertEqual(parts.count, 3)
        return (parts[0], try MJ.parse(Data(parts[1].utf8)))
    }
}

final class MusicNativePublicBrowseTests: XCTestCase {
    func testWYPlaylistSearchUsesNativeType1000AndDoesNotReadDocumentOrScripts() async throws {
        let row = MJ.obj([("id", .string(MusicNativePublicBrowseFixture.id)), ("name", .string("Synthetic")),
                          ("trackCount", .n(1)), ("creator", .obj([("nickname", .string("Creator"))]))])
        let http = NativeMusicHTTPFixture([.obj([("code", .n(200)), ("result", .obj([
            ("playlistCount", .n(1)), ("playlists", .array([row]))]))])])
        let store = NativeMusicMemoryDocument(), service = try await NativeMusicFixture.service(store, http: http)
        let page = try await service.publicPlaylists(platform: .netease, query: "test", cursor: nil)
        XCTAssertEqual(page.items.first?.nativeID, MusicNativePublicBrowseFixture.id)
        XCTAssertNil(page.cursor); XCTAssertEqual(store.snapshot().1, 0); XCTAssertEqual(store.snapshot().2, 0)
        let calls = await http.calls(), decoded = try MusicNativePublicBrowseFixture.decryptEAPI(calls[0])
        XCTAssertEqual(decoded.0, "/api/cloudsearch/pc")
        XCTAssertEqual(decoded.1["type"], .n(1000)); XCTAssertEqual(decoded.1["offset"], .n(0))
        XCTAssertFalse((calls[0].headers["Cookie"] ?? "").contains("MUSIC_U"))
    }
    func testWYDetailFetchesFullMetadataInNativeOrderAndPreservesLargeIDs() async throws {
        let ids = ["17", MusicNativePublicBrowseFixture.id], head = MusicNativePublicBrowseFixture.head(ids: ids)
        let songs = ids.reversed().map { MusicNativePublicBrowseFixture.song($0) }
        let http = NativeMusicHTTPFixture([head, .obj([("code", .n(200)), ("songs", .array(songs))])])
        let service = try await NativeMusicFixture.service(http: http)
        let page = try await service.publicDetail(MusicNativePublicBrowseFixture.collection, cursor: nil)
        XCTAssertEqual(page.tracks.map { $0.sources[0].nativeId }, ids)
        XCTAssertEqual(page.tracks.map(\.duration), [180, 180]); XCTAssertEqual(page.snapshot.count, 64)
        let calls = await http.calls()
        XCTAssertEqual(calls.map(\.url.path), ["/eapi/v6/playlist/detail", "/weapi/v3/song/detail"])
        XCTAssertEqual(calls[1].url.host, "music.163.com")
        XCTAssertNil(calls[1].headers["Cookie"])
    }
    func testWYMissingSongIsVisibleStatusAndChangedCompleteSnapshotCannotCommit() async throws {
        let head = MusicNativePublicBrowseFixture.head()
        let missing = NativeMusicHTTPFixture([head, .obj([("code", .n(200)), ("songs", .array([]))])])
        let service = try await NativeMusicFixture.service(http: missing)
        let unavailable = try await service.publicDetail(MusicNativePublicBrowseFixture.collection, cursor: nil)
        XCTAssertTrue(unavailable.tracks.isEmpty); XCTAssertNotNil(unavailable.notice)
        XCTAssertEqual(unavailable.total, 1); XCTAssertEqual(unavailable.consumed, 1)
        XCTAssertNil(unavailable.cursor)
        let changed = NativeMusicHTTPFixture([head, .obj([("code", .n(200)), ("songs", .array([MusicNativePublicBrowseFixture.song()]))]),
            MusicNativePublicBrowseFixture.head(version: 2)])
        let second = try await NativeMusicFixture.service(http: changed)
        let displayed = try await second.publicDetail(MusicNativePublicBrowseFixture.collection, cursor: nil)
        do {
            _ = try await second.openPublicQueue(.init(collection: displayed.collection, snapshot: displayed.snapshot))
            XCTFail("changed snapshot")
        }
        catch { XCTAssertEqual(error as? MusicServiceError, .staleResult) }
    }
    func testWYEmptyVisiblePageAdvancesOnlyOneRawPageAndReusesMetadataForExplicitQueue() async throws {
        let ids = (1...31).map(String.init), head = MusicNativePublicBrowseFixture.head(ids: ids)
        let http = NativeMusicHTTPFixture([head, .obj([("code", .n(200)), ("songs", .array([]))]),
            .obj([("code", .n(200)), ("songs", .array([MusicNativePublicBrowseFixture.song("31")]))]), head])
        let service = try await NativeMusicFixture.service(http: http)
        let first = try await service.publicDetail(MusicNativePublicBrowseFixture.collection, cursor: nil)
        XCTAssertTrue(first.tracks.isEmpty); XCTAssertEqual(first.consumed, 30)
        XCTAssertEqual(first.total, 31); XCTAssertNotNil(first.notice); XCTAssertNotNil(first.cursor)
        var calls = await http.calls(); XCTAssertEqual(calls.count, 2, "No automatic scan of unavailable pages")
        let second = try await service.publicDetail(first.collection, cursor: first.cursor)
        XCTAssertEqual(second.tracks.map { $0.sources[0].nativeId }, ["31"]); XCTAssertNil(second.cursor)
        let source = try await service.openPublicQueue(.init(collection: first.collection, snapshot: first.snapshot))
        XCTAssertEqual(source.context.unavailableCount, 30)
        let rows = try await source.completeTracks(intent: MusicQueueAuthorization())
        XCTAssertEqual(rows.count, 1)
        calls = await http.calls()
        XCTAssertEqual(calls.map(\.url.path), ["/eapi/v6/playlist/detail", "/weapi/v3/song/detail",
                                             "/weapi/v3/song/detail", "/eapi/v6/playlist/detail"])
        await source.close()
    }
    func testWYAllUnavailableQueueIsNotInventedPermissionErrorAndRefreshInvalidatesCaches() async throws {
        let head = MusicNativePublicBrowseFixture.head()
        let songs = MJ.obj([("code", .n(200)), ("songs", .array([]))])
        let http = NativeMusicHTTPFixture([head, songs, head, head, songs])
        let service = try await NativeMusicFixture.service(http: http)
        let page = try await service.publicDetail(MusicNativePublicBrowseFixture.collection, cursor: nil)
        do {
            _ = try await service.publicQueueTracks(.init(collection: page.collection, snapshot: page.snapshot))
            XCTFail("Invisible collection was admitted as a queue")
        } catch { XCTAssertEqual(error as? MusicServiceError, .contentUnavailable) }
        await service.refreshPublicCollection(page.collection)
        _ = try await service.publicDetail(page.collection, cursor: nil)
        let calls = await http.calls()
        XCTAssertEqual(calls.count, 5)
    }
    func testCursorBindsQueryProtectionEpochAndExpiryBeforeHTTP() async throws {
        let own = UUID(), now = NativeMusicFixture.date
        let first = try MusicNativePublicBrowseCursor.read(nil, key: "a", epoch: own, now: now)
        let token = try first.encode(key: "a", epoch: own, offset: 30)
        XCTAssertEqual(try MusicNativePublicBrowseCursor.read(token, key: "a", epoch: own, now: now).offset, 30)
        XCTAssertThrowsError(try MusicNativePublicBrowseCursor.read(token, key: "b", epoch: own, now: now))
        XCTAssertThrowsError(try MusicNativePublicBrowseCursor.read(token, key: "a", epoch: UUID(), now: now))
        XCTAssertThrowsError(try MusicNativePublicBrowseCursor.read(token, key: "a", epoch: own, now: now.addingTimeInterval(600)))
    }
    func testRefreshRevokesPublishedSourceAndProtectionClearsAllRegistrations() async throws {
        let head = MusicNativePublicBrowseFixture.head()
        let songs = MJ.obj([("code", .n(200)), ("songs", .array([MusicNativePublicBrowseFixture.song()]))])
        let http = NativeMusicHTTPFixture([head, songs, head, head, songs, head])
        let service = try await NativeMusicFixture.service(http: http)
        let page = try await service.publicDetail(MusicNativePublicBrowseFixture.collection, cursor: nil)
        let source = try await service.openPublicQueue(.init(collection: page.collection, snapshot: page.snapshot))
        _ = try await source.completeTracks(intent: MusicQueueAuthorization())
        let registered = await service.publicQueueSources.count
        XCTAssertEqual(registered, 1)
        await service.refreshPublicCollection(page.collection)
        XCTAssertFalse(source.authorization.isActive)
        let retired = await service.publicQueueSources.count
        XCTAssertEqual(retired, 0)
        do { _ = try await source.completeTracks(intent: MusicQueueAuthorization()); XCTFail("Refresh retained old authority") }
        catch { XCTAssertEqual(error as? MusicServiceError, .staleAccess) }
        await source.close()

        let fresh = try await service.publicDetail(page.collection, cursor: nil)
        let reopened = try await service.openPublicQueue(.init(collection: fresh.collection, snapshot: fresh.snapshot))
        try await service.setAccess(available: false, generation: 2)
        XCTAssertFalse(reopened.authorization.isActive)
        let cleared = await service.publicQueueSources.count
        XCTAssertEqual(cleared, 0, "Protection clears registrations, not only authorization flags")
        await reopened.close(); await service.close()
    }
    func testKGSearchRejectsSpecialIDFallbackAndKeepsNativeGID() async throws {
        func response(_ id: MJ) -> MJ {
            .obj([("status", .n(1)), ("error_code", .n(0)), ("data", .obj([("total", .n(1)), ("lists", .array([
                .obj([("gid", id), ("specialid", .n(9)), ("specialname", .string("Synthetic")), ("songcount", .n(1))])
            ]))]))])
        }
        let http = NativeMusicHTTPFixture([response(.string("synthetic-global-id")), response(.null)])
        let service = try await NativeMusicFixture.service(http: http)
        let page = try await service.publicPlaylists(platform: .kugou, query: "test", cursor: nil)
        XCTAssertEqual(page.items[0].nativeID, "synthetic-global-id")
        do { _ = try await service.publicPlaylists(platform: .kugou, query: "test", cursor: nil); XCTFail("specialid is not a global ID") }
        catch { XCTAssertEqual(error as? MusicServiceError, .upstreamInvalid) }
        let calls = await http.calls()
        XCTAssertEqual(calls[0].url.path, "/v1/search/special")
        XCTAssertEqual(calls[0].headers["x-router"], "complexsearch.kugou.com")
    }
    func testAdditionalRoutesAreExactAndDoNotOpenGenericAPIOrLegacyServices() {
        func allowed(_ url: String, method: String = "POST") -> Bool {
            MusicNativePublicBrowseRoutes.allows(.init(url: URL(string: url)!, method: method))
        }
        XCTAssertTrue(allowed("https://music.163.com/weapi/v1/album/42"))
        XCTAssertTrue(allowed("https://interfacepc.music.163.com/eapi/link/page/rcmd/resource/show"))
        XCTAssertTrue(allowed("https://gateway.kugou.com/pubsongs/v2/get_other_list_file_nofilt", method: "GET"))
        XCTAssertFalse(allowed("https://music.163.com/weapi/playlist/create"))
        XCTAssertFalse(allowed("https://music.163.com/weapi/v1/album/42?url=https://other.invalid"))
        XCTAssertFalse(allowed("https://music.163.com.evil.invalid/weapi/v1/album/42"))
        XCTAssertFalse(allowed("http://music.163.com/weapi/v1/album/42"))
        XCTAssertFalse(allowed("http://127.0.0.1:3000/album"))
    }
    func testAlbumReturnsFullNativeTracksAndRejectsIncorrectIdentity() async throws {
        let album = MJ.obj([("id", .n(4)), ("name", .string("Synthetic album")), ("size", .n(1))])
        let http = NativeMusicHTTPFixture([.obj([("code", .n(200)), ("album", album), ("songs", .array([MusicNativePublicBrowseFixture.song()]))])])
        let service = try await NativeMusicFixture.service(http: http)
        let collection = MusicPublicBrowseCollection(platform: .netease, nativeID: "4", kind: .album, title: "Synthetic album")
        let detail = try await service.publicDetail(collection, cursor: nil)
        XCTAssertEqual(detail.collection.kind, .album); XCTAssertEqual(detail.tracks.count, 1)
        XCTAssertEqual(detail.tracks[0].sources[0].nativeId, MusicNativePublicBrowseFixture.id)
        let calls = await http.calls(); XCTAssertEqual(calls[0].url.path, "/weapi/v1/album/4")
    }
}
