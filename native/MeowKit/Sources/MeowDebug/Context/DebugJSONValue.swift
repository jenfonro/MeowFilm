import Foundation

public enum DebugJSONValue: Sendable, Equatable {
    case null
    case bool(Bool)
    case integer(Int64)
    case number(Double)
    case string(String)
    case array([DebugJSONValue])
    case object([String: DebugJSONValue])

    public static func int(_ value: Int) -> DebugJSONValue { .integer(Int64(value)) }
    public static func uint64(_ value: UInt64) -> DebugJSONValue {
        value <= UInt64(Int64.max) ? .integer(Int64(value)) : .number(Double(value))
    }
    public static func optional(_ value: String?) -> DebugJSONValue { value.map(Self.string) ?? .null }
    public static func optional(_ value: Int?) -> DebugJSONValue { value.map(Self.int) ?? .null }
    public static func optional(_ value: Double?) -> DebugJSONValue {
        guard let value, value.isFinite else { return .null }
        return .number(value)
    }
    public static func optional(_ value: Bool?) -> DebugJSONValue { value.map(Self.bool) ?? .null }

    public var stringValue: String? {
        if case .string(let value) = self { return value }
        return nil
    }
    public var boolValue: Bool? {
        if case .bool(let value) = self { return value }
        return nil
    }
    public var intValue: Int? {
        switch self {
        case .integer(let value): return Int(exactly: value)
        case .number(let value):
            guard value.isFinite, value.rounded(.towardZero) == value else { return nil }
            return Int(exactly: value)
        default: return nil
        }
    }
    public var doubleValue: Double? {
        switch self {
        case .number(let value): return value.isFinite ? value : nil
        case .integer(let value): return Double(value)
        default: return nil
        }
    }
    public var arrayValue: [DebugJSONValue]? {
        if case .array(let value) = self { return value }
        return nil
    }
    public var objectValue: [String: DebugJSONValue]? {
        if case .object(let value) = self { return value }
        return nil
    }

    public subscript(_ key: String) -> DebugJSONValue? {
        guard case .object(let map) = self else { return nil }
        return map[key]
    }

    public func toFoundationObject() -> Any {
        switch self {
        case .null: return NSNull()
        case .bool(let value): return NSNumber(value: value)
        case .integer(let value): return NSNumber(value: value)
        case .number(let value): return value.isFinite ? NSNumber(value: value) : NSNull()
        case .string(let value): return value
        case .array(let items): return items.map { $0.toFoundationObject() }
        case .object(let fields):
            var result: [String: Any] = [:]
            result.reserveCapacity(fields.count)
            for (k, v) in fields { result[k] = v.toFoundationObject() }
            return result
        }
    }

    public static func fromFoundationObject(_ raw: Any) -> DebugJSONValue {
        switch raw {
        case is NSNull: return .null
        case let value as String: return .string(value)
        case let value as NSNumber:
            if CFGetTypeID(value) == CFBooleanGetTypeID() {
                return .bool(value.boolValue)
            }
            let objCType = String(cString: value.objCType)
            if ["c", "i", "s", "l", "q", "C", "I", "S", "L", "Q"].contains(objCType) {
                return .integer(value.int64Value)
            }
            let dbl = value.doubleValue
            return dbl.isFinite ? .number(dbl) : .null
        case let list as [Any]:
            return .array(list.map(fromFoundationObject))
        case let dict as [String: Any]:
            var result: [String: DebugJSONValue] = [:]
            result.reserveCapacity(dict.count)
            for (k, v) in dict { result[k] = fromFoundationObject(v) }
            return .object(result)
        default:
            return .string(String(describing: raw))
        }
    }

    public static func decode(data: Data) throws -> DebugJSONValue {
        let obj = try JSONSerialization.jsonObject(with: data, options: [.fragmentsAllowed])
        return fromFoundationObject(obj)
    }

    public func encode(pretty: Bool = false) throws -> Data {
        var options: JSONSerialization.WritingOptions = [.fragmentsAllowed, .sortedKeys]
        if pretty { options.insert(.prettyPrinted) }
        return try JSONSerialization.data(withJSONObject: toFoundationObject(), options: options)
    }

    public static func fromEncodable<T: Encodable>(_ value: T) -> DebugJSONValue {
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.sortedKeys]
        guard let data = try? encoder.encode(value),
              let decoded = try? decode(data: data) else { return .null }
        return decoded
    }
}

extension DebugJSONValue: Codable {
    public init(from decoder: Decoder) throws {
        let container = try decoder.singleValueContainer()
        if container.decodeNil() { self = .null; return }
        if let value = try? container.decode(Bool.self) { self = .bool(value); return }
        if let value = try? container.decode(Int64.self) { self = .integer(value); return }
        if let value = try? container.decode(Double.self) {
            self = value.isFinite ? .number(value) : .null
            return
        }
        if let value = try? container.decode(String.self) { self = .string(value); return }
        if let value = try? container.decode([DebugJSONValue].self) { self = .array(value); return }
        if let value = try? container.decode([String: DebugJSONValue].self) { self = .object(value); return }
        throw DecodingError.dataCorruptedError(in: container, debugDescription: "Unsupported JSON value")
    }

    public func encode(to encoder: Encoder) throws {
        var container = encoder.singleValueContainer()
        switch self {
        case .null: try container.encodeNil()
        case .bool(let value): try container.encode(value)
        case .integer(let value): try container.encode(value)
        case .number(let value):
            if value.isFinite { try container.encode(value) }
            else { try container.encodeNil() }
        case .string(let value): try container.encode(value)
        case .array(let items): try container.encode(items)
        case .object(let fields): try container.encode(fields)
        }
    }
}
