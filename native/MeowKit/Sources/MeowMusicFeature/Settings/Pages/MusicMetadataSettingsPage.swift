#if os(iOS)
import UIKit
import MeowUI

@MainActor
final class MusicMetadataSettingsViewController: SettingsListViewController {
    private let owner: MusicViewModel
    private let state: MusicMetadataSettingsModel?
    private var started = false

    override var allowsRowReordering: Bool { true }

    init(model: MusicViewModel, appearance: MusicAppearancePreferencesStore) {
        owner = model
        state = model.metadataPreferencesService.map { service in
            MusicMetadataSettingsModel(service: service, receipts: model.settingsReceipts) { [weak model] _ in
                model?.scheduleMetadataPreferencesRefresh()
            }
        }
        super.init(themeProvider: appearance)
        pageTitle = "歌词／封面设置"
    }

    override func viewDidLoad() {
        super.viewDidLoad()
        observe(owner.accessChanges) { [weak self] in
            guard let self, owner.isClosed else { return }
            clearErrorSource("metadata")
            state?.setActive(false)
            render()
        }
        if let state {
            observe(state, whenVisible: true) { [weak self] in
                self?.render()
                self?.showFailure()
            }
        }
        render()
    }

    override func pageDidBecomeActive() {
        started = true
        let open = !owner.isClosed
        state?.setActive(open)
        if open { state?.reload() }
        render()
    }

    override func pageDidBecomeVisible() {
        super.pageDidBecomeVisible()
        showFailure()
    }

    override func pageDidBecomeInactive() {
        started = false
        state?.setActive(false)
    }

    private func render() {
        guard isViewLoaded else { return }
        guard let state, let platforms = state.value?.platforms, !platforms.isEmpty else {
            applySections([])
            return
        }
        let rows = platforms.map { platform in
            SettingsListRow(
                id: "music.metadata.platform.\(platform.rawValue)",
                title: platform.title,
                leadingIcon: .asset(platform.settingsLogoName),
                accessory: .toggle(
                    isOn: state.value?.enabledPlatforms.contains(platform) == true,
                    enabled: !state.busy,
                    onChange: { [weak state] value in state?.setEnabled(platform, value) }
                ),
                enabled: !state.busy,
                canMove: !state.busy
            )
        }
        applySections([SettingsListSection(id: "platforms", title: "平台优先级", rows: rows)])
    }

    override func didMoveRow(from sourceIndexPath: IndexPath, to destinationIndexPath: IndexPath) {
        guard let state, var platforms = state.value?.platforms,
              platforms.indices.contains(sourceIndexPath.row),
              platforms.indices.contains(destinationIndexPath.row) else { return }
        let moved = platforms.remove(at: sourceIndexPath.row)
        platforms.insert(moved, at: destinationIndexPath.row)
        state.reorder(platforms)
    }

    private func showFailure() {
        syncErrorSource("metadata", message: state?.error, retry: { [weak self] in
            guard let self, self.started, !self.owner.isClosed else { return }
            self.state?.reload()
        })
    }
}
#endif
