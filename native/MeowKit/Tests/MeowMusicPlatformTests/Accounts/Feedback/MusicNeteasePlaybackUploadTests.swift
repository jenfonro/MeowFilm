import Foundation
import XCTest
import MeowMusicFeature
@testable import MeowMusicPlatform

@MainActor
final class MusicNeteasePlaybackUploadTests: XCTestCase {
    func testRecordsUseOriginalMillisecondsAndSeparateMediaAndWallSecondsWithoutFabricatedEnvironment() throws {
        let instance = UUID()
        let target = MusicPlaybackFeedbackTarget(platform: .netease, nativeID: "101",
            source: "search", sourceID: "真实关键词", algorithm: "actual-alg")
        let start = MusicPlaybackFeedbackReport(instanceID: instance, target: target, event: .start,
            duration: 0, listened: 0, mediaListened: 0, position: 0, rate: 32,
            startedAt: 1_800_000_000.125, occurredAt: 1_800_000_000.125)
        let end = MusicPlaybackFeedbackReport(instanceID: instance, target: target, event: .end,
            duration: 0, listened: 3.75, mediaListened: 7.5, position: 500_000, rate: 2, reason: .ui,
            startedAt: 1_800_000_000.125, occurredAt: 1_800_000_004.125)
        for (index, value) in [start, end].enumerated() {
            XCTAssertNoThrow(try MusicAccountFeedbackWire.validate(value))
            let line = String(decoding: try MusicAccountFeedbackNeteaseWire.records(value), as: UTF8.self)
            let parts = line.split(separator: "\u{1}", omittingEmptySubsequences: false)
            XCTAssertEqual(parts.count, 3)
            XCTAssertEqual(parts[0], index == 0 ? "1800000000" : "1800000004")
            XCTAssertEqual(parts[1], index == 0 ? "_plv" : "_pld")
            XCTAssertTrue(line.hasSuffix("\n")); XCTAssertEqual(line.filter { $0 == "\n" }.count, 1)
            let fields = try XCTUnwrap(JSONSerialization.jsonObject(with: Data(parts[2].utf8)) as? [String: Any])
            XCTAssertEqual(fields["id"] as? Int, 101)
            XCTAssertEqual(fields["type"] as? String, "song")
            XCTAssertEqual(fields["_logTime"] as? Int64, index == 0 ? 1_800_000_000_125 : 1_800_000_004_125)
            XCTAssertEqual(fields["time"] as? Int, index == 0 ? 0 : 7)
            XCTAssertEqual(fields["realtime"] as? Int, index == 0 ? 0 : 3)
            XCTAssertEqual(fields["source"] as? String, "search")
            XCTAssertEqual(fields["sourceId"] as? String, "真实关键词")
            XCTAssertEqual(fields["alg"] as? String, "actual-alg")
            if index == 0 { XCTAssertNil(fields["end"]) }
            else { XCTAssertEqual(fields["end"] as? String, "ui") }
            for absent in ["mainsite", "mainsiteWeb", "refer", "deviceId", "wifi", "osver", "resolution"] {
                XCTAssertNil(fields[absent], absent)
            }
        }
        let long = MusicPlaybackFeedbackReport(instanceID: instance, target: target, event: .end,
            duration: 0, listened: 90_000, mediaListened: 2_880_000, position: 2_880_000, rate: 32, reason: .interrupt,
            startedAt: 1_800_000_000, occurredAt: 1_800_090_000)
        XCTAssertNoThrow(try MusicAccountFeedbackWire.validate(long))
        XCTAssertNoThrow(try MusicAccountFeedbackNeteaseWire.records(long))
    }
    func testOnlyNumericCode200AndExactSubmittedFilenameConfirmUpload() throws {
        let filename = "synthetic.log"
        let bad: [[String: Any]] = [
            [:], ["code": true], ["code": "200"], ["code": 200.5],
            ["code": 401, "data": ["successfiles": [filename]]],
            ["code": 200], ["code": 200, "data": ["successfiles": []]],
            ["code": 200, "data": ["successfiles": ["other.log"]]],
            ["code": 200, "data": ["successfiles": [filename + ".extra"]]],
            ["code": 200, "data": ["successfiles": [1]]]
        ]
        for body in bad {
            let response = try MusicNeteaseFixture.response(body)
            XCTAssertFalse((try? MusicAccountFeedbackNeteaseWire.confirmed(response, filename: filename)) ?? false)
        }
        let valid = try MusicNeteaseFixture.response(["code": 200, "data": ["successfiles": [filename]]])
        XCTAssertTrue(try MusicAccountFeedbackNeteaseWire.confirmed(valid, filename: filename))
        XCTAssertFalse(try MusicAccountFeedbackNeteaseWire.confirmed(.init(status: 201, data: valid.data), filename: filename))
        XCTAssertThrowsError(try MusicAccountFeedbackNeteaseWire.confirmed(.init(status: 200, data: Data([0xff])), filename: filename))
    }
    func testRealAdapterUsesSingleMultipartNCBLUploadAndNeverImportsResponseCookies() async throws {
        let peer = PlaybackUploadPeer(), clock = MusicAccountTestClock()
        let adapter = NeteaseMusicAccountAdapter(transport: peer, now: { clock.now() })
        let credential = try MusicNeteaseFixture.credential()
        let instance = UUID(), authorization = MusicPlaybackFeedbackAuthorization()
        let start = try await adapter.feedback(MusicAccountFeedbackFixture.report(.start, platform: .netease, instance: instance),
            credential: credential, session: nil, guard: authorization)
        let end = try await adapter.feedback(MusicAccountFeedbackFixture.report(.end, platform: .netease, instance: instance),
            credential: credential, session: nil, guard: authorization)
        XCTAssertEqual(start.receipt, .confirmed); XCTAssertEqual(end.receipt, .confirmed)
        let requests = await peer.requests()
        XCTAssertEqual(requests.count, 2)
        for request in requests {
            XCTAssertEqual(request.url.absoluteString,
                "https://clientlog3.music.163.com/api/clientlog/encrypt/upload?multiupload=true")
            XCTAssertEqual(request.method, "POST"); XCTAssertEqual(request.maximumBytes, 65_536)
            XCTAssertTrue(MusicAccountFeedbackRoute.permits(request))
            XCTAssertEqual(request.headers["Cookie"], "MUSIC_U=synthetic-user-42; __csrf=synthetic-csrf")
            XCTAssertEqual(request.headers["User-Agent"], "Meowfilm")
            XCTAssertEqual(request.headers["Cache-Control"], "no-store")
            let body = try XCTUnwrap(request.body)
            let headerEnd = try XCTUnwrap(body.range(of: Data("\r\n\r\n".utf8)))
            XCTAssertEqual(body[headerEnd.upperBound..<(headerEnd.upperBound + 4)], Data("NCBL".utf8))
            XCTAssertTrue(String(decoding: body[..<headerEnd.lowerBound], as: UTF8.self)
                .contains("Content-Disposition: form-data; name=\"file\"; filename=\""))
            let filename = try MusicAccountFeedbackFixture.uploadFilename(request)
            XCTAssertTrue(filename.hasPrefix("play_"))
            XCTAssertNotNil(UUID(uuidString: String(filename.dropFirst(5))))
        }
        XCTAssertNotEqual(try MusicAccountFeedbackFixture.uploadFilename(requests[0]),
                          try MusicAccountFeedbackFixture.uploadFilename(requests[1]))
        // A revoked account/setting authorization never enters the adapter.
        authorization.revoke()
        do {
            _ = try await adapter.feedback(MusicAccountFeedbackFixture.report(.start, platform: .netease, instance: UUID()),
                credential: credential, session: nil, guard: authorization)
            XCTFail("Revoked upload")
        } catch { XCTAssertEqual(error as? MusicServiceError, .cancelled) }
        let after = await peer.requests(); XCTAssertEqual(after.count, 2)
    }
    func testUploadMetadataContainsOnlyOriginalMusicUAndCookieValuesAreNotReencodedOrSynthesized() throws {
        let records = Data("original-playback-records".utf8)
        let cookies = ["MUSIC_U": "synthetic+original/key=", "__csrf": "synthetic-csrf"]
        var inputs: [Data] = []
        let request = try MusicAccountFeedbackNeteaseWire.request(records: records, cookies: cookies,
            filename: "play_synthetic") { metadata, payload in
                inputs = [metadata, payload]
                return Data("encoded-file".utf8)
            }
        XCTAssertEqual(inputs.count, 2)
        let metadata = try MusicQQWire.decode(inputs[0])
        XCTAssertEqual(Set(metadata.keys), ["MUSIC_U"])
        XCTAssertEqual(metadata["MUSIC_U"] as? String, cookies["MUSIC_U"])
        XCTAssertEqual(inputs[1], records)
        XCTAssertEqual(request.headers["Cookie"], "MUSIC_U=synthetic+original/key=; __csrf=synthetic-csrf")
        XCTAssertEqual(request.headers["User-Agent"], "Meowfilm")
        XCTAssertTrue(String(decoding: try XCTUnwrap(request.body), as: UTF8.self).contains("\r\n\r\nencoded-file\r\n--"))
    }
    func testHeartbeatSceneKeepsActualUpstreamSourceAndFMUsesUserfm() throws {
        for (kind, source, sourceID) in [
            (MusicPlaybackFeedbackOrigin.Kind.fm, "userfm", ""),
            (.heartbeat, "", ""), (.heartbeat, "upstream-source", "actual-source-id"),
            (.heartbeat, "", "upstream-id-without-source")
        ] {
            let target = MusicPlaybackFeedbackTarget(platform: .netease, nativeID: "101",
                source: source, sourceID: sourceID, algorithm: "actual-alg",
                origin: .init(kind: kind, platform: .netease, sourceID: "501"))
            let report = MusicPlaybackFeedbackReport(instanceID: UUID(), target: target, event: .start,
                duration: 0, listened: 0, mediaListened: 0, position: 0, rate: 1,
                startedAt: 1_800_000_000, occurredAt: 1_800_000_000)
            let line = String(decoding: try MusicAccountFeedbackNeteaseWire.records(report), as: UTF8.self)
            let json = try XCTUnwrap(line.split(separator: "\u{1}").last)
            let fields = try XCTUnwrap(JSONSerialization.jsonObject(with: Data(json.utf8)) as? [String: Any])
            XCTAssertEqual(fields["source"] as? String, source.isEmpty ? nil : source)
            XCTAssertEqual(fields["sourceId"] as? String, sourceID.isEmpty ? nil : sourceID)
            XCTAssertEqual(fields["play_scene"] as? String, kind == .heartbeat ? "heartbeat" : nil)
            XCTAssertEqual(fields["alg"] as? String, "actual-alg")
        }
    }
    func testOnlyConnectionBeforeSendCanBeRestoredAndTimeoutOrResponseNeverMeansNotSent() throws {
        let url = MusicAccountFeedbackNeteaseWire.uploadURL
        for code in [NSURLErrorNotConnectedToInternet, NSURLErrorCannotFindHost,
                     NSURLErrorCannotConnectToHost, NSURLErrorDNSLookupFailed] {
            let error = NSError(domain: NSURLErrorDomain, code: code)
            XCTAssertTrue(MusicAccountFeedbackNotSent.matches(error, url: url, bytesSent: 0, responseReceived: false))
            XCTAssertFalse(MusicAccountFeedbackNotSent.matches(error, url: url, bytesSent: 1, responseReceived: false))
            XCTAssertFalse(MusicAccountFeedbackNotSent.matches(error, url: url, bytesSent: 0, responseReceived: true))
            XCTAssertFalse(MusicAccountFeedbackNotSent.matches(error, url: try XCTUnwrap(URL(string: "https://stat6.y.qq.com/android/fcgi-bin/imusic_tj")),
                bytesSent: 0, responseReceived: false), "QQ's existing transport errors remain unchanged")
        }
        for code in [NSURLErrorTimedOut, NSURLErrorNetworkConnectionLost, NSURLErrorCancelled,
                     NSURLErrorCannotParseResponse, NSURLErrorSecureConnectionFailed] {
            XCTAssertFalse(MusicAccountFeedbackNotSent.matches(NSError(domain: NSURLErrorDomain, code: code),
                url: url, bytesSent: 0, responseReceived: false))
        }
    }
    func testOnlyExactNewUploadRouteIsAdmitted() throws {
        let good = MusicAccountHTTPRequest(url: MusicAccountFeedbackNeteaseWire.uploadURL, method: "POST")
        XCTAssertTrue(MusicAccountFeedbackRoute.permits(good))
        for url in [
            "http://clientlog3.music.163.com/api/clientlog/encrypt/upload?multiupload=true",
            "https://clientlog3.music.163.com/api/clientlog/encrypt/upload",
            "https://clientlog3.music.163.com/api/clientlog/encrypt/upload?multiupload=false",
            "https://clientlog3.music.163.com/api/clientlog/encrypt/upload?multiupload=true&other=1",
            "https://clientlog3.music.163.com/api/clientlog/encrypt/upload/v2?multiupload=true",
            "https://clientlog3.music.163.com.invalid/api/clientlog/encrypt/upload?multiupload=true"
        ] {
            XCTAssertFalse(MusicAccountFeedbackRoute.permits(.init(url: try XCTUnwrap(URL(string: url)), method: "POST")), url)
        }
        XCTAssertFalse(MusicAccountFeedbackRoute.permits(.init(url: good.url, method: "GET")))
    }
}

private actor PlaybackUploadPeer: MusicAccountHTTPTransport {
    private var values: [MusicAccountHTTPRequest] = []
    func requests() -> [MusicAccountHTTPRequest] { values }
    func send(_ request: MusicAccountHTTPRequest) throws -> MusicAccountHTTPResponse {
        values.append(request)
        let body = String(decoding: request.body ?? Data(), as: UTF8.self)
        let prefix = "filename=\""
        guard let range = body.range(of: prefix),
              let end = body[range.upperBound...].firstIndex(of: "\"") else { throw MusicServiceError.invalidRequest }
        let filename = String(body[range.upperBound..<end])
        return try MusicNeteaseFixture.response(["code": 200, "data": ["successfiles": [filename]]],
            headers: ["set-cookie": "MUSIC_U=synthetic-replacement-never-imported; Path=/"])
    }
}
