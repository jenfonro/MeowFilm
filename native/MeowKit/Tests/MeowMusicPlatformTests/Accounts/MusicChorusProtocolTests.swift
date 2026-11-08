import Foundation
import XCTest
import MeowMusicFeature
@testable import MeowMusicPlatform

final class MusicChorusProtocolTests: XCTestCase {
    func testNeteaseRequiresOneExactRecordingAndConvertsMilliseconds() throws {
        let row: [String: Any] = ["id": 101, "startTime": 71_200, "endTime": 122_200]
        let point = try XCTUnwrap(MusicChorusWire.netease(["chorus": [row]], id: "101"))
        XCTAssertEqual(point.start, 71.2); XCTAssertEqual(point.end, 122.2)
        XCTAssertNil(try MusicChorusWire.netease(["chorus": [row]], id: "102"))
        XCTAssertNil(try MusicChorusWire.netease(["chorus": [row, row]], id: "101"))
        XCTAssertTrue(point.fits(recordingDuration: 180, actualDuration: 180))
        XCTAssertFalse(point.fits(recordingDuration: 180, actualDuration: 30))
        XCTAssertFalse(point.fits(recordingDuration: 120, actualDuration: 120))
        XCTAssertFalse(point.fits(recordingDuration: 180, actualDuration: .nan))
    }

    func testKugouRequiresExactHashAndClimaxLengthNotTrackLength() throws {
        let hash = String(repeating: "A", count: 32)
        var row: [String: Any] = ["hash": hash, "start_time": "71200", "end_time": "122200", "timelength": "51000"]
        func payload(_ rows: [[String: Any]]) -> [String: Any] {
            ["status": 1, "error_code": 0, "errcode": 0, "error": false, "data": rows]
        }
        let point = try XCTUnwrap(MusicChorusWire.kugou(payload([row]), hash: hash))
        XCTAssertEqual(point.start, 71.2); XCTAssertEqual(point.end, 122.2)
        XCTAssertNil(try MusicChorusWire.kugou(payload([row]), hash: String(repeating: "B", count: 32)))
        XCTAssertNil(try MusicChorusWire.kugou(payload([row, row]), hash: hash))
        row["timelength"] = "180000"
        XCTAssertNil(try MusicChorusWire.kugou(payload([row]), hash: hash))
    }

    func testGuestChorusUsesAnonymousBindingAndCacheButNoMembership() async throws {
        let f = try MusicNeteaseNativeFixture(restored: false); try await f.enable()
        let options = try await f.service.nativeOptions(platform: .netease, nativeID: "101", sourceKey: "wy:101")
        let selection = options[0].selection(quality: "128k")
        _ = try await f.service.nativeResolve(platform: .netease, nativeID: "101", selection: selection)
        let cold = try await f.service.nativeChorus(nativeID: "101", selection: selection,
            recordingDuration: 180, actualDuration: 180, cachedOnly: true)
        XCTAssertNil(cold, "A real provider identity on a file is not permission for network metadata")
        await f.http.replies("/weapi/song/chorus", [try MusicNeteaseFixture.response([
            "code": 200, "chorus": [["id": 101, "startTime": 71_200, "endTime": 122_200]]])])
        let point = try await f.service.nativeChorus(nativeID: "101", selection: selection,
                                                     recordingDuration: 180, actualDuration: 180, cachedOnly: false)
        XCTAssertEqual(point?.start, 71.2)
        _ = try await f.service.nativeChorus(nativeID: "101", selection: selection,
                                           recordingDuration: 180, actualDuration: 180, cachedOnly: true)
        let calls = await f.http.recorded()
        XCTAssertEqual(calls.filter { $0.request.url.path == "/weapi/song/chorus" }.count, 1)
        XCTAssertFalse(calls.contains {
            $0.request.url.path.contains("vip") || $0.request.url.path.contains("nuser") ||
                $0.request.url.path.contains("/register/")
        })
        _ = await f.service.close()
    }
    func testPublicChorusDoesNotNeedXEAPIHandshakeOrAnonymousBootstrap() async throws {
        let clock = MusicAccountTestClock()
        let http = MusicNeteaseTestHTTP(clock: clock)
        await http.replies("/weapi/song/chorus", [try MusicNeteaseFixture.response([
            "code": 200, "chorus": [["id": 101, "startTime": 12_000, "endTime": 30_000]]])])
        let adapter = NeteaseMusicAccountAdapter(transport: http, now: { clock.now() })
        let point = try await adapter.publicChorus(nativeID: "101")
        XCTAssertEqual(point?.start, 12)
        let calls = await http.recorded()
        XCTAssertEqual(calls.map(\.request.url.path), ["/weapi/song/chorus"])
        XCTAssertTrue(calls.allSatisfy { !($0.request.headers["Cookie"] ?? "").contains("MUSIC_U") })
    }
}
