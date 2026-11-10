import XCTest
import MeowRuntime
@testable import MeowFilmFeature

@MainActor
final class FilmPlaybackSettingsModelTests: XCTestCase {
    func testFailedSaveKeepsPublishedPreferencesAndEditableDraft() async throws {
        let server = FilmPlaybackWireServer(handler: { operation, _ in
            if operation == "settings.set" { throw FilmPlaybackError(code: "STORAGE_ERROR") }
            return FilmPlaybackPreferences.defaults.value
        })
        let model = FilmPlaybackSettingsModel(transport: server.transport)
        try await model.loadPreferences()
        model.edit { $0.iosAutoPrepare = false; $0.siteCleanKeywords = [] }
        do { try await model.savePreferences(); XCTFail("A failed persistence receipt must be reported") }
        catch { XCTAssertEqual(error as? FilmPlaybackError, .init(code: "STORAGE_ERROR")) }
        XCTAssertEqual(model.preferences, .defaults)
        XCTAssertEqual(model.draft?.iosAutoPrepare, false)
        XCTAssertEqual(model.draft?.siteCleanKeywords, [])
        XCTAssertFalse(model.busy)
    }

    func testSuccessfulReceiptPublishesCompletePreferencesAndEmptyKeywordLists() async throws {
        let server = FilmPlaybackWireServer(handler: { operation, value in
            operation == "settings.set" ? value : FilmPlaybackPreferences.defaults.value
        })
        let model = FilmPlaybackSettingsModel(transport: server.transport)
        try await model.loadPreferences()
        model.edit {
            $0.ruleOrder = [.keyword, .pan, .quality]
            $0.panOrder = [.baidu, .uc, .quark, .tianyi, .yidong]
            $0.keywords = []; $0.siteCleanKeywords = []; $0.iosAutoPrepare = false
        }
        let draft = try XCTUnwrap(model.draft)
        try await model.savePreferences()
        XCTAssertEqual(model.preferences, draft)
        XCTAssertEqual(model.draft, draft)
        let writes = await server.calls().filter { $0.0 == "settings.set" }
        XCTAssertEqual(writes.map(\.1), [draft.value])
    }

    func testRestoreOnlyChangesTheSelectedGroup() async throws {
        let server = FilmPlaybackWireServer(handler: { _, _ in FilmPlaybackPreferences.defaults.value })
        let model = FilmPlaybackSettingsModel(transport: server.transport)
        try await model.loadPreferences()
        model.edit {
            $0.ruleOrder = [.keyword, .pan, .quality]
            $0.panOrder.reverse(); $0.keywords = ["原盘", "字幕"]
            $0.siteCleanKeywords = ["直播"]; $0.iosAutoPrepare = false
        }
        let before = try XCTUnwrap(model.draft)
        model.restoreDraft(.panOrder)
        XCTAssertEqual(model.draft?.panOrder, FilmPlaybackPreferences.defaults.panOrder)
        XCTAssertEqual(model.draft?.ruleOrder, before.ruleOrder)
        XCTAssertEqual(model.draft?.keywords, before.keywords)
        XCTAssertEqual(model.draft?.siteCleanKeywords, before.siteCleanKeywords)
        XCTAssertEqual(model.draft?.iosAutoPrepare, false)
        XCTAssertEqual(model.preferences, .defaults, "Draft restoration is not a persistence receipt")
        let calls = await server.calls()
        XCTAssertEqual(calls.count, 1)
    }

    func testManualSavePreservesNumericZeroAndFailureDoesNotPublishRemoval() async throws {
        var item = FilmManualMatch(targetID: "tmdb:tv:19", title: "星河")
        item.value["sourceKey"] = .string("accurate-source")
        item.value["detailId"] = .number(0)
        item.value["seasonHint"] = .number(0)
        let savedItem = item
        let server = FilmPlaybackWireServer(handler: { operation, value in
            if operation == "manual.update" {
                if value.filmObject?["action"] == .string("remove") {
                    throw FilmPlaybackError(code: "STORAGE_ERROR")
                }
                return .array([try value.filmRequiredObject().filmValue("item")])
            }
            return FilmPlaybackFixture.library(manual: [])
        })
        let model = FilmPlaybackSettingsModel(transport: server.transport)
        try await model.loadLibrary()
        try await model.saveManual(savedItem)
        XCTAssertEqual(model.library?.manual, [savedItem])
        do { try await model.removeManual(id: savedItem.id); XCTFail("Removal must await durable success") }
        catch { XCTAssertEqual(error as? FilmPlaybackError, .init(code: "STORAGE_ERROR")) }
        XCTAssertEqual(model.library?.manual, [savedItem])
        let writes = await server.calls().filter { $0.0 == "manual.update" }
        XCTAssertEqual(writes.first?.1.filmObject?["item"]?.filmObject?["detailId"], .number(0))
        XCTAssertEqual(writes.first?.1.filmObject?["item"]?.filmObject?["seasonHint"], .number(0))
    }

    func testBlockRemovalUsesTheOriginalTypedIdentityAndBulkClearOmitsIdentity() async throws {
        let block = try FilmPlaybackBlock(.object([
            "targetId": .string("source-target"), "sourceKey": .string("source-key"),
            "detailId": .number(0), "lineId": .number(0),
        ]))
        let server = FilmPlaybackWireServer(handler: { _, _ in FilmPlaybackFixture.library() })
        let model = FilmPlaybackSettingsModel(transport: server.transport)
        try await model.removeBlock(block)
        try await model.removeBlock()
        try await model.removeHistory()
        let calls = await server.calls()
        XCTAssertEqual(calls[0].1.filmObject?["block"], .object(block.value))
        XCTAssertEqual(calls[1].1, .object(["kind": .string("blocks")]))
        XCTAssertEqual(calls[2].1, .object(["kind": .string("history")]))
    }

    func testBusySaveIsNotReportedAsSuccessOrSentTwice() async throws {
        let gate = FilmPlaybackGate()
        let server = FilmPlaybackWireServer(handler: { operation, value in
            if operation == "settings.set" { await gate.wait(); return value }
            return FilmPlaybackPreferences.defaults.value
        })
        let model = FilmPlaybackSettingsModel(transport: server.transport)
        try await model.loadPreferences()
        let save = Task { try await model.savePreferences() }
        await server.awaitCalls(2)
        XCTAssertTrue(model.busy)
        do { try await model.savePreferences(); XCTFail("A second save must not fabricate a receipt") }
        catch { XCTAssertEqual(error as? FilmPlaybackError, .init(code: "BUSY")) }
        await gate.release()
        try await save.value
        let writes = await server.calls().filter { $0.0 == "settings.set" }
        XCTAssertEqual(writes.count, 1)
        XCTAssertFalse(model.busy)
    }
}
