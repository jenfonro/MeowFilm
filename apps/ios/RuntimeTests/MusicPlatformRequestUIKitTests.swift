#if os(iOS)
import Foundation
import XCTest
import ImageIO
@testable import MeowMusicPlatform

final class MusicPlatformRequestUIKitTests: XCTestCase {
    func testNeteaseLoginQRCodeProducesNativePNG() throws {
        try checkPNG(NeteaseMusicAccountAdapter.qrImage("synthetic-ios-key"))
    }

    func testKugouLoginQRCodeProducesNativePNG() throws {
        try checkPNG(KugouMusicAccountAdapter.qrImage("synthetic-ios-key"))
    }

    func testWEAPIEncryptionUsesNativeSecurityOnIOS() throws {
        let value = try MusicNeteaseCrypto.weapi(Data(#"{"synthetic":true}"#.utf8), secret: "0123456789abcdef")
        XCTAssertFalse(try XCTUnwrap(value["params"]).isEmpty)
        XCTAssertEqual(try XCTUnwrap(value["encSecKey"]).count, 256)
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
