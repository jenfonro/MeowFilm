import Foundation
import XCTest
import MeowContracts
@testable import MeowPlayback

func hlsPlan(_ url: URL = URL(string: "https://source.fixture.invalid/master.m3u8")!) throws -> PlaybackNetworkPlan {
    try PlaybackNetworkPlan(resource: PlaybackResource(id: "synthetic-hls", url: url, title: "测试",
        headers: ["Authorization": "Bearer synthetic", "Cookie": "sid=synthetic",
                  "Referer": "https://page.fixture.invalid/", "X-Scope": "synthetic",
                  "User-Agent": "fixture", "Accept": "*/*"]),
        options: .init(format: .httpLiveStreaming))
}

final class HLSNetworkPolicyTests: XCTestCase {
    func testExactClientCapabilityRequestAndBoundedSingleRanges() throws {
        let registry = try HLSResourceRegistry(url: hlsPlan().url)
        let path = registry.path(registry.root)
        for method in ["GET", "HEAD"] {
            let request = try HLSClientRequest(Data("\(method) \(path) HTTP/1.1\r\nHost: 127.0.0.1:32123\r\nRange: bytes=2-19\r\n\r\n".utf8), port: 32123)
            XCTAssertEqual(request.method, method); XCTAssertEqual(request.path, path)
            XCTAssertEqual(request.range, .closed(2, 19))
        }
        XCTAssertEqual(try HLSByteRange("bytes=3-").resolved(total: 10), 3...9)
        XCTAssertEqual(try HLSByteRange("bytes=-3").resolved(total: 10), 7...9)
        XCTAssertEqual(try HLSByteRange("bytes=-30").resolved(total: 10), 0...9)
        XCTAssertNil(try HLSByteRange("bytes=10-").resolved(total: 10))
        for value in ["bytes=1-2,4-5", "bytes=-0", "bytes=-", "bytes=9-2", "bytes=+1-3", "bytes=9223372036854775808-"] {
            XCTAssertThrowsError(try HLSByteRange(value))
        }
    }

    func testUnknownURLsHostsBodiesSmugglingAndOversizedHeadersCannotAuthorizeFetch() throws {
        let registry = try HLSResourceRegistry(url: hlsPlan().url), path = registry.path(registry.root)
        let bad = [
            "GET https://other.invalid/ HTTP/1.1\r\nHost: 127.0.0.1:32123\r\n\r\n",
            "POST \(path) HTTP/1.1\r\nHost: 127.0.0.1:32123\r\n\r\n",
            "GET \(path)?url=https://other.invalid/ HTTP/1.1\r\nHost: 127.0.0.1:32123\r\n\r\n",
            "GET \(path) HTTP/1.1\r\nHost: localhost:32123\r\n\r\n",
            "GET \(path) HTTP/1.1\r\nHost: 127.0.0.1:32123\r\nhost: other.invalid\r\n\r\n",
            "GET \(path) HTTP/1.1\r\nHost: 127.0.0.1:32123\r\nContent-Length: 1\r\n\r\n",
            "GET \(path) HTTP/1.1\r\nHost: 127.0.0.1:32123\r\nTransfer-Encoding: chunked\r\n\r\n",
            "GET \(path) HTTP/1.1\r\nHost: 127.0.0.1:32123\r\nOrigin: https://other.invalid\r\n\r\n",
            String(repeating: "a", count: HLSClientRequest.maximumHeaderBytes + 1)
        ]
        for value in bad { XCTAssertThrowsError(try HLSClientRequest(Data(value.utf8), port: 32123)) }
        XCTAssertNil(registry.lookup(path: "/unknown/resource.bin"))
        XCTAssertNil(registry.lookup(path: path + "?extra"))
    }

    func testResourceAndRedirectScopesDoNotRestoreHeadersOnABAReturnOrForeignPlaylist() throws {
        let plan = try hlsPlan(), registry = try HLSResourceRegistry(url: plan.url)
        var policy = try HLSRequestPolicy(plan: plan, resource: registry.root)
        XCTAssertNotNil(policy.request(method: "GET", range: nil).value(forHTTPHeaderField: "Authorization"))
        try policy.redirect(to: URL(string: "https://cdn.fixture.invalid/next.m3u8")!)
        try policy.redirect(to: plan.url)
        for key in ["Authorization", "Cookie", "Referer", "X-Scope"] {
            XCTAssertNil(policy.request(method: "GET", range: .closed(10, 20)).value(forHTTPHeaderField: key))
        }
        XCTAssertEqual(policy.request(method: "GET", range: .closed(10, 20)).value(forHTTPHeaderField: "Range"), "bytes=10-20")
        XCTAssertEqual(policy.request(method: "GET", range: nil).value(forHTTPHeaderField: "User-Agent"), "fixture")
        let returned = try registry.register(reference: "https://source.fixture.invalid/segment.ts",
            base: policy.current, kind: .media, inheritsHeaders: policy.permitsHeaders)
        XCTAssertFalse(returned.permitsHeaders)
        let childPolicy = try HLSRequestPolicy(plan: plan, resource: returned)
        XCTAssertNil(childPolicy.request(method: "HEAD", range: nil).value(forHTTPHeaderField: "Cookie"))
        XCTAssertThrowsError(try policy.redirect(to: URL(string: "http://source.fixture.invalid/segment.ts")!))
    }

    func testResponseRangeIntegrityAndUnsupportedBodiesFailClosed() throws {
        let plan = try hlsPlan(), registry = try HLSResourceRegistry(url: plan.url)
        let request = try HLSClientRequest(Data("GET \(registry.path(registry.root)) HTTP/1.1\r\nHost: 127.0.0.1:32123\r\nRange: bytes=5-9\r\n\r\n".utf8), port: 32123)
        func response(_ status: Int, _ fields: [String: String]) -> HTTPURLResponse {
            HTTPURLResponse(url: plan.url, statusCode: status, httpVersion: "HTTP/1.1", headerFields: fields)!
        }
        let accepted = try HLSUpstreamResponse.parse(response(206, ["Content-Range": "bytes 5-9/100", "Content-Length": "5"]),
                                                    kind: .media, request: request)
        XCTAssertEqual(accepted.length, 5)
        for headers in [
            ["Content-Range": "bytes 6-9/100"], ["Content-Range": "bytes 5-10/100"],
            ["Content-Range": "bytes 5-9/100", "Content-Length": "6"],
            ["Content-Range": "bytes 5-9/100", "Content-Encoding": "gzip"],
            ["Content-Range": "bytes 5-9/100", "Content-Type": "application/vnd.apple.mpegurl"]
        ] {
            XCTAssertThrowsError(try HLSUpstreamResponse.parse(response(206, headers), kind: .media, request: request))
        }
        XCTAssertThrowsError(try HLSUpstreamResponse.parse(response(200, ["Content-Length": "100"]), kind: .media, request: request))
    }

    func testSharedListenerConnectionAndPayloadBudgetsIncludeRetiringAttempts() throws {
        var limits = PlaybackNetworkLimits(); limits.hlsListeners = 1; limits.hlsConnections = 1
        let budget = PlaybackNetworkBudget(limits: limits)
        let listener = try XCTUnwrap(budget.acquireAuxiliary(.listener, bytes: 0))
        let client = try XCTUnwrap(budget.acquireAuxiliary(.connection, bytes: 100))
        let task = try XCTUnwrap(budget.acquire(host: "fixture.invalid", bytes: 1024, additionalStorageBytes: 4096))
        XCTAssertNil(budget.acquireAuxiliary(.listener, bytes: 0))
        XCTAssertNil(budget.acquireAuxiliary(.connection, bytes: 1))
        XCTAssertEqual(budget.usage.reservedPayloadBytes, 5220)
        task.complete()
        XCTAssertEqual(budget.usage.activeConnections, 1)
        client.complete(); listener.complete(); listener.complete()
        XCTAssertEqual(budget.usage, .init(activeTasks: 0, reservedPayloadBytes: 0))
    }
}
