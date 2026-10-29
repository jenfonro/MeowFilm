import XCTest
import CommonCrypto
import CryptoKit
import MeowMusicFeature
@testable import MeowMusicNativeService

final class NativeMusicProtocolTests: XCTestCase {
    func testNativeEAPIIndependentDecryptionAndChecksum() throws {
        let path = "/api/cloudsearch/pc", input = MJ.obj([("s", .string("Synthetic")), ("type", .n(1)), ("limit", .n(30))])
        let body = try MusicPlatformProtocol.netease(uri: path, data: input)
        let hex = Array(String(decoding: body, as: UTF8.self).dropFirst(7))
        let encrypted = stride(from: 0, to: hex.count, by: 2).map { UInt8(String(hex[$0..<$0+2]), radix: 16)! }
        let key = Array("e82ckenh8dichen8".utf8)
        var output = [UInt8](repeating: 0, count: encrypted.count + 16), count = 0
        let capacity = output.count
        XCTAssertEqual(CCCrypt(CCOperation(kCCDecrypt), CCAlgorithm(kCCAlgorithmAES),
            CCOptions(kCCOptionPKCS7Padding | kCCOptionECBMode), key, key.count, nil, encrypted, encrypted.count, &output, capacity, &count), CCCryptorStatus(kCCSuccess))
        let checksum = Insecure.MD5.hash(data: Data(("nobody" + path + "use" + input.json + "md5forencrypt").utf8)).map { String(format: "%02x", $0) }.joined()
        XCTAssertEqual(String(decoding: output.prefix(count), as: UTF8.self), path + "-36cd479b6b5-" + input.json + "-36cd479b6b5-" + checksum)
    }
    func testNativeWYHTTPKeepsLongIDAndOffsetsWithoutScriptOrAccount() async throws {
        let reply = try MJ.parse(Data(#"{"code":200,"result":{"songCount":31,"songs":[{"id":"9007199254740993","name":"WY","ar":[{"name":"Artist"}],"al":{"id":1,"name":"Album"},"dt":180000}]}}"#.utf8))
        let http = NativeMusicHTTPFixture([reply])
        let service = try await NativeMusicFixture.service(http: http)
        let page = try await service.search(platform: "wy", query: "Synthetic", page: 2)
        XCTAssertEqual(page.items[0].sources[0].nativeId, "9007199254740993")
        XCTAssertNil(page.nextPage)
        let requests = await http.calls()
        XCTAssertEqual(requests.count, 1)
        XCTAssertEqual(requests[0].url.path, "/eapi/cloudsearch/pc")
        XCTAssertEqual(requests[0].method, "POST")
        XCTAssertFalse((requests[0].headers["Cookie"] ?? "").contains("MUSIC_U"))
    }
    func testNativeKGSignatureAndAmbiguousLyricsNeverFetchFirstCandidate() async throws {
        let params = ["keyword": "合成", "page": "1", "appid": "1005"]
        let expected = MusicPlatformProtocol.md5("OIlwieks28dk2k092lksi2UIkpappid=1005keyword=合成page=1OIlwieks28dk2k092lksi2UIkp")
        XCTAssertEqual(MusicPlatformProtocol.kugou(params), expected)
        let reply = try MJ.parse(Data(#"{"status":200,"errcode":200,"candidates":[{"id":"1"},{"id":"2"}]}"#.utf8))
        let http = NativeMusicHTTPFixture([reply])
        let platforms = MusicNativePlatforms(transport: http, qq: NativeMusicQQFixture(), clock: { NativeMusicFixture.date })
        do {
            _ = try await platforms.lyrics(platform: "kg", info: .obj([("hash", .string("1234567890ABCDEF1234567890ABCDEF")), ("duration", .n(180))]))
            XCTFail("ambiguous recording")
        } catch { XCTAssertEqual(error as? MusicServiceError, .lyricIdentityMismatch) }
        let requests = await http.calls(); XCTAssertEqual(requests.count, 1); XCTAssertEqual(requests[0].url.path, "/v1/search")
    }
    func testFixedSyntheticQRCVectorAndExactMillisecondTranslation() throws {
        let encrypted = "0c8d67dd3e549974b64ed2680459f13881aa15d10db4cc8324b86311d0d741bd6af5d8724f2b7571b2b2bf976be395e454a23ccb367e464b537e1be65e411da3f6e6b01aadcc5e634b317c352096ebbac83f8b1415b90ff5acdb443265324bbb5a5da122957dca8a91256a421f7fd9a27f649bd821b4fa3926e993f67c6fad7cbc377509b75144089a1e82144e798461"
        let trans = "32dabb4c5e9846faf8ad6a456c3eb58233c06a98fd7f551a855b6cacf6ffb3778cde8f3bc607e201"
        let lines = try MusicNativeLyricsParser.parse(.init(text: encrypted, translation: trans, encoding: .qqQRC))
        XCTAssertEqual(lines.count, 1); XCTAssertEqual(lines[0].text, "喵音乐")
        XCTAssertEqual(lines[0].translation, "Synthetic translation")
        XCTAssertEqual(lines[0].words[0].time, 1, accuracy: 0.000001)
        XCTAssertEqual(lines[0].words[1].time, 1.3, accuracy: 0.000001)
        let mismatch = try MusicNativeLyricsParser.parse(.init(text: "[00:01.000]Original", translation: "[00:01.001]Wrong"))
        XCTAssertEqual(mismatch[0].translation, "")
    }
    func testQRCZeroDurationAndXMLSingleEntityDecode() throws {
        let text = #"<QrcInfos><Lyric_1 LyricContent="[1000,0]Zero(1000,0)&#10;[1000,20]&amp;lt;(1000,20)"/></QrcInfos>"#
        let lines = try MusicNativeLyricsParser.parse(.init(text: text))
        XCTAssertEqual(lines[0].end, 1); XCTAssertEqual(lines[0].words[0].duration, 0)
        XCTAssertEqual(lines[1].text, "&lt;"); XCTAssertEqual(lines[1].end, 1.02, accuracy: 0.000001)
        XCTAssertThrowsError(try MusicLyricCrypto.qrc("not encrypted"))
        XCTAssertThrowsError(try MusicLyricCrypto.krc("not-base64"))
    }
    func testKRCRelativeWordsAndTranslationMetadata() throws {
        let value = try MusicNativeLyricsParser.kugou("[offset:100]\n[1000,1000]<0,400,0>A<400,600,0>B")
        let lines = try MusicNativeLyricsParser.parse(value)
        XCTAssertEqual(lines[0].text, "AB")
        XCTAssertEqual(lines[0].words.map(\.time), [1.1, 1.5])
        XCTAssertEqual(lines[0].words.map(\.duration), [0.4, 0.6])
    }
}
