import XCTest
import MeowMusicFeature
@testable import MeowMusicNativeService

private enum MusicSearchMemberFixture {
    static let headPath = "/eapi/v6/playlist/detail"
    static let songsPath = "/weapi/v3/song/detail"
    static let collection = MusicNativePublicBrowseFixture.collection
    static func song(_ id: String, title: String? = nil) -> MJ {
        var row = MusicNativePublicBrowseFixture.song(id)
        row["name"] = .string(title ?? "Song \(id)")
        var album = row["al"]; album["picUrl"] = .string("https://p1.music.126.net/synthetic.jpg")
        row["al"] = album
        return row
    }
    static func head(ids: [String], tracks: [MJ] = []) -> MJ {
        var root = MusicNativePublicBrowseFixture.head(ids: ids)
        var playlist = root["playlist"]; playlist["tracks"] = .array(tracks)
        root["playlist"] = playlist
        return root
    }
    static func songs(_ rows: [MJ]) -> MJ { .obj([("code", .n(200)), ("songs", .array(rows))]) }
}

final class MusicNativeSearchMemberTests: XCTestCase {
    private typealias Fixture = MusicSearchMemberFixture

    func testHeadReturnedSongsNeedNoSupplementAndSurviveHeadCacheExpiry() async throws {
        let ids = ["17", MusicNativePublicBrowseFixture.id, "23"], clock = NativeMusicFixtureClock()
        let http = MusicSearchDiscoveryHTTP([
            Fixture.headPath: [
                Fixture.head(ids: ids, tracks: ids.reversed().map { Fixture.song($0) }),
                Fixture.head(ids: ids)
            ]
        ])
        let store = NativeMusicMemoryDocument()
        let owner = try await NativeMusicFixture.service(store, http: http, clock: { clock.now() })
        let first = try await owner.publicDetail(Fixture.collection, cursor: nil)
        XCTAssertEqual(first.members.map(\.nativeID), ids)
        XCTAssertEqual(first.members.map(\.sourcePosition), [0, 1, 2])
        XCTAssertEqual(first.tracks, first.members.map(\.track))
        XCTAssertEqual(first.tracks.map(\.duration), [180, 180, 180])
        XCTAssertTrue(first.tracks.allSatisfy { $0.artist == "Artist" && $0.album == "Album" && $0.artwork != nil })
        XCTAssertEqual(first.consumed, 3); XCTAssertEqual(first.total, 3); XCTAssertNil(first.cursor)
        var calls = await http.calls(); XCTAssertEqual(calls.map(\.url.path), [Fixture.headPath])

        clock.advance(45)
        let freshHead = try await owner.publicDetail(first.collection, cursor: nil)
        XCTAssertEqual(freshHead.members.map(\.nativeID), ids)
        XCTAssertEqual(freshHead.snapshot, first.snapshot)
        calls = await http.calls(); XCTAssertEqual(calls.map(\.url.path), [Fixture.headPath, Fixture.headPath])
        XCTAssertEqual(store.snapshot().1, 0); XCTAssertEqual(store.snapshot().2, 0)
        await owner.close()
    }

    func testMissingDisplayFieldsUseOneBatchAndKeepReturnedFieldsInTheNativeCache() async throws {
        let ids = ["17", MusicNativePublicBrowseFixture.id, "23", "24"], clock = NativeMusicFixtureClock()
        var missingArtist = Fixture.song(ids[1]); missingArtist.remove("ar")
        var missingTitle = Fixture.song(ids[3]); missingTitle.remove("name")
        let returned = [Fixture.song(ids[2], title: "Head C"), missingArtist,
                        Fixture.song(ids[0], title: "Head A"), missingTitle]
        let http = MusicSearchDiscoveryHTTP([
            Fixture.headPath: [Fixture.head(ids: ids, tracks: returned), Fixture.head(ids: ids)],
            Fixture.songsPath: [Fixture.songs([
                Fixture.song(ids[3], title: "Filled D"), Fixture.song(ids[1], title: "Filled B")
            ])]
        ])
        let owner = try await NativeMusicFixture.service(http: http, clock: { clock.now() })
        let first = try await owner.publicDetail(Fixture.collection, cursor: nil)
        XCTAssertEqual(first.members.map(\.nativeID), ids)
        XCTAssertEqual(first.members.map(\.sourcePosition), [0, 1, 2, 3])
        XCTAssertEqual(first.tracks.map(\.title), ["Head A", "Filled B", "Head C", "Filled D"])
        var calls = await http.calls(); XCTAssertEqual(calls.map(\.url.path), [Fixture.headPath, Fixture.songsPath])

        clock.advance(45)
        let freshHead = try await owner.publicDetail(first.collection, cursor: nil)
        XCTAssertEqual(freshHead.members.map(\.nativeID), ids)
        XCTAssertEqual(freshHead.tracks.map(\.title), first.tracks.map(\.title))
        calls = await http.calls()
        XCTAssertEqual(calls.filter { $0.url.path == Fixture.headPath }.count, 2)
        XCTAssertEqual(calls.filter { $0.url.path == Fixture.songsPath }.count, 1)
        await owner.close()
    }

    func testForeignOrDuplicateHeadSongIDsAreRejectedWithoutSupplementOrHandles() async throws {
        for tracks in [[Fixture.song("999")], [Fixture.song("17"), Fixture.song("17")]] {
            let http = MusicSearchDiscoveryHTTP([Fixture.headPath: [Fixture.head(ids: ["17"], tracks: tracks)]])
            let owner = try await NativeMusicFixture.service(http: http)
            do { _ = try await owner.publicDetail(Fixture.collection, cursor: nil); XCTFail("Invalid member identity was accepted") }
            catch { XCTAssertEqual(error as? MusicServiceError, .targetChanged) }
            let calls = await http.calls(); XCTAssertEqual(calls.map(\.url.path), [Fixture.headPath])
            let handles = await owner.handles.count; XCTAssertEqual(handles, 0)
            await owner.close()
        }
    }

    func testMalformedPresentDisplayFieldIsRejectedInsteadOfSupplemented() async throws {
        var song = Fixture.song("17"); song["dt"] = .string("invalid")
        let http = MusicSearchDiscoveryHTTP([Fixture.headPath: [Fixture.head(ids: ["17"], tracks: [song])]])
        let owner = try await NativeMusicFixture.service(http: http)
        do { _ = try await owner.publicDetail(Fixture.collection, cursor: nil); XCTFail("Malformed display field was accepted") }
        catch { XCTAssertEqual(error as? MusicServiceError, .upstreamInvalid) }
        let calls = await http.calls(); XCTAssertEqual(calls.map(\.url.path), [Fixture.headPath])
        let handles = await owner.handles.count; XCTAssertEqual(handles, 0)
        await owner.close()
    }

    func testSupplementCannotReturnAnotherNativeIDOrRegisterAPartialPage() async throws {
        let http = MusicSearchDiscoveryHTTP([
            Fixture.headPath: [Fixture.head(ids: ["17", "18"], tracks: [Fixture.song("17")])],
            Fixture.songsPath: [Fixture.songs([Fixture.song("999")])]
        ])
        let owner = try await NativeMusicFixture.service(http: http)
        do { _ = try await owner.publicDetail(Fixture.collection, cursor: nil); XCTFail("Foreign supplement identity was accepted") }
        catch { XCTAssertEqual(error as? MusicServiceError, .targetChanged) }
        let calls = await http.calls(); XCTAssertEqual(calls.map(\.url.path), [Fixture.headPath, Fixture.songsPath])
        let handles = await owner.handles.count; XCTAssertEqual(handles, 0)
        await owner.close()
    }

    func testMissingSongsKeepAbsolutePositionsAndDoNotFillTheFirst20FromLaterRanks() async throws {
        let ids = (1...35).map(String.init)
        let firstIDs = Array(ids.prefix(30)).filter { $0 != "2" }
        let secondIDs = Array(ids.dropFirst(30)).filter { $0 != "32" }
        let http = MusicSearchDiscoveryHTTP([
            Fixture.headPath: [Fixture.head(ids: ids)],
            Fixture.songsPath: [
                Fixture.songs(firstIDs.reversed().map { Fixture.song($0) }),
                Fixture.songs(secondIDs.reversed().map { Fixture.song($0) })
            ]
        ])
        let owner = try await NativeMusicFixture.service(http: http)
        let first = try await owner.publicDetail(Fixture.collection, cursor: nil)
        XCTAssertEqual(first.members.map(\.nativeID), firstIDs)
        XCTAssertEqual(first.members.map(\.sourcePosition), Array(0..<30).filter { $0 != 1 })
        XCTAssertEqual(first.tracks.map { $0.sources[0].nativeId }, firstIDs)
        XCTAssertEqual(first.consumed, 30); XCTAssertEqual(first.total, 35)
        XCTAssertNotNil(first.notice); XCTAssertNotNil(first.cursor)
        let preview = first.members.filter { $0.sourcePosition < 20 }
        XCTAssertEqual(preview.map(\.nativeID), Array(ids.prefix(20)).filter { $0 != "2" })
        XCTAssertEqual(preview.count, 19)
        XCTAssertEqual(preview.map(\.sourcePosition), Array(0..<20).filter { $0 != 1 })
        var calls = await http.calls(); XCTAssertEqual(calls.count, 2, "The first page must not scan later positions")

        let cursor = try XCTUnwrap(first.cursor)
        let second = try await owner.publicDetail(first.collection, cursor: cursor)
        XCTAssertEqual(second.members.map(\.nativeID), secondIDs)
        XCTAssertEqual(second.members.map(\.sourcePosition), [30, 32, 33, 34])
        XCTAssertEqual(second.consumed, 5); XCTAssertEqual(second.total, 35); XCTAssertNil(second.cursor)
        XCTAssertEqual(second.snapshot, first.snapshot)
        calls = await http.calls()
        XCTAssertEqual(calls.map(\.url.path), [Fixture.headPath, Fixture.songsPath, Fixture.songsPath])
        await owner.close()
    }

    func testEntirelyUnavailablePageAdvancesOnlyItsRawCursorWithoutPlaceholders() async throws {
        let ids = (1...31).map(String.init)
        let http = MusicSearchDiscoveryHTTP([
            Fixture.headPath: [Fixture.head(ids: ids)],
            Fixture.songsPath: [Fixture.songs([]), Fixture.songs([Fixture.song("31")])]
        ])
        let owner = try await NativeMusicFixture.service(http: http)
        let first = try await owner.publicDetail(Fixture.collection, cursor: nil)
        XCTAssertTrue(first.members.isEmpty); XCTAssertTrue(first.tracks.isEmpty)
        XCTAssertEqual(first.consumed, 30); XCTAssertEqual(first.total, 31); XCTAssertNotNil(first.notice)
        var calls = await http.calls(); XCTAssertEqual(calls.count, 2)
        let handles = await owner.handles.count; XCTAssertEqual(handles, 0)
        let cursor = try XCTUnwrap(first.cursor)
        let second = try await owner.publicDetail(first.collection, cursor: cursor)
        XCTAssertEqual(second.members.map(\.nativeID), ["31"])
        XCTAssertEqual(second.members.map(\.sourcePosition), [30])
        XCTAssertEqual(second.consumed, 1); XCTAssertNil(second.cursor)
        calls = await http.calls(); XCTAssertEqual(calls.count, 3)
        await owner.close()
    }

    func testSearchAndDetailReuseTheSameNative30PositionPage() async throws {
        let ids = (1...31).map(String.init)
        let hotID = MusicNativeSearchDiscoveryParser.hotChartID
        let http = MusicSearchDiscoveryHTTP([
            "/eapi/search/pc/chart/list": [.obj([("code", .n(200)), ("data", .obj([
                ("tabInfo", .obj([("list", .array([
                    .obj([("id", .string(hotID)), ("name", .string("热搜榜")),
                          ("needPlay", .bool(true))])
                ]))])),
                ("items", .obj([(hotID, .array([
                    .obj([("searchWord", .string("Keyword"))])
                ]))]))
            ]))])],
            "/eapi/toplist": [.obj([("code", .n(200)), ("list", .array([
                .obj([("id", .n(41)), ("name", .string("Synthetic playlist")), ("trackCount", .n(ids.count))])
            ]))])],
            Fixture.headPath: [Fixture.head(ids: ids)],
            Fixture.songsPath: [Fixture.songs(ids.prefix(30).map { Fixture.song($0) })]
        ])
        let store = NativeMusicMemoryDocument(), owner = try await NativeMusicFixture.service(store, http: http)
        let summary = try await owner.searchDiscovery()
        var calls = await http.calls(); XCTAssertEqual(calls.count, 2)
        let collection = try XCTUnwrap(summary.charts.first).collection
        let searchPage = try await owner.publicDetail(collection, cursor: nil)
        XCTAssertEqual(searchPage.members.map(\.sourcePosition), Array(0..<30))
        XCTAssertEqual(searchPage.consumed, 30); XCTAssertNotNil(searchPage.cursor)
        calls = await http.calls(); XCTAssertEqual(calls.count, 4)

        let detailPage = try await owner.publicDetail(searchPage.collection, cursor: nil)
        XCTAssertEqual(detailPage.snapshot, searchPage.snapshot)
        XCTAssertEqual(detailPage.members.map(\.nativeID), searchPage.members.map(\.nativeID))
        XCTAssertEqual(detailPage.members.map(\.sourcePosition), searchPage.members.map(\.sourcePosition))
        XCTAssertTrue(zip(searchPage.tracks, detailPage.tracks).allSatisfy { $0.0.ref != $0.1.ref })
        _ = try await owner.searchDiscovery()
        calls = await http.calls(); XCTAssertEqual(calls.count, 4)
        XCTAssertEqual(calls.filter { $0.url.path == Fixture.headPath }.count, 1)
        XCTAssertEqual(calls.filter { $0.url.path == Fixture.songsPath }.count, 1)
        XCTAssertEqual(store.snapshot().1, 0); XCTAssertEqual(store.snapshot().2, 0)
        await owner.close()
    }

    func testHeadAndNativeSongBatchRetainTheir45And120SecondCacheBoundaries() async throws {
        let clock = NativeMusicFixtureClock(), head = Fixture.head(ids: ["17"])
        let http = MusicSearchDiscoveryHTTP([
            Fixture.headPath: [head, head, head],
            Fixture.songsPath: [
                Fixture.songs([Fixture.song("17", title: "Old title")]),
                Fixture.songs([Fixture.song("17", title: "New title")])
            ]
        ])
        let owner = try await NativeMusicFixture.service(http: http, clock: { clock.now() })
        _ = try await owner.publicDetail(Fixture.collection, cursor: nil)
        clock.advance(44)
        _ = try await owner.publicDetail(Fixture.collection, cursor: nil)
        var calls = await http.calls(); XCTAssertEqual(calls.count, 2)

        clock.advance(1)
        let freshHead = try await owner.publicDetail(Fixture.collection, cursor: nil)
        XCTAssertEqual(freshHead.tracks.map(\.title), ["Old title"])
        calls = await http.calls()
        XCTAssertEqual(calls.filter { $0.url.path == Fixture.headPath }.count, 2)
        XCTAssertEqual(calls.filter { $0.url.path == Fixture.songsPath }.count, 1)

        clock.advance(75)
        let expiredBatch = try await owner.publicDetail(Fixture.collection, cursor: nil)
        XCTAssertEqual(expiredBatch.tracks.map(\.title), ["New title"])
        calls = await http.calls()
        XCTAssertEqual(calls.filter { $0.url.path == Fixture.headPath }.count, 3)
        XCTAssertEqual(calls.filter { $0.url.path == Fixture.songsPath }.count, 2)
        await owner.close()
    }

    func testCollectionRefreshInvalidatesNativeMetadataEvenWhenSnapshotIDsAreUnchanged() async throws {
        let head = Fixture.head(ids: ["17"])
        let http = MusicSearchDiscoveryHTTP([
            Fixture.headPath: [head, head],
            Fixture.songsPath: [
                Fixture.songs([Fixture.song("17", title: "Old title")]),
                Fixture.songs([Fixture.song("17", title: "New title")])
            ]
        ])
        let owner = try await NativeMusicFixture.service(http: http)
        let first = try await owner.publicDetail(Fixture.collection, cursor: nil)
        await owner.refreshPublicCollection(first.collection)
        let refreshed = try await owner.publicDetail(first.collection, cursor: nil)
        XCTAssertEqual(refreshed.snapshot, first.snapshot)
        XCTAssertEqual(refreshed.tracks.map(\.title), ["New title"])
        let calls = await http.calls(); XCTAssertEqual(calls.count, 4)
        await owner.close()
    }

    func testAccessGenerationInvalidatesMemberMetadataAndIssuedHandles() async throws {
        let head = Fixture.head(ids: ["17"])
        let http = MusicSearchDiscoveryHTTP([
            Fixture.headPath: [head, head],
            Fixture.songsPath: [
                Fixture.songs([Fixture.song("17", title: "Old title")]),
                Fixture.songs([Fixture.song("17", title: "New title")])
            ]
        ])
        let owner = try await NativeMusicFixture.service(http: http)
        let first = try await owner.publicDetail(Fixture.collection, cursor: nil)
        try await owner.setAccess(available: false, generation: 2)
        let handles = await owner.handles.count; XCTAssertEqual(handles, 0)
        do { _ = try await owner.publicDetail(first.collection, cursor: nil); XCTFail("Locked member read was admitted") }
        catch { XCTAssertEqual(error as? MusicServiceError, .accessUnavailable) }
        var calls = await http.calls(); XCTAssertEqual(calls.count, 2)
        try await owner.setAccess(available: true, generation: 3)
        let fresh = try await owner.publicDetail(first.collection, cursor: nil)
        XCTAssertEqual(fresh.members.map(\.nativeID), ["17"])
        XCTAssertEqual(fresh.members.map(\.sourcePosition), [0])
        XCTAssertEqual(fresh.tracks.map(\.title), ["New title"])
        XCTAssertNotEqual(fresh.tracks.first?.ref, first.tracks.first?.ref)
        calls = await http.calls(); XCTAssertEqual(calls.count, 4)
        await owner.close()
    }

    func testEvictedHandlesAreLocallyReregisteredWhenTheSameNativePageIsConsumedAgain() async throws {
        let ids = (1...30).map(String.init), clock = NativeMusicFixtureClock()
        let http = MusicSearchDiscoveryHTTP([
            Fixture.headPath: [Fixture.head(ids: ids)],
            Fixture.songsPath: [Fixture.songs(ids.map { Fixture.song($0) })]
        ])
        let store = NativeMusicMemoryDocument()
        let owner = try await NativeMusicFixture.service(store, http: http, clock: { clock.now() })
        let first = try await owner.publicDetail(Fixture.collection, cursor: nil)
        let old = try XCTUnwrap(first.tracks.first)
        let filler = MusicNativeTrackPage(items: Array(repeating: NativeMusicFixture.track("Capacity filler"), count: 200))
        for _ in 0..<3 {
            clock.advance(1)
            _ = try await owner.page(filler, maximumItems: 200)
        }
        let oldHandlePresent = await owner.handles[old.ref.id] != nil
        XCTAssertFalse(oldHandlePresent)
        clock.advance(1)
        let renewed = try await owner.publicDetail(first.collection, cursor: nil)
        XCTAssertEqual(renewed.snapshot, first.snapshot)
        XCTAssertEqual(renewed.members.map(\.nativeID), ids)
        XCTAssertEqual(renewed.members.map(\.sourcePosition), Array(0..<30))
        XCTAssertTrue(zip(first.tracks, renewed.tracks).allSatisfy { $0.0.ref != $0.1.ref && $0.0.sources == $0.1.sources })
        for member in renewed.members {
            let live = await owner.handles[member.track.ref.id] != nil
            XCTAssertTrue(live, member.nativeID)
        }
        let calls = await http.calls(); XCTAssertEqual(calls.count, 2, "Reissuing handles uses cached native data, not HTTP")
        XCTAssertEqual(store.snapshot().1, 0); XCTAssertEqual(store.snapshot().2, 0)
        await owner.close()
    }

    func testMemberPageReservesAllHandlesBeforeEqualTimestampEviction() async throws {
        let ids = (1...30).map(String.init)
        let http = MusicSearchDiscoveryHTTP([
            Fixture.headPath: [Fixture.head(ids: ids, tracks: ids.map { Fixture.song($0) })]
        ])
        let store = NativeMusicMemoryDocument(), owner = try await NativeMusicFixture.service(store, http: http)
        let filler = MusicNativeTrackPage(items: Array(repeating: NativeMusicFixture.track("Capacity filler"), count: 200))
        for _ in 0..<3 { _ = try await owner.page(filler, maximumItems: 200) }
        let page = try await owner.publicDetail(Fixture.collection, cursor: nil)
        XCTAssertEqual(page.members.map(\.nativeID), ids)
        XCTAssertEqual(page.tracks.count, 30)
        XCTAssertEqual(Set(page.tracks.map(\.ref)).count, 30)
        for member in page.members {
            let live = await owner.handles[member.track.ref.id] != nil
            XCTAssertTrue(live, "An equal-timestamp page must not evict its own returned handles")
        }
        let calls = await http.calls(); XCTAssertEqual(calls.map(\.url.path), [Fixture.headPath])
        XCTAssertEqual(store.snapshot().2, 0)
        await owner.close()
    }
}
