import Foundation
import XCTest
import MeowContracts
@testable import MeowPlayback
#if canImport(AVFoundation)
import AVFoundation
#endif

final class PlaybackNetworkPolicyTests: XCTestCase {
    private func plan(_ url: String = "https://media.example.invalid/file.mp4",
                      headers: [String: String] = [:],
                      options: PlaybackNetworkOptions = .init()) throws -> PlaybackNetworkPlan {
        try PlaybackNetworkPlan(resource: PlaybackResource(id: "fixture", url: URL(string: url)!,
                                                          title: "测试", headers: headers), options: options)
    }

    func testProgressiveHeadersUseControlledTransportWithoutAContractChange() throws {
        let value = try plan(headers: ["Referer": "https://page.example.invalid/", "Cookie": "sid=synthetic",
                                       "User-Agent": "meow-test", "Authorization": "Bearer synthetic"])
        XCTAssertEqual(value.route, .progressive)
        let policy = PlaybackHTTPRequestPolicy(plan: value)
        let request = try policy.request(start: 5, end: 100, validator: "\"v1\"")
        XCTAssertEqual(request.httpMethod, "GET")
        XCTAssertEqual(request.value(forHTTPHeaderField: "Range"), "bytes=5-100")
        XCTAssertEqual(request.value(forHTTPHeaderField: "Accept-Encoding"), "identity")
        XCTAssertEqual(request.value(forHTTPHeaderField: "Authorization"), "Bearer synthetic")
        XCTAssertEqual(request.value(forHTTPHeaderField: "Cookie"), "sid=synthetic")
        XCTAssertEqual(request.value(forHTTPHeaderField: "If-Range"), "\"v1\"")
        XCTAssertFalse(request.httpShouldHandleCookies)
    }

    func testRedirectIsOriginScopedAndDoesNotRestoreCredentialsOnABAReturn() throws {
        var policy = PlaybackHTTPRequestPolicy(plan: try plan(headers: [
            "Authorization": "Bearer synthetic", "Cookie": "sid=synthetic",
            "X-Session": "synthetic", "Referer": "https://private.example.invalid/",
            "User-Agent": "test", "Accept": "video/*"
        ]))
        try policy.redirect(to: URL(string: "https://media.example.invalid:443/next")!)
        XCTAssertNotNil(try policy.request(start: 20, end: 30, validator: nil).value(forHTTPHeaderField: "Cookie"))
        try policy.redirect(to: URL(string: "https://cdn.example.invalid/next")!)
        for url in ["https://cdn.example.invalid/final", "https://media.example.invalid/final"] {
            try policy.redirect(to: URL(string: url)!)
            let request = try policy.request(start: 20, end: 30, validator: nil)
            for key in ["Authorization", "Cookie", "X-Session", "Referer"] {
                XCTAssertNil(request.value(forHTTPHeaderField: key))
            }
            XCTAssertEqual(request.value(forHTTPHeaderField: "User-Agent"), "test")
            XCTAssertEqual(request.value(forHTTPHeaderField: "Range"), "bytes=20-30")
        }
    }

    func testPortChangeStripsCredentialsAndHTTPSDowngradeOrUserInfoIsRejected() throws {
        var policy = PlaybackHTTPRequestPolicy(plan: try plan(headers: ["Cookie": "sid=synthetic"]))
        try policy.redirect(to: URL(string: "https://media.example.invalid:444/next")!)
        XCTAssertNil(try policy.request(start: 0, end: 1, validator: nil).value(forHTTPHeaderField: "Cookie"))
        for url in ["http://media.example.invalid/file", "file:///private/media", "https://user@media.example.invalid/a"] {
            XCTAssertThrowsError(try policy.redirect(to: URL(string: url)!))
        }
    }

    func testRedirectLimitAndFragmentRemoval() throws {
        var policy = PlaybackHTTPRequestPolicy(plan: try plan("https://media.example.invalid/a#not-transmitted"))
        XCTAssertNil(policy.current.fragment)
        for n in 1...5 { try policy.redirect(to: URL(string: "https://media.example.invalid/\(n)#x")!) }
        XCTAssertThrowsError(try policy.redirect(to: URL(string: "https://media.example.invalid/6")!)) {
            XCTAssertEqual($0 as? PlaybackNetworkError, .redirectLimit)
        }
    }

    func testReservedMalformedDuplicateAndOversizedHeadersFailClosed() throws {
        for headers in [
            ["Range": "bytes=0-"], ["Host": "evil.invalid"], ["Accept-Encoding": "gzip"],
            ["Proxy-Authorization": "synthetic"], ["If-Range": "\"invented\""],
            ["Cookie": "sid=synthetic\r\nX: bad"], ["Bad Key": "value"],
            ["User-Agent": "a", "user-agent": "b"],
            ["X-Big": String(repeating: "x", count: 16 * 1024)]
        ] {
            XCTAssertThrowsError(try plan(headers: headers)) {
                XCTAssertEqual($0 as? PlaybackNetworkError, .invalidHeaders)
            }
        }
    }

    func testHLSDoesNotSilentlyDropHeadersOrUseCustomScheme() throws {
        XCTAssertEqual(try plan("https://media.example.invalid/live.m3u8",
                                headers: ["User-Agent": "test"]).route, .systemHTTP)
        for headers in [["Referer": "https://page.example.invalid/"], ["Authorization": "Bearer synthetic"]] {
            XCTAssertEqual(try plan("https://media.example.invalid/live.m3u8", headers: headers).route, .scopedHLS)
            XCTAssertThrowsError(try plan("https://media.example.invalid/live.m3u8", headers: headers,
                                         options: .init(allowsScopedHLSLoopback: false))) {
                XCTAssertEqual($0 as? PlaybackNetworkError, .hlsCustomHeadersUnavailable)
            }
        }
        XCTAssertEqual(try plan("https://media.example.invalid/live.m3u8",
                                headers: ["Cookie": "sid=synthetic"]).route, .scopedHLS)
        XCTAssertThrowsError(try plan("https://media.example.invalid/live.m3u8",
                                      headers: ["Cookie": "sid=synthetic"],
                                      options: .init(allowsScopedHLSLoopback: false))) {
            XCTAssertEqual($0 as? PlaybackNetworkError, .hlsCookieScopeRequiresConsent)
        }
        XCTAssertEqual(try plan("https://media.example.invalid/opaque", headers: ["Referer": "synthetic"],
                                options: .init(format: .httpLiveStreaming)).route, .scopedHLS)
        XCTAssertThrowsError(try plan("https://media.example.invalid/live.m3u8",
                                      options: .init(format: .progressive)))
    }

    func testStandardCookiesAreExplicitSecureHostScopedAndNotWrittenToStorage() throws {
        let value = try plan("https://media.example.invalid/live.m3u8",
                             headers: ["User-Agent": "test", "Cookie": "sid=synthetic; empty="],
                             options: .init(format: .httpLiveStreaming, allowsStandardHTTPCookiesForHLS: true))
        XCTAssertEqual(value.route, .systemHTTP)
        let cookies = try PlaybackNetworkPlan.standardCookies(value.headers["cookie"]!, url: value.url)
        XCTAssertEqual(cookies.map(\.name), ["sid", "empty"])
        XCTAssertTrue(cookies.allSatisfy { $0.domain == "media.example.invalid" && $0.path == "/" && $0.isSecure })
        #if canImport(AVFoundation)
        let options = try NativePlaybackAsset.systemOptions(value, ambientCookies: [])
        XCTAssertEqual(options[AVURLAssetHTTPUserAgentKey] as? String, "test")
        XCTAssertEqual((options[AVURLAssetHTTPCookiesKey] as? [HTTPCookie])?.count, 2)
        XCTAssertThrowsError(try NativePlaybackAsset.systemOptions(value, ambientCookies: cookies)) {
            XCTAssertEqual($0 as? PlaybackNetworkError, .nativeCookieStorageNotIsolated)
        }
        #endif
        XCTAssertThrowsError(try plan("http://media.example.invalid/live.m3u8",
                                      headers: ["Cookie": "sid=synthetic"],
                                      options: .init(allowsStandardHTTPCookiesForHLS: true)))
        for raw in ["", "sid=a; sid=b", "$Domain=example.invalid", "no-equals", "sid=a,other=b"] {
            XCTAssertThrowsError(try PlaybackNetworkPlan.standardCookies(raw, url: value.url))
        }
    }

    func testNetworkErrorsCannotContainResourceCredentials() {
        let errors: [PlaybackNetworkError] = [.transportFailed, .redirectDenied, .httpStatus(403),
                                               .hlsCustomHeadersUnavailable, .resourceChanged]
        for error in errors {
            XCTAssertFalse(error.localizedDescription.contains("https://"))
            XCTAssertFalse(error.localizedDescription.contains("synthetic"))
        }
    }

    func testReadRangesHonorCurrentOffsetEOFAndOverflow() throws {
        let range = try ProgressiveReadRange(requestedOffset: 100, currentOffset: 130, requestedLength: 100, toEnd: false)
        XCTAssertEqual(range.start, 130)
        XCTAssertEqual(range.endExclusive, 200)
        XCTAssertNil(try ProgressiveReadRange(requestedOffset: 100, currentOffset: 130,
                                             requestedLength: 1, toEnd: true).endExclusive)
        XCTAssertThrowsError(try ProgressiveReadRange(requestedOffset: .max, currentOffset: .max,
                                                       requestedLength: 2, toEnd: false))
        XCTAssertThrowsError(try ProgressiveReadRange(requestedOffset: -1, currentOffset: 0,
                                                       requestedLength: 1, toEnd: false))
    }
}

final class ProgressiveMediaResponseTests: XCTestCase {
    private func response(_ code: Int = 206, headers: [String: String]) -> HTTPURLResponse {
        HTTPURLResponse(url: URL(string: "https://media.example.invalid/fixture.mp4")!,
                        statusCode: code, httpVersion: "HTTP/1.1", headerFields: headers)!
    }

    func testStrict206MetadataAndETagAcrossWindows() throws {
        let first = try ProgressiveMediaResponse.parse(
            response(headers: ["Content-Range": "bytes 0-99/300", "Content-Length": "100", "ETag": "\"v1\""]),
            start: 0, end: 99, previous: nil, fallbackExtension: "mp4")
        XCTAssertEqual(first.bodyBytes, 100)
        XCTAssertEqual(first.information.length, 300)
        XCTAssertEqual(first.information.mime, "video/mp4")
        XCTAssertEqual(first.information.validator, "\"v1\"")
        XCTAssertTrue(first.information.supportsRanges)
        XCTAssertNoThrow(try ProgressiveMediaResponse.parse(
            response(headers: ["Content-Range": "bytes 100-199/300", "ETag": "\"v1\""]),
            start: 100, end: 199, previous: first.information, fallbackExtension: "mp4"))
        for headers in [
            ["Content-Range": "bytes 100-199/301", "ETag": "\"v1\""],
            ["Content-Range": "bytes 100-199/300", "ETag": "\"v2\""],
            ["Content-Range": "bytes 100-199/300"]
        ] {
            XCTAssertThrowsError(try ProgressiveMediaResponse.parse(response(headers: headers),
                                                                     start: 100, end: 199,
                                                                     previous: first.information, fallbackExtension: "mp4")) {
                XCTAssertEqual($0 as? PlaybackNetworkError, .resourceChanged)
            }
        }
    }

    func testMalformedNoncontiguousEncodedAndOversizedResponsesAreRejected() {
        for headers in [
            ["Content-Range": "bytes 1-99/300"], ["Content-Range": "bytes 0-98/300"],
            ["Content-Range": "bytes 0-100/300"], ["Content-Range": "bytes 0-99/*"],
            ["Content-Range": "bytes 0-99/99"], ["Content-Range": "bytes -1-99/300"],
            ["Content-Range": "bytes +0-99/300"],
            ["Content-Range": "bytes 0-99/300", "Content-Length": "101"],
            ["Content-Range": "bytes 0-99/300", "Content-Encoding": "gzip"]
        ] {
            XCTAssertThrowsError(try ProgressiveMediaResponse.parse(response(headers: headers), start: 0, end: 99,
                                                                     previous: nil, fallbackExtension: "mp4"))
        }
        XCTAssertThrowsError(try ProgressiveMediaResponse.parse(response(200, headers: ["Content-Length": "1000000000"]),
                                                                 start: 0, end: 99, previous: nil, fallbackExtension: "mp4")) {
            XCTAssertEqual($0 as? PlaybackNetworkError, .rangeUnsupported)
        }
        XCTAssertThrowsError(try ProgressiveMediaResponse.parse(response(200, headers: ["Content-Length": "10"]),
                                                                 start: 100, end: 199, previous: nil, fallbackExtension: "mp4"))
    }

    func testFiniteSmall200AndEOF416HaveExplicitSemantics() throws {
        let small = try ProgressiveMediaResponse.parse(response(200, headers: ["Content-Length": "80"]),
                                                       start: 0, end: 99, previous: nil, fallbackExtension: "mp4")
        XCTAssertEqual(small.bodyBytes, 80)
        XCTAssertFalse(small.information.supportsRanges)
        let eof = try ProgressiveMediaResponse.parse(response(416, headers: ["Content-Range": "bytes */80"]),
                                                     start: 100, end: 199, previous: small.information, fallbackExtension: "mp4")
        XCTAssertEqual(eof.bodyBytes, 0)
        XCTAssertEqual(eof.information.length, 80)
        XCTAssertThrowsError(try ProgressiveMediaResponse.parse(response(416, headers: ["Content-Range": "bytes */300"]),
                                                                 start: 100, end: 199, previous: nil, fallbackExtension: "mp4"))
    }

    func testHLSAndErrorBodiesCannotMasqueradeAsProgressive() {
        for mime in ["application/vnd.apple.mpegurl", "application/x-mpegurl", "text/html", "application/json"] {
            XCTAssertThrowsError(try ProgressiveMediaResponse.parse(
                response(headers: ["Content-Range": "bytes 0-99/300", "Content-Type": mime]),
                start: 0, end: 99, previous: nil, fallbackExtension: "mp4"))
        }
        XCTAssertTrue(ProgressiveMediaResponse.isPlaylistPrefix(Data("#EXTM3U\n#EXT-X".utf8)))
        XCTAssertTrue(ProgressiveMediaResponse.isPlaylistPrefix(Data([0xef, 0xbb, 0xbf]) + Data("#EXTM3U\n".utf8)))
        XCTAssertFalse(ProgressiveMediaResponse.isPlaylistPrefix(Data("RIFF....WAVE".utf8)))
        XCTAssertThrowsError(try ProgressiveMediaResponse.parse(response(403, headers: ["Content-Type": "text/html"]), start: 0, end: 99,
                                                                 previous: nil, fallbackExtension: "mp4")) {
            XCTAssertEqual($0 as? PlaybackNetworkError, .httpStatus(403))
        }
    }
}

final class PlaybackNetworkBudgetTests: XCTestCase {
    func testGlobalAndPerHostBudgetsSurviveItemReplacementUntilActualCompletion() throws {
        var limits = PlaybackNetworkLimits()
        limits.activeTasks = 2; limits.tasksPerHost = 1
        let budget = PlaybackNetworkBudget(limits: limits)
        let retired = try XCTUnwrap(budget.acquire(host: "a.invalid", bytes: 256))
        XCTAssertNil(budget.acquire(host: "a.invalid", bytes: 256))
        let other = try XCTUnwrap(budget.acquire(host: "b.invalid", bytes: 256))
        XCTAssertNil(budget.acquire(host: "c.invalid", bytes: 256))
        XCTAssertFalse(other.move(to: "a.invalid"))
        XCTAssertEqual(budget.usage.activeTasks, 2)
        // Merely retiring a generation does not release its reservation.
        retired.complete()
        let next = try XCTUnwrap(budget.acquire(host: "a.invalid", bytes: 256))
        retired.complete() // idempotent; cannot steal next's slot
        XCTAssertEqual(budget.usage.activeTasks, 2)
        other.complete(); next.complete()
        XCTAssertEqual(budget.usage, .init(activeTasks: 0, reservedPayloadBytes: 0))
    }

    func testDeliveredBackingStoreRemainsChargedBeyondTaskCompletion() throws {
        var limits = PlaybackNetworkLimits()
        limits.windowBytes = 4096; limits.deliveryBytes = 4096; limits.retainedPayloadBytes = 4096
        let budget = PlaybackNetworkBudget(limits: limits)
        let reservation = try XCTUnwrap(budget.acquire(host: "a.invalid", bytes: 4096))
        var held: Data? = try reservation.payload(Data(repeating: 7, count: 4096))
        reservation.complete()
        withExtendedLifetime(held) {
            XCTAssertEqual(budget.usage.activeTasks, 0)
            XCTAssertEqual(budget.usage.reservedPayloadBytes, 4096)
            XCTAssertNil(budget.acquire(host: "a.invalid", bytes: 1))
        }
        held = nil
        XCTAssertEqual(budget.usage.reservedPayloadBytes, 0)
    }
}
