import Foundation
import XCTest
import MeowStorage
import MeowRuntime
import MeowFilmFeature
import MeowMusicFeature
import MeowUI

private final class AppStorageWork<Value>: @unchecked Sendable {
    var action: (() throws -> Value)?
    var result: Result<Value, Error>?
    init(_ action: @escaping () throws -> Value) { self.action = action }
    func run() { result = Result(catching: action!); action = nil }
}

func appStorageTestIO<Value>(_ action: @escaping () throws -> Value) throws -> Value {
    if !Thread.isMainThread { return try action() }
    let work = AppStorageWork(action), finished = DispatchSemaphore(value: 0)
    DispatchQueue.global(qos: .utility).async { work.run(); finished.signal() }
    finished.wait()
    return try work.result!.get()
}

/// Synchronous UIKit fixture setup pumps the test run loop; database I/O still
/// runs on the real executor. The product never uses this test-only bridge.
@MainActor
func appStorageTestWait<Value>(_ action: @escaping @MainActor () async throws -> Value) throws -> Value {
    let finished = XCTestExpectation(description: "App storage operation completed")
    var result: Result<Value, Error>?
    Task {
        do { result = .success(try await action()) }
        catch { result = .failure(error) }
        finished.fulfill()
    }
    guard XCTWaiter.wait(for: [finished], timeout: 10) == .completed, let result else {
        throw RuntimeHTTPError.timeout
    }
    return try result.get()
}

@MainActor
func appStorageTestEventually(_ condition: @escaping @MainActor () -> Bool,
    file: StaticString = #filePath, line: UInt = #line) {
    let finished = XCTNSPredicateExpectation(predicate: NSPredicate { _, _ in
        MainActor.assumeIsolated { condition() }
    }, object: nil)
    XCTAssertEqual(XCTWaiter.wait(for: [finished], timeout: 10), .completed, file: file, line: line)
    XCTAssertTrue(condition(), file: file, line: line)
}

func appStorageTestOpen(_ root: URL) throws -> SQLiteDatabase {
    try appStorageTestIO {
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        return try SQLiteDatabase(dataRoot: SQLiteDatabase.canonicalSystemBaseURL(root))
    }
}

extension XCTestCase {
    func appStorageTestDatabase() throws -> SQLiteDatabase {
        let root = FileManager.default.temporaryDirectory
            .appendingPathComponent("app-storage-test-" + UUID().uuidString, isDirectory: true)
        let database = try appStorageTestOpen(root)
        addTeardownBlock { try await database.close(); try FileManager.default.removeItem(at: root) }
        return database
    }
    @MainActor
    func appStorageTestMusicModel(_ owner: MusicViewModel, database: SQLiteDatabase? = nil) throws -> MusicViewModel {
        let database = try database ?? appStorageTestDatabase()
        try appStorageTestWait { try await owner.bindPreferences(database: database) }
        addTeardownBlock { await owner.close() }
        return owner
    }
    @MainActor
    func appStorageTestFilmLibrary(_ database: SQLiteDatabase? = nil) throws -> FilmLibrary {
        let database = try database ?? appStorageTestDatabase(), owner = FilmLibrary()
        try appStorageTestWait { try await owner.bind(database: database) }
        addTeardownBlock { await owner.close() }
        return owner
    }
    @MainActor
    func appStorageTestPlayerPreferences(_ database: SQLiteDatabase? = nil) throws -> FilmPlayerPreferencesStore {
        let database = try database ?? appStorageTestDatabase(), owner = FilmPlayerPreferencesStore(database: database)
        try appStorageTestWait { await owner.reload() }
        addTeardownBlock { await owner.close() }
        return owner
    }
    @MainActor
    func appStorageTestAppearance(_ database: SQLiteDatabase? = nil) throws -> MusicAppearancePreferencesStore {
        let database = try database ?? appStorageTestDatabase(), owner = MusicAppearancePreferencesStore()
        try appStorageTestWait { try await owner.bind(database: database) }
        addTeardownBlock { await owner.close() }
        return owner
    }
    @MainActor
    func appStorageTestSearchPreferences(_ database: SQLiteDatabase, initial: MusicSearchPreferences? = nil) throws -> MusicSearchPreferencesStore {
        let owner = MusicSearchPreferencesStore(database: database)
        try appStorageTestWait { await owner.reload() }
        if let initial { XCTAssertTrue(try appStorageTestWait { await owner.update(initial) }) }
        addTeardownBlock { await owner.close() }
        return owner
    }
    @MainActor
    func appStorageTestHistory(_ database: SQLiteDatabase) throws -> SearchHistoryStore {
        let owner = SearchHistoryStore(database: database)
        try appStorageTestWait { await owner.reload() }
        addTeardownBlock { try await owner.close() }
        return owner
    }
}

extension FilmSettingsDocument {
    func appStorageTestRead() throws -> RuntimeValue? {
        try appStorageTestIO { try self.snapshotSync().document }
    }
    func appStorageTestBytes() throws -> Data? {
        try appStorageTestRead().map(RuntimeJSON.encode)
    }
}

@MainActor
func appStorageTestRows(_ database: SQLiteDatabase, _ sql: String) throws -> [SQLiteRow] {
    try appStorageTestWait { try await database.snapshot { try $0.query(sql) } }
}

@MainActor
func appStorageTestReadOnly(_ database: SQLiteDatabase, _ enabled: Bool) throws {
    _ = try appStorageTestWait { try await database.read { try $0.query(enabled ? "PRAGMA query_only=ON" : "PRAGMA query_only=OFF") } }
}
