#if os(iOS)
import UIKit
import MeowUI

@MainActor
public final class MusicSettingsViewController: HostingPageViewController<MusicSettingsContent> {
    private let model: MusicViewModel
    private let appearance: MusicAppearancePreferencesStore
    private weak var router: (any MusicUIRouting)?
    private var renderedTheme: PageTheme?

    public init(
        model: MusicViewModel,
        appearance: MusicAppearancePreferencesStore,
        router: any MusicUIRouting
    ) {
        self.model = model
        self.appearance = appearance
        self.router = router
        super.init(
            themeProvider: appearance,
            content: MusicSettingsContent(theme: PageTheme(palette: appearance.pageColorway))
        )
        title = "设置"
    }

    public override func applyTheme() {
        super.applyTheme()
        guard isViewLoaded, renderedTheme != theme else { return }
        renderedTheme = theme
        setContent(MusicSettingsContent(theme: theme, open: { [weak self] in
            self?.open($0)
        }))
    }

    private func open(_ destination: MusicSettingsDestination) {
        let controller: UIViewController
        switch destination {
        case .aggregation:
            controller = MusicAccountsViewController(model: model, appearance: appearance, router: router)
        case .search:
            controller = MusicSearchSettingsViewController(appearance: appearance)
        case .playback:
            controller = MusicPlaybackSettingsViewController(model: model, appearance: appearance)
        case .appearance:
            controller = MusicAppearanceSettingsViewController(store: appearance)
        case .metadata:
            controller = MusicMetadataSettingsViewController(model: model, appearance: appearance)
        }
        navigationController?.pushViewController(controller, animated: true)
    }
}
#endif
