import Foundation
import MeowSQLite
import MeowDebug
#if canImport(Darwin)
import Darwin
#else
import Glibc
#endif

/// Closed failures never contain SQL parameters, credentials or file paths.
public enum SQLiteStorageError: Error, Equatable, Sendable {
    case invalidPath, invalidSchema, invalidValue, constraint, conflict, busy
    case full, readOnly, corrupt, io, closed, queueFull, mainThreadAccess
}

public enum SQLiteValue: Equatable, Hashable, Sendable {
    case null, integer(Int64), real(Double), text(String), blob(Data)

    public static func integer(_ value: Int) -> Self { .integer(Int64(value)) }
    public static func bool(_ value: Bool) -> Self { .integer(value ? 1 : 0) }
    public static func optional(_ value: String?) -> Self { value.map(Self.text) ?? .null }
    public static func optional(_ value: Double?) -> Self { value.map(Self.real) ?? .null }
    public static func optional(_ value: Int?) -> Self { value.map(Self.integer) ?? .null }
    public static func optional(_ value: Int64?) -> Self { value.map(Self.integer) ?? .null }
    public static func optional(_ value: Bool?) -> Self { value.map(Self.bool) ?? .null }
    public static func optional(_ value: Date?) -> Self { value.map { .real($0.timeIntervalSince1970) } ?? .null }
    public static func optional(_ value: Data?) -> Self { value.map(Self.blob) ?? .null }
    public var byteCount: Int {
        switch self {
        case .text(let text): return text.utf8.count
        case .blob(let data): return data.count
        default: return 8
        }
    }
}

public struct SQLiteRow: Equatable, Sendable {
    public let values: [String: SQLiteValue]
    public init(values: [String: SQLiteValue]) { self.values = values }
    public subscript(_ column: String) -> SQLiteValue { values[column] ?? .null }
    public func text(_ column: String) throws -> String {
        guard case .text(let value) = self[column] else { throw SQLiteStorageError.invalidValue }
        return value
    }
    public func optionalText(_ column: String) throws -> String? {
        self[column] == .null ? nil : try text(column)
    }
    public func int64(_ column: String) throws -> Int64 {
        guard case .integer(let value) = self[column] else { throw SQLiteStorageError.invalidValue }
        return value
    }
    public func optionalInt64(_ column: String) throws -> Int64? {
        self[column] == .null ? nil : try int64(column)
    }
    public func integer(_ column: String) throws -> Int {
        guard case .integer(let value) = self[column], let result = Int(exactly: value) else {
            throw SQLiteStorageError.invalidValue
        }
        return result
    }
    public func optionalInteger(_ column: String) throws -> Int? {
        self[column] == .null ? nil : try integer(column)
    }
    public func double(_ column: String) throws -> Double {
        switch self[column] {
        case .real(let value): return value
        case .integer(let value): return Double(value)
        default: throw SQLiteStorageError.invalidValue
        }
    }
    public func optionalDouble(_ column: String) throws -> Double? {
        self[column] == .null ? nil : try double(column)
    }
    public func bool(_ column: String) throws -> Bool {
        let value = try integer(column)
        guard value == 0 || value == 1 else { throw SQLiteStorageError.invalidValue }
        return value == 1
    }
    public func optionalBool(_ column: String) throws -> Bool? {
        self[column] == .null ? nil : try bool(column)
    }
    public func blob(_ column: String) throws -> Data {
        guard case .blob(let data) = self[column] else { throw SQLiteStorageError.invalidValue }
        return data
    }
    public func optionalBlob(_ column: String) throws -> Data? {
        self[column] == .null ? nil : try blob(column)
    }
    public func date(_ column: String) throws -> Date {
        Date(timeIntervalSince1970: try double(column))
    }
    public func optionalDate(_ column: String) throws -> Date? {
        try optionalDouble(column).map { Date(timeIntervalSince1970: $0) }
    }
    public func uuid(_ column: String) throws -> UUID {
        guard let value = UUID(uuidString: try text(column)) else { throw SQLiteStorageError.invalidValue }
        return value
    }
    public func optionalUUID(_ column: String) throws -> UUID? {
        guard let raw = try optionalText(column) else { return nil }
        guard let value = UUID(uuidString: raw) else { throw SQLiteStorageError.invalidValue }
        return value
    }
}

/// Zero-allocation positional row cursor backed directly by the active `sqlite3_stmt`.
/// Valid only within the `scan`/`query(map:)` closure invocation.
public struct SQLiteCursor {
    private let statement: OpaquePointer
    public let columnCount: Int32

    init(_ statement: OpaquePointer, columnCount: Int32) {
        self.statement = statement
        self.columnCount = columnCount
    }

    public func isNull(_ index: Int32) -> Bool {
        sqlite3_column_type(statement, index) == SQLITE_NULL
    }

    public func text(_ index: Int32) throws -> String {
        guard sqlite3_column_type(statement, index) == SQLITE_TEXT else { throw SQLiteStorageError.invalidValue }
        let byteCount = Int(sqlite3_column_bytes(statement, index))
        if byteCount == 0 { return "" }
        guard let pointer = sqlite3_column_text(statement, index) else { throw SQLiteStorageError.corrupt }
        return String(decoding: UnsafeBufferPointer(start: pointer, count: byteCount), as: UTF8.self)
    }

    public func optionalText(_ index: Int32) throws -> String? {
        isNull(index) ? nil : try text(index)
    }

    public func int64(_ index: Int32) throws -> Int64 {
        guard sqlite3_column_type(statement, index) == SQLITE_INTEGER else { throw SQLiteStorageError.invalidValue }
        return sqlite3_column_int64(statement, index)
    }

    public func optionalInt64(_ index: Int32) throws -> Int64? {
        isNull(index) ? nil : try int64(index)
    }

    public func integer(_ index: Int32) throws -> Int {
        let value = try int64(index)
        guard let result = Int(exactly: value) else { throw SQLiteStorageError.invalidValue }
        return result
    }

    public func optionalInteger(_ index: Int32) throws -> Int? {
        isNull(index) ? nil : try integer(index)
    }

    public func double(_ index: Int32) throws -> Double {
        switch sqlite3_column_type(statement, index) {
        case SQLITE_FLOAT: return sqlite3_column_double(statement, index)
        case SQLITE_INTEGER: return Double(sqlite3_column_int64(statement, index))
        default: throw SQLiteStorageError.invalidValue
        }
    }

    public func optionalDouble(_ index: Int32) throws -> Double? {
        isNull(index) ? nil : try double(index)
    }

    public func bool(_ index: Int32) throws -> Bool {
        let value = try int64(index)
        guard value == 0 || value == 1 else { throw SQLiteStorageError.invalidValue }
        return value == 1
    }

    public func optionalBool(_ index: Int32) throws -> Bool? {
        isNull(index) ? nil : try bool(index)
    }

    public func blob(_ index: Int32) throws -> Data {
        guard sqlite3_column_type(statement, index) == SQLITE_BLOB else { throw SQLiteStorageError.invalidValue }
        let byteCount = Int(sqlite3_column_bytes(statement, index))
        if byteCount == 0 { return Data() }
        guard let pointer = sqlite3_column_blob(statement, index) else { throw SQLiteStorageError.corrupt }
        return Data(bytes: pointer, count: byteCount)
    }

    public func optionalBlob(_ index: Int32) throws -> Data? {
        isNull(index) ? nil : try blob(index)
    }

    public func date(_ index: Int32) throws -> Date {
        Date(timeIntervalSince1970: try double(index))
    }

    public func optionalDate(_ index: Int32) throws -> Date? {
        try optionalDouble(index).map { Date(timeIntervalSince1970: $0) }
    }

    public func uuid(_ index: Int32) throws -> UUID {
        guard let value = UUID(uuidString: try text(index)) else { throw SQLiteStorageError.invalidValue }
        return value
    }

    public func optionalUUID(_ index: Int32) throws -> UUID? {
        guard let raw = try optionalText(index) else { return nil }
        guard let value = UUID(uuidString: raw) else { throw SQLiteStorageError.invalidValue }
        return value
    }

    public func value(_ index: Int32) throws -> SQLiteValue {
        switch sqlite3_column_type(statement, index) {
        case SQLITE_INTEGER: return .integer(sqlite3_column_int64(statement, index))
        case SQLITE_FLOAT: return .real(sqlite3_column_double(statement, index))
        case SQLITE_TEXT: return .text(try text(index))
        case SQLITE_BLOB: return .blob(try blob(index))
        default: return .null
        }
    }
}

/// A connection is borrowed only during an executor closure. Domain repositories
/// own SQL and typed projections; this layer never selects a business namespace.
public final class SQLiteConnection {
    private final class StatementNode {
        let sql: String
        let stmt: OpaquePointer
        var prev: StatementNode?
        var next: StatementNode?
        init(sql: String, stmt: OpaquePointer) { self.sql = sql; self.stmt = stmt }
    }

    let handle: OpaquePointer
    public let isReadOnly: Bool
    private var statementMap: [String: StatementNode] = [:]
    private var head: StatementNode?
    private var tail: StatementNode?
    private static let maximumStatements = 256
    private static let maximumCachedSQLLength = 4096

    init(_ handle: OpaquePointer, isReadOnly: Bool) {
        self.handle = handle
        self.isReadOnly = isReadOnly
    }

    private static func isPragmaSQL(_ sql: String) -> Bool {
        let bytes = Array(sql.utf8)
        var index = 0
        while index < bytes.count && (bytes[index] == 0x20 || bytes[index] == 0x09 || bytes[index] == 0x0A || bytes[index] == 0x0D) {
            index += 1
        }
        let prefix: [UInt8] = [0x70, 0x72, 0x61, 0x67, 0x6d, 0x61] // "pragma"
        guard bytes.count - index >= prefix.count else { return false }
        for offset in 0..<prefix.count {
            let ch = bytes[index + offset]
            let lower = (ch >= 0x41 && ch <= 0x5A) ? (ch + 0x20) : ch
            if lower != prefix[offset] { return false }
        }
        if index + prefix.count == bytes.count { return true }
        let next = bytes[index + prefix.count]
        let isWord = (next >= 0x30 && next <= 0x39) || (next >= 0x41 && next <= 0x5A) ||
            (next >= 0x61 && next <= 0x7A) || next == 0x5F
        return !isWord
    }

    private static func isCacheable(_ sql: String) -> Bool {
        sql.utf8.count <= maximumCachedSQLLength && !isPragmaSQL(sql)
    }

    private func unlink(_ node: StatementNode) {
        if let prev = node.prev { prev.next = node.next } else { head = node.next }
        if let next = node.next { next.prev = node.prev } else { tail = node.prev }
        node.prev = nil
        node.next = nil
    }

    private func insertAtFront(_ node: StatementNode) {
        node.prev = nil
        node.next = head
        if let head { head.prev = node } else { tail = node }
        head = node
    }

    private func prepare(_ sql: String, _ bindings: [SQLiteValue]) throws -> OpaquePointer {
        guard !sql.utf8.contains(0), bindings.allSatisfy({ $0.byteCount <= Int(Int32.max) }) else {
            throw SQLiteStorageError.invalidValue
        }
        let statement: OpaquePointer
        if let node = statementMap.removeValue(forKey: sql) {
            unlink(node)
            statement = node.stmt
        } else {
            var prepared: OpaquePointer?
            var trailing = ""
            let rc: Int32 = sql.withCString { cSQL in
                var remainder: UnsafePointer<CChar>?
                let code = sqlite3_prepare_v2(handle, cSQL, -1, &prepared, &remainder)
                if let remainder { trailing = String(cString: remainder).trimmingCharacters(in: .whitespacesAndNewlines) }
                return code
            }
            guard rc == SQLITE_OK, let prepared else {
                if let prepared { sqlite3_finalize(prepared) }
                throw Self.failure(sqlite3_extended_errcode(handle))
            }
            guard trailing.isEmpty || trailing == ";" else {
                sqlite3_finalize(prepared)
                throw SQLiteStorageError.invalidValue
            }
            statement = prepared
        }
        guard Int(sqlite3_bind_parameter_count(statement)) == bindings.count else {
            sqlite3_finalize(statement); throw SQLiteStorageError.invalidValue
        }
        let transient = unsafeBitCast(-1, to: sqlite3_destructor_type.self)
        for (position, value) in bindings.enumerated() {
            let index = Int32(position + 1), result: Int32
            switch value {
            case .null: result = sqlite3_bind_null(statement, index)
            case .integer(let number): result = sqlite3_bind_int64(statement, index, number)
            case .real(let number):
                guard number.isFinite else { sqlite3_finalize(statement); throw SQLiteStorageError.invalidValue }
                result = sqlite3_bind_double(statement, index, number)
            case .text(let text):
                result = text.withCString { sqlite3_bind_text(statement, index, $0, Int32(text.utf8.count), transient) }
            case .blob(let data):
                result = data.isEmpty ? sqlite3_bind_zeroblob(statement, index, 0) : data.withUnsafeBytes {
                    sqlite3_bind_blob(statement, index, $0.baseAddress, Int32(data.count), transient)
                }
            }
            guard result == SQLITE_OK else {
                sqlite3_finalize(statement); throw Self.failure(result)
            }
        }
        return statement
    }

    private static func shouldRetainStatement(after error: Error?) -> Bool {
        guard let error else { return true }
        if let storageError = error as? SQLiteStorageError {
            switch storageError {
            case .constraint, .conflict, .busy, .invalidValue, .queueFull: return true
            default: return false
            }
        }
        // Caller row-mapping exceptions leave the compiled statement valid.
        return true
    }

    private func recycle(_ statement: OpaquePointer, sql: String, retain: Bool) {
        guard retain, Self.isCacheable(sql) else {
            sqlite3_finalize(statement); return
        }
        // Note: sqlite3_reset returns the last step error code if the previous step failed
        // (e.g. SQLITE_CONSTRAINT), while still successfully resetting the statement for reuse.
        let resetRC = sqlite3_reset(statement) & 0xff
        let resetOk = resetRC == SQLITE_OK || resetRC == SQLITE_CONSTRAINT || resetRC == SQLITE_BUSY || resetRC == SQLITE_LOCKED
        guard resetOk, sqlite3_clear_bindings(statement) == SQLITE_OK else {
            sqlite3_finalize(statement); return
        }
        if let previous = statementMap.removeValue(forKey: sql) {
            unlink(previous)
            sqlite3_finalize(previous.stmt)
        }
        while statementMap.count >= Self.maximumStatements, let oldest = tail {
            unlink(oldest)
            statementMap.removeValue(forKey: oldest.sql)
            sqlite3_finalize(oldest.stmt)
        }
        let node = StatementNode(sql: sql, stmt: statement)
        insertAtFront(node)
        statementMap[sql] = node
    }

    func releaseStatements() {
        var cursor = head
        while let node = cursor {
            cursor = node.next
            node.prev = nil
            node.next = nil
            sqlite3_finalize(node.stmt)
        }
        head = nil
        tail = nil
        statementMap.removeAll()
    }

    deinit { releaseStatements() }

    @discardableResult
    public func execute(_ sql: String, _ bindings: [SQLiteValue] = []) throws -> Int {
        let statement = try prepare(sql, bindings)
        var stepError: Error?
        defer { recycle(statement, sql: sql, retain: Self.shouldRetainStatement(after: stepError)) }
        let result = sqlite3_step(statement)
        guard result == SQLITE_DONE else {
            let err = Self.failure(result)
            stepError = err
            throw err
        }
        return Int(sqlite3_changes(handle))
    }

    public func scan(_ sql: String, _ bindings: [SQLiteValue] = [], maximumRows: Int = .max,
                     consume: (SQLiteCursor) throws -> Void) throws {
        guard maximumRows > 0 else { throw SQLiteStorageError.invalidValue }
        let statement = try prepare(sql, bindings)
        var caughtError: Error?
        defer { recycle(statement, sql: sql, retain: Self.shouldRetainStatement(after: caughtError)) }
        let count = sqlite3_column_count(statement)
        let cursor = SQLiteCursor(statement, columnCount: count)
        var emitted = 0
        do {
            while true {
                let result = sqlite3_step(statement)
                if result == SQLITE_DONE { return }
                guard result == SQLITE_ROW else { throw Self.failure(result) }
                guard emitted < maximumRows else { throw SQLiteStorageError.queueFull }
                emitted += 1
                try consume(cursor)
            }
        } catch {
            caughtError = error
            throw error
        }
    }

    public func query<T>(_ sql: String, _ bindings: [SQLiteValue] = [], maximumRows: Int = .max,
                         map: (SQLiteCursor) throws -> T) throws -> [T] {
        var output: [T] = []
        try scan(sql, bindings, maximumRows: maximumRows) { output.append(try map($0)) }
        return output
    }

    public func queryOne<T>(_ sql: String, _ bindings: [SQLiteValue] = [],
                            map: (SQLiteCursor) throws -> T) throws -> T? {
        var output: T?
        try scan(sql, bindings, maximumRows: 2) { cursor in
            if output != nil { throw SQLiteStorageError.corrupt }
            output = try map(cursor)
        }
        return output
    }

    public func query(_ sql: String, _ bindings: [SQLiteValue] = [], maximumRows: Int = .max) throws -> [SQLiteRow] {
        guard maximumRows > 0 else { throw SQLiteStorageError.invalidValue }
        var output: [SQLiteRow] = []
        try rows(sql, bindings, maximumRows: maximumRows) { output.append($0) }
        return output
    }

    public func rows(_ sql: String, _ bindings: [SQLiteValue] = [], maximumRows: Int = .max,
                     consume: (SQLiteRow) throws -> Void) throws {
        guard maximumRows > 0 else { throw SQLiteStorageError.invalidValue }
        let statement = try prepare(sql, bindings)
        var caughtError: Error?
        defer { recycle(statement, sql: sql, retain: Self.shouldRetainStatement(after: caughtError)) }
        let count = Int(sqlite3_column_count(statement))
        let names = (0..<count).map { String(cString: sqlite3_column_name(statement, Int32($0))) }
        var emitted = 0
        do {
            while true {
                let result = sqlite3_step(statement)
                if result == SQLITE_DONE { return }
                guard result == SQLITE_ROW else { throw Self.failure(result) }
                guard emitted < maximumRows else { throw SQLiteStorageError.queueFull }
                emitted += 1
                var values: [String: SQLiteValue] = [:]
                values.reserveCapacity(count)
                for index in 0..<count {
                    let column = Int32(index), name = names[index]
                    switch sqlite3_column_type(statement, column) {
                    case SQLITE_INTEGER: values[name] = .integer(sqlite3_column_int64(statement, column))
                    case SQLITE_FLOAT: values[name] = .real(sqlite3_column_double(statement, column))
                    case SQLITE_TEXT:
                        let byteCount = Int(sqlite3_column_bytes(statement, column))
                        if byteCount == 0 { values[name] = .text("") }
                        else if let pointer = sqlite3_column_text(statement, column) {
                            let text = String(decoding: UnsafeBufferPointer(start: pointer, count: byteCount), as: UTF8.self)
                            values[name] = .text(text)
                        } else { throw SQLiteStorageError.corrupt }
                    case SQLITE_BLOB:
                        let byteCount = Int(sqlite3_column_bytes(statement, column))
                        if byteCount == 0 { values[name] = .blob(Data()) }
                        else if let pointer = sqlite3_column_blob(statement, column) {
                            values[name] = .blob(Data(bytes: pointer, count: byteCount))
                        } else { throw SQLiteStorageError.corrupt }
                    default: values[name] = .null
                    }
                }
                try consume(SQLiteRow(values: values))
            }
        } catch {
            caughtError = error
            throw error
        }
    }

    public func ownerRevision(_ owner: String) throws -> (revision: Int64, receipt: String) {
        guard let row = try queryOne(
            "SELECT revision, receipt FROM owner_revisions WHERE owner = ?",
            [.text(owner)],
            map: { (try $0.int64(0), try $0.text(1)) }
        ) else {
            return (0, "")
        }
        return row
    }

    @discardableResult
    public func commitOwnerRevision(
        _ owner: String,
        expectedRevision: Int64,
        expectedReceipt: String,
        operationID: String
    ) throws -> (revision: Int64, receipt: String) {
        guard expectedRevision >= 0, expectedRevision < 9_007_199_254_740_991, !operationID.isEmpty else {
            throw SQLiteStorageError.invalidValue
        }
        let current = try ownerRevision(owner)
        guard current.revision == expectedRevision, current.receipt == expectedReceipt else {
            throw SQLiteStorageError.conflict
        }
        let nextRevision = expectedRevision + 1
        try execute("""
            INSERT INTO owner_revisions(owner, revision, receipt) VALUES(?, ?, ?)
            ON CONFLICT(owner) DO UPDATE SET revision = excluded.revision, receipt = excluded.receipt
            """, [.text(owner), .integer(nextRevision), .text(operationID)])
        try execute("INSERT INTO operation_receipts(owner, operation_id, revision) VALUES(?, ?, ?)",
                    [.text(owner), .text(operationID), .integer(nextRevision)])
        try execute("DELETE FROM operation_receipts WHERE owner = ? AND revision < ? - 256",
                    [.text(owner), .integer(nextRevision)])
        return (nextRevision, operationID)
    }

    @discardableResult
    public func bumpOwnerRevision(_ owner: String, operationID: String = UUID().uuidString.lowercased()) throws -> Int64 {
        let current = try ownerRevision(owner)
        let next = try commitOwnerRevision(owner, expectedRevision: current.revision, expectedReceipt: current.receipt, operationID: operationID)
        return next.revision
    }

    /// Table/column identifiers are repository constants, never external input.
    private static func identifier(_ value: String) -> String {
        "\"" + value.replacingOccurrences(of: "\"", with: "\"\"") + "\""
    }
    public static func upsertSQL(_ table: String, columns: [String], key: [String]) -> String {
        let changed = columns.filter { !key.contains($0) }
        let conflict = changed.isEmpty ? "DO NOTHING" : "DO UPDATE SET " +
            changed.map { "\(identifier($0))=excluded.\(identifier($0))" }.joined(separator: ",") + " WHERE " +
            changed.map { "\(identifier(table)).\(identifier($0)) IS NOT excluded.\(identifier($0))" }.joined(separator: " OR ")
        return "INSERT INTO \(identifier(table))(\(columns.map(identifier).joined(separator: ","))) VALUES(" +
            Array(repeating: "?", count: columns.count).joined(separator: ",") +
            ") ON CONFLICT(\(key.map(identifier).joined(separator: ","))) \(conflict)"
    }
    @discardableResult
    public func upsert(_ table: String, columns: [String], values: [SQLiteValue], key: [String]) throws -> Int {
        guard columns.count == values.count, !columns.isEmpty, !key.isEmpty,
              key.allSatisfy(columns.contains) else { throw SQLiteStorageError.invalidValue }
        return try execute(Self.upsertSQL(table, columns: columns, key: key), values)
    }

    /// Diff a domain's typed relation in a transaction. Only changed rows are
    /// written; changing a singleton does not delete/reinsert its children.
    @discardableResult
    public func reconcile(_ table: String, columns: [String], key: [String],
                          rows proposed: [[SQLiteValue]], where predicate: String = "1",
                          bindings: [SQLiteValue] = [], replaceChanged: Bool = false) throws -> Bool {
        let identifier = Self.identifier
        let existing = try query("SELECT \(columns.map(identifier).joined(separator: ",")) FROM \(identifier(table)) WHERE \(predicate)", bindings)
        let keyOffsets = try key.map { name -> Int in
            guard let index = columns.firstIndex(of: name) else { throw SQLiteStorageError.invalidValue }
            return index
        }
        var changed = false
        guard proposed.allSatisfy({ $0.count == columns.count }) else { throw SQLiteStorageError.invalidValue }
        var remaining: [[SQLiteValue]: [SQLiteValue]] = [:]
        var replacements: [[SQLiteValue]] = []
        for row in proposed {
            guard remaining.updateValue(row, forKey: keyOffsets.map { row[$0] }) == nil else {
                throw SQLiteStorageError.constraint
            }
        }
        for previous in existing {
            let values = columns.map { previous[$0] }
            if let row = remaining.removeValue(forKey: keyOffsets.map { values[$0] }) {
                if row != values {
                    if replaceChanged {
                        try execute("DELETE FROM \(identifier(table)) WHERE " +
                            key.map { "\(identifier($0)) IS ?" }.joined(separator: " AND "), keyOffsets.map { values[$0] })
                        replacements.append(row); changed = true
                    } else { changed = try upsert(table, columns: columns, values: row, key: key) > 0 || changed }
                }
            } else {
                changed = try execute("DELETE FROM \(identifier(table)) WHERE " +
                    key.map { "\(identifier($0)) IS ?" }.joined(separator: " AND "), keyOffsets.map { values[$0] }) > 0 || changed
            }
        }
        for row in replacements { try upsert(table, columns: columns, values: row, key: key) }
        for row in remaining.values { changed = try upsert(table, columns: columns, values: row, key: key) > 0 || changed }
        return changed
    }

    public var isInTransaction: Bool { sqlite3_get_autocommit(handle) == 0 }

    static func failure(_ code: Int32) -> SQLiteStorageError {
        switch code & 0xff {
        case SQLITE_BUSY, SQLITE_LOCKED: return .busy
        case SQLITE_CONSTRAINT: return .constraint
        case SQLITE_FULL: return .full
        case SQLITE_READONLY: return .readOnly
        case SQLITE_CORRUPT, SQLITE_NOTADB: return .corrupt
        default: return .io
        }
    }
}

/// The App owns one database coordinator and lends dedicated WAL read/write lanes
/// to native repositories. Writes serialize on the write lane; reads run
/// concurrently on an independent read-only connection (`query_only=ON`).
public final class SQLiteDatabase: @unchecked Sendable {
    public static let applicationID = SQLiteSchema.applicationID
    public static let schemaVersion = SQLiteSchema.version
    public let dataRoot: URL
    public let url: URL
    private let writeQueue = DispatchQueue(label: "meowfilm.storage.sqlite.write", qos: .utility)
    private let readQueue = DispatchQueue(label: "meowfilm.storage.sqlite.read", qos: .utility)
    private let writeQueueKey = DispatchSpecificKey<UInt8>()
    private let readQueueKey = DispatchSpecificKey<UInt8>()
    private let admission = NSLock()
    private var pending = 0, pendingBytes = 0, closing = false
    private var maintenanceFailure: Error?
    private let maximumPending = 128, maximumPendingBytes = 32 * 1024 * 1024
    private var writeConnection: SQLiteConnection?
    private var readConnection: SQLiteConnection?
    private var maintenanceDescriptor: Int32 = -1
    private var databaseDevice: dev_t = 0
    private var databaseInode: ino_t = 0
    private var commits = 0

    public convenience init(dataRoot: URL) throws {
        try self.init(url: dataRoot.appendingPathComponent("storage-v1", isDirectory: true)
            .appendingPathComponent("meowfilm.sqlite3"), dataRoot: dataRoot)
    }

    /// Resolve only an existing, trusted system base before appending owned
    /// descendants. Foundation rewrites /private/var to /var on Apple platforms.
    public static func canonicalSystemBaseURL(_ base: URL) throws -> URL {
        guard base.isFileURL, !base.path.utf8.contains(0),
              let path = realpath(base.path, nil) else { throw SQLiteStorageError.invalidPath }
        defer { free(path) }
        return URL(fileURLWithPath: String(cString: path), isDirectory: true)
    }

    public init(url: URL, dataRoot: URL? = nil) throws {
        guard url.isFileURL, !url.path.utf8.contains(0), url.path != "/" else { throw SQLiteStorageError.invalidPath }
        self.url = url
        self.dataRoot = dataRoot ?? url.deletingLastPathComponent()
        writeQueue.setSpecific(key: writeQueueKey, value: 1)
        readQueue.setSpecific(key: readQueueKey, value: 1)
        guard !Thread.isMainThread else { throw SQLiteStorageError.mainThreadAccess }
        try writeQueue.sync { try open() }
    }

    private func open() throws {
        var opened = false
        defer {
            if !opened, maintenanceDescriptor >= 0 {
                _ = closeFile(maintenanceDescriptor); maintenanceDescriptor = -1
            }
        }
        let definition = try SQLiteSchema.load()
        let directory = url.deletingLastPathComponent()
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true,
                                                attributes: [.posixPermissions: 0o700])
        guard try Self.canonicalSystemBaseURL(directory).path == directory.path else {
            throw SQLiteStorageError.invalidPath
        }
        let lockURL = directory.appendingPathComponent("initialize.lock")
        let lock = lockURL.path.withCString { openFile($0, O_RDWR | O_CREAT | O_NOFOLLOW | O_CLOEXEC, 0o600) }
        guard lock >= 0 else { throw SQLiteStorageError.io }
        defer { _ = flock(lock, LOCK_UN); _ = closeFile(lock) }
        try Self.regular(lock)
        try Self.lock(lock, exclusive: true)
        let maintenanceURL = directory.appendingPathComponent("maintenance.lock")
        maintenanceDescriptor = maintenanceURL.path.withCString { openFile($0, O_RDWR | O_CREAT | O_NOFOLLOW | O_CLOEXEC, 0o600) }
        guard maintenanceDescriptor >= 0 else { throw SQLiteStorageError.io }
        try Self.regular(maintenanceDescriptor)
        try Self.lock(maintenanceDescriptor, exclusive: false)
        defer { _ = flock(maintenanceDescriptor, LOCK_UN) }
        let existing = try Self.regular(url, required: false)
        for suffix in ["-wal", "-shm", "-journal"] {
            let sidecar = try Self.regular(URL(fileURLWithPath: url.path + suffix), required: false)
            guard existing != nil || sidecar == nil else { throw SQLiteStorageError.corrupt }
        }
        guard existing?.st_size != 0 else { throw SQLiteStorageError.corrupt }
        var createdFresh = false
        var committedSchema = false
        func cleanupFailedCreation() {
            guard createdFresh && !committedSchema else { return }
            for suffix in ["", "-wal", "-shm", "-journal"] {
                _ = unlink(url.path + suffix)
            }
        }
        if existing == nil {
            let created = url.path.withCString { openFile($0, O_RDWR | O_CREAT | O_EXCL | O_NOFOLLOW | O_CLOEXEC, 0o600) }
            guard created >= 0 else { throw errno == ENOSPC ? SQLiteStorageError.full : SQLiteStorageError.io }
            createdFresh = true
            guard closeFile(created) == 0 else {
                cleanupFailedCreation()
                throw SQLiteStorageError.io
            }
        }
        var writeHandle: OpaquePointer?
        let openStatus = sqlite3_open_v2(url.path, &writeHandle,
            SQLITE_OPEN_READWRITE | SQLITE_OPEN_FULLMUTEX | SQLITE_OPEN_NOFOLLOW, nil)
        guard openStatus == SQLITE_OK, let writeHandle else {
            if let writeHandle { sqlite3_close_v2(writeHandle) }
            cleanupFailedCreation()
            throw SQLiteConnection.failure(openStatus)
        }
        guard chmod(url.path, 0o600) == 0 else {
            sqlite3_close_v2(writeHandle)
            cleanupFailedCreation()
            throw SQLiteStorageError.io
        }
        let writeDB = SQLiteConnection(writeHandle, isReadOnly: false)
        var readHandle: OpaquePointer?
        var readDB: SQLiteConnection?
        do {
            guard String(cString: sqlite3_libversion()) == SQLiteSchema.expectedSQLiteVersion else {
                throw SQLiteStorageError.invalidSchema
            }
            sqlite3_extended_result_codes(writeHandle, 1)
            sqlite3_busy_timeout(writeHandle, 5_000)
            guard meow_sqlite3_disable_close_checkpoint(writeHandle) == SQLITE_OK else {
                throw SQLiteStorageError.io
            }
            _ = try writeDB.query("PRAGMA foreign_keys=ON")
            _ = try writeDB.query("PRAGMA trusted_schema=OFF")
            let identity = try writeDB.query("PRAGMA application_id").first!.integer("application_id")
            let version = try writeDB.query("PRAGMA user_version").first!.integer("user_version")
            let empty = try writeDB.query("SELECT name FROM sqlite_schema WHERE name NOT LIKE 'sqlite_%'").isEmpty
            if empty {
                guard existing == nil, identity == 0, version == 0 else {
                    throw SQLiteStorageError.invalidSchema
                }
                _ = try writeDB.query("PRAGMA auto_vacuum=INCREMENTAL")
            } else if identity != Self.applicationID || version != Self.schemaVersion {
                throw SQLiteStorageError.invalidSchema
            }
            _ = try writeDB.query("PRAGMA busy_timeout=5000")
            _ = try writeDB.query("PRAGMA trusted_schema=OFF")
            _ = try writeDB.query("PRAGMA foreign_keys=ON")
            guard try writeDB.query("PRAGMA journal_mode=WAL").first?.text("journal_mode") == "wal" else {
                throw SQLiteStorageError.io
            }
            _ = try writeDB.query("PRAGMA synchronous=FULL")
            _ = try writeDB.query("PRAGMA wal_autocheckpoint=0")
            _ = try writeDB.query("PRAGMA temp_store=MEMORY")
            _ = try writeDB.query("PRAGMA cache_size=-16384")
            try Self.verifyBaselinePragmas(on: writeDB, expectQueryOnly: false)
            if empty {
                try Self.transaction(writeDB) { db in
                    guard sqlite3_exec(writeHandle, definition.sql, nil, nil, nil) == SQLITE_OK else {
                        throw SQLiteConnection.failure(sqlite3_extended_errcode(writeHandle))
                    }
                    try db.execute("INSERT INTO storage_identity(singleton,schema_hash) VALUES(1,?)", [.text(definition.hash)])
                    _ = try db.query("PRAGMA application_id=\(Self.applicationID)")
                    _ = try db.query("PRAGMA user_version=\(Self.schemaVersion)")
                }
                committedSchema = true
            }
            try SQLiteSchema.verifyStructure(of: writeDB, against: definition)

            let readStatus = sqlite3_open_v2(url.path, &readHandle,
                SQLITE_OPEN_READONLY | SQLITE_OPEN_FULLMUTEX | SQLITE_OPEN_NOFOLLOW, nil)
            guard readStatus == SQLITE_OK, let openedReadHandle = readHandle else {
                throw SQLiteConnection.failure(readStatus)
            }
            let openedReadDB = SQLiteConnection(openedReadHandle, isReadOnly: true)
            readDB = openedReadDB
            sqlite3_extended_result_codes(openedReadHandle, 1)
            sqlite3_busy_timeout(openedReadHandle, 5_000)
            guard meow_sqlite3_disable_close_checkpoint(openedReadHandle) == SQLITE_OK else {
                throw SQLiteStorageError.io
            }
            _ = try openedReadDB.query("PRAGMA busy_timeout=5000")
            _ = try openedReadDB.query("PRAGMA trusted_schema=OFF")
            _ = try openedReadDB.query("PRAGMA foreign_keys=ON")
            _ = try openedReadDB.query("PRAGMA synchronous=FULL")
            _ = try openedReadDB.query("PRAGMA wal_autocheckpoint=0")
            _ = try openedReadDB.query("PRAGMA temp_store=MEMORY")
            _ = try openedReadDB.query("PRAGMA cache_size=-16384")
            _ = try openedReadDB.query("PRAGMA query_only=ON")
            try Self.verifyBaselinePragmas(on: openedReadDB, expectQueryOnly: true)

            writeDB.releaseStatements()
            openedReadDB.releaseStatements()
            try Self.regular(url, required: true)
            var state = stat()
            guard lstat(url.path, &state) == 0 else { throw SQLiteStorageError.io }
            databaseDevice = state.st_dev; databaseInode = state.st_ino
            writeConnection = writeDB
            readConnection = openedReadDB
            opened = true
        } catch {
            writeDB.releaseStatements()
            readDB?.releaseStatements()
            if let readHandle { sqlite3_close_v2(readHandle) }
            sqlite3_close_v2(writeHandle)
            cleanupFailedCreation()
            throw error
        }
    }

    private static func verifyBaselinePragmas(on db: SQLiteConnection, expectQueryOnly: Bool) throws {
        guard try db.query("PRAGMA journal_mode").first?.text("journal_mode") == "wal",
              try db.query("PRAGMA synchronous").first?.integer("synchronous") == 2,
              try db.query("PRAGMA foreign_keys").first?.integer("foreign_keys") == 1,
              try db.query("PRAGMA trusted_schema").first?.integer("trusted_schema") == 0,
              try db.query("PRAGMA wal_autocheckpoint").first?.integer("wal_autocheckpoint") == 0,
              try db.query("PRAGMA auto_vacuum").first?.integer("auto_vacuum") == 2,
              try db.query("PRAGMA busy_timeout").first?.integer("timeout") == 5_000,
              try db.query("PRAGMA temp_store").first?.integer("temp_store") == 2,
              try db.query("PRAGMA cache_size").first?.integer("cache_size") == -16_384 else {
            throw SQLiteStorageError.invalidSchema
        }
        if expectQueryOnly {
            guard try db.query("PRAGMA query_only").first?.integer("query_only") == 1 else {
                throw SQLiteStorageError.invalidSchema
            }
        }
    }

    private static func regular(_ descriptor: Int32) throws {
        var state = stat()
        guard fstat(descriptor, &state) == 0, state.st_mode & mode_t(S_IFMT) == mode_t(S_IFREG),
              state.st_nlink == 1, state.st_uid == geteuid() else { throw SQLiteStorageError.invalidPath }
    }
    @discardableResult
    private static func regular(_ file: URL, required: Bool) throws -> stat? {
        var state = stat()
        if lstat(file.path, &state) != 0 {
            if errno == ENOENT && !required { return nil }
            throw SQLiteStorageError.io
        }
        guard state.st_mode & mode_t(S_IFMT) == mode_t(S_IFREG),
              state.st_nlink == 1, state.st_uid == geteuid() else { throw SQLiteStorageError.invalidPath }
        return state
    }
    private static func lock(_ descriptor: Int32, exclusive: Bool, wait: Bool = true) throws {
        let deadline = Date().addingTimeInterval(wait ? 5 : 0)
        while flock(descriptor, (exclusive ? LOCK_EX : LOCK_SH) | LOCK_NB) != 0 {
            guard errno == EWOULDBLOCK || errno == EAGAIN || errno == EINTR else { throw SQLiteStorageError.io }
            guard Date() < deadline else { throw SQLiteStorageError.busy }
            Thread.sleep(forTimeInterval: 0.02)
        }
    }
    private func checkFiles() throws {
        guard try Self.canonicalSystemBaseURL(url.deletingLastPathComponent()).path == url.deletingLastPathComponent().path else {
            throw SQLiteStorageError.invalidPath
        }
        try Self.regular(url, required: true)
        var state = stat()
        guard lstat(url.path, &state) == 0, state.st_dev == databaseDevice, state.st_ino == databaseInode else {
            throw SQLiteStorageError.invalidPath
        }
        for suffix in ["-wal", "-shm", "-journal", ""] {
            let file = suffix.isEmpty ? url.deletingLastPathComponent().appendingPathComponent("maintenance.lock") :
                URL(fileURLWithPath: url.path + suffix)
            try Self.regular(file, required: suffix.isEmpty)
            if suffix.isEmpty {
                var descriptorState = stat(), pathState = stat()
                guard fstat(maintenanceDescriptor, &descriptorState) == 0, lstat(file.path, &pathState) == 0,
                      descriptorState.st_dev == pathState.st_dev, descriptorState.st_ino == pathState.st_ino else {
                    throw SQLiteStorageError.invalidPath
                }
            }
        }
    }

    private func admit(_ bytes: Int) throws {
        admission.lock(); defer { admission.unlock() }
        guard !closing else { throw SQLiteStorageError.closed }
        guard bytes >= 0, pending < maximumPending, pending == 0 || bytes <= maximumPendingBytes - pendingBytes else {
            throw SQLiteStorageError.queueFull
        }
        pending += 1; pendingBytes += bytes
    }
    private func release(_ bytes: Int) {
        admission.lock(); pending -= 1; pendingBytes -= bytes; admission.unlock()
    }
    private func currentMaintenanceFailure() -> Error? {
        admission.withLock { maintenanceFailure }
    }
    private func recordMaintenanceFailure(_ error: Error) {
        admission.withLock { if maintenanceFailure == nil { maintenanceFailure = error } }
    }

    private func borrowRead<T>(_ action: (SQLiteConnection) throws -> T) throws -> T {
        guard let connection = readConnection else { throw SQLiteStorageError.closed }
        if let failure = currentMaintenanceFailure() { throw failure }
        let debugEnabled = DebugRuntime.isEnabled
        let started = debugEnabled ? DispatchTime.now().uptimeNanoseconds : 0
        do {
            return try action(connection)
        } catch {
            if debugEnabled {
                let ms = Double(DispatchTime.now().uptimeNanoseconds - started) / 1_000_000.0
                DebugRuntime.logError(.storage, sub: "sqlite", event: "operation.error", error: error,
                                      durationMs: ms, data: .object(["lane": .string("read")]))
            }
            throw error
        }
    }

    private func borrowWrite<T>(exclusive: Bool = false, _ action: (SQLiteConnection) throws -> T) throws -> T {
        guard let connection = writeConnection else { throw SQLiteStorageError.closed }
        if let failure = currentMaintenanceFailure() { throw failure }
        if exclusive {
            try Self.lock(maintenanceDescriptor, exclusive: true)
        }
        defer {
            if exclusive { _ = flock(maintenanceDescriptor, LOCK_UN) }
            scheduleMaintenance()
        }
        if exclusive {
            try checkFiles()
        }
        let changes = sqlite3_total_changes64(connection.handle)
        let debugEnabled = DebugRuntime.isEnabled
        let started = debugEnabled ? DispatchTime.now().uptimeNanoseconds : 0
        do {
            let result = try action(connection)
            let afterChanges = sqlite3_total_changes64(connection.handle)
            if !connection.isInTransaction && afterChanges != changes {
                commits += 1
                if debugEnabled {
                    let ms = Double(DispatchTime.now().uptimeNanoseconds - started) / 1_000_000.0
                    DebugRuntime.log(.storage, sub: "sqlite", event: "write.commit", level: .debug, durationMs: ms, data: .object([
                        "deltaChanges": .integer(afterChanges - changes),
                        "exclusive": .bool(exclusive)
                    ]))
                }
            }
            return result
        } catch {
            if debugEnabled {
                let ms = Double(DispatchTime.now().uptimeNanoseconds - started) / 1_000_000.0
                DebugRuntime.logError(.storage, sub: "sqlite", event: "operation.error", error: error,
                                      durationMs: ms, data: .object([
                    "lane": .string("write"),
                    "exclusive": .bool(exclusive)
                ]))
            }
            throw error
        }
    }

    /// Non-main synchronous reads reuse the active write lane when called
    /// re-entrantly inside a write transaction; otherwise they use the read lane.
    public func readSync<T>(_ action: (SQLiteConnection) throws -> T) throws -> T {
        if DispatchQueue.getSpecific(key: writeQueueKey) != nil {
            if let failure = currentMaintenanceFailure() { throw failure }
            guard let connection = writeConnection else { throw SQLiteStorageError.closed }
            return try action(connection)
        }
        if DispatchQueue.getSpecific(key: readQueueKey) != nil {
            if let failure = currentMaintenanceFailure() { throw failure }
            guard let connection = readConnection else { throw SQLiteStorageError.closed }
            return try action(connection)
        }
        guard !Thread.isMainThread else { throw SQLiteStorageError.mainThreadAccess }
        try admit(0); defer { release(0) }
        return try readQueue.sync { try borrowRead(action) }
    }

    /// Non-main synchronous owners can retain their generation/fence through a
    /// short transaction. Nested calls already inside the write lane reuse it.
    public func writeSync<T>(ownedBytes: Int = 0, _ action: (SQLiteConnection) throws -> T) throws -> T {
        if DispatchQueue.getSpecific(key: readQueueKey) != nil {
            throw SQLiteStorageError.readOnly
        }
        if DispatchQueue.getSpecific(key: writeQueueKey) != nil {
            if let failure = currentMaintenanceFailure() { throw failure }
            guard let connection = writeConnection else { throw SQLiteStorageError.closed }
            return try action(connection)
        }
        guard !Thread.isMainThread else { throw SQLiteStorageError.mainThreadAccess }
        try admit(ownedBytes); defer { release(ownedBytes) }
        return try writeQueue.sync { try borrowWrite(exclusive: false, action) }
    }

    public func read<T: Sendable>(_ action: @escaping @Sendable (SQLiteConnection) throws -> T) async throws -> T {
        try await performRead(action)
    }
    public func snapshot<T: Sendable>(_ action: @escaping @Sendable (SQLiteConnection) throws -> T) async throws -> T {
        try await read { db in try Self.readSnapshot(db, action) }
    }
    public static func readSnapshot<T>(_ db: SQLiteConnection, _ action: (SQLiteConnection) throws -> T) throws -> T {
        guard !db.isInTransaction else { return try action(db) }
        try db.execute("BEGIN DEFERRED")
        do {
            let value = try action(db)
            try db.execute("COMMIT")
            return value
        } catch {
            if db.isInTransaction { _ = try? db.execute("ROLLBACK") }
            throw error
        }
    }
    public func write<T: Sendable>(ownedBytes: Int = 0,
                                  _ action: @escaping @Sendable (SQLiteConnection) throws -> T) async throws -> T {
        try await performWrite(ownedBytes: ownedBytes, exclusive: false, action)
    }

    private func performRead<T: Sendable>(_ action: @escaping @Sendable (SQLiteConnection) throws -> T) async throws -> T {
        try Task.checkCancellation()
        try admit(0)
        return try await withCheckedThrowingContinuation { continuation in
            readQueue.async {
                defer { self.release(0) }
                continuation.resume(with: Result { try self.borrowRead(action) })
            }
        }
    }

    private func performWrite<T: Sendable>(ownedBytes: Int, exclusive: Bool,
                                           _ action: @escaping @Sendable (SQLiteConnection) throws -> T) async throws -> T {
        try Task.checkCancellation()
        try admit(ownedBytes)
        // Once admitted a write settles and returns its actual result even if
        // the awaiting task is cancelled; no continuation drops an accepted commit.
        return try await withCheckedThrowingContinuation { continuation in
            writeQueue.async {
                defer { self.release(ownedBytes) }
                continuation.resume(with: Result { try self.borrowWrite(exclusive: exclusive, action) })
            }
        }
    }

    public func transactionSync<T>(ownedBytes: Int = 0, _ action: (SQLiteConnection) throws -> T) throws -> T {
        try writeSync(ownedBytes: ownedBytes) { db in try Self.transaction(db, action) }
    }
    public func transaction<T: Sendable>(ownedBytes: Int = 0,
                                        _ action: @escaping @Sendable (SQLiteConnection) throws -> T) async throws -> T {
        try await write(ownedBytes: ownedBytes) { db in try Self.transaction(db, action) }
    }
    public static func transaction<T>(_ db: SQLiteConnection,
                                      commit: (_ action: () throws -> Void) throws -> Void = { try $0() },
                                      _ action: (SQLiteConnection) throws -> T) throws -> T {
        guard !db.isInTransaction else { throw SQLiteStorageError.conflict }
        try db.execute("BEGIN IMMEDIATE")
        do {
            let value = try action(db)
            try commit { try db.execute("COMMIT") }
            guard !db.isInTransaction else { throw SQLiteStorageError.conflict }
            return value
        } catch { if db.isInTransaction { _ = try? db.execute("ROLLBACK") }; throw error }
    }

    public func flush() async throws {
        try await write { _ in () }
        try await read { _ in () }
    }
    public func checkpoint() async throws {
        try await performWrite(ownedBytes: 0, exclusive: true) { db in try Self.maintain(db) }
    }
    /// A new, consistent current-format snapshot. Never overwrites a recovery point.
    public func backup(to destination: URL) async throws {
        guard destination.isFileURL, !destination.path.utf8.contains(0),
              ![".", ".."].contains(destination.lastPathComponent),
              try Self.canonicalSystemBaseURL(destination.deletingLastPathComponent()).path ==
                destination.deletingLastPathComponent().path else { throw SQLiteStorageError.invalidPath }
        try await performWrite(ownedBytes: destination.path.utf8.count, exclusive: true) { source in
            let file = destination.path.withCString { openFile($0, O_RDWR | O_CREAT | O_EXCL | O_NOFOLLOW | O_CLOEXEC, 0o600) }
            guard file >= 0 else { throw errno == EEXIST ? SQLiteStorageError.conflict : SQLiteStorageError.io }
            var completed = false
            defer { _ = closeFile(file); if !completed { _ = unlink(destination.path) } }
            var target: OpaquePointer?
            let opened = sqlite3_open_v2(destination.path, &target,
                SQLITE_OPEN_READWRITE | SQLITE_OPEN_FULLMUTEX | SQLITE_OPEN_NOFOLLOW, nil)
            guard opened == SQLITE_OK, let target else {
                if let target { sqlite3_close_v2(target) }
                throw SQLiteConnection.failure(opened)
            }
            let snapshot = SQLiteConnection(target, isReadOnly: false)
            defer {
                snapshot.releaseStatements()
                sqlite3_close_v2(target)
            }
            guard let backup = sqlite3_backup_init(target, "main", source.handle, "main") else {
                throw SQLiteConnection.failure(sqlite3_extended_errcode(target))
            }
            let copied = sqlite3_backup_step(backup, -1)
            let finished = sqlite3_backup_finish(backup)
            guard copied == SQLITE_DONE, finished == SQLITE_OK else {
                throw SQLiteConnection.failure(copied == SQLITE_DONE ? finished : copied)
            }
            guard try snapshot.query("PRAGMA quick_check").first?.text("quick_check") == "ok",
                  try snapshot.query("PRAGMA foreign_key_check").isEmpty else { throw SQLiteStorageError.corrupt }
            guard fsync(file) == 0 else { throw SQLiteStorageError.io }
            let directory = destination.deletingLastPathComponent().path.withCString {
                openFile($0, O_RDONLY | O_DIRECTORY | O_NOFOLLOW | O_CLOEXEC, 0)
            }
            guard directory >= 0 else { throw SQLiteStorageError.io }
            defer { _ = closeFile(directory) }
            guard fsync(directory) == 0 else { throw SQLiteStorageError.io }
            completed = true
        }
    }
    private func scheduleMaintenance() {
        guard commits >= 64, !admission.withLock({ closing }), let db = writeConnection,
              currentMaintenanceFailure() == nil else { return }
        commits = 0
        do {
            try Self.lock(maintenanceDescriptor, exclusive: true, wait: false)
            defer { _ = flock(maintenanceDescriptor, LOCK_UN) }
            try checkFiles()
            try Self.maintain(db)
        } catch SQLiteStorageError.busy { }
        catch let storageError as SQLiteStorageError where storageError == .invalidPath || storageError == .corrupt {
            recordMaintenanceFailure(storageError)
        } catch {
            if DebugRuntime.isEnabled {
                DebugRuntime.logError(.storage, sub: "sqlite", event: "maintenance.warn", error: error)
            }
        }
    }
    private static func maintain(_ db: SQLiteConnection) throws {
        guard !db.isInTransaction else { return }
        let busy = try db.query("PRAGMA wal_checkpoint(PASSIVE)").first?.integer("busy") ?? 0
        guard busy == 0 else { return }
        if try db.query("PRAGMA freelist_count").first!.integer("freelist_count") >= 256 {
            _ = try db.query("PRAGMA incremental_vacuum(128)")
        }
    }
    public func close() async throws {
        admission.withLock { closing = true }
        let readError: Error? = await withCheckedContinuation { continuation in
            readQueue.async {
                var failure: Error?
                if let db = self.readConnection {
                    db.releaseStatements()
                    if sqlite3_close_v2(db.handle) != SQLITE_OK { failure = SQLiteStorageError.io }
                    self.readConnection = nil
                }
                continuation.resume(returning: failure)
            }
        }
        let writeError: Error? = await withCheckedContinuation { continuation in
            writeQueue.async {
                var failure: Error?
                if let db = self.writeConnection {
                    // Accepted transactions have already committed to the WAL.
                    // Release does not contend with the other owner's maintenance.
                    db.releaseStatements()
                    if sqlite3_close_v2(db.handle) != SQLITE_OK { failure = SQLiteStorageError.io }
                    self.writeConnection = nil
                }
                if self.maintenanceDescriptor >= 0 {
                    _ = closeFile(self.maintenanceDescriptor)
                    self.maintenanceDescriptor = -1
                }
                continuation.resume(returning: failure)
            }
        }
        if let failure = writeError ?? readError { throw failure }
    }
    deinit {
        if let readConnection { readConnection.releaseStatements(); sqlite3_close_v2(readConnection.handle) }
        if let writeConnection { writeConnection.releaseStatements(); sqlite3_close_v2(writeConnection.handle) }
        if maintenanceDescriptor >= 0 { _ = closeFile(maintenanceDescriptor) }
    }
}

private func openFile(_ path: UnsafePointer<CChar>, _ flags: Int32, _ mode: mode_t) -> Int32 {
    #if canImport(Darwin)
    return Darwin.open(path, flags, mode)
    #else
    return Glibc.open(path, flags, mode)
    #endif
}
private func closeFile(_ fd: Int32) -> Int32 {
    #if canImport(Darwin)
    return Darwin.close(fd)
    #else
    return Glibc.close(fd)
    #endif
}
