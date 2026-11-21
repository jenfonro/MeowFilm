import Foundation

public struct SQLiteTreeField: Sendable {
    public let name: String
    public let value: SQLiteTreeValue
    public init(_ name: String, _ value: SQLiteTreeValue) { self.name = name; self.value = value }
}

/// Only genuinely dynamic guest/provider fields use trees. Known business
/// entities belong in their repositories' typed columns and relations.
public indirect enum SQLiteTreeValue: Sendable {
    case null, boolean(Bool), number(Double), exactNumber(String), text(String)
    case array([SQLiteTreeValue]), object([SQLiteTreeField])
}

public enum SQLiteValueTree {
    public static let columns = ["node_id", "parent_id", "prop_key", "item_index", "kind",
                                 "bool_value", "int_value", "real_value", "text_value"]
    private static let number = try! NSRegularExpression(pattern: #"^-?(?:0|[1-9][0-9]*)(?:\.[0-9]+)?(?:[eE][+-]?[0-9]+)?$"#)

    public static func rows(_ value: SQLiteTreeValue) throws -> [[SQLiteValue]] {
        var rows: [[SQLiteValue]] = []
        func visit(_ value: SQLiteTreeValue, parent: Int?, name: String?, index: Int?, depth: Int) throws {
            guard depth <= 64 else { throw SQLiteStorageError.queueFull }
            let id = rows.count
            var kind: String, boolean: SQLiteValue = .null, integer: SQLiteValue = .null
            var real: SQLiteValue = .null, text: SQLiteValue = .null
            switch value {
            case .null: kind = "null"
            case .boolean(let value): kind = "boolean"; boolean = .bool(value)
            case .number(let value):
                guard value.isFinite else { throw SQLiteStorageError.invalidValue }
                if value == 0 && value.sign == .minus { kind = "number_text"; text = .text("-0") }
                else if let value = Int64(exactly: value) { kind = "integer"; integer = .integer(value) }
                else { kind = "real"; real = .real(value) }
            case .exactNumber(let value):
                guard number.firstMatch(in: value, range: NSRange(value.startIndex..<value.endIndex, in: value))?.range.length == value.utf16.count else {
                    throw SQLiteStorageError.invalidValue
                }
                kind = "number_text"; text = .text(value)
            case .text(let value): kind = "text"; text = .text(value)
            case .array: kind = "array"
            case .object: kind = "object"
            }
            let row: [SQLiteValue] = [.integer(id), .optional(parent), .optional(name), .optional(index), .text(kind),
                                     boolean, integer, real, text]
            rows.append(row)
            switch value {
            case .array(let values):
                for (index, value) in values.enumerated() { try visit(value, parent: id, name: nil, index: index, depth: depth + 1) }
            case .object(let fields):
                var names = Set<Data>()
                for field in fields.sorted(by: { $0.name.utf8.lexicographicallyPrecedes($1.name.utf8) }) {
                    guard names.insert(Data(field.name.utf8)).inserted else { throw SQLiteStorageError.invalidValue }
                    try visit(field.value, parent: id, name: field.name, index: nil, depth: depth + 1)
                }
            default: break
            }
        }
        try visit(value, parent: nil, name: nil, index: nil, depth: 0)
        return rows
    }

    public static func value(_ rows: [SQLiteRow]) throws -> SQLiteTreeValue {
        guard !rows.isEmpty else { throw SQLiteStorageError.invalidValue }
        var indexed: [Int: SQLiteRow] = [:], children: [Int: [Int]] = [:]
        var root: Int?
        for row in rows {
            let id = try row.integer("node_id")
            guard indexed.updateValue(row, forKey: id) == nil, id >= 0 else { throw SQLiteStorageError.corrupt }
            if let parent = try row.optionalInteger("parent_id") { children[parent, default: []].append(id) }
            else {
                guard root == nil else { throw SQLiteStorageError.corrupt }
                root = id
            }
        }
        guard let root else { throw SQLiteStorageError.corrupt }
        var visited = Set<Int>()
        func build(_ id: Int, depth: Int) throws -> SQLiteTreeValue {
            guard depth <= 64, visited.insert(id).inserted, let row = indexed[id] else { throw SQLiteStorageError.corrupt }
            let kind = try row.text("kind"), descendants = children[id] ?? []
            guard kind == "array" || kind == "object" || descendants.isEmpty else { throw SQLiteStorageError.corrupt }
            switch kind {
            case "null": return .null
            case "boolean": return .boolean(try row.bool("bool_value"))
            case "integer":
                let integer = try row.integer("int_value")
                return (-9_007_199_254_740_991...9_007_199_254_740_991).contains(integer) ?
                    .number(Double(integer)) : .exactNumber(String(integer))
            case "real": return .number(try row.double("real_value"))
            case "number_text":
                let value = try row.text("text_value")
                guard number.firstMatch(in: value, range: NSRange(value.startIndex..<value.endIndex, in: value))?.range.length == value.utf16.count else {
                    throw SQLiteStorageError.corrupt
                }
                return .exactNumber(value)
            case "text": return .text(try row.text("text_value"))
            case "array":
                let list = try descendants.map { child -> (Int, Int) in
                    guard indexed[child]!["prop_key"] == .null else { throw SQLiteStorageError.corrupt }
                    return (try indexed[child]!.integer("item_index"), child)
                }.sorted { $0.0 < $1.0 }
                guard list.enumerated().allSatisfy({ $0.offset == $0.element.0 }) else { throw SQLiteStorageError.corrupt }
                return .array(try list.map { try build($0.1, depth: depth + 1) })
            case "object":
                var names = Set<Data>()
                return .object(try descendants.map {
                    let name = try indexed[$0]!.text("prop_key")
                    guard indexed[$0]!["item_index"] == .null, names.insert(Data(name.utf8)).inserted else {
                        throw SQLiteStorageError.corrupt
                    }
                    return SQLiteTreeField(name, try build($0, depth: depth + 1))
                })
            default: throw SQLiteStorageError.corrupt
            }
        }
        let value = try build(root, depth: 0)
        guard visited.count == rows.count else { throw SQLiteStorageError.corrupt }
        return value
    }
}
