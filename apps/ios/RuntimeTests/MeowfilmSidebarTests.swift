import XCTest
import UIKit
import MeowRuntime
import MeowStorage
import MeowUI
import MeowNovelFeature
@testable import MeowFilmFeature
@testable import Meowfilm

/// Real App-hosted UIKit; source directories and responses are local fixtures.
@MainActor
final class MeowfilmSidebarTests: XCTestCase {
    private var hostedWindows: [(window: UIWindow, previous: UIWindow?)] = []
    private var filmFixtures: [SidebarFilmFixture] = []
    private var novelFixtures: [SidebarNovelFixture] = []

    override func setUp() async throws {
        try await super.setUp()
        _ = try await hostResources()
        _ = try await foregroundScene()
    }

    override func tearDown() async throws {
        while let entry = hostedWindows.last { unhost(entry.window) }
        for fixture in filmFixtures { try await fixture.close() }
        filmFixtures.removeAll()
        for fixture in novelFixtures { try await fixture.close() }
        novelFixtures.removeAll()
        try await super.tearDown()
    }

    func testMusicLayoutAlignsRowsAndKeepsSelectedStyleAndIndependentSettings() async throws {
        let sidebar = try musicSidebar(selected: .channels)
        let window = try await host(sidebar)
        defer { unhost(window) }
        let parts = try parts(in: sidebar)
        assertLayout(parts, in: sidebar)

        XCTAssertNotNil(parts.icon.image, "The App icon must be bundled in the real host")
        XCTAssertEqual(parts.upper.arrangedSubviews.map(\.accessibilityIdentifier),
                       ["app.sidebar.home", "app.sidebar.channels"])
        let home = try item("home", in: sidebar, as: UIButton.self)
        let channels = try item("channels", in: sidebar, as: UIButton.self)
        XCTAssertEqual(home.configuration?.title, "首页")
        XCTAssertEqual(channels.configuration?.title, "频道")
        XCTAssertFalse(home.isSelected)
        XCTAssertTrue(channels.isSelected)
        XCTAssertLessThanOrEqual(frame(home, in: sidebar).maxY, frame(channels, in: sidebar).minY)
        XCTAssertEqual(frame(channels, in: sidebar).minY - frame(home, in: sidebar).maxY, 4, accuracy: 1)
        let panel = try XCTUnwrap(parts.scroll.superview as? WindowGradientView)
        XCTAssertEqual(home.configuration?.background.backgroundColor, .clear)
        XCTAssertEqual(channels.configuration?.background.backgroundColor, UIColor(panel.theme.buttonSelected))
        XCTAssertEqual(channels.configuration?.cornerStyle, .fixed)
        XCTAssertEqual(channels.configuration?.background.cornerRadius, 28)
        XCTAssertEqual(channels.backgroundColor ?? .clear, .clear, "The selected fill must not be duplicated behind the configured background")
        XCTAssertEqual(home.configuration?.baseForegroundColor, UIColor(panel.theme.ink))
        XCTAssertEqual(channels.configuration?.baseForegroundColor, UIColor(panel.theme.accent))
        XCTAssertEqual(parts.settings.configuration?.title, "设置")
        XCTAssertEqual(parts.settings.contentHorizontalAlignment, .leading)
        let chevron = try item("settings.chevron", in: parts.settings, as: UIImageView.self)
        XCTAssertNotNil(chevron.image)
        XCTAssertEqual(frame(chevron, in: sidebar).maxX, frame(parts.settings, in: sidebar).maxX - 14, accuracy: 1)
        try assertSettingsReachable(parts, in: sidebar)
        attach(sidebar, "sidebar-music-ordinary")
    }

    func testAddingIntrinsicMenuRowGrowsContentDownwardWithoutMovingBottomSettings() async throws {
        let sidebar = try musicSidebar()
        let window = try await host(sidebar)
        defer { unhost(window) }
        let parts = try parts(in: sidebar)
        let beforeUpper = frame(parts.upper, in: sidebar)
        let beforeRows = parts.upper.arrangedSubviews.map { frame($0, in: sidebar) }
        let beforeCard = frame(parts.card, in: sidebar)
        let beforeSettings = frame(parts.settings, in: sidebar)

        // Only this fixture adds a row; its height comes from UIKit content.
        let extra = UIButton(type: .system)
        extra.setTitle("布局测试菜单项", for: .normal)
        extra.titleLabel?.font = .preferredFont(forTextStyle: .body)
        extra.accessibilityIdentifier = "fixture.sidebar.extraRow"
        XCTAssertGreaterThan(extra.intrinsicContentSize.height, 0)
        parts.upper.addArrangedSubview(extra)
        sidebar.setNeedsLayout()
        sidebar.layoutIfNeeded()

        assertLayout(parts, in: sidebar)
        let afterUpper = frame(parts.upper, in: sidebar)
        XCTAssertGreaterThan(afterUpper.height, beforeUpper.height)
        XCTAssertEqual(afterUpper.minY, beforeUpper.minY, accuracy: 1)
        XCTAssertGreaterThan(afterUpper.maxY, beforeUpper.maxY)
        XCTAssertGreaterThanOrEqual(extra.bounds.height, extra.intrinsicContentSize.height - 1)
        for (row, before) in zip(parts.upper.arrangedSubviews, beforeRows) {
            assertEqualFrame(frame(row, in: sidebar), before)
        }
        let afterCard = frame(parts.card, in: sidebar)
        XCTAssertEqual(afterCard.minY, beforeCard.minY, accuracy: 1)
        XCTAssertEqual(afterCard.width, beforeCard.width, accuracy: 1)
        XCTAssertEqual(afterCard.height - beforeCard.height, afterUpper.height - beforeUpper.height, accuracy: 1)
        assertEqualFrame(frame(parts.settings, in: sidebar), beforeSettings)
        attach(sidebar, "sidebar-music-extra-menu-row")
    }

    func testNavigationSettingsAndDismissDispatchOnlyTheirOwnCallbacks() async throws {
        let sidebars = [
            try musicSidebar(selected: .channels),
            try sidebar(.film(settings: nil)),
        ]
        for (index, sidebar) in sidebars.enumerated() {
            let window = try await host(sidebar)
            defer { unhost(window) }
            var pages: [MeowfilmPrimaryPage] = []
            var settingsCount = 0
            var dismissCount = 0
            sidebar.onSelectPage = { pages.append($0) }
            sidebar.onSettings = { settingsCount += 1 }
            sidebar.onDismiss = { dismissCount += 1 }
            sidebar.onClosed = { XCTFail("A button action must not report a completed closing gesture") }
            try item("home", in: sidebar, as: UIButton.self).sendActions(for: .touchUpInside)
            XCTAssertEqual(pages, [index == 0 ? .recommend : .film])
            if index == 0 {
                try item("channels", in: sidebar, as: UIButton.self).sendActions(for: .touchUpInside)
                XCTAssertEqual(pages, [.recommend, .channels])
            }
            XCTAssertEqual(settingsCount, 0)
            XCTAssertEqual(dismissCount, 0)
            let expectedPages = pages
            try item("settings", in: sidebar, as: UIButton.self).sendActions(for: .touchUpInside)
            XCTAssertEqual(settingsCount, 1)
            XCTAssertEqual(pages, expectedPages)
            let dismiss = try XCTUnwrap(descendants(sidebar).compactMap { $0 as? UIButton }
                .first { $0.accessibilityLabel == "关闭侧边栏" })
            dismiss.sendActions(for: .touchUpInside)
            XCTAssertEqual(dismissCount, 1)
            XCTAssertEqual(settingsCount, 1)
            XCTAssertEqual(pages, expectedPages)
        }
    }

    func testFilmUsesSharedLayoutWithHomeBeforeSavedVideoRows() async throws {
        let fixture = try await filmFixture(videos: [video(key: .init(installationId: "local", siteKey: "one"),
                                                         name: "示例视频源", order: 0)])
        let music = try musicSidebar()
        let musicWindow = try await host(music)
        let musicParts = try parts(in: music)
        let reference = [musicParts.icon, musicParts.card, musicParts.settings].map { frame($0, in: music) }
        unhost(musicWindow)

        let film = try sidebar(.film(settings: fixture.model))
        let window = try await host(film)
        defer { unhost(window) }
        let parts = try parts(in: film)
        assertLayout(parts, in: film)
        for (view, expected) in zip([parts.icon, parts.settings], [reference[0], reference[2]]) {
            assertEqualFrame(frame(view, in: film), expected)
        }
        XCTAssertEqual(frame(parts.card, in: film).minX, reference[1].minX, accuracy: 1)
        XCTAssertEqual(frame(parts.card, in: film).minY, reference[1].minY, accuracy: 1)
        XCTAssertEqual(frame(parts.card, in: film).width, reference[1].width, accuracy: 1)
        XCTAssertEqual(parts.upper.arrangedSubviews.first?.accessibilityIdentifier, "app.sidebar.home")
        XCTAssertNil(identified("app.sidebar.channels", in: film))
        let buttons = descendants(parts.card).compactMap { $0 as? UIButton }
        XCTAssertEqual(buttons.map(\.accessibilityIdentifier), ["app.sidebar.home"])
        XCTAssertEqual(try videoRows(in: film).map(\.accessibilityLabel), ["示例视频源"])
        let videoRow = try XCTUnwrap(try videoRows(in: film).first)
        XCTAssertTrue(videoRow.isDescendant(of: parts.upper))
        XCTAssertLessThanOrEqual(frame(try item("home", in: film, as: UIButton.self), in: film).maxY,
                                 frame(videoRow, in: film).minY)
        try assertSettingsReachable(parts, in: film)
        let calls = await fixture.transport.calls
        XCTAssertTrue(calls.isEmpty, "Rendering the saved directory must not start or probe video sources")
        attach(film, "sidebar-film-shared-layout")
    }

    func testVideoRowsPreserveExactIdentityOrderAndSnapshotChangesWithoutNavigationOrRequests() async throws {
        let first = FilmSourceKey(installationId: "cat.one", siteKey: "same")
        let second = FilmSourceKey(installationId: "cat.two", siteKey: "same")
        let composed = FilmSourceKey(installationId: "cat.one", siteKey: "\u{00e9}")
        let decomposed = FilmSourceKey(installationId: "cat.one", siteKey: "e\u{0301}")
        let disabled = FilmSourceKey(installationId: "cat.one", siteKey: "disabled")
        let hidden = FilmSourceKey(installationId: "cat.one", siteKey: "hidden")
        let failed = FilmSourceDetection(result: "invalid", checkedAt: 1, playbackResolved: false,
                                         steps: [.init("home", "failed", "fixture failure")])
        let catalog = [
            video(key: second, name: "同名视频源", order: 30, options: .init(searchEnabled: false)),
            video(key: disabled, name: "停用源", order: 20, options: .init(enabled: false)),
            video(key: decomposed, name: "分解形式", order: 50),
            video(key: hidden, name: "隐藏源", order: 60, options: .init(homeVisible: false)),
            video(key: first, name: "同名视频源", order: 10, detection: failed),
            video(key: composed, name: "组合形式", order: 40),
        ]
        let fixture = try await filmFixture(videos: catalog)
        let sidebar = try sidebar(.film(settings: fixture.model))
        let window = try await host(sidebar)
        defer { unhost(window) }
        var navigations: [MeowfilmPrimaryPage] = []
        var settingsCount = 0
        sidebar.onSelectPage = { navigations.append($0) }
        sidebar.onSettings = { settingsCount += 1 }
        var rows = try videoRows(in: sidebar)
        XCTAssertEqual(try rows.map(rowKey), [first, second, composed, decomposed])
        XCTAssertEqual(rows.map(\.accessibilityLabel), ["同名视频源", "同名视频源", "组合形式", "分解形式"])
        let identifiers = try rows.map { try XCTUnwrap($0.accessibilityIdentifier) }
        XCTAssertEqual(Set(identifiers).count, 4)
        let initialIDs = Dictionary(uniqueKeysWithValues: try rows.map { (try rowKey($0), try XCTUnwrap($0.accessibilityIdentifier)) })
        for row in rows { assertDisplayOnly(row) }
        assertLayout(try parts(in: sidebar), in: sidebar)
        attach(sidebar, "sidebar-film-video-filtering-exact-identities")

        try await fixture.sources.saveVideoOptions(second, options: .init(searchEnabled: false), order: -1)
        await eventually("directory reordering reaches the visible sidebar") {
            (try? self.videoRows(in: sidebar).map(self.rowKey)) == [second, first, composed, decomposed]
        }
        rows = try videoRows(in: sidebar)
        for row in rows { XCTAssertEqual(row.accessibilityIdentifier, initialIDs[try rowKey(row)]) }
        try await fixture.sources.saveVideoOptions(first, options: .init(homeVisible: false))
        await eventually("home visibility change removes its row") {
            (try? self.videoRows(in: sidebar).map(self.rowKey)) == [second, composed, decomposed]
        }
        try await fixture.sources.saveVideoOptions(hidden, options: .init())
        await eventually("newly visible directory entry appears") {
            (try? self.videoRows(in: sidebar).map(self.rowKey)) == [second, composed, decomposed, hidden]
        }
        try await fixture.sources.deleteVideo(composed)
        await eventually("deleted exact key disappears without removing canonically similar key") {
            (try? self.videoRows(in: sidebar).map(self.rowKey)) == [second, decomposed, hidden]
        }
        let callsBeforeRefresh = await fixture.transport.calls
        XCTAssertTrue(callsBeforeRefresh.isEmpty)

        var renamedCatalog = catalog
        renamedCatalog[0].name = "源名称已更新"
        await fixture.transport.allowNextStatus(SidebarFilmFixture.cats(for: renamedCatalog))
        try await fixture.sources.refresh()
        await eventually("directory refresh updates a name in place") {
            (try? self.videoRows(in: sidebar).first?.accessibilityLabel) == "源名称已更新"
        }
        rows = try videoRows(in: sidebar)
        XCTAssertEqual(try rows.map(rowKey), [second, decomposed, hidden])
        XCTAssertEqual(rows.first?.accessibilityIdentifier, initialIDs[second])
        for row in rows { assertDisplayOnly(row) }
        assertLayout(try parts(in: sidebar), in: sidebar)
        await settle()
        XCTAssertTrue(navigations.isEmpty)
        XCTAssertEqual(settingsCount, 0)
        let callsAfterRefresh = await fixture.transport.calls
        XCTAssertEqual(callsAfterRefresh, ["status"], "Only the test's explicit directory refresh is allowed")
        attach(sidebar, "sidebar-film-video-snapshot-updated")

        for key in [second, decomposed, hidden] {
            try await fixture.sources.saveVideoOptions(key, options: .init(homeVisible: false))
        }
        await eventually("empty directory removes all display rows") {
            (try? self.videoRows(in: sidebar).isEmpty) == true
        }
        let emptyParts = try parts(in: sidebar)
        XCTAssertEqual(emptyParts.upper.arrangedSubviews.filter { !$0.isHidden }.map(\.accessibilityIdentifier),
                       ["app.sidebar.home"])
        assertLayout(emptyParts, in: sidebar)
        try assertSettingsReachable(emptyParts, in: sidebar)
        XCTAssertTrue(navigations.isEmpty)
        XCTAssertEqual(settingsCount, 0)
        let finalCalls = await fixture.transport.calls
        XCTAssertEqual(finalCalls, ["status"])
        attach(sidebar, "sidebar-film-empty-directory")
    }

    func testNovelShowsAllAddedSourcesAndUpdatesAvailabilityWithoutNavigationOrRequests() async throws {
        let catalog = [
            try bookSource(id: "book.two", name: "同名书源", order: 30, actions: ["search"]),
            try bookSource(id: "disabled", name: "停用书源", order: 20, enabled: false),
            try bookSource(id: "book.one", name: "同名书源", order: 10, errorCode: "RESTORE_FAILED"),
            try bookSource(id: "no-explore", name: "禁止发现书源", order: 40, enabledExplore: false),
        ]
        let fixture = try await novelFixture(sources: catalog)
        var homeCount = 0
        var dismissCount = 0
        var settingsCount = 0
        var pages: [MeowfilmPrimaryPage] = []
        let sidebar = try sidebar(.novel(discovery: fixture.discovery, home: { homeCount += 1 }))
        sidebar.onDismiss = { dismissCount += 1 }
        sidebar.onSettings = { settingsCount += 1 }
        sidebar.onSelectPage = { pages.append($0) }
        let window = try await host(sidebar)
        defer { unhost(window) }
        let parts = try parts(in: sidebar)
        assertLayout(parts, in: sidebar)
        XCTAssertEqual(parts.upper.arrangedSubviews.first?.accessibilityIdentifier, "app.sidebar.home")
        XCTAssertNil(identified("app.sidebar.shelf", in: sidebar))
        XCTAssertNil(identified("app.sidebar.musicSource", in: sidebar))
        XCTAssertNil(identified("app.sidebar.filmSource", in: sidebar))
        var rows = try bookRows(in: sidebar)
        XCTAssertEqual(try rows.map(bookRowID), ["book.one", "disabled", "book.two", "no-explore"])
        XCTAssertEqual(try rows.map(bookRowID), fixture.feature.sources.map(\.id))
        XCTAssertEqual(rows.map(\.accessibilityLabel), ["同名书源", "停用书源", "同名书源", "禁止发现书源"])
        XCTAssertEqual(rows.map(\.isEnabled), [true, false, false, false])
        let identifiers = try rows.map { try XCTUnwrap($0.accessibilityIdentifier) }
        XCTAssertEqual(Set(identifiers).count, catalog.count)
        let initialRows = Dictionary(uniqueKeysWithValues: try rows.map { (try bookRowID($0), $0) })
        for row in rows {
            XCTAssertTrue(row.accessibilityTraits.contains(.button))
            XCTAssertTrue(row.isDescendant(of: parts.upper))
        }
        let initialCalls = await fixture.runtime.calls
        XCTAssertEqual(initialCalls, ["library.list"], "Only the fixture's explicit snapshot load is allowed")
        attach(sidebar, "sidebar-novel-all-added-sources")

        var updated = catalog.filter { $0.id != "disabled" }
        updated[0].name = "已更新书源"
        updated[0].order = -1
        updated[0].actions = ["search", "explore"]
        updated[1].enabled = false
        updated[2].enabledExplore = true
        updated.append(try bookSource(id: "new-source", name: "新增书源", order: 50, actions: ["search"]))
        await fixture.runtime.replaceSources(updated)
        try await fixture.feature.refreshSources()
        await eventually("novel snapshot changes reach the open sidebar") {
            (try? self.bookRows(in: sidebar).map(self.bookRowID)) == ["book.two", "book.one", "no-explore", "new-source"] &&
                (try? self.bookRows(in: sidebar).first?.accessibilityLabel) == "已更新书源" &&
                (try? self.bookRows(in: sidebar).map(\.isEnabled)) == [true, false, true, false]
        }
        rows = try bookRows(in: sidebar)
        XCTAssertEqual(try rows.map(bookRowID), fixture.feature.sources.map(\.id))
        for row in rows {
            XCTAssertTrue(row.accessibilityTraits.contains(.button))
            if let previous = initialRows[try bookRowID(row)] {
                XCTAssertTrue(row === previous, "Snapshot changes retain the row for the same installation")
            }
        }
        assertLayout(parts, in: sidebar)
        XCTAssertEqual(homeCount, 0)
        XCTAssertEqual(dismissCount, 0)
        XCTAssertEqual(settingsCount, 0)
        XCTAssertTrue(pages.isEmpty)
        let updatedCalls = await fixture.runtime.calls
        XCTAssertEqual(updatedCalls, ["library.list", "library.list"])
        attach(sidebar, "sidebar-novel-snapshot-updated")

        await fixture.runtime.replaceSources([])
        try await fixture.feature.refreshSources()
        await eventually("empty novel snapshot leaves the home action") {
            (try? self.bookRows(in: sidebar).isEmpty) == true
        }
        XCTAssertEqual(parts.upper.arrangedSubviews.filter { !$0.isHidden }.map(\.accessibilityIdentifier),
                       ["app.sidebar.home"])
        assertLayout(parts, in: sidebar)
        try assertSettingsReachable(parts, in: sidebar)
        try item("home", in: sidebar, as: UIButton.self).sendActions(for: .touchUpInside)
        XCTAssertEqual(homeCount, 1)
        XCTAssertEqual(dismissCount, 1)
        XCTAssertEqual(settingsCount, 0)
        XCTAssertTrue(pages.isEmpty)
        let finalCalls = await fixture.runtime.calls
        XCTAssertEqual(finalCalls, ["library.list", "library.list", "library.list"],
                       "Rendering source choices and the home callback must not invoke discovery")
        attach(sidebar, "sidebar-novel-empty-directory")
    }

    func testNovelSourceButtonsSelectDismissObserveSelectionAndStopUpdatingWhenClosed() async throws {
        let catalog = [
            try bookSource(id: "first", name: "第一书源", order: 0),
            try bookSource(id: "disabled", name: "停用书源", order: 1, enabled: false),
            try bookSource(id: "no-explore", name: "禁止发现书源", order: 2, enabledExplore: false),
            try bookSource(id: "search-only", name: "仅搜索书源", order: 3, actions: ["search"]),
            try bookSource(id: "second", name: "第二书源", order: 4),
        ]
        let fixture = try await novelFixture(sources: catalog)
        fixture.discovery.selectSource(id: "first")
        var homeCount = 0
        var dismissCount = 0
        let sidebar = try sidebar(.novel(discovery: fixture.discovery, home: { homeCount += 1 }))
        sidebar.onDismiss = { dismissCount += 1 }
        sidebar.onSettings = { XCTFail("A source choice must not open settings") }
        sidebar.onSelectPage = { _ in XCTFail("A source choice must stay on the novel page") }
        let window = try await host(sidebar)
        defer { unhost(window) }
        let rows = try bookRows(in: sidebar)
        let first = try XCTUnwrap(rows.first)
        let second = try XCTUnwrap(rows.last)
        XCTAssertEqual(rows.map(\.isEnabled), [true, false, false, false, true])
        XCTAssertEqual(rows.filter(\.isSelected), [first])
        XCTAssertEqual(fixture.discovery.availableSources.map(\.id), ["first", "second"])

        second.sendActions(for: .touchUpInside)
        await eventually("source button selects the shared discovery session") {
            fixture.discovery.selectedSource?.id == "second" && second.isSelected && !first.isSelected
        }
        XCTAssertEqual(dismissCount, 1)
        XCTAssertEqual(homeCount, 0)
        let panel = try XCTUnwrap(try parts(in: sidebar).scroll.superview as? WindowGradientView)
        XCTAssertEqual(second.configuration?.background.backgroundColor, UIColor(panel.theme.buttonSelected))
        XCTAssertEqual(second.configuration?.baseForegroundColor, UIColor(panel.theme.accent))
        XCTAssertEqual(first.configuration?.background.backgroundColor, .clear)
        assertLayout(try parts(in: sidebar), in: sidebar)

        fixture.discovery.selectSource(id: "first")
        await eventually("selection changes outside the sidebar update its buttons") {
            first.isSelected && !second.isSelected
        }
        XCTAssertEqual(dismissCount, 1, "Observing a selection must not dispatch a dismissal")
        try item("home", in: sidebar, as: UIButton.self).sendActions(for: .touchUpInside)
        XCTAssertEqual(homeCount, 1)
        XCTAssertEqual(dismissCount, 2)
        XCTAssertEqual(fixture.discovery.selectedSource?.id, "first", "Home must preserve the discovery selection")

        let closed = expectation(description: "sidebar finished closing")
        sidebar.hide { closed.fulfill() }
        await fulfillment(of: [closed], timeout: 3)
        fixture.discovery.selectSource(id: "second")
        var updated = catalog
        updated[4].name = "第二书源已更新"
        updated.append(try bookSource(id: "third", name: "第三书源", order: 5))
        await fixture.runtime.replaceSources(updated)
        try await fixture.feature.refreshSources()

        let reopened = try self.sidebar(.novel(discovery: fixture.discovery, home: {}))
        let reopenedWindow = try await host(reopened)
        defer { unhost(reopenedWindow) }
        await eventually("reopening observes current sources and selection") {
            (try? self.bookRows(in: reopened).map(self.bookRowID)) == updated.map(\.id) &&
                (try? self.bookRows(in: reopened).filter(\.isSelected).map(self.bookRowID)) == ["second"]
        }
        let reopenedSecond = try XCTUnwrap(try bookRows(in: reopened).first { try bookRowID($0) == "second" })
        XCTAssertEqual(reopenedSecond.accessibilityLabel, "第二书源已更新")
        XCTAssertEqual(try bookRows(in: sidebar).map(bookRowID), catalog.map(\.id))
        XCTAssertTrue(first.isSelected, "The closed sidebar must unsubscribe from selection changes")
        XCTAssertFalse(second.isSelected)
        XCTAssertEqual(second.accessibilityLabel, "第二书源", "The closed sidebar must unsubscribe from source changes")
        XCTAssertEqual(dismissCount, 2)
        XCTAssertEqual(homeCount, 1)
        let calls = await fixture.runtime.calls
        XCTAssertEqual(calls, ["library.list", "library.list"],
                       "The sidebar selects the session; it must not activate a hidden discovery page")
    }

    func testCompactLandscapeAndAccessibilityTypeKeepAllDomainsScrollableAndSettingsFixed() async throws {
        let catalog = (0..<12).map {
            video(key: .init(installationId: "layout", siteKey: "site-\($0)"),
                  name: "视频源 \($0) · 用于验证长名称完整换行显示", order: $0)
        }
        let fixture = try await filmFixture(videos: catalog)
        let novel = try await novelFixture(sources: (0..<12).map {
            try bookSource(id: "layout-book-\($0)", name: "书源 \($0) · 用于验证长名称完整换行显示",
                           order: Double($0), enabled: $0 % 2 == 0, actions: ["search"])
        })
        let cases: [(String, CGSize, UIContentSizeCategory)] = [
            ("compact", CGSize(width: 320, height: 568), .large),
            ("landscape", CGSize(width: 844, height: 390), .large),
            ("compact-AXXXL", CGSize(width: 320, height: 568), .accessibilityExtraExtraExtraLarge),
            ("landscape-AXXXL", CGSize(width: 844, height: 390), .accessibilityExtraExtraExtraLarge),
        ]
        for domain in ["music", "film", "novel"] {
            var ordinaryFontSize: CGFloat?
            for (name, size, category) in cases {
                let sidebar: MeowfilmSidebarView
                switch domain {
                case "music": sidebar = try musicSidebar()
                case "film": sidebar = try self.sidebar(.film(settings: fixture.model))
                default: sidebar = try self.sidebar(.novel(discovery: novel.discovery, home: {}))
                }
                let window = try await host(sidebar, size: size, category: category)
                let parts = try parts(in: sidebar)
                assertLayout(parts, in: sidebar)
                XCTAssertEqual(sidebar.bounds.size, size)
                XCTAssertEqual(sidebar.traitCollection.preferredContentSizeCategory, category)
                let homeButton = try item("home", in: sidebar, as: UIButton.self)
                let homeLabel = try XCTUnwrap(homeButton.titleLabel)
                let fontSize = homeLabel.font.pointSize
                if category == .large { ordinaryFontSize = fontSize }
                else { XCTAssertGreaterThan(fontSize, try XCTUnwrap(ordinaryFontSize), "The large-type fixture must actually enlarge text") }
                let titleSize = homeLabel.sizeThatFits(CGSize(width: homeLabel.bounds.width, height: .greatestFiniteMagnitude))
                XCTAssertGreaterThanOrEqual(homeLabel.bounds.height + 1, titleSize.height, "Navigation titles must fit at the requested text size")
                for button in descendants(parts.card).compactMap({ $0 as? UIButton }) + [parts.settings] {
                    for label in descendants(button).compactMap({ $0 as? UILabel }) where !label.isHidden {
                        let fitting = label.sizeThatFits(CGSize(width: label.bounds.width, height: .greatestFiniteMagnitude))
                        XCTAssertGreaterThanOrEqual(label.bounds.height + 1, fitting.height,
                                                    "Menu titles must fit at the requested text size")
                    }
                }
                for row in try videoRows(in: sidebar) {
                    let label = try XCTUnwrap(row.arrangedSubviews.last as? UILabel)
                    let fitting = label.sizeThatFits(CGSize(width: label.bounds.width, height: .greatestFiniteMagnitude))
                    XCTAssertGreaterThanOrEqual(label.bounds.height + 1, fitting.height, "Source names must not be vertically clipped")
                    assertDisplayOnly(row)
                }
                for row in try bookRows(in: sidebar) {
                    let label = try XCTUnwrap(row.titleLabel)
                    let fitting = label.sizeThatFits(CGSize(width: label.bounds.width, height: .greatestFiniteMagnitude))
                    XCTAssertGreaterThanOrEqual(label.bounds.height + 1, fitting.height, "Source names must not be vertically clipped")
                    XCTAssertTrue(row.accessibilityTraits.contains(.button))
                    XCTAssertFalse(row.isEnabled, "These layout fixtures only support search")
                }
                attach(sidebar, "sidebar-\(domain)-\(name)-top")
                if domain != "music" {
                    XCTAssertGreaterThan(parts.scroll.contentSize.height, parts.scroll.bounds.height,
                                         "Long source lists must be scrollable instead of compressing the rows")
                }
                try assertSettingsReachable(parts, in: sidebar)
                attach(sidebar, "sidebar-\(domain)-\(name)-settings")
                unhost(window)
            }
        }
        let calls = await fixture.transport.calls
        XCTAssertTrue(calls.isEmpty)
        let novelCalls = await novel.runtime.calls
        XCTAssertEqual(novelCalls, ["library.list"])
    }

    private func assembly() throws -> MeowfilmAssembly {
        let app = try XCTUnwrap(UIApplication.shared.delegate as? MeowfilmAppDelegate)
        return try XCTUnwrap(app.readyAssembly())
    }

    private func sidebar(_ content: MeowfilmSidebarContent) throws -> MeowfilmSidebarView {
        MeowfilmSidebarView(content: content, themeProvider: try assembly().appearance)
    }

    private func musicSidebar(selected: MeowfilmPrimaryPage = .recommend) throws -> MeowfilmSidebarView {
        try sidebar(.music(selectedPage: selected))
    }

    private func filmFixture(videos: [FilmVideoSource]) async throws -> SidebarFilmFixture {
        let fixture = try SidebarFilmFixture(videos: videos)
        filmFixtures.append(fixture)
        fixture.model.start()
        await eventually("local film settings snapshot loaded") { fixture.model.snapshot != nil && fixture.model.rules.draft != nil }
        XCTAssertNil(fixture.model.sourcesError)
        XCTAssertNil(fixture.model.rulesError)
        return fixture
    }

    private func novelFixture(sources: [NovelSource]) async throws -> SidebarNovelFixture {
        let fixture = try SidebarNovelFixture(sources: sources)
        novelFixtures.append(fixture)
        try await fixture.feature.refreshSources()
        return fixture
    }

    private func bookSource(id: String, name: String, order: Double, enabled: Bool = true,
                            enabledExplore: Bool = true, actions: [String] = ["explore"],
                            errorCode: String? = nil) throws -> NovelSource {
        let value = RuntimeValue.object([
            "installationId": .string(id), "revision": .number(1), "name": .string(name),
            "sourceUrl": .string("https://sidebar-fixture.invalid/"), "group": .string(""),
            "order": .number(order), "enabled": .bool(enabled), "enabledExplore": .bool(enabledExplore),
            "protocol": .string("qysg"), "actions": .array(actions.map { .string($0) }),
            "errorCode": errorCode.map { .string($0) } ?? .null,
        ])
        return try JSONDecoder().decode(NovelSource.self, from: RuntimeJSON.encode(value))
    }

    private func video(key: FilmSourceKey, name: String, order: Int,
                       options: FilmVideoOptions = .init(), detection: FilmSourceDetection? = nil) -> FilmVideoSource {
        // Capability flags deliberately do not promise a home page: D1 permits display only.
        FilmVideoSource(key: key, name: name, api: "/fixture", actions: ["search"], methods: [:],
                        options: options, order: order, detection: detection)
    }

    private struct Parts {
        let icon: UIImageView
        let card: UIView
        let upper: UIStackView
        let settings: UIButton
        let scroll: UIScrollView
    }

    private func parts(in sidebar: UIView) throws -> Parts {
        try Parts(icon: item("icon", in: sidebar, as: UIImageView.self),
                  card: item("card", in: sidebar, as: UIView.self),
                  upper: item("upper", in: sidebar, as: UIStackView.self),
                  settings: item("settings", in: sidebar, as: UIButton.self),
                  scroll: item("scroll", in: sidebar, as: UIScrollView.self))
    }

    private func assertLayout(_ parts: Parts, in sidebar: UIView, file: StaticString = #filePath, line: UInt = #line) {
        sidebar.layoutIfNeeded()
        guard let panel = parts.scroll.superview else {
            XCTFail("The content viewport must be hosted in the sidebar panel", file: file, line: line)
            return
        }
        let safeArea = panel.safeAreaLayoutGuide.layoutFrame
        XCTAssertEqual(parts.scroll.frame.minX, safeArea.minX, accuracy: 1, file: file, line: line)
        XCTAssertEqual(parts.scroll.frame.width, safeArea.width, accuracy: 1, file: file, line: line)
        XCTAssertEqual(parts.scroll.frame.minY, safeArea.minY, accuracy: 1, file: file, line: line)
        XCTAssertTrue(parts.settings.superview === panel, file: file, line: line)
        XCTAssertEqual(parts.settings.frame.maxY, safeArea.maxY - 16, accuracy: 1, file: file, line: line)
        XCTAssertEqual(parts.scroll.frame.maxY, parts.settings.frame.minY - 16, accuracy: 1, file: file, line: line)
        let icon = frame(parts.icon, in: sidebar), card = frame(parts.card, in: sidebar)
        let upper = frame(parts.upper, in: sidebar)
        let settings = frame(parts.settings, in: sidebar)
        XCTAssertEqual(icon.midX, frame(parts.scroll, in: sidebar).midX, accuracy: 1, file: file, line: line)
        XCTAssertLessThan(icon.maxY, card.minY, file: file, line: line)
        XCTAssertEqual(settings.minX, card.minX, accuracy: 1, file: file, line: line)
        XCTAssertEqual(settings.maxX, card.maxX, accuracy: 1, file: file, line: line)
        XCTAssertEqual(parts.card.backgroundColor ?? .clear, .clear, file: file, line: line)
        XCTAssertTrue(parts.icon.isDescendant(of: parts.scroll), file: file, line: line)
        XCTAssertTrue(parts.card.isDescendant(of: parts.scroll), file: file, line: line)
        XCTAssertTrue(parts.upper.superview === parts.card, file: file, line: line)
        XCTAssertNil(identified("app.sidebar.musicSource", in: sidebar), file: file, line: line)
        XCTAssertNil(identified("app.sidebar.filmSource", in: sidebar), file: file, line: line)
        XCTAssertFalse(parts.settings.isDescendant(of: parts.scroll), file: file, line: line)
        XCTAssertEqual(upper.minY, card.minY, accuracy: 1, file: file, line: line)
        XCTAssertEqual(upper.maxY, card.maxY, accuracy: 1, file: file, line: line)
        XCTAssertEqual(parts.scroll.contentSize.width, parts.scroll.bounds.width, accuracy: 1, file: file, line: line)
        XCTAssertEqual(frame(parts.card, in: parts.scroll).maxY + 16,
                       parts.scroll.contentSize.height, accuracy: 1, file: file, line: line)
        for view in [parts.icon, parts.card, parts.upper, parts.settings, parts.scroll] {
            XCTAssertGreaterThan(view.bounds.width, 0, file: file, line: line)
            XCTAssertGreaterThan(view.bounds.height, 0, file: file, line: line)
        }
        XCTAssertEqual(upper.minX, card.minX, accuracy: 1, file: file, line: line)
        XCTAssertEqual(upper.maxX, card.maxX, accuracy: 1, file: file, line: line)
        let ownedViews = [sidebar, parts.icon, parts.card, parts.upper, parts.settings, parts.scroll]
            + parts.upper.arrangedSubviews
            + descendants(parts.card).filter { $0 is UIStackView || $0 is UILabel || $0 is UIImageView }
        for view in ownedViews where !view.isHidden {
            XCTAssertFalse(view.hasAmbiguousLayout, view.accessibilityIdentifier ?? String(describing: type(of: view)), file: file, line: line)
            XCTAssertGreaterThanOrEqual(view.bounds.height, 0, file: file, line: line)
        }
        for stack in descendants(parts.card).compactMap({ $0 as? UIStackView }) where stack.axis == .vertical {
            let frames = stack.arrangedSubviews.filter { !$0.isHidden }.map { frame($0, in: sidebar) }
            for (previous, next) in zip(frames, frames.dropFirst()) {
                XCTAssertLessThanOrEqual(previous.maxY, next.minY + 1, file: file, line: line)
            }
        }
        XCTAssertNoThrow(try assertRowAlignment(parts, in: sidebar, file: file, line: line), file: file, line: line)
    }

    private func assertRowAlignment(_ parts: Parts, in sidebar: UIView,
                                    file: StaticString = #filePath, line: UInt = #line) throws {
        let rows = descendants(parts.card).filter {
            $0.accessibilityIdentifier?.hasPrefix("app.sidebar.") == true && ($0 is UIButton || ($0 is UIStackView && $0.isAccessibilityElement))
        } + [parts.settings]
        for row in rows {
            let icon = try item("rowIcon", in: row, as: UIImageView.self)
            XCTAssertNotNil(icon.image, file: file, line: line)
            XCTAssertEqual(icon.bounds.width, 24, accuracy: 1, file: file, line: line)
            XCTAssertEqual(icon.bounds.height, 24, accuracy: 1, file: file, line: line)
            XCTAssertEqual(frame(icon, in: sidebar).minX, frame(row, in: sidebar).minX + 14, accuracy: 1, file: file, line: line)
            XCTAssertEqual(frame(icon, in: sidebar).midY, frame(row, in: sidebar).midY, accuracy: 1, file: file, line: line)
            XCTAssertGreaterThanOrEqual(row.bounds.height, 56, file: file, line: line)
            let title: UILabel
            if let button = row as? UIButton {
                title = try XCTUnwrap(button.titleLabel)
                XCTAssertNil(button.configuration?.image, "Symbols use an independent fixed slot", file: file, line: line)
                XCTAssertEqual(button.configuration?.contentInsets.leading, 50, file: file, line: line)
                XCTAssertEqual(button.contentHorizontalAlignment, .leading, file: file, line: line)
            } else {
                title = try XCTUnwrap((row as? UIStackView)?.arrangedSubviews.last as? UILabel)
            }
            XCTAssertEqual(frame(title, in: sidebar).minX, frame(row, in: sidebar).minX + 50, accuracy: 1, file: file, line: line)
            XCTAssertEqual(frame(title, in: sidebar).minX, frame(parts.settings, in: sidebar).minX + 50, accuracy: 1, file: file, line: line)
            XCTAssertTrue(title.adjustsFontForContentSizeCategory, file: file, line: line)
            for label in descendants(row).compactMap({ $0 as? UILabel }) where !label.isHidden {
                XCTAssertFalse(label.font.fontDescriptor.symbolicTraits.contains(.traitBold), file: file, line: line)
                XCTAssertFalse(label.font.fontDescriptor.symbolicTraits.contains(.traitItalic), file: file, line: line)
                if let traits = label.font.fontDescriptor.object(forKey: .traits) as? [UIFontDescriptor.TraitKey: Any],
                   let weight = traits[.weight] as? NSNumber {
                    XCTAssertEqual(weight.doubleValue, Double(UIFont.Weight.regular.rawValue), accuracy: 0.01, file: file, line: line)
                }
            }
        }
        for stack in [parts.upper] + descendants(parts.upper).compactMap({ $0 as? UIStackView }) {
            guard stack.axis == .vertical, !stack.isHidden else { continue }
            XCTAssertEqual(stack.spacing, 4, file: file, line: line)
            let rows = stack.arrangedSubviews.filter { !$0.isHidden }
            for (previous, next) in zip(rows, rows.dropFirst()) {
                XCTAssertEqual(frame(next, in: sidebar).minY - frame(previous, in: sidebar).maxY, 4, accuracy: 1, file: file, line: line)
            }
        }
    }

    private func assertSettingsReachable(_ parts: Parts, in sidebar: MeowfilmSidebarView) throws {
        let scroll = parts.scroll
        scroll.setContentOffset(.zero, animated: false)
        sidebar.layoutIfNeeded()
        let fixedFrame = frame(parts.settings, in: sidebar)
        let maximumOffset = max(0, scroll.contentSize.height - scroll.bounds.height)
        let previousAction = sidebar.onSettings
        var settingsCount = 0
        sidebar.onSettings = { settingsCount += 1 }
        defer { sidebar.onSettings = previousAction }
        for offset in [CGFloat.zero, maximumOffset] {
            scroll.setContentOffset(CGPoint(x: 0, y: offset), animated: false)
            sidebar.layoutIfNeeded()
            XCTAssertEqual(scroll.contentOffset.y, offset, accuracy: 1)
            assertEqualFrame(frame(parts.settings, in: sidebar), fixedFrame)
            XCTAssertLessThanOrEqual(frame(scroll, in: sidebar).maxY + 16, fixedFrame.minY + 1)
            XCTAssertTrue(sidebar.bounds.contains(fixedFrame))
            let point = parts.settings.convert(CGPoint(x: parts.settings.bounds.midX, y: parts.settings.bounds.midY), to: sidebar)
            let hit = try XCTUnwrap(sidebar.hitTest(point, with: nil))
            XCTAssertTrue(hit === parts.settings || hit.isDescendant(of: parts.settings), "The fixed footer remains hit-testable at either scroll edge")
            parts.settings.sendActions(for: .touchUpInside)
        }
        XCTAssertEqual(settingsCount, 2, "The real footer action remains callable before and after scrolling")
        let lastRow = try XCTUnwrap(descendants(parts.card).filter {
            $0.accessibilityIdentifier?.hasPrefix("app.sidebar.") == true && ($0 is UIButton || $0.isAccessibilityElement)
        }.max { frame($0, in: sidebar).maxY < frame($1, in: sidebar).maxY })
        // An AXXXL source name may be taller than the viewport; both edges must
        // remain reachable without asking UIKit to display the whole row at once.
        for y in [lastRow.bounds.minY, lastRow.bounds.maxY - 1] {
            let edge = CGRect(x: lastRow.bounds.midX, y: y, width: 1, height: 1)
            scroll.scrollRectToVisible(lastRow.convert(edge, to: scroll), animated: false)
            sidebar.layoutIfNeeded()
            XCTAssertTrue(scroll.bounds.insetBy(dx: -1, dy: -1).contains(lastRow.convert(edge, to: scroll)),
                          "Both edges of the final row remain reachable above the fixed footer")
            assertEqualFrame(frame(parts.settings, in: sidebar), fixedFrame)
        }
    }

    private func assertDisplayOnly(_ row: UIStackView, file: StaticString = #filePath, line: UInt = #line) {
        XCTAssertTrue(row.isAccessibilityElement, file: file, line: line)
        XCTAssertTrue(row.accessibilityTraits.contains(.staticText), file: file, line: line)
        XCTAssertFalse(row.accessibilityTraits.contains(.button), file: file, line: line)
        XCTAssertFalse(row.accessibilityTraits.contains(.link), file: file, line: line)
        XCTAssertFalse(row.accessibilityActivate(), "Display rows must not activate navigation", file: file, line: line)
        for view in [row] + descendants(row) {
            XCTAssertFalse(view is UIControl, file: file, line: line)
            XCTAssertTrue(view.gestureRecognizers?.isEmpty ?? true, file: file, line: line)
            XCTAssertTrue(view.accessibilityCustomActions?.isEmpty ?? true, file: file, line: line)
        }
    }

    private func videoRows(in sidebar: UIView) throws -> [UIStackView] {
        sidebar.layoutIfNeeded()
        return try descendants(sidebar).filter { $0.accessibilityIdentifier?.hasPrefix("app.sidebar.video.") == true }
            .map { try XCTUnwrap($0 as? UIStackView) }
            .sorted { frame($0, in: sidebar).minY < frame($1, in: sidebar).minY }
    }

    private func rowKey(_ row: UIStackView) throws -> FilmSourceKey {
        let id = try XCTUnwrap(row.accessibilityIdentifier)
        let encoded = id.dropFirst("app.sidebar.video.".count).split(separator: ".", omittingEmptySubsequences: false)
        XCTAssertEqual(encoded.count, 2)
        let installation = try XCTUnwrap(encoded.first.flatMap { Data(base64Encoded: String($0)) })
        let site = try XCTUnwrap(encoded.last.flatMap { Data(base64Encoded: String($0)) })
        return FilmSourceKey(installationId: try XCTUnwrap(String(data: installation, encoding: .utf8)),
                             siteKey: try XCTUnwrap(String(data: site, encoding: .utf8)))
    }

    private func bookRows(in sidebar: UIView) throws -> [UIButton] {
        sidebar.layoutIfNeeded()
        return try descendants(sidebar).filter { $0.accessibilityIdentifier?.hasPrefix("app.sidebar.book.") == true }
            .map { try XCTUnwrap($0 as? UIButton) }
            .sorted { frame($0, in: sidebar).minY < frame($1, in: sidebar).minY }
    }

    private func bookRowID(_ row: UIButton) throws -> String {
        let id = try XCTUnwrap(row.accessibilityIdentifier)
        let bytes = try XCTUnwrap(Data(base64Encoded: String(id.dropFirst("app.sidebar.book.".count))))
        return try XCTUnwrap(String(data: bytes, encoding: .utf8))
    }

    private func item<T: UIView>(_ suffix: String, in root: UIView, as type: T.Type) throws -> T {
        try XCTUnwrap(identified("app.sidebar.\(suffix)", in: root) as? T, suffix)
    }

    private func identified(_ id: String, in root: UIView) -> UIView? {
        ([root] + descendants(root)).first { $0.accessibilityIdentifier == id }
    }

    private func descendants(_ view: UIView) -> [UIView] {
        view.subviews.flatMap { [$0] + descendants($0) }
    }

    private func frame(_ view: UIView, in root: UIView) -> CGRect { view.convert(view.bounds, to: root) }

    private func assertEqualFrame(_ actual: CGRect, _ expected: CGRect, file: StaticString = #filePath, line: UInt = #line) {
        XCTAssertEqual(actual.minX, expected.minX, accuracy: 1, file: file, line: line)
        XCTAssertEqual(actual.minY, expected.minY, accuracy: 1, file: file, line: line)
        XCTAssertEqual(actual.width, expected.width, accuracy: 1, file: file, line: line)
        XCTAssertEqual(actual.height, expected.height, accuracy: 1, file: file, line: line)
    }

    private func eventually(_ description: String, _ predicate: @escaping @MainActor () -> Bool) async {
        let ready = expectation(for: NSPredicate { _, _ in MainActor.assumeIsolated { predicate() } }, evaluatedWith: nil)
        ready.expectationDescription = description
        await fulfillment(of: [ready], timeout: 5)
        XCTAssertTrue(predicate(), description)
    }

    private func settle() async {
        let settled = expectation(description: "Sidebar UIKit updates settled")
        DispatchQueue.main.async { settled.fulfill() }
        await fulfillment(of: [settled], timeout: 3)
    }

    private func foregroundScene() async throws -> UIWindowScene {
        await eventually("foreground App scene available") {
            UIApplication.shared.applicationState == .active &&
                UIApplication.shared.connectedScenes.contains { $0 is UIWindowScene && $0.activationState == .foregroundActive }
        }
        return try XCTUnwrap(UIApplication.shared.connectedScenes.compactMap { $0 as? UIWindowScene }
            .first { $0.activationState == .foregroundActive })
    }

    private func host(_ sidebar: MeowfilmSidebarView, size: CGSize = CGSize(width: 390, height: 844),
                      category: UIContentSizeCategory = .large) async throws -> UIWindow {
        let scene = try await foregroundScene()
        let window = UIWindow(windowScene: scene)
        hostedWindows.append((window, scene.windows.first { $0.isKeyWindow }))
        window.frame = scene.coordinateSpace.bounds
        window.windowLevel = .normal + 1
        let container = UIViewController()
        let page = UIViewController()
        page.traitOverrides.preferredContentSizeCategory = category
        if size.width > size.height {
            // Exercise a landscape safe area without rotating the shared App scene.
            page.additionalSafeAreaInsets = .init(top: 0, left: 44, bottom: 21, right: 12)
        }
        page.view = sidebar
        container.addChild(page)
        container.view.addSubview(sidebar)
        sidebar.translatesAutoresizingMaskIntoConstraints = false
        NSLayoutConstraint.activate([
            sidebar.leadingAnchor.constraint(equalTo: container.view.leadingAnchor),
            sidebar.topAnchor.constraint(equalTo: container.view.topAnchor),
            sidebar.widthAnchor.constraint(equalToConstant: size.width),
            sidebar.heightAnchor.constraint(equalToConstant: size.height),
        ])
        page.didMove(toParent: container)
        window.rootViewController = container
        window.makeKeyAndVisible()
        window.layoutIfNeeded()
        await settle()
        sidebar.layoutIfNeeded()
        XCTAssertTrue(sidebar.window === window)
        return window
    }

    private func unhost(_ window: UIWindow) {
        guard let index = hostedWindows.firstIndex(where: { $0.window === window }) else { return }
        let entry = hostedWindows.remove(at: index)
        for child in window.rootViewController?.children ?? [] { child.view.removeFromSuperview() }
        window.isHidden = true
        window.rootViewController = nil
        entry.previous?.makeKey()
    }

    private func attach(_ sidebar: UIView, _ name: String) {
        XCTAssertEqual(sidebar.window?.windowScene?.activationState, .foregroundActive)
        sidebar.layoutIfNeeded()
        var rendered = false
        let image = UIGraphicsImageRenderer(bounds: sidebar.bounds).image { _ in
            rendered = sidebar.drawHierarchy(in: sidebar.bounds, afterScreenUpdates: true)
        }
        XCTAssertTrue(rendered, "The App-hosted sidebar must render its attachment")
        let attachment = XCTAttachment(image: image)
        attachment.name = name
        attachment.lifetime = .keepAlways
        add(attachment)
    }
}

@MainActor
private final class SidebarFilmFixture {
    let directory: URL
    let database: SQLiteDatabase
    let sources: FilmSourceService
    let rules: FilmRuleService
    let model: FilmSettingsModel
    let transport: SidebarSourceTransport

    init(videos: [FilmVideoSource]) throws {
        directory = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        database = try appStorageTestOpen(directory)
        let settings = FilmRuntimeTestSettings(database: database)
        try settings.seedCats(Self.cats(for: videos))
        let document = FilmSourceDocument(cats: Self.cats(for: videos).map(StoredFilmCat.init), videos: videos)
        let repository = FilmSourceRepository(database: database)
        _ = try appStorageTestWait { try await repository.save(document, expected: 0) }
        let transport = SidebarSourceTransport()
        self.transport = transport
        sources = FilmSourceService(transport: .init(
            request: { method, _ in try await transport.call(method) }), database: database,
            settingsDocument: settings[.catSources], runtimeSourceRoot: directory.appendingPathComponent("cat"))
        rules = FilmRuleService(settingsDocument: settings[.rules], connection: {
            let resources = try await MainActor.run {
                let app = try XCTUnwrap(UIApplication.shared.delegate as? MeowfilmAppDelegate)
                return try XCTUnwrap(app.readyAssembly()?.resources)
            }
            return try await resources.scriptSession.connection()
        })
        let tmdb = FilmTMDBSettingsModel(service: FilmTMDBService(settingsDocument: settings[.tmdb], connection: {
            XCTFail("Rendering the saved sidebar must not request TMDB")
            throw FilmSearchError.unavailable
        }), didSave: {})
        let douban = FilmDoubanSettingsModel(service: FilmDoubanSettingsService(settingsDocument: settings[.douban]), didSave: {})
        model = FilmSettingsModel(rules: rules, sources: sources, tmdb: tmdb, douban: douban,
            pan: PanAccountsModel(service: .init(document: settings[.panAccounts],
                request: { _, _, _ in throw RuntimeHTTPError.closed })))
    }

    static func cats(for videos: [FilmVideoSource]) -> [FilmCatSource] {
        Set(videos.map(\.key.installationId)).sorted().map { installation in
            FilmCatSource(installationId: installation, name: installation, sourceType: "local",
                          generation: 1, state: "ready", sourceHash: "sidebar-fixture",
                          sites: videos.filter { $0.key.installationId == installation }.map {
                              .init(key: $0.key.siteKey, name: $0.name, api: $0.api, actions: $0.actions, methods: $0.methods)
                          }, inputRevision: 1)
        }
    }

    func close() async throws {
        model.close()
        await sources.close()
        await rules.close()
        try await database.close()
        try FileManager.default.removeItem(at: directory)
    }
}

private actor SidebarSourceTransport {
    private(set) var calls: [String] = []
    private var nextStatus: [FilmCatSource]?

    func allowNextStatus(_ cats: [FilmCatSource]) { nextStatus = cats }

    func call(_ method: String) throws -> RuntimeValue {
        calls.append(method)
        guard method == "status", let cats = nextStatus else {
            XCTFail("Sidebar unexpectedly called source transport: \(method)")
            throw FilmSourceError(code: "UNEXPECTED_SIDEBAR_REQUEST")
        }
        nextStatus = nil
        return .object(["instances": .array(try cats.map { try RuntimeJSON.decode(JSONEncoder().encode($0)) })])
    }
}

@MainActor
private final class SidebarNovelFixture {
    let directory: URL
    let database: SQLiteDatabase
    let feature: NovelFeature
    let discovery: NovelDiscoverySession
    let runtime: SidebarNovelRuntime

    init(sources: [NovelSource]) throws {
        directory = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        database = try appStorageTestOpen(directory)
        let runtime = SidebarNovelRuntime(sources: sources)
        self.runtime = runtime
        feature = NovelFeature(
            library: NovelLibrary(database: database),
            rules: NovelRuleProcessor(runtime: runtime), runtime: runtime)
        discovery = NovelDiscoverySession(feature: feature)
    }

    func close() async throws {
        await discovery.close()
        await feature.close()
        try await database.close()
        try FileManager.default.removeItem(at: directory)
    }
}

private actor SidebarNovelRuntime: NovelRuntimeServing {
    private var sources: [NovelSource]
    private(set) var calls: [String] = []

    init(sources: [NovelSource]) { self.sources = sources }
    func replaceSources(_ sources: [NovelSource]) { self.sources = sources }
    func hasActiveCalls() -> Bool { false }
    func setActivityHandler(_ handler: (@Sendable (Bool) -> Void)?) {}
    func close() {}

    func image(_ reference: String) throws -> Data {
        XCTFail("The sidebar must not request source images")
        throw NovelError.invalidResponse
    }

    func call(_ operation: String, _ parameters: RuntimeValue) throws -> RuntimeValue {
        calls.append(operation)
        guard operation == "library.list" else {
            XCTFail("Sidebar unexpectedly called novel runtime: \(operation)")
            throw NovelError.invalidResponse
        }
        return .object(["sources": try RuntimeJSON.decode(JSONEncoder().encode(sources))])
    }
}
