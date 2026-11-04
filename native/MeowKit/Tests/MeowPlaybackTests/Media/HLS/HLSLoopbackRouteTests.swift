#if os(iOS) || os(macOS)
import Foundation
import XCTest
import AVFoundation
import MeowContracts
@testable import MeowPlayback

final class HLSLoopbackRouteTests: XCTestCase {
    func testRealLoopbackMasterMediaKeyHeadAndRangeUseOnlyRegisteredNativeResources() async throws {
        let fixture = HLSHTTPFixture([
            "/master.m3u8": .body(Data("#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=1000\nvideo/list.m3u8\n".utf8), mime: "application/vnd.apple.mpegurl"),
            "/video/list.m3u8": .body(Data("#EXTM3U\n#EXT-X-TARGETDURATION:4\n#EXT-X-KEY:METHOD=AES-128,URI=\"key.bin\"\n#EXTINF:4,\nsegment.ts\n#EXT-X-ENDLIST\n".utf8), mime: "application/vnd.apple.mpegurl"),
            "/video/key.bin": .body(Data(repeating: 7, count: 16), mime: "application/octet-stream"),
            "/video/segment.ts": .body(Data((0..<2048).map { UInt8($0 % 251) }), mime: "video/mp2t")
        ])
        defer { fixture.close() }
        let budget = PlaybackNetworkBudget()
        let route = try HLSLoopbackRoute(plan: hlsPlan(fixture.url), budget: budget, configuration: fixture.configuration()) {
            _ in XCTFail("Unexpected controlled HLS failure")
        }
        defer { route.invalidate() }
        let rootURL = try await readyHLS(route)
        XCTAssertEqual(rootURL.host, "127.0.0.1"); XCTAssertNotEqual(rootURL.port, 0)
        let binding = NativePlaybackAsset(hls: route, url: rootURL)
        XCTAssertEqual(binding.asset.url.scheme, "http")
        XCTAssertFalse(binding.asset.url.absoluteString.contains(fixture.host))
        let (master, masterResponse) = try await hlsFetch(rootURL)
        XCTAssertEqual(masterResponse.statusCode, 200)
        let variantPath = try XCTUnwrap(pathsInPlaylist(String(decoding: master, as: UTF8.self)).first)
        let variant = try XCTUnwrap(URL(string: variantPath, relativeTo: rootURL)?.absoluteURL)
        let (playlist, _) = try await hlsFetch(variant)
        let paths = try pathsInPlaylist(String(decoding: playlist, as: UTF8.self))
        XCTAssertEqual(paths.count, 2)
        let keyURL = try XCTUnwrap(URL(string: paths[0], relativeTo: rootURL)?.absoluteURL)
        let segmentURL = try XCTUnwrap(URL(string: paths[1], relativeTo: rootURL)?.absoluteURL)
        XCTAssertEqual(keyURL.port, segmentURL.port)
        async let keyFetch = hlsFetch(keyURL)
        async let segmentFetch = hlsFetch(segmentURL, range: "bytes=17-99")
        let (key, keyResponse) = try await keyFetch, (segment, rangeResponse) = try await segmentFetch
        XCTAssertEqual(key, Data(repeating: 7, count: 16)); XCTAssertEqual(keyResponse.statusCode, 200)
        XCTAssertEqual(rangeResponse.statusCode, 206)
        XCTAssertEqual(rangeResponse.value(forHTTPHeaderField: "Content-Range"), "bytes 17-99/2048")
        XCTAssertEqual(segment, Data((17...99).map { UInt8($0 % 251) }))
        let (empty, head) = try await hlsFetch(segmentURL, method: "HEAD")
        XCTAssertTrue(empty.isEmpty); XCTAssertEqual(head.value(forHTTPHeaderField: "Content-Length"), "2048")
        let (small, partial) = try await hlsFetch(variant, range: "bytes=0-6")
        XCTAssertEqual(partial.statusCode, 206); XCTAssertEqual(small, Data("#EXTM3U".utf8))
        for request in fixture.requests {
            XCTAssertEqual(request.value(forHTTPHeaderField: "Authorization"), "Bearer synthetic")
            XCTAssertEqual(request.value(forHTTPHeaderField: "Cookie"), "sid=synthetic")
            XCTAssertEqual(request.value(forHTTPHeaderField: "Referer"), "https://page.fixture.invalid/")
            XCTAssertFalse(request.httpShouldHandleCookies)
        }
        XCTAssertTrue(fixture.requests.contains { $0.httpMethod == "HEAD" && $0.url?.path == "/video/segment.ts" })
    }

    func testSamePortCapabilitiesAndTwoAttemptListenersCannotReadEachOthersResources() async throws {
        let fixture = HLSHTTPFixture(["/master.m3u8": .body(Data("#EXTM3U\n#EXTINF:1,\nsegment.ts\n".utf8),
                                                          mime: "application/vnd.apple.mpegurl")])
        defer { fixture.close() }
        let budget = PlaybackNetworkBudget()
        let first = try HLSLoopbackRoute(plan: hlsPlan(fixture.url), budget: budget, configuration: fixture.configuration()) { _ in XCTFail() }
        let second = try HLSLoopbackRoute(plan: hlsPlan(fixture.url), budget: budget, configuration: fixture.configuration()) { _ in XCTFail() }
        defer { first.invalidate(); second.invalidate() }
        let a = try await readyHLS(first), b = try await readyHLS(second)
        XCTAssertNotEqual(a.port, b.port); XCTAssertNotEqual(a.path, b.path)
        var mixed = try XCTUnwrap(URLComponents(url: a, resolvingAgainstBaseURL: false)); mixed.path = b.path
        let (_, rejected) = try await hlsFetch(XCTUnwrap(mixed.url))
        XCTAssertEqual(rejected.statusCode, 404)
        XCTAssertTrue(fixture.requests.isEmpty)
        first.invalidate()
        let (_, accepted) = try await hlsFetch(b)
        XCTAssertEqual(accepted.statusCode, 200)
        XCTAssertEqual(fixture.requests.count, 1)
    }

    func testForeignPlaylistAndSegmentDoNotReceiveOriginHeadersOrResponseCookies() async throws {
        let cdn = HLSHTTPFixture([
            "/master.m3u8": .body(Data("#EXTM3U\n#EXTINF:1,\npublic.ts\n".utf8), mime: "application/vnd.apple.mpegurl"),
            "/public.ts": .body(Data(repeating: 23, count: 32), mime: "video/mp2t")
        ])
        let origin = HLSHTTPFixture([
            "/master.m3u8": .body(Data("#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=1000\n\(cdn.url.absoluteString)\n".utf8),
                                   mime: "application/vnd.apple.mpegurl")
        ])
        defer { cdn.close(); origin.close() }
        let route = try HLSLoopbackRoute(plan: hlsPlan(origin.url), budget: PlaybackNetworkBudget(),
                                         configuration: origin.configuration()) { _ in XCTFail() }
        defer { route.invalidate() }
        let root = try await readyHLS(route)
        let (master, _) = try await hlsFetch(root)
        let variantPath = try XCTUnwrap(pathsInPlaylist(String(decoding: master, as: UTF8.self)).first)
        let (playlist, _) = try await hlsFetch(XCTUnwrap(URL(string: variantPath, relativeTo: root)?.absoluteURL))
        let segmentPath = try XCTUnwrap(pathsInPlaylist(String(decoding: playlist, as: UTF8.self)).first)
        let (bytes, _) = try await hlsFetch(XCTUnwrap(URL(string: segmentPath, relativeTo: root)?.absoluteURL))
        XCTAssertEqual(bytes, Data(repeating: 23, count: 32))
        XCTAssertEqual(cdn.requests.count, 2)
        for request in cdn.requests {
            for header in ["Cookie", "Authorization", "Referer", "X-Scope"] {
                XCTAssertNil(request.value(forHTTPHeaderField: header))
            }
            XCTAssertEqual(request.value(forHTTPHeaderField: "User-Agent"), "fixture")
            XCTAssertFalse(request.httpShouldHandleCookies)
        }
        XCTAssertEqual(origin.requests.first?.value(forHTTPHeaderField: "Authorization"), "Bearer synthetic")
    }

    func testStreamingBeyondAWindowUsesSocketBackpressureAndNativePhysicalCompletion() async throws {
        let started = expectation(description: "native held stream")
        let fixture = HLSHTTPFixture([
            "/master.m3u8": .body(Data("#EXTM3U\n#EXTINF:4,\nstream.ts\n".utf8), mime: "application/vnd.apple.mpegurl"),
            "/stream.ts": .held(mime: "video/mp2t")
        ], onHold: { started.fulfill() })
        defer { fixture.close() }
        var limits = PlaybackNetworkLimits(); limits.windowBytes = 64 * 1024; limits.deliveryBytes = 8192
        let budget = PlaybackNetworkBudget(limits: limits)
        let route = try HLSLoopbackRoute(plan: hlsPlan(fixture.url), budget: budget, configuration: fixture.configuration()) { _ in XCTFail() }
        defer { route.invalidate() }
        let root = try await readyHLS(route), (playlist, _) = try await hlsFetch(root)
        let path = try XCTUnwrap(pathsInPlaylist(String(decoding: playlist, as: UTF8.self)).first)
        let finished = expectation(description: "stream EOF")
        let client = HLSStreamingClient(finished: finished)
        defer { client.close() }
        client.start(try XCTUnwrap(URL(string: path, relativeTo: root)?.absoluteURL))
        await fulfillment(of: [started], timeout: 5)
        let chunk = Data(repeating: 71, count: 16 * 1024)
        for index in 1...6 {
            let delivered = expectation(description: "bounded stream delivery \(index)")
            client.observe(count: index * chunk.count) { delivered.fulfill() }
            fixture.send(chunk)
            await fulfillment(of: [delivered], timeout: 5)
            XCTAssertLessThanOrEqual(budget.usage.reservedPayloadBytes, limits.retainedPayloadBytes)
            route.synchronized {
                XCTAssertTrue(route.transfers.values.allSatisfy { $0.buffered <= limits.windowBytes / 2 })
            }
        }
        fixture.finish()
        await fulfillment(of: [finished], timeout: 5)
        XCTAssertNil(client.error); XCTAssertEqual(client.bytes, Data(repeating: 71, count: chunk.count * 6))
    }

    func testRetirementFencesBytesAndKeepsTaskAndConnectionLeasesUntilActualDrain() async throws {
        let started = expectation(description: "held task")
        let fixture = HLSHTTPFixture(["/master.m3u8": .held(mime: "application/vnd.apple.mpegurl")],
                                     onHold: { started.fulfill() })
        defer { fixture.close() }
        let budget = PlaybackNetworkBudget()
        let route = try HLSLoopbackRoute(plan: hlsPlan(fixture.url), budget: budget, configuration: fixture.configuration()) {
            _ in XCTFail("Explicit retirement is not a playback failure")
        }
        defer { route.invalidate() }
        let root = try await readyHLS(route)
        let finished = expectation(description: "cancelled client"), client = HLSStreamingClient(finished: finished)
        defer { client.close() }
        client.start(root)
        await fulfillment(of: [started], timeout: 5)
        let drained = expectation(description: "physical task/listener/connection drain"), once = HLSOnce()
        let observation = budget.observe {
            if budget.usage == .init(activeTasks: 0, reservedPayloadBytes: 0) { once.run { drained.fulfill() } }
        }
        defer { budget.unobserve(observation) }
        route.synchronized {
            route.invalidate()
            XCTAssertEqual(budget.usage.activeTasks, 1)
            let successor = budget.acquire(host: "successor.invalid", bytes: 1)
            XCTAssertNotNil(successor); successor?.complete()
            XCTAssertTrue(route.clients.values.allSatisfy(\.closed))
        }
        await fulfillment(of: [finished, drained], timeout: 5)
        XCTAssertTrue(client.bytes.isEmpty)
        XCTAssertNotNil(client.error)
        XCTAssertEqual(budget.usage, .init(activeTasks: 0, reservedPayloadBytes: 0))
    }

    func testControllerPendingHLSStopKeepsUniquePlayerIdentityAndCannotInstallLateItem() async throws {
        let resource = try PlaybackResource(id: "same-business-resource", url: URL(string: "https://unrequested.fixture.invalid/live.m3u8")!,
                                            title: "测试", headers: ["Referer": "https://page.fixture.invalid/"])
        await MainActor.run {
            let controller = PlaybackController(), original = controller.player
            let attempt = controller.playAttempt(resource: resource, owner: "film", autoplay: false)
            XCTAssertEqual(attempt.resourceID, resource.id)
            XCTAssertTrue(controller.requiresLocalMediaFetch)
            XCTAssertFalse(controller.player.allowsExternalPlayback)
            // Listener readiness dispatches back to this actor. Stop before it
            // can create an item or perform any upstream request.
            controller.stop()
            XCTAssertFalse(controller.requiresLocalMediaFetch)
            XCTAssertTrue(controller.player === original)
            XCTAssertNil(controller.player.currentItem)
            XCTAssertEqual(controller.snapshot.phase, .idle)
        }
    }
}
#endif
