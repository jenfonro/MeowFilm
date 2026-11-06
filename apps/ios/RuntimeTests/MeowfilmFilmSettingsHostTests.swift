import XCTest
import UIKit
@testable import MeowFilmFeature
import MeowRuntime
import MeowStorage
import MeowUI
@testable import Meowfilm

extension XCTestCase {
    @MainActor
    func hostResources() async throws -> MeowfilmResources {
        let app = try XCTUnwrap(UIApplication.shared.delegate as? MeowfilmAppDelegate)
        let ready = expectation(for: NSPredicate { _, _ in
            MainActor.assumeIsolated { app.launch.phase == .ready }
        }, evaluatedWith: nil)
        await fulfillment(of: [ready], timeout: 10)
        let assembly = try XCTUnwrap(app.readyAssembly())
        await assembly.waitForStartup()
        return try XCTUnwrap(assembly.resources)
    }
}

@MainActor
final class MeowfilmFilmSettingsHostTests: XCTestCase {
    func testFilmSettingsConfirmationAndFailureRenderInForegroundApp() async throws {
        let resources = try await hostResources()
        let app = try XCTUnwrap(UIApplication.shared.delegate as? MeowfilmAppDelegate)
        let assembly = try XCTUnwrap(app.readyAssembly())
        await waitForFilmUI {
            UIApplication.shared.connectedScenes.contains { $0.activationState == .foregroundActive }
        }
        let scene = try XCTUnwrap(UIApplication.shared.connectedScenes
            .compactMap { $0 as? UIWindowScene }.first { $0.activationState == .foregroundActive })
        let previousWindow = scene.windows.first { $0.isKeyWindow }
        let page = FilmCatSourcesViewController(service: resources.filmSourceSettings, themeProvider: assembly.appearance)
        let window = UIWindow(windowScene: scene)
        window.frame = scene.coordinateSpace.bounds
        window.windowLevel = .normal + 1
        window.rootViewController = page
        window.makeKeyAndVisible()
        defer {
            PageViewController.invalidatePages(in: page)
            window.isHidden = true; window.rootViewController = nil
            previousWindow?.makeKey()
        }
        await waitForFilmUI { page.canPresentPageAlert }
        page.confirm("删除猫源？", message: "关联视频源也将删除。") { XCTFail("Presentation is not deletion consent") }
        await waitForFilmUI { page.presentedViewController != nil && page.presentedViewController?.isBeingPresented == false }
        let confirmation = try XCTUnwrap(page.presentedViewController as? UIAlertController)
        XCTAssertEqual(confirmation.title, "删除猫源？")
        XCTAssertEqual(confirmation.actions.map(\.style), [.cancel, .destructive])
        attachFilmWindow(window, "film-cats-delete-confirmation-foreground")
        let dismissed = expectation(description: "Confirmation dismissed without an action")
        confirmation.dismiss(animated: false) { dismissed.fulfill() }
        await fulfillment(of: [dismissed], timeout: 5)
        await waitForFilmUI { page.canPresentPageAlert }
        // Inject the file reader's real failure at the presentation boundary;
        // file reading and retry dispatch are covered by the component tests.
        page.failure(ImportedFileError.changed, retry: { XCTFail("Presentation must not retry automatically") })
        await waitForFilmUI { page.presentedViewController != nil && page.presentedViewController?.isBeingPresented == false }
        let failure = try XCTUnwrap(page.presentedViewController as? UIAlertController)
        XCTAssertEqual(failure.message, ImportedFileError.changed.localizedDescription)
        XCTAssertEqual(failure.actions.map(\.title), ["取消", "重试"])
        attachFilmWindow(window, "film-file-changed-failure-foreground")
    }

    private func waitForFilmUI(_ predicate: @escaping @MainActor () -> Bool) async {
        let ready = expectation(for: NSPredicate { _, _ in MainActor.assumeIsolated { predicate() } }, evaluatedWith: nil)
        await fulfillment(of: [ready], timeout: 10)
        XCTAssertTrue(predicate())
    }

    private func attachFilmWindow(_ window: UIWindow, _ name: String) {
        XCTAssertEqual(window.windowScene?.activationState, .foregroundActive)
        window.layoutIfNeeded()
        var rendered = false
        let image = UIGraphicsImageRenderer(bounds: window.bounds).image { _ in
            rendered = window.drawHierarchy(in: window.bounds, afterScreenUpdates: true)
        }
        XCTAssertTrue(rendered, "Foreground App window must render the complete alert")
        let attachment = XCTAttachment(image: image)
        attachment.name = name; attachment.lifetime = .keepAlways
        add(attachment)
    }

    func testBundledNativeRulesUseTheSameSavedAndDraftEntryWithoutNode() async throws {
        XCTAssertNotNil(Bundle.main.url(forResource: "quickjs-native-MIT", withExtension: "txt"))
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: directory) }
        let store = try DocumentStore(baseURL: directory, namespace: FilmRuleService.documentNamespace)
        let service = try FilmRuleService(store: store)
        do {
            try await service.initialize()
            let rules = FilmRuleSet(episodeCleanRegexRules: ["^广告"],
                episodeRules: [.init(pattern: "第([0-9]+)集", replace: "第$1集", flags: "u")],
                movieRules: [.init(pattern: "电影", replace: "影片", flags: "g")], aggregateRegexRules: ["高清"])
            _ = try await service.save(rules)
            for request in [FilmRuleRequest(section: .episode, text: "广告第12集"),
                            .init(section: .movie, text: "电影"), .init(section: .search, text: "高清斗破", query: "斗破")] {
                let saved = try await service.process(request)
                let draft = try await service.process(request, draft: rules)
                XCTAssertEqual(saved, draft)
            }
            let bytes = try store.read(key: FilmRuleService.documentKey)
            _ = try await service.process(.init(section: .movie, text: "电影"), draft: .init(movieRules: [.init(pattern: "[")]))
            XCTAssertEqual(try store.read(key: FilmRuleService.documentKey), bytes)
            await service.close()
        } catch { await service.close(); throw error }
    }

    func testCatUsesRealNodeMobileRoutesAndClientOwnedPersistence() async throws {
        let resources = try await hostResources()
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: directory) }
        let store = try DocumentStore(baseURL: directory, namespace: FilmSourceService.documentNamespace)
        let transport = CatScriptTransport(connectionProvider: { try await resources.scriptSession.connection() })
        let service = FilmSourceService(transport: transport, storeFactory: { store })
        let id = "ios-host-" + UUID().uuidString.lowercased()
        let key = FilmSourceKey(installationId: id, siteKey: "fixture")
        do {
            let installed = try await service.importLocal(name: "真实 Cat", source: filmCatScript("v1"), installationId: id)
            XCTAssertTrue(installed.enabled)
            XCTAssertTrue(installed.canDetect)
            XCTAssertEqual(installed.sourceType, "local")
            let result = try await service.detect(key)
            XCTAssertEqual(result.result, "valid", result.error ?? "")
            XCTAssertEqual(result.steps.map(\.operation), ["home", "category", "detail", "play", "search"])
            XCTAssertTrue(result.playbackResolved)
            XCTAssertFalse(result.mediaPlaybackVerified)
            let options = FilmVideoOptions(enabled: false, homeVisible: false, searchEnabled: false, aggregateImages: true)
            try await service.saveVideoOptions(key, options: options, order: -7)
            let rebuilt = FilmSourceService(transport: transport, storeFactory: { store })
            try await rebuilt.initialize()
            let persisted = await rebuilt.snapshot
            XCTAssertEqual(persisted.videos.first { $0.key == key }?.options, options)
            XCTAssertEqual(persisted.videos.first { $0.key == key }?.detection, result)
            await rebuilt.close() // Borrower close must not close the App's host.
            try await service.deleteVideo(key)
            let restarted = try await service.restartCat(id)
            XCTAssertEqual(restarted.modifiedAt, installed.modifiedAt)
            _ = try await service.updateCat(id, source: filmCatScript("v2"))
            try await service.refresh()
            let deleted = await service.snapshot
            XCTAssertFalse(deleted.videos.contains { $0.key == key })
            _ = try await service.importLocal(name: "真实 Cat", source: filmCatScript("v2"), installationId: id)
            let restored = await service.snapshot
            XCTAssertTrue(restored.videos.contains { $0.key == key })
            _ = try await service.enableCat(id, enabled: false)
            do { _ = try await service.detect(key); XCTFail("Disabled detection") }
            catch { XCTAssertEqual((error as? FilmSourceError)?.code, "DISABLED") }
            do { _ = try await service.restartCat(id); XCTFail("Disabled restart") }
            catch { XCTAssertEqual((error as? FilmSourceError)?.code, "DISABLED") }
            try await service.deleteCat(id)
            await service.close()
            let connection = try await resources.scriptSession.connection()
            XCTAssertFalse(connection.isClosed)
        } catch {
            let snapshot = await service.snapshot
            if snapshot.cats.contains(where: { $0.installationId == id }) {
                do { try await service.deleteCat(id) } catch { XCTFail("Cat cleanup: \(error)") }
            }
            await service.close()
            throw error
        }
    }
}

// Run these classes in two separate xcodebuild invocations. The second checks the
// actual App startup, not owner reconstruction within the same NodeMobile process.
@MainActor
final class MeowfilmCatColdStartPrepareTests: XCTestCase {
    func testPrepareEnabledAndDisabledInstallationsForNextProcess() async throws {
        guard ProcessInfo.processInfo.environment["MEOWFILM_CAT_COLD_PHASE"] == "prepare" else {
            throw XCTSkip("Run with the documented two-process cold-start procedure")
        }
        let service = try await hostResources().filmSourceSettings
        for id in filmColdIDs {
            let before = await service.snapshot
            if before.cats.contains(where: { $0.installationId == id }) { try await service.deleteCat(id) }
            let cat = try await service.importLocal(name: id, source: filmCatScript(id), installationId: id)
            XCTAssertEqual(cat.sites.first?.name, "Fixture \(id) starts=1 checks=0")
        }
        let key = FilmSourceKey(installationId: filmColdIDs[0], siteKey: "fixture")
        try await service.deleteVideo(key)
        _ = try await service.enableCat(filmColdIDs[1], enabled: false)
        let disabledKey = FilmSourceKey(installationId: filmColdIDs[1], siteKey: "fixture")
        try await service.saveVideoOptions(disabledKey, options: .init(enabled: false, homeVisible: false,
            searchEnabled: false, aggregateImages: true), order: -9)
    }
}

@MainActor
final class MeowfilmCatColdStartRestoreTests: XCTestCase {
    func testAppColdStartRestoresOnlyEnabledScriptsWithoutProbingOrResurrectingVideos() async throws {
        guard ProcessInfo.processInfo.environment["MEOWFILM_CAT_COLD_PHASE"] == "restore" else {
            throw XCTSkip("Requires the preceding prepare process on this test simulator")
        }
        let service = try await hostResources().filmSourceSettings
        addTeardownBlock {
            for id in filmColdIDs { try await service.deleteCat(id) }
        }
        let snapshot = await service.snapshot
        let enabled = try XCTUnwrap(snapshot.cats.first { $0.installationId == filmColdIDs[0] })
        let disabled = try XCTUnwrap(snapshot.cats.first { $0.installationId == filmColdIDs[1] })
        XCTAssertTrue(enabled.canDetect)
        XCTAssertEqual(enabled.sites.first?.name, "Fixture \(filmColdIDs[0]) starts=2 checks=0")
        XCTAssertEqual(disabled.state, "disabled")
        XCTAssertEqual(disabled.sites.first?.name, "Fixture \(filmColdIDs[1]) starts=1 checks=0")
        XCTAssertFalse(snapshot.videos.contains { $0.key.installationId == enabled.installationId })
        let video = try XCTUnwrap(snapshot.videos.first { $0.key.installationId == disabled.installationId })
        XCTAssertEqual(video.order, -9)
        XCTAssertEqual(video.options, .init(enabled: false, homeVisible: false, searchEnabled: false, aggregateImages: true))
        let resumed = try await service.enableCat(disabled.installationId, enabled: true)
        XCTAssertEqual(resumed.sites.first?.name, "Fixture \(filmColdIDs[1]) starts=2 checks=0")
        let result = try await service.detect(video.key)
        XCTAssertEqual(result.result, "valid", result.error ?? "")
    }
}

private let filmColdIDs = ["ios-film-cold-enabled", "ios-film-cold-disabled"]

/// Synthetic Cat HTTP program. Each route validates the actual wire method and
/// complete payload, including Unicode, punctuation and typed POST fields.
private func filmCatScript(_ revision: String) -> String {
    // All callers supply fixed test literals, never external JavaScript input.
    """
    const http = require('http'), fs = require('fs');
    const revision = '\(revision)';
    const API = '/spider/film-fixture/3', TYPE = '电影/中文 & 1';
    const VIDEO = '片/中文?键=一&二', FLAG = '线路 甲', EPISODE = 'episode/中文?token=一&part=2';
    const number = path => fs.existsSync(path) ? Number(fs.readFileSync(path, 'utf8')) : 0;
    const starts = number('/starts.txt') + 1;
    fs.writeFileSync('/starts.txt', String(starts));
    const order = ['home', 'category', 'detail', 'play', 'search'];
    let next = 0, server;
    function requireValue(value, message) { if (!value) throw new Error(message); }
    function expect(operation, actual, expected) {
      requireValue(order[next] === operation, 'action order: ' + operation);
      requireValue(JSON.stringify(Object.keys(actual).sort()) === JSON.stringify(Object.keys(expected).sort()), 'fields');
      for (const key of Object.keys(expected))
        requireValue(JSON.stringify(actual[key]) === JSON.stringify(expected[key]), operation + ' argument ' + key);
      next = (next + 1) % order.length;
    }
    const routes = new Map([
      ['GET ' + API + '/home', input => {
        expect('home', input, {});
        fs.writeFileSync('/checks.txt', String(number('/checks.txt') + 1));
        return { data: { types: [{ type_id: TYPE, type_name: '电影' }] } };
      }],
      ['POST ' + API + '/category', input => {
        expect('category', input, { id: TYPE, page: 1, filter: true, filters: {} });
        return { data: { items: [{ vod_id: VIDEO, vod_name: '受控影片' }] } };
      }],
      ['POST ' + API + '/detail', input => {
        expect('detail', input, { id: VIDEO, vod_id: VIDEO });
        return { list: [{ vod_id: VIDEO, vod_play_from: FLAG, vod_play_url: '第一集$' + EPISODE }] };
      }],
      ['POST ' + API + '/play', input => {
        expect('play', input, { flag: FLAG, id: EPISODE, siteApi: API });
        return { url: ['测试清晰度', 'https://media.invalid/film-fixture.m3u8'] };
      }],
      ['GET ' + API + '/search', input => {
        expect('search', input, { wd: '斗破', page: '1' });
        return { list: [{ vod_id: VIDEO }] };
      }]
    ]);
    function json(response, value) {
      response.setHeader('content-type', 'application/json; charset=utf-8'); response.end(JSON.stringify(value));
    }
    async function route(request, response) {
      const url = new URL(request.url, 'http://127.0.0.1');
      if (url.pathname === '/full-config' || url.pathname === '/config') {
        requireValue(request.method === 'GET', 'config method');
        return json(response, { video: { sites: [{ key: 'fixture',
          name: 'Fixture ' + revision + ' starts=' + starts + ' checks=' + number('/checks.txt'), api: API }] } });
      }
      if (!url.pathname.startsWith(API + '/')) { response.statusCode = 404; return json(response, {}); }
      const handler = routes.get(request.method + ' ' + url.pathname);
      requireValue(typeof handler === 'function', 'method or route: ' + request.method + ' ' + url.pathname);
      const chunks = []; for await (const chunk of request) chunks.push(Buffer.from(chunk));
      const body = Buffer.concat(chunks).toString('utf8');
      let input;
      if (request.method === 'GET') {
        requireValue(body.length === 0, 'GET body'); input = Object.fromEntries(url.searchParams);
      } else {
        requireValue(url.search === '' && body.length > 0, 'POST JSON without query'); input = JSON.parse(body);
      }
      json(response, handler(input));
    }
    module.exports.start = () => new Promise(resolve => {
      server = http.createServer((request, response) => {
        route(request, response).catch(error => { response.statusCode = 500; json(response, { error: error.message }); });
      });
      server.listen(Number(process.env.PORT), '127.0.0.1', resolve);
    });
    module.exports.stop = () => { server?.closeAllConnections(); server?.close(); };
    """
}
