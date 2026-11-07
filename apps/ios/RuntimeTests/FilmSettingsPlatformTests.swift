#if os(iOS)
import XCTest
import UIKit
import Combine
import UniformTypeIdentifiers
import MeowJSON
import MeowStorage
@testable import MeowFilmFeature
@testable import MeowUI

/// Exercises actual local owners and UIKit. The RPC boundary is a fixture, not Node evidence.
@MainActor
final class FilmSettingsPlatformTests: XCTestCase {
    private var hostedWindows: [(window: UIWindow, previousKeyWindow: UIWindow?)] = []

    override func setUp() async throws {
        try await super.setUp()
        _ = try await hostResources()
        _ = try await foregroundScene()
    }

    override func tearDown() async throws {
        // Also restore the App window if a throwing assertion interrupted a test.
        while let entry = hostedWindows.last { unhost(entry.window) }
        try await super.tearDown()
    }

    func testDraftSurvivesNavigationAndWholeDraftSaveRestoreAndTestNeverSaves() async throws {
        let fixture = try FilmSettingsFixture()
        defer { fixture.removeFiles() }
        let rules = fixture.rules
        try await rules.initialize()
        let owner = FilmRulesDraft(service: rules)
        try await owner.load()
        let initial = try XCTUnwrap(owner.draft)
        XCTAssertFalse(initial.episodeCleanRegexRules.isEmpty)
        XCTAssertFalse(initial.movieRules.isEmpty)
        let editor = FilmRuleEditorController(owner: owner, group: .episodeClean, index: 0, themeProvider: FilmSettingsTheme())
        let host = try await host(editor)
        defer { unhost(host) }
        editor.pattern.text = "^草稿"
        editor.textViewDidChange(editor.pattern)
        let section = FilmRuleSectionController(owner: owner, section: .movie, themeProvider: FilmSettingsTheme())
        section.loadViewIfNeeded()
        let reopened = FilmRuleEditorController(owner: owner, group: .episodeClean, index: 0, themeProvider: FilmSettingsTheme())
        XCTAssertEqual(reopened.pattern.text, "^草稿")
        owner.edit(.movie, index: 0, rule: .init(pattern: "(电影)", replace: "$1", flags: "i"))
        try await owner.save()
        let saved = try await rules.savedSnapshot()
        XCTAssertEqual(saved.episodeCleanRegexRules.first, "^草稿")
        XCTAssertEqual(saved.movieRules.first?.flags, "i")
        owner.edit(.episodeClean, index: 0, rule: .init(pattern: "^保留"))
        try await owner.restore(.movie)
        let restored = try await rules.savedSnapshot()
        XCTAssertEqual(restored.episodeCleanRegexRules.first, "^保留")
        XCTAssertNotEqual(restored.movieRules.first?.pattern, "(电影)")
        owner.edit(.movie, index: 0, rule: .init(pattern: "["))
        let before = try fixture.rulesStore.read(key: FilmRuleService.documentKey)
        let test = FilmRuleTestController(owner: owner, section: .movie, themeProvider: FilmSettingsTheme())
        test.loadViewIfNeeded(); test.input.text = "电影 2026"; test.runTest()
        await eventually { !test.testing && test.result != nil }
        XCTAssertFalse(try XCTUnwrap(test.result).errors.isEmpty)
        XCTAssertEqual(try fixture.rulesStore.read(key: FilmRuleService.documentKey), before)
        XCTAssertEqual(owner.rules(.movie).first?.pattern, "[")
        await rules.close(); await fixture.sources.close()
    }

    func testRulesSaveFailureRetainsDraftAndRetryCommitsWithoutLosingOtherGroups() async throws {
        let fixture = try FilmSettingsFixture(); defer { fixture.removeFiles() }
        try await fixture.rules.initialize()
        let owner = FilmRulesDraft(service: fixture.rules); try await owner.load()
        let bytes = try XCTUnwrap(fixture.rulesStore.read(key: FilmRuleService.documentKey))
        owner.edit(.episodeClean, index: 0, rule: .init(pattern: "^修改"))
        try fixture.rulesStore.write(Data("{}".utf8), key: FilmRuleService.documentKey)
        do { try await owner.save(); XCTFail("CAS conflict must remain a failure") } catch {}
        XCTAssertEqual(owner.rules(.episodeClean).first?.pattern, "^修改")
        let saved = try await fixture.rules.savedSnapshot()
        XCTAssertNotEqual(saved.episodeCleanRegexRules.first, "^修改")
        try fixture.rulesStore.write(bytes, key: FilmRuleService.documentKey)
        try await owner.save()
        let committed = try await fixture.rules.savedSnapshot()
        XCTAssertEqual(committed.episodeCleanRegexRules.first, "^修改")
        await fixture.rules.close(); await fixture.sources.close()
    }

    func testModelStartsIndependentLocalOwnersAndCloseDoesNotCloseServices() async throws {
        let fixture = try FilmSettingsFixture(); defer { fixture.removeFiles() }
        let model = FilmSettingsModel(rules: fixture.rules, sources: fixture.sources)
        model.start()
        await eventually { model.rules.draft != nil && model.snapshot != nil }
        let calls = await fixture.runtime.calls
        XCTAssertTrue(calls.isEmpty, "Model cannot launch Node or run detection")
        model.rules.edit(.episodeClean, index: 0, rule: .init(pattern: "长期草稿"))
        model.close(); model.start()
        await eventually { model.snapshot != nil }
        XCTAssertEqual(model.rules.rules(.episodeClean).first?.pattern, "长期草稿")
        model.close()
        _ = try await fixture.rules.savedSnapshot()
        try await fixture.sources.initialize()
        await fixture.rules.close(); await fixture.sources.close()
    }

    func testFilePickerSnapshotRejectsMalformedUTF8AndKeepsCopiedContent() async throws {
        let base = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: base, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: base) }
        let file = base.appendingPathComponent("猫源.js")
        let text = "// 猫源\nconst name = '影视';"
        try Data(text.utf8).write(to: file)
        let snapshot = try await FilmScriptFileSnapshot.read(file)
        try Data("changed".utf8).write(to: file)
        XCTAssertEqual(snapshot.source, text)
        XCTAssertEqual(snapshot.fileName, "猫源.js")
        try Data([0xff, 0xfe, 0x61]).write(to: file)
        let link = base.appendingPathComponent("link.js")
        try FileManager.default.createSymbolicLink(at: link, withDestinationURL: file)
        for invalid in [file, base.appendingPathComponent("script.txt"), link] {
            do { _ = try await FilmScriptFileSnapshot.read(invalid); XCTFail("Invalid file must be rejected") }
            catch {}
        }
        let oversized = base.appendingPathComponent("large.js")
        FileManager.default.createFile(atPath: oversized.path, contents: nil)
        let handle = try FileHandle(forWritingTo: oversized)
        try handle.truncate(atOffset: UInt64(ImportedFileReader.maximumSupportedBytes + 1))
        try handle.close()
        do { _ = try await FilmScriptFileSnapshot.read(oversized); XCTFail("File read resource boundary") }
        catch { XCTAssertEqual(error as? ImportedFileError, .tooLarge) }
    }

    func testVideoOptionsAreDurableExclusiveOrderedAndFailedSaveDoesNotPublish() async throws {
        let fixture = try FilmSettingsFixture(); defer { fixture.removeFiles() }
        try await fixture.sources.refresh()
        let first = FilmSourceKey(installationId: "a", siteKey: "site")
        let second = FilmSourceKey(installationId: "b", siteKey: "site")
        let page = FilmVideoDetailController(service: fixture.sources, key: first, themeProvider: FilmSettingsTheme())
        let window = try await host(page); defer { unhost(window) }
        await eventually { page.snapshot?.videos.count == 2 }
        page.saveOptions(.init(enabled: false, homeVisible: false, searchEnabled: false, aggregateImages: true), order: -2)
        await eventually { !page.busy && page.snapshot?.videos.first?.order == -2 }
        let options = try XCTUnwrap(page.snapshot?.videos.first).options
        XCTAssertEqual(options, .init(enabled: false, homeVisible: false, searchEnabled: false, aggregateImages: true))
        try await fixture.sources.saveVideoOptions(second, options: .init(aggregateImages: true), order: -3)
        await eventually { page.snapshot?.videos.first?.key == second }
        XCTAssertEqual(page.snapshot?.videos.first(where: { $0.key == first })?.options.aggregateImages, false)
        let bytes = try XCTUnwrap(fixture.sourceStore.read(key: FilmSourceService.documentKey))
        try fixture.sourceStore.write(Data("{}".utf8), key: FilmSourceService.documentKey)
        let before = await fixture.sources.snapshot
        page.saveOptions(.init())
        await eventually { !page.busy }
        XCTAssertEqual(page.snapshot, before)
        await eventually { page.presentedViewController is UIAlertController }
        let alert = try XCTUnwrap(page.presentedViewController as? UIAlertController)
        XCTAssertEqual(alert.actions.map(\.title), ["取消", "重试"])
        await eventually { !alert.isBeingPresented && alert.view.window != nil }
        try fixture.sourceStore.write(bytes, key: FilmSourceService.documentKey)
        let rebuilt = FilmSourceService(transport: .init(rpc: { _, _, _ in throw FilmSourceError(code: "OFFLINE") }),
                                       storeFactory: { fixture.sourceStore })
        try await rebuilt.initialize()
        let snapshot = await rebuilt.snapshot
        XCTAssertEqual(snapshot.videos, before.videos)
        await rebuilt.close(); await fixture.sources.close(); await fixture.rules.close()
    }

    func testVideoDetectionCancellationOnHiddenPageDoesNotPublishLateResult() async throws {
        let fixture = try FilmSettingsFixture(); defer { fixture.removeFiles() }
        try await fixture.sources.refresh()
        await fixture.runtime.pauseProbes()
        let key = FilmSourceKey(installationId: "a", siteKey: "site")
        let page = FilmVideoDetailController(service: fixture.sources, key: key, themeProvider: FilmSettingsTheme())
        let window = try await host(page); defer { unhost(window) }
        await eventually { page.snapshot?.videos.count == 2 }
        page.detect()
        await fixture.runtime.waitForProbe()
        XCTAssertTrue(page.detecting)
        attach(page, "film-video-detecting")
        page.pageDidBecomeHidden()
        XCTAssertFalse(page.detecting)
        await fixture.runtime.releaseProbe()
        let snapshot = await fixture.sources.snapshot
        XCTAssertNil(snapshot.videos.first { $0.key == key }?.detection)
        XCTAssertTrue(snapshot.videos.allSatisfy { $0.options == FilmVideoOptions() })
        await fixture.sources.close(); await fixture.rules.close()
    }

    func testCatActionsSelectionConfirmationAndDisabledParent() async throws {
        let fixture = try FilmSettingsFixture(); defer { fixture.removeFiles() }
        try await fixture.sources.refresh()
        let cats = FilmCatSourcesViewController(service: fixture.sources, themeProvider: FilmSettingsTheme())
        let window = try await host(cats); defer { unhost(window) }
        await eventually { cats.snapshot?.cats.count == 2 }
        try row(cats, "film.cats.select").action?()
        try row(cats, "film.cats.selectAll").action?()
        XCTAssertTrue(try row(cats, "film.cats.batch.4").enabled)
        try row(cats, "film.cats.batch.4").action?()
        let alert = try XCTUnwrap(cats.presentedViewController as? UIAlertController)
        XCTAssertEqual(alert.actions.map(\.style), [.cancel, .destructive])
        let calls = await fixture.runtime.calls
        XCTAssertFalse(calls.contains("uninstall"), "Showing confirmation is not deletion consent")
        await eventually { !alert.isBeingPresented && alert.view.window != nil }
        let detail = FilmCatDetailController(service: fixture.sources, installationId: "a", themeProvider: FilmSettingsTheme())
        detail.loadViewIfNeeded(); detail.beginAppearanceTransition(true, animated: false); detail.endAppearanceTransition()
        await eventually { detail.snapshot?.cats.count == 2 }
        for action in ["edit", "update", "restart", "delete", "enabled"] {
            XCTAssertNotNil(try row(detail, "film.cat.\(action)").action)
        }
        _ = try await fixture.sources.enableCat("a", enabled: false)
        await eventually { detail.snapshot?.cats.first?.enabled == false }
        XCTAssertFalse(try row(detail, "film.cat.restart").enabled)
        let disabled = await fixture.sources.snapshot
        XCTAssertEqual(disabled.videos.count, 2)
        PageViewController.invalidatePages(in: detail)
        await fixture.sources.close(); await fixture.rules.close()
    }

    func testNarrowWideLargeTypeAndEmptyScreensProduceRealUIKitAttachments() async throws {
        let fixture = try FilmSettingsFixture(); defer { fixture.removeFiles() }
        try await fixture.rules.initialize(); try await fixture.sources.initialize()
        let owner = FilmRulesDraft(service: fixture.rules); try await owner.load()
        for width: CGFloat in [320, 768] {
            let pages: [FilmSettingsListController] = [
                FilmRulesViewController(owner: owner, themeProvider: FilmSettingsTheme()),
                FilmRuleSectionController(owner: owner, section: .episode, themeProvider: FilmSettingsTheme()),
                FilmCatSourcesViewController(service: fixture.sources, themeProvider: FilmSettingsTheme()),
                FilmVideoSourcesViewController(service: fixture.sources, themeProvider: FilmSettingsTheme()),
            ]
            for (index, page) in pages.enumerated() {
                page.traitOverrides.preferredContentSizeCategory = .accessibilityExtraExtraExtraLarge
                let window = try await host(page, size: CGSize(width: width, height: 568))
                if let source = page as? FilmSourcePageController {
                    await eventually { source.snapshot != nil }
                }
                await settle()
                page.view.layoutIfNeeded()
                XCTAssertGreaterThan(page.table.bounds.width, 0)
                XCTAssertLessThanOrEqual(page.table.frame.maxX, page.view.bounds.maxX)
                XCTAssertGreaterThanOrEqual(page.table.frame.minY, page.pageHeader.frame.maxY)
                XCTAssertEqual(page.table.frame.maxY, page.view.safeAreaLayoutGuide.layoutFrame.maxY, accuracy: 1)
                for cell in page.table.visibleCells {
                    XCTAssertGreaterThanOrEqual(cell.bounds.height, 44)
                    XCTAssertFalse(cell.accessibilityIdentifier?.isEmpty ?? true)
                }
                attach(page, "film-settings-\(index)-\(Int(width))-AXXXL")
                if let lastSection = page.sections.indices.last,
                   let lastRow = page.sections[lastSection].rows.indices.last,
                   page.table.contentSize.height > page.table.bounds.height {
                    let last = IndexPath(row: lastRow, section: lastSection)
                    // Self-sizing rows update contentSize while UIKit lays them
                    // out. Walk actual rows instead of trusting estimated height.
                    for section in page.sections.indices {
                        for row in page.sections[section].rows.indices {
                            page.table.scrollToRow(at: IndexPath(row: row, section: section), at: .bottom, animated: false)
                            page.table.layoutIfNeeded()
                        }
                    }
                    page.table.scrollToRow(at: last, at: .bottom, animated: false)
                    page.table.layoutIfNeeded()
                    XCTAssertGreaterThan(page.table.contentOffset.y, 0)
                    XCTAssertTrue(page.table.indexPathsForVisibleRows?.contains(last) == true)
                    attach(page, "film-settings-\(index)-\(Int(width))-AXXXL-bottom")
                }
                unhost(window)
            }
        }
        await fixture.sources.close(); await fixture.rules.close()
    }

    func testSavingClearedLastAndMiddleRuleEndsEditorWithoutChangingSuccessor() async throws {
        let fixture = try FilmSettingsFixture(); defer { fixture.removeFiles() }
        try await fixture.rules.initialize()
        for index in [2, 1] {
            _ = try await fixture.rules.save(.init(episodeCleanRegexRules: ["first", "middle", "last"],
                                                   movieRules: [.init(pattern: "movie")]))
            let owner = FilmRulesDraft(service: fixture.rules); try await owner.load()
            owner.edit(.movie, index: 0, rule: .init(pattern: "other draft"))
            let list = FilmRuleSectionController(owner: owner, section: .episode, themeProvider: FilmSettingsTheme())
            let navigation = PageNavigationController(themeProvider: FilmSettingsTheme())
            navigation.setViewControllers([list], animated: false)
            let window = try await host(navigation)
            try selectRow(list, "film.rules.episodeClean.\(index)")
            await eventually { navigation.topViewController is FilmRuleEditorController }
            let editor = try XCTUnwrap(navigation.topViewController as? FilmRuleEditorController)
            await eventually { editor.canPresentPageAlert }
            // UITextView's actual editing path, followed by the table's save action.
            editor.pattern.becomeFirstResponder()
            editor.pattern.selectedRange = NSRange(location: 0, length: (editor.pattern.text as NSString).length)
            editor.pattern.deleteBackward()
            try selectRow(editor, "film.rule.save")
            await eventually { navigation.topViewController === list && list.canPresentPageAlert }
            await eventually { !editor.isPageActive }
            XCTAssertNil(editor.ruleIndex)
            XCTAssertFalse(editor.pattern.isEditable)
            XCTAssertFalse(editor.sections.flatMap(\.rows).contains { $0.id == "film.rule.save" })
            let expected = index == 2 ? ["first", "middle"] : ["first", "last"]
            XCTAssertEqual(owner.draft?.episodeCleanRegexRules, expected)
            let saved = try await fixture.rules.savedSnapshot()
            XCTAssertEqual(saved.episodeCleanRegexRules, expected)
            XCTAssertEqual(saved.movieRules.first?.pattern, "other draft")
            // Continue using a freshly selected target after normalization.
            try selectRow(list, "film.rules.episodeClean.1")
            await eventually { navigation.topViewController is FilmRuleEditorController }
            let next = try XCTUnwrap(navigation.topViewController as? FilmRuleEditorController)
            XCTAssertEqual(next.pattern.text, expected[1])
            unhost(window)
        }
        await fixture.rules.close(); await fixture.sources.close()
    }

    func testCatBatchDeleteRetryContinuesAfterReceiptAndCancelDoesNotDeleteMore() async throws {
        for retry in [true, false] {
            let fixture = try FilmSettingsFixture(); defer { fixture.removeFiles() }
            try await fixture.sources.refresh()
            await fixture.runtime.failDeletionOnce("b")
            let page = FilmCatSourcesViewController(service: fixture.sources, themeProvider: FilmSettingsTheme())
            let window = try await host(page)
            await eventually { page.snapshot?.cats.count == 2 && page.canPresentPageAlert }
            try selectRow(page, "film.cats.select")
            try selectRow(page, "film.cats.selectAll")
            try selectRow(page, "film.cats.batch.4")
            try await activateAlert(page, title: "确认")
            await eventually { !page.busy && (page.presentedViewController as? UIAlertController)?.title == "错误" }
            let partial = await fixture.sources.snapshot
            XCTAssertEqual(partial.cats.map(\.installationId), ["b"])
            try await activateAlert(page, title: retry ? "重试" : "取消")
            await eventually { !page.busy && page.presentedViewController == nil }
            let calls = await fixture.runtime.deletions
            XCTAssertEqual(calls, retry ? ["a", "b", "b"] : ["a", "b"])
            let completed = await fixture.sources.snapshot
            XCTAssertEqual(completed.cats.map(\.installationId), retry ? [] : ["b"])
            unhost(window)
            await fixture.sources.close(); await fixture.rules.close()
        }
    }

    func testVideoBatchPersistenceFailureRetryRetainsCompletedReceipts() async throws {
        let fixture = try FilmSettingsFixture(); defer { fixture.removeFiles() }
        try await fixture.sources.refresh()
        let snapshot = await fixture.sources.snapshot
        let keys = snapshot.videos.map(\.key)
        let page = FilmVideoBatchFailurePage(service: fixture.sources, store: fixture.sourceStore,
                                            keys: keys, themeProvider: FilmSettingsTheme())
        let window = try await host(page); defer { unhost(window) }
        await eventually { page.canPresentPageAlert }
        try selectRow(page, "fixture.deleteVideos")
        try await activateAlert(page, title: "确认")
        await eventually { !page.busy && (page.presentedViewController as? UIAlertController)?.title == "错误" }
        XCTAssertEqual(page.attempts, keys)
        let partial = await fixture.sources.snapshot
        XCTAssertEqual(partial.videos.map(\.key), [keys[1]])
        try fixture.sourceStore.write(try XCTUnwrap(page.committedBytes), key: FilmSourceService.documentKey)
        try await activateAlert(page, title: "重试")
        await eventually { !page.busy && page.presentedViewController == nil }
        XCTAssertEqual(page.attempts, [keys[0], keys[1], keys[1]])
        let completed = await fixture.sources.snapshot
        XCTAssertTrue(completed.videos.isEmpty)
        await fixture.sources.close(); await fixture.rules.close()
    }

    func testCatBatchDeleteRetrySkipsCommittedDeletionWhenFollowingStatusFails() async throws {
        let fixture = try FilmSettingsFixture(); defer { fixture.removeFiles() }
        try await fixture.sources.refresh()
        await fixture.runtime.failStatusAfterDeletion("a")
        let page = FilmCatSourcesViewController(service: fixture.sources, themeProvider: FilmSettingsTheme())
        let window = try await host(page); defer { unhost(window) }
        await eventually { page.snapshot?.cats.count == 2 && page.canPresentPageAlert }
        try selectRow(page, "film.cats.select")
        try selectRow(page, "film.cats.selectAll")
        try selectRow(page, "film.cats.batch.4")
        try await activateAlert(page, title: "确认")
        await eventually { !page.busy && (page.presentedViewController as? UIAlertController)?.title == "错误" }
        let alert = try XCTUnwrap(page.presentedViewController as? UIAlertController)
        XCTAssertEqual(alert.message, "STATUS_FAILED")
        let partial = await fixture.sources.snapshot
        XCTAssertEqual(partial.cats.map(\.installationId), ["b"])
        let firstCalls = await fixture.runtime.deletions
        XCTAssertEqual(firstCalls, ["a"])
        try await activateAlert(page, title: "重试")
        await eventually { !page.busy && page.snapshot?.cats.isEmpty == true }
        let calls = await fixture.runtime.deletions
        XCTAssertEqual(calls, ["a", "b"], "Committed A must not be sent to deleteCat again")
        XCTAssertNil(page.presentedViewController)
        await fixture.sources.close(); await fixture.rules.close()
    }

    func testVideoListBatchConfirmationCancelsThenDeletesSelectedRows() async throws {
        let fixture = try FilmSettingsFixture(); defer { fixture.removeFiles() }
        try await fixture.sources.refresh()
        let page = FilmVideoSourcesViewController(service: fixture.sources, themeProvider: FilmSettingsTheme())
        let window = try await host(page); defer { unhost(window) }
        await eventually { page.snapshot?.videos.count == 2 && page.canPresentPageAlert }
        try selectRow(page, "film.videos.select")
        try selectRow(page, "film.videos.selectAll")
        try selectRow(page, "film.videos.delete")
        try await activateAlert(page, title: "取消")
        let cancelled = await fixture.sources.snapshot
        XCTAssertEqual(cancelled.videos.count, 2)
        try selectRow(page, "film.videos.delete")
        try await activateAlert(page, title: "确认")
        await eventually { !page.busy && page.snapshot?.videos.isEmpty == true }
        let saved = await fixture.sources.snapshot
        XCTAssertTrue(saved.videos.isEmpty)
        await fixture.sources.close(); await fixture.rules.close()
    }

    func testBatchDetectionRemainsCancellableAfterDeselectingEveryRow() async throws {
        let fixture = try FilmSettingsFixture(); defer { fixture.removeFiles() }
        try await fixture.sources.refresh(); await fixture.runtime.pauseProbes()
        let page = FilmVideoSourcesViewController(service: fixture.sources, themeProvider: FilmSettingsTheme())
        let window = try await host(page); defer { unhost(window) }
        await eventually { page.snapshot?.videos.count == 2 }
        try selectRow(page, "film.videos.select")
        try selectRow(page, "film.videos.selectAll")
        try selectRow(page, "film.videos.detect")
        await fixture.runtime.waitForProbe()
        for id in ["a", "b"] { try selectRow(page, "film.video.\(id).site") }
        XCTAssertTrue(page.detecting)
        XCTAssertTrue(try row(page, "film.videos.detect").enabled)
        XCTAssertEqual(try row(page, "film.videos.detect").title, "取消检测")
        try selectRow(page, "film.videos.detect")
        XCTAssertFalse(page.detecting)
        await fixture.runtime.waitForProbeCancellation()
        await fixture.runtime.releaseProbe()
        // The transport acknowledged page cancellation before this drain. Drain
        // now makes the assertion observe the settled late result, not a race.
        await fixture.sources.prepareShutdown()
        let snapshot = await fixture.sources.snapshot
        XCTAssertTrue(snapshot.videos.allSatisfy { $0.detection == nil })
        try await fixture.sources.resume()
        try selectRow(page, "film.video.a.site")
        XCTAssertTrue(try row(page, "film.videos.detect").enabled)
        XCTAssertEqual(try row(page, "film.videos.detect").title, "检测")
        await fixture.sources.close(); await fixture.rules.close()
    }

    func testReturningFromAcceptedLocalBatchUpdateDoesNotPresentOrSubmitNextItem() async throws {
        for secondLocal in [true, false] {
            let fixture = try FilmSettingsFixture(); defer { fixture.removeFiles() }
            await fixture.runtime.setLocal(secondLocal ? ["a", "b"] : ["a"])
            try await fixture.sources.refresh()
            await fixture.runtime.pauseUpdates()
            let page = FilmCatSourcesViewController(service: fixture.sources, themeProvider: FilmSettingsTheme())
            let navigation = PageNavigationController(themeProvider: FilmSettingsTheme())
            navigation.setViewControllers([page], animated: false)
            let window = try await host(navigation)
            await eventually { page.snapshot?.cats.count == 2 }
            try selectRow(page, "film.cats.select")
            try selectRow(page, "film.cats.selectAll")
            try selectRow(page, "film.cats.batch.2")
            await eventually { navigation.topViewController is FilmCatEditorController }
            let editor = try XCTUnwrap(navigation.topViewController as? FilmCatEditorController)
            await eventually { editor.isPageVisible }
            // Visibility starts at willAppear; wait for natural push completion
            // before the test submits and attempts its return.
            await eventually { editor.canPresentPageAlert }
            let file = fixture.base.appendingPathComponent("update.js")
            try Data("// changed source".utf8).write(to: file)
            // Use the document picker's actual completion path, then the save row.
            let picker = UIDocumentPickerViewController(forOpeningContentTypes: [.javaScript])
            editor.documentPicker(picker, didPickDocumentsAt: [file])
            await eventually { editor.file != nil }
            try selectRow(editor, "film.cat.editor.save")
            await fixture.runtime.waitForUpdate()
            navigation.popViewController(animated: false)
            await eventually { !editor.isPageActive && navigation.topViewController === page }
            await fixture.runtime.releaseUpdate()
            await eventually {
                editor.sections.flatMap(\.rows).first { $0.id == "film.cat.editor.save" }?.title == "保存"
            }
            await settle()
            XCTAssertTrue(navigation.topViewController === page)
            let updates = await fixture.runtime.updates
            XCTAssertEqual(updates, ["a"])
            let result = await fixture.sources.snapshot
            XCTAssertEqual(result.cats.first { $0.installationId == "a" }?.updateState, "updated")
            XCTAssertNil(editor.onSaved)
            unhost(window)
            await fixture.sources.close(); await fixture.rules.close()
        }
    }

    func testRuntimeFailureRetriesFromItsAlertAndCanRetryAgain() async throws {
        let fixture = try FilmSettingsFixture(); defer { fixture.removeFiles() }
        await fixture.runtime.failStarts(2)
        do { try await fixture.sources.start(); XCTFail("First startup must fail") } catch {}
        let page = FilmCatSourcesViewController(service: fixture.sources, themeProvider: FilmSettingsTheme())
        let window = try await host(page); defer { unhost(window) }
        await eventually { page.snapshot?.runtimeError != nil && page.canPresentPageAlert }
        try selectRow(page, "film.sources.runtimeError")
        let alert = try XCTUnwrap(page.presentedViewController as? UIAlertController)
        XCTAssertEqual(alert.title, "运行器不可用")
        XCTAssertEqual(alert.actions.map(\.title), ["取消", "重试"])
        XCTAssertFalse(page.sections.flatMap(\.rows).contains { $0.id == "film.sources.retry" })
        try await activateAlert(page, title: "重试")
        await eventually { !page.busy && (page.presentedViewController as? UIAlertController)?.title == "错误" }
        try await activateAlert(page, title: "重试")
        await eventually { !page.busy && page.snapshot?.runtimeError == nil }
        let calls = await fixture.runtime.calls.filter { $0 == "start" }
        XCTAssertEqual(calls.count, 3)
        await fixture.sources.close(); await fixture.rules.close()
    }

    func testRuleResultDisplayDoesNotReplaceLosslessBusinessText() async throws {
        let fixture = try FilmSettingsFixture(); defer { fixture.removeFiles() }
        try await fixture.rules.initialize()
        _ = try await fixture.rules.save(.init(movieRules: [.init(pattern: "^.", replace: "x")]))
        let owner = FilmRulesDraft(service: fixture.rules); try await owner.load()
        let page = FilmRuleTestController(owner: owner, section: .movie, themeProvider: FilmSettingsTheme())
        let window = try await host(page); defer { unhost(window) }
        let before = try fixture.rulesStore.read(key: FilmRuleService.documentKey)
        page.input.becomeFirstResponder(); page.input.insertText("🎬")
        try selectRow(page, "film.rule.test.run")
        await eventually { !page.testing && page.result != nil }
        let result = try XCTUnwrap(page.result)
        XCTAssertEqual(result.outputText.utf16, [0x0078, 0xDFAC])
        XCTAssertEqual(try row(page, "film.rule.test.output").title, "x\u{FFFD}")
        XCTAssertEqual(page.input.text, "🎬")
        XCTAssertEqual(owner.rules(.movie).first?.pattern, "^.")
        XCTAssertEqual(try fixture.rulesStore.read(key: FilmRuleService.documentKey), before)
        await fixture.rules.close(); await fixture.sources.close()
    }

    func testCatBatchRestartRetriesOnlyUnconfirmedItemsAndCancelDoesNotRestartMore() async throws {
        for retries in [0, 1, 2] {
            let fixture = try FilmSettingsFixture(); defer { fixture.removeFiles() }
            try await fixture.sources.refresh()
            await fixture.runtime.failRestarts("b", count: max(1, retries))
            let page = FilmCatSourcesViewController(service: fixture.sources, themeProvider: FilmSettingsTheme())
            let window = try await host(page)
            await eventually { page.snapshot?.cats.count == 2 && page.canPresentPageAlert }
            try selectRow(page, "film.cats.select")
            try selectRow(page, "film.cats.selectAll")
            try selectRow(page, "film.cats.batch.3")
            await eventually { !page.busy && (page.presentedViewController as? UIAlertController)?.title == "错误" }
            XCTAssertEqual((page.presentedViewController as? UIAlertController)?.message, "RESTART_FAILED")
            let firstCalls = await fixture.runtime.restarts
            XCTAssertEqual(firstCalls, ["a", "b"])
            if retries == 2 {
                try await activateAlert(page, title: "重试")
                await eventually { !page.busy && (page.presentedViewController as? UIAlertController)?.title == "错误" }
                XCTAssertEqual((page.presentedViewController as? UIAlertController)?.message, "RESTART_FAILED")
                let secondCalls = await fixture.runtime.restarts
                XCTAssertEqual(secondCalls, ["a", "b", "b"])
            }
            try await activateAlert(page, title: retries == 0 ? "取消" : "重试")
            await eventually { !page.busy && page.presentedViewController == nil }
            let calls = await fixture.runtime.restarts
            XCTAssertEqual(calls, ["a", "b"] + Array(repeating: "b", count: retries))
            let saved = await fixture.sources.snapshot
            XCTAssertEqual(saved.cats.map(\.installationId), ["a", "b"])
            XCTAssertEqual(saved.videos.map(\.key.installationId), ["a", "b"])
            XCTAssertTrue(saved.cats.allSatisfy(\.enabled))
            let rebuilt = FilmSourceService(transport: .init(rpc: { _, _, _ in throw FilmSourceError(code: "OFFLINE") }),
                                           storeFactory: { fixture.sourceStore })
            try await rebuilt.initialize()
            let persisted = await rebuilt.snapshot
            XCTAssertEqual(persisted.cats.map(\.installationId), ["a", "b"])
            XCTAssertTrue(persisted.cats.allSatisfy(\.enabled))
            XCTAssertEqual(persisted.cats.map(\.modifiedAt), [1_700_000_000_000, 1_700_000_000_000])
            XCTAssertEqual(persisted.videos, saved.videos)
            await rebuilt.close()
            unhost(window)
            await fixture.sources.close(); await fixture.rules.close()
        }
    }

    func testCatDetailCommittedDeletionStatusFailureRetriesOrCancelsWithoutAnotherUninstall() async throws {
        for choice in ["成功", "重试", "取消"] {
            let fixture = try FilmSettingsFixture(); defer { fixture.removeFiles() }
            try await fixture.sources.refresh()
            if choice != "成功" { await fixture.runtime.failStatusAfterDeletion("a") }
            let page = FilmCatSourcesViewController(service: fixture.sources, themeProvider: FilmSettingsTheme())
            let navigation = PageNavigationController(themeProvider: FilmSettingsTheme())
            navigation.setViewControllers([page], animated: false)
            let window = try await host(navigation)
            await eventually { page.snapshot?.cats.count == 2 }
            try selectRow(page, "film.cat.a")
            let detail = try XCTUnwrap(navigation.topViewController as? FilmCatDetailController)
            await eventually { detail.snapshot?.cats.count == 2 && detail.canPresentPageAlert }
            try selectRow(detail, "film.cat.delete")
            try await activateAlert(detail, title: "确认")
            if choice != "成功" {
                await eventually { !detail.busy && (detail.presentedViewController as? UIAlertController)?.title == "错误" }
                XCTAssertEqual((detail.presentedViewController as? UIAlertController)?.message, "STATUS_FAILED")
                XCTAssertTrue(navigation.topViewController === detail)
                try await assertOnlyCatBRemainsOnDisk(fixture)
                let initialCalls = await fixture.runtime.deletions
                XCTAssertEqual(initialCalls, ["a"])
                try await activateAlert(detail, title: choice)
            }
            if choice == "取消" {
                await eventually { !detail.busy && detail.presentedViewController == nil }
                XCTAssertTrue(navigation.topViewController === detail)
                navigation.popViewController(animated: false)
            }
            await eventually { navigation.topViewController === page && page.canPresentPageAlert }
            XCTAssertNil(detail.presentedViewController)
            await eventually { page.snapshot?.cats.map(\.installationId) == ["b"] }
            XCTAssertFalse(page.sections.flatMap(\.rows).contains { $0.id == "film.cat.a" })
            try await assertOnlyCatBRemainsOnDisk(fixture)
            // Re-enter an existing detail after the old deletion settles.
            try selectRow(page, "film.cat.b")
            let other = try XCTUnwrap(navigation.topViewController as? FilmCatDetailController)
            await eventually { other.canPresentPageAlert }
            XCTAssertEqual(other.installationId, "b")
            XCTAssertTrue(navigation.topViewController === other)
            let calls = await fixture.runtime.deletions
            XCTAssertEqual(calls, ["a"])
            unhost(window)
            await fixture.sources.close(); await fixture.rules.close()
        }
    }

    func testCatDetailDeletionReceiptAfterReturningDoesNotPopAnotherDetail() async throws {
        for statusFails in [false, true] {
            let fixture = try FilmSettingsFixture(); defer { fixture.removeFiles() }
            try await fixture.sources.refresh()
            await fixture.runtime.pauseDeletion()
            if statusFails { await fixture.runtime.failStatusAfterDeletion("a") }
            let page = FilmCatSourcesViewController(service: fixture.sources, themeProvider: FilmSettingsTheme())
            let navigation = PageNavigationController(themeProvider: FilmSettingsTheme())
            navigation.setViewControllers([page], animated: false)
            let window = try await host(navigation)
            await eventually { page.snapshot?.cats.count == 2 }
            try selectRow(page, "film.cat.a")
            let detail = try XCTUnwrap(navigation.topViewController as? FilmCatDetailController)
            await eventually { detail.snapshot?.cats.count == 2 && detail.canPresentPageAlert }
            try selectRow(detail, "film.cat.delete")
            try await activateAlert(detail, title: "确认")
            await fixture.runtime.waitForDeletion()
            XCTAssertTrue(detail.busy)
            navigation.popViewController(animated: false)
            await eventually { !detail.isPageActive && page.canPresentPageAlert }
            try selectRow(page, "film.cat.b")
            let other = try XCTUnwrap(navigation.topViewController as? FilmCatDetailController)
            await eventually { other.canPresentPageAlert }
            await fixture.runtime.releaseDeletion()
            await eventually { !detail.busy }
            await settle()
            XCTAssertTrue(navigation.topViewController === other)
            XCTAssertEqual(navigation.viewControllers.count, 2)
            XCTAssertNil(other.presentedViewController)
            XCTAssertNil(page.presentedViewController)
            let calls = await fixture.runtime.deletions
            XCTAssertEqual(calls, ["a"])
            try await assertOnlyCatBRemainsOnDisk(fixture)
            navigation.popViewController(animated: false)
            await eventually { page.canPresentPageAlert && page.snapshot?.cats.map(\.installationId) == ["b"] }
            XCTAssertNil(page.presentedViewController)
            unhost(window)
            await fixture.sources.close(); await fixture.rules.close()
        }
    }

    private func assertOnlyCatBRemainsOnDisk(_ fixture: FilmSettingsFixture) async throws {
        let snapshot = await fixture.sources.snapshot
        XCTAssertEqual(snapshot.cats.map(\.installationId), ["b"])
        XCTAssertEqual(snapshot.videos.map(\.key.installationId), ["b"])
        let rebuilt = FilmSourceService(transport: .init(rpc: { _, _, _ in throw FilmSourceError(code: "OFFLINE") }),
                                       storeFactory: { fixture.sourceStore })
        try await rebuilt.initialize()
        let persisted = await rebuilt.snapshot
        XCTAssertEqual(persisted.cats.map(\.installationId), ["b"])
        XCTAssertEqual(persisted.videos.map(\.key.installationId), ["b"])
        await rebuilt.close()
    }

    private func selectRow(_ page: FilmSettingsListController, _ id: String) throws {
        page.loadViewIfNeeded()
        let location = try XCTUnwrap(page.sections.enumerated().compactMap { section, value -> IndexPath? in
            value.rows.firstIndex { $0.id == id }.map { IndexPath(row: $0, section: section) }
        }.first)
        XCTAssertTrue(page.sections[location.section].rows[location.row].enabled)
        page.tableView(page.table, didSelectRowAt: location)
    }

    /// Drive the exact choice handler installed on the real UIAlertController,
    /// including dismissal completion, without private UIKit KVC or touch claims.
    private func activateAlert(_ page: FilmSettingsListController, title: String) async throws {
        let alert = try XCTUnwrap(page.presentedViewController as? UIAlertController)
        await eventually { !alert.isBeingPresented && alert.view.window != nil }
        XCTAssertNotNil(alert.actions.first { $0.title == title })
        page.selectAlertChoice(title)
        await eventually { page.presentedViewController !== alert }
    }

    private func row(_ page: FilmSettingsListController, _ id: String) throws -> FilmSettingsRow {
        try XCTUnwrap(page.sections.flatMap(\.rows).first { $0.id == id })
    }
    private func eventually(_ predicate: @escaping @MainActor () -> Bool, file: StaticString = #filePath, line: UInt = #line) async {
        let expected = expectation(for: NSPredicate { _, _ in MainActor.assumeIsolated { predicate() } }, evaluatedWith: nil)
        await fulfillment(of: [expected], timeout: 5)
        XCTAssertTrue(predicate(), file: file, line: line)
    }
    private func settle() async {
        let settled = expectation(description: "UIKit layout settled")
        DispatchQueue.main.async { settled.fulfill() }
        await fulfillment(of: [settled], timeout: 3)
    }
    private func foregroundScene() async throws -> UIWindowScene {
        await eventually {
            UIApplication.shared.applicationState == .active &&
            UIApplication.shared.connectedScenes.contains {
                $0 is UIWindowScene && $0.activationState == .foregroundActive
            }
        }
        return try XCTUnwrap(UIApplication.shared.applicationState == .active
            ? UIApplication.shared.connectedScenes.compactMap { $0 as? UIWindowScene }
                .first { $0.activationState == .foregroundActive }
            : nil)
    }

    private func host(_ page: UIViewController, size: CGSize? = nil) async throws -> UIWindow {
        let scene = try await foregroundScene()
        let bounds = scene.coordinateSpace.bounds
        let window = UIWindow(windowScene: scene)
        let visiblePage = try XCTUnwrap(
            (page as? UINavigationController)?.topViewController as? PageViewController
                ?? page as? PageViewController)
        hostedWindows.append((window, scene.windows.first { $0.isKeyWindow }))
        window.frame = bounds
        window.windowLevel = .normal + 1
        let container = UIViewController()
        container.addChild(page); container.view.addSubview(page.view)
        page.view.translatesAutoresizingMaskIntoConstraints = false
        NSLayoutConstraint.activate([
            page.view.topAnchor.constraint(equalTo: container.view.topAnchor),
            page.view.leadingAnchor.constraint(equalTo: container.view.leadingAnchor),
            page.view.widthAnchor.constraint(equalToConstant: size?.width ?? bounds.width),
            page.view.heightAnchor.constraint(equalToConstant: size?.height ?? bounds.height),
        ])
        page.didMove(toParent: container)
        window.rootViewController = container; window.makeKeyAndVisible()
        window.layoutIfNeeded()
        page.view.layoutIfNeeded()
        await eventually { visiblePage.viewIfLoaded?.window === window && visiblePage.canPresentPageAlert }
        return window
    }
    private func unhost(_ window: UIWindow) {
        guard let index = hostedWindows.firstIndex(where: { $0.window === window }) else { return }
        let previousKeyWindow = hostedWindows.remove(at: index).previousKeyWindow
        if let page = window.rootViewController { PageViewController.invalidatePages(in: page) }
        window.endEditing(true)
        window.isHidden = true; window.rootViewController = nil
        previousKeyWindow?.makeKey()
    }
    private func attach(_ page: UIViewController, _ name: String) {
        page.view.layoutIfNeeded()
        // Alert compositing needs a foreground scene and is covered by the
        // App-hosted test; these fixtures capture only ordinary page content.
        let image = UIGraphicsImageRenderer(bounds: page.view.bounds).image { _ in
            page.view.drawHierarchy(in: page.view.bounds, afterScreenUpdates: true)
        }
        let attachment = XCTAttachment(image: image); attachment.name = name; attachment.lifetime = .keepAlways
        add(attachment)
    }
}

@MainActor
private final class FilmSettingsTheme: PageThemeProviding {
    let pageColorway = PageColorway.lilac
    let animatesPageColorway = false
    let pageThemeChanges = Empty<Void, Never>().eraseToAnyPublisher()
}

private struct FilmSettingsFixture: Sendable {
    let base: URL
    let rulesStore: DocumentStore
    let sourceStore: DocumentStore
    let rules: FilmRuleService
    let sources: FilmSourceService
    let runtime: FilmSettingsRPCFixture
    init() throws {
        base = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: base, withIntermediateDirectories: true)
        rulesStore = try DocumentStore(baseURL: base, namespace: FilmRuleService.documentNamespace)
        sourceStore = try DocumentStore(baseURL: base, namespace: FilmSourceService.documentNamespace)
        rules = try FilmRuleService(store: rulesStore)
        runtime = FilmSettingsRPCFixture()
        let runtime = runtime, store = sourceStore
        sources = FilmSourceService(transport: .init(rpc: { method, params, _ in try await runtime.rpc(method, params) }),
                                    storeFactory: { store })
    }
    func removeFiles() { try? FileManager.default.removeItem(at: base) }
}

/// Drives the production batch/confirmation/retry UI with a real DocumentStore
/// conflict at B. The fault lives at the operation boundary, not in the owner.
@MainActor
private final class FilmVideoBatchFailurePage: FilmSourcePageController {
    let store: DocumentStore
    let keys: [FilmSourceKey]
    var attempts: [FilmSourceKey] = []
    var committedBytes: Data?
    private var injected = false
    init(service: FilmSourceService, store: DocumentStore, keys: [FilmSourceKey], themeProvider: any PageThemeProviding) {
        self.store = store; self.keys = keys
        super.init(service: service, themeProvider: themeProvider)
    }
    override func render() {
        sections = [.init(title: "", rows: [.init(title: "删除", id: "fixture.deleteVideos", enabled: !busy,
            action: { [weak self] in
                self?.confirm("删除视频源？") { [weak self] in
                    guard let self else { return }
                    deleteBatch(keys, isPresent: { key, snapshot in
                        snapshot.videos.contains { $0.key == key }
                    }) { [weak self] key in
                        guard let self else { return }
                        attempts.append(key)
                        if key == keys[1], !injected {
                            injected = true
                            committedBytes = try store.read(key: FilmSourceService.documentKey)
                            try store.write(Data("{}".utf8), key: FilmSourceService.documentKey)
                        }
                        try await service.deleteVideo(key)
                    }
                }
            })])]
        super.render()
    }
}

private actor FilmSettingsRPCFixture {
    var calls: [String] = []
    private var disabled = Set<String>()
    private var removed = Set<String>()
    private var local = Set<String>()
    private var updated = Set<String>()
    private var failDeletion: String?
    private var statusFailureAfterDeletion: String?
    private var failNextStatus = false
    private var failedStarts = 0
    private var restartFailureID: String?
    private var restartFailures = 0
    private(set) var restarts: [String] = []
    private(set) var deletions: [String] = []
    private var deletionPaused = false
    private var deletionEntered = false
    private var deletionWaiter: CheckedContinuation<Void, Never>?
    private var deletionEnteredWaiters: [CheckedContinuation<Void, Never>] = []
    private(set) var updates: [String] = []
    private var updatePaused = false
    private var updateEntered = false
    private var updateWaiter: CheckedContinuation<Void, Never>?
    private var updateEnteredWaiters: [CheckedContinuation<Void, Never>] = []
    func failDeletionOnce(_ id: String) { failDeletion = id }
    func failStatusAfterDeletion(_ id: String) { statusFailureAfterDeletion = id }
    func failRestarts(_ id: String, count: Int) { restartFailureID = id; restartFailures = count }
    func pauseDeletion() { deletionPaused = true }
    func waitForDeletion() async {
        if deletionEntered { return }
        await withCheckedContinuation { deletionEnteredWaiters.append($0) }
    }
    func releaseDeletion() { deletionWaiter?.resume(); deletionWaiter = nil }
    func setLocal(_ ids: Set<String>) { local = ids }
    func failStarts(_ count: Int) { failedStarts = count }
    func pauseUpdates() { updatePaused = true }
    func waitForUpdate() async {
        if updateEntered { return }
        await withCheckedContinuation { updateEnteredWaiters.append($0) }
    }
    func releaseUpdate() { updateWaiter?.resume(); updateWaiter = nil }
    private var pause = false
    private var entered = false
    private var waiter: CheckedContinuation<Void, Never>?
    private var enteredWaiters: [CheckedContinuation<Void, Never>] = []
    private var probeCancelled = false
    private var cancellationWaiters: [CheckedContinuation<Void, Never>] = []
    func waitForProbeCancellation() async {
        if probeCancelled { return }
        await withCheckedContinuation { cancellationWaiters.append($0) }
    }
    private func cancelledProbe() {
        probeCancelled = true
        let waiting = cancellationWaiters; cancellationWaiters.removeAll(); waiting.forEach { $0.resume() }
    }
    func pauseProbes() { pause = true }
    func waitForProbe() async {
        if entered { return }
        await withCheckedContinuation { enteredWaiters.append($0) }
    }
    func releaseProbe() { waiter?.resume(); waiter = nil }
    func rpc(_ method: String, _ params: JSONValue) async throws -> JSONValue {
        let method = String(method.dropFirst("cat.script.".count)); calls.append(method)
        let fields: [String: JSONValue]
        if case .object(let values) = params { fields = values } else { fields = [:] }
        let id: String
        if case .string(let value)? = fields["installationId"] { id = value } else { id = "a" }
        let value: JSONValue
        switch method {
        case "status":
            if failNextStatus { failNextStatus = false; throw FilmSourceError(code: "STATUS_FAILED") }
            value = .object(["instances": .array(["a", "b"].filter { !removed.contains($0) }.map { cat($0) })])
        case "start":
            if failedStarts > 0 { failedStarts -= 1; throw FilmSourceError(code: "OFFLINE") }
            value = .object([:])
        case "uninstall":
            deletions.append(id)
            if deletionPaused {
                deletionPaused = false; deletionEntered = true
                let waiting = deletionEnteredWaiters; deletionEnteredWaiters.removeAll(); waiting.forEach { $0.resume() }
                await withCheckedContinuation { deletionWaiter = $0 }
            }
            if failDeletion == id { failDeletion = nil; throw FilmSourceError(code: "UNINSTALL_FAILED") }
            removed.insert(id)
            if statusFailureAfterDeletion == id {
                statusFailureAfterDeletion = nil; failNextStatus = true
            }
            value = .object(["failures": .array([])])
        case "stage.begin":
            return .object(["contract": .string("cat.script.v1"), "ok": .bool(true),
                            "token": .string("upload"), "chunkBytes": .number(65536)])
        case "stage.chunk":
            guard case .string(let encoded)? = fields["data"], let bytes = Data(base64Encoded: encoded),
                  case .number(let offset)? = fields["offset"] else { throw FilmSourceError(code: "INVALID_CHUNK") }
            return .object(["contract": .string("cat.script.v1"), "ok": .bool(true),
                            "next": .number(offset + Double(bytes.count))])
        case "stage.abort":
            return .object(["contract": .string("cat.script.v1"), "ok": .bool(true)])
        case "update":
            updates.append(id)
            if updatePaused {
                updatePaused = false; updateEntered = true
                let waiting = updateEnteredWaiters; updateEnteredWaiters.removeAll(); waiting.forEach { $0.resume() }
                await withCheckedContinuation { updateWaiter = $0 }
            }
            updated.insert(id); value = cat(id)
        case "enable":
            if fields["enabled"] == .bool(false) { disabled.insert(id) } else { disabled.remove(id) }
            value = cat(id)
        case "restart":
            restarts.append(id)
            if restartFailureID == id, restartFailures > 0 {
                restartFailures -= 1
                throw FilmSourceError(code: "RESTART_FAILED")
            }
            value = cat(id)
        case "edit", "install", "import": value = cat(id)
        case "invoke":
            if pause {
                pause = false; entered = true
                let waiting = enteredWaiters; enteredWaiters.removeAll(); waiting.forEach { $0.resume() }
                await withTaskCancellationHandler {
                    await withCheckedContinuation { waiter = $0 }
                } onCancel: { Task { await self.cancelledProbe() } }
            }
            value = .object(["list": .array([]), "class": .array([])])
        default: throw FilmSourceError(code: "UNEXPECTED_\(method)")
        }
        return .object(["contract": .string("cat.script.v1"), "ok": .bool(true),
                        "data": .string(String(decoding: try JSONEncoder().encode(value), as: UTF8.self))])
    }
    private func cat(_ id: String) -> JSONValue {
        .object(["installationId": .string(id), "name": .string("测试猫源 " + id),
                 "sourceType": .string(local.contains(id) ? "local" : "network"), "sourceUrl": local.contains(id) ? .null : .string("https://example.invalid/\(id).js"),
                 "enabled": .bool(!disabled.contains(id)), "generation": .number(1),
                 "state": .string(disabled.contains(id) ? "disabled" : "ready"),
                 "importedAt": .number(1_700_000_000_000), "updatedAt": .null, "updateState": .string(updated.contains(id) ? "updated" : "idle"),
                 "updateError": .null, "sourceHash": .string("fixture"), "error": .null,
                 "sites": .array([.object(["key": .string("site"), "name": .string("测试视频源 " + id),
                    "api": .string("/api/" + id), "actions": .array([.string("home"), .string("category")]), "methods": .object([:])])])])
    }
}
#endif
