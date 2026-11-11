import Foundation
import XCTest
import MeowMusicFeature
@testable import MeowMusicPlatform

final class MusicNativeFMProtocolTests: XCTestCase {
    private let fm = MusicHomeAction.fm(mode: .scene, submode: "ELECTRONIC", entry: .channel)
    func testFourMainModesAndAllOfficialScenesReachWireWithoutFallback() async throws {
        let scenes = [
            "NIGHT_EMO", "EXERCISE", "SLEEP_HELP", "RELAX", "CHEERFUL", "LYRICAL", "CURE", "FOCUS",
            "SWEET", "RHYTHM_BLUES", "RAINY", "GAMES", "RAP", "K_POP", "ORIGINAL_MUSICIAL", "ELECTRONIC", "COMMUTE",
            "TAKE_SHOWER", "COFFEE_SHOP", "ROCK", "INSPIRATIONAL", "CHINESE", "ENGLISH", "YUEYU", "MANYAO", "JINGDIAN",
            "LIGHT", "GUOFENG", "FOLK", "ACG", "GUDIAN", "JAZZ", "JAPANESE", "GLOBAL", "FRANCH", "BLUE", "DANCE",
            "LATIN", "PUNK", "COUNTRY", "MUSICAL", "YINGSHI", "HOT_SONG"
        ]
        XCTAssertEqual(scenes.count, 43)
        XCTAssertEqual(MusicNeteaseHomeActionRequest.scenes, Set(scenes))
        let f = try MusicNeteaseNativeFixture(); try await f.enable()
        let path = "/eapi/v1/radio/get", binding = try await f.service.nativeHomeBinding()
        await f.http.replies(path, [try MusicNeteaseFixture.response(["code": 200, "data": [MusicNeteaseFixture.song()]])])
        let modes: [(MusicHomeAction.FMMode, String)] = [
            (.default, "DEFAULT"), (.familiar, "FAMILIAR"), (.explore, "EXPLORE"), (.puzzle, "PUZZLE_MODE_RCMD")
        ]
        let actions = modes.map { MusicHomeAction.fm(mode: $0.0, submode: "", entry: nil) } +
            scenes.map { MusicHomeAction.fm(mode: .scene, submode: $0, entry: .channel) }
        for action in actions { _ = try await f.service.nativeFM(action, preview: false, binding: binding) }
        let records = await f.http.recorded(), requests = records.filter { $0.request.url.path == path }
        XCTAssertEqual(requests.count, actions.count)
        let expected = modes.map { ($0.1, "") } + scenes.map { ("SCENE_RCMD", $0) }
        for (request, wire) in zip(requests, expected) {
            let payload = try MusicQQWire.decode(XCTUnwrap(request.payload))
            XCTAssertEqual(payload["mode"] as? String, wire.0)
            XCTAssertEqual(payload["subMode"] as? String, wire.1)
            XCTAssertEqual(payload["limit"] as? String, "3")
            XCTAssertEqual(payload["unplaySongIds"] as? String, "[]"); XCTAssertNil(payload["unplaySonIds"])
            XCTAssertEqual(payload["openAidj"] as? String, "false"); XCTAssertEqual(payload["aidjReqTimes"] as? String, "0")
            XCTAssertNil(payload["sourceIds"])
        }
        _ = await f.service.close()
    }
    func testRuleAndOfficialTabSelectorsStaySeparateOnWire() async throws {
        let f = try MusicNeteaseNativeFixture(); try await f.enable()
        let path = "/eapi/tag/tab/fm/recommends", binding = try await f.service.nativeHomeBinding()
        await f.http.replies(path, [try MusicNeteaseFixture.response(["code": 200, "data": [MusicNeteaseFixture.song()]])])
        for (rule, tab) in [("40000", ""), ("", "electronic")] {
            _ = try await f.service.nativeFM(.ruleFM(ruleID: rule, tabTagID: tab, sourceName: "合成频道"),
                                           preview: false, binding: binding)
        }
        let records = await f.http.recorded(), requests = records.filter { $0.request.url.path == path }
        XCTAssertEqual(requests.count, 2)
        for (request, expected) in zip(requests, [("40000", ""), ("", "electronic")]) {
            let payload = try MusicQQWire.decode(XCTUnwrap(request.payload))
            XCTAssertEqual(payload["ruleId"] as? String, expected.0)
            XCTAssertEqual(payload["tabTagId"] as? String, expected.1)
            XCTAssertEqual(payload["ruleType"] as? Int, 1); XCTAssertEqual(payload["limit"] as? Int, 5)
        }
        _ = await f.service.close()
    }
    func testGuestChannelPreviewUsesOneSongNoRegistrationAndNoLoginOrMembership() async throws {
        let f = try MusicNeteaseNativeFixture(restored: false); try await f.enable()
        let path = "/eapi/v1/radio/get"
        var song = MusicNeteaseFixture.song()
        song["al"] = ["id": 301, "name": "专辑", "picUrl": "http://p1.music.126.net/synthetic.jpg"]
        await f.http.replies(path, [try MusicNeteaseFixture.response(["code": 200, "data": [song, MusicNeteaseFixture.song(102)]])])
        let binding = try await f.service.nativeHomeBinding()
        XCTAssertFalse(binding.loggedIn)
        let result = try await f.service.nativeFM(fm, preview: true, binding: binding)
        XCTAssertEqual(result.tracks.count, 1)
        XCTAssertEqual(result.artwork?.absoluteString, "https://p1.music.126.net/synthetic.jpg")
        let calls = await f.http.recorded(), request = try XCTUnwrap(calls.last)
        XCTAssertEqual(calls.map { $0.request.url.path }, [path])
        let payload = try MusicQQWire.decode(XCTUnwrap(request.payload))
        XCTAssertEqual(payload["limit"] as? String, "1"); XCTAssertEqual(payload["mode"] as? String, "SCENE_RCMD")
        XCTAssertEqual(payload["subMode"] as? String, "ELECTRONIC"); XCTAssertNil(payload["sourceIds"])
        XCTAssertFalse(request.request.headers["Cookie"]?.contains("MUSIC_U") == true)
        XCTAssertTrue(MusicNeteaseHomeActionRoute.permits(request.request))
        _ = await f.service.close()
    }
    func testNativeHomePreviewAndNextBatchUseThreeButRuleCoverNeverReturnsSongs() async throws {
        let f = try MusicNeteaseNativeFixture(); try await f.enable()
        let binding = try await f.service.nativeHomeBinding()
        await f.http.replies("/eapi/v1/radio/get", [try MusicNeteaseFixture.response([
            "code": 200, "data": (101...103).map(MusicNeteaseFixture.song)
        ])])
        let home = MusicHomeAction.fm(mode: .familiar, submode: "", entry: nil)
        let first = try await f.service.nativeFM(home, preview: true, binding: binding)
        let next = try await f.service.nativeFM(fm, preview: false, binding: binding)
        XCTAssertEqual(first.tracks.count, 3); XCTAssertEqual(next.tracks.count, 3)
        await f.http.replies("/eapi/tag/tab/fm/cover", [try MusicNeteaseFixture.response([
            "code": 200, "data": ["cover": "https://p1.music.126.net/rule.jpg", "songs": [MusicNeteaseFixture.song()]]
        ])])
        await f.http.replies("/eapi/tag/tab/fm/recommends", [try MusicNeteaseFixture.response([
            "code": 200, "data": (201...205).map(MusicNeteaseFixture.song)
        ])])
        let rule = MusicHomeAction.ruleFM(ruleID: "", tabTagID: "17001", sourceName: "频道")
        let cover = try await f.service.nativeFM(rule, preview: true, binding: binding)
        XCTAssertTrue(cover.tracks.isEmpty); XCTAssertNotNil(cover.artwork)
        let batch = try await f.service.nativeFM(rule, preview: false, binding: binding)
        XCTAssertEqual(batch.tracks.count, 5)
        let records = await f.http.recorded()
        for record in records where record.request.url.path == "/eapi/v1/radio/get" {
            XCTAssertEqual(try MusicQQWire.decode(XCTUnwrap(record.payload))["limit"] as? String, "3")
        }
        let coverCall = try XCTUnwrap(records.first { $0.request.url.path == "/eapi/tag/tab/fm/cover" })
        let data = try MusicQQWire.decode(XCTUnwrap(coverCall.payload))
        XCTAssertEqual(data["ruleType"] as? Int, 1); XCTAssertEqual(data["ruleId"] as? String, "")
        XCTAssertEqual(data["tabTagId"] as? String, "17001"); XCTAssertNil(data["limit"])
        XCTAssertEqual(data["firstSongId"] as? String, ""); XCTAssertEqual(data["firstSongAlg"] as? String, "")
        _ = await f.service.close()
    }
    func testInvalidModeAndStaleBindingCannotAcquireAnotherRadioOrGuestFallback() async throws {
        let f = try MusicNeteaseNativeFixture(restored: false); try await f.enable()
        let binding = try await f.service.nativeHomeBinding()
        do {
            _ = try await f.service.nativeFM(.fm(mode: .scene, submode: "UNKNOWN", entry: .channel), preview: true, binding: binding)
            XCTFail("invalid scene")
        } catch { XCTAssertEqual(error as? MusicServiceError, .invalidRequest) }
        let calls = await f.http.recorded(); XCTAssertTrue(calls.isEmpty)
        _ = try await f.service.setAccess(available: false, generation: 2)
        do { _ = try await f.service.nativeFM(fm, preview: true, binding: binding); XCTFail("revoked binding") }
        catch { XCTAssertFalse(binding.authorization.isActive) }
        _ = await f.service.close()
    }
    func testWrappedFMAlgorithmsSurviveMetadataHydrationWithoutInventingWireSource() async throws {
        let f = try MusicNeteaseNativeFixture(); try await f.enable()
        let binding = try await f.service.nativeHomeBinding()
        await f.http.replies("/eapi/v1/radio/get", [try MusicNeteaseFixture.response(["code": 200, "data": [
            ["songInfo": ["id": 101], "alg": "outer-101"],
            ["songInfo": MusicNeteaseFixture.song(102), "alg": "outer-102"]
        ]])])
        await f.http.replies("/weapi/v3/song/detail", [try MusicNeteaseFixture.response([
            "code": 200, "songs": [MusicNeteaseFixture.song(101)]
        ])])
        let batch = try await f.service.nativeFM(fm, preview: false, binding: binding)
        let rows = try batch.tracks.map { try MusicQQWire.decode($0.data) }
        let origins = try rows.map { try MusicQQWire.object($0["_recommendation"]) }
        XCTAssertEqual(origins.compactMap { $0["alg"] as? String }, ["outer-101", "outer-102"])
        XCTAssertTrue(origins.allSatisfy { $0["source"] as? String == "fm" && $0["feedbackSource"] as? String == "userfm" })
        XCTAssertTrue(rows.allSatisfy { ($0["name"] as? String)?.isEmpty == false })
        _ = await f.service.close()
    }

    func testRuleFMOmitsUnprovenDefaultAndPreservesExplicitPerRowSource() async throws {
        let f = try MusicNeteaseNativeFixture(); try await f.enable()
        let binding = try await f.service.nativeHomeBinding()
        await f.http.replies("/eapi/tag/tab/fm/recommends", [try MusicNeteaseFixture.response(["code": 200, "data": [
            ["songInfo": MusicNeteaseFixture.song(101)],
            ["songInfo": ["id": 102], "source": "rule-source", "sourceId": 502, "alg": "rule-alg"]
        ]])])
        var metadata = MusicNeteaseFixture.song(102)
        metadata["source"] = "metadata-ignored"; metadata["sourceId"] = "metadata-ignored"; metadata["alg"] = "metadata-ignored"
        await f.http.replies("/weapi/v3/song/detail", [try MusicNeteaseFixture.response([
            "code": 200, "songs": [metadata]
        ])])
        let result = try await f.service.nativeFM(.ruleFM(ruleID: "40000", tabTagID: "", sourceName: "合成频道"),
                                                 preview: false, binding: binding)
        let rows = try result.tracks.map { try MusicQQWire.decode($0.data) }
        let origins = try rows.map { try MusicQQWire.object($0["_recommendation"]) }
        XCTAssertEqual(origins.compactMap { $0["songId"] as? String }, ["101", "102"])
        XCTAssertEqual(origins.compactMap { $0["feedbackSource"] as? String }, ["", "rule-source"])
        XCTAssertEqual(origins.compactMap { $0["feedbackSourceId"] as? String }, ["", "502"])
        XCTAssertEqual(origins.compactMap { $0["alg"] as? String }, ["", "rule-alg"])
        XCTAssertTrue(origins.allSatisfy { $0["source"] as? String == "fm" && $0["sourceId"] as? String == "" })
        XCTAssertTrue(rows.allSatisfy { ($0["name"] as? String)?.isEmpty == false })
        _ = await f.service.close()
    }

    func testDailyStyleAlgorithmCannotReflectCredentialsIntoQueuePayload() async throws {
        let f = try MusicNeteaseNativeFixture(); try await f.enable()
        let lease = try await f.service.beginAccountQueue(platform: .netease)
        await f.http.replies("/xeapi/homepage/category/daily/song/list", [try MusicNeteaseFixture.response([
            "code": 200, "data": ["dailySongs": [["songInfo": MusicNeteaseFixture.song(), "alg": "synthetic-user-42"]]]
        ])])
        do {
            _ = try await f.service.homeActionPage(lease, action: .daily(branch: .style), cursor: nil)
            XCTFail("reflected credential cannot become playback provenance")
        } catch { XCTAssertEqual(error as? MusicServiceError, .upstreamInvalid) }
        _ = await f.service.close()
    }

    func testActualPerRowSourcesSurviveFMPreviewAndMetadataHydrationWithoutChangingDefaults() async throws {
        for preview in [true, false] {
            let f = try MusicNeteaseNativeFixture(); try await f.enable()
            let binding = try await f.service.nativeHomeBinding()
            var nested = MusicNeteaseFixture.song(102)
            nested["source"] = "song-source"; nested["sourceId"] = 502; nested["alg"] = "song-alg"
            await f.http.replies("/eapi/v1/radio/get", [try MusicNeteaseFixture.response(["code": 200, "data": [
                ["songInfo": ["id": 101, "source": "nested-ignored", "sourceId": "nested-ignored"],
                 "source": "row-source", "sourceId": "row-id", "alg": "row-alg"],
                ["song": nested], ["songInfo": MusicNeteaseFixture.song(103)]
            ]])])
            var metadata = MusicNeteaseFixture.song(101)
            metadata["source"] = "metadata-ignored"; metadata["sourceId"] = "metadata-ignored"; metadata["alg"] = "metadata-ignored"
            await f.http.replies("/weapi/v3/song/detail", [try MusicNeteaseFixture.response([
                "code": 200, "songs": [metadata]
            ])])
            let result = try await f.service.nativeFM(.fm(mode: .familiar, submode: "", entry: nil),
                                                     preview: preview, binding: binding)
            let rows = try result.tracks.map { try MusicQQWire.decode($0.data) }
            let origins = try rows.map { try MusicQQWire.object($0["_recommendation"]) }
            XCTAssertEqual(origins.compactMap { $0["feedbackSource"] as? String }, ["row-source", "song-source", "userfm"])
            XCTAssertEqual(origins.compactMap { $0["feedbackSourceId"] as? String }, ["row-id", "502", ""])
            XCTAssertEqual(origins.compactMap { $0["alg"] as? String }, ["row-alg", "song-alg", ""])
            XCTAssertTrue(origins.allSatisfy { $0["source"] as? String == "fm" && $0["sourceId"] as? String == "" })
            XCTAssertTrue(rows.allSatisfy { ($0["name"] as? String)?.isEmpty == false })
            _ = await f.service.close()
        }
    }

    func testDailyStyleSourcesCannotReflectCredentialsIntoPublicQueuePayload() async throws {
        for field in ["source", "sourceId"] {
            let f = try MusicNeteaseNativeFixture(); try await f.enable()
            let lease = try await f.service.beginAccountQueue(platform: .netease)
            var raw: [String: Any] = ["songInfo": MusicNeteaseFixture.song()]
            raw[field] = "synthetic-user-42"
            await f.http.replies("/xeapi/homepage/category/daily/song/list", [try MusicNeteaseFixture.response([
                "code": 200, "data": ["dailySongs": [raw]]
            ])])
            do {
                _ = try await f.service.homeActionPage(lease, action: .daily(branch: .style), cursor: nil)
                XCTFail("Reflected credential in \(field) cannot become public playback provenance")
            } catch { XCTAssertEqual(error as? MusicServiceError, .upstreamInvalid) }
            _ = await f.service.close()
        }
    }

    func testFMCoverRejectsUntrustedURLAndReflectedCredentials() async throws {
        let f = try MusicNeteaseNativeFixture(); try await f.enable()
        let binding = try await f.service.nativeHomeBinding()
        let rule = MusicHomeAction.ruleFM(ruleID: "40000", tabTagID: "", sourceName: "")
        // FM covers use the shared host-agnostic artwork URL contract; unsafe
        // URL forms and reflected credentials remain rejected.
        await f.http.replies("/eapi/tag/tab/fm/cover", [try MusicNeteaseFixture.response(["code": 200, "data": ["cover": "https://other-cdn.invalid/cover"]])])
        let accepted = try await f.service.nativeFM(rule, preview: true, binding: binding)
        XCTAssertEqual(accepted.artwork, URL(string: "https://other-cdn.invalid/cover")); XCTAssertTrue(accepted.tracks.isEmpty)
        for url in ["https://user@p1.music.126.net/cover", "https://p1.music.126.net:444/cover"] {
            await f.http.replies("/eapi/tag/tab/fm/cover", [try MusicNeteaseFixture.response(["code": 200, "data": ["cover": url]])])
            let value = try await f.service.nativeFM(rule, preview: true, binding: binding)
            XCTAssertNil(value.artwork); XCTAssertTrue(value.tracks.isEmpty)
        }
        await f.http.replies("/eapi/tag/tab/fm/cover", [try MusicNeteaseFixture.response([
            "code": 200, "data": ["cover": "https://p1.music.126.net/synthetic-user-42"]
        ])])
        do { _ = try await f.service.nativeFM(rule, preview: true, binding: binding); XCTFail("credential reflection") }
        catch { XCTAssertEqual(error as? MusicServiceError, .upstreamInvalid) }
        _ = await f.service.close()
    }
}
