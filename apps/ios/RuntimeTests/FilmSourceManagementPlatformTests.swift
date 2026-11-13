#if os(iOS)
import XCTest
import UIKit
import Combine
import MeowRuntime
import MeowStorage
@testable import MeowFilmFeature
@testable import MeowUI

@MainActor
final class FilmSourceManagementPlatformTests: XCTestCase {
    func testSourceRowsExposeSwitchEditAndOneMenuAndOpenNamedDetails() async throws {
        let fixture = try makeFixture()
        try await fixture.service.refresh()
        let theme = SourceListTheme()
        let cats = FilmCatSourcesViewController(service: fixture.service, themeProvider: theme)
        let navigation = PageNavigationController(themeProvider: theme)
        navigation.setViewControllers([cats], animated: false)
        let window = try await host(navigation)
        defer { unhost(window) }
        await eventually { cats.snapshot?.cats.count == 2 }
        let content = try ManagementListTestUI.cell("film.cat.a", in: cats.manager).contentView
        let toggle = try XCTUnwrap(descendants(content).compactMap { $0 as? UISwitch }.first)
        let more = try XCTUnwrap(descendants(content).compactMap { $0 as? UIButton }.first { $0.menu != nil })
        XCTAssertEqual(descendants(content).compactMap { $0 as? UIButton }.filter { $0.menu != nil }.count, 1)
        XCTAssertEqual(more.menu?.children.compactMap { ($0 as? UIAction)?.title }, ["更新", "重启"])
        XCTAssertTrue(toggle.isOn)
        toggle.setOn(false, animated: false); toggle.sendActions(for: .valueChanged)
        await eventually { cats.snapshot?.cats.first?.enabled == false && !cats.busy }
        XCTAssertEqual(cats.snapshot?.videos.count, 2, "Disabling a parent preserves its video sources")
        let editingContent = try ManagementListTestUI.cell("film.cat.a", in: cats.manager).contentView
        let edit = try ManagementListTestUI.button("management.edit.film.cat.a", in: editingContent)
        edit.sendActions(for: .touchUpInside)
        await eventually { (navigation.topViewController as? FilmCatEditorController)?.canPresentPageAlert == true }
        let editor = try XCTUnwrap(navigation.topViewController as? FilmCatEditorController)
        if case .edit(let cat) = editor.mode { XCTAssertEqual(cat.installationId, "a") }
        else { XCTFail("The row edit button must open the requested source editor") }
        navigation.popViewController(animated: false)
        await eventually { cats.canPresentPageAlert }
        try ManagementListTestUI.open("film.cat.a", in: cats.manager)
        await eventually { navigation.topViewController is FilmCatDetailController }
        let detail = try XCTUnwrap(navigation.topViewController as? FilmCatDetailController)
        await eventually { detail.pageTitle == "来源 a" && detail.canPresentPageAlert }
        XCTAssertFalse(detail.sections.flatMap(\.rows).contains { $0.title == detail.pageTitle })
        navigation.popViewController(animated: false)
        await eventually { cats.isPageVisible }
        let videos = FilmVideoSourcesViewController(service: fixture.service, themeProvider: theme)
        navigation.pushViewController(videos, animated: false)
        await eventually { videos.snapshot?.videos.count == 2 }
        let videoContent = try ManagementListTestUI.cell("film.video.b.site", in: videos.manager).contentView
        let videoSwitch = try XCTUnwrap(descendants(videoContent).compactMap { $0 as? UISwitch }.first)
        let videoMenus = descendants(videoContent).compactMap { $0 as? UIButton }.filter { $0.menu != nil }
        XCTAssertEqual(videoMenus.count, 1)
        XCTAssertEqual(videoMenus.first?.menu?.children.compactMap { ($0 as? UIAction)?.title }, ["检测"])
        let videoEdit = try ManagementListTestUI.button("management.edit.film.video.b.site", in: videoContent)
        XCTAssertTrue(videoEdit.isEnabled)
        videoSwitch.setOn(false, animated: false); videoSwitch.sendActions(for: .valueChanged)
        await eventually { videos.snapshot?.videos.first { $0.key.installationId == "b" }?.options.enabled == false && !videos.busy }
        try ManagementListTestUI.open("film.video.b.site", in: videos.manager)
        await eventually { navigation.topViewController is FilmVideoDetailController }
        let videoDetail = try XCTUnwrap(navigation.topViewController as? FilmVideoDetailController)
        await eventually { videoDetail.pageTitle == "视频 b" }
        XCTAssertNotNil(try row(videoDetail, "film.video.order").action)
        XCTAssertFalse(videoDetail.sections.flatMap(\.rows).contains { $0.title == videoDetail.pageTitle })
        XCTAssertFalse(videoDetail.sections.flatMap(\.rows).contains { $0.title == "删除" })
        await fixture.service.close()
    }

    func testCatDetailEditAndLocalUpdateStayAtFourthLevelAndReturnToList() async throws {
        for action in ["edit", "update"] {
            let fixture = try makeFixture()
            if action == "update" { await fixture.runtime.useLocalSource("a") }
            try await fixture.service.refresh()
            let theme = SourceListTheme()
            let page = FilmCatSourcesViewController(service: fixture.service, themeProvider: theme)
            let root = UIViewController(), settings = UIViewController()
            let navigation = PageNavigationController(themeProvider: theme)
            navigation.setViewControllers([root, settings, page], animated: false)
            let window = try await host(navigation)
            await eventually { page.snapshot?.cats.count == 2 && page.canPresentPageAlert }
            try ManagementListTestUI.open("film.cat.a", in: page.manager)
            await eventually { (navigation.topViewController as? FilmCatDetailController)?.canPresentPageAlert == true &&
                (navigation.topViewController as? FilmCatDetailController)?.snapshot?.cats.count == 2 }
            let detail = try XCTUnwrap(navigation.topViewController as? FilmCatDetailController)
            XCTAssertEqual(navigation.viewControllers.count, 4)
            try row(detail, "film.cat.\(action)").action?()
            await eventually { (navigation.topViewController as? FilmCatEditorController)?.canPresentPageAlert == true &&
                !detail.isPageActive }
            let editor = try XCTUnwrap(navigation.topViewController as? FilmCatEditorController)
            XCTAssertEqual(navigation.viewControllers.count, 4)
            XCTAssertTrue(navigation.viewControllers[0] === root)
            XCTAssertTrue(navigation.viewControllers[1] === settings)
            XCTAssertTrue(navigation.viewControllers[2] === page)
            XCTAssertFalse(navigation.viewControllers.contains { $0 === detail })
            if action == "edit", case .edit(let cat) = editor.mode {
                XCTAssertEqual(cat.installationId, "a")
                XCTAssertEqual(editor.name.text, "来源 a")
            } else if action == "update", case .updateLocal(let cat) = editor.mode {
                XCTAssertEqual(cat.installationId, "a")
                XCTAssertNotNil(try row(editor, "film.cat.editor.file").action)
            } else {
                XCTFail("The fourth level must retain the requested edit/update operation")
            }
            XCTAssertTrue(try row(editor, "film.cat.editor.save").enabled)
            let calls = await fixture.runtime.mutations
            XCTAssertTrue(calls.isEmpty, "Opening an editor does not save")
            navigation.popViewController(animated: false)
            await eventually { page.canPresentPageAlert && !editor.isPageActive }
            XCTAssertTrue(navigation.topViewController === page)
            XCTAssertEqual(navigation.viewControllers.count, 3)
            unhost(window)
            await fixture.service.close()
        }
    }

    func testVideoOrderUsesSharedRowsAndPreservesCurrentSourceOptions() async throws {
        let fixture = try makeFixture()
        try await fixture.service.refresh()
        let initial = await fixture.service.snapshot
        let first = try XCTUnwrap(initial.videos.first)
        var options = first.options
        options.enabled = false; options.homeVisible = false; options.aggregateImages = true
        try await fixture.service.saveVideoOptions(first.key, options: options)
        let saved = await fixture.service.snapshot
        let theme = SourceListTheme()
        let page = FilmVideoSourcesViewController(service: fixture.service, themeProvider: theme)
        let navigation = PageNavigationController(themeProvider: theme)
        navigation.setViewControllers([page], animated: false)
        let window = try await host(navigation); defer { unhost(window) }
        await eventually { page.snapshot?.videos.count == 2 }
        let sort = try ManagementListTestUI.button("film.videos.sort", in: page.manager)
        XCTAssertTrue(sort.isEnabled); sort.sendActions(for: .touchUpInside)
        await eventually { (navigation.topViewController as? FilmVideoSourceOrderController)?.canPresentPageAlert == true }
        let order = try XCTUnwrap(navigation.topViewController as? FilmVideoSourceOrderController)
        XCTAssertEqual(order.pageTitle, "视频源排序")
        XCTAssertEqual(order.sections.flatMap(\.rows).map(\.title), saved.videos.map(\.name))
        let down = try XCTUnwrap(descendants(order.view).compactMap { $0 as? PageHeaderButton }.first {
            $0.accessibilityIdentifier == "film.video.order.0.down"
        })
        XCTAssertTrue(down.isEnabled); down.sendActions(for: .touchUpInside)
        await eventually {
            order.sections.flatMap(\.rows).map(\.title) == saved.videos.reversed().map(\.name) &&
            self.descendants(order.view).compactMap { $0 as? PageHeaderButton }.contains {
                $0.accessibilityIdentifier == "film.video.order.0.down" && $0.isEnabled
            }
        }
        let reordered = await fixture.service.snapshot
        XCTAssertEqual(reordered.videos.map(\.key), saved.videos.reversed().map(\.key))
        for source in reordered.videos {
            XCTAssertEqual(source.options, saved.videos.first { $0.key == source.key }?.options)
        }
        for cell in order.table.visibleCells { XCTAssertTrue(cell is SettingsTableCell) }
        navigation.popViewController(animated: false)
        await eventually { page.snapshot?.videos == reordered.videos && page.canPresentPageAlert }
        XCTAssertEqual(ManagementListTestUI.rowIDs(in: page.manager),
                       reordered.videos.map { "management.row.film.video.\($0.key.installationId).\($0.key.siteKey)" })

        let target = try XCTUnwrap(reordered.videos.first)
        try ManagementListTestUI.open("film.video.\(target.key.installationId).\(target.key.siteKey)", in: page.manager)
        await eventually { navigation.topViewController is FilmVideoDetailController }
        let detail = try XCTUnwrap(navigation.topViewController as? FilmVideoDetailController)
        await eventually { detail.sections.flatMap(\.rows).contains { $0.id == "film.video.order" } }
        try row(detail, "film.video.order").action?()
        let dialog = try XCTUnwrap(detail.presentedViewController as? ManagementDialog)
        let input = try XCTUnwrap(descendants(dialog.view).first {
            $0.accessibilityIdentifier == "film.video.order.input"
        } as? UITextField)
        await eventually { dialog.view.window != nil && !dialog.isBeingPresented }
        XCTAssertEqual(input.text, String(target.order))
        XCTAssertEqual(input.backgroundColor, UIColor(detail.theme.settings.input))
        XCTAssertEqual(input.layer.borderColor, UIColor(detail.theme.settings.inputBorder).cgColor)
        input.text = "not-an-integer"
        try XCTUnwrap(descendants(dialog.view).compactMap { $0 as? UIButton }.first {
            $0.accessibilityIdentifier == "management.dialog.confirm"
        }).sendActions(for: .touchUpInside)
        await eventually {
            (self.descendants(dialog.view).first {
                $0.accessibilityIdentifier == "management.dialog.error"
            } as? UILabel)?.text == "排序必须是整数"
        }
        XCTAssertTrue(detail.presentedViewController === dialog)
        input.text = "7"
        try XCTUnwrap(descendants(dialog.view).compactMap { $0 as? UIButton }.first {
            $0.accessibilityIdentifier == "management.dialog.confirm"
        }).sendActions(for: .touchUpInside)
        await eventually {
            detail.presentedViewController == nil &&
            detail.snapshot?.videos.first { $0.key == target.key }?.order == 7
        }
        await fixture.service.close()
    }

    func testNetworkImportFloatsOverRetainedSourceListAndCancelRestoresIt() async throws {
        let fixture = try makeFixture()
        try await fixture.service.refresh()
        let theme = SourceListTheme()
        let page = FilmCatSourcesViewController(service: fixture.service, themeProvider: theme)
        let navigation = PageNavigationController(themeProvider: theme)
        navigation.setViewControllers([page], animated: false)
        let window = try await host(navigation); defer { unhost(window) }
        await eventually { page.snapshot?.cats.count == 2 && page.canPresentPageAlert }
        let background = page.view
        let sourceIDs = ManagementListTestUI.rowIDs(in: page.manager)
        XCTAssertEqual(sourceIDs.count, 2)
        page.importSource(local: false)
        await eventually {
            (page.presentedViewController as? FilmFloatingEditorController)?.viewIfLoaded?.window != nil &&
            page.presentedViewController?.isBeingPresented == false
        }
        let modal = try XCTUnwrap(page.presentedViewController as? FilmFloatingEditorController)
        let editor = try XCTUnwrap(modal.children.compactMap { $0 as? FilmCatEditorController }.first)
        XCTAssertEqual(modal.modalPresentationStyle, .overFullScreen)
        XCTAssertTrue(navigation.topViewController === page)
        XCTAssertTrue(page.view === background)
        XCTAssertTrue(page.view.isDescendant(of: window))
        XCTAssertFalse(page.view.isHidden)
        XCTAssertEqual(ManagementListTestUI.rowIDs(in: page.manager), sourceIDs)
        let color = try XCTUnwrap(modal.view.backgroundColor)
        var white: CGFloat = 0, alpha: CGFloat = 0
        XCTAssertTrue(color.getWhite(&white, alpha: &alpha))
        XCTAssertLessThan(alpha, 1)
        let card = try XCTUnwrap(descendants(modal.view).first {
            $0.accessibilityIdentifier == "film.cat.networkImport.card"
        })
        modal.view.layoutIfNeeded()
        XCTAssertGreaterThan(card.bounds.width, 0)
        XCTAssertLessThanOrEqual(card.frame.maxX, modal.view.safeAreaLayoutGuide.layoutFrame.maxX)
        XCTAssertGreaterThanOrEqual(card.frame.minX, modal.view.safeAreaLayoutGuide.layoutFrame.minX)
        XCTAssertTrue(editor.hasOwnPageHeader)
        XCTAssertEqual(editor.pageHeader.title, "导入猫源")
        editor.sourceURL.becomeFirstResponder()
        await eventually { editor.sourceURL.isFirstResponder }
        modal.view.layoutIfNeeded()
        XCTAssertLessThanOrEqual(card.frame.maxY, modal.view.keyboardLayoutGuide.layoutFrame.minY)
        let close = try XCTUnwrap(descendants(editor.pageHeader).compactMap { $0 as? PageHeaderButton }.first {
            $0.accessibilityLabel == "关闭"
        })
        close.sendActions(for: .touchUpInside)
        await eventually { page.presentedViewController == nil && page.canPresentPageAlert }
        XCTAssertTrue(navigation.topViewController === page)
        XCTAssertEqual(ManagementListTestUI.rowIDs(in: page.manager), sourceIDs)
        let mutations = await fixture.runtime.mutations
        XCTAssertTrue(mutations.isEmpty)
        await fixture.service.close()
    }

    func testLeavingBatchSettlesAcceptedWriteAndDoesNotSubmitNextSource() async throws {
        for (operation, action) in [("uninstall", 4), ("enable", 0), ("restart", 3), ("update", 2)] {
            let fixture = try makeFixture()
            try await fixture.service.refresh()
            let theme = SourceListTheme()
            let page = FilmCatSourcesViewController(service: fixture.service, themeProvider: theme)
            let navigation = PageNavigationController(themeProvider: theme)
            navigation.setViewControllers([page], animated: false)
            let window = try await host(navigation)
            await eventually { page.snapshot?.cats.count == 2 && page.canPresentPageAlert }
            try ManagementListTestUI.button("management.selectAll", in: page.manager).sendActions(for: .touchUpInside)
            let entered = expectation(description: operation + " admitted")
            await fixture.runtime.pause(operation) { entered.fulfill() }
            if action == 4 { page.manager.deleteButton.sendActions(for: .touchUpInside) }
            else {
                try ManagementListTestUI.activateMenu(
                    ["启用所选", "停用所选", "更新所选", "重启所选"][action],
                    on: page.manager.footerMore)
            }
            if action == 4 {
                await eventually { (page.presentedViewController as? UIAlertController)?.isBeingPresented == false }
                page.selectAlertChoice("确认")
            }
            await fulfillment(of: [entered], timeout: 5)
            // Also covers a quick return: the old batch cannot resume merely
            // because the same retained list becomes visible again.
            page.pageDidBecomeHidden()
            await fixture.runtime.release()
            await eventually { !page.busy }
            let calls = await fixture.runtime.mutations
            XCTAssertEqual(calls.map(\.0), [operation])
            XCTAssertEqual(calls.map(\.1), ["a"])
            let saved = await fixture.service.snapshot
            if action == 4 { XCTAssertEqual(saved.cats.map(\.installationId), ["b"]) }
            if action == 2 { XCTAssertEqual(saved.cats.first?.updateState, "updated") }
            unhost(window)
            await fixture.service.close()
        }
    }

    func testLocalBatchSaveHiddenOrBackgroundedCannotResumeAfterQuickReturnOrRetry() async throws {
        for background in [false, true] {
            for fail in [false, true] {
                let fixture = try makeFixture()
                await fixture.runtime.useLocalSource("a"); await fixture.runtime.useLocalSource("b")
                try await fixture.service.refresh()
                let theme = SourceListTheme()
                let page = FilmCatSourcesViewController(service: fixture.service, themeProvider: theme)
                let navigation = PageNavigationController(themeProvider: theme)
                navigation.setViewControllers([page], animated: false)
                let window = try await host(navigation)
                defer { unhost(window) }
                await eventually { page.snapshot?.cats.count == 2 && page.canPresentPageAlert }
                try ManagementListTestUI.button("management.selectAll", in: page.manager).sendActions(for: .touchUpInside)
                try ManagementListTestUI.activateMenu("更新所选", on: page.manager.footerMore)
                await eventually { (navigation.topViewController as? FilmCatEditorController)?.canPresentPageAlert == true }
                let editor = try XCTUnwrap(navigation.topViewController as? FilmCatEditorController)
                let file = fixture.base.appendingPathComponent("update.js")
                try Data("// changed source".utf8).write(to: file)
                editor.documentPicker(UIDocumentPickerViewController(forOpeningContentTypes: [.javaScript]),
                                      didPickDocumentsAt: [file])
                await eventually { editor.file != nil && (try? self.row(editor, "film.cat.editor.save").enabled) == true }
                let entered = expectation(description: "local update handed off")
                await fixture.runtime.pause("update") { entered.fulfill() }
                if fail { await fixture.runtime.fail("update", id: "a") }
                try row(editor, "film.cat.editor.save").action?()
                await fulfillment(of: [entered], timeout: 5)
                if background {
                    NotificationCenter.default.post(name: UIApplication.willResignActiveNotification, object: nil)
                    XCTAssertFalse(editor.isPageVisible)
                    NotificationCenter.default.post(name: UIApplication.didBecomeActiveNotification, object: nil)
                } else {
                    editor.beginAppearanceTransition(false, animated: false); editor.endAppearanceTransition()
                    XCTAssertFalse(editor.isPageVisible)
                    editor.beginAppearanceTransition(true, animated: false); editor.endAppearanceTransition()
                }
                await eventually { editor.canPresentPageAlert }
                XCTAssertTrue(editor.isPageActive)
                await fixture.runtime.release()
                await eventually { (try? self.row(editor, "film.cat.editor.save").title) == "保存" }
                XCTAssertTrue(navigation.topViewController === editor)
                XCTAssertNil(editor.onSaved, "An invalidated handoff must not be revived by a retry")
                if fail {
                    await eventually { (editor.presentedViewController as? UIAlertController)?.title == "错误" &&
                        (editor.presentedViewController as? UIAlertController)?.isBeingPresented == false }
                    XCTAssertNotNil(editor.file, "A failed save retains the selected file")
                    editor.selectAlertChoice("重试")
                    await eventually { (editor.presentedViewController as? UIAlertController)?.title != "错误" &&
                        (try? self.row(editor, "film.cat.editor.save").title) == "保存" &&
                        page.snapshot?.cats.first { $0.installationId == "a" }?.updateState == "updated" }
                }
                let calls = await fixture.runtime.mutations
                XCTAssertEqual(calls.map(\.0), fail ? ["update", "update"] : ["update"])
                XCTAssertEqual(calls.map(\.1), fail ? ["a", "a"] : ["a"])
                let saved = await fixture.service.snapshot
                XCTAssertEqual(saved.cats.first { $0.installationId == "a" }?.updateState, "updated")
                XCTAssertEqual(saved.cats.first { $0.installationId == "b" }?.updateState, "idle")
                XCTAssertTrue(navigation.topViewController === editor)
                XCTAssertEqual(navigation.viewControllers.count, 2)
                await fixture.service.close()
            }
        }
    }

    func testLocalBatchContinuesAfterFilePickerHidesEditorBeforeSave() async throws {
        for secondLocal in [false, true] {
            let fixture = try makeFixture()
            await fixture.runtime.useLocalSource("a")
            if secondLocal { await fixture.runtime.useLocalSource("b") }
            try await fixture.service.refresh()
            let theme = SourceListTheme()
            let page = FilmCatSourcesViewController(service: fixture.service, themeProvider: theme)
            let navigation = PageNavigationController(themeProvider: theme)
            navigation.setViewControllers([page], animated: false)
            let window = try await host(navigation)
            defer { unhost(window) }
            await eventually { page.snapshot?.cats.count == 2 && page.canPresentPageAlert }
            try ManagementListTestUI.button("management.selectAll", in: page.manager).sendActions(for: .touchUpInside)
            try ManagementListTestUI.activateMenu("更新所选", on: page.manager.footerMore)
            await eventually { (navigation.topViewController as? FilmCatEditorController)?.canPresentPageAlert == true }
            let editor = try XCTUnwrap(navigation.topViewController as? FilmCatEditorController)
            let file = fixture.base.appendingPathComponent("update.js")
            try Data("// changed source".utf8).write(to: file)
            // A full-screen document provider can hide the editor before a save
            // exists. Its ordinary return must keep the batch callback intact.
            editor.beginAppearanceTransition(false, animated: false); editor.endAppearanceTransition()
            XCTAssertFalse(editor.isPageVisible)
            editor.documentPicker(UIDocumentPickerViewController(forOpeningContentTypes: [.javaScript]),
                                  didPickDocumentsAt: [file])
            await eventually { editor.file != nil && (try? self.row(editor, "film.cat.editor.save").enabled) == true }
            editor.beginAppearanceTransition(true, animated: false); editor.endAppearanceTransition()
            await eventually { editor.canPresentPageAlert }
            XCTAssertNotNil(editor.onSaved)
            try row(editor, "film.cat.editor.save").action?()
            if secondLocal {
                await eventually { navigation.topViewController !== editor &&
                    (navigation.topViewController as? FilmCatEditorController)?.canPresentPageAlert == true }
                let next = try XCTUnwrap(navigation.topViewController as? FilmCatEditorController)
                guard case .updateLocal(let cat) = next.mode else { return XCTFail("Expected the next local update") }
                XCTAssertEqual(cat.installationId, "b")
                next.documentPicker(UIDocumentPickerViewController(forOpeningContentTypes: [.javaScript]),
                                    didPickDocumentsAt: [file])
                await eventually { next.file != nil && (try? self.row(next, "film.cat.editor.save").enabled) == true }
                try row(next, "film.cat.editor.save").action?()
            }
            await eventually { navigation.topViewController === page && !page.busy &&
                page.snapshot?.cats.allSatisfy { $0.updateState == "updated" } == true }
            let calls = await fixture.runtime.mutations
            XCTAssertEqual(calls.map(\.0), ["update", "update"])
            XCTAssertEqual(calls.map(\.1), ["a", "b"])
            XCTAssertEqual(navigation.viewControllers.count, 1)
            await fixture.service.close()
        }
    }

    func testCatBatchEnableAndDisableRetryOnlyUnconfirmedItemsAndCancelStops() async throws {
        for action in [0, 1] {
            for choice in ["重试", "取消"] {
                let fixture = try makeFixture()
                try await fixture.service.refresh()
                await fixture.runtime.fail("enable", id: "b")
                let page = FilmCatSourcesViewController(service: fixture.service, themeProvider: SourceListTheme())
                let window = try await host(page)
                await eventually { page.snapshot?.cats.count == 2 && page.canPresentPageAlert }
                try ManagementListTestUI.button("management.selectAll", in: page.manager).sendActions(for: .touchUpInside)
                try ManagementListTestUI.activateMenu(action == 0 ? "启用所选" : "停用所选",
                                                     on: page.manager.footerMore)
                await eventually { !page.busy && (page.presentedViewController as? UIAlertController)?.title == "错误" }
                let initial = await fixture.runtime.mutations
                XCTAssertEqual(initial.map(\.0), ["enable", "enable"])
                XCTAssertEqual(initial.map(\.1), ["a", "b"])
                await eventually { (page.presentedViewController as? UIAlertController)?.isBeingPresented == false }
                page.selectAlertChoice(choice)
                await eventually { !page.busy && page.presentedViewController == nil }
                let calls = await fixture.runtime.mutations
                XCTAssertEqual(calls.map(\.1), choice == "重试" ? ["a", "b", "b"] : ["a", "b"])
                let saved = await fixture.service.snapshot
                XCTAssertEqual(saved.cats.map(\.enabled), [action == 0, choice == "重试" ? action == 0 : true])
                XCTAssertEqual(saved.videos.count, 2)
                unhost(window)
                await fixture.service.close()
            }
        }
    }

    func testVideoBatchDetectionRetriesOnlyUnconfirmedSources() async throws {
        let fixture = try makeFixture()
        try await fixture.service.refresh()
        await fixture.runtime.fail("detect", id: "b")
        let page = FilmVideoSourcesViewController(service: fixture.service, themeProvider: SourceListTheme())
        let window = try await host(page)
        defer { unhost(window) }
        await eventually { page.snapshot?.videos.count == 2 && page.canPresentPageAlert }
        try ManagementListTestUI.button("management.selectAll", in: page.manager).sendActions(for: .touchUpInside)
        try ManagementListTestUI.activateMenu("检测所选", on: page.manager.footerMore)
        await eventually { !page.detecting && (page.presentedViewController as? UIAlertController)?.title == "错误" }
        let initial = await fixture.runtime.probes
        XCTAssertEqual(initial, ["a", "b"])
        let partial = await fixture.service.snapshot
        XCTAssertNotNil(partial.videos.first { $0.key.installationId == "a" }?.detection)
        XCTAssertNil(partial.videos.first { $0.key.installationId == "b" }?.detection)
        try await fixture.service.refresh()
        await eventually { (page.presentedViewController as? UIAlertController)?.isBeingPresented == false }
        page.selectAlertChoice("重试")
        await eventually { !page.detecting && page.presentedViewController == nil &&
            page.snapshot?.videos.allSatisfy { $0.detection != nil } == true }
        let calls = await fixture.runtime.probes
        XCTAssertEqual(calls, ["a", "b", "b"])
        await fixture.service.close()
    }

    func testLeavingVideoBatchDetectionCancelsCurrentProbeAndDoesNotSubmitNextSource() async throws {
        let fixture = try makeFixture()
        try await fixture.service.refresh()
        let page = FilmVideoSourcesViewController(service: fixture.service, themeProvider: SourceListTheme())
        let window = try await host(page)
        defer { unhost(window) }
        await eventually { page.snapshot?.videos.count == 2 && page.canPresentPageAlert }
        try ManagementListTestUI.button("management.selectAll", in: page.manager).sendActions(for: .touchUpInside)
        let entered = expectation(description: "probe admitted")
        await fixture.runtime.pause("invoke") { entered.fulfill() }
        try ManagementListTestUI.activateMenu("检测所选", on: page.manager.footerMore)
        await fulfillment(of: [entered], timeout: 5)
        page.pageDidBecomeHidden()
        XCTAssertFalse(page.detecting)
        await fixture.runtime.release()
        await fixture.service.prepareShutdown()
        let calls = await fixture.runtime.probes
        XCTAssertEqual(calls, ["a"])
        let saved = await fixture.service.snapshot
        XCTAssertTrue(saved.videos.allSatisfy { $0.detection == nil })
        await fixture.service.close()
    }

    func testNarrowLargeTypeSourceControlsKeepReaderLayoutAndCheckboxSelection() async throws {
        let fixture = try makeFixture()
        try await fixture.service.refresh()
        let page = FilmCatSourcesViewController(service: fixture.service, themeProvider: SourceListTheme())
        page.traitOverrides.preferredContentSizeCategory = .accessibilityExtraExtraExtraLarge
        let window = try await host(page, width: 320)
        defer { unhost(window) }
        await eventually { page.snapshot?.cats.count == 2 }
        let table = page.manager.table
        let cell = try ManagementListTestUI.cell("film.cat.a", in: page.manager)
        XCTAssertEqual(table.frame.minX, 24, accuracy: 0.5)
        XCTAssertEqual(table.layer.cornerRadius, 22)
        XCTAssertEqual(table.separatorStyle, .singleLine)
        XCTAssertEqual(table.backgroundColor, UIColor(page.theme.settings.surface))
        let controls = descendants(cell.contentView).compactMap { $0 as? UIControl }
            .filter { $0.accessibilityIdentifier?.hasSuffix(".film.cat.a") == true }
        XCTAssertEqual(Set(controls.compactMap(\.accessibilityIdentifier)),
                       ["management.select.film.cat.a", "management.enabled.film.cat.a",
                        "management.edit.film.cat.a", "management.more.film.cat.a"])
        for control in controls {
            let frame = control.convert(control.bounds, to: cell.contentView)
            XCTAssertGreaterThanOrEqual(frame.minX, -1)
            XCTAssertLessThanOrEqual(frame.maxX, cell.contentView.bounds.width + 1)
            XCTAssertGreaterThan(control.bounds.width, 0)
            XCTAssertGreaterThan(control.bounds.height, 0)
        }
        let edit = try XCTUnwrap(controls.compactMap { $0 as? UIButton }
            .first { $0.accessibilityIdentifier == "management.edit.film.cat.a" })
        let more = try XCTUnwrap(controls.compactMap { $0 as? UIButton }
            .first { $0.accessibilityIdentifier == "management.more.film.cat.a" })
        XCTAssertEqual(edit.bounds.size, CGSize(width: 38, height: 38))
        XCTAssertEqual(more.bounds.size, CGSize(width: 38, height: 38))
        XCTAssertEqual(more.convert(more.bounds, to: cell.contentView).minX -
            edit.convert(edit.bounds, to: cell.contentView).maxX, 4, accuracy: 0.5)
        XCTAssertEqual(edit.tintColor, UIColor(page.theme.settings.accent))
        XCTAssertEqual(more.tintColor, UIColor(page.theme.settings.accent))
        XCTAssertEqual(more.image(for: .normal)?.pngData(), ManagementIcon.more.pngData())
        let labels = descendants(cell.contentView).compactMap { $0 as? UILabel }
        let title = try XCTUnwrap(labels.first { $0.text == "来源 a" })
        let summary = try XCTUnwrap(labels.first { $0 !== title && !($0.text ?? "").isEmpty })
        XCTAssertEqual(title.font.pointSize,
            UIFontMetrics.default.scaledFont(for: .systemFont(ofSize: 14), compatibleWith: cell.traitCollection).pointSize)
        XCTAssertEqual(summary.font.pointSize,
            UIFontMetrics.default.scaledFont(for: .systemFont(ofSize: 11), compatibleWith: cell.traitCollection).pointSize)
        XCTAssertTrue(title.adjustsFontForContentSizeCategory)
        XCTAssertTrue(summary.adjustsFontForContentSizeCategory)
        let attachment = XCTAttachment(image: UIGraphicsImageRenderer(bounds: page.view.bounds).image { _ in
            page.view.drawHierarchy(in: page.view.bounds, afterScreenUpdates: true)
        })
        attachment.name = "Source list 320 AXXXL"; attachment.lifetime = .keepAlways; add(attachment)
        let toggle = try XCTUnwrap(controls.compactMap { $0 as? UISwitch }.first)
        XCTAssertEqual(toggle.transform, CGAffineTransform(scaleX: 0.7, y: 0.7))
        try ManagementListTestUI.select("film.cat.a", in: page.manager)
        XCTAssertEqual(page.manager.selected, ["film.cat.a"])
        XCTAssertTrue(page.manager.deleteButton.isEnabled)
        XCTAssertTrue(edit.isEnabled, "Selection must not replace the existing edit/toggle/more elements")
        await fixture.service.close()
    }

    private func makeFixture() throws -> SourceListFixture {
        let fixture = try SourceListFixture()
        addTeardownBlock { await fixture.service.close(); try FileManager.default.removeItem(at: fixture.base) }
        return fixture
    }
    private func row(_ page: FilmSettingsListController, _ id: String) throws -> FilmSettingsRow {
        try XCTUnwrap(page.sections.flatMap(\.rows).first { $0.id == id })
    }
    private func descendants(_ view: UIView) -> [UIView] { [view] + view.subviews.flatMap(descendants) }
    private func eventually(_ predicate: @escaping @MainActor () -> Bool, file: StaticString = #filePath, line: UInt = #line) async {
        let expected = expectation(for: NSPredicate { _, _ in MainActor.assumeIsolated { predicate() } }, evaluatedWith: nil)
        await fulfillment(of: [expected], timeout: 5)
        XCTAssertTrue(predicate(), file: file, line: line)
    }
    private var previousWindow: UIWindow?
    private func host(_ controller: UIViewController, width: CGFloat? = nil) async throws -> UIWindow {
        await eventually { UIApplication.shared.applicationState == .active }
        let scene = try XCTUnwrap(UIApplication.shared.connectedScenes.compactMap { $0 as? UIWindowScene }
            .first { $0.activationState == .foregroundActive })
        previousWindow = scene.windows.first { $0.isKeyWindow }
        let window = UIWindow(windowScene: scene)
        window.frame = scene.coordinateSpace.bounds; window.windowLevel = .normal + 1
        let container = UIViewController()
        container.addChild(controller); container.view.addSubview(controller.view)
        controller.view.translatesAutoresizingMaskIntoConstraints = false
        NSLayoutConstraint.activate([
            controller.view.topAnchor.constraint(equalTo: container.view.topAnchor),
            controller.view.leadingAnchor.constraint(equalTo: container.view.leadingAnchor),
            controller.view.widthAnchor.constraint(equalToConstant: width ?? window.bounds.width),
            controller.view.heightAnchor.constraint(equalTo: container.view.heightAnchor),
        ])
        controller.didMove(toParent: container)
        window.rootViewController = container; window.makeKeyAndVisible()
        window.layoutIfNeeded()
        let page = (controller as? UINavigationController)?.topViewController as? PageViewController
            ?? controller as? PageViewController
        await eventually { page?.canPresentPageAlert == true }
        return window
    }
    private func unhost(_ window: UIWindow) {
        if let root = window.rootViewController { PageViewController.invalidatePages(in: root) }
        window.isHidden = true; window.rootViewController = nil
        previousWindow?.makeKey(); previousWindow = nil
    }
}

@MainActor
private final class SourceListTheme: PageThemeProviding {
    let pagePalette = PagePalette.preset(.lilac)
    let animatesPageColorway = false
    let pageThemeChanges = Empty<Void, Never>().eraseToAnyPublisher()
}

private struct SourceListFixture {
    let base: URL
    let runtime: SourceListRuntime
    let service: FilmSourceService
    init() throws {
        base = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: base, withIntermediateDirectories: true)
        let store = try DocumentStore(baseURL: base, namespace: FilmSourceService.documentNamespace)
        let runtime = SourceListRuntime(); self.runtime = runtime
        service = FilmSourceService(transport: .init(
            request: { try await runtime.call($0, $1) },
            upload: { try await runtime.upload($0, $1, $2) }),
            storeFactory: { store })
    }
}

private actor SourceListRuntime {
    private var removed = Set<String>()
    private var disabled = Set<String>()
    private var updated = Set<String>()
    private var local = Set<String>()
    private var paused: String?
    private var entered: (@Sendable () -> Void)?
    private var waiter: CheckedContinuation<Void, Never>?
    private var failure: (operation: String, id: String)?
    private var probed: String?
    private var candidates: [String: Int] = [:]
    private(set) var mutations: [(String, String)] = []
    private(set) var probes: [String] = []
    func useLocalSource(_ id: String) { local.insert(id) }
    func fail(_ operation: String, id: String) { failure = (operation, id) }
    func pause(_ operation: String, entered: @escaping @Sendable () -> Void) {
        paused = operation; self.entered = entered
    }
    func release() { waiter?.resume(); waiter = nil }
    func upload(_ id: String, _ revision: Int, _ bytes: Data) -> RuntimeValue {
        candidates[id] = revision
        return .object(["installationId": .string(id), "sourceRevision": .number(Double(revision)),
                        "sourceBytes": .number(Double(bytes.count))])
    }
    func call(_ operation: String, _ parameters: RuntimeValue) async throws -> RuntimeValue {
        if operation == "status" {
            if let probed {
                self.probed = nil
                try failIfNeeded("detect", id: probed)
            }
            return .object(["instances": .array(["a", "b"].filter { !removed.contains($0) }.map(cat))])
        }
        guard case .object(let fields) = parameters, case .string(let id)? = fields["installationId"] else {
            throw FilmSourceError(code: "INVALID_ARGUMENT")
        }
        if operation == "invoke" { probes.append(id) }
        else { mutations.append((operation, id)) }
        if paused == operation {
            paused = nil; entered?(); entered = nil
            await withCheckedContinuation { waiter = $0 }
        }
        try failIfNeeded(operation, id: id)
        switch operation {
        case "invoke": probed = id; return .object(["list": .array([])])
        case "uninstall": removed.insert(id); return .object(["failures": .array([])])
        case "enable":
            if fields["enabled"] == .bool(false) { disabled.insert(id) } else { disabled.remove(id) }
        case "update":
            if local.contains(id) {
                guard case .number(let revision)? = fields["sourceRevision"], candidates[id] == Int(revision) else {
                    throw FilmSourceError(code: "INVALID_SOURCE")
                }
            }
            updated.insert(id)
        case "restart": break
        default: throw FilmSourceError(code: "UNEXPECTED_OPERATION")
        }
        return cat(id)
    }
    private func failIfNeeded(_ operation: String, id: String) throws {
        guard failure?.operation == operation, failure?.id == id else { return }
        failure = nil
        throw FilmSourceError(code: operation.uppercased() + "_FAILED")
    }
    private func cat(_ id: String) -> RuntimeValue {
        .object(["installationId": .string(id), "name": .string("来源 " + id),
            "sourceType": .string(local.contains(id) ? "local" : "network"),
            "sourceUrl": local.contains(id) ? .null : .string("https://example.invalid/\(id).js"),
            "enabled": .bool(!disabled.contains(id)), "generation": .number(1),
            "state": .string(disabled.contains(id) ? "disabled" : "ready"),
            "importedAt": .number(1_700_000_000_000), "updatedAt": .null,
            "updateState": .string(updated.contains(id) ? "updated" : "idle"),
            "updateError": .null, "sourceHash": .string("fixture"), "error": .null,
            "sites": .array([.object(["key": .string("site"), "name": .string("视频 " + id),
                "api": .string("/api/" + id), "actions": .array([.string("home")]), "methods": .object([:])])])])
    }
}
#endif
