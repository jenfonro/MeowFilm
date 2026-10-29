import Foundation

public enum JSONValueError: Error, Equatable, Sendable {
    case invalidValue, limit
}

/// Bounded protocol JSON. Object keys are ASCII; opaque Unicode values
/// remain exact text. Integer bounds match the platform document format.
public indirect enum JSONValue: Codable, Equatable, Sendable {
    case null, bool(Bool), number(Double), string(String)
    case array([JSONValue]), object([String: JSONValue])

    public init(from decoder: Decoder) throws {
        let container = try decoder.singleValueContainer()
        if container.decodeNil() { self = .null }
        else if let value = try? container.decode(Bool.self) { self = .bool(value) }
        else if let value = try? container.decode(Double.self) {
            guard value.isFinite, value.rounded(.towardZero) != value || abs(value) <= 9_007_199_254_740_991 else {
                throw JSONValueError.invalidValue
            }
            self = .number(value)
        }
        else if let value = try? container.decode(String.self) { self = .string(value) }
        else if let value = try? container.decode([JSONValue].self) { self = .array(value) }
        else {
            let values = try container.decode([String: JSONValue].self)
            guard values.keys.allSatisfy(Self.validKey) else { throw JSONValueError.invalidValue }
            self = .object(values)
        }
    }
    public func encode(to encoder: Encoder) throws {
        var container = encoder.singleValueContainer()
        switch self {
        case .null: try container.encodeNil()
        case .bool(let value): try container.encode(value)
        case .number(let value):
            guard value.isFinite, value.rounded(.towardZero) != value || abs(value) <= 9_007_199_254_740_991 else {
                throw JSONValueError.invalidValue
            }
            try container.encode(value)
        case .string(let value): try container.encode(value)
        case .array(let value): try container.encode(value)
        case .object(let value):
            guard value.keys.allSatisfy(Self.validKey) else { throw JSONValueError.invalidValue }
            try container.encode(value)
        }
    }
    func validate(depth: Int = 0) throws {
        guard depth <= 64 else { throw JSONValueError.invalidValue }
        switch self {
        case .number(let value):
            guard value.isFinite, value.rounded(.towardZero) != value || abs(value) <= 9_007_199_254_740_991 else {
                throw JSONValueError.invalidValue
            }
        case .array(let values): try values.forEach { try $0.validate(depth: depth + 1) }
        case .object(let values):
            guard values.keys.allSatisfy(Self.validKey) else { throw JSONValueError.invalidValue }
            try values.values.forEach { try $0.validate(depth: depth + 1) }
        default: break
        }
    }
    static func validKey(_ key: String) -> Bool { key.utf8.allSatisfy { $0 < 128 } }
}
