#if os(iOS)
import UIKit
import MeowUI

@MainActor
public final class FilmMetadataCacheSettingsViewController: PageViewController {
    private let owner: FilmPlaybackSettingsModel
    public init(owner: FilmPlaybackSettingsModel, themeProvider: any PageThemeProviding) {
        self.owner = owner
        super.init(themeProvider: themeProvider)
        pageTitle = "缓存管理"
    }
    public override func viewDidLoad() {
        super.viewDidLoad()
        let form = FilmMetadataCacheFormController(owner: owner, themeProvider: themeProvider)
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
final class FilmMetadataCacheFormController: FilmSettingsListController {
    private let owner: FilmPlaybackSettingsModel
    override var usesPageHeader: Bool { false }
    init(owner: FilmPlaybackSettingsModel, themeProvider: any PageThemeProviding) {
        self.owner = owner
        super.init(themeProvider: themeProvider)
    }
    override func viewDidLoad() {
        super.viewDidLoad()
        observe(owner) { [weak self] in self?.render() }
        render()
    }
    override func render() {
        sections = [.init(title: "影视数据缓存", rows: FilmMetadataCacheScope.allCases.map { scope in
            .init(title: scope.title, id: "settings.cache." + scope.rawValue, enabled: !owner.busy,
                action: { [weak self] in self?.clear(scope) })
        })]
        super.render()
    }
    private func clear(_ scope: FilmMetadataCacheScope) {
        guard !owner.busy else { return }
        Task { [self] in
            do { try await owner.clearMetadataCache(scope); notice("缓存已清空") }
            catch { failure(error, retry: { [weak self] in self?.clear(scope) }) }
        }
    }
}
#endif
