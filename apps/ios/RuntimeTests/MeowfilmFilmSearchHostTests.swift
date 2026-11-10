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
        let service = try FilmSearchService(sources: sources, rules: rules,
            data: FilmSearchData { try await resources.scriptSession.connection() })
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

    func testActualSearchTapUsesTheDataOwnerAndLiveGroupRawDirectoriesWithoutAnotherSearch() async throws {
        let resources = try await hostResources()
        let app = try XCTUnwrap(UIApplication.shared.delegate as? MeowfilmAppDelegate)
        let assembly = try XCTUnwrap(app.readyAssembly())
        let client = try await resources.scriptSession.connection()
        let id = "ios-film-search-live-" + UUID().uuidString.lowercased()
        let installed = try await resources.filmSourceSettings.importLocal(name: "Live search fixture",
            source: filmSearchPlaybackCatScript, installationId: id)
        let catalog = await resources.filmSourceSettings.snapshot
        let sites = catalog.videos.filter { $0.key.installationId == id }.map { FilmSearchSite(cat: installed, source: $0) }
        XCTAssertEqual(Set(sites.map { $0.source.key.siteKey }), ["first", "second"])
        // Only the installed fixture is admitted; unrelated user sources and
        // public metadata must not be searched by this controlled host case.
        let service = FilmSearchService(processor: try FilmSearchProcessor(),
            data: FilmSearchData(connection: { client }), sites: { sites }, rules: { [] })
        addTeardownBlock {
            _ = try? await self.sourceState(client, installed, release: true)
            await service.close()
            try await assembly.leaveFilm(allowSystemOwner: false)
            try await resources.filmSourceSettings.deleteCat(id)
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
        try await waitFor { self.descendants(results).contains {
            ($0 as? FilmSearchResultsContent)?.session?.snapshot.rawCards.count == 2
        } }
        let content = try XCTUnwrap(descendants(results).compactMap { $0 as? FilmSearchResultsContent }.first)
        let session = try XCTUnwrap(content.session)
        let grid = try XCTUnwrap(find("film.search.grid", in: content.view) as? UICollectionView)
        try await waitFor { grid.numberOfItems(inSection: 0) == 1 }
        try await navigate(root.navigation, to: { $0 is FilmDetailViewController }) {
            content.collectionView(grid, didSelectItemAt: IndexPath(item: 0, section: 0))
        }
        var detail = root.navigation.topViewController as? FilmDetailViewController
        weak var releasedDetail = detail
        let group = try XCTUnwrap(detail?.filmPlaybackModel)
        try await waitFor { group.snapshot?.stage == "idle" && !group.opening }
        XCTAssertNil(group.actionError); XCTAssertNil(group.observationError)
        XCTAssertTrue(assembly.filmPlayback === group)
        XCTAssertEqual(group.snapshot?.target.kind, .group)
        XCTAssertEqual(group.snapshot?.target.value["members"]?.filmArray?.count, 2)
        XCTAssertEqual(detail?.searchContext?.snapshot.query, "真实原查询")
        XCTAssertEqual(detail?.searchContext?.sources.count, 2)
        XCTAssertTrue(session.snapshot.loading, "Opening the page must not await a gated source")
        XCTAssertNil(group.snapshot?.attempt); XCTAssertNil(group.snapshot?.current)
        let groupID = group.sessionID
        _ = try await sourceState(client, installed, release: true)
        await session.drain(); await group.waitForPendingWork()
        try await waitFor { group.snapshot?.sources.count == 3 }
        XCTAssertEqual(group.sessionID, groupID, "Late search context updates the same real Node session")
        XCTAssertEqual(detail?.searchContext?.sources.count, 3)
        XCTAssertEqual(group.snapshot?.target.kind, .group)
        XCTAssertNil(group.snapshot?.attempt); XCTAssertNil(group.snapshot?.current)
        var state = try await sourceState(client, installed)
        XCTAssertEqual(state["searches"]?.filmArray?.count, 2)
        XCTAssertEqual(state["details"], .array([])); XCTAssertEqual(state["plays"], .array([]))
        XCTAssertTrue(state["searches"]?.filmArray?.allSatisfy { $0.filmObject?["query"] == .string("真实原查询") } == true)
        try await navigate(root.navigation, to: { $0 === results }) { root.navigation.popViewController(animated: false) }
        await group.close()
        detail = nil
        try await waitFor { releasedDetail == nil }
        XCTAssertTrue(content.session === session)
        XCTAssertEqual(results.searchTargetID, "film")
        session.loadMore(); await session.drain()
        try await waitFor { session.snapshot.cards.first?.members.count == 4 }
        XCTAssertEqual(session.snapshot.round, 2); XCTAssertFalse(session.snapshot.hasMore)
        try await navigate(root.navigation, to: { $0 is FilmDetailViewController }) {
            content.collectionView(grid, didSelectItemAt: IndexPath(item: 0, section: 0))
        }
        let later = try XCTUnwrap(root.navigation.topViewController as? FilmDetailViewController)
        let laterModel = try XCTUnwrap(later.filmPlaybackModel)
        try await waitFor { laterModel.snapshot?.stage == "idle" && !laterModel.opening }
        XCTAssertNil(laterModel.actionError)
        XCTAssertTrue(later.searchContext?.session === session)
        XCTAssertEqual(later.searchContext?.sources.count, 4)
        XCTAssertEqual(laterModel.snapshot?.sources.count, 4)
        try await navigate(root.navigation, to: { $0 === results }) { root.navigation.popViewController(animated: false) }
        await laterModel.close()
        let modes = try XCTUnwrap(find("film.search.mode", in: content.view) as? UISegmentedControl)
        modes.selectedSegmentIndex = 1; modes.sendActions(for: .valueChanged)
        try await waitFor { grid.numberOfItems(inSection: 0) == 4 }
        let rawIndex = try XCTUnwrap(session.snapshot.rawCards.firstIndex {
            $0.detailID == .number(0) && $0.text("siteKey") == sites.first { $0.source.key.siteKey == "first" }?.key
        })
        try await navigate(root.navigation, to: { $0 is FilmDetailViewController }) {
            content.collectionView(grid, didSelectItemAt: IndexPath(item: rawIndex, section: 0))
        }
        let raw = try XCTUnwrap(root.navigation.topViewController as? FilmDetailViewController)
        let rawModel = try XCTUnwrap(raw.filmPlaybackModel)
        try await waitFor { rawModel.snapshot?.stage == "idle" && !rawModel.opening }
        XCTAssertNil(rawModel.actionError)
        XCTAssertEqual(rawModel.snapshot?.target.kind, .source)
        XCTAssertEqual(rawModel.snapshot?.target.value["source"]?.filmObject?["detailId"], .number(0))
        XCTAssertEqual(rawModel.snapshot?.sources.count, 1, "Raw navigation cannot admit the same-title other IDs")
        let result = try XCTUnwrap(rawModel.snapshot?.sources.first)
        rawModel.browse(sourceID: result.id)
        try await waitFor {
            rawModel.snapshot?.browsing.sourceID == result.id && rawModel.snapshot?.browsing.loading == false &&
                rawModel.snapshot?.browsing.error == nil && rawModel.snapshot?.browsing.files.count == 2 &&
                rawModel.snapshot?.browsing.files.allSatisfy { $0.candidateID != nil } == true
        }
        XCTAssertEqual(rawModel.snapshot?.browsing.lines.map(\.label), ["原始线路"])
        XCTAssertEqual(rawModel.snapshot?.browsing.originalFiles.map(\.name), ["真实文件一", "真实文件二"])
        let shared = try await client.call(module: .film, operation: "data.detail", parameters: .object([
            "source": .object(try XCTUnwrap(sites.first { $0.source.key.siteKey == "first" }).playbackSource.value),
            "detailId": .number(0),
        ]))
        XCTAssertEqual(shared.filmObject?["lines"]?.filmArray?.count, 1, "The actual data owner serves the already-read detail")
        state = try await sourceState(client, installed)
        XCTAssertEqual(state["searches"]?.filmArray?.count, 3, "Navigation, cache export and live update do not re-search")
        XCTAssertEqual(state["details"], .array([.object(["site": .string("first"), "id": .number(0)])]))
        XCTAssertEqual(state["plays"], .array([]), "Directory browsing is not address resolution or playback")
        XCTAssertNil(rawModel.snapshot?.attempt); XCTAssertNil(rawModel.snapshot?.current)
        XCTAssertTrue(raw.searchContext?.session === session)
        XCTAssertEqual(raw.searchContext?.snapshot.query, "真实原查询")
        await rawModel.close()
        PageViewController.invalidatePages(in: root)
        await service.close()
        withExtendedLifetime(coordinator) {}
    }

    private func sourceState(_ client: RuntimeHTTPClient, _ source: FilmCatSource,
                             release: Bool = false) async throws -> RuntimeObject {
        let value = try await client.call(module: .cat, operation: "invoke", parameters: .object([
            "installationId": .string(source.installationId), "generation": .number(Double(source.generation)),
            "siteId": .string("first"), "operation": .string("home"),
            "payload": .object(["release": .bool(release)]),
        ]))
        return try value.filmRequiredObject()
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


private let filmSearchPlaybackCatScript = #"""
const http = require('http');
let server, released = false;
const searches = [], details = [], plays = [], pending = new Set();
const json = (response, value) => {
  response.setHeader('content-type', 'application/json'); response.end(JSON.stringify(value));
};
const state = () => ({ searches, details, plays });
module.exports.start = () => new Promise(resolve => {
  server = http.createServer(async (request, response) => {
    const url = new URL(request.url, 'http://127.0.0.1');
    if (url.pathname === '/config' || url.pathname === '/full-config') {
      json(response, { video: { sites: ['first', 'second'].map(key => ({ key, name: key, api: '/film/' + key,
        actions: ['home', 'search', 'detail', 'play'] })) } }); return;
    }
    const chunks = []; for await (const chunk of request) chunks.push(chunk);
    const input = chunks.length ? JSON.parse(Buffer.concat(chunks).toString()) : Object.fromEntries(url.searchParams);
    const [, , site, action] = url.pathname.split('/');
    if (action === 'home') {
      if (input.release) { released = true; for (const finish of [...pending]) finish(); }
      json(response, state()); return;
    }
    if (action === 'search') {
      searches.push({ site, page: Number(input.page), query: input.wd });
      if (input.wd !== '真实原查询' || ![1, 2].includes(Number(input.page))) {
        response.writeHead(400); json(response, { error: 'HOST_SEARCH_PROTOCOL' }); return;
      }
      const ids = site === 'first' ? Number(input.page) === 1 ? [0, '0'] : ['later'] : ['other'];
      const finish = () => {
        pending.delete(finish); clearTimeout(timer);
        json(response, { pagecount: site === 'first' ? 2 : 1,
          list: ids.map(vod_id => ({ vod_id, vod_name: '真实标题', vod_remarks: '完整原备注', unknown: { raw: true } })) });
      };
      let timer;
      if (site === 'second' && !released) {
        pending.add(finish);
        response.once('close', () => { pending.delete(finish); clearTimeout(timer); });
        timer = setTimeout(() => { pending.delete(finish); response.writeHead(504); json(response, { error: 'HOST_GATE_TIMEOUT' }); }, 10000);
      } else finish();
      return;
    }
    if (action === 'detail') {
      details.push({ site, id: input.id });
      json(response, { list: [{ vod_id: input.id, vod_name: '真实标题', vod_play_from: '原始线路',
        vod_play_url: '真实文件一$file-one#真实文件二$file-two' }] }); return;
    }
    if (action === 'play') { plays.push(input); response.writeHead(400); json(response, { error: 'UNEXPECTED_PLAY' }); return; }
    response.writeHead(404); response.end();
  });
  server.listen(9987, '127.0.0.1', resolve);
});
module.exports.stop = () => { server?.closeAllConnections(); server?.close(); };
"""#
