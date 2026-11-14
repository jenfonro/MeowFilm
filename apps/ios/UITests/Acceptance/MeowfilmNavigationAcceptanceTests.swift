import XCTest
import UIKit

@MainActor
final class MeowfilmNavigationAcceptanceTests: XCTestCase {
    private var app: XCUIApplication!

    override func setUp() {
        continueAfterFailure = false
        app = XCUIApplication()
        app.launch()
    }

    override func tearDown() {
        XCUIDevice.shared.orientation = .portrait
        super.tearDown()
    }

    func testPrimaryPagesUseAppOwnedHeaders() throws {
        let primary = app.otherElements["app.primary"]
        XCTAssertTrue(primary.waitForExistence(timeout: 30))
        XCTAssertTrue(app.otherElements["page.header"].exists)
        XCTAssertTrue(app.buttons["app.menu"].exists)
        XCTAssertTrue(app.buttons["search.open"].exists)
        XCTAssertTrue(app.buttons["app.feature"].exists)
        assertPrimaryNavigation(selected: "recommend")
        assertHeaderOrder([
            app.buttons["app.menu"], app.buttons["search.open"], app.buttons["app.feature"],
        ])
        assertPageOwnsHeader("recommend", controls: [
            "app.menu", "search.open", "app.feature",
        ])
        attach("01-home")

        openChannels()
        let channelMenu = app.buttons["app.channels"]
        let switcher = app.otherElements["music.channel.switcher"]
        XCTAssertTrue(channelMenu.waitForExistence(timeout: 5))
        XCTAssertTrue(switcher.waitForExistence(timeout: 5))
        assertInsideHeader(channelMenu)
        assertInsideHeader(switcher)
        assertPrimaryNavigation(selected: "recommend")
        assertHeaderOrder([
            app.buttons["app.menu"], switcher, channelMenu, app.buttons["search.open"],
        ])
        assertPageOwnsHeader("channels", controls: [
            "app.menu", "music.channel.switcher", "app.channels", "search.open",
        ])
        XCTAssertFalse(app.otherElements["app.page.recommend"].exists)
        attach("02-channels")

        channelMenu.tap()
        let picker = app.otherElements["music.channels.picker"]
        XCTAssertTrue(picker.waitForExistence(timeout: 5))
        app.buttons["music.channels.picker.close"].tap()
        let pickerDismissed = expectation(for: NSPredicate(format: "exists == false"), evaluatedWith: picker)
        wait(for: [pickerDismissed], timeout: 5)
        XCTAssertEqual(app.otherElements.matching(identifier: "page.header").count, 1)
        XCTAssertTrue(switcher.exists)
        assertInsideHeader(switcher)
        attach("02-channels-picker-dismissed")

        app.buttons["app.primary.library"].tap()
        let title = app.buttons["app.primary.title"]
        XCTAssertTrue(title.waitForExistence(timeout: 5))
        XCTAssertEqual(title.label, "我的")
        assertInsideHeader(title)
        XCTAssertTrue(app.buttons["app.primary.add"].isHittable)
        let layout = app.buttons["music.library.layout"]
        XCTAssertTrue(layout.isHittable)
        XCTAssertGreaterThanOrEqual(layout.frame.width, 44)
        XCTAssertGreaterThanOrEqual(layout.frame.height, 44)
        assertHeaderOrder([app.buttons["app.menu"], title, layout, app.buttons["app.primary.add"]])
        let originalLayoutLabel = layout.label
        XCTAssertTrue(["切换为列表", "切换为网格"].contains(originalLayoutLabel))
        let changedLayoutLabel = originalLayoutLabel == "切换为列表" ? "切换为网格" : "切换为列表"
        layout.tap()
        XCTAssertTrue(layout.waitForExistence(timeout: 5))
        let changed = expectation(for: NSPredicate(format: "label == %@", changedLayoutLabel), evaluatedWith: layout)
        wait(for: [changed], timeout: 5)
        layout.tap()
        let restored = expectation(for: NSPredicate(format: "label == %@", originalLayoutLabel), evaluatedWith: layout)
        wait(for: [restored], timeout: 5)
        assertPrimaryNavigation(selected: "library")
        assertPageOwnsHeader("library", controls: ["app.menu", "app.primary.title", "music.library.layout", "app.primary.add"])
        XCTAssertFalse(app.otherElements["app.page.channels"].exists)
        attach("03-library")

        app.buttons["app.primary.film"].tap()
        XCTAssertTrue(app.otherElements["film.home"].waitForExistence(timeout: 5))
        let filmSearch = app.buttons["search.open"]
        XCTAssertTrue(filmSearch.waitForExistence(timeout: 5))
        assertHeaderOrder([app.buttons["app.menu"], filmSearch])
        XCTAssertGreaterThan(filmSearch.frame.width, filmSearch.frame.height)
        assertPageOwnsHeader("film", controls: ["app.menu", "search.open"])
        assertPrimaryNavigation(selected: "film")
        XCTAssertFalse(app.otherElements["app.page.library"].exists)
        attach("03-film")

        app.buttons["app.primary.novel"].tap()
        XCTAssertTrue(app.otherElements["novel.home"].waitForExistence(timeout: 5))
        assertPageOwnsHeader("novel", controls: ["app.menu", "search.open"])
        assertPrimaryNavigation(selected: "novel")
        XCTAssertTrue(app.segmentedControls["novel.sections"].exists)
        attach("03-novel")

        app.buttons["app.primary.recommend"].tap()
        XCTAssertTrue(app.buttons["search.open"].waitForExistence(timeout: 5))
        assertPageOwnsHeader("recommend", controls: ["app.menu", "search.open", "app.feature"])
        assertPrimaryNavigation(selected: "recommend")
        XCTAssertFalse(app.otherElements["app.page.library"].exists)
        XCTAssertFalse(app.otherElements["app.page.film"].exists)
        app.collectionViews.firstMatch.swipeUp()
        attach("04-home-scrolled")
        assertInsideHeader(app.buttons["search.open"])
        app.collectionViews.firstMatch.swipeDown()
        attach("05-home-restored")
    }

    func testFilmCategoryFiltersOpenDetailAndPreserveSelectionOnReturn() {
        XCTAssertTrue(app.buttons["app.primary.film"].waitForExistence(timeout: 30))
        app.buttons["app.primary.film"].tap()
        let more = app.buttons["film.home.more.movie"]
        XCTAssertTrue(more.waitForExistence(timeout: 30))
        more.tap()
        XCTAssertTrue(app.otherElements["film.category.movie"].waitForExistence(timeout: 5))
        let grid = app.collectionViews["film.category.grid"]
        XCTAssertTrue(grid.cells.firstMatch.waitForExistence(timeout: 30))
        attach("44-film-category")
        let expand = app.buttons["film.category.category.tags.toggle"]
        if expand.exists { expand.tap() }
        let best = app.buttons["film.category.category.tag.豆瓣高分"]
        XCTAssertTrue(best.isHittable)
        best.tap()
        waitSelected(best)
        let japan = app.buttons["film.category.area.tag.日本"]
        XCTAssertTrue(japan.isHittable)
        japan.tap()
        waitSelected(japan)
        let settled = XCTNSPredicateExpectation(predicate: NSPredicate(format: "exists == false"),
                                               object: app.activityIndicators["film.category.loading"])
        XCTAssertEqual(XCTWaiter.wait(for: [settled], timeout: 30), .completed)
        let poster = grid.cells.firstMatch
        XCTAssertTrue(poster.waitForExistence(timeout: 30))
        let title = poster.label
        let identifier = poster.identifier
        attach("45-film-category-filtered")
        poster.tap()
        XCTAssertTrue(app.otherElements["film.detail"].waitForExistence(timeout: 5))
        XCTAssertEqual(app.staticTexts["film.detail.title"].label, title)
        app.buttons["page.header.back"].tap()
        XCTAssertTrue(grid.cells[identifier].waitForExistence(timeout: 5))
        XCTAssertTrue(best.isSelected)
        XCTAssertTrue(japan.isSelected)
        attach("46-film-category-returned")
        app.buttons["page.header.back"].tap()
        XCTAssertTrue(more.waitForExistence(timeout: 5))
    }

    func testFilmHomeOpensDetailAndKeepsItsSnapshot() {
        XCTAssertTrue(app.buttons["app.primary.film"].waitForExistence(timeout: 30))
        app.buttons["app.primary.film"].tap()
        XCTAssertTrue(app.otherElements["film.home"].waitForExistence(timeout: 5))
        assertPageOwnsHeader("film", controls: ["app.menu", "search.open"])
        let poster = app.cells.matching(NSPredicate(format: "identifier BEGINSWITH %@", "film.poster.douban.")).firstMatch
        XCTAssertTrue(poster.waitForExistence(timeout: 30))
        let identifier = poster.identifier
        let title = poster.label
        attach("40-film-home")
        poster.tap()
        XCTAssertTrue(app.otherElements["film.detail"].waitForExistence(timeout: 5))
        XCTAssertEqual(app.staticTexts["film.detail.title"].label, title)
        XCTAssertFalse(app.buttons["film.detail.play"].isEnabled)
        let favorite = app.buttons["film.detail.favorite"]
        XCTAssertTrue(favorite.isHittable)
        let wasSelected = favorite.isSelected
        favorite.tap()
        XCTAssertEqual(favorite.isSelected, !wasSelected)
        favorite.tap()
        XCTAssertEqual(favorite.isSelected, wasSelected)
        attach("41-film-detail")
        let detailTitle = app.staticTexts["film.detail.title"]
        let restingTitleY = detailTitle.frame.minY
        app.coordinate(withNormalizedOffset: CGVector(dx: 0.5, dy: 0.8))
            .press(forDuration: 0.1, thenDragTo: app.coordinate(withNormalizedOffset: CGVector(dx: 0.5, dy: 0.6)),
                   withVelocity: .slow, thenHoldForDuration: 0.3)
        XCTAssertLessThan(detailTitle.frame.minY, restingTitleY - 100, "The mid-scroll shot must show a moved hero")
        attach("41-film-detail-mid-scroll")
        app.scrollViews["film.detail.scroll"].swipeUp()
        attach("42-film-detail-scrolled")
        app.buttons["page.header.back"].tap()
        XCTAssertTrue(app.cells[identifier].waitForExistence(timeout: 5))
        attach("43-film-home-returned")
    }

    func testFilmSearchReturnsToTheSameHomeSnapshotAndScrollPosition() throws {
        XCTAssertTrue(app.buttons["app.primary.film"].waitForExistence(timeout: 30))
        app.buttons["app.primary.film"].tap()
        let collection = app.collectionViews["film.home.sections"]
        XCTAssertTrue(collection.waitForExistence(timeout: 5))
        let posters = collection.cells.matching(NSPredicate(
            format: "identifier BEGINSWITH %@", "film.poster.douban."
        ))
        let first = posters.firstMatch
        XCTAssertTrue(first.waitForExistence(timeout: 30))
        let initialID = first.identifier
        let initialY = first.frame.minY
        collection.swipeUp()
        let anchor = try XCTUnwrap(posters.allElementsBoundByIndex.first { $0.isHittable })
        let identifier = anchor.identifier
        let title = anchor.label
        let position = anchor.frame
        XCTAssertTrue(identifier != initialID || abs(position.minY - initialY) > 1,
                      "The return must preserve an already scrolled film home")
        attach("47-film-home-before-search")

        for useEdgeReturn in [false, true] {
            let search = app.buttons["search.open"]
            assertPageOwnsHeader("film", controls: ["app.menu", "search.open"])
            assertHeaderOrder([app.buttons["app.menu"], search])
            XCTAssertGreaterThan(search.frame.width, search.frame.height)
            search.tap()
            let field = app.textFields["search.field"]
            XCTAssertTrue(field.waitForExistence(timeout: 5))
            assertInsideHeader(field)
            assertInsideHeader(app.buttons["page.header.back"])
            XCTAssertFalse(app.buttons["app.menu"].exists)
            attach(useEdgeReturn ? "48-film-search-before-edge-return" : "48-film-search")
            if useEdgeReturn {
                dragRight(from: 0.005, to: 0.8)
            } else {
                app.buttons["page.header.back"].tap()
            }
            XCTAssertTrue(app.otherElements["app.page.film"].waitForExistence(timeout: 5))
            let restored = collection.cells[identifier]
            XCTAssertTrue(restored.waitForExistence(timeout: 5))
            XCTAssertTrue(restored.isHittable)
            XCTAssertEqual(restored.label, title)
            XCTAssertEqual(restored.frame.minX, position.minX, accuracy: 1)
            XCTAssertEqual(restored.frame.minY, position.minY, accuracy: 1)
            assertPrimaryNavigation(selected: "film")
            assertPageOwnsHeader("film", controls: ["app.menu", "search.open"])
            XCTAssertFalse(app.otherElements["app.page.recommend"].exists)
            attach(useEdgeReturn ? "49-film-home-after-search-edge-return" : "49-film-home-after-search")
        }
    }

    func testSidebarChannelsAndSearchPreserveSelectionAcrossHomeReturns() {
        openChannels()
        let selected = app.buttons.matching(NSPredicate(
            format: "identifier BEGINSWITH %@ AND selected == true", "music.channel.tab."
        )).firstMatch
        XCTAssertTrue(selected.waitForExistence(timeout: 30))
        let selectedID = selected.identifier
        assertPrimaryNavigation(selected: "recommend")
        XCTAssertTrue(app.buttons["app.channels"].isHittable)
        let search = app.buttons["search.open"]
        assertInsideHeader(search)
        XCTAssertEqual(search.label, "搜索")
        XCTAssertEqual(search.frame.width, search.frame.height, accuracy: 1)
        attach("06-channel-search")
        search.tap()
        XCTAssertTrue(app.textFields["search.field"].waitForExistence(timeout: 5))
        app.buttons["page.header.back"].tap()
        XCTAssertTrue(app.otherElements["app.page.channels"].waitForExistence(timeout: 5))
        waitSelected(app.buttons[selectedID])
        assertPrimaryNavigation(selected: "recommend")

        app.buttons["app.primary.recommend"].tap()
        XCTAssertTrue(app.otherElements["app.page.recommend"].waitForExistence(timeout: 5))
        XCTAssertFalse(app.otherElements["app.page.channels"].exists)
        assertPrimaryNavigation(selected: "recommend")
        XCTAssertTrue(app.buttons["app.feature"].isHittable)
        openChannels()
        waitSelected(app.buttons[selectedID])

        openSidebar()
        app.buttons["app.sidebar.home"].tap()
        XCTAssertTrue(app.otherElements["app.sidebar"].waitForNonExistence(timeout: 5))
        XCTAssertTrue(app.otherElements["app.page.recommend"].waitForExistence(timeout: 5))
        XCTAssertFalse(app.otherElements["app.page.channels"].exists)
        assertPrimaryNavigation(selected: "recommend")
        XCTAssertTrue(app.buttons["app.feature"].isHittable)
        openChannels()
        waitSelected(app.buttons[selectedID])
    }

    func testChannelContentPagingAndEdgeReturnHaveSeparateGestures() throws {
        openChannels()
        let channels = app.otherElements["app.page.channels"]
        let tabs = app.buttons.matching(NSPredicate(
            format: "identifier BEGINSWITH %@", "music.channel.tab."
        ))
        let selected = tabs.matching(NSPredicate(format: "selected == true")).firstMatch
        XCTAssertTrue(selected.waitForExistence(timeout: 30))
        let directory = tabs.allElementsBoundByIndex
        XCTAssertGreaterThan(directory.count, 1)
        // Start in the first, mixed-layout channel: a prior run may have retained
        // a grid-only playlist channel, which cannot exercise horizontal-row arbitration.
        let original = directory[0]
        if !original.isSelected { original.tap() }
        waitSelected(original)
        let originalID = original.identifier
        let next = directory[1]
        swipeChannelWhitespace(left: true)
        waitSelected(next)
        XCTAssertTrue(channels.exists)
        assertPrimaryNavigation(selected: "recommend")
        swipeChannelWhitespace(left: false)
        waitSelected(app.buttons[originalID])

        dragRight(from: 0.005, to: 0.04)
        XCTAssertTrue(channels.exists)
        assertPrimaryNavigation(selected: "recommend")
        waitSelected(app.buttons[originalID])
        attach("07-channel-edge-cancelled")
        dragRight(from: 0.005, to: 0.8)
        XCTAssertTrue(app.otherElements["app.page.recommend"].waitForExistence(timeout: 5))
        XCTAssertFalse(channels.exists)
        assertPrimaryNavigation(selected: "recommend")
        attach("08-channel-edge-returned")
        openChannels()
        waitSelected(app.buttons[originalID])

        let rowY = try horizontalRowMidY()
        attach("09-channel-row-before-edge-return")
        dragRight(from: 0.005, to: 0.8, y: rowY)
        XCTAssertTrue(app.otherElements["app.page.recommend"].waitForExistence(timeout: 5))
        XCTAssertFalse(channels.exists)
        assertPrimaryNavigation(selected: "recommend")
        attach("09-channel-row-edge-returned")
        openChannels()
        waitSelected(app.buttons[originalID])
    }

    func testSearchHeadersAndResults() throws {
        XCTAssertTrue(app.buttons["search.open"].waitForExistence(timeout: 30))
        app.buttons["search.open"].tap()

        let field = app.textFields["search.field"]
        XCTAssertTrue(field.waitForExistence(timeout: 5))
        XCTAssertTrue(app.otherElements["page.header"].exists)
        XCTAssertTrue(app.buttons["page.header.back"].exists)
        assertInsideHeader(field)
        assertInsideHeader(app.buttons["search.submit"])
        XCTAssertEqual(field.frame.height, 40, accuracy: 1)
        attach("10-search")
        app.tables["music.search.discovery"].swipeUp()
        attach("10-search-scrolled")

        field.tap()
        field.typeText("周杰伦")
        app.buttons["search.submit"].tap()
        XCTAssertTrue(app.tables["music.search.results.page.0"].waitForExistence(timeout: 15))
        XCTAssertTrue(app.otherElements["page.header"].exists)
        XCTAssertTrue(app.buttons["page.header.back"].exists)
        assertInsideHeader(field)
        XCTAssertEqual(field.value as? String, "周杰伦")
        attach("11-search-results")

        let songs = app.buttons["music.search.results.tab.1"]
        let playlists = app.buttons["music.search.results.tab.2"]
        songs.tap()
        waitSelected(songs)
        attach("13-search-songs")
        swipeSearchResults(app.tables["music.search.results.page.1"], left: true, to: playlists)
        attach("14-search-playlists")
        swipeSearchResults(app.tables["music.search.results.page.2"], left: false, to: songs)
        XCTAssertFalse(app.tables["music.search.discovery"].exists)
        swipeSearchResults(app.tables["music.search.results.page.1"], left: false,
                           to: app.buttons["music.search.results.tab.0"])
        attach("15-search-all-returned")
        dragRight(from: 0.25, to: 0.85)
        XCTAssertTrue(field.waitForExistence(timeout: 5))
        XCTAssertTrue(app.tables["music.search.discovery"].waitForExistence(timeout: 5))
        attach("12-search-returned")
        dragRight(from: 0.005, to: 0.8)
        XCTAssertTrue(app.buttons["search.open"].waitForExistence(timeout: 5))
    }

    func testLandscapeHeadersKeepControlsClearOfTheSensorHousing() {
        XCTAssertTrue(app.buttons["search.open"].waitForExistence(timeout: 30))
        app.buttons["search.open"].tap()
        let field = app.textFields["search.field"]
        XCTAssertTrue(field.waitForExistence(timeout: 5))
        for orientation in [UIDeviceOrientation.landscapeLeft, .landscapeRight] {
            XCUIDevice.shared.orientation = orientation
            let rotated = expectation(for: NSPredicate { _, _ in
                field.frame.width > 500
            }, evaluatedWith: nil)
            wait(for: [rotated], timeout: 5)
            let back = app.buttons["page.header.back"]
            let submit = app.buttons["search.submit"]
            assertInsideHeader(field)
            // Empty search is intentionally not submittable, but its geometry
            // must still stay clear of the landscape sensor and rounded corners.
            XCTAssertGreaterThanOrEqual(back.frame.minX, 48)
            XCTAssertLessThanOrEqual(submit.frame.maxX, app.frame.width - 48)
            XCTAssertEqual(field.frame.height, 40, accuracy: 1)
            attach(orientation == .landscapeLeft ? "16-search-landscape-left" : "17-search-landscape-right")
        }
        app.buttons["page.header.back"].tap()
        XCTAssertTrue(app.buttons["search.open"].waitForExistence(timeout: 5))
        app.buttons["music.miniPlayer.open"].tap()
        let player = app.otherElements["music.player.page"]
        let close = app.buttons["music.player.close"]
        XCTAssertTrue(player.waitForExistence(timeout: 5))
        for orientation in [UIDeviceOrientation.landscapeLeft, .landscapeRight] {
            XCUIDevice.shared.orientation = orientation
            let ready = expectation(for: NSPredicate { _, _ in
                close.isHittable && player.frame.width > player.frame.height &&
                    player.frame.contains(close.frame)
            }, evaluatedWith: nil)
            wait(for: [ready], timeout: 5)
            XCTAssertTrue(close.isHittable)
            XCTAssertGreaterThanOrEqual(close.frame.minX, 48)
            attach(orientation == .landscapeLeft ? "18-player-landscape-left" : "19-player-landscape-right")
        }
        close.tap()
        XCTAssertTrue(app.buttons["search.open"].waitForExistence(timeout: 5))
    }

    func testUserDataResetConfirmationCanBeCancelledWithoutLeavingSettings() {
        XCTAssertTrue(app.buttons["app.menu"].waitForExistence(timeout: 30))
        if app.alerts["错误"].exists { app.alerts["错误"].buttons["确定"].tap() }
        openSidebar()
        app.buttons["app.sidebar.settings"].tap()
        let clear = app.cells["settings.clearUserData"]
        if !clear.isHittable { app.swipeUp() }
        XCTAssertTrue(clear.waitForExistence(timeout: 5))
        clear.tap()
        let confirmation = app.alerts["清空本机数据？"]
        XCTAssertTrue(confirmation.waitForExistence(timeout: 5))
        XCTAssertTrue(confirmation.buttons["清除并等待重启"].exists)
        attach("settings-clear-user-data-confirmation")
        confirmation.buttons["取消"].tap()
        XCTAssertTrue(clear.waitForExistence(timeout: 5))
        XCTAssertTrue(clear.isEnabled)
        XCTAssertFalse(app.staticTexts["等待重新启动"].exists)
        XCTAssertFalse(confirmation.exists)
    }

    func testSidebarsShareStructureAndHomeFollowsPageBusiness() {
        openSidebar()
        let frames = assertSidebarStructure(controls: [
            "app.sidebar.home", "app.sidebar.channels", "app.sidebar.musicSource",
        ])
        attach("20-sidebar-music")
        app.buttons["app.sidebar.home"].tap()
        XCTAssertTrue(app.otherElements["app.sidebar"].waitForNonExistence(timeout: 5))
        XCTAssertTrue(app.otherElements["app.page.recommend"].waitForExistence(timeout: 5))

        for page in ["film", "library"] {
            app.buttons["app.primary." + page].tap()
            XCTAssertTrue(app.otherElements["app.page." + page].waitForExistence(timeout: 5))
            openSidebar()
            let controls = page == "film"
                ? ["app.sidebar.filmSource", "app.sidebar.home"]
                : ["app.sidebar.home", "app.sidebar.channels", "app.sidebar.musicSource"]
            assertSidebarStructure(controls: controls, matching: frames)
            attach("20-sidebar-" + page)
            if page == "film" {
                app.buttons["app.sidebar.filmSource"].tap()
                let douban = app.buttons["豆瓣"]
                XCTAssertTrue(douban.waitForExistence(timeout: 5))
                douban.tap()
                XCTAssertTrue(app.otherElements["app.sidebar"].exists)
            }
            revealSidebarElement(app.buttons["app.sidebar.home"])
            app.buttons["app.sidebar.home"].tap()
            XCTAssertTrue(app.otherElements["app.sidebar"].waitForNonExistence(timeout: 5))
            let home = page == "film" ? "film" : "recommend"
            XCTAssertTrue(app.otherElements["app.page." + home].waitForExistence(timeout: 5))
            assertPrimaryNavigation(selected: home)
            XCTAssertTrue(app.buttons["search.open"].isHittable)
            XCTAssertFalse(app.otherElements["app.page.library"].exists)

            if page == "film" {
                XCTAssertFalse(app.otherElements["app.page.recommend"].exists)
                openSidebar()
                revealSidebarElement(app.buttons["app.sidebar.settings"])
                app.buttons["app.sidebar.settings"].tap()
                XCTAssertTrue(app.otherElements["app.sidebar"].waitForNonExistence(timeout: 5))
                XCTAssertTrue(app.cells["settings.category.film"].waitForExistence(timeout: 5))
                assertInsideHeader(app.buttons["page.header.back"])
                XCTAssertFalse(app.buttons["app.menu"].exists)
                app.buttons["page.header.back"].tap()
                XCTAssertTrue(app.otherElements["app.page.film"].waitForExistence(timeout: 5))
                assertPrimaryNavigation(selected: "film")
            }
        }

        openChannels()
        openSidebar()
        assertSidebarStructure(controls: [
            "app.sidebar.home", "app.sidebar.channels", "app.sidebar.musicSource",
        ], matching: frames)
        attach("20-sidebar-channels")
        app.buttons["app.sidebar.home"].tap()
        XCTAssertTrue(app.otherElements["app.sidebar"].waitForNonExistence(timeout: 5))
        XCTAssertTrue(app.otherElements["app.page.recommend"].waitForExistence(timeout: 5))
        XCTAssertFalse(app.otherElements["app.page.channels"].exists)
        assertPrimaryNavigation(selected: "recommend")
    }

    func testMusicSourceSelectionStaysConsistentBetweenSidebarAndSettings() throws {
        let sources = ["网易云音乐", "酷狗音乐"]
        func displayedSource(_ element: XCUIElement) -> String? {
            let text = [element.label, element.value as? String ?? ""]
            let matches = sources.filter { source in text.contains { $0.contains(source) } }
            return matches.count == 1 ? matches[0] : nil
        }
        func waitForSource(_ source: String, on element: XCUIElement) {
            let updated = XCTNSPredicateExpectation(predicate: NSPredicate { _, _ in
                element.exists && element.isEnabled && displayedSource(element) == source
            }, object: nil)
            XCTAssertEqual(XCTWaiter.wait(for: [updated], timeout: 5), .completed)
            XCTAssertEqual(displayedSource(element), source)
        }
        func selectSource(_ source: String, on element: XCUIElement) {
            if displayedSource(element) == source { return }
            element.tap()
            let option = app.buttons[source]
            XCTAssertTrue(option.waitForExistence(timeout: 5))
            XCTAssertTrue(option.isEnabled)
            option.tap()
            waitForSource(source, on: element)
        }

        openSidebar()
        let shortcut = app.buttons["app.sidebar.musicSource"]
        XCTAssertTrue(shortcut.waitForExistence(timeout: 5))
        revealSidebarElement(shortcut)
        let ready = XCTNSPredicateExpectation(predicate: NSPredicate { _, _ in
            shortcut.isEnabled && displayedSource(shortcut) != nil
        }, object: nil)
        XCTAssertEqual(XCTWaiter.wait(for: [ready], timeout: 5), .completed)
        let original = try XCTUnwrap(displayedSource(shortcut))
        // Exercise the same write in each entry regardless of a previous run's selection.
        selectSource("网易云音乐", on: shortcut)
        selectSource("酷狗音乐", on: shortcut)
        attach("music-source-sidebar-selected")

        let settings = app.buttons["app.sidebar.settings"]
        revealSidebarElement(settings)
        settings.tap()
        XCTAssertTrue(app.otherElements["app.sidebar"].waitForNonExistence(timeout: 5))
        let music = app.cells["settings.category.music"]
        let reached = XCTNSPredicateExpectation(predicate: NSPredicate { _, _ in
            music.exists || self.app.alerts["错误"].exists
        }, object: nil)
        XCTAssertEqual(XCTWaiter.wait(for: [reached], timeout: 5), .completed)
        acknowledgeMusicContentFailureIfPresent()
        // Do not tap Settings again: the original navigation must have completed.
        XCTAssertTrue(music.waitForExistence(timeout: 5))
        music.tap()
        XCTAssertTrue(app.buttons["settings.search"].waitForExistence(timeout: 5))
        let picker = app.buttons["music.settings.catalogSource"]
        if !picker.isHittable { app.swipeUp() }
        XCTAssertTrue(picker.waitForExistence(timeout: 5))
        XCTAssertTrue(picker.isHittable)
        waitForSource("酷狗音乐", on: picker)
        attach("music-source-settings-selected")

        selectSource("网易云音乐", on: picker)
        app.buttons["page.header.back"].tap()
        XCTAssertTrue(music.waitForExistence(timeout: 5))
        app.buttons["page.header.back"].tap()
        XCTAssertTrue(app.otherElements["app.page.recommend"].waitForExistence(timeout: 5))

        acknowledgeMusicContentFailureIfPresent()
        openSidebar()
        XCTAssertTrue(shortcut.waitForExistence(timeout: 5))
        revealSidebarElement(shortcut)
        waitForSource("网易云音乐", on: shortcut)
        selectSource(original, on: shortcut)
        waitForSource(original, on: shortcut)
        attach("music-source-sidebar-restored")
        let home = app.buttons["app.sidebar.home"]
        revealSidebarElement(home)
        home.tap()
        XCTAssertTrue(app.otherElements["app.sidebar"].waitForNonExistence(timeout: 5))
        XCTAssertTrue(app.otherElements["app.page.recommend"].waitForExistence(timeout: 5))
    }

    func testSettingsPagesUseAppOwnedHeaders() throws {
        XCTAssertTrue(app.buttons["app.menu"].waitForExistence(timeout: 30))
        app.buttons["app.menu"].tap()
        XCTAssertTrue(app.otherElements["app.sidebar"].waitForExistence(timeout: 5))
        attach("20-sidebar")

        app.buttons["app.sidebar.settings"].tap()
        XCTAssertTrue(app.otherElements["page.header"].waitForExistence(timeout: 5))
        XCTAssertTrue(app.buttons["page.header.back"].waitForExistence(timeout: 5))
        XCTAssertTrue(app.staticTexts["设置"].exists)
        assertSettingsDock()
        attach("21-settings")
        XCTAssertTrue(app.cells["settings.category.film"].exists)
        app.cells["settings.category.music"].tap()
        XCTAssertTrue(app.buttons["settings.search"].waitForExistence(timeout: 5))
        assertSettingsDock()

        for (identifier, title) in [
            ("settings.search", "搜索设置"),
            ("music.settings.playback", "播放设置"),
            ("music.settings.metadata", "歌词／封面设置"),
            ("music.settings.aggregation", "平台账号"),
        ] {
            let destination = app.buttons[identifier]
            XCTAssertTrue(destination.isHittable)
            destination.tap()
            XCTAssertTrue(app.staticTexts[title].waitForExistence(timeout: 5))
            assertInsideHeader(app.buttons["page.header.back"])
            assertSettingsDock()
            attach("23-" + identifier)
            app.buttons["page.header.back"].tap()
            XCTAssertTrue(destination.waitForExistence(timeout: 5))
        }
        app.buttons["page.header.back"].tap()
        XCTAssertTrue(app.cells["settings.category.music"].waitForExistence(timeout: 5))
        app.cells["settings.preferences"].tap()
        XCTAssertTrue(app.staticTexts["偏好设置"].waitForExistence(timeout: 5))
        assertInsideHeader(app.buttons["page.header.back"])
        assertSettingsDock()
        app.buttons["settings.colors"].tap()
        XCTAssertTrue(app.staticTexts["配色设置"].waitForExistence(timeout: 5))
        assertInsideHeader(app.buttons["page.header.back"])
        attach("23-settings.colors")
        app.buttons["page.header.back"].tap()
        XCTAssertTrue(app.buttons["settings.colors"].waitForExistence(timeout: 5))
        app.buttons["page.header.back"].tap()
        XCTAssertTrue(app.cells["settings.category.music"].waitForExistence(timeout: 5))
        app.buttons["page.header.back"].tap()
        XCTAssertTrue(app.buttons["search.open"].waitForExistence(timeout: 5))
        attach("22-settings-returned")
    }

    func testInteractiveReturnCancellationAndCompletionKeepThePageUsable() {
        openSettings()
        dragRight(from: 0.25, to: 0.32)
        XCTAssertTrue(app.staticTexts["音乐设置"].exists)
        XCTAssertTrue(app.buttons["settings.search"].isHittable)
        assertInsideHeader(app.buttons["page.header.back"])
        attach("24-settings-cancelled-return")
        dragRight(from: 0.25, to: 0.85)
        XCTAssertTrue(app.cells["settings.category.music"].waitForExistence(timeout: 5))
        dragRight(from: 0.25, to: 0.85)
        XCTAssertTrue(app.buttons["search.open"].waitForExistence(timeout: 5))
        attach("25-settings-content-return")
        openSettings()
        dragRight(from: 0.005, to: 0.8)
        XCTAssertTrue(app.cells["settings.category.music"].waitForExistence(timeout: 5))
        dragRight(from: 0.005, to: 0.8)
        XCTAssertTrue(app.buttons["search.open"].waitForExistence(timeout: 5))
        attach("26-settings-edge-return")
    }

    func testFilmSettingsThreePagesAndDraftReturnUseIndependentCategory() {
        XCTAssertTrue(app.buttons["app.menu"].waitForExistence(timeout: 30))
        app.buttons["app.menu"].tap()
        app.buttons["app.sidebar.settings"].tap()
        XCTAssertTrue(app.cells["settings.category.film"].waitForExistence(timeout: 5))
        app.cells["settings.category.film"].tap()
        XCTAssertTrue(app.cells["film.settings.search"].waitForExistence(timeout: 5))
        attach("film-settings-category")
        for (identifier, title) in [
            ("film.settings.search", "搜索净化设置"),
            ("film.settings.cats", "猫源"),
            ("film.settings.videos", "视频源"),
        ] {
            app.cells[identifier].tap()
            XCTAssertTrue(app.staticTexts[title].waitForExistence(timeout: 5))
            assertInsideHeader(app.buttons["page.header.back"])
            attach(identifier)
            app.buttons["page.header.back"].tap()
            XCTAssertTrue(app.cells[identifier].waitForExistence(timeout: 5))
        }
        app.cells["film.settings.matching"].tap()
        XCTAssertTrue(app.cells["film.matching.episodeClean"].waitForExistence(timeout: 5))
        app.cells["film.matching.episodeClean"].tap()
        let pattern = app.textViews["film.rules.episodeClean.0.pattern"]
        XCTAssertTrue(pattern.waitForExistence(timeout: 5))
        pattern.tap()
        pattern.typeText("UI草稿")
        let draft = pattern.value as? String
        attach("film-rule-draft")
        app.buttons["page.header.back"].tap()
        XCTAssertTrue(app.cells["film.matching.movie"].waitForExistence(timeout: 5))
        app.cells["film.matching.movie"].tap()
        XCTAssertTrue(app.textViews["film.rules.movie.0.pattern"].waitForExistence(timeout: 5))
        app.buttons["page.header.back"].tap()
        // Search is a separate direct rule page, outside the matching submenu.
        app.buttons["page.header.back"].tap()
        XCTAssertTrue(app.cells["film.settings.search"].waitForExistence(timeout: 5))
        app.cells["film.settings.search"].tap()
        XCTAssertTrue(app.textViews["film.rules.search.0.pattern"].waitForExistence(timeout: 5))
        app.buttons["page.header.back"].tap()
        app.cells["film.settings.matching"].tap()
        app.cells["film.matching.episodeClean"].tap()
        XCTAssertTrue(pattern.waitForExistence(timeout: 5))
        XCTAssertEqual(pattern.value as? String, draft)
        attach("film-rule-draft-returned")
        // Exit all settings layers; the originating primary page remains in place.
        for _ in 0..<4 { app.buttons["page.header.back"].tap() }
        XCTAssertTrue(app.buttons["search.open"].waitForExistence(timeout: 5))
    }

    func testLocalCollectionSearchAndEditorStayBelowTheHeader() {
        XCTAssertTrue(app.buttons["app.primary.library"].waitForExistence(timeout: 30))
        app.buttons["app.primary.library"].tap()
        let name = "页头验收"
        deleteCollections(named: name)
        app.buttons["app.primary.add"].tap()
        let create = app.alerts["新建歌单"]
        XCTAssertTrue(create.waitForExistence(timeout: 5))
        attach("29-collection-create-confirmation")
        create.textFields.firstMatch.typeText(name)
        create.buttons["创建"].tap()
        let collection = app.staticTexts[name].firstMatch
        XCTAssertTrue(collection.waitForExistence(timeout: 10))
        collection.tap()
        XCTAssertTrue(app.buttons["music.collection.search"].waitForExistence(timeout: 5))
        assertInsideHeader(app.buttons["page.header.back"])
        attach("30-collection")
        app.buttons["music.collection.search"].tap()
        let search = app.textFields["music.collection.searchField"]
        XCTAssertTrue(search.waitForExistence(timeout: 5))
        search.tap()
        search.typeText("验收")
        attach("31-collection-search")
        app.buttons["music.collection.search"].tap()
        let edit = app.buttons["music.collection.edit"]
        XCTAssertTrue(edit.isHittable)
        edit.tap()
        let toolbar = app.otherElements["music.collection.editor.toolbar"]
        XCTAssertTrue(toolbar.waitForExistence(timeout: 5))
        let header = app.otherElements["page.header"]
        XCTAssertGreaterThanOrEqual(toolbar.frame.minY, header.frame.maxY - 1)
        assertInsideHeader(app.buttons["page.header.back"])
        XCTAssertTrue(app.buttons["music.collection.editor.done"].isHittable)
        attach("32-collection-editor")
        app.buttons["page.header.back"].tap()
        XCTAssertTrue(edit.waitForExistence(timeout: 5))
        app.buttons["music.collection.delete"].tap()
        let delete = app.alerts.firstMatch
        XCTAssertTrue(delete.waitForExistence(timeout: 5))
        delete.buttons["删除"].tap()
        XCTAssertTrue(app.buttons["app.primary.add"].waitForExistence(timeout: 5))
    }

    func testPlayerAndQueueRetainTheirIndependentPresentation() {
        let open = app.buttons["music.miniPlayer.open"]
        XCTAssertTrue(open.waitForExistence(timeout: 30))
        open.tap()
        let close = app.buttons["music.player.close"]
        XCTAssertTrue(close.waitForExistence(timeout: 5))
        let player = app.otherElements["music.player.page"]
        XCTAssertTrue(player.waitForExistence(timeout: 5))
        XCTAssertFalse(player.otherElements["page.header"].exists)
        XCTAssertTrue(close.isHittable)
        attach("40-player")
        app.buttons["music.player.lyricToggle"].tap()
        attach("41-player-lyrics")
        close.tap()
        XCTAssertTrue(open.waitForExistence(timeout: 5))
        open.tap()
        XCTAssertTrue(close.waitForExistence(timeout: 5))
        dragRight(from: 0.005, to: 0.07)
        XCTAssertTrue(close.isHittable)
        attach("42-player-cancelled-dismissal")
        dragRight(from: 0.005, to: 0.8)
        XCTAssertTrue(open.waitForExistence(timeout: 5))
        XCTAssertFalse(close.exists)
        attach("42-player-edge-dismissed")
        app.buttons["music.miniPlayer.queue"].tap()
        let queue = app.otherElements["music.queue.sheet"]
        XCTAssertTrue(queue.waitForExistence(timeout: 5))
        attach("43-queue")
        app.buttons["music.queue.tab.1"].tap()
        attach("44-queue-library")
        app.buttons["关闭播放队列"].tap()
        XCTAssertTrue(app.buttons["search.open"].waitForExistence(timeout: 5))
    }

    func testDockStaysAtTheBottomWhileTheKeyboardIsShown() {
        XCTAssertTrue(app.buttons["search.open"].waitForExistence(timeout: 30))
        app.buttons["search.open"].tap()
        let field = app.textFields["search.field"]
        XCTAssertTrue(field.waitForExistence(timeout: 5))
        let keyboard = app.keyboards.firstMatch
        XCTAssertTrue(keyboard.waitForExistence(timeout: 5))
        let capsule = app.buttons["music.miniPlayer.open"]
        XCTAssertTrue(capsule.exists)
        let raised = capsule.frame
        XCTAssertGreaterThanOrEqual(raised.minY, keyboard.frame.minY - 1, "The dock rose with the keyboard")
        attach("50-search-keyboard-dock")
        app.tables["music.search.discovery"].swipeUp()
        XCTAssertTrue(keyboard.waitForNonExistence(timeout: 5))
        XCTAssertEqual(capsule.frame.minY, raised.minY, accuracy: 1)
        attach("51-search-dock-after-keyboard")
        app.buttons["page.header.back"].tap()
        XCTAssertTrue(app.buttons["search.open"].waitForExistence(timeout: 5))
    }

    /// Records the dock backdrop on each page for visual review; the Simulator
    /// cannot assert the gradient blend, so this test only checks navigation.
    func testRecordsDockBackdropAcrossPages() {
        XCTAssertTrue(app.buttons["search.open"].waitForExistence(timeout: 30))
        let content = app.collectionViews.firstMatch
        for _ in 0..<4 { content.swipeUp() }
        let start = app.coordinate(withNormalizedOffset: CGVector(dx: 0.5, dy: 0.35))
        start.press(forDuration: 0.1, thenDragTo: app.coordinate(withNormalizedOffset: CGVector(dx: 0.5, dy: 0.55)),
                    withVelocity: .slow, thenHoldForDuration: 0.3)
        attach("52-home-faded-dock")
        openChannels()
        XCTAssertTrue(app.buttons["app.channels"].waitForExistence(timeout: 5))
        attach("53-channels-opaque-dock")
        app.buttons["app.primary.library"].tap()
        attach("54-library-opaque-dock")
        app.buttons["app.primary.recommend"].tap()
        XCTAssertTrue(app.buttons["search.open"].waitForExistence(timeout: 5))
        app.buttons["search.open"].tap()
        XCTAssertTrue(app.textFields["search.field"].waitForExistence(timeout: 5))
        app.tables["music.search.discovery"].swipeUp()
        attach("55-search-opaque-dock")
        app.buttons["page.header.back"].tap()
        XCTAssertTrue(app.buttons["search.open"].waitForExistence(timeout: 5))
    }

    /// A failed earlier run can leave its acceptance playlist behind. Remove it
    /// first so the test always opens the collection it creates.
    private func deleteCollections(named name: String) {
        let leftover = app.staticTexts[name].firstMatch
        while leftover.waitForExistence(timeout: 5) {
            leftover.tap()
            let delete = app.buttons["music.collection.delete"]
            XCTAssertTrue(delete.waitForExistence(timeout: 5))
            delete.tap()
            let alert = app.alerts.firstMatch
            XCTAssertTrue(alert.waitForExistence(timeout: 5))
            alert.buttons["删除"].tap()
            XCTAssertTrue(app.buttons["app.primary.add"].waitForExistence(timeout: 5))
        }
    }

    private func openSettings() {
        XCTAssertTrue(app.buttons["app.menu"].waitForExistence(timeout: 30))
        app.buttons["app.menu"].tap()
        XCTAssertTrue(app.buttons["app.sidebar.settings"].waitForExistence(timeout: 5))
        app.buttons["app.sidebar.settings"].tap()
        XCTAssertTrue(app.cells["settings.category.music"].waitForExistence(timeout: 5))
        app.cells["settings.category.music"].tap()
        XCTAssertTrue(app.buttons["settings.search"].waitForExistence(timeout: 5))
    }

    private func openSidebar() {
        XCTAssertTrue(app.buttons["app.menu"].waitForExistence(timeout: 30))
        app.buttons["app.menu"].tap()
        XCTAssertTrue(app.otherElements["app.sidebar"].waitForExistence(timeout: 5))
    }

    private func openChannels() {
        openSidebar()
        let channels = app.buttons["app.sidebar.channels"]
        XCTAssertTrue(channels.isHittable)
        channels.tap()
        XCTAssertTrue(app.otherElements["app.sidebar"].waitForNonExistence(timeout: 5))
        XCTAssertTrue(app.otherElements["app.page.channels"].waitForExistence(timeout: 5))
        XCTAssertTrue(app.buttons["app.channels"].isHittable)
        waitSelected(app.buttons["app.primary.recommend"])
    }

    private func assertPrimaryNavigation(selected: String,
                                         file: StaticString = #filePath, line: UInt = #line) {
        let buttons = app.otherElements["app.dock"].buttons.matching(NSPredicate(
            format: "identifier BEGINSWITH %@", "app.primary."
        )).allElementsBoundByIndex.sorted { $0.frame.minX < $1.frame.minX }
        XCTAssertEqual(buttons.map(\.identifier), [
            "app.primary.recommend", "app.primary.film", "app.primary.novel", "app.primary.library",
        ], file: file, line: line)
        XCTAssertEqual(buttons.map(\.label), ["首页", "影视", "小说", "我的"], file: file, line: line)
        waitSelected(app.buttons["app.primary." + selected])
        for button in buttons {
            if !button.isHittable { acknowledgeMusicContentFailureIfPresent(file: file, line: line) }
            XCTAssertTrue(button.isHittable, file: file, line: line)
            XCTAssertEqual(button.isSelected, button.identifier == "app.primary." + selected,
                           file: file, line: line)
        }
    }

    private func assertSettingsDock(file: StaticString = #filePath, line: UInt = #line) {
        XCTAssertFalse(app.buttons["music.miniPlayer.open"].isHittable,
                       "The capsule player must be hidden throughout settings",
                       file: file, line: line)
    }

    private func acknowledgeMusicContentFailureIfPresent(file: StaticString = #filePath, line: UInt = #line) {
        let alert = app.alerts["错误"]
        guard alert.exists else { return }
        // Remote content loading is independent of navigation and source persistence.
        // Record only that known failure, dismiss once, then keep every route/hit-test assertion.
        XCTAssertTrue(alert.staticTexts.matching(NSPredicate(
            format: "label CONTAINS %@", "音乐内容读取"
        )).firstMatch.exists, "Unexpected error must not be dismissed", file: file, line: line)
        attach("music-content-error")
        let details = XCTAttachment(string: alert.debugDescription)
        details.name = "music-content-error-details"
        details.lifetime = .keepAlways
        add(details)
        let cancel = alert.buttons["取消"]
        XCTAssertTrue(cancel.isHittable, file: file, line: line)
        cancel.tap()
        XCTAssertTrue(alert.waitForNonExistence(timeout: 5), file: file, line: line)
    }

    @discardableResult
    private func assertSidebarStructure(controls: [String], matching expectedFrames: [CGRect]? = nil,
                                         file: StaticString = #filePath, line: UInt = #line) -> [CGRect] {
        let sidebar = app.otherElements["app.sidebar"]
        let icon = sidebar.descendants(matching: .any)["app.sidebar.icon"]
        let card = sidebar.otherElements["app.sidebar.card"]
        let settings = sidebar.buttons["app.sidebar.settings"]
        for element in [icon, card, settings] {
            XCTAssertTrue(element.exists, file: file, line: line)
            XCTAssertGreaterThan(element.frame.width, 0, file: file, line: line)
            XCTAssertGreaterThan(element.frame.height, 0, file: file, line: line)
        }
        XCTAssertTrue(sidebar.frame.contains(icon.frame), file: file, line: line)
        XCTAssertEqual(settings.label, "设置", file: file, line: line)
        XCTAssertEqual(sidebar.buttons["app.sidebar.home"].label, "首页", file: file, line: line)
        XCTAssertEqual(icon.frame.midX, card.frame.midX, accuracy: 1, file: file, line: line)
        XCTAssertLessThan(icon.frame.maxY, card.frame.minY, file: file, line: line)
        XCTAssertLessThan(card.frame.maxY, settings.frame.minY, file: file, line: line)
        XCTAssertEqual(card.frame.minX, settings.frame.minX, accuracy: 1, file: file, line: line)
        XCTAssertEqual(card.frame.maxX, settings.frame.maxX, accuracy: 1, file: file, line: line)
        XCTAssertEqual(card.descendants(matching: .button).allElementsBoundByIndex.map(\.identifier).sorted(),
                       controls.sorted(), file: file, line: line)
        let rows = controls.map { card.buttons[$0] }
        for row in rows {
            XCTAssertTrue(row.exists, file: file, line: line)
            XCTAssertTrue(card.frame.contains(row.frame), file: file, line: line)
        }
        for (upper, lower) in zip(rows, rows.dropFirst()) {
            XCTAssertLessThanOrEqual(upper.frame.maxY, lower.frame.minY, file: file, line: line)
        }
        let frames = [icon.frame, card.frame, settings.frame]
        if let expectedFrames {
            // Content can grow vertically; all domains keep the same horizontal frame.
            for (actual, expected) in zip(frames, expectedFrames) {
                XCTAssertEqual(actual.minX, expected.minX, accuracy: 1, file: file, line: line)
                XCTAssertEqual(actual.width, expected.width, accuracy: 1, file: file, line: line)
            }
            XCTAssertEqual(icon.frame.minY, expectedFrames[0].minY, accuracy: 1, file: file, line: line)
            XCTAssertEqual(card.frame.minY, expectedFrames[1].minY, accuracy: 1, file: file, line: line)
        }
        for row in rows { revealSidebarElement(row, file: file, line: line) }
        revealSidebarElement(settings, file: file, line: line)
        revealSidebarElement(rows[0], file: file, line: line)
        return frames
    }

    private func revealSidebarElement(_ element: XCUIElement,
                                      file: StaticString = #filePath, line: UInt = #line) {
        let scroll = app.otherElements["app.sidebar"].scrollViews.firstMatch
        while !element.isHittable {
            let before = element.frame
            if before.midY > scroll.frame.midY { scroll.swipeUp() } else { scroll.swipeDown() }
            if abs(element.frame.minY - before.minY) < 1 { break }
        }
        XCTAssertTrue(element.isHittable, file: file, line: line)
    }

    private func waitSelected(_ button: XCUIElement) {
        let selected = expectation(for: NSPredicate(format: "selected == true"), evaluatedWith: button)
        wait(for: [selected], timeout: 5)
    }

    private func swipeSearchResults(_ table: XCUIElement, left: Bool, to tab: XCUIElement) {
        let alert = app.alerts["错误"]
        // The two independently loaded result sections can each show a native
        // service failure. Record and acknowledge it, then test the actual swipe;
        // no retry is allowed when a gesture fails without an intervening alert.
        for _ in 0..<3 {
            if alert.exists { dismissSearchFailure(alert) }
            if tab.isSelected { break }
            if left { table.swipeLeft() } else { table.swipeRight() }
            let settled = XCTNSPredicateExpectation(predicate: NSPredicate { _, _ in
                tab.isSelected || alert.exists
            }, object: nil)
            XCTAssertEqual(XCTWaiter.wait(for: [settled], timeout: 5), .completed)
            if tab.isSelected { break }
            XCTAssertTrue(alert.exists, "A swipe did not select its neighboring category")
        }
        if alert.exists { dismissSearchFailure(alert) }
        XCTAssertTrue(tab.isSelected, "Search remained blocked after acknowledging its section failures")
    }

    private func dismissSearchFailure(_ alert: XCUIElement) {
        attach("search-platform-error")
        let cancel = alert.buttons["取消"]
        XCTAssertTrue(cancel.isHittable)
        cancel.tap()
        let dismissed = expectation(for: NSPredicate(format: "exists == false"), evaluatedWith: alert)
        wait(for: [dismissed], timeout: 5)
    }

    private func assertInsideHeader(_ control: XCUIElement,
                                    file: StaticString = #filePath, line: UInt = #line) {
        let header = app.otherElements["page.header"]
        XCTAssertTrue(control.isHittable, file: file, line: line)
        XCTAssertGreaterThan(control.frame.width, 0, file: file, line: line)
        XCTAssertGreaterThan(control.frame.height, 0, file: file, line: line)
        XCTAssertTrue(header.frame.insetBy(dx: -1, dy: -1).contains(control.frame),
                      file: file, line: line)
    }

    private func assertHeaderOrder(_ controls: [XCUIElement],
                                   file: StaticString = #filePath, line: UInt = #line) {
        for control in controls { assertInsideHeader(control, file: file, line: line) }
        for (left, right) in zip(controls, controls.dropFirst()) {
            XCTAssertLessThanOrEqual(left.frame.maxX, right.frame.minX + 1, file: file, line: line)
        }
    }

    /// The primary pages are separate pages: the only visible header and its
    /// controls belong to the shown page, not to a header shared by the shell.
    private func assertPageOwnsHeader(_ page: String, controls: [String],
                                      file: StaticString = #filePath, line: UInt = #line) {
        let root = app.otherElements["app.page." + page]
        XCTAssertTrue(root.exists, file: file, line: line)
        XCTAssertEqual(app.otherElements.matching(identifier: "page.header").count, 1, file: file, line: line)
        let header = root.descendants(matching: .other).matching(identifier: "page.header")
        XCTAssertEqual(header.count, 1, file: file, line: line)
        for control in controls {
            XCTAssertTrue(header.firstMatch.descendants(matching: .any)[control].exists,
                          "\(control) belongs to the \(page) header", file: file, line: line)
        }
        let buttons = header.firstMatch.descendants(matching: .button).allElementsBoundByIndex
            .filter { !$0.identifier.hasPrefix("music.channel.tab.") }
        XCTAssertEqual(buttons.map(\.identifier).sorted(),
                       controls.filter { $0 != "music.channel.switcher" }.sorted(), file: file, line: line)
    }

    private func swipeChannelWhitespace(left: Bool) {
        let collection = app.collectionViews.firstMatch
        XCTAssertTrue(collection.waitForExistence(timeout: 10))
        let y = collection.frame.minY + 2
        let origin = app.coordinate(withNormalizedOffset: .zero).withOffset(
            CGVector(dx: app.frame.width * (left ? 0.75 : 0.25), dy: y)
        )
        let target = app.coordinate(withNormalizedOffset: .zero).withOffset(
            CGVector(dx: app.frame.width * (left ? 0.25 : 0.75), dy: y)
        )
        origin.press(forDuration: 0.1, thenDragTo: target, withVelocity: .fast, thenHoldForDuration: 0.2)
    }

    /// The topmost visible row whose cards run past its trailing inset scrolls sideways.
    private func horizontalRowMidY() throws -> CGFloat {
        let items = app.descendants(matching: .any).matching(NSPredicate(
            format: "identifier BEGINSWITH %@", "music.browse.item."
        ))
        XCTAssertTrue(items.firstMatch.waitForExistence(timeout: 30))
        let cards = items.allElementsBoundByIndex.map(\.frame)
        let width = app.frame.width, bottom = app.frame.height * 0.75
        let visible = cards.filter { $0.minX >= 0 && $0.minX < width && $0.maxY < bottom }
        let rows = Dictionary(grouping: visible) { $0.midY.rounded() }
        let scrolling = rows.filter { _, row in
            guard row.count > 1, let leading = row.map(\.minX).min() else { return false }
            return row.contains { $0.maxX > width - leading + 1 }
        }
        return try XCTUnwrap(scrolling.keys.min(), "No visible row runs past its trailing inset")
    }

    private func dragRight(from start: CGFloat, to end: CGFloat, y: CGFloat? = nil) {
        let dy = y.map { $0 / app.frame.height } ?? 0.5
        let origin = app.coordinate(withNormalizedOffset: CGVector(dx: start, dy: dy))
        let target = app.coordinate(withNormalizedOffset: CGVector(dx: end, dy: dy))
        origin.press(forDuration: 0.1, thenDragTo: target, withVelocity: .slow, thenHoldForDuration: 0.2)
    }

    private func attach(_ name: String) {
        let attachment = XCTAttachment(screenshot: XCUIScreen.main.screenshot())
        attachment.name = name
        attachment.lifetime = .keepAlways
        add(attachment)
    }
}
