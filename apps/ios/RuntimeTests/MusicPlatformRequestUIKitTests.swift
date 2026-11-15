#if os(iOS)
import Foundation
import XCTest
import ImageIO
@testable import MeowMusicPlatform

final class MusicPlatformRequestUIKitTests: XCTestCase {
    func testNeteaseLoginQRCodeProducesNativePNG() throws {
        try checkPNG(MusicLoginQRCode.encode("https://music.163.com/login?codekey=synthetic-ios-key"))
    }

    func testKugouLoginQRCodeProducesNativePNG() throws {
        try checkPNG(MusicLoginQRCode.encode("https://www.kugou.com/login/qr?key=synthetic-ios-key"))
    }

    private func checkPNG(_ data: Data) throws {
        XCTAssertLessThanOrEqual(data.count, 1024 * 1024)
        let source = try XCTUnwrap(CGImageSourceCreateWithData(data as CFData, nil))
        let image = try XCTUnwrap(CGImageSourceCreateImageAtIndex(source, 0, nil))
        XCTAssertGreaterThan(image.width, 100)
        XCTAssertEqual(image.width, image.height)
    }
}
#endif
