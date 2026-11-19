import Foundation
import XCTest
@testable import MeowStorage

final class SQLiteDatabaseTests: XCTestCase {
    private func fixture() async throws -> (URL, SQLiteDatabase) {
        let root = try SQLiteDatabase.canonicalSystemBaseURL(FileManager.default.temporaryDirectory)
            .appendingPathComponent("sqlite-native-" + UUID().uuidString.lowercased(), isDirectory: true)
        let db = try await Task.detached { try SQLiteDatabase(dataRoot: root) }.value
        addTeardownBlock { try await db.close(); try FileManager.default.removeItem(at: root) }
        return (root, db)
    }
    func testCurrentIdentityEngineAndWalConfiguration() async throws {
        let (_, db) = try await fixture()
        let values = try await db.read { connection in
            [try connection.query("SELECT sqlite_version() AS value").first!.text("value"),
             try connection.query("PRAGMA journal_mode").first!.text("journal_mode"),
             String(try connection.query("PRAGMA synchronous").first!.integer("synchronous")),
             String(try connection.query("PRAGMA application_id").first!.integer("application_id")),
             String(try connection.query("PRAGMA user_version").first!.integer("user_version")),
             String(try connection.query("PRAGMA wal_autocheckpoint").first!.integer("wal_autocheckpoint"))]
        }
        XCTAssertEqual(values, ["3.53.4", "wal", "2", "1296450626", "1", "0"])
    }
    func testTransactionRollbackAndCurrentReopen() async throws {
        let (root, db) = try await fixture()
        try await db.transaction { connection in
            try connection.execute("INSERT INTO owner_revisions VALUES(?,?,?)", [.text("test"), .integer(1), .text("first")])
        }
        do {
            try await db.transaction { connection in
                try connection.execute("UPDATE owner_revisions SET revision=2 WHERE owner='test'")
                try connection.execute("UPDATE owner_revisions SET revision=0 WHERE owner='test'")
            }
            XCTFail("invalid revision committed")
        } catch { XCTAssertEqual(error as? SQLiteStorageError, .constraint) }
        try await db.close()
        let reopened = try await Task.detached { try SQLiteDatabase(dataRoot: root) }.value
        let row = try await reopened.read { try $0.query("SELECT revision,receipt FROM owner_revisions WHERE owner='test'").first! }
        XCTAssertEqual(try row.integer("revision"), 1)
        XCTAssertEqual(try row.text("receipt"), "first")
        try await reopened.close()
    }
    func testIndependentConnectionsSerializeAtomicIncrements() async throws {
        let (root, first) = try await fixture()
        let second = try await Task.detached { try SQLiteDatabase(dataRoot: root) }.value
        try await first.transaction { try $0.execute("INSERT INTO owner_revisions VALUES('test',1,'receipt')") }
        try await withThrowingTaskGroup(of: Void.self) { group in
            for db in [first, second] {
                group.addTask {
                    for _ in 0..<20 { try await db.transaction { try $0.execute("UPDATE owner_revisions SET revision=revision+1 WHERE owner='test'") } }
                }
            }
            try await group.waitForAll()
        }
        let revision = try await first.read { try $0.query("SELECT revision FROM owner_revisions WHERE owner='test'").first!.integer("revision") }
        XCTAssertEqual(revision, 41)
        try await second.close()
    }
    func testModifiedStructureIsRejectedWithoutRebuildingTheDatabase() async throws {
        let (root, db) = try await fixture()
        try await db.transaction { try $0.execute("ALTER TABLE owner_revisions ADD COLUMN unknown TEXT") }
        try await db.close()
        let original = try Data(contentsOf: db.url)
        do {
            _ = try await Task.detached { try SQLiteDatabase(dataRoot: root) }.value
            XCTFail("changed structure admitted")
        } catch { XCTAssertEqual(error as? SQLiteStorageError, .invalidSchema) }
        XCTAssertEqual(try Data(contentsOf: db.url), original)
    }
    func testEmptyBlobAndEmbeddedNulTextKeepTheirTypes() async throws {
        let (_, db) = try await fixture()
        let row = try await db.read { try $0.query("SELECT ? AS bytes,? AS text", [.blob(Data()), .text("a\u{0}b")]).first! }
        XCTAssertEqual(row["bytes"], .blob(Data()))
        XCTAssertEqual(try row.text("text"), "a\u{0}b")
    }
    func testOversizedAdmissionAndClosedOperationsNeverExecuteTheirBodies() async throws {
        let (_, db) = try await fixture()
        do {
            try await db.transaction(ownedBytes: 40 * 1024 * 1024) { _ in XCTFail("oversized body executed") }
            XCTFail("oversized input admitted")
        } catch { XCTAssertEqual(error as? SQLiteStorageError, .queueFull) }
        try await db.close()
        do { try await db.transaction { _ in XCTFail("closed body executed") }; XCTFail("closed input admitted") }
        catch { XCTAssertEqual(error as? SQLiteStorageError, .closed) }
    }
    func testCancelledBeforeAdmissionCannotWrite() async throws {
        let (_, db) = try await fixture()
        let task = Task {
            withUnsafeCurrentTask { $0?.cancel() }
            try await db.transaction { _ in XCTFail("cancelled body executed") }
        }
        do { try await task.value; XCTFail("cancelled operation succeeded") }
        catch { XCTAssertTrue(error is CancellationError) }
    }
    func testLinkedDatabaseIsRejectedWithoutTouchingItsTarget() async throws {
        let (root, db) = try await fixture()
        try await db.close()
        let other = root.appendingPathComponent("target.sqlite3")
        try Data("synthetic outside file".utf8).write(to: other)
        try FileManager.default.removeItem(at: db.url)
        try FileManager.default.createSymbolicLink(at: db.url, withDestinationURL: other)
        do { _ = try await Task.detached { try SQLiteDatabase(dataRoot: root) }.value; XCTFail("link admitted") }
        catch { XCTAssertNotNil(error as? SQLiteStorageError) }
        XCTAssertEqual(try Data(contentsOf: other), Data("synthetic outside file".utf8))
    }
}
