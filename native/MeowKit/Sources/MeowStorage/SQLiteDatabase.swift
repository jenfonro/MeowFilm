import Foundation
import MeowSQLite
import CryptoKit
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
    public static func optional(_ value: Date?) -> Self { value.map { .real($0.timeIntervalSince1970) } ?? .null }
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
    public subscript(_ column: String) -> SQLiteValue { values[column] ?? .null }
    public func text(_ column: String) throws -> String {
        guard case .text(let value) = self[column] else { throw SQLiteStorageError.invalidValue }
        return value
    }
    public func optionalText(_ column: String) throws -> String? {
        self[column] == .null ? nil : try text(column)
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
}

/// A connection is borrowed only during an executor closure. Domain repositories
/// own SQL and projections; this layer never selects a business namespace.
public final class SQLiteConnection {
    let handle: OpaquePointer
    fileprivate let maintenanceDescriptor: Int32
    private var statements: [String: OpaquePointer] = [:]
    private var statementOrder: [String] = []
    private let maximumStatements = 128
    fileprivate init(_ handle: OpaquePointer, maintenanceDescriptor: Int32) {
        self.handle = handle; self.maintenanceDescriptor = maintenanceDescriptor
    }

    private func prepare(_ sql: String, _ bindings: [SQLiteValue]) throws -> OpaquePointer {
        guard !sql.utf8.contains(0), bindings.allSatisfy({ $0.byteCount <= Int(Int32.max) }) else {
            throw SQLiteStorageError.invalidValue
        }
        let statement: OpaquePointer
        if let cached = statements.removeValue(forKey: sql) {
            statementOrder.removeAll { $0 == sql }
            statement = cached
        } else {
            var prepared: OpaquePointer?
            guard sqlite3_prepare_v2(handle, sql, -1, &prepared, nil) == SQLITE_OK, let prepared else {
                if let prepared { sqlite3_finalize(prepared) }
                throw Self.failure(sqlite3_extended_errcode(handle))
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

    private func recycle(_ statement: OpaquePointer, sql: String, succeeded: Bool) {
        guard succeeded, ["SELECT ", "INSERT ", "UPDATE ", "DELETE "].contains(where: sql.hasPrefix),
              sqlite3_reset(statement) == SQLITE_OK, sqlite3_clear_bindings(statement) == SQLITE_OK else {
            sqlite3_finalize(statement); return
        }
        if let previous = statements.removeValue(forKey: sql) { sqlite3_finalize(previous); statementOrder.removeAll { $0 == sql } }
        if statements.count == maximumStatements {
            let oldest = statementOrder.removeFirst()
            if let previous = statements.removeValue(forKey: oldest) { sqlite3_finalize(previous) }
        }
        statements[sql] = statement; statementOrder.append(sql)
    }

    fileprivate func releaseStatements() {
        for statement in statements.values { sqlite3_finalize(statement) }
        statements.removeAll(); statementOrder.removeAll()
    }

    deinit { releaseStatements() }

    @discardableResult
    public func execute(_ sql: String, _ bindings: [SQLiteValue] = []) throws -> Int {
        let statement = try prepare(sql, bindings)
        var succeeded = false
        defer { recycle(statement, sql: sql, succeeded: succeeded) }
        let result = sqlite3_step(statement)
        guard result == SQLITE_DONE else { throw Self.failure(result) }
        succeeded = true
        return Int(sqlite3_changes(handle))
    }

    public func query(_ sql: String, _ bindings: [SQLiteValue] = [], maximumRows: Int = .max) throws -> [SQLiteRow] {
        guard maximumRows > 0 else { throw SQLiteStorageError.invalidValue }
        var result: [SQLiteRow] = []
        try rows(sql, bindings) { row in
            guard result.count < maximumRows else { throw SQLiteStorageError.queueFull }
            result.append(row)
        }
        return result
    }

    /// Streaming projections avoid retaining unrelated rows or duplicate DTOs.
    public func rows(_ sql: String, _ bindings: [SQLiteValue] = [], _ consume: (SQLiteRow) throws -> Void) throws {
        let statement = try prepare(sql, bindings)
        var succeeded = false
        defer { recycle(statement, sql: sql, succeeded: succeeded) }
        while true {
            let result = sqlite3_step(statement)
            if result == SQLITE_DONE { succeeded = true; return }
            guard result == SQLITE_ROW else { throw Self.failure(result) }
            var values: [String: SQLiteValue] = [:]
            for column in 0..<sqlite3_column_count(statement) {
                let name = String(cString: sqlite3_column_name(statement, column))
                switch sqlite3_column_type(statement, column) {
                case SQLITE_INTEGER: values[name] = .integer(sqlite3_column_int64(statement, column))
                case SQLITE_FLOAT: values[name] = .real(sqlite3_column_double(statement, column))
                case SQLITE_TEXT:
                    let count = Int(sqlite3_column_bytes(statement, column))
                    guard let pointer = sqlite3_column_text(statement, column),
                          let value = String(data: Data(bytes: pointer, count: count), encoding: .utf8) else {
                        throw SQLiteStorageError.corrupt
                    }
                    values[name] = .text(value)
                case SQLITE_BLOB:
                    let count = Int(sqlite3_column_bytes(statement, column))
                    if count == 0 { values[name] = .blob(Data()) }
                    else if let pointer = sqlite3_column_blob(statement, column) {
                        values[name] = .blob(Data(bytes: pointer, count: count))
                    } else { throw SQLiteStorageError.corrupt }
                default: values[name] = .null
                }
            }
            try consume(SQLiteRow(values: values))
        }
    }

    /// Table/column identifiers are repository constants, never external input.
    private static func identifier(_ value: String) -> String {
        "\"" + value.replacingOccurrences(of: "\"", with: "\"\"") + "\""
    }
    /// Record descriptors compute their fixed SQL once, independent of row values.
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

/// The App owns one executor and lends it to native repositories. SQLite and
/// raw resource I/O run on this dedicated serial queue, not a Swift actor's
/// inherited executor. Admission includes captured bytes, not just task count.
public final class SQLiteDatabase: @unchecked Sendable {
    public static let applicationID = 1_296_450_626
    public static let schemaVersion = 1
    public let dataRoot: URL
    public let url: URL
    private let queue = DispatchQueue(label: "meowfilm.storage.sqlite", qos: .utility)
    private let queueKey = DispatchSpecificKey<UInt8>()
    private let admission = NSLock()
    private var pending = 0, pendingBytes = 0, closing = false
    private let maximumPending = 128, maximumPendingBytes = 32 * 1024 * 1024
    private var connection: SQLiteConnection?
    private var maintenanceDescriptor: Int32 = -1
    private var databaseDevice: dev_t = 0
    private var databaseInode: ino_t = 0
    private var commits = 0
    private var maintenanceFailure: Error?

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

    /// Explicit URL injection is for isolated native repository tests.
    public init(url: URL, dataRoot: URL? = nil) throws {
        guard url.isFileURL, !url.path.utf8.contains(0), url.path != "/" else { throw SQLiteStorageError.invalidPath }
        self.url = url
        self.dataRoot = dataRoot ?? url.deletingLastPathComponent()
        queue.setSpecific(key: queueKey, value: 1)
        guard !Thread.isMainThread else { throw SQLiteStorageError.mainThreadAccess }
        try queue.sync { try open() }
    }

    private func open() throws {
        var opened = false
        defer {
            if !opened, maintenanceDescriptor >= 0 {
                _ = closeFile(maintenanceDescriptor); maintenanceDescriptor = -1
            }
        }
        guard let schemaURL = Bundle.module.url(forResource: "storage-schema", withExtension: "sql"),
              let schemaBytes = try? Data(contentsOf: schemaURL),
              let schema = String(data: schemaBytes, encoding: .utf8), !schema.isEmpty else {
            throw SQLiteStorageError.invalidSchema
        }
        let schemaHash = SHA256.hash(data: schemaBytes).map { String(format: "%02x", $0) }.joined()
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
        maintenanceDescriptor = directory.appendingPathComponent("maintenance.lock").path.withCString {
            openFile($0, O_RDWR | O_CREAT | O_NOFOLLOW | O_CLOEXEC, 0o600)
        }
        guard maintenanceDescriptor >= 0 else { throw SQLiteStorageError.io }
        try Self.regular(maintenanceDescriptor)
        try Self.lock(maintenanceDescriptor, exclusive: false)
        defer { _ = flock(maintenanceDescriptor, LOCK_UN) }
        let existing = try Self.regular(url, required: false)
        for suffix in ["-wal", "-shm", "-journal"] {
            let sidecar = try Self.regular(URL(fileURLWithPath: url.path + suffix), required: false)
            if sidecar != nil && existing == nil { throw SQLiteStorageError.corrupt }
        }
        if existing?.st_size == 0 { throw SQLiteStorageError.corrupt }
        if existing == nil {
            let fd = url.path.withCString { openFile($0, O_RDWR | O_CREAT | O_EXCL | O_NOFOLLOW | O_CLOEXEC, 0o600) }
            guard fd >= 0 else { throw errno == ENOSPC ? SQLiteStorageError.full : SQLiteStorageError.io }
            guard closeFile(fd) == 0 else { throw SQLiteStorageError.io }
        }
        var handle: OpaquePointer?
        let status = sqlite3_open_v2(url.path, &handle,
            SQLITE_OPEN_READWRITE | SQLITE_OPEN_FULLMUTEX | SQLITE_OPEN_NOFOLLOW, nil)
        guard status == SQLITE_OK, let handle else {
            if let handle { sqlite3_close_v2(handle) }
            throw SQLiteConnection.failure(status)
        }
        let db = SQLiteConnection(handle, maintenanceDescriptor: maintenanceDescriptor)
        do {
            guard String(cString: sqlite3_libversion()) == "3.53.4" else {
                throw SQLiteStorageError.invalidSchema
            }
            sqlite3_extended_result_codes(handle, 1)
            sqlite3_busy_timeout(handle, 5_000)
            guard meow_sqlite3_disable_close_checkpoint(handle) == SQLITE_OK else {
                throw SQLiteStorageError.io
            }
            _ = try db.query("PRAGMA foreign_keys=ON")
            _ = try db.query("PRAGMA trusted_schema=OFF")
            let identity = try db.query("PRAGMA application_id").first!.integer("application_id")
            let version = try db.query("PRAGMA user_version").first!.integer("user_version")
            if identity == 0 && version == 0 {
                guard existing == nil,
                      try db.query("SELECT name FROM sqlite_schema WHERE name NOT LIKE 'sqlite_%'").isEmpty else {
                    throw SQLiteStorageError.invalidSchema
                }
                _ = try db.query("PRAGMA auto_vacuum=INCREMENTAL")
                try Self.transaction(db) { db in
                    guard sqlite3_exec(handle, schema, nil, nil, nil) == SQLITE_OK else {
                        throw SQLiteConnection.failure(sqlite3_extended_errcode(handle))
                    }
                    try db.execute("INSERT INTO storage_identity(singleton,schema_hash) VALUES(1,?)", [.text(schemaHash)])
                    _ = try db.query("PRAGMA application_id=\(Self.applicationID)")
                    _ = try db.query("PRAGMA user_version=\(Self.schemaVersion)")
                }
            } else if identity != Self.applicationID || version != Self.schemaVersion {
                throw SQLiteStorageError.invalidSchema
            }
            guard try db.query("PRAGMA journal_mode=WAL").first!.text("journal_mode") == "wal" else {
                throw SQLiteStorageError.io
            }
            _ = try db.query("PRAGMA synchronous=FULL")
            _ = try db.query("PRAGMA wal_autocheckpoint=0")
            guard try db.query("PRAGMA foreign_keys").first!.integer("foreign_keys") == 1,
                  try db.query("PRAGMA synchronous").first!.integer("synchronous") == 2,
                  try db.query("PRAGMA trusted_schema").first!.integer("trusted_schema") == 0,
                  try db.query("PRAGMA wal_autocheckpoint").first!.integer("wal_autocheckpoint") == 0 else {
                throw SQLiteStorageError.invalidSchema
            }
            _ = try db.query("SELECT owner,revision,receipt FROM owner_revisions LIMIT 0")
            guard try db.query("SELECT schema_hash FROM storage_identity WHERE singleton=1").first?.text("schema_hash") == schemaHash else {
                throw SQLiteStorageError.invalidSchema
            }
            var expectedHandle: OpaquePointer?
            guard sqlite3_open_v2(":memory:", &expectedHandle, SQLITE_OPEN_READWRITE | SQLITE_OPEN_CREATE, nil) == SQLITE_OK,
                  let expectedHandle else { throw SQLiteStorageError.io }
            defer { sqlite3_close_v2(expectedHandle) }
            guard sqlite3_exec(expectedHandle, schema, nil, nil, nil) == SQLITE_OK else { throw SQLiteStorageError.invalidSchema }
            let structure = "SELECT type,name,tbl_name,sql FROM sqlite_schema WHERE name NOT LIKE 'sqlite_%' ORDER BY type,name"
            let expected = SQLiteConnection(expectedHandle, maintenanceDescriptor: -1)
            guard try db.query(structure) == expected.query(structure),
                  try db.query("PRAGMA auto_vacuum").first?.integer("auto_vacuum") == 2 else { throw SQLiteStorageError.invalidSchema }
            try Self.regular(url, required: true)
            var state = stat()
            guard lstat(url.path, &state) == 0 else { throw SQLiteStorageError.io }
            databaseDevice = state.st_dev; databaseInode = state.st_ino
            connection = db; opened = true
        } catch {
            sqlite3_close_v2(handle)
            throw error
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
    private func borrow<T>(exclusive: Bool = false, _ action: (SQLiteConnection) throws -> T) throws -> T {
        guard let connection else { throw SQLiteStorageError.closed }
        if let maintenanceFailure { throw maintenanceFailure }
        try checkFiles()
        try Self.lock(maintenanceDescriptor, exclusive: exclusive)
        defer { _ = flock(maintenanceDescriptor, LOCK_UN); scheduleMaintenance() }
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
                    "exclusive": .bool(exclusive)
                ]))
            }
            throw error
        }
    }

    /// Non-main synchronous owners can retain their generation/fence through a
    /// short transaction. Nested calls already inside this queue reuse it.
    public func readSync<T>(_ action: (SQLiteConnection) throws -> T) throws -> T {
        if DispatchQueue.getSpecific(key: queueKey) != nil {
            guard let connection else { throw SQLiteStorageError.closed }
            return try action(connection)
        }
        guard !Thread.isMainThread else { throw SQLiteStorageError.mainThreadAccess }
        try admit(0); defer { release(0) }
        return try queue.sync { try borrow(action) }
    }
    public func writeSync<T>(ownedBytes: Int = 0, _ action: (SQLiteConnection) throws -> T) throws -> T {
        if DispatchQueue.getSpecific(key: queueKey) != nil {
            guard let connection else { throw SQLiteStorageError.closed }
            return try action(connection)
        }
        guard !Thread.isMainThread else { throw SQLiteStorageError.mainThreadAccess }
        try admit(ownedBytes); defer { release(ownedBytes) }
        return try queue.sync { try borrow(action) }
    }
    public func read<T: Sendable>(_ action: @escaping @Sendable (SQLiteConnection) throws -> T) async throws -> T {
        try await perform(ownedBytes: 0, action)
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
            if db.isInTransaction { try? db.execute("ROLLBACK") }
            throw error
        }
    }
    public func write<T: Sendable>(ownedBytes: Int = 0,
                                  _ action: @escaping @Sendable (SQLiteConnection) throws -> T) async throws -> T {
        try await perform(ownedBytes: ownedBytes, action)
    }
    private func perform<T: Sendable>(ownedBytes: Int, exclusive: Bool = false,
                                     _ action: @escaping @Sendable (SQLiteConnection) throws -> T) async throws -> T {
        try Task.checkCancellation()
        try admit(ownedBytes)
        // Once admitted a write settles and returns its actual result even if
        // the awaiting task is cancelled; no continuation drops an accepted commit.
        return try await withCheckedThrowingContinuation { continuation in
            queue.async {
                defer { self.release(ownedBytes) }
                continuation.resume(with: Result { try self.borrow(exclusive: exclusive, action) })
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
        } catch { if db.isInTransaction { try? db.execute("ROLLBACK") }; throw error }
    }

    public func flush() async throws { try await read { _ in () } }
    public func checkpoint() async throws {
        try await perform(ownedBytes: 0, exclusive: true) { db in try Self.maintain(db) }
    }
    /// A new, consistent current-format snapshot. Never overwrites a recovery point.
    public func backup(to destination: URL) async throws {
        guard destination.isFileURL, !destination.path.utf8.contains(0),
              ![".", ".."].contains(destination.lastPathComponent),
              try Self.canonicalSystemBaseURL(destination.deletingLastPathComponent()).path ==
                destination.deletingLastPathComponent().path else { throw SQLiteStorageError.invalidPath }
        try await perform(ownedBytes: destination.path.utf8.count, exclusive: true) { source in
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
            defer { sqlite3_close_v2(target) }
            guard let backup = sqlite3_backup_init(target, "main", source.handle, "main") else {
                throw SQLiteConnection.failure(sqlite3_extended_errcode(target))
            }
            let copied = sqlite3_backup_step(backup, -1)
            let finished = sqlite3_backup_finish(backup)
            guard copied == SQLITE_DONE, finished == SQLITE_OK else {
                throw SQLiteConnection.failure(copied == SQLITE_DONE ? finished : copied)
            }
            let snapshot = SQLiteConnection(target, maintenanceDescriptor: -1)
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
        guard commits >= 64, !admission.withLock({ closing }), let db = connection, maintenanceFailure == nil else { return }
        commits = 0
        do {
            try Self.lock(maintenanceDescriptor, exclusive: true, wait: false)
            defer { _ = flock(maintenanceDescriptor, LOCK_UN) }
            try Self.maintain(db)
        } catch SQLiteStorageError.busy { }
        catch { maintenanceFailure = error }
    }
    private static func maintain(_ db: SQLiteConnection) throws {
        guard !db.isInTransaction else { throw SQLiteStorageError.busy }
        guard try db.query("PRAGMA wal_checkpoint(PASSIVE)").first?.integer("busy") == 0 else {
            throw SQLiteStorageError.busy
        }
        if try db.query("PRAGMA freelist_count").first!.integer("freelist_count") >= 256 {
            _ = try db.query("PRAGMA incremental_vacuum(128)")
        }
    }
    public func close() async throws {
        admission.withLock { closing = true }
        try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<Void, Error>) in
            queue.async {
                continuation.resume(with: Result {
                    if let db = self.connection {
                        // Accepted transactions have already committed to the WAL.
                        // Release does not contend with the other owner's maintenance.
                        var failure: Error?
                        db.releaseStatements()
                        if sqlite3_close_v2(db.handle) != SQLITE_OK { failure = failure ?? SQLiteStorageError.io }
                        self.connection = nil
                        _ = closeFile(self.maintenanceDescriptor); self.maintenanceDescriptor = -1
                        if let failure { throw failure }
                    }
                })
            }
        }
    }
    deinit {
        if let connection { connection.releaseStatements(); sqlite3_close_v2(connection.handle) }
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
