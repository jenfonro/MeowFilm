import Foundation
import CoreFoundation
import MeowJSON

public enum RuntimeJSONError: Error, Sendable, Equatable { case invalidJSON }

/// One JSON syntax owner; business consumers select their required fields.
/// Unconsumed numbers and UTF-8 member identities survive forwarding exactly.
public enum RuntimeJSON {
    public static func decode(_ data: Data) throws -> RuntimeValue { try parse(data, maximumDepth: 64) }

    private static func parse(_ data: Data, maximumDepth: Int) throws -> RuntimeValue {
        func value(_ raw: Any) throws -> RuntimeValue {
            if let fields = raw as? JSONObject.Fields {
                var object = RuntimeObject()
                for (key, child) in fields { object[key] = try value(child) }
                return .object(object)
            }
            if let values = raw as? [Any] { return .array(try values.map(value)) }
            if let text = raw as? String { return .string(text) }
            if let number = raw as? JSONNumber {
                return number.exactDouble.map(RuntimeValue.number) ?? .exactNumber(number)
            }
            if let boolean = raw as? NSNumber, CFGetTypeID(boolean) == CFBooleanGetTypeID() {
                return .bool(boolean.boolValue)
            }
            if raw is NSNull { return .null }
            throw RuntimeJSONError.invalidJSON
        }
        do { return try value(JSONObject.decodeValue(data, maximumDepth: maximumDepth)) }
        catch { throw RuntimeJSONError.invalidJSON }
    }

    public static func encode(_ value: RuntimeValue) throws -> Data {
        func raw(_ value: RuntimeValue, depth: Int = 0) throws -> Any {
            guard depth <= 64 else { throw RuntimeJSONError.invalidJSON }
            switch value {
            case .null: return NSNull()
            case .bool(let value): return NSNumber(value: value)
            case .number(let value):
                guard value.isFinite else { throw RuntimeJSONError.invalidJSON }
                return NSNumber(value: value)
            case .exactNumber(let value): return value
            case .string(let value): return value
            case .array(let values): return try values.map { try raw($0, depth: depth + 1) }
            case .object(let fields):
                var object = JSONObject.Fields()
                for (key, value) in fields { object[key] = try raw(value, depth: depth + 1) }
                return object
            }
        }
        do { return try JSONObject.encode(raw(value)) }
        catch { throw RuntimeJSONError.invalidJSON }
    }

    static func envelope(_ data: Data) throws -> RuntimeValue { try parse(data, maximumDepth: 65) }
}
