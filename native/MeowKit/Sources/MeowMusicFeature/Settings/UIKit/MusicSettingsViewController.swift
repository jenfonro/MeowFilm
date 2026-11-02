#if os(iOS)
import UIKit
import Combine
import MeowUI

@MainActor
public final class MusicSettingsViewController: HostingPageViewController<MusicSettingsContent> {
    private let model: MusicViewModel
    private let appearance: MusicAppearancePreferencesStore
    private weak var router: (any MusicUIRouting)?
    private var renderedTheme: PageTheme?
    private var renderedPreference: String?

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
        pageTitle = "设置"
    }

    public override func viewDidLoad() {
        super.viewDidLoad()
        // Delivered on the next main-loop turn, after the store has committed the value.
        observe(appearance.$preferences.map { _ -> Void in }.eraseToAnyPublisher()) { [weak self] in
            guard let self, renderedPreference != preferenceValue else { return }
            render()
        }
    }

    public override func applyTheme() {
        super.applyTheme()
        guard isViewLoaded, renderedTheme != theme else { return }
        renderedTheme = theme
        render()
    }

    private func render() {
        guard isViewLoaded else { return }
        let value = preferenceValue
        renderedPreference = value
        setContent(MusicSettingsContent(theme: theme, preferenceValue: value, open: { [weak self] in
            self?.open($0)
        }))
    }

    private var preferenceValue: String {
        let preferences = appearance.preferences
        return preferences.colorMode == .fixed ? preferences.colorPalette.title : preferences.colorMode.title
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
