import Foundation
import XCTest
import MeowMusicFeature
@testable import MeowMusicPlatform

final class MusicNativeFMProtocolTests: XCTestCase {
    private let fm = MusicHomeAction.fm(mode: .scene, submode: "ELECTRONIC", entry: .channel)
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
        XCTAssertEqual(payload["limit"] as? Int, 1); XCTAssertEqual(payload["mode"] as? String, "SCENE_RCMD")
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
            XCTAssertEqual(try MusicQQWire.decode(XCTUnwrap(record.payload))["limit"] as? Int, 3)
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
