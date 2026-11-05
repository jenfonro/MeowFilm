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
        waitSelected(app.buttons["app.header.recommend"])
        assertHeaderOrder([
            app.buttons["app.menu"], app.buttons["app.header.recommend"],
            app.buttons["app.header.channels"], app.buttons["search.open"], app.buttons["app.feature"],
        ])
        assertPageOwnsHeader("recommend", controls: [
            "app.menu", "app.header.recommend", "app.header.channels", "search.open", "app.feature",
        ])
        attach("01-home")

        app.buttons["app.primary.channels"].tap()
        let channelMenu = app.buttons["app.channels"]
        let switcher = app.otherElements["music.channel.switcher"]
        XCTAssertTrue(channelMenu.waitForExistence(timeout: 5))
        XCTAssertTrue(switcher.waitForExistence(timeout: 5))
        assertInsideHeader(channelMenu)
        assertInsideHeader(switcher)
        waitSelected(app.buttons["app.header.channels"])
        assertHeaderOrder([
            app.buttons["app.menu"], app.buttons["app.header.recommend"],
            app.buttons["app.header.channels"], switcher, channelMenu, app.buttons["search.open"],
        ])
        assertPageOwnsHeader("channels", controls: [
            "app.menu", "app.header.recommend", "app.header.channels", "music.channel.switcher",
            "app.channels", "search.open",
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
        assertPageOwnsHeader("library", controls: ["app.menu", "app.primary.title", "app.primary.add"])
        XCTAssertFalse(app.otherElements["app.page.channels"].exists)
        attach("03-library")

        app.buttons["app.primary.recommend"].tap()
        XCTAssertTrue(app.buttons["search.open"].waitForExistence(timeout: 5))
        assertPageOwnsHeader("recommend", controls: ["app.menu", "search.open", "app.feature"])
        XCTAssertFalse(app.otherElements["app.page.library"].exists)
        app.collectionViews.firstMatch.swipeUp()
        attach("04-home-scrolled")
        assertInsideHeader(app.buttons["search.open"])
        app.collectionViews.firstMatch.swipeDown()
        attach("05-home-restored")
    }

    func testHeaderPageSwitchingAndChannelSearchPreserveSelection() {
        let recommend = app.buttons["app.header.recommend"]
        let channels = app.buttons["app.header.channels"]
        XCTAssertTrue(channels.waitForExistence(timeout: 30))
        channels.tap()
        waitSelected(channels)
        waitSelected(app.buttons["app.primary.channels"])
        XCTAssertTrue(app.buttons["app.channels"].isHittable)
        let search = app.buttons["search.open"]
        assertInsideHeader(search)
        XCTAssertEqual(search.label, "搜索")
        XCTAssertEqual(search.frame.width, search.frame.height, accuracy: 1)
        attach("06-channel-search")
        search.tap()
        XCTAssertTrue(app.textFields["search.field"].waitForExistence(timeout: 5))
        app.buttons["page.header.back"].tap()
        XCTAssertTrue(channels.waitForExistence(timeout: 5))
        waitSelected(channels)
        waitSelected(app.buttons["app.primary.channels"])
        recommend.tap()
        waitSelected(recommend)
        waitSelected(app.buttons["app.primary.recommend"])
        XCTAssertTrue(app.buttons["app.feature"].isHittable)
        channels.tap()
        waitSelected(channels)
        app.buttons["app.primary.recommend"].tap()
        waitSelected(recommend)
    }

    func testChannelContentPagingAndEdgeReturnHaveSeparateGestures() throws {
        let channels = app.buttons["app.header.channels"]
        XCTAssertTrue(channels.waitForExistence(timeout: 30))
        channels.tap()
        waitSelected(channels)
        let tabs = app.buttons.matching(NSPredicate(
            format: "identifier BEGINSWITH %@", "music.channel.tab."
        ))
        let selected = tabs.matching(NSPredicate(format: "selected == true")).firstMatch
        XCTAssertTrue(selected.waitForExistence(timeout: 30))
        let originalID = selected.identifier
        let directory = tabs.allElementsBoundByIndex
        let index = try XCTUnwrap(directory.firstIndex { $0.identifier == originalID })
        XCTAssertGreaterThan(directory.count, 1)
        let nextIndex = index + 1 < directory.count ? index + 1 : index - 1
        let next = directory[nextIndex]
        swipeChannelWhitespace(left: nextIndex > index)
        waitSelected(next)
        waitSelected(channels)
        swipeChannelWhitespace(left: nextIndex < index)
        waitSelected(app.buttons[originalID])

        dragRight(from: 0.005, to: 0.04)
        waitSelected(channels)
        waitSelected(app.buttons[originalID])
        attach("07-channel-edge-cancelled")
        dragRight(from: 0.005, to: 0.8)
        waitSelected(app.buttons["app.header.recommend"])
        waitSelected(app.buttons["app.primary.recommend"])
        attach("08-channel-edge-returned")
        channels.tap()
        waitSelected(channels)
        waitSelected(app.buttons[originalID])

        let rowY = try horizontalRowMidY()
        dragRight(from: 0.005, to: 0.8, y: rowY)
        waitSelected(app.buttons["app.header.recommend"])
        waitSelected(app.buttons["app.primary.recommend"])
        attach("09-channel-row-edge-returned")
        channels.tap()
        waitSelected(channels)
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

    func testSettingsPagesUseAppOwnedHeaders() throws {
        XCTAssertTrue(app.buttons["app.menu"].waitForExistence(timeout: 30))
        app.buttons["app.menu"].tap()
        XCTAssertTrue(app.otherElements["app.sidebar"].waitForExistence(timeout: 5))
        attach("20-sidebar")

        app.buttons["app.sidebar.settings"].tap()
        XCTAssertTrue(app.otherElements["page.header"].waitForExistence(timeout: 5))
        XCTAssertTrue(app.buttons["page.header.back"].waitForExistence(timeout: 5))
        XCTAssertTrue(app.staticTexts["设置"].exists)
        attach("21-settings")

        for (identifier, title) in [
            ("settings.search", "搜索设置"),
            ("music.settings.playback", "播放设置"),
            ("music.settings.metadata", "歌词／封面设置"),
            ("settings.preferences", "偏好设置"),
            ("music.settings.aggregation", "平台账号"),
        ] {
            let destination = app.buttons[identifier]
            XCTAssertTrue(destination.isHittable)
            destination.tap()
            XCTAssertTrue(app.staticTexts[title].waitForExistence(timeout: 5))
            assertInsideHeader(app.buttons["page.header.back"])
            attach("23-" + identifier)
            app.buttons["page.header.back"].tap()
            XCTAssertTrue(destination.waitForExistence(timeout: 5))
        }
        app.buttons["page.header.back"].tap()
        XCTAssertTrue(app.buttons["search.open"].waitForExistence(timeout: 5))
        attach("22-settings-returned")
    }

    func testInteractiveReturnCancellationAndCompletionKeepThePageUsable() {
        openSettings()
        dragRight(from: 0.25, to: 0.32)
        XCTAssertTrue(app.staticTexts["设置"].exists)
        XCTAssertTrue(app.buttons["settings.search"].isHittable)
        assertInsideHeader(app.buttons["page.header.back"])
        attach("24-settings-cancelled-return")
        dragRight(from: 0.25, to: 0.85)
        XCTAssertTrue(app.buttons["search.open"].waitForExistence(timeout: 5))
        attach("25-settings-content-return")
        openSettings()
        dragRight(from: 0.005, to: 0.8)
        XCTAssertTrue(app.buttons["search.open"].waitForExistence(timeout: 5))
        attach("26-settings-edge-return")
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
        app.buttons["app.primary.channels"].tap()
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
        XCTAssertTrue(app.buttons["settings.search"].waitForExistence(timeout: 5))
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
        var cards: [CGRect] = []
        func collect(_ element: XCUIElementSnapshot) {
            if element.identifier.hasPrefix("music.browse.item.") { cards.append(element.frame) }
            element.children.forEach(collect)
        }
        let screen = try app.snapshot()
        collect(screen)
        let width = screen.frame.width, bottom = screen.frame.height * 0.75
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
