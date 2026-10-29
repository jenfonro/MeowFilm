import Foundation
import CryptoKit
import MeowMusicFeature

/// Ordered JSON keeps legacy locator hashes stable. Foundation Dictionary and
/// sortedKeys must not reorder an existing platform locator object.
indirect enum MJ: Sendable, Equatable {
    case object([(String, MJ)]), array([MJ]), string(String), number(Double), bool(Bool), null
    /// Upstream-only numeric lexeme, never a rounded Double or a document number.
    /// Unconsumed metadata can remain opaque; ID consumers use exact decimal text.
    case upstreamNumber(String)
    enum ParseMode { case document, upstream }

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
    /// without floating point, bounded by the existing 64-digit ID contract.
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
            guard digits.count <= 64, shift <= 64 - digits.count else { return nil }
            return String(digits) + String(repeating: "0", count: shift)
        }
        guard shift > -digits.count, digits.suffix(-shift).allSatisfy({ $0 == "0" }) else { return nil }
        let integer = digits.dropLast(-shift)
        return integer.count <= 64 ? String(integer) : nil
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
    func text(_ maximum: Int = 2048, empty: Bool = false) throws -> String {
        guard isString, string.utf8.count <= maximum, empty || !string.isEmpty,
              !string.unicodeScalars.contains(where: { $0.value < 32 || $0.value == 127 }) else { throw MusicServiceError.invalidResponse }
        return string
    }
    func integer(_ maximum: Int = 9_007_199_254_740_991) throws -> Int {
        guard isInteger, number <= Double(maximum) else { throw MusicServiceError.invalidResponse }; return int
    }
    func requireFields(_ allowed: [String], required: [String] = []) throws {
        guard isObject, fields.allSatisfy({ allowed.contains($0.0) }), required.allSatisfy(contains) else {
            throw MusicServiceError.corruptDocument
        }
    }
    var stable: MJ {
        switch self {
        case .object(let v): return .object(v.sorted { $0.0.utf16.lexicographicallyPrecedes($1.0.utf16) }.map { ($0.0, $0.1.stable) })
        case .array(let v): return .array(v.map(\.stable))
        default: return self
        }
    }
    var bytes: Data { Data(json.utf8) }
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
    /// must retain this rule (e.g. legacy numeric song/binding map IDs).
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
            if abs(v) >= 0.000001 && abs(v) < 1, s.contains("e"), let d = Decimal(string: s, locale: Locale(identifier: "en_US_POSIX")) {
                s = NSDecimalNumber(decimal: d).stringValue
            }
            // Swift emits 1.0e-7 while ECMAScript emits 1e-7.
            s = s.replacingOccurrences(of: ".0e", with: "e")
            return s
        case .array(let v): return "[" + v.map(\.json).joined(separator: ",") + "]"
        case .object(let v): return "{" + Self.enumeration(v).map { Self.quote($0.0) + ":" + $0.1.json }.joined(separator: ",") + "}"
        }
    }
    /// Documents, imports and normalized locators retain the strict default.
    /// Only a transport boundary may explicitly opt into upstream numbers.
    static func parse(_ data: Data, maximum: Int = 8 * 1024 * 1024, mode: ParseMode = .document,
                      ignoringUpstreamKeys: Set<String> = []) throws -> MJ {
        guard mode == .upstream || ignoringUpstreamKeys.isEmpty else { throw MusicServiceError.invalidRequest }
        guard data.count <= maximum, String(data: data, encoding: .utf8) != nil else { throw MusicServiceError.limit }
        var parser = Parser(bytes: Array(data), mode: mode, ignoredKeys: ignoringUpstreamKeys)
        let value = try parser.value(0)
        parser.space(); guard parser.i == parser.bytes.count else { throw MusicServiceError.corruptDocument }
        return value
    }
    private struct Parser {
        let bytes: [UInt8]
        let mode: ParseMode
        let ignoredKeys: Set<String>
        var i = 0, nodes = 0
        mutating func space() { while i < bytes.count && [9, 10, 13, 32].contains(bytes[i]) { i += 1 } }
        mutating func take(_ c: UInt8) -> Bool { space(); if i < bytes.count && bytes[i] == c { i += 1; return true }; return false }
        mutating func string() throws -> String {
            space(); let start = i
            guard take(34) else { throw MusicServiceError.corruptDocument }
            while i < bytes.count {
                let c = bytes[i]; i += 1
                if c == 34 {
                    do { return try JSONDecoder().decode(String.self, from: Data(bytes[start..<i])) }
                    catch { throw MusicServiceError.corruptDocument }
                }
                if c == 92 { i += 1 }
                if c < 32 { throw MusicServiceError.corruptDocument }
            }
            throw MusicServiceError.corruptDocument
        }
        mutating func value(_ depth: Int) throws -> MJ {
            space(); nodes += 1
            guard depth <= 40, nodes <= 600_000, i < bytes.count else { throw MusicServiceError.limit }
            switch bytes[i] {
            case 34: return .string(try string())
            case 123:
                i += 1; var rows: [(String, MJ)] = [], seen = Set<Data>()
                if take(125) { return .obj() }
                repeat {
                    let key = try string()
                    // JSON keys are byte identities, not Swift's canonical
                    // Unicode equivalence. Ordered storage preserves both.
                    // Only a protocol boundary can discard known unused
                    // upstream metadata. Identity-bearing and document keys
                    // remain strict; their duplicates are never normalized.
                    let ignored = ignoredKeys.contains(key)
                    guard ignored || seen.insert(Data(key.utf8)).inserted else { throw MusicServiceError.corruptDocument }
                    guard take(58) else { throw MusicServiceError.corruptDocument }
                    let child = try value(depth + 1)
                    if !ignored { rows.append((key, child)) }
                } while take(44)
                guard take(125) else { throw MusicServiceError.corruptDocument }; return .object(rows)
            case 91:
                i += 1; var rows: [MJ] = []
                if take(93) { return .array([]) }
                repeat { rows.append(try value(depth + 1)) } while take(44)
                guard take(93) else { throw MusicServiceError.corruptDocument }; return .array(rows)
            default:
                let start = i
                while i < bytes.count && ![9, 10, 13, 32, 44, 93, 125].contains(bytes[i]) { i += 1 }
                let token = String(decoding: bytes[start..<i], as: UTF8.self)
                if token == "null" { return .null }; if token == "true" { return .bool(true) }; if token == "false" { return .bool(false) }
                guard token.range(of: #"^-?(?:0|[1-9][0-9]*)(?:\.[0-9]+)?(?:[eE][+-]?[0-9]+)?$"#, options: .regularExpression) != nil else {
                    throw MusicServiceError.corruptDocument
                }
                if mode == .upstream {
                    // Preserve out-of-range/overflowing metadata at any path.
                    // Also keep tokens that would round a fractional value into
                    // an integer ID (or underflow into zero) opaque.
                    guard let n = Double(token), n.isFinite, abs(n) <= 9_007_199_254_740_991,
                          n < 0 || n.rounded() != n || MJ.exactIntegerID(token) == String(Int64(n)) else {
                        return .upstreamNumber(token)
                    }
                    return .number(n)
                }
                guard let n = Double(token), n.isFinite, abs(n) <= 9_007_199_254_740_991 else { throw MusicServiceError.corruptDocument }
                // Integer tokens may not round into a neighbouring native ID.
                if !token.contains(".") && !token.lowercased().contains("e") {
                    guard let exact = Int64(token), Double(exact) == n else { throw MusicServiceError.corruptDocument }
                }
                return .number(n)
            }
        }
    }
}

func musicRequire(_ condition: @autoclosure () throws -> Bool, _ error: MusicServiceError = .invalidRequest) throws {
    guard try condition() else { throw error }
}
func musicText(_ value: String, maximum: Int = 2048) throws -> String { try MJ.string(value).text(maximum) }
func musicHash(_ data: Data) -> String { SHA256.hash(data: data).map { String(format: "%02x", $0) }.joined() }
func musicHex(_ value: String) -> Bool { value.range(of: "^[a-f0-9]{64}$", options: .regularExpression) != nil }
func musicSame(_ a: String, _ b: String) -> Bool { a.utf8.elementsEqual(b.utf8) }
func musicIDs(_ values: [String]) -> Set<Data> { Set(values.map { Data($0.utf8) }) }
