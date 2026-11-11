#if os(iOS)
import UIKit
import MeowUI

@MainActor
public final class FilmScrapingSettingsViewController: PageViewController {
    private let tmdb: FilmTMDBSettingsModel
    private let douban: FilmDoubanSettingsModel

    public init(tmdb: FilmTMDBSettingsModel, douban: FilmDoubanSettingsModel, themeProvider: any PageThemeProviding) {
        self.tmdb = tmdb; self.douban = douban
        super.init(themeProvider: themeProvider)
        pageTitle = "刮削设置"
    }
    public override func viewDidLoad() {
        super.viewDidLoad()
        view.accessibilityIdentifier = "film.scraping.settings"
        let form = FilmScrapingFormController(tmdb: tmdb, douban: douban, themeProvider: themeProvider)
        addChild(form); view.addSubview(form.view)
        form.view.translatesAutoresizingMaskIntoConstraints = false
        NSLayoutConstraint.activate([
            form.view.topAnchor.constraint(equalTo: contentTopAnchor),
            form.view.leadingAnchor.constraint(equalTo: view.leadingAnchor),
            form.view.trailingAnchor.constraint(equalTo: view.trailingAnchor),
            form.view.bottomAnchor.constraint(equalTo: view.bottomAnchor),
        ])
        form.didMove(toParent: self)
    }
}

@MainActor
final class FilmScrapingFormController: FilmSettingsListController {
    private let tmdb: FilmTMDBSettingsModel
    private let douban: FilmDoubanSettingsModel
    private let provider = UISegmentedControl(items: ["TMDB", "豆瓣"])
    private var providerHeight: NSLayoutConstraint?
    private let api = filmTextField("API 基址", id: "film.tmdb.apiBase")
    private let images = filmTextField("图片基址", id: "film.tmdb.imageBase")
    private let key = filmTextField("API Key", id: "film.tmdb.apiKey")
    private let token = filmTextField("Access Token", id: "film.tmdb.accessToken")
    private let cookie = filmTextField("Cookie", id: "film.douban.cookie")
    private let doubanAPI = filmTextField("API 基址", id: "film.douban.apiBase")
    private let doubanSearch = filmTextField("搜索基址", id: "film.douban.searchBase")
    private let doubanImages = filmTextField("图片基址", id: "film.douban.imageBase")
    private let dataProxy = filmTextField("数据代理模板", id: "film.douban.apiProxy")
    private let imageProxy = filmTextField("图片代理模板", id: "film.douban.imageProxy")
    override var avoidsKeyboard: Bool { true }
    override var usesPageHeader: Bool { false }

    init(tmdb: FilmTMDBSettingsModel, douban: FilmDoubanSettingsModel, themeProvider: any PageThemeProviding) {
        self.tmdb = tmdb; self.douban = douban
        super.init(themeProvider: themeProvider)
    }
    override func viewDidLoad() {
        super.viewDidLoad()
        provider.selectedSegmentIndex = 0
        provider.accessibilityIdentifier = "film.scraping.provider"
        provider.accessibilityLabel = "刮削供应商"
        let providerHeight = provider.heightAnchor.constraint(greaterThanOrEqualToConstant: 44)
        self.providerHeight = providerHeight; providerHeight.isActive = true
        provider.addAction(UIAction { [weak self] _ in
            guard let self else { return }
            view.endEditing(true); render(); loadSelectedProvider()
        }, for: .valueChanged)
        for field in [api, images, doubanAPI, doubanSearch, doubanImages, dataProxy, imageProxy] {
            field.keyboardType = .URL
            field.smartQuotesType = .no; field.smartDashesType = .no
        }
        for field in [key, token, cookie] {
            field.isSecureTextEntry = true
            field.keyboardType = .asciiCapable
            field.textContentType = .password
        }
        for field in [api, images, key, token] {
            field.addAction(UIAction { [weak self] _ in self?.editTMDB() }, for: .editingChanged)
        }
        for field in [cookie, doubanAPI, doubanSearch, doubanImages, dataProxy, imageProxy] {
            field.addAction(UIAction { [weak self] _ in self?.editDouban() }, for: .editingChanged)
        }
        observe(tmdb) { [weak self] in self?.render() }
        observe(douban) { [weak self] in self?.render() }
        registerForTraitChanges([UITraitPreferredContentSizeCategory.self]) {
            (page: FilmScrapingFormController, _: UITraitCollection) in page.render()
        }
        render()
    }
    override func pageDidBecomeActive() { render(); loadSelectedProvider() }
    override func applyTheme() {
        super.applyTheme()
        render()
    }
    private func loadSelectedProvider() {
        let isTMDB = provider.selectedSegmentIndex == 0
        runPageTask { [weak self] in
            guard let self else { return }
            do {
                if isTMDB { try await tmdb.load() } else { try await douban.load() }
                render()
            } catch { failure(error, retry: { [weak self] in self?.loadSelectedProvider() }) }
        }
    }
    private func editTMDB() {
        tmdb.apiBase = api.text ?? ""; tmdb.imageBase = images.text ?? ""
        tmdb.apiKey = key.text ?? ""; tmdb.accessToken = token.text ?? ""
    }
    private func editDouban() {
        douban.cookie = cookie.text ?? ""
        douban.apiBase = doubanAPI.text ?? ""; douban.searchBase = doubanSearch.text ?? ""
        douban.imageBase = doubanImages.text ?? ""
        douban.apiProxy = dataProxy.text ?? ""; douban.imageProxy = imageProxy.text ?? ""
    }
    private func fieldRow(_ title: String, _ field: UITextField) -> FilmSettingsRow {
        let label = UILabel()
        label.text = title; label.textColor = UIColor(theme.ink)
        label.font = .preferredFont(forTextStyle: .body)
        label.adjustsFontForContentSizeCategory = true; label.numberOfLines = 0
        field.textColor = UIColor(theme.ink)
        field.placeholder = nil
        let stack = UIStackView(arrangedSubviews: [label, field])
        stack.axis = .vertical; stack.alignment = .fill; stack.spacing = 6
        return .init(title: title, id: (field.accessibilityIdentifier ?? "") + ".row", content: stack)
    }
    override func render() {
        guard isViewLoaded else { return }
        let providerFont = UIFont.preferredFont(forTextStyle: .body)
        provider.setTitleTextAttributes([.font: providerFont], for: .normal)
        providerHeight?.constant = max(44, providerFont.lineHeight + 16)
        let tmdbEnabled = tmdb.saved != nil && !tmdb.busy
        let doubanEnabled = douban.saved != nil && !douban.busy
        api.text = tmdb.apiBase; images.text = tmdb.imageBase
        key.text = tmdb.apiKey; token.text = tmdb.accessToken
        cookie.text = douban.cookie
        doubanAPI.text = douban.apiBase; doubanSearch.text = douban.searchBase; doubanImages.text = douban.imageBase
        dataProxy.text = douban.apiProxy; imageProxy.text = douban.imageProxy
        for field in [api, images, key, token] { field.isEnabled = tmdbEnabled }
        for field in [cookie, doubanAPI, doubanSearch, doubanImages, dataProxy, imageProxy] { field.isEnabled = doubanEnabled }
        sections = [.init(title: "", rows: [.init(title: "", id: "film.scraping.provider.row", content: provider)])]
        if provider.selectedSegmentIndex == 0 {
            sections.append(.init(title: "", rows: [
                fieldRow("API 基址", api), fieldRow("图片基址", images), fieldRow("API Key", key),
                .init(title: "API Key", detail: tmdb.clearAPIKey ? "待清除" : (tmdb.saved?.apiKeyConfigured == true ? "已配置" : "未配置"), id: "film.tmdb.keyStatus"),
                .init(title: "清除 API Key", id: "film.tmdb.clearKey", enabled: tmdbEnabled,
                      action: { [weak self] in self?.tmdb.clearKey(); self?.render() }),
                fieldRow("Access Token", token),
                .init(title: "Access Token", detail: tmdb.clearAccessToken ? "待清除" : (tmdb.saved?.accessTokenConfigured == true ? "已配置" : "未配置"), id: "film.tmdb.tokenStatus"),
                .init(title: "清除 Access Token", id: "film.tmdb.clearToken", enabled: tmdbEnabled,
                      action: { [weak self] in self?.tmdb.clearToken(); self?.render() }),
                .init(title: "保存", id: "film.tmdb.save", enabled: tmdbEnabled, action: { [weak self] in self?.save(isTMDB: true) }),
            ]))
        } else {
            sections.append(.init(title: "", rows: [
                fieldRow("Cookie", cookie),
                .init(title: "Cookie", detail: douban.clearCookie ? "待清除" : (douban.saved?.cookieConfigured == true ? "已配置" : "未配置"), id: "film.douban.cookieStatus"),
                .init(title: "清除 Cookie", id: "film.douban.clearCookie", enabled: doubanEnabled,
                      action: { [weak self] in self?.douban.clearCredential(); self?.render() }),
            ]))
            var dataRows: [FilmSettingsRow] = FilmDoubanDataRoute.allCases.map { route in
                .init(title: route.title, id: "film.douban.data." + route.rawValue, enabled: doubanEnabled,
                      selected: douban.dataRoute == route, action: { [weak self] in self?.douban.dataRoute = route; self?.render() })
            }
            if douban.dataRoute == .custom {
                dataRows += [fieldRow("API 基址", doubanAPI), fieldRow("搜索基址", doubanSearch), fieldRow("数据代理模板", dataProxy)]
            }
            sections.append(.init(title: "数据代理", rows: dataRows))
            var imageRows: [FilmSettingsRow] = FilmDoubanImageRoute.allCases.map { route in
                .init(title: route.title, id: "film.douban.image." + route.rawValue, enabled: doubanEnabled,
                      selected: douban.imageRoute == route, action: { [weak self] in self?.douban.imageRoute = route; self?.render() })
            }
            if douban.imageRoute == .custom { imageRows += [fieldRow("图片基址", doubanImages), fieldRow("图片代理模板", imageProxy)] }
            sections.append(.init(title: "图片代理", rows: imageRows))
            sections.append(.init(title: "", rows: [
                .init(title: "保存", id: "film.douban.save", enabled: doubanEnabled, action: { [weak self] in self?.save(isTMDB: false) }),
            ]))
        }
        super.render()
    }
    private func save(isTMDB: Bool) {
        view.endEditing(true)
        // An admitted write obtains its receipt even when navigation hides the page.
        Task { [self] in
            do {
                if isTMDB {
                    guard !tmdb.busy, tmdb.saved != nil else { return }
                    try await tmdb.save()
                } else {
                    guard !douban.busy, douban.saved != nil else { return }
                    try await douban.save()
                }
                render(); notice("已保存")
            } catch { failure(error, retry: { [weak self] in self?.save(isTMDB: isTMDB) }) }
        }
    }
}
#endif
