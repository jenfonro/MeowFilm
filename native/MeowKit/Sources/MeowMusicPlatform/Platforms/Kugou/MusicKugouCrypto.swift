// SPDX-License-Identifier: GPL-3.0-only
// Port of meowmusic cc22a7e kugou/crypto.go. Public protocol constants, not secrets.
import Foundation
import CryptoKit
import CommonCrypto
import Security
import MeowMusicFeature

enum MusicKugouCrypto {
    static let androidSalt = "OIlwieks28dk2k092lksi2UIkp"
    static let publicSPKI = "MIGfMA0GCSqGSIb3DQEBAQUAA4GNADCBiQKBgQDIAG7QOELSYoIJvTFJhMpe1s/gbjDJX51HBNnEl5HXqTW6lQ7LC8jr9fWZTwusknp+sVGzwd40MwP6U5yDE27M/X1+UR4tvOGOqp94TJtQ1EPnWGWXngpeIW5GxoQGao1rmYWAu6oi1z9XkChrsUdC6DJE5E221wf/4WLFxwAtRQIDAQAB"
    static func md5(_ text: String) -> String {
        Insecure.MD5.hash(data: Data(text.utf8)).map { String(format: "%02x", $0) }.joined()
    }
    static func signature(_ query: [String: String], body: Data? = nil, web: Bool = false) -> String {
        let salt = web ? "NVPh5oo715z5DIWAeQlhMDsWXXQV4hwt" : androidSalt
        let parts = query.filter { $0.key != "signature" }.map { $0.key + "=" + $0.value }.sorted().joined()
        let bytes = Data((salt + parts).utf8) + (body ?? Data()) + Data(salt.utf8)
        return Insecure.MD5.hash(data: bytes).map { String(format: "%02x", $0) }.joined()
    }
    static func paramsKey(_ time: Int) -> String { md5("1005" + androidSalt + "20489" + String(time)) }
    static func trackerKey(hash: String, mid: String, uid: String) -> String {
        md5(hash + "57ae12eb6890223e355ccfcb74edf70d1005" + mid + (uid.isEmpty ? "0" : uid))
    }
    static func randomHex(_ count: Int) throws -> String {
        guard (1...128).contains(count) else { throw MusicServiceError.invalidRequest }
        var bytes = [UInt8](repeating: 0, count: count)
        guard SecRandomCopyBytes(kSecRandomDefault, count, &bytes) == errSecSuccess else { throw MusicServiceError.unavailable }
        return bytes.map { String(format: "%02x", $0) }.joined()
    }
    static func publicKey() throws -> SecKey {
        guard let spki = Data(base64Encoded: publicSPKI), spki.count == 162 else { throw MusicServiceError.unavailable }
        let attributes: [CFString: Any] = [kSecAttrKeyType: kSecAttrKeyTypeRSA, kSecAttrKeyClass: kSecAttrKeyClassPublic,
                                         kSecAttrKeySizeInBits: 1024]
        var error: Unmanaged<CFError>?
        // Fixed SPKI contains a 22-byte wrapper; Security expects PKCS#1.
        guard let key = SecKeyCreateWithData(Data(spki.dropFirst(22)) as CFData, attributes as CFDictionary, &error) else {
            throw MusicServiceError.unavailable
        }
        return key
    }
    static func rawBlock(_ data: Data) throws -> Data {
        guard data.count <= 128 else { throw MusicServiceError.upstreamInvalid }
        let block = data + Data(repeating: 0, count: 128 - data.count)
        guard let spki = Data(base64Encoded: publicSPKI) else { throw MusicServiceError.unavailable }
        // DER modulus starts after the SPKI + sequence + INTEGER header + sign byte.
        let modulus = Data(spki[29..<157])
        guard block.lexicographicallyPrecedes(modulus) else { throw MusicServiceError.upstreamInvalid }
        return block
    }
    static func rsaRaw(_ data: Data) throws -> String {
        let block = try rawBlock(data), key = try publicKey()
        var error: Unmanaged<CFError>?
        guard let encrypted = SecKeyCreateEncryptedData(key, .rsaEncryptionRaw, block as CFData, &error),
              (encrypted as Data).count == 128 else { throw MusicServiceError.unavailable }
        return (encrypted as Data).map { String(format: "%02x", $0) }.joined()
    }
    static func rsaPKCS1(_ data: Data) throws -> String {
        guard data.count <= 117 else { throw MusicServiceError.upstreamInvalid }
        let key = try publicKey()
        var error: Unmanaged<CFError>?
        guard let encrypted = SecKeyCreateEncryptedData(key, .rsaEncryptionPKCS1, data as CFData, &error),
              (encrypted as Data).count == 128 else { throw MusicServiceError.unavailable }
        return (encrypted as Data).map { String(format: "%02X", $0) }.joined()
    }
    static func aes(_ data: Data, key: String, iv: String, decrypt: Bool = false) throws -> Data {
        guard [16, 24, 32].contains(key.utf8.count), iv.utf8.count == 16, data.count <= 2 * 1024 * 1024,
              !decrypt || !data.isEmpty && data.count % 16 == 0 else { throw MusicServiceError.upstreamInvalid }
        let input = Array(data), key = Array(key.utf8), iv = Array(iv.utf8)
        var output = [UInt8](repeating: 0, count: data.count + 16), written = 0
        let capacity = output.count
        let status = CCCrypt(CCOperation(decrypt ? kCCDecrypt : kCCEncrypt), CCAlgorithm(kCCAlgorithmAES),
                             CCOptions(decrypt ? 0 : kCCOptionPKCS7Padding), key, key.count, iv,
                             input, input.count, &output, capacity, &written)
        guard status == CCCryptorStatus(kCCSuccess) else { throw MusicServiceError.upstreamInvalid }
        if decrypt {
            guard written == input.count else { throw MusicServiceError.upstreamInvalid }
            let padding = Int(output[written - 1])
            guard (1...16).contains(padding),
                  output[(written - padding)..<written].reduce(UInt8(0), { $0 | ($1 ^ UInt8(padding)) }) == 0 else {
                throw MusicServiceError.upstreamInvalid
            }
            written -= padding
        }
        return Data(output.prefix(written))
    }
    static func hex(_ data: Data) -> String { data.map { String(format: "%02x", $0) }.joined() }
    static func unhex(_ text: String) throws -> Data {
        let bytes = Array(text.utf8)
        guard !bytes.isEmpty, bytes.count <= 32_768, bytes.count % 2 == 0 else { throw MusicServiceError.upstreamInvalid }
        var result = Data()
        for offset in stride(from: 0, to: bytes.count, by: 2) {
            guard let byte = UInt8(String(decoding: bytes[offset..<(offset + 2)], as: UTF8.self), radix: 16) else {
                throw MusicServiceError.upstreamInvalid
            }
            result.append(byte)
        }
        return result
    }
    static func loginEnvelope(milliseconds: Int, seed: String) throws -> (params: String, pk: String) {
        guard seed.range(of: "^[0-9a-f]{16}$", options: .regularExpression) != nil else { throw MusicServiceError.invalidRequest }
        let key = md5(seed)
        let cipher = try aes(MusicKugouWire.json([:]), key: key, iv: String(key.suffix(16)))
        return (hex(cipher), try rsaRaw(MusicKugouWire.json(["clienttime_ms": milliseconds, "key": seed])))
    }
    static func loginData(_ input: [String: Any], seed: String) throws -> [String: Any] {
        var result = input
        if let raw = input["secu_params"] {
            let encoded = try MusicKugouWire.text(raw, maximum: 32_768, empty: true)
            if !encoded.isEmpty {
                let key = md5(seed)
                let plain = try aes(unhex(encoded), key: key, iv: String(key.suffix(16)), decrypt: true)
                if let fields = try? MusicQQWire.decode(plain) { result.merge(fields) { _, value in value } }
                else {
                    guard let token = String(data: plain, encoding: .utf8) else { throw MusicServiceError.upstreamInvalid }
                    result["token"] = token
                }
            }
        }
        return result
    }
    static func registration(_ payload: [String: Any], seed: String) throws -> (body: Data, p: String) {
        guard seed.range(of: "^[0-9a-f]{6}$", options: .regularExpression) != nil else { throw MusicServiceError.invalidRequest }
        let key = md5(seed)
        let bytes = try aes(MusicKugouWire.json(payload), key: String(key.prefix(16)), iv: String(key.suffix(16)))
        let p = try rsaPKCS1(MusicKugouWire.json(["aes": seed, "uid": 0, "token": ""]))
        return (Data(bytes.base64EncodedString().utf8), p)
    }
    /// Decimal MD5 is 128-bit, NOT a Double/Int and never a lossy JSON number.
    static func decimalMD5(_ seed: String) -> String {
        var digits = [0]
        for byte in Insecure.MD5.hash(data: Data(seed.utf8)) {
            var carry = Int(byte)
            for i in digits.indices {
                let value = digits[i] * 256 + carry
                digits[i] = value % 10; carry = value / 10
            }
            while carry > 0 { digits.append(carry % 10); carry /= 10 }
        }
        return digits.reversed().map(String.init).joined()
    }
}
