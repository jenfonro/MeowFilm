import XCTest
import Foundation
import MeowMusicFeature
import MeowMusicPlatform

private actor NativeQQHTTP: MusicAccountHTTPTransport {
    private var seen: [MusicAccountHTTPRequest] = []
    private var wrongLyricID = false
    func mismatch() { wrongLyricID = true }
    func calls() -> [MusicAccountHTTPRequest] { seen }
    func send(_ request: MusicAccountHTTPRequest) throws -> MusicAccountHTTPResponse {
        seen.append(request)
        let object: [String: Any]
        if request.url.host == "api.tencentmusic.com" {
            let inner: [String: Any] = ["code": 0, "data": ["q16": "synthetic-q16", "q36": "synthetic-q36"]]
            object = ["code": 0, "data": String(decoding: try JSONSerialization.data(withJSONObject: inner), as: UTF8.self)]
        } else {
            let body = try XCTUnwrap(JSONSerialization.jsonObject(with: request.body ?? Data()) as? [String: Any])
            let rpc = try XCTUnwrap(body["req_0"] as? [String: Any]), method = rpc["method"] as? String
            let song: [String: Any] = ["mid": "SyntheticMID", "id": "9007199254740993", "type": 1, "title": "Synthetic QQ",
                "singer": [["name": "Fixture"]], "album": ["mid": "SyntheticAlbum", "name": "Album"], "interval": 180,
                "file": ["media_mid": "SyntheticMedia", "size_128mp3": 100, "size_hires": 500]]
            let data: [String: Any]
            switch method {
            case "GetSession": data = ["session": ["uid": "synthetic-anonymous", "sid": "synthetic-session"]]
            case "DoSearchForQQMusicMobile": data = ["meta": ["sum": 1, "nextpage": -1], "body": ["item_song": [song]]]
            case "CgiGetTrackInfo": data = ["tracks": [song]]
            case "GetPlayLyricInfo":
                data = ["songID": wrongLyricID ? "99" : "9007199254740993", "lyric": "", "trans": "", "roma": ""]
            default: throw MusicServiceError.upstreamFailed
            }
            object = ["code": 0, "req_0": ["code": 0, "data": data]]
        }
        return .init(status: 200, data: try JSONSerialization.data(withJSONObject: object))
    }
}
final class NativeMusicQQDirectTests: XCTestCase {
    func testAnonymousQQCallsNativeDeviceSearchAndExactNumericLyricProtocol() async throws {
        let http = NativeQQHTTP(), publicQQ = NativeMusicQQPublicService(transport: http)
        let page = try await publicQQ.search(query: "synthetic", page: 1, limit: 30)
        XCTAssertEqual(page.items.count, 1)
        let row = try XCTUnwrap(JSONSerialization.jsonObject(with: page.items[0].data) as? [String: Any])
        XCTAssertEqual(row["qqSongId"] as? String, "9007199254740993")
        XCTAssertEqual(row["songmid"] as? String, "SyntheticMID")
        _ = try await publicQQ.lyrics(mid: "SyntheticMID")
        await http.mismatch()
        do { _ = try await publicQQ.lyrics(mid: "SyntheticMID"); XCTFail("numeric song ID mismatch") }
        catch { XCTAssertEqual(error as? MusicServiceError, .lyricIdentityMismatch) }
        let calls = await http.calls()
        XCTAssertTrue(calls.contains { $0.url.host == "api.tencentmusic.com" })
        for call in calls {
            XCTAssertNil(call.headers["Cookie"])
            XCTAssertFalse(String(decoding: call.body ?? Data(), as: UTF8.self).contains("music.native.accounts.invoke"))
        }
        await publicQQ.close()
    }
}
