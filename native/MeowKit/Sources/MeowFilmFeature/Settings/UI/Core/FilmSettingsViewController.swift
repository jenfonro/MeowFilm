#if os(iOS)
import UIKit
import MeowUI

@MainActor
public final class FilmSettingsViewController: SettingsListViewController {
    let model: FilmSettingsModel
    private var displayedLoadError: String?

    public override var avoidsKeyboard: Bool { false }

    public init(model: FilmSettingsModel, themeProvider: any PageThemeProviding) {
        self.model = model
        super.init(themeProvider: themeProvider)
        pageTitle = "影视设置"
    }

    public override func viewDidLoad() {
        super.viewDidLoad()
        renderMenu()
        observe(model, whenVisible: true) { [weak self] in
            self?.renderMenu()
            self?.showLoadError()
        }
    }

    private func renderMenu() {
        let playbackAvailable = model.playback != nil
        let rows = FilmSettingsDestination.allCases.map { destination in
            SettingsListRow(
                id: "film.settings.\(destination.rawValue)",
                title: destination.title,
                accessory: .disclosure,
                enabled: !destination.needsPlayback || playbackAvailable,
                action: { [weak self] in self?.open(destination) }
            )
        }
        applySections([SettingsListSection(id: "film.settings.main", rows: rows)])
    }

    private func open(_ destination: FilmSettingsDestination) {
        let page: UIViewController
        switch destination {
        case .search:
            page = FilmRulePageViewController(owner: model.rules, group: .search, themeProvider: themeProvider)
        case .matching:
            page = FilmSmartMatchingViewController(model: model, themeProvider: themeProvider)
        case .cats:
            page = FilmCatSourcesViewController(service: model.sources, themeProvider: themeProvider)
        case .videos:
            page = FilmVideoSourcesViewController(service: model.sources, themeProvider: themeProvider)
        case .dataSources:
            page = FilmDataSourceSettingsViewController(tmdb: model.tmdb, douban: model.douban, themeProvider: themeProvider)
        case .pan:
            page = PanAccountsViewController(model: model.pan, themeProvider: themeProvider)
        case .panInterception:
            page = FilmPanInterceptionViewController(service: model.sources, themeProvider: themeProvider)
        case .preferences:
            guard let playback = model.playback else { enqueueError("播放设置暂不可用"); return }
            page = FilmSmartPlaybackSettingsController(owner: playback, themeProvider: themeProvider)
        case .player:
            page = FilmPlayerSettingsController(owner: model.player, themeProvider: themeProvider)
        }
        navigationController?.pushViewController(page, animated: true)
    }

    private func showLoadError() {
        guard let error = model.loadError else {
            displayedLoadError = nil
            return
        }
        guard error != displayedLoadError else { return }
        displayedLoadError = error
        enqueueError(error, retry: { [weak self] in
            self?.displayedLoadError = nil
            self?.model.start()
        })
    }
}

private enum FilmSettingsDestination: String, CaseIterable {
    case search, matching, cats, videos, dataSources, pan, panInterception, preferences, player
    var title: String {
        switch self {
        case .search: return "搜索净化设置"
        case .matching: return "智能匹配设置"
        case .cats: return "猫源"
        case .videos: return "视频源"
        case .dataSources: return "刮削设置"
        case .pan: return "网盘账号"
        case .panInterception: return "内置网盘接管"
        case .preferences: return "智能播放设置"
        case .player: return "播放器设置"
        }
    }
    var needsPlayback: Bool {
        switch self {
        case .preferences: return true
        default: return false
        }
    }
}

@MainActor
final class FilmPanInterceptionViewController: FilmSettingsListController {
    private let service: FilmSourceService
    private var saved: [PanProviderID: Bool]?
    private var busy = false

    init(service: FilmSourceService, themeProvider: any PageThemeProviding) {
        self.service = service
        super.init(themeProvider: themeProvider)
        pageTitle = "内置网盘接管"
    }

    override func viewDidLoad() {
        super.viewDidLoad()
        table.accessibilityIdentifier = "film.panInterception"
        update()
    }

    override func pageDidBecomeActive() { load() }

    private func load() {
        guard !busy else { return }
        perform { [service] in try await service.readPanInterception() }
    }

    private func update() {
        sections = [.init(title: "", rows: PanProviderID.allCases.map { provider in
            filmSwitchRow(provider.title, id: "film.panInterception.\(provider.rawValue)",
                          isOn: saved?[provider] ?? true, enabled: saved != nil && !busy) { [weak self] enabled in
                guard let self, !busy else { return }
                perform { [service] in try await service.savePanInterception(provider, enabled: enabled) }
            }
        })]
        render()
    }

    private func perform(_ operation: @escaping @Sendable () async throws -> [PanProviderID: Bool]) {
        guard isPageActive, !busy else { return }
        busy = true; update()
        Task { [weak self] in
            guard let self else { return }
            defer { busy = false; update() }
            do { saved = try await operation() }
            catch { failure(error, retry: { [weak self] in self?.load() }) }
        }
    }
}

@MainActor
final class FilmSmartMatchingViewController: FilmSettingsListController {
    private let model: FilmSettingsModel
    init(model: FilmSettingsModel, themeProvider: any PageThemeProviding) {
        self.model = model
        super.init(themeProvider: themeProvider)
        pageTitle = "智能匹配设置"
    }
    override func viewDidLoad() {
        super.viewDidLoad()
        let groups: [(FilmRuleGroup, String)] = [(.episodeClean, "剧集列表净化规则"),
            (.episode, "集数匹配规则"), (.movie, "电影匹配规则")]
        var rows = groups.map { group, title in
            FilmSettingsRow(title: title, id: "film.matching.\(group.rawValue)", disclosure: true,
                action: { [weak self] in
                    guard let self else { return }
                    navigationController?.pushViewController(FilmRulePageViewController(
                        owner: model.rules, group: group, themeProvider: themeProvider), animated: true)
                })
        }
        rows.append(.init(title: "站点净化设置", id: "film.matching.siteClean", enabled: model.playback != nil,
            disclosure: true, action: { [weak self] in
                guard let self, let playback = model.playback else { return }
                navigationController?.pushViewController(FilmPlaybackPreferencesController(
                    owner: playback, area: .siteClean, themeProvider: themeProvider), animated: true)
            }))
        sections = [.init(title: "", rows: rows)]
        render()
    }
}
@MainActor
final class FilmSmartPlaybackSettingsController: FilmSettingsListController {
    private let owner: FilmPlaybackSettingsModel
    init(owner: FilmPlaybackSettingsModel, themeProvider: any PageThemeProviding) {
        self.owner = owner
        super.init(themeProvider: themeProvider)
        pageTitle = "智能播放设置"
    }
    override func viewDidLoad() {
        super.viewDidLoad()
        sections = [.init(title: "", rows: [
            .init(title: "偏好设置", id: "film.smartPlayback.preferences", disclosure: true, action: { [weak self] in
                guard let self else { return }
                navigationController?.pushViewController(FilmPlaybackPreferencesController(owner: owner, area: .preferences,
                    themeProvider: themeProvider), animated: true)
            }),
            .init(title: "自动准备", id: "film.smartPlayback.autoPrepare", disclosure: true, action: { [weak self] in
                guard let self else { return }
                navigationController?.pushViewController(FilmPlaybackPreferencesController(owner: owner, area: .autoPrepare,
                    themeProvider: themeProvider), animated: true)
            }),
            .init(title: "Manual 匹配源管理", id: "film.smartPlayback.manual", disclosure: true, action: { [weak self] in
                guard let self else { return }
                navigationController?.pushViewController(FilmPlaybackLibraryController(owner: owner, area: .manual,
                    themeProvider: themeProvider), animated: true)
            }),
            .init(title: "匹配禁用管理", id: "film.smartPlayback.blocks", disclosure: true, action: { [weak self] in
                guard let self else { return }
                navigationController?.pushViewController(FilmPlaybackLibraryController(owner: owner, area: .blocks,
                    themeProvider: themeProvider), animated: true)
            }),
        ])]
        render()
    }
}
#endif
