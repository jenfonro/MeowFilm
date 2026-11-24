#if os(iOS)
import UIKit
import Combine
import MeowUI

@MainActor
public final class FilmMetadataCacheSettingsViewController: SettingsListViewController {
    let owner: FilmPlaybackSettingsModel

    public override var avoidsKeyboard: Bool { false }

    public init(owner: FilmPlaybackSettingsModel, themeProvider: any PageThemeProviding) {
        self.owner = owner
        super.init(themeProvider: themeProvider)
        pageTitle = "缓存管理"
    }

    public override func viewDidLoad() {
        super.viewDidLoad()
        observeDistinct(owner.$busy.dropFirst().eraseToAnyPublisher(), immediateOnMain: false, whenVisible: true) { [weak self] _ in
            self?.render()
        }
        render()
    }

    private func render() {
        let rows = FilmMetadataCacheScope.allCases.map { scope in
            SettingsListRow(
                id: "settings.cache." + scope.rawValue,
                title: scope.title,
                enabled: !owner.busy,
                action: { [weak self] in self?.clear(scope) }
            )
        }
        applySections([SettingsListSection(id: "film.cache.metadata", title: "影视数据缓存", rows: rows)])
    }

    private func clear(_ scope: FilmMetadataCacheScope) {
        guard !owner.busy, !hasActivePageTask(key: "clear") else { return }
        let owner = self.owner
        runPageTask(key: "clear", retry: { [weak self] in self?.clear(scope) }) { [weak self] in
            try await owner.clearMetadataCache(scope)
            self?.enqueueNotice(title: "缓存已清空")
        }
    }
}
#endif
