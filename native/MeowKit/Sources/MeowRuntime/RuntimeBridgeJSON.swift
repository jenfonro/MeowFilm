import Foundation
import MeowJSON

/// JSON at the runtime protocol boundary, not for platform responses or ordinary
/// documents. ASCII keys, safe integers and depth 64 match runtime-bridge.
/// The byte budget is the caller's message/transfer resource boundary.
public enum RuntimeBridgeJSON {
    public static func decode(_ data: Data, maximumBytes: Int = 1024 * 1024) throws -> JSONValue {
        guard maximumBytes > 0, maximumBytes <= 8 * 1024 * 1024,
              !data.isEmpty, data.count <= maximumBytes else { throw RuntimeBridgeError.limit }
        guard String(data: data, encoding: .utf8) != nil,
              !data.starts(with: [0xef, 0xbb, 0xbf]) else { throw RuntimeBridgeError.invalidMessage }
        do {
            try checkKeys(data)
            let value = try JSONDecoder().decode(JSONValue.self, from: data)
            try validate(value)
            return value
        } catch { throw error as? RuntimeBridgeError ?? RuntimeBridgeError.invalidMessage }
    }

    /// The encoding counterpart: the same depth, key and number rules as decode.
    public static func encode(_ value: JSONValue, maximumBytes: Int = 1024 * 1024) throws -> Data {
        guard maximumBytes > 0, maximumBytes <= 8 * 1024 * 1024 else { throw RuntimeBridgeError.limit }
        try validate(value)
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.withoutEscapingSlashes]
        let data: Data
        do { data = try encoder.encode(value) }
        catch { throw error as? RuntimeBridgeError ?? RuntimeBridgeError.invalidMessage }
        guard data.count <= maximumBytes else { throw RuntimeBridgeError.limit }
        return data
    }

    private static func checkKeys(_ data: Data) throws {
        let bytes = Array(data)
        var stack: [Set<String>?] = []
        var index = 0
        while index < bytes.count {
            let byte = bytes[index]
            if byte == 123 || byte == 91 {
                stack.append(byte == 123 ? Set<String>() : nil)
                guard stack.count <= 66 else { throw RuntimeBridgeError.invalidMessage }
            } else if byte == 125 || byte == 93 {
                guard !stack.isEmpty else { throw RuntimeBridgeError.invalidMessage }
                stack.removeLast()
            } else if byte == 34 {
                let start = index
                index += 1
                while index < bytes.count {
                    if bytes[index] == 92 { index += 1 }
                    else if bytes[index] == 34 { break }
                    index += 1
                }
                guard index < bytes.count else { throw RuntimeBridgeError.invalidMessage }
                var next = index + 1
                while next < bytes.count && [9, 10, 13, 32].contains(bytes[next]) { next += 1 }
                if next < bytes.count && bytes[next] == 58 {
                    let key = try JSONDecoder().decode(String.self, from: Data(bytes[start...index]))
                    guard validKey(key), let keys = stack.last ?? nil,
                          !keys.contains(key) else { throw RuntimeBridgeError.invalidMessage }
                    stack[stack.count - 1]?.insert(key)
                }
            }
            index += 1
        }
    }

    private static func validKey(_ key: String) -> Bool { key.utf8.allSatisfy { $0 < 128 } }

    private static func validate(_ value: JSONValue, depth: Int = 0) throws {
        guard depth <= 64 else { throw RuntimeBridgeError.invalidMessage }
        switch value {
        case .number(let number):
            guard number.isFinite, number.rounded(.towardZero) != number || abs(number) <= 9_007_199_254_740_991 else {
                throw RuntimeBridgeError.invalidMessage
            }
        case .array(let values): try values.forEach { try validate($0, depth: depth + 1) }
        case .object(let values):
            guard values.keys.allSatisfy(validKey) else { throw RuntimeBridgeError.invalidMessage }
            try values.values.forEach { try validate($0, depth: depth + 1) }
        default: break
        }
    }
}
