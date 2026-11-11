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
    func testSourceRowsExposeSwitchAndOneMenuAndOpenNamedDetails() async throws {
        let fixture = try makeFixture()
        try await fixture.service.refresh()
        let theme = SourceListTheme()
        let cats = FilmCatSourcesViewController(service: fixture.service, themeProvider: theme)
        let navigation = PageNavigationController(themeProvider: theme)
        navigation.setViewControllers([cats], animated: false)
        let window = try await host(navigation)
        defer { unhost(window) }
        await eventually { cats.snapshot?.cats.count == 2 }
        let catRow = try row(cats, "film.cat.a")
        let content = try XCTUnwrap(catRow.content)
        let toggle = try XCTUnwrap(descendants(content).compactMap { $0 as? UISwitch }.first)
        let more = try XCTUnwrap(descendants(content).compactMap { $0 as? UIButton }.first { $0.menu != nil })
        XCTAssertEqual(descendants(content).compactMap { $0 as? UIButton }.filter { $0.menu != nil }.count, 1)
        XCTAssertEqual(more.menu?.children.compactMap { ($0 as? UIAction)?.title }, ["编辑", "更新", "重启", "删除"])
        XCTAssertTrue(toggle.isOn)
        toggle.setOn(false, animated: false); toggle.sendActions(for: .valueChanged)
        await eventually { cats.snapshot?.cats.first?.enabled == false && !cats.busy }
        XCTAssertEqual(cats.snapshot?.videos.count, 2, "Disabling a parent preserves its video sources")
        try row(cats, "film.cat.a").action?()
        await eventually { navigation.topViewController is FilmCatDetailController }
        let detail = try XCTUnwrap(navigation.topViewController as? FilmCatDetailController)
        await eventually { detail.pageTitle == "来源 a" && detail.canPresentPageAlert }
        XCTAssertFalse(detail.sections.flatMap(\.rows).contains { $0.title == detail.pageTitle })
        navigation.popViewController(animated: false)
        await eventually { cats.isPageVisible }
        let videos = FilmVideoSourcesViewController(service: fixture.service, themeProvider: theme)
        navigation.pushViewController(videos, animated: false)
        await eventually { videos.snapshot?.videos.count == 2 }
        let videoContent = try XCTUnwrap(try row(videos, "film.video.b.site").content)
        let videoSwitch = try XCTUnwrap(descendants(videoContent).compactMap { $0 as? UISwitch }.first)
        let videoMenus = descendants(videoContent).compactMap { $0 as? UIButton }.filter { $0.menu != nil }
        XCTAssertEqual(videoMenus.count, 1)
        XCTAssertEqual(videoMenus.first?.menu?.children.compactMap { ($0 as? UIAction)?.title }, ["检测", "详情", "删除"])
        videoSwitch.setOn(false, animated: false); videoSwitch.sendActions(for: .valueChanged)
        await eventually { videos.snapshot?.videos.first { $0.key.installationId == "b" }?.options.enabled == false && !videos.busy }
        try row(videos, "film.video.b.site").action?()
        await eventually { navigation.topViewController is FilmVideoDetailController }
        let videoDetail = try XCTUnwrap(navigation.topViewController as? FilmVideoDetailController)
        await eventually { videoDetail.pageTitle == "视频 b" }
        XCTAssertNotNil(try row(videoDetail, "film.video.order").action)
        XCTAssertFalse(videoDetail.sections.flatMap(\.rows).contains { $0.title == videoDetail.pageTitle })
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
            try row(page, "film.cat.a").action?()
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
            try row(page, "film.cats.select").action?()
            try row(page, "film.cats.selectAll").action?()
            let entered = expectation(description: operation + " admitted")
            await fixture.runtime.pause(operation) { entered.fulfill() }
            try row(page, "film.cats.batch.\(action)").action?()
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
                try row(page, "film.cats.select").action?()
                try row(page, "film.cats.selectAll").action?()
                try row(page, "film.cats.batch.2").action?()
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
            try row(page, "film.cats.select").action?()
            try row(page, "film.cats.selectAll").action?()
            try row(page, "film.cats.batch.2").action?()
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
                try row(page, "film.cats.select").action?()
                try row(page, "film.cats.selectAll").action?()
                try row(page, "film.cats.batch.\(action)").action?()
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
        try row(page, "film.videos.select").action?()
        try row(page, "film.videos.selectAll").action?()
        try row(page, "film.videos.detect").action?()
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
        try row(page, "film.videos.select").action?()
        try row(page, "film.videos.selectAll").action?()
        let entered = expectation(description: "probe admitted")
        await fixture.runtime.pause("invoke") { entered.fulfill() }
        try row(page, "film.videos.detect").action?()
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

    func testNarrowLargeTypeSourceControlsFitAndSelectionUsesWholeRows() async throws {
        let fixture = try makeFixture()
        try await fixture.service.refresh()
        let page = FilmCatSourcesViewController(service: fixture.service, themeProvider: SourceListTheme())
        page.traitOverrides.preferredContentSizeCategory = .accessibilityExtraExtraExtraLarge
        let window = try await host(page, width: 320)
        defer { unhost(window) }
        await eventually { page.snapshot?.cats.count == 2 }
        page.table.layoutIfNeeded()
        let index = try XCTUnwrap(page.sections.enumerated().compactMap { section, value in
            value.rows.firstIndex { $0.id == "film.cat.a" }.map { IndexPath(row: $0, section: section) }
        }.first)
        page.table.scrollToRow(at: index, at: .top, animated: false)
        page.table.layoutIfNeeded()
        let cell = try XCTUnwrap(page.table.cellForRow(at: index))
        let controls = descendants(cell.contentView).compactMap { $0 as? UIControl }
            .filter { $0.accessibilityIdentifier?.hasPrefix("film.cat.a.") == true }
        XCTAssertEqual(Set(controls.compactMap(\.accessibilityIdentifier)),
                       ["film.cat.a.detail", "film.cat.a.enabled", "film.cat.a.more"])
        for control in controls {
            let frame = control.convert(control.bounds, to: cell.contentView)
            XCTAssertGreaterThanOrEqual(frame.minX, -1)
            XCTAssertLessThanOrEqual(frame.maxX, cell.contentView.bounds.width + 1)
            XCTAssertGreaterThanOrEqual(control.bounds.width, 44)
            XCTAssertGreaterThanOrEqual(control.bounds.height, 44)
        }
        let attachment = XCTAttachment(image: UIGraphicsImageRenderer(bounds: page.view.bounds).image { _ in
            page.view.drawHierarchy(in: page.view.bounds, afterScreenUpdates: true)
        })
        attachment.name = "Source list 320 AXXXL"; attachment.lifetime = .keepAlways; add(attachment)
        try row(page, "film.cats.select").action?()
        XCTAssertNil(try row(page, "film.cat.a").content)
        try row(page, "film.cat.a").action?()
        XCTAssertTrue(try row(page, "film.cat.a").selected)
        XCTAssertTrue(try row(page, "film.cats.batch.4").enabled)
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
