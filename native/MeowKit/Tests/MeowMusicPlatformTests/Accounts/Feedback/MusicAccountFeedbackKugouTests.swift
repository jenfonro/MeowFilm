import Foundation
import XCTest
import MeowMusicFeature
@testable import MeowMusicPlatform

// Fixed meowmusic cc22a7ee kugou/feedback.go. CSCC really uses HTTP on these
// three routes; this is not HTTP/3, not TLS and not a real remote test.
@MainActor final class MusicAccountFeedbackKugouTests: XCTestCase {
    func testKugouStartChecksExactRecordingThenAnonymousCSCCAndDoesNotReplayBadHandshake() async throws {
        let peer = MusicAccountFeedbackKugouPeer(), clock = MusicAccountTestClock()
        let provider = KugouMusicAccountAdapter(transport: peer, now: { clock.now() })
        let channel = MusicAccountFeedbackChannel(platform: .kugou, provider: provider,
            credential: try MusicKugouFixture.credential(), authorization: .init(), clock: { 100 })
        let instance = UUID()
        let start = MusicAccountFeedbackFixture.report(.start, platform: .kugou, instance: instance)
        do { _ = try await channel.report(start); XCTFail("Invalid gen ciphertext is not a successful session") }
        catch { XCTAssertEqual(error as? MusicServiceError, .upstreamInvalid) }
        let duplicate = try await channel.report(start)
        let end = try await channel.report(MusicAccountFeedbackFixture.report(.end, platform: .kugou, instance: instance))
        XCTAssertEqual(duplicate, .unconfirmed); XCTAssertEqual(end, .unconfirmed)
        let requests = await peer.recorded()
        XCTAssertEqual(requests.map { $0.url.path }, ["/kmr/v2/audio", "/v3/qrydid", "/v2/gen"])
        let metadata = try XCTUnwrap(requests.first)
        XCTAssertEqual(metadata.url.host, "gateway.kugou.com"); XCTAssertEqual(metadata.url.scheme, "https")
        let input = try MusicQQWire.decode(XCTUnwrap(metadata.body))
        XCTAssertEqual(input["data"] as? [[String: Int]], [["entity_id": 22]])
        let cscc = Array(requests.dropFirst())
        for request in cscc {
            XCTAssertEqual(request.url.scheme, "http"); XCTAssertEqual(request.url.host, "d.kugou.com")
            XCTAssertTrue(MusicAccountFeedbackRoute.permitsCleartext(request))
            let keys = request.headers.keys.map { $0.lowercased() }
            XCTAssertFalse(keys.contains("cookie")); XCTAssertFalse(keys.contains("authorization")); XCTAssertFalse(keys.contains("x-router"))
            XCTAssertEqual(request.headers["Cache-Control"], "no-store")
            XCTAssertFalse(request.url.absoluteString.contains(MusicKugouFixture.token))
            XCTAssertFalse(request.headers.values.contains { $0.contains(MusicKugouFixture.token) })
            let query = try MusicKugouFixture.fields(request)
            XCTAssertNil(query["token"]); XCTAssertNil(query["userid"]); XCTAssertNil(query["vip_token"])
        }
        let first = try XCTUnwrap(cscc.first), firstQuery = try MusicKugouFixture.fields(first)
        let firstBody = try XCTUnwrap(first.body), portrait = try MusicQQWire.decode(firstBody)
        XCTAssertEqual(first.method, "POST"); XCTAssertEqual(Set(firstQuery.keys), ["appid", "_t", "sign"])
        XCTAssertEqual(firstQuery["appid"], "and02")
        XCTAssertEqual(portrait["uuid"] as? String, MusicKugouFixture.seed)
        XCTAssertEqual(portrait["machine"] as? String, "0123456789")
        XCTAssertEqual(portrait["wh"] as? [Int], [1920, 1080])
        let time = String(Int(clock.now().timeIntervalSince1970))
        let didPrefix = "_t" + time + "appidand02pbKC7zn{4U*ydo2M1Rir"
        XCTAssertEqual(firstQuery["sign"], MusicAccountFeedbackFixture.md5(Data(didPrefix.utf8) + firstBody))
        let gen = try XCTUnwrap(cscc.last), genQuery = try MusicKugouFixture.fields(gen)
        XCTAssertEqual(gen.method, "GET"); XCTAssertNil(gen.body)
        XCTAssertEqual(Set(genQuery.keys), ["appid", "_t", "s", "sign"])
        XCTAssertEqual(genQuery["appid"], "3116")
        let encryptedPortrait = try XCTUnwrap(genQuery["s"])
        XCTAssertEqual(try XCTUnwrap(Data(base64Encoded: encryptedPortrait)).count, 128)
        let genSign = "_t" + time + "appid3116s" + encryptedPortrait + "LnT6xpN3khm36zse0QzvmgTZ3waWdRSA"
        XCTAssertEqual(genQuery["sign"], MusicAccountFeedbackFixture.md5(Data(genSign.utf8)))
        XCTAssertFalse(requests.contains { $0.url.path == "/v2/post" || $0.url.path.contains("grade") })
    }

    func testKugouPostUsesFixedSessionDoubleZlibAESAndSignatureWithoutPlatformCredentials() async throws {
        let peer = MusicAccountFeedbackKugouPeer(), clock = MusicAccountTestClock()
        let provider = KugouMusicAccountAdapter(transport: peer, now: { clock.now() })
        // Explicit internal session tests the post codec only. It is NOT evidence
        // of a successful synthetic RSA handshake or a real platform acceptance.
        let session = MusicAccountFeedbackNativeSession(mixsongID: "22", cookie: "synthetic-cscc-cookie",
            clienttime: "1800000000", serverstr: "synthetic-server", field: "0123456789abcdef0123456789abcdef")
        let target = MusicPlaybackFeedbackTarget(platform: .kugou, nativeID: MusicKugouFixture.baseHash,
                                                mixsongID: "22", automatic: true)
        let report = MusicPlaybackFeedbackReport(instanceID: UUID(), target: target, event: .end, duration: 180,
            listened: 12.5, mediaListened: 25, position: 25, rate: 2, reason: .interrupt,
            startedAt: 1_800_000_000, occurredAt: 1_800_000_012.5)
        let result = try await provider.feedback(report, credential: MusicKugouFixture.credential(),
                                                session: session, guard: .init())
        XCTAssertEqual(result.receipt, .confirmed); XCTAssertEqual(result.session?.mixsongID, "22")
        let requests = await peer.recorded()
        XCTAssertEqual(requests.count, 1)
        let request = try XCTUnwrap(requests.first)
        XCTAssertEqual(request.url.scheme, "http"); XCTAssertEqual(request.url.host, "d.kugou.com")
        XCTAssertEqual(request.url.path, "/v2/post"); XCTAssertEqual(request.method, "POST")
        XCTAssertTrue(MusicAccountFeedbackRoute.permitsCleartext(request))
        XCTAssertEqual(request.headers["Content-Type"], "application/octet-stream")
        XCTAssertEqual(request.headers["User-Agent"], "Android9-1070-10597-18-0-CsccPost-wifi")
        let headerNames = request.headers.keys.map { $0.lowercased() }
        XCTAssertFalse(headerNames.contains("cookie")); XCTAssertFalse(headerNames.contains("authorization"))
        let query = try MusicKugouFixture.fields(request), encrypted = try XCTUnwrap(request.body)
        XCTAssertEqual(Set(query.keys), ["cookie", "length", "appid", "_t", "sign"])
        XCTAssertEqual(query["cookie"], session.cookie); XCTAssertEqual(query["appid"], "3116")
        XCTAssertEqual(query["_t"], "1800000000")
        // The only cookie is the CSCC protocol session in its fixed query, never
        // the platform Cookie header or login token/Authorization.
        XCTAssertFalse(request.url.absoluteString.contains(MusicKugouFixture.token))
        let signed = query.keys.filter { $0 != "sign" }.sorted().map { $0 + query[$0]! }.joined()
        let signBytes = Data((signed + "LnT6xpN3khm36zse0QzvmgTZ3waWdRSA").utf8) + encrypted
        XCTAssertEqual(query["sign"], MusicAccountFeedbackFixture.md5(signBytes))
        let key = MusicAccountFeedbackFixture.md5(Data((MusicKugouFixture.seed + session.clienttime + session.field + session.serverstr).utf8))
        let compressed = try MusicNeteaseFixture.aes(encrypted, key: Data(key.utf8), iv: Data(MusicKugouFixture.seed.prefix(16).utf8))
        let plain = try MusicAccountFeedbackFixture.inflate(compressed)
        XCTAssertEqual(query["length"], String(plain.count))
        let separator = Data([13, 10])
        let firstBreak = try XCTUnwrap(plain.range(of: separator))
        let secondBreak = try XCTUnwrap(plain.range(of: separator, in: firstBreak.upperBound..<plain.endIndex))
        let line1 = String(decoding: plain[..<firstBreak.lowerBound], as: UTF8.self).components(separatedBy: "\t")
        let line2 = String(decoding: plain[firstBreak.upperBound..<secondBreak.lowerBound], as: UTF8.self).components(separatedBy: "\t")
        let event = try MusicAccountFeedbackFixture.inflate(Data(plain[secondBreak.upperBound...]))
        XCTAssertEqual(line1, ["4", "1", MusicKugouFixture.seed, "0", "3116", "10597", "18", "9", "0123456789",
                               MusicKugouFixture.seed, "176905054734845483393488264506404844848", "0",
                               "000000000000000000000000000000000000"])
        XCTAssertEqual(line2, [String(event.count), "10048", "0", "1800000000", "1", "42", "0", "0"])
        var fields: [String: String] = [:]
        for pair in String(decoding: event, as: UTF8.self).split(separator: "&") {
            let parts = pair.split(separator: "=", maxSplits: 1, omittingEmptySubsequences: false)
            XCTAssertEqual(parts.count, 2)
            if parts.count == 2 { XCTAssertNil(fields.updateValue(String(parts[1]), forKey: String(parts[0]))) }
        }
        XCTAssertEqual(fields["type_id"], "4"); XCTAssertEqual(fields["action"], "play")
        XCTAssertEqual(fields["mixsongid"], "22"); XCTAssertEqual(fields["st"], "180000")
        XCTAssertEqual(fields["spt"], "25000"); XCTAssertEqual(fields["duration"], "12500")
        XCTAssertEqual(fields["state"], "被终止"); XCTAssertEqual(fields["sty"], "自动"); XCTAssertEqual(fields["fs"], "2.0")
        XCTAssertEqual(fields["userid"], "42"); XCTAssertEqual(fields["time"], "1800000000000")
        XCTAssertNil(fields["source"]); XCTAssertNil(fields["fo"]); XCTAssertNil(fields["token"])
        XCTAssertFalse(String(decoding: event, as: UTF8.self).contains(MusicKugouFixture.token))
        let start = MusicAccountFeedbackFixture.report(.start, platform: .kugou, instance: UUID())
        XCTAssertEqual(MusicAccountFeedbackKugouWire.fields(start, mix: "22"),
                       ["type_id=20431", "action=play", "mixsongid=22", "spt=180000", "sty=手动"])
    }
}
