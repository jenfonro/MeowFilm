import Foundation
import XCTest
import MeowMusicFeature
@testable import MeowMusicNativeService

/// Fixed-reference response shapes only; no live account or product fallback data.
final class MusicNativeHomePresentationTests: XCTestCase {
    private func json(_ text: String) throws -> MJ {
        try MJ.parse(Data(text.utf8), mode: .upstream)
    }

    func testHeaderActionsUseDirectoryChannelIdentityAndPreserveCardDestinations() throws {
        let root = try json(#"""
        {"data":{"blocks":[
          {"positionCode":"lists","dslData":{"header":{"title":"推荐歌单",
            "action":"orpheus://rnpage?component=rn-playlistsquare"},"items":[
            {"resourceType":"playlist","id":"41","title":"歌单"}]}},
          {"positionCode":"ranks","dslData":{"title":"排行榜",
            "action":"orpheus://nm/mainTab/select?tab=music&musicTabCode=chart","items":[
            {"resourceType":"toplist","id":"42","title":"榜单"}]}}
        ]}}
        """#)
        let page = try MusicNativePublicBrowseParser.blocks(root, previous: .obj())
        let catalogue = try MusicNativePublicBrowseParser.channels(json(#"""
        {"data":{"channels":[{"code":"playlist","title":"歌单"},{"code":"chart","title":"排行榜"}]}}
        """#))
        XCTAssertEqual(page.sections.compactMap { $0.channel?.identity }, catalogue.map(\.identity))
        XCTAssertEqual(page.sections.compactMap { $0.channel?.kind }, [.playlists, .rankings])
        for (section, id) in zip(page.sections, ["41", "42"]) {
            guard case .collection(let value) = section.items[0].content else { return XCTFail("Card destination required") }
            XCTAssertEqual(value.nativeID, id)
            XCTAssertEqual(value.platform, .netease)
        }
    }

    func testHeaderFallbackUsesResourceKindInsteadOfTitleAndDoesNotReplaceDeclaredActions() throws {
        let root = try json(#"""
        {"data":{"blocks":[
          {"positionCode":"lists","dslData":{"title":"任意栏目","items":[{"resourceType":"playlist","id":"41","title":"歌单"}]}},
          {"positionCode":"ranks","dslData":{"title":"另一栏目","items":[{"resourceType":"toplist","id":"42","title":"榜单"}]}},
          {"positionCode":"songs","dslData":{"title":"推荐歌单","items":[{"resourceType":"song","id":"43"}]}},
          {"positionCode":"unsupported","dslData":{"title":"排行榜","action":"javascript:invalid",
            "items":[{"resourceType":"playlist","id":"44","title":"歌单"}]}}
        ]}}
        """#)
        let page = try MusicNativePublicBrowseParser.blocks(root, previous: .obj())
        XCTAssertEqual(page.sections[0].channel?.id, "playlist")
        XCTAssertEqual(page.sections[1].channel?.id, "chart")
        XCTAssertNil(page.sections[2].channel)
        XCTAssertNil(page.sections[3].channel)
        XCTAssertEqual(page.sections[3].items.count, 1)
    }

    func testCompactLabelsAndNativeArtworkFieldsFollowReferencePrecedence() throws {
        let rows = try json(#"""
        [
          {"resourceType":"dailySongs","title":"每日推荐 | 从「歌曲」听起","subTitle":"10-23","dayOfMonth":"23","singleLineTitle":"推荐曲目","coverImg":"http://p1.music.126.net/daily.jpg"},
          {"resourceType":"fm","title":"歌曲预览","subTitle":"私人漫游","singleLineTitle":"官方副标题","background":"https://p1.music.126.net/fm.jpg"},
          {"resourceType":"star","title":"红心歌曲","subTitle":"心动模式","singleLineTitle":"更多推荐","uiElement":{"images":[{"imageUrl":"https://p1.music.126.net/heart.jpg"}]}},
          {"resourceType":"playlist","id":41,"title":"歌单 | 原标题","subTitle":"原副标题","uiElement":{"images":[{"url":"https://p1.music.126.net/list.jpg"}]}}
        ]
        """#).array
        let items = try rows.enumerated().map {
            try XCTUnwrap(MusicNativePublicBrowseParser.resource($0.element, index: String($0.offset), modern: true))
        }
        XCTAssertEqual(items.map(\.title), ["每日推荐", "私人漫游", "心动模式", "歌单 | 原标题"])
        XCTAssertEqual(items.map(\.subtitle), ["推荐曲目", "官方副标题", "更多推荐", "原副标题"])
        XCTAssertEqual(items.map { $0.artwork?.absoluteString }, ["daily", "fm", "heart", "list"].map {
            "https://p1.music.126.net/" + $0 + ".jpg"
        })
        let legacy = try XCTUnwrap(MusicNativePublicBrowseParser.resource(rows[0], index: "legacy"))
        XCTAssertEqual(legacy.title, "每日推荐 | 从「歌曲」听起")
        XCTAssertEqual(legacy.subtitle, "10-23")
        var precedence = rows[3]
        precedence["cover"] = .string("https://p1.music.126.net/first.jpg")
        precedence["coverImg"] = .string("https://p1.music.126.net/second.jpg")
        XCTAssertEqual(MusicNativePublicBrowseParser.image(precedence)?.lastPathComponent, "first.jpg")
    }

    func testAlbumLogoUsesDeclaredPlaylistArtworkWithoutRelaxingURLPolicy() throws {
        var row = try json(#"""
        {"resourceType":"playlist","id":42,"title":"歌曲预览","subTitle":"私人雷达","singleLineTitle":"官方副标题",
         "coverType":"ALBUM_LOGO","coverImg":"https://p1.music.126.net/gradient.jpg"}
        """#)
        func action(_ cover: String) -> String {
            var url = URLComponents(string: "orpheus://playlist/42")!
            url.queryItems = [.init(name: "coverUrl", value: cover)]
            return url.string!
        }
        row["playBtnData"] = .string(MJ.obj([("detailUrl", .string(action("http://p1.music.126.net/album.jpg")))]).json)
        let item = try XCTUnwrap(MusicNativePublicBrowseParser.resource(row, index: "logo", modern: true))
        XCTAssertEqual(item.title, "私人雷达")
        XCTAssertEqual(item.artwork?.absoluteString, "https://p1.music.126.net/album.jpg")
        for host in ["independent-cdn.invalid", "pic.kuwo.cn", "d.musicapp.migu.cn", "p1.music.126.net.evil.invalid"] {
            row["action"] = .string(action("https://\(host)/album.jpg"))
            XCTAssertEqual(try MusicNativePublicBrowseParser.resource(row, index: "cdn")?.artwork?.host, host)
        }
        for unsafe in ["javascript:alert(1)", "https://user:secret@p1.music.126.net/a.jpg",
                       "https://cdn.invalid:8443/a.jpg", "https://p1.music.126.net/a.jpg#fragment"] {
            row["action"] = .string(action(unsafe))
            XCTAssertEqual(try MusicNativePublicBrowseParser.resource(row, index: "unsafe")?.artwork?.lastPathComponent, "gradient.jpg")
        }
        row["action"] = .string(action("https://p1.music.126.net/album.jpg").replacingOccurrences(of: "playlist/42", with: "playlist:99/42"))
        XCTAssertEqual(try MusicNativePublicBrowseParser.resource(row, index: "port")?.artwork?.lastPathComponent, "gradient.jpg")
    }

    func testDSLRootsKeepMixedRunsButCollectAllEntrancesBeforeSections() throws {
        let root = try json(#"""
        {"blocks":[
          {"positionCode":"CONTENT","dslData":{"group":{"blockResource":{"resourceType":"","title":"真实栏目","resources":[
            {"resourceType":"playlist","id":41,"title":"歌单"},
            {"resourceType":"song","id":42,"title":"歌曲"},
            {"resourceType":"digitalAlbum","id":43,"title":"数字专辑"}]}}}},
          {"positionCode":"ENTRY","dslData":{"dslShowTitle":false,"blockResource":{"resources":[
            {"resourceType":"dailySongs","title":"每日推荐"},
            {"resourceType":"fm","title":"私人漫游","mode":"SCENE_RCMD","subMode":"ELECTRONIC"},
            {"resourceType":"similarSong","title":"相似推荐","action":"orpheus://nm/play/similarFM?sourceId=%5B3,7%5D&sourceType=song"}]}}},
          {"positionCode":"ENTRY","dslData":{"dslShowTitle":false,"blockResource":{"resources":[
            {"resourceType":"dailySongs","title":"另一个实际入口"}]}}}
        ]}
        """#)
        let page = try MusicNativePublicBrowseParser.blocks(root, previous: .obj())
        XCTAssertEqual(page.sections.map(\.layout), [.featureRail, .playlistRail, .songColumns, .playlistRail])
        XCTAssertEqual(page.sections.map(\.title), ["", "真实栏目", "真实栏目", "真实栏目"])
        let entries = try XCTUnwrap(page.sections.first).items
        XCTAssertEqual(entries.map(\.title), ["每日推荐", "私人漫游", "相似推荐", "另一个实际入口"])
        XCTAssertEqual(Set(entries.map(\.id)).count, entries.count)
        XCTAssertTrue(entries.allSatisfy(\.isEntrance))
        if case .collection(let album) = page.sections[3].items[0].content {
            XCTAssertEqual(album.kind, .album)
        } else { XCTFail("digital album must retain its collection kind") }
        let link = try MusicNativePublicBrowseParser.action(.string("orpheus://nm/play/similarFM?sourceId=%5B3,7%5D&sourceType=song"))
        XCTAssertEqual(link?.id, "similar:song:3,7")
        XCTAssertNil(try MusicNativePublicBrowseParser.action(.string("orpheus://nm/play/similarFM?sourceId=%5B3%5D&sourceType=song&sourceType=artist")))
        XCTAssertNil(try MusicNativePublicBrowseParser.action(.string("orpheus://nm/play/similarFM?sourceId=not-json&sourceType=song")))
    }

    func testEmptyAndUnknownBlocksDoNotFabricateGuestCardsOrHeadings() throws {
        let root = try json(#"""
        {"blocks":[
          {"dslData":{"blockResource":{"title":"登录后动态","resources":[]}}},
          {"dslData":{"blockResource":{"title":"未知区块","resources":[
            {"resourceType":"promotion","title":"VIP享特权"},
            {"resourceType":"unknownWidget"},
            {"resourceType":"video","resourceExtInfo":{"songData":{"id":42}}}]}}}
        ]}
        """#)
        let page = try MusicNativePublicBrowseParser.blocks(root, previous: .obj())
        XCTAssertTrue(page.sections.isEmpty)
        XCTAssertNil(page.next)
        // An actual known music resource is not thrown out with unknown widgets.
        let finite = try MusicNativePublicBrowseParser.resource(json(#"""
        {"resourceType":"topList","resourceId":"USER_RCMD_SONG##","title":"实际歌曲集合",
         "playBtnData":{"playAction":{"songIds":[3,1,3]}}}
        """#), index: "finite")
        XCTAssertEqual(finite?.title, "实际歌曲集合")
        XCTAssertNotNil(try MusicNativePublicBrowseParser.resource(
            json(#"{"resourceType":"similarArtist","title":"相似艺人"}"#), index: "similar"))
    }

    func testLegacyCreativeMetadataAndPlaylistSquareAreNotLostToDSLFeatureHeuristics() throws {
        let root = try json(#"""
        {"blocks":[
          {"showType":"PLAYLIST_SQUARE_MULTI_THREE_GRID","hideTitle":true,"uiElement":{"mainTitle":{"title":"隐藏标题"}},
           "creatives":[
            {"creativeId":"41","creativeType":"list","name":"创意标题","coverImg":"https://p1.music.126.net/creative.jpg",
             "action":{"urlAction":{"url":"orpheus://playlist/41"}},"resources":[{"resourceId":"41","uiElement":null}]},
            {"creativeId":"42","creativeType":"list","name":"独立创意"},
            {"creativeType":"video","resources":[{"resourceType":"playlist","id":43,"name":"不是音乐"}]}]},
          {"positionCode":"PAGE_RECOMMEND_MY_SHEET","showType":"PLAYLIST_SQUARE_FLAT",
           "dslData":{"dslShowTitle":false,"blockResource":{"title":"原栏目名","resources":[{"resourceType":"playlist","id":44,"title":"另一个歌单"}]}}}
        ]}
        """#)
        let page = try MusicNativePublicBrowseParser.blocks(root, previous: .obj(), isHome: false)
        XCTAssertEqual(page.sections.map(\.layout), [.grid, .grid])
        XCTAssertEqual(page.sections.map(\.title), ["", "原栏目名"])
        XCTAssertEqual(page.sections[0].items.map(\.title), ["创意标题", "独立创意"])
        XCTAssertEqual(page.sections[0].items[0].artwork?.lastPathComponent, "creative.jpg")
        XCTAssertEqual(page.sections[0].items.map(\.id), ["wy:playlist:41", "wy:playlist:42"])
    }
}
