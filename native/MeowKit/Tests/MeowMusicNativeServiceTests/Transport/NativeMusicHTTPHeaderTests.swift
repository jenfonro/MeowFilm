import Foundation
import XCTest
import MeowMusicFeature
import MeowMusicTestSupport
@testable import MeowMusicNativeService

final class NativeMusicHTTPHeaderTests: XCTestCase {
    func testNativeConsumersSendBusinessParametersWithoutUpstreamHeadersOrCredentialPayloads() async throws {
        let peer = NativeMusicResultFixture([try MusicPlatformFixture.page([NativeMusicFixture.track()], platform: "tx")])
        let service = try await NativeMusicFixture.service(platform: peer)
        _ = try await service.search(platform: "tx", query: "A + B & 中文", page: 1, limit: 30)
        let calls = await peer.calls(), request = try XCTUnwrap(calls.first)
        XCTAssertEqual(request.transportOperation, "request")
        XCTAssertEqual(request.operation, "search")
        XCTAssertEqual(request.parameters[music: "keyword"].musicText, "A + B & 中文")
        for field in ["headers", "url", "cookie", "device", "context"] { XCTAssertEqual(request.parameters[music: field], .null) }
        await service.close()
    }
}
