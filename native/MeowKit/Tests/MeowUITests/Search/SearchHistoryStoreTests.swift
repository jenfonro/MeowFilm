import Foundation
import XCTest
import MeowStorage
import MeowStorageTestSupport
@testable import MeowUI

@MainActor final class SearchHistoryStoreTests: XCTestCase {
    private func database() throws -> SQLiteDatabase {
        let root = FileManager.default.temporaryDirectory.resolvingSymlinksInPath()
            .appendingPathComponent("search-history-" + UUID().uuidString)
        let database = try storageTestOpen(root)
        addTeardownBlock { try await database.close(); try FileManager.default.removeItem(at: root) }
        return database
    }
    func testExactKeywordMovesToFrontCapsAtSixtyAndSurvivesReopen() async throws {
        let database = try database(), store = SearchHistoryStore(database: database)
        for index in 0..<65 { await store.record("词\(index)") }
        await store.record("  词20 \n"); await store.record("   ")
        XCTAssertEqual(store.entries.count, 60)
        XCTAssertEqual(store.entries.first?.text, "词20")
        XCTAssertEqual(store.entries.filter { $0.text == "词20" }.count, 1)
        XCTAssertFalse(store.entries.contains { $0.text == "词0" })
        let reopened = SearchHistoryStore(database: database)
        await reopened.reload()
        XCTAssertEqual(reopened.entries, store.entries)
        XCTAssertNil(reopened.persistenceError)
    }
    func testHistoryDoesNotCaseFoldOrUnicodeFoldAndClearOnlyClearsHistory() async throws {
        let database = try database(), store = SearchHistoryStore(database: database)
        try await database.transaction { try $0.execute("INSERT INTO owner_revisions VALUES('unrelated',1,'synthetic')") }
        for keyword in ["Hello", "hello", "\u{e9}", "e\u{301}"] { await store.record(keyword) }
        XCTAssertEqual(store.entries.count, 4)
        XCTAssertEqual(Set(store.entries.map(\.id)).count, 4)
        await store.clear()
        let reopened = SearchHistoryStore(database: database)
        await reopened.reload()
        XCTAssertTrue(reopened.entries.isEmpty)
        let row = try await database.read { try $0.query("SELECT revision,receipt FROM owner_revisions WHERE owner='unrelated'").first! }
        XCTAssertEqual(try row.integer("revision"), 1)
        XCTAssertEqual(try row.text("receipt"), "synthetic")
    }
    func testAnotherPageWriteIsRebasedBeforeRecording() async throws {
        let database = try database()
        let first = SearchHistoryStore(database: database), second = SearchHistoryStore(database: database)
        await first.record("首页"); await second.record("频道"); await first.record("结果")
        let reopened = SearchHistoryStore(database: database)
        await reopened.reload()
        XCTAssertEqual(reopened.entries.map(\.text), ["结果", "频道", "首页"])
    }
    func testReadOnlyFailureDoesNotPublishAndCanRetry() async throws {
        let database = try database(), store = SearchHistoryStore(database: database)
        await store.record("已提交")
        try await database.read { try $0.execute("PRAGMA query_only=ON") }
        await store.record("不可提交")
        XCTAssertEqual(store.entries.map(\.text), ["已提交"])
        XCTAssertNotNil(store.persistenceError)
        try await database.read { try $0.execute("PRAGMA query_only=OFF") }
        await store.record("恢复")
        XCTAssertEqual(store.entries.map(\.text), ["恢复", "已提交"])
        XCTAssertNil(store.persistenceError)
    }
}
