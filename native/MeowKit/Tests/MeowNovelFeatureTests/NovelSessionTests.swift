import Foundation
import XCTest
import Combine
import MeowRuntime
import MeowStorage
@testable import MeowNovelFeature

@MainActor
final class NovelSessionTests: XCTestCase {
    private var roots: [URL] = []
    private var ruleFixtures: [NovelRulesRPCFixture] = []
    override func tearDown() async throws {
        for fixture in ruleFixtures { await fixture.close() }; ruleFixtures = []
        for root in roots { try? FileManager.default.removeItem(at: root) }; roots = []
        try await super.tearDown()
    }
    private func fixture(_ runtime: NovelSessionRuntime = .init()) async throws -> (NovelFeature, NovelSessionRuntime, DocumentStore) {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true); roots.append(root)
        let store = try DocumentStore(baseURL: root, namespace: NovelLibrary.namespace)
        let rpc = try await NovelRulesRPCFixture.start(); ruleFixtures.append(rpc)
        let feature = NovelFeature(library: NovelLibrary(store: store), rules: rpc.rules, runtime: runtime)
        try await feature.reloadLocal(); try await feature.refreshSources()
        return (feature, runtime, store)
    }
    private func wait(_ condition: @escaping @MainActor () -> Bool, file: StaticString = #filePath, line: UInt = #line) async {
        let done = expectation(for: NSPredicate { _, _ in MainActor.assumeIsolated { condition() } }, evaluatedWith: nil)
        await fulfillment(of: [done], timeout: 4)
        XCTAssertTrue(condition(), file: file, line: line)
    }
    private func makeReader(_ feature: NovelFeature, using processor: NovelSessionTransform) -> NovelReadingSession {
        feature.makeReader(book: .init(installationId: "a", bookUrl: "book-a", name: "猫")) { content, book, source, rules, conversion in
            try await processor.transform(content, book: book, source: source, rules: rules, conversion: conversion)
        }
    }
    private func replacementRule() -> NovelRule {
        var rule = NovelRule(); rule.pattern = "与"; rule.replacement = "书"; return rule
    }
    func testSearchKeepsPerSourceFailuresPagesAndIsolatesCancelledLateQuery() async throws {
        let (feature, runtime, _) = try await fixture()
        let session = NovelSearchSession(feature: feature)
        await runtime.failSearch("b")
        session.submit("猫"); await wait { !session.loading && !session.books.isEmpty }
        XCTAssertEqual(session.books.count, 1)
        XCTAssertTrue(session.states.contains { if case .failed = $0.status { return $0.source.id == "b" }; return false })
        session.loadMore(); await wait { !session.loading }
        XCTAssertEqual(session.books.count, 2)
        XCTAssertEqual(session.states.first { $0.source.id == "a" }?.page, 2)
        session.loadMore(); await wait { !session.loading }
        XCTAssertEqual(session.states.first { $0.source.id == "a" }?.status, .exhausted)
        await runtime.blockSearch("旧词")
        session.submit("旧词")
        await wait { session.loading }
        // Wait for the physical, cancellation-ignoring fixture call to arrive.
        await runtime.waitForSearch()
        session.submit("新词"); await wait { !session.loading && session.books.first?.book.name == "新词" }
        await runtime.releaseSearch()
        await session.close()
        XCTAssertTrue(session.books.allSatisfy { $0.book.name == "新词" })
        await feature.close()
    }
    func testReaderCarriesBookAndChapterVariablesNextURLAndSavesOnlyDisplayedBody() async throws {
        let (feature, runtime, _) = try await fixture()
        let reader = feature.makeReader(book: .init(installationId: "a", bookUrl: "book-a", name: "猫"))
        reader.start(); await wait { !reader.loading && reader.content != nil }
        XCTAssertTrue(feature.local.books(history: true).isEmpty)
        XCTAssertEqual(reader.book.author, "林")
        XCTAssertEqual(reader.book.variable, .string(#"{"body":"saved"}"#))
        XCTAssertEqual(reader.chapters[0].variable, .string(#"{"body":"saved"}"#))
        let calls = await runtime.calls
        let firstBody = try XCTUnwrap(calls.first { $0["action"] == .string("content") })["payload"]
        XCTAssertEqual(firstBody["nextChapterUrl"], .string("chapter-a-1"))
        XCTAssertEqual(firstBody["book"]["variable"], .string(#"{"toc":"saved"}"#))
        XCTAssertEqual(firstBody["chapter"]["variable"], .string(#"{"chapter":"zero"}"#))
        reader.display(try XCTUnwrap(reader.anchor)); await reader.flush()
        XCTAssertEqual(feature.local.books(history: true).first?.id, reader.book.id)
        let id = reader.book.id
        reader.jump(1, ratio: 0.5); await wait { !reader.loading && reader.chapterIndex == 1 }
        XCTAssertGreaterThan(reader.anchor?.utf16Offset ?? 0, 0)
        reader.display(try XCTUnwrap(reader.anchor)); reader.suspend()
        await reader.close()
        let snapshot = try await feature.library.snapshot()
        XCTAssertEqual(snapshot.books(history: true).first?.id, id)
        XCTAssertEqual(snapshot.books(history: true).first?.anchor?.chapterIndex, 1)
        await feature.close()
    }
    func testSourceSwitchFailureAmbiguityAndCancellationKeepCurrentIdentityAndBody() async throws {
        let (feature, runtime, _) = try await fixture()
        let reader = feature.makeReader(book: .init(installationId: "a", bookUrl: "book-a", name: "猫"))
        reader.start(); await wait { reader.content != nil && !reader.loading }
        let book = reader.book, body = reader.content
        let before = try await feature.library.snapshot()
        await runtime.setSwitchMode(.failure)
        reader.switchSource(.init(installationId: "b", bookUrl: "book-b", name: "猫", author: "林"))
        await wait { !reader.loading && reader.error != nil }
        XCTAssertEqual(reader.book, book); XCTAssertEqual(reader.content, body)
        await runtime.setSwitchMode(.ambiguous); reader.error = nil
        reader.switchSource(.init(installationId: "b", bookUrl: "book-b", name: "猫", author: "林"))
        await wait { reader.sourceSelection != nil && !reader.loading }
        reader.cancelSourceSelection()
        XCTAssertEqual(reader.book, book); XCTAssertEqual(reader.content, body)
        XCTAssertNil(reader.sourceSelection)
        await runtime.setSwitchMode(.unique)
        reader.switchSource(.init(installationId: "b", bookUrl: "book-b", name: "猫", author: "林"))
        await wait { !reader.loading && reader.book.installationId == "b" }
        XCTAssertEqual(reader.book.id, book.id)
        XCTAssertEqual(reader.chapterIndex, 0)
        let prepared = try await feature.library.snapshot()
        XCTAssertEqual(prepared.entries, before.entries)
        XCTAssertTrue(prepared.books(history: true).isEmpty, "Preparing another source is not a displayed reading")
        reader.display(try XCTUnwrap(reader.anchor)); await reader.flush()
        let displayed = try await feature.library.snapshot()
        XCTAssertEqual(displayed.books(history: true).map(\.id), [book.id])
        XCTAssertEqual(displayed.books(history: true).first?.book.installationId, "b")
        await feature.close()
    }
    func testPurchaseRequiresExactObjectAndRefreshesOnlyAfterExplicitConsent() async throws {
        let (feature, runtime, _) = try await fixture()
        let reader = feature.makeReader(book: .init(installationId: "a", bookUrl: "book-a", name: "猫"))
        reader.start(); await wait { reader.content != nil && !reader.loading }
        let chapter = try XCTUnwrap(reader.purchasableChapter)
        reader.display(.init(chapterUrl: chapter.chapterUrl, chapterTitle: chapter.name, chapterIndex: chapter.index,
            blockIndex: 0, utf16Offset: 6, ratio: 0.5))
        let before = await runtime.purchaseCount()
        XCTAssertEqual(before, 0, "Displaying a paywall has no purchase effect")
        reader.confirmPurchase(chapter: chapter, bookID: "not-this-book")
        let afterWrongBook = await runtime.purchaseCount()
        XCTAssertEqual(afterWrongBook, 0)
        reader.confirmPurchase(chapter: chapter, bookID: reader.book.id)
        await wait { !reader.loading && reader.purchasableChapter == nil }
        let afterConsent = await runtime.purchaseCount()
        XCTAssertEqual(afterConsent, 1)
        XCTAssertEqual(reader.content?.blocks.first?.text, "已购正文")
        XCTAssertEqual(reader.anchor?.utf16Offset, 2, "Changed purchased content restores by ratio, not an offset into the old text")
        XCTAssertEqual(reader.anchor?.ratio, 0.5)
        await feature.close()
    }
    func testPurchaseRefreshKeepsContentAndPayContextWithFreshCatalogFlagsAndLatestVariable() async throws {
        let (feature, runtime, _) = try await fixture()
        await runtime.usePurchaseContextFixture()
        let reader = feature.makeReader(book: .init(installationId: "a", bookUrl: "book-a", name: "猫"))
        reader.start(); await wait { reader.content != nil && !reader.loading }
        let chapter = try XCTUnwrap(reader.purchasableChapter)
        let contentContext = RuntimeValue.object(["nested": .array([.string("content"), .null, .number(1)])])
        let payContext = RuntimeValue.object(["nested": .object(["receipt": .string("paid"), "accepted": .bool(true)])])
        let paidVariable = RuntimeValue.object(["stage": .string("paid"), "version": .number(2)])
        XCTAssertEqual(chapter.wire["contentContext"], contentContext)
        reader.suspend()
        let beforeConsent = await runtime.purchaseCount()
        XCTAssertEqual(beforeConsent, 0, "Cancelling reading before consent cannot call payAction")

        reader.confirmPurchase(chapter: chapter, bookID: reader.book.id)
        await wait { !reader.loading && reader.purchasableChapter == nil }
        let calls = await runtime.calls
        let paid = calls.filter { $0["action"] == .string("payAction") }
        XCTAssertEqual(paid.count, 1)
        XCTAssertEqual(paid.first?["payload"]["chapter"]["contentContext"], contentContext)
        XCTAssertEqual(paid.first?["payload"]["chapter"]["variable"], .string(#"{"body":"saved"}"#))
        XCTAssertEqual(calls.filter { $0["action"] == .string("toc") }.count, 2, "Payment must refresh the catalog")
        let bodies = calls.filter { $0["action"] == .string("content") }
        XCTAssertEqual(bodies.count, 2, "Payment must request the body again through the reading session")
        let payload = try XCTUnwrap(bodies.last)["payload"], refreshed = payload["chapter"]
        XCTAssertEqual(refreshed["chapterUrl"], .string(chapter.chapterUrl))
        XCTAssertEqual(refreshed["index"], .number(1), "A reordered catalog must match the original URL, not the previous index")
        XCTAssertEqual(refreshed["name"], .string("第一章 猫（已购目录）"))
        XCTAssertEqual(refreshed["title"], .string("刷新目录规则标题"))
        XCTAssertEqual(refreshed["isPay"], .bool(true), "Fresh catalog paid flags override the payment response")
        XCTAssertEqual(refreshed["time"], .number(123), "Fresh catalog metadata wins when both contexts supply a field")
        XCTAssertEqual(refreshed["contentContext"], contentContext)
        XCTAssertEqual(refreshed["payContext"], payContext)
        XCTAssertEqual(refreshed["variable"], paidVariable)
        XCTAssertEqual(payload["book"]["variable"], paidVariable)
        XCTAssertEqual(payload["variable"], paidVariable)
        XCTAssertEqual(reader.chapterIndex, 1)
        XCTAssertEqual(reader.chapters[1].wire["contentContext"], contentContext)
        XCTAssertEqual(reader.chapters[1].wire["payContext"], payContext)
        XCTAssertEqual(reader.chapters[1].variable, paidVariable)
        XCTAssertEqual(reader.book.variable, paidVariable)
        XCTAssertEqual(reader.content?.blocks.first?.text, "已购正文")
        await feature.close()
    }
    func testSaveFailureIsVisibleAndAcceptedCloseDrainsWithoutOverwritingExternalBytes() async throws {
        let (feature, _, store) = try await fixture()
        let reader = feature.makeReader(book: .init(installationId: "a", bookUrl: "book-a", name: "猫"))
        reader.start(); await wait { reader.content != nil && !reader.loading }
        let external = Data("external".utf8); try store.write(external, key: "library")
        reader.display(try XCTUnwrap(reader.anchor)); await reader.flush()
        XCTAssertTrue(reader.error?.hasPrefix("阅读进度保存失败") == true)
        XCTAssertEqual(try store.read(key: "library"), external)
        async let first: Void = feature.close()
        async let second: Void = feature.close()
        _ = await (first, second)
        XCTAssertTrue(reader.closed)
        XCTAssertEqual(try store.read(key: "library"), external)
    }
    func testCancelledChapterCompletesPhysicalWorkBeforeCloseAndCannotReplaceBody() async throws {
        let (feature, runtime, _) = try await fixture()
        let reader = feature.makeReader(book: .init(installationId: "a", bookUrl: "book-a", name: "猫"))
        reader.start(); await wait { reader.content != nil && !reader.loading }
        let body = reader.content
        await runtime.blockBody()
        reader.jump(1); await runtime.waitForBody()
        reader.suspend()
        let closing = Task { await reader.close() }
        await Task.yield()
        XCTAssertEqual(reader.content, body)
        await runtime.releaseBody(); await closing.value
        XCTAssertNil(reader.content)
        XCTAssertEqual(reader.chapterIndex, 0)
        await feature.close()
    }
    func testStableDisplayedAnchorAndFlushDoNotCreateAnotherAcceptedWrite() async throws {
        let (feature, _, store) = try await fixture()
        let reader = feature.makeReader(book: .init(installationId: "a", bookUrl: "book-a", name: "猫"))
        reader.start(); await wait { reader.content != nil && !reader.loading }
        let anchor = try XCTUnwrap(reader.anchor)
        reader.display(anchor); await reader.flush()
        let bytes = try store.read(key: "library"), readAt = feature.local.books(history: true).first?.readAt
        for _ in 0..<6 { reader.display(anchor); await reader.flush() }
        try await Task.sleep(nanoseconds: 850_000_000)
        XCTAssertEqual(try store.read(key: "library"), bytes)
        XCTAssertEqual(feature.local.books(history: true).first?.readAt, readAt)
        var moved = anchor; moved.utf16Offset = 2; moved.ratio = 0.2
        reader.display(moved); await reader.flush()
        XCTAssertNotEqual(try store.read(key: "library"), bytes)
        await feature.close()
    }
    func testInfoTocContentAndPayPreserveAllRuleVisibleFieldsAndVariableTypes() async throws {
        let (feature, runtime, _) = try await fixture()
        let source = try XCTUnwrap(feature.sources.first), client = feature.sourcesClient
        let book = NovelBook(installationId: source.id, bookUrl: "book-a", name: "猫")
        let detail = try await client.detail(book, source: source)
        let catalog = try await client.chapters(detail, source: source)
        let body = try await client.content(catalog.book, source: source, chapter: catalog.chapters[0], nextChapterURL: catalog.chapters[1].chapterUrl)
        _ = try await client.invoke(source, action: "payAction", payload: client.payload(catalog.book, chapter: body.chapter))
        let calls = await runtime.calls
        XCTAssertEqual(calls.first?["payload"]["book"]["variable"], .null)
        for action in ["toc", "content", "payAction"] {
            let payload = try XCTUnwrap(calls.first { $0["action"] == .string(action) })["payload"]
            XCTAssertEqual(payload["book"]["tocHtml"], .string("<ul>cached catalog</ul>"))
            XCTAssertEqual(payload["book"]["ruleSpecific"], .object(["nested": .array([.null, .number(2), .bool(true)])]))
        }
        for action in ["content", "payAction"] {
            let chapter = try XCTUnwrap(calls.first { $0["action"] == .string(action) })["payload"]["chapter"]
            XCTAssertEqual(chapter["title"], .string("规则标题"))
            XCTAssertEqual(chapter["url"], .string("rule-url"))
            XCTAssertEqual(chapter["bookUrl"], .string("book-a"))
            XCTAssertEqual(chapter["baseUrl"], .string("https://fixture.invalid/base/"))
            XCTAssertEqual(chapter["updateTime"], .string("卷首正文"))
            XCTAssertEqual(chapter["time"], .number(123))
            XCTAssertEqual(chapter["tag"], .array([.string("卷一")]))
        }
        XCTAssertEqual(body.chapter.variable, .string(#"{"body":"saved"}"#))
        XCTAssertEqual(try JSONDecoder().decode(NovelBook.self, from: JSONEncoder().encode(catalog.book)).wire, catalog.book.wire)
        XCTAssertEqual(try JSONDecoder().decode(NovelChapter.self, from: JSONEncoder().encode(body.chapter)).wire, body.chapter.wire)
        await feature.close()
    }
    func testSameQueryUsesNewSourceRevisionAndHiddenSearchWaitsForExplicitReturn() async throws {
        let (feature, runtime, _) = try await fixture()
        let search = NovelSearchSession(feature: feature)
        search.submit("猫"); await wait { !search.loading && !search.books.isEmpty }
        await runtime.setRevision(2); try await feature.refreshSources()
        await wait { !search.loading && search.states.allSatisfy { $0.source.revision == 2 && $0.page == 1 } }
        let activeCalls = await runtime.calls
        XCTAssertTrue(activeCalls.contains { $0["action"] == .string("search") && $0["revision"] == .number(2) })
        search.cancel()
        let count = activeCalls.count
        await runtime.setRevision(3); try await feature.refreshSources()
        await Task.yield()
        let hiddenCalls = await runtime.calls
        XCTAssertEqual(hiddenCalls.count, count, "Source updates cannot start a hidden domain")
        XCTAssertTrue(search.books.isEmpty)
        search.submit("猫"); search.loadMore()
        await wait { !search.loading && search.states.allSatisfy { $0.source.revision == 3 && $0.page == 1 } }
        await search.close(); await feature.close()
    }
    func testShortenedCatalogMatchesUniqueNameAndAmbiguityRequiresExplicitChapter() async throws {
        let (feature, runtime, _) = try await fixture()
        let reader = feature.makeReader(book: .init(installationId: "a", bookUrl: "book-a", name: "猫"))
        reader.start(); await wait { reader.content != nil && !reader.loading }
        reader.jump(1); await wait { !reader.loading && reader.chapterIndex == 1 }
        await runtime.setCatalog([.init(name: "第2章：夜", chapterUrl: "moved-night", index: 0)])
        reader.refreshCatalog(); await wait { !reader.loading && reader.anchor?.chapterUrl == "moved-night" }
        XCTAssertEqual(reader.chapterIndex, 0)
        let before = reader.content, book = reader.book
        await runtime.setCatalog([.init(name: "第二章 夜", chapterUrl: "night-a", index: 0), .init(name: "第2章：夜", chapterUrl: "night-b", index: 1)])
        reader.refreshCatalog(); await wait { !reader.loading && reader.sourceSelection != nil }
        XCTAssertEqual(reader.content, before); XCTAssertEqual(reader.book, book)
        reader.cancelSourceSelection()
        XCTAssertEqual(reader.anchor?.chapterUrl, "moved-night")
        reader.refreshCatalog(); await wait { !reader.loading && reader.sourceSelection != nil }
        reader.selectSourceChapter(1); await wait { !reader.loading && reader.anchor?.chapterUrl == "night-b" }
        XCTAssertEqual(reader.book.id, book.id)
        await feature.close()
    }
    func testUnknownAuthorRequiresConfirmationAndKnownDifferentAuthorCannotReplaceCurrentBook() async throws {
        let (feature, runtime, _) = try await fixture()
        let reader = feature.makeReader(book: .init(installationId: "a", bookUrl: "book-a", name: "猫"))
        reader.start(); await wait { reader.content != nil && !reader.loading }
        let before = reader.book, body = reader.content
        await runtime.setAuthor("周", source: "b")
        reader.switchSource(.init(installationId: "b", bookUrl: "book-b", name: "猫"))
        await wait { !reader.loading && reader.error != nil }
        XCTAssertEqual(reader.error, NovelError.differentBook.localizedDescription)
        XCTAssertEqual(reader.book, before); XCTAssertEqual(reader.content, body)
        await runtime.setAuthor("", source: "b"); reader.error = nil
        reader.switchSource(.init(installationId: "b", bookUrl: "book-b", name: "猫"))
        await wait { !reader.loading && reader.sourceConfirmation != nil }
        XCTAssertEqual(reader.book, before); XCTAssertEqual(reader.content, body)
        reader.cancelSourceConfirmation()
        XCTAssertEqual(reader.book, before)
        reader.switchSource(.init(installationId: "b", bookUrl: "book-b", name: "猫"))
        await wait { reader.sourceConfirmation != nil && !reader.loading }
        reader.confirmUnknownSource(); await wait { !reader.loading && reader.book.installationId == "b" }
        XCTAssertEqual(reader.book.id, before.id)
        await feature.close()
    }
    func testColdReaderNeverFallsBackToOldIndexWhenSavedChapterNameIsAmbiguous() async throws {
        let (feature, runtime, _) = try await fixture()
        let local = try await feature.retain(.init(installationId: "a", bookUrl: "book-a", name: "猫", author: "林"), shelf: true)
        let old = NovelAnchor(chapterUrl: "retired-url", chapterTitle: "第二章 夜", chapterIndex: 9, ratio: 0.4)
        let identity = try await feature.rules.identity(local)
        try await feature.library.record(local, identity: identity, anchor: old); try await feature.reloadLocal()
        await runtime.setCatalog([.init(name: "第2章：夜", chapterUrl: "night-a", index: 0), .init(name: "第二章 夜", chapterUrl: "night-b", index: 1)])
        let reader = feature.makeReader(book: local); reader.start()
        await wait { !reader.loading && reader.sourceSelection != nil }
        XCTAssertNil(reader.content)
        let calls = await runtime.calls; XCTAssertFalse(calls.contains { $0["action"] == .string("content") })
        reader.selectSourceChapter(1)
        await wait { !reader.loading && reader.content != nil }
        XCTAssertEqual(reader.anchor?.chapterUrl, "night-b"); XCTAssertEqual(reader.book.id, local.id)
        await feature.close()
    }
    func testRuleFailureKeepsOriginalBodyAndTitleRatherThanAnEarlierPartialRule() async throws {
        let (feature, runtime, _) = try await fixture()
        await runtime.setBodyText(String(repeating: "a", count: 100_000) + "!")
        let reader = feature.makeReader(book: .init(installationId: "a", bookUrl: "book-a", name: "猫"))
        reader.start(); await wait { !reader.loading && reader.content != nil }
        let original = reader.content
        var title = NovelRule(); title.pattern = "第一章"; title.replacement = "不应提交"; title.scopeTitle = true; title.scopeContent = false
        var timeout = NovelRule(); timeout.order = 1; timeout.pattern = "^(a+)+$"; timeout.isRegex = true; timeout.timeoutMillisecond = 30
        try await feature.saveRules([title, timeout])
        await wait { !reader.loading && reader.error != nil }
        XCTAssertEqual(reader.content, original)
        XCTAssertFalse(reader.content?.title.contains("不应提交") == true)
        await feature.close()
    }
    func testRetainedReadersApplyGlobalRulesAndConversionOnReturnWithoutDownloadingAgain() async throws {
        let (feature, runtime, _) = try await fixture()
        await runtime.setBodyText("猫与国")
        let processor = NovelSessionTransform(rules: feature.rules)
        let visible = makeReader(feature, using: processor), retained = makeReader(feature, using: processor)
        visible.start(); retained.start()
        await wait { !visible.loading && !retained.loading && visible.content != nil && retained.content != nil }
        retained.display(.init(chapterUrl: "chapter-a-0", chapterTitle: "第一章 猫", chapterIndex: 0,
            blockIndex: 0, utf16Offset: 1, ratio: 1.0 / 3))
        retained.suspend()
        let before = retained.content, anchor = retained.anchor
        try await feature.saveRules([replacementRule()])
        await wait { !visible.loading && visible.content?.blocks.first?.text == "猫书国" }
        var settings = feature.local.settings; settings.conversion = "traditional"
        try await feature.saveSettings(settings)
        await wait { !visible.loading && visible.content?.blocks.first?.text == "貓書國" }
        XCTAssertEqual(retained.content, before); XCTAssertEqual(retained.anchor, anchor)
        XCTAssertFalse(retained.loading, "Global changes do not restart a suspended page")
        retained.resume()
        await wait { !retained.loading && retained.content?.blocks.first?.text == "貓書國" }
        XCTAssertEqual(retained.anchor, anchor)
        let calls = await runtime.calls
        XCTAssertEqual(calls.filter { $0["action"] == .string("content") }.count, 2)
        await feature.close()
    }
    func testSuspendedConversionKeepsOldBodyAndAnchorAndResumesOnlyTheLatestInput() async throws {
        let (feature, runtime, _) = try await fixture()
        await runtime.setBodyText("猫与国")
        let processor = NovelSessionTransform(rules: feature.rules), reader = makeReader(feature, using: processor)
        reader.start(); await wait { !reader.loading && reader.content != nil }
        reader.display(.init(chapterUrl: "chapter-a-0", chapterTitle: "第一章 猫", chapterIndex: 0,
            blockIndex: 0, utf16Offset: 1, ratio: 1.0 / 3))
        let before = try XCTUnwrap(reader.content), anchor = reader.anchor
        var published: [NovelContent] = []
        let observation = reader.$content.compactMap { $0 }.sink { published.append($0) }
        await processor.blockNext()
        var settings = feature.local.settings; settings.conversion = "traditional"
        try await feature.saveSettings(settings); await processor.waitForTransform()
        XCTAssertTrue(reader.loading); XCTAssertEqual(reader.content, before)
        reader.suspend()
        XCTAssertFalse(reader.loading); XCTAssertEqual(reader.content, before); XCTAssertEqual(reader.anchor, anchor)
        try await feature.saveRules([replacementRule()])
        XCTAssertFalse(reader.loading)
        reader.resume()
        await wait { !reader.loading && reader.content?.blocks.first?.text == "貓書國" }
        let final = try XCTUnwrap(reader.content)
        XCTAssertEqual(reader.anchor, anchor)
        await processor.release()
        // close drains the deliberately cancellation-ignoring old transform.
        await reader.close()
        XCTAssertEqual(published, [before, final], "The late pre-suspend result cannot replace the latest body")
        XCTAssertEqual(reader.anchor, anchor)
        withExtendedLifetime(observation) {}
        let calls = await runtime.calls
        XCTAssertEqual(calls.filter { $0["action"] == .string("content") }.count, 1)
        await feature.close()
    }
    func testFailedReprocessingRetriesOnResumeAndSuccessfulUnchangedInputDoesNotRunAgain() async throws {
        let (feature, runtime, _) = try await fixture()
        await runtime.setBodyText("猫与国")
        let processor = NovelSessionTransform(rules: feature.rules), reader = makeReader(feature, using: processor)
        reader.start(); await wait { !reader.loading && reader.content != nil }
        try await feature.saveRules([replacementRule()])
        await wait { !reader.loading && reader.content?.blocks.first?.text == "猫书国" }
        reader.display(.init(chapterUrl: "chapter-a-0", chapterTitle: "第一章 猫", chapterIndex: 0,
            blockIndex: 0, utf16Offset: 1, ratio: 1.0 / 3))
        let before = reader.content, anchor = reader.anchor
        await processor.failNext()
        var settings = feature.local.settings; settings.conversion = "traditional"
        try await feature.saveSettings(settings)
        await wait { !reader.loading && reader.error != nil }
        XCTAssertEqual(reader.content, before, "A failed new chain cannot roll back an already complete body to raw")
        XCTAssertEqual(reader.anchor, anchor)
        reader.error = nil; reader.resume()
        await wait { !reader.loading && reader.content?.blocks.first?.text == "貓書國" }
        XCTAssertEqual(reader.anchor, anchor)
        let count = await processor.calls.count, calls = await runtime.calls
        for _ in 0..<3 { reader.suspend(); reader.resume(); XCTAssertFalse(reader.loading) }
        try await feature.saveRules(feature.local.rules)
        settings.fontSize += 2; try await feature.saveSettings(settings)
        XCTAssertFalse(reader.loading)
        let after = await processor.calls.count, afterCalls = await runtime.calls
        XCTAssertEqual(after, count, "Only rules and conversion invalidate the processed body")
        XCTAssertEqual(afterCalls, calls, "No unchanged input may re-download the chapter")
        await feature.close()
    }
    func testInitialLoadDiscardsAnObsoleteFailureAndCommitsOnlyTheLastSettings() async throws {
        let (feature, runtime, _) = try await fixture()
        await runtime.setBodyText("猫与国")
        let processor = NovelSessionTransform(rules: feature.rules), reader = makeReader(feature, using: processor)
        await processor.blockNext(failing: true)
        reader.start(); await processor.waitForTransform()
        try await feature.saveRules([replacementRule()])
        var settings = feature.local.settings; settings.conversion = "traditional"
        try await feature.saveSettings(settings)
        XCTAssertTrue(reader.loading); XCTAssertNil(reader.content)
        await processor.release()
        await wait { !reader.loading && reader.content?.blocks.first?.text == "貓書國" }
        XCTAssertNil(reader.error, "An obsolete input's failure is not the latest input's failure")
        let transforms = await processor.calls, calls = await runtime.calls
        XCTAssertEqual(transforms.count, 2)
        XCTAssertEqual(transforms.last?.conversion, "traditional")
        XCTAssertEqual(transforms.last?.rules, feature.local.rules)
        XCTAssertEqual(calls.filter { $0["action"] == .string("content") }.count, 1)
        await feature.close()
    }
    func testSourceSwitchKeepsOldSessionUntilTheLatestRulesAndConversionComplete() async throws {
        let (feature, runtime, _) = try await fixture()
        await runtime.setBodyText("猫与国"); await runtime.setAuthor("", source: "a")
        let processor = NovelSessionTransform(rules: feature.rules), reader = makeReader(feature, using: processor)
        reader.start(); await wait { !reader.loading && reader.content != nil }
        try await feature.setShelf(reader.book, present: true); try await feature.pin(reader.book.id)
        reader.display(try XCTUnwrap(reader.anchor)); await reader.flush()
        let before = try await feature.library.snapshot(), entry = try XCTUnwrap(before.entries.first)
        let book = reader.book, content = reader.content, anchor = reader.anchor
        await processor.blockNext()
        reader.switchSource(.init(installationId: "b", bookUrl: "book-b", name: "猫", author: "林"))
        await wait { !reader.loading && reader.sourceConfirmation != nil }
        reader.confirmUnknownSource()
        await processor.waitForTransform()
        try await feature.saveRules([replacementRule()])
        var settings = feature.local.settings; settings.conversion = "traditional"
        try await feature.saveSettings(settings)
        XCTAssertTrue(reader.loading)
        XCTAssertEqual(reader.book, book); XCTAssertEqual(reader.content, content); XCTAssertEqual(reader.anchor, anchor)
        await processor.release()
        await wait { !reader.loading && reader.book.installationId == "b" && reader.content?.blocks.first?.text == "貓書國" }
        XCTAssertEqual(reader.book.id, book.id)
        await reader.flush()
        let prepared = try await feature.library.snapshot()
        XCTAssertEqual(prepared.entries, before.entries, "Prepared content cannot replace a locator before it is displayed")
        reader.display(try XCTUnwrap(reader.anchor)); await reader.flush()
        let displayed = try await feature.library.snapshot(), saved = try XCTUnwrap(displayed.entries.first)
        XCTAssertEqual(displayed.entries.count, 1)
        XCTAssertEqual(saved.id, entry.id); XCTAssertEqual(saved.book, reader.book)
        XCTAssertEqual(saved.book.installationId, "b"); XCTAssertEqual(saved.book.bookUrl, "book-b")
        XCTAssertEqual(saved.anchor, reader.anchor); XCTAssertEqual(saved.anchor?.chapterUrl, "chapter-b-0")
        XCTAssertEqual(saved.onShelf, entry.onShelf); XCTAssertEqual(saved.pinnedAt, entry.pinnedAt)
        XCTAssertEqual(saved.addedAt, entry.addedAt)
        XCTAssertEqual(displayed.books(history: true).map(\.id), before.books(history: true).map(\.id))
        let identity = try await feature.rules.identity(reader.book)
        XCTAssertEqual(saved.identity, identity); XCTAssertNotEqual(saved.identity, entry.identity)
        let third = try await feature.retain(.init(installationId: "c", bookUrl: "book-c", name: "猫", author: "林"))
        XCTAssertEqual(third.id, book.id, "A third known-author source must match the identity saved with the displayed body")
        let matched = try await feature.library.snapshot(); XCTAssertEqual(matched.entries.count, 1)
        let transforms = await processor.calls, calls = await runtime.calls
        XCTAssertEqual(transforms.map(\.sourceID), ["a", "b", "b"])
        XCTAssertEqual(transforms.last?.rules, feature.local.rules)
        XCTAssertEqual(transforms.last?.conversion, "traditional")
        XCTAssertEqual(calls.filter { $0["action"] == .string("content") && $0["installationId"] == .string("b") }.count, 1)
        await feature.close()
    }
    func testLatestSourceSwitchProcessingFailureKeepsPersistedSourceAndDisplayedProgress() async throws {
        let (feature, runtime, _) = try await fixture()
        await runtime.setBodyText("猫与国")
        let processor = NovelSessionTransform(rules: feature.rules), reader = makeReader(feature, using: processor)
        reader.start(); await wait { !reader.loading && reader.content != nil }
        try await feature.setShelf(reader.book, present: true); try await feature.pin(reader.book.id)
        reader.display(.init(chapterUrl: "chapter-a-0", chapterTitle: "第一章 猫", chapterIndex: 0,
            blockIndex: 0, utf16Offset: 1, ratio: 1.0 / 3))
        await reader.flush()
        let before = try await feature.library.snapshot(), book = reader.book, chapters = reader.chapters, anchor = reader.anchor
        await processor.blockNext()
        reader.switchSource(.init(installationId: "b", bookUrl: "book-b", name: "猫", author: "林"))
        await processor.waitForTransform()
        try await feature.saveRules([replacementRule()])
        var settings = feature.local.settings; settings.conversion = "traditional"
        try await feature.saveSettings(settings)
        await processor.failNext(); await processor.release()
        // The obsolete pass succeeds, but the last input fails for the candidate.
        // Global display settings may still be applied to the retained old source.
        await wait { !reader.loading && reader.error != nil && reader.content?.blocks.first?.text == "貓書國" }
        XCTAssertEqual(reader.book, book); XCTAssertEqual(reader.chapters, chapters); XCTAssertEqual(reader.anchor, anchor)
        XCTAssertEqual(reader.error, NovelError.invalidResponse.localizedDescription)
        await reader.flush()
        let failed = try await feature.library.snapshot(), transforms = await processor.calls, calls = await runtime.calls
        XCTAssertEqual(failed.entries, before.entries, "A failed candidate must not leave its book locator or anchor on disk")
        XCTAssertEqual(failed.entries.first?.book.installationId, "a")
        XCTAssertEqual(failed.entries.first?.book.bookUrl, "book-a")
        XCTAssertEqual(transforms.map(\.sourceID), ["a", "b", "b", "a"])
        XCTAssertEqual(transforms.last?.rules, feature.local.rules)
        XCTAssertEqual(transforms.last?.conversion, "traditional")
        XCTAssertEqual(calls.filter { $0["action"] == .string("content") }.count, 2)
        await feature.close()
    }
    func testInitialProcessingFailureRemainsRetryableWithoutAnotherContentRequest() async throws {
        let (feature, runtime, _) = try await fixture()
        await runtime.setBodyText("猫与国")
        var settings = feature.local.settings; settings.conversion = "traditional"
        try await feature.saveSettings(settings)
        let processor = NovelSessionTransform(rules: feature.rules), reader = makeReader(feature, using: processor)
        await processor.failNext()
        reader.start(); await wait { !reader.loading && reader.content != nil && reader.error != nil }
        XCTAssertEqual(reader.content?.blocks.first?.text, "猫与国")
        let anchor = reader.anchor
        reader.error = nil; reader.suspend(); reader.resume()
        await wait { !reader.loading && reader.content?.blocks.first?.text == "貓與國" }
        XCTAssertEqual(reader.anchor, anchor)
        let calls = await runtime.calls, transforms = await processor.calls
        XCTAssertEqual(calls.filter { $0["action"] == .string("content") }.count, 1)
        XCTAssertEqual(transforms.count, 2)
        await feature.close()
    }
    func testCancelledSourceSwitchReprocessesOnlyTheRetainedSourceOnResume() async throws {
        let (feature, runtime, _) = try await fixture()
        await runtime.setBodyText("猫与国")
        let processor = NovelSessionTransform(rules: feature.rules), reader = makeReader(feature, using: processor)
        reader.start(); await wait { !reader.loading && reader.content != nil }
        try await feature.setShelf(reader.book, present: true); try await feature.pin(reader.book.id)
        reader.display(.init(chapterUrl: "chapter-a-0", chapterTitle: "第一章 猫", chapterIndex: 0,
            blockIndex: 0, utf16Offset: 1, ratio: 1.0 / 3))
        await reader.flush()
        let before = try await feature.library.snapshot()
        let book = reader.book, content = reader.content, anchor = reader.anchor
        await processor.blockNext()
        reader.switchSource(.init(installationId: "b", bookUrl: "book-b", name: "猫", author: "林"))
        await processor.waitForTransform()
        var settings = feature.local.settings; settings.conversion = "traditional"
        try await feature.saveSettings(settings)
        reader.suspend()
        XCTAssertEqual(reader.book, book); XCTAssertEqual(reader.content, content); XCTAssertEqual(reader.anchor, anchor)
        await reader.flush()
        let cancelled = try await feature.library.snapshot()
        XCTAssertEqual(cancelled.entries, before.entries, "Cancelling a candidate keeps the old persisted source and progress")
        reader.resume()
        await wait { !reader.loading && reader.content?.blocks.first?.text == "貓與國" }
        XCTAssertEqual(reader.book, book); XCTAssertEqual(reader.anchor, anchor)
        await processor.release(); await reader.close()
        XCTAssertEqual(reader.book, book)
        let drained = try await feature.library.snapshot()
        XCTAssertEqual(drained.entries, before.entries, "A late cancelled transform cannot persist the candidate")
        let calls = await runtime.calls, transforms = await processor.calls
        XCTAssertEqual(calls.filter { $0["action"] == .string("content") }.count, 2)
        XCTAssertEqual(transforms.map(\.sourceID), ["a", "b", "a"])
        await feature.close()
    }
    func testDisplayedProgressFlushAndCloseRemainLocalWhenTheRuntimeIsUnavailable() async throws {
        let (feature, runtime, store) = try await fixture()
        let reader = feature.makeReader(book: .init(installationId: "a", bookUrl: "book-a", name: "猫"))
        reader.start(); await wait { !reader.loading && reader.content != nil }
        XCTAssertEqual(reader.book.author, "林")
        let identity = try await feature.rules.identity(reader.book)
        let calls = await runtime.calls
        await feature.rules.close(); await runtime.setUnavailable()
        let first = try XCTUnwrap(reader.anchor)
        reader.display(first); await reader.flush()
        var snapshot = try await feature.library.snapshot()
        XCTAssertEqual(snapshot.books(history: true).first?.anchor, first)
        XCTAssertEqual(snapshot.entries.first?.identity, identity)
        XCTAssertEqual(snapshot.entries.first?.book, reader.book)
        var last = first; last.utf16Offset = 2; last.ratio = 0.2
        reader.display(last); await reader.close()
        snapshot = try await feature.library.snapshot()
        XCTAssertEqual(snapshot.books(history: true).first?.anchor, last)
        XCTAssertEqual(snapshot.entries.first?.identity, identity)
        XCTAssertEqual(snapshot.entries.first?.book, reader.book)
        XCTAssertNil(reader.error); XCTAssertNil(feature.error)
        let after = await runtime.calls; XCTAssertEqual(after, calls)
        await feature.close()
        let reopened = NovelLibrary(store: store)
        let restored = try await reopened.snapshot()
        XCTAssertEqual(restored.entries, snapshot.entries)
        await reopened.close()
    }
    func testExistingShelfMembershipByIDAndLocatorWorksWithoutTheRuntime() async throws {
        let (feature, runtime, store) = try await fixture()
        let reader = feature.makeReader(book: .init(installationId: "a", bookUrl: "book-a", name: "猫"))
        reader.start(); await wait { !reader.loading && reader.content != nil }
        reader.display(try XCTUnwrap(reader.anchor)); await reader.flush()
        try await feature.setShelf(reader.book, present: true); try await feature.pin(reader.book.id)
        let before = try XCTUnwrap(feature.local.entries.first)
        await feature.rules.close(); await runtime.setUnavailable()
        try await feature.setShelf(reader.book, present: false)
        XCTAssertTrue(feature.local.books(history: false).isEmpty)
        XCTAssertEqual(feature.local.books(history: true).map(\.id), [before.id])
        var sameLocator = reader.book; sameLocator.id = UUID().uuidString; sameLocator.author = ""
        try await feature.setShelf(sameLocator, present: true)
        XCTAssertEqual(feature.local.entries.count, 1)
        XCTAssertEqual(feature.local.entries.first, before, "Membership alone cannot replace metadata, identity or the displayed anchor")
        let bytes = try store.read(key: "library")
        do {
            try await feature.setShelf(.init(installationId: "new", bookUrl: "new-book", name: "新书"), present: true)
            XCTFail("A new identity still requires the Node rule owner")
        } catch { XCTAssertEqual(error as? NovelError, .closed) }
        XCTAssertEqual(try store.read(key: "library"), bytes)
        await feature.close()
        let reopened = NovelLibrary(store: store), restored = try await reopened.snapshot()
        XCTAssertEqual(restored.entries, [before]); await reopened.close()
    }
    func testPreparedSourceIdentityPersistsLocallyAndStillMatchesAThirdSource() async throws {
        let (feature, runtime, store) = try await fixture()
        await runtime.setAuthor("", source: "a")
        let reader = feature.makeReader(book: .init(installationId: "a", bookUrl: "book-a", name: "猫"))
        reader.start(); await wait { !reader.loading && reader.content != nil }
        try await feature.setShelf(reader.book, present: true); try await feature.pin(reader.book.id)
        reader.display(try XCTUnwrap(reader.anchor)); await reader.flush()
        let old = try XCTUnwrap(feature.local.entries.first)
        reader.switchSource(.init(installationId: "b", bookUrl: "book-b", name: "猫", author: "林"))
        await wait { !reader.loading && reader.sourceConfirmation != nil }
        reader.confirmUnknownSource(); await wait { !reader.loading && reader.book.installationId == "b" }
        let third = NovelBook(installationId: "c", bookUrl: "book-c", name: "猫", author: "林")
        let expectedIdentity = try await feature.rules.identity(reader.book), thirdIdentity = try await feature.rules.identity(third)
        XCTAssertNotEqual(expectedIdentity, old.identity)
        await feature.rules.close(); await runtime.setUnavailable()
        reader.display(try XCTUnwrap(reader.anchor)); await reader.flush()
        let saved = try XCTUnwrap(feature.local.entries.first)
        XCTAssertEqual(saved.id, old.id); XCTAssertEqual(saved.identity, expectedIdentity)
        XCTAssertEqual(saved.book, reader.book); XCTAssertEqual(saved.anchor, reader.anchor)
        XCTAssertEqual(saved.onShelf, old.onShelf); XCTAssertEqual(saved.pinnedAt, old.pinnedAt)
        XCTAssertEqual(saved.addedAt, old.addedAt); XCTAssertNil(reader.error)
        await feature.close()
        let reopened = NovelLibrary(store: store)
        let matched = try await reopened.retain(third, identity: thirdIdentity)
        XCTAssertEqual(matched.id, old.id)
        let restored = try await reopened.snapshot()
        XCTAssertEqual(restored.entries.count, 1); XCTAssertEqual(restored.entries.first?.identity, expectedIdentity)
        XCTAssertEqual(restored.entries.first?.anchor, saved.anchor); XCTAssertEqual(restored.entries.first?.readAt, saved.readAt)
        await reopened.close()
    }
    func testCatalogBodyFailureAndCancellationKeepTheDisplayedSnapshotUntilSuccessfulRetry() async throws {
        let (feature, runtime, store) = try await fixture()
        let reader = feature.makeReader(book: .init(installationId: "a", bookUrl: "book-a", name: "猫"))
        reader.start(); await wait { !reader.loading && reader.content != nil }
        reader.jump(1); await wait { !reader.loading && reader.chapterIndex == 1 }
        reader.display(try XCTUnwrap(reader.anchor)); await reader.flush()
        let book = reader.book, chapters = reader.chapters, body = reader.content, anchor = reader.anchor
        let bytes = try store.read(key: "library")
        await runtime.setCatalog([.init(name: "第2章：夜", chapterUrl: "moved-night", index: 0)])
        await runtime.failNextBody()
        reader.refreshCatalog(); await wait { !reader.loading && reader.error != nil }
        XCTAssertEqual(reader.book, book); XCTAssertEqual(reader.chapters, chapters)
        XCTAssertEqual(reader.content, body); XCTAssertEqual(reader.chapterIndex, 1); XCTAssertEqual(reader.anchor, anchor)
        await reader.flush(); XCTAssertEqual(try store.read(key: "library"), bytes)
        reader.error = nil; await runtime.blockBody()
        reader.refreshCatalog(); await runtime.waitForBody()
        reader.suspend(); await runtime.releaseBody(); reader.resume()
        XCTAssertEqual(reader.book, book); XCTAssertEqual(reader.chapters, chapters)
        XCTAssertEqual(reader.content, body); XCTAssertEqual(reader.chapterIndex, 1); XCTAssertEqual(reader.anchor, anchor)
        reader.refreshCatalog(); await wait { !reader.loading && reader.anchor?.chapterUrl == "moved-night" }
        XCTAssertEqual(reader.chapterIndex, 0); XCTAssertEqual(reader.chapters.count, 1)
        reader.display(try XCTUnwrap(reader.anchor)); await reader.flush()
        let saved = try XCTUnwrap(feature.local.books(history: true).first)
        XCTAssertEqual(saved.book, reader.book); XCTAssertEqual(saved.anchor, reader.anchor)
        await feature.close()
    }
    func testCatalogProcessingFailureCannotPublishACandidateCatalogOrBody() async throws {
        let (feature, runtime, store) = try await fixture()
        let processor = NovelSessionTransform(rules: feature.rules), reader = makeReader(feature, using: processor)
        reader.start(); await wait { !reader.loading && reader.content != nil }
        reader.display(try XCTUnwrap(reader.anchor)); await reader.flush()
        let book = reader.book, chapters = reader.chapters, content = reader.content, anchor = reader.anchor
        let bytes = try store.read(key: "library")
        await runtime.setCatalog([.init(name: "第1章：猫", chapterUrl: "moved-cat", index: 0)])
        await processor.failNext()
        reader.refreshCatalog(); await wait { !reader.loading && reader.error != nil }
        XCTAssertEqual(reader.book, book); XCTAssertEqual(reader.chapters, chapters)
        XCTAssertEqual(reader.content, content); XCTAssertEqual(reader.anchor, anchor)
        await reader.flush(); XCTAssertEqual(try store.read(key: "library"), bytes)
        await feature.close()
    }
    func testAcceptedPurchaseBodyFailureKeepsOldDisplayAndExplicitRefreshNeverRepays() async throws {
        let (feature, runtime, store) = try await fixture()
        await runtime.usePurchaseContextFixture()
        let reader = feature.makeReader(book: .init(installationId: "a", bookUrl: "book-a", name: "猫"))
        reader.start(); await wait { !reader.loading && reader.content != nil }
        reader.display(try XCTUnwrap(reader.anchor)); await reader.flush()
        let book = reader.book, chapters = reader.chapters, body = reader.content, anchor = reader.anchor
        let bytes = try store.read(key: "library"), chapter = try XCTUnwrap(reader.purchasableChapter)
        await runtime.failNextBody()
        reader.confirmPurchase(chapter: chapter, bookID: book.id)
        await wait { !reader.loading && reader.error != nil }
        let paid = await runtime.purchaseCount(); XCTAssertEqual(paid, 1)
        XCTAssertEqual(reader.book, book); XCTAssertEqual(reader.chapters, chapters)
        XCTAssertEqual(reader.content, body); XCTAssertEqual(reader.chapterIndex, 0); XCTAssertEqual(reader.anchor, anchor)
        await reader.flush(); XCTAssertEqual(try store.read(key: "library"), bytes)
        reader.error = nil; reader.suspend(); reader.resume()
        let resumed = await runtime.purchaseCount(); XCTAssertEqual(resumed, 1)
        XCTAssertEqual(reader.content, body); XCTAssertFalse(reader.loading)
        reader.refreshCatalog(); await wait { !reader.loading && reader.purchasableChapter == nil }
        let final = await runtime.purchaseCount(); XCTAssertEqual(final, 1)
        XCTAssertEqual(reader.chapterIndex, 1); XCTAssertEqual(reader.anchor?.chapterUrl, chapter.chapterUrl)
        XCTAssertEqual(reader.content?.blocks.first?.text, "已购正文")
        let paidVariable = RuntimeValue.object(["stage": .string("paid"), "version": .number(2)])
        XCTAssertEqual(reader.book.variable, paidVariable); XCTAssertEqual(reader.chapters[1].variable, paidVariable)
        XCTAssertEqual(reader.chapters[1].wire["payContext"]["nested"]["receipt"], .string("paid"))
        XCTAssertEqual(reader.chapters[1].wire["contentContext"], chapter.wire["contentContext"])
        let calls = await runtime.calls, bodies = calls.filter { $0["action"] == .string("content") }
        XCTAssertEqual(bodies.last?["payload"]["variable"], paidVariable)
        XCTAssertEqual(bodies.last?["payload"]["book"]["variable"], paidVariable)
        reader.display(try XCTUnwrap(reader.anchor)); await reader.flush()
        XCTAssertEqual(feature.local.books(history: true).first?.anchor, reader.anchor)
        await feature.close()
    }
    func testAcceptedPurchaseBodyCancellationCannotPublishLateStateOrRepeatPayment() async throws {
        let (feature, runtime, store) = try await fixture()
        await runtime.usePurchaseContextFixture()
        let reader = feature.makeReader(book: .init(installationId: "a", bookUrl: "book-a", name: "猫"))
        reader.start(); await wait { !reader.loading && reader.content != nil }
        reader.display(try XCTUnwrap(reader.anchor)); await reader.flush()
        let book = reader.book, chapters = reader.chapters, body = reader.content, anchor = reader.anchor
        let bytes = try store.read(key: "library"), chapter = try XCTUnwrap(reader.purchasableChapter)
        await runtime.blockBody()
        reader.confirmPurchase(chapter: chapter, bookID: book.id); await runtime.waitForBody()
        reader.suspend()
        XCTAssertEqual(reader.book, book); XCTAssertEqual(reader.chapters, chapters); XCTAssertEqual(reader.content, body)
        XCTAssertEqual(reader.chapterIndex, 0); XCTAssertEqual(reader.anchor, anchor)
        await runtime.releaseBody(); reader.resume()
        let paid = await runtime.purchaseCount(); XCTAssertEqual(paid, 1)
        await reader.flush(); XCTAssertEqual(try store.read(key: "library"), bytes)
        reader.confirmPurchase(chapter: chapter, bookID: book.id)
        await wait { !reader.loading && reader.purchasableChapter == nil }
        let final = await runtime.purchaseCount(); XCTAssertEqual(final, 1, "A second confirmation after an accepted payment only refreshes")
        XCTAssertEqual(reader.chapterIndex, 1); XCTAssertEqual(reader.anchor?.chapterUrl, chapter.chapterUrl)
        XCTAssertEqual(reader.content?.blocks.first?.text, "已购正文")
        XCTAssertEqual(reader.chapters[1].wire["payContext"]["nested"]["receipt"], .string("paid"))
        await feature.close()
    }
    func testAcceptedPurchaseProcessingFailureRetainsTheLatestContentContextForRetry() async throws {
        let (feature, runtime, store) = try await fixture()
        await runtime.usePurchaseContextFixture()
        let processor = NovelSessionTransform(rules: feature.rules), reader = makeReader(feature, using: processor)
        reader.start(); await wait { !reader.loading && reader.content != nil }
        reader.display(try XCTUnwrap(reader.anchor)); await reader.flush()
        let book = reader.book, chapters = reader.chapters, content = reader.content, anchor = reader.anchor
        let bytes = try store.read(key: "library"), chapter = try XCTUnwrap(reader.purchasableChapter)
        await runtime.advancePaidBodyContext(); await processor.failNext()
        reader.confirmPurchase(chapter: chapter, bookID: book.id)
        await wait { !reader.loading && reader.error != nil }
        XCTAssertEqual(reader.book, book); XCTAssertEqual(reader.chapters, chapters)
        XCTAssertEqual(reader.content, content); XCTAssertEqual(reader.chapterIndex, 0); XCTAssertEqual(reader.anchor, anchor)
        await reader.flush(); XCTAssertEqual(try store.read(key: "library"), bytes)
        let paid = await runtime.purchaseCount(); XCTAssertEqual(paid, 1)

        reader.error = nil; reader.jump(0, ratio: 0.4)
        await wait { !reader.loading && reader.purchasableChapter == nil }
        let latestVariable = RuntimeValue.object(["stage": .string("content"), "version": .number(3)])
        let calls = await runtime.calls, bodies = calls.filter { $0["action"] == .string("content") }
        let payload = try XCTUnwrap(bodies.last)["payload"]
        XCTAssertEqual(calls.filter { $0["action"] == .string("payAction") }.count, 1)
        XCTAssertEqual(calls.last { $0["action"] == .string("toc") }?["payload"]["book"]["variable"], latestVariable)
        XCTAssertEqual(payload["variable"], latestVariable)
        XCTAssertEqual(payload["book"]["variable"], latestVariable)
        XCTAssertEqual(payload["chapter"]["variable"], latestVariable)
        XCTAssertEqual(payload["chapter"]["retryContext"], .array([.string("content"), .null, .bool(true)]))
        XCTAssertEqual(payload["chapter"]["payContext"]["nested"]["receipt"], .string("paid"))
        XCTAssertEqual(payload["chapter"]["contentContext"], chapter.wire["contentContext"])
        XCTAssertEqual(reader.book.variable, latestVariable); XCTAssertEqual(reader.chapterIndex, 1)
        XCTAssertEqual(reader.anchor?.chapterUrl, chapter.chapterUrl); XCTAssertEqual(reader.anchor?.ratio, 0.4)
        await feature.close()
    }
    func testCatalogRefreshAfterAnAcceptedPurchaseKeepsTheCurrentChapterAndPaidContext() async throws {
        let (feature, runtime, _) = try await fixture()
        await runtime.usePurchaseContextFixture()
        let reader = feature.makeReader(book: .init(installationId: "a", bookUrl: "book-a", name: "猫"))
        reader.start(); await wait { !reader.loading && reader.content != nil }
        let chapter = try XCTUnwrap(reader.purchasableChapter)
        await runtime.failNextBody()
        reader.confirmPurchase(chapter: chapter, bookID: reader.book.id)
        await wait { !reader.loading && reader.error != nil }
        await runtime.advancePaidBodyContext()
        reader.error = nil; reader.jump(1)
        await wait { !reader.loading && reader.anchor?.chapterUrl == "chapter-a-1" }
        let jumped = await runtime.calls
        let jumpPayload = try XCTUnwrap(jumped.last { $0["action"] == .string("content") })["payload"]
        let paidVariable = RuntimeValue.object(["stage": .string("paid"), "version": .number(2)])
        XCTAssertEqual(jumpPayload["variable"], paidVariable); XCTAssertEqual(jumpPayload["book"]["variable"], paidVariable)
        reader.display(try XCTUnwrap(reader.anchor)); await reader.flush()
        var expectedAnchor = try XCTUnwrap(reader.anchor); expectedAnchor.chapterIndex = 0
        reader.refreshCatalog()
        await wait { !reader.loading }
        XCTAssertEqual(reader.anchor, expectedAnchor)
        XCTAssertEqual(reader.chapterIndex, 0); XCTAssertEqual(reader.chapters[0].chapterUrl, "chapter-a-1")
        XCTAssertEqual(reader.chapters[1].chapterUrl, chapter.chapterUrl)
        XCTAssertEqual(reader.chapters[1].wire["payContext"]["nested"]["receipt"], .string("paid"))
        let latestVariable = RuntimeValue.object(["stage": .string("content"), "version": .number(3)])
        XCTAssertEqual(reader.book.variable, latestVariable); XCTAssertEqual(reader.chapters[1].variable, latestVariable)
        reader.jump(1); await wait { !reader.loading && reader.anchor?.chapterUrl == chapter.chapterUrl }
        let calls = await runtime.calls, payload = try XCTUnwrap(calls.last { $0["action"] == .string("content") })["payload"]
        XCTAssertEqual(calls.filter { $0["action"] == .string("payAction") }.count, 1)
        XCTAssertEqual(payload["chapter"]["payContext"]["nested"]["receipt"], .string("paid"))
        XCTAssertEqual(payload["chapter"]["contentContext"], chapter.wire["contentContext"])
        XCTAssertEqual(payload["chapter"]["variable"], latestVariable)
        await feature.close()
    }
    func testSuspendedInitialDownloadRestartsWithANewGenerationAndClosePreventsRevival() async throws {
        let (feature, runtime, _) = try await fixture()
        let reader = feature.makeReader(book: .init(installationId: "a", bookUrl: "book-a", name: "猫"))
        var published: [NovelContent] = []
        let observation = reader.$content.compactMap { $0 }.sink { published.append($0) }
        await runtime.blockBody()
        reader.start(); await runtime.waitForBody()
        reader.suspend(); await runtime.unblockFutureBodies()
        XCTAssertNil(reader.content); XCTAssertFalse(reader.loading)
        await runtime.setBodyText("返回后的新正文")
        reader.resume(); await wait { !reader.loading && reader.content != nil }
        let current = try XCTUnwrap(reader.content)
        XCTAssertEqual(current.blocks.first?.text, "返回后的新正文")
        await runtime.setBodyText("被取消的迟到正文"); await runtime.releaseBody()
        await reader.close(); reader.resume()
        XCTAssertEqual(published, [current]); withExtendedLifetime(observation) {}
        XCTAssertTrue(reader.closed); XCTAssertFalse(reader.loading); XCTAssertNil(reader.content)
        let calls = await runtime.calls
        XCTAssertEqual(calls.filter { $0["action"] == .string("content") }.count, 2)

        let closed = feature.makeReader(book: .init(installationId: "a", bookUrl: "book-a", name: "猫"))
        await runtime.blockBody()
        closed.start(); await runtime.waitForBody(); closed.suspend()
        let closing = Task { await closed.close() }
        await Task.yield(); await runtime.releaseBody(); await closing.value
        let beforeResume = await runtime.calls
        closed.resume()
        XCTAssertTrue(closed.closed); XCTAssertFalse(closed.loading); XCTAssertNil(closed.content)
        let afterResume = await runtime.calls; XCTAssertEqual(afterResume, beforeResume)
        await feature.close()
    }
    func testSuspendedInitialTransformPublishesOnlyTheResumedLatestInput() async throws {
        let (feature, runtime, _) = try await fixture()
        await runtime.setBodyText("猫与国")
        let processor = NovelSessionTransform(rules: feature.rules), reader = makeReader(feature, using: processor)
        var published: [NovelContent] = []
        let observation = reader.$content.compactMap { $0 }.sink { published.append($0) }
        await processor.blockNext()
        reader.start(); await processor.waitForTransform(); reader.suspend()
        try await feature.saveRules([replacementRule()])
        var settings = feature.local.settings; settings.conversion = "traditional"
        try await feature.saveSettings(settings)
        reader.resume(); await wait { !reader.loading && reader.content?.blocks.first?.text == "貓書國" }
        let current = try XCTUnwrap(reader.content)
        await processor.release(); await reader.close()
        XCTAssertEqual(published, [current])
        withExtendedLifetime(observation) {}
        let transforms = await processor.calls
        XCTAssertEqual(transforms.count, 2); XCTAssertEqual(transforms.last?.rules, feature.local.rules)
        XCTAssertEqual(transforms.last?.conversion, "traditional")
        await feature.close()
    }
    func testResumeDoesNotConfirmAmbiguousInitialChapterOrAnUnknownAuthor() async throws {
        let (feature, runtime, _) = try await fixture()
        let local = try await feature.retain(.init(installationId: "a", bookUrl: "book-a", name: "猫", author: "林"))
        let old = NovelAnchor(chapterUrl: "retired", chapterTitle: "第二章 夜", chapterIndex: 9, ratio: 0.4)
        let identity = try await feature.rules.identity(local)
        try await feature.library.record(local, identity: identity, anchor: old); try await feature.reloadLocal()
        await runtime.setCatalog([.init(name: "第2章：夜", chapterUrl: "night-a", index: 0),
                                  .init(name: "第二章 夜", chapterUrl: "night-b", index: 1)])
        let reader = feature.makeReader(book: local); reader.start()
        await wait { !reader.loading && reader.sourceSelection != nil }
        let before = await runtime.calls
        reader.suspend(); reader.resume()
        XCTAssertFalse(reader.loading); XCTAssertNil(reader.content); XCTAssertNotNil(reader.sourceSelection)
        let after = await runtime.calls; XCTAssertEqual(after, before)
        reader.cancelSourceSelection(); reader.suspend(); reader.resume()
        XCTAssertFalse(reader.loading); XCTAssertNil(reader.content); XCTAssertNil(reader.sourceSelection)
        reader.start(); await wait { !reader.loading && reader.sourceSelection != nil }
        await runtime.blockBody(); reader.selectSourceChapter(1); await runtime.waitForBody()
        reader.suspend(); await runtime.unblockFutureBodies(); reader.resume()
        await wait { !reader.loading && reader.anchor?.chapterUrl == "night-b" && reader.content != nil }
        await runtime.releaseBody()
        await runtime.setAuthor("", source: "b")
        reader.switchSource(.init(installationId: "b", bookUrl: "book-b", name: "猫"))
        await wait { !reader.loading && reader.sourceConfirmation != nil }
        reader.suspend(); reader.resume()
        XCTAssertFalse(reader.loading); XCTAssertNotNil(reader.sourceConfirmation); XCTAssertEqual(reader.book.installationId, "a")
        reader.cancelSourceConfirmation(); reader.suspend(); reader.resume()
        XCTAssertFalse(reader.loading); XCTAssertNil(reader.sourceConfirmation); XCTAssertEqual(reader.book.installationId, "a")
        let payments = await runtime.purchaseCount(); XCTAssertEqual(payments, 0)

        let stale = feature.makeReader(book: local); stale.start()
        await wait { !stale.loading && stale.sourceSelection != nil }
        await runtime.blockBody(); stale.selectSourceChapter(1); await runtime.waitForBody()
        stale.suspend()
        await runtime.setRevision(2); try await feature.refreshSources()
        await runtime.unblockFutureBodies()
        let beforeResume = await runtime.calls
        stale.resume()
        XCTAssertFalse(stale.loading); XCTAssertNil(stale.content); XCTAssertNotNil(stale.error)
        let afterResume = await runtime.calls; XCTAssertEqual(afterResume, beforeResume)
        await runtime.releaseBody(); await stale.close()
        XCTAssertNil(stale.content)
        await feature.close()
    }
    func testAmbiguousInitialSelectionIsInvalidatedWhenItsSourceChangesOrIsDisabled() async throws {
        for revisionChange in [true, false] {
            let (feature, runtime, _) = try await fixture()
            let local = try await feature.retain(.init(installationId: "a", bookUrl: "book-a", name: "猫", author: "林"))
            let saved = NovelAnchor(chapterUrl: "retired", chapterTitle: "第二章 夜", chapterIndex: 9, ratio: 0.4)
            let identity = try await feature.rules.identity(local)
            try await feature.library.record(local, identity: identity, anchor: saved); try await feature.reloadLocal()
            await runtime.setCatalog([.init(name: "第2章：夜", chapterUrl: "night-a", index: 0),
                                      .init(name: "第二章 夜", chapterUrl: "night-b", index: 1)])
            let reader = feature.makeReader(book: local); reader.start()
            await wait { !reader.loading && reader.sourceSelection != nil }
            let selection = try XCTUnwrap(reader.sourceSelection)
            XCTAssertEqual(selection.source, feature.sources.first { $0.id == "a" })
            let book = reader.book, before = try await feature.library.snapshot()
            if revisionChange { await runtime.setRevision(2, source: "a") }
            else { await runtime.disableSource("a") }
            try await feature.refreshSources()
            XCTAssertNil(reader.sourceSelection); XCTAssertFalse(reader.loading)
            XCTAssertEqual(reader.error, "当前书源已更新或停用，请重新载入")
            let calls = await runtime.calls
            reader.selectSourceChapter(1); reader.resume()
            XCTAssertFalse(reader.loading)
            await wait { !reader.loading }; await reader.flush()
            let after = await runtime.calls, persisted = try await feature.library.snapshot()
            XCTAssertEqual(after, calls, "An old picker cannot request a chapter from the retired catalog")
            XCTAssertEqual(reader.book, book); XCTAssertNil(reader.content); XCTAssertTrue(reader.chapters.isEmpty)
            XCTAssertEqual(reader.chapterIndex, 0); XCTAssertEqual(reader.anchor, saved)
            XCTAssertEqual(persisted.entries, before.entries)
            await feature.close()
        }
    }
    func testAmbiguousCatalogSelectionIsInvalidatedWithoutReplacingDisplayedProgress() async throws {
        for revisionChange in [true, false] {
            let (feature, runtime, _) = try await fixture()
            let reader = feature.makeReader(book: .init(installationId: "a", bookUrl: "book-a", name: "猫"))
            reader.start(); await wait { !reader.loading && reader.content != nil }
            var position = try XCTUnwrap(reader.anchor); position.utf16Offset = 2; position.ratio = 0.2
            reader.display(position); await reader.flush()
            let book = reader.book, content = reader.content, chapters = reader.chapters, index = reader.chapterIndex
            let before = try await feature.library.snapshot()
            await runtime.setCatalog([.init(name: "第一章 猫", chapterUrl: "new-a", index: 0),
                                      .init(name: "第1章 猫", chapterUrl: "new-b", index: 1)])
            reader.refreshCatalog(); await wait { !reader.loading && reader.sourceSelection != nil }
            XCTAssertEqual(reader.sourceSelection?.source, feature.sources.first { $0.id == "a" })
            if revisionChange { await runtime.setRevision(2, source: "a") }
            else { await runtime.disableSource("a") }
            try await feature.refreshSources()
            XCTAssertNil(reader.sourceSelection); XCTAssertFalse(reader.loading)
            XCTAssertEqual(reader.error, "当前书源已更新或停用，请重新载入")
            let calls = await runtime.calls
            reader.selectSourceChapter(1); reader.resume()
            XCTAssertFalse(reader.loading)
            await wait { !reader.loading }; await reader.flush()
            let after = await runtime.calls, persisted = try await feature.library.snapshot()
            XCTAssertEqual(after, calls, "An old picker cannot request a chapter from the retired catalog")
            XCTAssertEqual(reader.book, book); XCTAssertEqual(reader.content, content)
            XCTAssertEqual(reader.chapters, chapters); XCTAssertEqual(reader.chapterIndex, index)
            XCTAssertEqual(reader.anchor, position); XCTAssertEqual(persisted.entries, before.entries)
            await feature.close()
        }
    }
    func testAmbiguousCandidateSelectionIsInvalidatedWhenOnlyTheCandidateSourceChangesOrIsDisabled() async throws {
        for revisionChange in [true, false] {
            let (feature, runtime, _) = try await fixture()
            let reader = feature.makeReader(book: .init(installationId: "a", bookUrl: "book-a", name: "猫"))
            reader.start(); await wait { !reader.loading && reader.content != nil }
            var position = try XCTUnwrap(reader.anchor); position.utf16Offset = 2; position.ratio = 0.2
            reader.display(position); await reader.flush()
            let book = reader.book, content = reader.content, chapters = reader.chapters, index = reader.chapterIndex
            let source = try XCTUnwrap(feature.sources.first { $0.id == "a" })
            let before = try await feature.library.snapshot()
            await runtime.setSwitchMode(.ambiguous)
            reader.switchSource(.init(installationId: "b", bookUrl: "book-b", name: "猫", author: "林"))
            await wait { !reader.loading && reader.sourceSelection != nil }
            XCTAssertEqual(reader.sourceSelection?.source, feature.sources.first { $0.id == "b" })
            if revisionChange { await runtime.setRevision(2, source: "b") }
            else { await runtime.disableSource("b") }
            try await feature.refreshSources()
            XCTAssertEqual(feature.sources.first { $0.id == "a" }, source)
            XCTAssertNil(reader.sourceSelection); XCTAssertFalse(reader.loading)
            XCTAssertEqual(reader.error, "当前书源已更新或停用，请重新载入")
            let calls = await runtime.calls
            reader.selectSourceChapter(1); reader.resume()
            XCTAssertFalse(reader.loading)
            await wait { !reader.loading }; await reader.flush()
            let after = await runtime.calls, persisted = try await feature.library.snapshot()
            XCTAssertEqual(after, calls, "An old candidate picker cannot request or replay a source switch")
            XCTAssertEqual(reader.book, book); XCTAssertEqual(reader.content, content)
            XCTAssertEqual(reader.chapters, chapters); XCTAssertEqual(reader.chapterIndex, index)
            XCTAssertEqual(reader.anchor, position); XCTAssertEqual(persisted.entries, before.entries)
            await feature.close()
        }
    }
}

private actor NovelSessionTransform {
    struct Call: Sendable {
        let sourceID: String
        let rules: [NovelRule]
        let conversion: String
    }
    private let rules: NovelRuleProcessor
    private var block = false, fail = false
    private var gate: CheckedContinuation<Void, Never>?
    private var arrived: CheckedContinuation<Void, Never>?
    private(set) var calls: [Call] = []
    init(rules: NovelRuleProcessor) { self.rules = rules }
    func blockNext(failing: Bool = false) { block = true; fail = failing }
    func failNext() { fail = true }
    func waitForTransform() async {
        if gate != nil { return }
        await withCheckedContinuation { arrived = $0 }
    }
    func release() { gate?.resume(); gate = nil }
    func transform(_ content: NovelContent, book: NovelBook, source: NovelSource,
                   rules: [NovelRule], conversion: String) async throws -> NovelContent {
        calls.append(.init(sourceID: source.id, rules: rules, conversion: conversion))
        let blocked = block, failing = fail; block = false; fail = false
        if blocked {
            await withCheckedContinuation { gate = $0; arrived?.resume(); arrived = nil }
        }
        if failing { throw NovelError.invalidResponse }
        return try await self.rules.transform(content, book: book, source: source, rules: rules, conversion: conversion)
    }
}

actor NovelSessionRuntime: NovelRuntimeServing {
    enum SwitchMode { case unique, ambiguous, failure }
    var calls: [RuntimeValue] = []
    private var failedSearch = "", blockedSearch = "", blockedBody = false, failedBody = false, paid = false, unavailable = false
    private var searchGate: CheckedContinuation<Void, Never>?, bodyGate: CheckedContinuation<Void, Never>?
    private var searchArrived: CheckedContinuation<Void, Never>?, bodyArrived: CheckedContinuation<Void, Never>?
    private var switchMode = SwitchMode.unique
    private var revision = 1, authors: [String: String] = [:]
    private var sourceRevisions: [String: Int] = [:]
    private var disabledSources: Set<String> = []
    private var catalogOverride: [NovelChapter]?
    private var bodyText: String?
    private var purchaseContextFixture = false, advancesPaidBodyContext = false
    func usePurchaseContextFixture() { purchaseContextFixture = true }
    func advancePaidBodyContext() { advancesPaidBodyContext = true }
    func setUnavailable() { unavailable = true }
    func failNextBody() { failedBody = true }
    func setBodyText(_ text: String) { bodyText = text }
    func setRevision(_ value: Int) { revision = value }
    func setRevision(_ value: Int, source: String) { sourceRevisions[source] = value }
    func disableSource(_ source: String) { disabledSources.insert(source) }
    func setAuthor(_ author: String, source: String) { authors[source] = author }
    func setCatalog(_ chapters: [NovelChapter]) { catalogOverride = chapters }
    func setActivityHandler(_ handler: (@Sendable (Bool) -> Void)?) {}
    func hasActiveCalls() -> Bool { false }
    func close() {}
    func image(_ reference: String) async throws -> Data { throw NovelError.invalidResponse }
    func failSearch(_ source: String) { failedSearch = source }
    func blockSearch(_ keyword: String) { blockedSearch = keyword }
    func waitForSearch() async { if searchGate != nil { return }; await withCheckedContinuation { searchArrived = $0 } }
    func releaseSearch() { searchGate?.resume(); searchGate = nil; blockedSearch = "" }
    func blockBody() { blockedBody = true }
    func unblockFutureBodies() { blockedBody = false }
    func waitForBody() async { if bodyGate != nil { return }; await withCheckedContinuation { bodyArrived = $0 } }
    func releaseBody() { bodyGate?.resume(); bodyGate = nil; blockedBody = false }
    func setSwitchMode(_ mode: SwitchMode) { switchMode = mode }
    func purchaseCount() -> Int { calls.filter { $0["action"] == .string("payAction") }.count }
    func call(_ operation: String, _ parameters: RuntimeValue) async throws -> RuntimeValue {
        guard !unavailable else { throw NovelError.closed }
        if operation == "library.list" {
            var sources = [NovelRuleProcessorTests.source(), NovelRuleProcessorTests.source("b")]
            for index in sources.indices {
                sources[index].revision = sourceRevisions[sources[index].id] ?? revision
                if disabledSources.contains(sources[index].id) { sources[index].enabled = false }
            }
            return try .encoding(["sources": sources])
        }
        guard operation == "library.invoke" else { throw NovelError.invalidResponse }
        calls.append(parameters)
        let source = parameters["installationId"].string ?? "a", action = parameters["action"].string ?? "", payload = parameters["payload"]
        switch action {
        case "search":
            let word = payload["keyword"].string ?? "", page = Int(payload["page"].number ?? 1)
            if source == failedSearch { throw NovelError.invalidResponse }
            if word == blockedSearch {
                await withCheckedContinuation { searchGate = $0; searchArrived?.resume(); searchArrived = nil }
            }
            let books: [RuntimeValue] = page > 2 ? [] : [.object(["bookUrl": .string("book-\(source)-\(page)"), "name": .string(word), "author": .string(page == 1 ? "林" : "周")])]
            return .object(["books": .array(books)])
        case "bookInfo":
            guard case .object(var book) = payload["book"] else { throw NovelError.invalidResponse }
            book["author"] = .string(authors[source] ?? "林"); book["variable"] = .string(#"{"info":"saved"}"#)
            book["tocHtml"] = .string("<ul>cached catalog</ul>")
            book["ruleSpecific"] = .object(["nested": .array([.null, .number(2), .bool(true)])])
            return .object(["book": .object(book)])
        case "toc":
            if source == "b", switchMode == .failure { throw NovelError.noChapter }
            let names = source == "b" && switchMode == .ambiguous ? ["第一章 猫", "第1章 猫"] : ["第一章 猫", "第二章 夜"]
            let chapters = names.enumerated().map { index, name -> RuntimeValue in
                var fields: RuntimeObject = ["name": .string(name), "chapterUrl": .string("chapter-\(source)-\(index)"),
                    "index": .number(Double(index)), "isPay": .bool(paid), "isVip": .bool(true), "variable": .string(#"{"chapter":"zero"}"#),
                    "title": .string("规则标题"), "url": .string("rule-url"), "bookUrl": payload["book"]["bookUrl"],
                    "baseUrl": .string("https://fixture.invalid/base/"), "updateTime": .string("卷首正文"), "time": .number(123), "tag": .array([.string("卷一")])]
                fields["isVolume"] = .bool(false)
                if purchaseContextFixture, paid, index == 0 {
                    fields["name"] = .string("第一章 猫（已购目录）")
                    fields["title"] = .string("刷新目录规则标题")
                }
                return .object(fields)
            }
            guard case .object(var book) = payload["book"] else { throw NovelError.invalidResponse }
            book["variable"] = .string(#"{"toc":"saved"}"#)
            let refreshed = purchaseContextFixture && paid ? Array(chapters.reversed()) : chapters
            return .object(["chapters": .array(catalogOverride?.map(\.wire) ?? refreshed), "book": .object(book)])
        case "content":
            if blockedBody { await withCheckedContinuation { bodyGate = $0; bodyArrived?.resume(); bodyArrived = nil } }
            if failedBody { failedBody = false; throw NovelError.invalidResponse }
            var chapter = payload["chapter"]
            if case .object(var fields) = chapter {
                if !purchaseContextFixture || !paid { fields["variable"] = .string(#"{"body":"saved"}"#) }
                if purchaseContextFixture, !paid {
                    fields["contentContext"] = .object(["nested": .array([.string("content"), .null, .number(1)])])
                }
                if purchaseContextFixture, paid, advancesPaidBodyContext {
                    fields["variable"] = .object(["stage": .string("content"), "version": .number(3)])
                    fields["retryContext"] = .array([.string("content"), .null, .bool(true)])
                }
                chapter = .object(fields)
            }
            return .object(["title": .string("第一章 猫"), "chapter": chapter, "blocks": .array([.object(["type": .string("text"), "text": .string(bodyText ?? (paid ? "已购正文" : "正文猫的旅行继续在夜色之中"))])])])
        case "payAction":
            paid = true
            if purchaseContextFixture {
                return .object(["chapter": .object([
                    "payContext": .object(["nested": .object(["receipt": .string("paid"), "accepted": .bool(true)])]),
                    "variable": .object(["stage": .string("paid"), "version": .number(2)]),
                    "name": .string("支付前旧标题"), "title": .string("支付前旧规则标题"),
                    "isPay": .bool(false), "time": .number(456)
                ])])
            }
            return .bool(true)
        default: throw NovelError.invalidResponse
        }
    }
}
