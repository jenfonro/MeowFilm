import Foundation
import XCTest
import MeowMusicFeature
@testable import MeowMusicPlatform

final class MusicAccountHTTPRoutingTests: XCTestCase {
    private func assertRejected(_ request: MusicAccountHTTPRequest, by transport: URLSessionMusicAccountTransport,
                                as expected: MusicServiceError, file: StaticString = #filePath, line: UInt = #line) async {
        do {
            _ = try await transport.send(request)
            XCTFail("Closed transport accepted a request", file: file, line: line)
        } catch { XCTAssertEqual(error as? MusicServiceError, expected, file: file, line: line) }
    }

    func testKugouRoutesUseExactMethodAndRouterWithoutOpeningNetwork() async {
        let transport = URLSessionMusicAccountTransport()
        transport.close() // A regressed validator must not contact a platform.
        for route in MusicKugouRoute.allCases {
            let url = URL(string: "https://" + route.host + route.path)!
            let headers = route.router.map { ["x-router": $0] } ?? [:]
            await assertRejected(.init(url: url, method: route.method, headers: headers), by: transport, as: .closed)
            let wrongMethod = route.method == "GET" ? "POST" : "GET"
            await assertRejected(.init(url: url, method: wrongMethod, headers: headers), by: transport, as: .invalidRequest)
            await assertRejected(.init(url: url, method: route.method, headers: ["x-router": "other.invalid"]),
                                 by: transport, as: .invalidRequest)
            if let router = route.router {
                await assertRejected(.init(url: url, method: route.method), by: transport, as: .invalidRequest)
                await assertRejected(.init(url: url, method: route.method, headers: ["X-Router": router]),
                                     by: transport, as: .closed)
                await assertRejected(.init(url: url, method: route.method,
                                           headers: ["x-router": router, "X-Router": router]),
                                     by: transport, as: .invalidRequest)
            }
            for bad in ["one\r\ntwo", "one\rtwo", "one\ntwo", "one\0two"] {
                for extra in [["X-Synthetic": bad], [bad: "synthetic"]] {
                    let unsafe = headers.merging(extra) { _, right in right }
                    await assertRejected(.init(url: url, method: route.method, headers: unsafe),
                                         by: transport, as: .invalidRequest)
                }
            }
        }
        // Subscription is the direct gateway route, not a cloudlist router.
        let subscription = URL(string: "https://gateway.kugou.com/pubsongs/v2/get_other_list_file_nofilt")!
        await assertRejected(.init(url: subscription), by: transport, as: .closed)
        await assertRejected(.init(url: subscription, headers: ["x-router": "cloudlist.service.kugou.com"]),
                             by: transport, as: .invalidRequest)
        for raw in ["https://login-user.kugou.com/v5/login_by_token",
                    "https://gateway.kugou.com/v2/search/song",
                    "https://gateway.kugou.com/unknown",
                    "http://gateway.kugou.com/v5/url",
                    "https://gateway.kugou.com.evil.invalid/v5/url"] {
            await assertRejected(.init(url: URL(string: raw)!, headers: ["x-router": "trackercdn.kugou.com"]),
                                 by: transport, as: .invalidRequest)
        }
        for raw in ["https://u.y.qq.com/cgi-bin/musicu.fcg", "https://music.163.com/weapi/w/nuser/account/get"] {
            await assertRejected(.init(url: URL(string: raw)!, method: "POST"), by: transport, as: .closed)
        }
    }

    func testVerificationHeaderIsCapturedAndEveryCapturedValueRejectsControls() throws {
        let fields: [AnyHashable: Any] = [
            "SSA-Code": "synthetic-verification", "Content-Type": "application/json",
            "X-Unregistered": "ignored"
        ]
        let captured = try URLSessionMusicAccountTransport.capturedHeaders(fields)
        XCTAssertEqual(captured, ["ssa-code": "synthetic-verification", "content-type": "application/json"])
        for name in ["Set-Cookie", "Location", "Content-Type", "X-Encr-SSID", "X-Encr-SSKey", "SSA-Code"] {
            for bad in ["one\r\ntwo", "one\rtwo", "one\ntwo", "one\0two"] {
                XCTAssertThrowsError(try URLSessionMusicAccountTransport.capturedHeaders([AnyHashable(name): bad])) {
                    XCTAssertEqual($0 as? MusicServiceError, .upstreamInvalid)
                }
            }
            XCTAssertThrowsError(try URLSessionMusicAccountTransport.capturedHeaders([AnyHashable(name): 1])) {
                XCTAssertEqual($0 as? MusicServiceError, .limit)
            }
            XCTAssertThrowsError(try URLSessionMusicAccountTransport.capturedHeaders([AnyHashable(name): String(repeating: "a", count: 32_769)])) {
                XCTAssertEqual($0 as? MusicServiceError, .limit)
            }
        }
    }
}
