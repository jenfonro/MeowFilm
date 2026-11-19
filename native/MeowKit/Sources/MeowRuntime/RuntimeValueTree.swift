import Foundation
import MeowStorage
import MeowJSON

/// Typed conversion for source/provider extension values, never whole known
/// business documents. Neither conversion serializes a JSON object.
public extension RuntimeValue {
    var storageTree: SQLiteTreeValue {
        switch self {
        case .null: return .null
        case .bool(let value): return .boolean(value)
        case .number(let value): return .number(value)
        case .exactNumber(let value): return .exactNumber(value.literal)
        case .string(let value): return .text(value)
        case .array(let values): return .array(values.map(\.storageTree))
        case .object(let fields): return .object(fields.map { SQLiteTreeField($0.key, $0.value.storageTree) })
        }
    }

    init(storageTree value: SQLiteTreeValue) throws {
        switch value {
        case .null: self = .null
        case .boolean(let value): self = .bool(value)
        case .number(let value): self = .number(value)
        case .exactNumber(let value):
            guard let data = value.data(using: .utf8) else { throw SQLiteStorageError.corrupt }
            self = try RuntimeJSON.decode(data)
            guard case .number = self else {
                if case .exactNumber = self { return }
                throw SQLiteStorageError.corrupt
            }
        case .text(let value): self = .string(value)
        case .array(let values): self = .array(try values.map { try RuntimeValue(storageTree: $0) })
        case .object(let fields):
            var object = RuntimeObject()
            for field in fields {
                guard object[field.name] == nil else { throw SQLiteStorageError.corrupt }
                object[field.name] = try RuntimeValue(storageTree: field.value)
            }
            self = .object(object)
        }
    }
}
