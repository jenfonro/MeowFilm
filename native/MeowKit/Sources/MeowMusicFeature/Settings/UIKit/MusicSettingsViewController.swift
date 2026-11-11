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
    private let sourceSettings: MusicHomeSourceSettingsModel

    public init(
        model: MusicViewModel,
        appearance: MusicAppearancePreferencesStore,
        router: any MusicUIRouting
    ) {
        self.model = model
        self.appearance = appearance
        self.router = router
        sourceSettings = MusicHomeSourceSettingsModel(model: model)
        super.init(
            themeProvider: appearance,
            content: MusicSettingsContent(theme: PageTheme(colors: appearance.pagePalette))
        )
        pageTitle = "音乐设置"
        sourceSettings.onError = { [weak self] in self?.showError($0) }
    }

    public override func viewDidLoad() {
        super.viewDidLoad()
        // Delivered on the next main-loop turn, after the store has committed the value.
        observe(appearance.$preferences.map { _ -> Void in }.eraseToAnyPublisher()) { [weak self] in
            self?.render()
        }
        observe(sourceSettings) { [weak self] in self?.render() }
    }

    public override func pageDidBecomeVisible() {
        super.pageDidBecomeVisible()
        sourceSettings.activate()
    }

    public override func pageDidBecomeHidden() {
        super.pageDidBecomeHidden()
        sourceSettings.deactivate()
    }

    public override func pageDidBecomeInactive() {
        super.pageDidBecomeInactive()
        sourceSettings.deactivate()
    }

    public override func applyTheme() {
        super.applyTheme()
        guard isViewLoaded, renderedTheme != theme else { return }
        renderedTheme = theme
        render()
    }

    private func render() {
        guard isViewLoaded else { return }
        setContent(MusicSettingsContent(theme: theme, lockLyric: appearance.preferences.lockLyric,
            catalogSource: sourceSettings.selection, savingCatalogSource: sourceSettings.isSaving,
            open: { [weak self] in self?.open($0) },
            setLockLyric: { [weak self] value in
                guard let self else { return }
                if !appearance.set(\.lockLyric, value) {
                    render()
                    if let message = appearance.persistenceError { showError(message) }
                }
            },
            selectCatalogSource: { [weak self] in self?.sourceSettings.select($0) }))
    }

    private func open(_ destination: MusicSettingsDestination) {
        let controller: UIViewController
        switch destination {
        case .aggregation:
            controller = MusicAccountsViewController(model: model, appearance: appearance, router: router)
        case .sources:
            controller = MusicSourceManagementViewController(model: model, appearance: appearance)
        case .exclusions:
            controller = MusicSourceExclusionsViewController(model: model, appearance: appearance)
        case .search:
            controller = MusicSearchSettingsViewController(appearance: appearance)
        case .playback:
            controller = MusicPlaybackSettingsViewController(model: model, appearance: appearance)
        case .metadata:
            controller = MusicMetadataSettingsViewController(model: model, appearance: appearance)
        }
        navigationController?.pushViewController(controller, animated: true)
    }
}
#endif
