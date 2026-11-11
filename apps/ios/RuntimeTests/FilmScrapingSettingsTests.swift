import XCTest
import UIKit
import Combine
@testable import MeowRuntime
import MeowUI
@testable import MeowFilmFeature

@MainActor
final class FilmScrapingSettingsTests: XCTestCase {
    func testDoubanDefaultsAndEveryPresetMapToExplicitRuntimeBasesAndTemplates() async throws {
        let wire = ScrapingSettingsWire(), client = try client(wire)
        addTeardownBlock { await client.close() }
        var saved = 0
        let model = FilmDoubanSettingsModel(service: .init(connection: { client })) { saved += 1 }
        try await model.load()
        XCTAssertEqual(model.dataRoute, .direct); XCTAssertEqual(model.imageRoute, .direct)
        XCTAssertEqual(wire.writes.count, 0, "Loading settings must not write defaults")
        let expectedData: [(FilmDoubanDataRoute, String, String)] = [
            (.direct, "https://m.douban.com", ""),
            (.cors, "https://m.douban.com", "https://ciao-cors.is-an.org/{encodedUrl}"),
            (.corsAnywhere, "https://m.douban.com", "https://cors-anywhere.com/{url}"),
        ]
        for (route, base, proxy) in expectedData {
            model.dataRoute = route
            try await model.save()
            let patch = try XCTUnwrap(wire.writes.last)
            XCTAssertEqual(patch["apiBase"], .string(base))
            XCTAssertEqual(patch["searchBase"], .string("https://search.douban.com"))
            XCTAssertEqual(patch["apiProxy"], .string(proxy))
            if route != .direct {
                XCTAssertFalse(patch["apiProxy"]?.catText?.isEmpty ?? true,
                    "A data proxy preset must wrap both the API and HTML search bases")
            }
            XCTAssertEqual(patch["imageBase"], .string("https://img3.doubanio.com"))
            XCTAssertEqual(patch["imageProxy"], .string(""))
            XCTAssertNil(patch["cookie"])
            XCTAssertEqual(model.dataRoute, route)
        }
        let expectedImages: [(FilmDoubanImageRoute, String)] = [
            (.cdnTX, "https://img.doubanio.cmliussss.net"),
            (.cdnAli, "https://img.doubanio.cmliussss.com"),
            (.direct, "https://img3.doubanio.com"),
        ]
        for (route, base) in expectedImages {
            model.imageRoute = route
            try await model.save()
            XCTAssertEqual(wire.writes.last?["imageBase"], .string(base))
            XCTAssertEqual(wire.writes.last?["imageProxy"], .string(""))
            XCTAssertEqual(wire.writes.last?["apiProxy"], .string("https://cors-anywhere.com/{url}"))
            XCTAssertEqual(model.imageRoute, route)
        }
        XCTAssertEqual(saved, 6)
    }

    func testAPICDNAddressesLoadAsCustomAndPreserveTheIndependentSearchConfiguration() async throws {
        for base in ["https://m.douban.cmliussss.net", "https://m.douban.cmliussss.com"] {
            let initial: RuntimeObject = [
                "apiBase": .string(base), "searchBase": .string("https://search.douban.com"),
                "apiProxy": .string(""), "imageBase": .string("https://img3.doubanio.com"),
                "imageProxy": .string(""), "cookieConfigured": .bool(false),
            ]
            let wire = ScrapingSettingsWire(douban: initial), client = try client(wire)
            addTeardownBlock { await client.close() }
            let model = FilmDoubanSettingsModel(service: .init(connection: { client }), didSave: {})
            try await model.load()
            XCTAssertEqual(model.dataRoute, .custom, "An API-only CDN cannot represent a data-and-search proxy preset")
            XCTAssertEqual(model.imageRoute, .direct)
            XCTAssertEqual(wire.writes.count, 0)
            try await model.save()
            XCTAssertEqual(wire.writes.last, [
                "apiBase": .string(base), "searchBase": .string("https://search.douban.com"),
                "apiProxy": .string(""), "imageBase": .string("https://img3.doubanio.com"), "imageProxy": .string(""),
            ])
            await client.close()
        }
    }

    func testUnknownCustomConfigurationAndIndependentRoutesSurviveLoadAndSave() async throws {
        let initial = doubanCustomSettings
        let wire = ScrapingSettingsWire(douban: initial), client = try client(wire)
        addTeardownBlock { await client.close() }
        let model = FilmDoubanSettingsModel(service: .init(connection: { client }), didSave: {})
        try await model.load()
        XCTAssertEqual(model.dataRoute, .custom); XCTAssertEqual(model.imageRoute, .custom)
        XCTAssertEqual(model.cookie, ""); XCTAssertTrue(model.saved?.cookieConfigured == true)
        try await model.save()
        XCTAssertEqual(wire.writes.last, [
            "apiBase": initial["apiBase"]!, "searchBase": initial["searchBase"]!, "apiProxy": initial["apiProxy"]!,
            "imageBase": initial["imageBase"]!, "imageProxy": initial["imageProxy"]!,
        ])
        model.dataRoute = .direct
        try await model.save()
        XCTAssertEqual(wire.writes.last?["imageBase"], initial["imageBase"])
        XCTAssertEqual(wire.writes.last?["imageProxy"], initial["imageProxy"])
        model.imageRoute = .cdnTX
        try await model.save()
        XCTAssertEqual(wire.writes.last?["apiBase"], .string("https://m.douban.com"))
        XCTAssertEqual(wire.writes.last?["apiProxy"], .string(""))
    }

    func testDoubanCredentialsPreserveReplaceClearAndFailuresKeepDraftWithoutNotifying() async throws {
        let wire = ScrapingSettingsWire(douban: doubanCustomSettings), client = try client(wire)
        addTeardownBlock { await client.close() }
        var notifications = 0
        let model = FilmDoubanSettingsModel(service: .init(connection: { client })) { notifications += 1 }
        try await model.load()
        model.cookie = "   "
        try await model.save()
        XCTAssertNil(wire.writes.last?["cookie"])
        XCTAssertTrue(model.saved?.cookieConfigured == true); XCTAssertEqual(model.cookie, "")
        model.clearCredential()
        wire.fail = true
        do { try await model.save(); XCTFail("Storage failure must remain a failure") }
        catch { XCTAssertEqual(error as? RuntimeHTTPError, .remote("STORAGE_ERROR")) }
        XCTAssertTrue(model.clearCookie); XCTAssertTrue(model.saved?.cookieConfigured == true)
        XCTAssertFalse(model.busy); XCTAssertEqual(notifications, 1)
        model.cookie = "synthetic_cookie=replacement"
        model.apiProxy = "https://new.example.invalid/?target={encodedUrl}"
        let saved = model.saved
        do { try await model.save(); XCTFail("Failed replacement must preserve input") } catch {}
        XCTAssertEqual(model.saved, saved)
        XCTAssertEqual(model.cookie, "synthetic_cookie=replacement"); XCTAssertFalse(model.clearCookie)
        XCTAssertEqual(model.apiProxy, "https://new.example.invalid/?target={encodedUrl}")
        XCTAssertEqual(notifications, 1)
        wire.fail = false
        try await model.save()
        XCTAssertEqual(wire.writes.last?["cookie"], .string("synthetic_cookie=replacement"))
        XCTAssertEqual(model.cookie, ""); XCTAssertEqual(notifications, 2)
        model.clearCredential()
        try await model.save()
        XCTAssertEqual(wire.writes.last?["cookie"], .string(""))
        XCTAssertFalse(model.saved?.cookieConfigured == true); XCTAssertFalse(model.clearCookie)
        XCTAssertEqual(notifications, 3)
    }

    func testTMDBCredentialsPreserveClearAndFailedInputWithoutEcho() async throws {
        let wire = ScrapingSettingsWire(), client = try client(wire)
        addTeardownBlock { await client.close() }
        var notifications = 0
        let model = FilmTMDBSettingsModel(service: .init(connection: { client })) { notifications += 1 }
        try await model.load()
        XCTAssertEqual(model.apiKey, ""); XCTAssertEqual(model.accessToken, "")
        model.apiKey = " "; model.accessToken = " "
        try await model.save()
        XCTAssertNil(wire.writes.last?["apiKey"]); XCTAssertNil(wire.writes.last?["accessToken"])
        model.clearKey(); model.clearToken()
        wire.fail = true
        do { try await model.save(); XCTFail("Failure must keep explicit clear intents") } catch {}
        XCTAssertTrue(model.clearAPIKey); XCTAssertTrue(model.clearAccessToken)
        XCTAssertEqual(notifications, 1)
        model.apiKey = "synthetic-new-key"; model.accessToken = "synthetic-new-token"
        model.apiBase = "https://custom.example.invalid/nested/tmdb"
        do { try await model.save(); XCTFail("Failure must retain credential replacement") } catch {}
        XCTAssertEqual(model.apiKey, "synthetic-new-key"); XCTAssertEqual(model.accessToken, "synthetic-new-token")
        XCTAssertEqual(model.apiBase, "https://custom.example.invalid/nested/tmdb")
        wire.fail = false
        try await model.save()
        XCTAssertEqual(model.apiKey, ""); XCTAssertEqual(model.accessToken, "")
        XCTAssertTrue(model.saved?.apiKeyConfigured == true); XCTAssertTrue(model.saved?.accessTokenConfigured == true)
        model.clearKey(); model.clearToken()
        try await model.save()
        XCTAssertEqual(wire.writes.last?["apiKey"], .string("")); XCTAssertEqual(wire.writes.last?["accessToken"], .string(""))
        XCTAssertFalse(model.saved?.apiKeyConfigured == true); XCTAssertFalse(model.saved?.accessTokenConfigured == true)
        XCTAssertEqual(notifications, 3)
    }

    func testScrapingFormStacksFullWidthFieldsAndKeepsProviderDraftsAndCustomExpansionIndependent() async throws {
        let wire = ScrapingSettingsWire(), client = try client(wire)
        addTeardownBlock { await client.close() }
        let tmdb = FilmTMDBSettingsModel(service: .init(connection: { client }), didSave: {})
        let douban = FilmDoubanSettingsModel(service: .init(connection: { client }), didSave: {})
        try await tmdb.load(); try await douban.load()
        let page = FilmScrapingSettingsViewController(tmdb: tmdb, douban: douban, themeProvider: ScrapingSettingsTheme())
        page.loadViewIfNeeded()
        defer { PageViewController.invalidatePages(in: page) }
        XCTAssertEqual(page.pageTitle, "刮削设置")
        let form = try XCTUnwrap(page.children.compactMap { $0 as? FilmScrapingFormController }.first)
        let provider = try XCTUnwrap(form.sections.first?.rows.first?.content as? UISegmentedControl)
        XCTAssertEqual(provider.numberOfSegments, 2)
        XCTAssertEqual(provider.titleForSegment(at: 0), "TMDB"); XCTAssertEqual(provider.titleForSegment(at: 1), "豆瓣")
        let api = try field(form, "film.tmdb.apiBase")
        let key = try field(form, "film.tmdb.apiKey")
        XCTAssertEqual(api.keyboardType, .URL); XCTAssertTrue(key.isSecureTextEntry)
        XCTAssertTrue(api.adjustsFontForContentSizeCategory)
        let stack = try XCTUnwrap(api.superview as? UIStackView)
        XCTAssertEqual(stack.axis, .vertical); XCTAssertEqual(stack.alignment, .fill)
        XCTAssertEqual((stack.arrangedSubviews.first as? UILabel)?.text, "API 基址")
        XCTAssertTrue(api.constraints.contains { $0.firstAttribute == .height && $0.constant >= 44 })
        api.text = "https://draft.example.invalid/nested"; api.sendActions(for: .editingChanged)
        key.text = "synthetic-unsaved-key"; key.sendActions(for: .editingChanged)
        provider.selectedSegmentIndex = 1; provider.sendActions(for: .valueChanged)
        XCTAssertEqual(try field(form, "film.douban.cookie").text, "")
        XCTAssertTrue(try field(form, "film.douban.cookie").isSecureTextEntry)
        XCTAssertFalse(form.sections.flatMap(\.rows).contains { $0.id == "film.douban.apiBase.row" })
        try select(form, "film.douban.data.custom")
        XCTAssertNotNil(try field(form, "film.douban.apiBase"))
        XCTAssertFalse(form.sections.flatMap(\.rows).contains { $0.id == "film.douban.imageBase.row" })
        try select(form, "film.douban.image.custom")
        XCTAssertNotNil(try field(form, "film.douban.imageBase"))
        let cookie = try field(form, "film.douban.cookie")
        cookie.text = "synthetic_cookie=draft"; cookie.sendActions(for: .editingChanged)
        provider.selectedSegmentIndex = 0; provider.sendActions(for: .valueChanged)
        XCTAssertEqual(try field(form, "film.tmdb.apiBase").text, "https://draft.example.invalid/nested")
        XCTAssertEqual(try field(form, "film.tmdb.apiKey").text, "synthetic-unsaved-key")
        provider.selectedSegmentIndex = 1; provider.sendActions(for: .valueChanged)
        XCTAssertEqual(try field(form, "film.douban.cookie").text, "synthetic_cookie=draft")
        XCTAssertEqual(douban.dataRoute, .custom); XCTAssertEqual(douban.imageRoute, .custom)
        XCTAssertEqual(wire.writes.count, 0)
    }

    private func client(_ wire: ScrapingSettingsWire) throws -> RuntimeHTTPClient {
        ScrapingSettingsProtocol.set { wire.receive($0) }
        let configuration = URLSessionConfiguration.ephemeral
        configuration.protocolClasses = [ScrapingSettingsProtocol.self]
        return try RuntimeHTTPClient(baseURL: URL(string: "http://127.0.0.1:43111")!, configuration: configuration)
    }
    private func field(_ form: FilmScrapingFormController, _ id: String) throws -> UITextField {
        func fields(_ view: UIView) -> [UITextField] {
            if let field = view as? UITextField { return [field] }
            return view.subviews.flatMap(fields)
        }
        return try XCTUnwrap(form.sections.flatMap(\.rows).compactMap(\.content).flatMap(fields).first { $0.accessibilityIdentifier == id })
    }
    private func select(_ form: FilmScrapingFormController, _ id: String) throws {
        let row = try XCTUnwrap(form.sections.flatMap(\.rows).first { $0.id == id })
        XCTAssertTrue(row.enabled)
        try XCTUnwrap(row.action)()
    }
}

@MainActor
private final class ScrapingSettingsTheme: PageThemeProviding {
    var pagePalette: PagePalette { .preset(.lilac) }
    let animatesPageColorway = false
    let pageThemeChanges = Empty<Void, Never>().eraseToAnyPublisher()
}

private let doubanCustomSettings: RuntimeObject = [
    "apiBase": .string("https://custom.example.invalid/pan/douban"),
    "searchBase": .string("https://search.example.invalid/nested"),
    "imageBase": .string("https://images.example.invalid/pan/douban-img"),
    "apiProxy": .string("https://relay.example.invalid/?target={encodedUrl}"),
    "imageProxy": .string("https://pictures.example.invalid/{url}"), "cookieConfigured": .bool(true),
]

private final class ScrapingSettingsWire: @unchecked Sendable {
    private let lock = NSLock()
    private var values: [String: RuntimeObject]
    private var recorded: [RuntimeObject] = []
    private var failing = false
    var writes: [RuntimeObject] { lock.lock(); defer { lock.unlock() }; return recorded }
    var fail: Bool {
        get { lock.lock(); defer { lock.unlock() }; return failing }
        set { lock.lock(); defer { lock.unlock() }; failing = newValue }
    }
    init(douban: RuntimeObject = [
        "apiBase": .string("https://m.douban.com"), "searchBase": .string("https://search.douban.com"),
        "imageBase": .string("https://img3.doubanio.com"), "apiProxy": .string(""), "imageProxy": .string(""),
        "cookieConfigured": .bool(false),
    ]) {
        values = ["douban": douban, "tmdb": [
            "apiBase": .string("https://api.themoviedb.org"), "imageBase": .string("https://image.tmdb.org/t/p"),
            "apiKeyConfigured": .bool(true), "accessTokenConfigured": .bool(true),
        ]]
    }
    func receive(_ transport: ScrapingSettingsProtocol) {
        lock.lock(); defer { lock.unlock() }
        let path = transport.request.url!.path.components(separatedBy: "/")
        XCTAssertEqual(transport.request.httpMethod, "POST")
        XCTAssertEqual(path.count, 4)
        guard path.count == 4, var value = values[path[2]] else { return XCTFail("Unexpected settings route") }
        let contract = path[2] + ".data.v1"
        if path[3] == "settings.update" {
            guard case .object(let patch)? = try? RuntimeJSON.decode(transport.body()) else {
                return XCTFail("Settings updates require an object")
            }
            recorded.append(patch)
            if failing {
                transport.reply(.object(["ok": .bool(false), "contract": .string(contract), "code": .string("STORAGE_ERROR")]))
                return
            }
            for (key, item) in patch {
                if ["cookie", "apiKey", "accessToken"].contains(key) { value[key + "Configured"] = .bool(!(item.catText ?? "").isEmpty) }
                else { value[key] = item }
            }
            values[path[2]] = value
        } else { XCTAssertEqual(path[3], "settings.get") }
        transport.reply(.object(["ok": .bool(true), "contract": .string(contract), "value": .object(value)]))
    }
}

private final class ScrapingSettingsProtocol: URLProtocol, @unchecked Sendable {
    private static let lock = NSLock()
    private static var handler: (@Sendable (ScrapingSettingsProtocol) -> Void)?
    static func set(_ value: @escaping @Sendable (ScrapingSettingsProtocol) -> Void) {
        lock.lock(); handler = value; lock.unlock()
    }
    override class func canInit(with request: URLRequest) -> Bool { true }
    override class func canonicalRequest(for request: URLRequest) -> URLRequest { request }
    override func startLoading() {
        Self.lock.lock(); let callback = Self.handler; Self.lock.unlock()
        callback?(self)
    }
    override func stopLoading() {}
    func reply(_ value: RuntimeValue) {
        let response = HTTPURLResponse(url: request.url!, statusCode: 200, httpVersion: "HTTP/1.1", headerFields: [
            "Content-Type": "application/json", "X-Meow-Request-Id": request.value(forHTTPHeaderField: "X-Meow-Request-Id")!,
        ])!
        client?.urlProtocol(self, didReceive: response, cacheStoragePolicy: .notAllowed)
        client?.urlProtocol(self, didLoad: try! RuntimeJSON.encode(value))
        client?.urlProtocolDidFinishLoading(self)
    }
    func body() -> Data {
        if let body = request.httpBody { return body }
        guard let stream = request.httpBodyStream else { return Data() }
        stream.open(); defer { stream.close() }
        var data = Data(), bytes = [UInt8](repeating: 0, count: 4096)
        while true {
            let count = stream.read(&bytes, maxLength: bytes.count)
            if count <= 0 { return data }
            data.append(contentsOf: bytes.prefix(count))
        }
    }
}
