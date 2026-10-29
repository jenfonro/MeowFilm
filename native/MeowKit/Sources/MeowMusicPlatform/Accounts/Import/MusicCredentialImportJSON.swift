import Foundation
import MeowMusicFeature

/// Reject ambiguity before Foundation constructs a dictionary. No arbitrary
/// imported key/value or Foundation error is ever included in a diagnostic.
enum MusicCredentialImportJSON {
    static func object(_ bytes: Data) throws -> [String: Any] {
        guard bytes.count <= MusicCredentialImportInput.maximumBytes,
              String(data: bytes, encoding: .utf8) != nil else { throw MusicCredentialImportError.invalidFormat }
        do {
            var scanner = Scanner(bytes: Array(bytes))
            try scanner.value(depth: 0)
            scanner.whitespace()
            guard scanner.offset == scanner.bytes.count,
                  let object = try JSONSerialization.jsonObject(with: bytes) as? [String: Any] else {
                throw MusicCredentialImportError.invalidFormat
            }
            return object
        } catch let error as MusicCredentialImportError { throw error }
        catch { throw MusicCredentialImportError.invalidFormat }
    }
    private struct Scanner {
        let bytes: [UInt8]
        var offset = 0
        var nodes = 0
        mutating func whitespace() {
            while offset < bytes.count, [9, 10, 13, 32].contains(bytes[offset]) { offset += 1 }
        }
        mutating func take(_ byte: UInt8) throws {
            whitespace()
            guard offset < bytes.count, bytes[offset] == byte else { throw MusicCredentialImportError.invalidFormat }
            offset += 1
        }
        mutating func string() throws -> String {
            whitespace()
            let start = offset
            try take(34)
            while offset < bytes.count {
                let byte = bytes[offset]; offset += 1
                if byte == 92 {
                    guard offset < bytes.count else { throw MusicCredentialImportError.invalidFormat }
                    let escape = bytes[offset]; offset += 1
                    guard [34, 47, 92, 98, 102, 110, 114, 116, 117].contains(escape) else {
                        throw MusicCredentialImportError.invalidFormat
                    }
                    if escape == 117 {
                        let first = try hex4()
                        if (0xD800...0xDBFF).contains(first) {
                            guard offset + 2 <= bytes.count, bytes[offset] == 92, bytes[offset + 1] == 117 else {
                                throw MusicCredentialImportError.invalidFormat
                            }
                            offset += 2
                            guard (0xDC00...0xDFFF).contains(try hex4()) else { throw MusicCredentialImportError.invalidFormat }
                        } else if (0xDC00...0xDFFF).contains(first) { throw MusicCredentialImportError.invalidFormat }
                    }
                    continue
                }
                if byte == 34 {
                    // Foundation validates escapes/surrogates; fragment parsing
                    // allows comparison of decoded keys such as uid and \\u0075id.
                    guard let text = try JSONSerialization.jsonObject(
                        with: Data(bytes[start..<offset]), options: .fragmentsAllowed) as? String else {
                        throw MusicCredentialImportError.invalidFormat
                    }
                    return text
                }
                guard byte >= 32 else { throw MusicCredentialImportError.invalidFormat }
            }
            throw MusicCredentialImportError.invalidFormat
        }
        mutating func hex4() throws -> Int {
            guard offset + 4 <= bytes.count else { throw MusicCredentialImportError.invalidFormat }
            var number = 0
            for _ in 0..<4 {
                let byte = bytes[offset]; offset += 1
                let digit: Int
                switch byte {
                case 48...57: digit = Int(byte - 48)
                case 65...70: digit = Int(byte - 65) + 10
                case 97...102: digit = Int(byte - 97) + 10
                default: throw MusicCredentialImportError.invalidFormat
                }
                number = number * 16 + digit
            }
            return number
        }
        mutating func value(depth: Int) throws {
            nodes += 1
            guard depth <= 16, nodes <= 4096 else { throw MusicCredentialImportError.sizeLimit }
            whitespace()
            guard offset < bytes.count else { throw MusicCredentialImportError.invalidFormat }
            switch bytes[offset] {
            case 123:
                offset += 1; whitespace()
                if offset < bytes.count, bytes[offset] == 125 { offset += 1; return }
                var keys = Set<String>()
                while true {
                    let key = try string()
                    guard key.utf8.count <= 256 else { throw MusicCredentialImportError.sizeLimit }
                    guard keys.insert(key).inserted else { throw MusicCredentialImportError.ambiguous }
                    try take(58); try value(depth: depth + 1); whitespace()
                    guard offset < bytes.count else { throw MusicCredentialImportError.invalidFormat }
                    if bytes[offset] == 125 { offset += 1; break }
                    try take(44)
                }
            case 91:
                offset += 1; whitespace()
                if offset < bytes.count, bytes[offset] == 93 { offset += 1; return }
                while true {
                    try value(depth: depth + 1); whitespace()
                    guard offset < bytes.count else { throw MusicCredentialImportError.invalidFormat }
                    if bytes[offset] == 93 { offset += 1; break }
                    try take(44)
                }
            case 34: _ = try string()
            default:
                let start = offset
                while offset < bytes.count, ![9, 10, 13, 32, 44, 93, 125].contains(bytes[offset]) { offset += 1 }
                guard offset > start else { throw MusicCredentialImportError.invalidFormat }
                let token = String(decoding: bytes[start..<offset], as: UTF8.self)
                if !["true", "false", "null"].contains(token) {
                    // Platform IDs must not become a different integer through
                    // Foundation's floating-point/exponent normalization.
                    guard token.range(of: "^(0|-?[1-9][0-9]{0,15})$", options: .regularExpression) != nil,
                          let number = Int64(token), number >= -9_007_199_254_740_991,
                          number <= 9_007_199_254_740_991 else { throw MusicCredentialImportError.invalidFormat }
                }
            }
        }
    }
}
