#if os(iOS)
import XCTest
import UIKit
import Combine
import UniformTypeIdentifiers
import MeowRuntime
import MeowStorage
@testable import MeowFilmFeature
@testable import MeowUI
@testable import Meowfilm

/// UIKit uses real Node rules; source management alone uses a protocol fixture.
@MainActor
final class FilmSettingsPlatformTests: XCTestCase {
    private var hostedWindows: [(window: UIWindow, previousKeyWindow: UIWindow?)] = []

    override func setUp() async throws {
        try await super.setUp()
        _ = try await hostResources()
        _ = try await foregroundScene()
    }

    private func makeFixture() async throws -> FilmSettingsFixture {
        let resources = try await hostResources()
        let fixture = try await FilmSettingsFixture(client: try await resources.scriptSession.connection())
        addTeardownBlock { try await fixture.removeFiles() }
        return fixture
    }

    override func tearDown() async throws {
        // Also restore the App window if a throwing assertion interrupted a test.
        while let entry = hostedWindows.last { unhost(entry.window) }
        try await super.tearDown()
    }

    func testGroupDraftSurvivesNavigationAndRestoreAndInlineTestNeverSave() async throws {
        let fixture = try await makeFixture()
        let rules = fixture.rules
        try await rules.initialize()
        let owner = FilmRulesDraft(service: rules)
        try await owner.load()
        let initial = try XCTUnwrap(owner.draft)
        XCTAssertFalse(initial.episodeCleanRegexRules.isEmpty)
        XCTAssertFalse(initial.movieRules.isEmpty)
        let page = FilmRulePageViewController(owner: owner, group: .episodeClean, themeProvider: FilmSettingsTheme())
        let window = try await host(page)
        defer { unhost(window) }
        let editor = try XCTUnwrap(page.editors.first)
        editor.pattern.text = "^草稿"; editor.textViewDidChange(editor.pattern)
        owner.edit(.movie, index: 0, rule: .init(pattern: "(电影)", replace: "$1", flags: "i"))
        let reopened = FilmRulePageViewController(owner: owner, group: .episodeClean, themeProvider: FilmSettingsTheme())
        reopened.loadViewIfNeeded()
        XCTAssertEqual(reopened.editors.first?.pattern.text, "^草稿")
        try await owner.save(.episodeClean)
        let saved = try await rules.savedSnapshot()
        XCTAssertEqual(saved.episodeCleanRegexRules.first, "^草稿")
        XCTAssertEqual(saved.movieRules, initial.movieRules)
        XCTAssertEqual(owner.rules(.movie).first?.pattern, "(电影)")
        try await owner.restore(.movie)
        let afterRestore = try await rules.savedSnapshot()
        XCTAssertEqual(afterRestore, saved)
        XCTAssertNotEqual(owner.rules(.movie).first?.pattern, "(电影)")
        owner.edit(.movie, index: 0, rule: .init(pattern: "["))
        let before = try await fixture.ruleRPC.saved()
        let test = FilmRulePageViewController(owner: owner, group: .movie, themeProvider: FilmSettingsTheme())
        test.loadViewIfNeeded(); test.toggleTest(); test.testFields.input.text = "电影 2026"; test.runTest()
        await eventually { !test.testing && test.result != nil }
        XCTAssertFalse(try XCTUnwrap(test.result).errors.isEmpty)
        let after = try await fixture.ruleRPC.saved()
        XCTAssertEqual(after, before)
        XCTAssertEqual(owner.rules(.movie).first?.pattern, "[")
        await rules.close(); await fixture.sources.close()
    }

    func testRulesGroupSaveFailureRetainsDraftAndRetryPreservesOtherSavedGroups() async throws {
        let fixture = try await makeFixture()
        try await fixture.rules.initialize()
        let owner = FilmRulesDraft(service: fixture.rules); try await owner.load()
        let original = try await fixture.ruleRPC.saved()
        let originalSaved = try await fixture.rules.savedSnapshot()
        owner.edit(.episodeClean, index: 0, rule: .init(pattern: "^修改"))
        owner.edit(.movie, index: 0, rule: .init(pattern: "other pending"))
        await fixture.ruleRPC.failWrites(true)
        do { try await owner.save(.episodeClean); XCTFail("Node storage failure must remain a failure") } catch {}
        XCTAssertEqual(owner.rules(.episodeClean).first?.pattern, "^修改")
        let saved = try await fixture.rules.savedSnapshot()
        XCTAssertNotEqual(saved.episodeCleanRegexRules.first, "^修改")
        let failed = try await fixture.ruleRPC.saved()
        XCTAssertEqual(failed, original)
        await fixture.ruleRPC.failWrites(false)
        try await owner.save(.episodeClean)
        let committed = try await fixture.rules.savedSnapshot()
        XCTAssertEqual(committed.episodeCleanRegexRules.first, "^修改")
        XCTAssertEqual(committed.movieRules, originalSaved.movieRules)
        XCTAssertEqual(owner.rules(.movie).first?.pattern, "other pending")
        await fixture.rules.close(); await fixture.sources.close()
    }

    func testModelLoadsNodeRulesAndLocalSourcesAndCloseDoesNotCloseServices() async throws {
        let fixture = try await makeFixture()
        let tmdb = FilmTMDBSettingsModel(service: FilmTMDBService(connection: {
            XCTFail("Starting local settings must not request TMDB"); throw FilmSearchError.unavailable
        }), didSave: {})
        let douban = FilmDoubanSettingsModel(service: FilmDoubanSettingsService(connection: {
            XCTFail("Starting local settings must not request Douban"); throw FilmSearchError.unavailable
        }), didSave: {})
        let playback = FilmPlaybackSettingsModel(transport: .init(request: { _, _ in
            XCTFail("Starting local settings must not request playback"); throw FilmPlaybackError(code: "CLOSED")
        }, observation: { _, _, _ in XCTFail("Settings must not observe a playback session") }))
        let model = FilmSettingsModel(rules: fixture.rules, sources: fixture.sources,
                                      tmdb: tmdb, douban: douban, playback: playback)
        XCTAssertTrue(model.tmdb === tmdb)
        XCTAssertTrue(model.douban === douban)
        XCTAssertTrue(model.playback === playback)
        model.start()
        await eventually { model.rules.draft != nil && model.snapshot != nil }
        let calls = await fixture.runtime.calls
        XCTAssertTrue(calls.isEmpty, "Opening settings never runs source detection")
        model.rules.edit(.episodeClean, index: 0, rule: .init(pattern: "长期草稿"))
        model.close(); model.start()
        await eventually { model.snapshot != nil }
        XCTAssertEqual(model.rules.rules(.episodeClean).first?.pattern, "长期草稿")
        model.close()
        XCTAssertTrue(model.tmdb === tmdb)
        XCTAssertTrue(model.douban === douban)
        XCTAssertTrue(model.playback === playback)
        XCTAssertNil(tmdb.saved)
        XCTAssertNil(douban.saved)
        XCTAssertNil(playback.preferences)
        _ = try await fixture.rules.savedSnapshot()
        try await fixture.sources.initialize()
        await fixture.rules.close(); await fixture.sources.close()
    }

    func testSettingsMenuKeepsRulesAndDataSourcesIndependentWhenPlaybackIsUnavailable() async throws {
        let fixture = try await makeFixture()
        let tmdb = FilmTMDBSettingsModel(service: FilmTMDBService(connection: { throw FilmSearchError.unavailable }), didSave: {})
        let douban = FilmDoubanSettingsModel(service: FilmDoubanSettingsService(connection: { throw FilmSearchError.unavailable }), didSave: {})
        let model = FilmSettingsModel(rules: fixture.rules, sources: fixture.sources, tmdb: tmdb, douban: douban)
        XCTAssertNil(model.playback)
        let page = FilmSettingsViewController(model: model, themeProvider: FilmSettingsTheme())
        let navigation = UINavigationController(rootViewController: page)
        defer { PageViewController.invalidatePages(in: navigation) }
        page.loadViewIfNeeded()
        let menu = try XCTUnwrap(page.children.compactMap { $0 as? FilmSettingsListController }.first)
        menu.loadViewIfNeeded()
        let rows = try XCTUnwrap(menu.sections.first).rows
        XCTAssertEqual(rows.map(\.id), [
            "film.settings.search", "film.settings.matching", "film.settings.cats", "film.settings.videos",
            "film.settings.dataSources", "film.settings.preferences",
        ])
        XCTAssertTrue(rows.prefix(5).allSatisfy(\.enabled))
        XCTAssertTrue(rows.dropFirst(5).allSatisfy { !$0.enabled })
        for index in 5..<rows.count {
            menu.tableView(menu.table, didSelectRowAt: IndexPath(row: index, section: 0))
            XCTAssertTrue(navigation.topViewController === page)
        }
        try selectRow(menu, "film.settings.matching")
        let matching = try XCTUnwrap(navigation.topViewController as? FilmSmartMatchingViewController)
        matching.loadViewIfNeeded()
        XCTAssertEqual(matching.sections.flatMap(\.rows).map(\.id), [
            "film.matching.episodeClean", "film.matching.episode", "film.matching.movie", "film.matching.siteClean",
        ])
        XCTAssertTrue(matching.sections.flatMap(\.rows).prefix(3).allSatisfy(\.enabled))
        XCTAssertFalse(try row(matching, "film.matching.siteClean").enabled)
        navigation.setViewControllers([page], animated: false)
        try selectRow(menu, "film.settings.dataSources")
        XCTAssertTrue(navigation.topViewController is FilmDataSourceSettingsViewController)
        XCTAssertEqual((navigation.topViewController as? PageViewController)?.pageTitle, "数据源")
        await fixture.rules.close(); await fixture.sources.close()
    }

    func testSettingsMenuRoutesSearchAndMatchingGroupsAlongsidePlaybackAndDataSources() async throws {
        let fixture = try await makeFixture()
        let tmdb = FilmTMDBSettingsModel(service: FilmTMDBService(connection: { throw FilmSearchError.unavailable }), didSave: {})
        let douban = FilmDoubanSettingsModel(service: FilmDoubanSettingsService(connection: { throw FilmSearchError.unavailable }), didSave: {})
        let playback = FilmPlaybackSettingsModel(transport: .init(
            request: { _, _ in throw FilmPlaybackError(code: "CLOSED") },
            observation: { _, _, _ in XCTFail("Settings must not observe a playback session") }))
        let model = FilmSettingsModel(rules: fixture.rules, sources: fixture.sources, tmdb: tmdb, douban: douban, playback: playback)
        let page = FilmSettingsViewController(model: model, themeProvider: FilmSettingsTheme())
        let navigation = UINavigationController(rootViewController: page)
        defer { PageViewController.invalidatePages(in: navigation) }
        page.loadViewIfNeeded()
        let menu = try XCTUnwrap(page.children.compactMap { $0 as? FilmSettingsListController }.first)
        menu.loadViewIfNeeded()
        XCTAssertEqual(menu.sections.flatMap(\.rows).count, 6)
        XCTAssertTrue(menu.sections.flatMap(\.rows).allSatisfy(\.enabled))
        for (id, title) in [
            ("search", "搜索净化设置"), ("matching", "智能匹配设置"), ("dataSources", "数据源"),
            ("preferences", "智能播放设置"),
        ] {
            navigation.setViewControllers([page], animated: false)
            try selectRow(menu, "film.settings.\(id)")
            let destination = try XCTUnwrap(navigation.topViewController as? PageViewController)
            XCTAssertFalse(destination === page)
            XCTAssertEqual(destination.pageTitle, title)
            switch id {
            case "search":
                XCTAssertEqual((destination as? FilmRulePageViewController)?.group, .search)
            case "matching": XCTAssertTrue(destination is FilmSmartMatchingViewController)
            case "dataSources": XCTAssertTrue(destination is FilmDataSourceSettingsViewController)
            default: XCTAssertTrue(destination is FilmSmartPlaybackSettingsController)
            }
        }
        navigation.setViewControllers([page], animated: false)
        try selectRow(menu, "film.settings.preferences")
        let smart = try XCTUnwrap(navigation.topViewController as? FilmSmartPlaybackSettingsController)
        smart.loadViewIfNeeded()
        XCTAssertEqual(smart.sections.flatMap(\.rows).map(\.title),
                       ["偏好设置", "自动准备", "Manual 匹配源管理", "匹配禁用管理"])
        for (id, title) in [("preferences", "偏好设置"), ("autoPrepare", "自动准备"),
                            ("manual", "Manual 匹配源管理"), ("blocks", "匹配禁用管理")] {
            navigation.setViewControllers([page, smart], animated: false)
            try selectRow(smart, "film.smartPlayback.\(id)")
            let destination = try XCTUnwrap(navigation.topViewController as? PageViewController)
            XCTAssertEqual(destination.pageTitle, title)
            if id == "preferences" || id == "autoPrepare" {
                XCTAssertTrue(destination is FilmPlaybackPreferencesController)
            } else { XCTAssertTrue(destination is FilmPlaybackLibraryController) }
        }
        navigation.setViewControllers([page], animated: false)
        try selectRow(menu, "film.settings.matching")
        let matching = try XCTUnwrap(navigation.topViewController as? FilmSmartMatchingViewController)
        matching.loadViewIfNeeded()
        XCTAssertEqual(matching.sections.flatMap(\.rows).map(\.title),
                       ["剧集列表净化规则", "集数匹配规则", "电影匹配规则", "站点净化设置"])
        for group in [FilmRuleGroup.episodeClean, .episode, .movie] {
            navigation.setViewControllers([page, matching], animated: false)
            try selectRow(matching, "film.matching.\(group.rawValue)")
            let destination = try XCTUnwrap(navigation.topViewController as? FilmRulePageViewController)
            XCTAssertEqual(destination.group, group)
            XCTAssertEqual(destination.pageTitle, group.title)
        }
        navigation.setViewControllers([page, matching], animated: false)
        try selectRow(matching, "film.matching.siteClean")
        let site = try XCTUnwrap(navigation.topViewController as? FilmPlaybackPreferencesController)
        XCTAssertEqual(site.pageTitle, "站点净化设置")
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
        let fixture = try await makeFixture()
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
        let rebuilt = FilmSourceService(transport: .init(
            request: { _, _ in throw FilmSourceError(code: "OFFLINE") },
            upload: { _, _, _ in throw FilmSourceError(code: "OFFLINE") }),
                                       storeFactory: { fixture.sourceStore })
        try await rebuilt.initialize()
        let snapshot = await rebuilt.snapshot
        XCTAssertEqual(snapshot.videos, before.videos)
        await rebuilt.close(); await fixture.sources.close(); await fixture.rules.close()
    }

    func testVideoDetectionCancellationOnHiddenPageDoesNotPublishLateResult() async throws {
        let fixture = try await makeFixture()
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
        let fixture = try await makeFixture()
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
        let fixture = try await makeFixture()
        try await fixture.rules.initialize(); try await fixture.sources.initialize()
        let owner = FilmRulesDraft(service: fixture.rules); try await owner.load()
        for width: CGFloat in [320, 768] {
            let pages: [FilmSettingsListController] = [
                FilmRulePageViewController(owner: owner, group: .search, themeProvider: FilmSettingsTheme()),
                FilmRulePageViewController(owner: owner, group: .episode, themeProvider: FilmSettingsTheme()),
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
                    // Trailing section spacing can overflow while the last row
                    // already fits. Check content reachability, not a positive offset.
                    let lastFrame = page.table.rectForRow(at: last)
                    let viewport = page.table.bounds.inset(by: page.table.adjustedContentInset)
                    let context = "page=\(index), width=\(width)"
                    XCTAssertLessThanOrEqual(lastFrame.maxY, viewport.maxY + 1, context)
                    if lastFrame.height <= viewport.height {
                        XCTAssertGreaterThanOrEqual(lastFrame.minY, viewport.minY - 1, context)
                    }
                    XCTAssertTrue(page.table.indexPathsForVisibleRows?.contains(last) == true)
                    attach(page, "film-settings-\(index)-\(Int(width))-AXXXL-bottom")
                }
                unhost(window)
            }
        }
        await fixture.sources.close(); await fixture.rules.close()
    }

    func testPageSaveNormalizesClearedRowsAndStaleFieldsCannotChangeSuccessor() async throws {
        let fixture = try await makeFixture()
        try await fixture.rules.initialize()
        for index in [2, 1] {
            _ = try await fixture.rules.save(.init(episodeCleanRegexRules: ["first", "middle", "last"],
                                                   movieRules: [.init(pattern: "movie")]))
            let owner = FilmRulesDraft(service: fixture.rules); try await owner.load()
            owner.edit(.movie, index: 0, rule: .init(pattern: "other draft"))
            let page = FilmRulePageViewController(owner: owner, group: .episodeClean, themeProvider: FilmSettingsTheme())
            let navigation = PageNavigationController(themeProvider: FilmSettingsTheme())
            navigation.setViewControllers([page], animated: false)
            let window = try await host(navigation)
            let editor = page.editors[index]
            editor.pattern.becomeFirstResponder()
            editor.pattern.selectedRange = NSRange(location: 0, length: (editor.pattern.text as NSString).length)
            editor.pattern.deleteBackward()
            XCTAssertEqual(owner.rules(.episodeClean)[index].pattern, "")
            let before = try await fixture.rules.savedSnapshot()
            XCTAssertEqual(before.episodeCleanRegexRules.count, 3)
            page.save()
            await eventually { !page.writing && page.presentedViewController != nil }
            XCTAssertTrue(navigation.topViewController === page)
            let expected = index == 2 ? ["first", "middle"] : ["first", "last"]
            XCTAssertEqual(owner.draft?.episodeCleanRegexRules, expected)
            XCTAssertFalse(editor.pattern.isEditable)
            editor.pattern.text = "stale"; editor.textViewDidChange(editor.pattern)
            XCTAssertEqual(owner.draft?.episodeCleanRegexRules, expected)
            let saved = try await fixture.rules.savedSnapshot()
            XCTAssertEqual(saved.episodeCleanRegexRules, expected)
            XCTAssertEqual(saved.movieRules.first?.pattern, "movie")
            XCTAssertEqual(owner.rules(.movie).first?.pattern, "other draft")
            XCTAssertEqual(page.editors[1].pattern.text, expected[1])
            unhost(window)
        }
        await fixture.rules.close(); await fixture.sources.close()
    }

    func testInlineAddDeleteAndDefaultsOnlyChangeCurrentGroupDraft() async throws {
        let fixture = try await makeFixture()
        try await fixture.rules.initialize()
        let owner = FilmRulesDraft(service: fixture.rules); try await owner.load()
        let before = try await fixture.ruleRPC.saved()
        let initialMovie = owner.rules(.movie)
        let page = FilmRulePageViewController(owner: owner, group: .episodeClean, themeProvider: FilmSettingsTheme())
        let navigation = UINavigationController(rootViewController: page)
        let window = try await host(navigation); defer { unhost(window) }
        let count = page.editors.count
        XCTAssertEqual(page.pageHeader.trailingCount, 1)
        try selectRow(page, "film.rules.episodeClean.add")
        XCTAssertEqual(page.editors.count, count + 1)
        page.editors[count].pattern.text = "new rule"
        page.editors[count].textViewDidChange(page.editors[count].pattern)
        page.editors[count].delete.sendActions(for: .touchUpInside)
        XCTAssertEqual(page.editors.count, count)
        try await owner.restore(.episodeClean)
        XCTAssertEqual(owner.rules(.movie), initialMovie)
        page.toggleTest()
        XCTAssertTrue(page.testExpanded)
        XCTAssertTrue(navigation.topViewController === page)
        let after = try await fixture.ruleRPC.saved()
        XCTAssertEqual(after, before)
        await fixture.rules.close(); await fixture.sources.close()
    }

    func testInlineRuleTestCancellationOnHiddenPageDoesNotPublishOrSave() async throws {
        let fixture = try await makeFixture()
        try await fixture.rules.initialize()
        let owner = FilmRulesDraft(service: fixture.rules); try await owner.load()
        owner.edit(.movie, index: 0, rule: .init(pattern: "^(a+)+$", replace: "x"))
        let page = FilmRulePageViewController(owner: owner, group: .movie, themeProvider: FilmSettingsTheme())
        let window = try await host(page); defer { unhost(window) }
        let before = try await fixture.ruleRPC.saved()
        page.toggleTest()
        page.testFields.input.text = String(repeating: "a", count: 100_000) + "!"
        page.runTest()
        try await Task.sleep(nanoseconds: 25_000_000)
        XCTAssertTrue(page.testing)
        page.pageDidBecomeHidden()
        XCTAssertFalse(page.testing)
        // Admission to the same service waits for the cancelled execution to drain.
        _ = try await fixture.rules.process(.init(section: .movie, text: "next"), group: .movie,
                                            rules: [.init(pattern: "next")])
        XCTAssertNil(page.result)
        let after = try await fixture.ruleRPC.saved()
        XCTAssertEqual(after, before)
        XCTAssertEqual(owner.rules(.movie).first?.pattern, "^(a+)+$")
        await fixture.rules.close(); await fixture.sources.close()
    }

    func testInlineEpisodeTestsUseThisGroupDraftAndAssociatedSavedGroupOnly() async throws {
        let fixture = try await makeFixture()
        let saved = FilmRuleSet(episodeCleanRegexRules: ["^广告"],
                                episodeRules: [.init(pattern: "^第([0-9]+)集$", replace: "E$1")])
        _ = try await fixture.rules.save(saved)
        let owner = FilmRulesDraft(service: fixture.rules); try await owner.load()
        owner.edit(.episodeClean, index: 0, rule: .init(pattern: "^草稿"))
        owner.edit(.episode, index: 0, rule: .init(pattern: "^第([0-9]+)集$", replace: "Draft$1"))
        let before = try await fixture.ruleRPC.saved()
        for group in [FilmRuleGroup.episodeClean, .episode] {
            let page = FilmRulePageViewController(owner: owner, group: group, themeProvider: FilmSettingsTheme())
            let window = try await host(page)
            page.toggleTest()
            page.testFields.input.text = group == .episodeClean ? "草稿第12集" : "广告第12集"
            page.runTest()
            await eventually { !page.testing && page.result != nil }
            let result = try XCTUnwrap(page.result)
            XCTAssertEqual(result.processedText.displayString, "第12集")
            XCTAssertEqual(result.outputText.displayString, group == .episodeClean ? "E12" : "Draft12")
            XCTAssertTrue(result.errors.isEmpty)
            unhost(window)
        }
        let after = try await fixture.ruleRPC.saved()
        XCTAssertEqual(after, before)
        XCTAssertEqual(owner.rules(.episodeClean).first?.pattern, "^草稿")
        XCTAssertEqual(owner.rules(.episode).first?.replace, "Draft$1")
        await fixture.rules.close(); await fixture.sources.close()
    }

    func testGroupSaveHandoffReceivesNormalizationAfterPageRemovalAndWaiterCancellation() async throws {
        let fixture = try await makeFixture()
        try await fixture.rules.initialize()
        for cancelWaiter in [false, true] {
            let normalized = cancelWaiter ? "cancelled-waiter" : "removed-page"
            let owner = FilmRulesDraft(service: fixture.rules); try await owner.load()
            owner.edit(.movie, index: 0, rule: .init(pattern: "  \(normalized)  ", replace: " keep "))
            owner.edit(.episodeClean, index: 0, rule: .init(pattern: "other pending"))
            let page = FilmRulePageViewController(owner: owner, group: .movie, themeProvider: FilmSettingsTheme())
            let root = FilmSettingsListController(themeProvider: FilmSettingsTheme())
            let navigation = PageNavigationController(themeProvider: FilmSettingsTheme())
            navigation.setViewControllers([root, page], animated: false)
            let window = try await host(navigation)
            await fixture.ruleRPC.holdNextGroupReceipt()
            let waiter: Task<Void, Error>?
            if cancelWaiter {
                waiter = Task { try await owner.save(.movie) }
            } else {
                waiter = nil; page.save()
            }
            await fixture.ruleRPC.waitForGroupReceipt()
            XCTAssertTrue(owner.busy)
            let persisted = try await fixture.ruleRPC.saved()
            let persistedRules = try JSONDecoder().decode(FilmRuleSet.self, from: RuntimeJSON.encode(persisted))
            XCTAssertEqual(persistedRules.movieRules.first?.pattern, normalized)
            XCTAssertEqual(owner.rules(.movie).first?.pattern, "  \(normalized)  ")
            let beforeReceipt = try await fixture.rules.savedSnapshot()
            XCTAssertNotEqual(beforeReceipt.movieRules.first?.pattern, normalized)
            waiter?.cancel()
            navigation.popViewController(animated: false)
            await eventually { !page.isPageActive }
            XCTAssertTrue(owner.busy)
            await fixture.ruleRPC.releaseGroupReceipt()
            try await waiter?.value
            await eventually { !owner.busy }
            XCTAssertEqual(owner.rules(.movie).first?.pattern, normalized)
            XCTAssertEqual(owner.rules(.movie).first?.replace, " keep ")
            XCTAssertEqual(owner.rules(.episodeClean).first?.pattern, "other pending")
            let saved = try await fixture.rules.savedSnapshot()
            XCTAssertEqual(saved.movieRules, owner.rules(.movie))
            XCTAssertNotEqual(saved.episodeCleanRegexRules.first, "other pending")
            unhost(window)
        }
        await fixture.rules.close(); await fixture.sources.close()
    }

    func testInlineRuleFieldsUseCompactFlagsAndStackOnlyForNarrowOrLargeType() async throws {
        let fixture = try await makeFixture()
        try await fixture.rules.initialize()
        let owner = FilmRulesDraft(service: fixture.rules); try await owner.load()
        for (width, largeType) in [(CGFloat(390), false), (CGFloat(320), false), (CGFloat(390), true)] {
            let page = FilmRulePageViewController(owner: owner, group: .movie, themeProvider: FilmSettingsTheme())
            if largeType { page.traitOverrides.preferredContentSizeCategory = .accessibilityExtraExtraExtraLarge }
            let window = try await host(page, size: CGSize(width: width, height: 844))
            await settle(); page.table.layoutIfNeeded()
            let editor = try XCTUnwrap(page.editors.first)
            editor.layoutIfNeeded()
            let replace = editor.replacement.convert(editor.replacement.bounds, to: editor)
            let flags = editor.flags.convert(editor.flags.bounds, to: editor)
            XCTAssertGreaterThanOrEqual(flags.width, 44)
            XCTAssertTrue(editor.pattern.superview === editor)
            if width == 390 && !largeType {
                XCTAssertGreaterThanOrEqual(editor.bounds.width, 300)
                XCTAssertEqual(replace.minY, flags.minY, accuracy: 1)
                XCTAssertGreaterThan(replace.width, flags.width)
            } else {
                XCTAssertGreaterThan(flags.minY, replace.maxY)
            }
            unhost(window)
        }
        await fixture.rules.close(); await fixture.sources.close()
    }

    func testCatBatchDeleteRetryContinuesAfterReceiptAndCancelDoesNotDeleteMore() async throws {
        for retry in [true, false] {
            let fixture = try await makeFixture()
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
        let fixture = try await makeFixture()
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
        let fixture = try await makeFixture()
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
        let fixture = try await makeFixture()
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
        let fixture = try await makeFixture()
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
            let fixture = try await makeFixture()
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
        let fixture = try await makeFixture()
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
        let fixture = try await makeFixture()
        try await fixture.rules.initialize()
        _ = try await fixture.rules.save(.init(movieRules: [.init(pattern: "^.", replace: "x")]))
        let owner = FilmRulesDraft(service: fixture.rules); try await owner.load()
        let page = FilmRulePageViewController(owner: owner, group: .movie, themeProvider: FilmSettingsTheme())
        let window = try await host(page); defer { unhost(window) }
        let before = try await fixture.ruleRPC.saved()
        page.toggleTest()
        page.testFields.input.becomeFirstResponder(); page.testFields.input.insertText("🎬")
        try selectRow(page, "film.rule.test.run")
        await eventually { !page.testing && page.result != nil }
        let result = try XCTUnwrap(page.result)
        XCTAssertEqual(result.outputText.utf16, [0x0078, 0xDFAC])
        XCTAssertEqual(try row(page, "film.rule.test.output").title, "x\u{FFFD}")
        XCTAssertEqual(page.testFields.input.text, "🎬")
        XCTAssertEqual(owner.rules(.movie).first?.pattern, "^.")
        let after = try await fixture.ruleRPC.saved()
        XCTAssertEqual(after, before)
        await fixture.rules.close(); await fixture.sources.close()
    }

    func testCatBatchRestartRetriesOnlyUnconfirmedItemsAndCancelDoesNotRestartMore() async throws {
        for retries in [0, 1, 2] {
            let fixture = try await makeFixture()
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
            let rebuilt = FilmSourceService(transport: .init(
                request: { _, _ in throw FilmSourceError(code: "OFFLINE") },
                upload: { _, _, _ in throw FilmSourceError(code: "OFFLINE") }),
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
            let fixture = try await makeFixture()
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
            let fixture = try await makeFixture()
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
        let rebuilt = FilmSourceService(transport: .init(
            request: { _, _ in throw FilmSourceError(code: "OFFLINE") },
            upload: { _, _, _ in throw FilmSourceError(code: "OFFLINE") }),
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
    var pagePalette: PagePalette { .preset(.lilac) }
    let animatesPageColorway = false
    let pageThemeChanges = Empty<Void, Never>().eraseToAnyPublisher()
}

@MainActor
private final class FilmSettingsFixture {
    let base: URL
    let sourceStore: DocumentStore
    let rules: FilmRuleService
    let sources: FilmSourceService
    let runtime: FilmSettingsRPCFixture
    let ruleRPC: FilmSettingsRuleRPC
    private let original: RuntimeValue
    init(client: RuntimeHTTPClient) async throws {
        base = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: base, withIntermediateDirectories: true)
        sourceStore = try DocumentStore(baseURL: base, namespace: FilmSourceService.documentNamespace)
        ruleRPC = FilmSettingsRuleRPC(client: client)
        original = try await ruleRPC.saved()
        let rpc = ruleRPC
        rules = FilmRuleService(request: { try await rpc.call($0, $1) })
        runtime = FilmSettingsRPCFixture()
        let runtime = runtime, store = sourceStore
        sources = FilmSourceService(transport: .init(
            request: { operation, params in try await runtime.call(operation, params) },
            upload: { id, revision, bytes in try await runtime.upload(id, revision, bytes) }),
                                    storeFactory: { store })
        // Match the old fresh-owner fixture, not the simulator's saved edits.
        // Teardown restores the original Node receipt.
        _ = try await rpc.call("rules.clear", .object([:]))
    }
    func removeFiles() async throws {
        await ruleRPC.releaseGroupReceipt()
        await ruleRPC.failWrites(false)
        _ = try await ruleRPC.call("rules.set", .object(["rules": original]))
        await rules.close(); await sources.close()
        try FileManager.default.removeItem(at: base)
    }
}

private actor FilmSettingsRuleRPC {
    let client: RuntimeHTTPClient
    private var failedWrites = false
    private var holdGroupReceipt = false
    private var receiptWaiter: CheckedContinuation<Void, Never>?
    private var receiptEnteredWaiters: [CheckedContinuation<Void, Never>] = []
    init(client: RuntimeHTTPClient) { self.client = client }
    func failWrites(_ value: Bool) { failedWrites = value }
    func holdNextGroupReceipt() { holdGroupReceipt = true }
    func waitForGroupReceipt() async {
        if receiptWaiter != nil { return }
        await withCheckedContinuation { receiptEnteredWaiters.append($0) }
    }
    func releaseGroupReceipt() { receiptWaiter?.resume(); receiptWaiter = nil }
    func saved() async throws -> RuntimeValue {
        let receipt = try await client.call(module: .film, operation: "rules.get")
        return try receipt.filmRequiredObject().filmValue("rules")
    }
    func call(_ operation: String, _ input: RuntimeValue) async throws -> RuntimeValue {
        if failedWrites && ["rules.set", "rules.group.set"].contains(operation) { throw RuntimeHTTPError.remote("STORAGE_ERROR") }
        let response = try await client.call(module: .film, operation: operation, parameters: input)
        if operation == "rules.group.set", holdGroupReceipt {
            holdGroupReceipt = false
            await withCheckedContinuation {
                receiptWaiter = $0
                let waiters = receiptEnteredWaiters; receiptEnteredWaiters.removeAll()
                for waiter in waiters { waiter.resume() }
            }
        }
        return response
    }
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
    private var candidates: [String: (revision: Int, bytes: Data)] = [:]
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
    func upload(_ id: String, _ revision: Int, _ bytes: Data) throws -> RuntimeValue {
        guard revision > 0, !bytes.isEmpty else { throw FilmSourceError(code: "INVALID_ARGUMENT") }
        candidates[id] = (revision, bytes)
        return .object(["installationId": .string(id), "sourceRevision": .number(Double(revision)),
            "sourceBytes": .number(Double(bytes.count))])
    }
    func call(_ method: String, _ params: RuntimeValue) async throws -> RuntimeValue {
        calls.append(method)
        let fields: RuntimeObject
        if case .object(let values) = params { fields = values } else { fields = [:] }
        let id: String
        if case .string(let value)? = fields["installationId"] { id = value } else { id = "a" }
        let value: RuntimeValue
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
        case "update":
            if local.contains(id) {
                guard case .number(let revision)? = fields["sourceRevision"],
                      candidates[id]?.revision == Int(revision) else {
                    throw FilmSourceError(code: "NOT_FOUND")
                }
            } else if fields["sourceRevision"] != nil { throw FilmSourceError(code: "INVALID_ARGUMENT") }
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
        case "install":
            guard case .number(let revision)? = fields["sourceRevision"],
                  candidates[id]?.revision == Int(revision) else {
                throw FilmSourceError(code: "NOT_FOUND")
            }
            value = cat(id)
        case "edit", "import": value = cat(id)
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
        return value
    }
    private func cat(_ id: String) -> RuntimeValue {
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
