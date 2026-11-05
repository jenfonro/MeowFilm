import Foundation
import XCTest
@testable import MeowMusicFeature

final class MusicArtworkURLTests: XCTestCase {
    func testAllSupportedPlatformImagesKeepTheirCompleteQueryAndUseHTTPS() throws {
        for host in ["p1.music.126.net", "p4.music.163.net", "music.163.com", "y.gtimg.cn",
                     "y.qq.com", "p.qpic.cn", "q.qlogo.cn", "imge.kugou.com", "img.kgimg.com",
                     "img.kugou.net", "img.kuwo.cn", "d.musicapp.migu.cn", "independent-cdn.invalid",
                     "p1.music.126.net.evil.invalid", "127.0.0.1"] {
            let source = "http://\(host)/cover.jpg?param=800y800&quality=90&extra=a%2Fb"
            let image = try XCTUnwrap(MusicArtworkURL.normalized(source))
            XCTAssertEqual(image.absoluteString, source.replacingOccurrences(of: "http:", with: "https:"))
            XCTAssertTrue(MusicArtworkHTTP.permits(image))
        }
        XCTAssertEqual(MusicArtworkURL.normalized("http://imge.kugou.com:80/{size}/x.jpg")?.absoluteString,
                       "https://imge.kugou.com/480/x.jpg")
        XCTAssertEqual(MusicArtworkURL.normalized("http://cdn.invalid:443/x.jpg")?.absoluteString,
                       "https://cdn.invalid:443/x.jpg")
    }
    func testArtworkStillRejectsCredentialsUnsafeURLFormsAndNonHTTPResources() {
        for text in ["https://user:secret@p1.music.126.net/a.jpg", "https://p1.music.126.net:444/a.jpg",
                     "https:///a.jpg", "http://cdn.invalid:444/a.jpg",
                     "file:///a.jpg", "javascript:alert(1)", "https://p1.music.126.net/a.jpg#fragment",
                     "https://p1.music.126.net/a\\b.jpg", "https://p1.music.126.net/a\nb.jpg"] {
            XCTAssertNil(MusicArtworkURL.normalized(text), text)
        }
    }
}
