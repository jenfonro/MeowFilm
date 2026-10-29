// SPDX-License-Identifier: GPL-3.0-only
// Fixed meowmusic cc22a7e netease/crypto.go. Public protocol constants only.
import Foundation
import CryptoKit
import CommonCrypto
import Security
import zlib
import MeowMusicFeature

struct MusicNeteaseKeyState: Sendable {
    let publicKey: Data
    let sk: String
    let version: String
    var sessionID = ""
    var sessionKey: Data?
}
enum MusicNeteaseCrypto {
    static let eapiKey = Data("e82ckenh8dichen8".utf8)
    static let staticKey = Data([0xab,0x1d,0x5a,0x43,0x0f,0x6b,0xb0,0x4a,0x3f,0x01,0xe8,0x1d,0xdd,0x72,0xbd,0x91,
                                0x6d,0x5c,0xe5,0x91,0x24,0x8a,0xc1,0x28,0x71,0x48,0x06,0xd7,0xf8,0xfb,0x1b,0x84])
    static func random(_ count: Int) throws -> Data {
        var bytes = [UInt8](repeating: 0, count: count)
        guard count > 0, SecRandomCopyBytes(kSecRandomDefault, count, &bytes) == errSecSuccess else { throw MusicServiceError.unavailable }
        return Data(bytes)
    }
    static func text(_ count: Int, alphabet: String = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789") throws -> String {
        guard (1...4096).contains(count), !alphabet.isEmpty, alphabet.utf8.count <= 256 else { throw MusicServiceError.invalidRequest }
        let chars = Array(alphabet.utf8), ceiling = 256 - 256 % alphabet.utf8.count
        var bytes: [UInt8] = []
        while bytes.count < count {
            for value in try random(count) where Int(value) < ceiling {
                bytes.append(chars[Int(value) % chars.count])
                if bytes.count == count { break }
            }
        }
        return String(decoding: bytes, as: UTF8.self)
    }
    static func aes(_ data: Data, key: Data, iv: Data? = nil, decrypt: Bool = false) throws -> Data {
        guard [16, 24, 32].contains(key.count), iv == nil || iv?.count == 16 else { throw MusicServiceError.invalidRequest }
        let blockSize = 16
        if decrypt {
            guard !data.isEmpty, data.count % blockSize == 0 else { throw MusicServiceError.upstreamInvalid }
        }
        let input = Array(data), secret = Array(key), vector = iv.map(Array.init)
        var output = [UInt8](repeating: 0, count: data.count + blockSize), written = 0
        let capacity = output.count
        // Match the reference's strict unpadding instead of assuming that
        // kCCSuccess proves every PKCS#7 byte is valid. Encryption is unchanged.
        let options = CCOptions((decrypt ? 0 : kCCOptionPKCS7Padding) | (iv == nil ? kCCOptionECBMode : 0))
        let result: CCCryptorStatus
        if let vector {
            result = CCCrypt(CCOperation(decrypt ? kCCDecrypt : kCCEncrypt), CCAlgorithm(kCCAlgorithmAES), options,
                             secret, secret.count, vector, input, input.count, &output, capacity, &written)
        } else {
            result = CCCrypt(CCOperation(decrypt ? kCCDecrypt : kCCEncrypt), CCAlgorithm(kCCAlgorithmAES), options,
                             secret, secret.count, nil, input, input.count, &output, capacity, &written)
        }
        guard result == CCCryptorStatus(kCCSuccess) else { throw MusicServiceError.upstreamInvalid }
        if decrypt {
            guard written == input.count else { throw MusicServiceError.upstreamInvalid }
            let padding = Int(output[written - 1])
            guard (1...blockSize).contains(padding) else { throw MusicServiceError.upstreamInvalid }
            let mismatch = output[(written - padding)..<written].reduce(UInt8(0)) { $0 | ($1 ^ UInt8(padding)) }
            guard mismatch == 0 else { throw MusicServiceError.upstreamInvalid }
            written -= padding
        }
        return Data(output.prefix(written))
    }
    static func weapi(_ plain: Data, secret: String) throws -> [String: String] {
        guard secret.utf8.count == 16 else { throw MusicServiceError.invalidRequest }
        let iv = Data("0102030405060708".utf8)
        let first = try aes(plain, key: Data("0CoJUm6Qyw8W8jud".utf8), iv: iv).base64EncodedString()
        let second = try aes(Data(first.utf8), key: Data(secret.utf8), iv: iv)
        let spki = Data(base64Encoded: "MIGfMA0GCSqGSIb3DQEBAQUAA4GNADCBiQKBgQDgtQn2JZ34ZC28NWYpAUd98iZ37BUrX/aKzmFbt7clFSs6sXqHauqKWqdtLkF2KexO40H1YTX8z2lSgBBOAxLsvaklV8k4cBFK9snQXE9/DDaFt6Rr7iVZMldczhC0JNgTz+SHXT6CBHuX3e9SdB1Ua44oncaTWz7OBGLbCiK45wIDAQAB")!
        let attributes: [CFString: Any] = [kSecAttrKeyType: kSecAttrKeyTypeRSA, kSecAttrKeyClass: kSecAttrKeyClassPublic, kSecAttrKeySizeInBits: 1024]
        var error: Unmanaged<CFError>?
        let padded = Data(repeating: 0, count: 112) + Data(secret.utf8.reversed())
        // Raw RSA is required by this legacy public protocol, not credential encryption.
        guard let key = SecKeyCreateWithData(Data(spki.dropFirst(22)) as CFData, attributes as CFDictionary, &error),
              let result = SecKeyCreateEncryptedData(key, .rsaEncryptionRaw, padded as CFData, &error) else { throw MusicServiceError.unavailable }
        return ["params": second.base64EncodedString(), "encSecKey": (result as Data).map { String(format: "%02x", $0) }.joined()]
    }
    static func eapi(_ uri: String, plain: Data) throws -> [String: String] {
        guard let json = String(data: plain, encoding: .utf8) else { throw MusicServiceError.invalidRequest }
        let hash = MusicQQWire.md5("nobody" + uri + "use" + json + "md5forencrypt")
        let encrypted = try aes(Data((uri + "-36cd479b6b5-" + json + "-36cd479b6b5-" + hash).utf8), key: eapiKey)
        return ["params": encrypted.map { String(format: "%02X", $0) }.joined()]
    }
    static func sign(timestamp: String, nonce: String) -> String {
        let publicSalt = "mUHCwVNWJbunMqAHf5MImuirT6plvs6VSFW62MGHstFQxhBGdEoIhLItH3djc4+FB/OKty3+lL2rGeoFBpVe5g=="
        return Data(HMAC<SHA256>.authenticationCode(for: Data((timestamp + nonce).utf8), using: SymmetricKey(data: Data(publicSalt.utf8)))).base64EncodedString()
    }
    static func xeapi(_ uri: String, data: [String: Any], state: MusicNeteaseKeyState) throws -> [String: String] {
        let peer = try Curve25519.KeyAgreement.PublicKey(rawRepresentation: state.publicKey)
        let secret = Curve25519.KeyAgreement.PrivateKey()
        let shared = try secret.sharedSecretFromKeyAgreement(with: peer)
        let ephemeral = secret.publicKey.rawRepresentation
        let key = shared.hkdfDerivedSymmetricKey(using: SHA256.self, salt: Data(repeating: 0, count: 32), sharedInfo: ephemeral, outputByteCount: 16)
        let reusable = state.sessionKey.map { [16, 24, 32].contains($0.count) } == true && !state.sessionID.isEmpty
        let dynamic: Data
        if reusable { dynamic = state.sessionKey! } else { dynamic = try random(16) }
        let sessionID = reusable ? state.sessionID : ""
        let keyPlain = Data((dynamic.base64EncodedString() + "|android|" + state.sk).utf8)
        let box = try AES.GCM.seal(keyPlain, using: key)
        let nonce = box.nonce.withUnsafeBytes { Data($0) }
        let s = ephemeral + nonce + box.ciphertext + box.tag
        var fields: [String: String] = [:]
        for (name, value) in data where name != "e_r" && !(value is NSNull) {
            if let value = value as? String { fields[name] = value }
            else if let number = value as? NSNumber {
                fields[name] = CFGetTypeID(number) == CFBooleanGetTypeID() ? (number.boolValue ? "true" : "false") : number.stringValue
            } else { throw MusicServiceError.invalidRequest }
        }
        var inner: [String: Any] = ["queryString": "e_r=true"]
        if !fields.isEmpty { inner["content"] = String(decoding: MusicQQWire.form(fields), as: UTF8.self) }
        let encrypted = try aes(MusicQQWire.json(inner), key: staticKey), mask = try random(16)
        let mixed = encrypted.enumerated().map { $0.element ^ mask[$0.offset & 15] }
        let rotation = Int(mask[0] & 15) % mixed.count
        let middle = mask + Data(mixed.dropFirst(rotation)) + Data(mixed.prefix(rotation))
        let c = try aes(middle, key: dynamic)
        let r = try aes(Data((state.version + "|" + sessionID).utf8), key: staticKey)
        return ["C": c.base64EncodedString().replacingOccurrences(of: "+", with: "-").replacingOccurrences(of: "/", with: "_").replacingOccurrences(of: "=", with: ""),
                "S": s.base64EncodedString(), "R": r.base64EncodedString()]
    }
    static func gunzip(_ data: Data) throws -> Data {
        guard data.starts(with: [0x1f, 0x8b]) else { return data }
        var stream = z_stream()
        guard inflateInit2_(&stream, 31, ZLIB_VERSION, Int32(MemoryLayout<z_stream>.size)) == Z_OK else { throw MusicServiceError.upstreamInvalid }
        defer { inflateEnd(&stream) }
        return try data.withUnsafeBytes { input in
            stream.next_in = UnsafeMutablePointer<Bytef>(mutating: input.bindMemory(to: Bytef.self).baseAddress)
            stream.avail_in = uInt(data.count)
            var out = Data(), chunk = [UInt8](repeating: 0, count: 16_384)
            while true {
                let status = chunk.withUnsafeMutableBytes { buffer -> Int32 in
                    stream.next_out = buffer.bindMemory(to: Bytef.self).baseAddress
                    stream.avail_out = uInt(buffer.count)
                    return inflate(&stream, Z_NO_FLUSH)
                }
                let count = chunk.count - Int(stream.avail_out)
                guard out.count + count <= 2 * 1024 * 1024 else { throw MusicServiceError.limit }
                out.append(contentsOf: chunk.prefix(count))
                if status == Z_STREAM_END {
                    guard stream.avail_in == 0 else { throw MusicServiceError.upstreamInvalid }; return out
                }
                guard status == Z_OK, count > 0 || stream.avail_in > 0 else { throw MusicServiceError.upstreamInvalid }
            }
        }
    }
}
