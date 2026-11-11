// SPDX-License-Identifier: GPL-3.0-only
import XCTest
import MeowMusicFeature
@testable import MeowMusicNativeService

final class MusicNativeArtistActivityTests: XCTestCase {
    private func parse(_ json: String) throws -> MusicNativePublicBrowseDiscovery {
        try MusicNativePublicBrowseParser.artistActivity(MJ.parse(Data(json.utf8)))
    }

    private func album(_ item: MusicNativePublicBrowseItem,
                       file: StaticString = #filePath, line: UInt = #line) throws -> MusicPublicBrowseCollection {
        guard case .collection(let collection) = item.content else {
            XCTFail("An actual album must open the shared collection detail", file: file, line: line)
            throw MusicServiceError.invalidResponse
        }
        XCTAssertEqual(collection.kind, .album, file: file, line: line)
        XCTAssertEqual(collection.platform, .netease, file: file, line: line)
        return collection
    }

    func testMultiSongAlbumKeepsSummaryAndReportedCountInsteadOfPreviewLength() throws {
        let page = try parse(#"""
        {"data":{"title":"艺人新作","newWorks":[{"publishTime":1234,"info":{
          "blockType":"album","albumId":42,"albumSongCount":8,
          "blockTitle":{"resourceName":"新专辑","artistName":"艺人",
            "resourcePicUrl":"https://p1.music.126.net/album.jpg","imgUrl":"https://p1.music.126.net/avatar.jpg"},
          "songLists":[{"id":41,"name":"预览曲","al":{"id":42,"name":"曲目附带专辑"}}]
        }}]}}
        """#)
        let section = try XCTUnwrap(page.sections.first)
        XCTAssertEqual(page.sections.count, 1)
        XCTAssertEqual(section.title, "艺人新作")
        XCTAssertEqual(section.layout, .artistUpdates)
        XCTAssertEqual(section.channel?.kind, .artistActivity)
        XCTAssertEqual(section.items.count, 1)
        let item = try XCTUnwrap(section.items.first), collection = try album(item)
        XCTAssertEqual(item.id, "wy:album:42")
        XCTAssertEqual(collection.nativeID, "42")
        XCTAssertEqual(collection.count, 8)
        XCTAssertEqual(collection.title, "新专辑")
        XCTAssertEqual(collection.creator, "艺人")
        XCTAssertEqual(collection.artwork?.absoluteString, "https://p1.music.126.net/album.jpg")
        XCTAssertEqual(item.title, collection.title)
        XCTAssertEqual(item.subtitle, collection.creator)
        XCTAssertEqual(item.artwork, collection.artwork)
        XCTAssertEqual(item.coverLabel, "新专辑")
        XCTAssertEqual(section.avatars.map(\.absoluteString), ["https://p1.music.126.net/avatar.jpg"])
    }

    func testOneSongAlbumKeepsAlbumIdentityAndSingleBadgeEvenInSongBlock() throws {
        for kind in ["song", "album"] {
            let page = try parse("""
            {"newWorks":[{"info":{"blockType":"\(kind)","albumId":"42","albumSongCount":1,
              "blockTitle":{"resourceName":"单曲专辑","artistName":"艺人"},
              "songLists":[{"id":41,"name":"单曲","al":{"id":42,"name":"单曲专辑"}}]
            }}]}
            """)
            let item = try XCTUnwrap(page.sections.first?.items.first), collection = try album(item)
            XCTAssertEqual(collection.nativeID, "42")
            XCTAssertEqual(collection.title, "单曲专辑")
            XCTAssertEqual(collection.count, 1)
            XCTAssertEqual(item.coverLabel, "新单曲")
        }
    }

    func testAlbumWithoutReliableCountKeepsIdentityWithoutInventingCountOrBadge() throws {
        for count in ["", #","albumSongCount":null"#, #","albumSongCount":0"#,
                      #","albumSongCount":-1"#, #","albumSongCount":1.5"#] {
            let page = try parse("""
            {"newWorks":[{"info":{"blockType":"album","albumId":42\(count),
              "blockTitle":{"resourceName":"未知曲数专辑"},"songLists":[{"id":41}]}}]}
            """)
            let item = try XCTUnwrap(page.sections.first?.items.first), collection = try album(item)
            XCTAssertEqual(collection.nativeID, "42")
            XCTAssertNil(collection.count)
            XCTAssertEqual(item.coverLabel, "")
        }
    }

    func testAlbumCountFitsSnapshotContractAndRejectsOverflowWithoutClamping() throws {
        let page = try parse(#"""
        {"newWorks":[{"info":{"blockType":"album","albumId":42,"albumSongCount":1000000,
          "blockTitle":{"resourceName":"专辑"}}}]}
        """#)
        let item = try XCTUnwrap(page.sections.first?.items.first)
        XCTAssertEqual(try album(item).count, 1_000_000)
        let data = try MusicNativeHomeSnapshot.encode(page, source: .netease)
        let restored = try XCTUnwrap(MusicNativeHomeSnapshot.decode(data, source: .netease))
        let restoredItem = try XCTUnwrap(restored.sections.first?.items.first)
        XCTAssertEqual(try album(restoredItem).count, 1_000_000)

        XCTAssertThrowsError(try parse(#"""
        {"newWorks":[{"info":{"blockType":"album","albumId":42,"albumSongCount":1000001,
          "blockTitle":{"resourceName":"专辑"}}}]}
        """#)) { error in
            XCTAssertEqual(error as? MusicServiceError, .invalidResponse)
        }
    }

    func testAlbumSummaryDoesNotNeedAnyPreviewSong() throws {
        for songs in ["", #","songLists":[]"#] {
            let page = try parse("""
            {"newWorks":[{"info":{"blockType":"album","albumId":42,"albumSongCount":2,
              "blockTitle":{"resourceName":"摘要专辑","artistName":"摘要艺人",
                "resourcePicUrl":"https://p1.music.126.net/summary.jpg"}\(songs)}}]}
            """)
            let item = try XCTUnwrap(page.sections.first?.items.first), collection = try album(item)
            XCTAssertEqual(collection.nativeID, "42")
            XCTAssertEqual(collection.title, "摘要专辑")
            XCTAssertEqual(collection.creator, "摘要艺人")
            XCTAssertEqual(collection.count, 2)
            XCTAssertEqual(item.artwork?.absoluteString, "https://p1.music.126.net/summary.jpg")
            XCTAssertEqual(item.subtitle, "摘要艺人")
            XCTAssertEqual(item.coverLabel, "新专辑")
        }
    }

    func testSongAlbumProvidesRealIdentityAndDisplayFallbackWithoutAssumingCount() throws {
        for id in ["", #","albumId":0"#, #","albumId":"invalid""#] {
            let page = try parse("""
            {"newWorks":[{"info":{"blockType":"song"\(id),"songLists":[
              {"id":41,"name":"曲名","ar":[{"name":"歌手甲"},{"name":"歌手乙"}],
               "al":{"id":43,"name":"真实专辑","picUrl":"https://p1.music.126.net/fallback.jpg"}}
            ]}}]}
            """)
            let item = try XCTUnwrap(page.sections.first?.items.first), collection = try album(item)
            XCTAssertEqual(collection.nativeID, "43")
            XCTAssertEqual(collection.title, "真实专辑")
            XCTAssertEqual(collection.creator, "歌手甲、歌手乙")
            XCTAssertEqual(item.subtitle, collection.creator)
            XCTAssertEqual(item.artwork?.absoluteString, "https://p1.music.126.net/fallback.jpg")
            XCTAssertNil(collection.count)
            XCTAssertEqual(item.coverLabel, "")
        }
    }

    func testDeclaredAlbumIdentityTakesPrecedenceOverSongAlbum() throws {
        let page = try parse(#"""
        {"newWorks":[{"info":{"blockType":"album","albumId":42,"albumSongCount":3,
          "blockTitle":{"resourceName":"主专辑","artistName":"艺人"},
          "songLists":[{"id":41,"al":{"id":43,"name":"其他专辑"}}]
        }}]}
        """#)
        let item = try XCTUnwrap(page.sections.first?.items.first), collection = try album(item)
        XCTAssertEqual(collection.nativeID, "42")
        XCTAssertEqual(collection.title, "主专辑")
        XCTAssertEqual(collection.count, 3)
    }

    func testSongWithoutValidAlbumKeepsSongContentAndNeverUsesSongIDAsAlbumID() throws {
        for id in ["", #","id":null"#, #","id":0"#, #","id":"0""#] {
            let page = try parse("""
            {"newWorks":[{"info":{"blockType":"song","albumId":0,"albumSongCount":1,
              "blockTitle":{"artistName":"艺人"},"songLists":[
                {"id":41,"name":"独立歌曲","ar":[{"name":"艺人"}],"dt":180000,
                 "al":{"name":"歌曲附带名称","picUrl":"https://p1.music.126.net/song.jpg"\(id)}}
              ]}}]}
            """)
            let item = try XCTUnwrap(page.sections.first?.items.first)
            switch item.content {
            case .song(let track):
                let data = try MJ.parse(track.data)
                XCTAssertEqual(data["songmid"].id, "41")
                XCTAssertEqual(data["albumId"].string, "")
            default: XCTFail("A song without an actual album must retain its song behavior")
            }
            XCTAssertEqual(item.id, "song:41")
            XCTAssertEqual(item.title, "独立歌曲")
            XCTAssertEqual(item.subtitle, "艺人")
            XCTAssertEqual(item.artwork?.absoluteString, "https://p1.music.126.net/song.jpg")
            XCTAssertEqual(item.coverLabel, "新单曲")
        }
    }

    func testFilteringDeduplicationAvatarsAndContinuationUseOriginalWorks() throws {
        let page = try parse(#"""
        {"data":{"hasMore":true,"newWorks":[
          {"publishTime":100,"info":{"blockType":"mv","albumId":90,"albumSongCount":1,
            "blockTitle":{"resourceName":"视频","imgUrl":"https://p1.music.126.net/mv.jpg"}}},
          {"publishTime":101,"info":{"blockType":"album","albumId":42,"albumSongCount":1,
            "blockTitle":{"resourceName":"首条","imgUrl":"https://p1.music.126.net/a.jpg"}}},
          {"publishTime":102,"info":{"blockType":"song","albumId":42,"albumSongCount":1,
            "blockTitle":{"resourceName":"重复","imgUrl":"https://p1.music.126.net/duplicate.jpg"}}},
          {"publishTime":103,"info":{"blockType":"album","albumId":43,"albumSongCount":2,
            "blockTitle":{"resourceName":"第二条","imgUrl":"https://p1.music.126.net/a.jpg"}}},
          {"publishTime":104,"info":{"blockType":"album","albumId":44,"albumSongCount":3,
            "blockTitle":{"resourceName":"第三条","imgUrl":"https://p1.music.126.net/b.jpg"}}},
          {"publishTime":105,"info":{"blockType":"album","albumId":45,"albumSongCount":4,
            "blockTitle":{"resourceName":"第四条","imgUrl":"https://p1.music.126.net/c.jpg"}}},
          {"publishTime":106,"info":{"blockType":"album","albumId":46,"albumSongCount":5,
            "blockTitle":{"resourceName":"第五条","imgUrl":"https://p1.music.126.net/d.jpg"}}},
          {"publishTime":107,"info":{"blockType":"podcast","albumId":91,
            "blockTitle":{"resourceName":"播客","imgUrl":"https://p1.music.126.net/podcast.jpg"}}}
        ]}}
        """#)
        let section = try XCTUnwrap(page.sections.first)
        XCTAssertEqual(section.items.map(\.title), ["首条", "第二条", "第三条", "第四条", "第五条"])
        XCTAssertEqual(section.items.map(\.id), ["wy:album:42", "wy:album:43", "wy:album:44",
                                                "wy:album:45", "wy:album:46"])
        XCTAssertEqual(section.avatars.map(\.absoluteString), ["https://p1.music.126.net/a.jpg",
                       "https://p1.music.126.net/b.jpg", "https://p1.music.126.net/c.jpg"])
        XCTAssertEqual(page.consumed, 8)
        XCTAssertEqual(page.next?["before"].string, "107")
    }

    func testContinuationRequiresHasMoreAndARealTimestamp() throws {
        for suffix in [#""hasMore":false,"newWorks":[{"publishTime":1234"#,
                       #""hasMore":true,"newWorks":[{"publishTime":0"#,
                       #""hasMore":true,"newWorks":[{"publishTime":"invalid""#] {
            let page = try parse("""
            {\(suffix),"info":{"blockType":"album","albumId":42,
              "blockTitle":{"resourceName":"专辑"}}}]}
            """)
            XCTAssertEqual(page.sections.first?.items.count, 1)
            XCTAssertNil(page.next)
        }
    }
}
