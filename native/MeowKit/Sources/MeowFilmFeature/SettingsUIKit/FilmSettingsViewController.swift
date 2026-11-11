#if os(iOS)
import UIKit
import MeowUI

@MainActor
public final class FilmSettingsViewController: PageViewController {
    private let model: FilmSettingsModel
    private var list: FilmSettingsMenuController?

    public init(model: FilmSettingsModel, themeProvider: any PageThemeProviding) {
        self.model = model
        super.init(themeProvider: themeProvider)
        pageTitle = "影视设置"
    }

    public override func viewDidLoad() {
        super.viewDidLoad()
        let list = FilmSettingsMenuController(playbackAvailable: model.playback != nil, themeProvider: themeProvider)
        self.list = list
        list.open = { [weak self] destination in
            guard let self else { return }
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
            case .preferences:
                guard let playback = model.playback else { showError("播放设置暂不可用"); return }
                page = FilmSmartPlaybackSettingsController(owner: playback, themeProvider: themeProvider)
            }
            navigationController?.pushViewController(page, animated: true)
        }
        addChild(list); view.addSubview(list.view)
        list.view.translatesAutoresizingMaskIntoConstraints = false
        NSLayoutConstraint.activate([
            list.view.topAnchor.constraint(equalTo: contentTopAnchor),
            list.view.leadingAnchor.constraint(equalTo: view.leadingAnchor),
            list.view.trailingAnchor.constraint(equalTo: view.trailingAnchor),
            list.view.bottomAnchor.constraint(equalTo: view.bottomAnchor),
        ])
        list.didMove(toParent: self)
        observe(model) { [weak self] in self?.showLoadError() }
    }
    private var displayedLoadError: String?
    public override func pageDidFinishAppearing() { showLoadError() }
    private func showLoadError() {
        guard let error = model.loadError, error != displayedLoadError, canPresentPageAlert else { return }
        displayedLoadError = error
        let alert = UIAlertController(title: "错误", message: error, preferredStyle: .alert)
        alert.addAction(UIAlertAction(title: "取消", style: .cancel))
        alert.addAction(UIAlertAction(title: "重试", style: .default) { [weak self] _ in
            self?.displayedLoadError = nil; self?.model.start()
        })
        present(alert, animated: true)
    }
}

private enum FilmSettingsDestination: String, CaseIterable {
    case search, matching, cats, videos, dataSources, preferences
    var title: String {
        switch self {
        case .search: return "搜索净化设置"
        case .matching: return "智能匹配设置"
        case .cats: return "猫源"
        case .videos: return "视频源"
        case .dataSources: return "数据源"
        case .preferences: return "智能播放设置"
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
private final class FilmSettingsMenuController: FilmSettingsListController {
    var open: ((FilmSettingsDestination) -> Void)?
    private let playbackAvailable: Bool
    init(playbackAvailable: Bool, themeProvider: any PageThemeProviding) {
        self.playbackAvailable = playbackAvailable
        super.init(themeProvider: themeProvider)
    }
    override var usesPageHeader: Bool { false }
    override func viewDidLoad() {
        super.viewDidLoad()
        sections = [.init(title: "", rows: FilmSettingsDestination.allCases.map { destination in
            .init(title: destination.title, id: "film.settings.\(destination.rawValue)",
                  enabled: !destination.needsPlayback || playbackAvailable, disclosure: true,
                  action: { [weak self] in self?.open?(destination) })
        })]
        render()
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
