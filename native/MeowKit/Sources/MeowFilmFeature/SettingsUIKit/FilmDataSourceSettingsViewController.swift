#if os(iOS)
import UIKit
import MeowUI

@MainActor
public final class FilmDataSourceSettingsViewController: PageViewController {
    private let tmdb: FilmTMDBSettingsModel
    private let douban: FilmDoubanSettingsModel

    public init(tmdb: FilmTMDBSettingsModel, douban: FilmDoubanSettingsModel, themeProvider: any PageThemeProviding) {
        self.tmdb = tmdb; self.douban = douban
        super.init(themeProvider: themeProvider)
        pageTitle = "数据源"
    }
    public override func viewDidLoad() {
        super.viewDidLoad()
        view.accessibilityIdentifier = "film.dataSources.settings"
        let form = FilmDataSourceFormController(tmdb: tmdb, douban: douban, themeProvider: themeProvider)
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
final class FilmDataSourceFormController: FilmSettingsListController {
    private let tmdb: FilmTMDBSettingsModel
    private let douban: FilmDoubanSettingsModel
    private let provider = UISegmentedControl(items: ["TMDB", "豆瓣"])
    private var providerHeight: NSLayoutConstraint?
    private let api = filmTextField("TMDB 数据代理地址", id: "film.tmdb.apiBase")
    private let images = filmTextField("TMDB 图片代理地址", id: "film.tmdb.imageBase")
    private let token = filmTextField("TMDB API TOKEN", id: "film.tmdb.apiToken")
    private let language = filmTextField("语言", id: "film.tmdb.language")
    private let region = filmTextField("地区", id: "film.tmdb.region")
    private let cookie = filmTextField("豆瓣搜索 Cookie", id: "film.douban.cookie")
    private let dataProxy = filmTextField("豆瓣代理地址", id: "film.douban.apiProxy")
    private let imageProxy = filmTextField("豆瓣图片代理地址", id: "film.douban.imageProxy")
    override var avoidsKeyboard: Bool { true }
    override var usesPageHeader: Bool { false }

    init(tmdb: FilmTMDBSettingsModel, douban: FilmDoubanSettingsModel, themeProvider: any PageThemeProviding) {
        self.tmdb = tmdb; self.douban = douban
        super.init(themeProvider: themeProvider)
    }
    override func viewDidLoad() {
        super.viewDidLoad()
        provider.selectedSegmentIndex = 0
        provider.accessibilityIdentifier = "film.dataSources.provider"
        provider.accessibilityLabel = "数据源"
        let providerHeight = provider.heightAnchor.constraint(greaterThanOrEqualToConstant: 44)
        self.providerHeight = providerHeight; providerHeight.isActive = true
        provider.addAction(UIAction { [weak self] _ in
            guard let self else { return }
            view.endEditing(true); render(); loadSelectedProvider()
        }, for: .valueChanged)
        for field in [api, images, dataProxy, imageProxy] {
            field.keyboardType = .URL
            field.smartQuotesType = .no; field.smartDashesType = .no
        }
        for field in [token, cookie, language, region] {
            field.keyboardType = .asciiCapable
            field.smartQuotesType = .no; field.smartDashesType = .no
        }
        for field in [api, images, token, language, region] {
            field.addAction(UIAction { [weak self] _ in self?.editTMDB() }, for: .editingChanged)
        }
        for field in [cookie, dataProxy, imageProxy] {
            field.addAction(UIAction { [weak self] _ in self?.editDouban() }, for: .editingChanged)
        }
        observe(tmdb) { [weak self] in self?.render() }
        observe(douban) { [weak self] in self?.render() }
        registerForTraitChanges([UITraitPreferredContentSizeCategory.self]) {
            (page: FilmDataSourceFormController, _: UITraitCollection) in page.render()
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
        tmdb.apiToken = token.text ?? ""; tmdb.language = language.text ?? ""; tmdb.region = region.text ?? ""
    }
    private func editDouban() {
        douban.cookie = cookie.text ?? ""
        douban.apiProxy = dataProxy.text ?? ""; douban.imageProxy = imageProxy.text ?? ""
    }
    private func fieldRow(_ title: String, _ field: UITextField) -> FilmSettingsRow {
        let label = UILabel()
        label.text = title; label.textColor = UIColor(theme.settings.ink)
        label.font = .preferredFont(forTextStyle: .body)
        label.adjustsFontForContentSizeCategory = true; label.numberOfLines = 0
        field.textColor = UIColor(theme.settings.ink)
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
        token.text = tmdb.apiToken; language.text = tmdb.language; region.text = tmdb.region
        cookie.text = douban.cookie
        dataProxy.text = douban.apiProxy; imageProxy.text = douban.imageProxy
        for field in [api, images, token, language, region] { field.isEnabled = tmdbEnabled }
        for field in [cookie, dataProxy, imageProxy] { field.isEnabled = doubanEnabled }
        sections = [.init(title: "", rows: [.init(title: "", id: "film.dataSources.provider.row", content: provider)])]
        if provider.selectedSegmentIndex == 0 {
            sections.append(.init(title: "TMDB 设置", rows: [
                fieldRow("TMDB API TOKEN", token), fieldRow("TMDB 数据代理地址", api), fieldRow("TMDB 图片代理地址", images),
                fieldRow("语言", language), fieldRow("地区", region),
                filmSwitchRow("包含成人内容", id: "film.tmdb.includeAdult", isOn: tmdb.includeAdult,
                    enabled: tmdbEnabled) { [weak self] in self?.tmdb.includeAdult = $0 },
                .init(title: "保存", id: "film.tmdb.save", enabled: tmdbEnabled, action: { [weak self] in self?.save(isTMDB: true) }),
            ]))
        } else {
            var dataRows: [FilmSettingsRow] = FilmDoubanDataRoute.allCases.map { route in
                .init(title: route.title, id: "film.douban.data." + route.rawValue, enabled: doubanEnabled,
                      selected: douban.dataRoute == route, action: { [weak self] in self?.douban.dataRoute = route; self?.render() })
            }
            if douban.dataRoute == .custom { dataRows.append(fieldRow("豆瓣代理地址", dataProxy)) }
            sections.append(.init(title: "豆瓣数据代理", rows: dataRows))
            var imageRows: [FilmSettingsRow] = FilmDoubanImageRoute.allCases.map { route in
                .init(title: route.title, id: "film.douban.image." + route.rawValue, enabled: doubanEnabled,
                      selected: douban.imageRoute == route, action: { [weak self] in self?.douban.imageRoute = route; self?.render() })
            }
            if douban.imageRoute == .custom { imageRows.append(fieldRow("豆瓣图片代理地址", imageProxy)) }
            sections.append(.init(title: "豆瓣图片代理", rows: imageRows))
            sections.append(.init(title: "豆瓣配置", rows: [
                fieldRow("豆瓣搜索 Cookie", cookie),
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
