import Foundation
import Security

enum MusicNeteasePlaybackLogCodec {
    enum EncodingError: Error, Equatable {
        case inputTooLarge
        case randomUnavailable
    }

    private static let metadataLimit = 8 * 1024
    private static let recordsLimit = 64 * 1024
    private static let frameLimit = 32768
    // Little-endian UInt32 limbs of the NCBLv3 public wrapping modulus.
    private static let modulus: [UInt32] = [
        0x5cb4139d, 0x89b31c1d, 0xfa5d7aab, 0x5c564879,
        0xf263b90d, 0x3fec2fbc, 0x6ff9bc8a, 0xfd90bd46
    ]

    static func encode(metadata: Data, records: Data) throws -> Data {
        try encodeForTesting(metadata: metadata, records: records) { buffer in
            guard SecRandomCopyBytes(kSecRandomDefault, buffer.count, buffer.baseAddress!) == errSecSuccess else {
                throw EncodingError.randomUnavailable
            }
        }
    }

    // Per-call test seam; production never takes a caller-selected key or nonce.
    static func encodeForTesting(metadata: Data, records: Data,
                                 fillRandom: (UnsafeMutableBufferPointer<UInt8>) throws -> Void) throws -> Data {
        guard metadata.count <= metadataLimit, records.count <= recordsLimit else {
            throw EncodingError.inputTooLarge
        }
        var key = [UInt8](repeating: 0, count: 32)
        repeat {
            try key.withUnsafeMutableBufferPointer { try fillRandom($0) }
        } while keyAtLeastModulus(key)
        var uuid = [UInt8](repeating: 0, count: 16)
        try uuid.withUnsafeMutableBufferPointer { try fillRandom($0) }
        let wrapped = wrapKey(key)
        let compressed = zstandardRawFrame(records)
        let frameCount = (compressed.count + frameLimit - 1) / frameLimit
        let headerLength = 74 + metadata.count
        var output = [UInt8](repeating: 0, count: headerLength + compressed.count + frameCount * 6)
        output.replaceSubrange(0..<4, with: "NCBL".utf8)
        writeLittleEndian(3, to: &output, at: 4)
        writeUInt16(UInt16(headerLength), to: &output, at: 8)
        output.replaceSubrange(10..<26, with: uuid)
        output.replaceSubrange(26..<58, with: wrapped)
        // New payloads use file-local sequences. Native totalBytes includes each six-byte frame header.
        writeLittleEndian(0, to: &output, at: 58)
        writeLittleEndian(UInt32(frameCount - 1), to: &output, at: 62)
        writeLittleEndian(UInt32(compressed.count + frameCount * 6), to: &output, at: 66)
        writeUInt16(0x4343, to: &output, at: 70)
        writeUInt16(UInt16(metadata.count), to: &output, at: 72)
        encrypt(Array(metadata)[...], into: &output, at: 74, key: wrapped, uuid: uuid)
        var destination = headerLength
        for offset in stride(from: 0, to: compressed.count, by: frameLimit) {
            let length = min(frameLimit, compressed.count - offset)
            writeUInt16(UInt16(length), to: &output, at: destination)
            writeLittleEndian(UInt32(offset / frameLimit), to: &output, at: destination + 2)
            // Native framing restarts the same ChaCha stream for each encrypted payload.
            encrypt(compressed[offset..<(offset + length)], into: &output, at: destination + 6, key: key, uuid: uuid)
            destination += 6 + length
        }
        return Data(output)
    }

    private static func keyAtLeastModulus(_ key: [UInt8]) -> Bool {
        for i in 0..<32 {
            let word = modulus[7 - i / 4]
            let byte = UInt8(truncatingIfNeeded: word >> ((3 - i % 4) * 8))
            if key[i] != byte { return key[i] > byte }
        }
        return true
    }

    private static func wrapKey(_ key: [UInt8]) -> [UInt8] {
        let value = stride(from: 28, through: 0, by: -4).map { offset in
            UInt32(key[offset]) << 24 | UInt32(key[offset + 1]) << 16 |
                UInt32(key[offset + 2]) << 8 | UInt32(key[offset + 3])
        }
        // This is the protocol's raw public modular exponent, not an RSA signature API.
        var result = value
        for _ in 0..<16 { result = multiplyModulo(result, result) }
        result = multiplyModulo(result, value)
        return result.reversed().flatMap { word in
            [UInt8(truncatingIfNeeded: word >> 24), UInt8(truncatingIfNeeded: word >> 16),
             UInt8(truncatingIfNeeded: word >> 8), UInt8(truncatingIfNeeded: word)]
        }
    }

    private static func multiplyModulo(_ lhs: [UInt32], _ rhs: [UInt32]) -> [UInt32] {
        var result = [UInt32](repeating: 0, count: 8)
        var value = lhs
        for word in rhs {
            var bits = word
            for _ in 0..<32 {
                if bits & 1 != 0 { result = addModulo(result, value) }
                value = addModulo(value, value)
                bits >>= 1
            }
        }
        return result
    }

    private static func atLeastModulus(_ value: [UInt32]) -> Bool {
        for i in (0..<8).reversed() {
            if value[i] != modulus[i] { return value[i] > modulus[i] }
        }
        return true
    }

    private static func addModulo(_ lhs: [UInt32], _ rhs: [UInt32]) -> [UInt32] {
        var result = [UInt32](repeating: 0, count: 8)
        var carry: UInt64 = 0
        for i in 0..<8 {
            let sum = UInt64(lhs[i]) + UInt64(rhs[i]) + carry
            result[i] = UInt32(truncatingIfNeeded: sum)
            carry = sum >> 32
        }
        if carry != 0 || atLeastModulus(result) {
            var borrow: UInt64 = 0
            for i in 0..<8 {
                let value = UInt64(result[i]), subtrahend = UInt64(modulus[i]) + borrow
                result[i] = UInt32(truncatingIfNeeded: value &- subtrahend)
                borrow = value < subtrahend ? 1 : 0
            }
        }
        return result
    }

    private static func zstandardRawFrame(_ records: Data) -> [UInt8] {
        // Single-segment Zstandard frame, four-byte content size, one final raw block.
        // The records resource limit keeps this below Zstandard's 128 KiB block limit.
        var output: [UInt8] = [0x28, 0xb5, 0x2f, 0xfd, 0xa0]
        appendLittleEndian(UInt32(records.count), to: &output)
        let header = UInt32(records.count) << 3 | 1
        output.append(contentsOf: [UInt8(truncatingIfNeeded: header),
                                  UInt8(truncatingIfNeeded: header >> 8),
                                  UInt8(truncatingIfNeeded: header >> 16)])
        output.append(contentsOf: records)
        return output
    }

    private static func encrypt(_ input: ArraySlice<UInt8>, into output: inout [UInt8], at destination: Int,
                                key: [UInt8], uuid: [UInt8]) {
        var state: [UInt32] = [0x61707865, 0x3320646e, 0x79622d32, 0x6b206574]
        for i in 0..<8 { state.append(readLittleEndian(key, at: i * 4)) }
        state.append(readLittleEndian(uuid, at: 12) >> 2)
        for i in 0..<3 { state.append(readLittleEndian(uuid, at: i * 4)) }
        var working = [UInt32](repeating: 0, count: 16)
        for offset in stride(from: 0, to: input.count, by: 64) {
            for i in 0..<16 { working[i] = state[i] }
            for _ in 0..<10 {
                quarterRound(&working, 0, 4, 8, 12)
                quarterRound(&working, 1, 5, 9, 13)
                quarterRound(&working, 2, 6, 10, 14)
                quarterRound(&working, 3, 7, 11, 15)
                quarterRound(&working, 0, 5, 10, 15)
                quarterRound(&working, 1, 6, 11, 12)
                quarterRound(&working, 2, 7, 8, 13)
                quarterRound(&working, 3, 4, 9, 14)
            }
            for i in 0..<16 { working[i] &+= state[i] }
            for i in 0..<min(64, input.count - offset) {
                let stream = UInt8(truncatingIfNeeded: working[i >> 2] >> ((i & 3) * 8))
                output[destination + offset + i] = input[input.startIndex + offset + i] ^ stream
            }
            state[12] &+= 1
        }
    }

    private static func rotateLeft(_ value: UInt32, _ bits: UInt32) -> UInt32 {
        value << bits | value >> (32 - bits)
    }

    private static func quarterRound(_ state: inout [UInt32], _ a: Int, _ b: Int, _ c: Int, _ d: Int) {
        state[a] &+= state[b]; state[d] = rotateLeft(state[d] ^ state[a], 16)
        state[c] &+= state[d]; state[b] = rotateLeft(state[b] ^ state[c], 12)
        state[a] &+= state[b]; state[d] = rotateLeft(state[d] ^ state[a], 8)
        state[c] &+= state[d]; state[b] = rotateLeft(state[b] ^ state[c], 7)
    }

    private static func readLittleEndian(_ input: [UInt8], at offset: Int) -> UInt32 {
        UInt32(input[offset]) | UInt32(input[offset + 1]) << 8 |
            UInt32(input[offset + 2]) << 16 | UInt32(input[offset + 3]) << 24
    }

    private static func appendLittleEndian(_ value: UInt32, to output: inout [UInt8]) {
        output.append(contentsOf: [UInt8(truncatingIfNeeded: value), UInt8(truncatingIfNeeded: value >> 8),
                                  UInt8(truncatingIfNeeded: value >> 16), UInt8(truncatingIfNeeded: value >> 24)])
    }

    private static func writeLittleEndian(_ value: UInt32, to output: inout [UInt8], at offset: Int) {
        for i in 0..<4 { output[offset + i] = UInt8(truncatingIfNeeded: value >> (i * 8)) }
    }

    private static func writeUInt16(_ value: UInt16, to output: inout [UInt8], at offset: Int) {
        output[offset] = UInt8(truncatingIfNeeded: value)
        output[offset + 1] = UInt8(truncatingIfNeeded: value >> 8)
    }
}
