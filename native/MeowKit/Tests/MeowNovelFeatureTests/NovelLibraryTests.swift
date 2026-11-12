import Foundation
import XCTest
import MeowStorage
@testable import MeowNovelFeature

final class NovelLibraryTests: XCTestCase {
    private func store(_ root: URL) throws -> DocumentStore { try DocumentStore(baseURL: root, namespace: NovelLibrary.namespace) }
    func testShelfHistoryAreIndependentAndIdentitySurvivesEnrichmentAndSwitch() async throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: root) }
        let library = NovelLibrary(store: try store(root))
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
        let reopened = NovelLibrary(store: try store(root))
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
        defer { try? FileManager.default.removeItem(at: root) }
        let rpc = try await NovelRulesRPCFixture.start()
        addTeardownBlock { await rpc.close() }
        let library = NovelLibrary(store: try store(root)), rules = rpc.rules
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
        let reopened = NovelLibrary(store: try store(root))
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
    func testCorruptAndExternallyChangedDocumentsAreNeverOverwritten() async throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: root) }
        let store = try store(root), corrupt = Data("broken".utf8)
        try store.write(corrupt, key: "library")
        let library = NovelLibrary(store: store)
        do { _ = try await library.snapshot(); XCTFail("Corrupt JSON must be reported") }
        catch { XCTAssertEqual(error as? NovelError, .corruptStorage) }
        XCTAssertEqual(try store.read(key: "library"), corrupt)
        try store.remove(key: "library")
        _ = try await library.snapshot()
        try store.write(Data("external".utf8), key: "library")
        do { try await library.saveSettings(.init()); XCTFail("CAS must reject an external edit") }
        catch { XCTAssertEqual(error as? DocumentStoreError, .revisionConflict) }
        XCTAssertEqual(try store.read(key: "library"), Data("external".utf8))
        await library.close()
    }
    func testLayoutSettingsAndRulesPersistTogetherAndDefaultRuleBudgetIsReaderCompatible() async throws {
        let imported = try JSONDecoder().decode(NovelRule.self, from: Data(#"{"pattern":"广告","replacement":"","scopeTitle":true}"#.utf8))
        XCTAssertEqual(imported.timeoutMillisecond, 3000)
        XCTAssertTrue(imported.isEnabled); XCTAssertTrue(imported.scopeContent); XCTAssertTrue(imported.isRegex)
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: root) }
        let library = NovelLibrary(store: try store(root))
        var settings = NovelReadingSettings(); settings.mode = .scroll; settings.theme = 7
        settings.fontName = "fixture-font"; settings.foreground = "#CCCCCC"; settings.backgroundImage = Data([1, 2, 3])
        try await library.saveSettings(settings); try await library.saveRules([imported]); await library.close()
        let reopened = NovelLibrary(store: try store(root)), restored = try await reopened.snapshot()
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
    func testSemanticallyCorruptAnchorAndAppearanceAreReportedWithoutChangingBytes() async throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: root) }
        let disk = try store(root), setup = NovelLibrary(store: disk)
        let book = try await setup.retain(.init(installationId: "source", bookUrl: "book", name: "猫"), identity: "known", shelf: true)
        try await setup.record(book, identity: "known", anchor: .init(chapterUrl: "chapter", chapterTitle: "章", chapterIndex: 0))
        await setup.close()
        let valid = try XCTUnwrap(try disk.read(key: "library"))
        for mutation in ["negative", "ratio", "theme", "color", "font", "speed", "conversion"] {
            var object = try XCTUnwrap(JSONSerialization.jsonObject(with: valid) as? [String: Any])
            if mutation == "negative" || mutation == "ratio" {
                var entries = try XCTUnwrap(object["entries"] as? [[String: Any]])
                var anchor = try XCTUnwrap(entries[0]["anchor"] as? [String: Any])
                anchor[mutation == "negative" ? "blockIndex" : "ratio"] = mutation == "negative" ? -1 : 1.2
                entries[0]["anchor"] = anchor; object["entries"] = entries
            } else {
                var settings = try XCTUnwrap(object["settings"] as? [String: Any])
                switch mutation {
                case "theme": settings["theme"] = 8
                case "color": settings["foreground"] = "bad"
                case "font": settings["fontSize"] = -2
                case "speed": settings["automaticSpeed"] = 11
                default: settings["conversion"] = "invented"
                }
                object["settings"] = settings
            }
            let bytes = try JSONSerialization.data(withJSONObject: object)
            try disk.write(bytes, key: "library")
            let library = NovelLibrary(store: disk)
            do { _ = try await library.snapshot(); XCTFail(mutation) }
            catch { XCTAssertEqual(error as? NovelError, .corruptStorage, mutation) }
            do { try await library.saveSettings(.init()); XCTFail("A corrupt document cannot be overwritten") }
            catch { XCTAssertEqual(error as? NovelError, .corruptStorage) }
            XCTAssertEqual(try disk.read(key: "library"), bytes, mutation)
            await library.close()
        }
    }
}
