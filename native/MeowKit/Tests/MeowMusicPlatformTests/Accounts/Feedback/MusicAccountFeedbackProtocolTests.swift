import Foundation
import XCTest
import MeowAccounts
import MeowMusicFeature
@testable import MeowMusicPlatform

// Wire baseline: read-only meowmusic cc22a7ee, platform/{qq,netease}/feedback.go.
// No running reference program, upstream connection or real account is involved.
@MainActor final class MusicAccountFeedbackProtocolTests: XCTestCase {
    func testQQVIPTrackStillUsesExactVkeyAndOneGzippedDurationReport() async throws {
        let f = try MusicNativeFixture()
        try await f.enable()
        _ = try await f.service.validate(platform: .qq)
        let baseline = await f.http.recorded(), stored = f.store.peek(f.identity)
        let writes = f.store.calls().filter { $0 == "write" }.count
        let before = await f.accounts.snapshot(for: f.identity)
        var track = MusicAccountFixture.song()
        track["pay"] = ["pay_play": 1, "pay_down": 1]
        track["singer"] = [["id": 77, "name": "Synthetic"]]
        let filename = "M500" + MusicAccountFixture.mid + MusicAccountFixture.mid + ".mp3"
        await f.http.replies("CgiGetTrackInfo", try MusicAccountFixture.rpc(["tracks": [track]]))
        await f.http.replies("UrlGetVkey", try MusicAccountFixture.rpc(["midurlinfo": [[
            "songmid": MusicAccountFixture.mid, "filename": filename, "result": 0,
            "vkey": "synthetic-<&\"'>"
        ]]]))
        // A feedback implementation that adds a membership gate will fail here.
        await f.http.replies("vip_login_base", .init(status: 403, data: Data()))
        await f.http.replies("/android/fcgi-bin/imusic_tj", .init(status: 200, data: MusicAccountFeedbackFixture.qqAck))
        let lease = try await f.service.beginPlaybackFeedback(platform: .qq, authorization: .init()), instance = UUID()
        let start = try await lease.report(MusicAccountFeedbackFixture.report(.start, instance: instance))
        XCTAssertEqual(start, .registered)
        let afterStart = await f.http.recorded(); XCTAssertEqual(afterStart.count, baseline.count)
        let end = MusicAccountFeedbackFixture.report(.end, instance: instance)
        let receipt = try await lease.report(end), duplicate = try await lease.report(end)
        XCTAssertEqual(receipt, .confirmed); XCTAssertEqual(duplicate, .confirmed)
        let requests = Array((await f.http.recorded()).dropFirst(baseline.count))
        XCTAssertEqual(try requests.map(MusicAccountTestHTTP.method), ["CgiGetTrackInfo", "UrlGetVkey", "/android/fcgi-bin/imusic_tj"])
        let vkeyRequest = try XCTUnwrap(requests.first { (try? MusicAccountTestHTTP.method($0)) == "UrlGetVkey" })
        let rpc = try XCTUnwrap(MusicAccountTestHTTP.fields(vkeyRequest)["req_0"] as? [String: Any])
        let params = try XCTUnwrap(rpc["param"] as? [String: Any])
        XCTAssertEqual(rpc["module"] as? String, "music.vkey.GetVkey")
        XCTAssertEqual(params["filename"] as? [String], [filename])
        XCTAssertEqual(params["songmid"] as? [String], [MusicAccountFixture.mid])
        XCTAssertEqual(params["songtype"] as? [Int], [0]); XCTAssertEqual(params["uin"] as? String, "101")
        let request = try XCTUnwrap(requests.last)
        XCTAssertEqual(request.url.absoluteString, "https://stat6.y.qq.com/android/fcgi-bin/imusic_tj")
        XCTAssertEqual(request.method, "POST"); XCTAssertEqual(request.maximumBytes, 65536)
        XCTAssertEqual(request.headers["Content-Encoding"], "gzip")
        XCTAssertEqual(request.headers["Content-Type"], "application/octet-stream")
        XCTAssertEqual(request.headers["Cookie"], "uin=101; qqmusic_uin=101; qm_keyst=synthetic-account-key; qqmusic_key=synthetic-account-key")
        let bytes = try XCTUnwrap(request.body)
        XCTAssertEqual(Array(bytes.prefix(3)), [0x1f, 0x8b, 0x08])
        let xml = String(decoding: try MusicAccountFeedbackFixture.inflate(bytes, gzip: true), as: UTF8.self)
        let time = String(Int(f.clock.now().timeIntervalSince1970))
        let timekey = MusicAccountFeedbackFixture.md5(Data((time + "1" + "101" + "gk2$Lh-&l4#!4iow").utf8)).uppercased()
        for expected in ["<cid>228</cid>", "cmd=\"1\"", "QQ=\"101\"", "optime=\"\(time)\"",
                         "time=\"1\"", "play_duration_mi=\"1000\"", "audiotime=\"180000\"",
                         "songid=\"101\"", "singerid=\"77\"", "timekey=\"\(timekey)\"",
                         "vkey=\"synthetic-&lt;&amp;&quot;&apos;&gt;\""] {
            XCTAssertTrue(xml.contains(expected), expected)
        }
        XCTAssertFalse(xml.contains("https://")) // No media URL or audio download in this protocol.
        XCTAssertEqual(f.store.peek(f.identity)?.revision, stored?.revision)
        XCTAssertEqual(f.store.calls().filter { $0 == "write" }.count, writes)
        let after = await f.accounts.snapshot(for: f.identity); XCTAssertEqual(after.generation, before.generation)
        _ = await f.service.close()
    }

    func testNeteaseUsesSingleEAPIWeblogPerPhaseAndNeverImportsFeedbackCookiesOrReplaysBadAck() async throws {
        let f = try MusicNeteaseNativeFixture()
        try await f.enable(); _ = try await f.service.validate(platform: .netease)
        let baseline = await f.http.recorded(), stored = f.store.peek(f.identity)
        let writes = f.store.calls().filter { $0 == "write" }.count
        let path = "/eapi/feedback/weblog"
        await f.http.replies(path, [try MusicNeteaseFixture.response(["code": 200],
            headers: ["set-cookie": "MUSIC_U=synthetic-feedback-replacement; Path=/; HttpOnly"])])
        let lease = try await f.service.beginPlaybackFeedback(platform: .netease, authorization: .init()), instance = UUID()
        let start = MusicAccountFeedbackFixture.report(.start, platform: .netease, instance: instance)
        let end = MusicAccountFeedbackFixture.report(.end, platform: .netease, instance: instance)
        let started = try await lease.report(start), finished = try await lease.report(end)
        let duplicate = try await lease.report(end)
        XCTAssertEqual(started, .confirmed); XCTAssertEqual(finished, .confirmed); XCTAssertEqual(duplicate, .confirmed)
        let records = Array((await f.http.recorded()).dropFirst(baseline.count))
        XCTAssertEqual(records.count, 2)
        for (index, record) in records.enumerated() {
            XCTAssertEqual(record.request.url.absoluteString, "https://clientlog.music.163.com/eapi/feedback/weblog")
            XCTAssertEqual(record.request.method, "POST"); XCTAssertEqual(record.request.maximumBytes, 65536)
            XCTAssertEqual(record.request.headers["Cache-Control"], "no-store")
            XCTAssertTrue(record.request.headers["Cookie"]?.contains("MUSIC_U=synthetic-user-42") == true)
            XCTAssertFalse(record.request.headers["Cookie"]?.contains("synthetic-feedback-replacement") == true)
            // Existing peer independently decrypts EAPI AES and verifies the
            // embedded /api path + MD5, not production eapi() as its oracle.
            let payload = try MusicQQWire.decode(XCTUnwrap(record.payload))
            XCTAssertEqual(payload["e_r"] as? Bool, false)
            let logs = try XCTUnwrap(payload["logs"] as? String)
            let rows = try XCTUnwrap(JSONSerialization.jsonObject(with: Data(logs.utf8)) as? [[String: Any]])
            XCTAssertEqual(rows.count, 1)
            let row = try XCTUnwrap(rows.first), fields = try XCTUnwrap(row["json"] as? [String: Any])
            XCTAssertEqual(row["action"] as? String, index == 0 ? "startplay" : "play")
            XCTAssertEqual(fields["id"] as? String, "101"); XCTAssertEqual(fields["type"] as? String, "song")
            XCTAssertEqual(fields["source"] as? String, "list"); XCTAssertEqual(fields["sourceId"] as? String, "501")
            XCTAssertEqual(fields["alg"] as? String, "synthetic-alg")
            if index == 0 { XCTAssertNil(fields["time"]); XCTAssertNil(fields["end"]) }
            else {
                XCTAssertEqual(fields["time"] as? Int, 1); XCTAssertEqual(fields["end"] as? String, "ui")
                XCTAssertEqual(fields["download"] as? Int, 0); XCTAssertEqual(fields["wifi"] as? Int, 0)
            }
        }
        await f.http.replies(path, [try MusicNeteaseFixture.response(["code": false])])
        let bad = try await f.service.beginPlaybackFeedback(platform: .netease, authorization: .init()), badID = UUID()
        let badStart = MusicAccountFeedbackFixture.report(.start, platform: .netease, instance: badID)
        do { _ = try await bad.report(badStart); XCTFail("Boolean is not an acknowledgement code") }
        catch { XCTAssertEqual(error as? MusicServiceError, .upstreamInvalid) }
        let repeated = try await bad.report(badStart)
        let absentEnd = try await bad.report(MusicAccountFeedbackFixture.report(.end, platform: .netease, instance: badID))
        XCTAssertEqual(repeated, .unconfirmed); XCTAssertEqual(absentEnd, .unconfirmed)
        let after = Array((await f.http.recorded()).dropFirst(baseline.count))
        XCTAssertEqual(after.map { $0.request.url.path }, [path, path, path])
        XCTAssertEqual(f.store.peek(f.identity)?.revision, stored?.revision)
        XCTAssertEqual(f.store.peek(f.identity)?.credential, stored?.credential)
        XCTAssertEqual(f.store.calls().filter { $0 == "write" }.count, writes)
        _ = await f.service.close()
    }

    func testFeedbackRoutesAdmitOnlyExactMethodsHostsPathsAndNoPlatformHeadersOverHTTP() throws {
        let accepted = [
            ("http://d.kugou.com/v3/qrydid", "POST"), ("http://d.kugou.com/v2/gen", "GET"),
            ("http://d.kugou.com/v2/post", "POST"), ("http://d.kugou.com:80/v2/post", "POST"),
            ("https://stat6.y.qq.com/android/fcgi-bin/imusic_tj", "POST"),
            ("https://clientlog.music.163.com/eapi/feedback/weblog", "POST")
        ]
        for (url, method) in accepted {
            let request = MusicAccountHTTPRequest(url: try XCTUnwrap(URL(string: url)), method: method)
            XCTAssertTrue(MusicAccountFeedbackRoute.permits(request), url)
            XCTAssertEqual(MusicAccountFeedbackRoute.permitsCleartext(request), url.hasPrefix("http://"))
        }
        let rejected = [
            ("https://d.kugou.com/v2/post", "POST"), ("http://d.kugou.com/v2/post", "GET"),
            ("http://d.kugou.com/v2/gen", "POST"), ("http://d.kugou.com/v3/qrydid", "GET"),
            ("http://d.kugou.com/v2/post/extra", "POST"), ("http://d.kugou.com/v2/grade", "POST"),
            ("http://d.kugou.com.invalid/v2/post", "POST"), ("http://d.kugou.com:443/v2/post", "POST"),
            ("http://synthetic@d.kugou.com/v2/post", "POST"), ("http://d.kugou.com/v2/post#fragment", "POST"),
            ("http://stat6.y.qq.com/android/fcgi-bin/imusic_tj", "POST"),
            ("https://stat6.y.qq.com/android/fcgi-bin/imusic_tj", "GET"),
            ("https://music.163.com/eapi/feedback/weblog", "POST"),
            ("https://clientlog.music.163.com/api/feedback/weblog", "POST")
        ]
        for (url, method) in rejected {
            let request = MusicAccountHTTPRequest(url: try XCTUnwrap(URL(string: url)), method: method)
            XCTAssertFalse(MusicAccountFeedbackRoute.permits(request), url)
        }
        for path in ["/v3/qrydid", "/v2/gen", "/v2/post"] {
            for header in ["Cookie", "cOoKiE", "Authorization", "authorization", "X-Router"] {
                let request = MusicAccountHTTPRequest(url: try XCTUnwrap(URL(string: "http://d.kugou.com" + path)),
                    method: path == "/v2/gen" ? "GET" : "POST", headers: [header: "synthetic-not-permitted"])
                XCTAssertFalse(MusicAccountFeedbackRoute.permitsCleartext(request))
                XCTAssertFalse(MusicAccountFeedbackRoute.permits(request))
            }
        }
    }

    func testCompressionAndAcknowledgementsRejectMalformedTrailingOversizedAndNonintegerSuccess() throws {
        let valid = MusicAccountFeedbackFixture.codeZero
        XCTAssertEqual(try MusicAccountFeedbackCompression.inflateZlib(valid), Data("{\"code\":0}".utf8))
        XCTAssertTrue(MusicAccountFeedbackQQWire.confirmed(MusicAccountFeedbackFixture.qqAck))
        for gzip in [false, true] {
            let plain = Data("synthetic feedback 中文".utf8)
            let encoded = try MusicAccountFeedbackCompression.compress(plain, gzip: gzip)
            XCTAssertEqual(try MusicAccountFeedbackFixture.inflate(encoded, gzip: gzip), plain)
        }
        var corrupted = valid; corrupted[corrupted.count - 1] ^= 1
        let bomb = try XCTUnwrap(Data(base64Encoded: "eJztwYEAAAAAgCC2/aUWqQoAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAACqASKYBBE="))
        let invalidStreams = [Data(), Data([0x78, 0x9c]), Data(valid.dropLast()), corrupted,
                              valid + Data([0]), valid + valid, bomb, Data(repeating: 0, count: 65537),
                              try MusicAccountFeedbackCompression.compress(Data("{}".utf8), gzip: true)]
        for bytes in invalidStreams {
            XCTAssertThrowsError(try MusicAccountFeedbackCompression.inflateZlib(bytes))
            XCTAssertFalse(MusicAccountFeedbackQQWire.confirmed(Data(repeating: 0, count: 5) + bytes))
        }
        XCTAssertThrowsError(try MusicAccountFeedbackCompression.inflateZlib(valid, limit: 9))
        XCTAssertThrowsError(try MusicAccountFeedbackCompression.compress(Data(repeating: 0, count: 128 * 1024 + 1)))
        for base64 in ["eJyrVkrOT0lVskpLzClOrQUAJJ8FHQ==", "eJyrVkrOT0lVslIyUKoFABi1A4Y=", "eJyrrgUAAXUA+Q==", "eJyrVkrOT0lVsjKsBQASkQND"] {
            XCTAssertFalse(MusicAccountFeedbackQQWire.confirmed(Data(repeating: 0, count: 5) + (try XCTUnwrap(Data(base64Encoded: base64)))))
        }
        XCTAssertFalse(MusicAccountFeedbackQQWire.confirmed(Data([1, 0, 0, 0, 0]) + valid))
        XCTAssertFalse(MusicAccountFeedbackQQWire.confirmed(valid))
        _ = try MusicAccountFeedbackKugouWire.ack(MusicKugouFixture.response(["status": 1, "errcode": 0]))
        let badAcknowledgements: [[String: Any]] = [
            [:], ["status": true], ["status": "1"], ["status": 0], ["status": 1, "errcode": 7],
            ["status": 1, "error_code": 1], ["status": 1, "data": ["grade_synced": false]]
        ]
        for bad in badAcknowledgements {
            XCTAssertThrowsError(try MusicAccountFeedbackKugouWire.ack(MusicKugouFixture.response(bad)))
        }
        XCTAssertThrowsError(try MusicAccountFeedbackKugouWire.ack(.init(status: 200, data: Data("not-json".utf8))))
        XCTAssertThrowsError(try MusicAccountFeedbackKugouWire.ack(MusicKugouFixture.response(["status": 1], status: 201)))
    }

    func testNeteaseRecommendationWireRequiresLiveProofAndPreservesPerSongSource() async throws {
        let f = try MusicNeteaseNativeFixture()
        try await f.enable(); _ = try await f.service.validate(platform: .netease)
        let baseline = await f.http.recorded()
        let path = "/eapi/feedback/weblog"
        await f.http.replies(path, [try MusicNeteaseFixture.response(["code": 200])])
        for source in ["aiMode", "moreRcmd", "sourceMap-synthetic"] {
            let authorization = MusicQueueAuthorization()
            let sourceID = source == "aiMode" ? "501" : ""
            let proof = MusicPlaybackRecommendationProof(occurrenceID: "entry-1", nativeID: "101",
                source: source, sourceID: sourceID, algorithm: "synthetic-alg", authorization: authorization)
            let target = MusicPlaybackFeedbackTarget(platform: .netease, nativeID: "101",
                source: source, sourceID: sourceID, algorithm: "synthetic-alg", recommendationProof: proof)
            let report = MusicPlaybackFeedbackReport(instanceID: UUID(), target: target, event: .start,
                duration: 180, listened: 0, mediaListened: 0, position: 0, rate: 1)
            let lease = try await f.service.beginPlaybackFeedback(platform: .netease, authorization: .init())
            let receipt = try await lease.report(report)
            XCTAssertEqual(receipt, .confirmed)
            let requests = await f.http.recorded()
            let record = try XCTUnwrap(requests.last)
            let payload = try MusicQQWire.decode(XCTUnwrap(record.payload))
            let logs = try XCTUnwrap(payload["logs"] as? String)
            let rows = try XCTUnwrap(JSONSerialization.jsonObject(with: Data(logs.utf8)) as? [[String: Any]])
            let fields = try XCTUnwrap(rows.first?["json"] as? [String: Any])
            XCTAssertEqual(fields["id"] as? String, "101")
            XCTAssertEqual(fields["source"] as? String, source)
            XCTAssertEqual(fields["sourceId"] as? String, sourceID)
            XCTAssertEqual(fields["alg"] as? String, "synthetic-alg")
            authorization.revoke()
            XCTAssertThrowsError(try MusicAccountFeedbackWire.validate(report))
            lease.revoke()

            let forged = MusicPlaybackFeedbackTarget(platform: .netease, nativeID: "101",
                source: source, sourceID: sourceID, algorithm: "synthetic-alg")
            XCTAssertThrowsError(try MusicAccountFeedbackWire.validate(.init(instanceID: UUID(),
                target: forged, event: .start, duration: 180, listened: 0, mediaListened: 0, position: 0, rate: 1)))
        }
        let records = Array((await f.http.recorded()).dropFirst(baseline.count))
        XCTAssertEqual(records.map { $0.request.url.path }, [path, path, path])
        _ = await f.service.close()
    }

    func testRecommendationProofCannotBeReusedForAnotherRecordingOrAttribution() throws {
        let authorization = MusicQueueAuthorization()
        let proof = MusicPlaybackRecommendationProof(occurrenceID: "entry-1", nativeID: "101",
            source: "sourceMap-synthetic", sourceID: "", algorithm: "synthetic-alg", authorization: authorization)
        for target in [
            MusicPlaybackFeedbackTarget(platform: .netease, nativeID: "102", source: proof.source,
                algorithm: proof.algorithm, recommendationProof: proof),
            .init(platform: .netease, nativeID: "101", source: "aiMode", sourceID: "501",
                algorithm: proof.algorithm, recommendationProof: proof),
            .init(platform: .netease, nativeID: "101", source: proof.source,
                algorithm: "changed", recommendationProof: proof)
        ] {
            XCTAssertThrowsError(try MusicAccountFeedbackWire.validate(.init(instanceID: UUID(), target: target,
                event: .start, duration: 180, listened: 0, mediaListened: 0, position: 0, rate: 1)))
        }
    }
}
