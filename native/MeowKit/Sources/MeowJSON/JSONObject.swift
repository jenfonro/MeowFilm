import Foundation
import CoreFoundation

/// A syntax-checked JSON number. The original token survives read/modify/write.
public struct JSONNumber: Sendable, Equatable {
    public let literal: String
    fileprivate init(_ literal: String) { self.literal = literal }

    /// Ordinary consumers can use a Double only when writing it back preserves
    /// the decimal value and integer identity. Unconsumed numbers stay opaque.
    public var exactDouble: Double? {
        guard let value = Double(literal), value.isFinite,
              value.rounded(.towardZero) != value || abs(value) <= 9_007_199_254_740_991,
              let decimal = Self.decimal(literal), decimal == Self.decimal(String(value)) else { return nil }
        return value
    }

    private static func decimal(_ token: String) -> String? {
        let negative = token.hasPrefix("-")
        let parts = token.drop(while: { $0 == "-" }).split(whereSeparator: { $0 == "e" || $0 == "E" })
        let mantissa = parts[0]
        let fraction = mantissa.firstIndex(of: ".").map {
            mantissa.distance(from: mantissa.index(after: $0), to: mantissa.endIndex)
        } ?? 0
        var digits = mantissa.filter { $0 != "." }.drop(while: { $0 == "0" })
        if digits.isEmpty { return negative ? "-0" : "0" }
        guard let exponent = parts.count == 2 ? Int(parts[1]) : 0 else { return nil }
        var (shift, overflow) = exponent.subtractingReportingOverflow(fraction)
        guard !overflow else { return nil }
        while digits.last == "0" {
            digits = digits.dropLast()
            (shift, overflow) = shift.addingReportingOverflow(1)
            guard !overflow else { return nil }
        }
        return (negative ? "-" : "") + String(digits) + "e" + String(shift)
    }
}

/// Dictionary JSON for external documents. Repeated members always use the
/// last value; callers own byte budgets and validate only the fields they use.
public enum JSONObject {
    public enum Error: Swift.Error { case invalidJSON }

    /// Exact object members in insertion order; updating a key keeps its place.
    /// Swift String dictionary equality normalizes keys.
    public struct Fields: Sequence {
        public typealias Element = (key: String, value: Any)
        private var members: [Data: Any] = [:]
        private var order: [Data] = []
        public init() {}
        public subscript(_ key: String) -> Any? {
            get { members[Data(key.utf8)] }
            set {
                let key = Data(key.utf8)
                if let newValue {
                    if members.updateValue(newValue, forKey: key) == nil { order.append(key) }
                } else if members.removeValue(forKey: key) != nil {
                    order.removeAll { $0 == key }
                }
            }
        }
        public var count: Int { members.count }
        public func makeIterator() -> AnyIterator<Element> {
            var iterator = order.makeIterator()
            return AnyIterator {
                guard let key = iterator.next() else { return nil }
                return (String(decoding: key, as: UTF8.self), self.members[key]!)
            }
        }
    }

    /// Exact raw JSON, including scalar roots, numeric tokens and UTF-8 keys.
    public static func decodeValue(_ data: Data, maximumDepth: Int = 511) throws -> Any {
        guard String(data: data, encoding: .utf8) != nil else { throw Error.invalidJSON }
        var parser = Parser(bytes: Array(data), maximumDepth: maximumDepth)
        if parser.bytes.starts(with: [0xEF, 0xBB, 0xBF]) { parser.offset = 3 }
        let value = try parser.value(depth: 0)
        parser.space()
        guard parser.offset == parser.bytes.count else { throw Error.invalidJSON }
        return value
    }

    /// A Foundation projection for DTO consumers. Retained documents must use
    /// decodeValue; a String dictionary cannot retain distinct UTF-8 spellings.
    public static func decode(_ data: Data) throws -> [String: Any] {
        guard let fields = try decodeValue(data) as? Fields else { throw Error.invalidJSON }
        func project(_ value: Any) -> Any {
            if let fields = value as? Fields {
                var result: [String: Any] = [:]
                for (key, value) in fields { result[key] = project(value) }
                return result
            }
            if let values = value as? [Any] { return values.map(project) }
            if let number = value as? JSONNumber {
                if let value = Int64(number.literal) { return NSNumber(value: value) }
                if let value = UInt64(number.literal) { return NSNumber(value: value) }
                if let value = number.exactDouble { return NSNumber(value: value) }
            }
            return value
        }
        return project(fields) as! [String: Any]
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
            if let object = value as? Fields {
                bytes.append(123)
                for (index, field) in object.sorted(by: { $0.key.utf8.lexicographicallyPrecedes($1.key.utf8) }).enumerated() {
                    if index > 0 { bytes.append(44) }
                    bytes.append(try encoder.encode(field.key)); bytes.append(58)
                    try write(field.value, depth: depth + 1)
                }
                bytes.append(125)
            } else if let object = value as? [String: Any] {
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
        let maximumDepth: Int
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
            guard depth <= maximumDepth, offset < bytes.count else { throw Error.invalidJSON }
            switch bytes[offset] {
            case 34: return try string()
            case 123:
                offset += 1
                var object = Fields()
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
            if offset < bytes.count, bytes[offset] == 46 {
                offset += 1
                guard digits() > 0 else { throw Error.invalidJSON }
            }
            if offset < bytes.count, bytes[offset] == 101 || bytes[offset] == 69 {
                offset += 1
                if offset < bytes.count, bytes[offset] == 43 || bytes[offset] == 45 { offset += 1 }
                guard digits() > 0 else { throw Error.invalidJSON }
            }
            return JSONNumber(String(decoding: bytes[start..<offset], as: UTF8.self))
        }
    }
}
