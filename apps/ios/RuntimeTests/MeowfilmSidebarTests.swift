import XCTest
import UIKit
import MeowRuntime
import MeowStorage
import MeowMusicFeature
@testable import MeowFilmFeature
@testable import Meowfilm

/// Real App-hosted UIKit; video directories and metadata responses are local fixtures.
@MainActor
final class MeowfilmSidebarTests: XCTestCase {
    private var hostedWindows: [(window: UIWindow, previous: UIWindow?)] = []
    private var filmFixtures: [SidebarFilmFixture] = []

    override func setUp() async throws {
        try await super.setUp()
        _ = try await hostResources()
        _ = try await foregroundScene()
    }

    override func tearDown() async throws {
        while let entry = hostedWindows.last { unhost(entry.window) }
        for fixture in filmFixtures { try await fixture.close() }
        filmFixtures.removeAll()
        try await super.tearDown()
    }

    func testMusicLayoutCentersIconSeparatesGroupsAndAlignsIndependentSettings() async throws {
        let sidebar = try musicSidebar(selected: .channels)
        let window = try await host(sidebar)
        defer { unhost(window) }
        try await waitForMusicMenu(sidebar)
        let parts = try parts(in: sidebar)
        assertLayout(parts, in: sidebar)

        XCTAssertNotNil(parts.icon.image, "The App icon must be bundled in the real host")
        XCTAssertEqual(parts.upper.arrangedSubviews.map(\.accessibilityIdentifier),
                       ["app.sidebar.home", "app.sidebar.channels"])
        XCTAssertEqual(parts.lower.arrangedSubviews.map(\.accessibilityIdentifier),
                       ["app.sidebar.musicSource"])
        let home = try item("home", in: sidebar, as: UIButton.self)
        let channels = try item("channels", in: sidebar, as: UIButton.self)
        XCTAssertEqual(home.configuration?.title, "首页")
        XCTAssertEqual(channels.configuration?.title, "频道")
        XCTAssertFalse(home.isSelected)
        XCTAssertTrue(channels.isSelected)
        XCTAssertLessThanOrEqual(frame(home, in: sidebar).maxY, frame(channels, in: sidebar).minY)
        let upper = frame(parts.upper, in: sidebar)
        let lower = frame(parts.lower, in: sidebar)
        let card = frame(parts.card, in: sidebar)
        XCTAssertGreaterThan(lower.minY - upper.maxY, home.bounds.height,
                             "Ordinary size keeps real flexible space between navigation and quick settings")
        XCTAssertLessThan(upper.minY - card.minY, card.maxY - upper.maxY)
        XCTAssertLessThan(card.maxY - lower.maxY, lower.minY - card.minY)
        XCTAssertEqual(parts.settings.configuration?.title, "设置")
        XCTAssertEqual(parts.settings.contentHorizontalAlignment, .center)
        let title = try XCTUnwrap(parts.settings.titleLabel)
        XCTAssertEqual(frame(title, in: sidebar).midX, frame(parts.settings, in: sidebar).midX, accuracy: 1)
        try assertSettingsReachable(parts, in: sidebar)
        attach(sidebar, "sidebar-music-ordinary")
    }

    func testAddingIntrinsicQuickSettingConsumesGapUpwardWithoutMovingBottomSettings() async throws {
        let sidebar = try musicSidebar()
        let window = try await host(sidebar)
        defer { unhost(window) }
        try await waitForMusicMenu(sidebar)
        let parts = try parts(in: sidebar)
        let beforeUpper = frame(parts.upper, in: sidebar)
        let beforeLower = frame(parts.lower, in: sidebar)
        let beforeCard = frame(parts.card, in: sidebar)
        let beforeSettings = frame(parts.settings, in: sidebar)

        // Only this fixture adds a control; its height comes from UIKit content.
        let extra = UIButton(type: .system)
        extra.setTitle("布局测试快捷设置", for: .normal)
        extra.titleLabel?.font = .preferredFont(forTextStyle: .body)
        extra.accessibilityIdentifier = "fixture.sidebar.extraSetting"
        XCTAssertGreaterThan(extra.intrinsicContentSize.height, 0)
        parts.lower.addArrangedSubview(extra)
        sidebar.setNeedsLayout()
        sidebar.layoutIfNeeded()

        assertLayout(parts, in: sidebar)
        let afterLower = frame(parts.lower, in: sidebar)
        XCTAssertGreaterThan(afterLower.height, beforeLower.height)
        XCTAssertLessThan(afterLower.minY, beforeLower.minY)
        XCTAssertEqual(afterLower.maxY, beforeLower.maxY, accuracy: 1)
        XCTAssertGreaterThanOrEqual(extra.bounds.height, extra.intrinsicContentSize.height - 1)
        assertEqualFrame(frame(parts.upper, in: sidebar), beforeUpper)
        assertEqualFrame(frame(parts.card, in: sidebar), beforeCard)
        assertEqualFrame(frame(parts.settings, in: sidebar), beforeSettings)
        XCTAssertLessThan(afterLower.minY - beforeUpper.maxY, beforeLower.minY - beforeUpper.maxY)
        attach(sidebar, "sidebar-music-extra-quick-setting")
    }

    func testNavigationSettingsAndDismissDispatchOnlyTheirOwnCallbacks() async throws {
        let metadata = SidebarMetadataSource(id: "fixture", title: "测试元数据")
        let home = FilmHomeModel(sources: [metadata])
        defer { home.close() }
        let sidebars = [
            try musicSidebar(selected: .channels),
            try sidebar(.film(home: home, settings: nil)),
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
            sidebar.onError = { XCTFail("Unexpected sidebar error: \($0)") }
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
        let metadataReads = await metadata.reads
        XCTAssertEqual(metadataReads, 0, "Navigation callbacks do not themselves fetch film content")
    }

    func testFilmUsesSharedLayoutAndOnlyTheProductionDoubanMetadataChoice() async throws {
        let fixture = try await filmFixture(videos: [video(key: .init(installationId: "local", siteKey: "one"),
                                                         name: "示例视频源", order: 0)])
        let music = try musicSidebar()
        let musicWindow = try await host(music)
        try await waitForMusicMenu(music)
        let musicParts = try parts(in: music)
        let reference = [musicParts.icon, musicParts.card, musicParts.settings].map { frame($0, in: music) }
        unhost(musicWindow)

        // Read the real assembly's registered sources without asking them to load.
        let home = FilmHomeModel(sources: try assembly().filmSources)
        defer { home.close() }
        XCTAssertEqual(home.sources.map(\.id), ["douban"])
        XCTAssertEqual(home.sources.map(\.title), ["豆瓣"])
        let film = try sidebar(.film(home: home, settings: fixture.model))
        let window = try await host(film)
        defer { unhost(window) }
        let parts = try parts(in: film)
        assertLayout(parts, in: film)
        for (view, expected) in zip([parts.icon, parts.card, parts.settings], reference) {
            assertEqualFrame(frame(view, in: film), expected)
        }
        XCTAssertEqual(Array(parts.upper.arrangedSubviews.prefix(2)).map(\.accessibilityIdentifier),
                       ["app.sidebar.filmSource", "app.sidebar.home"])
        XCTAssertTrue(parts.lower.arrangedSubviews.isEmpty)
        XCTAssertNil(identified("app.sidebar.musicSource", in: film))
        XCTAssertNil(identified("app.sidebar.channels", in: film))
        let buttons = descendants(parts.card).compactMap { $0 as? UIButton }
        XCTAssertEqual(buttons.map(\.accessibilityIdentifier), ["app.sidebar.filmSource", "app.sidebar.home"])
        let source = try item("filmSource", in: film, as: UIButton.self)
        XCTAssertTrue(source.showsMenuAsPrimaryAction)
        XCTAssertEqual(source.configuration?.subtitle, "豆瓣")
        let actions = try menuActions(source)
        XCTAssertEqual(actions.map(\.title), ["豆瓣"])
        XCTAssertEqual(actions.map(\.state), [.on])
        XCTAssertEqual(try videoRows(in: film).map(\.accessibilityLabel), ["示例视频源"])
        let calls = await fixture.transport.calls
        XCTAssertTrue(calls.isEmpty, "Rendering the saved directory must not start or probe video sources")
        attach(film, "sidebar-film-shared-layout-douban")
    }

    func testFilmMenuActionsSelectRealModelSourcesAndRestoreCachedSelection() async throws {
        let first = SidebarMetadataSource(id: "first", title: "第一源")
        let second = SidebarMetadataSource(id: "second", title: "第二源")
        let home = FilmHomeModel(sources: [first, second])
        defer { home.close() }
        await home.loadIfNeeded()?.value
        let sidebar = try sidebar(.film(home: home, settings: nil))
        let window = try await host(sidebar)
        defer { unhost(window) }
        let source = try item("filmSource", in: sidebar, as: UIButton.self)
        var actions = try menuActions(source)
        XCTAssertEqual(actions.map(\.title), ["第一源", "第二源"])
        XCTAssertEqual(actions.map(\.state), [.on, .off])
        XCTAssertEqual(source.configuration?.subtitle, "第一源")

        // Invoke the actual menu handler, not FilmHomeModel.selectSource directly.
        invoke(try XCTUnwrap(actions.first { $0.title == "第二源" }))
        await eventually("second metadata source selected through sidebar") {
            home.selectedSourceID == "second" && !home.isLoading &&
                home.sections.first?.items.first?.sourceID == "second" &&
                source.configuration?.subtitle == "第二源"
        }
        actions = try menuActions(source)
        XCTAssertEqual(actions.map(\.state), [.off, .on])
        attach(sidebar, "sidebar-film-second-metadata-selection")
        invoke(try XCTUnwrap(actions.first { $0.title == "第一源" }))
        await eventually("cached first metadata source restored through sidebar") {
            home.selectedSourceID == "first" && !home.isLoading &&
                home.sections.first?.items.first?.sourceID == "first" &&
                source.configuration?.subtitle == "第一源"
        }
        XCTAssertEqual(try menuActions(source).map(\.state), [.on, .off])
        let firstReads = await first.reads
        let secondReads = await second.reads
        XCTAssertEqual(firstReads, 2)
        XCTAssertEqual(secondReads, 1)
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
        let metadata = SidebarMetadataSource(id: "metadata", title: "测试元数据")
        let home = FilmHomeModel(sources: [metadata])
        defer { home.close() }
        let sidebar = try sidebar(.film(home: home, settings: fixture.model))
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
        XCTAssertEqual(home.selectedSourceID, "metadata")
        let metadataReads = await metadata.reads
        let callsAfterRefresh = await fixture.transport.calls
        XCTAssertEqual(metadataReads, 0)
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
                       ["app.sidebar.filmSource", "app.sidebar.home"])
        assertLayout(emptyParts, in: sidebar)
        try assertSettingsReachable(emptyParts, in: sidebar)
        XCTAssertTrue(navigations.isEmpty)
        XCTAssertEqual(settingsCount, 0)
        let finalCalls = await fixture.transport.calls
        XCTAssertEqual(finalCalls, ["status"])
        attach(sidebar, "sidebar-film-empty-directory")
    }

    func testCompactLandscapeAndAccessibilityTypeKeepBothDomainsScrollableAndSettingsReachable() async throws {
        let catalog = (0..<12).map {
            video(key: .init(installationId: "layout", siteKey: "site-\($0)"),
                  name: "视频源 \($0) · 用于验证长名称完整换行显示", order: $0)
        }
        let fixture = try await filmFixture(videos: catalog)
        let home = FilmHomeModel(sources: [SidebarMetadataSource(id: "fixture", title: "豆瓣")])
        defer { home.close() }
        let cases: [(String, CGSize, UIContentSizeCategory)] = [
            ("compact", CGSize(width: 320, height: 568), .large),
            ("landscape", CGSize(width: 844, height: 390), .large),
            ("compact-AXXXL", CGSize(width: 320, height: 568), .accessibilityExtraExtraExtraLarge),
            ("landscape-AXXXL", CGSize(width: 844, height: 390), .accessibilityExtraExtraExtraLarge),
        ]
        for domain in ["music", "film"] {
            var ordinaryFontSize: CGFloat?
            for (name, size, category) in cases {
                let sidebar = try (domain == "music" ? musicSidebar() : self.sidebar(.film(home: home, settings: fixture.model)))
                let window = try await host(sidebar, size: size, category: category)
                if domain == "music" { try await waitForMusicMenu(sidebar) }
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
                                                    "Menu titles and selected values must fit at the requested text size")
                    }
                }
                for row in try videoRows(in: sidebar) {
                    let label = try XCTUnwrap(row.arrangedSubviews.last as? UILabel)
                    let fitting = label.sizeThatFits(CGSize(width: label.bounds.width, height: .greatestFiniteMagnitude))
                    XCTAssertGreaterThanOrEqual(label.bounds.height + 1, fitting.height, "Video names must not be vertically clipped")
                    assertDisplayOnly(row)
                }
                attach(sidebar, "sidebar-\(domain)-\(name)-top")
                if domain == "film" || size.width > size.height || category.isAccessibilityCategory {
                    XCTAssertGreaterThan(parts.scroll.contentSize.height, parts.scroll.bounds.height,
                                         "Overflow must be scrollable instead of compressing the groups")
                }
                try assertSettingsReachable(parts, in: sidebar)
                attach(sidebar, "sidebar-\(domain)-\(name)-settings")
                unhost(window)
            }
        }
        let calls = await fixture.transport.calls
        XCTAssertTrue(calls.isEmpty)
    }

    private func assembly() throws -> MeowfilmAssembly {
        let app = try XCTUnwrap(UIApplication.shared.delegate as? MeowfilmAppDelegate)
        return try XCTUnwrap(app.readyAssembly())
    }

    private func sidebar(_ content: MeowfilmSidebarContent) throws -> MeowfilmSidebarView {
        MeowfilmSidebarView(content: content, themeProvider: try assembly().appearance)
    }

    private func musicSidebar(selected: MeowfilmPrimaryPage = .recommend) throws -> MeowfilmSidebarView {
        let music = try XCTUnwrap(try assembly().music)
        return try sidebar(.music(source: MusicHomeSourceSettingsModel(model: music), selectedPage: selected))
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

    private func video(key: FilmSourceKey, name: String, order: Int,
                       options: FilmVideoOptions = .init(), detection: FilmSourceDetection? = nil) -> FilmVideoSource {
        // Capability flags deliberately do not promise a home page: D1 permits display only.
        FilmVideoSource(key: key, name: name, api: "/fixture", actions: ["search"], methods: [:],
                        options: options, order: order, detection: detection)
    }

    private func waitForMusicMenu(_ sidebar: MeowfilmSidebarView) async throws {
        let source = try item("musicSource", in: sidebar, as: UIButton.self)
        await eventually("music source selection rendered") { source.menu != nil && source.configuration?.subtitle != nil }
        sidebar.setNeedsLayout()
        sidebar.layoutIfNeeded()
    }

    private struct Parts {
        let icon: UIImageView
        let card: UIView
        let upper: UIStackView
        let lower: UIStackView
        let settings: UIButton
        let scroll: UIScrollView
    }

    private func parts(in sidebar: UIView) throws -> Parts {
        try Parts(icon: item("icon", in: sidebar, as: UIImageView.self),
                  card: item("card", in: sidebar, as: UIView.self),
                  upper: item("upper", in: sidebar, as: UIStackView.self),
                  lower: item("lower", in: sidebar, as: UIStackView.self),
                  settings: item("settings", in: sidebar, as: UIButton.self),
                  scroll: item("scroll", in: sidebar, as: UIScrollView.self))
    }

    private func assertLayout(_ parts: Parts, in sidebar: UIView, file: StaticString = #filePath, line: UInt = #line) {
        sidebar.layoutIfNeeded()
        guard let panel = parts.scroll.superview else {
            XCTFail("The content viewport must be hosted in the sidebar panel", file: file, line: line)
            return
        }
        assertEqualFrame(parts.scroll.frame, panel.safeAreaLayoutGuide.layoutFrame, file: file, line: line)
        let icon = frame(parts.icon, in: sidebar), card = frame(parts.card, in: sidebar)
        let upper = frame(parts.upper, in: sidebar), lower = frame(parts.lower, in: sidebar)
        let settings = frame(parts.settings, in: sidebar)
        XCTAssertEqual(icon.midX, frame(parts.scroll, in: sidebar).midX, accuracy: 1, file: file, line: line)
        XCTAssertLessThan(icon.maxY, card.minY, file: file, line: line)
        XCTAssertLessThan(card.maxY, settings.minY, file: file, line: line)
        XCTAssertEqual(settings.minX, card.minX, accuracy: 1, file: file, line: line)
        XCTAssertEqual(settings.maxX, card.maxX, accuracy: 1, file: file, line: line)
        XCTAssertTrue(parts.upper.isDescendant(of: parts.card), file: file, line: line)
        XCTAssertTrue(parts.lower.isDescendant(of: parts.card), file: file, line: line)
        XCTAssertFalse(parts.settings.isDescendant(of: parts.card), file: file, line: line)
        XCTAssertGreaterThanOrEqual(upper.minY, card.minY, file: file, line: line)
        XCTAssertLessThanOrEqual(upper.maxY, lower.minY, file: file, line: line)
        XCTAssertLessThanOrEqual(lower.maxY, card.maxY, file: file, line: line)
        for view in [parts.icon, parts.card, parts.upper, parts.settings, parts.scroll] {
            XCTAssertGreaterThan(view.bounds.width, 0, file: file, line: line)
            XCTAssertGreaterThan(view.bounds.height, 0, file: file, line: line)
        }
        for group in [upper, lower] {
            XCTAssertGreaterThanOrEqual(group.minX + 1, card.minX, file: file, line: line)
            XCTAssertLessThanOrEqual(group.maxX, card.maxX + 1, file: file, line: line)
        }
        let ownedViews = [sidebar, parts.icon, parts.card, parts.upper, parts.lower, parts.settings, parts.scroll]
            + parts.upper.arrangedSubviews + parts.lower.arrangedSubviews
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
    }

    private func assertSettingsReachable(_ parts: Parts, in sidebar: UIView) throws {
        let scroll = parts.scroll
        XCTAssertGreaterThan(parts.settings.bounds.height, 0)
        let target = frame(parts.settings, in: scroll)
        scroll.scrollRectToVisible(target, animated: false)
        sidebar.layoutIfNeeded()
        let visible = scroll.bounds.inset(by: scroll.adjustedContentInset)
        let reached = frame(parts.settings, in: scroll)
        XCTAssertGreaterThanOrEqual(reached.minY + 1, visible.minY)
        XCTAssertLessThanOrEqual(reached.maxY, visible.maxY + 1)
        XCTAssertGreaterThanOrEqual(reached.minX + 1, visible.minX)
        XCTAssertLessThanOrEqual(reached.maxX, visible.maxX + 1)
        let point = parts.settings.convert(CGPoint(x: parts.settings.bounds.midX, y: parts.settings.bounds.midY), to: sidebar)
        let hit = try XCTUnwrap(sidebar.hitTest(point, with: nil))
        XCTAssertTrue(hit === parts.settings || hit.isDescendant(of: parts.settings), "The reachable setting remains hit-testable")
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

    private func menuActions(_ button: UIButton) throws -> [UIAction] {
        let menu = try XCTUnwrap(button.menu)
        return try menu.children.map { try XCTUnwrap($0 as? UIAction) }
    }

    private func invoke(_ action: UIAction) {
        let sender = UIButton(type: .system)
        sender.addAction(action, for: .touchUpInside)
        sender.sendActions(for: .touchUpInside)
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

private actor SidebarMetadataSource: FilmSource {
    nonisolated let descriptor: FilmSourceDescriptor
    private(set) var reads = 0

    init(id: String, title: String) { descriptor = .init(id: id, title: title) }

    func loadHome(refresh: Bool) async throws -> [FilmHomeSection] {
        reads += 1
        return [.init(id: "movies", title: descriptor.title,
                      items: [.init(sourceID: descriptor.id, id: "shared", title: "\(descriptor.title)影片")])]
    }
}

@MainActor
private final class SidebarFilmFixture {
    let directory: URL
    let sources: FilmSourceService
    let rules: FilmRuleService
    let model: FilmSettingsModel
    let transport: SidebarSourceTransport

    init(videos: [FilmVideoSource]) throws {
        directory = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        let store = try DocumentStore(baseURL: directory, namespace: FilmSourceService.documentNamespace)
        let document = FilmSourceDocument(cats: Self.cats(for: videos).map(StoredFilmCat.init), videos: videos)
        try store.write(JSONEncoder().encode(document), key: FilmSourceService.documentKey)
        let transport = SidebarSourceTransport()
        self.transport = transport
        sources = FilmSourceService(transport: .init(
            request: { method, _ in try await transport.call(method) },
            upload: { _, _, _ in try await transport.call("upload") }), storeFactory: { store })
        rules = FilmRuleService(connection: {
            let resources = try await MainActor.run {
                let app = try XCTUnwrap(UIApplication.shared.delegate as? MeowfilmAppDelegate)
                return try XCTUnwrap(app.readyAssembly()?.resources)
            }
            return try await resources.scriptSession.connection()
        })
        let tmdb = FilmTMDBSettingsModel(service: FilmTMDBService(connection: {
            XCTFail("Rendering the saved sidebar must not request TMDB")
            throw FilmSearchError.unavailable
        }), didSave: {})
        model = FilmSettingsModel(rules: rules, sources: sources, tmdb: tmdb)
    }

    static func cats(for videos: [FilmVideoSource]) -> [FilmCatSource] {
        Set(videos.map(\.key.installationId)).sorted().map { installation in
            FilmCatSource(installationId: installation, name: installation, sourceType: "local",
                          generation: 1, state: "ready", sourceHash: "sidebar-fixture",
                          sites: videos.filter { $0.key.installationId == installation }.map {
                              .init(key: $0.key.siteKey, name: $0.name, api: $0.api, actions: $0.actions, methods: $0.methods)
                          })
        }
    }

    func close() async throws {
        model.close()
        await sources.close()
        await rules.close()
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
