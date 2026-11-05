import Foundation
import XCTest
import MeowMusicFeature
@testable import MeowMusicNativeService

final class MusicNativeHomeActionTests: XCTestCase {
    private func json(_ value: String) throws -> MJ {
        try MJ.parse(Data(value.utf8))
    }
    private func item(_ row: MJ, channel: Bool = false) throws -> MusicNativePublicBrowseItem {
        try XCTUnwrap(MusicNativePublicBrowseParser.resource(row, index: "fixture", modern: true, channel: channel))
    }
    private func descriptor(_ row: MJ, channel: Bool = false) throws -> MusicHomeAction {
        let value = try item(row, channel: channel)
        guard case .action(let action) = value.content else {
            XCTFail("Expected a typed home action, not a playlist/default FM/unavailable substitute")
            throw MusicServiceError.invalidResponse
        }
        return action
    }
    private func unavailable(_ row: MJ, channel: Bool = false, file: StaticString = #filePath, line: UInt = #line) throws {
        let value = try item(row, channel: channel)
        guard case .unavailable = value.content else {
            XCTFail("Unsupported declaration acquired a different playback meaning", file: file, line: line)
            return
        }
    }
    private func similarURL(_ ids: MJ, sourceName: String = "") -> String {
        var url = URLComponents(string: "orpheus://nm/play/similarFM")!
        url.queryItems = [.init(name: "sourceType", value: "song"), .init(name: "sourceId", value: ids.json)]
        if !sourceName.isEmpty { url.queryItems?.append(.init(name: "sourceName", value: sourceName)) }
        return url.string!
    }
    private func block(_ code: String, id: String = "", rows: [MJ]) -> MJ {
        let resources = rows.map { row -> MJ in
            var value = row
            if value["resourceType"] == .null { value["resourceType"] = row["type"] }
            return value
        }
        var block = MJ.obj([("dslData", .obj([("blockResource", .obj([("resources", .array(resources))]))]))])
        if !code.isEmpty { block["positionCode"] = .string(code) }
        if !id.isEmpty { block["id"] = .string(id) }
        return block
    }
    private func page(_ blocks: [MJ], previous: MJ = .obj(), isHome: Bool = true) throws -> MusicNativePublicBrowseDiscovery {
        try MusicNativePublicBrowseParser.blocks(.obj([("blocks", .array(blocks))]), previous: previous, isHome: isHome)
    }

    func testModeFMRetainsExplicitModeSubmodeAndChannelEntryWithoutRuleFallback() throws {
        let modes: [MusicHomeAction.FMMode] = [.default, .familiar, .explore, .puzzle, .scene]
        for mode in modes {
            let submode = mode == .scene ? "ELECTRONIC" : ""
            let row = MJ.obj([("resourceType", .string("fm")), ("mode", .string(mode.rawValue)),
                ("subMode", .string(submode)), ("ruleId", .string("40000")), ("tabTagId", .string("invalid")),
                ("action", .string("orpheus://privatefm"))])
            XCTAssertEqual(try descriptor(row), .fm(mode: mode, submode: submode, entry: nil))
            XCTAssertEqual(try descriptor(row, channel: true), .fm(mode: mode, submode: submode, entry: .channel))
        }
        XCTAssertEqual(try descriptor(json(#"{"type":"fm","mode":"SCENE_RCMD","submode":"ACG"}"#)),
                       .fm(mode: .scene, submode: "ACG", entry: nil))
        XCTAssertEqual(try descriptor(json(#"{"type":"fm"}"#)), .fm(mode: .default, submode: "", entry: nil))
        XCTAssertEqual(try descriptor(json(#"{"resourceType":"entrance","action":"orpheus://nm/luna/privatefm"}"#)),
                       .fm(mode: .default, submode: "", entry: nil))
        // A named playlist is still a playlist, never a heuristically chosen FM.
        let playlist = try item(json(#"{"resourceType":"playlist","id":42,"title":"私人漫游"}"#))
        if case .collection(let value) = playlist.content { XCTAssertEqual(value.nativeID, "42") }
        else { XCTFail("A title must not change the action") }
    }

    func testRuleFMRetainsOneExactSelectorAndOfficialSourceName() throws {
        for rule in [MJ.n(40000), .string("1"), .string("9007199254740991")] {
            let row = MJ.obj([("type", .string("fm")), ("title", .string("频道标题")), ("fmLogoTextContent", .string("电音漫游")),
                ("ruleId", rule), ("tabTagId", .string("ignored-invalid")), ("mode", .string(""))])
            XCTAssertEqual(try descriptor(row), .ruleFM(ruleID: rule.id, tabTagID: "", sourceName: "电音漫游"))
            XCTAssertEqual(try descriptor(row, channel: true), try descriptor(row))
        }
        for tab in ["17001", "9007199254740991", "rap", "electronic", "sleep", "chinese", "occident", "korea", "classical"] {
            let row = MJ.obj([("type", .string("fm")), ("title", .string("频道标题")), ("mode", .null),
                ("ruleId", .string("")), ("tabTagId", .string(tab))])
            XCTAssertEqual(try descriptor(row, channel: true), .ruleFM(ruleID: "", tabTagID: tab, sourceName: "频道标题"))
        }
    }

    func testUnsupportedFMModesAndInvalidSelectorsCannotAcquireDefaultOrOtherProviders() throws {
        for mode in [MJ.string("UNKNOWN"), .string("SIMILAR"), .string("default"), .string(" DEFAULT "), .n(0), .bool(false), .obj(), .array([])] {
            try unavailable(.obj([("type", .string("fm")), ("mode", mode), ("ruleId", .string("40000")),
                ("action", .string("orpheus://privatefm"))]), channel: true)
        }
        for raw in [
            #"{"type":"fm","subMode":"ELECTRONIC","ruleId":"40000"}"#,
            #"{"type":"fm","submode":"ACG","tabTagId":"electronic"}"#,
            #"{"type":"fm","subMode":"UNKNOWN"}"#
        ] { try unavailable(json(raw), channel: true) }
        for bad in [MJ.n(0), .n(-1), .string("01"), .string("1.0"), .number(1.5), .string(" 1"), .string("1\n"),
                    .string("9007199254740992"), .bool(false), .bool(true), .obj(), .array([.n(40000)])] {
            try unavailable(.obj([("type", .string("fm")), ("ruleId", bad), ("tabTagId", .string("electronic"))]))
            try unavailable(.obj([("type", .string("fm")), ("tabTagId", bad)]), channel: true)
        }
        try unavailable(json(#"{"type":"fm"}"#), channel: true)
        // A future syntactically valid submode is preserved as declared; only
        // the executor can decide support, never normalize it to DEFAULT.
        XCTAssertEqual(try descriptor(json(#"{"type":"fm","mode":"SCENE_RCMD","subMode":"FUTURE_SCENE"}"#)),
                       .fm(mode: .scene, submode: "FUTURE_SCENE", entry: nil))
    }

    func testDailyBranchesFollowTabBeforeTypeAndLegacyEntranceWrappers() throws {
        let inputs: [(String, MusicHomeAction.DailyBranch)] = [
            ("orpheus://songrcmd", .default), ("orpheus://songrcmd?tab=style", .style),
            ("orpheus://nm/play/dailySong?type=style", .style),
            ("orpheus://songrcmd?tab=&type=style", .style),
            ("orpheus://songrcmd?tab=default&type=style", .default),
            ("orpheus://songrcmd?tab=default&tab=style", .default)
        ]
        for (url, branch) in inputs {
            let wrapped = MJ.obj([("clickAction", .obj([("urlAction", .obj([("url", .string(url))]))]))])
            XCTAssertEqual(try descriptor(.obj([("type", .string("dailySongs")), ("action", .string(wrapped.json))])),
                           .daily(branch: branch))
        }
        XCTAssertEqual(try descriptor(json(#"""
        {"resourceType":"dragon_ball","uiElement":{"mainTitle":{"title":"风格推荐","action":"orpheus://songrcmd?tab=style"}}}
        """#)), .daily(branch: .style))
        let result = try page([block("ENTRY", rows: [
            .obj([("type", .string("dailySongs"))]),
            .obj([("type", .string("dailySongs")), ("action", .string("orpheus://songrcmd?tab=style"))])
        ])])
        XCTAssertEqual(result.sections.first?.layout, .featureRail)
        XCTAssertEqual(result.sections.first?.items.count, 2)
        XCTAssertEqual(Set(result.sections.flatMap(\.items).map(\.id)).count, 2)
    }

    func testHeartbeatNeverTrustsResourceIDsOrPersistedSeedContext() throws {
        for raw in [
            #"{"type":"star","resourceId":"987","action":"orpheus://playlist/42"}"#,
            #"{"lunaItemType":"heartRcmd","resourceId":"invalid","playlistId":"42","sid":"99"}"#
        ] { XCTAssertEqual(try descriptor(json(raw)), .heartbeat) }
        let saved = Data(#"{"type":"heartbeat","playlistId":"42","resourceId":"not-a-playlist","pid":"99","sid":"100","url":"https://invalid.test"}"#.utf8)
        let decoded = try JSONDecoder().decode(MusicHomeAction.self, from: saved)
        XCTAssertEqual(decoded, .heartbeat)
        XCTAssertEqual(try MJ.parse(JSONEncoder().encode(decoded)), .obj([("type", .string("heartbeat"))]))
    }

    func testSimilarUsesOnlyBoundedOrderedSongSeedsAndPreservesSourceName() throws {
        let url = similarURL(.array([.string("9"), .n(2), .string("9")]), sourceName: "官方来源 / 测试歌曲")
        let expected = MusicHomeAction.similar(sourceIDs: ["9", "2", "9"], sourceName: "官方来源 / 测试歌曲")
        let wrappers = [
            MJ.string(url),
            .obj([("clickAction", .obj([("targetUrl", .string(url))]))]),
            .string(MJ.obj([("urlAction", .obj([("url", .string(url))]))]).json)
        ]
        for action in wrappers {
            let row = MJ.obj([("type", .string("similarSong")), ("resourceId", .string("[99]")), ("action", action)])
            XCTAssertEqual(try descriptor(row), expected)
            XCTAssertEqual(try item(row).id, "similar:song:9,2,9")
        }
        let maximum = similarURL(.array([.string("9007199254740991")]))
        XCTAssertEqual(try MusicNativePublicBrowseParser.action(.string(maximum))?.homeAction,
                       .similar(sourceIDs: ["9007199254740991"], sourceName: ""))
        let twenty = similarURL(.array((1...20).map(MJ.n)))
        XCTAssertEqual(try MusicNativePublicBrowseParser.action(.string(twenty))?.homeAction,
                       .similar(sourceIDs: (1...20).map(String.init), sourceName: ""))
    }

    func testMalformedSimilarDeclarationNeverFallsThroughToDetailPreviewOrFM() throws {
        let valid = similarURL(.array([.n(3), .n(7)]))
        let badSeeds: [MJ] = [.array([]), .null, .n(1), .string("1"), .obj(), .array(Array(repeating: .n(1), count: 21))] +
            [MJ.n(0), .n(-1), .number(1.5), .string("01"), .string("1e3"), .bool(true), .null,
             .array([.n(1)]), .string("9007199254740992"), .string("3\n")].map { .array([.n(2), $0]) }
        var invalid = badSeeds.map { similarURL($0) }
        invalid += [valid + "&sourceId=%5B9%5D", valid + "&sourceType=artist",
                    valid + "&sourceName=a&sourceName=b", valid + "#extra",
                    valid.replacingOccurrences(of: "nm/", with: "user@nm/"),
                    valid.replacingOccurrences(of: "nm/", with: "nm:99/"),
                    "orpheus://privatefm", "orpheus://song/3",
                    "orpheus://nm/play/similarFM?sourceId=not-json&sourceType=song"]
        for url in invalid {
            let row = MJ.obj([("type", .string("similarSong")), ("resourceId", .n(99)), ("action", .string(url)),
                ("playBtnData", .obj([("detailUrl", .string(valid))])),
                ("resourceExtInfo", .obj([("songData", .obj([("id", .n(3))]))]))])
            try unavailable(row)
        }
        try unavailable(.obj([("type", .string("similarArtist")), ("action", .string(valid))]))
        let alternate = MJ.obj([("clickAction", .string("https://invalid.test")), ("url", .string(valid))])
        try unavailable(.obj([("type", .string("similarSong")), ("action", alternate)]))
        XCTAssertEqual(try descriptor(.obj([("type", .string("similarSong")),
            ("playBtnData", .obj([("detailUrl", .string(valid))]))])),
                       .similar(sourceIDs: ["3", "7"], sourceName: ""))
    }

    func testFiniteSongCollectionsFilterDeduplicateAndDoNotBecomePlaylists() throws {
        var row = try json(#"""
        {"resourceType":"topList","resourceId":"USER_RCMD_SONG##","title":"实际集合",
         "action":"https://st.music.163.com/nebula?scene=native",
         "playBtnData":{"playAction":{"songIds":[3,1,3,"2","0","unsafe","01",true,1.5]}}}
        """#)
        let first = try item(row)
        XCTAssertEqual(try descriptor(row), .songCollection(ids: ["3", "1", "2"]))
        XCTAssertTrue(first.id.hasPrefix("songs:USER_RCMD_SONG##:"))
        row["title"] = .string("改名"); row["action"] = .string("javascript:ignored()")
        XCTAssertEqual(try item(row).id, first.id)
        row["action"] = .string("orpheus://playlist/42")
        if case .collection(let value) = try item(row).content { XCTAssertEqual(value.nativeID, "42") }
        else { XCTFail("An explicit real playlist takes precedence over finite preview IDs") }
        row["action"] = .null; row["resourceType"] = .string("activity")
        XCTAssertNil(try MusicNativePublicBrowseParser.resource(row, index: "activity"))
        row["resourceType"] = .string("video")
        XCTAssertNil(try MusicNativePublicBrowseParser.resource(row, index: "video"))
        row["resourceType"] = .string("topList")
        row["playBtnData"] = .obj([("playAction", .obj([("songIds", .array([]))]))])
        XCTAssertNil(try MusicNativePublicBrowseParser.resource(row, index: "empty"))
        row["playBtnData"] = .null
        row["songIds"] = .array((1...1000).map(MJ.n))
        XCTAssertEqual(try descriptor(row), .songCollection(ids: (1...1000).map(String.init)))
        row["songIds"] = .array(Array(repeating: .n(1), count: 1001))
        XCTAssertEqual(try descriptor(row), .songCollection(ids: ["1"]))
    }

    func testDescriptorCodableRoundTripsOnlyTypedFieldsAndValidatesBounds() throws {
        let values: [MusicHomeAction] = [
            .fm(mode: .scene, submode: "ELECTRONIC", entry: .channel),
            .fm(mode: .familiar, submode: "", entry: nil),
            .ruleFM(ruleID: "40000", tabTagID: "", sourceName: "规则频道"),
            .ruleFM(ruleID: "", tabTagID: "electronic", sourceName: ""),
            .similar(sourceIDs: ["3", "1", "3"], sourceName: "相似来源"),
            .daily(branch: .default), .daily(branch: .style), .heartbeat, .songCollection(ids: ["3", "1"])
        ]
        for value in values {
            try value.validate()
            let encoded = try JSONEncoder().encode(value)
            XCTAssertEqual(try JSONDecoder().decode(MusicHomeAction.self, from: encoded), value)
            let object = try MJ.parse(encoded)
            XCTAssertFalse(object.contains("url")); XCTAssertFalse(object.contains("dsl"))
            XCTAssertFalse(object.contains("playlistId")); XCTAssertFalse(object.contains("resourceId"))
        }
        XCTAssertEqual(Set(values).count, values.count)
        for invalid in [
            #"{"type":"similar","sourceType":"artist","sourceIds":["3"]}"#,
            #"{"type":"similar","sourceType":"song","sourceIds":["9007199254740992"]}"#,
            #"{"type":"similar","sourceType":"song","sourceIds":[]}"#,
            #"{"type":"fm","provider":"other","mode":"DEFAULT","submode":""}"#,
            #"{"type":"fm","mode":"SIMILAR","submode":""}"#,
            #"{"type":"fm","mode":"DEFAULT","submode":"","entry":"other"}"#,
            #"{"type":"fm","provider":"rule","ruleId":"40000","tabTagId":"electronic"}"#,
            #"{"type":"song-collection","ids":["01"]}"#,
            #"{"type":"song-collection","ids":["1","2\n"]}"#,
            #"{"type":"daily","branch":"other"}"#,
            #"{"type":"url","url":"orpheus://privatefm"}"#
        ] { XCTAssertThrowsError(try JSONDecoder().decode(MusicHomeAction.self, from: Data(invalid.utf8))) }
        XCTAssertThrowsError(try JSONEncoder().encode(MusicHomeAction.similar(sourceIDs: Array(repeating: "1", count: 21), sourceName: "")))
        XCTAssertNoThrow(try JSONEncoder().encode(MusicHomeAction.songCollection(ids: Array(repeating: "1", count: 1001))))
    }

    func testArtistActivitySectionFieldsRemainAdditiveAndKeepTheKnownChannelKind() throws {
        let legacy = MusicPublicBrowseSection(id: "legacy", title: "", items: [])
        XCTAssertTrue(legacy.avatars.isEmpty)
        XCTAssertNil(legacy.channel)
        XCTAssertEqual(legacy.layout, .list)
        let channel = MusicPublicBrowseChannel(id: "artist-activity", title: "关注艺人动态", kind: .artistActivity)
        let avatar = try XCTUnwrap(URL(string: "https://p1.music.126.net/synthetic.jpg"))
        let section = MusicPublicBrowseSection(id: "updates", title: "关注艺人动态", items: [], layout: .artistUpdates,
                                               avatars: [avatar], channel: channel)
        XCTAssertEqual(section.channel?.kind.rawValue, "artist-activity")
        XCTAssertEqual(section.avatars, [avatar])
        let internalSection = MusicNativePublicBrowseSection(id: "updates", title: "", items: [], layout: .artistUpdates,
                                                             avatars: [avatar], channel: channel)
        XCTAssertEqual(internalSection.channel, channel)
        XCTAssertEqual(internalSection.avatars, section.avatars)
    }

    func testParsingDoesNotSwallowMalformedPayloadsTraversalLimitsOrExecutableURLs() throws {
        XCTAssertThrowsError(try MusicNativePublicBrowseParser.resource(
            json(#"{"type":"fm","playBtnData":"not-json"}"#), index: "fixture", modern: true)) {
            XCTAssertEqual($0 as? MusicServiceError, .corruptDocument)
        }
        var deep = MJ.string("orpheus://privatefm")
        for _ in 0..<6 { deep = .obj([("clickAction", deep)]) }
        XCTAssertThrowsError(try MusicNativePublicBrowseParser.action(deep)) {
            XCTAssertEqual($0 as? MusicServiceError, .limit)
        }
        for url in ["javascript:alert(1)", "https://invalid.test/playlist/3", "file:///song/3",
                    "orpheus://playlist//3", "orpheus://playlist/3/", "orpheus://playlist/3#fragment"] {
            XCTAssertNil(try MusicNativePublicBrowseParser.action(.string(url)))
        }
    }

    func testHomeSectionAndEntranceIDsSurvivePagePositionCursorAndPresentationChanges() throws {
        let song1 = try json(#"{"type":"song","id":1,"name":"一"}"#)
        let song2 = try json(#"{"type":"song","id":2,"name":"二"}"#)
        let playlist = try json(#"{"type":"playlist","id":42,"name":"歌单"}"#)
        let fm = try json(#"{"type":"fm","mode":"EXPLORE","title":"漫游"}"#)
        let first = try page([block("OLD", rows: [playlist]), block("CONTINUED", id: "stable-block", rows: [song1, playlist]),
                              block("ENTRANCE", rows: [fm])])
        let next = try page([block("ENTRANCE", rows: [fm]), block("CONTINUED", id: "stable-block", rows: [song2, playlist])],
                            previous: .obj([("cursor", .string("123")), ("loaded", .strings(["OLD", "CONTINUED"]))]))
        XCTAssertEqual(first.sections.first?.id, "features")
        XCTAssertEqual(first.sections.first?.items.first?.id, next.sections.first?.items.first?.id)
        let oldRuns = first.sections.filter { $0.id.contains("stable-block") }
        let newRuns = next.sections.filter { $0.id.contains("stable-block") }
        XCTAssertEqual(oldRuns.map(\.id), newRuns.map(\.id))
        XCTAssertEqual(oldRuns.map(\.layout), [.songColumns, .playlistRail])
        XCTAssertNotEqual(oldRuns[0].id, oldRuns[1].id)
        XCTAssertNotEqual(oldRuns[0].items[0].id, newRuns[0].items[0].id)
        XCTAssertEqual(oldRuns[1].items[0].id, newRuns[1].items[0].id)
    }

    func testStableBlocksKeepDistinctOccurrencesAndOnlyAnonymousBlocksUsePageFallback() throws {
        let a = try json(#"{"type":"playlist","id":1}"#), b = try json(#"{"type":"playlist","id":2}"#)
        let duplicate = block("DUP", rows: [a])
        let distinct = try page([duplicate, duplicate, block("DUP", id: "another", rows: [b])])
        XCTAssertEqual(Set(distinct.sections.map(\.id)).count, 3)
        let reordered = try page([block("DUP", id: "another", rows: [b]), duplicate])
        XCTAssertEqual(distinct.sections.last?.id, reordered.sections.first?.id)
        XCTAssertEqual(distinct.sections.first?.id, reordered.sections.last?.id)
        let cursor = MJ.obj([("cursor", .string("1"))])
        XCTAssertEqual(try page([duplicate]).sections.first?.id, try page([duplicate], previous: cursor).sections.first?.id)
        let anonymous = block("", rows: [a])
        XCTAssertNotEqual(try page([anonymous]).sections.first?.id, try page([anonymous], previous: cursor).sections.first?.id)
        XCTAssertEqual(try page([duplicate], isHome: false).sections.first?.id, "0:0:0")
        XCTAssertEqual(try page([duplicate], previous: cursor, isHome: false).sections.first?.id, "0:0:0")
    }

    func testIdentifiedDSLRootsDoNotDependOnDictionaryOrder() throws {
        let firstRoot = try json(#"{"id":"first","items":[{"type":"playlist","resourceType":"playlist","id":1}]}"#)
        let secondRoot = try json(#"{"id":"second","items":[{"type":"playlist","resourceType":"playlist","id":2}]}"#)
        let initial = MJ.obj([("positionCode", .string("ROOTS")), ("dslData", .obj([("a", firstRoot), ("b", secondRoot)]))])
        let later = MJ.obj([("positionCode", .string("ROOTS")), ("dslData", .obj([("b", secondRoot), ("a", firstRoot)]))])
        let one = try page([initial]), two = try page([later], previous: .obj([("cursor", .string("2"))]))
        XCTAssertEqual(one.sections.map(\.id), two.sections.reversed().map(\.id))
    }
}
