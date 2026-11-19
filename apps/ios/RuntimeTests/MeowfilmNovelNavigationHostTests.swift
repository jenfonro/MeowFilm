import XCTest
import UIKit
import MeowUI
import MeowMusicFeature
import MeowRuntime
@testable import MeowPlayback
@testable import MeowNovelFeature
@testable import Meowfilm

@MainActor
final class MeowfilmNovelNavigationHostTests: XCTestCase {
    func testActualNovelDiscoveryDetailReaderDockRestoreAndSettingsRoutes() async throws {
        let app = try XCTUnwrap(UIApplication.shared.delegate as? MeowfilmAppDelegate)
        try await wait { app.launch.phase == .ready }
        let assembly = try XCTUnwrap(app.readyAssembly()); await assembly.waitForStartup()
        let resources = try await hostResources(), feature = try XCTUnwrap(assembly.novel)
        _ = try XCTUnwrap(assembly.music, "The main shell requires the music startup owner")
        let suffix = UUID().uuidString.lowercased()
        let url = "https://novel-navigation-\(suffix).invalid/", title = "雨夜来信 \(suffix)"
        let html = """
        <script>
        const base = '\(url)';
        function getfinds() { return [{title:'全部',url:base+'find'}]; }
        function find(url, page) {
          if (url !== base+'find') throw new Error('Unexpected discovery URL');
          return page === 1 ? [info()] : [];
        }
        function info() { return {bookUrl:base+'book',tocUrl:base+'toc',name:'\(title)',author:'林舟'}; }
        function chapter() { return [{name:'第一章 雨声',chapterUrl:base+'1'}]; }
        function content() { return '原生阅读页面正文。'; }
        </script>
        """
        let definition = String(decoding: try JSONSerialization.data(withJSONObject: [
            "bookSourceName": "Navigation fixture", "bookSourceUrl": url, "enabledExplore": true, "html": html,
        ]), as: UTF8.self)
        let receipt = try await feature.sourcesClient.importSources(text: definition), source = try XCTUnwrap(receipt.sources.first)
        addTeardownBlock { @MainActor in
            if let current = try await feature.sourcesClient.list().first(where: { $0.id == source.id }) {
                try await feature.sourcesClient.remove(current)
            }
            try await feature.refreshSources()
        }
        addTeardownBlock { @MainActor in
            let entries = try await resources.novelLibrary.snapshot().entries
            for entry in entries where entry.book.installationId == source.id && entry.book.bookUrl == url + "book" {
                try await resources.novelLibrary.deleteHistory(entry.id)
            }
            try await feature.reloadLocal()
        }
        XCTAssertTrue(receipt.failures.isEmpty)
        XCTAssertEqual(source.protocol, "qysg")
        XCTAssertTrue(source.enabled && source.enabledExplore && source.actions.contains("explore"))
        try await feature.refreshSources()
        let scene = try XCTUnwrap(UIApplication.shared.connectedScenes.compactMap { $0 as? UIWindowScene }.first { $0.activationState == .foregroundActive })
        let previous = scene.windows.first { $0.isKeyWindow }, oldPresenter = feature.web.present
        let window = UIWindow(windowScene: scene); window.frame = scene.coordinateSpace.bounds; window.windowLevel = .normal + 1
        let coordinator = MeowfilmCoordinator(window: window, assembly: assembly, resetUserData: {})
        coordinator.start()
        let root = try XCTUnwrap(window.rootViewController as? MeowfilmRootContainerViewController)
        addTeardownBlock { @MainActor in
            PageViewController.invalidatePages(in: root); window.isHidden = true; window.rootViewController = nil; previous?.makeKey()
            feature.web.present = oldPresenter
        }
        try await wait { root.navigation.topViewController is MeowfilmPrimaryShellViewController }
        let main = try XCTUnwrap(root.navigation.topViewController as? MeowfilmPrimaryShellViewController)
        try await settle(root)
        main.select(.novel)
        let home = try XCTUnwrap(controllers(main).compactMap { $0 as? NovelHomeViewController }.first)
        // Select before yielding so initial discovery cannot invoke an unrelated installed source.
        home.discovery.selectSource(id: source.id)
        addTeardownBlock { @MainActor in await home.discovery.close() }
        try await wait { main.selectedPage == .novel && home.isPageVisible }
        let table = try XCTUnwrap(views(home.view).first { $0.accessibilityIdentifier == "novel.books" } as? UITableView)
        try await wait {
            home.discovery.selectedSource?.id == source.id && !home.discovery.loading &&
                home.discovery.books.contains { $0.installationId == source.id && $0.bookUrl == url + "book" } &&
                table.numberOfRows(inSection: 0) == home.discovery.books.count
        }
        let index = try XCTUnwrap(home.discovery.books.firstIndex { $0.installationId == source.id && $0.bookUrl == url + "book" })
        let book = home.discovery.books[index]
        XCTAssertEqual(book.name, title)
        XCTAssertEqual(book.author, "林舟")
        XCTAssertFalse(feature.local.entries.contains { $0.book.installationId == source.id },
                       "Discovery must provide the navigation entry without retaining a shelf book")
        home.tableView(table, didSelectRowAt: .init(row: index, section: 0))
        try await wait { root.navigation.topViewController is NovelDetailViewController }; try await settle(root)
        let detail = try XCTUnwrap(root.navigation.topViewController as? NovelDetailViewController)
        XCTAssertFalse(root.dock?.isHidden == true)
        let currentItem = assembly.playback.activeAudioMedia
        let read = try XCTUnwrap(views(detail.view).first { $0.accessibilityIdentifier == "novel.detail.read" } as? UIButton)
        read.sendActions(for: .touchUpInside)
        try await wait { root.navigation.topViewController is NovelReaderViewController }; try await settle(root)
        let reader = try XCTUnwrap(root.navigation.topViewController as? NovelReaderViewController)
        addTeardownBlock { @MainActor in await reader.session.close() }
        try await wait { reader.session.content != nil && !reader.session.loading }
        XCTAssertEqual(reader.session.book.installationId, source.id)
        XCTAssertEqual(reader.session.book.bookUrl, book.bookUrl)
        XCTAssertFalse(reader.session.content?.blocks.isEmpty ?? true)
        XCTAssertTrue(root.dock?.isHidden == true)
        XCTAssertTrue(assembly.playback.activeAudioMedia === currentItem)
        root.view.layoutIfNeeded(); XCTAssertEqual(root.navigation.additionalSafeAreaInsets.bottom, 0, accuracy: 1)
        let attachment = XCTAttachment(image: UIGraphicsImageRenderer(bounds: window.bounds).image { _ in window.drawHierarchy(in: window.bounds, afterScreenUpdates: true) })
        attachment.name = "novel-real-app-reader-hidden-dock"; attachment.lifetime = .keepAlways; add(attachment)
        root.navigation.popViewController(animated: false); try await wait { root.navigation.topViewController === detail }; try await settle(root)
        XCTAssertFalse(root.dock?.isHidden == true)
        XCTAssertTrue(assembly.playback.activeAudioMedia === currentItem)
        root.navigation.popViewController(animated: false); try await wait { root.navigation.topViewController === main }; try await settle(root)
        try await wait { home.isPageVisible && !home.discovery.loading }
        XCTAssertEqual(home.discovery.selectedSource?.id, source.id)
        XCTAssertEqual(home.discovery.books.map(\.bookUrl), [book.bookUrl])
        XCTAssertEqual(table.numberOfRows(inSection: 0), 1)
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
