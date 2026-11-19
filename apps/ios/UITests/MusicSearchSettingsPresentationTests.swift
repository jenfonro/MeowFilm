#if os(iOS)
import XCTest
import UIKit
import MeowStorage
@testable import MeowMusicFeature
@testable import MeowUI

@MainActor
final class MusicSearchSettingsPresentationTests: XCTestCase {
    private var window: UIWindow?
    private weak var previousKeyWindow: UIWindow?

    func testSearchSettingsGroupsKeepTheRequestedOrderAndExistingFields() throws {
        try withPreferences { defaults, store, appearance in
            let page = MusicSearchSettingsViewController(appearance: appearance, store: store)
            try withMountedPage(page) { runner in
                let list = try searchList(in: page)
                XCTAssertEqual(list.numberOfSections, 4)
                XCTAssertEqual((0..<4).map { list.numberOfItems(inSection: $0) }, [1, 2, 3, 3])
                let ordinaryHeight = try XCTUnwrap(list.layoutAttributesForItem(
                    at: IndexPath(item: 0, section: 1)
                )).size.height
                XCTAssertEqual(try XCTUnwrap(list.layoutAttributesForItem(
                    at: IndexPath(item: 0, section: 0)
                )).size.height, ordinaryHeight, accuracy: 1)
                let toggle = runner.switches["music.search.searchPlaylists"]
                let songs = runner.buttons["music.search.songs.platforms"]
                let playlists = runner.buttons["music.search.playlists.platforms"]
                XCTAssertTrue(toggle.waitForExistence(timeout: 3))
                XCTAssertEqual(toggle.label, "搜索歌曲同时搜索歌单")
                XCTAssertEqual(songs.label, "歌曲搜索平台设置")
                XCTAssertEqual(playlists.label, "歌单搜索平台设置")
                XCTAssertLessThan(toggle.frame.maxY, songs.frame.minY)
                XCTAssertLessThan(songs.frame.minY, playlists.frame.minY)
                attach(page.view, name: "Music search settings 390")
                perform { toggle.coordinate(withNormalizedOffset: CGVector(dx: 0.9, dy: 0.5)).tap() }
                waitFor("simultaneous playlists saved") { !store.preferences.searchPlaylists }
                XCTAssertFalse(try saved(defaults).searchPlaylists)
                for (section, kind) in [(2, "songs"), (3, "playlists")] {
                    showRow(0, section: section, in: list)
                    let header = runner.staticTexts["music.search.\(kind).advanced"]
                    XCTAssertTrue(header.waitForExistence(timeout: 3))
                    XCTAssertEqual(header.label, kind == "songs" ? "歌曲搜索高级设置" : "歌单搜索高级设置")
                    for (row, name) in ["pages", "concurrency", "cacheMinutes"].enumerated() {
                        showRow(row, section: section, in: list)
                        XCTAssertTrue(runner.staticTexts["music.search.\(kind).\(name).label"].exists)
                        XCTAssertTrue(runner.staticTexts["music.search.\(kind).\(name).value"].exists)
                        XCTAssertEqual(try XCTUnwrap(list.cellForItem(
                            at: IndexPath(item: row, section: section)
                        )).bounds.height, ordinaryHeight, accuracy: 1)
                    }
                }
            }
        }
    }

    func testBothPlatformWindowsKeepDisabledRowsAndPersistIndependentPriority() throws {
        let initial = MusicSearchPreferences(songPlatforms: [.qq, .netease, .kugou],
                                             playlistPlatforms: [.kuwo, .migu])
        try withPreferences(initial) { defaults, store, appearance in
            let page = MusicSearchSettingsViewController(appearance: appearance, store: store)
            try withMountedPage(page) { runner in
                perform { runner.buttons["music.search.songs.platforms"].tap() }
                let songs = try platformWindow(on: page, kind: .songs)
                let songTable = try platformTable(in: songs)
                XCTAssertEqual(songs.modalPresentationStyle, .overFullScreen)
                XCTAssertTrue(songs.view is AccountFloatingBackdrop)
                XCTAssertEqual(songTable.numberOfRows(inSection: 0), MusicSearchPreferences.platforms.count)
                XCTAssertTrue(songTable.isEditing)
                XCTAssertGreaterThan(songTable.bounds.height, 0)
                XCTAssertEqual(songTable.convert(songTable.bounds, to: songs.view).midX,
                               songs.view.bounds.midX, accuracy: 1)
                assertLeadingReorderControl(in: songTable)
                attach(try XCTUnwrap(songs.view.window), name: "Music search song platforms 390")
                let songQQ = runner.switches["music.search.songs.platform.tx"]
                XCTAssertTrue(songQQ.waitForExistence(timeout: 3))
                perform { songQQ.tap() }
                waitFor("song platform disabled") { store.preferences.songPlatforms == [.netease, .kugou] }
                XCTAssertEqual(try saved(defaults).playlistPlatforms, initial.playlistPlatforms)
                XCTAssertEqual(songTable.numberOfRows(inSection: 0), MusicSearchPreferences.platforms.count)
                XCTAssertTrue(runner.switches["music.search.songs.platform.tx"].exists)
                try dragRow(in: songTable, from: 2, to: 0, runner: runner)
                waitFor("native reorder gesture saved song priority") {
                    store.preferences.songPlatforms == [.kugou, .netease]
                }
                XCTAssertEqual(try saved(defaults).songPlatforms, [.kugou, .netease])
                perform { runner.switches["music.search.songs.platform.tx"].tap() }
                waitFor("song platform re-enabled in its displayed position") {
                    store.preferences.songPlatforms == [.kugou, .qq, .netease]
                }
                let retiredSwitch = try XCTUnwrap(descendants(songTable).compactMap { $0 as? UISwitch }.first)
                try closeWindow(on: page, runner: runner)
                let beforeRetiredActions = try saved(defaults)
                retiredSwitch.setOn(!retiredSwitch.isOn, animated: false)
                retiredSwitch.sendActions(for: .valueChanged)
                songTable.dataSource?.tableView?(songTable, moveRowAt: IndexPath(row: 0, section: 0),
                                                 to: IndexPath(row: 1, section: 0))
                XCTAssertEqual(try saved(defaults), beforeRetiredActions)

                perform { runner.buttons["music.search.playlists.platforms"].tap() }
                let playlists = try platformWindow(on: page, kind: .playlists)
                let playlistTable = try platformTable(in: playlists)
                XCTAssertEqual(playlistTable.numberOfRows(inSection: 0), MusicSearchPreferences.platforms.count)
                moveRow(in: playlistTable, from: 1, to: 0)
                XCTAssertEqual(try saved(defaults).playlistPlatforms, [.migu, .kuwo])
                perform { runner.switches["music.search.playlists.platform.kw"].tap() }
                waitFor("playlist platform disabled") { store.preferences.playlistPlatforms == [.migu] }
                perform { runner.switches["music.search.playlists.platform.tx"].tap() }
                waitFor("previously disabled playlist platform enabled") {
                    store.preferences.playlistPlatforms == [.migu, .qq]
                }
                XCTAssertEqual(try saved(defaults).songPlatforms, [.kugou, .qq, .netease])
                try closeWindow(on: page, runner: runner)

                for kind in MusicSearchSettingsKind.allCases {
                    perform { runner.buttons["music.search.\(kind.rawValue).platforms"].tap() }
                    let window = try platformWindow(on: page, kind: kind)
                    let table = try platformTable(in: window)
                    let expected = kind == .songs
                        ? [MusicPublicBrowsePlatform.kugou, .qq, .netease, .kuwo, .migu]
                        : [MusicPublicBrowsePlatform.migu, .qq, .netease, .kugou, .kuwo]
                    let tableElement = runner.tables[try XCTUnwrap(table.accessibilityIdentifier)]
                    XCTAssertTrue(tableElement.exists)
                    XCTAssertEqual(tableElement.cells.count, expected.count)
                    let persisted = try saved(defaults)
                    let enabled = kind == .songs ? persisted.songPlatforms : persisted.playlistPlatforms
                    for (index, platform) in expected.enumerated() {
                        let cell = try XCTUnwrap(table.cellForRow(at: IndexPath(row: index, section: 0)))
                        let control = try XCTUnwrap(descendants(cell).compactMap { $0 as? UISwitch }.first)
                        XCTAssertEqual(control.isOn, enabled.contains(platform))
                        let platformSwitch = runner.switches[
                            "music.search.\(kind.rawValue).platform.\(platform.rawValue)"
                        ]
                        XCTAssertTrue(platformSwitch.exists)
                        let rowFrame = tableElement.cells.element(boundBy: index).frame
                        XCTAssertFalse(rowFrame.isEmpty)
                        XCTAssertGreaterThanOrEqual(platformSwitch.frame.midY, rowFrame.minY)
                        XCTAssertLessThanOrEqual(platformSwitch.frame.midY, rowFrame.maxY)
                        let font = UIFont.preferredFont(forTextStyle: .body, compatibleWith: cell.traitCollection)
                        XCTAssertEqual(cell.bounds.height, max(SettingsVisualMetrics.rowHeight,
                            ceil(font.lineHeight) + SettingsVisualMetrics.rowVerticalPadding * 2), accuracy: 1)
                        let input = try XCTUnwrap(descendants(cell).compactMap { $0 as? SettingsSwitch }.first)
                        XCTAssertEqual(input.bounds.height, font.lineHeight, accuracy: 1)
                        XCTAssertLessThanOrEqual(control.frame.height, input.bounds.height + 1)
                    }
                    try closeWindow(on: page, runner: runner)
                }
                XCTAssertEqual(try saved(defaults).songs.pages, initial.songs.pages)
                XCTAssertEqual(try saved(defaults).playlists.cacheMinutes, initial.playlists.cacheMinutes)
            }
        }
    }

    func testNumberCapsulesCenterValuesAndStepOnlyTheirPersistedFieldAtExistingBoundaries() throws {
        let initial = MusicSearchPreferences(songPages: 1, songConcurrency: 1, songCacheMinutes: 1440,
                                             playlistPages: Int.max, playlistConcurrency: Int.max,
                                             playlistCacheMinutes: 1)
        try withPreferences(initial) { defaults, store, appearance in
            let page = MusicSearchSettingsViewController(appearance: appearance, store: store)
            try withMountedPage(page) { runner in
                let list = try searchList(in: page)
                let fields: [(String, WritableKeyPath<MusicSearchPreferences.Kind, Int>, Int)] = [
                    ("pages", \.pages, Int.max), ("concurrency", \.concurrency, Int.max),
                    ("cacheMinutes", \.cacheMinutes, 1440)
                ]
                for (section, kind) in [(2, MusicSearchSettingsKind.songs), (3, .playlists)] {
                    for (row, field) in fields.enumerated() {
                        showRow(row, section: section, in: list)
                        let identifier = "music.search.\(kind.rawValue).\(field.0)"
                        let decrease = runner.buttons["\(identifier).decrease"]
                        let increase = runner.buttons["\(identifier).increase"]
                        let number = runner.staticTexts["\(identifier).value"]
                        XCTAssertTrue(number.waitForExistence(timeout: 3))
                        assertCapsuleGeometry(identifier, runner: runner)
                        let before = try saved(defaults)
                        let value = (kind == .songs ? before.songs : before.playlists)[keyPath: field.1]
                        XCTAssertTrue(number.label.contains(String(value)))
                        XCTAssertEqual(decrease.isEnabled, value > 1)
                        XCTAssertEqual(increase.isEnabled, value < field.2)
                        let step = value == field.2 ? -1 : 1
                        perform { (step < 0 ? decrease : increase).tap() }
                        var expected = before
                        if kind == .songs { expected.songs[keyPath: field.1] = value + step }
                        else { expected.playlists[keyPath: field.1] = value + step }
                        waitFor("single numeric field persisted") { store.preferences == expected }
                        XCTAssertEqual(try saved(defaults), expected)
                        perform { (step < 0 ? increase : decrease).tap() }
                        waitFor("numeric value restored") { store.preferences == before }
                        XCTAssertEqual(try saved(defaults), before)
                    }
                }
            }
        }
    }

    func testNarrowAccessibilityTypeKeepsNumericLabelsAndCapsulesApart() throws {
        let initial = MusicSearchPreferences(songPages: Int.max, songConcurrency: Int.max)
        try withPreferences(initial) { _, store, appearance in
            let page = MusicSearchSettingsViewController(appearance: appearance, store: store)
            try withMountedPage(page, width: 320, category: .accessibilityExtraExtraExtraLarge) { runner in
                let list = try searchList(in: page)
                for (section, kind) in [(2, "songs"), (3, "playlists")] {
                    for (row, field) in ["pages", "concurrency", "cacheMinutes"].enumerated() {
                        showRow(row, section: section, in: list)
                        assertCapsuleGeometry("music.search.\(kind).\(field)", runner: runner)
                    }
                }
                attach(page.view, name: "Music search settings 320 AXXXL")
            }
        }
    }

    func testCompactNumberButtonsKeepFullRowTouchHeight() throws {
        try withPreferences { defaults, store, appearance in
            let page = MusicSearchSettingsViewController(appearance: appearance, store: store)
            try withMountedPage(page) { runner in
                let list = try searchList(in: page)
                showRow(0, section: 2, in: list)
                let increase = runner.buttons["music.search.songs.pages.increase"]
                let decrease = runner.buttons["music.search.songs.pages.decrease"]
                XCTAssertTrue(increase.waitForExistence(timeout: 3))
                let before = try saved(defaults)
                perform {
                    increase.coordinate(withNormalizedOffset: CGVector(dx: 0.5, dy: 0.5))
                        .withOffset(CGVector(dx: 0, dy: 20)).tap()
                }
                var expected = before
                expected.songs.pages += 1
                waitFor("compact increment accepts lower row touch") { store.preferences == expected }
                XCTAssertEqual(try saved(defaults), expected)
                perform {
                    decrease.coordinate(withNormalizedOffset: CGVector(dx: 0.5, dy: 0.5))
                        .withOffset(CGVector(dx: 0, dy: -20)).tap()
                }
                waitFor("compact decrement accepts upper row touch") { store.preferences == before }
                XCTAssertEqual(try saved(defaults), before)
            }
        }
    }

    func testInvalidatingTheSearchPageClosesItsPlatformWindow() throws {
        try withPreferences { _, store, appearance in
            let page = MusicSearchSettingsViewController(appearance: appearance, store: store)
            try withMountedPage(page) { runner in
                perform { runner.buttons["music.search.playlists.platforms"].tap() }
                let window = try platformWindow(on: page, kind: .playlists)
                PageViewController.invalidatePages(in: page)
                waitFor("platform window closed with its owner") { page.presentedViewController == nil }
                XCTAssertNil(window.presentingViewController)
                XCTAssertFalse(page.isPageActive)
            }
        }
    }

    private func assertCapsuleGeometry(_ identifier: String, runner: XCUIApplication,
                                       file: StaticString = #filePath, line: UInt = #line) {
        let viewport = runner.otherElements["music.search.fixture.page"]
        let label = runner.staticTexts["\(identifier).label"]
        let number = runner.staticTexts["\(identifier).value"]
        let decrease = runner.buttons["\(identifier).decrease"]
        let increase = runner.buttons["\(identifier).increase"]
        XCTAssertTrue(viewport.waitForExistence(timeout: 3), file: file, line: line)
        XCTAssertFalse(viewport.frame.isEmpty, file: file, line: line)
        XCTAssertTrue(number.waitForExistence(timeout: 3), file: file, line: line)
        let values = [decrease.frame, number.frame, increase.frame]
        XCTAssertTrue(values.allSatisfy { !$0.isEmpty }, file: file, line: line)
        XCTAssertLessThanOrEqual(decrease.frame.maxX, number.frame.minX + 1, file: file, line: line)
        XCTAssertLessThanOrEqual(number.frame.maxX, increase.frame.minX + 1, file: file, line: line)
        XCTAssertEqual(number.frame.midX, (decrease.frame.midX + increase.frame.midX) / 2,
                       accuracy: 1, file: file, line: line)
        XCTAssertEqual(decrease.frame.midY, increase.frame.midY, accuracy: 1, file: file, line: line)
        XCTAssertFalse(label.frame.intersects(decrease.frame), file: file, line: line)
        XCTAssertFalse(label.frame.intersects(number.frame), file: file, line: line)
        XCTAssertFalse(label.frame.intersects(increase.frame), file: file, line: line)
        XCTAssertGreaterThanOrEqual(decrease.frame.minX, viewport.frame.minX, file: file, line: line)
        XCTAssertLessThanOrEqual(increase.frame.maxX, viewport.frame.maxX, file: file, line: line)
    }

    private func assertLeadingReorderControl(in table: UITableView) {
        table.layoutIfNeeded()
        for cell in table.visibleCells {
            XCTAssertTrue(cell.showsReorderControl)
            let content = cell.contentView.convert(cell.contentView.bounds, to: table.window)
            let leadingChrome = cell.subviews.filter {
                $0 !== cell.contentView && !$0.isHidden && $0.bounds.width > 0 &&
                    $0.convert($0.bounds, to: table.window).maxX <= content.minX + 1
            }
            XCTAssertFalse(leadingChrome.isEmpty, "The native reorder control must precede the platform content")
        }
    }

    private func moveRow(in table: UITableView, from source: Int, to destination: Int) {
        let from = IndexPath(row: source, section: 0), to = IndexPath(row: destination, section: 0)
        XCTAssertEqual(table.dataSource?.tableView?(table, canMoveRowAt: from), true)
        table.dataSource?.tableView?(table, moveRowAt: from, to: to)
        table.layoutIfNeeded()
    }

    private func dragRow(in table: UITableView, from source: Int, to destination: Int,
                         runner: XCUIApplication) throws {
        let cell = try XCTUnwrap(table.cellForRow(at: IndexPath(row: source, section: 0)))
        let target = try XCTUnwrap(table.cellForRow(at: IndexPath(row: destination, section: 0)))
        let content = cell.contentView.convert(cell.contentView.bounds, to: table.window)
        let handle = try XCTUnwrap(cell.subviews.first {
            $0 !== cell.contentView && !$0.isHidden && $0.bounds.width > 0 &&
                $0.convert($0.bounds, to: table.window).maxX <= content.minX + 1
        })
        let bounds = table.convert(table.bounds, to: table.window)
        let start = handle.convert(handle.bounds, to: table.window)
        let end = target.convert(target.bounds, to: table.window)
        let tableElement = runner.tables[try XCTUnwrap(table.accessibilityIdentifier)]
        XCTAssertTrue(tableElement.waitForExistence(timeout: 3))
        XCTAssertFalse(bounds.isEmpty)
        // Use visual positions after the native table's mirror transform.
        let from = tableElement.coordinate(withNormalizedOffset: CGVector(
            dx: (start.midX - bounds.minX) / bounds.width,
            dy: (start.midY - bounds.minY) / bounds.height
        ))
        let to = tableElement.coordinate(withNormalizedOffset: CGVector(
            dx: (start.midX - bounds.minX) / bounds.width,
            dy: (end.midY - bounds.minY) / bounds.height
        ))
        perform { from.press(forDuration: 0.5, thenDragTo: to) }
    }

    private func platformWindow(on page: UIViewController,
                                kind: MusicSearchSettingsKind) throws -> MusicSearchPlatformsViewController {
        waitFor("platform window visible") {
            guard let window = page.presentedViewController as? MusicSearchPlatformsViewController else { return false }
            return window.viewIfLoaded?.window != nil && !window.isBeingPresented && window.isPageVisible
        }
        let window = try XCTUnwrap(page.presentedViewController as? MusicSearchPlatformsViewController)
        XCTAssertEqual(window.kind, kind)
        return window
    }

    private func platformTable(in window: UIViewController) throws -> UITableView {
        waitFor("platform rows laid out") {
            window.view.layoutIfNeeded()
            return self.descendants(window.view).compactMap { $0 as? UITableView }
                .contains { $0.visibleCells.count == MusicSearchPreferences.platforms.count }
        }
        return try XCTUnwrap(descendants(window.view).compactMap { $0 as? UITableView }.first)
    }

    private func closeWindow(on page: UIViewController, runner: XCUIApplication) throws {
        perform { runner.buttons["关闭"].tap() }
        waitFor("returned to search settings") { page.presentedViewController == nil }
        XCTAssertTrue(runner.buttons["music.search.songs.platforms"].isHittable)
        XCTAssertTrue(runner.buttons["music.search.playlists.platforms"].isHittable)
    }

    private func searchList(in page: UIViewController) throws -> UICollectionView {
        waitFor("search settings list laid out") {
            page.view.layoutIfNeeded()
            return self.descendants(page.view).compactMap { $0 as? UICollectionView }
                .contains { $0.numberOfSections >= 4 }
        }
        return try XCTUnwrap(descendants(page.view).compactMap { $0 as? UICollectionView }.first)
    }

    private func showRow(_ row: Int, section: Int, in list: UICollectionView) {
        list.scrollToItem(at: IndexPath(item: row, section: section), at: .centeredVertically, animated: false)
        list.layoutIfNeeded()
    }

    private func saved(_ database: SQLiteDatabase) throws -> MusicSearchPreferences {
        try appStorageTestSearchPreferences(database).preferences
    }

    private func withPreferences(
        _ initial: MusicSearchPreferences = .init(),
        body: (SQLiteDatabase, MusicSearchPreferencesStore, MusicAppearancePreferencesStore) throws -> Void
    ) throws {
        let database = try appStorageTestDatabase()
        let store = try appStorageTestSearchPreferences(database, initial: initial)
        XCTAssertEqual(store.preferences, initial)
        try body(database, store, appStorageTestAppearance(database))
    }

    private func descendants(_ view: UIView) -> [UIView] {
        [view] + view.subviews.flatMap(descendants)
    }

    private func waitFor(_ description: String, until predicate: @escaping @MainActor () -> Bool) {
        let ready = XCTNSPredicateExpectation(predicate: NSPredicate { _, _ in
            MainActor.assumeIsolated { predicate() }
        }, object: nil)
        wait(for: [ready], timeout: 5)
        XCTAssertTrue(predicate(), description)
    }

    private func perform(_ action: @escaping @MainActor () -> Void) {
        let performed = expectation(description: "search settings UI action delivered")
        RunLoop.main.perform {
            MainActor.assumeIsolated { action() }
            performed.fulfill()
        }
        wait(for: [performed], timeout: 5)
    }

    private func attach(_ view: UIView, name: String) {
        let attachment = XCTAttachment(image: UIGraphicsImageRenderer(bounds: view.bounds).image { _ in
            XCTAssertTrue(view.drawHierarchy(in: view.bounds, afterScreenUpdates: true))
        })
        attachment.name = name
        attachment.lifetime = .keepAlways
        add(attachment)
    }

    private func unmountPage() {
        if let root = window?.rootViewController {
            PageViewController.invalidatePages(in: root)
            root.dismiss(animated: false)
        }
        window?.isHidden = true
        window?.rootViewController = nil
        window = nil
        previousKeyWindow?.makeKey()
        previousKeyWindow = nil
    }

    override func tearDown() {
        unmountPage()
        super.tearDown()
    }

    private func withMountedPage(
        _ page: PageViewController, width: CGFloat = 390,
        category: UIContentSizeCategory = .large, body: (XCUIApplication) throws -> Void
    ) throws {
        let runner = XCUIApplication(bundleIdentifier: try XCTUnwrap(Bundle.main.bundleIdentifier))
        perform { runner.activate() }
        waitFor("test host foreground") { UIApplication.shared.applicationState == .active }
        let scene = try XCTUnwrap(UIApplication.shared.connectedScenes.compactMap { $0 as? UIWindowScene }
            .first { $0.activationState == .foregroundActive })
        previousKeyWindow = scene.windows.first { $0.isKeyWindow }
        let navigation = PageNavigationController(themeProvider: page.themeProvider)
        navigation.setViewControllers([page], animated: false)
        navigation.setOverrideTraitCollection(UITraitCollection(preferredContentSizeCategory: category), forChild: page)
        page.view.accessibilityIdentifier = "music.search.fixture.page"
        let container = UIViewController()
        container.addChild(navigation)
        container.view.addSubview(navigation.view)
        navigation.view.translatesAutoresizingMaskIntoConstraints = false
        NSLayoutConstraint.activate([
            navigation.view.leadingAnchor.constraint(equalTo: container.view.leadingAnchor),
            navigation.view.topAnchor.constraint(equalTo: container.view.topAnchor),
            navigation.view.bottomAnchor.constraint(equalTo: container.view.bottomAnchor),
            navigation.view.widthAnchor.constraint(equalToConstant: width)
        ])
        navigation.didMove(toParent: container)
        let window = UIWindow(windowScene: scene)
        self.window = window
        window.frame = scene.screen.bounds
        window.windowLevel = .normal + 1
        window.rootViewController = container
        window.makeKeyAndVisible()
        defer {
            if page.presentedViewController != nil {
                let dismissed = expectation(description: "search platform window dismissed")
                page.dismiss(animated: false) { dismissed.fulfill() }
                wait(for: [dismissed], timeout: 3)
            }
            unmountPage()
        }
        waitFor("search page ready") {
            page.view.layoutIfNeeded()
            return page.isPageActive && page.isPageVisible && page.canPresentPageAlert
        }
        try body(runner)
    }
}
#endif
