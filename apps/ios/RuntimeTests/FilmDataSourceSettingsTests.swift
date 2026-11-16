import XCTest
import UIKit
import Combine
@testable import MeowRuntime
import MeowStorage
import MeowUI
@testable import MeowFilmFeature

@MainActor
final class FilmDataSourceSettingsTests: XCTestCase {
    func testDoubanDefaultsAndEveryPresetUseServerProxyAddresses() async throws {
        let storage = try DataSourceSettingsStorage(self)
        var saved = 0
        let model = FilmDoubanSettingsModel(service: FilmDoubanSettingsService(
            settingsDocument: storage.documents[.douban])) { saved += 1 }
        try await model.load()
        XCTAssertEqual(model.dataRoute, .direct); XCTAssertEqual(model.imageRoute, .direct)
        XCTAssertNil(try storage.documents[.douban].read(), "Loading settings must not write defaults")
        let expectedData: [(FilmDoubanDataRoute, String, String)] = [
            (.direct, "https://m.douban.com", ""),
            (.cors, "https://m.douban.com", "https://ciao-cors.is-an.org/"),
            (.cdnTX, "https://m.douban.cmliussss.net", ""),
            (.cdnAli, "https://m.douban.cmliussss.com", ""),
        ]
        for (route, base, proxy) in expectedData {
            model.dataRoute = route
            try await model.save()
            let settings = try storage.values(.douban)
            XCTAssertEqual(settings["apiBase"], .string(base))
            XCTAssertEqual(settings["searchBase"], .string("https://search.douban.com"))
            XCTAssertEqual(settings["apiProxy"], .string(proxy))
            XCTAssertEqual(settings["imageBase"], .string("https://img3.doubanio.com"))
            XCTAssertEqual(settings["imageProxy"], .string(""))
            XCTAssertEqual(settings["cookie"], .string(""))
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
            let settings = try storage.values(.douban)
            XCTAssertEqual(settings["imageBase"], .string(base))
            XCTAssertEqual(settings["imageProxy"], .string(""))
            XCTAssertEqual(settings["apiBase"], .string("https://m.douban.cmliussss.com"))
            XCTAssertEqual(settings["apiProxy"], .string(""))
            XCTAssertEqual(model.imageRoute, route)
        }
        XCTAssertEqual(saved, 7)
    }

    func testAPICDNPresetsKeepTheIndependentHTMLSearchEndpoint() async throws {
        for (base, route) in [("https://m.douban.cmliussss.net", FilmDoubanDataRoute.cdnTX),
                              ("https://m.douban.cmliussss.com", .cdnAli)] {
            let initial: RuntimeObject = [
                "apiBase": .string(base), "searchBase": .string("https://search.douban.com"),
                "apiProxy": .string(""), "imageBase": .string("https://img3.doubanio.com"),
                "imageProxy": .string(""), "cookie": .string(""),
            ]
            let storage = try DataSourceSettingsStorage(self)
            try storage.seed(initial, for: .douban)
            let before = try storage.bytes(.douban)
            let model = FilmDoubanSettingsModel(service: FilmDoubanSettingsService(
                settingsDocument: storage.documents[.douban]), didSave: {})
            try await model.load()
            XCTAssertEqual(model.dataRoute, route)
            XCTAssertEqual(model.imageRoute, .direct)
            XCTAssertEqual(try storage.bytes(.douban), before)
            try await model.save()
            XCTAssertEqual(try storage.values(.douban), initial)
            XCTAssertEqual(try storage.bytes(.douban), before)
        }
    }

    func testCustomProxyPrefixesAndIndependentRoutesSurviveLoadAndSave() async throws {
        let initial = doubanCustomSettings
        let storage = try DataSourceSettingsStorage(self)
        try storage.seed(initial, for: .douban)
        let model = FilmDoubanSettingsModel(service: FilmDoubanSettingsService(
            settingsDocument: storage.documents[.douban]), didSave: {})
        try await model.load()
        XCTAssertEqual(model.dataRoute, .custom); XCTAssertEqual(model.imageRoute, .custom)
        XCTAssertEqual(model.cookie, "synthetic_cookie=initial")
        try await model.save()
        XCTAssertEqual(try storage.values(.douban), initial)
        model.dataRoute = .direct
        try await model.save()
        let direct = try storage.values(.douban)
        XCTAssertEqual(direct["imageBase"], initial["imageBase"])
        XCTAssertEqual(direct["imageProxy"], initial["imageProxy"])
        XCTAssertEqual(direct["cookie"], initial["cookie"])
        model.imageRoute = .cdnTX
        try await model.save()
        let settings = try storage.values(.douban)
        XCTAssertEqual(settings["apiBase"], .string("https://m.douban.com"))
        XCTAssertEqual(settings["apiProxy"], .string(""))
    }

    func testDoubanCookieEchoReplaceAndEmptySaveWithFailuresKeepingTheDraft() async throws {
        let storage = try DataSourceSettingsStorage(self)
        try storage.seed(doubanCustomSettings, for: .douban)
        var notifications = 0
        let model = FilmDoubanSettingsModel(service: FilmDoubanSettingsService(
            settingsDocument: storage.documents[.douban])) { notifications += 1 }
        try await model.load()
        try await model.save()
        XCTAssertEqual(try storage.values(.douban)["cookie"], .string("synthetic_cookie=initial"))
        XCTAssertEqual(model.cookie, "synthetic_cookie=initial")
        let before = try storage.bytes(.douban)
        model.cookie = ""
        try storage.setSaveFailure(true, for: .douban)
        do { try await model.save(); XCTFail("Storage failure must remain a failure") }
        catch { XCTAssertEqual(error as? FilmSettingsStorageError, .storageFailure) }
        XCTAssertEqual(model.cookie, ""); XCTAssertEqual(model.saved?.cookie, "synthetic_cookie=initial")
        XCTAssertFalse(model.busy); XCTAssertEqual(notifications, 1)
        model.cookie = "synthetic_cookie=replacement"
        model.apiProxy = "https://new.example.invalid/nested/"
        let saved = model.saved
        do { try await model.save(); XCTFail("Failed replacement must preserve input") }
        catch { XCTAssertEqual(error as? FilmSettingsStorageError, .storageFailure) }
        XCTAssertEqual(model.saved, saved)
        XCTAssertEqual(model.cookie, "synthetic_cookie=replacement")
        XCTAssertEqual(model.apiProxy, "https://new.example.invalid/nested/")
        XCTAssertEqual(notifications, 1)
        try storage.setSaveFailure(false, for: .douban)
        XCTAssertEqual(try storage.bytes(.douban), before)
        XCTAssertEqual(try storage.values(.douban), doubanCustomSettings)
        try await model.save()
        XCTAssertEqual(try storage.values(.douban)["cookie"], .string("synthetic_cookie=replacement"))
        XCTAssertEqual(try storage.values(.douban)["apiProxy"], .string("https://new.example.invalid/nested/"))
        XCTAssertEqual(model.cookie, "synthetic_cookie=replacement"); XCTAssertEqual(notifications, 2)
        model.cookie = ""
        try await model.save()
        XCTAssertEqual(try storage.values(.douban)["cookie"], .string(""))
        XCTAssertEqual(model.saved?.cookie, ""); XCTAssertEqual(model.cookie, "")
        XCTAssertEqual(notifications, 3)
        let reopened = try FilmRuntimeTestSettings(root: storage.root)
        let restored = FilmDoubanSettingsModel(service: FilmDoubanSettingsService(
            settingsDocument: reopened[.douban])) { notifications += 1 }
        try await restored.load()
        XCTAssertEqual(restored.saved, model.saved)
        XCTAssertEqual(notifications, 3)
    }

    func testTMDBSingleTokenEchoClearAndFailedDraftPreserveAllOptions() async throws {
        let storage = try DataSourceSettingsStorage(self)
        try storage.seed(tmdbInitialSettings, for: .tmdb)
        var notifications = 0
        let model = FilmTMDBSettingsModel(service: FilmTMDBService(
            settingsDocument: storage.documents[.tmdb], connection: {
                XCTFail("Native settings must not request a runtime connection")
                throw FilmSearchError.unavailable
            })) { notifications += 1 }
        try await model.load()
        XCTAssertEqual(model.apiToken, "Bearer synthetic.old.token")
        XCTAssertEqual(model.apiBase, ""); XCTAssertEqual(model.imageBase, "")
        try await model.save()
        XCTAssertEqual(try storage.values(.tmdb)["apiKey"], .string("Bearer synthetic.old.token"))
        XCTAssertEqual(try storage.values(.tmdb)["accessToken"], .string(""))
        let before = try storage.bytes(.tmdb), saved = model.saved
        model.apiToken = ""
        try storage.setSaveFailure(true, for: .tmdb)
        do { try await model.save(); XCTFail("Failure must keep the empty draft") }
        catch { XCTAssertEqual(error as? FilmSettingsStorageError, .storageFailure) }
        XCTAssertEqual(model.apiToken, ""); XCTAssertEqual(model.saved?.apiToken, "Bearer synthetic.old.token")
        XCTAssertEqual(notifications, 1)
        model.apiToken = "synthetic-new-v3-key"
        model.apiBase = "https://custom.example.invalid/nested/tmdb/3"
        model.language = "en-US"; model.region = "US"; model.includeAdult = true
        do { try await model.save(); XCTFail("Failure must retain replacement and options") }
        catch { XCTAssertEqual(error as? FilmSettingsStorageError, .storageFailure) }
        XCTAssertEqual(model.apiToken, "synthetic-new-v3-key")
        XCTAssertEqual(model.apiBase, "https://custom.example.invalid/nested/tmdb/3")
        XCTAssertEqual(model.language, "en-US"); XCTAssertEqual(model.region, "US")
        XCTAssertTrue(model.includeAdult); XCTAssertFalse(model.busy)
        XCTAssertEqual(model.saved, saved); XCTAssertEqual(notifications, 1)
        try storage.setSaveFailure(false, for: .tmdb)
        XCTAssertEqual(try storage.bytes(.tmdb), before)
        try await model.save()
        XCTAssertEqual(model.apiToken, "synthetic-new-v3-key")
        XCTAssertEqual(try storage.values(.tmdb)["apiKey"], .string("synthetic-new-v3-key"))
        XCTAssertEqual(try storage.values(.tmdb)["apiBase"], .string("https://custom.example.invalid/nested/tmdb/3"))
        XCTAssertEqual(model.saved?.accessToken, "")
        XCTAssertEqual(model.saved?.language, "en-US"); XCTAssertEqual(model.saved?.region, "US")
        XCTAssertEqual(model.saved?.includeAdult, true)
        model.apiToken = ""
        try await model.save()
        XCTAssertEqual(try storage.values(.tmdb)["apiKey"], .string(""))
        XCTAssertEqual(try storage.values(.tmdb)["accessToken"], .string(""))
        XCTAssertEqual(model.saved?.apiToken, "")
        XCTAssertEqual(notifications, 3)
        let reopened = try FilmRuntimeTestSettings(root: storage.root)
        let restored = FilmTMDBSettingsModel(service: FilmTMDBService(
            settingsDocument: reopened[.tmdb], connection: {
                XCTFail("Reopening native settings must not request a runtime connection")
                throw FilmSearchError.unavailable
            })) { notifications += 1 }
        try await restored.load()
        XCTAssertEqual(restored.saved, model.saved)
        XCTAssertEqual(notifications, 3)
    }

    func testDataSourceFormStacksFullWidthFieldsAndKeepsProviderDraftsAndCustomExpansionIndependent() async throws {
        let storage = try DataSourceSettingsStorage(self)
        try storage.seed(tmdbInitialSettings, for: .tmdb)
        let before = try storage.bytes(.tmdb)
        let tmdb = FilmTMDBSettingsModel(service: FilmTMDBService(
            settingsDocument: storage.documents[.tmdb], connection: {
                XCTFail("Editing native settings must not request a runtime connection")
                throw FilmSearchError.unavailable
            }), didSave: {})
        let douban = FilmDoubanSettingsModel(service: FilmDoubanSettingsService(
            settingsDocument: storage.documents[.douban]), didSave: {})
        try await tmdb.load(); try await douban.load()
        let page = FilmDataSourceSettingsViewController(tmdb: tmdb, douban: douban, themeProvider: DataSourceSettingsTheme())
        page.loadViewIfNeeded()
        defer { PageViewController.invalidatePages(in: page) }
        XCTAssertEqual(page.pageTitle, "刮削设置")
        let form = try XCTUnwrap(page.children.compactMap { $0 as? FilmDataSourceFormController }.first)
        let provider = try XCTUnwrap(form.sections.first?.rows.first?.content as? UISegmentedControl)
        XCTAssertEqual(provider.numberOfSegments, 2)
        XCTAssertEqual(provider.titleForSegment(at: 0), "TMDB"); XCTAssertEqual(provider.titleForSegment(at: 1), "豆瓣")
        let api = try field(form, "film.tmdb.apiBase")
        let key = try field(form, "film.tmdb.apiToken")
        XCTAssertEqual(api.keyboardType, .URL); XCTAssertFalse(key.isSecureTextEntry)
        XCTAssertTrue(api.adjustsFontForContentSizeCategory)
        let stack = try XCTUnwrap(api.superview as? UIStackView)
        XCTAssertEqual(stack.axis, .vertical); XCTAssertEqual(stack.alignment, .fill)
        XCTAssertEqual((stack.arrangedSubviews.first as? UILabel)?.text, "TMDB 数据代理地址")
        XCTAssertTrue(api.constraints.contains { $0.firstAttribute == .height && $0.constant >= 44 })
        api.text = "https://draft.example.invalid/nested"; api.sendActions(for: .editingChanged)
        key.text = "synthetic-unsaved-key"; key.sendActions(for: .editingChanged)
        provider.selectedSegmentIndex = 1; provider.sendActions(for: .valueChanged)
        XCTAssertEqual(try field(form, "film.douban.cookie").text, "")
        XCTAssertFalse(try field(form, "film.douban.cookie").isSecureTextEntry)
        XCTAssertFalse(form.sections.flatMap(\.rows).contains { $0.id == "film.douban.apiBase.row" })
        try select(form, "film.douban.data.custom")
        XCTAssertNotNil(try field(form, "film.douban.apiProxy"))
        XCTAssertFalse(form.sections.flatMap(\.rows).contains { $0.id == "film.douban.imageBase.row" })
        try select(form, "film.douban.image.custom")
        XCTAssertNotNil(try field(form, "film.douban.imageProxy"))
        let cookie = try field(form, "film.douban.cookie")
        cookie.text = "synthetic_cookie=draft"; cookie.sendActions(for: .editingChanged)
        provider.selectedSegmentIndex = 0; provider.sendActions(for: .valueChanged)
        XCTAssertEqual(try field(form, "film.tmdb.apiBase").text, "https://draft.example.invalid/nested")
        XCTAssertEqual(try field(form, "film.tmdb.apiToken").text, "synthetic-unsaved-key")
        provider.selectedSegmentIndex = 1; provider.sendActions(for: .valueChanged)
        XCTAssertEqual(try field(form, "film.douban.cookie").text, "synthetic_cookie=draft")
        XCTAssertEqual(douban.dataRoute, .custom); XCTAssertEqual(douban.imageRoute, .custom)
        XCTAssertEqual(try storage.bytes(.tmdb), before)
        XCTAssertNil(try storage.documents[.douban].read())
    }

    private func field(_ form: FilmDataSourceFormController, _ id: String) throws -> UITextField {
        func fields(_ view: UIView) -> [UITextField] {
            if let field = view as? UITextField { return [field] }
            return view.subviews.flatMap(fields)
        }
        return try XCTUnwrap(form.sections.flatMap(\.rows).compactMap(\.content).flatMap(fields).first { $0.accessibilityIdentifier == id })
    }
    private func select(_ form: FilmDataSourceFormController, _ id: String) throws {
        let row = try XCTUnwrap(form.sections.flatMap(\.rows).first { $0.id == id })
        XCTAssertTrue(row.enabled)
        try XCTUnwrap(row.action)()
    }
}

@MainActor
private final class DataSourceSettingsTheme: PageThemeProviding {
    var pagePalette: PagePalette { .preset(.lilac) }
    let animatesPageColorway = false
    let pageThemeChanges = Empty<Void, Never>().eraseToAnyPublisher()
}

private let doubanCustomSettings: RuntimeObject = [
    "apiBase": .string("https://m.douban.com"),
    "searchBase": .string("https://search.douban.com"),
    "imageBase": .string("https://img3.doubanio.com"),
    "apiProxy": .string("https://relay.example.invalid/nested/"),
    "imageProxy": .string("https://pictures.example.invalid/nested/"), "cookie": .string("synthetic_cookie=initial"),
]

private let tmdbInitialSettings: RuntimeObject = [
    "apiBase": .string("https://api.themoviedb.org/3"), "imageBase": .string("https://image.tmdb.org"),
    "apiKey": .string("synthetic-old-key"), "accessToken": .string("Bearer synthetic.old.token"),
    "language": .string("zh-CN"), "region": .string("CN"), "includeAdult": .bool(false),
]

private struct DataSourceSettingsStorage {
    let root: URL
    let documents: FilmRuntimeTestSettings

    init(_ test: XCTestCase) throws {
        let root = FileManager.default.temporaryDirectory
            .appendingPathComponent("film-data-source-settings-" + UUID().uuidString.lowercased(), isDirectory: true)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: false)
        test.addTeardownBlock { try FileManager.default.removeItem(at: root) }
        self.root = root
        documents = try FilmRuntimeTestSettings(root: root)
    }
    func seed(_ settings: RuntimeObject, for block: FilmSettingsBlock) throws {
        _ = try documents[block].update { _ in
            .object(["version": .number(1), "settings": .object(settings)])
        }
    }
    func values(_ block: FilmSettingsBlock) throws -> RuntimeObject {
        let envelope = try XCTUnwrap(try documents[block].read()?.filmObject)
        XCTAssertEqual(envelope["version"], .number(1))
        return try XCTUnwrap(envelope["settings"]?.filmObject)
    }
    func bytes(_ block: FilmSettingsBlock) throws -> Data? {
        try DocumentStore(baseURL: root, namespace: block.namespace).read(key: FilmSettingsDocument.key)
    }
    func setSaveFailure(_ failing: Bool, for block: FilmSettingsBlock) throws {
        let file = root.appendingPathComponent(block.namespace).appendingPathComponent(FilmSettingsDocument.key + ".json")
        let saved = file.appendingPathExtension("saved")
        if failing {
            try FileManager.default.moveItem(at: file, to: saved)
            do { try FileManager.default.createSymbolicLink(at: file, withDestinationURL: saved) }
            catch { try FileManager.default.moveItem(at: saved, to: file); throw error }
        } else {
            try FileManager.default.removeItem(at: file)
            try FileManager.default.moveItem(at: saved, to: file)
        }
    }
}
