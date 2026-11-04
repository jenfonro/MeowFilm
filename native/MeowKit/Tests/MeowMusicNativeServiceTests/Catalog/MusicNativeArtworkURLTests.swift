import Foundation
import XCTest
import MeowMusicFeature
@testable import MeowMusicNativeService

final class MusicNativeArtworkURLTests: XCTestCase {
    func testDirectoryArtworkAcceptsAnyCDNAndUpgradesHTTP() {
        for host in ["pic.kuwo.cn", "d.musicapp.migu.cn", "independent-cdn.invalid",
                     "p1.music.126.net.independent-cdn.invalid"] {
            for scheme in ["http", "https"] {
                let value = MJ.string("\(scheme)://\(host)/cover.jpg?size=large")
                let expected = URL(string: "https://\(host)/cover.jpg?size=large")
                XCTAssertEqual(MusicNativePublicBrowseSupport.artwork(value), expected)
                for platform in [MusicPublicBrowsePlatform.kuwo, .migu] {
                    XCTAssertEqual(MusicNativePublicBrowseWire.artwork(value, platform: platform), expected)
                }
            }
        }
        XCTAssertEqual(MusicNativePublicBrowseWire.artwork(.string("/cover.jpg"), platform: .migu),
                       URL(string: "https://d.musicapp.migu.cn/cover.jpg"))
        XCTAssertNil(MusicNativePublicBrowseWire.artwork(.string("/cover.jpg"), platform: .kuwo))
    }

    func testDirectoryArtworkKeepsURLSyntaxCredentialAndResourceBoundaries() {
        let invalid: [MJ] = [
            .n(1), .string(""), .string("/relative.jpg"), .string("//cdn.invalid/a.jpg"),
            .string("javascript:alert(1)"), .string("file:///a.jpg"),
            .string("https://user@cdn.invalid/a.jpg"), .string("https://user:secret@cdn.invalid/a.jpg"),
            .string("https://cdn.invalid/a.jpg#fragment"), .string("https://cdn.invalid:8443/a.jpg"),
            .string("https://cdn.invalid/" + String(repeating: "a", count: 4096)),
        ]
        for value in invalid {
            XCTAssertNil(MusicNativePublicBrowseSupport.artwork(value))
            for platform in [MusicPublicBrowsePlatform.kuwo, .migu] {
                // Migu's documented relative-path form is intentionally accepted.
                if value.string == "/relative.jpg", platform == .migu { continue }
                XCTAssertNil(MusicNativePublicBrowseWire.artwork(value, platform: platform))
            }
        }
    }
}
