import Foundation
import CryptoKit
import CoreFoundation
import MeowJSON
import MeowMusicFeature
import MeowRuntime

/// Ordered JSON keeps locator hashes stable. Foundation Dictionary and
/// sortedKeys must not reorder an existing platform locator object.
indirect enum MJ: Sendable, Equatable {
    private static let maximumExpandedIntegerBytes = 8 * 1024 * 1024
    case object([(String, MJ)]), array([MJ]), string(String), number(Double), bool(Bool), null
    /// Numeric lexeme that cannot be represented exactly as a Double.
    /// Unconsumed metadata can remain opaque; ID consumers use exact decimal text.
    case upstreamNumber(String)

    static func == (a: MJ, b: MJ) -> Bool {
        switch (a, b) {
        case (.null, .null): return true
        case (.bool(let x), .bool(let y)): return x == y
        case (.number(let x), .number(let y)): return x == y
        case (.upstreamNumber(let x), .upstreamNumber(let y)): return x.utf8.elementsEqual(y.utf8)
        case (.string(let x), .string(let y)): return x.utf8.elementsEqual(y.utf8)
        case (.array(let x), .array(let y)): return x.count == y.count && zip(x, y).allSatisfy { $0.0 == $0.1 }
        case (.object(let x), .object(let y)):
            return x.count == y.count && zip(x, y).allSatisfy { $0.0.0.utf8.elementsEqual($0.1.0.utf8) && $0.0.1 == $0.1.1 }
        default: return false
        }
    }
    static func obj(_ pairs: [(String, MJ)] = []) -> MJ { .object(pairs) }
    static func n(_ value: Int) -> MJ { .number(Double(value)) }
    static func strings(_ values: [String]) -> MJ { .array(values.map(MJ.string)) }
    var fields: [(String, MJ)] { if case .object(let v) = self { return v }; return [] }
    var array: [MJ] { if case .array(let v) = self { return v }; return [] }
    var string: String { if case .string(let v) = self { return v }; return "" }
    var number: Double {
        if case .number(let v) = self { return v }
        // Do not turn an opaque upstream number into either a rounded value or
        // a valid-looking zero when a numeric (non-ID) field is consumed.
        if case .upstreamNumber = self { return .nan }
        return 0
    }
    var int: Int { Int(exactly: number.rounded(.towardZero)) ?? 0 }
    var bool: Bool { if case .bool(let v) = self { return v }; return false }
    var isObject: Bool { if case .object = self { return true }; return false }
    var isArray: Bool { if case .array = self { return true }; return false }
    var isString: Bool { if case .string = self { return true }; return false }
    var isBool: Bool { if case .bool = self { return true }; return false }
    var isNumber: Bool { if case .number = self { return true }; return false }
    var isInteger: Bool { if case .number(let v) = self { return v >= 0 && v <= 9_007_199_254_740_991 && v.rounded() == v }; return false }
    var strings: [String] { array.map(\.string) }
    var id: String {
        if case .upstreamNumber(let token) = self { return Self.exactIntegerID(token) ?? "" }
        return isString ? string : isInteger ? String(int) : ""
    }
    /// Called only for syntax-checked JSON numbers. Expands integer exponents
    /// without floating point. Expansion has a separate parser resource budget
    /// so a small exponent token cannot allocate an unbounded string.
    /// Fractional values (including those Double rounds to an integer) fail.
    private static func exactIntegerID(_ token: String) -> String? {
        let negative = token.hasPrefix("-")
        let unsigned = negative ? token.dropFirst() : token[...]
        let parts = unsigned.split(whereSeparator: { $0 == "e" || $0 == "E" })
        let mantissa = parts[0]
        let fraction = mantissa.firstIndex(of: ".").map { mantissa.distance(from: mantissa.index(after: $0), to: mantissa.endIndex) } ?? 0
        let digits = mantissa.filter { $0 != "." }.drop(while: { $0 == "0" })
        if digits.isEmpty { return "0" } // Includes -0 and zero with any exponent.
        guard !negative, let exponent = parts.count == 2 ? Int(parts[1]) : 0 else { return nil }
        let (shift, overflow) = exponent.subtractingReportingOverflow(fraction)
        guard !overflow else { return nil }
        if shift >= 0 {
            guard digits.count <= maximumExpandedIntegerBytes, shift <= maximumExpandedIntegerBytes - digits.count else { return nil }
            return String(digits) + String(repeating: "0", count: shift)
        }
        guard shift > -digits.count, digits.suffix(-shift).allSatisfy({ $0 == "0" }) else { return nil }
        let integer = digits.dropLast(-shift)
        return String(integer)
    }
    subscript(_ key: String) -> MJ {
        get { fields.first { $0.0.utf8.elementsEqual(key.utf8) }?.1 ?? .null }
        set {
            var rows = fields
            if let i = rows.firstIndex(where: { $0.0.utf8.elementsEqual(key.utf8) }) { rows[i].1 = newValue }
            else { rows.append((key, newValue)) }
            self = .object(rows)
        }
    }
    func contains(_ key: String) -> Bool { fields.contains { $0.0.utf8.elementsEqual(key.utf8) } }
    mutating func remove(_ key: String) { self = .object(fields.filter { !$0.0.utf8.elementsEqual(key.utf8) }) }
    func text(_ maximum: Int? = nil, empty: Bool = false) throws -> String {
        guard isString, maximum.map({ string.utf8.count <= $0 }) ?? true, empty || !string.isEmpty,
              !string.unicodeScalars.contains(where: { $0.value < 32 || $0.value == 127 }) else { throw MusicServiceError.invalidResponse }
        return string
    }
    func integer(_ maximum: Int = 9_007_199_254_740_991) throws -> Int {
        guard isInteger, number <= Double(maximum) else { throw MusicServiceError.invalidResponse }; return int
    }
    var stable: MJ {
        switch self {
        case .object(let v): return .object(v.sorted { $0.0.utf16.lexicographicallyPrecedes($1.0.utf16) }.map { ($0.0, $0.1.stable) })
        case .array(let v): return .array(v.map(\.stable))
        default: return self
        }
    }
    var bytes: Data { Data(json.utf8) }
    /// Domain values reach the relational writer without a JSON text round trip.
    var storageValue: RuntimeValue {
        get throws {
            switch self {
            case .null: return .null
            case .bool(let value): return .bool(value)
            case .number(let value):
                guard value.isFinite else { throw MusicServiceError.corruptDocument }
                return .number(value)
            case .string(let value): return .string(value)
            case .array(let values): return .array(try values.map { try $0.storageValue })
            case .object(let fields):
                return .object(try RuntimeObject(uniqueKeysWithValues: fields.map { ($0.0, try $0.1.storageValue) }))
            case .upstreamNumber(let token): return try RuntimeJSON.decode(Data(token.utf8))
            }
        }
    }
    var digest: String { SHA256.hash(data: bytes).map { String(format: "%02x", $0) }.joined() }
    static func quote(_ value: String) -> String {
        var out = "\""
        for c in value.unicodeScalars {
            switch c.value {
            case 34: out += "\\\""
            case 92: out += "\\\\"
            case 8: out += "\\b"
            case 9: out += "\\t"
            case 10: out += "\\n"
            case 12: out += "\\f"
            case 13: out += "\\r"
            case 0..<32: out += String(format: "\\u%04x", c.value)
            default: out.unicodeScalars.append(c)
            }
        }
        return out + "\""
    }
    /// JSON.stringify enumerates array-index keys before other object keys,
    /// even when a "stable" object was built in lexical order. Receipt hashes
    /// must retain this rule (e.g. numeric song/binding map IDs).
    static func indexKey(_ key: String) -> UInt32? {
        guard let n = UInt32(key), n != UInt32.max, String(n).utf8.elementsEqual(key.utf8) else { return nil }
        return n
    }
    static func enumeration(_ fields: [(String, MJ)]) -> [(String, MJ)] {
        let indices = fields.compactMap { row -> (UInt32, String, MJ)? in
            guard let n = indexKey(row.0) else { return nil }; return (n, row.0, row.1)
        }.sorted { $0.0 < $1.0 }.map { ($0.1, $0.2) }
        return indices + fields.filter { indexKey($0.0) == nil }
    }
    var json: String {
        switch self {
        case .null: return "null"
        case .bool(let v): return v ? "true" : "false"
        case .string(let v): return Self.quote(v)
        case .upstreamNumber(let token): return token
        case .number(let v):
            if v == 0 { return "0" }
            if v.rounded() == v && abs(v) <= 9_007_199_254_740_991 { return String(Int64(v)) }
            var s = String(v).replacingOccurrences(of: "e-0", with: "e-").replacingOccurrences(of: "e+0", with: "e+")
            // ECMAScript uses decimal notation down to 1e-6.
            if abs(v) >= 0.000001 && abs(v) < 1, s.contains("e"), let d = Decimal(string: s, locale: Self.posixLocale) {
                s = NSDecimalNumber(decimal: d).stringValue
            }
            // Swift emits 1.0e-7 while ECMAScript emits 1e-7.
            s = s.replacingOccurrences(of: ".0e", with: "e")
            return s
        case .array(let v): return "[" + v.map(\.json).joined(separator: ",") + "]"
        case .object(let v): return "{" + Self.enumeration(v).map { Self.quote($0.0) + ":" + $0.1.json }.joined(separator: ",") + "}"
        }
    }
    static func parse(_ data: Data) throws -> MJ {
        guard String(data: data, encoding: .utf8) != nil else { throw MusicServiceError.corruptDocument }
        func value(_ raw: Any) throws -> MJ {
            if let fields = raw as? JSONObject.Fields {
                return .object(try fields.map { ($0.key, try value($0.value)) })
            }
            if let rows = raw as? [Any] { return .array(try rows.map(value)) }
            if let text = raw as? String { return .string(text) }
            if let number = raw as? JSONNumber {
                return number.exactDouble.map(MJ.number) ?? .upstreamNumber(number.literal)
            }
            if let boolean = raw as? NSNumber, CFGetTypeID(boolean) == CFBooleanGetTypeID() {
                return .bool(boolean.boolValue)
            }
            if raw is NSNull { return .null }
            throw MusicServiceError.corruptDocument
        }
        do { return try value(JSONObject.decodeValue(data)) }
        catch { throw MusicServiceError.corruptDocument }
    }

    private static let posixLocale = Locale(identifier: "en_US_POSIX")
}

func musicRequire(_ condition: @autoclosure () throws -> Bool, _ error: MusicServiceError = .invalidRequest) throws {
    guard try condition() else { throw error }
}
func musicText(_ value: String, maximum: Int? = nil) throws -> String { try MJ.string(value).text(maximum) }
func musicHash(_ data: Data) -> String { SHA256.hash(data: data).map { String(format: "%02x", $0) }.joined() }
func musicHex(_ value: String) -> Bool {
    let bytes = value.utf8
    return bytes.count == 64 && bytes.allSatisfy { (48...57).contains($0) || (97...102).contains($0) }
}
func musicSame(_ a: String, _ b: String) -> Bool { a.utf8.elementsEqual(b.utf8) }
func musicIDs(_ values: [String]) -> Set<Data> { Set(values.map { Data($0.utf8) }) }
