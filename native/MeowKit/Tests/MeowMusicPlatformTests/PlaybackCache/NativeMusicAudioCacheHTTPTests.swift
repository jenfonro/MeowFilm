import Foundation
import XCTest
@testable import MeowMusicPlatform

final class NativeMusicAudioCacheHTTPTests: XCTestCase {
    func testSequentialStreamingPreservesAdmittedHeadersAndOriginBoundary() throws {
        let source = URL(string: "http://[::1]/audio#position")!
        let headers = ["X-Media-Key": "fixture", "Cookie": "media=fixture", "User-Agent": "fixture"]
        let request = try NativeMusicAudioCacheHTTPPolicy.request(
            .init(url: source, platform: "mg", headers: headers, streaming: true))
        XCTAssertEqual(request.url?.absoluteString, "http://[::1]/audio")
        XCTAssertEqual(request.value(forHTTPHeaderField: "X-Media-Key"), "fixture")
        XCTAssertEqual(request.value(forHTTPHeaderField: "Cookie"), "media=fixture")
        XCTAssertNil(request.value(forHTTPHeaderField: "Range"))
        XCTAssertFalse(NativeMusicAudioCacheHTTPPolicy.permits(source, platform: "mg"))
        let response = try XCTUnwrap(HTTPURLResponse(url: source, statusCode: 302, httpVersion: nil, headerFields: nil))
        let same = try NativeMusicAudioCacheHTTPPolicy.redirect(response,
            proposed: URLRequest(url: URL(string: "http://[::1]:80/next")!), platform: "mg", count: 1,
            headers: headers, streaming: true)
        XCTAssertEqual(same.value(forHTTPHeaderField: "X-Media-Key"), "fixture")
        let other = try NativeMusicAudioCacheHTTPPolicy.redirect(response,
            proposed: URLRequest(url: URL(string: "http://[::1]:8080/next")!), platform: "mg", count: 1,
            headers: headers, streaming: true)
        XCTAssertNil(other.value(forHTTPHeaderField: "X-Media-Key"))
        XCTAssertNil(other.value(forHTTPHeaderField: "Cookie"))
        XCTAssertEqual(other.value(forHTTPHeaderField: "User-Agent"), "fixture")
        for header in ["Range", "Host", "Proxy-Authorization", "Accept-Encoding"] {
            XCTAssertThrowsError(try NativeMusicAudioCacheHTTPPolicy.request(
                .init(url: source, platform: "mg", headers: [header: "fixture"], streaming: true)))
        }
        let secure = try XCTUnwrap(HTTPURLResponse(url: URL(string: "https://media.invalid/a")!,
                                                statusCode: 302, httpVersion: nil, headerFields: nil))
        XCTAssertThrowsError(try NativeMusicAudioCacheHTTPPolicy.redirect(secure,
            proposed: URLRequest(url: URL(string: "http://media.invalid/a")!), platform: "mg", count: 1, streaming: true))
    }

    func testOfficialCDNIsBoundToExactPlatformAndHTTPSBoundary() throws {
        for (platform, host) in [("wy", "m701.music.126.net"), ("tx", "isure.stream.qqmusic.qq.com"),
                                 ("kg", "fs.kugou.com"), ("kg", "fs.kugou.net"), ("kg", "fs.kgimg.com")] {
            let url = URL(string: "https://" + host + "/file")!
            XCTAssertTrue(NativeMusicAudioCacheHTTPPolicy.permits(url, platform: platform))
            for wrong in ["lx", "kw", "mg", platform == "tx" ? "wy" : "tx"] {
                XCTAssertFalse(NativeMusicAudioCacheHTTPPolicy.permits(url, platform: wrong))
            }
        }
        for value in [
            "http://isure.stream.qqmusic.qq.com/file", "file:///tmp/file", "https://127.0.0.1/file",
            "https://user:password@isure.stream.qqmusic.qq.com/file",
            "https://isure.stream.qqmusic.qq.com:8443/file", "https://isure.stream.qqmusic.qq.com./file",
            "https://isure.stream.qqmusic.qq.com.attacker.invalid/file",
            "https://notstream.qqmusic.qq.com/file", "https://isure.stream.qqmusic.qq.com/file#fragment",
            "https://music.163.com/weapi/account"
        ] {
            XCTAssertFalse(NativeMusicAudioCacheHTTPPolicy.permits(URL(string: value)!, platform: "tx"), value)
        }
        XCTAssertEqual(NativeMusicAudioCacheHTTPPolicy.maximumBytes, 268_435_456)
    }

    func testRedirectRebuildsRequestAndNeverForwardsAccountRangeOrCookies() throws {
        let source = URL(string: "https://isure.stream.qqmusic.qq.com/old")!
        let response = try XCTUnwrap(HTTPURLResponse(url: source, statusCode: 302, httpVersion: "HTTP/1.1",
                                                   headerFields: ["Location": "https://dl.stream.qqmusic.qq.com/new"]))
        var proposed = URLRequest(url: URL(string: "https://dl.stream.qqmusic.qq.com/new")!)
        proposed.httpMethod = "POST"; proposed.httpBody = Data("sensitive".utf8)
        proposed.allHTTPHeaderFields = [
            "Authorization": "synthetic-bearer", "Proxy-Authorization": "synthetic-proxy", "Cookie": "synthetic-cookie",
            "X-Account-Token": "synthetic-token", "Referer": "https://account.invalid/", "Range": "bytes=100-"
        ]
        let sanitized = try NativeMusicAudioCacheHTTPPolicy.redirect(response, proposed: proposed, platform: "tx", count: 1)
        XCTAssertEqual(sanitized.httpMethod, "GET")
        XCTAssertNil(sanitized.httpBody)
        XCTAssertFalse(sanitized.httpShouldHandleCookies)
        XCTAssertEqual(sanitized.value(forHTTPHeaderField: "Accept-Encoding"), "identity")
        for header in ["Authorization", "Proxy-Authorization", "Cookie", "X-Account-Token", "Referer", "Range"] {
            XCTAssertNil(sanitized.value(forHTTPHeaderField: header))
        }
        for destination in ["https://m701.music.126.net/file", "https://127.0.0.1/file",
                            "http://dl.stream.qqmusic.qq.com/new", "https://example.invalid/file"] {
            proposed.url = URL(string: destination)
            XCTAssertThrowsError(try NativeMusicAudioCacheHTTPPolicy.redirect(response, proposed: proposed, platform: "tx", count: 1))
        }
        proposed.url = source
        XCTAssertThrowsError(try NativeMusicAudioCacheHTTPPolicy.redirect(response, proposed: proposed, platform: "tx", count: 6))
        let configuration = NativeMusicAudioCacheHTTPPolicy.configuration()
        XCTAssertNil(configuration.httpCookieStorage); XCTAssertNil(configuration.urlCredentialStorage)
        XCTAssertNil(configuration.urlCache); XCTAssertFalse(configuration.httpShouldSetCookies)
        XCTAssertEqual(configuration.timeoutIntervalForRequest, 120)
        XCTAssertEqual(configuration.timeoutIntervalForResource, 1800)
    }

    func testOnlyBoundedHTTP200IdentityRepresentationWithExplicitLengthAndType() throws {
        let url = URL(string: "https://isure.stream.qqmusic.qq.com/file")!
        let valid = ["Content-Type": "audio/mpeg; charset=binary", "Content-Length": "32"]
        XCTAssertEqual(try NativeMusicAudioCacheHTTPPolicy.length(.init(url: url, status: 200, headers: valid), platform: "tx"), 32)
        for status in [204, 206, 301, 401, 403, 404, 500] {
            XCTAssertThrowsError(try NativeMusicAudioCacheHTTPPolicy.length(.init(url: url, status: status, headers: valid), platform: "tx"))
        }
        let invalid: [[String: String]] = [
            ["Content-Type": "audio/mpeg"],
            ["Content-Length": "32"],
            ["Content-Type": "text/html", "Content-Length": "32"],
            ["Content-Type": "audio/x-mpegurl", "Content-Length": "32"],
            valid.merging(["Content-Range": "bytes 0-31/32"]) { _, new in new },
            valid.merging(["Content-Encoding": "gzip"]) { _, new in new },
            valid.merging(["Transfer-Encoding": "chunked"]) { _, new in new },
            valid.merging(["content-length": "32"]) { _, new in new },
            valid.merging(["Content-Type": "audio/mpeg\r\nSet-Cookie: synthetic"]) { _, new in new }
        ]
        for headers in invalid {
            XCTAssertThrowsError(try NativeMusicAudioCacheHTTPPolicy.length(.init(url: url, status: 200, headers: headers), platform: "tx"))
        }
        for length in ["0", "-1", "01", " 32", "32 ", "+32", "3e1", "32,32", "268435457", "99999999999999999999999"] {
            var headers = valid; headers["Content-Length"] = length
            XCTAssertThrowsError(try NativeMusicAudioCacheHTTPPolicy.length(.init(url: url, status: 200, headers: headers), platform: "tx"), length)
        }
    }

    func testShortOverlongOrNoTransportEOFNeverBecomeCompleteFiles() async throws {
        for (declared, eof) in [(31, true), (33, true), (32, false)] {
            let f = try AudioCacheTestFixture()
            let transport = AudioCacheTestTransport(declaredLength: declared, successfulEOF: eof)
            let cache = f.cache(transport: transport)
            await cache.storeAfterPlayback(f.request())
            let hit = await f.hit(cache)
            XCTAssertNil(hit)
            XCTAssertFalse(try f.names().contains { $0.hasSuffix(".wav") || $0.hasSuffix(".part") })
            await cache.close(); f.cleanup()
        }
    }

    func testComplete206RequiresExactSingleRangeWithOptionalMatchingLength() throws {
        let url = URL(string: "https://isure.stream.qqmusic.qq.com/file")!
        for length in [nil, "32"] as [String?] {
            var headers = ["Content-Type": "audio/mpeg", "Content-Range": "bytes 0-31/32"]
            headers["Content-Length"] = length
            XCTAssertEqual(try NativeMusicAudioCacheHTTPPolicy.length(
                .init(url: url, status: 206, headers: headers), platform: "tx"), 32)
        }
        for range in ["bytes 1-31/32", "bytes 0-30/32", "bytes 0-32/32", "bytes 0-31/*",
                      "bytes 0-0/0", "bytes 0-031/32", "bytes 0-31/032", "bytes 0-31/32,0-31/32",
                      "bytes 0-31/32 ", "bytes 0-999999999999999999999/1000000000000000000000",
                      "bytes 0-268435456/268435457", "items 0-31/32"] {
            XCTAssertThrowsError(try NativeMusicAudioCacheHTTPPolicy.length(.init(url: url, status: 206,
                headers: ["Content-Type": "audio/mpeg", "Content-Range": range]), platform: "tx"), range)
        }
        for extra in [["Content-Length": "31"], ["Content-Length": "33"], ["Content-Length": "032"],
                      ["Content-Type": "multipart/byteranges"], ["Content-Encoding": "gzip"],
                      ["Transfer-Encoding": "chunked"]] {
            let headers = ["Content-Type": "audio/mpeg", "Content-Range": "bytes 0-31/32"].merging(extra) { _, new in new }
            XCTAssertThrowsError(try NativeMusicAudioCacheHTTPPolicy.length(
                .init(url: url, status: 206, headers: headers), platform: "tx"))
        }
    }

    func testComplete206FlowsThroughTheSameEOFInspectionAndPublicationBoundary() async throws {
        for hasLength in [true, false] {
            for eof in [true, false] {
                let f = try AudioCacheTestFixture()
                let transport = AudioCacheTestTransport(successfulEOF: eof, status: 206,
                                                       omitLength: !hasLength)
                let cache = f.cache(transport: transport)
                await cache.storeAfterPlayback(f.request())
                let hit = await f.hit(cache)
                XCTAssertEqual(hit != nil, eof)
                XCTAssertFalse(try f.names().contains { $0.hasSuffix(".part") })
                await cache.close(); f.cleanup()
            }
        }
    }
}
