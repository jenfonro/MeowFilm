import Foundation
import UIKit
import XCTest
import MeowContracts
import MeowUI
import MeowStorage
@testable import MeowFilmFeature

@MainActor
final class FilmPlayerSettingsPresentationTests: XCTestCase {
    private let switchFields: [(String, WritableKeyPath<FilmPlayerPreferences, Bool>)] = [
        ("showsControlCenter", \.showsControlCenter), ("recognizesChapters", \.recognizesChapters),
        ("swipeVolume", \.swipeVolume), ("swipeBrightness", \.swipeBrightness),
        ("swipeSeek", \.swipeSeek), ("swipeEpisodes", \.swipeEpisodes),
        ("hapticHold", \.hapticHold), ("hapticBrightness", \.hapticBrightness),
        ("hapticVolume", \.hapticVolume), ("hapticSeek", \.hapticSeek),
    ]

    private func store() throws -> FilmPlayerPreferencesStore { try appStorageTestPlayerPreferences() }

    private func page(_ owner: FilmPlayerPreferencesStore,
                      area: FilmPlayerSettingsController.Area = .controls) -> FilmPlayerSettingsController {
        let page = FilmPlayerSettingsController(owner: owner, area: area, themeProvider: TestPageTheme())
        page.loadViewIfNeeded()
        return page
    }

    private func rows(_ page: FilmSettingsListController) -> [FilmSettingsRow] {
        page.sections.flatMap(\.rows)
    }

    private func choice(_ id: String, from page: FilmSettingsListController) throws -> UIButton {
        let row = try XCTUnwrap(rows(page).first { $0.id == "film.player." + id })
        XCTAssertNil(row.action)
        XCTAssertFalse(row.disclosure)
        let button = try XCTUnwrap(row.content as? UIButton)
        XCTAssertTrue(button.showsMenuAsPrimaryAction)
        XCTAssertEqual(button.accessibilityIdentifier, "film.player." + id + ".menu")
        XCTAssertEqual(button.accessibilityLabel, row.title)
        XCTAssertEqual(button.accessibilityValue, row.detail)
        return button
    }

    private func actions(_ button: UIButton) throws -> [UIAction] {
        let menu = try XCTUnwrap(button.menu)
        XCTAssertTrue(menu.options.contains(.singleSelection))
        let actions = menu.children.compactMap { $0 as? UIAction }
        XCTAssertEqual(actions.count, menu.children.count)
        return actions
    }

    private func invoke(_ action: UIAction) {
        let button = UIButton(type: .system)
        button.addAction(action, for: .touchUpInside); button.sendActions(for: .touchUpInside)
    }

    private func toggle(_ row: FilmSettingsRow) throws -> SettingsSwitch {
        let content = try XCTUnwrap(row.content as? UIStackView)
        return try XCTUnwrap(content.arrangedSubviews.compactMap { $0 as? SettingsSwitch }.first)
    }

    func testPlayerSettingsSeparatesDefaultEngineAndPlaybackControls() throws {
        let owner = try store(), page = page(owner, area: .settings)
        XCTAssertEqual(rows(page).map(\.title), ["播放核心", "播放控制"])
        XCTAssertEqual(rows(page).first?.detail, "KSPlayer")
        let navigation = UINavigationController(rootViewController: page)
        let engines = try actions(choice("engine", from: page))
        XCTAssertEqual(engines.map(\.title), ["KSPlayer", "AetherEngine"])
        XCTAssertEqual(engines.map(\.state), [.on, .off])
        invoke(engines[1])
        appStorageTestEventually { owner.preferences.engine == .aether }
        XCTAssertEqual(owner.preferences.engine.rawValue, "aether")
        XCTAssertEqual(rows(page).first?.detail, "AetherEngine")
        XCTAssertEqual(try actions(choice("engine", from: page)).map(\.state), [.off, .on])
        XCTAssertTrue(navigation.topViewController === page)
        XCTAssertEqual(navigation.viewControllers.count, 1)
        try XCTUnwrap(rows(page).last?.action)()
        let controls = try XCTUnwrap(navigation.topViewController as? FilmPlayerSettingsController)
        controls.loadViewIfNeeded()
        XCTAssertEqual(controls.pageTitle, "播放控制")
        XCTAssertEqual(rows(controls).count, 17)
    }

    func testControlGroupsAndEverySwitchUseTheSharedPreferenceOwner() throws {
        let database = try appStorageTestDatabase(), owner = try appStorageTestPlayerPreferences(database), page = page(owner)
        XCTAssertEqual(page.sections.map(\.title), ["播放控制", "单击手势", "双击手势", "滑动手势", "震动反馈"])
        XCTAssertEqual(page.sections.map { $0.rows.count }, [5, 2, 2, 4, 4])
        for (id, keyPath) in switchFields {
            let row = try XCTUnwrap(rows(page).first { $0.id == "film.player." + id })
            let content = try XCTUnwrap(row.content as? UIStackView)
            let control = try toggle(row).control
            XCTAssertTrue(control.isOn)
            let label = try XCTUnwrap(content.arrangedSubviews.compactMap { $0 as? UILabel }.first)
            assertRegular(label.font)
            control.isOn = false
            control.sendActions(for: .valueChanged)
            appStorageTestEventually { !owner.preferences[keyPath: keyPath] }
            XCTAssertFalse(owner.preferences[keyPath: keyPath])
            XCTAssertFalse(try toggle(XCTUnwrap(rows(page).first { $0.id == "film.player." + id })).control.isOn)
        }
        XCTAssertEqual(try appStorageTestPlayerPreferences(database).preferences, owner.preferences)
        XCTAssertNil(owner.persistenceError)
    }

    func testTapMenusExcludeUnsupportedActionsAndPersistSelections() throws {
        let database = try appStorageTestDatabase(), owner = try appStorageTestPlayerPreferences(database), page = page(owner)
        let navigation = UINavigationController(rootViewController: page)
        let expected: [(String, [String])] = [
            ("singleTapCenter", ["无操作", "显示/隐藏播放栏", "播放/暂停"]),
            ("singleTapSides", ["无操作", "显示/隐藏播放栏", "播放/暂停", "快进/快退"]),
            ("doubleTapCenter", ["无操作", "播放/暂停"]),
            ("doubleTapSides", ["无操作", "播放/暂停", "快进/快退"]),
        ]
        for (id, titles) in expected {
            let options = try actions(choice(id, from: page))
            XCTAssertEqual(options.map(\.title), titles)
            XCTAssertEqual(options.filter { $0.state == .on }.count, 1)
            invoke(try XCTUnwrap(options.first))
            appStorageTestEventually { self.rows(page).first { $0.id == "film.player." + id }?.detail == "无操作" }
            XCTAssertEqual(rows(page).first { $0.id == "film.player." + id }?.detail, "无操作")
            XCTAssertEqual(try actions(choice(id, from: page)).filter { $0.state == .on }.map(\.title), ["无操作"])
            XCTAssertTrue(navigation.topViewController === page)
            XCTAssertEqual(navigation.viewControllers.count, 1)
        }
        XCTAssertEqual(owner.preferences.singleTapCenter, .none)
        XCTAssertEqual(owner.preferences.singleTapSides, .none)
        XCTAssertEqual(owner.preferences.doubleTapCenter, .none)
        XCTAssertEqual(owner.preferences.doubleTapSides, .none)
        XCTAssertNil(owner.persistenceError)
    }

    func testRateMenusRefreshByPreferredEngineAndShowTheSavedCorrection() throws {
        let database = try appStorageTestDatabase(), owner = try appStorageTestPlayerPreferences(database), page = page(owner)
        let navigation = UINavigationController(rootViewController: page)
        XCTAssertTrue(try appStorageTestWait { await owner.set(\.defaultRate, 3) })
        XCTAssertTrue(try appStorageTestWait { await owner.set(\.holdRate, 8) })
        page.pageDidFinishAppearing()
        let defaults = try actions(choice("defaultRate", from: page))
        XCTAssertEqual(defaults.count, 5)
        XCTAssertEqual(defaults.last?.title, "3X")
        XCTAssertTrue(try appStorageTestWait { await owner.set(\.engine, .aether) })
        page.pageDidFinishAppearing()
        let corrected = try actions(choice("defaultRate", from: page))
        XCTAssertEqual(corrected.count, 4)
        XCTAssertEqual(corrected.filter { $0.state == .on }.map(\.title), ["2X"])
        XCTAssertEqual(rows(page).first { $0.id == "film.player.defaultRate" }?.detail, "2X")
        XCTAssertEqual(rows(page).first { $0.id == "film.player.holdRate" }?.detail, "2X")
        XCTAssertEqual(try actions(choice("holdRate", from: page)).map(\.title), ["2X"])
        XCTAssertTrue(try appStorageTestWait { await owner.set(\.engine, .ksPlayer) })
        page.pageDidFinishAppearing()
        let holds = try actions(choice("holdRate", from: page))
        XCTAssertEqual(holds.map(\.title), ["2X", "3X", "4X", "6X", "8X"])
        XCTAssertEqual(holds.filter { $0.state == .on }.map(\.title), ["2X"])
        let steps = try actions(choice("seekStep", from: page))
        XCTAssertEqual(steps.map(\.title), ["5 秒", "10 秒", "15 秒", "20 秒", "30 秒", "60 秒"])
        invoke(try XCTUnwrap(steps.last))
        appStorageTestEventually { owner.preferences.seekStep == 60 }
        XCTAssertEqual(owner.preferences.seekStep, 60)
        XCTAssertEqual(rows(page).first { $0.id == "film.player.seekStep" }?.detail, "60 秒")
        XCTAssertTrue(navigation.topViewController === page)
        XCTAssertEqual(navigation.viewControllers.count, 1)
        XCTAssertTrue(owner.preferences.swipeVolume)
        XCTAssertTrue(owner.preferences.swipeBrightness)
        XCTAssertTrue(owner.preferences.swipeSeek)
    }

    // This mounts the production controller in the foreground XCTest runner;
    // screen attachments are component evidence, not real App navigation.
    func testForegroundComponentScreensShowBothEnginesAndAllSeventeenControls() async throws {
        let expected = [
            [("defaultRate", "默认倍速"), ("holdRate", "长按倍速"), ("seekStep", "快进/快退步长"),
             ("showsControlCenter", "控制中心显示"), ("recognizesChapters", "自动识别片头片尾")],
            [("singleTapCenter", "单击屏幕中间"), ("singleTapSides", "单击屏幕两边")],
            [("doubleTapCenter", "双击屏幕中间"), ("doubleTapSides", "双击屏幕两边")],
            [("swipeVolume", "滑动调节音量"), ("swipeBrightness", "滑动调节亮度"),
             ("swipeSeek", "滑动调节进度"), ("swipeEpisodes", "短剧滑动切集")],
            [("hapticHold", "长按屏幕"), ("hapticBrightness", "滑动调节亮度"),
             ("hapticVolume", "滑动调节音量"), ("hapticSeek", "滑动调节进度")],
        ]
        for engine in [PlaybackVideoEngine.ksPlayer, .aether] {
            let database = try appStorageTestDatabase(), owner = try appStorageTestPlayerPreferences(database)
            XCTAssertTrue(try appStorageTestWait { await owner.set(\.engine, engine) })
            let settings = page(owner, area: .settings)
            try await withForegroundPage(settings) { navigation in
                let engineCell = try visibleCell(IndexPath(row: 0, section: 0), on: settings)
                let engineButton = try choice("engine", from: settings)
                XCTAssertEqual(engineButton.accessibilityLabel, "播放核心")
                XCTAssertEqual(engineButton.accessibilityValue, engine.title)
                try assertChoiceLayout(rows(settings)[0], cell: engineCell)
                try await capture(settings, name: "film-player-component-\(engine.rawValue)-settings",
                                  anchors: ["engine", "controls"])
                try await runnerAction { $0.tables["film.player.settings"].cells["film.player.controls"].tap() }
                try await waitFor("Playback controls finished their real appearance") {
                    navigation.topViewController !== settings &&
                        (navigation.topViewController as? FilmPlayerSettingsController)?.canPresentPageAlert == true
                }
                let controls = try XCTUnwrap(navigation.topViewController as? FilmPlayerSettingsController)
                XCTAssertEqual(controls.sections.map(\.title),
                               ["播放控制", "单击手势", "双击手势", "滑动手势", "震动反馈"])
                XCTAssertEqual(controls.sections.map { $0.rows.map(\.id) },
                               expected.map { $0.map { "film.player." + $0.0 } })
                XCTAssertEqual(controls.sections.map { $0.rows.map(\.title) }, expected.map { $0.map(\.1) })
                for (index, value) in ["1X", "2X", "15 秒"].enumerated() {
                    XCTAssertEqual(try visibleCell(IndexPath(row: index, section: 0), on: controls)
                        .accessibilityValue, value)
                }
                try await capture(controls, name: "film-player-component-\(engine.rawValue)-rates",
                                  anchors: ["defaultRate", "holdRate", "seekStep"])
                var displayed = Set<String>()
                for (section, fields) in expected.enumerated() {
                    for (row, field) in fields.enumerated() {
                        let path = IndexPath(row: row, section: section)
                        controls.table.scrollToRow(at: path, at: .middle, animated: false)
                        controls.view.layoutIfNeeded()
                        let cell = try visibleCell(path, on: controls)
                        XCTAssertEqual(cell.accessibilityIdentifier, "film.player." + field.0)
                        let setting = controls.sections[section].rows[row]
                        if setting.content is UIStackView {
                            XCTAssertTrue(try toggle(setting).control.isOn)
                            _ = try assertSwitchLayout(setting, cell: cell)
                        } else {
                            try assertChoiceLayout(setting, cell: cell)
                            XCTAssertEqual(cell.accessoryType, .none)
                        }
                        displayed.insert(try XCTUnwrap(cell.accessibilityIdentifier))
                    }
                }
                XCTAssertEqual(displayed.count, 17)
                XCTAssertEqual(displayed, Set(expected.flatMap { $0.map { "film.player." + $0.0 } }))
                controls.table.scrollToRow(at: IndexPath(row: 0, section: 1), at: .top, animated: false)
                controls.table.setContentOffset(CGPoint(x: 0, y: max(-controls.table.adjustedContentInset.top,
                    controls.table.contentOffset.y - controls.table.rectForHeader(inSection: 1).height)), animated: false)
                try await capture(controls, name: "film-player-component-\(engine.rawValue)-gestures",
                                  anchors: ["singleTapCenter", "singleTapSides", "doubleTapCenter", "doubleTapSides"])
                controls.table.scrollToRow(at: IndexPath(row: 3, section: 4), at: .bottom, animated: false)
                try await capture(controls, name: "film-player-component-\(engine.rawValue)-feedback",
                                  anchors: ["hapticHold", "hapticBrightness", "hapticVolume", "hapticSeek"])
            }
        }
    }

    func testForegroundNativeMenusAndSwitchesPersistWithoutPushingChoicePages() async throws {
        for engine in [PlaybackVideoEngine.ksPlayer, .aether] {
            let database = try appStorageTestDatabase(), owner = try appStorageTestPlayerPreferences(database)
            XCTAssertTrue(try appStorageTestWait { await owner.set(\.engine, engine) })
            let settings = page(owner, area: .settings)
            try await withForegroundPage(settings) { navigation in
                try await selectMenu("engine", on: settings, titles: ["KSPlayer", "AetherEngine"], value: engine.title,
                                     evidenceName: "film-player-native-menu-\(engine.rawValue)-engine")
                XCTAssertEqual(owner.preferences.engine, engine)
                try await runnerAction { $0.tables["film.player.settings"].cells["film.player.controls"].tap() }
                try await waitFor("Playback control navigation remains available") {
                    (navigation.topViewController as? FilmPlayerSettingsController)?.pageTitle == "播放控制" &&
                        navigation.topViewController !== settings &&
                        (navigation.topViewController as? PageViewController)?.canPresentPageAlert == true
                }
                let controls = try XCTUnwrap(navigation.topViewController as? FilmPlayerSettingsController)
                let expected: [(String, [String])] = [
                    ("defaultRate", engine == .ksPlayer ? ["1X", "1.25X", "1.5X", "2X", "3X"] :
                        ["1X", "1.25X", "1.5X", "2X"]),
                    ("holdRate", engine == .ksPlayer ? ["2X", "3X", "4X", "6X", "8X"] : ["2X"]),
                    ("seekStep", ["5 秒", "10 秒", "15 秒", "20 秒", "30 秒", "60 秒"]),
                    ("singleTapCenter", ["无操作", "显示/隐藏播放栏", "播放/暂停"]),
                    ("singleTapSides", ["无操作", "显示/隐藏播放栏", "播放/暂停", "快进/快退"]),
                    ("doubleTapCenter", ["无操作", "播放/暂停"]),
                    ("doubleTapSides", ["无操作", "播放/暂停", "快进/快退"]),
                ]
                for (id, titles) in expected {
                    try await selectMenu(id, on: controls, titles: titles, value: XCTUnwrap(titles.last),
                                         evidenceName: "film-player-native-menu-\(engine.rawValue)-\(id)")
                    XCTAssertEqual(navigation.viewControllers.count, 2)
                }
                XCTAssertEqual(owner.preferences.defaultRate, engine == .ksPlayer ? 3 : 2)
                XCTAssertEqual(owner.preferences.holdRate, engine == .ksPlayer ? 8 : 2)
                XCTAssertEqual(owner.preferences.seekStep, 60)
                XCTAssertEqual(owner.preferences.singleTapCenter, .playPause)
                XCTAssertEqual(owner.preferences.singleTapSides, .seek)
                XCTAssertEqual(owner.preferences.doubleTapCenter, .playPause)
                XCTAssertEqual(owner.preferences.doubleTapSides, .seek)
                var rightEdge: CGFloat?
                for (id, keyPath) in switchFields {
                    let path = try path(id, on: controls)
                    controls.table.scrollToRow(at: path, at: .middle, animated: false)
                    let row = controls.sections[path.section].rows[path.row]
                    let edge = try assertSwitchLayout(row, cell: visibleCell(path, on: controls))
                    if let rightEdge { XCTAssertEqual(edge, rightEdge, accuracy: 0.5) }
                    else { rightEdge = edge }
                    let control = try toggle(row).control
                    let identifier = try XCTUnwrap(control.accessibilityIdentifier)
                    try await runnerAction { runner in
                        let toggle = runner.switches[identifier]
                        XCTAssertTrue(toggle.exists && toggle.isHittable)
                        toggle.tap()
                    }
                    try await waitFor("The switch updates its existing owner") {
                        !owner.preferences[keyPath: keyPath]
                    }
                }
                XCTAssertNil(owner.persistenceError)
                XCTAssertEqual(try appStorageTestPlayerPreferences(database).preferences, owner.preferences)
            }
        }
    }

    func testNativeMenuAndSwitchFailuresPreservePreferencesAndUseRetryFeedback() async throws {
        for menuSelection in [true, false] {
            let database = try appStorageTestDatabase(), owner = try appStorageTestPlayerPreferences(database)
            XCTAssertTrue(try appStorageTestWait { await owner.set(\.seekStep, 30) })
            let before = owner.preferences
            let bytes = try await database.snapshot { try $0.query("SELECT seekStep FROM ios_film_player_preferences").first!.integer("seekStep") }
            let controls = page(owner)
            try await withForegroundPage(controls) { navigation in
                let id = menuSelection ? "defaultRate" : "showsControlCenter"
                let path = try path(id, on: controls)
                controls.table.scrollToRow(at: path, at: .middle, animated: false)
                _ = try visibleCell(path, on: controls)
                if menuSelection {
                    try await runnerAction { $0.buttons["film.player.defaultRate.menu"].tap() }
                    try await runnerAction { XCTAssertTrue($0.buttons["3X"].isHittable) }
                }
                try await database.transaction { try $0.execute("UPDATE ios_film_player_preferences SET seekStep=999 WHERE singleton=1") }
                try await runnerAction { runner in
                    if menuSelection { runner.buttons["3X"].tap() }
                    else { runner.switches["film.player.showsControlCenter.switch"].tap() }
                }
                try await waitFor("Failed edits use the existing floating error feedback") {
                    controls.presentedViewController is UIAlertController
                }
                let alert = try XCTUnwrap(controls.presentedViewController as? UIAlertController)
                XCTAssertEqual(alert.title, "错误")
                XCTAssertEqual(alert.message, owner.persistenceError)
                XCTAssertEqual(alert.actions.map(\.title), ["取消", "重试"])
                XCTAssertEqual(owner.preferences, before)
                let damaged = try await database.read { try $0.query("SELECT seekStep FROM ios_film_player_preferences").first!.integer("seekStep") }; XCTAssertEqual(damaged, 999)
                XCTAssertTrue(rows(controls).allSatisfy { !$0.enabled })
                for row in rows(controls) {
                    if let button = row.content as? UIButton {
                        XCTAssertFalse(button.isEnabled)
                        XCTAssertTrue(try actions(button).allSatisfy { $0.attributes.contains(.disabled) })
                    } else {
                        XCTAssertFalse(try toggle(row).control.isEnabled)
                    }
                }
                XCTAssertTrue(navigation.topViewController === controls)
                XCTAssertEqual(navigation.viewControllers.count, 1)
                try await database.transaction { try $0.execute("UPDATE ios_film_player_preferences SET seekStep=? WHERE singleton=1", [.integer(bytes)]) }
                try await runnerAction { $0.alerts.buttons["重试"].tap() }
                try await waitFor("Retry follows the real dismissal completion and reloads the owner") {
                    controls.presentedViewController == nil && controls.canPresentPageAlert &&
                        owner.persistenceError == nil && self.rows(controls).allSatisfy(\.enabled)
                }
                XCTAssertEqual(owner.preferences, before)
                let restored = try await database.read { try $0.query("SELECT seekStep FROM ios_film_player_preferences").first!.integer("seekStep") }; XCTAssertEqual(restored, bytes)
                XCTAssertTrue(try toggle(XCTUnwrap(rows(controls).first {
                    $0.id == "film.player.showsControlCenter"
                })).control.isOn)
            }
        }
    }

    func testNarrowAndAccessibleRowsKeepCompleteTextAndSharedRightEdges() async throws {
        for area in [FilmPlayerSettingsController.Area.settings, .controls] {
            let database = try appStorageTestDatabase(), owner = try appStorageTestPlayerPreferences(database)
            XCTAssertTrue(try appStorageTestWait { await owner.set(\.engine, .aether) })
            let page = page(owner, area: area)
            try await withForegroundPage(page) { navigation in
                let window = try XCTUnwrap(navigation.view.window)
                let originalFrame = window.frame
                defer {
                    window.frame = originalFrame
                    navigation.traitOverrides.preferredContentSizeCategory = .large
                }
                for category in [UIContentSizeCategory.large, .accessibilityExtraExtraExtraLarge] {
                    navigation.traitOverrides.preferredContentSizeCategory = category
                    await Task.yield()
                    page.pageDidFinishAppearing()
                    for width in [CGFloat(320), 390, 430] {
                        window.frame.size.width = width
                        window.layoutIfNeeded(); page.view.layoutIfNeeded()
                        XCTAssertEqual(page.view.bounds.width, width, accuracy: 0.5)
                        XCTAssertEqual(page.traitCollection.preferredContentSizeCategory, category)
                        var rightEdge: CGFloat?
                        var checked = 0
                        for (section, group) in page.sections.enumerated() {
                            for (index, row) in group.rows.enumerated() where row.content != nil {
                                let path = IndexPath(row: index, section: section)
                                page.table.scrollToRow(at: path, at: .middle, animated: false)
                                let cell = try visibleCell(path, on: page)
                                if row.content is UIButton {
                                    try assertChoiceLayout(row, cell: cell)
                                } else {
                                    let edge = try assertSwitchLayout(row, cell: cell)
                                    if let rightEdge { XCTAssertEqual(edge, rightEdge, accuracy: 0.5) }
                                    else { rightEdge = edge }
                                }
                                checked += 1
                            }
                        }
                        XCTAssertEqual(checked, area == .settings ? 1 : 17)
                        page.table.setContentOffset(CGPoint(x: 0, y: -page.table.adjustedContentInset.top),
                                                    animated: false)
                        page.table.layoutIfNeeded()
                        try await capture(page, name: "film-player-component-\(area)-\(width)-\(category.rawValue)",
                                          anchors: area == .settings ? ["engine"] : ["defaultRate"])
                    }
                }
            }
        }
    }

    private func path(_ id: String, on page: FilmSettingsListController) throws -> IndexPath {
        for (section, group) in page.sections.enumerated() {
            if let row = group.rows.firstIndex(where: { $0.id == "film.player." + id }) {
                return IndexPath(row: row, section: section)
            }
        }
        throw NSError(domain: "FilmPlayerSettingsPresentationTests", code: 2,
                      userInfo: [NSLocalizedDescriptionKey: "Missing setting: " + id])
    }

    private func selectMenu(_ id: String, on page: FilmSettingsListController,
                            titles: [String], value: String, evidenceName: String) async throws {
        let path = try path(id, on: page)
        page.table.scrollToRow(at: path, at: .middle, animated: false)
        let cell = try visibleCell(path, on: page)
        let button = try choice(id, from: page), options = try actions(button)
        XCTAssertEqual(options.map(\.title), titles)
        XCTAssertEqual(options.filter { $0.state == .on }.map(\.title), [try XCTUnwrap(button.accessibilityValue)])
        XCTAssertFalse(options.contains { $0.attributes.contains(.disabled) })
        for (index, option) in options.enumerated() {
            XCTAssertEqual(option.identifier, .init("film.player.\(id).option.\(index)"))
        }
        try assertChoiceLayout(page.sections[path.section].rows[path.row], cell: cell)
        let navigation = try XCTUnwrap(page.navigationController)
        let depth = navigation.viewControllers.count
        let identifier = try XCTUnwrap(button.accessibilityIdentifier)
        try await runnerAction { runner in
            XCTAssertTrue(runner.buttons[identifier].isHittable)
            runner.buttons[identifier].tap()
        }
        try await runnerAction { runner in
            for title in titles {
                XCTAssertTrue(runner.buttons[title].exists && runner.buttons[title].isHittable, title)
            }
            let attachment = XCTAttachment(screenshot: XCUIScreen.main.screenshot())
            attachment.name = evidenceName
            attachment.lifetime = .keepAlways
            self.add(attachment)
        }
        try await runnerAction { $0.buttons[value].tap() }
        try await waitFor("Native selection refreshes the same setting row") {
            self.rows(page).first { $0.id == "film.player." + id }?.detail == value && page.canPresentPageAlert
        }
        XCTAssertTrue(navigation.topViewController === page)
        XCTAssertEqual(navigation.viewControllers.count, depth)
        XCTAssertEqual(try actions(choice(id, from: page)).filter { $0.state == .on }.map(\.title), [value])
    }

    private func assertChoiceLayout(_ row: FilmSettingsRow, cell: UITableViewCell) throws {
        let button = try XCTUnwrap(row.content as? UIButton)
        let content = try XCTUnwrap(button.subviews.compactMap { $0 as? UIStackView }.first)
        let labels = content.arrangedSubviews.compactMap { $0 as? UILabel }
        XCTAssertEqual(labels.count, 2)
        let heading = try XCTUnwrap(labels.first), current = try XCTUnwrap(labels.last)
        XCTAssertEqual(heading.text, row.title); XCTAssertEqual(current.text, row.detail)
        XCTAssertEqual(current.textAlignment, .right)
        XCTAssertNil(cell.contentConfiguration)
        XCTAssertEqual(cell.accessoryType, .none)
        XCTAssertFalse(content.hasAmbiguousLayout)
        for (label, style) in [(heading, UIFont.TextStyle.body), (current, .subheadline)] {
            assertRegular(label.font)
            XCTAssertTrue(label.adjustsFontForContentSizeCategory)
            XCTAssertFalse(label.adjustsFontSizeToFitWidth)
            XCTAssertEqual(label.font.pointSize,
                           UIFont.preferredFont(forTextStyle: style, compatibleWith: label.traitCollection).pointSize,
                           accuracy: 0.5)
            XCTAssertGreaterThan(label.bounds.width, 0)
            XCTAssertLessThanOrEqual(label.sizeThatFits(CGSize(width: label.bounds.width,
                height: .greatestFiniteMagnitude)).height, label.bounds.height + 1)
            XCTAssertTrue(cell.contentView.bounds.insetBy(dx: -1, dy: -1)
                .contains(label.convert(label.bounds, to: cell.contentView)))
            if label.traitCollection.preferredContentSizeCategory == .large {
                XCTAssertLessThanOrEqual(label.bounds.height, label.font.lineHeight + 1)
            }
        }
        let headingFrame = heading.convert(heading.bounds, to: cell.contentView)
        let valueFrame = current.convert(current.bounds, to: cell.contentView)
        XCTAssertEqual(headingFrame.minX, cell.contentView.layoutMarginsGuide.layoutFrame.minX, accuracy: 0.5)
        XCTAssertEqual(valueFrame.maxX, cell.contentView.layoutMarginsGuide.layoutFrame.maxX, accuracy: 0.5)
        XCTAssertLessThanOrEqual(headingFrame.maxX, valueFrame.minX)
        for point in [CGPoint(x: 0.5, y: cell.contentView.bounds.midY),
                      CGPoint(x: cell.contentView.bounds.maxX - 0.5, y: cell.contentView.bounds.midY),
                      CGPoint(x: cell.contentView.bounds.midX, y: cell.contentView.bounds.midY - 21.5),
                      CGPoint(x: cell.contentView.bounds.midX, y: cell.contentView.bounds.midY + 21.5)] {
            XCTAssertTrue(cell.contentView.hitTest(point, with: nil) === button)
        }
    }

    private func assertSwitchLayout(_ row: FilmSettingsRow, cell: UITableViewCell) throws -> CGFloat {
        let content = try XCTUnwrap(row.content as? UIStackView)
        let label = try XCTUnwrap(content.arrangedSubviews.compactMap { $0 as? UILabel }.first)
        let wrapper = try toggle(row), control = wrapper.control
        XCTAssertEqual(label.text, row.title)
        XCTAssertEqual(control.accessibilityIdentifier, row.id + ".switch")
        XCTAssertEqual(control.isEnabled, row.enabled)
        assertRegular(label.font)
        XCTAssertEqual(label.font.pointSize,
                       UIFont.preferredFont(forTextStyle: .body, compatibleWith: label.traitCollection).pointSize,
                       accuracy: 0.5)
        XCTAssertGreaterThan(label.bounds.width, 0)
        XCTAssertLessThanOrEqual(label.sizeThatFits(CGSize(width: label.bounds.width,
            height: .greatestFiniteMagnitude)).height, label.bounds.height + 1)
        let labelFrame = label.convert(label.bounds, to: cell.contentView)
        let controlFrame = control.convert(control.bounds, to: cell.contentView)
        let wrapperFrame = wrapper.convert(wrapper.bounds, to: cell.contentView)
        XCTAssertTrue(cell.contentView.bounds.insetBy(dx: -1, dy: -1).contains(labelFrame))
        XCTAssertTrue(cell.contentView.bounds.insetBy(dx: -1, dy: -1).contains(controlFrame))
        XCTAssertLessThanOrEqual(labelFrame.maxX, controlFrame.minX)
        XCTAssertEqual(wrapperFrame.width, 44, accuracy: 0.5)
        XCTAssertEqual(wrapperFrame.maxX, cell.contentView.layoutMarginsGuide.layoutFrame.maxX, accuracy: 0.5)
        for y in [wrapperFrame.midY - 21.5, wrapperFrame.midY + 21.5] {
            let hit = cell.contentView.hitTest(CGPoint(x: wrapperFrame.midX, y: y), with: nil)
            XCTAssertTrue(hit === control || hit?.isDescendant(of: control) == true)
        }
        return control.convert(control.bounds, to: cell.window).maxX
    }

    private func withForegroundPage(_ page: FilmPlayerSettingsController,
                                   body: (PageNavigationController) async throws -> Void) async throws {
        try await runnerAction { $0.activate() }
        try await waitFor("The actual XCTest runner is foreground active") {
            UIApplication.shared.applicationState == .active
        }
        let scene = try XCTUnwrap(UIApplication.shared.connectedScenes.compactMap { $0 as? UIWindowScene }
            .first { $0.activationState == .foregroundActive }, "A real foreground runner scene is required")
        let previous = scene.windows.first { $0.isKeyWindow }
        let navigation = PageNavigationController(themeProvider: page.themeProvider)
        navigation.setViewControllers([page], animated: false)
        let window = UIWindow(windowScene: scene)
        window.frame = scene.coordinateSpace.bounds
        window.rootViewController = navigation
        XCTAssertEqual(window.windowLevel, .normal)
        window.makeKeyAndVisible()
        defer {
            PageViewController.invalidatePages(in: navigation)
            window.isHidden = true; window.rootViewController = nil
            previous?.makeKey()
        }
        try await waitFor("Settings are visible in the actual key window") {
            window.layoutIfNeeded()
            return window.isKeyWindow && page.isPageVisible && page.canPresentPageAlert
        }
        XCTAssertTrue(navigation.isNavigationBarHidden)
        try await body(navigation)
    }

    private func visibleCell(_ path: IndexPath, on page: FilmSettingsListController) throws -> UITableViewCell {
        page.view.layoutIfNeeded()
        let cell = try XCTUnwrap(page.table.cellForRow(at: path))
        XCTAssertTrue(cell.window === page.view.window)
        XCTAssertTrue(page.table.bounds.insetBy(dx: -1, dy: -1).contains(cell.frame))
        XCTAssertGreaterThan(cell.bounds.width, 0)
        XCTAssertGreaterThanOrEqual(cell.bounds.height, 44)
        return cell
    }

    private func assertRegular(_ font: UIFont) {
        XCTAssertTrue(font.fontDescriptor.symbolicTraits.intersection([.traitBold, .traitItalic]).isEmpty)
        let traits = font.fontDescriptor.object(forKey: .traits) as? [UIFontDescriptor.TraitKey: Any]
        XCTAssertEqual((traits?[.weight] as? NSNumber)?.doubleValue ?? 0, Double(UIFont.Weight.regular.rawValue))
    }

    private func capture(_ page: FilmSettingsListController, name: String, anchors: [String]) async throws {
        try await waitFor("Screen attachment has a real visible foreground page") {
            page.view.layoutIfNeeded()
            return UIApplication.shared.applicationState == .active && page.canPresentPageAlert &&
                page.view.window?.isKeyWindow == true &&
                page.view.window?.windowScene?.activationState == .foregroundActive &&
                !page.table.isDragging && !page.table.isDecelerating
        }
        let window = try XCTUnwrap(page.view.window)
        let tableFrame = page.table.convert(page.table.bounds, to: window)
        let headerFrame = page.pageHeader.convert(page.pageHeader.bounds, to: window)
        XCTAssertGreaterThanOrEqual(tableFrame.minY, headerFrame.maxY - 1)
        XCTAssertTrue(window.bounds.insetBy(dx: -1, dy: -1).contains(tableFrame))
        assertRegular(page.pageHeader.titleFont)
        let tableID = try XCTUnwrap(page.table.accessibilityIdentifier)
        try await runnerAction { runner in
            let table = runner.tables[tableID]
            XCTAssertTrue(table.exists && table.isHittable)
            for id in anchors { XCTAssertTrue(table.cells["film.player." + id].isHittable, id) }
            let attachment = XCTAttachment(screenshot: XCUIScreen.main.screenshot())
            attachment.name = name
            attachment.lifetime = .keepAlways
            self.add(attachment)
        }
    }

    private func runnerAction(_ action: @escaping @MainActor (XCUIApplication) -> Void) async throws {
        let id = try XCTUnwrap(Bundle.main.bundleIdentifier)
        await withCheckedContinuation { continuation in
            RunLoop.main.perform {
                MainActor.assumeIsolated { action(XCUIApplication(bundleIdentifier: id)) }
                continuation.resume()
            }
        }
    }

    private func waitFor(_ message: String, _ condition: @escaping @MainActor () -> Bool) async throws {
        let ready = expectation(for: NSPredicate { _, _ in MainActor.assumeIsolated { condition() } },
                                evaluatedWith: nil)
        let result = await XCTWaiter.fulfillment(of: [ready], timeout: 4)
        XCTAssertEqual(result, .completed, message)
        if result != .completed { throw NSError(domain: "FilmPlayerSettingsPresentationTests", code: 1) }
    }
}
