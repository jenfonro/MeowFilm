import Foundation
import MeowStorage

/// Closed, repository-declared columns and relationships. This maps an in-memory
/// wire projection to real domain tables; no JSON or generic business EAV is stored.
public struct RuntimeRecordField: Sendable {
    public enum Kind: Equatable, Sendable { case text, integer, real, boolean, identifier }
    public let name: String
    public let kind: Kind
    public let column: String
    public init(_ name: String, _ kind: Kind, column: String? = nil) {
        self.name = name; self.kind = kind; self.column = column ?? name
    }
    var columns: [String] { kind == .identifier ? [column + "_kind", column + "_value"] : [column] }
}

public struct RuntimeRecordRelation: Sendable {
    public enum Kind: Equatable, Sendable { case object, array, dictionary, values, mapValues }
    public let name: String
    public let kind: Kind
    public let record: RuntimeRelationalRecord
    public let identity: String?
    public init(_ name: String, _ kind: Kind, _ record: RuntimeRelationalRecord, identity: String? = nil) {
        self.name = name; self.kind = kind; self.record = record; self.identity = identity
    }
}

public final class RuntimeRelationalRecord: Sendable {
    public let table: String
    public let keys: [String]
    public let fields: [RuntimeRecordField]
    public let relations: [RuntimeRecordRelation]
    private let constants: RuntimeObject
    private let extensionTable: String?
    private let identityField: String?
    private let upsertSQL: String
    private let memberNames: [String]
    private let declaredNames: Set<Data>

    public init(_ table: String, keys: [String], fields: [RuntimeRecordField] = [],
                relations: [RuntimeRecordRelation] = [], constants: RuntimeObject = [:], extensionTable: String? = nil,
                identityField: String? = nil) {
        precondition(fields.count + relations.count < 63)
        self.table = table; self.keys = keys; self.fields = fields; self.relations = relations
        self.constants = constants; self.extensionTable = extensionTable
        self.identityField = identityField
        self.upsertSQL = SQLiteConnection.upsertSQL(table, columns: keys + ["ordinal", "shape_mask", "null_mask"] + fields.flatMap(\.columns), key: keys)
        self.memberNames = fields.map(\.name) + relations.map(\.name)
        self.declaredNames = Set((memberNames + constants.keys + (identityField.map { [$0] } ?? [])).map { Data($0.utf8) })
    }
    private func quote(_ text: String) -> String { "\"" + text.replacingOccurrences(of: "\"", with: "\"\"") + "\"" }
    private func predicate(_ count: Int? = nil) -> String { keys.prefix(count ?? keys.count).map { quote($0) + " IS ?" }.joined(separator: " AND ") }
    private static func object(_ value: RuntimeValue?) throws -> RuntimeObject {
        guard case .object(let object) = value else { throw SQLiteStorageError.invalidValue }
        return object
    }
    private func extensions(_ object: RuntimeObject) throws -> RuntimeObject {
        let result = RuntimeObject(uniqueKeysWithValues: object.filter { !declaredNames.contains(Data($0.key.utf8)) })
        guard extensionTable != nil || result.isEmpty else { throw SQLiteStorageError.invalidValue }
        return result
    }
    private func values(_ field: RuntimeRecordField, _ value: RuntimeValue?) throws -> [SQLiteValue] {
        guard let value, value != .null else { return Array(repeating: .null, count: field.columns.count) }
        switch (field.kind, value) {
        case (.text, .string(let value)): return [.text(value)]
        case (.boolean, .bool(let value)): return [.bool(value)]
        case (.real, .number(let value)) where value.isFinite: return [.real(value)]
        case (.integer, .number(let value)) where value.isFinite && value.rounded(.towardZero) == value &&
            abs(value) <= 9_007_199_254_740_991: return [.integer(Int64(value))]
        case (.identifier, .string(let value)): return [.text("text"), .text(value)]
        case (.identifier, .number(let value)) where value.isFinite:
            return [.text("number"), .text(value == 0 && value.sign == .minus ? "-0" : String(value))]
        case (.identifier, .exactNumber(let value)): return [.text("number"), .text(value.literal)]
        default: throw SQLiteStorageError.invalidValue
        }
    }
    private func value(_ field: RuntimeRecordField, _ row: SQLiteRow) throws -> RuntimeValue {
        if row[field.columns[0]] == .null { return .null }
        switch field.kind {
        case .text: return .string(try row.text(field.column))
        case .boolean: return .bool(try row.bool(field.column))
        case .integer: return .number(Double(try row.integer(field.column)))
        case .real: return .number(try row.double(field.column))
        case .identifier:
            let text = try row.text(field.column + "_value")
            switch try row.text(field.column + "_kind") {
            case "text": return .string(text)
            case "number":
                let number = try RuntimeJSON.decode(Data(text.utf8))
                guard case .number = number else {
                    if case .exactNumber = number { return number }
                    throw SQLiteStorageError.corrupt
                }
                return number
            default: throw SQLiteStorageError.corrupt
            }
        }
    }
    private struct ReadBatch {
        var tables: [String: [[SQLiteValue]: [SQLiteRow]]] = [:]
        mutating func add(_ table: String, rows: [SQLiteRow], keys: [String]) {
            tables[table] = Dictionary(grouping: rows) { row in keys.map { row[$0] } }
        }
        func rows(_ table: String, key: [SQLiteValue]) -> [SQLiteRow] { tables[table]?[key] ?? [] }
    }
    public func read(_ db: SQLiteConnection, key: [SQLiteValue]) throws -> RuntimeValue? {
        guard key.count == keys.count else { throw SQLiteStorageError.invalidValue }
        guard let row = try db.query("SELECT * FROM \(quote(table)) WHERE \(predicate())", key).first else { return nil }
        var batch = ReadBatch()
        try loadChildren(db, rootKey: key, batch: &batch)
        return try read(row: row, batch: batch)
    }
    // One scoped SELECT per declared table, independent of the number of parent rows.
    private func loadChildren(_ db: SQLiteConnection, rootKey: [SQLiteValue], batch: inout ReadBatch) throws {
        for relation in relations {
            let child = relation.record
            let rows = try db.query("SELECT * FROM \(quote(child.table)) WHERE \(child.predicate(rootKey.count)) ORDER BY ordinal", rootKey)
            batch.add(child.table, rows: rows, keys: keys)
            try child.loadChildren(db, rootKey: rootKey, batch: &batch)
        }
        if let extensionTable {
            let nodes = try db.query("SELECT * FROM \(quote(extensionTable)) WHERE \(predicate(rootKey.count)) ORDER BY node_id", rootKey)
            batch.add(extensionTable, rows: nodes, keys: keys)
        }
    }
    private func read(row: SQLiteRow, batch: ReadBatch) throws -> RuntimeValue {
        var object = constants
        if let identityField { object[identityField] = .string(try row.text(keys.last!)) }
        let key = keys.map { row[$0] }, shape = Int64(try row.integer("shape_mask")), nulls = Int64(try row.integer("null_mask"))
        guard shape >= 0, shape >> (fields.count + relations.count) == 0, nulls >= 0, nulls & ~shape == 0 else {
            throw SQLiteStorageError.corrupt
        }
        for (index, field) in fields.enumerated() where shape & (1 << index) != 0 { object[field.name] = try value(field, row) }
        for (index, relation) in relations.enumerated() where shape & (1 << (fields.count + index)) != 0 {
            if nulls & (1 << (fields.count + index)) != 0 { object[relation.name] = .null; continue }
            let child = relation.record
            let rows = batch.rows(child.table, key: key)
            switch relation.kind {
            case .object:
                guard rows.count == 1 else { throw SQLiteStorageError.corrupt }
                object[relation.name] = try child.read(row: rows[0], batch: batch)
            case .array: object[relation.name] = .array(try rows.map { try child.read(row: $0, batch: batch) })
            case .values:
                object[relation.name] = .array(try rows.map { try Self.object(child.read(row: $0, batch: batch))["value"] ?? .null })
            case .dictionary, .mapValues:
                var dictionary = RuntimeObject()
                for row in rows {
                    let name = try row.text(child.keys.last!)
                    guard dictionary[name] == nil else { throw SQLiteStorageError.corrupt }
                    let value = try child.read(row: row, batch: batch)
                    dictionary[name] = relation.kind == .mapValues ? try Self.object(value)["value"] ?? .null : value
                }
                object[relation.name] = .object(dictionary)
            }
        }
        if let extensionTable {
            let nodes = batch.rows(extensionTable, key: key)
            if !nodes.isEmpty {
                guard case .object(let extra) = try RuntimeValue(storageTree: SQLiteValueTree.value(nodes)),
                      try extensions(extra) == extra else { throw SQLiteStorageError.corrupt }
                for (name, value) in extra { object[name] = value }
            }
        }
        return .object(object)
    }

    private struct Child {
        let key: [SQLiteValue]
        let value: RuntimeValue
        let ordinal: Int
    }
    private func children(_ relation: RuntimeRecordRelation, value: RuntimeValue?, key: [SQLiteValue]) throws -> [Child] {
        guard let value, value != .null else { return [] }
        if relation.kind == .object {
            _ = try Self.object(value)
            return [.init(key: key, value: value, ordinal: 0)]
        }
        let rows: [(String, RuntimeValue)]
        if relation.kind == .dictionary || relation.kind == .mapValues {
            rows = try Self.object(value).map {
                ($0.key, relation.kind == .mapValues ? .object(["value": $0.value]) : $0.value)
            }
        } else {
            guard case .array(let values) = value else { throw SQLiteStorageError.invalidValue }
            rows = try values.enumerated().map { index, value in
                if let identity = relation.identity {
                    let identityValue = relation.kind == .values ? value : try Self.object(value)[identity]
                    guard case .string(let id) = identityValue, !id.isEmpty else { throw SQLiteStorageError.invalidValue }
                    return (id, relation.kind == .values ? .object(["value": value]) : value)
                }
                return (String(index), relation.kind == .values ? .object(["value": value]) : value)
            }
        }
        var identities = Set<Data>()
        return try rows.enumerated().map { index, row in
            guard identities.insert(Data(row.0.utf8)).inserted else { throw SQLiteStorageError.constraint }
            // Keyed maps have no positional identity. Inserting one key must
            // not renumber and rewrite every lexically following member.
            let ordinal = relation.kind == .dictionary || relation.kind == .mapValues ? 0 : index
            return .init(key: key + [.text(row.0)], value: row.1, ordinal: ordinal)
        }
    }
    /// `previous` is from the repository's revision-checked coherent snapshot.
    /// Equal subtrees are not queried, encoded or rewritten.
    public func save(_ db: SQLiteConnection, value: RuntimeValue?, previous: RuntimeValue?, key: [SQLiteValue], ordinal: Int = 0,
                     previousOrdinal: Int = 0) throws {
        guard key.count == keys.count else { throw SQLiteStorageError.invalidValue }
        if value == previous, ordinal == previousOrdinal { return }
        guard let value else { try db.execute("DELETE FROM \(quote(table)) WHERE \(predicate())", key); return }
        let object = try Self.object(value), extra = try extensions(object)
        if let identityField {
            guard let last = key.last, case .text(let id) = last, object[identityField] == .string(id) else {
                throw SQLiteStorageError.invalidValue
            }
        }
        for (name, constant) in constants where object[name] != constant { throw SQLiteStorageError.invalidValue }
        var shape: Int64 = 0, nulls: Int64 = 0
        for (index, name) in memberNames.enumerated() {
            if object[name] != nil { shape |= 1 << index }
            if object[name] == .null { nulls |= 1 << index }
        }
        let cells = key + [.integer(ordinal), .integer(shape), .integer(nulls)] + (try fields.flatMap { try values($0, object[$0.name]) })
        try db.execute(upsertSQL, cells)
        let before = previous.flatMap { if case .object(let fields) = $0 { return fields }; return nil } ?? RuntimeObject()
        for relation in relations {
            if before[relation.name] == object[relation.name] { continue }
            let old = try children(relation, value: before[relation.name], key: key)
            let next = try children(relation, value: object[relation.name], key: key)
            let nextKeys = Set(next.map(\.key)), oldByKey = Dictionary(uniqueKeysWithValues: old.map { ($0.key, $0) })
            for child in old where !nextKeys.contains(child.key) {
                try relation.record.save(db, value: nil, previous: child.value, key: child.key)
            }
            for child in next {
                let prior = oldByKey[child.key]
                try relation.record.save(db, value: child.value, previous: prior?.value, key: child.key, ordinal: child.ordinal,
                    previousOrdinal: prior?.ordinal ?? 0)
            }
        }
        if let extensionTable, extra != (try extensions(before)) {
            let nodes = extra.isEmpty ? [] : try SQLiteValueTree.rows(RuntimeValue.object(extra).storageTree)
            try db.reconcile(extensionTable, columns: keys + SQLiteValueTree.columns, key: keys + ["node_id"],
                rows: nodes.map { key + $0 }, where: predicate(), bindings: key)
        }
    }
}
