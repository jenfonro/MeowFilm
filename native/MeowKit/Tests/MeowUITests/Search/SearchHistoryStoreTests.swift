import XCTest
@testable import MeowUI

@MainActor final class SearchHistoryStoreTests: XCTestCase {
    private func isolated(_ body: (UserDefaults) throws -> Void) rethrows {
        let name = "meowfilm.tests.search." + UUID().uuidString
        let defaults = UserDefaults(suiteName: name)!
        defer { defaults.removePersistentDomain(forName: name) }
        try body(defaults)
    }
    func testExactKeywordMovesToFrontCapsAtSixtyAndSurvivesReopen() {
        isolated { defaults in
            let store = SearchHistoryStore(defaults: defaults)
            for index in 0..<65 { store.record("词\(index)") }
            store.record("  词20 \n"); store.record("   ")
            XCTAssertEqual(store.entries.count, 60)
            XCTAssertEqual(store.entries.first?.text, "词20")
            XCTAssertEqual(store.entries.filter { $0.text == "词20" }.count, 1)
            XCTAssertFalse(store.entries.contains { $0.text == "词0" })
            XCTAssertEqual(SearchHistoryStore(defaults: defaults).entries, store.entries)
        }
    }
    func testHistoryDoesNotCaseFoldOrUnicodeFoldAndClearOnlyClearsHistory() {
        isolated { defaults in
            let policy = Data(#"{"songPlatforms":["tx"],"searchPlaylists":false}"#.utf8)
            defaults.set(policy, forKey: "music.search.preferences.v1")
            let store = SearchHistoryStore(defaults: defaults)
            ["Hello", "hello", "\u{e9}", "e\u{301}"].forEach(store.record)
            XCTAssertEqual(store.entries.count, 4)
            XCTAssertEqual(Set(store.entries.map(\.id)).count, 4)
            store.clear()
            XCTAssertTrue(SearchHistoryStore(defaults: defaults).entries.isEmpty)
            XCTAssertEqual(defaults.data(forKey: "music.search.preferences.v1"), policy)
        }
    }
    func testAnotherPageWriteIsRebasedBeforeRecording() {
        isolated { defaults in
            let first = SearchHistoryStore(defaults: defaults), second = SearchHistoryStore(defaults: defaults)
            first.record("首页"); second.record("频道"); first.record("结果")
            XCTAssertEqual(SearchHistoryStore(defaults: defaults).entries.map(\.text), ["结果", "频道", "首页"])
        }
    }
}
