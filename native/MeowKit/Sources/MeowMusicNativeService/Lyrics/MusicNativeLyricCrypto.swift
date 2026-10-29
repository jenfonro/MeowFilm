// SPDX-FileCopyrightText: Copyright (c) 2024 沉默の金 <cmzj@cmzj.org>
// SPDX-License-Identifier: GPL-3.0-only
// Port of fixed meowmusic cc22a7e qq/qrc.go. The PC2 offset (27) and
// nonstandard S boxes are intentional; substituting system DES is incorrect.
import Foundation
import zlib
import MeowMusicFeature

enum MusicLyricCrypto {
    static let maximum = 512 * 1024
    static func inflate(_ data: [UInt8]) throws -> String {
        var output = [UInt8](repeating: 0, count: maximum + 1), size = uLongf(maximum + 1)
        let status = uncompress(&output, &size, data, uLong(data.count))
        try musicRequire(status == Z_OK && size <= maximum, status == Z_BUF_ERROR ? .limit : .invalidLyricResponse)
        guard let text = String(bytes: output.prefix(Int(size)), encoding: .utf8) else { throw MusicServiceError.invalidLyricResponse }
        return text
    }
    static func krc(_ encoded: String) throws -> String {
        try musicRequire(encoded.utf8.count <= 2 * maximum, .limit)
        guard let data = Data(base64Encoded: encoded), data.base64EncodedString() == encoded,
              data.prefix(4) == Data("krc1".utf8) else { throw MusicServiceError.invalidLyricResponse }
        let key: [UInt8] = [64, 71, 97, 119, 94, 50, 116, 71, 81, 54, 49, 45, 206, 210, 110, 105]
        return try inflate(data.dropFirst(4).enumerated().map { $0.element ^ key[$0.offset % 16] })
    }
    static func qrc(_ encrypted: String) throws -> String {
        if encrypted.isEmpty { return "" }
        try musicRequire(encrypted.utf8.count <= 2 * maximum, .limit)
        let hex = encrypted.replacingOccurrences(of: "\\s", with: "", options: .regularExpression)
        try musicRequire(!hex.isEmpty && hex.count % 16 == 0 && hex.range(of: "^[a-fA-F0-9]+$", options: .regularExpression) != nil, .invalidLyricResponse)
        let chars = Array(hex.utf8)
        var data = stride(from: 0, to: chars.count, by: 2).map { UInt8(String(decoding: chars[$0..<$0+2], as: UTF8.self), radix: 16)! }
        let key = Array("!@#)(*$%123ZXC!@!@#)(NHL".utf8)
        let schedules = [keys(Array(key[16..<24]), decrypt: true), keys(Array(key[8..<16]), decrypt: false), keys(Array(key[0..<8]), decrypt: true)]
        for at in stride(from: 0, to: data.count, by: 8) {
            if at % 4096 == 0 { try Task.checkCancellation() }
            var bytes = Array(data[at..<at+8])
            for schedule in schedules { block(&bytes, schedule) }
            data.replaceSubrange(at..<at+8, with: bytes)
        }
        return try inflate(data)
    }
    static func keys(_ key: [UInt8], decrypt: Bool) -> [[UInt32]] {
        let pc1 = [[56,48,40,32,24,16,8,0,57,49,41,33,25,17,9,1,58,50,42,34,26,18,10,2,59,51,43,35],
                   [62,54,46,38,30,22,14,6,61,53,45,37,29,21,13,5,60,52,44,36,28,20,12,4,27,19,11,3]]
        let pc2 = [13,16,10,23,0,4,2,27,14,5,20,9,22,18,11,3,25,7,15,6,26,19,12,1,
                   40,51,30,36,46,54,29,39,50,44,32,47,43,48,38,55,33,52,45,41,49,35,28,31]
        let words = (0..<2).map { i in (0..<4).reduce(UInt32(0)) { $0 | UInt32(key[i * 4 + $1]) << ($1 * 8) } }
        var halves: [UInt32] = [0, 0]
        for h in 0..<2 { for (i, b) in pc1[h].enumerated() { halves[h] |= ((words[b / 32] >> (31 - b % 32)) & 1) << (31 - i) } }
        var result = [[UInt32]](repeating: [UInt32](repeating: 0, count: 6), count: 16)
        for (round, shift) in [1,1,2,2,2,2,2,2,1,2,2,2,2,2,2,1].enumerated() {
            for h in 0..<2 { halves[h] = (halves[h] << shift | halves[h] >> (28 - shift)) & 0xfffffff0 }
            let target = decrypt ? 15 - round : round
            for (j, bit) in pc2.enumerated() {
                let h = j >= 24 ? 1 : 0, b = bit - (h == 1 ? 27 : 0)
                // JavaScript shift counts are modulo 32, including -1.
                result[target][j / 8] |= ((halves[h] >> ((31 - b) & 31)) & 1) << (7 - j % 8)
            }
        }
        return result
    }
    static func f(_ state: UInt32, _ key: [UInt32]) -> UInt32 {
        let t1 = (state & 1) << 31 | (state & 0xf8000000) >> 1 | (state & 0x1f800000) >> 3 | (state & 0x01f80000) >> 5 | (state & 0x001f8000) >> 7
        let t2 = (state & 0x0001f800) << 15 | (state & 0x00001f80) << 13 | (state & 0x000001f8) << 11 | (state & 0x1f) << 9 | (state & 0x80000000) >> 23
        let k = [t1 >> 24 ^ key[0], (t1 >> 16 & 255) ^ key[1], (t1 >> 8 & 255) ^ key[2],
                 t2 >> 24 ^ key[3], (t2 >> 16 & 255) ^ key[4], (t2 >> 8 & 255) ^ key[5]]
        let six = [k[0] >> 2, (k[0] & 3) << 4 | k[1] >> 4, (k[1] & 15) << 2 | k[2] >> 6, k[2] & 63,
                   k[3] >> 2, (k[3] & 3) << 4 | k[4] >> 4, (k[4] & 15) << 2 | k[5] >> 6, k[5] & 63]
        var x: UInt32 = 0, result: UInt32 = 0
        for (i, v) in six.enumerated() { x |= MusicQRCTables.sbox[i][Int(v & 32 | (v & 31) >> 1 | (v & 1) << 4)] << (28 - i * 4) }
        for (i, b) in [16,25,12,11,3,20,4,15,31,17,9,6,27,14,1,22,30,24,8,18,0,5,29,23,13,19,2,26,10,21,28,7].enumerated() {
            result |= ((x >> b) & 1) << (31 - i)
        }
        return result
    }
    static func block(_ bytes: inout [UInt8], _ keys: [[UInt32]]) {
        var l: UInt32 = 0, r: UInt32 = 0
        let order = [4,5,6,7,0,1,2,3]
        for row in 0..<4 { for (col, b) in order.enumerated() {
            l |= UInt32((bytes[b] >> (6 - 2 * row)) & 1) << (31 - row * 8 - col)
            r |= UInt32((bytes[b] >> (7 - 2 * row)) & 1) << (31 - row * 8 - col)
        } }
        for i in 0..<15 { let next = f(r, keys[i]) ^ l; l = r; r = next }
        l ^= f(r, keys[15]); bytes = [UInt8](repeating: 0, count: 8)
        for row in 0..<4 { for (col, b) in order.enumerated() {
            bytes[b] |= UInt8((l >> (31 - row * 8 - col)) & 1) << (6 - 2 * row)
            bytes[b] |= UInt8((r >> (31 - row * 8 - col)) & 1) << (7 - 2 * row)
        } }
    }
}
