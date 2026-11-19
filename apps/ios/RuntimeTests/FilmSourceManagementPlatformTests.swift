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
            if action == "update" { try await fixture.useLocalSource("a") }
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
        await eventually { page.snapshot?.videos == saved.videos && page.canPresentPageAlert }
        for (index, target) in saved.videos.reversed().enumerated() {
            try ManagementListTestUI.open("film.video.\(target.key.installationId).\(target.key.siteKey)", in: page.manager)
            await eventually {
                (navigation.topViewController as? FilmVideoDetailController)?.canPresentPageAlert == true &&
                (navigation.topViewController as? FilmVideoDetailController)?.snapshot?.videos == page.snapshot?.videos
            }
            let detail = try XCTUnwrap(navigation.topViewController as? FilmVideoDetailController)
            XCTAssertEqual(detail.pageTitle, target.name)
            XCTAssertEqual(try row(detail, "film.video.order").detail, String(target.order))
            XCTAssertFalse(detail.table.visibleCells.isEmpty)
            for cell in detail.table.visibleCells { XCTAssertTrue(cell is SettingsTableCell) }
            try row(detail, "film.video.order").action?()
            let dialog = try XCTUnwrap(detail.presentedViewController as? ManagementDialog)
            let input = try XCTUnwrap(descendants(dialog.view).first {
                $0.accessibilityIdentifier == "film.video.order.input"
            } as? UITextField)
            await eventually { dialog.view.window != nil && !dialog.isBeingPresented }
            XCTAssertEqual(input.text, String(target.order))
            input.text = String(index)
            try XCTUnwrap(descendants(dialog.view).compactMap { $0 as? UIButton }.first {
                $0.accessibilityIdentifier == "management.dialog.confirm"
            }).sendActions(for: .touchUpInside)
            await eventually {
                detail.presentedViewController == nil &&
                detail.snapshot?.videos.first { $0.key == target.key }?.order == index
            }
            let current = await fixture.service.snapshot
            navigation.popViewController(animated: false)
            await eventually { page.snapshot?.videos == current.videos && page.canPresentPageAlert }
        }
        let reordered = await fixture.service.snapshot
        XCTAssertEqual(reordered.videos.map(\.key), saved.videos.reversed().map(\.key))
        for source in reordered.videos {
            XCTAssertEqual(source.options, saved.videos.first { $0.key == source.key }?.options)
        }
        XCTAssertEqual(ManagementListTestUI.rowIDs(in: page.manager),
                       reordered.videos.map { "management.row.film.video.\($0.key.installationId).\($0.key.siteKey)" })

        let target = try XCTUnwrap(reordered.videos.first)
        try ManagementListTestUI.open("film.video.\(target.key.installationId).\(target.key.siteKey)", in: page.manager)
        await eventually { navigation.topViewController is FilmVideoDetailController }
        let detail = try XCTUnwrap(navigation.topViewController as? FilmVideoDetailController)
        await eventually { detail.canPresentPageAlert && detail.sections.flatMap(\.rows).contains { $0.id == "film.video.order" } }
        try row(detail, "film.video.order").action?()
        let dialog = try XCTUnwrap(detail.presentedViewController as? ManagementDialog)
        let input = try XCTUnwrap(descendants(dialog.view).first {
            $0.accessibilityIdentifier == "film.video.order.input"
        } as? UITextField)
        await eventually { dialog.view.window != nil && !dialog.isBeingPresented }
        XCTAssertEqual(input.text, String(target.order))
        let inputSection = try XCTUnwrap(input.superview as? SettingsInputSection)
        XCTAssertEqual(inputSection.titleLabel.text, "排序")
        XCTAssertLessThanOrEqual(inputSection.titleLabel.frame.maxY, input.frame.minY)
        XCTAssertEqual((input as? SettingsTextField)?.inputAppearance, .underline)
        XCTAssertEqual(input.backgroundColor, .clear)
        XCTAssertEqual(input.layer.borderWidth, 0)
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
        let invalid = await fixture.service.snapshot
        XCTAssertEqual(invalid.videos, reordered.videos)
        input.text = "7"
        try XCTUnwrap(descendants(dialog.view).compactMap { $0 as? UIButton }.first {
            $0.accessibilityIdentifier == "management.dialog.confirm"
        }).sendActions(for: .touchUpInside)
        await eventually {
            detail.presentedViewController == nil &&
            detail.snapshot?.videos.first { $0.key == target.key }?.order == 7
        }
        let manual = await fixture.service.snapshot
        XCTAssertEqual(manual.videos.map(\.key), saved.videos.map(\.key))
        for source in manual.videos {
            XCTAssertEqual(source.options, saved.videos.first { $0.key == source.key }?.options)
        }
        XCTAssertEqual(manual.videos.filter { $0.key != target.key },
                       reordered.videos.filter { $0.key != target.key })
        navigation.popViewController(animated: false)
        await eventually { page.snapshot?.videos == manual.videos && page.canPresentPageAlert }
        XCTAssertEqual(ManagementListTestUI.rowIDs(in: page.manager),
                       manual.videos.map { "management.row.film.video.\($0.key.installationId).\($0.key.siteKey)" })
        await fixture.service.close()
    }

    func testCatHeaderImportMenuPreservesLocalCancelAndBusyVisibilityGuards() async throws {
        let fixture = try makeFixture()
        try await fixture.service.refresh()
        let theme = SourceListTheme()
        let page = FilmCatSourcesViewController(service: fixture.service, themeProvider: theme)
        let navigation = PageNavigationController(themeProvider: theme)
        navigation.setViewControllers([page], animated: false)
        let window = try await host(navigation); defer { unhost(window) }
        await eventually { page.snapshot?.cats.count == 2 && page.canPresentPageAlert }
        let add = try XCTUnwrap(descendants(page.pageHeader).compactMap { $0 as? PageHeaderButton }.first {
            $0.accessibilityLabel == "导入猫源"
        })
        page.view.layoutIfNeeded()
        XCTAssertEqual(page.pageHeader.trailingCount, 1)
        XCTAssertTrue(add.window === window)
        XCTAssertFalse(add.isHidden)
        XCTAssertTrue(add.showsMenuAsPrimaryAction)
        XCTAssertEqual(add.tintColor, UIColor(page.theme.ink))
        XCTAssertEqual(add.menu?.children.compactMap { ($0 as? UIAction)?.title }, ["本地导入", "网络导入"])
        let buttonFrame = add.convert(add.bounds, to: page.pageHeader)
        let headerFrame = page.pageHeader.contentFrame
        XCTAssertGreaterThan(buttonFrame.width, 0)
        XCTAssertGreaterThan(buttonFrame.height, 0)
        XCTAssertGreaterThanOrEqual(buttonFrame.minX, headerFrame.midX)
        XCTAssertLessThanOrEqual(buttonFrame.maxX, headerFrame.maxX)
        XCTAssertGreaterThanOrEqual(buttonFrame.minY, headerFrame.minY)
        XCTAssertLessThanOrEqual(buttonFrame.maxY, headerFrame.maxY)
        let pendingImports = try ["本地导入", "网络导入"].map {
            try ManagementListTestUI.menuAction($0, on: add)
        }
        try ManagementListTestUI.select("film.cat.a", in: page.manager)
        let sourceIDs = ManagementListTestUI.rowIDs(in: page.manager)
        try ManagementListTestUI.activateMenu("本地导入", on: add)
        await eventually { (navigation.topViewController as? FilmCatEditorController)?.canPresentPageAlert == true }
        let editor = try XCTUnwrap(navigation.topViewController as? FilmCatEditorController)
        if case .importSource(let local) = editor.mode { XCTAssertTrue(local) }
        else { XCTFail("The local menu action must open a local import") }
        XCTAssertNotNil(try row(editor, "film.cat.editor.file").action)
        navigation.popViewController(animated: false)
        await eventually { page.canPresentPageAlert && !editor.isPageActive }
        XCTAssertEqual(ManagementListTestUI.rowIDs(in: page.manager), sourceIDs)
        XCTAssertEqual(page.manager.selected, ["film.cat.a"])
        let afterCancel = await fixture.runtime.mutations
        XCTAssertTrue(afterCancel.isEmpty)

        let successor = UIViewController()
        navigation.pushViewController(successor, animated: false)
        await eventually { !page.isPageVisible }
        for title in ["本地导入", "网络导入"] {
            try ManagementListTestUI.activateMenu(title, on: add)
        }
        XCTAssertTrue(navigation.topViewController === successor)
        XCTAssertEqual(navigation.viewControllers.count, 2)
        XCTAssertNil(page.presentedViewController)
        navigation.popViewController(animated: false)
        await eventually { page.canPresentPageAlert }
        let content = try ManagementListTestUI.cell("film.cat.a", in: page.manager).contentView
        let more = try ManagementListTestUI.button("management.more.film.cat.a", in: content)
        let entered = expectation(description: "restart admitted")
        await fixture.runtime.pause("restart") { entered.fulfill() }
        try ManagementListTestUI.activateMenu("重启", on: more)
        await fulfillment(of: [entered], timeout: 5)
        XCTAssertTrue(page.busy)
        XCTAssertFalse(add.isEnabled)
        // A menu action captured before the write must still honor the busy guard.
        for action in pendingImports {
            let trigger = UIButton()
            trigger.addAction(action, for: .touchUpInside)
            trigger.sendActions(for: .touchUpInside)
        }
        XCTAssertTrue(navigation.topViewController === page)
        XCTAssertNil(page.presentedViewController)
        await fixture.runtime.release()
        await eventually { !page.busy && add.isEnabled }
        let mutations = await fixture.runtime.mutations
        XCTAssertEqual(mutations.map(\.0), ["restart"])
        XCTAssertEqual(mutations.map(\.1), ["a"])
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
        let add = try XCTUnwrap(descendants(page.pageHeader).compactMap { $0 as? PageHeaderButton }.first {
            $0.accessibilityLabel == "导入猫源"
        })
        try ManagementListTestUI.activateMenu("网络导入", on: add)
        await eventually {
            (page.presentedViewController as? FilmFloatingEditorController)?.viewIfLoaded?.window != nil &&
            page.presentedViewController?.isBeingPresented == false
        }
        let modal = try XCTUnwrap(page.presentedViewController as? FilmFloatingEditorController)
        let editor = try XCTUnwrap(modal.children.compactMap { $0 as? FilmCatEditorController }.first)
        if case .importSource(let local) = editor.mode { XCTAssertFalse(local) }
        else { XCTFail("The network menu action must open a network import") }
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
        for (operation, action) in [("delete", 4), ("enable", 0), ("restart", 3), ("update", 2)] {
            let fixture = try makeFixture()
            try await fixture.service.refresh()
            if action == 0 {
                _ = try await fixture.service.enableCat("a", enabled: false)
                try await fixture.loadRuntime()
                fixture.native.receipts.reset()
            }
            let theme = SourceListTheme()
            let page = FilmCatSourcesViewController(service: fixture.service, themeProvider: theme)
            let navigation = PageNavigationController(themeProvider: theme)
            navigation.setViewControllers([page], animated: false)
            let window = try await host(navigation)
            await eventually { page.snapshot?.cats.count == 2 && page.canPresentPageAlert }
            try ManagementListTestUI.button("management.selectAll", in: page.manager).sendActions(for: .touchUpInside)
            let entered = expectation(description: operation + " admitted")
            if action == 0 || action == 4 {
                fixture.native.receipts.pauseNext { entered.fulfill() }
            } else {
                await fixture.runtime.pause(operation) { entered.fulfill() }
            }
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
            if action == 0 || action == 4 { fixture.native.receipts.release() }
            else { await fixture.runtime.release() }
            await eventually { !page.busy }
            let calls = await fixture.runtime.mutations
            if action == 0 || action == 4 {
                XCTAssertEqual(fixture.native.receipts.commits, ["a"])
                XCTAssertTrue(calls.isEmpty, "Native batch writes do not call Node")
            } else {
                XCTAssertEqual(calls.map(\.0), [operation])
                XCTAssertEqual(calls.map(\.1), ["a"])
            }
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
                try await fixture.useLocalSource("a"); try await fixture.useLocalSource("b")
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
                let before = try await fixture.native.document.read()
                if fail { try fixture.native.receipts.setSaveFailure(true) }
                let entered = expectation(description: "native local input committed")
                if !fail { fixture.native.receipts.pauseNext { entered.fulfill() } }
                try row(editor, "film.cat.editor.save").action?()
                if fail {
                    await eventually { (editor.presentedViewController as? UIAlertController)?.title == "错误" &&
                        (editor.presentedViewController as? UIAlertController)?.isBeingPresented == false }
                    XCTAssertNotNil(editor.file, "A failed save retains the selected file")
                    XCTAssertTrue(fixture.native.receipts.commits.isEmpty)
                    try fixture.native.receipts.setSaveFailure(false)
                    let unchanged = try await fixture.native.document.read()
                    XCTAssertEqual(unchanged, before)
                    fixture.native.receipts.pauseNext { entered.fulfill() }
                    editor.selectAlertChoice("重试")
                    await fulfillment(of: [entered], timeout: 5)
                } else {
                    await fulfillment(of: [entered], timeout: 5)
                }
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
                fixture.native.receipts.release()
                await eventually { (try? self.row(editor, "film.cat.editor.save").title) == "保存" }
                XCTAssertTrue(navigation.topViewController === editor)
                XCTAssertNil(editor.onSaved, "An invalidated handoff must not be revived by a retry")
                if fail {
                    XCTAssertNotNil(editor.file, "A failed save retains the selected file")
                    await eventually { (editor.presentedViewController as? UIAlertController)?.title != "错误" &&
                        (try? self.row(editor, "film.cat.editor.save").title) == "保存" &&
                        page.snapshot?.cats.first { $0.installationId == "a" }?.inputRevision == 3 }
                }
                let calls = await fixture.runtime.mutations
                XCTAssertTrue(calls.isEmpty, "Local source replacement commits without HTTP update/upload")
                XCTAssertEqual(fixture.native.receipts.commits, ["a"])
                let committed = await fixture.service.snapshot
                XCTAssertEqual(committed.cats.first { $0.installationId == "a" }?.inputRevision, 3)
                XCTAssertEqual(committed.cats.first { $0.installationId == "b" }?.inputRevision, 2)
                try await fixture.loadRuntime()
                try await fixture.service.refresh()
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
            try await fixture.useLocalSource("a")
            if secondLocal { try await fixture.useLocalSource("b") }
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
            await eventually { navigation.topViewController === page && !page.busy }
            try await fixture.loadRuntime()
            try await fixture.service.refresh()
            await eventually { page.snapshot?.cats.allSatisfy { $0.updateState == "updated" } == true }
            let calls = await fixture.runtime.mutations
            XCTAssertEqual(calls.map(\.0), secondLocal ? [] : ["update"])
            XCTAssertEqual(calls.map(\.1), secondLocal ? [] : ["b"])
            XCTAssertEqual(fixture.native.receipts.commits, secondLocal ? ["a", "b"] : ["a"])
            XCTAssertEqual(navigation.viewControllers.count, 1)
            await fixture.service.close()
        }
    }

    func testCatBatchEnableAndDisableRetryOnlyUnconfirmedItemsAndCancelStops() async throws {
        for action in [0, 1] {
            for choice in ["重试", "取消"] {
                let fixture = try makeFixture()
                try await fixture.service.refresh()
                if action == 0 {
                    _ = try await fixture.service.enableCat("a", enabled: false)
                    _ = try await fixture.service.enableCat("b", enabled: false)
                    try await fixture.loadRuntime()
                    fixture.native.receipts.reset()
                }
                fixture.native.receipts.failAfterNextCommit()
                let page = FilmCatSourcesViewController(service: fixture.service, themeProvider: SourceListTheme())
                let window = try await host(page)
                await eventually { page.snapshot?.cats.count == 2 && page.canPresentPageAlert }
                try ManagementListTestUI.button("management.selectAll", in: page.manager).sendActions(for: .touchUpInside)
                try ManagementListTestUI.activateMenu(action == 0 ? "启用所选" : "停用所选",
                                                     on: page.manager.footerMore)
                await eventually { !page.busy && (page.presentedViewController as? UIAlertController)?.title == "错误" }
                XCTAssertEqual(fixture.native.receipts.commits, ["a"])
                try fixture.native.receipts.setSaveFailure(false)
                let partial = try await CatSourceSettings(document: fixture.native.document).read()
                XCTAssertEqual(partial.map(\.enabled), [action == 0, action != 0])
                await eventually { (page.presentedViewController as? UIAlertController)?.isBeingPresented == false }
                page.selectAlertChoice(choice)
                await eventually { !page.busy && page.presentedViewController == nil }
                let calls = await fixture.runtime.mutations
                XCTAssertTrue(calls.isEmpty)
                XCTAssertEqual(fixture.native.receipts.commits, choice == "重试" ? ["a", "b"] : ["a"])
                let saved = await fixture.service.snapshot
                XCTAssertEqual(saved.cats.map(\.enabled), [action == 0, choice == "重试" ? action == 0 : action != 0])
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
        addTeardownBlock {
            fixture.native.receipts.release()
            await fixture.runtime.release()
            await fixture.service.close()
            try await fixture.database.close()
            try FileManager.default.removeItem(at: fixture.base)
        }
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
    let database: SQLiteDatabase
    let runtime: SourceListRuntime
    let service: FilmSourceService
    let native: SourceListNativeSettings
    init() throws {
        base = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: base, withIntermediateDirectories: true)
        database = try appStorageTestOpen(base)
        let runtime = SourceListRuntime(); self.runtime = runtime
        let native = try SourceListNativeSettings(database); self.native = native
        service = FilmSourceService(transport: .init(request: { try await runtime.call($0, $1) }),
            database: database, settingsDocument: native.document,
            runtimeSourceRoot: base.appendingPathComponent("cat"))
    }
    func useLocalSource(_ id: String) async throws {
        _ = try await service.editCat(id, name: "来源 " + id, sourceType: "local", source: "// initial " + id)
        try await runtime.acceptNativeSettings(native.document, resetUpdates: true)
        native.receipts.reset()
    }
    func loadRuntime() async throws {
        try await runtime.acceptNativeSettings(native.document)
    }
}

private struct SourceListNativeSettings: Sendable {
    let document: FilmSettingsDocument
    let receipts: SourceListNativeReceipts
    init(_ database: SQLiteDatabase) throws {
        let scripts = ["a", "b"].map { id in
            CatSourceConfiguration(installationId: id, name: "来源 " + id, sourceType: "network",
                sourceUrl: "https://example.invalid/\(id).js", localPath: nil, localHash: nil,
                enabled: true, inputRevision: 1).value
        }
        let seed = FilmSettingsDocument(block: .catSources, database: database)
        try appStorageTestIO {
            _ = try database.transactionSync { db in
                for (ordinal, input) in scripts.enumerated() {
                    guard case .object(let fields) = input else { throw FilmPlaybackError.invalidResponse }
                    guard let installationId = fields["installationId"]?.filmText,
                          let name = fields["name"]?.filmText,
                          let sourceType = fields["sourceType"]?.filmText,
                          case .bool(let enabled)? = fields["enabled"],
                          let inputRevision = fields["inputRevision"]?.filmInteger else {
                        throw FilmPlaybackError.invalidResponse
                    }
                    try db.upsert("ios_cat_inputs", columns: ["installationId", "ordinal", "name", "sourceType", "sourceUrl", "localPath", "localHash", "enabled", "inputRevision"],
                        values: [.text(installationId), .integer(ordinal), .text(name), .text(sourceType),
                            .optional(fields["sourceUrl"]?.filmText),
                            .null, .null,
                            .bool(enabled), .integer(inputRevision)],
                        key: ["installationId"])
                }
                try db.execute("INSERT INTO ios_cat_settings(singleton,port) VALUES(1,0)")
                try db.execute("UPDATE ios_settings_blocks SET revision=1 WHERE block='cat.sources'")
            }
        }
        let receipts = SourceListNativeReceipts(document: seed, database: database, scripts: scripts)
        self.receipts = receipts
        document = FilmSettingsDocument(block: .catSources, database: database,
            changed: { _ in receipts.committed() })
    }
}

/// Blocks only a synthetic native commit receipt, never a runtime apply.
/// query_only is reversible and preserves the committed database rows.
private final class SourceListNativeReceipts: @unchecked Sendable {
    private let lock = NSLock()
    private let document: FilmSettingsDocument
    private let database: SQLiteDatabase
    private var previous: [RuntimeValue]
    private var recorded: [String] = []
    private var gate: (DispatchSemaphore, @Sendable () -> Void)?
    private var failNext = false
    private var failing = false

    init(document: FilmSettingsDocument, database: SQLiteDatabase, scripts: [RuntimeValue]) {
        self.document = document; self.database = database; previous = scripts
    }
    var commits: [String] { lock.lock(); defer { lock.unlock() }; return recorded }
    func reset() { lock.lock(); recorded.removeAll(); lock.unlock() }
    func pauseNext(_ entered: @escaping @Sendable () -> Void) {
        lock.lock(); gate = (DispatchSemaphore(value: 0), entered); lock.unlock()
    }
    func release() {
        lock.lock(); let gate = gate; self.gate = nil; lock.unlock()
        gate?.0.signal()
    }
    func failAfterNextCommit() { lock.lock(); failNext = true; lock.unlock() }
    func setSaveFailure(_ value: Bool) throws {
        lock.lock(); defer { lock.unlock() }
        guard value != failing else { return }
        try appStorageTestIO {
            _ = try self.database.readSync { try $0.query(value ? "PRAGMA query_only=ON" : "PRAGMA query_only=OFF") }
        }
        failing = value
    }
    func committed() {
        do {
            let scripts = try XCTUnwrap(document.appStorageTestRead()?.catField("scripts")?.catArray)
            lock.lock()
            let ids = try (previous + scripts).compactMap {
                try $0.filmRequiredObject().filmValue("installationId").filmText
            }
            let changed = ids.reduce(into: [String]()) { result, id in
                guard !result.contains(id),
                      previous.first(where: { $0.filmObject?["installationId"] == .string(id) }) !=
                        scripts.first(where: { $0.filmObject?["installationId"] == .string(id) }) else { return }
                result.append(id)
            }
            recorded += changed; previous = scripts
            let gate = gate, fail = failNext; failNext = false
            lock.unlock()
            if fail { try setSaveFailure(true) }
            if let gate { gate.1(); gate.0.wait() }
        } catch { XCTFail("Synthetic native settings receipt failed") }
    }
}

private actor SourceListRuntime {
    private var updated = Set<String>()
    private var inputs = ["a", "b"].map { id in
        CatSourceConfiguration(installationId: id, name: "来源 " + id, sourceType: "network",
            sourceUrl: "https://example.invalid/\(id).js", localPath: nil, localHash: nil,
            enabled: true, inputRevision: 1)
    }
    private var paused: String?
    private var entered: (@Sendable () -> Void)?
    private var waiter: CheckedContinuation<Void, Never>?
    private var failure: (operation: String, id: String)?
    private var probed: String?
    private(set) var mutations: [(String, String)] = []
    private(set) var probes: [String] = []
    func acceptNativeSettings(_ document: FilmSettingsDocument, resetUpdates: Bool = false) async throws {
        let saved = try await CatSourceSettings(document: document).read()
        if resetUpdates { updated.removeAll() }
        else {
            for input in saved {
                if let old = inputs.first(where: { $0.installationId == input.installationId }),
                   !input.hasSameInput(as: old) { updated.insert(input.installationId) }
            }
        }
        inputs = saved
    }
    func fail(_ operation: String, id: String) { failure = (operation, id) }
    func pause(_ operation: String, entered: @escaping @Sendable () -> Void) {
        paused = operation; self.entered = entered
    }
    func release() { waiter?.resume(); waiter = nil }
    func call(_ operation: String, _ parameters: RuntimeValue) async throws -> RuntimeValue {
        if operation == "status" {
            if let probed {
                self.probed = nil
                try failIfNeeded("detect", id: probed)
            }
            return .object(["instances": .array(inputs.map { cat($0.installationId) })])
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
        case "update": updated.insert(id)
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
        let input = inputs.first { $0.installationId == id }!
        return .object(["installationId": .string(id), "name": .string(input.name),
            "sourceType": .string(input.sourceType),
            "sourceUrl": input.sourceUrl.map(RuntimeValue.string) ?? .null,
            "enabled": .bool(input.enabled), "generation": .number(Double(input.inputRevision)),
            "inputRevision": .number(Double(input.inputRevision)),
            "state": .string(input.enabled ? "ready" : "disabled"),
            "importedAt": .number(1_700_000_000_000), "updatedAt": .null,
            "updateState": .string(updated.contains(id) ? "updated" : "idle"),
            "updateError": .null, "sourceHash": .string(input.localHash ?? "fixture"), "error": .null,
            "sites": .array([.object(["key": .string("site"), "name": .string("视频 " + id),
                "api": .string("/api/" + id), "actions": .array([.string("home")]), "methods": .object([:])])])])
    }
}
#endif
