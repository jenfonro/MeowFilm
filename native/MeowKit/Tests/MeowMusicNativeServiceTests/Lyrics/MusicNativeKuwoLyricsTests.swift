import Foundation
import XCTest
import MeowMusicFeature
@testable import MeowMusicNativeService

final class MusicNativeKuwoLyricsTests: XCTestCase {
    func testIndependentZlibXORVectorPreservesWordsOffsetAndTranslation() throws {
        let decoded = try MusicNativeKuwoLyrics.decode(MusicNativeLyricsFixture.kuwo)
        XCTAssertEqual(decoded.text, "[offset:100]\n[1000,1200](1200,200,0)A(1800,400,0)B\n[2000,200](2100,100,0)C")
        XCTAssertEqual(decoded.translation, "[offset:100]\n[00:01.000]Translation")
        XCTAssertEqual(decoded.romanization, "", "Neither fixed KW nor MG response supplies a romanization track")
        let lines = try MusicNativeLyricsParser.parse(decoded)
        XCTAssertEqual(lines.map(\.text), ["AB", "C"])
        XCTAssertEqual(lines.map(\.timing), ["word", "word"])
        XCTAssertEqual(lines[0].time, 1.1, accuracy: 0.000001)
        XCTAssertEqual(lines[0].translation, "Translation")
        XCTAssertEqual(lines[0].words[0].time, 1.3, accuracy: 0.000001)
        XCTAssertEqual(lines[0].words[0].duration, 0.2, accuracy: 0.000001)
        XCTAssertEqual(lines[0].words[1].time, 1.9, accuracy: 0.000001)
        XCTAssertEqual(lines[0].words[1].duration, 0.4, accuracy: 0.000001)
        XCTAssertEqual(lines[1].words[0].time, 2.2, accuracy: 0.000001)
    }

    func testPlainLyricsEntitiesDuplicateTranslationAndOverlapFollowFixedSemantics() throws {
        let plain = try MusicNativeKuwoLyrics.parse("[00:01.00]A &amp; B\n[00:02.00]译文\n[00:02.00]next")
        let rows = try MusicNativeLyricsParser.parse(plain)
        XCTAssertEqual(rows.map(\.text), ["A & B", "next"])
        XCTAssertEqual(rows[0].translation, "译文")
        XCTAssertEqual(rows[0].timing, "estimated")
        let once = try MusicNativeKuwoLyrics.parse("[00:01.00]&amp;lt;")
        XCTAssertEqual(try MusicNativeLyricsParser.parse(once)[0].text, "&lt;")
        let overlap = try MusicNativeKuwoLyrics.parse("[kuwo:13]\n[00:01.00]<800,-400>A<600,200>B")
        let words = try MusicNativeLyricsParser.parse(overlap)[0].words
        XCTAssertEqual(words[0].time, 1.2, accuracy: 0.000001)
        XCTAssertEqual(words[0].duration, 0.2, accuracy: 0.000001)
        XCTAssertEqual(words[1].time, 1.4, accuracy: 0.000001)
        XCTAssertEqual(words[1].duration, 0.2, accuracy: 0.000001)
        for bad in ["<html>denied</html>", "[kuwo:0]\n[00:01.00]<2,0>A",
                    "[kuwo:invalid]\n[00:01.00]<2,0>A", "[00:70.00]A",
                    "[00:01.00]<999999999999999999999,0>A",
                    "[offset:1]\n[offset:2]\n[00:01.00]A"] {
            XCTAssertThrowsError(try MusicNativeKuwoLyrics.parse(bad))
        }
    }

    func testEnvelopeRejectsTruncationTrailingStreamsInvalidEncodingAndExpansionBudget() throws {
        let good = MusicNativeLyricsFixture.kuwo
        for bad in [Data(), Data("tp=content invalid".utf8), Data(good.dropLast()), good + Data([0]),
                    Data(repeating: 0, count: MusicLyricCrypto.maximum + 1)] {
            XCTAssertThrowsError(try MusicNativeKuwoLyrics.decode(bad))
        }
        for raw in ["eJzLyy/RTUosTjUzUQQAGIkDpQ==", "eJzLSLe1BQADkAFK"] {
            let body = Data("tp=content\r\n\r\n".utf8) + (try XCTUnwrap(Data(base64Encoded: raw)))
            XCTAssertThrowsError(try MusicNativeKuwoLyrics.decode(body), "base64/UTF8 must be strict")
        }
        let bomb = try XCTUnwrap(Data(base64Encoded: MusicNativeLyricsFixture.expansionBomb))
        XCTAssertThrowsError(try MusicNativeKuwoLyrics.inflateEnvelope(bomb)) {
            XCTAssertEqual($0 as? MusicServiceError, .limit)
        }
    }

    func testAnonymousFixedEndpointIDAndClosedTransportAdmissionWithoutNetworking() async throws {
        let info = MJ.obj([("songmid", .string("9007199254740993"))])
        let good = try MusicNativeKuwoLyrics.request(info: info)
        XCTAssertEqual(good.url.scheme, "https"); XCTAssertEqual(good.url.host, "mlyric.kuwo.cn")
        XCTAssertEqual(good.url.path, "/mobi.s"); XCTAssertEqual(good.method, "GET"); XCTAssertNil(good.body)
        let query = Dictionary(uniqueKeysWithValues: try XCTUnwrap(URLComponents(url: good.url, resolvingAgainstBaseURL: false)?.queryItems)
            .map { ($0.name, $0.value ?? "") })
        XCTAssertEqual(query, ["f": "web", "type": "lyric", "lrcx": "1", "rid": "9007199254740993", "encode": "utf8"])
        XCTAssertEqual(Set(good.headers.keys), ["User-Agent"])
        XCTAssertTrue(MusicNativeKuwoLyrics.permits(good))
        XCTAssertThrowsError(try MusicNativeKuwoLyrics.request(info: .obj([("id", .string("MUSIC_9007199254740993"))])))
        XCTAssertEqual(try MusicNativeKuwoLyrics.request(info: .obj([
            ("songmid", .string("MUSIC_9007199254740993"))])).url, good.url)
        let transport = URLSessionMusicNativeHTTPTransport(); transport.close()
        do { _ = try await transport.send(good); XCTFail("closed") }
        catch { XCTAssertEqual(error as? MusicServiceError, .closed) }
        let bad = [
            MusicNativeHTTPRequest(url: good.url, method: "POST", headers: good.headers),
            .init(url: URL(string: good.url.absoluteString.replacingOccurrences(of: "https:", with: "http:"))!, headers: good.headers),
            .init(url: URL(string: good.url.absoluteString + "&rid=124")!, headers: good.headers),
            .init(url: URL(string: good.url.absoluteString.replacingOccurrences(of: "mlyric.kuwo.cn", with: "mlyric.kuwo.cn.evil.invalid"))!, headers: good.headers),
            .init(url: good.url, headers: ["Cookie": "synthetic"]),
            .init(url: good.url, headers: ["Authorization": "synthetic"]),
            .init(url: good.url, headers: ["User-Agent": "one\r\ntwo"])
        ]
        for request in bad {
            XCTAssertFalse(MusicNativeKuwoLyrics.permits(request))
            do { _ = try await transport.send(request); XCTFail("invalid") }
            catch { XCTAssertEqual(error as? MusicServiceError, .invalidRequest) }
        }
        let selected = try MusicNativeKuwoLyrics.request(info: .obj([("songmid", .string("123")), ("id", .string("124"))]))
        XCTAssertEqual(URLComponents(url: selected.url, resolvingAgainstBaseURL: false)?.queryItems?
            .first { $0.name == "rid" }?.value, "123")
        XCTAssertThrowsError(try MusicNativeKuwoLyrics.request(id: "../123"))
    }
}
