import XCTest
import UIKit
import MeowUI
import MeowRuntime
import MeowStorage
import MeowMusicFeature
@testable import MeowFilmFeature
@testable import Meowfilm

@MainActor
final class MeowfilmFilmSearchHostTests: XCTestCase {
    func testRealCatSearchReadsCompleteInstalledSourcePagesAndRetainsDetailIdentity() async throws {
        let resources = try await hostResources()
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: directory) }
        let sourceStore = try DocumentStore(baseURL: directory, namespace: FilmSourceService.documentNamespace)
        let ruleStore = try DocumentStore(baseURL: directory, namespace: FilmRuleService.documentNamespace)
        let transport = CatScriptTransport(connectionProvider: { try await resources.scriptSession.connection() })
        let sources = FilmSourceService(transport: transport, storeFactory: { sourceStore })
        let rules = try FilmRuleService(store: ruleStore)
        let service = try FilmSearchService(sources: sources, rules: rules)
        let id = "ios-film-search-" + UUID().uuidString.lowercased()
        do {
            let installed = try await sources.importLocal(name: "Search transport fixture", source: filmSearchCatScript, installationId: id)
            let session = try service.session(for: "原查询")
            await session.drain()
            XCTAssertEqual(session.snapshot.siteItems.count, 125, "A real Cat response must not be truncated by the client")
            XCTAssertEqual(session.snapshot.metadataState, .unavailable)
            XCTAssertTrue(session.snapshot.hasMore)
            session.loadMore(); await session.drain()
            XCTAssertEqual(session.snapshot.siteItems.count, 126)
            XCTAssertEqual(session.snapshot.round, 2)
            XCTAssertFalse(session.snapshot.hasMore)
            let context = session.context(for: try XCTUnwrap(session.snapshot.cards.first))
            XCTAssertEqual(context.snapshot.query, "原查询")
            XCTAssertTrue(context.sources.allSatisfy { $0.catField("installationId") == .string(id) })
            XCTAssertTrue(context.sources.allSatisfy { $0.catField("generation") == .number(Double(installed.generation)) })
            XCTAssertNil(context.detail.playbackURL)
            XCTAssertTrue(context.detail.summary.isEmpty)
            await service.close()
            try await sources.deleteCat(id)
            await sources.close(); await rules.close()
        } catch {
            await service.close()
            _ = try? await transport.call("uninstall", ["installationId": .string(id)])
            await sources.close(); await rules.close()
            throw error
        }
    }

    func testActualCoordinatorEntryUsesMusicAndFilmPageDefaultsWithoutSubmitting() async throws {
        let app = try XCTUnwrap(UIApplication.shared.delegate as? MeowfilmAppDelegate)
        try await waitFor { app.launch.phase == .ready }
        let assembly = try XCTUnwrap(app.readyAssembly())
        await assembly.waitForStartup()
        let (window, previous) = try hostWindow()
        let coordinator = MeowfilmCoordinator(window: window, assembly: assembly, resetUserData: {})
        coordinator.start()
        let root = try XCTUnwrap(window.rootViewController as? MeowfilmRootContainerViewController)
        defer { unmount(window, previous: previous) }
        root.loadViewIfNeeded()
        let main = try XCTUnwrap(root.navigation.viewControllers.first as? MeowfilmPrimaryShellViewController)
        try await waitFor { main.isPageVisible }
        try await dismissKnownMusicNotice(root)
        try await waitFor { main.canPresentPageAlert }
        for (page, target) in [(MeowfilmPrimaryPage.recommend, "music"), (.channels, "music"), (.film, "film")] {
            try await select(page, on: main)
            try await navigate(root.navigation, to: { $0 is MusicSearchViewController }) {
                coordinator.showSearch()
            }
            let search = try XCTUnwrap(root.navigation.topViewController as? MusicSearchViewController)
            XCTAssertEqual(search.searchTargetID, target)
            XCTAssertFalse(descendants(search).contains { $0 is FilmSearchResultsContent }, "Entry creation must not start film search")
            try await dismissKnownMusicNotice(root)
            search.view.endEditing(true)
            try await navigate(root.navigation, to: { $0 === main }) {
                root.navigation.popViewController(animated: false)
            }
        }
        withExtendedLifetime(coordinator) {}
    }

    func testActualSearchTapRetainsLiveGroupTMDBAndLaterPageWithoutAnotherSearch() async throws {
        let app = try XCTUnwrap(UIApplication.shared.delegate as? MeowfilmAppDelegate)
        try await waitFor { app.launch.phase == .ready }
        let assembly = try XCTUnwrap(app.readyAssembly())
        await assembly.waitForStartup()
        let reads = FilmSearchHostReads()
        let metadata = FilmSearchHostMetadata()
        let service = FilmSearchService(processor: try FilmSearchProcessor(), transport: .init(request: { _, params in
            await reads.read(params)
        }, upload: { _, _, _ in throw FilmSearchError.unavailable }), sites: {
            ["first", "second"].enumerated().map { index, installation in
                FilmSearchSite(cat: .init(installationId: installation, name: installation, sourceType: "local", generation: Int64(index + 3)),
                    source: .init(key: .init(installationId: installation, siteKey: "same"), name: installation, api: "fixture", actions: ["search"], methods: [:], order: index))
            }
        }, rules: { ["高清"] }, metadata: metadata)
        addTeardownBlock {
            await reads.release(); await metadata.release()
            await service.close()
        }
        let (window, previous) = try hostWindow()
        let coordinator = MeowfilmCoordinator(window: window, assembly: assembly, resetUserData: {})
        coordinator.start()
        let root = try XCTUnwrap(window.rootViewController as? MeowfilmRootContainerViewController)
        defer { unmount(window, previous: previous) }
        root.loadViewIfNeeded()
        let main = try XCTUnwrap(root.navigation.topViewController as? MeowfilmPrimaryShellViewController)
        try await waitFor { main.isPageVisible }
        try await dismissKnownMusicNotice(root)
        try await waitFor { main.canPresentPageAlert }
        let category = MeowfilmSearchComposition.category(service: service, images: assembly.filmImages,
            themeProvider: assembly.appearance) { coordinator.showFilmSearchResult($0) }
        let results = MusicSearchResultsViewController(model: try XCTUnwrap(assembly.music), query: "真实原查询",
            appearance: assembly.appearance, router: coordinator, history: SearchHistoryStore(),
            primaryTarget: MeowfilmSearchComposition.music, additionalCategory: category, initialAdditional: true)
        try await navigate(root.navigation, to: { $0 === results }) {
            root.navigation.pushViewController(results, animated: false)
        }
        try await waitFor { self.descendants(results).contains { ($0 as? FilmSearchResultsContent)?.session?.snapshot.cards.count == 1 } }
        let content = try XCTUnwrap(descendants(results).compactMap { $0 as? FilmSearchResultsContent }.first)
        let session = try XCTUnwrap(content.session)
        let grid = try XCTUnwrap(find("film.search.grid", in: content.view) as? UICollectionView)
        try await waitFor { grid.numberOfItems(inSection: 0) == 1 }
        try await navigate(root.navigation, to: { $0 is FilmDetailViewController }) {
            content.collectionView(grid, didSelectItemAt: IndexPath(item: 0, section: 0))
        }
        var detail = root.navigation.topViewController as? FilmDetailViewController
        weak var releasedDetail = detail
        XCTAssertEqual(detail?.searchContext?.selectedCard.sourceKind, "site")
        XCTAssertEqual(detail?.searchContext?.snapshot.query, "真实原查询")
        XCTAssertEqual(detail?.searchContext?.sources.count, 1)
        XCTAssertTrue(detail?.searchContext?.snapshot.loading == true, "A tap does not claim the in-flight session is complete")
        XCTAssertTrue(detail?.detail.summary.isEmpty == true)
        XCTAssertNil(detail?.detail.playbackURL)
        await reads.release(); await metadata.release()
        await session.drain()
        try await waitFor { detail?.detail.summary == "已得真实结构简介" }
        XCTAssertTrue(detail?.searchContext?.session === session)
        XCTAssertEqual(detail?.searchContext?.card.displayKey, "tmdb:tv:7")
        XCTAssertEqual(detail?.searchContext?.selectedCard.sourceKind, "site")
        XCTAssertEqual(detail?.searchContext?.sources.count, 2)
        XCTAssertEqual(Set(detail?.searchContext?.sources.compactMap { $0.catField("installationId")?.catText } ?? []), ["first", "second"])
        XCTAssertEqual(Set(detail?.searchContext?.sources.compactMap { $0.catField("generation")?.catInteger } ?? []), [3, 4])
        XCTAssertTrue(detail?.searchContext?.sources.allSatisfy { $0.catField("siteDetail") == .string("same-id") } == true)
        XCTAssertEqual(detail?.searchContext?.metadataDetail?.catField("number_of_episodes"), .number(24))
        XCTAssertEqual(detail?.detail.episodeCount, 24)
        XCTAssertNil(detail?.detail.playbackURL)
        try await navigate(root.navigation, to: { $0 === results }) {
            root.navigation.popViewController(animated: false)
        }
        detail = nil
        try await waitFor { releasedDetail == nil }
        XCTAssertTrue(content.session === session)
        XCTAssertEqual(results.searchTargetID, "film")
        session.loadMore(); await session.drain()
        try await waitFor { session.snapshot.cards.first?.members.count == 3 }
        try await navigate(root.navigation, to: { $0 is FilmDetailViewController }) {
            content.collectionView(grid, didSelectItemAt: IndexPath(item: 0, section: 0))
        }
        let second = try XCTUnwrap(root.navigation.topViewController as? FilmDetailViewController)
        XCTAssertTrue(second.searchContext?.session === session)
        XCTAssertEqual(second.searchContext?.sources.count, 3, "All acquired later-page sources reach the actual detail page")
        XCTAssertEqual(second.searchContext?.snapshot.query, "真实原查询")
        XCTAssertEqual(second.searchContext?.snapshot.round, 2)
        let calls = await reads.calls, queries = await metadata.queries, detailCalls = await metadata.details
        XCTAssertEqual(calls.map { "\($0.0):\($0.1)" }.sorted(), ["first:1", "first:2", "second:1"])
        XCTAssertTrue(calls.allSatisfy { $0.2 == "真实原查询" })
        XCTAssertEqual(queries, ["真实原查询"])
        XCTAssertEqual(detailCalls, ["tv:7"])
        PageViewController.invalidatePages(in: root)
        await service.close()
        XCTAssertFalse(session.snapshot.loading)
    }

    private func descendants(_ controller: UIViewController) -> [UIViewController] {
        controller.children.flatMap { [$0] + descendants($0) }
    }
    private func find(_ id: String, in view: UIView) -> UIView? {
        if view.accessibilityIdentifier == id { return view }
        for child in view.subviews { if let found = find(id, in: child) { return found } }
        return nil
    }
    private func hostWindow() throws -> (UIWindow, UIWindow?) {
        let scene = try XCTUnwrap(UIApplication.shared.connectedScenes.compactMap { $0 as? UIWindowScene }.first { $0.activationState == .foregroundActive })
        let previous = scene.windows.first { $0.isKeyWindow }
        let window = UIWindow(windowScene: scene)
        window.frame = scene.coordinateSpace.bounds
        window.windowLevel = .normal + 1
        return (window, previous)
    }
    private func unmount(_ window: UIWindow, previous: UIWindow?) {
        if let root = window.rootViewController { PageViewController.invalidatePages(in: root) }
        window.isHidden = true; window.rootViewController = nil
        previous?.makeKey()
    }
    private func navigate(_ navigation: PageNavigationController,
                          to matches: @escaping (UIViewController) -> Bool, action: () -> Void) async throws {
        try await settleNavigation(navigation)
        let shown = XCTestExpectation(description: "The real navigation owner finished showing the requested page")
        let previous = navigation.didShowPage
        navigation.didShowPage = { page in
            previous?(page)
            if matches(page) { shown.fulfill() }
        }
        defer { navigation.didShowPage = previous }
        action()
        let result = await XCTWaiter.fulfillment(of: [shown], timeout: 10)
        XCTAssertEqual(result, .completed)
        guard result == .completed else { throw HostFailure.navigation }
        try await settleNavigation(navigation)
        let page = try XCTUnwrap(navigation.topViewController as? PageViewController)
        XCTAssertTrue(matches(page))
        XCTAssertTrue(page.isPageVisible)
        XCTAssertNotNil(page.viewIfLoaded?.window)
    }

    private func select(_ page: MeowfilmPrimaryPage, on main: MeowfilmPrimaryShellViewController) async throws {
        guard main.selectedPage != page else { return }
        let pages = try XCTUnwrap(main.children.compactMap { $0 as? RetainedPagesController }.first)
        let selected = XCTestExpectation(description: "The actual primary-page transition completed")
        let previous = pages.didSelect
        pages.didSelect = { index in
            previous?(index)
            if main.selectedPage == page { selected.fulfill() }
        }
        defer { pages.didSelect = previous }
        main.select(page)
        let result = await XCTWaiter.fulfillment(of: [selected], timeout: 10)
        XCTAssertEqual(result, .completed)
        guard result == .completed else { throw HostFailure.navigation }
        XCTAssertEqual(main.selectedPage, page)
    }

    private func settleNavigation(_ navigation: PageNavigationController) async throws {
        let root = try XCTUnwrap(navigation.parent as? MeowfilmRootContainerViewController)
        // didShow can be followed by an appearance-triggered music notice.
        // Finish its presentation and dismiss only the known unrelated error
        // before issuing the next real navigation action.
        try await waitFor { navigation.transitionCoordinator == nil && root.transitionCoordinator == nil }
        try await dismissKnownMusicNotice(root)
        try await waitFor {
            navigation.transitionCoordinator == nil && root.transitionCoordinator == nil &&
                root.presentedViewController == nil
        }
    }

    private func dismissKnownMusicNotice(_ root: MeowfilmRootContainerViewController) async throws {
        guard let alert = root.presentedViewController as? UIAlertController else { return }
        // A dedicated simulator can retain an unrelated music Keychain-policy
        // error. Close only that observed notice; never reset data or retry it.
        let known = alert.title == "错误" && alert.actions.map(\.title) == ["取消", "重试"] &&
            alert.message?.contains(MusicServiceError.storagePolicyMismatch.localizedDescription) == true
        XCTAssertTrue(known, "Unexpected notification must not be silently dismissed")
        guard known else { throw HostFailure.unexpectedNotice }
        try await waitFor { !alert.isBeingPresented && alert.transitionCoordinator == nil }
        let dismissed = XCTestExpectation(description: "Observed music notification dismissed without retry")
        alert.dismiss(animated: false) { dismissed.fulfill() }
        let result = await XCTWaiter.fulfillment(of: [dismissed], timeout: 10)
        XCTAssertEqual(result, .completed)
        guard result == .completed else { throw HostFailure.navigation }
    }

    private func waitFor(_ predicate: @escaping @MainActor () -> Bool) async throws {
        if predicate() { return }
        let ready = XCTNSPredicateExpectation(predicate: NSPredicate { _, _ in
            MainActor.assumeIsolated { predicate() }
        }, object: nil)
        let result = await XCTWaiter.fulfillment(of: [ready], timeout: 10)
        XCTAssertEqual(result, .completed)
        guard result == .completed, predicate() else { throw HostFailure.notReady }
    }
    private enum HostFailure: Error { case navigation, unexpectedNotice, notReady }

}

private actor FilmSearchHostReads {
    var calls: [(String, Int, String)] = []
    private var continuation: CheckedContinuation<Void, Never>?
    private var released = false
    func release() { released = true; continuation?.resume(); continuation = nil }
    func read(_ params: RuntimeValue) async -> RuntimeValue {
        let installation = params.catField("installationId")?.catText ?? ""
        let payload = params.catField("payload") ?? .null
        let page = payload.catField("page")?.catInteger ?? 0
        calls.append((installation, page, payload.catField("wd")?.catText ?? ""))
        if installation == "second", !released { await withCheckedContinuation { continuation = $0 } }
        return .object(["pagecount": .number(installation == "first" ? 2 : 1), "list": .array([
            .object(["vod_id": .string(page == 1 ? "same-id" : "later-id"), "vod_name": .string("斗破苍穹高清"),
                     "vod_remarks": .string("更新至12集")])])])
    }
}

private actor FilmSearchHostMetadata: FilmSearchMetadataReading {
    var queries: [String] = [], details: [String] = []
    private var continuation: CheckedContinuation<Void, Never>?
    private var released = false
    func release() { released = true; continuation?.resume(); continuation = nil }
    func search(query: String) async -> RuntimeValue {
        queries.append(query)
        if !released { await withCheckedContinuation { continuation = $0 } }
        return .object(["results": .array([.object(["id": .number(7), "media_type": .string("tv"), "name": .string("斗破苍穹")])])])
    }
    func detail(type: String, id: Int) -> RuntimeValue {
        details.append("\(type):\(id)")
        return .object(["id": .number(7), "name": .string("斗破苍穹"), "overview": .string("已得真实结构简介"),
                        "number_of_seasons": .number(2), "number_of_episodes": .number(24), "first_air_date": .string("2026-01-01")])
    }
    nonisolated func imageURL(path: String) -> URL? { nil }
}

private let filmSearchCatScript = #"""
const http = require('http');
let server, nextPage = 1;
const routes = new Map([['GET /film/search/search', (url, response) => {
  const page = Number(url.searchParams.get('page'));
  if (url.searchParams.get('wd') !== '原查询' || page !== nextPage || page > 2) {
    response.statusCode = 400; response.end(JSON.stringify({error:'Search wire contract'})); return;
  }
  nextPage++;
  const list = Array.from({length:page === 1 ? 125 : 1}, (_, index) => ({
    vod_id:'page-' + page + '-' + index, vod_name:'影片' + page + '-' + index, vod_remarks:'来源备注'
  }));
  response.end(JSON.stringify({pagecount:2,list}));
}]]);
module.exports.start = () => new Promise(resolve => {
  server = http.createServer((request, response) => {
    const url = new URL(request.url, 'http://127.0.0.1');
    response.setHeader('content-type', 'application/json; charset=utf-8');
    if (url.pathname === '/config' || url.pathname === '/full-config') {
      response.end(JSON.stringify({video:{sites:[{key:'search',name:'真实搜索源',api:'/film/search'}]}}));
      return;
    }
    const route = routes.get(request.method + ' ' + url.pathname);
    if (route) return route(url, response);
    response.statusCode = 400; response.end(JSON.stringify({error:'Search wire contract'}));
  });
  server.listen(Number(process.env.PORT), '127.0.0.1', resolve);
});
module.exports.stop = () => { server?.closeAllConnections(); server?.close(); };
"""#
