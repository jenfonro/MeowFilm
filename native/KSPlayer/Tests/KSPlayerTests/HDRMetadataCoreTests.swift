import XCTest
import Libavutil
@testable import KSPlayer

final class HDRMetadataCoreTests: XCTestCase {
    private func metadata() -> AVMasteringDisplayMetadata {
        var value = AVMasteringDisplayMetadata()
        value.has_primaries = 1
        value.has_luminance = 1
        value.display_primaries = (
            (AVRational(num: 708, den: 1000), AVRational(num: 292, den: 1000)),
            (AVRational(num: 170, den: 1000), AVRational(num: 797, den: 1000)),
            (AVRational(num: 131, den: 1000), AVRational(num: 46, den: 1000))
        )
        value.white_point = (AVRational(num: 3127, den: 10000), AVRational(num: 329, den: 1000))
        value.max_luminance = AVRational(num: 1000, den: 1)
        value.min_luminance = AVRational(num: 1, den: 10000)
        return value
    }

    func testMasteringVolumeUsesNormalizedST2086OrderAndUnits() throws {
        var value = try XCTUnwrap(MasteringDisplayMetadata(metadata: metadata()))
        let bytes = withUnsafeBytes(of: &value) { Array($0) }
        XCTAssertEqual(bytes, [
            0x21, 0x34, 0x9b, 0xaa, 0x19, 0x96, 0x08, 0xfc,
            0x8a, 0x48, 0x39, 0x08, 0x3d, 0x13, 0x40, 0x42,
            0x00, 0x98, 0x96, 0x80, 0x00, 0x00, 0x00, 0x01
        ])
    }

    func testPartialVolumeDoesNotInventMissingMetadata() {
        var value = metadata()
        value.has_primaries = 0
        XCTAssertNil(MasteringDisplayMetadata(metadata: value))
        value = metadata()
        value.has_luminance = 0
        XCTAssertNil(MasteringDisplayMetadata(metadata: value))
    }

    func testInvalidRationalOrOutOfRangeMetadataCannotTrapOrSerialize() {
        var value = metadata()
        value.white_point.0.den = 0
        XCTAssertNil(MasteringDisplayMetadata(metadata: value))
        value = metadata()
        value.max_luminance = AVRational(num: Int32.max, den: 1)
        XCTAssertNil(MasteringDisplayMetadata(metadata: value))
    }
}
