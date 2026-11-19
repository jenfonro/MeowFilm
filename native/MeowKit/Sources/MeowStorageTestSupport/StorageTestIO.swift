import Foundation
import MeowStorage
import XCTest

private final class ResultBox<Value>: @unchecked Sendable {
    var value: Result<Value, Error>?
}

private final class SynchronousWork<Value>: @unchecked Sendable {
    var action: (@Sendable () throws -> Value)?
    var value: Result<Value, Error>?
    init(_ action: @escaping @Sendable () throws -> Value) { self.action = action }
    func run() {
        value = Result(catching: action!)
        action = nil
    }
}

/// Synchronous XCTest setup/assertions still execute actual database work off
/// the main thread. This module is not an App product or a storage substitute.
public func storageTestIO<Value>(_ action: @escaping @Sendable () throws -> Value) throws -> Value {
    if !Thread.isMainThread { return try action() }
    let work = SynchronousWork(action), finished = DispatchSemaphore(value: 0)
    DispatchQueue.global(qos: .utility).async {
        work.run()
        finished.signal()
    }
    finished.wait()
    return try work.value!.get()
}

public func storageTestOpen(_ root: URL) throws -> SQLiteDatabase {
    try storageTestIO {
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        return try SQLiteDatabase(dataRoot: SQLiteDatabase.canonicalSystemBaseURL(root))
    }
}

public func storageTestDatabase(_ test: XCTestCase, root: URL? = nil) throws -> SQLiteDatabase {
    let root = (root ?? FileManager.default.temporaryDirectory
        .appendingPathComponent("storage-test-" + UUID().uuidString, isDirectory: true)).resolvingSymlinksInPath()
    let database = try storageTestOpen(root)
    test.addTeardownBlock {
        try await database.close()
        try FileManager.default.removeItem(at: root)
    }
    return database
}

/// A synchronous fixture can inspect an async, non-UI storage owner.
public func storageTestAsync<Value: Sendable>(
    _ action: @escaping @Sendable () async throws -> Value
) throws -> Value {
    let box = ResultBox<Value>(), finished = DispatchSemaphore(value: 0)
    Task.detached {
        do { box.value = .success(try await action()) }
        catch { box.value = .failure(error) }
        finished.signal()
    }
    finished.wait()
    return try box.value!.get()
}

public func storageTestRows(_ database: SQLiteDatabase, _ sql: String) throws -> [SQLiteRow] {
    try storageTestAsync { try await database.snapshot { try $0.query(sql) } }
}

/// Diagnostic projection only; the actual fixture remains typed SQLite rows.
public func storageTestRowData(_ database: SQLiteDatabase, _ sql: String,
    booleans: Set<String> = []) throws -> Data? {
    let rows = try storageTestRows(database, sql)
    guard !rows.isEmpty else { return nil }
    let values = try rows.map { row in
        try row.values.mapValues { value -> Any in
            switch value {
            case .null: return NSNull()
            case .text(let text): return text
            case .integer(let number): return number
            case .real(let number): return number
            case .blob: throw SQLiteStorageError.invalidValue
            }
        }.merging(try Dictionary(uniqueKeysWithValues: booleans.compactMap { name -> (String, Any)? in
            row[name] == .null ? nil : (name, try row.bool(name) as Any)
        })) { _, new in new }
    }
    return try JSONSerialization.data(withJSONObject: values, options: [.sortedKeys])
}

public func storageTestReadOnly(_ database: SQLiteDatabase, _ value: Bool) throws {
    try storageTestAsync { _ = try await database.read { try $0.query(value ? "PRAGMA query_only=ON" : "PRAGMA query_only=OFF") } }
}

public func storageTestEqual<Value: Equatable>(_ value: Value, _ expected: Value,
    _ message: String = "", file: StaticString = #filePath, line: UInt = #line) {
    XCTAssertEqual(value, expected, message, file: file, line: line)
}
public func storageTestTrue(_ value: Bool, _ message: String = "",
    file: StaticString = #filePath, line: UInt = #line) {
    XCTAssertTrue(value, message, file: file, line: line)
}
public func storageTestFalse(_ value: Bool, _ message: String = "",
    file: StaticString = #filePath, line: UInt = #line) {
    XCTAssertFalse(value, message, file: file, line: line)
}
public func storageTestNil<Value>(_ value: Value?, _ message: String = "",
    file: StaticString = #filePath, line: UInt = #line) {
    XCTAssertNil(value, message, file: file, line: line)
}
public func storageTestNotNil<Value>(_ value: Value?, _ message: String = "",
    file: StaticString = #filePath, line: UInt = #line) {
    XCTAssertNotNil(value, message, file: file, line: line)
}
public func storageTestThrows<Value>(_ action: () async throws -> Value,
    _ check: (Error) -> Void = { _ in }, file: StaticString = #filePath, line: UInt = #line) async {
    do { _ = try await action(); XCTFail("Expected an error", file: file, line: line) }
    catch { check(error) }
}
