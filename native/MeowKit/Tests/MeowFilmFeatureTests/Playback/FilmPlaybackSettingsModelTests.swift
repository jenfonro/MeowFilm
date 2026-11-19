import XCTest
import MeowRuntime
@testable import MeowFilmFeature

@MainActor
final class FilmPlaybackSettingsModelTests: XCTestCase {
    func testPreferenceEditsPreserveOtherValidatedOrdersUntilTheirOrderIsEdited() async throws {
        var fields = try FilmPlaybackPreferences.defaults.value.filmRequiredObject()
        fields["ruleOrder"] = .array([.string("keyword"), .string("quality"), .string("pan")])
        fields["panOrder"] = .array([.string("quark"), .string("baidu"), .string("uc"), .string("yidong"), .string("tianyi")])
        let original = RuntimeValue.object(fields)
        let server = FilmPlaybackWireServer(handler: { _, _ in XCTFail("No preferences RPC"); throw FilmPlaybackError.closed })
        let store = try FilmNativeSettingsStore(self, block: .preferences)
        try await store.seed(store.envelope(original, key: "preferences"))
        let model = FilmPlaybackSettingsModel(transport: server.transport, settingsDocument: store.document)
        try await model.loadPreferences()
        XCTAssertEqual(model.preferences?.ruleOrder, [.keyword, .quality, .pan])
        XCTAssertEqual(model.preferences?.panOrder, [.quark, .baidu, .uc, .yidong, .tianyi])
        XCTAssertEqual(model.preferences?.iosAutoPrepare, true)
        model.edit { $0.keywords = ["新关键词"] }
        try await model.savePreferences()
        XCTAssertEqual(model.preferences?.keywords, ["新关键词"])
        XCTAssertEqual(model.preferences?.value.filmObject?["ruleOrder"], original.filmObject?["ruleOrder"])
        XCTAssertEqual(model.preferences?.value.filmObject?["panOrder"], original.filmObject?["panOrder"])
        var firstSave = fields; firstSave["keywords"] = .array([.string("新关键词")])
        XCTAssertEqual(try store.document.readForTest()?.filmObject?["preferences"], .object(firstSave))
        XCTAssertEqual(store.notifications, ["film.preferences"])
        model.edit { $0.ruleOrder = [.quality, .keyword, .pan] }
        try await model.savePreferences()
        XCTAssertEqual(model.preferences?.value.filmObject?["ruleOrder"],
                       .array([.string("quality"), .string("keyword"), .string("pan")]))
        XCTAssertEqual(model.preferences?.value.filmObject?["panOrder"], original.filmObject?["panOrder"])
        var secondSave = firstSave
        secondSave["ruleOrder"] = .array([.string("quality"), .string("keyword"), .string("pan")])
        XCTAssertEqual(try store.document.readForTest()?.filmObject?["preferences"], .object(secondSave))
        XCTAssertEqual(store.notifications, ["film.preferences", "film.preferences"])
        let reloaded = FilmPlaybackSettingsModel(transport: server.transport, settingsDocument: store.makeDocument())
        try await reloaded.loadPreferences()
        XCTAssertEqual(reloaded.preferences?.value, .object(secondSave))
        let calls = await server.calls()
        XCTAssertTrue(calls.isEmpty)
    }

    func testFailedSaveKeepsPublishedPreferencesAndEditableDraft() async throws {
        let server = FilmPlaybackWireServer(handler: { _, _ in
            XCTFail("Preferences must never request the runtime")
            throw FilmPlaybackError.closed
        })
        let store = try FilmNativeSettingsStore(self, block: .preferences)
        let model = FilmPlaybackSettingsModel(transport: server.transport, settingsDocument: store.document)
        try await model.loadPreferences()
        model.edit { $0.iosAutoPrepare = false; $0.siteCleanKeywords = []; $0.keywords = ["pending"] }
        try store.readOnly(true)
        do { try await model.savePreferences(); XCTFail("A failed persistence receipt must be reported") }
        catch { XCTAssertEqual(error as? FilmSettingsStorageError, .storageFailure) }
        XCTAssertEqual(model.preferences, .defaults)
        XCTAssertEqual(model.draft?.iosAutoPrepare, false)
        XCTAssertEqual(model.draft?.siteCleanKeywords, [])
        XCTAssertFalse(model.busy)
        XCTAssertNil(try store.document.readForTest()); XCTAssertTrue(store.notifications.isEmpty)
        try store.readOnly(false)
        model.edit { $0.keywords = [] }
        try await model.savePreferences()
        XCTAssertEqual(model.preferences, model.draft)
        XCTAssertEqual(store.notifications, ["film.preferences"])
    }

    func testSuccessfulReceiptPublishesCompletePreferencesAndEmptyKeywordLists() async throws {
        let server = FilmPlaybackWireServer(handler: { _, _ in XCTFail("No preferences RPC"); throw FilmPlaybackError.closed })
        let store = try FilmNativeSettingsStore(self, block: .preferences)
        let model = FilmPlaybackSettingsModel(transport: server.transport, settingsDocument: store.document)
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
        let calls = await server.calls()
        XCTAssertTrue(calls.isEmpty)
        XCTAssertEqual(try store.document.readForTest()?.filmObject?["preferences"], draft.value)
        XCTAssertEqual(store.notifications, ["film.preferences"])
        let reopened = FilmPlaybackSettingsModel(transport: server.transport, settingsDocument: store.makeDocument())
        try await reopened.loadPreferences()
        XCTAssertEqual(reopened.preferences, draft)
    }

    func testRestoreOnlyChangesTheSelectedGroup() async throws {
        let server = FilmPlaybackWireServer(handler: { _, _ in XCTFail("No preferences RPC"); throw FilmPlaybackError.closed })
        let model = FilmPlaybackSettingsModel(transport: server.transport, settingsDocument: try FilmNativeSettingsStore(self, block: .preferences).document)
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
        XCTAssertEqual(calls.count, 0)
    }

    func testManualPageUsesHistoryIdentityAndKeepsHealthyRowsDespiteUnrelatedMaterials() async throws {
        let first = FilmManualMatch(targetID: "target-one", title: "第一条")
        let second = FilmManualMatch(targetID: "target-two", title: "第二条")
        let history: RuntimeValue = .object([
            "targetId": .string("history-target"), "title": .string("可选影片"),
            "position": .object(["unused": .bool(true)]), "duration": .string("unused"), "updatedAt": .null,
        ])
        var fields = try FilmPlaybackFixture.library(manual: [.object(first.value), .null, .object(second.value)],
            history: [.bool(false), history, .object(["title": .string("没有身份")])]).filmRequiredObject()
        fields["preferences"] = .string("not consumed by this page")
        fields["blocks"] = .object(["unused": .array([])])
        let receipt = RuntimeValue.object(fields)
        let server = FilmPlaybackWireServer(handler: { _, _ in receipt })
        let model = FilmPlaybackSettingsModel(transport: server.transport, settingsDocument: try FilmNativeSettingsStore(self, block: .preferences).document)
        try await model.loadLibrary()
        XCTAssertEqual(model.library?.area, .manual)
        XCTAssertEqual(model.library?.manual, [first, second])
        XCTAssertEqual(model.library?.history.map(\.targetID), ["history-target"])
        XCTAssertEqual(model.library?.history.map(\.title), ["可选影片"])
        XCTAssertEqual(model.library?.history.first?.value["position"], history.filmObject?["position"])
        XCTAssertNil(model.preferences, "The library read must not publish a preference receipt.")
        XCTAssertFalse(model.busy)
    }

    func testExplicitOrderRestoreReplacesOnlyThatGroupsValidatedValues() async throws {
        var fields = try FilmPlaybackPreferences.defaults.value.filmRequiredObject()
        guard var rules = fields["ruleOrder"]?.filmArray, var providers = fields["panOrder"]?.filmArray else {
            return XCTFail("Fixture")
        }
        rules.reverse(); providers.reverse()
        fields["ruleOrder"] = .array(rules); fields["panOrder"] = .array(providers)
        let original = RuntimeValue.object(fields)
        let server = FilmPlaybackWireServer(handler: { _, _ in XCTFail("No preferences RPC"); throw FilmPlaybackError.closed })
        let store = try FilmNativeSettingsStore(self, block: .preferences)
        try await store.seed(store.envelope(original, key: "preferences"))
        let model = FilmPlaybackSettingsModel(transport: server.transport, settingsDocument: store.document)
        try await model.loadPreferences()
        model.edit { $0.keywords = ["新关键词"] }
        try await model.savePreferences()
        XCTAssertEqual(model.preferences?.value.filmObject?["ruleOrder"], .array(rules))
        XCTAssertEqual(model.preferences?.value.filmObject?["panOrder"], .array(providers))
        model.restoreDraft(.ruleOrder)
        try await model.savePreferences()
        XCTAssertEqual(model.preferences?.value.filmObject?["ruleOrder"],
                       FilmPlaybackPreferences.defaults.value.filmObject?["ruleOrder"])
        XCTAssertEqual(model.preferences?.value.filmObject?["panOrder"], .array(providers))
    }

    func testBlocksPageOnlyConsumesItsTypedBlockRows() async throws {
        let block: RuntimeValue = .object([
            "targetId": .string("target"), "sourceKey": .string("source-key"),
            "detailId": .number(0), "lineId": .string("0"),
        ])
        let receipt: RuntimeValue = .object([
            "preferences": .bool(false), "history": .object(["unused": .number(1)]),
            "manual": .string("unused"),
            "blocks": .array([.null, block, .object(["targetId": .string("missing-identity")])]),
        ])
        let server = FilmPlaybackWireServer(handler: { _, _ in receipt })
        let model = FilmPlaybackSettingsModel(transport: server.transport, settingsDocument: try FilmNativeSettingsStore(self, block: .preferences).document)
        try await model.loadLibrary(area: .blocks)
        XCTAssertEqual(model.library?.area, .blocks)
        let saved = try XCTUnwrap(model.library?.blocks.first)
        XCTAssertEqual(model.library?.blocks.count, 1)
        XCTAssertEqual(saved.value, block.filmObject)
        XCTAssertEqual(saved.detailID, .number(0))
        XCTAssertEqual(saved.lineID, .string("0"))
        XCTAssertFalse(model.busy)
    }

    func testSelectedLibraryListRejectsUnusableContentAndMalformedRootsButAllowsAnActualEmptyList() async throws {
        for (area, key) in [(FilmPlaybackLibrary.Area.manual, "manual"), (.blocks, "blocks")] {
            for value in [RuntimeValue.null, .array([]), .string("not an object"),
                          .object([:]), .object([key: .object([:])]),
                          .object([key: .array([.null, .object(["unrelated": .bool(true)])])])] {
                XCTAssertThrowsError(try FilmPlaybackLibrary(value, area: area)) {
                    XCTAssertEqual($0 as? FilmPlaybackError, .invalidResponse)
                }
            }
            let empty = try FilmPlaybackLibrary(.object([key: .array([])]), area: area)
            XCTAssertEqual(empty.area, area)
            XCTAssertTrue(empty.manual.isEmpty)
            XCTAssertTrue(empty.blocks.isEmpty)
        }
    }

    func testManualMutationReceiptsIsolateBadRowsWithoutPublishingUnusableLists() async throws {
        let first = FilmManualMatch(targetID: "first", title: "第一条")
        let second = FilmManualMatch(targetID: "second", title: "第二条")
        let server = FilmPlaybackWireServer(handler: { operation, value in
            if operation == "library.get" { return FilmPlaybackFixture.library() }
            if value.filmObject?["action"] == .string("remove") {
                return .array([.null, .object(second.value)])
            }
            return .array([.object(first.value), .bool(false)])
        })
        let model = FilmPlaybackSettingsModel(transport: server.transport, settingsDocument: try FilmNativeSettingsStore(self, block: .preferences).document)
        try await model.loadLibrary()
        try await model.saveManual(first)
        XCTAssertEqual(model.library?.manual, [first])
        try await model.removeManual(id: first.id)
        XCTAssertEqual(model.library?.manual, [second])

        let unusable = FilmPlaybackWireServer(handler: { operation, _ in
            operation == "library.get" ? FilmPlaybackFixture.library(manual: [.object(first.value)]) : .array([.null])
        })
        let failed = FilmPlaybackSettingsModel(transport: unusable.transport, settingsDocument: try FilmNativeSettingsStore(self, block: .preferences).document)
        try await failed.loadLibrary()
        let before = failed.library
        do { try await failed.saveManual(second); XCTFail("An unusable receipt must not publish an empty library.") }
        catch { XCTAssertEqual(error as? FilmPlaybackError, .invalidResponse) }
        XCTAssertEqual(failed.library, before)
        XCTAssertFalse(failed.busy)
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
        let model = FilmPlaybackSettingsModel(transport: server.transport, settingsDocument: try FilmNativeSettingsStore(self, block: .preferences).document)
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
        let model = FilmPlaybackSettingsModel(transport: server.transport, settingsDocument: try FilmNativeSettingsStore(self, block: .preferences).document)
        try await model.removeBlock(block)
        try await model.removeBlock()
        let calls = await server.calls()
        XCTAssertEqual(calls.count, 2)
        XCTAssertEqual(calls[0].1.filmObject?["block"], .object(block.value))
        XCTAssertEqual(calls[1].1, .object(["kind": .string("blocks")]))
    }

    func testMetadataCacheScopesOnlyInvalidateTheSelectedProvidersAndKeepSettingsAndLibrary() async throws {
        for (scope, providers) in [(FilmMetadataCacheScope.douban, ["douban"]), (.tmdb, ["tmdb"]), (.both, ["douban", "tmdb"])] {
            let server = FilmPlaybackWireServer(handler: { operation, _ in
                if operation == "library.get" { return FilmPlaybackFixture.library() }
                return .null
            })
            let model = FilmPlaybackSettingsModel(transport: server.transport, settingsDocument: try FilmNativeSettingsStore(self, block: .preferences).document)
            try await model.loadPreferences(); try await model.loadLibrary()
            let library = model.library
            try await model.clearMetadataCache(scope)
            let calls = await server.calls()
            XCTAssertEqual(calls.prefix(1).map(\.0), ["library.get"])
            XCTAssertEqual(calls.dropFirst(1).map(\.0), Array(repeating: "data.invalidate", count: providers.count))
            XCTAssertEqual(Set(calls.dropFirst(1).compactMap { $0.1.filmObject?["domain"]?.filmText }), Set(providers))
            XCTAssertTrue(calls.dropFirst(1).allSatisfy { $0.1.filmObject?.count == 1 })
            XCTAssertEqual(model.preferences, .defaults)
            XCTAssertEqual(model.library, library)
            XCTAssertFalse(model.busy)
        }
    }

    func testFailedCombinedCacheClearStillDrainsTheOtherProviderBeforeEndingBusy() async throws {
        let gate = FilmPlaybackGate()
        let server = FilmPlaybackWireServer(handler: { _, value in
            if value.filmObject?["domain"] == .string("douban") { throw FilmPlaybackError(code: "STORAGE_ERROR") }
            await gate.wait()
            return .null
        })
        let model = FilmPlaybackSettingsModel(transport: server.transport, settingsDocument: try FilmNativeSettingsStore(self, block: .preferences).document)
        let clear = Task { try await model.clearMetadataCache(.both) }
        await server.awaitCalls(2)
        XCTAssertTrue(model.busy)
        do { try await model.clearMetadataCache(.tmdb); XCTFail("No duplicate operation while clearing") }
        catch { XCTAssertEqual(error as? FilmPlaybackError, .init(code: "BUSY")) }
        await gate.release()
        do { try await clear.value; XCTFail("Partial failure must remain a failure") }
        catch { XCTAssertEqual(error as? FilmPlaybackError, .init(code: "STORAGE_ERROR")) }
        XCTAssertFalse(model.busy)
        let calls = await server.calls()
        XCTAssertEqual(calls.count, 2)
    }

    func testBusyOnlineOperationCannotBlockNativePreferencesAndDoesNotReceiveSettingsRPC() async throws {
        let gate = FilmPlaybackGate()
        let server = FilmPlaybackWireServer(handler: { _, _ in
            await gate.wait()
            return FilmPlaybackFixture.library()
        })
        let store = try FilmNativeSettingsStore(self, block: .preferences)
        let model = FilmPlaybackSettingsModel(transport: server.transport, settingsDocument: store.document)
        try await model.loadPreferences()
        model.edit { $0.iosAutoPrepare = false }
        let reading = Task { try await model.loadLibrary() }
        await server.awaitCalls(1)
        XCTAssertTrue(model.busy)
        try await model.loadPreferences()
        try await model.savePreferences()
        XCTAssertTrue(model.busy, "The unrelated online operation is still pending")
        XCTAssertFalse(model.preferencesBusy)
        XCTAssertEqual(model.preferences?.iosAutoPrepare, false)
        XCTAssertEqual(store.notifications, ["film.preferences"])
        await gate.release(); try await reading.value
        XCTAssertEqual(model.preferences?.iosAutoPrepare, false)
        let calls = await server.calls()
        XCTAssertEqual(calls.map(\.0), ["library.get"])
        XCTAssertFalse(model.busy)
    }

    func testMalformedCurrentPreferenceFieldsAreRejectedWithoutWriting() async throws {
        let store = try FilmNativeSettingsStore(self, block: .preferences)
        let raw: RuntimeValue = .object(["ruleOrder": .array([.string("future"), .null]), "panOrder": .number(7),
            "keywords": .object(["future": .bool(true)]), "iosAutoPrepare": .string("not-a-bool")])
        do { try await store.seed(store.envelope(raw, key: "preferences")); XCTFail("invalid current fields admitted") }
        catch { XCTAssertEqual(error as? FilmSettingsStorageError, .invalidDocument) }
        XCTAssertNil(try store.document.readForTest()); XCTAssertTrue(store.notifications.isEmpty)
    }

    func testPreferenceNormalizationRejectsInvalidDraftsAtomicallyBeforeCommit() async throws {
        let store = try FilmNativeSettingsStore(self, block: .preferences)
        let server = FilmPlaybackWireServer(handler: { _, _ in XCTFail("No preferences RPC"); throw FilmPlaybackError.closed })
        let model = FilmPlaybackSettingsModel(transport: server.transport, settingsDocument: store.document)
        try await model.loadPreferences()
        model.edit { $0.ruleOrder = [.quality, .quality, .pan]; $0.keywords = ["valid"] }
        await filmRuleFailure(FilmPlaybackError(code: "INVALID_PREFERENCES")) { try await model.savePreferences() }
        XCTAssertNil(try store.document.readForTest()); XCTAssertTrue(store.notifications.isEmpty)
        model.restoreDraft(.ruleOrder)
        model.edit { $0.keywords = [" Name ", "name"]; $0.iosAutoPrepare = false }
        await filmRuleFailure(FilmPlaybackError(code: "INVALID_PREFERENCES")) { try await model.savePreferences() }
        XCTAssertNil(try store.document.readForTest()); XCTAssertTrue(store.notifications.isEmpty)
        XCTAssertEqual(model.preferences, .defaults)
        XCTAssertEqual(model.draft?.keywords, [" Name ", "name"])
        model.edit { $0.keywords = ["\u{feff}\u{00a0}Name\u{feff}", "\u{0085}keep\u{0085}"]; $0.panOrder = [.quark] }
        try await model.savePreferences()
        XCTAssertEqual(model.preferences?.keywords, ["Name", "\u{0085}keep\u{0085}"])
        XCTAssertEqual(model.preferences?.panOrder, [.quark, .yidong, .tianyi, .uc, .baidu])
        XCTAssertEqual(model.preferences?.iosAutoPrepare, false)
        model.edit { $0.panOrder = []; $0.keywords = []; $0.siteCleanKeywords = [] }
        try await model.savePreferences()
        XCTAssertEqual(model.preferences?.panOrder, FilmPlaybackPreferences.defaults.panOrder)
        XCTAssertEqual(model.preferences?.keywords, []); XCTAssertEqual(model.preferences?.siteCleanKeywords, [])
        XCTAssertEqual(store.notifications, ["film.preferences", "film.preferences"])
    }

    func testPreferenceEditMergesLatestOtherFieldsAndUnchangedUnknownBranch() async throws {
        let store = try FilmNativeSettingsStore(self, block: .preferences)
        let server = FilmPlaybackWireServer(handler: { _, _ in XCTFail("No preferences RPC"); throw FilmPlaybackError.closed })
        let first = FilmPlaybackSettingsModel(transport: server.transport, settingsDocument: store.document)
        let second = FilmPlaybackSettingsModel(transport: server.transport, settingsDocument: store.makeDocument())
        try await first.loadPreferences(); try await second.loadPreferences()
        first.edit { $0.keywords = ["new"] }; try await first.savePreferences()
        second.edit { $0.iosAutoPrepare = false }; try await second.savePreferences()
        XCTAssertEqual(second.preferences?.keywords, ["new"])
        XCTAssertEqual(second.preferences?.iosAutoPrepare, false)
        XCTAssertEqual(store.notifications, ["film.preferences", "film.preferences"])
        second.edit { $0.keywords = ["  new  "] }
        try await second.savePreferences()
        XCTAssertEqual(second.draft?.keywords, ["new"])
        XCTAssertEqual(store.notifications.count, 2, "Normalization equal to current bytes is not a new commit")
    }

    func testCancelledNativePreferenceSaveDoesNotWriteAndCanResumeWithoutRuntime() async throws {
        let store = try FilmNativeSettingsStore(self, block: .preferences)
        let server = FilmPlaybackWireServer(handler: { _, _ in XCTFail("No preferences RPC"); throw FilmPlaybackError.closed })
        let model = FilmPlaybackSettingsModel(transport: server.transport, settingsDocument: store.document)
        try await model.loadPreferences(); model.edit { $0.keywords = ["pending"] }
        let gate = FilmRuleGate()
        let save = Task { await gate.wait(); try await model.savePreferences() }
        save.cancel(); await gate.release()
        await filmRuleCancelled { try await save.value }
        XCTAssertNil(try store.document.readForTest()); XCTAssertTrue(store.notifications.isEmpty)
        XCTAssertEqual(model.draft?.keywords, ["pending"]); XCTAssertFalse(model.busy)
        try await model.savePreferences()
        XCTAssertEqual(model.preferences?.keywords, ["pending"])
        XCTAssertEqual(store.notifications, ["film.preferences"])
    }
}
