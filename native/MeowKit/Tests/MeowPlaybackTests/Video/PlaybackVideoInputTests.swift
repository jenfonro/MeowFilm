import XCTest
import MeowContracts

final class PlaybackVideoInputTests: XCTestCase {
    func testVideoInputAcceptsLocalAndRemoteURLsWithoutOriginOrResourcePathAdmission() throws {
        for value in [
            "http://127.0.0.1:12345/media/issued/manifest",
            "http://127.0.0.1:12346/media/issued/manifest", "http://127.0.0.1:12345/media/other/manifest",
            "http://127.0.0.1:12345/media/issued-extended/manifest",
            "http://127.0.0.1:12345/media/issued/../other", "http://127.0.0.1:12345/media/issued/%2e%2e/other",
            "http://127.0.0.1:12345/media/issued/manifest?url=fixture",
            "http://localhost:19191/video.mp4", "http://[::1]:19191/video.mp4",
            "http://192.168.1.2:19191/video.mp4", "https://media.invalid:8443/video.mp4",
            "https://user:synthetic@media.invalid/video.mp4?token=fixture#chapter",
            "file:///tmp/synthetic-video.mp4",
        ] {
            let url = try XCTUnwrap(URL(string: value))
            let input = try PlaybackVideoInput(resourceID: "receipt/id:+opaque", url: url,
                title: "test", format: .hls, startPosition: 7)
            XCTAssertEqual(input.url, url)
            XCTAssertEqual(input.resourceID, "receipt/id:+opaque")
            XCTAssertEqual(input.format, .hls)
            XCTAssertEqual(input.startPosition, 7)
        }
    }

    func testVideoInputRetainsResourceIdentityPositionAndSupportedProtocolRequirements() throws {
        let url = try XCTUnwrap(URL(string: "http://localhost:19191/video.mp4"))
        XCTAssertThrowsError(try PlaybackVideoInput(resourceID: "", url: url, title: "test"))
        for position in [-1, Double.infinity, Double.nan] {
            XCTAssertThrowsError(try PlaybackVideoInput(resourceID: "receipt", url: url, title: "test",
                startPosition: position))
        }
        for value in ["/relative.mp4", "ftp://media.invalid/video.mp4", "https:/missing-host.mp4"] {
            XCTAssertThrowsError(try PlaybackVideoInput(resourceID: "receipt",
                url: XCTUnwrap(URL(string: value)), title: "test"))
        }
    }

    func testVideoInputsAndKeysDoNotDisclosePrivateDataInDescriptionsOrMirror() throws {
        let kid = Data((0..<16).map(UInt8.init))
        let key = Data((16..<32).map(UInt8.init))
        let keys = try PlaybackVideoDecryption(clearKeys: [
            kid.base64EncodedString().replacingOccurrences(of: "+", with: "-").replacingOccurrences(of: "/", with: "_"):
            key.base64EncodedString().replacingOccurrences(of: "+", with: "-").replacingOccurrences(of: "/", with: "_")
        ])
        let input = try PlaybackVideoInput(resourceID: "private-resource",
            url: XCTUnwrap(URL(string: "https://media.invalid/video?token=private-test-only")), title: "private-title", decryption: keys)
        let text = String(describing: input) + String(reflecting: input) + String(describing: keys) + String(reflecting: keys)
        XCTAssertFalse(text.contains("private")); XCTAssertFalse(text.contains("10111213"))
        XCTAssertEqual(Mirror(reflecting: input).children.count, 1)
        XCTAssertEqual(Mirror(reflecting: keys).children.count, 0)
        keys.withKeyMaterial { XCTAssertEqual(Array($0.values), [key]) }
        XCTAssertThrowsError(try PlaybackVideoDecryption(clearKeys: ["wrong-size": "not-a-key"]))
    }
}
