#if os(iOS)
import UIKit
import MeowUI

@MainActor
final class FilmTMDBSettingsViewController: FilmSettingsListController {
    private let model: FilmTMDBSettingsModel
    private let api = filmTextField("API 基址", id: "film.tmdb.apiBase")
    private let images = filmTextField("图片基址", id: "film.tmdb.imageBase")
    private let key = filmTextField("API Key", id: "film.tmdb.apiKey")
    private let token = filmTextField("Access Token", id: "film.tmdb.accessToken")
    override var avoidsKeyboard: Bool { true }
    init(model: FilmTMDBSettingsModel, themeProvider: any PageThemeProviding) {
        self.model = model; super.init(themeProvider: themeProvider); pageTitle = "TMDB"
    }
    override func viewDidLoad() {
        super.viewDidLoad()
        view.accessibilityIdentifier = "film.tmdb.settings"
        key.isSecureTextEntry = true; token.isSecureTextEntry = true
        for field in [api, images, key, token] {
            field.addAction(UIAction { [weak self] _ in self?.edit() }, for: .editingChanged)
        }
        observe(model) { [weak self] in self?.render() }
        render()
    }
    override func pageDidBecomeActive() {
        runPageTask { [weak self] in
            guard let self else { return }
            do { try await model.load(); render() }
            catch { failure(error, retry: { [weak self] in self?.pageDidBecomeActive() }) }
        }
    }
    private func edit() {
        model.apiBase = api.text ?? ""; model.imageBase = images.text ?? ""
        model.apiKey = key.text ?? ""; model.accessToken = token.text ?? ""
    }
    override func render() {
        api.text = model.apiBase; images.text = model.imageBase
        key.text = model.apiKey; token.text = model.accessToken
        for field in [api, images, key, token] { field.isEnabled = model.saved != nil && !model.busy }
        sections = [.init(title: "", rows: [
            .init(title: "API 基址", id: "", content: api),
            .init(title: "图片基址", id: "", content: images),
            .init(title: "API Key", id: "", content: key),
            .init(title: "API Key", detail: model.clearAPIKey ? "待清除" : (model.saved?.apiKeyConfigured == true ? "已配置" : "未配置"),
                  id: "film.tmdb.keyStatus"),
            .init(title: "清除 API Key", id: "film.tmdb.clearKey", enabled: !model.busy && model.saved != nil,
                  action: { [weak self] in self?.model.clearKey(); self?.render() }),
            .init(title: "Access Token", id: "", content: token),
            .init(title: "Access Token", detail: model.clearAccessToken ? "待清除" : (model.saved?.accessTokenConfigured == true ? "已配置" : "未配置"),
                  id: "film.tmdb.tokenStatus"),
            .init(title: "清除 Access Token", id: "film.tmdb.clearToken", enabled: !model.busy && model.saved != nil,
                  action: { [weak self] in self?.model.clearToken(); self?.render() }),
            .init(title: "保存", id: "film.tmdb.save", enabled: !model.busy && model.saved != nil,
                  action: { [weak self] in self?.save() }),
        ])]
        super.render()
    }
    private func save() {
        edit()
        // An admitted settings write obtains its receipt even if navigation hides.
        Task { [self] in
            guard !model.busy, model.saved != nil else { return }
            do { try await model.save(); render(); notice("已保存") }
            catch { failure(error, retry: { [weak self] in self?.save() }) }
        }
    }
}
#endif
