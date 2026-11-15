import Foundation
import XCTest
import MeowMusicFeature
import MeowMusicTestSupport
import MeowRuntime
@testable import MeowMusicNativeService

final class MusicNativeArtworkURLTests: XCTestCase {
    func testCanonicalDirectoryArtworkAcceptsAnyCDNAndPreservesResourceQuery() async throws {
        var urls: [String] = []
        for host in ["pic.kuwo.cn", "d.musicapp.migu.cn", "independent-cdn.invalid",
                     "p1.music.126.net.independent-cdn.invalid"] {
            for scheme in ["http", "https"] { urls.append("\(scheme)://\(host)/cover.jpg?size=large&extra=a%2Fb") }
        }
        urls.append("https://independent-cdn.invalid/" + String(repeating: "a", count: 4096))
        for platform in [MusicPublicBrowsePlatform.kuwo, .migu] {
            let rows = urls.enumerated().map { index, url in RuntimeValue.object([
                "id": .string("collection:\(index)"), "cover": .string(url)
            ]) }
            let peer = NativeMusicResultFixture([.object(["items": .array(rows), "total": .number(Double(rows.count))])])
            let service = try await NativeMusicFixture.service(platform: peer)
            let page = try await service.publicPlaylists(platform: platform, query: "covers", cursor: nil)
            XCTAssertEqual(page.items.map(\.artwork), urls.map { URL(string: $0.replacingOccurrences(of: "http:", with: "https:")) })
            let calls = await peer.calls(); XCTAssertEqual(calls.map(\.operation), ["search"])
            await service.close()
        }
    }

    func testCanonicalDirectoryArtworkKeepsURLSyntaxCredentialAndResourceBoundaries() async throws {
        let invalid: [RuntimeValue] = [
            .number(1), .string(""), .string("/relative.jpg"), .string("//cdn.invalid/a.jpg"),
            .string("javascript:alert(1)"), .string("file:///a.jpg"),
            .string("https://user@cdn.invalid/a.jpg"), .string("https://user:secret@cdn.invalid/a.jpg"),
            .string("https://cdn.invalid/a.jpg#fragment"), .string("https://cdn.invalid:8443/a.jpg"),
            .string("https://cdn.invalid/" + String(repeating: "a", count: 8193)),
        ]
        for platform in [MusicPublicBrowsePlatform.kuwo, .migu] {
            let rows = invalid.enumerated().map { index, value in RuntimeValue.object([
                "id": .string("collection:\(index)"), "cover": value
            ]) }
            let peer = NativeMusicResultFixture([.object(["items": .array(rows), "total": .number(Double(rows.count))])])
            let service = try await NativeMusicFixture.service(platform: peer)
            let page = try await service.publicPlaylists(platform: platform, query: "covers", cursor: nil)
            XCTAssertEqual(page.items.count, invalid.count)
            XCTAssertTrue(page.items.allSatisfy { $0.artwork == nil })
            await service.close()
        }
    }
}
