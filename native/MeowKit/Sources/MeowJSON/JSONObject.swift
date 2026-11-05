import Foundation
import CoreFoundation

/// A syntax-checked number that Foundation cannot represent without losing
/// an integer identity. It remains numeric when forwarded, never a string.
public struct JSONNumber: Sendable, Equatable {
    public let literal: String
    fileprivate init(_ literal: String) { self.literal = literal }
}

/// Dictionary JSON for external documents. Repeated members always use the
/// last value; callers own byte budgets and validate only the fields they use.
public enum JSONObject {
    public enum Error: Swift.Error { case invalidJSON }

    public static func decode(_ data: Data) throws -> [String: Any] {
        guard String(data: data, encoding: .utf8) != nil else { throw Error.invalidJSON }
        var parser = Parser(bytes: Array(data))
        if parser.bytes.starts(with: [0xEF, 0xBB, 0xBF]) { parser.offset = 3 }
        let value = try parser.value(depth: 0)
        parser.space()
        guard parser.offset == parser.bytes.count, let object = value as? [String: Any] else {
            throw Error.invalidJSON
        }
        return object
    }

    /// Used when forwarding parsed metadata, including opaque number literals.
    /// Own-format dictionaries can continue to use Foundation's encoder.
    public static func encode(_ value: Any) throws -> Data {
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.withoutEscapingSlashes]
        var bytes = Data()
        func write(_ value: Any, depth: Int) throws {
            // Preserve Foundation's nesting resource boundary for recursive I/O.
            guard depth < 512 else { throw Error.invalidJSON }
            if let object = value as? [String: Any] {
                bytes.append(123)
                for (index, key) in object.keys.sorted().enumerated() {
                    if index > 0 { bytes.append(44) }
                    bytes.append(try encoder.encode(key)); bytes.append(58)
                    try write(object[key]!, depth: depth + 1)
                }
                bytes.append(125)
            } else if let array = value as? [Any] {
                bytes.append(91)
                for (index, child) in array.enumerated() {
                    if index > 0 { bytes.append(44) }
                    try write(child, depth: depth + 1)
                }
                bytes.append(93)
            } else if let text = value as? String {
                bytes.append(try encoder.encode(text))
            } else if let number = value as? JSONNumber {
                bytes.append(contentsOf: number.literal.utf8)
            } else if let number = value as? NSNumber {
                guard number.doubleValue.isFinite else { throw Error.invalidJSON }
                let text = CFGetTypeID(number) == CFBooleanGetTypeID()
                    ? (number.boolValue ? "true" : "false") : number.stringValue
                bytes.append(contentsOf: text.utf8)
            } else if value is NSNull {
                bytes.append(contentsOf: "null".utf8)
            } else { throw Error.invalidJSON }
        }
        try write(value, depth: 0)
        return bytes
    }

    private struct Parser {
        let bytes: [UInt8]
        var offset = 0
        let decoder = JSONDecoder()

        mutating func space() {
            while offset < bytes.count, [9, 10, 13, 32].contains(bytes[offset]) { offset += 1 }
        }
        mutating func take(_ byte: UInt8) -> Bool {
            space()
            guard offset < bytes.count, bytes[offset] == byte else { return false }
            offset += 1
            return true
        }
        mutating func string() throws -> String {
            guard take(34) else { throw Error.invalidJSON }
            let start = offset - 1
            var escaped = false
            while offset < bytes.count {
                let byte = bytes[offset]; offset += 1
                if byte == 34 {
                    if !escaped { return String(decoding: bytes[(start + 1)..<(offset - 1)], as: UTF8.self) }
                    do { return try decoder.decode(String.self, from: Data(bytes[start..<offset])) }
                    catch { throw Error.invalidJSON }
                }
                guard byte >= 32 else { throw Error.invalidJSON }
                if byte == 92 { escaped = true; offset += 1 }
            }
            throw Error.invalidJSON
        }
        mutating func value(depth: Int) throws -> Any {
            space()
            // As in Foundation, bound recursive stack use, not key names or
            // node counts. The caller already bounds the whole byte buffer.
            guard depth < 512, offset < bytes.count else { throw Error.invalidJSON }
            switch bytes[offset] {
            case 34: return try string()
            case 123:
                offset += 1
                var object: [String: Any] = [:]
                if take(125) { return object }
                repeat {
                    let key = try string()
                    guard take(58) else { throw Error.invalidJSON }
                    object[key] = try value(depth: depth + 1)
                } while take(44)
                guard take(125) else { throw Error.invalidJSON }
                return object
            case 91:
                offset += 1
                var array: [Any] = []
                if take(93) { return array }
                repeat { array.append(try value(depth: depth + 1)) } while take(44)
                guard take(93) else { throw Error.invalidJSON }
                return array
            case 110: try literal("null"); return NSNull()
            case 116: try literal("true"); return NSNumber(value: true)
            case 102: try literal("false"); return NSNumber(value: false)
            default: return try number()
            }
        }
        mutating func literal(_ text: String) throws {
            guard bytes[offset...].starts(with: text.utf8) else { throw Error.invalidJSON }
            offset += text.utf8.count
        }
        mutating func digits() -> Int {
            let start = offset
            while offset < bytes.count, (48...57).contains(bytes[offset]) { offset += 1 }
            return offset - start
        }
        mutating func number() throws -> Any {
            let start = offset
            if bytes[offset] == 45 { offset += 1 }
            guard offset < bytes.count else { throw Error.invalidJSON }
            if bytes[offset] == 48 { offset += 1 }
            else {
                guard (49...57).contains(bytes[offset]), digits() > 0 else { throw Error.invalidJSON }
            }
            var integer = true
            if offset < bytes.count, bytes[offset] == 46 {
                integer = false; offset += 1
                guard digits() > 0 else { throw Error.invalidJSON }
            }
            if offset < bytes.count, bytes[offset] == 101 || bytes[offset] == 69 {
                integer = false; offset += 1
                if offset < bytes.count, bytes[offset] == 43 || bytes[offset] == 45 { offset += 1 }
                guard digits() > 0 else { throw Error.invalidJSON }
            }
            let token = String(decoding: bytes[start..<offset], as: UTF8.self)
            if integer {
                if let value = Int64(token) { return NSNumber(value: value) }
                if let value = UInt64(token) { return NSNumber(value: value) }
                return JSONNumber(token)
            }
            // Scalar conversion preserves Foundation's existing NSNumber types
            // (including decimal/exponent tokens for credential imports). It
            // does not parse objects or decide which duplicate member wins.
            guard let number = try? JSONSerialization.jsonObject(with: Data(token.utf8),
                                                                 options: .fragmentsAllowed) as? NSNumber,
                  number.doubleValue.isFinite, abs(number.doubleValue) <= 9_007_199_254_740_991 else {
                return JSONNumber(token)
            }
            let value = number.doubleValue
            if value.rounded() == value, !Self.represents(token, integer: Int64(value)) {
                return JSONNumber(token)
            }
            return number
        }

        /// Compare an exponent/fraction token with the integer Foundation
        /// produced, without rounding, expanding giant exponents or bounding
        /// unused number literals. Non-integral/underflowing IDs stay opaque.
        static func represents(_ token: String, integer: Int64) -> Bool {
            let negative = token.hasPrefix("-")
            let unsigned = negative ? token.dropFirst() : token[...]
            let parts = unsigned.split(whereSeparator: { $0 == "e" || $0 == "E" })
            let mantissa = parts[0]
            let fraction = mantissa.firstIndex(of: ".").map {
                mantissa.distance(from: mantissa.index(after: $0), to: mantissa.endIndex)
            } ?? 0
            let digits = mantissa.filter { $0 != "." }.drop(while: { $0 == "0" })
            if digits.isEmpty { return integer == 0 }
            guard integer != 0, negative == (integer < 0),
                  let exponent = parts.count == 2 ? Int(parts[1]) : 0 else { return false }
            let (shift, overflow) = exponent.subtractingReportingOverflow(fraction)
            guard !overflow else { return false }
            let expected = String(abs(integer))
            if shift >= 0 {
                guard digits.count <= expected.count, shift == expected.count - digits.count else { return false }
                return expected == String(digits) + String(repeating: "0", count: shift)
            }
            guard shift > -digits.count, digits.suffix(-shift).allSatisfy({ $0 == "0" }) else { return false }
            return digits.dropLast(-shift) == expected
        }
    }
}
