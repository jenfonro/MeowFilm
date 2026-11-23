#if os(iOS)
import UIKit
import Combine
import MeowUI

@MainActor
public final class MusicSettingsViewController: SettingsListViewController {
    let model: MusicViewModel
    let appearance: MusicAppearancePreferencesStore
    weak var router: (any MusicUIRouting)?
    let sourceSettings: MusicHomeSourceSettingsModel
    private var destinationIcons: [MusicSettingsDestination: MusicSettingsSymbolView] = [:]
    private lazy var catalogSourceIcon = MusicSettingsSymbolView(symbol: "music.note.house", theme: theme)

    public init(
        model: MusicViewModel,
        appearance: MusicAppearancePreferencesStore,
        router: any MusicUIRouting
    ) {
        self.model = model
        self.appearance = appearance
        self.router = router
        sourceSettings = MusicHomeSourceSettingsModel(model: model)
        super.init(themeProvider: appearance)
        pageTitle = "音乐设置"
        sourceSettings.onError = { [weak self] in self?.enqueueError($0) }
    }

    public override func viewDidLoad() {
        super.viewDidLoad()
        table.accessibilityIdentifier = "music.settings.list"
        observeDistinct(appearance.$preferences.map(\.lockLyric).dropFirst().eraseToAnyPublisher(), immediateOnMain: false, whenVisible: true) { [weak self] _ in
            self?.render()
        }
        observe(sourceSettings, whenVisible: true) { [weak self] in self?.render() }
        render()
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

    private func render() {
        guard isViewLoaded else { return }
        var mainRows: [SettingsListRow] = MusicSettingsDestination.allCases.map { destination in
            let icon = destinationIcons[destination] ?? {
                let view = MusicSettingsSymbolView(symbol: destination.symbol, theme: theme)
                destinationIcons[destination] = view
                return view
            }()
            return SettingsListRow(
                id: destination.accessibilityIdentifier,
                title: destination.title,
                leadingView: icon,
                accessory: .disclosure,
                action: { [weak self] in self?.open(destination) }
            )
        }

        if let catalogSource = sourceSettings.selection, !catalogSource.available.isEmpty {
            let menu = UIMenu(options: .singleSelection, children: catalogSource.available.map { platform in
                UIAction(
                    title: platform.title,
                    state: platform == catalogSource.selected ? .on : .off
                ) { [weak self] _ in
                    self?.sourceSettings.select(platform)
                }
            })
            mainRows.append(SettingsListRow(
                id: "music.settings.catalogSource",
                title: "数据源",
                leadingView: catalogSourceIcon,
                accessory: .menu(title: catalogSource.selected.title, menu: menu, enabled: !sourceSettings.isSaving),
                enabled: !sourceSettings.isSaving
            ))
        }

        let lyricRow = SettingsListRow(
            id: "music.settings.lockLyric",
            title: "锁屏歌词",
            accessory: .toggle(isOn: appearance.preferences.lockLyric, enabled: true) { [weak self] value in
                guard let self else { return }
                let appearance = self.appearance
                self.runPageTask(key: "lockLyric") { [weak self] in
                    if !(await appearance.set(\.lockLyric, value)) {
                        self?.render()
                        if let message = appearance.persistenceError { self?.enqueueError(message) }
                    }
                }
            }
        )

        applySections([
            SettingsListSection(id: "main", rows: mainRows),
            SettingsListSection(id: "lyric", title: "歌词", rows: [lyricRow]),
        ])
    }

    func open(_ destination: MusicSettingsDestination, animated: Bool = true) {
        let controller: UIViewController
        switch destination {
        case .aggregation:
            controller = MusicAccountsViewController(model: model, appearance: appearance, router: router)
        case .sources:
            controller = MusicSourceManagementViewController(model: model, appearance: appearance)
        case .exclusions:
            controller = MusicSourceExclusionsViewController(model: model, appearance: appearance)
        case .search:
            controller = MusicSearchSettingsViewController(appearance: appearance, store: model.searchPreferences)
        case .playback:
            controller = MusicPlaybackSettingsViewController(model: model, appearance: appearance)
        case .metadata:
            controller = MusicMetadataSettingsViewController(model: model, appearance: appearance)
        }
        navigationController?.pushViewController(controller, animated: animated)
    }
}
#endif
