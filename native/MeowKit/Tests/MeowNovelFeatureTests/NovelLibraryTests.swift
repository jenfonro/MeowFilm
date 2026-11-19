import Foundation
import XCTest
import MeowStorage
import MeowStorageTestSupport
import MeowRuntime
@testable import MeowNovelFeature

final class NovelLibraryTests: XCTestCase {
    private var databases: [SQLiteDatabase] = []
    private var roots: Set<URL> = []
    private func store(_ root: URL) throws -> SQLiteDatabase {
        let database = try storageTestOpen(root)
        databases.append(database); roots.insert(root)
        return database
    }
    override func tearDown() async throws {
        for database in databases { try await database.close() }
        for root in roots { try FileManager.default.removeItem(at: root) }
        try await super.tearDown()
    }
    func testBookRuntimeMaterialSurvivesShelfMutationAndReopenExactly() async throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        let material = try RuntimeJSON.decode(Data(#"{"n":0.12345678901234567890123456789,"large":1e400,"é":1,"e\u0301":2}"#.utf8))
        let library = NovelLibrary(database: try store(root))
        let found = NovelBook(installationId: "source", bookUrl: "https://fixture.invalid/book", name: "书", variable: material)
        let retained = try await library.retain(found, identity: "book", shelf: true)
        try await library.pin(retained.id)
        await library.close()
        let reopened = NovelLibrary(database: try store(root))
        let saved = try await reopened.snapshot()
        XCTAssertEqual(saved.entries.first?.book.variable, material)
        await reopened.close()
    }

    func testShelfHistoryAreIndependentAndIdentitySurvivesEnrichmentAndSwitch() async throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        let library = NovelLibrary(database: try store(root))
        let found = NovelBook(installationId: "source-a", bookUrl: "https://fixture.invalid/a", name: "猫的旅行")
        let retained = try await library.retain(found, identity: "unknown", shelf: true)
        var state = try await library.snapshot()
        XCTAssertEqual(state.books(history: false).count, 1)
        XCTAssertTrue(state.books(history: true).isEmpty, "Metadata and shelf admission cannot create reading history")
        var enriched = retained; enriched.author = "林"; enriched.installationId = "source-b"; enriched.bookUrl = "https://fixture.invalid/b"
        let switched = try await library.retain(enriched, identity: "known")
        XCTAssertEqual(switched.id, retained.id)
        let anchor = NovelAnchor(chapterUrl: "https://fixture.invalid/ch1", chapterTitle: "第一章", chapterIndex: 0, blockIndex: 1, utf16Offset: 8, ratio: 0.3)
        try await library.record(switched, identity: "known", anchor: anchor)
        try await library.setShelf(retained.id, present: false)
        state = try await library.snapshot()
        XCTAssertTrue(state.books(history: false).isEmpty)
        XCTAssertEqual(state.books(history: true).map(\.id), [retained.id])
        try await library.setShelf(retained.id, present: true)
        try await library.deleteHistory(retained.id)
        try await library.pin(retained.id)
        await library.close()
        let reopened = NovelLibrary(database: try store(root))
        state = try await reopened.snapshot()
        XCTAssertTrue(state.books(history: true).isEmpty)
        XCTAssertEqual(state.books(history: false).first?.id, retained.id)
        XCTAssertNotNil(state.books(history: false).first?.pinnedAt)
        XCTAssertEqual(state.books(history: false).first?.book.author, "林")
        await reopened.close()
    }
    func testRecordedAuthorEnrichmentPersistsIdentityAndThirdSourceRetainsTheSameLocalID() async throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        let rpc = try await NovelRulesRPCFixture.start()
        addTeardownBlock { await rpc.close() }
        let library = NovelLibrary(database: try store(root)), rules = rpc.rules
        let found = NovelBook(installationId: "source-a", bookUrl: "https://fixture.invalid/a", name: "猫")
        let unknown = try await rules.identity(found)
        let local = try await library.retain(found, identity: unknown, shelf: true)
        try await library.pin(local.id)
        try await library.record(local, identity: unknown, anchor: .init(chapterUrl: "chapter-a", chapterTitle: "第一章", chapterIndex: 0))
        let before = try await library.snapshot(), previous = try XCTUnwrap(before.entries.first)
        var switched = local; switched.author = "林"
        switched.installationId = "source-b"; switched.bookUrl = "https://fixture.invalid/b"
        let known = try await rules.identity(switched)
        XCTAssertNotEqual(known, unknown)
        let anchor = NovelAnchor(chapterUrl: "chapter-b", chapterTitle: "第一章", chapterIndex: 0, utf16Offset: 1, ratio: 0.3)
        try await library.record(switched, identity: known, anchor: anchor)
        let recorded = try await library.snapshot(), entry = try XCTUnwrap(recorded.entries.first)
        XCTAssertEqual(recorded.entries.count, 1)
        XCTAssertEqual(entry.id, local.id); XCTAssertEqual(entry.book, switched)
        XCTAssertEqual(entry.identity, known); XCTAssertEqual(entry.anchor, anchor)
        XCTAssertEqual(entry.onShelf, previous.onShelf); XCTAssertEqual(entry.pinnedAt, previous.pinnedAt)
        XCTAssertEqual(entry.addedAt, previous.addedAt)
        XCTAssertEqual(recorded.books(history: true).map(\.id), [local.id])
        await library.close()
        let reopened = NovelLibrary(database: try store(root))
        let third = NovelBook(installationId: "source-c", bookUrl: "https://fixture.invalid/c", name: "猫", author: "林")
        let thirdIdentity = try await rules.identity(third)
        let retained = try await reopened.retain(third, identity: thirdIdentity)
        XCTAssertEqual(retained.id, local.id, "An enriched author's matching key must survive on disk, not create a second local book")
        let final = try await reopened.snapshot(), matched = try XCTUnwrap(final.entries.first)
        XCTAssertEqual(final.entries.count, 1)
        XCTAssertEqual(matched.identity, known); XCTAssertEqual(matched.anchor, anchor)
        XCTAssertEqual(matched.onShelf, entry.onShelf); XCTAssertEqual(matched.pinnedAt, entry.pinnedAt)
        XCTAssertEqual(matched.addedAt, entry.addedAt); XCTAssertEqual(matched.readAt, entry.readAt)
        await reopened.close(); await rules.close()
    }
    func testReadOnlyFailureAndIndependentOwnersDoNotLoseOtherEntities() async throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        let database = try store(root), library = NovelLibrary(database: database)
        let book = try await library.retain(.init(installationId: "source", bookUrl: "book", name: "猫"),
            identity: "known", shelf: true)
        _ = try await database.read { try $0.execute("PRAGMA query_only=ON") }
        do { try await library.pin(book.id); XCTFail("read-only write succeeded") }
        catch { XCTAssertEqual(error as? SQLiteStorageError, .readOnly) }
        let before = try await library.snapshot()
        XCTAssertNil(before.entries.first?.pinnedAt)
        _ = try await database.read { try $0.execute("PRAGMA query_only=OFF") }
        let other = NovelLibrary(database: try store(root))
        var settings = NovelReadingSettings(); settings.fontSize = 25
        try await other.saveSettings(settings)
        try await library.pin(book.id)
        let saved = try await other.snapshot()
        XCTAssertEqual(saved.settings.fontSize, 25)
        XCTAssertNotNil(saved.entries.first?.pinnedAt)
        await library.close(); await other.close()
    }
    func testLayoutSettingsAndRulesPersistTogetherAndDefaultRuleBudgetIsReaderCompatible() async throws {
        let imported = try JSONDecoder().decode(NovelRule.self, from: Data(#"{"pattern":"广告","replacement":"","scopeTitle":true}"#.utf8))
        XCTAssertEqual(imported.timeoutMillisecond, 3000)
        XCTAssertTrue(imported.isEnabled); XCTAssertTrue(imported.scopeContent); XCTAssertTrue(imported.isRegex)
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        let library = NovelLibrary(database: try store(root))
        var settings = NovelReadingSettings(); settings.mode = .scroll; settings.theme = 7
        settings.fontName = "fixture-font"; settings.foreground = "#CCCCCC"; settings.backgroundImage = Data([1, 2, 3])
        try await library.saveSettings(settings); try await library.saveRules([imported]); await library.close()
        let reopened = NovelLibrary(database: try store(root)), restored = try await reopened.snapshot()
        XCTAssertEqual(restored.settings, settings); XCTAssertEqual(restored.rules, [imported]); await reopened.close()
    }
    func testContentMutationRechecksBlockAndComposedCharacterBeforeRatioFallback() {
        let previous = NovelContent(title: "章", blocks: [.init(text: "广告"), .init(text: "猫👨‍👩‍👧‍👦尾声")])
        var anchor = NovelAnchor(chapterUrl: "c", chapterTitle: "章", chapterIndex: 0, blockIndex: 1, utf16Offset: 3, ratio: 0.5)
        let next = NovelContent(title: "章", blocks: [.init(text: "猫👨‍👩‍👧‍👦尾声")])
        let moved = NovelAnchorResolver.resolve(anchor, previous: previous, next: next)
        XCTAssertEqual(moved?.blockIndex, 0); XCTAssertEqual(moved?.utf16Offset, 1, "Restoration must stay at a complete grapheme")
        anchor.blockIndex = 50
        let fallback = NovelAnchorResolver.resolve(anchor, previous: previous, next: .init(title: "章", blocks: [.init(text: "abcdefghij")]))
        XCTAssertEqual(fallback?.blockIndex, 0); XCTAssertEqual(fallback?.utf16Offset, 5)
    }
    func testSemanticallyCorruptAnchorAndAppearanceAreReportedWithoutAutomaticRepair() async throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        let disk = try store(root), setup = NovelLibrary(database: disk)
        let book = try await setup.retain(.init(installationId: "source", bookUrl: "book", name: "猫"), identity: "known", shelf: true)
        try await setup.record(book, identity: "known", anchor: .init(chapterUrl: "chapter", chapterTitle: "章", chapterIndex: 0))
        try await setup.saveSettings(.init())
        await setup.close()
        let mutations: [(String, String, SQLiteValue)] = [
            ("ios_novel_progress", "blockIndex", .integer(-1)), ("ios_novel_progress", "ratio", .real(1.2)),
            ("ios_novel_reading_settings", "theme", .integer(8)), ("ios_novel_reading_settings", "foreground", .text("bad")),
            ("ios_novel_reading_settings", "fontSize", .real(-2)), ("ios_novel_reading_settings", "automaticSpeed", .real(11)),
            ("ios_novel_reading_settings", "conversion", .text("invented"))
        ]
        for (table, column, invalid) in mutations {
            let select = "SELECT \"\(column)\" AS value FROM \(table)"
            let original = try await disk.read { try $0.query(select).first!["value"] }
            try await disk.transaction { db in
                try db.execute("PRAGMA ignore_check_constraints=ON")
                defer { _ = try? db.execute("PRAGMA ignore_check_constraints=OFF") }
                try db.execute("UPDATE \(table) SET \"\(column)\"=?", [invalid])
            }
            let library = NovelLibrary(database: disk)
            do { _ = try await library.snapshot(); XCTFail(column) }
            catch { XCTAssertEqual(error as? NovelError, .corruptStorage, column) }
            try await library.saveRules([])
            let unchanged = try await disk.read { try $0.query(select).first!["value"] }
            XCTAssertEqual(unchanged, invalid, column)
            await library.close()
            _ = try await disk.transaction { try $0.execute("UPDATE \(table) SET \"\(column)\"=?", [original]) }
        }
    }
}
