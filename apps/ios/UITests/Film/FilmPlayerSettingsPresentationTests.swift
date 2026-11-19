import Foundation
import UIKit
import XCTest
import MeowContracts
import MeowUI
@testable import MeowFilmFeature

@MainActor
final class FilmPlayerSettingsPresentationTests: XCTestCase {
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

    private func choice(_ id: String, from page: FilmSettingsListController,
                        in navigation: UINavigationController) throws -> FilmSettingsListController {
        navigation.setViewControllers([page], animated: false)
        let row = try XCTUnwrap(rows(page).first { $0.id == "film.player." + id })
        try XCTUnwrap(row.action)()
        let choice = try XCTUnwrap(navigation.topViewController as? FilmSettingsListController)
        XCTAssertFalse(choice === page)
        choice.loadViewIfNeeded()
        return choice
    }

    func testPlayerSettingsSeparatesDefaultEngineAndPlaybackControls() throws {
        let owner = try store(), page = page(owner, area: .settings)
        XCTAssertEqual(rows(page).map(\.title), ["默认内核", "播放控制"])
        XCTAssertEqual(rows(page).first?.detail, "KSPlayer")
        let navigation = UINavigationController(rootViewController: page)
        let engines = try choice("engine", from: page, in: navigation)
        XCTAssertEqual(rows(engines).map(\.title), ["KSPlayer", "AetherEngine"])
        XCTAssertEqual(rows(engines).map(\.selected), [true, false])
        try XCTUnwrap(rows(engines)[1].action)()
        appStorageTestEventually { owner.preferences.engine == .aether }
        XCTAssertEqual(owner.preferences.engine.rawValue, "aether")
        page.pageDidFinishAppearing()
        XCTAssertEqual(rows(page).first?.detail, "AetherEngine")
        try XCTUnwrap(rows(page).last?.action)()
        let controls = try XCTUnwrap(navigation.topViewController as? FilmPlayerSettingsController)
        controls.loadViewIfNeeded()
        XCTAssertEqual(controls.pageTitle, "播放控制")
        XCTAssertEqual(rows(controls).count, 17)
    }

    func testControlGroupsAndEverySwitchUseTheSharedPreferenceOwner() throws {
        let owner = try store(), page = page(owner)
        XCTAssertEqual(page.sections.map(\.title), ["播放控制", "单击手势", "双击手势", "滑动手势", "震动反馈"])
        XCTAssertEqual(page.sections.map { $0.rows.count }, [5, 2, 2, 4, 4])
        let fields: [(String, KeyPath<FilmPlayerPreferences, Bool>)] = [
            ("showsControlCenter", \.showsControlCenter), ("recognizesChapters", \.recognizesChapters),
            ("swipeVolume", \.swipeVolume), ("swipeBrightness", \.swipeBrightness),
            ("swipeSeek", \.swipeSeek), ("swipeEpisodes", \.swipeEpisodes),
            ("hapticHold", \.hapticHold), ("hapticBrightness", \.hapticBrightness),
            ("hapticVolume", \.hapticVolume), ("hapticSeek", \.hapticSeek),
        ]
        for (id, keyPath) in fields {
            let row = try XCTUnwrap(rows(page).first { $0.id == "film.player." + id })
            let content = try XCTUnwrap(row.content as? UIStackView)
            let toggle = try XCTUnwrap(content.arrangedSubviews.compactMap { $0 as? UISwitch }.first)
            XCTAssertTrue(toggle.isOn)
            let label = try XCTUnwrap(content.arrangedSubviews.compactMap { $0 as? UILabel }.first)
            XCTAssertFalse(label.font.fontDescriptor.symbolicTraits.contains(.traitBold))
            toggle.isOn = false
            toggle.sendActions(for: .valueChanged)
            appStorageTestEventually { !owner.preferences[keyPath: keyPath] }
            XCTAssertFalse(owner.preferences[keyPath: keyPath])
        }
    }

    func testTapMenusExcludeUnsupportedActionsAndPersistSelections() throws {
        let owner = try store(), page = page(owner)
        let navigation = UINavigationController(rootViewController: page)
        let expected: [(String, [String])] = [
            ("singleTapCenter", ["无操作", "显示/隐藏播放栏", "播放/暂停"]),
            ("singleTapSides", ["无操作", "显示/隐藏播放栏", "播放/暂停", "快进/快退"]),
            ("doubleTapCenter", ["无操作", "播放/暂停"]),
            ("doubleTapSides", ["无操作", "播放/暂停", "快进/快退"]),
        ]
        for (id, titles) in expected {
            let options = try choice(id, from: page, in: navigation)
            XCTAssertEqual(rows(options).map(\.title), titles)
            XCTAssertEqual(rows(options).filter(\.selected).count, 1)
            try XCTUnwrap(rows(options).first?.action)()
            appStorageTestEventually { self.rows(page).first { $0.id == "film.player." + id }?.detail == "无操作" }
            page.pageDidFinishAppearing()
            XCTAssertEqual(rows(page).first { $0.id == "film.player." + id }?.detail, "无操作")
        }
    }

    func testRateMenusRefreshByPreferredEngineAndShowTheSavedCorrection() throws {
        let owner = try store(), page = page(owner)
        let navigation = UINavigationController(rootViewController: page)
        XCTAssertTrue(try appStorageTestWait { await owner.set(\.defaultRate, 3) })
        XCTAssertTrue(try appStorageTestWait { await owner.set(\.holdRate, 8) })
        page.pageDidFinishAppearing()
        let defaults = try choice("defaultRate", from: page, in: navigation)
        XCTAssertEqual(rows(defaults).count, 5)
        XCTAssertEqual(rows(defaults).last?.title, "3X")
        XCTAssertTrue(try appStorageTestWait { await owner.set(\.engine, .aether) })
        defaults.pageDidFinishAppearing()
        XCTAssertEqual(rows(defaults).count, 4)
        XCTAssertEqual(rows(defaults).filter(\.selected).map(\.title), ["2X"])
        page.pageDidFinishAppearing()
        XCTAssertEqual(rows(page).first { $0.id == "film.player.defaultRate" }?.detail, "2X")
        XCTAssertEqual(rows(page).first { $0.id == "film.player.holdRate" }?.detail, "2X")
        let holds = try choice("holdRate", from: page, in: navigation)
        XCTAssertEqual(rows(holds).map(\.title), ["2X"])
        XCTAssertTrue(try appStorageTestWait { await owner.set(\.engine, .ksPlayer) })
        holds.pageDidFinishAppearing()
        XCTAssertEqual(rows(holds).map(\.title), ["2X", "3X", "4X", "6X", "8X"])
        XCTAssertEqual(rows(holds).filter(\.selected).map(\.title), ["2X"])
        let steps = try choice("seekStep", from: page, in: navigation)
        XCTAssertEqual(rows(steps).map(\.title), ["5 秒", "10 秒", "15 秒", "20 秒", "30 秒", "60 秒"])
        try XCTUnwrap(rows(steps).last?.action)()
        appStorageTestEventually { owner.preferences.seekStep == 60 }
        XCTAssertEqual(owner.preferences.seekStep, 60)
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
            let owner = try store()
            XCTAssertTrue(try appStorageTestWait { await owner.set(\.engine, engine) })
            let settings = page(owner, area: .settings)
            try await withForegroundPage(settings) { navigation in
                let engineCell = try visibleCell(IndexPath(row: 0, section: 0), on: settings)
                XCTAssertEqual(engineCell.accessibilityLabel, "默认内核")
                XCTAssertEqual(engineCell.accessibilityValue, engine.title)
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
                        if let content = controls.sections[section].rows[row].content as? UIStackView {
                            let label = try XCTUnwrap(content.arrangedSubviews.compactMap { $0 as? UILabel }.first)
                            let toggle = try XCTUnwrap(content.arrangedSubviews.compactMap { $0 as? UISwitch }.first)
                            XCTAssertEqual(label.text, field.1)
                            XCTAssertEqual(toggle.accessibilityIdentifier, "film.player.\(field.0).switch")
                            XCTAssertTrue(toggle.isOn && toggle.isEnabled)
                            XCTAssertGreaterThan(label.bounds.width, 0)
                            XCTAssertTrue(cell.contentView.bounds.insetBy(dx: -1, dy: -1)
                                .contains(label.convert(label.bounds, to: cell.contentView)))
                            XCTAssertLessThanOrEqual(label.sizeThatFits(CGSize(width: label.bounds.width,
                                height: .greatestFiniteMagnitude)).height, label.bounds.height + 1)
                            XCTAssertLessThanOrEqual(label.convert(label.bounds, to: cell).maxX,
                                                    toggle.convert(toggle.bounds, to: cell).minX)
                            XCTAssertTrue(cell.contentView.bounds.insetBy(dx: -1, dy: -1)
                                .contains(toggle.convert(toggle.bounds, to: cell.contentView)))
                            assertRegular(label.font)
                        } else {
                            let content = try XCTUnwrap(cell.contentConfiguration as? UIListContentConfiguration)
                            XCTAssertEqual(content.text, field.1)
                            assertRegular(content.textProperties.font)
                            assertRegular(content.secondaryTextProperties.font)
                            XCTAssertEqual(cell.accessoryType, .disclosureIndicator)
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
        window.windowLevel = .normal + 1
        window.rootViewController = navigation
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
