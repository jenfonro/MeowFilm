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

/// Settings use real native documents; rule execution uses the hosted Node.
/// Source runtime operations alone use a protocol fixture.
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

    private func panModel(_ fixture: FilmSettingsFixture) throws -> PanAccountsModel {
        PanAccountsModel(service: .init(document: try fixture.document(.panAccounts), request: { _, _, _ in
            throw RuntimeHTTPError.closed
        }))
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
        editor.pattern.text = "^草稿"; editor.pattern.delegate?.textViewDidChange?(editor.pattern)
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
        let before = try fixture.rulesDocument.read()
        let test = FilmRulePageViewController(owner: owner, group: .movie, themeProvider: FilmSettingsTheme())
        let testWindow = try await host(test)
        defer { unhost(testWindow) }
        test.testFields.input.text = "电影 2026"; try await runRuleTest(test)
        await eventually { !test.testing && test.result != nil }
        XCTAssertFalse(try XCTUnwrap(test.result).errors.isEmpty)
        let after = try fixture.rulesDocument.read()
        XCTAssertEqual(after, before)
        XCTAssertEqual(owner.rules(.movie).first?.pattern, "[")
        await rules.close(); await fixture.sources.close()
    }

    func testRulesGroupSaveFailureRetainsDraftAndRetryPreservesOtherSavedGroups() async throws {
        let fixture = try await makeFixture()
        try await fixture.rules.initialize()
        let owner = FilmRulesDraft(service: fixture.rules); try await owner.load()
        let original = try fixture.rulesDocument.read()
        let originalSaved = try await fixture.rules.savedSnapshot()
        owner.edit(.episodeClean, index: 0, rule: .init(pattern: "^修改"))
        owner.edit(.movie, index: 0, rule: .init(pattern: "other pending"))
        try fixture.ruleStorage.blockWrites()
        do { try await owner.save(.episodeClean); XCTFail("Native storage failure must remain a failure") }
        catch { XCTAssertEqual(error as? FilmSettingsStorageError, .storageFailure) }
        XCTAssertEqual(owner.rules(.episodeClean).first?.pattern, "^修改")
        let saved = try await fixture.rules.savedSnapshot()
        XCTAssertNotEqual(saved.episodeCleanRegexRules.first, "^修改")
        XCTAssertTrue(fixture.ruleStorage.notifications.isEmpty)
        try fixture.ruleStorage.restoreWrites()
        let failed = try fixture.rulesDocument.read()
        XCTAssertEqual(failed, original)
        try await owner.save(.episodeClean)
        let committed = try await fixture.rules.savedSnapshot()
        XCTAssertEqual(committed.episodeCleanRegexRules.first, "^修改")
        XCTAssertEqual(committed.movieRules, originalSaved.movieRules)
        XCTAssertEqual(owner.rules(.movie).first?.pattern, "other pending")
        XCTAssertEqual(fixture.ruleStorage.notifications, ["film.rules"])
        await fixture.rules.close(); await fixture.sources.close()
    }

    func testModelLoadsNativeRulesAndLocalSourcesAndCloseDoesNotCloseServices() async throws {
        let fixture = try await makeFixture()
        let tmdb = FilmTMDBSettingsModel(service: FilmTMDBService(settingsDocument: try fixture.document(.tmdb), connection: {
            XCTFail("Starting local settings must not request TMDB"); throw FilmSearchError.unavailable
        }), didSave: {})
        let douban = FilmDoubanSettingsModel(service: FilmDoubanSettingsService(
            settingsDocument: try fixture.document(.douban)), didSave: {})
        let playback = FilmPlaybackSettingsModel(transport: .init(request: { _, _ in
            XCTFail("Starting local settings must not request playback"); throw FilmPlaybackError(code: "CLOSED")
        }, observation: { _, _, _ in XCTFail("Settings must not observe a playback session") }),
                                                 settingsDocument: try fixture.document(.preferences))
        let pan = try panModel(fixture)
        let model = FilmSettingsModel(rules: fixture.rules, sources: fixture.sources,
                                      tmdb: tmdb, douban: douban, pan: pan, playback: playback)
        XCTAssertTrue(model.tmdb === tmdb)
        XCTAssertTrue(model.douban === douban)
        XCTAssertTrue(model.pan === pan)
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
        XCTAssertTrue(model.pan === pan)
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
        let tmdb = FilmTMDBSettingsModel(service: FilmTMDBService(
            settingsDocument: try fixture.document(.tmdb), connection: { throw FilmSearchError.unavailable }), didSave: {})
        let douban = FilmDoubanSettingsModel(service: FilmDoubanSettingsService(
            settingsDocument: try fixture.document(.douban)), didSave: {})
        let model = FilmSettingsModel(rules: fixture.rules, sources: fixture.sources, tmdb: tmdb, douban: douban,
                                      pan: try panModel(fixture))
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
            "film.settings.dataSources", "film.settings.pan", "film.settings.preferences",
        ])
        XCTAssertTrue(rows.prefix(6).allSatisfy(\.enabled))
        XCTAssertTrue(rows.dropFirst(6).allSatisfy { !$0.enabled })
        for index in 6..<rows.count {
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
        XCTAssertEqual((navigation.topViewController as? PageViewController)?.pageTitle, "刮削设置")
        navigation.setViewControllers([page], animated: false)
        try selectRow(menu, "film.settings.pan")
        XCTAssertTrue(navigation.topViewController is PanAccountsViewController)
        XCTAssertEqual((navigation.topViewController as? PageViewController)?.pageTitle, "网盘账号")
        await fixture.rules.close(); await fixture.sources.close()
    }

    func testSettingsMenuRoutesSearchAndMatchingGroupsAlongsidePlaybackAndDataSources() async throws {
        let fixture = try await makeFixture()
        let tmdb = FilmTMDBSettingsModel(service: FilmTMDBService(
            settingsDocument: try fixture.document(.tmdb), connection: { throw FilmSearchError.unavailable }), didSave: {})
        let douban = FilmDoubanSettingsModel(service: FilmDoubanSettingsService(
            settingsDocument: try fixture.document(.douban)), didSave: {})
        let playback = FilmPlaybackSettingsModel(transport: .init(
            request: { _, _ in throw FilmPlaybackError(code: "CLOSED") },
            observation: { _, _, _ in XCTFail("Settings must not observe a playback session") }),
                                                 settingsDocument: try fixture.document(.preferences))
        let model = FilmSettingsModel(rules: fixture.rules, sources: fixture.sources, tmdb: tmdb,
                                      douban: douban, pan: try panModel(fixture), playback: playback)
        let page = FilmSettingsViewController(model: model, themeProvider: FilmSettingsTheme())
        let navigation = UINavigationController(rootViewController: page)
        defer { PageViewController.invalidatePages(in: navigation) }
        page.loadViewIfNeeded()
        let menu = try XCTUnwrap(page.children.compactMap { $0 as? FilmSettingsListController }.first)
        menu.loadViewIfNeeded()
        XCTAssertEqual(menu.sections.flatMap(\.rows).count, 7)
        XCTAssertTrue(menu.sections.flatMap(\.rows).allSatisfy(\.enabled))
        for (id, title) in [
            ("search", "搜索净化设置"), ("matching", "智能匹配设置"), ("dataSources", "刮削设置"),
            ("pan", "网盘账号"), ("preferences", "智能播放设置"),
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
            case "pan": XCTAssertTrue(destination is PanAccountsViewController)
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

    func testSettingsMenusFieldsSelectionAndDangerUseSharedCardRows() throws {
        let page = FilmSettingsListController(themeProvider: FilmSettingsTheme())
        defer { PageViewController.invalidatePages(in: page) }
        let field = filmTextField("名称", id: "fixture.name")
        var disabledInvocations = 0
        page.sections = [.init(title: "", rows: [
            .init(title: "菜单", detail: "详情", id: "fixture.menu", disclosure: true, action: {}),
            .init(title: "已选", id: "fixture.selected", selected: true, action: {}),
            .init(title: "不可用", id: "fixture.disabled", enabled: false, action: { disabledInvocations += 1 }),
            .init(title: "删除", id: "fixture.danger", destructive: true, action: {}),
            .init(title: "名称", id: "fixture.name", content: field),
        ])]
        page.loadViewIfNeeded()
        let table: UITableView = page.table
        XCTAssertTrue(table is SettingsTableView)
        XCTAssertEqual(table.separatorStyle, .none)
        for style in [UIUserInterfaceStyle.light, .dark] {
            page.traitOverrides.userInterfaceStyle = style
            page.applyTheme()
            let header = UITableViewHeaderFooterView(reuseIdentifier: nil)
            header.textLabel?.text = "设置分组"
            page.tableView(table, willDisplayHeaderView: header, forSection: 0)
            XCTAssertEqual(header.textLabel?.font, .preferredFont(forTextStyle: .subheadline))
            XCTAssertTrue(header.textLabel?.adjustsFontForContentSizeCategory == true)
            let cells = (0..<5).map {
                page.tableView(page.table, cellForRowAt: IndexPath(row: $0, section: 0))
            }
            for (index, cell) in cells.enumerated() {
                XCTAssertTrue(cell is SettingsTableCell)
                let background = try XCTUnwrap(cell.backgroundView as? SettingsRowBackgroundView)
                let selected = try XCTUnwrap(cell.selectedBackgroundView as? SettingsRowBackgroundView)
                XCTAssertEqual(background.drawsSeparator, index < 4)
                XCTAssertEqual(selected.drawsSeparator, index < 4)
            }
            let menu = try XCTUnwrap(cells[0].contentConfiguration as? UIListContentConfiguration)
            XCTAssertEqual(menu.textProperties.font, .preferredFont(forTextStyle: .body))
            XCTAssertTrue(menu.textProperties.adjustsFontForContentSizeCategory)
            XCTAssertEqual(menu.textProperties.color, UIColor(page.theme.settings.ink))
            XCTAssertEqual(menu.secondaryTextProperties.color, UIColor(page.theme.settings.muted))
            XCTAssertEqual(cells[0].accessoryType, .disclosureIndicator)
            XCTAssertEqual(cells[1].accessoryType, .checkmark)
            XCTAssertTrue(cells[1].accessibilityTraits.contains(.selected))
            XCTAssertTrue(cells[2].accessibilityTraits.contains(.notEnabled))
            let disabled = try XCTUnwrap(cells[2].contentConfiguration as? UIListContentConfiguration)
            let danger = try XCTUnwrap(cells[3].contentConfiguration as? UIListContentConfiguration)
            XCTAssertEqual(disabled.textProperties.color, UIColor(page.theme.settings.muted))
            XCTAssertEqual(danger.textProperties.color, UIColor(page.theme.settings.danger))
            XCTAssertEqual(cells[4].contentView.directionalLayoutMargins, menu.directionalLayoutMargins)
            XCTAssertTrue(field.superview === cells[4].contentView)
            XCTAssertEqual(field.backgroundColor, UIColor(page.theme.settings.input))
            page.tableView(page.table, didSelectRowAt: IndexPath(row: 2, section: 0))
            XCTAssertEqual(disabledInvocations, 0)
        }
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
            request: { _, _ in throw FilmSourceError(code: "OFFLINE") }),
                                       storeFactory: { fixture.sourceStore }, settingsDocument: fixture.catDocument,
                                       runtimeSourceRoot: fixture.runtimeSourceRoot)
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
        try ManagementListTestUI.button("management.selectAll", in: cats.manager).sendActions(for: .touchUpInside)
        XCTAssertTrue(cats.manager.deleteButton.isEnabled)
        cats.manager.deleteButton.sendActions(for: .touchUpInside)
        let alert = try XCTUnwrap(cats.presentedViewController as? UIAlertController)
        XCTAssertEqual(alert.actions.map(\.style), [.cancel, .destructive])
        let calls = await fixture.runtime.calls
        XCTAssertFalse(calls.contains("uninstall"), "Showing confirmation is not deletion consent")
        await eventually { !alert.isBeingPresented && alert.view.window != nil }
        let detail = FilmCatDetailController(service: fixture.sources, installationId: "a", themeProvider: FilmSettingsTheme())
        detail.loadViewIfNeeded(); detail.beginAppearanceTransition(true, animated: false); detail.endAppearanceTransition()
        await eventually { detail.snapshot?.cats.count == 2 }
        for action in ["edit", "update", "restart", "enabled"] {
            XCTAssertNotNil(try row(detail, "film.cat.\(action)").action)
        }
        XCTAssertFalse(detail.sections.flatMap(\.rows).contains { $0.title == "删除" })
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
                if let rules = page as? FilmRulePageViewController {
                    let scroll = rules.scrollView
                    let frame = scroll.convert(scroll.bounds, to: page.view)
                    XCTAssertGreaterThan(scroll.bounds.width, 0)
                    XCTAssertLessThanOrEqual(frame.maxX, page.view.bounds.maxX)
                    XCTAssertGreaterThanOrEqual(frame.minY, page.pageHeader.frame.maxY)
                    let footer = try ruleActions(rules)
                    let footerFrame = footer.convert(footer.bounds, to: page.view)
                    XCTAssertEqual(frame.maxY, footerFrame.minY, accuracy: 1)
                    XCTAssertEqual(footerFrame.maxY, page.view.safeAreaLayoutGuide.layoutFrame.maxY, accuracy: 1)
                    for editor in rules.editors {
                        XCTAssertGreaterThanOrEqual(editor.bounds.height, 44)
                        XCTAssertFalse(editor.accessibilityIdentifier?.isEmpty ?? true)
                        XCTAssertLessThanOrEqual(editor.bounds.width, scroll.bounds.width)
                        XCTAssertGreaterThan(editor.pattern.bounds.width, 0)
                    }
                    scroll.setContentOffset(.init(x: 0, y: max(0, scroll.contentSize.height - scroll.bounds.height)), animated: false)
                    scroll.layoutIfNeeded()
                    let last = try XCTUnwrap(rules.editors.last)
                    XCTAssertLessThanOrEqual(last.convert(last.bounds, to: scroll).maxY, scroll.bounds.maxY + 1)
                    XCTAssertEqual(footer.convert(footer.bounds, to: page.view), footerFrame)
                    for id in ["film.rules.\(rules.group.rawValue).add", "film.rules.restore", "film.rules.test"] {
                        let button = try ruleButton(rules, id)
                        let buttonFrame = button.convert(button.bounds, to: page.view)
                        XCTAssertGreaterThanOrEqual(buttonFrame.minY, footerFrame.minY)
                        XCTAssertLessThanOrEqual(buttonFrame.maxY, footerFrame.maxY)
                    }
                    attach(page, "film-settings-\(index)-\(Int(width))-AXXXL-bottom")
                    unhost(window)
                    continue
                }
                let manager = (page as? FilmCatSourcesViewController)?.manager
                    ?? (page as? FilmVideoSourcesViewController)?.manager
                let table = manager?.table ?? page.table
                let frame = table.convert(table.bounds, to: page.view)
                XCTAssertGreaterThan(table.bounds.width, 0)
                XCTAssertLessThanOrEqual(frame.maxX, page.view.bounds.maxX)
                XCTAssertGreaterThanOrEqual(frame.minY, page.pageHeader.frame.maxY)
                XCTAssertEqual(frame.maxY, page.view.safeAreaLayoutGuide.layoutFrame.maxY - (manager == nil ? 0 : 4), accuracy: 1)
                for cell in table.visibleCells {
                    XCTAssertGreaterThanOrEqual(cell.bounds.height, 44)
                    XCTAssertFalse(cell.accessibilityIdentifier?.isEmpty ?? true)
                }
                attach(page, "film-settings-\(index)-\(Int(width))-AXXXL")
                if let lastSection = (0..<table.numberOfSections).last,
                   let lastRow = (0..<table.numberOfRows(inSection: lastSection)).last,
                   table.contentSize.height > table.bounds.height {
                    let last = IndexPath(row: lastRow, section: lastSection)
                    // Self-sizing rows update contentSize while UIKit lays them
                    // out. Walk actual rows instead of trusting estimated height.
                    for section in 0..<table.numberOfSections {
                        for row in 0..<table.numberOfRows(inSection: section) {
                            table.scrollToRow(at: IndexPath(row: row, section: section), at: .bottom, animated: false)
                            table.layoutIfNeeded()
                        }
                    }
                    table.scrollToRow(at: last, at: .bottom, animated: false)
                    table.layoutIfNeeded()
                    // Trailing section spacing can overflow while the last row
                    // already fits. Check content reachability, not a positive offset.
                    let lastFrame = table.rectForRow(at: last)
                    let viewport = table.bounds.inset(by: table.adjustedContentInset)
                    let context = "page=\(index), width=\(width)"
                    XCTAssertLessThanOrEqual(lastFrame.maxY, viewport.maxY + 1, context)
                    if lastFrame.height <= viewport.height {
                        XCTAssertGreaterThanOrEqual(lastFrame.minY, viewport.minY - 1, context)
                    }
                    XCTAssertTrue(table.indexPathsForVisibleRows?.contains(last) == true)
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
            editor.pattern.text = "stale"; editor.pattern.delegate?.textViewDidChange?(editor.pattern)
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

    func testAddAndDefaultsOnlyChangeCurrentGroupDraftAndTestStaysInline() async throws {
        let fixture = try await makeFixture()
        try await fixture.rules.initialize()
        let owner = FilmRulesDraft(service: fixture.rules); try await owner.load()
        let before = try fixture.rulesDocument.read()
        let initialEpisode = owner.rules(.episodeClean)
        let initialMovie = owner.rules(.movie)
        let page = FilmRulePageViewController(owner: owner, group: .episodeClean, themeProvider: FilmSettingsTheme())
        let navigation = UINavigationController(rootViewController: page)
        let window = try await host(navigation); defer { unhost(window) }
        let count = page.editors.count
        XCTAssertEqual(page.pageHeader.trailingCount, 1)
        try ruleButton(page, "film.rules.episodeClean.add").sendActions(for: .touchUpInside)
        XCTAssertEqual(page.editors.count, count + 1)
        page.editors[count].pattern.text = "new rule"
        page.editors[count].pattern.delegate?.textViewDidChange?(page.editors[count].pattern)
        XCTAssertEqual(owner.rules(.episodeClean).last?.pattern, "new rule")
        XCTAssertEqual(owner.rules(.movie), initialMovie)
        let remove = try XCTUnwrap(descendants(page.editors[count]).compactMap { $0 as? UIButton }.first {
            $0.accessibilityIdentifier == "film.rules.episodeClean.\(count).delete"
        })
        XCTAssertEqual(remove.title(for: .normal), "删除")
        remove.sendActions(for: .touchUpInside)
        XCTAssertEqual(page.editors.count, count)
        XCTAssertEqual(owner.rules(.episodeClean), initialEpisode)
        try ruleButton(page, "film.rules.restore").sendActions(for: .touchUpInside)
        try await activateAlert(page, title: "确认")
        await eventually { !page.writing && page.editors.count == count }
        XCTAssertEqual(owner.rules(.movie), initialMovie)
        page.testFields.input.text = "广告第12集"
        try ruleButton(page, "film.rules.test").sendActions(for: .touchUpInside)
        try await confirmRuleTest(page)
        await eventually { !page.testing && page.result != nil }
        XCTAssertTrue(navigation.topViewController === page)
        XCTAssertEqual(navigation.viewControllers.count, 1)
        XCTAssertEqual(page.pageTitle, FilmRuleGroup.episodeClean.title)
        XCTAssertTrue(page.editors.allSatisfy { $0.resultLabel.text?.hasPrefix("测试结果：") == true })
        let after = try fixture.rulesDocument.read()
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
        let before = try fixture.rulesDocument.read()
        page.testFields.input.text = String(repeating: "a", count: 100_000) + "!"
        try await runRuleTest(page)
        try await Task.sleep(nanoseconds: 25_000_000)
        XCTAssertTrue(page.testing)
        page.pageDidBecomeHidden()
        XCTAssertFalse(page.testing)
        // Admission to the same service waits for the cancelled execution to drain.
        _ = try await fixture.rules.process(.init(section: .movie, text: "next"), group: .movie,
                                            rules: [.init(pattern: "next")])
        XCTAssertNil(page.result)
        let after = try fixture.rulesDocument.read()
        XCTAssertEqual(after, before)
        XCTAssertEqual(owner.rules(.movie).first?.pattern, "^(a+)+$")
        await fixture.rules.close(); await fixture.sources.close()
    }

    func testRuleActionsStayFixedAndLastCardClearsFooter() async throws {
        let fixture = try await makeFixture()
        let patterns = (1...7).map { "rule-\($0)" }
        let matching = patterns.map { FilmMatchRule(pattern: $0, replace: "$1", flags: "i") }
        _ = try await fixture.rules.save(.init(episodeCleanRegexRules: patterns, episodeRules: matching,
                                               movieRules: matching, aggregateRegexRules: patterns))
        let owner = FilmRulesDraft(service: fixture.rules); try await owner.load()
        for group in [FilmRuleGroup.search, .episodeClean, .episode, .movie] {
            for (width, height) in [(CGFloat(320), CGFloat(568)), (CGFloat(430), CGFloat(844))] {
                for largeType in [false, true] {
                    let page = FilmRulePageViewController(owner: owner, group: group, themeProvider: FilmSettingsTheme())
                    if largeType { page.traitOverrides.preferredContentSizeCategory = .accessibilityExtraExtraExtraLarge }
                    let window = try await host(page, size: .init(width: width, height: height))
                    await settle(); page.view.layoutIfNeeded()
                    let scroll = page.scrollView
                    let footer = try ruleActions(page)
                    let footerFrame = footer.convert(footer.bounds, to: page.view)
                    let first = try XCTUnwrap(page.editors.first)
                    XCTAssertEqual(first.convert(first.bounds, to: page.view).minY - page.pageHeader.frame.maxY,
                                   SettingsVisualMetrics.contentTopSpacing, accuracy: 1)
                    XCTAssertEqual(scroll.convert(scroll.bounds, to: page.view).maxY, footerFrame.minY, accuracy: 1)
                    XCTAssertEqual(footerFrame.maxY, page.view.safeAreaLayoutGuide.layoutFrame.maxY, accuracy: 1)
                    XCTAssertFalse(footer.isDescendant(of: scroll))
                    XCTAssertFalse(footer.hasAmbiguousLayout)
                    XCTAssertFalse(page.testFields.input.isDescendant(of: page.view))
                    XCTAssertFalse(page.testFields.query.isDescendant(of: page.view))
                    let buttons = try ["film.rules.\(group.rawValue).add", "film.rules.restore", "film.rules.test"].map {
                        try ruleButton(page, $0)
                    }
                    let frames = buttons.map { $0.convert($0.bounds, to: page.view) }
                    for button in buttons {
                        XCTAssertGreaterThanOrEqual(button.bounds.height, 44)
                        let text = try XCTUnwrap(button.titleLabel)
                        let fit = text.sizeThatFits(.init(width: text.bounds.width, height: .greatestFiniteMagnitude))
                        XCTAssertLessThanOrEqual(fit.height, text.bounds.height + 1)
                        if !largeType { XCTAssertLessThanOrEqual(fit.height, text.font.lineHeight + 1) }
                        XCTAssertFalse(text.font.fontDescriptor.symbolicTraits.contains(.traitBold))
                    }
                    scroll.setContentOffset(.init(x: 0, y: max(0, scroll.contentSize.height - scroll.bounds.height)), animated: false)
                    page.view.layoutIfNeeded()
                    let last = try XCTUnwrap(page.editors.last)
                    XCTAssertLessThanOrEqual(last.convert(last.bounds, to: scroll).maxY, scroll.bounds.maxY + 1)
                    XCTAssertEqual(footer.convert(footer.bounds, to: page.view), footerFrame)
                    XCTAssertEqual(buttons.map { $0.convert($0.bounds, to: page.view) }, frames)
                    attach(page, "film-rule-footer-\(group.rawValue)-\(Int(width))-\(largeType ? "AXXXL" : "regular")")
                    unhost(window)
                }
            }
        }
        await fixture.rules.close(); await fixture.sources.close()
    }

    func testRuleHeadingFirstLineMatchesDeleteAndLongResultsDoNotMoveIt() async throws {
        let fixture = try await makeFixture()
        try await fixture.rules.initialize()
        let owner = FilmRulesDraft(service: fixture.rules); try await owner.load()
        for group in [FilmRuleGroup.search, .episodeClean, .episode, .movie] {
            for (width, largeType) in [(CGFloat(320), false), (CGFloat(430), false), (CGFloat(320), true)] {
                let page = FilmRulePageViewController(owner: owner, group: group, themeProvider: FilmSettingsTheme())
                if largeType { page.traitOverrides.preferredContentSizeCategory = .accessibilityExtraExtraExtraLarge }
                let window = try await host(page, size: .init(width: width, height: 844))
                await settle(); page.view.layoutIfNeeded()
                let editor = try XCTUnwrap(page.editors.first)
                try assertRuleHeading(editor)
                let original = editor.deleteButton.convert(editor.deleteButton.bounds, to: editor)
                attach(page, "film-rule-heading-before-\(group.rawValue)-\(Int(width))-\(largeType ? "AXXXL" : "regular")")
                editor.showResult(.init(text: String(repeating: "结果", count: 100)), theme: page.theme)
                page.view.layoutIfNeeded()
                try assertRuleHeading(editor)
                XCTAssertEqual(editor.deleteButton.convert(editor.deleteButton.bounds, to: editor).minY,
                               original.minY, accuracy: 1)
                XCTAssertEqual(editor.deleteButton.bounds.height, original.height, accuracy: 1)
                XCTAssertGreaterThan(editor.resultLabel.bounds.height, editor.resultLabel.font.lineHeight)
                XCTAssertLessThanOrEqual(editor.resultLabel.convert(editor.resultLabel.bounds, to: editor).maxX,
                                         editor.deleteButton.convert(editor.deleteButton.bounds, to: editor).minX)
                let heading = try XCTUnwrap(editor.arrangedSubviews.first)
                XCTAssertLessThanOrEqual(editor.resultLabel.convert(editor.resultLabel.bounds, to: heading).maxY,
                                         heading.bounds.maxY + 1)
                XCTAssertFalse(heading.hasAmbiguousLayout)
                XCTAssertFalse(editor.hasAmbiguousLayout)
                assertRuleUnderline(editor.pattern)
                attach(page, "film-rule-heading-\(group.rawValue)-\(Int(width))-\(largeType ? "AXXXL" : "regular")")
                editor.showResult(nil, theme: page.theme)
                page.view.layoutIfNeeded()
                try assertRuleHeading(editor)
                XCTAssertEqual(editor.deleteButton.convert(editor.deleteButton.bounds, to: editor).minY,
                               original.minY, accuracy: 1)
                XCTAssertEqual(editor.deleteButton.bounds.height, original.height, accuracy: 1)
                XCTAssertTrue(editor.resultLabel.isHidden)
                unhost(window)
            }
        }
        await fixture.rules.close(); await fixture.sources.close()
    }

    func testRuleTestDialogCollectsMultilineTextAndOnlySearchQuery() async throws {
        let fixture = try await makeFixture()
        _ = try await fixture.rules.save(.init(episodeCleanRegexRules: ["^广告\\n"],
            episodeRules: [.init(pattern: "^第([0-9]+)集$", replace: "E$1")],
            movieRules: [.init(pattern: "^电影\\n([0-9]+)$", replace: "$1")],
            aggregateRegexRules: ["^广告\\s+"]))
        let owner = FilmRulesDraft(service: fixture.rules); try await owner.load()
        let before = try fixture.rulesDocument.read()
        var expectedRequests: [FilmRuleRequest] = []
        for group in [FilmRuleGroup.search, .episodeClean, .episode, .movie] {
            let page = FilmRulePageViewController(owner: owner, group: group, themeProvider: FilmSettingsTheme())
            let window = try await host(page)
            try ruleButton(page, "film.rules.test").sendActions(for: .touchUpInside)
            let dialog = try XCTUnwrap(page.presentedViewController as? ManagementDialog)
            await eventually { !dialog.isBeingPresented && dialog.view.window != nil }
            XCTAssertFalse(page.testing); XCTAssertNil(page.result)
            XCTAssertEqual(dialog.body.arrangedSubviews.count, group == .search ? 2 : 1)
            XCTAssertFalse(descendants(dialog.body).contains { $0 is ManagementFormView })
            XCTAssertTrue(page.testFields.inputSection.isDescendant(of: dialog.body))
            XCTAssertEqual(page.testFields.querySection.isDescendant(of: dialog.body), group == .search)
            let text = group == .movie ? "电影\n2026" : (group == .search ? "广告\n作品2" : "广告\n第12集")
            XCTAssertTrue(page.testFields.input.becomeFirstResponder())
            page.testFields.input.insertText(text)
            page.testFields.query.text = "作品2"
            dialog.view.layoutIfNeeded()
            XCTAssertEqual(page.testFields.input.text, text)
            let section = page.testFields.inputSection
            XCTAssertEqual(section.titleLabel.text, "测试文本")
            XCTAssertGreaterThanOrEqual(section.input.convert(section.input.bounds, to: section).minY,
                                       section.titleLabel.convert(section.titleLabel.bounds, to: section).maxY)
            assertRuleUnderline(page.testFields.input)
            if group == .search {
                let query = page.testFields.querySection
                XCTAssertEqual(query.titleLabel.text, "查询词")
                XCTAssertGreaterThanOrEqual(query.input.convert(query.input.bounds, to: query).minY,
                                           query.titleLabel.convert(query.titleLabel.bounds, to: query).maxY)
                assertRuleUnderline(page.testFields.query)
            }
            attach(dialog, "film-rule-test-dialog-\(group.rawValue)")
            try await confirmRuleTest(page)
            await eventually { !page.testing && page.result != nil }
            XCTAssertEqual(page.result?.inputText.utf16, Array(text.utf16))
            XCTAssertEqual(page.result?.outputText.displayString,
                           group == .movie ? "2026" : (group == .search ? "作品2" : "E12"))
            expectedRequests.append(.init(section: group.section, text: text, query: group == .search ? "作品2" : nil))
            let requests = await fixture.ruleRPC.requests
            XCTAssertEqual(requests, expectedRequests)
            XCTAssertFalse(page.testFields.input.isDescendant(of: page.view))
            XCTAssertEqual(try fixture.rulesDocument.read(), before)
            unhost(window)
        }
        await fixture.rules.close(); await fixture.sources.close()
    }

    func testRuleTestDialogCancelDoesNotExecuteOrSave() async throws {
        let fixture = try await makeFixture()
        try await fixture.rules.initialize()
        let owner = FilmRulesDraft(service: fixture.rules); try await owner.load()
        owner.edit(.search, index: 0, rule: .init(pattern: "^草稿"))
        let page = FilmRulePageViewController(owner: owner, group: .search, themeProvider: FilmSettingsTheme())
        let window = try await host(page); defer { unhost(window) }
        let before = try fixture.rulesDocument.read()
        try ruleButton(page, "film.rules.test").sendActions(for: .touchUpInside)
        let dialog = try XCTUnwrap(page.presentedViewController as? ManagementDialog)
        await eventually { !dialog.isBeingPresented && dialog.view.window != nil }
        page.testFields.input.text = "草稿\n作品2"
        page.testFields.input.delegate?.textViewDidChange?(page.testFields.input)
        page.testFields.query.text = "作品2"; page.testFields.query.sendActions(for: .editingChanged)
        let cancel = try XCTUnwrap(descendants(dialog.view).compactMap { $0 as? UIButton }.first {
            $0.accessibilityIdentifier == "management.dialog.cancel"
        })
        cancel.sendActions(for: .touchUpInside)
        await eventually { page.canPresentPageAlert && dialog.viewIfLoaded?.window == nil }
        XCTAssertFalse(page.testing); XCTAssertNil(page.result)
        XCTAssertTrue(page.editors.allSatisfy { $0.resultLabel.isHidden })
        let requests = await fixture.ruleRPC.requests
        XCTAssertTrue(requests.isEmpty)
        XCTAssertEqual(owner.rules(.search).first?.pattern, "^草稿")
        XCTAssertEqual(try fixture.rulesDocument.read(), before)
        XCTAssertFalse(page.testFields.input.isDescendant(of: page.view))
        await fixture.rules.close(); await fixture.sources.close()
    }

    func testSearchRuleDialogAt320WithLargestTypeAndKeyboardKeepsInputsAndActionsReachable() async throws {
        let fixture = try await makeFixture()
        _ = try await fixture.rules.save(.search, rules: [.init(pattern: "^广告\\s+")])
        let owner = FilmRulesDraft(service: fixture.rules); try await owner.load()
        let page = FilmRulePageViewController(owner: owner, group: .search, themeProvider: FilmSettingsTheme())
        let navigation = UINavigationController(rootViewController: page)
        let scene = try await foregroundScene()
        // The sized host constrains a child inside an overlay window. Exercise
        // keyboard focus with a normal-level scene window and a navigation root.
        let window = UIWindow(windowScene: scene)
        hostedWindows.append((window, scene.windows.first { $0.isKeyWindow }))
        defer { unhost(window) }
        window.frame = scene.coordinateSpace.bounds
        window.frame.size.width = 320
        window.windowLevel = .normal
        // overFullScreen inherits the window's traits, not a child VC override.
        window.traitOverrides.preferredContentSizeCategory = .accessibilityExtraExtraExtraLarge
        window.rootViewController = navigation
        window.makeKeyAndVisible()
        window.layoutIfNeeded()
        await eventually {
            window.isKeyWindow && window.windowScene?.activationState == .foregroundActive &&
                page.viewIfLoaded?.window === window && page.canPresentPageAlert
        }
        XCTAssertEqual(window.traitCollection.preferredContentSizeCategory, .accessibilityExtraExtraExtraLarge)
        XCTAssertEqual(page.traitCollection.preferredContentSizeCategory, .accessibilityExtraExtraExtraLarge)
        let before = try fixture.rulesDocument.read()
        try ruleButton(page, "film.rules.test").sendActions(for: .touchUpInside)
        let dialog = try XCTUnwrap(page.presentedViewController as? ManagementDialog)
        if let transition = dialog.transitionCoordinator {
            let presented = expectation(description: "rule dialog presentation completed before keyboard focus")
            XCTAssertTrue(transition.animate(alongsideTransition: nil) { context in
                XCTAssertFalse(context.isCancelled)
                presented.fulfill()
            })
            await fulfillment(of: [presented], timeout: 5)
        }
        await eventually { window.isKeyWindow && !dialog.isBeingPresented && dialog.view.window === window }
        let scroll = try XCTUnwrap(descendants(dialog.view).first {
            $0.accessibilityIdentifier == "management.dialog.scroll"
        } as? UIScrollView)
        let panel = try XCTUnwrap(descendants(dialog.view).first {
            $0.accessibilityIdentifier == "management.dialog.panel"
        })
        let confirm = try XCTUnwrap(descendants(dialog.view).first {
            $0.accessibilityIdentifier == "management.dialog.confirm"
        } as? UIButton)
        let cancel = try XCTUnwrap(descendants(dialog.view).first {
            $0.accessibilityIdentifier == "management.dialog.cancel"
        } as? UIButton)
        let input = page.testFields.input, query = page.testFields.query
        XCTAssertEqual(dialog.view.bounds.width, 320, accuracy: 0.5)
        XCTAssertEqual(dialog.traitCollection.preferredContentSizeCategory, .accessibilityExtraExtraExtraLarge)
        XCTAssertEqual(input.traitCollection.preferredContentSizeCategory, .accessibilityExtraExtraExtraLarge)
        XCTAssertEqual(query.traitCollection.preferredContentSizeCategory, .accessibilityExtraExtraExtraLarge)
        XCTAssertTrue(input.window === window); XCTAssertTrue(query.window === window)
        XCTAssertEqual(dialog.body.arrangedSubviews.count, 2)
        XCTAssertTrue(page.testFields.inputSection.isDescendant(of: dialog.body))
        XCTAssertTrue(page.testFields.querySection.isDescendant(of: dialog.body))

        scroll.scrollRectToVisible(input.convert(input.bounds, to: scroll), animated: false)
        dialog.view.layoutIfNeeded()
        let keyboardShown = expectation(forNotification: UIResponder.keyboardDidShowNotification, object: nil) { notification in
            let frame = notification.userInfo?[UIResponder.keyboardFrameEndUserInfoKey] as? NSValue
            return (frame?.cgRectValue.height ?? 0) > 100
        }
        XCTAssertTrue(input.becomeFirstResponder())
        input.insertText("广告\n作品2")
        await fulfillment(of: [keyboardShown], timeout: 5)
        await eventually {
            dialog.view.layoutIfNeeded()
            return window.isKeyWindow && input.isFirstResponder &&
                dialog.view.keyboardLayoutGuide.layoutFrame.minY < dialog.view.safeAreaLayoutGuide.layoutFrame.maxY - 100
        }
        dialog.view.layoutIfNeeded()
        XCTAssertTrue(input.isFirstResponder)
        XCTAssertEqual(input.text, "广告\n作品2")
        XCTAssertGreaterThan(scroll.contentSize.height, scroll.bounds.height)
        XCTAssertLessThanOrEqual(scroll.contentSize.width, scroll.bounds.width + 1)
        XCTAssertGreaterThanOrEqual(panel.frame.minY, dialog.view.safeAreaLayoutGuide.layoutFrame.minY)
        XCTAssertLessThanOrEqual(panel.frame.maxY, dialog.view.keyboardLayoutGuide.layoutFrame.minY)

        func reveal(_ content: UIView, file: StaticString = #filePath, line: UInt = #line) {
            scroll.scrollRectToVisible(content.convert(content.bounds, to: scroll), animated: false)
            dialog.view.layoutIfNeeded()
            XCTAssertTrue(scroll.bounds.insetBy(dx: -1, dy: -1).contains(content.convert(content.bounds, to: scroll)),
                          file: file, line: line)
            let frame = content.convert(content.bounds, to: dialog.view)
            XCTAssertLessThanOrEqual(frame.maxY, dialog.view.keyboardLayoutGuide.layoutFrame.minY, file: file, line: line)
            let hit = dialog.view.hitTest(CGPoint(x: frame.midX, y: frame.midY), with: nil)
            XCTAssertTrue(hit === content || hit?.isDescendant(of: content) == true, file: file, line: line)
        }
        func assertRegular(_ font: UIFont, file: StaticString = #filePath, line: UInt = #line) {
            let traits = font.fontDescriptor.object(forKey: .traits) as? [UIFontDescriptor.TraitKey: Any]
            XCTAssertEqual((traits?[.weight] as? NSNumber)?.doubleValue ?? 0,
                           Double(UIFont.Weight.regular.rawValue), accuracy: 0.01, file: file, line: line)
            XCTAssertFalse(font.fontDescriptor.symbolicTraits.contains(.traitItalic), file: file, line: line)
        }
        let heading = try XCTUnwrap(descendants(scroll).compactMap { $0 as? UILabel }.first { $0.text == "测试" })
        for label in [heading, page.testFields.inputSection.titleLabel, page.testFields.querySection.titleLabel] {
            let fit = label.sizeThatFits(.init(width: label.bounds.width, height: .greatestFiniteMagnitude))
            XCTAssertGreaterThan(label.bounds.width, 0)
            XCTAssertLessThanOrEqual(fit.width, label.bounds.width + 1)
            XCTAssertLessThanOrEqual(fit.height, label.bounds.height + 1)
            XCTAssertTrue(label.adjustsFontForContentSizeCategory)
            assertRegular(label.font)
            // Labels are not interactive; their complete bounds must still scroll into view.
            scroll.scrollRectToVisible(label.convert(label.bounds, to: scroll), animated: false)
            XCTAssertTrue(scroll.bounds.insetBy(dx: -1, dy: -1).contains(label.convert(label.bounds, to: scroll)))
        }
        reveal(input)
        assertRuleUnderline(input)
        let inputFont = try XCTUnwrap(input.font), queryFont = try XCTUnwrap(query.font)
        for font in [inputFont, queryFont] {
            assertRegular(font)
            XCTAssertEqual(font.pointSize, UIFont.preferredFont(forTextStyle: .body, compatibleWith: dialog.traitCollection).pointSize,
                           accuracy: 0.1)
        }
        XCTAssertTrue(input.adjustsFontForContentSizeCategory); XCTAssertTrue(query.adjustsFontForContentSizeCategory)
        for range in [NSRange(location: 0, length: 2), NSRange(location: 3, length: 3)] {
            let start = try XCTUnwrap(input.position(from: input.beginningOfDocument, offset: range.location))
            let end = try XCTUnwrap(input.position(from: start, offset: range.length))
            let textRange = try XCTUnwrap(input.textRange(from: start, to: end))
            input.scrollRangeToVisible(range)
            await eventually { input.bounds.insetBy(dx: -1, dy: -1).contains(input.firstRect(for: textRange)) }
            XCTAssertGreaterThan(input.firstRect(for: textRange).height, 0)
        }
        attach(dialog, "film-search-test-dialog-320-AXXXL-keyboard-multiline")
        reveal(query)
        XCTAssertTrue(query.becomeFirstResponder())
        query.insertText("作品2")
        dialog.view.layoutIfNeeded()
        reveal(query)
        XCTAssertTrue(query.isFirstResponder); XCTAssertFalse(input.isFirstResponder)
        XCTAssertEqual(query.text, "作品2")
        assertRuleUnderline(query)
        XCTAssertGreaterThanOrEqual(query.editingRect(forBounds: query.bounds).height + 1, queryFont.lineHeight)
        let queryRange = try XCTUnwrap(query.textRange(from: query.beginningOfDocument, to: query.endOfDocument))
        XCTAssertTrue(query.bounds.insetBy(dx: -1, dy: -1).contains(query.firstRect(for: queryRange)))
        XCTAssertGreaterThan(query.firstRect(for: queryRange).height, 0)
        attach(dialog, "film-search-test-dialog-320-AXXXL-keyboard-query")
        for button in [cancel, confirm] {
            reveal(button)
            XCTAssertTrue(button.isEnabled)
            XCTAssertGreaterThanOrEqual(button.bounds.width, 44); XCTAssertGreaterThanOrEqual(button.bounds.height, 44)
            let label = try XCTUnwrap(button.titleLabel)
            let fit = label.sizeThatFits(.init(width: label.bounds.width, height: .greatestFiniteMagnitude))
            XCTAssertLessThanOrEqual(fit.width, label.bounds.width + 1)
            XCTAssertLessThanOrEqual(fit.height, label.bounds.height + 1)
            XCTAssertTrue(button.bounds.insetBy(dx: -1, dy: -1).contains(label.convert(label.bounds, to: button)))
            assertRegular(label.font)
            XCTAssertTrue(query.isFirstResponder)
            XCTAssertLessThan(dialog.view.keyboardLayoutGuide.layoutFrame.minY,
                              dialog.view.safeAreaLayoutGuide.layoutFrame.maxY - 100)
        }
        XCTAssertGreaterThan(scroll.contentOffset.y, 0)
        attach(dialog, "film-search-test-dialog-320-AXXXL-keyboard-actions")
        let pendingRequests = await fixture.ruleRPC.requests
        XCTAssertTrue(pendingRequests.isEmpty)
        try await confirmRuleTest(page)
        await eventually { !page.testing && page.result != nil && page.canPresentPageAlert }
        XCTAssertTrue(navigation.topViewController === page); XCTAssertEqual(navigation.viewControllers.count, 1)
        XCTAssertTrue(page.isPageVisible)
        XCTAssertEqual(page.result?.inputText.utf16, Array("广告\n作品2".utf16))
        XCTAssertEqual(page.result?.outputText.displayString, "作品2")
        let result = try XCTUnwrap(page.editors.first).resultLabel
        XCTAssertEqual(result.text, "测试结果：作品2"); XCTAssertFalse(result.isHidden)
        page.view.layoutIfNeeded()
        page.scrollView.scrollRectToVisible(result.convert(result.bounds, to: page.scrollView), animated: false)
        XCTAssertTrue(page.scrollView.bounds.insetBy(dx: -1, dy: -1).contains(result.convert(result.bounds, to: page.scrollView)))
        attach(page, "film-search-test-result-320-AXXXL")
        let requests = await fixture.ruleRPC.requests
        XCTAssertEqual(requests, [.init(section: .search, text: "广告\n作品2", query: "作品2")])
        XCTAssertEqual(try fixture.rulesDocument.read(), before)
        await fixture.rules.close(); await fixture.sources.close()
    }

    func testRulePreviewFailureRetriesThroughDismissedAlertAndCollectsInputBeforeRequest() async throws {
        let fixture = try await makeFixture()
        _ = try await fixture.rules.save(.search, rules: [.init(pattern: "^广告\\s+")])
        let before = try fixture.rulesDocument.read()
        var expectedRequests: [FilmRuleRequest] = []
        for cancelRetry in [true, false] {
            let owner = FilmRulesDraft(service: fixture.rules); try await owner.load()
            let page = FilmRulePageViewController(owner: owner, group: .search, themeProvider: FilmSettingsTheme())
            let window = try await host(page)
            await fixture.ruleRPC.failNextPreview()
            try ruleButton(page, "film.rules.test").sendActions(for: .touchUpInside)
            let firstDialog = try XCTUnwrap(page.presentedViewController as? ManagementDialog)
            page.testFields.input.text = "广告\n作品1"; page.testFields.query.text = "作品1"
            try await confirmRuleTest(page)
            await eventually { !page.testing && page.presentedViewController is UIAlertController }
            let alert = try XCTUnwrap(page.presentedViewController as? UIAlertController)
            await eventually { !alert.isBeingPresented && alert.view.window === window }
            XCTAssertNil(firstDialog.presentingViewController); XCTAssertNil(firstDialog.viewIfLoaded?.window)
            XCTAssertEqual(alert.title, "错误")
            XCTAssertEqual(alert.message, URLError(.cannotConnectToHost).localizedDescription)
            XCTAssertEqual(alert.actions.compactMap(\.title), ["取消", "重试"])
            XCTAssertNil(page.result); XCTAssertTrue(page.editors.allSatisfy { $0.resultLabel.isHidden })
            expectedRequests.append(.init(section: .search, text: "广告\n作品1", query: "作品1"))
            let failedRequests = await fixture.ruleRPC.requests
            XCTAssertEqual(failedRequests, expectedRequests)

            try await activateAlert(page, title: "重试")
            await eventually {
                guard let dialog = page.presentedViewController as? ManagementDialog else { return false }
                return !dialog.isBeingPresented && dialog.view.window === window
            }
            let retryDialog = try XCTUnwrap(page.presentedViewController as? ManagementDialog)
            XCTAssertFalse(retryDialog === firstDialog)
            XCTAssertNil(alert.presentingViewController); XCTAssertNil(alert.viewIfLoaded?.window)
            XCTAssertFalse(alert.isBeingDismissed)
            XCTAssertTrue(page.testFields.inputSection.isDescendant(of: retryDialog.body))
            XCTAssertTrue(page.testFields.querySection.isDescendant(of: retryDialog.body))
            XCTAssertEqual(page.testFields.input.text, "广告\n作品1"); XCTAssertEqual(page.testFields.query.text, "作品1")
            XCTAssertFalse(page.testing); XCTAssertNil(page.result)
            let reopenedRequests = await fixture.ruleRPC.requests
            XCTAssertEqual(reopenedRequests, expectedRequests, "Retry must collect input before issuing another preview")
            page.testFields.input.text = "广告\n作品2"
            page.testFields.input.delegate?.textViewDidChange?(page.testFields.input)
            page.testFields.query.text = "作品2"; page.testFields.query.sendActions(for: .editingChanged)
            if cancelRetry {
                let cancel = try XCTUnwrap(descendants(retryDialog.view).first {
                    $0.accessibilityIdentifier == "management.dialog.cancel"
                } as? UIButton)
                cancel.sendActions(for: .touchUpInside)
                await eventually { page.canPresentPageAlert && retryDialog.viewIfLoaded?.window == nil }
                XCTAssertNil(retryDialog.presentingViewController)
                XCTAssertFalse(page.testing); XCTAssertNil(page.result)
                XCTAssertTrue(page.editors.allSatisfy { $0.resultLabel.isHidden })
            } else {
                try await confirmRuleTest(page)
                await eventually { !page.testing && page.result != nil && page.canPresentPageAlert }
                expectedRequests.append(.init(section: .search, text: "广告\n作品2", query: "作品2"))
                XCTAssertEqual(page.result?.inputText.utf16, Array("广告\n作品2".utf16))
                XCTAssertEqual(page.result?.outputText.displayString, "作品2")
                XCTAssertEqual(page.editors.first?.resultLabel.text, "测试结果：作品2")
            }
            let completedRequests = await fixture.ruleRPC.requests
            XCTAssertEqual(completedRequests, expectedRequests)
            XCTAssertEqual(try fixture.rulesDocument.read(), before)
            XCTAssertEqual(owner.rules(.search), [.init(pattern: "^广告\\s+")])
            unhost(window)
        }
        await fixture.rules.close(); await fixture.sources.close()
        let requests = await fixture.ruleRPC.requests
        XCTAssertEqual(requests, expectedRequests)
        XCTAssertEqual(requests.count, 3, "Two failed previews, no cancelled retry request, and one confirmed retry")
    }

    func testRuleTestDialogConfirmationUsesActualDismissalReceiptBeforeNode() async throws {
        let fixture = try await makeFixture()
        _ = try await fixture.rules.save(.movie, rules: [.init(pattern: "^match$", replace: "result")])
        let owner = FilmRulesDraft(service: fixture.rules); try await owner.load()
        let page = FilmRulePageViewController(owner: owner, group: .movie, themeProvider: FilmSettingsTheme())
        let window = try await host(page); defer { unhost(window) }
        let before = try fixture.rulesDocument.read()
        try ruleButton(page, "film.rules.test").sendActions(for: .touchUpInside)
        let dialog = try XCTUnwrap(page.presentedViewController as? ManagementDialog)
        await eventually { !dialog.isBeingPresented && dialog.view.window != nil }
        page.testFields.input.text = "match"
        let submit = try XCTUnwrap(dialog.submit)
        dialog.submit = {
            let value = try await submit()
            XCTAssertTrue(value)
            XCTAssertFalse(page.testing); XCTAssertNil(page.result)
            let requests = await fixture.ruleRPC.requests
            XCTAssertTrue(requests.isEmpty, "Submitting the floating input must not await Node")
            return value
        }
        let accepted = try XCTUnwrap(dialog.accepted)
        let completed = expectation(description: "actual UIKit dismissal completion")
        dialog.accepted = { [weak dialog] in
            XCTAssertNil(dialog?.presentingViewController)
            XCTAssertNil(dialog?.viewIfLoaded?.window)
            XCTAssertFalse(dialog?.isBeingDismissed ?? true)
            XCTAssertFalse(page.testing); XCTAssertNil(page.result)
            accepted()
            XCTAssertTrue(page.canPresentPageAlert)
            completed.fulfill()
        }
        await fixture.ruleRPC.holdNextPreviewReceipt()
        try await confirmRuleTest(page)
        await fulfillment(of: [completed], timeout: 5)
        await fixture.ruleRPC.waitForPreviewReceipt()
        XCTAssertTrue(page.testing)
        let requests = await fixture.ruleRPC.requests
        XCTAssertEqual(requests, [.init(section: .movie, text: "match")])
        await fixture.ruleRPC.releasePreviewReceipt()
        await eventually { !page.testing && page.result != nil }
        XCTAssertEqual(page.result?.outputText.displayString, "result")
        XCTAssertEqual(page.editors.first?.resultLabel.text, "测试结果：result")
        XCTAssertEqual(try fixture.rulesDocument.read(), before)
        await fixture.rules.close(); await fixture.sources.close()
    }

    func testRuleTestDialogRejectsChangedSubmissionOrInactivePage() async throws {
        let fixture = try await makeFixture()
        _ = try await fixture.rules.save(.search, rules: [.init(pattern: "^广告")])
        let before = try fixture.rulesDocument.read()
        for change in ["input", "query", "rules", "cancel", "hidden", "inactive"] {
            let owner = FilmRulesDraft(service: fixture.rules); try await owner.load()
            let page = FilmRulePageViewController(owner: owner, group: .search, themeProvider: FilmSettingsTheme())
            let window = try await host(page)
            page.testFields.input.text = "广告作品2"; page.testFields.query.text = "作品2"
            try ruleButton(page, "film.rules.test").sendActions(for: .touchUpInside)
            let dialog = try XCTUnwrap(page.presentedViewController as? ManagementDialog)
            await eventually { !dialog.isBeingPresented && dialog.view.window != nil }
            let submit = try XCTUnwrap(dialog.submit)
            dialog.submit = {
                let value = try await submit()
                switch change {
                case "input":
                    page.testFields.input.text = "changed"
                    page.testFields.input.delegate?.textViewDidChange?(page.testFields.input)
                case "query":
                    page.testFields.query.text = "作品3"; page.testFields.query.sendActions(for: .editingChanged)
                case "rules": owner.edit(.search, index: 0, rule: .init(pattern: "^草稿"))
                case "hidden": page.pageDidBecomeHidden()
                case "inactive": PageViewController.invalidatePages(in: page)
                default: page.cancelTest()
                }
                return value
            }
            try await confirmRuleTest(page)
            await settle()
            XCTAssertFalse(page.testing, change); XCTAssertNil(page.result, change)
            XCTAssertTrue(page.editors.allSatisfy { $0.resultLabel.isHidden }, change)
            let requests = await fixture.ruleRPC.requests
            XCTAssertTrue(requests.isEmpty, change)
            XCTAssertEqual(try fixture.rulesDocument.read(), before)
            unhost(window)
        }
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
        let before = try fixture.rulesDocument.read()
        for group in [FilmRuleGroup.episodeClean, .episode] {
            let page = FilmRulePageViewController(owner: owner, group: group, themeProvider: FilmSettingsTheme())
            let window = try await host(page)
            page.testFields.input.text = group == .episodeClean ? "草稿第12集" : "广告第12集"
            try await runRuleTest(page)
            await eventually { !page.testing && page.result != nil }
            let result = try XCTUnwrap(page.result)
            XCTAssertEqual(result.processedText.displayString, "第12集")
            XCTAssertEqual(result.outputText.displayString, group == .episodeClean ? "E12" : "Draft12")
            XCTAssertTrue(result.errors.isEmpty)
            XCTAssertEqual(page.editors.first?.resultLabel.text,
                group == .episodeClean ? "测试结果：第12集" : "测试结果：Draft12")
            unhost(window)
        }
        let after = try fixture.rulesDocument.read()
        XCTAssertEqual(after, before)
        XCTAssertEqual(owner.rules(.episodeClean).first?.pattern, "^草稿")
        XCTAssertEqual(owner.rules(.episode).first?.replace, "Draft$1")
        await fixture.rules.close(); await fixture.sources.close()
    }

    func testNativeGroupSaveWhilePreviewHeldKeepsReceiptAfterPageRemovalAndWaiterCancellation() async throws {
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
            // Native settings must complete independently of this held Node
            // preview. Only their real local commit receipt is paused below.
            await fixture.ruleRPC.holdNextPreviewReceipt()
            let preview = Task { try await fixture.rules.process(.init(section: .movie, text: "barrier")) }
            await fixture.ruleRPC.waitForPreviewReceipt()
            let committed = expectation(description: "native group committed while preview held")
            fixture.ruleStorage.pauseNextCommit { committed.fulfill() }
            let before = try FilmRuleSettings.read(fixture.rulesDocument.read())
            let waiter: Task<Void, Error>?
            if cancelWaiter {
                waiter = Task { try await owner.save(.movie) }
            } else {
                waiter = nil; page.save()
            }
            await fulfillment(of: [committed], timeout: 5)
            XCTAssertTrue(owner.busy)
            let persisted = try FilmRuleSettings.read(fixture.rulesDocument.read())
            XCTAssertEqual(persisted.movieRules.first?.pattern, normalized)
            XCTAssertEqual(persisted.episodeCleanRegexRules, before.episodeCleanRegexRules)
            XCTAssertEqual(owner.rules(.movie).first?.pattern, "  \(normalized)  ")
            waiter?.cancel()
            navigation.popViewController(animated: false)
            await eventually { !page.isPageActive }
            XCTAssertTrue(owner.busy)
            fixture.ruleStorage.releaseReceipt()
            try await waiter?.value
            await eventually { !owner.busy }
            XCTAssertEqual(owner.rules(.movie).first?.pattern, normalized)
            XCTAssertEqual(owner.rules(.movie).first?.replace, " keep ")
            XCTAssertEqual(owner.rules(.episodeClean).first?.pattern, "other pending")
            let saved = try await fixture.rules.savedSnapshot()
            XCTAssertEqual(saved.movieRules, owner.rules(.movie))
            XCTAssertNotEqual(saved.episodeCleanRegexRules.first, "other pending")
            await fixture.ruleRPC.releasePreviewReceipt()
            _ = try await preview.value
            XCTAssertEqual(try FilmRuleSettings.read(fixture.rulesDocument.read()), saved)
            unhost(window)
        }
        await fixture.rules.close(); await fixture.sources.close()
    }

    func testRuleFieldsShowEditableFlagsBesideReplacementInTheSameCard() async throws {
        let fixture = try await makeFixture()
        try await fixture.rules.initialize()
        let owner = FilmRulesDraft(service: fixture.rules); try await owner.load()
        for group in [FilmRuleGroup.episode, .movie] {
            owner.edit(group, index: 0, rule: .init(pattern: "original", replace: "before", flags: "g"))
            for (width, largeType) in [(CGFloat(390), false), (CGFloat(320), false), (CGFloat(390), true)] {
                let page = FilmRulePageViewController(owner: owner, group: group, themeProvider: FilmSettingsTheme())
                if largeType { page.traitOverrides.preferredContentSizeCategory = .accessibilityExtraExtraExtraLarge }
                let window = try await host(page, size: CGSize(width: width, height: 844))
                await settle(); page.view.layoutIfNeeded()
                let editor = try XCTUnwrap(page.editors.first)
                editor.layoutIfNeeded()
                let replacement = try XCTUnwrap(editor.replacement)
                let flags = try XCTUnwrap(editor.flags)
                assertRuleUnderline(editor.pattern)
                assertRuleUnderline(replacement)
                assertRuleUnderline(flags)
                XCTAssertEqual(editor.deleteButton.titleColor(for: .normal), UIColor(page.theme.settings.danger))
                XCTAssertEqual(editor.pattern.font?.pointSize,
                    UIFont.preferredFont(forTextStyle: .body, compatibleWith: editor.traitCollection).pointSize)
                let replace = replacement.convert(replacement.bounds, to: editor)
                let flagsFrame = flags.convert(flags.bounds, to: editor)
                if largeType {
                    XCTAssertGreaterThanOrEqual(flagsFrame.minY, replace.maxY)
                    XCTAssertEqual(replace.width, flagsFrame.width, accuracy: 1)
                } else {
                    XCTAssertEqual(replace.minY, flagsFrame.minY, accuracy: 1)
                    XCTAssertEqual(replace.maxY, flagsFrame.maxY, accuracy: 1)
                    XCTAssertGreaterThan(replace.width, flagsFrame.width)
                    XCTAssertGreaterThanOrEqual(flagsFrame.minX, replace.maxX)
                }
                XCTAssertEqual(flags.text, owner.rules(group).first?.flags)
                editor.pattern.text = "changed"
                editor.pattern.delegate?.textViewDidChange?(editor.pattern)
                replacement.text = "replacement"
                replacement.sendActions(for: .editingChanged)
                flags.text = "gi"; flags.sendActions(for: .editingChanged)
                XCTAssertEqual(owner.rules(group).first, .init(pattern: "changed", replace: "replacement", flags: "gi"))
                XCTAssertTrue(editor.pattern.isDescendant(of: editor))
                XCTAssertTrue(replacement.isDescendant(of: editor))
                XCTAssertTrue(flags.isDescendant(of: editor))
                XCTAssertEqual(flags.accessibilityIdentifier, "film.rules.\(group.rawValue).0.flags")
                unhost(window)
            }
            try await owner.save(group)
            let saved = try await fixture.rules.savedSnapshot()
            XCTAssertEqual(saved.rules(in: group).first, .init(pattern: "changed", replace: "replacement", flags: "gi"))
        }
        await fixture.rules.close(); await fixture.sources.close()
    }

    func testNewMatchingRuleWithEmptyFlagsKeepsRuntimeCaseInsensitiveDefault() async throws {
        let fixture = try await makeFixture()
        _ = try await fixture.rules.save(.movie, rules: [])
        let owner = FilmRulesDraft(service: fixture.rules); try await owner.load()
        let page = FilmRulePageViewController(owner: owner, group: .movie, themeProvider: FilmSettingsTheme())
        let window = try await host(page); defer { unhost(window) }
        try ruleButton(page, "film.rules.movie.add").sendActions(for: .touchUpInside)
        let editor = try XCTUnwrap(page.editors.first)
        editor.pattern.text = "^movie$"; editor.pattern.delegate?.textViewDidChange?(editor.pattern)
        let replacement = try XCTUnwrap(editor.replacement)
        XCTAssertEqual(editor.flags?.text, "")
        replacement.text = "matched"; replacement.sendActions(for: .editingChanged)
        try await owner.save(.movie)
        let saved = try await fixture.rules.savedSnapshot()
        XCTAssertEqual(saved.movieRules.first?.pattern, "^movie$")
        XCTAssertEqual(saved.movieRules.first?.replace, "matched")
        XCTAssertEqual(saved.movieRules.first?.flags, "")
        let test = FilmRulePageViewController(owner: owner, group: .movie, themeProvider: FilmSettingsTheme())
        let testWindow = try await host(test); defer { unhost(testWindow) }
        test.testFields.input.text = "MOVIE"
        try await runRuleTest(test)
        await eventually { !test.testing && test.result != nil }
        XCTAssertEqual(test.result?.outputText.displayString, "matched")
        XCTAssertEqual(test.result?.matched, true)
        let after = try await fixture.rules.savedSnapshot()
        XCTAssertEqual(after, saved)
        await fixture.rules.close(); await fixture.sources.close()
    }

    func testCatBatchDeleteRetryContinuesAfterReceiptAndCancelDoesNotDeleteMore() async throws {
        for retry in [true, false] {
            let fixture = try await makeFixture()
            try await fixture.sources.refresh()
            fixture.catStorage.failNextWrite(afterDeleting: "a")
            let page = FilmCatSourcesViewController(service: fixture.sources, themeProvider: FilmSettingsTheme())
            let window = try await host(page)
            await eventually { page.snapshot?.cats.count == 2 && page.canPresentPageAlert }
            try ManagementListTestUI.button("management.selectAll", in: page.manager).sendActions(for: .touchUpInside)
            page.manager.deleteButton.sendActions(for: .touchUpInside)
            try await activateAlert(page, title: "确认")
            await eventually { !page.busy && (page.presentedViewController as? UIAlertController)?.title == "错误" }
            let partial = await fixture.sources.snapshot
            XCTAssertEqual(partial.cats.map(\.installationId), ["b"])
            try fixture.catStorage.checkFault()
            XCTAssertEqual(fixture.catStorage.scriptReceipts, [["b"]])
            try fixture.catStorage.restoreWrites()
            try await activateAlert(page, title: retry ? "重试" : "取消")
            await eventually { !page.busy && page.presentedViewController == nil }
            let calls = await fixture.runtime.calls
            XCTAssertFalse(calls.contains("uninstall"), "Deleting native intent must not borrow the runtime")
            XCTAssertEqual(fixture.catStorage.scriptReceipts, retry ? [["b"], []] : [["b"]])
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

    func testCatBatchNativeDeletionNeverRequestsFollowingRuntimeStatus() async throws {
        let fixture = try await makeFixture()
        try await fixture.sources.refresh()
        await fixture.runtime.failStatus()
        let before = await fixture.runtime.calls
        let page = FilmCatSourcesViewController(service: fixture.sources, themeProvider: FilmSettingsTheme())
        let window = try await host(page); defer { unhost(window) }
        await eventually { page.snapshot?.cats.count == 2 && page.canPresentPageAlert }
        try ManagementListTestUI.button("management.selectAll", in: page.manager).sendActions(for: .touchUpInside)
        page.manager.deleteButton.sendActions(for: .touchUpInside)
        try await activateAlert(page, title: "确认")
        await eventually { !page.busy && page.snapshot?.cats.isEmpty == true }
        let after = await fixture.runtime.calls
        XCTAssertEqual(after, before, "A committed native deletion needs no following runtime status receipt")
        XCTAssertEqual(fixture.catStorage.scriptReceipts, [["b"], []])
        XCTAssertNil(page.presentedViewController)
        let scripts = try XCTUnwrap(fixture.catDocument.read()?.filmObject?["scripts"]?.filmArray)
        XCTAssertTrue(scripts.isEmpty)
        await fixture.sources.close(); await fixture.rules.close()
    }

    func testVideoListBatchConfirmationCancelsThenDeletesSelectedRows() async throws {
        let fixture = try await makeFixture()
        try await fixture.sources.refresh()
        let page = FilmVideoSourcesViewController(service: fixture.sources, themeProvider: FilmSettingsTheme())
        let window = try await host(page); defer { unhost(window) }
        await eventually { page.snapshot?.videos.count == 2 && page.canPresentPageAlert }
        try ManagementListTestUI.button("management.selectAll", in: page.manager).sendActions(for: .touchUpInside)
        page.manager.deleteButton.sendActions(for: .touchUpInside)
        try await activateAlert(page, title: "取消")
        let cancelled = await fixture.sources.snapshot
        XCTAssertEqual(cancelled.videos.count, 2)
        page.manager.deleteButton.sendActions(for: .touchUpInside)
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
        try ManagementListTestUI.button("management.selectAll", in: page.manager).sendActions(for: .touchUpInside)
        try ManagementListTestUI.activateMenu("检测所选", on: page.manager.footerMore)
        await fixture.runtime.waitForProbe()
        for id in ["a", "b"] { try ManagementListTestUI.select("film.video.\(id).site", in: page.manager) }
        XCTAssertTrue(page.detecting)
        XCTAssertTrue(page.manager.selected.isEmpty)
        try ManagementListTestUI.activateMenu("取消检测", on: page.manager.footerMore)
        XCTAssertFalse(page.detecting)
        await fixture.runtime.waitForProbeCancellation()
        await fixture.runtime.releaseProbe()
        // The transport acknowledged page cancellation before this drain. Drain
        // now makes the assertion observe the settled late result, not a race.
        await fixture.sources.prepareShutdown()
        let snapshot = await fixture.sources.snapshot
        XCTAssertTrue(snapshot.videos.allSatisfy { $0.detection == nil })
        try await fixture.sources.resume()
        try ManagementListTestUI.select("film.video.a.site", in: page.manager)
        _ = try ManagementListTestUI.menuAction("检测所选", on: page.manager.footerMore)
        await fixture.sources.close(); await fixture.rules.close()
    }

    func testReturningFromAcceptedLocalBatchUpdateDoesNotPresentOrSubmitNextItem() async throws {
        for secondLocal in [true, false] {
            let fixture = try await makeFixture()
            try await fixture.runtime.setLocal(secondLocal ? ["a", "b"] : ["a"])
            try await fixture.sources.refresh()
            let page = FilmCatSourcesViewController(service: fixture.sources, themeProvider: FilmSettingsTheme())
            let navigation = PageNavigationController(themeProvider: FilmSettingsTheme())
            navigation.setViewControllers([page], animated: false)
            let window = try await host(navigation)
            await eventually { page.snapshot?.cats.count == 2 }
            try ManagementListTestUI.button("management.selectAll", in: page.manager).sendActions(for: .touchUpInside)
            try ManagementListTestUI.activateMenu("更新所选", on: page.manager.footerMore)
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
            let before = try fixture.catDocument.read()
            try selectRow(editor, "film.cat.editor.save")
            // The accepted local transaction has no remote receipt to hold.
            // Remove the page in this same MainActor turn, before its save task
            // can finish and hand the batch to the next editor.
            navigation.popViewController(animated: false)
            await eventually { !editor.isPageActive && navigation.topViewController === page }
            await eventually {
                editor.sections.flatMap(\.rows).first { $0.id == "film.cat.editor.save" }?.title == "保存"
            }
            await settle()
            XCTAssertTrue(navigation.topViewController === page)
            let updates = await fixture.runtime.updates
            XCTAssertTrue(updates.isEmpty, "A local input replacement is a native settings transaction")
            XCTAssertNotEqual(try fixture.catDocument.read(), before)
            let result = await fixture.sources.snapshot
            XCTAssertEqual(result.cats.first { $0.installationId == "a" }?.inputRevision, 2)
            XCTAssertEqual(result.cats.first { $0.installationId == "b" }?.inputRevision, 1)
            XCTAssertEqual(result.cats.first { $0.installationId == "a" }?.state, "stopped",
                           "Saved input must not be presented as already loaded")
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
        try ManagementListTestUI.open("film.sources.runtimeError", in: page.manager)
        let alert = try XCTUnwrap(page.presentedViewController as? UIAlertController)
        XCTAssertEqual(alert.title, "运行器不可用")
        XCTAssertEqual(alert.actions.map(\.title), ["取消", "重试"])
        XCTAssertFalse(ManagementListTestUI.rowIDs(in: page.manager).contains("management.row.film.sources.retry"))
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
        let before = try fixture.rulesDocument.read()
        let editor = try XCTUnwrap(page.editors.first)
        XCTAssertTrue(editor.resultLabel.isHidden)
        try ruleButton(page, "film.rules.test").sendActions(for: .touchUpInside)
        let dialog = try XCTUnwrap(page.presentedViewController as? ManagementDialog)
        await eventually { !dialog.isBeingPresented && dialog.view.window != nil }
        assertRuleUnderline(page.testFields.input)
        page.testFields.input.becomeFirstResponder(); page.testFields.input.insertText("🎬")
        try await confirmRuleTest(page)
        await eventually { !page.testing && page.result != nil }
        let result = try XCTUnwrap(page.result)
        XCTAssertEqual(result.outputText.utf16, [0x0078, 0xDFAC])
        XCTAssertTrue(page.editors.first === editor)
        XCTAssertEqual(editor.resultLabel.text, "测试结果：x\u{FFFD}")
        XCTAssertEqual(editor.resultLabel.accessibilityLabel, "测试结果：x\u{FFFD}")
        XCTAssertFalse(editor.resultLabel.isHidden)
        XCTAssertEqual(page.testFields.input.text, "🎬")
        XCTAssertEqual(owner.rules(.movie).first?.pattern, "^.")
        let after = try fixture.rulesDocument.read()
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
            try ManagementListTestUI.button("management.selectAll", in: page.manager).sendActions(for: .touchUpInside)
            try ManagementListTestUI.activateMenu("重启所选", on: page.manager.footerMore)
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
                request: { _, _ in throw FilmSourceError(code: "OFFLINE") }),
                                           storeFactory: { fixture.sourceStore }, settingsDocument: fixture.catDocument,
                                           runtimeSourceRoot: fixture.runtimeSourceRoot)
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

    func testCatDetailKeepsEditingButDeletionIsOnlyAvailableThroughBatchSelection() async throws {
        let fixture = try await makeFixture()
        try await fixture.sources.refresh()
        let page = FilmCatSourcesViewController(service: fixture.sources, themeProvider: FilmSettingsTheme())
        let navigation = PageNavigationController(themeProvider: FilmSettingsTheme())
        navigation.setViewControllers([page], animated: false)
        let window = try await host(navigation)
        await eventually { page.snapshot?.cats.count == 2 }
        try ManagementListTestUI.open("film.cat.a", in: page.manager)
        let detail = try XCTUnwrap(navigation.topViewController as? FilmCatDetailController)
        await eventually { detail.snapshot?.cats.count == 2 && detail.canPresentPageAlert }
        XCTAssertFalse(detail.sections.flatMap(\.rows).contains { $0.title == "删除" })
        XCTAssertTrue(detail.sections.flatMap(\.rows).contains { $0.id == "film.cat.edit" })
        XCTAssertTrue(detail.sections.flatMap(\.rows).contains { $0.id == "film.cat.update" })
        navigation.popViewController(animated: false)
        await eventually { page.canPresentPageAlert }
        try ManagementListTestUI.select("film.cat.a", in: page.manager)
        XCTAssertEqual(page.manager.selected, ["film.cat.a"])
        XCTAssertTrue(page.manager.deleteButton.isEnabled)
        page.manager.deleteButton.sendActions(for: .touchUpInside)
        try await activateAlert(page, title: "取消")
        XCTAssertEqual(page.snapshot?.cats.map(\.installationId), ["a", "b"])
        XCTAssertTrue(fixture.catStorage.notifications.isEmpty)
        unhost(window)
        await fixture.sources.close(); await fixture.rules.close()
    }

    func testEmptyInputKeepsLosslessResultAndMarksMatchingCardsUnexecuted() async throws {
        let fixture = try await makeFixture()
        _ = try await fixture.rules.save(.movie, rules: [.init(pattern: "^nonempty$")])
        let owner = FilmRulesDraft(service: fixture.rules); try await owner.load()
        let page = FilmRulePageViewController(owner: owner, group: .movie, themeProvider: FilmSettingsTheme())
        let window = try await host(page); defer { unhost(window) }
        page.testFields.input.text = ""
        try await runRuleTest(page)
        await eventually { !page.testing && page.result != nil }
        XCTAssertEqual(page.result?.outputText.displayString, "")
        XCTAssertEqual(page.editors.first?.resultLabel.text, "测试结果：未执行")
        XCTAssertEqual(page.editors.first?.resultLabel.accessibilityLabel, "测试结果：未执行")
        await fixture.sources.close(); await fixture.rules.close()
    }

    func testInlineCleanPreviewsKeepSubmittedOrderEmptyRowsAndSearchTailProtection() async throws {
        let fixture = try await makeFixture()
        _ = try await fixture.rules.save(.init(
            episodeCleanRegexRules: ["^广告", "\\s+", "["],
            episodeRules: [.init(pattern: "^第([0-9]+)集$", replace: "E$1")],
            aggregateRegexRules: ["\\([^)]*\\)", "\\d+$", "["]))
        let owner = FilmRulesDraft(service: fixture.rules); try await owner.load()
        owner.add(.search)
        let before = try fixture.rulesDocument.read()
        for group in [FilmRuleGroup.episodeClean, .search] {
            let page = FilmRulePageViewController(owner: owner, group: group, themeProvider: FilmSettingsTheme())
            let window = try await host(page)
            page.testFields.input.text = group == .search ? "作品2(广告)" : "广告 第12集"
            page.testFields.query.text = "作品2"
            try await runRuleTest(page)
            await eventually { !page.testing && page.result != nil }
            let result = try XCTUnwrap(page.result)
            XCTAssertEqual(result.previews.count, owner.rules(group).count)
            for (editor, preview) in zip(page.editors, result.previews) {
                XCTAssertEqual(editor.resultLabel.text, "测试结果：" + (preview.isEmpty ? "空结果" : preview.displayString))
                XCTAssertNil(editor.replacement); XCTAssertNil(editor.flags)
            }
            XCTAssertEqual(page.editors[2].resultLabel.textColor, UIColor(page.theme.settings.danger))
            if group == .search {
                XCTAssertEqual(page.editors[0].resultLabel.text, "测试结果：作品2")
                XCTAssertEqual(page.editors[1].resultLabel.text, "测试结果：作品2（跳过尾号规则）")
                XCTAssertEqual(page.editors[3].resultLabel.text, "测试结果：作品2")
                XCTAssertFalse(page.testFields.query.isHidden)
            } else {
                XCTAssertEqual(page.editors[0].resultLabel.text, "测试结果：第12集")
                XCTAssertEqual(result.outputText.displayString, "E12")
                XCTAssertTrue(page.testFields.query.isHidden)
            }
            XCTAssertTrue(page.sections.isEmpty, "Inline previews must not recreate an output table")
            attach(page, "film-rule-inline-results-\(group.rawValue)")
            unhost(window)
        }
        XCTAssertEqual(try fixture.rulesDocument.read(), before)
        await fixture.sources.close(); await fixture.rules.close()
    }

    func testInlineMatchingSeparatesGroupErrorsFirstHitAndUnexecutedRules() async throws {
        let fixture = try await makeFixture()
        _ = try await fixture.rules.save(.init(
            episodeCleanRegexRules: ["["],
            episodeRules: [.init(pattern: "^miss$"), .init(pattern: "^第([0-9]+)集$", replace: "E$1"),
                           .init(pattern: "[")],
            movieRules: [.init(pattern: "["), .init(pattern: "^miss$"),
                         .init(pattern: "^match$", replace: "rewritten"), .init(pattern: "[")]))
        let owner = FilmRulesDraft(service: fixture.rules); try await owner.load()
        for group in [FilmRuleGroup.episode, .movie] {
            let page = FilmRulePageViewController(owner: owner, group: group, themeProvider: FilmSettingsTheme())
            let window = try await host(page)
            page.testFields.input.text = group == .episode ? "第12集" : "match"
            try await runRuleTest(page)
            await eventually { !page.testing && page.result != nil }
            let result = try XCTUnwrap(page.result)
            if group == .episode {
                XCTAssertEqual(result.previews.first?.displayString, "正则无效")
                XCTAssertEqual(result.errors.first?.group, "episodeClean")
                XCTAssertEqual(page.editors[0].resultLabel.text, "测试结果：未命中")
                XCTAssertEqual(page.editors[1].resultLabel.text, "测试结果：E12")
                XCTAssertEqual(page.editors[1].resultLabel.textColor, UIColor(page.theme.settings.ink))
                XCTAssertEqual(page.editors[2].resultLabel.text, "测试结果：未执行")
            } else {
                XCTAssertTrue(page.editors[0].resultLabel.text?.contains("正则无效") == true)
                XCTAssertEqual(page.editors[0].resultLabel.textColor, UIColor(page.theme.settings.danger))
                XCTAssertEqual(page.editors[1].resultLabel.text, "测试结果：未命中")
                XCTAssertEqual(page.editors[2].resultLabel.text, "测试结果：rewritten")
                XCTAssertEqual(page.editors[3].resultLabel.text, "测试结果：未执行")
                page.testFields.input.text = "unknown"
                page.testFields.input.delegate?.textViewDidChange?(page.testFields.input)
                XCTAssertNil(page.result)
                try await runRuleTest(page)
                await eventually { !page.testing && page.result != nil }
                XCTAssertEqual(page.editors[1].resultLabel.text, "测试结果：未命中")
                XCTAssertEqual(page.editors[2].resultLabel.text, "测试结果：未命中")
                XCTAssertTrue(page.editors[3].resultLabel.text?.contains("正则无效") == true)
            }
            unhost(window)
        }
        await fixture.sources.close(); await fixture.rules.close()
    }

    func testInlineEmptyPreviewAndFailedRewriteStayDifferentFromUntestedOrUnmatched() async throws {
        let fixture = try await makeFixture()
        _ = try await fixture.rules.save(.init(movieRules: [.init(pattern: "^match()$", replace: "$1")],
                                               aggregateRegexRules: ["^match$"]))
        let owner = FilmRulesDraft(service: fixture.rules); try await owner.load()
        for group in [FilmRuleGroup.search, .movie] {
            let page = FilmRulePageViewController(owner: owner, group: group, themeProvider: FilmSettingsTheme())
            let window = try await host(page)
            page.testFields.input.text = "match"
            try await runRuleTest(page)
            await eventually { !page.testing && page.result != nil }
            let result = try XCTUnwrap(page.result)
            XCTAssertEqual(result.outputText.utf16, [])
            XCTAssertFalse(try XCTUnwrap(page.editors.first).resultLabel.isHidden)
            if group == .search {
                XCTAssertEqual(page.editors.first?.resultLabel.text, "测试结果：空结果")
                XCTAssertEqual(result.previews.first?.utf16, [])
            } else {
                XCTAssertEqual(result.rewrittenText?.utf16, [])
                XCTAssertTrue(result.matched)
                XCTAssertEqual(page.editors.first?.resultLabel.text, "测试结果：空结果（改写失败）")
                let replacement = try XCTUnwrap(page.editors.first?.replacement)
                replacement.text = ""; replacement.sendActions(for: .editingChanged)
                try await runRuleTest(page)
                await eventually { !page.testing && page.result != nil }
                XCTAssertNil(page.result?.rewrittenText)
                XCTAssertEqual(page.editors.first?.resultLabel.text, "测试结果：match（无改写）")
            }
            unhost(window)
        }
        await fixture.sources.close(); await fixture.rules.close()
    }

    func testInlineResultAndThemeUpdatesKeepActiveFieldsSelectionAndLongHeaderReadable() async throws {
        let fixture = try await makeFixture()
        let output = String(repeating: "结果", count: 100)
        _ = try await fixture.rules.save(.movie, rules: [.init(pattern: "^match$", replace: output)])
        let owner = FilmRulesDraft(service: fixture.rules); try await owner.load()
        let page = FilmRulePageViewController(owner: owner, group: .movie, themeProvider: FilmSettingsTheme())
        let window = try await host(page, size: .init(width: 320, height: 844)); defer { unhost(window) }
        let editor = try XCTUnwrap(page.editors.first)
        await fixture.ruleRPC.holdNextPreviewReceipt()
        page.testFields.input.text = "match"
        try await runRuleTest(page)
        await fixture.ruleRPC.waitForPreviewReceipt()
        editor.pattern.becomeFirstResponder()
        let selection = NSRange(location: 1, length: 2)
        editor.pattern.selectedRange = selection
        await fixture.ruleRPC.releasePreviewReceipt()
        await eventually { !page.testing && page.result != nil }
        XCTAssertTrue(page.editors.first === editor)
        XCTAssertTrue(editor.pattern.isFirstResponder)
        XCTAssertEqual(editor.pattern.selectedRange, selection)
        XCTAssertEqual(editor.resultLabel.text, "测试结果：" + output)
        page.traitOverrides.userInterfaceStyle = .dark
        page.traitOverrides.preferredContentSizeCategory = .accessibilityExtraExtraExtraLarge
        await settle(); page.view.layoutIfNeeded()
        XCTAssertTrue(page.editors.first === editor)
        XCTAssertTrue(editor.pattern.isFirstResponder)
        XCTAssertEqual(editor.pattern.selectedRange, selection)
        XCTAssertEqual(editor.pattern.text, "^match$")
        let result = editor.resultLabel.convert(editor.resultLabel.bounds, to: editor)
        let remove = editor.deleteButton.convert(editor.deleteButton.bounds, to: editor)
        XCTAssertGreaterThan(result.height, editor.resultLabel.font.lineHeight)
        XCTAssertLessThanOrEqual(result.maxX, remove.minX)
        XCTAssertGreaterThanOrEqual(remove.width, 44)
        XCTAssertGreaterThanOrEqual(remove.height, 44)
        let footer = try ruleActions(page)
        XCTAssertEqual(footer.convert(footer.bounds, to: page.view).maxY,
                       page.view.keyboardLayoutGuide.layoutFrame.minY, accuracy: 1)
        XCTAssertEqual(page.scrollView.convert(page.scrollView.bounds, to: page.view).maxY,
                       footer.convert(footer.bounds, to: page.view).minY, accuracy: 1)
        assertRuleUnderline(editor.pattern)
        try assertRuleHeading(editor)
        editor.pattern.text = "^changed$"; editor.pattern.delegate?.textViewDidChange?(editor.pattern)
        XCTAssertNil(page.result); XCTAssertTrue(editor.resultLabel.isHidden)
        await fixture.sources.close(); await fixture.rules.close()
    }

    func testInlineInputQueryRuleChangesAndCancelRejectHeldPreviewReceipts() async throws {
        let fixture = try await makeFixture()
        _ = try await fixture.rules.save(.search, rules: [.init(pattern: "^广告")])
        let owner = FilmRulesDraft(service: fixture.rules); try await owner.load()
        let page = FilmRulePageViewController(owner: owner, group: .search, themeProvider: FilmSettingsTheme())
        let window = try await host(page); defer { unhost(window) }
        let before = try fixture.rulesDocument.read()
        for change in ["input", "query", "pattern", "cancel"] {
            page.testFields.input.text = "广告作品2"; page.testFields.query.text = "作品2"
            let editor = try XCTUnwrap(page.editors.first)
            await fixture.ruleRPC.holdNextPreviewReceipt()
            try await runRuleTest(page)
            await fixture.ruleRPC.waitForPreviewReceipt()
            XCTAssertTrue(page.testing)
            switch change {
            case "input":
                page.testFields.input.text = "changed"
                page.testFields.input.delegate?.textViewDidChange?(page.testFields.input)
            case "query":
                page.testFields.query.text = "作品3"; page.testFields.query.sendActions(for: .editingChanged)
            case "pattern":
                editor.pattern.text = "^作品"; editor.pattern.delegate?.textViewDidChange?(editor.pattern)
            default: try ruleButton(page, "film.rules.test").sendActions(for: .touchUpInside)
            }
            XCTAssertFalse(page.testing); XCTAssertNil(page.result)
            XCTAssertTrue(editor.resultLabel.isHidden)
            XCTAssertTrue(page.editors.first === editor)
            await fixture.ruleRPC.releasePreviewReceipt()
            _ = try await fixture.rules.process(.init(section: .search, text: "drained"), group: .search, rules: [])
            XCTAssertNil(page.result); XCTAssertTrue(editor.resultLabel.isHidden)
        }
        XCTAssertEqual(try fixture.rulesDocument.read(), before)
        await fixture.sources.close(); await fixture.rules.close()
    }

    func testInlinePreviewDoesNotBlockNativeSaveOrPublishAfterSaveNormalization() async throws {
        let fixture = try await makeFixture()
        _ = try await fixture.rules.save(.movie, rules: [.init(pattern: "^match$")])
        let owner = FilmRulesDraft(service: fixture.rules); try await owner.load()
        owner.edit(.movie, index: 0, rule: .init(pattern: "  ^match$  ", replace: "saved"))
        let page = FilmRulePageViewController(owner: owner, group: .movie, themeProvider: FilmSettingsTheme())
        let window = try await host(page); defer { unhost(window) }
        page.testFields.input.text = "match"
        await fixture.ruleRPC.holdNextPreviewReceipt()
        try await runRuleTest(page)
        await fixture.ruleRPC.waitForPreviewReceipt()
        page.save()
        await eventually { !page.writing && !owner.busy }
        XCTAssertEqual(try FilmRuleSettings.read(fixture.rulesDocument.read()).movieRules.first?.pattern, "^match$")
        XCTAssertEqual(page.editors.first?.pattern.text, "^match$")
        XCTAssertNil(page.result); XCTAssertFalse(page.testing)
        await fixture.ruleRPC.releasePreviewReceipt()
        _ = try await fixture.rules.process(.init(section: .movie, text: "drained"), group: .movie, rules: [])
        XCTAssertNil(page.result)
        XCTAssertTrue(page.editors.allSatisfy { $0.resultLabel.isHidden })
        await fixture.sources.close(); await fixture.rules.close()
    }

    func testCatBatchDeletionReceiptDoesNotNavigateOrAlertOverAnotherDetail() async throws {
        for statusUnavailable in [false, true] {
            let fixture = try await makeFixture()
            try await fixture.sources.refresh()
            if statusUnavailable { await fixture.runtime.failStatus() }
            let receipt = expectation(description: "Native deletion committed")
            fixture.catStorage.pauseNextCommit { receipt.fulfill() }
            addTeardownBlock { fixture.catStorage.releaseReceipt() }
            let page = FilmCatSourcesViewController(service: fixture.sources, themeProvider: FilmSettingsTheme())
            let navigation = PageNavigationController(themeProvider: FilmSettingsTheme())
            navigation.setViewControllers([page], animated: false)
            let window = try await host(navigation)
            await eventually { page.snapshot?.cats.count == 2 && page.canPresentPageAlert }
            try ManagementListTestUI.select("film.cat.a", in: page.manager)
            page.manager.deleteButton.sendActions(for: .touchUpInside)
            try await activateAlert(page, title: "确认")
            await fulfillment(of: [receipt], timeout: 5)
            XCTAssertTrue(page.busy)
            let other = FilmCatDetailController(service: fixture.sources, installationId: "b", themeProvider: FilmSettingsTheme())
            navigation.pushViewController(other, animated: false)
            await eventually { other.canPresentPageAlert }
            fixture.catStorage.releaseReceipt()
            await eventually { !page.busy }
            await settle()
            XCTAssertTrue(navigation.topViewController === other)
            XCTAssertEqual(navigation.viewControllers.count, 2)
            XCTAssertNil(other.presentedViewController)
            XCTAssertNil(page.presentedViewController)
            XCTAssertEqual(fixture.catStorage.scriptReceipts, [["b"]])
            try await assertOnlyCatBRemainsOnDisk(fixture)
            navigation.popViewController(animated: false)
            await eventually { page.snapshot?.cats.map(\.installationId) == ["b"] }
            await eventually { page.canPresentPageAlert }
            XCTAssertTrue(navigation.topViewController === page)
            XCTAssertNil(page.presentedViewController)
            let calls = await fixture.runtime.calls
            XCTAssertEqual(calls, ["status"], "The native deletion must not request unavailable runtime state")
            XCTAssertEqual(fixture.catStorage.scriptReceipts, [["b"]])
            unhost(window)
            await fixture.sources.close(); await fixture.rules.close()
        }
    }

    private func assertOnlyCatBRemainsOnDisk(_ fixture: FilmSettingsFixture) async throws {
        let snapshot = await fixture.sources.snapshot
        XCTAssertEqual(snapshot.cats.map(\.installationId), ["b"])
        XCTAssertEqual(snapshot.videos.map(\.key.installationId), ["b"])
        let rebuilt = FilmSourceService(transport: .init(
            request: { _, _ in throw FilmSourceError(code: "OFFLINE") }),
                                       storeFactory: { fixture.sourceStore }, settingsDocument: fixture.catDocument,
                                       runtimeSourceRoot: fixture.runtimeSourceRoot)
        try await rebuilt.initialize()
        let persisted = await rebuilt.snapshot
        XCTAssertEqual(persisted.cats.map(\.installationId), ["b"])
        XCTAssertEqual(persisted.videos.map(\.key.installationId), ["b"])
        await rebuilt.close()
    }

    private func selectRow(_ page: FilmSettingsListController, _ id: String) throws {
        page.loadViewIfNeeded()
        let location = try indexPath(page, id)
        XCTAssertTrue(page.sections[location.section].rows[location.row].enabled)
        page.tableView(page.table, didSelectRowAt: location)
    }

    private func ruleButton(_ page: FilmRulePageViewController, _ id: String) throws -> UIButton {
        page.loadViewIfNeeded()
        let button = try XCTUnwrap(descendants(page.view).compactMap { $0 as? UIButton }.first {
            $0.accessibilityIdentifier == id
        })
        XCTAssertTrue(button.isEnabled)
        return button
    }

    private func ruleActions(_ page: FilmRulePageViewController) throws -> UIStackView {
        try XCTUnwrap(descendants(page.view).compactMap { $0 as? UIStackView }.first {
            $0.accessibilityIdentifier == "film.rules.actions"
        })
    }

    private func runRuleTest(_ page: FilmRulePageViewController) async throws {
        await eventually { page.canPresentPageAlert }
        try ruleButton(page, "film.rules.test").sendActions(for: .touchUpInside)
        try await confirmRuleTest(page)
    }

    private func confirmRuleTest(_ page: FilmRulePageViewController) async throws {
        let dialog = try XCTUnwrap(page.presentedViewController as? ManagementDialog)
        await eventually { !dialog.isBeingPresented && dialog.view.window != nil }
        let accepted = try XCTUnwrap(dialog.accepted)
        let receipt = expectation(description: "rule test dialog dismissed")
        dialog.accepted = { accepted(); receipt.fulfill() }
        let button = try XCTUnwrap(descendants(dialog.view).compactMap { $0 as? UIButton }.first {
            $0.accessibilityIdentifier == "management.dialog.confirm"
        })
        XCTAssertTrue(button.isEnabled)
        button.sendActions(for: .touchUpInside)
        await fulfillment(of: [receipt], timeout: 5)
        XCTAssertNil(dialog.presentingViewController)
        XCTAssertNil(dialog.viewIfLoaded?.window)
    }

    private func assertRuleUnderline(_ input: UIView, file: StaticString = #filePath, line: UInt = #line) {
        let appearance = (input as? SettingsTextView)?.inputAppearance
            ?? (input as? SettingsTextField)?.inputAppearance
        XCTAssertEqual(appearance, .underline, file: file, line: line)
        XCTAssertEqual(input.layer.borderWidth, 0, file: file, line: line)
        XCTAssertEqual(input.layer.cornerRadius, 0, file: file, line: line)
        XCTAssertEqual(input.backgroundColor, .clear, file: file, line: line)
        let underline = input.layer.sublayers?.first {
            !$0.isHidden && abs($0.frame.height - SettingsVisualMetrics.separatorHeight) < 0.01
                && abs($0.frame.width - input.bounds.width) < 0.01
                && abs($0.frame.maxY - input.bounds.maxY) < 0.01
        }
        XCTAssertNotNil(underline, file: file, line: line)
    }

    private func assertRuleHeading(_ editor: FilmRuleEditorView,
                                   file: StaticString = #filePath, line: UInt = #line) throws {
        let title = try XCTUnwrap(descendants(editor).compactMap { $0 as? UILabel }.first {
            $0.text?.hasPrefix("规则 ") == true
        }, file: file, line: line)
        let remove = try XCTUnwrap(editor.deleteButton.titleLabel, file: file, line: line)
        func baseline(_ label: UILabel) -> CGFloat {
            let text = label.textRect(forBounds: label.bounds, limitedToNumberOfLines: 0)
            return label.convert(CGPoint(x: 0, y: text.minY + label.font.ascender), to: editor).y
        }
        XCTAssertEqual(baseline(title), baseline(remove), accuracy: 1, file: file, line: line)
        XCTAssertEqual(editor.deleteButton.convert(editor.deleteButton.bounds, to: editor).minY,
                       editor.layoutMargins.top, accuracy: 1, file: file, line: line)
        for label in [title, editor.resultLabel, remove] {
            XCTAssertFalse(label.font.fontDescriptor.symbolicTraits.contains(.traitBold), file: file, line: line)
        }
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
    private func indexPath(_ page: FilmSettingsListController, _ id: String) throws -> IndexPath {
        try XCTUnwrap(page.sections.enumerated().compactMap { section, value -> IndexPath? in
            value.rows.firstIndex { $0.id == id }.map { IndexPath(row: $0, section: section) }
        }.first)
    }
    private func cell(_ page: FilmSettingsListController, _ id: String) throws -> UITableViewCell {
        let location = try indexPath(page, id)
        page.table.layoutIfNeeded()
        if let cell = page.table.cellForRow(at: location) { return cell }
        page.table.scrollToRow(at: location, at: .top, animated: false)
        page.table.layoutIfNeeded()
        return try XCTUnwrap(page.table.cellForRow(at: location))
    }
    private func descendants(_ view: UIView) -> [UIView] { [view] + view.subviews.flatMap(descendants) }
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
    let runtimeSourceRoot: URL
    let sourceStore: DocumentStore
    let ruleStorage: FilmSettingsTestStorage
    let catStorage: FilmSettingsTestStorage
    let rulesDocument: FilmSettingsDocument
    let catDocument: FilmSettingsDocument
    let rules: FilmRuleService
    let sources: FilmSourceService
    let runtime: FilmSettingsRPCFixture
    let ruleRPC: FilmSettingsRuleRPC
    init(client: RuntimeHTTPClient) async throws {
        base = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: base, withIntermediateDirectories: true)
        runtimeSourceRoot = base.appendingPathComponent("cat", isDirectory: true)
        sourceStore = try DocumentStore(baseURL: base, namespace: FilmSourceService.documentNamespace)
        ruleStorage = try FilmSettingsTestStorage(base: base, block: .rules)
        catStorage = try FilmSettingsTestStorage(base: base, block: .catSources)
        rulesDocument = ruleStorage.document; catDocument = catStorage.document
        try catStorage.store.write(RuntimeJSON.encode(.object([
            "version": .number(1), "port": .number(0), "providers": .object([:]), "mappings": .array([]),
            "scripts": .array(["a", "b"].map { id in .object([
                "installationId": .string(id), "name": .string("测试猫源 " + id),
                "sourceType": .string("network"), "sourceUrl": .string("https://example.invalid/\(id).js"),
                "localPath": .null, "localHash": .null, "enabled": .bool(true), "inputRevision": .number(1),
            ]) }),
        ])), key: FilmSettingsDocument.key)
        ruleRPC = FilmSettingsRuleRPC(client: client, document: rulesDocument)
        let rpc = ruleRPC
        rules = FilmRuleService(settingsDocument: rulesDocument, request: { try await rpc.call($0, $1) })
        runtime = FilmSettingsRPCFixture(document: catDocument)
        let runtime = runtime, store = sourceStore
        sources = FilmSourceService(transport: .init(
            request: { operation, params in try await runtime.call(operation, params) }),
                                    storeFactory: { store }, settingsDocument: catDocument,
                                    runtimeSourceRoot: runtimeSourceRoot)
    }
    func document(_ block: FilmSettingsBlock) throws -> FilmSettingsDocument {
        let store = try DocumentStore(baseURL: base, namespace: block.namespace)
        return .init(block: block, storage: .plain { store })
    }
    func removeFiles() async throws {
        await ruleRPC.releasePreviewReceipt()
        await runtime.releaseProbe()
        catStorage.releaseReceipt()
        ruleStorage.releaseReceipt()
        await rules.close(); await sources.close()
        try FileManager.default.removeItem(at: base)
    }
}

/// Synthetic settings use the actual DocumentStore CAS. A temporary namespace
/// symlink exercises its safe-path rejection without depending on chmod/root.
private final class FilmSettingsTestStorage: @unchecked Sendable {
    let store: DocumentStore
    private let block: FilmSettingsBlock
    private let directory: URL
    private let backup: URL
    private let lock = NSLock()
    private var changes: [String] = []
    private var catReceipts: [[String]] = []
    private var failAfterRemoval: String?
    private var fault: Error?
    private var receiptGate: (DispatchSemaphore, @Sendable () -> Void)?
    lazy var document = FilmSettingsDocument(block: block, storage: .plain { [store] in store },
        changed: { [weak self] in self?.changed($0) })
    var notifications: [String] { lock.lock(); defer { lock.unlock() }; return changes }
    var scriptReceipts: [[String]] { lock.lock(); defer { lock.unlock() }; return catReceipts }
    init(base: URL, block: FilmSettingsBlock) throws {
        self.block = block
        store = try DocumentStore(baseURL: base, namespace: block.namespace)
        directory = base.appendingPathComponent(block.namespace, isDirectory: true)
        backup = base.appendingPathComponent(block.namespace + "-blocked", isDirectory: true)
    }
    func failNextWrite(afterDeleting id: String) {
        lock.lock(); defer { lock.unlock() }; failAfterRemoval = id
    }
    func pauseNextCommit(_ entered: @escaping @Sendable () -> Void) {
        lock.lock(); defer { lock.unlock() }; receiptGate = (DispatchSemaphore(value: 0), entered)
    }
    func releaseReceipt() {
        lock.lock(); let gate = receiptGate; receiptGate = nil; lock.unlock()
        gate?.0.signal()
    }
    func checkFault() throws {
        lock.lock(); defer { lock.unlock() }; if let fault { throw fault }
    }
    func blockWrites() throws {
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        try FileManager.default.moveItem(at: directory, to: backup)
        try FileManager.default.createSymbolicLink(at: directory, withDestinationURL: backup)
    }
    func restoreWrites() throws {
        try FileManager.default.removeItem(at: directory)
        try FileManager.default.moveItem(at: backup, to: directory)
    }
    private func changed(_ block: String) {
        lock.lock()
        changes.append(block)
        if block == FilmSettingsBlock.catSources.rawValue {
            do {
                let value = try RuntimeJSON.decode(try XCTUnwrap(store.read(key: FilmSettingsDocument.key)))
                let ids = try value.filmRequiredObject().filmValue("scripts").filmArray!.map {
                    try $0.filmRequiredObject().filmValue("installationId").filmText!
                }
                catReceipts.append(ids)
                if let id = failAfterRemoval, !ids.contains(id) {
                    failAfterRemoval = nil
                    try blockWrites()
                }
            } catch { fault = error }
        }
        let gate = receiptGate
        lock.unlock()
        if let gate { gate.1(); gate.0.wait() }
    }
}

private actor FilmSettingsRuleRPC {
    let client: RuntimeHTTPClient
    let document: FilmSettingsDocument
    private(set) var requests: [FilmRuleRequest] = []
    private var failPreview = false
    private var holdPreviewReceipt = false
    private var receiptWaiter: CheckedContinuation<Void, Never>?
    private var receiptEnteredWaiters: [CheckedContinuation<Void, Never>] = []
    init(client: RuntimeHTTPClient, document: FilmSettingsDocument) {
        self.client = client; self.document = document
    }
    func failNextPreview() { failPreview = true }
    func holdNextPreviewReceipt() { holdPreviewReceipt = true }
    func waitForPreviewReceipt() async {
        if receiptWaiter != nil { return }
        await withCheckedContinuation { receiptEnteredWaiters.append($0) }
    }
    func releasePreviewReceipt() { receiptWaiter?.resume(); receiptWaiter = nil }
    func call(_ operation: String, _ input: RuntimeValue) async throws -> RuntimeValue {
        guard ["rules.preview", "rules.group.preview"].contains(operation) else {
            XCTFail("Native settings must not call \(operation)"); throw RuntimeHTTPError.closed
        }
        // This fixture's private native document is intentionally not the host
        // App's settings. Supply that snapshot to the public full-preview route
        // so associated saved groups are executed by real Node without editing
        // or clearing any host settings.
        var fields = try input.filmRequiredObject()
        var rules = try RuntimeJSON.decode(JSONEncoder().encode(FilmRuleSettings.read(document.read()))).filmRequiredObject()
        if operation == "rules.group.preview" {
            let group = try XCTUnwrap(fields["group"]?.filmText.flatMap(FilmRuleGroup.init(rawValue:)))
            rules[FilmRuleSettings.key(group)] = try fields.filmValue("rules")
            fields = ["request": try fields.filmValue("request"), "rules": .object(rules)]
        } else if fields["rules"] == nil { fields["rules"] = .object(rules) }
        requests.append(try JSONDecoder().decode(FilmRuleRequest.self,
            from: RuntimeJSON.encode(try fields.filmValue("request"))))
        if failPreview { failPreview = false; throw URLError(.cannotConnectToHost) }
        let response = try await client.call(module: .film, operation: "rules.preview", parameters: .object(fields))
        if holdPreviewReceipt {
            holdPreviewReceipt = false
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
    let document: FilmSettingsDocument
    var calls: [String] = []
    private var updated = Set<String>()
    private var failedStarts = 0
    private var statusUnavailable = false
    private var restartFailureID: String?
    private var restartFailures = 0
    private(set) var restarts: [String] = []
    private(set) var updates: [String] = []
    init(document: FilmSettingsDocument) { self.document = document }
    func failStatus() { statusUnavailable = true }
    func failRestarts(_ id: String, count: Int) { restartFailureID = id; restartFailures = count }
    func setLocal(_ ids: Set<String>) throws {
        _ = try document.update { current in
            var root = try XCTUnwrap(current).filmRequiredObject()
            root["scripts"] = .array(try root.filmValue("scripts").filmArray!.map { value in
                var fields = try value.filmRequiredObject()
                let id = try XCTUnwrap(fields["installationId"]?.filmText)
                fields["sourceType"] = .string(ids.contains(id) ? "local" : "network")
                fields["sourceUrl"] = ids.contains(id) ? .null : .string("https://example.invalid/\(id).js")
                fields["localHash"] = ids.contains(id) ? .string(String(repeating: "0", count: 64)) : .null
                return .object(fields)
            })
            return .object(root)
        }
    }
    func failStarts(_ count: Int) { failedStarts = count }
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
    func call(_ method: String, _ params: RuntimeValue) async throws -> RuntimeValue {
        calls.append(method)
        let fields: RuntimeObject
        if case .object(let values) = params { fields = values } else { fields = [:] }
        let id: String
        if case .string(let value)? = fields["installationId"] { id = value } else { id = "a" }
        let value: RuntimeValue
        switch method {
        case "status":
            if statusUnavailable { throw FilmSourceError(code: "STATUS_FAILED") }
            value = .object(["instances": .array(try inputs().map { try cat($0) })])
        case "start":
            if failedStarts > 0 { failedStarts -= 1; throw FilmSourceError(code: "OFFLINE") }
            value = .object([:])
        case "update":
            updates.append(id)
            updated.insert(id); value = try cat(id)
        case "restart":
            restarts.append(id)
            if restartFailureID == id, restartFailures > 0 {
                restartFailures -= 1
                throw FilmSourceError(code: "RESTART_FAILED")
            }
            value = try cat(id)
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
    private func inputs() throws -> [RuntimeObject] {
        try XCTUnwrap(document.read()).filmRequiredObject().filmValue("scripts").filmArray!.map {
            try $0.filmRequiredObject()
        }
    }
    private func cat(_ id: String) throws -> RuntimeValue {
        try cat(XCTUnwrap(inputs().first { $0["installationId"] == .string(id) }))
    }
    private func cat(_ input: RuntimeObject) throws -> RuntimeValue {
        let id = try XCTUnwrap(input["installationId"]?.filmText)
        return .object(["installationId": .string(id), "name": try input.filmValue("name"),
                 "sourceType": try input.filmValue("sourceType"), "sourceUrl": try input.filmValue("sourceUrl"),
                 "enabled": try input.filmValue("enabled"), "generation": .number(1),
                 "inputRevision": try input.filmValue("inputRevision"),
                 "state": .string(input["enabled"] == .bool(false) ? "disabled" : "ready"),
                 "importedAt": .number(1_700_000_000_000), "updatedAt": .null, "updateState": .string(updated.contains(id) ? "updated" : "idle"),
                 "updateError": .null, "sourceHash": .string("fixture"), "error": .null,
                 "sites": .array([.object(["key": .string("site"), "name": .string("测试视频源 " + id),
                    "api": .string("/api/" + id), "actions": .array([.string("home"), .string("category")]), "methods": .object([:])])])])
    }
}
#endif
