import Foundation
import CryptoKit
import XCTest
@testable import MeowMusicPlatform

final class MusicNeteasePlaybackLogCodecTests: XCTestCase {
    func testSharedIndependentCryptographyFixture() throws {
        // Python cryptography ChaCha20 + pow(key, 65537, N); raw frame decoded by standard libzstd.
        // No production decoder is mirrored here. Windows uses these same synthetic vectors.
        let draws = [Array(UInt8(0)...UInt8(31)), Array(UInt8(16)...UInt8(31))]
        var draw = 0
        let output = [UInt8](try MusicNeteasePlaybackLogCodec.encodeForTesting(
            metadata: Data("{\"fixture\":1}".utf8),
            records: Data((0..<65536).map { UInt8(truncatingIfNeeded: $0) })) { buffer in
                guard draw < draws.count else {
                    XCTFail("Unexpected random draw")
                    throw MusicNeteasePlaybackLogCodec.EncodingError.randomUnavailable
                }
                XCTAssertEqual(buffer.count, draws[draw].count)
                for i in buffer.indices { buffer[i] = draws[draw][i] }
                draw += 1
        })
        XCTAssertEqual(draw, 2)
        XCTAssertEqual(Array(SHA256.hash(data: Data(output))),
                       bytes("b9f9fa0e856f9f471eceb56febd04e65a220f52a26e6714b34f03ec480a9bb45"))
        XCTAssertEqual(Array(output[26..<58]),
                       bytes("dda20437ce173c34273cb03bffb85db8f3ce53bc2f1334a752303d26890094af"))
        XCTAssertEqual(Array(output[74..<87]), bytes("daba5c8865dc9e78fbccc0c2b1"))
        var ciphertext = Data()
        var offset = read16(output, at: 8)
        for frame in frames(output) {
            ciphertext.append(contentsOf: output[(offset + 6)..<(offset + 6 + frame.length)])
            offset += frame.length + 6
        }
        XCTAssertEqual(Array(SHA256.hash(data: ciphertext)),
                       bytes("4a055e6685394f6030c2a1410bd443e5a2b5e87e597f503f61f87abdb6ee0457"))
        let firstFrame = read16(output, at: 8) + 6
        XCTAssertEqual(Array(output[firstFrame..<(firstFrame + 32)]),
                       bytes("32816c50960617d8be56dc3f0adb5b05e9806668770571a129ea6682ff79a58e"))
    }

    func testCompleteMaximumSizeContainer() throws {
        let metadata = Data(repeating: 0xff, count: 8192)
        let records = Data((0..<65536).map { UInt8(truncatingIfNeeded: $0) })
        let output = [UInt8](try MusicNeteasePlaybackLogCodec.encode(metadata: metadata, records: records))
        XCTAssertEqual(Array(output.prefix(4)), Array("NCBL".utf8))
        XCTAssertEqual(read32(output, at: 4), 3)
        XCTAssertEqual(read16(output, at: 8), 8266)
        XCTAssertEqual(read16(output, at: 70), 0x4343)
        XCTAssertEqual(read16(output, at: 72), 8192)
        let parsedFrames = frames(output)
        XCTAssertEqual(read32(output, at: 58), 0)
        XCTAssertEqual(read32(output, at: 62), 2)
        XCTAssertEqual(read32(output, at: 66), 65566)
        XCTAssertEqual(parsedFrames.map(\.length), [32768, 32768, 12])
        for (first, second) in zip(parsedFrames, parsedFrames.dropFirst()) {
            XCTAssertEqual(second.sequence, first.sequence + 1)
        }
        XCTAssertEqual(output.count, 8266 + 65548 + 18)
        XCTAssertEqual(metadata, Data(repeating: 0xff, count: 8192))
        XCTAssertEqual(records, Data((0..<65536).map { UInt8(truncatingIfNeeded: $0) }))
    }

    func testEmptyBytesAndFrameBoundaries() throws {
        for length in [0, 1, 32755, 32756, 32757, 65524, 65525, 65536] {
            let output = [UInt8](try MusicNeteasePlaybackLogCodec.encode(
                metadata: Data(), records: Data(repeating: 0, count: length)))
            XCTAssertEqual(read16(output, at: 8), 74)
            XCTAssertEqual(read16(output, at: 72), 0, "No JSON validation in a byte codec")
            let parsedFrames = frames(output)
            XCTAssertEqual(parsedFrames.count, (length + 12 + 32767) / 32768)
            XCTAssertEqual(parsedFrames.reduce(0) { $0 + $1.length }, length + 12)
            XCTAssertTrue(parsedFrames.allSatisfy { $0.length > 0 && $0.length <= 32768 })
        }
    }

    func testFreshNonceAndWrappedKeyPerPayload() throws {
        let first = [UInt8](try MusicNeteasePlaybackLogCodec.encode(
            metadata: Data([0xff]), records: Data([0x80, 0])))
        let second = [UInt8](try MusicNeteasePlaybackLogCodec.encode(
            metadata: Data([0xff]), records: Data([0x80, 0])))
        XCTAssertNotEqual(Array(first[10..<26]), Array(second[10..<26]))
        XCTAssertNotEqual(Array(first[26..<58]), Array(second[26..<58]))
        XCTAssertNotEqual(first, second)
    }

    func testOversizedInputsAreRejectedWithoutTruncation() {
        XCTAssertThrowsError(try MusicNeteasePlaybackLogCodec.encode(
            metadata: Data(repeating: 0, count: 8193), records: Data())) {
                XCTAssertEqual($0 as? MusicNeteasePlaybackLogCodec.EncodingError, .inputTooLarge)
        }
        XCTAssertThrowsError(try MusicNeteasePlaybackLogCodec.encode(
            metadata: Data(), records: Data(repeating: 0, count: 65537))) {
                XCTAssertEqual($0 as? MusicNeteasePlaybackLogCodec.EncodingError, .inputTooLarge)
        }
    }

    func testUnsignedWrappingKeyRejectionSamplingAndPadding() throws {
        let modulus = bytes("fd90bd466ff9bc8a3fec2fbcf263b90d5c564879fa5d7aab89b31c1d5cb4139d")
        var largestKey = modulus
        largestKey[31] -= 1
        let uuid = Array(UInt8(16)...UInt8(31))
        let draws = [modulus, [UInt8](repeating: 0xff, count: 32), largestKey, uuid]
        var count = 0
        let output = [UInt8](try MusicNeteasePlaybackLogCodec.encodeForTesting(
            metadata: Data(), records: Data()) { buffer in
                guard count < draws.count else {
                    XCTFail("Unexpected random draw")
                    throw MusicNeteasePlaybackLogCodec.EncodingError.randomUnavailable
                }
                XCTAssertEqual(buffer.count, draws[count].count)
                for i in buffer.indices { buffer[i] = draws[count][i] }
                count += 1
        })
        XCTAssertEqual(count, 4)
        // (N - 1)^65537 mod N == N - 1, with the unsigned high bit preserved.
        XCTAssertEqual(Array(output[26..<58]), largestKey)
        XCTAssertEqual(Array(output[10..<26]), uuid)
        let zero = [UInt8](try MusicNeteasePlaybackLogCodec.encodeForTesting(
            metadata: Data(), records: Data()) { buffer in
                for i in buffer.indices { buffer[i] = 0 }
        })
        XCTAssertEqual(Array(zero[26..<58]), [UInt8](repeating: 0, count: 32))
    }

    func testRandomSourceFailureNeverUsesFallback() {
        XCTAssertThrowsError(try MusicNeteasePlaybackLogCodec.encodeForTesting(
            metadata: Data(), records: Data(), fillRandom: { _ in
                throw MusicNeteasePlaybackLogCodec.EncodingError.randomUnavailable
        })) {
            XCTAssertEqual($0 as? MusicNeteasePlaybackLogCodec.EncodingError, .randomUnavailable)
        }
    }

    private func frames(_ output: [UInt8]) -> [(length: Int, sequence: UInt32)] {
        var result: [(length: Int, sequence: UInt32)] = []
        var offset = read16(output, at: 8)
        while offset < output.count {
            guard offset + 6 <= output.count else { XCTFail("Incomplete frame header"); return [] }
            let length = read16(output, at: offset)
            guard offset + 6 + length <= output.count else { XCTFail("Incomplete frame payload"); return [] }
            result.append((length, read32(output, at: offset + 2)))
            offset += 6 + length
        }
        XCTAssertEqual(offset, output.count)
        return result
    }

    private func read16(_ value: [UInt8], at offset: Int) -> Int {
        Int(value[offset]) | Int(value[offset + 1]) << 8
    }

    private func read32(_ value: [UInt8], at offset: Int) -> UInt32 {
        UInt32(value[offset]) | UInt32(value[offset + 1]) << 8 |
            UInt32(value[offset + 2]) << 16 | UInt32(value[offset + 3]) << 24
    }

    private func bytes(_ hex: String) -> [UInt8] {
        let characters = Array(hex.utf8)
        func nibble(_ value: UInt8) -> UInt8 { value <= 57 ? value - 48 : value - 87 }
        return stride(from: 0, to: characters.count, by: 2).map {
            nibble(characters[$0]) << 4 | nibble(characters[$0 + 1])
        }
    }
}
