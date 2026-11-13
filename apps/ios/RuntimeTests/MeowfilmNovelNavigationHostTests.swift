import XCTest
import UIKit
import MeowUI
import MeowMusicFeature
import MeowRuntime
@testable import MeowNovelFeature
@testable import Meowfilm

@MainActor
final class MeowfilmNovelNavigationHostTests: XCTestCase {
    func testActualNovelShelfDetailReaderDockRestoreAndSettingsRoutes() async throws {
        let app = try XCTUnwrap(UIApplication.shared.delegate as? MeowfilmAppDelegate)
        try await wait { app.launch.phase == .ready }
        let assembly = try XCTUnwrap(app.readyAssembly()); await assembly.waitForStartup()
        let resources = try await hostResources(), feature = try XCTUnwrap(assembly.novel)
        let url = "https://novel-navigation-\(UUID().uuidString.lowercased()).invalid/"
        let definition = #"{"bookSourceName":"Navigation fixture","bookSourceUrl":"\#(url)","html":"<script>function info(){return {bookUrl:'\#(url)book',name:'雨夜来信',author:'林舟'}};function chapter(){return [{name:'第一章 雨声',chapterUrl:'\#(url)1'}]};function content(){return '原生阅读页面正文。'}</script>"}"#
        let receipt = try await feature.sourcesClient.importSources(text: definition), source = try XCTUnwrap(receipt.sources.first)
        try await feature.refreshSources()
        let book = try await feature.retain(.init(installationId: source.id, bookUrl: url + "book", name: "雨夜来信", author: "林舟"), shelf: true)
        let scene = try XCTUnwrap(UIApplication.shared.connectedScenes.compactMap { $0 as? UIWindowScene }.first { $0.activationState == .foregroundActive })
        let previous = scene.windows.first { $0.isKeyWindow }, oldPresenter = feature.web.present
        let window = UIWindow(windowScene: scene); window.frame = scene.coordinateSpace.bounds; window.windowLevel = .normal + 1
        let coordinator = MeowfilmCoordinator(window: window, assembly: assembly, resetUserData: {})
        coordinator.start()
        let root = try XCTUnwrap(window.rootViewController as? MeowfilmRootContainerViewController)
        addTeardownBlock { @MainActor in
            PageViewController.invalidatePages(in: root); window.isHidden = true; window.rootViewController = nil; previous?.makeKey()
            feature.web.present = oldPresenter
            if let current = try? await feature.sourcesClient.list().first(where: { $0.id == source.id }) { try? await feature.sourcesClient.remove(current) }
            try? await resources.novelLibrary.setShelf(book.id, present: false)
            try? await resources.novelLibrary.deleteHistory(book.id)
            try? await feature.reloadLocal(); try? await feature.refreshSources()
        }
        try await wait { root.navigation.topViewController is MeowfilmPrimaryShellViewController }
        let main = try XCTUnwrap(root.navigation.topViewController as? MeowfilmPrimaryShellViewController)
        try await settle(root)
        main.select(.novel)
        try await wait { main.selectedPage == .novel && self.controllers(main).contains { ($0 as? NovelHomeViewController)?.isPageVisible == true } }
        let home = try XCTUnwrap(controllers(main).compactMap { $0 as? NovelHomeViewController }.first)
        home.showShelf()
        let table = try XCTUnwrap(views(home.view).first { $0.accessibilityIdentifier == "novel.books" } as? UITableView)
        try await wait { table.numberOfRows(inSection: 0) > 0 }
        let index = try XCTUnwrap(feature.local.books(history: false).firstIndex { $0.id == book.id })
        home.tableView(table, didSelectRowAt: .init(row: index, section: 0))
        try await wait { root.navigation.topViewController is NovelDetailViewController }; try await settle(root)
        let detail = try XCTUnwrap(root.navigation.topViewController as? NovelDetailViewController)
        XCTAssertFalse(root.dock?.isHidden == true)
        let currentItem = assembly.playback.player.currentItem
        let read = try XCTUnwrap(views(detail.view).first { $0.accessibilityIdentifier == "novel.detail.read" } as? UIButton)
        read.sendActions(for: .touchUpInside)
        try await wait { root.navigation.topViewController is NovelReaderViewController }; try await settle(root)
        let reader = try XCTUnwrap(root.navigation.topViewController as? NovelReaderViewController)
        try await wait { reader.session.content != nil && !reader.session.loading }
        XCTAssertTrue(root.dock?.isHidden == true)
        XCTAssertTrue(assembly.playback.player.currentItem === currentItem)
        root.view.layoutIfNeeded(); XCTAssertEqual(root.navigation.additionalSafeAreaInsets.bottom, 0, accuracy: 1)
        let attachment = XCTAttachment(image: UIGraphicsImageRenderer(bounds: window.bounds).image { _ in window.drawHierarchy(in: window.bounds, afterScreenUpdates: true) })
        attachment.name = "novel-real-app-reader-hidden-dock"; attachment.lifetime = .keepAlways; add(attachment)
        root.navigation.popViewController(animated: false); try await wait { root.navigation.topViewController === detail }; try await settle(root)
        XCTAssertFalse(root.dock?.isHidden == true)
        XCTAssertTrue(assembly.playback.player.currentItem === currentItem)
        root.navigation.popViewController(animated: false); try await wait { root.navigation.topViewController === main }; try await settle(root)
        coordinator.showSettings(); try await wait { root.navigation.topViewController is MeowfilmSettingsViewController }; try await settle(root)
        let settings = try XCTUnwrap(root.navigation.topViewController as? MeowfilmSettingsViewController)
        root.view.layoutIfNeeded()
        XCTAssertTrue(root.dock?.surfaceHost.isHidden == true)
        XCTAssertEqual(root.navigation.additionalSafeAreaInsets.bottom, 0, accuracy: 1)
        let settingsTable = try XCTUnwrap(views(settings.view).compactMap { $0 as? UITableView }.first)
        XCTAssertEqual(settings.tableView(settingsTable, numberOfRowsInSection: 0), 3)
        settings.tableView(settingsTable, didSelectRowAt: .init(row: 2, section: 0))
        try await wait { root.navigation.topViewController is NovelSettingsViewController }; try await settle(root)
        let novelSettings = try XCTUnwrap(root.navigation.topViewController as? NovelSettingsViewController)
        root.view.layoutIfNeeded()
        XCTAssertTrue(root.dock?.surfaceHost.isHidden == true)
        XCTAssertEqual(root.navigation.additionalSafeAreaInsets.bottom, 0, accuracy: 1)
        let options = try XCTUnwrap(views(novelSettings.view).compactMap { $0 as? UITableView }.first)
        XCTAssertEqual(options.numberOfRows(inSection: 0), 3)
        novelSettings.tableView(options, didSelectRowAt: .init(row: 1, section: 0))
        try await wait { root.navigation.topViewController?.view.accessibilityIdentifier == "novel.rules" }
        withExtendedLifetime(coordinator) {}
    }
    private func settle(_ root: MeowfilmRootContainerViewController,
                        file: StaticString = #filePath, line: UInt = #line) async throws {
        try await wait({
            let presenters = [root] + self.controllers(root)
            let alert = presenters.compactMap { $0.presentedViewController as? UIAlertController }.first
            return alert.map { !$0.isBeingPresented } ??
                ((root.navigation.topViewController as? PageViewController)?.canPresentPageAlert == true)
        }, file: file, line: line)
        let presenters = [root] + controllers(root)
        if let alert = presenters.compactMap({ $0.presentedViewController as? UIAlertController }).first {
            let known = alert.title == "错误" && alert.actions.map(\.title) == ["取消", "重试"] &&
                alert.message?.contains(MusicServiceError.storagePolicyMismatch.localizedDescription) == true
            XCTAssertTrue(known, "An unexpected alert must remain visible as a test failure: " +
                "\(alert.title ?? "-"); \(alert.actions.map { $0.title ?? "-" }); \(alert.message ?? "-")")
            guard known else { throw NovelError.invalidResponse }
            try await wait { !alert.isBeingPresented }
            let dismissed = expectation(description: "known unrelated music notice dismissed")
            let presenter = try XCTUnwrap(alert.presentingViewController as? PageViewController)
            presenter.dismissPageModal(alert) { dismissed.fulfill() }
            await fulfillment(of: [dismissed], timeout: 5)
        }
        try await wait({
            (root.navigation.topViewController as? PageViewController)?.canPresentPageAlert == true
        }, file: file, line: line)
    }
    private func wait(_ condition: @escaping @MainActor () -> Bool,
                      file: StaticString = #filePath, line: UInt = #line) async throws {
        if condition() { return }
        let done = expectation(for: NSPredicate { _, _ in MainActor.assumeIsolated { condition() } }, evaluatedWith: nil)
        let result = await XCTWaiter.fulfillment(of: [done], timeout: 10)
        XCTAssertEqual(result, .completed, file: file, line: line)
        guard result == .completed, condition() else { throw NovelError.invalidResponse }
    }
    private func controllers(_ root: UIViewController) -> [UIViewController] { root.children.flatMap { [$0] + controllers($0) } }
    private func views(_ root: UIView) -> [UIView] { [root] + root.subviews.flatMap(views) }
}
