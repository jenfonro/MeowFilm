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
        let initialDouban = try await storage.documents[.douban].read()
        XCTAssertNil(initialDouban, "Loading settings must not write defaults")
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
        let reopened = FilmRuntimeTestSettings(database: storage.database)
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
        let reopened = FilmRuntimeTestSettings(database: storage.database)
        let restored = FilmTMDBSettingsModel(service: FilmTMDBService(
            settingsDocument: reopened[.tmdb], connection: {
                XCTFail("Reopening native settings must not request a runtime connection")
                throw FilmSearchError.unavailable
            })) { notifications += 1 }
        try await restored.load()
        XCTAssertEqual(restored.saved, model.saved)
        XCTAssertEqual(notifications, 3)
    }

    func testDataSourceWholeFormKeepsProviderDraftsAndCustomExpansionIndependent() async throws {
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
        let provider = try XCTUnwrap(descendants(form.view).first {
            $0.accessibilityIdentifier == "film.dataSources.provider"
        } as? UISegmentedControl)
        XCTAssertEqual(provider.numberOfSegments, 2)
        XCTAssertEqual(provider.titleForSegment(at: 0), "TMDB"); XCTAssertEqual(provider.titleForSegment(at: 1), "豆瓣")
        XCTAssertTrue(provider.superview === form.view, "The provider capsule must not be nested in a settings card")
        XCTAssertNil(form.table.superview)
        XCTAssertFalse(form.form.boxedInputs)
        let api = try field(form, "film.tmdb.apiBase")
        let key = try field(form, "film.tmdb.apiToken")
        XCTAssertEqual(api.keyboardType, .URL); XCTAssertFalse(key.isSecureTextEntry)
        XCTAssertTrue(api.adjustsFontForContentSizeCategory)
        let stack = try XCTUnwrap(api.superview as? UIStackView)
        XCTAssertEqual(stack.axis, .vertical); XCTAssertEqual(stack.alignment, .fill)
        XCTAssertEqual((stack.arrangedSubviews.first as? UILabel)?.text, "TMDB 数据代理地址")
        XCTAssertTrue(api.constraints.contains { $0.firstAttribute == .height && $0.constant >= 36 })
        page.view.frame = CGRect(x: 0, y: 0, width: 390, height: 844)
        page.view.layoutIfNeeded()
        XCTAssertGreaterThan(provider.bounds.height, 0)
        XCTAssertEqual(provider.layer.cornerRadius, provider.bounds.height / 2, accuracy: 0.5)
        XCTAssertGreaterThan(api.bounds.width, api.bounds.height)
        XCTAssertEqual(api.layer.cornerRadius, api.bounds.height / 2, accuracy: 0.5)
        XCTAssertFalse(descendants(form.view).compactMap { $0 as? UILabel }.contains {
            $0.text == "TMDB 设置" || $0.text == "豆瓣配置"
        })
        api.text = "https://draft.example.invalid/nested"; api.sendActions(for: .editingChanged)
        key.text = "synthetic-unsaved-key"; key.sendActions(for: .editingChanged)
        provider.selectedSegmentIndex = 1; provider.sendActions(for: .valueChanged)
        XCTAssertEqual(try field(form, "film.douban.cookie").text, "")
        XCTAssertFalse(try field(form, "film.douban.cookie").isSecureTextEntry)
        XCTAssertFalse(descendants(form.view).contains { $0.accessibilityIdentifier == "film.douban.apiBase" })
        let dataProxy = try field(form, "film.douban.apiProxy")
        let imageProxy = try field(form, "film.douban.imageProxy")
        XCTAssertTrue(try XCTUnwrap(dataProxy.superview).isHidden)
        XCTAssertTrue(try XCTUnwrap(imageProxy.superview).isHidden)
        try select(form, "film.douban.data.custom")
        XCTAssertFalse(try XCTUnwrap(dataProxy.superview).isHidden)
        XCTAssertTrue(try XCTUnwrap(imageProxy.superview).isHidden)
        XCTAssertFalse(descendants(form.view).contains { $0.accessibilityIdentifier == "film.douban.imageBase" })
        try select(form, "film.douban.image.custom")
        XCTAssertFalse(try XCTUnwrap(imageProxy.superview).isHidden)
        dataProxy.text = "https://data.example.invalid/draft/"; dataProxy.sendActions(for: .editingChanged)
        imageProxy.text = "https://images.example.invalid/draft/"; imageProxy.sendActions(for: .editingChanged)
        let cookie = try field(form, "film.douban.cookie")
        cookie.text = "synthetic_cookie=draft"; cookie.sendActions(for: .editingChanged)
        form.applyTheme(); form.render()
        form.traitOverrides.preferredContentSizeCategory = .accessibilityExtraExtraExtraLarge
        form.applyTheme()
        page.view.frame = CGRect(x: 0, y: 0, width: 320, height: 480)
        page.view.layoutIfNeeded(); form.view.layoutIfNeeded()
        for id in ["film.douban.dataRoute", "film.douban.imageRoute"] {
            let button = try XCTUnwrap(descendants(form.view).first { $0.accessibilityIdentifier == id } as? UIButton)
            let label = try XCTUnwrap(button.titleLabel)
            let fitted = label.sizeThatFits(.init(width: label.bounds.width, height: .greatestFiniteMagnitude))
            let insets = try XCTUnwrap(button.configuration).contentInsets
            let labelFrame = label.convert(label.bounds, to: button)
            XCTAssertGreaterThan(label.bounds.width, 0)
            XCTAssertGreaterThan(fitted.height, label.font.lineHeight, "Narrow accessibility options must exercise wrapping")
            XCTAssertGreaterThanOrEqual(label.bounds.height + 1, fitted.height, "The option must not crop wrapped text")
            XCTAssertGreaterThanOrEqual(labelFrame.minY + 1, insets.top)
            XCTAssertLessThanOrEqual(labelFrame.maxY, button.bounds.height - insets.bottom + 1)
        }
        XCTAssertTrue(try field(form, "film.douban.cookie") === cookie)
        XCTAssertTrue(try field(form, "film.douban.apiProxy") === dataProxy)
        XCTAssertTrue(try field(form, "film.douban.imageProxy") === imageProxy)
        XCTAssertEqual(cookie.text, "synthetic_cookie=draft")
        try select(form, "film.douban.data.direct")
        XCTAssertTrue(try XCTUnwrap(dataProxy.superview).isHidden)
        XCTAssertFalse(try XCTUnwrap(imageProxy.superview).isHidden)
        try select(form, "film.douban.data.custom")
        XCTAssertEqual(dataProxy.text, "https://data.example.invalid/draft/")
        XCTAssertEqual(imageProxy.text, "https://images.example.invalid/draft/")
        provider.selectedSegmentIndex = 0; provider.sendActions(for: .valueChanged)
        XCTAssertEqual(try field(form, "film.tmdb.apiBase").text, "https://draft.example.invalid/nested")
        XCTAssertEqual(try field(form, "film.tmdb.apiToken").text, "synthetic-unsaved-key")
        provider.selectedSegmentIndex = 1; provider.sendActions(for: .valueChanged)
        XCTAssertEqual(try field(form, "film.douban.cookie").text, "synthetic_cookie=draft")
        XCTAssertEqual(douban.dataRoute, .custom); XCTAssertEqual(douban.imageRoute, .custom)
        XCTAssertEqual(try storage.bytes(.tmdb), before)
        let savedDouban = try await storage.documents[.douban].read()
        XCTAssertNil(savedDouban)
    }

    private func field(_ form: FilmDataSourceFormController, _ id: String) throws -> UITextField {
        try XCTUnwrap(descendants(form.view).first { $0.accessibilityIdentifier == id } as? UITextField)
    }
    private func select(_ form: FilmDataSourceFormController, _ id: String) throws {
        let button = try XCTUnwrap(descendants(form.view).compactMap { $0 as? UIButton }.first {
            $0.menu?.children.contains { ($0 as? UIAction)?.identifier.rawValue == id } == true
        })
        XCTAssertTrue(button.isEnabled)
        let action = try XCTUnwrap(button.menu?.children.compactMap { $0 as? UIAction }.first {
            $0.identifier.rawValue == id
        })
        let control = UIButton(type: .system)
        control.addAction(action, for: .touchUpInside); control.sendActions(for: .touchUpInside)
    }
    private func descendants(_ view: UIView) -> [UIView] {
        view.subviews.flatMap { [$0] + descendants($0) }
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

@MainActor private struct DataSourceSettingsStorage {
    let root: URL
    let database: SQLiteDatabase
    let documents: FilmRuntimeTestSettings

    init(_ test: XCTestCase) throws {
        database = try test.appStorageTestDatabase()
        root = database.dataRoot
        documents = FilmRuntimeTestSettings(database: database)
    }
    func seed(_ settings: RuntimeObject, for block: FilmSettingsBlock) throws {
        _ = try appStorageTestWait { try await documents[block].update { _ in
            .object(["version": .number(1), "settings": .object(settings)])
        } }
    }
    func values(_ block: FilmSettingsBlock) throws -> RuntimeObject {
        let envelope = try XCTUnwrap(try documents[block].appStorageTestRead()?.filmObject)
        XCTAssertEqual(envelope["version"], .number(1))
        return try XCTUnwrap(envelope["settings"]?.filmObject)
    }
    func bytes(_ block: FilmSettingsBlock) throws -> Data? {
        try documents[block].appStorageTestBytes()
    }
    func setSaveFailure(_ failing: Bool, for block: FilmSettingsBlock) throws {
        try appStorageTestReadOnly(database, failing)
    }
}
