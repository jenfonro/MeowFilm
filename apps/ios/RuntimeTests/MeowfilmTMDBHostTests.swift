import XCTest
import UIKit
import Network
import MeowRuntime
import MeowUI
@testable import MeowFilmFeature
@testable import Meowfilm

/// TEST_HOST + the App's actual NodeMobile runtime, TMDB adapter and Artwork
/// owner. Only the HTTP upstream and its credentials are synthetic.
@MainActor
final class MeowfilmTMDBHostTests: XCTestCase {
    func testActualTMDBSettingsSearchDetailsAndArtworkPreserveReplaceClearAndFailedSave() async throws {
        let resources = try await hostResources()
        let assembly = try assembly()
        let original = try await resources.filmMetadata.settings()
        try requireEmptyCredentials(original)
        let image = png()
        let upstream = try TMDBTestUpstream { request in
            if request.path.hasPrefix("/failure/") { return .json(.object(["error": .string("Synthetic failure")]), status: 503) }
            if request.path.contains("/images/") { return .bytes(image, type: "image/png") }
            if request.path.hasSuffix("/3/search/multi") { return .json(tmdbHostSearch) }
            if request.path.hasSuffix("/3/tv/7") { return .json(tmdbHostTV) }
            if request.path.hasSuffix("/3/movie/42") { return .json(tmdbHostMovie) }
            return .json(.object(["error": .string("Unexpected route")]), status: 404)
        }
        try await upstream.start()
        let search = try XCTUnwrap(assembly.filmSearch)
        let settings = try XCTUnwrap(assembly.filmSettings)
        addTeardownBlock {
            defer { upstream.stop() }
            try await Self.restoreSettings(settings.tmdb, service: resources.filmMetadata, original: original, search: search)
        }
        let connection = try await resources.scriptSession.connection()
        try await connection.validateAppModules()
        let root = FilmSettingsViewController(model: settings, themeProvider: assembly.appearance)
        let navigation = UINavigationController(rootViewController: root)
        let (window, previous) = try await mount(navigation)
        defer { unmount(window, previous: previous) }
        root.loadViewIfNeeded()
        await waitFor { root.canPresentPageAlert }
        let menu = try XCTUnwrap(root.children.compactMap { $0 as? FilmSettingsListController }.first)
        try select(menu, "film.settings.tmdb")
        await waitFor { navigation.topViewController is FilmTMDBSettingsViewController }
        let page = try XCTUnwrap(navigation.topViewController as? FilmTMDBSettingsViewController)
        await waitFor { settings.tmdb.saved != nil && !settings.tmdb.busy && page.canPresentPageAlert }
        XCTAssertEqual(try field(page, "apiKey").text, "")
        XCTAssertEqual(try field(page, "accessToken").text, "")
        XCTAssertTrue(try field(page, "apiKey").isSecureTextEntry)
        XCTAssertTrue(try field(page, "accessToken").isSecureTextEntry)
        edit(page, "apiBase", upstream.base + "/api")
        edit(page, "imageBase", upstream.base + "/images")
        edit(page, "apiKey", "synthetic-key")
        edit(page, "accessToken", "synthetic-token")
        try await save(page, model: settings.tmdb)
        XCTAssertEqual(settings.tmdb.saved?.apiBase, upstream.base + "/api")
        XCTAssertTrue(settings.tmdb.saved?.apiKeyConfigured == true)
        XCTAssertTrue(settings.tmdb.saved?.accessTokenConfigured == true)
        XCTAssertEqual(try field(page, "apiKey").text, "")
        XCTAssertEqual(try field(page, "accessToken").text, "")
        XCTAssertTrue(upstream.requests.isEmpty, "Saving never starts a search or a configuration request")
        attach(window, name: "tmdb-settings-saved-no-password-echo")

        let query = "原词 2 & 中文"
        let session = try search.session(for: query)
        await waitFor { !session.snapshot.loading }
        XCTAssertEqual(session.snapshot.metadataState, .completed)
        XCTAssertEqual(session.snapshot.metadataSearch?.catField("unknown"), .string("preserved"))
        let tv = try XCTUnwrap(session.snapshot.cards.first { $0.tmdbType == "tv" && $0.tmdbID == 7 })
        let movie = try XCTUnwrap(session.snapshot.cards.first { $0.tmdbType == "movie" && $0.tmdbID == 42 })
        let context = session.context(for: tv)
        XCTAssertEqual(context.detail.summary, "已得剧集详情")
        XCTAssertEqual(session.context(for: movie).detail.summary, "已得电影详情")
        XCTAssertEqual(context.metadataDetail?.catField("extra"), .object(["kept": .bool(true)]))
        XCTAssertEqual(context.metadataDetail?.catField("poster_path"), .string("/v1/tmdb/images/original/tv.png"))
        XCTAssertEqual(context.snapshot.metadataSearch?.catField("results")?.catArray?.first?.catField("poster_path"),
            .string("/v1/tmdb/images/w500/tv.png"), "The original search response retains its distinct list-sized image identity")
        let poster = try XCTUnwrap(context.detail.item.posterURL)
        XCTAssertEqual(poster.absoluteString, "/v1/tmdb/images/original/tv.png")
        XCTAssertNil(poster.scheme); XCTAssertNil(poster.baseURL)
        let cell = FilmPosterCell(frame: CGRect(x: 0, y: 0, width: 110, height: 224))
        cell.configure(item: context.detail.item, images: assembly.filmImages, theme: PageTheme(), label: tv.label)
        let artwork = try XCTUnwrap(cell.contentView.subviews.compactMap { $0 as? ArtworkView }.first)
        await waitFor { artwork.image != nil }
        XCTAssertNotNil(assembly.filmImages.cached(poster))
        XCTAssertTrue(try search.session(for: query, reusing: session) === session)
        session.loadMore(); await session.drain()
        let apiReads = upstream.requests.filter { $0.path.contains("/api/") }
        XCTAssertEqual(apiReads.count, 3)
        let searchRequest = try XCTUnwrap(apiReads.first { $0.path.hasSuffix("/search/multi") })
        XCTAssertEqual(searchRequest.method, "GET")
        XCTAssertEqual(searchRequest.query, ["query": query, "page": "1", "language": "zh-CN"])
        XCTAssertEqual(Set(apiReads.map(\.path)), ["/api/3/search/multi", "/api/3/tv/7", "/api/3/movie/42"])
        XCTAssertTrue(apiReads.allSatisfy { $0.headers["authorization"] == "Bearer synthetic-token" })
        XCTAssertTrue(apiReads.filter { !$0.path.hasSuffix("/multi") }.allSatisfy {
            $0.query == ["language": "zh-CN", "append_to_response": "credits,similar,images"]
        })
        assertNoRuntimeAuthentication(upstream.requests)
        let imageReads = upstream.requests.filter { $0.path == "/images/original/tv.png" }
        XCTAssertEqual(imageReads.count, 1)
        XCTAssertNil(imageReads.first?.headers["authorization"])
        XCTAssertEqual(imageReads.first?.query, [:])
        let punctuationQuery = "空 格+与&中文"
        _ = try await resources.filmMetadata.search(query: punctuationQuery)
        XCTAssertEqual(upstream.requests.last?.query["query"], punctuationQuery,
            "The real query preserves spaces, literal plus signs, ampersands and Unicode")

        // Blank secure fields preserve both credentials; changing the proxy
        // keeps the relative cache identity and does not automatically search.
        let requestCount = upstream.requests.count
        edit(page, "imageBase", upstream.base + "/images/changed")
        edit(page, "apiKey", " ")
        edit(page, "accessToken", " ")
        try await save(page, model: settings.tmdb)
        XCTAssertTrue(settings.tmdb.saved?.apiKeyConfigured == true)
        XCTAssertTrue(settings.tmdb.saved?.accessTokenConfigured == true)
        XCTAssertEqual(upstream.requests.count, requestCount)
        let warm = try await assembly.filmImages.read(poster)
        XCTAssertTrue(warm.0 === artwork.image)
        XCTAssertNil(warm.1)
        XCTAssertEqual(upstream.requests.count, requestCount, "Changing a proxy does not change a cached image's identity")
        XCTAssertEqual(context.detail.summary, "已得剧集详情")
        let next = try search.session(for: query, reusing: session)
        XCTAssertFalse(next === session)
        await waitFor { !next.snapshot.loading }
        XCTAssertEqual(upstream.requests.filter { $0.path.hasSuffix("/3/tv/7") }.count, 2, "A saved configuration revokes detail reuse")

        edit(page, "apiKey", "synthetic-key-replaced")
        edit(page, "accessToken", "synthetic-token-replaced")
        try await save(page, model: settings.tmdb)
        _ = try await resources.filmMetadata.detail(type: "tv", id: 7)
        XCTAssertEqual(upstream.requests.last?.headers["authorization"], "Bearer synthetic-token-replaced")
        try select(page, "film.tmdb.clearToken")
        try await save(page, model: settings.tmdb)
        _ = try await resources.filmMetadata.detail(type: "movie", id: 42)
        XCTAssertNil(upstream.requests.last?.headers["authorization"])
        XCTAssertEqual(upstream.requests.last?.query["api_key"], "synthetic-key-replaced", "Unedited key was preserved")
        try select(page, "film.tmdb.clearKey")
        try await save(page, model: settings.tmdb)
        XCTAssertFalse(settings.tmdb.saved?.apiKeyConfigured == true)
        XCTAssertFalse(settings.tmdb.saved?.accessTokenConfigured == true)
        let active = try search.session(for: query)
        await waitFor { !active.snapshot.loading }
        XCTAssertEqual(active.snapshot.metadataState, .completed, "A proxy can authenticate without native credentials")
        XCTAssertNil(upstream.requests.last?.headers["authorization"])
        XCTAssertNil(upstream.requests.last?.query["api_key"])

        let saved = settings.tmdb.saved
        edit(page, "apiBase", "not a valid endpoint")
        edit(page, "apiKey", "synthetic-unsaved-key")
        try select(page, "film.tmdb.save")
        await waitFor { !settings.tmdb.busy && (page.presentedViewController as? UIAlertController)?.title == "错误" }
        XCTAssertEqual(settings.tmdb.saved, saved)
        XCTAssertEqual(settings.tmdb.apiBase, "not a valid endpoint")
        XCTAssertEqual(try field(page, "apiKey").text, "synthetic-unsaved-key")
        let stillSaved = try await resources.filmMetadata.settings()
        XCTAssertEqual(stillSaved, saved)
        XCTAssertTrue(try search.session(for: query, reusing: active) === active, "A failed save must not revoke the valid session")
        let alert = try XCTUnwrap(page.presentedViewController as? UIAlertController)
        XCTAssertEqual(alert.actions.map(\.title), ["取消", "重试"])
        attach(window, name: "tmdb-settings-failed-save-retains-input")
        page.selectAlertChoice("取消")
        await waitFor { page.canPresentPageAlert }
        edit(page, "apiBase", upstream.base + "/api")
        try await save(page, model: settings.tmdb)
        XCTAssertTrue(settings.tmdb.saved?.apiKeyConfigured == true, "The retained input remains available for a corrected save")
        XCTAssertEqual(try field(page, "apiKey").text, "")
        let corrected = try search.session(for: query, reusing: active)
        XCTAssertFalse(corrected === active)
        await waitFor { !corrected.snapshot.loading }
        edit(page, "apiBase", upstream.base + "/failure")
        try await save(page, model: settings.tmdb)
        do { _ = try await resources.filmMetadata.search(query: "上游失败"); XCTFail("An upstream failure is not a successful empty page") }
        catch { XCTAssertEqual(error as? RuntimeHTTPError, .remote("UPSTREAM_HTTP")) }
        do { _ = try await resources.filmMetadata.detail(type: "tv", id: 7); XCTFail("An upstream failure is not an empty detail") }
        catch { XCTAssertEqual(error as? RuntimeHTTPError, .remote("UPSTREAM_HTTP")) }
    }

    func testSuccessfulSettingsSaveCancelsActualOldDetailAndKeepsItsNavigationSnapshot() async throws {
        let resources = try await hostResources(), assembly = try assembly()
        let original = try await resources.filmMetadata.settings()
        try requireEmptyCredentials(original)
        let entered = expectation(description: "Old physical upstream detail entered")
        let disconnected = expectation(description: "Old physical upstream detail cancelled")
        let upstream = try TMDBTestUpstream { request in
            if request.path.hasSuffix("/3/search/multi") { return .json(tmdbHostSearch) }
            if request.path == "/old/3/tv/7" {
                entered.fulfill()
                return .hold(onDisconnect: { disconnected.fulfill() })
            }
            return .json(request.path.hasSuffix("/tv/7") ? tmdbHostTV : tmdbHostMovie)
        }
        try await upstream.start()
        let search = try XCTUnwrap(assembly.filmSearch)
        addTeardownBlock {
            upstream.stop() // Always release pending physical I/O, even after an assertion throws.
            _ = try await resources.filmMetadata.updateSettings([
                "apiBase": .string(original.apiBase), "imageBase": .string(original.imageBase),
                "apiKey": .string(""), "accessToken": .string("")
            ])
            await MainActor.run { search.metadataSettingsChanged() }
        }
        let model = FilmTMDBSettingsModel(service: resources.filmMetadata) { search.metadataSettingsChanged() }
        try await model.load()
        model.apiBase = upstream.base + "/old"; model.imageBase = upstream.base + "/images"
        try await model.save()
        let old = try search.session(for: "同词")
        await fulfillment(of: [entered], timeout: 10)
        await waitFor { old.snapshot.cards.contains { $0.tmdbType == "tv" } }
        let card = try XCTUnwrap(old.snapshot.cards.first { $0.tmdbType == "tv" })
        let context = old.context(for: card)
        let detail = FilmDetailViewController(searchContext: context, library: assembly.filmLibrary,
            images: assembly.filmImages, themeProvider: assembly.appearance)
        XCTAssertEqual(detail.detail.summary, "搜索已得简介")
        let page = FilmTMDBSettingsViewController(model: model, themeProvider: assembly.appearance)
        let (window, previous) = try await mount(page)
        defer { unmount(window, previous: previous) }
        await waitFor { page.canPresentPageAlert }
        edit(page, "apiBase", upstream.base + "/new")
        try await save(page, model: model)
        await fulfillment(of: [disconnected], timeout: 10)
        await old.drain()
        XCTAssertEqual(upstream.requests.filter { $0.path.hasPrefix("/new/") }.count, 0, "A save cannot auto-search")
        let current = try search.session(for: "同词", reusing: context.session)
        XCTAssertFalse(current === old)
        await waitFor { !current.snapshot.loading }
        let now = current.context(for: try XCTUnwrap(current.snapshot.cards.first { $0.tmdbType == "tv" }))
        XCTAssertEqual(now.detail.summary, "已得剧集详情")
        XCTAssertEqual(context.snapshot.query, "同词")
        XCTAssertEqual(detail.detail.summary, "搜索已得简介", "The existing detail is not rebound to the new session")
        XCTAssertNil(old.snapshot.metadataDetails["tv:7"])
        XCTAssertEqual(upstream.requests.filter { $0.path == "/new/3/tv/7" }.count, 1)
    }

    func testCancelledDetailWaiterLeavesActualProducerToPopulateTheSharedCache() async throws {
        let resources = try await hostResources(), assembly = try assembly()
        let original = try await resources.filmMetadata.settings()
        try requireEmptyCredentials(original)
        let entered = expectation(description: "The actual Node producer reached the metadata upstream")
        let upstream = try TMDBTestUpstream { request in
            XCTAssertEqual(request.path, "/api/3/tv/7")
            XCTAssertEqual(request.query, ["language": "zh-CN", "append_to_response": "credits,similar,images"])
            entered.fulfill()
            return .hold(onDisconnect: {})
        }
        try await upstream.start()
        let search = try XCTUnwrap(assembly.filmSearch)
        addTeardownBlock {
            upstream.stop()
            _ = try await resources.filmMetadata.updateSettings([
                "apiBase": .string(original.apiBase), "imageBase": .string(original.imageBase),
                "apiKey": .string(""), "accessToken": .string("")
            ])
            await MainActor.run { search.metadataSettingsChanged() }
        }
        _ = try await resources.filmMetadata.updateSettings([
            "apiBase": .string(upstream.base + "/api"), "imageBase": .string(upstream.base + "/images")
        ])
        search.metadataSettingsChanged()
        let connection = try await resources.scriptSession.connection()
        let read = Task { try await resources.filmMetadata.detailRead(type: "tv", id: 7) }
        addTeardownBlock { read.cancel(); upstream.stop(); _ = await read.result }
        await fulfillment(of: [entered], timeout: 10)
        read.cancel()
        do { _ = try await read.value; XCTFail("A cancelled observer cannot publish metadata") }
        catch { XCTAssertEqual(error as? RuntimeHTTPError, .cancelled) }

        XCTAssertEqual(upstream.replyToHeldJSON(tmdbHostTV), 1,
            "Cancelling the borrower must leave the admitted upstream producer alive")
        let next = try await resources.filmMetadata.detailRead(type: "tv", id: 7)
        XCTAssertTrue(next.producer === connection)
        XCTAssertEqual(next.value.catField("overview"), .string("已得剧集详情"))
        XCTAssertEqual(next.value.catField("poster_path"), .string("/v1/tmdb/images/original/tv.png"))
        let seed = try XCTUnwrap(next.cacheEntry)
        XCTAssertEqual(seed.catField("kind"), .string("metadata"))
        XCTAssertEqual(seed.catField("completeness"), .string("complete"))
        XCTAssertEqual(seed.catField("imageSize"), .string("original"))
        XCTAssertEqual(seed.catField("query"), .object([
            "language": .string("zh-CN"), "append_to_response": .string("credits,similar,images")
        ]))
        XCTAssertEqual(seed.catField("value"), next.value)
        XCTAssertNotNil(seed.catField("fetchedAt"))
        XCTAssertNotNil(seed.catField("settings")?.catField("revision"))
        let cached = try await resources.filmMetadata.detailRead(type: "tv", id: 7)
        XCTAssertEqual(cached.value, next.value)
        XCTAssertEqual(cached.cacheEntry, seed, "Reusing the owner cache must not reset its timestamps or revision")
        XCTAssertEqual(upstream.requests.count, 1, "The completed producer serves both later reads without a new upstream request")
        assertNoRuntimeAuthentication(upstream.requests)
    }

    func testActualArtworkRejectsModuleFailureTruncationAndCancellationWithoutCachingThenRetries() async throws {
        let resources = try await hostResources(), assembly = try assembly()
        let original = try await resources.filmMetadata.settings()
        try requireEmptyCredentials(original)
        let bytes = png()
        XCTAssertEqual(bytes.suffix(12), Data([0, 0, 0, 0, 0x49, 0x45, 0x4e, 0x44, 0xae, 0x42, 0x60, 0x82]),
            "The incomplete-container fixture removes only the final IEND chunk of a complete PNG")
        let started = expectation(description: "Actual image upstream is physically pending")
        let cancelled = expectation(description: "Actual image upstream disconnected after last consumer cancellation")
        let sequence = TMDBHostImageSequence(bytes: bytes, started: started, cancelled: cancelled)
        let upstream = try TMDBTestUpstream { sequence.response($0) }
        try await upstream.start()
        addTeardownBlock {
            upstream.stop()
            _ = try await resources.filmMetadata.updateSettings([
                "apiBase": .string(original.apiBase), "imageBase": .string(original.imageBase),
                "apiKey": .string(""), "accessToken": .string("")
            ])
            await MainActor.run { assembly.filmSearch?.metadataSettingsChanged() }
        }
        _ = try await resources.filmMetadata.updateSettings([
            "apiBase": .string(upstream.base + "/api"), "imageBase": .string(upstream.base + "/images"),
            "apiKey": .string("synthetic-image-api-key"), "accessToken": .string("synthetic-image-api-token")
        ])
        assembly.filmSearch?.metadataSettingsChanged()
        // Unique relative file names avoid depending on other tests' warm cache.
        let prefix = UUID().uuidString.lowercased()
        for kind in ["failure", "truncated", "missing-iend", "bad-decode"] {
            let reference = try XCTUnwrap(resources.filmMetadata.imageURL(path: "/v1/tmdb/images/w500/\(prefix)-\(kind).png"))
            let view = ArtworkView(size: 0, images: assembly.filmImages)
            let failed = expectation(description: kind + " failed in actual ArtworkView")
            view.onFailure = { XCTAssertEqual($0, reference); failed.fulfill() }
            view.load(reference)
            await fulfillment(of: [failed], timeout: 10)
            XCTAssertNil(view.image); XCTAssertNil(assembly.filmImages.cached(reference))
            view.load(nil)
            let loaded = expectation(description: kind + " fresh read succeeds")
            view.onImage = { _, _ in loaded.fulfill() }
            view.load(reference)
            await fulfillment(of: [loaded], timeout: 10)
            XCTAssertNotNil(view.image)
            XCTAssertNotNil(assembly.filmImages.cached(reference))
        }
        let held = try XCTUnwrap(resources.filmMetadata.imageURL(path: "/v1/tmdb/images/w500/\(prefix)-held.png"))
        let read = Task { try await assembly.filmImages.read(held) }
        addTeardownBlock { read.cancel(); upstream.stop(); _ = await read.result }
        await fulfillment(of: [started], timeout: 10)
        read.cancel()
        do { _ = try await read.value; XCTFail("Cancelled physical image cannot succeed") }
        catch {}
        await fulfillment(of: [cancelled], timeout: 10)
        XCTAssertNil(assembly.filmImages.cached(held))
        _ = try await assembly.filmImages.read(held)
        XCTAssertNotNil(assembly.filmImages.cached(held))
        assertNoRuntimeAuthentication(upstream.requests)
        XCTAssertTrue(upstream.requests.allSatisfy { $0.headers["authorization"] == nil && $0.query["api_key"] == nil })
    }

    private func assembly() throws -> MeowfilmAssembly {
        try XCTUnwrap((UIApplication.shared.delegate as? MeowfilmAppDelegate)?.readyAssembly())
    }
    private func requireEmptyCredentials(_ settings: FilmTMDBSettings) throws {
        guard !settings.apiKeyConfigured && !settings.accessTokenConfigured else {
            throw TMDBHostFailure.dedicatedSimulatorRequired
        }
    }
    private static func restoreSettings(_ model: FilmTMDBSettingsModel, service: FilmTMDBService,
                                        original: FilmTMDBSettings, search: FilmSearchService) async throws {
        if model.saved != nil {
            model.apiBase = original.apiBase; model.imageBase = original.imageBase
            model.clearKey(); model.clearToken()
            try await model.save()
        } else {
            _ = try await service.updateSettings([
                "apiBase": .string(original.apiBase), "imageBase": .string(original.imageBase),
                "apiKey": .string(""), "accessToken": .string("")
            ])
            search.metadataSettingsChanged()
        }
    }
    private func assertNoRuntimeAuthentication(_ requests: [TMDBTestUpstream.Request]) {
        XCTAssertTrue(requests.allSatisfy { $0.headers["x-meow-session"] == nil && $0.headers["x-meow-request-id"] == nil })
    }
    private func field(_ page: FilmTMDBSettingsViewController, _ suffix: String) throws -> UITextField {
        try XCTUnwrap(page.sections.flatMap(\.rows).compactMap(\.content).compactMap { $0 as? UITextField }
            .first { $0.accessibilityIdentifier == "film.tmdb." + suffix })
    }
    private func edit(_ page: FilmTMDBSettingsViewController, _ suffix: String, _ text: String) {
        do { let field = try field(page, suffix); field.text = text; field.sendActions(for: .editingChanged) }
        catch { XCTFail("Missing TMDB field \(suffix)") }
    }
    private func select(_ page: FilmSettingsListController, _ id: String) throws {
        for (section, value) in page.sections.enumerated() {
            if let row = value.rows.firstIndex(where: { $0.id == id }) {
                XCTAssertTrue(value.rows[row].enabled)
                page.tableView(page.table, didSelectRowAt: IndexPath(row: row, section: section))
                return
            }
        }
        XCTFail("Missing TMDB action \(id)")
        throw TMDBHostFailure.missingAction
    }
    private func save(_ page: FilmTMDBSettingsViewController, model: FilmTMDBSettingsModel) async throws {
        try select(page, "film.tmdb.save")
        await waitFor { !model.busy && (page.presentedViewController as? UIAlertController)?.title == "已保存" }
        let alert = try XCTUnwrap(page.presentedViewController as? UIAlertController)
        let dismissed = expectation(description: "Settings receipt dismissed")
        alert.dismiss(animated: false) { dismissed.fulfill() }
        await fulfillment(of: [dismissed], timeout: 5)
        await waitFor { page.canPresentPageAlert }
    }
    private func mount(_ page: UIViewController) async throws -> (UIWindow, UIWindow?) {
        await waitFor { UIApplication.shared.connectedScenes.contains { $0.activationState == .foregroundActive } }
        let scene = try XCTUnwrap(UIApplication.shared.connectedScenes.compactMap { $0 as? UIWindowScene }
            .first { $0.activationState == .foregroundActive })
        let previous = scene.windows.first { $0.isKeyWindow }
        let window = UIWindow(windowScene: scene)
        window.frame = scene.coordinateSpace.bounds; window.windowLevel = .normal + 1
        window.rootViewController = page; window.makeKeyAndVisible()
        return (window, previous)
    }
    private func unmount(_ window: UIWindow, previous: UIWindow?) {
        if let page = window.rootViewController { PageViewController.invalidatePages(in: page) }
        window.isHidden = true; window.rootViewController = nil; previous?.makeKey()
    }
    private func waitFor(_ predicate: @escaping @MainActor () -> Bool) async {
        let ready = expectation(for: NSPredicate { _, _ in MainActor.assumeIsolated { predicate() } }, evaluatedWith: nil)
        await fulfillment(of: [ready], timeout: 10)
        XCTAssertTrue(predicate())
    }
    private func png() -> Data {
        UIGraphicsImageRenderer(size: CGSize(width: 24, height: 36)).pngData {
            UIColor.systemTeal.setFill(); $0.fill(CGRect(x: 0, y: 0, width: 24, height: 36))
        }
    }
    private func attach(_ window: UIWindow, name: String) {
        window.layoutIfNeeded()
        let format = UIGraphicsImageRendererFormat(); format.scale = 1
        let bounds = CGRect(origin: .zero, size: CGSize(width: min(430, window.bounds.width), height: min(780, window.bounds.height)))
        let image = UIGraphicsImageRenderer(bounds: bounds, format: format).image { _ in
            XCTAssertTrue(window.drawHierarchy(in: window.bounds, afterScreenUpdates: true))
        }
        guard let data = image.jpegData(compressionQuality: 0.55) else { return XCTFail("Screenshot encoding") }
        XCTAssertLessThan(data.count, 700 * 1024)
        let attachment = XCTAttachment(data: data, uniformTypeIdentifier: "public.jpeg")
        attachment.name = name; attachment.lifetime = .keepAlways
        add(attachment)
    }
}

/// Terminal App lifecycle: run alone, never alongside the reusable host cases.
@MainActor
final class MeowfilmTMDBShutdownHostTests: XCTestCase {
    func testAppShutdownDrainsPendingActualArtwork() async throws {
        guard ProcessInfo.processInfo.environment["MEOWFILM_TMDB_SHUTDOWN"] == "1" else {
            throw XCTSkip("Run this terminal lifecycle case in a separate App process")
        }
        let resources = try await hostResources()
        let app = try XCTUnwrap(UIApplication.shared.delegate as? MeowfilmAppDelegate)
        let assembly = try XCTUnwrap(app.readyAssembly())
        let original = try await resources.filmMetadata.settings()
        guard !original.apiKeyConfigured && !original.accessTokenConfigured else { throw TMDBHostFailure.dedicatedSimulatorRequired }
        let entered = expectation(description: "Image physically entered upstream")
        let disconnected = expectation(description: "App close disconnects actual image upstream")
        let upstream = try TMDBTestUpstream { _ in
            entered.fulfill()
            return .hold(onDisconnect: { disconnected.fulfill() })
        }
        try await upstream.start()
        let connection = try await resources.scriptSession.connection()
        addTeardownBlock {
            upstream.stop()
            if !connection.isClosed {
                _ = try await resources.filmMetadata.updateSettings([
                    "apiBase": .string(original.apiBase), "imageBase": .string(original.imageBase),
                    "apiKey": .string(""), "accessToken": .string("")
                ])
            }
        }
        _ = try await resources.filmMetadata.updateSettings(["imageBase": .string(upstream.base + "/images")])
        let relative = try XCTUnwrap(resources.filmMetadata.imageURL(path: "/v1/tmdb/images/w500/shutdown-\(UUID().uuidString).png"))
        let read = Task { try await assembly.filmImages.read(relative) }
        addTeardownBlock { read.cancel(); upstream.stop(); _ = await read.result }
        await fulfillment(of: [entered], timeout: 10)
        // Restore the fixture before terminal shutdown. The admitted image still
        // owns its original upstream configuration snapshot.
        _ = try await resources.filmMetadata.updateSettings([
            "apiBase": .string(original.apiBase), "imageBase": .string(original.imageBase),
            "apiKey": .string(""), "accessToken": .string("")
        ])
        let ended = expectation(description: "App owner physically shut down")
        assembly.shutdown()
        let closing = Task { await assembly.waitForShutdown(); ended.fulfill() }
        await fulfillment(of: [disconnected, ended], timeout: 15)
        upstream.stop()
        await closing.value
        do { _ = try await read.value; XCTFail("Owner close cannot cache pending image bytes") }
        catch {}
        XCTAssertNil(assembly.filmImages.cached(relative))
        XCTAssertTrue(connection.isClosed)
        XCTAssertNil(assembly.shutdownError)
    }
}

@MainActor
final class MeowfilmTMDBColdStartPrepareTests: XCTestCase {
    func testSaveSyntheticSettingsForNextAppProcess() async throws {
        guard ProcessInfo.processInfo.environment["MEOWFILM_TMDB_COLD_PHASE"] == "prepare" else {
            throw XCTSkip("Use the documented dedicated-simulator two-process procedure")
        }
        let service = try await hostResources().filmMetadata
        let before = try await service.settings()
        guard !before.apiKeyConfigured && !before.accessTokenConfigured,
              before.apiBase == "https://api.themoviedb.org",
              before.imageBase == "https://image.tmdb.org/t/p" else { throw TMDBHostFailure.dedicatedSimulatorRequired }
        _ = try await service.updateSettings([
            "apiBase": .string("https://tmdb-cold.example.invalid/api"),
            "imageBase": .string("https://tmdb-cold.example.invalid/images"),
            "apiKey": .string("synthetic-cold-key"), "accessToken": .string("synthetic-cold-token")
        ])
    }
}

@MainActor
final class MeowfilmTMDBColdStartRestoreTests: XCTestCase {
    func testNextAppProcessRestoresSettingsWithoutEchoingSecretsOrNativeWriteback() async throws {
        guard ProcessInfo.processInfo.environment["MEOWFILM_TMDB_COLD_PHASE"] == "restore" else {
            throw XCTSkip("Requires the preceding prepare process on this dedicated test simulator")
        }
        let service = try await hostResources().filmMetadata
        let settings = try await service.settings()
        guard settings.apiBase == "https://tmdb-cold.example.invalid/api",
              settings.imageBase == "https://tmdb-cold.example.invalid/images",
              settings.apiKeyConfigured && settings.accessTokenConfigured else { throw TMDBHostFailure.dedicatedSimulatorRequired }
        addTeardownBlock {
            _ = try await service.updateSettings([
                "apiBase": .string("https://api.themoviedb.org"), "imageBase": .string("https://image.tmdb.org/t/p"),
                "apiKey": .string(""), "accessToken": .string("")
            ])
        }
        let client = try await hostResources().scriptSession.connection()
        let value = try await client.call(module: .tmdb, operation: "settings.get")
        XCTAssertNil(value.catField("apiKey")); XCTAssertNil(value.catField("accessToken"))
        XCTAssertEqual(settings.apiBase, "https://tmdb-cold.example.invalid/api")
        XCTAssertEqual(settings.imageBase, "https://tmdb-cold.example.invalid/images")
        XCTAssertTrue(settings.apiKeyConfigured); XCTAssertTrue(settings.accessTokenConfigured)
    }
}

private enum TMDBHostFailure: Error { case dedicatedSimulatorRequired, missingAction }
private let tmdbHostSearch: RuntimeValue = .object([
    "page": .number(1), "total_pages": .number(1), "unknown": .string("preserved"),
    "results": .array([
        .object(["id": .number(7), "media_type": .string("tv"), "name": .string("受控剧集"),
            "poster_path": .string("/tv.png"), "overview": .string("搜索已得简介")]),
        .object(["id": .number(42), "media_type": .string("movie"), "title": .string("受控电影"),
            "poster_path": .string("/movie.png")])
    ])
])
private let tmdbHostTV: RuntimeValue = .object([
    "id": .number(7), "name": .string("受控剧集"), "poster_path": .string("/tv.png"),
    "overview": .string("已得剧集详情"), "number_of_episodes": .number(24),
    "genres": .array([]), "seasons": .array([]), "extra": .object(["kept": .bool(true)])
])
private let tmdbHostMovie: RuntimeValue = .object([
    "id": .number(42), "title": .string("受控电影"), "poster_path": .string("/movie.png"),
    "overview": .string("已得电影详情"), "genres": .array([]), "runtime": .number(90)
])

private final class TMDBHostImageSequence: @unchecked Sendable {
    private let lock = NSLock()
    private var counts: [String: Int] = [:]
    private let bytes: Data, started: XCTestExpectation, cancelled: XCTestExpectation
    init(bytes: Data, started: XCTestExpectation, cancelled: XCTestExpectation) {
        self.bytes = bytes; self.started = started; self.cancelled = cancelled
    }
    func response(_ request: TMDBTestUpstream.Request) -> TMDBTestUpstream.Response {
        lock.lock(); counts[request.path, default: 0] += 1; let first = counts[request.path] == 1; lock.unlock()
        if first {
            if request.path.contains("-failure.") { return .json(.object(["error": .string("Synthetic upstream error")]), status: 503) }
            if request.path.contains("-truncated.") { return .truncated(bytes.prefix(12)) }
            // Unlike a broken HTTP body, this is a complete 200 response with
            // the exact Content-Length of an otherwise valid PNG without IEND.
            if request.path.contains("-missing-iend.") { return .bytes(Data(bytes.dropLast(12)), type: "image/png") }
            if request.path.contains("-bad-decode.") { return .bytes(Data([137, 80, 78, 71, 13, 10, 26, 10, 0]), type: "image/png") }
            if request.path.contains("-held.") {
                started.fulfill()
                return .hold(onDisconnect: { self.cancelled.fulfill() })
            }
        }
        return .bytes(bytes, type: "image/png")
    }
}

/// A real, local HTTP upstream. It receives no runtime credentials and never
/// reads application files. Held responses are released by stop on any failure.
final class TMDBTestUpstream: @unchecked Sendable {
    struct Request: Sendable {
        let method: String, path: String
        let query: [String: String], headers: [String: String]
    }
    enum Response {
        case bytes(Data, type: String, status: Int = 200)
        case truncated(Data)
        case hold(onDisconnect: @Sendable () -> Void)
        static func json(_ value: RuntimeValue, status: Int = 200) -> Self {
            .bytes(try! RuntimeJSON.encode(value), type: "application/json", status: status)
        }
    }
    private let queue = DispatchQueue(label: "meowfilm.tmdb-test-upstream")
    private let listener: NWListener
    private let handler: @Sendable (Request) -> Response
    private var connections: [ObjectIdentifier: NWConnection] = [:]
    private var held: [ObjectIdentifier: NWConnection] = [:]
    private var recorded: [Request] = []
    var base: String { "http://127.0.0.1:\(listener.port!.rawValue)" }
    var requests: [Request] { queue.sync { recorded } }
    init(handler: @escaping @Sendable (Request) -> Response) throws {
        self.handler = handler
        let parameters = NWParameters.tcp
        parameters.requiredLocalEndpoint = .hostPort(host: "127.0.0.1", port: .any)
        listener = try NWListener(using: parameters)
    }
    func start() async throws {
        try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<Void, Error>) in
            listener.stateUpdateHandler = { [weak self] state in
                switch state {
                case .ready: self?.listener.stateUpdateHandler = nil; continuation.resume()
                case .failed(let error): self?.listener.stateUpdateHandler = nil; continuation.resume(throwing: error)
                default: break
                }
            }
            listener.newConnectionHandler = { [weak self] in self?.accept($0) }
            listener.start(queue: queue)
        }
    }
    func stop() {
        queue.sync {
            listener.cancel()
            for connection in connections.values { connection.cancel() }
            connections.removeAll()
            held.removeAll()
        }
    }
    func replyToHeldJSON(_ value: RuntimeValue, status: Int = 200) -> Int {
        queue.sync {
            let waiting = Array(held.values)
            held.removeAll()
            let data = try! RuntimeJSON.encode(value)
            for connection in waiting {
                send(connection, data: data, type: "application/json", status: status, length: data.count)
            }
            return waiting.count
        }
    }
    private func accept(_ connection: NWConnection) {
        connections[ObjectIdentifier(connection)] = connection
        connection.start(queue: queue)
        receive(connection, data: Data())
    }
    private func receive(_ connection: NWConnection, data: Data) {
        connection.receive(minimumIncompleteLength: 1, maximumLength: 16 * 1024) { [weak self] chunk, _, ended, error in
            guard let self else { return }
            var bytes = data; if let chunk { bytes.append(chunk) }
            if let separator = bytes.range(of: Data("\r\n\r\n".utf8)),
               let head = String(data: bytes[..<separator.lowerBound], encoding: .utf8),
               let request = self.parse(head) {
                self.recorded.append(request)
                self.respond(connection, self.handler(request))
            } else if ended || error != nil {
                self.remove(connection)
            } else { self.receive(connection, data: bytes) }
        }
    }
    private func parse(_ head: String) -> Request? {
        let lines = head.components(separatedBy: "\r\n")
        guard let line = lines.first else { return nil }
        let parts = line.split(separator: " ")
        guard parts.count == 3, var components = URLComponents(string: "http://127.0.0.1" + String(parts[1])) else { return nil }
        var headers: [String: String] = [:]
        for line in lines.dropFirst() {
            guard let colon = line.firstIndex(of: ":") else { return nil }
            headers[String(line[..<colon]).lowercased()] = line[line.index(after: colon)...].trimmingCharacters(in: .whitespaces)
        }
        var query: [String: String] = [:]
        // URLSearchParams serializes form queries: '+' is a space, while a
        // literal plus is '%2B'. Translate before percent-decoding queryItems.
        components.percentEncodedQuery = components.percentEncodedQuery?.replacingOccurrences(of: "+", with: "%20")
        for item in components.queryItems ?? [] { query[item.name] = item.value ?? "" }
        return Request(method: String(parts[0]), path: components.path, query: query, headers: headers)
    }
    private func respond(_ connection: NWConnection, _ response: Response) {
        switch response {
        case .hold(let onDisconnect):
            held[ObjectIdentifier(connection)] = connection
            waitForDisconnect(connection, callback: onDisconnect)
        case .bytes(let data, let type, let status):
            send(connection, data: data, type: type, status: status, length: data.count)
        case .truncated(let data):
            send(connection, data: data, type: "image/png", status: 200, length: data.count + 200)
        }
    }
    private func send(_ connection: NWConnection, data: Data, type: String, status: Int, length: Int) {
        var response = Data("HTTP/1.1 \(status) Fixture\r\nContent-Type: \(type)\r\nContent-Length: \(length)\r\nConnection: close\r\n\r\n".utf8)
        response.append(data)
        connection.send(content: response, contentContext: .finalMessage, isComplete: true,
            completion: .contentProcessed { [weak self] _ in self?.remove(connection) })
    }
    private func waitForDisconnect(_ connection: NWConnection, callback: @escaping @Sendable () -> Void) {
        connection.receive(minimumIncompleteLength: 1, maximumLength: 1024) { [weak self] _, _, ended, error in
            guard let self else { return }
            if ended || error != nil { self.remove(connection); callback() }
            else { self.waitForDisconnect(connection, callback: callback) }
        }
    }
    private func remove(_ connection: NWConnection) {
        connections[ObjectIdentifier(connection)] = nil
        held[ObjectIdentifier(connection)] = nil
        connection.cancel()
    }
}
