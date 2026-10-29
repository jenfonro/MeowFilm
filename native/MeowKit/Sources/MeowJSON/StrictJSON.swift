import Foundation

/// Validates byte size, UTF-8, duplicate/escaped keys and depth before Codable.
/// No message framing, RPC, process lifecycle or executable payloads.
public enum StrictJSON {
    public static func decode(_ data: Data, maximumBytes: Int = 1024 * 1024) throws -> JSONValue {
        guard maximumBytes > 0, maximumBytes <= 8 * 1024 * 1024,
              !data.isEmpty, data.count <= maximumBytes else { throw JSONValueError.limit }
        guard String(data: data, encoding: .utf8) != nil,
              !data.starts(with: [0xef, 0xbb, 0xbf]) else { throw JSONValueError.invalidValue }
        do {
            try checkKeys(data)
            let value = try JSONDecoder().decode(JSONValue.self, from: data)
            try value.validate()
            return value
        } catch { throw error as? JSONValueError ?? JSONValueError.invalidValue }
    }

    private static func checkKeys(_ data: Data) throws {
        let bytes = Array(data)
        var stack: [Set<String>?] = []
        var index = 0
        while index < bytes.count {
            let byte = bytes[index]
            if byte == 123 || byte == 91 {
                stack.append(byte == 123 ? Set<String>() : nil)
                guard stack.count <= 66 else { throw JSONValueError.invalidValue }
            } else if byte == 125 || byte == 93 {
                guard !stack.isEmpty else { throw JSONValueError.invalidValue }
                stack.removeLast()
            } else if byte == 34 {
                let start = index
                index += 1
                while index < bytes.count {
                    if bytes[index] == 92 { index += 1 }
                    else if bytes[index] == 34 { break }
                    index += 1
                }
                guard index < bytes.count else { throw JSONValueError.invalidValue }
                var next = index + 1
                while next < bytes.count && [9, 10, 13, 32].contains(bytes[next]) { next += 1 }
                if next < bytes.count && bytes[next] == 58 {
                    let key = try JSONDecoder().decode(String.self, from: Data(bytes[start...index]))
                    guard JSONValue.validKey(key), let keys = stack.last ?? nil,
                          !keys.contains(key) else { throw JSONValueError.invalidValue }
                    stack[stack.count - 1]?.insert(key)
                }
            }
            index += 1
        }
    }
}
