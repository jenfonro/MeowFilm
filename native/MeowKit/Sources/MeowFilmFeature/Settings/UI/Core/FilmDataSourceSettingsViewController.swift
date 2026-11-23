#if os(iOS)
import UIKit
import MeowUI

@MainActor
public final class FilmDataSourceSettingsViewController: SettingsFormViewController {
    private let tmdb: FilmTMDBSettingsModel
    private let douban: FilmDoubanSettingsModel
    private let provider: SettingsSegmentedControl = {
        let control = SettingsSegmentedControl(items: ["TMDB", "豆瓣"])
        control.style = .accentPill
        return control
    }()
    private var providerHeight: NSLayoutConstraint?
    private let tmdbFields = UIStackView(), doubanFields = UIStackView()
    private lazy var api = addTextField("TMDB 数据代理地址", id: "film.tmdb.apiBase", value: tmdb.apiBase, in: tmdbFields) { [weak self] in self?.tmdb.apiBase = $0 }
    private lazy var images = addTextField("TMDB 图片代理地址", id: "film.tmdb.imageBase", value: tmdb.imageBase, in: tmdbFields) { [weak self] in self?.tmdb.imageBase = $0 }
    private lazy var token = addTextField("TMDB API TOKEN", id: "film.tmdb.apiToken", value: tmdb.apiToken, in: tmdbFields) { [weak self] in self?.tmdb.apiToken = $0 }
    private lazy var language = addTextField("语言", id: "film.tmdb.language", value: tmdb.language, in: tmdbFields) { [weak self] in self?.tmdb.language = $0 }
    private lazy var region = addTextField("地区", id: "film.tmdb.region", value: tmdb.region, in: tmdbFields) { [weak self] in self?.tmdb.region = $0 }
    private lazy var cookie = addTextField("豆瓣搜索 Cookie", id: "film.douban.cookie", value: douban.cookie, in: doubanFields) { [weak self] in self?.douban.cookie = $0 }
    private lazy var dataProxy = addTextField("豆瓣代理地址", id: "film.douban.apiProxy", value: douban.apiProxy, in: doubanFields) { [weak self] in self?.douban.apiProxy = $0 }
    private lazy var imageProxy = addTextField("豆瓣图片代理地址", id: "film.douban.imageProxy", value: douban.imageProxy, in: doubanFields) { [weak self] in self?.douban.imageProxy = $0 }
    private lazy var adult = ManagementFormView.check("包含成人内容", value: tmdb.includeAdult) { [weak self] in self?.tmdb.includeAdult = $0 }
    private lazy var dataRoute = filmOption("", id: "film.douban.dataRoute", disclosure: true, action: {})
    private lazy var imageRoute = filmOption("", id: "film.douban.imageRoute", disclosure: true, action: {})
    private lazy var tmdbSave = filmOption("保存", id: "film.tmdb.save") { [weak self] in self?.save(isTMDB: true) }
    private lazy var doubanSave = filmOption("保存", id: "film.douban.save") { [weak self] in self?.save(isTMDB: false) }

    public override var formHeader: UIView? { provider }

    public init(tmdb: FilmTMDBSettingsModel, douban: FilmDoubanSettingsModel, themeProvider: any PageThemeProviding) {
        self.tmdb = tmdb; self.douban = douban
        super.init(themeProvider: themeProvider)
        pageTitle = "刮削设置"
    }

    public override func viewDidLoad() {
        super.viewDidLoad()
        view.accessibilityIdentifier = "film.dataSources.settings"
        form.accessibilityIdentifier = "film.dataSources.form"
        provider.selectedSegmentIndex = 0
        provider.accessibilityIdentifier = "film.dataSources.provider"
        provider.accessibilityLabel = "数据源"
        let providerHeight = provider.heightAnchor.constraint(greaterThanOrEqualToConstant: 44)
        self.providerHeight = providerHeight; providerHeight.isActive = true
        registerThemeable(provider)
        provider.addAction(UIAction { [weak self] _ in
            guard let self else { return }
            view.endEditing(true); render(); loadSelectedProvider()
        }, for: .valueChanged)
        tmdbFields.axis = .vertical; doubanFields.axis = .vertical
        _ = token; _ = api; _ = images; _ = language; _ = region
        adult.accessibilityIdentifier = "film.tmdb.includeAdult"
        tmdbFields.addArrangedSubview(filmOptionRow([adult]))
        tmdbFields.addArrangedSubview(filmOptionRow([tmdbSave])); form.addSection(tmdbFields)
        dataRoute.showsMenuAsPrimaryAction = true; imageRoute.showsMenuAsPrimaryAction = true
        doubanFields.addArrangedSubview(filmOptionRow([dataRoute])); _ = dataProxy
        doubanFields.addArrangedSubview(filmOptionRow([imageRoute])); _ = imageProxy; _ = cookie
        doubanFields.addArrangedSubview(filmOptionRow([doubanSave])); form.addSection(doubanFields)
        for field in [api, images, dataProxy, imageProxy] { field.keyboardType = .URL }
        for field in [token, cookie, language, region] { field.keyboardType = .asciiCapable }
        token.textContentType = .none; cookie.textContentType = .none
        observe(tmdb, whenVisible: true) { [weak self] in self?.render() }
        observe(douban, whenVisible: true) { [weak self] in self?.render() }
        render(); applyTheme()
    }

    public override func pageDidBecomeActive() { render(); loadSelectedProvider() }

    public override func applyTheme() {
        super.applyTheme()
        guard isViewLoaded else { return }
        let font = UIFont.preferredFont(forTextStyle: .body, compatibleWith: traitCollection)
        providerHeight?.constant = max(44, font.lineHeight + 16)
    }

    private func loadSelectedProvider() {
        let isTMDB = provider.selectedSegmentIndex == 0
        runPageTask(key: "load", retry: { [weak self] in self?.loadSelectedProvider() }) { [weak self, tmdb, douban] in
            if isTMDB { try await tmdb.load() } else { try await douban.load() }
            self?.render()
        }
    }

    private func render() {
        guard isViewLoaded else { return }
        let tmdbEnabled = tmdb.saved != nil && !tmdb.busy
        let doubanEnabled = douban.saved != nil && !douban.busy
        for (field, value) in [(api, tmdb.apiBase), (images, tmdb.imageBase), (token, tmdb.apiToken),
                               (language, tmdb.language), (region, tmdb.region), (cookie, douban.cookie),
                               (dataProxy, douban.apiProxy), (imageProxy, douban.imageProxy)] {
            if !field.isFirstResponder, field.text != value { field.text = value }
        }
        for field in [api, images, token, language, region] { field.isEnabled = tmdbEnabled }
        for field in [cookie, dataProxy, imageProxy] { field.isEnabled = doubanEnabled }
        adult.isSelected = tmdb.includeAdult; adult.accessibilityValue = tmdb.includeAdult ? "已勾选" : "未勾选"
        adult.isEnabled = tmdbEnabled; tmdbSave.isEnabled = tmdbEnabled; doubanSave.isEnabled = doubanEnabled
        dataRoute.isEnabled = doubanEnabled; imageRoute.isEnabled = doubanEnabled
        dataRoute.configuration?.title = "豆瓣数据代理：\(douban.dataRoute.title)"
        dataRoute.accessibilityLabel = "豆瓣数据代理"; dataRoute.accessibilityValue = douban.dataRoute.title
        dataRoute.menu = UIMenu(children: FilmDoubanDataRoute.allCases.map { route in
            UIAction(title: route.title, identifier: .init("film.douban.data." + route.rawValue),
                     state: douban.dataRoute == route ? .on : .off) { [weak self] _ in
                self?.douban.dataRoute = route; self?.render()
            }
        })
        imageRoute.configuration?.title = "豆瓣图片代理：\(douban.imageRoute.title)"
        imageRoute.accessibilityLabel = "豆瓣图片代理"; imageRoute.accessibilityValue = douban.imageRoute.title
        imageRoute.menu = UIMenu(children: FilmDoubanImageRoute.allCases.map { route in
            UIAction(title: route.title, identifier: .init("film.douban.image." + route.rawValue),
                     state: douban.imageRoute == route ? .on : .off) { [weak self] _ in
                self?.douban.imageRoute = route; self?.render()
            }
        })
        dataProxy.superview?.isHidden = douban.dataRoute != .custom
        imageProxy.superview?.isHidden = douban.imageRoute != .custom
        tmdbFields.isHidden = provider.selectedSegmentIndex != 0
        doubanFields.isHidden = provider.selectedSegmentIndex == 0
    }

    private func save(isTMDB: Bool) {
        view.endEditing(true)
        let tmdb = self.tmdb
        let douban = self.douban
        // An admitted write completes on the model even if navigation pops the page,
        // without retaining the popped view controller.
        Task { [weak self] in
            do {
                if isTMDB {
                    guard !tmdb.busy, tmdb.saved != nil else { return }
                    try await tmdb.save()
                } else {
                    guard !douban.busy, douban.saved != nil else { return }
                    try await douban.save()
                }
                self?.render()
                self?.enqueueNotice(title: "已保存")
            } catch {
                self?.enqueueError(error.localizedDescription, retry: { [weak self] in self?.save(isTMDB: isTMDB) })
            }
        }
    }
}
#endif
