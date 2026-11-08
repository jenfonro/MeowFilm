import Foundation

public enum RuntimeJSONError: Error, Sendable, Equatable { case invalidJSON }

/// Strict runtime business JSON. Depth 64 is the public value contract;
/// complete JSON documents have no transport byte quota.
public enum RuntimeJSON {
    public static func decode(_ data: Data) throws -> RuntimeValue {
        try parse(data, maximumDepth: 64)
    }

    public static func encode(_ value: RuntimeValue) throws -> Data {
        var data = Data()
        try write(value, to: &data, depth: 0)
        return data
    }

    // HTTP adds its envelope around the independently depth-checked value.
    static func envelope(_ data: Data) throws -> RuntimeValue {
        try parse(data, maximumDepth: 66)
    }

    static func validate(_ value: RuntimeValue, depth: Int = 0) throws {
        guard depth <= 64 else { throw RuntimeJSONError.invalidJSON }
        switch value {
        case .number(let number): try valid(number)
        case .array(let values):
            for value in values { try validate(value, depth: depth + 1) }
        case .object(let fields):
            for (_, value) in fields { try validate(value, depth: depth + 1) }
        default: break
        }
    }

    private static func valid(_ number: Double) throws {
        guard number.isFinite,
              number.rounded(.towardZero) != number || abs(number) <= 9_007_199_254_740_991 else {
            throw RuntimeJSONError.invalidJSON
        }
    }

    private static func parse(_ data: Data, maximumDepth: Int) throws -> RuntimeValue {
        guard String(data: data, encoding: .utf8) != nil else { throw RuntimeJSONError.invalidJSON }
        var parser = Parser(bytes: Array(data), maximumDepth: maximumDepth)
        if parser.bytes.starts(with: [0xef, 0xbb, 0xbf]) { parser.offset = 3 }
        let value = try parser.value(depth: 0)
        parser.space()
        guard parser.offset == parser.bytes.count else { throw RuntimeJSONError.invalidJSON }
        return value
    }

    private static func write(_ value: RuntimeValue, to data: inout Data, depth: Int) throws {
        guard depth <= 64 else { throw RuntimeJSONError.invalidJSON }
        switch value {
        case .null: data.append(contentsOf: "null".utf8)
        case .bool(let value): data.append(contentsOf: (value ? "true" : "false").utf8)
        case .number(let value):
            try valid(value)
            data.append(contentsOf: String(value).utf8)
        case .string(let value): writeString(value, to: &data)
        case .array(let values):
            data.append(91)
            for (index, child) in values.enumerated() {
                if index > 0 { data.append(44) }
                try write(child, to: &data, depth: depth + 1)
            }
            data.append(93)
        case .object(let fields):
            data.append(123)
            for (index, field) in fields.enumerated() {
                if index > 0 { data.append(44) }
                writeString(field.key, to: &data)
                data.append(58)
                try write(field.value, to: &data, depth: depth + 1)
            }
            data.append(125)
        }
    }

    private static func writeString(_ value: String, to data: inout Data) {
        data.append(34)
        if !value.utf8.contains(where: { $0 < 32 || $0 == 34 || $0 == 92 }) {
            data.append(contentsOf: value.utf8)
        } else {
            let bytes = Array(value.utf8), hex = Array("0123456789abcdef".utf8)
            var start = 0
            for index in bytes.indices {
                let byte = bytes[index]
                guard byte < 32 || byte == 34 || byte == 92 else { continue }
                data.append(contentsOf: bytes[start..<index])
                if byte == 34 || byte == 92 { data.append(contentsOf: [92, byte]) }
                else { data.append(contentsOf: [92, 117, 48, 48, hex[Int(byte >> 4)], hex[Int(byte & 15)]]) }
                start = index + 1
            }
            data.append(contentsOf: bytes[start...])
        }
        data.append(34)
    }

    private struct Parser {
        let bytes: [UInt8]
        let maximumDepth: Int
        var offset = 0

        mutating func space() {
            while offset < bytes.count, [9, 10, 13, 32].contains(bytes[offset]) { offset += 1 }
        }

        mutating func take(_ byte: UInt8) -> Bool {
            space()
            guard offset < bytes.count, bytes[offset] == byte else { return false }
            offset += 1
            return true
        }

        mutating func value(depth: Int) throws -> RuntimeValue {
            space()
            guard depth <= maximumDepth, offset < bytes.count else { throw RuntimeJSONError.invalidJSON }
            switch bytes[offset] {
            case 34: return .string(try string())
            case 123:
                offset += 1
                var fields = RuntimeObject()
                if take(125) { return .object(fields) }
                repeat {
                    let key = try string()
                    guard fields[key] == nil, take(58) else { throw RuntimeJSONError.invalidJSON }
                    fields[key] = try value(depth: depth + 1)
                } while take(44)
                guard take(125) else { throw RuntimeJSONError.invalidJSON }
                return .object(fields)
            case 91:
                offset += 1
                var values: [RuntimeValue] = []
                if take(93) { return .array(values) }
                repeat { values.append(try value(depth: depth + 1)) } while take(44)
                guard take(93) else { throw RuntimeJSONError.invalidJSON }
                return .array(values)
            case 110: try literal("null"); return .null
            case 116: try literal("true"); return .bool(true)
            case 102: try literal("false"); return .bool(false)
            default: return .number(try number())
            }
        }

        mutating func literal(_ text: String) throws {
            guard bytes[offset...].starts(with: text.utf8) else { throw RuntimeJSONError.invalidJSON }
            offset += text.utf8.count
        }

        mutating func string() throws -> String {
            guard take(34) else { throw RuntimeJSONError.invalidJSON }
            var start = offset, result = ""
            while offset < bytes.count {
                let byte = bytes[offset]
                if byte == 34 {
                    result += String(decoding: bytes[start..<offset], as: UTF8.self)
                    offset += 1
                    return result
                }
                guard byte >= 32 else { throw RuntimeJSONError.invalidJSON }
                if byte == 92 {
                    result += String(decoding: bytes[start..<offset], as: UTF8.self)
                    offset += 1
                    guard offset < bytes.count else { throw RuntimeJSONError.invalidJSON }
                    let escaped = bytes[offset]
                    offset += 1
                    switch escaped {
                    case 34: result.append("\"")
                    case 92: result.append("\\")
                    case 47: result.append("/")
                    case 98: result.append("\u{08}")
                    case 102: result.append("\u{0c}")
                    case 110: result.append("\n")
                    case 114: result.append("\r")
                    case 116: result.append("\t")
                    case 117:
                        var scalar = try hexUnit()
                        if (0xd800...0xdbff).contains(scalar) {
                            guard offset + 2 <= bytes.count, bytes[offset] == 92, bytes[offset + 1] == 117 else {
                                throw RuntimeJSONError.invalidJSON
                            }
                            offset += 2
                            let low = try hexUnit()
                            guard (0xdc00...0xdfff).contains(low) else { throw RuntimeJSONError.invalidJSON }
                            scalar = 0x10000 + ((scalar - 0xd800) << 10) + low - 0xdc00
                        }
                        guard let value = UnicodeScalar(scalar) else { throw RuntimeJSONError.invalidJSON }
                        result.unicodeScalars.append(value)
                    default: throw RuntimeJSONError.invalidJSON
                    }
                    start = offset
                } else { offset += 1 }
            }
            throw RuntimeJSONError.invalidJSON
        }

        mutating func hexUnit() throws -> UInt32 {
            guard offset + 4 <= bytes.count else { throw RuntimeJSONError.invalidJSON }
            var value: UInt32 = 0
            for byte in bytes[offset..<(offset + 4)] {
                let digit: UInt8
                switch byte {
                case 48...57: digit = byte - 48
                case 65...70: digit = byte - 55
                case 97...102: digit = byte - 87
                default: throw RuntimeJSONError.invalidJSON
                }
                value = value << 4 | UInt32(digit)
            }
            offset += 4
            return value
        }

        mutating func digits() -> Int {
            let start = offset
            while offset < bytes.count, (48...57).contains(bytes[offset]) { offset += 1 }
            return offset - start
        }

        mutating func number() throws -> Double {
            let start = offset
            if bytes[offset] == 45 { offset += 1 }
            guard offset < bytes.count else { throw RuntimeJSONError.invalidJSON }
            if bytes[offset] == 48 { offset += 1 }
            else {
                guard (49...57).contains(bytes[offset]), digits() > 0 else { throw RuntimeJSONError.invalidJSON }
            }
            if offset < bytes.count, bytes[offset] == 46 {
                offset += 1
                guard digits() > 0 else { throw RuntimeJSONError.invalidJSON }
            }
            if offset < bytes.count, bytes[offset] == 101 || bytes[offset] == 69 {
                offset += 1
                if offset < bytes.count, bytes[offset] == 43 || bytes[offset] == 45 { offset += 1 }
                guard digits() > 0 else { throw RuntimeJSONError.invalidJSON }
            }
            guard let result = Double(String(decoding: bytes[start..<offset], as: UTF8.self)) else {
                throw RuntimeJSONError.invalidJSON
            }
            try RuntimeJSON.valid(result)
            return result
        }
    }
}
